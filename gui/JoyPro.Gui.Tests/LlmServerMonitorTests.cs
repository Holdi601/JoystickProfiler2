using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The warm local AI model (LlmServerMonitor): a stand-in process plays llama-server, the state file is the engine's
// server.json. Idle stop, busy requests, Stop now and a reused process id.
[SupportedOSPlatform("windows")]
public sealed class LlmServerMonitorTests
{
    private static (Process Server, LlmServerMonitor Monitor, string Folder) Start(DateTime lastUsedUtc, int busyPid = 0, bool wrongStart = false)
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "llmserver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Process server = Process.Start(new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        DateTime started = server.StartTime.ToUniversalTime();
        if (wrongStart)
            started = started.AddHours(-1);
        string state = Path.Combine(folder, "server.json");
        File.WriteAllText(state, "{\"Pid\":" + server.Id.ToString(CultureInfo.InvariantCulture) + ",\"Port\":1,\"Key\":\"k\",\"Model\":\"Test model\",\"OwnerPid\":1," +
            "\"StartedUtc\":\"" + started.ToString("o", CultureInfo.InvariantCulture) + "\",\"LastUsedUtc\":\"" + lastUsedUtc.ToString("o", CultureInfo.InvariantCulture) + "\"," +
            "\"BusyPid\":" + busyPid.ToString(CultureInfo.InvariantCulture) + ",\"IdleMinutes\":2}");
        LlmServerMonitor monitor = new(state) { IdleMinutes = 2, VramQuery = _ => 3L * 1024 * 1024 * 1024 };
        return (server, monitor, folder);
    }

    private static void Cleanup(Process server, string folder)
    {
        try
        {
            if (!server.HasExited)
                server.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        server.Dispose();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    [Fact]
    public void Poll_ReportsTheRunningModelAndStopsItAfterTheIdleMinutes()
    {
        DateTime now = DateTime.UtcNow;
        (Process server, LlmServerMonitor monitor, string folder) = Start(now);
        try
        {
            monitor.UtcNow = () => now.AddSeconds(30);
            LlmServerSnapshot running = monitor.Poll();
            Assert.True(running.Running);
            Assert.Equal("Test model", running.Model);
            Assert.True(running.MemoryIsVram);
            Assert.Equal(3L * 1024 * 1024 * 1024, running.MemoryBytes);
            Assert.InRange(running.IdleSecondsLeft, 85, 95);
            Assert.False(server.HasExited);

            monitor.UtcNow = () => now.AddMinutes(2).AddSeconds(1);
            Assert.False(monitor.Poll().Running);
            Assert.True(server.WaitForExit(5000));
            Assert.False(File.Exists(monitor.StateFile));
        }
        finally
        {
            Cleanup(server, folder);
        }
    }

    [Fact]
    public void Poll_NeverStopsTheModelWhileARequestRuns()
    {
        DateTime now = DateTime.UtcNow;
        (Process server, LlmServerMonitor monitor, string folder) = Start(now.AddMinutes(-30), busyPid: Environment.ProcessId);
        try
        {
            LlmServerSnapshot busy = monitor.Poll();
            Assert.True(busy.Running);
            Assert.True(busy.Busy);
            Assert.False(server.HasExited);
        }
        finally
        {
            Cleanup(server, folder);
        }
    }

    [Fact]
    public void Poll_ShowsTheWorkingSetWithoutVideoMemoryNumbers()
    {
        (Process server, LlmServerMonitor monitor, string folder) = Start(DateTime.UtcNow);
        try
        {
            monitor.VramQuery = _ => null;
            LlmServerSnapshot running = monitor.Poll();
            Assert.True(running.Running);
            Assert.False(running.MemoryIsVram);
            Assert.True(running.MemoryBytes > 0);
        }
        finally
        {
            Cleanup(server, folder);
        }
    }

    [Fact]
    public void Poll_LeavesAProcessWithAReusedIdAlone()
    {
        (Process server, LlmServerMonitor monitor, string folder) = Start(DateTime.UtcNow.AddHours(-1), wrongStart: true);
        try
        {
            Assert.False(monitor.Poll().Running);
            Assert.False(monitor.StopNow());
            Assert.False(server.HasExited);
            Assert.False(File.Exists(monitor.StateFile));
        }
        finally
        {
            Cleanup(server, folder);
        }
    }

    [Fact]
    public async Task StopNow_FromTheViewModel_EndsTheModelAtOnce()
    {
        (Process server, LlmServerMonitor monitor, string folder) = Start(DateTime.UtcNow);
        try
        {
            MainWindowViewModel vm = new(new AxisTuningFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
            vm.UseLlmServerMonitor(monitor);
            await vm.PollLlmServerAsync();
            Assert.True(vm.LlmServerRunning);
            Assert.Contains("Test model", vm.LlmServerStatusText);
            Assert.Contains("3.0 GB", vm.LlmServerChipText.Replace(',', '.'));

            await vm.StopLlmServerCommand.ExecuteAsync(null);
            Assert.True(server.WaitForExit(5000));
            Assert.False(vm.LlmServerRunning);
            Assert.False(File.Exists(monitor.StateFile));
            Assert.False(string.IsNullOrWhiteSpace(vm.StatusText));
        }
        finally
        {
            Cleanup(server, folder);
        }
    }

    [Fact]
    public void IdleMinutes_AreKeptInRange()
    {
        MainWindowViewModel vm = new(new AxisTuningFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
        Assert.Equal(2m, vm.LlmIdleMinutes);
        vm.LlmIdleMinutes = 0;
        Assert.Equal(0.5m, vm.LlmIdleMinutes);
        vm.LlmIdleMinutes = 500;
        Assert.Equal(120m, vm.LlmIdleMinutes);
    }
}
