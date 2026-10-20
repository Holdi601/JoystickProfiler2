using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Watch;

namespace JoyPro.Gui.ViewModels;

// The game config watcher (docs/sync-watcher.md): when a game changed its controls and has closed again, JoyPro asks
// whether to take the changes into the profile (engine sync diff / apply / accept). Only games JoyPro exported to (or
// whose state it accepted) are watched, only while "Watch game configs" is on (the master switch), and only the games
// ticked on the Start page's game cards ("Watch for changes", GameWatchChoices).
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<GameSyncChangeItem> GameSyncChanges { get; } = [];

    [ObservableProperty]
    private bool watchGameConfigs = true;

    [ObservableProperty]
    private bool hasGameSyncChanges;

    [ObservableProperty]
    private string gameSyncTitle = string.Empty;

    [ObservableProperty]
    private string gameSyncStatus = string.Empty;

    [ObservableProperty]
    private string gameWatchStatus = "Not started.";

    // The dialog came up (title, text): the window shows a notice and, when it is not the active window (the game just
    // closed, JoyPro is behind other windows or minimized), flashes its taskbar button without taking the focus.
    public event Action<string, string>? GameChangesOffered;

    public static string GameChangesNoticeTitle => T("A game changed its controls");

    private GameConfigWatcher? gameConfigWatcher;
    // Watch key ("<game>|<config path>": a game may have several configs, e.g. one exported to a custom path) -> location.
    private readonly Dictionary<string, (string GameId, string ConfigPath)> watchedConfigs = new(StringComparer.OrdinalIgnoreCase);
    // Configs waiting for a check while another one's changes are shown (at most one entry per watch key).
    private readonly List<string> pendingGameSyncChecks = [];
    private bool gameSyncQueueRunning;
    private string gameSyncWatchKey = string.Empty;

    partial void OnWatchGameConfigsChanged(bool value)
    {
        SaveUiSettings();
        OnPropertyChanged(nameof(GameWatchHint));
        if (gameSyncWatcherStarted)
            _ = RefreshGameConfigWatcherAsync(checkNow: value);
    }

    private bool gameSyncWatcherStarted;
    private string lastUnwatchedGames = string.Empty;

    // How the watcher's thread hands a change to the UI thread (tests: run it at once, there is no UI thread).
    internal Action<Action> GameSyncPost { get; set; } = action => Dispatcher.UIThread.Post(action);

    // Makes the watcher (tests: other quiet times).
    internal Func<GameConfigWatcher> GameConfigWatcherFactory { get; set; } = () => new GameConfigWatcher();

    // Started once the window is open (RunStartupChecksAsync); also offers what games changed while JoyPro was closed.
    // Once per run. While the GUI runs it owns the watcher: the tray host (JoyPro.Tray) pauses until the GUI ends.
    public async Task StartGameConfigWatcherAsync()
    {
        if (gameSyncWatcherStarted)
            return;
        gameSyncWatcherStarted = true;
        await RefreshGameConfigWatcherAsync(checkNow: true);
    }

    // Asks the engine which configs JoyPro manages and watches those of installed games it exported to.
    private async Task RefreshGameConfigWatcherAsync(bool checkNow)
    {
        if (!WatchGameConfigs)
        {
            StopGameConfigWatcher();
            GameWatchStatus = T("Off: game configs are not watched.");
            return;
        }

        EngineCommandResult? result = await RunEngineAsync(["sync", "status"], includeProfile: false, mutatesProfile: false,
            busyLabel: N("Checking game configs..."), forceText: false, background: true);
        if (result == null || result.ExitCode != 0 || !WatchGameConfigs)
            return;
        // The Start page's "Recent activity" (last export / import per game).
        UpdateRecentActivity(result.StandardOutput);

        // Disabled, untracked and missing games are never watched (the parser leaves them out).
        List<SyncStatusEntry>? entries = SyncOutput.ParseStatus(result.StandardOutput);
        if (entries == null)
            return;
        // Diagnostics: why an installed game is not watched (untracked: neither exported nor imported yet).
        string unwatched = string.Join("; ", SyncOutput.DescribeUnwatched(result.StandardOutput));
        if (unwatched != lastUnwatchedGames)
        {
            lastUnwatchedGames = unwatched;
            if (unwatched.Length > 0)
                JoyPro.Logging.JoyProLog.Info("Watcher", "Installed games that are not watched: " + unwatched);
        }
        syncStatusEntries = entries;
        // Changed while JoyPro was closed: checked once the game is not running (the watcher waits for it).
        ApplyGameWatchChoices(checkNow ? entries.Where(e => e.Changed).Select(e => e.WatchKey).ToList() : []);
    }

    // The configs of the last sync status that the user wants watched (the Start page's ticks) go to the watcher; an
    // unticked game's file watchers are released at once (GameConfigWatcher.Update), a ticked one gets them. No engine
    // call: the ticks take effect live.
    // "IL-2 Sturmovik: Great Battles", not the engine's id "IL2Game".
    private string WatchedGameName(string gameId)
    {
        string name = GameInstalls.FirstOrDefault(g => string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase))?.Name
                      ?? GameCards.FirstOrDefault(c => string.Equals(c.GameId, gameId, StringComparison.OrdinalIgnoreCase))?.Name
                      ?? string.Empty;
        return name.Length > 0 ? name : DisplayText.Game(gameId);
    }

    private void ApplyGameWatchChoices(IReadOnlyCollection<string> checkKeys)
    {
        if (!WatchGameConfigs || !gameSyncWatcherStarted)
            return;
        List<SyncStatusEntry> entries = gameWatchChoices.Filter(syncStatusEntries);
        watchedConfigs.Clear();
        foreach (SyncStatusEntry entry in entries)
            watchedConfigs[entry.WatchKey] = (entry.GameId, entry.ConfigPath);
        pendingGameSyncChecks.RemoveAll(key => !watchedConfigs.ContainsKey(key));
        if (gameConfigWatcher == null)
        {
            gameConfigWatcher = GameConfigWatcherFactory();
            gameConfigWatcher.ConfigChanged += OnGameConfigChanged;
        }
        gameConfigWatcher.Update(entries.Select(e => e.Spec));

        List<string> unticked = syncStatusEntries.Select(e => e.GameId).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => !gameWatchChoices.IsWatched(id, installed: true)).ToList();
        string notWatched = unticked.Count == 0 ? string.Empty : T(" Not watched (unticked on the Start page): {0}.", string.Join(", ", unticked.Select(WatchedGameName)));
        GameWatchStatus = (entries.Count == 0
            ? T("On: no game to watch yet (JoyPro watches the games it exported to).")
            : T("On: watching {0}.", string.Join(", ", entries.Select(e => e.GameId).Distinct(StringComparer.OrdinalIgnoreCase).Select(WatchedGameName)))) + notWatched;

        foreach (string key in checkKeys.Where(watchedConfigs.ContainsKey))
            gameConfigWatcher.Trigger(key);
    }

    public void StopGameConfigWatcher()
    {
        if (gameConfigWatcher == null)
            return;
        gameConfigWatcher.ConfigChanged -= OnGameConfigChanged;
        gameConfigWatcher.Dispose();
        gameConfigWatcher = null;
    }

    // What the watcher holds right now (tests: only ticked games get file watchers, unticked ones none).
    public IReadOnlyList<string> WatchedConfigKeys => gameConfigWatcher?.WatchedGames ?? [];

    public int GameConfigWatcherHandles => gameConfigWatcher?.HandleCount ?? 0;

    // From the watcher's thread: the game changed its config and is closed.
    private void OnGameConfigChanged(string watchKey)
    {
        GameSyncPost(() =>
        {
            // A game unticked meanwhile: its late event is dropped.
            if (watchedConfigs.ContainsKey(watchKey))
                QueueGameSyncCheck(watchKey);
            else
                JoyPro.Logging.JoyProLog.Info("Watcher", watchKey + ": change dropped (no longer watched: unticked or watching turned off)");
        });
    }

    // ---- Per game: the "Watch for changes" ticks on the Start page's game cards ---------------------------------------

    // The user's explicit ticks (ui-settings.json "watchedGames", read by the tray host too).
    private GameWatchChoices gameWatchChoices = GameWatchChoices.Empty;

    // The configs of the last engine sync status that can be watched, before the ticks are applied.
    private List<SyncStatusEntry> syncStatusEntries = [];

    private bool settingAllGameWatches;

    public GameWatchChoices GameWatchChoices => gameWatchChoices;

    // The section header's "Watch all": ticked when every installed game is watched, empty when none, mixed otherwise.
    public bool? WatchAllGames
    {
        get
        {
            List<GameCardItem> installed = GameCards.Where(c => c.CanWatch).ToList();
            if (installed.Count == 0 || installed.All(c => !c.Watch))
                return false;
            return installed.All(c => c.Watch) ? true : null;
        }
        set
        {
            if (value == null)
                return;
            List<string> ticked = [];
            settingAllGameWatches = true;
            try
            {
                foreach (GameCardItem card in GameCards.Where(c => c.CanWatch))
                {
                    if (card.Watch != value.Value && value.Value)
                        ticked.Add(card.GameId);
                    card.Watch = value.Value;
                    gameWatchChoices = gameWatchChoices.With(card.GameId, value.Value);
                }
            }
            finally
            {
                settingAllGameWatches = false;
            }
            GameWatchChoicesChanged(ticked);
        }
    }

    public bool HasWatchableGameCards => GameCards.Any(c => c.CanWatch);

    public string GameWatchHint => WatchGameConfigs
        ? T("Ticked games are watched: change binds in a game and JoyPro offers to take them over after the game closed.")
        : T("Watching is off for all games (Settings > Games > Watch game configs); the ticks apply once it is on again.");

    private void OnGameCardWatchChanged(GameCardItem card)
    {
        if (settingAllGameWatches)
            return;
        gameWatchChoices = gameWatchChoices.With(card.GameId, card.Watch);
        GameWatchChoicesChanged(card.Watch ? [card.GameId] : []);
    }

    // Saved at once (the tray host reads the file when the GUI closed) and applied to the running watcher; a game ticked
    // again is checked for what it changed while it was not watched.
    private void GameWatchChoicesChanged(IReadOnlyCollection<string> tickedGames)
    {
        SaveUiSettings();
        OnPropertyChanged(nameof(WatchAllGames));
        List<string> recheck = syncStatusEntries.Where(e => tickedGames.Contains(e.GameId, StringComparer.OrdinalIgnoreCase)).Select(e => e.WatchKey).ToList();
        ApplyGameWatchChoices(recheck);
    }

    private void QueueGameSyncCheck(string watchKey)
    {
        if (!WatchGameConfigs || string.IsNullOrWhiteSpace(watchKey))
        {
            JoyPro.Logging.JoyProLog.Info("Watcher", watchKey + ": change not checked (watching game configs is off)");
            return;
        }
        if (!pendingGameSyncChecks.Contains(watchKey, StringComparer.OrdinalIgnoreCase) &&
            !(HasGameSyncChanges && string.Equals(gameSyncWatchKey, watchKey, StringComparison.OrdinalIgnoreCase)))
            pendingGameSyncChecks.Add(watchKey);
        if (!gameSyncQueueRunning)
            _ = ProcessGameSyncQueueAsync();
    }

    private async Task ProcessGameSyncQueueAsync()
    {
        gameSyncQueueRunning = true;
        try
        {
            // One game's changes at a time; the next one is checked when the panel closes.
            while (pendingGameSyncChecks.Count > 0 && !HasGameSyncChanges)
            {
                string watchKey = pendingGameSyncChecks[0];
                pendingGameSyncChecks.RemoveAt(0);
                await CheckGameChangesAsync(watchKey);
            }
        }
        finally
        {
            gameSyncQueueRunning = false;
        }
    }

    private List<string> SyncPathArgs(string watchKey)
    {
        (string gameId, string path) = GameOfWatchKey(watchKey);
        List<string> args = ["--game", gameId];
        if (!string.IsNullOrWhiteSpace(path))
        {
            args.Add("--path");
            args.Add(path);
        }
        return args;
    }

    private (string GameId, string ConfigPath) GameOfWatchKey(string watchKey)
    {
        if (watchedConfigs.TryGetValue(watchKey, out (string GameId, string ConfigPath) config))
            return config;
        int bar = watchKey.IndexOf('|');
        return bar < 0 ? (watchKey, string.Empty) : (watchKey[..bar], watchKey[(bar + 1)..]);
    }

    private async Task CheckGameChangesAsync(string watchKey)
    {
        string gameId = GameOfWatchKey(watchKey).GameId;
        // Without a profile there is nothing to take the changes into; the config stays "changed" for later.
        if (string.IsNullOrWhiteSpace(ProfilePath) || !File.Exists(ProfilePath))
        {
            JoyPro.Logging.JoyProLog.Info("Watcher", gameId + ": no profile is loaded; the change waits until one is");
            return;
        }

        EngineCommandResult? status = await RunEngineAsync(["sync", "status", .. SyncPathArgs(watchKey)], includeProfile: false, mutatesProfile: false,
            busyLabel: "Checking " + gameId + " config...", forceText: false, background: true);
        if (status == null || status.ExitCode != 0)
        {
            JoyPro.Logging.JoyProLog.Warn("Watcher", gameId + ": not checked: sync status failed (" + (status == null ? "the engine did not run" : "exit code " + status.ExitCode) + ")");
            return;
        }
        if (!StatusSaysChanged(status.StandardOutput))
        {
            JoyPro.Logging.JoyProLog.Info("Watcher", gameId + ": the config was written, its content is unchanged; nothing to ask");
            return;
        }

        EngineCommandResult? diff = await RunEngineAsync(["sync", "diff", .. SyncPathArgs(watchKey)], includeProfile: true, mutatesProfile: false,
            busyLabel: "Reading what " + gameId + " changed...", forceText: false, background: true);
        if (diff == null || diff.ExitCode != 0)
        {
            JoyPro.Logging.JoyProLog.Warn("Watcher", gameId + ": changed, not compared: sync diff failed (" + (diff == null ? "the engine did not run" : "exit code " + diff.ExitCode) + "); nothing asked");
            return;
        }

        List<GameSyncChangeItem> items = [];
        string displayName = gameId;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(diff.StandardOutput));
            displayName = ReadJsonString(doc.RootElement, "DisplayName") is { Length: > 0 } name ? name : gameId;
            if (doc.RootElement.TryGetProperty("Changes", out JsonElement changes) && changes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement change in changes.EnumerateArray())
                {
                    items.Add(new GameSyncChangeItem(ReadJsonString(change, "Id"), ReadJsonString(change, "Kind"), ReadJsonString(change, "Description"),
                        ReadStringArray(change, "Candidates"), ReadJsonString(change, "Suggested"), ReadJsonString(change, "NewRelationName")));
                }
            }
        }
        catch (JsonException ex)
        {
            JoyPro.Logging.JoyProLog.Warn("Watcher", gameId + ": the diff could not be read (" + ex.Message + "); nothing asked");
            return;
        }

        if (items.Count == 0)
        {
            // Only changes JoyPro does not manage (or only formatting): nothing to ask, the state is known now.
            JoyPro.Logging.JoyProLog.Info("Watcher", gameId + ": the config changed, but nothing JoyPro manages; accepted without asking (" + SyncOutput.DiffCounts(diff.StandardOutput) + ")");
            await RunEngineAsync(["sync", "accept", .. SyncPathArgs(watchKey)], includeProfile: false, mutatesProfile: false,
                busyLabel: "Marking the " + gameId + " config as known...", forceText: false, background: true);
            return;
        }

        GameSyncChanges.Clear();
        foreach (GameSyncChangeItem item in items)
            GameSyncChanges.Add(item);
        gameSyncWatchKey = watchKey;
        GameSyncTitle = T("{0}: {1}{2} made in the game", displayName, items.Count, (items.Count == 1 ? " change" : " changes"));
        GameSyncStatus = string.Empty;
        HasGameSyncChanges = true;
        JoyPro.Logging.JoyProLog.Info("Watcher", gameId + ": offering " + items.Count + " change(s) in the dialog: " +
                                                 string.Join(" | ", items.Select(i => i.Kind + " " + i.Description)));
        JoyPro.Logging.JoyProLog.Info("Watcher", gameId + ": the user is asked (notice in the window" + (GameChangesOffered == null ? ", no notice handler" : string.Empty) + ")");
        GameChangesOffered?.Invoke(GameChangesNoticeTitle, T("{0} changed controls JoyPro manages. Review them in JoyPro.", displayName));
    }

    private static bool StatusSaysChanged(string output) => SyncOutput.StatusSaysChanged(output);

    // Take over: the ticked changes go into the chosen relations (sync apply); unticked ones count as ignored.
    [RelayCommand]
    private async Task TakeOverGameChangesAsync()
    {
        if (!HasGameSyncChanges || GameSyncChanges.Count == 0 || string.IsNullOrWhiteSpace(gameSyncWatchKey))
            return;
        List<Dictionary<string, string>> choices = [];
        foreach (GameSyncChangeItem item in GameSyncChanges)
        {
            (string target, string name) = item.Choice();
            choices.Add(new Dictionary<string, string> { ["Id"] = item.Id, ["Target"] = target, ["Name"] = name });
        }
        string choicesFile = Path.Combine(Path.GetTempPath(), "joypro-sync-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(choicesFile, JsonSerializer.Serialize(new Dictionary<string, object> { ["Choices"] = choices }));
            EngineCommandResult? result = await RunEngineAsync(["sync", "apply", .. SyncPathArgs(gameSyncWatchKey), "--choices", choicesFile],
                includeProfile: true, mutatesProfile: true, busyLabel: N("Taking over the game's changes..."), forceText: false);
            if (result == null || result.ExitCode != 0)
            {
                GameSyncStatus = result == null ? T("The engine could not be started.") : BuildFailureStatus(result);
                return;
            }
        }
        finally
        {
            try { File.Delete(choicesFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        JoyPro.Logging.JoyProLog.Info("Watcher", GameOfWatchKey(gameSyncWatchKey).GameId + ": took over " + choices.Count(c => c["Target"] != "ignore") + " of " + choices.Count + " change(s)");
        CloseGameSyncPanel();
        await RefreshRelationsAsync();
        await RefreshBindsAsync();
        StatusText = T("Took over the game's changes.");
        _ = ProcessGameSyncQueueAsync();
    }

    // Not now: the changes stay; JoyPro asks again after the next change or start.
    [RelayCommand]
    private void PostponeGameChanges()
    {
        if (!HasGameSyncChanges)
            return;
        CloseGameSyncPanel();
        _ = ProcessGameSyncQueueAsync();
    }

    // Ignore these: the game's state counts as known (sync accept); the next export writes the profile over it.
    [RelayCommand]
    private async Task IgnoreGameChangesAsync()
    {
        if (!HasGameSyncChanges || string.IsNullOrWhiteSpace(gameSyncWatchKey))
            return;
        await RunEngineAsync(["sync", "accept", .. SyncPathArgs(gameSyncWatchKey)], includeProfile: false, mutatesProfile: false,
            busyLabel: N("Ignoring the game's changes..."), forceText: false);
        JoyPro.Logging.JoyProLog.Info("Watcher", GameOfWatchKey(gameSyncWatchKey).GameId + ": ignored " + GameSyncChanges.Count + " change(s)");
        CloseGameSyncPanel();
        _ = ProcessGameSyncQueueAsync();
    }

    private void CloseGameSyncPanel()
    {
        HasGameSyncChanges = false;
        GameSyncChanges.Clear();
        gameSyncWatchKey = string.Empty;
    }

    // After an export or an import the game may be watched for the first time (its fingerprint was just recorded: an
    // import records a game that had no record yet). Up to 2.2.8 only an export refreshed the watch list, so a game only
    // imported was watched from the next start of JoyPro on.
    internal void OnEngineCommandFinished(IReadOnlyList<string> commandArgs, int exitCode)
    {
        if (exitCode != 0 || !gameSyncWatcherStarted || !WatchGameConfigs || commandArgs.Count < 2)
            return;
        if ((string.Equals(commandArgs[1], "export", StringComparison.OrdinalIgnoreCase) || string.Equals(commandArgs[1], "import", StringComparison.OrdinalIgnoreCase)) &&
            !commandArgs.Contains("--dry-run", StringComparer.OrdinalIgnoreCase))
            GameSyncPost(() => _ = RefreshGameConfigWatcherAsync(checkNow: false));
    }
}
