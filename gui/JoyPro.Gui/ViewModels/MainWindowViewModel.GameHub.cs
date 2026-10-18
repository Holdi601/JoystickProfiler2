using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// Start page, "Your games": one card per game the user has (engine games installs), with its cover and what JoyPro
// does with it, and the import wizard: 1. pick the game, 2. say how (add to the profile or start over, keyboard,
// DCS aircraft), 3. see the result. The same engine commands as the expert form below it (dcs/il2/starcitizen import,
// engine <game> import), with safe defaults: the wizard adds to the profile unless the user asks to start over.
// The cards also update a game's command list (DB refresh) and undo its last export (engine backups restore latest).
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<GameCardItem> GameCards { get; } = [];

    public bool HasGameCards => GameCards.Count > 0;

    // 1 = pick a game, 2 = how to import, 3 = done (the result over the game cards).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGameCardList))]
    [NotifyPropertyChangedFor(nameof(IsImportWizardPickStep))]
    [NotifyPropertyChangedFor(nameof(IsImportWizardOptionsStep))]
    [NotifyPropertyChangedFor(nameof(IsImportWizardDoneStep))]
    private int importWizardStep = 1;

    public bool IsImportWizardPickStep => ImportWizardStep == 1;

    public bool IsImportWizardOptionsStep => ImportWizardStep == 2;

    public bool IsImportWizardDoneStep => ImportWizardStep == 3;

    // The game cards (and with them update, export, undo and watch per game) are on the Start page except while the
    // user chooses how to import: an import's result shows over them, so the page never loses them after an import.
    public bool ShowGameCardList => ImportWizardStep != 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportWizardTitle))]
    [NotifyPropertyChangedFor(nameof(ImportWizardIsDcs))]
    [NotifyPropertyChangedFor(nameof(ImportWizardOffersKeyboard))]
    private GameCardItem? importWizardGame;

    public string ImportWizardTitle => ImportWizardGame == null ? T("Import your binds from a game") : T("Import from {0}", ImportWizardGame.Name);

    public bool ImportWizardIsDcs => ImportWizardGame?.Kind == "dcs";

    // IL-2 has no keyboard choice (its import reads the joystick binds).
    public bool ImportWizardOffersKeyboard => ImportWizardGame != null && ImportWizardGame.Kind != "il2";

    // true: add to the open profile (engine --merge); false: the profile's relations are replaced by the game's.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportWizardStartOver))]
    private bool importWizardAddToProfile = true;

    public bool ImportWizardStartOver
    {
        get => !ImportWizardAddToProfile;
        set => ImportWizardAddToProfile = !value;
    }

    [ObservableProperty]
    private bool importWizardKeyboard;

    [ObservableProperty]
    private string importWizardResult = string.Empty;

    [ObservableProperty]
    private bool importWizardFailed;

    private bool registryGamesRequestedForCards;

    // The cards follow Settings > Games (installed, hidden) and the game integrations of the engine.
    private void RebuildGameCards()
    {
        Dictionary<string, GameCardItem> old = GameCards.ToDictionary(c => c.GameId, StringComparer.OrdinalIgnoreCase);
        List<GameCardItem> cards = [];
        foreach (GameInstallItem game in OwnedGameInstalls)
        {
            (string kind, string command, bool canRefresh) = GameCardKind(game.GameId);
            if (kind.Length == 0)
                continue;
            GameCardItem card = new(game.GameId, game.Name, kind, command, game.Installed, canRefresh);
            if (old.TryGetValue(game.GameId, out GameCardItem? previous))
            {
                card.Result = previous.Result;
                card.ResultFailed = previous.ResultFailed;
                card.Cover = previous.Cover;
                card.Logo = previous.Logo;
            }
            card.Cover ??= game.Cover;
            card.NeedsDbUpdate = DbUpdateItems.Any(u => string.Equals(u.Game, game.GameId, StringComparison.OrdinalIgnoreCase));
            // Installed: ticked unless the user unticked it; not found: off (ticks itself once installed).
            card.Watch = gameWatchChoices.IsWatched(game.GameId, game.Installed);
            card.WatchChanged = OnGameCardWatchChanged;
            cards.Add(card);
        }
        // Installed games first, then by name.
        cards = cards.OrderBy(c => c.Installed ? 0 : 1).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        GameCards.Clear();
        foreach (GameCardItem card in cards)
            GameCards.Add(card);
        OnPropertyChanged(nameof(HasGameCards));
        OnPropertyChanged(nameof(WatchAllGames));
        OnPropertyChanged(nameof(HasWatchableGameCards));
        if (ImportWizardGame != null)
            ImportWizardGame = GameCards.FirstOrDefault(c => c.GameId == ImportWizardGame.GameId) ?? ImportWizardGame;
        _ = LoadGameCardCoversAsync(cards);

        // The games of the registry (WARDOGS, MSFS, ...) need the engine's integration list once.
        if (!registryGamesRequestedForCards && allRegistryGames.Count == 0 && OwnedGameInstalls.Any(g => GameCardKind(g.GameId).Kind.Length == 0))
        {
            registryGamesRequestedForCards = true;
            _ = RefreshRegistryGamesAsync();
        }
    }

    private (string Kind, string Command, bool CanRefresh) GameCardKind(string gameId)
    {
        switch (gameId)
        {
            case "DCS":
                return ("dcs", "dcs", true);
            case "IL2Game":
                return ("il2", "il2", true);
            case "StarCitizen":
                return ("starcitizen", "starcitizen", true);
        }
        RegistryGameItem? registry = allRegistryGames.FirstOrDefault(g => string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));
        return registry == null ? (string.Empty, string.Empty, false) : ("registry", registry.CommandName, registry.SupportsCatalogRefresh);
    }

    private async Task LoadGameCardCoversAsync(List<GameCardItem> cards)
    {
        foreach (GameCardItem card in cards.Where(c => c.Cover == null))
        {
            card.Cover = await LoadGameCoverAsync(card.GameId, 120);
            if (card.Cover == null)
                card.Logo ??= await LoadGameLogoAsync(card.GameId);
        }
    }

    [RelayCommand]
    private async Task RefreshGameCardsAsync()
    {
        await RefreshGameInstallsAsync();
        await RefreshRegistryGamesAsync();
    }

    // ---- Import wizard ----

    [RelayCommand]
    private void StartImportWizard(GameCardItem? game)
    {
        if (game == null)
            return;
        if (!game.Installed)
        {
            StatusText = T("{0} is not found on this PC. Settings > Games: choose its folder.", game.Name);
            return;
        }
        ImportWizardGame = game;
        ImportWizardResult = string.Empty;
        ImportWizardFailed = false;
        ImportWizardKeyboard = game.Kind switch
        {
            "dcs" => ImportKeyboardCommands,
            "starcitizen" => ImportStarCitizenIncludeKeyboard,
            _ => RegistryGameIncludeKeyboard
        };
        ImportWizardStep = 2;
    }

    [RelayCommand]
    private void ImportWizardBack()
    {
        ImportWizardStep = 1;
        ImportWizardResult = string.Empty;
    }

    // "Look first": what the game has, without changing the profile (engine ... scan), in the output panel.
    [RelayCommand]
    private async Task PreviewImportWizardAsync()
    {
        GameCardItem? game = ImportWizardGame;
        if (game == null)
            return;
        ApplyImportWizardChoices(game);
        switch (game.Kind)
        {
            case "dcs":
                await ScanDcsAsync();
                break;
            case "il2":
                await ScanIl2Async();
                break;
            case "starcitizen":
                await ScanStarCitizenAsync();
                break;
            default:
                if (!await SelectRegistryGameAsync(game))
                    return;
                await ScanRegistryGameAsync();
                break;
        }
        EngineOutputExpanded = true;
        StatusText = T("{0}: what the game has is in the output panel below. Nothing was changed.", game.Name);
    }

    [RelayCommand]
    private async Task RunImportWizardAsync()
    {
        GameCardItem? game = ImportWizardGame;
        if (game == null)
            return;
        if (!ImportWizardAddToProfile && HasProfile && Relations.Count > 0 &&
            !await ConfirmAsync(T("Start over from {0}?", game.Name),
                T("The relations of {0} are replaced by what {1} has. A copy of your profile is kept next to it first.", ProfileDisplayName, game.Name),
                T("Start over"), isDanger: true))
            return;
        if (!ImportWizardAddToProfile)
            BackupProfileBeforeChange("before-import");

        ApplyImportWizardChoices(game);
        UiStage stageBefore = CurrentStage;
        LastExitCode = "n/a";
        string? crash = null;
        try
        {
            switch (game.Kind)
            {
                case "dcs":
                    await ImportDcsAsync();
                    break;
                case "il2":
                    await ImportIl2Async();
                    break;
                case "starcitizen":
                    await ImportStarCitizenAsync();
                    break;
                default:
                    if (await SelectRegistryGameAsync(game))
                        await ImportRegistryGameAsync();
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever went wrong, the wizard ends on its result and the Start page keeps its games.
            JoyPro.Logging.JoyProLog.Warn("ImportWizard", "Import of " + game.GameId + " failed", ex);
            crash = ex.Message;
            LastExitCode = "n/a";
        }

        bool ok = LastExitCode == "0" && crash == null;
        ImportWizardFailed = !ok;
        ImportWizardResult = ok
            ? Plural("Done: your profile {1} now has {0} relation ({2} bound).", "Done: your profile {1} now has {0} relations ({2} bound).", Relations.Count, Relations.Count, ProfileDisplayName, Binds.Count(b => !string.IsNullOrWhiteSpace(b.Input)))
            : T("The import did not work: {0}", crash ?? (lastEngineFailureReason.Length > 0 && LastExitCode != "n/a" ? Translate(lastEngineFailureReason) : StatusText));
        game.ResultFailed = !ok;
        game.Result = ok ? T("Imported {0}", DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture)) : T("Import failed");
        ImportWizardStep = 3;
        // The import opens the Relations page; the wizard stays on its result.
        if (CurrentStage != stageBefore)
            CurrentStage = stageBefore;
    }

    // Leaving the Start page closes an import's result (the card keeps "Imported 14:02"): coming back shows the
    // games as always.
    private void OnStartPageLeft()
    {
        if (ImportWizardStep == 3)
            RestartImportWizard();
    }

    [RelayCommand]
    private void FinishImportWizard()
    {
        ImportWizardStep = 1;
        ImportWizardGame = null;
        ImportWizardResult = string.Empty;
        CurrentStage = UiStage.Relations;
    }

    [RelayCommand]
    private void RestartImportWizard()
    {
        ImportWizardStep = 1;
        ImportWizardGame = null;
        ImportWizardResult = string.Empty;
    }

    // The wizard's plain choices onto the options of the expert form (which the import commands read).
    private void ApplyImportWizardChoices(GameCardItem game)
    {
        ImportMerge = ImportWizardAddToProfile;
        switch (game.Kind)
        {
            case "dcs":
                ImportKeyboardCommands = ImportWizardKeyboard;
                break;
            case "starcitizen":
                ImportStarCitizenIncludeKeyboard = ImportWizardKeyboard;
                break;
            case "registry":
                RegistryGameIncludeKeyboard = ImportWizardKeyboard;
                break;
        }
    }

    private async Task<bool> SelectRegistryGameAsync(GameCardItem game)
    {
        if (RegistryGames.Count == 0)
            await RefreshRegistryGamesAsync();
        RegistryGameItem? registry = RegistryGames.FirstOrDefault(g => string.Equals(g.GameId, game.GameId, StringComparison.OrdinalIgnoreCase));
        if (registry == null)
        {
            StatusText = T("JoyPro cannot read {0} yet.", game.Name);
            return false;
        }
        if (SelectedRegistryGame != registry)
            SelectedRegistryGame = registry;
        return true;
    }

    // ---- Per-game actions on the cards ----

    // Reads the game's new commands into JoyPro's command list (engine dcs refresh id / db refresh-il2 /
    // db refresh-starcitizen / <game> refresh-db), then checks the games for updates again.
    [RelayCommand]
    private async Task UpdateGameCommandListAsync(GameCardItem? game)
    {
        if (game == null)
            return;
        if (!game.CanRefreshNow)
        {
            StatusText = game.Installed
                ? T("{0}: its command list comes with JoyPro updates.", game.Name)
                : T("{0} is not found on this PC.", game.Name);
            return;
        }
        List<string>? args = DbRefreshArguments(new DbUpdateItem(game.GameId, game.Name, string.Empty, "engine " + game.CommandName + " refresh-db"));
        if (args == null)
            return;
        await AddHllvOodleConsentAsync(game.GameId, args);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: "Reading the new commands of " + game.Name + "...");
        if (result == null)
            return;
        game.ResultFailed = result.ExitCode != 0;
        game.Result = result.ExitCode == 0
            ? T("Command list updated {0}", DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture))
            : T("Update failed: {0}", BuildFailureStatus(result));
        if (result.ExitCode != 0)
            return;
        if (!await BuildDcsCleanProfilesIfNewAsync(game.GameId, game.MissingCommandList))
        {
            game.ResultFailed = true;
            game.Result = T("Command list built; the DCS clean profiles failed (see the log)");
            return;
        }
        InvalidateDbCaches();
        RebuildRelationNodeGameOptions();
        await RunDbUpdateCheckAsync(["db", "check-updates"], "Checking games for updates...");
        game.NeedsDbUpdate = false;
        StatusText = T("{0}: command list is up to date.", game.Name);
    }

    [RelayCommand]
    private async Task UndoGameCardExportAsync(GameCardItem? game)
    {
        if (game == null)
            return;
        EngineCommandResult? result = await RestoreLatestGameBackupAsync(game.GameId, game.Name);
        if (result == null)
            return;
        game.ResultFailed = result.ExitCode != 0;
        game.Result = result.ExitCode == 0 ? T("Last export undone") : T("Undo failed: {0}", BuildFailureStatus(result));
    }

    // "Export" on a card: the Export page with this game's tile.
    [RelayCommand]
    private async Task ExportGameCardAsync(GameCardItem? game)
    {
        CurrentStage = UiStage.Export;
        await RefreshExportGamesAsync();
        if (game == null)
            return;
        // Only this game for now; the ticks the user set on the Export page stay remembered.
        using (SuppressExportSettings())
        {
            foreach (ExportGameItem tile in ExportGames)
                tile.IsSelected = string.Equals(tile.GameId, game.GameId, StringComparison.OrdinalIgnoreCase);
        }
        if (ExportGames.All(t => !t.IsSelected))
            StatusText = T("{0}: your profile has no relations for this game yet. Import from it or add relations first.", game.Name);
    }
}
