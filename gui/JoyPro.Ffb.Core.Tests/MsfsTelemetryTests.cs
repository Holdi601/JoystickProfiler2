using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using JoyPro.Ffb.Core.Telemetry;
using JoyPro.Ffb.Core.Telemetry.Msfs;

namespace JoyPro.Ffb.Core.Tests;

/// <summary>MSFS 2020 / 2024 through SimConnect, with a fake client (no sim, no SimConnect.dll).</summary>
public class MsfsTelemetryTests
{
    private static string K(string simVar) => MsfsSimVars.KeyOf(simVar);

    private static int Datum(string simVar)
    {
        for (var i = 0; i < MsfsSimVars.All.Count; i++)
            if (MsfsSimVars.All[i].Name == simVar)
                return i;
        throw new ArgumentException(simVar);
    }

    private static int StringDatum(string name) => MsfsSimVars.Strings.ToList().IndexOf(name);

    private static (MsfsTelemetrySource Source, FakeSimConnect Client, ManualClock Clock) Connected(double pollHz = 20)
    {
        var clock = new ManualClock(10);
        var source = new MsfsTelemetrySource(clock, "Microsoft Flight Simulator 2024", pollHz);
        var client = new FakeSimConnect();
        Assert.True(client.Open("test", new AutoResetEvent(false)));
        Assert.True(source.Connect(client));
        return (source, client, clock);
    }

    private static byte[] Fast(params (string SimVar, double Value)[] values) =>
        SimMsg.Tagged(MsfsTelemetrySource.RequestFast, 1, MsfsTelemetrySource.DefineFast, values.Select(v => (Datum(v.SimVar), v.Value)).ToArray());

    private static byte[] Slow(params (string SimVar, double Value)[] values) =>
        SimMsg.Tagged(MsfsTelemetrySource.RequestSlow, 1, MsfsTelemetrySource.DefineSlow, values.Select(v => (Datum(v.SimVar), v.Value)).ToArray());

    private static readonly (string, double)[] Position = [("PLANE LATITUDE", 47.45), ("PLANE LONGITUDE", -122.31), ("PLANE ALTITUDE", 1000)];

    [Fact]
    public void Catalogue_IsLarge_KeysAreUniqueLowercaseUnderscored()
    {
        Assert.InRange(MsfsSimVars.All.Count, 150, 250);
        var keys = MsfsSimVars.All.Select(v => v.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.Matches("^msfs\\.[a-z0-9]+(_[a-z0-9]+)*$", k));
        Assert.Equal("msfs.plane_latitude", K("PLANE LATITUDE"));
        Assert.Equal("msfs.general_eng_throttle_lever_position_1", K("GENERAL ENG THROTTLE LEVER POSITION:1"));
        Assert.Equal("msfs.gear_is_on_ground_0", K("GEAR IS ON GROUND:0"));
        Assert.Equal(MsfsSimVars.All.Count, MsfsSimVars.Descriptors.Count);
        foreach (var category in new[] { "Position", "Attitude", "Velocity", "Acceleration", "Air data", "Controls", "Inputs", "Trim", "Configuration", "Ground", "Engines", "Fuel", "Autopilot", "Lights", "Environment", "Simulation", "Warnings" })
            Assert.Contains(MsfsSimVars.All, v => v.Category == category);
        Assert.Contains("TITLE", MsfsSimVars.Strings);
        Assert.Contains("ATC ID", MsfsSimVars.Strings);
    }

    [Fact]
    public void Connect_DefinesEveryVariable_WithNamesAndUnits_AndAsksOnlyForData()
    {
        var (_, client, _) = Connected();
        var defs = client.Definitions;
        Assert.Equal(MsfsSimVars.All.Count + MsfsSimVars.Strings.Count + 8, defs.Count);

        var lat = defs.Single(d => d.Name == "PLANE LATITUDE" && d.Define == MsfsTelemetrySource.DefineFast);
        Assert.Equal("degrees", lat.Units);
        Assert.Equal(SimConnectDataType.Float64, lat.Type);
        Assert.Equal((uint)Datum("PLANE LATITUDE"), lat.Datum);
        Assert.Equal("feet", defs.Single(d => d.Name == "PLANE ALTITUDE" && d.Define == MsfsTelemetrySource.DefineFast).Units);
        Assert.Equal("knots", defs.Single(d => d.Name == "AIRSPEED INDICATED").Units);
        Assert.Equal("gforce", defs.Single(d => d.Name == "G FORCE").Units);
        var fuel = defs.Single(d => d.Name == "FUEL TOTAL QUANTITY WEIGHT");
        Assert.Equal(MsfsTelemetrySource.DefineSlow, fuel.Define);
        Assert.Equal("pounds", fuel.Units);
        for (var n = 1; n <= 4; n++)
            Assert.Contains(defs, d => d.Name == "GENERAL ENG THROTTLE LEVER POSITION:" + n && d.Units == "percent");
        var title = defs.Single(d => d.Name == "TITLE" && d.Define == MsfsTelemetrySource.DefineStrings);
        Assert.Null(title.Units);
        Assert.Equal(SimConnectDataType.String256, title.Type);
        Assert.Equal(8, defs.Count(d => d.Define == MsfsTelemetrySource.DefineObjects));

        var fast = client.Requests.Single(r => r.Request == MsfsTelemetrySource.RequestFast);
        Assert.Equal(SimConnectPeriod.SimFrame, fast.Period);
        Assert.Equal(SimConnectRequestFlags.Changed | SimConnectRequestFlags.Tagged, fast.Flags);
        Assert.Equal(1u, fast.Interval);    // 45 Hz assumed, 20 Hz wanted: every second frame
        Assert.Equal(SimConnectPeriod.Second, client.Requests.Single(r => r.Request == MsfsTelemetrySource.RequestSlow).Period);
        Assert.Equal(SimConnectPeriod.Second, client.Requests.Single(r => r.Request == MsfsTelemetrySource.RequestStrings).Period);
        Assert.All(client.Requests, r => Assert.Equal(SimConnectLayout.ObjectIdUser, r.Object));

        Assert.Equal(MsfsTelemetrySource.SystemEvents, client.Subscriptions);
        Assert.Contains("SimStart", client.Subscriptions);
        Assert.Contains("FlightLoaded", client.Subscriptions);
        Assert.Equal(["FlightLoaded", "AircraftLoaded", "Sim"], client.StateRequests);
    }

    [Fact]
    public void DataMessages_DecodeToKeys_SlowValuesRideAlong()
    {
        var (source, _, _) = Connected();
        Assert.Equal(MsfsTelemetrySource.MessageResult.Handled, source.Handle(Slow(("FUEL TOTAL QUANTITY WEIGHT", 2000), ("TURB ENG N2:1", 95.5))));
        Assert.Null(source.Latest);     // no flight model message yet

        source.Handle(Fast([.. Position, ("PLANE BANK DEGREES", -30), ("AIRSPEED INDICATED", 120), ("G FORCE", 1.5)]));
        var frame = source.Latest!;
        Assert.Equal(47.45, frame.Values[K("PLANE LATITUDE")]);
        Assert.Equal(-122.31, frame.Values[K("PLANE LONGITUDE")]);
        Assert.Equal(-30, frame.Values[K("PLANE BANK DEGREES")]);
        Assert.Equal(1.5, frame.Values[K("G FORCE")]);
        Assert.Equal(2000, frame.Values[K("FUEL TOTAL QUANTITY WEIGHT")]);
        Assert.Equal(95.5, frame.Values[K("TURB ENG N2:1")]);

        // Changed-only: a later message with one value keeps the others.
        source.Handle(Fast(("G FORCE", 2.5)));
        Assert.Equal(2, source.FramesPublished);
        Assert.Equal(2.5, source.Latest!.Values[K("G FORCE")]);
        Assert.Equal(120, source.Latest!.Values[K("AIRSPEED INDICATED")]);
        Assert.Equal(2, source.FastMessages);
    }

    [Fact]
    public void Frames_AreHeldBack_AtNullIsland_AndOutsideAFlight()
    {
        var (source, _, _) = Connected();
        source.Handle(Fast(("G FORCE", 1)));
        Assert.Null(source.Latest);     // lat/lon 0/0: menu or loading

        source.Handle(SimMsg.SystemState(MsfsTelemetrySource.RequestStateSim, 0, ""));
        Assert.False(source.InFlight);
        source.Handle(Fast(Position));
        Assert.Null(source.Latest);

        source.Handle(SimMsg.Event(1, 0));   // SimStart
        Assert.True(source.InFlight);
        source.Handle(Fast(("G FORCE", 1.1)));
        Assert.NotNull(source.Latest);
    }

    [Fact]
    public void Strings_BecomeSessionInfo()
    {
        var (source, _, _) = Connected();
        var events = new List<TelemetryEvent>();
        source.EventRaised += events.Add;
        source.Handle(SimMsg.TaggedStrings(MsfsTelemetrySource.RequestStrings,
            (StringDatum("TITLE"), "Cessna Skyhawk G1000 Asobo"), (StringDatum("ATC ID"), "D-EJPR"), (StringDatum("ATC MODEL"), "C172")));
        Assert.Equal("Cessna Skyhawk G1000 Asobo", source.SessionInfo[TelemetryInfoKeys.Aircraft]);
        Assert.Equal("D-EJPR", source.SessionInfo[TelemetryInfoKeys.Pilot]);
        Assert.Equal("C172", source.SessionInfo["msfs.atc_model"]);
        Assert.Contains(events, e => e.Kind == TelemetryEventKinds.Aircraft && e.Text == "Cessna Skyhawk G1000 Asobo");

        // The same strings again (once a second): no new events.
        var count = events.Count;
        source.Handle(SimMsg.TaggedStrings(MsfsTelemetrySource.RequestStrings, (StringDatum("TITLE"), "Cessna Skyhawk G1000 Asobo")));
        Assert.Equal(count, events.Count);
    }

    [Fact]
    public void SystemEvents_BecomeSessionEvents()
    {
        var (source, _, _) = Connected();
        var events = new List<TelemetryEvent>();
        source.EventRaised += events.Add;
        uint Id(string name) => (uint)MsfsTelemetrySource.SystemEvents.ToList().IndexOf(name) + 1;

        source.Handle(SimMsg.Open("SunRise", 1, 2, 3, 4));
        Assert.Equal("SunRise 1.2.3.4", source.SessionInfo[TelemetryInfoKeys.GameVersion]);

        source.Handle(SimMsg.EventFilename(Id("FlightLoaded"), @"C:\Users\x\AppData\Roaming\Microsoft Flight Simulator 2024\Flights\Custom Flight.FLT"));
        Assert.Equal("Custom Flight", source.SessionInfo[TelemetryInfoKeys.Mission]);
        Assert.EndsWith("Custom Flight.FLT", source.SessionInfo[MsfsTelemetrySource.InfoFlightFile]);
        source.Handle(SimMsg.EventFilename(Id("AircraftLoaded"), @"SimObjects\Airplanes\Asobo_C172\aircraft.CFG"));
        Assert.EndsWith("aircraft.CFG", source.SessionInfo[MsfsTelemetrySource.InfoAircraftFile]);

        source.Handle(SimMsg.Event(Id("SimStart"), 0));
        source.Handle(SimMsg.Event(Id("Sim"), 1));                 // the same start: no second session
        source.Handle(SimMsg.Event(Id("Pause"), 1));
        source.Handle(SimMsg.Event(Id("Pause"), 0));
        source.Handle(SimMsg.Event(Id("Crashed"), 0));
        source.Handle(SimMsg.Event(Id("PositionChanged"), 0));
        source.Handle(Fast(Position));
        Assert.NotNull(source.Latest);
        source.Handle(SimMsg.Event(Id("SimStop"), 0));
        Assert.Null(source.Latest);                                // stale outside the flight

        var kinds = events.Where(e => e.Kind != TelemetryEventKinds.Info).Select(e => e.Kind + ":" + e.Text).ToList();
        Assert.Equal(new[]
        {
            TelemetryEventKinds.SessionStart + ":SimStart",
            MsfsTelemetrySource.KindPause + ":1",
            MsfsTelemetrySource.KindPause + ":0",
            MsfsTelemetrySource.KindCrashed + ":",
            MsfsTelemetrySource.KindPositionChanged + ":",
            TelemetryEventKinds.SessionEnd + ":SimStop",
        }, kinds);
        Assert.Contains(events, e => e.Kind == TelemetryEventKinds.Info && e.Text.StartsWith(TelemetryInfoKeys.Mission + "=", StringComparison.Ordinal));
    }

    [Fact]
    public void SystemStateAnswers_FillTheSessionAfterConnectingMidFlight()
    {
        var (source, _, _) = Connected();
        var events = new List<TelemetryEvent>();
        source.EventRaised += events.Add;
        source.Handle(SimMsg.SystemState(MsfsTelemetrySource.RequestStateFlightLoaded, 0, @"flights\other\Seattle.FLT"));
        source.Handle(SimMsg.SystemState(MsfsTelemetrySource.RequestStateSim, 1, ""));
        Assert.Equal("Seattle", source.SessionInfo[TelemetryInfoKeys.Mission]);
        Assert.True(source.InFlight);
        Assert.Contains(events, e => e.Kind == TelemetryEventKinds.SessionStart);
    }

    [Fact]
    public void NavMappings_ConvertUnits_AndFlipMsfsSigns()
    {
        var (source, _, _) = Connected();
        // Right bank 30 deg, nose up 5 deg: MSFS reports both negative.
        source.Handle(Fast([.. Position, ("PLANE BANK DEGREES", -30), ("PLANE PITCH DEGREES", -5), ("PLANE HEADING DEGREES TRUE", 270),
            ("AIRSPEED INDICATED", 100), ("AIRSPEED TRUE", 110), ("AIRSPEED MACH", 0.17), ("INCIDENCE ALPHA", 4), ("INCIDENCE BETA", -1),
            ("PLANE ALT ABOVE GROUND", 500), ("G FORCE", 1.2), ("GENERAL ENG THROTTLE LEVER POSITION:1", 75), ("GEAR TOTAL PCT EXTENDED", 100),
            ("FLAPS HANDLE PERCENT", 50), ("SPOILERS HANDLE POSITION", 0), ("VERTICAL SPEED", 600), ("GPS GROUND TRUE TRACK", 268)]));
        source.Handle(Slow(("FUEL TOTAL QUANTITY WEIGHT", 100)));
        source.Handle(Fast(("G FORCE", 1.2)));
        var frame = source.Latest!;
        double Nav(string target)
        {
            var m = source.NavMappings.Single(n => n.Target == target);
            Assert.True(frame.TryGet(m.Key, out var raw), m.Key);
            return raw * m.Scale + m.Offset;
        }

        Assert.Equal(47.45, Nav(TelemetryNav.Latitude), 9);
        Assert.Equal(-122.31, Nav(TelemetryNav.Longitude), 9);
        Assert.Equal(304.8, Nav(TelemetryNav.Altitude), 6);
        Assert.Equal(30, Nav(TelemetryNav.Roll), 9);       // Tacview: right wing down positive
        Assert.Equal(5, Nav(TelemetryNav.Pitch), 9);       // Tacview: nose up positive
        Assert.Equal(270, Nav(TelemetryNav.Yaw), 9);
        Assert.Equal(268, Nav(TelemetryNav.Heading), 9);
        Assert.Equal(51.4444, Nav(TelemetryNav.Ias), 3);
        Assert.Equal(56.5888, Nav(TelemetryNav.Tas), 3);
        Assert.Equal(0.17, Nav(TelemetryNav.Mach), 9);
        Assert.Equal(4, Nav(TelemetryNav.Aoa), 9);
        Assert.Equal(-1, Nav(TelemetryNav.Aos), 9);
        Assert.Equal(152.4, Nav(TelemetryNav.Agl), 6);
        Assert.Equal(1.2, Nav(TelemetryNav.VerticalG), 9);
        Assert.Equal(0.75, Nav(TelemetryNav.Throttle), 9);
        Assert.Equal(1, Nav(TelemetryNav.Gear), 9);
        Assert.Equal(0.5, Nav(TelemetryNav.Flaps), 9);
        Assert.Equal(0, Nav(TelemetryNav.Airbrakes), 9);
        Assert.Equal(45.359237, Nav(TelemetryNav.FuelKg), 6);
        Assert.Equal(3.048, Nav(TelemetryNav.VerticalSpeed), 6);

        var keys = MsfsSimVars.All.Select(v => v.Key).ToHashSet();
        Assert.All(source.NavMappings, m => Assert.Contains(m.Key, keys));
    }

    [Fact]
    public void Raw_IsCopiedOnlyWhileSomeoneListens()
    {
        var (source, _, _) = Connected();
        Assert.False(source.WantsRaw);
        source.Handle(Fast(Position));

        var raw = new List<TelemetryRawPacket>();
        source.RawReceived += raw.Add;
        var message = Fast(("G FORCE", 1.3));
        source.Handle(message);
        Assert.Single(raw);
        Assert.Equal("simconnect", raw[0].Stream);
        Assert.Equal(message, raw[0].Data);

        source.RawReceived -= raw.Add;
        source.Handle(Fast(("G FORCE", 1.4)));
        Assert.Single(raw);
    }

    [Fact]
    public void UnknownSimVar_IsDropped()
    {
        var (source, client, _) = Connected();
        var stall = client.Definitions.Single(d => d.Name == "STALL WARNING");
        source.Handle(Fast([.. Position, ("STALL WARNING", 1)]));
        source.Handle(SimMsg.Exception(SimConnectLayout.ExceptionNameUnrecognized, stall.Packet, 4));
        Assert.Contains("STALL WARNING", source.RejectedSimVars);
        source.Handle(Fast(("G FORCE", 1)));
        Assert.False(source.Latest!.TryGet(K("STALL WARNING"), out _));
    }

    [Fact]
    public void Traffic_BecomesOneObjectsEventPerSecond_WithoutTheUserAircraft()
    {
        var (source, client, clock) = Connected();
        var events = new List<TelemetryEvent>();
        source.EventRaised += events.Add;
        source.Handle(Fast(Position));       // the user aircraft is object 1

        clock.Advance(1);
        Assert.True(source.Tick());
        Assert.Equal(2, client.TypeRequests.Count);
        Assert.Contains(client.TypeRequests, r => r.Type == SimConnectSimObjectType.Aircraft && r.Radius == 50_000 && r.Define == MsfsTelemetrySource.DefineObjects);
        Assert.Contains(client.TypeRequests, r => r.Type == SimConnectSimObjectType.Helicopter);

        source.Handle(SimMsg.ByType(MsfsTelemetrySource.RequestObjectsAircraft, 1, 1, 2, 47.45, -122.31, 1000, 0, 0, 0, "Me", ""));
        source.Handle(SimMsg.ByType(MsfsTelemetrySource.RequestObjectsAircraft, 412, 2, 2, 47.5, -122.3, 3000, -20, -3, 90, "Boeing 737|MAX", "N737JP"));
        Assert.DoesNotContain(events, e => e.Kind == TelemetryEventKinds.Objects);   // helicopters not answered yet
        source.Handle(SimMsg.ByType(MsfsTelemetrySource.RequestObjectsHelicopters, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", ""));

        var snapshot = Assert.Single(events, e => e.Kind == TelemetryEventKinds.Objects);
        var line = Assert.Single(snapshot.Text.Split('\n'));
        var f = line.Split('|');
        Assert.Equal(12, f.Length);
        Assert.Equal("412", f[0]);
        Assert.Equal("Air+FixedWing", f[1]);
        Assert.Equal("Boeing 737/MAX (N737JP)", f[2]);
        Assert.Equal("47.5", f[4]);
        Assert.Equal("914.4", f[6]);
        Assert.Equal("20", f[7]);     // roll: right wing down positive
        Assert.Equal("3", f[8]);      // pitch: nose up positive
        Assert.Equal("90", f[9]);
    }

    [Fact]
    public void FastInterval_AdaptsToTheMeasuredSimFrameRate()
    {
        var (source, client, clock) = Connected(20);
        Assert.Equal(1u, source.FastInterval);
        source.Handle(Fast(Position));
        for (var i = 0; i < 180; i++)          // 60 messages per second at interval 1 = a 120 fps sim
            source.Handle(Fast(("G FORCE", 1 + i * 0.001)));
        clock.Advance(3);
        Assert.True(source.Tick());
        Assert.Equal(5u, source.FastInterval);
        var last = client.Requests.Last(r => r.Request == MsfsTelemetrySource.RequestFast);
        Assert.Equal(5u, last.Interval);
    }

    [Fact]
    public void Tick_ReportsADeadConnection()
    {
        var (source, client, clock) = Connected();
        clock.Advance(5);
        Assert.True(source.Tick());
        Assert.Contains("Sim", client.StateRequests.Skip(3));     // heartbeat
        clock.Advance(MsfsTelemetrySource.SilenceTimeoutSeconds + 1);
        Assert.False(source.Tick());
    }

    [Fact]
    public void Feed_RetriesUntilTheSimAnswers_EndsTheSessionOnQuit_AndReconnects()
    {
        var clock = new ManualClock(1);
        var client = new FakeSimConnect { Answering = false };
        var source = new MsfsTelemetrySource(clock, "Microsoft Flight Simulator 2024");
        using var feed = new MsfsTelemetryFeed(source, () => (client, false, null), retrySeconds: 0.05);
        var events = new ConcurrentQueue<TelemetryEvent>();
        source.EventRaised += events.Enqueue;
        Assert.Equal(0, client.Opens);
        feed.Start();

        Assert.True(SpinWait.SpinUntil(() => feed.Problem?.Contains("does not answer SimConnect", StringComparison.Ordinal) == true, 5000));
        Assert.False(feed.IsConnected);
        client.Answering = true;
        Assert.True(SpinWait.SpinUntil(() => feed.IsConnected && client.Subscriptions.Count > 0, 5000));
        Assert.Null(feed.Problem);
        Assert.Equal("SimConnect (fake)", feed.Endpoint);

        client.Push(SimMsg.Open("SunRise", 1, 0, 0, 0));
        client.Push(SimMsg.SystemState(MsfsTelemetrySource.RequestStateSim, 1, ""));
        client.Push(Fast(Position));
        Assert.True(SpinWait.SpinUntil(() => source.Latest is not null, 5000));
        var definitions = client.Definitions.Count;

        client.Push(SimMsg.Quit());
        Assert.True(SpinWait.SpinUntil(() => events.Any(e => e.Kind == TelemetryEventKinds.SessionEnd), 5000));
        Assert.Equal("the sim closed SimConnect", events.Last(e => e.Kind == TelemetryEventKinds.SessionEnd).Text);
        Assert.True(SpinWait.SpinUntil(() => feed.Connections == 2 && client.Definitions.Count == 2 * definitions, 5000));
        Assert.True(client.Closes >= 1);

        feed.Stop();
        Assert.False(feed.IsConnected);
        var opens = client.Opens;
        Thread.Sleep(150);
        Assert.Equal(opens, client.Opens);     // nothing after Stop
    }

    [Fact]
    public void Feeds_CreateMakesTheMsfsFeed_AndNamesAMissingDll()
    {
        var fake = new FakeSimConnect();
        using (var feed = TelemetryFeeds.Create("MSFS2020", new ManualClock(), new TelemetryFeedOptions { SimConnectClient = fake }))
        {
            Assert.IsType<MsfsTelemetryFeed>(feed);
            Assert.Equal("Microsoft Flight Simulator 2020", feed.Source.Name);
            Assert.Equal(0, fake.Opens);      // nothing before Start
        }

        var missing = Path.Combine(Path.GetTempPath(), "joypro-no-simconnect-" + Guid.NewGuid().ToString("N"), "SimConnect.dll");
        using var native = TelemetryFeeds.Create("msfs", new ManualClock(), new TelemetryFeedOptions { SimConnectDll = missing });
        Assert.Equal("Microsoft Flight Simulator 2024", native.Source.Name);
        native.Start();
        Assert.True(SpinWait.SpinUntil(() => native.Problem is not null, 5000));
        Assert.StartsWith("SimConnect.dll not found in ", native.Problem);
        Assert.Contains(Path.GetDirectoryName(missing)!, native.Problem);
        native.Stop();
    }

    [Fact]
    public void Locator_UsesTheConfiguredFileOrFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "joypro-simconnect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var dll = Path.Combine(folder, "SimConnect.dll");
            Assert.Null(new SimConnectLocator("MSFS2024", folder, null).Find());
            File.WriteAllBytes(dll, [0]);
            Assert.Equal(dll, new SimConnectLocator("MSFS2024", folder, null).Find());
            Assert.Equal(dll, new SimConnectLocator("MSFS2020", dll, null).Find());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // ------------------------------------------------------------------ native layout

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Recv
    {
        public uint dwSize, dwVersion, dwID;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RecvException
    {
        public Recv Header;
        public uint dwException, dwSendID, dwIndex;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct RecvOpen
    {
        public Recv Header;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] szApplicationName;
        public uint dwApplicationVersionMajor, dwApplicationVersionMinor, dwApplicationBuildMajor, dwApplicationBuildMinor;
        public uint dwSimConnectVersionMajor, dwSimConnectVersionMinor, dwSimConnectBuildMajor, dwSimConnectBuildMinor;
        public uint dwReserved1, dwReserved2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RecvEvent
    {
        public Recv Header;
        public uint uGroupID, uEventID, dwData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RecvEventFilename
    {
        public RecvEvent Event;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 260)] public byte[] szFileName;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RecvSimObjectData
    {
        public Recv Header;
        public uint dwRequestID, dwObjectID, dwDefineID, dwFlags, dwentrynumber, dwoutof, dwDefineCount, dwData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RecvSystemState
    {
        public Recv Header;
        public uint dwRequestID, dwInteger;
        public float fFloat;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 260)] public byte[] szString;
    }

    private static int Off<T>(string field) => (int)Marshal.OffsetOf<T>(field);

    [Fact]
    public void NativeLayout_MatchesSimConnectH()
    {
        Assert.Equal(SimConnectLayout.HeaderSize, Marshal.SizeOf<Recv>());
        Assert.Equal(SimConnectLayout.Id, Off<Recv>(nameof(Recv.dwID)));
        Assert.Equal(SimConnectLayout.ExceptionSendId, Off<RecvException>(nameof(RecvException.dwSendID)));
        Assert.Equal(SimConnectLayout.ExceptionIndex, Off<RecvException>(nameof(RecvException.dwIndex)));
        Assert.Equal(SimConnectLayout.ExceptionSize, Marshal.SizeOf<RecvException>());
        Assert.Equal(SimConnectLayout.OpenApplicationName, Off<RecvOpen>(nameof(RecvOpen.szApplicationName)));
        Assert.Equal(SimConnectLayout.OpenVersionMajor, Off<RecvOpen>(nameof(RecvOpen.dwApplicationVersionMajor)));
        Assert.Equal(SimConnectLayout.OpenBuildMinor, Off<RecvOpen>(nameof(RecvOpen.dwApplicationBuildMinor)));
        Assert.Equal(SimConnectLayout.OpenSimConnectVersionMajor, Off<RecvOpen>(nameof(RecvOpen.dwSimConnectVersionMajor)));
        Assert.Equal(SimConnectLayout.OpenSize, Marshal.SizeOf<RecvOpen>());
        Assert.Equal(SimConnectLayout.EventId, Off<RecvEvent>(nameof(RecvEvent.uEventID)));
        Assert.Equal(SimConnectLayout.EventData, Off<RecvEvent>(nameof(RecvEvent.dwData)));
        Assert.Equal(SimConnectLayout.EventSize, Marshal.SizeOf<RecvEvent>());
        Assert.Equal(SimConnectLayout.EventFileName, Off<RecvEventFilename>(nameof(RecvEventFilename.szFileName)));
        Assert.Equal(SimConnectLayout.EventFilenameFlags, Off<RecvEventFilename>(nameof(RecvEventFilename.dwFlags)));
        Assert.Equal(SimConnectLayout.EventFilenameSize, Marshal.SizeOf<RecvEventFilename>());
        Assert.Equal(SimConnectLayout.DataRequestId, Off<RecvSimObjectData>(nameof(RecvSimObjectData.dwRequestID)));
        Assert.Equal(SimConnectLayout.DataObjectId, Off<RecvSimObjectData>(nameof(RecvSimObjectData.dwObjectID)));
        Assert.Equal(SimConnectLayout.DataEntryNumber, Off<RecvSimObjectData>(nameof(RecvSimObjectData.dwentrynumber)));
        Assert.Equal(SimConnectLayout.DataOutOf, Off<RecvSimObjectData>(nameof(RecvSimObjectData.dwoutof)));
        Assert.Equal(SimConnectLayout.DataDefineCount, Off<RecvSimObjectData>(nameof(RecvSimObjectData.dwDefineCount)));
        Assert.Equal(SimConnectLayout.DataStart, Off<RecvSimObjectData>(nameof(RecvSimObjectData.dwData)));
        Assert.Equal(SimConnectLayout.StateInteger, Off<RecvSystemState>(nameof(RecvSystemState.dwInteger)));
        Assert.Equal(SimConnectLayout.StateRequestId, Off<RecvSystemState>(nameof(RecvSystemState.dwRequestID)));
        Assert.Equal(SimConnectLayout.StateFloat, Off<RecvSystemState>(nameof(RecvSystemState.fFloat)));
        Assert.Equal(SimConnectLayout.StateString, Off<RecvSystemState>(nameof(RecvSystemState.szString)));
        Assert.Equal(SimConnectLayout.StateSize, Marshal.SizeOf<RecvSystemState>());
        Assert.Equal(40 + 6 * 8 + 2 * 256, SimConnectLayout.DataStart + MsfsTelemetrySource.ObjectDataSize);
        Assert.Equal(4u, (uint)SimConnectDataType.Float64);
        Assert.Equal(9u, (uint)SimConnectDataType.String256);
        Assert.Equal(3u, (uint)SimConnectPeriod.SimFrame);
        Assert.Equal(8u, (uint)SimConnectRecvId.SimObjectData);
        Assert.Equal(9u, (uint)SimConnectRecvId.SimObjectDataByType);
        Assert.Equal(15u, (uint)SimConnectRecvId.SystemState);
    }

    [Fact]
    public void ShortOrTruncatedMessages_AreHarmless()
    {
        var (source, _, _) = Connected();
        Assert.Equal(MsfsTelemetrySource.MessageResult.Ignored, source.Handle(new byte[5]));
        var message = Fast([.. Position, ("G FORCE", 1)]);
        source.Handle(message.AsSpan(0, message.Length - 7));     // the last tagged value is cut off
        Assert.False(source.Latest!.TryGet(K("G FORCE"), out _));
        source.Handle(SimMsg.Header(SimConnectRecvId.EventFilename, 30));
        source.Handle(SimMsg.Header((SimConnectRecvId)99, 12));
    }
}

/// <summary>Builds SimConnect messages in the native byte layout.</summary>
internal static class SimMsg
{
    public static byte[] Header(SimConnectRecvId id, int size)
    {
        var data = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(SimConnectLayout.Size), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(SimConnectLayout.Version), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(SimConnectLayout.Id), (uint)id);
        return data;
    }

    private static void U32(byte[] data, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);

    private static void Text(byte[] data, int at, string text, int length)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Array.Copy(bytes, 0, data, at, Math.Min(bytes.Length, length - 1));
    }

    public static byte[] Quit() => Header(SimConnectRecvId.Quit, SimConnectLayout.HeaderSize);

    public static byte[] Open(string app, uint major, uint minor, uint buildMajor, uint buildMinor)
    {
        var data = Header(SimConnectRecvId.Open, SimConnectLayout.OpenSize);
        Text(data, SimConnectLayout.OpenApplicationName, app, SimConnectLayout.OpenApplicationNameLength);
        U32(data, SimConnectLayout.OpenVersionMajor, major);
        U32(data, SimConnectLayout.OpenVersionMinor, minor);
        U32(data, SimConnectLayout.OpenBuildMajor, buildMajor);
        U32(data, SimConnectLayout.OpenBuildMinor, buildMinor);
        U32(data, SimConnectLayout.OpenSimConnectVersionMajor, 12);
        return data;
    }

    public static byte[] Exception(uint code, uint sendId, uint index)
    {
        var data = Header(SimConnectRecvId.Exception, SimConnectLayout.ExceptionSize);
        U32(data, SimConnectLayout.ExceptionCode, code);
        U32(data, SimConnectLayout.ExceptionSendId, sendId);
        U32(data, SimConnectLayout.ExceptionIndex, index);
        return data;
    }

    public static byte[] Event(uint eventId, uint value)
    {
        var data = Header(SimConnectRecvId.Event, SimConnectLayout.EventSize);
        U32(data, SimConnectLayout.EventGroupId, SimConnectLayout.UnknownGroup);
        U32(data, SimConnectLayout.EventId, eventId);
        U32(data, SimConnectLayout.EventData, value);
        return data;
    }

    public static byte[] EventFilename(uint eventId, string file)
    {
        var data = Header(SimConnectRecvId.EventFilename, SimConnectLayout.EventFilenameSize);
        U32(data, SimConnectLayout.EventGroupId, SimConnectLayout.UnknownGroup);
        U32(data, SimConnectLayout.EventId, eventId);
        Text(data, SimConnectLayout.EventFileName, file, SimConnectLayout.MaxPath);
        return data;
    }

    public static byte[] SystemState(uint requestId, uint integer, string text)
    {
        var data = Header(SimConnectRecvId.SystemState, SimConnectLayout.StateSize);
        U32(data, SimConnectLayout.StateRequestId, requestId);
        U32(data, SimConnectLayout.StateInteger, integer);
        Text(data, SimConnectLayout.StateString, text, SimConnectLayout.MaxPath);
        return data;
    }

    private static byte[] Data(SimConnectRecvId id, uint requestId, uint objectId, uint defineId, uint entry, uint outOf, uint count, int payload)
    {
        var data = Header(id, SimConnectLayout.DataStart + payload);
        U32(data, SimConnectLayout.DataRequestId, requestId);
        U32(data, SimConnectLayout.DataObjectId, objectId);
        U32(data, SimConnectLayout.DataDefineId, defineId);
        U32(data, SimConnectLayout.DataFlags, (uint)(SimConnectRequestFlags.Changed | SimConnectRequestFlags.Tagged));
        U32(data, SimConnectLayout.DataEntryNumber, entry);
        U32(data, SimConnectLayout.DataOutOf, outOf);
        U32(data, SimConnectLayout.DataDefineCount, count);
        return data;
    }

    public static byte[] Tagged(uint requestId, uint objectId, uint defineId, (int Datum, double Value)[] values)
    {
        var data = Data(SimConnectRecvId.SimObjectData, requestId, objectId, defineId, 1, 1, (uint)values.Length, values.Length * 12);
        var at = SimConnectLayout.DataStart;
        foreach (var (datum, value) in values)
        {
            U32(data, at, (uint)datum);
            BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(at + 4), value);
            at += 12;
        }

        return data;
    }

    public static byte[] TaggedStrings(uint requestId, params (int Datum, string Text)[] values)
    {
        const int stride = 4 + SimConnectLayout.StringLength;
        var data = Data(SimConnectRecvId.SimObjectData, requestId, 1, MsfsTelemetrySource.DefineStrings, 1, 1, (uint)values.Length, values.Length * stride);
        var at = SimConnectLayout.DataStart;
        foreach (var (datum, text) in values)
        {
            U32(data, at, (uint)datum);
            Text(data, at + 4, text, SimConnectLayout.StringLength);
            at += stride;
        }

        return data;
    }

    public static byte[] ByType(uint requestId, uint objectId, uint entry, uint outOf, double lat, double lon, double altFt, double bank, double pitch, double heading, string title, string atcId)
    {
        var data = Data(SimConnectRecvId.SimObjectDataByType, requestId, objectId, MsfsTelemetrySource.DefineObjects, entry, outOf, 8, MsfsTelemetrySource.ObjectDataSize);
        var at = SimConnectLayout.DataStart;
        foreach (var value in new[] { lat, lon, altFt, bank, pitch, heading })
        {
            BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(at), value);
            at += 8;
        }

        Text(data, at, title, SimConnectLayout.StringLength);
        Text(data, at + SimConnectLayout.StringLength, atcId, SimConnectLayout.StringLength);
        return data;
    }
}

/// <summary>A SimConnect stand-in: records every call, hands out queued messages, sets the data-ready handle.</summary>
internal sealed class FakeSimConnect : ISimConnectClient
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<byte[]> _messages = new();
    private readonly List<(uint Define, string Name, string? Units, SimConnectDataType Type, uint Datum, uint Packet)> _definitions = new();
    private readonly List<(uint Request, uint Define, uint Object, SimConnectPeriod Period, SimConnectRequestFlags Flags, uint Interval)> _requests = new();
    private readonly List<(uint Request, uint Define, uint Radius, SimConnectSimObjectType Type)> _typeRequests = new();
    private readonly List<string> _subscriptions = new();
    private readonly List<string> _states = new();
    private EventWaitHandle? _ready;
    private uint _packet;
    private byte[]? _current;
    private volatile bool _open;
    private int _opens;
    private int _closes;

    public volatile bool Answering = true;

    public string Location => "fake";
    public int Opens => Volatile.Read(ref _opens);
    public int Closes => Volatile.Read(ref _closes);

    public List<(uint Define, string Name, string? Units, SimConnectDataType Type, uint Datum, uint Packet)> Definitions
    {
        get { lock (_gate) return [.. _definitions]; }
    }

    public List<(uint Request, uint Define, uint Object, SimConnectPeriod Period, SimConnectRequestFlags Flags, uint Interval)> Requests
    {
        get { lock (_gate) return [.. _requests]; }
    }

    public List<(uint Request, uint Define, uint Radius, SimConnectSimObjectType Type)> TypeRequests
    {
        get { lock (_gate) return [.. _typeRequests]; }
    }

    public List<string> Subscriptions
    {
        get { lock (_gate) return [.. _subscriptions]; }
    }

    public List<string> StateRequests
    {
        get { lock (_gate) return [.. _states]; }
    }

    public void Push(byte[] message)
    {
        _messages.Enqueue(message);
        _ready?.Set();
    }

    public bool Open(string applicationName, WaitHandle dataReady)
    {
        Interlocked.Increment(ref _opens);
        if (!Answering)
            return false;
        lock (_gate)
        {
            _subscriptions.Clear();
        }

        _ready = (EventWaitHandle)dataReady;
        _open = true;
        return true;
    }

    public void Close()
    {
        if (!_open)
            return;
        _open = false;
        Interlocked.Increment(ref _closes);
    }

    public bool AddToDataDefinition(uint defineId, string datumName, string? unitsName, SimConnectDataType type, uint datumId)
    {
        lock (_gate)
            _definitions.Add((defineId, datumName, unitsName, type, datumId, ++_packet));
        return _open;
    }

    public bool ClearDataDefinition(uint defineId) => _open;

    public uint LastSentPacketId()
    {
        lock (_gate)
            return _packet;
    }

    public bool RequestDataOnSimObject(uint requestId, uint defineId, uint objectId, SimConnectPeriod period, SimConnectRequestFlags flags, uint interval)
    {
        lock (_gate)
        {
            _requests.Add((requestId, defineId, objectId, period, flags, interval));
            ++_packet;
        }

        return _open;
    }

    public bool RequestDataOnSimObjectType(uint requestId, uint defineId, uint radiusMeters, SimConnectSimObjectType type)
    {
        lock (_gate)
        {
            _typeRequests.Add((requestId, defineId, radiusMeters, type));
            ++_packet;
        }

        return _open;
    }

    public bool SubscribeToSystemEvent(uint eventId, string systemEventName)
    {
        lock (_gate)
        {
            _subscriptions.Add(systemEventName);
            ++_packet;
        }

        return _open;
    }

    public bool RequestSystemState(uint requestId, string state)
    {
        lock (_gate)
        {
            _states.Add(state);
            ++_packet;
        }

        return _open;
    }

    public bool TryGetNextDispatch(out ReadOnlySpan<byte> message)
    {
        if (_open && _messages.TryDequeue(out _current))
        {
            message = _current;
            return true;
        }

        message = default;
        return false;
    }

    public void Dispose() => Close();
}
