using AuraFlyout.Core;

namespace AuraFlyout.Modules;

/// <summary>
/// A device with a charge, whatever reads it (Logitech HID++, Windows' Bluetooth battery, game
/// controller reports): what the battery panel, the taskbar readout and the settings list show.
/// </summary>
public interface IBatteryDevice
{
    string Id { get; }
    string Name { get; }
    string Glyph { get; }
    bool Connected { get; }
    bool Hidden { get; }
    int Percent { get; }
    bool HasBattery { get; }
    string PercentText { get; }
    string StatusText { get; }
    bool Low { get; }
    bool Charging { get; }
    bool Full { get; }

    /// <summary>Remembered by the app while away (Logitech): can be forgotten.</summary>
    bool CanForget { get; }
}

/// <summary>Every device with a charge, from all sources, plus the names and hiding they share.</summary>
public static class Batteries
{
    private static AppSettings S => SettingsStore.Current;

    public static IEnumerable<IBatteryDevice> All =>
        (App.Logi?.Devices.Cast<IBatteryDevice>() ?? [])
        .Concat(App.Gamepads?.Devices.Cast<IBatteryDevice>() ?? [])
        .Concat(App.Bluetooth?.Devices.Cast<IBatteryDevice>() ?? []);

    /// <summary>Visible, connected devices that report a charge, in panel order.</summary>
    public static IEnumerable<IBatteryDevice> Taskbar => All.Where(d => d.Connected && !d.Hidden && d.HasBattery);

    /// <summary>Raised when any source changed its list.</summary>
    public static event Action? Changed
    {
        add
        {
            if (App.Logi != null) App.Logi.Changed += value;
            if (App.Bluetooth != null) App.Bluetooth.Changed += value;
            if (App.Gamepads != null) App.Gamepads.Changed += value;
        }
        remove
        {
            if (App.Logi != null) App.Logi.Changed -= value;
            if (App.Bluetooth != null) App.Bluetooth.Changed -= value;
            if (App.Gamepads != null) App.Gamepads.Changed -= value;
        }
    }

    /// <summary>The user's name for a device, or <paramref name="name"/>.</summary>
    public static string NameFor(string id, string name) => S.LogiAliases.TryGetValue(id, out var alias) ? alias : name;

    public static bool IsHidden(string id) => S.LogiHidden.Contains(id);

    public static void Rename(string id, string? alias)
    {
        var map = new Dictionary<string, string>(S.LogiAliases);
        if (string.IsNullOrWhiteSpace(alias)) map.Remove(id);
        else map[id] = alias.Trim();
        S.LogiAliases = map;
    }

    /// <summary>Hidden devices stay in the panel but leave the taskbar.</summary>
    public static void SetHidden(string id, bool hide)
    {
        var list = S.LogiHidden;
        if (hide && !list.Contains(id)) list.Add(id);
        if (!hide) list.Remove(id);
    }

    /// <summary>Calls <paramref name="apply"/> when names or hiding change.</summary>
    public static void OnNamesChanged(Action apply)
    {
        S.LogiHidden.CollectionChanged += (_, _) => apply();
        S.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.LogiAliases) or nameof(AppSettings.LogiLowThreshold)) apply();
        };
        L10n.LanguageChanged += apply;
    }
}
