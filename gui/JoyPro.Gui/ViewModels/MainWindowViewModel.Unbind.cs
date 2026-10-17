using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace JoyPro.Gui.ViewModels;

public sealed partial class MainWindowViewModel
{
    // Frees the physical input of the selected bind in every relation that uses it (engine binds unbind).
    [RelayCommand]
    private async Task UnbindSelectedInputAsync()
    {
        if (SelectedBind == null || !SelectedBind.HasBind || string.IsNullOrWhiteSpace(SelectedBind.Input))
        {
            StatusText = T("Select a bind that has an input first.");
            return;
        }

        UnbindTarget = string.Empty;
        UnbindDevice = SelectedBind.Device;
        UnbindInput = SelectedBind.Input;
        UnbindGame = string.Empty;
        UnbindAll = true;
        await UnbindInputAsync();
    }
}
