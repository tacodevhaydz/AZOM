using System;
using System.Diagnostics;
using System.Threading;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Background worker that renders the three host-rendered wheelbase
    /// low-frequency effects (LFE) — complex gearshift vibration, continuous
    /// engine vibration, and ABS — as cmd 0x2D/0x77 streams on the base primary
    /// pipe. Available only on base firmware >= 1.2.10.10 (gated live each tick
    /// via <see cref="MozaData.BaseSupportsLfe"/>). See MozaBaseLfeProtocol and
    /// docs/protocol/devices/wheelbase-0x13.md.
    ///
    /// Mirrors <see cref="Ab9EngineVibrationWorker"/> (dedicated background
    /// thread, Stopwatch-scheduled fixed tick, telemetry published via
    /// <see cref="PostFrame"/>) and reuses the mBooster encoders + ABS
    /// synthesizer. All three effects follow the "emit while active, one disable
    /// frame on the active->idle edge, nothing while idle" rule.
    ///
    /// Effect behaviour (capture-verified):
    /// - Engine: continuous while driving; frequency = slider (redline pitch)
    ///   scaled by rpm/redline, intensity = slider. Same model as the AB9 worker.
    /// - ABS: streamed while ABS is active; frequency fixed at the slider,
    ///   intensity = slider * host pulse (SynthesizeAbs).
    /// - Gearshift: a short burst on each gear change (posted from
    ///   MozaPlugin.CheckGearshiftEvent); fixed placeholder period, freq+intensity
    ///   from the sliders.
    /// </summary>
    internal sealed class BaseLfeEffectWorker : IDisposable
    {
        private const int TickPeriodMs = 20;                    // 50 Hz — matches MBoosterEffectWorker
        private const double TickPeriodSec = TickPeriodMs / 1000.0;
        private const double DefaultRedlineRpm = 8000.0;        // fallback when MaxRpm is absent
        private const double EngineRpmThreshold = 100.0;        // engine silent below this rpm
        private const double MaxFreqHz = 200.0;                 // encoder full-scale
        private const long GearshiftBurstMs = 120;              // per-shift burst hold
        // Momentary test-button patterns.
        private const long EngineTestMs = 2000;                 // engine test = 2 s frequency sweep
        private const long AbsTestMs = 1000;                    // abs test = 1 s pulse burst
        private const double GsTestBump1EndMs = 70;             // gearshift test = two rapid bumps
        private const double GsTestBump2StartMs = 180;
        private const double GsTestBump2EndMs = 250;
        // If no telemetry frame arrives for this long, treat the feed as paused/
        // stopped (SimHub stops calling DataUpdate on game exit / feed pause, so
        // the last game state would otherwise freeze the effect streaming).
        private const long FeedStaleMs = 250;

        // Active profile carries no LFE block → factory defaults (all disabled),
        // so the effects follow per-game profile switches instead of freezing.
        private static readonly BaseLfeSettings DefaultLfe = new BaseLfeSettings();

        private readonly MozaDeviceManager _base;               // base == primary pipe
        private readonly DeviceDetectionState _detectionState;
        private readonly MozaData _data;
        private readonly Func<BaseLfeSettings?> _lookup;
        private readonly Func<bool> _isShuttingDown;

        private Thread? _thread;
        private volatile bool _stop;

        // Telemetry published from the SimHub data thread.
        private long _latestRpmBits;
        private long _latestMaxRpmBits;
        private volatile bool _latestGameRunning;
        private volatile bool _latestAbsActive;
        private long _lastFrameTicks;   // Stopwatch stamp of the last PostFrame

        // Momentary test triggers (UI button). Each is a Stopwatch-tick deadline /
        // start-stamp; the effect renders a fixed pattern then stops.
        private long _engineTestUntil;   // 2 s sweep window
        private long _engineTestStart;
        private long _absTestUntil;      // 1 s burst window
        private long _gsTestStart;       // 0 = no test sequence; else two-bump start stamp

        // Gearshift burst deadline in Stopwatch ticks (0 = no burst). Written from
        // the data thread (PostGearshiftEvent) and read from the worker thread.
        private long _gearshiftBurstUntil;

        // Per-effect active state + ABS oscillator phase (worker thread only).
        private bool _engineActive;
        private bool _absActive;
        private bool _gearshiftActive;
        private double _absPhaseRad;

        private long _burstTicks;
        private long _feedStaleTicks;

        public BaseLfeEffectWorker(
            MozaDeviceManager baseManager,
            DeviceDetectionState detectionState,
            MozaData data,
            Func<BaseLfeSettings?> lookup,
            Func<bool> isShuttingDown)
        {
            _base = baseManager;
            _detectionState = detectionState;
            _data = data;
            _lookup = lookup;
            _isShuttingDown = isShuttingDown;
        }

        public void Start()
        {
            _stop = false;
            _burstTicks = Stopwatch.Frequency * GearshiftBurstMs / 1000;
            _feedStaleTicks = Stopwatch.Frequency * FeedStaleMs / 1000;
            _thread = new Thread(Loop)
            {
                Name = "MozaBaseLfe",
                IsBackground = true,
            };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            try { _thread?.Join(1000); } catch { }
            _thread = null;
        }

        public void Dispose() => Stop();

        /// <summary>Publish latest game state from the SimHub data-update thread.</summary>
        public void PostFrame(double rpm, double maxRpm, bool gameRunning, bool absActive)
        {
            Interlocked.Exchange(ref _latestRpmBits, BitConverter.DoubleToInt64Bits(rpm));
            Interlocked.Exchange(ref _latestMaxRpmBits, BitConverter.DoubleToInt64Bits(maxRpm));
            _latestGameRunning = gameRunning;
            _latestAbsActive = absActive;
            Interlocked.Exchange(ref _lastFrameTicks, Stopwatch.GetTimestamp());
        }

        /// <summary>Trigger a complex-gearshift burst (from CheckGearshiftEvent).</summary>
        public void PostGearshiftEvent()
        {
            long until = Stopwatch.GetTimestamp() + _burstTicks;
            Interlocked.Exchange(ref _gearshiftBurstUntil, until);
        }

        /// <summary>Trigger the engine test: a 2 s frequency sweep (idle → redline pitch).</summary>
        public void PostEngineTest()
        {
            long now = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _engineTestStart, now);
            Interlocked.Exchange(ref _engineTestUntil, now + Stopwatch.Frequency * EngineTestMs / 1000);
        }

        /// <summary>Trigger the ABS test: a 1 s pulse burst.</summary>
        public void PostAbsTest()
            => Interlocked.Exchange(ref _absTestUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency * AbsTestMs / 1000);

        /// <summary>Trigger the gearshift test: two rapid bumps.</summary>
        public void PostGearshiftTest()
            => Interlocked.Exchange(ref _gsTestStart, Stopwatch.GetTimestamp());

        private void Loop()
        {
            long stopwatchFreq = Stopwatch.Frequency;
            long periodTicks = stopwatchFreq * TickPeriodMs / 1000;
            long next = Stopwatch.GetTimestamp() + periodTicks;
            while (!_stop)
            {
                try { Tick(); }
                catch (Exception ex) { MozaLog.Debug($"[AZOM/LFE] tick: {ex.Message}"); }

                long now = Stopwatch.GetTimestamp();
                long delta = next - now;
                if (delta <= 0)
                {
                    next = now + periodTicks;
                    continue;
                }
                int sleepMs = (int)Math.Min(50, Math.Max(1, delta * 1000 / stopwatchFreq));
                Thread.Sleep(sleepMs);
                next += periodTicks;
            }
        }

        private void Tick()
        {
            if (_isShuttingDown()) return;
            if (_base == null || !_base.IsConnected || !_detectionState.BaseDetected) return;
            // Firmware gate — read live so a hot-attach / reconnect re-evaluates.
            if (!_data.BaseSupportsLfe)
            {
                // If firmware regressed (hot-swap to an older base), silence any
                // effect that was mid-stream.
                SilenceIfActive();
                return;
            }

            // Feed paused/stopped: SimHub stops calling DataUpdate on game exit or
            // feed pause, so the last game state would otherwise freeze the stream.
            // Staleness zeroes the GAME-driven activation; test triggers (their own
            // deadlines) still run so the buttons work with no game.
            bool feedStale = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastFrameTicks)) > _feedStaleTicks;
            bool gameActive = _latestGameRunning && !feedStale;
            bool absActive = _latestAbsActive && !feedStale;

            var lfe = _lookup() ?? DefaultLfe;
            TickEngine(lfe, gameActive);
            TickAbs(lfe, absActive);
            TickGearshift(lfe);
        }

        private void TickEngine(BaseLfeSettings lfe, bool gameActive)
        {
            var s = lfe.Engine ?? DefaultLfe.Engine;
            double sliderFreq = Clamp(s.FrequencyHz, 0, MaxFreqHz);
            int intensity = ClampPct(s.IntensityPct);
            double rpm = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _latestRpmBits));
            double maxRpm = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _latestMaxRpmBits));

            long now = Stopwatch.GetTimestamp();
            bool testActive = Interlocked.Read(ref _engineTestUntil) > now;
            bool byGame = gameActive && s.Enabled && rpm > EngineRpmThreshold;
            bool want = (byGame || testActive) && intensity > 0 && sliderFreq > 0;
            if (!want)
            {
                if (_engineActive) { _base.SendBaseLfeDisable(MozaBaseLfeProtocol.LfeEffect.Engine); _engineActive = false; }
                return;
            }

            // Slider = redline frequency; audible pitch scales with rpm fraction.
            // The test button sweeps the pitch from idle to redline over 2 s so the
            // user hears the full RPM-tracking range without a game running.
            double fraction;
            if (testActive)
            {
                double elapsedMs = (now - Interlocked.Read(ref _engineTestStart)) * 1000.0 / Stopwatch.Frequency;
                double t = elapsedMs / EngineTestMs;
                if (t < 0) t = 0; if (t > 1) t = 1;
                fraction = 0.12 + 0.88 * t;   // audible low → redline
            }
            else
            {
                double redline = maxRpm > EngineRpmThreshold ? maxRpm : DefaultRedlineRpm;
                fraction = rpm / redline;
                if (fraction > 1.0) fraction = 1.0;
                if (fraction < 0.0) fraction = 0.0;
            }
            double actualHz = sliderFreq * fraction;
            if (actualHz < 1.0) actualHz = 1.0;   // keep the frame well-formed at very low rpm

            _base.SendBaseLfeEngineStream(playing: true, actualHz, intensity);
            _engineActive = true;
        }

        private void TickAbs(BaseLfeSettings lfe, bool absActive)
        {
            var s = lfe.Abs ?? DefaultLfe.Abs;
            double freq = Clamp(s.FrequencyHz, 0, MaxFreqHz);
            int intensityPct = ClampPct(s.IntensityPct);
            double smoothness01 = ClampPct(s.SmoothnessPct) / 100.0;

            bool testActive = Interlocked.Read(ref _absTestUntil) > Stopwatch.GetTimestamp();
            bool want = ((absActive && s.Enabled) || testActive) && intensityPct > 0 && freq > 0;
            if (!want)
            {
                if (_absActive)
                {
                    _base.SendBaseLfeDisable(MozaBaseLfeProtocol.LfeEffect.Abs);
                    _absActive = false;
                    _absPhaseRad = 0;
                }
                return;
            }

            if (!_absActive) { _absActive = true; _absPhaseRad = 0; }
            _absPhaseRad += 2.0 * Math.PI * freq * TickPeriodSec;
            if (_absPhaseRad >= 2.0 * Math.PI)
                _absPhaseRad -= 2.0 * Math.PI * Math.Floor(_absPhaseRad / (2.0 * Math.PI));

            // intensity envelope = slider ceiling * host pulse waveform (0..1).
            double amp01 = MBoosterEffectSynthesizer.SynthesizeAbs(intensityPct / 100.0, _absPhaseRad, smoothness01);
            _base.SendBaseLfeAbsStream(playing: true, freq, amp01);
        }

        private void TickGearshift(BaseLfeSettings lfe)
        {
            var s = lfe.Gearshift ?? DefaultLfe.Gearshift;
            double freq = Clamp(s.FrequencyHz, 0, MaxFreqHz);
            int intensityPct = ClampPct(s.IntensityPct);

            long now = Stopwatch.GetTimestamp();
            bool realBurst = Interlocked.Read(ref _gearshiftBurstUntil) > now;
            // Test = two rapid bumps: bump1 [0,70) ms, gap, bump2 [180,250) ms.
            bool testBump = false;
            long ts = Interlocked.Read(ref _gsTestStart);
            if (ts > 0)
            {
                double ms = (now - ts) * 1000.0 / Stopwatch.Frequency;
                if (ms < GsTestBump1EndMs) testBump = true;
                else if (ms >= GsTestBump2StartMs && ms < GsTestBump2EndMs) testBump = true;
                else if (ms >= GsTestBump2EndMs) Interlocked.Exchange(ref _gsTestStart, 0);
            }
            bool want = (realBurst || testBump) && intensityPct > 0 && freq > 0;
            if (!want)
            {
                if (_gearshiftActive) { _base.SendBaseLfeDisable(MozaBaseLfeProtocol.LfeEffect.Gearshift); _gearshiftActive = false; }
                return;
            }

            _base.SendBaseLfeGearshiftBurst(freq, intensityPct);
            _gearshiftActive = true;
        }

        private void SilenceIfActive()
        {
            if (_engineActive) { _base.SendBaseLfeDisable(MozaBaseLfeProtocol.LfeEffect.Engine); _engineActive = false; }
            if (_absActive) { _base.SendBaseLfeDisable(MozaBaseLfeProtocol.LfeEffect.Abs); _absActive = false; _absPhaseRad = 0; }
            if (_gearshiftActive) { _base.SendBaseLfeDisable(MozaBaseLfeProtocol.LfeEffect.Gearshift); _gearshiftActive = false; }
        }

        private static double Clamp(double v, double lo, double hi)
            => v < lo ? lo : (v > hi ? hi : v);

        private static int ClampPct(int v) => v < 0 ? 0 : (v > 100 ? 100 : v);
    }
}
