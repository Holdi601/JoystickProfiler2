using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The live check of the page for OBS (docs/hud.md "Streamer mode", "Live check in OBS"): the real HudStreamServer, started
// by Start HUD of a view model in a sandbox (JOYPRO_DATA_DIR in the output folder) with the user's four sticks
// (HudStickBaseTests) and the fake device stream moving every axis; a demo HUD or a copied hud.json. Checked: GET /hud,
// /hud/state and the live channel /hud/events (the layout, then states that change while the devices move). No window
// opens (the on-screen HUD and the stream window are off, no presenters).
//
// Opt-in: JOYPRO_HUD_LIVE=<output folder> (live-check.md, live-check.json there).
//   JOYPRO_HUD_LIVE_HUDJSON=<hud.json>  copied into the sandbox first (else JoyPro's default HUD).
//   JOYPRO_HUD_LIVE_PORT=<port>         the page's port (else a free one).
//   JOYPRO_HUD_LIVE_SECONDS=<seconds>   keeps the page running that long after the checks (for OBS: tools\obs_hud_live_check.ps1
//                                       starts OBS once ready.json is there); a file "stop" in the output folder ends it early.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class HudObsLiveTests
{
    [Fact]
    public void StreamPage_ServesTheHud_AndUpdatesLive()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_HUD_LIVE");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        foreach (string stale in new[] { "ready.json", "stop", "live-check.md", "live-check.json" })
            File.Delete(Path.Combine(outDir, stale));
        int port = int.TryParse(Environment.GetEnvironmentVariable("JOYPRO_HUD_LIVE_PORT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p is > 1023 and < 65536
            ? p
            : HudStreamerTests.FreePort();
        double hold = double.TryParse(Environment.GetEnvironmentVariable("JOYPRO_HUD_LIVE_SECONDS"), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? Math.Max(0, s) : 0;
        string? hudJson = Environment.GetEnvironmentVariable("JOYPRO_HUD_LIVE_HUDJSON");

        string sandbox = Path.Combine(outDir, "data");
        if (Directory.Exists(sandbox))
            Directory.Delete(sandbox, recursive: true);
        string? previous = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", sandbox);
        List<string> log = [];
        List<string> failures = [];
        void Note(string line) => log.Add(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + line);
        void Check(bool ok, string what)
        {
            Note((ok ? "ok    " : "FAIL  ") + what);
            if (!ok)
                failures.Add(what);
        }
        try
        {
            Directory.CreateDirectory(AppInfo.SettingsFolder);
            if (!string.IsNullOrWhiteSpace(hudJson))
            {
                File.Copy(hudJson, Path.Combine(AppInfo.SettingsFolder, "hud.json"), overwrite: true);
                Note("hud.json copied from " + hudJson);
            }
            else
            {
                Note("demo HUD (JoyPro's defaults)");
            }
            AvaloniaRenderThread.Run(() => RunOnUiThread(port, hold, outDir, Note, Check));
        }
        catch (Exception ex)
        {
            failures.Add("threw: " + ex.Message);
            Note("FAIL  threw: " + ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previous);
            File.Delete(Path.Combine(outDir, "ready.json"));
            StringBuilder md = new();
            md.AppendLine("# HUD page live check");
            md.AppendLine();
            md.AppendLine("Page: http://127.0.0.1:" + port + "/hud");
            md.AppendLine();
            md.AppendLine(failures.Count == 0 ? "Result: all checks passed." : "Result: " + failures.Count + " check(s) failed:\n- " + string.Join("\n- ", failures));
            md.AppendLine();
            md.AppendLine("```");
            foreach (string line in log)
                md.AppendLine(line);
            md.AppendLine("```");
            File.WriteAllText(Path.Combine(outDir, "live-check.md"), md.ToString());
            File.WriteAllText(Path.Combine(outDir, "live-check.json"),
                JsonSerializer.Serialize(new { port, url = "http://127.0.0.1:" + port + "/hud", passed = failures.Count == 0, failures, log }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(failures.Count == 0, string.Join("\n", log));
    }

    private static void RunOnUiThread(int port, double hold, string outDir, Action<string> note, Action<bool, string> check)
    {
        HudSoak.FakeStream capture = new(HudStickBaseTests.VJoy, HudStickBaseTests.Vkb, HudStickBaseTests.Rhino, HudStickBaseTests.WarBrd);
        MainWindowViewModel vm = new(new HudStickBaseTests.UserSticksEngine(), capture, loadPersistedUiSettings: true) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };
        AvaloniaRenderThread.Wait(vm.LoadHudDevicesCommand.ExecuteAsync(null));
        note("devices on the HUD: " + string.Join(", ", vm.HudWidgets.Where(w => w.Device != null).Select(w => w.Device!.ShortTitle + (w.Device.PairedBase != null ? " (on " + w.Device.PairedBase.ShortTitle + ")" : string.Empty))));
        // Only the page: no window on the screen.
        vm.HudOnScreen = false;
        vm.HudStreamWindow = false;
        vm.HudStreamPort = port;
        vm.HudStreamBrowser = true;
        foreach (Models.HudWidget w in vm.HudWidgets.Where(w => w.IsDevice))
            w.StreamVisible = true;
        AvaloniaRenderThread.Wait(vm.ToggleOverlayCommand.ExecuteAsync(null));
        try
        {
            HudStreamServer? server = vm.HudStreamServerForTests;
            check(server is { IsRunning: true } && server.Port == port, "Start HUD started the page server on port " + port + " (" + vm.HudStreamStatus + ")");
            if (server == null)
                return;
            string url = "http://127.0.0.1:" + port;
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };

            HttpResponseMessage page = AvaloniaRenderThread.Wait(http.GetAsync(url + "/hud"));
            string html = AvaloniaRenderThread.Wait(page.Content.ReadAsStringAsync());
            check(page.StatusCode == HttpStatusCode.OK && page.Content.Headers.ContentType?.MediaType == "text/html", "GET /hud: " + (int)page.StatusCode + " " + page.Content.Headers.ContentType?.MediaType + ", " + html.Length + " characters");
            check(html.Contains("new EventSource(\"/hud/events\")", StringComparison.Ordinal), "the page subscribes to /hud/events");
            check(html.Contains("transparent", StringComparison.Ordinal), "the page's background is transparent");

            HttpResponseMessage state = AvaloniaRenderThread.Wait(http.GetAsync(url + "/hud/state"));
            string stateJson = AvaloniaRenderThread.Wait(state.Content.ReadAsStringAsync());
            int widgets = 0;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(stateJson);
                if (doc.RootElement.TryGetProperty("layout", out JsonElement layout) && layout.TryGetProperty("widgets", out JsonElement list))
                    widgets = list.GetArrayLength();
                else if (doc.RootElement.TryGetProperty("widgets", out JsonElement direct))
                    widgets = direct.GetArrayLength();
            }
            catch (JsonException)
            {
            }
            check(state.StatusCode == HttpStatusCode.OK && widgets > 0, "GET /hud/state: " + (int)state.StatusCode + ", " + widgets + " widget(s) in the layout");

            // The live channel: the layout, then states that change while the fake devices move.
            (string received, int states, int distinct) = AvaloniaRenderThread.Wait(ReadEventsAsync(port, TimeSpan.FromSeconds(4)));
            check(received.Contains("text/event-stream", StringComparison.Ordinal), "/hud/events answers as text/event-stream");
            check(received.Contains("event: layout", StringComparison.Ordinal), "/hud/events sends the layout");
            check(states >= 5 && distinct >= 3, "/hud/events sends live states: " + states + " in 4 s, " + distinct + " different");

            if (hold > 0)
            {
                File.WriteAllText(Path.Combine(outDir, "ready.json"), JsonSerializer.Serialize(new { url = url + "/hud", port, pid = Environment.ProcessId, seconds = hold }));
                note("ready: the page runs for " + hold.ToString("0", CultureInfo.InvariantCulture) + " s (or until " + Path.Combine(outDir, "stop") + ")");
                Stopwatch clock = Stopwatch.StartNew();
                int maxClients = 0;
                while (clock.Elapsed.TotalSeconds < hold && !File.Exists(Path.Combine(outDir, "stop")))
                {
                    AvaloniaRenderThread.Wait(Task.Delay(250));
                    maxClients = Math.Max(maxClients, server.ClientCount);
                }
                note("held " + clock.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s; at most " + maxClients + " page(s) connected meanwhile (OBS: 1)");
            }
        }
        finally
        {
            vm.ToggleOverlayCommand.Execute(null);
            capture.Stop();
            AvaloniaRenderThread.Pump();
        }
        check(vm.HudStreamServerForTests == null, "Stop HUD stopped the page server");
    }

    // Reads /hud/events for a while: what came, how many states and how many different ones.
    private static async Task<(string Received, int States, int Distinct)> ReadEventsAsync(int port, TimeSpan time)
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /hud/events HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\n\r\n"));
        StringBuilder sb = new();
        byte[] buffer = new byte[65536];
        DateTime end = DateTime.UtcNow + time;
        while (DateTime.UtcNow < end)
        {
            if (stream.DataAvailable)
            {
                int read = await stream.ReadAsync(buffer);
                if (read <= 0)
                    break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            else
            {
                await Task.Delay(20);
            }
        }
        string text = sb.ToString();
        List<string> states = text.Split("\n\n").Where(e => e.Contains("event: state", StringComparison.Ordinal)).ToList();
        return (text, states.Count, states.Distinct(StringComparer.Ordinal).Count());
    }
}
