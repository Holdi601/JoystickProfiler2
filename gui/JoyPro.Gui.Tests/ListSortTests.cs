using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The tiered sort of the Relations and Binds lists (ViewModels/ListSortViewModel.cs).
[SupportedOSPlatform("windows")]
public sealed class ListSortTests
{
    private static ListSortRow Row(string name, string device, string input, bool axis = false) =>
        new(name, device, input, axis, "DCS", 0, string.Empty, device.Length > 0, string.Empty);

    // 1) Device, 2) Input (natural), 3) Name; unbound rows last; a level turned around; reordered levels.
    [Fact]
    public void Levels_SortInOrderAndCanBeTurnedAndMoved()
    {
        List<ListSortRow> rows =
        [
            Row("Gear", "Throttle", "JOY_BTN10"),
            Row("Flaps", "Stick", "JOY_BTN2"),
            Row("Pitch", "Stick", "JOY_Y", axis: true),
            Row("Brake", "Throttle", "JOY_BTN10"),
            Row("Unbound", string.Empty, string.Empty),
            Row("Roll", "Stick", "JOY_X", axis: true),
        ];
        ListSortViewModel sort = new("test");
        // No level: by name (the list's own order).
        Assert.Equal(["Brake", "Flaps", "Gear", "Pitch", "Roll", "Unbound"], sort.Sort(rows, r => r).Select(r => r.Name).ToArray());
        sort.Add("device", false);
        sort.Add("input", false);
        Assert.Equal(["Roll", "Pitch", "Flaps", "Brake", "Gear", "Unbound"], sort.Sort(rows, r => r).Select(r => r.Name).ToArray());
        Assert.Equal("Sort: Device, Input", sort.Label);

        // Name descending as the third level decides between the two on Throttle BTN 10.
        sort.Add("name", true);
        Assert.Equal(["Roll", "Pitch", "Flaps", "Gear", "Brake", "Unbound"], sort.Sort(rows, r => r).Select(r => r.Name).ToArray());

        // Input first: the axes, then the buttons by number, device second.
        sort.Levels[1].MoveUpCommand.Execute(null);
        Assert.Equal("input", sort.Levels[0].Key.Key);
        Assert.Equal(["1.", "2.", "3."], sort.Levels.Select(l => l.Number).ToArray());
        Assert.Equal(["Roll", "Pitch", "Flaps", "Gear", "Brake", "Unbound"], sort.Sort(rows, r => r).Select(r => r.Name).ToArray());
        sort.Levels[0].ToggleDirectionCommand.Execute(null);
        Assert.Equal("Z to A", sort.Levels[0].DirectionLabel);
        sort.Levels[0].RemoveCommand.Execute(null);
        sort.Levels[0].RemoveCommand.Execute(null);
        Assert.Equal(["Unbound", "Roll", "Pitch", "Gear", "Flaps", "Brake"], sort.Sort(rows, r => r).Select(r => r.Name).ToArray());
        sort.ResetCommand.Execute(null);
        Assert.False(sort.HasLevels);
    }

    [Fact]
    public void Input_HasTheNaturalOrder()
    {
        string[] inputs = ["JOY_BTN10", "JOY_SLIDER1", "JOY_BTN2", "JOY_RZ", "JOY_BTN_POV1_U", "JOY_X", "JOY_RX", "JOY_BTN1", "JOY_Z", "JOY_Y", "JOY_RY", "LShift"];
        string[] sorted = inputs.OrderBy(i => i, Comparer<string>.Create(ListSortRow.CompareInput)).ToArray();
        Assert.Equal(["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1", "JOY_BTN1", "JOY_BTN2", "JOY_BTN10", "JOY_BTN_POV1_U", "LShift"], sorted);
        Assert.True(ListSortRow.Natural("Panel 2", "Panel 10") < 0);
    }

    // The levels survive a restart (list-preferences.json "Sorts"), unknown keys are dropped.
    [Fact]
    public void Levels_AreRememberedInTheListPreferences()
    {
        string file = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "list-sort-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ListSortViewModel sort = new("binds");
            sort.Add("device", false);
            sort.Add("input", true);
            ListPreferences prefs = new();
            prefs.Sorts[sort.Page] = sort.ToState();
            prefs.Save(file);

            ListPreferences loaded = ListPreferences.Load(file);
            ListSortViewModel again = new("binds");
            again.LoadState(loaded.Sorts["BINDS"].Append("nonsense:asc"));
            Assert.Equal(["device:asc", "input:desc"], again.ToState());
        }
        finally
        {
            File.Delete(file);
        }
    }

    // The Relations page: a sort level reorders the list (with the filters), keeps the open relation and is remembered.
    [Fact]
    public async Task RelationsPage_SortsTheListAndRemembersIt()
    {
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(new BuilderFakeEngine(), "list-sort.pr0file");
        try
        {
            vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Chaff");
            Assert.Equal(vm.Relations.Select(r => r.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase), vm.Relations.Select(r => r.Name));
            vm.RelationSort.Add("bound", false);
            Assert.Equal("Flare", vm.Relations[0].Name);
            Assert.Equal("Chaff", vm.SelectedRelation?.Name);
            Assert.Equal(["bound:asc"], vm.ListPrefs.Sorts["relations"]);

            // The Binds page's old sort buttons put their key first.
            vm.SortBindsByColumnCommand.Execute("TypeDisplay");
            Assert.Equal(["kind:asc"], vm.ListPrefs.Sorts["binds"]);
        }
        finally
        {
            await RelationBuilderTests.SettleAllAsync();
        }
    }
}
