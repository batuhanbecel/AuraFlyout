using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AuraFlyout.Core;

namespace AuraFlyout.Settings.Pages;

public partial class SystemPage : UserControl
{
    public SystemPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            StartupSwitch.IsChecked = StartupManager.IsEnabled;
            ElevatedSwitch.IsChecked = SettingsStore.Current.RunElevated;
            ShowInstallState();
            UpdateState.Text = Installer.VersionText;
        };
    }

    // ---------- installation ----------

    private void ShowInstallState()
    {
        bool here = Installer.RunningInstalled;
        InstallState.Text = L10n.Get(here ? "Install_Installed" : Installer.Installed ? "Install_Other" : "Install_Portable");
        InstallButton.Content = L10n.Get(here ? "Install_Uninstall" : "Install_Button");
        InstallButton.Appearance = here ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Primary;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        bool uninstall = Installer.RunningInstalled;
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = L10n.Get(uninstall ? "Install_Uninstall" : "Install_Button"),
            Content = L10n.Get(uninstall ? "Install_UninstallConfirm" : "Install_Confirm"),
            PrimaryButtonText = L10n.Get("Common_Yes"),
            CloseButtonText = L10n.Get("Common_No"),
        };
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        if (uninstall)
        {
            Installer.Uninstall();
            App.Instance.ExitApp();
        }
        else Installer.Install();
    }

    // ---------- updates ----------

    private Updater.Release? _release;

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

    // ---------- settings file ----------

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"AuraFlyout-{DateTime.Now:yyyy-MM-dd}.json",
            Filter = "AuraFlyout (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            SettingsStore.Export(dialog.FileName);
            DataState.Text = L10n.Format("System_Exported", dialog.FileName);
        }
        catch (Exception ex)
        {
            DataState.Text = ex.Message;
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "AuraFlyout (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            SettingsStore.Import(dialog.FileName);
            DataState.Text = L10n.Get("System_Imported");
        }
        catch (Exception ex)
        {
            DataState.Text = L10n.Get("System_ImportFailed") + " " + ex.Message;
        }
    }

    private void Startup_Click(object sender, RoutedEventArgs e) =>
        StartupManager.SetEnabled(StartupSwitch.IsChecked == true);

    private async void Elevated_Click(object sender, RoutedEventArgs e)
    {
        await App.Instance.EnableElevationAsync(ElevatedSwitch.IsChecked == true);
        ElevatedSwitch.IsChecked = SettingsStore.Current.RunElevated;
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
            Log.Warn("Could not open data folder", ex);
        }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = L10n.Get("System_Reset"),
            Content = L10n.Get("System_ResetConfirm"),
            PrimaryButtonText = L10n.Get("Common_Yes"),
            CloseButtonText = L10n.Get("Common_No"),
        };
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        // copy defaults onto the live settings object so every binding updates
        var current = SettingsStore.Current;
        var defaults = new AppSettings
        {
            FirstRunDone = true,
            ModulesImported = true,
            RunElevated = current.RunElevated,          // tied to the scheduled task, changed from its own switch
            SpeedHistory = current.SpeedHistory,        // results are data, not settings
            LogiAliases = current.LogiAliases,
        };
        foreach (var p in typeof(AppSettings).GetProperties())
        {
            if (!p.CanWrite || !p.CanRead) continue;
            // live collections keep their instance (others subscribed to them)
            if (p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(System.Collections.ObjectModel.ObservableCollection<>)) continue;
            p.SetValue(current, p.GetValue(defaults));
        }
        current.ExcludedApps.Clear();
        current.LogiHidden.Clear();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => App.Instance.ExitApp();
}
