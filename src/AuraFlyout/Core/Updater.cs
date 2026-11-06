using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AuraFlyout.Core;

/// <summary>
/// Updates from GitHub Releases (the same scheme as TaskbarSpeedTest's updater): the latest release's
/// AuraFlyout exe is downloaded, checked against the release's SHA-256 digest when GitHub provides
/// one, and swapped in for the running exe (a running exe can be renamed, not overwritten), then
/// AuraFlyout restarts. The scheduled task and start-up entry keep pointing at the same path.
/// </summary>
public static class Updater
{
    public sealed record Release(Version Version, string DownloadUrl, string? Sha256, string Page);

    // static initialisers run in source order: Current first, the client's user agent reads it
    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(1, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    /// <summary>The latest release of the configured repository; null when none could be found.</summary>
    public static async Task<Release?> CheckAsync()
    {
        string repo = SettingsStore.Current.UpdateRepo.Trim();
        if (repo.Length == 0) return null;
        try
        {
            using var res = await Http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest");
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? "";
            if (!Version.TryParse(tag, out var version)) return null;
            string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : $"https://github.com/{repo}/releases/latest";
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("AuraFlyout", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                string? digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                return new Release(Normalize(version), asset.GetProperty("browser_download_url").GetString() ?? "",
                    digest != null && digest.StartsWith("sha256:") ? digest[7..] : null, page);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed", ex);
        }
        return null;
    }

    /// <summary>Downloads, verifies and swaps the exe, then restarts AuraFlyout.</summary>
    public static async Task InstallAsync(Release release)
    {
        string exe = Environment.ProcessPath!;
        string download = Path.Combine(Path.GetTempPath(), $"AuraFlyout-{release.Version.ToString(3)}.exe");
        await using (var src = await Http.GetStreamAsync(release.DownloadUrl))
        await using (var dst = File.Create(download))
            await src.CopyToAsync(dst);

        if (release.Sha256 != null)
        {
            string actual;
            await using (var f = File.OpenRead(download))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(f));
            if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(download);
                throw new InvalidDataException("The download does not match the release checksum.");
            }
        }

        string old = exe + ".old";
        File.Move(exe, old, overwrite: true); // allowed while running
        File.Copy(download, exe, overwrite: true);
        File.Delete(download);
        Log.Info($"Updated to {release.Version}");
        App.Instance.ExitApp(restart: () => Process.Start(new ProcessStartInfo(exe, "--tray") { UseShellExecute = true }));
    }

    /// <summary>Removes the previous exe left behind by an update.</summary>
    public static void CleanUp()
    {
        try
        {
            string old = Environment.ProcessPath + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch
        {
            // still locked: next time
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"AuraFlyout/{Current.ToString(3)}");
        return c;
    }
}
