using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// The export tiles: every game the profile has relations for (engine profile games), with its cover, what the export
// writes, and an export button. DCS opens to its aircraft with their pictures; only the ticked aircraft are exported.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<ExportGameItem> ExportGames { get; } = [];

    [ObservableProperty]
    private string exportGamesStatus = string.Empty;

    // Games of the profile that are not installed (no tile; Settings > Games sets their folder).
    [ObservableProperty]
    private string exportGamesNotInstalled = string.Empty;

    [RelayCommand]
    private async Task RefreshExportGamesAsync()
    {
        if (string.IsNullOrWhiteSpace(ProfilePath))
        {
            ExportGamesStatus = T("Load a profile to see which games it exports to.");
            return;
        }
        EngineCommandResult? result = await RunEngineAsync(["profile", "games"], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Listing the games of the profile..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;

        // Ticks the user changed survive a refresh and the next start (remembered per profile,
        // MainWindowViewModel.ExportSettings.cs); the others follow the game (bound relations now, started since).
        (Dictionary<string, bool> gameTicks, Dictionary<string, bool> planeTicks, bool sameProfile) = ExportTicksForRefresh();
        Dictionary<string, (string State, bool Failed)> exportStates = sameProfile
            ? ExportGames.Where(g => g.ExportState.Length > 0).ToDictionary(g => g.GameId, g => (g.ExportState, g.ExportFailed), StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> expanded = sameProfile ? new(ExportGames.Where(g => g.IsExpanded).Select(g => g.GameId), StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        List<ExportGameItem> games = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            if (doc.RootElement.TryGetProperty("Games", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    string gameId = ReadJsonString(item, "GameId");
                    if (gameId.Length == 0)
                        continue;
                    bool installed = !item.TryGetProperty("Installed", out JsonElement installedValue) || installedValue.ValueKind != JsonValueKind.False;
                    bool notOwned = ReadJsonBool(item, "Disabled") || IsGameHidden(gameId);
                    ExportGameItem game = new(gameId, ReadJsonString(item, "Name"), ReadJsonString(item, "Exporter"), ReadJsonInt(item, "Relations"), ReadJsonInt(item, "Bound"),
                        installed && !notOwned, notOwned, started: GameWasStarted(gameId))
                    {
                        HasBackup = gamesWithBackups.Contains(gameId)
                    };
                    if (gameTicks.TryGetValue(gameId, out bool tick))
                        game.IsSelected = tick;
                    if (exportStates.TryGetValue(gameId, out (string State, bool Failed) state))
                    {
                        game.ExportFailed = state.Failed;
                        game.ExportState = state.State;
                    }
                    game.IsExpanded = expanded.Contains(gameId);
                    if (item.TryGetProperty("Planes", out JsonElement planes) && planes.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement plane in planes.EnumerateArray())
                        {
                            ExportPlaneItem planeItem = new(ReadJsonString(plane, "Plane"), ReadJsonInt(plane, "Relations"), ReadJsonInt(plane, "Bound"));
                            if (planeTicks.TryGetValue(planeItem.Plane, out bool planeTick))
                                planeItem.IsSelected = planeTick;
                            game.AddPlane(planeItem);
                        }
                    }
                    games.Add(game);
                }
            }
        }
        catch (JsonException)
        {
            return;
        }

        ExportGames.Clear();
        foreach (ExportGameItem game in games.Where(g => g.Installed))
            ExportGames.Add(game);
        WatchExportTiles(ExportGames);
        List<string> missing = games.Where(g => !g.Installed && !g.Hidden).Select(g => g.Name).ToList();
        List<string> hidden = games.Where(g => g.Hidden).Select(g => g.Name).ToList();
        List<string> notes = [];
        if (missing.Count > 0)
            notes.Add(T("Not installed on this PC, so not exported: {0}. If a game is installed, set its folder in Settings > Games.", string.Join(", ", missing)));
        if (hidden.Count > 0)
            notes.Add(T("Hidden, as you do not have {0}: {1} (Settings > Games).", (hidden.Count == 1 ? "it" : "them"), string.Join(", ", hidden)));
        ExportGamesNotInstalled = string.Join(" ", notes);
        games = games.Where(g => g.Installed).ToList();
        ExportGamesStatus = ExportTilesStatus(games);
        _ = LoadExportGamePicturesAsync(games);
    }

    // The games JoyPro made a backup for (engine backups list): their tiles can undo the last export.
    private readonly HashSet<string> gamesWithBackups = new(StringComparer.OrdinalIgnoreCase);

    // Started once: the game has written its control files (engine games installs: ConfigPaths). Unknown (the
    // installs are not read yet) counts as started.
    private bool GameWasStarted(string gameId)
    {
        GameInstallItem? install = GameInstalls.FirstOrDefault(g => string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));
        return install == null || install.ConfigPathList.Count > 0 || install.ConfigOverridden;
    }

    internal static string ExportTilesStatus(IReadOnlyCollection<ExportGameItem> games)
    {
        if (games.Count == 0)
            return T("The profile has no relations for any game yet.");
        int ticked = games.Count(g => g.IsSelected);
        int noBinds = games.Count(g => g.Bound == 0);
        int notStarted = games.Count(g => g.Bound > 0 && !g.Started);
        string text = Plural("The profile exports to {0} game; {1} ticked for export.", "The profile exports to {0} games; {1} ticked for export.", games.Count, games.Count, ticked);
        if (noBinds > 0)
            text += " " + Plural("{0} game without a bound relation is not ticked.", "{0} games without a bound relation are not ticked.", noBinds);
        if (notStarted > 0)
            text += " " + Plural("{0} game not started yet is not ticked (start the game once, so it writes its control files).", "{0} games not started yet are not ticked (start the game once, so it writes its control files).", notStarted);
        return text;
    }

    // "Exported 5 of 6 games, 1 failed: MSFS 2020 (No MSFS device profiles ...)".
    internal static string ExportRunStatus(IReadOnlyList<ExportGameItem> games, IReadOnlyList<ExportGameItem> exported, bool dryRun)
    {
        List<ExportGameItem> failed = games.Where(g => !exported.Contains(g)).ToList();
        string text = dryRun
            ? Plural("Checked {0} of {1} game", "Checked {0} of {1} games", games.Count, exported.Count, games.Count)
            : Plural("Exported {0} of {1} game", "Exported {0} of {1} games", games.Count, exported.Count, games.Count);
        if (failed.Count == 0)
            return text + ".";
        // One failure with its reason; several by name (each tile says why).
        if (failed.Count == 1)
            return T("{0}, 1 failed: {1} ({2}).", text, failed[0].Name, failed[0].ExportState.Replace(FailedPrefix, string.Empty).TrimEnd('.'));
        return T("{0}, {1} failed: {2} (see the tiles).", text, failed.Count, string.Join(", ", failed.Select(g => g.Name)));
    }

    private async Task LoadExportGamePicturesAsync(List<ExportGameItem> games)
    {
        foreach (ExportGameItem game in games)
            game.Cover ??= await LoadGameCoverAsync(game.GameId, 150);
        foreach (ExportPlaneItem plane in games.SelectMany(g => g.Planes))
        {
            plane.Image ??= await LoadAircraftPictureAsync(plane.Plane);
            plane.DisplayName = await AircraftDisplayNameAsync(plane.Plane);
        }
    }

    [RelayCommand]
    private async Task ExportGameNowAsync(ExportGameItem? game)
    {
        if (game == null || !await ConfirmRiskyExportAsync(game.Name))
            return;
        bool ok = await ExportGameAsync(game);
        StatusText = ok ? game.Name + ": " + game.ExportState + "." : game.Name + ": " + game.ExportState;
        await AfterExportsAsync();
    }

    [RelayCommand]
    private async Task ExportSelectedGamesAsync()
    {
        List<ExportGameItem> games = ExportGames.Where(g => g.IsSelected && g.CanExport).ToList();
        if (games.Count == 0)
        {
            StatusText = T("Tick at least one game to export.");
            return;
        }
        if (!await ConfirmRiskyExportAsync(string.Join(", ", games.Select(g => g.Name))))
            return;
        List<ExportGameItem> exported = [];
        foreach (ExportGameItem game in games)
        {
            if (await ExportGameAsync(game))
                exported.Add(game);
        }
        string status = ExportRunStatus(games, exported, dryRun: false);
        await AfterExportsAsync();
        StatusText = status;
        ExportGamesStatus = status;
    }

    // After an export the backups list shows the new backup sets (and the tiles can undo).
    private async Task AfterExportsAsync()
    {
        string status = StatusText;
        await RefreshGameBackupsAsync();
        StatusText = status;
    }

    private async Task<bool> ExportGameAsync(ExportGameItem game)
    {
        if (!game.CanExport)
            return MarkExport(game, null, T("JoyPro has no export for this game."));
        EngineCommandResult? result;
        switch (game.Exporter)
        {
            case "dcs":
                {
                    List<string> planes = game.Planes.Where(p => p.IsSelected).Select(p => p.Plane).ToList();
                    if (game.HasPlanes && planes.Count == 0)
                        return MarkExport(game, null, T("No aircraft ticked."));
                    result = await RunDcsExportAsync(game.AllPlanesSelected ? null : planes);
                    break;
                }
            case "il2":
                result = await RunIl2ExportAsync();
                break;
            case "starcitizen":
                result = await RunStarCitizenExportAsync();
                break;
            default:
                {
                    if (RegistryGames.Count == 0)
                        await RefreshRegistryGamesAsync();
                    RegistryGameItem? registryGame = RegistryGames.FirstOrDefault(g => string.Equals(g.GameId, game.GameId, StringComparison.OrdinalIgnoreCase));
                    if (registryGame == null)
                        return MarkExport(game, null, T("The engine does not know this game."));
                    if (SelectedRegistryGame != registryGame)
                        SelectedRegistryGame = registryGame;
                    result = await RunRegistryGameExportAsync();
                    break;
                }
        }
        return MarkExport(game, result, null);
    }

    private bool MarkExport(ExportGameItem game, EngineCommandResult? result, string? problem)
    {
        if (problem == null && result == null)
            problem = T("Not exported (see the status line).");
        if (problem == null && result!.ExitCode != 0)
            problem = BuildFailureStatus(result);
        bool dryRun = game.Exporter == "registry" && RegistryGameDryRun;
        game.ExportFailed = problem != null;
        game.ExportState = problem ?? T(dryRun ? "Dry run checked {0}" : "Exported {0}", DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture));
        // A real export backed up the game's files first: the tile can undo it.
        if (problem == null && !dryRun)
        {
            game.HasBackup = true;
            gamesWithBackups.Add(game.GameId);
        }
        return problem == null;
    }

    private static int ReadJsonInt(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;
    }
}
