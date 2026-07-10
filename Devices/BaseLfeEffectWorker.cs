using System;
using System.Diagnostics;
using System.Threading;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Renders the three host-rendered wheelbase LFE channels (Engine / ABS /
    /// Gearshift) as cmd 0x2D/0x77 streams. Each channel's four parameters —
    /// trigger, frequency, intensity, smoothness — are dual-mode: a static slider
    /// value, or an NCalc/property formula (evaluated live per tick via the
    /// injected evaluator) that overrides it. The trigger gates the channel:
    /// non-zero = active for the level channels (Engine/ABS), or a change fires a
    /// burst for the edge channel (Gearshift). Available only on base firmware
    /// >= 1.2.10.10 (MozaData.BaseSupportsLfe). See docs/protocol/devices/wheelbase-0x13.md.
    ///
    /// Parameters are not waveforms — the firmware oscillates at the streamed
    /// frequency; the host sets freq + an amplitude ENVELOPE (intensity shaped by
    /// smoothness: 100 = steady, 0 = full-swing pulse). Frequency is sent
    /// unclamped (the wire encoder saturates the freq field at 200 Hz).
    /// </summary>
    internal sealed class BaseLfeEffectWorker : IDisposable
    {
        private const int TickPeriodMs = 20;                    // 50 Hz
        private const double TickPeriodSec = TickPeriodMs / 1000.0;
        private const long FeedStaleMs = 250;                  // feed paused/stopped → silence game-driven effects
        private const long GearshiftBurstMs = 120;             // per-shift / per-bump burst hold
        // Momentary test-button patterns.
        private const long EngineTestMs = 2000;                // engine test = 2 s frequency sweep
        private const long AbsTestMs = 1000;                   // abs test = 1 s pulse burst
        private const double GsTestBump1EndMs = 70;            // gearshift test = two rapid bumps
        private const double GsTestBump2StartMs = 180;
        private const double GsTestBump2EndMs = 250;

        // Active profile carries no LFE block → factory defaults (all disabled).
        private static readonly BaseLfeSettings DefaultLfe = new BaseLfeSettings();

        private readonly MozaDeviceManager _base;              // base == primary pipe
        private readonly DeviceDetectionState _detectionState;
        private readonly MozaData _data;
        private readonly Func<BaseLfeSettings?> _lookup;
        private readonly Func<bool> _isShuttingDown;
        private readonly Func<string, double> _evalFormula;    // NCalc/property → double (0 on error)

        private Thread? _thread;
        private volatile bool _stop;

        // Feed liveness (from the SimHub data thread). gameActive = running and
        // not paused/in-menu; _lastFrameTicks detects a stopped feed (game exit).
        private volatile bool _latestGameActive;
        private long _lastFrameTicks;

        // Momentary test triggers.
        private long _engineTestUntil, _engineTestStart, _absTestUntil, _gsTestStart;

        // Per-channel state (worker thread only). Every channel carries both the
        // continuous phase and the OnChange edge state, so any channel can run in
        // either trigger mode (used by the additive-engine partials).
        private struct ChannelState
        {
            public bool Active;
            public double Phase;
            public double LastTrigger;   // OnChange edge detection
            public bool Warm;
            public long BurstUntil;
            public long LastFireTicks;
        }
        private ChannelState _engineSt, _absSt, _gearSt;

        private long _burstTicks, _feedStaleTicks;

        public BaseLfeEffectWorker(
            MozaDeviceManager baseManager,
            DeviceDetectionState detectionState,
            MozaData data,
            Func<BaseLfeSettings?> lookup,
            Func<string, double> evalFormula,
            Func<bool> isShuttingDown)
        {
            _base = baseManager;
            _detectionState = detectionState;
            _data = data;
            _lookup = lookup;
            _evalFormula = evalFormula ?? (_ => 0.0);
            _isShuttingDown = isShuttingDown;
        }

        public void Start()
        {
            _stop = false;
            _burstTicks = Stopwatch.Frequency * GearshiftBurstMs / 1000;
            _feedStaleTicks = Stopwatch.Frequency * FeedStaleMs / 1000;
            _thread = new Thread(Loop) { Name = "MozaBaseLfe", IsBackground = true };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            try { _thread?.Join(1000); } catch { }
            _thread = null;
        }

        public void Dispose() => Stop();

        /// <summary>Feed liveness from the SimHub data thread (game running and not paused/in-menu).</summary>
        public void PostFrame(bool gameActive)
        {
            _latestGameActive = gameActive;
            Interlocked.Exchange(ref _lastFrameTicks, Stopwatch.GetTimestamp());
        }

        /// <summary>Engine test: 2 s frequency sweep (idle → slider pitch).</summary>
        public void PostEngineTest()
        {
            long now = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _engineTestStart, now);
            Interlocked.Exchange(ref _engineTestUntil, now + Stopwatch.Frequency * EngineTestMs / 1000);
        }

        /// <summary>ABS test: 1 s pulse burst.</summary>
        public void PostAbsTest()
            => Interlocked.Exchange(ref _absTestUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency * AbsTestMs / 1000);

        /// <summary>Gearshift test: two rapid bumps.</summary>
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
                if (delta <= 0) { next = now + periodTicks; continue; }
                int sleepMs = (int)Math.Min(50, Math.Max(1, delta * 1000 / stopwatchFreq));
                Thread.Sleep(sleepMs);
                next += periodTicks;
            }
        }

        private void Tick()
        {
            if (_isShuttingDown()) return;
            if (_base == null || !_base.IsConnected || !_detectionState.BaseDetected) return;
            if (!_data.BaseSupportsLfe) { SilenceIfActive(); return; }

            // Feed paused/stopped → game-driven activation goes false (test
            // triggers still fire on their own deadlines).
            bool feedLive = _latestGameActive &&
                (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastFrameTicks)) <= _feedStaleTicks;

            var lfe = _lookup() ?? DefaultLfe;
            TickEngine(lfe, feedLive);
            TickAbs(lfe, feedLive);
            TickGearshift(lfe, feedLive);
        }

        // Game-driven activation, mode-aware. Level = trigger != 0 (continuous);
        // OnChange = burst on trigger change (neutral-suppress + debounce, edge
        // state kept per channel). Test windows are handled by the per-channel
        // tick methods. Any channel can run in either mode (additive partials).
        private bool ActiveByGame(BaseLfeChannel ch, BaseLfeSettings lfe, bool feedLive, ref ChannelState st)
        {
            if (!ch.Enabled || !feedLive) { st.Warm = false; return false; }
            if (ch.TriggerMode == BaseLfeTriggerMode.Level)
                return Triggered(ch.TriggerFormula);

            long now = Stopwatch.GetTimestamp();
            double raw = EvalFormulaRaw(ch.TriggerFormula);
            if (!st.Warm) { st.LastTrigger = raw; st.Warm = true; }
            else if (Math.Abs(raw - st.LastTrigger) > 1e-6)
            {
                bool intoNeutral = Math.Abs(raw) < 0.5;   // gear ≈ 0
                st.LastTrigger = raw;
                int debounceMs = lfe.GearshiftDebounceMs; if (debounceMs < 0) debounceMs = 0;
                bool debounced = (now - st.LastFireTicks) * 1000.0 / Stopwatch.Frequency < debounceMs;
                if ((!intoNeutral || lfe.GearshiftVibrateOnNeutral) && !debounced)
                {
                    st.LastFireTicks = now;
                    st.BurstUntil = now + _burstTicks;
                }
            }
            return st.BurstUntil > now;
        }

        // ── Engine (2 s sweep test) ───────────────────────────────────────────
        private void TickEngine(BaseLfeSettings lfe, bool feedLive)
        {
            var ch = lfe.Engine ?? DefaultLfe.Engine;
            long now = Stopwatch.GetTimestamp();
            bool testActive = Interlocked.Read(ref _engineTestUntil) > now;
            bool active = testActive || ActiveByGame(ch, lfe, feedLive, ref _engineSt);
            if (!active) { DisableIf(ref _engineSt, MozaBaseLfeProtocol.LfeEffect.Engine); return; }

            double freq, intensity01, smoothness01;
            if (testActive)
            {
                double t = (now - Interlocked.Read(ref _engineTestStart)) * 1000.0 / Stopwatch.Frequency / EngineTestMs;
                if (t < 0) t = 0; if (t > 1) t = 1;
                freq = Clamp(ch.Frequency, 0, 500) * (0.12 + 0.88 * t);
                intensity01 = ClampPct(ch.Intensity) / 100.0;
                smoothness01 = ClampPct(ch.Smoothness) / 100.0;
            }
            else EvalRender(ch, out freq, out intensity01, out smoothness01);
            _base.SendBaseLfeEngineStream(playing: true, freq, Envelope(intensity01, ref _engineSt.Phase, freq, smoothness01));
            _engineSt.Active = true;
        }

        // ── ABS (1 s burst test) ──────────────────────────────────────────────
        private void TickAbs(BaseLfeSettings lfe, bool feedLive)
        {
            var ch = lfe.Abs ?? DefaultLfe.Abs;
            bool testActive = Interlocked.Read(ref _absTestUntil) > Stopwatch.GetTimestamp();
            bool active = testActive || ActiveByGame(ch, lfe, feedLive, ref _absSt);
            if (!active) { DisableIf(ref _absSt, MozaBaseLfeProtocol.LfeEffect.Abs); return; }

            double freq, intensity01, smoothness01;
            if (testActive) { freq = Clamp(ch.Frequency, 0, 500); intensity01 = ClampPct(ch.Intensity) / 100.0; smoothness01 = ClampPct(ch.Smoothness) / 100.0; }
            else EvalRender(ch, out freq, out intensity01, out smoothness01);
            _base.SendBaseLfeAbsStream(playing: true, freq, Envelope(intensity01, ref _absSt.Phase, freq, smoothness01));
            _absSt.Active = true;
        }

        // ── Gearshift (two-bump test) ─────────────────────────────────────────
        private void TickGearshift(BaseLfeSettings lfe, bool feedLive)
        {
            var ch = lfe.Gearshift ?? DefaultLfe.Gearshift;
            long now = Stopwatch.GetTimestamp();

            bool testBump = false;
            long ts = Interlocked.Read(ref _gsTestStart);
            if (ts > 0)
            {
                double ms = (now - ts) * 1000.0 / Stopwatch.Frequency;
                if (ms < GsTestBump1EndMs) testBump = true;
                else if (ms >= GsTestBump2StartMs && ms < GsTestBump2EndMs) testBump = true;
                else if (ms >= GsTestBump2EndMs) Interlocked.Exchange(ref _gsTestStart, 0);
            }

            bool active = testBump || ActiveByGame(ch, lfe, feedLive, ref _gearSt);
            if (!active) { DisableIf(ref _gearSt, MozaBaseLfeProtocol.LfeEffect.Gearshift); return; }

            double freq, intensity01, smoothness01;
            if (testBump) { freq = Clamp(ch.Frequency, 0, 500); intensity01 = ClampPct(ch.Intensity) / 100.0; smoothness01 = ClampPct(ch.Smoothness) / 100.0; }
            else EvalRender(ch, out freq, out intensity01, out smoothness01);
            _base.SendBaseLfeGearshiftBurst(freq, Envelope(intensity01, ref _gearSt.Phase, freq, smoothness01));
            _gearSt.Active = true;
        }

        private void EvalRender(BaseLfeChannel ch, out double freq, out double intensity01, out double smoothness01)
        {
            freq = EvalParam(ch.FrequencyFormula, ch.Frequency);
            intensity01 = Clamp01(EvalParam(ch.IntensityFormula, ch.Intensity) / 100.0);
            smoothness01 = Clamp01(EvalParam(ch.SmoothnessFormula, ch.Smoothness) / 100.0);
        }

        private void DisableIf(ref ChannelState st, MozaBaseLfeProtocol.LfeEffect id)
        {
            if (st.Active) { _base.SendBaseLfeDisable(id); st.Active = false; st.Phase = 0; }
        }

        private void SilenceIfActive()
        {
            DisableIf(ref _engineSt, MozaBaseLfeProtocol.LfeEffect.Engine);
            DisableIf(ref _absSt, MozaBaseLfeProtocol.LfeEffect.Abs);
            DisableIf(ref _gearSt, MozaBaseLfeProtocol.LfeEffect.Gearshift);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Trigger truth: empty formula = always active; else eval != 0.</summary>
        private bool Triggered(string? formula)
        {
            if (string.IsNullOrWhiteSpace(formula)) return true;
            return Math.Abs(_evalFormula(formula!)) > 1e-9;
        }

        private double EvalFormulaRaw(string? formula)
            => string.IsNullOrWhiteSpace(formula) ? 0.0 : _evalFormula(formula!);

        /// <summary>Slider value, or the formula's evaluated value when non-empty.</summary>
        private double EvalParam(string? formula, double sliderValue)
            => string.IsNullOrWhiteSpace(formula) ? sliderValue : _evalFormula(formula!);

        /// <summary>
        /// Amplitude envelope: <c>intensity01 × ((1-depth) + depth·(0.5+0.5·sin φ))</c>,
        /// depth = 1 - smoothness01. smoothness 100 → steady; 0 → full-swing pulse.
        /// Phase advances at the carrier frequency.
        /// </summary>
        private static double Envelope(double intensity01, ref double phase, double freqHz, double smoothness01)
        {
            double depth = 1.0 - smoothness01;
            if (depth > 1e-6 && freqHz > 0)
            {
                phase += 2.0 * Math.PI * freqHz * TickPeriodSec;
                if (phase >= 2.0 * Math.PI) phase -= 2.0 * Math.PI * Math.Floor(phase / (2.0 * Math.PI));
            }
            double env = (1.0 - depth) + depth * (0.5 + 0.5 * Math.Sin(phase));
            return Clamp01(intensity01 * env);
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
        private static int ClampPct(int v) => v < 0 ? 0 : (v > 100 ? 100 : v);
    }
}
