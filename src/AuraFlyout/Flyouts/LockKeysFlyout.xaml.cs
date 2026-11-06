using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AuraFlyout.Core;

namespace AuraFlyout.Flyouts;

/// <summary>
/// Shows the state of Caps / Num / Scroll Lock and Insert (and the microphone and keyboard language):
/// one compact line with the state as a short accent bar under the words.
/// </summary>
public partial class LockKeysFlyout : FlyoutWindow
{
    private const double IndicatorOffScale = 0.25;

    private string? _shownName;
    private bool _shownOn;
    private int _version;

    public LockKeysFlyout()
    {
        InitializeComponent();
        // the bar spans about the middle two fifths of the words above it
        Line.SizeChanged += (_, _) => Indicator.Width = Math.Max(24, Math.Round(Line.ActualWidth * 0.4));
    }

    protected override int DurationMs => SettingsStore.Current.LockKeysDuration;
    protected override int PositionIndex => SettingsStore.Current.LockKeysPosition;
    protected override FrameworkElement Surface => Root;

    // what the flyout currently stands for: a lock key by default, or the microphone / keyboard language
    private string _glyphOn = Controls.FluentIcons.Lock, _glyphOff = Controls.FluentIcons.Unlock;
    private string _stateOn = "Lock_OnLine", _stateOff = "Lock_OffLine";
    private string? _stateText;

    public void ShowKey(string name, bool on) =>
        ShowState(name, on, Controls.FluentIcons.Lock, Controls.FluentIcons.Unlock, "Lock_OnLine", "Lock_OffLine", indicator: true);

    /// <summary>
    /// The same card for any on / off state (microphone, keyboard language): title, glyph for each
    /// state, state text keys (or a fixed <paramref name="text"/>), and whether to show the state bar.
    /// </summary>
    public void ShowState(string title, bool on, string glyphOn, string glyphOff, string stateOn, string stateOff,
        bool indicator, string? text = null)
    {
        bool kindChanged = glyphOn != _glyphOn;
        (_glyphOn, _glyphOff, _stateOn, _stateOff, _stateText) = (glyphOn, glyphOff, stateOn, stateOff, text);
        if (kindChanged) _shownName = null; // another kind of card: play it as a fresh one
        Indicator.Visibility = indicator ? Visibility.Visible : Visibility.Collapsed;
        // a fixed text (the keyboard layout's name) is a detail, not a state: quieter
        KeyState.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            text != null ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");

        // A fresh flyout (or another key) starts from the opposite state, so the change itself is what
        // moves. The flyout is still sliding in for its first frames: start the change a moment later.
        bool animate = ThemeService.AnimationsEnabled; // Windows' "Animation effects"
        bool fresh = !IsShown || title != _shownName;
        if (fresh && animate) ApplyState(!on);
        SetKey(title, on, animate, delay: TimeSpan.FromMilliseconds(IsShown ? 0 : 90));
        ShowFlyout();
    }

    /// <summary>Sets the final state at once (previews).</summary>
    internal void SetKey(string name, bool on) => SetKey(name, on, animate: false, TimeSpan.Zero);

    private void SetKey(string name, bool on, bool animate, TimeSpan delay)
    {
        bool changed = on != _shownOn || name != _shownName;
        _shownName = name;
        _shownOn = on;
        KeyName.Text = name;
        KeyState.Text = _stateText ?? L10n.Get(on ? _stateOn : _stateOff);
        int version = ++_version;

        if (!animate || !changed)
        {
            if (!animate) ApplyState(on);
            return;
        }

        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        // the bar: grows out of its middle into the accent when switched on, shrinks back into the dim bar when off
        Animate(IndicatorOn, OpacityProperty, on ? 1 : 0, 220, delay, easeOut);
        IEasingFunction barEase = on ? new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } : easeOut;
        Animate(IndicatorScale, ScaleTransform.ScaleXProperty, on ? 1 : IndicatorOffScale, 320, delay, barEase);

        // state word slides up into place
        Animate(KeyState, OpacityProperty, 1, 220, delay + TimeSpan.FromMilliseconds(40), easeOut, from: 0);
        Animate(KeyStateMove, TranslateTransform.YProperty, 0, 260, delay + TimeSpan.FromMilliseconds(40), easeOut, from: 5);

        // lock: shrinks, swaps glyph at its smallest, then pops back while the shackle swings shut / open
        var shrink = new DoubleAnimation(1, 0.6, TimeSpan.FromMilliseconds(110))
        {
            BeginTime = delay,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        shrink.Completed += (_, _) =>
        {
            if (version != _version) return; // superseded by a newer key press
            KeyIcon.Text = on ? _glyphOn : _glyphOff;
            var pop = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };
            Animate(IconScale, ScaleTransform.ScaleXProperty, 1, 300, TimeSpan.Zero, pop);
            Animate(IconScale, ScaleTransform.ScaleYProperty, 1, 300, TimeSpan.Zero, pop);
            Animate(IconRotate, RotateTransform.AngleProperty, 0, 420, TimeSpan.Zero,
                new ElasticEase { Oscillations = 2, Springiness = 6, EasingMode = EasingMode.EaseOut }, from: on ? -20 : 20);
        };
        IconScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        IconScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    /// <summary>Jumps straight to a state, stopping any running animation.</summary>
    private void ApplyState(bool on)
    {
        Set(IndicatorOn, OpacityProperty, on ? 1 : 0);
        Set(IndicatorScale, ScaleTransform.ScaleXProperty, on ? 1 : IndicatorOffScale);
        Set(KeyState, OpacityProperty, 1);
        Set(KeyStateMove, TranslateTransform.YProperty, 0);
        Set(IconScale, ScaleTransform.ScaleXProperty, 1);
        Set(IconScale, ScaleTransform.ScaleYProperty, 1);
        Set(IconRotate, RotateTransform.AngleProperty, 0);
        KeyIcon.Text = on ? _glyphOn : _glyphOff;
        _shownOn = on;
    }

    /// <summary>Animates from the current (possibly mid-animation) value unless <paramref name="from"/> is given.</summary>
    private static void Animate(IAnimatable target, DependencyProperty dp, double to, double ms, TimeSpan delay,
        IEasingFunction? ease, double? from = null)
    {
        var obj = (DependencyObject)target;
        double start = from ?? (double)obj.GetValue(dp);
        target.BeginAnimation(dp, null);
        obj.SetValue(dp, start); // what shows during the delay
        target.BeginAnimation(dp, new DoubleAnimation(start, to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = delay,
            EasingFunction = ease,
        });
    }

    private static void Set(IAnimatable target, DependencyProperty dp, double value)
    {
        target.BeginAnimation(dp, null);
        ((DependencyObject)target).SetValue(dp, value);
    }
}
