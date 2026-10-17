using System.Net;
using System.Net.Sockets;

namespace JoyPro.Ffb.Core.Telemetry;

/// <summary>Receives datagrams on a local UDP port and hands each one to a parser (an IL-2 or DCS source).</summary>
/// <remarks>
/// Listens on the loopback address by default: the games send to 127.0.0.1. Nothing is ever sent. A bad datagram
/// never stops the loop; the handler decides what is valid. Port 0 picks a free port (tests).
/// </remarks>
public sealed class UdpTelemetryReceiver : IDisposable
{
    private readonly UdpClient _client;
    private readonly Action<ReadOnlyMemory<byte>> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public UdpTelemetryReceiver(int port, Action<ReadOnlyMemory<byte>> handler, IPAddress? address = null)
    {
        _handler = handler;
        _client = new UdpClient(new IPEndPoint(address ?? IPAddress.Loopback, port));
        Port = ((IPEndPoint)_client.Client.LocalEndPoint!).Port;
        _loop = Task.Run(ReceiveLoop);
    }

    public int Port { get; }
    public long Received { get; private set; }
    public Exception? LastHandlerError { get; private set; }

    public static UdpTelemetryReceiver ForIl2(int port, Il2.Il2TelemetrySource source) =>
        new(port, datagram => source.Ingest(datagram.Span));

    public static UdpTelemetryReceiver ForDcs(Dcs.DcsTelemetrySource source, int port = Dcs.DcsTelemetryProtocol.Port) =>
        new(port, datagram => source.Ingest(datagram.Span));

    private async Task ReceiveLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _client.ReceiveAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // ICMP port unreachable and similar: keep listening.
                continue;
            }

            Received++;
            try
            {
                _handler(result.Buffer);
            }
            catch (Exception ex)
            {
                LastHandlerError = ex;
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _client.Dispose();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}
