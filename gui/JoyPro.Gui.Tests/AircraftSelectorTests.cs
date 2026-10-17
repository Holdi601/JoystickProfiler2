using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Same id, different meaning per aircraft (docs/relation-builder.md): the aircraft selector of the Add actions rows
// and of the open relation's actions (Models/RelationBuilderItems.cs AircraftSelector,
// ViewModels/MainWindowViewModel.RelationAircraft.cs): All / None / Only same title / Invert / filter, Shift+click
// ranges, the badge and the "differs" marker; a row adds only its ticked aircraft; a node's ticks go to the engine in
// one step; the profile check notice and its repair; Show more / Show less per game.
[SupportedOSPlatform("windows")]
public sealed class AircraftSelectorTests : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await RelationBuilderTests.SettleAllAsync();

    private static AircraftSelector PitchSelector() => AircraftSelector.FromVariants("Pitch",
    [
        ("Pitch", "Flight Control", "same", "", true, (IReadOnlyList<string>)["F-15C", "F-16C_50"]),
        ("Pitch", "", "near", "category differs", true, ["FA-18C_hornet"]),
        ("Trim pitch", "Flight Control", "near", "title differs by 'trim'", true, ["MiG-29"]),
        ("Collective", "Flight Control", "differs", "", false, ["UH-1H"]),
    ]);

    [Fact]
    public void Selector_TicksTheRowsMeaningAndMarksTheOthers()
    {
        AircraftSelector s = PitchSelector();
        Assert.Equal(5, s.Total);
        Assert.Equal(4, s.CheckedCount);
        Assert.Equal("4 of 5 aircraft", s.Badge);
        Assert.Equal("F-15C, F-16C_50 +2 more", s.Summary);
        AircraftChoiceItem heli = s.Items.Single(i => i.Plane == "UH-1H");
        Assert.True(heli.IsDiffers);
        Assert.False(heli.IsChecked);
        Assert.Equal("differs", heli.MarkerText);
        Assert.Equal("Collective · Flight Control", heli.MeaningText);
        // The ones where the id means something else come last.
        Assert.Equal("UH-1H", s.Items.Last().Plane);
        Assert.True(s.HasDiffers);
        Assert.False(s.HasMixed);
        Assert.False(s.HasChanges);
    }

    [Fact]
    public void Selector_AllNoneInvertOnlySameTitle()
    {
        AircraftSelector s = PitchSelector();
        s.SelectAll();
        Assert.Equal(5, s.CheckedCount);
        Assert.True(s.HasMixed);
        Assert.Equal(1, s.CheckedDiffersCount);
        Assert.True(s.HasChanges);

        s.SelectNone();
        Assert.Equal(0, s.CheckedCount);
        Assert.Equal("0 of 5 aircraft", s.Badge);
        Assert.Equal("no aircraft ticked", s.Summary);

        s.Invert();
        Assert.Equal(5, s.CheckedCount);

        // Only same title: the exact title (also in another category), not the near "Trim pitch", never "Collective".
        s.OnlySameTitle();
        Assert.Equal(["F-15C", "F-16C_50", "FA-18C_hornet"], s.CheckedPlanes.OrderBy(p => p, StringComparer.Ordinal).ToArray());

        s.Reset();
        Assert.Equal(4, s.CheckedCount);
        Assert.False(s.HasChanges);
    }

    [Fact]
    public void Selector_FilterLimitsTheBulkButtons()
    {
        AircraftSelector s = PitchSelector();
        s.FilterText = "F-1";
        Assert.Equal(["F-15C", "F-16C_50"], s.Visible.Select(i => i.Plane).ToArray());
        s.SelectNone();
        Assert.Equal(2, s.CheckedCount);
        Assert.True(s.Items.Single(i => i.Plane == "FA-18C_hornet").IsChecked);

        // A title filter, and "differs" for the ones with another meaning.
        s.FilterText = "collective";
        Assert.Equal("UH-1H", Assert.Single(s.Visible).Plane);
        s.FilterText = "differs";
        Assert.Equal("UH-1H", Assert.Single(s.Visible).Plane);
        s.SelectAll();
        Assert.True(s.Items.Single(i => i.Plane == "UH-1H").IsChecked);
        s.FilterText = string.Empty;
        Assert.Equal(5, s.Visible.Count);
    }

    [Fact]
    public void Selector_ShiftClickTicksARange()
    {
        AircraftSelector s = PitchSelector();
        s.SelectNone();
        List<AircraftChoiceItem> list = s.Visible.ToList();
        // A click ticks the first one (the CheckBox toggles, then the click is reported).
        list[0].IsChecked = true;
        s.Clicked(list[0], shift: false);
        // Shift+click on the fourth: everything between takes its state.
        list[3].IsChecked = true;
        s.Clicked(list[3], shift: true);
        Assert.Equal(4, s.CheckedCount);
        Assert.False(list[4].IsChecked);
        // Shift+click unticking a range.
        list[1].IsChecked = false;
        s.Clicked(list[1], shift: true);
        Assert.True(list[0].IsChecked);
        Assert.False(list[1].IsChecked);
        Assert.False(list[2].IsChecked);
        Assert.False(list[3].IsChecked);
    }

    [Fact]
    public async Task SearchRow_AddsOnlyTheTickedAircraft()
    {
        PerAircraftFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        vm.SelectedRelation = null;
        vm.BuilderSearchText = "pitch";
        await vm.SearchBuilderActionsAsync();
        BuilderSearchResultItem row = vm.BuilderSearchResults.Single(r => r.Id == "a2001cdnil");
        Assert.True(row.HasAircraft);
        Assert.Equal("same id, different meaning in 1 aircraft (left out)", row.MeaningNote);
        Assert.Equal("DCS:a2001cdnil:F-15C|F-16C_50|FA-18C_hornet", row.NodeSpec);
        Assert.Equal("3 of 4 aircraft", row.Aircraft!.Badge);
        Assert.False(row.IsChecked);

        // Unticking an aircraft ticks the row with the rest; the helicopter never goes in unless ticked.
        row.Aircraft.Items.Single(i => i.Plane == "FA-18C_hornet").IsChecked = false;
        Assert.True(row.IsChecked);
        Assert.Equal("DCS:a2001cdnil:F-15C|F-16C_50", row.NodeSpec);
        Assert.Equal("F-15C, F-16C_50", row.PlanesText);

        // A new search keeps the row's ticks and its aircraft ticks.
        await vm.SearchBuilderActionsAsync();
        BuilderSearchResultItem again = vm.BuilderSearchResults.Single(r => r.Id == "a2001cdnil");
        Assert.True(again.IsChecked);
        Assert.Equal("DCS:a2001cdnil:F-15C|F-16C_50", again.NodeSpec);

        fake.Calls.Clear();
        await vm.AddCheckedBuilderResultsAsync();
        IReadOnlyList<string> add = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "add"));
        Assert.True(AxisTuningTests.Seq(add, "--node", "DCS:a2001cdnil:F-15C|F-16C_50"));
        Assert.DoesNotContain(add, a => a.Contains("UH-1H", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GameGroup_ShowMoreLoadsTheRestAndKeepsTicks()
    {
        PerAircraftFakeEngine fake = new() { DcsTotal = 30 };
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        vm.SelectedRelation = null;
        vm.BuilderSearchText = "pitch";
        await vm.SearchBuilderActionsAsync();
        BuilderSearchGroupItem dcs = vm.BuilderSearchRows.OfType<BuilderSearchGroupItem>().Single(g => g.Game == "DCS");
        Assert.True(dcs.CanExpand);
        Assert.Equal("Show 29 more", dcs.ExpandLabel);
        // The header counts what the game chip counts.
        Assert.Equal("1 of 30 actions", dcs.CountText);
        Assert.True(vm.CanExpandAllBuilderGames);
        vm.BuilderSearchResults.Single(r => r.Id == "a2001cdnil").IsChecked = true;

        fake.Calls.Clear();
        await vm.ToggleBuilderGameExpandedAsync(dcs);
        IReadOnlyList<string> more = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "search"));
        Assert.True(AxisTuningTests.Seq(more, "--game", "DCS"));
        dcs = vm.BuilderSearchRows.OfType<BuilderSearchGroupItem>().Single(g => g.Game == "DCS");
        Assert.True(dcs.IsExpanded);
        Assert.Equal("Show less", dcs.ExpandLabel);
        Assert.Equal(3, vm.BuilderSearchRows.OfType<BuilderSearchResultItem>().Count(r => r.Game == "DCS"));
        Assert.True(vm.BuilderSearchResults.Single(r => r.Id == "a2001cdnil").IsChecked);
        Assert.True(vm.CanCollapseAllBuilderGames);

        // The same query keeps it expanded; Collapse all folds it again.
        await vm.SearchBuilderActionsAsync();
        Assert.True(vm.BuilderSearchRows.OfType<BuilderSearchGroupItem>().Single(g => g.Game == "DCS").IsExpanded);
        vm.CollapseAllBuilderGames();
        Assert.False(vm.BuilderSearchRows.OfType<BuilderSearchGroupItem>().Single(g => g.Game == "DCS").IsExpanded);
        Assert.True(vm.BuilderSearchResults.Single(r => r.Id == "a2001cdnil").IsChecked);
    }

    [Fact]
    public async Task OpenRelation_EachActionHasItsAircraftWithBadgeMixedFlagAndApply()
    {
        PerAircraftFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        await RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Any(n => n.HasAircraft));
        RelationNodeItem node = vm.RelationNodes.Single(n => n.Id == "d3014pnil");
        Assert.Equal("2 of 3 aircraft", node.BadgeText);
        Assert.True(node.IsMixed);
        Assert.Contains("this id is not 'Countermeasures Management Switch - Fwd'", node.MixedText);
        Assert.True(vm.HasMixedRelationNodes);

        // Untick the aircraft where the id means something else, tick another: one engine run.
        node.Aircraft!.Items.Single(i => i.Plane == "F-15E").IsChecked = false;
        node.Aircraft.Items.Single(i => i.Plane == "F-14").IsChecked = true;
        Assert.False(node.IsMixed);
        Assert.Equal("2 of 3 aircraft", node.BadgeText);
        Assert.True(node.Aircraft.HasChanges);
        fake.Calls.Clear();
        await vm.ApplyNodeAircraftCommand.ExecuteAsync(node);
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "aircraft", "set"));
        Assert.True(AxisTuningTests.Seq(set, "set", "Flare", "--node", "DCS:d3014pnil", "--on", "F-14", "--off", "F-15E"));
    }

    [Fact]
    public async Task ProfileCheck_ShowsTheNoticeAndRepairsInOneStep()
    {
        PerAircraftFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await vm.RefreshAircraftCheckAsync();
        Assert.True(vm.HasAircraftCheckNotice);
        Assert.Contains("1 action in 1 relation include 1 aircraft", vm.AircraftCheckText);
        AircraftCheckItem item = Assert.Single(vm.AircraftCheckItems);
        Assert.Equal("Pitch: 'Pitch' - but 'Collective' in UH-1H", item.Text);

        fake.Calls.Clear();
        await vm.RepairAircraftCommand.ExecuteAsync(null);
        Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "check-aircraft", "--apply"));
        Assert.False(vm.HasAircraftCheckNotice);
        Assert.Contains("1 aircraft switched off", vm.StatusText);

        // "Not now" hides it.
        await vm.RefreshAircraftCheckAsync();
        Assert.True(vm.HasAircraftCheckNotice);
        vm.DismissAircraftCheckCommand.Execute(null);
        Assert.False(vm.HasAircraftCheckNotice);
    }

    // The aircraft are listed by what the id means in each: a heading over each group of the shown ones; "Only same
    // meaning" ticks same and near and unticks the other meanings.
    [Fact]
    public void Selector_GroupsTheAircraftByMeaning()
    {
        AircraftSelector s = PitchSelector();
        Assert.Equal(["F-15C", "F-16C_50", "FA-18C_hornet", "MiG-29", "UH-1H"], s.Visible.Select(i => i.Plane).ToArray());
        Assert.Equal(["Same meaning: 'Pitch' (2)", "Nearly the same (2)", "Other meaning here (own title shown) (1)"],
            s.Visible.Where(i => i.HasGroupHeader).Select(i => i.GroupHeader).ToArray());
        Assert.Equal("F-15C", s.Visible.First(i => i.HasGroupHeader).Plane);
        Assert.Equal("Collective · Flight Control", s.Visible.Single(i => i.IsDiffers).MeaningText);

        // The headings follow the filter.
        s.FilterText = "F-1";
        Assert.Equal(["Same meaning: 'Pitch' (2)"], s.Visible.Where(i => i.HasGroupHeader).Select(i => i.GroupHeader).ToArray());
        s.FilterText = string.Empty;

        s.SelectAll();
        Assert.True(s.HasMixed);
        s.OnlySameMeaning();
        Assert.Equal(4, s.CheckedCount);
        Assert.False(s.Items.Single(i => i.Plane == "UH-1H").IsChecked);
        Assert.False(s.HasMixed);
    }

    // A node's aircraft come grouped too: the same meaning first (ticked ones first), the other meaning last.
    [Fact]
    public void Selector_SortsGivenItemsByMeaning()
    {
        AircraftSelector s = new("Flare",
        [
            new AircraftChoiceItem("F-15E", "Canopy Open", "Cockpit", "differs", "", true),
            new AircraftChoiceItem("F-16C_50", "Flare", "Stick", "same", "", true),
            new AircraftChoiceItem("AV8BNA", "", "", "unknown", "", false),
            new AircraftChoiceItem("F-14", "Flare", "Stick", "same", "", false),
        ]);
        Assert.Equal(["F-16C_50", "F-14", "AV8BNA", "F-15E"], s.Items.Select(i => i.Plane).ToArray());
        Assert.Equal(["Same meaning: 'Flare' (2)", "Not in its list (1)", "Other meaning here (own title shown) (1)"],
            s.Visible.Where(i => i.HasGroupHeader).Select(i => i.GroupHeader).ToArray());
    }

    // Another title of the same function (a helicopter's "Flight Control Cyclic Pitch" for "Pitch") is its own group, no
    // mixed meaning and no "differs" warning; only another function is.
    [Fact]
    public void Selector_SameFunctionUnderAnotherTitleIsNoMixedMeaning()
    {
        AircraftSelector s = new("Pitch",
        [
            new AircraftChoiceItem("F-16C_50", "Pitch", "", "same", "", true),
            new AircraftChoiceItem("UH-1H", "Flight Control Cyclic Pitch", "", "differs", "", true, sameFunction: true),
            new AircraftChoiceItem("La-7", "Bomb control handle", "", "differs", "", false),
        ]);
        Assert.Equal(["Same meaning: 'Pitch' (1)", "Other title, same function (1)", "Other meaning here (own title shown) (1)"],
            s.Visible.Where(i => i.HasGroupHeader).Select(i => i.GroupHeader).ToArray());
        Assert.False(s.HasMixed);
        Assert.Equal("same function", s.Items.Single(i => i.Plane == "UH-1H").MarkerText);
        Assert.Equal("2 of 3 aircraft · 1 differ · 1 other title", s.ExpandLabel);
        s.SelectAll();
        Assert.True(s.HasMixed);
        Assert.Equal(1, s.CheckedDiffersCount);
        s.OnlySameFunction();
        Assert.Equal(["F-16C_50", "UH-1H"], s.CheckedPlanes.ToArray());
        s.OnlySameMeaning();
        Assert.Equal(["F-16C_50"], s.CheckedPlanes.ToArray());
    }

    // A click on an action of the open relation opens its aircraft, a second click closes them; open stays open when
    // the relation is loaded again (Apply); an action of a one-list game has none.
    [Fact]
    public async Task OpenRelation_AClickOnAnActionOpensItsAircraft()
    {
        PerAircraftFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        await RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Any(n => n.HasAircraft));
        RelationNodeItem node = vm.RelationNodes.Single(n => n.Id == "d3014pnil");
        Assert.False(node.Aircraft!.IsExpanded);
        vm.ToggleNodeAircraftCommand.Execute(node);
        Assert.True(node.Aircraft.IsExpanded);
        Assert.Same(node, vm.SelectedRelationNode);
        Assert.Equal(["Same meaning: 'Countermeasures Management Switch - Fwd' (2)", "Other meaning here (own title shown) (1)"],
            node.Aircraft.Visible.Where(i => i.HasGroupHeader).Select(i => i.GroupHeader).ToArray());

        // Apply loads the relation again: the action's aircraft stay open.
        node.Aircraft.Items.Single(i => i.Plane == "F-15E").IsChecked = false;
        await vm.ApplyNodeAircraftCommand.ExecuteAsync(node);
        await RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Any(n => n.Id == "d3014pnil" && n.HasAircraft && !ReferenceEquals(n, node)));
        RelationNodeItem reloaded = vm.RelationNodes.Single(n => n.Id == "d3014pnil");
        Assert.True(reloaded.Aircraft!.IsExpanded);
        vm.ToggleNodeAircraftCommand.Execute(reloaded);
        Assert.False(reloaded.Aircraft.IsExpanded);

        // Another game's action: no aircraft, nothing opens.
        RelationNodeItem other = new() { Game = "WarThunder", Id = "ID_FLARES" };
        vm.ToggleNodeAircraftCommand.Execute(other);
        Assert.Null(other.Aircraft);
    }

    // Other games' actions show their title in the game, not the id ("elevator" is War Thunder's "Pitch axis").
    [Fact]
    public void OpenRelation_OtherGamesShowTheirTitleNotTheId()
    {
        MainWindowViewModel vm = new(new PerAircraftFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
        RelationNodeItem wt = new() { Game = "WarThunder", Id = "elevator", TitlesSummary = "elevator", TitlesDisplay = "elevator" };
        RelationNodeItem msfs = new() { Game = "MSFS2024", Id = "KEY_AXIS_ELEVATOR_SET", TitlesSummary = "KEY_AXIS_ELEVATOR_SET" };
        vm.RelationNodes.Add(wt);
        vm.RelationNodes.Add(msfs);
        vm.ApplyNodeAircraft("{\"Nodes\":[{\"Game\":\"WarThunder\",\"Id\":\"elevator\",\"Title\":\"Pitch axis\",\"Aircraft\":[]}," +
                             "{\"Game\":\"MSFS2024\",\"Id\":\"KEY_AXIS_ELEVATOR_SET\",\"Title\":\"AXIS ELEVATOR SET\",\"Aircraft\":[]}]}");
        Assert.Equal("Pitch axis", wt.TitlesSummary);
        Assert.Equal("Pitch axis (elevator)", wt.TitlesDisplay);
        Assert.Equal("AXIS ELEVATOR SET", msfs.TitlesSummary);
        Assert.Null(wt.Aircraft);
        vm.StopGameConfigWatcher();
    }

    // A search row that is the function's main control says so, and a title that does not name the function shows it.
    [Fact]
    public void SearchRow_MainControlAndFunctionInTheTitle()
    {
        BuilderSearchResultItem elevator = new("MSFS2024", "KEY_AXIS_ELEVATOR_SET", "AXIS ELEVATOR SET", "SURFACES", true, ["MSFS2024"], [], "Function 'Pitch axis' ('elevator')")
        {
            Match = "synonym",
            Primary = true,
            Display = "AXIS ELEVATOR SET — Pitch axis (elevator)"
        };
        Assert.Equal("main control", elevator.MatchLabel);
        Assert.True(elevator.IsStrongMatch);
        Assert.Equal("AXIS ELEVATOR SET — Pitch axis (elevator)", elevator.DisplayTitle);
        // The relation is named after the game's own title, the row's key stays the title.
        Assert.Equal("MSFS2024|KEY_AXIS_ELEVATOR_SET|AXIS ELEVATOR SET", elevator.RowKey);

        BuilderSearchResultItem burner = new("MSFS2024", "KEY_BURNER_PITCH_INC", "BURNER PITCH INC", "", false, ["MSFS2024"], [], "Title match (97%)") { Match = "words" };
        Assert.Equal("word match", burner.MatchLabel);
        Assert.Equal("BURNER PITCH INC", burner.DisplayTitle);
    }

    [Fact]
    public async Task SearchRow_ReadsPrimaryAndDisplayFromTheEngine()
    {
        PerAircraftFakeEngine fake = new() { PitchPrimary = true };
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        vm.SelectedRelation = null;
        vm.BuilderSearchText = "pitch";
        await vm.SearchBuilderActionsAsync();
        BuilderSearchResultItem row = vm.BuilderSearchResults.Single(r => r.Id == "a2001cdnil");
        Assert.True(row.Primary);
        Assert.Equal("main control", row.MatchLabel);
        Assert.Equal("Pitch — Pitch axis", row.DisplayTitle);
        Assert.Equal("Pitch", row.Title);
    }
}

// The relation builder's fake engine with the per-aircraft answers: a search row whose id is "Collective" in the
// UH-1H, the aircraft of the Flare relation's action (F-15E: the id is another switch there), the profile check.
[SupportedOSPlatform("windows")]
internal sealed class PerAircraftFakeEngine : IEngineCliClient
{
    private readonly BuilderFakeEngine inner = new();

    public List<IReadOnlyList<string>> Calls { get; } = [];

    // How many DCS actions the search has in all (the first answer shows one).
    public int DcsTotal { get; set; } = 1;

    // The pitch row comes as the main control with a display title (relations search "Primary", "Display").
    public bool PitchPrimary { get; set; }

    public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        lock (Calls)
            Calls.Add(arguments.ToArray());
        string? output = Output(arguments);
        if (output == null)
            return inner.RunAsync(engineExePath, arguments, cancellationToken);
        return Task.FromResult(new EngineCommandResult
        {
            ExitCode = 0,
            Duration = TimeSpan.FromMilliseconds(1),
            StandardOutput = output,
            StandardError = string.Empty,
            Arguments = arguments.ToArray()
        });
    }

    private const string PitchRow =
        "{\"Game\":\"DCS\",\"Id\":\"a2001cdnil\",\"Title\":\"Pitch\",\"Category\":\"Flight Control\",\"IsAxis\":true,\"Planes\":[\"F-15C\",\"F-16C_50\",\"FA-18C_hornet\"]," +
        "\"Score\":1330,\"Why\":\"Function 'Pitch axis'\",\"Match\":\"exact\",\"Function\":\"pitch\",\"InRelations\":[],\"Near\":0,\"SameIdDifferent\":1," +
        "\"MeaningNote\":\"same id, different meaning in 1 aircraft (left out)\",\"Variants\":[" +
        "{\"Title\":\"Pitch\",\"Category\":\"Flight Control\",\"Match\":\"same\",\"Note\":\"\",\"Included\":true,\"Planes\":[\"F-15C\",\"F-16C_50\",\"FA-18C_hornet\"]}," +
        "{\"Title\":\"Collective\",\"Category\":\"Flight Control\",\"Match\":\"differs\",\"Note\":\"\",\"Included\":false,\"Planes\":[\"UH-1H\"]}]}";

    private static string Row(string id, string title) =>
        "{\"Game\":\"DCS\",\"Id\":\"" + id + "\",\"Title\":\"" + title + "\",\"Category\":\"\",\"IsAxis\":true,\"Planes\":[\"F-15C\"],\"Score\":900,\"Why\":\"\",\"Match\":\"function\",\"Function\":\"pitch\",\"InRelations\":[]}";

    private string? Output(IReadOnlyList<string> args)
    {
        if (AxisTuningTests.Seq(args, "relations", "search"))
        {
            bool all = args.Contains("--game");
            string pitch = PitchPrimary ? PitchRow.Replace("\"Match\":\"exact\",", "\"Match\":\"exact\",\"Primary\":true,\"Display\":\"Pitch — Pitch axis\",", StringComparison.Ordinal) : PitchRow;
            string rows = all ? pitch + "," + Row("a3002cd19", "Horizon pitch") + "," + Row("a3015cd52", "AFCS Pitch Axis") : pitch;
            int count = all ? 3 : 1;
            return "{\"Query\":\"pitch\",\"DidYouMean\":\"\",\"Functions\":[\"Pitch\"],\"FunctionGroups\":[{\"Function\":\"pitch\",\"Name\":\"Pitch\",\"Count\":" + count + "}]," +
                   "\"Groups\":[{\"Game\":\"DCS\",\"Count\":" + count + ",\"Total\":" + (all ? count : DcsTotal) + "}],\"Results\":[" + rows + "],\"Total\":" + DcsTotal + "}";
        }
        if (AxisTuningTests.Seq(args, "relations", "aircraft") && !args.Contains("set"))
        {
            return "{\"Relation\":\"Flare\",\"Nodes\":[{\"Relation\":\"Flare\",\"Game\":\"DCS\",\"Id\":\"d3014pnil\",\"Title\":\"Countermeasures Management Switch - Fwd\",\"Category\":\"Stick\"," +
                   "\"Active\":2,\"Available\":3,\"Differs\":1,\"Excluded\":0,\"Mixed\":true,\"Badge\":\"2 of 3 aircraft\",\"Aircraft\":[" +
                   "{\"Plane\":\"F-16C_50\",\"Title\":\"Countermeasures Management Switch - Fwd\",\"Category\":\"Stick\",\"State\":\"on\",\"Match\":\"same\",\"Note\":\"\"}," +
                   "{\"Plane\":\"F-14\",\"Title\":\"Countermeasures Management Switch - Fwd\",\"Category\":\"Stick\",\"State\":\"absent\",\"Match\":\"same\",\"Note\":\"\"}," +
                   "{\"Plane\":\"F-15E\",\"Title\":\"Canopy Open\",\"Category\":\"Cockpit\",\"State\":\"on\",\"Match\":\"differs\",\"Note\":\"\"}]}]}";
        }
        if (AxisTuningTests.Seq(args, "relations", "check-aircraft"))
        {
            bool apply = args.Contains("--apply");
            return "{\"Relations\":1,\"Nodes\":1,\"AffectedNodes\":1,\"AffectedRelations\":1,\"AircraftToDrop\":1,\"AircraftSameFunction\":0,\"Applied\":" + (apply ? "true" : "false") +
                   ",\"Dropped\":" + (apply ? 1 : 0) + ",\"Found\":[{\"Relation\":\"Pitch\",\"Game\":\"DCS\",\"Id\":\"a2001cdnil\",\"Title\":\"Pitch\",\"Category\":\"\",\"Active\":4,\"Available\":4," +
                   "\"Meanings\":[{\"Title\":\"Collective\",\"Category\":\"Flight Control\",\"Match\":\"differs\",\"Note\":\"\",\"Included\":true,\"Planes\":[\"UH-1H\"]}],\"Drop\":[\"UH-1H\"],\"SameFunction\":[]}]}";
        }
        return null;
    }
}
