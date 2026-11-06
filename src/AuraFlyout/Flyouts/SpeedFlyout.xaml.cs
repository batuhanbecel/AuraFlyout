using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using AuraFlyout.Controls;
using AuraFlyout.Core;
using AuraFlyout.Modules.SpeedTest;

namespace AuraFlyout.Flyouts;

/// <summary>
/// Speed test panel: TaskbarSpeedTest's layout (gauge with scale marks and the start button, ping /
/// download / upload, server, the last five tests) drawn with the same header, tiles and type ramp as
/// the other panels. The running phase's tile is outlined and shows its progress.
/// </summary>
public partial class SpeedFlyout : FlyoutWindow
{
    private const int HistoryRows = 5;
    private double _peak; // fastest live value of the running test: picks the gauge scale

    public SpeedFlyout()
    {
        InitializeComponent();
        App.Speed.Updated += () =>
        {
            if (IsShown) Refresh();
        };
        ShownChanged += shown =>
        {
            if (!shown) return;
            Gauge.BeginAnimation(SpeedGauge.FractionProperty, null);
            Gauge.Fraction = 0; // the gauge sweeps up to the value on every open
            Refresh();
        };
    }

    protected override bool IsPanel => true;
    protected override int DurationMs => 60000;
    protected override int PositionIndex => 2;
    protected override FrameworkElement Surface => Root;

    private static AppSettings S => SettingsStore.Current;

    internal void Refresh()
    {
        var svc = App.Speed;
        var st = svc.Status;
        bool running = st is { Running: true };
        bool mb = S.SpeedUnit == 1;
        var shown = svc.Latest;
        string? failure = st is { Running: false, Cancelled: false } && !st.Result.Complete && !string.IsNullOrEmpty(st.Result.Error)
            ? st.Result.Error : null;

        if (running && st!.Phase == Phase.Preparing) _peak = 0;
        if (running && st!.Phase is Phase.Download or Phase.Upload) _peak = Math.Max(_peak, st.Live);

        // ---- the connection the test runs over ----
        string link = !string.IsNullOrEmpty(shown?.Link) ? shown!.Link : NetworkInfo.Describe();
        LinkGlyph.Text = link.Length == 0 ? "" : link == "Wi-Fi" ? "" : "";

        // ---- gauge ----
        bool big = running ? _peak > 1000 : shown != null && Math.Max(shown.DownMbps, shown.UpMbps) > 1000;
        Gauge.Marks = SpeedGauge.MarksFor(mb, big);
        double Unit(double mbps) => mb ? mbps / 8 : mbps;
        double target = failure != null ? 0
            : running ? st!.Phase is Phase.Download or Phase.Upload ? SpeedGauge.Position(Unit(st.Live), Gauge.Marks) : 0
            : shown is { DownMbps: > 0 } ? SpeedGauge.Position(Unit(shown.DownMbps), Gauge.Marks) : 0;
        SweepTo(target);

        ErrorPanel.Visibility = failure != null ? Visibility.Visible : Visibility.Collapsed;
        ReadyPanel.Visibility = failure == null && !running && shown == null ? Visibility.Visible : Visibility.Collapsed;
        ValuePanel.Visibility = failure == null && (running || shown != null) ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = failure ?? "";
        if (running)
        {
            bool ping = st!.Phase == Phase.Ping;
            PhaseText.Text = PhaseName(st.Phase);
            LiveText.Text = st.Phase == Phase.Preparing ? "–" : ping ? SpeedTestService.Ms(st.Live) : SpeedTestService.Speed(st.Live);
            LiveText.SetResourceReference(TextBlock.ForegroundProperty, st.Phase == Phase.Preparing ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush");
            UnitText.Text = st.Phase == Phase.Preparing ? "" : ping ? "ms" : SpeedTestService.UnitName;
        }
        else if (shown != null)
        {
            PhaseText.Text = PhaseName(Phase.Download);
            LiveText.Text = SpeedTestService.Speed(shown.DownMbps);
            LiveText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            UnitText.Text = SpeedTestService.UnitName;
        }

        // ---- start / stop: the app's standard button ----
        bool again = shown != null || failure != null;
        RunButton.Appearance = running ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Primary;
        RunButton.Icon = new Wpf.Ui.Controls.FontIcon { Glyph = running ? FluentIcons.Stop : again ? "" : FluentIcons.Play, FontFamily = FluentIcons.Family };
        RunButton.Content = L10n.Get(running ? "Speed_StopShort" : again ? "Speed_Again" : "Speed_Start");

        // ---- metric tiles ----
        Metric(PingCard, PingText, PingUnit, PingProgress, Phase.Ping, st, shown?.PingMs ?? -1, ms: true);
        Metric(DownCard, DownText, DownUnit, DownProgress, Phase.Download, st, shown?.DownMbps ?? -1, ms: false);
        Metric(UpCard, UpText, UpUnit, UpProgress, Phase.Upload, st, shown?.UpMbps ?? -1, ms: false);
        bool pingActive = running && st!.Phase == Phase.Ping;
        JitterText.Text = shown != null && shown.JitterMs >= 0 && !pingActive ? $"Jitter {SpeedTestService.Ms(shown.JitterMs)} ms" : " ";

        // ---- server line: ISP · Cloudflare <data centre> ----
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(shown?.Isp)) parts.Add(shown!.Isp);
        parts.Add("Cloudflare" + (!string.IsNullOrEmpty(shown?.Server) ? " " + shown!.Server : ""));
        ServerText.Text = string.Join(" · ", parts);

        // ---- recent tests ----
        DownHead.Text = UpHead.Text = SpeedTestService.UnitName;
        var rows = svc.History.Take(HistoryRows).ToList();
        HistoryList.ItemsSource = rows;
        HistoryCard.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // ---- subtitle: connection and when the last test ran ----
        string when = running ? L10n.Get("Speed_Testing")
            : svc.Results.Count > 0 ? L10n.Format("Speed_LastTest", SpeedTestService.Ago(svc.Results[0].Time))
            : L10n.Get("Speed_NoResult");
        SubtitleText.Text = link.Length > 0 ? link + " · " + when : when;
    }

    /// <summary>One metric tile: live value while its phase runs (outlined, with progress), else the result.</summary>
    private static void Metric(Border card, TextBlock value, TextBlock unit, Border progress, Phase phase, TestStatus? st, double result, bool ms)
    {
        bool active = st is { Running: true } && st.Phase == phase;
        double v = active ? st!.Live : result;
        value.Text = ms ? SpeedTestService.Ms(v) : SpeedTestService.Speed(v);
        value.SetResourceReference(TextBlock.ForegroundProperty, v < 0 ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush");
        unit.Text = v < 0 ? "" : ms ? "ms" : SpeedTestService.UnitName;

        // the running phase's tile is outlined in the accent colour
        if (active) card.SetResourceReference(Border.BorderBrushProperty, "AuraAccentBrush");
        else card.BorderBrush = System.Windows.Media.Brushes.Transparent;
        progress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (active) progress.Width = Math.Max(3, (card.ActualWidth - 24) * Math.Clamp(st!.Progress, 0, 1));
    }

    /// <summary>Eases the arc to the new position (TaskbarSpeedTest's needle), instantly without Windows animations.</summary>
    private void SweepTo(double target)
    {
        if (!ThemeService.AnimationsEnabled)
        {
            Gauge.BeginAnimation(SpeedGauge.FractionProperty, null);
            Gauge.Fraction = target;
            return;
        }
        if (Math.Abs(Gauge.Fraction - target) < 0.001) return;
        Gauge.BeginAnimation(SpeedGauge.FractionProperty, new DoubleAnimation(Gauge.Fraction, target, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private static string PhaseName(Phase p) => p switch
    {
        Phase.Ping => "Ping",
        Phase.Download => L10n.Get("Speed_Download"),
        Phase.Upload => L10n.Get("Speed_Upload"),
        _ => L10n.Get("Speed_Preparing"),
    };

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        App.Speed.Toggle();
        Refresh();
    }

    private void CopyCsv_Click(object sender, RoutedEventArgs e) => SetClipboard(App.Speed.HistoryAsCsv());

    private static void SetClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Log.Warn("Clipboard failed", ex); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        App.Instance.OpenSettings("Nav_Speed");
    }
}
