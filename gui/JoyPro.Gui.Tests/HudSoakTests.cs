using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views.Controls;
using SkiaSharp;
using Xunit;

namespace JoyPro.Gui.Tests;

// The HUD's background cost (docs/testing.md "Speed", HUD soak): the HUD on with a fake device stream for minutes, as
// a player has it - three devices reporting their axes like the DirectInput poll (every 15 ms, every axis moving), a
// button every half second, the HUD laid out and drawn in a root that is never shown, the aircraft widget with its
// grips, the stream page's server with one connected page reading the events. Measured after a warm-up: CPU of this
// process (% of one core), allocations per second, managed heap and working set growth, updates and events per second;
// checked against the budgets below. Opt-in (long): JOYPRO_HUD_SOAK=<minutes> [JOYPRO_HUD_SOAK_OUT=<folder>];
// the quality gate runs it as I12 with JOYPRO_QUALITY_GATE_HUD_SOAK=<minutes>.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class HudSoakTests
{
    [Fact]
    public void Hud_WithStreamPage_StaysWithinItsBudgets()
    {
        if (!double.TryParse(Environment.GetEnvironmentVariable("JOYPRO_HUD_SOAK"), NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes) || minutes <= 0)
            return;
        HudSoak.Result result = HudSoak.Run(TimeSpan.FromMinutes(minutes));
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_HUD_SOAK_OUT");
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "hud-soak.md"), result.Report());
        }
        Assert.True(result.Failures.Count == 0, result.Report());
    }
}

[SupportedOSPlatform("windows")]
internal static class HudSoak
{
    // The budgets (measured numbers in docs/testing.md "Speed"). CPU and allocations are of the whole test process: they
    // include the fake device stream (a new report array per poll, as the real poll makes) and the page reader; the real
    // app adds the DirectInput poll instead. Phase "stream": the stream page only (the player's own HUD off, nothing
    // drawn by JoyPro); phase "drawn": the on-screen HUD laid out and drawn as well (Avalonia's layout and drawing). The
    // baseline (the fake devices and the dispatcher with the HUD off) is measured first and only reported: the HUD's
    // own share is a phase minus the baseline.
    public static readonly Budget Baseline = new("baseline (fake devices, HUD off)", CpuPercent: 100, AllocKbPerSecond: 100000);
    public static readonly Budget Stream = new("stream page only", CpuPercent: 5, AllocKbPerSecond: 250);
    public static readonly Budget Drawn = new("HUD drawn + stream page", CpuPercent: 8, AllocKbPerSecond: 1500);
    public const double HeapGrowthBudgetMb = 4;        // managed heap after a full GC, end vs. after the warm-up
    public const double WorkingSetGrowthBudgetMb = 40;
    public const double MaxUpdatesPerSecond = 31;      // the HUD's cap is 30
    public const double FrameBudgetMs = 12;            // drawing the whole HUD once in software (upper bound of a frame)

    public sealed record Budget(string Name, double CpuPercent, double AllocKbPerSecond);

    public sealed class Phase
    {
        public Budget Budget = Stream;
        public TimeSpan Measured;
        public double CpuPercent;
        public double AllocKbPerSecond;
        public double HeapGrowthMb;
        public double WorkingSetGrowthMb;
        public double UpdatesPerSecond;
        public double EventsPerSecond;
        public double PageKbPerSecond;
        public double ReportsPerSecond;
    }

    public sealed class Result
    {
        public List<Phase> Phases = [];
        public double FrameMs;
        public List<string> Failures = [];

        public string Report()
        {
            StringBuilder sb = new();
            sb.AppendLine("# HUD soak");
            sb.AppendLine();
            sb.AppendLine("| Measure | " + string.Join(" | ", Phases.Select(p => p.Budget.Name)) + " | Budget |");
            sb.AppendLine("|---|" + string.Concat(Phases.Select(_ => "---|")) + "---|");
            Row(sb, "Measured (s, after a warm-up)", p => p.Measured.TotalSeconds.ToString("0", CultureInfo.InvariantCulture), "-");
            Row(sb, "Axis reports / s (3 devices)", p => p.ReportsPerSecond.ToString("0", CultureInfo.InvariantCulture), "-");
            Row(sb, "CPU (% of one core)", p => p.CpuPercent.ToString("0.00", CultureInfo.InvariantCulture), string.Join(" / ", Phases.Select(p => "< " + p.Budget.CpuPercent)));
            Row(sb, "Allocations (KB/s)", p => p.AllocKbPerSecond.ToString("0", CultureInfo.InvariantCulture), string.Join(" / ", Phases.Select(p => "< " + p.Budget.AllocKbPerSecond)));
            Row(sb, "Managed heap growth (MB)", p => p.HeapGrowthMb.ToString("0.00", CultureInfo.InvariantCulture), "< " + HeapGrowthBudgetMb);
            Row(sb, "Working set growth (MB)", p => p.WorkingSetGrowthMb.ToString("0.0", CultureInfo.InvariantCulture), "< " + WorkingSetGrowthBudgetMb);
            Row(sb, "HUD updates / s", p => p.UpdatesPerSecond.ToString("0.0", CultureInfo.InvariantCulture), "<= " + MaxUpdatesPerSecond);
            Row(sb, "Stream page events / s", p => p.EventsPerSecond.ToString("0.0", CultureInfo.InvariantCulture), "5 .. " + MaxUpdatesPerSecond);
            Row(sb, "Stream page data (KB/s)", p => p.PageKbPerSecond.ToString("0.0", CultureInfo.InvariantCulture), "-");
            sb.AppendLine();
            sb.AppendLine("One frame of the whole HUD drawn in software: " + FrameMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms (budget " + FrameBudgetMs + " ms).");
            sb.AppendLine();
            sb.AppendLine(Failures.Count == 0 ? "Within every budget." : "Over budget:\n- " + string.Join("\n- ", Failures));
            return sb.ToString();
        }

        private void Row(StringBuilder sb, string name, Func<Phase, string> value, string budget) =>
            sb.AppendLine("| " + name + " | " + string.Join(" | ", Phases.Select(value)) + " | " + budget + " |");
    }

    public static Result Run(TimeSpan duration)
    {
        Result result = new();
        AvaloniaRenderThread.Run(() => RunOnUiThread(duration, result));
        return result;
    }

    private static void RunOnUiThread(TimeSpan duration, Result result)
    {
        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "hud-soak-" + Guid.NewGuid().ToString("N"));
        string pictures = WritePictureSet(root);
        FakeStream capture = new();
        MainWindowViewModel vm = new(new TesterBatch250Tests.GripOnBaseEngine(), capture, loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location,
            HudAircraftRoot = pictures
        };
        AvaloniaRenderThread.Wait(vm.LoadHudDevicesCommand.ExecuteAsync(null));
        HudWidget aircraft = vm.HudWidgets.Single(w => w.IsAircraft);
        aircraft.AircraftChoice = "DCS/A-10C_2";
        AvaloniaRenderThread.Wait(vm.RefreshHudAircraftAsync());
        foreach (HudWidget w in vm.HudWidgets)
            w.Visible = w.StreamVisible = true;

        // Baseline: the fake devices report, nothing listens.
        using (CancellationTokenSource baseline = new())
        {
            _ = capture.MonitorAsync(_ => { }, _ => { }, baseline.Token);
            RunFor(TimeSpan.FromSeconds(10));
            result.Phases.Add(Measure(Baseline, TimeSpan.FromTicks(duration.Ticks / 4), vm, capture, () => 0, () => 0));
            baseline.Cancel();
            capture.Stop();
        }

        vm.HudStreamPort = HudStreamerTests.FreePort();
        vm.HudStreamBrowser = true;
        vm.HudOnScreen = false;
        AvaloniaRenderThread.Wait(vm.ToggleOverlayCommand.ExecuteAsync(null));
        HudStreamServer server = vm.HudStreamServerForTests ?? throw new InvalidOperationException("the stream page did not start: " + vm.HudStreamStatus);

        // One page connected, reading every event.
        long pageBytes = 0, pageEvents = 0;
        using CancellationTokenSource stop = new();
        Thread reader = new(() =>
        {
            try
            {
                using TcpClient client = new();
                client.Connect(IPAddress.Loopback, server.Port);
                NetworkStream stream = client.GetStream();
                byte[] request = Encoding.ASCII.GetBytes("GET /hud/events HTTP/1.1\r\nHost: 127.0.0.1:" + server.Port + "\r\n\r\n");
                stream.Write(request);
                byte[] buffer = new byte[65536];
                while (!stop.IsCancellationRequested)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;
                    Interlocked.Add(ref pageBytes, read);
                    for (int i = 0; i < read - 6; i++)
                    {
                        if (buffer[i] == (byte)'e' && buffer[i + 1] == (byte)'v' && buffer[i + 2] == (byte)'e' && buffer[i + 3] == (byte)'n' && buffer[i + 4] == (byte)'t' && buffer[i + 5] == (byte)':')
                            Interlocked.Increment(ref pageEvents);
                    }
                }
            }
            catch (Exception)
            {
            }
        }) { IsBackground = true, Name = "HUD soak page" };
        reader.Start();

        TimeSpan half = TimeSpan.FromTicks(duration.Ticks / 2);
        RunFor(TimeSpan.FromSeconds(20));
        result.Phases.Add(Measure(Stream, half, vm, capture, () => Interlocked.Read(ref pageEvents), () => Interlocked.Read(ref pageBytes)));

        // The player's own HUD as well: laid out and drawn by the render loop of a root that is never shown.
        HudSurface surface = new() { Widgets = vm.HudWidgets, Context = vm.HudContext };
        using (HiddenWindow view = HiddenWindow.Create(new Window { Content = new Border { Child = surface } }, new Size(1920, 1080)))
        {
            RunFor(TimeSpan.FromSeconds(30));
            result.Phases.Add(Measure(Drawn, half, vm, capture, () => Interlocked.Read(ref pageEvents), () => Interlocked.Read(ref pageBytes)));

            // One frame of the whole HUD drawn in software (the real HUD draws only the changed parts, on the GPU).
            Stopwatch frame = Stopwatch.StartNew();
            const int frames = 20;
            using (RenderTargetBitmap target = new(new PixelSize(1920, 1080)))
            {
                for (int i = 0; i < frames; i++)
                    target.Render(surface);
            }
            result.FrameMs = frame.Elapsed.TotalMilliseconds / frames;
        }

        stop.Cancel();
        capture.Stop();
        vm.ToggleOverlayCommand.Execute(null);
        AvaloniaRenderThread.Pump();
        reader.Join(3000);

        foreach (Phase p in result.Phases.Where(x => x.Budget != Baseline))
        {
            string at = p.Budget.Name + ": ";
            if (p.CpuPercent > p.Budget.CpuPercent)
                result.Failures.Add(at + "CPU " + p.CpuPercent.ToString("0.00", CultureInfo.InvariantCulture) + " % of one core");
            if (p.AllocKbPerSecond > p.Budget.AllocKbPerSecond)
                result.Failures.Add(at + "allocations " + p.AllocKbPerSecond.ToString("0", CultureInfo.InvariantCulture) + " KB/s");
            if (p.HeapGrowthMb > HeapGrowthBudgetMb)
                result.Failures.Add(at + "managed heap grew " + p.HeapGrowthMb.ToString("0.0", CultureInfo.InvariantCulture) + " MB (a leak?)");
            if (p.WorkingSetGrowthMb > WorkingSetGrowthBudgetMb)
                result.Failures.Add(at + "working set grew " + p.WorkingSetGrowthMb.ToString("0", CultureInfo.InvariantCulture) + " MB");
            if (p.UpdatesPerSecond > MaxUpdatesPerSecond)
                result.Failures.Add(at + p.UpdatesPerSecond.ToString("0.0", CultureInfo.InvariantCulture) + " HUD updates a second (cap 30)");
            if (p.EventsPerSecond > MaxUpdatesPerSecond || p.EventsPerSecond < 5)
                result.Failures.Add(at + p.EventsPerSecond.ToString("0.0", CultureInfo.InvariantCulture) + " page events a second (live, at most 30)");
        }
        if (result.FrameMs > FrameBudgetMs)
            result.Failures.Add("one software frame took " + result.FrameMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private static Phase Measure(Budget budget, TimeSpan time, MainWindowViewModel vm, FakeStream capture, Func<long> events, Func<long> bytes)
    {
        Process self = Process.GetCurrentProcess();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long heap0 = GC.GetTotalMemory(true);
        self.Refresh();
        long ws0 = self.WorkingSet64;
        TimeSpan cpu0 = self.TotalProcessorTime;
        long alloc0 = GC.GetTotalAllocatedBytes(precise: true);
        long flush0 = vm.HudPump.Flushes, events0 = events(), bytes0 = bytes(), offered0 = capture.Offered;
        Stopwatch clock = Stopwatch.StartNew();
        RunFor(time);
        double seconds = clock.Elapsed.TotalSeconds;
        self.Refresh();
        TimeSpan cpu1 = self.TotalProcessorTime;
        long alloc1 = GC.GetTotalAllocatedBytes(precise: true);
        long flush1 = vm.HudPump.Flushes, events1 = events(), bytes1 = bytes(), offered1 = capture.Offered;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long heap1 = GC.GetTotalMemory(true);
        self.Refresh();
        return new Phase
        {
            Budget = budget,
            Measured = TimeSpan.FromSeconds(seconds),
            ReportsPerSecond = (offered1 - offered0) / seconds,
            CpuPercent = (cpu1 - cpu0).TotalSeconds / seconds * 100,
            AllocKbPerSecond = (alloc1 - alloc0) / 1024.0 / seconds,
            HeapGrowthMb = (heap1 - heap0) / 1024.0 / 1024.0,
            WorkingSetGrowthMb = (self.WorkingSet64 - ws0) / 1024.0 / 1024.0,
            UpdatesPerSecond = (flush1 - flush0) / seconds,
            EventsPerSecond = (events1 - events0) / seconds,
            PageKbPerSecond = (bytes1 - bytes0) / 1024.0 / seconds
        };
    }

    // Runs the UI thread's dispatcher (the HUD's updates, the hidden root's render loop) for a while.
    private static void RunFor(TimeSpan time)
    {
        using CancellationTokenSource done = new(time);
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.MainLoop(done.Token);
        }
        catch (PlatformNotSupportedException)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed < time)
            {
                AvaloniaRenderThread.Pump();
                Thread.Sleep(5);
            }
        }
    }

    // A small A-10C II grip picture set (as the loader test builds it).
    private static string WritePictureSet(string root)
    {
        string pictures = Path.Combine(root, "CockpitPictures");
        string set = Path.Combine(pictures, "DCS", "A-10C_2");
        Directory.CreateDirectory(set);
        foreach ((string name, int w, int h) in new[] { ("device_stick", 400, 600), ("device_throttle", 500, 400) })
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
                File.WriteAllBytes(Path.Combine(set, name + ".jpg"), data.ToArray());
            using (SKData data = SKImage.FromBitmap(grey).Encode(SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(Path.Combine(set, name + ".mask.png"), data.ToArray());
        }
        File.WriteAllText(Path.Combine(set, "pictures.json"),
            "{\"format\":\"joypro-cockpit-pictures\",\"version\":1,\"game\":\"DCS\",\"plane\":\"A-10C_2\",\"views\":[" +
            "{\"id\":\"device:stick\",\"kind\":\"device\",\"device\":\"stick\",\"image\":\"device_stick.jpg\",\"mask\":\"device_stick.mask.png\",\"callouts\":[{\"n\":1,\"id\":\"STICK-BTN-TMS\",\"label\":\"TMS (Target Management Switch)\",\"x\":0.5,\"y\":0.3}]}," +
            "{\"id\":\"device:throttle\",\"kind\":\"device\",\"device\":\"throttle\",\"image\":\"device_throttle.jpg\",\"mask\":\"device_throttle.mask.png\",\"callouts\":[]}]}");
        File.WriteAllText(Path.Combine(pictures, "DCS", "index.json"), "{\"format\":\"joypro-cockpit-pictures-index\",\"aircraft\":{\"A-10C II\":\"A-10C_2\",\"A-10C_2\":\"A-10C_2\"}}");
        return pictures;
    }

    // The input monitor of a player's PC: three devices, every axis moving, reported every 15 ms like the DirectInput
    // poll (a new array per report, as the real one makes), and a button down / up every half second.
    // Also the device stream of the live check of the page in OBS (HudObsLiveTests), with the devices given there.
    internal sealed class FakeStream(params string[] devices) : IJoystickCaptureService
    {
        private readonly string[] Devices = devices.Length > 0 ? devices : [TesterBatch250Tests.Grip, TesterBatch250Tests.Base, TesterBatch250Tests.Throttle];
        private CancellationTokenSource? running;
        private long offered;

        public long Offered => Interlocked.Read(ref offered);

        public string LastDiagnostics => string.Empty;

        public Task<JoystickCaptureService.CaptureResult?> CaptureNextAsync(JoystickCaptureService.CaptureOptions options, CancellationToken cancellationToken) =>
            Task.FromResult<JoystickCaptureService.CaptureResult?>(null);

        public Task MonitorAsync(Action<JoystickInputEvent> onEvent, Action<JoystickAxisSnapshot>? onAxes, CancellationToken cancellationToken)
        {
            running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken token = running.Token;
            return Task.Run(async () =>
            {
                Stopwatch clock = Stopwatch.StartNew();
                int tick = 0;
                bool down = false;
                while (!token.IsCancellationRequested)
                {
                    double t = clock.Elapsed.TotalSeconds;
                    for (int d = 0; d < Devices.Length; d++)
                    {
                        double[] axes = new double[8];
                        for (int a = 0; a < 8; a++)
                            axes[a] = 0.5 + 0.45 * Math.Sin(t * (0.7 + a * 0.13) + d);
                        onAxes?.Invoke(new JoystickAxisSnapshot(Devices[d], axes));
                        Interlocked.Increment(ref offered);
                    }
                    if (++tick % 33 == 0)
                    {
                        down = !down;
                        onEvent(new JoystickInputEvent(Devices[0], "JOY_BTN1", down));
                    }
                    try
                    {
                        await Task.Delay(15, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }, CancellationToken.None);
        }

        public void Stop() => running?.Cancel();
    }
}
