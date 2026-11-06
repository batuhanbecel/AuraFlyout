using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AuraFlyout.Core;

/// <summary>
/// All user settings. Every property raises PropertyChanged, so XAML binds straight to it
/// and the store saves automatically (debounced).
/// </summary>
public partial class AppSettings : ObservableObject
{
    // ---------- General ----------
    [ObservableProperty] public partial string Language { get; set; } = "auto"; // auto | tr | en
    [ObservableProperty] public partial int Theme { get; set; } = 0;             // 0 system, 1 light, 2 dark
    [ObservableProperty] public partial int Backdrop { get; set; } = 0;          // 0 Fluent acrylic (Windows 11), 1 classic acrylic, 2 solid
    [ObservableProperty] public partial int AcrylicOpacity { get; set; } = WindowsAcrylicTint; // 0..255 tint over the acrylic (Pro)

    /// <summary>Tint alpha that matches the Windows 11 shell flyouts (measured).</summary>
    public const int WindowsAcrylicTint = 192;
    [ObservableProperty] public partial int SettingsVersion { get; set; } = 0;
    [ObservableProperty] public partial bool HideTrayIcon { get; set; } = false; // Pro
    [ObservableProperty] public partial bool DisableInFullscreen { get; set; } = true;
    [ObservableProperty] public partial int FlyoutMonitor { get; set; } = 0;     // 0 primary, 1 monitor under cursor
    [ObservableProperty] public partial bool FirstRunDone { get; set; } = false;
    [ObservableProperty] public partial int FlyoutScale { get; set; } = 100;     // percent

    // ---------- Media flyout ----------
    [ObservableProperty] public partial bool MediaFlyoutEnabled { get; set; } = true;
    [ObservableProperty] public partial int MediaFlyoutPosition { get; set; } = 0; // 0 bottom-center, 1 bottom-left, 2 bottom-right, 3 top-center, 4 top-left, 5 top-right
    // clicking the media item on the taskbar opens the flyout right over it (keys still use the position above)
    [ObservableProperty] public partial bool MediaFlyoutAboveWidget { get; set; } = true;
    // device notifications go through Windows (notification centre, Do not disturb) instead of our own toast
    [ObservableProperty] public partial bool UseWindowsNotifications { get; set; } = true;
    // Bluetooth devices' charge as Windows reports it, listed in the battery panel
    [ObservableProperty] public partial bool BluetoothBatteryEnabled { get; set; } = true;
    [ObservableProperty] public partial bool GamepadBatteryEnabled { get; set; } = true;
    // ---------- In-game readout (on-screen display) ----------
    [ObservableProperty] public partial bool OverlayEnabled { get; set; } = true;
    [ObservableProperty] public partial int OverlayMode { get; set; } = 0;      // 0 only over games, 1 always
    [ObservableProperty] public partial int OverlayPosition { get; set; } = 0;  // 0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right
    [ObservableProperty] public partial int OverlayLayout { get; set; } = 0;    // 0 horizontal strip, 1 vertical list
    [ObservableProperty] public partial int OverlayFontSize { get; set; } = 14;
    [ObservableProperty] public partial int OverlayOpacity { get; set; } = 150; // background, 0..255
    [ObservableProperty] public partial bool OverlayShowCpu { get; set; } = true;
    [ObservableProperty] public partial bool OverlayShowGpu { get; set; } = true;
    [ObservableProperty] public partial bool OverlayShowTemps { get; set; } = true;
    [ObservableProperty] public partial bool OverlayShowRam { get; set; } = true;
    [ObservableProperty] public partial bool OverlayShowVram { get; set; } = false;
    [ObservableProperty] public partial bool OverlayShowPing { get; set; } = false;
    [ObservableProperty] public partial bool OverlayShowFps { get; set; } = true;
    [ObservableProperty] public partial bool OverlayShowFrametime { get; set; } = true;

    // ---------- More system readings and warnings ----------
    [ObservableProperty] public partial bool StatusShowDisk { get; set; } = false;
    [ObservableProperty] public partial bool StatusShowVram { get; set; } = false;
    [ObservableProperty] public partial bool StatusShowPing { get; set; } = false;
    [ObservableProperty] public partial bool AlertsEnabled { get; set; } = true;
    [ObservableProperty] public partial int AlertCpuTemp { get; set; } = 90;
    [ObservableProperty] public partial int AlertGpuTemp { get; set; } = 85;
    [ObservableProperty] public partial int AlertRam { get; set; } = 92;

    // ---------- More flyouts ----------
    [ObservableProperty] public partial bool BrightnessFlyoutEnabled { get; set; } = true;
    [ObservableProperty] public partial bool MicTaskbarIndicator { get; set; } = true;
    [ObservableProperty] public partial bool LayoutFlyoutEnabled { get; set; } = true;
    [ObservableProperty] public partial bool MediaFlyoutLyrics { get; set; } = true;

    // ---------- Updates ----------
    [ObservableProperty] public partial string UpdateRepo { get; set; } = "batuhanbecel/AuraFlyout";
    [ObservableProperty] public partial bool UpdateAutoCheck { get; set; } = true;

    // global shortcuts by action id ("mixer", "mic", ...): "Ctrl+Alt+V", "" = off; missing = the default
    [ObservableProperty] public partial Dictionary<string, string> Hotkeys { get; set; } = [];
    [ObservableProperty] public partial int MediaFlyoutDuration { get; set; } = 3000;
    [ObservableProperty] public partial bool MediaFlyoutOnVolumeKeys { get; set; } = true;
    [ObservableProperty] public partial bool MediaFlyoutCompact { get; set; } = false;
    [ObservableProperty] public partial bool MediaFlyoutShowSeekbar { get; set; } = true;
    [ObservableProperty] public partial bool MediaFlyoutShowRepeatShuffle { get; set; } = true;
    [ObservableProperty] public partial bool MediaFlyoutShowPlayer { get; set; } = true;
    [ObservableProperty] public partial bool MediaFlyoutShowVolume { get; set; } = true;
    [ObservableProperty] public partial bool MediaFlyoutCoverGlow { get; set; } = false; // not part of the Windows 11 look

    // ---------- Up next ----------
    [ObservableProperty] public partial bool UpNextEnabled { get; set; } = true;
    [ObservableProperty] public partial int UpNextDuration { get; set; } = 2500;
    [ObservableProperty] public partial int UpNextPosition { get; set; } = 0;

    // ---------- Lock keys ----------
    [ObservableProperty] public partial bool LockKeysEnabled { get; set; } = true;
    [ObservableProperty] public partial bool LockKeysCaps { get; set; } = true;
    [ObservableProperty] public partial bool LockKeysNum { get; set; } = true;
    [ObservableProperty] public partial bool LockKeysScroll { get; set; } = true;
    [ObservableProperty] public partial bool LockKeysInsert { get; set; } = false;
    [ObservableProperty] public partial int LockKeysDuration { get; set; } = 1500;
    [ObservableProperty] public partial int LockKeysPosition { get; set; } = 0;

    // ---------- Volume ----------
    [ObservableProperty] public partial bool VolumeFlyoutEnabled { get; set; } = true;
    [ObservableProperty] public partial bool HideNativeVolumeOsd { get; set; } = true;
    [ObservableProperty] public partial int VolumeFlyoutDuration { get; set; } = 2500;
    [ObservableProperty] public partial bool VolumeShortcutEnabled { get; set; } = true; // Ctrl+Alt+V opens the mixer
    [ObservableProperty] public partial bool VolumeMixerHighlightActive { get; set; } = true; // Pro
    [ObservableProperty] public partial int VolumeStep { get; set; } = 2;
    [ObservableProperty] public partial int VolumeFlyoutPosition { get; set; } = 0; // like MediaFlyoutPosition; bottom-centre, where Windows shows its own (the brightness flyout too)
    [ObservableProperty] public partial bool TraySpeakerOpensMixer { get; set; } = true; // the taskbar's speaker icon opens our mixer (and its wheel uses our feedback)

    // ---------- Taskbar widget (Pro) ----------
    [ObservableProperty] public partial bool TaskbarWidgetEnabled { get; set; } = true;
    [ObservableProperty] public partial int TaskbarWidgetPosition { get; set; } = 0; // 0 left, 1 center, 2 right (next to tray)
    [ObservableProperty] public partial int TaskbarWidgetOffset { get; set; } = 0;   // manual padding (px)
    [ObservableProperty] public partial bool TaskbarWidgetShowControls { get; set; } = true;
    [ObservableProperty] public partial bool TaskbarWidgetShowCover { get; set; } = true;
    [ObservableProperty] public partial bool TaskbarWidgetHideWhenIdle { get; set; } = true;
    [ObservableProperty] public partial bool TaskbarWidgetScrollVolume { get; set; } = true;
    [ObservableProperty] public partial bool TaskbarWidgetHoverBackground { get; set; } = true;
    [ObservableProperty] public partial bool TaskbarWidgetFixedWidth { get; set; } = false;
    [ObservableProperty] public partial int TaskbarWidgetWidth { get; set; } = 220;
    [ObservableProperty] public partial bool TaskbarWidgetClickOpensFlyout { get; set; } = true;
    [ObservableProperty] public partial int TaskbarWidgetMonitor { get; set; } = 0;
    [ObservableProperty] public partial bool TaskbarWidgetPauseDim { get; set; } = true;

    // ---------- Visualizer (Pro) ----------
    [ObservableProperty] public partial bool VisualizerEnabled { get; set; } = true;
    [ObservableProperty] public partial int VisualizerPosition { get; set; } = 2; // 0 before widget, 1 after widget, 2 next to the title (in the widget)
    [ObservableProperty] public partial int VisualizerBarCount { get; set; } = 10;
    [ObservableProperty] public partial bool VisualizerCentered { get; set; } = true;
    [ObservableProperty] public partial int VisualizerSensitivity { get; set; } = 1; // 0 low, 1 medium, 2 high
    [ObservableProperty] public partial bool VisualizerUseAccent { get; set; } = true;
    [ObservableProperty] public partial bool VisualizerInFlyout { get; set; } = true;

    // ---------- App filtering ----------
    [ObservableProperty] public partial ObservableCollection<string> ExcludedApps { get; set; } = [];

    // ---------- Taskbar layout ----------
    // Anchor: 0 left, 1 center, 2 right (next to the tray). Order: lower = closer to the anchor edge.
    [ObservableProperty] public partial int TaskbarMediaOrder { get; set; } = 1;
    [ObservableProperty] public partial int TaskbarGap { get; set; } = 8;
    /// <summary>Text size / weight of every taskbar item (12 px regular = the taskbar clock).</summary>
    [ObservableProperty] public partial int TaskbarFontSize { get; set; } = 12;
    [ObservableProperty] public partial bool TaskbarBold { get; set; } = false;
    /// <summary>Left-to-right order of the taskbar items, see <see cref="Core.TaskbarLayout"/>.</summary>
    [ObservableProperty] public partial List<string> TaskbarLayout { get; set; } = [];

    // ---------- System status (ex TaskbarStatus) ----------
    [ObservableProperty] public partial bool StatusEnabled { get; set; } = true;
    [ObservableProperty] public partial bool StatusShowCpu { get; set; } = true;
    [ObservableProperty] public partial bool StatusShowCpuTemp { get; set; } = true;
    [ObservableProperty] public partial bool StatusShowGpu { get; set; } = true;
    [ObservableProperty] public partial bool StatusShowGpuTemp { get; set; } = true;
    [ObservableProperty] public partial bool StatusShowRam { get; set; } = true;
    [ObservableProperty] public partial bool StatusShowFps { get; set; } = true;
    [ObservableProperty] public partial bool StatusShowNet { get; set; } = false;
    [ObservableProperty] public partial bool StatusTwoLines { get; set; } = false;
    [ObservableProperty] public partial bool StatusBold { get; set; } = false;
    [ObservableProperty] public partial bool StatusShowLabels { get; set; } = true;
    [ObservableProperty] public partial bool StatusShowGlyphs { get; set; } = true;
    [ObservableProperty] public partial bool StatusColorWarnings { get; set; } = true;
    [ObservableProperty] public partial bool StatusRamAsGb { get; set; } = false;
    [ObservableProperty] public partial int StatusFontSize { get; set; } = 12;
    [ObservableProperty] public partial int StatusInterval { get; set; } = 1000;
    [ObservableProperty] public partial int StatusAnchor { get; set; } = 0;
    [ObservableProperty] public partial int StatusOrder { get; set; } = 0;
    [ObservableProperty] public partial int StatusOffset { get; set; } = 0;

    // ---------- Logitech battery (ex TaskbarLogiBattery) ----------
    [ObservableProperty] public partial bool LogiEnabled { get; set; } = true;
    [ObservableProperty] public partial bool LogiNotifyConnect { get; set; } = true;
    [ObservableProperty] public partial bool LogiNotifyDisconnect { get; set; } = true;
    [ObservableProperty] public partial bool LogiNotifyLow { get; set; } = true;
    [ObservableProperty] public partial bool LogiNotifyFull { get; set; } = true;
    [ObservableProperty] public partial int LogiLowThreshold { get; set; } = 20;
    [ObservableProperty] public partial int LogiPollSeconds { get; set; } = 60;
    [ObservableProperty] public partial bool LogiShowPercent { get; set; } = true;
    [ObservableProperty] public partial bool LogiDebugLog { get; set; } = false;
    [ObservableProperty] public partial int LogiAnchor { get; set; } = 2;
    [ObservableProperty] public partial int LogiOrder { get; set; } = 0;
    [ObservableProperty] public partial int LogiOffset { get; set; } = 0;
    [ObservableProperty] public partial ObservableCollection<string> LogiHidden { get; set; } = [];
    [ObservableProperty] public partial Dictionary<string, string> LogiAliases { get; set; } = [];

    // ---------- Speed test (ex TaskbarSpeedTest) ----------
    [ObservableProperty] public partial bool SpeedEnabled { get; set; } = true;
    [ObservableProperty] public partial bool SpeedWidgetDownload { get; set; } = true;
    [ObservableProperty] public partial bool SpeedWidgetUpload { get; set; } = true;
    [ObservableProperty] public partial bool SpeedWidgetPing { get; set; } = false;
    [ObservableProperty] public partial bool SpeedWidgetUnits { get; set; } = true;
    [ObservableProperty] public partial bool SpeedTwoLines { get; set; } = true;
    [ObservableProperty] public partial int SpeedUnit { get; set; } = 0;          // 0 Mbps, 1 MB/s
    [ObservableProperty] public partial int SpeedSeconds { get; set; } = 10;      // per direction
    [ObservableProperty] public partial int SpeedAutoTestHours { get; set; } = 0; // 0 = off
    [ObservableProperty] public partial int SpeedAnchor { get; set; } = 2;
    [ObservableProperty] public partial int SpeedOrder { get; set; } = 1;
    [ObservableProperty] public partial int SpeedOffset { get; set; } = 0;
    [ObservableProperty] public partial List<string> SpeedHistory { get; set; } = []; // newest first, SpeedResult.Serialize()

    // ---------- Elevation / migration ----------
    [ObservableProperty] public partial bool RunElevated { get; set; } = false; // CPU temperature (PawnIO) and FPS (ETW)
    [ObservableProperty] public partial bool ModulesImported { get; set; } = false;

    /// <summary>Raises PropertyChanged for a collection-typed setting that was changed in place.</summary>
    public void Touch(string propertyName) => OnPropertyChanged(propertyName);

    [JsonIgnore]
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuraFlyout");
}

/// <summary>Loads and persists <see cref="AppSettings"/> as JSON under %AppData%\AuraFlyout.</summary>
public static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(AppSettings.Folder, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static DispatcherTimer? _saveTimer;

    public static AppSettings Current { get; private set; } = new();

    private static bool _readOnly;

    /// <summary>Loads the settings. <paramref name="readOnly"/> never writes them back (preview / test runs).</summary>
    public static void Load(bool readOnly = false)
    {
        _readOnly = readOnly;
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded != null) Current = loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings could not be read, defaults are used", ex);
            Current = new AppSettings();
        }

        int version = Current.SettingsVersion;
        Migrate(Current);
        Current.PropertyChanged += (_, _) => ScheduleSave();
        Current.ExcludedApps.CollectionChanged += (_, _) => ScheduleSave();
        Current.LogiHidden.CollectionChanged += (_, _) => ScheduleSave();
        // a migration is written back once, or it would run again on every start until something changes
        if (Current.SettingsVersion != version) ScheduleSave();
    }

    private static void Migrate(AppSettings s)
    {
        if (s.SettingsVersion < 2)
        {
            // v2: the Windows 11 system acrylic became the default material; "opacity" is now an extra tint
            s.Backdrop = 0;
            s.AcrylicOpacity = 0;
            s.SettingsVersion = 2;
        }
        if (s.SettingsVersion < 3)
        {
            // v3: one text style for all taskbar items (was a system-status-only setting)
            s.TaskbarFontSize = Math.Clamp(s.StatusFontSize, 9, 18);
            s.TaskbarBold = s.StatusBold;
            s.SettingsVersion = 3;
        }
        if (s.SettingsVersion < 4)
        {
            // v4: look exactly like the Windows 11 shell flyouts: system acrylic, no extra tint, no cover glow
            s.Backdrop = 0;
            s.AcrylicOpacity = 0;
            s.MediaFlyoutCoverGlow = false;
            s.SettingsVersion = 4;
        }
        if (s.SettingsVersion < 5)
        {
            // v5: tint calibrated against the shell's own flyouts
            s.AcrylicOpacity = AppSettings.WindowsAcrylicTint;
            s.SettingsVersion = 5;
        }
        if (s.SettingsVersion < 6)
        {
            // v6: Ctrl+Alt+V became one of the configurable shortcuts
            s.Hotkeys ??= [];
            if (!s.VolumeShortcutEnabled) s.Hotkeys["mixer"] = "";
            s.SettingsVersion = 6;
        }
        if (s.SettingsVersion < 7)
        {
            // v7: the taskbar visualizer moved next to the song's title by default
            s.VisualizerPosition = 2;
            s.SettingsVersion = 7;
        }
        s.Hotkeys ??= [];
    }

    private static void ScheduleSave()
    {
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            Save();
        }, Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes the settings to a file of the user's choice (a backup, or for another PC).</summary>
    public static void Export(string path) => File.WriteAllText(path, JsonSerializer.Serialize(Current, JsonOptions));

    /// <summary>
    /// Takes over the settings of an exported file onto the live object (so every window updates).
    /// Machine-bound state (administrator task, first-run flags) stays as it is on this PC.
    /// </summary>
    public static void Import(string path)
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
                     ?? throw new InvalidDataException("Not an AuraFlyout settings file.");
        Migrate(loaded);
        string[] keep = [nameof(AppSettings.RunElevated), nameof(AppSettings.FirstRunDone), nameof(AppSettings.ModulesImported), nameof(AppSettings.SettingsVersion)];
        foreach (var p in typeof(AppSettings).GetProperties())
        {
            if (!p.CanRead || !p.CanWrite || keep.Contains(p.Name) || p.GetCustomAttributes(typeof(JsonIgnoreAttribute), true).Length > 0) continue;
            object? value = p.GetValue(loaded);
            if (p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(System.Collections.ObjectModel.ObservableCollection<>))
            {
                // live collections keep their instance (others are subscribed to them)
                var target = (System.Collections.IList)p.GetValue(Current)!;
                target.Clear();
                foreach (object? item in (System.Collections.IEnumerable)value!) target.Add(item);
                continue;
            }
            p.SetValue(Current, value);
        }
        Save();
    }

    public static void Save()
    {
        if (_readOnly) return;
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Settings could not be saved", ex);
        }
    }
}
