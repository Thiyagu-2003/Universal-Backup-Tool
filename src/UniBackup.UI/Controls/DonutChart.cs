using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UniBackup.UI.Controls;

public class DonutChart : Control
{
    static DonutChart()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(DonutChart), new FrameworkPropertyMetadata(typeof(DonutChart)));
    }

    public static readonly DependencyProperty SucceededCountProperty =
        DependencyProperty.Register(nameof(SucceededCount), typeof(int), typeof(DonutChart),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PartialCountProperty =
        DependencyProperty.Register(nameof(PartialCount), typeof(int), typeof(DonutChart),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FailedCountProperty =
        DependencyProperty.Register(nameof(FailedCount), typeof(int), typeof(DonutChart),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(DonutChart),
            new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public int SucceededCount
    {
        get => (int)GetValue(SucceededCountProperty);
        set => SetValue(SucceededCountProperty, value);
    }

    public int PartialCount
    {
        get => (int)GetValue(PartialCountProperty);
        set => SetValue(PartialCountProperty, value);
    }

    public int FailedCount
    {
        get => (int)GetValue(FailedCountProperty);
        set => SetValue(FailedCountProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public DonutChart()
    {
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 10 || h <= 10) return;

        double size = Math.Min(w, h);
        Point center = new Point(w / 2.0, h / 2.0);
        double stroke = StrokeThickness;
        double radius = (size - stroke) / 2.0;
        if (radius <= 0) return;

        var brushSunken = TryFindResource("BrushSunken") as Brush ?? new SolidColorBrush(Color.FromRgb(0xEA, 0xEF, 0xF6));
        var brushJade = TryFindResource("BrushJade") as Brush ?? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41));
        var brushSaffron = TryFindResource("BrushSaffron") as Brush ?? new SolidColorBrush(Color.FromRgb(0xE3, 0xA0, 0x08));
        var brushCoral = TryFindResource("BrushCoral") as Brush ?? new SolidColorBrush(Color.FromRgb(0xDA, 0x3B, 0x01));

        int total = SucceededCount + PartialCount + FailedCount;

        // Background track
        var penTrack = new Pen(brushSunken, stroke);
        penTrack.Freeze();
        dc.DrawEllipse(null, penTrack, center, radius, radius);

        if (total <= 0) return;

        // Draw slices
        var slices = new (int count, Brush brush)[]
        {
            (SucceededCount, brushJade),
            (PartialCount, brushSaffron),
            (FailedCount, brushCoral)
        };

        // If only 1 category has count == total, draw a full circle
        foreach (var slice in slices)
        {
            if (slice.count == total && slice.count > 0)
            {
                var fullPen = new Pen(slice.brush, stroke);
                fullPen.Freeze();
                dc.DrawEllipse(null, fullPen, center, radius, radius);
                return;
            }
        }

        // Draw arcs starting at -90 degrees (12 o'clock)
        double currentAngle = -90.0;
        double totalDegrees = 360.0;

        foreach (var slice in slices)
        {
            if (slice.count <= 0) continue;

            double sweepAngle = (slice.count / (double)total) * totalDegrees;
            DrawArc(dc, center, radius, currentAngle, sweepAngle, slice.brush, stroke);
            currentAngle += sweepAngle;
        }
    }

    private static void DrawArc(DrawingContext dc, Point center, double radius, double startAngleDeg, double sweepAngleDeg, Brush brush, double stroke)
    {
        if (sweepAngleDeg <= 0.01) return;

        double gap = sweepAngleDeg > 4.0 ? 1.5 : 0.0;
        double effectiveSweep = sweepAngleDeg - gap;
        double effectiveStart = startAngleDeg + (gap / 2.0);

        double startRad = effectiveStart * Math.PI / 180.0;
        double endRad = (effectiveStart + effectiveSweep) * Math.PI / 180.0;

        Point p1 = new Point(center.X + radius * Math.Cos(startRad), center.Y + radius * Math.Sin(startRad));
        Point p2 = new Point(center.X + radius * Math.Cos(endRad), center.Y + radius * Math.Sin(endRad));

        bool isLargeArc = effectiveSweep > 180.0;

        var geom = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = p1,
            IsClosed = false
        };
        figure.Segments.Add(new ArcSegment(p2, new Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise, true));
        geom.Figures.Add(figure);
        geom.Freeze();

        var pen = new Pen(brush, stroke)
        {
            StartLineCap = PenLineCap.Flat,
            EndLineCap = PenLineCap.Flat
        };
        pen.Freeze();

        dc.DrawGeometry(null, pen, geom);
    }
}
