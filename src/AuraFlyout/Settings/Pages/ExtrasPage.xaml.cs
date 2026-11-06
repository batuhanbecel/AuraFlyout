using System.Windows;
using System.Windows.Controls;
using AuraFlyout.Core;

namespace AuraFlyout.Settings.Pages;

/// <summary>Brightness, microphone mute and keyboard language flyouts.</summary>
public partial class ExtrasPage : UserControl
{
    public ExtrasPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ShowMic();
    }

    private void ShowMic() =>
        MicName.Text = App.Mic is { HasMicrophone: true } m ? m.DeviceName : L10n.Get("Mic_None");

    private void Brightness_Click(object sender, RoutedEventArgs e) => App.Flyouts.Brightness.ShowNow();

    private void Mic_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.ToggleMicrophone();
        ShowMic();
    }
}
