using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using JoyPro.Ffb.Core.Host;
using JoyPro.Ffb.Core.Telemetry;

namespace JoyPro.Ffb;

/// <summary>JoyPro.Ffb simulate: plays the synthetic test flight (<see cref="SyntheticFlight"/>) the way a game sends it,
/// for tests without the games.</summary>
/// <remarks>
/// DCS: the hook's text lines over UDP to 127.0.0.1:port; IL-2: telemetrydevice packets over UDP; War Thunder: a small
/// HTTP server on 127.0.0.1:port answering /state and /indicators. --gap-from/--gap-to make the game go quiet (UDP:
/// nothing sent; War Thunder: "valid": false) to test the telemetry timeout.
/// </remarks>
internal static class Simulator
{
    public static int Run(TelemetryGame game, int port, double seconds, double rateHz, double gapFrom, double gapTo)
    {
        var watch = Stopwatch.StartNew();
        bool InGap() => watch.Elapsed.TotalSeconds >= gapFrom && watch.Elapsed.TotalSeconds < gapTo;
        if (game.Id == TelemetryGame.WarThunder)
            return ServeWarThunder(port, seconds, watch, InGap);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var target = new IPEndPoint(IPAddress.Loopback, port);
        var period = TimeSpan.FromSeconds(1 / Math.Clamp(rateHz, 1, 200));
        using var timer = new PeriodicTimer(period);
        FlightState? previous = null;
        var lastPlane = double.NegativeInfinity;
        var sent = new SendGaps();
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            var now = watch.Elapsed.TotalSeconds;
            if (InGap())
            {
                sent.Pause();   // the requested silence is not a late send
            }
            else
            {
                var state = SyntheticFlight.At(now);
                byte[] datagram;
                if (game.Id == TelemetryGame.Dcs)
                {
                    if (now - lastPlane > 5)
                    {
                        var plane = Encoding.UTF8.GetBytes("plane Synthetic-F");
                        udp.Send(plane, plane.Length, target);
                        lastPlane = now;
                    }

                    datagram = Encoding.UTF8.GetBytes(SyntheticFlight.DcsLine(state));
                }
                else
                {
                    datagram = SyntheticFlight.Il2Packet(state, previous);
                }

                try
                {
                    udp.Send(datagram, datagram.Length, target);
                    sent.Mark(now);
                }
                catch (SocketException)
                {
                    // Nobody listens yet (ICMP port unreachable): keep going.
                }

                previous = state;
            }

            timer.WaitForNextTickAsync().AsTask().Wait();
        }

        Console.WriteLine("simulate: " + sent.Count.ToString(CultureInfo.InvariantCulture) + " datagrams to 127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) +
                          ", longest gap " + sent.LongestGap.ToString("0.000", CultureInfo.InvariantCulture) + " s");
        return 0;
    }

    private static int ServeWarThunder(int port, double seconds, Stopwatch watch, Func<bool> inGap)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var served = new SendGaps();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClientAsync(stop.Token).AsTask().Result;
                }
                catch (AggregateException)
                {
                    break;
                }

                _ = Task.Run(() =>
                {
                    using (client)
                    {
                        ServeConnection(client, watch, inGap, served, stop.Token);
                    }
                });
            }
        }
        finally
        {
            listener.Stop();
        }

        Console.WriteLine("simulate: served " + served.Count.ToString(CultureInfo.InvariantCulture) + " War Thunder /state requests on 127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) +
                          ", longest gap " + served.LongestGap.ToString("0.000", CultureInfo.InvariantCulture) + " s");
        return 0;
    }

    // HTTP/1.1 with keep-alive: one GET after the other on the same connection. Answers to /state are counted.
    private static void ServeConnection(TcpClient client, Stopwatch watch, Func<bool> inGap, SendGaps served, CancellationToken stop)
    {
        try
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            while (!stop.IsCancellationRequested)
            {
                var requestLine = reader.ReadLine();
                if (requestLine is null)
                    break;
                string? header;
                while (!string.IsNullOrEmpty(header = reader.ReadLine()))
                {
                }

                var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
                var state = SyntheticFlight.At(watch.Elapsed.TotalSeconds);
                var flying = !inGap();
                string body;
                var status = "200 OK";
                if (path.StartsWith("/state", StringComparison.Ordinal))
                    body = SyntheticFlight.WarThunderState(state, flying);
                else if (path.StartsWith("/indicators", StringComparison.Ordinal))
                    body = SyntheticFlight.WarThunderIndicators(state, flying);
                else
                {
                    body = "{}";
                    status = "404 Not Found";
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: application/json\r\nContent-Length: " +
                                                   bytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: keep-alive\r\n\r\n");
                stream.Write(head);
                stream.Write(bytes);
                if (path.StartsWith("/state", StringComparison.Ordinal))
                    served.Mark(watch.Elapsed.TotalSeconds);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Counts sends (or answers) and keeps the longest time between two of them. On a busy PC the simulator
    /// runs late like a game would; the smoke test widens its timing checks by what this measured.</summary>
    private sealed class SendGaps
    {
        private readonly object _gate = new();
        private double _last = double.NaN;
        private long _count;
        private double _longest;

        public long Count { get { lock (_gate) return _count; } }
        public double LongestGap { get { lock (_gate) return _longest; } }

        public void Mark(double now)
        {
            lock (_gate)
            {
                if (!double.IsNaN(_last))
                    _longest = Math.Max(_longest, now - _last);
                _last = now;
                _count++;
            }
        }

        public void Pause()
        {
            lock (_gate)
                _last = double.NaN;
        }
    }
}
