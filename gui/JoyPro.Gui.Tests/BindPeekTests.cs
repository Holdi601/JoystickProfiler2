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
            BindPeekGroup dcs = Assert.Single(vm.BindPeekGroups);
            Assert.Equal("DCS (1)", dcs.Header);
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
}
