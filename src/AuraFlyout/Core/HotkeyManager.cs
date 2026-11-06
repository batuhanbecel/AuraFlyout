using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace AuraFlyout.Core;

/// <summary>A global shortcut: what it does, its name and its default keys.</summary>
public sealed record HotkeyAction(string Id, string TitleKey, string DescKey, string DefaultKeys, Action Run);

/// <summary>
/// Global shortcuts through Windows' own RegisterHotKey: the keys reach only AuraFlyout (never the
/// app in front), and a combination another program already owns is reported instead of silently
/// doing nothing. Keys are stored as text, e.g. "Ctrl+Alt+V".
/// </summary>
public static class HotkeyManager
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private static readonly List<HotkeyAction> Actions = [];
    private static readonly Dictionary<int, HotkeyAction> Registered = [];
    private static readonly HashSet<string> Failed = [];
    private static HwndSource? _window;
    private static bool _suspended;

    public static IReadOnlyList<HotkeyAction> All => Actions;

    /// <summary>Raised after the shortcuts were (re)registered, so the settings page can show conflicts.</summary>
    public static event Action? Changed;

    public static void Define(HotkeyAction action)
    {
        Actions.RemoveAll(a => a.Id == action.Id);
        Actions.Add(action);
    }

    public static void Start()
    {
        _window = new HwndSource(new HwndSourceParameters("AuraFlyoutHotkeys") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);
        SettingsStore.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.Hotkeys)) Apply();
        };
        Apply();
    }

    /// <summary>The keys of an action (the default until the user changed it; "" = off).</summary>
    public static string KeysOf(string id) =>
        SettingsStore.Current.Hotkeys.TryGetValue(id, out var k) ? k : Actions.FirstOrDefault(a => a.Id == id)?.DefaultKeys ?? "";

    public static void SetKeys(string id, string keys)
    {
        SettingsStore.Current.Hotkeys[id] = keys;
        SettingsStore.Current.Touch(nameof(AppSettings.Hotkeys));
    }

    /// <summary>True when Windows refused the combination (another program owns it).</summary>
    public static bool IsTaken(string id) => Failed.Contains(id);

    /// <summary>Lets a key box capture any combination, including ours, while it has focus.</summary>
    public static void Suspend(bool suspend)
    {
        if (_suspended == suspend) return;
        _suspended = suspend;
        Apply();
    }

    private static void Apply()
    {
        if (_window == null) return;
        foreach (int id in Registered.Keys) UnregisterHotKey(_window.Handle, id);
        Registered.Clear();
        Failed.Clear();
        if (!_suspended)
        {
            int next = 1;
            foreach (var a in Actions)
            {
                if (!TryParse(KeysOf(a.Id), out uint mods, out uint vk)) continue;
                int id = next++;
                if (RegisterHotKey(_window.Handle, id, mods | MOD_NOREPEAT, vk)) Registered[id] = a;
                else
                {
                    Failed.Add(a.Id);
                    Log.Warn($"Shortcut {KeysOf(a.Id)} for {a.Id} is taken by another program");
                }
            }
        }
        Changed?.Invoke();
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && Registered.TryGetValue(wParam.ToInt32(), out var a))
        {
            handled = true;
            try { a.Run(); }
            catch (Exception ex) { Log.Error($"Shortcut {a.Id} failed", ex); }
        }
        return IntPtr.Zero;
    }

    // ---------- "Ctrl+Alt+V" <-> modifiers + virtual key ----------

    public static bool TryParse(string text, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (string part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default:
                    if (!Enum.TryParse(part, true, out Key key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }

    /// <summary>The text for a key press in a key box, or null while only modifiers are down.</summary>
    public static string? Describe(Key key, ModifierKeys mods)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.None) return null;
        // a bare letter would fire while typing: function keys and media keys may stand alone
        bool standalone = key is >= Key.F1 and <= Key.F24 || key is Key.Pause or Key.Scroll or Key.PrintScreen;
        if (mods == ModifierKeys.None && !standalone) return null;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>"Ctrl+Alt+V" → "Ctrl + Alt + V" for display; "" → the "off" text.</summary>
    public static string Display(string keys) =>
        string.IsNullOrEmpty(keys) ? L10n.Get("Hotkey_None") : keys.Replace("+", " + ").Replace("D0", "0").Replace("D1", "1").Replace("D2", "2")
            .Replace("D3", "3").Replace("D4", "4").Replace("D5", "5").Replace("D6", "6").Replace("D7", "7").Replace("D8", "8").Replace("D9", "9");

    public static void Stop()
    {
        if (_window == null) return;
        foreach (int id in Registered.Keys) UnregisterHotKey(_window.Handle, id);
        Registered.Clear();
        _window.Dispose();
        _window = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
