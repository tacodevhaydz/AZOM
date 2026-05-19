using System;
using System.Collections.Generic;
using System.ComponentModel;
using MozaPlugin.Telemetry;

namespace MozaPlugin.Devices.WheelUi
{
    /// <summary>
    /// Row backing the per-wheel telemetry channel-mapping DataGrid. Holds the
    /// channel metadata (name/url/package level/compression) read from the
    /// wheel's catalog, the user-edited SimHub property mapping, and the
    /// filtered ComboBox autocomplete state.
    /// </summary>
    internal sealed class ChannelMappingRow : INotifyPropertyChanged
    {
        // Min characters before the filter activates. Below this the dropdown
        // stays empty — SimHub's full property list (1500+ entries) can't be
        // virtualized smoothly at 1-2 char matches.
        private const int MinSearchChars = 3;
        // Cap filtered results — protects against substrings like "data"
        // matching half the universe. User narrows further; 200 is enough.
        private const int MaxFilteredResults = 200;

        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public int PackageLevel { get; set; }
        public string Compression { get; set; } = "";

        private string _simHubProperty = "";
        public string SimHubProperty
        {
            get => _simHubProperty;
            set
            {
                var v = (value ?? "").Trim();
                if (_simHubProperty == v) return;
                _simHubProperty = v;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SimHubProperty)));
                // Clear the live value so the next refresh repopulates from the
                // new path and the user doesn't see a stale value matched
                // against an unrelated property name.
                CurrentValueText = "";
                UpdateFilteredProperties();
            }
        }

        /// <summary>
        /// Master snapshot of every SimHub property name (set once by the populator
        /// from <see cref="MozaPlugin.GetAllSimHubPropertyNames"/>). The ComboBox
        /// dropdown does NOT bind to this directly — filter into
        /// <see cref="FilteredProperties"/> on each keystroke.
        /// </summary>
        public IReadOnlyList<string> AllProperties { get; set; } = KnownSimHubProperties.Paths;

        private IReadOnlyList<string> _filteredProperties = Array.Empty<string>();
        /// <summary>
        /// Live filtered subset of <see cref="AllProperties"/>. Bound to
        /// ComboBox.ItemsSource. Swapped wholesale (immutable-list swap) on
        /// each keystroke — Clear+Add would race a mid-click on a dropdown item
        /// and lose the SelectedItem reference.
        /// </summary>
        public IReadOnlyList<string> FilteredProperties
        {
            get => _filteredProperties;
            private set
            {
                _filteredProperties = value ?? Array.Empty<string>();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredProperties)));
            }
        }

        private bool _isDropDownOpen;
        /// <summary>
        /// TwoWay-bound to ComboBox.IsDropDownOpen. Auto-opened by the filter
        /// step on user input when there are matches; closed when the typed
        /// text is an exact property name.
        /// </summary>
        public bool IsDropDownOpen
        {
            get => _isDropDownOpen;
            set
            {
                if (_isDropDownOpen == value) return;
                _isDropDownOpen = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDropDownOpen)));
            }
        }

        /// <summary>
        /// Gate that suppresses auto-open during the initial populate. Set true
        /// by the populator after the row is wired into the grid.
        /// </summary>
        public bool AllowDropdownOpen { get; set; }

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

        private void UpdateFilteredProperties()
        {
            string query = _simHubProperty;
            if (string.IsNullOrEmpty(query) || query.Length < MinSearchChars)
            {
                FilteredProperties = Array.Empty<string>();
                if (AllowDropdownOpen) IsDropDownOpen = false;
                return;
            }

            // Build off-bind then swap. Previous list reference stays alive
            // while any in-flight click on a dropdown item finishes committing.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>(MaxFilteredResults);
            bool exactMatchSeen = false;
            var src = AllProperties;
            if (src != null)
            {
                for (int i = 0; i < src.Count; i++)
                {
                    var p = src[i];
                    if (string.IsNullOrEmpty(p)) continue;
                    if (p.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!seen.Add(p)) continue;
                    list.Add(p);
                    if (string.Equals(p, query, StringComparison.OrdinalIgnoreCase))
                        exactMatchSeen = true;
                    if (list.Count >= MaxFilteredResults) break;
                }
            }

            FilteredProperties = list;

            // Typed text matches a property exactly = user picked / typed full
            // path; close. Otherwise auto-open while filter has hits.
            if (!AllowDropdownOpen) return;
            IsDropDownOpen = !exactMatchSeen && list.Count > 0;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
