using System.Diagnostics;
using System.Globalization;
using JoyPro.Ffb.Core.Telemetry;
using JoyPro.Ffb.Core.Telemetry.Recording;
using JoyPro.Logging;
using JoyPro.TelemetrySessions;

namespace JoyPro.Ffb.Core.Host;

public sealed class TelemetryRecordHostOptions
{
    /// <summary>The game process to follow (the GUI passes it); null = look for the game by name.</summary>
    public int? GamePid { get; init; }

    public double GameCheckSeconds { get; init; } = 2;

    /// <summary>Record without the game process (tests, replays, the CLI's --no-game-check).</summary>
    public bool IgnoreGameProcess { get; init; }

    public TelemetryFeedOptions Feed { get; init; } = new();

    public TelemetrySessionRecorderOptions Recorder { get; init; } = new();

    /// <summary>Record even when the settings say off (the CLI's explicit "telemetry record").</summary>
    public bool Force { get; init; }
}

/// <summary>What the record-only host does now: "off", "waiting" (for the game), "listening" (no session), "recording".</summary>
public sealed record TelemetryRecordStatus(string State, string Reason, string Game, string? Aircraft, string? Session, long Frames, string Endpoint, int Sessions, string? Error);

/// <summary>Records a game's telemetry without force feedback (JoyPro.Ffb record): the game's feed runs only while the
/// game runs and recording is on for it; everything else is the <see cref="TelemetrySessionRecorder"/>.</summary>
public sealed class TelemetryRecordHost : IDisposable
{
    private readonly object _gate = new();
    private readonly TelemetryRecordingGame _game;
    private readonly IFfbClock _clock;
    private readonly TelemetryRecordHostOptions _options;
    private TelemetryRecordingConfig _config;
    private ITelemetryFeed? _feed;
    private TelemetrySessionRecorder? _recorder;
    private Process? _gameProcess;
    private Timer? _gameTimer;
    private Timer? _statusTimer;
    private bool _gameRunning;
    private bool _disposed;
    private TelemetryRecordStatus? _last;
    private string? _feedError;

    public TelemetryRecordHost(string gameId, TelemetryRecordingConfig config, IFfbClock clock, TelemetryRecordHostOptions? options = null)
    {
        _game = TelemetryRecordingGames.Find(gameId) ?? throw new ArgumentException("JoyPro reads no telemetry of '" + gameId + "'.", nameof(gameId));
        _config = config;
        _clock = clock;
        _options = options ?? new TelemetryRecordHostOptions();
    }

    public TelemetryRecordingGame Game => _game;
    public ITelemetryFeed? Feed => _feed;
    public TelemetrySessionRecorder? Recorder => _recorder;

    public event Action<TelemetryRecordStatus>? StatusChanged;
    public event Action? GameExited;

    public TelemetryRecordStatus Status => Compute();

    private bool Wanted => _options.Force || _config.Records(_game.Id);

    public void Start()
    {
        lock (_gate)
        {
            if (_feed is not null)
                return;
            _feed = TelemetryFeeds.Create(_game.Id, _clock, _options.Feed);
            // Recording wants everything: War Thunder's map, objects, HUD messages and mission too.
            if (_feed is WarThunderTelemetryFeed wt)
                wt.RecordExtras = true;
            _feed.Source.EventRaised += _ => Publish();
            JoyProLog.Info("Recorder", "Record host: " + _game.Id + " via " + _feed.Endpoint + (_options.GamePid is { } pid ? ", game pid " + pid.ToString(CultureInfo.InvariantCulture) : string.Empty));
            FollowGameLocked();
            ApplyLocked();
            _statusTimer = new Timer(_ => Publish(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        Publish();
    }

    public void Reload(TelemetryRecordingConfig config)
    {
        lock (_gate)
        {
            var previous = _config;
            _config = config;
            if (_recorder is not null && !SameRecorderSettings(previous, config))
            {
                _recorder.Dispose("the recording settings changed");
                _recorder = null;
            }

            ApplyLocked();
        }

        Publish();
    }

    public void Stop(string reason = "recording stopped")
    {
        lock (_gate)
        {
            _gameTimer?.Dispose();
            _gameTimer = null;
            _statusTimer?.Dispose();
            _statusTimer = null;
            if (_gameProcess is not null)
            {
                _gameProcess.Exited -= OnGameExited;
                _gameProcess.Dispose();
                _gameProcess = null;
            }

            _recorder?.Dispose(reason);
            _recorder = null;
            _feed?.Dispose();
            _feed = null;
        }

        Publish();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    private static bool SameRecorderSettings(TelemetryRecordingConfig a, TelemetryRecordingConfig b) =>
        a.ResolvedFolder() == b.ResolvedFolder() && a.SampleHz == b.SampleHz && a.KeepRaw == b.KeepRaw &&
        a.SessionTimeoutSeconds == b.SessionTimeoutSeconds && a.MaxTotalMb == b.MaxTotalMb && a.KeepDays == b.KeepDays;

    // Feed and recorder exist exactly while the game runs and recording is on for it.
    private void ApplyLocked()
    {
        if (_feed is null)
            return;
        var active = Wanted && (_gameRunning || _options.IgnoreGameProcess);
        if (active)
        {
            if (_recorder is null)
            {
                _recorder = new TelemetrySessionRecorder(_feed.Source, _game.Id, _config, _options.Recorder);
                _recorder.SessionStarted += _ => Publish();
                _recorder.SessionEnded += (_, _) => Publish();
            }

            try
            {
                _feed.Start();
                _feedError = null;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                _feedError = _feed.Problem ?? ex.Message;
                JoyProLog.Warn("Recorder", "The " + _game.Title + " feed did not start", ex);
            }
        }
        else
        {
            _feed.Stop();
            if (_recorder is not null)
            {
                _recorder.Dispose(_gameRunning || _options.IgnoreGameProcess ? "recording switched off" : "game exited");
                _recorder = null;
            }
        }
    }

    private void FollowGameLocked()
    {
        if (_options.IgnoreGameProcess)
        {
            _gameRunning = true;
            return;
        }

        if (_options.GamePid is { } pid)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                process.EnableRaisingEvents = true;
                process.Exited += OnGameExited;
                _gameProcess = process;
                _gameRunning = !process.HasExited;
            }
            catch (ArgumentException)
            {
                _gameRunning = false;
            }

            if (!_gameRunning)
                ThreadPool.QueueUserWorkItem(_ => OnGameExited(null, EventArgs.Empty));
            return;
        }

        _gameRunning = GameProcessWatch.IsRunning(_game.ProcessNames);
        var period = TimeSpan.FromSeconds(Math.Max(0.5, _options.GameCheckSeconds));
        _gameTimer = new Timer(_ => CheckGame(), null, period, period);
    }

    private void CheckGame()
    {
        var running = GameProcessWatch.IsRunning(_game.ProcessNames);
        lock (_gate)
        {
            if (running == _gameRunning || _feed is null)
                return;
            _gameRunning = running;
            JoyProLog.Info("Recorder", _game.Id + (running ? " started" : " is no longer running"));
            ApplyLocked();
        }

        Publish();
    }

    private void OnGameExited(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _gameRunning = false;
            ApplyLocked();
        }

        Publish();
        GameExited?.Invoke();
    }

    private void Publish()
    {
        var status = Compute();
        var previous = Interlocked.Exchange(ref _last, status);
        if (previous is not null && previous.State == status.State && previous.Reason == status.Reason && previous.Session == status.Session &&
            previous.Frames == status.Frames && previous.Aircraft == status.Aircraft && previous.Error == status.Error)
            return;
        StatusChanged?.Invoke(status);
    }

    private TelemetryRecordStatus Compute()
    {
        var feed = _feed;
        var recorder = _recorder;
        var endpoint = feed?.Endpoint ?? _game.Transport;
        string? aircraft = null;
        if (feed is not null && feed.Source.SessionInfo.TryGetValue(TelemetryInfoKeys.Aircraft, out var name))
            aircraft = name;
        var sessions = recorder?.SessionsWritten ?? 0;
        if (feed is null)
            return new TelemetryRecordStatus("off", "stopped", _game.Id, aircraft, null, 0, endpoint, sessions, null);
        if (!Wanted)
            return new TelemetryRecordStatus("off", "telemetry recording is off for " + _game.Title, _game.Id, aircraft, null, 0, endpoint, sessions, null);
        if (!_gameRunning && !_options.IgnoreGameProcess)
            return new TelemetryRecordStatus("waiting", "waiting for " + string.Join(" / ", _game.ProcessNames.Select(n => n + ".exe")), _game.Id, aircraft, null, 0, endpoint, sessions, null);
        var error = _feedError ?? recorder?.LastError;
        if (recorder?.CurrentPath is { } path)
            return new TelemetryRecordStatus("recording", "recording" + (aircraft is null ? "" : " - " + aircraft), _game.Id, aircraft, path, recorder.CurrentFrames, endpoint, sessions, error);
        var problem = error ?? feed.Problem;
        return new TelemetryRecordStatus("listening", "listening - no telemetry " + (problem is null ? "on " + endpoint : "(" + problem + ")"), _game.Id, aircraft, null, 0, endpoint, sessions, error);
    }
}
