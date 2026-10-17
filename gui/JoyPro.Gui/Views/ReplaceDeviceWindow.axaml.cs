using Avalonia.Controls;
using Avalonia.Interactivity;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views;

// The Replace device window (MainWindowViewModel.DeviceReplace.cs). Nothing changes before Move binds.
public partial class ReplaceDeviceWindow : Window
{
    public ReplaceDeviceWindow()
    {
        InitializeComponent();
    }

    private void CancelClicked(object? sender, RoutedEventArgs e) => Close();

    private async void ApplyClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && await vm.ApplyReplaceDeviceAsync())
            Close();
    }
}
