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

    // An import while the profile has changes for the game that were never exported (MainWindowViewModel.ImportGuard.cs).
    public const string ConfirmImportUnexportedKey = "games.import-unexported-changes";

    public bool AskBeforeImportDropsChanges
    {
        get => ListPrefs.ShouldAsk(ConfirmImportUnexportedKey);
        set => SetConfirmationAsked(ConfirmImportUnexportedKey, value);
    }

    public bool HasSwitchedOffConfirmations => !AskBeforeRemovingAction || !AskBeforeImportDropsChanges;

    internal void SetConfirmationAsked(string key, bool ask)
    {
        if (ListPrefs.ShouldAsk(key) == ask)
            return;
        ListPrefs.Confirmations[key] = ask;
        SaveListPreferences();
        OnPropertyChanged(nameof(AskBeforeRemovingAction));
        OnPropertyChanged(nameof(AskBeforeImportDropsChanges));
        OnPropertyChanged(nameof(HasSwitchedOffConfirmations));
    }

    // Every question asked again.
    [RelayCommand]
    private void AskAllConfirmationsAgain()
    {
        SetConfirmationAsked(ConfirmRemoveActionKey, true);
        SetConfirmationAsked(ConfirmImportUnexportedKey, true);
        StatusText = T("JoyPro asks every question again.");
    }
}
