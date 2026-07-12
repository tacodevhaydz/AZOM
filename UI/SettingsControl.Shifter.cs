using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MozaPlugin.Resources;

namespace MozaPlugin
{
    /// <summary>
    /// Shifter settings tab for the passive HGP (H-pattern) and SGP (sequential)
    /// shifters. Both expose reverse-direction + paddle-sync; the SGP additionally
    /// has 2 configurable LEDs (fixed 8-colour palette, index 0-7) + brightness;
    /// the HGP has an H-pattern calibration routine. Config is serial-only — gear
    /// input stays HID-sourced.
    /// </summary>
    public partial class SettingsControl
    {
        // SGP LED palette: wire index 0-7 -> swatch RGB. Names are localized (see
        // EnsureShifterCombos). Matches PitHouse / foxblat (data/style.css .c0-.c7).
        private static readonly (byte R, byte G, byte B)[] ShifterPaletteRgb =
        {
            (0xcf, 0x27, 0x27), // 0 red
            (0xdf, 0xa5, 0x00), // 1 orange
            (0xdf, 0xdf, 0x3a), // 2 yellow
            (0x3a, 0x90, 0x3a), // 3 green
            (0x00, 0xd0, 0xd0), // 4 cyan
            (0x3a, 0x3a, 0xff), // 5 blue
            (0x80, 0x20, 0x80), // 6 purple
            (0xdd, 0xdd, 0xdd), // 7 white
        };
        private bool _shifterCombosBuilt;

        private void RefreshShifterTab()
        {
            bool detected = _plugin.IsShifterDetected;
            ShifterTab.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;
            if (!detected) return;

            EnsureShifterCombos();

            bool hasLeds = _plugin.IsShifterHasLeds;      // SGP has LEDs; HGP does not
            ShifterLedCard.Visibility = hasLeds ? Visibility.Visible : Visibility.Collapsed;
            ShifterCalCard.Visibility = hasLeds ? Visibility.Collapsed : Visibility.Visible;

            using (_suppressor.Begin())
            {
                ShifterDirectionCheck.IsChecked = _data?.ShifterDirection == 1;
                // Paddle-sync wire range is {1,2}: 2 = enabled, 1 = disabled.
                ShifterPaddleSyncCheck.IsChecked = _data?.ShifterPaddleSync == 2;

                if (hasLeds && _data != null)
                {
                    if (_data.ShifterLed1Index >= 0 && _data.ShifterLed1Index < ShifterPaletteRgb.Length)
                        ShifterLed1Combo.SelectedIndex = _data.ShifterLed1Index;
                    if (_data.ShifterLed2Index >= 0 && _data.ShifterLed2Index < ShifterPaletteRgb.Length)
                        ShifterLed2Combo.SelectedIndex = _data.ShifterLed2Index;
                    if (_data.ShifterBrightness >= 0)
                    {
                        ShifterBrightnessSlider.Value = _data.ShifterBrightness;
                        ShifterBrightnessValue.Text = _data.ShifterBrightness.ToString();
                    }
                }
            }
        }

        private void EnsureShifterCombos()
        {
            if (_shifterCombosBuilt) return;
            _shifterCombosBuilt = true;
            var names = new[]
            {
                Strings.ShifterColor_Red, Strings.ShifterColor_Orange, Strings.ShifterColor_Yellow,
                Strings.ShifterColor_Green, Strings.ShifterColor_Cyan, Strings.ShifterColor_Blue,
                Strings.ShifterColor_Purple, Strings.ShifterColor_White,
            };
            PopulateShifterCombo(ShifterLed1Combo, names);
            PopulateShifterCombo(ShifterLed2Combo, names);
        }

        private static void PopulateShifterCombo(ComboBox combo, string[] names)
        {
            combo.Items.Clear();
            for (int i = 0; i < ShifterPaletteRgb.Length; i++)
            {
                var (r, g, b) = ShifterPaletteRgb[i];
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new Rectangle
                {
                    Width = 14,
                    Height = 14,
                    Margin = new Thickness(0, 0, 8, 0),
                    Fill = new SolidColorBrush(Color.FromRgb(r, g, b)),
                    Stroke = Brushes.Gray,
                    StrokeThickness = 0.5,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                sp.Children.Add(new TextBlock { Text = names[i], VerticalAlignment = VerticalAlignment.Center });
                combo.Items.Add(new ComboBoxItem { Content = sp });
            }
        }

        // Handlers follow the handbrake/pedals convention: set _data, write to the
        // device, save. Persistence to the profile is via MozaProfile.CaptureFromCurrent
        // (shifter fields are device-read + only read on connect, so no drift).
        private void ShifterDirectionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int v = ShifterDirectionCheck.IsChecked == true ? 1 : 0;
            if (_data != null) _data.ShifterDirection = v;
            _plugin.WriteIfShifterDetected("shifter-direction", v);
            _plugin.SaveSettings();
        }

        private void ShifterPaddleSyncCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int v = ShifterPaddleSyncCheck.IsChecked == true ? 2 : 1;   // wire range {1,2}
            if (_data != null) _data.ShifterPaddleSync = v;
            _plugin.WriteIfShifterDetected("shifter-paddle-sync", v);
            _plugin.SaveSettings();
        }

        private void ShifterLed1Combo_Changed(object sender, SelectionChangedEventArgs e) => OnShifterColorChanged();
        private void ShifterLed2Combo_Changed(object sender, SelectionChangedEventArgs e) => OnShifterColorChanged();

        private void OnShifterColorChanged()
        {
            if (_suppressEvents) return;
            // Both LEDs ride one 2-byte command [S1,S2], so a change to either
            // re-sends both. If the other combo hasn't been seeded yet (device read
            // still in flight), fall back to its last-known value rather than
            // clobbering that LED with index 0 (red).
            int s1 = ResolveShifterColor(ShifterLed1Combo.SelectedIndex, _data?.ShifterLed1Index ?? -1);
            int s2 = ResolveShifterColor(ShifterLed2Combo.SelectedIndex, _data?.ShifterLed2Index ?? -1);
            if (_data != null) { _data.ShifterLed1Index = s1; _data.ShifterLed2Index = s2; }
            _plugin.WriteArrayIfShifterDetected("shifter-colors", new byte[] { (byte)s1, (byte)s2 });
            _plugin.SaveSettings();
        }

        private static int ResolveShifterColor(int comboIndex, int dataIndex)
        {
            if (comboIndex >= 0) return comboIndex;            // user's current pick
            if (dataIndex >= 0) return dataIndex;              // last device-read value
            return 0;                                          // nothing known yet
        }

        private void ShifterBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(e.NewValue);
            ShifterBrightnessValue.Text = v.ToString();
            if (_data != null) _data.ShifterBrightness = v;
            _plugin.WriteIfShifterDetected("shifter-brightness", v);
            _plugin.SaveSettings();
        }

        private void ShifterCalStartButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin.WriteIfShifterDetected("shifter-cal-start", 1);
            if (ShifterCalStatus != null)
            {
                ShifterCalStatus.Text = Strings.Subtitle_ShifterCalibrate;
                ShifterCalStatus.Visibility = Visibility.Visible;
            }
        }
    }
}
