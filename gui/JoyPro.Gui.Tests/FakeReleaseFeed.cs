using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JoyPro.Gui.Tests;

// A release as tools/publish_release.ps1 makes it and GitHub's releases/latest describes it, in a local folder: the
// package (JoyPro.Gui.exe, engine\engine.exe, a DB file), its .sha256 and latest.json. The assets point at the files
// (file://) or at a LocalReleaseServer. For the updater's tests and the journey; nothing is published.
internal sealed class FakeReleaseFeed
{
    public required string Folder { get; init; }
    public required string FeedPath { get; init; }
    public required string PackagePath { get; init; }
    public required string ChecksumPath { get; init; }
    public required string Tag { get; init; }

    public const string DefaultNotes = "What's new in this test release:\n- Settings sections you can fold and move\n- Hidden games on the Start page\n- The export page remembers your choices";

    public static FakeReleaseFeed Create(string folder, string version, string notes = DefaultNotes, bool badHash = false, bool withPackage = true, string? urlBase = null)
    {
        Directory.CreateDirectory(folder);
        string tag = "v" + version;
        string name = "JoyPro2-" + version + "-win-x64";
        string content = Path.Combine(folder, "content-" + version);
        if (Directory.Exists(content))
            Directory.Delete(content, true);
        Directory.CreateDirectory(Path.Combine(content, "engine", "DB", "Functions"));
        File.WriteAllText(Path.Combine(content, "JoyPro.Gui.exe"), "fake JoyPro " + version);
        File.WriteAllText(Path.Combine(content, "engine", "engine.exe"), "fake engine " + version);
        File.WriteAllText(Path.Combine(content, "engine", "DB", "Functions", "functions.txt"), "functions of " + version);
        string zip = Path.Combine(folder, name + ".zip");
        if (File.Exists(zip))
            File.Delete(zip);
        ZipFile.CreateFromDirectory(content, zip);
        string hash;
        using (FileStream stream = File.OpenRead(zip))
            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (badHash)
            hash = new string('0', 64);
        string sha = zip + ".sha256";
        File.WriteAllText(sha, hash + "  " + name + ".zip", Encoding.ASCII);

        string Url(string file) => urlBase != null ? urlBase.TrimEnd('/') + "/" + Path.GetFileName(file) : new Uri(file).AbsoluteUri;
        List<object> assets = [];
        if (withPackage)
        {
            assets.Add(new { name = name + ".zip", size = new FileInfo(zip).Length, browser_download_url = Url(zip) });
            assets.Add(new { name = name + ".zip.sha256", size = new FileInfo(sha).Length, browser_download_url = Url(sha) });
        }
        string feed = Path.Combine(folder, "latest-" + version + (badHash ? "-badhash" : string.Empty) + (withPackage ? string.Empty : "-nopackage") + ".json");
        File.WriteAllText(feed, JsonSerializer.Serialize(new
        {
            tag_name = tag,
            name = "JoyPro2 " + version,
            body = notes,
            html_url = "https://github.com/Holdi601/JoystickProfiler2/releases/tag/" + tag,
            draft = false,
            prerelease = false,
            published_at = "2026-10-17T12:00:00Z",
            assets
        }));
        return new FakeReleaseFeed { Folder = folder, FeedPath = feed, PackagePath = zip, ChecksumPath = sha, Tag = tag };
    }

    // An address nothing listens on: "no internet".
    public static string UnreachableFeed() => "http://127.0.0.1:" + FreePort() + "/repos/x/y/releases/latest";

    public static int FreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

// A tiny HTTP server on localhost for the updater's tests: serves the files of a folder; /ratelimit answers 403 like
// GitHub when too many checks came from one address, /missing 404.
internal sealed class LocalReleaseServer : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly string folder;
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    public LocalReleaseServer(string folder)
    {
        this.folder = folder;
        Port = FakeReleaseFeed.FreePort();
        BaseUrl = "http://localhost:" + Port + "/";
        listener.Prefixes.Add(BaseUrl);
        listener.Start();
        loop = Task.Run(ServeAsync);
    }

    public int Port { get; }

    public string BaseUrl { get; }

    public int Requests;

    private async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            Interlocked.Increment(ref Requests);
            try
            {
                string name = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath.TrimStart('/'));
                string file = Path.Combine(folder, name);
                if (name == "ratelimit")
                {
                    context.Response.StatusCode = 403;
                }
                else if (File.Exists(file))
                {
                    byte[] bytes = await File.ReadAllBytesAsync(file);
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        listener.Close();
        try
        {
            loop.Wait(2000);
        }
        catch (AggregateException)
        {
        }
    }
}
