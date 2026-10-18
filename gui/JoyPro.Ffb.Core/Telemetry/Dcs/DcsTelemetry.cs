using System.Globalization;
using System.Text;

namespace JoyPro.Ffb.Core.Telemetry.Dcs;

/// <summary>The DCS telemetry line protocol of JoyPro's hook (UDP 127.0.0.1:<see cref="Port"/>).</summary>
/// <remarks>
/// The hook (Saved Games\DCS\Scripts\Hooks\JoyProFfb.lua, engine\DcsHooks\JoyProFfb.lua, installed by
/// "engine ffb telemetry setup dcs") sends these lines; several lines may share one datagram, separated by '\n':
/// <list type="bullet">
/// <item><c>t=12.340;ias=154.2;aoa=0.081;g=3.42;shells=412</c> - flight values, at most 100 lines per second of model
/// time. Pairs separated by ';', key and value by '='; keys are lower case [a-z0-9_]; numbers use '.' (a ',' is read
/// as '.') and may use exponents; "t" (LoGetModelTime) is required; unknown keys are accepted (as "dcs.&lt;key&gt;") so
/// the hook can grow without a JoyPro update; a pair that does not parse is skipped, not the whole line.</item>
/// <item><c>slow t=12.3;fuel_int=2450;chaff=60</c> - values read 10 times a second (route and wingmen once a second).
/// They are not a frame of their own: the source adds the latest of them to every following frame (for at most
/// <see cref="SlowValueLifetime"/> seconds of model time).</item>
/// <item><c>plane &lt;unit type&gt;</c> - the aircraft (on change and every 5 s); <c>start</c> / <c>exit</c> -
/// simulation start / stop; <c>noexport</c> - the multiplayer server forbids ownship export.</item>
/// <item><c>info &lt;key&gt;=&lt;text&gt;</c> - session facts (<see cref="TelemetryInfoKeys"/> map, mission, pilot,
/// game_version, plus unit, group, coalition, country, mission_date "yyyy-MM-dd", mission_time "seconds after
/// midnight at mission start", multiplayer 0/1, objects on/off, sensors on/off). Sent at start and every 10 s.</item>
/// <item><c>objs &lt;seq&gt; &lt;part&gt; &lt;t&gt;</c> + <c>obj ...</c> lines, the last datagram ending with
/// <c>objend &lt;seq&gt; &lt;parts&gt; &lt;count&gt;</c> - the other objects (LoGetWorldObjects) once a second when
/// the server allows object export. One obj line:
/// <c>id|level1.level2.level3.level4|name|coalition id|lat|lon|alt|bank|pitch|heading|unit|group|country|flags</c>
/// (angles in rad, flags letters H human, R radar on, J jamming, K IR jamming, S static, I invisible, A AI on). A snapshot
/// becomes one <see cref="TelemetryEventKinds.Objects"/> event when all its parts arrived.</item>
/// <item><c>ev &lt;name&gt; &lt;text&gt;</c> - game events (onGameEvent: takeoff, landing, kill, crash, eject, ...;
/// pause, resume; mode = navigation master/submode and ACS mode) -&gt; event "dcs.&lt;name&gt;".
/// <c>msg &lt;from&gt;|&lt;text&gt;</c> - chat -&gt; <see cref="TelemetryEventKinds.Message"/>.</item>
/// </list>
/// The keys and their LoGet* sources are <see cref="Channels"/>. The overlay hook (port 1992) is a separate file: both
/// can be installed, and the GUI's overlay listener and the force feedback process never compete for one port.
/// </remarks>
public static class DcsTelemetryProtocol
{
    public const int Port = 41992;
    public const string KeyPrefix = "dcs.";

    /// <summary>How long (model time) a value of a "slow" line is added to the frames after it.</summary>
    public const double SlowValueLifetime = 3;

    /// <param name="RateHz">How often the hook sends it (100 = every frame line, 10 / 1 = in the "slow" line).</param>
    public sealed record Channel(string WireKey, string Title, string Unit, double Min, double Max, string Category, string LoGet, double RateHz = 100)
    {
        public string Key => KeyPrefix + WireKey;
        public TelemetryDescriptor Descriptor => new(Key, Title, Unit, Min, Max, Category, LoGet);
    }

    /// <summary>The LoGetMCPState() flags the hook sends with a short key (other true/false flags come as "mcp_&lt;name&gt;").</summary>
    public static IReadOnlyList<(string Name, string Key, string Title)> McpFlags { get; } =
    [
        ("LeftEngineFailure", "fail_eng_l", "Left engine failure"),
        ("RightEngineFailure", "fail_eng_r", "Right engine failure"),
        ("HydraulicsFailure", "fail_hyd", "Hydraulics failure"),
        ("ACSFailure", "fail_acs", "ACS failure"),
        ("AutopilotFailure", "fail_ap", "Autopilot failure"),
        ("AutopilotOn", "ap_on", "Autopilot on"),
        ("MasterWarning", "master_warn", "Master warning"),
        ("LeftTailPlaneFailure", "fail_tail_l", "Left tail plane failure"),
        ("RightTailPlaneFailure", "fail_tail_r", "Right tail plane failure"),
        ("LeftAileronFailure", "fail_ail_l", "Left aileron failure"),
        ("RightAileronFailure", "fail_ail_r", "Right aileron failure"),
        ("CanopyOpen", "canopy_open", "Canopy open"),
        ("CannonFailure", "fail_gun", "Cannon failure"),
        ("LeftMainPumpFailure", "fail_pump_l", "Left main fuel pump failure"),
        ("RightMainPumpFailure", "fail_pump_r", "Right main fuel pump failure"),
        ("LeftWingPumpFailure", "fail_wpump_l", "Left wing fuel pump failure"),
        ("RightWingPumpFailure", "fail_wpump_r", "Right wing fuel pump failure"),
        ("RadarFailure", "fail_radar", "Radar failure"),
        ("EOSFailure", "fail_eos", "EOS failure"),
        ("MLWSFailure", "fail_mlws", "Missile launch warning failure"),
        ("RWSFailure", "fail_rws", "Radar warning failure"),
        ("ECMFailure", "fail_ecm", "ECM failure"),
        ("GearFailure", "fail_gear", "Gear failure"),
        ("MFDFailure", "fail_mfd", "MFD failure"),
        ("HUDFailure", "fail_hud", "HUD failure"),
        ("HelmetFailure", "fail_hmd", "Helmet display failure"),
        ("FuelTankDamage", "fuel_tank_dmg", "Fuel tank damage"),
    ];

    private const double Pi = Math.PI;

    public static IReadOnlyList<Channel> Channels { get; } =
    [
        // ---- every frame line (100 Hz) - the force feedback graph reads these (AircraftTelemetryDeriver.ForDcs)
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
        new("pitch", "Pitch (ADI)", "rad", -Pi / 2, Pi / 2, "Attitude", "LoGetADIPitchBankYaw() #1"),
        new("bank", "Bank (ADI)", "rad", -Pi, Pi, "Attitude", "LoGetADIPitchBankYaw() #2"),
        new("yaw", "Yaw (ADI)", "rad", -Pi, Pi, "Attitude", "LoGetADIPitchBankYaw() #3"),
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
        new("vx", "Velocity north", "m/s", -700, 700, "Flight", "LoGetVectorVelocity().x (world: x north)"),
        new("vy", "Velocity up", "m/s", -300, 300, "Flight", "LoGetVectorVelocity().y (world: y up)"),
        new("vz", "Velocity east", "m/s", -700, 700, "Flight", "LoGetVectorVelocity().z (world: z east)"),
        new("lat", "Latitude", "deg", -90, 90, "Position", "LoGetSelfData().LatLongAlt.Lat"),
        new("lon", "Longitude", "deg", -180, 180, "Position", "LoGetSelfData().LatLongAlt.Long"),
        new("alt_msl", "Altitude (world position)", "m", 0, 20000, "Position", "LoGetSelfData().LatLongAlt.Alt"),
        new("heading", "True heading", "rad", 0, 2 * Pi, "Attitude", "LoGetSelfData().Heading (clockwise from north)"),
        new("true_pitch", "Pitch (world)", "rad", -Pi / 2, Pi / 2, "Attitude", "LoGetSelfData().Pitch"),
        new("true_bank", "Bank (world)", "rad", -Pi, Pi, "Attitude", "LoGetSelfData().Bank"),
        new("pos_x", "Map position north", "m", -1e6, 1e6, "Position", "LoGetSelfData().Position.x"),
        new("pos_y", "Map position up", "m", 0, 20000, "Position", "LoGetSelfData().Position.y"),
        new("pos_z", "Map position east", "m", -1e6, 1e6, "Position", "LoGetSelfData().Position.z"),

        // ---- the "slow" line (10 Hz; 1 Hz where noted)
        new("coalition", "Coalition", "0 neutral, 1 red, 2 blue", 0, 2, "Session", "LoGetSelfData().CoalitionID", 10),
        new("country", "Country", "id", 0, 100, "Session", "LoGetSelfData().Country (Scripts\\Database\\db_countries.lua)", 10),
        new("temp_l", "Engine temperature left", "°C", 0, 1000, "Engine", "LoGetEngineInfo().Temperature.left", 10),
        new("temp_r", "Engine temperature right", "°C", 0, 1000, "Engine", "LoGetEngineInfo().Temperature.right", 10),
        new("fuel_flow_l", "Fuel flow left", "kg/s", 0, 5, "Engine", "LoGetEngineInfo().FuelConsumption.left", 10),
        new("fuel_flow_r", "Fuel flow right", "kg/s", 0, 5, "Engine", "LoGetEngineInfo().FuelConsumption.right", 10),
        new("hyd_l", "Hydraulic pressure left", "kg/cm²", 0, 300, "Engine", "LoGetEngineInfo().HydraulicPressure.left", 10),
        new("hyd_r", "Hydraulic pressure right", "kg/cm²", 0, 300, "Engine", "LoGetEngineInfo().HydraulicPressure.right", 10),
        new("fuel_int", "Fuel internal", "kg", 0, 10000, "Fuel", "LoGetEngineInfo().fuel_internal", 10),
        new("fuel_ext", "Fuel external", "kg", 0, 10000, "Fuel", "LoGetEngineInfo().fuel_external", 10),
        new("fuel", "Fuel total", "kg", 0, 20000, "Fuel", "LoGetEngineInfo() fuel_internal + fuel_external", 10),
        new("elev_l", "Elevator left", "-1..1", -1, 1, "Controls", "LoGetMechInfo().controlsurfaces.elevator.left", 10),
        new("elev_r", "Elevator right", "-1..1", -1, 1, "Controls", "LoGetMechInfo().controlsurfaces.elevator.right", 10),
        new("ail_l", "Aileron left", "-1..1", -1, 1, "Controls", "LoGetMechInfo().controlsurfaces.eleron.left", 10),
        new("ail_r", "Aileron right", "-1..1", -1, 1, "Controls", "LoGetMechInfo().controlsurfaces.eleron.right", 10),
        new("rud_l", "Rudder left", "-1..1", -1, 1, "Controls", "LoGetMechInfo().controlsurfaces.rudder.left", 10),
        new("rud_r", "Rudder right", "-1..1", -1, 1, "Controls", "LoGetMechInfo().controlsurfaces.rudder.right", 10),
        new("wheelbrake", "Wheel brakes", "0..1", 0, 1, "Mechanics", "LoGetMechInfo().wheelbrakes.value", 10),
        new("canopy", "Canopy", "0..1", 0, 1, "Mechanics", "LoGetMechInfo().canopy.value", 10),
        new("refuel_boom", "Refuelling probe / boom", "0..1", 0, 1, "Mechanics", "LoGetMechInfo().refuelingboom.value", 10),
        new("chute", "Drag chute", "0..1", 0, 1, "Mechanics", "LoGetMechInfo().parachute.value", 10),
        new("hook", "Tail hook", "0..1", 0, 1, "Mechanics", "LoGetMechInfo().hook.value", 10),
        new("wing_fold", "Wing fold", "0..1", 0, 1, "Mechanics", "LoGetMechInfo().wing.value", 10),
        new("air_intake", "Air intake", "0..1", 0, 1, "Mechanics", "LoGetMechInfo().airintake.value", 10),
        new("nose_flap", "Nose flap", "0..1", 0, 1, "Mechanics", "LoGetMechInfo().noseflap.value", 10),
        new("station", "Selected station", "", 0, 20, "Weapons", "LoGetPayloadInfo().CurrentStation (0 = none)", 10),
        new("station_count", "Stores on the selected station", "", 0, 20, "Weapons", "LoGetPayloadInfo().Stations[CurrentStation].count", 10),
        new("stations", "Stations", "", 0, 20, "Weapons", "LoGetPayloadInfo().Stations count", 10),
        new("stores", "Stores left", "", 0, 100, "Weapons", "LoGetPayloadInfo().Stations sum of count", 10),
        new("chaff", "Chaff left", "", 0, 240, "Weapons", "LoGetSnares().chaff", 10),
        new("flare", "Flares left", "", 0, 240, "Weapons", "LoGetSnares().flare", 10),
        new("ralt", "Radar altimeter", "m", 0, 1500, "Navigation", "LoGetRadarAltimeter()", 10),
        new("mag_heading", "Magnetic heading", "rad", 0, 2 * Pi, "Navigation", "LoGetMagneticYaw()", 10),
        new("gs_dev", "Glide slope deviation", "-1..1", -1, 1, "Navigation", "LoGetGlideDeviation()", 10),
        new("loc_dev", "Localizer deviation", "-1..1", -1, 1, "Navigation", "LoGetSideDeviation()", 10),
        new("slip_ball", "Slip ball", "-1..1", -1, 1, "Flight", "LoGetSlipBallPosition()", 10),
        new("baro", "Static pressure", "mm Hg", 100, 800, "Atmosphere", "LoGetBasicAtmospherePressure()", 10),
        new("shake", "Shake amplitude", "", 0, 1, "Flight", "LoGetShakeAmplitude()", 10),
        new("time_accel", "Time acceleration", "x", 0, 16, "Time", "LoGetModelTimeAcceleration()", 10),
        new("in_air", "In the air", "0/1", 0, 1, "Flight", "LoGetInAir()", 10),
        new("wind_x", "Wind north", "m/s", -50, 50, "Atmosphere", "LoGetVectorWindVelocity().x", 10),
        new("wind_y", "Wind up", "m/s", -20, 20, "Atmosphere", "LoGetVectorWindVelocity().y", 10),
        new("wind_z", "Wind east", "m/s", -50, 50, "Atmosphere", "LoGetVectorWindVelocity().z", 10),
        new("req_roll", "Required roll (ACS)", "rad", -Pi, Pi, "Navigation", "LoGetNavigationInfo().Requirements.roll", 10),
        new("req_pitch", "Required pitch (ACS)", "rad", -Pi / 2, Pi / 2, "Navigation", "LoGetNavigationInfo().Requirements.pitch", 10),
        new("req_speed", "Required speed (ACS)", "m/s", 0, 700, "Navigation", "LoGetNavigationInfo().Requirements.speed", 10),
        new("req_vs", "Required vertical speed (ACS)", "m/s", -150, 150, "Navigation", "LoGetNavigationInfo().Requirements.vertical_speed", 10),
        new("req_alt", "Required altitude (ACS)", "m", 0, 20000, "Navigation", "LoGetNavigationInfo().Requirements.altitude", 10),
        new("autothrust", "Autothrust", "0/1", 0, 1, "Navigation", "LoGetNavigationInfo().ACS.autothrust", 10),
        new("targets", "Targets", "", 0, 50, "Sensors", "LoGetTargetInformation() count (sensor export)", 10),
        new("locked", "Locked targets", "", 0, 10, "Sensors", "LoGetLockedTargetInformation() count (sensor export)", 10),
        new("lock_id", "Locked target id", "", 0, 1e9, "Sensors", "LoGetLockedTargetInformation()[first].ID", 10),
        new("lock_dist", "Locked target distance", "m", 0, 200000, "Sensors", "LoGetLockedTargetInformation()[first].distance", 10),
        new("lock_vc", "Locked target closing speed", "m/s", -1000, 1000, "Sensors", "LoGetLockedTargetInformation()[first].convergence_velocity", 10),
        new("lock_mach", "Locked target Mach", "", 0, 3, "Sensors", "LoGetLockedTargetInformation()[first].mach", 10),
        new("lock_aspect", "Locked target aspect", "rad", -Pi, Pi, "Sensors", "LoGetLockedTargetInformation()[first].delta_psi", 10),
        new("wpt", "Next waypoint", "", 0, 50, "Navigation", "LoGetRoute().goto_point.this_point_num", 1),
        new("wpt_dist", "Distance to the next waypoint", "m", 0, 500000, "Navigation", "LoGetRoute().goto_point.world_point - own position", 1),
        new("wpts", "Waypoints", "", 0, 50, "Navigation", "LoGetRoute().route count", 1),
        new("wingmen", "Wingmen", "", 0, 4, "Navigation", "LoGetWingInfo() count", 1),
        .. McpFlags.Select(f => new Channel(f.Key, f.Title, "0/1", 0, 1, "Warnings", "LoGetMCPState()." + f.Name, 10)),
    ];

    public static IReadOnlyList<TelemetryDescriptor> Descriptors { get; } = Channels.Select(c => c.Descriptor).ToArray();

    /// <summary>Where the ACMI export finds position and attitude (degrees for angles, as <see cref="TelemetryNav"/> wants).</summary>
    public static IReadOnlyList<TelemetryNavMapping> NavMappings { get; } =
    [
        new(TelemetryNav.Latitude, KeyPrefix + "lat"),
        new(TelemetryNav.Longitude, KeyPrefix + "lon"),
        new(TelemetryNav.Altitude, KeyPrefix + "alt_msl"),
        new(TelemetryNav.Roll, KeyPrefix + "true_bank", TelemetryNav.RadToDeg),
        new(TelemetryNav.Pitch, KeyPrefix + "true_pitch", TelemetryNav.RadToDeg),
        new(TelemetryNav.Yaw, KeyPrefix + "heading", TelemetryNav.RadToDeg),
        new(TelemetryNav.Agl, KeyPrefix + "agl"),
        new(TelemetryNav.Ias, KeyPrefix + "ias"),
        new(TelemetryNav.Tas, KeyPrefix + "tas"),
        new(TelemetryNav.Mach, KeyPrefix + "mach"),
        new(TelemetryNav.Aoa, KeyPrefix + "aoa", TelemetryNav.RadToDeg),
        new(TelemetryNav.Aos, KeyPrefix + "aos", TelemetryNav.RadToDeg),
        new(TelemetryNav.VerticalG, KeyPrefix + "g"),
        new(TelemetryNav.LongitudinalG, KeyPrefix + "gx"),
        new(TelemetryNav.LateralG, KeyPrefix + "gz"),
        new(TelemetryNav.Gear, KeyPrefix + "gear"),
        new(TelemetryNav.Flaps, KeyPrefix + "flaps"),
        new(TelemetryNav.Airbrakes, KeyPrefix + "speedbrake"),
        new(TelemetryNav.FuelKg, KeyPrefix + "fuel"),
        new(TelemetryNav.VerticalSpeed, KeyPrefix + "vv"),
    ];

    /// <summary>Parses one telemetry line. False for "plane"/"exit"/"slow"/... lines, empty lines and lines without "t".</summary>
    public static bool TryParseFrame(string line, out TelemetryFrame? frame)
    {
        frame = null;
        if (!TryParseValues(line, out var time, out var values))
            return false;
        frame = new TelemetryFrame(time, values);
        return true;
    }

    /// <summary>The key=value pairs of a frame or slow line (without "slow "), keys with <see cref="KeyPrefix"/>.</summary>
    public static bool TryParseValues(string line, out double time, out Dictionary<string, double> values)
    {
        values = new Dictionary<string, double>(StringComparer.Ordinal);
        double? t = null;
        foreach (var part in line.Trim().Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = part[..eq].Trim();
            // A decimal comma (a Lua state with a German C locale) is read as a point: ';' and '=' separate the pairs.
            if (!IsKey(key) || !TryNumber(part[(eq + 1)..], out var value))
                continue;
            if (key == "t")
                t = value;
            values[KeyPrefix + key] = value;
        }

        time = t ?? 0;
        return t is not null;
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

    /// <summary>One "obj" line of the hook (without "obj ") as a line of an <see cref="TelemetryEventKinds.Objects"/>
    /// snapshot (<see cref="TelemetryEventKinds.ObjectLine"/>). False when it has no id or too few fields.</summary>
    public static bool TryConvertObject(string wire, out string line)
    {
        line = "";
        var f = wire.Split('|');
        if (f.Length < 10 || f[0].Trim().Length == 0)
            return false;
        var flags = f.Length > 13 ? f[13] : "";
        double? Num(int i) => i < f.Length && TryNumber(f[i], out var v) ? v : null;
        double? Deg(int i) => Num(i) is { } r ? r * TelemetryNav.RadToDeg : null;
        var yaw = Deg(9);
        if (yaw is { } y)
            yaw = (y % 360 + 360) % 360;
        line = TelemetryEventKinds.ObjectLine(f[0].Trim(), DcsObjectTypes.ToTacview(f[1], flags), f[2].Trim(), Coalition(f[3]),
            Num(4), Num(5), Num(6), Deg(7), Deg(8), yaw);
        return true;
    }

    /// <summary>DCS coalition id (0 neutral, 1 red, 2 blue) as a word; other text unchanged.</summary>
    public static string Coalition(string text) => text.Trim() switch
    {
        "0" => "Neutral",
        "1" => "Red",
        "2" => "Blue",
        var other => other
    };

    internal static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    internal static bool IsKey(string key) =>
        key.Length is > 0 and <= 32 && key.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}

/// <summary>DCS object types (Type.level1..4, Scripts\Database\wsTypes.lua) in Tacview's words.</summary>
public static class DcsObjectTypes
{
    /// <param name="levels">"level1.level2.level3.level4" as the hook sends it.</param>
    /// <param name="flags">The hook's flag letters (S = static object).</param>
    public static string? ToTacview(string levels, string flags = "")
    {
        var p = levels.Split('.');
        int L(int i) => i < p.Length && int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        int l1 = L(0), l2 = L(1), l3 = L(2);
        var isStatic = flags.Contains('S');
        switch (l1)
        {
            case 1: // wsType_Air
                return l2 switch
                {
                    1 => "Air+FixedWing",
                    2 => "Air+Rotorcraft",
                    3 => l3 switch { 31 => "Misc+Decoy", 43 => "Misc+Container", 35 => "Misc+Shrapnel", _ => "Misc" },
                    _ => "Air"
                };
            case 2: // wsType_Ground
                if (isStatic || l2 == 9)
                    return "Ground+Static";
                return l2 switch
                {
                    16 => "Ground+AntiAircraft",
                    17 => "Ground+Heavy+Armor+Vehicle+Tank",
                    20 => "Ground+Light+Human+Infantry",
                    _ => "Ground+Vehicle"
                };
            case 3: // wsType_Navy
                return l3 switch
                {
                    12 => "Sea+Watercraft+AircraftCarrier",
                    13 or 14 => "Sea+Watercraft+Warship",
                    16 => "Sea+Watercraft+Submarine",
                    _ => "Sea+Watercraft"
                };
            case 4: // wsType_Weapon
                return l2 switch
                {
                    4 => "Weapon+Missile",
                    5 => "Weapon+Bomb",
                    6 => "Projectile+Shell",
                    7 => "Weapon+Rocket",
                    8 => "Weapon+Torpedo",
                    _ => "Weapon"
                };
            case 5: // wsType_Static
                return l2 switch
                {
                    13 => "Ground+Static+Aerodrome",
                    14 => "Misc+Explosion",
                    15 => "Misc+Container",
                    _ => "Ground+Static+Building"
                };
            case 6: // wsType_Destroyed
                return "Ground+Static";
            case 201: // wsType_Point
                return "Navaid+Static";
            default:
                return null;
        }
    }
}

/// <summary>Live DCS telemetry from the hook's lines.</summary>
/// <remarks>"noexport" means the multiplayer server forbids ownship export (LoIsOwnshipExportAllowed() false): the
/// hook then sends no values, and <see cref="ExportNotAllowed"/> lets the status line say why. For the session recorder
/// the source raises <see cref="TelemetryEventKinds.SessionStart"/> ("start"), <see cref="TelemetryEventKinds.SessionEnd"/>
/// ("exit"), <see cref="TelemetryEventKinds.Objects"/> (a complete objs..objend snapshot), Message (chat) and
/// "dcs.&lt;name&gt;" (game events), sets the session facts ("plane", "info") and hands every datagram to
/// <see cref="FrameTelemetrySource.RawReceived"/> while a recorder listens (streams "udp" frames, "udp:objects" object
/// snapshots, "udp:notice" the rest: <see cref="RawStreamOf"/>).</remarks>
public sealed class DcsTelemetrySource : FrameTelemetrySource
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (double Value, double Time)> _slow = new(StringComparer.Ordinal);

    // The objects snapshot being received.
    private long _objSeq = -1;
    private readonly HashSet<int> _objParts = [];
    private readonly List<string> _objLines = [];
    private bool _objReceiving;

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

    /// <summary>Complete object snapshots received (one <see cref="TelemetryEventKinds.Objects"/> event each).</summary>
    public long ObjectSnapshots { get; private set; }

    /// <summary>The lines of the last complete object snapshot (<see cref="TelemetryEventKinds.ObjectLine"/> format).</summary>
    public IReadOnlyList<string> LastObjects { get; private set; } = [];

    public override IReadOnlyList<TelemetryNavMapping> NavMappings => DcsTelemetryProtocol.NavMappings;

    /// <summary>Raw stream of frame datagrams (a frame line, with or without a slow line before it).</summary>
    public const string RawFrames = "udp";

    /// <summary>Raw stream of object snapshot datagrams (several per snapshot, a few ms apart: a recorder that thins
    /// raw input out by rate must keep all of them or none).</summary>
    public const string RawObjects = "udp:objects";

    /// <summary>Raw stream of everything else (plane, start, exit, noexport, info, ev, msg): rare, each one matters.</summary>
    public const string RawNotices = "udp:notice";

    /// <summary>Which raw stream a datagram belongs to (<see cref="RawFrames"/>, <see cref="RawObjects"/>, <see cref="RawNotices"/>).</summary>
    public static string RawStreamOf(ReadOnlySpan<byte> datagram) =>
        datagram.StartsWith("t="u8) || datagram.StartsWith("slow "u8) ? RawFrames
        : datagram.StartsWith("objs "u8) ? RawObjects
        : RawNotices;

    /// <summary>Handles one datagram (one or more lines).</summary>
    public void Ingest(ReadOnlySpan<byte> datagram)
    {
        if (WantsRaw)
            PublishRaw(RawStreamOf(datagram), datagram);
        IngestText(Encoding.UTF8.GetString(datagram));
    }

    public void Ingest(string text)
    {
        if (WantsRaw)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            PublishRaw(RawStreamOf(bytes), bytes);
        }

        IngestText(text);
    }

    private void IngestText(string text)
    {
        var notice = false;
        lock (_gate)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue;
                if (line.StartsWith("obj ", StringComparison.Ordinal))
                {
                    if (_objReceiving && DcsTelemetryProtocol.TryConvertObject(line[4..], out var obj))
                        _objLines.Add(obj);
                }
                else if (CommandLine(line) is { } changed)
                {
                    notice |= changed;
                }
                else
                {
                    notice |= SimpleLine(line);
                }
            }
        }

        if (notice)
            Notice?.Invoke();
    }

    // A frame, "start", "exit", "noexport".
    private bool SimpleLine(string line)
    {
        switch (line)
        {
            case "exit":
                RaiseEvent(TelemetryEventKinds.SessionEnd, "mission ended");
                Aircraft = null;
                ExportNotAllowed = false;
                ResetSession();
                foreach (var key in SessionInfo.Keys)
                    SetInfo(key, null);
                Clear();
                return true;
            case "start":
                ResetSession();
                RaiseEvent(TelemetryEventKinds.SessionStart, "simulation start");
                return false;
            case "noexport":
                var changed = !ExportNotAllowed;
                ExportNotAllowed = true;
                return changed;
        }

        if (!DcsTelemetryProtocol.TryParseValues(line, out var time, out var values))
            return false;
        if (_slow.Count > 0)
        {
            List<string>? stale = null;
            foreach (var (key, (value, at)) in _slow)
            {
                var age = time - at;
                if (age is < -1 or > DcsTelemetryProtocol.SlowValueLifetime)
                    (stale ??= []).Add(key);
                else
                    values.TryAdd(key, value);
            }

            if (stale is not null)
                foreach (var key in stale)
                    _slow.Remove(key);
        }

        FramesAccepted++;
        ExportNotAllowed = false;
        Publish(new TelemetryFrame(time, values));
        return false;
    }

    // "<word> <rest>" lines; null = not one (a frame line).
    private bool? CommandLine(string line)
    {
        var space = line.IndexOf(' ');
        if (space <= 0)
            return null;
        var word = line[..space];
        var rest = line[(space + 1)..].Trim();
        switch (word)
        {
            case "plane":
            {
                var changed = rest != Aircraft;
                Aircraft = rest;
                SetInfo(TelemetryInfoKeys.Aircraft, rest);
                return changed;
            }
            case "slow":
                if (DcsTelemetryProtocol.TryParseValues(rest, out var time, out var values))
                    foreach (var (key, value) in values)
                        if (key != DcsTelemetryProtocol.KeyPrefix + "t")
                            _slow[key] = (value, time);
                return false;
            case "info":
            {
                var eq = rest.IndexOf('=');
                if (eq > 0 && DcsTelemetryProtocol.IsKey(rest[..eq]))
                {
                    var value = rest[(eq + 1)..].Trim();
                    SetInfo(rest[..eq], value.Length > 300 ? value[..300] : value);
                }

                return false;
            }
            case "objs":
            {
                // objs <seq> <part> <t>
                var f = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 2 && long.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq) &&
                    int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var part))
                {
                    if (seq != _objSeq)
                    {
                        // A new snapshot: one that never completed is dropped (an object missing from a snapshot is gone).
                        _objSeq = seq;
                        _objParts.Clear();
                        _objLines.Clear();
                    }

                    _objParts.Add(part);
                    _objReceiving = true;
                }
                else
                {
                    _objReceiving = false;
                }

                return false;
            }
            case "objend":
            {
                // objend <seq> <parts> <count>
                var f = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 2 && long.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq) &&
                    int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parts) &&
                    seq == _objSeq && _objParts.Count == parts && Enumerable.Range(1, parts).All(_objParts.Contains))
                {
                    LastObjects = _objLines.ToArray();
                    ObjectSnapshots++;
                    RaiseEvent(TelemetryEventKinds.Objects, string.Join('\n', LastObjects));
                }

                _objParts.Clear();
                _objLines.Clear();
                _objReceiving = false;
                return false;
            }
            case "ev":
            {
                var sp = rest.IndexOf(' ');
                var name = (sp < 0 ? rest : rest[..sp]).ToLowerInvariant();
                if (DcsTelemetryProtocol.IsKey(name))
                    RaiseEvent(DcsTelemetryProtocol.KeyPrefix + name, sp < 0 ? "" : rest[(sp + 1)..].Trim());
                return false;
            }
            case "msg":
            {
                var bar = rest.IndexOf('|');
                RaiseEvent(TelemetryEventKinds.Message, bar < 0 ? rest : rest[..bar] + ": " + rest[(bar + 1)..]);
                return false;
            }
            default:
                return null;
        }
    }

    private void ResetSession()
    {
        _slow.Clear();
        _objSeq = -1;
        _objParts.Clear();
        _objLines.Clear();
        _objReceiving = false;
    }
}
