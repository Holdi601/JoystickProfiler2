namespace JoyPro.Ffb.Core.Telemetry;

/// <summary>One value a source can deliver: what the blueprint editor lists and what a Telemetry node normalises.</summary>
/// <param name="Key">Stable key, e.g. "il2.eas" or "dcs.g". Saved in graphs and recordings.</param>
/// <param name="Min">Typical lower bound (used as the default normalisation range).</param>
/// <param name="Max">Typical upper bound.</param>
/// <param name="Origin">Where the value comes from in the game (IL-2 indicator id, DCS LoGet* call, ...).</param>
public sealed record TelemetryDescriptor(
    string Key,
    string Title,
    string Unit,
    double Min,
    double Max,
    string Category = "",
    string Origin = "");

/// <summary>A snapshot of values. Time is in seconds on the source's own time line (game time or recording time).</summary>
public sealed class TelemetryFrame
{
    private readonly Dictionary<string, double> _values;

    public TelemetryFrame(double time, IReadOnlyDictionary<string, double> values)
    {
        Time = time;
        _values = new Dictionary<string, double>(values, StringComparer.Ordinal);
    }

    public TelemetryFrame(double time, IEnumerable<KeyValuePair<string, double>> values)
    {
        Time = time;
        _values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pair in values)
            _values[pair.Key] = pair.Value;
    }

    public double Time { get; }
    public IReadOnlyDictionary<string, double> Values => _values;

    public bool TryGet(string key, out double value) => _values.TryGetValue(key, out value);
}

/// <summary>Anything that delivers telemetry to the graph: a game parser, a replay, a synthetic script.</summary>
public interface ITelemetrySource
{
    string Name { get; }

    /// <summary>The catalogue of keys this source knows (dynamic keys such as engine 5 may still work).</summary>
    IReadOnlyList<TelemetryDescriptor> Descriptors { get; }

    /// <summary>Latest value of a key. False when the key is unknown or no frame has arrived.</summary>
    bool TryGet(string key, out double value);

    /// <summary>The latest frame, or null before the first one.</summary>
    TelemetryFrame? Latest { get; }

    /// <summary>When the latest frame arrived, on the runtime clock (seconds). Null before the first frame.</summary>
    /// <remarks>The runtime compares this with its clock to silence the stick on stale telemetry.</remarks>
    double? LastReceiveTime { get; }
}

/// <summary>Monotonic time in seconds. The runtime and the sources share one clock so staleness is comparable.</summary>
public interface IFfbClock
{
    double Now { get; }
}

public sealed class StopwatchClock : IFfbClock
{
    private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
    public double Now => _watch.Elapsed.TotalSeconds;
}

/// <summary>A clock tests move by hand.</summary>
public sealed class ManualClock : IFfbClock
{
    private long _ticks;

    public ManualClock(double start = 0) => Now = start;

    public double Now
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _ticks));
        set => Interlocked.Exchange(ref _ticks, BitConverter.DoubleToInt64Bits(value));
    }

    public void Advance(double seconds) => Now += seconds;
}

/// <summary>Holds the latest frame of a source thread-safely; parsers and replays publish into it.</summary>
/// <remarks>
/// Writers (a UDP thread, a replay) swap one immutable reference; readers (the runtime thread) read it without
/// locks. <see cref="FrameReceived"/> lets a recorder tap every frame.
/// </remarks>
public class FrameTelemetrySource : ITelemetrySource
{
    private sealed record Snapshot(TelemetryFrame Frame, double ReceiveTime);

    private readonly IFfbClock _clock;
    private Snapshot? _latest;
    private long _framesPublished;
    private IReadOnlyList<TelemetryDescriptor> _descriptors;

    public FrameTelemetrySource(string name, IReadOnlyList<TelemetryDescriptor> descriptors, IFfbClock clock)
    {
        Name = name;
        _descriptors = descriptors;
        _clock = clock;
    }

    public string Name { get; }

    public IReadOnlyList<TelemetryDescriptor> Descriptors
    {
        get
        {
            var enricher = Enricher;
            return enricher is null ? _descriptors : [.. _descriptors, .. enricher.Descriptors];
        }
    }

    public IFfbClock Clock => _clock;

    /// <summary>Adds derived values to every published frame (the normalised aircraft model "ac.*").</summary>
    public ITelemetryEnricher? Enricher { get; set; }

    /// <summary>Frames published since the source was made.</summary>
    public long FramesPublished => Interlocked.Read(ref _framesPublished);
    public TelemetryFrame? Latest => Volatile.Read(ref _latest)?.Frame;
    public double? LastReceiveTime => Volatile.Read(ref _latest)?.ReceiveTime;

    public event Action<TelemetryFrame>? FrameReceived;

    public bool TryGet(string key, out double value)
    {
        var latest = Volatile.Read(ref _latest);
        if (latest is not null && latest.Frame.TryGet(key, out value))
            return true;
        value = 0;
        return false;
    }

    public void Publish(TelemetryFrame frame)
    {
        var enricher = Enricher;
        if (enricher is not null)
            frame = enricher.Enrich(frame);
        Volatile.Write(ref _latest, new Snapshot(frame, _clock.Now));
        Interlocked.Increment(ref _framesPublished);
        FrameReceived?.Invoke(frame);
    }

    /// <summary>Forget the latest frame (the game paused, the mission ended): the source reads as stale.</summary>
    public void Clear()
    {
        Volatile.Write(ref _latest, null);
        Enricher?.Reset();
    }

    protected void SetDescriptors(IReadOnlyList<TelemetryDescriptor> descriptors) => _descriptors = descriptors;
}

/// <summary>Adds values derived from a game's raw keys to each frame (see <see cref="AircraftTelemetry"/>).</summary>
public interface ITelemetryEnricher
{
    IReadOnlyList<TelemetryDescriptor> Descriptors { get; }

    TelemetryFrame Enrich(TelemetryFrame frame);

    /// <summary>Forgets derived state (counters, movement detection): a new mission or aircraft.</summary>
    void Reset();
}
