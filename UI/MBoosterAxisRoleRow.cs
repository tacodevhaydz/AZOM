using System;
using System.ComponentModel;
using MozaPlugin.Devices;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Row view-model for the mBooster tab's per-axis "Pedal Roles" list — one
    /// row per detected pedal axis on a chain lane (a single mBooster can host
    /// up to 3 pedals on one USB connection, exposed as separate HID axes). Lets
    /// the user assign each hosted pedal to a game input (throttle/brake/clutch)
    /// when the default Rx/Ry/Rz order is wrong for their wiring. Writes the
    /// device's <see cref="MBoosterDeviceSettings.AxisRoles"/> via the supplied
    /// callback on change. Mirrors <see cref="MBoosterCustomEffectRow"/>'s role
    /// as an INotifyPropertyChanged wrapper for an ItemsControl template.
    /// </summary>
    internal sealed class MBoosterAxisRoleRow : INotifyPropertyChanged
    {
        private readonly Action<int, MBoosterRole> _onRoleChanged;

        public int AxisIndex { get; }
        public string Label { get; }

        public MBoosterAxisRoleRow(int axisIndex, string label, MBoosterRole role, Action<int, MBoosterRole> onRoleChanged)
        {
            AxisIndex = axisIndex;
            Label = label ?? "";
            _onRoleChanged = onRoleChanged ?? throw new ArgumentNullException(nameof(onRoleChanged));
            _selectedRoleIndex = (int)role;
        }

        private int _selectedRoleIndex;
        // 0=Disabled, 1=Throttle, 2=Brake, 3=Clutch — matches the MBoosterRole
        // enum values AND the ComboBox item order in the DataTemplate, so the
        // selected index maps straight to a role.
        public int SelectedRoleIndex
        {
            get => _selectedRoleIndex;
            set
            {
                if (_selectedRoleIndex == value) return;
                _selectedRoleIndex = value;
                Raise(nameof(SelectedRoleIndex));
                if (value >= 0 && value <= 3)
                    _onRoleChanged(AxisIndex, (MBoosterRole)value);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
