using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace MozaControls
{
    /// <summary>
    /// Time-domain scope for the 3 wheelbase-LFE slots. The carrier oscillation
    /// happens on the base firmware (the host only streams freq + amplitude at
    /// 50 Hz), so the waveform is SYNTHESISED here: phase is integrated from the
    /// polled carrier frequency, and each slot is drawn as a min/max amplitude
    /// envelope — a clean wave at low Hz, a filled band once a screen column spans
    /// more than half a cycle. Height scales with amplitude (intensity × envelope).
    /// Poll() supplies the live (freq, amp) per slot; a flat line = idle.
    /// </summary>
    public class LfeScope : FrameworkElement
    {
        public Func<(double freq, double amp)[]>? Poll { get; set; }

        private const double WindowSec = 8.0;
        private static readonly Color[] SlotColors =
        {
            Color.FromRgb(0x3C, 0xE0, 0xE0),   // slot 1 — cyan
            Color.FromRgb(0xFF, 0xB8, 0x4D),   // slot 2 — amber
            Color.FromRgb(0x8B, 0xD8, 0x66),   // slot 3 — green
        };

        private struct Sample { public double T, Phase, Amp; }
        private readonly List<Sample>[] _buf = { new List<Sample>(), new List<Sample>(), new List<Sample>() };
        private readonly Brush[] _fill = new Brush[3];
        private readonly Pen[] _stroke = new Pen[3];
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
                _fill[i] = new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B)); _fill[i].Freeze();
                _stroke[i] = new Pen(new SolidColorBrush(c), 1.2); _stroke[i].Freeze();
            }
            _bg = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)); _bg.Freeze();
            _grid = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1); _grid.Freeze();
            _sep = new Pen(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), 1); _sep.Freeze();
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
                double freq = i < vals.Length ? vals[i].freq : 0;
                double amp = i < vals.Length ? vals[i].amp : 0;
                double phase = 0;
                if (buf.Count > 0)
                {
                    var last = buf[buf.Count - 1];
                    double dt = t - last.T;
                    if (dt > 0.3) buf.Clear();                 // hidden / hitched → restart cleanly
                    else phase = last.Phase + 2.0 * Math.PI * freq * dt;
                }
                buf.Add(new Sample { T = t, Phase = phase, Amp = amp });
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

            // One horizontal lane per slot so the traces never overlap.
            double laneH = h / 3.0;
            double t1 = _clock.Elapsed.TotalSeconds, t0 = t1 - WindowSec;
            for (int i = 0; i < 3; i++)
            {
                double mid = laneH * (i + 0.5);
                double scale = laneH / 2.0 * 0.82;
                dc.DrawLine(_grid, new Point(0, mid), new Point(w, mid));      // lane centreline
                if (i > 0) dc.DrawLine(_sep, new Point(0, laneH * i), new Point(w, laneH * i));  // lane divider

                var buf = _buf[i];
                if (buf.Count < 2) continue;
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    // top edge (max) left→right, then bottom edge (min) right→left.
                    var top = new List<Point>(buf.Count);
                    var bot = new List<Point>(buf.Count);
                    for (int k = 0; k < buf.Count - 1; k++)
                    {
                        var a = buf[k]; var b = buf[k + 1];
                        double x = (a.T - t0) / WindowSec * w;
                        var (lo, hi) = SinRange(a.Phase, b.Phase);
                        top.Add(new Point(x, mid - a.Amp * hi * scale));
                        bot.Add(new Point(x, mid - a.Amp * lo * scale));
                    }
                    g.BeginFigure(top[0], true, true);
                    g.PolyLineTo(top, true, false);
                    for (int k = bot.Count - 1; k >= 0; k--) g.LineTo(bot[k], true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(_fill[i], _stroke[i], geo);
            }
        }

        // Min and max of sin over [a, b] — endpoints, plus ±1 if the interval
        // sweeps past a peak (π/2) or trough (3π/2).
        private static (double lo, double hi) SinRange(double a, double b)
        {
            if (b < a) { double t = a; a = b; b = t; }
            if (b - a >= 2.0 * Math.PI) return (-1.0, 1.0);
            double sa = Math.Sin(a), sb = Math.Sin(b);
            double lo = Math.Min(sa, sb), hi = Math.Max(sa, sb);
            if (Contains(a, b, Math.PI / 2.0)) hi = 1.0;
            if (Contains(a, b, 3.0 * Math.PI / 2.0)) lo = -1.0;
            return (lo, hi);
        }
        private static bool Contains(double a, double b, double target)
        {
            double k = Math.Ceiling((a - target) / (2.0 * Math.PI));
            return target + k * 2.0 * Math.PI <= b;
        }
    }
}
