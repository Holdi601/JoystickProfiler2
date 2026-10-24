using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Binds page's read-only peek at the selected relation's actions (ViewModels/MainWindowViewModel.BindPeek.cs).
[SupportedOSPlatform("windows")]
public sealed class BindPeekTests
{
    [Fact]
    public async Task SelectedBind_ShowsItsRelationsActionsByGame_CollapsesAndOpensInRelations()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "bind-peek.pr0file");
        try
        {
            vm.CurrentStage = UiStage.Binds;
            await vm.RefreshBindsCommand.ExecuteAsync(null);
            vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Flare");
            await RelationBuilderTests.WaitForAsync(() => vm.HasBindPeekActions);
            // The peek reads the text lines of "relations show" (the engine answers JSON without --text: the real
            // engine's peek was empty).
            Assert.All(fake.Calls.Where(c => AxisTuningTests.Seq(c, "relations", "show")), c => Assert.Contains("--text", c));
            BindPeekGroup dcs = Assert.Single(vm.BindPeekGroups);
            Assert.Equal("DCS World (1)", dcs.Header);
            BindPeekAction action = Assert.Single(dcs.Actions);
            Assert.Equal("1 aircraft", action.PlanesText);
            Assert.Equal("F-16C_50", action.PlaneNames);
            Assert.Equal("Button", action.KindText);
            Assert.False(action.IsExpanded);
            action.ToggleExpandedCommand.Execute(null);
            Assert.True(action.IsExpanded);
            Assert.Equal("What it does: 1 action in 1 game", vm.BindPeekTitle);

            // Closed is remembered; closed, it reads nothing.
            vm.ToggleBindPeekCommand.Execute(null);
            Assert.False(vm.IsBindPeekOpen);
            Assert.False(vm.ListPrefs.BindPeekOpen);
            Assert.Equal("Show", vm.BindPeekToggleText);
            vm.ToggleBindPeekCommand.Execute(null);
            Assert.True(vm.IsBindPeekOpen);

            await vm.OpenBindPeekInRelationsCommand.ExecuteAsync(null);
            Assert.Equal(UiStage.Relations, vm.CurrentStage);
            Assert.Equal("Flare", vm.SelectedRelation?.Name);
        }
        finally
        {
            await RelationBuilderTests.SettleAllAsync();
        }
    }

    // An action of another game that the profile stored without a title shows its title in the game (as the relation
    // editor does), not its id ("elevator").
    [Fact]
    public async Task OtherGamesAction_WithoutStoredTitle_ShowsItsGameTitle()
    {
        BuilderFakeEngine fake = new();
        fake.AddElevatorRelation();
        fake.AircraftJson = "{\"Relation\":\"Elevator\",\"Nodes\":[{\"Game\":\"DCS\",\"Id\":\"a2001cdnil\",\"Title\":\"Pitch\",\"Aircraft\":[]}," +
                            "{\"Game\":\"WarThunder\",\"Id\":\"elevator\",\"Title\":\"Pitch axis\",\"Aircraft\":[]}]}";
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "bind-peek-titles.pr0file");
        try
        {
            vm.CurrentStage = UiStage.Binds;
            await vm.RefreshBindsCommand.ExecuteAsync(null);
            vm.IsBindPeekOpen = true;
            await vm.LoadBindPeekAsync("Elevator");
            BindPeekAction warThunder = Assert.Single(vm.BindPeekGroups.Single(g => g.Game == "WarThunder").Actions);
            Assert.Equal("Pitch axis", warThunder.Title);
            Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "aircraft", "Elevator"));

            // A relation of DCS actions only asks nothing more.
            int before = fake.Calls.Count(c => AxisTuningTests.Seq(c, "relations", "aircraft"));
            await vm.LoadBindPeekAsync("Flare");
            Assert.Equal(before, fake.Calls.Count(c => AxisTuningTests.Seq(c, "relations", "aircraft")));
        }
        finally
        {
            await RelationBuilderTests.SettleAllAsync();
        }
    }
}
