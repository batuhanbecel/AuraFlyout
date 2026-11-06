using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AuraFlyout.Core;

namespace AuraFlyout.Flyouts;

/// <summary>Fluent toast for device events (connected, disconnected, low, fully charged).</summary>
public partial class NotificationToast : FlyoutWindow
{
    private Action? _click;

    public NotificationToast()
    {
        InitializeComponent();
    }

    // Settings › Accessibility › Visual effects › "Dismiss notifications after"
    protected override int DurationMs => ThemeService.MessageDurationSeconds * 1000;
    protected override int PositionIndex => 2; // bottom right, like Windows notifications
    protected override FrameworkElement Surface => Root;

    public void Show(string title, string text, string glyph, bool warning, Action? click = null)
    {
        if (SettingsStore.Current.DisableInFullscreen && FullscreenDetector.IsFullscreenAppRunning()) return;
        Prepare(title, text, glyph, warning, click);
        ShowFlyout();
    }

    internal void Prepare(string title, string text, string glyph, bool warning, Action? click = null)
    {
        TitleText.Text = title;
        BodyText.Text = text;
        BodyText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        GlyphText.Text = glyph;
        _click = click;
        if (warning)
        {
            IconBack.SetResourceReference(Border.BackgroundProperty, "SystemFillColorCriticalBrush");
            // like an InfoBadge: dark glyph on the light dark-theme red, white on the light-theme red
            GlyphText.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
        }
        else
        {
            IconBack.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            GlyphText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        }
    }

    private void Root_Click(object sender, MouseButtonEventArgs e)
    {
        HideFlyout();
        _click?.Invoke();
    }
}
