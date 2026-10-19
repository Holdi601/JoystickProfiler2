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

// Games handled by the engine's game registry ("engine games integrations"): one panel for scan, import, export
// and DB refresh, plus their action catalogs (DB\<GameId>\actions.tsv) in the relation node search.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<RegistryGameItem> RegistryGames { get; } = [];

    [ObservableProperty]
    private RegistryGameItem? selectedRegistryGame;

    [ObservableProperty]
    private string registryGamePath = string.Empty;

    [ObservableProperty]
    private string registryGameProfileName = string.Empty;

    [ObservableProperty]
    private string registryGameDbSource = string.Empty;

    [ObservableProperty]
    private bool registryGameDryRun;

    // Keyboard binds are only imported and exported when the user asks for them (flight actions only).
    [ObservableProperty]
    private bool registryGameIncludeKeyboard;

    // MSFS keeps the binds of every device it ever saw: import takes the connected devices unless the user asks for
    // the others too (engine --include-disconnected; remembered with the import/export settings).
    [ObservableProperty]
    private bool importIncludeDisconnected;

    // MSFS runs an untouched device on a default preset it ships; JoyPro takes it when the sim's device records show
    // it is used. On: also when that is not known (engine --include-sim-defaults; remembered like the one above).
    [ObservableProperty]
    private bool importIncludeSimDefaults;

    internal static bool KeepsDisconnectedDevices(string? gameId) =>
        string.Equals(gameId, "MSFS2020", StringComparison.OrdinalIgnoreCase) || string.Equals(gameId, "MSFS2024", StringComparison.OrdinalIgnoreCase);

    public bool SelectedRegistryGameKeepsDisconnectedDevices => KeepsDisconnectedDevices(SelectedRegistryGame?.GameId);

    public string SelectedRegistryGameNotes => SelectedRegistryGame?.Notes ?? T("Load the game list and pick a game.");

    public bool SelectedRegistryGameSupportsProfiles => SelectedRegistryGame?.SupportsGameProfile == true;

    public bool SelectedRegistryGameSupportsRefresh => SelectedRegistryGame?.SupportsCatalogRefresh == true;

    public bool SelectedRegistryGameSupportsAircraft => SelectedRegistryGame?.SupportsAircraft == true;

    // Export only: the aircraft whose limited relations are written too (empty = only the relations for all aircraft).
    [ObservableProperty]
    private string registryGameAircraft = string.Empty;

    // The aircraft keys of the selected game's DB list (DB\<GameId>\aircraft.tsv), for the export's aircraft box.
    public ObservableCollection<string> RegistryGameAircraftOptions { get; } = [];

    // Aircraft lists of the registry games (DB\<GameId>\aircraft.tsv, written by the game's refresh-db): the plane
    // pickers list them next to the game name, which stands for all aircraft.
    private readonly Dictionary<string, List<RegistryAircraft>> registryAircraftLists = new(StringComparer.OrdinalIgnoreCase);

    private sealed record RegistryAircraft(string Key, string Title, string Description);

    private IReadOnlyList<RegistryAircraft> RegistryAircraftOf(string game)
    {
        return registryAircraftLists.TryGetValue(NormalizeRelationGameToken(game), out List<RegistryAircraft>? list) ? list : [];
    }

    private void RebuildRegistryGameAircraftOptions()
    {
        RegistryGameAircraftOptions.Clear();
        if (SelectedRegistryGame?.SupportsAircraft != true)
            return;
        string catalogPath = Path.Combine(GameDataLocations.DbFolder(GetEngineDirectoryOrNull(), SelectedRegistryGame.GameId), "aircraft.tsv");
        foreach (RegistryAircraft aircraft in ReadRegistryAircraft(catalogPath))
            RegistryGameAircraftOptions.Add(aircraft.Key);
    }

    private string? GetEngineDirectoryOrNull()
    {
        string engineExe = ResolveEnginePath();
        string? engineDirectory = string.IsNullOrWhiteSpace(engineExe) ? null : Path.GetDirectoryName(engineExe);
        return string.IsNullOrWhiteSpace(engineDirectory) ? null : engineDirectory;
    }

    // aircraft.tsv: key, title, category, sims, ICAO type, manufacturer, packages, variants, title source.
    private static List<RegistryAircraft> ReadRegistryAircraft(string path)
    {
        List<RegistryAircraft> list = [];
        if (!File.Exists(path))
            return list;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return list;
        }
        foreach (string line in lines)
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            string[] parts = line.Split('\t');
            string key = parts[0].Trim();
            if (key.Length == 0)
                continue;
            string title = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            string category = parts.Length > 2 ? parts[2].Trim() : string.Empty;
            string sims = parts.Length > 3 ? parts[3].Trim().Replace(",", "/") : string.Empty;
            string description = string.Join(", ", new[] { category, sims.Length > 0 ? "MSFS " + sims : string.Empty }.Where(v => v.Length > 0));
            list.Add(new RegistryAircraft(key, title, description.Length > 0 ? description : "aircraft"));
        }
        return list;
    }

    partial void OnSelectedRegistryGameChanged(RegistryGameItem? value)
    {
        OnPropertyChanged(nameof(SelectedRegistryGameNotes));
        OnPropertyChanged(nameof(SelectedRegistryGameKeepsDisconnectedDevices));
        OnPropertyChanged(nameof(SelectedRegistryGameSupportsProfiles));
        OnPropertyChanged(nameof(SelectedRegistryGameSupportsRefresh));
        OnPropertyChanged(nameof(SelectedRegistryGameSupportsAircraft));
        RegistryGameAircraft = string.Empty;
        RebuildRegistryGameAircraftOptions();
        OnPropertyChanged(nameof(SelectedRegistryGameInstalled));
        OnPropertyChanged(nameof(CanRunRegistryGame));
        OnPropertyChanged(nameof(NotInstalledHint));
        // The path box: the one the user set for this game before (remembered), else empty (detected).
        restoringRegistryGamePath = true;
        try
        {
            RegistryGamePath = string.Empty;
        }
        finally
        {
            restoringRegistryGamePath = false;
        }
        RestoreRegistryGamePath(value);
        RegistryGameProfileName = string.Empty;
        RegistryGameDbSource = string.Empty;
    }

    [RelayCommand]
    private async Task RefreshRegistryGamesAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(
            ["games", "integrations"],
            includeProfile: false,
            mutatesProfile: false,
            busyLabel: N("Loading game integrations..."),
            forceText: false);
        if (result == null || result.ExitCode != 0)
            return;

        allRegistryGames.Clear();
        allRegistryGames.AddRange(ParseRegistryGames(result.StandardOutput));
        RebuildVisibleRegistryGames();
        RebuildGameCards();
        StatusText = T("Game integrations: {0}{1}", RegistryGames.Count, (allRegistryGames.Count > RegistryGames.Count ? T(" ({0} hidden).", (allRegistryGames.Count - RegistryGames.Count)) : "."));
    }

    // Every integration the engine has; RegistryGames leaves out the games the user does not have.
    private readonly List<RegistryGameItem> allRegistryGames = [];

    private void RebuildVisibleRegistryGames()
    {
        string? previous = SelectedRegistryGame?.GameId;
        List<RegistryGameItem> visible = allRegistryGames.Where(g => !IsGameHidden(g.GameId)).ToList();
        if (visible.SequenceEqual(RegistryGames))
            return;
        RegistryGames.Clear();
        foreach (RegistryGameItem game in visible)
        {
            game.Installed = IsGameInstalled(game.GameId);
            RegistryGames.Add(game);
        }
        RegistryGameItem? keep = RegistryGames.FirstOrDefault(g => string.Equals(g.GameId, previous, StringComparison.OrdinalIgnoreCase)) ?? RegistryGames.FirstOrDefault();
        if (SelectedRegistryGame != keep)
            SelectedRegistryGame = keep;
    }

    private static List<RegistryGameItem> ParseRegistryGames(string output)
    {
        List<RegistryGameItem> games = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            if (!doc.RootElement.TryGetProperty("Integrations", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                return games;
            foreach (JsonElement item in list.EnumerateArray())
            {
                string gameId = ReadJsonString(item, "GameId");
                if (gameId.Length == 0)
                    continue;
                string command = item.TryGetProperty("Aliases", out JsonElement aliases) && aliases.ValueKind == JsonValueKind.Array && aliases.GetArrayLength() > 0
                    ? aliases[0].GetString() ?? gameId
                    : gameId;
                games.Add(new RegistryGameItem(
                    gameId,
                    ReadJsonString(item, "DisplayName"),
                    command,
                    ReadJsonString(item, "Notes"),
                    ReadJsonBool(item, "Experimental"),
                    ReadJsonBool(item, "SupportsCatalogRefresh"),
                    ReadJsonBool(item, "SupportsGameProfile"),
                    ReadJsonBool(item, "SupportsAircraft")));
            }
        }
        catch (JsonException)
        {
        }
        return games;
    }

    private static string ReadJsonString(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static bool ReadJsonBool(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    }

    private List<string>? BuildRegistryGameArgs(string action, bool withPath = true)
    {
        if (SelectedRegistryGame == null)
        {
            StatusText = T("Pick a game under 'Other Games' first (Load Games).");
            return null;
        }

        List<string> args = [SelectedRegistryGame.CommandName, action];
        if (withPath && !string.IsNullOrWhiteSpace(RegistryGamePath))
            args.AddRange(["--path", RegistryGamePath.Trim()]);
        if (SelectedRegistryGame.SupportsGameProfile && !string.IsNullOrWhiteSpace(RegistryGameProfileName))
            args.AddRange(["--game-profile", RegistryGameProfileName.Trim()]);
        return args;
    }

    [RelayCommand]
    private async Task DetectRegistryGamePathAsync()
    {
        if (SelectedRegistryGame == null)
        {
            StatusText = T("Pick a game first.");
            return;
        }

        EngineCommandResult? result = await RunEngineAsync(
            [SelectedRegistryGame.CommandName, "paths"],
            includeProfile: false,
            mutatesProfile: false,
            busyLabel: "Detecting " + SelectedRegistryGame.DisplayName + " files...",
            forceText: false);
        if (result == null || result.ExitCode != 0)
            return;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            if (doc.RootElement.TryGetProperty("ConfigPaths", out JsonElement paths) && paths.ValueKind == JsonValueKind.Array && paths.GetArrayLength() > 0)
            {
                RegistryGamePath = paths[0].GetString() ?? string.Empty;
                StatusText = paths.GetArrayLength() == 1
                    ? T("Found: {0}", RegistryGamePath)
                    : T("Found {0} files; using the newest. See Engine Output for the others.", paths.GetArrayLength());
                return;
            }
        }
        catch (JsonException)
        {
        }
        StatusText = T("No {0} control file found. Enter the path manually.", SelectedRegistryGame.DisplayName);
    }

    [RelayCommand]
    private async Task ScanRegistryGameAsync()
    {
        List<string>? args = BuildRegistryGameArgs("scan");
        if (args == null)
            return;
        AddDisconnectedDevicesOption(args);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: "Scanning " + SelectedRegistryGame!.DisplayName + "...");
        await AfterCommandListBuiltAsync(result);
    }

    // A scan, import or export of a game without a command list builds it first (a fresh install ships none; the
    // engine says "... had no ... command list yet: built it ..."): the lists the GUI keeps are read again and the
    // missing-list notices go away.
    // (No second engine call here: the caller still reads the import's exit code, status and output.)
    private Task AfterCommandListBuiltAsync(EngineCommandResult? result, string? gameId = null)
    {
        if (result == null || !EngineBuiltCommandList(result.StandardOutput))
            return Task.CompletedTask;
        InvalidateDbCaches();
        RebuildRelationNodeGameOptions();
        RebuildRegistryGameAircraftOptions();
        string game = gameId ?? SelectedRegistryGame?.GameId ?? string.Empty;
        foreach (DbUpdateItem item in DbUpdateItems.Where(u => u.MissingList && string.Equals(u.Game, game, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            DbUpdateItems.Remove(item);
            DbUpdateMessages.Remove(item.Message);
        }
        HasDbUpdates = DbUpdateMessages.Count > 0;
        HasMissingCommandLists = DbUpdateItems.Any(u => u.MissingList && u.CanRefresh);
        foreach (GameCardItem card in GameCards.Where(c => string.Equals(c.GameId, game, StringComparison.OrdinalIgnoreCase)))
        {
            card.MissingCommandList = false;
            card.NeedsDbUpdate = DbUpdateItems.Any(u => string.Equals(u.Game, game, StringComparison.OrdinalIgnoreCase));
        }
        return Task.CompletedTask;
    }

    // MSFS import and scan: the profiles of devices that are not connected only when the user ticked it, and the
    // sim's default presets whose use the sim's records do not show likewise.
    internal void AddDisconnectedDevicesOption(List<string> args)
    {
        if (ImportIncludeDisconnected && SelectedRegistryGameKeepsDisconnectedDevices)
            args.Add("--include-disconnected");
        if (ImportIncludeSimDefaults && SelectedRegistryGameKeepsDisconnectedDevices)
            args.Add("--include-sim-defaults");
    }

    internal static bool EngineBuiltCommandList(string? output)
    {
        return !string.IsNullOrEmpty(output) && System.Text.RegularExpressions.Regex.IsMatch(output, @"had no .{1,80} command list yet: built it");
    }

    [RelayCommand]
    private async Task ImportRegistryGameAsync()
    {
        List<string>? args = BuildRegistryGameArgs("import");
        if (args == null)
            return;
        if (!ImportMerge)
            args.Add("--replace");
        if (RegistryGameIncludeKeyboard)
            args.Add("--include-keyboard");
        AddDisconnectedDevicesOption(args);

        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: "Importing from " + SelectedRegistryGame!.DisplayName + "...");
        await AfterCommandListBuiltAsync(result);
        if (result?.ExitCode != 0)
            return;

        bool previousSuppress = suppressStageAutoRefresh;
        suppressStageAutoRefresh = true;
        try
        {
            CurrentStage = UiStage.Relations;
            await RefreshRelationsAsync();
            await RefreshBindsAsync();
            await RefreshModifiersAsync();
        }
        finally
        {
            suppressStageAutoRefresh = previousSuppress;
        }
    }

    [RelayCommand]
    private async Task ExportRegistryGameAsync() => await RunRegistryGameExportAsync();

    private async Task<EngineCommandResult?> RunRegistryGameExportAsync()
    {
        List<string>? args = BuildRegistryGameArgs("export");
        if (args == null)
            return null;
        args.AddRange(["--mode", string.IsNullOrWhiteSpace(ExportMode) ? "merge-overwrite" : ExportMode.Trim()]);
        if (RegistryGameDryRun)
            args.Add("--dry-run");
        if (RegistryGameIncludeKeyboard)
            args.Add("--include-keyboard");
        if (ExportNukeUntouchedDevices)
            args.Add("--nuke-untouched-devices");
        if (SelectedRegistryGame!.SupportsAircraft && !string.IsNullOrWhiteSpace(RegistryGameAircraft))
            args.AddRange(["--aircraft", RegistryGameAircraft.Trim()]);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: false, busyLabel: (RegistryGameDryRun ? T("Checking export to ") : T("Exporting to ")) + SelectedRegistryGame!.DisplayName + "...");
        await AfterCommandListBuiltAsync(result);
        return result;
    }

    [RelayCommand]
    private async Task RefreshRegistryGameDbAsync()
    {
        List<string>? args = BuildRegistryGameArgs("refresh-db", withPath: false);
        if (args == null)
            return;
        if (!SelectedRegistryGame!.SupportsCatalogRefresh)
        {
            StatusText = T("{0} has no DB refresh; its action list ships with JoyPro.", SelectedRegistryGame.DisplayName);
            return;
        }
        if (!string.IsNullOrWhiteSpace(RegistryGameDbSource))
            args.AddRange(["--path", RegistryGameDbSource.Trim()]);

        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: "Refreshing the " + SelectedRegistryGame.DisplayName + " DB...");
        if (result?.ExitCode == 0)
        {
            InvalidateDbCaches();
            RebuildRelationNodeGameOptions();
            RebuildRegistryGameAircraftOptions();
        }
    }

    // DB\<GameId>\actions.tsv: "id<TAB>title<TAB>axis|button<TAB>category"; the folder name is the engine's GameId.
    // Each game's folder is read where the engine reads it (<data>\GameData first, GameDataLocations).
    private void LoadRegistryGameDbNodeCatalogs()
    {
        registryAircraftLists.Clear();
        string? engineDirectory = GetEngineDirectoryOrNull();
        if (engineDirectory == null)
            return;

        foreach (string folder in GameDataLocations.GameDbFolders(engineDirectory))
        {
            string catalogPath = Path.Combine(folder, "actions.tsv");
            if (!File.Exists(catalogPath))
                continue;

            string gameId = Path.GetFileName(folder);
            List<RegistryAircraft> aircraft = ReadRegistryAircraft(Path.Combine(folder, "aircraft.tsv"));
            if (aircraft.Count > 0)
                registryAircraftLists[NormalizeRelationGameToken(gameId)] = aircraft;
            List<DbNodeCatalogEntry> entries = [];
            string[] lines;
            try
            {
                lines = File.ReadAllLines(catalogPath);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (string line in lines)
            {
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;
                string[] parts = line.Split('\t');
                if (parts.Length < 3 || parts[0].Trim().Length == 0)
                    continue;
                string kind = parts[2].Trim().Equals("axis", StringComparison.OrdinalIgnoreCase) ? "Axis" : "Button";
                string category = parts.Length > 3 ? parts[3].Trim() : string.Empty;
                entries.Add(new DbNodeCatalogEntry(gameId, gameId, parts[0].Trim(), parts[1].Trim(), category.Length > 0 ? kind + " | " + category : kind));
            }

            if (entries.Count > 0)
                dbNodeCatalogByGame[gameId] = entries.Distinct(DbNodeCatalogEntryComparer.Instance).ToList();
        }
    }
}
