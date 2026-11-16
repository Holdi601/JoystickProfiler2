using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using JoyPro.Gui.Services.Help;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views;

// F1 / Esc for the help mode (docs/ui/help-and-tours.md); the layer itself is Views/Controls/HelpLayer.axaml. In help
// mode Enter and Space show the help of the focused control instead of pressing it.
public partial class MainWindow
{
    private void AttachHelpKeys() => AddHandler(KeyDownEvent, HelpKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

    private void AttachHelpLayer(MainWindowViewModel vm) => HelpLayer.Attach(RootLayoutGrid, vm);

    private void HelpKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        if (e.Key == Key.F1 && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            if (!vm.IsWelcomeOpen)
                vm.ToggleHelpModeCommand.Execute(null);
            return;
        }
        if (!vm.IsHelpModeOn)
            return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.EndHelpModeCommand.Execute(null);
            return;
        }
        if (e.Key is Key.Enter or Key.Space && FocusManager?.GetFocusedElement() is Control focused && HelpResolver.FindTarget(focused) is Control target)
        {
            e.Handled = true;
            HelpLayer.ShowHelpFor(target, Controls.HelpLayer.PageOf(target, vm.CurrentHelpPage));
        }
    }
}
