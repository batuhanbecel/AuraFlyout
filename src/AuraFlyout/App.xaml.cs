using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AuraFlyout.Audio;
using AuraFlyout.Core;
using AuraFlyout.Flyouts;
using AuraFlyout.Media;
using AuraFlyout.Modules.LogiBattery;
using AuraFlyout.Modules.SpeedTest;
using AuraFlyout.Modules.SystemStatus;
using AuraFlyout.Settings;
using AuraFlyout.Taskbar;
using Wpf.Ui.Controls;
using Log = AuraFlyout.Core.Log;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AuraFlyout;

public partial class App : Application
{
    private const string MutexName = @"Local\AuraFlyout.Instance";
    private const string ShowEventName = @"Local\AuraFlyout.ShowSettings";
    private const string ExitEventName = @"Local\AuraFlyout.Exit";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private KeyboardHook? _hook;
    private Taskbar.TraySpeaker? _speaker;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _spectrumTimer;
    private long _spectrumIdleSince;

    /// <summary>Taskbar items by name, with the setting(s) that switch each one on.</summary>
    private readonly Dictionary<string, DockWindow> _docks = [];
    private readonly Dictionary<string, (Func<bool> Wanted, Func<DockWindow> Create)> _dockFactories = new()
    {
        ["media"] = (() => SettingsStore.Current.TaskbarWidgetEnabled || SettingsStore.Current.VisualizerEnabled, () => new TaskbarHost()),
        ["status"] = (() => SettingsStore.Current.StatusEnabled, () => new StatusDock()),
        ["logi"] = (() => SettingsStore.Current.LogiEnabled, () => new BatteryDock()),
        ["speed"] = (() => SettingsStore.Current.SpeedEnabled, () => new SpeedDock()),
        ["mic"] = (() => SettingsStore.Current.MicTaskbarIndicator, () => new MicDock()),
    };

    public static App Instance => (App)Current;
    public static MediaService Media { get; private set; } = null!;
    public static AudioService Audio { get; private set; } = null!;
    public static SpectrumAnalyzer Spectrum { get; private set; } = null!;
    public static FlyoutManager Flyouts { get; private set; } = null!;
    public static PanelManager Panels { get; private set; } = null!;
    public static SystemStatusService Status { get; private set; } = null!;
    public static LogiBatteryService Logi { get; private set; } = null!;
    public static SpeedTestService Speed { get; private set; } = null!;
    public static Modules.Bluetooth.BluetoothBatteryService Bluetooth { get; private set; } = null!;
    public static Modules.Bluetooth.BluetoothAudio BluetoothAudio { get; private set; } = new();
    public static Modules.Gamepad.GamepadBatteryService Gamepads { get; private set; } = new();
    public static Modules.Display.BrightnessService Brightness { get; private set; } = null!;
    public static MicrophoneService Mic { get; private set; } = null!;
    public static LyricsService Lyrics { get; private set; } = new();
    public static Overlay.GameOverlay? Overlay { get; private set; }
    private KeyboardLayoutWatcher? _layoutWatcher;
    private DispatcherTimer? _updateTimer;
    public static bool IsShuttingDown { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // elevated helper: only (un)registers the scheduled task and exits
        if (e.Args.Contains(Elevation.RegisterArg))
        {
            Shutdown(Elevation.HandleRegisterArgs(e.Args));
            return;
        }

        int backdropTest = Array.IndexOf(e.Args, "--backdrop-test");
        if (backdropTest >= 0)
        {
            await BackdropTestAsync(backdropTest + 1 < e.Args.Length && int.TryParse(e.Args[backdropTest + 1], out int m) ? m : 0);
            Shutdown();
            return;
        }

        // development: one Windows notification (exit code 0 = shown through Windows)
        if (e.Args.Contains("--test-notify"))
        {
            SettingsStore.Load(readOnly: true);
            L10n.Apply(SettingsStore.Current.Language);
            ThemeService.Initialize();
            bool ok = WindowsNotifications.Show("PRO X 2 LIGHTSPEED", L10n.Get("Logi_EvLowText"), "", warning: true);
            await Task.Delay(1500);
            Shutdown(ok ? 0 : 1);
            return;
        }

        // development: log the paired Bluetooth audio devices (no connect / disconnect is sent)
        if (e.Args.Contains("--test-btaudio"))
        {
            BluetoothAudio.Refresh();
            await Task.Delay(4000);
            var devices = BluetoothAudio.Devices.ToList();
            // off the UI thread, like a real connect / disconnect
            var probes = await Task.Run(() => devices.Select(d => BluetoothAudio.Probe(d)).ToList());
            Log.Info("Bluetooth audio: " + string.Join(", ", devices.Select((d, i) => $"{d.Name} connected={d.Connected} endpoints={d.EndpointIds.Count} [{probes[i]}]"))
                     + (BluetoothAudio.Devices.Count == 0 ? "none" : ""));
            Shutdown();
            return;
        }

        // development: log the Bluetooth batteries Windows reports
        if (e.Args.Contains("--test-bluetooth"))
        {
            SettingsStore.Load(readOnly: true);
            Logi = new LogiBatteryService();
            Modules.Bluetooth.BluetoothBatteryService.Verbose = true;
            Bluetooth = new Modules.Bluetooth.BluetoothBatteryService();
            Bluetooth.Start();
            Gamepads.Start();
            await Task.Delay(4000);
            Log.Info("Bluetooth batteries: " + string.Join(", ", Bluetooth.Devices.Select(d => $"{d.Name} {d.PercentText} {d.StatusText} [{d.Glyph[0]:X4}]")) + (Bluetooth.Devices.Count == 0 ? "none" : ""));
            Log.Info("Game controllers: " + string.Join(", ", Gamepads.Devices.Select(d => $"{d.Name} {d.PercentText} {d.StatusText}")) + (Gamepads.Devices.Count == 0 ? "none" : ""));
            foreach (var c in global::Windows.Gaming.Input.RawGameController.RawGameControllers)
            {
                var b = c.TryGetBatteryReport();
                Log.Info($"WGI: {c.DisplayName} {c.HardwareVendorId:X4}:{c.HardwareProductId:X4} wireless={c.IsWireless} id={c.NonRoamableId} " +
                         $"battery={b?.Status} {b?.RemainingCapacityInMilliwattHours}/{b?.FullChargeCapacityInMilliwattHours} rate={b?.ChargeRateInMilliwatts}");
            }
            Shutdown();
            return;
        }

        // Settings > Apps > Uninstall: confirm, close the running copy, remove the installation
        if (e.Args.Contains(Installer.UninstallArg))
        {
            SettingsStore.Load(readOnly: true);
            L10n.Apply(SettingsStore.Current.Language);
            ThemeService.Initialize();
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = L10n.Get("Install_Uninstall"),
                Content = L10n.Get("Install_UninstallConfirm"),
                PrimaryButtonText = L10n.Get("Common_Yes"),
                CloseButtonText = L10n.Get("Common_No"),
            };
            if (await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                Ipc.Signal(ExitEventName);
                await Task.Delay(1500);
                Installer.Uninstall();
            }
            Shutdown();
            return;
        }

        int preview = Array.IndexOf(e.Args, "--render-previews");
        if (preview >= 0)
        {
            string? Arg(string name) => Array.IndexOf(e.Args, name) is int i and >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null;
            await RenderPreviewsAsync(preview + 1 < e.Args.Length ? e.Args[preview + 1] : "previews", Arg("--lang"), Arg("--theme"));
            Shutdown();
            return;
        }

        _mutex = Ipc.CreateMutex(MutexName, out bool firstInstance);
        bool exitRequest = e.Args.Contains("--exit");
        if (!firstInstance || exitRequest)
        {
            // already running: open its settings, or ask it to quit ("--exit")
            Ipc.Signal(exitRequest ? ExitEventName : ShowEventName);
            if (firstInstance) _mutex.ReleaseMutex();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warn("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        Log.Info($"AuraFlyout starting (admin: {Elevation.IsAdmin})");
        SettingsStore.Load();

        // run-as-administrator mode: hop through the elevated task (no UAC prompt)
        if (SettingsStore.Current.RunElevated && !Elevation.IsAdmin && !e.Args.Contains(Elevation.FromTaskArg) && Elevation.TaskExists())
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
            if (Elevation.RunTask())
            {
                Shutdown();
                return;
            }
            _mutex = Ipc.CreateMutex(MutexName, out _); // task failed: carry on unelevated
        }

        L10n.Apply(SettingsStore.Current.Language);
        WindowsNotifications.Register(); // app id first: before any window or notification exists
        if (e.Args.Contains(Installer.FinishArg)) Installer.Finish(startup: e.Args.Contains("startup"));
        Updater.CleanUp();
        ThemeService.Initialize();
        LegacyImport.RunOnce();

        Audio = new AudioService();
        Spectrum = new SpectrumAnalyzer();
        Media = new MediaService();
        await Media.InitializeAsync();

        Status = new SystemStatusService();
        Status.Start();
        Logi = new LogiBatteryService();
        Logi.Notify += (title, text, glyph, warning) => Panels.ShowToast(title, text, glyph, warning);
        Logi.Start();
        Bluetooth = new Modules.Bluetooth.BluetoothBatteryService();
        Bluetooth.Start();
        Gamepads.Start();
        Speed = new SpeedTestService();
        Speed.Start();
        Brightness = new Modules.Display.BrightnessService();
        Brightness.Start();
        Mic = new MicrophoneService();
        Lyrics.Start();

        Flyouts = new FlyoutManager();
        Panels = new PanelManager();
        MainWindow = null; // a flyout must never be treated as the main window

        _hook = new KeyboardHook();
        _hook.KeyEvent += Flyouts.OnKey;
        _hook.Takes = Flyouts.TakesKey;

        // the taskbar's speaker icon opens our mixer instead of the quick settings
        _speaker = new Taskbar.TraySpeaker();
        _speaker.Clicked += Flyouts.ToggleMixer;
        _speaker.Wheel += Flyouts.OnSpeakerWheel;
        void SyncSpeaker()
        {
            _speaker.Enabled = SettingsStore.Current.TraySpeakerOpensMixer;
            _speaker.DeviceName = Audio.DeviceName;
        }
        SyncSpeaker();
        SettingsStore.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.TraySpeakerOpensMixer)) SyncSpeaker();
        };
        Audio.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AudioService.DeviceName)) SyncSpeaker();
        };

        DefineHotkeys();
        HotkeyManager.Start();
        Overlay = new Overlay.GameOverlay();
        Overlay.Start();
        _layoutWatcher = new KeyboardLayoutWatcher();
        _layoutWatcher.Switched += (title, subtitle) =>
        {
            if (SettingsStore.Current.LayoutFlyoutEnabled && !(SettingsStore.Current.DisableInFullscreen && FullscreenDetector.IsFullscreenAppRunning()))
                Flyouts.LockKeys.ShowState(title, true, "\uE765", "\uE765", "", "", indicator: false, text: subtitle);
        };
        StartUpdateChecks();
        Panels.Prepare(); // the panels exist before the first click
        RunLater(20000, TrimMemory); // once everything has started

        _tray = new TrayIcon { MenuFactory = BuildTrayMenu };
        _tray.LeftClick += () => Flyouts.ToggleMedia();
        _tray.DoubleClick += () => OpenSettings();
        _tray.TaskbarCreated += OnTaskbarCreated;
        TaskbarDock.DockLost += OnDockLost;
        _tray.Visible = !SettingsStore.Current.HideTrayIcon;
        Media.Updated += UpdateTrayTooltip;

        EnsureDocks();
        StartSpectrumController();
        Audio.DeviceChanged += () =>
        {
            if (Spectrum.IsRunning) Spectrum.Start(Audio.Device);
        };

        SettingsStore.Current.PropertyChanged += OnSettingChanged;

        _showEvent = Ipc.CreateEvent(ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                if (IsShuttingDown) return;
                Dispatcher.BeginInvoke(() => OpenSettings());
            }
        }) { IsBackground = true, Name = "AuraFlyout.ShowListener" };
        listener.Start();

        var exitEvent = Ipc.CreateEvent(ExitEventName);
        new Thread(() =>
        {
            exitEvent.WaitOne();
            Dispatcher.BeginInvoke(() => ExitApp());
        }) { IsBackground = true, Name = "AuraFlyout.ExitListener" }.Start();

        bool silent = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (!SettingsStore.Current.FirstRunDone || !silent)
        {
            SettingsStore.Current.FirstRunDone = true;
            OpenSettings();
        }
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        var s = SettingsStore.Current;
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Language):
                L10n.Apply(s.Language);
                (_docks.GetValueOrDefault("media") as TaskbarHost)?.UpdateUi();
                break;
            case nameof(AppSettings.Theme):
                ThemeService.Apply();
                break;
            case nameof(AppSettings.HideTrayIcon):
                if (_tray != null) _tray.Visible = !s.HideTrayIcon;
                break;
            case nameof(AppSettings.TaskbarWidgetEnabled) or nameof(AppSettings.VisualizerEnabled)
                or nameof(AppSettings.StatusEnabled) or nameof(AppSettings.LogiEnabled) or nameof(AppSettings.SpeedEnabled)
                or nameof(AppSettings.MicTaskbarIndicator):
                EnsureDocks();
                break;
        }
    }

    // ---------- windows ----------

    /// <summary>Opens the settings window, optionally on the page with the given navigation key.</summary>
    public void OpenSettings(string? page = null)
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                MainWindow = null;
                RunLater(2000, TrimMemory); // the pages and their visuals are gone: give the memory back
            };
            MainWindow = _settingsWindow;
            _settingsWindow.Show();
        }
        if (page != null) _settingsWindow.Navigate(page);
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
        _settingsWindow.Topmost = true;
        _settingsWindow.Topmost = false;
        _settingsWindow.Focus();
    }

    /// <summary>
    /// A taskbar item died with explorer's taskbar (WPF does not always report that as Closed): drop
    /// it, release what it holds, and create it again once the new taskbar is there.
    /// </summary>
    private void OnDockLost(DockWindow dead)
    {
        foreach (var name in _docks.Where(kv => kv.Value == dead).Select(kv => kv.Key).ToList()) _docks.Remove(name);
        dead.Release(); // stops its event handlers; the HWND is already gone
        if (!_rebuildPending)
        {
            _rebuildPending = true;
            RunLater(1500, () =>
            {
                _rebuildPending = false;
                TaskbarDock.Reattach();
                EnsureDocks();
            });
        }
    }

    private bool _rebuildPending;

    /// <summary>Creates / closes the taskbar items that are switched on / off.</summary>
    private void EnsureDocks()
    {
        if (IsShuttingDown) return;
        if (Native.FindWindow("Shell_TrayWnd", null) == IntPtr.Zero)
        {
            // explorer not ready yet: try again shortly
            RunLater(2000, EnsureDocks);
            return;
        }

        foreach (var (name, (wanted, create)) in _dockFactories)
        {
            bool want = wanted();
            _docks.TryGetValue(name, out var dock);
            if (dock != null && dock.Handle != IntPtr.Zero && !Native.IsWindow(dock.Handle))
            {
                // its window went down with the old taskbar: build a new one
                _docks.Remove(name);
                dock.Release();
                dock = null;
            }
            if (want && dock == null)
            {
                try
                {
                    dock = create();
                    _docks[name] = dock;
                    var self = dock;
                    dock.Closed += (_, _) =>
                    {
                        if (_docks.GetValueOrDefault(name) == self) _docks.Remove(name);
                        // destroyed together with the taskbar (explorer restart): come back
                        if (!IsShuttingDown) RunLater(2000, EnsureDocks);
                    };
                    dock.Show();
                }
                catch (Exception ex)
                {
                    Log.Error($"Taskbar item '{name}' failed", ex);
                    _docks.Remove(name);
                }
            }
            else if (!want && dock != null)
            {
                _docks.Remove(name);
                dock.Close();
            }
        }
    }

    private void OnTaskbarCreated()
    {
        // explorer.exe restarted: re-dock the items once the new taskbar is ready
        RunLater(1500, () =>
        {
            TaskbarDock.Reattach();
            EnsureDocks();
        });
    }

    /// <summary>
    /// Gives start-up memory back: a full GC, then the working set is trimmed so pages that are no longer
    /// used (start-up code, the closed settings window) leave RAM. Windows brings back what is needed.
    /// </summary>
    private static void TrimMemory()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            SetProcessWorkingSetSize(self.Handle, -1, -1);
        }
        catch
        {
            // best effort
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    private static void RunLater(int ms, Action action)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            action();
        };
        t.Start();
    }

    // ---------- run as administrator (CPU temperature, FPS) ----------

    /// <summary>
    /// Turns the elevated start on or off; on → restarts through the elevated task.
    /// Always asks first: with UAC set to "elevate without prompting" nothing else would.
    /// </summary>
    public async Task EnableElevationAsync(bool on)
    {
        var s = SettingsStore.Current;
        bool startup = StartupManager.IsEnabled;
        if (on)
        {
            var confirm = new Wpf.Ui.Controls.MessageBox
            {
                Title = L10n.Get("System_Elevated"),
                Content = L10n.Get("System_ElevatedConfirm"),
                PrimaryButtonText = L10n.Get("Common_Yes"),
                CloseButtonText = L10n.Get("Common_No"),
                Topmost = true,
            };
            if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

            bool ok = Elevation.IsAdmin
                ? Elevation.Register(startup)
                : Elevation.RunElevatedAndWait($"{Elevation.RegisterArg} {(startup ? "on" : "nologon")}");
            if (!ok)
            {
                s.RunElevated = false;
                return;
            }
            s.RunElevated = true;
            if (startup) StartupManager.SetRunKey(false); // the task's logon trigger starts us now
            SettingsStore.Save();
            if (!Elevation.IsAdmin) RestartThroughTask();
        }
        else
        {
            s.RunElevated = false;
            if (Elevation.IsAdmin) Elevation.Unregister();
            else Elevation.RunElevatedAndWait($"{Elevation.RegisterArg} off");
            if (startup) StartupManager.SetRunKey(true);
            SettingsStore.Save();
        }
    }

    private void RestartThroughTask()
    {
        Log.Info("Restarting elevated");
        ExitApp(restart: () => Elevation.RunTask());
    }

    // ---------- visualizer capture lifecycle ----------

    private void StartSpectrumController()
    {
        _spectrumTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _spectrumTimer.Tick += (_, _) =>
        {
            bool want = SettingsStore.Current.VisualizerEnabled && Media.IsPlaying;
            bool wasRunning = Spectrum.IsRunning;
            if (want)
            {
                _spectrumIdleSince = 0;
                if (!Spectrum.IsRunning) Spectrum.Start(Audio.Device);
            }
            else if (Spectrum.IsRunning)
            {
                // keep capturing a few seconds so quick pauses / track skips do not restart it
                if (_spectrumIdleSince == 0) _spectrumIdleSince = Environment.TickCount64;
                else if (Environment.TickCount64 - _spectrumIdleSince > 5000) Spectrum.Stop();
            }
            if (wasRunning != Spectrum.IsRunning) (_docks.GetValueOrDefault("media") as TaskbarHost)?.OnSpectrumStateChanged();
        };
        _spectrumTimer.Start();
    }

    // ---------- shortcuts, microphone, updates ----------

    private void DefineHotkeys()
    {
        HotkeyManager.Define(new("mixer", "Hotkey_Mixer", "Hotkey_MixerDesc", "Ctrl+Alt+V", () => Flyouts.Volume.Show(expanded: true)));
        HotkeyManager.Define(new("media", "Hotkey_Media", "Hotkey_MediaDesc", "", () => Flyouts.ShowMedia(force: true)));
        HotkeyManager.Define(new("mic", "Hotkey_Mic", "Hotkey_MicDesc", "Ctrl+Alt+M", () => ToggleMicrophone()));
        HotkeyManager.Define(new("brightness", "Hotkey_Brightness", "Hotkey_BrightnessDesc", "Ctrl+Alt+B", () => Flyouts.Brightness.ShowNow()));
        HotkeyManager.Define(new("overlay", "Hotkey_Overlay", "Hotkey_OverlayDesc", "Ctrl+Alt+O", () => Overlay?.ToggleByHotkey()));
        HotkeyManager.Define(new("speedtest", "Hotkey_Speed", "Hotkey_SpeedDesc", "", () => Speed.Toggle()));
        HotkeyManager.Define(new("settings", "Hotkey_Settings", "Hotkey_SettingsDesc", "", () => OpenSettings()));
    }

    /// <summary>Mutes / unmutes the microphone and shows the new state, like Windows' own microphone mute.</summary>
    public void ToggleMicrophone()
    {
        bool muted = Mic.Toggle();
        Flyouts.LockKeys.ShowState(L10n.Get("Mic_Title"), !muted, "\uE720", "\uEC54", "Mic_OnLine", "Mic_OffLine", indicator: true);
    }

    /// <summary>Looks for a new release a minute after start and once a day; a Windows notification tells about it.</summary>
    private void StartUpdateChecks()
    {
        string notified = "";
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(24);
            if (!SettingsStore.Current.UpdateAutoCheck) return;
            var release = await Updater.CheckAsync();
            if (release == null || release.Version <= Updater.Current || notified == release.Version.ToString()) return;
            notified = release.Version.ToString();
            Panels.ShowToast("AuraFlyout", L10n.Format("Update_Available", "v" + release.Version.ToString(3)), "\uE896", warning: false);
        };
        _updateTimer.Start();
    }

    // ---------- tray ----------

    private List<TrayMenuItem> BuildTrayMenu()
    {
        var items = new List<TrayMenuItem>
        {
            new(L10n.Get("Tray_ShowMedia"), () => Flyouts.ShowMedia(force: true)),
            new(L10n.Get("Tray_Mixer"), () => Flyouts.Volume.Show(expanded: true)),
            new(L10n.Get("Tray_Brightness"), () => Flyouts.Brightness.ShowNow()),
            new(L10n.Get(Mic.IsMuted ? "Tray_MicOn" : "Tray_MicOff"), () => ToggleMicrophone()),
        };
        if (SettingsStore.Current.SpeedEnabled)
            items.Add(new(L10n.Get(Speed.IsRunning ? "Speed_Stop" : "Speed_RunTest"), () => Speed.Toggle()));
        items.Add(TrayMenuItem.Line);
        items.Add(new(L10n.Get("Tray_Settings"), () => OpenSettings()));
        items.Add(new(L10n.Get("Tray_Exit"), () => ExitApp()));
        return items;
    }

    private void UpdateTrayTooltip()
    {
        if (_tray == null) return;
        string tip = Media.HasSession && !string.IsNullOrEmpty(Media.Title)
            ? $"AuraFlyout\n{Media.Title}{(string.IsNullOrEmpty(Media.Artist) ? "" : " – " + Media.Artist)}"
            : "AuraFlyout";
        _tray.SetTooltip(tip);
    }

    // ---------- backdrop test (development) ----------

    /// <summary>
    /// "--backdrop-test &lt;mode&gt;": shows the media flyout and the three panels for 10 s without
    /// activating them (no focus is taken), so the real material can be checked on screen.
    /// </summary>
    private static async Task BackdropTestAsync(int mode)
    {
        SettingsStore.Load(readOnly: true);
        SettingsStore.Current.Backdrop = mode >= 7 ? 0 : mode;
        L10n.Apply(SettingsStore.Current.Language);
        ThemeService.Initialize();
        Audio = new AudioService();
        Spectrum = new SpectrumAnalyzer();
        Media = new MediaService();
        await Media.InitializeAsync();
        Status = new SystemStatusService();
        Status.Start();
        Logi = new LogiBatteryService();
        Logi.Start();
        Bluetooth = new Modules.Bluetooth.BluetoothBatteryService();
        Bluetooth.Start();
        Gamepads.Start();
        Speed = new SpeedTestService();
        Speed.Start();
        Brightness = new Modules.Display.BrightnessService();
        Brightness.Start();
        Mic = new MicrophoneService();
        await Task.Delay(2500);

        FlyoutWindow.TestMode = true;
        var wa = MonitorHelper.Primary();
        int y = wa.WorkArea.Bottom;

        // vivid stripes behind the flyouts: blur (or the lack of it) is obvious on them
        var stripes = new StackPanel();
        string[] colors = ["#FF3B30", "#FF9500", "#FFCC00", "#34C759", "#00C7BE", "#007AFF", "#AF52DE", "#FFFFFF", "#000000"];
        for (int i = 0; i < 36; i++)
            stripes.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "AURAFLYOUT BLUR TEST  ████  AURAFLYOUT BLUR TEST  ████  AURAFLYOUT BLUR TEST  ████  AURAFLYOUT BLUR TEST",
                FontSize = 26,
                FontWeight = FontWeights.Black,
                Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(colors[i % colors.Length])!,
                Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(colors[(i + 4) % colors.Length])!,
            });
        double scale = wa.Scale;
        var backdrop = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Left = wa.WorkArea.Left / scale,
            Top = (wa.WorkArea.Bottom - 760 * scale) / scale,
            Width = wa.WorkArea.Width / scale,
            Height = 760,
            Content = stripes,
        };
        if (mode is 10 or 12) backdrop.Topmost = false; // 10, 12: under the flyouts (they go right under the taskbar)
        if (mode != 7) backdrop.Show(); // 7: over the real desktop

        if (mode == 11)
        {
            // the accent after the settings window has opened
            Instance.OpenSettings();
            await Task.Delay(2500);
            PerfLog.Write("---- settings open ----");
            ThemeService.DumpAccentResources(Current.Resources, "");
            return;
        }

        if (mode == 10)
        {
            // the taskbar click path itself (focus taken, data refreshed): open, close, open again
            FlyoutWindow.TestMode = false;
            Panels = new PanelManager();
            Panels.Prepare();
            await Task.Delay(1500);
            // the stripes act as the desktop: at the very bottom, under the prepared panels
            Native.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(backdrop).Handle, new IntPtr(1), 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            await Task.Delay(300);
            Action<Native.RECT>[] toggles = [Panels.ToggleStatus, Panels.ToggleBattery, Panels.ToggleSpeed];
            int[] xs = [260, wa.Bounds.Right - 260, wa.Bounds.Right - 760];
            for (int i = 0; i < toggles.Length; i++)
            {
                for (int round = 0; round < 2; round++)
                {
                    toggles[i](At(xs[i]));
                    await Task.Delay(1300);
                    toggles[i](At(xs[i]));
                    await Task.Delay(700);
                }
            }
            return;
        }

        if (mode == 8)
        {
            // re-open after a (simulated) deactivation: the case where the dark fallback flashed
            var panel = new StatusFlyout();
            panel.ShowAt(At(260));
            await Task.Delay(1500);
            Native.SendMessage(panel.Hwnd, Native.WM_NCACTIVATE, IntPtr.Zero, IntPtr.Zero); // what a real deactivation sends
            panel.HideFlyout();
            await Task.Delay(1200);
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\AuraFlyout.TestReshow")) ready.Set();
            panel.ShowAt(At(260));
            await Task.Delay(3000);
            return;
        }

        if (mode == 12)
        {
            // the lock-key flyout: a fresh open, a state change and a key change while shown, close, a fresh open
            var lockKeys = new LockKeysFlyout();
            // the stripes on top of every ordinary window (nothing else on screen shows), under the flyout
            Native.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(backdrop).Handle, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            await Task.Delay(800);
            PerfLog.Write("---- caps on (fresh) ----");
            lockKeys.ShowKey("Caps Lock", true);
            await Task.Delay(1200);
            PerfLog.Write("---- caps off ----");
            lockKeys.ShowKey("Caps Lock", false);
            await Task.Delay(1200);
            PerfLog.Write("---- num on ----");
            lockKeys.ShowKey("Num Lock", true);
            await Task.Delay(1200);
            PerfLog.Write("---- hide ----");
            lockKeys.HideFlyout();
            await Task.Delay(1000);
            PerfLog.Write("---- scroll off (fresh) ----");
            lockKeys.ShowKey("Scroll Lock", false);
            await Task.Delay(1400);
            PerfLog.Write("---- hide ----");
            lockKeys.HideFlyout();
            await Task.Delay(800);
            return;
        }

        if (mode == 9)
        {
            double top = (wa.WorkArea.Bottom - 600 * scale) / scale;
            string[] labels = ["2: DWM transient", "4: + SizeToContent", "5: + DwmExtendFrame", "6: + ApplyBackdrop"];
            int[] variants = [2, 4, 5, 6];
            for (int i = 0; i < 4; i++) new BackdropProbe(variants[i], labels[i], 60 + i * 340, top).Show();
            // 7: a real FlyoutWindow
            var lockKeys = new LockKeysFlyout();
            lockKeys.SetKey("7: FlyoutWindow", true);
            lockKeys.ShowFlyout();
            await Task.Delay(10000);
            return;
        }
        Native.RECT At(int x) => new() { Left = x - 20, Right = x + 20, Top = y, Bottom = y + 48 };

        new MediaFlyout().Show(emphasizeVolume: true);
        new StatusFlyout().ShowAt(At(260));
        new SpeedFlyout().ShowAt(At(wa.Bounds.Right - 760));
        new BatteryFlyout().ShowAt(At(wa.Bounds.Right - 260));
        await Task.Delay(10000);
    }

    // ---------- previews (development) ----------

    private static async Task RenderPreviewsAsync(string folder, string? lang, string? theme)
    {
        SettingsStore.Load(readOnly: true);
        if (theme != null) SettingsStore.Current.Theme = theme == "light" ? 1 : 2;
        L10n.Apply(lang ?? SettingsStore.Current.Language);
        // previews in a given language also use its number and date format (README screenshots)
        if (lang != null)
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(lang == "en" ? "en-US" : "tr-TR");
            System.Globalization.CultureInfo.CurrentCulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        }
        ThemeService.Initialize();
        Audio = new AudioService();
        Spectrum = new SpectrumAnalyzer();
        Media = new MediaService();
        await Media.InitializeAsync();
        Status = new SystemStatusService();
        Status.Start();
        Logi = new LogiBatteryService();
        Logi.Start();
        Bluetooth = new Modules.Bluetooth.BluetoothBatteryService();
        Bluetooth.Start();
        Gamepads.Start();
        Speed = new SpeedTestService();
        Speed.Start();
        Brightness = new Modules.Display.BrightnessService();
        Brightness.Start();
        Mic = new MicrophoneService();
        await Task.Delay(5000); // let the sensors and the HID engine report

        var media = new MediaFlyout();
        var volume = new VolumeFlyout();
        var lockKeys = new LockKeysFlyout();
        var status = new StatusFlyout();
        var battery = new BatteryFlyout();
        var speed = new SpeedFlyout();
        var toast = new NotificationToast();

        var items = new List<(string, Func<FrameworkElement>, double)>
        {
            ("flyout_media", () => PreviewRenderer.Detach(media, () => media.UpdateLive()), 0),
            ("flyout_volume", () => PreviewRenderer.Detach(volume, () => volume.SetExpanded(true)), 0),
            ("flyout_lock", () => PreviewRenderer.Detach(lockKeys, () => lockKeys.SetKey("Caps Lock", true)), 0),
            ("flyout_toast", () => PreviewRenderer.Detach(toast, () => toast.Prepare("Logitech", $"MX Master 3S · {0.15:P0}", "", true)), 0),
            ("panel_status", () => PreviewRenderer.Detach(status, () => status.Refresh()), 0),
            ("panel_battery", () => PreviewRenderer.Detach(battery, () => battery.UpdateEmpty()), 0),
            ("panel_speed", () => PreviewRenderer.Detach(speed, () => speed.Refresh()), 0),
            ("flyout_brightness", () => PreviewRenderer.Detach(new BrightnessFlyout()), 0),
            ("overlay", () => PreviewRenderer.Detach(new Overlay.GameOverlay()), 0),
            ("overlay_list", () =>
            {
                SettingsStore.Current.OverlayLayout = 1;
                SettingsStore.Current.OverlayShowVram = true;
                SettingsStore.Current.OverlayShowPing = true;
                // read-only store, and the last overlay preview: the setting stays (resetting it would rebuild this one as a strip)
                return PreviewRenderer.Detach(new Overlay.GameOverlay());
            }, 0),
            // the taskbar media item with its visualizer next to the title, on a taskbar-coloured strip
            ("taskbar_media", () =>
            {
                var bars = new Controls.SpectrumBars { Height = 18, BarWidth = 3, BarGap = 2, BarCount = 10, Centered = true, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                bars.SetResourceReference(Controls.SpectrumBars.BarBrushProperty, "AuraTaskbarAccentBrush");
                bars.Levels = [0.3f, 0.65f, 0.95f, 0.55f, 0.8f, 0.4f, 0.9f, 0.5f, 0.25f, 0.45f];
                var title = new System.Windows.Controls.TextBlock { Text = "Midnight City" };
                title.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AuraTaskbarForeground");
                var artist = new System.Windows.Controls.TextBlock { Text = "M83" };
                artist.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AuraTaskbarSecondaryForeground");
                var text = new System.Windows.Controls.StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(title);
                text.Children.Add(artist);
                var cover = new System.Windows.Controls.Border
                {
                    Width = 32, Height = 32, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0),
                    Background = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x4E, 0x9A), System.Windows.Media.Color.FromRgb(0x3B, 0x2A, 0x8C), 45),
                };
                var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
                row.Children.Add(cover);
                row.Children.Add(text);
                row.Children.Add(bars);
                var strip = ThemeService.IsDark ? System.Windows.Media.Color.FromRgb(0x1C, 0x1C, 0x1C) : System.Windows.Media.Color.FromRgb(0xEE, 0xEE, 0xEE);
                return (FrameworkElement)new System.Windows.Controls.Border { Background = new System.Windows.Media.SolidColorBrush(strip), Child = row };
            }, 0),
        };
        // every settings page, in the order of the navigation
        foreach (var type in typeof(Settings.Pages.HomePage).Assembly.GetTypes()
                     .Where(t => t.Namespace == "AuraFlyout.Settings.Pages" && t.IsSubclassOf(typeof(UserControl)) && t.GetConstructor(Type.EmptyTypes) != null)
                     .OrderBy(t => t.Name))
        {
            items.Add(("page_" + type.Name.Replace("Page", "").ToLowerInvariant(), () =>
            {
                var page = (UserControl)Activator.CreateInstance(type)!;
                page.DataContext = SettingsStore.Current;
                page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                return page;
            }, 820));
        }
        PreviewRenderer.Render(folder, items);
    }

    // ---------- shutdown ----------

    public void ExitApp(Action? restart = null)
    {
        if (IsShuttingDown) return;
        IsShuttingDown = true;
        Log.Info("AuraFlyout exiting");

        try
        {
            _hook?.Dispose();
            _speaker?.Dispose();
            NativeOsd.Restore();
            Spectrum?.Dispose();
            _tray?.Dispose();
            foreach (var d in _docks.Values.ToList()) d.Close();
            Speed?.Cancel();
            Logi?.Dispose();
            HotkeyManager.Stop();
            _layoutWatcher?.Dispose();
            Brightness?.Dispose();
            Mic?.Dispose();
            Overlay?.Close();
            Bluetooth?.Dispose();
            Status?.Dispose();
            Audio?.Dispose();
            SettingsStore.Save();
            _showEvent?.Set();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown error", ex);
        }

        if (restart != null)
        {
            // release the single-instance mutex before the new instance starts
            try { _mutex?.ReleaseMutex(); } catch { /* not owner */ }
            _mutex?.Dispose();
            _mutex = null;
            restart();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!IsShuttingDown && _mutex != null)
        {
            IsShuttingDown = true;
            _hook?.Dispose();
            _speaker?.Dispose();
            NativeOsd.Restore();
            _tray?.Dispose();
        }
        try { _mutex?.ReleaseMutex(); } catch { /* not owner */ }
        base.OnExit(e);
    }
}
