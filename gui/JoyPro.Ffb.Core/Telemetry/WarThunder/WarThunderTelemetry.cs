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
/// </remarks>
public sealed class WarThunderTelemetrySource : FrameTelemetrySource, IDisposable
{
    public const double OfflineRetrySeconds = 2;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(400);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _baseUri;
    private readonly double _rateHz;
    private readonly Dictionary<string, double> _indicators = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task? _loop;

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

    /// <summary>True while the last request was answered (the server is up, even outside a flight).</summary>
    public bool ServerAnswering { get; private set; }

    public string? LastError { get; private set; }

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
                Vehicle = type;
        }

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!WarThunderProtocol.Read(stateJson, WarThunderProtocol.StatePrefix, values, out _))
            return false;
        lock (_indicators)
        {
            foreach (var pair in _indicators)
                values[pair.Key] = pair.Value;
        }

        Publish(new TelemetryFrame(Clock.Now, values));
        return true;
    }

    private async Task PollLoop(CancellationToken stop)
    {
        var period = TimeSpan.FromSeconds(1 / _rateHz);
        using var timer = new PeriodicTimer(period);
        var tick = 0L;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var state = await GetAsync(WarThunderProtocol.StatePath, stop).ConfigureAwait(false);
                var indicators = tick % 2 == 0 ? await GetAsync(WarThunderProtocol.IndicatorsPath, stop).ConfigureAwait(false) : null;
                ServerAnswering = true;
                Ingest(state, indicators);
                tick++;
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
