using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AuraFlyout.Core;
using AuraFlyout.Modules;

namespace AuraFlyout.Settings.Pages;

/// <summary>Device batteries: Logitech, game controllers and Bluetooth devices in one list.</summary>
public partial class LogiPage : UserControl
{
    public LogiPage()
    {
        InitializeComponent();
        DeviceList.ItemsSource = new CompositeCollection
        {
            new CollectionContainer { Collection = App.Logi.Devices },
            new CollectionContainer { Collection = App.Gamepads.Devices },
            new CollectionContainer { Collection = App.Bluetooth.Devices },
        };
        Batteries.Changed += UpdateEmpty;
        Loaded += (_, _) => UpdateEmpty();
        Unloaded += (_, _) => Batteries.Changed -= UpdateEmpty;
    }

    private void UpdateEmpty() =>
        NoDevices.Visibility = Batteries.All.Any() ? Visibility.Collapsed : Visibility.Visible;

    private static IBatteryDevice? Item(object sender) => (sender as FrameworkElement)?.Tag as IBatteryDevice;

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Item(sender) is not { } d) return;
        var box = new Wpf.Ui.Controls.TextBox { Text = d.Name, MinWidth = 260 };
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = L10n.Get("Logi_Rename"),
            Content = box,
            PrimaryButtonText = L10n.Get("Common_Save"),
            CloseButtonText = L10n.Get("Common_Cancel"),
        };
        if (await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary) Batteries.Rename(d.Id, box.Text);
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (Item(sender) is { } d) Batteries.SetHidden(d.Id, !d.Hidden);
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (Item(sender) is { CanForget: true } d) App.Logi.Forget(d.Id);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        App.Logi.RefreshNow();
        App.Gamepads.RefreshNow();
        App.Bluetooth.RefreshNow();
    }

    private void Test_Click(object sender, RoutedEventArgs e) => App.Logi.TestNotification();

    private void Layout_Click(object sender, RoutedEventArgs e) => App.Instance.OpenSettings("Nav_Layout");
}
