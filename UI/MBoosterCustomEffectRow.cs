using System;
using System.ComponentModel;
using MozaPlugin.Devices;

namespace MozaPlugin.UI
{
    /// <summary>
    /// UI-layer wrapper around one <see cref="MBoosterCustomEffect"/> for the
    /// mBooster tab's dynamic "Custom Effects (Experimental)" list —
    /// <see cref="MBoosterCustomEffect"/> itself stays a plain POCO (matching
    /// every other mBooster settings type, persisted via Newtonsoft.Json), so
    /// this row supplies the <see cref="INotifyPropertyChanged"/> binding
    /// surface an <c>ItemsControl</c> template needs, plus a callback to
    /// persist on every edit. Mirrors <c>Devices/WheelUi/ChannelMappingRow.cs</c>'s
    /// role for the (unrelated) channel-mapping list.
    /// </summary>
    internal sealed class MBoosterCustomEffectRow : INotifyPropertyChanged
    {
        public MBoosterCustomEffect Model { get; }
        private readonly Action _onChanged;
        private readonly Action<string, bool> _onTestToggle;

        public MBoosterCustomEffectRow(MBoosterCustomEffect model, Action onChanged, Action<string, bool> onTestToggle)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
            _onTestToggle = onTestToggle ?? throw new ArgumentNullException(nameof(onTestToggle));
        }

        // Sustained Test toggle — never persisted (mirrors the five built-in
        // effects' Test toggles), always starts unchecked because a fresh row
        // instance is created every time the list is repopulated. Forwards
        // to the controller/worker rather than the model — there is nothing
        // to save here.
        private bool _testActive;
        public bool TestActive
        {
            get => _testActive;
            set
            {
                if (_testActive == value) return;
                _testActive = value;
                Raise(nameof(TestActive));
                _onTestToggle(Id, value);
            }
        }

        public string Id => Model.Id;

        public string Name
        {
            get => Model.Name;
            set
            {
                var v = value ?? "";
                if (Model.Name == v) return;
                Model.Name = v;
                Raise(nameof(Name));
                _onChanged();
            }
        }

        public bool Enabled
        {
            get => Model.Enabled;
            set
            {
                if (Model.Enabled == value) return;
                Model.Enabled = value;
                Raise(nameof(Enabled));
                _onChanged();
            }
        }

        public string Formula
        {
            get => Model.Formula;
            set
            {
                var v = value ?? "";
                if (Model.Formula == v) return;
                Model.Formula = v;
                Raise(nameof(Formula));
                _onChanged();
            }
        }

        public bool ThresholdEnabled
        {
            get => Model.ThresholdEnabled;
            set
            {
                if (Model.ThresholdEnabled == value) return;
                Model.ThresholdEnabled = value;
                Raise(nameof(ThresholdEnabled));
                _onChanged();
            }
        }

        public double Threshold
        {
            get => Model.Threshold;
            set
            {
                if (Model.Threshold.Equals(value)) return;
                Model.Threshold = value;
                Raise(nameof(Threshold));
                Raise(nameof(ThresholdDisplay));
                _onChanged();
            }
        }

        public double FrequencyHz
        {
            get => Model.FrequencyHz;
            set
            {
                float v = (float)value;
                if (Model.FrequencyHz.Equals(v)) return;
                Model.FrequencyHz = v;
                Raise(nameof(FrequencyHz));
                Raise(nameof(FrequencyDisplay));
                _onChanged();
            }
        }

        public double IntensityPct
        {
            get => Model.IntensityPct;
            set
            {
                int v = (int)Math.Round(value);
                if (Model.IntensityPct == v) return;
                Model.IntensityPct = v;
                Raise(nameof(IntensityPct));
                Raise(nameof(IntensityDisplay));
                _onChanged();
            }
        }

        public string FrequencyDisplay => $"{FrequencyHz:F0} Hz";
        public string IntensityDisplay => $"{IntensityPct:F0}%";
        public string ThresholdDisplay => Threshold.ToString("F1");

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
