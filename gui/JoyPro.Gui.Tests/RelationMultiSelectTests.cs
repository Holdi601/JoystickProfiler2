using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// Several actions at once (MainWindowViewModel.RelationMultiSelect.cs, docs/relation-multi-select.md): in the relation
// editor the selected actions are removed, copied and pasted into another relation, or moved to a new relation (named
// in the dialog, the source's kind) or an existing one of the same kind; in the Relations list the actions of several
// relations move together. Every operation is one engine call (one profile version, one Ctrl+Z).
// Renders (opt-in): JOYPRO_MULTISELECT_RENDER_OUT=<folder> the editor with three actions selected and the Move dialog.
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class RelationMultiSelectTests
{
    private const string Bombs = "DCS:d3001pnilu3001cd2vd1vpnilvunil";
    private const string WtBombs = "WarThunder:ID_BOMBS";

    [Fact]
    public async Task RemoveSeveralActions_IsOneEngineCall()
    {
        RecordingEngine fake = new();
        MainWindowViewModel vm = await OpenAsync(fake, "Weapon Release");
        List<RelationNodeItem> picked = vm.RelationNodes.Take(2).ToList();
        vm.SetRelationNodeSelection(picked);
        Assert.True(vm.HasRelationNodeMultiSelection);
        Assert.Equal("2 actions selected", vm.RelationNodeSelectionSummary);
        int asked = vm.AskedConfirmations.Count;
        await vm.DeleteSelectedRelationNodesCommand.ExecuteAsync(null);
        Assert.Equal(asked + 1, vm.AskedConfirmations.Count);
        IReadOnlyList<string> remove = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "item", "remove"));
        Assert.Equal(["relations", "item", "remove", "Weapon Release", "--node", Bombs, "--node", WtBombs], remove.SkipWhile(a => a != "relations").Take(8));
        Assert.Equal("Remove actions", ValueAfter(remove, "--history-note"));
        Assert.Contains("Ctrl+Z", vm.StatusText);
        vm.StopGameConfigWatcher();
    }

    [Fact]
    public async Task CopyAndPaste_IntoAnotherRelationOfTheSameKind()
    {
        RecordingEngine fake = new();
        MainWindowViewModel vm = await OpenAsync(fake, "Weapon Release");
        vm.SetRelationNodeSelection(vm.RelationNodes.Take(2).ToList());
        vm.CopySelectedRelationNodesCommand.Execute(null);
        Assert.True(vm.HasCopiedRelationNodes);
        Assert.False(vm.CanPasteRelationNodes); // the same relation
        Assert.Equal("Paste 2 action(s)", vm.PasteRelationNodesLabel);

        // An axis relation does not take button actions: nothing reaches the engine.
        await SelectRelationAsync(vm, "Pitch");
        Assert.True(vm.CanPasteRelationNodes);
        await vm.PasteRelationNodesCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "item", "copy-to"));
        Assert.Contains("button relation", vm.StatusText);

        await SelectRelationAsync(vm, "Gun Trigger");
        Assert.True(vm.CanPasteRelationNodes);
        await vm.PasteRelationNodesCommand.ExecuteAsync(null);
        IReadOnlyList<string> paste = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "item", "copy-to"));
        Assert.Equal(["relations", "item", "copy-to", "Weapon Release", "--node", Bombs, "--node", WtBombs, "--to", "Gun Trigger"], paste.SkipWhile(a => a != "relations").Take(10));
        Assert.Equal("Paste actions", ValueAfter(paste, "--history-note"));
        Assert.Equal("Pasted 2 action(s) into 'Gun Trigger'. Ctrl+Z undoes it.", vm.StatusText);

        // The right-clicked action alone when it is not part of the selection.
        vm.SetRelationNodeSelection([]);
        RelationNodeItem single = vm.RelationNodes.Last();
        vm.CopySelectedRelationNodesCommand.Execute(single);
        Assert.Equal("Paste 1 action(s)", vm.PasteRelationNodesLabel);
        vm.StopGameConfigWatcher();
    }

    [Fact]
    public async Task MoveTo_ANewRelation_OrAnExistingOneOfTheSameKind()
    {
        RecordingEngine fake = new();
        MainWindowViewModel vm = await OpenAsync(fake, "Weapon Release");
        vm.SetRelationNodeSelection(vm.RelationNodes.Take(2).ToList());
        vm.OpenMoveRelationNodesCommand.Execute(null);
        Assert.True(vm.IsMoveNodesOpen);
        Assert.Equal("Move 2 action(s) to...", vm.MoveNodesTitle);
        Assert.True(vm.IsMoveNodesToNew);
        Assert.Equal("Weapon Release (2)", vm.MoveNodesNewName);
        // Only button relations, not the source.
        Assert.Contains("Gun Trigger", vm.MoveNodesTargets);
        Assert.DoesNotContain("Weapon Release", vm.MoveNodesTargets);
        Assert.DoesNotContain("Pitch", vm.MoveNodesTargets);
        vm.MoveNodesSearch = "gun trig";
        Assert.Equal(["Gun Trigger"], vm.MoveNodesTargets);

        // A taken name is caught before the engine.
        vm.MoveNodesNewName = "gear up/down";
        await vm.ApplyMoveNodesCommand.ExecuteAsync(null);
        Assert.True(vm.HasMoveNodesStatus);
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "item", "move-to"));

        vm.MoveNodesNewName = "Pickle";
        await vm.ApplyMoveNodesCommand.ExecuteAsync(null);
        IReadOnlyList<string> toNew = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "item", "move-to"));
        Assert.Equal(["relations", "item", "move-to", "Weapon Release", "--node", Bombs, "--node", WtBombs, "--new", "Pickle"], toNew.SkipWhile(a => a != "relations").Take(10));
        Assert.Equal("Move actions", ValueAfter(toNew, "--history-note"));
        Assert.False(vm.IsMoveNodesOpen);

        await SelectRelationAsync(vm, "Weapon Release");
        vm.OpenMoveRelationNodesCommand.Execute(vm.RelationNodes.Last());
        Assert.Equal("Move 1 action(s) to...", vm.MoveNodesTitle);
        vm.IsMoveNodesToExisting = true;
        Assert.False(vm.CanApplyMoveNodes);
        vm.SelectedMoveNodesTarget = "Gun Trigger";
        Assert.True(vm.CanApplyMoveNodes);
        await vm.ApplyMoveNodesCommand.ExecuteAsync(null);
        IReadOnlyList<string> toExisting = fake.Calls.Last(c => AxisTuningTests.Seq(c, "relations", "item", "move-to"));
        Assert.Equal("Gun Trigger", ValueAfter(toExisting, "--to"));
        Assert.DoesNotContain("--new", toExisting);
        vm.StopGameConfigWatcher();
    }

    [Fact]
    public async Task RelationsList_MovesTheActionsOfSeveralRelations()
    {
        RecordingEngine fake = new();
        MainWindowViewModel vm = await UiDesignRenderTests.CreateLoadedVmAsync(fake.Design, fake);
        // Mixed kinds: refused with a word, no dialog.
        vm.SetRelationMassSelection(vm.Relations.Where(r => r.Name is "Pitch" or "Gun Trigger"));
        vm.OpenMoveRelationsActionsCommand.Execute(null);
        Assert.False(vm.IsMoveNodesOpen);
        Assert.Contains("one kind", vm.StatusText);

        vm.SetRelationMassSelection(vm.Relations.Where(r => r.Name is "TMS Up" or "TMS Down"));
        vm.OpenMoveRelationsActionsCommand.Execute(null);
        Assert.True(vm.IsMoveNodesOpen);
        Assert.Equal("Move the actions of 2 relation(s) to...", vm.MoveNodesTitle);
        Assert.DoesNotContain("TMS Up", vm.MoveNodesTargets);
        Assert.DoesNotContain("TMS Down", vm.MoveNodesTargets);
        vm.IsMoveNodesToExisting = true;
        vm.SelectedMoveNodesTarget = "Weapon Release";
        await vm.ApplyMoveNodesCommand.ExecuteAsync(null);
        IReadOnlyList<string> mass = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "mass", "move-actions"));
        Assert.Equal("TMS Down,TMS Up", string.Join(",", ValueAfter(mass, "--relations").Split(',').OrderBy(n => n, StringComparer.Ordinal)));
        Assert.Contains("--delete-empty", mass);
        Assert.Equal("Weapon Release", ValueAfter(mass, "--to"));
        Assert.Equal("Moved the actions of 2 relation(s) to 'Weapon Release'. Ctrl+Z undoes it.", vm.StatusText);
        vm.StopGameConfigWatcher();
    }

    // The real list: Ctrl+A (Select all) selects every action, the bar shows; a plain list keeps one selected.
    [Fact]
    public void TheListSelection_ReachesTheViewModel()
    {
        AvaloniaRenderThread.Run(() =>
        {
            RecordingEngine fake = new();
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(OpenAsync(fake, "Weapon Release"));
            try
            {
                vm.CurrentStage = UiStage.Relations;
                MainWindow main = new() { DataContext = vm, Width = 1600, Height = 1000 };
                main.Measure(new Size(1600, 1000));
                main.Arrange(new Rect(0, 0, 1600, 1000));
                AvaloniaRenderThread.Pump();
                ListBox list = main.FindControl<ListBox>("RelationNodeList")!;
                Assert.Equal(SelectionMode.Multiple, list.SelectionMode);
                vm.SelectAllRelationNodesCommand.Execute(null);
                AvaloniaRenderThread.Pump();
                Assert.Equal(vm.RelationNodes.Count, vm.RelationNodeSelectionCount);
                Assert.True(vm.HasRelationNodeMultiSelection);
                main.Measure(new Size(1600, 1000));
                main.Arrange(new Rect(0, 0, 1600, 1000));
                Assert.True(main.FindControl<Border>("RelationNodeMultiSelectBar")!.IsEffectivelyVisible);
                vm.ClearRelationNodeSelectionCommand.Execute(null);
                AvaloniaRenderThread.Pump();
                Assert.Equal(0, vm.RelationNodeSelectionCount);
            }
            finally
            {
                vm.StopGameConfigWatcher();
            }
        });
    }

    [Fact]
    public void RenderEditorWithSeveralActionsSelected()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_MULTISELECT_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            RecordingEngine fake = new();
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(OpenAsync(fake, "Weapon Release"));
            try
            {
                vm.StatusText = "Ready.";
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach (int width in new[] { 1280, 1920 })
                    {
                        vm.CurrentStage = UiStage.Relations;
                        vm.IsMoveNodesOpen = false;
                        AvaloniaRenderThread.Wait(SelectRelationAsync(vm, "Weapon Release"));
                        vm.SetRelationNodeSelection(vm.RelationNodes.ToList());
                        vm.CopySelectedRelationNodesCommand.Execute(null);
                        AvaloniaRenderThread.Wait(SelectRelationAsync(vm, "Gear Up/Down"));
                        AvaloniaRenderThread.Pump();
                        // The editor's list: Ctrl+A (all three actions selected), the bar and Paste.
                        Action<MainWindow> selectNodes = main => main.FindControl<ListBox>("RelationNodeList")!.SelectAll();
                        UiDesignRenderTests.RenderWindow(vm, width, 1000, Path.Combine(outDir, "warm-up.png"), selectNodes, detach: true);
                        UiDesignRenderTests.RenderWindow(vm, width, 1000, Path.Combine(outDir, "editor-selected-" + width + "-" + theme + ".png"), selectNodes, detach: true);
                        Assert.Equal(3, vm.RelationNodeSelectionCount);
                        // The Relations list: two relations selected with Ctrl+click.
                        Action<MainWindow> selectRelations = main =>
                        {
                            ListBox list = main.FindControl<ListBox>("RelationsListBox")!;
                            list.SelectedItems!.Clear();
                            foreach (RelationListItem item in vm.Relations.Where(r => r.Name is "TMS Up" or "TMS Down"))
                                list.SelectedItems.Add(item);
                        };
                        UiDesignRenderTests.RenderWindow(vm, width, 1000, Path.Combine(outDir, "relations-selected-" + width + "-" + theme + ".png"), selectRelations, detach: true);
                        Assert.True(vm.HasRelationMultiSelection);
                        vm.OpenMoveRelationNodesCommand.Execute(null);
                        vm.IsMoveNodesToExisting = true;
                        vm.SelectedMoveNodesTarget = vm.MoveNodesTargets.FirstOrDefault();
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, 1000, Path.Combine(outDir, "move-dialog-" + width + "-" + theme + ".png"), detach: true);
                        vm.IsMoveNodesOpen = false;
                        vm.SetRelationMassSelection([]);
                    }
                }
            }
            finally
            {
                vm.StopGameConfigWatcher();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
                File.Delete(Path.Combine(outDir, "warm-up.png"));
            }
        });
    }

    private static async Task<MainWindowViewModel> OpenAsync(RecordingEngine fake, string relation)
    {
        MainWindowViewModel vm = await UiDesignRenderTests.CreateLoadedVmAsync(fake.Design, fake);
        await SelectRelationAsync(vm, relation);
        return vm;
    }

    private static async Task SelectRelationAsync(MainWindowViewModel vm, string relation)
    {
        vm.SelectedRelation = vm.Relations.Single(r => r.Name == relation);
        await vm.ShowRelationAsync(relation);
        for (int i = 0; i < 200 && vm.RelationNodes.Count == 0; i++)
            await Task.Delay(10);
        Assert.NotEmpty(vm.RelationNodes);
    }

    private static string ValueAfter(IReadOnlyList<string> args, string option)
    {
        int at = args.ToList().IndexOf(option);
        return at >= 0 && at + 1 < args.Count ? args[at + 1] : string.Empty;
    }

    // UiDesignRenderTests' profile; the transfer commands answer as the engine does.
    internal sealed class RecordingEngine : IEngineCliClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public UiDesignRenderTests.DesignFakeEngine Design { get; } = new();

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            if (AxisTuningTests.Seq(arguments, "relations", "item", "copy-to") || AxisTuningTests.Seq(arguments, "relations", "item", "move-to") ||
                AxisTuningTests.Seq(arguments, "relations", "mass", "move-actions") || AxisTuningTests.Seq(arguments, "relations", "item", "remove"))
                return Task.FromResult(new EngineCommandResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(1), StandardOutput = "{}", StandardError = string.Empty, Arguments = arguments.ToArray() });
            return Design.RunAsync(engineExePath, arguments, cancellationToken);
        }
    }
}
