using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using MozaPlugin.Devices.StalksTruckSim;

namespace MozaPlugin
{
    /// <summary>
    /// One row in the Stalks truck-sim button-map editor: a stalk button and its
    /// assigned action. A single non-editable combo per row picks the assignment
    /// from a flat option list (encoded as strings) — simple, robust binding.
    /// </summary>
    internal sealed class StalkRow : INotifyPropertyChanged
    {
        public const string NoneOption = "(none)";
        private const string KeyPrefix = "Key: ";
        private const string WiperPrefix = "Wiper stage ";
        private const string LightPrefix = "Light stage ";
        private const string IndLeft = "Indicator: left";
        private const string IndRight = "Indicator: right";
        private const string IndCancel = "Indicator: cancel";
        private const string WiperSwipe = "Wiper: single swipe";

        // ETS2/ATS-relevant keys first, then a general set for custom binds.
        private static readonly string[] PresetKeys =
        {
            "Comma", "Period", "F", "K", "J", "L", "O", "H", "P", "Minus",
            "G", "I", "N", "B", "E", "R", "T", "Y", "U",
            "1", "2", "3", "4", "5", "6", "7", "8", "9", "0",
        };

        private readonly Action<StalkRow> _onChanged;

        public StalkRow(int buttonIndex, StalkAction action, IReadOnlyList<string> options, Action<StalkRow> onChanged)
        {
            ButtonIndex = buttonIndex;
            Options = options;
            _onChanged = onChanged;
            _selected = OptionForAction(action);
        }

        public int ButtonIndex { get; }
        public string Label => "Btn " + (ButtonIndex + 1);

        /// <summary>The shared option list (same instance for every row).</summary>
        public IReadOnlyList<string> Options { get; }

        private string _selected = NoneOption;
        public string Selected
        {
            get => _selected;
            set
            {
                var v = value ?? NoneOption;
                if (_selected == v) return;
                _selected = v;
                OnPropertyChanged(nameof(Selected));
                try { _onChanged?.Invoke(this); } catch { }
            }
        }

        private bool _isPressed;
        public bool IsPressed
        {
            get => _isPressed;
            set { if (_isPressed != value) { _isPressed = value; OnPropertyChanged(nameof(IsPressed)); } }
        }

        /// <summary>The <see cref="StalkAction"/> for this row's current selection,
        /// or null when unassigned.</summary>
        public StalkAction ToAction() => ParseAction(_selected);

        // ---- option encoding (plain technical strings; not localized) ----

        public static List<string> BuildOptions(int wiperStageCount, int lightStageCount)
        {
            var list = new List<string> { NoneOption, IndLeft, IndRight, IndCancel, WiperSwipe };
            foreach (var k in PresetKeys) list.Add(KeyPrefix + k);
            for (int i = 0; i < Math.Max(1, wiperStageCount); i++) list.Add(WiperPrefix + i);
            for (int i = 0; i < Math.Max(1, lightStageCount); i++) list.Add(LightPrefix + i);
            return list;
        }

        public static StalkAction ParseAction(string option)
        {
            if (string.IsNullOrEmpty(option) || option == NoneOption)
                return new StalkAction { Kind = StalkActionKind.None };
            if (option == IndLeft) return new StalkAction { Kind = StalkActionKind.IndicatorLeft };
            if (option == IndRight) return new StalkAction { Kind = StalkActionKind.IndicatorRight };
            if (option == IndCancel) return new StalkAction { Kind = StalkActionKind.IndicatorCancel };
            if (option == WiperSwipe) return new StalkAction { Kind = StalkActionKind.WiperSingleSwipe };
            if (option.StartsWith(KeyPrefix, StringComparison.Ordinal))
                return new StalkAction { Kind = StalkActionKind.Momentary, Key = option.Substring(KeyPrefix.Length) };
            if (option.StartsWith(WiperPrefix, StringComparison.Ordinal))
                return new StalkAction { Kind = StalkActionKind.WiperStage, Stage = ParseInt(option.Substring(WiperPrefix.Length)) };
            if (option.StartsWith(LightPrefix, StringComparison.Ordinal))
                return new StalkAction { Kind = StalkActionKind.LightStage, Stage = ParseInt(option.Substring(LightPrefix.Length)) };
            return new StalkAction { Kind = StalkActionKind.None };
        }

        public static string OptionForAction(StalkAction a)
        {
            if (a == null) return NoneOption;
            switch (a.Kind)
            {
                case StalkActionKind.Momentary: return KeyPrefix + (a.Key ?? "");
                case StalkActionKind.WiperStage: return WiperPrefix + a.Stage;
                case StalkActionKind.LightStage: return LightPrefix + a.Stage;
                case StalkActionKind.IndicatorLeft: return IndLeft;
                case StalkActionKind.IndicatorRight: return IndRight;
                case StalkActionKind.IndicatorCancel: return IndCancel;
                case StalkActionKind.WiperSingleSwipe: return WiperSwipe;
                default: return NoneOption;
            }
        }

        private static int ParseInt(string s)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
