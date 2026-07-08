using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// 50 Hz motor-frame producer + 500 ms keepalive for one mBooster device.
    /// Owns per-effect synthesis state (phase, elapsed, last amplitude) and
    /// the telemetry → freq/intensity mapping per protocol note § 4.
    ///
    /// One worker per <see cref="MBoosterDeviceController"/>. The shared
    /// <c>StreamKind.MBoosterEffect</c> lane on the device's connection
    /// coalesces frames if the writer lags — at 50 Hz with one frame per
    /// tick this is harmless (older tick gets dropped, newer tick lands).
    /// </summary>
    internal sealed class MBoosterEffectWorker : IDisposable
    {
        // Motor loop @ 50 Hz (20 ms period — protocol note § 4).
        private const int TickPeriodMs = 20;
        private const double TickPeriodSec = 0.020;
        // Keepalive @ ~2 Hz — 25 ticks × 20 ms = 500 ms (protocol note § 3 / § 4).
        private const int KeepaliveTickInterval = 25;

        // Per-effect maximum scale at user IntensityPct = 100, per protocol note § 4
        // ("Suggested defaults: 0.01 / 0.10 / 0.15 / 0.10"). The note treats those
        // numbers as the suggested *applied* scale, so user 100 % maps to those
        // ceilings; user 0 % is silent. Matches PitHouse's perceived loudness at
        // equivalent slider positions.
        //
        // Engine is the exception: its intensity slider is meant to be fully
        // parametric like AB9's engine-vibration slider (0-100 % maps ~1:1 to
        // the device's full 0..1 amplitude range), so it has no scale ceiling
        // of its own — see UpdateEngineRequest.
        private const double AbsScaleMax       = 0.10;
        private const double LockupScaleMax    = 0.15;
        private const double ThresholdScaleMax = 0.10;
        // Custom effects share Engine's verified wire shape (see
        // ProcessCustomEffect) so they share a scale cap too — a
        // continuous-mode custom effect (no Threshold gate) can run
        // indefinitely just like Engine does, and would otherwise dominate.
        private const double CustomEffectScaleMax = 0.10;

        private readonly MBoosterDeviceController _device;
        private readonly Func<MBoosterDeviceSettings?> _settingsLookup;
        private readonly Func<bool> _isShuttingDown;
        // Evaluates a custom effect's Formula (bare SimHub property or NCalc
        // expression) to a double each tick. Defaults to "always 0" (never
        // active) if the caller didn't wire one up.
        private readonly Func<string, double> _customEffectFormulaEvaluator;

        private Thread? _thread;
        private volatile bool _stop;

        // Latest telemetry snapshot — published from MozaPlugin.DataUpdate via the
        // registry. Read via Volatile.Read; struct itself is small (≤ ~64 B).
        // We can't make `MBoosterTelemetrySnapshot` volatile so use a holder.
        private MBoosterTelemetrySnapshot _latest = MBoosterTelemetrySnapshot.Empty;
        private readonly object _telemetryLock = new object();

        // Per-effect synthesis state.
        private EffectState _abs;
        private EffectState _lockup;
        private EffectState _threshold;
        private EffectState _engine;
        private EffectState _roadTexture;
        private bool _thresholdLatched; // hysteresis flag for the Threshold effect (doc § 4)

        // Custom (NCalc) effects — one EffectState per user-created effect,
        // keyed by MBoosterCustomEffect.Id (stable across list edits/reorders,
        // unlike an index). Pruned each tick against the live settings list —
        // see UpdateAndProcessCustomEffects.
        private readonly Dictionary<string, EffectState> _customEffectStates =
            new Dictionary<string, EffectState>(StringComparer.Ordinal);

        // Brake Fade — NOT part of the vibration-motor effect pipeline
        // above; ramps TWO real hardware-calibration overrides in lockstep,
        // see UpdateBrakeFadeTravelEnd/UpdateBrakeFadeThreshold. -1 = we
        // haven't overridden that value (device presumably still holds the
        // user's configured base).
        private float _brakeFadeAppliedTravelEndMm = -1;
        private long _brakeFadeTravelEndLastWriteTicks;
        private float _brakeFadeAppliedThresholdKg = -1;
        private long _brakeFadeThresholdLastWriteTicks;
        private volatile bool _brakeFadeTestActive;

        // Engine's, ABS's, Road Texture's, Lockup's, and Threshold's Test
        // toggles all run indefinitely while on, live-tracking Frequency/
        // Intensity/Smoothness/Decay from settings every tick (no snapshot)
        // so slider drags are felt immediately during a test. Set via
        // MBoosterDeviceController.SetEngineTestActive/SetAbsTestActive/
        // SetRoadTextureTestActive/SetLockupTestActive/
        // SetThresholdTestActive. (The old fire-and-forget 1s TestPulse
        // mechanism this replaced across all five effects has been removed
        // entirely — nothing constructs one anymore.)
        private volatile bool _engineTestSustained;
        private volatile bool _absTestSustained;
        private volatile bool _roadTextureTestSustained;
        private volatile bool _lockupTestSustained;
        private volatile bool _thresholdTestSustained;

        // Custom effects' sustained Test toggles — same semantics as the five
        // built-ins above (runs indefinitely, live-tracks Frequency/Intensity,
        // bypasses Enabled/Formula/Threshold entirely), but keyed by
        // MBoosterCustomEffect.Id since the count is unbounded. A
        // ConcurrentDictionary (value unused) rather than a plain HashSet +
        // lock — set/cleared from the UI thread, read every tick from the
        // worker thread. Presence = on.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _customEffectTestSustained =
            new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

        // Keepalive tick counter (mod KeepaliveTickInterval).
        private int _keepaliveCounter;

        // This worker drives ONE pedal on the lane: its HID axis index (for the
        // brake-position feed + role resolution), the motor device id its frames
        // are addressed to (0x12 host / 0x1d / 0x1e chain — configured effects
        // go to the device the pedal belongs to), and whether it's the primary
        // worker (only the primary sends the shared keepalive and runs Brake
        // Fade, which rewrites the brake's calibration once per lane).
        private readonly int _pedalAxisIndex;
        private readonly byte _targetDevice;
        private readonly bool _isPrimary;

        /// <summary>This pedal's vibration-effect settings — the master's flat
        /// fields for axis 0, else the per-pedal entry (both implement
        /// <see cref="IMBoosterEffects"/>). Null when no per-pedal entry exists.</summary>
        private IMBoosterEffects? PedalEffects(MBoosterDeviceSettings? lane)
        {
            if (_pedalAxisIndex == 0) return lane;
            // Snapshot the dictionary reference: the UI publishes new pedal
            // entries via copy-on-write (atomic reference swap in
            // CurrentMBoosterEffectTarget), so a snapshot is an immutable view —
            // safe to read from this worker thread without a lock.
            var pedals = lane?.Pedals;
            if (pedals != null && pedals.TryGetValue(_pedalAxisIndex, out var p)) return p;
            return null;
        }

        /// <summary>This pedal's role (for the brake-position test feed).</summary>
        private MBoosterRole PedalRole(MBoosterDeviceSettings? lane)
        {
            int axisCount = _device.AxisCount > 0 ? _device.AxisCount : 1;
            return MozaMBoosterRegistry.ResolveAxisRole(lane, _pedalAxisIndex, axisCount);
        }

        /// <summary>This pedal's own shaped HID position (0..1).</summary>
        private double PedalHid() =>
            _pedalAxisIndex == 0 ? _device.LastHidPosition
            : (_pedalAxisIndex < _device.LastAxisPositions.Length ? _device.LastAxisPositions[_pedalAxisIndex] : 0.0);

        /// <summary>
        /// Emit a motor frame (already addressed to this pedal's device id) for
        /// this pedal. The primary (device 0x12) uses the coalescing stream lane
        /// so its effects follow the latest-wins priority ladder; chained pedals
        /// (0x1d/0x1e) use the one-shot FIFO so they don't clobber the primary's
        /// single shared StreamKind lane. In the common one-effect-per-pedal case
        /// this is equivalent; only simultaneous effects on ONE chained pedal
        /// skip the ladder (rare).
        /// </summary>
        private void SendMotor(byte[] frame)
        {
            if (_isPrimary) _device.SendMotorStream(frame);
            else _device.SendOneShot(frame);
        }

        private struct EffectState
        {
            public bool Active;
            public double PhaseRad;     // wraps at 2π
            public double ElapsedSec;
            public double IntensityRequest;  // 0..1
            public double FreqHz;            // user/telemetry-mapped frequency
            public double SmoothnessRequest01; // 0..1, ABS-only for now
            public double RoadTextureRoughness01; // 0..1, Road-Texture-only: live suspension-derived intensity scale
            public double ThresholdDecayRequest01; // 0..1, Threshold-only: sustain-decay depth
        }

        public MBoosterEffectWorker(
            MBoosterDeviceController device,
            Func<MBoosterDeviceSettings?> settingsLookup,
            Func<bool> isShuttingDown,
            Func<string, double>? customEffectFormulaEvaluator = null,
            int pedalAxisIndex = 0,
            byte targetDevice = MozaMBoosterProtocol.DeviceMotor,
            bool isPrimary = true)
        {
            _device = device;
            _settingsLookup = settingsLookup;
            _isShuttingDown = isShuttingDown;
            _customEffectFormulaEvaluator = customEffectFormulaEvaluator ?? (_ => 0.0);
            _pedalAxisIndex = pedalAxisIndex;
            _targetDevice = targetDevice;
            _isPrimary = isPrimary;
        }

        public void Start()
        {
            _stop = false;
            if (_thread != null && _thread.IsAlive) return;
            _thread = new Thread(Loop)
            {
                Name = "MozaMBoosterEffect-" + MBoosterDeviceController.ShortIdentity(_device.Identity),
                IsBackground = true,
            };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            try { _thread?.Join(1000); } catch { }
            _thread = null;
            // Best-effort only — covers a clean disconnect/shutdown while
            // connected. An abrupt crash/force-quit while an override is
            // active can still leave the device holding the extended
            // Travel End/Max Threshold until brake temp is next read as
            // cooled (or the user re-applies their Pedal Feel Travel slider
            // / Sim Input Mapping Max Threshold slider), since there is no
            // watchdog outside this worker's own tick loop.
            TryRestoreBrakeFadeOnStop();
        }

        private void TryRestoreBrakeFadeOnStop()
        {
            var settings = _settingsLookup();
            if (_brakeFadeAppliedTravelEndMm >= 0)
            {
                float baseMm = settings?.TravelEndMm ?? -1;
                if (baseMm >= 0 && Math.Abs(_brakeFadeAppliedTravelEndMm - baseMm) >= 0.01f
                    && _device.SendIntWrite("mbooster-brake-travel-end", MozaMBoosterProtocol.EncodeTravelMm(baseMm)))
                    _brakeFadeAppliedTravelEndMm = baseMm;
            }
            if (_brakeFadeAppliedThresholdKg >= 0)
            {
                float baseKg = settings?.MaxThresholdKg ?? -1;
                if (baseKg >= 0 && Math.Abs(_brakeFadeAppliedThresholdKg - baseKg) >= 0.5f
                    && _device.SendIntWrite("mbooster-brake-threshold", MozaMBoosterProtocol.EncodeThresholdKg(baseKg)))
                    _brakeFadeAppliedThresholdKg = baseKg;
            }
        }

        public void Dispose() => Stop();

        public void PostFrame(in MBoosterTelemetrySnapshot snap)
        {
            lock (_telemetryLock) _latest = snap;
        }

        /// <summary>Turn Engine's sustained test toggle on/off. See <see cref="_engineTestSustained"/>.</summary>
        public void SetEngineTestSustained(bool on) => _engineTestSustained = on;

        /// <summary>Turn ABS's sustained test toggle on/off. See <see cref="_absTestSustained"/>.</summary>
        public void SetAbsTestSustained(bool on) => _absTestSustained = on;

        /// <summary>Turn Road Texture's sustained test toggle on/off. See <see cref="_roadTextureTestSustained"/>.</summary>
        public void SetRoadTextureTestSustained(bool on) => _roadTextureTestSustained = on;

        /// <summary>Turn Lockup's sustained test toggle on/off. See <see cref="_lockupTestSustained"/>.</summary>
        public void SetLockupTestSustained(bool on) => _lockupTestSustained = on;

        /// <summary>Turn Threshold's sustained test toggle on/off. See <see cref="_thresholdTestSustained"/>.</summary>
        public void SetThresholdTestSustained(bool on) => _thresholdTestSustained = on;

        /// <summary>Turn Brake Fade's sustained test toggle on/off. See <see cref="_brakeFadeTestActive"/>.</summary>
        public void SetBrakeFadeTestSustained(bool on) => _brakeFadeTestActive = on;

        /// <summary>
        /// Turn one custom effect's sustained test toggle on/off. See
        /// <see cref="_customEffectTestSustained"/>. Effects with no id are
        /// never testable (nothing to key the toggle on).
        /// </summary>
        public void SetCustomEffectTestSustained(string effectId, bool on)
        {
            if (string.IsNullOrEmpty(effectId)) return;
            if (on) _customEffectTestSustained[effectId] = true;
            else _customEffectTestSustained.TryRemove(effectId, out _);
        }

        private void Loop()
        {
            long stopwatchFreq = Stopwatch.Frequency;
            long periodTicks = stopwatchFreq * TickPeriodMs / 1000;
            long next = Stopwatch.GetTimestamp() + periodTicks;
            while (!_stop)
            {
                try { Tick(); }
                catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] worker tick: {ex.Message}"); }

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
            if (!_device.IsConnected) return;

            var lane = _settingsLookup();
            var effects = PedalEffects(lane);

            MBoosterTelemetrySnapshot snap;
            lock (_telemetryLock) snap = _latest;

            // Brake signal for THIS pedal: the game's brake, rising to this
            // pedal's own HID position when it's the one assigned Brake (so its
            // brake-modulated test pulses feel right with no game running).
            double brakeSignal = EffectiveBrake(PedalRole(lane), snap);

            // --- Compute per-effect requests from telemetry per doc § 4 -----

            UpdateEngineRequest(effects, snap, ref _engine);
            UpdateAbsRequest(effects, brakeSignal, snap, ref _abs);
            UpdateLockupRequest(effects, brakeSignal, snap, ref _lockup);
            UpdateThresholdRequest(effects, brakeSignal, snap, ref _threshold);
            UpdateRoadTextureRequest(effects, snap, ref _roadTexture);

            // --- Apply per-effect activation edges + emit motor frame ------
            //
            // All five vibration effects share ONE latest-wins motor stream
            // slot (MozaSerialConnection StreamKind.MBoosterEffect —
            // SendStream overwrites the pending value), so when more than
            // one effect is active in the same tick only the LAST frame
            // emitted here reaches the motor. Emission order is therefore a
            // priority ladder, lowest first: the two continuous "ambient"
            // effects (Engine, Road Texture) are emitted BEFORE the
            // transient braking cues (ABS, Lockup, Threshold) so a
            // lockup/ABS/threshold pulse always overrides the ambient
            // vibration instead of being masked by it.
            ProcessEffect(MBoosterEffectId.Engine,    ref _engine);
            // (ProcessRoadTextureEffect / custom below now take this pedal's effects.)
            // Road Texture has a materially different wire payload (see
            // MozaMBoosterProtocol.BuildRoadTextureFrame) so it doesn't go
            // through the shared ProcessEffect/BuildMotorFrame path. Even
            // though it now only streams frames for the duration of a bump's
            // decaying pulse (see UpdateRoadTextureRequest) rather than the
            // whole time the car is moving, it still needs to sit here
            // (ambient tier, before the braking cues) so a lockup/ABS/
            // threshold pulse that lands in the same tick as a bump always
            // wins instead of being masked by it.
            ProcessRoadTextureEffect(effects, ref _roadTexture);
            // Custom (NCalc) effects — Experimental. Placed in the ambient
            // tier (after Engine/Road Texture, before the braking cues) so a
            // user-authored effect can override built-in ambient vibration
            // but never masks a real ABS/Lockup/Threshold safety pulse. They
            // also share Engine's wire slot (effect type 4 — see
            // ProcessCustomEffect), so a custom effect active in the same
            // tick as the real Engine effect wins on the wire, same
            // last-write-wins masking rule as every other pair on this ladder.
            UpdateAndProcessCustomEffects(effects);
            ProcessEffect(MBoosterEffectId.Abs,       ref _abs);
            ProcessEffect(MBoosterEffectId.Lockup,    ref _lockup);
            ProcessEffect(MBoosterEffectId.Threshold, ref _threshold);

            // Brake Fade is NOT a vibration effect — it rewrites the brake's
            // hardware calibration, so it runs ONCE per lane on the primary
            // worker (not per pedal) against the lane settings. See UpdateBrakeFade.
            if (_isPrimary) UpdateBrakeFade(lane, snap);

            // --- 500 ms keepalive (separate from motor frames) -------------
            // Primary worker only, and to ALL of the chain's motor device ids
            // (0x12 host + 0x1d/0x1e chain ports), matching PitHouse — a chained
            // active mBooster's motor drops its connection state if its own
            // device id isn't kept alive. Harmless for empty/passive ports.
            if (_isPrimary)
            {
                _keepaliveCounter++;
                if (_keepaliveCounter >= KeepaliveTickInterval)
                {
                    _keepaliveCounter = 0;
                    foreach (var dev in MozaMBoosterProtocol.MotorDeviceIds)
                        _device.SendOneShot(MozaMBoosterProtocol.BuildKeepalive(dev));
                }
            }
        }

        // ===== Telemetry → effect parameters (doc § 4) ====================

        private void UpdateEngineRequest(IMBoosterEffects? effects, in MBoosterTelemetrySnapshot snap, ref EffectState st)
        {
            // Engine's frequency used to be derived from RPM (doc § 4:
            // clamp(rpm / 20000 * 200, 10, 200)); it's now a fixed,
            // user-configured value (MBoosterEffectSettings.FrequencyHz,
            // 60-200Hz) — Intensity is still the only thing telemetry (or
            // the test pulse) modulates.
            double freqHz = ClampEngineFreq(effects?.Engine?.FrequencyHz ?? MBoosterUiConstants.EngineFreqMinHz);

            // Sustained test toggle overrides telemetry-driven engine
            // entirely (ignores Enabled/RPM-idle gates, matching how the
            // other effects' test pulses also bypass them) and, unlike a
            // one-shot pulse, re-reads Intensity live every tick so slider
            // drags are felt immediately while testing.
            if (_engineTestSustained)
            {
                st.IntensityRequest = Clamp01((effects?.Engine?.IntensityPct ?? 0) / 100.0);
                st.FreqHz = freqHz;
                return;
            }

            if (effects?.Engine == null || !effects.Engine.Enabled)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                return;
            }

            double rpm = snap.Rpm;
            double idle = Math.Max(snap.IdleRpm, 500);
            if (!snap.GameRunning || rpm <= 0.8 * idle)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                return;
            }

            st.FreqHz = freqHz;
            // Engine continuous-effect: user 0..100 % maps ~1:1 to output
            // amplitude 0..1, matching AB9's parametric engine-vibration
            // intensity slider (no artificial ceiling — see the constants
            // block above).
            st.IntensityRequest = Clamp01(effects.Engine.IntensityPct / 100.0);
        }

        private static double ClampEngineFreq(double hz)
        {
            if (hz < MBoosterUiConstants.EngineFreqMinHz) return MBoosterUiConstants.EngineFreqMinHz;
            if (hz > MBoosterUiConstants.EngineFreqMaxHz) return MBoosterUiConstants.EngineFreqMaxHz;
            return hz;
        }

        private void UpdateAbsRequest(IMBoosterEffects? effects, double brakeSignal, in MBoosterTelemetrySnapshot snap, ref EffectState st)
        {
            // ABS's frequency used to be derived from ABS-activation depth
            // (doc § 4: 18 + abs01*12, 18-30Hz) — but the plugin's snapshot
            // exposes AbsActive as a bool, not the 0..1 float the doc's
            // pseudocode expects, which collapsed that mapping to a constant
            // 30Hz anyway. It's now a fixed, user-configured value
            // (MBoosterEffectSettings.FrequencyHz, 5-30Hz).
            double freqHz = ClampAbsFreq(effects?.Abs?.FrequencyHz ?? MBoosterUiConstants.AbsFreqMinHz);
            double smoothness01 = Clamp01((effects?.Abs?.SmoothnessPct ?? 100) / 100.0);
            st.SmoothnessRequest01 = smoothness01;

            // Sustained test toggle overrides telemetry-driven ABS entirely
            // (ignoring Enabled), substituting live brake position for
            // absActive — same substitution the old 1s test pulse used
            // (there's no live ABS-activation signal to press against
            // outside a real ABS event) — just indefinite, and live-tracking
            // Frequency/Intensity/Smoothness every tick instead of
            // snapshotting them. Gated at 60% brake (not any nonzero press)
            // so the test only fires once you're pressing hard enough to
            // plausibly trigger real ABS, not on a light tap.
            if (_absTestSustained)
            {
                double brakeT = brakeSignal;
                if (brakeT < 0.6)
                {
                    st.IntensityRequest = 0;
                    st.FreqHz = 0;
                    return;
                }
                double testScale = ((effects?.Abs?.IntensityPct ?? 0) / 100.0) * AbsScaleMax;
                st.IntensityRequest = Clamp01(brakeT * testScale);
                st.FreqHz = freqHz;
                return;
            }

            if (effects?.Abs == null || !effects.Abs.Enabled)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                return;
            }

            double abs01 = snap.AbsActive ? 1.0 : 0.0;
            if (abs01 <= 0.1)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                return;
            }
            st.FreqHz = freqHz;
            double absScale = (effects.Abs.IntensityPct / 100.0) * AbsScaleMax;
            st.IntensityRequest = Clamp01(abs01 * absScale);
        }

        private static double ClampAbsFreq(double hz)
        {
            if (hz < MBoosterUiConstants.AbsFreqMinHz) return MBoosterUiConstants.AbsFreqMinHz;
            if (hz > MBoosterUiConstants.AbsFreqMaxHz) return MBoosterUiConstants.AbsFreqMaxHz;
            return hz;
        }

        private void UpdateLockupRequest(IMBoosterEffects? effects, double brakeSignal, in MBoosterTelemetrySnapshot snap, ref EffectState st)
        {
            // Lockup's frequency used to be derived from brake position
            // (doc § 4: 40 + brake*30, 40-70Hz); it's now a fixed,
            // user-configured value (MBoosterEffectSettings.FrequencyHz,
            // 10-100Hz), same treatment as Engine/ABS. The wheel-slip
            // detection gate below (brake + speed + wheel-speed heuristic)
            // is unchanged — only frequency became fixed.
            double freqHz = ClampLockupFreq(effects?.Lockup?.FrequencyHz ?? MBoosterUiConstants.LockupFreqMinHz);

            // Sustained test toggle bypasses the lockup-detection heuristic
            // (which needs vehicle speed) entirely, substituting live brake
            // position for it — same substitution the old 1s test pulse
            // used — just indefinite, and live-tracking Frequency/Intensity
            // every tick instead of snapshotting them.
            if (_lockupTestSustained)
            {
                double brakeT = brakeSignal;
                if (brakeT <= 0.01)
                {
                    st.IntensityRequest = 0;
                    st.FreqHz = 0;
                    return;
                }
                double testScale = ((effects?.Lockup?.IntensityPct ?? 0) / 100.0) * LockupScaleMax;
                st.IntensityRequest = Clamp01(brakeT * testScale);
                st.FreqHz = freqHz;
                return;
            }

            if (effects?.Lockup == null || !effects.Lockup.Enabled)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                return;
            }

            double brake = Clamp01(snap.Brake);
            double vehicleSpeed = Math.Abs(snap.VehicleSpeedMs);
            double avgWheelSpeed = Math.Abs(snap.AvgWheelSpeedMs);

            bool isLocking = brake > 0.8
                          && vehicleSpeed > 5
                          && avgWheelSpeed < vehicleSpeed * 0.3;

            // Fallback path: many games don't expose per-wheel speeds. If
            // avgWheelSpeedMs is zero AND vehicle is moving heavily braked,
            // treat as a probable lockup so the effect still fires meaningfully.
            if (!isLocking && avgWheelSpeed <= 0 && brake > 0.9 && vehicleSpeed > 5)
                isLocking = true;

            if (!isLocking)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                return;
            }
            st.FreqHz = freqHz;
            double lockupScale = (effects.Lockup.IntensityPct / 100.0) * LockupScaleMax;
            st.IntensityRequest = Clamp01(brake * lockupScale);
        }

        private static double ClampLockupFreq(double hz)
        {
            if (hz < MBoosterUiConstants.LockupFreqMinHz) return MBoosterUiConstants.LockupFreqMinHz;
            if (hz > MBoosterUiConstants.LockupFreqMaxHz) return MBoosterUiConstants.LockupFreqMaxHz;
            return hz;
        }

        // Threshold's frequency used to be derived from brake position
        // (doc § 4: 60 + brake*30, 60-90Hz); it's now a fixed, user-
        // configured value (MBoosterEffectSettings.FrequencyHz, 5-100Hz),
        // same treatment as Engine/ABS/Lockup. The rising-edge trigger
        // point (originally a fixed 0.6, with release at a fixed 0.3) is
        // now also user-configured via TriggerLevelPct (50-100%) — the
        // release point stays a fixed 30 points below it, preserving the
        // original hysteresis gap. Decay (envelope sustain level after the
        // initial burst) is likewise now configurable — see
        // MBoosterEffectSynthesizer.SynthesizeThreshold.
        private void UpdateThresholdRequest(IMBoosterEffects? effects, double brakeSignal, in MBoosterTelemetrySnapshot snap, ref EffectState st)
        {
            double freqHz = ClampThresholdFreq(effects?.Threshold?.FrequencyHz ?? MBoosterUiConstants.ThresholdFreqMinHz);
            double decay01 = Clamp01((effects?.Threshold?.DecayPct ?? 20) / 100.0);
            st.ThresholdDecayRequest01 = decay01;
            double triggerLevel = Clamp01((effects?.Threshold?.TriggerLevelPct ?? MBoosterUiConstants.ThresholdTriggerMinPct) / 100.0);
            double releaseLevel = Math.Max(0, triggerLevel - 0.3);

            // Sustained test toggle shares the same rising-edge hysteresis
            // as real gameplay — the effect shouldn't fire until the
            // configured Trigger Input Level is actually reached, so the
            // user can verify the threshold feels right rather than getting
            // a false "it works" from any light tap. Only Frequency/
            // Intensity/Decay are live-tracked from settings instead of the
            // 1s-pulse snapshot the old mechanism used; the hysteresis logic
            // itself (and _thresholdLatched) is shared with the real path
            // below since only one of the two runs per tick.
            if (_thresholdTestSustained)
            {
                double brakeT = brakeSignal;
                if (!_thresholdLatched && brakeT > triggerLevel)
                    _thresholdLatched = true;
                else if (_thresholdLatched && brakeT < releaseLevel)
                    _thresholdLatched = false;

                if (!_thresholdLatched)
                {
                    st.IntensityRequest = 0;
                    st.FreqHz = 0;
                    return;
                }
                double testScale = ((effects?.Threshold?.IntensityPct ?? 0) / 100.0) * ThresholdScaleMax;
                st.IntensityRequest = Clamp01(brakeT * testScale);
                st.FreqHz = freqHz;
                return;
            }

            if (effects?.Threshold == null || !effects.Threshold.Enabled)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                _thresholdLatched = false;
                return;
            }

            double brake = Clamp01(snap.Brake);
            if (!_thresholdLatched && brake > triggerLevel)
                _thresholdLatched = true;
            else if (_thresholdLatched && brake < releaseLevel)
                _thresholdLatched = false;

            if (!_thresholdLatched)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                return;
            }
            st.FreqHz = freqHz;
            double thresholdScale = (effects.Threshold.IntensityPct / 100.0) * ThresholdScaleMax;
            st.IntensityRequest = Clamp01(brake * thresholdScale);
        }

        private static double ClampThresholdFreq(double hz)
        {
            if (hz < MBoosterUiConstants.ThresholdFreqMinHz) return MBoosterUiConstants.ThresholdFreqMinHz;
            if (hz > MBoosterUiConstants.ThresholdFreqMaxHz) return MBoosterUiConstants.ThresholdFreqMaxHz;
            return hz;
        }

        // Road Texture's Smoothness is still sent as a fixed user-configured
        // percentage (the firmware applies it internally to the noise
        // signal we stream; see ProcessRoadTextureEffect). Intensity is
        // driven by a bump/kerb detector, not a constant ambient level:
        // there's no generic suspension-travel telemetry in SimHub (see
        // MBoosterTelemetrySnapshot.SuspensionHeaveG's doc comment), so
        // vertical chassis acceleration (heave) stands in for it. A single
        // bump only spikes AccelerationHeave for one or two ticks (40 ms),
        // too short to feel as a motor pulse, so RoadTextureRoughness01 is a
        // peak-and-decay envelope (fast attack on a heave spike above
        // RoadTextureBumpTriggerG, exponential release with time constant
        // RoadTextureBumpDecayTau) rather than the instantaneous |heave|
        // reading — this also lets the effect go fully silent (activation
        // edge fires, disable frame sent) between bumps instead of
        // streaming near-zero noise the whole time you're driving, same
        // "quiet unless something is actually happening" contract Lockup/
        // Threshold/ABS already have. The sustained test toggle bypasses
        // Enabled/the telemetry gate entirely and previews at full envelope
        // (1.0), same as Engine's and ABS's tests — there's no live road to
        // preview against outside a real drive.
        private const double RoadTextureHeaveScaleMaxG = 1.0; // 1g vertical accel -> envelope saturates at 100%
        // Heuristic, not a hardware-verified value (there's no wire-protocol
        // reference for a host-side telemetry threshold) — chosen so normal
        // tarmac's small accelerometer noise stays under it while a real
        // bump/kerb strike clears it. Same spirit as Lockup's hardcoded
        // brake/speed/wheel-slip heuristic: not user-configurable.
        private const double RoadTextureBumpTriggerG = 0.15;
        // Exponential release time constant, seconds — how long a single
        // bump's pulse takes to decay back toward silence. ~0.15 s gives a
        // punchy, distinct "hit" rather than a lingering buzz.
        private const double RoadTextureBumpDecayTau = 0.15;
        private static readonly double RoadTextureBumpDecayPerTick = Math.Exp(-TickPeriodSec / RoadTextureBumpDecayTau);

        private void UpdateRoadTextureRequest(IMBoosterEffects? effects, in MBoosterTelemetrySnapshot snap, ref EffectState st)
        {
            if (_roadTextureTestSustained)
            {
                st.IntensityRequest = 1;
                st.RoadTextureRoughness01 = 1;
                return;
            }
            bool active = effects?.RoadTexture != null && effects.RoadTexture.Enabled
                && snap.GameRunning && snap.VehicleSpeedMs > 0.5;
            if (!active)
            {
                st.IntensityRequest = 0;
                st.RoadTextureRoughness01 = 0;
                return;
            }
            double bumpMagnitude01 = Clamp01(
                (Math.Abs(snap.SuspensionHeaveG) - RoadTextureBumpTriggerG)
                / (RoadTextureHeaveScaleMaxG - RoadTextureBumpTriggerG));
            double decayed = st.RoadTextureRoughness01 * RoadTextureBumpDecayPerTick;
            double envelope = Math.Max(bumpMagnitude01, decayed);
            st.RoadTextureRoughness01 = envelope;
            // Below this, the envelope is inaudible/imperceptible on the
            // motor — treat as fully silent so the activation edge below
            // actually fires (disable frame sent) instead of streaming a
            // frame with a rounds-to-zero amplitude forever.
            st.IntensityRequest = envelope > 0.01 ? 1 : 0;
        }

        // Brake Fade — NOT a vibration effect. Dynamically rewrites TWO real
        // hardware calibrations in lockstep as brake temp climbs past
        // BrakeFadeOnsetC, using the SAME ramp01 fraction for both so they
        // progress together:
        // - Travel End (mbooster-brake-travel-end, TravelEndMm's own wire
        //   command) — more physical travel needed to reach 100%.
        // - Max Threshold (mbooster-brake-threshold, MaxThresholdKg's own
        //   wire command) — more load-cell force needed to reach 100%, the
        //   real (non-host-side) equivalent of "softer to press": unlike
        //   MaxForceKg (Pedal Feel), MaxThreshold actually changes what the
        //   game receives, since it's the device's own calibration.
        // Both restore to their configured base values as temp cools. Each
        // is independently gated on already having a known base (>= 0) to
        // restore to — without one, that ONE calibration stays fully inert
        // rather than guessing (the other can still ramp on its own).
        //
        // Unlike the vibration effects, calibration writes are a real
        // hardware command with no evidence they're safe to stream at 50Hz
        // (see docs/protocol/devices/mbooster.md "Pedal Feel" — every other
        // calibration write in this app only fires when a user drags a
        // slider thumb, not continuously). Both Apply* helpers throttle
        // writes to at most once per BrakeFadeWriteMinIntervalSec AND only
        // when the target has moved by at least their own min-delta —
        // except restoring to the exact base value on cooldown/disable,
        // which is a safety action and always goes through immediately.
        private const double BrakeFadeSpanC = 200.0;
        private const double BrakeFadeWriteMinIntervalSec = 0.5;
        private const float BrakeFadeWriteMinDeltaMm = 0.2f;
        private const float BrakeFadeWriteMinDeltaKg = 1.0f;

        private void UpdateBrakeFade(MBoosterDeviceSettings? settings, in MBoosterTelemetrySnapshot snap)
        {
            var bf = settings?.BrakeFade;
            double ramp01;
            if (_brakeFadeTestActive) ramp01 = 1.0;
            else if (bf == null || !bf.Enabled) ramp01 = 0.0;
            else ramp01 = Clamp01((snap.BrakeTempC - bf.BrakeFadeOnsetC) / BrakeFadeSpanC);

            UpdateBrakeFadeTravelEnd(settings, ramp01);
            UpdateBrakeFadeThreshold(settings, ramp01);
        }

        private void UpdateBrakeFadeTravelEnd(MBoosterDeviceSettings? settings, double ramp01)
        {
            float baseMm = settings?.TravelEndMm ?? -1;
            if (baseMm < 0) return; // no known safe base — stay fully inert

            float cap = MBoosterUiConstants.BrakeFadeMaxTravelEndMm;
            float extendedMm = (float)(baseMm + ramp01 * (cap - baseMm));
            // Never shrink below the user's own base — if baseMm is already
            // >= cap there's no room to extend at all.
            float targetMm = extendedMm > baseMm ? Math.Min(extendedMm, cap) : baseMm;

            bool isRestoreToBase = Math.Abs(targetMm - baseMm) < 0.01f;
            if (_brakeFadeAppliedTravelEndMm < 0)
            {
                // Never overridden anything yet this session — assume the
                // device currently holds the base value, so don't fire a
                // spurious write just to "confirm" that on every tick.
                if (isRestoreToBase) return;
                _brakeFadeAppliedTravelEndMm = baseMm;
            }

            float delta = Math.Abs(targetMm - _brakeFadeAppliedTravelEndMm);
            double sinceLastWriteSec = (Stopwatch.GetTimestamp() - _brakeFadeTravelEndLastWriteTicks) / (double)Stopwatch.Frequency;

            // Restoring to baseline is a safety action, never throttled away.
            bool shouldWrite = isRestoreToBase
                ? delta > 0.01f
                : delta >= BrakeFadeWriteMinDeltaMm && sinceLastWriteSec >= BrakeFadeWriteMinIntervalSec;
            if (!shouldWrite) return;

            if (!_device.SendIntWrite("mbooster-brake-travel-end", MozaMBoosterProtocol.EncodeTravelMm(targetMm)))
                return; // not connected — nothing written, don't update tracking state

            _brakeFadeAppliedTravelEndMm = targetMm;
            _brakeFadeTravelEndLastWriteTicks = Stopwatch.GetTimestamp();
        }

        private void UpdateBrakeFadeThreshold(MBoosterDeviceSettings? settings, double ramp01)
        {
            float baseKg = settings?.MaxThresholdKg ?? -1;
            if (baseKg < 0) return; // no known safe base — stay fully inert

            float cap = MBoosterUiConstants.BrakeFadeMaxThresholdKg;
            float extendedKg = (float)(baseKg + ramp01 * (cap - baseKg));
            float targetKg = extendedKg > baseKg ? Math.Min(extendedKg, cap) : baseKg;

            bool isRestoreToBase = Math.Abs(targetKg - baseKg) < 0.5f;
            if (_brakeFadeAppliedThresholdKg < 0)
            {
                if (isRestoreToBase) return;
                _brakeFadeAppliedThresholdKg = baseKg;
            }

            float delta = Math.Abs(targetKg - _brakeFadeAppliedThresholdKg);
            double sinceLastWriteSec = (Stopwatch.GetTimestamp() - _brakeFadeThresholdLastWriteTicks) / (double)Stopwatch.Frequency;

            bool shouldWrite = isRestoreToBase
                ? delta > 0.5f
                : delta >= BrakeFadeWriteMinDeltaKg && sinceLastWriteSec >= BrakeFadeWriteMinIntervalSec;
            if (!shouldWrite) return;

            if (!_device.SendIntWrite("mbooster-brake-threshold", MozaMBoosterProtocol.EncodeThresholdKg(targetKg)))
                return;

            _brakeFadeAppliedThresholdKg = targetKg;
            _brakeFadeThresholdLastWriteTicks = Stopwatch.GetTimestamp();
        }

        // ===== Edge handling + frame emission =============================

        private void ProcessEffect(MBoosterEffectId id, ref EffectState st)
        {
            bool wantActive = st.IntensityRequest > 0 && st.FreqHz > 0;

            if (!wantActive && st.Active)
            {
                // Deactivation edge: emit one disable frame and go silent.
                _device.SendOneShot(MozaMBoosterProtocol.BuildDisableFrame(id, _targetDevice));
                st.Active = false;
                st.PhaseRad = 0;
                st.ElapsedSec = 0;
                return;
            }
            if (!wantActive)
            {
                return;   // already silent — doc § 4: don't send frames while inactive
            }

            // Activation or already-active path.
            if (!st.Active)
            {
                st.Active = true;
                st.PhaseRad = 0;
                st.ElapsedSec = 0;
            }

            st.ElapsedSec += TickPeriodSec;
            // phase += 2π * freq * dt; wrap at 2π for numerical stability.
            st.PhaseRad += 2.0 * Math.PI * st.FreqHz * TickPeriodSec;
            if (st.PhaseRad >= 2.0 * Math.PI)
                st.PhaseRad -= 2.0 * Math.PI * Math.Floor(st.PhaseRad / (2.0 * Math.PI));

            double amp01 = id switch
            {
                MBoosterEffectId.Abs       => MBoosterEffectSynthesizer.SynthesizeAbs(st.IntensityRequest, st.PhaseRad, st.SmoothnessRequest01),
                MBoosterEffectId.Lockup    => MBoosterEffectSynthesizer.SynthesizeLockup(st.IntensityRequest, st.ElapsedSec),
                MBoosterEffectId.Threshold => MBoosterEffectSynthesizer.SynthesizeThreshold(st.IntensityRequest, st.ElapsedSec, st.ThresholdDecayRequest01),
                MBoosterEffectId.Engine    => MBoosterEffectSynthesizer.SynthesizeEngine(st.IntensityRequest, st.PhaseRad),
                _                          => 0.0,
            };

            byte param1 = MozaMBoosterProtocol.ComputeParam1(
                MozaMBoosterProtocol.ParamKFor(id), st.FreqHz);
            ushort freqU16 = MozaMBoosterProtocol.EncodeFreq(st.FreqHz);
            ushort ampU16 = MozaMBoosterProtocol.EncodeAmp(amp01);

            var frame = MozaMBoosterProtocol.BuildMotorFrame(id, enable: true, param1, freqU16, ampU16, _targetDevice);
            SendMotor(frame);
        }

        /// <summary>
        /// Road Texture's activation-edge + frame-emission path — separate
        /// from <see cref="ProcessEffect"/> because its wire payload shape
        /// is different (no ComputeParam1/EncodeFreq/EncodeAmp; Intensity
        /// and Smoothness go out as raw percentages via
        /// <see cref="MozaMBoosterProtocol.EncodeRoadTextureLevel"/>, and the
        /// "freq" slot carries a live noise sample instead of a Hz value —
        /// see <see cref="MozaMBoosterProtocol.BuildRoadTextureFrame"/>).
        /// Mirrors ProcessEffect's activation-edge/disable-frame handling
        /// otherwise (only <see cref="EffectState.IntensityRequest"/> gates
        /// session activity here, not FreqHz, since Road Texture has no
        /// frequency setting of its own). <see cref="EffectState.IntensityRequest"/>
        /// (and hence <paramref name="st"/>'s activation edge) now tracks the
        /// bump/kerb peak-and-decay envelope computed in
        /// <see cref="UpdateRoadTextureRequest"/> — the effect goes fully
        /// silent (disable frame sent) on smooth track and only streams
        /// frames for the duration of a bump's decaying pulse, instead of
        /// running continuously the whole time you're driving. The
        /// transmitted Intensity is the user's configured percentage scaled
        /// by that same envelope (<see cref="EffectState.RoadTextureRoughness01"/>)
        /// every tick.
        /// </summary>
        private void ProcessRoadTextureEffect(IMBoosterEffects? effects, ref EffectState st)
        {
            const MBoosterEffectId id = MBoosterEffectId.RoadTexture;
            bool wantActive = st.IntensityRequest > 0;

            if (!wantActive && st.Active)
            {
                _device.SendOneShot(MozaMBoosterProtocol.BuildDisableFrame(id, _targetDevice));
                st.Active = false;
                st.ElapsedSec = 0;
                return;
            }
            if (!wantActive) return;

            if (!st.Active)
            {
                st.Active = true;
                st.ElapsedSec = 0;
            }
            st.ElapsedSec += TickPeriodSec;

            double noise = MBoosterEffectSynthesizer.SynthesizeRoadTextureNoise(st.ElapsedSec);
            if (noise < -1) noise = -1; else if (noise > 1) noise = 1;
            short noiseSample = (short)Math.Round(noise * short.MaxValue);
            ushort noiseRaw = unchecked((ushort)noiseSample);
            double effectiveIntensityPct = (effects?.RoadTexture?.IntensityPct ?? 0) * st.RoadTextureRoughness01;
            ushort intensityRaw = MozaMBoosterProtocol.EncodeRoadTextureLevel(effectiveIntensityPct);
            ushort smoothnessRaw = MozaMBoosterProtocol.EncodeRoadTextureLevel(effects?.RoadTexture?.SmoothnessPct ?? 0);

            var frame = MozaMBoosterProtocol.BuildRoadTextureFrame(true, intensityRaw, smoothnessRaw, noiseRaw, _targetDevice);
            SendMotor(frame);
        }

        /// <summary>
        /// Update + process every user-created custom effect for one tick
        /// (Experimental — docs/protocol/devices/mbooster.md "Custom
        /// Effects"). Each effect gets its own <see cref="EffectState"/>,
        /// keyed by <see cref="MBoosterCustomEffect.Id"/> so per-effect phase/
        /// elapsed-time state survives across ticks regardless of list
        /// reordering. States whose effect was deleted from the settings list
        /// are disabled on the wire (if still mid-vibration) and dropped —
        /// same "always send a disable frame on removal" rule every other
        /// effect follows so the last-active waveform can't latch.
        /// </summary>
        private void UpdateAndProcessCustomEffects(IMBoosterEffects? effects)
        {
            var list = effects?.CustomEffects;

            if (_customEffectStates.Count > 0)
            {
                List<string>? stale = null;
                foreach (var kvp in _customEffectStates)
                {
                    bool exists = false;
                    if (list != null)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            if (string.Equals(list[i].Id, kvp.Key, StringComparison.Ordinal)) { exists = true; break; }
                        }
                    }
                    if (!exists) (stale ??= new List<string>()).Add(kvp.Key);
                }
                if (stale != null)
                {
                    foreach (var key in stale)
                    {
                        if (_customEffectStates[key].Active)
                            _device.SendOneShot(MozaMBoosterProtocol.BuildDisableFrame(MBoosterEffectId.Engine, _targetDevice));
                        _customEffectStates.Remove(key);
                        // Also drop a lingering test-toggle flag for a deleted
                        // effect — otherwise a stale UI row's forgotten Test
                        // toggle keeps forcing this id "active" forever
                        // (harmless on its own since UpdateCustomEffectRequest
                        // requires a matching settings-list entry too, but no
                        // reason to keep the entry around).
                        _customEffectTestSustained.TryRemove(key, out _);
                    }
                }
            }

            if (list == null || list.Count == 0) return;
            for (int i = 0; i < list.Count; i++)
            {
                var effect = list[i];
                if (string.IsNullOrEmpty(effect.Id)) continue;
                _customEffectStates.TryGetValue(effect.Id, out var st);
                UpdateCustomEffectRequest(effect, ref st);
                ProcessCustomEffect(ref st);
                _customEffectStates[effect.Id] = st;
            }
        }

        /// <summary>
        /// Compute one custom effect's intensity/frequency request for this
        /// tick. <see cref="MBoosterCustomEffect.Formula"/> is evaluated live
        /// every tick (not cached) via the injected formula evaluator, so
        /// editing the formula text is felt immediately. Two modes:
        /// <see cref="MBoosterCustomEffect.ThresholdEnabled"/> true = pulse
        /// trigger (fixed Intensity while Formula's value clears Threshold,
        /// like Lockup/Threshold); false = continuous proportional (Formula's
        /// value, clamped 0..1, directly scales Intensity, like Engine). The
        /// sustained Test toggle bypasses Enabled/Formula/Threshold entirely
        /// and runs continuously at the live Frequency/Intensity sliders —
        /// same substitution Engine's own test toggle uses (there's no live
        /// signal to preview a user's arbitrary formula against outside
        /// whatever it's actually wired to).
        /// </summary>
        private void UpdateCustomEffectRequest(MBoosterCustomEffect effect, ref EffectState st)
        {
            if (effect == null)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                return;
            }

            double freqHz = effect.FrequencyHz;
            if (freqHz < MBoosterUiConstants.CustomEffectFreqMinHz) freqHz = MBoosterUiConstants.CustomEffectFreqMinHz;
            if (freqHz > MBoosterUiConstants.CustomEffectFreqMaxHz) freqHz = MBoosterUiConstants.CustomEffectFreqMaxHz;
            double scale = Clamp01(effect.IntensityPct / 100.0) * CustomEffectScaleMax;

            if (_customEffectTestSustained.ContainsKey(effect.Id))
            {
                st.IntensityRequest = scale;
                st.FreqHz = freqHz;
                return;
            }

            if (!effect.Enabled || string.IsNullOrWhiteSpace(effect.Formula))
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                return;
            }

            double raw = _customEffectFormulaEvaluator(effect.Formula);
            st.IntensityRequest = effect.ThresholdEnabled
                ? (raw >= effect.Threshold ? scale : 0.0)
                : Clamp01(raw) * scale;
            st.FreqHz = freqHz;
        }

        /// <summary>
        /// Activation-edge + frame-emission path for one custom effect —
        /// mirrors <see cref="ProcessEffect"/>, but since there is no
        /// protocol-verified wire effect type for arbitrary user content,
        /// every custom effect is transmitted using the already-verified
        /// Engine (effect type 4) frame shape/ParamK and Engine's plain sine
        /// waveform (<see cref="MBoosterEffectSynthesizer.SynthesizeEngine"/>).
        /// This means a custom effect competes with the real Engine effect
        /// (and any other simultaneously-active custom effect) for that one
        /// wire slot — see the ordering note at this method's call site in
        /// <see cref="Tick"/>.
        /// </summary>
        private void ProcessCustomEffect(ref EffectState st)
        {
            const MBoosterEffectId id = MBoosterEffectId.Engine;
            bool wantActive = st.IntensityRequest > 0 && st.FreqHz > 0;

            if (!wantActive && st.Active)
            {
                _device.SendOneShot(MozaMBoosterProtocol.BuildDisableFrame(id, _targetDevice));
                st.Active = false;
                st.PhaseRad = 0;
                st.ElapsedSec = 0;
                return;
            }
            if (!wantActive) return;

            if (!st.Active)
            {
                st.Active = true;
                st.PhaseRad = 0;
                st.ElapsedSec = 0;
            }

            st.ElapsedSec += TickPeriodSec;
            st.PhaseRad += 2.0 * Math.PI * st.FreqHz * TickPeriodSec;
            if (st.PhaseRad >= 2.0 * Math.PI)
                st.PhaseRad -= 2.0 * Math.PI * Math.Floor(st.PhaseRad / (2.0 * Math.PI));

            double amp01 = MBoosterEffectSynthesizer.SynthesizeEngine(st.IntensityRequest, st.PhaseRad);

            byte param1 = MozaMBoosterProtocol.ComputeParam1(MozaMBoosterProtocol.ParamKFor(id), st.FreqHz);
            ushort freqU16 = MozaMBoosterProtocol.EncodeFreq(st.FreqHz);
            ushort ampU16 = MozaMBoosterProtocol.EncodeAmp(amp01);

            var frame = MozaMBoosterProtocol.BuildMotorFrame(id, enable: true, param1, freqU16, ampU16, _targetDevice);
            SendMotor(frame);
        }

        // ===== Helpers ====================================================

        /// <summary>
        /// Live brake reading for test pulses. Prefers <c>snap.Brake</c> (the
        /// game-telemetry source SimHub publishes) and rises to the mBooster's
        /// own HID pedal position when its role is Brake — so the user can feel
        /// brake-modulated test pulses even with no game running.
        /// </summary>
        private double EffectiveBrake(MBoosterRole role, in MBoosterTelemetrySnapshot snap)
        {
            double b = Clamp01(snap.Brake);
            if (role == MBoosterRole.Brake)
            {
                double hid = Clamp01(PedalHid());
                if (hid > b) b = hid;
            }
            return b;
        }

        private static double Clamp01(double v)
        {
            if (double.IsNaN(v)) return 0;
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }
    }
}
