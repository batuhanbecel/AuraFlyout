using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using AuraFlyout.Core;

namespace AuraFlyout.Flyouts;

/// <summary>Brightness of every screen, at the volume flyout's place (where Windows shows its own).</summary>
public partial class BrightnessFlyout : FlyoutWindow
{
    public BrightnessFlyout()
    {
        InitializeComponent();
        List.ItemsSource = App.Brightness.Displays;
        App.Brightness.Displays.CollectionChanged += (_, _) => UpdateEmpty();
        App.Brightness.ChangedExternally += _ =>
        {
            // laptop brightness keys: our flyout instead of the Windows overlay
            if (!SettingsStore.Current.BrightnessFlyoutEnabled) return;
            if (SettingsStore.Current.HideNativeVolumeOsd) Audio.NativeOsd.Hide();
            ShowFlyout();
        };
        UpdateEmpty();
    }

    protected override int DurationMs => Math.Max(3000, SettingsStore.Current.VolumeFlyoutDuration);
    protected override int PositionIndex => SettingsStore.Current.VolumeFlyoutPosition;
    protected override FrameworkElement Surface => Root;

    /// <summary>Shows the flyout and re-reads the monitors (their own buttons may have changed them).</summary>
    public void ShowNow()
    {
        App.Brightness.Refresh();
        ShowFlyout();
    }

    private void UpdateEmpty() =>
        EmptyText.Visibility = App.Brightness.Displays.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Touch();
    private void Slider_DragStarted(object sender, DragStartedEventArgs e) => KeepOpen = true;

    private void Slider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        KeepOpen = false;
        Touch();
    }

    private void DisplaySettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Could not open display settings", ex); }
        HideFlyout();
    }
}
