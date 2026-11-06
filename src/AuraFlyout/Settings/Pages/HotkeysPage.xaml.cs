using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AuraFlyout.Controls;
using AuraFlyout.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace AuraFlyout.Settings.Pages;

/// <summary>Every global shortcut: click the box and press the new keys; Backspace clears it.</summary>
public partial class HotkeysPage : UserControl
{
    public HotkeysPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Build();
            HotkeyManager.Changed += Later;
        };
        Unloaded += (_, _) => HotkeyManager.Changed -= Later;
    }

    private bool _pending;

    /// <summary>Rebuilds after the current input is handled (a box losing focus re-registers the keys too).</summary>
    private void Later()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _pending = false;
            Build();
        });
    }

    private void Build()
    {
        List.Children.Clear();
        foreach (var a in HotkeyManager.All)
        {
            string keys = HotkeyManager.KeysOf(a.Id);
            var box = new Wpf.Ui.Controls.TextBox
            {
                Text = HotkeyManager.Display(keys),
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                MinWidth = 200,
                Cursor = Cursors.Hand,
                ToolTip = L10n.Get("Hotkey_BoxTip"),
                Tag = a.Id,
            };
            System.Windows.Automation.AutomationProperties.SetName(box, L10n.Get(a.TitleKey));
            box.GotKeyboardFocus += (_, _) =>
            {
                HotkeyManager.Suspend(true); // so even our own combinations can be typed in
                box.Text = L10n.Get("Hotkey_Press");
            };
            box.LostKeyboardFocus += (_, _) =>
            {
                HotkeyManager.Suspend(false);
                box.Text = HotkeyManager.Display(HotkeyManager.KeysOf(a.Id));
            };
            box.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Escape)
                {
                    Keyboard.ClearFocus();
                    return;
                }
                if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
                {
                    HotkeyManager.SetKeys(a.Id, "");
                    Keyboard.ClearFocus();
                    return;
                }
                string? combo = HotkeyManager.Describe(key, Keyboard.Modifiers);
                if (combo == null) return; // modifiers only so far
                HotkeyManager.SetKeys(a.Id, combo);
                Keyboard.ClearFocus();
            };

            var reset = new Wpf.Ui.Controls.Button
            {
                Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowReset24 },
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = L10n.Get("Hotkey_Reset"),
                Visibility = keys == a.DefaultKeys ? Visibility.Collapsed : Visibility.Visible,
            };
            System.Windows.Automation.AutomationProperties.SetName(reset, L10n.Get("Hotkey_Reset"));
            reset.Click += (_, _) =>
            {
                SettingsStore.Current.Hotkeys.Remove(a.Id);
                SettingsStore.Current.Touch(nameof(AppSettings.Hotkeys));
            };

            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(box);
            right.Children.Add(reset);

            string desc = L10n.Get(a.DescKey);
            if (HotkeyManager.IsTaken(a.Id)) desc = L10n.Get("Hotkey_Taken") + (desc.Length > 0 ? " · " + desc : "");
            var card = new SettingCard
            {
                Icon = SymbolRegular.Keyboard24,
                Title = L10n.Get(a.TitleKey),
                Description = desc,
                Content = right,
            };
            List.Children.Add(card);
        }
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.Hotkeys.Clear();
        SettingsStore.Current.Touch(nameof(AppSettings.Hotkeys));
    }
}
