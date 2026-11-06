using System.Windows;
using System.Windows.Controls;

namespace AuraFlyout.Settings.Pages;

public partial class VolumePage : UserControl
{
    public VolumePage() => InitializeComponent();

    private void OpenMixer_Click(object sender, RoutedEventArgs e) => App.Flyouts.Volume.Show(expanded: true);

    private void Hotkeys_Click(object sender, RoutedEventArgs e) => App.Instance.OpenSettings("Nav_Hotkeys");
}
