using System.Windows;
using System.Windows.Media;

namespace AuraFlyout.Controls;

/// <summary>Tiny line chart with a soft fill, used in the system status panel.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
            new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(Sparkline),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private float[] _values = [];

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Top of the scale; 0 = scale to the largest value.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>Number of samples the width represents.</summary>
    public int Capacity { get; set; } = 60;

    public void SetValues(IEnumerable<float> values)
    {
        _values = values.ToArray();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || _values.Length < 2) return;

        double max = Maximum > 0 ? Maximum : Math.Max(0.001, _values.Max() * 1.15);
        double step = w / Math.Max(1, Capacity - 1);
        double x0 = w - step * (_values.Length - 1);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            for (int i = 0; i < _values.Length; i++)
            {
                var p = new Point(x0 + i * step, h - Math.Clamp(_values[i] / max, 0, 1) * (h - 1.5) - 0.75);
                if (i == 0)
                {
                    l.BeginFigure(p, false, false);
                    a.BeginFigure(new Point(p.X, h), true, true);
                    a.LineTo(p, false, false);
                }
                else
                {
                    l.LineTo(p, true, true);
                    a.LineTo(p, false, false);
                }
            }
            a.LineTo(new Point(w, h), false, false);
        }
        line.Freeze();
        area.Freeze();

        var fill = Stroke.Clone();
        fill.Opacity = 0.16;
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.5) { LineJoin = PenLineJoin.Round }, line);
    }
}

/// <summary>
/// TaskbarSpeedTest's gauge: a 240° arc with evenly spaced scale marks (0, 5, 10, 50, 100, 250, 500,
/// 1G) and linear steps in between, so small speeds still move it. The arc's centre sits 20 + 92 px
/// from the top; <see cref="Fraction"/> (0..1) is animated by the owner.
/// </summary>
public sealed class SpeedGauge : FrameworkElement
{
    public const double Radius = 92, Thickness = 10, CentreY = 20 + Radius;
    private const double StartAngle = 150, SweepAngle = 240;

    public static readonly DependencyProperty FractionProperty =
        DependencyProperty.Register(nameof(Fraction), typeof(double), typeof(SpeedGauge),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(SpeedGauge),
            new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty =
        DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(SpeedGauge),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty =
        DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(SpeedGauge),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private double[] _marks = MarksFor(megabytes: false, big: false);

    /// <summary>0..1 along the arc.</summary>
    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public double[] Marks
    {
        get => _marks;
        set
        {
            if (_marks.SequenceEqual(value)) return;
            _marks = value;
            InvalidateVisual();
        }
    }

    /// <summary>Scale marks in the chosen unit; a wider scale once speeds pass a gigabit.</summary>
    public static double[] MarksFor(bool megabytes, bool big) => megabytes
        ? big ? [0, 1, 10, 50, 100, 250, 500, 1250] : [0, 1, 5, 10, 25, 50, 100, 125]
        : big ? [0, 10, 100, 500, 1000, 2500, 5000, 10000] : [0, 5, 10, 50, 100, 250, 500, 1000];

    /// <summary>Where <paramref name="v"/> sits on the arc (0..1): evenly spaced marks, linear in between.</summary>
    public static double Position(double v, double[] m)
    {
        if (v <= 0) return 0;
        for (int i = 1; i < m.Length; i++)
            if (v <= m[i]) return (i - 1 + (v - m[i - 1]) / (m[i] - m[i - 1])) / (m.Length - 1);
        return 1;
    }

    private static string MarkText(double v)
    {
        if (v >= 1000 && v % 1000 == 0) return v / 1000 + "G";
        if (v >= 2500) return (v / 1000).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + "G";
        return v.ToString("0", System.Globalization.CultureInfo.CurrentCulture);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0) return;
        var c = new Point(ActualWidth / 2, CentreY);
        var cap = new Func<Brush, Pen>(b => new Pen(b, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        DrawArc(dc, c, Radius, StartAngle, SweepAngle, cap(Track));
        if (Fraction > 0.002) DrawArc(dc, c, Radius, StartAngle, SweepAngle * Math.Min(1, Fraction), cap(Fill));

        // scale labels just inside the arc
        double lr = Radius - Thickness / 2 - 13;
        var face = new Typeface(Taskbar.DockWindow.TaskbarFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i < _marks.Length; i++)
        {
            double a = (StartAngle + SweepAngle * i / (_marks.Length - 1)) * Math.PI / 180;
            var ft = new FormattedText(MarkText(_marks[i]), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                face, 10, LabelBrush, dpi);
            dc.DrawText(ft, new Point(c.X + Math.Cos(a) * lr - ft.Width / 2, c.Y + Math.Sin(a) * lr - ft.Height / 2));
        }
    }

    private static void DrawArc(DrawingContext dc, Point c, double r, double startDeg, double sweepDeg, Pen pen)
    {
        double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
        var p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
        var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(p0, false, false);
            ctx.ArcTo(p1, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}
