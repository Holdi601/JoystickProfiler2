using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// Games updated since their DB was built (engine db check-updates): shown as a banner on the Start page, per game with
// "Refresh DB" (the game's refresh command) and "Keep DB" (db check-updates --accept <game>).
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<string> DbUpdateMessages { get; } = [];

    public ObservableCollection<DbUpdateItem> DbUpdateItems { get; } = [];

    [ObservableProperty]
    private bool hasDbUpdates;

    [RelayCommand]
    private async Task CheckDbUpdatesAsync()
    {
        await RunDbUpdateCheckAsync(["db", "check-updates"], "Checking games for updates...");
        if (HasDbUpdates)
        {
            WarningRequested?.Invoke(DbUpdateMessages[0] + (DbUpdateMessages.Count > 1 ? " (+" + (DbUpdateMessages.Count - 1) + " more on the Start page)" : string.Empty));
        }
    }

    // The user saw the hint and keeps the DB as it is (or refreshed it outside JoyPro).
    [RelayCommand]
    private async Task AcceptDbUpdatesAsync()
    {
        await RunDbUpdateCheckAsync(["db", "check-updates", "--accept-all"], "Marking the DBs as current...");
    }

    // One game: its current build counts as known, its DB stays as it is.
    [RelayCommand]
    private async Task AcceptDbUpdateAsync(DbUpdateItem? item)
    {
        if (item == null)
            return;
        await RunDbUpdateCheckAsync(["db", "check-updates", "--accept", item.Game], "Keeping the " + item.DisplayName + " DB...");
    }

    // One game: refreshes its DB with the game's own refresh command (which records the build), then checks again.
    [RelayCommand]
    private async Task RefreshDbUpdateAsync(DbUpdateItem? item)
    {
        if (item == null)
            return;
        List<string>? args = DbRefreshArguments(item);
        if (args == null)
        {
            StatusText = T("{0} has no DB refresh: its action list comes with JoyPro updates. Use \"Keep DB\" to hide the notice.", item.DisplayName);
            return;
        }
        await AddHllvOodleConsentAsync(item.Game, args);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false,
            busyLabel: (item.MissingList ? T("Building the {0} command list", item.DisplayName) : T("Refreshing the {0} DB", item.DisplayName)) + "...");
        if (result == null || result.ExitCode != 0)
            return;
        if (!await BuildDcsCleanProfilesIfNewAsync(item.Game, item.MissingList))
            return;
        InvalidateDbCaches();
        RebuildRelationNodeGameOptions();
        await RunDbUpdateCheckAsync(["db", "check-updates"], "Checking games for updates...");
        StatusText = item.DisplayName + (item.MissingList ? " command list built." : T(" DB refreshed."));
    }

    // The refresh the engine names for the game (RefreshCommand), with the paths set in Settings. DCS refreshes headless
    // (evaluates the game's Lua files; DCS is not started).
    internal List<string>? DbRefreshArguments(DbUpdateItem item)
    {
        switch (item.Game)
        {
            case "DCS":
            {
                List<string> args = ["dcs", "refresh", "id"];
                if (!string.IsNullOrWhiteSpace(DcsSavedGamesPath))
                    args.AddRange(["--path", DcsSavedGamesPath.Trim()]);
                if (!string.IsNullOrWhiteSpace(DcsInstallPath))
                    args.AddRange(["--install-path", DcsInstallPath.Trim()]);
                args.AddRange(["--headless-lua", "true", "--launch", "false", "--keep-running", "false", "--ocr", "false"]);
                return args;
            }
            case "IL2Game":
            {
                List<string> args = ["db", "refresh-il2"];
                if (!string.IsNullOrWhiteSpace(ExportIl2Path))
                    args.AddRange(["--path", ExportIl2Path.Trim()]);
                return args;
            }
            case "StarCitizen":
            {
                List<string> args = ["db", "refresh-starcitizen"];
                AddStarCitizenRefreshSource(args);
                return args;
            }
        }
        // "engine <game> refresh-db" for the games of the registry; "update JoyPro" for those without a refresh.
        string[] tokens = item.RefreshCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 3 && tokens[0] == "engine" && tokens[2] == "refresh-db")
        {
            List<string> args = [tokens[1], "refresh-db"];
            // War Thunder: the engine reads the game's packed archives for the full list unless the user turned it off.
            if (string.Equals(item.Game, "WarThunder", StringComparison.OrdinalIgnoreCase) && !ReadWarThunderArchives)
                args.Add("--no-game-archives");
            return args;
        }
        return null;
    }

    // Settings > Games: JoyPro unpacks War Thunder's archives (gui.vromfs.bin, lang.vromfs.bin) to read the full command
    // list. On by default (the user approved it on 2026-10-12); Gaijin's EULA restricts unpacking game files, so it can
    // be turned off, and then JoyPro uses its own list of the War Thunder actions it knows (docs/games/war-thunder.md).
    [ObservableProperty]
    private bool readWarThunderArchives = true;

    partial void OnReadWarThunderArchivesChanged(bool value)
    {
        if (!loadingUiSettings)
            SaveUiSettings();
    }

    // After a DCS command list was built for the first time: its clean profiles too (false when that failed).
    internal async Task<bool> BuildDcsCleanProfilesIfNewAsync(string game, bool newList)
    {
        if (!newList || !string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase))
            return true;
        EngineCommandResult? clean = await RunEngineAsync(DcsCleanRefreshArguments(), includeProfile: false, mutatesProfile: false, busyLabel: N("Building the DCS clean profiles from the game's default binds..."));
        return clean != null && clean.ExitCode == 0;
    }

    // DCS without clean profiles (a fresh install): after the command list, "dcs refresh clean" builds them from the
    // game's own default binds (headless, DCS is not started), so clean and nuke exports work.
    private List<string> DcsCleanRefreshArguments()
    {
        List<string> args = ["dcs", "refresh", "clean"];
        if (!string.IsNullOrWhiteSpace(DcsSavedGamesPath))
            args.AddRange(["--path", DcsSavedGamesPath.Trim()]);
        if (!string.IsNullOrWhiteSpace(DcsInstallPath))
            args.AddRange(["--install-path", DcsInstallPath.Trim()]);
        args.AddRange(["--headless-lua", "true", "--launch", "false", "--keep-running", "false", "--ocr", "false"]);
        return args;
    }

    // The engine's message names its command line ("... refresh the DCS World DB (engine dcs refresh id)."); the banner
    // has a Refresh DB button for it, so the command line is left out.
    internal static string FriendlyDbUpdateMessage(string message)
    {
        string text = System.Text.RegularExpressions.Regex.Replace(message ?? string.Empty, @"\s*\(engine\s[^)]*\)", string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return text.Trim();
    }

    private async Task RunDbUpdateCheckAsync(string[] args, string busyLabel)
    {
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: busyLabel, forceText: false);
        if (result == null || result.ExitCode != 0)
            return;

        DbUpdateMessages.Clear();
        DbUpdateItems.Clear();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            if (doc.RootElement.TryGetProperty("Games", out JsonElement games) && games.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement game in games.EnumerateArray().Where(g => GetJsonString(g, "State") is "game-updated" or "no-catalog"))
                {
                    string details = game.TryGetProperty("Details", out JsonElement detailsEl) && detailsEl.ValueKind == JsonValueKind.Array
                        ? string.Join("; ", detailsEl.EnumerateArray().Select(d => d.GetString()))
                        : string.Empty;
                    string message = FriendlyDbUpdateMessage(GetJsonString(game, "Message")) + (details.Length > 0 ? " [" + details + "]" : string.Empty);
                    DbUpdateMessages.Add(message);
                    string id = GetJsonString(game, "Game");
                    string name = GetJsonString(game, "DisplayName");
                    DbUpdateItem item = new(id, name.Length > 0 ? name : id, message, GetJsonString(game, "RefreshCommand"),
                        GetJsonString(game, "State") == "no-catalog");
                    item.CanRefresh = DbRefreshArguments(item) != null;
                    DbUpdateItems.Add(item);
                }
            }
        }
        catch (JsonException)
        {
        }
        HasDbUpdates = DbUpdateMessages.Count > 0;
        HasMissingCommandLists = DbUpdateItems.Any(u => u.MissingList && u.CanRefresh);
        foreach (GameCardItem card in GameCards)
        {
            DbUpdateItem? update = DbUpdateItems.FirstOrDefault(u => string.Equals(u.Game, card.GameId, System.StringComparison.OrdinalIgnoreCase));
            card.MissingCommandList = update?.MissingList == true;
            card.NeedsDbUpdate = update != null;
        }
    }

    // Some installed games have no command list yet (a fresh install ships none; docs/licensing-audit.md).
    [ObservableProperty]
    private bool hasMissingCommandLists;

    // Builds the command list of every installed game that has none yet, from the game's own files, one after the
    // other (the first-start setup runs it after Done; the Start page notice has a button for it).
    [RelayCommand]
    private async Task BuildMissingCommandListsAsync()
    {
        List<DbUpdateItem> missing = DbUpdateItems.Where(u => u.MissingList && u.CanRefresh).ToList();
        int built = 0;
        List<string> failed = [];
        bool cancelled = false;
        try
        {
            for (int i = 0; i < missing.Count; i++)
            {
                DbUpdateItem item = missing[i];
                List<string>? args = DbRefreshArguments(item);
                if (args == null)
                    continue;
                await AddHllvOodleConsentAsync(item.Game, args);
                // The busy area names the list and how many there are ("Command list 2 of 5: War Thunder").
                busyBatchText = T("Command list {0} of {1}: {2}", (i + 1), missing.Count, item.DisplayName);
                EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: "Building the " + item.DisplayName + " command list from the game...");
                if (LastEngineCallCancelled)
                {
                    cancelled = true;
                    break;
                }
                if (result != null && result.ExitCode == 0 && await BuildDcsCleanProfilesIfNewAsync(item.Game, true))
                    built++;
                else if (LastEngineCallCancelled)
                {
                    cancelled = true;
                    break;
                }
                else
                    failed.Add(item.DisplayName);
            }
        }
        finally
        {
            busyBatchText = string.Empty;
        }
        if (missing.Count == 0)
            return;
        InvalidateDbCaches();
        RebuildRelationNodeGameOptions();
        await RunDbUpdateCheckAsync(["db", "check-updates"], "Checking games for updates...");
        StatusText = T("Command lists built: {0}{1}{2}.", built, (failed.Count > 0 ? T("; failed: {0} (see the log)", string.Join(", ", failed)) : string.Empty), (cancelled ? T("; cancelled, the others were not started") : string.Empty));
    }
}
