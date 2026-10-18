using System.Diagnostics;
using System.Globalization;
using JoyPro.Ffb.Core.Graph;
using JoyPro.Ffb.Core.Output;
using JoyPro.Ffb.Core.Runtime;
using JoyPro.Ffb.Core.Telemetry;
using JoyPro.Ffb.Core.Telemetry.Dcs;
using JoyPro.Ffb.Core.Telemetry.Il2;
using JoyPro.Ffb.Core.Telemetry.Recording;
using JoyPro.Ffb.Core.Telemetry.WarThunder;
using JoyPro.Logging;

namespace JoyPro.Ffb.Core.Host;

public enum TelemetryFfbState
{
    /// <summary>Switched off in the settings (the device is left alone).</summary>
    Disabled,

    /// <summary>The game is not running.</summary>
    WaitingForGame,

    /// <summary>The game runs but sends nothing (menus, pause, setup missing): silent.</summary>
    NoTelemetry,

    /// <summary>Forces are being sent.</summary>
    Active,

    /// <summary>An error stopped the forces.</summary>
    Faulted
}

/// <summary>A status line: the state and why, as the Forza app's header ("idle - no telemetry on UDP 5300").</summary>
public sealed record TelemetryFfbStatus(
    TelemetryFfbState State,
    string Reason,
    string Game,
    string? Aircraft,
    string Graph,
    long Frames,
    string Endpoint)
{
    public string StateName => State switch
    {
        TelemetryFfbState.Disabled => "disabled",
        TelemetryFfbState.WaitingForGame => "waiting",
        TelemetryFfbState.NoTelemetry => "silent",
        TelemetryFfbState.Active => "active",
        _ => "faulted"
    };
}

public sealed class TelemetryFfbHostOptions
{
    /// <summary>The game process to follow (the GUI passes it); null = look for the game by name.</summary>
    public int? GamePid { get; init; }

    /// <summary>How often the game is looked for by name when no pid is given (Forza app: every 2 s).</summary>
    public double GameCheckSeconds { get; init; } = 2;

    /// <summary>Skip the game process check (tests and replays drive telemetry without the game).</summary>
    public bool IgnoreGameProcess { get; init; }

    /// <summary>Bind address for UDP (tests: loopback, port 0 picks one).</summary>
    public int? PortOverride { get; init; }

    /// <summary>War Thunder base URL override (tests: a local stub).</summary>
    public Uri? WarThunderUri { get; init; }

    public HttpMessageHandler? WarThunderHandler { get; init; }

    /// <summary>Where graph references of the config are resolved (default: TelemetryFfbPaths.GraphFolder()).</summary>
    public string? GraphFolder { get; init; }

    /// <summary>How long a device plays an effect without refresh. The DirectInput backend restarts effects after half
    /// of it, so a hung or killed process leaves the stick within this time; shorter means more USB traffic.</summary>
    public double EffectDurationSeconds { get; init; } = 0.3;

    /// <summary>Buttons and stick position for input and trim nodes (JoyPro.Ffb: DirectInput; tests: fakes).</summary>
    public JoyPro.Ffb.Core.Input.IFfbInputs? Inputs { get; init; }

    /// <summary>Wait before the output is tried again after an error.</summary>
    public double RetrySeconds { get; init; } = 10;

    /// <summary>Telemetry recording settings (docs/telemetry-recording.md); null = read telemetry-recording.json.</summary>
    public TelemetryRecordingConfig? RecordingConfig { get; init; }

    public TelemetrySessionRecorderOptions? RecorderOptions { get; init; }
}

/// <summary>Runs telemetry force feedback for one game: source -> normalised model -> graph -> mixer -> backend.</summary>
/// <remarks>
/// Gating (from the Forza app's "only while the game is running"): forces only while the game process runs AND
/// telemetry arrives. Going idle zeroes the outputs once (one STOP ALL) and then leaves the device alone; the graph
/// keeps being evaluated while someone watches (<see cref="Watch"/>), for the editor and the telemetry inspector.
/// <see cref="Status"/> names the reason. The game process is followed through its handle when the pid is known
/// (a wait, no polling); otherwise it is looked for by name every <see cref="TelemetryFfbHostOptions.GameCheckSeconds"/>.
/// </remarks>
public sealed class TelemetryFfbHost : IDisposable
{
    private readonly object _gate = new();
    private readonly TelemetryGame _game;
    private readonly IFfbBackend _backend;
    private readonly IFfbClock _clock;
    private readonly TelemetryFfbHostOptions _options;
    private TelemetryFfbConfig _config;
    private FrameTelemetrySource? _source;
    private UdpTelemetryReceiver? _udp;
    private FfbRuntime? _runtime;
    private Timer? _gameTimer;
    private Timer? _retryTimer;
    private Process? _gameProcess;
    private bool _gameRunning;
    private string? _aircraft;
    private string _graphName = "";
    private string _graphReference = "\0";
    private TelemetryFfbStatus? _status;
    private bool _watching;
    private bool _disposed;
    private int _graphSwitchPending;
    private TelemetryRecordingConfig _recordingConfig = new();
    private TelemetrySessionRecorder? _recorder;

    public TelemetryFfbHost(TelemetryFfbConfig config, TelemetryGame game, IFfbBackend backend, IFfbClock clock, TelemetryFfbHostOptions? options = null)
    {
        _config = config;
        _game = game;
        _backend = backend;
        _clock = clock;
        _options = options ?? new TelemetryFfbHostOptions();
    }

    public TelemetryGame Game => _game;
    public FfbRuntime? Runtime => _runtime;
    public FrameTelemetrySource? Source => _source;

    /// <summary>The UDP port actually listened on (DCS, IL-2), 0 otherwise.</summary>
    public int Port => _udp?.Port ?? 0;

    public string? Aircraft => _aircraft;

    /// <summary>The session recorder while telemetry recording is on for this game (it shares this host's source).</summary>
    public TelemetrySessionRecorder? Recorder => _recorder;

    /// <summary>Raised (on any thread) when a recording session starts or ends.</summary>
    public event Action? RecordingChanged;

    /// <summary>Raised (on any thread) when the status line changes.</summary>
    public event Action<TelemetryFfbStatus>? StatusChanged;

    /// <summary>Raised when the followed game process exited (the process host then ends).</summary>
    public event Action? GameExited;

    /// <summary>The status line now (computed fresh; <see cref="StatusChanged"/> reports changes).</summary>
    public TelemetryFfbStatus Status => Compute();

    public void Start()
    {
        lock (_gate)
        {
            if (_runtime is not null)
                return;
            var runtime = new FfbRuntime(_backend, _clock, new FfbRuntimeOptions
            {
                TickRateHz = _config.TickRateHz,
                StaleAfterSeconds = _config.TimeoutSeconds,
                EffectDurationSeconds = Math.Max(_options.EffectDurationSeconds, 3 / _config.TickRateHz)
            });
            runtime.StateChanged += state =>
            {
                JoyProLog.Debug("FfbHost", "Runtime state: " + state);
                Publish();
            };
            runtime.Faulted += ex =>
            {
                JoyProLog.Warn("FfbHost", "Force output faulted (device unplugged or held by the game?); retry in " +
                                          Math.Max(1, _options.RetrySeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + " s", ex);
                Publish();
                ScheduleRetry();
            };
            JoyProLog.Info("FfbHost", "Start: game " + _game.Id + ", tick " + _config.TickRateHz.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                      " Hz, timeout " + _config.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " s, enabled " + _config.Enabled +
                                      (_options.GamePid is { } followPid ? ", following game pid " + followPid.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty) +
                                      (_options.IgnoreGameProcess ? ", game check off" : string.Empty));
            _runtime = runtime;
            _recordingConfig = _options.RecordingConfig ?? TelemetryRecordingConfig.Load();
            _source = CreateSource();
            _source.Enricher = _game.Id switch
            {
                TelemetryGame.Dcs => AircraftTelemetryDeriver.ForDcs(),
                TelemetryGame.WarThunder => AircraftTelemetryDeriver.ForWarThunder(),
                _ => AircraftTelemetryDeriver.ForIl2()
            };
            _source.FrameReceived += OnFrame;
            runtime.Source = _source;
            runtime.Inputs = _options.Inputs;
            ApplyConfigLocked();
            runtime.Start();
            FollowGameLocked();
        }

        Publish();
    }

    /// <summary>After an error (the device was unplugged, the game holds it exclusively) the output is off; it is tried
    /// again after <see cref="TelemetryFfbHostOptions.RetrySeconds"/> (each retry that fails sends STOP ALL again).</summary>
    private void ScheduleRetry()
    {
        lock (_gate)
        {
            if (_runtime is null || _disposed)
                return;
            _retryTimer?.Dispose();
            _retryTimer = new Timer(_ =>
            {
                lock (_gate)
                {
                    if (_runtime is null || _runtime.State != FfbRuntimeState.Faulted)
                        return;
                    ApplyConfigLocked();
                }

                Publish();
            }, null, TimeSpan.FromSeconds(Math.Max(1, _options.RetrySeconds)), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Takes new settings (the GUI or the CLI changed them): strengths, effects, graph, on/off.</summary>
    public void Reload(TelemetryFfbConfig config)
    {
        JoyProLog.Info("FfbHost", "Settings reloaded (enabled " + config.Enabled + ", device '" + config.Device + "')");
        lock (_gate)
        {
            _config = config;
            _graphReference = "\0";
            if (_runtime is not null)
                ApplyConfigLocked();
        }

        Publish();
    }

    /// <summary>Takes new recording settings (recording switched on or off, other folder or rate).</summary>
    public void ReloadRecording(TelemetryRecordingConfig config)
    {
        lock (_gate)
        {
            var changed = !string.Equals(config.ToJson(), _recordingConfig.ToJson(), StringComparison.Ordinal);
            _recordingConfig = config;
            if (changed && _recorder is not null)
            {
                _recorder.Dispose("the recording settings changed");
                _recorder = null;
            }

            if (_runtime is not null)
                ApplyConfigLocked();
            if (!changed)
                return;
        }

        RecordingChanged?.Invoke();
    }

    /// <summary>Someone looks at the live values: keep evaluating while idle and capture node values.</summary>
    public void Watch(bool on)
    {
        _watching = on;
        if (_runtime is null)
            return;
        _runtime.PreviewWhileIdle = on;
        _runtime.CaptureNodeValues = on;
        _runtime.Wake();
    }

    public bool Watching => _watching;

    /// <summary>Stops the forces (STOP ALL) and the sources.</summary>
    public void Stop()
    {
        FfbRuntime? runtime;
        lock (_gate)
        {
            runtime = _runtime;
            _runtime = null;
            _gameTimer?.Dispose();
            _gameTimer = null;
            _retryTimer?.Dispose();
            _retryTimer = null;
            if (_gameProcess is not null)
            {
                _gameProcess.Exited -= OnGameExited;
                _gameProcess.Dispose();
                _gameProcess = null;
            }
        }

        if (runtime is not null)
        {
            runtime.Enabled = false;
            runtime.Dispose();
        }

        var recorder = _recorder;
        _recorder = null;
        recorder?.Dispose(_gameRunning || _options.IgnoreGameProcess ? "recording stopped" : "game exited");
        _udp?.Dispose();
        _udp = null;
        if (_source is IDisposable disposable)
            disposable.Dispose();
        if (_source is not null)
            _source.FrameReceived -= OnFrame;
        Publish();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    // ------------------------------------------------------------------ setup

    private FrameTelemetrySource CreateSource()
    {
        var port = _options.PortOverride ?? _config.PortFor(_game);
        switch (_game.Id)
        {
            case TelemetryGame.Dcs:
            {
                var dcs = new DcsTelemetrySource(_clock);
                dcs.Notice += () => OnFrame(null);
                _udp = new UdpTelemetryReceiver(port, datagram => dcs.Ingest(datagram.Span));
                return dcs;
            }
            case TelemetryGame.WarThunder:
            {
                var settings = _config.Game(_game.Id);
                var uri = _options.WarThunderUri ?? (Uri.TryCreate(settings.Url, UriKind.Absolute, out var parsed) && parsed.IsLoopback
                    ? parsed
                    : WarThunderProtocol.DefaultBaseUri);
                var wt = new WarThunderTelemetrySource(_clock, uri, settings.PollHz, _options.WarThunderHandler);
                return wt;
            }
            default:
            {
                var il2 = new Il2TelemetrySource(_clock, _game.Title);
                _udp = new UdpTelemetryReceiver(port, datagram => il2.Ingest(datagram.Span));
                return il2;
            }
        }
    }

    private void ApplyConfigLocked()
    {
        var runtime = _runtime!;
        var gameSettings = _config.Game(_game.Id);
        var enabled = _config.Enabled && gameSettings.Enabled && (_gameRunning || _options.IgnoreGameProcess);
        runtime.DeviceSettingsOverride = new FfbDeviceSettings(_config.GainFor(_game.Id, _aircraft), _config.MaxForce);
        runtime.EffectScales = _config.EffectScales(_game.Id, _aircraft);
        runtime.Centring = _config.Centring();
        LoadGraphLocked();
        runtime.PreviewWhileIdle = _watching;
        runtime.CaptureNodeValues = _watching;
        runtime.Enabled = enabled;
        var recording = _recordingConfig.Records(_game.Id) && (_gameRunning || _options.IgnoreGameProcess);
        ApplyRecordingLocked(recording);
        if (_source is WarThunderTelemetrySource wt)
        {
            // Poll only while the game runs (and force feedback or recording is on): no requests otherwise. The map,
            // objects, HUD messages and mission are polled only for a recording.
            wt.RecordExtras = recording;
            if (enabled || recording)
                wt.Start();
            else
                wt.Stop();
        }
    }

    // The recorder exists exactly while recording is on for this game and the game runs.
    private void ApplyRecordingLocked(bool recording)
    {
        if (_source is null)
            return;
        if (recording && _recorder is null)
        {
            _recorder = new TelemetrySessionRecorder(_source, _game.Id, _recordingConfig, _options.RecorderOptions);
            _recorder.SessionStarted += _ => RecordingChanged?.Invoke();
            _recorder.SessionEnded += (_, _) => RecordingChanged?.Invoke();
        }
        else if (!recording && _recorder is not null)
        {
            var recorder = _recorder;
            _recorder = null;
            recorder.Dispose(_gameRunning || _options.IgnoreGameProcess ? "recording switched off" : "game exited");
            RecordingChanged?.Invoke();
        }
    }

    private void LoadGraphLocked()
    {
        var reference = _config.GraphFor(_game.Id, _aircraft);
        if (reference == _graphReference)
            return;
        _graphReference = reference;
        FfbGraph graph;
        if (reference.Length == 0)
        {
            graph = FfbPresets.Flight();
        }
        else
        {
            try
            {
                var folder = _options.GraphFolder ?? TelemetryFfbPaths.GraphFolder();
                var path = Path.IsPathRooted(reference) ? reference : Path.Combine(folder, reference);
                graph = FfbGraphPersistence.Load(path).Graph;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                JoyProLog.Warn("FfbHost", "Graph '" + reference + "' not loaded; using the built-in flight graph", ex);
                graph = FfbPresets.Flight();
                graph.Name = FfbPresets.FlightName + " (" + reference + " not loaded: " + ex.Message + ")";
            }
        }

        _graphName = string.IsNullOrWhiteSpace(graph.Name) ? reference : graph.Name;
        JoyProLog.Info("FfbHost", "Graph for " + (_aircraft ?? "(no aircraft yet)") + ": " + _graphName + (reference.Length == 0 ? " (built-in)" : " (" + reference + ")"));
        var wasEnabled = _runtime!.Enabled;
        _runtime.ReplaceGraph(graph);
        _runtime.Enabled = wasEnabled;
    }

    // ------------------------------------------------------------------ the game process

    private void FollowGameLocked()
    {
        if (_options.IgnoreGameProcess)
        {
            _gameRunning = true;
            ApplyConfigLocked();
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
                if (!_gameRunning)
                    ThreadPool.QueueUserWorkItem(_ => OnGameExited(null, EventArgs.Empty));
            }
            catch (ArgumentException)
            {
                _gameRunning = false;
                ThreadPool.QueueUserWorkItem(_ => OnGameExited(null, EventArgs.Empty));
            }

            ApplyConfigLocked();
            return;
        }

        _gameRunning = GameProcessWatch.IsRunning(_game.ProcessNames);
        ApplyConfigLocked();
        var period = TimeSpan.FromSeconds(Math.Max(0.5, _options.GameCheckSeconds));
        _gameTimer = new Timer(_ => CheckGame(), null, period, period);
    }

    private void CheckGame()
    {
        var running = GameProcessWatch.IsRunning(_game.ProcessNames);
        lock (_gate)
        {
            if (running == _gameRunning || _runtime is null)
                return;
            _gameRunning = running;
            JoyProLog.Info("FfbHost", _game.Id + (running ? " started" : " is no longer running"));
            ApplyConfigLocked();
        }

        Publish();
    }

    private void OnGameExited(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _gameRunning = false;
            if (_runtime is not null)
                ApplyConfigLocked();
        }

        Publish();
        GameExited?.Invoke();
    }

    // ------------------------------------------------------------------ frames, aircraft, status

    private void OnFrame(TelemetryFrame? frame)
    {
        if (frame is null)
            Publish();
        _runtime?.Wake();
        var aircraft = CurrentAircraft();
        if (!string.Equals(aircraft, _aircraft, StringComparison.Ordinal) && Interlocked.Exchange(ref _graphSwitchPending, 1) == 0)
        {
            // Off the receive thread: loading a graph file must not delay telemetry.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Interlocked.Exchange(ref _graphSwitchPending, 0);
                lock (_gate)
                {
                    _aircraft = CurrentAircraft();
                    if (_runtime is not null)
                        ApplyConfigLocked();
                }

                Publish();
            });
        }

        if (_status is { State: not TelemetryFfbState.Active } && _runtime?.State == FfbRuntimeState.Active)
            Publish();
    }

    private string? CurrentAircraft() => _source switch
    {
        DcsTelemetrySource dcs => dcs.Aircraft,
        Il2TelemetrySource il2 => il2.FocusObject,
        WarThunderTelemetrySource wt => wt.Vehicle,
        _ => null
    };

    private void Publish()
    {
        var status = Compute();
        var previous = Interlocked.Exchange(ref _status, status);
        if (previous is not null && previous.State == status.State && previous.Reason == status.Reason &&
            previous.Aircraft == status.Aircraft && previous.Graph == status.Graph)
            return;
        JoyProLog.Info("FfbHost", "Status: " + status.StateName + " | " + status.Reason + " | aircraft " + (status.Aircraft ?? "-") + " | graph " + status.Graph +
                                  " | frames " + status.Frames.ToString(System.Globalization.CultureInfo.InvariantCulture) + " | " + status.Endpoint);
        StatusChanged?.Invoke(status);
    }

    private TelemetryFfbStatus Compute()
    {
        var endpoint = Endpoint();
        var frames = _source?.FramesPublished ?? 0;
        var runtime = _runtime;
        var gameSettings = _config.Game(_game.Id);
        TelemetryFfbState state;
        string reason;
        if (runtime is null)
        {
            state = TelemetryFfbState.Disabled;
            reason = "stopped";
        }
        else if (!_config.Enabled || !gameSettings.Enabled)
        {
            state = TelemetryFfbState.Disabled;
            reason = !_config.Enabled ? "idle - telemetry force feedback is off" : "idle - switched off for " + _game.Title;
        }
        else if (!_gameRunning && !_options.IgnoreGameProcess)
        {
            state = TelemetryFfbState.WaitingForGame;
            reason = "idle - waiting for " + string.Join(" / ", _game.ProcessNames.Select(n => n + ".exe"));
        }
        else if (runtime.State == FfbRuntimeState.Faulted)
        {
            state = TelemetryFfbState.Faulted;
            reason = "stopped after an error: " + (runtime.LastError?.Message ?? "unknown") + " (tried again in " +
                     Math.Max(1, _options.RetrySeconds).ToString("0", CultureInfo.InvariantCulture) + " s)";
        }
        else if (runtime.State == FfbRuntimeState.Active)
        {
            state = TelemetryFfbState.Active;
            reason = "active" + (_aircraft is null ? "" : " - " + _aircraft);
        }
        else if (_source is DcsTelemetrySource { ExportNotAllowed: true })
        {
            state = TelemetryFfbState.NoTelemetry;
            reason = "idle - the multiplayer server does not allow ownship export";
        }
        else
        {
            state = TelemetryFfbState.NoTelemetry;
            reason = "idle - no telemetry " + (_game.Transport == "HTTP" ? "from " : "on ") + endpoint;
        }

        return new TelemetryFfbStatus(state, reason, _game.Id, _aircraft, _graphName, frames, endpoint);
    }

    private string Endpoint()
    {
        if (_source is WarThunderTelemetrySource wt)
            return wt.BaseUri.ToString();
        var port = _udp?.Port ?? _config.PortFor(_game);
        return "UDP " + port.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>Is a game running? Looked up by process name (walks the process table: call it every few seconds at most).</summary>
public static class GameProcessWatch
{
    public static bool IsRunning(IEnumerable<string> names) => FindPid(names) is not null;

    public static int? FindPid(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            int? pid = null;
            foreach (var process in processes)
            {
                pid ??= process.Id;
                process.Dispose();
            }

            if (pid is not null)
                return pid;
        }

        return null;
    }
}
