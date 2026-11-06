using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AuraFlyout.Core;

namespace AuraFlyout.Media;

public sealed record AppDisplayInfo(string Name, ImageSource? Icon);

/// <summary>Turns an AppUserModelID (or exe name) into a friendly name + icon.</summary>
public static class AppInfoResolver
{
    private static readonly Dictionary<string, AppDisplayInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MSEdge"] = "Microsoft Edge",
        ["msedge"] = "Microsoft Edge",
        ["Chrome"] = "Google Chrome",
        ["chrome"] = "Google Chrome",
        ["firefox"] = "Firefox",
        ["308046B0AF4A39CB"] = "Firefox",
        ["Spotify"] = "Spotify",
        ["opera"] = "Opera",
        ["brave"] = "Brave",
        ["vlc"] = "VLC",
        ["foobar2000"] = "foobar2000",
        ["AIMP"] = "AIMP",
        ["MusicBee"] = "MusicBee",
    };

    private static readonly Dictionary<string, string> KnownProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MSEdge"] = "msedge",
        ["Chrome"] = "chrome",
        ["308046B0AF4A39CB"] = "firefox",
    };

    public static AppDisplayInfo Resolve(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return new AppDisplayInfo("", null);
        if (Cache.TryGetValue(appId, out var cached)) return cached;

        string name = Friendly(appId);
        ImageSource? icon = null;

        // Packaged apps (Media Player, Spotify Store, ...)
        if (appId.Contains('!'))
        {
            try
            {
                var info = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(appId);
                if (!string.IsNullOrWhiteSpace(info?.DisplayInfo?.DisplayName))
                    name = info.DisplayInfo.DisplayName;
            }
            catch
            {
                // not resolvable
            }
        }
        else
        {
            string? exe = FindExecutable(appId);
            if (exe != null)
            {
                icon = IconFromExe(exe);
                try
                {
                    string desc = FileVersionInfo.GetVersionInfo(exe).FileDescription ?? "";
                    if (!KnownNames.ContainsKey(Strip(appId)) && !string.IsNullOrWhiteSpace(desc) && desc.Length < 40)
                        name = desc;
                }
                catch
                {
                    // keep friendly name
                }
            }
        }

        var result = new AppDisplayInfo(name, icon);
        Cache[appId] = result;
        return result;
    }

    /// <summary>Logo for packaged apps (async WinRT call).</summary>
    public static async Task<ImageSource?> ResolveIconAsync(string appId)
    {
        if (!appId.Contains('!')) return null;
        try
        {
            var info = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(appId);
            var logo = info.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
            using var stream = await logo.OpenReadAsync();
            using var net = stream.AsStream();
            var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            if (Cache.TryGetValue(appId, out var c)) Cache[appId] = c with { Icon = bmp };
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static void Activate(string appId)
    {
        try
        {
            string proc = ProcessName(appId);
            var p = Process.GetProcessesByName(proc).FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero);
            if (p != null)
            {
                Native.ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                Native.SetForegroundWindow(p.MainWindowHandle);
                return;
            }
            if (appId.Contains('!'))
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appId}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not activate player", ex);
        }
    }

    private static readonly Dictionary<string, ImageSource?> ExeIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The exe's icon, extracted once per path (the mixer asks for it every time it opens).</summary>
    public static ImageSource? IconFromExe(string path)
    {
        if (ExeIcons.TryGetValue(path, out var known)) return known;
        var large = new IntPtr[1];
        try
        {
            if (Native.ExtractIconEx(path, 0, large, null, 1) == 0 || large[0] == IntPtr.Zero) return ExeIcons[path] = null;
            var src = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return ExeIcons[path] = src;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (large[0] != IntPtr.Zero) Native.DestroyIcon(large[0]);
        }
    }

    private static string Strip(string appId) =>
        appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? appId[..^4] : appId;

    private static string ProcessName(string appId)
    {
        if (KnownProcesses.TryGetValue(appId, out var p)) return p;
        if (appId.Contains('!'))
        {
            string tail = appId[(appId.IndexOf('!') + 1)..];
            return tail.Split('.').Last();
        }
        return Path.GetFileNameWithoutExtension(Strip(appId));
    }

    private static string Friendly(string appId)
    {
        string s = Strip(appId);
        if (KnownNames.TryGetValue(s, out var n)) return n;
        if (s.Contains('!'))
        {
            string family = s[..s.IndexOf('!')];
            string pkg = family.Split('_')[0];
            s = pkg.Split('.').Last();
        }
        s = Path.GetFileName(s);
        return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : appId;
    }

    private static string? FindExecutable(string appId)
    {
        try
        {
            string proc = ProcessName(appId);
            foreach (var p in Process.GetProcessesByName(proc))
            {
                string? path = Native.GetProcessPath((uint)p.Id);
                if (path != null) return path;
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }
}
