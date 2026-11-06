using System.Windows;
using System.Windows.Controls;
using AuraFlyout.Core;

namespace AuraFlyout.Settings.Pages;

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            StartupSwitch.IsChecked = StartupManager.IsEnabled;
            CheckLegacy();
        };
    }

    private void CheckLegacy()
    {
        var running = LegacyImport.RunningApps();
        LegacyBar.IsOpen = running.Count > 0;
        LegacyText.Text = L10n.Format("Home_LegacyText", string.Join(", ", running));
    }

    private void Retire_Click(object sender, RoutedEventArgs e)
    {
        LegacyImport.RetireStandaloneApps();
        CheckLegacy();
    }

    private void Startup_Click(object sender, RoutedEventArgs e) =>
        StartupManager.SetEnabled(StartupSwitch.IsChecked == true);

    private void TestMedia_Click(object sender, RoutedEventArgs e) => App.Flyouts.ShowMedia(force: true);
    private void TestVolume_Click(object sender, RoutedEventArgs e) => App.Flyouts.Volume.Show(expanded: true);

    private void TestLock_Click(object sender, RoutedEventArgs e) =>
        App.Flyouts.LockKeys.ShowKey("Caps Lock", KeyboardHook.IsToggled(Native.VK_CAPITAL));

    private void TestUpNext_Click(object sender, RoutedEventArgs e) => App.Flyouts.UpNext.ShowFlyout();
}
