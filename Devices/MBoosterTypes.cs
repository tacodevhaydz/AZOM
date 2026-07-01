using System;

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
        public const float TravelMinGapMm = 4f;
        public const float TravelMaxGapMm = 32.5f;
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
    /// Per-effect knobs the user can tweak. Frequency is computed at runtime
    /// per protocol note § 4 telemetry pseudocode — only enable + intensity
    /// are surfaced in the UI. Each effect's user 0..100 % maps to scale
    /// 0..ScaleMax at apply time (protocol note § 4 suggested defaults:
    /// ABS / Threshold = 0.10, Lockup = 0.15, Engine = 0.10 — engine runs
    /// continuously and would dominate the others without this cap). The
    /// caps live on <c>MBoosterEffectWorker</c>.
    /// </summary>
    public sealed class MBoosterEffectSettings
    {
        public bool Enabled { get; set; } = false;
        public int IntensityPct { get; set; } = 50; // 0..100

        public MBoosterEffectSettings Clone() =>
            new MBoosterEffectSettings { Enabled = Enabled, IntensityPct = IntensityPct };
    }

    /// <summary>
    /// All settings for ONE mBooster device, keyed by stable identity (USB
    /// instance ID) in the profile's per-device dictionary. Each effect has
    /// its own enable + intensity. Calibration values (group 35/36 — marked
    /// "likely but unverified" in the protocol note) are stored separately.
    /// </summary>
    public sealed class MBoosterDeviceSettings
    {
        public MBoosterRole Role { get; set; } = MBoosterRole.Disabled;

        public MBoosterEffectSettings Abs       { get; set; } = new MBoosterEffectSettings();
        public MBoosterEffectSettings Lockup    { get; set; } = new MBoosterEffectSettings();
        public MBoosterEffectSettings Threshold { get; set; } = new MBoosterEffectSettings();
        public MBoosterEffectSettings Engine    { get; set; } = new MBoosterEffectSettings { IntensityPct = 50 };

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
        // reaches 100%. Host-side only. Both this and DeadzoneKg treat raw
        // 0-100% pedal travel as 0-200kg (the raw HID axis has no
        // independent force calibration of its own — same assumption
        // MaxThresholdKg makes). 200 = off (default; the full theoretical
        // range is used, i.e. today's behavior). Lower it if you never
        // press hard enough to reach the curve's right edge otherwise.
        public float MaxForceKg { get; set; } = 200;

        // Start/End of pedal travel, in mm (Pit House-style). Host-side
        // only — treats raw 0-100% pedal travel as spanning the full
        // [MBoosterUiConstants.TravelMinMm, TravelMaxMm] physical range
        // (same "no independent calibration" assumption as the kg-based
        // controls above), clips outside [TravelStartMm, TravelEndMm], and
        // rescales what's left back to 0-100% — same shape as
        // Deadzone/MaxForce, applied first since physical travel bounds
        // are a more fundamental sensor characteristic than force. See
        // MozaMBoosterRegistry.ApplyTravelRangeMm. Defaults anchor at the
        // slider's minimum with the maximum allowed span
        // (MBoosterUiConstants.TravelMinMm + TravelMaxGapMm) so a fresh
        // profile starts with the widest usable window instead of the
        // narrowest.
        public float TravelStartMm { get; set; } = MBoosterUiConstants.TravelMinMm;
        public float TravelEndMm { get; set; } = MBoosterUiConstants.TravelMinMm + MBoosterUiConstants.TravelMaxGapMm;

        // Friendly display label the user can edit (defaults to "mBooster"
        // with a serial-tail fallback). Survives reconnects with the dict key.
        public string DisplayName { get; set; } = "";

        public MBoosterDeviceSettings Clone()
        {
            return new MBoosterDeviceSettings
            {
                Role = Role,
                Abs = Abs?.Clone() ?? new MBoosterEffectSettings(),
                Lockup = Lockup?.Clone() ?? new MBoosterEffectSettings(),
                Threshold = Threshold?.Clone() ?? new MBoosterEffectSettings(),
                Engine = Engine?.Clone() ?? new MBoosterEffectSettings(),
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

        public MBoosterTelemetrySnapshot(
            bool gameRunning, double rpm, double idleRpm, double brake, bool absActive,
            double vehicleSpeedMs, double avgWheelSpeedMs)
        {
            GameRunning = gameRunning;
            Rpm = rpm;
            IdleRpm = idleRpm;
            Brake = brake;
            AbsActive = absActive;
            VehicleSpeedMs = vehicleSpeedMs;
            AvgWheelSpeedMs = avgWheelSpeedMs;
        }

        public static readonly MBoosterTelemetrySnapshot Empty =
            new MBoosterTelemetrySnapshot(false, 0, 800, 0, false, 0, 0);
    }
}
