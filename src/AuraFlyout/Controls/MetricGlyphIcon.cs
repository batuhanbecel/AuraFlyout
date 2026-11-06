using System.Windows;
using System.Windows.Media;
using AuraFlyout.Modules.SystemStatus;

namespace AuraFlyout.Controls;

/// <summary>
/// The small monochrome metric glyphs of TaskbarStatus (CPU, RAM, GPU, FPS, temperature,
/// download, upload), redrawn with WPF on the same 16×16 grid with a 1.4 px pen.
/// </summary>
public sealed class MetricGlyphIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(MetricGlyphKind), typeof(MetricGlyphIcon),
            new FrameworkPropertyMetadata(MetricGlyphKind.None, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrushProperty =
        DependencyProperty.Register(nameof(Brush), typeof(Brush), typeof(MetricGlyphIcon),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public MetricGlyphKind Kind
    {
        get => (MetricGlyphKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Brush Brush
    {
        get => (Brush)GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double s = double.IsNaN(Width) ? 12 : Width;
        return new Size(s, double.IsNaN(Height) ? s : Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0 || Kind == MetricGlyphKind.None) return;

        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(size / 16.0, size / 16.0));
        var p = new Pen(Brush, 1.4) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };

        void Line(double x1, double y1, double x2, double y2) => dc.DrawLine(p, new Point(x1, y1), new Point(x2, y2));
        void Rect(double x, double y, double w, double h) => dc.DrawRectangle(null, p, new Rect(x, y, w, h));

        switch (Kind)
        {
            case MetricGlyphKind.Cpu:
                Rect(4, 4, 8, 8);
                for (int n = 5; n <= 11; n += 3)
                {
                    Line(n, 1, n, 4); Line(n, 12, n, 15);
                    Line(1, n, 4, n); Line(12, n, 15, n);
                }
                break;
            case MetricGlyphKind.Ram:
                Rect(1, 4, 14, 7);
                for (int n = 3; n <= 12; n += 3)
                {
                    Line(n, 6, n, 9); Line(n, 11, n, 14);
                }
                break;
            case MetricGlyphKind.Gpu:
                Rect(1, 2, 14, 10);
                Line(8, 12, 8, 15); Line(4, 15, 12, 15);
                break;
            case MetricGlyphKind.Fps:
                Arc(dc, p, 2, 3, 12, 12, 180, 180);
                Line(8, 9, 12, 5); Line(2, 12, 14, 12);
                break;
            case MetricGlyphKind.Temperature:
                Arc(dc, p, 6, 1, 4, 4, 180, 180);
                Line(6, 3, 6, 9); Line(10, 3, 10, 9);
                Arc(dc, p, 4, 7, 8, 8, -60, 300);
                Line(8, 5, 8, 12);
                break;
            case MetricGlyphKind.Download:
            case MetricGlyphKind.Upload:
                bool down = Kind == MetricGlyphKind.Download;
                double end = down ? 12 : 3, tail = down ? 3 : 12, wing = down ? 8 : 7;
                Line(8, tail, 8, end);
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(4, wing), false, false);
                    ctx.LineTo(new Point(8, end), true, false);
                    ctx.LineTo(new Point(12, wing), true, false);
                }
                dc.DrawGeometry(null, p, g);
                Line(3, 15, 13, 15);
                break;
            case MetricGlyphKind.Disk:
                Rect(1, 4, 14, 8);
                Line(3, 10, 6, 10);
                dc.DrawEllipse(Brush, null, new Point(12, 8), 1, 1);
                break;
            case MetricGlyphKind.Ping: // stopwatch
                dc.DrawEllipse(null, p, new Point(8, 9), 6, 6);
                Line(8, 9, 8, 5.5); Line(6, 1, 10, 1); Line(8, 1, 8, 3);
                break;
        }
        dc.Pop();
        dc.Pop();
    }

    /// <summary>GDI+ style DrawArc: ellipse in (x,y,w,h), angles in degrees, clockwise from +X.</summary>
    private static void Arc(DrawingContext dc, Pen pen, double x, double y, double w, double h, double start, double sweep)
    {
        double rx = w / 2, ry = h / 2, cx = x + rx, cy = y + ry;
        double a0 = start * Math.PI / 180, a1 = (start + sweep) * Math.PI / 180;
        var p0 = new Point(cx + rx * Math.Cos(a0), cy + ry * Math.Sin(a0));
        var p1 = new Point(cx + rx * Math.Cos(a1), cy + ry * Math.Sin(a1));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(p0, false, false);
            ctx.ArcTo(p1, new Size(rx, ry), 0, Math.Abs(sweep) > 180, sweep >= 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
        }
        dc.DrawGeometry(null, pen, g);
    }
}
