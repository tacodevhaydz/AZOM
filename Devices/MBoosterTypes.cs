using System;
using System.Collections.Generic;
using System.Linq;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Shared bounds for the Pedal Feel "Start/End of Travel (mm)" range
    /// slider — kept as named constants (rather than duplicated literals in
    /// both this file and the XAML control instance) since a mismatch
    /// between the two would let <see cref="MBoosterDeviceSettings"/>'s
    /// defaults land outside the slider's own allowed range.
    /// </summary>
    public static class MBoosterUiConstants
    {
        public const float TravelMinMm = 3.8f;
        public const float TravelMaxMm = 49.7f;
        public const float TravelMinGapMm = 3.8f;
        public const float TravelMaxGapMm = 32.1f;

        // Engine Vibration's fixed frequency slider bounds — see
        // MBoosterEffectSettings.FrequencyHz and MBoosterEffectWorker.
        public const float EngineFreqMinHz = 60f;
        public const float EngineFreqMaxHz = 200f;

        // ABS's fixed frequency slider bounds — same field, different range.
        public const float AbsFreqMinHz = 5f;
        public const float AbsFreqMaxHz = 30f;

        // Lockup's fixed frequency slider bounds — same field, different range.
        public const float LockupFreqMinHz = 10f;
        public const float LockupFreqMaxHz = 100f;

        // Threshold's fixed frequency slider bounds — same field, different range.
        public const float ThresholdFreqMinHz = 5f;
        public const float ThresholdFreqMaxHz = 100f;

        // Threshold's Trigger Input Level slider bounds. See
        // MBoosterEffectSettings.TriggerLevelPct.
        public const float ThresholdTriggerMinPct = 50f;
        public const float ThresholdTriggerMaxPct = 100f;

        // Brake Fade's Onset Temperature slider bounds, in the same unit
        // BrakeTempC normalizes to (Celsius) — see
        // MBoosterEffectSettings.BrakeFadeOnsetC. Real fade onset varies
        // hugely by pad compound (road pads ~300C, race pads 600C+), so
        // this is user-configurable rather than a hardcoded heuristic like
        // Lockup's brake/speed/wheel-slip gate.
        public const float BrakeFadeOnsetMinC = 300f;
        public const float BrakeFadeOnsetMaxC = 900f;

        // Brake Fade's hard cap on the Travel End (mm) it's allowed to push
        // the pedal to — deliberately BELOW TravelMaxMm (49.7), per explicit
        // user instruction, not derived from any capture/spec. See
        // MBoosterEffectWorker.UpdateBrakeFadeTravelEnd.
        public const float BrakeFadeMaxTravelEndMm = 47.9f;

        // Brake Fade's hard cap on Max Threshold (kg) it's allowed to push
        // the pedal to alongside Travel End — the theoretical full-scale
        // MaxThresholdKg's own wire encoding uses (EncodeThresholdKg's
        // "raw = kg * 65536 / 200" pattern), so this ramps to as much extra
        // force as the device can represent. See MBoosterEffectWorker
        // .UpdateBrakeFadeThreshold.
        public const float BrakeFadeMaxThresholdKg = 200f;

        // Custom (NCalc) effects — user-defined vibration effects driven by
        // an arbitrary SimHub property/formula rather than a fixed protocol
        // telemetry mapping. Wire transport reuses the verified Engine
        // (effect type 4) frame shape/ParamK — see MBoosterEffectWorker
        // .ProcessCustomEffect — so the frequency range matches Engine's.
        public const float CustomEffectFreqMinHz = 5f;
        public const float CustomEffectFreqMaxHz = 200f;
    }

    /// <summary>
    /// User-assigned role for a single mBooster's analog pedal axis. The HID
    /// reader routes the device's position into <c>MozaData.{Throttle,Brake,Clutch}Position</c>
    /// based on this setting. <see cref="Disabled"/> means the device's position
    /// is ignored entirely (motor effects can still fire).
    /// </summary>
    public enum MBoosterRole
    {
        Disabled = 0,
        Throttle = 1,
        Brake    = 2,
        Clutch   = 3,
    }

    /// <summary>
    /// Per-effect knobs the user can tweak. Originally frequency was computed
    /// at runtime per protocol note § 4 telemetry pseudocode with only
    /// enable + intensity surfaced in the UI — Engine, ABS, Lockup, and
    /// Threshold have since all been rebuilt with a fixed, user-set
    /// Frequency slider instead (see <see cref="FrequencyHz"/>). Each
    /// effect's user 0..100 % Intensity maps to scale 0..ScaleMax at apply
    /// time (protocol note § 4 suggested defaults: ABS / Threshold = 0.10,
    /// Lockup = 0.15, Engine = 0.10 — engine runs continuously and would
    /// dominate the others without this cap). The caps live on
    /// <c>MBoosterEffectWorker</c>.
    /// </summary>
    public sealed class MBoosterEffectSettings
    {
        public bool Enabled { get; set; } = false;
        public int IntensityPct { get; set; } = 50; // 0..100

        // Fixed vibration frequency, in Hz. Consumed by Engine (60-200Hz),
        // ABS (5-30Hz), Lockup (10-100Hz), and Threshold (5-100Hz) — see
        // MBoosterUiConstants for each effect's *FreqMinHz/MaxHz bounds.
        // All four used to derive their frequency from telemetry (Engine:
        // RPM; ABS: activation depth; Lockup/Threshold: brake position);
        // all four mappings were replaced with this user-set fixed value as
        // each effect was rebuilt.
        public float FrequencyHz { get; set; } = 100;

        // Pulse modulation depth, ABS-only for now, 0..100. Controls the
        // depth of the sine ripple in MBoosterEffectSynthesizer.SynthesizeAbs:
        // 100 (default) reproduces the exact original verified formula
        // (0.9 + 0.1*sin, depth 0.1 — "do not modify without verifying
        // against the protocol document"); 0 widens it to a full 0..1 swing
        // (0.5 + 0.5*sin, matching Engine's shape) for a sharper, choppier
        // pulse. Values between are a host-side interpolation, not from the
        // protocol note. Default 100 preserves pre-existing behavior for
        // profiles that predate this slider.
        public int SmoothnessPct { get; set; } = 100;

        // Threshold-only, 50..100 (MBoosterUiConstants.ThresholdTriggerMinPct/
        // MaxPct) — the brake position (%) at which the rising-edge trigger
        // fires. The falling/release threshold stays a fixed 30 points
        // below this (same hysteresis gap the original fixed 0.6/0.3
        // thresholds had) rather than being independently configurable.
        // Default 60 exactly reproduces the original verified trigger
        // point. See MBoosterEffectWorker.UpdateThresholdRequest.
        public int TriggerLevelPct { get; set; } = 60;

        // Threshold-only, 0..100 — how much the pulse fades after its
        // initial burst. Generalizes MBoosterEffectSynthesizer
        // .SynthesizeThreshold's fixed "20ms full + 120ms @ 80% + 60ms gap"
        // envelope: sustain level = 1 - decay/100, so 20 (default) exactly
        // reproduces the original verified 80% sustain; 0 sustains at full
        // strength for the whole 120ms (barely decays), 100 drops to
        // silence immediately after the burst (a short, sharp tick).
        public int DecayPct { get; set; } = 20;

        // Brake Fade-only, degrees Celsius (MBoosterUiConstants
        // .BrakeFadeOnsetMinC/MaxC) — the brake temperature above which the
        // pedal's Travel End starts extending (a real hardware calibration
        // write, not a vibration — see MBoosterDeviceSettings.TravelEndMm
        // and MBoosterEffectWorker.UpdateBrakeFadeTravelEnd). Compared
        // against MBoosterTelemetrySnapshot.BrakeTempC (normalized to
        // Celsius regardless of the game's reported TemperatureUnit).
        // Unlike Lockup's hardcoded wheel-slip heuristic, this is
        // user-configurable because real fade onset varies hugely by pad
        // compound and game.
        public float BrakeFadeOnsetC { get; set; } = 550f;

        public MBoosterEffectSettings Clone() =>
            new MBoosterEffectSettings
            {
                Enabled = Enabled,
                IntensityPct = IntensityPct,
                FrequencyHz = FrequencyHz,
                SmoothnessPct = SmoothnessPct,
                TriggerLevelPct = TriggerLevelPct,
                DecayPct = DecayPct,
                BrakeFadeOnsetC = BrakeFadeOnsetC,
            };
    }

    /// <summary>
    /// One user-created, formula-driven vibration effect (Experimental —
    /// see docs/protocol/devices/mbooster.md "Custom Effects"). Unlike the
    /// five built-in effects, there is no protocol-verified wire effect
    /// type for arbitrary user content, so the worker transmits these using
    /// the already-verified Engine (effect type 4) frame shape — see
    /// MBoosterEffectWorker.ProcessCustomEffect. <see cref="Formula"/> is
    /// evaluated once per tick via the same SimHub NCalc engine the
    /// telemetry channel-mapper uses (Telemetry/NCalcExpressionEvaluator.cs)
    /// — either a bare SimHub property path (e.g. <c>SpeedKmh</c>) or a full
    /// <c>[prop]</c> NCalc formula.
    /// </summary>
    public sealed class MBoosterCustomEffect
    {
        // Stable identity for the worker's per-effect synthesis state and
        // for matching a UI row back to its model after a list edit —
        // independent of list order/Name so renaming or reordering never
        // loses in-progress vibration state.
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "Custom Effect";
        public bool Enabled { get; set; } = false;

        // A SimHub property path or NCalc formula — see
        // Telemetry/NCalcExpressionEvaluator.LooksLikeExpression.
        public string Formula { get; set; } = "";

        // Optional trigger gate: when true, the effect vibrates at a fixed
        // Intensity whenever Formula's value is >= Threshold (a pulse-style
        // trigger, like the built-in Threshold/Lockup effects). When false,
        // Formula's value (clamped 0..1) directly scales Intensity every
        // tick instead (a continuous, proportional effect, like Engine).
        // The user's formula is responsible for producing a sensible range
        // for whichever mode they pick.
        public bool ThresholdEnabled { get; set; } = false;
        public double Threshold { get; set; } = 50;

        public float FrequencyHz { get; set; } = 50;
        public int IntensityPct { get; set; } = 50; // 0..100

        public MBoosterCustomEffect Clone() =>
            new MBoosterCustomEffect
            {
                Id = Id,
                Name = Name,
                Enabled = Enabled,
                Formula = Formula,
                ThresholdEnabled = ThresholdEnabled,
                Threshold = Threshold,
                FrequencyHz = FrequencyHz,
                IntensityPct = IntensityPct,
            };
    }

    /// <summary>
    /// The per-pedal VIBRATION-motor effect settings the effect worker reads.
    /// Both <see cref="MBoosterDeviceSettings"/> (the master pedal / device 0x12)
    /// and <see cref="MBoosterPedalSettings"/> (each chained pedal at 0x1d/0x1e)
    /// implement this, so ONE effect worker per motor device can run the same
    /// effect code against whichever pedal it drives — configured effects are
    /// sent to the device id the pedal belongs to. Brake Fade is deliberately
    /// NOT here: it rewrites the brake's hardware calibration rather than driving
    /// a motor, so it stays per-lane on the primary worker.
    /// </summary>
    public interface IMBoosterEffects
    {
        MBoosterEffectSettings Abs { get; set; }
        MBoosterEffectSettings Lockup { get; set; }
        MBoosterEffectSettings Threshold { get; set; }
        MBoosterEffectSettings Engine { get; set; }
        MBoosterEffectSettings RoadTexture { get; set; }
        System.Collections.Generic.List<MBoosterCustomEffect> CustomEffects { get; set; }
    }

    /// <summary>
    /// ALL per-pedal config the settings UI edits — effects (via
    /// <see cref="IMBoosterEffects"/>) PLUS Calibration, Sim Input Mapping, and
    /// Pedal Feel. Both <see cref="MBoosterDeviceSettings"/> (the master pedal /
    /// device 0x12, using its flat fields) and <see cref="MBoosterPedalSettings"/>
    /// (each chained pedal) implement this, so the settings tab's one per-pedal
    /// selector can point every config section at whichever pedal is chosen.
    /// (Brake Fade stays on the device settings — it's per-lane, master only.)
    /// </summary>
    public interface IMBoosterPedalConfig : IMBoosterEffects
    {
        // Calibration
        int Direction { get; set; }
        int Min { get; set; }
        int Max { get; set; }
        float[]? CurveY { get; set; }
        float[]? CurveX { get; set; }
        // Sim Input Mapping
        float SensorOutputRatioPct { get; set; }
        float MaxThresholdKg { get; set; }
        // Pedal Feel
        float[]? InputCurveY { get; set; }
        float DeadzoneKg { get; set; }
        float MaxForceKg { get; set; }
        float TravelStartMm { get; set; }
        float TravelEndMm { get; set; }
        float EndstopFrontStiffness { get; set; }
        float EndstopEndStiffness { get; set; }
    }

    /// <summary>
    /// Settings for ONE hosted pedal on a chained mBooster lane: its output
    /// calibration (Direction / Min / Max / curve, written to that pedal's
    /// role-specific group-35/36 commands) AND its vibration effects (sent to
    /// the pedal's own motor device id 0x1d/0x1e — see IMBoosterEffects). Pedal 0
    /// (the master) keeps both in the flat fields on
    /// <see cref="MBoosterDeviceSettings"/> for UI backward-compat; the chained
    /// pedals store theirs here, keyed by axis index in
    /// <see cref="MBoosterDeviceSettings.Pedals"/>. -1 / null = "not set".
    /// </summary>
    public sealed class MBoosterPedalSettings : IMBoosterPedalConfig
    {
        // Calibration (same sentinels as the master's flat fields).
        public int Direction { get; set; } = -1;
        public int Min { get; set; } = -1;
        public int Max { get; set; } = -1;
        public float[]? CurveY { get; set; } = null;   // 5-point output curve
        public float[]? CurveX { get; set; } = null;   // draggable node X (null = fixed breakpoints)

        // Sim Input Mapping (see MBoosterDeviceSettings for the field semantics).
        public float SensorOutputRatioPct { get; set; } = -1;
        public float MaxThresholdKg { get; set; } = -1;

        // Pedal Feel (host-side shaping + brake-only wire calibration).
        public float[]? InputCurveY { get; set; } = null;
        public float DeadzoneKg { get; set; } = 0;
        public float MaxForceKg { get; set; } = 200;
        public float TravelStartMm { get; set; } = -1;
        public float TravelEndMm { get; set; } = -1;
        public float EndstopFrontStiffness { get; set; } = -1;
        public float EndstopEndStiffness { get; set; } = -1;

        // Per-pedal vibration effects (same defaults as the master's flat fields).
        public MBoosterEffectSettings Abs { get; set; } = new MBoosterEffectSettings { FrequencyHz = 22 };
        public MBoosterEffectSettings Lockup { get; set; } = new MBoosterEffectSettings { FrequencyHz = 55 };
        public MBoosterEffectSettings Threshold { get; set; } = new MBoosterEffectSettings { FrequencyHz = 70, TriggerLevelPct = 60, DecayPct = 20 };
        public MBoosterEffectSettings Engine { get; set; } = new MBoosterEffectSettings { IntensityPct = 50, FrequencyHz = 100 };
        public MBoosterEffectSettings RoadTexture { get; set; } = new MBoosterEffectSettings { IntensityPct = 50, SmoothnessPct = 50 };
        public List<MBoosterCustomEffect> CustomEffects { get; set; } = new List<MBoosterCustomEffect>();

        public MBoosterPedalSettings Clone() =>
            new MBoosterPedalSettings
            {
                Direction = Direction,
                Min = Min,
                Max = Max,
                CurveY = CurveY == null ? null : (float[])CurveY.Clone(),
                CurveX = CurveX == null ? null : (float[])CurveX.Clone(),
                SensorOutputRatioPct = SensorOutputRatioPct,
                MaxThresholdKg = MaxThresholdKg,
                InputCurveY = InputCurveY == null ? null : (float[])InputCurveY.Clone(),
                DeadzoneKg = DeadzoneKg,
                MaxForceKg = MaxForceKg,
                TravelStartMm = TravelStartMm,
                TravelEndMm = TravelEndMm,
                EndstopFrontStiffness = EndstopFrontStiffness,
                EndstopEndStiffness = EndstopEndStiffness,
                Abs = Abs?.Clone() ?? new MBoosterEffectSettings(),
                Lockup = Lockup?.Clone() ?? new MBoosterEffectSettings(),
                Threshold = Threshold?.Clone() ?? new MBoosterEffectSettings(),
                Engine = Engine?.Clone() ?? new MBoosterEffectSettings(),
                RoadTexture = RoadTexture?.Clone() ?? new MBoosterEffectSettings(),
                CustomEffects = CustomEffects?.Select(c => c.Clone()).ToList() ?? new List<MBoosterCustomEffect>(),
            };
    }

    /// <summary>
    /// All settings for ONE mBooster device, keyed by stable identity (USB
    /// instance ID) in the profile's per-device dictionary. Each effect has
    /// its own enable + intensity. Calibration values (group 35/36 — marked
    /// "likely but unverified" in the protocol note) are stored separately.
    /// </summary>
    public sealed class MBoosterDeviceSettings : IMBoosterPedalConfig
    {
        public MBoosterRole Role { get; set; } = MBoosterRole.Disabled;

        // Per-axis role for a multi-pedal chain: the mBooster hosts up to 3
        // pedals on ONE lane, reported as HID axes in a deterministic order
        // (axis 0 = the master unit, axis 1 = 2nd chained device, axis 2 =
        // 3rd). null = use defaults — a single-axis device falls back to the
        // legacy Role above (exact backward compat); a multi-axis chain
        // defaults to [Brake, Throttle, Clutch] by axis order (a guess, since
        // the physical axis→pedal wiring isn't reported — the UI lets the user
        // remap). When the user edits any axis the UI writes the full array so
        // every axis becomes explicit. See MozaMBoosterRegistry.ResolveAxisRole.
        public MBoosterRole[]? AxisRoles { get; set; } = null;

        // FrequencyHz defaults to 22 — the exact value from the "known-good"
        // real Pit House capture (docs/protocol/devices/mbooster.md: "ABS on,
        // 22Hz, amp=0x08e8").
        public MBoosterEffectSettings Abs       { get; set; } = new MBoosterEffectSettings { FrequencyHz = 22 };
        // FrequencyHz defaults to 55 — the exact value from the "known-good"
        // real Pit House capture (docs/protocol/devices/mbooster.md:
        // "Lockup on, 55 Hz, start of ramp").
        public MBoosterEffectSettings Lockup    { get; set; } = new MBoosterEffectSettings { FrequencyHz = 55 };
        // FrequencyHz defaults to 70 — the exact value from the
        // "known-good" ComputeParam1 reference (docs/protocol/devices/
        // mbooster.md: "Threshold @ 70 Hz -> 44"). TriggerLevelPct/DecayPct
        // default to 60/20, exactly reproducing the original verified
        // 0.6-brake trigger and 80% sustain.
        public MBoosterEffectSettings Threshold { get; set; } = new MBoosterEffectSettings { FrequencyHz = 70, TriggerLevelPct = 60, DecayPct = 20 };
        public MBoosterEffectSettings Engine    { get; set; } = new MBoosterEffectSettings { IntensityPct = 50, FrequencyHz = 100 };

        // Road Texture (effect type 9) reuses IntensityPct and SmoothnessPct
        // — both are sent to the device as raw percentages (see
        // MozaMBoosterProtocol.EncodeRoadTextureLevel); it has no
        // FrequencyHz of its own, unlike Abs/Engine.
        public MBoosterEffectSettings RoadTexture { get; set; } = new MBoosterEffectSettings { IntensityPct = 50, SmoothnessPct = 50 };

        // Brake Fade — NOT a vibration effect (no motor-frame wire type
        // involved at all). While enabled and BrakeFadeOnsetC is exceeded,
        // MBoosterEffectWorker ramps TWO real hardware calibrations in
        // lockstep, each independently gated on already having a known base
        // value (>= 0) to restore to:
        // - UpdateBrakeFadeTravelEnd rewrites mbooster-brake-travel-end
        //   (TravelEndMm's own wire command) so the pedal needs more
        //   physical travel to reach 100%, capped at
        //   MBoosterUiConstants.BrakeFadeMaxTravelEndMm.
        // - UpdateBrakeFadeThreshold rewrites mbooster-brake-threshold
        //   (MaxThresholdKg's own wire command) so the pedal needs more
        //   load-cell force to reach 100%, capped at
        //   MBoosterUiConstants.BrakeFadeMaxThresholdKg — this is what
        //   actually makes the pedal feel "softer" (more effort needed for
        //   the same signal), unlike the host-side-only MaxForceKg, which
        //   has no wire command and wouldn't affect what the game receives.
        // Both restore to their configured values as brake temp cools. If
        // the user has never configured a given base value, that ONE
        // calibration stays fully inert (the other can still ramp
        // independently). Disabled by default given it writes real
        // hardware calibration, not merely a vibration amplitude.
        public MBoosterEffectSettings BrakeFade { get; set; } = new MBoosterEffectSettings { BrakeFadeOnsetC = 550 };

        // User-created NCalc-driven vibration effects (Experimental). See
        // MBoosterCustomEffect and MBoosterEffectWorker.ProcessCustomEffect.
        public List<MBoosterCustomEffect> CustomEffects { get; set; } = new List<MBoosterCustomEffect>();

        // Calibration (experimental per protocol note § 6). -1 = "not yet
        // read / no override"; the worker treats -1 as "do not write".
        // The user has been warned in-UI that these commands may not be
        // acknowledged by mBooster firmware.
        public int Direction { get; set; } = -1;
        public int Min { get; set; } = -1;
        public int Max { get; set; } = -1;
        public float[]? CurveY { get; set; } = null;   // 5-point output curve

        // X position (0..100) of each output-curve node, draggable in the
        // Sim Input Mapping curve editor. Null = default fixed breakpoints
        // (20/40/60/80/100 — identical to every other curve in the app).
        // There is no hardware command for this (unlike the wheelbase's own
        // FFB curve, which has base-ffb-curve-x1..x4) — moving a node here
        // instead RESAMPLES the (CurveX, CurveY) shape at the fixed
        // 20/40/60/80/100 breakpoints and pushes those 5 values through the
        // existing mbooster-throttle-y1..y5 commands, so "100% output before
        // 100% input" works using only the wire commands that actually
        // exist. See MozaMBoosterRegistry.EvaluateCurveArbitraryX and
        // docs/protocol/devices/mbooster.md "Sim Input Mapping".
        public float[]? CurveX { get; set; } = null;

        // Per-pedal calibration for the ADDITIONAL pedals on a chained mBooster
        // (axes 1+), keyed by HID axis index. Axis 0 (the master) keeps its
        // calibration in the flat Direction/Min/Max/CurveY/CurveX fields above
        // (unchanged for the existing UI). Absent key = that pedal uses no
        // calibration override. See MozaPlugin.ApplyMBoosterToHardware, which
        // writes each pedal's calibration to its role-specific command.
        public Dictionary<int, MBoosterPedalSettings> Pedals { get; set; }
            = new Dictionary<int, MBoosterPedalSettings>();

        // Sim Input Mapping (Pit House-style). -1 = "not yet set / no
        // override" — mirrors the Direction/Min/Max sentinel convention.
        // Blend between the mBooster's angle sensor (0%) and its load cell
        // (100%) — wire command mbooster-brake-angle-ratio (cmdId 26), the
        // same command the wheelbase's own Brake tab "Sensor Ratio" slider
        // uses via pedals-brake-angle-ratio.
        public float SensorOutputRatioPct { get; set; } = -1;

        // Pit House's "max threshold" — the load cell force (kg) at which
        // output reaches 100%. Reverse-engineered from a real capture (see
        // docs/protocol/devices/mbooster.md "Sim Input Mapping"): wire
        // command mbooster-brake-threshold (cmdId 0xB3) is a 4-byte
        // big-endian uint, NOT a float — raw = round(kg * 65535 / 200).
        // -1 = "not yet set / no override", same sentinel convention as
        // Direction/Min/Max, so a fresh profile never overwrites whatever
        // value is already on the device.
        public float MaxThresholdKg { get; set; } = -1;

        // Pedal Feel (Pit House-style). Host-side only — there is no wire
        // command for this; it shapes the raw HID axis position BEFORE it
        // becomes MozaData.{Throttle,Brake,Clutch}Position (and before the
        // effect worker's brake-position fallback), independent of CurveY
        // (which still writes to the device's own output-curve command
        // unchanged). Null = identity / no shaping — existing profiles are
        // unaffected until the user opens the new Pedal Feel section. See
        // MozaMBoosterRegistry.EvaluateInputCurve and
        // docs/protocol/devices/mbooster.md "Pedal Feel".
        public float[]? InputCurveY { get; set; } = null;

        // Deadzone at the start of pedal travel, in kg of force (0..40).
        // Host-side only, applied before InputCurveY (a physical/sensor
        // characteristic — the resting force before the load cell means
        // anything — should shape the signal before the user's "feel"
        // curve does). See MozaMBoosterRegistry.ApplyDeadzoneAndMaxForce.
        // 0 = off (default).
        public float DeadzoneKg { get; set; } = 0;

        // Force (kg, 0..200) at which the Pedal Feel input curve's X-axis
        // reaches 100%. Host-side only. Raw 0-100% pedal travel isn't a
        // fixed 0-200kg scale — 100% raw is whatever MaxThresholdKg (Sim
        // Input Mapping) currently calibrates the device itself to reach
        // 100% at (200kg is only a fallback guess when MaxThresholdKg is
        // still -1/unset — see MozaMBoosterRegistry.ApplyDeadzoneAndMaxForce).
        // 200 = off IF the device's real threshold is also 200kg; if it's
        // lower (real Pit House captures commonly show ~100-125kg), 200
        // has no additional effect beyond whatever the device already
        // saturates at, since there's no headroom above the device's own
        // calibrated max for software to require more force. Lower it if
        // you never press hard enough to reach the curve's right edge
        // otherwise.
        public float MaxForceKg { get; set; } = 200;

        // Start/End of pedal travel, in mm (Pit House's own calibration
        // control, not a host-side shim). Reverse-engineered from two real
        // Pit House USB captures (see docs/protocol/devices/mbooster.md
        // "Pedal Feel"): wire commands mbooster-brake-travel-start (cmdId
        // 0x84) and mbooster-brake-travel-end (cmdId 0x85), group 35 read /
        // 36 write, 2-byte int each — same shape as Min/Max. Encodes mm on
        // a fixed 0-53.5mm scale over the 0-65535 range (mirroring
        // MaxThresholdKg's "value * 65536 / fullscale" pattern): see
        // MozaMBoosterProtocol.EncodeTravelMm/DecodeTravelMm.
        // -1 = "not yet set / no override", same sentinel convention as
        // Direction/Min/Max/MaxThresholdKg, so a fresh profile never
        // overwrites whatever calibration is already on the device.
        public float TravelStartMm { get; set; } = -1;
        public float TravelEndMm { get; set; } = -1;

        // End Stop Stiffness (Front Limit / End Limit), 1-10 — Pit House's
        // own hardware calibration, reverse-engineered from two real Pit
        // House USB captures (see docs/protocol/devices/mbooster.md "Pedal
        // Feel"): both share wire command cmdId 0xB2 with a selector byte
        // (mbooster-brake-endstop-front / -end), 2-byte int, encoding on a
        // fixed 1-10 scale over 0-65535: see
        // MozaMBoosterProtocol.EncodeEndstopStiffness/DecodeEndstopStiffness.
        // -1 = "not yet set / no override", same sentinel convention as
        // TravelStartMm/TravelEndMm.
        public float EndstopFrontStiffness { get; set; } = -1;
        public float EndstopEndStiffness { get; set; } = -1;

        // Friendly display label the user can edit (defaults to "mBooster"
        // with a serial-tail fallback). Survives reconnects with the dict key.
        public string DisplayName { get; set; } = "";

        public MBoosterDeviceSettings Clone()
        {
            return new MBoosterDeviceSettings
            {
                Role = Role,
                AxisRoles = AxisRoles == null ? null : (MBoosterRole[])AxisRoles.Clone(),
                Pedals = Pedals == null
                    ? new Dictionary<int, MBoosterPedalSettings>()
                    : Pedals.ToDictionary(kv => kv.Key, kv => kv.Value?.Clone() ?? new MBoosterPedalSettings()),
                Abs = Abs?.Clone() ?? new MBoosterEffectSettings(),
                Lockup = Lockup?.Clone() ?? new MBoosterEffectSettings(),
                Threshold = Threshold?.Clone() ?? new MBoosterEffectSettings(),
                Engine = Engine?.Clone() ?? new MBoosterEffectSettings(),
                RoadTexture = RoadTexture?.Clone() ?? new MBoosterEffectSettings(),
                BrakeFade = BrakeFade?.Clone() ?? new MBoosterEffectSettings(),
                CustomEffects = CustomEffects?.Select(c => c.Clone()).ToList() ?? new List<MBoosterCustomEffect>(),
                Direction = Direction,
                Min = Min,
                Max = Max,
                CurveY = CurveY == null ? null : (float[])CurveY.Clone(),
                CurveX = CurveX == null ? null : (float[])CurveX.Clone(),
                SensorOutputRatioPct = SensorOutputRatioPct,
                MaxThresholdKg = MaxThresholdKg,
                InputCurveY = InputCurveY == null ? null : (float[])InputCurveY.Clone(),
                DeadzoneKg = DeadzoneKg,
                MaxForceKg = MaxForceKg,
                TravelStartMm = TravelStartMm,
                TravelEndMm = TravelEndMm,
                EndstopFrontStiffness = EndstopFrontStiffness,
                EndstopEndStiffness = EndstopEndStiffness,
                DisplayName = DisplayName,
            };
        }
    }

    /// <summary>
    /// Subset of game telemetry the mBooster effect worker reads each tick.
    /// Populated from SimHub's data in <c>MozaPlugin.DataUpdate</c> and
    /// published via <c>MozaMBoosterRegistry.OnDataUpdate</c>.
    /// All speed fields are in m/s for consistency (the protocol note's
    /// "unit gotcha" warns about mixing m/s and km/h — we normalise here).
    /// </summary>
    public readonly struct MBoosterTelemetrySnapshot
    {
        public readonly bool   GameRunning;
        public readonly double Rpm;
        public readonly double IdleRpm;
        public readonly double Brake;        // 0..1
        public readonly bool   AbsActive;
        public readonly double VehicleSpeedMs;
        public readonly double AvgWheelSpeedMs;
        // Vertical chassis acceleration, in G — SimHub's StatusDataBase.
        // AccelerationHeave (nullable; 0 when a game doesn't report it).
        // There's no generic suspension-travel telemetry in SimHub at all
        // (StatusDataBase has no Suspension*/Damper*/RideHeight fields);
        // this is a proxy for road-surface roughness used by Road Texture.
        // See docs/protocol/devices/mbooster.md "Road Texture".
        public readonly double SuspensionHeaveG;
        // Peak brake temperature across all 4 corners, normalized to
        // Celsius regardless of the game's reported TemperatureUnit —
        // sourced from StatusDataBase.BrakesTemperatureMax (nullable; 0
        // when a game doesn't report it). Used by Brake Fade. Unlike
        // SuspensionHeaveG this is a real, direct measurement (not a
        // proxy) when the game populates it — but per-corner brake temp is
        // less universally supported across SimHub's game plugins than
        // basics like Brake/SpeedKmh, so 0 (unpopulated) is a real
        // possibility for some titles. See docs/protocol/devices/
        // mbooster.md "Brake Fade".
        public readonly double BrakeTempC;

        public MBoosterTelemetrySnapshot(
            bool gameRunning, double rpm, double idleRpm, double brake, bool absActive,
            double vehicleSpeedMs, double avgWheelSpeedMs, double suspensionHeaveG,
            double brakeTempC)
        {
            GameRunning = gameRunning;
            Rpm = rpm;
            IdleRpm = idleRpm;
            Brake = brake;
            AbsActive = absActive;
            VehicleSpeedMs = vehicleSpeedMs;
            AvgWheelSpeedMs = avgWheelSpeedMs;
            SuspensionHeaveG = suspensionHeaveG;
            BrakeTempC = brakeTempC;
        }

        public static readonly MBoosterTelemetrySnapshot Empty =
            new MBoosterTelemetrySnapshot(false, 0, 800, 0, false, 0, 0, 0, 0);
    }
}
