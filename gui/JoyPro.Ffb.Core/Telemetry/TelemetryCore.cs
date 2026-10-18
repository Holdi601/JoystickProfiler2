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

    // ------------------------------------------------------------------ for the session recorder (docs/telemetry-recording.md)

    private readonly object _infoGate = new();
    private Dictionary<string, string> _info = new(StringComparer.Ordinal);

    /// <summary>Every raw unit of input as it arrived (a datagram, an HTTP answer, a shared memory snapshot, a journal
    /// line). Only raised while someone listens: a source without a recorder copies nothing.</summary>
    public event Action<TelemetryRawPacket>? RawReceived;

    /// <summary>Discrete things (aircraft change, mission start/end, weapon release, journal events).</summary>
    public event Action<TelemetryEvent>? EventRaised;

    /// <summary>True while a recorder listens for raw input (a source can skip work otherwise).</summary>
    public bool WantsRaw => RawReceived is not null;

    /// <summary>Facts about the current session (<see cref="TelemetryInfoKeys"/>: aircraft, map, mission, pilot, server ...).</summary>
    public IReadOnlyDictionary<string, string> SessionInfo
    {
        get
        {
            lock (_infoGate)
                return new Dictionary<string, string>(_info, StringComparer.Ordinal);
        }
    }

    /// <summary>Where the standard quantities (<see cref="TelemetryNav"/>) are in this source's keys: the ACMI export
    /// takes position and attitude from them. Empty = the source has no position.</summary>
    public virtual IReadOnlyList<TelemetryNavMapping> NavMappings => [];

    /// <summary>Hands one raw unit of input to the recorder (copied; nothing happens without a listener).</summary>
    protected void PublishRaw(string stream, ReadOnlySpan<byte> data)
    {
        var handler = RawReceived;
        if (handler is null)
            return;
        handler(new TelemetryRawPacket(_clock.Now, stream, data.ToArray()));
    }

    /// <summary>Raises an event (<see cref="TelemetryEventKinds"/> or a game's own kind, e.g. "il2.gun_fire").</summary>
    protected void RaiseEvent(string kind, string text = "") => EventRaised?.Invoke(new TelemetryEvent(_clock.Now, kind, text ?? ""));

    /// <summary>Sets a session fact. A change raises an event: <see cref="TelemetryEventKinds.Aircraft"/> or
    /// <see cref="TelemetryEventKinds.Map"/> for those keys, else <see cref="TelemetryEventKinds.Info"/> ("key=value").
    /// Null or empty removes the fact (no event).</summary>
    protected void SetInfo(string key, string? value)
    {
        bool changed;
        lock (_infoGate)
        {
            if (string.IsNullOrEmpty(value))
            {
                if (!_info.ContainsKey(key))
                    return;
                var copy = new Dictionary<string, string>(_info, StringComparer.Ordinal);
                copy.Remove(key);
                _info = copy;
                return;
            }

            changed = !_info.TryGetValue(key, out var old) || old != value;
            if (changed)
            {
                var copy = new Dictionary<string, string>(_info, StringComparer.Ordinal) { [key] = value };
                _info = copy;
            }
        }

        if (!changed)
            return;
        switch (key)
        {
            case TelemetryInfoKeys.Aircraft:
                RaiseEvent(TelemetryEventKinds.Aircraft, value!);
                break;
            case TelemetryInfoKeys.Map:
                RaiseEvent(TelemetryEventKinds.Map, value!);
                break;
            default:
                RaiseEvent(TelemetryEventKinds.Info, key + "=" + value);
                break;
        }
    }
}

/// <summary>One raw unit of input as it arrived. <paramref name="Time"/> is the source's clock (seconds).</summary>
/// <param name="Stream">What it is: "udp", "http:/state", "shm:FlightData", "file:Status.json", "simconnect" ...</param>
public sealed record TelemetryRawPacket(double Time, string Stream, byte[] Data);

/// <summary>Something that happened at one moment. <paramref name="Time"/> is the source's clock (seconds).</summary>
public sealed record TelemetryEvent(double Time, string Kind, string Text = "");

/// <summary>Event kinds every source may raise (a game's own kinds carry its prefix, e.g. "il2.hit", "ed.FSDJump").</summary>
public static class TelemetryEventKinds
{
    /// <summary>A flight / mission / round begins (optional: the first frame starts a recording anyway).</summary>
    public const string SessionStart = "session_start";

    /// <summary>The flight / mission / round ended: the recorder closes the session (text = why).</summary>
    public const string SessionEnd = "session_end";

    /// <summary>The vehicle changed (text = its name).</summary>
    public const string Aircraft = "aircraft";

    /// <summary>The map, theatre or star system changed (text = its name).</summary>
    public const string Map = "map";

    /// <summary>Another session fact changed (text = "key=value").</summary>
    public const string Info = "info";

    /// <summary>A text from the game (chat, mission message, kill message).</summary>
    public const string Message = "message";

    /// <summary>A snapshot of the OTHER objects the game reports (not the player's own vehicle, which is in the frames).</summary>
    /// <remarks>Text: one object per line (separated by '\n'), fields separated by '|':
    /// <c>id|type|name|coalition|lat|lon|alt|roll|pitch|yaw|u|v</c>. id = the game's object id (stable while the object
    /// lives); type in Tacview words ("Air+FixedWing", "Air+Rotorcraft", "Ground+Vehicle", "Sea+Watercraft",
    /// "Weapon+Missile", "Ground+Static+Building", "Navaid+Static+Bullseye"); coalition free text ("Allies", "Enemies",
    /// "Red", "Blue", "Neutral"); lat/lon degrees, alt m above sea level, roll/pitch/yaw degrees (yaw = true heading);
    /// u/v m east/north for flat-world games. An empty field = unknown. Each event is the full set at that moment: an
    /// object missing from the next snapshot is gone. "|" and line breaks never appear inside a field (replace them).</remarks>
    public const string Objects = "objects";

    /// <summary>Formats one line of an <see cref="Objects"/> snapshot (null = unknown field).</summary>
    public static string ObjectLine(string id, string? type, string? name, string? coalition, double? lat, double? lon, double? alt,
        double? roll = null, double? pitch = null, double? yaw = null, double? u = null, double? v = null)
    {
        static string Clean(string? s) => (s ?? "").Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ');
        static string Num(double? d) => d is { } x && double.IsFinite(x) ? x.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture) : "";
        return string.Join('|', Clean(id), Clean(type), Clean(name), Clean(coalition), Num(lat), Num(lon), Num(alt), Num(roll), Num(pitch), Num(yaw), Num(u), Num(v));
    }
}

/// <summary>Keys of <see cref="FrameTelemetrySource.SessionInfo"/>.</summary>
public static class TelemetryInfoKeys
{
    public const string Aircraft = "aircraft";
    public const string Map = "map";
    public const string Mission = "mission";
    public const string Pilot = "pilot";
    public const string Server = "server";
    public const string GameVersion = "game_version";

    /// <summary>Flat-world games: the latitude / longitude (degrees) that local x/y 0/0 is placed at for ACMI.</summary>
    public const string ReferenceLatitude = "ref_lat";
    public const string ReferenceLongitude = "ref_lon";
}

/// <summary>Where a standard quantity is in a source's keys: value = raw * <paramref name="Scale"/> + <paramref name="Offset"/>.</summary>
/// <param name="Target">One of <see cref="TelemetryNav"/>.</param>
public sealed record TelemetryNavMapping(string Target, string Key, double Scale = 1, double Offset = 0);

/// <summary>Standard quantities for the ACMI export (units as Tacview's ACMI 2.2 expects them).</summary>
public static class TelemetryNav
{
    public const string Longitude = "lon";          // degrees, east positive
    public const string Latitude = "lat";           // degrees, north positive
    public const string Altitude = "alt";           // m above sea level
    public const string Roll = "roll";              // degrees, right wing down positive
    public const string Pitch = "pitch";            // degrees, nose up positive
    public const string Yaw = "yaw";                // degrees, true heading of the nose, clockwise from north
    public const string U = "u";                    // m east (flat-world games: their own x)
    public const string V = "v";                    // m north (flat-world games: their own y)
    public const string Heading = "heading";        // degrees, flight path heading (if it differs from yaw)
    public const string Agl = "agl";                // m above ground
    public const string Ias = "ias";                // m/s
    public const string Tas = "tas";                // m/s
    public const string Mach = "mach";
    public const string Aoa = "aoa";                // degrees
    public const string Aos = "aos";                // degrees
    public const string VerticalG = "g";            // G
    public const string LongitudinalG = "gx";       // G
    public const string LateralG = "gz";            // G
    public const string Throttle = "throttle";      // 0..1 (more with afterburner)
    public const string Gear = "gear";              // 0..1
    public const string Flaps = "flaps";            // 0..1
    public const string Airbrakes = "airbrakes";    // 0..1
    public const string FuelKg = "fuel_kg";         // kg
    public const string VerticalSpeed = "vs";       // m/s

    public const double RadToDeg = 180 / Math.PI;
    public const double FeetToMetres = 0.3048;
    public const double KnotsToMetresPerSecond = 0.514444;
    public const double PoundsToKg = 0.45359237;
}

/// <summary>Adds values derived from a game's raw keys to each frame (see <see cref="AircraftTelemetry"/>).</summary>
public interface ITelemetryEnricher
{
    IReadOnlyList<TelemetryDescriptor> Descriptors { get; }

    TelemetryFrame Enrich(TelemetryFrame frame);

    /// <summary>Forgets derived state (counters, movement detection): a new mission or aircraft.</summary>
    void Reset();
}
