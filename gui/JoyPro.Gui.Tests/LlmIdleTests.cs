using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The local AI model's idle stop: 30 minutes by default, it does not count down while JoyPro does AI work (an engine
// command that uses the model, its streaming answer, a plan being made or applied) and starts again from the end of the
// last work. The minutes persist in ui-settings.json; a file of 2.2.17 or older with the old default (2, written with
// every save) gets the new default, a value the user chose stays. The persistence tests write the test run's
// ui-settings.json, so they run in the TrayMode collection (alone).
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class LlmIdleTests
{
    private static (Process Server, LlmServerMonitor Monitor, string Folder) Start(DateTime lastUsedUtc)
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "llmidle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Process server = Process.Start(new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        DateTime started = server.StartTime.ToUniversalTime();
        string state = Path.Combine(folder, "server.json");
        File.WriteAllText(state, "{\"Pid\":" + server.Id.ToString(CultureInfo.InvariantCulture) + ",\"Port\":1,\"Key\":\"k\",\"Model\":\"Test model\",\"OwnerPid\":1," +
            "\"StartedUtc\":\"" + started.ToString("o", CultureInfo.InvariantCulture) + "\",\"LastUsedUtc\":\"" + lastUsedUtc.ToString("o", CultureInfo.InvariantCulture) + "\"," +
            "\"BusyPid\":0,\"IdleMinutes\":30}");
        return (server, new LlmServerMonitor(state) { VramQuery = _ => null }, folder);
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
    public void Defaults_AreThirtyMinutes()
    {
        Assert.Equal(30, new LlmServerMonitor(Path.Combine(Path.GetTempPath(), "none.json")).IdleMinutes);
        MainWindowViewModel vm = new(new AxisTuningFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
        Assert.Equal(30m, vm.LlmIdleMinutes);
        Assert.Equal(30m, MainWindowViewModel.DefaultLlmIdleMinutes);
    }

    [Fact]
    public void Poll_DoesNotCountDownWhileTheAppWorks_AndStartsAgainWhenTheWorkEnds()
    {
        DateTime now = DateTime.UtcNow;
        (Process server, LlmServerMonitor monitor, string folder) = Start(now.AddMinutes(-29));
        try
        {
            monitor.UtcNow = () => now;
            Assert.InRange(monitor.Poll().IdleSecondsLeft, 55, 65);

            // A plan is made: two hours of AI work (requests and the pauses between them) never stop the model.
            IDisposable work = monitor.BeginWork();
            Assert.True(monitor.IsWorking);
            monitor.UtcNow = () => now.AddHours(2);
            LlmServerSnapshot working = monitor.Poll();
            Assert.True(working.Running);
            Assert.True(working.Busy);
            Assert.Equal(30 * 60, working.IdleSecondsLeft);
            Assert.False(server.HasExited);

            // Nested work (an answer streaming in while the plan runs): idle only when the last one ends.
            IDisposable inner = monitor.BeginWork();
            inner.Dispose();
            Assert.True(monitor.IsWorking);
            Assert.True(monitor.Poll().Busy);

            // The work ends: the full 30 minutes start from that moment.
            work.Dispose();
            work.Dispose();
            Assert.False(monitor.IsWorking);
            monitor.UtcNow = () => now.AddHours(2).AddMinutes(10);
            LlmServerSnapshot idle = monitor.Poll();
            Assert.True(idle.Running);
            Assert.False(idle.Busy);
            Assert.InRange(idle.IdleSecondsLeft, 20 * 60 - 5, 20 * 60 + 5);

            monitor.UtcNow = () => now.AddHours(2).AddMinutes(30).AddSeconds(1);
            Assert.False(monitor.Poll().Running);
            Assert.True(server.WaitForExit(5000));
        }
        finally
        {
            Cleanup(server, folder);
        }
    }

    [Theory]
    [InlineData(true, "relations", "plan", "ask", "make groups", "--llm", "auto")]
    [InlineData(true, "relations", "plan", "apply", "--file", "plan.json")]
    [InlineData(true, "relations", "assistant", "hello", "--llm", "auto")]
    [InlineData(true, "relations", "from-text", "all flaps", "--dry-run", "--llm", "auto")]
    [InlineData(true, "relations", "from-text", "all flaps", "--llm", "on")]
    [InlineData(true, "llm", "start")]
    [InlineData(false, "relations", "from-text", "all flaps", "--llm", "off")]
    [InlineData(false, "llm", "status")]
    [InlineData(false, "llm", "download", "model", "--accept-license")]
    [InlineData(false, "binds", "list")]
    public void AiWorkCommands_AreTheOnesThatUseTheModel(bool expected, params string[] command)
    {
        Assert.Equal(expected, MainWindowViewModel.IsAiWorkCommand(command));
    }

    // The view model holds the idle stop for the whole engine command that uses the model, not for others.
    [Fact]
    public async Task EngineCommandsThatUseTheModel_HoldTheIdleStop()
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "llmidle-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            LlmServerMonitor monitor = new(Path.Combine(folder, "server.json"));
            WorkProbeEngine engine = new(monitor);
            MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = typeof(MainWindowViewModel).Assembly.Location,
                ProfilePath = Path.Combine(folder, "p.pr0file"),
                LlmEnabled = true
            };
            vm.UseLlmServerMonitor(monitor);
            vm.AiPlanText = "Put the flaps together";
            await vm.AskAiPlanCommand.ExecuteAsync(null);
            Assert.Contains(engine.Seen, s => s.StartsWith("relations plan ask", StringComparison.Ordinal) && s.EndsWith("working=True", StringComparison.Ordinal));
            Assert.Contains(engine.Seen, s => s.StartsWith("llm status", StringComparison.Ordinal) && s.EndsWith("working=False", StringComparison.Ordinal));
            Assert.False(monitor.IsWorking);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void IdleMinutes_PersistAcrossRestarts_AndTheOldDefaultBecomesThirty()
    {
        string settingsFile = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
            File.Delete(settingsFile);
            MainWindowViewModel first = new(new AxisTuningFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
            Assert.Equal(30m, first.LlmIdleMinutes);
            first.LlmIdleMinutes = 45;
            MainWindowViewModel second = new(new AxisTuningFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
            Assert.Equal(45m, second.LlmIdleMinutes);

            // Chosen on purpose after the new default: even 2 minutes stay 2.
            second.LlmIdleMinutes = 2;
            MainWindowViewModel third = new(new AxisTuningFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
            Assert.Equal(2m, third.LlmIdleMinutes);

            // A file of 2.2.17 or older: the old default 2 (written with every save) becomes 30, another value stays.
            WriteOldSettings(settingsFile, 2);
            Assert.Equal(30m, new MainWindowViewModel(new AxisTuningFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true).LlmIdleMinutes);
            WriteOldSettings(settingsFile, 5);
            Assert.Equal(5m, new MainWindowViewModel(new AxisTuningFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true).LlmIdleMinutes);
        }
        finally
        {
            if (File.Exists(settingsFile))
                File.Delete(settingsFile);
        }
    }

    // The settings file as 2.2.17 wrote it: llmIdleMinutes without llmIdleDefault.
    private static void WriteOldSettings(string settingsFile, double minutes)
    {
        JsonObject root = File.Exists(settingsFile) ? (JsonNode.Parse(File.ReadAllText(settingsFile)) as JsonObject ?? new JsonObject()) : new JsonObject();
        root.Remove("llmIdleDefault");
        root["llmIdleMinutes"] = minutes;
        File.WriteAllText(settingsFile, root.ToJsonString());
    }

    // Answers like the engine and notes for every command whether the monitor counted AI work while it ran.
    private sealed class WorkProbeEngine(LlmServerMonitor monitor) : IEngineCliClient
    {
        public List<string> Seen { get; } = [];

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            RunStreamingAsync(engineExePath, arguments, new EngineRunOptions(), cancellationToken);

        public Task<EngineCommandResult> RunStreamingAsync(string engineExePath, IReadOnlyList<string> arguments, EngineRunOptions options, CancellationToken cancellationToken = default)
        {
            List<string> command = [];
            for (int i = 0; i < arguments.Count; i++)
            {
                string a = arguments[i];
                if (a is "--profile" or "--status" or "--benchmark" or "--load-corrections" or "--auto-add-db-items" or "--remap-missing-devices" or
                    "--confirm-device-remap" or "--correction-mode" or "--confirm-corrections" or "--history-note")
                {
                    i++;
                    continue;
                }
                if (a == "--text")
                    continue;
                command.Add(a);
            }
            string line = string.Join(" ", command);
            lock (Seen)
                Seen.Add(line + " working=" + monitor.IsWorking);
            string output = line.StartsWith("llm status", StringComparison.Ordinal)
                ? "{\"Enabled\":true,\"Ready\":true,\"Model\":{\"Id\":\"test\",\"Label\":\"Test model\"}}"
                : line.StartsWith("relations plan ask", StringComparison.Ordinal)
                    ? "{\"Steps\":[],\"Ops\":[],\"Reply\":\"Nothing to do.\"}"
                    : "{}";
            return Task.FromResult(new EngineCommandResult { ExitCode = 0, StandardOutput = output, StandardError = string.Empty });
        }
    }
}
