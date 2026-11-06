using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AuraFlyout.Core;
using AuraFlyout.Modules;
using AuraFlyout.Modules.LogiBattery;

namespace AuraFlyout.Taskbar;

/// <summary>
/// TaskbarLogiBattery's taskbar readout: one "device glyph + charge" pair per connected device
/// (headset, keyboard, mouse); a low one turns red, a charging one gets a bolt.
/// </summary>
public sealed class BatteryDock : DockWindow
{
    private static readonly FontFamily GlyphFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToolTip _tip = new();

    public BatteryDock()
    {
        Title = "AuraFlyout Logitech";
        SetDockContent(_row);
        ToolTip = _tip;
        _tip.Opened += (_, _) => _tip.Content = Details();

        Batteries.Changed += Render;
        SettingsStore.Current.PropertyChanged += OnSettingChanged;
        ThemeService.Changed += OnTextStyleChanged;
        MouseLeftButtonUp += (_, _) => App.Panels.ToggleBattery(ScreenRect);
        Loaded += (_, _) => Render();
    }

    private static AppSettings S => SettingsStore.Current;

    public override string Id => "logi";
    public override int OffsetDip => S.LogiOffset;
    public override bool WantsVisible => S.LogiEnabled && Batteries.Taskbar.Any();

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != null && (e.PropertyName.StartsWith("Logi") || e.PropertyName is nameof(AppSettings.BluetoothBatteryEnabled) or nameof(AppSettings.GamepadBatteryEnabled))) Render();
    }

    private string _shape = "";

    protected override void OnTextStyleChanged()
    {
        _shape = "";
        Render();
    }

    private void Render()
    {
        // rebuild only when something visible changed (device events can repeat the same values)
        string shape = string.Join(";", Batteries.Taskbar.Select(d => $"{d.Id}{d.Glyph}{d.PercentText}{d.Low}{d.Charging}{d.Full}"))
                       + S.LogiShowPercent + FontSize + FontWeight + ThemeService.IsTaskbarDark;
        if (shape == _shape) return;
        _shape = shape;

        var normal = PrimaryBrush;
        var critical = HotBrush;
        var charging = GoodBrush;
        double slot = SlotWidth(LogiBatteryService.Pct(100), FontSize, FontWeight);

        _row.Children.Clear();
        foreach (var d in Batteries.Taskbar)
        {
            var color = d.Low ? critical : normal;
            var item = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(_row.Children.Count > 0 ? 10 : 0, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var glyph = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            glyph.Children.Add(new TextBlock { Text = d.Glyph, FontFamily = GlyphFont, FontSize = FontSize + 2, FontWeight = FontWeights.Normal, Foreground = color, VerticalAlignment = VerticalAlignment.Center });
            if (d.Charging || d.Full)
            {
                glyph.Children.Add(new TextBlock
                {
                    Text = "", // lightning bolt
                    FontFamily = GlyphFont,
                    FontSize = 8,
                    Foreground = charging,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, -4, -2),
                });
            }
            item.Children.Add(glyph);
            if (S.LogiShowPercent)
                item.Children.Add(new TextBlock
                {
                    Text = d.PercentText,
                    Foreground = color,
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = slot,
                    TextAlignment = TextAlignment.Left,
                });
            _row.Children.Add(item);
        }
        RequestLayout();
    }

    private static string Details()
    {
        var sb = new StringBuilder();
        foreach (var d in Batteries.All.Where(x => !x.Hidden))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(d.Name).Append(": ");
            sb.Append(d.Connected ? d.PercentText + " · " + d.StatusText : d.StatusText);
        }
        return sb.Length > 0 ? sb.ToString() : L10n.Get("Logi_NoDevices");
    }


    protected override void OnClosed(EventArgs e)
    {
        Batteries.Changed -= Render;
        SettingsStore.Current.PropertyChanged -= OnSettingChanged;
        ThemeService.Changed -= OnTextStyleChanged;
        base.OnClosed(e);
    }
}
