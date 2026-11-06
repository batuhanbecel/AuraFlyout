using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Threading;
using AuraFlyout.Core;

namespace AuraFlyout.Taskbar;

/// <summary>
/// The taskbar's speaker icon, AuraFlyout's way: a click opens the volume mixer above it (Windows
/// opens its quick settings there, which have no mixer) and the mouse wheel over it changes the
/// volume with AuraFlyout's feedback instead of Windows' overlay. The network icon next to it still
/// opens the quick settings.
///
/// The icon is part of the taskbar's own XAML, without a window of its own: its place is read through
/// UI Automation, by the name the shell gives it, which carries the output device's name (so it works
/// in every Windows language). A low-level mouse hook on a thread of its own takes the clicks and
/// wheel turns that land on it; every other mouse input passes untouched.
/// </summary>
public sealed class TraySpeaker : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc; // same signature as a mouse hook procedure
    private readonly Dispatcher _ui;
    private readonly Timer _refresh;
    private uint _threadId;
    private IntPtr _hook;

    // written by the refresh, read by the hook
    private volatile Box? _icon;
    private IntPtr _taskbar;
    private volatile bool _enabled;
    private volatile string _device = "";
    private volatile bool _refind = true;

    private bool _swallowUp;   // hook thread only
    private int _wheel;        // hook thread only: wheel delta not yet turned into steps
    private AutomationElement? _element; // refresh only
    private long _nextFind;              // refresh only
    private int _refreshing;

    private sealed record Box(Native.RECT Rect);

    /// <summary>The icon was clicked (UI thread), with its place on screen.</summary>
    public event Action<Native.RECT>? Clicked;

    /// <summary>The wheel turned over the icon (UI thread): +1 per notch up, -1 per notch down.</summary>
    public event Action<int>? Wheel;

    public TraySpeaker()
    {
        _proc = Callback;
        _ui = Dispatcher.CurrentDispatcher;
        using var ready = new ManualResetEventSlim();
        new Thread(() => Run(ready)) { IsBackground = true, Name = "Taskbar speaker hook", Priority = ThreadPriority.AboveNormal }.Start();
        ready.Wait(2000);
        // the icon moves only when the tray changes (clock width, icons coming and going): a light check every 2 s
        _refresh = new Timer(_ => Refresh(), null, 500, 2000);
    }

    /// <summary>On or off (settings); off lets every click through.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value) _refind = true;
        }
    }

    /// <summary>The default output device's name: the icon's name contains it.</summary>
    public string DeviceName
    {
        set
        {
            if (value == _device) return;
            _device = value;
            _refind = true;
        }
    }

    // ---------- where the icon is ----------

    private void Refresh()
    {
        if (!_enabled || Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
            string device = _device;
            if (taskbar == IntPtr.Zero || device.Length == 0)
            {
                _icon = null;
                return;
            }
            if (taskbar != _taskbar) _refind = true;
            _taskbar = taskbar;

            // the element is looked up again when the device, the taskbar or its name changed;
            // while it cannot be found (another output, a hidden icon), only every 10 s
            if (_refind || (_element == null && Environment.TickCount64 >= _nextFind) ||
                (_element != null && !(_element.Current.Name ?? "").Contains(device, StringComparison.OrdinalIgnoreCase)))
            {
                _refind = false;
                _element = Find(taskbar, device);
                if (_element == null) _nextFind = Environment.TickCount64 + 10_000;
            }
            var icon = _element != null && RectOf(_element) is { } r ? new Box(r) : null;
            if (icon != _icon && PerfLog.Enabled)
                PerfLog.Write(icon is { } b ? $"speaker icon at {b.Rect.Left},{b.Rect.Top} {b.Rect.Width}x{b.Rect.Height}" : $"speaker icon not found for '{device}'");
            _icon = icon;
        }
        catch
        {
            // the element went away (explorer restarted, the tray rebuilt): find it again next time
            _element = null;
            _icon = null;
            _refind = true;
        }
        finally
        {
            _refreshing = 0;
        }
    }

    /// <summary>
    /// The speaker icon among the shell's own child windows of the taskbar. AuraFlyout's taskbar items
    /// are child windows of the taskbar too: a search from the taskbar itself would walk them as well,
    /// through their UI thread, and make WPF build (and keep) automation peers for every element.
    /// </summary>
    private static AutomationElement? Find(IntPtr taskbar, string device)
    {
        uint self = (uint)Environment.ProcessId;
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon");
        for (IntPtr child = Native.FindWindowEx(taskbar, IntPtr.Zero, null, null); child != IntPtr.Zero;
             child = Native.FindWindowEx(taskbar, child, null, null))
        {
            Native.GetWindowThreadProcessId(child, out uint pid);
            if (pid == self) continue;
            foreach (AutomationElement b in AutomationElement.FromHandle(child).FindAll(TreeScope.Subtree, condition))
            {
                if ((b.Current.Name ?? "").Contains(device, StringComparison.OrdinalIgnoreCase))
                    return b;
            }
        }
        return null;
    }

    private static Native.RECT? RectOf(AutomationElement el)
    {
        var r = el.Current.BoundingRectangle;
        if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return null;
        return new Native.RECT { Left = (int)r.Left, Top = (int)r.Top, Right = (int)r.Right, Bottom = (int)r.Bottom };
    }

    // ---------- the hook ----------

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = Native.GetCurrentThreadId();
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            Log.Error($"Mouse hook could not be installed (error {Marshal.GetLastWin32Error()})");
        ready.Set();
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        int msg = (int)wParam;
        if (nCode >= 0 && msg is Native.WM_LBUTTONDOWN or Native.WM_LBUTTONUP or Native.WM_MOUSEWHEEL)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            if (msg == Native.WM_LBUTTONUP)
            {
                // the release of a press we took is ours too, wherever it happens
                if (_swallowUp)
                {
                    _swallowUp = false;
                    if (OnIcon(info.pt, out var rect)) _ui.BeginInvoke(() => Clicked?.Invoke(rect));
                    return 1;
                }
            }
            else if (_enabled && OnIcon(info.pt, out _))
            {
                if (msg == Native.WM_LBUTTONDOWN)
                {
                    _swallowUp = true;
                    return 1;
                }
                // high-resolution wheels report fractions of a notch: whole notches only
                _wheel += (short)(info.mouseData >> 16);
                int notches = _wheel / 120;
                _wheel -= notches * 120;
                if (notches != 0) _ui.BeginInvoke(() => Wheel?.Invoke(notches));
                return 1;
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>On the icon, and the taskbar really is under the pointer (not a full-screen window or an auto-hidden taskbar).</summary>
    private bool OnIcon(Native.POINT pt, out Native.RECT rect)
    {
        rect = default;
        if (_icon is not { } icon) return false;
        var r = icon.Rect;
        if (pt.X < r.Left || pt.X >= r.Right || pt.Y < r.Top || pt.Y >= r.Bottom) return false;
        if (Native.GetAncestorWindow(Native.WindowFromPoint(pt), Native.GA_ROOT) != _taskbar) return false;
        rect = r;
        return true;
    }

    public void Dispose()
    {
        _refresh.Dispose();
        if (_threadId != 0) Native.PostThreadMessage(_threadId, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
        _threadId = 0;
    }
}
