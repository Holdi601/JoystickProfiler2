using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace JoyPro.Ffb.Core.Telemetry.WarThunder;

/// <summary>War Thunder's local web server: GET http://127.0.0.1:8111/state and /indicators return JSON objects.</summary>
/// <remarks>
/// /state holds the flight model ("valid", "IAS, km/h", "AoA, deg", "Ny", "gear, %", ...), /indicators the cockpit
/// instruments ("valid", "type" = the vehicle, "speed", "g_meter", "trimmer", ...). Every numeric field becomes a key:
/// "wt." + the sanitised name for /state ("IAS, km/h" -> "wt.ias_km_h", "gear, %" -> "wt.gear_pct"), "wt.ind." + the
/// sanitised name for /indicators. true/false become 1/0. A frame is published only while /state says "valid": true
/// (in a flight); in menus, hangar and replays the source goes stale and the force feedback silent.
/// </remarks>
public static class WarThunderProtocol
{
    public const string StatePath = "state";
    public const string IndicatorsPath = "indicators";
    public const string StatePrefix = "wt.";
    public const string IndicatorsPrefix = "wt.ind.";
    public static readonly Uri DefaultBaseUri = new("http://127.0.0.1:8111/");

    // Recording extras (WarThunderTelemetrySource.RecordExtras). /gamechat and /map.img are never requested.
    public const string MapInfoPath = "map_info.json";
    public const string MapObjectsPath = "map_obj.json";
    public const string HudMessagesPath = "hudmsg";
    public const string MissionPath = "mission.json";

    /// <summary>Player position on the map in metres: x east, y north (see <see cref="WarThunderMapInfo.ToMetres"/>).</summary>
    public const string MapXKey = "wt.map.x_m";
    public const string MapYKey = "wt.map.y_m";

    /// <summary>Player heading from the map arrow, degrees clockwise from north.</summary>
    public const string MapHeadingKey = "wt.map.heading_deg";

    /// <summary>Artificial horizon (cockpits that have one), degrees: roll right wing down +, pitch nose up +.</summary>
    public const string RollKey = "wt.att.roll_deg";
    public const string PitchKey = "wt.att.pitch_deg";

    /// <summary>Event: /mission.json changed (text = the compact JSON: objectives with "primary", "status", "text").</summary>
    public const string MissionEventKind = "wt.mission";

    /// <summary>Standard quantities for the ACMI export. Position and heading need the recording extras.</summary>
    public static readonly IReadOnlyList<TelemetryNavMapping> NavMappings =
    [
        new(TelemetryNav.U, MapXKey),
        new(TelemetryNav.V, MapYKey),
        new(TelemetryNav.Altitude, "wt.h_m"),
        new(TelemetryNav.Roll, RollKey),
        new(TelemetryNav.Pitch, PitchKey),
        new(TelemetryNav.Yaw, MapHeadingKey),
        new(TelemetryNav.Ias, "wt.ias_km_h", 1 / 3.6),
        new(TelemetryNav.Tas, "wt.tas_km_h", 1 / 3.6),
        new(TelemetryNav.Mach, "wt.m"),
        new(TelemetryNav.Aoa, "wt.aoa_deg"),
        new(TelemetryNav.Aos, "wt.aos_deg"),
        new(TelemetryNav.VerticalG, "wt.ny"),
        new(TelemetryNav.Gear, "wt.gear_pct", 0.01),
        new(TelemetryNav.Flaps, "wt.flaps_pct", 0.01),
        new(TelemetryNav.Airbrakes, "wt.airbrake_pct", 0.01),
        new(TelemetryNav.VerticalSpeed, "wt.vy_m_s"),
        new(TelemetryNav.Throttle, "wt.throttle_1_pct", 0.01),
        new(TelemetryNav.FuelKg, "wt.mfuel_kg")
    ];

    private static readonly Dictionary<string, string> KeyCache = new(StringComparer.Ordinal);

    /// <summary>"IAS, km/h" -> "ias_km_h"; "gear, %" -> "gear_pct"; "throttle 1, %" -> "throttle_1_pct".</summary>
    public static string Sanitise(string name)
    {
        lock (KeyCache)
        {
            if (KeyCache.TryGetValue(name, out var cached))
                return cached;
            var sb = new StringBuilder(name.Length + 4);
            var underscore = false;
            foreach (var raw in name)
            {
                var c = char.ToLowerInvariant(raw);
                if (c == '%')
                {
                    if (sb.Length > 0 && !underscore)
                        sb.Append('_');
                    sb.Append("pct");
                    underscore = false;
                }
                else if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                {
                    sb.Append(c);
                    underscore = false;
                }
                else if (!underscore && sb.Length > 0)
                {
                    sb.Append('_');
                    underscore = true;
                }
            }

            var key = sb.ToString().TrimEnd('_');
            if (KeyCache.Count < 4096)
                KeyCache[name] = key;
            return key;
        }
    }

    /// <summary>Reads one JSON object: numbers and booleans into <paramref name="values"/>, the "type" string out.</summary>
    /// <returns>The "valid" field (false when missing or the text is not a JSON object).</returns>
    public static bool Read(ReadOnlySpan<byte> json, string prefix, IDictionary<string, double> values, out string? type)
    {
        type = null;
        var valid = false;
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;
                var name = reader.GetString() ?? "";
                if (!reader.Read())
                    break;
                switch (reader.TokenType)
                {
                    case JsonTokenType.Number when reader.TryGetDouble(out var number) && double.IsFinite(number):
                        values[prefix + Sanitise(name)] = number;
                        break;
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                        var flag = reader.TokenType == JsonTokenType.True;
                        if (name == "valid")
                            valid = flag;
                        else
                            values[prefix + Sanitise(name)] = flag ? 1 : 0;
                        break;
                    case JsonTokenType.String:
                        if (name == "type")
                            type = reader.GetString();
                        else if (double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed))
                            values[prefix + Sanitise(name)] = parsed;
                        break;
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        reader.Skip();
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return valid;
    }
}

/// <summary>Polls War Thunder's /state and /indicators while it runs and publishes them as frames.</summary>
/// <remarks>
/// One request per period on a timer (default 20 Hz for /state, every second period for /indicators); nothing
/// spins. When the server does not answer (the game is starting, closed, or the port is off) the poll backs off to
/// once every <see cref="OfflineRetrySeconds"/>. Requests go to 127.0.0.1 only and time out after
/// <see cref="RequestTimeout"/>. The host starts the source only while aces.exe runs.
/// <para>For the session recorder (<see cref="RecordExtras"/>, off by default so force feedback alone polls nothing
/// more): /map_info.json every <see cref="MapInfoSeconds"/>, /map_obj.json every <see cref="MapObjectsSeconds"/>,
/// /hudmsg every <see cref="HudMessagesSeconds"/>, /mission.json every <see cref="MissionSeconds"/>, at most one of
/// them per poll period after /state (never two requests at once), and only while /state is valid. Chat (/gamechat)
/// is never requested: other players' messages are not recorded.</para>
/// </remarks>
public sealed class WarThunderTelemetrySource : FrameTelemetrySource, IDisposable
{
    public const double OfflineRetrySeconds = 2;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(400);

    public const double MapInfoSeconds = 10;
    public const double MapObjectsSeconds = 0.5;
    public const double HudMessagesSeconds = 1;
    public const double MissionSeconds = 5;

    /// <summary>/state invalid this long (death, back in the hangar, the game closed) ends the session.</summary>
    public const double SessionEndSeconds = 10;

    /// <summary>Player position from the map is dropped from frames when it is older than this.</summary>
    public const double MapValuesMaxAgeSeconds = 3;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _baseUri;
    private readonly double _rateHz;
    private readonly Dictionary<string, double> _indicators = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private long _tick;

    // Session and recording extras (poll thread only, except RecordExtras).
    private volatile bool _recordExtras;
    private bool _stateValid;
    private bool _inSession;
    private double _lastValidTime;
    private double _dueMapInfo, _dueMapObjects, _dueHud, _dueMission;
    private WarThunderMapInfo? _map;
    private string? _mapId;
    private long _lastEvtId, _lastDmgId;
    private bool _hudResync = true;
    private string? _lastMission;
    private readonly WarThunderObjectTracker _tracker = new();
    private volatile MapValues? _mapValues;
    private double? _lastHeading;
    private double _lastHeadingTime;
    private double _lastRollRaw = double.NaN;
    private double _lastTasMs;

    private sealed record MapValues(double Time, double U, double V, double? Heading);

    public WarThunderTelemetrySource(IFfbClock clock, Uri? baseUri = null, double rateHz = 20, HttpMessageHandler? handler = null)
        : base("War Thunder", [], clock)
    {
        _baseUri = baseUri ?? WarThunderProtocol.DefaultBaseUri;
        _rateHz = Math.Clamp(rateHz, 1, 60);
        _ownsHttp = true;
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = RequestTimeout,
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
                AutomaticDecompression = DecompressionMethods.None
            })
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = RequestTimeout;
    }

    public Uri BaseUri => _baseUri;

    /// <summary>The vehicle of /indicators "type" (e.g. "spitfire_mk1"), null before the first answer.</summary>
    public string? Vehicle { get; private set; }

    public long Requests { get; private set; }
    public long Failures { get; private set; }

    /// <summary>Failed requests of the recording extras (an endpoint missing does not stop the poll).</summary>
    public long ExtraFailures { get; private set; }

    /// <summary>True while the last request was answered (the server is up, even outside a flight).</summary>
    public bool ServerAnswering { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Also polls the map, the HUD messages and the mission for the session recorder (off by default).</summary>
    /// <remarks>Adds the frame keys <see cref="WarThunderProtocol.MapXKey"/>, <see cref="WarThunderProtocol.MapYKey"/>,
    /// <see cref="WarThunderProtocol.MapHeadingKey"/>, <see cref="WarThunderProtocol.RollKey"/> and
    /// <see cref="WarThunderProtocol.PitchKey"/> and raises <see cref="TelemetryEventKinds.Objects"/>,
    /// <see cref="TelemetryEventKinds.Message"/> and <see cref="WarThunderProtocol.MissionEventKind"/> events.</remarks>
    public bool RecordExtras
    {
        get => _recordExtras;
        set
        {
            if (_recordExtras == value)
                return;
            _recordExtras = value;
            if (!value)
                _mapValues = null;
        }
    }

    /// <summary>The map of the current battle (recording extras), null when unknown.</summary>
    public WarThunderMapInfo? Map => _map;

    /// <summary>True between <see cref="TelemetryEventKinds.SessionStart"/> and <see cref="TelemetryEventKinds.SessionEnd"/>.</summary>
    public bool InSession => _inSession;

    /// <summary>The sign applied to the artificial horizon's roll / pitch gauge (decided from the flight physics).</summary>
    public WarThunderSignVote RollSign { get; } = new(-1);
    public WarThunderSignVote PitchSign { get; } = new(-1);

    public override IReadOnlyList<TelemetryNavMapping> NavMappings => WarThunderProtocol.NavMappings;

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
                return;
            _stop = new CancellationTokenSource();
            _loop = Task.Run(() => PollLoop(_stop.Token));
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
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        lock (_gate)
        {
            _stop?.Dispose();
            _stop = null;
        }
    }

    public void Dispose()
    {
        Stop();
        if (_ownsHttp)
            _http.Dispose();
    }

    /// <summary>Handles one /state answer (and optionally a fresh /indicators answer). Public for tests.</summary>
    /// <returns>True when a frame was published (the state was valid).</returns>
    public bool Ingest(ReadOnlySpan<byte> stateJson, ReadOnlySpan<byte> indicatorsJson = default)
    {
        if (!indicatorsJson.IsEmpty)
        {
            var indicators = new Dictionary<string, double>(StringComparer.Ordinal);
            var indicatorsValid = WarThunderProtocol.Read(indicatorsJson, WarThunderProtocol.IndicatorsPrefix, indicators, out var type);
            lock (_indicators)
            {
                _indicators.Clear();
                if (indicatorsValid)
                {
                    foreach (var pair in indicators)
                        _indicators[pair.Key] = pair.Value;
                }
            }

            if (indicatorsValid && !string.IsNullOrEmpty(type))
            {
                Vehicle = type;
                SetInfo(TelemetryInfoKeys.Aircraft, type);
            }
        }

        var now = Clock.Now;
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!WarThunderProtocol.Read(stateJson, WarThunderProtocol.StatePrefix, values, out _))
        {
            _stateValid = false;
            CheckSessionEnd(now, "no flight for " + SessionEndSeconds.ToString(CultureInfo.InvariantCulture) + " s");
            return false;
        }

        _stateValid = true;
        _lastValidTime = now;
        if (!_inSession)
            StartSession();
        lock (_indicators)
        {
            foreach (var pair in _indicators)
                values[pair.Key] = pair.Value;
        }

        if (_recordExtras)
            AddRecordingValues(values, now);
        Publish(new TelemetryFrame(now, values));
        return true;
    }

    /// <summary>One poll period: /state, /indicators every second period, then at most one due recording extra.
    /// Public for tests; the poll loop calls it on its timer.</summary>
    /// <exception cref="HttpRequestException">/state or /indicators did not answer.</exception>
    public async Task PollOnceAsync(CancellationToken stop = default)
    {
        byte[] state;
        byte[]? indicators = null;
        try
        {
            state = await GetAsync(WarThunderProtocol.StatePath, stop).ConfigureAwait(false);
            PublishRaw("http:/" + WarThunderProtocol.StatePath, state);
            if (_tick % 2 == 0)
            {
                indicators = await GetAsync(WarThunderProtocol.IndicatorsPath, stop).ConfigureAwait(false);
                PublishRaw("http:/" + WarThunderProtocol.IndicatorsPath, indicators);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !stop.IsCancellationRequested))
        {
            ServerAnswering = false;
            _stateValid = false;
            CheckSessionEnd(Clock.Now, "the game stopped answering");
            throw;
        }

        ServerAnswering = true;
        Ingest(state, indicators);
        _tick++;
        if (_recordExtras && _stateValid)
            await PollExtraAsync(stop).ConfigureAwait(false);
    }

    private void StartSession()
    {
        _inSession = true;
        var now = Clock.Now;
        _dueMapInfo = _dueMapObjects = _dueHud = _dueMission = now;
        _hudResync = true;
        _lastMission = null;
        _tracker.Reset();
        _lastHeading = null;
        RaiseEvent(TelemetryEventKinds.SessionStart, Vehicle ?? "");
    }

    private void CheckSessionEnd(double now, string why)
    {
        if (!_inSession || now - _lastValidTime <= SessionEndSeconds)
            return;
        _inSession = false;
        _mapValues = null;
        _tracker.Reset();
        RaiseEvent(TelemetryEventKinds.SessionEnd, why);
    }

    /// <summary>Keeps the cadence of an extra (a request that waited a period for its turn does not drift the next one).</summary>
    private static void Next(ref double due, double period, double now)
    {
        due += period;
        if (due <= now)
            due = now + period;
    }

    private async Task PollExtraAsync(CancellationToken stop)
    {
        var now = Clock.Now;
        string path;
        string stream;
        Action<byte[], double> handle;
        if (now >= _dueMapInfo)
        {
            Next(ref _dueMapInfo, MapInfoSeconds, now);
            path = stream = WarThunderProtocol.MapInfoPath;
            handle = (data, _) => HandleMapInfo(data);
        }
        else if (now >= _dueMapObjects)
        {
            Next(ref _dueMapObjects, MapObjectsSeconds, now);
            path = stream = WarThunderProtocol.MapObjectsPath;
            handle = HandleMapObjects;
        }
        else if (now >= _dueHud)
        {
            Next(ref _dueHud, HudMessagesSeconds, now);
            stream = WarThunderProtocol.HudMessagesPath;
            path = _hudResync
                ? stream + "?lastEvt=0&lastDmg=0"
                : string.Create(CultureInfo.InvariantCulture, $"{stream}?lastEvt={_lastEvtId}&lastDmg={_lastDmgId}");
            handle = (data, _) => HandleHudMessages(data);
        }
        else if (now >= _dueMission)
        {
            Next(ref _dueMission, MissionSeconds, now);
            path = stream = WarThunderProtocol.MissionPath;
            handle = (data, _) => HandleMission(data);
        }
        else
        {
            return;
        }

        byte[] data;
        try
        {
            data = await GetAsync(path, stop).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !stop.IsCancellationRequested))
        {
            ExtraFailures++;
            return;
        }

        PublishRaw("http:/" + stream, data);
        handle(data, Clock.Now);
    }

    private void HandleMapInfo(byte[] data)
    {
        var info = WarThunderMapInfo.Parse(data);
        _map = info;
        if (info is null)
        {
            _mapValues = null;
            return;
        }

        var id = info.Id;
        if (_mapId is not null && _mapId != id)
        {
            // Another battle: the HUD message ids start again and old object tracks mean nothing.
            _lastEvtId = _lastDmgId = 0;
            _hudResync = true;
            _tracker.Reset();
        }

        _mapId = id;
        SetInfo(TelemetryInfoKeys.Map, id);
    }

    private void HandleMapObjects(byte[] data, double now)
    {
        var map = _map;
        if (map is null)
        {
            _dueMapInfo = Math.Min(_dueMapInfo, now + 2);     // objects but no map yet: ask for it again soon
            return;
        }

        var objects = WarThunderMap.ParseObjects(data);
        if (objects is null)
            return;
        var player = objects.FirstOrDefault(o => o.IsPlayer);
        if (player is null)
        {
            _mapValues = null;
            _lastHeading = null;
        }
        else
        {
            var (u, v) = map.ToMetres(player.X, player.Y);
            var heading = player.Heading;
            _mapValues = new MapValues(now, u, v, heading);
            VoteRoll(heading, now);
        }

        var others = new List<(WarThunderMapObject Obj, double U, double V)>(objects.Count);
        foreach (var o in objects)
        {
            if (o.IsPlayer)
                continue;
            var (u, v) = map.ToMetres(o.X, o.Y);
            others.Add((o, u, v));
        }

        var ids = _tracker.Update(now, others);
        var lines = new List<string>(others.Count);
        for (var i = 0; i < others.Count; i++)
        {
            var (o, u, v) = others[i];
            lines.Add(TelemetryEventKinds.ObjectLine(ids[i], WarThunderMap.TacviewType(o.Type, o.Icon),
                string.IsNullOrEmpty(o.Icon) || o.Icon == "none" ? o.Type : o.Icon,
                WarThunderMap.Coalition(o.Color, player?.Color), null, null, null, yaw: o.Heading, u: u, v: v));
        }

        RaiseEvent(TelemetryEventKinds.Objects, string.Join('\n', lines));
    }

    private void HandleHudMessages(byte[] data)
    {
        if (!WarThunderHudMessages.TryParse(data, out var events, out var damage))
            return;
        if (_hudResync)
        {
            // Asked for everything of the battle: counters lower than ours = a new battle, take all of them.
            if (events.Count > 0 && events[^1].Id < _lastEvtId)
                _lastEvtId = 0;
            if (damage.Count > 0 && damage[^1].Id < _lastDmgId)
                _lastDmgId = 0;
            _hudResync = false;
        }

        _lastEvtId = RaiseNew(events, _lastEvtId);
        _lastDmgId = RaiseNew(damage, _lastDmgId);
    }

    private long RaiseNew(List<WarThunderHudMessages.Entry> entries, long last)
    {
        foreach (var entry in entries)
        {
            if (entry.Id <= last)
                continue;
            last = entry.Id;
            RaiseEvent(TelemetryEventKinds.Message, entry.Text);
        }

        return last;
    }

    private void HandleMission(byte[] data)
    {
        var compact = WarThunderJson.Compact(data);
        if (compact is null || compact == _lastMission)
            return;
        _lastMission = compact;
        RaiseEvent(WarThunderProtocol.MissionEventKind, compact);
    }

    /// <summary>Recording keys of a valid frame: the map position and the artificial horizon with its decided sign.</summary>
    private void AddRecordingValues(Dictionary<string, double> values, double now)
    {
        var tas = values.TryGetValue("wt.tas_km_h", out var tasKmh) ? tasKmh / 3.6 : 0;
        _lastTasMs = tas;
        if (values.TryGetValue(WarThunderProtocol.IndicatorsPrefix + "aviahorizon_roll", out var rollRaw))
        {
            _lastRollRaw = rollRaw;
            values[WarThunderProtocol.RollKey] = rollRaw * RollSign.Sign;
        }
        else
        {
            _lastRollRaw = double.NaN;
        }

        if (values.TryGetValue(WarThunderProtocol.IndicatorsPrefix + "aviahorizon_pitch", out var pitchRaw))
        {
            // Wings about level: pitch ~ flight path angle + AoA.
            if (tas > 40 && values.TryGetValue("wt.vy_m_s", out var vy) && Math.Abs(pitchRaw) > 8
                && (double.IsNaN(_lastRollRaw) || Math.Abs(_lastRollRaw) < 30))
            {
                var physical = Math.Asin(Math.Clamp(vy / tas, -1, 1)) * TelemetryNav.RadToDeg
                               + (values.TryGetValue("wt.aoa_deg", out var aoa) ? aoa : 0);
                if (Math.Abs(physical) > 8)
                    PitchSign.Add(Math.Sign(pitchRaw) == Math.Sign(physical));
            }

            values[WarThunderProtocol.PitchKey] = pitchRaw * PitchSign.Sign;
        }

        var map = _mapValues;
        if (map is not null && now - map.Time <= MapValuesMaxAgeSeconds)
        {
            values[WarThunderProtocol.MapXKey] = map.U;
            values[WarThunderProtocol.MapYKey] = map.V;
            if (map.Heading is { } heading)
                values[WarThunderProtocol.MapHeadingKey] = heading;
        }
    }

    /// <summary>A banked turn turns towards the low wing: a right turn (heading growing) goes with right wing down.</summary>
    private void VoteRoll(double? heading, double now)
    {
        if (heading is { } h && _lastHeading is { } h0)
        {
            var dt = now - _lastHeadingTime;
            if (dt is > 0.2 and < 3)
            {
                var dh = h - h0;
                if (dh > 180)
                    dh -= 360;
                else if (dh < -180)
                    dh += 360;
                var rate = dh / dt;
                if (Math.Abs(rate) > 4 && Math.Abs(_lastRollRaw) > 15 && _lastTasMs > 40)
                    RollSign.Add(Math.Sign(_lastRollRaw) == Math.Sign(rate));
            }
        }

        _lastHeading = heading;
        _lastHeadingTime = now;
    }

    private async Task PollLoop(CancellationToken stop)
    {
        var period = TimeSpan.FromSeconds(1 / _rateHz);
        using var timer = new PeriodicTimer(period);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stop).ConfigureAwait(false);
                if (!await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                Failures++;
                ServerAnswering = false;
                LastError = ex.Message;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(OfflineRetrySeconds), stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task<byte[]> GetAsync(string path, CancellationToken stop)
    {
        Requests++;
        using var response = await _http.GetAsync(new Uri(_baseUri, path), HttpCompletionOption.ResponseContentRead, stop).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(stop).ConfigureAwait(false);
    }
}
