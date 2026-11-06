using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AuraFlyout.Core;

/// <summary>
/// Global low-level keyboard hook. It observes keys and raises events on the UI thread; only the
/// keys <see cref="Takes"/> claims are kept from every other program (the shell included).
/// The hook lives on a thread of its own: the whole system's keyboard input waits for it, and
/// Windows skips (and in the end removes) a hook that answers late, which a UI thread busy
/// opening a flyout would.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc; // keep the delegate alive
    private readonly Dispatcher _ui;
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _hook;
    private readonly HashSet<int> _down = []; // hook thread only

    /// <summary>vk, isKeyDown, isRepeat, taken (the key went to AuraFlyout only)</summary>
    public event Action<int, bool, bool, bool>? KeyEvent;

    /// <summary>
    /// Asked on the hook thread for every key press and release: true keeps the key from everyone
    /// else. The system's keyboard input waits for it, so it must be quick and thread-safe.
    /// </summary>
    public Func<int, bool>? Takes;

    public KeyboardHook()
    {
        _proc = Callback;
        _ui = Dispatcher.CurrentDispatcher;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Keyboard hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(2000);
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = Native.GetCurrentThreadId();
        using (var process = Process.GetCurrentProcess())
        using (var module = process.MainModule)
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            Log.Error($"Keyboard hook could not be installed (error {Marshal.GetLastWin32Error()})");
        ready.Set();
        // the hook is called through this thread's message loop
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
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
            if (down || up)
            {
                var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)info.vkCode;
                bool repeat = down && !_down.Add(vk);
                if (up) _down.Remove(vk);

                bool taken = false;
                try { taken = Takes?.Invoke(vk) == true; }
                catch (Exception ex) { Log.Warn("Key filter failed", ex); }
                if (PerfLog.Enabled && vk is Native.VK_VOLUME_UP or Native.VK_VOLUME_DOWN or Native.VK_VOLUME_MUTE)
                    PerfLog.Write($"hook vk={vk:X2} {(down ? "down" : "up")} repeat={repeat} flags={info.flags:X} taken={taken}");

                // never block the hook chain: hand the work to the UI thread
                var handler = KeyEvent;
                if (handler != null) _ui.BeginInvoke(handler, vk, down, repeat, taken);
                if (taken) return 1;
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public static bool IsToggled(int vk) => (Native.GetKeyState(vk) & 1) != 0;
    public static bool IsPressed(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
        _threadId = 0;
    }
}
