using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using AuraFlyout.Core;

namespace AuraFlyout.Flyouts;

/// <summary>
/// Base class for every pop-up flyout: borderless, top-most, never steals focus,
/// acrylic backdrop, slide/fade animation, auto-hide (paused while hovered) and stacking.
/// </summary>
public abstract class FlyoutWindow : Window
{
    private static readonly List<FlyoutWindow> Visible = [];

    private readonly DispatcherTimer _hideTimer;
    private IntPtr _hwnd;
    private bool _hiding;
    private bool _animating;
    private bool _animIn;
    private bool _transparentBackdrop;
    private Native.RECT? _anchor;
    private bool _fromTop; // where the flyout currently sits: it slides out towards that edge
    private long _hiddenAt;

    /// <summary>
    /// Panels (status, battery, speed test) open above their taskbar item, take focus and close
    /// when the user clicks elsewhere, like the Windows 11 quick settings. Toasts do not.
    /// </summary>
    protected virtual bool IsPanel => false;

    /// <summary>Opened by a click (taskbar widget, tray icon): acts like a panel until it closes.</summary>
    private bool _clickOpened, _clickPending;

    /// <summary>
    /// The next show comes from a click: the flyout then takes focus and closes on a click elsewhere
    /// (a key press shows it without taking focus and lets it time out).
    /// </summary>
    public void OpenedByClick() => _clickPending = true;

    /// <summary>Shown by a click (stays until a click elsewhere), not by a key.</summary>
    public bool IsOpenByClick => IsShown && _clickOpened;

    /// <summary>Hidden a moment ago: the click that hid it (by taking focus away) is not a re-open.</summary>
    public bool JustHidden => Environment.TickCount64 - _hiddenAt < 300;

    /// <summary>Whether the window can be activated (toasts must never take focus from the app).</summary>
    private void SetActivatable(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        const int WS_EX_NOACTIVATE = 0x08000000;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        int want = on ? ex & ~WS_EX_NOACTIVATE : ex | WS_EX_NOACTIVATE;
        if (want != ex) Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, want);
    }

    /// <summary>Takes focus, closes on a click elsewhere or Esc, does not time out.</summary>
    private bool ActsAsPanel => IsPanel || _clickOpened;

    /// <summary>Development: never activate or auto-hide (used by --backdrop-test).</summary>
    public static bool TestMode { get; set; }

    protected FlyoutWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        Left = -32000;
        Top = -32000;
        UseLayoutRounding = true;
        Focusable = false;
        // one typeface for the whole app: the same as the taskbar items (and the Windows 11 shell)
        FontFamily = Taskbar.DockWindow.TaskbarFont;
        System.Windows.Documents.Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
            NonClientFrameEdges = NonClientFrameEdges.None,
        });

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DurationMs) };
        _hideTimer.Tick += (_, _) =>
        {
            if (IsMouseOver || KeepOpen || ActsAsPanel || TestMode)
                return; // keep it while the pointer is on it
            _hideTimer.Stop();
            HideFlyout();
        };
        Deactivated += (_, _) =>
        {
            if (ActsAsPanel && IsShown && !KeepOpen) HideFlyout();
        };
        PreviewKeyDown += (_, e) =>
        {
            // panels hold the keyboard only to be closed with Esc; no key may press a button
            if (!ActsAsPanel) return;
            if (e.Key == System.Windows.Input.Key.Escape) HideFlyout();
            if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space) e.Handled = true;
        };

        ThemeService.Changed += OnThemeChanged;
        SettingsStore.Current.PropertyChanged += OnSettingChanged;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (IsShown) Reposition(animate: false);
        });
        // Content that arrives later (Bluetooth devices, test results) changes the size. The window
        // already keeps its taskbar-side edge (KeepEdge); once WPF has resized it, a new width is
        // centred again (reading the size in SizeChanged itself gives the old one).
        SizeChanged += (_, e) =>
        {
            if (PerfLog.Enabled) PerfLog.Write($"{GetType().Name} size {e.PreviousSize.Height:F0} -> {e.NewSize.Height:F0} animating={_animating} warmup={_warmup}");
            if (!IsShown || _animating) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (IsShown && !_animating) Reposition(animate: false);
            });
        };
    }

    /// <summary>Visible duration in milliseconds.</summary>
    protected abstract int DurationMs { get; }

    /// <summary>0 bottom-center, 1 bottom-left, 2 bottom-right, 3 top-center, 4 top-left, 5 top-right</summary>
    protected abstract int PositionIndex { get; }

    /// <summary>Element whose background is swapped between transparent (acrylic) and solid.</summary>
    protected abstract FrameworkElement Surface { get; }

    /// <summary>While true the flyout does not auto-hide (e.g. user is dragging a slider).</summary>
    protected bool KeepOpen { get; set; }

    public bool IsShown => IsVisible && !_hiding;

    /// <summary>Raised when the flyout appears (true) or has finished hiding (false).</summary>
    public event Action<bool>? ShownChanged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.MakeToolWindow(_hwnd, noActivate: !IsPanel || TestMode);
        WindowEffects.EnableSystemShadow(_hwnd);
        ApplyLook();
        // DWM draws the system acrylic only while the window frame is "active", otherwise a flat dark
        // fallback: keep the frame active for good. The filter must run before WPF, whose WindowChrome
        // handles WM_NCACTIVATE itself, hence a window subclass rather than an HwndSource hook.
        MarkFrameActive();
        _subclass = SubclassProc;
        SetWindowSubclass(_hwnd, _subclass, 1, IntPtr.Zero);
    }

    private delegate IntPtr SubclassProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);
    private SubclassProcDelegate? _subclass; // keep alive

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProcDelegate proc, nuint id, IntPtr data);

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    private const int WM_WINDOWPOSCHANGING = 0x0046;

    private IntPtr SubclassProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        // every "frame inactive" notification is turned into "active": the acrylic never goes dull
        if (msg == Native.WM_NCACTIVATE && wParam == IntPtr.Zero)
            return DefSubclassProc(hWnd, msg, new IntPtr(1), lParam);
        if (msg == WM_WINDOWPOSCHANGING) KeepEdge(lParam);
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// WPF resizes the window (SizeToContent) by keeping its top-left corner, so content arriving
    /// after the flyout appeared would push its bottom over the taskbar. A flyout on a bottom
    /// taskbar keeps its bottom edge instead and grows upwards, in the same move as the resize.
    /// Only WPF's own resizes (no position given) are turned; positions set here are kept.
    /// </summary>
    private void KeepEdge(IntPtr lParam)
    {
        var p = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if ((p.flags & Native.SWP_NOSIZE) != 0 || (p.flags & Native.SWP_NOMOVE) == 0) return;
        if (_fromTop || !IsVisible || !Native.GetWindowRect(_hwnd, out var r)) return;
        if (p.cx == r.Width && p.cy == r.Height) return;
        p.x = r.Left;
        p.y = r.Bottom - p.cy;
        p.flags &= ~Native.SWP_NOMOVE;
        System.Runtime.InteropServices.Marshal.StructureToPtr(p, lParam, false);
    }

    public IntPtr Hwnd => _hwnd;

    private void MarkFrameActive() => Native.SendMessage(_hwnd, Native.WM_NCACTIVATE, new IntPtr(1), new IntPtr(-1));

    protected virtual void OnThemeChanged() => ApplyLook();

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.Backdrop) or nameof(AppSettings.AcrylicOpacity) or nameof(AppSettings.Theme))
            ApplyLook();
        if (e.PropertyName == nameof(AppSettings.FlyoutScale))
            ApplyScale();
    }

    private void ApplyLook()
    {
        if (_hwnd == IntPtr.Zero) return;
        int mode = SettingsStore.Current.Backdrop;
        // Windows' "Transparency effects" off or a contrast theme: solid, like the shell's own flyouts
        if (!ThemeService.TransparencyEnabled || ThemeService.HighContrast) mode = 2;
        _transparentBackdrop = WindowEffects.ApplyBackdrop(this, mode);
        if (Surface is System.Windows.Controls.Border border)
        {
            if (ThemeService.HighContrast)
            {
                border.BorderThickness = new Thickness(1);
                border.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, SystemColors.WindowTextBrushKey);
            }
            else
            {
                border.BorderThickness = new Thickness(0);
            }
            if (_transparentBackdrop && mode == 0)
            {
                // The shell's own flyouts (notification centre, calendar, quick settings) put a strong
                // tint + luminosity layer over the raw acrylic. Measured against them, the raw DWM material
                // plus #121212 (dark) / #F9F9F9 (light) at ~75 % gives the same result; the density
                // setting (Pro) moves that tint, 192 = Windows.
                byte a = (byte)Math.Clamp(SettingsStore.Current.AcrylicOpacity, 0, 255);
                border.Background = new SolidColorBrush(ThemeService.IsDark ? Color.FromArgb(a, 0x12, 0x12, 0x12) : Color.FromArgb(a, 0xF9, 0xF9, 0xF9));
            }
            else if (_transparentBackdrop)
                border.Background = new SolidColorBrush(ThemeService.IsDark ? Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            else
                border.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "AuraFlyoutSolidBrush");
        }
        ApplyScale();
    }

    private void ApplyScale()
    {
        // own size setting × Settings › Accessibility › Text size (the shell's flyouts grow with it too)
        double s = Math.Clamp(SettingsStore.Current.FlyoutScale, 75, 150) / 100.0 * ThemeService.TextScale;
        if (Content is FrameworkElement fe)
            fe.LayoutTransform = Math.Abs(s - 1) < 0.001 ? Transform.Identity : new ScaleTransform(s, s);
    }

    /// <summary>Shows the flyout (or extends its visible time when already open).</summary>
    public void ShowFlyout()
    {
        _hideTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(500, DurationMs));
        _hideTimer.Stop();
        _hideTimer.Start();

        bool click = _clickPending;
        _clickPending = false;
        if (IsShown)
        {
            // already up (shown by a key press): a click makes it stay and take focus
            if (click && !_clickOpened)
            {
                _clickOpened = true;
                SetActivatable(true);
                if (_animating) _focusPending = !TestMode;
                else TakeFocus();
            }
            return;
        }

        // a key press after a click-open that is still closing goes back to a plain toast
        if (click || !_animating) _clickOpened = click;
        SetActivatable(ActsAsPanel && !TestMode);
        _hiding = false;
        if (!Visible.Contains(this)) Visible.Add(this);

        // fill the content first: the slide is planned with the final size (never ends half open)
        ShownChanged?.Invoke(true);
        bool fresh = !IsVisible || _warmup; // (still being prepared counts as hidden)
        PerfLog.Write($"{GetType().Name} show (fresh={fresh})");
        if (fresh)
        {
            // A hidden window first lays out and draws its (new) content at its final spot, cloaked
            // (DWM composes it, WPF draws it, nothing shows): all of it is on the screen there, so
            // WPF's first frame covers the whole window. Parts past the screen edge would not be
            // drawn and show up empty once they slide into view. (Under every other window was not
            // enough: Windows keeps the desktop at the bottom, so it showed wherever only the
            // desktop was behind it.)
            _warmup = true;
            _focusPending = ActsAsPanel && !TestMode; // taking focus raises a window over everything: once it slides
            MarkFrameActive(); // the acrylic is live from the very first frame
            if (!IsVisible) Show(); // where the last close (or Prepare) left it: behind the taskbar / off screen
            Cloak(true);            // before it moves to its spot
        }
        UpdateLayout();
        PerfLog.Write($"{GetType().Name} after layout");
        Reposition(animate: true);
        PerfLog.Write($"{GetType().Name} placed");
        if (!fresh) _focusPending = ActsAsPanel && !TestMode; // re-opened while closing: focus once it is out too
    }

    private static readonly IntPtr HWND_BOTTOM = new(1);
    private bool _focusPending, _cloaked;

    /// <summary>A cloaked window is composed by DWM (WPF keeps drawing it) but not shown.</summary>
    private void Cloak(bool on)
    {
        if (_hwnd == IntPtr.Zero || _cloaked == on) return;
        int value = on ? 1 : 0;
        Native.DwmSetWindowAttribute(_hwnd, 13 /*DWMWA_CLOAK*/, ref value, sizeof(int));
        _cloaked = on;
    }

    private void TakeFocus()
    {
        _focusPending = false;
        if (!ActsAsPanel || TestMode || !IsShown) return;
        Native.SetForegroundWindow(_hwnd);
        Activate();
        // keep focus on the panel itself, never on one of its buttons
        System.Windows.Input.Keyboard.ClearFocus();
        System.Windows.Input.FocusManager.SetFocusedElement(this, null);
        // activating raised it over the taskbar: back under it (the taskbar keeps the shadow's edge)
        UnderTaskbar();
    }

    /// <summary>
    /// Shows the flyout; when <paramref name="anchor"/> is set (and the flyout is not already up)
    /// it opens over that taskbar item instead of at its configured position.
    /// </summary>
    protected void ShowFlyout(Native.RECT? anchor)
    {
        if (!IsShown) _anchor = anchor;
        ShowFlyout();
    }

    /// <summary>Opens the panel above a taskbar item (screen rect in physical pixels).</summary>
    public void ShowAt(Native.RECT anchor)
    {
        _anchor = anchor;
        if (IsShown) Reposition(animate: false);
        ShowFlyout();
    }

    /// <summary>
    /// Toggle for taskbar clicks. Clicking the taskbar item first deactivates (and hides) an open
    /// panel, so a re-open right after that hide is ignored.
    /// </summary>
    public void ToggleAt(Native.RECT anchor)
    {
        if (IsShown) HideFlyout();
        else if (Environment.TickCount64 - _hiddenAt > 300) ShowAt(anchor);
    }

    /// <summary>Toggle helper used by tray / taskbar clicks.</summary>
    public void ToggleFlyout()
    {
        if (IsShown) HideFlyout();
        else ShowFlyout();
    }

    public void HideFlyout()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        _hiddenAt = Environment.TickCount64;
        _hideTimer.Stop();
        Visible.Remove(this);
        // back behind the taskbar, from wherever it is (an opening slide turns around)
        StartAnimation(animIn: false);
    }

    /// <summary>
    /// Creates the window (and lays out its content) ahead of the first open, so a click only has
    /// to show it.
    /// </summary>
    public void Prepare()
    {
        if (_hwnd != IntPtr.Zero || IsVisible) return;
        double p0 = PerfLog.Now;
        new WindowInteropHelper(this).EnsureHandle();
        PerfLog.Write($"{GetType().Name} prepare: handle +{PerfLog.Now - p0:F1}");
        // The first Show builds WPF's whole render side (~100 ms): done once here, unseen, under every
        // window and away from the screens, then hidden again.
        Native.SetWindowPos(_hwnd, HWND_BOTTOM, -32000, -32000, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _warmup = true;
        Show();
        PerfLog.Write($"{GetType().Name} prepare: shown +{PerfLog.Now - p0:F1}");
        int frames = 0;
        TimeSpan last = TimeSpan.Zero;
        void OnFrame(object? sender, EventArgs e)
        {
            var now = e is RenderingEventArgs re ? re.RenderingTime : TimeSpan.Zero;
            if (now == last) return;
            last = now;
            if (++frames < 2) return;
            CompositionTarget.Rendering -= OnFrame;
            if (_animating || _hiding || Visible.Contains(this)) return; // opened meanwhile
            _warmup = false;
            Hide();
        }
        CompositionTarget.Rendering += OnFrame;
    }

    // ---------- Windows 11 flyout motion ----------
    //
    // Like the shell's own flyouts (Start, quick settings, notification centre) the flyout moves as one
    // piece: DWM moves the window with its acrylic, content, border and shadow together, so no frame
    // ever shows them apart and nothing has to be redrawn while it moves. A flyout on the taskbar's
    // side rises from behind the taskbar: while it slides, the window sits right under the taskbar in
    // the z-order and the taskbar covers what is not out yet (a window region cannot do that: DWM's
    // acrylic ignores it). The frames come from FlyoutMotion, in step with the screen.

    private int _x, _rest, _hidden; // left; resting bottom (top for a top taskbar); hidden top (bottom)
    private IntPtr _below;          // the taskbar on the flyout's monitor (it slides under that one)
    private FlyoutMotion.Slide? _slide;
    private bool _warmup;
    private int _warmupFrames;
    private TimeSpan _lastFrame;

    /// <summary>0 = behind the taskbar / past the screen edge, 1 = in place.</summary>
    private double Shown => _slide?.Shown ?? (IsVisible && !_warmup ? 1 : 0);

    /// <summary>The window's top for <paramref name="shown"/> (0..1) with its current height.</summary>
    private int TopFor(double shown, int height)
    {
        int restTop = _fromTop ? _rest : _rest - height;
        int hiddenTop = _fromTop ? _hidden - height : _hidden;
        return (int)Math.Round(hiddenTop + (restTop - hiddenTop) * shown);
    }

    /// <summary>Puts the window at <paramref name="shown"/> (0..1) from the UI thread.</summary>
    private void Step(double shown)
    {
        if (!Native.GetWindowRect(_hwnd, out var r)) return;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, _x, TopFor(shown, r.Height), 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
    }

    /// <summary>
    /// Right under the taskbar in the z-order (over every other window): the taskbar covers the part
    /// that is not out yet. Only this window moves in the z-order; the taskbar is not touched.
    /// </summary>
    private void UnderTaskbar()
    {
        IntPtr after = _below != IntPtr.Zero && Native.IsWindow(_below) ? _below : Native.HWND_TOPMOST;
        Native.SetWindowPos(_hwnd, after, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Restarts the auto-hide countdown (called on user interaction).</summary>
    protected void Touch()
    {
        if (!IsShown) return;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void Reposition(bool animate)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_anchor is { } a)
        {
            RepositionAnchored(a, animate);
            return;
        }
        var monitor = MonitorHelper.ForFlyouts();
        Native.GetWindowRect(_hwnd, out var r);
        int w = r.Width, h = r.Height;
        var wa = monitor.WorkArea;
        int margin = (int)(12 * monitor.Scale);
        int pos = Math.Clamp(PositionIndex, 0, 5);

        int x = pos switch
        {
            1 or 4 => wa.Left + margin,
            2 or 5 => wa.Right - w - margin,
            _ => wa.Left + (wa.Width - w) / 2,
        };

        // stack on top of other flyouts that share the same anchor
        int stack = 0;
        foreach (var other in Visible)
        {
            if (other == this) break;
            if (other.PositionIndex == pos && other._anchor == null && other._hwnd != IntPtr.Zero && Native.GetWindowRect(other._hwnd, out var or))
                stack += or.Height + (int)(8 * monitor.Scale);
        }

        bool top = pos >= 3;
        int y = top ? wa.Top + margin + stack : wa.Bottom - h - margin - stack;
        Place(monitor, x, y, h, top, animate);
    }

    /// <summary>Centred over the taskbar item, just above (or below, for a top taskbar) the taskbar.</summary>
    private void RepositionAnchored(Native.RECT a, bool animate)
    {
        var centre = new Native.POINT { X = (a.Left + a.Right) / 2, Y = (a.Top + a.Bottom) / 2 };
        IntPtr hmon = Native.MonitorFromPoint(centre, Native.MONITOR_DEFAULTTONEAREST);
        var monitor = MonitorHelper.GetAll().FirstOrDefault(m => m.Handle == hmon) ?? MonitorHelper.Primary();
        Native.GetWindowRect(_hwnd, out var r);
        int w = r.Width, h = r.Height;
        var wa = monitor.WorkArea;
        int margin = (int)(12 * monitor.Scale);

        int x = Math.Clamp(centre.X - w / 2, wa.Left + margin, Math.Max(wa.Left + margin, wa.Right - w - margin));
        bool taskbarOnTop = a.Top < (monitor.Bounds.Top + monitor.Bounds.Bottom) / 2;
        int y = taskbarOnTop ? wa.Top + margin : wa.Bottom - h - margin;
        Place(monitor, x, y, h, taskbarOnTop, animate);
    }

    /// <summary>
    /// Where the flyout rests (<paramref name="y"/>) and where it hides: just past the work area's
    /// edge on its side, under that monitor's taskbar. With <paramref name="animate"/> it slides in.
    /// </summary>
    private void Place(MonitorInfo monitor, int x, int y, int height, bool top, bool animate)
    {
        _fromTop = top;
        _x = x;
        _rest = top ? y : y + height;
        _hidden = top ? monitor.WorkArea.Top : monitor.WorkArea.Bottom;
        _below = TaskbarOn(monitor);
        if (_slide is { } s)
        {
            // a running slide simply goes on towards the new spot
            s.X = _x;
            s.Rest = _rest;
            s.Hidden = _hidden;
            s.FromTop = _fromTop;
        }
        if (animate)
        {
            StartAnimation(animIn: true);
            return;
        }
        if (!_animating) Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
    }

    /// <summary>The taskbar window on that monitor (main or secondary), or zero (none, or auto-hidden).</summary>
    private static IntPtr TaskbarOn(MonitorInfo monitor)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            if (Native.GetClassName(h) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" &&
                Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST) == monitor.Handle)
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private void StartAnimation(bool animIn)
    {
        double from = Shown;            // turns around from wherever a running slide is
        CancelSlide();
        _animIn = animIn;
        _animating = true;
        if (!animIn)
        {
            _warmup = false;
            _focusPending = false;
            if (!ThemeService.AnimationsEnabled)
            {
                Finish();               // Windows' "Animation effects" off: gone at once
                return;
            }
            BeginSlide(from);
            return;
        }
        if (_warmup)
        {
            // laid out and drawn at its final spot, under every window (see ShowFlyout): two frames
            // for WPF to present it, then out from behind the taskbar
            if (Native.GetWindowRect(_hwnd, out var r))
                Native.SetWindowPos(_hwnd, IntPtr.Zero, _x, TopFor(1, r.Height), 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
            _warmupFrames = 2;
            _lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering -= OnWarmupFrame;
            CompositionTarget.Rendering += OnWarmupFrame;
            return;
        }
        BeginSlide(from);
    }

    private void OnWarmupFrame(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs re ? re.RenderingTime : TimeSpan.Zero;
        if (now == _lastFrame) return; // raised more than once for the same frame
        _lastFrame = now;
        if (--_warmupFrames > 0) return;
        CompositionTarget.Rendering -= OnWarmupFrame;
        if (!_warmup || !_animIn) return;
        _warmup = false;
        if (!ThemeService.AnimationsEnabled)
        {
            UnderTaskbar();
            Cloak(false);
            if (_focusPending) TakeFocus();
            EndOpen();
            return;
        }
        BeginSlide(0);
    }

    /// <summary>Hands the slide to FlyoutMotion; the UI thread only sets it up and finishes it.</summary>
    private void BeginSlide(double from)
    {
        Step(from);
        UnderTaskbar();
        Cloak(false); // behind the taskbar now: shown from here on
        double duration = _animIn ? 330 * (1 - from) : 190 * from;
        var slide = new FlyoutMotion.Slide
        {
            Hwnd = _hwnd,
            In = _animIn,
            From = from,
            DurationMs = duration,
            X = _x,
            Rest = _rest,
            Hidden = _hidden,
            FromTop = _fromTop,
            Done = s => Dispatcher.BeginInvoke(() => SlideDone(s)),
        };
        slide.Shown = from;
        _slide = slide;
        FlyoutMotion.Start(slide);
        if (PerfLog.Enabled)
        {
            bool topmost = (Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE) & 0x8 /*WS_EX_TOPMOST*/) != 0;
            PerfLog.Write($"{GetType().Name} slide {(_animIn ? "in" : "out")} from {from:F2} topmost={topmost} underTaskbar={Native.GetWindow(_hwnd, 3 /*GW_HWNDPREV*/) == _below}");
        }
    }

    private void CancelSlide()
    {
        CompositionTarget.Rendering -= OnWarmupFrame;
        if (_slide == null) return;
        FlyoutMotion.Cancel(_hwnd);
        _slide = null;
    }

    private void SlideDone(FlyoutMotion.Slide s)
    {
        if (s != _slide || s.Cancelled) return; // a newer slide took over
        _slide = null;
        if (s.In) EndOpen();
        else Finish();
    }

    /// <summary>
    /// In place. It stays right under the taskbar (over every other window), so nothing changes in
    /// the last frame: raising it would uncover the bit of its shadow the taskbar hides.
    /// </summary>
    private void EndOpen()
    {
        _animating = false;
        Step(1);
        // Taking focus raises a window over everything: only now, when nothing of it is behind the
        // taskbar any more (during the slide it showed the hidden part over the taskbar for a frame)
        if (_focusPending) TakeFocus();
        PerfLog.Write($"{GetType().Name} open");
        // a width that changed while sliding is centred again once WPF has resized the window
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (IsShown && !_animating) Reposition(animate: false);
        });
    }

    /// <summary>Behind the taskbar: hidden there, so the next open starts from the same place.</summary>
    private void Finish()
    {
        CancelSlide();
        _animating = false;
        _warmup = false;
        Hide();
        Cloak(false);
        _hiding = false;
        _clickOpened = false;
        SetActivatable(IsPanel && !TestMode); // a toast again until the next click
        if (!IsPanel) _anchor = null; // the next key press uses the configured position again
        PerfLog.Write($"{GetType().Name} closed");
        OnHidden();
        ShownChanged?.Invoke(false);
    }

    protected virtual void OnHidden()
    {
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // flyouts live for the whole app lifetime; only hide them
        if (!App.IsShuttingDown)
        {
            e.Cancel = true;
            HideFlyout();
        }
        base.OnClosing(e);
    }
}
