using System.Windows;
using System.Windows.Controls;

namespace AuraFlyout.Settings.Pages;

public partial class TaskbarPage : UserControl
{
    public TaskbarPage() => InitializeComponent();

    private void Layout_Click(object sender, RoutedEventArgs e) => App.Instance.OpenSettings("Nav_Layout");
}
