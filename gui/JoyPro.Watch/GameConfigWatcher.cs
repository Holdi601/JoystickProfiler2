using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace JoyPro.Watch;

// A folder (and the file names in it) a game keeps its controls in, as engine sync status lists it.
public sealed record GameWatchFolder(string Directory, IReadOnlyList<string> Filters, bool IncludeSubdirectories);

// What to watch for one game: its config files or registry key, and the processes that write them on exit (by name, or
// any exe under one of ProcessFolders: image path prefixes). SettleSeconds: how long the config must be quiet before it
// is checked (also after the game closed), when the game needs longer than the watcher's default (0).
public sealed record GameWatchSpec(string GameId, IReadOnlyList<GameWatchFolder> Folders, string RegistryKey, IReadOnlyList<string> ProcessNames,
                                   IReadOnlyList<string>? ProcessFolders = null, int SettleSeconds = 0)
{
    public IReadOnlyList<string> ProcessFolderList => ProcessFolders ?? [];
}

// Tells the GUI when a game changed its controls and has closed again ("after the game has closed").
// Event driven only: a FileSystemWatcher per config folder (only the managed file names, LastWrite|Size|FileName) or a
// registry change notification (RegNotifyChangeKeyValue on a thread-pool wait), a one-shot debounce timer per game that
// only runs after a change, and Process.Exited while the game is still running (only a game whose exit Windows does not
// let JoyPro await is looked for again every 10 s, after a change, until it closed). Nothing polls; when idle the watcher
// costs no CPU. Every watcher, timer, wait and process object is disposed and every event unsubscribed on Update and
// Dispose; the only collections are one entry per watched game.
[SupportedOSPlatform("windows")]
public sealed class GameConfigWatcher : IGameWatcher
{
    private readonly object gate = new();
    private readonly Dictionary<string, GameWatch> games = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan debounce;
    private readonly TimeSpan afterExit;
    private bool disposed;

    public GameConfigWatcher(TimeSpan? debounce = null, TimeSpan? afterExit = null)
    {
        this.debounce = debounce ?? TimeSpan.FromSeconds(2);
        this.afterExit = afterExit ?? TimeSpan.FromSeconds(3);
    }

    // The game id, raised on a thread-pool thread once the game's config changed and none of its processes runs.
    public event Action<string>? ConfigChanged;

    public IReadOnlyList<string> WatchedGames
    {
        get
        {
            lock (gate)
                return games.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    // File and registry watchers, timers and process waits held right now (the resource test checks it stays flat).
    public int HandleCount
    {
        get
        {
            lock (gate)
                return games.Values.Sum(g => g.HandleCount);
        }
    }

    // Replaces what is watched. A game whose spec did not change keeps its watchers (no churn on refresh).
    public void Update(IEnumerable<GameWatchSpec> specs)
    {
        List<GameWatch> stale = [];
        List<string> added = [];
        lock (gate)
        {
            if (disposed)
                return;
            Dictionary<string, GameWatchSpec> wanted = new(StringComparer.OrdinalIgnoreCase);
            foreach (GameWatchSpec spec in specs ?? [])
            {
                if (!string.IsNullOrWhiteSpace(spec.GameId))
                    wanted[spec.GameId] = spec;
            }
            foreach (string id in games.Keys.ToList())
            {
                if (!wanted.TryGetValue(id, out GameWatchSpec? spec) || !SameSpec(games[id].Spec, spec))
                {
                    stale.Add(games[id]);
                    games.Remove(id);
                }
            }
            foreach (GameWatchSpec spec in wanted.Values)
            {
                if (!games.ContainsKey(spec.GameId))
                {
                    games[spec.GameId] = new GameWatch(this, spec);
                    added.Add(spec.GameId + " (" + (!string.IsNullOrEmpty(spec.RegistryKey) ? spec.RegistryKey : string.Join("; ", spec.Folders.Select(f => f.Directory + "\\" + string.Join("|", f.Filters) + (f.IncludeSubdirectories ? " with subfolders" : string.Empty) +
                                                                                                                                                (Directory.Exists(f.Directory) ? string.Empty : " [folder missing: not watched]")))) +
                              "; game processes: " + (spec.ProcessNames.Count == 0 ? "none" : string.Join(", ", spec.ProcessNames)) +
                              (spec.ProcessFolderList.Count > 0 ? " or any exe under " + string.Join(", ", spec.ProcessFolderList) : string.Empty) +
                              "; quiet " + games[spec.GameId].Settle.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s before a check, " +
                              games[spec.GameId].AfterExit.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s after the game closed)");
                }
            }
        }
        foreach (GameWatch watch in stale)
            watch.Dispose();
        if (stale.Count > 0 || added.Count > 0)
            JoyPro.Logging.JoyProLog.Debug("Watcher", "Watch list changed: " + string.Join(", ", WatchedGames) +
                                                      (added.Count > 0 ? Environment.NewLine + "new: " + string.Join(Environment.NewLine + "new: ", added) : string.Empty) +
                                                      (stale.Count > 0 ? Environment.NewLine + "stopped: " + string.Join(", ", stale.Select(s => s.Spec.GameId)) : string.Empty));
    }

    // Checks a game now (as if its config had changed): at startup for configs the game changed while JoyPro was closed.
    public void Trigger(string gameId)
    {
        lock (gate)
        {
            if (!disposed && games.TryGetValue(gameId, out GameWatch? watch))
                watch.Schedule(TimeSpan.Zero);
        }
    }

    public void Dispose()
    {
        List<GameWatch> all;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            all = games.Values.ToList();
            games.Clear();
        }
        foreach (GameWatch watch in all)
            watch.Dispose();
        ConfigChanged = null;
    }

    private void Raise(string gameId)
    {
        Action<string>? handler = ConfigChanged;
        JoyPro.Logging.JoyProLog.Info("Watcher", gameId + ": the game's config changed and the game is not running; checking it");
        try
        {
            handler?.Invoke(gameId);
        }
        catch (Exception ex)
        {
            // A failing handler must not take the timer thread down.
            JoyPro.Logging.JoyProLog.Warn("Watcher", gameId + ": the change handler failed", ex);
        }
    }

    private static bool SameSpec(GameWatchSpec a, GameWatchSpec b)
    {
        return string.Equals(a.RegistryKey, b.RegistryKey, StringComparison.OrdinalIgnoreCase) &&
               a.ProcessNames.SequenceEqual(b.ProcessNames, StringComparer.OrdinalIgnoreCase) &&
               a.ProcessFolderList.SequenceEqual(b.ProcessFolderList, StringComparer.OrdinalIgnoreCase) &&
               a.SettleSeconds == b.SettleSeconds &&
               a.Folders.Count == b.Folders.Count &&
               a.Folders.Zip(b.Folders).All(p => string.Equals(p.First.Directory, p.Second.Directory, StringComparison.OrdinalIgnoreCase) &&
                                                 p.First.IncludeSubdirectories == p.Second.IncludeSubdirectories &&
                                                 p.First.Filters.SequenceEqual(p.Second.Filters, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class GameWatch : IDisposable
    {
        private readonly object sync = new();
        private readonly GameConfigWatcher owner;
        private readonly List<FileSystemWatcher> fileWatchers = [];
        private readonly RegistryChangeWatch? registryWatch;
        private readonly Timer timer;
        private Process? running;
        private bool disposed;

        public GameWatch(GameConfigWatcher owner, GameWatchSpec spec)
        {
            this.owner = owner;
            Spec = spec;
            TimeSpan settle = TimeSpan.FromSeconds(Math.Max(0, spec.SettleSeconds));
            Settle = settle > owner.debounce ? settle : owner.debounce;
            AfterExit = settle > owner.afterExit ? settle : owner.afterExit;
            timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
            foreach (GameWatchFolder folder in spec.Folders)
            {
                if (string.IsNullOrWhiteSpace(folder.Directory) || !Directory.Exists(folder.Directory))
                    continue;
                FileSystemWatcher watcher = new(folder.Directory)
                {
                    IncludeSubdirectories = folder.IncludeSubdirectories,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                };
                foreach (string filter in folder.Filters.Where(f => !string.IsNullOrWhiteSpace(f)))
                    watcher.Filters.Add(filter);
                watcher.Changed += OnFileEvent;
                watcher.Created += OnFileEvent;
                watcher.Deleted += OnFileEvent;
                watcher.Renamed += OnFileRenamed;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                fileWatchers.Add(watcher);
            }
            if (!string.IsNullOrWhiteSpace(spec.RegistryKey))
                registryWatch = RegistryChangeWatch.TryCreate(spec.RegistryKey, () => Schedule(Settle));
        }

        public GameWatchSpec Spec { get; }

        // Quiet time after the last write before the check, and the wait after the game closed.
        public TimeSpan Settle { get; }

        public TimeSpan AfterExit { get; }

        public int HandleCount
        {
            get
            {
                lock (sync)
                    return disposed ? 0 : fileWatchers.Count + (registryWatch == null ? 0 : 1) + (running == null ? 0 : 1) + 1;
            }
        }

        public void Schedule(TimeSpan delay)
        {
            lock (sync)
            {
                if (!disposed)
                    timer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }

        private void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            LogEvent(e.ChangeType.ToString(), e.FullPath);
            Schedule(Settle);
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            LogEvent("Renamed from " + e.OldName, e.FullPath);
            Schedule(Settle);
        }

        // Every file event (diagnostics: what the game wrote and when), at most one line per file and kind every 2 s
        // (a game rewriting a file a thousand times gives a few lines).
        private void LogEvent(string kind, string path)
        {
            if (JoyPro.Logging.JoyProLog.ShouldLog("watch-event-" + Spec.GameId + "|" + kind + "|" + path, TimeSpan.FromSeconds(2)))
                JoyPro.Logging.JoyProLog.Debug("Watcher", Spec.GameId + ": " + kind + " " + path + " at " + DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) +
                                                           " (check after " + Settle.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s quiet)");
        }

        // A lost event (buffer overflow) may have been a change.
        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            if (JoyPro.Logging.JoyProLog.ShouldLog("watcher-error-" + Spec.GameId, TimeSpan.FromMinutes(1)))
                JoyPro.Logging.JoyProLog.Warn("Watcher", Spec.GameId + ": file watcher error (events may be lost)", e.GetException());
            Schedule(Settle);
        }

        // The config is quiet for the debounce time: report it, or wait for the game to exit first.
        private void OnTimer(object? state)
        {
            lock (sync)
            {
                if (disposed || running != null)
                    return;
                RunningGame? game = GameProcesses.Find(Spec.ProcessNames, Spec.ProcessFolderList);
                if (game != null)
                {
                    Process process = game.Process;
                    bool exitEvent = false;
                    try
                    {
                        process.EnableRaisingEvents = true;
                        process.Exited += OnGameExited;
                        running = process;
                        exitEvent = true;
                        if (!process.HasExited)
                        {
                            string started;
                            try
                            {
                                started = process.StartTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                            }
                            catch (Exception)
                            {
                                started = "unknown";
                            }
                            JoyPro.Logging.JoyProLog.Debug("Watcher", Spec.GameId + ": config changed while the game runs (" + process.ProcessName + ", pid " + process.Id + ", started " + started + ", found by " + game.How + "); waiting for it to close");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        // The process is gone, or Windows refuses to wait for it (another user's or a protected process).
                        bool alive;
                        try
                        {
                            alive = !exitEvent && !process.HasExited;
                        }
                        catch (Exception)
                        {
                            alive = !exitEvent;
                        }
                        if (alive)
                        {
                            // No exit event: look again later instead of checking while the game still runs.
                            JoyPro.Logging.JoyProLog.Debug("Watcher", Spec.GameId + ": the game runs (" + process.ProcessName + ", pid " + process.Id + ", found by " + game.How +
                                                                       ") but its exit cannot be awaited (" + ex.Message + "); looking again in " + RecheckWhileRunning.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s");
                            if (running == null)
                                process.Dispose();
                            else
                                ReleaseProcess();
                            timer.Change(RecheckWhileRunning, Timeout.InfiniteTimeSpan);
                            return;
                        }
                    }
                    if (running == null)
                        process.Dispose();
                    ReleaseProcess();
                }
            }
            JoyPro.Logging.JoyProLog.Debug("Watcher", Spec.GameId + ": quiet for " + Settle.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s and no game process (" +
                                                       (Spec.ProcessNames.Count == 0 ? "no names" : string.Join(", ", Spec.ProcessNames)) +
                                                       (Spec.ProcessFolderList.Count > 0 ? "; no exe under " + string.Join(", ", Spec.ProcessFolderList) : string.Empty) + ") runs: checking now");
            owner.Raise(Spec.GameId);
        }

        // A game whose exit cannot be awaited is looked for again this often (only after a change, until it closed).
        private static readonly TimeSpan RecheckWhileRunning = TimeSpan.FromSeconds(10);

        private void OnGameExited(object? sender, EventArgs e)
        {
            lock (sync)
            {
                if (disposed)
                    return;
                JoyPro.Logging.JoyProLog.Debug("Watcher", Spec.GameId + ": the game closed at " + DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) +
                                                           "; checking in " + AfterExit.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s (a later write starts the wait again)");
                ReleaseProcess();
                // Games write their config while closing; the last write starts a new debounce anyway.
                timer.Change(AfterExit, Timeout.InfiniteTimeSpan);
            }
        }

        private void ReleaseProcess()
        {
            if (running == null)
                return;
            running.Exited -= OnGameExited;
            running.Dispose();
            running = null;
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                    return;
                disposed = true;
                foreach (FileSystemWatcher watcher in fileWatchers)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Changed -= OnFileEvent;
                    watcher.Created -= OnFileEvent;
                    watcher.Deleted -= OnFileEvent;
                    watcher.Renamed -= OnFileRenamed;
                    watcher.Error -= OnWatcherError;
                    watcher.Dispose();
                }
                fileWatchers.Clear();
                registryWatch?.Dispose();
                ReleaseProcess();
                timer.Dispose();
            }
        }
    }

    // RegNotifyChangeKeyValue on an event, waited for by the thread pool (no thread of its own, no polling). Thread
    // agnostic, so the registration survives the thread-pool thread that armed it.
    private sealed class RegistryChangeWatch : IDisposable
    {
        private const int RegNotifyChangeName = 0x1;
        private const int RegNotifyChangeLastSet = 0x4;
        private const int RegNotifyThreadAgnostic = 0x10000000;

        private readonly object sync = new();
        private readonly RegistryKey key;
        private readonly AutoResetEvent signal = new(false);
        private readonly Action changed;
        private RegisteredWaitHandle? wait;
        private bool disposed;

        private RegistryChangeWatch(RegistryKey key, Action changed)
        {
            this.key = key;
            this.changed = changed;
        }

        [DllImport("advapi32.dll")]
        private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, bool watchSubtree, int notifyFilter, SafeWaitHandle hEvent, bool asynchronous);

        // HKCU\... (or HKEY_CURRENT_USER\...); null when the key does not exist.
        public static RegistryChangeWatch? TryCreate(string path, Action changed)
        {
            int slash = path.IndexOf('\\');
            if (slash < 0)
                return null;
            RegistryKey? key = Registry.CurrentUser.OpenSubKey(path[(slash + 1)..], false);
            if (key == null)
                return null;
            RegistryChangeWatch watch = new(key, changed);
            if (!watch.Arm())
            {
                watch.Dispose();
                return null;
            }
            return watch;
        }

        private bool Arm()
        {
            lock (sync)
            {
                if (disposed)
                    return false;
                if (RegNotifyChangeKeyValue(key.Handle, false, RegNotifyChangeName | RegNotifyChangeLastSet | RegNotifyThreadAgnostic, signal.SafeWaitHandle, true) != 0)
                    return false;
                wait = ThreadPool.RegisterWaitForSingleObject(signal, OnSignal, null, Timeout.Infinite, executeOnlyOnce: true);
                return true;
            }
        }

        private void OnSignal(object? state, bool timedOut)
        {
            lock (sync)
            {
                if (disposed)
                    return;
                wait?.Unregister(null);
                wait = null;
            }
            // A notification fires once: arm the next one before reporting this change.
            Arm();
            changed();
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                    return;
                disposed = true;
                wait?.Unregister(null);
                wait = null;
                key.Dispose();
                signal.Dispose();
            }
        }
    }
}
