using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JoyPro.Watch;

// A game log or journal the usage recording follows while the game runs (engine usage sources, "Tail").
public sealed record UsageTailTarget(string GameId, string Kind, string Directory, string Filter, IReadOnlyList<string> ProcessNames);

// The usage recording's live tail (a fake in the tests of the tray host).
public interface IUsageTailer : IDisposable
{
    // The game id, on a thread-pool thread, after the game closed and its live copies are finished: time to harvest.
    event Action<string>? GameSessionEnded;

    void Update(IEnumerable<UsageTailTarget> targets);
}

// Follows game logs and journals while the game runs (docs/usage-statistics.md#live-tail) and keeps a live copy of each
// in <data>\Usage\Live\<game>\<kind>~<file>~<ticks>.live, so a log the game overwrites at its next start, or a game
// switch of aircraft or ship in the middle of a session, is never lost. Event driven like the config watcher: a
// FileSystemWatcher per folder (only the log's file names; Size, LastWrite, FileName), a one-shot timer per folder that
// coalesces a burst of writes, and per changed file only the bytes appended since the last offset are read (plus its
// first 256 bytes, to notice a log that was overwritten or replaced: a new live copy then starts). The game's process is
// looked up once when its log first changes and its Exited event finishes the copies (renamed to .done) and raises
// GameSessionEnded. Nothing polls; when no game runs it costs nothing. Every watcher, timer and process is released on
// Update and Dispose.
[SupportedOSPlatform("windows")]
public sealed class UsageLogTailer : IUsageTailer
{
    public const string LiveExtension = ".live";
    public const string DoneExtension = ".done";
    private const int HeadBytes = 256;

    private readonly object gate = new();
    private readonly string liveFolder;
    private readonly TimeSpan readDelay;
    private readonly TimeSpan afterExit;
    private readonly Func<IReadOnlyList<string>, RunningGame?> findGame;
    private readonly Dictionary<string, TargetWatch> targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Tracked> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Process> running = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> gamesWithoutExitWait = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public UsageLogTailer(string liveFolder, TimeSpan? readDelay = null, TimeSpan? afterExit = null, Func<IReadOnlyList<string>, RunningGame?>? findGame = null)
    {
        this.liveFolder = liveFolder;
        this.readDelay = readDelay ?? TimeSpan.FromMilliseconds(750);
        this.afterExit = afterExit ?? TimeSpan.FromSeconds(3);
        this.findGame = findGame ?? (names => GameProcesses.Find(names, null));
    }

    public event Action<string>? GameSessionEnded;

    // Bytes copied into live copies so far (diagnostics and the tests).
    public long BytesCopied { get; private set; }

    // How often a file was read (each read covers only what was appended).
    public int Reads { get; private set; }

    // File watchers, timers and process waits held right now (the resource test checks it stays flat).
    public int HandleCount
    {
        get
        {
            lock (gate)
                return targets.Count * 2 + running.Count;
        }
    }

    public IReadOnlyList<string> Games
    {
        get
        {
            lock (gate)
                return targets.Values.Select(t => t.Target.GameId).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    // The live copies being written right now (path of the game's file -> live copy).
    public IReadOnlyDictionary<string, string> LiveCopies
    {
        get
        {
            lock (gate)
                return files.ToDictionary(f => f.Key, f => f.Value.Live, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Update(IEnumerable<UsageTailTarget> wanted)
    {
        List<TargetWatch> stale = [];
        lock (gate)
        {
            if (disposed)
                return;
            Dictionary<string, UsageTailTarget> next = new(StringComparer.OrdinalIgnoreCase);
            foreach (UsageTailTarget t in wanted ?? [])
            {
                if (!string.IsNullOrWhiteSpace(t.GameId) && !string.IsNullOrWhiteSpace(t.Directory) && !string.IsNullOrWhiteSpace(t.Filter))
                    next[Key(t)] = t;
            }
            foreach (string key in targets.Keys.ToList())
            {
                if (!next.ContainsKey(key))
                {
                    stale.Add(targets[key]);
                    targets.Remove(key);
                }
            }
            foreach (KeyValuePair<string, UsageTailTarget> pair in next)
            {
                if (targets.ContainsKey(pair.Key) || !System.IO.Directory.Exists(pair.Value.Directory))
                    continue;
                targets[pair.Key] = new TargetWatch(this, pair.Value);
            }
        }
        foreach (TargetWatch watch in stale)
            watch.Dispose();
        JoyPro.Logging.JoyProLog.Debug("Usage", "Live tail follows: " + string.Join(", ", Games));
    }

    private static string Key(UsageTailTarget t) => t.GameId + "|" + t.Directory + "|" + t.Filter;

    // Reads what was appended to a game's file since the last read into its live copy.
    internal void Pump(string path, UsageTailTarget target, string? renamedTo = null)
    {
        lock (gate)
        {
            if (disposed)
                return;
            string source = renamedTo ?? path;
            FileStream stream;
            try
            {
                stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            using (stream)
            {
                long length = stream.Length;
                byte[] head = new byte[(int)Math.Min(HeadBytes, length)];
                ReadFully(stream, head);
                files.TryGetValue(path, out Tracked? tracked);
                // Overwritten (shorter than what was read, or other first bytes): the old copy is finished, a new one starts.
                if (tracked != null && (length < tracked.Offset || !SamePrefix(tracked.Head, head)))
                {
                    Finish(path, tracked);
                    tracked = null;
                }
                if (tracked == null)
                {
                    if (length == 0)
                        return;
                    string folder = Path.Combine(liveFolder, Safe(target.GameId));
                    System.IO.Directory.CreateDirectory(folder);
                    tracked = new Tracked(target.GameId, Path.Combine(folder, LiveName(target.Kind, Path.GetFileName(path), DateTime.UtcNow)), head);
                    files[path] = tracked;
                    WaitForGame(target);
                }
                else if (head.Length > tracked.Head.Length)
                {
                    tracked.Head = head;
                }
                if (length > tracked.Offset)
                {
                    stream.Position = tracked.Offset;
                    using FileStream live = new(tracked.Live, FileMode.Append, FileAccess.Write, FileShare.Read);
                    byte[] buffer = new byte[65536];
                    long left = length - tracked.Offset;
                    while (left > 0)
                    {
                        int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                        if (n <= 0)
                            break;
                        live.Write(buffer, 0, n);
                        left -= n;
                        tracked.Offset += n;
                        BytesCopied += n;
                    }
                    Reads++;
                }
                if (renamedTo != null)
                    Finish(path, tracked);
            }
        }
    }

    // A file the game removed (or renamed to something we do not follow): its copy is finished.
    internal void Gone(string path)
    {
        lock (gate)
        {
            if (files.TryGetValue(path, out Tracked? tracked))
                Finish(path, tracked);
        }
    }

    private void Finish(string path, Tracked tracked)
    {
        files.Remove(path);
        try
        {
            if (File.Exists(tracked.Live))
                File.Move(tracked.Live, Path.ChangeExtension(tracked.Live, null) + DoneExtension, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left as .live: the harvest finishes copies older than two days itself.
        }
    }

    // Once per game run: its process, to finish the copies when it closes.
    private void WaitForGame(UsageTailTarget target)
    {
        if (running.ContainsKey(target.GameId) || gamesWithoutExitWait.Contains(target.GameId))
            return;
        RunningGame? game = target.ProcessNames.Count == 0 ? null : findGame(target.ProcessNames);
        if (game == null)
        {
            // The log changed but no process of the game was found (a launcher wrote it, or a test): the copies stay
            // .live until the next change of an overwritten file or the harvest's two days.
            gamesWithoutExitWait.Add(target.GameId);
            return;
        }
        Process process = game.Process;
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => OnGameExited(target.GameId);
            running[target.GameId] = process;
            if (process.HasExited)
                OnGameExited(target.GameId);
            else
                JoyPro.Logging.JoyProLog.Debug("Usage", target.GameId + ": recording live (" + process.ProcessName + ", pid " + process.Id + ")");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            running.Remove(target.GameId);
            process.Dispose();
            gamesWithoutExitWait.Add(target.GameId);
        }
    }

    private void OnGameExited(string game)
    {
        // Games write their last lines while closing.
        Task.Delay(afterExit).ContinueWith(_ => EndSession(game), TaskScheduler.Default);
    }

    // The game closed: last bytes into the copies, copies finished, then the harvest may read them.
    internal void EndSession(string game)
    {
        List<(string Path, UsageTailTarget Target)> mine;
        lock (gate)
        {
            if (disposed)
                return;
            if (running.TryGetValue(game, out Process? process))
            {
                running.Remove(game);
                process.Dispose();
            }
            gamesWithoutExitWait.Remove(game);
            mine = files.Where(f => string.Equals(f.Value.Game, game, StringComparison.OrdinalIgnoreCase))
                .Select(f => (f.Key, targets.Values.Select(t => t.Target).FirstOrDefault(t => string.Equals(t.GameId, game, StringComparison.OrdinalIgnoreCase))!))
                .Where(x => x.Item2 != null).ToList();
        }
        foreach ((string path, UsageTailTarget target) in mine)
            Pump(path, target);
        lock (gate)
        {
            foreach ((string path, _) in mine)
            {
                if (files.TryGetValue(path, out Tracked? tracked))
                    Finish(path, tracked);
            }
        }
        JoyPro.Logging.JoyProLog.Info("Usage", game + " closed: " + mine.Count + " live cop" + (mine.Count == 1 ? "y" : "ies") + " finished");
        try
        {
            GameSessionEnded?.Invoke(game);
        }
        catch (Exception ex)
        {
            JoyPro.Logging.JoyProLog.Warn("Usage", game + ": the session-end handler failed", ex);
        }
    }

    public void Dispose()
    {
        List<TargetWatch> all;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            all = targets.Values.ToList();
            targets.Clear();
            foreach (Process process in running.Values)
                process.Dispose();
            running.Clear();
            files.Clear();
        }
        foreach (TargetWatch watch in all)
            watch.Dispose();
        GameSessionEnded = null;
    }

    public static string LiveName(string kind, string file, DateTime startedUtc)
    {
        return kind + "~" + Safe(file).Replace('~', '_') + "~" + startedUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + LiveExtension;
    }

    private static string Safe(string name) => new((name ?? "log").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

    private static bool SamePrefix(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    private static void ReadFully(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
                break;
            read += n;
        }
    }

    // engine usage sources (JSON): the live folder and what to follow.
    public static (string LiveFolder, List<UsageTailTarget> Targets)? ParseSources(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string live = root.TryGetProperty("LiveFolder", out JsonElement l) ? l.GetString() ?? string.Empty : string.Empty;
            List<UsageTailTarget> list = [];
            if (root.TryGetProperty("Tail", out JsonElement tail) && tail.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement t in tail.EnumerateArray())
                {
                    List<string> names = t.TryGetProperty("ProcessNames", out JsonElement p) && p.ValueKind == JsonValueKind.Array
                        ? p.EnumerateArray().Select(n => n.GetString() ?? string.Empty).Where(n => n.Length > 0).ToList()
                        : [];
                    list.Add(new UsageTailTarget(Str(t, "Game"), Str(t, "Kind"), Str(t, "Directory"), Str(t, "Filter"), names));
                }
            }
            return live.Length == 0 ? null : (live, list);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private sealed class Tracked(string game, string live, byte[] head)
    {
        public string Game { get; } = game;
        public string Live { get; } = live;
        public byte[] Head { get; set; } = head;
        public long Offset { get; set; }
    }

    // One folder of one game: its watcher and the one-shot timer that coalesces the writes of a burst.
    private sealed class TargetWatch : IDisposable
    {
        private readonly UsageLogTailer owner;
        private readonly FileSystemWatcher watcher;
        private readonly Timer timer;
        private readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly object sync = new();
        private bool disposed;

        public TargetWatch(UsageLogTailer owner, UsageTailTarget target)
        {
            this.owner = owner;
            Target = target;
            timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
            watcher = new FileSystemWatcher(target.Directory, target.Filter)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                // Small buffer: only a few files per folder.
                InternalBufferSize = 8192
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnDeleted;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
        }

        public UsageTailTarget Target { get; }

        private void OnChanged(object sender, FileSystemEventArgs e) => Schedule(e.FullPath);

        private void OnDeleted(object sender, FileSystemEventArgs e) => owner.Gone(e.FullPath);

        // A log rotated away (dcs.log -> dcs.log.old): its last bytes come from the new name, then the copy is finished.
        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            owner.Pump(e.OldFullPath, Target, e.FullPath);
            if (Matches(e.Name))
                Schedule(e.FullPath);
        }

        private bool Matches(string? name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            string pattern = "^" + System.Text.RegularExpressions.Regex.Escape(Target.Filter).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(name, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // A lost event (buffer overflow): read every followed file of the folder once.
        private void OnError(object sender, ErrorEventArgs e)
        {
            try
            {
                foreach (string file in System.IO.Directory.GetFiles(Target.Directory, Target.Filter))
                    Schedule(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        private void Schedule(string path)
        {
            lock (sync)
            {
                if (disposed)
                    return;
                pending.Add(path);
                timer.Change(owner.readDelay, Timeout.InfiniteTimeSpan);
            }
        }

        private void OnTimer(object? state)
        {
            List<string> paths;
            lock (sync)
            {
                if (disposed)
                    return;
                paths = pending.ToList();
                pending.Clear();
            }
            foreach (string path in paths)
                owner.Pump(path, Target);
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                    return;
                disposed = true;
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= OnChanged;
                watcher.Created -= OnChanged;
                watcher.Deleted -= OnDeleted;
                watcher.Renamed -= OnRenamed;
                watcher.Error -= OnError;
                watcher.Dispose();
                timer.Dispose();
                pending.Clear();
            }
        }
    }
}
