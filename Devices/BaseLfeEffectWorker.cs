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
        // Momentary test-button patterns. Which pattern a slot's Test plays is
        // chosen by the slot's Mode (Engine sweep / ABS pulse / Gearshift bump /
        // Custom tone), not the slot's fixed wire id — see RenderTest.
        private const long EngineTestMs = 2000;                // engine test = 2 s frequency sweep
        private const long AbsTestMs = 1000;                   // abs test = 1 s pulse burst
        private const long CustomTestMs = 1000;                // custom test = 1 s steady tone at the slot's values
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

        // Momentary test triggers — one start timestamp per slot (0 = idle). The
        // pattern + duration come from the slot's Mode at render time.
        private long _engineTestStart, _absTestStart, _gearTestStart;

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

        // Each slot's Test plays the pattern for that slot's current Mode.
        public void PostEngineTest() => Interlocked.Exchange(ref _engineTestStart, Stopwatch.GetTimestamp());
        public void PostAbsTest() => Interlocked.Exchange(ref _absTestStart, Stopwatch.GetTimestamp());
        public void PostGearshiftTest() => Interlocked.Exchange(ref _gearTestStart, Stopwatch.GetTimestamp());

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
        private bool ActiveByGame(BaseLfeChannel ch, bool feedLive, ref ChannelState st)
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
                int debounceMs = ch.DebounceMs; if (debounceMs < 0) debounceMs = 0;
                bool debounced = (now - st.LastFireTicks) * 1000.0 / Stopwatch.Frequency < debounceMs;
                if ((!intoNeutral || ch.VibrateOnNeutral) && !debounced)
                {
                    st.LastFireTicks = now;
                    st.BurstUntil = now + _burstTicks;
                }
            }
            return st.BurstUntil > now;
        }

        // ── Engine slot (wire id 1, continuous stream) ────────────────────────
        private void TickEngine(BaseLfeSettings lfe, bool feedLive)
        {
            var ch = lfe.Engine ?? DefaultLfe.Engine;
            if (TickTest(ref _engineTestStart, ch, ref _engineSt, MozaBaseLfeProtocol.LfeEffect.Engine,
                         (f, a) => _base.SendBaseLfeEngineStream(playing: true, f, a))) return;
            if (!ActiveByGame(ch, feedLive, ref _engineSt)) { DisableIf(ref _engineSt, MozaBaseLfeProtocol.LfeEffect.Engine); return; }
            EvalRender(ch, out double freq, out double int01, out double smooth01);
            _base.SendBaseLfeEngineStream(playing: true, freq, Envelope(int01, ref _engineSt.Phase, freq, smooth01));
            _engineSt.Active = true;
        }

        // ── ABS slot (wire id 2, continuous stream) ───────────────────────────
        private void TickAbs(BaseLfeSettings lfe, bool feedLive)
        {
            var ch = lfe.Abs ?? DefaultLfe.Abs;
            if (TickTest(ref _absTestStart, ch, ref _absSt, MozaBaseLfeProtocol.LfeEffect.Abs,
                         (f, a) => _base.SendBaseLfeAbsStream(playing: true, f, a))) return;
            if (!ActiveByGame(ch, feedLive, ref _absSt)) { DisableIf(ref _absSt, MozaBaseLfeProtocol.LfeEffect.Abs); return; }
            EvalRender(ch, out double freq, out double int01, out double smooth01);
            _base.SendBaseLfeAbsStream(playing: true, freq, Envelope(int01, ref _absSt.Phase, freq, smooth01));
            _absSt.Active = true;
        }

        // ── Gearshift slot (wire id 0, one-shot burst) ────────────────────────
        private void TickGearshift(BaseLfeSettings lfe, bool feedLive)
        {
            var ch = lfe.Gearshift ?? DefaultLfe.Gearshift;
            if (TickTest(ref _gearTestStart, ch, ref _gearSt, MozaBaseLfeProtocol.LfeEffect.Gearshift,
                         (f, a) => _base.SendBaseLfeGearshiftBurst(f, a))) return;
            if (!ActiveByGame(ch, feedLive, ref _gearSt)) { DisableIf(ref _gearSt, MozaBaseLfeProtocol.LfeEffect.Gearshift); return; }
            EvalRender(ch, out double freq, out double int01, out double smooth01);
            _base.SendBaseLfeGearshiftBurst(freq, Envelope(int01, ref _gearSt.Phase, freq, smooth01));
            _gearSt.Active = true;
        }

        // Test playback for one slot. Returns true while the test owns the slot
        // (rendered a frame or a silent gap); false once elapsed so the caller
        // falls through to the normal game-driven path. Pattern chosen by Mode.
        private bool TickTest(ref long testStart, BaseLfeChannel ch, ref ChannelState st,
                              MozaBaseLfeProtocol.LfeEffect id, Action<double, double> send)
        {
            long ts = Interlocked.Read(ref testStart);
            if (ts <= 0) return false;
            double ms = (Stopwatch.GetTimestamp() - ts) * 1000.0 / Stopwatch.Frequency;
            if (!RenderTest(ch.Mode, ms, ch, out bool playing, out double freq, out double int01, out double smooth01))
            {
                Interlocked.Exchange(ref testStart, 0);     // window elapsed → hand back to the game path
                return false;
            }
            if (playing) { send(freq, Envelope(int01, ref st.Phase, freq, smooth01)); st.Active = true; }
            else DisableIf(ref st, id);                      // silent gap (gearshift between bumps)
            return true;
        }

        // Momentary test pattern for a slot's Mode. Returns false once the window
        // has elapsed. Uses the slot's static (fallback) values so a parked car
        // (formula → 0) still produces a felt test. `playing` gates the gearshift
        // inter-bump gap.
        private bool RenderTest(BaseLfeMode mode, double ms, BaseLfeChannel ch,
                                out bool playing, out double freq, out double int01, out double smooth01)
        {
            freq = Clamp(ch.Frequency, 0, 500);
            int01 = ClampPct(ch.Intensity) / 100.0;
            smooth01 = ClampPct(ch.Smoothness) / 100.0;
            playing = true;
            switch (mode)
            {
                case BaseLfeMode.Engine:                      // 2 s idle→pitch sweep
                    if (ms > EngineTestMs) return false;
                    freq = Clamp(ch.Frequency, 0, 500) * (0.12 + 0.88 * Clamp01(ms / EngineTestMs));
                    return true;
                case BaseLfeMode.Abs:                         // 1 s pulse (pulse from smoothness envelope)
                    return ms <= AbsTestMs;
                case BaseLfeMode.Gearshift:                   // two rapid bumps
                    if (ms >= GsTestBump2EndMs) return false;
                    playing = ms < GsTestBump1EndMs || (ms >= GsTestBump2StartMs && ms < GsTestBump2EndMs);
                    return true;
                default:                                      // Custom — 1 s steady tone at the slot's values
                    return ms <= CustomTestMs;
            }
        }

        private void EvalRender(BaseLfeChannel ch, out double freq, out double intensity01, out double smoothness01)
        {
            // Plain slider value is used as-is. A formula's output is linearly
            // RE-SCALED from the full 0..200 Hz wire range into the channel's
            // permitted [min, max] band, so the whole dynamic range is preserved
            // (compressed to fit) instead of flat-topping at the limits. The
            // full 0..200 band is the identity map (presets unaffected).
            if (string.IsNullOrWhiteSpace(ch.FrequencyFormula))
                freq = ch.Frequency;
            else
                freq = ch.RescaleFreq(_evalFormula(ch.FrequencyFormula!));
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
