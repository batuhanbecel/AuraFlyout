using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace AuraFlyout.Core;

/// <summary>
/// Device notifications as real Windows notifications: they land in the notification centre, follow
/// Do not disturb / focus and the game / full-screen rules, use the Windows notification sound and
/// can be turned off per app in Settings › System › Notifications, exactly like any other app's.
/// An unpackaged app only needs its AppUserModelID registered under HKCU for that.
/// </summary>
public static class WindowsNotifications
{
    public const string AppId = "AuraFlyout.App";

    private static ToastNotifier? _notifier;
    private static bool _registered, _failed;

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>Registers the app (name and icon shown on the notifications) and claims the id for this process.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppId);
            Directory.CreateDirectory(AppSettings.Folder);
            string icon = Path.Combine(AppSettings.Folder, "notify-icon.png");
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/AuraFlyout;component/Assets/AuraFlyout.png"));
            if (res != null)
            {
                using var fs = File.Create(icon);
                res.Stream.CopyTo(fs);
            }
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId);
            key.SetValue("DisplayName", "AuraFlyout");
            key.SetValue("IconUri", icon);
            key.SetValue("ShowInSettings", 1, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Log.Warn("Windows notifications could not be registered", ex);
            _failed = true;
        }
    }

    /// <summary>
    /// Shows a notification. False when Windows notifications are not available (the caller then uses
    /// the built-in toast); true also when the user switched AuraFlyout's notifications off in Windows.
    /// </summary>
    public static bool Show(string title, string text, string glyph, bool warning)
    {
        Register();
        if (_failed) return false;
        try
        {
            _notifier ??= ToastNotificationManager.CreateToastNotifier(AppId);
            if (NotifierSetting() is { } setting && setting != NotificationSetting.Enabled) return true; // turned off in Windows: respected

            string? logo = GlyphImage(glyph, warning);
            string logoXml = logo == null ? "" :
                $"<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"{SecurityElement.Escape(new Uri(logo).AbsoluteUri)}\"/>";
            string body = string.IsNullOrEmpty(text) ? "" : $"<text>{SecurityElement.Escape(text)}</text>";
            // only a low battery is worth a sound; (dis)connecting already makes Windows' device sound
            string audio = warning ? "" : "<audio silent=\"true\"/>";
            var xml = new XmlDocument();
            xml.LoadXml($"<toast><visual><binding template=\"ToastGeneric\"><text>{SecurityElement.Escape(title)}</text>{body}{logoXml}</binding></visual>{audio}</toast>");
            _notifier.Show(new ToastNotification(xml)
            {
                Group = "devices",
                ExpirationTime = DateTimeOffset.Now.AddHours(warning ? 12 : 1),
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Windows notification failed, using the built-in toast", ex);
            _failed = true;
            return false;
        }
    }

    /// <summary>
    /// Whether the user allows our notifications. Unknown (null) until Windows has seen the app's first
    /// notification: the query fails with "element not found" (0x80070490) for a new unpackaged app.
    /// </summary>
    private static NotificationSetting? NotifierSetting()
    {
        try { return _notifier!.Setting; }
        catch { return null; }
    }

    /// <summary>The device glyph on a round badge (Segoe Fluent Icons), rendered once per look into a PNG.</summary>
    private static string? GlyphImage(string glyph, bool warning)
    {
        if (string.IsNullOrEmpty(glyph)) return null;
        try
        {
            bool dark = ThemeService.IsTaskbarDark; // the notification centre follows the Windows mode
            string name = $"notify-{(int)glyph[0]:X4}-{(warning ? "w" : "n")}-{(dark ? "d" : "l")}.png";
            string path = Path.Combine(AppSettings.Folder, name);
            if (File.Exists(path)) return path;

            const int size = 96;
            Color back = warning ? ThemeService.TaskbarColor("SystemFillColorCritical")
                : dark ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xE8, 0xE8, 0xE8);
            Color fore = warning ? (dark ? Colors.Black : Colors.White) : dark ? Colors.White : Colors.Black;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawEllipse(new SolidColorBrush(back), null, new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0);
                var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(Controls.FluentIcons.Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    size * 0.46, new SolidColorBrush(fore), 1.0);
                dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) enc.Save(fs);
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn("Notification image failed", ex);
            return null;
        }
    }
}
