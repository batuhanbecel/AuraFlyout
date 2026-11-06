using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using AuraFlyout.Controls;
using AuraFlyout.Core;
using AuraFlyout.Modules.SpeedTest;
using AuraFlyout.Modules.SystemStatus;

namespace AuraFlyout.Taskbar;

/// <summary>
/// TaskbarSpeedTest's readout: latest download / upload (and optionally ping), on two lines like the
/// clock or on one, updating live while a test runs with a thin progress bar underneath. Same glyphs,
/// colours and fixed-width slots as the system status; live updates only touch texts.
/// </summary>
public sealed class SpeedDock : DockWindow
{
    private readonly StackPanel _rows = new() { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
    // drawn just below the text through a render transform: it takes no layout space, so the text
    // stays centred on the taskbar exactly like the other items'
    private readonly Rectangle _progress = new()
    {
        Height = 2, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
        RadiusX = 1, RadiusY = 1, Opacity = 0, Width = 2, RenderTransform = new System.Windows.Media.TranslateTransform(0, 4),
    };
    private readonly ToolTip _tip = new();
    private readonly List<(Phase Phase, MetricGlyphIcon Glyph, TextBlock Text)> _slots = [];
    private string _shape = "";

    public SpeedDock()
    {
        Title = "AuraFlyout Speed";
        var content = new Grid { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(_rows);
        content.Children.Add(_progress);
        SetDockContent(content);
        ToolTip = _tip;
        _tip.Opened += (_, _) => _tip.Content = Details();
        _progress.SetResourceReference(Shape.FillProperty, "AuraTaskbarAccentBrush");

        App.Speed.Updated += Update;
        ThemeService.Changed += Rebuild;
        MouseLeftButtonUp += (_, _) => App.Panels.ToggleSpeed(ScreenRect);
        Loaded += (_, _) => Rebuild();
    }

    private static AppSettings S => SettingsStore.Current;

    public override string Id => "speed";
    public override int OffsetDip => S.SpeedOffset;
    public override bool WantsVisible => S.SpeedEnabled && (S.SpeedWidgetDownload || S.SpeedWidgetUpload || S.SpeedWidgetPing);

    protected override void OnTextStyleChanged() => Rebuild();

    private void Rebuild()
    {
        _shape = "";
        Update();
    }

    private void Update()
    {
        string shape = $"{S.SpeedWidgetDownload}{S.SpeedWidgetUpload}{S.SpeedWidgetPing}{S.SpeedWidgetUnits}{S.SpeedTwoLines}{S.SpeedUnit}{FontSize}{FontWeight}{ThemeService.IsTaskbarDark}";
        if (shape != _shape)
        {
            _shape = shape;
            Build();
            RequestLayout();
        }

        var svc = App.Speed;
        var st = svc.Status;
        var r = svc.Latest;
        bool running = st is { Running: true };
        string unit = S.SpeedWidgetUnits ? " " + SpeedTestService.UnitName : "";
        string ms = S.SpeedWidgetUnits ? " ms" : "";

        foreach (var (phase, glyph, text) in _slots)
        {
            bool ping = phase == Phase.Ping;
            double finished = phase switch { Phase.Download => r?.DownMbps ?? -1, Phase.Upload => r?.UpMbps ?? -1, _ => r?.PingMs ?? -1 };
            text.Text = Value(st, phase, finished, ping ? ms : unit, ping);
            var brush = running && st!.Phase != phase ? SecondaryBrush : PrimaryBrush;
            text.Foreground = brush;
            glyph.Brush = brush;
        }

        // the bar always keeps its space, so a test does not change the item's size
        _progress.Opacity = running ? 1 : 0;
        _progress.Width = running ? Math.Max(2, Overall(st!) * Math.Max(20, _rows.ActualWidth)) : 2;
    }

    private void Build()
    {
        _slots.Clear();
        _rows.Children.Clear();
        string unit = S.SpeedWidgetUnits ? " " + SpeedTestService.UnitName : "";
        string ms = S.SpeedWidgetUnits ? " ms" : "";
        double speedSlot = SlotWidth("0000" + unit, FontSize, FontWeight);
        double pingSlot = SlotWidth("000" + ms, FontSize, FontWeight);

        var items = new List<(Phase Phase, MetricGlyphKind Kind, double Slot)>();
        if (S.SpeedWidgetDownload) items.Add((Phase.Download, MetricGlyphKind.Download, speedSlot));
        if (S.SpeedWidgetUpload) items.Add((Phase.Upload, MetricGlyphKind.Upload, speedSlot));
        if (S.SpeedWidgetPing) items.Add((Phase.Ping, MetricGlyphKind.Fps, pingSlot));

        int lines = S.SpeedTwoLines && items.Count > 1 ? 2 : 1;
        var rowPanels = new StackPanel[lines];
        for (int i = 0; i < lines; i++)
        {
            rowPanels[i] = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            _rows.Children.Add(rowPanels[i]);
        }
        for (int i = 0; i < items.Count; i++)
        {
            var line = rowPanels[i % lines];
            var (phase, kind, slot) = items[i];
            var glyph = new MetricGlyphIcon
            {
                Kind = kind,
                Width = FontSize,
                Height = FontSize,
                Margin = new Thickness(line.Children.Count > 0 ? 12 : 0, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var text = new TextBlock { MinWidth = slot, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var pair = new StackPanel { Orientation = Orientation.Horizontal };
            pair.Children.Add(glyph);
            pair.Children.Add(text);
            line.Children.Add(pair);
            _slots.Add((phase, glyph, text));
        }
    }

    private static string Value(TestStatus? st, Phase phase, double finished, string unit, bool ping)
    {
        // live value while that phase runs, the finished one otherwise
        if (st is { Running: true } && st.Phase == phase)
            return (ping ? SpeedTestService.Ms(st.Live) : SpeedTestService.Speed(st.Live)) + unit;
        return (ping ? SpeedTestService.Ms(finished) : SpeedTestService.Speed(finished)) + unit;
    }

    /// <summary>10 % for the ping, then 45 % for each direction (as in TaskbarSpeedTest).</summary>
    public static double Overall(TestStatus st) => st.Phase switch
    {
        Phase.Preparing => 0,
        Phase.Ping => 0.10 * st.Progress,
        Phase.Download => 0.10 + 0.45 * st.Progress,
        Phase.Upload => 0.55 + 0.45 * st.Progress,
        _ => 1,
    };

    private static string Details()
    {
        var svc = App.Speed;
        if (svc.Status is { Running: true }) return L10n.Get("Speed_Running");
        var r = svc.Results.FirstOrDefault();
        if (r == null) return L10n.Get("Speed_NoResult");
        return $"{L10n.Get("Speed_Download")}: {SpeedTestService.SpeedWithUnit(r.DownMbps)}\n" +
               $"{L10n.Get("Speed_Upload")}: {SpeedTestService.SpeedWithUnit(r.UpMbps)}\n" +
               $"Ping: {SpeedTestService.Ms(r.PingMs)} ms · Jitter: {SpeedTestService.Ms(r.JitterMs)} ms\n" +
               $"{r.Server} · {SpeedTestService.Ago(r.Time)}";
    }

    protected override void OnClosed(EventArgs e)
    {
        App.Speed.Updated -= Update;
        ThemeService.Changed -= Rebuild;
        base.OnClosed(e);
    }
}
