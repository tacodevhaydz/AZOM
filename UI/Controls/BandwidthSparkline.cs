using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MozaControls
{
    /// <summary>
    /// Dual-series rolling sparkline (inbound + outbound traffic). Both series
    /// share a single Y scale so the user can read relative weight at a glance.
    /// Each series renders as gradient fill + line stroke + trailing tip dot.
    /// Bind <see cref="InSamples"/> / <see cref="OutSamples"/> to
    /// <see cref="System.Collections.IEnumerable"/>s (typically
    /// ObservableCollection&lt;double&gt; that the timer tick mutates).
    /// </summary>
    public class BandwidthSparkline : Control
    {
        static BandwidthSparkline()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(typeof(BandwidthSparkline)));
        }

        // -------- Samples DPs --------

        public static readonly DependencyProperty InSamplesProperty =
            DependencyProperty.Register(nameof(InSamples), typeof(System.Collections.IEnumerable),
                typeof(BandwidthSparkline), new PropertyMetadata(null, OnSamplesChanged));
        public System.Collections.IEnumerable? InSamples
        {
            get => (System.Collections.IEnumerable?)GetValue(InSamplesProperty);
            set => SetValue(InSamplesProperty, value);
        }

        public static readonly DependencyProperty OutSamplesProperty =
            DependencyProperty.Register(nameof(OutSamples), typeof(System.Collections.IEnumerable),
                typeof(BandwidthSparkline), new PropertyMetadata(null, OnSamplesChanged));
        public System.Collections.IEnumerable? OutSamples
        {
            get => (System.Collections.IEnumerable?)GetValue(OutSamplesProperty);
            set => SetValue(OutSamplesProperty, value);
        }

        // -------- Line + fill brush DPs --------

        public static readonly DependencyProperty InBrushProperty =
            DependencyProperty.Register(nameof(InBrush), typeof(Brush), typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));
        public Brush InBrush { get => (Brush)GetValue(InBrushProperty); set => SetValue(InBrushProperty, value); }

        public static readonly DependencyProperty OutBrushProperty =
            DependencyProperty.Register(nameof(OutBrush), typeof(Brush), typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));
        public Brush OutBrush { get => (Brush)GetValue(OutBrushProperty); set => SetValue(OutBrushProperty, value); }

        public static readonly DependencyProperty InFillBrushProperty =
            DependencyProperty.Register(nameof(InFillBrush), typeof(Brush), typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(null));
        public Brush? InFillBrush { get => (Brush?)GetValue(InFillBrushProperty); set => SetValue(InFillBrushProperty, value); }

        public static readonly DependencyProperty OutFillBrushProperty =
            DependencyProperty.Register(nameof(OutFillBrush), typeof(Brush), typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(null));
        public Brush? OutFillBrush { get => (Brush?)GetValue(OutFillBrushProperty); set => SetValue(OutFillBrushProperty, value); }

        /// <summary>Fixed maximum used for vertical scaling. When > 0 both series
        /// render relative to this ceiling (e.g. the serial port's byte-rate cap)
        /// so quiet traffic stays small instead of being auto-stretched. When 0
        /// the chart auto-scales to the rolling-window peak across both series.</summary>
        public static readonly DependencyProperty MaxValueProperty =
            DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((BandwidthSparkline)d).Recompute()));
        public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }

        // -------- Read-only geometries + tip points --------

        private static DependencyPropertyKey RoG(string name) =>
            DependencyProperty.RegisterReadOnly(name, typeof(Geometry),
                typeof(BandwidthSparkline), new PropertyMetadata(null));
        private static DependencyPropertyKey RoD(string name) =>
            DependencyProperty.RegisterReadOnly(name, typeof(double),
                typeof(BandwidthSparkline), new PropertyMetadata(0.0));

        private static readonly DependencyPropertyKey InLineGeometryKey =  RoG("InLineGeometry");
        private static readonly DependencyPropertyKey InFillGeometryKey =  RoG("InFillGeometry");
        private static readonly DependencyPropertyKey OutLineGeometryKey = RoG("OutLineGeometry");
        private static readonly DependencyPropertyKey OutFillGeometryKey = RoG("OutFillGeometry");
        private static readonly DependencyPropertyKey InTipXKey =  RoD("InTipX");
        private static readonly DependencyPropertyKey InTipYKey =  RoD("InTipY");
        private static readonly DependencyPropertyKey OutTipXKey = RoD("OutTipX");
        private static readonly DependencyPropertyKey OutTipYKey = RoD("OutTipY");

        public static readonly DependencyProperty InLineGeometryProperty =  InLineGeometryKey.DependencyProperty;
        public static readonly DependencyProperty InFillGeometryProperty =  InFillGeometryKey.DependencyProperty;
        public static readonly DependencyProperty OutLineGeometryProperty = OutLineGeometryKey.DependencyProperty;
        public static readonly DependencyProperty OutFillGeometryProperty = OutFillGeometryKey.DependencyProperty;
        public static readonly DependencyProperty InTipXProperty =  InTipXKey.DependencyProperty;
        public static readonly DependencyProperty InTipYProperty =  InTipYKey.DependencyProperty;
        public static readonly DependencyProperty OutTipXProperty = OutTipXKey.DependencyProperty;
        public static readonly DependencyProperty OutTipYProperty = OutTipYKey.DependencyProperty;

        public Geometry? InLineGeometry  => (Geometry?)GetValue(InLineGeometryProperty);
        public Geometry? InFillGeometry  => (Geometry?)GetValue(InFillGeometryProperty);
        public Geometry? OutLineGeometry => (Geometry?)GetValue(OutLineGeometryProperty);
        public Geometry? OutFillGeometry => (Geometry?)GetValue(OutFillGeometryProperty);
        public double InTipX  => (double)GetValue(InTipXProperty);
        public double InTipY  => (double)GetValue(InTipYProperty);
        public double OutTipX => (double)GetValue(OutTipXProperty);
        public double OutTipY => (double)GetValue(OutTipYProperty);

        // --------------------------------------------------------------------

        private static void OnSamplesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (BandwidthSparkline)d;
            if (e.OldValue is INotifyCollectionChanged oldNcc)
                oldNcc.CollectionChanged -= self.OnCollectionChanged;
            if (e.NewValue is INotifyCollectionChanged newNcc)
                newNcc.CollectionChanged += self.OnCollectionChanged;
            self.Recompute();
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Recompute();

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            Recompute();
        }

        private static List<double> Collect(System.Collections.IEnumerable? src)
        {
            var list = new List<double>();
            if (src == null) return list;
            foreach (var v in src)
            {
                if (v is IConvertible c)
                    list.Add(c.ToDouble(System.Globalization.CultureInfo.InvariantCulture));
            }
            return list;
        }

        private void Recompute()
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0)
            {
                Clear();
                return;
            }

            var inVals  = Collect(InSamples);
            var outVals = Collect(OutSamples);
            if (inVals.Count == 0 && outVals.Count == 0)
            {
                Clear();
                return;
            }

            // Shared Y scale across both series so the user can compare them.
            // Fixed ceiling (MaxValue) wins when set, else auto-scale to the
            // combined window peak.
            double peak = 0;
            if (inVals.Count > 0)  peak = Math.Max(peak, inVals.Max());
            if (outVals.Count > 0) peak = Math.Max(peak, outVals.Max());
            double scaleRef = MaxValue > 0 ? MaxValue : peak;
            double max = Math.Max(1, scaleRef);
            double clip = max;

            double pad = 2;
            double plotW = w - pad * 2;
            double plotH = h - pad * 2;

            (Geometry? line, Geometry? fill, double tipX, double tipY)
                Build(List<double> values)
            {
                if (values.Count == 0) return (null, null, 0, 0);
                int n = values.Count;
                double X(int i) => pad + (n == 1 ? 0 : plotW * i / (n - 1));
                double Y(int i) => h - pad - Math.Min(values[i], clip) / max * plotH;

                var lineFig = new PathFigure { StartPoint = new Point(X(0), Y(0)), IsClosed = false, IsFilled = false };
                for (int i = 1; i < n; i++)
                    lineFig.Segments.Add(new LineSegment(new Point(X(i), Y(i)), true));
                var lineGeom = new PathGeometry(); lineGeom.Figures.Add(lineFig); lineGeom.Freeze();

                var fillFig = new PathFigure { StartPoint = new Point(pad, h - pad), IsClosed = true, IsFilled = true };
                for (int i = 0; i < n; i++)
                    fillFig.Segments.Add(new LineSegment(new Point(X(i), Y(i)), false));
                fillFig.Segments.Add(new LineSegment(new Point(pad + plotW, h - pad), false));
                var fillGeom = new PathGeometry(); fillGeom.Figures.Add(fillFig); fillGeom.Freeze();

                return (lineGeom, fillGeom, pad + plotW - 3, Y(n - 1) - 3);
            }

            var (inLine,  inFill,  inTipX,  inTipY)  = Build(inVals);
            var (outLine, outFill, outTipX, outTipY) = Build(outVals);

            SetValue(InLineGeometryKey,  inLine);
            SetValue(InFillGeometryKey,  inFill);
            SetValue(OutLineGeometryKey, outLine);
            SetValue(OutFillGeometryKey, outFill);
            SetValue(InTipXKey,  inTipX);  SetValue(InTipYKey,  inTipY);
            SetValue(OutTipXKey, outTipX); SetValue(OutTipYKey, outTipY);
        }

        private void Clear()
        {
            SetValue(InLineGeometryKey,  null);
            SetValue(InFillGeometryKey,  null);
            SetValue(OutLineGeometryKey, null);
            SetValue(OutFillGeometryKey, null);
        }
    }
}
