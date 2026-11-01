using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Add actions list after the 2.5.0 tester reports (docs/relation-builder.md "Make a relation, step by step"):
// - an action switched on in other relations has one tag: none, "in 'X'" (a click opens it) or "in n relations" (a click
//   lists them, a name opens that relation; "Back to '<relation>'" returns with the search as it was);
// - an action already in the open relation is shown greyed in a group of its own at the bottom, not tickable, and the
//   header counts only what can be added ("+ 1 already in this relation");
// - "Replace 'X' on <button> with..." from a Check card: the relation opens with the search on the command's name and its
//   aircraft as the filter; the ticked action takes the old one's place in one engine call; "Back to the check".
[SupportedOSPlatform("windows")]
public sealed class SearchTagsTests
{
    private static async Task<MainWindowViewModel> SearchFlareAsync(BuilderFakeEngine fake)
    {
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "search-tags.pr0file");
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        vm.ShowBuilderFindCommand.Execute(null);
        vm.BuilderSearchText = "flare";
        await RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSearching && vm.BuilderSearchResults.Count == 5 && vm.BuilderSearchStatus.Length > 0);
        return vm;
    }

    [Fact]
    public async Task OtherRelations_NoneOneSeveral_OneTag()
    {
        BuilderFakeEngine fake = new() { SeveralRelations = "Chaff|Hornet dispense" };
        MainWindowViewModel vm = await SearchFlareAsync(fake);

        // None: no tag.
        BuilderSearchResultItem none = vm.BuilderSearchResults.Single(r => r.Id == "SimCMSUp");
        Assert.False(none.HasOtherRelations);
        Assert.Equal(string.Empty, none.OtherRelationsTag);
        // One: the tag names it.
        BuilderSearchResultItem one = vm.BuilderSearchResults.Single(r => r.Id == "d3001");
        Assert.True(one.HasOtherRelations);
        Assert.False(one.HasSeveralOtherRelations);
        Assert.Equal("in 'Chaff'", one.OtherRelationsTag);
        // Several: the count; a click lists the names.
        BuilderSearchResultItem several = vm.BuilderSearchResults.Single(r => r.Id == "ID_FLARES");
        Assert.True(several.HasSeveralOtherRelations);
        Assert.Equal("in 2 relations", several.OtherRelationsTag);
        Assert.False(several.ShowOtherRelationsList);
        await vm.OpenOtherRelationsTagCommand.ExecuteAsync(several);
        Assert.True(several.ShowOtherRelationsList);
        Assert.Equal(["Chaff", "Hornet dispense"], several.OtherRelations);
        Assert.Equal("Flare", vm.SelectedRelation!.Name);

        // A name opens that relation (the search stays); "Back to 'Flare'" returns with the search as it was.
        await vm.OpenRelationFromSearchCommand.ExecuteAsync("Hornet dispense");
        Assert.Equal("Hornet dispense", vm.SelectedRelation!.Name);
        Assert.True(vm.CanReturnToCheck);
        Assert.Equal("Back to 'Flare'", vm.ReturnToCheckLabel);
        await RelationBuilderTests.WaitForAsync(() => !vm.IsBusy && !vm.IsBuilderSearching);
        await vm.ReturnToCheckCommand.ExecuteAsync(null);
        await RelationBuilderTests.WaitForAsync(() => vm.SelectedRelation?.Name == "Flare" && !vm.IsBusy);
        Assert.Equal("flare", vm.BuilderSearchText);
        Assert.False(vm.CanReturnToCheck);

        // One other relation: the tag opens it at once.
        await RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSearching && vm.BuilderSearchResults.Count == 5);
        await vm.OpenOtherRelationsTagCommand.ExecuteAsync(vm.BuilderSearchResults.Single(r => r.Id == "d3001"));
        Assert.Equal("Chaff", vm.SelectedRelation!.Name);
        await RelationBuilderTests.SettleAllAsync();
    }

    // "Suggested for this relation" has the same tag: one relation opens at once, several are listed and a name opens it;
    // "Back to 'Flare'" returns.
    [Fact]
    public async Task Suggestions_HaveTheSameTag()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "search-tags-suggest.pr0file");
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        BuilderSuggestionItem none = vm.BuilderSuggestions.Single(s => s.Game == "FalconBMS");
        Assert.False(none.HasOtherRelations);
        Assert.Equal(string.Empty, none.OtherRelationsTag);
        BuilderSuggestionItem one = vm.BuilderSuggestions.Single(s => s.Id == "d3033");
        Assert.True(one.HasOtherRelations);
        Assert.Equal("in 'Hornet dispense'", one.OtherRelationsTag);
        await vm.OpenOtherRelationsTagCommand.ExecuteAsync(one);
        Assert.Equal("Hornet dispense", vm.SelectedRelation!.Name);
        Assert.Equal("Back to 'Flare'", vm.ReturnToCheckLabel);
        await RelationBuilderTests.WaitForAsync(() => !vm.IsBusy);
        await vm.ReturnToCheckCommand.ExecuteAsync(null);
        await RelationBuilderTests.WaitForAsync(() => vm.SelectedRelation?.Name == "Flare" && !vm.IsBusy && !vm.IsBuilderSuggestLoading);

        fake.HornetRelations = "Hornet dispense|Chaff";
        await vm.LoadBuilderSuggestionsAsync("Flare");
        await RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSuggestLoading && vm.BuilderSuggestions.Any(s => s.Id == "d3033"));
        BuilderSuggestionItem several = vm.BuilderSuggestions.Single(s => s.Id == "d3033");
        Assert.Equal("in 2 relations", several.OtherRelationsTag);
        await vm.OpenOtherRelationsTagCommand.ExecuteAsync(several);
        Assert.True(several.ShowOtherRelationsList);
        Assert.Equal("Flare", vm.SelectedRelation!.Name);
        await vm.OpenRelationFromSearchCommand.ExecuteAsync("Chaff");
        Assert.Equal("Chaff", vm.SelectedRelation!.Name);
        await RelationBuilderTests.SettleAllAsync();
    }

    [Fact]
    public async Task CurrentRelation_GreyedAtTheBottom_NotTickable_NotCounted()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await SearchFlareAsync(fake);

        // The row of the open relation comes last, after a group of its own, and cannot be ticked.
        List<object> rows = vm.BuilderSearchRows.ToList();
        BuilderSearchResultItem inside = Assert.IsType<BuilderSearchResultItem>(rows[^1]);
        Assert.Equal("d3014pnil", inside.Id);
        Assert.True(inside.IsInOpenRelation);
        Assert.False(inside.CanCheck);
        BuilderSearchGroupItem group = Assert.IsType<BuilderSearchGroupItem>(rows[^2]);
        Assert.True(group.IsInRelationGroup);
        Assert.False(group.IsGame);
        Assert.False(group.HasTickable);
        Assert.Equal("Already in this relation", group.Label);
        // The header counts what can be added.
        Assert.StartsWith("4 actions in 3 games + 1 already in this relation", vm.BuilderSearchStatus);
        // Tick all shown and Add selected leave it out.
        vm.CheckAllShownBuilderResultsCommand.Execute(null);
        Assert.Equal(4, vm.BuilderCheckedCount);
        Assert.False(inside.IsChecked);
        // A row that comes into the relation moves to the bottom group.
        foreach (BuilderSearchResultItem row in vm.BuilderSearchResults.Where(r => r.Id != "ID_FLARES"))
            row.IsChecked = false;
        await vm.AddCheckedBuilderResultsCommand.ExecuteAsync(null);
        await RelationBuilderTests.WaitForAsync(() => !vm.IsBusy && vm.BuilderSearchResults.Single(r => r.Id == "ID_FLARES").IsInOpenRelation);
        rows = vm.BuilderSearchRows.ToList();
        int groupAt = rows.FindIndex(r => r is BuilderSearchGroupItem { IsInRelationGroup: true });
        Assert.True(groupAt > 0);
        Assert.Equal(["ID_FLARES", "d3014pnil"], rows.Skip(groupAt + 1).OfType<BuilderSearchResultItem>().Select(r => r.Id).OrderBy(i => i, StringComparer.Ordinal).ToArray());
        Assert.All(rows.Take(groupAt).OfType<BuilderSearchResultItem>(), r => Assert.True(r.CanCheck));
        await RelationBuilderTests.SettleAllAsync();
    }

    [Fact]
    public async Task ReplaceFromTheCheck_SearchesTheName_SwapsInOneCall_AndGoesBack()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "search-replace.pr0file");
        vm.CurrentStage = UiStage.Export;

        await vm.ReplaceCheckActionAsync("Flare", "DCS", "d3014pnil", "Countermeasures Management Switch - Fwd", ["F-16C_50"]);
        await RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSearching && !vm.IsBusy && vm.BuilderSearchResults.Count > 0);
        Assert.Equal(UiStage.Relations, vm.CurrentStage);
        Assert.Equal("Flare", vm.SelectedRelation!.Name);
        Assert.True(vm.IsBuilderAddPanel);
        Assert.True(vm.IsBuilderReplacing);
        // The command's name without its direction, the aircraft as the filter.
        Assert.Equal("Countermeasures Management Switch", vm.BuilderSearchText);
        Assert.Equal(["F-16C_50"], vm.BuilderSearchPlanes.ToArray());
        Assert.Contains("Replacing 'Countermeasures Management Switch - Fwd' in 'Flare'", vm.BuilderReplaceText);
        Assert.True(vm.CanReturnToCheck);
        Assert.Equal("Back to the check", vm.ReturnToCheckLabel);

        vm.BuilderSearchResults.Single(r => r.Id == "d3084").IsChecked = true;
        Assert.Equal("Replace with the ticked (1)", vm.BuilderAddButtonLabel);
        fake.Calls.Clear();
        await vm.AddCheckedBuilderResultsCommand.ExecuteAsync(null);
        IReadOnlyList<string> apply = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "apply"));
        Assert.True(AxisTuningTests.Seq(apply, "apply", "Flare"));
        Assert.True(AxisTuningTests.Seq(apply, "--add", "DCS:d3084:F-14"));
        Assert.True(AxisTuningTests.Seq(apply, "--remove", "DCS:d3014pnil:F-16C_50"));
        Assert.False(vm.IsBuilderReplacing);
        Assert.Contains("Replaced 'Countermeasures Management Switch - Fwd'", vm.StatusText);

        // Back to the check: the Export page again, the check read again (the card may be gone).
        fake.Calls.Clear();
        await vm.ReturnToCheckCommand.ExecuteAsync(null);
        Assert.Equal(UiStage.Export, vm.CurrentStage);
        Assert.False(vm.CanReturnToCheck);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "profile", "validate"));
        await RelationBuilderTests.SettleAllAsync();
    }

    // The Relations page with the tags, the greyed bottom group, the replace banner and "Back to the check", at 1280 and
    // 1920 in Dark and Light (JOYPRO_SEARCH_RENDER_OUT=<folder>; nothing without it).
    [Fact]
    public void RenderTagsAndReplace()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_SEARCH_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            BuilderFakeEngine fake = new() { SeveralRelations = "Chaff|Hornet dispense" };
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(SearchFlareAsync(fake));
            try
            {
                vm.BuilderSearchResults.Single(r => r.Id == "ID_FLARES").IsOtherRelationsExpanded = true;
                foreach ((string theme, Avalonia.Styling.ThemeVariant variant) in new[] { ("dark", Avalonia.Styling.ThemeVariant.Dark), ("light", Avalonia.Styling.ThemeVariant.Light) })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = variant;
                    foreach (int width in new[] { 1280, 1920 })
                        UiDesignRenderTests.RenderWindow(vm, width, width == 1280 ? 800 : 1080, Path.Combine(outDir, "search-tags-" + width + "-" + theme + ".png"));
                }
                AvaloniaRenderThread.Wait(vm.ReplaceCheckActionAsync("Flare", "DCS", "d3014pnil", "Countermeasures Management Switch - Fwd", ["F-16C_50"]));
                AvaloniaRenderThread.Wait(RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSearching && !vm.IsBusy));
                foreach ((string theme, Avalonia.Styling.ThemeVariant variant) in new[] { ("dark", Avalonia.Styling.ThemeVariant.Dark), ("light", Avalonia.Styling.ThemeVariant.Light) })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = variant;
                    foreach (int width in new[] { 1280, 1920 })
                        UiDesignRenderTests.RenderWindow(vm, width, width == 1280 ? 800 : 1080, Path.Combine(outDir, "search-replace-" + width + "-" + theme + ".png"));
                }
            }
            finally
            {
                vm.StopGameConfigWatcher();
                Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
            }
        });
    }

    [Theory]
    [InlineData("TACAN Channel (xx0x) - [Dec]", "TACAN Channel (xx0x)")]
    [InlineData("Countermeasures Management Switch - Fwd", "Countermeasures Management Switch")]
    [InlineData("Zoom -", "Zoom")]
    [InlineData("Gear Up", "Gear")]
    [InlineData("MASTER RESET", "MASTER RESET")]
    [InlineData("[FWD] Canopy", "[FWD] Canopy")]
    public void ReplaceSearchText_DropsTheDirection(string title, string expected) =>
        Assert.Equal(expected, MainWindowViewModel.ReplaceSearchText(title));
}

// The second tester's F-14 cases through the view model and the real engine: "master" with MASTER RESET in the open
// relation lists every title with the word, MASTER RESET last, greyed, not tickable; "reset" in another relation lists
// MASTER RESET with the tag of the relation it is in.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class SearchTagsRealEngineTests
{
    private const string MasterReset = "d3058pnilu3058cd23vd1vpnilvu0";
    private const string WingSweepFwd = "d3025pnilu3025cd17vd1vpnilvu0";

    private static string? Engine()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "bin")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return engine != null && File.Exists(engine) && Directory.Exists(Path.Combine(Path.GetDirectoryName(engine)!, "DB", "DCS")) ? engine : null;
    }

    [Fact]
    public async Task F14Master_MasterResetInTheRelation_LastGreyedNotTickable()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "search-f14-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            DataDirEngineClient client = new(Path.Combine(folder, "data"));
            string profile = Path.Combine(folder, "f14.pr0file");
            foreach (string[] add in new[]
                     {
                         new[] { "relations", "add", "Button VMAX 04", "--button", "--node", "DCS:" + MasterReset + ":F-14" },
                         new[] { "relations", "add", "Button VMAX 07", "--button", "--node", "DCS:" + WingSweepFwd + ":F-14" }
                     })
            {
                EngineCommandResult made = await client.RunAsync(engine, ["--profile", profile, "--status", "false", .. add]);
                Assert.True(made.ExitCode == 0, made.StandardError);
            }
            MainWindowViewModel vm = new(client, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
            try
            {
                await vm.RefreshRelationsCommand.ExecuteAsync(null);
                vm.CurrentStage = UiStage.Relations;

                // "master" in the relation that has MASTER RESET: every title with the word, MASTER RESET last and greyed.
                await RelationBuilderTests.OpenAsync(vm, "Button VMAX 04");
                vm.ShowBuilderFindCommand.Execute(null);
                await vm.AddBuilderSearchPlaneCommand.ExecuteAsync("F-14");
                vm.BuilderSearchText = "master";
                await RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSearching && vm.BuilderLastSearchedQueryForTests == "master");
                await vm.ExpandAllBuilderGamesCommand.ExecuteAsync(null);
                await RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSearching && !vm.IsBusy);
                List<BuilderSearchResultItem> shown = vm.BuilderSearchRows.OfType<BuilderSearchResultItem>().ToList();
                Assert.True(shown.Count(r => r.Title.Contains("master", StringComparison.OrdinalIgnoreCase)) >= 35, shown.Count + " rows: " + vm.BuilderSearchStatus);
                BuilderSearchResultItem last = shown[^1];
                Assert.Equal("MASTER RESET", last.Title);
                Assert.True(last.IsInOpenRelation);
                Assert.False(last.CanCheck);
                Assert.All(shown.Take(shown.Count - 1), r => Assert.True(r.CanCheck));
                Assert.Contains("+ 1 already in this relation", vm.BuilderSearchStatus);

                // "reset" in another relation: MASTER RESET is listed, tagged with the relation it is in.
                await RelationBuilderTests.OpenAsync(vm, "Button VMAX 07");
                vm.ShowBuilderFindCommand.Execute(null);
                vm.BuilderSearchText = "reset";
                await RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSearching && vm.BuilderLastSearchedQueryForTests == "reset");
                BuilderSearchResultItem reset = Assert.Single(vm.BuilderSearchResults, r => r.Title == "MASTER RESET");
                Assert.True(reset.CanCheck);
                Assert.Equal("in 'Button VMAX 04'", reset.OtherRelationsTag);

                // "master reset": the whole title first.
                vm.BuilderSearchText = "master reset";
                await RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSearching && vm.BuilderLastSearchedQueryForTests == "master reset");
                Assert.Equal("MASTER RESET", vm.BuilderSearchRows.OfType<BuilderSearchResultItem>().First().Title);
            }
            finally
            {
                vm.StopGameConfigWatcher();
                EngineServerPool.Recycle("search tags test done");
            }
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
