using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AuraFlyout.Core;

namespace AuraFlyout.Taskbar;

/// <summary>
/// A transparent child window docked inside the Windows taskbar (Shell_TrayWnd). Several of these
/// (media, system status, Logitech battery, speed test) are lined up by <see cref="TaskbarDock"/>
/// so they never overlap. All of them share the same font, padding and hover surface.
/// </summary>
public abstract class DockWindow : Window
{
    /// <summary>The taskbar clock's font, used by every taskbar item.</summary>
    public static readonly FontFamily TaskbarFont = new("Segoe UI Variable Text, Segoe UI");
    public const double TaskbarFontSize = 12;

    private IntPtr _hwnd;
    private bool _shown;
    private Border? _surface;

    protected DockWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        Focusable = false;
        Width = 10;
        Height = 10;
        Left = -32000;
        Top = -32000;
        UseLayoutRounding = true;
        FontFamily = TaskbarFont;
        ApplyTextStyle();
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        System.Windows.Documents.Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);
        SettingsStore.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.TaskbarFontSize) or nameof(AppSettings.TaskbarBold))
            {
                ApplyTextStyle();
                OnTextStyleChanged();
            }
        };
        _textScale = ThemeService.TextScale;
        ThemeService.Changed += OnSystemTextScale;
    }

    /// <summary>Size and weight inherited by every text of the item (one setting for all items).</summary>
    private void ApplyTextStyle()
    {
        // grows with Settings › Accessibility › Text size, like the taskbar clock, within the bar's height
        FontSize = Math.Clamp(Math.Round(SettingsStore.Current.TaskbarFontSize * ThemeService.TextScale), 9, 18);
        FontWeight = SettingsStore.Current.TaskbarBold ? FontWeights.SemiBold : FontWeights.Normal;
    }

    protected virtual void OnTextStyleChanged() => RequestLayout();

    private double _textScale;

    private void OnSystemTextScale()
    {
        if (Math.Abs(_textScale - ThemeService.TextScale) < 0.001) return;
        _textScale = ThemeService.TextScale;
        ApplyTextStyle();
        OnTextStyleChanged();
    }

    // ---------- shared colours (same instances every time, so unchanged values cause no redraw) ----------

    private static string _brushesFor = "";
    private static SolidColorBrush? _primary, _secondary, _warn, _hot, _good;

    /// <summary>Fluent's text and status colours for the taskbar's theme (caution, critical, success).</summary>
    private static void EnsureBrushes()
    {
        string key = $"{ThemeService.IsTaskbarDark}{ThemeService.HighContrast}";
        if (_primary != null && key == _brushesFor) return;
        _brushesFor = key;
        _primary = Frozen(ThemeService.TaskbarColor("TextFillColorPrimary"));
        _secondary = Frozen(ThemeService.TaskbarColor("TextFillColorSecondary"));
        _warn = Frozen(ThemeService.TaskbarColor("SystemFillColorCaution"));
        _hot = Frozen(ThemeService.TaskbarColor("SystemFillColorCritical"));
        _good = Frozen(ThemeService.TaskbarColor("SystemFillColorSuccess"));
    }

    protected static SolidColorBrush PrimaryBrush { get { EnsureBrushes(); return _primary!; } }
    protected static SolidColorBrush SecondaryBrush { get { EnsureBrushes(); return _secondary!; } }
    protected static SolidColorBrush WarnBrush { get { EnsureBrushes(); return _warn!; } }
    protected static SolidColorBrush HotBrush { get { EnsureBrushes(); return _hot!; } }
    protected static SolidColorBrush GoodBrush { get { EnsureBrushes(); return _good!; } }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public IntPtr Handle => _hwnd;

    /// <summary>Key of this item in <see cref="AppSettings.TaskbarLayout"/> ("media", "status", "logi", "speed").</summary>
    public abstract string Id { get; }

    /// <summary>Extra gap in DIPs added on the item's left side.</summary>
    public abstract int OffsetDip { get; }

    /// <summary>Whether the item has something to show right now.</summary>
    public abstract bool WantsVisible { get; }

    /// <summary>Last position on screen (physical pixels), used to anchor the item's flyout.</summary>
    public Native.RECT ScreenRect { get; private set; }

    public bool IsDocked => _shown;

    /// <summary>
    /// Wraps the item's content in the shared surface: nearly invisible hit area, rounded corners,
    /// the same padding as the media widget and its hover background.
    /// </summary>
    protected void SetDockContent(UIElement inner)
    {
        _surface = new Border
        {
            CornerRadius = new CornerRadius(4), // ControlCornerRadius, like the taskbar buttons
            Padding = new Thickness(8, 4, 8, 4),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = inner,
        };
        _surface.MouseEnter += (_, _) => SetHover(true);
        _surface.MouseLeave += (_, _) => SetHover(false);
        var root = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        root.Children.Add(_surface);
        Content = root;
        Cursor = Cursors.Hand;
    }

    private void SetHover(bool on)
    {
        if (_surface == null) return;
        if (on && SettingsStore.Current.TaskbarWidgetHoverBackground)
            _surface.SetResourceReference(Border.BackgroundProperty, "AuraTaskbarHover");
        else
            _surface.Background = Brushes.Transparent;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // turn the WPF top-level window into a child of the taskbar
        int style = Native.GetWindowLong(_hwnd, Native.GWL_STYLE);
        style = (style & ~Native.WS_POPUP & ~Native.WS_CAPTION) | Native.WS_CHILD;
        Native.SetWindowLong(_hwnd, Native.GWL_STYLE, style);
        WindowEffects.MakeToolWindow(_hwnd);

        TaskbarDock.Register(this);
    }

    /// <summary>Desired width in DIPs.</summary>
    public virtual double MeasureWidth()
    {
        if (Content is not UIElement root) return 0;
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return root.DesiredSize.Width;
    }

    internal void Place(IntPtr taskbar, Native.RECT screen, int clientX, int clientY)
    {
        if (_hwnd == IntPtr.Zero) return;

        // nothing moved: leave the window alone (re-positioning a layered window repaints it = flicker)
        var last = ScreenRect;
        if (_shown && last.Left == screen.Left && last.Top == screen.Top && last.Right == screen.Right && last.Bottom == screen.Bottom &&
            Native.GetParent(_hwnd) == taskbar)
        {
            if (CoveredByShell()) Native.SetWindowPos(_hwnd, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            return;
        }

        if (Native.GetParent(_hwnd) != taskbar) Native.SetParent(_hwnd, taskbar);
        // HWND_TOP keeps us above the taskbar's own XAML content bridge
        Native.SetWindowPos(_hwnd, Native.HWND_TOP, clientX, clientY, screen.Width, screen.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        ScreenRect = screen;
        if (!_shown)
        {
            _shown = true;
            if (Content is UIElement c) Controls.Motion.FadeIn(c, 300); // appears softly, like a new taskbar button
            OnDockVisibilityChanged(true);
        }
    }

    /// <summary>True when one of explorer's own taskbar children sits above us in the z-order.</summary>
    private bool CoveredByShell()
    {
        IntPtr h = _hwnd;
        while ((h = Native.GetWindow(h, 3 /*GW_HWNDPREV*/)) != IntPtr.Zero)
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid != Environment.ProcessId) return true;
        }
        return false;
    }

    /// <summary>
    /// Draws the item anew. A transparent (layered) WPF window can lose what it drew when the display
    /// driver resets, as it does around sign-in, and then stays blank until its size changes: items
    /// whose width changes came back by themselves, fixed-width ones did not. A 1 px resize and back
    /// gives it a new surface.
    /// </summary>
    internal void Repaint()
    {
        if (_hwnd == IntPtr.Zero || !_shown) return;
        var r = ScreenRect;
        const uint flags = Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, r.Width + 1, r.Height, flags);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, r.Width, r.Height, flags);
    }

    internal void Unplace()
    {
        if (_hwnd == IntPtr.Zero || !_shown) return;
        Native.ShowWindow(_hwnd, Native.SW_HIDE);
        _shown = false;
        OnDockVisibilityChanged(false);
    }

    internal void Reparent(IntPtr taskbar)
    {
        if (_hwnd != IntPtr.Zero && taskbar != IntPtr.Zero) Native.SetParent(_hwnd, taskbar);
    }

    protected virtual void OnDockVisibilityChanged(bool visible)
    {
    }

    /// <summary>Asks the dock to lay everything out again (content size changed).</summary>
    protected void RequestLayout() => TaskbarDock.Invalidate();

    /// <summary>
    /// Fixed width for a value slot: the width of <paramref name="template"/> (digits as "0"), so
    /// changing numbers never make the item (or its neighbours) move.
    /// </summary>
    public static double SlotWidth(string template, double size, FontWeight weight) =>
        Math.Ceiling(new FormattedText(template, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(TaskbarFont, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.White, 1.0)
            .WidthIncludingTrailingWhitespace) + 1;

    /// <summary>
    /// Closes the item; when its window was already destroyed with the old taskbar (Close then fails),
    /// still runs the clean-up so the item stops listening to the services.
    /// </summary>
    internal void Release()
    {
        try { Close(); }
        catch { OnClosed(EventArgs.Empty); }
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeService.Changed -= OnSystemTextScale;
        TaskbarDock.Unregister(this);
        base.OnClosed(e);
    }
}
