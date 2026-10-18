using System.Globalization;
using System.Net;
using System.Text;
using JoyPro.Ffb.Core.Telemetry;
using JoyPro.Ffb.Core.Telemetry.WarThunder;

namespace JoyPro.Ffb.Core.Tests;

/// <summary>War Thunder's recording extras (map, HUD messages, mission) against a stub of the game's web server.</summary>
public sealed class WarThunderRecordingTests
{
    private const string MapInfo =
        """{"grid_steps":[8192.0,8192.0],"grid_zero":[-32768.0,32768.0],"map_generation":3,"hud_type":0,"map_max":[32768.0,32768.0],"map_min":[-32768.0,-32768.0],"valid":true}""";

    private const string Mission = """{"objectives":[{"primary":true,"status":"in_progress","text":"Destroy the enemy bombers"}],"status":"running"}""";

    /// <summary>Serves fixture JSON per path; counts requests and the most requests in flight at once.</summary>
    private sealed class StubServer : HttpMessageHandler
    {
        private int _inFlight;

        public bool StateValid = true;
        public bool Down;
        public double Vy;            // m/s, for the pitch sign
        public double HorizonPitch = 5;
        public double HorizonRoll = 3;
        public Func<string> MapObjects = () => "[]";
        public string MissionJson = Mission;
        public List<(long Id, string Msg)> Damage = [];
        public List<(long Id, string Msg)> Events = [];
        public readonly List<string> Requests = [];
        public int MaxInFlight;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _inFlight);
            lock (Requests)
            {
                MaxInFlight = Math.Max(MaxInFlight, now);
                Requests.Add(request.RequestUri!.PathAndQuery);
            }

            try
            {
                await Task.Yield();
                if (Down)
                    throw new HttpRequestException("connection refused (stub)");
                var path = request.RequestUri!.AbsolutePath.TrimStart('/');
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var body = path switch
                {
                    "state" => StateValid ? State(Vy) : """{"valid": false}""",
                    "indicators" => StateValid ? Indicators(HorizonRoll, HorizonPitch) : """{"valid": false}""",
                    "map_info.json" => StateValid ? MapInfo : """{"valid": false}""",
                    "map_obj.json" => MapObjects(),
                    "mission.json" => MissionJson,
                    "hudmsg" => Hud(long.Parse(query["lastEvt"] ?? "0", CultureInfo.InvariantCulture), long.Parse(query["lastDmg"] ?? "0", CultureInfo.InvariantCulture)),
                    _ => null
                };
                if (body is null)
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private string Hud(long lastEvt, long lastDmg)
        {
            static string List(IEnumerable<(long Id, string Msg)> items) =>
                "[" + string.Join(",", items.Select(i => string.Create(CultureInfo.InvariantCulture,
                    $$"""{"id":{{i.Id}},"msg":"{{i.Msg}}","sender":"","enemy":false,"mode":"","time":{{i.Id * 10}}}"""))) + "]";
            return "{\"events\":" + List(Events.Where(e => e.Id > lastEvt)) + ",\"damage\":" + List(Damage.Where(d => d.Id > lastDmg)) + "}";
        }

        public int Count(string path)
        {
            lock (Requests)
                return Requests.Count(r => r.StartsWith("/" + path, StringComparison.Ordinal));
        }
    }

    private static string State(double vy) => string.Create(CultureInfo.InvariantCulture,
        $$"""{"valid": true, "H, m": 1500, "TAS, km/h": 396, "IAS, km/h": 360, "M": 0.33, "AoA, deg": 2.0, "AoS, deg": 0.0, "Ny": 1.0, "Vy, m/s": {{vy}}, "Wx, deg/s": 0, "Mfuel, kg": 250, "aileron, %": 0, "elevator, %": 0, "rudder, %": 0, "flaps, %": 0, "gear, %": 0, "airbrake, %": 0, "throttle 1, %": 100, "RPM 1": 2600}""");

    private static string Indicators(double roll, double pitch) => string.Create(CultureInfo.InvariantCulture,
        $$"""{"valid": true, "type": "bf-109f-4", "speed": 100.0, "aviahorizon_roll": {{roll}}, "aviahorizon_pitch": {{pitch}}, "compass": 90.0, "g_meter": 1.0}""");

    private static string Obj(string type, string icon, string color, double x, double y, double dx = 0, double dy = -1) =>
        string.Create(CultureInfo.InvariantCulture,
            $$"""{"type":"{{type}}","color":"{{color}}","color[]":[0,0,0],"blink":0,"icon":"{{icon}}","icon_bg":"none","x":{{x}},"y":{{y}},"dx":{{dx}},"dy":{{dy}}}""");

    private static string Objects(params string[] items) => "[" + string.Join(",", items) + "]";

    private const string PlayerColor = "#faC81E";
    private const string EnemyColor = "#f40C00";
    private const string AllyColor = "#174DFF";

    private static string StandardObjects(double shift = 0) => Objects(
        Obj("aircraft", "Player", PlayerColor, 0.75, 0.25, 1, 0),
        Obj("aircraft", "Fighter", EnemyColor, 0.30 + shift, 0.40),
        Obj("aircraft", "Bomber", AllyColor, 0.60 - shift, 0.70),
        Obj("ground_model", "Mediumtank", EnemyColor, 0.10, 0.90),
        """{"type":"airfield","color":"#174DFF","color[]":[23,77,255],"blink":0,"icon":"none","icon_bg":"none","sx":0.50,"sy":0.80,"ex":0.50,"ey":0.84}""",
        Obj("capture_zone", "capture_zone", "#FFFFFF", 0.5, 0.5, 0, 0));

    private sealed class Rig : IDisposable
    {
        public readonly StubServer Server = new();
        public readonly ManualClock Clock = new(100);
        public readonly WarThunderTelemetrySource Source;
        public readonly List<TelemetryEvent> Events = [];

        public Rig(bool extras)
        {
            Source = new WarThunderTelemetrySource(Clock, new Uri("http://127.0.0.1:8111/"), 20, Server) { RecordExtras = extras };
            Source.EventRaised += e => { lock (Events) Events.Add(e); };
        }

        /// <summary>Polls like the 20 Hz loop does, moving the clock 50 ms per period.</summary>
        public async Task Poll(double seconds)
        {
            var periods = (int)Math.Round(seconds * 20);
            for (var i = 0; i < periods; i++)
            {
                try
                {
                    await Source.PollOnceAsync();
                }
                catch (HttpRequestException)
                {
                }

                Clock.Advance(0.05);
            }
        }

        public List<TelemetryEvent> Of(string kind)
        {
            lock (Events)
                return Events.Where(e => e.Kind == kind).ToList();
        }

        public void Dispose() => Source.Dispose();
    }

    private static Dictionary<string, string[]> ParseObjects(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('|')).ToDictionary(f => f[2] + "@" + f[3] + "@" + f[1], f => f);

    [Fact]
    public async Task WithoutRecordExtras_OnlyStateAndIndicatorsArePolled_AndFrameKeysStayTheSame()
    {
        using var rig = new Rig(extras: false);
        rig.Server.MapObjects = () => StandardObjects();
        await rig.Poll(12);

        Assert.True(rig.Server.Count("state") >= 200);
        Assert.Equal((rig.Server.Count("state") + 1) / 2, rig.Server.Count("indicators"));      // every second period
        Assert.Equal(rig.Server.Count("state") + rig.Server.Count("indicators"), rig.Server.Requests.Count);
        Assert.Empty(rig.Of(TelemetryEventKinds.Objects));
        Assert.Empty(rig.Of(TelemetryEventKinds.Message));

        // Exactly what /state and /indicators give (the force feedback keys): nothing added.
        var expected = new Dictionary<string, double>(StringComparer.Ordinal);
        WarThunderProtocol.Read(Encoding.UTF8.GetBytes(State(0)), WarThunderProtocol.StatePrefix, expected, out _);
        WarThunderProtocol.Read(Encoding.UTF8.GetBytes(Indicators(3, 5)), WarThunderProtocol.IndicatorsPrefix, expected, out _);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), rig.Source.Latest!.Values.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(360, rig.Source.Latest.Values["wt.ias_km_h"]);
        Assert.Equal(5, rig.Source.Latest.Values["wt.ind.aviahorizon_pitch"]);
        Assert.Equal("bf-109f-4", rig.Source.Vehicle);
        Assert.Equal("bf-109f-4", rig.Source.SessionInfo[TelemetryInfoKeys.Aircraft]);
    }

    [Fact]
    public async Task WithRecordExtras_TheExtrasArePolledAtTheirRates_OneRequestAtATime_NeverChat()
    {
        using var rig = new Rig(extras: true);
        rig.Server.MapObjects = () => StandardObjects();
        await rig.Poll(20);

        Assert.Equal(1, rig.Server.MaxInFlight);
        Assert.InRange(rig.Server.Count("map_obj.json"), 38, 41);       // 2 Hz
        Assert.InRange(rig.Server.Count("hudmsg"), 19, 21);             // 1 Hz
        Assert.InRange(rig.Server.Count("mission.json"), 4, 5);         // every 5 s
        Assert.InRange(rig.Server.Count("map_info.json"), 2, 3);        // every 10 s
        Assert.InRange(rig.Server.Count("state"), 399, 401);            // the 20 Hz poll is unchanged
        Assert.Equal(0, rig.Server.Count("gamechat"));
        Assert.Equal(0, rig.Server.Count("map.img"));
        Assert.Equal("/map_info.json", rig.Server.Requests[2]);         // first extra of a battle: the map
        Assert.Equal("/hudmsg?lastEvt=0&lastDmg=0", rig.Server.Requests.First(r => r.StartsWith("/hudmsg", StringComparison.Ordinal)));

        var map = rig.Source.Map!;
        Assert.Equal(65536, map.Width);
        Assert.Equal(map.Id, rig.Source.SessionInfo[TelemetryInfoKeys.Map]);
        Assert.StartsWith("wt_65536x65536_", map.Id, StringComparison.Ordinal);
        Assert.False(rig.Source.SessionInfo.ContainsKey(TelemetryInfoKeys.ReferenceLatitude));   // no geo reference exists
        Assert.Single(rig.Of(TelemetryEventKinds.Map));
    }

    [Fact]
    public async Task ExtrasStopWhileTheStateIsInvalid()
    {
        using var rig = new Rig(extras: true);
        rig.Server.StateValid = false;
        await rig.Poll(15);
        Assert.Equal(rig.Server.Count("state") + rig.Server.Count("indicators"), rig.Server.Requests.Count);
        Assert.Empty(rig.Of(TelemetryEventKinds.SessionStart));
    }

    [Fact]
    public async Task PlayerPosition_IsInMetresEastNorth_WithTheMapHeading_AndNavMappingsPointAtIt()
    {
        using var rig = new Rig(extras: true);
        rig.Server.MapObjects = () => StandardObjects();
        await rig.Poll(2);

        var frame = rig.Source.Latest!;
        // x 0.75 of [-32768, 32768] = 16384 east; y 0.25 from the top edge (north, +32768) = 16384 north.
        Assert.Equal(16384, frame.Values[WarThunderProtocol.MapXKey], 3);
        Assert.Equal(16384, frame.Values[WarThunderProtocol.MapYKey], 3);
        Assert.Equal(90, frame.Values[WarThunderProtocol.MapHeadingKey], 6);       // dx 1, dy 0 = east
        Assert.Equal(1500, frame.Values["wt.h_m"]);

        foreach (var mapping in rig.Source.NavMappings)
            Assert.True(frame.Values.ContainsKey(mapping.Key), mapping.Target + " -> " + mapping.Key);

        Assert.Contains(rig.Source.NavMappings, m => m.Target == TelemetryNav.Ias && m.Key == "wt.ias_km_h" && Math.Abs(m.Scale - 1 / 3.6) < 1e-12);
        Assert.Contains(rig.Source.NavMappings, m => m.Target == TelemetryNav.Throttle && m.Key == "wt.throttle_1_pct" && m.Scale == 0.01);

        Assert.Equal(0, WarThunderMap.HeadingDeg(0, -1), 9);         // up on the picture = north
        Assert.Equal(90, WarThunderMap.HeadingDeg(1, 0), 9);
        Assert.Equal(180, WarThunderMap.HeadingDeg(0, 1), 9);
        Assert.Equal(270, WarThunderMap.HeadingDeg(-1, 0), 9);
        var odd = WarThunderMapInfo.Parse("""{"map_min":[0,0],"map_max":[1000,2000],"grid_steps":[100,100],"valid":true}"""u8)!;
        Assert.Equal((0d, 2000d), odd.ToMetres(0, 0));               // top left = west, north edge
        Assert.Equal((1000d, 0d), odd.ToMetres(1, 1));
        Assert.Null(WarThunderMapInfo.Parse("""{"valid":false}"""u8));
    }

    [Fact]
    public async Task PlayerGone_PositionLeavesTheFrames()
    {
        using var rig = new Rig(extras: true);
        rig.Server.MapObjects = () => StandardObjects();
        await rig.Poll(2);
        Assert.True(rig.Source.TryGet(WarThunderProtocol.MapXKey, out _));
        rig.Server.MapObjects = () => Objects(Obj("aircraft", "Fighter", EnemyColor, 0.3, 0.4));
        await rig.Poll(2);
        Assert.False(rig.Source.TryGet(WarThunderProtocol.MapXKey, out _));
    }

    [Fact]
    public async Task OtherObjects_KeepTheirIdsBetweenPolls_WithTypesAndCoalitions_WithoutThePlayer()
    {
        using var rig = new Rig(extras: true);
        var shift = 0.0;
        var swapped = false;
        rig.Server.MapObjects = () =>
        {
            shift += 0.002;                                            // ~130 m per poll
            var objects = StandardObjects(shift);
            if (!swapped)
                return objects;
            // Same objects in another order, plus a second enemy fighter far away.
            var list = objects.Trim('[', ']').Split("},{").Select(s => "{" + s.Trim('{', '}') + "}").Reverse().ToList();
            list.Add(Obj("aircraft", "Fighter", EnemyColor, 0.9, 0.9));
            return "[" + string.Join(",", list) + "]";
        };
        await rig.Poll(3);
        var first = ParseObjects(rig.Of(TelemetryEventKinds.Objects)[^1].Text);
        Assert.Equal(5, first.Count);
        Assert.DoesNotContain(first.Keys, k => k.StartsWith("Player", StringComparison.Ordinal));

        var fighter = first["Fighter@Enemies@Air+FixedWing"];
        Assert.Equal(12, fighter.Length);
        Assert.Equal("", fighter[4]);                                  // no lat/lon/alt: the map is flat and 2D
        Assert.Equal("", fighter[6]);
        Assert.Equal("0", fighter[9]);                                 // heading north
        Assert.Equal("Allies", first["Bomber@Allies@Air+FixedWing"][3]);
        Assert.True(first.ContainsKey("Mediumtank@Enemies@Ground+Heavy+Armor+Vehicle+Tank"));
        var airfield = first["airfield@Allies@Ground+Static+Aerodrome"];
        Assert.Equal(0, double.Parse(airfield[10], CultureInfo.InvariantCulture), 3);       // middle of the runway
        Assert.Equal(32768 - 0.82 * 65536, double.Parse(airfield[11], CultureInfo.InvariantCulture), 3);
        Assert.Equal("180", airfield[9]);
        Assert.Equal("Neutral", first["capture_zone@Neutral@Navaid+Static"][3]);

        await rig.Poll(3);
        var later = ParseObjects(rig.Of(TelemetryEventKinds.Objects)[^1].Text);
        foreach (var key in first.Keys)
            Assert.Equal(first[key][0], later[key][0]);

        swapped = true;
        await rig.Poll(1);
        var lines = rig.Of(TelemetryEventKinds.Objects)[^1].Text.Split('\n').Select(l => l.Split('|')).ToList();
        Assert.Equal(6, lines.Count);
        var fighters = lines.Where(f => f[2] == "Fighter").ToList();
        Assert.Equal(2, fighters.Count);
        Assert.Contains(fighters, f => f[0] == fighter[0]);            // the known one keeps its id
        Assert.Single(fighters, f => f[0] != fighter[0]);              // the new one gets a new id
        Assert.Equal(6, lines.Select(f => f[0]).Distinct().Count());
        Assert.All(lines, f => Assert.Matches("^[0-9a-f]+$", f[0]));
    }

    [Fact]
    public void Coalitions_AndTypes_FromTheMapColoursAndIcons()
    {
        Assert.Equal("Enemies", WarThunderMap.Coalition("#f40C00", PlayerColor));
        Assert.Equal("Allies", WarThunderMap.Coalition("#174DFF", PlayerColor));
        Assert.Equal("Allies", WarThunderMap.Coalition("#43E244", PlayerColor));   // squad: green
        Assert.Equal("Allies", WarThunderMap.Coalition(PlayerColor, PlayerColor));
        Assert.Equal("Neutral", WarThunderMap.Coalition("#FFFFFF", PlayerColor));
        Assert.Equal("Neutral", WarThunderMap.Coalition("", PlayerColor));
        Assert.Equal("Air+Rotorcraft", WarThunderMap.TacviewType("aircraft", "Helicopter"));
        Assert.Equal("Air+FixedWing", WarThunderMap.TacviewType("aircraft", "Assault"));
        Assert.Equal("Ground+AntiAircraft", WarThunderMap.TacviewType("ground_model", "SPAA"));
        Assert.Equal("Ground+Heavy+Armor+Vehicle+Tank", WarThunderMap.TacviewType("ground_model", "TankDestroyer"));
        Assert.Equal("Ground+Static+Building", WarThunderMap.TacviewType("ground_model", "Structure"));
        Assert.Equal("Ground+Vehicle", WarThunderMap.TacviewType("ground_model", "Wheeled"));
        Assert.Equal("Sea+Watercraft", WarThunderMap.TacviewType("ground_model", "Ship"));
        Assert.Equal("Navaid+Static", WarThunderMap.TacviewType("bombing_point", "bombing_point"));
        Assert.Equal("Navaid+Static", WarThunderMap.TacviewType("respawn_base_fighter", "respawn_base_fighter"));
        Assert.Equal("Navaid+Static+Waypoint", WarThunderMap.TacviewType("waypoint", "waypoint"));
    }

    [Fact]
    public async Task HudMessages_AreRaisedOnce_IncrementallyById_AndTheMissionOnChange()
    {
        using var rig = new Rig(extras: true);
        rig.Server.Damage.Add((1, "PilotA (Bf 109 F-4) shot down PilotB (Spitfire Mk Vc)"));
        rig.Server.Events.Add((1, "Enemy airfield is under attack"));
        await rig.Poll(3);
        rig.Server.Damage.Add((2, "PilotC (Yak-1) has crashed"));
        await rig.Poll(3);
        rig.Server.MissionJson = Mission.Replace("in_progress", "complete", StringComparison.Ordinal);
        await rig.Poll(6);

        var texts = rig.Of(TelemetryEventKinds.Message).Select(e => e.Text).ToList();
        Assert.Equal(3, texts.Count);
        Assert.Equal(1, texts.Count(t => t.Contains("shot down", StringComparison.Ordinal)));
        Assert.Contains("PilotC (Yak-1) has crashed", texts);
        Assert.Contains(rig.Server.Requests, r => r == "/hudmsg?lastEvt=1&lastDmg=2");

        var missions = rig.Of(WarThunderProtocol.MissionEventKind);
        Assert.Equal(2, missions.Count);                              // the first answer and the one change
        Assert.Equal(Mission, missions[0].Text);
        Assert.Contains("\"complete\"", missions[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sessions_StartWithAValidState_EndAfterTenSecondsWithout_OrWhenTheGameStopsAnswering()
    {
        using var rig = new Rig(extras: true);
        rig.Server.StateValid = false;
        await rig.Poll(2);
        Assert.Empty(rig.Of(TelemetryEventKinds.SessionStart));

        rig.Server.StateValid = true;
        await rig.Poll(2);
        var start = Assert.Single(rig.Of(TelemetryEventKinds.SessionStart));
        Assert.Equal("bf-109f-4", start.Text);
        Assert.True(rig.Source.InSession);

        rig.Server.StateValid = false;                                 // dead / back in the hangar
        await rig.Poll(5);
        Assert.Empty(rig.Of(TelemetryEventKinds.SessionEnd));          // a short gap keeps the session
        rig.Server.StateValid = true;
        await rig.Poll(1);
        rig.Server.StateValid = false;
        await rig.Poll(11);
        Assert.Single(rig.Of(TelemetryEventKinds.SessionEnd));
        Assert.False(rig.Source.InSession);

        rig.Server.StateValid = true;
        await rig.Poll(1);
        Assert.Equal(2, rig.Of(TelemetryEventKinds.SessionStart).Count);

        rig.Server.Down = true;                                        // the game closed
        await rig.Poll(11);
        var ends = rig.Of(TelemetryEventKinds.SessionEnd);
        Assert.Equal(2, ends.Count);
        Assert.Contains("stopped answering", ends[1].Text, StringComparison.Ordinal);
        Assert.False(rig.Source.ServerAnswering);
    }

    [Fact]
    public async Task RawAnswers_GoToTheRecorder_OnlyWhileItListens()
    {
        using var rig = new Rig(extras: true);
        rig.Server.MapObjects = () => StandardObjects();
        Assert.False(rig.Source.WantsRaw);
        await rig.Poll(1);

        var raw = new List<TelemetryRawPacket>();
        void Listen(TelemetryRawPacket p)
        {
            lock (raw)
                raw.Add(p);
        }

        rig.Source.RawReceived += Listen;
        Assert.True(rig.Source.WantsRaw);
        await rig.Poll(11);
        rig.Source.RawReceived -= Listen;
        var count = raw.Count;
        await rig.Poll(1);
        Assert.Equal(count, raw.Count);

        var streams = raw.Select(p => p.Stream).ToHashSet();
        Assert.Equal(new HashSet<string> { "http:/state", "http:/indicators", "http:/map_obj.json", "http:/map_info.json", "http:/hudmsg", "http:/mission.json" }, streams);
        var state = raw.First(p => p.Stream == "http:/state");
        Assert.Equal(State(0), Encoding.UTF8.GetString(state.Data));
        Assert.Equal(StandardObjects(), Encoding.UTF8.GetString(raw.First(p => p.Stream == "http:/map_obj.json").Data));
    }

    [Fact]
    public async Task ArtificialHorizon_SignIsDecidedFromTheFlight()
    {
        // The gauge reads -20 while the aircraft climbs at ~14 degrees + 2 AoA: the gauge is inverted.
        using (var rig = new Rig(extras: true))
        {
            rig.Server.Vy = 27;                                        // 27 / 110 m/s -> 14 degrees
            rig.Server.HorizonPitch = -20;
            await rig.Poll(3);
            Assert.Equal(-1, rig.Source.PitchSign.Sign);
            Assert.Equal(20, rig.Source.Latest!.Values[WarThunderProtocol.PitchKey], 6);
        }

        using (var rig = new Rig(extras: true))
        {
            rig.Server.Vy = 27;
            rig.Server.HorizonPitch = 20;                              // the gauge reads like the flight
            await rig.Poll(3);
            Assert.Equal(1, rig.Source.PitchSign.Sign);
            Assert.Equal(20, rig.Source.Latest!.Values[WarThunderProtocol.PitchKey], 6);
        }

        using (var rig = new Rig(extras: true))
        {
            // A right turn (heading grows 10 degrees per second) with the gauge at +30: positive = right wing down.
            var heading = 0.0;
            rig.Server.HorizonRoll = 30;
            rig.Server.MapObjects = () =>
            {
                heading += 5;
                var rad = heading / TelemetryNav.RadToDeg;
                return Objects(Obj("aircraft", "Player", PlayerColor, 0.5, 0.5, Math.Sin(rad), -Math.Cos(rad)));
            };
            await rig.Poll(8);
            Assert.True(rig.Source.RollSign.Votes >= 10);
            Assert.Equal(1, rig.Source.RollSign.Sign);
            Assert.Equal(30, rig.Source.Latest!.Values[WarThunderProtocol.RollKey], 6);
        }
    }

    [Fact]
    public void Feed_ForwardsRecordExtrasToTheSource()
    {
        using var feed = (WarThunderTelemetryFeed)TelemetryFeeds.Create("WarThunder", new ManualClock(),
            new TelemetryFeedOptions { WarThunderHandler = new StubServer() });
        var source = (WarThunderTelemetrySource)feed.Source;
        Assert.False(feed.RecordExtras);
        Assert.False(source.RecordExtras);
        feed.RecordExtras = true;
        Assert.True(source.RecordExtras);
        Assert.NotEmpty(source.NavMappings);
    }
}
