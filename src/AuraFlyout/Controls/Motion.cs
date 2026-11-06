using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AuraFlyout.Core;

namespace AuraFlyout.Controls;

/// <summary>
/// The app's motion, on WinUI's curves: entrances decelerate hard (≈ cubic-bezier(0,0,0,1)), exits
/// accelerate. Everything is skipped when Windows' "Animation effects" is off.
/// </summary>
public static class Motion
{
    public static readonly IEasingFunction Decelerate = new ExponentialEase { Exponent = 7, EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction Standard = new CubicEase { EasingMode = EasingMode.EaseInOut };

    static Motion()
    {
        ((Freezable)Decelerate).Freeze();
        ((Freezable)Standard).Freeze();
    }

    public static bool On => ThemeService.AnimationsEnabled;

    /// <summary>WinUI's page entrance: slides up from <paramref name="offset"/> px while fading in.</summary>
    public static void Entrance(UIElement e, double offset = 28, int ms = 400, int delayMs = 0)
    {
        if (!On)
        {
            e.Opacity = 1;
            return;
        }
        var move = e.RenderTransform as TranslateTransform;
        if (move == null || move.IsFrozen)
        {
            move = new TranslateTransform();
            e.RenderTransform = move;
        }
        var delay = TimeSpan.FromMilliseconds(delayMs);
        e.Opacity = 0;
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Math.Min(ms, 250)))
        {
            BeginTime = delay,
        });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = delay,
            EasingFunction = Decelerate,
        });
    }

    /// <summary>Fades an element in (e.g. a taskbar item that just appeared).</summary>
    public static void FadeIn(UIElement e, int ms = 250)
    {
        if (!On)
        {
            e.Opacity = 1;
            return;
        }
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Standard });
    }

    /// <summary>Cross-fades an image source: the new one fades in over the old.</summary>
    public static void Swap(UIElement e, Action change, int ms = 220)
    {
        change();
        if (!On) return;
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Standard });
    }

    /// <summary>Rotates a chevron to <paramref name="angle"/> degrees.</summary>
    public static void Rotate(UIElement e, double angle, int ms = 200)
    {
        if (e.RenderTransform is not RotateTransform rot || rot.IsFrozen)
        {
            rot = new RotateTransform();
            e.RenderTransformOrigin = new Point(0.5, 0.5);
            e.RenderTransform = rot;
        }
        if (!On || ms <= 0)
        {
            rot.BeginAnimation(RotateTransform.AngleProperty, null);
            rot.Angle = angle;
            return;
        }
        rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Standard });
    }
}
