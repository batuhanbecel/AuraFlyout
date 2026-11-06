using AuraFlyout.Core;

namespace AuraFlyout.Flyouts;

/// <summary>Panels opened from the taskbar items plus the device notification toast.</summary>
public sealed class PanelManager
{
    private StatusFlyout? _status;
    private BatteryFlyout? _battery;
    private SpeedFlyout? _speed;
    private NotificationToast? _toast;

    public StatusFlyout Status => _status ??= new StatusFlyout();
    public BatteryFlyout Battery => _battery ??= new BatteryFlyout();
    public SpeedFlyout Speed => _speed ??= new SpeedFlyout();
    public NotificationToast Toast => _toast ??= new NotificationToast();

    /// <summary>Creates the panels while the app is idle, so the first click only shows one.</summary>
    public void Prepare()
    {
        var d = System.Windows.Application.Current.Dispatcher;
        foreach (var make in new Func<FlyoutWindow>[] { () => Status, () => Battery, () => Speed })
            d.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => make().Prepare());
    }

    public void ToggleStatus(Native.RECT anchor)
    {
        if (!Status.IsShown)
        {
            App.Status.PanelOpen = true; // the panel's extra readings (disk, VRAM, ping) start right away
            App.Status.SampleNow();
        }
        Status.ToggleAt(anchor);
    }

    public void ToggleBattery(Native.RECT anchor)
    {
        PerfLog.Write("click battery");
        Battery.ToggleAt(anchor);
    }

    public void ToggleSpeed(Native.RECT anchor)
    {
        PerfLog.Write("click speed");
        Speed.ToggleAt(anchor);
    }

    /// <summary>A device notification: a real Windows notification, or the built-in toast as a fallback.</summary>
    public void ShowToast(string title, string text, string glyph, bool warning)
    {
        if (SettingsStore.Current.UseWindowsNotifications && WindowsNotifications.Show(title, text, glyph, warning)) return;
        Toast.Show(title, text, glyph, warning);
    }
}
