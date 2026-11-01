using System;
using System.Collections.Generic;
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
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using SkiaSharp;
using Xunit;

namespace JoyPro.Gui.Tests;

// The 2.5.1 HUD enhancement (docs/hud.md): every element can be shown and hidden, a long list of style presets with
// design options per element type and own styles (saved, exported, imported), all kept over a restart; the aircraft
// widget (the aircraft's own stick and throttle lighting the control the pressed button is); the stream page for OBS
// (transparent background, live state by Server-Sent Events, this PC only).
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class HudStreamerTests
{
    private const string Grip = TesterBatch250Tests.Grip;
    private const string Base = TesterBatch250Tests.Base;
    private const string Throttle = TesterBatch250Tests.Throttle;

    // ---- Styles ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Presets_AreMany_Unique_AndEachMakesAValidLook()
    {
        Assert.True(HudStylePresets.All.Count >= 30, "presets: " + HudStylePresets.All.Count);
        Assert.Equal(HudStylePresets.All.Count, HudStylePresets.All.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["Glass", "Neon", "Minimal"], HudStylePresets.All.Take(3).Select(p => p.Name));
        foreach (HudLookSettings preset in HudStylePresets.All)
        {
            HudLook look = new(preset);
            Dictionary<string, string> css = look.CssVariables();
            foreach (string key in new[] { "accent", "cardBg", "font", "gimbal", "bar", "lever", "opacity", "chipShape" })
                Assert.True(css.ContainsKey(key), preset.Name + " has no " + key);
            Assert.Contains(look.Settings.GimbalShape, HudLook.GimbalShapes);
            Assert.Contains(look.Settings.BarShape, HudLook.BarShapes);
        }
        // Many looks, not one look in many colours: the presets use every shape.
        Assert.Equal(HudLook.GimbalShapes.Count, HudStylePresets.All.Select(p => p.GimbalShape).Distinct().Count());
        Assert.Equal(HudLook.BarShapes.Count, HudStylePresets.All.Select(p => p.BarShape).Distinct().Count());
        Assert.True(HudStylePresets.All.Select(p => p.Font).Distinct().Count() >= 5);
    }

    [Fact]
    public void Normalized_KeepsValuesInRange_AndOneSpellingPerColour()
    {
        HudLookSettings odd = new() { Accent = "#38bdf8", Opacity = 7, GimbalShape = "hexagon", BarHeight = -3, Track = "nonsense", Font = " " };
        HudLookSettings n = odd.Normalized();
        Assert.Equal("#FF38BDF8", n.Accent);
        Assert.Equal(1, n.Opacity);
        Assert.Equal("Ring", n.GimbalShape);
        Assert.Equal(2, n.BarHeight);
        Assert.Equal("#26FFFFFF", n.Track);
        Assert.Equal("Default", n.Font);
        Assert.True(new HudLookSettings { Accent = "#38BDF8" }.SameDesign(new HudLookSettings { Accent = "#ff38bdf8", Name = "other" }));
    }

    // Every choice of the HUD page survives a restart: style, a changed option, an own style, a widget's own style,
    // what each widget shows on screen and in the stream, the outputs and the update rate.
    [Fact]
    public async Task Hud_Settings_SurviveARestart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "hud-settings-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", folder);
        try
        {
            MainWindowViewModel first = await TesterBatch250Tests.HudVmAsync(persisted: true);
            first.HudStyle = "Amber MFD";
            Assert.Equal("Amber MFD", first.HudContext.Look.Settings.Name);
            first.HudLookEditor.GimbalShape = "Radar";
            Assert.Equal("Radar", first.HudContext.Look.Settings.GimbalShape);
            first.HudStyleName = "My amber";
            first.SaveHudStyleCommand.Execute(null);
            Assert.Equal(MainWindowViewModel.HudStyleOwnPrefix + "My amber", first.HudStyle);

            HudWidget grip = first.HudWidgets.Single(w => w.Device?.Device == Grip);
            grip.StreamVisible = false;
            grip.ShowChips = false;
            grip.ShowValues = false;
            grip.ShowKind = false;
            first.SelectedHudWidget = grip;
            first.SelectedHudWidgetStyle = first.HudWidgetStyleOptions.Single(o => o.Id == "Cyberpunk");
            HudWidget header = first.HudWidgets.Single(w => w.IsHeader);
            header.ShowHeaderPicture = false;
            HudWidget aircraft = first.HudWidgets.Single(w => w.IsAircraft);
            aircraft.ShowThrottle = false;
            aircraft.StreamVisible = true;

            first.HudOnScreen = false;
            first.HudStreamBrowser = true;
            first.HudStreamPort = 8799;
            first.HudStreamWindow = true;
            first.HudStreamBackground = "#FF00FF";
            first.HudMaxFps = 60;
            first.HudPreviewStream = true;
            first.SaveHudLayoutNow();

            MainWindowViewModel second = await TesterBatch250Tests.HudVmAsync(persisted: true);
            Assert.Equal(MainWindowViewModel.HudStyleOwnPrefix + "My amber", second.HudStyle);
            Assert.Equal("Radar", second.HudContext.Look.Settings.GimbalShape);
            Assert.Equal("Radar", second.HudLookEditor.GimbalShape);
            Assert.Contains(second.HudStyleOptions, o => o.Id == MainWindowViewModel.HudStyleOwnPrefix + "My amber");
            HudWidget again = second.HudWidgets.Single(w => w.Device?.Device == Grip);
            Assert.False(again.StreamVisible);
            Assert.True(again.Visible);
            Assert.False(again.ShowChips);
            Assert.False(again.ShowValues);
            Assert.False(again.ShowKind);
            Assert.True(again.HasOwnStyle);
            Assert.Equal("Cyberpunk", again.Look.Settings.Name);
            Assert.False(second.HudWidgets.Single(w => w.IsHeader).ShowHeaderPicture);
            Assert.False(second.HudWidgets.Single(w => w.IsAircraft).ShowThrottle);
            Assert.True(second.HudWidgets.Single(w => w.IsAircraft).StreamVisible);
            Assert.False(second.HudOnScreen);
            Assert.True(second.HudStreamBrowser);
            Assert.Equal(8799, (int)second.HudStreamPort);
            Assert.True(second.HudStreamWindow);
            Assert.Equal("#FF00FF", second.HudStreamBackground);
            Assert.Equal(60, second.HudMaxFps);
            // The designer's preview: "The stream" stays picked.
            Assert.True(second.HudPreviewStream);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previous);
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    // Undo changes goes back to the style before the changes (it went to Glass once an option was changed by hand, since
    // the list then shows no style); › steps to the next style of the list.
    [Fact]
    public async Task UndoChanges_GoesBackToTheStyleBeforeTheChanges_NextStepsThroughTheList()
    {
        MainWindowViewModel vm = await TesterBatch250Tests.HudVmAsync();
        vm.HudStyle = "Amber MFD";
        HudLookSettings amber = HudStylePresets.Find("Amber MFD")!;
        string other = HudLook.GimbalShapes.First(s => s != amber.GimbalShape);
        vm.HudLookEditor.GimbalShape = other;
        Assert.Equal(other, vm.HudContext.Look.Settings.GimbalShape);
        Assert.NotEqual("Amber MFD", vm.HudStyle);

        vm.ResetHudStyleCommand.Execute(null);
        Assert.Equal("Amber MFD", vm.HudStyle);
        Assert.Equal(amber.GimbalShape, vm.HudContext.Look.Settings.GimbalShape);
        Assert.Equal(amber.GimbalShape, vm.HudLookEditor.GimbalShape);

        int at = vm.HudStyleOptions.IndexOf(vm.SelectedHudStyleOption!);
        vm.NextHudStyleCommand.Execute(null);
        Assert.Equal(vm.HudStyleOptions[(at + 1) % vm.HudStyleOptions.Count].Id, vm.HudStyle);
    }

    // A hud.json of 2.5.0 (style by name, accent, opacity, the green stream window) keeps its look.
    [Fact]
    public async Task Hud_250File_KeepsItsLook()
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "hud-250-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "settings"));
        string? previous = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", folder);
        try
        {
            string settings = AppInfo.SettingsFolder;
            Directory.CreateDirectory(settings);
            File.WriteAllText(Path.Combine(settings, "hud.json"),
                "{\"Widgets\":[{\"Id\":\"header\",\"Kind\":\"header\",\"X\":40,\"Y\":36,\"Visible\":false}],\"Accent\":\"#EF4444\",\"Style\":\"Neon\",\"Opacity\":0.7,\"StreamWindow\":true,\"StreamBackground\":\"#00FF00\"}");
            MainWindowViewModel vm = await TesterBatch250Tests.HudVmAsync(persisted: true);
            HudLookSettings look = vm.HudContext.Look.Settings;
            Assert.Equal("#FFEF4444", look.Accent);
            Assert.Equal(0.7, look.Opacity, 3);
            Assert.Equal(HudStylePresets.Find("Neon")!.CardBackground, look.CardBackground);
            Assert.True(vm.HudStreamWindow);
            Assert.Equal("#00FF00", vm.HudStreamBackground);
            HudWidget header = vm.HudWidgets.Single(w => w.IsHeader);
            Assert.False(header.Visible);
            Assert.False(header.StreamVisible);
            // An older HUD gets the aircraft widget unticked.
            Assert.False(vm.HudWidgets.Single(w => w.IsAircraft).Visible);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previous);
        }
    }

    [Fact]
    public async Task StyleFile_ExportImport_RoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "hud-style-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        MainWindowViewModel a = await TesterBatch250Tests.HudVmAsync();
        a.HudStyle = "Retro terminal";
        a.HudLookEditor.CardRadius = 9;
        a.HudStyleName = "Terminal 9";
        string file = Path.Combine(dir, a.SuggestedHudStyleFileName);
        Assert.True(a.ExportHudStyle(file));
        Assert.Contains("joypro-hud-style", File.ReadAllText(file));

        MainWindowViewModel b = await TesterBatch250Tests.HudVmAsync();
        Assert.True(b.ImportHudStyle(file));
        Assert.Equal(MainWindowViewModel.HudStyleOwnPrefix + "Terminal 9", b.HudStyle);
        Assert.Equal(9, b.HudContext.Look.Settings.CardRadius);
        Assert.Equal("Courier New", b.HudContext.Look.Settings.Font);
        // Not a style: refused, the look stays.
        File.WriteAllText(Path.Combine(dir, "theme.json"), "{\"format\":\"joypro-theme\"}");
        Assert.False(b.ImportHudStyle(Path.Combine(dir, "theme.json")));
        Assert.Equal(9, b.HudContext.Look.Settings.CardRadius);
        // A preset's name cannot be taken by an own style.
        b.HudStyleName = "Glass";
        b.SaveHudStyleCommand.Execute(null);
        Assert.DoesNotContain(b.HudStyleOptions, o => o.Id == MainWindowViewModel.HudStyleOwnPrefix + "Glass");
    }

    // ---- The aircraft widget -----------------------------------------------------------------------------------------

    [Fact]
    public void Aircraft_LightsTheControlOfThePressedButton_AndMovesWithTheSources()
    {
        HudDevice stick = new(Grip, "Grip", "Warthog", "stick", 19, 1,
            [("JOY_X", "stick.axis.roll", "Roll"), ("JOY_Y", "stick.axis.pitch", "Pitch")], new Dictionary<string, string>(), [], "right")
        {
            Controls = new Dictionary<string, string> { ["JOY_BTN1"] = "stick.trigger.stage1", ["JOY_BTN7"] = "stick.hat.tms.up", ["JOY_BTN9"] = "stick.hat.tms.down", ["JOY_BTN2"] = "stick.weapon_release" }
        };
        HudDevice throttle = new(Throttle, "Throttle", "Throttle", "throttle", 32, 0, [("JOY_Z", "throttle.axis.left", "Left")], new Dictionary<string, string>(), [], "left");
        HudAircraft.Control tms = new("stick.hat.tms", "TMS (Target Management Switch)", "stick");
        tms.Markers.Add(new HudAircraftMarker("stick.hat.tms", "TMS", 0.5, 0.1));
        HudAircraft.Control trigger = new("stick.trigger", "Trigger", "stick");
        trigger.Markers.Add(new HudAircraftMarker("stick.trigger", "Trigger", 0.4, 0.2));
        HudAircraft aircraft = new("test", "Test jet", "test", null, null, [tms, trigger]);
        aircraft.BindDevices([stick, throttle]);
        aircraft.SetSources(stick, throttle);

        aircraft.OnInput(stick, "JOY_BTN7", true);
        Assert.True(tms.Markers[0].IsPressed);
        Assert.Contains("TMS (Target Management Switch)", aircraft.PressedChips);
        aircraft.OnInput(stick, "JOY_BTN9", true);
        aircraft.OnInput(stick, "JOY_BTN7", false);
        Assert.True(tms.Markers[0].IsPressed);
        aircraft.OnInput(stick, "JOY_BTN9", false);
        Assert.False(tms.Markers[0].IsPressed);
        aircraft.OnInput(stick, "JOY_BTN1", true);
        Assert.True(trigger.Markers[0].IsPressed);
        Assert.False(aircraft.Knows(stick, "JOY_BTN2"));

        // Centred first (an axis at an end since its first report counts as dead), then full right and forward.
        stick.ApplyAxes([0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5]);
        stick.ApplyAxes([1.0, 0.0, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5]);
        throttle.ApplyAxes([0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5]);
        throttle.ApplyAxes([0.5, 0.5, 1.0, 0.5, 0.5, 0.5, 0.5, 0.5]);
        aircraft.Flush();
        Assert.Equal(HudAircraft.MaxRoll, aircraft.StickRoll);
        Assert.Equal(HudAircraft.MaxPitch, aircraft.StickPitch);
        Assert.Equal(1.0, aircraft.ThrottleValue);
        Assert.Equal(-HudAircraft.ThrottleTravel / 2, aircraft.ThrottleOffset);

        // The live part allocates nothing: reports, the flush, the property changes.
        double[] a = [0.2, 0.7, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5], b = [0.8, 0.3, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5];
        for (int i = 0; i < 100; i++)
        {
            stick.ApplyAxes(i % 2 == 0 ? a : b);
            aircraft.Flush();
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
        {
            stick.ApplyAxes(i % 2 == 0 ? a : b);
            aircraft.Flush();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 4096, "10 000 reports with the aircraft widget allocated " + allocated + " bytes");
        aircraft.Detach();
    }

    // A picture set of the game (grip pictures as JPEG with a grey mask, like the picture packs) for the A-10C II of the
    // shipped cockpit database: the grips are cut to what is drawn and the TMS callout is the catalog's stick.hat.tms.
    [Fact]
    public void AircraftLoader_FindsTheGripsOfThePictureSet_AndPlacesTheControls()
    {
        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "hud-aircraft-" + Guid.NewGuid().ToString("N"));
        string set = Path.Combine(root, "DCS", "A-10C_2");
        Directory.CreateDirectory(set);
        WriteGrip(Path.Combine(set, "device_stick.jpg"), Path.Combine(set, "device_stick.mask.png"), 400, 600);
        WriteGrip(Path.Combine(set, "device_throttle.jpg"), Path.Combine(set, "device_throttle.mask.png"), 500, 400);
        File.WriteAllText(Path.Combine(set, "pictures.json"),
            "{\"format\":\"joypro-cockpit-pictures\",\"version\":1,\"game\":\"DCS\",\"plane\":\"A-10C_2\",\"planes\":[\"A-10C_2\"],\"views\":[" +
            "{\"id\":\"device:stick\",\"kind\":\"device\",\"device\":\"stick\",\"seat\":1,\"image\":\"device_stick.jpg\",\"mask\":\"device_stick.mask.png\",\"width\":400,\"height\":600," +
            "\"callouts\":[{\"n\":1,\"id\":\"STICK-BTN-TMS\",\"label\":\"TMS (Target Management Switch)\",\"x\":0.5,\"y\":0.3,\"kind\":\"part\"}]}," +
            "{\"id\":\"device:throttle\",\"kind\":\"device\",\"device\":\"throttle\",\"seat\":1,\"image\":\"device_throttle.jpg\",\"mask\":\"device_throttle.mask.png\",\"width\":500,\"height\":400,\"callouts\":[]}" +
            "]}");
        File.WriteAllText(Path.Combine(root, "DCS", "index.json"), "{\"format\":\"joypro-cockpit-pictures-index\",\"aircraft\":{\"A-10C II\":\"A-10C_2\",\"A-10C_2\":\"A-10C_2\"}}");

        Assert.Contains(HudAircraftLoader.Choices(root), c => c.Id == "DCS/A-10C_2");
        // Bitmaps need Avalonia's platform: on the render tests' thread.
        AvaloniaRenderThread.Run(() => CheckLoader(root));
    }

    private static void CheckLoader(string root)
    {
        HudAircraftLoader.Found? found = HudAircraftLoader.Find("DCS/A-10C_2", null, root);
        Assert.True(found != null, HudAircraftLoader.LastError);
        Assert.NotNull(found);
        Assert.NotNull(found!.Stick);
        Assert.NotNull(found.Throttle);
        // Cut to the drawn part (a quarter of the frame is empty all round) and made small.
        Assert.InRange(found.Stick!.CropX, 0.15, 0.26);
        Assert.InRange(found.Stick.CropW, 0.45, 0.6);
        Assert.True(found.Stick.Bitmap.PixelSize.Height <= HudAircraftLoader.PictureHeightPx);
        Assert.True(found.Stick.Png.Length > 100);
        Assert.NotNull(found.Variant);

        // The widget's model: the TMS callout is the catalog's stick.hat.tms, placed on the cut picture.
        HudAircraft aircraft = HudAircraftLoader.Build(found);
        Assert.True(aircraft.HasStick && aircraft.HasThrottle);
        HudAircraftMarker tms = Assert.Single(aircraft.StickMarkers, m => m.ControlId == "stick.hat.tms");
        Assert.InRange(tms.RelativeX, 0.45, 0.55);
        Assert.InRange(tms.RelativeY, 0.05, 0.15);

        found = HudAircraftLoader.Find(string.Empty, "A-10C_2", root);
        Assert.NotNull(found);
        Assert.Equal("DCS/A-10C_2", found!.Key);
    }

    private static void WriteGrip(string jpeg, string mask, int w, int h)
    {
        using SKBitmap color = new(w, h);
        using SKBitmap grey = new(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool inside = x > w / 4 && x < w * 3 / 4 && y > h / 4 && y < h * 3 / 4;
                color.SetPixel(x, y, inside ? new SKColor(120, 120, 130) : new SKColor(34, 34, 34));
                grey.SetPixel(x, y, inside ? SKColors.White : SKColors.Black);
            }
        }
        using (SKData data = SKImage.FromBitmap(color).Encode(SKEncodedImageFormat.Jpeg, 90))
            File.WriteAllBytes(jpeg, data.ToArray());
        using (SKData data = SKImage.FromBitmap(grey).Encode(SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(mask, data.ToArray());
    }

    // ---- The stream page ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task StreamPage_IsTransparent_LiveAndOnlyForThisPc()
    {
        MainWindowViewModel vm = await TesterBatch250Tests.HudVmAsync();
        vm.HudStreamPort = FreePort();
        vm.StartHudStreamServer();
        HudStreamServer server = vm.HudStreamServerForTests!;
        try
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
            string url = "http://127.0.0.1:" + server.Port;
            HttpResponseMessage page = await http.GetAsync(url + "/hud");
            Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
            string html = await page.Content.ReadAsStringAsync();
            Assert.Contains("background: transparent !important", html);
            Assert.Contains("new EventSource(\"/hud/events\")", html);
            Assert.DoesNotContain("<link", html);

            // Another host name (DNS rebinding): refused.
            using (HttpRequestMessage foreign = new(HttpMethod.Get, url + "/hud"))
            {
                foreign.Headers.Host = "evil.example";
                Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(foreign)).StatusCode);
            }

            // A page connects: it gets the layout and the state, then each change.
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            NetworkStream stream = client.GetStream();
            byte[] request = Encoding.ASCII.GetBytes("GET /hud/events HTTP/1.1\r\nHost: 127.0.0.1:" + server.Port + "\r\n\r\n");
            await stream.WriteAsync(request);
            for (int i = 0; i < 200 && server.ClientCount == 0; i++)
                await Task.Delay(20);
            vm.HudPump.Flush();
            string received = await ReadUntilAsync(stream, "event: state", TimeSpan.FromSeconds(10));
            Assert.Contains("text/event-stream", received);
            Assert.Contains("event: layout", received);
            Assert.Equal(1, server.ClientCount);
            string layout = received[(received.IndexOf("event: layout\ndata: ", StringComparison.Ordinal) + 20)..];
            layout = layout[..layout.IndexOf("\n\n", StringComparison.Ordinal)];
            using (JsonDocument doc = JsonDocument.Parse(layout))
            {
                JsonElement widgets = doc.RootElement.GetProperty("widgets");
                Assert.Contains(widgets.EnumerateArray(), w => w.GetProperty("k").GetString() == "device" && w.GetProperty("css").GetProperty("cardBg").GetString()!.StartsWith("rgba(", StringComparison.Ordinal));
            }

            // Live: the Rhino moves, a button of the grip goes down.
            vm.OnHudAxes(new JoystickAxisSnapshot(Base, [0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5]));
            vm.OnHudAxes(new JoystickAxisSnapshot(Base, [1.0, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5]));
            vm.OnHudInput(new JoystickInputEvent(Grip, "JOY_BTN1", true));
            vm.HudPump.Flush();
            received = await ReadUntilAsync(stream, "\"Trigger\"", TimeSpan.FromSeconds(10));
            Assert.Contains("\"g\":[1,", received);

            // A widget unticked for the stream leaves the page's layout.
            HudWidget grip = vm.HudWidgets.Single(w => w.Device?.Device == Grip);
            grip.StreamVisible = false;
            vm.HudPump.Flush();
            received = await ReadUntilAsync(stream, "event: layout", TimeSpan.FromSeconds(10));
            Assert.DoesNotContain("\"i\":\"" + grip.Config.Id + "\"", received[received.LastIndexOf("event: layout", StringComparison.Ordinal)..].Split("\n\n")[0]);
        }
        finally
        {
            vm.StopHudStreamServer();
        }
        Assert.False(server.IsRunning);
    }

    // Nothing is built while no page is connected.
    [Fact]
    public async Task StreamHub_WithoutAPage_BuildsNothing()
    {
        MainWindowViewModel vm = await TesterBatch250Tests.HudVmAsync();
        vm.HudStreamPort = FreePort();
        vm.StartHudStreamServer();
        try
        {
            for (int i = 0; i < 50; i++)
            {
                vm.OnHudAxes(new JoystickAxisSnapshot(Base, [i % 2, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5]));
                vm.HudPump.Flush();
            }
            Assert.Equal(0, vm.HudStreamHubForTests!.Published);
        }
        finally
        {
            vm.StopHudStreamServer();
        }
    }

    [Fact]
    public void Pump_KeepsTheNewestReportPerDevice_AndAppliesThemTogether()
    {
        List<JoystickAxisSnapshot> applied = [];
        int flushed = 0;
        HudInputPump pump = new(applied.Add, () => flushed++);
        pump.Offer(new JoystickAxisSnapshot("a", [0.1]));
        pump.Offer(new JoystickAxisSnapshot("a", [0.2]));
        pump.Offer(new JoystickAxisSnapshot("b", [0.3]));
        pump.Flush();
        Assert.Equal(2, applied.Count);
        Assert.Equal(0.2, applied.Single(s => s.Device == "a").Axes[0]);
        Assert.Equal(1, flushed);
        pump.Flush();
        Assert.Equal(2, applied.Count);
        Assert.Equal(2, flushed);
    }

    internal static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // Reads the event stream until the text came.
    internal static async Task<string> ReadUntilAsync(NetworkStream stream, string text, TimeSpan timeout)
    {
        StringBuilder sb = new();
        byte[] buffer = new byte[65536];
        DateTime end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (stream.DataAvailable)
            {
                int read = await stream.ReadAsync(buffer);
                sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                if (sb.ToString().Contains(text, StringComparison.Ordinal))
                    return sb.ToString();
            }
            else
            {
                await Task.Delay(20);
            }
        }
        throw new TimeoutException("No '" + text + "' in: " + sb);
    }
}
