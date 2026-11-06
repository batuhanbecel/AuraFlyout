using System.Windows.Media;

namespace AuraFlyout.Controls;

/// <summary>
/// Windows 11's own icon font, the one the shell (taskbar, quick settings, notification centre,
/// media and volume flyouts) draws its icons with; Segoe MDL2 Assets on Windows 10. Flyouts and
/// taskbar items use it so their icons are the system's, glyph for glyph.
/// </summary>
public static class FluentIcons
{
    public static readonly FontFamily Family = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // the filled media glyphs of Windows 11's own media controls (quick settings, media flyout)
    public const string Play = "";
    public const string Pause = "";
    public const string Previous = "";
    public const string Next = "";

    public const string Stop = "";
    public const string Shuffle = "";
    public const string RepeatAll = "";
    public const string RepeatOne = "";

    // the taskbar's speaker: muted, then 0 / low / medium / high
    public const string Mute = "";
    public const string Volume0 = "";
    public const string Volume1 = "";
    public const string Volume2 = "";
    public const string Volume3 = "";
    public const string Speaker = ""; // an app (or the system sounds) without an icon of its own
    public const string Mixer = "";   // volume mixer: slider and speaker

    public const string Lock = "";
    public const string Unlock = "";
    public const string AllApps = "";
    public const string ChevronUp = "";
}
