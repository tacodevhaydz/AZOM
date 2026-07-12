using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MozaControls
{
    /// <summary>
    /// Time-domain visualizer for the 3 wheelbase-LFE slots, one lane each. Rather
    /// than synthesising the raw carrier (unresolvable at high Hz over a multi-second
    /// window), each slot is drawn as a RIBBON: vertical position = carrier frequency
    /// (0..200 Hz up the lane), thickness = amplitude (intensity × envelope). A steady
    /// tone is a flat ribbon, a rev is a rising one, a strong effect is thick, an idle
    /// slot collapses to a thin line at the floor. Poll() supplies live (freq, amp).
    /// </summary>
    public class LfeScope : FrameworkElement
    {
        public Func<(double freq, double amp)[]>? Poll { get; set; }

        private const double WindowSec = 8.0;
        private const double MaxHz = 200.0;
        private static readonly Color[] SlotColors =
        {
            Color.FromRgb(0x3C, 0xE0, 0xE0),   // slot 1 — cyan
            Color.FromRgb(0xFF, 0xB8, 0x4D),   // slot 2 — amber
            Color.FromRgb(0x8B, 0xD8, 0x66),   // slot 3 — green
        };

        private struct Sample { public double T, Freq, Amp; }
        private readonly List<Sample>[] _buf = { new List<Sample>(), new List<Sample>(), new List<Sample>() };
        private readonly Brush[] _fill = new Brush[3];
        private readonly Pen[] _stroke = new Pen[3];
        private readonly Typeface _face = new Typeface("Segoe UI");
        private readonly Brush _bg;
        private readonly Pen _grid;
        private readonly Pen _sep;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private bool _hooked;

        public LfeScope()
        {
            for (int i = 0; i < 3; i++)
            {
                var c = SlotColors[i];
                _fill[i] = new SolidColorBrush(Color.FromArgb(0x9A, c.R, c.G, c.B)); _fill[i].Freeze();
                _stroke[i] = new Pen(new SolidColorBrush(c), 1.0); _stroke[i].Freeze();
            }
            _bg = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)); _bg.Freeze();
            _grid = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1); _grid.Freeze();
            _sep = new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), 1); _sep.Freeze();
            Loaded += (_, __) => Hook();
            Unloaded += (_, __) => Unhook();
        }

        private void Hook() { if (!_hooked) { CompositionTarget.Rendering += OnFrame; _hooked = true; } }
        private void Unhook() { if (_hooked) { CompositionTarget.Rendering -= OnFrame; _hooked = false; } }

        private void OnFrame(object? sender, EventArgs e)
        {
            if (!IsVisible || Poll == null || ActualWidth <= 0) return;
            double t = _clock.Elapsed.TotalSeconds;
            var vals = Poll();
            for (int i = 0; i < 3; i++)
            {
                var buf = _buf[i];
                double amp = i < vals.Length ? vals[i].amp : 0;
                double freq = i < vals.Length ? vals[i].freq : 0;
                if (buf.Count > 0 && t - buf[buf.Count - 1].T > 0.3) buf.Clear();   // hidden/hitched → restart
                buf.Add(new Sample { T = t, Freq = freq, Amp = amp });
                double cutoff = t - WindowSec;
                int drop = 0;
                while (drop < buf.Count && buf[drop].T < cutoff) drop++;
                if (drop > 0) buf.RemoveRange(0, drop);
            }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            dc.DrawRectangle(_bg, null, new Rect(0, 0, w, h));

            double laneH = h / 3.0;
            double t1 = _clock.Elapsed.TotalSeconds, t0 = t1 - WindowSec;
            for (int i = 0; i < 3; i++)
            {
                double laneTop = laneH * i, laneBot = laneTop + laneH;
                double floor = laneBot - laneH * 0.08;              // freq 0 sits here
                double span = laneH * 0.84;                         // freq 0..MaxHz spans this
                double maxHalf = laneH * 0.30;                      // full-amplitude half-thickness

                if (i > 0) dc.DrawLine(_sep, new Point(0, laneTop), new Point(w, laneTop));
                dc.DrawLine(_grid, new Point(0, floor), new Point(w, floor));   // freq-0 baseline

                var buf = _buf[i];
                if (buf.Count >= 2)
                {
                    var top = new List<Point>(buf.Count);
                    var bot = new List<Point>(buf.Count);
                    for (int k = 0; k < buf.Count; k++)
                    {
                        var s = buf[k];
                        double x = (s.T - t0) / WindowSec * w;
                        double fN = Math.Max(0, Math.Min(1, s.Freq / MaxHz));
                        double cy = floor - fN * span;
                        double half = s.Amp * maxHalf;
                        top.Add(new Point(x, Math.Max(laneTop + 1, cy - half)));
                        bot.Add(new Point(x, Math.Min(laneBot - 1, cy + half)));
                    }
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        g.BeginFigure(top[0], true, true);
                        g.PolyLineTo(top, true, false);
                        for (int k = bot.Count - 1; k >= 0; k--) g.LineTo(bot[k], true, false);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(_fill[i], _stroke[i], geo);

                    // current-frequency readout at the right of the lane
                    var last = buf[buf.Count - 1];
                    if (last.Amp > 0.01)
                    {
                        var txt = new FormattedText(
                            ((int)Math.Round(last.Freq)).ToString(CultureInfo.InvariantCulture) + " Hz",
                            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, 10.5,
                            new SolidColorBrush(SlotColors[i]), 1.0);
                        dc.DrawText(txt, new Point(w - txt.Width - 4, laneTop + 3));
                    }
                }
            }
        }
    }
}
