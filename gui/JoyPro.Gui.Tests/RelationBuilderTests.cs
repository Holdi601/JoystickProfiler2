using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Relations page (MainWindowViewModel.RelationBuilder.cs, docs/relation-builder.md): the search over every game with
// tick boxes and "Add selected" (also creating a new relation), the filters (kind, games), the name edited in place,
// "Suggested for this relation" (add, add all, dismiss, move, misfits) and the JoyPro assistant (changes applied at once,
// one Undo per answer, rejected ids shown, the offline path). The engine is faked here; the engine's search, suggestions
// and the assistant's tool loop are tested by engine\tests\smoke\relationbuilder.ps1 and relationassistant.ps1, and
// AssistantRealEngineTests runs this view model against the real engine with a scripted model.
[SupportedOSPlatform("windows")]
public sealed class RelationBuilderTests : IAsyncLifetime
{
    // Every view model of the running test: the editor starts work on its own (suggestions after a selection, the
    // search after typing, pictures); a test ends only when that work is done, so nothing of it overlaps the next test
    // (render tests use Avalonia's one UI thread).
    private static readonly List<MainWindowViewModel> Created = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await SettleAllAsync();

    internal static async Task SettleAllAsync()
    {
        List<MainWindowViewModel> vms;
        lock (Created)
        {
            vms = [.. Created];
            Created.Clear();
        }
        // The typed search waits 300 ms before it runs.
        await Task.Delay(400);
        foreach (MainWindowViewModel vm in vms)
        {
            int idle = 0;
            for (int i = 0; i < 300 && idle < 5; i++)
            {
                idle = vm.IsBusy || vm.IsBuilderSuggestLoading || vm.IsBuilderSearching || vm.IsAssistantRunning ? 0 : idle + 1;
                await Task.Delay(10);
            }
        }
    }

    // A refresh of the relation list (after an edit, an undo, a save) keeps the open relation open; when it leaves the
    // list (filtered out, deleted) its actions leave the editor with it, so only the "how to start" steps show.
    [Fact]
    public async Task RefreshingTheList_KeepsTheOpenRelationAndItsActions()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await OpenAsync(vm, "Flare");
        await WaitForAsync(() => vm.RelationNodes.Count > 0);
        Assert.True(vm.ShowRelationNodeList);
        // Add actions searches for what the opened relation is.
        Assert.Equal("Flare", vm.BuilderSearchText);

        fake.Calls.Clear();
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        Assert.Same(vm.Relations.Single(r => r.Name == "Flare"), vm.SelectedRelation);
        Assert.True(vm.ShowRelationNodeList);
        Assert.False(vm.HasNoRelationSelected);
        // Selected again without loading it again.
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "show"));

        // The function chip and the suggestions stay too.
        await WaitForAsync(() => !vm.IsBuilderSuggestLoading && !vm.IsBusy);
        Assert.Equal("covered", vm.BuilderFunctionState);
        Assert.NotEmpty(vm.BuilderSuggestions);
        await vm.RefreshAircraftCheckAsync();
        await WaitForAsync(() => !vm.IsBuilderSuggestLoading && !vm.IsBusy);
        Assert.Equal("covered", vm.BuilderFunctionState);

        vm.RelationSearchText = "Chaff";
        Assert.Null(vm.SelectedRelation);
        Assert.Empty(vm.RelationNodes);
        Assert.False(vm.ShowRelationNodeList);
        Assert.True(vm.HasNoRelationSelected);
    }

    [Fact]
    public async Task OpeningARelation_LoadsSuggestionsWithReasonGameAndConflict()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);

        await OpenAsync(vm, "Flare");
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "Flare", "--min", "low"));
        Assert.Equal("CMS FWD (Start Program or just Flare)", vm.BuilderFunctionText);
        Assert.Equal("Flare", vm.BuilderRenameText);
        // The weak match (title only) is hidden until asked for.
        Assert.Equal(3, vm.BuilderSuggestions.Count);
        BuilderSuggestionItem bms = vm.BuilderSuggestions.Single(s => s.Game == "FalconBMS");
        Assert.True(bms.IsHigh);
        Assert.Equal("Same function as F-16C_50 'Countermeasures Management Switch - Fwd'", bms.Why);
        BuilderSuggestionItem hornet = vm.BuilderSuggestions.Single(s => s.Id == "d3033");
        Assert.True(hornet.HasConflict);
        Assert.Equal("Already in 'Hornet dispense'", hornet.ConflictText);
        // "Add all" takes every shown row no other relation uses.
        Assert.Equal(2, vm.BuilderAddAllCount);
        Assert.Equal("Add all (2)", vm.BuilderAddAllLabel);
        Assert.Equal("Suggested for this relation (3)", vm.BuilderSuggestedTabLabel);
        Assert.Single(vm.BuilderDismissed);

        vm.BuilderShowWeakMatches = true;
        Assert.Equal(4, vm.BuilderSuggestions.Count);
        BuilderGameChip warThunder = vm.BuilderSuggestionGames.Single(g => g.GameId == "WarThunder");
        warThunder.IsSelected = true;
        Assert.All(vm.BuilderSuggestions, s => Assert.Equal("WarThunder", s.Game));
        vm.ClearBuilderGameFilterCommand.Execute(null);
        Assert.Equal(4, vm.BuilderSuggestions.Count);
    }

    [Fact]
    public async Task Suggestions_OneClickAddAndAddAll()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await OpenAsync(vm, "Flare");
        BuilderSuggestionItem bms = vm.BuilderSuggestions.Single(s => s.Game == "FalconBMS");

        fake.Calls.Clear();
        await vm.AddBuilderSuggestionCommand.ExecuteAsync(bms);
        IReadOnlyList<string> apply = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "apply"));
        Assert.True(AxisTuningTests.Seq(apply, "apply", "Flare", "--add", "FalconBMS:SimCMSUp:FalconBMS"));
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "show", "Flare"));
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "Flare", "--min", "low"));
        Assert.DoesNotContain(vm.BuilderSuggestions, s => s.Game == "FalconBMS");
        Assert.Contains(vm.RelationNodes, n => n.Game == "FalconBMS");
        Assert.Contains("added", vm.StatusText);

        // Add all: every shown row without a conflict, in one change.
        fake.Calls.Clear();
        fake.ExtraHigh = true;
        await vm.LoadBuilderSuggestionsAsync("Flare");
        await vm.AddAllBuilderSuggestionsCommand.ExecuteAsync(null);
        IReadOnlyList<string> all = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "apply"));
        Assert.True(AxisTuningTests.Seq(all, "--add", "WarThunder:ID_FLARES:WarThunder"));
        Assert.True(AxisTuningTests.Seq(all, "--add", "DCS:d3014:F-14|F-15E"));
        Assert.DoesNotContain("DCS:d3033:FA-18C_hornet", all);
    }

    [Fact]
    public async Task Suggestions_DismissNeverComesBackAndCanBeRestored()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await OpenAsync(vm, "Flare");
        vm.BuilderShowWeakMatches = true;
        BuilderSuggestionItem weak = vm.BuilderSuggestions.Single(s => s.IsLow);

        fake.Calls.Clear();
        await vm.DismissBuilderSuggestionCommand.ExecuteAsync(weak);
        IReadOnlyList<string> apply = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "apply"));
        Assert.True(AxisTuningTests.Seq(apply, "apply", "Flare", "--never", "DCS:d77:Mi-8MT"));
        Assert.DoesNotContain(vm.BuilderSuggestions, s => s.Id == "d77");
        Assert.Contains(vm.BuilderDismissed, d => d.Id == "d77");

        await vm.LoadBuilderSuggestionsAsync("Flare");
        Assert.DoesNotContain(vm.BuilderSuggestions, s => s.Id == "d77");

        BuilderDismissedItem dismissed = vm.BuilderDismissed.Single(d => d.Id == "d77");
        fake.Calls.Clear();
        await vm.RestoreBuilderDismissedCommand.ExecuteAsync(dismissed);
        Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "apply", "Flare", "--remove", "DCS:d77:Mi-8MT"));
        Assert.Contains(vm.BuilderSuggestions, s => s.Id == "d77");
    }

    [Fact]
    public async Task Suggestions_ConflictMovesTheCommandFromTheOtherRelation()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await OpenAsync(vm, "Flare");
        BuilderSuggestionItem hornet = vm.BuilderSuggestions.Single(s => s.HasConflict);

        fake.Calls.Clear();
        await vm.MoveBuilderSuggestionHereCommand.ExecuteAsync(hornet);
        IReadOnlyList<string> apply = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "apply"));
        Assert.True(AxisTuningTests.Seq(apply, "apply", "Flare", "--take", "DCS:d3033:FA-18C_hornet"));
        Assert.Contains("moved here from 'Hornet dispense'", vm.StatusText);
        Assert.DoesNotContain(vm.BuilderSuggestions, s => s.Id == "d3033");
    }

    // Opening another relation: the last one's suggestions, "Looks wrong" entries, dismissed actions and function chip go
    // at once (the engine run for the new one takes a while), and a row left over from it never acts on the new one
    // (the user saw 'UFC 3 Key' buttons offered under 'Pitch' while its suggestions were still loading).
    [Fact]
    public async Task SwitchingRelations_NeverShowsOrAppliesTheLastRelationsSuggestions()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await OpenAsync(vm, "Flare");
        BuilderSuggestionItem stale = vm.BuilderSuggestions.First(s => s.Game == "FalconBMS");
        RelationMisfitItem staleMisfit = Assert.Single(vm.RelationMisfits);
        Assert.NotEmpty(vm.BuilderFunctionText);

        fake.GateRelation = "Chaff";
        fake.SuggestGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Chaff");
        await WaitForAsync(() => vm.IsBuilderSuggestLoading && vm.BuilderRelationName == "Chaff");
        Assert.Empty(vm.BuilderSuggestions);
        Assert.Empty(vm.RelationMisfits);
        Assert.Empty(vm.BuilderDismissed);
        Assert.Equal(string.Empty, vm.BuilderFunctionText);
        Assert.Equal("Suggested for this relation", vm.BuilderSuggestedTabLabel);
        Assert.Equal(0, vm.BuilderMissingCount);

        fake.SuggestGate.SetResult();
        await WaitForAsync(() => !vm.IsBuilderSuggestLoading && !vm.IsBusy);
        fake.Calls.Clear();
        await vm.AddBuilderSuggestionCommand.ExecuteAsync(stale);
        await vm.DismissBuilderSuggestionCommand.ExecuteAsync(stale);
        await vm.MoveMisfitCommand.ExecuteAsync(staleMisfit);
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "apply"));
    }

    // One recommended action per game, ticked; its alternatives collapsed under it, each with how it differs, never
    // ticked (not even by the section's tick while collapsed); MSFS shown by the sim's menu name with the event id small.
    [Fact]
    public async Task Suggestions_RecommendedFirstAlternativesCollapsedUnderIt()
    {
        BuilderFakeEngine fake = new();
        fake.AddPitchRelation();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await OpenAsync(vm, "Pitch");
        SuggestionSectionItem games = vm.BuilderSuggestionSections.Single(s => s.Title == "Games not in this relation yet");
        List<string> order = games.Items.Select(i => i.Id).ToList();
        BuilderSuggestionItem raw = games.Items.Single(i => i.Id == "PitchAxisRaw");
        BuilderSuggestionItem alternate = games.Items.Single(i => i.Id == "PitchAxisAlternate");
        BuilderSuggestionItem landing = games.Items.Single(i => i.Id == "PitchAxis_Landing");
        Assert.True(raw.IsRecommended && raw.IsChecked);
        Assert.Equal(order.IndexOf("PitchAxisRaw") + 1, Math.Min(order.IndexOf("PitchAxisAlternate"), order.IndexOf("PitchAxis_Landing")));
        Assert.Equal(2, raw.AlternativeItems.Count);
        Assert.Equal("+2 other actions in this game", raw.AlternativesLabel);
        Assert.All(new[] { alternate, landing }, a => Assert.True(a.IsAlternative && !a.IsShown && !a.IsChecked && a.HasVariant));
        Assert.Equal("Differs: used in the game's landing mode", landing.VariantText);

        BuilderSuggestionItem elevator = games.Items.Single(i => i.Id == "KEY_AXIS_ELEVATOR_SET");
        Assert.Equal("Elevator Axis", elevator.DisplayTitle);
        Assert.Equal("Pitch axis · KEY_AXIS_ELEVATOR_SET", elevator.FunctionAndId);
        Assert.Equal("Recommended for planes", elevator.RecommendedLabel);
        Assert.Equal("Control the elevator axis.", elevator.Explanation);
        BuilderSuggestionItem cyclic = games.Items.Single(i => i.Id == "KEY_AXIS_CYCLIC_LONGITUDINAL_SET");
        Assert.True(cyclic.IsRecommended && cyclic.IsChecked && !cyclic.IsAlternative);
        Assert.Equal("Recommended for helicopters", cyclic.RecommendedLabel);

        // The section's tick takes what is shown; the collapsed alternatives stay unticked.
        games.IsChecked = false;
        games.IsChecked = true;
        Assert.False(alternate.IsChecked);
        Assert.True(raw.IsChecked);
        raw.ToggleAlternativesCommand.Execute(null);
        Assert.True(alternate.IsShown && landing.IsShown);
        Assert.Equal("Hide the other actions", raw.AlternativesLabel);
        Assert.Equal(3, vm.BuilderTickedCount);
    }

    // The function chip: "Function: <name>" only when the function covers the relation, never a majority with a
    // percentage; a button position (different functions per aircraft) gets the neutral chip; the picker sets a
    // function, "None" or goes back to automatic (engine relations function), then the suggestions are asked again.
    [Fact]
    public async Task FunctionChip_ShowsOnlyACoveringFunction_AndThePickerSetsOrClearsIt()
    {
        BuilderFakeEngine fake = new();
        fake.AddBoatRelation();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await OpenAsync(vm, "Flare");
        Assert.True(vm.HasBuilderFunction && vm.ShowBuilderFunctionChip);
        Assert.Equal("Function: CMS FWD (Start Program or just Flare)", vm.BuilderFunctionChipText);
        Assert.DoesNotContain("%", vm.BuilderFunctionChipText);
        Assert.Contains("suggest the same function", vm.BuilderFunctionChipTooltip);

        await OpenAsync(vm, "Boat Aft");
        await WaitForAsync(() => vm.BuilderFunctionState == "mixed");
        Assert.False(vm.HasBuilderFunction);
        Assert.True(vm.IsBuilderFunctionMixed && vm.ShowBuilderFunctionChip);
        Assert.Equal("Different functions per aircraft", vm.BuilderFunctionChipText);
        Assert.Contains("This relation is a button position: each aircraft uses it for its own function", vm.BuilderFunctionChipTooltip);
        Assert.Contains("button position", vm.BuilderSuggestEmptyText);

        await vm.OpenBuilderFunctionPickerCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.BuilderFunctionChoices.Count);
        vm.BuilderFunctionQuery = "flaps";
        RelationFunctionChoice flaps = Assert.Single(vm.BuilderFunctionChoices);
        fake.Calls.Clear();
        await vm.SetBuilderFunctionCommand.ExecuteAsync(flaps);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "function", "Boat Aft", "--set", "flapsdown"));
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "Boat Aft"));
        Assert.Equal("Function: Flaps Down", vm.BuilderFunctionChipText);
        Assert.True(vm.IsBuilderFunctionSetByUser);
        Assert.Contains("picked by you", vm.BuilderFunctionChipTooltip);

        await vm.ClearBuilderFunctionCommand.ExecuteAsync(null);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "function", "Boat Aft", "--none"));
        Assert.Equal("Function: none", vm.BuilderFunctionChipText);
        await vm.AutoBuilderFunctionCommand.ExecuteAsync(null);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "function", "Boat Aft", "--auto"));
        Assert.Equal("Different functions per aircraft", vm.BuilderFunctionChipText);
    }

    // A relation opened while a tool covers the panel (Auto Group): nothing of the last relation stays in the panel.
    [Fact]
    public async Task OpeningARelationUnderATool_ClearsTheLastRelationsSuggestions()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await OpenAsync(vm, "Flare");
        Assert.NotEmpty(vm.BuilderSuggestions);
        vm.CurrentStage = UiStage.Binds;
        vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Chaff");
        Assert.Empty(vm.BuilderSuggestions);
        Assert.Empty(vm.RelationMisfits);
        Assert.Equal(string.Empty, vm.BuilderFunctionText);
    }

    [Fact]
    public async Task Suggestions_MisfitMovesToTheRelationOfItsFunction()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await OpenAsync(vm, "Flare");
        RelationMisfitItem misfit = Assert.Single(vm.RelationMisfits);
        Assert.Equal("Chaff", misfit.MoveTo);

        fake.Calls.Clear();
        await vm.MoveMisfitCommand.ExecuteAsync(misfit);
        Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "apply", "Flare", "--move", "DCS:d3015:F-16C_50=>Chaff"));
    }

    [Fact]
    public async Task Search_RunsAsYouTypeOverEveryGameAndAddsTheTickedRows()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await OpenAsync(vm, "Flare");
        Assert.True(vm.IsBuilderAddPanel || vm.IsBuilderSuggestedPanel);
        vm.ShowBuilderFindCommand.Execute(null);
        Assert.True(vm.IsBuilderAddPanel);

        // Typing searches by itself after a short pause: every game (no --game), only the open relation's kind.
        fake.Calls.Clear();
        vm.BuilderSearchText = "flare";
        await WaitForAsync(() => fake.Calls.Any(c => AxisTuningTests.Seq(c, "relations", "search")) && !vm.IsBuilderSearching && vm.BuilderSearchResults.Count > 0);
        IReadOnlyList<string> search = fake.Calls.Last(c => AxisTuningTests.Seq(c, "relations", "search"));
        Assert.True(AxisTuningTests.Seq(search, "search", "flare"));
        Assert.DoesNotContain("--game", search);
        Assert.True(AxisTuningTests.Seq(search, "--limit", "200", "--per-game", "12"));
        Assert.Contains("--button", search);
        Assert.False(vm.CanChangeBuilderKind);
        Assert.True(vm.IsBuilderKindButton);

        // Grouped by game, a header per game; the row already in the relation cannot be ticked.
        Assert.Equal(5, vm.BuilderSearchResults.Count);
        List<BuilderSearchGroupItem> groups = vm.BuilderSearchRows.OfType<BuilderSearchGroupItem>().ToList();
        Assert.Equal(["DCS", "FalconBMS", "WarThunder"], groups.Select(g => g.Game).ToArray());
        Assert.Equal(3, vm.BuilderSearchGames.Count);
        BuilderSearchResultItem inside = vm.BuilderSearchResults.Single(r => r.Id == "d3014pnil");
        Assert.True(inside.IsInOpenRelation);
        Assert.False(inside.CanCheck);
        Assert.Equal("already in this relation", inside.InOtherRelationsText);
        BuilderSearchResultItem used = vm.BuilderSearchResults.Single(r => r.Id == "d3001");
        Assert.Equal("also in 'Chaff'", used.InOtherRelationsText);
        Assert.Contains("understood as CMS FWD", vm.BuilderSearchStatus);
        // The match tier and the function of each row; grouped by function on request.
        Assert.Equal("same function", vm.BuilderSearchResults.Single(r => r.Id == "d3084").MatchLabel);
        Assert.Equal("Countermeasures dispense", vm.BuilderSearchResults.Single(r => r.Id == "d3084").FunctionName);
        Assert.Equal("word match", used.MatchLabel);
        vm.BuilderGroupByFunction = true;
        Assert.Equal(["Countermeasures dispense", "Other matches"], vm.BuilderSearchRows.OfType<BuilderSearchGroupItem>().Select(g => g.Label).ToArray());
        vm.BuilderGroupByFunction = false;

        // A game chip searches that game again (all its rows); the chips stay; none ticked = every game.
        fake.Calls.Clear();
        vm.BuilderSearchGames.Single(g => g.GameId == "WarThunder").IsSelected = true;
        await WaitForAsync(() => fake.Calls.Any(c => AxisTuningTests.Seq(c, "relations", "search") && c.Contains("--game")) && !vm.IsBuilderSearching);
        Assert.True(AxisTuningTests.Seq(fake.Calls.Last(c => AxisTuningTests.Seq(c, "relations", "search")), "--game", "WarThunder"));
        Assert.All(vm.BuilderSearchRows.OfType<BuilderSearchResultItem>(), r => Assert.Equal("WarThunder", r.Game));
        Assert.Equal(3, vm.BuilderSearchGames.Count);
        vm.BuilderSearchGames.Single(g => g.GameId == "WarThunder").IsSelected = false;
        await WaitForAsync(() => !vm.IsBuilderSearching && vm.BuilderSearchResults.Count == 5);

        // Tick several (one by its game's box), then one "Add selected".
        groups = vm.BuilderSearchRows.OfType<BuilderSearchGroupItem>().ToList();
        groups.Single(g => g.Game == "FalconBMS").IsChecked = true;
        vm.BuilderSearchResults.Single(r => r.Id == "ID_FLARES").IsChecked = true;
        vm.BuilderSearchResults.Single(r => r.Id == "d3084").IsChecked = true;
        Assert.Equal(3, vm.BuilderCheckedCount);
        Assert.Equal("Add selected (3)", vm.BuilderAddButtonLabel);
        Assert.Null(groups.Single(g => g.Game == "DCS").IsChecked);
        fake.Calls.Clear();
        await vm.AddCheckedBuilderResultsCommand.ExecuteAsync(null);
        IReadOnlyList<string> apply = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "apply"));
        Assert.True(AxisTuningTests.Seq(apply, "apply", "Flare"));
        Assert.True(AxisTuningTests.Seq(apply, "--add", "FalconBMS:SimCMSUp:FalconBMS"));
        Assert.True(AxisTuningTests.Seq(apply, "--add", "WarThunder:ID_FLARES:WarThunder"));
        Assert.True(AxisTuningTests.Seq(apply, "--add", "DCS:d3084:F-14"));
        Assert.Equal(0, vm.BuilderCheckedCount);
        Assert.Contains(vm.RelationNodes, n => n.Game == "WarThunder");
        Assert.Contains("3 actions added to 'Flare'", vm.StatusText);
        // What is in the relation now is marked in the list.
        Assert.True(vm.BuilderSearchResults.Single(r => r.Id == "ID_FLARES").IsInOpenRelation);
    }

    // The causes of "Find actions shows nothing": a search that needed a profile, and an empty relation that hid every
    // axis behind --button.
    [Fact]
    public async Task Search_WorksWithoutAProfileAndAnEmptyRelationHidesNoKind()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await OpenAsync(vm, "Empty one");
        Assert.True(vm.CanChangeBuilderKind);
        vm.BuilderSearchText = "throttle";
        await vm.SearchBuilderActionsCommand.ExecuteAsync(null);
        IReadOnlyList<string> search = fake.Calls.Last(c => AxisTuningTests.Seq(c, "relations", "search"));
        Assert.DoesNotContain("--button", search);
        Assert.DoesNotContain("--axis", search);
        Assert.Contains(vm.BuilderSearchResults, r => r.IsAxis);
        // Axes into the empty button relation are refused with a hint (no engine call).
        vm.BuilderSearchResults.First(r => r.IsAxis).IsChecked = true;
        fake.Calls.Clear();
        await vm.AddCheckedBuilderResultsCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "apply"));
        Assert.Contains("button relation", vm.BuilderSearchStatus);

        // No profile open: the search still runs, without --profile.
        MainWindowViewModel bare = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };
        lock (Created)
            Created.Add(bare);
        bare.CurrentStage = UiStage.Relations;
        fake.Calls.Clear();
        bare.BuilderSearchText = "throtle";
        await bare.SearchBuilderActionsCommand.ExecuteAsync(null);
        IReadOnlyList<string> withoutProfile = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "search"));
        Assert.DoesNotContain("--profile", withoutProfile);
        Assert.NotEmpty(bare.BuilderSearchResults);
        Assert.Equal("throttle", bare.BuilderDidYouMean);
        bare.UseBuilderDidYouMeanCommand.Execute(null);
        Assert.Equal("throttle", bare.BuilderSearchText);
    }

    [Fact]
    public async Task NewRelation_NameThenTickedAxesCreateIt()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        vm.StartNewRelationCommand.Execute(null);
        Assert.True(vm.ShowNewRelationCard);
        Assert.Null(vm.SelectedRelation);
        Assert.True(vm.CanChangeBuilderKind);
        vm.RelationNameInput = "Throttle ALL";

        // Any kind first: an axis and a button together are refused.
        vm.BuilderSearchText = "throttle";
        await vm.SearchBuilderActionsCommand.ExecuteAsync(null);
        IReadOnlyList<string> any = fake.Calls.Last(c => AxisTuningTests.Seq(c, "relations", "search"));
        Assert.DoesNotContain("--axis", any);
        Assert.DoesNotContain("--button", any);
        vm.BuilderSearchResults.Single(r => r.Id == "a2004cdnil").IsChecked = true;
        vm.BuilderSearchResults.Single(r => r.Id == "cutoff").IsChecked = true;
        fake.Calls.Clear();
        await vm.AddCheckedBuilderResultsCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "add"));
        Assert.Contains("axes or buttons, not both", vm.BuilderSearchStatus);

        // Axes only: the chip searches again with --axis; the ticks stay on what comes back.
        vm.SetBuilderKindCommand.Execute("axis");
        await WaitForAsync(() => !vm.IsBuilderSearching && fake.Calls.Any(c => AxisTuningTests.Seq(c, "relations", "search") && c.Contains("--axis")));
        Assert.DoesNotContain(vm.BuilderSearchResults, r => !r.IsAxis);
        Assert.True(vm.BuilderSearchResults.Single(r => r.Id == "a2004cdnil").IsChecked);
        vm.BuilderSearchResults.Single(r => r.Game == "StarCitizen").IsChecked = true;
        Assert.Equal("Create relation with 2 actions", vm.BuilderAddButtonLabel);
        fake.Calls.Clear();
        await vm.AddCheckedBuilderResultsCommand.ExecuteAsync(null);
        IReadOnlyList<string> add = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "add"));
        Assert.True(AxisTuningTests.Seq(add, "add", "Throttle ALL", "--axis"));
        Assert.True(AxisTuningTests.Seq(add, "--node", "DCS:a2004cdnil:F-16C_50|F-15C"));
        Assert.True(AxisTuningTests.Seq(add, "--node", "StarCitizen:v_strafe_longitudinal:StarCitizen"));
        Assert.Equal("Throttle ALL", vm.SelectedRelation?.Name);
        Assert.False(vm.ShowNewRelationForm);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "Throttle ALL"));
        Assert.Contains("created with 2 actions", vm.StatusText);
        Assert.Equal("Flare Dispense Button", MainWindowViewModel.RelationNameFromTitle("29Fulcrum:Inp:Flare_Dispense_Button"));
    }

    [Fact]
    public async Task Rename_InPlaceRenamesTheRelation()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await OpenAsync(vm, "Flare");
        vm.BuilderRenameText = "Chaff";
        fake.Calls.Clear();
        await vm.CommitBuilderRenameCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "rename"));
        Assert.Equal("Flare", vm.BuilderRenameText);

        vm.BuilderRenameText = "Flares";
        await vm.CommitBuilderRenameCommand.ExecuteAsync(null);
        Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "rename", "Flare", "Flares"));
        Assert.Equal("Flares", vm.SelectedRelation?.Name);
        Assert.Contains("is now 'Flares'", vm.StatusText);
    }

    [Fact]
    public async Task Assistant_AppliesDirectlyShowsTheSummaryAndUndoesTheWholeAnswer()
    {
        BuilderFakeEngine fake = new() { LlmEnabled = true };
        MainWindowViewModel vm = await CreateVmAsync(fake, "relation-builder-assistant-" + Guid.NewGuid().ToString("N")[..8] + ".pr0file");
        BuilderFakeEngine.SaveVersion(vm.ProfilePath, "before the assistant", "First version");
        await OpenAsync(vm, "Flare");
        await WaitForAsync(() => vm.IsBuilderChatAvailable);
        vm.ShowBuilderAssistantCommand.Execute(null);
        Assert.True(vm.IsBuilderAssistantPanel);
        Assert.StartsWith("Local AI model ready", vm.AssistantModeText);

        vm.AssistantInput = "Add all inputs that bind all thrust to one axis into one relation called Throttle ALL including collectives";
        fake.Calls.Clear();
        await vm.AskAssistantCommand.ExecuteAsync(null);
        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "assistant"));
        Assert.True(AxisTuningTests.Seq(call, "--llm", "auto"));
        Assert.DoesNotContain("--relation", call);
        Assert.Contains("--profile", call);

        // Visible at once: the relation list, the open relation and its actions.
        Assert.Contains(vm.Relations, r => r.Name == "Throttle ALL");
        Assert.Equal("Throttle ALL", vm.SelectedRelation?.Name);
        Assert.Contains(vm.RelationNodes, n => n.Game == "StarCitizen");
        Assert.Equal(2, vm.AssistantMessages.Count);
        AssistantMessageItem answer = vm.AssistantMessages[1];
        Assert.False(answer.IsUser);
        Assert.Equal("I created Throttle ALL with the throttle axes, the collectives and the spaceship forward thrust.", answer.Text);
        AssistantRelationSummary summary = Assert.Single(answer.Relations);
        Assert.Equal("Throttle ALL", summary.Name);
        Assert.Equal("created · added DCS 12 aircraft, Star Citizen 2, IL-2 1 · now 5 actions", summary.Text);
        Assert.Contains("A999", answer.Notes);
        Assert.Equal(4, answer.Steps.Count);
        Assert.Contains(answer.Steps, s => s.Contains("rejected ids: A999", StringComparison.Ordinal));
        Assert.True(answer.CanUndo);
        Assert.Equal("after the assistant", File.ReadAllText(vm.ProfilePath));

        // One Undo takes the whole answer back.
        fake.Calls.Clear();
        await vm.UndoAssistantTurnCommand.ExecuteAsync(answer);
        Assert.Equal("before the assistant", File.ReadAllText(vm.ProfilePath));
        Assert.True(answer.IsUndone);
        Assert.False(answer.CanUndo);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "list"));
    }

    [Fact]
    public async Task Assistant_ExpandWithAiWorksOnTheOpenRelation()
    {
        BuilderFakeEngine fake = new() { LlmEnabled = true };
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await OpenAsync(vm, "Flare");
        vm.ExpandWithAiCommand.Execute(null);
        Assert.True(vm.IsBuilderAssistantPanel);
        Assert.Equal("Flare", vm.AssistantContextRelation);
        Assert.Contains("'Flare'", vm.AssistantPlaceholder);

        vm.AssistantInput = "also the chaff-and-flare program buttons of the jets";
        fake.Calls.Clear();
        await vm.AskAssistantCommand.ExecuteAsync(null);
        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "assistant"));
        Assert.True(AxisTuningTests.Seq(call, "--relation", "Flare"));
        Assert.Equal("Flare", vm.SelectedRelation?.Name);
        Assert.Equal("added DCS 2 aircraft · now 2 actions", vm.AssistantMessages.Last().Relations.Single().Text);
    }

    [Fact]
    public async Task Assistant_WithoutTheModelUsesTheOfflineBrainAndSaysWhatNeedsTheModel()
    {
        BuilderFakeEngine fake = new() { LlmEnabled = false };
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await OpenAsync(vm, "Flare");
        await WaitForAsync(() => fake.Calls.Any(c => AxisTuningTests.Seq(c, "llm", "status")));
        Assert.False(vm.IsBuilderChatAvailable);
        Assert.StartsWith("Offline brain", vm.AssistantModeText);
        Assert.Contains("Throttle ALL: all throttle axes", vm.AssistantExamples);

        // A simple command works offline.
        vm.AssistantInput = "Throttle ALL: all throttle axes";
        fake.Calls.Clear();
        await vm.AskAssistantCommand.ExecuteAsync(null);
        Assert.True(AxisTuningTests.Seq(Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "assistant")), "--llm", "off"));
        Assert.Equal("Throttle ALL", vm.SelectedRelation?.Name);
        Assert.Contains("offline brain", vm.AssistantMessages.Last().Method);

        // A free sentence: nothing changes, the answer says it needs the model.
        vm.AssistantInput = "make my helicopters feel nicer";
        fake.Calls.Clear();
        await vm.AskAssistantCommand.ExecuteAsync(null);
        AssistantMessageItem answer = vm.AssistantMessages.Last();
        Assert.True(answer.NeedsModel);
        Assert.False(answer.CanUndo);
        Assert.Contains("need the local AI model", answer.Text);
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "list"));
    }

    [Fact]
    public async Task WholeProfileTools_GroupAllAutomaticallyHereAutoAssignOnTheBindsPage()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        vm.ToggleOrganizeCommand.Execute(null);
        Assert.True(vm.IsAutoGroupPanelOpen);
        Assert.True(vm.IsAnyRelationToolOpen);
        vm.ShowOrganizeByDescriptionCommand.Execute(null);
        Assert.True(vm.IsDescribeGroupPanelOpen);
        vm.ToggleOrganizeCommand.Execute(null);
        Assert.False(vm.IsOrganizeOpen);
        Assert.True(vm.IsNoRelationToolOpen);

        // "Put relations on my buttons automatically" lives on the Binds page; the relation editor stays.
        await vm.ToggleAutoAssignPanelCommand.ExecuteAsync(null);
        Assert.True(vm.IsAutoAssignPanelOpen);
        Assert.True(vm.IsBindsStage);
        Assert.False(vm.IsAnyRelationToolOpen);
    }

    [Fact]
    public async Task EmptyRelation_SaysToAddTheFirstActions()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await OpenAsync(vm, "Empty one");
        Assert.True(vm.HasBuilderNoNodes);
        Assert.True(vm.ShowBuilderSuggestEmpty);
        Assert.StartsWith("This relation has no actions yet", vm.BuilderSuggestEmptyText);
    }

    internal static async Task<MainWindowViewModel> CreateVmAsync(IEngineCliClient fake, string profileName = "relation-builder.pr0file")
    {
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
        lock (Created)
            Created.Add(vm);
        vm.EnginePath = typeof(MainWindowViewModel).Assembly.Location;
        string tempRoot = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests");
        Directory.CreateDirectory(tempRoot);
        vm.ProfilePath = Path.Combine(tempRoot, profileName);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        vm.CurrentStage = UiStage.Relations;
        return vm;
    }

    internal static async Task OpenAsync(MainWindowViewModel vm, string relation)
    {
        vm.SelectedRelation = vm.Relations.Single(r => r.Name == relation);
        await WaitForAsync(() => vm.BuilderRelationName == relation && !vm.IsBuilderSuggestLoading);
        await vm.LoadBuilderSuggestionsAsync(relation);
        await WaitForAsync(() => !vm.IsBusy);
    }

    internal static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 400 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }
}

// A profile with "Flare" (DCS F-16C CMS forward, on JOY_BTN5 of the stick), "Chaff", "Hornet dispense" and an empty
// relation; answers relations list/show/suggest/apply/search/add/rename/assistant and llm status like the engine and
// remembers what was added or dismissed.
[SupportedOSPlatform("windows")]
internal sealed class BuilderFakeEngine : IEngineCliClient
{
    public const string Stick = "Joystick - HOTAS Warthog {00000000-0000-0000-0000-0000000000a1}";

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public bool LlmEnabled { get; set; }

    // A second high suggestion (several DCS aircraft) for "Add all".
    public bool ExtraHigh { get; set; }

    // An axis relation "Pitch" whose suggestions carry the recommended action per game (relations suggest Recommended,
    // Variant, Group, Readable, Explanation).
    public void AddPitchRelation() => relations.Add(("Pitch", true, string.Empty, [("EliteDangerous", "RollAxisRaw", "EliteDangerous")]));

    private static string Choice(string game, string id, string title, string readable, bool recommended, string recommendedFor, string variant, string group, int alternatives, string explanation) =>
        "{\"Game\":\"" + game + "\",\"Id\":\"" + id + "\",\"Title\":\"" + title + "\",\"IsAxis\":true,\"Planes\":[\"" + game + "\"],\"Confidence\":\"high\",\"Score\":90," +
        "\"Source\":\"function map\",\"Why\":\"Same function: Pitch axis\",\"Reasons\":[],\"InOtherRelations\":[],\"Missing\":\"game\",\"NewGame\":true,\"NewPlanes\":[\"" + game + "\"]," +
        "\"Recommended\":" + (recommended ? "true" : "false") + ",\"RecommendedFor\":\"" + recommendedFor + "\",\"Variant\":\"" + variant + "\",\"Group\":\"" + group + "\"," +
        "\"Alternatives\":" + alternatives + ",\"Readable\":\"" + readable + "\",\"FunctionName\":\"Pitch axis\",\"Explanation\":\"" + explanation + "\",\"ExplanationSource\":\"game\"}";

    private static string PitchSuggestions() =>
        "{\"Relation\":\"Pitch\",\"Slot\":\"pitch\",\"SlotName\":\"Pitch\",\"IsAxis\":true,\"Functions\":[{\"Slot\":\"pitch\",\"Name\":\"Pitch\",\"Weight\":1,\"Reference\":\"\"}],\"Suggestions\":[" +
        string.Join(",",
            Choice("EliteDangerous", "PitchAxisAlternate", "Pitch Axis (Alternate)", "Pitch Axis (Alternate)", false, "", "the game's alternate set of this control", "EliteDangerous:PitchAxisRaw:EliteDangerous", 0, ""),
            Choice("EliteDangerous", "PitchAxisRaw", "Pitch Axis", "Pitch Axis", true, "", "", "EliteDangerous:PitchAxisRaw:EliteDangerous", 2, ""),
            Choice("EliteDangerous", "PitchAxis_Landing", "Pitch Axis (Landing)", "Pitch Axis (Landing)", false, "", "used in the game's landing mode", "EliteDangerous:PitchAxisRaw:EliteDangerous", 0, ""),
            Choice("MSFS2024", "KEY_AXIS_ELEVATOR_SET", "AXIS ELEVATOR SET", "Elevator Axis", true, "for planes", "", "MSFS2024:KEY_AXIS_ELEVATOR_SET:MSFS2024", 1, "Control the elevator axis."),
            Choice("MSFS2024", "KEY_ELEVATOR_SET", "Elevator Set", "Elevator Set", false, "", "another action for the same thing; the game does not say how it differs", "MSFS2024:KEY_AXIS_ELEVATOR_SET:MSFS2024", 0, ""),
            Choice("MSFS2024", "KEY_AXIS_CYCLIC_LONGITUDINAL_SET", "Set Cyclic Longitudinal Axis", "Set Cyclic Longitudinal Axis", true, "for helicopters", "", "MSFS2024:KEY_AXIS_CYCLIC_LONGITUDINAL_SET:MSFS2024", 0, "")) +
        "],\"Misfits\":[],\"Dismissed\":[],\"Relations\":[],\"Warnings\":[],\"Errors\":[]}";

    private readonly HashSet<string> added = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> dismissed = new(StringComparer.OrdinalIgnoreCase) { "Arma3:LaunchCM:Arma3" };
    private readonly List<(string Name, bool Axis, string Bind, List<(string Game, string Id, string Planes)> Nodes)> relations =
    [
        ("Flare", false, Stick + "::JOY_BTN5", [("DCS", "d3014pnil", "F-16C_50")]),
        ("Chaff", false, string.Empty, [("DCS", "d3015pnil", "F-16C_50")]),
        ("Hornet dispense", false, string.Empty, [("DCS", "d3033", "FA-18C_hornet")]),
        ("Empty one", false, string.Empty, []),
    ];

    // "Boat Aft" (AddBoatRelation): a button position, different functions per aircraft, until the user picks one.
    public (string State, string Label) BoatFunction { get; set; } = ("mixed", string.Empty);

    public void AddBoatRelation() => relations.Add(("Boat Aft", false, string.Empty, [("DCS", "d3014pnil", "F-16C_50"), ("DCS", "d3601", "AJS37"), ("DCS", "d3005", "F-5E-3")]));

    // "relations suggest <GateRelation>" waits for this (an engine run that takes a while).
    public string GateRelation { get; set; } = string.Empty;

    public TaskCompletionSource? SuggestGate { get; set; }

    public async Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        lock (Calls)
            Calls.Add(arguments.ToArray());
        if (SuggestGate != null && AxisTuningTests.Seq(arguments, "relations", "suggest", GateRelation))
            await SuggestGate.Task;
        return new EngineCommandResult
        {
            ExitCode = 0,
            Duration = TimeSpan.FromMilliseconds(1),
            StandardOutput = Output(arguments),
            StandardError = string.Empty,
            Arguments = arguments.ToArray()
        };
    }

    private static string After(IReadOnlyList<string> args, string word) => args[args.ToList().IndexOf(word) + 1];

    private static List<string> AllAfter(IReadOnlyList<string> args, string word) =>
        args.Select((a, i) => (a, i)).Where(p => p.a == word && p.i + 1 < args.Count).Select(p => args[p.i + 1]).ToList();

    private string Output(IReadOnlyList<string> args)
    {
        if (AxisTuningTests.Seq(args, "relations", "list"))
        {
            return "{\"Relations\":[" + string.Join(",", relations.Select(r =>
                "{\"Name\":\"" + r.Name + "\",\"IsAxis\":" + (r.Axis ? "true" : "false") + ",\"Groups\":[],\"Nodes\":[" +
                string.Join(",", r.Nodes.Select(n => "{\"Id\":\"" + n.Id + "\",\"Game\":\"" + n.Game + "\",\"Aircraft\":{\"" + n.Planes + "\":true}}")) + "]," +
                "\"Bind\":" + (r.Bind.Length == 0 ? "null" : "{\"Joystick\":\"" + r.Bind[..r.Bind.LastIndexOf("::", StringComparison.Ordinal)] + "\",\"JButton\":\"" +
                                                               r.Bind[(r.Bind.LastIndexOf("::", StringComparison.Ordinal) + 2)..] + "\"}") + "}")) + "]}";
        }
        if (AxisTuningTests.Seq(args, "relations", "show"))
        {
            string name = After(args, "show");
            var relation = relations.FirstOrDefault(r => r.Name == name);
            List<(string Game, string Id, string Planes)> nodes = relation.Nodes ?? [];
            return "Relation: " + name + Environment.NewLine + "Nodes:" + Environment.NewLine +
                   string.Join(Environment.NewLine, nodes.Select((n, i) => "  [" + (i + 1) + "] game=" + n.Game + ", id=" + n.Id + ", aircraft=" + n.Planes));
        }
        if (AxisTuningTests.Seq(args, "relations", "suggest", "apply"))
        {
            string name = After(args, "apply");
            var relation = relations.First(r => r.Name == name);
            for (int i = 0; i + 1 < args.Count; i++)
            {
                string spec = args[i + 1];
                string[] parts = spec.Split(':');
                switch (args[i])
                {
                    case "--add":
                    case "--take":
                        added.Add(spec);
                        relation.Nodes.Add((parts[0], parts[1], parts[2].Replace('|', ',')));
                        break;
                    case "--never":
                        dismissed.Add(spec);
                        break;
                    case "--remove":
                        dismissed.Remove(spec);
                        break;
                }
            }
            return "Relation: " + name + Environment.NewLine + "Added: 1";
        }
        if (AxisTuningTests.Seq(args, "relations", "browse"))
            return Browse(args);
        if (AxisTuningTests.Seq(args, "relations", "suggest", "--all", "--missing"))
            return "{\"Relations\":[{\"Relation\":\"Chaff\",\"IsAxis\":false,\"Suggestions\":2,\"High\":2,\"Planes\":1,\"Misfits\":0,\"NewGames\":1,\"UsedElsewhere\":3,\"Items\":[" +
                   Row("FalconBMS", "SimCMSDown", "STICK: CMS Down", "FalconBMS", "high", 95, "Same function", "") + "," +
                   Row("DCS", "d3016", "Countermeasures Aft", "F-15E", "high", 90, "Same command id", "") + "]}],\"Suggestions\":[]}";
        if (AxisTuningTests.Seq(args, "relations", "suggest", "--all"))
            return "{\"Relations\":[{\"Relation\":\"Chaff\",\"Suggestions\":5,\"High\":4,\"Planes\":9,\"Misfits\":0}],\"Suggestions\":[]}";
        if (AxisTuningTests.Seq(args, "relations", "suggest"))
        {
            string name = After(args, "suggest");
            if (name == "Pitch")
                return PitchSuggestions();
            if (name == "Boat Aft")
            {
                (string state, string label) = BoatFunction;
                return "{\"Relation\":\"Boat Aft\",\"Slot\":\"" + (label.Length > 0 ? "flapsdown" : string.Empty) + "\",\"SlotName\":\"" + label + "\",\"FunctionState\":\"" + state + "\"," +
                       "\"FunctionLabel\":\"" + label + "\",\"FunctionShare\":0.25,\"FunctionSetByUser\":" + (state is "set" or "cleared" ? "true" : "false") +
                       ",\"Functions\":[],\"Suggestions\":[],\"Misfits\":[],\"Dismissed\":[],\"Relations\":[],\"Warnings\":[],\"Errors\":[]}";
            }
            if (name != "Flare" && name != "Flares")
                return "{\"Relation\":\"" + name + "\",\"Slot\":\"\",\"SlotName\":\"\",\"Functions\":[],\"Suggestions\":[],\"Misfits\":[],\"Dismissed\":[],\"Relations\":[],\"Warnings\":[],\"Errors\":[]}";
            List<string> rows =
            [
                Row("FalconBMS", "SimCMSUp", "STICK: CMS Up", "FalconBMS", "high", 95, "Same function as F-16C_50 'Countermeasures Management Switch - Fwd'", ""),
                Row("DCS", "d3033", "Dispense Switch - Aft(FLARE)", "FA-18C_hornet", "high", 95, "Same function as F-16C_50 'Countermeasures Management Switch - Fwd'", "Hornet dispense"),
                Row("WarThunder", "ID_FLARES", "Fire countermeasures", "WarThunder", ExtraHigh ? "high" : "medium", ExtraHigh ? 90 : 80, "Your style: 'CMS FWD' where it is missing", ""),
                Row("DCS", "d77", "Countermeasures Continuously Dispense", "Mi-8MT", "low", 40, "Title match: 'Countermeasures Dispense' (72%)", ""),
            ];
            if (ExtraHigh)
                rows.Add(Row("DCS", "d3014", "Countermeasures Management Switch - Fwd", "F-14|F-15E", "high", 95, "Same function as F-16C_50 'Countermeasures Management Switch - Fwd'", ""));
            List<string> visible = rows.Where(r => !added.Any(a => r.Contains("\"Spec\":\"" + a + "\"")) && !dismissed.Any(d => r.Contains("\"Spec\":\"" + d + "\""))).ToList();
            string dismissedJson = string.Join(",", dismissed.Select(d =>
            {
                string[] p = d.Split(':');
                return "{\"Game\":\"" + p[0] + "\",\"Id\":\"" + p[1] + "\",\"Title\":\"" + p[1] + "\",\"Planes\":[\"" + p[2] + "\"]}";
            }));
            return "{\"Relation\":\"" + name + "\",\"Slot\":\"cms_fwd\",\"SlotName\":\"CMS FWD (Start Program or just Flare)\",\"IsAxis\":false," +
                   "\"Functions\":[{\"Slot\":\"cms_fwd\",\"Name\":\"CMS FWD (Start Program or just Flare)\",\"Weight\":1,\"Reference\":\"F-16C_50 'Countermeasures Management Switch - Fwd'\"}]," +
                   "\"Suggestions\":[" + string.Join(",", visible) + "]," +
                   "\"Misfits\":[{\"Game\":\"DCS\",\"Id\":\"d3015\",\"Title\":\"Countermeasures Management Switch - Aft\",\"Planes\":[\"F-16C_50\"],\"Reason\":\"The function catalog files it under 'CMS Aft'.\",\"BelongsTo\":\"CMS Aft\",\"MoveTo\":\"Chaff\"}]," +
                   "\"Dismissed\":[" + dismissedJson + "],\"Relations\":[],\"Warnings\":[],\"Errors\":[]}";
        }
        if (AxisTuningTests.Seq(args, "relations", "search"))
        {
            bool axisOnly = args.Contains("--axis");
            bool buttonOnly = args.Contains("--button");
            List<(bool Axis, string Json)> hits;
            string functions;
            if (args.Contains("throttle") || args.Contains("throtle"))
            {
                functions = "\"Throttle\"";
                hits =
                [
                    (true, Hit("DCS", "a2004cdnil", "Thrust", "Engine", true, "F-16C_50|F-15C", "function 'Throttle'", "")),
                    (true, Hit("DCS", "a2005cdnil", "Throttle Left", "Engine", true, "F-14", "function 'Throttle'", "")),
                    (true, Hit("IL2Game", "rpc_all_engines_throttles", "Engines throttle control", "Engine", true, "IL2Game", "function 'Throttle'", "")),
                    (true, Hit("StarCitizen", "v_strafe_longitudinal", "Strafe forward / backward", "spaceship_movement", true, "StarCitizen", "synonym 'thrust'", "")),
                    (false, Hit("DCS", "cutoff", "Throttle cutoff", "Engine", false, "F-16C_50", "", "")),
                ];
            }
            else
            {
                functions = "\"CMS FWD (Start Program or just Flare)\"";
                hits =
                [
                    (false, Hit("DCS", "d3084", "DLC Toggle / Countermeasure Dispense", "Stick", false, "F-14", "function 'CMS FWD'", "")),
                    (false, Hit("DCS", "d3001", "AN/ALE-39 Flare SGL", "RIO", false, "F-14_RIO", "", "Chaff")),
                    (false, Hit("DCS", "d3014pnil", "Countermeasures Management Switch - Fwd", "Stick", false, "F-16C_50", "function 'CMS FWD'", "Flare")),
                    (false, Hit("FalconBMS", "SimCMSUp", "STICK: CMS Up", "Stick", false, "FalconBMS", "function 'CMS FWD'", "")),
                    (false, Hit("WarThunder", "ID_FLARES", "Fire countermeasures", "Weapons", false, "WarThunder", "", "")),
                ];
            }
            HashSet<string> games = args.Contains("--game") ? After(args, "--game").Split('|').ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
            List<string> shown = hits.Where(h => (!axisOnly || h.Axis) && (!buttonOnly || !h.Axis))
                .Where(h => games.Count == 0 || games.Any(g => h.Json.Contains("\"Game\":\"" + g + "\"", StringComparison.Ordinal))).Select(h => h.Json).ToList();
            string didYouMean = args.Contains("throtle") ? "throttle" : string.Empty;
            return "{\"Query\":\"q\",\"DidYouMean\":\"" + didYouMean + "\",\"Functions\":[" + functions + "]," +
                   "\"FunctionGroups\":[{\"Function\":\"throttle\",\"Name\":\"Throttle\",\"Count\":4},{\"Function\":\"cm_dispense\",\"Name\":\"Countermeasures dispense\",\"Count\":3}]," +
                   "\"Results\":[" + string.Join(",", shown) + "],\"Total\":" + shown.Count + "}";
        }
        if (AxisTuningTests.Seq(args, "relations", "add"))
        {
            string name = After(args, "add");
            List<(string Game, string Id, string Planes)> nodes = AllAfter(args, "--node").Select(n => n.Split(':')).Select(n => (n[0], n[1], n[2].Replace('|', ','))).ToList();
            relations.Add((name, args.Contains("--axis"), args.Contains("--device") ? After(args, "--device") + "::" + After(args, "--input") : string.Empty, nodes));
            return "Relation: " + name;
        }
        if (AxisTuningTests.Seq(args, "relations", "rename"))
        {
            string from = After(args, "rename");
            string to = args[args.ToList().IndexOf("rename") + 2];
            int index = relations.FindIndex(r => r.Name == from);
            relations[index] = (to, relations[index].Axis, relations[index].Bind, relations[index].Nodes);
            return "Renamed: " + from + " -> " + to;
        }
        if (AxisTuningTests.Seq(args, "relations", "assistant"))
            return Assistant(args);
        if (AxisTuningTests.Seq(args, "relations", "function", "--list"))
            return "[{\"Id\":\"flapsdown\",\"Name\":\"Flaps Down\",\"IsAxis\":false,\"Kind\":\"\"},{\"Id\":\"airbrake_toggle\",\"Name\":\"Airbrake toggle\",\"IsAxis\":false,\"Kind\":\"\"}," +
                   "{\"Id\":\"cms_fwd\",\"Name\":\"CMS FWD (Start Program or just Flare)\",\"IsAxis\":false,\"Kind\":\"\"}]";
        if (AxisTuningTests.Seq(args, "relations", "function", "Boat Aft"))
        {
            BoatFunction = args.Contains("--set") ? ("set", After(args, "--set") == "flapsdown" ? "Flaps Down" : After(args, "--set"))
                : args.Contains("--none") ? ("cleared", string.Empty) : ("mixed", string.Empty);
            return "{\"Relation\":\"Boat Aft\",\"State\":\"" + BoatFunction.State + "\"}";
        }
        if (AxisTuningTests.Seq(args, "llm", "status"))
            return "{\"Enabled\":" + (LlmEnabled ? "true" : "false") + ",\"Ready\":" + (LlmEnabled ? "true" : "false") + ",\"SelectedModel\":\"qwen3-8b-q4_k_m\",\"Models\":[" +
                   "{\"Id\":\"qwen3-8b-q4_k_m\",\"Label\":\"Qwen3 8B\",\"Tier\":\"large\",\"Size\":\"4.68 GB\",\"License\":\"Apache-2.0\",\"LicenseUrl\":\"\",\"Source\":\"\",\"Installed\":true,\"Recommended\":true}]}";
        if (AxisTuningTests.Seq(args, "binds", "list"))
            return "{\"Binds\":[]}";
        return "ok";
    }

    // engine relations assistant: what the engine's tool loop reports (it changed the profile itself).
    private string Assistant(IReadOnlyList<string> args)
    {
        string message = args[args.ToList().IndexOf("assistant") + 1];
        string profile = args.Contains("--profile") ? After(args, "--profile") : string.Empty;
        bool offline = AxisTuningTests.Seq(args, "--llm", "off");
        string method = offline ? "offline brain (no AI model)" : "local AI model (qwen3-8b-q4_k_m)";
        if (message.Contains("Throttle ALL", StringComparison.Ordinal))
        {
            relations.Add(("Throttle ALL", true, string.Empty,
            [
                ("DCS", "a2004cdnil", "F-16C_50,F-15C"), ("DCS", "a2005cdnil", "F-14"), ("IL2Game", "rpc_all_engines_throttles", "IL2Game"),
                ("StarCitizen", "v_strafe_longitudinal", "StarCitizen"), ("StarCitizen", "v_ifcs_throttle", "StarCitizen")
            ]));
            // The engine saves the profile and records the version this command made.
            if (profile.Length > 0)
                SaveVersion(profile, "after the assistant", "Assistant: Throttle ALL");
            return "{\"Message\":\"m\",\"FocusRelation\":\"Throttle ALL\",\"Method\":\"" + method + "\",\"Finished\":true,\"Changed\":true,\"NeedsModel\":false," +
                   "\"Reply\":\"I created Throttle ALL with the throttle axes, the collectives and the spaceship forward thrust.\",\"Summary\":\"\"," +
                   "\"Relations\":[{\"Name\":\"Throttle ALL\",\"Created\":true,\"RenamedFrom\":\"\",\"Added\":{\"DCS\":12,\"IL-2\":1,\"Star Citizen\":2},\"Removed\":{},\"Actions\":5,\"AlsoIn\":[]}]," +
                   "\"Changes\":[],\"Steps\":[" +
                   "{\"Step\":1,\"Tool\":\"resolve\",\"Call\":\"resolve(\\\"throttle\\\", axis)\",\"Result\":\"RESULT S1 resolve: 3 actions\",\"Ok\":true,\"Error\":\"\",\"Rejected\":[],\"Skipped\":[]}," +
                   "{\"Step\":2,\"Tool\":\"create_relation\",\"Call\":\"create_relation(\\\"Throttle ALL\\\", axis)\",\"Result\":\"RESULT created\",\"Ok\":true,\"Error\":\"\",\"Rejected\":[],\"Skipped\":[]}," +
                   "{\"Step\":3,\"Tool\":\"add_to_relation\",\"Call\":\"add_to_relation(\\\"Throttle ALL\\\", S1,A999)\",\"Result\":\"RESULT added 3 actions\",\"Ok\":true,\"Error\":\"\",\"Rejected\":[\"A999\"],\"Skipped\":[]}," +
                   "{\"Step\":4,\"Tool\":\"done\",\"Call\":\"done()\",\"Result\":\"\",\"Ok\":true,\"Error\":\"\",\"Rejected\":[],\"Skipped\":[]}]," +
                   "\"Rejected\":[\"A999\"],\"Llm\":{\"Mode\":\"auto\",\"Used\":" + (offline ? "false" : "true") + ",\"Note\":\"\"},\"Warnings\":[],\"Errors\":[]}";
        }
        if (args.Contains("--relation"))
        {
            string relation = After(args, "--relation");
            int index = relations.FindIndex(r => r.Name == relation);
            relations[index].Nodes.Add(("DCS", "d3020", "F-15E"));
            return "{\"FocusRelation\":\"" + relation + "\",\"Method\":\"" + method + "\",\"Finished\":true,\"Changed\":true,\"Reply\":\"Added the program buttons.\"," +
                   "\"Relations\":[{\"Name\":\"" + relation + "\",\"Created\":false,\"RenamedFrom\":\"\",\"Added\":{\"DCS\":2},\"Removed\":{},\"Actions\":2,\"AlsoIn\":[]}]," +
                   "\"Steps\":[],\"Rejected\":[],\"Llm\":{\"Used\":true},\"Warnings\":[],\"Errors\":[]}";
        }
        return "{\"FocusRelation\":\"\",\"Method\":\"" + method + "\",\"Finished\":false,\"Changed\":false,\"NeedsModel\":true," +
               "\"Reply\":\"Without the local AI model I understand simple commands: \\\"Throttle ALL: all throttle axes\\\". Free sentences need the local AI model.\"," +
               "\"Relations\":[],\"Steps\":[],\"Rejected\":[],\"Llm\":{\"Used\":false,\"Note\":\"The local AI model is off.\"},\"Warnings\":[],\"Errors\":[]}";
    }

    // A save as the engine does it: the file, then its version in the profile history.
    internal static void SaveVersion(string profile, string content, string label)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        JoyPro.History.ProfileHistory.WriteFileAtomic(profile, bytes);
        JoyPro.History.ProfileHistory.Record(profile, bytes, new JoyPro.History.ProfileHistoryRecordOptions { Label = label, Coalesce = false });
    }

    private static string Hit(string game, string id, string title, string category, bool axis, string planes, string why, string inRelation) =>
        "{\"Game\":\"" + game + "\",\"Id\":\"" + id + "\",\"Title\":\"" + title + "\",\"Category\":\"" + category + "\",\"IsAxis\":" + (axis ? "true" : "false") +
        ",\"Planes\":[" + string.Join(",", planes.Split('|').Select(p => "\"" + p + "\"")) + "],\"Score\":99,\"Why\":\"" + why + "\",\"InRelations\":[" +
        (inRelation.Length > 0 ? "\"" + inRelation + "\"" : string.Empty) + "]" +
        ",\"Match\":\"" + (why.StartsWith("function", StringComparison.Ordinal) ? "function" : why.StartsWith("synonym", StringComparison.Ordinal) ? "synonym" : "words") + "\"" +
        ",\"Function\":\"" + (why.StartsWith("function 'Throttle'", StringComparison.Ordinal) || why.StartsWith("synonym", StringComparison.Ordinal) ? "throttle" : why.StartsWith("function", StringComparison.Ordinal) ? "cm_dispense" : string.Empty) + "\"}";

    private static string Row(string game, string id, string title, string planes, string confidence, int score, string why, string other) =>
        "{\"Game\":\"" + game + "\",\"Id\":\"" + id + "\",\"Title\":\"" + title + "\",\"Spec\":\"" + game + ":" + id + ":" + planes + "\",\"IsAxis\":false," +
        "\"Planes\":[" + string.Join(",", planes.Split('|').Select(p => "\"" + p + "\"")) + "],\"Confidence\":\"" + confidence + "\",\"Score\":" + score + "," +
        "\"Source\":\"catalog\",\"Why\":\"" + why + "\",\"Reasons\":[\"" + why + " (function catalog, checked against the manuals).\"]," +
        "\"InOtherRelations\":[" + (other.Length > 0 ? "\"" + other + "\"" : string.Empty) + "]," +
        // "Flare" has F-16C_50 only: another game is a new game, other DCS aircraft are new aircraft.
        "\"Missing\":\"" + (game == "DCS" ? "aircraft" : "game") + "\",\"NewGame\":" + (game == "DCS" ? "false" : "true") +
        ",\"NewPlanes\":[" + string.Join(",", planes.Split('|').Select(p => "\"" + p + "\"")) + "]}";

    // relations browse: the games (DCS with two aircraft), or one aircraft's list by category.
    private static string Browse(IReadOnlyList<string> args)
    {
        string games = "\"Games\":[{\"Game\":\"DCS\",\"Actions\":4,\"InRelations\":1,\"Planes\":[{\"Plane\":\"A-10C\",\"Actions\":3,\"InRelations\":0},{\"Plane\":\"F-16C_50\",\"Actions\":3,\"InRelations\":1}]}," +
                       "{\"Game\":\"IL2Game\",\"Actions\":2,\"InRelations\":0,\"Planes\":[]}]";
        if (!args.Contains("--game"))
            return "{" + games + ",\"Game\":\"\",\"Categories\":[],\"Results\":[]}";
        string game = After(args, "--game");
        string plane = args.Contains("--plane") ? After(args, "--plane") : game;
        List<string> hits = game == "DCS"
            ?
            [
                Hit("DCS", "d3014pnil", "Countermeasures Management Switch - Fwd", "HOTAS; Stick", false, plane, "", plane == "F-16C_50" ? "Flare" : ""),
                Hit("DCS", "d3015pnil", "Countermeasures Management Switch - Aft", "HOTAS", false, plane, "", plane == "F-16C_50" ? "Chaff" : ""),
                Hit("DCS", "d68", "Landing Gear Up", "Landing gear panel", false, plane, "", ""),
            ]
            :
            [
                Hit("IL2Game", "gear_toggle", "Landing gear", "Aircraft systems", false, "IL2Game", "", ""),
                Hit("IL2Game", "flares", "Flares", "Weapons", false, "IL2Game", "", ""),
            ];
        List<string> categories = game == "DCS"
            ? ["{\"Name\":\"HOTAS\",\"Count\":2,\"InRelations\":0}", "{\"Name\":\"Landing gear panel\",\"Count\":1,\"InRelations\":0}", "{\"Name\":\"Stick\",\"Count\":1,\"InRelations\":0}"]
            : ["{\"Name\":\"Aircraft systems\",\"Count\":1,\"InRelations\":0}", "{\"Name\":\"Weapons\",\"Count\":1,\"InRelations\":0}"];
        return "{" + games + ",\"Game\":\"" + game + "\",\"CategorySource\":\"" + (game == "DCS" ? "game" : "joypro") + "\",\"Categories\":[" + string.Join(",", categories) + "],\"Results\":[" + string.Join(",", hits) + "]}";
    }
}
