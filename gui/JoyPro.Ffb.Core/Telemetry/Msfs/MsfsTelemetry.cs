using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace JoyPro.Ffb.Core.Telemetry.Msfs;

// Microsoft Flight Simulator 2020 / 2024 through SimConnect, read only (docs/telemetry-recording.md): the feed asks for
// simulation variables and subscribes to system events; it never sends events, never sets data, never changes the
// sim's state. SimConnect.dll is never shipped: the native client loads the user's copy (MsfsNative.cs).

/// <summary>SIMCONNECT_DATATYPE (the subset JoyPro uses).</summary>
public enum SimConnectDataType : uint
{
    Float64 = 4,
    String256 = 9,
}

/// <summary>SIMCONNECT_PERIOD.</summary>
public enum SimConnectPeriod : uint
{
    Never = 0,
    Once = 1,
    VisualFrame = 2,
    SimFrame = 3,
    Second = 4,
}

/// <summary>SIMCONNECT_DATA_REQUEST_FLAG.</summary>
[Flags]
public enum SimConnectRequestFlags : uint
{
    Default = 0,
    Changed = 1,
    Tagged = 2,
}

/// <summary>SIMCONNECT_SIMOBJECT_TYPE.</summary>
public enum SimConnectSimObjectType : uint
{
    User = 0,
    All = 1,
    Aircraft = 2,
    Helicopter = 3,
    Boat = 4,
    Ground = 5,
}

/// <summary>SIMCONNECT_RECV_ID (the messages JoyPro reads).</summary>
public enum SimConnectRecvId : uint
{
    Null = 0,
    Exception = 1,
    Open = 2,
    Quit = 3,
    Event = 4,
    EventFilename = 6,
    SimObjectData = 8,
    SimObjectDataByType = 9,
    SystemState = 15,
}

/// <summary>Byte layout of the SimConnect messages (SimConnect.h, #pragma pack(1); every field is a DWORD or a fixed
/// char array, so the offsets are the same for 32 and 64 bit).</summary>
public static class SimConnectLayout
{
    // SIMCONNECT_RECV: dwSize, dwVersion, dwID
    public const int Size = 0;
    public const int Version = 4;
    public const int Id = 8;
    public const int HeaderSize = 12;

    // SIMCONNECT_RECV_EXCEPTION: dwException, dwSendID, dwIndex
    public const int ExceptionCode = 12;
    public const int ExceptionSendId = 16;
    public const int ExceptionIndex = 20;
    public const int ExceptionSize = 24;

    // SIMCONNECT_RECV_OPEN: szApplicationName[256], dwApplicationVersionMajor/Minor, dwApplicationBuildMajor/Minor,
    // dwSimConnectVersionMajor/Minor, dwSimConnectBuildMajor/Minor, dwReserved1/2
    public const int OpenApplicationName = 12;
    public const int OpenApplicationNameLength = 256;
    public const int OpenVersionMajor = 268;
    public const int OpenVersionMinor = 272;
    public const int OpenBuildMajor = 276;
    public const int OpenBuildMinor = 280;
    public const int OpenSimConnectVersionMajor = 284;
    public const int OpenSize = 308;

    // SIMCONNECT_RECV_EVENT: uGroupID, uEventID, dwData
    public const int EventGroupId = 12;
    public const int EventId = 16;
    public const int EventData = 20;
    public const int EventSize = 24;

    // SIMCONNECT_RECV_EVENT_FILENAME: SIMCONNECT_RECV_EVENT + szFileName[MAX_PATH], dwFlags
    public const int EventFileName = 24;
    public const int MaxPath = 260;
    public const int EventFilenameFlags = 284;
    public const int EventFilenameSize = 288;

    // SIMCONNECT_RECV_SIMOBJECT_DATA (and _BYTYPE, same layout): dwRequestID, dwObjectID, dwDefineID, dwFlags,
    // dwentrynumber, dwoutof, dwDefineCount, dwData...
    public const int DataRequestId = 12;
    public const int DataObjectId = 16;
    public const int DataDefineId = 20;
    public const int DataFlags = 24;
    public const int DataEntryNumber = 28;
    public const int DataOutOf = 32;
    public const int DataDefineCount = 36;
    public const int DataStart = 40;

    // SIMCONNECT_RECV_SYSTEM_STATE: dwRequestID, dwInteger, fFloat, szString[MAX_PATH]
    public const int StateRequestId = 12;
    public const int StateInteger = 16;
    public const int StateFloat = 20;
    public const int StateString = 24;
    public const int StateSize = 284;

    /// <summary>SIMCONNECT_EXCEPTION_NAME_UNRECOGNIZED (an unknown simulation variable).</summary>
    public const uint ExceptionNameUnrecognized = 7;

    public const uint UnknownGroup = 0xFFFFFFFF;
    public const uint ObjectIdUser = 0;
    public const int StringLength = 256;

    public static uint U32(ReadOnlySpan<byte> message, int offset) =>
        offset + 4 <= message.Length ? BinaryPrimitives.ReadUInt32LittleEndian(message[offset..]) : 0;

    public static double F64(ReadOnlySpan<byte> message, int offset) =>
        offset + 8 <= message.Length ? BinaryPrimitives.ReadDoubleLittleEndian(message[offset..]) : double.NaN;

    /// <summary>A NUL-terminated char array (UTF-8; the sim writes ANSI/UTF-8).</summary>
    public static string Text(ReadOnlySpan<byte> message, int offset, int length)
    {
        if (offset >= message.Length)
            return "";
        var field = message.Slice(offset, Math.Min(length, message.Length - offset));
        var end = field.IndexOf((byte)0);
        if (end >= 0)
            field = field[..end];
        return Encoding.UTF8.GetString(field).Trim();
    }
}

/// <summary>The SimConnect calls JoyPro makes (all read only). The native client wraps the user's SimConnect.dll;
/// tests pass a fake through <see cref="TelemetryFeedOptions.SimConnectClient"/>.</summary>
/// <remarks>Every call returns false when SimConnect answered with a failing HRESULT (the connection is gone).
/// Messages are handed out in SimConnect's own byte layout (<see cref="SimConnectLayout"/>).</remarks>
public interface ISimConnectClient : IDisposable
{
    /// <summary>For the status line: the SimConnect.dll in use (or "fake").</summary>
    string Location { get; }

    /// <summary>SimConnect_Open. <paramref name="dataReady"/> is set by SimConnect whenever a message waits.
    /// False = the sim is not running or does not answer yet.</summary>
    bool Open(string applicationName, WaitHandle dataReady);

    /// <summary>SimConnect_Close (no-op when not open).</summary>
    void Close();

    bool AddToDataDefinition(uint defineId, string datumName, string? unitsName, SimConnectDataType type, uint datumId);

    bool ClearDataDefinition(uint defineId);

    /// <summary>SimConnect_GetLastSentPacketID: exceptions name the packet that caused them.</summary>
    uint LastSentPacketId();

    bool RequestDataOnSimObject(uint requestId, uint defineId, uint objectId, SimConnectPeriod period, SimConnectRequestFlags flags, uint interval);

    bool RequestDataOnSimObjectType(uint requestId, uint defineId, uint radiusMeters, SimConnectSimObjectType type);

    bool SubscribeToSystemEvent(uint eventId, string systemEventName);

    bool RequestSystemState(uint requestId, string state);

    /// <summary>SimConnect_GetNextDispatch: the next waiting message (valid until the next call), false when none.</summary>
    bool TryGetNextDispatch(out ReadOnlySpan<byte> message);
}

/// <summary>One simulation variable of the catalogue.</summary>
/// <param name="Slow">Read once per second (engines, fuel, lights, weather) instead of with the sim frames.</param>
public sealed record MsfsSimVar(string Name, string Units, string Title, string Unit, double Min, double Max, string Category, bool Slow)
{
    public string Key { get; } = MsfsSimVars.KeyOf(Name);
}

/// <summary>The simulation variables JoyPro reads (FLOAT64), and the strings (STRING256).</summary>
/// <remarks>Names are the same in 2020 and 2024. A variable the running sim does not know is answered with
/// SIMCONNECT_EXCEPTION_NAME_UNRECOGNIZED and simply missing from the frames (data is read tagged).</remarks>
public static class MsfsSimVars
{
    public const string Prefix = "msfs.";

    /// <summary>"PLANE LATITUDE" -> "msfs.plane_latitude"; "GENERAL ENG RPM:1" -> "msfs.general_eng_rpm_1".</summary>
    public static string KeyOf(string name)
    {
        var sb = new StringBuilder(Prefix, Prefix.Length + name.Length);
        var underscore = true;
        foreach (var raw in name)
        {
            var c = char.ToLowerInvariant(raw);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(c);
                underscore = false;
            }
            else if (!underscore)
            {
                sb.Append('_');
                underscore = true;
            }
        }

        if (sb[^1] == '_')
            sb.Length--;
        return sb.ToString();
    }

    public const string Latitude = "PLANE LATITUDE";
    public const string Longitude = "PLANE LONGITUDE";
    public const string Altitude = "PLANE ALTITUDE";
    public const string Bank = "PLANE BANK DEGREES";
    public const string Pitch = "PLANE PITCH DEGREES";
    public const string HeadingTrue = "PLANE HEADING DEGREES TRUE";

    public static readonly IReadOnlyList<MsfsSimVar> All = Build();

    /// <summary>The string variables (one STRING256 definition).</summary>
    public static readonly IReadOnlyList<string> Strings =
        ["TITLE", "ATC ID", "ATC MODEL", "ATC TYPE", "ATC AIRLINE", "ATC FLIGHT NUMBER", "CATEGORY", "LIVERY NAME"];

    private static List<MsfsSimVar> Build()
    {
        var list = new List<MsfsSimVar>();
        void Add(string name, string units, string title, string unit, double min, double max, string category, bool slow = false) =>
            list.Add(new MsfsSimVar(name, units, title, unit, min, max, category, slow));
        void Flag(string name, string title, string category, bool slow = false) => Add(name, "bool", title, "0/1", 0, 1, category, slow);

        // Position
        Add(Latitude, "degrees", "Latitude", "deg", -90, 90, "Position");
        Add(Longitude, "degrees", "Longitude", "deg", -180, 180, "Position");
        Add(Altitude, "feet", "Altitude (MSL)", "ft", -1000, 60000, "Position");
        Add("PLANE ALT ABOVE GROUND", "feet", "Height above ground", "ft", 0, 60000, "Position");
        Add("PLANE ALT ABOVE GROUND MINUS CG", "feet", "Height above ground (wheels)", "ft", 0, 60000, "Position");
        Add("GROUND ALTITUDE", "feet", "Ground elevation", "ft", -1000, 30000, "Position");
        Add("INDICATED ALTITUDE", "feet", "Indicated altitude", "ft", -1000, 60000, "Position");
        Add("PRESSURE ALTITUDE", "feet", "Pressure altitude", "ft", -1000, 60000, "Position");
        Add("RADIO HEIGHT", "feet", "Radio altimeter", "ft", 0, 5000, "Position");

        // Attitude
        Add(Pitch, "degrees", "Pitch (MSFS: negative = nose up)", "deg", -90, 90, "Attitude");
        Add(Bank, "degrees", "Bank (MSFS: negative = right wing down)", "deg", -180, 180, "Attitude");
        Add(HeadingTrue, "degrees", "Heading (true)", "deg", 0, 360, "Attitude");
        Add("PLANE HEADING DEGREES MAGNETIC", "degrees", "Heading (magnetic)", "deg", 0, 360, "Attitude");
        Add("GPS GROUND TRUE TRACK", "degrees", "Ground track (true)", "deg", 0, 360, "Attitude");
        Add("MAGVAR", "degrees", "Magnetic variation", "deg", -180, 180, "Attitude", true);

        // Velocities
        foreach (var axis in new[] { "X", "Y", "Z" })
            Add("VELOCITY BODY " + axis, "feet per second", "Body velocity " + axis, "ft/s", -1000, 1000, "Velocity");
        foreach (var axis in new[] { "X", "Y", "Z" })
            Add("VELOCITY WORLD " + axis, "feet per second", "World velocity " + axis, "ft/s", -1000, 1000, "Velocity");
        Add("VERTICAL SPEED", "feet per minute", "Vertical speed", "ft/min", -6000, 6000, "Velocity");
        Add("GROUND VELOCITY", "knots", "Ground speed", "kt", 0, 700, "Velocity");
        foreach (var axis in new[] { "X", "Y", "Z" })
            Add("ROTATION VELOCITY BODY " + axis, "degrees per second", "Rotation rate " + axis, "deg/s", -180, 180, "Velocity");
        foreach (var axis in new[] { "X", "Y", "Z" })
            Add("AIRCRAFT WIND " + axis, "knots", "Wind on the aircraft " + axis, "kt", -100, 100, "Velocity");

        // Accelerations
        foreach (var axis in new[] { "X", "Y", "Z" })
            Add("ACCELERATION BODY " + axis, "feet per second squared", "Body acceleration " + axis, "ft/s²", -100, 100, "Acceleration");
        foreach (var axis in new[] { "X", "Y", "Z" })
            Add("ACCELERATION WORLD " + axis, "feet per second squared", "World acceleration " + axis, "ft/s²", -100, 100, "Acceleration");
        Add("G FORCE", "gforce", "G load", "G", -3, 9, "Acceleration");
        Add("MAX G FORCE", "gforce", "Highest G load", "G", 0, 9, "Acceleration", true);
        Add("MIN G FORCE", "gforce", "Lowest G load", "G", -3, 0, "Acceleration", true);

        // Air data
        Add("AIRSPEED INDICATED", "knots", "Indicated airspeed", "kt", 0, 600, "Air data");
        Add("AIRSPEED TRUE", "knots", "True airspeed", "kt", 0, 700, "Air data");
        Add("AIRSPEED MACH", "mach", "Mach", "M", 0, 2, "Air data");
        Add("AIRSPEED BARBER POLE", "knots", "Maximum speed (barber pole)", "kt", 0, 600, "Air data", true);
        Add("INCIDENCE ALPHA", "degrees", "Angle of attack", "deg", -10, 30, "Air data");
        Add("INCIDENCE BETA", "degrees", "Sideslip", "deg", -20, 20, "Air data");
        Add("STALL ALPHA", "degrees", "Stall angle of attack", "deg", 0, 30, "Air data", true);
        Flag("STALL WARNING", "Stall warning", "Warnings");
        Flag("OVERSPEED WARNING", "Overspeed warning", "Warnings");
        Flag("GEAR DAMAGE BY SPEED", "Gear damaged by speed", "Warnings", true);
        Flag("FLAP DAMAGE BY SPEED", "Flaps damaged by speed", "Warnings", true);
        Add("DESIGN SPEED VS0", "knots", "Stall speed, landing configuration", "kt", 0, 300, "Air data", true);
        Add("DESIGN SPEED VS1", "knots", "Stall speed, clean", "kt", 0, 300, "Air data", true);
        Add("DESIGN SPEED VC", "knots", "Cruise speed", "kt", 0, 600, "Air data", true);

        // Controls (what the aircraft does) and inputs (what the pilot does)
        Add("ELEVATOR POSITION", "position", "Elevator", "-1..1", -1, 1, "Controls");
        Add("AILERON POSITION", "position", "Ailerons", "-1..1", -1, 1, "Controls");
        Add("RUDDER POSITION", "position", "Rudder", "-1..1", -1, 1, "Controls");
        Add("ELEVATOR DEFLECTION", "degrees", "Elevator deflection", "deg", -30, 30, "Controls");
        Add("AILERON AVERAGE DEFLECTION", "degrees", "Aileron deflection", "deg", -30, 30, "Controls");
        Add("RUDDER DEFLECTION", "degrees", "Rudder deflection", "deg", -30, 30, "Controls");
        Add("YOKE X POSITION", "position", "Yoke / stick X", "-1..1", -1, 1, "Inputs");
        Add("YOKE Y POSITION", "position", "Yoke / stick Y", "-1..1", -1, 1, "Inputs");
        Add("RUDDER PEDAL POSITION", "position", "Rudder pedals", "-1..1", -1, 1, "Inputs");
        Add("BRAKE LEFT POSITION", "percent over 100", "Left brake", "0..1", 0, 1, "Inputs");
        Add("BRAKE RIGHT POSITION", "percent over 100", "Right brake", "0..1", 0, 1, "Inputs");
        Flag("BRAKE PARKING POSITION", "Parking brake", "Inputs");
        for (var n = 1; n <= 4; n++)
            Add("GENERAL ENG THROTTLE LEVER POSITION:" + n, "percent", "Throttle " + n, "%", -25, 100, "Inputs");
        for (var n = 1; n <= 4; n++)
            Add("GENERAL ENG PROPELLER LEVER POSITION:" + n, "percent", "Propeller lever " + n, "%", 0, 100, "Inputs");
        for (var n = 1; n <= 4; n++)
            Add("GENERAL ENG MIXTURE LEVER POSITION:" + n, "percent", "Mixture lever " + n, "%", 0, 100, "Inputs");
        Add("ELEVATOR TRIM PCT", "percent over 100", "Elevator trim", "-1..1", -1, 1, "Trim");
        Add("AILERON TRIM PCT", "percent over 100", "Aileron trim", "-1..1", -1, 1, "Trim");
        Add("RUDDER TRIM PCT", "percent over 100", "Rudder trim", "-1..1", -1, 1, "Trim");
        Add("ELEVATOR TRIM POSITION", "degrees", "Elevator trim angle", "deg", -20, 20, "Trim");
        Add("FLAPS HANDLE PERCENT", "percent", "Flaps handle", "%", 0, 100, "Configuration");
        Add("FLAPS HANDLE INDEX", "number", "Flaps handle notch", "", 0, 8, "Configuration");
        Add("TRAILING EDGE FLAPS LEFT PERCENT", "percent", "Trailing edge flaps", "%", 0, 100, "Configuration");
        Add("LEADING EDGE FLAPS LEFT PERCENT", "percent", "Leading edge flaps", "%", 0, 100, "Configuration");
        Add("SPOILERS HANDLE POSITION", "percent", "Spoilers / speed brake handle", "%", 0, 100, "Configuration");
        Flag("SPOILERS ARMED", "Spoilers armed", "Configuration");
        Add("SPOILERS LEFT POSITION", "percent", "Spoilers (left)", "%", 0, 100, "Configuration");
        Flag("GEAR HANDLE POSITION", "Gear handle down", "Configuration");
        Add("GEAR TOTAL PCT EXTENDED", "percent", "Gear extended", "%", 0, 100, "Configuration");
        Add("GEAR CENTER POSITION", "percent over 100", "Nose / centre gear", "0..1", 0, 1, "Configuration");
        Add("GEAR LEFT POSITION", "percent over 100", "Left gear", "0..1", 0, 1, "Configuration");
        Add("GEAR RIGHT POSITION", "percent over 100", "Right gear", "0..1", 0, 1, "Configuration");
        Add("TAILHOOK POSITION", "percent", "Tailhook", "%", 0, 100, "Configuration", true);

        // Ground contact
        Flag("SIM ON GROUND", "On the ground", "Ground");
        for (var n = 0; n <= 2; n++)
            Flag("GEAR IS ON GROUND:" + n, "Wheel " + n + " on the ground", "Ground");
        for (var n = 0; n <= 2; n++)
            Add("CONTACT POINT COMPRESSION:" + n, "position", "Wheel " + n + " compression", "0..1", 0, 1, "Ground");
        Add("SURFACE TYPE", "enum", "Surface type", "", 0, 30, "Ground", true);
        Add("PLANE TOUCHDOWN NORMAL VELOCITY", "feet per second", "Last touchdown: vertical speed", "ft/s", -20, 20, "Ground", true);
        Add("PLANE TOUCHDOWN PITCH DEGREES", "degrees", "Last touchdown: pitch", "deg", -30, 30, "Ground", true);
        Add("PLANE TOUCHDOWN BANK DEGREES", "degrees", "Last touchdown: bank", "deg", -30, 30, "Ground", true);

        // Engines 1..4 (RPM and N1 with the frames, the rest once per second)
        Add("NUMBER OF ENGINES", "number", "Number of engines", "", 0, 8, "Engines", true);
        Add("ENGINE TYPE", "enum", "Engine type", "", 0, 7, "Engines", true);
        for (var n = 1; n <= 4; n++)
        {
            var e = ":" + n;
            Add("GENERAL ENG RPM" + e, "rpm", "Engine " + n + " RPM", "rpm", 0, 10000, "Engines");
            Add("TURB ENG N1" + e, "percent", "Engine " + n + " N1", "%", 0, 110, "Engines");
            Add("TURB ENG N2" + e, "percent", "Engine " + n + " N2", "%", 0, 110, "Engines", true);
            Add("PROP RPM" + e, "rpm", "Propeller " + n + " RPM", "rpm", 0, 3000, "Engines", true);
            Add("GENERAL ENG EXHAUST GAS TEMPERATURE" + e, "celsius", "Engine " + n + " EGT", "°C", 0, 1200, "Engines", true);
            Add("TURB ENG ITT" + e, "celsius", "Engine " + n + " ITT", "°C", 0, 1200, "Engines", true);
            Add("ENG FUEL FLOW GPH" + e, "gallons per hour", "Engine " + n + " fuel flow", "gal/h", 0, 5000, "Engines", true);
            Add("GENERAL ENG OIL TEMPERATURE" + e, "celsius", "Engine " + n + " oil temperature", "°C", -40, 200, "Engines", true);
            Add("GENERAL ENG OIL PRESSURE" + e, "psi", "Engine " + n + " oil pressure", "psi", 0, 200, "Engines", true);
            Add("ENG TORQUE" + e, "foot pounds", "Engine " + n + " torque", "ft·lbf", 0, 10000, "Engines", true);
            Add("RECIP ENG MANIFOLD PRESSURE" + e, "inHg", "Engine " + n + " manifold pressure", "inHg", 0, 60, "Engines", true);
            Flag("GENERAL ENG COMBUSTION" + e, "Engine " + n + " running", "Engines", true);
            Flag("TURB ENG AFTERBURNER" + e, "Engine " + n + " afterburner", "Engines", true);
        }

        // Fuel and weight
        Add("FUEL TOTAL QUANTITY WEIGHT", "pounds", "Fuel", "lb", 0, 400000, "Fuel", true);
        Add("FUEL TOTAL QUANTITY", "gallons", "Fuel (volume)", "gal", 0, 60000, "Fuel", true);
        Add("FUEL TOTAL CAPACITY", "gallons", "Fuel capacity", "gal", 0, 60000, "Fuel", true);
        Add("FUEL WEIGHT PER GALLON", "pounds", "Fuel weight per gallon", "lb", 5, 8, "Fuel", true);
        Add("TOTAL WEIGHT", "pounds", "Total weight", "lb", 0, 1000000, "Fuel", true);

        // Autopilot
        Flag("AUTOPILOT MASTER", "Autopilot", "Autopilot", true);
        Flag("AUTOPILOT HEADING LOCK", "Heading hold", "Autopilot", true);
        Add("AUTOPILOT HEADING LOCK DIR", "degrees", "Selected heading", "deg", 0, 360, "Autopilot", true);
        Flag("AUTOPILOT ALTITUDE LOCK", "Altitude hold", "Autopilot", true);
        Add("AUTOPILOT ALTITUDE LOCK VAR", "feet", "Selected altitude", "ft", 0, 60000, "Autopilot", true);
        Flag("AUTOPILOT VERTICAL HOLD", "Vertical speed hold", "Autopilot", true);
        Add("AUTOPILOT VERTICAL HOLD VAR", "feet per minute", "Selected vertical speed", "ft/min", -6000, 6000, "Autopilot", true);
        Flag("AUTOPILOT AIRSPEED HOLD", "Airspeed hold", "Autopilot", true);
        Add("AUTOPILOT AIRSPEED HOLD VAR", "knots", "Selected airspeed", "kt", 0, 600, "Autopilot", true);
        Flag("AUTOPILOT NAV1 LOCK", "NAV hold", "Autopilot", true);
        Flag("AUTOPILOT APPROACH HOLD", "Approach hold", "Autopilot", true);
        Flag("AUTOPILOT THROTTLE ARM", "Autothrottle armed", "Autopilot", true);
        Flag("AUTOPILOT YAW DAMPER", "Yaw damper", "Autopilot", true);

        // Lights and systems
        foreach (var light in new[] { "NAV", "BEACON", "LANDING", "TAXI", "STROBE", "PANEL", "LOGO", "WING", "RECOGNITION", "CABIN" })
            Flag("LIGHT " + light, char.ToUpperInvariant(light[0]) + light[1..].ToLowerInvariant() + " light", "Lights", true);
        Flag("ELECTRICAL MASTER BATTERY", "Battery master", "Systems", true);
        Add("ELECTRICAL MAIN BUS VOLTAGE", "volts", "Main bus voltage", "V", 0, 32, "Systems", true);
        Add("COM ACTIVE FREQUENCY:1", "MHz", "COM1 frequency", "MHz", 118, 137, "Systems", true);
        Add("NAV ACTIVE FREQUENCY:1", "MHz", "NAV1 frequency", "MHz", 108, 118, "Systems", true);
        Add("KOHLSMAN SETTING MB", "millibars", "Altimeter setting", "hPa", 940, 1060, "Systems", true);

        // Environment
        Add("AMBIENT WIND VELOCITY", "knots", "Wind speed", "kt", 0, 100, "Environment", true);
        Add("AMBIENT WIND DIRECTION", "degrees", "Wind direction (from)", "deg", 0, 360, "Environment", true);
        Add("AMBIENT TEMPERATURE", "celsius", "Outside air temperature", "°C", -60, 50, "Environment", true);
        Add("TOTAL AIR TEMPERATURE", "celsius", "Total air temperature", "°C", -60, 80, "Environment", true);
        Add("AMBIENT PRESSURE", "inHg", "Static pressure", "inHg", 0, 32, "Environment", true);
        Add("SEA LEVEL PRESSURE", "millibars", "Sea level pressure", "hPa", 940, 1060, "Environment", true);
        Add("AMBIENT DENSITY", "slugs per cubic feet", "Air density", "slug/ft³", 0, 0.003, "Environment", true);
        Add("AMBIENT VISIBILITY", "meters", "Visibility", "m", 0, 100000, "Environment", true);
        Flag("AMBIENT IN CLOUD", "In a cloud", "Environment", true);
        Add("AMBIENT PRECIP STATE", "mask", "Precipitation (2 none, 4 rain, 8 snow)", "", 0, 8, "Environment", true);

        // Simulation
        Add("SIMULATION TIME", "seconds", "Simulation time", "s", 0, 100000, "Simulation");
        Add("SIMULATION RATE", "number", "Simulation rate", "x", 0.25, 128, "Simulation", true);
        Add("LOCAL TIME", "seconds", "Local time of day", "s", 0, 86400, "Simulation", true);
        Add("ZULU TIME", "seconds", "UTC time of day", "s", 0, 86400, "Simulation", true);
        Add("ZULU DAY OF MONTH", "number", "UTC day", "", 1, 31, "Simulation", true);
        Add("ZULU MONTH OF YEAR", "number", "UTC month", "", 1, 12, "Simulation", true);
        Add("ZULU YEAR", "number", "UTC year", "", 1900, 2100, "Simulation", true);
        Add("TIME OF DAY", "enum", "Time of day (0 dawn, 1 day, 2 dusk, 3 night)", "", 0, 3, "Simulation", true);
        Add("CAMERA STATE", "enum", "Camera state", "", 0, 40, "Simulation", true);
        Flag("IS SLEW ACTIVE", "Slew mode", "Simulation", true);
        Add("CRASH FLAG", "enum", "Crash flag", "", 0, 2048, "Simulation", true);
        Add("CRASH SEQUENCE", "enum", "Crash sequence", "", 0, 11, "Simulation", true);

        return list;
    }

    public static IReadOnlyList<TelemetryDescriptor> Descriptors { get; } = All
        .Select(v => new TelemetryDescriptor(v.Key, v.Title, v.Unit, v.Min, v.Max, v.Category, "SimConnect " + v.Name + " (" + v.Units + ")"))
        .ToList();
}

/// <summary>Reads Microsoft Flight Simulator 2020 / 2024 through a SimConnect client (decoding and requests; the
/// thread, the connection and the retries are <see cref="MsfsTelemetryFeed"/>).</summary>
/// <remarks>
/// Four data definitions on the user aircraft: the flight model at sim-frame rate (interval adapted to about
/// <c>pollHz</c> frames per second), the slow values once per second, the strings once per second (all three tagged
/// and only when changed), and the traffic around the user (aircraft and helicopters within
/// <see cref="ObjectsRadiusMeters"/>, asked once per second, raised as one <see cref="TelemetryEventKinds.Objects"/>
/// event). A frame is published with each flight model message; it carries the latest slow values too. Frames are held
/// back while the sim says it is not in a flight ("Sim" state 0, SimStop) and at latitude/longitude exactly 0/0.
/// </remarks>
public sealed class MsfsTelemetrySource : FrameTelemetrySource
{
    public const uint DefineFast = 1;
    public const uint DefineSlow = 2;
    public const uint DefineStrings = 3;
    public const uint DefineObjects = 4;

    public const uint RequestFast = 1;
    public const uint RequestSlow = 2;
    public const uint RequestStrings = 3;
    public const uint RequestObjectsAircraft = 10;
    public const uint RequestObjectsHelicopters = 11;
    public const uint RequestStateFlightLoaded = 20;
    public const uint RequestStateAircraftLoaded = 21;
    public const uint RequestStateSim = 22;

    /// <summary>A sim frame rate assumed until the real one is measured.</summary>
    public const double AssumedSimFrameRate = 45;

    /// <summary>How long without any message the connection counts as dead (no RECV_QUIT came: the sim crashed).</summary>
    public const double SilenceTimeoutSeconds = 45;

    public const double HeartbeatSeconds = 5;

    /// <summary>The system events subscribed to (event id = index + 1).</summary>
    public static readonly IReadOnlyList<string> SystemEvents =
        ["SimStart", "SimStop", "Pause", "Sim", "Crashed", "CrashReset", "FlightLoaded", "AircraftLoaded",
         "FlightPlanActivated", "FlightPlanDeactivated", "PositionChanged"];

    public const string KindPause = "msfs.pause";
    public const string KindCrashed = "msfs.crashed";
    public const string KindCrashReset = "msfs.crash_reset";
    public const string KindPositionChanged = "msfs.position_changed";
    public const string InfoFlightFile = "msfs.flight_file";
    public const string InfoAircraftFile = "msfs.aircraft_file";
    public const string InfoFlightPlan = "msfs.flight_plan";
    public const string InfoSimConnectVersion = "msfs.simconnect_version";

    // The objects definition (untagged, fixed layout): 6 FLOAT64, then TITLE and ATC ID as STRING256.
    private static readonly (string Name, string Units)[] ObjectNumbers =
    [
        (MsfsSimVars.Latitude, "degrees"), (MsfsSimVars.Longitude, "degrees"), (MsfsSimVars.Altitude, "feet"),
        (MsfsSimVars.Bank, "degrees"), (MsfsSimVars.Pitch, "degrees"), (MsfsSimVars.HeadingTrue, "degrees")
    ];

    private static readonly string[] ObjectStrings = ["TITLE", "ATC ID"];
    public static readonly int ObjectDataSize = ObjectNumbers.Length * 8 + ObjectStrings.Length * SimConnectLayout.StringLength;

    private readonly double _pollHz;
    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, (uint Define, int Index)> _packets = new();
    private readonly HashSet<string> _rejected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private readonly List<string> _objectLines = new();
    private readonly bool[] _objectsDone = new bool[2];
    private ISimConnectClient? _client;
    private uint _interval;
    private long _fastMessages;
    private long _windowMessages;
    private double _windowStart;
    private double _lastMessage;
    private double _lastHeartbeat;
    private double _lastObjects;
    private bool _objectsPending;
    private bool _objectsBroken;
    private uint _userObjectId = uint.MaxValue;
    private bool? _inFlight;
    private bool _sessionOpen;

    public MsfsTelemetrySource(IFfbClock clock, string name = "Microsoft Flight Simulator", double pollHz = 20)
        : base(name, MsfsSimVars.Descriptors, clock)
    {
        _pollHz = Math.Clamp(pollHz <= 0 ? 20 : pollHz, 1, 60);
    }

    /// <summary>Radius of the traffic snapshot (m, SimConnect allows up to 200 km); 0 = no traffic.</summary>
    public uint ObjectsRadiusMeters { get; init; } = 50_000;

    /// <summary>Variables the running sim did not know (SIMCONNECT_EXCEPTION_NAME_UNRECOGNIZED).</summary>
    public IReadOnlyCollection<string> RejectedSimVars => _rejected;

    /// <summary>The sim-frame interval currently asked for (0 = every sim frame).</summary>
    public uint FastInterval => _interval;

    /// <summary>Flight model messages since the connection was made.</summary>
    public long FastMessages => _fastMessages;

    /// <summary>"Sim" state: true in a flight, false in menus, null = unknown.</summary>
    public bool? InFlight => _inFlight;

    public bool Connected => _client is not null;

    public override IReadOnlyList<TelemetryNavMapping> NavMappings { get; } =
    [
        new(TelemetryNav.Latitude, MsfsSimVars.KeyOf(MsfsSimVars.Latitude)),
        new(TelemetryNav.Longitude, MsfsSimVars.KeyOf(MsfsSimVars.Longitude)),
        new(TelemetryNav.Altitude, MsfsSimVars.KeyOf(MsfsSimVars.Altitude), TelemetryNav.FeetToMetres),
        // MSFS (as FSX): bank positive = left wing down, pitch positive = nose down. Tacview: the opposite.
        new(TelemetryNav.Roll, MsfsSimVars.KeyOf(MsfsSimVars.Bank), -1),
        new(TelemetryNav.Pitch, MsfsSimVars.KeyOf(MsfsSimVars.Pitch), -1),
        new(TelemetryNav.Yaw, MsfsSimVars.KeyOf(MsfsSimVars.HeadingTrue)),
        new(TelemetryNav.Heading, MsfsSimVars.KeyOf("GPS GROUND TRUE TRACK")),
        new(TelemetryNav.Agl, MsfsSimVars.KeyOf("PLANE ALT ABOVE GROUND"), TelemetryNav.FeetToMetres),
        new(TelemetryNav.Ias, MsfsSimVars.KeyOf("AIRSPEED INDICATED"), TelemetryNav.KnotsToMetresPerSecond),
        new(TelemetryNav.Tas, MsfsSimVars.KeyOf("AIRSPEED TRUE"), TelemetryNav.KnotsToMetresPerSecond),
        new(TelemetryNav.Mach, MsfsSimVars.KeyOf("AIRSPEED MACH")),
        new(TelemetryNav.Aoa, MsfsSimVars.KeyOf("INCIDENCE ALPHA")),
        new(TelemetryNav.Aos, MsfsSimVars.KeyOf("INCIDENCE BETA")),
        new(TelemetryNav.VerticalG, MsfsSimVars.KeyOf("G FORCE")),
        new(TelemetryNav.Throttle, MsfsSimVars.KeyOf("GENERAL ENG THROTTLE LEVER POSITION:1"), 0.01),
        new(TelemetryNav.Gear, MsfsSimVars.KeyOf("GEAR TOTAL PCT EXTENDED"), 0.01),
        new(TelemetryNav.Flaps, MsfsSimVars.KeyOf("FLAPS HANDLE PERCENT"), 0.01),
        new(TelemetryNav.Airbrakes, MsfsSimVars.KeyOf("SPOILERS HANDLE POSITION"), 0.01),
        new(TelemetryNav.FuelKg, MsfsSimVars.KeyOf("FUEL TOTAL QUANTITY WEIGHT"), TelemetryNav.PoundsToKg),
        new(TelemetryNav.VerticalSpeed, MsfsSimVars.KeyOf("VERTICAL SPEED"), TelemetryNav.FeetToMetres / 60),
    ];

    /// <summary>Sets up the definitions, requests and subscriptions on an open client.</summary>
    /// <returns>False when a call failed (the connection is gone).</returns>
    public bool Connect(ISimConnectClient client)
    {
        Reset();
        _client = client;
        var now = Clock.Now;
        _lastMessage = now;
        _lastHeartbeat = now;
        _lastObjects = now;
        _windowStart = now;
        _interval = IntervalFor(AssumedSimFrameRate);

        var ok = true;
        for (var i = 0; i < MsfsSimVars.All.Count; i++)
        {
            var v = MsfsSimVars.All[i];
            var define = v.Slow ? DefineSlow : DefineFast;
            ok &= client.AddToDataDefinition(define, v.Name, v.Units, SimConnectDataType.Float64, (uint)i);
            _packets[client.LastSentPacketId()] = (define, i);
        }

        for (var i = 0; i < MsfsSimVars.Strings.Count; i++)
        {
            ok &= client.AddToDataDefinition(DefineStrings, MsfsSimVars.Strings[i], null, SimConnectDataType.String256, (uint)i);
            _packets[client.LastSentPacketId()] = (DefineStrings, i);
        }

        if (ObjectsRadiusMeters > 0)
        {
            for (var i = 0; i < ObjectNumbers.Length; i++)
            {
                ok &= client.AddToDataDefinition(DefineObjects, ObjectNumbers[i].Name, ObjectNumbers[i].Units, SimConnectDataType.Float64, (uint)i);
                _packets[client.LastSentPacketId()] = (DefineObjects, i);
            }

            for (var i = 0; i < ObjectStrings.Length; i++)
            {
                ok &= client.AddToDataDefinition(DefineObjects, ObjectStrings[i], null, SimConnectDataType.String256, (uint)(ObjectNumbers.Length + i));
                _packets[client.LastSentPacketId()] = (DefineObjects, ObjectNumbers.Length + i);
            }
        }

        const SimConnectRequestFlags changedTagged = SimConnectRequestFlags.Changed | SimConnectRequestFlags.Tagged;
        ok &= client.RequestDataOnSimObject(RequestFast, DefineFast, SimConnectLayout.ObjectIdUser, SimConnectPeriod.SimFrame, changedTagged, _interval);
        ok &= client.RequestDataOnSimObject(RequestSlow, DefineSlow, SimConnectLayout.ObjectIdUser, SimConnectPeriod.Second, changedTagged, 0);
        ok &= client.RequestDataOnSimObject(RequestStrings, DefineStrings, SimConnectLayout.ObjectIdUser, SimConnectPeriod.Second, changedTagged, 0);

        for (var i = 0; i < SystemEvents.Count; i++)
            ok &= client.SubscribeToSystemEvent((uint)(i + 1), SystemEvents[i]);

        ok &= client.RequestSystemState(RequestStateFlightLoaded, "FlightLoaded");
        ok &= client.RequestSystemState(RequestStateAircraftLoaded, "AircraftLoaded");
        ok &= client.RequestSystemState(RequestStateSim, "Sim");
        return ok;
    }

    /// <summary>Forgets the connection (the feed closes the client). <paramref name="reason"/> != null ends an open
    /// session with that text.</summary>
    public void Disconnect(string? reason)
    {
        _client = null;
        if (reason is not null && _sessionOpen)
            RaiseEvent(TelemetryEventKinds.SessionEnd, reason);
        _sessionOpen = false;
        foreach (var key in new[] { TelemetryInfoKeys.Aircraft, TelemetryInfoKeys.Pilot, TelemetryInfoKeys.Mission, InfoFlightFile, InfoAircraftFile, InfoFlightPlan })
            SetInfo(key, null);
        foreach (var name in MsfsSimVars.Strings)
            SetInfo(MsfsSimVars.KeyOf(name), null);
        Clear();
        Reset();
    }

    private void Reset()
    {
        _values.Clear();
        _packets.Clear();
        _rejected.Clear();
        _strings.Clear();
        _objectLines.Clear();
        _objectsPending = false;
        _objectsBroken = false;
        _userObjectId = uint.MaxValue;
        _inFlight = null;
        _fastMessages = 0;
        _windowMessages = 0;
    }

    private uint IntervalFor(double simFrameRate) => (uint)Math.Max(0, Math.Round(simFrameRate / _pollHz) - 1);

    public enum MessageResult
    {
        Handled,
        Ignored,
        Quit,
    }

    /// <summary>Handles one SimConnect message (its native bytes). Public for tests and replays.</summary>
    public MessageResult Handle(ReadOnlySpan<byte> message)
    {
        if (message.Length < SimConnectLayout.HeaderSize)
            return MessageResult.Ignored;
        var declared = (int)SimConnectLayout.U32(message, SimConnectLayout.Size);
        if (declared >= SimConnectLayout.HeaderSize && declared < message.Length)
            message = message[..declared];
        _lastMessage = Clock.Now;
        var id = (SimConnectRecvId)SimConnectLayout.U32(message, SimConnectLayout.Id);
        if (id != SimConnectRecvId.Null)
            PublishRaw("simconnect", message);
        switch (id)
        {
            case SimConnectRecvId.Open:
                OnOpen(message);
                return MessageResult.Handled;
            case SimConnectRecvId.Quit:
                return MessageResult.Quit;
            case SimConnectRecvId.Exception:
                OnException(message);
                return MessageResult.Handled;
            case SimConnectRecvId.SimObjectData:
                OnData(message);
                return MessageResult.Handled;
            case SimConnectRecvId.SimObjectDataByType:
                OnObject(message);
                return MessageResult.Handled;
            case SimConnectRecvId.Event:
                OnEvent(SimConnectLayout.U32(message, SimConnectLayout.EventId), SimConnectLayout.U32(message, SimConnectLayout.EventData), null);
                return MessageResult.Handled;
            case SimConnectRecvId.EventFilename:
                OnEvent(SimConnectLayout.U32(message, SimConnectLayout.EventId), 0,
                    SimConnectLayout.Text(message, SimConnectLayout.EventFileName, SimConnectLayout.MaxPath));
                return MessageResult.Handled;
            case SimConnectRecvId.SystemState:
                OnSystemState(message);
                return MessageResult.Handled;
            default:
                return MessageResult.Ignored;
        }
    }

    /// <summary>Once-per-second work: the traffic request, the rate adaptation, the heartbeat.</summary>
    /// <returns>False when the connection is gone (a call failed or the sim was silent for
    /// <see cref="SilenceTimeoutSeconds"/>).</returns>
    public bool Tick()
    {
        var client = _client;
        if (client is null)
            return false;
        var now = Clock.Now;
        if (now - _lastMessage > SilenceTimeoutSeconds)
            return false;

        var ok = true;
        if (ObjectsRadiusMeters > 0 && !_objectsBroken && _inFlight != false && now - _lastObjects >= 0.95)
        {
            if (_objectsPending)
                FlushObjects();
            _lastObjects = now;
            _objectLines.Clear();
            _objectsDone[0] = _objectsDone[1] = false;
            _objectsPending = true;
            ok &= client.RequestDataOnSimObjectType(RequestObjectsAircraft, DefineObjects, ObjectsRadiusMeters, SimConnectSimObjectType.Aircraft);
            ok &= client.RequestDataOnSimObjectType(RequestObjectsHelicopters, DefineObjects, ObjectsRadiusMeters, SimConnectSimObjectType.Helicopter);
        }

        // The real sim frame rate from the messages of the last seconds; ask again when the rate is far off.
        var window = now - _windowStart;
        if (window >= 3)
        {
            if (_windowMessages >= 10)
            {
                var rate = _windowMessages / window;
                if (Math.Abs(rate - _pollHz) > _pollHz * 0.25)
                {
                    var next = IntervalFor(rate * (_interval + 1));
                    if (next != _interval)
                    {
                        _interval = next;
                        ok &= client.RequestDataOnSimObject(RequestFast, DefineFast, SimConnectLayout.ObjectIdUser, SimConnectPeriod.SimFrame,
                            SimConnectRequestFlags.Changed | SimConnectRequestFlags.Tagged, _interval);
                    }
                }
            }

            _windowStart = now;
            _windowMessages = 0;
        }

        if (now - _lastHeartbeat >= HeartbeatSeconds)
        {
            _lastHeartbeat = now;
            ok &= client.RequestSystemState(RequestStateSim, "Sim");
        }

        return ok;
    }

    private void OnOpen(ReadOnlySpan<byte> message)
    {
        var app = SimConnectLayout.Text(message, SimConnectLayout.OpenApplicationName, SimConnectLayout.OpenApplicationNameLength);
        var version = string.Join('.', SimConnectLayout.U32(message, SimConnectLayout.OpenVersionMajor), SimConnectLayout.U32(message, SimConnectLayout.OpenVersionMinor),
            SimConnectLayout.U32(message, SimConnectLayout.OpenBuildMajor), SimConnectLayout.U32(message, SimConnectLayout.OpenBuildMinor));
        SetInfo(TelemetryInfoKeys.GameVersion, (app + " " + version).Trim());
        if (message.Length >= SimConnectLayout.OpenSize)
        {
            var at = SimConnectLayout.OpenSimConnectVersionMajor;
            var sc = string.Join('.', SimConnectLayout.U32(message, at), SimConnectLayout.U32(message, at + 4),
                SimConnectLayout.U32(message, at + 8), SimConnectLayout.U32(message, at + 12));
            SetInfo(InfoSimConnectVersion, sc);
        }
    }

    private void OnException(ReadOnlySpan<byte> message)
    {
        var sendId = SimConnectLayout.U32(message, SimConnectLayout.ExceptionSendId);
        if (!_packets.TryGetValue(sendId, out var datum))
            return;
        switch (datum.Define)
        {
            case DefineFast:
            case DefineSlow:
                var v = MsfsSimVars.All[datum.Index];
                _rejected.Add(v.Name);
                _values.Remove(v.Key);
                break;
            case DefineStrings:
                _rejected.Add(MsfsSimVars.Strings[datum.Index]);
                break;
            case DefineObjects:
                _objectsBroken = true;   // fixed layout: one missing variable shifts the rest
                break;
        }
    }

    private void OnData(ReadOnlySpan<byte> message)
    {
        var request = SimConnectLayout.U32(message, SimConnectLayout.DataRequestId);
        var count = (int)SimConnectLayout.U32(message, SimConnectLayout.DataDefineCount);
        switch (request)
        {
            case RequestFast:
            case RequestSlow:
            {
                _userObjectId = SimConnectLayout.U32(message, SimConnectLayout.DataObjectId);
                ReadTaggedNumbers(message, count);
                if (request == RequestFast)
                {
                    _fastMessages++;
                    _windowMessages++;
                    PublishFrame();
                }

                break;
            }
            case RequestStrings:
                ReadTaggedStrings(message, count);
                break;
        }
    }

    private void ReadTaggedNumbers(ReadOnlySpan<byte> message, int count)
    {
        var at = SimConnectLayout.DataStart;
        for (var i = 0; i < count && at + 12 <= message.Length; i++, at += 12)
        {
            var datum = SimConnectLayout.U32(message, at);
            if (datum >= MsfsSimVars.All.Count)
                continue;
            var value = SimConnectLayout.F64(message, at + 4);
            if (double.IsFinite(value))
                _values[MsfsSimVars.All[(int)datum].Key] = value;
        }
    }

    private void ReadTaggedStrings(ReadOnlySpan<byte> message, int count)
    {
        var at = SimConnectLayout.DataStart;
        const int stride = 4 + SimConnectLayout.StringLength;
        for (var i = 0; i < count && at + stride <= message.Length; i++, at += stride)
        {
            var datum = SimConnectLayout.U32(message, at);
            if (datum >= MsfsSimVars.Strings.Count)
                continue;
            var name = MsfsSimVars.Strings[(int)datum];
            var text = SimConnectLayout.Text(message, at + 4, SimConnectLayout.StringLength);
            _strings[name] = text;
            switch (name)
            {
                case "TITLE":
                    SetInfo(TelemetryInfoKeys.Aircraft, text);
                    break;
                case "ATC ID":
                    SetInfo(TelemetryInfoKeys.Pilot, text);
                    break;
                default:
                    SetInfo(MsfsSimVars.KeyOf(name), text);
                    break;
            }
        }
    }

    private void PublishFrame()
    {
        if (_inFlight == false)
            return;
        var lat = _values.GetValueOrDefault(MsfsSimVars.KeyOf(MsfsSimVars.Latitude));
        var lon = _values.GetValueOrDefault(MsfsSimVars.KeyOf(MsfsSimVars.Longitude));
        if (lat == 0 && lon == 0)
            return;   // no position yet (menu, loading)
        _sessionOpen = true;
        Publish(new TelemetryFrame(Clock.Now, _values));
    }

    private void OnObject(ReadOnlySpan<byte> message)
    {
        var request = SimConnectLayout.U32(message, SimConnectLayout.DataRequestId);
        var slot = request switch
        {
            RequestObjectsAircraft => 0,
            RequestObjectsHelicopters => 1,
            _ => -1
        };
        if (slot < 0 || !_objectsPending)
            return;
        var objectId = SimConnectLayout.U32(message, SimConnectLayout.DataObjectId);
        var entry = SimConnectLayout.U32(message, SimConnectLayout.DataEntryNumber);
        var outOf = SimConnectLayout.U32(message, SimConnectLayout.DataOutOf);
        if (outOf > 0 && objectId != _userObjectId && objectId != SimConnectLayout.ObjectIdUser
            && message.Length >= SimConnectLayout.DataStart + ObjectDataSize)
        {
            var at = SimConnectLayout.DataStart;
            var lat = SimConnectLayout.F64(message, at);
            var lon = SimConnectLayout.F64(message, at + 8);
            var altFt = SimConnectLayout.F64(message, at + 16);
            var bank = SimConnectLayout.F64(message, at + 24);
            var pitch = SimConnectLayout.F64(message, at + 32);
            var heading = SimConnectLayout.F64(message, at + 40);
            var title = SimConnectLayout.Text(message, at + 48, SimConnectLayout.StringLength);
            var atcId = SimConnectLayout.Text(message, at + 48 + SimConnectLayout.StringLength, SimConnectLayout.StringLength);
            var name = string.IsNullOrEmpty(atcId) ? title : title + " (" + atcId + ")";
            _objectLines.Add(TelemetryEventKinds.ObjectLine(objectId.ToString(CultureInfo.InvariantCulture),
                slot == 0 ? "Air+FixedWing" : "Air+Rotorcraft", name, null, lat, lon, altFt * TelemetryNav.FeetToMetres, -bank, -pitch, heading));
        }

        if (outOf == 0 || entry >= outOf)
        {
            _objectsDone[slot] = true;
            if (_objectsDone[0] && _objectsDone[1])
                FlushObjects();
        }
    }

    private void FlushObjects()
    {
        _objectsPending = false;
        RaiseEvent(TelemetryEventKinds.Objects, string.Join('\n', _objectLines));
        _objectLines.Clear();
    }

    private void OnSystemState(ReadOnlySpan<byte> message)
    {
        var request = SimConnectLayout.U32(message, SimConnectLayout.StateRequestId);
        var integer = SimConnectLayout.U32(message, SimConnectLayout.StateInteger);
        var text = SimConnectLayout.Text(message, SimConnectLayout.StateString, SimConnectLayout.MaxPath);
        switch (request)
        {
            case RequestStateFlightLoaded:
                SetFlight(text);
                break;
            case RequestStateAircraftLoaded:
                SetInfo(InfoAircraftFile, text);
                break;
            case RequestStateSim:
                SetInFlight(integer != 0, "Sim state");
                break;
        }
    }

    private void SetFlight(string file)
    {
        SetInfo(InfoFlightFile, file);
        SetInfo(TelemetryInfoKeys.Mission, string.IsNullOrEmpty(file) ? null : Path.GetFileNameWithoutExtension(file.Replace('/', '\\').Split('\\')[^1]));
    }

    private void SetInFlight(bool inFlight, string why)
    {
        if (_inFlight == inFlight)
            return;
        _inFlight = inFlight;
        if (inFlight)
        {
            if (!_sessionOpen)
            {
                _sessionOpen = true;
                RaiseEvent(TelemetryEventKinds.SessionStart, why);
            }
        }
        else
        {
            if (_sessionOpen)
            {
                _sessionOpen = false;
                RaiseEvent(TelemetryEventKinds.SessionEnd, why);
            }

            Clear();
        }
    }

    private void OnEvent(uint eventId, uint data, string? file)
    {
        if (eventId == 0 || eventId > SystemEvents.Count)
            return;
        switch (SystemEvents[(int)eventId - 1])
        {
            case "SimStart":
                SetInFlight(true, "SimStart");
                break;
            case "SimStop":
                SetInFlight(false, "SimStop");
                break;
            case "Sim":
                SetInFlight(data != 0, data != 0 ? "SimStart" : "SimStop");
                break;
            case "Pause":
                RaiseEvent(KindPause, data != 0 ? "1" : "0");
                break;
            case "Crashed":
                RaiseEvent(KindCrashed);
                break;
            case "CrashReset":
                RaiseEvent(KindCrashReset);
                break;
            case "PositionChanged":
                RaiseEvent(KindPositionChanged);
                break;
            case "FlightLoaded":
                SetFlight(file ?? "");
                break;
            case "AircraftLoaded":
                SetInfo(InfoAircraftFile, file);
                break;
            case "FlightPlanActivated":
                SetInfo(InfoFlightPlan, file);
                break;
            case "FlightPlanDeactivated":
                SetInfo(InfoFlightPlan, null);
                break;
        }
    }
}
