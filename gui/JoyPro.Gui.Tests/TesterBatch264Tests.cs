using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The 2.6.3 tester report (imacken): the relation "Stick Red Button" has "Bomb Release Button" of the F-4E pilot and WSO;
// Add actions, search "Bomb Release Button", Same meaning off, Buttons:
// 1. the callout said no action's name has every word, right above the two that do: they were only left out because they
//    are in the open relation already;
// 2. a row with all its aircraft in the relation had an empty aircraft line (the line named only the free aircraft), so
//    the two rows looked the same;
// 3. their aircraft button said "0 of 0 free aircraft";
// 4. the status line counted the hidden rows: "11 actions in 1 game + 7 already in this relation" with nothing to add and
//    2 listed.
// Real engine and the development game lists, the aircraft limited to the F-4E's and others without the words.
// Pictures and the texts with JOYPRO_264_RENDER_OUT=<folder>.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class TesterBatch264RealEngineTests
{
    private const string Relation = "Stick Red Button";
    private const string Query = "Bomb Release Button";
    private const string Pilot = "d3002pnilu3002cd27vd1vpnilvu0";
    private const string Wso = "d3038pnilu3038cd27vd1vpnilvu0";
    private const string F16WeaponRelease = "d3022pnilu3022cd16vd1vpnilvu0";
    private static readonly string[] Planes = ["F-4E-45MC", "F-4E-45MC_WSO", "SpitfireLFMkIX", "F-16C_50", "FA-18C_hornet"];

    private static async Task<(MainWindowViewModel Vm, string Folder)> OpenAsync(string engine, params string[] nodes)
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "batch264-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        DataDirEngineClient client = new(Path.Combine(folder, "data"));
        string profile = Path.Combine(folder, "stick.pr0file");
        List<string> add = ["--profile", profile, "--status", "false", "relations", "add", Relation];
        foreach (string node in nodes)
            add.AddRange(["--node", node]);
        EngineCommandResult made = await client.RunAsync(engine, add);
        Assert.True(made.ExitCode == 0, made.StandardError);
        MainWindowViewModel vm = new(client, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        vm.CurrentStage = UiStage.Relations;
        vm.SelectedRelation = vm.Relations.Single(r => r.Name == Relation);
        await SameMeaningRealEngineTests.WaitLongAsync(() => vm.BuilderRelationName == Relation && !vm.IsBusy);
        await SameMeaningRealEngineTests.WaitLongAsync(() => vm.RelationNodes.Count == nodes.Length && !vm.IsBuilderSuggestLoading);
        vm.ShowBuilderFindCommand.Execute(null);
        foreach (string plane in Planes)
            await vm.AddBuilderSearchPlaneCommand.ExecuteAsync(plane);
        await SameMeaningRealEngineTests.SearchAsync(vm, Query);
        return (vm, folder);
    }

    private static void Done(MainWindowViewModel vm, string folder)
    {
        vm.StopGameConfigWatcher();
        EngineServerPool.Recycle("batch 264 test done");
        try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly string[] BothF4 = ["DCS:" + Pilot + ":F-4E-45MC", "DCS:" + Wso + ":F-4E-45MC_WSO"];

    [Fact]
    public async Task AllNameMatchesInTheRelation_TheListSaysSo_AndTheRowsNameTheirAircraft()
    {
        string? engine = SameMeaningRealEngineTests.Engine();
        if (engine == null)
            return;
        (MainWindowViewModel vm, string folder) = await OpenAsync(engine, BothF4);
        try
        {
            Assert.Equal("button", vm.BuilderEffectiveKind);
            // Same meaning on: the four actions found by what they do can be added, both name matches are in the relation.
            Assert.Equal("4 actions in 1 game + 2 already in this relation · understood as Weapon release (pickle)", vm.BuilderSearchStatus);
            Assert.False(vm.ShowBuilderNoNameMatches);

            vm.BuilderShowSameMeaning = false;
            // Nothing to add is listed, and the line does not say otherwise.
            Assert.Equal("No new actions + 2 already in this relation · understood as Weapon release (pickle) · 4 with the same meaning hidden (Same meaning is off)", vm.BuilderSearchStatus);
            Assert.True(vm.ShowBuilderNoNameMatches);
            Assert.Equal("No new actions with every word of \"Bomb Release Button\": the 2 found are already in this relation (see below). 4 more found by what they do are hidden because Same meaning is off.", vm.BuilderNoNameMatchesText);
            Assert.True(vm.CanShowBuilderSameMeaning);

            BuilderSearchGroupItem group = Assert.Single(vm.BuilderSearchRows.OfType<BuilderSearchGroupItem>());
            Assert.True(group.IsInRelationGroup);
            List<BuilderSearchResultItem> rows = vm.BuilderSearchRows.OfType<BuilderSearchResultItem>().ToList();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r =>
            {
                Assert.Equal(Query, r.Title);
                Assert.True(r.IsInOpenRelation);
                Assert.Equal("already in this relation", r.InOtherRelationsText);
                Assert.Empty(r.SelectedPlanes);
                Assert.False(r.Aircraft!.CanTick);
                Assert.DoesNotContain("free aircraft", r.Aircraft.ExpandLabel);
            });
            // Nothing is free: no "0 of 0 free aircraft". The WSO's id exists only for the WSO; the pilot's id has two more
            // aircraft where it means something else.
            Assert.Equal("1 of 1 aircraft in this relation", rows.Single(r => r.Id == Wso).Aircraft!.ExpandLabel);
            Assert.Equal("1 of 3 aircraft in this relation · 2 aircraft use this command for something else (show)", rows.Single(r => r.Id == Pilot).Aircraft!.ExpandLabel);
            // Two rows of one title: the aircraft line tells them apart.
            Assert.Equal(["F-4E-45MC", "F-4E-45MC_WSO"], rows.Select(r => r.PlanesText).OrderBy(p => p, StringComparer.Ordinal).ToArray());

            // The aircraft list of such a row opens, says the same and only shows: a tick there could add nothing.
            BuilderSearchResultItem pilot = rows.Single(r => r.Id == Pilot);
            pilot.Aircraft!.ToggleExpandedCommand.Execute(null);
            Assert.True(pilot.Aircraft.IsExpanded);
            Assert.Equal("Hide aircraft", pilot.Aircraft.ExpandLabel);
            Assert.Equal(3, pilot.Aircraft.Visible.Count);
            AircraftChoiceItem own = pilot.Aircraft.Visible.Single(i => i.Plane == "F-4E-45MC");
            Assert.Equal("already in this relation", own.MarkerText);
            Assert.All(pilot.Aircraft.Visible, i => Assert.False(i.CanChange));
            pilot.Aircraft.SelectAll();
            Assert.All(pilot.Aircraft.Visible, i => Assert.False(i.IsChecked));
            Assert.False(vm.HasBuilderChecked);
            // Its filter box still filters.
            pilot.Aircraft.FilterText = "F-4E";
            Assert.Equal("F-4E-45MC", Assert.Single(pilot.Aircraft.Visible).Plane);
            pilot.Aircraft.FilterText = string.Empty;
            pilot.Aircraft.ToggleExpandedCommand.Execute(null);
            Assert.StartsWith("1 of 3 aircraft in this relation", pilot.Aircraft.ExpandLabel);

            // One click on the callout's button and the four are back.
            vm.ShowBuilderSameMeaningCommand.Execute(null);
            Assert.False(vm.ShowBuilderNoNameMatches);
            Assert.Equal(4, vm.BuilderSearchRows.OfType<BuilderSearchResultItem>().Count(r => !r.IsInOpenRelation));
            Assert.Equal("4 actions in 1 game + 2 already in this relation · understood as Weapon release (pickle)", vm.BuilderSearchStatus);
        }
        finally
        {
            Done(vm, folder);
        }
    }

    // The tester's "+ 7 already in this relation" with 2 listed: the relation also has an action that only has the meaning.
    [Fact]
    public async Task StatusLine_CountsWhatIsListed_AndNamesWhatIsHidden()
    {
        string? engine = SameMeaningRealEngineTests.Engine();
        if (engine == null)
            return;
        (MainWindowViewModel vm, string folder) = await OpenAsync(engine, [.. BothF4, "DCS:" + F16WeaponRelease + ":F-16C_50"]);
        try
        {
            Assert.Equal("3 actions in 1 game + 3 already in this relation · understood as Weapon release (pickle)", vm.BuilderSearchStatus);
            vm.BuilderShowSameMeaning = false;
            Assert.Equal("No new actions + 2 already in this relation · understood as Weapon release (pickle) · 3 with the same meaning hidden (Same meaning is off) · 1 more in this relation hidden", vm.BuilderSearchStatus);
            Assert.Equal(2, vm.BuilderSearchRows.OfType<BuilderSearchResultItem>().Count());
            Assert.Equal("No new actions with every word of \"Bomb Release Button\": the 2 found are already in this relation (see below). 3 more found by what they do are hidden because Same meaning is off.", vm.BuilderNoNameMatchesText);
            vm.BuilderShowSameMeaning = true;
            Assert.Equal(6, vm.BuilderSearchRows.OfType<BuilderSearchResultItem>().Count());
        }
        finally
        {
            Done(vm, folder);
        }
    }

    // One name match free (the pilot's), the WSO's in the relation: an ordinary list, no callout, the count is what is listed.
    [Fact]
    public async Task NameMatchStillFree_NoCallout_AndItsCountIsListed()
    {
        string? engine = SameMeaningRealEngineTests.Engine();
        if (engine == null)
            return;
        (MainWindowViewModel vm, string folder) = await OpenAsync(engine, "DCS:" + Wso + ":F-4E-45MC_WSO");
        try
        {
            vm.BuilderShowSameMeaning = false;
            Assert.False(vm.ShowBuilderNoNameMatches);
            Assert.Equal("1 action in 1 game + 1 already in this relation · understood as Weapon release (pickle) · 4 with the same meaning hidden (Same meaning is off)", vm.BuilderSearchStatus);
            BuilderSearchResultItem free = vm.BuilderSearchRows.OfType<BuilderSearchResultItem>().Single(r => !r.IsInOpenRelation);
            Assert.Equal(Pilot, free.Id);
            Assert.Equal("F-4E-45MC", free.PlanesText);
            // Its id has aircraft of another meaning too: an ordinary row's count, nothing about the relation.
            Assert.Equal("1 of 3 aircraft", free.Aircraft!.Badge);
            Assert.True(free.Aircraft.CanTick);
        }
        finally
        {
            Done(vm, folder);
        }
    }

    // The search before and after, light and dark, with the aircraft list of a row opened; the texts in texts.txt.
    [Fact]
    public void Render()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_264_RENDER_OUT");
        string? engine = SameMeaningRealEngineTests.Engine();
        if (string.IsNullOrWhiteSpace(outDir) || engine == null)
            return;
        Directory.CreateDirectory(outDir);
        StringBuilder texts = new();
        AvaloniaRenderThread.Run(() =>
        {
            (MainWindowViewModel vm, string folder) = AvaloniaRenderThread.Wait(OpenAsync(engine, BothF4));
            try
            {
                void Shot(string name, int height = 1000)
                {
                    texts.AppendLine("--- " + name);
                    texts.AppendLine("status: " + vm.BuilderSearchStatus);
                    texts.AppendLine("callout (" + vm.ShowBuilderNoNameMatches + "): " + vm.BuilderNoNameMatchesText);
                    foreach (BuilderSearchResultItem row in vm.BuilderSearchRows.OfType<BuilderSearchResultItem>())
                        texts.AppendLine("row: " + row.Title + " | line: '" + row.PlanesText + "' | button: '" + row.Aircraft?.ExpandLabel + "' | note: '" + row.InOtherRelationsText + "'");
                    foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                    {
                        Application.Current!.RequestedThemeVariant = variant;
                        UiDesignRenderTests.RenderWindow(vm, 1600, height, Path.Combine(outDir, name + "-" + theme + ".png"));
                    }
                }

                vm.BuilderShowSameMeaning = false;
                Shot("bomb-release-same-meaning-off");
                vm.BuilderSearchRows.OfType<BuilderSearchResultItem>().First().Aircraft!.IsExpanded = true;
                Shot("bomb-release-aircraft-list-open");
                vm.BuilderSearchRows.OfType<BuilderSearchResultItem>().First().Aircraft!.IsExpanded = false;
                vm.BuilderShowSameMeaning = true;
                Shot("bomb-release-same-meaning-on", 1400);
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
                Done(vm, folder);
                File.WriteAllText(Path.Combine(outDir, "texts.txt"), texts.ToString());
            }
        });
    }
}

// The rows' lines without an engine: every kind of row, and a list where nothing is free.
[SupportedOSPlatform("windows")]
public sealed class TesterBatch264RowTests
{
    private static AircraftSelector Selector(string title, params (string Plane, string Match)[] planes) =>
        new(title, planes.Select(p => new AircraftChoiceItem(p.Plane, p.Match == "same" ? title : "Other", "View", p.Match, string.Empty, p.Match == "same")));

    private static BuilderSearchResultItem Dcs(string title, AircraftSelector selector) =>
        new("DCS", "d1", title, "View", false, selector.Items.Select(i => i.Plane).ToList(), [], string.Empty) { Aircraft = selector };

    [Fact]
    public void WhollyIn_NamesItsAircraft_PartlyIn_NamesOnlyTheFreeOnes()
    {
        BuilderSearchResultItem row = Dcs("View", Selector("View", ("AJS37", "same"), ("F-14", "same"), ("M-2000C", "same")));
        Assert.Equal("AJS37, F-14, M-2000C", row.PlanesText);
        Assert.Equal("3 of 3 aircraft", row.Aircraft!.Badge);

        // Partly in: the line and what is added are the free one, the button says what is left to add.
        Assert.False(row.MarkInOpenRelation(new HashSet<string>(["AJS37", "F-14"])));
        Assert.Equal("M-2000C", row.PlanesText);
        Assert.Equal("1 of 1 free aircraft", row.Aircraft.Badge);
        Assert.Equal(["M-2000C"], row.SelectedPlanes);

        // Wholly in: the line names them all again, nothing is offered.
        Assert.True(row.MarkInOpenRelation(new HashSet<string>(["AJS37", "F-14", "M-2000C"])));
        Assert.Equal("AJS37, F-14, M-2000C", row.PlanesText);
        Assert.Equal("3 of 3 aircraft in this relation", row.Aircraft.Badge);
        Assert.Empty(row.SelectedPlanes);

        // Another relation open: the row's own proposal is back.
        Assert.False(row.MarkInOpenRelation(null));
        Assert.Equal("AJS37, F-14, M-2000C", row.PlanesText);
        Assert.Equal("3 of 3 aircraft", row.Aircraft.Badge);
    }

    [Fact]
    public void WhollyIn_ManyAircraft_TheLineIsShortened_TheTooltipHasThemAll()
    {
        string[] planes = ["AJS37", "F-14", "F-16C_50", "F-4E-45MC", "M-2000C"];
        BuilderSearchResultItem row = Dcs("View", Selector("View", planes.Select(p => (p, "same")).ToArray()));
        Assert.True(row.MarkInOpenRelation(new HashSet<string>(planes)));
        Assert.Equal("AJS37, F-14 +3 more", row.PlanesText);
        Assert.Equal(string.Join(", ", planes), row.PlanesTooltip);
    }

    // Aircraft where the id means something else are no part of the row: it is wholly in with its own, and the button counts
    // what the relation has out of the list it opens ("0 of 1 free aircraft" said there was something to add).
    [Fact]
    public void WhollyIn_WithAircraftOfAnotherMeaning_CountsWhatIsInTheRelation()
    {
        BuilderSearchResultItem row = Dcs("View", Selector("View", ("AJS37", "same"), ("F-14", "same"), ("Ka-50 III", "differs")));
        Assert.True(row.MarkInOpenRelation(new HashSet<string>(["AJS37", "F-14"])));
        Assert.Equal("AJS37, F-14", row.PlanesText);
        Assert.Equal("2 of 3 aircraft in this relation", row.Aircraft!.Badge);
        Assert.Contains("2 of 3 aircraft in this relation · 1 aircraft uses this command for something else", row.Aircraft.ExpandLabel);

        // Its list only shows: the row is not addable, so a tick on Ka-50 III would add nothing.
        AircraftChoiceItem other = row.Aircraft.Items.Single(i => i.Plane == "Ka-50 III");
        Assert.False(row.Aircraft.CanTick);
        Assert.False(other.CanChange);
        row.Aircraft.SelectAll();
        row.Aircraft.Invert();
        Assert.False(other.IsChecked);

        // One of the row's own aircraft free again: it can be ticked again, the row is addable.
        Assert.False(row.MarkInOpenRelation(new HashSet<string>(["AJS37"])));
        Assert.True(row.Aircraft.CanTick);
        Assert.True(other.CanChange);
        Assert.Equal("1 of 2 free aircraft", row.Aircraft.Badge);
        Assert.Equal(["F-14"], row.SelectedPlanes);
    }

    // Outside DCS the line is the category, with or without the relation, and there is no aircraft button.
    [Fact]
    public void OtherGames_ShowTheirCategory()
    {
        BuilderSearchResultItem row = new("FalconBMS", "SimPickle", "Pickle", "Weapons", false, ["FalconBMS"], [], string.Empty);
        Assert.Equal("Weapons", row.PlanesText);
        Assert.False(row.HasAircraft);
        Assert.True(row.MarkInOpenRelation(new HashSet<string>(["FalconBMS"])));
        Assert.Equal("Weapons", row.PlanesText);
        Assert.False(row.HasAircraft);
    }

    // A DCS row without a selector (an older engine's answer): its aircraft from the row.
    [Fact]
    public void WhollyIn_WithoutASelector_StillNamesItsAircraft()
    {
        BuilderSearchResultItem row = new("DCS", "d1", "View", "View", false, ["AJS37"], [], string.Empty);
        Assert.True(row.MarkInOpenRelation(new HashSet<string>(["AJS37"])));
        Assert.Equal("AJS37", row.PlanesText);
    }
}

// The callout and the status line with the fake engine: the cases the real lists have no fixture for.
[SupportedOSPlatform("windows")]
public sealed class TesterBatch264ListTests
{
    private static string Hit(string id, string title, string section, string plane) =>
        "{\"Game\":\"DCS\",\"Id\":\"" + id + "\",\"Title\":\"" + title + "\",\"Category\":\"Weapons\",\"IsAxis\":false,\"Planes\":[\"" + plane + "\"]," +
        "\"Variants\":[{\"Title\":\"" + title + "\",\"Category\":\"Weapons\",\"Match\":\"same\",\"Note\":\"\",\"Included\":true,\"Planes\":[\"" + plane + "\"]}]," +
        "\"Score\":900,\"Literal\":" + (section == "name" ? "true" : "false") + ",\"Why\":\"\",\"Match\":\"" + (section == "name" ? "function" : "synonym") + "\",\"Function\":\"pickle\"," +
        "\"Display\":\"" + title + "\",\"InRelations\":[],\"Recommended\":false,\"Variant\":\"\",\"Section\":\"" + section + "\"}";

    private static string Result(params string[] hits) =>
        "{\"Query\":\"q\",\"Total\":" + hits.Length + ",\"Functions\":[\"Weapon release\"],\"Resolved\":[],\"Results\":[" + string.Join(",", hits) + "],\"Groups\":[{\"Game\":\"DCS\",\"Count\":" + hits.Length + ",\"Total\":" + hits.Length + "}],\"FunctionGroups\":[],\"Warnings\":[],\"Errors\":[]}";

    private static async Task<MainWindowViewModel> SearchedAsync(string file, string node, string relationNodePlane, params string[] hits)
    {
        BuilderFakeEngine fake = new();
        fake.AddRelation("Stick", false, string.Empty, ("DCS", node, relationNodePlane));
        fake.SearchOverride = _ => Result(hits);
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, file);
        await RelationBuilderTests.OpenAsync(vm, "Stick");
        vm.BuilderSearchText = "pickle it";
        await RelationBuilderTests.WaitForAsync(() => vm.BuilderLastSearchedQueryForTests == "pickle it" && !vm.IsBuilderSearching);
        vm.BuilderShowSameMeaning = false;
        return vm;
    }

    // The relation has the only name match and nothing else was found: nothing is hidden, so no callout; the status line says it.
    [Fact]
    public async Task OnlyTheRelationsOwnNameMatch_NoHiddenRows_NoCallout()
    {
        MainWindowViewModel vm = await SearchedAsync("batch264-only.pr0file", "d1", "AJS37", Hit("d1", "Pickle It", "name", "AJS37"));
        Assert.False(vm.ShowBuilderNoNameMatches);
        Assert.False(vm.CanShowBuilderSameMeaning);
        Assert.Equal("No new actions: the 1 found is already in this relation (see below) · understood as Weapon release", vm.BuilderSearchStatus);
        // The same with the switch on (it said "0 actions in 0 games").
        vm.BuilderShowSameMeaning = true;
        Assert.False(vm.ShowBuilderNoNameMatches);
        Assert.Equal("No new actions: the 1 found is already in this relation (see below) · understood as Weapon release", vm.BuilderSearchStatus);
    }

    // No name match anywhere the search looked: only the new rows count as hidden here, the status line names the others.
    [Fact]
    public async Task NoNameMatchAnywhere_SaysNoActionFound()
    {
        MainWindowViewModel vm = await SearchedAsync("batch264-none.pr0file", "d1", "AJS37", Hit("d1", "Weapon Release", "function", "AJS37"), Hit("d2", "Drop", "function", "F-14"));
        Assert.True(vm.ShowBuilderNoNameMatches);
        Assert.Equal("No action found whose name has every word of \"pickle it\". 1 found by what it does is hidden because Same meaning is off.", vm.BuilderNoNameMatchesText);
        Assert.True(vm.CanShowBuilderSameMeaning);
        Assert.Equal("No new actions · understood as Weapon release · 1 with the same meaning hidden (Same meaning is off) · 1 more in this relation hidden", vm.BuilderSearchStatus);
    }

    // The switch off with only rows of the relation hidden: nothing new is hidden, so no callout; the status line names them.
    [Fact]
    public async Task NameMatchInTheRelation_HiddenOnesAlsoInTheRelation_NoCallout()
    {
        BuilderFakeEngine fake = new();
        fake.AddRelation("Stick", false, string.Empty, ("DCS", "d1", "AJS37"), ("DCS", "d2", "F-14"));
        fake.SearchOverride = _ => Result(Hit("d1", "Pickle It", "name", "AJS37"), Hit("d2", "Drop", "function", "F-14"));
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "batch264-both.pr0file");
        await RelationBuilderTests.OpenAsync(vm, "Stick");
        vm.BuilderSearchText = "pickle it";
        await RelationBuilderTests.WaitForAsync(() => vm.BuilderLastSearchedQueryForTests == "pickle it" && !vm.IsBuilderSearching);
        vm.BuilderShowSameMeaning = false;
        Assert.False(vm.ShowBuilderNoNameMatches);
        Assert.Equal("No new actions: the 1 found is already in this relation (see below) · understood as Weapon release · 1 more in this relation hidden", vm.BuilderSearchStatus);
    }

    // The switch off with name matches to add, in two games' worth of rows: the count and the games are the listed ones.
    [Fact]
    public async Task SameMeaningOff_GamesAndCount_AreTheListedOnes()
    {
        MainWindowViewModel vm = await SearchedAsync("batch264-count.pr0file", "d9", "AJS37",
            Hit("d1", "Pickle It", "name", "F-14"), Hit("d2", "Pickle It Too", "name", "M-2000C"), Hit("d3", "Drop", "function", "F-16C_50"));
        Assert.False(vm.ShowBuilderNoNameMatches);
        Assert.Equal("2 actions in 1 game · understood as Weapon release · 1 with the same meaning hidden (Same meaning is off)", vm.BuilderSearchStatus);
        vm.BuilderShowSameMeaning = true;
        Assert.Equal("3 actions in 1 game · understood as Weapon release", vm.BuilderSearchStatus);
    }
}
