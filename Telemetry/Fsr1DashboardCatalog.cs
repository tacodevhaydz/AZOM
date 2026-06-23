using System.Collections.Generic;
using System.Linq;

namespace MozaPlugin.Telemetry
{
    /// <summary>Byte encoding of a group-0x42 field. Width fixes the field's
    /// full-scale capability (<see cref="Fsr1FieldDef.CapabilityMax"/>).</summary>
    internal enum Fsr1Encoding { U8, U16_BE, U16_LE, U24_BE }

    /// <summary>How a resolved SimHub value becomes the field's wire integer.</summary>
    internal enum Fsr1FieldKind
    {
        /// <summary>Normalise the source over [InMin,InMax] → [0, FullScale].</summary>
        Scaled,
        /// <summary>Use the source value directly (rounded, clamped to capability).</summary>
        Direct,
        /// <summary>Protocol anchor, not user-mappable: 0x4B when the engine runs, else 0.</summary>
        EngineFlag,
    }

    /// <summary>
    /// One mappable (or anchor) field within an FSR V1 dashboard record. Offsets
    /// are payload-relative (payload[0]=type, [1]=b1, [2]=b2, data from [3]).
    /// </summary>
    internal sealed class Fsr1FieldDef
    {
        public string FieldId = "";
        public string Label = "";
        public int[] Offsets = System.Array.Empty<int>();
        public Fsr1Encoding Encoding = Fsr1Encoding.U8;
        public Fsr1FieldKind Kind = Fsr1FieldKind.Scaled;
        public string DefaultProperty = "";
        public double DefaultInMin = 0;
        public double DefaultInMax = 1;
        /// <summary>Output cap; 0 = use the encoding's full capability.</summary>
        public long FullScale = 0;
        /// <summary>True when the field's semantics are proven; false = raw/experimental slot.</summary>
        public bool Decoded = true;

        /// <summary>Largest value the field's byte width can represent.</summary>
        public long CapabilityMax => Encoding switch
        {
            Fsr1Encoding.U8 => 0xFF,
            Fsr1Encoding.U16_BE => 0xFFFF,
            Fsr1Encoding.U16_LE => 0xFFFF,
            Fsr1Encoding.U24_BE => 0xFFFFFF,
            _ => 0xFF,
        };

        /// <summary>Effective output ceiling (FullScale override or capability).</summary>
        public long OutputMax => FullScale > 0 ? FullScale : CapabilityMax;

        /// <summary>Anchor fields are filled by the protocol, not user-mappable.</summary>
        public bool IsUserMappable => Kind != Fsr1FieldKind.EngineFlag;
    }

    /// <summary>One built-in dashboard = one group-0x42 record type.</summary>
    internal sealed class Fsr1Dashboard
    {
        public byte RecordType;
        public string Key = "";        // settings key, e.g. "type-02"
        public string Label = "";      // UI group header
        public byte PayloadLen;        // wire len byte (type+b1+b2+data)
        public byte LiveB1;            // b1 anchor (per-dashboard config; option-A default)
        public byte LiveB2;            // b2 anchor (per-dashboard config; option-A default)
        public bool IsLive;            // false = declared-only (never streams live)
        public Fsr1FieldDef[] Fields = System.Array.Empty<Fsr1FieldDef>();
    }

    /// <summary>
    /// The fixed catalog of FSR V1 built-in dashboards and their fields — the
    /// single source of truth for "what is on which dashboard". Derived verbatim
    /// from the captures in <c>usb-capture/fsr1/</c> via <c>tools/fsr1-0x42-extract</c>
    /// and <c>tools/fsr1-field-decode</c> (see docs/protocol/devices/wheel-0x17.md
    /// § Group 0x42).
    ///
    /// Field OFFSETS/WIDTHS are proven by per-offset variance analysis; field
    /// SEMANTICS (default mappings) are best-guess for all but the high-confidence
    /// anchors (RPM bar, gear at the last byte, the 0x4B engine flag). Everything is
    /// user-overridable, so precise default semantics are not load-bearing.
    /// Undecoded live bytes are exposed as raw slots (<see cref="Fsr1FieldDef.Decoded"/>
    /// false) so users can experiment / help finish the decode.
    /// </summary>
    internal static class Fsr1DashboardCatalog
    {

        // ===== Auto-generated from usb-capture/fsr1 (tools/.. gencatalog) =====
        public static readonly Fsr1Dashboard[] Dashboards =
        {
            new()
            {
                RecordType = 0x01, Key = "type-01", Label = "Dashboard 01 — dashboard", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g19", Label = "Gauge @19 (16-bit)", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g23", Label = "Gauge @23 (16-bit)", Offsets = new[] { 23, 24 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x02, Key = "type-02", Label = "Dashboard 02 — RPM / gear", IsLive = true,
                PayloadLen = 18, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "b17", Label = "Slot @17 (8-bit)", Offsets = new[] { 17 }, Encoding = Fsr1Encoding.U8, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x03, Key = "type-03", Label = "Dashboard 03 — dashboard", IsLive = true,
                PayloadLen = 19, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x04, Key = "type-04", Label = "Dashboard 04 — dashboard", IsLive = true,
                PayloadLen = 23, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g19", Label = "Gauge @19 (16-bit)", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x05, Key = "type-05", Label = "Dashboard 05 — dashboard", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g19", Label = "Gauge @19 (16-bit)", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g23", Label = "Gauge @23 (16-bit)", Offsets = new[] { 23, 24 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x06, Key = "type-06", Label = "Dashboard 06 — multi-gauge", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x08,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g19", Label = "Gauge @19 (16-bit)", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g23", Label = "Gauge @23 (16-bit)", Offsets = new[] { 23, 24 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x08, Key = "type-08", Label = "Dashboard 08 — dashboard", IsLive = true,
                PayloadLen = 23, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g19", Label = "Gauge @19 (16-bit)", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x09, Key = "type-09", Label = "Dashboard 09 — timing", IsLive = true,
                PayloadLen = 24, LiveB1 = 0x00, LiveB2 = 0x08,
                // Decoded from a probe-validated user profile (lap-time dashboard). Lap times
                // are 3-byte (u24) values — they exceed u16 in ms. See wheel-0x17.md § Group 0x42.
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5",  Label = "Current lap time", Offsets = new[] { 5, 6, 7 },    Encoding = Fsr1Encoding.U24_BE, Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.CurrentLapTime", Decoded = true },
                    new Fsr1FieldDef { FieldId = "g8",  Label = "Last lap time",    Offsets = new[] { 8, 9, 10 },   Encoding = Fsr1Encoding.U24_BE, Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.LastLapTime", Decoded = true },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Best lap time",    Offsets = new[] { 11, 12, 13 }, Encoding = Fsr1Encoding.U24_BE, Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.BestLapTime", Decoded = true },
                    new Fsr1FieldDef { FieldId = "g14", Label = "Gauge @14 (24-bit)", Offsets = new[] { 14, 15, 16 }, Encoding = Fsr1Encoding.U24_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Speed (km/h)",     Offsets = new[] { 17, 18 },     Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.SpeedKmh", Decoded = true },
                    new Fsr1FieldDef { FieldId = "b19", Label = "Position",         Offsets = new[] { 19 },         Encoding = Fsr1Encoding.U8,     Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.Position", Decoded = true },
                    new Fsr1FieldDef { FieldId = "b20", Label = "RPM %",            Offsets = new[] { 20 },         Encoding = Fsr1Encoding.U8,     Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.CarSettings_CurrentDisplayedRPMPercent", Decoded = true },
                    new Fsr1FieldDef { FieldId = "b21", Label = "TC level",         Offsets = new[] { 21 },         Encoding = Fsr1Encoding.U8,     Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.TCLevel", Decoded = true },
                    new Fsr1FieldDef { FieldId = "b22", Label = "ABS level",        Offsets = new[] { 22 },         Encoding = Fsr1Encoding.U8,     Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.ABSLevel", Decoded = true },
                    new Fsr1FieldDef { FieldId = "b23", Label = "Gear",             Offsets = new[] { 23 },         Encoding = Fsr1Encoding.U8,     Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.Gear", Decoded = true },
                },
            },
            new()
            {
                RecordType = 0x0b, Key = "type-0b", Label = "Dashboard 0B — dashboard", IsLive = true,
                PayloadLen = 15, LiveB1 = 0x00, LiveB2 = 0x04,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x0c, Key = "type-0c", Label = "Dashboard 0C — dashboard", IsLive = true,
                PayloadLen = 18, LiveB1 = 0x00, LiveB2 = 0x02,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "b17", Label = "Slot @17 (8-bit)", Offsets = new[] { 17 }, Encoding = Fsr1Encoding.U8, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x0d, Key = "type-0d", Label = "Dashboard 0D — dashboard", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g19", Label = "Gauge @19 (16-bit)", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g23", Label = "Gauge @23 (16-bit)", Offsets = new[] { 23, 24 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x0e, Key = "type-0e", Label = "Dashboard 0E — multi-field", IsLive = true,
                PayloadLen = 24, LiveB1 = 0x0e, LiveB2 = 0x01,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g19", Label = "Gauge @19 (16-bit)", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "b23", Label = "Slot @23 (8-bit)", Offsets = new[] { 23 }, Encoding = Fsr1Encoding.U8, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x11, Key = "type-11", Label = "Dashboard 11 — GT (A)", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x06,
                // GT-style page record A (streamed interleaved with type-12). Decoded from a
                // probe-validated user profile; unmapped slots left raw. See wheel-0x17.md.
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5",  Label = "Gauge @5 (24-bit)",  Offsets = new[] { 5, 6, 7 },   Encoding = Fsr1Encoding.U24_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g8",  Label = "Estimated lap time", Offsets = new[] { 8, 9, 10 },  Encoding = Fsr1Encoding.U24_BE, Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.EstimatedLapTime", Decoded = true },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 },    Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 },    Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "b15", Label = "Slot @15 (8-bit)",   Offsets = new[] { 15 },        Encoding = Fsr1Encoding.U8,     Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g16", Label = "Speed (km/h)",       Offsets = new[] { 16, 17 },    Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.SpeedKmh", Decoded = true },
                    new Fsr1FieldDef { FieldId = "g18", Label = "Fuel — remaining laps", Offsets = new[] { 18, 19 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.FuelLaps", Decoded = true },
                    new Fsr1FieldDef { FieldId = "b20", Label = "Gear",               Offsets = new[] { 20 },        Encoding = Fsr1Encoding.U8,     Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.Gear", Decoded = true },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 },    Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g23", Label = "Gauge @23 (16-bit)", Offsets = new[] { 23, 24 },    Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x12, Key = "type-12", Label = "Dashboard 12 — GT (B)", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x00,
                // GT-style page record B (streamed interleaved with type-11). Decoded from a
                // probe-validated user profile. TC2/light-stage have no generic aggregate
                // channel in Telemetry.json, so ship unmapped. See wheel-0x17.md § Group 0x42.
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5",  Label = "Tyre pressure FL",  Offsets = new[] { 5, 6 },       Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.TyrePressureFrontLeft", Decoded = true },
                    new Fsr1FieldDef { FieldId = "g7",  Label = "Gauge @7 (24-bit)", Offsets = new[] { 7, 8, 9 },    Encoding = Fsr1Encoding.U24_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g10", Label = "Fuel used (l)",     Offsets = new[] { 10, 11 },     Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.FuelUsed", Decoded = true },
                    new Fsr1FieldDef { FieldId = "g12", Label = "Fuel per lap (l)",  Offsets = new[] { 12, 13 },     Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.FuelConsumeLap", Decoded = true },
                    new Fsr1FieldDef { FieldId = "g14", Label = "Fuel level",        Offsets = new[] { 14, 15 },     Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.Fuel", Decoded = true },
                    new Fsr1FieldDef { FieldId = "g16", Label = "Last lap time",     Offsets = new[] { 16, 17, 18 }, Encoding = Fsr1Encoding.U24_BE, Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.LastLapTime", Decoded = true },
                    new Fsr1FieldDef { FieldId = "b19", Label = "Total laps",        Offsets = new[] { 19 },         Encoding = Fsr1Encoding.U8,     Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.TotalLaps", Decoded = true },
                    new Fsr1FieldDef { FieldId = "b20", Label = "TC level",          Offsets = new[] { 20 },         Encoding = Fsr1Encoding.U8,     Kind = Fsr1FieldKind.Direct, DefaultProperty = "DataCorePlugin.GameData.TCLevel", Decoded = true },
                    new Fsr1FieldDef { FieldId = "b21", Label = "Slot @21 (TC2)",    Offsets = new[] { 21 },         Encoding = Fsr1Encoding.U8,     Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "b22", Label = "Slot @22 (8-bit)",  Offsets = new[] { 22 },         Encoding = Fsr1Encoding.U8,     Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "b23", Label = "Slot @23 (lights)", Offsets = new[] { 23 },         Encoding = Fsr1Encoding.U8,     Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "b24", Label = "Slot @24 (8-bit)",  Offsets = new[] { 24 },         Encoding = Fsr1Encoding.U8,     Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
        };

        // Index → record type(s), verified by correlating g32/81 selects + the wheel's
        // Param-6 0x0E log with the streamed 0x42 record type across the usb-capture/fsr1
        // dashboard-change captures (All dashboards / Moza FSR1 dashboard change / FS1
        // multiple changes / GT Style / manual). Index 0 is the power-on default (784
        // streamed type-01 frames before the first switch). Index 16 is never enumerated
        // by PitHouse (its sweep goes …15, 17, 18) → left unmapped (falls back to the
        // full live set). See docs/protocol/devices/wheel-0x17.md § Group 0x42.
        private static readonly System.Collections.Generic.Dictionary<int, byte[]> IndexToRecordTypes = new()
        {
            { 0, new byte[] { 0x01 } },
            { 1, new byte[] { 0x02 } },
            { 2, new byte[] { 0x06 } },
            { 3, new byte[] { 0x06 } },
            { 4, new byte[] { 0x03 } },
            { 5, new byte[] { 0x04 } },
            { 6, new byte[] { 0x04 } },
            { 7, new byte[] { 0x06 } },
            { 8, new byte[] { 0x05 } },
            { 9, new byte[] { 0x03 } },
            { 10, new byte[] { 0x08 } },
            { 11, new byte[] { 0x09 } },
            { 12, new byte[] { 0x0e } },
            { 13, new byte[] { 0x04 } },
            { 14, new byte[] { 0x04 } },
            { 15, new byte[] { 0x0c } },
            { 17, new byte[] { 0x11, 0x12 } },
            { 18, new byte[] { 0x0c } },
        };

        /// <summary>Live dashboards (stream at runtime). Type 02 first (primary).</summary>
        public static readonly Fsr1Dashboard[] LiveDashboards =
            Dashboards.Where(d => d.IsLive).OrderBy(d => d.RecordType == 0x02 ? 0 : 1)
                      .ThenBy(d => d.RecordType).ToArray();

        public static Fsr1Dashboard? ByKey(string key) =>
            Dashboards.FirstOrDefault(d => d.Key == key);

        public static Fsr1Dashboard? ByType(byte type) =>
            Dashboards.FirstOrDefault(d => d.RecordType == type);

        /// <summary>
        /// Active page index (Param 6 / g32-81) -> the record type(s) the wheel renders
        /// on that page (table above). Firmware-fixed: the index->type mapping is
        /// consistent across all captures (only the channel feeding each field is
        /// per-dashboard config). Most pages map to one type; the GT-style page streams
        /// two (0x11 + 0x12). Unmapped indices return empty -> the driver falls back to
        /// the full live set. See docs/protocol/devices/wheel-0x17.md.
        /// </summary>
        public static Fsr1Dashboard[] ByIndex(int index)
        {
            if (!IndexToRecordTypes.TryGetValue(index, out var types))
                return System.Array.Empty<Fsr1Dashboard>();
            var list = new List<Fsr1Dashboard>(types.Length);
            foreach (var t in types) { var d = ByType(t); if (d != null) list.Add(d); }
            return list.ToArray();
        }

        // ── Per-profile override resolution ─────────────────────────────────
        // Turn (catalog default, user override, payload length) into the effective
        // wire layout. The driver, emitter, and UI all go through this so they agree
        // on where a field sits and how it is packed. A null override field means
        // "use the catalog default" (dict-missing ≠ explicit-off).

        /// <summary>Resolve the effective byte span + encoding for one field, applying
        /// any user override on top of the catalog default. Start/end are clamped to the
        /// record's data range <c>[5, payloadLen-1]</c>, width is clamped to 1..3 (the
        /// FSR1's byte-aligned encodings), and endianness only matters for width 2.</summary>
        internal static (int[] offsets, Fsr1Encoding encoding) ResolveLayout(
            Fsr1FieldDef def, Fsr1FieldMapping? m, int payloadLen)
        {
            int defStart = def.Offsets.Length > 0 ? def.Offsets[0] : 5;
            int defEnd = def.Offsets.Length > 0 ? def.Offsets[def.Offsets.Length - 1] : defStart;
            int start = m?.StartOffset ?? defStart;
            int end = m?.EndOffset ?? defEnd;

            int maxOff = payloadLen - 1;
            if (start < 5) start = 5;
            if (start > maxOff) start = maxOff;
            if (end < start) end = start;
            if (end > maxOff) end = maxOff;
            if (end - start > 2) end = start + 2;   // width ≤ 3

            int width = end - start + 1;
            Fsr1Encoding enc = width switch
            {
                1 => Fsr1Encoding.U8,
                2 => (m?.LittleEndian ?? (def.Encoding == Fsr1Encoding.U16_LE))
                        ? Fsr1Encoding.U16_LE : Fsr1Encoding.U16_BE,
                _ => Fsr1Encoding.U24_BE,
            };

            var offsets = new int[width];
            for (int i = 0; i < width; i++) offsets[i] = start + i;
            return (offsets, enc);
        }

        /// <summary>Output ceiling for a resolved encoding (mirrors
        /// <see cref="Fsr1FieldDef.OutputMax"/> but for the overridden width): the
        /// field's <paramref name="fullScale"/> cap if set, else the encoding capability.</summary>
        internal static long OutputMaxFor(Fsr1Encoding enc, long fullScale)
        {
            if (fullScale > 0) return fullScale;
            return enc switch
            {
                Fsr1Encoding.U8 => 0xFF,
                Fsr1Encoding.U16_BE => 0xFFFF,
                Fsr1Encoding.U16_LE => 0xFFFF,
                Fsr1Encoding.U24_BE => 0xFFFFFF,
                _ => 0xFF,
            };
        }

        /// <summary>
        /// The record's fields resolved into a GUARANTEED gapless, non-overlapping partition
        /// of the data range <c>[5, PayloadLen-1]</c> — the single layout source of truth for
        /// the driver, emitter, viz, UI, and probe. Composes catalog + synthetic fields, takes
        /// each field's desired span (<see cref="ResolveLayout"/>, catalog default merged with
        /// the per-profile override), sorts by start, then tiles left-to-right preserving each
        /// field's width where room allows; the last field absorbs any slack to the record end.
        ///
        /// This both enforces the invariant for new edits and AUTO-REPAIRS already-broken
        /// stored configs (gaps/overlaps from earlier builds) at use time — every byte ends up
        /// owned by exactly one field, so the wheel never renders a dead (gap) byte. Field
        /// order and identity are preserved; only spans are reapportioned to close gaps/overlaps.
        /// </summary>
        internal static System.Collections.Generic.IReadOnlyList<(Fsr1FieldDef field, int[] offsets, Fsr1Encoding enc)>
            ResolvePartition(MozaPlugin? plugin, Fsr1Dashboard dash)
        {
            const int dataMin = 5;
            int dataMax = dash.PayloadLen - 1;
            int dataBytes = dataMax - dataMin + 1;
            var empty = System.Array.Empty<(Fsr1FieldDef, int[], Fsr1Encoding)>();
            if (dataBytes <= 0) return empty;

            // Desired width + endianness per composed field (catalog + synthetic splits),
            // ordered by where the field wants to sit.
            var items = new System.Collections.Generic.List<(Fsr1FieldDef f, int start, int width, bool le)>();
            foreach (var f in Fsr1FieldComposer.FieldsFor(plugin, dash))
            {
                var m = plugin?.GetFsr1FieldMapping(dash.Key, f.FieldId);
                var (offs, enc) = ResolveLayout(f, m, dash.PayloadLen);
                items.Add((f, offs[0], offs.Length, enc == Fsr1Encoding.U16_LE));
            }
            items.Sort((a, b) => a.start.CompareTo(b.start));

            // A partition can't have more parts than bytes; drop any overflow (e.g. stale
            // synthetic splits piled onto a since-rebuilt catalog) so the tiling stays valid.
            int n = System.Math.Min(items.Count, dataBytes);
            if (n < items.Count)
                MozaLog.Warn($"[AZOM] FSR1 partition {dash.Key}: {items.Count} fields for {dataBytes} bytes — dropping {items.Count - n}.");
            if (n == 0) return empty;

            // Distribute the data bytes across the fields: each gets its desired width clamped
            // to [minW, maxW], where minW forces a field to grow when the remaining fields
            // can't otherwise reach the end (≤3 each), and maxW makes it shrink to leave ≥1
            // byte for each remaining field. Result tiles [5, dataMax] exactly — no gap/overlap.
            var result = new (Fsr1FieldDef, int[], Fsr1Encoding)[n];
            int cursor = dataMin;
            for (int i = 0; i < n; i++)
            {
                int after = n - 1 - i;
                int bytesLeft = dataMax - cursor + 1;
                int maxW = System.Math.Min(3, bytesLeft - after);
                int minW = System.Math.Max(1, bytesLeft - 3 * after);
                int w = items[i].width;
                if (w < minW) w = minW;
                if (w > maxW) w = maxW;
                if (w < 1) w = 1;  // defensive — only if too few fields to cover the range
                var offsets = new int[w];
                for (int k = 0; k < w; k++) offsets[k] = cursor + k;
                Fsr1Encoding enc = w switch
                {
                    1 => Fsr1Encoding.U8,
                    2 => items[i].le ? Fsr1Encoding.U16_LE : Fsr1Encoding.U16_BE,
                    _ => Fsr1Encoding.U24_BE,
                };
                result[i] = (items[i].f, offsets, enc);
                cursor += w;
            }
            return result;
        }

        /// <summary>Debug self-check: every live record's DEFAULT fields must tile
        /// <c>[5, PayloadLen-1]</c> with no gap/overlap. Logs each violation; returns false if
        /// any. Run once at startup so a future catalog edit that breaks the partition is caught.</summary>
        internal static bool ValidateDefaultPartitions()
        {
            bool ok = true;
            foreach (var dash in LiveDashboards)
            {
                int expect = 5;
                foreach (var f in dash.Fields)
                {
                    int s = f.Offsets.Length > 0 ? f.Offsets[0] : 5;
                    int e = f.Offsets.Length > 0 ? f.Offsets[f.Offsets.Length - 1] : s;
                    if (s != expect)
                    {
                        MozaLog.Warn($"[AZOM] FSR1 catalog {dash.Key}: field {f.FieldId} starts at {s}, expected {expect} (gap/overlap).");
                        ok = false;
                    }
                    expect = e + 1;
                }
                if (expect != dash.PayloadLen)
                {
                    MozaLog.Warn($"[AZOM] FSR1 catalog {dash.Key}: fields end at {expect - 1}, expected {dash.PayloadLen - 1} (trailing gap/overflow).");
                    ok = false;
                }
            }
            return ok;
        }
    }
}
