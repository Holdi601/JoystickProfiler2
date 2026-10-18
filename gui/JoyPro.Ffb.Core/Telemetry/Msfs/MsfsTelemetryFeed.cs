namespace JoyPro.Ffb.Core.Telemetry.Msfs;

/// <summary>MSFS 2020 / 2024: one background thread connects to SimConnect, waits on SimConnect's event handle (no
/// polling), hands every message to <see cref="MsfsTelemetrySource"/> and does the once-per-second work.</summary>
/// <remarks>While SimConnect.dll is missing or the sim does not answer, the thread retries every
/// <see cref="RetrySeconds"/> (sleeping on the stop signal in between). RECV_QUIT (the sim closed) ends the session and
/// the thread waits for the next sim start. Nothing is loaded or opened before Start and after Stop.</remarks>
public sealed class MsfsTelemetryFeed : ITelemetryFeed
{
    public const double RetrySeconds = 3;
    public const string ApplicationName = "JoyPro telemetry";

    /// <summary>At most this many messages per wake-up before the thread looks at the clock again.</summary>
    private const int MaxMessagesPerWake = 2000;

    private readonly MsfsTelemetrySource _source;
    private readonly Func<(ISimConnectClient? Client, bool Owned, string? Problem)> _connect;
    private readonly object _gate = new();
    private readonly double _retrySeconds;
    private ManualResetEvent? _stop;
    private Thread? _thread;
    private volatile string? _problem;
    private volatile string? _location;
    private long _connections;
    private volatile bool _connected;

    /// <summary>The feed for a game id ("MSFS2024", "MSFS2020"); <see cref="TelemetryFeedOptions.SimConnectClient"/>
    /// (an <see cref="ISimConnectClient"/>) replaces the native client.</summary>
    public MsfsTelemetryFeed(string gameId, string title, IFfbClock clock, TelemetryFeedOptions options)
        : this(new MsfsTelemetrySource(clock, title, options.PollHz), ClientFactory(gameId, options))
    {
    }

    public MsfsTelemetryFeed(MsfsTelemetrySource source, Func<(ISimConnectClient? Client, bool Owned, string? Problem)> connect, double retrySeconds = RetrySeconds)
    {
        _source = source;
        _connect = connect;
        _retrySeconds = Math.Max(0.01, retrySeconds);
    }

    public FrameTelemetrySource Source => _source;

    public string Endpoint => _location is { } dll ? "SimConnect (" + dll + ")" : "SimConnect";

    public string? Problem => _problem;

    /// <summary>True while a SimConnect connection is open.</summary>
    public bool IsConnected => _connected;

    /// <summary>Successful connections since the feed was made (a reconnect after the sim restarted counts).</summary>
    public long Connections => Interlocked.Read(ref _connections);

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null)
                return;
            _stop = new ManualResetEvent(false);
            _thread = new Thread(Run) { IsBackground = true, Name = "JoyPro MSFS telemetry" };
            _thread.Start(_stop);
        }
    }

    public void Stop()
    {
        Thread? thread;
        ManualResetEvent? stop;
        lock (_gate)
        {
            thread = _thread;
            stop = _stop;
            _thread = null;
            _stop = null;
        }

        if (thread is null)
            return;
        stop!.Set();
        if (thread.Join(TimeSpan.FromSeconds(5)))
            stop.Dispose();
    }

    public void Dispose() => Stop();

    private static Func<(ISimConnectClient?, bool, string?)> ClientFactory(string gameId, TelemetryFeedOptions options)
    {
        if (options.SimConnectClient is ISimConnectClient fake)
            return () => (fake, false, null);
        var locator = new SimConnectLocator(gameId, options.SimConnectDll, options.GameExePath);
        NativeSimConnectClient? native = null;
        return () =>
        {
            if (native is not null)
                return (native, false, null);
            var dll = locator.Find();
            if (dll is null)
                return (null, false, "SimConnect.dll not found in " + (locator.Searched.Count == 0 ? "any MSFS folder" : string.Join(", ", locator.Searched)));
            try
            {
                native = new NativeSimConnectClient(dll);
                return (native, false, null);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                return (null, false, "SimConnect.dll could not be loaded (" + dll + "): " + ex.Message);
            }
        };
    }

    private void Run(object? state)
    {
        var stop = (ManualResetEvent)state!;
        using var dataReady = new AutoResetEvent(false);
        var handles = new WaitHandle[] { stop, dataReady };
        var retry = TimeSpan.FromSeconds(_retrySeconds);
        while (!stop.WaitOne(0))
        {
            ISimConnectClient? client = null;
            var owned = false;
            try
            {
                string? problem;
                (client, owned, problem) = _connect();
                if (client is null)
                {
                    _problem = problem ?? "SimConnect.dll not found";
                    stop.WaitOne(retry);
                    continue;
                }

                _location = client.Location;
                if (!client.Open(ApplicationName, dataReady))
                {
                    _problem = "the sim does not answer SimConnect yet (" + client.Location + ")";
                    stop.WaitOne(retry);
                    continue;
                }

                _connected = true;
                Interlocked.Increment(ref _connections);
                _problem = null;
                var reason = Session(client, stop, handles);
                _source.Disconnect(reason);
                if (reason is not null && !stop.WaitOne(0))
                    _problem = reason;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _problem = "SimConnect failed: " + ex.Message;
                try
                {
                    _source.Disconnect("SimConnect failed");
                }
                catch (Exception)
                {
                }
            }
            finally
            {
                _connected = false;
                try
                {
                    client?.Close();
                    if (owned)
                        client?.Dispose();
                }
                catch (Exception)
                {
                }
            }

            stop.WaitOne(retry);
        }
    }

    /// <summary>One connection. Returns why it ended (null = JoyPro stopped).</summary>
    private string? Session(ISimConnectClient client, ManualResetEvent stop, WaitHandle[] handles)
    {
        if (!_source.Connect(client))
            return "SimConnect refused the data requests";
        var clock = _source.Clock;
        var nextTick = clock.Now + 1;
        while (true)
        {
            var wait = Math.Clamp(nextTick - clock.Now, 0, 1);
            var signalled = WaitHandle.WaitAny(handles, TimeSpan.FromSeconds(wait));
            if (signalled == 0)
                return null;
            for (var n = 0; n < MaxMessagesPerWake && client.TryGetNextDispatch(out var message); n++)
            {
                if (_source.Handle(message) == MsfsTelemetrySource.MessageResult.Quit)
                    return "the sim closed SimConnect";
            }

            if (clock.Now >= nextTick)
            {
                nextTick = clock.Now + 1;
                if (!_source.Tick())
                    return "the sim stopped answering SimConnect";
            }
        }
    }
}
