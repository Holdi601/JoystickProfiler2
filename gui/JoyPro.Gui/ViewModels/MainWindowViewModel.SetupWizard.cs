using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// The Setup wizard (docs/setup-wizard.md): opened from the Start page and the Binds page ("Set up my devices
// automatically..."). It starts with the games ticked for watching on the Start page and runs every step through the
// engine (engine setup ...), so the CLI and the GUI make the same plan.
public sealed partial class MainWindowViewModel
{
    public SetupWizardViewModel? SetupWizard { get; private set; }

    // The main window opens the wizard's window when this is raised.
    public event Action? SetupWizardRequested;

    // A wizard the user closed before Apply comes back as it was (devices, priorities, plan); after Apply a new one starts.
    [RelayCommand]
    private void OpenSetupWizard()
    {
        if (SetupWizard == null || SetupWizard.Applied)
        {
            SetupWizard = CreateSetupWizard();
            OnPropertyChanged(nameof(SetupWizard));
        }
        SetupWizardRequested?.Invoke();
    }

    public SetupWizardViewModel CreateSetupWizard()
    {
        // Installed games; ticked are the watched ones (the Start page's "Watch for changes") that were started once
        // (their control files exist), not every installed game; the user can change it here.
        SetupGameChoice[] games = GameCards.Where(c => c.Installed)
            .Select(c => new SetupGameChoice(c.GameId, c.Name, gameWatchChoices.IsWatched(c.GameId, c.Installed), GameWasStarted(c.GameId)))
            .ToArray();
        return new SetupWizardViewModel(RunSetupWizardEngineAsync, games, SetupWizardAppliedAsync, () =>
        {
            CurrentStage = UiStage.Export;
        });
    }

    private Task<EngineCommandResult?> RunSetupWizardEngineAsync(System.Collections.Generic.IReadOnlyList<string> args, bool includeProfile, bool mutatesProfile, string busyLabel, bool quiet)
    {
        bool withProfile = mutatesProfile || (includeProfile && HasProfile);
        return RunEngineAsync(args, withProfile, mutatesProfile, busyLabel, forceText: false, background: quiet);
    }

    private async Task SetupWizardAppliedAsync()
    {
        await RefreshRelationsAsync();
        await RefreshBindsAsync();
        await RefreshModifiersAsync();
    }
}
