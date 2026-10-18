using System.Collections.Concurrent;
using System.Globalization;
using JoyPro.Logging;
using JoyPro.TelemetrySessions;

namespace JoyPro.Ffb.Core.Telemetry.Recording;

public sealed class TelemetrySessionRecorderOptions
{
    /// <summary>Overrides the settings' folder (tests, the CLI's --folder).</summary>
    public string? Folder { get; init; }

    /// <summary>How often the recorder looks for the end of a session and flushes (tests: 0 = only by <see cref="TelemetrySessionRecorder.Tick"/>).</summary>
    public TimeSpan TickPeriod { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Written data reaches the disk at least this often (a crash loses at most this much).</summary>
    public TimeSpan FlushPeriod { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Items waiting for the writer before frames are dropped (the disk cannot keep up).</summary>
    public int MaxQueue { get; init; } = 20000;

    /// <summary>Key prefixes not written: derived values JoyPro computes again on replay (the "ac.*" model).</summary>
    public IReadOnlyList<string> ExcludePrefixes { get; init; } = [AircraftTelemetry.Prefix];

    /// <summary>Recording stops when the drive has less free space than this.</summary>
    public long MinFreeBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>The local time for file names (tests).</summary>
    public Func<DateTime> LocalNow { get; init; } = () => DateTime.Now;

    public string RecorderName { get; init; } = "JoyPro " + (typeof(TelemetrySessionRecorder).Assembly.GetName().Version?.ToString(3) ?? "2");
}

/// <summary>Records a source's telemetry into session files (docs/telemetry-recording.md).</summary>
/// <remarks>
/// A session starts with the first frame and ends when the source says so (<see cref="TelemetryEventKinds.SessionEnd"/>),
/// after <see cref="TelemetryRecordingConfig.SessionTimeoutSeconds"/> without a frame, or on <see cref="EndSession"/> /
/// Dispose (game exit, JoyPro exit). The source's threads only decimate (sample rate) and enqueue: compression and disk
/// I/O happen on one background thread of below-normal priority, so the force loop and the HUD never wait for the disk.
/// Every frame, event and raw packet is written in the order it arrived; data reaches the disk at least every
/// <see cref="TelemetrySessionRecorderOptions.FlushPeriod"/>. Sessions cut off by a crash are finished the next time a
/// recorder starts (<see cref="TelemetrySessionStore.Recover"/>). Retention runs before each new session and at its end.
/// </remarks>
public sealed class TelemetrySessionRecorder : IDisposable
{
    private enum ItemKind { Open, Frame, Event, Raw, Close, Flush }

    private sealed record Item(ItemKind Kind, double Time, object? Payload = null, string? Text = null, string? Text2 = null);

    private sealed record OpenPayload(DateTime CreatedUtc, DateTime CreatedLocal, Dictionary<string, string> Header, IReadOnlyList<TelemetryDescriptor> Descriptors);

    private readonly FrameTelemetrySource _source;
    private readonly string _gameId;
    private readonly TelemetryRecordingConfig _config;
    private readonly TelemetrySessionRecorderOptions _options;
    private readonly IFfbClock _clock;
    private readonly object _gate = new();
    private readonly BlockingCollection<Item> _queue = new(new ConcurrentQueue<Item>());
    private readonly Thread _writerThread;
    private readonly Timer? _timer;
    private readonly List<TelemetryEvent> _pendingEvents = [];
    private readonly List<TelemetryRawPacket> _pendingRaw = [];
    private readonly Dictionary<string, double> _lastRawByStream = new(StringComparer.Ordinal);
    private readonly ManualResetEventSlim _idle = new(true);
    private int _pending;
    private bool _open;
    private bool _disposed;
    private double _sessionStart;
    private double _lastFrame;
    private double _lastWritten = double.NegativeInfinity;
    private long _sessionFrames;
    private bool _recovered;

    // Writer thread state.
    private TelemetrySessionWriter? _writer;
    private DateTime _lastFlush = DateTime.UtcNow;
    private DateTime _lastSpaceCheck = DateTime.MinValue;

    public TelemetrySessionRecorder(FrameTelemetrySource source, string gameId, TelemetryRecordingConfig config, TelemetrySessionRecorderOptions? options = null)
    {
        _source = source;
        _gameId = gameId;
        _config = config.Clone();
        _options = options ?? new TelemetrySessionRecorderOptions();
        _clock = source.Clock;
        Folder = _options.Folder ?? _config.ResolvedFolder();
        _writerThread = new Thread(WriterLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "JoyPro telemetry recorder" };
        _writerThread.Start();
        _source.FrameReceived += OnFrame;
        _source.EventRaised += OnEvent;
        if (_config.KeepRaw)
            _source.RawReceived += OnRaw;
        if (_options.TickPeriod > TimeSpan.Zero)
            _timer = new Timer(_ => Tick(), null, _options.TickPeriod, _options.TickPeriod);
        JoyProLog.Info("Recorder", "Recording " + gameId + " to " + Folder + " (sample " + (_config.SampleHz <= 0 ? "every frame" : _config.SampleHz.ToString(CultureInfo.InvariantCulture) + " Hz") +
                                   ", raw " + (_config.KeepRaw ? "on" : "off") + ", session timeout " + _config.SessionTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " s)");
    }

    public string Folder { get; }
    public string GameId => _gameId;

    /// <summary>The file being written, or null between sessions.</summary>
    public string? CurrentPath { get; private set; }

    public bool Recording { get { lock (_gate) return _open; } }

    public long CurrentFrames => Interlocked.Read(ref _sessionFrames);
    public int SessionsWritten { get; private set; }
    public long Dropped { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Raised on the writer thread with the new file.</summary>
    public event Action<string>? SessionStarted;

    /// <summary>Raised on the writer thread with the finished file and why it ended.</summary>
    public event Action<string, string>? SessionEnded;

    /// <summary>Looks for the end of a session (timeout) and asks for a flush. Called by the timer; public for tests.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_disposed || !_open)
                return;
            var now = _clock.Now;
            if (now - _lastFrame > _config.SessionTimeoutSeconds)
            {
                CloseLocked("no telemetry for " + _config.SessionTimeoutSeconds.ToString("0", CultureInfo.InvariantCulture) + " s", _lastFrame);
                return;
            }
        }

        Enqueue(new Item(ItemKind.Flush, 0));
    }

    /// <summary>Ends the current session now (game exit, recording switched off).</summary>
    public void EndSession(string reason)
    {
        lock (_gate)
        {
            if (_open)
                CloseLocked(reason, Math.Max(_lastFrame, _clock.Now));
        }
    }

    /// <summary>Waits until the writer has written everything queued (tests, shutdown).</summary>
    public bool WaitIdle(TimeSpan timeout) => _idle.Wait(timeout);

    public void Dispose() => Dispose("recording stopped");

    public void Dispose(string reason)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _source.FrameReceived -= OnFrame;
            _source.EventRaised -= OnEvent;
            _source.RawReceived -= OnRaw;
            if (_open)
                CloseLocked(reason, Math.Max(_lastFrame, _clock.Now));
            _disposed = true;
        }

        _timer?.Dispose();
        _queue.CompleteAdding();
        if (!_writerThread.Join(TimeSpan.FromSeconds(10)))
            JoyProLog.Warn("Recorder", "The writer did not finish within 10 s");
        _queue.Dispose();
    }

    // ------------------------------------------------------------------ capture side (source threads)

    private void OnFrame(TelemetryFrame frame)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var now = _clock.Now;
            if (!_open)
                OpenLocked(now);
            _lastFrame = now;
            var hz = _config.SampleHz;
            if (hz > 0 && now - _lastWritten < 1 / hz - 1e-6)
                return;
            _lastWritten = now;
            if (_pending > _options.MaxQueue)
            {
                Dropped++;
                return;
            }

            var values = Filter(frame.Values);
            Interlocked.Increment(ref _sessionFrames);
            Enqueue(new Item(ItemKind.Frame, now - _sessionStart, values));
        }
    }

    private void OnEvent(TelemetryEvent e)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (e.Kind == TelemetryEventKinds.SessionStart)
            {
                // A new flight while one is recorded: the old one ends (the first frame opens the next).
                if (_open && Interlocked.Read(ref _sessionFrames) > 0)
                    CloseLocked("a new session started", Math.Max(_lastFrame, e.Time));
                AddPendingEvent(e);
                return;
            }

            if (!_open)
            {
                if (e.Kind != TelemetryEventKinds.SessionEnd)
                    AddPendingEvent(e);
                return;
            }

            Enqueue(new Item(ItemKind.Event, Math.Max(0, e.Time - _sessionStart), null, e.Kind, Cap(e.Text)));
            if (e.Kind == TelemetryEventKinds.SessionEnd)
                CloseLocked(string.IsNullOrWhiteSpace(e.Text) ? "the game ended the session" : e.Text, Math.Max(_lastFrame, e.Time));
        }
    }

    private void OnRaw(TelemetryRawPacket packet)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var hz = _config.SampleHz;
            if (hz > 0 && DecimatedStreams.Contains(packet.Stream))
            {
                if (_lastRawByStream.TryGetValue(packet.Stream, out var last) && packet.Time - last < 1 / hz - 1e-6)
                    return;
                _lastRawByStream[packet.Stream] = packet.Time;
            }

            if (!_open)
            {
                // The packet that makes the first frame arrives before it: keep the last few for the start.
                _pendingRaw.Add(packet);
                if (_pendingRaw.Count > 16)
                    _pendingRaw.RemoveAt(0);
                return;
            }

            if (_pending > _options.MaxQueue)
            {
                Dropped++;
                return;
            }

            Enqueue(new Item(ItemKind.Raw, Math.Max(0, packet.Time - _sessionStart), packet.Data, packet.Stream));
        }
    }

    private void AddPendingEvent(TelemetryEvent e)
    {
        _pendingEvents.Add(e);
        if (_pendingEvents.Count > 200)
            _pendingEvents.RemoveAt(0);
    }

    private void OpenLocked(double now)
    {
        _open = true;
        _sessionStart = now;
        _lastFrame = now;
        _lastWritten = double.NegativeInfinity;
        _lastRawByStream.Clear();
        Interlocked.Exchange(ref _sessionFrames, 0);
        var game = TelemetryRecordingGames.Find(_gameId);
        var header = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["game"] = game?.Id ?? _gameId,
            ["gameTitle"] = game?.Title ?? _gameId,
            ["source"] = _source.Name,
            ["sampleHz"] = TelemetrySessionFormat.FormatNumber(_config.SampleHz),
            ["keepRaw"] = _config.KeepRaw ? "true" : "false",
            ["recorder"] = _options.RecorderName
        };
        foreach (var pair in _source.SessionInfo)
            header["info." + pair.Key] = pair.Value;
        foreach (var nav in _source.NavMappings)
            header["nav." + nav.Target] = new TelemetrySessionNav { Target = nav.Target, Key = nav.Key, Scale = nav.Scale, Offset = nav.Offset }.Format();
        var descriptors = _source.Descriptors.Where(d => !Excluded(d.Key)).ToList();
        Enqueue(new Item(ItemKind.Open, 0, new OpenPayload(DateTime.UtcNow, _options.LocalNow(), header, descriptors)));
        foreach (var e in _pendingEvents)
        {
            if (e.Kind != TelemetryEventKinds.SessionEnd)
                Enqueue(new Item(ItemKind.Event, 0, null, e.Kind, Cap(e.Text)));
        }

        _pendingEvents.Clear();
        foreach (var raw in _pendingRaw.Where(r => now - r.Time < 1))
            Enqueue(new Item(ItemKind.Raw, 0, raw.Data, raw.Stream));
        _pendingRaw.Clear();
    }

    private void CloseLocked(string reason, double at)
    {
        _open = false;
        Enqueue(new Item(ItemKind.Close, Math.Max(0, at - _sessionStart), null, reason));
    }

    private Dictionary<string, double> Filter(IReadOnlyDictionary<string, double> values)
    {
        var result = new Dictionary<string, double>(values.Count, StringComparer.Ordinal);
        foreach (var pair in values)
        {
            if (!Excluded(pair.Key) && double.IsFinite(pair.Value))
                result[pair.Key] = pair.Value;
        }

        return result;
    }

    private bool Excluded(string key)
    {
        foreach (var prefix in _options.ExcludePrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    // The high-rate raw streams (one packet per frame) follow the sample rate; the others (DCS objects and notices,
    // journal lines, War Thunder's map and messages, BMS strings) are kept whole: they are rare and every one counts.
    private static readonly HashSet<string> DecimatedStreams = new(StringComparer.Ordinal)
    {
        "udp", "simconnect", "http:/state", "http:/indicators", "shm:FlightData", "shm:FlightData2"
    };

    private static string Cap(string text) => text.Length <= 65536 ? text : text[..65536];

    private void Enqueue(Item item)
    {
        if (_queue.IsAddingCompleted)
            return;
        Interlocked.Increment(ref _pending);
        _idle.Reset();
        try
        {
            _queue.Add(item);
        }
        catch (InvalidOperationException)
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    // ------------------------------------------------------------------ writer thread

    private void WriterLoop()
    {
        try
        {
            while (!_queue.IsCompleted)
            {
                Item? item = null;
                try
                {
                    if (!_queue.TryTake(out item, _options.FlushPeriod))
                        item = null;
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                if (item is not null)
                {
                    try
                    {
                        Handle(item);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Fail(ex);
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref _pending) == 0)
                            _idle.Set();
                    }
                }

                if (_writer is not null && DateTime.UtcNow - _lastFlush >= _options.FlushPeriod)
                {
                    try
                    {
                        _writer.Flush();
                    }
                    catch (IOException ex)
                    {
                        Fail(ex);
                    }

                    _lastFlush = DateTime.UtcNow;
                }

                if (_pending == 0)
                    _idle.Set();
            }
        }
        finally
        {
            if (_writer is not null)
            {
                try
                {
                    FinishWriter("recording stopped");
                }
                catch (IOException ex)
                {
                    Fail(ex);
                }
            }

            _idle.Set();
        }
    }

    private void Handle(Item item)
    {
        switch (item.Kind)
        {
            case ItemKind.Open:
                OpenWriter((OpenPayload)item.Payload!);
                break;
            case ItemKind.Frame:
                _writer?.Frame(item.Time, (Dictionary<string, double>)item.Payload!);
                break;
            case ItemKind.Event:
                _writer?.Event(item.Time, item.Text ?? "", item.Text2 ?? "");
                break;
            case ItemKind.Raw:
                if (_writer is not null && item.Payload is byte[] data)
                    _writer.Raw(item.Time, item.Text ?? "", data, 0, data.Length);
                break;
            case ItemKind.Close:
                if (_writer is not null)
                    FinishWriter(item.Text ?? "");
                break;
            case ItemKind.Flush:
                if (_writer is not null)
                {
                    _writer.Flush();
                    _lastFlush = DateTime.UtcNow;
                    CheckSpace();
                }

                break;
        }
    }

    private void OpenWriter(OpenPayload open)
    {
        if (_writer is not null)
            FinishWriter("a new session started");
        Directory.CreateDirectory(Folder);
        if (!_recovered)
        {
            _recovered = true;
            var problems = new List<string>();
            foreach (var finished in TelemetrySessionStore.Recover(Folder, problems))
                JoyProLog.Info("Recorder", "Finished a session that was cut off: " + finished);
            foreach (var problem in problems)
                JoyProLog.Warn("Recorder", "Recover: " + problem);
        }

        Prune();
        if (!EnoughSpace())
            return;
        var header = new Dictionary<string, string>(open.Header, StringComparer.Ordinal)
        {
            ["created"] = TelemetrySessionFormat.FormatTime(open.CreatedUtc),
            ["createdLocal"] = open.CreatedLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
        };
        var path = TelemetrySessionStore.NewSessionPath(Folder, _gameId, open.CreatedLocal);
        _writer = new TelemetrySessionWriter(path, header);
        foreach (var d in open.Descriptors)
            _writer.Channel(d.Key, d.Unit, d.Title, d.Category, d.Min, d.Max);
        CurrentPath = path;
        LastError = null;
        _lastFlush = DateTime.UtcNow;
        JoyProLog.Info("Recorder", "Session started: " + path);
        SessionStarted?.Invoke(path);
    }

    private void FinishWriter(string reason)
    {
        var writer = _writer!;
        _writer = null;
        var path = writer.Path;
        try
        {
            writer.Finish(reason, DateTime.UtcNow);
        }
        finally
        {
            writer.Dispose();
        }

        CurrentPath = null;
        SessionsWritten++;
        JoyProLog.Info("Recorder", "Session ended (" + reason + "): " + path + ", " + writer.Frames.ToString(CultureInfo.InvariantCulture) + " frames, " +
                                   TelemetrySessionStore.FormatSize(new FileInfo(path).Length));
        SessionEnded?.Invoke(path, reason);
        Prune();
    }

    private void Prune()
    {
        if (_config.MaxTotalBytes <= 0 && _config.KeepDays <= 0)
            return;
        try
        {
            var problems = new List<string>();
            foreach (var path in TelemetrySessionStore.Prune(Folder, _config.MaxTotalBytes, _config.KeepDays, DateTime.UtcNow, problems))
                JoyProLog.Info("Recorder", "Retention: deleted " + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            JoyProLog.Warn("Recorder", "Retention failed", ex);
        }
    }

    private bool EnoughSpace()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(Folder));
            if (string.IsNullOrEmpty(root))
                return true;
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.AvailableFreeSpace >= _options.MinFreeBytes)
                return true;
            LastError = "not recording: less than " + TelemetrySessionStore.FormatSize(_options.MinFreeBytes) + " free on " + root;
            JoyProLog.Warn("Recorder", LastError);
            return false;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private void CheckSpace()
    {
        if (DateTime.UtcNow - _lastSpaceCheck < TimeSpan.FromSeconds(30))
            return;
        _lastSpaceCheck = DateTime.UtcNow;
        if (_writer is not null && !EnoughSpace())
            FinishWriter("stopped: the drive is almost full");
    }

    private void Fail(Exception ex)
    {
        LastError = ex.Message;
        JoyProLog.Warn("Recorder", "Writing the session failed", ex);
        if (_writer is not null)
        {
            try
            {
                _writer.Dispose();
            }
            catch (IOException)
            {
            }

            _writer = null;
            CurrentPath = null;
        }
    }
}
