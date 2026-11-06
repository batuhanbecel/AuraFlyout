using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AuraFlyout.Core;
using AuraFlyout.Modules.SystemStatus;

namespace AuraFlyout.Settings.Pages;

public partial class StatusPage : UserControl
{
    private bool _loading;

    public StatusPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _loading = true;
        int ms = SettingsStore.Current.StatusInterval;
        foreach (ComboBoxItem item in IntervalCombo.Items)
            if (int.Parse((string)item.Tag) == ms) IntervalCombo.SelectedItem = item;
        if (IntervalCombo.SelectedItem == null) IntervalCombo.SelectedIndex = 1;

        ElevatedState.Text = L10n.Get(SettingsStore.Current.RunElevated ? "Lock_On" : "Lock_Off");
        bool installed = PawnIo.Installed;
        PawnIoState.Text = L10n.Get(installed ? "Status_PawnIoInstalled" : "Status_PawnIoMissing");
        PawnIoButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;

        var st = App.Status;
        SourceText.Text = st.CpuTemp.HasValue ? $"{SystemStatusService.Temp(st.CpuTemp)} · {st.CpuTempSource}" : st.CpuTempHint(withLabel: false);
        _loading = false;
    }

    private void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || IntervalCombo.SelectedItem is not ComboBoxItem item) return;
        SettingsStore.Current.StatusInterval = int.Parse((string)item.Tag);
    }

    private void System_Click(object sender, RoutedEventArgs e) => App.Instance.OpenSettings("Nav_System");

    private void PawnIo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://pawnio.eu") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open the PawnIO website", ex);
        }
    }

    private void Layout_Click(object sender, RoutedEventArgs e) => App.Instance.OpenSettings("Nav_Layout");
}
