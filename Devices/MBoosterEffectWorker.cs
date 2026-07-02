using System;
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
        private const double AbsScaleMax       = 0.10;
        private const double LockupScaleMax    = 0.15;
        private const double ThresholdScaleMax = 0.10;
        private const double EngineScaleMax    = 0.10;

        private readonly MBoosterDeviceController _device;
        private readonly Func<MBoosterDeviceSettings?> _settingsLookup;
        private readonly Func<bool> _isShuttingDown;

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

        // Brake Fade — NOT part of the vibration-motor effect pipeline
        // above; a real Travel End (mm) hardware-calibration override, see
        // UpdateBrakeFadeTravelEnd. -1 = we haven't overridden anything
        // (device presumably still holds the user's configured base value).
        private float _brakeFadeAppliedTravelEndMm = -1;
        private long _brakeFadeLastWriteTicks;
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

        // Keepalive tick counter (mod KeepaliveTickInterval).
        private int _keepaliveCounter;

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
            Func<bool> isShuttingDown)
        {
            _device = device;
            _settingsLookup = settingsLookup;
            _isShuttingDown = isShuttingDown;
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
            // Travel End until brake temp is next read as cooled (or the
            // user re-applies their Pedal Feel Travel slider), since there
            // is no watchdog outside this worker's own tick loop.
            TryRestoreBrakeFadeOnStop();
        }

        private void TryRestoreBrakeFadeOnStop()
        {
            if (_brakeFadeAppliedTravelEndMm < 0) return; // never overrode anything
            var settings = _settingsLookup();
            float baseMm = settings?.TravelEndMm ?? -1;
            if (baseMm < 0) return; // no known safe value to restore to
            if (Math.Abs(_brakeFadeAppliedTravelEndMm - baseMm) < 0.01f) return; // already at base
            if (_device.SendIntWrite("mbooster-brake-travel-end", MozaMBoosterProtocol.EncodeTravelMm(baseMm)))
                _brakeFadeAppliedTravelEndMm = baseMm;
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

            var settings = _settingsLookup();

            MBoosterTelemetrySnapshot snap;
            lock (_telemetryLock) snap = _latest;

            // --- Compute per-effect requests from telemetry per doc § 4 -----

            UpdateEngineRequest(settings, snap, ref _engine);
            UpdateAbsRequest(settings, snap, ref _abs);
            UpdateLockupRequest(settings, snap, ref _lockup);
            UpdateThresholdRequest(settings, snap, ref _threshold);
            UpdateRoadTextureRequest(settings, snap, ref _roadTexture);

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
            // Road Texture has a materially different wire payload (see
            // MozaMBoosterProtocol.BuildRoadTextureFrame) so it doesn't go
            // through the shared ProcessEffect/BuildMotorFrame path. Even
            // though it now only streams frames for the duration of a bump's
            // decaying pulse (see UpdateRoadTextureRequest) rather than the
            // whole time the car is moving, it still needs to sit here
            // (ambient tier, before the braking cues) so a lockup/ABS/
            // threshold pulse that lands in the same tick as a bump always
            // wins instead of being masked by it.
            ProcessRoadTextureEffect(settings, ref _roadTexture);
            ProcessEffect(MBoosterEffectId.Abs,       ref _abs);
            ProcessEffect(MBoosterEffectId.Lockup,    ref _lockup);
            ProcessEffect(MBoosterEffectId.Threshold, ref _threshold);

            // Brake Fade is NOT a vibration effect — it doesn't touch the
            // motor stream slot at all, so it's entirely independent of the
            // priority ladder above. See UpdateBrakeFadeTravelEnd.
            UpdateBrakeFadeTravelEnd(settings, snap);

            // --- 500 ms keepalive (separate from motor frames) -------------
            _keepaliveCounter++;
            if (_keepaliveCounter >= KeepaliveTickInterval)
            {
                _keepaliveCounter = 0;
                _device.SendOneShot(MozaMBoosterProtocol.BuildKeepalive());
            }
        }

        // ===== Telemetry → effect parameters (doc § 4) ====================

        private void UpdateEngineRequest(MBoosterDeviceSettings? settings, in MBoosterTelemetrySnapshot snap, ref EffectState st)
        {
            // Engine's frequency used to be derived from RPM (doc § 4:
            // clamp(rpm / 20000 * 200, 10, 200)); it's now a fixed,
            // user-configured value (MBoosterEffectSettings.FrequencyHz,
            // 60-200Hz) — Intensity is still the only thing telemetry (or
            // the test pulse) modulates.
            double freqHz = ClampEngineFreq(settings?.Engine?.FrequencyHz ?? MBoosterUiConstants.EngineFreqMinHz);

            // Sustained test toggle overrides telemetry-driven engine
            // entirely (ignores Enabled/RPM-idle gates, matching how the
            // other effects' test pulses also bypass them) and, unlike a
            // one-shot pulse, re-reads Intensity live every tick so slider
            // drags are felt immediately while testing.
            if (_engineTestSustained)
            {
                double testScale = ((settings?.Engine?.IntensityPct ?? 0) / 100.0) * EngineScaleMax;
                st.IntensityRequest = Clamp01(testScale);
                st.FreqHz = freqHz;
                return;
            }

            if (settings?.Engine == null || !settings.Engine.Enabled)
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
            // Engine continuous-effect: user 0..100 % maps to scale 0..EngineScaleMax.
            double engineScale = (settings.Engine.IntensityPct / 100.0) * EngineScaleMax;
            st.IntensityRequest = Clamp01(engineScale);
        }

        private static double ClampEngineFreq(double hz)
        {
            if (hz < MBoosterUiConstants.EngineFreqMinHz) return MBoosterUiConstants.EngineFreqMinHz;
            if (hz > MBoosterUiConstants.EngineFreqMaxHz) return MBoosterUiConstants.EngineFreqMaxHz;
            return hz;
        }

        private void UpdateAbsRequest(MBoosterDeviceSettings? settings, in MBoosterTelemetrySnapshot snap, ref EffectState st)
        {
            // ABS's frequency used to be derived from ABS-activation depth
            // (doc § 4: 18 + abs01*12, 18-30Hz) — but the plugin's snapshot
            // exposes AbsActive as a bool, not the 0..1 float the doc's
            // pseudocode expects, which collapsed that mapping to a constant
            // 30Hz anyway. It's now a fixed, user-configured value
            // (MBoosterEffectSettings.FrequencyHz, 5-30Hz).
            double freqHz = ClampAbsFreq(settings?.Abs?.FrequencyHz ?? MBoosterUiConstants.AbsFreqMinHz);
            double smoothness01 = Clamp01((settings?.Abs?.SmoothnessPct ?? 100) / 100.0);
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
                double brakeT = EffectiveBrake(settings, snap);
                if (brakeT < 0.6)
                {
                    st.IntensityRequest = 0;
                    st.FreqHz = 0;
                    return;
                }
                double testScale = ((settings?.Abs?.IntensityPct ?? 0) / 100.0) * AbsScaleMax;
                st.IntensityRequest = Clamp01(brakeT * testScale);
                st.FreqHz = freqHz;
                return;
            }

            if (settings?.Abs == null || !settings.Abs.Enabled)
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
            double absScale = (settings.Abs.IntensityPct / 100.0) * AbsScaleMax;
            st.IntensityRequest = Clamp01(abs01 * absScale);
        }

        private static double ClampAbsFreq(double hz)
        {
            if (hz < MBoosterUiConstants.AbsFreqMinHz) return MBoosterUiConstants.AbsFreqMinHz;
            if (hz > MBoosterUiConstants.AbsFreqMaxHz) return MBoosterUiConstants.AbsFreqMaxHz;
            return hz;
        }

        private void UpdateLockupRequest(MBoosterDeviceSettings? settings, in MBoosterTelemetrySnapshot snap, ref EffectState st)
        {
            // Lockup's frequency used to be derived from brake position
            // (doc § 4: 40 + brake*30, 40-70Hz); it's now a fixed,
            // user-configured value (MBoosterEffectSettings.FrequencyHz,
            // 10-100Hz), same treatment as Engine/ABS. The wheel-slip
            // detection gate below (brake + speed + wheel-speed heuristic)
            // is unchanged — only frequency became fixed.
            double freqHz = ClampLockupFreq(settings?.Lockup?.FrequencyHz ?? MBoosterUiConstants.LockupFreqMinHz);

            // Sustained test toggle bypasses the lockup-detection heuristic
            // (which needs vehicle speed) entirely, substituting live brake
            // position for it — same substitution the old 1s test pulse
            // used — just indefinite, and live-tracking Frequency/Intensity
            // every tick instead of snapshotting them.
            if (_lockupTestSustained)
            {
                double brakeT = EffectiveBrake(settings, snap);
                if (brakeT <= 0.01)
                {
                    st.IntensityRequest = 0;
                    st.FreqHz = 0;
                    return;
                }
                double testScale = ((settings?.Lockup?.IntensityPct ?? 0) / 100.0) * LockupScaleMax;
                st.IntensityRequest = Clamp01(brakeT * testScale);
                st.FreqHz = freqHz;
                return;
            }

            if (settings?.Lockup == null || !settings.Lockup.Enabled)
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
            double lockupScale = (settings.Lockup.IntensityPct / 100.0) * LockupScaleMax;
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
        private void UpdateThresholdRequest(MBoosterDeviceSettings? settings, in MBoosterTelemetrySnapshot snap, ref EffectState st)
        {
            double freqHz = ClampThresholdFreq(settings?.Threshold?.FrequencyHz ?? MBoosterUiConstants.ThresholdFreqMinHz);
            double decay01 = Clamp01((settings?.Threshold?.DecayPct ?? 20) / 100.0);
            st.ThresholdDecayRequest01 = decay01;
            double triggerLevel = Clamp01((settings?.Threshold?.TriggerLevelPct ?? MBoosterUiConstants.ThresholdTriggerMinPct) / 100.0);
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
                double brakeT = EffectiveBrake(settings, snap);
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
                double testScale = ((settings?.Threshold?.IntensityPct ?? 0) / 100.0) * ThresholdScaleMax;
                st.IntensityRequest = Clamp01(brakeT * testScale);
                st.FreqHz = freqHz;
                return;
            }

            if (settings?.Threshold == null || !settings.Threshold.Enabled)
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
            double thresholdScale = (settings.Threshold.IntensityPct / 100.0) * ThresholdScaleMax;
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

        private void UpdateRoadTextureRequest(MBoosterDeviceSettings? settings, in MBoosterTelemetrySnapshot snap, ref EffectState st)
        {
            if (_roadTextureTestSustained)
            {
                st.IntensityRequest = 1;
                st.RoadTextureRoughness01 = 1;
                return;
            }
            bool active = settings?.RoadTexture != null && settings.RoadTexture.Enabled
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

        // Brake Fade — NOT a vibration effect. Dynamically rewrites the
        // REAL mbooster-brake-travel-end hardware calibration (the same
        // wire command TravelEndMm's own Pedal Feel slider writes) so the
        // pedal requires more physical travel to reach 100% as brake temp
        // climbs past BrakeFadeOnsetC, ramping linearly up to
        // MBoosterUiConstants.BrakeFadeMaxTravelEndMm over BrakeFadeSpanC
        // degrees, then restoring the user's configured TravelEndMm as temp
        // cools. Requires TravelEndMm >= 0 (the user has actually configured
        // a base value in Pedal Feel) — without a known-safe value to
        // restore to, this stays fully inert rather than guessing, since
        // leaving the device's calibration silently altered would be a much
        // worse outcome than the feature simply not running.
        //
        // Unlike the vibration effects, calibration writes are a real
        // hardware command with no evidence they're safe to stream at 50Hz
        // (see docs/protocol/devices/mbooster.md "Pedal Feel" — every other
        // calibration write in this app only fires when a user drags a
        // slider thumb, not continuously). ApplyBrakeFadeTravelEnd throttles
        // writes to at most once per BrakeFadeWriteMinIntervalSec AND only
        // when the target has moved by at least BrakeFadeWriteMinDeltaMm —
        // except restoring to the exact base value on cooldown/disable,
        // which is a safety action and always goes through immediately.
        private const double BrakeFadeSpanC = 200.0;
        private const double BrakeFadeWriteMinIntervalSec = 0.5;
        private const float BrakeFadeWriteMinDeltaMm = 0.2f;

        private void UpdateBrakeFadeTravelEnd(MBoosterDeviceSettings? settings, in MBoosterTelemetrySnapshot snap)
        {
            float baseMm = settings?.TravelEndMm ?? -1;
            if (baseMm < 0) return; // no known safe base — stay fully inert

            var bf = settings?.BrakeFade;
            float cap = MBoosterUiConstants.BrakeFadeMaxTravelEndMm;

            float targetMm;
            if (_brakeFadeTestActive)
            {
                targetMm = baseMm < cap ? cap : baseMm;
            }
            else if (bf == null || !bf.Enabled)
            {
                targetMm = baseMm;
            }
            else
            {
                double ramp01 = Clamp01((snap.BrakeTempC - bf.BrakeFadeOnsetC) / BrakeFadeSpanC);
                float extendedMm = (float)(baseMm + ramp01 * (cap - baseMm));
                // Never shrink below the user's own base — if baseMm is
                // already >= cap there's no room to extend at all.
                targetMm = extendedMm > baseMm ? Math.Min(extendedMm, cap) : baseMm;
            }

            ApplyBrakeFadeTravelEnd(targetMm, baseMm);
        }

        private void ApplyBrakeFadeTravelEnd(float targetMm, float baseMm)
        {
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
            double sinceLastWriteSec = (Stopwatch.GetTimestamp() - _brakeFadeLastWriteTicks) / (double)Stopwatch.Frequency;

            // Restoring to baseline is a safety action, never throttled away.
            bool shouldWrite = isRestoreToBase
                ? delta > 0.01f
                : delta >= BrakeFadeWriteMinDeltaMm && sinceLastWriteSec >= BrakeFadeWriteMinIntervalSec;
            if (!shouldWrite) return;

            if (!_device.SendIntWrite("mbooster-brake-travel-end", MozaMBoosterProtocol.EncodeTravelMm(targetMm)))
                return; // not connected — nothing written, don't update tracking state

            _brakeFadeAppliedTravelEndMm = targetMm;
            _brakeFadeLastWriteTicks = Stopwatch.GetTimestamp();
        }

        // ===== Edge handling + frame emission =============================

        private void ProcessEffect(MBoosterEffectId id, ref EffectState st)
        {
            bool wantActive = st.IntensityRequest > 0 && st.FreqHz > 0;

            if (!wantActive && st.Active)
            {
                // Deactivation edge: emit one disable frame and go silent.
                _device.SendOneShot(MozaMBoosterProtocol.BuildDisableFrame(id));
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

            var frame = MozaMBoosterProtocol.BuildMotorFrame(id, enable: true, param1, freqU16, ampU16);
            _device.SendMotorStream(frame);
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
        private void ProcessRoadTextureEffect(MBoosterDeviceSettings? settings, ref EffectState st)
        {
            const MBoosterEffectId id = MBoosterEffectId.RoadTexture;
            bool wantActive = st.IntensityRequest > 0;

            if (!wantActive && st.Active)
            {
                _device.SendOneShot(MozaMBoosterProtocol.BuildDisableFrame(id));
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
            double effectiveIntensityPct = (settings?.RoadTexture?.IntensityPct ?? 0) * st.RoadTextureRoughness01;
            ushort intensityRaw = MozaMBoosterProtocol.EncodeRoadTextureLevel(effectiveIntensityPct);
            ushort smoothnessRaw = MozaMBoosterProtocol.EncodeRoadTextureLevel(settings?.RoadTexture?.SmoothnessPct ?? 0);

            var frame = MozaMBoosterProtocol.BuildRoadTextureFrame(true, intensityRaw, smoothnessRaw, noiseRaw);
            _device.SendMotorStream(frame);
        }

        // ===== Helpers ====================================================

        /// <summary>
        /// Live brake reading for test pulses. Prefers <c>snap.Brake</c> (the
        /// game-telemetry source SimHub publishes) and rises to the mBooster's
        /// own HID pedal position when its role is Brake — so the user can feel
        /// brake-modulated test pulses even with no game running.
        /// </summary>
        private double EffectiveBrake(MBoosterDeviceSettings? settings, in MBoosterTelemetrySnapshot snap)
        {
            double b = Clamp01(snap.Brake);
            if (settings?.Role == MBoosterRole.Brake)
            {
                double hid = Clamp01(_device.LastHidPosition);
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
