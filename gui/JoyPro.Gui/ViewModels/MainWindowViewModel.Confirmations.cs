using CommunityToolkit.Mvvm.Input;

namespace JoyPro.Gui.ViewModels;

// Questions the user can switch off with "Do not ask again" (ConfirmAsync with a key) and switch on again in
// Settings > Confirmations (Views/Controls/ConfirmationSettingsPanel.axaml).
public sealed partial class MainWindowViewModel
{
    // The x of an action in the open relation (Relations page): "Remove this action?".
    public const string ConfirmRemoveActionKey = "relations.remove-action";

    public bool AskBeforeRemovingAction
    {
        get => ListPrefs.ShouldAsk(ConfirmRemoveActionKey);
        set => SetConfirmationAsked(ConfirmRemoveActionKey, value);
    }

    public bool HasSwitchedOffConfirmations => !AskBeforeRemovingAction;

    internal void SetConfirmationAsked(string key, bool ask)
    {
        if (ListPrefs.ShouldAsk(key) == ask)
            return;
        ListPrefs.Confirmations[key] = ask;
        SaveListPreferences();
        OnPropertyChanged(nameof(AskBeforeRemovingAction));
        OnPropertyChanged(nameof(HasSwitchedOffConfirmations));
    }

    // Every question asked again.
    [RelayCommand]
    private void AskAllConfirmationsAgain()
    {
        SetConfirmationAsked(ConfirmRemoveActionKey, true);
        StatusText = T("JoyPro asks again before removing an action from a relation.");
    }
}
