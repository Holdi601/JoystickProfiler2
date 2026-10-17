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

// What to watch for one game: its config files or registry key, and the processes that write them on exit.
public sealed record GameWatchSpec(string GameId, IReadOnlyList<GameWatchFolder> Folders, string RegistryKey, IReadOnlyList<string> ProcessNames);

// Tells the GUI when a game changed its controls and has closed again ("after the game has closed").
// Event driven only: a FileSystemWatcher per config folder (only the managed file names, LastWrite|Size|FileName) or a
// registry change notification (RegNotifyChangeKeyValue on a thread-pool wait), a one-shot debounce timer per game that
// only runs after a change, and Process.Exited while the game is still running. Nothing polls; when idle the watcher
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
                    added.Add(spec.GameId + " (" + (!string.IsNullOrEmpty(spec.RegistryKey) ? spec.RegistryKey : string.Join("; ", spec.Folders.Select(f => f.Directory + "\\" + string.Join("|", f.Filters)))) + ")");
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
                registryWatch = RegistryChangeWatch.TryCreate(spec.RegistryKey, () => Schedule(owner.debounce));
        }

        public GameWatchSpec Spec { get; }

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

        private void OnFileEvent(object sender, FileSystemEventArgs e) => Schedule(owner.debounce);

        private void OnFileRenamed(object sender, RenamedEventArgs e) => Schedule(owner.debounce);

        // A lost event (buffer overflow) may have been a change.
        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            if (JoyPro.Logging.JoyProLog.ShouldLog("watcher-error-" + Spec.GameId, TimeSpan.FromMinutes(1)))
                JoyPro.Logging.JoyProLog.Warn("Watcher", Spec.GameId + ": file watcher error (events may be lost)", e.GetException());
            Schedule(owner.debounce);
        }

        // The config is quiet for the debounce time: report it, or wait for the game to exit first.
        private void OnTimer(object? state)
        {
            lock (sync)
            {
                if (disposed || running != null)
                    return;
                Process? process = FindRunning(Spec.ProcessNames);
                if (process != null)
                {
                    try
                    {
                        process.EnableRaisingEvents = true;
                        process.Exited += OnGameExited;
                        running = process;
                        if (!process.HasExited)
                        {
                            JoyPro.Logging.JoyProLog.Debug("Watcher", Spec.GameId + ": config changed while the game runs (" + process.ProcessName + ", pid " + process.Id + "); waiting for it to close");
                            return;
                        }
                    }
                    catch (Exception)
                    {
                        // Access denied (elevated game) or the process is gone: treat it as exited.
                    }
                    ReleaseProcess();
                }
            }
            owner.Raise(Spec.GameId);
        }

        private void OnGameExited(object? sender, EventArgs e)
        {
            lock (sync)
            {
                if (disposed)
                    return;
                ReleaseProcess();
                // Games write their config while closing; the last write starts a new debounce anyway.
                timer.Change(owner.afterExit, Timeout.InfiniteTimeSpan);
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

        // The first running process of the game, or null; every other Process object is disposed.
        private static Process? FindRunning(IReadOnlyList<string> names)
        {
            Process? found = null;
            foreach (string name in names)
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(name);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (Process process in processes)
                {
                    if (found == null)
                        found = process;
                    else
                        process.Dispose();
                }
            }
            return found;
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
