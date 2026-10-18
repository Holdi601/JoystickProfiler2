using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Ffb.Core.Telemetry;
using JoyPro.Ffb.Core.Telemetry.Bms;

namespace JoyPro.Ffb.Core.Tests;

public class BmsTelemetryTests
{
    // ------------------------------------------------------------------ fixtures

    private sealed class Fixture
    {
        public readonly byte[] Fd = new byte[BmsSharedMemory.FlightDataSize];
        public readonly byte[] Fd2 = new byte[BmsSharedMemory.FlightData2Size];

        public Fixture(bool flying = true, int version2 = BmsSharedMemory.FlightData2Version)
        {
            I1("VersionNum", BmsSharedMemory.FlightDataVersion);
            I2("VersionNum", version2);
            Flying = flying;
        }

        public bool Flying
        {
            set
            {
                var o = BmsSharedMemory.FlightDataOffsets["hsiBits"];
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(Fd.AsSpan(o));
                BinaryPrimitives.WriteUInt32LittleEndian(Fd.AsSpan(o), value ? bits | BmsSharedMemory.HsiFlying : bits & ~BmsSharedMemory.HsiFlying);
            }
        }

        public Fixture F1(string field, float v, int index = 0) { BinaryPrimitives.WriteSingleLittleEndian(Fd.AsSpan(BmsSharedMemory.FlightDataOffsets[field] + 4 * index), v); return this; }
        public Fixture I1(string field, int v, int index = 0) { BinaryPrimitives.WriteInt32LittleEndian(Fd.AsSpan(BmsSharedMemory.FlightDataOffsets[field] + 4 * index), v); return this; }
        public Fixture U1(string field, uint v) { BinaryPrimitives.WriteUInt32LittleEndian(Fd.AsSpan(BmsSharedMemory.FlightDataOffsets[field]), v); return this; }
        public Fixture F2(string field, float v) { BinaryPrimitives.WriteSingleLittleEndian(Fd2.AsSpan(BmsSharedMemory.FlightData2Offsets[field]), v); return this; }
        public Fixture I2(string field, int v) { BinaryPrimitives.WriteInt32LittleEndian(Fd2.AsSpan(BmsSharedMemory.FlightData2Offsets[field]), v); return this; }
        public Fixture S2(string field, short v) { BinaryPrimitives.WriteInt16LittleEndian(Fd2.AsSpan(BmsSharedMemory.FlightData2Offsets[field]), v); return this; }
        public Fixture B2(string field, byte v, int index = 0) { Fd2[BmsSharedMemory.FlightData2Offsets[field] + index] = v; return this; }

        public Fixture Text1(string field, int line, string text, int length = BmsSharedMemory.DedLineLength)
        {
            var o = BmsSharedMemory.FlightDataOffsets[field] + line * length;
            Fd.AsSpan(o, length).Clear();
            Encoding.Latin1.GetBytes(text).CopyTo(Fd.AsSpan(o));
            return this;
        }

        public Fixture Callsign(int index, string name, byte state)
        {
            var o = BmsSharedMemory.FlightData2Offsets["pilotsCallsign"] + index * BmsSharedMemory.CallsignLength;
            Fd2.AsSpan(o, BmsSharedMemory.CallsignLength).Clear();
            Encoding.Latin1.GetBytes(name).CopyTo(Fd2.AsSpan(o));
            Fd2[BmsSharedMemory.FlightData2Offsets["pilotsStatus"] + index] = state;
            return this;
        }

        /// <summary>A typical F-16 in flight over Korea.</summary>
        public static Fixture Flight()
        {
            var f = new Fixture()
                .F1("x", 1_500_000).F1("y", 2_000_000).F1("z", -10_000)
                .F1("xDot", 600).F1("yDot", 0).F1("zDot", -10)
                .F1("alpha", 4.5f).F1("beta", -0.5f).F1("pitch", 0.1f).F1("roll", -0.5f).F1("yaw", (float)(-Math.PI / 2))
                .F1("mach", 0.8f).F1("kias", 400).F1("vt", 700).F1("gs", 1.2f)
                .F1("internalFuel", 5000).F1("externalFuel", 2000).F1("fuelFlow", 9000).F1("rpm", 92).F1("ftit", 700)
                .F1("gearPos", 0).F1("speedBrake", 0.25f).F1("ChaffCount", 60).F1("FlareCount", 30)
                .F1("TrimPitch", 0.125f)
                .U1("lightBits", 0x1 | 0x40000000)            // master caution + autopilot
                .U1("lightBits3", 0x00010000)                 // nose gear down and locked
                .F2("rpm2", 0).F2("latitude", 37.5f).F2("longitude", 127.25f)
                .I2("BMSVersionMajor", 4).I2("BMSVersionMinor", 38).I2("BMSVersionMicro", 1).I2("BMSBuildNumber", 12345)
                .F2("RALT", 9500).I2("miscBits", 1)
                .F2("bullseyeX", 1_400_000).F2("bullseyeY", 2_000_000)
                .S2("vehicleACD", 31)
                .F2("gsMax", 5.5f);
            f.Flying = true;
            return f;
        }
    }

    private static byte[] StringArea(params (uint Id, string Text)[] strings)
    {
        var data = new MemoryStream();
        Span<byte> head = stackalloc byte[8];
        foreach (var (id, text) in strings)
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            BinaryPrimitives.WriteUInt32LittleEndian(head, id);
            BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)bytes.Length);
            data.Write(head);
            data.Write(bytes);
            data.WriteByte(0);
        }

        var result = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 5);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)strings.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)data.Length);
        data.ToArray().CopyTo(result, 12);
        return result;
    }

    private sealed class Recorder
    {
        public readonly ConcurrentQueue<TelemetryEvent> Events = new();
        public readonly ConcurrentQueue<TelemetryRawPacket> Raw = new();
        public readonly ConcurrentQueue<TelemetryFrame> Frames = new();

        public Recorder(FrameTelemetrySource source, bool raw)
        {
            source.EventRaised += Events.Enqueue;
            source.FrameReceived += Frames.Enqueue;
            if (raw)
                source.RawReceived += Raw.Enqueue;
        }

        public TelemetryEvent[] Of(string kind) => Events.Where(e => e.Kind == kind).ToArray();
    }

    private static double Nav(BmsTelemetrySource source, TelemetryFrame frame, string target)
    {
        var m = source.NavMappings.Single(n => n.Target == target);
        Assert.True(frame.TryGet(m.Key, out var raw), "missing " + m.Key);
        return raw * m.Scale + m.Offset;
    }

    // ------------------------------------------------------------------ source

    [Fact]
    public void Layout_constants_match_the_known_values()
    {
        Assert.Equal(220, BmsSharedMemory.FlightDataOffsets["TrimPitch"]);   // what tools/e2e/games/BmsSettings.ps1 counted
        Assert.Equal(BmsSharedMemory.FlightDataSize, BmsSharedMemory.FlightDataOffsets["MainPower"] + 4);
        Assert.Equal(BmsSharedMemory.FlightData2Size, BmsSharedMemory.FlightData2Offsets["gsMin"] + 4);
        foreach (var v in BmsSharedMemory.Values)
        {
            var size = v.Area == BmsArea.FlightData ? BmsSharedMemory.FlightDataSize : BmsSharedMemory.FlightData2Size;
            Assert.True(v.Offset + BmsSharedMemory.SizeOf(v.Type) <= size, v.Key);
        }

        var keys = BmsTelemetrySource.Catalogue.Select(d => d.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.StartsWith("bms.", k));
    }

    [Fact]
    public void Flight_frame_has_raw_values_flags_derived_values_and_nav_mappings()
    {
        var clock = new ManualClock(10);
        var source = new BmsTelemetrySource(clock);
        var rec = new Recorder(source, raw: false);
        var f = Fixture.Flight();

        Assert.True(source.Ingest(f.Fd, f.Fd2));
        var frame = Assert.Single(rec.Frames);
        Assert.Equal(400, frame.Values["bms.air.kias"]);
        Assert.Equal(0.125, frame.Values["bms.trim.pitch"]);
        Assert.Equal(60, frame.Values["bms.cm.chaff"]);
        Assert.Equal(0x40000001u, (uint)frame.Values["bms.light.bits"]);
        Assert.Equal(1, frame.Values["bms.light.master_caution"]);
        Assert.Equal(1, frame.Values["bms.light.autopilot_on"]);
        Assert.Equal(0, frame.Values["bms.light.fuel_low"]);
        Assert.Equal(1, frame.Values["bms.light3.nose_gear_down"]);
        Assert.Equal(1, frame.Values["bms.hsi.flying"]);
        Assert.Equal(31, frame.Values["bms.session.vehicle_acd"]);
        Assert.Equal(5.5, frame.Values["bms.air.gs_max"], 5);
        Assert.Equal(7000, frame.Values["bms.calc.fuel"]);
        Assert.Equal(10_000, frame.Values["bms.calc.alt"]);
        Assert.Equal(600, frame.Values["bms.calc.vs"]);
        Assert.Equal(270, frame.Values["bms.calc.heading"], 3);
        Assert.Equal(0, frame.Values["bms.calc.track"], 3);
        Assert.Equal(9500, frame.Values["bms.calc.agl"]);
        Assert.Equal(0, frame.Values["bms.calc.bullseye_bearing"], 3);   // 100000 ft north of the bullseye
        Assert.Equal(100_000 / 6076.12, frame.Values["bms.calc.bullseye_range"], 3);
        Assert.Equal(0, frame.Values["bms.rwr.count"]);
        Assert.DoesNotContain(frame.Values.Keys, k => k.StartsWith("bms.rwr.0.", StringComparison.Ordinal));

        Assert.Equal(37.5, Nav(source, frame, TelemetryNav.Latitude), 4);
        Assert.Equal(127.25, Nav(source, frame, TelemetryNav.Longitude), 4);
        Assert.Equal(3048, Nav(source, frame, TelemetryNav.Altitude), 3);
        Assert.Equal(2_000_000 * 0.3048, Nav(source, frame, TelemetryNav.U), 1);
        Assert.Equal(1_500_000 * 0.3048, Nav(source, frame, TelemetryNav.V), 1);
        Assert.Equal(-0.5 * 180 / Math.PI, Nav(source, frame, TelemetryNav.Roll), 3);
        Assert.Equal(0.1 * 180 / Math.PI, Nav(source, frame, TelemetryNav.Pitch), 3);
        Assert.Equal(270, Nav(source, frame, TelemetryNav.Yaw), 3);
        Assert.Equal(400 * 0.514444, Nav(source, frame, TelemetryNav.Ias), 3);
        Assert.Equal(700 * 0.3048, Nav(source, frame, TelemetryNav.Tas), 3);
        Assert.Equal(4.5, Nav(source, frame, TelemetryNav.Aoa), 4);
        Assert.Equal(1.2, Nav(source, frame, TelemetryNav.VerticalG), 4);
        Assert.Equal(0.25, Nav(source, frame, TelemetryNav.Airbrakes), 4);
        Assert.Equal(7000 * 0.45359237, Nav(source, frame, TelemetryNav.FuelKg), 3);
        Assert.Equal(10 * 0.3048, Nav(source, frame, TelemetryNav.VerticalSpeed), 4);
        Assert.Equal(9500 * 0.3048, Nav(source, frame, TelemetryNav.Agl), 3);
        Assert.Equal("4.38.1 build 12345", source.SessionInfo[TelemetryInfoKeys.GameVersion]);
        Assert.Equal("ACD 31", source.SessionInfo[TelemetryInfoKeys.Aircraft]);   // no string area yet
    }

    [Fact]
    public void Session_starts_when_entering_3D_and_ends_when_leaving_it()
    {
        var clock = new ManualClock();
        var source = new BmsTelemetrySource(clock);
        var rec = new Recorder(source, raw: false);
        var ui = new Fixture(flying: false);

        Assert.False(source.Ingest(ui.Fd, ui.Fd2));
        Assert.Empty(rec.Frames);
        Assert.Empty(rec.Of(TelemetryEventKinds.SessionStart));

        var f = Fixture.Flight();
        Assert.True(source.Ingest(f.Fd, f.Fd2));
        Assert.True(source.Flying);
        Assert.Single(rec.Of(TelemetryEventKinds.SessionStart));

        // unchanged data: no new frame
        clock.Advance(0.05);
        Assert.False(source.Ingest(f.Fd, f.Fd2));
        Assert.Single(rec.Frames);

        f.F1("kias", 401);
        clock.Advance(0.05);
        Assert.True(source.Ingest(f.Fd, f.Fd2));
        Assert.Equal(2, rec.Frames.Count);

        f.Flying = false;
        Assert.False(source.Ingest(f.Fd, f.Fd2));
        Assert.False(source.Flying);
        Assert.Single(rec.Of(TelemetryEventKinds.SessionEnd));
        Assert.Null(source.Latest);
        Assert.Equal(2, rec.Frames.Count);

        // back in 3D: a second session; stopping the feed ends it
        f.Flying = true;
        Assert.True(source.Ingest(f.Fd, f.Fd2));
        Assert.Equal(2, rec.Of(TelemetryEventKinds.SessionStart).Length);
        source.EndSession("recording stopped");
        Assert.Equal(2, rec.Of(TelemetryEventKinds.SessionEnd).Length);
        Assert.Equal("recording stopped", rec.Of(TelemetryEventKinds.SessionEnd)[1].Text);
    }

    [Fact]
    public void Raw_snapshots_only_with_a_listener_and_only_when_changed()
    {
        var f = Fixture.Flight();
        var quiet = new BmsTelemetrySource(new ManualClock());
        var quietRec = new Recorder(quiet, raw: false);
        Assert.True(quiet.Ingest(f.Fd, f.Fd2));
        Assert.False(quiet.WantsRaw);
        Assert.Empty(quietRec.Raw);

        var clock = new ManualClock();
        var source = new BmsTelemetrySource(clock);
        var rec = new Recorder(source, raw: true);
        Assert.True(source.Ingest(f.Fd, f.Fd2));
        Assert.Equal(["shm:FlightData", "shm:FlightData2"], rec.Raw.Select(r => r.Stream).ToArray());
        Assert.Equal(f.Fd, rec.Raw.First().Data);

        source.Ingest(f.Fd, f.Fd2);   // unchanged
        Assert.Equal(2, rec.Raw.Count);

        f.F1("kias", 410);            // only FlightData changed
        source.Ingest(f.Fd, f.Fd2);
        Assert.Equal(3, rec.Raw.Count);
        Assert.Equal("shm:FlightData", rec.Raw.Last().Stream);
        Assert.Equal(410, BinaryPrimitives.ReadSingleLittleEndian(rec.Raw.Last().Data.AsSpan(52)));
    }

    [Fact]
    public void Ded_and_pfl_lines_are_throttled_events()
    {
        var clock = new ManualClock();
        var source = new BmsTelemetrySource(clock);
        var rec = new Recorder(source, raw: false);
        var f = Fixture.Flight().Text1("DEDLines", 0, "  UHF 292.30  STPT  5").Text1("DEDLines", 1, " COM2 VHF 1").Text1("PFLLines", 0, "PFL FAULT");

        source.Ingest(f.Fd, f.Fd2);
        var ded = Assert.Single(rec.Of(BmsTelemetrySource.DedEvent));
        Assert.Equal("  UHF 292.30  STPT  5\n COM2 VHF 1", ded.Text);
        Assert.Equal("PFL FAULT", Assert.Single(rec.Of(BmsTelemetrySource.PflEvent)).Text);

        // a burst within 0.25 s: only the last text, once the interval is over
        clock.Advance(0.05);
        f.Text1("DEDLines", 1, " COM2 VHF 2");
        source.Ingest(f.Fd, f.Fd2);
        clock.Advance(0.05);
        f.Text1("DEDLines", 1, " COM2 VHF 3");
        source.Ingest(f.Fd, f.Fd2);
        Assert.Single(rec.Of(BmsTelemetrySource.DedEvent));
        clock.Advance(0.2);
        source.Ingest(f.Fd, f.Fd2);   // unchanged data still flushes the pending text
        var all = rec.Of(BmsTelemetrySource.DedEvent);
        Assert.Equal(2, all.Length);
        Assert.EndsWith("VHF 3", all[1].Text);
        Assert.Single(rec.Of(BmsTelemetrySource.PflEvent));
    }

    [Fact]
    public void Strings_give_aircraft_theatre_flight_and_steerpoints()
    {
        var clock = new ManualClock();
        var source = new BmsTelemetrySource(clock);
        var rec = new Recorder(source, raw: false);
        source.IngestStrings(StringArea(
            (BmsSharedMemory.StringIds.BmsExe, @"C:\Falcon BMS 4.38\Bin\x64\Falcon BMS.exe"),
            (BmsSharedMemory.StringIds.ThrName, "Korea KTO"),
            (BmsSharedMemory.StringIds.AcName, "F-16C-50"),
            (BmsSharedMemory.StringIds.AcNctr, "F-16"),
            (BmsSharedMemory.StringIds.NavPoint, "NP:1,WP,1450000.0,2010000.0,-150.0,10.0;"),
            (BmsSharedMemory.StringIds.NavPoint, "NP:2,PT,1600000.0,2100000.0,0.0,20.0;PT:\"SA-2\",15.0,0;"),
            (BmsSharedMemory.StringIds.NavPoint, "NP:3,L1,1600000.0,2100000.0,0.0,20.0;"),
            (BmsSharedMemory.StringIds.VoiceHelpers, "Goblin1|PAXX,Texaco7,Chalice9,Osan,Osan,Pyeongtaek")));
        var f = Fixture.Flight().B2("pilotsOnline", 1).Callsign(0, "Viper", 3);

        source.Ingest(f.Fd, f.Fd2);
        Assert.Equal("F-16C-50", source.SessionInfo[TelemetryInfoKeys.Aircraft]);
        Assert.Equal("Korea KTO", source.SessionInfo[TelemetryInfoKeys.Map]);
        Assert.Equal("Viper", source.SessionInfo[TelemetryInfoKeys.Pilot]);
        Assert.Equal("Goblin1", source.SessionInfo[BmsTelemetrySource.FlightInfoKey]);
        Assert.Equal("F-16C-50, Korea KTO", Assert.Single(rec.Of(TelemetryEventKinds.SessionStart)).Text);
        Assert.Equal("F-16C-50", Assert.Single(rec.Of(TelemetryEventKinds.Aircraft)).Text);
        Assert.Equal("Viper|flying", Assert.Single(rec.Of(BmsTelemetrySource.PilotsEvent)).Text);
        Assert.Equal(3, rec.Frames.Single().Values["bms.mp.pilot.0.state"]);

        var objects = Assert.Single(rec.Of(TelemetryEventKinds.Objects)).Text.Split('\n');
        Assert.Equal(3, objects.Length);   // bullseye, STPT 1, PPT 2 (the line point is skipped)
        var bull = objects[0].Split('|');
        Assert.Equal(["bullseye", "Navaid+Static+Bullseye", "Bullseye"], bull[..3]);
        Assert.Equal(37.5 - 100_000 * 0.3048 / 111320, double.Parse(bull[4], System.Globalization.CultureInfo.InvariantCulture), 5);
        Assert.Equal(127.25, double.Parse(bull[5], System.Globalization.CultureInfo.InvariantCulture), 5);
        Assert.Equal(2_000_000 * 0.3048, double.Parse(bull[10], System.Globalization.CultureInfo.InvariantCulture), 1);
        var stpt = objects[1].Split('|');
        Assert.Equal(["np1WP", "Navaid+Static+Waypoint", "STPT 1"], stpt[..3]);
        Assert.Equal(1500 * 0.3048, double.Parse(stpt[6], System.Globalization.CultureInfo.InvariantCulture), 3);
        var ppt = objects[2].Split('|');
        Assert.Equal(["np2PT", "Ground+Static+AntiAircraft", "PPT 2 SA-2", "Enemies"], ppt[..4]);

        // unchanged: no repeated objects / pilots events
        f.F1("kias", 420);
        source.Ingest(f.Fd, f.Fd2);
        Assert.Single(rec.Of(TelemetryEventKinds.Objects));
        Assert.Single(rec.Of(BmsTelemetrySource.PilotsEvent));
    }

    [Fact]
    public void Rwr_contacts_are_values_of_the_active_slots()
    {
        var source = new BmsTelemetrySource(new ManualClock());
        var rec = new Recorder(source, raw: false);
        var f = Fixture.Flight().I1("RwrObjectCount", 2)
            .I1("RWRsymbol", 101, 0).F1("bearing", 0.5f, 0).F1("lethality", 0.9f, 0)
            .I1("RWRsymbol", 105, 1).I1("missileLaunch", 1, 1).B2("RWRjammingStatus", 1, 1);
        source.Ingest(f.Fd, f.Fd2);
        var v = rec.Frames.Single().Values;
        Assert.Equal(2, v["bms.rwr.count"]);
        Assert.Equal(101, v["bms.rwr.0.symbol"]);
        Assert.Equal(0.5, v["bms.rwr.0.bearing"], 5);
        Assert.Equal(0.9, v["bms.rwr.0.lethality"], 5);
        Assert.Equal(1, v["bms.rwr.1.missile_launch"]);
        Assert.Equal(1, v["bms.rwr.1.jamming"]);
        Assert.False(v.ContainsKey("bms.rwr.2.symbol"));
    }

    [Fact]
    public void Older_flightdata2_versions_leave_out_newer_members()
    {
        var source = new BmsTelemetrySource(new ManualClock());
        var rec = new Recorder(source, raw: false);
        var f = Fixture.Flight();
        f.I2("VersionNum", 10);
        source.Ingest(f.Fd, f.Fd2);
        var v = rec.Frames.Single().Values;
        Assert.True(v.ContainsKey("bms.eng2.rpm"));
        Assert.True(v.ContainsKey("bms.gear.bump"));        // version 10
        Assert.False(v.ContainsKey("bms.pos.lat"));          // version 11
        Assert.False(v.ContainsKey("bms.air.gs_max"));       // version 23
        Assert.False(v.ContainsKey("bms.calc.agl"));

        // no FlightData2 at all: FlightData still works
        var only = new BmsTelemetrySource(new ManualClock());
        var onlyRec = new Recorder(only, raw: false);
        Assert.True(only.Ingest(f.Fd, ReadOnlySpan<byte>.Empty));
        Assert.Equal(400, onlyRec.Frames.Single().Values["bms.air.kias"]);
        Assert.False(onlyRec.Frames.Single().Values.ContainsKey("bms.eng2.rpm"));
    }

    // ------------------------------------------------------------------ feed over real (test-named) shared memory

    private sealed class FakeBms : IDisposable
    {
        private readonly List<(MemoryMappedFile File, MemoryMappedViewAccessor View)> _areas = [];
        public readonly string Prefix = "JoyProTest_" + Guid.NewGuid().ToString("N") + "_";

        public void Create(string name, int size)
        {
            var file = MemoryMappedFile.CreateNew(Prefix + name, size);
            _areas.Add((file, file.CreateViewAccessor()));
        }

        public void Write(int area, byte[] data) => _areas[area].View.WriteArray(0, data, 0, data.Length);

        public void Dispose()
        {
            foreach (var (file, view) in _areas)
            {
                view.Dispose();
                file.Dispose();
            }
        }
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return true;
            Thread.Sleep(10);
        }

        return condition();
    }

    [Fact]
    public void Feed_reads_test_named_shared_memory_and_follows_3D_state()
    {
        using var bms = new FakeBms();
        var f = Fixture.Flight();
        var strings = StringArea((BmsSharedMemory.StringIds.ThrName, "Balkans"), (BmsSharedMemory.StringIds.AcName, "F-16C-52"));
        f.I2("StringAreaSize", strings.Length).I2("StringAreaTime", 7);
        bms.Create(BmsSharedMemory.FlightDataName, BmsSharedMemory.FlightDataSize);
        bms.Create(BmsSharedMemory.FlightData2Name, BmsSharedMemory.FlightData2Size);
        bms.Create(BmsSharedMemory.StringDataName, strings.Length);
        bms.Write(0, f.Fd);
        bms.Write(1, f.Fd2);
        bms.Write(2, strings);

        var feed = (BmsTelemetryFeed)TelemetryFeeds.Create("FalconBMS", new StopwatchClock(), new TelemetryFeedOptions { BmsMemoryPrefix = bms.Prefix, PollHz = 50 });
        var rec = new Recorder(feed.Source, raw: true);
        Assert.Equal("shared memory " + bms.Prefix + "FalconSharedMemoryArea", feed.Endpoint);
        feed.Start();
        try
        {
            Assert.True(WaitUntil(() => !rec.Frames.IsEmpty));
            Assert.Null(feed.Problem);
            var frame = rec.Frames.First();
            Assert.Equal(400, frame.Values["bms.air.kias"]);
            Assert.Equal(37.5, frame.Values["bms.pos.lat"], 4);
            Assert.Equal("Balkans", feed.Source.SessionInfo[TelemetryInfoKeys.Map]);
            Assert.Equal("F-16C-52", feed.Source.SessionInfo[TelemetryInfoKeys.Aircraft]);
            Assert.Single(rec.Of(TelemetryEventKinds.SessionStart));
            Assert.Contains(rec.Raw, r => r.Stream == "shm:FlightData" && r.Data.Length == BmsSharedMemory.FlightDataSize);
            Assert.Contains(rec.Raw, r => r.Stream == "shm:FlightData2" && r.Data.Length == BmsSharedMemory.FlightData2Size);
            Assert.Contains(rec.Raw, r => r.Stream == "shm:StringData");

            // unchanged memory: polls go on, frames do not
            var polls = feed.Polls;
            var frames = rec.Frames.Count;
            Assert.True(WaitUntil(() => feed.Polls >= polls + 5));
            Assert.Equal(frames, rec.Frames.Count);

            f.F1("kias", 450);
            bms.Write(0, f.Fd);
            Assert.True(WaitUntil(() => rec.Frames.Any(fr => fr.Values["bms.air.kias"] == 450)));

            f.Flying = false;
            bms.Write(0, f.Fd);
            Assert.True(WaitUntil(() => rec.Of(TelemetryEventKinds.SessionEnd).Length == 1));
            Assert.Equal("left the 3D world", rec.Of(TelemetryEventKinds.SessionEnd)[0].Text);
        }
        finally
        {
            feed.Dispose();
        }

        Assert.False(feed.IsOpen);
    }

    [Fact]
    public void Feed_without_listener_publishes_no_raw_and_waits_for_bms()
    {
        using var bms = new FakeBms();
        var feed = new BmsTelemetryFeed(new BmsTelemetrySource(new StopwatchClock()), bms.Prefix, 50) { RetryInterval = TimeSpan.FromMilliseconds(100) };
        var rec = new Recorder(feed.Source, raw: false);
        feed.Start();
        try
        {
            Thread.Sleep(150);
            Assert.False(feed.IsOpen);
            Assert.Equal(BmsTelemetryFeed.NotOpenProblem, feed.Problem);

            // BMS starts: it creates the areas, the feed finds them on its next retry
            var f = Fixture.Flight();
            bms.Create(BmsSharedMemory.FlightDataName, BmsSharedMemory.FlightDataSize);
            bms.Create(BmsSharedMemory.FlightData2Name, BmsSharedMemory.FlightData2Size);
            bms.Write(0, f.Fd);
            bms.Write(1, f.Fd2);
            Assert.True(WaitUntil(() => !rec.Frames.IsEmpty));
            Assert.True(feed.IsOpen);
            Assert.Null(feed.Problem);
            Assert.Empty(rec.Raw);
        }
        finally
        {
            feed.Stop();
        }

        // stopping in 3D ends the session
        Assert.Single(rec.Of(TelemetryEventKinds.SessionEnd));
        Assert.False(feed.IsOpen);
        feed.Stop();   // idempotent
        Assert.Single(rec.Of(TelemetryEventKinds.SessionEnd));
    }

    // ------------------------------------------------------------------ the installed header (skipped without BMS)

    private static string? FindInstalledHeader()
    {
        var env = Environment.GetEnvironmentVariable("JOYPRO_BMS_FLIGHTDATA_H");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
            return env;
        const string known = @"C:\Falcon BMS 4.38\Tools\SharedMem\FlightData.h";
        if (File.Exists(known))
            return known;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;
                foreach (var dir in Directory.GetDirectories(drive.RootDirectory.FullName, "Falcon BMS*").OrderDescending())
                {
                    var path = Path.Combine(dir, "Tools", "SharedMem", "FlightData.h");
                    if (File.Exists(path))
                        return path;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    [Fact]
    public void Offsets_match_the_installed_FlightData_header()
    {
        var path = FindInstalledHeader();
        if (path is null)
            return;   // no BMS on this PC: nothing to compare with
        var header = new HeaderLayout(File.ReadAllText(path));

        var (fd, fdSize) = header.Layout("FlightData");
        var (fd2, fd2Size) = header.Layout("FlightData2");
        Assert.Equal(BmsSharedMemory.FlightDataOffsets.OrderBy(p => p.Value).Select(p => p.Key + "@" + p.Value), fd.Select(p => p.Name + "@" + p.Offset));
        Assert.Equal(BmsSharedMemory.FlightDataSize, fdSize);
        Assert.Equal(BmsSharedMemory.FlightData2Offsets.OrderBy(p => p.Value).Select(p => p.Key + "@" + p.Value), fd2.Select(p => p.Name + "@" + p.Offset));
        Assert.Equal(BmsSharedMemory.FlightData2Size, fd2Size);
        Assert.Equal(BmsSharedMemory.FlightDataVersion, header.Constants["FLIGHTDATA_VERSION"]);
        Assert.Equal(BmsSharedMemory.FlightData2Version, header.Constants["FLIGHTDATA2_VERSION"]);
        Assert.Equal(BmsSharedMemory.MaxRwrObjects, header.Constants["MAX_RWR_OBJECTS"]);
        Assert.Equal(BmsSharedMemory.CallsignLength, header.Constants["CALLSIGN_LEN"]);
        Assert.Equal(BmsSharedMemory.MaxCallsigns, header.Constants["MAX_CALLSIGNS"]);

        var ids = header.EnumValues("StringIdentifier");
        Assert.Equal(BmsSharedMemory.StringIds.BmsExe, (uint)ids["BmsExe"]);
        Assert.Equal(BmsSharedMemory.StringIds.ThrName, (uint)ids["ThrName"]);
        Assert.Equal(BmsSharedMemory.StringIds.AcName, (uint)ids["AcName"]);
        Assert.Equal(BmsSharedMemory.StringIds.AcNctr, (uint)ids["AcNCTR"]);
        Assert.Equal(BmsSharedMemory.StringIds.NavPoint, (uint)ids["NavPoint"]);
        Assert.Equal(BmsSharedMemory.StringIds.VoiceHelpers, (uint)ids["VoiceHelpers"]);
        Assert.Equal(0x80000000L, header.EnumValues("HsiBits")["Flying"]);
        Assert.Equal(0x01L, header.EnumValues("MiscBits")["RALT_Valid"]);

        // every decoded flag is a bit the header names in that enum
        var enums = new Dictionary<string, string>
        {
            ["bms.light.bits"] = "LightBits", ["bms.light2.bits"] = "LightBits2", ["bms.light3.bits"] = "LightBits3", ["bms.hsi.bits"] = "HsiBits",
            ["bms.alt.bits"] = "AltBits", ["bms.elec.power_bits"] = "PowerBits", ["bms.blink.bits"] = "BlinkBits", ["bms.betty.bits"] = "BettyBits",
            ["bms.misc.bits"] = "MiscBits", ["bms.nav.tacan_ufc_bits"] = "TacanBits", ["bms.nav.tacan_aux_bits"] = "TacanBits",
        };
        foreach (var flag in BmsSharedMemory.Flags)
            Assert.Contains((long)flag.Mask, header.EnumValues(enums[flag.WordKey]).Values);
    }

    /// <summary>Re-derives the MSVC layout of the classes in FlightData.h (natural alignment, max 8).</summary>
    private sealed class HeaderLayout
    {
        private readonly string _text;
        private readonly Dictionary<string, int> _enumSizes = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Constants = new(StringComparer.Ordinal);

        public HeaderLayout(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            _text = Regex.Replace(text, @"//[^\n]*", "");
            foreach (Match m in Regex.Matches(_text, @"#define\s+(\w+)\s+(\d+)\s*$", RegexOptions.Multiline))
                Constants[m.Groups[1].Value] = long.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            foreach (Match m in Regex.Matches(_text, @"enum\s+(\w+)\s*(?::\s*([\w ]+?))?\s*\{"))
            {
                var underlying = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "int";
                _enumSizes[m.Groups[1].Value] = underlying.EndsWith("char", StringComparison.Ordinal) ? 1 : underlying.EndsWith("short", StringComparison.Ordinal) ? 2 : 4;
                foreach (var pair in EnumValues(m.Groups[1].Value))
                    Constants.TryAdd(pair.Key, pair.Value);
            }
        }

        public Dictionary<string, long> EnumValues(string name)
        {
            var m = Regex.Match(_text, @"enum\s+" + name + @"\b\s*(?::[\w ]+)?\s*\{(?<body>[^}]*)\}");
            Assert.True(m.Success, "enum " + name);
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            long next = 0;
            foreach (var raw in m.Groups["body"].Value.Split(','))
            {
                var item = raw.Trim();
                if (item.Length == 0)
                    continue;
                var eq = item.IndexOf('=');
                var key = (eq < 0 ? item : item[..eq]).Trim();
                if (eq >= 0)
                {
                    var expr = item[(eq + 1)..].Trim();
                    if (!TryNumber(expr, out next))
                        continue;   // computed masks (AllLampBits... = A ^ B)
                }

                values[key] = next;
                next++;
            }

            return values;
        }

        private static bool TryNumber(string s, out long value)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
            return long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        public (List<(string Name, int Offset)> Members, int Size) Layout(string className)
        {
            var m = Regex.Match(_text, @"class\s+" + className + @"\s*\{");
            Assert.True(m.Success, "class " + className);
            var body = Body(m.Index + m.Length);
            body = Regex.Replace(body, @"enum\s+\w+[^{;]*\{[^}]*\}\s*;", " ");
            body = Regex.Replace(body, @"\b(public|private|protected)\s*:", " ");

            var members = new List<(string, int)>();
            int offset = 0, maxAlign = 1;
            foreach (var statement in Statements(body))
            {
                var d = Regex.Match(statement, @"^(?<type>(unsigned\s+|signed\s+)?(long\s+long|\w+))\s+(?<names>[^()]+)$");
                Assert.True(d.Success, "cannot parse: " + statement);
                var type = Regex.Replace(d.Groups["type"].Value, @"^(unsigned|signed)\s+", "");
                var size = type switch
                {
                    "char" or "bool" => 1,
                    "short" => 2,
                    "int" or "long" or "float" or "unsigned" => 4,
                    "double" or "long long" => 8,
                    _ when _enumSizes.TryGetValue(type, out var e) => e,
                    _ => throw new InvalidOperationException("unknown type " + type + " in " + statement)
                };
                foreach (var rawName in d.Groups["names"].Value.Split(','))
                {
                    var nm = Regex.Match(rawName.Trim(), @"^(\w+)((\[\w+\])*)$");
                    Assert.True(nm.Success, "member: " + rawName);
                    long count = 1;
                    foreach (Match dim in Regex.Matches(nm.Groups[2].Value, @"\[(\w+)\]"))
                        count *= long.TryParse(dim.Groups[1].Value, out var n) ? n : Constants[dim.Groups[1].Value];
                    var align = Math.Min(size, 8);
                    maxAlign = Math.Max(maxAlign, align);
                    offset = (offset + align - 1) / align * align;
                    members.Add((nm.Groups[1].Value, offset));
                    offset += (int)(size * count);
                }
            }

            return (members, (offset + maxAlign - 1) / maxAlign * maxAlign);
        }

        private string Body(int start)
        {
            var depth = 1;
            for (var i = start; i < _text.Length; i++)
            {
                if (_text[i] == '{')
                    depth++;
                else if (_text[i] == '}' && --depth == 0)
                    return _text[start..i];
            }

            throw new InvalidOperationException("unbalanced braces");
        }

        /// <summary>Top-level declarations; inline method bodies are dropped.</summary>
        private static IEnumerable<string> Statements(string body)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < body.Length; i++)
            {
                var c = body[i];
                if (c == '{')
                {
                    var depth = 1;
                    while (++i < body.Length && depth > 0)
                    {
                        if (body[i] == '{') depth++;
                        else if (body[i] == '}') depth--;
                    }

                    i--;
                    sb.Clear();   // a method: its signature is not a member
                    while (i + 1 < body.Length && char.IsWhiteSpace(body[i + 1])) i++;
                    if (i + 1 < body.Length && body[i + 1] == ';') i++;
                    continue;
                }

                if (c == ';')
                {
                    var s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
                    sb.Clear();
                    if (s.Length > 0 && !s.Contains('('))
                        yield return s;
                    continue;
                }

                sb.Append(c);
            }
        }
    }
}
