using System.Windows;
using System.Windows.Input;
using AuraFlyout.Core;
using AuraFlyout.Modules;
using AuraFlyout.Modules.Bluetooth;
using AuraFlyout.Modules.LogiBattery;

namespace AuraFlyout.Flyouts;

/// <summary>
/// Every device with a charge (Logitech, game controllers, Bluetooth) with its connection; opens
/// from the taskbar readout. A Bluetooth headset's icon connects or disconnects it.
/// </summary>
public partial class BatteryFlyout : FlyoutWindow
{
    public BatteryFlyout()
    {
        InitializeComponent();
        DeviceList.ItemsSource = App.Logi.Devices;
        BluetoothList.ItemsSource = App.Bluetooth.Devices;
        GamepadList.ItemsSource = App.Gamepads.Devices;
        Batteries.Changed += UpdateEmpty;
        ShownChanged += shown =>
        {
            if (shown)
            {
                App.Bluetooth.RefreshNow();
                App.BluetoothAudio.Refresh();
                App.Gamepads.RefreshNow();
            }
            UpdateEmpty();
        };
    }

    protected override bool IsPanel => true;
    protected override int DurationMs => 60000;
    protected override int PositionIndex => 2;
    protected override FrameworkElement Surface => Root;

    internal void UpdateEmpty()
    {
        EmptyText.Visibility = Batteries.All.Any() ? Visibility.Collapsed : Visibility.Visible;
        GamepadSection.Visibility = App.Gamepads.Devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BluetoothSection.Visibility = App.Bluetooth.Devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A headset / speaker's icon: connect it, or disconnect it.</summary>
    private void Icon_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BluetoothDeviceItem { Audio: { } audio }) return;
        e.Handled = true;
        App.BluetoothAudio.Toggle(audio);
        Touch();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        App.Logi.RefreshNow();
        App.Bluetooth.RefreshNow();
        App.Gamepads.RefreshNow();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        App.Instance.OpenSettings("Nav_Logi");
    }

    private void Device_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not IBatteryDevice d) return;
        e.Handled = true;
        // Windows' own context menu (like every right-click menu in the shell), not a WPF one
        KeepOpen = true;
        try
        {
            var items = new List<TrayMenuItem>
            {
                new(L10n.Get("Logi_Rename"), () => _ = RenameAsync(d)),
                new(L10n.Get(d.Hidden ? "Logi_Show" : "Logi_Hide"), () => Batteries.SetHidden(d.Id, !d.Hidden)),
            };
            if (d.CanForget)
            {
                items.Add(TrayMenuItem.Line);
                items.Add(new(L10n.Get("Logi_Forget"), () => App.Logi.Forget(d.Id), Enabled: !d.Connected));
            }
            NativeMenu.Show(Hwnd, items);
        }
        finally
        {
            KeepOpen = false;
        }
    }

    private async Task RenameAsync(IBatteryDevice d)
    {
        KeepOpen = true;
        try
        {
            var box = new Wpf.Ui.Controls.TextBox { Text = d.Name, MinWidth = 260 };
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = L10n.Get("Logi_Rename"),
                Content = box,
                PrimaryButtonText = L10n.Get("Common_Save"),
                CloseButtonText = L10n.Get("Common_Cancel"),
                Topmost = true,
            };
            dialog.Loaded += (_, _) => box.Focus();
            if (await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary)
                Batteries.Rename(d.Id, box.Text);
        }
        finally
        {
            KeepOpen = false;
        }
    }
}
