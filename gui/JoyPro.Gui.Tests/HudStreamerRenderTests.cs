using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views.Controls;
using SkiaSharp;
using Xunit;

namespace JoyPro.Gui.Tests;

// The 2.5.1 HUD rendered offscreen to look at (docs/hud.md): the HUD in many presets over a game picture and on a
// transparent background, the aircraft widget with the aircraft's own grips (the cockpit pictures of this PC, read
// only), the HUD Designer page at 1280 and 2267 wide in Light and Dark, and the stream page as a headless Edge sees it
// (its background must stay transparent). Opt-in, needs the whole application, run alone:
//   set JOYPRO_HUD_RENDER_OUT=<folder> [& set JOYPRO_HUD_ENGINE=<engine.exe> for the devices of this PC]
//   dotnet test --filter HudStreamerRenderTests
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class HudStreamerRenderTests
{
    public static readonly string[] Presets =
    [
        "Glass", "Neon", "Military green HUD", "Amber MFD", "Airliner PFD", "Streamer bold", "Synthwave", "Retro terminal",
        "High contrast light", "Outline only", "Compact", "Holo cyan", "Warbird", "Frosted light"
    ];

    [Fact]
    public void RenderHudStreamer()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_HUD_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        string? engine = Environment.GetEnvironmentVariable("JOYPRO_HUD_ENGINE");
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = string.IsNullOrWhiteSpace(engine)
                ? new MainWindowViewModel(new TesterBatch250Tests.GripOnBaseEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = typeof(MainWindowViewModel).Assembly.Location }
                : new MainWindowViewModel(new EngineCliClient(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine };
            AvaloniaRenderThread.Wait(vm.LoadHudDevicesCommand.ExecuteAsync(null));
            AvaloniaRenderThread.Wait(vm.HudPicturesTask);

            // The aircraft: the F-16C's grips from this PC's cockpit pictures when there are some.
            HudWidget aircraft = vm.HudWidgets.Single(w => w.IsAircraft);
            aircraft.Visible = true;
            aircraft.StreamVisible = true;
            // This PC's pictures (read only; the test's data folder is a sandbox).
            vm.HudAircraftRoot = Environment.GetEnvironmentVariable("JOYPRO_HUD_PICTURES")
                                 ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro2", "GameData", "CockpitPictures");
            string wanted = Environment.GetEnvironmentVariable("JOYPRO_HUD_AIRCRAFT") ?? "DCS/A-10C_2";
            string pick = HudAircraftLoader.Choices(vm.HudAircraftRoot).Select(c => c.Id).FirstOrDefault(id => id == wanted)
                          ?? HudAircraftLoader.Choices(vm.HudAircraftRoot).Select(c => c.Id).FirstOrDefault() ?? string.Empty;
            aircraft.AircraftChoice = pick;
            AvaloniaRenderThread.Wait(vm.RefreshHudAircraftAsync());
            vm.HudContext.HeaderText = "F-16C Viper · DCS";
            vm.OverlayLines.Add(new OverlayLine("Gear Toggle", "Landing Gear Lever - UP/DOWN", 20, Avalonia.Layout.HorizontalAlignment.Left));
            vm.OverlayLines.Add(new OverlayLine("TMS Up", "TMS - Up (Target Management Switch)", 20, Avalonia.Layout.HorizontalAlignment.Left));
            Arrange(vm);
            aircraft.X = 1500;
            aircraft.Y = 60;
            Animate(vm);

            Bitmap? backdrop = PreviewBackdrop();
            foreach (string preset in Presets)
            {
                vm.HudStyle = preset;
                AvaloniaRenderThread.Pump();
                RenderHud(vm, Path.Combine(outDir, "hud-" + Slug(preset) + ".png"), backdrop, stream: false);
            }
            vm.HudStyle = "Glass";
            RenderHud(vm, Path.Combine(outDir, "hud-stream-transparent.png"), null, stream: true);

            // The aircraft widget alone, three looks, stick deflected and a control lit.
            if (vm.CurrentHudAircraft is { } a)
            {
                foreach (HudAircraftMarker m in a.StickMarkers.Take(2).Concat(a.ThrottleMarkers.Take(1)))
                    m.IsPressed = true;
                if (a.Controls.FirstOrDefault(c => c.Markers.Count > 0) is { } lit)
                    a.PressedChips.Add(HudAircraft.ShortName(lit.Name));
                foreach (string preset in new[] { "Glass", "Military green HUD", "Streamer bold" })
                {
                    vm.HudStyle = preset;
                    RenderWidget(vm, aircraft, Path.Combine(outDir, "aircraft-" + Slug(preset) + ".png"), backdrop);
                }
                vm.HudStyle = "Glass";
            }
            foreach (HudWidget device in vm.HudWidgets.Where(w => w.IsDevice && w.Visible).Take(2))
            {
                foreach (string preset in new[] { "Glass", "Amber MFD", "Vector wireframe" })
                {
                    vm.HudStyle = preset;
                    RenderWidget(vm, device, Path.Combine(outDir, "device-" + Slug(device.Device!.ShortTitle) + "-" + Slug(preset) + ".png"), backdrop);
                }
            }
            vm.HudStyle = "Glass";

            // The designer page at 1280 and large, light and dark.
            vm.SelectedHudWidget = aircraft;
            foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
            {
                Application.Current!.RequestedThemeVariant = variant;
                foreach ((int width, int height) in new[] { (1280, 800), (2267, 1300) })
                {
                    vm.CurrentStage = UiStage.Hud;
                    AvaloniaRenderThread.Pump();
                    vm.HudStyle = theme == "dark" ? "Synthwave" : "Glass";
                    UiDesignRenderTests.RenderWindow(vm, width, height, Path.Combine(outDir, "designer-" + width + "-" + theme + ".png"));
                }
            }
            vm.SelectedHudWidget = vm.HudWidgets.FirstOrDefault(w => w.IsDevice);
            UiDesignRenderTests.RenderWindow(vm, 1280, 800, Path.Combine(outDir, "designer-1280-light-device.png"));
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            vm.CurrentStage = UiStage.Start;

            // The same style as hud-amber-mfd.png: the page must look like the app's HUD.
            vm.HudStyle = "Amber MFD";
            Animate(vm);
            RenderStreamPage(vm, outDir);
        });
    }

    // The widgets packed along the bottom as the designer does it.
    internal static void Arrange(MainWindowViewModel vm)
    {
        Grid measureRoot = new() { Width = 1920, Height = 1080 };
        HudSurface measure = new() { Fixed = true, Widgets = vm.HudWidgets, Context = vm.HudContext };
        measureRoot.Children.Add(measure);
        Window host = new() { Content = measureRoot };
        host.Measure(new Size(1920, 1080));
        host.Arrange(new Rect(0, 0, 1920, 1080));
        vm.ArrangeHudWidgets(measure.MeasuredWidgets());
        host.Content = null;
    }

    // Some input: sticks right and forward with twist, levers, a few buttons, a hat.
    internal static void Animate(MainWindowViewModel vm)
    {
        foreach (HudDevice device in vm.HudWidgets.Where(w => w.Device != null).Select(w => w.Device!))
        {
            device.ApplyAxes([0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5]);
            device.ApplyAxes([0.85, 0.2, 0.75, 0.6, 0.35, 0.8, 0.4, 0.3]);
            device.SetInput("JOY_BTN1", true);
            device.SetInput("JOY_BTN2", true);
            device.SetInput("JOY_BTN_POV1_UR", true);
        }
        vm.HudPump.Flush();
    }

    private static Bitmap? PreviewBackdrop()
    {
        string pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro2", "GameData", "CockpitPictures", "DCS", "F-16C_50", "overview.jpg");
        try
        {
            return File.Exists(pictures) ? new Bitmap(pictures) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void RenderHud(MainWindowViewModel vm, string file, Bitmap? backdrop, bool stream)
    {
        Grid root = new() { Width = 1920, Height = 1080, Background = stream ? Brushes.Transparent : new SolidColorBrush(Color.Parse("#1E293B")) };
        if (backdrop != null && !stream)
        {
            root.Children.Add(new Image { Source = backdrop, Stretch = Stretch.UniformToFill });
            root.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#55000000")) });
        }
        root.Children.Add(new HudSurface { Fixed = true, Stream = stream, Widgets = vm.HudWidgets, Context = vm.HudContext });
        Save(root, new Size(1920, 1080), file);
    }

    private static void RenderWidget(MainWindowViewModel vm, HudWidget widget, string file, Bitmap? backdrop)
    {
        double x = widget.X, y = widget.Y, scale = widget.Scale;
        widget.X = 20;
        widget.Y = 20;
        widget.Scale = 1;
        Grid root = new() { Background = new SolidColorBrush(Color.Parse("#334155")) };
        if (backdrop != null)
            root.Children.Add(new Image { Source = backdrop, Stretch = Stretch.UniformToFill, Opacity = 0.6 });
        root.Children.Add(new HudSurface { Widgets = [widget], Context = vm.HudContext });
        Save(root, new Size(1000, 560), file);
        widget.X = x;
        widget.Y = y;
        widget.Scale = scale;
    }

    private static void Save(Control control, Size size, string file)
    {
        Window host = new() { Content = control, Width = size.Width, Height = size.Height, SizeToContent = SizeToContent.Manual };
        for (int pass = 0; pass < 3; pass++)
        {
            host.Measure(size);
            host.Arrange(new Rect(size));
            control.Measure(size);
            control.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        using RenderTargetBitmap bitmap = new(new PixelSize((int)size.Width, (int)size.Height));
        bitmap.Render(control);
        bitmap.Save(file);
        host.Content = null;
    }

    // The page for OBS in a headless Edge (as OBS's Chromium shows it): screenshot on a transparent background; its
    // corner must stay fully transparent and the widgets must be drawn.
    private static void RenderStreamPage(MainWindowViewModel vm, string outDir)
    {
        string? edge = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
        }.FirstOrDefault(File.Exists);
        if (edge == null)
            return;
        vm.HudStreamPort = HudStreamerTests.FreePort();
        vm.StartHudStreamServer();
        string shot = Path.Combine(outDir, "stream-page-edge.png");
        string profile = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "edge-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (File.Exists(shot))
                File.Delete(shot);
            ProcessStartInfo start = new(edge)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (string arg in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--hide-scrollbars", "--user-data-dir=" + profile,
                         "--window-size=1920,1080", "--default-background-color=00000000", "--timeout=6000", "--screenshot=" + shot, vm.HudStreamUrl })
                start.ArgumentList.Add(arg);
            using Process process = Process.Start(start)!;
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception)
            {
            }
            Stopwatch clock = Stopwatch.StartNew();
            while (!process.HasExited && clock.Elapsed < TimeSpan.FromSeconds(40))
            {
                // The page connects: the HUD sends the layout and the state (the UI thread's update).
                AvaloniaRenderThread.Pump();
                vm.HudPump.Flush();
                System.Threading.Thread.Sleep(30);
            }
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            // The aircraft's pictures are served (the page asks for /hud/img/<id>).
            using System.Net.Http.HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
            System.Threading.Tasks.Task<string> stateTask = http.GetStringAsync(vm.HudStreamUrl + "/state");
            while (!stateTask.IsCompleted)
            {
                AvaloniaRenderThread.Pump();
                System.Threading.Thread.Sleep(10);
            }
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(stateTask.Result);
            File.WriteAllText(Path.Combine(outDir, "stream-page-state.json"), stateTask.Result);
            foreach (System.Text.Json.JsonElement w in doc.RootElement.GetProperty("layout").GetProperty("widgets").EnumerateArray().Where(w => w.GetProperty("k").GetString() == "aircraft"))
            {
                string id = w.GetProperty("sp").GetString() ?? string.Empty;
                Assert.False(string.IsNullOrEmpty(id), "the aircraft widget names no stick picture");
                byte[] picture = http.GetByteArrayAsync(vm.HudStreamUrl + "/img/" + id).GetAwaiter().GetResult();
                Assert.True(picture.Length > 100);
            }
        }
        finally
        {
            vm.StopHudStreamServer();
            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch (Exception)
            {
            }
        }
        Assert.True(File.Exists(shot), "Edge made no screenshot of " + vm.HudStreamUrl);
        using SKBitmap png = SKBitmap.Decode(shot);
        Assert.Equal(0, png.GetPixel(2, 2).Alpha);
        Assert.Equal(0, png.GetPixel(png.Width / 2, png.Height / 3).Alpha);
        int drawn = 0;
        for (int y = 0; y < png.Height; y += 8)
        {
            for (int x = 0; x < png.Width; x += 8)
            {
                if (png.GetPixel(x, y).Alpha > 0)
                    drawn++;
            }
        }
        Assert.True(drawn > 200, "the stream page drew nothing (" + drawn + " samples)");
    }

    private static string Slug(string text) => new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
}
