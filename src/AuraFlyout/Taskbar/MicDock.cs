using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AuraFlyout.Controls;
using AuraFlyout.Core;

namespace AuraFlyout.Taskbar;

/// <summary>
/// A muted-microphone sign on the taskbar, next to the tray by default: shown only while the default
/// microphone is muted, in the critical colour; a click unmutes it.
/// </summary>
public sealed class MicDock : DockWindow
{
    private readonly TextBlock _glyph = new()
    {
        FontFamily = FluentIcons.Family,
        Text = "", // MicOff
        VerticalAlignment = VerticalAlignment.Center,
    };

    public MicDock()
    {
        Title = "AuraFlyout Microphone";
        SetDockContent(_glyph);
        ToolTip = L10n.Get("Mic_MutedTip");
        System.Windows.Automation.AutomationProperties.SetName(this, L10n.Get("Mic_MutedTip"));
        App.Mic.Changed += Render;
        SettingsStore.Current.PropertyChanged += OnSettingChanged;
        ThemeService.Changed += Render;
        MouseLeftButtonUp += (_, _) => App.Instance.ToggleMicrophone();
        Loaded += (_, _) => Render();
    }

    public override string Id => "mic";
    public override int OffsetDip => 0;
    public override bool WantsVisible => SettingsStore.Current.MicTaskbarIndicator && App.Mic.HasMicrophone && App.Mic.IsMuted;

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.MicTaskbarIndicator)) Render();
    }

    private void Render()
    {
        _glyph.FontSize = FontSize + 3;
        _glyph.Foreground = HotBrush;
        RequestLayout();
    }

    protected override void OnClosed(EventArgs e)
    {
        App.Mic.Changed -= Render;
        SettingsStore.Current.PropertyChanged -= OnSettingChanged;
        ThemeService.Changed -= Render;
        base.OnClosed(e);
    }
}
