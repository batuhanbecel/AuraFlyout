using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using AuraFlyout.Core;

namespace AuraFlyout.Media;

/// <summary>
/// Time-synced lyrics for the playing track from LRCLIB (lrclib.net, a free, open lyrics database):
/// only artist, title, album and length are sent; results are kept for the session.
/// </summary>
public sealed partial class LyricsService
{
    public sealed record Line(TimeSpan At, string Text);

    private static readonly HttpClient Http = CreateClient();
    private readonly Dictionary<string, List<Line>?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private string _key = "";

    /// <summary>The current track's lines, null while none are known.</summary>
    public List<Line>? Lines { get; private set; }

    public event Action? Changed;

    public void Start()
    {
        App.Media.TrackChanged += Load;
        SettingsStore.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.MediaFlyoutLyrics)) Load();
        };
        Load();
    }

    private void Load()
    {
        var m = App.Media;
        string key = $"{m.Artist}|{m.Title}";
        if (!SettingsStore.Current.MediaFlyoutLyrics || !m.HasSession || string.IsNullOrWhiteSpace(m.Title))
        {
            Set(key, null);
            return;
        }
        if (_cache.TryGetValue(key, out var cached))
        {
            Set(key, cached);
            return;
        }
        Set(key, null);
        string artist = m.Artist, title = m.Title, album = m.Album;
        int seconds = (int)Math.Round(m.Duration.TotalSeconds);
        Task.Run(() => FetchAsync(artist, title, album, seconds)).ContinueWith(t =>
        {
            var lines = t.IsCompletedSuccessfully ? t.Result : null;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _cache[key] = lines;
                if (_key == key) Set(key, lines);
            });
        });
    }

    private void Set(string key, List<Line>? lines)
    {
        _key = key;
        if (ReferenceEquals(Lines, lines)) return;
        Lines = lines;
        Changed?.Invoke();
    }

    /// <summary>The line being sung at <paramref name="position"/> and the one after it.</summary>
    public (string? Current, string? Next) At(TimeSpan position)
    {
        if (Lines is not { Count: > 0 } lines) return (null, null);
        int i = lines.FindLastIndex(l => l.At <= position + TimeSpan.FromMilliseconds(250));
        if (i < 0) return ("♪", lines[0].Text);
        return (lines[i].Text.Length == 0 ? "♪" : lines[i].Text, i + 1 < lines.Count ? lines[i + 1].Text : null);
    }

    private static async Task<List<Line>?> FetchAsync(string artist, string title, string album, int seconds)
    {
        try
        {
            string url = "https://lrclib.net/api/get?artist_name=" + Uri.EscapeDataString(artist) +
                         "&track_name=" + Uri.EscapeDataString(title) +
                         (album.Length > 0 ? "&album_name=" + Uri.EscapeDataString(album) : "") +
                         (seconds > 0 ? "&duration=" + seconds : "");
            using var res = await Http.GetAsync(url);
            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                if (Parse(doc.RootElement) is { } exact) return exact;
            }
            // no exact match (other album / length): the best search result that has timings
            string search = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title) + "&artist_name=" + Uri.EscapeDataString(artist);
            using var res2 = await Http.GetAsync(search);
            if (!res2.IsSuccessStatusCode) return null;
            using var list = JsonDocument.Parse(await res2.Content.ReadAsStringAsync());
            foreach (var item in list.RootElement.EnumerateArray())
                if (Parse(item) is { } found) return found;
        }
        catch (Exception ex)
        {
            Log.Warn("Lyrics lookup failed", ex);
        }
        return null;
    }

    private static List<Line>? Parse(JsonElement e)
    {
        if (!e.TryGetProperty("syncedLyrics", out var synced) || synced.ValueKind != JsonValueKind.String) return null;
        var lines = new List<Line>();
        foreach (string raw in (synced.GetString() ?? "").Split('\n'))
        {
            var m = Stamp().Match(raw);
            if (!m.Success) continue;
            var at = TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value)) +
                     TimeSpan.FromSeconds(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            lines.Add(new Line(at, raw[m.Length..].Trim()));
        }
        return lines.Count > 0 ? lines.OrderBy(l => l.At).ToList() : null;
    }

    [GeneratedRegex(@"^\[(\d+):(\d+(?:\.\d+)?)\]")]
    private static partial Regex Stamp();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AuraFlyout/1.0 (Windows media flyout)");
        return c;
    }
}
