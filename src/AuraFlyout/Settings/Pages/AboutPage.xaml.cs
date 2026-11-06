using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AuraFlyout.Core;

namespace AuraFlyout.Settings.Pages;

public partial class AboutPage : UserControl
{
    private Updater.Release? _release;

    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"{L10n.Get("About_Version")} {Updater.Current.ToString(3)}";
        DataCard.Description = AppSettings.Folder;
    }

    // a feature tile opens its page
    private void Feature_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key } && Window.GetWindow(this) is SettingsWindow window)
            window.Navigate(key);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        try
        {
            if (_release != null && _release.Version > Updater.Current)
            {
                UpdateState.Text = L10n.Get("Update_Installing");
                await Updater.InstallAsync(_release);
                return;
            }
            UpdateState.Text = L10n.Get("Update_Checking");
            _release = await Updater.CheckAsync();
            if (_release == null) UpdateState.Text = L10n.Get("Update_None");
            else if (_release.Version > Updater.Current)
            {
                UpdateState.Text = L10n.Format("Update_Available", "v" + _release.Version.ToString(3));
                UpdateButton.Content = L10n.Get("Update_Install");
            }
            else UpdateState.Text = L10n.Format("Update_Latest", Installer.VersionText);
        }
        catch (Exception ex)
        {
            UpdateState.Text = ex.Message;
            Log.Warn("Update failed", ex);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Open settings folder failed", ex);
        }
    }
}
