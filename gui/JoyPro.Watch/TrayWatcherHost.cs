using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace JoyPro.Watch;

// What the tray icon shows; the tray host's UI (WinForms NotifyIcon) only displays it.
public sealed record TrayViewState(
    string ToolTip,
    string? ReviewText,
    string WatchingText,
    bool WatchingChecked,
    bool WatchingEnabled);

public interface ITrayView
{
    void Show(TrayViewState state);

    // A notification that never takes the focus (a balloon); a click on it means Review.
    void Notify(string title, string text);

    // Ends the tray host.
    void Close();
}

// Starts the full GUI (which hands a --review on to a JoyPro2 that already runs).
public interface IJoyProLauncher
{
    void OpenGui(IReadOnlyList<string> arguments);
}

public sealed class ProcessJoyProLauncher : IJoyProLauncher
{
    private readonly string guiExe;

    public ProcessJoyProLauncher(string guiExe)
    {
        this.guiExe = guiExe;
    }

    public void OpenGui(IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start = new(guiExe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(guiExe) ?? string.Empty
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        try
        {
            using Process? process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // The GUI could not be started (moved or deleted): nothing to show.
        }
    }
}

// A game whose changes wait for a review in the GUI.
public sealed record PendingReview(string WatchKey, string DisplayName, int Changes);

// The tray host's logic (docs/sync-watcher.md#tray-host), free of any UI framework so it is tested with fakes:
// - watches the configs engine sync status lists (GameConfigWatcher) while the GUI is not running, only of the games
//   ticked on the Start page (TraySettings.WatchChoices, read again whenever the GUI closed);
// - after a change (the game closed): sync status, then sync diff against the last profile; no change JoyPro
//   manages: sync accept (silently, as the GUI does); otherwise a notification and a "Review" menu entry that starts
//   the GUI with --review <watch key>;
// - pauses while the GUI runs (the GUI owns the watcher then; no double prompts) and resumes when it exits;
// - ends itself when the GUI exits with "Close to tray" off.
// Every call comes in on one thread (post), the WinForms UI thread in the real tray host.
public sealed class TrayWatcherHost : IDisposable
{
    public const string NotificationTitle = "A game changed its controls";

    private readonly IWatchEngine engine;
    private readonly ITraySettingsStore settingsStore;
    private readonly IGuiPresence gui;
    private readonly IJoyProLauncher launcher;
    private readonly ITrayView view;
    private readonly Func<IGameWatcher> watcherFactory;
    private readonly Action<Action> post;
    private readonly Action trimMemory;
    private readonly Func<string, bool> fileExists;
    private readonly List<string> queue = [];
    private readonly List<PendingReview> pending = [];
    private readonly HashSet<string> watched = new(StringComparer.OrdinalIgnoreCase);
    private TraySettings settings = new(true, false, false, string.Empty);
    private IGameWatcher? watcher;
    private bool queueRunning;
    private bool disposed;
    private int refreshGeneration;
    private string lastUnwatched = string.Empty;

    public TrayWatcherHost(
        IWatchEngine engine,
        ITraySettingsStore settingsStore,
        IGuiPresence gui,
        IJoyProLauncher launcher,
        ITrayView view,
        Func<IGameWatcher> watcherFactory,
        Action<Action> post,
        Action? trimMemory = null,
        Func<string, bool>? fileExists = null)
    {
        this.engine = engine;
        this.settingsStore = settingsStore;
        this.gui = gui;
        this.launcher = launcher;
        this.view = view;
        this.watcherFactory = watcherFactory;
        this.post = post;
        this.trimMemory = trimMemory ?? (() => { });
        this.fileExists = fileExists ?? File.Exists;
    }

    public bool Paused { get; private set; }

    public bool Watching => settings.WatchGameConfigs;

    public IReadOnlyList<PendingReview> Pending => pending;

    public IReadOnlyCollection<string> WatchedKeys => watched;

    public bool HasWatcher => watcher != null;

    public TrayViewState State { get; private set; } = new(string.Empty, null, string.Empty, false, false);

    public async Task StartAsync()
    {
        settings = settingsStore.Load();
        gui.Started += OnGuiStartedEvent;
        gui.Exited += OnGuiExitedEvent;
        gui.Start();
        if (gui.IsRunning)
            Pause();
        UpdateView();
        if (!Paused && Watching)
            await RefreshAsync(checkNow: true);
        trimMemory();
    }

    // Menu "Open JoyPro2" and a click on the icon.
    public void Open() => launcher.OpenGui([]);

    // The notification or menu "Review ...": the GUI checks these configs at once (and loads the last profile).
    public void Review()
    {
        if (pending.Count == 0)
        {
            Open();
            return;
        }
        List<string> args = [];
        foreach (PendingReview review in pending)
        {
            args.Add("--review");
            args.Add(review.WatchKey);
        }
        launcher.OpenGui(args);
    }

    // Menu "Watching game configs" (the same setting as the GUI's; only while the GUI is closed).
    public void ToggleWatching()
    {
        if (Paused || disposed)
            return;
        bool value = !settings.WatchGameConfigs;
        settings = settings with { WatchGameConfigs = value };
        try
        {
            settingsStore.SaveWatchGameConfigs(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept for this run; the GUI's settings stay as they were.
        }
        if (value)
        {
            UpdateView();
            _ = RefreshAsync(checkNow: true);
        }
        else
        {
            StopWatcher();
            pending.Clear();
            queue.Clear();
            UpdateView();
        }
    }

    public void Exit() => view.Close();

    // Asks the engine which configs to watch and (re)arms the watcher; checkNow offers what changed meanwhile.
    public async Task RefreshAsync(bool checkNow)
    {
        if (disposed || Paused || !Watching)
            return;
        int generation = ++refreshGeneration;
        EngineRun run = await engine.RunAsync([.. SyncOutput.CommonArguments, "sync", "status"]);
        if (disposed || Paused || !Watching || generation != refreshGeneration || run.ExitCode != 0)
            return;
        List<SyncStatusEntry>? parsed = SyncOutput.ParseStatus(run.StandardOutput);
        if (parsed == null)
            return;
        string unwatched = string.Join("; ", SyncOutput.DescribeUnwatched(run.StandardOutput));
        if (unwatched != lastUnwatched)
        {
            lastUnwatched = unwatched;
            if (unwatched.Length > 0)
                JoyPro.Logging.JoyProLog.Info("Watcher", "Installed games that are not watched: " + unwatched);
        }
        // Only the games ticked on the Start page (settings reloaded at start and whenever the GUI closed).
        List<SyncStatusEntry> entries = settings.WatchChoices.Filter(parsed);
        if (watcher == null)
        {
            watcher = watcherFactory();
            watcher.ConfigChanged += OnConfigChangedEvent;
        }
        watcher.Update(entries.Select(e => e.Spec));
        watched.Clear();
        foreach (SyncStatusEntry entry in entries)
            watched.Add(entry.WatchKey);
        UpdateView();
        if (checkNow)
        {
            foreach (SyncStatusEntry entry in entries.Where(e => e.Changed))
                watcher.Trigger(entry.WatchKey);
        }
        trimMemory();
    }

    // From the watcher (any thread): the game changed its config and is closed.
    private void OnConfigChangedEvent(string watchKey) => post(() => Enqueue(watchKey));

    private void OnGuiStartedEvent() => post(Pause);

    private void OnGuiExitedEvent() => post(Resume);

    internal void Enqueue(string watchKey)
    {
        if (disposed || Paused || !Watching || string.IsNullOrWhiteSpace(watchKey))
            return;
        // A game the user unticked meanwhile (its watcher is already gone; a late event is dropped).
        if (!settings.WatchChoices.IsWatched(SyncOutput.SplitWatchKey(watchKey).GameId, installed: true))
            return;
        if (!queue.Contains(watchKey, StringComparer.OrdinalIgnoreCase))
            queue.Add(watchKey);
        if (!queueRunning)
            _ = ProcessQueueAsync();
    }

    private async Task ProcessQueueAsync()
    {
        queueRunning = true;
        try
        {
            while (queue.Count > 0 && !disposed && !Paused)
            {
                string key = queue[0];
                queue.RemoveAt(0);
                await CheckAsync(key);
            }
        }
        finally
        {
            queueRunning = false;
            trimMemory();
        }
    }

    private async Task CheckAsync(string watchKey)
    {
        List<string> gameArgs = SyncOutput.GameArguments(watchKey);
        EngineRun status = await engine.RunAsync([.. SyncOutput.CommonArguments, "sync", "status", .. gameArgs]);
        if (Paused || status.ExitCode != 0)
        {
            JoyPro.Logging.JoyProLog.Info("Watcher", watchKey + ": not checked (" + (Paused ? "the tray host is paused" : "sync status failed, exit code " + status.ExitCode) + ")");
            return;
        }
        if (!SyncOutput.StatusSaysChanged(status.StandardOutput))
        {
            JoyPro.Logging.JoyProLog.Info("Watcher", watchKey + ": the config was written, its content is unchanged; nothing to ask");
            return;
        }
        // Without a profile there is nothing to take the changes into (as in the GUI): the config stays "changed".
        string profile = settings.LastProfilePath;
        if (string.IsNullOrWhiteSpace(profile) || !fileExists(profile))
        {
            JoyPro.Logging.JoyProLog.Info("Watcher", watchKey + ": changed, but no profile to compare with; the change waits");
            return;
        }
        EngineRun diff = await engine.RunAsync(["--profile", profile, .. SyncOutput.CommonArguments, "sync", "diff", .. gameArgs]);
        if (Paused || diff.ExitCode != 0)
        {
            JoyPro.Logging.JoyProLog.Info("Watcher", watchKey + ": changed, not compared (" + (Paused ? "the tray host is paused" : "sync diff failed, exit code " + diff.ExitCode) + ")");
            return;
        }
        (string DisplayName, int Changes)? summary = SyncOutput.ParseDiffSummary(diff.StandardOutput);
        if (summary == null)
        {
            JoyPro.Logging.JoyProLog.Warn("Watcher", watchKey + ": the diff could not be read; nothing asked");
            return;
        }
        if (summary.Value.Changes == 0)
        {
            // Only changes JoyPro does not manage (or formatting): the state is known now, nothing to ask.
            JoyPro.Logging.JoyProLog.Info("Watcher", watchKey + ": the config changed, but nothing JoyPro manages; accepted without asking (" + SyncOutput.DiffCounts(diff.StandardOutput) + ")");
            await engine.RunAsync([.. SyncOutput.CommonArguments, "sync", "accept", .. gameArgs]);
            return;
        }
        string name = summary.Value.DisplayName.Length > 0 ? summary.Value.DisplayName : SyncOutput.SplitWatchKey(watchKey).GameId;
        pending.RemoveAll(p => string.Equals(p.WatchKey, watchKey, StringComparison.OrdinalIgnoreCase));
        pending.Add(new PendingReview(watchKey, name, summary.Value.Changes));
        UpdateView();
        JoyPro.Logging.JoyProLog.Info("Watcher", watchKey + ": " + summary.Value.Changes + " change(s) JoyPro manages; notification shown");
        view.Notify(WatchText.T(NotificationTitle), NotificationText(name, summary.Value.Changes));
    }

    public static string NotificationText(string displayName, int changes) =>
        changes == 1
            ? WatchText.T("{0} changed {1} control JoyPro manages. Click here to take it over or ignore it in JoyPro2.", displayName, changes)
            : WatchText.T("{0} changed {1} controls JoyPro manages. Click here to take them over or ignore them in JoyPro2.", displayName, changes);

    // The GUI started: it watches the games itself (and offers what changed); the tray only keeps its icon.
    private void Pause()
    {
        if (disposed)
            return;
        JoyPro.Logging.JoyProLog.Info("Tray", "JoyPro2 started: it watches the games itself, the tray host pauses");
        Paused = true;
        refreshGeneration++;
        StopWatcher();
        queue.Clear();
        pending.Clear();
        UpdateView();
        trimMemory();
    }

    // The GUI ended: its settings may have changed. Watch again, or end when "Close to tray" is off (closing JoyPro2
    // then ends it completely). Changes the user postponed in the GUI ("Not now") are not offered again until the next
    // change.
    private void Resume()
    {
        if (disposed || !Paused || gui.IsRunning)
            return;
        JoyPro.Logging.JoyProLog.Info("Tray", "JoyPro2 ended: the tray host watches again (or ends without Close to tray)");
        Paused = false;
        settings = settingsStore.Load();
        if (!settings.CloseToTray)
        {
            view.Close();
            return;
        }
        UpdateView();
        if (Watching)
            _ = RefreshAsync(checkNow: false);
    }

    private void StopWatcher()
    {
        if (watcher != null)
        {
            watcher.ConfigChanged -= OnConfigChangedEvent;
            watcher.Dispose();
            watcher = null;
        }
        watched.Clear();
    }

    private void UpdateView()
    {
        if (disposed)
            return;
        string tip;
        if (Paused)
            tip = WatchText.T("JoyPro2 is open and watches the games itself");
        else if (pending.Count > 0)
            tip = pending.Count == 1
                ? WatchText.T("JoyPro2: {0} changed controls - click to review", pending[0].DisplayName)
                : WatchText.T("JoyPro2: {0} games changed controls - click to review", pending.Count);
        else if (!Watching)
            tip = WatchText.T("JoyPro2: not watching game configs");
        else
            tip = watched.Count == 0 ? WatchText.T("JoyPro2: no game to watch yet")
                : watched.Count == 1 ? WatchText.T("JoyPro2: watching {0} game config", watched.Count)
                : WatchText.T("JoyPro2: watching {0} game configs", watched.Count);
        if (tip.Length > 127)
            tip = tip[..127];
        string? review = pending.Count switch
        {
            0 => null,
            1 => WatchText.T("Review the changes made in {0} ({1})", pending[0].DisplayName, pending[0].Changes),
            _ => WatchText.T("Review the changes made in {0} games", pending.Count)
        };
        string watchingText = Paused ? WatchText.T("Watching game configs (JoyPro2 is open: set it there)") : WatchText.T("Watching game configs");
        State = new TrayViewState(tip, review, watchingText, Watching, !Paused);
        view.Show(State);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        StopWatcher();
        disposed = true;
        gui.Started -= OnGuiStartedEvent;
        gui.Exited -= OnGuiExitedEvent;
        gui.Dispose();
        queue.Clear();
        pending.Clear();
    }
}
