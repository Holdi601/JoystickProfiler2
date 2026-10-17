using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Real DirectInput with vJoy device 1 fed by this test (only when device 1 is free; it never configures vJoy):
//   set JOYPRO_RECORD_VJOY=1 & dotnet test gui/JoyPro.Gui.Tests --filter LiveInputVJoyTests
// - Record takes the new press, not a button resting pressed (button 3 held like a 3-way switch's middle);
// - releasing the resting button counts only when enabled;
// - the live monitor reports the press and the axis; follow input on the Binds page jumps to the relation.
[SupportedOSPlatform("windows")]
public sealed class LiveInputVJoyTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("JOYPRO_RECORD_VJOY") == "1";

    [Fact]
    public async Task Record_TakesTheNewPress_NotTheRestingButton()
    {
        if (!Enabled)
            return;
        using VJoyDevice vjoy = VJoyDevice.Acquire();
        vjoy.Button(3, true);                    // resting pressed
        await Task.Delay(300);
        JoystickCaptureService capture = new();
        Task<JoystickCaptureService.CaptureResult?> record = capture.CaptureNextAsync(new JoystickCaptureService.CaptureOptions
        {
            PreferredDevice = "vJoy",
            TimeoutMs = 5000,
            WarmupMs = 300
        }, CancellationToken.None);
        await Task.Delay(900);
        vjoy.Button(5, true);
        await Task.Delay(150);
        vjoy.Button(5, false);
        // vJoy sends a report only when fed: the press is the device's first report since recording opened it, and it
        // shows the resting button 3 too (the old capture took JOY_BTN3). The press counts once released.
        JoystickCaptureService.CaptureResult? result = await record;
        Assert.NotNull(result);
        Assert.Contains("vJoy", result!.Device, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("JOY_BTN5", result.Input);
    }

    [Fact]
    public async Task Record_DeviceReportsDuringTheWarmup_RestingButtonIgnoredPressCountsAtOnce()
    {
        if (!Enabled)
            return;
        using VJoyDevice vjoy = VJoyDevice.Acquire();
        vjoy.Button(3, true);
        await Task.Delay(300);
        JoystickCaptureService capture = new();
        Task<JoystickCaptureService.CaptureResult?> record = capture.CaptureNextAsync(new JoystickCaptureService.CaptureOptions
        {
            PreferredDevice = "vJoy",
            TimeoutMs = 5000,
            WarmupMs = 400
        }, CancellationToken.None);
        await Task.Delay(150);
        // Reports all the time, like most real devices (the stick's sensor noise): button 3 is known to rest pressed.
        await vjoy.ReportFor(2000);
        vjoy.Button(6, true);                    // counts on the press, no release needed
        JoystickCaptureService.CaptureResult? result = await record;
        vjoy.Button(6, false);
        Assert.Equal("JOY_BTN6", result?.Input);
    }

    [Fact]
    public async Task Record_ReleasingTheRestingButton_CountsOnlyWhenEnabled()
    {
        if (!Enabled)
            return;
        using VJoyDevice vjoy = VJoyDevice.Acquire();
        JoystickCaptureService capture = new();
        foreach (bool allow in new[] { false, true })
        {
            vjoy.Button(3, true);
            await Task.Delay(300);
            Task<JoystickCaptureService.CaptureResult?> record = capture.CaptureNextAsync(new JoystickCaptureService.CaptureOptions
            {
                PreferredDevice = "vJoy",
                TimeoutMs = 5000,
                WarmupMs = 300,
                RecordReleaseOfHeldButtons = allow
            }, CancellationToken.None);
            await Task.Delay(150);
            await vjoy.ReportFor(2000);   // reports all the time, like a real device
            vjoy.Button(3, false);
            JoystickCaptureService.CaptureResult? result = await record;
            if (allow)
                Assert.Equal("JOY_BTN3", result?.Input);
            else
                Assert.True(result == null || !result.Device.Contains("vJoy", StringComparison.OrdinalIgnoreCase), "took " + result);
        }
    }

    [Fact]
    public void Follow_RealMonitor_JumpsToTheRelationOfTheVJoyButtonAndAxis()
    {
        if (!Enabled)
            return;
        using VJoyDevice vjoy = VJoyDevice.Acquire();
        JoystickCaptureService capture = new();
        string device = DiscoverVJoyName(capture, vjoy);
        Assert.Contains("vJoy", device, StringComparison.OrdinalIgnoreCase);

        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = new(new VJoyBindsEngine(device), capture, loadPersistedUiSettings: false)
            {
                EnginePath = typeof(MainWindowViewModel).Assembly.Location,
                ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "vjoy-follow.pr0file")
            };
            AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
            AvaloniaRenderThread.Wait(vm.RefreshBindsCommand.ExecuteAsync(null));
            vm.CurrentStage = UiStage.Binds;
            try
            {
                Assert.True(vm.IsLiveInputMonitoring);
                vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Canopy");
                PumpFor(600);                                   // the monitor opens the devices
                // vJoy has not reported since the monitor opened it: the press is its first report, which could be a
                // resting button; it counts when released soon.
                vjoy.Button(8, true);
                PumpFor(400);
                Assert.Equal("Canopy", vm.SelectedBind?.RelationName);
                vjoy.Button(8, false);
                PumpUntil(() => vm.SelectedBind?.RelationName == "Gear", 2000);
                Assert.Equal("Gear", vm.SelectedBind?.RelationName);
                Assert.Contains("BTN 8 on vJoy Device: Gear (1/2)", vm.FollowInputToast);
                PumpFor(300);
                vjoy.Button(8, true);
                PumpUntil(() => vm.SelectedBind?.RelationName == "Hook", 2000);
                Assert.Equal("Hook", vm.SelectedBind?.RelationName);
                vjoy.Button(8, false);

                // The X axis a long way: Thrust.
                for (int value = 16384; value <= 32767; value += 2048)
                {
                    vjoy.Axis(VJoyDevice.AxisX, value);
                    PumpFor(25);
                }
                PumpUntil(() => vm.SelectedBind?.RelationName == "Thrust", 2000);
                Assert.Equal("Thrust", vm.SelectedBind?.RelationName);

                // Visual Assign open on the vJoy device: the held button lights up, the list stays.
                vm.VisualAssignDevice = device;
                vm.OpenVisualAssignMode = true;
                PumpFor(600);
                vjoy.Button(8, true);
                PumpUntil(() => vm.WorkspaceLiveInput.IsPressed("JOY_BTN8"), 2000);
                Assert.True(vm.WorkspaceLiveInput.IsPressed("JOY_BTN8"));
                Assert.Equal("Thrust", vm.SelectedBind?.RelationName);
                vjoy.Button(8, false);
                PumpUntil(() => !vm.WorkspaceLiveInput.IsPressed("JOY_BTN8"), 2000);
                Assert.False(vm.WorkspaceLiveInput.IsPressed("JOY_BTN8"));
            }
            finally
            {
                vm.CurrentStage = UiStage.Start;
                vm.OpenVisualAssignMode = false;
                Assert.False(vm.IsLiveInputMonitoring);
                vm.StopGameConfigWatcher();
            }
        });
    }

    // The DirectInput name of vJoy device 1 ("vJoy Device {GUID}") as the monitor reports it.
    private static string DiscoverVJoyName(JoystickCaptureService capture, VJoyDevice vjoy)
    {
        ConcurrentQueue<JoystickInputEvent> events = new();
        using CancellationTokenSource cts = new();
        Task monitor = capture.MonitorAsync(events.Enqueue, null, cts.Token);
        Thread.Sleep(600);
        vjoy.Button(12, true);
        Thread.Sleep(200);
        vjoy.Button(12, false);
        Thread.Sleep(200);
        cts.Cancel();
        monitor.Wait(3000);
        return events.FirstOrDefault(e => e.Input == "JOY_BTN12" && e.Device.Contains("vJoy", StringComparison.OrdinalIgnoreCase))?.Device ?? string.Empty;
    }

    private static void PumpFor(int ms)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            AvaloniaRenderThread.Pump();
            Thread.Sleep(5);
        }
    }

    private static void PumpUntil(Func<bool> condition, int ms)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end && !condition())
        {
            AvaloniaRenderThread.Pump();
            Thread.Sleep(5);
        }
    }

    // Binds on the vJoy device: BTN 8 does Gear and Hook, X does Thrust; Canopy is on BTN 1.
    private sealed class VJoyBindsEngine(string device) : IEngineCliClient
    {
        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            List<string> args = arguments.ToList();
            string output = string.Empty;
            if (args.Contains("relations") && args.Contains("list"))
            {
                output = "Relations:" + Environment.NewLine + string.Join(Environment.NewLine,
                    new[] { "Canopy", "Gear", "Hook", "Thrust" }.Select(r => r + " | type=" + (r == "Thrust" ? "axis" : "button") + " | bind=bound | mods=0 | games=DCS | planes=1 | groups=Test"));
            }
            else if (args.Contains("binds") && args.Contains("list"))
            {
                output = string.Join(Environment.NewLine,
                    "Binds:",
                    "Canopy | type=button | device=" + device + " | input=JOY_BTN1 | modifiers(0)=<none>",
                    "Gear | type=button | device=" + device + " | input=JOY_BTN8 | modifiers(0)=<none>",
                    "Hook | type=button | device=" + device + " | input=JOY_BTN8 | modifiers(0)=<none>",
                    "Thrust | type=axis | device=" + device + " | input=JOY_X | modifiers(0)=<none>");
            }
            return Task.FromResult(new EngineCommandResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(1), StandardOutput = output, StandardError = string.Empty, Arguments = arguments.ToArray() });
        }
    }

    // vJoy device 1, fed only (acquire, set buttons and axes, centre and release at the end).
    private sealed class VJoyDevice : IDisposable
    {
        private const string Dll = @"C:\Program Files\vJoy\x64\vJoyInterface.dll";
        public const uint AxisX = 0x30;

        public static VJoyDevice Acquire()
        {
            Assert.True(vJoyEnabled(), "vJoy is not enabled");
            Assert.Equal(1, GetVJDStatus(1));   // free: nobody else feeds device 1
            Assert.True(AcquireVJD(1), "vJoy device 1 could not be acquired");
            ResetVJD(1);
            SetAxis(16384, 1, AxisX);
            SetAxis(16384, 1, 0x31);
            return new VJoyDevice();
        }

        public void Button(byte number, bool down) => Assert.True(SetBtn(down, 1, number));

        public void Axis(uint axis, int value) => Assert.True(SetAxis(value, 1, axis));

        // Sensor noise on Y for a while: the device sends report after report.
        public async Task ReportFor(int ms)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            for (int i = 0; DateTime.UtcNow < end; i++)
            {
                SetAxis(16384 + (i % 2 == 0 ? 20 : -20), 1, 0x31);
                await Task.Delay(30);
            }
        }

        public void Dispose()
        {
            ResetVJD(1);
            SetAxis(16384, 1, AxisX);
            SetAxis(16384, 1, 0x31);
            RelinquishVJD(1);
        }

        [DllImport(Dll)]
        private static extern bool vJoyEnabled();

        [DllImport(Dll)]
        private static extern int GetVJDStatus(uint id);

        [DllImport(Dll)]
        private static extern bool AcquireVJD(uint id);

        [DllImport(Dll)]
        private static extern void RelinquishVJD(uint id);

        [DllImport(Dll)]
        private static extern bool ResetVJD(uint id);

        [DllImport(Dll)]
        private static extern bool SetAxis(int value, uint id, uint axis);

        [DllImport(Dll)]
        private static extern bool SetBtn(bool value, uint id, byte button);
    }
}
