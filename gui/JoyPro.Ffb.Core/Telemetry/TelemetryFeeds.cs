using JoyPro.Ffb.Core.Telemetry.Bms;
using JoyPro.Ffb.Core.Telemetry.Dcs;
using JoyPro.Ffb.Core.Telemetry.Elite;
using JoyPro.Ffb.Core.Telemetry.Il2;
using JoyPro.Ffb.Core.Telemetry.Msfs;
using JoyPro.Ffb.Core.Telemetry.WarThunder;
using JoyPro.Ffb.Core.Telemetry.XPlane;

namespace JoyPro.Ffb.Core.Telemetry;

/// <summary>A game's telemetry source together with what feeds it (a UDP socket, an HTTP poll, a shared memory poll,
/// a SimConnect session, a file watcher).</summary>
/// <remarks>Start and Stop are idempotent; nothing is received, polled or opened before Start and after Stop.
/// Dispose stops and frees everything. A feed never writes to the game (X-Plane's RREF requests and SimConnect data
/// requests only ask for values).</remarks>
public interface ITelemetryFeed : IDisposable
{
    FrameTelemetrySource Source { get; }

    /// <summary>For the status line: "UDP 41992", "http://127.0.0.1:8111/", "shared memory FalconSharedMemoryArea".</summary>
    string Endpoint { get; }

    /// <summary>Why nothing arrives, if the feed knows ("SimConnect.dll not found", "Status.json missing"), else null.</summary>
    string? Problem { get; }

    void Start();

    void Stop();
}

/// <summary>Test seams and overrides for <see cref="TelemetryFeeds.Create"/>.</summary>
public sealed class TelemetryFeedOptions
{
    /// <summary>The local UDP port to listen on (DCS, IL-2, X-Plane); 0 picks a free one (tests); null = the default.</summary>
    public int? Port { get; init; }

    /// <summary>War Thunder: base URL (tests: a stub) and handler.</summary>
    public Uri? WarThunderUri { get; init; }
    public HttpMessageHandler? WarThunderHandler { get; init; }
    public double WarThunderPollHz { get; init; } = 20;

    /// <summary>X-Plane: where the sim listens for RREF requests (default 127.0.0.1:49000).</summary>
    public System.Net.IPEndPoint? XPlaneEndpoint { get; init; }

    /// <summary>How often a polled source (shared memory, SimConnect, files) is read, per second.</summary>
    public double PollHz { get; init; } = 20;

    /// <summary>Falcon BMS: shared memory names (tests use their own, so a running BMS is never touched).</summary>
    public string? BmsMemoryPrefix { get; init; }

    /// <summary>MSFS: the SimConnect.dll to load (default: the one in the running sim's install folder).</summary>
    public string? SimConnectDll { get; init; }

    /// <summary>MSFS tests: replaces the native SimConnect client.</summary>
    public object? SimConnectClient { get; init; }

    /// <summary>Elite Dangerous: the journal folder (default Saved Games\Frontier Developments\Elite Dangerous).</summary>
    public string? EliteFolder { get; init; }

    /// <summary>The running game's exe (lets MSFS find its SimConnect.dll), when known.</summary>
    public string? GameExePath { get; init; }
}

/// <summary>Creates the telemetry feed of a game (ids of JoyPro.TelemetrySessions.TelemetryRecordingGames).</summary>
public static class TelemetryFeeds
{
    /// <exception cref="ArgumentException">The game has no telemetry JoyPro reads.</exception>
    public static ITelemetryFeed Create(string gameId, IFfbClock clock, TelemetryFeedOptions? options = null)
    {
        options ??= new TelemetryFeedOptions();
        var game = JoyPro.TelemetrySessions.TelemetryRecordingGames.Find(gameId)
                   ?? throw new ArgumentException("JoyPro reads no telemetry of '" + gameId + "'.", nameof(gameId));
        switch (game.Id)
        {
            case "DCS":
            {
                var dcs = new DcsTelemetrySource(clock);
                return new UdpTelemetryFeed(dcs, options.Port ?? DcsTelemetryProtocol.Port, data => dcs.Ingest(data.Span));
            }
            case "IL2Game":
            case "IL2Korea":
            {
                var il2 = new Il2TelemetrySource(clock, game.Title);
                return new UdpTelemetryFeed(il2, options.Port ?? 41994, data => il2.Ingest(data.Span));
            }
            case "WarThunder":
                return new WarThunderTelemetryFeed(new WarThunderTelemetrySource(clock, options.WarThunderUri, options.WarThunderPollHz, options.WarThunderHandler));
            case "FalconBMS":
                return new BmsTelemetryFeed(new BmsTelemetrySource(clock), options.BmsMemoryPrefix, options.PollHz);
            case "MSFS2024":
            case "MSFS2020":
                return new MsfsTelemetryFeed(game.Id, game.Title, clock, options);
            case "EliteDangerous":
                // Status.json changes a few times a second at most: 10 polls per second are plenty.
                return new EliteTelemetryFeed(new EliteTelemetrySource(clock), options.EliteFolder, Math.Min(options.PollHz, 10));
            case "XPlane12":
            case "XPlane11":
            {
                var xplane = new XPlaneTelemetrySource(clock, game.Title, options.PollHz);
                return new XPlaneTelemetryFeed(xplane, options.XPlaneEndpoint, options.Port ?? 0, options.PollHz);
            }
            default:
                throw new ArgumentException("JoyPro has no telemetry reader for " + game.Title + " yet.", nameof(gameId));
        }
    }
}

/// <summary>A source fed by datagrams on a local UDP port (DCS, IL-2). The socket is open only between Start and Stop.</summary>
public sealed class UdpTelemetryFeed : ITelemetryFeed
{
    private readonly Action<ReadOnlyMemory<byte>> _ingest;
    private readonly int _port;
    private readonly object _gate = new();
    private UdpTelemetryReceiver? _receiver;

    public UdpTelemetryFeed(FrameTelemetrySource source, int port, Action<ReadOnlyMemory<byte>> ingest)
    {
        Source = source;
        _port = port;
        _ingest = ingest;
    }

    public FrameTelemetrySource Source { get; }

    /// <summary>The port actually listened on (after Start), else the configured one.</summary>
    public int Port => _receiver?.Port ?? _port;

    public string Endpoint => "UDP " + Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string? Problem { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            if (_receiver is not null)
                return;
            try
            {
                _receiver = new UdpTelemetryReceiver(_port, _ingest);
                Problem = null;
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                Problem = "UDP port " + _port.ToString(System.Globalization.CultureInfo.InvariantCulture) + " is in use (" + ex.Message + ")";
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _receiver?.Dispose();
            _receiver = null;
        }
    }

    public void Dispose() => Stop();
}

/// <summary>War Thunder: the source polls the game's web server itself between Start and Stop.</summary>
public sealed class WarThunderTelemetryFeed : ITelemetryFeed
{
    private readonly WarThunderTelemetrySource _source;

    public WarThunderTelemetryFeed(WarThunderTelemetrySource source) => _source = source;

    public FrameTelemetrySource Source => _source;

    /// <summary>For the session recorder: also poll the map (player position, other objects), HUD messages and the
    /// mission (see <see cref="WarThunderTelemetrySource.RecordExtras"/>). Off by default; may be switched any time.</summary>
    public bool RecordExtras
    {
        get => _source.RecordExtras;
        set => _source.RecordExtras = value;
    }

    public string Endpoint => _source.BaseUri.ToString();
    public string? Problem => _source.ServerAnswering || _source.Requests == 0 ? null : _source.LastError;
    public void Start() => _source.Start();
    public void Stop() => _source.Stop();
    public void Dispose() => _source.Dispose();
}
