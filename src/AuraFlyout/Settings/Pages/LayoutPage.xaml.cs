using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AuraFlyout.Core;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace AuraFlyout.Settings.Pages;

/// <summary>
/// One editor for the order of every taskbar item: a miniature taskbar with left / centre / right
/// zones; ◀ ▶ move an item one step, across zone borders too.
/// </summary>
public partial class LayoutPage : UserControl
{
    private static readonly string[] DefaultLayout = ["status", "media", TaskbarLayout.Separator, TaskbarLayout.Separator, "speed", "logi", "mic"];
    private bool _loading;

    public LayoutPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Build();
            FillMonitors();
        };
        SettingsStore.Current.PropertyChanged += OnSettingChanged;
        Unloaded += (_, _) => SettingsStore.Current.PropertyChanged -= OnSettingChanged;
        L10n.LanguageChanged += Build;
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.TaskbarLayout) or nameof(AppSettings.TaskbarWidgetEnabled) or nameof(AppSettings.VisualizerEnabled)
            or nameof(AppSettings.StatusEnabled) or nameof(AppSettings.LogiEnabled) or nameof(AppSettings.SpeedEnabled))
            Build();
    }

    private static (string Key, SymbolRegular Icon, bool Enabled) Describe(string id)
    {
        var s = SettingsStore.Current;
        return id switch
        {
            "media" => ("Layout_Media", SymbolRegular.MusicNote224, s.TaskbarWidgetEnabled || s.VisualizerEnabled),
            "status" => ("Nav_Status", SymbolRegular.DesktopPulse24, s.StatusEnabled),
            "logi" => ("Nav_Logi", SymbolRegular.Battery524, s.LogiEnabled),
            "mic" => ("Mic_Indicator", SymbolRegular.MicOff24, s.MicTaskbarIndicator),
            _ => ("Nav_Speed", SymbolRegular.TopSpeed24, s.SpeedEnabled),
        };
    }

    private void Build()
    {
        var list = TaskbarLayout.Get();
        WrapPanel[] zones = [LeftZone, CenterZone, RightZone];
        foreach (var z in zones) z.Children.Clear();

        int zone = 0;
        for (int i = 0; i < list.Count; i++)
        {
            string id = list[i];
            if (id == TaskbarLayout.Separator)
            {
                zone++;
                continue;
            }
            zones[Math.Min(zone, 2)].Children.Add(Chip(id, canLeft: i > 0, canRight: i < list.Count - 1));
        }

        foreach (var z in zones)
            if (z.Children.Count == 0)
                z.Children.Add(new TextBlock
                {
                    Text = L10n.Get("Layout_Empty"),
                    FontSize = 12,
                    Margin = new Thickness(4, 10, 0, 0),
                    Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorTertiaryBrush"),
                });
    }

    private FrameworkElement Chip(string id, bool canLeft, bool canRight)
    {
        var (key, icon, enabled) = Describe(id);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Arrow(SymbolRegular.ChevronLeft24, () => Move(id, -1), canLeft, "Layout_MoveLeft"));
        row.Children.Add(new SymbolIcon { Symbol = icon, FontSize = 16, Margin = new Thickness(2, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock { Text = L10n.Get(key), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        row.Children.Add(name);
        if (!enabled)
        {
            var off = new TextBlock { Text = " · " + L10n.Get("Layout_Off"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            off.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            row.Children.Add(off);
        }
        row.Children.Add(Arrow(SymbolRegular.ChevronRight24, () => Move(id, +1), canRight, "Layout_MoveRight"));

        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2, 2, 2, 2),
            Margin = new Thickness(0, 0, 6, 6),
            Child = row,
            Opacity = enabled ? 1 : 0.55,
        };
        chip.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        return chip;
    }

    private Button Arrow(SymbolRegular symbol, Action action, bool enabled, string tipKey)
    {
        var b = new Button
        {
            Style = (Style)FindResource("AuraIconButton"),
            Width = 28,
            Height = 30,
            IsEnabled = enabled,
            ToolTip = L10n.Get(tipKey),
            Content = new SymbolIcon { Symbol = symbol, FontSize = 14 },
        };
        b.Click += (_, _) => action();
        return b;
    }

    private static void Move(string id, int direction) => TaskbarLayout.Set(TaskbarLayout.Move(TaskbarLayout.Get(), id, direction));

    private void Reset_Click(object sender, RoutedEventArgs e) => TaskbarLayout.Set([.. DefaultLayout]);

    private void FillMonitors()
    {
        _loading = true;
        MonitorCombo.Items.Clear();
        var monitors = MonitorHelper.GetAll();
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            string label = $"{i + 1}: {m.Bounds.Width}×{m.Bounds.Height}";
            if (m.IsPrimary) label += $" ({L10n.Get("Common_Primary")})";
            MonitorCombo.Items.Add(new ComboBoxItem { Content = label });
        }
        MonitorCombo.SelectedIndex = Math.Clamp(SettingsStore.Current.TaskbarWidgetMonitor, 0, Math.Max(0, monitors.Count - 1));
        _loading = false;
    }

    private void Monitor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && MonitorCombo.SelectedIndex >= 0) SettingsStore.Current.TaskbarWidgetMonitor = MonitorCombo.SelectedIndex;
    }
}
