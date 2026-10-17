using System.Diagnostics;
using JoyPro.Ffb.Core.Graph;
using JoyPro.Ffb.Core.Output;
using JoyPro.Ffb.Core.Telemetry;

namespace JoyPro.Ffb.Core.Runtime;

public sealed class FfbRuntimeOptions
{
    public double TickRateHz { get; init; } = 100;

    /// <summary>Telemetry older than this silences the stick (the game paused, crashed or stopped sending).</summary>
    public double StaleAfterSeconds { get; init; } = 0.3;

    /// <summary>Duration of every effect; refreshed each tick, so a stuck loop goes quiet within this time.</summary>
    public double EffectDurationSeconds { get; init; } = 0.1;

    /// <summary>Gain and gain cap per device id; devices not listed use <see cref="DefaultDevice"/>.</summary>
    public IReadOnlyDictionary<string, FfbDeviceSettings> Devices { get; init; } = new Dictionary<string, FfbDeviceSettings>();

    public FfbDeviceSettings DefaultDevice { get; init; } = FfbDeviceSettings.Default;

    /// <summary>How long the loop sleeps between checks while there is nothing to send (no telemetry, disabled).</summary>
    /// <remarks>A telemetry frame (<see cref="FfbRuntime.Wake"/>) ends the wait at once, so this only bounds how late
    /// a state change without telemetry (disable, stale) is noticed.</remarks>
    public double IdleWaitSeconds { get; init; } = 1;

    /// <summary>Graph evaluations per second while idle with <see cref="FfbRuntime.PreviewWhileIdle"/> (the editor's live view).</summary>
    public double PreviewRateHz { get; init; } = 10;
}

/// <summary>Who centres the stick while JoyPro drives it.</summary>
public enum FfbCentringMode
{
    /// <summary>Whatever the graph's Spring nodes say (no spring of JoyPro's own).</summary>
    GraphOnly,

    /// <summary>JoyPro provides centring: the graph's springs, a plain centring spring on every axis without one, and
    /// the last springs (with their trim) are held while the telemetry pauses, as long as JoyPro owns the device.</summary>
    Ensure,

    /// <summary>JoyPro sends no springs at all: the device's own centring (firmware, driver) is used.</summary>
    Off
}

/// <summary>The centring spring settings (<see cref="FfbRuntime.Centring"/>).</summary>
/// <param name="Stiffness">Coefficient of the plain centring spring, 0..1.</param>
/// <param name="Saturation">Its force cap, 0..1.</param>
public sealed record FfbCentring(FfbCentringMode Mode = FfbCentringMode.GraphOnly, double Stiffness = 0.3, double Saturation = 0.5)
{
    public static readonly FfbCentring GraphOnly = new();

    /// <summary>The tag of the springs JoyPro adds itself (never scaled by the per-game effect switches).</summary>
    public const string Tag = "centring";
}

public enum FfbRuntimeState
{
    /// <summary>Not enabled or no graph: nothing is sent.</summary>
    Idle,

    /// <summary>Enabled, but the telemetry is missing or stale: silent.</summary>
    Silent,

    /// <summary>Forces are being sent.</summary>
    Active,

    /// <summary>An error stopped all forces and switched the runtime off; enable again to retry.</summary>
    Faulted
}

/// <summary>The force loop: telemetry -> graph -> mixer -> backend on its own thread.</summary>
/// <remarks>
/// Safety rules (each one has a test):
///  - Never enabled by loading: <see cref="LoadGraph"/> always leaves <see cref="Enabled"/> false.
///  - Finite effects: every command carries <see cref="FfbRuntimeOptions.EffectDurationSeconds"/> and is sent again
///    each tick; a hung loop or killed process leaves the device silent after that time.
///  - Stale telemetry (older than <see cref="FfbRuntimeOptions.StaleAfterSeconds"/> or none) silences the output
///    with ONE STOP ALL (then the device is left alone, as in the Forza app), and the graph state is reset before
///    forces resume (no burst from old counters).
///  - Exception to the above, only with <see cref="FfbCentring"/> mode Ensure ("JoyPro provides the centring"): when
///    the telemetry goes stale while JoyPro owns the device, the springs of the last output (with their trim) and a
///    plain centring spring on every other axis are held instead of a STOP ALL -- springs are passive, and a stick
///    whose firmware spring the user switched off must not go slack while the game pauses. Disable, Stop, Dispose and
///    faults still send STOP ALL and release the device.
///  - Per-device gain cap (<see cref="FfbMixer"/>).
///  - STOP ALL on disable, on <see cref="Stop"/>, on <see cref="Dispose"/> and on any exception in a tick (which
///    also switches the runtime off: <see cref="FfbRuntimeState.Faulted"/>).
/// Cost: while forces are sent the loop wakes <see cref="FfbRuntimeOptions.TickRateHz"/> times a second on a
/// high-resolution waitable timer (no busy wait, no raised system timer resolution). Without fresh telemetry it
/// blocks on an event that the next telemetry frame sets (<see cref="Wake"/>), checking at most once per
/// <see cref="FfbRuntimeOptions.IdleWaitSeconds"/>, so an idle runtime costs no measurable CPU.
/// The loop never touches UI objects; the UI reads <see cref="State"/>, <see cref="LastOutput"/>,
/// <see cref="NodeValues"/> or listens to <see cref="Faulted"/>.
/// </remarks>
public sealed class FfbRuntime : IDisposable
{
    private static readonly IReadOnlyDictionary<string, double> NoScales = new Dictionary<string, double>();
    private static readonly IReadOnlyDictionary<string, double> NoValues = new Dictionary<string, double>();

    private readonly object _gate = new();
    private readonly IFfbBackend _backend;
    private readonly IFfbClock _clock;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stopRequested;
    private FfbGraphEvaluator? _evaluator;
    private bool _enabled;
    private bool _outputActive;
    private bool _needsReset = true;
    private double _lastSend = double.NegativeInfinity;
    private double _lastPreview = double.NegativeInfinity;
    private double _lastCapture = double.NegativeInfinity;
    private IReadOnlyList<FfbEffectCommand> _lastOutput = [];
    private IReadOnlyDictionary<string, double> _effectScales = NoScales;
    private IReadOnlyDictionary<string, double> _nodeValues = NoValues;
    private FfbCentring _centring = FfbCentring.GraphOnly;
    private double _lastHold = double.NegativeInfinity;

    public FfbRuntime(IFfbBackend backend, IFfbClock clock, FfbRuntimeOptions? options = null)
    {
        _backend = backend;
        _clock = clock;
        Options = options ?? new FfbRuntimeOptions();
    }

    public FfbRuntimeOptions Options { get; }
    public IFfbBackend Backend => _backend;
    public ITelemetrySource? Source { get; set; }

    /// <summary>Buttons and the stick position for input and trim nodes; polled once per evaluation on the loop thread.</summary>
    public JoyPro.Ffb.Core.Input.IFfbInputs? Inputs { get; set; }

    /// <summary>Who centres the stick (default: the graph only). Takes effect on the next tick.</summary>
    public FfbCentring Centring
    {
        get => Volatile.Read(ref _centring);
        set => Volatile.Write(ref _centring, value ?? FfbCentring.GraphOnly);
    }

    /// <summary>True while the last springs are held during a telemetry pause (<see cref="FfbCentringMode.Ensure"/>).</summary>
    public bool Holding { get; private set; }
    public FfbRuntimeState State { get; private set; } = FfbRuntimeState.Idle;
    public Exception? LastError { get; private set; }
    public long Ticks { get; private set; }

    /// <summary>Ticks that evaluated the graph (active or preview); <see cref="Ticks"/> counts idle checks too.</summary>
    public long Evaluations { get; private set; }

    /// <summary>Commands dropped because the device does not exist or lacks the axis/effect (shown in the editor).</summary>
    public long DroppedCommands { get; private set; }

    public bool IsRunning => _thread is not null;

    /// <summary>Keep evaluating the graph (without output) while there is no telemetry, for the editor's live view.</summary>
    public bool PreviewWhileIdle { get; set; }

    /// <summary>Copy every node's value into <see cref="NodeValues"/> (at most 10 times a second); costs an allocation.</summary>
    public bool CaptureNodeValues { get; set; }

    /// <summary>The first output of every node after the last capture (empty unless <see cref="CaptureNodeValues"/>).</summary>
    public IReadOnlyDictionary<string, double> NodeValues => Volatile.Read(ref _nodeValues);

    /// <summary>Strength per effect name (the sinks' <see cref="EffectSinkNode.Effect"/>): 0 switches it off, 1 is
    /// unchanged. Effects not listed play at 1.</summary>
    public IReadOnlyDictionary<string, double> EffectScales
    {
        get => Volatile.Read(ref _effectScales);
        set => Volatile.Write(ref _effectScales, value ?? NoScales);
    }

    /// <summary>Replaces <see cref="FfbRuntimeOptions.DefaultDevice"/> (the user's strength and force cap, reloadable).</summary>
    public FfbDeviceSettings? DeviceSettingsOverride { get; set; }

    /// <summary>The evaluator of the loaded copy (read its node outputs for previews; do not tick it yourself).</summary>
    public FfbGraphEvaluator? Evaluator
    {
        get
        {
            lock (_gate)
                return _evaluator;
        }
    }

    /// <summary>The effects sent in the last tick (after mixing and capping).</summary>
    public IReadOnlyList<FfbEffectCommand> LastOutput
    {
        get
        {
            lock (_gate)
                return _lastOutput;
        }
    }

    public event Action<Exception>? Faulted;

    /// <summary>Raised on the loop thread when <see cref="State"/> changes.</summary>
    public event Action<FfbRuntimeState>? StateChanged;

    /// <summary>The user's switch. Turning it off sends STOP ALL; turning it on starts from a fresh graph state.</summary>
    public bool Enabled
    {
        get
        {
            lock (_gate)
                return _enabled;
        }
        set
        {
            lock (_gate)
            {
                if (_enabled == value)
                    return;
                _enabled = value;
                if (value)
                {
                    _needsReset = true;
                    LastError = null;
                }
                else
                {
                    SilenceLocked(force: true);
                    SetState(FfbRuntimeState.Idle);
                }
            }

            Wake();
        }
    }

    /// <summary>Uses a private copy of <paramref name="graph"/>. Always leaves the runtime disabled.</summary>
    public IReadOnlyList<FfbConnection> LoadGraph(FfbGraph graph)
    {
        var copy = FfbGraphPersistence.Clone(graph);
        copy.Enabled = false;
        var evaluator = new FfbGraphEvaluator(copy);
        lock (_gate)
        {
            _enabled = false;
            SilenceLocked(force: _outputActive);
            _evaluator = evaluator;
            _needsReset = true;
            SetState(FfbRuntimeState.Idle);
        }

        return evaluator.BrokenConnections;
    }

    /// <summary>Swaps the graph while running and keeps <see cref="Enabled"/> (an aircraft change, an edited graph).</summary>
    public IReadOnlyList<FfbConnection> ReplaceGraph(FfbGraph graph)
    {
        var copy = FfbGraphPersistence.Clone(graph);
        copy.Enabled = false;
        var evaluator = new FfbGraphEvaluator(copy);
        lock (_gate)
        {
            SilenceLocked(force: _outputActive);
            _evaluator = evaluator;
            _needsReset = true;
        }

        Wake();
        return evaluator.BrokenConnections;
    }

    /// <summary>Uses an evaluator as it is (tests that need a graph the editor would refuse, e.g. with a loop).</summary>
    internal void LoadEvaluator(FfbGraphEvaluator evaluator)
    {
        lock (_gate)
        {
            _enabled = false;
            _evaluator = evaluator;
            _needsReset = true;
            SetState(FfbRuntimeState.Idle);
        }
    }

    /// <summary>Ends the loop's idle wait now (a telemetry frame arrived, a setting changed). Cheap; any thread.</summary>
    public void Wake()
    {
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>One loop iteration. The thread calls it; tests call it directly with a <see cref="ManualClock"/>.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            Ticks++;
            if (!_enabled || _evaluator is null)
            {
                if (_outputActive)
                    SilenceLocked(force: true);
                if (State != FfbRuntimeState.Faulted)
                    SetState(FfbRuntimeState.Idle);
                return;
            }

            try
            {
                var now = _clock.Now;
                var source = Source;
                var received = source?.LastReceiveTime;
                if (source is null || received is null || now - received.Value > Options.StaleAfterSeconds)
                {
                    // Going idle: one STOP ALL, then the device is left alone -- unless JoyPro provides the centring
                    // and owns the device: then only the springs stay (passive, with their trim), refreshed slowly.
                    if (_outputActive && Centring.Mode == FfbCentringMode.Ensure)
                    {
                        HoldLocked(now);
                    }
                    else if (_outputActive || State != FfbRuntimeState.Silent)
                    {
                        SilenceLocked(force: true);
                    }

                    _needsReset = true;
                    SetState(FfbRuntimeState.Silent);
                    if (PreviewWhileIdle && now - _lastPreview >= 1 / Math.Max(0.1, Options.PreviewRateHz) - 1e-6)
                    {
                        _lastPreview = now;
                        PollInputs();
                        _evaluator.Evaluate(source, now, Inputs);
                        Evaluations++;
                        CaptureLocked(now, force: true);
                    }

                    return;
                }

                if (_needsReset)
                {
                    _evaluator.Reset();
                    _needsReset = false;
                }

                PollInputs();
                var commands = _evaluator.Evaluate(source, now, Inputs);
                Evaluations++;
                CaptureLocked(now, force: false);
                var routed = ApplyCentring(Route(commands));
                Holding = false;
                var maxRate = Math.Max(1, _backend.Devices.Count == 0 ? Options.TickRateHz : _backend.Devices.Min(d => d.MaxUpdateRateHz));
                var rate = Math.Max(1, Math.Min(Options.TickRateHz, maxRate));
                var duration = Math.Max(Options.EffectDurationSeconds, 3 / rate);
                var output = FfbMixer.Mix(routed, SettingsFor, duration);

                if (now - _lastSend >= 1 / maxRate - 1e-6)
                {
                    _backend.Apply(now, output);
                    _lastSend = now;
                    _outputActive = true;
                }

                _lastOutput = output;
                SetState(FfbRuntimeState.Active);
            }
            catch (Exception ex)
            {
                LastError = ex;
                _enabled = false;
                SilenceLocked(force: true);
                SetState(FfbRuntimeState.Faulted);
                ThreadPool.QueueUserWorkItem(_ => Faulted?.Invoke(ex));
            }
        }
    }

    /// <summary>Starts the loop thread (the runtime stays disabled until <see cref="Enabled"/> is set).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null)
                return;
            _stopRequested = false;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "JoyPro FFB loop",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
        }
    }

    /// <summary>Stops the loop thread and sends STOP ALL.</summary>
    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
            _stopRequested = true;
        }

        Wake();
        thread?.Join(TimeSpan.FromSeconds(2));
        lock (_gate)
        {
            SilenceLocked(force: true);
            if (State != FfbRuntimeState.Faulted)
                SetState(FfbRuntimeState.Idle);
        }
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }

    private void Loop()
    {
        var period = 1.0 / Math.Max(1, Options.TickRateHz);
        using var timer = PeriodicWaiter.Create(period);
        while (!_stopRequested)
        {
            Tick();
            if (_stopRequested)
                break;
            if (State == FfbRuntimeState.Active)
            {
                timer.Wait();
                continue;
            }

            timer.Pause();
            var wait = PreviewWhileIdle ? 1 / Math.Max(0.1, Options.PreviewRateHz) : Math.Max(0.01, Options.IdleWaitSeconds);
            _wake.WaitOne(TimeSpan.FromSeconds(wait));
        }
    }

    private void CaptureLocked(double now, bool force)
    {
        if (!CaptureNodeValues || _evaluator is null || (!force && now - _lastCapture < 0.1))
            return;
        _lastCapture = now;
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        _evaluator.CopyNodeValues(values);
        Volatile.Write(ref _nodeValues, values);
    }

    private void SetState(FfbRuntimeState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private List<FfbEffectCommand> Route(List<FfbEffectCommand> commands)
    {
        var devices = _backend.Devices;
        var scales = EffectScales;
        var routed = new List<FfbEffectCommand>(commands.Count);
        foreach (var command in commands)
        {
            var target = command.Device.Length == 0 && devices.Count > 0 ? command with { Device = devices[0].DeviceId } : command;
            var device = devices.FirstOrDefault(d => d.DeviceId == target.Device);
            if (device is null || !device.Supports(target))
            {
                DroppedCommands++;
                continue;
            }

            if (target.Tag.Length > 0 && scales.TryGetValue(target.Tag, out var scale))
            {
                if (!(scale > 0))
                    continue;
                if (scale != 1)
                    target = FfbMixer.Scale(target, scale);
            }

            routed.Add(target);
        }

        return routed;
    }

    private void PollInputs() => Inputs?.Poll();

    /// <summary>Off: no springs; Ensure: a plain centring spring on every spring-capable axis that has none.</summary>
    internal List<FfbEffectCommand> ApplyCentring(List<FfbEffectCommand> routed)
    {
        var centring = Centring;
        switch (centring.Mode)
        {
            case FfbCentringMode.Off:
                routed.RemoveAll(c => c.Type == FfbEffectType.Spring);
                return routed;
            case FfbCentringMode.Ensure:
                foreach (var device in _backend.Devices)
                {
                    if (!device.SupportedEffects.Contains(FfbEffectType.Spring))
                        continue;
                    foreach (var axis in device.Axes)
                    {
                        if (!routed.Exists(c => c.Type == FfbEffectType.Spring && c.Device == device.DeviceId && c.Axis == axis))
                            routed.Add(PlainSpring(device.DeviceId, axis, centring));
                    }
                }

                return routed;
            default:
                return routed;
        }
    }

    private static FfbEffectCommand PlainSpring(string device, FfbAxis axis, FfbCentring centring) =>
        new(device, axis, FfbEffectType.Spring, Centre: 0, Coefficient: Math.Clamp(centring.Stiffness, 0, 1),
            Saturation: Math.Clamp(centring.Saturation, 0, 1), Tag: FfbCentring.Tag);

    /// <summary>Seconds between refreshes of the held springs (their duration is three times that).</summary>
    internal const double HoldRefreshSeconds = 0.5;

    // The springs of the last output (trim included), plus a plain spring where an axis had none; everything else stops.
    private void HoldLocked(double now)
    {
        Holding = true;
        if (now - _lastHold < HoldRefreshSeconds - 1e-6)
            return;
        var hold = new List<FfbEffectCommand>();
        foreach (var command in _lastOutput)
        {
            if (command.Type == FfbEffectType.Spring)
                hold.Add(command);
        }

        var centring = Centring;
        foreach (var device in _backend.Devices)
        {
            if (!device.SupportedEffects.Contains(FfbEffectType.Spring))
                continue;
            foreach (var axis in device.Axes)
            {
                if (!hold.Exists(c => c.Device == device.DeviceId && c.Axis == axis))
                    hold.Add(PlainSpring(device.DeviceId, axis, centring));
            }
        }

        // The idle loop wakes about once per IdleWaitSeconds: the held springs outlast three of those waits.
        var duration = Math.Max(Options.EffectDurationSeconds, 3 * Math.Max(HoldRefreshSeconds, Options.IdleWaitSeconds));
        for (var i = 0; i < hold.Count; i++)
            hold[i] = hold[i] with { DurationSeconds = duration };
        _backend.Apply(now, hold);
        _lastHold = now;
        _lastSend = now;
        _lastOutput = hold;
    }

    private FfbDeviceSettings SettingsFor(string device) =>
        Options.Devices.TryGetValue(device, out var settings) ? settings : DeviceSettingsOverride ?? Options.DefaultDevice;

    private void SilenceLocked(bool force)
    {
        if (!force && !_outputActive)
            return;
        _outputActive = false;
        Holding = false;
        _lastOutput = [];
        _lastSend = double.NegativeInfinity;
        _lastHold = double.NegativeInfinity;
        try
        {
            _backend.StopAll();
        }
        catch (Exception ex)
        {
            LastError ??= ex;
        }
    }
}
