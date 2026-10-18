using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace JoyPro.Ffb.Core.Telemetry.XPlane;

/// <summary>X-Plane 11 / 12: subscribes to the datarefs of <see cref="XPlaneProtocol"/> over UDP (RREF) and feeds the
/// answers into an <see cref="XPlaneTelemetrySource"/>.</summary>
/// <remarks>
/// <para>Start binds a local UDP socket (an ephemeral port on 127.0.0.1, or on all addresses when the sim is on another
/// machine) and sends the subscriptions from it to the sim (default 127.0.0.1:49000) in batches of 100, 100 ms apart.
/// X-Plane answers to that socket. While nothing arrives for <see cref="ResubscribeAfter"/> (X-Plane not started yet,
/// restarted, or still loading) the subscriptions are sent again; nothing else is sent. Stop sends freq 0 for every
/// index (unsubscribe) and closes the socket. RREF requests only ask for values: nothing in the sim changes.</para>
/// <para>One receive loop (awaits the socket, no polling) and one control loop (wakes every 250 ms).</para>
/// </remarks>
public sealed class XPlaneTelemetryFeed : ITelemetryFeed
{
    public static readonly IPEndPoint DefaultSimEndpoint = new(IPAddress.Loopback, XPlaneProtocol.DefaultPort);

    private readonly XPlaneTelemetrySource _source;
    private readonly int _localPort;
    private readonly double _rateHz;
    private readonly object _gate = new();
    private UdpClient? _client;
    private CancellationTokenSource? _stop;
    private Task? _receive;
    private Task? _control;
    private long _lastAnswerTicks;
    private long _startTicks;
    private bool _nothingListens;

    public XPlaneTelemetryFeed(XPlaneTelemetrySource source, IPEndPoint? simEndpoint = null, int localPort = 0, double rateHz = 20)
    {
        _source = source;
        SimEndpoint = simEndpoint ?? DefaultSimEndpoint;
        _localPort = localPort;
        _rateHz = Math.Clamp(rateHz, 1, 99);
    }

    public FrameTelemetrySource Source => _source;

    /// <summary>Where the requests go.</summary>
    public IPEndPoint SimEndpoint { get; }

    /// <summary>The local port answers come to (after Start), else the configured one (0 = any).</summary>
    public int LocalPort { get; private set; }

    public string Endpoint => "UDP " + SimEndpoint.Address + ":" + SimEndpoint.Port.ToString(CultureInfo.InvariantCulture) + " (RREF)";

    /// <summary>Silence after which the subscriptions are sent again (tests: shorter).</summary>
    public TimeSpan ResubscribeAfter { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Pause between batches of <see cref="XPlaneProtocol.RequestBatch"/> requests (tests: 0).</summary>
    public TimeSpan BatchDelay { get; init; } = XPlaneProtocol.RequestBatchDelay;

    /// <summary>How many times the full subscription list was sent since Start.</summary>
    public int SubscribeRounds { get; private set; }

    public long RequestsSent => Interlocked.Read(ref _requestsSent);
    private long _requestsSent;

    public long DatagramsReceived => Interlocked.Read(ref _datagramsReceived);
    private long _datagramsReceived;

    /// <summary>True once X-Plane answered since Start.</summary>
    public bool Answering => Interlocked.Read(ref _lastAnswerTicks) != 0;

    public string? Problem
    {
        get
        {
            lock (_gate)
            {
                if (_client is null || Answering)
                    return null;
                if (Environment.TickCount64 - _startTicks < ResubscribeAfter.TotalMilliseconds)
                    return null;
            }

            var where = SimEndpoint.Address + ":" + SimEndpoint.Port.ToString(CultureInfo.InvariantCulture);
            return (_nothingListens ? "Nothing listens on UDP " + where + " (X-Plane is not running there). " : "X-Plane does not answer on UDP " + where + ". ") +
                   "Start X-Plane; in X-Plane 12 Settings > Network \"Accept incoming connections\" must be on, and a firewall must let X-Plane receive UDP " +
                   SimEndpoint.Port.ToString(CultureInfo.InvariantCulture) + ".";
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_client is not null)
                return;
            var bind = IPAddress.IsLoopback(SimEndpoint.Address) ? IPAddress.Loopback : IPAddress.Any;
            var client = new UdpClient(new IPEndPoint(bind, _localPort));
            _client = client;
            LocalPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
            _stop = new CancellationTokenSource();
            Interlocked.Exchange(ref _lastAnswerTicks, 0);
            _startTicks = Environment.TickCount64;
            _nothingListens = false;
            SubscribeRounds = 0;
            var token = _stop.Token;
            _receive = Task.Run(() => ReceiveLoop(client, token));
            _control = Task.Run(() => ControlLoop(client, token));
        }
    }

    public void Stop()
    {
        UdpClient? client;
        Task? receive, control;
        lock (_gate)
        {
            client = _client;
            if (client is null)
                return;
            _client = null;
            receive = _receive;
            control = _control;
            _stop?.Cancel();
        }

        try
        {
            control?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        // Unsubscribe from the same socket (X-Plane keys subscriptions by the sender's address and port).
        try
        {
            SendAll(client, 0, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        client.Dispose();
        try
        {
            receive?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        lock (_gate)
        {
            _stop?.Dispose();
            _stop = null;
            _receive = _control = null;
        }
    }

    public void Dispose() => Stop();

    private async Task ControlLoop(UdpClient client, CancellationToken token)
    {
        try
        {
            await SendAll(client, null, token).ConfigureAwait(false);
            var lastRound = Environment.TickCount64;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250, Math.Max(20, ResubscribeAfter.TotalMilliseconds / 4))), token).ConfigureAwait(false);
                var now = Environment.TickCount64;
                var lastAnswer = Interlocked.Read(ref _lastAnswerTicks);
                var silentSince = Math.Max(lastAnswer, lastRound);
                if (now - silentSince >= ResubscribeAfter.TotalMilliseconds)
                {
                    await SendAll(client, null, token).ConfigureAwait(false);
                    lastRound = Environment.TickCount64;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    /// <summary>Sends every subscription (frequency null = the catalogue's rates, 0 = unsubscribe).</summary>
    private async Task SendAll(UdpClient client, int? frequency, CancellationToken token)
    {
        var subscriptions = XPlaneProtocol.Subscriptions(_rateHz);
        for (var i = 0; i < subscriptions.Count; i++)
        {
            if (i > 0 && i % XPlaneProtocol.RequestBatch == 0 && BatchDelay > TimeSpan.Zero)
                await Task.Delay(BatchDelay, token).ConfigureAwait(false);
            var (index, path, rate) = subscriptions[i];
            var packet = XPlaneProtocol.BuildRequest(frequency ?? rate, index, path);
            try
            {
                client.Send(packet, packet.Length, SimEndpoint);
                Interlocked.Increment(ref _requestsSent);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                _nothingListens = true;
            }
        }

        if (frequency is null)
            SubscribeRounds++;
    }

    private async Task ReceiveLoop(UdpClient client, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // ICMP port unreachable (nobody on 49000) and similar: keep listening.
                if (ex.SocketErrorCode == SocketError.ConnectionReset)
                    _nothingListens = true;
                continue;
            }

            // Only the sim's answers (a stray datagram from elsewhere is ignored).
            if (!result.RemoteEndPoint.Address.Equals(SimEndpoint.Address) && !(IPAddress.IsLoopback(SimEndpoint.Address) && IPAddress.IsLoopback(result.RemoteEndPoint.Address)))
                continue;
            Interlocked.Increment(ref _datagramsReceived);
            Interlocked.Exchange(ref _lastAnswerTicks, Environment.TickCount64);
            _nothingListens = false;
            try
            {
                _source.Ingest(result.Buffer);
            }
            catch (Exception ex)
            {
                LastIngestError = ex;
            }
        }
    }

    public Exception? LastIngestError { get; private set; }
}
