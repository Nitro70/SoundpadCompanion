using System.Windows.Controls;
using System.Windows.Input;
using SoundpadCompanion.Hotkeys;

namespace SoundpadCompanion.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        HotkeyBox.GotKeyboardFocus += (_, _) => (DataContext as SettingsViewModel)?.BeginHotkeyCapture();
        HotkeyBox.LostKeyboardFocus += (_, _) => (DataContext as SettingsViewModel)?.EndHotkeyCapture();
        HotkeyBox.PreviewKeyDown += OnHotkeyKeyDown;
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
            return;
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
            return;   // let Tab move on
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            LeaveHotkeyBox();
            return;
        }
        var hotkey = Hotkey.FromKeyPress(e.Key, e.SystemKey, Keyboard.Modifiers);
        if (vm.TryCaptureHotkey(hotkey))
            LeaveHotkeyBox();
    }

    private void LeaveHotkeyBox() =>
        HotkeyBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
}
