using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace JoyPro.Gui.Services;

// The HUD for OBS's Browser Source (docs/hud.md "Streamer mode"): a tiny web server on 127.0.0.1 only (never another
// computer), port 8765 by default:
//   GET /hud            the page (Resources/hud-stream.html): the HUD with a transparent background, same styles;
//   GET /hud/events     Server-Sent Events: "layout" (widgets, places, styles, pictures) and "state" (live input);
//   GET /hud/img/<id>   the pictures as PNG (with their transparency);
//   GET /hud/state      the last layout and state as JSON (for a look in a browser, and the tests).
// It does nothing while no page is connected: the HUD builds the state only for connected pages (HudStreamHub), and
// each page has one sleeping thread (below normal priority) that wakes when there is something new. A request from
// another host name than 127.0.0.1 / localhost is refused (no DNS rebinding).
public sealed class HudStreamServer : IDisposable
{
    public const int MaxClients = 8;

    private readonly IHudStreamSource source;
    private TcpListener? listener;
    private Thread? acceptThread;
    private readonly List<EventClient> clients = [];
    private readonly object gate = new();
    private volatile bool running;
    private static readonly Lazy<byte[]> Page = new(LoadPage);

    public HudStreamServer(int port, IHudStreamSource source)
    {
        Port = port;
        this.source = source;
    }

    public int Port { get; private set; }

    public bool IsRunning => running;

    public string Url => "http://127.0.0.1:" + Port + "/hud";

    public int ClientCount
    {
        get
        {
            lock (gate)
                return clients.Count;
        }
    }

    // Raised (on the server's thread) when a page connects or leaves.
    public event Action? ClientsChanged;

    // Throws a SocketException when the port is taken.
    public void Start()
    {
        if (running)
            return;
        TcpListener l = new(IPAddress.Loopback, Port);
        l.Start();
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        listener = l;
        running = true;
        acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "JoyPro HUD stream", Priority = ThreadPriority.BelowNormal };
        acceptThread.Start();
    }

    public void Stop()
    {
        running = false;
        try
        {
            listener?.Stop();
        }
        catch (Exception)
        {
        }
        listener = null;
        EventClient[] all;
        lock (gate)
        {
            all = clients.ToArray();
            clients.Clear();
        }
        foreach (EventClient client in all)
            client.Close();
    }

    public void Dispose() => Stop();

    // A message for every connected page (the latest state replaces one not sent yet; a layout is always sent).
    public void Broadcast(ReadOnlySpan<byte> message, bool replaceable)
    {
        lock (gate)
        {
            for (int i = 0; i < clients.Count; i++)
                clients[i].Offer(message, replaceable);
        }
    }

    private void AcceptLoop()
    {
        while (running)
        {
            TcpClient tcp;
            try
            {
                TcpListener? l = listener;
                if (l == null)
                    return;
                tcp = l.AcceptTcpClient();
            }
            catch (Exception)
            {
                if (!running)
                    return;
                continue;
            }
            Thread worker = new(() => Serve(tcp)) { IsBackground = true, Name = "JoyPro HUD stream client", Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }
    }

    private void Serve(TcpClient tcp)
    {
        try
        {
            tcp.NoDelay = true;
            NetworkStream stream = tcp.GetStream();
            stream.ReadTimeout = 5000;
            string? request = ReadRequest(stream, out string host);
            if (request == null)
            {
                tcp.Close();
                return;
            }
            if (!LocalHost(host))
            {
                Respond(stream, "403 Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Only this PC (127.0.0.1)."));
                tcp.Close();
                return;
            }
            string path = request;
            int query = path.IndexOf('?');
            if (query >= 0)
                path = path[..query];
            if (path is "/" or "/hud" or "/hud/")
            {
                // The page comes with the HUD as it is now: it shows at once, the events keep it live.
                string snapshot = source.Snapshot().Replace("</", "<\\/", StringComparison.Ordinal);
                string html = Encoding.UTF8.GetString(Page.Value).Replace("<!--HUD-SNAPSHOT-->", "<script>window.__hud=" + snapshot + ";</script>", StringComparison.Ordinal);
                Respond(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
            }
            else if (path == "/hud/events")
            {
                ServeEvents(tcp, stream);
                return;
            }
            else if (path == "/hud/state")
            {
                Respond(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(source.Snapshot()));
            }
            else if (path.StartsWith("/hud/img/", StringComparison.Ordinal) && source.Picture(path["/hud/img/".Length..]) is { } png)
            {
                Respond(stream, "200 OK", "image/png", png, "Cache-Control: max-age=31536000, immutable\r\n");
            }
            else
            {
                Respond(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found"));
            }
            tcp.Close();
        }
        catch (Exception)
        {
            try
            {
                tcp.Close();
            }
            catch (Exception)
            {
            }
        }
    }

    private void ServeEvents(TcpClient tcp, NetworkStream stream)
    {
        EventClient client;
        lock (gate)
        {
            if (clients.Count >= MaxClients || !running)
            {
                Respond(stream, "503 Service Unavailable", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Too many pages"));
                tcp.Close();
                return;
            }
            client = new EventClient(tcp, stream);
            clients.Add(client);
        }
        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: keep-alive\r\nX-Accel-Buffering: no\r\n\r\nretry: 2000\n\n");
        try
        {
            stream.Write(head, 0, head.Length);
            ClientsChanged?.Invoke();
            source.ClientJoined();
            client.Run();
        }
        catch (Exception)
        {
        }
        finally
        {
            lock (gate)
                clients.Remove(client);
            client.Close();
            ClientsChanged?.Invoke();
        }
    }

    private static bool LocalHost(string host)
    {
        string name = host;
        int colon = name.LastIndexOf(':');
        if (colon > 0 && !name.EndsWith(']'))
            name = name[..colon];
        return name is "127.0.0.1" or "localhost" or "[::1]" or "";
    }

    // "GET <path> HTTP/1.1" and the Host header; null for anything else.
    private static string? ReadRequest(NetworkStream stream, out string host)
    {
        host = string.Empty;
        byte[] buffer = new byte[4096];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = stream.Read(buffer, length, buffer.Length - length);
            if (read <= 0)
                return null;
            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }
        string text = Encoding.ASCII.GetString(buffer, 0, length);
        string[] lines = text.Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length < 2 || first[0] != "GET")
            return null;
        foreach (string line in lines)
        {
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                host = line[5..].Trim();
        }
        return first[1];
    }

    private static void Respond(NetworkStream stream, string status, string type, byte[] body, string extraHeaders = "")
    {
        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                                              "\r\n" + extraHeaders + "Connection: close\r\n\r\n");
        stream.Write(head, 0, head.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    private static byte[] LoadPage()
    {
        using Stream? resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("JoyPro.Gui.HudStream.html");
        if (resource == null)
            return Encoding.UTF8.GetBytes("<!doctype html><title>JoyPro HUD</title>");
        using MemoryStream memory = new();
        resource.CopyTo(memory);
        return memory.ToArray();
    }

    // One connected page: a thread that sleeps until there is a message (or 15 s passed: a keep-alive comment, which
    // also finds a page that is gone). The newest state waits in a buffer that is reused; layouts are queued.
    private sealed class EventClient
    {
        private static readonly byte[] KeepAlive = Encoding.ASCII.GetBytes(":\n\n");
        private readonly TcpClient tcp;
        private readonly NetworkStream stream;
        private readonly AutoResetEvent signal = new(false);
        private readonly object gate = new();
        private readonly ConcurrentQueue<byte[]> queued = new();
        private byte[] pending = new byte[16384];
        private byte[] sending = new byte[16384];
        private int pendingLength;
        private volatile bool closed;

        public EventClient(TcpClient tcp, NetworkStream stream)
        {
            this.tcp = tcp;
            this.stream = stream;
        }

        public void Offer(ReadOnlySpan<byte> message, bool replaceable)
        {
            if (closed)
                return;
            if (!replaceable)
            {
                queued.Enqueue(message.ToArray());
            }
            else
            {
                lock (gate)
                {
                    if (pending.Length < message.Length)
                        pending = new byte[message.Length * 2];
                    message.CopyTo(pending);
                    pendingLength = message.Length;
                }
            }
            signal.Set();
        }

        public void Run()
        {
            stream.WriteTimeout = 5000;
            while (!closed)
            {
                bool woke = signal.WaitOne(15000);
                if (closed)
                    return;
                if (!woke)
                {
                    stream.Write(KeepAlive, 0, KeepAlive.Length);
                    continue;
                }
                while (queued.TryDequeue(out byte[]? message))
                    stream.Write(message, 0, message.Length);
                int length;
                lock (gate)
                {
                    length = pendingLength;
                    pendingLength = 0;
                    (pending, sending) = (sending, pending);
                }
                if (length > 0)
                    stream.Write(sending, 0, length);
            }
        }

        public void Close()
        {
            closed = true;
            signal.Set();
            try
            {
                tcp.Close();
            }
            catch (Exception)
            {
            }
        }
    }
}

// What the server needs from the HUD (HudStreamHub).
public interface IHudStreamSource
{
    // The layout and the state as one JSON document.
    string Snapshot();

    // A picture's PNG by its id, or null.
    byte[]? Picture(string id);

    // A page connected: it gets the layout and the state at once.
    void ClientJoined();
}
