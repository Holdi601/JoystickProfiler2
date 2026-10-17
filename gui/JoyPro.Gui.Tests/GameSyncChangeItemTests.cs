using JoyPro.Gui.Models;
using Xunit;

namespace JoyPro.Gui.Tests;

// The choices the game config watcher's dialog sends to engine sync apply.
public sealed class GameSyncChangeItemTests
{
    [Fact]
    public void AddedBind_OffersCandidatesNewRelationAndIgnore()
    {
        GameSyncChangeItem item = new("c1", "added", "Rockets now on Stick JOY_BTN5", ["Brake", "Bombs"], "Brake", "War Thunder Fire rocket");
        Assert.Equal(["Brake", "Bombs", GameSyncChangeItem.NewRelationOption, GameSyncChangeItem.IgnoreOption], item.Targets);
        Assert.Equal(("Brake", string.Empty), item.Choice());
        item.SelectedTarget = GameSyncChangeItem.NewRelationOption;
        Assert.True(item.IsNewRelation);
        Assert.Equal(("new", "War Thunder Fire rocket"), item.Choice());
        item.IsSelected = false;
        Assert.Equal(("ignore", string.Empty), item.Choice());
    }

    [Fact]
    public void RemovedBind_HasNoNewRelation()
    {
        GameSyncChangeItem item = new("c2", "removed", "Gear no longer on Throttle JOY_BTN7", ["Toggle Gear"], "Toggle Gear", string.Empty);
        Assert.DoesNotContain(GameSyncChangeItem.NewRelationOption, item.Targets);
        Assert.Equal(("Toggle Gear", string.Empty), item.Choice());
    }

    [Fact]
    public void GameSetting_IsTakenOverOrIgnored()
    {
        GameSyncChangeItem item = new("c3", "changed-setting", "Game setting force: 0.7 -> 0.5", [], "apply", string.Empty);
        Assert.Equal([GameSyncChangeItem.TakeOverOption, GameSyncChangeItem.IgnoreOption], item.Targets);
        Assert.Equal("Game setting changed", item.KindLabel);
        Assert.Equal(("apply", string.Empty), item.Choice());
        item.SelectedTarget = GameSyncChangeItem.IgnoreOption;
        Assert.Equal(("ignore", string.Empty), item.Choice());
    }
}
