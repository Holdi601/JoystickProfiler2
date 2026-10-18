using System.Text;
using JoyPro.Ffb.Core.Telemetry;
using JoyPro.Ffb.Core.Telemetry.Elite;

namespace JoyPro.Ffb.Core.Tests;

/// <summary>Elite Dangerous: a synthetic journal folder in temp, polled by hand (no timers) except for the Start/Stop test.</summary>
public sealed class EliteTelemetryTests : IDisposable
{
    private const string Journal1 = "Journal.2026-10-18T100000.01.log";
    private const string Journal2 = "Journal.2026-10-18T120000.01.log";

    private const string Header = """{ "timestamp":"2026-10-18T10:00:00Z", "event":"Fileheader", "part":1, "language":"English/UK", "Odyssey":true, "gameversion":"4.4.1.1", "build":"r332841/r0 " }""";
    private const string Commander = """{ "timestamp":"2026-10-18T10:00:05Z", "event":"Commander", "FID":"F0000001", "Name":"Test Pilot" }""";
    private const string LoadGame = """{ "timestamp":"2026-10-18T10:00:06Z", "event":"LoadGame", "FID":"F0000001", "Commander":"Test Pilot", "Horizons":true, "Odyssey":true, "Ship":"Krait_MkII", "Ship_Localised":"Krait Mk II", "ShipID":3, "ShipName":"Test Ship", "ShipIdent":"TS-01", "FuelLevel":32.0, "FuelCapacity":32.0, "GameMode":"Solo", "Credits":1000, "Loan":0, "language":"English/UK", "gameversion":"4.4.1.1", "build":"r332841/r0 " }""";
    private const string Location = """{ "timestamp":"2026-10-18T10:00:08Z", "event":"Location", "DistFromStarLS":12.5, "Docked":true, "StationName":"Test Port", "StationType":"Coriolis", "StarSystem":"Test System", "SystemAddress":12345, "StarPos":[10.5,-2.25,30.0] }""";
    private const string Loadout = """{ "timestamp":"2026-10-18T10:00:09Z", "event":"Loadout", "Ship":"krait_mkii", "ShipID":3, "ShipName":"Test Ship", "ShipIdent":"TS-01", "HullHealth":0.95, "Modules":[ { "Slot":"Armour", "Item":"krait_mkii_armour_grade1" } ] }""";
    private const string Undocked = """{ "timestamp":"2026-10-18T10:01:00Z", "event":"Undocked", "StationName":"Test Port", "MarketID":1 }""";
    private const string FsdJump = """{ "timestamp":"2026-10-18T10:05:00Z", "event":"FSDJump", "StarSystem":"Other System", "SystemAddress":6789, "StarPos":[20.0,1.0,-5.5], "JumpDist":14.2, "FuelUsed":2.1 }""";
    private const string HullDamage = """{ "timestamp":"2026-10-18T10:06:00Z", "event":"HullDamage", "Health":0.8, "PlayerPilot":true, "Fighter":false }""";
    private const string MainMenu = """{ "timestamp":"2026-10-18T10:10:00Z", "event":"Music", "MusicTrack":"MainMenu" }""";
    private const string Shutdown = """{ "timestamp":"2026-10-18T10:11:00Z", "event":"Shutdown" }""";

    private const string StatusMenu = """{ "timestamp":"2026-10-18T09:59:00Z", "event":"Status", "Flags":0 }""";

    // Docked (0), landing gear down (2), shields up (3), lights (8), in main ship (24); flags2 0.
    private const string StatusDocked = """{ "timestamp":"2026-10-18T10:00:10Z", "event":"Status", "Flags":16777485, "Flags2":0, "Pips":[4,8,0], "FireGroup":1, "GuiFocus":5, "Fuel":{ "FuelMain":30.5, "FuelReservoir":0.63 }, "Cargo":4.000000, "LegalState":"Clean", "Balance":1000 }""";

    // Shields up (3), supercruise (4), FA off (5), hardpoints (6), mass locked (16), has lat long (21), in main ship (24), bit 31;
    // flags2 glide mode (12) + breathable atmosphere (16).
    private const string StatusFlying = """{ "timestamp":"2026-10-18T10:02:00Z", "event":"Status", "Flags":2166423672, "Flags2":69632, "Pips":[2,6,4], "FireGroup":0, "GuiFocus":0, "Fuel":{ "FuelMain":29.0, "FuelReservoir":0.5 }, "Cargo":4.0, "LegalState":"Wanted", "Latitude":12.5, "Longitude":-45.25, "Altitude":1500, "Heading":270, "BodyName":"Test System 1 a", "PlanetRadius":2500000.0, "Balance":1200, "Destination":{ "System":12345, "Body":7, "Name":"Test Port" }, "Gravity":0.35, "SelectedWeapon":"$humanoid_fists_name;", "SelectedWeapon_Localised":"Unarmed", "Oxygen":1.0, "Health":0.9, "Temperature":293.5 }""";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "joypro-elite-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(100);
    private readonly List<TelemetryEvent> _events = [];
    private readonly List<TelemetryFrame> _frames = [];
    private DateTime _fileTime = new(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc);

    public EliteTelemetryTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private EliteTelemetryFeed MakeFeed()
    {
        var source = new EliteTelemetrySource(_clock);
        source.EventRaised += e => _events.Add(e);
        source.FrameReceived += f => _frames.Add(f);
        return new EliteTelemetryFeed(source, _folder);
    }

    /// <summary>Writes a file as the game does (whole rewrite) with a write time that always moves on.</summary>
    private void WriteFile(string name, string text)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        _fileTime = _fileTime.AddSeconds(1);
        File.SetLastWriteTimeUtc(path, _fileTime);
    }

    private void AppendJournal(string name, params string[] lines) => AppendRaw(name, string.Concat(lines.Select(l => l + "\r\n")));

    private void AppendRaw(string name, string text)
    {
        var path = Path.Combine(_folder, name);
        File.AppendAllText(path, text, new UTF8Encoding(false));
        _fileTime = _fileTime.AddSeconds(1);
        File.SetLastWriteTimeUtc(path, _fileTime);
    }

    private List<string> Kinds() => _events.Select(e => e.Kind).ToList();

    [Fact]
    public void Status_Rewrite_Publishes_Frames_With_Flags_Decoded()
    {
        AppendJournal(Journal1, Header, Commander, LoadGame, Location);
        WriteFile("Status.json", StatusDocked);
        using var feed = MakeFeed();
        feed.Poll();

        Assert.Null(feed.Problem);
        var docked = Assert.Single(_frames);           // the state at start: the journal says a session runs
        Assert.Equal(1, docked.Values["ed.flag.docked"]);
        Assert.Equal(1, docked.Values["ed.flag.gear_down"]);
        Assert.Equal(1, docked.Values["ed.flag.shields_up"]);
        Assert.Equal(1, docked.Values["ed.flag.lights"]);
        Assert.Equal(1, docked.Values["ed.flag.in_main_ship"]);
        Assert.Equal(0, docked.Values["ed.flag.supercruise"]);
        Assert.Equal(16777485, docked.Values["ed.flags"]);
        Assert.Equal(4, docked.Values["ed.pips.sys"]);
        Assert.Equal(8, docked.Values["ed.pips.eng"]);
        Assert.Equal(0, docked.Values["ed.pips.wep"]);
        Assert.Equal(30.5, docked.Values["ed.fuel_main"]);
        Assert.Equal(0.63, docked.Values["ed.fuel_reservoir"]);
        Assert.Equal(4, docked.Values["ed.cargo"]);
        Assert.Equal(5, docked.Values["ed.gui_focus"]);
        Assert.Equal(0, docked.Values["ed.legal_state"]);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 10, 0, 10, TimeSpan.Zero).ToUnixTimeSeconds(), docked.Values["ed.time_unix"]);
        Assert.Equal(10.5, docked.Values["ed.star_x"]);      // journal values ride along
        Assert.Equal(100, docked.Time);
        Assert.Equal(32 + 20, docked.Values.Keys.Count(k => k.StartsWith("ed.flag.", StringComparison.Ordinal) || k.StartsWith("ed.flag2.", StringComparison.Ordinal)));
        Assert.False(docked.Values.ContainsKey("ed.lat"));
        Assert.Equal("station_services", feed.Source.SessionInfo[EliteTelemetrySource.InfoGuiFocus]);
        Assert.Equal("ship", feed.Source.SessionInfo[EliteTelemetrySource.InfoVehicle]);

        _clock.Advance(0.1);
        WriteFile("Status.json", StatusFlying);
        feed.Poll();
        Assert.Equal(2, _frames.Count);
        var flying = _frames[1];
        Assert.Equal(100.1, flying.Time, 6);
        Assert.Equal(0, flying.Values["ed.flag.docked"]);
        Assert.Equal(0, flying.Values["ed.flag.gear_down"]);
        Assert.Equal(1, flying.Values["ed.flag.supercruise"]);
        Assert.Equal(1, flying.Values["ed.flag.fa_off"]);
        Assert.Equal(1, flying.Values["ed.flag.hardpoints"]);
        Assert.Equal(1, flying.Values["ed.flag.mass_locked"]);
        Assert.Equal(1, flying.Values["ed.flag.has_lat_long"]);
        Assert.Equal(1, flying.Values["ed.flag.srv_high_beam"]);   // bit 31 survives (the number is above int.MaxValue)
        Assert.Equal(2166423672, flying.Values["ed.flags"]);
        Assert.Equal(1, flying.Values["ed.flag2.glide_mode"]);
        Assert.Equal(1, flying.Values["ed.flag2.breathable_atmosphere"]);
        Assert.Equal(0, flying.Values["ed.flag2.on_foot"]);
        Assert.Equal(69632, flying.Values["ed.flags2"]);
        Assert.Equal(12.5, flying.Values["ed.lat"]);
        Assert.Equal(-45.25, flying.Values["ed.lon"]);
        Assert.Equal(1500, flying.Values["ed.alt"]);
        Assert.Equal(270, flying.Values["ed.heading"]);
        Assert.Equal(2500000, flying.Values["ed.planet_radius"]);
        Assert.Equal(0.35, flying.Values["ed.gravity"]);
        Assert.Equal(1, flying.Values["ed.oxygen"]);
        Assert.Equal(0.9, flying.Values["ed.health"]);
        Assert.Equal(293.5, flying.Values["ed.temperature"]);
        Assert.Equal(1200, flying.Values["ed.balance"]);
        Assert.Equal(12345, flying.Values["ed.dest.system"]);
        Assert.Equal(7, flying.Values["ed.dest.body"]);
        Assert.Equal(3, flying.Values["ed.legal_state"]);          // Wanted
        Assert.True(feed.Source.TryGet("ed.flag.supercruise", out var sc) && sc == 1);

        var info = feed.Source.SessionInfo;
        Assert.Equal("Test System 1 a", info[EliteTelemetrySource.InfoBody]);
        Assert.Equal("Wanted", info[EliteTelemetrySource.InfoLegalState]);
        Assert.Equal("Unarmed", info[EliteTelemetrySource.InfoSelectedWeapon]);
        Assert.Equal("Test Port", info[EliteTelemetrySource.InfoDestination]);
        Assert.Equal("none", info[EliteTelemetrySource.InfoGuiFocus]);
        Assert.Contains(_events, e => e.Kind == TelemetryEventKinds.Info && e.Text == "legal_state=Wanted");

        // The same content again (a rewrite with a new file time) is no new frame; Elite has no globe position.
        WriteFile("Status.json", StatusFlying);
        feed.Poll();
        Assert.Equal(2, _frames.Count);
        Assert.Empty(feed.Source.NavMappings);
    }

    [Fact]
    public void Half_Written_Status_Is_Read_Again_Next_Tick()
    {
        AppendJournal(Journal1, Header, LoadGame);
        WriteFile("Status.json", StatusMenu);
        using var feed = MakeFeed();
        feed.Poll();
        Assert.Empty(_frames);

        WriteFile("Status.json", StatusDocked[..40]);
        feed.Poll();
        Assert.Empty(_frames);

        // The game finished writing; same length checks do not matter, the content does.
        WriteFile("Status.json", StatusDocked);
        feed.Poll();
        Assert.Single(_frames);
    }

    [Fact]
    public void Menu_Status_Clears_The_Source()
    {
        AppendJournal(Journal1, Header, LoadGame);
        WriteFile("Status.json", StatusMenu);
        using var feed = MakeFeed();
        feed.Poll();
        WriteFile("Status.json", StatusDocked);
        feed.Poll();
        Assert.NotNull(feed.Source.Latest);
        WriteFile("Status.json", StatusMenu);
        feed.Poll();
        Assert.Null(feed.Source.Latest);
        Assert.Single(_frames);
    }

    [Fact]
    public void Journal_Append_Raises_Events_Session_And_Info()
    {
        AppendJournal(Journal1, Header);
        WriteFile("Status.json", StatusMenu);
        using var feed = MakeFeed();
        feed.Poll();
        Assert.Empty(_events);                       // at the main menu: nothing to say yet

        AppendJournal(Journal1, Commander, LoadGame, Location, Loadout);
        feed.Poll();
        var kinds = Kinds();
        Assert.Equal("ed.Commander", kinds[0]);
        var start = kinds.IndexOf(TelemetryEventKinds.SessionStart);
        Assert.True(start >= 0 && start < kinds.IndexOf("ed.LoadGame"), string.Join(",", kinds));
        Assert.True(kinds.IndexOf(TelemetryEventKinds.Aircraft) > kinds.IndexOf("ed.LoadGame"));
        Assert.Equal("Test Pilot, krait_mkii, Solo", _events[start].Text);
        Assert.Single(_events, e => e.Kind == TelemetryEventKinds.Aircraft);   // LoadGame and Loadout agree
        Assert.Contains(_events, e => e.Kind == TelemetryEventKinds.Map && e.Text == "Test System");
        Assert.Contains("ed.Location", kinds);
        Assert.Contains("ed.Loadout", kinds);

        var loadGame = _events.Single(e => e.Kind == "ed.LoadGame");
        Assert.StartsWith("{\"timestamp\":\"2026-10-18T10:00:06Z\",\"event\":\"LoadGame\"", loadGame.Text);
        Assert.DoesNotContain("\", \"", loadGame.Text);                        // compact

        var info = feed.Source.SessionInfo;
        Assert.Equal("krait_mkii", info[TelemetryInfoKeys.Aircraft]);
        Assert.Equal("Test Pilot", info[TelemetryInfoKeys.Pilot]);
        Assert.Equal("Test System", info[TelemetryInfoKeys.Map]);
        Assert.Equal("Solo", info[TelemetryInfoKeys.Mission]);
        Assert.Equal("4.4.1.1 r332841/r0", info[TelemetryInfoKeys.GameVersion]);
        Assert.Equal("Test Ship", info[EliteTelemetrySource.InfoShipName]);
        Assert.Equal("Test Port", info[EliteTelemetrySource.InfoStation]);
        Assert.True(feed.EliteSource.InSession);

        WriteFile("Status.json", StatusDocked);
        feed.Poll();
        Assert.Equal(0.95, _frames[^1].Values["ed.hull_health"]);

        // Journal values publish a frame of their own (Status.json may stay unchanged).
        _events.Clear();
        AppendJournal(Journal1, Undocked, FsdJump, HullDamage);
        feed.Poll();
        Assert.False(feed.Source.SessionInfo.ContainsKey(EliteTelemetrySource.InfoStation));
        Assert.Contains(_events, e => e.Kind == TelemetryEventKinds.Map && e.Text == "Other System");
        Assert.Equal(0.8, _frames[^1].Values["ed.hull_health"]);
        Assert.Equal(20, _frames[^1].Values["ed.star_x"]);
        Assert.Equal(14.2, _frames[^1].Values["ed.jump_dist"]);

        _events.Clear();
        AppendJournal(Journal1, MainMenu, Shutdown);
        feed.Poll();
        Assert.Equal(["ed.Music", TelemetryEventKinds.SessionEnd, "ed.Shutdown"], Kinds());
        Assert.Equal("MainMenu", _events[1].Text);
        Assert.False(feed.EliteSource.InSession);
        Assert.Null(feed.Source.Latest);
    }

    [Fact]
    public void Long_Journal_Lines_Are_Capped()
    {
        AppendJournal(Journal1, Header);
        using var feed = MakeFeed();
        feed.Poll();
        var big = "{ \"timestamp\":\"2026-10-18T10:00:00Z\", \"event\":\"Market\", \"Items\":[" + string.Join(",", Enumerable.Range(0, 2000).Select(i => "{ \"id\":" + i + " }")) + "] }";
        AppendJournal(Journal1, big);
        feed.Poll();
        var market = Assert.Single(_events, e => e.Kind == "ed.Market");
        Assert.Equal(EliteTelemetryProtocol.MaxEventText, market.Text.Length);
        Assert.EndsWith("...", market.Text);
    }

    [Fact]
    public void No_Old_Events_At_Start_But_The_Current_Context()
    {
        AppendJournal(Journal1, Header, Commander, LoadGame, Location, Loadout, Undocked, FsdJump, HullDamage,
            """{ "timestamp":"2026-10-18T10:07:00Z", "event":"ReceiveText", "From":"Someone", "Message":"hello", "Channel":"local" }""");
        WriteFile("Status.json", StatusFlying);
        WriteFile("NavRoute.json", """{ "timestamp":"2026-10-18T10:00:00Z", "event":"NavRoute", "Route":[] }""");
        using var feed = MakeFeed();
        feed.Poll();

        Assert.DoesNotContain(_events, e => e.Kind.StartsWith("ed.", StringComparison.Ordinal));
        Assert.DoesNotContain(_events, e => e.Kind == TelemetryEventKinds.Message);
        Assert.Equal(TelemetryEventKinds.SessionStart, _events[0].Kind);
        Assert.Equal("in progress", _events[0].Text);
        Assert.Single(_events, e => e.Kind == TelemetryEventKinds.SessionStart);
        Assert.Contains(_events, e => e.Kind == TelemetryEventKinds.Aircraft && e.Text == "krait_mkii");
        Assert.Contains(_events, e => e.Kind == TelemetryEventKinds.Map && e.Text == "Other System");
        Assert.Equal("Test Pilot", feed.Source.SessionInfo[TelemetryInfoKeys.Pilot]);
        Assert.False(feed.Source.SessionInfo.ContainsKey(EliteTelemetrySource.InfoStation));
        var frame = Assert.Single(_frames);
        Assert.Equal(0.8, frame.Values["ed.hull_health"]);
        Assert.Equal(-5.5, frame.Values["ed.star_z"]);

        // Later polls raise only what is new.
        _clock.Advance(EliteTelemetryFeed.SlowCheckSeconds + 0.1);
        feed.Poll();
        Assert.DoesNotContain(_events, e => e.Kind.StartsWith("ed.", StringComparison.Ordinal));
        AppendJournal(Journal1, """{ "timestamp":"2026-10-18T10:08:00Z", "event":"SupercruiseEntry", "StarSystem":"Other System" }""");
        feed.Poll();
        Assert.Equal("ed.SupercruiseEntry", Assert.Single(_events, e => e.Kind.StartsWith("ed.", StringComparison.Ordinal)).Kind);
    }

    [Fact]
    public void Ended_Session_At_Start_Gives_No_Frame_And_No_Facts()
    {
        AppendJournal(Journal1, Header, Commander, LoadGame, Location, Shutdown);
        WriteFile("Status.json", StatusDocked);       // left over: the game is gone
        using var feed = MakeFeed();
        feed.Poll();
        Assert.Empty(_events);
        Assert.Empty(_frames);
        Assert.Empty(feed.Source.SessionInfo);
        Assert.False(feed.EliteSource.InSession);
    }

    [Fact]
    public void Partial_Line_Waits_Until_Complete()
    {
        AppendJournal(Journal1, Header);
        using var feed = MakeFeed();
        feed.Poll();

        AppendRaw(Journal1, FsdJump[..50]);
        feed.Poll();
        _clock.Advance(EliteTelemetryFeed.SlowCheckSeconds + 0.1);
        feed.Poll();
        Assert.Empty(_events);

        AppendRaw(Journal1, FsdJump[50..] + "\r\n");
        feed.Poll();
        Assert.Single(_events, e => e.Kind == "ed.FSDJump");
    }

    [Fact]
    public void Newer_Journal_Is_Followed()
    {
        AppendJournal(Journal1, Header, Commander, LoadGame);
        using var feed = MakeFeed();
        feed.Poll();
        Assert.EndsWith(Journal1, feed.CurrentJournal);
        _events.Clear();

        // The old journal gets a last line, then the game restarts (crash: no Shutdown) and opens a new journal.
        AppendJournal(Journal1, Location);
        AppendJournal(Journal2, Header, Commander, LoadGame);
        _clock.Advance(EliteTelemetryFeed.JournalRescanSeconds + 0.1);
        feed.Poll();
        Assert.EndsWith(Journal2, feed.CurrentJournal);
        var kinds = Kinds();
        Assert.Equal("ed.Location", kinds[0]);                    // the rest of the old one first
        var fileheader = kinds.IndexOf("ed.Fileheader");
        Assert.True(fileheader > 0);
        Assert.Equal(TelemetryEventKinds.SessionEnd, kinds[fileheader + 1]);
        Assert.Equal("new journal", _events[fileheader + 1].Text);
        Assert.Single(_events, e => e.Kind == TelemetryEventKinds.SessionStart);
        Assert.True(feed.EliteSource.InSession);

        AppendJournal(Journal2, Undocked);
        feed.Poll();
        Assert.Equal("ed.Undocked", _events[^1].Kind);
    }

    [Fact]
    public void Journal_Order_Uses_The_Time_In_The_Name()
    {
        var newStyle = EliteTelemetryFeed.JournalKey("Journal.2026-10-11T101337.01.log");
        var oldStyle = EliteTelemetryFeed.JournalKey("Journal.210101120000.01.log");   // 2021: sorts after "2026" by name
        var part2 = EliteTelemetryFeed.JournalKey("Journal.2026-10-11T101337.02.log");
        Assert.True(newStyle.CompareTo(oldStyle) > 0);
        Assert.True(part2.CompareTo(newStyle) > 0);
        Assert.Equal(new DateTime(2021, 1, 1, 12, 0, 0), oldStyle.Time);

        AppendJournal("Journal.210101120000.01.log", Header);
        AppendJournal(Journal1, Header);
        using var feed = MakeFeed();
        feed.Poll();
        Assert.EndsWith(Journal1, feed.CurrentJournal);
    }

    [Fact]
    public void Raw_Only_With_A_Listener()
    {
        AppendJournal(Journal1, Header, LoadGame);
        WriteFile("Status.json", StatusMenu);
        WriteFile("Cargo.json", """{ "timestamp":"2026-10-18T10:00:00Z", "event":"Cargo", "Vessel":"Ship", "Count":0, "Inventory":[] }""");
        using var feed = MakeFeed();
        feed.Poll();
        Assert.False(feed.Source.WantsRaw);

        WriteFile("Status.json", StatusDocked);
        AppendJournal(Journal1, Location);
        feed.Poll();
        Assert.Contains(_events, e => e.Kind == "ed.Location");      // events flow without a listener

        var raw = new List<TelemetryRawPacket>();
        feed.Source.RawReceived += raw.Add;
        Assert.True(feed.Source.WantsRaw);
        _clock.Advance(EliteTelemetryFeed.SlowCheckSeconds + 0.1);
        WriteFile("Status.json", StatusFlying);
        AppendJournal(Journal1, Undocked);
        const string route = """{ "timestamp":"2026-10-18T10:03:00Z", "event":"NavRoute", "Route":[ { "StarSystem":"Test System", "SystemAddress":12345, "StarPos":[0,0,0], "StarClass":"K" } ] }""";
        WriteFile("NavRoute.json", route);
        feed.Poll();

        var journal = Assert.Single(raw, p => p.Stream == EliteTelemetrySource.JournalRawStream);
        Assert.Equal(Undocked, Encoding.UTF8.GetString(journal.Data));
        var status = Assert.Single(raw, p => p.Stream == EliteTelemetrySource.StatusRawStream);
        Assert.Equal(StatusFlying, Encoding.UTF8.GetString(status.Data));
        var navRoute = Assert.Single(raw, p => p.Stream == "file:NavRoute.json");
        Assert.Equal(route, Encoding.UTF8.GetString(navRoute.Data));
        Assert.Equal(_clock.Now, navRoute.Time);
        var routeEvent = Assert.Single(_events, e => e.Kind == "ed.file.NavRoute");
        Assert.Contains("\"StarClass\":\"K\"", routeEvent.Text);
        Assert.DoesNotContain(raw, p => p.Stream == "file:Cargo.json");   // unchanged since start
        Assert.DoesNotContain(_events, e => e.Kind == "ed.file.Cargo");
    }

    [Fact]
    public void Heartbeat_Repeats_The_Last_State_In_A_Session()
    {
        AppendJournal(Journal1, Header, LoadGame);
        WriteFile("Status.json", StatusDocked);
        using var feed = MakeFeed();
        feed.Poll();
        Assert.Single(_frames);
        _clock.Advance(0.5);
        feed.Poll();
        Assert.Single(_frames);
        _clock.Advance(0.6);
        feed.Poll();
        Assert.Equal(2, _frames.Count);
        Assert.Equal(_frames[0].Values["ed.time_unix"], _frames[1].Values["ed.time_unix"]);   // a repeat, not a new state
        Assert.Equal(101.1, _frames[1].Time, 6);

        AppendJournal(Journal1, Shutdown);
        feed.Poll();
        _clock.Advance(5);
        feed.Poll();
        Assert.Equal(2, _frames.Count);
    }

    [Fact]
    public void Problems_Name_The_Missing_Folder_And_Status()
    {
        var missing = Path.Combine(_folder, "nope");
        using (var feed = new EliteTelemetryFeed(new EliteTelemetrySource(_clock), missing))
        {
            feed.Poll();
            Assert.Contains("folder not found", feed.Problem);
            Assert.Contains(missing, feed.Problem);
            Assert.Equal(missing, feed.Endpoint);
        }

        using (var feed = MakeFeed())
        {
            feed.Poll();
            Assert.Contains("Status.json missing", feed.Problem);
            WriteFile("Status.json", StatusMenu);
            feed.Poll();
            Assert.Null(feed.Problem);
        }
    }

    [Fact]
    public void Factory_Creates_The_Elite_Feed()
    {
        using var feed = TelemetryFeeds.Create("elite", _clock, new TelemetryFeedOptions { EliteFolder = _folder, PollHz = 50 });
        var elite = Assert.IsType<EliteTelemetryFeed>(feed);
        Assert.Equal(_folder, elite.Folder);
        Assert.Equal(10, elite.PollHz);
        Assert.Equal("Elite Dangerous", feed.Source.Name);
        Assert.Contains(feed.Source.Descriptors, d => d.Key == "ed.flag.cargo_scoop");
        Assert.Contains(feed.Source.Descriptors, d => d.Key == "ed.flag2.on_foot");
        Assert.EndsWith(Path.Combine("Frontier Developments", "Elite Dangerous"), EliteTelemetryProtocol.DefaultFolder());
    }

    [Fact]
    public void Snake_Case_Keys()
    {
        Assert.Equal("planet_radius", EliteTelemetryProtocol.Snake("PlanetRadius"));
        Assert.Equal("fuel_main", EliteTelemetryProtocol.Snake("FuelMain"));
        Assert.Equal("srv_type", EliteTelemetryProtocol.Snake("SRVType"));
        Assert.Equal("balance", EliteTelemetryProtocol.Snake("Balance"));
        var values = new Dictionary<string, double>();
        var texts = new Dictionary<string, string?>();
        Assert.True(EliteTelemetrySource.ParseStatus("""{ "timestamp":"2026-10-18T10:00:00Z", "event":"Status", "Flags":16777216, "NewThing":3, "Nested":{ "InnerValue":true } }"""u8, values, texts));
        Assert.Equal(3, values["ed.new_thing"]);
        Assert.Equal(1, values["ed.nested.inner_value"]);
        Assert.False(EliteTelemetrySource.ParseStatus("{ \"timestamp\":\"2026"u8, values, texts));
        Assert.False(EliteTelemetrySource.ParseStatus(""u8, values, texts));
    }

    [Fact]
    public void Start_Polls_On_A_Timer_And_Stop_Ends_It()
    {
        AppendJournal(Journal1, Header, LoadGame);
        var source = new EliteTelemetrySource(new StopwatchClock());
        var frames = 0;
        source.FrameReceived += _ => Interlocked.Increment(ref frames);
        using var feed = new EliteTelemetryFeed(source, _folder, pollHz: 50);
        feed.Start();
        feed.Start();
        WriteFile("Status.json", StatusDocked);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref frames) == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(20);
        Assert.True(Volatile.Read(ref frames) > 0);
        Assert.True(source.InSession);

        feed.Stop();
        var polls = feed.Polls;
        Thread.Sleep(100);
        Assert.Equal(polls, feed.Polls);
        feed.Stop();
    }
}
