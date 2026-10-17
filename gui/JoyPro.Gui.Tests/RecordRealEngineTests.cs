using System;
using System.Diagnostics;
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

// Bind Studio's Record end to end: this view model and the real engine on a profile copy, an unbound axis relation
// "Pitch" selected, Record pressed; the engine's own "binds list" must show the recorded input afterwards. Needs a built
// engine, so it runs only when JOYPRO_RECORD_ENGINE names engine.exe. JOYPRO_RECORD_PROFILE names a profile to copy
// (default: a new one with Pitch); JOYPRO_RECORD_WORK the work folder (default: temp).
// JOYPRO_RECORD_VJOY=1 records for real: the real DirectInput capture while this test moves vJoy device 1's X axis
// (only when device 1 is free; it never configures vJoy). Without it a fake capture hands over the input.
//   set JOYPRO_RECORD_ENGINE=<repo>\engine\bin\x64\Debug\engine.exe & set JOYPRO_RECORD_VJOY=1 & dotnet test gui/JoyPro.Gui.Tests --filter RecordRealEngineTests
[SupportedOSPlatform("windows")]
public sealed class RecordRealEngineTests
{
    private const string FakeDevice = "vJoy Device {89D63500-B53F-11f1-8002-444553540000}";

    [Fact]
    public async Task Record_OnUnboundAxisRelation_SavesTheBindInTheProfile()
    {
        string? engine = Environment.GetEnvironmentVariable("JOYPRO_RECORD_ENGINE");
        if (string.IsNullOrWhiteSpace(engine) || !File.Exists(engine))
            return;
        bool realVJoy = Environment.GetEnvironmentVariable("JOYPRO_RECORD_VJOY") == "1";
        string workRoot = Environment.GetEnvironmentVariable("JOYPRO_RECORD_WORK") is { Length: > 0 } work ? work : Path.GetTempPath();
        string folder = Path.Combine(workRoot, "JoyPro2.Gui.Tests", "record-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string previousData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR") ?? string.Empty;
        bool acquired = false;
        try
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(folder, "data"));
            string profile = Path.Combine(folder, "record.pr0file");
            string? source = Environment.GetEnvironmentVariable("JOYPRO_RECORD_PROFILE");
            if (!string.IsNullOrWhiteSpace(source) && File.Exists(source))
                File.Copy(source, profile);
            else
                Assert.Equal(0, (await RunEngineAsync(engine, "--profile \"" + profile + "\" relations add Pitch --axis --node DCS:a2001cdnil:FA-18C_hornet")).ExitCode);

            IJoystickCaptureService capture;
            if (realVJoy)
            {
                Assert.True(VJoy.vJoyEnabled(), "vJoy is not enabled");
                Assert.Equal(1, VJoy.GetVJDStatus(1));  // free: nobody else feeds device 1
                acquired = VJoy.AcquireVJD(1);
                Assert.True(acquired, "vJoy device 1 could not be acquired");
                VJoy.SetAxis(16384, 1, VJoy.AxisX);
                capture = new JoystickCaptureService();
            }
            else
            {
                capture = new FixedCapture(FakeDevice, "JOY_X");
            }

            MainWindowViewModel vm = new(new EngineCliClient(), capture, loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            await vm.RefreshBindsCommand.ExecuteAsync(null);
            BindListItem pitch = Assert.Single(vm.Binds, b => b.RelationName == "Pitch");
            Assert.False(pitch.HasBind);
            vm.SelectedBind = pitch;
            await Task.Delay(300);
            vm.RecordTimeoutMs = "5000";

            Task record = vm.RecordNextInputCommand.ExecuteAsync(null);
            if (realVJoy)
            {
                // After the warmup: X from the centre to full deflection within about half a second, as a hand would.
                await Task.Delay(900);
                for (int value = 16384; value <= 32767; value += 1024)
                {
                    VJoy.SetAxis(value, 1, VJoy.AxisX);
                    await Task.Delay(30);
                }
            }
            await record;

            Assert.Contains("Bound 'Pitch'", vm.RecordStatusText);
            Assert.True(vm.Binds.Single(b => b.RelationName == "Pitch").HasBind);

            (int exitCode, string output) = await RunEngineAsync(engine, "--profile \"" + profile + "\" --text binds list");
            Assert.Equal(0, exitCode);
            string line = output.Split('\n').Select(v => v.Trim()).Single(v => v.StartsWith("Pitch |", StringComparison.Ordinal));
            Assert.Contains("type=axis", line);
            Assert.Contains("device=vJoy Device {", line);
            Assert.Contains("input=JOY_X", line);
        }
        finally
        {
            if (acquired)
            {
                VJoy.SetAxis(16384, 1, VJoy.AxisX);
                VJoy.RelinquishVJD(1);
            }
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previousData.Length == 0 ? null : previousData);
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<(int ExitCode, string Output)> RunEngineAsync(string engine, string arguments)
    {
        using Process process = Process.Start(new ProcessStartInfo(engine, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = await process.StandardOutput.ReadToEndAsync();
        await error;
        await process.WaitForExitAsync();
        return (process.ExitCode, output);
    }

    private sealed class FixedCapture(string device, string input) : IJoystickCaptureService
    {
        public string LastDiagnostics => "fixed";

        public Task<JoystickCaptureService.CaptureResult?> CaptureNextAsync(JoystickCaptureService.CaptureOptions options, CancellationToken cancellationToken)
        {
            Assert.True(options.AxisMode);
            return Task.FromResult<JoystickCaptureService.CaptureResult?>(new JoystickCaptureService.CaptureResult { Device = device, Input = input });
        }

        public Task MonitorAsync(Action<JoystickInputEvent> onEvent, Action<JoystickAxisSnapshot>? onAxes, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // Feeding only: acquire device 1, move an axis, release it. Nothing here changes the vJoy configuration.
    private static class VJoy
    {
        private const string Dll = @"C:\Program Files\vJoy\x64\vJoyInterface.dll";
        public const uint AxisX = 0x30;

        [DllImport(Dll)]
        public static extern bool vJoyEnabled();

        [DllImport(Dll)]
        public static extern int GetVJDStatus(uint id);

        [DllImport(Dll)]
        public static extern bool AcquireVJD(uint id);

        [DllImport(Dll)]
        public static extern void RelinquishVJD(uint id);

        [DllImport(Dll)]
        public static extern bool SetAxis(int value, uint id, uint axis);
    }
}
