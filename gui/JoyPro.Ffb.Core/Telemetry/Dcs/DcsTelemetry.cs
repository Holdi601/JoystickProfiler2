using System.Globalization;
using System.Text;

namespace JoyPro.Ffb.Core.Telemetry.Dcs;

/// <summary>The DCS telemetry line protocol of JoyPro's force feedback hook (UDP 127.0.0.1:<see cref="Port"/>).</summary>
/// <remarks>
/// The hook (Saved Games\DCS\Scripts\JoyProFfb.lua, installed by "engine ffb telemetry setup dcs") sends
/// "plane &lt;unit type&gt;" when the aircraft changes, "exit" when the mission ends, and at most 100 times a second
/// of model time one line of key=value pairs:
/// <code>
/// t=12.340;ias=154.2;aoa=0.081;g=3.42;shells=412
/// </code>
/// Rules: pairs separated by ';', key and value by '='; keys are lower case [a-z0-9_]; numbers use '.' and may use
/// exponents; "t" (LoGetModelTime) is required and comes first; unknown keys are accepted (as "dcs.&lt;key&gt;") so
/// the hook can grow without a JoyPro update; a pair that does not parse is skipped, not the whole line. Several
/// lines may share one datagram, separated by '\n'. The keys and their LoGet* sources are <see cref="Channels"/>.
/// The overlay hook (port 1992) is a separate file: both can be installed, and the GUI's overlay listener and the
/// force feedback process never compete for one port.
/// </remarks>
public static class DcsTelemetryProtocol
{
    public const int Port = 41992;
    public const string KeyPrefix = "dcs.";

    public sealed record Channel(string WireKey, string Title, string Unit, double Min, double Max, string Category, string LoGet)
    {
        public string Key => KeyPrefix + WireKey;
        public TelemetryDescriptor Descriptor => new(Key, Title, Unit, Min, Max, Category, LoGet);
    }

    public static IReadOnlyList<Channel> Channels { get; } =
    [
        new("t", "Model time", "s", 0, 86400, "Time", "LoGetModelTime()"),
        new("ias", "Indicated airspeed", "m/s", 0, 400, "Flight", "LoGetIndicatedAirSpeed()"),
        new("tas", "True airspeed", "m/s", 0, 700, "Flight", "LoGetTrueAirSpeed()"),
        new("mach", "Mach number", "", 0, 2.5, "Flight", "LoGetMachNumber()"),
        new("aoa", "Angle of attack", "rad", -0.5, 0.5, "Flight", "LoGetAngleOfAttack()"),
        new("aos", "Angle of sideslip", "rad", -0.5, 0.5, "Flight", "LoGetAngleOfSideSlip()"),
        new("g", "G load (normal)", "G", -3, 9, "Flight", "LoGetAccelerationUnits().y"),
        new("gx", "Longitudinal acceleration", "G", -3, 3, "Flight", "LoGetAccelerationUnits().x"),
        new("gz", "Lateral acceleration", "G", -3, 3, "Flight", "LoGetAccelerationUnits().z"),
        new("vv", "Vertical velocity", "m/s", -150, 150, "Flight", "LoGetVerticalVelocity()"),
        new("alt", "Altitude above sea level", "m", 0, 20000, "Flight", "LoGetAltitudeAboveSeaLevel()"),
        new("agl", "Altitude above ground", "m", 0, 20000, "Flight", "LoGetAltitudeAboveGroundLevel()"),
        new("pitch", "Pitch", "rad", -Math.PI / 2, Math.PI / 2, "Attitude", "LoGetADIPitchBankYaw() #1"),
        new("bank", "Bank", "rad", -Math.PI, Math.PI, "Attitude", "LoGetADIPitchBankYaw() #2"),
        new("yaw", "Yaw", "rad", -Math.PI, Math.PI, "Attitude", "LoGetADIPitchBankYaw() #3"),
        new("wx", "Roll rate", "rad/s", -5, 5, "Attitude", "LoGetAngularVelocity().x"),
        new("wy", "Yaw rate", "rad/s", -5, 5, "Attitude", "LoGetAngularVelocity().y"),
        new("wz", "Pitch rate", "rad/s", -5, 5, "Attitude", "LoGetAngularVelocity().z"),
        new("rpm_l", "Engine RPM left", "%", 0, 110, "Engine", "LoGetEngineInfo().RPM.left"),
        new("rpm_r", "Engine RPM right", "%", 0, 110, "Engine", "LoGetEngineInfo().RPM.right"),
        new("gear", "Gear position", "0..1", 0, 1, "Mechanics", "LoGetMechInfo().gear.value"),
        new("flaps", "Flaps position", "0..1", 0, 1, "Mechanics", "LoGetMechInfo().flaps.value"),
        new("speedbrake", "Speed brake position", "0..1", 0, 1, "Mechanics", "LoGetMechInfo().speedbrakes.value"),
        new("shells", "Cannon shells left", "rounds", 0, 2000, "Weapons", "LoGetPayloadInfo().Cannon.shells"),
        new("rod_l", "Left main gear strut", "", 0, 1, "Mechanics", "LoGetMechInfo().gear.main.left.rod"),
        new("rod_r", "Right main gear strut", "", 0, 1, "Mechanics", "LoGetMechInfo().gear.main.right.rod"),
        new("rod_n", "Nose gear strut", "", 0, 1, "Mechanics", "LoGetMechInfo().gear.main.nose.rod"),
        new("gs", "Ground speed", "m/s", 0, 400, "Flight", "LoGetVectorVelocity() horizontal length"),
        new("stall", "Stall warning", "0/1", 0, 1, "Flight", "LoGetMCPState().StallSignalization"),
        new("fail", "Failed systems", "", 0, 20, "Damage", "LoGetMCPState() failures that are true"),
    ];

    public static IReadOnlyList<TelemetryDescriptor> Descriptors { get; } = Channels.Select(c => c.Descriptor).ToArray();

    /// <summary>Parses one telemetry line. False for "plane"/"exit" lines, empty lines and lines without "t".</summary>
    public static bool TryParseFrame(string line, out TelemetryFrame? frame)
    {
        frame = null;
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        double? time = null;
        foreach (var part in line.Trim().Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = part[..eq].Trim();
            // A decimal comma (a Lua state with a German C locale) is read as a point: ';' and '=' separate the pairs.
            if (!IsKey(key) ||
                !double.TryParse(part[(eq + 1)..].Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value))
                continue;
            if (key == "t")
                time = value;
            values[KeyPrefix + key] = value;
        }

        if (time is null)
            return false;
        frame = new TelemetryFrame(time.Value, values);
        return true;
    }

    /// <summary>Builds a line (tests; the Lua hook writes the same text).</summary>
    public static string FormatFrame(double time, IEnumerable<KeyValuePair<string, double>> values)
    {
        var sb = new StringBuilder("t=").Append(time.ToString("0.###", CultureInfo.InvariantCulture));
        foreach (var pair in values)
        {
            var key = pair.Key.StartsWith(KeyPrefix, StringComparison.Ordinal) ? pair.Key[KeyPrefix.Length..] : pair.Key;
            if (key == "t" || !IsKey(key) || !double.IsFinite(pair.Value))
                continue;
            sb.Append(';').Append(key).Append('=').Append(pair.Value.ToString("R", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static bool IsKey(string key) =>
        key.Length is > 0 and <= 32 && key.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}

/// <summary>Live DCS telemetry from the hook's lines.</summary>
/// <remarks>"noexport" means the multiplayer server forbids ownship export (LoIsOwnshipExportAllowed() false): the
/// hook then sends no values, and <see cref="ExportNotAllowed"/> lets the status line say why.</remarks>
public sealed class DcsTelemetrySource : FrameTelemetrySource
{
    private readonly object _gate = new();

    public DcsTelemetrySource(IFfbClock clock, string name = "DCS")
        : base(name, DcsTelemetryProtocol.Descriptors, clock)
    {
    }

    /// <summary>The unit type of the last "plane &lt;type&gt;" line (null after "exit").</summary>
    public string? Aircraft { get; private set; }

    /// <summary>The last notice of the hook was "noexport" (cleared by the next frame).</summary>
    public bool ExportNotAllowed { get; private set; }

    /// <summary>Raised for "plane", "exit" and "noexport" lines (status changes without a frame).</summary>
    public event Action? Notice;

    public long FramesAccepted { get; private set; }

    /// <summary>Handles one datagram (one or more lines).</summary>
    public void Ingest(ReadOnlySpan<byte> datagram) => Ingest(Encoding.UTF8.GetString(datagram));

    public void Ingest(string text)
    {
        var notice = false;
        lock (_gate)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("plane ", StringComparison.Ordinal))
                {
                    var aircraft = line[6..].Trim();
                    notice |= aircraft != Aircraft;
                    Aircraft = aircraft;
                }
                else if (line == "exit")
                {
                    Aircraft = null;
                    ExportNotAllowed = false;
                    Clear();
                    notice = true;
                }
                else if (line == "noexport")
                {
                    notice |= !ExportNotAllowed;
                    ExportNotAllowed = true;
                }
                else if (DcsTelemetryProtocol.TryParseFrame(line, out var frame))
                {
                    FramesAccepted++;
                    ExportNotAllowed = false;
                    Publish(frame!);
                }
            }
        }

        if (notice)
            Notice?.Invoke();
    }
}
