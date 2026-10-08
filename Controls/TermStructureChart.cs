// Controls/TermStructureChart.cs
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace FxVolatilityImport.Controls
{
    public readonly record struct TermPoint(string Label, double Bid, double Ask)
    {
        public double Mid => (Bid + Ask) / 2;
        public bool IsValid => double.IsFinite(Bid) && double.IsFinite(Ask);
    }

    /// <summary>
    /// Ritar ATM-termstrukturen för ett valutapar: bid/ask-band och mid-linje per tenor.
    /// Tenorerna ligger med jämnt avstånd (lättare att läsa än en tidsskalad axel).
    /// </summary>
    public sealed class TermStructureChart : FrameworkElement
    {
        public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
            nameof(Points), typeof(IReadOnlyList<TermPoint>), typeof(TermStructureChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
            nameof(LineBrush), typeof(Brush), typeof(TermStructureChart),
            new FrameworkPropertyMetadata(Brushes.CornflowerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BandBrushProperty = DependencyProperty.Register(
            nameof(BandBrush), typeof(Brush), typeof(TermStructureChart),
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
            nameof(GridBrush), typeof(Brush), typeof(TermStructureChart),
            new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
            nameof(LabelBrush), typeof(Brush), typeof(TermStructureChart),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PointFillProperty = DependencyProperty.Register(
            nameof(PointFill), typeof(Brush), typeof(TermStructureChart),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<TermPoint>? Points
        {
            get => (IReadOnlyList<TermPoint>?)GetValue(PointsProperty);
            set => SetValue(PointsProperty, value);
        }

        public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
        public Brush BandBrush { get => (Brush)GetValue(BandBrushProperty); set => SetValue(BandBrushProperty, value); }
        public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
        public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
        public Brush PointFill { get => (Brush)GetValue(PointFillProperty); set => SetValue(PointFillProperty, value); }

        private const double LeftMargin = 40;
        private const double RightMargin = 12;
        private const double TopMargin = 10;
        private const double BottomMargin = 24;
        private const double LabelFontSize = 10.5;

        protected override void OnRender(DrawingContext dc)
        {
            var width = ActualWidth;
            var height = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

            var points = Points;
            if (points == null || points.Count < 2 || width < 120 || height < 80)
                return;

            var valid = points.Select((p, i) => (Point: p, Index: i)).Where(t => t.Point.IsValid).ToList();
            if (valid.Count < 2)
                return;

            double min = valid.Min(t => t.Point.Bid);
            double max = valid.Max(t => t.Point.Ask);
            if (max - min < 0.5)
            {
                var centre = (max + min) / 2;
                min = centre - 0.25;
                max = centre + 0.25;
            }
            var padding = (max - min) * 0.12;
            min -= padding;
            max += padding;

            var plotWidth = width - LeftMargin - RightMargin;
            var plotHeight = height - TopMargin - BottomMargin;
            double X(int i) => LeftMargin + plotWidth * i / (points.Count - 1);
            double Y(double v) => TopMargin + plotHeight * (1 - (v - min) / (max - min));

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            // Horisontella stödlinjer med etiketter
            var gridPen = new Pen(GridBrush, 1);
            gridPen.Freeze();
            var step = NiceStep((max - min) / 4);
            var format = step < 0.1 ? "0.00" : step < 1 ? "0.0" : "0";
            for (var v = Math.Ceiling(min / step) * step; v <= max + step * 0.001; v += step)
            {
                var y = Math.Round(Y(v)) + 0.5;
                dc.DrawLine(gridPen, new Point(LeftMargin, y), new Point(width - RightMargin, y));

                var label = CreateText(v.ToString(format, CultureInfo.InvariantCulture), typeface, dpi);
                dc.DrawText(label, new Point(LeftMargin - 8 - label.Width, y - label.Height / 2));
            }

            // Bid/ask-band
            var band = new StreamGeometry();
            using (var ctx = band.Open())
            {
                ctx.BeginFigure(new Point(X(valid[0].Index), Y(valid[0].Point.Ask)), isFilled: true, isClosed: true);
                foreach (var t in valid.Skip(1))
                    ctx.LineTo(new Point(X(t.Index), Y(t.Point.Ask)), isStroked: true, isSmoothJoin: true);
                for (int k = valid.Count - 1; k >= 0; k--)
                    ctx.LineTo(new Point(X(valid[k].Index), Y(valid[k].Point.Bid)), isStroked: true, isSmoothJoin: true);
            }
            band.Freeze();
            dc.DrawGeometry(BandBrush, null, band);

            // Mid-linje
            var linePen = new Pen(LineBrush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            linePen.Freeze();
            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(new Point(X(valid[0].Index), Y(valid[0].Point.Mid)), isFilled: false, isClosed: false);
                foreach (var t in valid.Skip(1))
                    ctx.LineTo(new Point(X(t.Index), Y(t.Point.Mid)), isStroked: true, isSmoothJoin: true);
            }
            line.Freeze();
            dc.DrawGeometry(null, linePen, line);

            var dotPen = new Pen(LineBrush, 1.5);
            dotPen.Freeze();
            foreach (var t in valid)
                dc.DrawEllipse(PointFill, dotPen, new Point(X(t.Index), Y(t.Point.Mid)), 3, 3);

            // Tenor-etiketter
            for (int i = 0; i < points.Count; i++)
            {
                var label = CreateText(points[i].Label, typeface, dpi);
                dc.DrawText(label, new Point(X(i) - label.Width / 2, height - BottomMargin + 6));
            }
        }

        private FormattedText CreateText(string text, Typeface typeface, double dpi)
            => new(text, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, typeface, LabelFontSize, LabelBrush, dpi);

        private static double NiceStep(double raw)
        {
            if (raw <= 0 || !double.IsFinite(raw))
                return 1;

            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            var normalized = raw / magnitude;
            var nice = normalized < 1.5 ? 1 : normalized < 3 ? 2 : normalized < 7 ? 5 : 10;
            return nice * magnitude;
        }
    }
}