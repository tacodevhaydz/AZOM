using System;
using System.ComponentModel;
using MozaPlugin.Telemetry;
using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon;

namespace MozaPlugin.Devices.WheelUi
{
    /// <summary>
    /// Row backing the per-wheel telemetry channel-mapping list. Holds the
    /// channel metadata (name/url/package level/compression) read from the
    /// wheel's catalog, the user-edited SimHub property/formula mapping (a
    /// SimHub <see cref="ExpressionValue"/> edited via the embedded
    /// FormulaPickerButton), and the FSR1/CM1 field-editor visibility flag.
    /// </summary>
    internal sealed class ChannelMappingRow : INotifyPropertyChanged
    {
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

        // ── Formula editing (embedded SimHub FormulaPickerButton) ───────────
        // The picker binds to Expression (an ExpressionValue) and Engine. On
        // commit the dialog mutates the SAME ExpressionValue in place; we listen
        // for that and serialize it back into SimHubProperty (the persisted form),
        // which fires the per-row persist listener. SimHubProperty stays the
        // source of truth so persistence/back-compat are unchanged.

        /// <summary>Shared SimHub formula engine (set by the row factory). May be
        /// null if engine construction failed; the picker degrades to read-only.</summary>
        public NCalcEngineBase? Engine { get; set; }

        private bool _syncingExpression;
        private ExpressionValue? _expression;
        public ExpressionValue Expression
        {
            get
            {
                if (_expression == null)
                {
                    _expression = MakeExpression(_simHubProperty);
                    _expression.PropertyChanged += OnExpressionChanged;
                }
                return _expression;
            }
        }

        private void OnExpressionChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_syncingExpression || _expression == null) return;
            // The picker sets Expression (always) and Interpreter (on NCalc<->JS).
            if (e.PropertyName != nameof(ExpressionValue.Expression)
                && e.PropertyName != nameof(ExpressionValue.UseJavascript)) return;
            SimHubProperty = SerializeExpression(_expression);
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

        /// <summary>FSR1/CM1 rows carry extra per-field options (boundary/scale/bias)
        /// reached via the row's pencil; plain channels are edited entirely through
        /// the formula picker, so their pencil is hidden.</summary>
        public bool ShowFieldOptions => IsFsr1 || IsCm1;

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

        // ── Inline-editor state ────────────────────────────────────────

        private bool _isEditing;
        /// <summary>
        /// Drives the FSR1/CM1 per-field editor panel's visibility (boundary /
        /// scale / bias steppers). Toggled by the row's pencil via
        /// <see cref="BeginEdit"/> / <see cref="EndEdit"/>. Plain channels have no
        /// extra options, so their pencil is hidden and this stays false — the
        /// formula picker edits them directly.
        /// </summary>
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

        /// <summary>Open the FSR1/CM1 field-options panel.</summary>
        public void BeginEdit() => IsEditing = true;

        /// <summary>Close the field-options panel (OK / Cancel both just collapse —
        /// FSR1/CM1 edits persist live as they are made).</summary>
        public void EndEdit() => IsEditing = false;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
