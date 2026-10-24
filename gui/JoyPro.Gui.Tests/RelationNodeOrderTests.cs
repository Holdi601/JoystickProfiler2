using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The order of a relation's actions in the relation editor: dragged by the grip (dropped above or below a card),
// Alt+Up / Alt+Down and the card's context menu; the engine keeps it in the profile (relations item move).
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class RelationNodeOrderTests
{
    [Theory]
    // from, card dropped on, below it -> new index
    [InlineData(0, 2, false, 1)]
    [InlineData(0, 2, true, 2)]
    [InlineData(3, 0, false, 0)]
    [InlineData(3, 0, true, 1)]
    [InlineData(1, 1, true, 1)]
    [InlineData(1, 2, false, 1)]
    public void DropAboveOrBelowACard_GivesTheNewPlace(int from, int over, bool below, int expected) =>
        Assert.Equal(expected, MainWindow.RelationNodeDropIndex(from, over, below));

    private static string? Engine()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "bin")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return engine != null && File.Exists(engine) ? engine : null;
    }

    // The real engine takes a few seconds per call.
    private static async Task WaitLongAsync(Func<bool> condition)
    {
        for (int i = 0; i < 600 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition());
    }

    private static List<string> Order(MainWindowViewModel vm) => vm.RelationNodes.Select(n => n.Game + ":" + n.Id).ToList();

    // The real engine: a move is saved in the profile and comes back in that order after a reload; the keyboard
    // (Alt+Down on the action list of the real page) and the context menu commands move too.
    [Fact]
    public void Move_IsKeptInTheProfile_AndTheKeyboardMovesTheSelectedAction()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "nodeorder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            DataDirEngineClient client = new(Path.Combine(folder, "data"));
            string profile = Path.Combine(folder, "order.pr0file");
            EngineCommandResult add = client.RunAsync(engine, ["--profile", profile, "relations", "add", "Pitch", "--axis",
                "--node", "DCS:a2001cdnil:FA-18C_hornet", "--node", "IL2Game:mc_pitch:IL2Game", "--node", "WarThunder:elevator:WarThunder"]).GetAwaiter().GetResult();
            Assert.True(add.ExitCode == 0, add.StandardError);

            AvaloniaRenderThread.Run(() =>
            {
                MainWindowViewModel vm = new(client, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
                vm.CurrentStage = UiStage.Relations;
                AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
                using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 800));
                vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Pitch");
                AvaloniaRenderThread.Wait(WaitLongAsync(() => vm.RelationNodes.Count == 3 && !vm.IsBusy));
                Assert.Equal(["DCS:a2001cdnil", "IL2Game:mc_pitch", "WarThunder:elevator"], Order(vm));

                // Drag: War Thunder dropped above the first card.
                RelationNodeItem thunder = vm.RelationNodes[2];
                Assert.True(AvaloniaRenderThread.Wait(vm.MoveRelationNodeAsync(thunder, MainWindow.RelationNodeDropIndex(2, 0, below: false))));
                Assert.Equal(["WarThunder:elevator", "DCS:a2001cdnil", "IL2Game:mc_pitch"], Order(vm));
                Assert.Contains("is now action 1 of 3", vm.StatusText);

                // Keyboard: Alt+Down on the action list moves the selected action one down.
                vm.SelectedRelationNode = vm.RelationNodes[0];
                main.Settle();
                ListBox list = main.Window.FindControl<ListBox>("RelationNodeList")!;
                list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, KeyModifiers = KeyModifiers.Alt, Source = list });
                AvaloniaRenderThread.Wait(WaitLongAsync(() => !vm.IsBusy && Order(vm)[1] == "WarThunder:elevator"));
                Assert.Equal(["DCS:a2001cdnil", "WarThunder:elevator", "IL2Game:mc_pitch"], Order(vm));

                // Context menu: to the bottom; then the relation is read again from the profile.
                AvaloniaRenderThread.Wait(vm.MoveRelationNodeToBottomCommand.ExecuteAsync(vm.RelationNodes[0]));
                Assert.Equal(["WarThunder:elevator", "IL2Game:mc_pitch", "DCS:a2001cdnil"], Order(vm));
                main.Snap("relation-node-order.png");
            });

            // A fresh engine reads the order from the file.
            EngineCommandResult show = client.RunAsync(engine, ["--profile", profile, "--text", "relations", "show", "Pitch"]).GetAwaiter().GetResult();
            List<string> lines = show.StandardOutput.Split('\n').Where(l => l.TrimStart().StartsWith('[')).Select(l => l.Trim()).ToList();
            Assert.StartsWith("[1] game=WarThunder, id=elevator", lines[0]);
            Assert.StartsWith("[2] game=IL2Game, id=mc_pitch", lines[1]);
            Assert.StartsWith("[3] game=DCS, id=a2001cdnil", lines[2]);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // A relation of one action: nothing moves, no engine call.
    [Fact]
    public async Task OneAction_NothingMoves()
    {
        BuilderFakeEngine fake = new();
        fake.AddPitchRelation();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "relation-order-refused.pr0file");
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        Assert.Single(vm.RelationNodes);
        // One action: nothing to move.
        Assert.False(await vm.MoveRelationNodeAsync(vm.RelationNodes[0], 1));
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "item", "move"));
        await RelationBuilderTests.SettleAllAsync();
    }
}
