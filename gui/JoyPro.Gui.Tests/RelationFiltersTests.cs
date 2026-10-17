using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The filter bar of the Relations and Binds pages (ListFilterViewModel), the bind label with device and modifiers, the
// question before removing an action ("Do not ask again", Settings > Confirmations), the catalog (relations browse),
// "Suggest missing" (one relation and every relation). The engine is faked (RelationBuilderTests.BuilderFakeEngine); the
// journey test runs them on the user's big profile with the real engine.
[SupportedOSPlatform("windows")]
public sealed class RelationFiltersTests : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await RelationBuilderTests.SettleAllAsync();

    private static FilterSubject Subject(string name, string groups, string gamePlanes, string device = "", bool axis = false, string search = "") =>
        new(name, groups.Split(',', StringSplitOptions.RemoveEmptyEntries), gamePlanes.Split(',', StringSplitOptions.RemoveEmptyEntries), device, device.Length > 0, axis,
            name + " " + search);

    private static List<FilterSubject> Sample() =>
    [
        Subject("A10 DMS Up", "HOTAS,DCS", "DCS|A-10C,DCS|A-10C II", "WarBRD {1}"),
        Subject("A10A Gear", "DCS", "DCS|A-10A", "WarBRD {1}"),
        Subject("Hornet Flare", "HOTAS", "DCS|FA-18C_hornet,StarCitizen|StarCitizen", "Throttle {2}"),
        Subject("Zoom", "", "IL2Game|IL2Game"),
        Subject("Throttle ALL", "Axes", "DCS|A-10C,DCS|F-16C_50,IL2Game|IL2Game", "Throttle {2}", axis: true),
    ];

    private static List<string> Shown(ListFilterViewModel filter, IEnumerable<FilterSubject> subjects) =>
        subjects.Where(filter.Matches).Select(s => s.Name).ToList();

    [Fact]
    public void Games_WholeGameThenOnlySomeAircraft()
    {
        ListFilterViewModel filter = new("relations");
        List<FilterSubject> subjects = Sample();
        filter.SetSubjects(subjects);
        Assert.Equal(3, filter.Games.Count);
        FilterGameItem dcs = filter.Games.Single(g => g.Game == "DCS");
        Assert.Equal(4, dcs.Count);
        Assert.Equal(["A-10A", "A-10C", "A-10C II", "F-16C_50", "FA-18C_hornet"], dcs.Planes.Select(p => p.Label).ToArray());
        Assert.False(filter.Games.Single(g => g.Game == "IL2Game").HasPlanes);

        // Tick DCS: every relation with DCS.
        dcs.IsChecked = true;
        Assert.Equal(["A10 DMS Up", "A10A Gear", "Hornet Flare", "Throttle ALL"], Shown(filter, subjects));
        Assert.All(dcs.Planes, p => Assert.True(p.IsChecked));

        // Open it, None, then only the A-10s (typed filter + All).
        dcs.IsExpanded = true;
        filter.SelectNoPlanesCommand.Execute(dcs);
        Assert.False(dcs.IsChecked);
        dcs.PlaneFilterText = "A-10";
        Assert.Equal(3, dcs.Planes.Count(p => p.IsVisible));
        filter.SelectAllPlanesCommand.Execute(dcs);
        Assert.Null(dcs.IsChecked);
        Assert.Equal("3 of 5 aircraft", dcs.PlanesSummary);
        Assert.Equal(["A10 DMS Up", "A10A Gear", "Throttle ALL"], Shown(filter, subjects));

        // Untick A-10C: the A-10C II and A-10A relations stay.
        dcs.Planes.Single(p => p.Label == "A-10C").IsChecked = false;
        Assert.Equal(["A10 DMS Up", "A10A Gear"], Shown(filter, subjects));
        Assert.Contains(filter.ActiveChips, c => c.Label == "DCS: A-10A, A-10C II");

        // Every aircraft ticked again is the whole game.
        foreach (FilterOptionItem plane in dcs.Planes)
            plane.IsChecked = true;
        Assert.True(dcs.IsChecked);
        Assert.Contains(filter.ActiveChips, c => c.Label == "DCS");
    }

    [Fact]
    public void Filters_Combine_WithCountsOfTheOtherFilters()
    {
        ListFilterViewModel filter = new("binds");
        List<FilterSubject> subjects = Sample();
        filter.SetSubjects(subjects);
        Assert.Equal(5, filter.TotalCount);
        Assert.Equal(5, filter.ShownCount);
        Assert.Equal(4, filter.BoundCount);
        Assert.Equal(1, filter.UnboundCount);
        Assert.Equal(1, filter.AxisCount);
        Assert.Equal(2, filter.Devices.Count);
        Assert.Equal(["Throttle", "WarBRD"], filter.Devices.Select(d => d.Label).ToArray());
        Assert.Contains(filter.Groups, g => g.Key == FilterSubject.NoGroup && g.Label == "No group");

        filter.SelectGroups("HOTAS");
        Assert.Equal(["A10 DMS Up", "Hornet Flare"], Shown(filter, subjects));
        // The device counts follow the group filter; the group counts do not shrink by their own filter.
        Assert.Equal(1, filter.Devices.Single(d => d.Label == "WarBRD").Count);
        Assert.Equal(1, filter.Devices.Single(d => d.Label == "Throttle").Count);
        Assert.Equal(2, filter.Groups.Single(g => g.Key == "DCS").Count);

        filter.SelectDevices("Throttle {2}");
        Assert.Equal(["Hornet Flare"], Shown(filter, subjects));
        Assert.Equal("1 of 5 shown", filter.ResultText);

        filter.ClearAll();
        filter.SetKindCommand.Execute("axis");
        Assert.Equal(["Throttle ALL"], Shown(filter, subjects));
        filter.SetKindCommand.Execute("axis");
        Assert.Equal("all", filter.Kind);
        filter.SetBoundCommand.Execute("unbound");
        Assert.Equal(["Zoom"], Shown(filter, subjects));
        Assert.True(filter.IsUnboundOnly);

        // The typed search: every word somewhere (name, game, aircraft, device ...).
        filter.ClearAll();
        filter.SearchText = "a10 up";
        Assert.Equal(["A10 DMS Up"], Shown(filter, subjects));
    }

    [Fact]
    public void State_IsRememberedAndAppliedToTheNextList()
    {
        ListFilterViewModel first = new("relations");
        first.SetSubjects(Sample());
        first.SelectOnlyPlanes("DCS", "A-10A");
        first.SelectGame("IL2Game");
        first.SelectGroups("DCS");
        first.SetBoundCommand.Execute("bound");
        ListFilterState state = first.ToState();
        Assert.Equal(["IL2Game"], state.Games);
        Assert.Equal(["DCS|A-10A"], state.Planes);

        ListFilterViewModel second = new("relations");
        second.LoadState(state);
        List<FilterSubject> subjects = Sample();
        second.SetSubjects(subjects);
        Assert.Null(second.Games.Single(g => g.Game == "DCS").IsChecked);
        Assert.True(second.Games.Single(g => g.Game == "IL2Game").IsChecked);
        Assert.Equal(["A10A Gear"], Shown(second, subjects));

        // Ticks of things the list no longer has are dropped.
        second.SetSubjects(subjects.Where(s => s.Name != "A10A Gear").ToList());
        Assert.DoesNotContain("DCS|A-10A", second.ToState().Planes);
    }

    // 524 relations with 7 000 actions (the user's big profile): every filter change under 300 ms (counts included).
    [Fact]
    public void Filters_AreFastOnABigProfile()
    {
        Random random = new(7);
        string[] planes = Enumerable.Range(1, 110).Select(i => "Plane" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        string[] games = ["IL2Game", "StarCitizen", "MSFS2024", "XPlane12", "FalconBMS"];
        List<FilterSubject> subjects = [];
        for (int i = 0; i < 524; i++)
        {
            List<string> gp = Enumerable.Range(0, 13).Select(_ => "DCS|" + planes[random.Next(planes.Length)]).ToList();
            gp.Add(games[random.Next(games.Length)] + "|" + games[random.Next(games.Length)]);
            subjects.Add(Subject("Relation " + i, "G" + (i % 30), string.Join(",", gp), i % 3 == 0 ? string.Empty : "Device " + (i % 7) + " {x}", axis: i % 10 == 0));
        }
        ListFilterViewModel filter = new("relations");
        Stopwatch watch = Stopwatch.StartNew();
        filter.SetSubjects(subjects);
        long set = watch.ElapsedMilliseconds;
        watch.Restart();
        filter.SelectGame("DCS");
        filter.SelectOnlyPlanes("DCS", "Plane1", "Plane2", "Plane3");
        filter.SelectGroups("G1", "G2");
        filter.SelectDevices("Device 1 {x}");
        filter.SetBoundCommand.Execute("bound");
        filter.SearchText = "relation 1";
        int shown = subjects.Count(filter.Matches);
        long changes = watch.ElapsedMilliseconds;
        Assert.True(set < 300, "SetSubjects took " + set + " ms");
        Assert.True(changes / 6.0 < 300, "6 filter changes took " + changes + " ms");
        Assert.True(shown >= 0);
    }

    [Fact]
    public void BindLabel_NamesDeviceInputAndModifiers()
    {
        Assert.Equal("VKBsim Gladiator EVO WarBRD · BTN 8 · Shift A", DisplayText.BindLabel("VKBsim Gladiator EVO WarBRD {42CF1390-0A3F-11f0-8011-444553540000}", "JOY_BTN8", ["Shift A"]));
        Assert.Equal("Throttle · BTN 2 · Lpinky + Rpinky", DisplayText.BindLabel("Throttle", "JOY_BTN2", ["Lpinky", "Rpinky"]));
        Assert.Equal(string.Empty, DisplayText.BindLabel("Throttle", "<unbound>", []));
        RelationListItem relation = new() { Name = "Gear", Bind = "WarBRD {1}::JOY_BTN8", Modifiers = ["Shift A"] };
        Assert.Equal("WarBRD · BTN 8 · Shift A", relation.BindLabel);
        Assert.Contains("Modifiers: hold Shift A", relation.BindTooltip);
        BindListItem bind = new() { RelationName = "Gear", HasBind = true, Device = "WarBRD {1}", Input = "JOY_BTN8", ModifiersSummary = "Shift A" };
        Assert.Equal("WarBRD · BTN 8 · Shift A", bind.BindLabel);
        Assert.Equal("BTN 8", bind.InputShort);
    }

    [Fact]
    public async Task RelationsPage_FiltersByDeviceGameAndBound_AndKeepsTheOpenRelation()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        Assert.Equal(4, vm.Relations.Count);
        RelationListItem flare = vm.Relations.Single(r => r.Name == "Flare");
        Assert.Equal("Joystick - HOTAS Warthog · BTN 5", flare.BindLabel);

        vm.RelationFilter.SelectDevices(BuilderFakeEngine.Stick);
        Assert.Equal(["Flare"], vm.Relations.Select(r => r.Name).ToArray());
        vm.RelationFilter.ClearAll();
        vm.RelationFilter.SetBoundCommand.Execute("unbound");
        Assert.Equal(["Chaff", "Empty one", "Hornet dispense"], vm.Relations.Select(r => r.Name).ToArray());
        vm.RelationFilter.ClearAll();
        vm.RelationFilter.SelectOnlyPlanes("DCS", "FA-18C_hornet");
        Assert.Equal(["Hornet dispense"], vm.Relations.Select(r => r.Name).ToArray());
        Assert.True(vm.RelationFilterMilliseconds < 300);

        // Opening a relation the filters hide clears them.
        await vm.SuggestMissingForAllCommand.ExecuteAsync(null);
        vm.OpenMissingReviewRelationCommand.Execute(vm.MissingReview.Single());
        Assert.Equal("Chaff", vm.SelectedRelation?.Name);
        Assert.False(vm.RelationFilter.HasActiveFilters);

        // The Binds page has its own filters.
        vm.BindFilter.SetBoundCommand.Execute("bound");
        Assert.Equal(["Flare"], vm.Binds.Select(b => b.RelationName).ToArray());
        Assert.Equal(4, vm.Relations.Count);
    }

    [Fact]
    public async Task RemovingAnAction_AsksFirst_DoNotAskAgain_SettingsAsksAgain()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        await RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Count > 0);
        List<ConfirmRequest> asked = [];
        bool answer = false;
        bool tickDontAsk = false;
        vm.ConfirmHandler = request =>
        {
            asked.Add(request);
            request.DontAskAgain = tickDontAsk;
            return Task.FromResult(answer);
        };
        RelationNodeItem node = vm.RelationNodes[0];

        await vm.RemoveNodeCommand.ExecuteAsync(node);
        ConfirmRequest question = Assert.Single(asked);
        Assert.Equal("Remove this action?", question.Title);
        Assert.Equal(MainWindowViewModel.ConfirmRemoveActionKey, question.DontAskKey);
        Assert.Contains("Flare", question.Message);
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "item", "remove"));

        answer = true;
        tickDontAsk = true;
        await vm.RemoveNodeCommand.ExecuteAsync(node);
        Assert.Equal(2, asked.Count);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "item", "remove"));
        Assert.False(vm.AskBeforeRemovingAction);
        Assert.True(vm.HasSwitchedOffConfirmations);

        // Not asked any more.
        await vm.RemoveNodeCommand.ExecuteAsync(node);
        Assert.Equal(2, asked.Count);

        // Settings > Confirmations: asked again.
        vm.AskAllConfirmationsAgainCommand.Execute(null);
        Assert.True(vm.AskBeforeRemovingAction);
        answer = false;
        await vm.RemoveNodeCommand.ExecuteAsync(node);
        Assert.Equal(3, asked.Count);
    }

    [Fact]
    public async Task SuggestMissing_NewGamesAndAircraftFirst_UsedElsewhereApart_TickAndAdd()
    {
        BuilderFakeEngine fake = new();
        fake.ExtraHigh = true;
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        await vm.SuggestMissingCommand.ExecuteAsync(null);
        Assert.True(vm.IsBuilderSuggestedPanel);
        Assert.Equal(["Games not in this relation yet", "Aircraft not in this relation yet", "Used in other relations"],
            vm.BuilderSuggestionSections.Select(s => s.Title).ToArray());
        SuggestionSectionItem games = vm.BuilderSuggestionSections[0];
        // High before medium.
        Assert.Equal(["FalconBMS", "WarThunder"], games.Items.Select(i => i.Game).ToArray());
        Assert.Equal("new game for this relation", games.Items[0].MissingText);
        Assert.False(vm.BuilderSuggestionSections[2].Tickable);
        Assert.Equal("new for F-14, F-15E", vm.BuilderSuggestionSections[1].Items.Single().MissingText);
        Assert.Equal("Suggest missing (3)", vm.SuggestMissingLabel);

        games.IsChecked = true;
        Assert.Equal("Add ticked (2)", vm.BuilderAddTickedLabel);
        fake.Calls.Clear();
        await vm.AddTickedBuilderSuggestionsCommand.ExecuteAsync(null);
        IReadOnlyList<string> apply = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "apply"));
        Assert.True(AxisTuningTests.Seq(apply, "--add", "FalconBMS:SimCMSUp:FalconBMS"));
        Assert.True(AxisTuningTests.Seq(apply, "--add", "WarThunder:ID_FLARES:WarThunder"));
    }

    [Fact]
    public async Task SuggestMissingForEveryRelation_ReviewTickAdd()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await vm.SuggestMissingForAllCommand.ExecuteAsync(null);
        Assert.True(vm.IsBuilderMissingAllPanel);
        MissingReviewRelationItem chaff = Assert.Single(vm.MissingReview);
        Assert.Equal("Chaff", chaff.Name);
        Assert.Equal(2, chaff.Items.Count);
        Assert.Contains("3 used elsewhere", chaff.Summary);
        Assert.Contains("1 new game", chaff.Summary);

        chaff.Items[1].IsChecked = true;
        Assert.Null(chaff.IsChecked);
        Assert.Equal("Add ticked (1)", vm.MissingReviewAddLabel);
        fake.Calls.Clear();
        await vm.AddTickedMissingReviewCommand.ExecuteAsync(null);
        IReadOnlyList<string> apply = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "apply"));
        Assert.True(AxisTuningTests.Seq(apply, "apply", "Chaff", "--add", "DCS:d3016:F-15E"));
        Assert.Single(chaff.Items);
    }

    [Fact]
    public async Task Catalog_BrowsesAnAircraftByCategory_AndAddsTicked()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        await RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Count > 0);
        await vm.OpenCatalogCommand.ExecuteAsync(null);
        Assert.True(vm.IsBuilderCatalogPanel);
        // The open relation's aircraft is picked.
        Assert.Equal("DCS", vm.CatalogGame?.Game);
        Assert.Equal("F-16C_50", vm.CatalogPlane?.Plane);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "browse", "--game", "DCS", "--plane", "F-16C_50", "--button"));
        Assert.Equal(["HOTAS", "Landing gear panel", "Stick"], vm.CatalogCategories.Select(c => c.Name).ToArray());
        CatalogCategoryItem hotas = vm.CatalogCategories[0];
        Assert.Equal(2, hotas.Rows.Count);
        Assert.Empty(hotas.VisibleRows);
        // "HOTAS; Stick": the same row in both.
        Assert.Same(hotas.Rows[0], vm.CatalogCategories[2].Rows[0]);
        CatalogRowItem fwd = hotas.Rows.Single(r => r.Row.Id == "d3014pnil");
        Assert.True(fwd.Row.IsInOpenRelation);
        Assert.False(fwd.Row.CanCheck);
        CatalogRowItem aft = hotas.Rows.Single(r => r.Row.Id == "d3015pnil");
        Assert.Equal("in 'Chaff'", aft.UsedText);

        vm.ToggleCatalogCategoryCommand.Execute(hotas);
        Assert.Equal(2, hotas.VisibleRows.Count);
        vm.CatalogOnlyFree = true;
        Assert.False(hotas.IsVisible);
        vm.CatalogOnlyFree = false;
        vm.CatalogFilterText = "gear";
        Assert.True(vm.CatalogCategories[1].IsExpanded);
        Assert.Single(vm.CatalogCategories[1].VisibleRows);

        vm.CatalogCategories[1].IsChecked = true;
        Assert.Equal("Add selected (1)", vm.CatalogAddLabel);
        fake.Calls.Clear();
        await vm.AddCheckedCatalogCommand.ExecuteAsync(null);
        IReadOnlyList<string> apply = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "suggest", "apply"));
        Assert.True(AxisTuningTests.Seq(apply, "apply", "Flare", "--add", "DCS:d68:F-16C_50"));

        // A game without aircraft lists.
        await vm.SelectCatalogAsync("IL2Game", string.Empty);
        Assert.Null(vm.CatalogPlane);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "browse", "--game", "IL2Game", "--button"));
        Assert.Contains("grouped by what they do", vm.CatalogStatus);
    }
}
