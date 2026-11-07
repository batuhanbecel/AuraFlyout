using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using AuraFlyout.Core;

namespace AuraFlyout.Taskbar;

/// <summary>
/// Lines up every <see cref="DockWindow"/> on the taskbar: left items start after the Windows
/// Widgets button, right items stack leftwards from the system tray, centre items are centred
/// together. Replaces the "line up next to each other" logic of the standalone apps.
/// </summary>
public static class TaskbarDock
{
    private static readonly List<DockWindow> Windows = [];
    private static DispatcherTimer? _timer;
    private static bool _pending;
    private static IntPtr _taskbar;

    // where the shell's own content sits (physical pixels), read through UI Automation:
    // the Start / Search / app icons block and the Widgets button
    private static Native.RECT? _iconArea, _widgetsButton;
    private static long _shellChecked, _shellFound;
    private static bool _shellQueryRunning;
    private static AutomationElement? _iconsElement, _widgetsElement; // only touched by the query task
    private static IntPtr _shellFor;

    public static IntPtr Taskbar => _taskbar;

    /// <summary>
    /// An item's window is gone: it was a child of a taskbar that explorer.exe destroyed (restart or
    /// crash). The app creates it again on the new taskbar.
    /// </summary>
    public static event Action<DockWindow>? DockLost;

    internal static void Register(DockWindow w)
    {
        if (!Windows.Contains(w)) Windows.Add(w);
        if (_timer == null)
        {
            // a safety net only: content changes lay out at once through Invalidate()
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000) };
            _timer.Tick += (_, _) => Layout();
            _timer.Start();
            SettingsStore.Current.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppSettings.TaskbarWidgetMonitor)) Reattach();
                else Invalidate();
            };
        }
        Invalidate();
    }

    internal static void Unregister(DockWindow w)
    {
        Windows.Remove(w);
        Invalidate();
    }

    /// <summary>Every item draws itself anew (see <see cref="DockWindow.Repaint"/>).</summary>
    public static void RepaintAll()
    {
        foreach (var w in Windows.ToList()) w.Repaint();
    }

    /// <summary>Coalesced re-layout on the next dispatcher idle.</summary>
    public static void Invalidate()
    {
        if (_pending) return;
        _pending = true;
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _pending = false;
            Layout();
        });
    }

    /// <summary>Finds the taskbar again (explorer.exe restarted or another monitor was chosen).</summary>
    public static void Reattach()
    {
        _taskbar = FindTaskbar();
        _widgetsButton = null;
        _iconArea = null;
        _shellChecked = 0;
        foreach (var w in Windows) w.Reparent(_taskbar);
        Invalidate();
    }

    private static IntPtr FindTaskbar()
    {
        IntPtr main = Native.FindWindow("Shell_TrayWnd", null);
        int index = SettingsStore.Current.TaskbarWidgetMonitor;
        if (index <= 0) return main;

        var monitors = MonitorHelper.GetAll();
        if (index >= monitors.Count) return main;
        var target = monitors[index];

        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            if (Native.GetClassName(h) is "Shell_SecondaryTrayWnd" or "Shell_TrayWnd" &&
                MonitorHelper.FromWindow(h).DeviceName == target.DeviceName)
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found != IntPtr.Zero ? found : main;
    }

    private static void Layout()
    {
        // windows destroyed together with the old taskbar: hand them back to be re-created
        foreach (var w in Windows.ToList())
        {
            if (w.Handle == IntPtr.Zero || Native.IsWindow(w.Handle)) continue;
            Windows.Remove(w);
            Log.Info($"Taskbar item '{w.Id}' was destroyed with the taskbar, re-creating it");
            DockLost?.Invoke(w);
        }
        if (Windows.Count == 0) return;
        if (_taskbar == IntPtr.Zero || !Native.IsWindow(_taskbar))
        {
            _taskbar = FindTaskbar();
            if (_taskbar == IntPtr.Zero) return;
        }

        if (!Native.GetWindowRect(_taskbar, out var tb) || tb.Width <= 0 || tb.Height > tb.Width)
        {
            // gone, or a vertical taskbar (not supported)
            foreach (var w in Windows) w.Unplace();
            return;
        }

        double scale = Math.Max(1, Native.GetDpiForWindow(_taskbar)) / 96.0;
        int gap = (int)Math.Round(Math.Clamp(SettingsStore.Current.TaskbarGap, 0, 40) * scale);
        int h = tb.Height;

        var layout = TaskbarLayout.Get();
        var visible = new List<(DockWindow Window, int Width, int Zone, int Index)>();
        foreach (var w in Windows)
        {
            if (!w.IsLoaded || !w.WantsVisible)
            {
                w.Unplace();
                continue;
            }
            int width = (int)Math.Ceiling(w.MeasureWidth() * scale) + 2;
            if (width <= 2)
            {
                w.Unplace();
                continue;
            }
            var (zone, index) = TaskbarLayout.Locate(layout, w.Id);
            visible.Add((w, width, zone, index));
        }

        // The shell's own content never gets covered: the Start / app icons block (left or centre,
        // per Settings › Personalisation › Taskbar › alignment) and the Widgets button are obstacles
        // each zone moves around. Zones placed first become obstacles for the next ones.
        QueryShell();
        int pad = (int)(4 * scale);
        var obstacles = new List<(int Left, int Right)>();
        if (_iconArea is { } ia && ia.Width > 0) obstacles.Add((ia.Left, ia.Right));
        if (_widgetsButton is { } wb && wb.Width > 0) obstacles.Add((wb.Left, wb.Right));

        int Total(List<(DockWindow Window, int Width, int Zone, int Index)> zone) =>
            zone.Sum(v => v.Width + (int)(v.Window.OffsetDip * scale)) + gap * Math.Max(0, zone.Count - 1);

        // ---- left zone: left to right from the left edge, after anything of the shell's there ----
        var leftZone = visible.Where(v => v.Zone == 0).OrderBy(v => v.Index).ToList();
        if (leftZone.Count > 0)
        {
            int total = Total(leftZone);
            int x = PushRight(tb.Left + pad, total, obstacles, pad);
            obstacles.Add((x, x + total));
            foreach (var v in leftZone)
            {
                x += (int)(v.Window.OffsetDip * scale);
                Put(v.Window, x, tb, v.Width, h);
                x += v.Width + gap;
            }
        }

        // ---- right zone: the list order is kept, so the last item sits next to the tray ----
        var rightZone = visible.Where(v => v.Zone == 2).OrderBy(v => v.Index).ToList();
        if (rightZone.Count > 0)
        {
            int right = tb.Right - (int)(150 * scale);
            IntPtr tray = Native.FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out var tr) && tr.Width > 0) right = tr.Left - (int)(2 * scale);
            int total = Total(rightZone);
            int rx = PushLeft(right - total, total, obstacles, pad);
            obstacles.Add((rx, rx + total));
            foreach (var v in rightZone)
            {
                rx += (int)(v.Window.OffsetDip * scale);
                Put(v.Window, rx, tb, v.Width, h);
                rx += v.Width + gap;
            }
        }

        // ---- centre zone: one block in the middle, or right after centred app icons ----
        var centre = visible.Where(v => v.Zone == 1).OrderBy(v => v.Index).ToList();
        if (centre.Count > 0)
        {
            int total = Total(centre);
            int cx = PushRight(tb.Left + (tb.Width - total) / 2, total, obstacles, gap);
            foreach (var v in centre)
            {
                cx += (int)(v.Window.OffsetDip * scale);
                Put(v.Window, cx, tb, v.Width, h);
                cx += v.Width + gap;
            }
        }
    }

    /// <summary>Moves the span [x, x+width) right past every obstacle it overlaps.</summary>
    private static int PushRight(int x, int width, List<(int Left, int Right)> obstacles, int pad)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            bool moved = false;
            foreach (var o in obstacles.OrderBy(o => o.Left))
            {
                if (o.Left < x + width && o.Right > x)
                {
                    x = o.Right + pad;
                    moved = true;
                }
            }
            if (!moved) break;
        }
        return x;
    }

    /// <summary>Moves the span [x, x+width) left past every obstacle it overlaps.</summary>
    private static int PushLeft(int x, int width, List<(int Left, int Right)> obstacles, int pad)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            bool moved = false;
            foreach (var o in obstacles.OrderByDescending(o => o.Right))
            {
                if (o.Left < x + width && o.Right > x)
                {
                    x = o.Left - pad - width;
                    moved = true;
                }
            }
            if (!moved) break;
        }
        return x;
    }

    private static void Put(DockWindow w, int screenX, Native.RECT tb, int width, int height)
    {
        screenX = Math.Clamp(screenX, tb.Left, Math.Max(tb.Left, tb.Right - width));
        var p = new Native.POINT { X = screenX, Y = tb.Top };
        Native.ScreenToClient(_taskbar, ref p);
        var screen = new Native.RECT { Left = screenX, Top = tb.Top, Right = screenX + width, Bottom = tb.Top + height };
        w.Place(_taskbar, screen, p.X, p.Y);
    }

    /// <summary>
    /// Reads where the shell's icon block ("TaskbarFrameRepeater": Start, Search, Task View and the
    /// app buttons) and the Widgets button are, off the UI thread. The elements are looked up every
    /// 8 s (a tree search); their rectangles are re-read every second, so opening or closing an app
    /// moves our items along with the icons.
    /// </summary>
    private static void QueryShell()
    {
        long now = Environment.TickCount64;
        if (_shellQueryRunning || now - _shellChecked < 2000) return;
        _shellQueryRunning = true;
        _shellChecked = now;
        IntPtr taskbar = _taskbar;

        Task.Run(() =>
        {
            Native.RECT? icons = null, widgets = null;
            try
            {
                if (_shellFor != taskbar || now - _shellFound > 8000)
                {
                    _shellFor = taskbar;
                    _shellFound = now;
                    _iconsElement = FindInShell(taskbar, "TaskbarFrameRepeater");
                    _widgetsElement = FindInShell(taskbar, "WidgetsButton");
                }
                icons = RectOf(_iconsElement);
                widgets = RectOf(_widgetsElement);
            }
            catch
            {
                // element gone (explorer restarted) or UI Automation unavailable: look up again next time
                _shellFound = 0;
            }
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                bool changed = !Nullable.Equals(icons, _iconArea) || !Nullable.Equals(widgets, _widgetsButton);
                _iconArea = icons;
                _widgetsButton = widgets;
                _shellQueryRunning = false;
                if (changed) Invalidate();
            });
        });
    }

    /// <summary>
    /// The taskbar's child windows that belong to the shell. AuraFlyout's items are children of the
    /// taskbar too; a UI Automation search from the taskbar itself would walk them as well, through
    /// our UI thread, and make WPF build (and keep) automation peers for all of their elements.
    /// </summary>
    internal static List<IntPtr> ShellChildren(IntPtr taskbar)
    {
        var list = new List<IntPtr>();
        uint self = (uint)Environment.ProcessId;
        for (IntPtr child = Native.FindWindowEx(taskbar, IntPtr.Zero, null, null); child != IntPtr.Zero;
             child = Native.FindWindowEx(taskbar, child, null, null))
        {
            Native.GetWindowThreadProcessId(child, out uint pid);
            if (pid != self) list.Add(child);
        }
        return list;
    }

    private static AutomationElement? FindInShell(IntPtr taskbar, string automationId)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        foreach (IntPtr child in ShellChildren(taskbar))
        {
            if (AutomationElement.FromHandle(child).FindFirst(TreeScope.Subtree, condition) is { } found)
                return found;
        }
        return null;
    }

    private static Native.RECT? RectOf(AutomationElement? el)
    {
        if (el == null) return null;
        var r = el.Current.BoundingRectangle;
        if (r.IsEmpty || r.Width <= 0) return null;
        return new Native.RECT { Left = (int)r.Left, Top = (int)r.Top, Right = (int)r.Right, Bottom = (int)r.Bottom };
    }
}
