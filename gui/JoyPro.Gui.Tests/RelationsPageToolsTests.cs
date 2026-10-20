using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Relations page's top tools close when the user leaves the page: it always opens in its normal state.
[SupportedOSPlatform("windows")]
public sealed class RelationsPageToolsTests
{
    [Fact]
    public async Task LeavingTheRelationsPage_ClosesTheAutoBuildWizardAndTheOtherTools()
    {
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(new BuilderFakeEngine(), "relations-tools.pr0file");
        try
        {
            vm.ToggleOrganizeCommand.Execute(null);
            Assert.True(vm.IsAutoGroupPanelOpen);
            Assert.True(vm.IsAnyRelationToolOpen);
            vm.CurrentStage = UiStage.Binds;
            vm.CurrentStage = UiStage.Relations;
            Assert.False(vm.IsOrganizeOpen);
            Assert.False(vm.IsAnyRelationToolOpen);
            Assert.True(vm.IsNoRelationToolOpen);

            // By description and the AI plan panel too; the auto-build entry from the Start page still opens it.
            vm.ShowOrganizeByDescriptionCommand.Execute(null);
            vm.CurrentStage = UiStage.Start;
            Assert.False(vm.IsDescribeGroupPanelOpen);
            vm.StartAutoGroupCommand.Execute(null);
            Assert.Equal(UiStage.Relations, vm.CurrentStage);
            Assert.True(vm.IsAutoGroupPanelOpen);
        }
        finally
        {
            await RelationBuilderTests.SettleAllAsync();
        }
    }
}
