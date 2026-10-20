using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Manage with AI / Edit with AI (docs/ai-plans.md) with the real engine and its offline rules (no model): the plan
// shows a checkbox per step, rejected steps stay unticked, Apply runs the ticked steps in one engine call and one Undo
// takes all of them back; Edit with AI never touches another relation.
[CollectionDefinition("ProcessEnvironment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
}

// Sets JOYPRO_DATA_DIR for the whole test process (the engine and the profile history read it): runs alone.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class AiPlanTests
{
    private static string? Engine()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "bin")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return engine != null && File.Exists(engine) ? engine : null;
    }

    private static async Task<(MainWindowViewModel Vm, string Folder, string Previous)> SetUp(string engine)
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "aiplan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string previous = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR") ?? string.Empty;
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(folder, "data"));
        string profile = Path.Combine(folder, "plan.pr0file");
        using (Process start = Process.Start(new ProcessStartInfo(engine, "--profile \"" + profile + "\" relations from-text \"speed brake in and out on all jets\"")
               { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            await start.StandardOutput.ReadToEndAsync();
            await start.WaitForExitAsync();
            Assert.Equal(0, start.ExitCode);
        }
        // Every engine the view model starts, also a late background one after the test, uses this test's data folder
        // (an engine copies the game DBs into its data folder, which other tests read).
        MainWindowViewModel vm = new(new DataDirEngineClient(Path.Combine(folder, "data")), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        vm.CurrentStage = UiStage.Relations;
        return (vm, folder, previous);
    }

    private static void TearDown(string folder, string previous)
    {
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previous.Length > 0 ? previous : null);
        try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ManageWithAi_OfflineRule_PreviewTickApplyAndOneUndo()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        (MainWindowViewModel vm, string folder, string previous) = await SetUp(engine);
        try
        {
            Assert.Equal(2, vm.Relations.Count);
            vm.ShowManageWithAiCommand.Execute(null);
            Assert.True(vm.IsAiPlanPanelOpen);
            Assert.True(vm.IsAnyRelationToolOpen);
            Assert.False(vm.IsAiPlanScoped);

            vm.AiPlanText = "append _sc to every relation that contains Speed Brake";
            await vm.AskAiPlanCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.AiPlanSteps.Count);
            Assert.All(vm.AiPlanSteps, s => Assert.True(s.IsValid && s.IsSelected));
            Assert.Contains("_sc", vm.AiPlanSteps[0].After);
            // Nothing changed by the preview.
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            Assert.DoesNotContain(vm.Relations, r => r.Name.EndsWith("_sc", StringComparison.Ordinal));

            await vm.ApplyAiPlanCommand.ExecuteAsync(null);
            Assert.Empty(vm.AiPlanSteps);
            Assert.Equal(2, vm.Relations.Count(r => r.Name.EndsWith("_sc", StringComparison.Ordinal)));
            Assert.Contains("Applied 2 steps", vm.AiPlanStatus);

            await vm.UndoLastChangeCommand.ExecuteAsync(null);
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            Assert.DoesNotContain(vm.Relations, r => r.Name.EndsWith("_sc", StringComparison.Ordinal));
            Assert.Equal(2, vm.Relations.Count);

            // Untick a step: only the ticked one runs.
            vm.AiPlanText = "delete all relations that have no bind";
            await vm.AskAiPlanCommand.ExecuteAsync(null);
            AiPlanStepItem delete = Assert.Single(vm.AiPlanSteps);
            delete.IsSelected = false;
            await vm.ApplyAiPlanCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.Relations.Count);
            vm.DiscardAiPlanCommand.Execute(null);
            Assert.Empty(vm.AiPlanSteps);
        }
        finally
        {
            TearDown(folder, previous);
        }
    }

    [Fact]
    public async Task ManageWithAi_Fill_PreviewPerRelationAndApplyTicked()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        (MainWindowViewModel vm, string folder, string previous) = await SetUp(engine);
        try
        {
            vm.ShowManageWithAiCommand.Execute(null);
            vm.AiPlanText = "fill the F-15E Strike Eagle and the F4U-1D Corsair into my relations where they fit";
            await vm.AskAiPlanCommand.ExecuteAsync(null);
            AiPlanStepItem fill = Assert.Single(vm.AiPlanSteps);
            Assert.True(fill.IsPreview);
            Assert.True(vm.HasAiFill);
            Assert.Contains("relations get", vm.AiFillSummary);
            AiFillRelationItem first = vm.AiFillRelations.First();
            Assert.NotEmpty(first.Actions);
            Assert.All(first.Actions, a => Assert.False(string.IsNullOrEmpty(a.Spec)));

            // The aircraft filter hides the other aircraft; unticking a relation unticks its visible actions.
            string plane = vm.AiFillAircraftChoices.Skip(1).First();
            vm.AiFillAircraftFilter = plane;
            Assert.All(vm.AiFillRelations.SelectMany(r => r.Actions), a => Assert.Equal(string.Equals(a.Where, plane, StringComparison.OrdinalIgnoreCase), a.IsVisible));
            vm.AiFillAircraftFilter = vm.AiFillAircraftChoices[0];
            foreach (AiFillRelationItem other in vm.AiFillRelations.Skip(1))
                other.IsSelected = false;
            int ticked = first.Actions.Count(a => a.IsSelected);
            Assert.True(ticked > 0);

            await vm.ApplyAiPlanCommand.ExecuteAsync(null);
            Assert.Contains("Applied " + 1, vm.AiPlanStatus);
            Assert.False(vm.HasAiFill);
        }
        finally
        {
            TearDown(folder, previous);
        }
    }

    [Fact]
    public async Task EditWithAi_ChangesOnlyTheOpenRelation()    {
        string? engine = Engine();
        if (engine == null)
            return;
        (MainWindowViewModel vm, string folder, string previous) = await SetUp(engine);
        try
        {
            RelationListItem open = vm.Relations.First(r => r.Name.StartsWith("Speed Brake In", StringComparison.Ordinal));
            string other = vm.Relations.First(r => r != open).Name;
            vm.SelectedRelation = open;
            vm.ShowEditWithAiCommand.Execute(null);
            Assert.True(vm.IsAiPlanScoped);
            Assert.Equal(open.Name, vm.AiPlanScope);

            // Another relation is out of reach.
            vm.AiPlanText = "rename " + other + " to Nope";
            await vm.AskAiPlanCommand.ExecuteAsync(null);
            AiPlanStepItem rejected = Assert.Single(vm.AiPlanSteps);
            Assert.False(rejected.IsValid);
            Assert.False(rejected.IsSelected);

            vm.AiPlanText = "rename it to Brakes in";
            await vm.AskAiPlanCommand.ExecuteAsync(null);
            AiPlanStepItem rename = Assert.Single(vm.AiPlanSteps);
            Assert.True(rename.IsValid);
            Assert.Equal(open.Name, rename.Before);
            Assert.Equal("Brakes in", rename.After);
            await vm.ApplyAiPlanCommand.ExecuteAsync(null);
            Assert.Contains(vm.Relations, r => r.Name == "Brakes in");
            Assert.Contains(vm.Relations, r => r.Name == other);
            Assert.Equal("Brakes in", vm.AiPlanScope);
        }
        finally
        {
            TearDown(folder, previous);
        }
    }
}

// The real engine with a fixed JOYPRO_DATA_DIR per call, whatever the process environment is by then.
internal sealed class DataDirEngineClient : IEngineCliClient
{
    private readonly EngineCliClient inner = new();
    private readonly string dataDir;

    public DataDirEngineClient(string dataDir)
    {
        this.dataDir = dataDir;
    }

    public Task<EngineCommandResult> RunAsync(string engineExePath, System.Collections.Generic.IReadOnlyList<string> arguments, System.Threading.CancellationToken cancellationToken = default) =>
        RunStreamingAsync(engineExePath, arguments, new EngineRunOptions(), cancellationToken);

    public Task<EngineCommandResult> RunStreamingAsync(string engineExePath, System.Collections.Generic.IReadOnlyList<string> arguments, EngineRunOptions options, System.Threading.CancellationToken cancellationToken = default)
    {
        System.Collections.Generic.Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase);
        if (options?.Environment != null)
            foreach (System.Collections.Generic.KeyValuePair<string, string> pair in options.Environment)
                environment[pair.Key] = pair.Value;
        environment["JOYPRO_DATA_DIR"] = dataDir;
        return inner.RunStreamingAsync(engineExePath, arguments, new EngineRunOptions { StandardErrorLine = options?.StandardErrorLine, Environment = environment }, cancellationToken);
    }
}