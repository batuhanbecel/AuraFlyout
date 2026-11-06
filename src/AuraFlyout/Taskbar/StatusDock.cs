using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AuraFlyout.Controls;
using AuraFlyout.Core;
using AuraFlyout.Modules.SystemStatus;

namespace AuraFlyout.Taskbar;

/// <summary>
/// The TaskbarStatus readout (CPU / GPU / RAM / FPS / network) docked in the taskbar, on one line or
/// two, with glyph → label → value order and colour warnings. The visual tree is only rebuilt when
/// its shape changes; a new sample just updates texts and colours, in fixed-width slots, so nothing
/// moves or flickers.
/// </summary>
public sealed class StatusDock : DockWindow
{
    private readonly StackPanel _rows = new() { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToolTip _tip = new();
    private readonly List<(MetricGlyphIcon? Glyph, TextBlock Text)> _slots = [];
    private string _shape = "";
    private readonly System.Windows.Threading.DispatcherTimer _clickTimer;
    private long _doubleClickAt;

    public StatusDock()
    {
        Title = "AuraFlyout Status";
        SetDockContent(_rows);
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 400);
        _tip.Opened += (_, _) => _tip.Content = Details();

        App.Status.Sampled += Update;
        SettingsStore.Current.PropertyChanged += OnSettingChanged;
        ThemeService.Changed += Rebuild;
        // A single click opens the panel, a double click Task Manager. Like a tray icon with a
        // double-click action, the single click waits Windows' double-click time, so a double click
        // never flashes the panel open and shut first.
        _clickTimer = new System.Windows.Threading.DispatcherTimer();
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            App.Panels.ToggleStatus(ScreenRect);
        };
        MouseLeftButtonUp += (_, _) =>
        {
            if (Environment.TickCount64 - _doubleClickAt < Native.GetDoubleClickTime()) return; // release of the 2nd click
            _clickTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(Native.GetDoubleClickTime(), 100, 900));
            _clickTimer.Stop();
            _clickTimer.Start();
        };
        MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            _clickTimer.Stop();
            _doubleClickAt = Environment.TickCount64;
            OpenTaskManager();
        };
        Loaded += (_, _) => Rebuild();
    }

    private static AppSettings S => SettingsStore.Current;

    public override string Id => "status";
    public override int OffsetDip => S.StatusOffset;
    public override bool WantsVisible => S.StatusEnabled;

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != null && e.PropertyName.StartsWith("Status")) Rebuild();
    }

    protected override void OnTextStyleChanged() => Rebuild();

    /// <summary>Widest text a value of this kind can have (digits as 0).</summary>
    private static string SlotTemplate(StatusValue v) => v.Glyph switch
    {
        MetricGlyphKind.Temperature => "100°C",
        MetricGlyphKind.Fps => "000",
        MetricGlyphKind.Download or MetricGlyphKind.Upload => "000 KB/s",
        MetricGlyphKind.Ping => "000 ms",
        _ when v.Text.EndsWith("GB") => "00,0 GB",
        _ => "100%",
    };

    private string ShapeOf(IReadOnlyList<StatusCell> cells)
    {
        var sb = new StringBuilder();
        foreach (var c in cells)
        {
            sb.Append(c.Label).Append(':');
            foreach (var v in c.Values) sb.Append((int)v.Glyph).Append(SlotTemplate(v)).Append(',');
            sb.Append(';');
        }
        sb.Append(S.StatusTwoLines).Append(S.StatusShowGlyphs).Append(S.StatusShowLabels)
          .Append(FontSize).Append(FontWeight).Append(ThemeService.IsTaskbarDark);
        return sb.ToString();
    }

    private void Rebuild()
    {
        _shape = "";
        Update();
    }

    private void Update()
    {
        var cells = App.Status.Cells;
        string shape = ShapeOf(cells);
        if (shape != _shape)
        {
            _shape = shape;
            Build(cells);
            RequestLayout();
        }

        // just the values: same brush instances and same strings cause no redraw at all
        int k = 0;
        foreach (var cell in cells)
        {
            foreach (var value in cell.Values)
            {
                if (k >= _slots.Count) break;
                var brush = !S.StatusColorWarnings || value.Level == MetricLevel.Normal ? PrimaryBrush
                    : value.Level == MetricLevel.Warn ? WarnBrush : HotBrush;
                var (glyph, text) = _slots[k++];
                text.Text = value.Text;
                text.Foreground = brush;
                if (glyph != null) glyph.Brush = brush;
            }
        }
    }

    private void Build(IReadOnlyList<StatusCell> cells)
    {
        var s = S;
        int lines = s.StatusTwoLines ? 2 : 1;
        double size = FontSize;

        _slots.Clear();
        _rows.Children.Clear();
        var rowPanels = new StackPanel[lines];
        for (int i = 0; i < lines; i++)
        {
            rowPanels[i] = new StackPanel { Orientation = Orientation.Horizontal };
            _rows.Children.Add(rowPanels[i]);
        }

        if (cells.Count == 0) rowPanels[0].Children.Add(new TextBlock { Text = "AuraFlyout", Foreground = PrimaryBrush });

        // column-major like TaskbarStatus: cell i on row i % lines, each row advancing on its own
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            var row = rowPanels[i % lines];
            var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(row.Children.Count > 0 ? 12 : 0, 0, 0, 0) };
            for (int v = 0; v < cell.Values.Count; v++)
            {
                var value = cell.Values[v];
                double left = v > 0 ? 6 : 0;
                MetricGlyphIcon? glyph = null;
                if (s.StatusShowGlyphs)
                {
                    glyph = new MetricGlyphIcon
                    {
                        Kind = value.Glyph,
                        Width = size,
                        Height = size,
                        Margin = new Thickness(left, 0, 3, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    group.Children.Add(glyph);
                    left = 0;
                }
                bool arrow = cell.Label.Length == 1;
                if (v == 0 && ((s.StatusShowLabels && !arrow) || (!s.StatusShowGlyphs && arrow)))
                {
                    group.Children.Add(new TextBlock
                    {
                        Text = cell.Label,
                        Foreground = SecondaryBrush,
                        Margin = new Thickness(left, 0, 4, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    left = 0;
                }
                var text = new TextBlock
                {
                    Margin = new Thickness(left, 0, 0, 0),
                    MinWidth = SlotWidth(SlotTemplate(value), size, FontWeight),
                    TextAlignment = TextAlignment.Left, // next to its label; the slot keeps the width steady
                    VerticalAlignment = VerticalAlignment.Center,
                };
                group.Children.Add(text);
                _slots.Add((glyph, text));
            }
            row.Children.Add(group);
        }
    }

    private static string Details()
    {
        var st = App.Status;
        var sb = new StringBuilder();
        sb.Append("CPU: ").Append(SystemStatusService.Percent(st.CpuUsage));
        if (st.CpuTemp.HasValue) sb.Append("  ·  ").Append(SystemStatusService.Temp(st.CpuTemp)).Append(" (").Append(st.CpuTempSource).Append(')');
        else if (S.StatusShowCpuTemp) sb.Append("  ·  ").Append(st.CpuTempHint());
        sb.AppendLine();
        sb.Append("RAM: ").Append(st.RamUsedGb.ToString("0.0")).Append(" / ").Append(st.RamTotalGb.ToString("0.0"))
          .Append(" GB (").Append(SystemStatusService.Percent(st.RamUsage)).AppendLine(")");
        sb.Append("GPU: ");
        if (st.GpuName != null) sb.Append(st.GpuName).Append("  ·  ");
        sb.Append(st.GpuUsage.HasValue ? SystemStatusService.Percent(st.GpuUsage.Value) : "--");
        if (st.GpuTemp.HasValue) sb.Append("  ·  ").Append(SystemStatusService.Temp(st.GpuTemp));
        if (st.GpuFanRpm > 0) sb.Append("  ·  ").Append(st.GpuFanRpm).Append(" RPM");
        sb.AppendLine();
        sb.Append("FPS: ");
        if (!st.FpsAvailable) sb.Append(L10n.Get("Status_NeedsAdmin"));
        else if (st.Fps > 0) sb.Append(st.Fps).Append("  ·  ").Append(st.FpsProcess);
        else sb.Append("--");
        sb.AppendLine().Append(L10n.Get("Status_Network")).Append(": ↓ ").Append(NetMonitor.Format(st.NetDown))
          .Append("   ↑ ").Append(NetMonitor.Format(st.NetUp));
        return sb.ToString();
    }

    public static void OpenTaskManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Task Manager could not be opened", ex);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        App.Status.Sampled -= Update;
        SettingsStore.Current.PropertyChanged -= OnSettingChanged;
        ThemeService.Changed -= Rebuild;
        base.OnClosed(e);
    }
}
