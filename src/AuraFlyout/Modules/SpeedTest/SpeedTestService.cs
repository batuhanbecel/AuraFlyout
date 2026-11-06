using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using AuraFlyout.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace AuraFlyout.Modules.SpeedTest;

/// <summary>History row for the flyout.</summary>
public sealed record SpeedHistoryItem(SpeedResult Result, string When, string Down, string Up, string Ping);

/// <summary>
/// Runs TaskbarSpeedTest's measurement, keeps the last 20 results, runs automatic tests and
/// exposes everything the taskbar widget and the flyout need.
/// </summary>
public sealed partial class SpeedTestService : ObservableObject
{
    public const int HistoryLength = 20;
    private const string LegacyKey = @"Software\TaskbarSpeedTest";

    private readonly SpeedTestEngine _engine = new();
    private readonly DispatcherTimer _timer;
    private readonly DateTime _started = DateTime.UtcNow;
    private DateTime _lastAttempt = DateTime.MinValue;
    private readonly List<SpeedResult> _history = [];

    [ObservableProperty] public partial TestStatus? Status { get; private set; }
    [ObservableProperty] public partial bool IsRunning { get; private set; }

    public ObservableCollection<SpeedHistoryItem> History { get; } = [];

    public event Action? Updated;

    private static AppSettings S => SettingsStore.Current;

    public SpeedTestService()
    {
        _engine.Changed += st => Application.Current?.Dispatcher.BeginInvoke(() => OnStatus(st));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => MaybeAutoTest();
    }

    public void Start()
    {
        MigrateLegacy();
        foreach (var s in S.SpeedHistory)
            if (SpeedResult.Parse(s) is { } r) _history.Add(r);
        RebuildHistory();
        _timer.Start();
        L10n.LanguageChanged += () =>
        {
            RebuildHistory();
            Updated?.Invoke();
        };
        S.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.SpeedUnit)) RebuildHistory();
            if (e.PropertyName != null && e.PropertyName.StartsWith("Speed")) Updated?.Invoke();
        };
    }

    /// <summary>The values to show: the running test, or the last one that went through.</summary>
    public SpeedResult? Latest =>
        Status != null && (Status.Running || Status.Result.Complete) ? Status.Result : _history.FirstOrDefault();

    public IReadOnlyList<SpeedResult> Results => _history;

    public void Toggle()
    {
        if (_engine.Running) _engine.Cancel();
        else StartTest();
    }

    public void StartTest()
    {
        if (_engine.Running) return;
        _lastAttempt = DateTime.UtcNow;
        _engine.Seconds = Math.Clamp(S.SpeedSeconds, 3, 30);
        _engine.Start();
        IsRunning = true;
    }

    public void Cancel() => _engine.Cancel();

    private void OnStatus(TestStatus st)
    {
        Status = st;
        IsRunning = st.Running;
        if (!st.Running && st.Result.Complete)
        {
            _history.Insert(0, st.Result);
            if (_history.Count > HistoryLength) _history.RemoveRange(HistoryLength, _history.Count - HistoryLength);
            SaveHistory();
            RebuildHistory();
        }
        Updated?.Invoke();
    }

    /// <summary>
    /// Runs a test when the last one is older than the chosen interval. Waits a minute after start,
    /// and skips while something full screen is in front or the network is down.
    /// </summary>
    private void MaybeAutoTest()
    {
        if (!S.SpeedEnabled || S.SpeedAutoTestHours <= 0 || _engine.Running) return;
        var now = DateTime.UtcNow;
        if (now - _started < TimeSpan.FromMinutes(1)) return;
        var last = _history.Count > 0 ? _history[0].Time : DateTime.MinValue;
        if (now - last < TimeSpan.FromHours(S.SpeedAutoTestHours)) return;
        if (now - _lastAttempt < TimeSpan.FromMinutes(30)) return; // failed or stopped not long ago
        if (FullscreenDetector.IsFullscreenAppRunning() || !NetworkInterface.GetIsNetworkAvailable()) return;
        Log.Info("automatic speed test");
        StartTest();
    }

    public void ClearHistory()
    {
        _history.Clear();
        SaveHistory();
        RebuildHistory();
        Updated?.Invoke();
    }

    private void SaveHistory() => S.SpeedHistory = _history.Take(HistoryLength).Select(r => r.Serialize()).ToList();

    private void RebuildHistory()
    {
        // the panel's table carries the units in its header, like TaskbarSpeedTest's
        History.Clear();
        foreach (var r in _history)
            History.Add(new SpeedHistoryItem(r, When(r.Time), Speed(r.DownMbps), Speed(r.UpMbps), Ms(r.PingMs)));
    }

    /// <summary>"14:32", "Yesterday 14:32", "28 Oct 14:32".</summary>
    public static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        string time = local.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (local.Date == DateTime.Today) return time;
        if (local.Date == DateTime.Today.AddDays(-1)) return L10n.Get("Ago_Yesterday") + " " + time;
        return local.ToString("d MMM", CultureInfo.CurrentCulture) + " " + time;
    }

    // ---------- text ----------

    public static string UnitName => S.SpeedUnit == 1 ? "MB/s" : "Mbps";

    /// <summary>"8.4", "245", "1250"; one decimal below 10.</summary>
    public static string Speed(double mbps)
    {
        if (mbps < 0) return "–";
        double v = S.SpeedUnit == 1 ? mbps / 8 : mbps;
        return v.ToString(v < 9.95 ? "0.0" : "0", CultureInfo.CurrentCulture);
    }

    public static string SpeedWithUnit(double mbps) => Speed(mbps) + " " + UnitName;

    public static string Ms(double ms) => ms < 0 ? "–" : ms.ToString(ms < 9.95 ? "0.0" : "0", CultureInfo.CurrentCulture);

    public static string Ago(DateTime utc)
    {
        if (utc == DateTime.MinValue) return "";
        var t = DateTime.UtcNow - utc;
        if (t.TotalMinutes < 1) return L10n.Get("Ago_Now");
        if (t.TotalMinutes < 60) return L10n.Format("Ago_Min", (int)t.TotalMinutes);
        if (t.TotalHours < 24) return L10n.Format("Ago_Hour", (int)t.TotalHours);
        if (t.TotalDays < 2) return L10n.Get("Ago_Yesterday");
        return L10n.Format("Ago_Days", (int)t.TotalDays);
    }

    public string LastAsText()
    {
        var r = _history.FirstOrDefault();
        if (r == null) return "";
        return $"{L10n.Get("Speed_Download")} {SpeedWithUnit(r.DownMbps)} · {L10n.Get("Speed_Upload")} {SpeedWithUnit(r.UpMbps)} · " +
               $"Ping {Ms(r.PingMs)} ms · Jitter {Ms(r.JitterMs)} ms · {r.Server} · {r.Time.ToLocalTime():g}";
    }

    /// <summary>Values in Mbps and ms with a dot as decimal separator, for spreadsheets.</summary>
    public string HistoryAsCsv()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("time,download_mbps,upload_mbps,ping_ms,jitter_ms,server,isp,link\r\n");
        foreach (var r in _history)
            sb.Append(string.Join(",", r.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", ci), r.DownMbps.ToString("0.0", ci),
                r.UpMbps.ToString("0.0", ci), r.PingMs.ToString("0.0", ci), r.JitterMs.ToString("0.0", ci),
                Csv(r.Server), Csv(r.Isp), Csv(r.Link))).Append("\r\n");
        return sb.ToString();
    }

    private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    /// <summary>First start: take over TaskbarSpeedTest's saved results.</summary>
    private static void MigrateLegacy()
    {
        if (S.SpeedHistory.Count > 0) return;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(LegacyKey);
            if (k?.GetValue("History") is string h && h.Length > 0)
            {
                S.SpeedHistory = h.Split(';', StringSplitOptions.RemoveEmptyEntries).Take(HistoryLength).ToList();
                Log.Info("Imported speed test history from TaskbarSpeedTest");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Speed test history import failed", ex);
        }
    }
}
