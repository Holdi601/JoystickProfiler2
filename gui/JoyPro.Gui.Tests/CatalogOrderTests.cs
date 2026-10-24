using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The catalog's own order (report: Star Citizen opened a Pitch relation's catalog with "Character customizer"): the
// categories that matter for the open relation first, the rest by name. And no game id under any row.
[SupportedOSPlatform("windows")]
public sealed class CatalogOrderTests
{
    private static CatalogRowItem Row(string game, string id, string title, string category, string function = "", bool recommended = false) =>
        new(new BuilderSearchResultItem(game, id, title, category, true, [game], [], string.Empty) { FunctionName = function, IsRecommended = recommended }, string.Empty, string.Empty);

    [Fact]
    public async Task Relevance_RelationsActionsThenItsFunctionThenRecommended()
    {
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(new BuilderFakeEngine());
        vm.BuilderFunctionText = "Pitch axis";
        CatalogRowItem customizer = Row("StarCitizen", "character_customizer|character_customizer_gp_pitch", "Character customizer gp pitch", "Character customizer");
        CatalogRowItem flight = Row("StarCitizen", "spaceship_movement|v_pitch", "Pitch", "Flight: movement", "Pitch axis", recommended: true);
        CatalogRowItem turret = Row("StarCitizen", "turret_movement|turret_pitch", "Pitch", "Turret: movement", "Pitch axis");
        CatalogRowItem view = Row("StarCitizen", "spaceship_view|v_view_zoom", "Zoom", "Flight: view", "Zoom", recommended: true);
        Assert.Equal(0, vm.CatalogCategoryRelevance([customizer]));
        Assert.Equal(3, vm.CatalogCategoryRelevance([flight]));
        Assert.Equal(2, vm.CatalogCategoryRelevance([turret]));
        Assert.Equal(1, vm.CatalogCategoryRelevance([view]));
        flight.Row.IsInOpenRelation = true;
        Assert.Equal(4, vm.CatalogCategoryRelevance([customizer, flight]));
    }

    // Outside DCS the game's id is neither under the title nor in the caption; the title's tooltip keeps it.
    [Fact]
    public void Rows_ShowNoGameId_TheTooltipHasIt()
    {
        BuilderSearchResultItem row = new("MSFS2024", "KEY_AXIS_CONDITION_LEVER_1_SET", "Condition Lever 1 Axis", "Aircraft", true, ["MSFS2024"], [], string.Empty);
        Assert.False(row.HasIdHint);
        Assert.False(row.HasIdText);
        Assert.Equal("Aircraft", row.PlanesText);
        Assert.Contains("KEY_AXIS_CONDITION_LEVER_1_SET", row.IdTip);
        BuilderSearchResultItem dcs = new("DCS", "d3014pnil", "Countermeasures Fwd", "HOTAS", false, ["F-16C_50"], [], string.Empty);
        Assert.DoesNotContain("d3014pnil", dcs.IdTip);
    }
}
