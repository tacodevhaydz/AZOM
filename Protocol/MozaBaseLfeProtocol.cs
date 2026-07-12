using System;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Frame builder + value encoders for the wheelbase "low-frequency effects"
    /// (LFE): the complex gearshift vibration, continuous engine vibration, and
    /// ABS effect introduced on recent MOZA base firmware (>= 1.2.10.10). All
    /// three are HOST-RENDERED streams — PitHouse computes freq/intensity each
    /// frame and pushes cmd <c>0x77</c> on group <c>0x2D</c> to the base
    /// (device <c>0x13</c>). Reverse-engineered byte-exact from the four
    /// <c>lfe-*.pcapng</c> captures; see docs/protocol/devices/wheelbase-0x13.md.
    ///
    /// cmd <c>0x77</c> sits one id above the classic <c>base-gearshift-event</c>
    /// (<c>0x76</c>) on the same write-only group. The frequency/intensity
    /// encodings are identical to the mBooster's (<see cref="MozaMBoosterProtocol.EncodeFreq"/>
    /// / <see cref="MozaMBoosterProtocol.EncodeAmp"/>); only the 16-bit period
    /// field differs from the mBooster's byte-clamped <c>param1</c>.
    /// </summary>
    public static class MozaBaseLfeProtocol
    {
        /// <summary>Group 45 (0x2D) — the base's write-only discrete-events group (also carries the classic gearshift event 0x76).</summary>
        public const byte Group = 0x2D;
        /// <summary>LFE effect config/stream command id.</summary>
        public const byte Cmd = 0x77;

        // Period = round(ParamK / freqHz), 16-bit BE. Same ParamK values as the
        // mBooster (engine 1000 / abs 2000) — capture-verified: engine sweep gave
        // period=1000/Hz, abs gave 2000/Hz. Gearshift is event-driven, not a
        // sustained tone, so PitHouse leaves the period at a fixed placeholder.
        public const double ParamKEngine = 1000.0;
        public const double ParamKAbs = 2000.0;
        public const ushort GearshiftPeriod = 0x000F;

        /// <summary>Effect id transmitted at payload byte 1 (p1).</summary>
        public enum LfeEffect : byte
        {
            Gearshift = 0x00,
            Engine = 0x01,
            Abs = 0x02,
        }

        /// <summary>
        /// Period encoding: <c>u16 = clamp(floor(paramK / freqHz), 1, 0xFFFF)</c>,
        /// 16-bit BE (the oscillation period in ms). NOT
        /// <see cref="MozaMBoosterProtocol.ComputeParam1"/> — that clamps to a byte
        /// (1..255), which overflows here (abs @ 5 Hz = 400). A non-positive
        /// frequency yields the gearshift placeholder.
        ///
        /// FLOOR, not round: verified byte-exact against all 100 distinct engine
        /// frequencies in <c>lfe-engine-*.pcapng</c> + <c>lfe-in-game-test.pcapng</c>
        /// (round diverges on ~40% of them). For ABS (K=2000) only 4 capture points
        /// exist; floor matches 5 Hz→400 and 30 Hz→66 exactly, and is ±1 raw on
        /// 15/18 Hz — an imperceptible timing hint on a host-modulated effect.
        /// </summary>
        public static ushort EncodePeriod(double paramK, double freqHz)
        {
            if (double.IsNaN(freqHz) || freqHz <= 0) return GearshiftPeriod;
            double raw = Math.Floor(paramK / freqHz);
            if (raw < 1) return 1;
            if (raw > 0xFFFF) return 0xFFFF;
            return (ushort)raw;
        }

        /// <summary>
        /// Build the 15-byte LFE stream frame. Wire layout:
        /// <pre>
        /// 7e  0a  2d  13   77   p0  p1  p2   p3 p4   p5 p6   p7 p8   CK
        ///                  │    │   │   │    └─┴period └─┴freq  └─┴intensity (all u16 BE)
        ///                  │    │   │   └ play flag (1 = playing, 0 = staged/idle)
        ///                  │    │   └ effect id (0 gearshift / 1 engine / 2 abs)
        ///                  │    └ pad (0x00)
        ///                  └ cmd id (0x77)
        /// </pre>
        /// Length byte 0x0A = cmd(1) + 9 payload (excludes group + device, per the
        /// standard framing rule). Verified frames:
        /// <list type="bullet">
        /// <item>gearshift 100 Hz/100%: <c>7e 0a 2d 13 77 00 00 01 00 0f 80 00 ff ff da</c></item>
        /// <item>engine 100 Hz/50%: <c>7e 0a 2d 13 77 00 01 01 00 0a 80 00 80 00 ..</c></item>
        /// <item>abs 30 Hz/100%: <c>7e 0a 2d 13 77 00 02 01 00 42 26 66 ff ff ..</c></item>
        /// </list>
        /// </summary>
        public static byte[] BuildFrame(
            LfeEffect id,
            bool playing,
            ushort period16,
            ushort freq16,
            ushort int16,
            byte device = MozaProtocol.DeviceBase)
        {
            var f = new byte[15];
            f[0] = MozaProtocol.MessageStart;   // 0x7e
            f[1] = 0x0A;                         // length = cmd + 9 payload
            f[2] = Group;                        // 0x2d
            f[3] = device;                       // 0x13 base
            f[4] = Cmd;                          // 0x77
            f[5] = 0x00;                         // p0 const
            f[6] = (byte)id;                     // p1 effect id
            f[7] = playing ? (byte)1 : (byte)0;  // p2 play flag
            f[8] = (byte)(period16 >> 8);        // p3 period high
            f[9] = (byte)(period16 & 0xFF);      // p4 period low
            f[10] = (byte)(freq16 >> 8);         // p5 freq high
            f[11] = (byte)(freq16 & 0xFF);       // p6 freq low
            f[12] = (byte)(int16 >> 8);          // p7 intensity high
            f[13] = (byte)(int16 & 0xFF);        // p8 intensity low
            f[14] = MozaProtocol.CalculateWireChecksum(f, 14);
            return f;
        }

        /// <summary>
        /// Disable one effect: all-zero payload (capture-verified idle/off frame).
        /// Send exactly one at each active->idle edge so the last waveform can't latch.
        /// </summary>
        public static byte[] BuildDisable(LfeEffect id, byte device = MozaProtocol.DeviceBase)
            => BuildFrame(id, playing: false, period16: 0, freq16: 0, int16: 0, device);
    }
}
