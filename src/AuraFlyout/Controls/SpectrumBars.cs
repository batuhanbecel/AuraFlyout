using System.Windows;
using System.Windows.Media;

namespace AuraFlyout.Controls;

/// <summary>Lightweight bar visualizer drawn in OnRender (no element per bar).</summary>
public sealed class SpectrumBars : FrameworkElement
{
    public static readonly DependencyProperty BarBrushProperty =
        DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(SpectrumBars),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarWidthProperty =
        DependencyProperty.Register(nameof(BarWidth), typeof(double), typeof(SpectrumBars),
            new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarGapProperty =
        DependencyProperty.Register(nameof(BarGap), typeof(double), typeof(SpectrumBars),
            new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenteredProperty =
        DependencyProperty.Register(nameof(Centered), typeof(bool), typeof(SpectrumBars),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarCountProperty =
        DependencyProperty.Register(nameof(BarCount), typeof(int), typeof(SpectrumBars),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private float[] _levels = [];

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public double BarWidth
    {
        get => (double)GetValue(BarWidthProperty);
        set => SetValue(BarWidthProperty, value);
    }

    public double BarGap
    {
        get => (double)GetValue(BarGapProperty);
        set => SetValue(BarGapProperty, value);
    }

    /// <summary>Bars grow from the vertical centre instead of the bottom.</summary>
    public bool Centered
    {
        get => (bool)GetValue(CenteredProperty);
        set => SetValue(CenteredProperty, value);
    }

    /// <summary>Fixed bar count used for measuring (0 = stretch to available width).</summary>
    public int BarCount
    {
        get => (int)GetValue(BarCountProperty);
        set => SetValue(BarCountProperty, value);
    }

    public float[] Levels
    {
        get => _levels;
        set
        {
            // nothing visible changed (silence, a held note): skip the redraw, which on the taskbar
            // means skipping a whole layered-window update
            if (_levels.Length == value.Length)
            {
                bool same = true;
                for (int i = 0; i < value.Length && same; i++) same = Math.Abs(_levels[i] - value[i]) < 0.004f;
                if (same) return;
                Array.Copy(value, _levels, value.Length);
            }
            else _levels = (float[])value.Clone();
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (BarCount <= 0) return new Size(0, 0);
        return new Size(BarCount * BarWidth + (BarCount - 1) * BarGap, 0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double h = ActualHeight, w = ActualWidth;
        if (h <= 0 || w <= 0 || _levels.Length == 0) return;

        int n = _levels.Length;
        double barW = BarWidth;
        double gap = BarGap;
        double total = n * barW + (n - 1) * gap;
        if (total > w)
        {
            // squeeze to fit
            double k = w / total;
            barW *= k;
            gap *= k;
            total = w;
        }
        double x = (w - total) / 2;
        var brush = Shaded(BarBrush);
        // pill-shaped bars; at rest they are dots
        double radius = barW / 2;
        double minH = barW;

        for (int i = 0; i < n; i++)
        {
            float level = Math.Clamp(_levels[i], 0, 1);
            double bh = Math.Max(minH, level * h);
            double y = Centered ? (h - bh) / 2 : h - bh;
            // quiet bars recede a little, loud ones stand out
            dc.PushOpacity(0.55 + 0.45 * level);
            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, barW, bh), radius, radius);
            dc.Pop();
            x += barW + gap;
        }
    }

    private Brush? _shadedFrom;
    private bool _shadedCentered;
    private Brush? _shaded;

    /// <summary>
    /// A plain colour becomes a soft vertical gradient (each bar gets the whole gradient): a lit core
    /// for centred bars, a lighter tip for bars standing on the bottom.
    /// </summary>
    private Brush Shaded(Brush brush)
    {
        if (ReferenceEquals(brush, _shadedFrom) && _shadedCentered == Centered && _shaded != null) return _shaded;
        _shadedFrom = brush;
        _shadedCentered = Centered;
        if (brush is not SolidColorBrush solid)
            return _shaded = brush;
        var c = solid.Color;
        static byte Lighten(byte v) => (byte)(v + (255 - v) * 0.35);
        var tip = Color.FromArgb(c.A, Lighten(c.R), Lighten(c.G), Lighten(c.B));
        var g = Centered
            ? new LinearGradientBrush(new GradientStopCollection { new(c, 0), new(tip, 0.5), new(c, 1) }, 90)
            : new LinearGradientBrush(tip, c, 90);
        g.Freeze();
        return _shaded = g;
    }
}
