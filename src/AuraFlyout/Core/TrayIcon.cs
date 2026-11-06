using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AuraFlyout.Core;

/// <summary>One entry of the tray menu; <see cref="Separator"/> draws a line instead.</summary>
public sealed record TrayMenuItem(string Text, Action? Action, bool Separator = false, bool Enabled = true)
{
    public static TrayMenuItem Line { get; } = new("", null, Separator: true);
}

/// <summary>
/// Notification-area icon built on Shell_NotifyIcon with Windows' own popup menu (the same menu
/// every other tray icon shows: Windows 11 rounded style, follows the light / dark Windows mode).
/// Uses a hidden top-level window so it also receives the "TaskbarCreated" broadcast.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 42;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    private readonly HwndSource _source;
    private readonly int _taskbarCreatedMsg;
    private readonly DispatcherTimer _clickTimer;
    private IntPtr _icon;
    private bool _added;

    public event Action? LeftClick;
    public event Action? DoubleClick;

    /// <summary>Raised when explorer.exe restarted (the taskbar was re-created).</summary>
    public event Action? TaskbarCreated;

    public Func<IReadOnlyList<TrayMenuItem>>? MenuFactory { get; set; }

    public TrayIcon()
    {
        _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
        var p = new HwndSourceParameters("AuraFlyoutTray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = Native.WS_EX_TOOLWINDOW,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        _icon = LoadIcon();

        // a single click waits Windows' double-click time, so a double click (settings) never
        // shows and hides the media flyout first
        _clickTimer = new DispatcherTimer();
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            LeftClick?.Invoke();
        };

        try { AllowDarkModeForWindow(_source.Handle, true); }
        catch { /* older Windows: light menu */ }

        // when running elevated, explorer (medium integrity) must still be able to reach us
        foreach (int msg in new[] { WM_APP_TRAY, _taskbarCreatedMsg, WM_CONTEXTMENU })
            if (msg != 0) ChangeWindowMessageFilterEx(_source.Handle, (uint)msg, 1 /*MSGFLT_ALLOW*/, IntPtr.Zero);
    }

    public IntPtr Handle => _source.Handle;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeFilterStruct);

    public bool Visible
    {
        get => _added;
        set
        {
            if (value) Add();
            else Remove();
        }
    }

    private static IntPtr LoadIcon()
    {
        try
        {
            string path = Path.Combine(AppSettings.Folder, "tray.ico");
            Directory.CreateDirectory(AppSettings.Folder);
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/AuraFlyout;component/Assets/AuraFlyout.ico"));
            if (res != null)
            {
                using var fs = File.Create(path);
                res.Stream.CopyTo(fs);
            }
            int size = Native.GetSystemMetrics(49); // SM_CXSMICON
            if (size <= 0) size = 16;
            return Native.LoadImage(IntPtr.Zero, path, 1 /*IMAGE_ICON*/, size, size, 0x10 /*LR_LOADFROMFILE*/);
        }
        catch (Exception ex)
        {
            Log.Warn("Tray icon image failed", ex);
            return IntPtr.Zero;
        }
    }

    private Native.NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = _source.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = "AuraFlyout",
        szInfo = "",
        szInfoTitle = "",
    };

    private void Add()
    {
        if (_added) return;
        var d = Data(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        if (Native.Shell_NotifyIcon(Native.NIM_ADD, ref d))
        {
            d.uVersion = Native.NOTIFYICON_VERSION_4;
            Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref d);
            _added = true;
        }
    }

    private void Remove()
    {
        if (!_added) return;
        var d = Data(0);
        Native.Shell_NotifyIcon(Native.NIM_DELETE, ref d);
        _added = false;
    }

    public void SetTooltip(string text)
    {
        if (!_added) return;
        var d = Data(Native.NIF_TIP | Native.NIF_SHOWTIP);
        d.szTip = text.Length > 127 ? text[..127] : text;
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref d);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAY)
        {
            int ev = (int)(lParam.ToInt64() & 0xFFFF);
            switch (ev)
            {
                case NIN_SELECT:
                    _clickTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(Native.GetDoubleClickTime(), 100, 900));
                    _clickTimer.Stop();
                    _clickTimer.Start();
                    break;
                case NIN_KEYSELECT:
                    LeftClick?.Invoke(); // keyboard (Enter / Space on the focused icon): no double click to wait for
                    break;
                case WM_LBUTTONDBLCLK:
                    _clickTimer.Stop();
                    DoubleClick?.Invoke();
                    break;
                case WM_CONTEXTMENU:
                    // NOTIFYICON_VERSION_4: wParam carries the anchor point (screen pixels)
                    long wp = wParam.ToInt64();
                    ShowMenu((short)(wp & 0xFFFF), (short)((wp >> 16) & 0xFFFF));
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            bool wasAdded = _added;
            _added = false;
            if (wasAdded) Add();
            TaskbarCreated?.Invoke();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu(int x, int y)
    {
        if (MenuFactory != null) NativeMenu.Show(_source.Handle, MenuFactory(), x, y, above: true);
    }

    // uxtheme's dark-mode switch for this window's menus
    [DllImport("uxtheme.dll", EntryPoint = "#133")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowDarkModeForWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool allow);

    public void Dispose()
    {
        _clickTimer.Stop();
        Remove();
        _source.RemoveHook(WndProc);
        _source.Dispose();
        if (_icon != IntPtr.Zero) Native.DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }
}
