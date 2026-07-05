using System;
using System.Collections.Generic;
using System.ComponentModel;
using MozaPlugin.Telemetry;
using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon;

namespace MozaPlugin.Devices.WheelUi
{
    /// <summary>
    /// Row backing the per-wheel telemetry channel-mapping list. Holds the
    /// channel metadata (name/url/package level/compression) read from the
    /// wheel's catalog, the user-edited SimHub property/formula mapping, and the
    /// inline-editor state. Two edit modes share one stored string
    /// (<see cref="SimHubProperty"/>): the <b>simple</b> inline property list
    /// (pencil) and the <b>advanced</b> SimHub formula dialog (ƒₓ button, edits
    /// the bound <see cref="Expression"/>).
    /// </summary>
    internal sealed class ChannelMappingRow : INotifyPropertyChanged
    {
        // Cap simple-editor filtered results — protects against substrings like
        // "data" matching half the property universe. The ListBox virtualizes,
        // but an unfiltered first-keystroke render would still cost layout time.
        private const int MaxFilteredResults = 500;

        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public int PackageLevel { get; set; }
        public string Compression { get; set; } = "";

        // ── FSR V1 dashboard-field rows (group-0x42) ────────────────────
        // Set by ChannelMappingRowFactory.BuildFromFsr1Catalog. When IsFsr1 is
        // true the row maps a fixed dashboard FIELD (RecordKey + FieldId) rather
        // than a tier-def channel URL, and carries a scale (InMin..InMax mapped to
        // the field's full-scale capability shown by CapabilityText).
        public bool IsFsr1 { get; set; }
        /// <summary>True for a CM1 base-bridged dash field (group-0x35). Flat — uses
        /// FieldId only (no RecordKey); the row maps the field to a SimHub property.</summary>
        public bool IsCm1 { get; set; }
        /// <summary>True for a net-new synthetic split field (not in the static catalog).
        /// Such a row offers "Remove split" instead of "Reset field" and always persists a
        /// full mapping with an explicit byte span.</summary>
        public bool IsSynthetic { get; set; }
        public string RecordKey { get; set; } = "";
        public string FieldId { get; set; } = "";
        /// <summary>Human-readable field output capability, e.g. "0–255".</summary>
        public string CapabilityText { get; set; } = "";
        /// <summary>Record payload length (wire len byte) — caps the highest editable data
        /// byte at <c>PayloadLen-1</c>. Set by the FSR1 factory; 0 for non-FSR1 rows.</summary>
        public int PayloadLen { get; set; }

        // ── FSR1 boundary / scale editor state (coupled-divider model) ──────
        // An FSR1 record is a CONTIGUOUS, gapless partition of data bytes [5, PayloadLen-1]
        // (that is how PitHouse packs it — every byte belongs to exactly one field). So the
        // editor treats each field as separated from its neighbours by a shared divider:
        // stepping a boundary moves that one divider, reapportioning a single byte between
        // this field and the adjacent one. PrevField/NextField are wired by the FSR1 factory
        // in field order; null at the record's fixed edges (first field's left edge = 5,
        // last field's right edge = PayloadLen-1, neither of which can move). Span mutation
        // goes through SetSpan (raises UI only); the code-behind owns persistence + probe so
        // the per-property persist listener never double-fires on a coupled move.

        /// <summary>Adjacent field sharing this field's LEFT divider, or null at the record start.</summary>
        public ChannelMappingRow? PrevField { get; set; }
        /// <summary>Adjacent field sharing this field's RIGHT divider, or null at the record end.</summary>
        public ChannelMappingRow? NextField { get; set; }

        /// <summary>Payload-relative first data byte of the field (inclusive).</summary>
        public int Start { get; set; } = 5;
        /// <summary>Payload-relative last data byte of the field (inclusive).</summary>
        public int End { get; set; } = 5;

        private bool _littleEndian;
        /// <summary>Width-2 byte order. Ignored for width 1/3 (U8 / U24-BE are fixed). </summary>
        public bool LittleEndian
        {
            get => _littleEndian;
            set
            {
                if (_littleEndian == value) return;
                _littleEndian = value;
                // Raise LittleEndian itself so the persist listener fires, then the
                // layout-dependent properties (EncodingText etc.) for the UI.
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LittleEndian)));
                RaiseLayoutChanged();
            }
        }

        private double _scale = 1;
        /// <summary>Per-field gain: emitted value = <c>raw·Scale + Bias</c>. 1 = no gain.</summary>
        public double Scale
        {
            get => _scale;
            set
            {
                if (_scale.Equals(value)) return;
                _scale = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scale)));
            }
        }

        private double _bias;
        /// <summary>Per-field offset added after Scale. 0 = none. (FSR1 only; CM1 uses Scale alone.)</summary>
        public double Bias
        {
            get => _bias;
            set
            {
                if (_bias.Equals(value)) return;
                _bias = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bias)));
            }
        }

        /// <summary>Field width in bytes (1..3), derived from the current Start/End span.</summary>
        public int Width => End - Start + 1;

        /// <summary>The resolved encoding label shown next to the steppers, e.g. "U8", "U16 BE".</summary>
        public string EncodingText => Width switch
        {
            1 => "U8",
            2 => _littleEndian ? "U16 LE" : "U16 BE",
            _ => "U24 BE",
        };

        /// <summary>BE/LE toggle only applies to a 2-byte field; hidden otherwise.</summary>
        public bool EndianApplies => Width == 2;

        /// <summary>An FSR1 field ≥ 2 bytes wide can be split into two smaller fields.</summary>
        public bool CanSplit => IsFsr1 && Width >= 2;

        /// <summary>A catalog (non-synthetic) FSR1 field — offers "Reset field"; a synthetic split
        /// offers "Remove split" instead. Fixed per row lifetime (no notify).</summary>
        public bool IsCatalogFsr1 => IsFsr1 && !IsSynthetic;

        // Divider guards. A divider can move only if BOTH neighbours can absorb it: the field
        // gaining a byte stays ≤ 3 wide, the field losing a byte stays ≥ 1 wide. A boundary at
        // a record edge (Prev/Next == null) is fixed and never moves.
        public bool CanStartMinus => PrevField != null && PrevField.Width > 1 && Width < 3;
        public bool CanStartPlus => PrevField != null && Width > 1 && PrevField.Width < 3;
        public bool CanEndMinus => NextField != null && Width > 1 && NextField.Width < 3;
        public bool CanEndPlus => NextField != null && NextField.Width > 1 && Width < 3;

        /// <summary>Merge with the previous/next field into one wider field (≤ 3 bytes) — the
        /// inverse of Split. Absorbs a neighbour the divider steppers can't (e.g. a 1-byte
        /// slot), so two u8 slots can become a u16/u24 where the catalog lacks one.</summary>
        public bool CanMergePrev => PrevField != null && Width + PrevField.Width <= 3;
        public bool CanMergeNext => NextField != null && Width + NextField.Width <= 3;

        /// <summary>Move the LEFT divider one byte toward the record start: this field grows
        /// left, the previous field shrinks from its right. Returns the neighbour that also
        /// changed (so the caller can persist it), or null if the move was not allowed.</summary>
        public ChannelMappingRow? StartMinus()
        {
            if (!CanStartMinus) return null;
            var p = PrevField!;
            p.SetSpan(p.Start, p.End - 1);
            SetSpan(Start - 1, End);
            return p;
        }

        /// <summary>Move the LEFT divider one byte toward the record end: this field shrinks
        /// from its left, the previous field grows right.</summary>
        public ChannelMappingRow? StartPlus()
        {
            if (!CanStartPlus) return null;
            var p = PrevField!;
            p.SetSpan(p.Start, p.End + 1);
            SetSpan(Start + 1, End);
            return p;
        }

        /// <summary>Move the RIGHT divider one byte toward the record start: this field shrinks
        /// from its right, the next field grows left.</summary>
        public ChannelMappingRow? EndMinus()
        {
            if (!CanEndMinus) return null;
            var n = NextField!;
            n.SetSpan(n.Start - 1, n.End);
            SetSpan(Start, End - 1);
            return n;
        }

        /// <summary>Move the RIGHT divider one byte toward the record end: this field grows
        /// right, the next field shrinks from its left.</summary>
        public ChannelMappingRow? EndPlus()
        {
            if (!CanEndPlus) return null;
            var n = NextField!;
            n.SetSpan(n.Start + 1, n.End);
            SetSpan(Start, End + 1);
            return n;
        }

        /// <summary>Set the span and refresh the dependent UI state. Does NOT route through the
        /// Start/End setters, so the per-property persist listener is not triggered — the
        /// code-behind persists both sides of a divider move explicitly.</summary>
        public void SetSpan(int start, int end)
        {
            Start = start;
            End = end;
            RaiseLayoutChanged();
        }

        // ── FSR1 sub-byte / bit-packed editor state (INDEPENDENT steppers) ──────
        // A packed field owns an arbitrary MSB-first bit run [BitOffset, BitOffset+BitWidth) that
        // may share a byte with a neighbour and leave spare bits between fields. Unlike the coupled
        // byte divider, the bit steppers move only THIS field — LowerBitBound/UpperBitBound (set by
        // the factory from the adjacent slots' bit edges) fence it off from its neighbours' bits.
        // BitOffset/BitWidth are non-notifying (like Start/End); SetBitSpan raises UI only, the
        // code-behind owns persistence.

        /// <summary>True when this row edits a bit-packed field (shows the bit-stepper panel).</summary>
        public bool IsBitPacked { get; set; }
        /// <summary>Absolute MSB-first bit offset of the field's MSB over the payload.</summary>
        public int BitOffset { get; set; }
        /// <summary>Field width in bits (1..24).</summary>
        public int BitWidth { get; set; } = 8;
        /// <summary>Lowest bit this field may start at (previous field's bit-end / record start).</summary>
        public int LowerBitBound { get; set; }
        /// <summary>Highest bit-end this field may reach (next field's bit-start / record end).</summary>
        public int UpperBitBound { get; set; }

        /// <summary>Set the bit span and refresh dependent UI (no persist — code-behind owns that).</summary>
        public void SetBitSpan(int bitOffset, int bitWidth)
        {
            BitOffset = bitOffset;
            BitWidth = bitWidth;
            RaiseLayoutChanged();
        }

        /// <summary>Bit-range label shown next to the bit steppers, e.g. "b15.5 +11b".</summary>
        public string BitRangeText => $"b{BitOffset >> 3}.{BitOffset & 7} +{BitWidth}b";

        /// <summary>Total bits the field currently occupies (packed → BitWidth, else Width×8).</summary>
        private int CurrentBits => IsBitPacked ? BitWidth : Width * 8;

        /// <summary>An FSR1 field ≥ 2 bits wide can be bit-split into two sub-byte fields that
        /// share a byte — the shared-byte packing the wheel actually uses for tyre/brake temps.</summary>
        public bool CanBitSplit => IsFsr1 && CurrentBits >= 2;

        /// <summary>Byte-boundary steppers apply to byte-aligned FSR1 fields only. Once a field is
        /// bit-packed its geometry is defined by BitOffset/BitWidth, so the byte steppers are hidden
        /// (they'd show a "moved" byte span and, if used, clobber the bit layout).</summary>
        public bool ShowByteSteppers => IsFsr1 && !IsBitPacked;

        // Independent bit-stepper guards — fenced by Lower/UpperBitBound so a step can never
        // overlap a neighbour's bits (shrinking just leaves spare bits, which is allowed).
        public bool CanBitOffsetMinus => IsBitPacked && BitOffset > LowerBitBound;
        public bool CanBitOffsetPlus => IsBitPacked && BitOffset + BitWidth < UpperBitBound;
        public bool CanBitWidthMinus => IsBitPacked && BitWidth > 1;
        public bool CanBitWidthPlus => IsBitPacked && BitWidth < 24 && BitOffset + BitWidth < UpperBitBound;

        // A span/endianness change can flip the encoding text, the BE/LE visibility, and every
        // stepper guard (a neighbour's guards depend on this field's width) — re-raise the lot.
        // Public so the code-behind can refresh sibling rows after a divider move ripples out.
        public void RaiseLayoutChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Start)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(End)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Width)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncodingText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EndianApplies)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSplit)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStartMinus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStartPlus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEndMinus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEndPlus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMergePrev)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMergeNext)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowByteSteppers)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BitRangeText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanBitSplit)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanBitOffsetMinus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanBitOffsetPlus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanBitWidthMinus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanBitWidthPlus)));
        }

        private double _inMin;
        public double InMin
        {
            get => _inMin;
            set
            {
                if (_inMin.Equals(value)) return;
                _inMin = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InMin)));
            }
        }

        private double _inMax = 1;
        public double InMax
        {
            get => _inMax;
            set
            {
                if (_inMax.Equals(value)) return;
                _inMax = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InMax)));
            }
        }

        private string _simHubProperty = "";
        /// <summary>
        /// The persisted mapping string — a plain SimHub property path
        /// (<c>DataCorePlugin.GameData.Rpms</c>) or a SimHub formula
        /// (<c>[SpeedKmh] * 0.621</c>, or a <c>js:</c> JavaScript expression).
        /// Source of truth; <see cref="Expression"/> is kept in sync both ways.
        /// </summary>
        public string SimHubProperty
        {
            get => _simHubProperty;
            set
            {
                var v = (value ?? "").Trim();
                if (_simHubProperty == v) return;
                _simHubProperty = v;
                // Keep the bound ExpressionValue in sync without re-firing back
                // into us (the FormulaPicker mutates Expression; this is the
                // reverse direction — Reset, repopulate, programmatic set).
                if (_expression != null && !_syncingExpression)
                {
                    _syncingExpression = true;
                    try { ApplyStoredToExpression(_expression, v); }
                    finally { _syncingExpression = false; }
                }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SimHubProperty)));
                // Clear the live value so the next refresh repopulates from the
                // new path/formula and the user doesn't see a stale value matched
                // against an unrelated source.
                CurrentValueText = "";
            }
        }

        // ── Advanced editing (SimHub formula dialog) ───────────────────────
        // The ƒₓ button opens SimHub's BindingEditor against Engine + a working
        // copy of Expression; on OK the code-behind calls ApplyEditedFormula,
        // which serializes the result back into SimHubProperty (the persisted
        // form) and fires the per-row persist listener. SimHubProperty stays the
        // source of truth so persistence/back-compat are unchanged.

        /// <summary>Shared SimHub formula engine (set by the row factory). Null if
        /// engine construction failed; the ƒₓ button is then disabled.</summary>
        public NCalcEngineBase? Engine { get; set; }

        private ExpressionValue? _expression;
        /// <summary>The mapping as a SimHub <see cref="ExpressionValue"/>, kept in
        /// sync with <see cref="SimHubProperty"/>. A bare property path is wrapped
        /// as <c>[path]</c> so the formula dialog opens on valid NCalc.</summary>
        public ExpressionValue Expression
        {
            get
            {
                if (_expression == null) _expression = MakeExpression(_simHubProperty);
                return _expression;
            }
        }

        // Sync direction string -> Expression only; the dialog never mutates the
        // row's Expression directly (it works on a clone), so no reverse listener
        // is needed — the code-behind calls ApplyEditedFormula on OK.
        private bool _syncingExpression;

        /// <summary>Apply a formula chosen in the advanced dialog: set the bound
        /// Expression in place (so the live object the next ƒₓ open reads is
        /// current) and serialize it once into SimHubProperty (firing persistence).
        /// A sole <c>[property]</c> is unwrapped to a bare path; JavaScript is
        /// <c>js:</c>-prefixed.</summary>
        public void ApplyEditedFormula(string? expression, bool useJavascript)
        {
            var ev = Expression;
            _syncingExpression = true;
            try
            {
                ev.UseJavascript = useJavascript;
                ev.Expression = expression ?? "";
            }
            finally { _syncingExpression = false; }
            SimHubProperty = SerializeExpression(ev);
        }

        // Build a fresh ExpressionValue from a stored mapping string.
        private static ExpressionValue MakeExpression(string? stored)
        {
            var ev = new ExpressionValue();
            ApplyStoredToExpression(ev, stored);
            return ev;
        }

        // Mutate an existing ExpressionValue in place to match a stored string
        // (the FormulaPicker holds a reference to this object, so we must not swap
        // it). A bare property path is WRAPPED as [path] so the NCalc editor sees
        // a valid single-property formula — existing mappings persisted before
        // this feature are bare paths and would otherwise be invalid NCalc. A
        // string that already looks like a formula (brackets/operators/js:) is
        // used verbatim. UseJavascript's setter flips the interpreter for us.
        private static void ApplyStoredToExpression(ExpressionValue ev, string? stored)
        {
            var s = (stored ?? "").Trim();
            if (s.Length == 0) { ev.UseJavascript = false; ev.Expression = ""; return; }
            if (s.StartsWith("js:", StringComparison.OrdinalIgnoreCase))
            {
                ev.UseJavascript = true;
                ev.Expression = s.Substring(3);
                return;
            }
            ev.UseJavascript = false;
            ev.Expression = NCalcExpressionEvaluator.LooksLikeExpression(s) ? s : "[" + s + "]";
        }

        // Serialize an ExpressionValue back to the persisted string form. A sole
        // [property] reference is UNWRAPPED to its bare path so existing mappings
        // keep their plain stored form (and the resolver's fast GetPropertyValue
        // path); a real formula ([a]+[b], functions, js:, …) is stored verbatim
        // (js:-prefixed for JavaScript so MakeExpression restores the interpreter).
        private static string SerializeExpression(ExpressionValue ev)
        {
            var expr = (ev.Expression ?? "").Trim();
            if (expr.Length == 0) return "";
            if (ev.UseJavascript) return "js:" + expr;
            if (expr.Length >= 2 && expr[0] == '[' && expr[expr.Length - 1] == ']')
            {
                var inner = expr.Substring(1, expr.Length - 2);
                if (inner.IndexOf('[') < 0 && inner.IndexOf(']') < 0
                    && !NCalcExpressionEvaluator.LooksLikeExpression(inner))
                    return inner;
            }
            return expr;
        }

        // ── Simple editor: property list (pencil) ──────────────────────────

        /// <summary>Master snapshot of every SimHub property name (set once by the
        /// row factory). The inline ListBox binds to <see cref="FilteredProperties"/>,
        /// filtered from this on each <see cref="EditFilter"/> keystroke.</summary>
        public IReadOnlyList<string> AllProperties { get; set; } = KnownSimHubProperties.Paths;

        private IReadOnlyList<string> _filteredProperties = Array.Empty<string>();
        /// <summary>Live filtered subset of <see cref="AllProperties"/>, bound to the
        /// simple editor's ListBox. Immutable-list swap per keystroke.</summary>
        public IReadOnlyList<string> FilteredProperties
        {
            get => _filteredProperties;
            private set
            {
                _filteredProperties = value ?? Array.Empty<string>();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredProperties)));
            }
        }

        private string _currentValueText = "";
        public string CurrentValueText
        {
            get => _currentValueText;
            set
            {
                if (_currentValueText == value) return;
                _currentValueText = value ?? "";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentValueText)));
            }
        }

        // ── Inline simple-editor state ─────────────────────────────────────

        private bool _isEditing;
        /// <summary>Drives the inline simple-editor panel's visibility: a searchable
        /// property list (all rows) plus the FSR1/CM1 boundary/scale/bias steppers.
        /// Toggled by the row's pencil via <see cref="BeginEdit"/> /
        /// <see cref="CommitEdit"/> / <see cref="CancelEdit"/>.</summary>
        public bool IsEditing
        {
            get => _isEditing;
            private set
            {
                if (_isEditing == value) return;
                _isEditing = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
            }
        }

        private string _editFilter = "";
        /// <summary>Filter text inside the simple editor; substring, ordinal-ignore-case,
        /// capped at <c>MaxFilteredResults</c>. Empty = full list.</summary>
        public string EditFilter
        {
            get => _editFilter;
            set
            {
                var v = value ?? "";
                if (_editFilter == v) return;
                _editFilter = v;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditFilter)));
                UpdateFilteredProperties();
            }
        }

        private string _pendingProperty = "";
        /// <summary>ListBox selection inside the simple editor. Applied to
        /// <see cref="SimHubProperty"/> on <see cref="CommitEdit"/>.</summary>
        public string PendingProperty
        {
            get => _pendingProperty;
            set
            {
                var v = value ?? "";
                if (_pendingProperty == v) return;
                _pendingProperty = v;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PendingProperty)));
            }
        }

        /// <summary>Open the simple editor: empty filter (full list) + pending seeded
        /// to the current mapping so the user's choice is highlighted.</summary>
        public void BeginEdit()
        {
            _editFilter = "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditFilter)));
            PendingProperty = SimHubProperty;
            UpdateFilteredProperties();
            IsEditing = true;
        }

        /// <summary>Apply the picked property to <see cref="SimHubProperty"/> (firing
        /// persistence) and collapse the editor.</summary>
        public void CommitEdit()
        {
            SimHubProperty = PendingProperty;
            IsEditing = false;
        }

        /// <summary>Discard the pending selection and collapse the editor.</summary>
        public void CancelEdit()
        {
            PendingProperty = SimHubProperty;
            IsEditing = false;
        }

        private void UpdateFilteredProperties()
        {
            string query = _editFilter;
            var src = AllProperties;
            if (src == null || src.Count == 0)
            {
                FilteredProperties = Array.Empty<string>();
                return;
            }

            bool noFilter = string.IsNullOrEmpty(query);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>(Math.Min(src.Count, MaxFilteredResults));
            for (int i = 0; i < src.Count; i++)
            {
                var p = src[i];
                if (string.IsNullOrEmpty(p)) continue;
                if (!noFilter && p.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!seen.Add(p)) continue;
                list.Add(p);
                if (list.Count >= MaxFilteredResults) break;
            }
            FilteredProperties = list;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
