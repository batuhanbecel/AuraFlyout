using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AuraFlyout.Core;
using AuraFlyout.Modules.SystemStatus;
using AuraFlyout.Taskbar;

namespace AuraFlyout.Overlay;

/// <summary>
/// The in-game readout (like an on-screen display): CPU, GPU, RAM, VRAM, ping, FPS and frame time
/// over the game, in a corner of the game's monitor. Click-through and never activated, so it never
/// takes focus or input from the game; kept top-most while it is shown. Works over borderless and
/// windowed games and over "full screen" games running with Windows' full-screen optimisations;
/// a game in true exclusive full screen draws over every window and hides it.
/// </summary>
public sealed class GameOverlay : Window
{
    // browsers and video players are full screen too, but no game: no readout over a film
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "vlc", "mpv", "mpc-hc64", "mpc-be64", "potplayermini64",
        "QMPlay2", "Microsoft.Media.Player", "Video.UI", "PowerPnt", "explorer", "ApplicationFrameHost", "Spotify",
    };

    private readonly Border _box;
    private readonly DispatcherTimer _timer;
    private IntPtr _hwnd;
    private bool _closed;
    private bool _hiddenByHotkey;
    private string _shape = "";
    private readonly Dictionary<string, TextBlock> _values = [];

    private static AppSettings S => SettingsStore.Current;

    public GameOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = -32000;
        Top = -32000;
        Title = "AuraFlyout Overlay";
        FontFamily = DockWindow.TaskbarFont;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        System.Windows.Documents.Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);

        _box = new Border();
        Content = _box;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Evaluate();
        App.Status.Sampled += UpdateValues;
        S.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null && e.PropertyName.StartsWith("Overlay")) Rebuild();
            if (e.PropertyName == nameof(AppSettings.OverlayEnabled) && S.OverlayEnabled && !_timer.IsEnabled && !_closed) _timer.Start();
        };
        ThemeService.Changed += Rebuild;
        Rebuild();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        // click-through, never activated, not in Alt+Tab
        ex |= 0x20 /*WS_EX_TRANSPARENT*/ | 0x80000 /*WS_EX_LAYERED*/ | Native.WS_EX_TOOLWINDOW | 0x08000000 /*WS_EX_NOACTIVATE*/;
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex);
    }

    public void Start()
    {
        Show();
        Hide();
        _timer.Start();
    }

    // closed at exit (or by the session ending): the timer must not show it again
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _timer.Stop();
        base.OnClosed(e);
    }

    /// <summary>The shortcut: hides the readout until pressed again (for this session).</summary>
    public void ToggleByHotkey()
    {
        if (!S.OverlayEnabled)
        {
            S.OverlayEnabled = true;
            _hiddenByHotkey = false;
        }
        else _hiddenByHotkey = !_hiddenByHotkey;
        Evaluate();
    }

    // ---------- when and where ----------

    private void Evaluate()
    {
        bool want = S.OverlayEnabled && !_hiddenByHotkey && (S.OverlayMode == 1 || GameInFront());
        App.Status.OverlayVisible = want;
        if (!want)
        {
            if (IsVisible) Hide();
            if (!S.OverlayEnabled) _timer.Stop(); // switched off: no polling at all until it is on again
            return;
        }
        if (!IsVisible)
        {
            UpdateValues();
            Show();
            Controls.Motion.FadeIn(_box, 250);
        }
        Place();
    }

    private uint _lastPid;
    private bool _lastPidIsGame;

    /// <summary>A full-screen (or borderless) window in front that draws frames and is no browser or player.</summary>
    private bool GameInFront()
    {
        if (!FullscreenDetector.IsFullscreenAppRunning()) return false;
        IntPtr fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid != _lastPid)
        {
            // the process name only changes with the process: looked up once, not twice a second
            _lastPid = pid;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                _lastPidIsGame = !NotGames.Contains(p.ProcessName);
            }
            catch
            {
                _lastPidIsGame = false;
            }
        }
        if (!_lastPidIsGame) return false;
        // without frame counting (no admin rights) every other full-screen window counts as a game
        return !App.Status.FpsAvailable || App.Status.Fps > 0;
    }

    private void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        IntPtr fg = Native.GetForegroundWindow();
        var monitor = fg != IntPtr.Zero ? MonitorHelper.FromWindow(fg) : MonitorHelper.Primary();
        var b = monitor.Bounds;
        Native.GetWindowRect(_hwnd, out var r);
        // 12 px from the corner for the capsule itself; the window around it holds its shadow
        int m = (int)((12 - ShadowPad) * monitor.Scale);
        int x = S.OverlayPosition is 1 or 3 ? b.Right - r.Width - m : b.Left + m;
        int y = S.OverlayPosition is 2 or 3 ? b.Bottom - r.Height - m : b.Top + m;
        // re-asserted every tick: games bring their own window to the top
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    // ---------- content ----------
    //
    // A small dark capsule in the app's (Fluent) language: a glyph, the value and a quiet unit per
    // reading, frames per second first and largest. As a horizontal strip (default) or a list.

    private const double ShadowPad = 14; // room for the soft shadow around the capsule (DIPs)

    // the box is always dark: white text in Fluent's dark-theme shades
    private static readonly Brush Primary = Frozen(Colors.White);
    private static readonly Brush Secondary = Frozen(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Divider = Frozen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Outline = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));

    private void Rebuild()
    {
        double size = Math.Clamp(S.OverlayFontSize, 10, 28);
        bool list = S.OverlayLayout == 1;
        string shape = $"{S.OverlayShowCpu}{S.OverlayShowGpu}{S.OverlayShowRam}{S.OverlayShowVram}{S.OverlayShowPing}{S.OverlayShowFps}{S.OverlayShowFrametime}{S.OverlayShowTemps}{size}{S.OverlayOpacity}{list}{ThemeService.AccentOnDark}";
        if (shape == _shape) return;
        _shape = shape;
        _values.Clear();

        // never fully clear: the outline and shadow alone would look broken
        byte alpha = (byte)Math.Clamp(S.OverlayOpacity, 40, 255);
        _box.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x16, 0x16, 0x16));
        _box.BorderBrush = Outline;
        _box.BorderThickness = new Thickness(1);
        _box.CornerRadius = new CornerRadius(Math.Round(size * 0.75));
        _box.Padding = list ? new Thickness(size * 0.85, size * 0.6, size * 0.85, size * 0.6) : new Thickness(size * 0.85, size * 0.45, size * 0.85, size * 0.45);
        _box.Margin = new Thickness(ShadowPad);
        _box.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Direction = 270, Opacity = 0.4, Color = Colors.Black };

        var accent = Frozen(ThemeService.AccentOnDark);
        var items = new List<(string Label, string Glyph, UIElement Value)>();

        TextBlock Text(string text, double fontSize, Brush brush, FontWeight weight) => new()
        {
            Text = text,
            FontSize = fontSize,
            Foreground = brush,
            FontWeight = weight,
            VerticalAlignment = VerticalAlignment.Center,
        };
        TextBlock Value(string key, string template, double fontSize, FontWeight weight, Brush? brush = null)
        {
            var tb = Text("--", fontSize, brush ?? Primary, weight);
            tb.TextAlignment = TextAlignment.Right;
            tb.MinWidth = DockWindow.SlotWidth(template, fontSize, weight);
            _values[key] = tb;
            return tb;
        }
        // value, then a quieter unit or second reading, on one baseline
        UIElement Reading(UIElement value, params UIElement[] after)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(value);
            foreach (var a in after)
            {
                if (a is FrameworkElement fe) fe.Margin = new Thickness(size * 0.3, 0, 0, 0);
                row.Children.Add(a);
            }
            return row;
        }

        if (S.OverlayShowFps)
        {
            var fps = Value("fps", "0000", Math.Round(size * 1.3), FontWeights.Bold);
            // the list names every row already: no second "FPS" after the number there
            var after = new List<UIElement>();
            if (!list) after.Add(Text("FPS", Math.Round(size * 0.72), Secondary, FontWeights.SemiBold));
            if (S.OverlayShowFrametime) after.Add(Value("ft", "00,0 ms", Math.Round(size * 0.85), FontWeights.Normal, Secondary));
            items.Add(("FPS", "", Reading(fps, [.. after])));
        }
        if (S.OverlayShowCpu)
            items.Add(("CPU", "", S.OverlayShowTemps
                ? Reading(Value("cpu", "100%", size, FontWeights.SemiBold), Value("cputemp", "100°", Math.Round(size * 0.85), FontWeights.Normal, Secondary))
                : Value("cpu", "100%", size, FontWeights.SemiBold)));
        if (S.OverlayShowGpu)
            items.Add(("GPU", "", S.OverlayShowTemps
                ? Reading(Value("gpu", "100%", size, FontWeights.SemiBold), Value("gputemp", "100°", Math.Round(size * 0.85), FontWeights.Normal, Secondary))
                : Value("gpu", "100%", size, FontWeights.SemiBold)));
        if (S.OverlayShowRam)
            items.Add(("RAM", "", Reading(Value("ram", "00,0", size, FontWeights.SemiBold), Text("GB", Math.Round(size * 0.72), Secondary, FontWeights.SemiBold))));
        if (S.OverlayShowVram)
            items.Add(("VRAM", "", Reading(Value("vram", "00,0", size, FontWeights.SemiBold), Text("GB", Math.Round(size * 0.72), Secondary, FontWeights.SemiBold))));
        if (S.OverlayShowPing)
            items.Add(("PING", "", Reading(Value("ping", "000", size, FontWeights.SemiBold), Text("ms", Math.Round(size * 0.72), Secondary, FontWeights.SemiBold))));

        TextBlock Glyph(string glyph, bool first) => new()
        {
            Text = glyph,
            FontFamily = Controls.FluentIcons.Family,
            FontSize = Math.Round(size * (first ? 1.1 : 0.95)),
            Foreground = first ? accent : Secondary,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Panel content;
        if (!list)
        {
            // FPS | CPU | GPU | RAM … in one strip, thin dividers between
            var strip = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                    strip.Children.Add(new Border
                    {
                        Width = 1,
                        Height = Math.Round(size * 1.15),
                        Background = Divider,
                        Margin = new Thickness(size * 0.75, 0, size * 0.75, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                bool fps = items[i].Label == "FPS";
                var chip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var g = Glyph(items[i].Glyph, fps);
                g.Margin = new Thickness(0, 0, size * 0.45, 0);
                chip.Children.Add(g);
                chip.Children.Add(items[i].Value);
                strip.Children.Add(chip);
            }
            content = strip;
        }
        else
        {
            // glyph, name, reading: one row each
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < items.Count; i++)
            {
                bool fps = items[i].Label == "FPS";
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var g = Glyph(items[i].Glyph, fps);
                g.Margin = new Thickness(0, i > 0 ? size * 0.35 : 0, size * 0.55, 0);
                var name = Text(items[i].Label, Math.Round(size * 0.78), Secondary, FontWeights.SemiBold);
                name.Margin = new Thickness(0, i > 0 ? size * 0.35 : 0, size * 1.2, 0);
                if (items[i].Value is FrameworkElement v)
                {
                    v.HorizontalAlignment = HorizontalAlignment.Right;
                    v.Margin = new Thickness(0, i > 0 ? size * 0.35 : 0, 0, 0);
                }
                Grid.SetRow(g, i);
                Grid.SetRow(name, i);
                Grid.SetColumn(name, 1);
                Grid.SetRow(items[i].Value, i);
                Grid.SetColumn(items[i].Value, 2);
                grid.Children.Add(g);
                grid.Children.Add(name);
                grid.Children.Add(items[i].Value);
            }
            content = grid;
        }
        _box.Child = content;
        _box.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateValues();
    }

    private static string Degrees(float? v) => v.HasValue ? (int)Math.Round(v.Value) + "°" : "--";

    private void UpdateValues()
    {
        if (!IsVisible && _values.Count > 0 && IsLoaded) return;
        var st = App.Status;
        void Set(string key, string text, MetricLevel level = MetricLevel.Normal, Brush? normal = null)
        {
            if (!_values.TryGetValue(key, out var tb)) return;
            if (tb.Text != text) tb.Text = text;
            var brush = level switch
            {
                MetricLevel.Hot => HotBrush,
                MetricLevel.Warn => WarnBrush,
                _ => normal ?? Primary,
            };
            if (tb.Foreground != brush) tb.Foreground = brush;
        }
        Set("cpu", SystemStatusService.Percent(st.CpuUsage), st.CpuUsage >= 90 ? MetricLevel.Warn : MetricLevel.Normal);
        Set("cputemp", Degrees(st.CpuTemp), SystemStatusService.TempLevelOf(st.CpuTemp), Secondary);
        Set("gpu", st.GpuUsage.HasValue ? SystemStatusService.Percent(st.GpuUsage.Value) : "--");
        Set("gputemp", Degrees(st.GpuTemp), SystemStatusService.TempLevelOf(st.GpuTemp), Secondary);
        Set("ram", st.RamUsedGb.ToString("0.0"), st.RamUsage >= 90 ? MetricLevel.Warn : MetricLevel.Normal);
        Set("vram", st.VramUsedGb is double v ? v.ToString("0.0") : "--");
        Set("ping", st.PingMs is double p ? Math.Round(p).ToString() : "--");
        bool fps = st.FpsAvailable && st.Fps > 0;
        Set("fps", fps ? st.Fps.ToString() : "--");
        Set("ft", fps ? (1000.0 / st.Fps).ToString("0.0") + " ms" : "", normal: Secondary);
    }

    // Fluent's dark-theme caution / critical colours: the box is always dark
    private static readonly Brush WarnBrush = Frozen(Color.FromRgb(0xFC, 0xE1, 0x00));
    private static readonly Brush HotBrush = Frozen(Color.FromRgb(0xFF, 0x99, 0xA4));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
