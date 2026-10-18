using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace JoyPro.Ffb.Core.Telemetry.XPlane;

/// <summary>How often X-Plane sends a dataref (the RREF "freq").</summary>
public enum XPlaneRate
{
    /// <summary>The feed's request rate (default 20 per second): flight values.</summary>
    Fast,

    /// <summary>Twice a second: switches, dials, weights, weather.</summary>
    Slow
}

/// <summary>One dataref JoyPro subscribes to. <paramref name="Index"/> is the RREF index X-Plane echoes back.</summary>
/// <remarks>Indices are part of recordings (the raw datagrams carry only index + value): never renumber a dataref,
/// only append new ones.</remarks>
public sealed record XPlaneDataref(int Index, string Path, string Key, string Title, string Unit, double Min, double Max, string Category, XPlaneRate Rate)
{
    public TelemetryDescriptor Descriptor => new(Key, Title, Unit, Min, Max, Category, Path);
}

/// <summary>X-Plane 11 / 12 UDP "RREF" protocol ("Exchanging Data with X-Plane.rtfd" in the install's Instructions folder).</summary>
/// <remarks>
/// <para>Request (to the sim, UDP 49000): "RREF\0" + int32 freq (sends per second, 0 = stop) + int32 index (ours) +
/// char[400] dataref path, null terminated and padded: 413 bytes. Array elements are asked for as "path[i]".</para>
/// <para>Answer (to the address and port the request came from): "RREF" + one internal byte + n × (int32 index,
/// float32 value), little endian, at most 183 pairs per datagram (more follow in further datagrams). Every value is a
/// float, even ints and doubles: latitude / longitude arrive with about 0.5 m resolution.</para>
/// <para>X-Plane takes at most about 148 requests per sim frame: send them in batches of 100 with 100 ms between.</para>
/// <para>Index ranges: 0..999 flight values (<see cref="Datarefs"/>), 1000..1999 text bytes (aircraft name, ICAO,
/// tail number), 2000..2999 traffic (TCAS targets), 3000 the X-Plane version.</para>
/// </remarks>
public static class XPlaneProtocol
{
    public const int DefaultPort = 49000;
    public const string KeyPrefix = "xp.";
    public const int PathLength = 400;
    public const int RequestLength = 5 + 4 + 4 + PathLength;  // 413
    public const int HeaderLength = 5;
    public const int PairLength = 8;
    public const int MaxPairsPerDatagram = 183;
    public const int RequestBatch = 100;
    public static readonly TimeSpan RequestBatchDelay = TimeSpan.FromMilliseconds(100);

    public const int TextIndexBase = 1000;
    public const int TrafficIndexBase = 2000;
    public const int VersionIndex = 3000;

    /// <summary>Text rate (aircraft name bytes), per second.</summary>
    public const int TextRate = 1;

    /// <summary>Traffic rate (TCAS target positions), per second.</summary>
    public const int TrafficRate = 2;

    public const int SlowRate = 2;

    /// <summary>The TCAS target slots read (index 0 of the TCAS arrays is the user's own aircraft).</summary>
    public const int TrafficTargets = 19;

    public static readonly string[] TrafficFields = ["lat", "lon", "ele", "psi", "the", "phi"];

    /// <summary>Builds one 413-byte RREF request. A path longer than 399 characters is rejected.</summary>
    public static byte[] BuildRequest(int frequency, int index, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var ascii = Encoding.ASCII.GetBytes(path);
        if (ascii.Length >= PathLength)
            throw new ArgumentException("Dataref path too long: " + path, nameof(path));
        var packet = new byte[RequestLength];
        "RREF"u8.CopyTo(packet);
        packet[4] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(5), frequency);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(9), index);
        ascii.CopyTo(packet, 13);
        return packet;
    }

    /// <summary>Reads one request (tests, the fake sim).</summary>
    public static bool TryParseRequest(ReadOnlySpan<byte> packet, out int frequency, out int index, out string path)
    {
        frequency = index = 0;
        path = "";
        if (packet.Length < 13 || !packet[..4].SequenceEqual("RREF"u8) || packet[4] != 0)
            return false;
        frequency = BinaryPrimitives.ReadInt32LittleEndian(packet[5..]);
        index = BinaryPrimitives.ReadInt32LittleEndian(packet[9..]);
        var text = packet[13..];
        var end = text.IndexOf((byte)0);
        path = Encoding.ASCII.GetString(end < 0 ? text : text[..end]);
        return true;
    }

    /// <summary>Builds an answer datagram (tests, the fake sim).</summary>
    public static byte[] BuildAnswer(IReadOnlyList<(int Index, float Value)> pairs)
    {
        var packet = new byte[HeaderLength + pairs.Count * PairLength];
        "RREF,"u8.CopyTo(packet);
        for (var i = 0; i < pairs.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderLength + i * PairLength), pairs[i].Index);
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(HeaderLength + i * PairLength + 4), pairs[i].Value);
        }

        return packet;
    }

    /// <summary>Reads an answer datagram. A trailing partial pair is ignored.</summary>
    /// <returns>The number of pairs, or -1 when the datagram is not an RREF answer.</returns>
    public static int ParseAnswer(ReadOnlySpan<byte> datagram, Action<int, float> pair)
    {
        if (datagram.Length < HeaderLength || !datagram[..4].SequenceEqual("RREF"u8))
            return -1;
        var count = (datagram.Length - HeaderLength) / PairLength;
        for (var i = 0; i < count; i++)
        {
            var offset = HeaderLength + i * PairLength;
            pair(BinaryPrimitives.ReadInt32LittleEndian(datagram[offset..]), BinaryPrimitives.ReadSingleLittleEndian(datagram[(offset + 4)..]));
        }

        return count;
    }

    // ------------------------------------------------------------------ the catalogue

    public static IReadOnlyList<XPlaneDataref> Datarefs { get; } = BuildCatalogue();

    public static IReadOnlyList<TelemetryDescriptor> Descriptors { get; } =
        [.. Datarefs.Select(d => d.Descriptor), new TelemetryDescriptor(KeyPrefix + "traffic_count", "Other aircraft (TCAS)", "", 0, 64, "Traffic", "sim/cockpit2/tcas/indicators/tcas_num_acf")];

    /// <summary>The text datarefs read byte by byte (index = <see cref="TextIndexBase"/> + offset).</summary>
    public static IReadOnlyList<TextDataref> Texts { get; } =
    [
        new("name", "sim/aircraft/view/acf_ui_name", 0, 48),
        new("descrip", "sim/aircraft/view/acf_descrip", 100, 40),
        new("icao", "sim/aircraft/view/acf_ICAO", 200, 8),
        new("tailnum", "sim/aircraft/view/acf_tailnum", 300, 12),
    ];

    public sealed record TextDataref(string Name, string Path, int Offset, int Length)
    {
        public int FirstIndex => TextIndexBase + Offset;
    }

    /// <summary>Every subscription JoyPro sends: (index, path, frequency per second).</summary>
    public static IReadOnlyList<(int Index, string Path, int Frequency)> Subscriptions(double rateHz)
    {
        var fast = (int)Math.Clamp(Math.Round(rateHz), 1, 99);
        var slow = Math.Min(SlowRate, fast);
        var list = new List<(int, string, int)>();
        foreach (var d in Datarefs)
            list.Add((d.Index, d.Path, d.Rate == XPlaneRate.Fast ? fast : slow));
        foreach (var t in Texts)
            for (var i = 0; i < t.Length; i++)
                list.Add((t.FirstIndex + i, t.Path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", TextRate));
        list.Add((TrafficIndexBase, "sim/cockpit2/tcas/indicators/tcas_num_acf", TrafficRate));
        for (var target = 1; target <= TrafficTargets; target++)
        {
            for (var f = 0; f < TrafficFields.Length; f++)
                list.Add((TrafficIndex(target, f), "sim/cockpit2/tcas/targets/position/" + TrafficFields[f] + "[" + target.ToString(CultureInfo.InvariantCulture) + "]", TrafficRate));
            list.Add((TrafficIndex(target, TrafficFields.Length), "sim/cockpit2/tcas/targets/modeS_id[" + target.ToString(CultureInfo.InvariantCulture) + "]", TrafficRate));
        }

        list.Add((VersionIndex, "sim/version/xplane_internal_version", TextRate));
        return list;
    }

    /// <summary>RREF index of a traffic field (target 1..<see cref="TrafficTargets"/>; field 0..5 = lat, lon, ele, psi,
    /// the, phi; 6 = Mode S id).</summary>
    public static int TrafficIndex(int target, int field) => TrafficIndexBase + 1 + (target - 1) * (TrafficFields.Length + 1) + field;

    private static List<XPlaneDataref> BuildCatalogue()
    {
        var list = new List<XPlaneDataref>();

        void Add(string path, string key, string title, string unit, double min, double max, string category, XPlaneRate rate = XPlaneRate.Fast) =>
            list.Add(new XPlaneDataref(list.Count, path, KeyPrefix + key, title, unit, min, max, category, rate));

        // Array elements: keys and titles count from 1 (engine 1 = path[0]).
        void Arr(string path, int count, string key, string title, string unit, double min, double max, string category, XPlaneRate rate = XPlaneRate.Fast)
        {
            for (var i = 0; i < count; i++)
            {
                var n = (i + 1).ToString(CultureInfo.InvariantCulture);
                Add(path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", key + "_" + n, title + " " + n, unit, min, max, category, rate);
            }
        }

        const XPlaneRate S = XPlaneRate.Slow;

        // Time
        Add("sim/time/total_running_time_sec", "time", "Sim running time", "s", 0, 86400, "Time");
        Add("sim/time/total_flight_time_sec", "flight_time", "Time since the flight was reset", "s", 0, 86400, "Time");
        Add("sim/time/zulu_time_sec", "zulu_time", "Zulu time (seconds since midnight)", "s", 0, 86400, "Time");
        Add("sim/time/local_time_sec", "local_time", "Local time (seconds since midnight)", "s", 0, 86400, "Time");
        Add("sim/time/zulu_date_days", "zulu_date", "Zulu date (days since 1 January)", "days", 0, 365, "Time", S);
        Add("sim/time/paused", "paused", "Paused", "0/1", 0, 1, "Time");
        Add("sim/time/sim_speed_actual", "sim_speed", "Actual time speed", "x", 0, 16, "Time");
        Add("sim/time/is_in_replay", "replay", "Replay", "0/1", 0, 1, "Time");
        Add("sim/time/framerate_period", "frame_period", "Frame time (1/FPS)", "s", 0, 0.2, "Time");

        // Position
        Add("sim/flightmodel/position/latitude", "lat", "Latitude", "deg", -90, 90, "Position");
        Add("sim/flightmodel/position/longitude", "lon", "Longitude", "deg", -180, 180, "Position");
        Add("sim/flightmodel/position/elevation", "elevation", "Altitude above sea level", "m", 0, 20000, "Position");
        Add("sim/flightmodel/position/y_agl", "agl", "Height above ground", "m", 0, 20000, "Position");
        Add("sim/cockpit2/gauges/indicators/altitude_ft_pilot", "alt_ind_ft", "Indicated altitude", "ft", 0, 60000, "Position");
        Add("sim/cockpit2/gauges/indicators/radio_altimeter_height_ft_pilot", "radio_alt_ft", "Radio altitude", "ft", 0, 2500, "Position");
        Add("sim/flightmodel/position/local_x", "local_x", "Local x (east, OpenGL)", "m", -100000, 100000, "Position");
        Add("sim/flightmodel/position/local_y", "local_y", "Local y (up, OpenGL)", "m", -100000, 100000, "Position");
        Add("sim/flightmodel/position/local_z", "local_z", "Local z (south, OpenGL)", "m", -100000, 100000, "Position");

        // Attitude
        Add("sim/flightmodel/position/theta", "theta", "Pitch (OpenGL)", "deg", -90, 90, "Attitude");
        Add("sim/flightmodel/position/phi", "phi", "Roll (OpenGL)", "deg", -180, 180, "Attitude");
        Add("sim/flightmodel/position/psi", "psi", "Heading (OpenGL, true)", "deg", 0, 360, "Attitude");
        Add("sim/flightmodel/position/true_theta", "true_theta", "Pitch", "deg", -90, 90, "Attitude");
        Add("sim/flightmodel/position/true_phi", "true_phi", "Roll", "deg", -180, 180, "Attitude");
        Add("sim/flightmodel/position/true_psi", "true_psi", "True heading", "deg", 0, 360, "Attitude");
        Add("sim/flightmodel/position/mag_psi", "mag_psi", "Magnetic heading", "deg", 0, 360, "Attitude");
        Add("sim/flightmodel/position/hpath", "hpath", "Flight path heading (track)", "deg", 0, 360, "Attitude");
        Add("sim/flightmodel/position/vpath", "vpath", "Flight path angle", "deg", -90, 90, "Attitude");
        Add("sim/flightmodel/position/P", "p", "Roll rate", "deg/s", -300, 300, "Attitude");
        Add("sim/flightmodel/position/Q", "q", "Pitch rate", "deg/s", -100, 100, "Attitude");
        Add("sim/flightmodel/position/R", "r", "Yaw rate", "deg/s", -100, 100, "Attitude");

        // Flight
        Add("sim/flightmodel/position/indicated_airspeed", "ias", "Indicated airspeed", "kt", 0, 700, "Flight");
        Add("sim/flightmodel/position/true_airspeed", "tas", "True airspeed", "m/s", 0, 700, "Flight");
        Add("sim/flightmodel/position/equivalent_airspeed", "eas", "Equivalent airspeed", "kt", 0, 700, "Flight");
        Add("sim/flightmodel/position/groundspeed", "gs", "Ground speed", "m/s", 0, 700, "Flight");
        Add("sim/flightmodel/misc/machno", "mach", "Mach number", "", 0, 3, "Flight");
        Add("sim/flightmodel/position/alpha", "alpha", "Angle of attack", "deg", -20, 40, "Flight");
        Add("sim/flightmodel/position/beta", "beta", "Sideslip angle", "deg", -30, 30, "Flight");
        Add("sim/flightmodel2/misc/AoA_angle_degrees", "aoa", "Angle of attack (flight model)", "deg", -20, 40, "Flight");
        Add("sim/flightmodel/position/vh_ind", "vs", "Vertical speed", "m/s", -150, 150, "Flight");
        Add("sim/cockpit2/gauges/indicators/vvi_fpm_pilot", "vvi_fpm", "Vertical speed indicator", "ft/min", -6000, 6000, "Flight");
        Add("sim/flightmodel/forces/g_nrml", "g_nrml", "G load (normal)", "G", -3, 9, "Flight");
        Add("sim/flightmodel/forces/g_axil", "g_axil", "G load (longitudinal)", "G", -3, 3, "Flight");
        Add("sim/flightmodel/forces/g_side", "g_side", "G load (lateral)", "G", -3, 3, "Flight");
        Add("sim/flightmodel/position/local_vx", "vx", "Velocity x (east, OpenGL)", "m/s", -700, 700, "Flight");
        Add("sim/flightmodel/position/local_vy", "vy", "Velocity y (up, OpenGL)", "m/s", -300, 300, "Flight");
        Add("sim/flightmodel/position/local_vz", "vz", "Velocity z (south, OpenGL)", "m/s", -700, 700, "Flight");
        Add("sim/flightmodel/position/local_ax", "ax", "Acceleration x (OpenGL)", "m/s²", -100, 100, "Flight");
        Add("sim/flightmodel/position/local_ay", "ay", "Acceleration y (OpenGL)", "m/s²", -100, 100, "Flight");
        Add("sim/flightmodel/position/local_az", "az", "Acceleration z (OpenGL)", "m/s²", -100, 100, "Flight");
        Add("sim/cockpit2/gauges/indicators/slip_deg", "slip", "Slip ball", "deg", -20, 20, "Flight");
        Add("sim/cockpit2/gauges/indicators/turn_rate_heading_deg_pilot", "turn_rate", "Turn rate", "deg/s", -30, 30, "Flight");
        Add("sim/cockpit2/annunciators/stall_warning", "stall_warning", "Stall warning", "0/1", 0, 1, "Flight");
        Add("sim/cockpit2/annunciators/stall_warning_ratio", "stall_ratio", "Stall warning ratio", "0..1", 0, 1, "Flight");
        Add("sim/flightmodel/failures/onground_any", "on_ground", "On the ground", "0/1", 0, 1, "Flight");

        // Controls (pilot input and the surfaces)
        Add("sim/cockpit2/controls/yoke_pitch_ratio", "yoke_pitch", "Pitch input", "-1..1", -1, 1, "Controls");
        Add("sim/cockpit2/controls/yoke_roll_ratio", "yoke_roll", "Roll input", "-1..1", -1, 1, "Controls");
        Add("sim/cockpit2/controls/yoke_heading_ratio", "yoke_yaw", "Rudder pedal input", "-1..1", -1, 1, "Controls");
        Add("sim/cockpit2/controls/total_pitch_ratio", "total_pitch", "Pitch command (pilot + autopilot)", "-1..1", -1, 1, "Controls");
        Add("sim/cockpit2/controls/total_roll_ratio", "total_roll", "Roll command (pilot + autopilot)", "-1..1", -1, 1, "Controls");
        Add("sim/cockpit2/controls/total_heading_ratio", "total_yaw", "Yaw command (pilot + autopilot)", "-1..1", -1, 1, "Controls");
        Add("sim/cockpit2/controls/elevator_trim", "elevator_trim", "Elevator trim", "-1..1", -1, 1, "Controls");
        Add("sim/cockpit2/controls/aileron_trim", "aileron_trim", "Aileron trim", "-1..1", -1, 1, "Controls");
        Add("sim/cockpit2/controls/rudder_trim", "rudder_trim", "Rudder trim", "-1..1", -1, 1, "Controls");
        Add("sim/cockpit2/controls/flap_handle_request_ratio", "flap_handle", "Flap handle", "0..1", 0, 1, "Controls");
        Add("sim/cockpit2/controls/speedbrake_ratio", "speedbrake_handle", "Speed brake handle", "-0.5..1", -0.5, 1, "Controls");
        Add("sim/cockpit2/controls/gear_handle_down", "gear_handle", "Gear handle down", "0/1", 0, 1, "Controls");
        Add("sim/cockpit2/controls/parking_brake_ratio", "parking_brake", "Parking brake", "0..1", 0, 1, "Controls");
        Add("sim/cockpit2/controls/left_brake_ratio", "brake_left", "Left toe brake", "0..1", 0, 1, "Controls");
        Add("sim/cockpit2/controls/right_brake_ratio", "brake_right", "Right toe brake", "0..1", 0, 1, "Controls");
        Add("sim/cockpit2/engine/actuators/throttle_ratio_all", "throttle_all", "Throttle (all engines)", "0..1", 0, 1, "Controls");
        Add("sim/flightmodel/misc/act_frc_ptch_lb", "yoke_force_pitch", "Pitch control force", "lb", -100, 100, "Controls");
        Add("sim/flightmodel/misc/act_frc_roll_lb", "yoke_force_roll", "Roll control force", "lb", -100, 100, "Controls");
        Add("sim/flightmodel/misc/act_frc_hdng_lb", "pedal_force", "Pedal force", "lb", -200, 200, "Controls");

        Add("sim/flightmodel2/controls/pitch_ratio", "surface_pitch", "Pitch surfaces (after augmentation)", "-1..1", -1, 1, "Surfaces");
        Add("sim/flightmodel2/controls/roll_ratio", "surface_roll", "Roll surfaces (after augmentation)", "-1..1", -1, 1, "Surfaces");
        Add("sim/flightmodel2/controls/heading_ratio", "surface_yaw", "Yaw surfaces (after augmentation)", "-1..1", -1, 1, "Surfaces");
        Add("sim/flightmodel/controls/hstab1_elv1def", "elevator_deg", "Elevator deflection", "deg", -30, 30, "Surfaces");
        Add("sim/flightmodel/controls/lail1def", "aileron_left_deg", "Left aileron deflection", "deg", -30, 30, "Surfaces");
        Add("sim/flightmodel/controls/rail1def", "aileron_right_deg", "Right aileron deflection", "deg", -30, 30, "Surfaces");
        Add("sim/flightmodel/controls/vstab1_rud1def", "rudder_deg", "Rudder deflection", "deg", -30, 30, "Surfaces");
        Add("sim/flightmodel/controls/lsplrdef", "spoiler_left_deg", "Left spoiler deflection", "deg", 0, 60, "Surfaces");
        Add("sim/flightmodel/controls/rsplrdef", "spoiler_right_deg", "Right spoiler deflection", "deg", 0, 60, "Surfaces");
        Add("sim/cockpit2/controls/flap_system_deploy_ratio", "flaps", "Flaps", "0..1", 0, 1, "Surfaces");
        Add("sim/flightmodel2/controls/flap1_deploy_ratio", "flap1", "Flap set 1", "0..1", 0, 1, "Surfaces");
        Add("sim/flightmodel2/controls/flap2_deploy_ratio", "flap2", "Flap set 2", "0..1", 0, 1, "Surfaces");
        Add("sim/flightmodel2/controls/slat1_deploy_ratio", "slats", "Slats", "0..1", 0, 1, "Surfaces");
        Add("sim/flightmodel2/controls/speedbrake_ratio", "speedbrake", "Speed brakes", "0..1.5", 0, 1.5, "Surfaces");
        Add("sim/flightmodel2/controls/wingsweep_ratio", "wing_sweep", "Wing sweep", "0..1", 0, 1, "Surfaces");
        Add("sim/flightmodel2/misc/tailhook_deploy_ratio", "tailhook", "Tail hook", "0..1", 0, 1, "Surfaces");
        Add("sim/flightmodel2/misc/canopy_open_ratio", "canopy", "Canopy open", "0..1", 0, 1, "Surfaces");

        // Gear (wheels 1..6 of X-Plane's 10)
        Arr("sim/flightmodel2/gear/deploy_ratio", 6, "gear_deploy", "Gear deployed", "0..1", 0, 1, "Gear");
        Arr("sim/flightmodel2/gear/on_ground", 6, "gear_on_ground", "Wheel on the ground", "0/1", 0, 1, "Gear");
        Arr("sim/flightmodel2/gear/tire_vertical_deflection_mtr", 6, "gear_deflection", "Tyre vertical deflection", "m", 0, 0.5, "Gear");

        // Engines 1..4
        const int engines = 4;
        Arr("sim/cockpit2/engine/actuators/throttle_ratio", engines, "throttle", "Throttle", "0..1", 0, 1, "Engines");
        Arr("sim/cockpit2/engine/actuators/prop_ratio", engines, "prop", "Prop lever", "0..1", 0, 1, "Engines");
        Arr("sim/cockpit2/engine/actuators/mixture_ratio", engines, "mixture", "Mixture lever", "0..1", 0, 1, "Engines");
        Arr("sim/flightmodel/engine/ENGN_running", engines, "running", "Engine running", "0/1", 0, 1, "Engines");
        Arr("sim/cockpit2/engine/indicators/N1_percent", engines, "n1", "N1", "%", 0, 110, "Engines");
        Arr("sim/cockpit2/engine/indicators/N2_percent", engines, "n2", "N2", "%", 0, 110, "Engines");
        Arr("sim/cockpit2/engine/indicators/engine_speed_rpm", engines, "rpm", "Engine RPM", "rpm", 0, 3000, "Engines");
        Arr("sim/cockpit2/engine/indicators/prop_speed_rpm", engines, "prop_rpm", "Prop RPM", "rpm", 0, 3000, "Engines");
        Arr("sim/cockpit2/engine/indicators/MPR_in_hg", engines, "manifold", "Manifold pressure", "inHg", 0, 60, "Engines");
        Arr("sim/cockpit2/engine/indicators/torque_n_mtr", engines, "torque", "Torque", "N·m", 0, 20000, "Engines");
        Arr("sim/cockpit2/engine/indicators/thrust_n", engines, "thrust", "Thrust", "N", 0, 200000, "Engines");
        Arr("sim/cockpit2/engine/indicators/fuel_flow_kg_sec", engines, "fuel_flow", "Fuel flow", "kg/s", 0, 5, "Engines");
        Arr("sim/cockpit2/engine/indicators/EGT_deg_cel", engines, "egt", "EGT", "°C", 0, 1000, "Engines");
        Arr("sim/cockpit2/engine/indicators/ITT_deg_cel", engines, "itt", "ITT", "°C", 0, 1000, "Engines");
        Arr("sim/cockpit2/engine/indicators/CHT_deg_cel", engines, "cht", "CHT", "°C", 0, 300, "Engines");
        Arr("sim/cockpit2/engine/indicators/oil_temperature_deg_C", engines, "oil_temp", "Oil temperature (aircraft units)", "deg", 0, 150, "Engines");
        Arr("sim/cockpit2/engine/indicators/oil_pressure_psi", engines, "oil_press", "Oil pressure", "psi", 0, 150, "Engines");
        Arr("sim/flightmodel2/engines/afterburner_ratio", engines, "afterburner", "Afterburner", "0..1", 0, 1, "Engines");
        Arr("sim/flightmodel2/engines/thrust_reverser_deploy_ratio", engines, "reverser", "Thrust reverser", "0..1", 0, 1, "Engines");
        Add("sim/aircraft/engine/acf_num_engines", "engines", "Number of engines", "", 0, 16, "Engines", S);

        // Fuel and weight
        Add("sim/flightmodel/weight/m_fuel_total", "fuel_total", "Fuel (total)", "kg", 0, 20000, "Fuel");
        Add("sim/flightmodel/weight/m_total", "weight_total", "Total weight", "kg", 0, 400000, "Fuel");
        Add("sim/flightmodel/weight/m_fixed", "payload", "Payload", "kg", 0, 100000, "Fuel", S);
        Arr("sim/flightmodel/weight/m_fuel", 9, "fuel_tank", "Fuel tank", "kg", 0, 10000, "Fuel", S);

        // Autopilot
        Add("sim/cockpit2/autopilot/autopilot_on", "ap_on", "Autopilot on", "0/1", 0, 1, "Autopilot");
        Add("sim/cockpit2/autopilot/servos_on", "ap_servos", "Autopilot servos on", "0/1", 0, 1, "Autopilot");
        Add("sim/cockpit2/autopilot/flight_director_mode", "fd_mode", "Flight director mode (0 off, 1 FD, 2 AP)", "", 0, 2, "Autopilot", S);
        Add("sim/cockpit2/autopilot/autothrottle_on", "at_on", "Autothrottle on", "0/1", 0, 1, "Autopilot", S);
        Add("sim/cockpit2/autopilot/heading_mode", "ap_heading_mode", "Autopilot heading mode", "", 0, 20, "Autopilot", S);
        Add("sim/cockpit2/autopilot/altitude_hold_status", "ap_alt_status", "Altitude hold (0 off, 1 armed, 2 captured)", "", 0, 2, "Autopilot", S);
        Add("sim/cockpit2/autopilot/vvi_status", "ap_vs_status", "Vertical speed mode (0 off, 1 armed, 2 captured)", "", 0, 2, "Autopilot", S);
        Add("sim/cockpit2/autopilot/speed_status", "ap_speed_status", "Speed mode (0 off, 1 armed, 2 captured)", "", 0, 2, "Autopilot", S);
        Add("sim/cockpit2/autopilot/nav_status", "ap_nav_status", "NAV mode (0 off, 1 armed, 2 captured)", "", 0, 2, "Autopilot", S);
        Add("sim/cockpit2/autopilot/approach_status", "ap_appr_status", "Approach mode (0 off, 1 armed, 2 captured)", "", 0, 2, "Autopilot", S);
        Add("sim/cockpit2/autopilot/glideslope_status", "ap_gs_status", "Glide slope (0 off, 1 armed, 2 captured)", "", 0, 2, "Autopilot", S);
        Add("sim/cockpit2/autopilot/altitude_dial_ft", "ap_alt_ft", "Selected altitude", "ft", 0, 50000, "Autopilot", S);
        Add("sim/cockpit2/autopilot/heading_dial_deg_mag_pilot", "ap_heading", "Selected heading (magnetic)", "deg", 0, 360, "Autopilot", S);
        Add("sim/cockpit2/autopilot/airspeed_dial_kts_mach", "ap_speed", "Selected speed (kt or Mach)", "", 0, 500, "Autopilot", S);
        Add("sim/cockpit2/autopilot/vvi_dial_fpm", "ap_vs_fpm", "Selected vertical speed", "ft/min", -6000, 6000, "Autopilot", S);

        // Weapons (stations 1..8 of X-Plane's 25)
        Add("sim/cockpit2/weapons/master_arm", "master_arm", "Master arm", "0/1", 0, 1, "Weapons", S);
        Add("sim/cockpit/weapons/guns_armed", "guns_armed", "Guns armed", "0/1", 0, 1, "Weapons", S);
        Add("sim/cockpit/weapons/rockets_armed", "rockets_armed", "Rockets armed", "0/1", 0, 1, "Weapons", S);
        Add("sim/cockpit/weapons/missiles_armed", "missiles_armed", "Missiles armed", "0/1", 0, 1, "Weapons", S);
        Add("sim/cockpit/weapons/bombs_armed", "bombs_armed", "Bombs armed", "0/1", 0, 1, "Weapons", S);
        Add("sim/cockpit/weapons/chaff_now", "chaff", "Chaff left", "", 0, 200, "Weapons", S);
        Add("sim/cockpit/weapons/flare_now", "flares", "Flares left", "", 0, 200, "Weapons", S);
        Add("sim/cockpit/weapons/incoming_missile_lock", "missile_lock", "Missile locked on us", "0/1", 0, 1, "Weapons");
        Arr("sim/weapons/firing", 8, "firing", "Station firing", "0/1", 0, 1, "Weapons");
        Arr("sim/weapons/bul_rounds", 8, "rounds", "Rounds left at station", "", 0, 2000, "Weapons");

        // Warnings
        Add("sim/cockpit2/annunciators/master_warning", "master_warning", "Master warning", "0/1", 0, 1, "Warnings");
        Add("sim/cockpit2/annunciators/master_caution", "master_caution", "Master caution", "0/1", 0, 1, "Warnings");
        Add("sim/cockpit2/annunciators/gear_unsafe", "gear_unsafe", "Gear unsafe", "0/1", 0, 1, "Warnings");
        Add("sim/cockpit2/annunciators/gear_warning", "gear_warning", "Gear warning", "0/1", 0, 1, "Warnings");
        Add("sim/cockpit2/annunciators/GPWS", "gpws", "Ground proximity warning", "0/1", 0, 1, "Warnings");
        Add("sim/flightmodel2/misc/has_crashed", "crashed", "Crashed", "0/1", 0, 1, "Warnings");
        Arr("sim/cockpit2/annunciators/engine_fires", engines, "engine_fire", "Engine fire", "0/1", 0, 1, "Warnings");

        // Lights
        Add("sim/cockpit2/switches/landing_lights_on", "light_landing", "Landing lights", "0/1", 0, 1, "Lights", S);
        Add("sim/cockpit2/switches/taxi_light_on", "light_taxi", "Taxi light", "0/1", 0, 1, "Lights", S);
        Add("sim/cockpit2/switches/navigation_lights_on", "light_nav", "Navigation lights", "0/1", 0, 1, "Lights", S);
        Add("sim/cockpit2/switches/beacon_on", "light_beacon", "Beacon", "0/1", 0, 1, "Lights", S);
        Add("sim/cockpit2/switches/strobe_lights_on", "light_strobe", "Strobes", "0/1", 0, 1, "Lights", S);

        // Environment
        Add("sim/weather/wind_direction_degt", "wind_dir", "Wind from (true)", "deg", 0, 360, "Environment", S);
        Add("sim/weather/wind_speed_kt", "wind_speed", "Wind speed (the dataref is in m/s despite its name)", "m/s", 0, 60, "Environment", S);
        Add("sim/cockpit2/temperature/outside_air_temp_degc", "oat", "Outside air temperature", "°C", -60, 50, "Environment", S);
        Add("sim/weather/barometer_current_inhg", "pressure_inhg", "Static pressure at the aircraft", "inHg", 5, 32, "Environment", S);
        Add("sim/weather/barometer_sealevel_inhg", "qnh_inhg", "Sea level pressure", "inHg", 28, 31, "Environment", S);
        Add("sim/cockpit2/gauges/actuators/barometer_setting_in_hg_pilot", "baro_setting", "Altimeter setting", "inHg", 28, 31, "Environment", S);

        return list;
    }
}

/// <summary>Live X-Plane 11 / 12 telemetry from RREF answers (fed by <see cref="XPlaneTelemetryFeed"/>).</summary>
/// <remarks>
/// <para>Answers are merged into one value set; a frame is published when a set is complete: a datagram with fewer than
/// 183 pairs ends a set, and so does one after which every flight value of the previous set was updated again. Datagrams that
/// only carry slow values (text bytes, traffic, version) publish nothing. At most one frame per half a request period.
/// The frame time is X-Plane's sim time (sim/time/total_running_time_sec) once known.</para>
/// <para>Session facts: aircraft (sim/aircraft/view/acf_ui_name, else acf_descrip, else acf_ICAO), "aircraft_icao",
/// "tail_number", game_version (sim/version/xplane_internal_version). A text is taken once two answers in a row agree.
/// Events: SessionStart with the first frame; SessionEnd "flight reset" + SessionStart when
/// sim/time/total_flight_time_sec jumps back to near 0 (a new flight was loaded); "xp.crash" when the aircraft crashes;
/// Objects (TCAS targets 1..19, two per second) while other aircraft exist. Pause is not a session end.</para>
/// </remarks>
public sealed class XPlaneTelemetrySource : FrameTelemetrySource
{
    public const string CrashEvent = "xp.crash";
    public const string IcaoInfoKey = "aircraft_icao";
    public const string TailNumberInfoKey = "tail_number";

    private static readonly XPlaneDataref?[] ByIndex = BuildIndex();

    private readonly object _gate = new();
    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);
    private readonly bool[] _fastSeen = new bool[ByIndex.Length];
    private readonly bool[] _fastUpdated = new bool[ByIndex.Length];
    private readonly byte[] _textBytes = new byte[1000];
    private readonly float[] _traffic = new float[1 + XPlaneProtocol.TrafficTargets * (XPlaneProtocol.TrafficFields.Length + 1)];
    private readonly Dictionary<string, string> _textCandidates = new(StringComparer.Ordinal);
    private int _fastSeenCount;
    private int _lastSetSize;
    private int _fastUpdatedCount;
    private long _lastPublishTicks;
    private bool _started;
    private double? _flightTime;
    private bool _crashed;
    private bool _trafficUpdated;
    private int _lastTrafficCount;

    public XPlaneTelemetrySource(IFfbClock clock, string name = "X-Plane", double rateHz = 20)
        : base(name, XPlaneProtocol.Descriptors, clock)
    {
        MinFrameIntervalSeconds = 0.5 / Math.Clamp(rateHz, 1, 99);
    }

    /// <summary>Frames closer together than this are merged (tests: 0).</summary>
    public double MinFrameIntervalSeconds { get; set; }

    public long DatagramsAccepted { get; private set; }
    public long DatagramsRejected { get; private set; }
    public long UnknownIndices { get; private set; }

    /// <summary>The current aircraft name (null before X-Plane sent it).</summary>
    public string? Aircraft => SessionInfo.TryGetValue(TelemetryInfoKeys.Aircraft, out var a) ? a : null;

    public override IReadOnlyList<TelemetryNavMapping> NavMappings { get; } =
    [
        new(TelemetryNav.Latitude, "xp.lat"),
        new(TelemetryNav.Longitude, "xp.lon"),
        new(TelemetryNav.Altitude, "xp.elevation"),
        new(TelemetryNav.Roll, "xp.true_phi"),
        new(TelemetryNav.Pitch, "xp.true_theta"),
        new(TelemetryNav.Yaw, "xp.true_psi"),
        new(TelemetryNav.Heading, "xp.hpath"),
        new(TelemetryNav.Agl, "xp.agl"),
        new(TelemetryNav.Ias, "xp.ias", TelemetryNav.KnotsToMetresPerSecond),
        new(TelemetryNav.Tas, "xp.tas"),
        new(TelemetryNav.Mach, "xp.mach"),
        new(TelemetryNav.Aoa, "xp.alpha"),
        new(TelemetryNav.Aos, "xp.beta"),
        new(TelemetryNav.VerticalG, "xp.g_nrml"),
        new(TelemetryNav.LongitudinalG, "xp.g_axil"),
        new(TelemetryNav.LateralG, "xp.g_side"),
        new(TelemetryNav.Throttle, "xp.throttle_all"),
        new(TelemetryNav.Gear, "xp.gear_deploy_1"),
        new(TelemetryNav.Flaps, "xp.flaps"),
        new(TelemetryNav.Airbrakes, "xp.speedbrake"),
        new(TelemetryNav.FuelKg, "xp.fuel_total"),
        new(TelemetryNav.VerticalSpeed, "xp.vs"),
    ];

    /// <summary>Handles one datagram from X-Plane.</summary>
    public void Ingest(ReadOnlySpan<byte> datagram)
    {
        PublishRaw("udp", datagram);
        lock (_gate)
        {
            var anyFast = false;
            var anyText = false;
            var pairs = XPlaneProtocol.ParseAnswer(datagram, (index, value) =>
            {
                if (!float.IsFinite(value))
                    return;
                if (index >= 0 && index < ByIndex.Length && ByIndex[index] is { } dataref)
                {
                    _values[dataref.Key] = value;
                    if (dataref.Rate == XPlaneRate.Fast)
                    {
                        anyFast = true;
                        if (!_fastSeen[index])
                        {
                            _fastSeen[index] = true;
                            _fastSeenCount++;
                        }

                        if (!_fastUpdated[index])
                        {
                            _fastUpdated[index] = true;
                            _fastUpdatedCount++;
                        }
                    }
                }
                else if (index >= XPlaneProtocol.TextIndexBase && index < XPlaneProtocol.TextIndexBase + _textBytes.Length)
                {
                    _textBytes[index - XPlaneProtocol.TextIndexBase] = (byte)Math.Clamp((int)value, 0, 255);
                    anyText = true;
                }
                else if (index >= XPlaneProtocol.TrafficIndexBase && index < XPlaneProtocol.TrafficIndexBase + _traffic.Length)
                {
                    _traffic[index - XPlaneProtocol.TrafficIndexBase] = value;
                    _trafficUpdated = true;
                    if (index == XPlaneProtocol.TrafficIndexBase)
                        _values[XPlaneProtocol.KeyPrefix + "traffic_count"] = value;
                }
                else if (index == XPlaneProtocol.VersionIndex)
                {
                    if (value > 0)
                        SetInfo(TelemetryInfoKeys.GameVersion, "X-Plane build " + ((long)value).ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    UnknownIndices++;
                }
            });

            if (pairs < 0)
            {
                DatagramsRejected++;
                return;
            }

            DatagramsAccepted++;
            if (anyText)
                UpdateTexts();

            var setComplete = pairs < XPlaneProtocol.MaxPairsPerDatagram || (_lastSetSize > 0 && _fastUpdatedCount >= _lastSetSize);
            if (!setComplete)
                return;

            if (_trafficUpdated)
            {
                _trafficUpdated = false;
                RaiseTraffic();
            }

            if (!anyFast && _fastUpdatedCount == 0)
                return;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastPublishTicks != 0 && MinFrameIntervalSeconds > 0 &&
                (now - _lastPublishTicks) / (double)System.Diagnostics.Stopwatch.Frequency < MinFrameIntervalSeconds)
                return;
            _lastPublishTicks = now;
            PublishSet();
        }
    }

    private void PublishSet()
    {
        _lastSetSize = _fastSeenCount;
        Array.Clear(_fastUpdated);
        _fastUpdatedCount = 0;

        if (_values.TryGetValue("xp.flight_time", out var flightTime))
        {
            var replay = _values.TryGetValue("xp.replay", out var r) && r > 0.5;
            if (_started && !replay && _flightTime is { } previous && previous > 5 && flightTime < previous - 2 && flightTime < 10)
            {
                RaiseEvent(TelemetryEventKinds.SessionEnd, "flight reset");
                _crashed = false;
                Clear();
                RaiseEvent(TelemetryEventKinds.SessionStart, "new flight");
            }

            if (!replay)
                _flightTime = flightTime;
        }

        if (!_started)
        {
            _started = true;
            RaiseEvent(TelemetryEventKinds.SessionStart, "X-Plane answers");
        }

        if (_values.TryGetValue("xp.crashed", out var crashed))
        {
            var now = crashed > 0.5;
            if (now && !_crashed)
                RaiseEvent(CrashEvent, Aircraft ?? "");
            _crashed = now;
        }

        var time = _values.TryGetValue("xp.time", out var t) ? t : Clock.Now;
        Publish(new TelemetryFrame(time, _values));
    }

    private void UpdateTexts()
    {
        string Text(string name)
        {
            var t = XPlaneProtocol.Texts.First(x => x.Name == name);
            var span = _textBytes.AsSpan(t.Offset, t.Length);
            var end = span.IndexOf((byte)0);
            if (end >= 0)
                span = span[..end];
            var sb = new StringBuilder(span.Length);
            foreach (var b in span)
                sb.Append(b is >= 32 and < 127 ? (char)b : b >= 160 ? (char)b : ' ');
            return sb.ToString().Trim();
        }

        var name = Text("name");
        if (name.Length == 0)
            name = Text("descrip");
        var icao = Text("icao");
        if (name.Length == 0)
            name = icao;
        Stable(TelemetryInfoKeys.Aircraft, name);
        Stable(IcaoInfoKey, icao);
        Stable(TailNumberInfoKey, Text("tailnum"));
    }

    // A text is assembled from single bytes that may arrive in different datagrams: take it once two reads agree.
    private void Stable(string key, string value)
    {
        if (value.Length == 0)
            return;
        if (_textCandidates.TryGetValue(key, out var candidate) && candidate == value)
            SetInfo(key, value);
        _textCandidates[key] = value;
    }

    private void RaiseTraffic()
    {
        var count = (int)Math.Round(_traffic[0]);
        var lines = new List<string>();
        for (var target = 1; target <= XPlaneProtocol.TrafficTargets; target++)
        {
            // tcas_num_acf counts the user's aircraft as slot 0 (X-Plane's TCAS arrays): slots 1..count-1 are others.
            if (count > 0 && target >= count)
                break;
            var b = XPlaneProtocol.TrafficIndex(target, 0) - XPlaneProtocol.TrafficIndexBase;
            double lat = _traffic[b], lon = _traffic[b + 1], ele = _traffic[b + 2];
            if (lat == 0 && lon == 0)
                continue;
            var modeS = (long)_traffic[b + 6];
            var id = modeS > 0 ? modeS.ToString("X6", CultureInfo.InvariantCulture) : "tcas" + target.ToString(CultureInfo.InvariantCulture);
            lines.Add(TelemetryEventKinds.ObjectLine(id, "Air+FixedWing", null, null, lat, lon, ele,
                roll: _traffic[b + 5], pitch: _traffic[b + 4], yaw: _traffic[b + 3]));
        }

        if (lines.Count == 0 && _lastTrafficCount == 0)
            return;  // nobody around, and nobody was: no empty snapshots (an empty one after traffic = all gone)
        _lastTrafficCount = lines.Count;
        RaiseEvent(TelemetryEventKinds.Objects, string.Join('\n', lines));
    }

    private static XPlaneDataref?[] BuildIndex()
    {
        var max = XPlaneProtocol.Datarefs.Max(d => d.Index);
        var map = new XPlaneDataref?[max + 1];
        foreach (var d in XPlaneProtocol.Datarefs)
            map[d.Index] = d;
        return map;
    }
}
