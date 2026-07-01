using System;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Per-effect waveform synthesis for the Moza mBooster vibration motor.
    /// All four functions return an amplitude in [0, 1] that the caller scales
    /// to u16 via <see cref="Protocol.MozaMBoosterProtocol.EncodeAmp"/>.
    ///
    /// The motor itself is "dumb" — it plays the instantaneous amplitude we
    /// send. These functions are the host-side waveform generators called
    /// once per motor tick (~50 Hz).
    ///
    /// Reference: <c>docs/MozamBooster — Protocol Note.md</c> § 4
    /// "Waveform synthesizers". Formulas are reproduced verbatim from the
    /// protocol note's dirt-client reference implementation; do not modify
    /// without verifying against the protocol document. Exceptions:
    /// <see cref="SynthesizeAbs"/> and <see cref="SynthesizeThreshold"/> now
    /// take a host-side "smoothness"/"decay" parameter (not from the
    /// protocol note) that generalizes the verified formula — both reduce
    /// to the exact reference at their default value, see each method's doc
    /// comment.
    /// </summary>
    public static class MBoosterEffectSynthesizer
    {
        /// <summary>
        /// ABS — oscillating pulse. <paramref name="smoothness01"/> (0..1,
        /// from <c>MBoosterEffectSettings.SmoothnessPct / 100</c>) is a
        /// host-side extension controlling the ripple depth, NOT part of the
        /// protocol note: at <c>smoothness01 = 1</c> (the default, matching
        /// pre-existing profiles) this reduces to the exact verified
        /// reference formula <c>wave = 0.9 + 0.1 * sin(phase)</c>; at
        /// <c>smoothness01 = 0</c> it widens to <c>wave = 0.5 + 0.5 *
        /// sin(phase)</c> (the same full-swing shape as <see cref="SynthesizeEngine"/>)
        /// for a sharper, choppier pulse. <c>amp = wave * intensity</c>.
        /// </summary>
        public static double SynthesizeAbs(double intensity, double phase, double smoothness01)
        {
            if (double.IsNaN(smoothness01)) smoothness01 = 1.0;
            else if (smoothness01 < 0) smoothness01 = 0;
            else if (smoothness01 > 1) smoothness01 = 1;
            double depth = 0.5 - 0.4 * smoothness01;
            double wave = (1.0 - depth) + depth * Math.Sin(phase);
            return wave * intensity;
        }

        /// <summary>
        /// LOCKUP — linear ramp up over 0.5 s, then hold.
        /// <c>ramp = clamp(elapsed / 0.5, 0, 1); amp = ramp * intensity</c>.
        /// </summary>
        public static double SynthesizeLockup(double intensity, double elapsedSec)
        {
            double ramp = elapsedSec / 0.5;
            if (ramp < 0) ramp = 0;
            if (ramp > 1) ramp = 1;
            return ramp * intensity;
        }

        /// <summary>
        /// THRESHOLD — burst → sustain → off envelope, repeating every 200 ms
        /// (5 Hz). 20 ms burst at full, then 120 ms sustain, then 60 ms gap.
        /// <paramref name="decay01"/> (0..1, from
        /// <c>MBoosterEffectSettings.DecayPct / 100</c>) is a host-side
        /// extension controlling the sustain level, NOT part of the protocol
        /// note: <c>sustain = intensity * (1 - decay01)</c>. At
        /// <c>decay01 = 0.2</c> (the default, matching pre-existing
        /// profiles) this reduces to the exact verified reference sustain
        /// (80 %); at <c>decay01 = 0</c> the sustain barely decays (stays at
        /// full strength for the whole 120 ms); at <c>decay01 = 1</c> it
        /// drops to silence immediately after the burst, for a short, sharp
        /// tick instead of a sustained buzz.
        /// </summary>
        public static double SynthesizeThreshold(double intensity, double elapsedSec, double decay01)
        {
            if (double.IsNaN(decay01)) decay01 = 0.2;
            else if (decay01 < 0) decay01 = 0;
            else if (decay01 > 1) decay01 = 1;
            double cyclePos = elapsedSec - Math.Floor(elapsedSec / 0.2) * 0.2;
            if (cyclePos < 0.02) return intensity;                       // 20 ms burst at full
            if (cyclePos < 0.14) return intensity * (1.0 - decay01);     // 120 ms sustain
            return 0;                                                    // 60 ms gap
        }

        /// <summary>
        /// ENGINE — sine wave at the supplied frequency, biased into [0, 1].
        /// <c>wave = 0.5 + 0.5 * sin(phase); amp = wave * intensity</c>.
        /// </summary>
        public static double SynthesizeEngine(double intensity, double phase)
        {
            double wave = 0.5 + 0.5 * Math.Sin(phase);
            return wave * intensity;
        }

        // ROAD TEXTURE keyframe spacing — chosen to match the ~1.3-1.6
        // peaks/sec oscillation rate measured in a real Pit House capture
        // (docs/protocol/devices/mbooster.md "Effects card UI"). Not a
        // protocol-verified constant, just a "feels similar" match: capture
        // evidence shows Intensity/Smoothness don't affect the noise
        // signal's shape at all (the firmware applies both internally), so
        // there's no real reference to match exactly — any reasonable
        // road-like noise generator satisfies the wire contract.
        private const double RoadTextureKeyframeSec = 0.35;

        /// <summary>
        /// ROAD TEXTURE noise generator — unlike the other three effects,
        /// this doesn't return an amplitude the caller scales by intensity;
        /// it returns a raw signed sample in [-1, 1] that gets sent to the
        /// device UNSCALED (see MozaMBoosterProtocol.BuildRoadTextureFrame).
        /// A real Pit House capture confirmed the firmware — not Pit House
        /// or this plugin — applies Intensity/Smoothness to shape this
        /// signal internally, so this is a host-side stand-in for whatever
        /// reference noise Pit House streams, not a decoded/verified
        /// algorithm. Deterministic value noise: a new pseudo-random target
        /// every <see cref="RoadTextureKeyframeSec"/>, smoothstep-
        /// interpolated between keyframes so the output can't jump
        /// discontinuously.
        /// </summary>
        public static double SynthesizeRoadTextureNoise(double elapsedSec)
        {
            double t = elapsedSec / RoadTextureKeyframeSec;
            double t0 = Math.Floor(t);
            double frac = t - t0;
            double a = KeyframeNoise((long)t0);
            double b = KeyframeNoise((long)t0 + 1);
            double s = frac * frac * (3.0 - 2.0 * frac); // smoothstep
            return a + (b - a) * s;
        }

        /// <summary>Deterministic hash of an integer keyframe index to a pseudo-random value in [-1, 1].</summary>
        private static double KeyframeNoise(long seed)
        {
            unchecked
            {
                ulong h = (ulong)seed * 0x9E3779B97F4A7C15UL;
                h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL;
                h ^= h >> 27; h *= 0x94D049BB133111EBUL;
                h ^= h >> 31;
                return ((h & 0xFFFFFF) / (double)0xFFFFFF) * 2.0 - 1.0;
            }
        }
    }
}
