using System.Buffers.Binary;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace JoyPro.Ffb.Core.Telemetry.Bms;

public enum BmsArea
{
    FlightData,
    FlightData2
}

public enum BmsType
{
    F32,
    I32,
    U32,
    I16,
    U16,
    I8,
    U8
}

/// <summary>One numeric value of a BMS shared memory area.</summary>
/// <param name="Field">The member name in Tools\SharedMem\FlightData.h.</param>
/// <param name="Index">Array element (0 for plain members).</param>
/// <param name="MinVersion">FlightData2: the FLIGHTDATA2_VERSION that added the member (older BMS: the key is absent).</param>
public sealed record BmsValue(string Key, string Title, string Unit, double Min, double Max, string Category, BmsArea Area, string Field,
    int Index, BmsType Type, int MinVersion)
{
    public int Offset => BmsSharedMemory.OffsetOf(Area, Field) + Index * BmsSharedMemory.SizeOf(Type);
}

/// <summary>One bit of a bit field, decoded into its own 0/1 key.</summary>
public sealed record BmsFlag(string Key, string Title, string WordKey, uint Mask, string Category);

/// <summary>Falcon BMS shared memory: names, the byte layout of FlightData / FlightData2 (FLIGHTDATA_VERSION 118,
/// FLIGHTDATA2_VERSION 23, BMS 4.38) and what JoyPro reads of it.</summary>
/// <remarks>
/// The layout is the MSVC layout of the official header <c>Tools\SharedMem\FlightData.h</c> (natural alignment, no
/// vtable). A test re-derives every offset from the installed header, so a BMS update that moves a member is noticed.
/// Keys keep the game's own units (feet, knots, radians, lbs); <see cref="BmsTelemetrySource.NavMappings"/> converts.
/// </remarks>
public static class BmsSharedMemory
{
    public const string FlightDataName = "FalconSharedMemoryArea";
    public const string FlightData2Name = "FalconSharedMemoryArea2";
    public const string StringDataName = "FalconSharedMemoryAreaString";

    public const int FlightDataSize = 1920;
    public const int FlightData2Size = 1284;
    public const int FlightDataVersion = 118;
    public const int FlightData2Version = 23;
    public const int StringAreaSizeMax = 1024 * 1024;

    public const int MaxRwrObjects = 40;
    public const int DedLineLength = 26;
    public const int CallsignLength = 12;
    public const int MaxCallsigns = 32;

    public const uint HsiFlying = 0x80000000;
    public const uint MiscRaltValid = 0x01;

    /// <summary>Byte offset of every member of FlightData (BMS 4.38).</summary>
    public static readonly IReadOnlyDictionary<string, int> FlightDataOffsets = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["x"] = 0, ["y"] = 4, ["z"] = 8, ["xDot"] = 12, ["yDot"] = 16, ["zDot"] = 20, ["alpha"] = 24, ["beta"] = 28, ["gamma"] = 32,
        ["pitch"] = 36, ["roll"] = 40, ["yaw"] = 44, ["mach"] = 48, ["kias"] = 52, ["vt"] = 56, ["gs"] = 60, ["windOffset"] = 64,
        ["nozzlePos"] = 68, ["internalFuel"] = 72, ["externalFuel"] = 76, ["fuelFlow"] = 80, ["rpm"] = 84, ["ftit"] = 88,
        ["gearPos"] = 92, ["speedBrake"] = 96, ["epuFuel"] = 100, ["oilPressure"] = 104, ["lightBits"] = 108,
        ["headPitch"] = 112, ["headRoll"] = 116, ["headYaw"] = 120, ["lightBits2"] = 124, ["lightBits3"] = 128,
        ["ChaffCount"] = 132, ["FlareCount"] = 136, ["NoseGearPos"] = 140, ["LeftGearPos"] = 144, ["RightGearPos"] = 148,
        ["AdiIlsHorPos"] = 152, ["AdiIlsVerPos"] = 156, ["courseState"] = 160, ["headingState"] = 164, ["totalStates"] = 168,
        ["courseDeviation"] = 172, ["desiredCourse"] = 176, ["distanceToBeacon"] = 180, ["bearingToBeacon"] = 184,
        ["currentHeading"] = 188, ["desiredHeading"] = 192, ["deviationLimit"] = 196, ["halfDeviationLimit"] = 200,
        ["localizerCourse"] = 204, ["airbaseX"] = 208, ["airbaseY"] = 212, ["totalValues"] = 216,
        ["TrimPitch"] = 220, ["TrimRoll"] = 224, ["TrimYaw"] = 228, ["hsiBits"] = 232,
        ["DEDLines"] = 236, ["Invert"] = 366, ["PFLLines"] = 496, ["PFLInvert"] = 626,
        ["UFCTChan"] = 756, ["AUXTChan"] = 760, ["RwrObjectCount"] = 764, ["RWRsymbol"] = 768, ["bearing"] = 928,
        ["missileActivity"] = 1088, ["missileLaunch"] = 1248, ["selected"] = 1408, ["lethality"] = 1568, ["newDetection"] = 1728,
        ["fwd"] = 1888, ["aft"] = 1892, ["total"] = 1896, ["VersionNum"] = 1900, ["headX"] = 1904, ["headY"] = 1908, ["headZ"] = 1912,
        ["MainPower"] = 1916,
    };

    /// <summary>Byte offset of every member of FlightData2 (BMS 4.38, version 23).</summary>
    public static readonly IReadOnlyDictionary<string, int> FlightData2Offsets = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["nozzlePos2"] = 0, ["rpm2"] = 4, ["ftit2"] = 8, ["oilPressure2"] = 12, ["navMode"] = 16, ["AAUZ"] = 20, ["tacanInfo"] = 24,
        ["AltCalReading"] = 28, ["altBits"] = 32, ["powerBits"] = 36, ["blinkBits"] = 40, ["cmdsMode"] = 44, ["uhf_panel_preset"] = 48,
        ["uhf_panel_frequency"] = 52, ["cabinAlt"] = 56, ["hydPressureA"] = 60, ["hydPressureB"] = 64, ["currentTime"] = 68,
        ["vehicleACD"] = 72, ["VersionNum"] = 76, ["fuelFlow2"] = 80, ["RwrInfo"] = 84, ["lefPos"] = 596, ["tefPos"] = 600,
        ["vtolPos"] = 604, ["pilotsOnline"] = 608, ["pilotsCallsign"] = 609, ["pilotsStatus"] = 993, ["bumpIntensity"] = 1028,
        ["latitude"] = 1032, ["longitude"] = 1036, ["RTT_size"] = 1040, ["RTT_area"] = 1044,
        ["iffBackupMode1Digit1"] = 1100, ["iffBackupMode1Digit2"] = 1101, ["iffBackupMode3ADigit1"] = 1102, ["iffBackupMode3ADigit2"] = 1103,
        ["instrLight"] = 1104, ["bettyBits"] = 1108, ["miscBits"] = 1112, ["RALT"] = 1116, ["bingoFuel"] = 1120, ["caraAlow"] = 1124,
        ["bullseyeX"] = 1128, ["bullseyeY"] = 1132, ["BMSVersionMajor"] = 1136, ["BMSVersionMinor"] = 1140, ["BMSVersionMicro"] = 1144,
        ["BMSBuildNumber"] = 1148, ["StringAreaSize"] = 1152, ["StringAreaTime"] = 1156, ["DrawingAreaSize"] = 1160, ["turnRate"] = 1164,
        ["floodConsole"] = 1168, ["magDeviationSystem"] = 1172, ["magDeviationReal"] = 1176, ["ecmBits"] = 1180, ["ecmOper"] = 1200,
        ["RWRjammingStatus"] = 1201, ["radio2_preset"] = 1244, ["radio2_frequency"] = 1248, ["iffTransponderActiveCode1"] = 1252,
        ["iffTransponderActiveCode2"] = 1254, ["iffTransponderActiveCode3A"] = 1256, ["iffTransponderActiveCodeC"] = 1258,
        ["iffTransponderActiveCode4"] = 1260, ["tacan_ils_frequency"] = 1264, ["desired_RTT_FPS"] = 1268, ["sideSlipdeg"] = 1272,
        ["gsMax"] = 1276, ["gsMin"] = 1280,
    };

    /// <summary>StringData.StringIdentifier values JoyPro uses (STRINGDATA_VERSION 5).</summary>
    public static class StringIds
    {
        public const uint BmsExe = 0;
        public const uint ThrName = 13;
        public const uint AcName = 29;
        public const uint AcNctr = 30;
        public const uint NavPoint = 33;
        public const uint VoiceHelpers = 35;
    }

    public static int OffsetOf(BmsArea area, string field) =>
        (area == BmsArea.FlightData ? FlightDataOffsets : FlightData2Offsets)[field];

    public static int SizeOf(BmsType type) => type switch
    {
        BmsType.F32 or BmsType.I32 or BmsType.U32 => 4,
        BmsType.I16 or BmsType.U16 => 2,
        _ => 1
    };

    public static double Read(ReadOnlySpan<byte> data, int offset, BmsType type) => type switch
    {
        BmsType.F32 => (double)BinaryPrimitives.ReadSingleLittleEndian(data[offset..]),   // (double): else the switch type is float
        BmsType.I32 => (double)BinaryPrimitives.ReadInt32LittleEndian(data[offset..]),
        BmsType.U32 => (double)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]),
        BmsType.I16 => BinaryPrimitives.ReadInt16LittleEndian(data[offset..]),
        BmsType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]),
        BmsType.I8 => (sbyte)data[offset],
        _ => data[offset]
    };

    public static readonly string[] RttAreas = ["hud", "pfl", "ded", "rwr", "mfd_left", "mfd_right", "hms"];
    public static readonly string[] PilotStates = ["in UI", "loading", "waiting", "flying", "dead", "unknown"];

    public static IReadOnlyList<BmsValue> Values { get; } = BuildValues();
    public static IReadOnlyList<BmsFlag> Flags { get; } = BuildFlags();

    private const string CPos = "Position";
    private const string CAtt = "Attitude";
    private const string CAir = "Air data";
    private const string CEng = "Engine";
    private const string CFuel = "Fuel";
    private const string CGear = "Gear and surfaces";
    private const string CLights = "Lights";
    private const string CInstr = "Instruments";
    private const string CTrim = "Trim";
    private const string CCm = "Countermeasures and ECM";
    private const string CRwr = "RWR";
    private const string CNav = "Radio, navigation and IFF";
    private const string CElec = "Electrics and hydraulics";
    private const string CSession = "Session";
    private const string CHead = "Head (input)";
    private const string CRtt = "Display export (RTT)";
    public const string CDerived = "Derived";

    private static List<BmsValue> BuildValues()
    {
        var l = new List<BmsValue>();
        void A(string field, string key, string title, string unit, double min, double max, string cat, BmsType type = BmsType.F32, int index = 0) =>
            l.Add(new BmsValue(key, title, unit, min, max, cat, BmsArea.FlightData, field, index, type, 0));
        void B(int version, string field, string key, string title, string unit, double min, double max, string cat, BmsType type = BmsType.F32, int index = 0) =>
            l.Add(new BmsValue(key, title, unit, min, max, cat, BmsArea.FlightData2, field, index, type, version));

        // FlightData
        A("x", "bms.pos.x", "Position north (sim x)", "ft", 0, 3.4e6, CPos);
        A("y", "bms.pos.y", "Position east (sim y)", "ft", 0, 3.4e6, CPos);
        A("z", "bms.pos.z", "Position down (minus altitude)", "ft", -60000, 0, CPos);
        A("xDot", "bms.vel.north", "Velocity north", "ft/s", -2500, 2500, CPos);
        A("yDot", "bms.vel.east", "Velocity east", "ft/s", -2500, 2500, CPos);
        A("zDot", "bms.vel.down", "Velocity down", "ft/s", -1000, 1000, CPos);
        A("alpha", "bms.air.alpha", "Angle of attack", "deg", -10, 40, CAir);
        A("beta", "bms.air.beta", "Sideslip angle", "deg", -20, 20, CAir);
        A("gamma", "bms.att.gamma", "Flight path angle", "rad", -Math.PI / 2, Math.PI / 2, CAtt);
        A("pitch", "bms.att.pitch", "Pitch", "rad", -Math.PI / 2, Math.PI / 2, CAtt);
        A("roll", "bms.att.roll", "Roll (right wing down positive)", "rad", -Math.PI, Math.PI, CAtt);
        A("yaw", "bms.att.yaw", "Yaw (true heading)", "rad", -Math.PI, Math.PI, CAtt);
        A("mach", "bms.air.mach", "Mach", "", 0, 2.5, CAir);
        A("kias", "bms.air.kias", "Indicated airspeed", "kt", 0, 800, CAir);
        A("vt", "bms.air.vt", "True airspeed", "ft/s", 0, 2500, CAir);
        A("gs", "bms.air.gs", "Normal load factor", "G", -3, 9, CAir);
        A("windOffset", "bms.air.wind_offset", "Wind delta to the flight path marker", "rad", -0.5, 0.5, CAir);
        A("nozzlePos", "bms.eng.nozzle", "Nozzle open", "%", 0, 100, CEng);
        A("internalFuel", "bms.fuel.internal", "Internal fuel", "lbs", 0, 20000, CFuel);
        A("externalFuel", "bms.fuel.external", "External fuel", "lbs", 0, 10000, CFuel);
        A("fuelFlow", "bms.fuel.flow", "Fuel flow", "lbs/h", 0, 80000, CFuel);
        A("rpm", "bms.eng.rpm", "Engine RPM", "%", 0, 110, CEng);
        A("ftit", "bms.eng.ftit", "Forward turbine inlet temperature", "degC", 0, 1200, CEng);
        A("gearPos", "bms.gear.pos", "Gear (0 up, 1 down)", "0..1", 0, 1, CGear);
        A("speedBrake", "bms.gear.speedbrake", "Speed brake (1 = 60 deg open)", "0..1", 0, 1, CGear);
        A("epuFuel", "bms.elec.epu_fuel", "EPU fuel", "%", 0, 100, CElec);
        A("oilPressure", "bms.eng.oil_pressure", "Oil pressure", "%", 0, 100, CEng);
        A("lightBits", "bms.light.bits", "Light bits (word)", "bits", 0, uint.MaxValue, CLights, BmsType.U32);
        A("headPitch", "bms.head.pitch", "Head pitch offset", "rad", -1.5, 1.5, CHead);
        A("headRoll", "bms.head.roll", "Head roll offset", "rad", -1.5, 1.5, CHead);
        A("headYaw", "bms.head.yaw", "Head yaw offset", "rad", -3.2, 3.2, CHead);
        A("lightBits2", "bms.light2.bits", "Light bits 2 (word)", "bits", 0, uint.MaxValue, CLights, BmsType.U32);
        A("lightBits3", "bms.light3.bits", "Light bits 3 (word)", "bits", 0, uint.MaxValue, CLights, BmsType.U32);
        A("ChaffCount", "bms.cm.chaff", "Chaff left", "", 0, 180, CCm);
        A("FlareCount", "bms.cm.flare", "Flares left", "", 0, 90, CCm);
        A("NoseGearPos", "bms.gear.nose", "Nose gear position (full down per aircraft data)", "", 0, 1, CGear);
        A("LeftGearPos", "bms.gear.left", "Left gear position", "", 0, 1, CGear);
        A("RightGearPos", "bms.gear.right", "Right gear position", "", 0, 1, CGear);
        A("AdiIlsHorPos", "bms.adi.ils_hor", "ADI ILS horizontal bar", "", -1, 1, CInstr);
        A("AdiIlsVerPos", "bms.adi.ils_ver", "ADI ILS vertical bar", "", -1, 1, CInstr);
        A("courseState", "bms.hsi.course_state", "HSI course state", "", 0, 3, CInstr, BmsType.I32);
        A("headingState", "bms.hsi.heading_state", "HSI heading state", "", 0, 3, CInstr, BmsType.I32);
        A("courseDeviation", "bms.hsi.course_deviation", "HSI course deviation", "deg", -180, 180, CInstr);
        A("desiredCourse", "bms.hsi.desired_course", "HSI desired course", "deg", 0, 360, CInstr);
        A("distanceToBeacon", "bms.hsi.distance_to_beacon", "HSI distance to beacon", "nm", 0, 300, CInstr);
        A("bearingToBeacon", "bms.hsi.bearing_to_beacon", "HSI bearing to beacon", "deg", 0, 360, CInstr);
        A("currentHeading", "bms.hsi.current_heading", "HSI current heading", "deg", 0, 360, CInstr);
        A("desiredHeading", "bms.hsi.desired_heading", "HSI desired heading", "deg", 0, 360, CInstr);
        A("deviationLimit", "bms.hsi.deviation_limit", "HSI deviation limit", "deg", 0, 20, CInstr);
        A("halfDeviationLimit", "bms.hsi.half_deviation_limit", "HSI half deviation limit", "deg", 0, 10, CInstr);
        A("localizerCourse", "bms.hsi.localizer_course", "HSI localizer course", "deg", 0, 360, CInstr);
        A("airbaseX", "bms.hsi.airbase_x", "HSI airbase north (sim x)", "ft", 0, 3.4e6, CInstr);
        A("airbaseY", "bms.hsi.airbase_y", "HSI airbase east (sim y)", "ft", 0, 3.4e6, CInstr);
        A("TrimPitch", "bms.trim.pitch", "Pitch trim", "", -0.5, 0.5, CTrim);
        A("TrimRoll", "bms.trim.roll", "Roll trim", "", -0.5, 0.5, CTrim);
        A("TrimYaw", "bms.trim.yaw", "Yaw trim", "", -0.5, 0.5, CTrim);
        A("hsiBits", "bms.hsi.bits", "HSI bits (word)", "bits", 0, uint.MaxValue, CInstr, BmsType.U32);
        A("UFCTChan", "bms.nav.tacan_ufc_channel", "TACAN channel (UFC)", "", 0, 126, CNav, BmsType.I32);
        A("AUXTChan", "bms.nav.tacan_aux_channel", "TACAN channel (AUX)", "", 0, 126, CNav, BmsType.I32);
        A("RwrObjectCount", "bms.rwr.count", "RWR contacts", "", 0, MaxRwrObjects, CRwr, BmsType.I32);
        A("fwd", "bms.fuel.fwd", "Fuel forward", "lbs", 0, 10000, CFuel);
        A("aft", "bms.fuel.aft", "Fuel aft", "lbs", 0, 10000, CFuel);
        A("total", "bms.fuel.total", "Fuel total (totaliser)", "lbs", 0, 30000, CFuel);
        A("VersionNum", "bms.version.flightdata", "FlightData version", "", 0, 200, CSession, BmsType.I32);
        A("MainPower", "bms.elec.main_power", "Main power switch (0 off, 1 batt, 2 main)", "", 0, 2, CElec, BmsType.I32);

        // FlightData2
        B(1, "nozzlePos2", "bms.eng2.nozzle", "Engine 2 nozzle open", "%", 0, 100, CEng);
        B(1, "rpm2", "bms.eng2.rpm", "Engine 2 RPM", "%", 0, 110, CEng);
        B(1, "ftit2", "bms.eng2.ftit", "Engine 2 FTIT", "degC", 0, 1200, CEng);
        B(1, "oilPressure2", "bms.eng2.oil_pressure", "Engine 2 oil pressure", "%", 0, 100, CEng);
        B(1, "navMode", "bms.nav.mode", "HSI mode (0 ILS/TCN, 1 TCN, 2 NAV, 3 ILS/NAV)", "", 0, 3, CNav, BmsType.U8);
        B(1, "AAUZ", "bms.alt.baro", "Barometric altitude (AAU)", "ft", 0, 60000, CInstr);
        B(1, "tacanInfo", "bms.nav.tacan_ufc_bits", "TACAN band/mode bits (UFC)", "bits", 0, 3, CNav, BmsType.U8);
        B(1, "tacanInfo", "bms.nav.tacan_aux_bits", "TACAN band/mode bits (AUX)", "bits", 0, 3, CNav, BmsType.U8, 1);
        B(2, "AltCalReading", "bms.alt.cal_reading", "Altimeter setting (hPa or inHg x100)", "", 0, 3100, CInstr, BmsType.I32);
        B(2, "altBits", "bms.alt.bits", "Altimeter bits (word)", "bits", 0, 3, CInstr, BmsType.U32);
        B(2, "powerBits", "bms.elec.power_bits", "Power bits (word)", "bits", 0, 127, CElec, BmsType.U32);
        B(2, "blinkBits", "bms.blink.bits", "Blink bits (word)", "bits", 0, 8191, CLights, BmsType.U32);
        B(2, "cmdsMode", "bms.cm.cmds_mode", "CMDS mode (0 off, 1 stby, 2 man, 3 semi, 4 auto, 5 byp)", "", 0, 5, CCm, BmsType.I32);
        B(2, "uhf_panel_preset", "bms.radio.uhf_preset", "UHF (radio 1) preset", "", 0, 20, CNav, BmsType.I32);
        B(3, "uhf_panel_frequency", "bms.radio.uhf_frequency", "UHF (radio 1) frequency", "kHz/10", 0, 400000, CNav, BmsType.I32);
        B(3, "cabinAlt", "bms.env.cabin_alt", "Cabin altitude", "ft", 0, 50000, CElec);
        B(3, "hydPressureA", "bms.hyd.pressure_a", "Hydraulic pressure A", "psi", 0, 4000, CElec);
        B(3, "hydPressureB", "bms.hyd.pressure_b", "Hydraulic pressure B", "psi", 0, 4000, CElec);
        B(3, "currentTime", "bms.time.of_day", "Time of day", "s", 0, 86400, CSession, BmsType.I32);
        B(3, "vehicleACD", "bms.session.vehicle_acd", "Aircraft type (ACD index)", "", 0, 2000, CSession, BmsType.I16);
        B(3, "VersionNum", "bms.version.flightdata2", "FlightData2 version", "", 0, 100, CSession, BmsType.I32);
        B(4, "fuelFlow2", "bms.fuel.flow2", "Fuel flow engine 2", "lbs/h", 0, 80000, CFuel);
        B(5, "lefPos", "bms.surf.lef", "Leading edge flaps", "deg", -5, 30, CGear);
        B(5, "tefPos", "bms.surf.tef", "Trailing edge flaps", "deg", -25, 25, CGear);
        B(6, "vtolPos", "bms.surf.vtol", "VTOL exhaust angle", "deg", 0, 100, CGear);
        B(9, "pilotsOnline", "bms.mp.pilots_online", "Pilots in the session", "", 0, MaxCallsigns, CSession, BmsType.U8);
        B(10, "bumpIntensity", "bms.gear.bump", "Taxi/rolling bump intensity", "0..1", 0, 1, CGear);
        B(11, "latitude", "bms.pos.lat", "Latitude (avionics)", "deg", -90, 90, CPos);
        B(11, "longitude", "bms.pos.lon", "Longitude (avionics)", "deg", -180, 180, CPos);
        B(12, "RTT_size", "bms.rtt.width", "RTT texture width", "px", 0, 8192, CRtt, BmsType.U16);
        B(12, "RTT_size", "bms.rtt.height", "RTT texture height", "px", 0, 8192, CRtt, BmsType.U16, 1);
        string[] sides = ["left", "top", "right", "bottom"];
        for (var a = 0; a < RttAreas.Length; a++)
        {
            for (var s = 0; s < 4; s++)
                B(12, "RTT_area", $"bms.rtt.{RttAreas[a]}.{sides[s]}", $"RTT {RttAreas[a].ToUpperInvariant().Replace('_', ' ')} area {sides[s]}", "px", 0, 8192, CRtt, BmsType.U16, a * 4 + s);
        }

        B(13, "iffBackupMode1Digit1", "bms.iff.backup_m1_digit1", "IFF backup mode 1 digit 1", "", 0, 7, CNav, BmsType.I8);
        B(13, "iffBackupMode1Digit2", "bms.iff.backup_m1_digit2", "IFF backup mode 1 digit 2", "", 0, 7, CNav, BmsType.I8);
        B(13, "iffBackupMode3ADigit1", "bms.iff.backup_m3a_digit1", "IFF backup mode 3A digit 1", "", 0, 7, CNav, BmsType.I8);
        B(13, "iffBackupMode3ADigit2", "bms.iff.backup_m3a_digit2", "IFF backup mode 3A digit 2", "", 0, 7, CNav, BmsType.I8);
        B(14, "instrLight", "bms.light.instr", "Instrument backlight (0 off, 1 dim, 2 bright)", "", 0, 2, CLights, BmsType.U8);
        B(15, "bettyBits", "bms.betty.bits", "Betty voice messages playing (word)", "bits", 0, 262143, CLights, BmsType.U32);
        B(15, "miscBits", "bms.misc.bits", "Misc bits (word)", "bits", 0, 63, CElec, BmsType.U32);
        B(15, "RALT", "bms.radar.ralt", "Radar altitude (valid when bms.misc.ralt_valid)", "ft", 0, 50000, CInstr);
        B(15, "bingoFuel", "bms.fuel.bingo", "Bingo fuel", "lbs", 0, 20000, CFuel);
        B(15, "caraAlow", "bms.radar.cara_alow", "CARA ALOW setting", "ft", 0, 50000, CInstr);
        B(15, "bullseyeX", "bms.nav.bullseye_x", "Bullseye north (sim x)", "ft", 0, 3.4e6, CNav);
        B(15, "bullseyeY", "bms.nav.bullseye_y", "Bullseye east (sim y)", "ft", 0, 3.4e6, CNav);
        B(15, "BMSVersionMajor", "bms.version.major", "BMS version major", "", 0, 10, CSession, BmsType.I32);
        B(15, "BMSVersionMinor", "bms.version.minor", "BMS version minor", "", 0, 100, CSession, BmsType.I32);
        B(15, "BMSVersionMicro", "bms.version.micro", "BMS version micro", "", 0, 100, CSession, BmsType.I32);
        B(15, "BMSBuildNumber", "bms.version.build", "BMS build number", "", 0, 100000, CSession, BmsType.I32);
        B(15, "StringAreaSize", "bms.strings.size", "String area size", "bytes", 0, StringAreaSizeMax, CSession, BmsType.U32);
        B(15, "StringAreaTime", "bms.strings.time", "String area last change", "", 0, uint.MaxValue, CSession, BmsType.U32);
        B(15, "DrawingAreaSize", "bms.drawing.size", "Drawing area size", "bytes", 0, StringAreaSizeMax, CSession, BmsType.U32);
        B(16, "turnRate", "bms.att.turn_rate", "Turn rate", "deg/s", -30, 30, CAtt);
        B(18, "floodConsole", "bms.light.flood_console", "Flood console brightness (0..6)", "", 0, 6, CLights, BmsType.U8);
        B(19, "magDeviationSystem", "bms.nav.mag_deviation_system", "Magnetic deviation (system)", "deg", -30, 30, CNav);
        B(19, "magDeviationReal", "bms.nav.mag_deviation_real", "Magnetic deviation (real)", "deg", -30, 30, CNav);
        for (var p = 0; p < 5; p++)
            B(19, "ecmBits", $"bms.ecm.program{p + 1}", $"ECM program {p + 1} button state", "bits", 0, 128, CCm, BmsType.U32, p);
        B(19, "ecmOper", "bms.ecm.oper", "ECM OPER lamp (0 off, 1 stby, 2 active, 3 all lit)", "", 0, 3, CCm, BmsType.U8);
        B(20, "radio2_preset", "bms.radio.radio2_preset", "Radio 2 preset", "", 0, 20, CNav, BmsType.I32);
        B(20, "radio2_frequency", "bms.radio.radio2_frequency", "Radio 2 frequency", "kHz/10", 0, 400000, CNav, BmsType.I32);
        B(20, "iffTransponderActiveCode1", "bms.iff.mode1", "IFF active code mode 1 (negative = off)", "", -1, 77, CNav, BmsType.I8);
        B(20, "iffTransponderActiveCode2", "bms.iff.mode2", "IFF active code mode 2", "", -1, 7777, CNav, BmsType.I16);
        B(20, "iffTransponderActiveCode3A", "bms.iff.mode3a", "IFF active code mode 3A", "", -1, 7777, CNav, BmsType.I16);
        B(20, "iffTransponderActiveCodeC", "bms.iff.modec", "IFF active code mode C", "", -1, 7777, CNav, BmsType.I16);
        B(20, "iffTransponderActiveCode4", "bms.iff.mode4", "IFF active code mode 4", "", -1, 7777, CNav, BmsType.I16);
        B(21, "tacan_ils_frequency", "bms.nav.tacan_ils_frequency", "TACAN ILS frequency (11030 = 110.30)", "", 10810, 11195, CNav, BmsType.I32);
        B(22, "desired_RTT_FPS", "bms.rtt.desired_fps", "RTT export FPS setting", "fps", 0, 120, CRtt, BmsType.I32);
        B(23, "sideSlipdeg", "bms.air.sideslip", "ADI side slip", "deg", -20, 20, CAir);
        B(23, "gsMax", "bms.air.gs_max", "Max G", "G", -3, 12, CAir);
        B(23, "gsMin", "bms.air.gs_min", "Min G", "G", -5, 3, CAir);
        return l;
    }

    private static List<BmsFlag> BuildFlags()
    {
        var l = new List<BmsFlag>();
        void F(string word, string category, params (string Name, uint Mask, string Title)[] bits)
        {
            var prefix = word[..word.LastIndexOf('.')];
            foreach (var (name, mask, title) in bits)
                l.Add(new BmsFlag(prefix + "." + name, title, word, mask, category));
        }

        F("bms.light.bits", CLights,
            ("master_caution", 0x1, "MASTER CAUTION"), ("tf", 0x2, "TF FAIL"), ("oxy_brow", 0x4, "OXY LOW (eyebrow)"), ("equip_hot", 0x8, "EQUIP HOT"),
            ("on_ground", 0x10, "On ground"), ("eng_fire", 0x20, "ENG FIRE"), ("config", 0x40, "CONFIG"), ("hyd", 0x80, "HYD/OIL PRESS"),
            ("flcs_abcd", 0x100, "FLCS channel lamps (test panel)"), ("flcs", 0x200, "FLCS"), ("can", 0x400, "CANOPY"), ("t_l_cfg", 0x800, "TO/LDG CONFIG"),
            ("aoa_above", 0x1000, "AOA indexer above"), ("aoa_on", 0x2000, "AOA indexer on speed"), ("aoa_below", 0x4000, "AOA indexer below"),
            ("refuel_rdy", 0x8000, "Refuel RDY"), ("refuel_ar", 0x10000, "Refuel AR/NWS"), ("refuel_dsc", 0x20000, "Refuel DISC"),
            ("flt_control_sys", 0x40000, "FLT CONTROL SYS"), ("le_flaps", 0x80000, "LE FLAPS"), ("engine_fault", 0x100000, "ENGINE FAULT"),
            ("overheat", 0x200000, "OVERHEAT"), ("fuel_low", 0x400000, "FUEL LOW"), ("avionics", 0x800000, "AVIONICS"), ("radar_alt", 0x1000000, "RADAR ALT"),
            ("iff", 0x2000000, "IFF"), ("ecm", 0x4000000, "ECM"), ("hook", 0x8000000, "HOOK"), ("nws_fail", 0x10000000, "NWS FAIL"),
            ("cabin_press", 0x20000000, "CABIN PRESS"), ("autopilot_on", 0x40000000, "Autopilot on"), ("tfr_stby", 0x80000000, "TFR STBY"));
        F("bms.light2.bits", CLights,
            ("handoff", 0x1, "TWP HANDOFF"), ("launch", 0x2, "TWP MISSILE LAUNCH"), ("pri_mode", 0x4, "TWP PRI MODE"), ("naval", 0x8, "TWP NAVAL"),
            ("unk", 0x10, "TWP UNKNOWN"), ("tgt_sep", 0x20, "TWP TGT SEP"), ("ews_go", 0x40, "EWS GO"), ("ews_nogo", 0x80, "EWS NO GO"),
            ("ews_degr", 0x100, "EWS AUTO DEGR"), ("ews_rdy", 0x200, "EWS DISPENSE RDY"), ("chaff_lo", 0x400, "Chaff low"), ("flare_lo", 0x800, "Flare low"),
            ("aux_srch", 0x1000, "TWA SEARCH"), ("aux_act", 0x2000, "TWA ACT/PWR"), ("aux_low", 0x4000, "TWA LOW ALT"), ("aux_pwr", 0x8000, "TWA SYSTEM PWR"),
            ("ecm_pwr", 0x10000, "ECM power"), ("ecm_fail", 0x20000, "ECM fail"), ("fwd_fuel_low", 0x40000, "FWD FUEL LOW"), ("aft_fuel_low", 0x80000, "AFT FUEL LOW"),
            ("epu_on", 0x100000, "EPU run"), ("jfs_on", 0x200000, "JFS run"), ("sec", 0x400000, "SEC"), ("oxy_low", 0x800000, "OXY LOW"),
            ("probeheat", 0x1000000, "PROBE HEAT"), ("seat_arm", 0x2000000, "SEAT NOT ARMED"), ("buc", 0x4000000, "BUC"), ("fuel_oil_hot", 0x8000000, "FUEL OIL HOT"),
            ("anti_skid", 0x10000000, "ANTI SKID"), ("tfr_engaged", 0x20000000, "TFR ENGAGED"), ("gear_handle", 0x40000000, "Gear handle light"), ("engine", 0x80000000, "ENGINE"));
        F("bms.light3.bits", CLights,
            ("flcs_pmg", 0x1, "FLCS PMG"), ("main_gen", 0x2, "MAIN GEN"), ("stby_gen", 0x4, "STBY GEN"), ("epu_gen", 0x8, "EPU GEN"),
            ("epu_pmg", 0x10, "EPU PMG"), ("to_flcs", 0x20, "TO FLCS"), ("flcs_rly", 0x40, "FLCS RLY"), ("bat_fail", 0x80, "BATT FAIL"),
            ("hydrazine", 0x100, "EPU HYDRAZINE"), ("air", 0x200, "EPU AIR"), ("elec_fault", 0x400, "ELEC SYS"), ("lef_fault", 0x800, "LEF fault"),
            ("on_ground", 0x1000, "Weight on wheels"), ("flcs_bit_run", 0x2000, "FLCS BIT RUN"), ("flcs_bit_fail", 0x4000, "FLCS BIT FAIL"), ("dbu_warn", 0x8000, "DBU ON"),
            ("nose_gear_down", 0x10000, "Nose gear down and locked"), ("left_gear_down", 0x20000, "Left gear down and locked"), ("right_gear_down", 0x40000, "Right gear down and locked"),
            ("park_brake_on", 0x100000, "Parking brake on"), ("power_off", 0x200000, "No electrical power"), ("cadc", 0x400000, "CADC"),
            ("speed_brake", 0x800000, "Speed brake not stowed"), ("sys_test", 0x1000000, "TWP SYS TEST"), ("mc_announced", 0x2000000, "Master caution announced"),
            ("mlg_wow", 0x4000000, "MLG WOW (AFM)"), ("nlg_wow", 0x8000000, "NLG WOW (AFM)"), ("atf_not_engaged", 0x10000000, "ATF NOT ENGAGED"),
            ("inlet_icing", 0x20000000, "INLET ICING"));
        F("bms.hsi.bits", CInstr,
            ("to_true", 0x1, "HSI TO"), ("ils_warning", 0x2, "HSI ILS warning"), ("course_warning", 0x4, "HSI course warning"), ("init", 0x8, "HSI init"),
            ("adi_off", 0x20, "ADI OFF flag"), ("adi_aux", 0x40, "ADI AUX flag"), ("adi_gs", 0x80, "ADI GS flag"), ("adi_loc", 0x100, "ADI LOC flag"),
            ("hsi_off", 0x200, "HSI OFF flag"), ("bup_adi_off", 0x400, "Backup ADI OFF flag"), ("vvi_off", 0x800, "VVI OFF flag"), ("aoa_off", 0x1000, "AOA OFF flag"),
            ("avtr", 0x2000, "AVTR light"), ("outer_marker", 0x4000, "Outer marker"), ("middle_marker", 0x8000, "Middle marker"), ("from_true", 0x10000, "HSI FROM"),
            ("flying", HsiFlying, "In 3D (attached to an aircraft)"));
        F("bms.alt.bits", CInstr, ("cal_inhg", 0x1, "Altimeter in inHg"), ("pneu_flag", 0x2, "Altimeter PNEU flag"));
        F("bms.elec.power_bits", CElec,
            ("bus_battery", 0x1, "Battery bus powered"), ("bus_emergency", 0x2, "Emergency bus powered"), ("bus_essential", 0x4, "Essential bus powered"),
            ("bus_non_essential", 0x8, "Non-essential bus powered"), ("main_generator", 0x10, "Main generator online"), ("standby_generator", 0x20, "Standby generator online"),
            ("jfs_running", 0x40, "JFS running"));
        F("bms.blink.bits", CLights,
            ("outer_marker", 0x1, "Outer marker blinking"), ("middle_marker", 0x2, "Middle marker blinking"), ("probeheat", 0x4, "PROBE HEAT blinking"),
            ("aux_srch", 0x8, "TWA SEARCH blinking"), ("launch", 0x10, "LAUNCH blinking"), ("pri_mode", 0x20, "PRI MODE blinking"), ("unk", 0x40, "UNKNOWN blinking"),
            ("elec_fault", 0x80, "ELEC SYS blinking"), ("oxy_brow", 0x100, "OXY blinking"), ("epu_on", 0x200, "EPU blinking"),
            ("jfs_on_slow", 0x400, "JFS slow blink"), ("jfs_on_fast", 0x800, "JFS fast blink"), ("ecm_oper", 0x1000, "ECM OPER blinking"));
        F("bms.betty.bits", CLights,
            ("allwords", 0x1, "Betty: all words"), ("pullup", 0x2, "Betty: pull up"), ("altitude", 0x4, "Betty: altitude"), ("warning", 0x8, "Betty: warning"),
            ("jammer", 0x10, "Betty: jammer"), ("counter", 0x20, "Betty: counter"), ("chaff_flare", 0x40, "Betty: chaff flare"), ("chaff_flare_low", 0x80, "Betty: chaff flare low"),
            ("chaff_flare_out", 0x100, "Betty: chaff flare out"), ("lock", 0x200, "Betty: lock"), ("caution", 0x400, "Betty: caution"), ("bingo", 0x800, "Betty: bingo"),
            ("data", 0x1000, "Betty: data"), ("iff", 0x2000, "Betty: IFF"), ("lowspeed", 0x4000, "Betty: low speed"), ("beeps", 0x8000, "Betty: beeps"),
            ("aoa", 0x10000, "Betty: AOA"), ("max_g", 0x20000, "Betty: max G"));
        F("bms.misc.bits", CElec,
            ("ralt_valid", 0x1, "Radar altitude valid"), ("flcc_a", 0x2, "FLCC A"), ("flcc_b", 0x4, "FLCC B"), ("flcc_c", 0x8, "FLCC C"), ("flcc_d", 0x10, "FLCC D"),
            ("solenoid_ok", 0x20, "Solenoid working"));
        F("bms.nav.tacan_ufc_bits", CNav, ("tacan_ufc_x", 0x1, "TACAN UFC band X"), ("tacan_ufc_aa", 0x2, "TACAN UFC A/A"));
        F("bms.nav.tacan_aux_bits", CNav, ("tacan_aux_x", 0x1, "TACAN AUX band X"), ("tacan_aux_aa", 0x2, "TACAN AUX A/A"));
        return l;
    }

    /// <summary>Parses the StringData area (VersionNum, NoOfStrings, dataSize, then id/length/chars+NUL per string).</summary>
    public static List<(uint Id, string Text)> ParseStrings(ReadOnlySpan<byte> area)
    {
        var list = new List<(uint, string)>();
        if (area.Length < 12)
            return list;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(area[4..]);
        var offset = 12;
        for (var i = 0u; i < count && i < 100000; i++)
        {
            if (offset + 8 > area.Length)
                break;
            var id = BinaryPrimitives.ReadUInt32LittleEndian(area[offset..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(area[(offset + 4)..]);
            offset += 8;
            if (length > (uint)(area.Length - offset))
                break;
            list.Add((id, Encoding.Latin1.GetString(area.Slice(offset, (int)length))));
            offset += (int)length + 1;
        }

        return list;
    }

    /// <summary>A NUL-terminated char field (DED line, callsign).</summary>
    public static string ReadText(ReadOnlySpan<byte> data, int offset, int length)
    {
        var span = data.Slice(offset, length);
        var end = span.IndexOf((byte)0);
        if (end >= 0)
            span = span[..end];
        var chars = new char[span.Length];
        for (var i = 0; i < span.Length; i++)
        {
            var b = span[i];
            chars[i] = b < 0x20 || b == 0x7F ? ' ' : (char)b;   // the DED's own symbols (arrows) are control codes
        }

        return new string(chars).TrimEnd();
    }
}

/// <summary>Falcon BMS's ownship telemetry from its shared memory areas (FlightData, FlightData2, StringData).</summary>
/// <remarks>
/// Frames are published only while the player is in 3D (FlightData.hsiBits "Flying") and only when the areas changed.
/// Entering 3D raises <see cref="TelemetryEventKinds.SessionStart"/>, leaving it (or the feed stopping)
/// <see cref="TelemetryEventKinds.SessionEnd"/>. DED and PFL lines are events ("bms.ded", "bms.pfl"; 5 lines joined by
/// '\n') at most <see cref="TextEventsPerSecond"/> per second; the MP pilot list is an event "bms.pilots" (one
/// "callsign|state" per line); bullseye and the steerpoints of the string area are an <see cref="TelemetryEventKinds.Objects"/>
/// snapshot. RWR contacts are values (bms.rwr.N.*): BMS gives only their bearing, no position.
/// </remarks>
public sealed class BmsTelemetrySource : FrameTelemetrySource
{
    public const double TextEventsPerSecond = 4;
    public const string DedEvent = "bms.ded";
    public const string PflEvent = "bms.pfl";
    public const string PilotsEvent = "bms.pilots";
    public const string FlightInfoKey = "bms.flight";
    private const double FeetPerNm = 6076.12;
    private const double MetresPerDegree = 111320;

    private readonly object _gate = new();
    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);
    private byte[] _lastFd = [];
    private byte[] _lastFd2 = [];
    private bool _flying;
    private readonly TextChannel _ded = new(DedEvent);
    private readonly TextChannel _pfl = new(PflEvent);
    private string? _pilots;
    private string? _objects;
    private bool _objectsDirty = true;
    private double _bullX = double.NaN, _bullY = double.NaN;

    private string? _theater, _aircraft, _nctr, _flight;
    private readonly List<(int Index, string Type, double X, double Y, double Z, string Extra)> _navPoints = [];

    public BmsTelemetrySource(IFfbClock clock) : base("Falcon BMS", Catalogue, clock)
    {
    }

    public static IReadOnlyList<TelemetryDescriptor> Catalogue { get; } = BuildCatalogue();

    /// <summary>True while the player is in 3D.</summary>
    public bool Flying
    {
        get { lock (_gate) return _flying; }
    }

    public string? Theater => _theater;
    public string? AircraftName => _aircraft;

    public override IReadOnlyList<TelemetryNavMapping> NavMappings { get; } =
    [
        new(TelemetryNav.Latitude, "bms.pos.lat"),
        new(TelemetryNav.Longitude, "bms.pos.lon"),
        new(TelemetryNav.Altitude, "bms.pos.z", -TelemetryNav.FeetToMetres),
        new(TelemetryNav.U, "bms.pos.y", TelemetryNav.FeetToMetres),
        new(TelemetryNav.V, "bms.pos.x", TelemetryNav.FeetToMetres),
        new(TelemetryNav.Roll, "bms.att.roll", TelemetryNav.RadToDeg),
        new(TelemetryNav.Pitch, "bms.att.pitch", TelemetryNav.RadToDeg),
        new(TelemetryNav.Yaw, "bms.calc.heading"),
        new(TelemetryNav.Heading, "bms.calc.track"),
        new(TelemetryNav.Agl, "bms.calc.agl", TelemetryNav.FeetToMetres),
        new(TelemetryNav.Ias, "bms.air.kias", TelemetryNav.KnotsToMetresPerSecond),
        new(TelemetryNav.Tas, "bms.air.vt", TelemetryNav.FeetToMetres),
        new(TelemetryNav.Mach, "bms.air.mach"),
        new(TelemetryNav.Aoa, "bms.air.alpha"),
        new(TelemetryNav.Aos, "bms.air.beta"),
        new(TelemetryNav.VerticalG, "bms.air.gs"),
        new(TelemetryNav.Gear, "bms.gear.pos"),
        new(TelemetryNav.Airbrakes, "bms.gear.speedbrake"),
        new(TelemetryNav.FuelKg, "bms.calc.fuel", TelemetryNav.PoundsToKg),
        new(TelemetryNav.VerticalSpeed, "bms.vel.down", -TelemetryNav.FeetToMetres),
    ];

    /// <summary>Handles the string area (theatre, aircraft, steerpoints). The feed calls it when StringAreaTime changes.</summary>
    public void IngestStrings(ReadOnlySpan<byte> area)
    {
        var strings = BmsSharedMemory.ParseStrings(area);
        lock (_gate)
        {
            string? theater = null, aircraft = null, nctr = null, flight = null;
            _navPoints.Clear();
            foreach (var (id, text) in strings)
            {
                switch (id)
                {
                    case BmsSharedMemory.StringIds.ThrName: theater = text.Trim(); break;
                    case BmsSharedMemory.StringIds.AcName: aircraft = text.Trim(); break;
                    case BmsSharedMemory.StringIds.AcNctr: nctr = text.Trim(); break;
                    case BmsSharedMemory.StringIds.VoiceHelpers:
                        var bar = text.IndexOf('|');
                        flight = (bar > 0 ? text[..bar] : "").Trim();
                        break;
                    case BmsSharedMemory.StringIds.NavPoint:
                        ParseNavPoint(text);
                        break;
                }
            }

            _theater = NullIfEmpty(theater);
            _aircraft = NullIfEmpty(aircraft);
            _nctr = NullIfEmpty(nctr);
            _flight = NullIfEmpty(flight);
            _objectsDirty = true;
        }
    }

    /// <summary>Handles one poll of FlightData and FlightData2 (FlightData2 may be empty). Public for tests.</summary>
    /// <returns>True when a frame was published.</returns>
    public bool Ingest(ReadOnlySpan<byte> fd, ReadOnlySpan<byte> fd2)
    {
        lock (_gate)
        {
            if (fd.Length < BmsSharedMemory.FlightDataSize)
                return false;
            var fdChanged = !fd.SequenceEqual(_lastFd);
            var fd2Changed = !fd2.SequenceEqual(_lastFd2);
            if (!fdChanged && !fd2Changed)
            {
                if (_flying)
                {
                    _ded.Flush(this, Clock.Now);
                    _pfl.Flush(this, Clock.Now);
                }

                return false;
            }

            if (fdChanged)
                _lastFd = fd.ToArray();
            if (fd2Changed)
                _lastFd2 = fd2.ToArray();

            var hsi = BinaryPrimitives.ReadUInt32LittleEndian(fd[BmsSharedMemory.FlightDataOffsets["hsiBits"]..]);
            var flying = (hsi & BmsSharedMemory.HsiFlying) != 0;
            var wasFlying = _flying;
            if (!flying)
            {
                if (wasFlying)
                {
                    PublishRawChanged(fd, fd2, fdChanged, fd2Changed);
                    EndSessionLocked("left the 3D world");
                }

                return false;
            }

            PublishRawChanged(fd, fd2, fdChanged, fd2Changed);
            Fill(fd, fd2);
            if (!wasFlying)
            {
                _flying = true;
                ApplyInfo();
                RaiseEvent(TelemetryEventKinds.SessionStart, string.Join(", ", new[] { _aircraft, _theater }.Where(s => !string.IsNullOrEmpty(s))));
                _objectsDirty = true;
                _objects = null;
                _pilots = null;
                _ded.Reset();
                _pfl.Reset();
            }
            else
            {
                ApplyInfo();
            }

            var now = Clock.Now;
            _ded.Offer(this, now, Lines(fd, "DEDLines"));
            _pfl.Offer(this, now, Lines(fd, "PFLLines"));
            UpdatePilots(fd2);
            UpdateObjects();
            Publish(new TelemetryFrame(now, _values));
            return true;
        }
    }

    /// <summary>The feed stopped (or BMS went away): ends a running session.</summary>
    public void EndSession(string reason)
    {
        lock (_gate)
        {
            if (_flying)
                EndSessionLocked(reason);
            _lastFd = [];
            _lastFd2 = [];
        }
    }

    private void EndSessionLocked(string reason)
    {
        _flying = false;
        _ded.Reset();
        _pfl.Reset();
        RaiseEvent(TelemetryEventKinds.SessionEnd, reason);
        Clear();
    }

    private void PublishRawChanged(ReadOnlySpan<byte> fd, ReadOnlySpan<byte> fd2, bool fdChanged, bool fd2Changed)
    {
        if (!WantsRaw)
            return;
        if (fdChanged)
            PublishRaw("shm:FlightData", fd);
        if (fd2Changed && !fd2.IsEmpty)
            PublishRaw("shm:FlightData2", fd2);
    }

    /// <summary>The raw string area for the recorder (the feed calls it after a re-read).</summary>
    internal void PublishStringsRaw(ReadOnlySpan<byte> area)
    {
        if (WantsRaw)
            PublishRaw("shm:StringData", area);
    }

    private void Fill(ReadOnlySpan<byte> fd, ReadOnlySpan<byte> fd2)
    {
        _values.Clear();
        var version2 = fd2.Length >= 80 ? BinaryPrimitives.ReadInt32LittleEndian(fd2[76..]) : 0;
        foreach (var v in BmsSharedMemory.Values)
        {
            var data = v.Area == BmsArea.FlightData ? fd : fd2;
            if (v.Area == BmsArea.FlightData2 && version2 < v.MinVersion)
                continue;
            var offset = v.Offset;
            if (offset + BmsSharedMemory.SizeOf(v.Type) > data.Length)
                continue;
            var value = BmsSharedMemory.Read(data, offset, v.Type);
            if (double.IsFinite(value))
                _values[v.Key] = value;
        }

        foreach (var f in BmsSharedMemory.Flags)
        {
            if (_values.TryGetValue(f.WordKey, out var word))
                _values[f.Key] = ((uint)word & f.Mask) != 0 ? 1 : 0;
        }

        // RWR contacts (only the active slots)
        var count = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(fd[BmsSharedMemory.FlightDataOffsets["RwrObjectCount"]..]), 0, BmsSharedMemory.MaxRwrObjects);
        var jam = BmsSharedMemory.FlightData2Offsets["RWRjammingStatus"];
        for (var i = 0; i < count; i++)
        {
            var p = "bms.rwr." + i.ToString(CultureInfo.InvariantCulture) + ".";
            _values[p + "symbol"] = BinaryPrimitives.ReadInt32LittleEndian(fd[(BmsSharedMemory.FlightDataOffsets["RWRsymbol"] + 4 * i)..]);
            _values[p + "bearing"] = BinaryPrimitives.ReadSingleLittleEndian(fd[(BmsSharedMemory.FlightDataOffsets["bearing"] + 4 * i)..]);
            _values[p + "missile_activity"] = BinaryPrimitives.ReadUInt32LittleEndian(fd[(BmsSharedMemory.FlightDataOffsets["missileActivity"] + 4 * i)..]);
            _values[p + "missile_launch"] = BinaryPrimitives.ReadUInt32LittleEndian(fd[(BmsSharedMemory.FlightDataOffsets["missileLaunch"] + 4 * i)..]);
            _values[p + "selected"] = BinaryPrimitives.ReadUInt32LittleEndian(fd[(BmsSharedMemory.FlightDataOffsets["selected"] + 4 * i)..]);
            _values[p + "lethality"] = BinaryPrimitives.ReadSingleLittleEndian(fd[(BmsSharedMemory.FlightDataOffsets["lethality"] + 4 * i)..]);
            _values[p + "new"] = BinaryPrimitives.ReadUInt32LittleEndian(fd[(BmsSharedMemory.FlightDataOffsets["newDetection"] + 4 * i)..]);
            if (version2 >= 19 && jam + i < fd2.Length)
                _values[p + "jamming"] = fd2[jam + i];
        }

        // MP pilot states
        if (_values.TryGetValue("bms.mp.pilots_online", out var online))
        {
            var status = BmsSharedMemory.FlightData2Offsets["pilotsStatus"];
            for (var i = 0; i < Math.Min((int)online, BmsSharedMemory.MaxCallsigns); i++)
                _values["bms.mp.pilot." + i.ToString(CultureInfo.InvariantCulture) + ".state"] = (sbyte)fd2[status + i];
        }

        // Derived values
        var x = _values["bms.pos.x"];
        var y = _values["bms.pos.y"];
        var vn = _values["bms.vel.north"];
        var ve = _values["bms.vel.east"];
        _values["bms.calc.heading"] = Degrees360(_values["bms.att.yaw"] * TelemetryNav.RadToDeg);
        if (Math.Abs(vn) + Math.Abs(ve) > 1e-3)
            _values["bms.calc.track"] = Degrees360(Math.Atan2(ve, vn) * TelemetryNav.RadToDeg);
        _values["bms.calc.ground_speed"] = Math.Sqrt(vn * vn + ve * ve) * 3600 / FeetPerNm;
        _values["bms.calc.alt"] = -_values["bms.pos.z"];
        _values["bms.calc.vs"] = -_values["bms.vel.down"] * 60;
        _values["bms.calc.fuel"] = _values["bms.fuel.internal"] + _values["bms.fuel.external"];
        if (_values.TryGetValue("bms.misc.ralt_valid", out var raltValid) && raltValid > 0 && _values.TryGetValue("bms.radar.ralt", out var ralt))
            _values["bms.calc.agl"] = ralt;
        if (_values.TryGetValue("bms.nav.bullseye_x", out var bx) && _values.TryGetValue("bms.nav.bullseye_y", out var by) && (bx != 0 || by != 0))
        {
            _values["bms.calc.bullseye_bearing"] = Degrees360(Math.Atan2(y - by, x - bx) * TelemetryNav.RadToDeg);
            _values["bms.calc.bullseye_range"] = Math.Sqrt((x - bx) * (x - bx) + (y - by) * (y - by)) / FeetPerNm;
            if (bx != _bullX || by != _bullY)
            {
                _bullX = bx;
                _bullY = by;
                _objectsDirty = true;
            }
        }
    }

    private void ApplyInfo()
    {
        var aircraft = _aircraft;
        if (aircraft is null && _values.TryGetValue("bms.session.vehicle_acd", out var acd))
            aircraft = "ACD " + acd.ToString(CultureInfo.InvariantCulture);
        SetInfo(TelemetryInfoKeys.Aircraft, aircraft);
        SetInfo(TelemetryInfoKeys.Map, _theater);
        SetInfo(FlightInfoKey, _flight);
        if (_values.TryGetValue("bms.version.major", out var major) && major > 0)
        {
            static string I(double d) => ((long)d).ToString(CultureInfo.InvariantCulture);
            SetInfo(TelemetryInfoKeys.GameVersion, $"{I(major)}.{I(_values["bms.version.minor"])}.{I(_values["bms.version.micro"])} build {I(_values["bms.version.build"])}");
        }
    }

    private void UpdatePilots(ReadOnlySpan<byte> fd2)
    {
        if (!_values.TryGetValue("bms.mp.pilots_online", out var onlineValue))
            return;
        var online = Math.Min((int)onlineValue, BmsSharedMemory.MaxCallsigns);
        var names = BmsSharedMemory.FlightData2Offsets["pilotsCallsign"];
        var status = BmsSharedMemory.FlightData2Offsets["pilotsStatus"];
        var sb = new StringBuilder();
        string? first = null;
        for (var i = 0; i < online; i++)
        {
            var name = BmsSharedMemory.ReadText(fd2, names + i * BmsSharedMemory.CallsignLength, BmsSharedMemory.CallsignLength).Replace('|', '/');
            first ??= name;
            var state = (sbyte)fd2[status + i];
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(name).Append('|').Append(state >= 0 && state < BmsSharedMemory.PilotStates.Length ? BmsSharedMemory.PilotStates[state] : state.ToString(CultureInfo.InvariantCulture));
        }

        // Single player (and an MP session of one): the list holds just the player's own callsign.
        SetInfo(TelemetryInfoKeys.Pilot, online == 1 ? first : null);
        var text = sb.ToString();
        if (text == _pilots)
            return;
        _pilots = text;
        if (online > 0)
            RaiseEvent(PilotsEvent, text);
    }

    private void UpdateObjects()
    {
        if (!_objectsDirty)
            return;
        _objectsDirty = false;
        var lines = new List<string>();
        if (!double.IsNaN(_bullX) && (_bullX != 0 || _bullY != 0))
            lines.Add(ObjectLine("bullseye", "Navaid+Static+Bullseye", "Bullseye", "", _bullX, _bullY, 0));
        foreach (var np in _navPoints)
        {
            var (type, name, coalition) = np.Type switch
            {
                "WP" => ("Navaid+Static+Waypoint", "STPT", ""),
                "MK" => ("Navaid+Static+Waypoint", "MARK", ""),
                "PO" => ("Navaid+Static+Waypoint", "POS", ""),
                "GM" => ("Navaid+Static+Waypoint", "GM", ""),
                "DL" => ("Navaid+Static+Waypoint", "DL", ""),
                "PT" => ("Ground+Static+AntiAircraft", "PPT", "Enemies"),
                _ => (null, null, null)
            };
            if (type is null)
                continue;   // lines and the campaign bullseye (FlightData2 has the bullseye)
            var label = name + " " + np.Index.ToString(CultureInfo.InvariantCulture) + (np.Extra.Length > 0 ? " " + np.Extra : "");
            lines.Add(ObjectLine("np" + np.Index.ToString(CultureInfo.InvariantCulture) + np.Type, type, label, coalition, np.X, np.Y, Math.Abs(np.Z) * 10));
        }

        var text = string.Join('\n', lines);
        if (text == _objects)
            return;
        _objects = text;
        if (lines.Count > 0 || _objects.Length == 0)
            RaiseEvent(TelemetryEventKinds.Objects, text);
    }

    /// <summary>An object at sim x (north ft) / y (east ft) / altitude (ft): u/v exact, lat/lon linearised around the ownship.</summary>
    private string ObjectLine(string id, string type, string name, string? coalition, double x, double y, double altFt)
    {
        double? lat = null, lon = null;
        if (_values.TryGetValue("bms.pos.lat", out var ownLat) && _values.TryGetValue("bms.pos.lon", out var ownLon) && (ownLat != 0 || ownLon != 0))
        {
            var dn = (x - _values["bms.pos.x"]) * TelemetryNav.FeetToMetres;
            var de = (y - _values["bms.pos.y"]) * TelemetryNav.FeetToMetres;
            lat = ownLat + dn / MetresPerDegree;
            lon = ownLon + de / (MetresPerDegree * Math.Max(0.01, Math.Cos(ownLat * Math.PI / 180)));
        }

        return TelemetryEventKinds.ObjectLine(id, type, name, coalition, lat, lon, altFt * TelemetryNav.FeetToMetres,
            u: y * TelemetryNav.FeetToMetres, v: x * TelemetryNav.FeetToMetres);
    }

    /// <summary>"NP:&lt;index&gt;,&lt;type&gt;,&lt;x&gt;,&lt;y&gt;,&lt;z&gt;,&lt;grnd_elev&gt;;" plus optional "O1:...;O2:...;PT:&lt;str_id&gt;,...;".</summary>
    private void ParseNavPoint(string text)
    {
        string? np = null, pt = null;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("NP:", StringComparison.Ordinal))
                np = part[3..];
            else if (part.StartsWith("PT:", StringComparison.Ordinal))
                pt = part[3..];
        }

        if (np is null)
            return;
        var f = np.Split(',');
        if (f.Length < 5 || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return;
        if (!TryNum(f[2], out var x) || !TryNum(f[3], out var y) || !TryNum(f[4], out var z))
            return;
        var extra = pt is null ? "" : pt.Split(',')[0].Trim().Trim('"');
        _navPoints.Add((index, f[1].Trim(), x, y, z, extra));
    }

    private static bool TryNum(string s, out double value) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static string Lines(ReadOnlySpan<byte> fd, string field)
    {
        var start = BmsSharedMemory.FlightDataOffsets[field];
        var lines = new string[5];
        for (var i = 0; i < 5; i++)
            lines[i] = BmsSharedMemory.ReadText(fd, start + i * BmsSharedMemory.DedLineLength, BmsSharedMemory.DedLineLength);
        return string.Join('\n', lines).TrimEnd('\n', ' ');
    }

    private static double Degrees360(double deg)
    {
        deg %= 360;
        return deg < 0 ? deg + 360 : deg;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>A text display: an event when it changes, at most <see cref="TextEventsPerSecond"/> a second (the last
    /// text of a burst is sent once the interval is over).</summary>
    private sealed class TextChannel(string kind)
    {
        private string? _sent;
        private string? _pending;
        private double _lastTime = double.NegativeInfinity;

        public void Reset()
        {
            _sent = null;
            _pending = null;
            _lastTime = double.NegativeInfinity;
        }

        public void Offer(BmsTelemetrySource source, double now, string text)
        {
            if (text == _sent)
            {
                _pending = null;
                return;
            }

            _pending = text;
            Flush(source, now);
        }

        public void Flush(BmsTelemetrySource source, double now)
        {
            if (_pending is null || now - _lastTime < 1 / TextEventsPerSecond)
                return;
            _sent = _pending;
            _pending = null;
            _lastTime = now;
            source.RaiseEvent(kind, _sent);
        }
    }

    private static IReadOnlyList<TelemetryDescriptor> BuildCatalogue()
    {
        const string origin = "FalconSharedMemoryArea";
        var list = new List<TelemetryDescriptor>();
        foreach (var v in BmsSharedMemory.Values)
            list.Add(new TelemetryDescriptor(v.Key, v.Title, v.Unit, v.Min, v.Max, v.Category,
                (v.Area == BmsArea.FlightData ? origin + " FlightData." : origin + "2 FlightData2.") + v.Field + (v.Index > 0 ? "[" + v.Index.ToString(CultureInfo.InvariantCulture) + "]" : "")));
        foreach (var f in BmsSharedMemory.Flags)
            list.Add(new TelemetryDescriptor(f.Key, f.Title, "0/1", 0, 1, f.Category, f.WordKey + " & 0x" + f.Mask.ToString("X", CultureInfo.InvariantCulture)));

        const string rwr = "RWR";
        for (var i = 0; i < 8; i++)
        {
            var p = "bms.rwr." + i.ToString(CultureInfo.InvariantCulture) + ".";
            var n = "RWR contact " + (i + 1).ToString(CultureInfo.InvariantCulture) + " ";
            var idx = "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
            list.Add(new(p + "symbol", n + "symbol", "", 0, 200, rwr, origin + " FlightData.RWRsymbol" + idx + " (slots up to 39 while active)"));
            list.Add(new(p + "bearing", n + "bearing", "rad", -Math.PI, Math.PI, rwr, origin + " FlightData.bearing" + idx));
            list.Add(new(p + "missile_activity", n + "missile activity", "0/1", 0, 1, rwr, origin + " FlightData.missileActivity" + idx));
            list.Add(new(p + "missile_launch", n + "missile launch", "0/1", 0, 1, rwr, origin + " FlightData.missileLaunch" + idx));
            list.Add(new(p + "selected", n + "selected", "0/1", 0, 1, rwr, origin + " FlightData.selected" + idx));
            list.Add(new(p + "lethality", n + "lethality", "", 0, 2, rwr, origin + " FlightData.lethality" + idx));
            list.Add(new(p + "new", n + "new detection", "0/1", 0, 1, rwr, origin + " FlightData.newDetection" + idx));
            list.Add(new(p + "jamming", n + "jamming (0 no, 1 yes, 2 should)", "", 0, 2, rwr, origin + "2 FlightData2.RWRjammingStatus" + idx));
        }

        for (var i = 0; i < 4; i++)
            list.Add(new("bms.mp.pilot." + i.ToString(CultureInfo.InvariantCulture) + ".state", "MP pilot " + (i + 1).ToString(CultureInfo.InvariantCulture) + " state (0 UI, 1 loading, 2 waiting, 3 flying, 4 dead)",
                "", 0, 5, "Session", origin + "2 FlightData2.pilotsStatus[" + i.ToString(CultureInfo.InvariantCulture) + "] (up to 31)"));

        const string d = BmsSharedMemory.CDerived;
        list.AddRange(
        [
            new("bms.calc.heading", "True heading", "deg", 0, 360, d, "yaw, 0..360"),
            new("bms.calc.track", "Ground track", "deg", 0, 360, d, "atan2(yDot, xDot)"),
            new("bms.calc.ground_speed", "Ground speed", "kt", 0, 1500, d, "|xDot, yDot|"),
            new("bms.calc.alt", "Altitude (true, -z)", "ft", 0, 60000, d, "-z"),
            new("bms.calc.vs", "Vertical speed", "ft/min", -30000, 30000, d, "-zDot * 60"),
            new("bms.calc.fuel", "Fuel internal + external", "lbs", 0, 30000, d, "internalFuel + externalFuel"),
            new("bms.calc.agl", "Radar altitude while valid", "ft", 0, 50000, d, "RALT when MiscBits RALT_Valid"),
            new("bms.calc.bullseye_bearing", "Bearing from bullseye", "deg", 0, 360, d, "bullseyeX/Y to x/y"),
            new("bms.calc.bullseye_range", "Range from bullseye", "nm", 0, 500, d, "bullseyeX/Y to x/y"),
        ]);
        return list;
    }
}

/// <summary>Reads Falcon BMS's shared memory areas (read only) on a timer and feeds a <see cref="BmsTelemetrySource"/>.</summary>
/// <remarks>
/// The areas are BMS's official export interface for cockpit builders and tools (Tools\SharedMem\FlightData.h), not
/// the game's process memory. They are opened with read rights only; while BMS has not created them the feed retries
/// every <see cref="RetryInterval"/>. One poll copies FlightData and FlightData2 into reused buffers; the string area
/// is re-read only when FlightData2.StringAreaTime changes. Nothing spins: polls wait on a periodic timer.
/// </remarks>
public sealed class BmsTelemetryFeed : ITelemetryFeed
{
    public const string NotOpenProblem = "BMS has not opened its shared memory (it does so when it starts)";

    private readonly BmsTelemetrySource _source;
    private readonly string _prefix;
    private readonly double _pollHz;
    private readonly object _gate = new();
    private readonly byte[] _fdBuffer = new byte[BmsSharedMemory.FlightDataSize];
    private readonly byte[] _fd2Buffer = new byte[BmsSharedMemory.FlightData2Size];
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private Area? _fd, _fd2;
    private uint? _stringTime;
    private uint _stringSize;
    private double _lastFd2Attempt = double.NegativeInfinity;

    public BmsTelemetryFeed(BmsTelemetrySource source, string? memoryPrefix = null, double pollHz = 20)
    {
        _source = source;
        _prefix = memoryPrefix ?? "";
        _pollHz = Math.Clamp(pollHz, 1, 100);
        Problem = NotOpenProblem;
    }

    public FrameTelemetrySource Source => _source;
    public BmsTelemetrySource BmsSource => _source;
    public string Endpoint => "shared memory " + _prefix + BmsSharedMemory.FlightDataName;
    public string? Problem { get; private set; }

    /// <summary>How often a missing area is looked for again.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Polls done since Start (tests).</summary>
    public long Polls { get; private set; }

    public bool IsOpen => _fd is not null;

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
                return;
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _loop = Task.Run(() => Loop(token));
        }
    }

    public void Stop()
    {
        Task? loop;
        lock (_gate)
        {
            loop = _loop;
            _loop = null;
            _stop?.Cancel();
        }

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        lock (_gate)
        {
            _stop?.Dispose();
            _stop = null;
            CloseAll();
        }

        _source.EndSession("recording stopped");
    }

    public void Dispose() => Stop();

    private async Task Loop(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1 / _pollHz));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (_fd is null && !TryOpenFlightData())
                {
                    Problem = NotOpenProblem;
                    await Task.Delay(RetryInterval, stop).ConfigureAwait(false);
                    continue;
                }

                Problem = null;
                if (_fd2 is null && clock.Elapsed.TotalSeconds - _lastFd2Attempt >= RetryInterval.TotalSeconds)
                {
                    _lastFd2Attempt = clock.Elapsed.TotalSeconds;
                    _fd2 = Area.TryOpen(_prefix + BmsSharedMemory.FlightData2Name);
                }

                Poll();
                if (!await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                lock (_gate)
                    CloseAll();
                _source.EndSession("BMS shared memory closed");
            }
        }
    }

    private bool TryOpenFlightData()
    {
        _fd = Area.TryOpen(_prefix + BmsSharedMemory.FlightDataName);
        if (_fd is null)
            return false;
        _fd2 = Area.TryOpen(_prefix + BmsSharedMemory.FlightData2Name);
        _lastFd2Attempt = double.NegativeInfinity;
        _stringTime = null;
        return true;
    }

    private void Poll()
    {
        Polls++;
        var fdLength = _fd!.Read(_fdBuffer);
        var fd2Length = _fd2?.Read(_fd2Buffer) ?? 0;
        ReadOnlySpan<byte> fd2 = _fd2Buffer.AsSpan(0, fd2Length);
        if (fd2Length >= 1160)
        {
            var version = BinaryPrimitives.ReadInt32LittleEndian(fd2[76..]);
            var time = BinaryPrimitives.ReadUInt32LittleEndian(fd2[BmsSharedMemory.FlightData2Offsets["StringAreaTime"]..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(fd2[BmsSharedMemory.FlightData2Offsets["StringAreaSize"]..]);
            if (version >= 15 && size > 0 && (time != _stringTime || size != _stringSize))
            {
                var area = ReadStrings(size);
                _stringTime = time;   // also when the area is missing: looked for again when BMS changes it
                _stringSize = size;
                if (area is not null)
                {
                    _source.IngestStrings(area);
                    _source.PublishStringsRaw(area);
                }
            }
        }

        _source.Ingest(_fdBuffer.AsSpan(0, fdLength), fd2);
    }

    private byte[]? ReadStrings(uint size)
    {
        using var area = Area.TryOpen(_prefix + BmsSharedMemory.StringDataName);
        if (area is null)
            return null;
        var buffer = new byte[Math.Min(size, (uint)BmsSharedMemory.StringAreaSizeMax)];
        var length = area.Read(buffer);
        return length == buffer.Length ? buffer : buffer[..length];
    }

    private void CloseAll()
    {
        _fd?.Dispose();
        _fd2?.Dispose();
        _fd = null;
        _fd2 = null;
        _stringTime = null;
    }

    /// <summary>One named mapping opened read only.</summary>
    private sealed class Area : IDisposable
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;

        private Area(MemoryMappedFile file, MemoryMappedViewAccessor view)
        {
            _file = file;
            _view = view;
        }

        public static Area? TryOpen(string name)
        {
            if (!OperatingSystem.IsWindows())
                return null;
            MemoryMappedFile? file = null;
            try
            {
                file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                return new Area(file, view);
            }
            catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                file?.Dispose();
                return null;
            }
        }

        public int Read(byte[] buffer)
        {
            var length = (int)Math.Min(buffer.Length, _view.Capacity);
            _view.ReadArray(0, buffer, 0, length);
            return length;
        }

        public void Dispose()
        {
            _view.Dispose();
            _file.Dispose();
        }
    }
}
