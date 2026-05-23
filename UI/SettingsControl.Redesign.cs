using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using MozaPlugin.UI;
using SerialTrafficCapture = MozaPlugin.Diagnostics.SerialTrafficCapture;

namespace MozaPlugin
{
    // Partial-class continuation of SettingsControl that holds wiring for the
    // 2026-05 redesign (new top bar, status bar, SectionCard-wrapped sections,
    // SteeringArc / TempCell live header, MozaCurveEditor (5-node curves and
    // 6-band EQ), bandwidth sparklines, Full Diagnostic Report expander).
    // Lives in a separate file to keep the existing SettingsControl.xaml.cs
    // untouched.
    public partial class SettingsControl
    {
        // ---- Bandwidth sparkline state (60 samples = 30s @ 500ms tick) ----
        private const int BandwidthSamples = 60;
        private readonly ObservableCollection<double> _bwInSamples = new ObservableCollection<double>();
        private readonly ObservableCollection<double> _bwOutSamples = new ObservableCollection<double>();
        private DispatcherTimer? _bandwidthTimer;
        private long _bwLastInBytes;
        private long _bwLastOutBytes;
        private DateTime _bwLastTick = DateTime.MinValue;
        private long _bwPeakIn;
        private long _bwPeakOut;
        private long _bwSessionIn;
        private long _bwSessionOut;

        // ---- Temperature-graph sample buffers. Same window length as the
        // bandwidth sparkline so the two share the timescale on screen. The
        // main refresh timer (RefreshBaseTab → UpdateRedesignLiveDisplays)
        // pushes samples at the same 500-ms cadence. ----
        private const int TemperatureSamples = 60;
        private readonly ObservableCollection<double> _mcuTempSamples = new ObservableCollection<double>();
        private readonly ObservableCollection<double> _mosfetTempSamples = new ObservableCollection<double>();
        private readonly ObservableCollection<double> _motorTempSamples = new ObservableCollection<double>();

        /// <summary>
        /// Called from the existing constructor after InitializeComponent runs.
        /// Wires the new controls' bindings + initial values. Safe to invoke
        /// even if a subset of the new controls aren't present (FindName guard).
        /// </summary>
        private void InitRedesignControls()
        {
            try
            {
                // Two-way bindings: CurveEditor.YN ↔ underlying slider.Value
                BindEditorToSliders(FfbCurveEditor, new[]
                {
                    FfbCurveY1Slider, FfbCurveY2Slider, FfbCurveY3Slider,
                    FfbCurveY4Slider, FfbCurveY5Slider
                });
                BindEditorToSliders(HandbrakeCurveEditor, new[]
                {
                    HbY1Slider, HbY2Slider, HbY3Slider, HbY4Slider, HbY5Slider
                });
                BindEditorToSliders(ThrottleCurveEditor, new[]
                {
                    ThrottleY1Slider, ThrottleY2Slider, ThrottleY3Slider,
                    ThrottleY4Slider, ThrottleY5Slider
                });
                BindEditorToSliders(BrakeCurveEditor, new[]
                {
                    BrakeY1Slider, BrakeY2Slider, BrakeY3Slider,
                    BrakeY4Slider, BrakeY5Slider
                });
                BindEditorToSliders(ClutchCurveEditor, new[]
                {
                    ClutchY1Slider, ClutchY2Slider, ClutchY3Slider,
                    ClutchY4Slider, ClutchY5Slider
                });

                // Two-way bindings: CurveEditor.YN ↔ EqNSlider.Value (FFB EQ
                // now uses the same line-graph control as the output curves,
                // configured for 6 nodes / 0-400 range via MozaEqualizerLineStyle).
                BindEditorToSliders(FfbEqualizer, new[]
                {
                    Eq1Slider, Eq2Slider, Eq3Slider, Eq4Slider, Eq5Slider, Eq6Slider
                });

                // Bandwidth sparkline data sources
                BandwidthInSparkline.Samples = _bwInSamples;
                BandwidthOutSparkline.Samples = _bwOutSamples;
                for (int i = 0; i < BandwidthSamples; i++)
                {
                    _bwInSamples.Add(0);
                    _bwOutSamples.Add(0);
                }

                // Temperature graph: prime the three rolling buffers + bind.
                if (TemperatureGraphViz != null)
                {
                    TemperatureGraphViz.McuSamples    = _mcuTempSamples;
                    TemperatureGraphViz.MosfetSamples = _mosfetTempSamples;
                    TemperatureGraphViz.MotorSamples  = _motorTempSamples;
                }
                for (int i = 0; i < TemperatureSamples; i++)
                {
                    _mcuTempSamples.Add(0);
                    _mosfetTempSamples.Add(0);
                    _motorTempSamples.Add(0);
                }

                _bandwidthTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _bandwidthTimer.Tick += OnBandwidthTick;
                _bandwidthTimer.Start();

                // Wire the custom hue picker so every PaletteStrip's CUSTOM chip
                // opens the existing ColorPickerDialog. Set globally (static) but
                // each plugin init resets it — that's fine since the factory is
                // pure (no closures over instance state).
                MozaControls.PaletteStrip.CustomPickerFactory = (current) =>
                {
                    var dlg = new ColorPickerDialog(current.R, current.G, current.B)
                    {
                        Owner = System.Windows.Application.Current?.MainWindow,
                    };
                    if (dlg.ShowDialog() == true)
                        return System.Windows.Media.Color.FromRgb(dlg.SelectedR, dlg.SelectedG, dlg.SelectedB);
                    return null;
                };

                // Status bar removed — its content was placeholder strings.

                // About-tab version line — same source as the diagnostics dump.
                if (AboutVersionText != null)
                    AboutVersionText.Text = "v" + DiagnosticsTextBuilder.GetPluginVersion();

                // Connection pill initial sync
                UpdateConnectionPill();

                // Pedal sub-selector starts on Throttle
                SelectPedalGroup("throttle");
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Redesign] init failed: {ex.Message}");
            }
        }

        // Two-way bind a MozaCurveEditor's YN dependency properties to the
        // corresponding slider's Value. Accepts 5 sliders (curve mode) or 6
        // (equalizer mode); the 6th slot binds to Y6Property.
        private void BindEditorToSliders(MozaControls.MozaCurveEditor editor, Slider[] sliders)
        {
            if (editor == null || sliders == null || sliders.Length < 5) return;
            var ys = new[] {
                MozaControls.MozaCurveEditor.Y1Property, MozaControls.MozaCurveEditor.Y2Property,
                MozaControls.MozaCurveEditor.Y3Property, MozaControls.MozaCurveEditor.Y4Property,
                MozaControls.MozaCurveEditor.Y5Property, MozaControls.MozaCurveEditor.Y6Property };
            int n = Math.Min(sliders.Length, ys.Length);
            for (int i = 0; i < n; i++)
            {
                var b = new Binding(nameof(Slider.Value))
                {
                    Source = sliders[i],
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                };
                BindingOperations.SetBinding(editor, ys[i], b);
            }
        }

        // Called from existing RefreshBaseTab — pushes new live-display values.
        private void UpdateRedesignLiveDisplays()
        {
            try
            {
                bool has = _data.IsBaseConnected;
                string unit = _data.UseFahrenheit ? "°F" : "°C";

                // Convert each raw temp to the display unit, push onto the
                // rolling buffer, and update the legend text. When the base is
                // disconnected the legend reads "—" but the buffer still slides
                // (zeros) so the graph trails off to baseline.
                double mcu = has ? ConvertTemp(_data.McuTemp) : 0;
                double mosfet = has ? ConvertTemp(_data.MosfetTemp) : 0;
                double motor = has ? ConvertTemp(_data.MotorTemp) : 0;

                PushTemperatureSample(_mcuTempSamples, mcu);
                PushTemperatureSample(_mosfetTempSamples, mosfet);
                PushTemperatureSample(_motorTempSamples, motor);

                if (McuTempLegend != null)
                    McuTempLegend.Text = has ? $"{mcu:F0} {unit}" : "—";
                if (MosfetTempLegend != null)
                    MosfetTempLegend.Text = has ? $"{mosfet:F0} {unit}" : "—";
                if (MotorTempLegend != null)
                    MotorTempLegend.Text = has ? $"{motor:F0} {unit}" : "—";

                if (SteeringArcViz != null)
                {
                    double maxA = _data.MaxAngle > 0 ? _data.MaxAngle : 540;
                    SteeringArcViz.MaxAngle = maxA;
                }
                UpdateConnectionPill();
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[Redesign] live update failed: {ex.Message}");
            }
        }

        private static void PushTemperatureSample(ObservableCollection<double> series, double value)
        {
            series.Add(value);
            while (series.Count > TemperatureSamples) series.RemoveAt(0);
        }

        // Called from existing OnSteeringAngleTick — every ~33ms.
        private void UpdateRedesignSteeringAngle(double degrees, bool valid)
        {
            if (SteeringArcViz == null) return;
            SteeringArcViz.Angle = valid ? degrees : 0;
        }

        private void UpdateConnectionPill()
        {
            if (ConnectionPill == null) return;
            ConnectionPill.IsConnected = _data.IsConnected;
            ConnectionPill.PortName = _plugin.Connection?.LastPortName ?? "—";
            ConnectionPill.StatusText = _data.IsConnected ? "Connected" : "Disconnected";
        }

        private void OnBandwidthTick(object? sender, EventArgs e)
        {
            try
            {
                long inBytes = SerialTrafficCapture.Instance.TotalRxBytes;
                long outBytes = SerialTrafficCapture.Instance.TotalTxBytes;

                var now = DateTime.UtcNow;
                if (_bwLastTick != DateTime.MinValue)
                {
                    double elapsed = (now - _bwLastTick).TotalSeconds;
                    if (elapsed > 0)
                    {
                        long inDelta = Math.Max(0, inBytes - _bwLastInBytes);
                        long outDelta = Math.Max(0, outBytes - _bwLastOutBytes);
                        double inRate = inDelta / elapsed;
                        double outRate = outDelta / elapsed;

                        PushBandwidthSample(_bwInSamples, inRate);
                        PushBandwidthSample(_bwOutSamples, outRate);

                        _bwSessionIn += inDelta;
                        _bwSessionOut += outDelta;
                        if (inRate > _bwPeakIn) _bwPeakIn = (long)inRate;
                        if (outRate > _bwPeakOut) _bwPeakOut = (long)outRate;

                        BandwidthInValueText.Text = FormatBytesPerSec(inRate);
                        BandwidthOutValueText.Text = FormatBytesPerSec(outRate);
                        BandwidthInPeakText.Text = FormatBytesPerSec(_bwPeakIn);
                        BandwidthOutPeakText.Text = FormatBytesPerSec(_bwPeakOut);
                        BandwidthInSessionText.Text = FormatBytesTotal(_bwSessionIn);
                        BandwidthOutSessionText.Text = FormatBytesTotal(_bwSessionOut);
                    }
                }
                _bwLastInBytes = inBytes;
                _bwLastOutBytes = outBytes;
                _bwLastTick = now;
            }
            catch
            {
                // Bandwidth display is non-critical; swallow errors so the
                // timer continues ticking and other UI keeps refreshing.
            }
        }

        private static void PushBandwidthSample(ObservableCollection<double> series, double value)
        {
            series.Add(value);
            while (series.Count > BandwidthSamples) series.RemoveAt(0);
        }

        private static string FormatBytesPerSec(double bps)
        {
            if (bps < 1024) return $"{bps:F0} B/s";
            if (bps < 1024 * 1024) return $"{bps / 1024:F1} KB/s";
            return $"{bps / (1024.0 * 1024):F2} MB/s";
        }

        private static string FormatBytesTotal(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        // -------- Pedal sub-selector --------

        private void PedalSelector_Throttle_Click(object sender, RoutedEventArgs e) => SelectPedalGroup("throttle");
        private void PedalSelector_Brake_Click(object sender, RoutedEventArgs e) => SelectPedalGroup("brake");
        private void PedalSelector_Clutch_Click(object sender, RoutedEventArgs e) => SelectPedalGroup("clutch");

        private void SelectPedalGroup(string which)
        {
            if (ThrottlePedalGroup == null || BrakePedalGroup == null || ClutchPedalGroup == null) return;
            ThrottlePedalGroup.Visibility = which == "throttle" ? Visibility.Visible : Visibility.Collapsed;
            BrakePedalGroup.Visibility    = which == "brake"    ? Visibility.Visible : Visibility.Collapsed;
            ClutchPedalGroup.Visibility   = which == "clutch"   ? Visibility.Visible : Visibility.Collapsed;

            // Restyle the chips — selected one gets the primary cyan, others ghost
            var primary = (Style)FindResource("MozaButtonPrimary");
            var ghost   = (Style)FindResource("MozaButtonGhost");
            PedalSelectorThrottle.Style = which == "throttle" ? primary : ghost;
            PedalSelectorBrake.Style    = which == "brake"    ? primary : ghost;
            PedalSelectorClutch.Style   = which == "clutch"   ? primary : ghost;
        }

        // -------- Full Diagnostic Report expander handler --------

        private bool _fullDiagExpanded;
        private void FullDiagToggle_Click(object sender, RoutedEventArgs e)
        {
            _fullDiagExpanded = !_fullDiagExpanded;
            if (_fullDiagExpanded)
            {
                try
                {
                    var report = BuildDiagnosticsDump();
                    FullDiagReportBox.Text = report;
                    int lineCount = report.Split('\n').Length;
                    FullDiagSummaryText.Text = $"// {lineCount} lines · same content as Export bundle";
                }
                catch (Exception ex)
                {
                    FullDiagReportBox.Text = $"[error rendering diagnostic report]\n{ex}";
                    FullDiagSummaryText.Text = "// render failed";
                }
                FullDiagReportBox.Visibility = Visibility.Visible;
                FullDiagToggleButton.Content = "COLLAPSE";
            }
            else
            {
                FullDiagReportBox.Visibility = Visibility.Collapsed;
                FullDiagToggleButton.Content = "EXPAND";
            }
        }

    }
}
