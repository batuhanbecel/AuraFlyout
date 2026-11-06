using System.Windows;
using System.Windows.Controls;

namespace AuraFlyout.Settings.Pages;

public partial class OverlayPage : UserControl
{
    public OverlayPage() => InitializeComponent();

    private void Hotkeys_Click(object sender, RoutedEventArgs e) => App.Instance.OpenSettings("Nav_Hotkeys");
}
