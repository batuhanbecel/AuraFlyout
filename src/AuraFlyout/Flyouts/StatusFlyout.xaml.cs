using System.Windows;
using AuraFlyout.Core;
using AuraFlyout.Modules.SystemStatus;
using AuraFlyout.Taskbar;

namespace AuraFlyout.Flyouts;

/// <summary>Detailed system status panel (opens from the status readout on the taskbar).</summary>
public partial class StatusFlyout : FlyoutWindow
{
    public StatusFlyout()
    {
        InitializeComponent();
        CpuNameText.Text = App.Status.CpuName;
        App.Status.Sampled += () =>
        {
            if (IsShown) Refresh();
        };
        ShownChanged += shown =>
        {
            App.Status.PanelOpen = shown; // the ping chart samples while the panel is open
            if (shown) Refresh();
        };
    }

    private void Power_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int mode))
        {
            // Windows reports the new mode a moment later: show the choice now, check it then
            ShowPowerMode(PowerMode.Set(mode) ? mode : PowerMode.Get());
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(800);
                ShowPowerMode();
            });
        }
        else ShowPowerMode();
    }

    private void ShowPowerMode() => ShowPowerMode(PowerMode.Get());

    private void ShowPowerMode(int? mode)
    {
        PowerSaver.IsChecked = mode == 0;
        PowerBalanced.IsChecked = mode == 1;
        PowerBest.IsChecked = mode == 2;
        PowerSaver.IsEnabled = PowerBalanced.IsEnabled = PowerBest.IsEnabled = mode != null;
    }

    protected override bool IsPanel => true;
    protected override int DurationMs => 60000;
    protected override int PositionIndex => 2;
    protected override FrameworkElement Surface => Root;

    internal void Refresh()
    {
        var st = App.Status;
        CpuText.Text = SystemStatusService.Percent(st.CpuUsage);
        CpuSub.Text = st.CpuTemp.HasValue
            ? $"{SystemStatusService.Temp(st.CpuTemp)} · {st.CpuTempSource}"
            : st.CpuTempHint();
        CpuChart.SetValues(st.CpuHistory);

        GpuText.Text = st.GpuUsage.HasValue ? SystemStatusService.Percent(st.GpuUsage.Value) : "--";
        var gpu = new List<string>();
        if (st.GpuTemp.HasValue) gpu.Add(SystemStatusService.Temp(st.GpuTemp));
        if (st.GpuFanRpm > 0) gpu.Add(st.GpuFanRpm + " RPM");
        if (st.GpuName != null) gpu.Add(st.GpuName);
        GpuSub.Text = string.Join(" · ", gpu);
        GpuChart.SetValues(st.GpuHistory);

        RamText.Text = SystemStatusService.Percent(st.RamUsage);
        RamSub.Text = $"{st.RamUsedGb:0.0} / {st.RamTotalGb:0.0} GB";
        RamChart.SetValues(st.RamHistory);

        if (!st.FpsAvailable)
        {
            FpsText.Text = "--";
            FpsSub.Text = L10n.Get("Status_NeedsAdmin");
        }
        else
        {
            FpsText.Text = st.Fps > 0 ? st.Fps.ToString() : "--";
            FpsSub.Text = st.Fps > 0 ? st.FpsProcess : L10n.Get("Status_FpsIdle");
        }

        DownText.Text = NetMonitor.Format(st.NetDown);
        UpText.Text = NetMonitor.Format(st.NetUp);
        NetChart.SetValues(st.NetHistory);

        DiskText.Text = st.DiskUsage.HasValue ? SystemStatusService.Percent(st.DiskUsage.Value) : "--";
        DiskChart.SetValues(st.DiskHistory);
        VramText.Text = st.VramUsedGb is double vu ? vu.ToString("0.0") + " GB" : "--";
        VramSub.Text = st.VramTotalGb is double vt ? "/ " + vt.ToString("0") + " GB" : "";
        PingText.Text = st.PingMs is double pm ? Math.Round(pm) + " ms" : "--";
        PingChart.SetValues(st.PingHistory);
        ShowPowerMode();

        bool needsAdmin = !Elevation.IsAdmin && (!st.FpsAvailable || !st.CpuTemp.HasValue);
        AdminHint.Visibility = needsAdmin ? Visibility.Visible : Visibility.Collapsed;
        AdminHintText.Text = L10n.Get("Status_AdminHint");
    }

    private void TaskManager_Click(object sender, RoutedEventArgs e)
    {
        StatusDock.OpenTaskManager();
        HideFlyout();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        App.Instance.OpenSettings("Nav_Status");
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _ = App.Instance.EnableElevationAsync(true);
    }
}
