using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AuraFlyout.Core;

/// <summary>
/// Light/dark + accent handling. Apps theme follows the setting (or Windows),
/// taskbar theme always follows Windows' "system" mode, like the real taskbar.
/// </summary>
public static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark { get; private set; } = true;
    public static bool IsTaskbarDark { get; private set; } = true;
    public static Color Accent { get; private set; } = Color.FromRgb(0x4C, 0xC2, 0xFF);

    /// <summary>The accent shade Windows uses on dark surfaces (AccentLight2), whatever the app theme.</summary>
    public static Color AccentOnDark { get; private set; } = Color.FromRgb(0x4C, 0xC2, 0xFF);

    // ---- Windows' own preferences (Settings › Accessibility / Personalisation) ----

    /// <summary>Settings › Accessibility › Visual effects › Animation effects.</summary>
    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation && !_reducedMotion;

    /// <summary>Settings › Personalisation › Colours › Transparency effects (off: solid surfaces, like the shell).</summary>
    public static bool TransparencyEnabled { get; private set; } = true;

    /// <summary>A contrast theme is on (Settings › Accessibility › Contrast themes).</summary>
    public static bool HighContrast { get; private set; }

    /// <summary>Settings › Accessibility › Text size, 1.0 – 2.25.</summary>
    public static double TextScale { get; private set; } = 1;

    /// <summary>Settings › Accessibility › Visual effects › "Dismiss notifications after", in seconds.</summary>
    public static int MessageDurationSeconds { get; private set; } = 5;

    private static bool _reducedMotion;
    private static Windows.UI.ViewManagement.UISettings? _ui; // kept alive for its change events

    /// <summary>Raised after the theme or accent colour changed.</summary>
    public static event Action? Changed;

    public static void Initialize()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle
                or UserPreferenceCategory.Accessibility)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
        try
        {
            _ui = new Windows.UI.ViewManagement.UISettings();
            void Later(object? s, object? e) => Application.Current?.Dispatcher.BeginInvoke(Apply);
            _ui.TextScaleFactorChanged += (s, e) => Later(s, e);
            _ui.ColorValuesChanged += (s, e) => Later(s, e);
            _ui.AdvancedEffectsEnabledChanged += (s, e) => Later(s, e);
            _ui.AnimationsEnabledChanged += (s, e) => Later(s, e);
            _ui.MessageDurationChanged += (s, e) => Later(s, e);
        }
        catch (Exception ex)
        {
            Log.Warn("UISettings unavailable", ex);
        }
    }

    public static void Apply()
    {
        bool systemAppsLight = ReadDword("AppsUseLightTheme", 1) == 1;
        IsTaskbarDark = ReadDword("SystemUsesLightTheme", 0) == 0;
        HighContrast = SystemParameters.HighContrast;
        ReadSystemPreferences();

        IsDark = SettingsStore.Current.Theme switch
        {
            1 => false,
            2 => true,
            _ => !systemAppsLight,
        };

        // WPF-UI re-styles Application.MainWindow on theme changes: make sure that is the
        // settings window (Mica) and never one of the flyouts (they manage their own backdrop).
        var app = Application.Current;
        var settingsWindow = app.Windows.OfType<FluentWindow>().FirstOrDefault(w => w.IsLoaded);
        try { app.MainWindow = settingsWindow; } catch { /* ignore */ }
        // The accent is never WPF-UI's own: it lightens the raw accent itself (a pinkish #FF7373 for a
        // red accent instead of Windows' #FF6545), and when it did so after ours (a theme change
        // while the palette could not be read) its controls and ours showed two shades side by side.
        ApplicationThemeManager.Apply(HighContrast ? ApplicationTheme.HighContrast : IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light,
            settingsWindow != null && TransparencyEnabled && !HighContrast ? WindowBackdropType.Mica : WindowBackdropType.None, false);
        Color taskbarAccent = ApplyAccent();

        if (HighContrast)
        {
            Accent = SystemColors.HighlightColor;
            taskbarAccent = SystemColors.HighlightColor;
        }

        var res = Application.Current.Resources;
        if (PerfLog.Enabled) DumpAccentResources(res, "");
        res["AuraAccentBrush"] = Freeze(new SolidColorBrush(Accent));
        // taskbar items follow the taskbar's theme, which can differ from the apps' theme
        res["AuraTaskbarAccentBrush"] = Freeze(new SolidColorBrush(taskbarAccent));
        // the shell's acrylic fallback (transparency off): AcrylicBackgroundFillColorDefault's solid colour
        res["AuraFlyoutSolidBrush"] = Freeze(new SolidColorBrush(HighContrast ? SystemColors.WindowColor
            : IsDark ? Color.FromRgb(0x2C, 0x2C, 0x2C) : Color.FromRgb(0xF9, 0xF9, 0xF9)));
        // cards inside flyouts, as light as the notification centre's cards (measured: ~4 % white)
        res["AuraCardBrush"] = Freeze(new SolidColorBrush(HighContrast ? Colors.Transparent
            : IsDark ? Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));
        // the taskbar's text and hover colours: Fluent's own values for the taskbar's theme
        res["AuraTaskbarForeground"] = Freeze(new SolidColorBrush(TaskbarColor("TextFillColorPrimary")));
        res["AuraTaskbarSecondaryForeground"] = Freeze(new SolidColorBrush(TaskbarColor("TextFillColorSecondary")));
        res["AuraTaskbarHover"] = Freeze(new SolidColorBrush(TaskbarColor("SubtleFillColorSecondary")));

        Changed?.Invoke();
    }

    /// <summary>Development: every reddish brush / colour resource and where it lives.</summary>
    internal static void DumpAccentResources(ResourceDictionary d, string path)
    {
        foreach (var key in d.Keys)
        {
            object? v;
            try { v = d[key]; } catch { continue; }
            Color? c = v switch { SolidColorBrush b => b.Color, Color col => col, _ => null };
            if (c is { } x && x.R > 200 && x.G < 160 && x.B < 160)
                PerfLog.Write($"accent res {path}{d.Source?.ToString() ?? ""} {key} = #{x.R:X2}{x.G:X2}{x.B:X2}{(v is SolidColorBrush sb && sb.IsFrozen ? " frozen" : "")}");
        }
        foreach (var m in d.MergedDictionaries) DumpAccentResources(m, path + "/");
    }

    private static void ReadSystemPreferences()
    {
        TransparencyEnabled = ReadDword("EnableTransparency", 1) == 1;
        try
        {
            var ui = _ui ?? new Windows.UI.ViewManagement.UISettings();
            TextScale = Math.Clamp(ui.TextScaleFactor, 1, 2.25);
            MessageDurationSeconds = (int)Math.Clamp(ui.MessageDuration, 5, 300);
            TransparencyEnabled = ui.AdvancedEffectsEnabled;
            _reducedMotion = !ui.AnimationsEnabled;
        }
        catch
        {
            // keep the registry / defaults
        }
    }

    // ---- Fluent colour tokens for the taskbar's theme (the apps' theme may differ) ----

    private static ResourceDictionary? _darkTokens, _lightTokens;

    /// <summary>
    /// A colour token (TextFillColorPrimary, SystemFillColorCritical, ...) as defined by the Fluent
    /// theme the taskbar uses, straight from WPF-UI's Windows 11 dictionaries; system colours with a
    /// contrast theme.
    /// </summary>
    public static Color TaskbarColor(string token)
    {
        if (HighContrast)
        {
            return token switch
            {
                "SubtleFillColorSecondary" => SystemColors.HighlightColor,
                "TextFillColorSecondary" => SystemColors.GrayTextColor,
                _ => SystemColors.WindowTextColor,
            };
        }
        try
        {
            var dict = IsTaskbarDark
                ? _darkTokens ??= new ResourceDictionary { Source = new Uri("pack://application:,,,/Wpf.Ui;component/Resources/Theme/Dark.xaml") }
                : _lightTokens ??= new ResourceDictionary { Source = new Uri("pack://application:,,,/Wpf.Ui;component/Resources/Theme/Light.xaml") };
            if (dict[token] is Color c) return c;
        }
        catch (Exception ex)
        {
            Log.Warn($"Fluent token {token} unavailable", ex);
        }
        return IsTaskbarDark ? Colors.White : Colors.Black;
    }

    /// <summary>Tint for the classic acrylic backdrop in AABBGGRR order (never fully clear: it would glare).</summary>
    public static uint AcrylicTint()
    {
        byte alpha = (byte)Math.Clamp(SettingsStore.Current.AcrylicOpacity, 0x50, 255);
        uint rgb = IsDark ? 0x202020u : 0xF2F2F2u; // symmetric, so RGB order does not matter
        return ((uint)alpha << 24) | rgb;
    }

    private readonly record struct AccentPalette(Color Dark3, Color Dark2, Color Dark1, Color Light1, Color Light2, Color Light3);

    private static AccentPalette? _lastPalette;
    private static bool _applyingAccent, _accentHooked;

    /// <summary>
    /// Puts Windows' own accent shades into every accent resource (WPF-UI's and ours) and returns
    /// the taskbar's accent. Windows 11 never paints with the raw accent: in dark mode accent
    /// surfaces use AccentLight2 (text Light3), in light mode AccentDark1 (text Dark2); WinUI's dark
    /// resources never use Light1, hence Light2 twice.
    /// </summary>
    private static Color ApplyAccent()
    {
        if (!_accentHooked)
        {
            // WPF-UI re-applying its theme on its own (a FluentWindow's backdrop, say): ours again
            _accentHooked = true;
            ApplicationThemeManager.Changed += (_, _) =>
            {
                if (!_applyingAccent) Application.Current?.Dispatcher.BeginInvoke(() => ApplyAccent());
            };
        }
        _applyingAccent = true;
        try
        {
            var p = ReadAccentPalette() ?? _lastPalette;
            if (p is not { } palette)
            {
                ApplicationAccentColorManager.ApplySystemAccent(); // no palette at all (Windows 10)
                Accent = ApplicationAccentColorManager.SystemAccent;
                AccentOnDark = Accent;
                return Accent;
            }
            _lastPalette = palette;
            if (IsDark) ApplicationAccentColorManager.Apply(palette.Light2, palette.Light2, palette.Light2, palette.Light3);
            else ApplicationAccentColorManager.Apply(palette.Dark1, palette.Dark1, palette.Dark2, palette.Dark3);
            Accent = IsDark ? palette.Light2 : palette.Dark1;
            AccentOnDark = palette.Light2;
            return IsTaskbarDark ? palette.Light2 : palette.Dark1;
        }
        catch (Exception ex)
        {
            Log.Warn("Accent could not be applied", ex);
            return Accent;
        }
        finally
        {
            _applyingAccent = false;
        }
    }

    /// <summary>
    /// The accent shades Windows itself computed (Settings › Personalisation › Colours), from
    /// UISettings or, when that fails (it can during a settings change), the registry copy.
    /// </summary>
    private static AccentPalette? ReadAccentPalette()
    {
        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            Color Get(Windows.UI.ViewManagement.UIColorType type)
            {
                var c = ui.GetColorValue(type);
                return Color.FromRgb(c.R, c.G, c.B);
            }

            return new AccentPalette(
                Get(Windows.UI.ViewManagement.UIColorType.AccentDark3),
                Get(Windows.UI.ViewManagement.UIColorType.AccentDark2),
                Get(Windows.UI.ViewManagement.UIColorType.AccentDark1),
                Get(Windows.UI.ViewManagement.UIColorType.AccentLight1),
                Get(Windows.UI.ViewManagement.UIColorType.AccentLight2),
                Get(Windows.UI.ViewManagement.UIColorType.AccentLight3));
        }
        catch
        {
            // HKCU\…\Explorer\Accent\AccentPalette: Light3, Light2, Light1, accent, Dark1, Dark2, Dark3 (RGBA each)
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
                if (key?.GetValue("AccentPalette") is not byte[] b || b.Length < 28) return null;
                Color At(int i) => Color.FromRgb(b[i * 4], b[i * 4 + 1], b[i * 4 + 2]);
                return new AccentPalette(At(6), At(5), At(4), At(2), At(1), At(0));
            }
            catch
            {
                return null;
            }
        }
    }

    private static int ReadDword(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(name) is int v ? v : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
