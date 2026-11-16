using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Start card's look at the games (engine setup games, MainWindowViewModel.AutoSetupStart.cs) runs in the background
// and is never noticed: it waits for the Start page and an idle app, runs outside the queue of the app's engine calls in
// a low-priority process of its own (no busy state; a foreground call does not wait for it), stops when the Start page is
// left, and its answer is kept until the games, the devices or the profile change. The card still names the games
// without binds. (Quality gate I7 "games list": 13-14 s while the look sat in the engine queue, 2026-11-14.)
[SupportedOSPlatform("windows")]
public sealed class AutoSetupStatusTests
{
    [Fact]
    public async Task Look_WaitsForTheIdleStartPage_RunsBesideTheEngineQueue_AndFillsTheCard()
    {
        StatusFakeEngine engine = new() { SetupDelayMs = 1500 };
        MainWindowViewModel vm = NewVm(engine);
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        // Not right after the game list: the look waits for the app to be idle.
        Assert.Equal(0, engine.SetupCalls);
        Assert.True(vm.AutoSetupPending);

        Assert.True(await WaitAsync(() => engine.SetupRunning, 5000), "the look never started");
        Assert.False(vm.IsBusy);
        // A foreground engine call does not queue behind the look.
        Stopwatch watch = Stopwatch.StartNew();
        EngineCommandResult? list = await vm.RunEngineForTestsAsync(["relations", "list"]);
        watch.Stop();
        Assert.NotNull(list);
        Assert.True(engine.SetupRunning, "the look ended before the foreground call: the test proves nothing");
        Assert.True(watch.ElapsedMilliseconds < 800, "the foreground call waited " + watch.ElapsedMilliseconds + " ms for the look");

        Assert.True(await WaitAsync(() => vm.ShowAutoSetupCard, 5000), "the card never filled");
        List<string> args = engine.LastSetupArgs!;
        Assert.Contains("--status-only", args);
        Assert.Equal(["DCS", "IL2Game"], args[args.IndexOf("--games") + 1].Split(',').OrderBy(g => g));
        Assert.True(engine.LastSetupOptions!.LowPriority);
        Assert.True(engine.LastSetupOptions.KillOnCancel);
        // The games without binds lead the card; the one already in the profile is a secondary row.
        Assert.Equal(["IL2Game"], vm.AutoSetupNewGames.Select(g => g.GameId));
        Assert.Equal(["DCS"], vm.AutoSetupProfileGames.Select(g => g.GameId));
        Assert.Contains("IL-2", vm.AutoSetupHeadline);
        Assert.Equal(1, vm.AutoSetupRuns);
    }

    [Fact]
    public async Task Look_IsKept_StopsWhenTheStartPageIsLeft_AndRunsAgainOnChanges()
    {
        StatusFakeEngine engine = new();
        MainWindowViewModel vm = NewVm(engine);
        // The devices as the Relations page reads them (a first device list is a change of its own).
        await vm.RefreshDevicesCommand.ExecuteAsync(null);
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        Assert.True(await WaitAsync(() => vm.ShowAutoSetupCard, 5000), "the card never filled");
        Assert.Equal(1, engine.SetupCalls);

        // Away and back with nothing changed: the kept answer, no new engine run.
        string keyBefore = vm.AutoSetupKeyForTests;
        vm.CurrentStage = UiStage.Relations;
        vm.CurrentStage = UiStage.Start;
        await Task.Delay(600);
        Assert.False(vm.AutoSetupPending);
        Assert.True(engine.SetupCalls == 1, keyBefore + " -> " + vm.AutoSetupKeyForTests);

        // Another device: the look runs again.
        engine.Devices = ["Stick {1}", "Throttle {2}"];
        await vm.RefreshDevicesCommand.ExecuteAsync(null);
        Assert.True(await WaitAsync(() => engine.SetupCalls == 2 && !vm.AutoSetupPending, 5000), "a device change did not ask again");

        // A look that runs while the user leaves the Start page stops (its process ends) and runs on the next visit.
        engine.SetupDelayMs = 3000;
        engine.Statuses["IL2Game"] = "profile";
        vm.RefreshDashboardCommand.Execute(null);
        Assert.True(await WaitAsync(() => engine.SetupRunning, 5000), "the forced look never started");
        vm.CurrentStage = UiStage.Relations;
        Assert.True(await WaitAsync(() => engine.SetupCancelled == 1 && !vm.AutoSetupPending, 3000), "leaving the Start page did not stop the look");
        Assert.Equal(["IL2Game"], vm.AutoSetupNewGames.Select(g => g.GameId));
        engine.SetupDelayMs = 0;
        vm.CurrentStage = UiStage.Start;
        Assert.True(await WaitAsync(() => vm.AutoSetupNewGames.Count == 0 && !vm.AutoSetupPending, 5000), "the look did not run again on the next visit");
        Assert.Equal(["DCS", "IL2Game"], vm.AutoSetupProfileGames.Select(g => g.GameId).OrderBy(g => g));
        Assert.True(vm.AutoSetupOnlySecondary);
    }

    [Fact]
    public async Task Look_DoesNotRunAwayFromTheStartPage()
    {
        StatusFakeEngine engine = new();
        MainWindowViewModel vm = NewVm(engine);
        vm.CurrentStage = UiStage.Relations;
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        await Task.Delay(600);
        Assert.Equal(0, engine.SetupCalls);
        Assert.False(vm.AutoSetupPending);
        vm.CurrentStage = UiStage.Start;
        Assert.True(await WaitAsync(() => vm.ShowAutoSetupCard, 5000), "the Start page did not ask");
        Assert.Equal(1, engine.SetupCalls);
    }

    private static MainWindowViewModel NewVm(StatusFakeEngine engine)
    {
        MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location,
            AutoSetupIdleMs = 200,
            AutoSetupPollMs = 50
        };
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "autosetup-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string profile = Path.Combine(folder, "My buttons.pr0file");
        File.WriteAllText(profile, "{}");
        vm.ProfilePath = profile;
        return vm;
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }

    // IL-2 (nothing assigned) and DCS (in the profile) installed; setup games takes SetupDelayMs and honours the token.
    private sealed class StatusFakeEngine : IEngineCliClient
    {
        private readonly NoviceGuiTests.NoviceFakeEngine basics = new();

        public int SetupDelayMs { get; set; }

        public Dictionary<string, string> Statuses { get; } = new(StringComparer.OrdinalIgnoreCase) { ["IL2Game"] = "empty", ["DCS"] = "profile" };

        public List<string> Devices { get; set; } = ["Stick {1}"];

        public int SetupCalls;
        public int SetupCancelled;
        public volatile bool SetupRunning;
        public List<string>? LastSetupArgs;
        public EngineRunOptions? LastSetupOptions;

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            RunStreamingAsync(engineExePath, arguments, new EngineRunOptions(), cancellationToken);

        public async Task<EngineCommandResult> RunStreamingAsync(string engineExePath, IReadOnlyList<string> arguments, EngineRunOptions options, CancellationToken cancellationToken = default)
        {
            List<string> args = arguments.ToList();
            string? output = null;
            if (AxisTuningTests.Seq(args, "games", "installs"))
                output = "{\"Games\":[{\"GameId\":\"IL2Game\",\"Name\":\"IL-2 Sturmovik: Great Battles\",\"Installed\":true,\"InstallPath\":\"D:\\\\IL-2\",\"Source\":\"test\",\"ConfigPaths\":[]}," +
                         "{\"GameId\":\"DCS\",\"Name\":\"DCS World\",\"Installed\":true,\"InstallPath\":\"D:\\\\DCS\",\"Source\":\"test\",\"ConfigPaths\":[]}]}";
            else if (AxisTuningTests.Seq(args, "games", "integrations"))
                output = "{\"Integrations\":[]}";
            else if (AxisTuningTests.Seq(args, "devices", "list"))
                output = "Connected:" + Environment.NewLine + string.Concat(Devices.Select(d => "  - " + d + Environment.NewLine));
            else if (AxisTuningTests.Seq(args, "setup", "games"))
            {
                Interlocked.Increment(ref SetupCalls);
                LastSetupArgs = args;
                LastSetupOptions = options;
                SetupRunning = true;
                try
                {
                    if (SetupDelayMs > 0)
                        await Task.Delay(SetupDelayMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref SetupCancelled);
                    throw;
                }
                finally
                {
                    SetupRunning = false;
                }
                string games = args[args.IndexOf("--games") + 1];
                output = "{\"Games\":[" + string.Join(",", games.Split(',').Select(g =>
                    "{\"Game\":\"" + g + "\",\"Wizard\":true,\"Status\":\"" + (Statuses.TryGetValue(g, out string? s) ? s : "empty") + "\",\"GameBinds\":0,\"ProfileBinds\":0}")) + "]}";
            }
            if (output == null)
                return await basics.RunAsync(engineExePath, arguments, cancellationToken);
            return new EngineCommandResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(1), StandardOutput = output, StandardError = string.Empty, Arguments = arguments.ToArray() };
        }
    }
}
