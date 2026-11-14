using System.Windows;

namespace SoundpadCompanion.Settings;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Icon = AppIcon.ImageSource;
        Closed += (_, _) => _vm.Dispose();
    }
}
