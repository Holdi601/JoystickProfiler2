using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The layout window (LayoutWindowViewModel, docs/setup-wizard.md#layout-window):
// - the games and aircraft of the source, the remembered game / aircraft / device, and every change kept;
// - the highlight path: a pressed button (fake input) lights its row up and names its function; an axis moved far enough
//   does the same; another device's input or "Light up" off does nothing;
// - the live monitor runs only while the window is shown and "Light up" is on, with the idle delay;
// - the window's place, size and choices come back after a restart (ui-settings.json "layoutWindow").
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class LayoutWindowTests : IDisposable
{
    private const string Stick = "Joystick - HOTAS Warthog {e7a1e7a1-0000-0000-0000-000000000001}";
    private readonly string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "layout-window-" + Guid.NewGuid().ToString("N"));
    private readonly string uiSettings = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");

    public LayoutWindowTests()
    {
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(AppInfo.SettingsFolder);
        File.Delete(uiSettings);
    }

    public void Dispose()
    {
        File.Delete(uiSettings);
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
        }
    }

    // A sheet with the trigger, the weapon release and the pitch axis.
    private string WriteSheet()
    {
        CalloutSheet sheet = new() { Width = 900, Height = 600, PictureX = 300, PictureY = 50, PictureWidth = 300, PictureHeight = 500 };
        CalloutTable buttons = new() { Id = "stick.trigger", Title = "Trigger", X = 20, Y = 40, Width = 260, Side = "left" };
        buttons.Rows.Add(new CalloutRow { Input = "JOY_BTN1", Label = "Trigger" });
        buttons.Rows.Add(new CalloutRow { Input = "JOY_BTN2", Label = "Weapon release" });
        CalloutTable axes = new() { Id = "stick.axis", Title = "Stick", X = 620, Y = 40, Width = 260, Side = "right" };
        axes.Rows.Add(new CalloutRow { Input = "JOY_Y", Label = "Pitch", Icon = "axis" });
        sheet.Tables.Add(buttons);
        sheet.Tables.Add(axes);
        string file = Path.Combine(folder, "01-stick.sheet.json");
        File.WriteAllText(file, sheet.ToJson());
        return file;
    }

    private LayoutFakeEngine Engine() => new(WriteSheet());

    private sealed class LayoutFakeEngine(string sheetFile)
    {
        public List<List<string>> Calls { get; } = [];

        public Task<EngineCommandResult?> RunAsync(IReadOnlyList<string> args, bool includeProfile, bool mutatesProfile, string label, bool quiet)
        {
            Calls.Add(args.ToList());
            string output = args.Contains("--live-data")
                ? "{\"Devices\":[{\"Device\":\"" + Stick.Replace("\\", "\\\\") + "\",\"Name\":\"Thrustmaster HOTAS Warthog Joystick\",\"Role\":\"main-stick\",\"Picture\":\"\",\"Sheet\":\"" +
                  sheetFile.Replace("\\", "\\\\") + "\",\"Texts\":{\"JOY_BTN1\":[\"Gun\",\"Laser\"],\"JOY_BTN2\":[\"Weapon release\",\"\"],\"JOY_Y\":[\"Pitch\",\"\"]}}]}"
                : "{\"Games\":[{\"Game\":\"DCS\",\"Aircraft\":[\"F-16C_50\",\"FA-18C_hornet\"]},{\"Game\":\"StarCitizen\",\"Aircraft\":[\"StarCitizen\"]}]}";
            return Task.FromResult<EngineCommandResult?>(new EngineCommandResult { ExitCode = 0, StandardOutput = output, Arguments = args.ToArray() });
        }
    }

    private static void Settle(LayoutWindowViewModel vm)
    {
        for (int i = 0; i < 50 && vm.IsWorking; i++)
            Thread.Sleep(20);
    }

    [Fact]
    public async Task LayoutWindow_ReadsTheSource_AndKeepsWhatWasChosen()
    {
        LayoutWindowSettings settings = new() { Game = "DCS", Aircraft = "FA-18C_hornet" };
        int saved = 0;
        LayoutFakeEngine engine = Engine();
        LayoutWindowViewModel vm = new(engine.RunAsync, null, id => id == "DCS" ? "DCS World" : id, settings, () => saved++, false, post: a => a(), workFolder: folder);
        await vm.LoadAsync();
        Settle(vm);

        Assert.Equal(["DCS", "StarCitizen"], vm.Games.Select(g => g.Id));
        Assert.Equal("DCS World", vm.SelectedGame!.Name);
        Assert.Equal("FA-18C_hornet", vm.SelectedAircraft);
        Assert.Contains(engine.Calls, c => c.Contains("--from-profile") && !c.Contains("--live-data"));
        List<string> render = engine.Calls.Last(c => c.Contains("--live-data"));
        Assert.Equal("FA-18C_hornet", render[render.IndexOf("--aircraft") + 1]);
        Assert.Single(vm.Devices);
        Assert.Single(vm.ShownDevices);
        Assert.Equal(2, vm.DeviceChoices.Count);

        vm.SelectedAircraft = "F-16C_50";
        Settle(vm);
        Assert.Equal("F-16C_50", settings.Aircraft);
        vm.SelectedDevice = vm.DeviceChoices[1];
        Assert.Equal(DeviceMarkerText.DeviceName(Stick), settings.Device);
        vm.LightUp = false;
        vm.FollowGame = true;
        Assert.False(settings.LightUp);
        Assert.True(settings.FollowGame);
        Assert.True(saved >= 4);

        // Follow the game: DCS reports the F/A-18; an unknown game leaves the choice alone.
        vm.OnRunningGame("DCS", "FA-18C_hornet");
        Settle(vm);
        Assert.Equal("FA-18C_hornet", vm.SelectedAircraft);
        vm.OnRunningGame("IL2Game", "IL2Game");
        Assert.Equal("DCS", vm.SelectedGame!.Id);
        vm.FollowGame = false;
        vm.OnRunningGame("StarCitizen", "StarCitizen");
        Assert.Equal("DCS", vm.SelectedGame!.Id);
    }

    [Fact]
    public async Task PressedButton_LightsItsRow_AndNamesItsFunction()
    {
        LayoutWindowViewModel vm = new(Engine().RunAsync, null, id => id, new LayoutWindowSettings { Game = "DCS", Aircraft = "F-16C_50" }, () => { }, false, post: a => a(), workFolder: folder);
        await vm.LoadAsync();
        Settle(vm);
        LayoutWindowDevice device = vm.Devices.Single();
        Assert.Equal("Gun", device.Callouts.RowText(device.Callouts.Sheet!.Tables[0], device.Callouts.Sheet.Tables[0].Rows[0], 0));

        // Another instance of the same model (other GUID) is another device: nothing lights up.
        vm.OnInput(new JoystickInputEvent("Joystick - HOTAS Warthog {e7a1e7a1-0000-0000-0000-000000000099}", "JOY_BTN1", true));
        Assert.False(device.Live.IsActive("JOY_BTN1"));

        vm.OnInput(new JoystickInputEvent(Stick, "JOY_BTN1", true));
        Assert.True(device.Live.IsActive("JOY_BTN1"));
        Assert.Equal("Gun", vm.LastFunction);
        Assert.Contains("Thrustmaster HOTAS Warthog Joystick", vm.LastControl);
        vm.OnInput(new JoystickInputEvent(Stick, "JOY_BTN1", false));
        Assert.False(device.Live.IsActive("JOY_BTN1"));
        Assert.Equal("Gun", vm.LastFunction);

        // A button that rests pressed when the device opens is no press of the user: lit, not named.
        vm.OnInput(new JoystickInputEvent(Stick, "JOY_BTN2", true, FirstReport: true));
        Assert.Equal("Gun", vm.LastFunction);
        vm.OnInput(new JoystickInputEvent(Stick, "JOY_BTN2", false));

        // An axis: the first report anchors it, a move of more than the noise lights it up and names it.
        double[] rest = [0.5, 0.5, 0, 0, 0, 0, 0, 0];
        double[] pulled = [0.5, 0.8, 0, 0, 0, 0, 0, 0];
        vm.OnAxes(new JoystickAxisSnapshot(Stick, rest, FirstReport: true));
        vm.OnAxes(new JoystickAxisSnapshot(Stick, pulled));
        Assert.True(device.Live.IsActive("JOY_Y"));
        Assert.Equal("Pitch", vm.LastFunction);

        // Light up off: input is ignored and everything goes dark.
        vm.LightUp = false;
        vm.OnInput(new JoystickInputEvent(Stick, "JOY_BTN2", true));
        Assert.False(device.Live.IsActive("JOY_BTN2"));
        Assert.False(device.Live.AnyActive);
    }

    [Fact]
    public void LiveMonitor_RunsOnlyWhileShownAndLit_WithTheIdleDelay()
    {
        RecordingCapture capture = new();
        LayoutWindowViewModel vm = new(Engine().RunAsync, null, id => id, new LayoutWindowSettings(), () => { }, false, capture, a => a(), folder);
        Assert.False(vm.IsLive);
        vm.SetWindowShown(true);
        Assert.True(vm.IsLive);
        Assert.True(capture.Started.Wait(5000));
        Assert.Equal(LayoutWindowViewModel.LiveIdleDelay, capture.IdleDelay);
        vm.SetWindowShown(false);
        Assert.False(vm.IsLive);
        Assert.True(capture.Stopped.Wait(5000));
        vm.SetWindowShown(true);
        Assert.True(vm.IsLive);
        vm.LightUp = false;
        Assert.False(vm.IsLive);
        vm.StopLive();
    }

    [Fact]
    public void LayoutWindowSettings_ComeBackAfterARestart()
    {
        MainWindowViewModel first = new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };
        first.OpenLayoutWindowCommand.Execute(null);
        LayoutWindowViewModel window = first.LayoutWindow!;
        window.LightUp = false;
        window.Settings.Game = "DCS";
        window.Settings.Aircraft = "F-16C_50";
        window.Settings.Device = "HOTAS Warthog Joystick";
        window.Settings.X = 2200;
        window.Settings.Y = 120;
        window.Settings.Width = 1500;
        window.Settings.Height = 1000;
        window.Settings.Screen = "1920,0,2560,1440";
        first.LayoutWindowOpenChanged(true);
        // (Set on the settings: the option itself would start listening for DCS on UDP 1992.)
        window.Settings.FollowGame = true;
        first.SaveLayoutWindowSettings();
        window.StopLive();

        MainWindowViewModel second = new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };
        LayoutWindowSettings back = second.LayoutWindowSettings;
        Assert.False(back.LightUp);
        Assert.True(back.FollowGame);
        Assert.Equal("DCS", back.Game);
        Assert.Equal("F-16C_50", back.Aircraft);
        Assert.Equal("HOTAS Warthog Joystick", back.Device);
        Assert.Equal(2200, back.X);
        Assert.Equal(120, back.Y);
        Assert.Equal(1500, back.Width);
        Assert.Equal(1000, back.Height);
        Assert.Equal("1920,0,2560,1440", back.Screen);
        Assert.True(back.WasOpen);
        Assert.True(second.ReopenLayoutWindow);

        // Closed by the user: it does not open again.
        second.OpenLayoutWindowCommand.Execute(null);
        second.LayoutWindowOpenChanged(false);
        second.LayoutWindow!.StopLive();
        MainWindowViewModel third = new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };
        Assert.False(third.ReopenLayoutWindow);
    }

    // The tray's "Where is what?" and a second start hand the layout window over like a review key.
    [Fact]
    public void StartupOption_LayoutWindow()
    {
        StartupOptions options = StartupOptions.Parse(["--layout-window", "--review", "DCS|x"]);
        Assert.True(options.OpenLayoutWindow);
        Assert.Contains("DCS|x", options.ReviewKeys);
        Assert.False(StartupOptions.Parse(["--review", "DCS|x"]).OpenLayoutWindow);
    }

    // Idle CPU of the live monitor with the real devices of this PC (DirectInput, read-only). Opt-in:
    //   set JOYPRO_LAYOUT_CPU=1 & dotnet test gui/JoyPro.Gui.Tests --filter LiveMonitor_IdleCpu
    // Prints the CPU time of 20 s without input, polling every 15 ms vs. with the layout window's 250 ms idle delay.
    [Fact]
    public async Task LiveMonitor_IdleCpu()
    {
        if (Environment.GetEnvironmentVariable("JOYPRO_LAYOUT_CPU") != "1")
            return;
        JoystickCaptureService capture = new();
        async Task<double> Measure(TimeSpan? idle, Func<string, bool>? include = null)
        {
            using CancellationTokenSource cts = new();
            Process self = Process.GetCurrentProcess();
            Task run = idle.HasValue ? capture.MonitorAsync(_ => { }, _ => { }, idle.Value, include, cts.Token) : Task.CompletedTask;
            await Task.Delay(3000);
            self.Refresh();
            TimeSpan before = self.TotalProcessorTime;
            await Task.Delay(20000);
            self.Refresh();
            double ms = (self.TotalProcessorTime - before).TotalMilliseconds;
            cts.Cancel();
            await run;
            return ms;
        }
        // The test process itself (test host, timers) without any monitor: subtracted from both.
        double baseline = await Measure(null);
        double fast = Math.Max(0, await Measure(TimeSpan.Zero) - baseline);
        double idle = Math.Max(0, await Measure(LayoutWindowViewModel.LiveIdleDelay) - baseline);
        string all = capture.LastDiagnostics;
        // The window opens only the devices it shows: here every one but an analog keyboard (it reports about 1000 times
        // a second, which DirectInput handles whether or not anyone reads it).
        double shown = Math.Max(0, await Measure(LayoutWindowViewModel.LiveIdleDelay, name => !name.Contains("Wooting", StringComparison.OrdinalIgnoreCase)) - baseline);
        string report = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "Live monitor, 20 s without input, {0}: every 15 ms {1:0} ms CPU ({2:0.00} % of one core); layout window (250 ms when idle) {3:0} ms CPU ({4:0.00} %); " +
            "only the shown devices (no analog keyboard: {5}) {6:0} ms CPU ({7:0.00} %); the test process alone {8:0} ms.",
            all, fast, fast / 200.0, idle, idle / 200.0, capture.LastDiagnostics, shown, shown / 200.0, baseline);
        string? output = Environment.GetEnvironmentVariable("JOYPRO_LAYOUT_CPU_OUT");
        if (!string.IsNullOrWhiteSpace(output))
            File.WriteAllText(output, report);
        // Measured 2026-11-14 on the author's PC (12 devices, one an analog keyboard reporting at 1 kHz): 2 to 3 % of one
        // core in every mode, little difference between 15 ms and 250 ms polling: DirectInput's own handling of the
        // device reports dominates, not the reads. The bound catches a polling loop that runs away.
        Assert.True(shown / 200.0 < 5.0, report);
    }

    private sealed class RecordingCapture : IJoystickCaptureService
    {
        public string LastDiagnostics => string.Empty;
        public TimeSpan IdleDelay { get; private set; }
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Stopped { get; } = new();

        public Task<JoystickCaptureService.CaptureResult?> CaptureNextAsync(JoystickCaptureService.CaptureOptions options, CancellationToken cancellationToken) =>
            Task.FromResult<JoystickCaptureService.CaptureResult?>(null);

        public Task MonitorAsync(Action<JoystickInputEvent> onEvent, Action<JoystickAxisSnapshot>? onAxes, CancellationToken cancellationToken) =>
            MonitorAsync(onEvent, onAxes, TimeSpan.Zero, cancellationToken);

        public async Task MonitorAsync(Action<JoystickInputEvent> onEvent, Action<JoystickAxisSnapshot>? onAxes, TimeSpan idleDelay, CancellationToken cancellationToken)
        {
            IdleDelay = idleDelay;
            Stopped.Reset();
            Started.Set();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            Stopped.Set();
        }
    }
}
