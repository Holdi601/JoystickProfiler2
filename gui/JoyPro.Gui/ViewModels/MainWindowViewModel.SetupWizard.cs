using System;
using System.Collections.Generic;
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

    public SetupWizardViewModel CreateSetupWizard(IReadOnlyCollection<string>? chosen = null)
    {
        // Installed games; ticked are the watched ones (the Start page's "Watch for changes") that were started once
        // (their control files exist), not every installed game; the user can change it here. From the Start page's auto
        // setup: the games it names.
        SetupGameChoice[] games = GameCards.Where(c => c.Installed)
            .Select(c => new SetupGameChoice(c.GameId, c.Name, gameWatchChoices.IsWatched(c.GameId, c.Installed), GameWasStarted(c.GameId)))
            .ToArray();
        if (chosen != null && chosen.Count > 0)
        {
            foreach (SetupGameChoice game in games)
                game.IsChosen = chosen.Contains(game.GameId, StringComparer.OrdinalIgnoreCase);
        }
        SetupWizardViewModel? made = null;
        SetupWizardViewModel wizard = new(RunSetupWizardEngineAsync, games, SetupWizardAppliedAsync, () =>
        {
            // The quick path writes to the game right away (pad games: the pad steps instead, they get no export); the
            // full wizard opens the Export page.
            if (made is { QuickMode: true, PadGamesOnly: true })
                QuickSetUpPad(made.ChosenGames);
            else if (made is { QuickMode: true })
                _ = QuickExportAsync(made.ChosenGames);
            else
                CurrentStage = UiStage.Export;
        });
        made = wizard;
        wizard.EasyWordsSource = () => EasyWords;
        // Only an Xbox controller and only pad games: "Close and play" (nothing to set up, no pretend controller).
        wizard.PlayAsIs = () =>
        {
            if (ActiveTour?.Id == JoyPro.Gui.Services.Help.TourCatalog.Pad)
                EndTourCommand.Execute(null);
            StatusText = T("Your controller works as it is. Start the game and have fun!");
        };
        wizard.UseSettings(wizardSettings, WizardSettingsChanged);
        wizard.OpenPreview = OpenLayoutPreviewFor;
        // "Use this layout" on the Cockpits page before the wizard was made.
        if (TakePendingCockpitLayout() is { } cockpit)
            wizard.UseCockpitLayout(cockpit);
        return wizard;
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
        RequestAutoSetupStatus(true);
    }
}
