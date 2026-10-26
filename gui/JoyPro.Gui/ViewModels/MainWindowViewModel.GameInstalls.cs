using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// Settings > Games: which supported games are installed and where (engine games installs), with the user's own
// folder per game. A game that is not installed cannot be imported, exported, scanned, refreshed or restored; its
// buttons are off and the export page leaves it out. Hidden games (the user does not have them or is not interested:
// the hide button on a Start page card, "I have this game" unticked in the game setup, Hide in Settings > Games;
// engine games install disable) count as not installed: the Start page lists them under "Hidden games" with "Show
// again", Settings > Games lists them apart, and the cards, the game counts, the watcher, the setup suggestions, the
// export page, the registry game lists and the relation node game picker leave them out. The profile's relations and
// binds for them stay.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<GameInstallItem> GameInstalls { get; } = [];

    // Settings > Games: the games the user has, and (collapsed) the ones they do not have.
    public ObservableCollection<GameInstallItem> OwnedGameInstalls { get; } = [];

    public ObservableCollection<GameInstallItem> HiddenGameInstalls { get; } = [];

    private readonly HashSet<string> installedGames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> hiddenGames = new(StringComparer.OrdinalIgnoreCase);
    private bool gameInstallsKnown;
    private string lastAdoptedDcsConfig = string.Empty;
    private string lastAdoptedStarCitizenConfig = string.Empty;

    [ObservableProperty]
    private string gameInstallsStatus = "Checking which games are installed...";

    // Until the first check has answered, nothing is blocked (the engine still refuses what is not installed).
    public bool IsGameInstalled(string gameId) => !gameInstallsKnown || installedGames.Contains(gameId);

    // The user said they do not have the game: game lists leave it out.
    public bool IsGameHidden(string gameId) => hiddenGames.Contains(NormalizeRelationGameToken(gameId));

    public bool HasHiddenGames => HiddenGameInstalls.Count > 0;

    public string HiddenGamesHeader => T("Hidden games ({0}): not shown anywhere in JoyPro; your binds for them stay", HiddenGameInstalls.Count);

    // The Start page's list under the cards.
    public string HiddenGamesTitle => T("Hidden games ({0})", HiddenGameInstalls.Count);

    [ObservableProperty]
    private bool hiddenGamesExpanded;

    public bool IsDcsInstalled => IsGameInstalled("DCS");

    public bool IsIl2Installed => IsGameInstalled("IL2Game");

    public bool IsStarCitizenInstalled => IsGameInstalled("StarCitizen");

    public bool CanRunDcs => CanRun && IsDcsInstalled;

    public bool CanRunIl2 => CanRun && IsIl2Installed;

    public bool CanRunStarCitizen => CanRun && IsStarCitizenInstalled;

    public bool SelectedRegistryGameInstalled => SelectedRegistryGame == null || IsGameInstalled(SelectedRegistryGame.GameId);

    public bool CanRunRegistryGame => CanRun && SelectedRegistryGameInstalled;

    public string NotInstalledHint
    {
        get
        {
            List<string> missing = [];
            List<string> hidden = [];
            void Check(string gameId, string name, bool installed)
            {
                if (installed)
                    return;
                (IsGameHidden(gameId) ? hidden : missing).Add(name);
            }
            Check("DCS", "DCS", IsDcsInstalled);
            Check("IL2Game", "IL-2", IsIl2Installed);
            Check("StarCitizen", T("Star Citizen"), IsStarCitizenInstalled);
            if (SelectedRegistryGame != null)
                Check(SelectedRegistryGame.GameId, SelectedRegistryGame.DisplayName, SelectedRegistryGameInstalled);
            List<string> parts = [];
            if (missing.Count > 0)
                parts.Add(T("Not installed (blocked): {0}. If a game is installed, set its folder in Settings > Games.", string.Join(", ", missing)));
            if (hidden.Count > 0)
                parts.Add(T("You do not have (hidden): {0}. Settings > Games brings a game back.", string.Join(", ", hidden)));
            return string.Join(" ", parts);
        }
    }

    private void NotifyInstallState()
    {
        OnPropertyChanged(nameof(IsDcsInstalled));
        OnPropertyChanged(nameof(IsIl2Installed));
        OnPropertyChanged(nameof(IsStarCitizenInstalled));
        OnPropertyChanged(nameof(CanRunDcs));
        OnPropertyChanged(nameof(CanRunIl2));
        OnPropertyChanged(nameof(CanRunStarCitizen));
        OnPropertyChanged(nameof(SelectedRegistryGameInstalled));
        OnPropertyChanged(nameof(CanRunRegistryGame));
        OnPropertyChanged(nameof(NotInstalledHint));
        OnPropertyChanged(nameof(CanRestoreGameBackup));
        foreach (RegistryGameItem game in RegistryGames)
            game.Installed = IsGameInstalled(game.GameId);
    }

    [RelayCommand]
    private async Task RefreshGameInstallsAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["games", "installs"], includeProfile: false, mutatesProfile: false,
            busyLabel: N("Checking which games are installed..."), forceText: false);
        if (result != null && result.ExitCode == 0)
            ApplyGameInstalls(result.StandardOutput, replaceAll: true);
    }

    private void ApplyGameInstalls(string output, bool replaceAll)
    {
        List<GameInstallItem> items = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            if (doc.RootElement.TryGetProperty("Games", out JsonElement games) && games.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement game in games.EnumerateArray())
                {
                    items.Add(new GameInstallItem(ReadJsonString(game, "GameId"), DisplayText.GameName(ReadJsonString(game, "GameId"), ReadJsonString(game, "Name")), ReadJsonBool(game, "Installed"),
                        ReadJsonString(game, "InstallPath"), ReadJsonString(game, "Source"), ReadJsonBool(game, "Overridden"), ReadJsonBool(game, "MarkerFound"),
                        ReadJsonString(game, "Note"), ReadStringArray(game, "ConfigPaths"), ReadJsonBool(game, "Disabled"), ReadStringArray(game, "Markers"),
                        ReadJsonBool(game, "ConfigOverridden") ? ReadJsonString(game, "ConfigOverride") : string.Empty, ReadJsonBool(game, "ConfigInInstallation"))
                    {
                        // "games installs set" and older engines do not count: -1 (nothing is left out for it).
                        Actions = game.TryGetProperty("Actions", out JsonElement actions) && actions.ValueKind == JsonValueKind.Number && actions.TryGetInt32(out int n) ? n : -1,
                        ActionsNote = ReadJsonString(game, "ActionsNote")
                    });
                }
            }
        }
        catch (JsonException)
        {
            return;
        }
        if (items.Count == 0)
            return;
        if (replaceAll)
            GameInstalls.Clear();
        foreach (GameInstallItem item in items)
        {
            GameInstallItem? old = GameInstalls.FirstOrDefault(g => g.GameId == item.GameId);
            int index = old == null ? GameInstalls.Count : GameInstalls.IndexOf(old);
            if (old != null)
            {
                item.Cover = old.Cover;
                if (item.Actions < 0)
                {
                    item.Actions = old.Actions;
                    item.ActionsNote = old.ActionsNote;
                }
                GameInstalls.Remove(old);
            }
            GameInstalls.Insert(index, item);
        }
        installedGames.Clear();
        foreach (GameInstallItem item in GameInstalls.Where(g => g.Installed))
            installedGames.Add(item.GameId);
        bool hiddenChanged = !hiddenGames.SetEquals(GameInstalls.Where(g => g.Disabled).Select(g => g.GameId));
        hiddenGames.Clear();
        foreach (GameInstallItem item in GameInstalls.Where(g => g.Disabled))
            hiddenGames.Add(item.GameId);
        OwnedGameInstalls.Clear();
        HiddenGameInstalls.Clear();
        foreach (GameInstallItem item in GameInstalls)
            (item.Disabled ? HiddenGameInstalls : OwnedGameInstalls).Add(item);
        OnPropertyChanged(nameof(HasHiddenGames));
        OnPropertyChanged(nameof(HiddenGamesHeader));
        OnPropertyChanged(nameof(HiddenGamesTitle));
        gameInstallsKnown = true;
        int installed = GameInstalls.Count(g => g.Installed);
        GameInstallsStatus = T("{0} of {1} supported games are installed{2}. Games that are not installed are blocked for import, export, DB refresh and backup restore.", installed, GameInstalls.Count, (hiddenGames.Count > 0 ? T(" ({0} hidden)", hiddenGames.Count) : string.Empty));
        AdoptEngineGamePaths();
        NotifyInstallState();
        RebuildGameCards();
        if (hiddenChanged)
        {
            RebuildVisibleRegistryGames();
            RebuildVisibleDbGames();
            RebuildVisibleFfbGames();
            if (GameBackupGames.Count > 1 || GameBackups.Count > 0)
                _ = RefreshGameBackupsAsync();
            if (ExportGames.Count > 0 || ExportGamesNotInstalled.Length > 0)
                _ = RefreshExportGamesAsync();
            // A hidden game is not watched any more (the engine's sync status leaves it out), a shown one again.
            if (gameSyncWatcherStarted)
                _ = RefreshGameConfigWatcherAsync(checkNow: false);
        }
        _ = LoadGameInstallCoversAsync(items);
    }

    // The GUI's own DCS and Star Citizen paths (prefilled from the default folders) follow the engine where the user
    // linked a folder or a config location, or where the default folder does not exist (portable installs, other drives).
    private void AdoptEngineGamePaths()
    {
        // The test sandbox (UserFolders): the engine finds the real games of the PC; their folders are never taken over.
        if (UserFolders.IsSandboxed)
            return;
        GameInstallItem? dcs = GameInstalls.FirstOrDefault(g => g.GameId == "DCS");
        if (dcs != null && !dcs.Disabled)
        {
            if (dcs.Installed && dcs.InstallPath.Length > 0 && Directory.Exists(dcs.InstallPath) &&
                (dcs.Overridden || string.IsNullOrWhiteSpace(DcsInstallPath) || !Directory.Exists(DcsInstallPath)))
                DcsInstallPath = dcs.InstallPath;
            string config = dcs.FirstConfigPath;
            if (config.Length > 0 && Directory.Exists(config) && !string.Equals(config, DcsSavedGamesPath, StringComparison.OrdinalIgnoreCase) &&
                (dcs.ConfigOverridden || !Directory.Exists(DcsSavedGamesPath) || string.Equals(DcsSavedGamesPath, lastAdoptedDcsConfig, StringComparison.OrdinalIgnoreCase)))
            {
                string previous = DcsSavedGamesPath;
                DcsSavedGamesPath = config;
                if (string.IsNullOrWhiteSpace(ImportDcsPath) || string.Equals(ImportDcsPath, previous, StringComparison.OrdinalIgnoreCase))
                    ImportDcsPath = config;
                if (string.IsNullOrWhiteSpace(ExportDcsPath) || string.Equals(ExportDcsPath, previous, StringComparison.OrdinalIgnoreCase))
                    ExportDcsPath = config;
                lastAdoptedDcsConfig = config;
            }
        }

        GameInstallItem? sc = GameInstalls.FirstOrDefault(g => g.GameId == "StarCitizen");
        if (sc != null && !sc.Disabled)
        {
            string config = sc.FirstConfigPath;
            if (config.Length > 0 && Directory.Exists(config) && !string.Equals(config, ImportStarCitizenPath, StringComparison.OrdinalIgnoreCase) &&
                (sc.ConfigOverridden || sc.Overridden || !Directory.Exists(ImportStarCitizenPath) ||
                 string.Equals(ImportStarCitizenPath, lastAdoptedStarCitizenConfig, StringComparison.OrdinalIgnoreCase)))
            {
                string previous = ImportStarCitizenPath;
                ImportStarCitizenPath = config;
                if (string.IsNullOrWhiteSpace(ExportStarCitizenPath) || string.Equals(ExportStarCitizenPath, previous, StringComparison.OrdinalIgnoreCase))
                    ExportStarCitizenPath = config;
                lastAdoptedStarCitizenConfig = config;
            }
        }
    }

    private async Task LoadGameInstallCoversAsync(List<GameInstallItem> items)
    {
        foreach (GameInstallItem item in items)
            item.Cover ??= await LoadGameCoverAsync(item.GameId, 80);
    }

    // The user's folder for a game (from the folder picker); it then counts as the installation.
    public async Task SetGameInstallFolderAsync(GameInstallItem item, string folder)
    {
        EngineCommandResult? result = await RunEngineAsync(["games", "install", "set", item.GameId, folder], includeProfile: false, mutatesProfile: false,
            busyLabel: "Setting the folder of " + item.Name + "...", forceText: false);
        if (result == null)
            return;
        if (result.ExitCode != 0)
        {
            GameInstallsStatus = BuildFailureStatus(result);
            return;
        }
        ApplyGameInstalls(result.StandardOutput, replaceAll: false);
        // The engine links the game's folder for a picked .exe, launcher folder or bin folder.
        string linked = GameInstalls.FirstOrDefault(g => g.GameId == item.GameId)?.InstallPath is { Length: > 0 } path ? path : folder;
        StatusText = string.Equals(linked.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? T("{0}: {1} is its installation now.", item.Name, linked)
            : T("{0}: {1} is its installation now (you picked {2}).", item.Name, linked, folder);
    }

    [RelayCommand]
    private async Task ClearGameInstallFolderAsync(GameInstallItem? item)
    {
        if (item == null)
            return;
        EngineCommandResult? result = await RunEngineAsync(["games", "install", "clear", item.GameId], includeProfile: false, mutatesProfile: false,
            busyLabel: "Detecting " + item.Name + " again...", forceText: false);
        if (result != null && result.ExitCode == 0)
            ApplyGameInstalls(result.StandardOutput, replaceAll: false);
    }

    // Settings > Games: "Show again" (engine games install enable) or "Hide" (disable: hidden everywhere).
    [RelayCommand]
    private async Task ToggleGameOwnedAsync(GameInstallItem? item)
    {
        if (item != null)
            await SetGameHiddenAsync(item.GameId, item.Name, !item.Disabled);
    }

    // The hide button on a Start page card.
    [RelayCommand]
    private async Task HideGameCardAsync(GameCardItem? card)
    {
        if (card != null)
            await SetGameHiddenAsync(card.GameId, card.Name, hide: true);
    }

    // "Show again" under the Start page's cards.
    [RelayCommand]
    private async Task ShowHiddenGameAsync(GameInstallItem? item)
    {
        if (item != null)
            await SetGameHiddenAsync(item.GameId, item.Name, hide: false);
    }

    internal async Task<bool> SetGameHiddenAsync(string gameId, string name, bool hide)
    {
        EngineCommandResult? result = await RunEngineAsync(["games", "install", hide ? "disable" : "enable", gameId], includeProfile: false, mutatesProfile: false,
            busyLabel: hide ? T("Hiding {0}...", name) : T("Showing {0}...", name), forceText: false);
        if (result == null)
            return false;
        if (result.ExitCode != 0)
        {
            GameInstallsStatus = BuildFailureStatus(result);
            StatusText = GameInstallsStatus;
            return false;
        }
        ApplyGameInstalls(result.StandardOutput, replaceAll: false);
        JoyPro.Logging.JoyProLog.Info("Games", (hide ? "Hidden: " : "Shown again: ") + gameId);
        StatusText = hide
            ? T("{0} is hidden: off the cards, the export and the watcher (your binds for it stay). 'Hidden games' under the cards shows it again.", name)
            : T("{0} is back on the cards, in the export and the watcher.", name);
        return true;
    }
}
