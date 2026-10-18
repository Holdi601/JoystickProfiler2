using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Ffb.Core.Telemetry;
using JoyPro.Ffb.Core.Telemetry.Dcs;

namespace JoyPro.Ffb.Core.Tests;

/// <summary>The DCS hook's recording lines (docs/telemetry-recording.md) and the hook itself, run in DCS's own
/// luae.exe (Lua 5.1) with stubbed DCS, Export, net, log and socket tables. The luae tests return early (and say so)
/// when no DCS install is found; set JOYPRO_DCS_LUAE to a luae.exe to point them at one. Nothing is written into the
/// DCS folder: the hook copy, the harness and the output live in a temporary folder (JOYPRO_TEST_WORK_DIR, else %TEMP%).</summary>
public class DcsRecordingTelemetryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // ------------------------------------------------------------------ protocol and source

    [Fact]
    public void FastLine_NewKeysAndNavMappings()
    {
        Assert.True(DcsTelemetryProtocol.TryParseFrame(
            "t=10.000;ias=150;lat=41.6000000;lon=41,6000000;alt_msl=1500.00;heading=1.5;true_pitch=0.05;true_bank=-0.2;pos_x=-300000.00;vx=30;vy=5;vz=160;pitch=0.05;bank=-0.2;yaw=1.5",
            out var frame));
        Assert.Equal(41.6, frame!.Values["dcs.lat"]);
        Assert.Equal(41.6, frame.Values["dcs.lon"]);   // decimal comma
        Assert.Equal(160, frame.Values["dcs.vz"]);
        Assert.Equal(1.5, frame.Values["dcs.yaw"]);

        var source = new DcsTelemetrySource(new ManualClock());
        var nav = source.NavMappings.ToDictionary(m => m.Target);
        foreach (var target in new[] { TelemetryNav.Latitude, TelemetryNav.Longitude, TelemetryNav.Altitude, TelemetryNav.Roll, TelemetryNav.Pitch,
                     TelemetryNav.Yaw, TelemetryNav.Ias, TelemetryNav.Tas, TelemetryNav.Mach, TelemetryNav.Aoa, TelemetryNav.Aos, TelemetryNav.Agl,
                     TelemetryNav.VerticalG, TelemetryNav.LongitudinalG, TelemetryNav.LateralG, TelemetryNav.Gear, TelemetryNav.Flaps,
                     TelemetryNav.Airbrakes, TelemetryNav.FuelKg, TelemetryNav.VerticalSpeed })
            Assert.True(nav.ContainsKey(target), target);
        Assert.Equal("dcs.heading", nav[TelemetryNav.Yaw].Key);
        Assert.Equal(1.5 * 180 / Math.PI, frame.Values[nav[TelemetryNav.Yaw].Key] * nav[TelemetryNav.Yaw].Scale, 9);
        Assert.Equal(-0.2 * 180 / Math.PI, frame.Values[nav[TelemetryNav.Roll].Key] * nav[TelemetryNav.Roll].Scale, 9);
        // Every mapped key is a declared channel.
        Assert.All(source.NavMappings, m => Assert.Contains(DcsTelemetryProtocol.Channels, c => c.Key == m.Key));
    }

    [Fact]
    public void Channels_AreUniqueValidAndCoverEveryKeyTheHookSends()
    {
        var keys = DcsTelemetryProtocol.Channels.Select(c => c.WireKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.Matches("^[a-z0-9_]{1,32}$", k));
        Assert.All(DcsTelemetryProtocol.Channels, c => Assert.Contains(c.RateHz, new[] { 1.0, 10, 100 }));

        var lua = File.ReadAllText(HookPath());
        var sent = Regex.Matches(lua, @"\b(?:add|flag)\('([a-z0-9_]+)'").Select(m => m.Groups[1].Value).ToHashSet();
        foreach (Match m in Regex.Matches(lua, @"\blr\('([a-z0-9_]+)'"))
        {
            sent.Add(m.Groups[1].Value + "_l");
            sent.Add(m.Groups[1].Value + "_r");
        }

        foreach (Match m in Regex.Matches(lua, @"(\w+) = '([a-z0-9_]+)'"))
            if (DcsTelemetryProtocol.McpFlags.Any(f => f.Name == m.Groups[1].Value))
            {
                Assert.Equal(DcsTelemetryProtocol.McpFlags.First(f => f.Name == m.Groups[1].Value).Key, m.Groups[2].Value);
                sent.Add(m.Groups[2].Value);
            }

        Assert.True(sent.Count > 60, "the key scan of the hook found too little: " + sent.Count);
        Assert.All(sent, k => Assert.Contains(k, keys));
        // The keys the force feedback graph reads stay what they were.
        foreach (var old in new[] { "t", "ias", "tas", "mach", "aoa", "aos", "g", "gx", "gz", "vv", "alt", "agl", "wx", "wy", "wz", "rpm_l", "rpm_r",
                     "gear", "flaps", "speedbrake", "shells", "rod_l", "rod_r", "rod_n", "gs", "stall", "fail", "pitch", "bank", "yaw" })
        {
            Assert.Contains(old, sent);
            Assert.Equal(100, DcsTelemetryProtocol.Channels.First(c => c.WireKey == old).RateHz);
        }
    }

    [Fact]
    public void SlowLine_IsAddedToTheFollowingFramesAndExpires()
    {
        var source = new DcsTelemetrySource(new ManualClock());
        source.Ingest("slow t=1.000;fuel_int=2000;chaff=60\nt=1.000;g=1.1");
        Assert.Equal(1, source.FramesAccepted);   // the slow line is no frame of its own
        Assert.True(source.TryGet("dcs.fuel_int", out var fuel));
        Assert.Equal(2000, fuel);
        Assert.Equal(1, source.Latest!.Time);

        source.Ingest("t=3.5;g=1.2");
        Assert.True(source.TryGet("dcs.chaff", out var chaff));
        Assert.Equal(60, chaff);
        source.Ingest("slow t=3.6;chaff=58\nt=3.6;g=1.2");
        Assert.True(source.TryGet("dcs.chaff", out chaff));
        Assert.Equal(58, chaff);
        Assert.True(source.TryGet("dcs.fuel_int", out _));

        source.Ingest("t=4.5;g=1.0");   // fuel_int is 3.5 s old: gone; chaff (0.9 s) stays
        Assert.False(source.TryGet("dcs.fuel_int", out _));
        Assert.True(source.TryGet("dcs.chaff", out _));

        // A value in the frame line wins over a slow one of the same name.
        source.Ingest("slow t=5;g=9\nt=5;g=2");
        Assert.True(source.TryGet("dcs.g", out var g));
        Assert.Equal(2, g);

        // A new mission forgets the slow values.
        source.Ingest("start\nt=0.1;g=1");
        Assert.False(source.TryGet("dcs.chaff", out _));
    }

    [Fact]
    public void InfoPlaneStartAndExit_SetSessionFactsAndRaiseEvents()
    {
        var clock = new ManualClock(5);
        var source = new DcsTelemetrySource(clock);
        var events = new List<TelemetryEvent>();
        source.EventRaised += events.Add;

        source.Ingest("start");
        source.Ingest("info map=Caucasus\ninfo mission=Free flight\ninfo pilot=Holdi\ninfo game_version=2.9.8.1214\ninfo coalition=Blue\ninfo Bad Key=x\ninfo =y");
        source.Ingest("plane F-16C_50\nt=1;g=1");
        source.Ingest("info map=Caucasus\nplane F-16C_50");   // repeats: no new events

        Assert.Equal("Caucasus", source.SessionInfo[TelemetryInfoKeys.Map]);
        Assert.Equal("Free flight", source.SessionInfo[TelemetryInfoKeys.Mission]);
        Assert.Equal("Holdi", source.SessionInfo[TelemetryInfoKeys.Pilot]);
        Assert.Equal("2.9.8.1214", source.SessionInfo[TelemetryInfoKeys.GameVersion]);
        Assert.Equal("F-16C_50", source.SessionInfo[TelemetryInfoKeys.Aircraft]);
        Assert.Equal("Blue", source.SessionInfo["coalition"]);
        Assert.Equal(6, source.SessionInfo.Count);
        Assert.Equal(
            [
                (TelemetryEventKinds.SessionStart, "simulation start"),
                (TelemetryEventKinds.Map, "Caucasus"),
                (TelemetryEventKinds.Info, "mission=Free flight"),
                (TelemetryEventKinds.Info, "pilot=Holdi"),
                (TelemetryEventKinds.Info, "game_version=2.9.8.1214"),
                (TelemetryEventKinds.Info, "coalition=Blue"),
                (TelemetryEventKinds.Aircraft, "F-16C_50"),
            ],
            events.Select(e => (e.Kind, e.Text)).ToArray());
        Assert.All(events, e => Assert.Equal(5, e.Time));

        events.Clear();
        var notices = 0;
        source.Notice += () => notices++;
        source.Ingest("exit");
        Assert.Equal([(TelemetryEventKinds.SessionEnd, "mission ended")], events.Select(e => (e.Kind, e.Text)).ToArray());
        Assert.Empty(source.SessionInfo);
        Assert.Null(source.Aircraft);
        Assert.Null(source.Latest);
        Assert.Equal(1, notices);
    }

    [Fact]
    public void Objects_OneEventPerCompleteSnapshot()
    {
        var source = new DcsTelemetrySource(new ManualClock());
        var snapshots = new List<string>();
        source.EventRaised += e =>
        {
            if (e.Kind == TelemetryEventKinds.Objects)
                snapshots.Add(e.Text);
        };

        source.Ingest("objs 7 1 12.000\nobj 16777472|1.1.1.6|F-15C|1|41.5|41,7|5000.5|0.1|0.05|1.5707963|Dodge 1-1|Dodge 1|0|A\nobj 16777473|2.17.104.55|T-72B|2|41.6|41.8|20||||Armor 1|Armor|2|");
        Assert.Empty(snapshots);
        source.Ingest("objs 7 2 12.000\nobj 16777474|4.4.7.24|R-27ER|1|41.55|41.75|4800|0|0|-1.5707963|||0|\nobj bad\nobjend 7 2 3");
        var lines = Assert.Single(snapshots).Split('\n');
        Assert.Equal(3, lines.Length);   // "obj bad" has too few fields
        Assert.Equal(source.LastObjects, lines);
        Assert.Equal(1, source.ObjectSnapshots);

        var jet = lines[0].Split('|');
        Assert.Equal(12, jet.Length);
        Assert.Equal(["16777472", "Air+FixedWing", "F-15C", "Red", "41.5", "41.7", "5000.5"], jet[..7]);
        Assert.Equal(0.1 * 180 / Math.PI, double.Parse(jet[7], System.Globalization.CultureInfo.InvariantCulture), 5);
        Assert.Equal(90, double.Parse(jet[9], System.Globalization.CultureInfo.InvariantCulture), 5);
        Assert.Equal(["16777473", "Ground+Heavy+Armor+Vehicle+Tank", "T-72B", "Blue", "41.6", "41.8", "20", "", "", "", "", ""], lines[1].Split('|'));
        var missile = lines[2].Split('|');
        Assert.Equal("Weapon+Missile", missile[1]);
        Assert.Equal(270, double.Parse(missile[9], System.Globalization.CultureInfo.InvariantCulture), 5);   // heading 0..360

        // A part lost: no snapshot (an object missing from a snapshot would read as gone).
        source.Ingest("objs 8 1 13.000\nobj 1|1.1.1.6|F-15C|1|41.5|41.7|5000|0|0|0|||0|");
        source.Ingest("objs 8 3 13.000\nobjend 8 3 1");
        Assert.Single(snapshots);
        // A new snapshot before the end of the last one drops the old one; an empty snapshot is a valid one.
        source.Ingest("objs 9 1 14.000\nobj 1|1.1.1.6|F-15C|1|41.5|41.7|5000|0|0|0|||0|");
        source.Ingest("objs 10 1 15.000\nobjend 10 1 0");
        Assert.Equal(2, snapshots.Count);
        Assert.Equal("", snapshots[1]);
        // obj lines outside a snapshot are ignored.
        source.Ingest("obj 1|1.1.1.6|F-15C|1|41.5|41.7|5000|0|0|0|||0|\nobjend 10 1 1");
        Assert.Equal(2, snapshots.Count);
    }

    [Theory]
    [InlineData("1.1.1.6", "", "Air+FixedWing")]
    [InlineData("1.2.4.5", "", "Air+Rotorcraft")]
    [InlineData("1.3.31.0", "", "Misc+Decoy")]
    [InlineData("2.16.27.0", "", "Ground+AntiAircraft")]
    [InlineData("2.8.25.0", "", "Ground+Vehicle")]
    [InlineData("2.8.25.0", "AS", "Ground+Static")]
    [InlineData("2.20.0.0", "", "Ground+Light+Human+Infantry")]
    [InlineData("3.12.12.0", "", "Sea+Watercraft+AircraftCarrier")]
    [InlineData("3.12.14.0", "", "Sea+Watercraft+Warship")]
    [InlineData("3.12.15.0", "", "Sea+Watercraft")]
    [InlineData("4.5.9.0", "", "Weapon+Bomb")]
    [InlineData("4.6.10.0", "", "Projectile+Shell")]
    [InlineData("4.7.33.0", "", "Weapon+Rocket")]
    [InlineData("5.13.20.0", "", "Ground+Static+Aerodrome")]
    [InlineData("5.18.0.0", "", "Ground+Static+Building")]
    [InlineData("", "", null)]
    public void ObjectTypes_InTacviewWords(string levels, string flags, string? expected) =>
        Assert.Equal(expected, DcsObjectTypes.ToTacview(levels, flags));

    [Fact]
    public void GameEventsAndChat_BecomeEvents()
    {
        var source = new DcsTelemetrySource(new ManualClock());
        var events = new List<TelemetryEvent>();
        source.EventRaised += events.Add;
        source.Ingest("ev takeoff 1|16777472|Batumi\nev pause\nev mode NAV/ROUTE OFF\nmsg Holdi|hello there\nev Bad-Name x\nwhatever line");
        Assert.Equal(
            [
                ("dcs.takeoff", "1|16777472|Batumi"),
                ("dcs.pause", ""),
                ("dcs.mode", "NAV/ROUTE OFF"),
                (TelemetryEventKinds.Message, "Holdi: hello there"),
            ],
            events.Select(e => (e.Kind, e.Text)).ToArray());
        Assert.Equal(0, source.FramesAccepted);
    }

    [Fact]
    public void Raw_OnlyWithAListener_EveryDatagram()
    {
        var clock = new ManualClock(2);
        var source = new DcsTelemetrySource(clock);
        Assert.False(source.WantsRaw);
        source.Ingest("t=1;g=1"u8);

        var raw = new List<TelemetryRawPacket>();
        void Listener(TelemetryRawPacket p) => raw.Add(p);
        source.RawReceived += Listener;
        Assert.True(source.WantsRaw);
        source.Ingest("t=1.01;g=1.2\nslow t=1.01;chaff=3"u8);
        source.Ingest("ev pause");
        source.Ingest("objs 1 1 1.000\nobjend 1 1 0"u8);
        source.Ingest("slow t=1.02;chaff=3\nt=1.02;g=1.2"u8);
        var packet = raw[0];
        Assert.Equal(4, raw.Count);
        Assert.Equal(DcsTelemetrySource.RawFrames, packet.Stream);
        Assert.Equal(2, packet.Time);
        Assert.Equal("t=1.01;g=1.2\nslow t=1.01;chaff=3", Encoding.UTF8.GetString(packet.Data));
        Assert.Equal("ev pause", Encoding.UTF8.GetString(raw[1].Data));
        Assert.Equal(
            [DcsTelemetrySource.RawFrames, DcsTelemetrySource.RawNotices, DcsTelemetrySource.RawObjects, DcsTelemetrySource.RawFrames],
            raw.Select(p => p.Stream).ToArray());

        source.RawReceived -= Listener;
        source.Ingest("t=1.02;g=1"u8);
        Assert.Equal(4, raw.Count);
    }

    [Fact]
    public void OldKeys_StillFeedTheForceFeedbackModel()
    {
        var source = new DcsTelemetrySource(new ManualClock()) { Enricher = AircraftTelemetryDeriver.ForDcs() };
        source.Ingest("slow t=1;fuel=4200;chaff=60\nt=1.000;ias=150;tas=170;mach=0.5;aoa=0.08;aos=0.01;vv=5;agl=1200;alt=1500;g=3.4;gx=0.1;gz=0.02;wx=0.3;wy=0.01;wz=0.05;gs=162.8;vx=30;vy=5;vz=160;gear=0;rod_l=0;rod_r=0;rod_n=0;flaps=0.5;speedbrake=0.25;shells=510;stall=0;fail=1;rpm_l=92;rpm_r=0;pitch=0.05;bank=-0.2;yaw=1.5;lat=41.6;lon=41.6;alt_msl=1500;heading=1.5");
        Assert.True(source.TryGet(AircraftTelemetry.Ias, out var ias));
        Assert.Equal(150, ias);
        Assert.True(source.TryGet(AircraftTelemetry.Aoa, out var aoa));
        Assert.Equal(0.08 * 180 / Math.PI, aoa, 9);
        Assert.True(source.TryGet(AircraftTelemetry.G, out var g));
        Assert.Equal(3.4, g);
        Assert.True(source.TryGet(AircraftTelemetry.Rpm, out var rpm));
        Assert.Equal(0.92, rpm, 9);
        Assert.True(source.TryGet(AircraftTelemetry.OnGround, out var onGround));
        Assert.Equal(0, onGround);
        Assert.True(source.TryGet(AircraftTelemetry.SpeedBrake, out var brake));
        Assert.Equal(0.25, brake);
        Assert.True(source.TryGet(AircraftTelemetry.Ammo, out var ammo));
        Assert.Equal(510, ammo);
        Assert.True(source.TryGet(AircraftTelemetry.Damage, out var damage));
        Assert.Equal(1, damage);
    }

    // ------------------------------------------------------------------ the hook in DCS's luae.exe

    [Fact]
    public void Hook_RunsInLuae_WithTheFullApi()
    {
        var run = RunHook("full");
        if (run is null)
            return;
        Assert.Empty(run.Logs);
        Assert.All(run.Datagrams, d =>
        {
            Assert.Equal("127.0.0.1", d.Host);
            Assert.Equal(DcsTelemetryProtocol.Port, d.Port);
            Assert.True(d.Data.Length < 8192, "datagram of " + d.Data.Length + " bytes");
        });

        var clock = new ManualClock();
        var source = new DcsTelemetrySource(clock);
        var events = new List<TelemetryEvent>();
        var raw = 0;
        source.EventRaised += events.Add;
        source.RawReceived += _ => raw++;
        IReadOnlyDictionary<string, string>? infoAtEnd = null;
        TelemetryFrame? lastFrame = null;
        foreach (var d in run.Datagrams)
        {
            if (Encoding.UTF8.GetString(d.Data) == "exit")
            {
                infoAtEnd = source.SessionInfo;
                lastFrame = source.Latest;
            }

            clock.Advance(0.01);
            source.Ingest(d.Data);
        }

        Assert.Equal(run.Datagrams.Count, raw);
        Assert.NotNull(infoAtEnd);
        Assert.Equal("Caucasus", infoAtEnd![TelemetryInfoKeys.Map]);
        Assert.Equal("Test mission", infoAtEnd[TelemetryInfoKeys.Mission]);
        Assert.Equal("Holdi", infoAtEnd[TelemetryInfoKeys.Pilot]);
        Assert.Equal("2.9.8.1214", infoAtEnd[TelemetryInfoKeys.GameVersion]);
        Assert.Equal("F-16C_50", infoAtEnd[TelemetryInfoKeys.Aircraft]);
        Assert.Equal("2016-06-21", infoAtEnd["mission_date"]);
        Assert.Equal("43200", infoAtEnd["mission_time"]);
        Assert.Equal("Enfield 1-1", infoAtEnd["unit"]);
        Assert.Equal("Enfield 1", infoAtEnd["group"]);
        Assert.Equal("Blue", infoAtEnd["coalition"]);
        Assert.Equal("0", infoAtEnd["multiplayer"]);
        Assert.Equal("on", infoAtEnd["objects"]);
        Assert.Equal("on", infoAtEnd["sensors"]);

        var v = lastFrame!.Values;
        void Is(string key, double expected) => Assert.True(v.TryGetValue("dcs." + key, out var got) && Math.Abs(got - expected) <= 1e-3 + 1e-4 * Math.Abs(expected),   // %.5g
            key + " = " + (v.TryGetValue("dcs." + key, out var x) ? x.ToString(System.Globalization.CultureInfo.InvariantCulture) : "missing"));
        Is("ias", 150); Is("tas", 170); Is("mach", 0.5); Is("aoa", 0.08); Is("aos", 0.01); Is("vv", 5); Is("agl", 1200); Is("alt", 1500);
        Is("g", 3.4); Is("gx", 0.1); Is("gz", 0.02); Is("wx", 0.3); Is("wy", 0.01); Is("wz", 0.05); Is("gs", Math.Sqrt(30 * 30 + 160 * 160));
        Is("gear", 0); Is("rod_l", 0); Is("rod_r", 0); Is("rod_n", 0); Is("flaps", 0.5); Is("speedbrake", 0.25); Is("shells", 510);
        Is("stall", 0); Is("fail", 1); Is("rpm_l", 92); Is("rpm_r", 0);
        Is("pitch", 0.05); Is("bank", -0.2); Is("yaw", 1.5);
        Is("lat", 41.6); Is("lon", 41.65); Is("alt_msl", 1500); Is("heading", 1.5); Is("true_pitch", 0.05); Is("true_bank", -0.2);
        Is("pos_x", -300000); Is("pos_y", 1500); Is("pos_z", 600000); Is("vx", 30); Is("vy", 5); Is("vz", 160);
        Is("coalition", 2); Is("country", 2); Is("temp_l", 650); Is("fuel_flow_l", 0.8); Is("hyd_l", 210);
        Is("fuel_int", 3000); Is("fuel_ext", 1200); Is("fuel", 4200);
        Is("elev_l", 0.1); Is("ail_l", -0.2); Is("ail_r", 0.2); Is("rud_r", 0); Is("wheelbrake", 0); Is("canopy", 0); Is("hook", 0);
        Is("station", 3); Is("station_count", 2); Is("stations", 4); Is("stores", 4); Is("chaff", 60); Is("flare", 30);
        Is("fail_eng_l", 0); Is("master_warn", 1); Is("mcp_somethingnewfailure", 1); Is("fuel_tank_dmg", 0);
        Is("ralt", 1190); Is("mag_heading", 1.45); Is("slip_ball", 0.01); Is("baro", 720); Is("time_accel", 1); Is("in_air", 1);
        Is("wind_x", 2); Is("wind_z", -3); Is("req_speed", 200); Is("req_alt", 1500); Is("autothrust", 0);
        Is("targets", 2); Is("locked", 1); Is("lock_id", 5001); Is("lock_dist", 18000); Is("lock_vc", 250);
        Is("wpt", 2); Is("wpts", 3); Is("wpt_dist", 10000); Is("wingmen", 1);
        Assert.All(v.Keys.Where(k => !k.StartsWith("dcs.mcp_", StringComparison.Ordinal)),
            k => Assert.Contains(DcsTelemetryProtocol.Channels, c => c.Key == k));
        Assert.True(source.FramesAccepted > 500, "frames: " + source.FramesAccepted);

        Assert.Equal(TelemetryEventKinds.SessionStart, events[0].Kind);
        Assert.Equal(TelemetryEventKinds.SessionEnd, events[^1].Kind);
        var objects = events.Where(e => e.Kind == TelemetryEventKinds.Objects).ToList();
        Assert.True(objects.Count >= 9, "object snapshots: " + objects.Count);   // ~11 s of model time, one a second
        foreach (var snapshot in objects)
        {
            var lines = snapshot.Text.Split('\n');
            Assert.Equal(302, lines.Length);   // the player's own aircraft is left out
            Assert.All(lines, l => Assert.Equal(12, l.Split('|').Length));
            Assert.DoesNotContain(lines, l => l.StartsWith("1000|", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("3000|Air+FixedWing|F-15C|Red|42.1|42.2|6000|", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("3001|Weapon+Missile|R-27ER|Red|", StringComparison.Ordinal));
            Assert.Equal(300, lines.Count(l => l.Split('|')[1] == "Ground+Heavy+Armor+Vehicle+Tank"));
        }

        Assert.Contains(events, e => e.Kind == "dcs.pause");
        Assert.Contains(events, e => e.Kind == "dcs.resume");
        Assert.Contains(events, e => e.Kind == "dcs.takeoff" && e.Text == "1|1000|Batumi");
        Assert.Contains(events, e => e.Kind == "dcs.mode" && e.Text == "NAV/ROUTE OFF");
        Assert.Contains(events, e => e.Kind == TelemetryEventKinds.Message && e.Text == "Holdi: hello/there");
        Assert.Null(source.Latest);
        Assert.Empty(source.SessionInfo);
        output.WriteLine(run.Datagrams.Count + " datagrams, " + source.FramesAccepted + " frames, " + objects.Count + " object snapshots, " +
                         run.Datagrams.Max(d => d.Data.Length) + " bytes max");
        output.WriteLine(run.Datagrams.Select(d => Encoding.UTF8.GetString(d.Data)).Last(t => t.StartsWith("slow ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Hook_RunsInLuae_CapsAndSplitsManyObjects()
    {
        var run = RunHook("many");
        if (run is null)
            return;
        Assert.Empty(run.Logs);
        Assert.All(run.Datagrams, d => Assert.True(d.Data.Length < 8192, "datagram of " + d.Data.Length + " bytes"));
        var source = new DcsTelemetrySource(new ManualClock());
        var snapshots = new List<string>();
        source.EventRaised += e =>
        {
            if (e.Kind == TelemetryEventKinds.Objects)
                snapshots.Add(e.Text);
        };
        foreach (var d in run.Datagrams)
            source.Ingest(d.Data);
        Assert.NotEmpty(snapshots);
        Assert.All(snapshots, s => Assert.Equal(600, s.Split('\n').Length));
        var parts = run.Datagrams.Select(d => Encoding.UTF8.GetString(d.Data)).Count(t => t.StartsWith("objs 1 ", StringComparison.Ordinal));
        Assert.True(parts >= 8, "parts of the first snapshot: " + parts);
        Assert.True(source.FramesAccepted > 0);
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("throwing")]
    public void Hook_RunsInLuae_WithMissingOrFailingFunctions(string scenario)
    {
        var run = RunHook(scenario);
        if (run is null)
            return;
        Assert.Empty(run.Logs);
        var source = new DcsTelemetrySource(new ManualClock());
        var events = new List<TelemetryEvent>();
        source.EventRaised += events.Add;
        TelemetryFrame? last = null;
        foreach (var d in run.Datagrams)
        {
            source.Ingest(d.Data);
            last = source.Latest ?? last;
        }

        Assert.True(source.FramesAccepted > 500, "frames: " + source.FramesAccepted);
        Assert.Equal(41.6, last!.Values["dcs.lat"], 6);
        Assert.False(last.Values.ContainsKey("dcs.ias"));
        Assert.Equal(TelemetryEventKinds.SessionStart, events[0].Kind);
        Assert.Equal(TelemetryEventKinds.SessionEnd, events[^1].Kind);
        Assert.Contains(events, e => e.Kind == TelemetryEventKinds.Aircraft && e.Text == "F-16C_50");
    }

    [Fact]
    public void Hook_RunsInLuae_WhenTheServerForbidsExport()
    {
        var run = RunHook("noexport");
        if (run is null)
            return;
        Assert.Empty(run.Logs);
        var texts = run.Datagrams.Select(d => Encoding.UTF8.GetString(d.Data)).ToList();
        Assert.Contains("noexport", texts);
        Assert.DoesNotContain(texts, t => t.StartsWith("t=", StringComparison.Ordinal) || t.Contains("\nt=") || t.StartsWith("objs", StringComparison.Ordinal));
        var source = new DcsTelemetrySource(new ManualClock());
        IReadOnlyDictionary<string, string>? info = null;
        foreach (var d in run.Datagrams)
        {
            if (Encoding.UTF8.GetString(d.Data) == "exit")
                info = source.SessionInfo;
            source.Ingest(d.Data);
        }

        Assert.True(source.ExportNotAllowed || info is not null);
        Assert.Equal(0, source.FramesAccepted);
        Assert.Equal("off", info!["objects"]);
        Assert.Equal("Caucasus", info[TelemetryInfoKeys.Map]);
    }

    // ------------------------------------------------------------------ luae harness

    private sealed record Datagram(string Host, int Port, byte[] Data);

    private sealed record HookRun(List<Datagram> Datagrams, List<string> Logs);

    // JOYPRO_DCS_HOOK_FILE: test a hook the engine wrote ("engine ffb telemetry setup dcs --path <copy>") instead of the template.
    private static string HookPath() =>
        Environment.GetEnvironmentVariable("JOYPRO_DCS_HOOK_FILE") is { Length: > 0 } file
            ? file
            : Path.Combine(AppContext.BaseDirectory, "DcsHooks", "JoyProFfb.lua");

    private static string? FindLuae()
    {
        var env = Environment.GetEnvironmentVariable("JOYPRO_DCS_LUAE");
        if (!string.IsNullOrWhiteSpace(env))
            return File.Exists(env) ? env : null;
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var key in new[] { @"Software\Eagle Dynamics\DCS World", @"Software\Eagle Dynamics\DCS World OpenBeta" })
            {
                using var reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key);
                if (reg?.GetValue("Path") is string path)
                    roots.Add(path);
            }
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            var r = drive.RootDirectory.FullName;
            roots.Add(Path.Combine(r, "Program Files", "Eagle Dynamics", "DCS World"));
            roots.Add(Path.Combine(r, "Program Files", "Eagle Dynamics", "DCS World OpenBeta"));
            roots.Add(Path.Combine(r, "DCS World"));
            roots.Add(Path.Combine(r, "Program Files (x86)", "Steam", "steamapps", "common", "DCSWorld"));
            roots.Add(Path.Combine(r, "SteamLibrary", "steamapps", "common", "DCSWorld"));
        }

        return roots.Select(root => Path.Combine(root, "bin", "luae.exe")).FirstOrDefault(File.Exists);
    }

    // Runs a copy of the hook in luae.exe with the stubs of one scenario; null (and a note) without DCS.
    private HookRun? RunHook(string scenario)
    {
        var luae = FindLuae();
        if (luae is null)
        {
            output.WriteLine("Skipped: DCS's luae.exe was not found (set JOYPRO_DCS_LUAE).");
            return null;
        }

        var baseDir = Environment.GetEnvironmentVariable("JOYPRO_TEST_WORK_DIR");
        var work = Path.Combine(string.IsNullOrWhiteSpace(baseDir) ? Path.GetTempPath() : baseDir, "dcs-hook-" + scenario + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var hook = Path.Combine(work, "JoyProFfb.lua");
            File.Copy(HookPath(), hook);
            var outFile = Path.Combine(work, "sent.bin");
            var harness = Path.Combine(work, "harness.lua");
            File.WriteAllText(harness, Harness
                .Replace("@HOOK@", hook.Replace('\\', '/'))
                .Replace("@OUT@", outFile.Replace('\\', '/'))
                .Replace("@SCENARIO@", scenario), new UTF8Encoding(false));
            var start = new ProcessStartInfo(luae, "\"" + harness + "\"")
            {
                WorkingDirectory = work,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(60000))
            {
                process.Kill();
                Assert.Fail("luae.exe did not finish");
            }

            output.WriteLine(luae + " " + harness + " -> exit " + process.ExitCode + ": " + stdout.Trim() + " " + stderr.Result.Trim());
            Assert.True(process.ExitCode == 0, "luae.exe failed: " + stdout + stderr.Result);
            Assert.Contains("HARNESS_OK", stdout);
            return Read(File.ReadAllBytes(outFile));
        }
        finally
        {
            try
            {
                Directory.Delete(work, true);
            }
            catch (IOException)
            {
            }
        }
    }

    // Records: "D <port> <host> <length>\n<bytes>\n" (a datagram) or "L <length>\n<bytes>\n" (a log message).
    private static HookRun Read(byte[] data)
    {
        var datagrams = new List<Datagram>();
        var logs = new List<string>();
        var i = 0;
        while (i < data.Length)
        {
            var nl = Array.IndexOf(data, (byte)'\n', i);
            var head = Encoding.ASCII.GetString(data, i, nl - i).Split(' ');
            var length = int.Parse(head[^1], System.Globalization.CultureInfo.InvariantCulture);
            var body = data.AsSpan(nl + 1, length).ToArray();
            i = nl + 1 + length + 1;
            if (head[0] == "D")
                datagrams.Add(new Datagram(head[2], int.Parse(head[1], System.Globalization.CultureInfo.InvariantCulture), body));
            else
                logs.Add(Encoding.UTF8.GetString(body));
        }

        return new HookRun(datagrams, logs);
    }

    private const string Harness = """
        local HOOK, OUT, SCENARIO = [[@HOOK@]], [[@OUT@]], '@SCENARIO@'
        local out = assert(io.open(OUT, 'wb'))
        local function record(head, text) out:write(head .. ' ' .. #text .. '\n' .. text .. '\n') end
        package.loaded.socket = { udp = function()
            return {
                settimeout = function() end,
                sendto = function(self, text, host, port) record('D ' .. tostring(port) .. ' ' .. tostring(host), text); return #text end
            }
        end }
        log = { INFO = 1, WARNING = 2, ERROR = 3, write = function(subsystem, level, message) record('L', tostring(message)) end }
        local t, cb = 0, nil
        local throwing = SCENARIO == 'throwing'
        local function boom() error('boom') end
        DCS = {
            getModelTime = function() return t end,
            setUserCallbacks = function(c) cb = c end,
            getMissionName = throwing and boom or function() return 'Test mission' end,
            getCurrentMission = throwing and boom or function() return { mission = { theatre = 'Caucasus', date = { Day = 21, Month = 6, Year = 2016 }, start_time = 43200 } } end,
            isMultiplayer = throwing and boom or function() return false end,
        }
        net = { get_my_player_id = function() return 1 end, get_player_info = function() return 'Holdi' end, get_name = function() return 'Holdi' end }
        local function v3(x, y, z) return { x = x, y = y, z = z } end
        local own = {
            Name = 'F-16C_50', UnitName = 'Enfield 1-1', GroupName = 'Enfield 1', CoalitionID = 2, Coalition = 'Enemies', Country = 2,
            LatLongAlt = { Lat = 41.6, Long = 41.65, Alt = 1500 }, Heading = 1.5, Pitch = 0.05, Bank = -0.2,
            Position = v3(-300000, 1500, 600000), Type = { level1 = 1, level2 = 1, level3 = 1, level4 = 6 },
        }
        local world = { [1000] = own }
        local tanks = SCENARIO == 'many' and 800 or 300
        for i = 1, tanks do
            world[2000 + i] = { Name = 'T-72B', Type = { level1 = 2, level2 = 17, level3 = 104, level4 = 55 }, CoalitionID = 1, Country = 0,
                LatLongAlt = { Lat = 41.5 + i * 0.0001, Long = 41.7, Alt = 20 }, Heading = 3.0, Pitch = 0, Bank = 0,
                UnitName = 'Armor|' .. i, GroupName = 'Armor\ngroup', Flags = { Born = true, AI_ON = true } }
        end
        world[3000] = { Name = 'F-15C', Type = { level1 = 1, level2 = 1, level3 = 1, level4 = 6 }, CoalitionID = 1, Country = 0,
            LatLongAlt = { Lat = 42.1, Long = 42.2, Alt = 6000 }, Heading = 0.5, Pitch = 0.1, Bank = 0.2, UnitName = 'Dodge 1-1', GroupName = 'Dodge',
            Flags = { Human = true, RadarActive = true } }
        world[3001] = { Name = 'R-27ER', Type = { level1 = 4, level2 = 4, level3 = 7, level4 = 24 }, CoalitionID = 1, Country = 0,
            LatLongAlt = { Lat = 42.0, Long = 42.1, Alt = 5800 }, Heading = 2.5, Pitch = 0, Bank = 0 }
        local full = {
            LoIsOwnshipExportAllowed = function() return true end,
            LoIsObjectExportAllowed = function() return true end,
            LoIsSensorExportAllowed = function() return true end,
            LoGetSelfData = function() return own end,
            LoGetPlayerPlaneId = function() return 1000 end,
            LoGetIndicatedAirSpeed = function() return 150 end,
            LoGetTrueAirSpeed = function() return 170 end,
            LoGetMachNumber = function() return 0.5 end,
            LoGetAngleOfAttack = function() return 0.08 end,
            LoGetAngleOfSideSlip = function() return 0.01 end,
            LoGetVerticalVelocity = function() return 5 end,
            LoGetAltitudeAboveGroundLevel = function() return 1200 end,
            LoGetAltitudeAboveSeaLevel = function() return 1500 end,
            LoGetAccelerationUnits = function() return v3(0.1, 3.4, 0.02) end,
            LoGetAngularVelocity = function() return v3(0.3, 0.01, 0.05) end,
            LoGetVectorVelocity = function() return v3(30, 5, 160) end,
            LoGetMechInfo = function() return {
                gear = { status = 0, value = 0, main = { left = { rod = 0 }, right = { rod = 0 }, nose = { rod = 0 } } },
                flaps = { status = 0, value = 0.5 }, speedbrakes = { status = 0, value = 0.25 }, wheelbrakes = { value = 0 },
                canopy = { value = 0 }, refuelingboom = { value = 0 }, parachute = { value = 0 }, hook = { value = 0 }, wing = { value = 0 },
                airintake = { value = 0 }, noseflap = { value = 0 },
                controlsurfaces = { elevator = { left = 0.1, right = 0.1 }, eleron = { left = -0.2, right = 0.2 }, rudder = { left = 0, right = 0 } } } end,
            LoGetPayloadInfo = function() return { CurrentStation = 3, Cannon = { shells = 510 },
                Stations = { { container = false, weapon = { level1 = 4, level2 = 4, level3 = 7, level4 = 1 }, count = 1 }, { count = 1 }, { count = 2 }, { count = 0 } } } end,
            LoGetMCPState = function() return { StallSignalization = false, LeftEngineFailure = false, MasterWarning = true, SomethingNewFailure = true, FuelTankDamage = false } end,
            LoGetEngineInfo = function() return { RPM = { left = 92, right = 0 }, Temperature = { left = 650, right = 0 }, FuelConsumption = { left = 0.8, right = 0 },
                HydraulicPressure = { left = 210, right = 0 }, fuel_internal = 3000, fuel_external = 1200 } end,
            LoGetADIPitchBankYaw = function() return 0.05, -0.2, 1.5 end,
            LoGetSnares = function() return { chaff = 60, flare = 30 } end,
            LoGetRadarAltimeter = function() return 1190 end,
            LoGetMagneticYaw = function() return 1.45 end,
            LoGetGlideDeviation = function() return 0 end,
            LoGetSideDeviation = function() return 0 end,
            LoGetSlipBallPosition = function() return 0.01 end,
            LoGetBasicAtmospherePressure = function() return 720 end,
            LoGetShakeAmplitude = function() return 0 end,
            LoGetModelTimeAcceleration = function() return 1 end,
            LoGetInAir = function() return true end,
            LoGetVectorWindVelocity = function() return v3(2, 0, -3) end,
            LoGetNavigationInfo = function() return { SystemMode = { master = 'NAV', submode = 'ROUTE' },
                Requirements = { roll = 0, pitch = 0, speed = 200, vertical_speed = 0, altitude = 1500 }, ACS = { mode = 'OFF', autothrust = false } } end,
            LoGetTargetInformation = function() return { { ID = 5001 }, { ID = 5002 } } end,
            LoGetLockedTargetInformation = function() return { { ID = 5001, distance = 18000, convergence_velocity = 250, mach = 0.9, delta_psi = 0.3 } } end,
            LoGetRoute = function() return { goto_point = { this_point_num = 2, world_point = v3(-290000, 0, 600000) }, route = { {}, {}, {} } } end,
            LoGetWingInfo = function() return { { wingmen_id = 1001 } } end,
            LoGetPilotName = function() return 'Holdi' end,
            LoGetVersionInfo = function() return { ProductVersion = { 2, 9, 8, 1214 } } end,
            LoGetWorldObjects = function() return world end,
        }
        if SCENARIO == 'minimal' then
            Export = { LoGetSelfData = full.LoGetSelfData }
        elseif SCENARIO == 'throwing' then
            Export = {}
            for name in pairs(full) do Export[name] = boom end
            Export.LoGetSelfData = full.LoGetSelfData
        elseif SCENARIO == 'noexport' then
            Export = {}
            for name, f in pairs(full) do Export[name] = f end
            Export.LoIsOwnshipExportAllowed = function() return false end
            Export.LoIsObjectExportAllowed = function() return false end
        else
            Export = full
        end
        dofile(HOOK)
        assert(cb, 'the hook registered no callbacks')
        cb.onSimulationStart()
        for i = 1, 700 do
            t = i / 64
            cb.onSimulationFrame()
            cb.onSimulationFrame()   -- a second frame at the same model time sends nothing
        end
        cb.onSimulationPause()
        cb.onSimulationResume()
        cb.onGameEvent('takeoff', 1, 1000, 'Batumi')
        cb.onChatMessage('hello|there', 1)
        cb.onSimulationStop()
        out:close()
        print('HARNESS_OK')
        """;
}
