using System.Runtime.InteropServices;

namespace AuraFlyout.Core;

/// <summary>
/// Windows' own popup menu (TrackPopupMenuEx), as every tray icon and the shell use it: Windows 11
/// rounded style, light or dark like the Windows mode. Used for the tray menu and the panels'
/// right-click menus, so no WPF-drawn menu looks different from the system's.
/// </summary>
public static class NativeMenu
{
    private static bool _open;

    /// <summary>
    /// Shows the menu at a screen point (physical pixels; the cursor when 0, 0) and runs the chosen
    /// item. <paramref name="owner"/> must be a window of this process; it is brought to the
    /// foreground so the menu closes on an outside click. <paramref name="above"/> opens it upwards
    /// (from the taskbar).
    /// </summary>
    public static void Show(IntPtr owner, IReadOnlyList<TrayMenuItem> items, int x = 0, int y = 0, bool above = false)
    {
        if (_open || items.Count == 0) return;
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        _open = true;
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.Separator) AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                else AppendMenu(menu, MF_STRING | (it.Enabled ? 0 : MF_GRAYED), (UIntPtr)(uint)(i + 1), it.Text);
            }

            try
            {
                AllowDarkModeForWindow(owner, true);
                SetPreferredAppMode(ThemeService.HighContrast ? 0 : ThemeService.IsTaskbarDark ? 2 /*ForceDark*/ : 3 /*ForceLight*/);
                FlushMenuThemes();
            }
            catch
            {
                // undocumented API missing: default (light) menu
            }

            if (x == 0 && y == 0 && GetCursorPos(out var pt))
            {
                x = pt.X;
                y = pt.Y;
            }
            Native.SetForegroundWindow(owner);
            uint align = Native.GetSystemMetrics(40 /*SM_MENUDROPALIGNMENT*/) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | (above ? TPM_BOTTOMALIGN : TPM_TOPALIGN) | align,
                x, y, owner, IntPtr.Zero);
            PostMessage(owner, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero); // documented: lets the menu close properly
            if (cmd > 0 && cmd <= items.Count) items[cmd - 1].Action?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
            _open = false;
        }
    }

    private const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_SEPARATOR = 0x0800;
    private const uint TPM_LEFTALIGN = 0x0000, TPM_RIGHTALIGN = 0x0008, TPM_TOPALIGN = 0x0000, TPM_BOTTOMALIGN = 0x0020,
        TPM_RIGHTBUTTON = 0x0002, TPM_NONOTIFY = 0x0080, TPM_RETURNCMD = 0x0100;

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Native.POINT point);

    // uxtheme's (undocumented, stable since Windows 10 1903) dark-mode switches for Win32 menus
    [DllImport("uxtheme.dll", EntryPoint = "#133")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowDarkModeForWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool allow);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();
}
