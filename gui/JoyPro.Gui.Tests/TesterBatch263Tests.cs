using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The 2.6.3 tester reports:
// 1. imacken: "F1 Cockpit view" searched in its own relation looked like a new action. The relation has it for 19 of the
//    row's 20 aircraft (the 20th of the node is ground_unit, the row's su-25T is not in it): such a row stays addable, says
//    how many are in and offers only the others; with all of them it is greyed "already in this relation".
// 2. Speed of heat: the row's reason when a number or side typed is missing; the title's highlight for "OSB15".
// 3. Speed of heat: the game chips of Add actions stay for another relation, a new search and a restart; nothing in the
//    chosen games is said plainly with one click to show all games; the Relations page's left filter stays as well.
// 4. Speed of heat: "Which grip?" on pedals: "Which model?" for one USB id of several products, a click opens the chooser
//    on the Layout page, the answer stays after a restart; "MFG Crosswind V2/3" names both models: nothing to ask.
// Pictures with JOYPRO_263_RENDER_OUT=<folder> and JOYPRO_263_ENGINE=<engine.exe> (the real engine's search, sandboxed).
[SupportedOSPlatform("windows")]
public sealed class TesterBatch263Tests
{
    private const string F1Id = "d7pnilunilcdnilvdnilvpnilvunil";

    // The tester's installed aircraft (his bundle): the row has su-25T, the node ground_unit instead.
    private static readonly string[] F1Common =
    [
        "A-10C II", "AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT", "AJS37", "AV8BNA", "Bf-109K-4", "Default", "F-14", "F-14_RIO", "F-16C_50", "F-4E-45MC",
        "F-4E-45MC_WSO", "F-5E-3", "FA-18C_hornet", "Ka-50 III", "M-2000C", "P-51D", "SpitfireLFMkIX", "TF-51D"
    ];

    private static string Hit(string game, string id, string title, string section, bool recommended, string[] planes, string relatedBy = "", string missing = "", string variant = "", string function = "mfd_button")
    {
        string list = string.Join(",", planes.Select(p => "\"" + p + "\""));
        string variants = game == "DCS" ? "[{\"Title\":\"" + title + "\",\"Category\":\"View\",\"Match\":\"same\",\"Note\":\"\",\"Included\":true,\"Planes\":[" + list + "]}]" : "[]";
        return "{\"Game\":\"" + game + "\",\"Id\":\"" + id + "\",\"Title\":\"" + title + "\",\"Category\":\"View\",\"IsAxis\":false,\"Planes\":[" + list + "],\"Variants\":" + variants +
               ",\"Score\":900,\"Literal\":" + (section == "name" ? "true" : "false") + ",\"Why\":\"\",\"Match\":\"" + (section == "name" ? "function" : "synonym") + "\",\"Function\":\"" + function + "\"" +
               ",\"Display\":\"" + title + "\",\"InRelations\":[],\"Recommended\":" + (recommended ? "true" : "false") + ",\"Variant\":\"" + variant + "\",\"Section\":\"" + section + "\"" +
               ",\"Understood\":\"MFD button (OSB)\",\"UnderstoodBy\":\"synonym\",\"QueryTerm\":\"osb\",\"ActionFunction\":\"MFD button (OSB)\",\"RelatedBy\":\"" + relatedBy + "\"" +
               ",\"MissingWords\":[" + (missing.Length > 0 ? "\"" + missing + "\"" : string.Empty) + "]}";
    }

    private static string Result(int total, params string[] hits) =>
        "{\"Query\":\"q\",\"Total\":" + total + ",\"Functions\":[],\"Resolved\":[],\"Results\":[" + string.Join(",", hits) + "],\"Groups\":[" +
        string.Join(",", hits.Select(h => h[(h.IndexOf("\"Game\":\"", StringComparison.Ordinal) + 8)..]).Select(h => h[..h.IndexOf('"')]).GroupBy(g => g)
            .Select(g => "{\"Game\":\"" + g.Key + "\",\"Count\":" + g.Count() + ",\"Total\":" + g.Count() + "}")) + "],\"FunctionGroups\":[],\"Warnings\":[],\"Errors\":[]}";

    private static BuilderFakeEngine F1Engine(params string[] nodePlanes)
    {
        BuilderFakeEngine fake = new();
        fake.AddRelation("F1 Cockpit view", false, string.Empty, ("DCS", F1Id, string.Join(",", nodePlanes)));
        fake.AddRelation("Second relation", false, string.Empty, ("DCS", "d9pnil", "F-16C_50"));
        fake.SearchOverride = _ => Result(1, Hit("DCS", F1Id, "F1 Cockpit view", "name", true, [.. F1Common, "su-25T"], function: "view"));
        return fake;
    }

    [Fact]
    public async Task PartlyInTheRelation_StaysAddable_SaysHowMany_AndOffersOnlyTheOthers()
    {
        BuilderFakeEngine fake = F1Engine([.. F1Common, "ground_unit"]);
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "batch263-partly.pr0file");
        await RelationBuilderTests.OpenAsync(vm, "F1 Cockpit view");
        vm.BuilderSearchText = "f1 cockpit";
        await RelationBuilderTests.WaitForAsync(() => vm.BuilderLastSearchedQueryForTests == "f1 cockpit" && !vm.IsBuilderSearching);

        BuilderSearchResultItem row = vm.BuilderSearchResults.Single();
        // Not "already in this relation" (su-25T is free), no plain new action either.
        Assert.False(row.IsInOpenRelation);
        Assert.True(row.IsPartlyInOpenRelation);
        Assert.True(row.CanCheck);
        Assert.Equal("already in this relation for 19 of 20 aircraft", row.InOtherRelationsText);
        Assert.True(row.HasNote);
        // Only the free aircraft are offered, and what is added is only them.
        Assert.Equal(["su-25T"], row.SelectedPlanes);
        Assert.Equal("DCS:" + F1Id + ":su-25T", row.NodeSpec);
        Assert.Equal("su-25T", row.PlanesText);
        Assert.Equal("1 of 1 free aircraft", row.Aircraft!.Badge);
        AircraftChoiceItem inside = row.Aircraft.Items.Single(i => i.Plane == "AJS37");
        Assert.True(inside.IsInOpenRelation);
        Assert.False(inside.CanChange);
        Assert.False(inside.IsChecked);
        Assert.Equal("already in this relation", inside.MarkerText);
        // All / Invert leave them alone; the row is not ticked by the marks themselves.
        row.Aircraft.SelectAll();
        Assert.Equal(["su-25T"], row.Aircraft.CheckedPlanes);
        row.IsChecked = false;
        // It is listed with the actions that can be added, not in the greyed group.
        Assert.DoesNotContain(vm.BuilderSearchRows.OfType<BuilderSearchGroupItem>(), g => g.IsInRelationGroup);
        Assert.Contains(row, vm.BuilderSearchRows.OfType<BuilderSearchResultItem>());

        // Another relation open: the row's whole proposal is back.
        await RelationBuilderTests.OpenAsync(vm, "Second relation");
        vm.BuilderSearchText = "f1 cockpit";
        await RelationBuilderTests.WaitForAsync(() => vm.BuilderLastSearchedQueryForTests == "f1 cockpit" && !vm.IsBuilderSearching && vm.BuilderSearchResults.Count == 1);
        row = vm.BuilderSearchResults.Single();
        Assert.False(row.IsPartlyInOpenRelation);
        Assert.Equal(20, row.SelectedPlanes.Count);
        Assert.Equal(string.Empty, row.InOtherRelationsText);
    }

    [Fact]
    public async Task WhollyInTheRelation_IsGreyedAtTheBottom()
    {
        BuilderFakeEngine fake = F1Engine([.. F1Common, "su-25T", "ground_unit"]);
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "batch263-whole.pr0file");
        await RelationBuilderTests.OpenAsync(vm, "F1 Cockpit view");
        vm.BuilderSearchText = "f1 cockpit";
        await RelationBuilderTests.WaitForAsync(() => vm.BuilderLastSearchedQueryForTests == "f1 cockpit" && !vm.IsBuilderSearching);
        BuilderSearchResultItem row = vm.BuilderSearchResults.Single();
        Assert.True(row.IsInOpenRelation);
        Assert.False(row.CanCheck);
        Assert.Equal("already in this relation", row.InOtherRelationsText);
        Assert.Contains(vm.BuilderSearchRows.OfType<BuilderSearchGroupItem>(), g => g.IsInRelationGroup);
        Assert.Contains("1 already in this relation", vm.BuilderSearchStatus);
    }

    // ---- 2: what a row says when it lacks a word, and the highlight ---------------------------------------------------

    [Fact]
    public void RowWithoutTheTypedWord_SaysSo()
    {
        BuilderSearchResultItem row = new("FalconBMS", "SimCBEOSB_15F", "FMFD: OSB-15 Button - Push", string.Empty, false, ["FalconBMS"], [], string.Empty)
        {
            Section = "related", RelatedBy = "lacks-word", Understood = "MFD button (OSB)", MissingWords = ["LMFD"]
        };
        Assert.True(row.IsRelated);
        Assert.Equal("Related: it is an action of 'MFD button (OSB)', but its title does not have 'LMFD'.", row.ReasonText);
        Assert.Equal(row.ReasonText, row.WhyText);
    }

    [Theory]
    [InlineData("LMFD: OSB-15 Button - Push", "LMFD OSB 15", new[] { "LMFD", "OSB", "15" })]
    [InlineData("LMFD: OSB-15 Button - Push", "OSB15", new[] { "OSB", "15" })]
    [InlineData("Left MFD OSB 15", "OSB-15", new[] { "OSB", "15" })]
    [InlineData("Left MFD OSB 15", "LMFD OSB 15", new[] { "Left", "MFD", "OSB", "15" })]
    [InlineData("FMFD: OSB-15 Button - Push", "LMFD OSB 15", new[] { "OSB", "15" })]
    public void TheTitleHighlight_FollowsWhatTheSearchReads(string title, string query, string[] marked)
    {
        List<string> segments = Views.Controls.HighlightTextBlock.FindHighlightSegments(title, query).Select(s => title.Substring(s.Start, s.Length)).ToList();
        Assert.Equal(marked, segments);
    }

    // ---- 3: the game chips stay ------------------------------------------------------------------------------------

    private static BuilderFakeEngine ChipEngine()
    {
        BuilderFakeEngine fake = new();
        fake.AddRelation("MFD LEFT BTN12", false, string.Empty, ("DCS", "d3015", "F-16C_50"));
        fake.AddRelation("MFD LEFT BTN13", false, string.Empty, ("DCS", "d3014", "F-16C_50"));
        fake.SearchOverride = args =>
        {
            bool bms = args.Contains("FalconBMS");
            string query = args[args.ToList().IndexOf("search") + 1];
            string dcsRow = Hit("DCS", "d3015", "Left MFD OSB 15", "name", true, ["F-16C_50"]);
            string bmsRow = Hit("FalconBMS", "SimCBEOSB_15L", "LMFD: OSB-15 Button - Push", "name", true, ["FalconBMS"]);
            if (query == "only dcs")
                return bms ? Result(0) : Result(1, dcsRow);
            if (query == "nowhere")
                return Result(0);
            return bms ? Result(1, bmsRow) : Result(2, dcsRow, bmsRow);
        };
        return fake;
    }

    [Fact]
    public async Task GameChip_StaysForAnotherRelation_AndANewSearch()
    {
        BuilderFakeEngine fake = ChipEngine();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "batch263-chip.pr0file");
        await RelationBuilderTests.OpenAsync(vm, "MFD LEFT BTN12");
        vm.BuilderSearchText = "lmfd osb 15";
        await RelationBuilderTests.WaitForAsync(() => vm.BuilderLastSearchedQueryForTests == "lmfd osb 15" && !vm.IsBuilderSearching && vm.BuilderSearchGames.Count == 2);
        vm.BuilderSearchGames.Single(c => c.GameId == "FalconBMS").IsSelected = true;
        await RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSearching && vm.BuilderSearchResults.All(r => r.Game == "FalconBMS"));

        // Another relation: the box is empty, the chip stays on and visible.
        await RelationBuilderTests.OpenAsync(vm, "MFD LEFT BTN13");
        await RelationBuilderTests.WaitForAsync(() => vm.BuilderSearchResults.Count == 0 && vm.BuilderSearchGames.Count == 1);
        Assert.True(vm.ShowBuilderSearchGameChips);
        Assert.True(vm.BuilderSearchGames.Single().IsSelected);
        Assert.Equal("FalconBMS", vm.BuilderSearchGames.Single().GameId);
        Assert.False(vm.ShowBuilderChosenGamesEmpty);

        // A new search: only that game's rows, the other games' chips are back with their numbers, the chip still on.
        fake.Calls.Clear();
        vm.BuilderSearchText = "rmfd osb 3";
        await RelationBuilderTests.WaitForAsync(() => vm.BuilderLastSearchedQueryForTests == "rmfd osb 3" && !vm.IsBuilderSearching);
        Assert.True(AxisTuningTests.Seq(fake.Calls.Last(c => AxisTuningTests.Seq(c, "relations", "search")), "--game", "FalconBMS"));
        Assert.All(vm.BuilderSearchResults, r => Assert.Equal("FalconBMS", r.Game));
        Assert.True(vm.BuilderSearchGames.Single(c => c.GameId == "FalconBMS").IsSelected);
        Assert.False(vm.BuilderSearchGames.Single(c => c.GameId == "DCS").IsSelected);
        Assert.Equal("FalconBMS", vm.BuilderSearchGamesTop[0].GameId);

        // The chip off again: every game (no --game), and it stays off.
        fake.Calls.Clear();
        vm.BuilderSearchGames.Single(c => c.GameId == "FalconBMS").IsSelected = false;
        await RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSearching && vm.BuilderSearchResults.Any(r => r.Game == "DCS"));
        Assert.DoesNotContain("--game", fake.Calls.Last(c => AxisTuningTests.Seq(c, "relations", "search")));
    }

    [Fact]
    public async Task ChosenGameWithoutResults_IsSaid_AndOneClickShowsAllGames()
    {
        BuilderFakeEngine fake = ChipEngine();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "batch263-empty.pr0file");
        await RelationBuilderTests.OpenAsync(vm, "MFD LEFT BTN12");
        vm.BuilderSearchText = "lmfd osb 15";
        await RelationBuilderTests.WaitForAsync(() => vm.BuilderLastSearchedQueryForTests == "lmfd osb 15" && !vm.IsBuilderSearching && vm.BuilderSearchGames.Count == 2);
        vm.BuilderSearchGames.Single(c => c.GameId == "FalconBMS").IsSelected = true;
        await RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSearching && vm.BuilderSearchResults.All(r => r.Game == "FalconBMS"));

        vm.BuilderSearchText = "only dcs";
        await RelationBuilderTests.WaitForAsync(() => vm.BuilderLastSearchedQueryForTests == "only dcs" && !vm.IsBuilderSearching);
        Assert.Empty(vm.BuilderSearchResults);
        Assert.True(vm.ShowBuilderChosenGamesEmpty);
        Assert.Equal("Nothing found for \"only dcs\" in Falcon BMS. Other games have results: 1.", vm.BuilderChosenGamesEmptyText);
        // The chips stay to click, also with no row in the list.
        Assert.True(vm.ShowBuilderSearchGameChips);
        Assert.True(vm.BuilderSearchGames.Single(c => c.GameId == "FalconBMS").IsSelected);

        vm.ShowAllBuilderSearchGamesCommand.Execute(null);
        await RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSearching && vm.BuilderSearchResults.Count == 1);
        Assert.Equal("DCS", vm.BuilderSearchResults.Single().Game);
        Assert.False(vm.ShowBuilderChosenGamesEmpty);
        Assert.DoesNotContain(vm.BuilderSearchGames, c => c.IsSelected);

        // Nowhere at all: it says that too.
        vm.BuilderSearchText = "lmfd osb 15";
        await RelationBuilderTests.WaitForAsync(() => vm.BuilderLastSearchedQueryForTests == "lmfd osb 15" && !vm.IsBuilderSearching && vm.BuilderSearchGames.Count == 2);
        vm.BuilderSearchGames.Single(c => c.GameId == "FalconBMS").IsSelected = true;
        await RelationBuilderTests.WaitForAsync(() => !vm.IsBuilderSearching && vm.BuilderSearchResults.All(r => r.Game == "FalconBMS"));
        vm.BuilderSearchText = "nowhere";
        await RelationBuilderTests.WaitForAsync(() => vm.BuilderLastSearchedQueryForTests == "nowhere" && !vm.IsBuilderSearching);
        Assert.Equal("Nothing found for \"nowhere\" in Falcon BMS, and in no other game either.", vm.BuilderChosenGamesEmptyText);
    }

    [Fact]
    public async Task SuggestionGameChip_StaysForTheNextRelation()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "batch263-suggest.pr0file");
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        vm.BuilderSuggestionGames.Single(g => g.GameId == "WarThunder").IsSelected = true;
        await RelationBuilderTests.OpenAsync(vm, "Chaff");
        BuilderGameChip chip = vm.BuilderSuggestionGames.Single(g => g.GameId == "WarThunder");
        Assert.True(chip.IsSelected);
        // Back: the same chip is on and the list is filtered by it.
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        Assert.True(vm.BuilderSuggestionGames.Single(g => g.GameId == "WarThunder").IsSelected);
        Assert.All(vm.BuilderSuggestions, s => Assert.Equal("WarThunder", s.Game));
    }

    // The left filter: a relation it hides that is opened from elsewhere does not switch it off.
    [Fact]
    public async Task LeftGamesFilter_StaysWhenAHiddenRelationIsOpened()
    {
        BuilderFakeEngine fake = new();
        fake.AddRelation("Only BMS", false, string.Empty, ("FalconBMS", "SimCMSUp", "FalconBMS"));
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "batch263-filter.pr0file");
        await RelationBuilderTests.WaitForAsync(() => vm.Relations.Count >= 5);
        vm.RelationFilter.SelectGame("DCS");
        Assert.True(vm.RelationFilter.IsFiltered);
        Assert.DoesNotContain(vm.Relations, r => r.Name == "Only BMS");

        await vm.OpenRelationFromSearchAsync("Only BMS");
        await RelationBuilderTests.WaitForAsync(() => vm.SelectedRelation?.Name == "Only BMS");
        Assert.True(vm.RelationFilter.IsFiltered);
        Assert.Contains(vm.Relations, r => r.Name == "Only BMS");

        // Another relation open and the filter applied again: it is hidden again, the filter never went away.
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        vm.RelationFilter.SelectOnlyPlanes("DCS", "F-16C_50");
        await RelationBuilderTests.WaitForAsync(() => !vm.Relations.Any(r => r.Name == "Only BMS"));
        Assert.True(vm.RelationFilter.IsFiltered);
    }

    // ---- 4: which grip, which model -----------------------------------------------------------------------------------

    private const string Crosswind = "MFG Crosswind {071D3320-0D8D-11f1-8001-444553540000}";
    private const string CrosswindBoth = "MFG Crosswind V2_3 {071D3320-0D8D-11f1-8001-444553540000}";

    private static HudDeviceInfo Pedals(string device, string name) => new()
    {
        Device = device, Name = name, VidPid = "16D0:0A38", Model = "MFG Crosswind V2 Rudder Pedals", ModelId = "mfg-crosswind-v2", Kind = "pedals", Axes = ["JOY_RZ", "JOY_Y", "JOY_X"], Buttons = 2, Hats = 0
    };

    private static DeviceCatalog ShippedCatalog()
    {
        DeviceReferenceService.CatalogFolderOverride = () => DeviceReferenceTests.ShippedCatalog;
        DeviceReferenceService.Reload();
        return DeviceReferenceService.Catalog;
    }

    [Fact]
    public void SharedUsbId_AsksWhichModel_NotWhichGrip()
    {
        try
        {
            DeviceCatalog catalog = ShippedCatalog();
            DeviceCardItem card = MainWindowViewModel.BuildDeviceCards([Pedals(Crosswind, "MFG Crosswind")], [Crosswind], _ => null, catalog).Cards.Single();
            Assert.True(card.NeedsGrip);
            Assert.Equal("model", card.QuestionKind);
            Assert.Equal("Which model?", card.QuestionChip);
            Assert.Contains("Several products report this USB id", card.QuestionTip);
            Assert.Contains("Several products report this USB id", card.Tooltip);
            Assert.DoesNotContain("grip", card.Tooltip, StringComparison.OrdinalIgnoreCase);
            // A base still asks for its grip.
            HudDeviceInfo rhino = new() { Device = "VPforce Rhino FFB Joystick {AD5168C0-B52F-11f1-8001-444553540000}", Name = "Rhino FFB Joystick", VidPid = "FFFF:2055", Kind = "stick", Axes = ["JOY_X", "JOY_Y"], Buttons = 64, Hats = 4 };
            DeviceCardItem baseCard = MainWindowViewModel.BuildDeviceCards([rhino], [], _ => null, catalog).Cards.Single();
            Assert.True(baseCard.NeedsGrip);
            Assert.Equal("Which grip?", baseCard.QuestionChip);
        }
        finally
        {
            DeviceReferenceService.CatalogFolderOverride = () => null;
            DeviceReferenceService.Reload();
        }
    }

    // The tester's pedals: the device's own name lists both models, which have the same inputs: nothing to answer.
    [Fact]
    public void NameThatListsBothModels_AsksNothing()
    {
        try
        {
            DeviceCatalog catalog = ShippedCatalog();
            DeviceCardItem card = MainWindowViewModel.BuildDeviceCards([Pedals(CrosswindBoth, "MFG Crosswind V2/3")], [CrosswindBoth], _ => null, catalog).Cards.Single();
            Assert.False(card.NeedsGrip);
            Assert.Equal("mfg-crosswind-v2", card.ModelId);
            DeviceProbe probe = new() { VidPid = "16D0:0A38", ProductName = "MFG Crosswind V2/3", InstanceName = CrosswindBoth, Buttons = 2, Hats = 0, Axes = ["JOY_RZ", "JOY_Y", "JOY_X"] };
            Assert.Null(DeviceChoices.Question(DeviceIdentifier.Identify(catalog, probe), probe, catalog));
        }
        finally
        {
            DeviceReferenceService.CatalogFolderOverride = () => null;
            DeviceReferenceService.Reload();
        }
    }

    private sealed class PedalsEngine : IEngineCliClient
    {
        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            string output = "ok";
            if (NoviceGuiTests.Seq(arguments, "devices", "list"))
                output = "Connected:" + Environment.NewLine + "  - " + Crosswind + Environment.NewLine + "Profile:" + Environment.NewLine + "  - " + Crosswind;
            else if (NoviceGuiTests.Seq(arguments, "hud", "devices"))
                output = "{\"Devices\":[{\"Device\":\"" + Crosswind + "\",\"Name\":\"MFG Crosswind\",\"VidPid\":\"16D0:0A38\",\"Model\":\"MFG Crosswind V2 Rudder Pedals\",\"ModelId\":\"mfg-crosswind-v2\"," +
                         "\"Kind\":\"pedals\",\"Hand\":\"\",\"Generic\":false,\"Image\":\"\",\"Buttons\":2,\"Hats\":0,\"Axes\":[{\"Input\":\"JOY_RZ\",\"Control\":\"\",\"Label\":\"\"},{\"Input\":\"JOY_X\",\"Control\":\"\",\"Label\":\"\"},{\"Input\":\"JOY_Y\",\"Control\":\"\",\"Label\":\"\"}],\"Labels\":{},\"GripChoices\":[],\"Layout\":null}],\"Warnings\":[]}";
            return Task.FromResult(new EngineCommandResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(1), StandardOutput = output, StandardError = string.Empty, Arguments = arguments.ToArray() });
        }
    }

    private static MainWindowViewModel PedalsVm() => new(new PedalsEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };

    // A click on the chip: the Layout page with that device and its chooser open; the Model... answer stays and clears the chip,
    // also in a new view model (a restart reads grips.json again).
    [Fact]
    public async Task ChipClick_OpensTheChooser_AndTheAnswerStaysAfterARestart()
    {
        string grips = Path.Combine(DeviceReferenceService.DataFolder(), "grips.json");
        if (File.Exists(grips))
            File.Delete(grips);
        try
        {
            ShippedCatalog();
            MainWindowViewModel vm = PedalsVm();
            await vm.LoadDeviceCardsCommand.ExecuteAsync(null);
            DeviceCardItem card = vm.DeviceCards.Single(c => c.Device == Crosswind);
            Assert.True(card.NeedsGrip);
            Assert.Equal("Which model?", card.QuestionChip);

            await vm.AnswerDeviceCardQuestionCommand.ExecuteAsync(card);
            await RelationBuilderTests.WaitForAsync(() => vm.ShowReferenceQuestion);
            Assert.Equal(UiStage.Layout, vm.CurrentStage);
            Assert.Equal(Crosswind, vm.DeviceReference.DeviceName);
            Assert.Equal("model", vm.DeviceReference.Question!.Kind);
            Assert.Contains(vm.DeviceReference.QuestionCandidates, c => c.Model.Id == "mfg-crosswind-v2");
            ChoiceCandidate v3 = vm.DeviceReference.QuestionCandidates.Single(c => c.Model.Id == "mfg-crosswind-v3");

            await vm.DeviceReference.ChooseCandidateCommand.ExecuteAsync(v3);
            Assert.False(vm.ShowReferenceQuestion);
            Assert.Equal("mfg-crosswind-v3", DeviceReferenceService.ChoiceOf(Crosswind));
            await RelationBuilderTests.WaitForAsync(() => vm.DeviceCards.Single(c => c.Device == Crosswind).ModelId == "mfg-crosswind-v3");
            Assert.False(vm.DeviceCards.Single(c => c.Device == Crosswind).NeedsGrip);

            // A restart: the answer is read again, no chip, no question on the Layout page.
            MainWindowViewModel again = PedalsVm();
            await again.LoadDeviceCardsCommand.ExecuteAsync(null);
            DeviceCardItem after = again.DeviceCards.Single(c => c.Device == Crosswind);
            Assert.False(after.NeedsGrip);
            Assert.Equal("mfg-crosswind-v3", after.ModelId);
            await again.ShowDeviceReferenceForAsync(Crosswind);
            Assert.False(again.DeviceReference.HasQuestion);
            Assert.Equal("mfg-crosswind-v3", again.DeviceReference.Model?.Id);
        }
        finally
        {
            if (File.Exists(grips))
                File.Delete(grips);
            DeviceReferenceService.CatalogFolderOverride = () => null;
            DeviceReferenceService.Reload();
        }
    }

    // The Layout page asks the same as the card (the engine's model of a shared USB id used to end the question there, so
    // the card asked and the page had no answer to give but Model...).
    [Fact]
    public async Task LayoutPage_AsksTheSameAsTheCard()
    {
        string grips = Path.Combine(DeviceReferenceService.DataFolder(), "grips.json");
        if (File.Exists(grips))
            File.Delete(grips);
        try
        {
            ShippedCatalog();
            MainWindowViewModel vm = PedalsVm();
            await vm.ShowDeviceReferenceForAsync(Crosswind);
            Assert.True(vm.DeviceReference.HasQuestion);
            Assert.Equal("model", vm.DeviceReference.Question!.Kind);
            Assert.Equal(2, vm.DeviceReference.QuestionCandidates.Count);
        }
        finally
        {
            DeviceReferenceService.CatalogFolderOverride = () => null;
            DeviceReferenceService.Reload();
        }
    }

    // ---- Pictures with the real engine (opt-in) --------------------------------------------------------------------

    private static async Task SettleAsync(MainWindowViewModel vm, Func<bool> done)
    {
        for (int i = 0; i < 6000 && !(done() && !vm.IsBuilderSearching && !vm.IsBusy); i++)
        {
            AvaloniaRenderThread.Pump();
            await Task.Delay(10);
        }
    }

    private static void Render(MainWindowViewModel vm, int width, int height, string outDir, string file)
    {
        using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(width, height));
        main.Settle();
        using RenderTargetBitmap bitmap = main.Render();
        bitmap.Save(Path.Combine(outDir, file));
    }

    [Fact]
    public void RenderWithTheRealEngine()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_263_RENDER_OUT");
        string? engine = Environment.GetEnvironmentVariable("JOYPRO_263_ENGINE");
        string? source = Environment.GetEnvironmentVariable("JOYPRO_263_PROFILE");
        if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(engine) || !File.Exists(engine) || string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return;
        Directory.CreateDirectory(outDir);
        string profile = Path.Combine(outDir, "work.pr0file");
        File.Copy(source, profile, overwrite: true);
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = new(new EngineCliClient(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
            try
            {
                AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
                vm.CurrentStage = UiStage.Relations;
                using (HiddenWindow window = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1500, 900)))
                {
                    vm.SelectedRelation = vm.Relations.First(r => r.Name == "F1 Cockpit view");
                    AvaloniaRenderThread.Wait(SettleAsync(vm, () => vm.BuilderRelationName == "F1 Cockpit view" && vm.RelationNodes.Count > 0));
                    vm.BuilderSearchText = "f1 cockpit";
                    AvaloniaRenderThread.Wait(SettleAsync(vm, () => vm.BuilderLastSearchedQueryForTests == "f1 cockpit"));
                }
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    Render(vm, 1500, 900, outDir, "partly-f1-cockpit-" + theme + ".png");
                }

                // "LMFD OSB 15", Falcon BMS only (the chip on), then the Related rows open.
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                vm.BuilderSearchText = "LMFD OSB 15";
                AvaloniaRenderThread.Wait(SettleAsync(vm, () => vm.BuilderLastSearchedQueryForTests == "LMFD OSB 15" && vm.BuilderSearchGames.Count > 0));
                Render(vm, 1500, 900, outDir, "lmfd-osb-15-all-games-dark.png");
                vm.BuilderSearchGames.Single(c => c.GameId == "FalconBMS").IsSelected = true;
                AvaloniaRenderThread.Wait(SettleAsync(vm, () => vm.BuilderSearchResults.All(r => r.Game == "FalconBMS")));
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    Render(vm, 1500, 900, outDir, "lmfd-osb-15-bms-" + theme + ".png");
                }
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                vm.ToggleBuilderRelatedCommand.Execute(null);
                Render(vm, 1500, 900, outDir, "lmfd-osb-15-bms-related-open-dark.png");

                // The sticky chip: another relation (empty box, chip on), a new search, nothing in the chosen game.
                RelationListItem next = vm.Relations.First(r => r.Name != "F1 Cockpit view");
                using (HiddenWindow window = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1500, 900)))
                {
                    vm.SelectedRelation = next;
                    AvaloniaRenderThread.Wait(SettleAsync(vm, () => vm.BuilderRelationName == next.Name && vm.BuilderSearchResults.Count == 0));
                }
                Render(vm, 1500, 900, outDir, "sticky-chip-next-relation-dark.png");
                Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                Render(vm, 1500, 900, outDir, "sticky-chip-next-relation-light.png");
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                vm.BuilderSearchText = "gunner role";
                AvaloniaRenderThread.Wait(SettleAsync(vm, () => vm.BuilderLastSearchedQueryForTests == "gunner role"));
                Render(vm, 1500, 900, outDir, "sticky-chip-no-results-dark.png");
                Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                Render(vm, 1500, 900, outDir, "sticky-chip-no-results-light.png");
            }
            finally
            {
                vm.StopGameConfigWatcher();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
    }

    // The device chip on the Start page and its chooser on the Layout page.
    [Fact]
    public void RenderDeviceChip()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_263_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        string grips = Path.Combine(DeviceReferenceService.DataFolder(), "grips.json");
        try
        {
            if (File.Exists(grips))
                File.Delete(grips);
            ShippedCatalog();
            AvaloniaRenderThread.Run(() =>
            {
                MainWindowViewModel vm = PedalsVm();
                AvaloniaRenderThread.Wait(vm.LoadDeviceCardsCommand.ExecuteAsync(null));
                vm.CurrentStage = UiStage.Start;
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1500, 900));
                    main.Settle();
                    Assert.Contains(main.Page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Which model?" && t.IsEffectivelyVisible);
                    Assert.DoesNotContain(main.Page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Which grip?" && t.IsEffectivelyVisible);
                    using RenderTargetBitmap bitmap = main.Render();
                    bitmap.Save(Path.Combine(outDir, "device-chip-start-" + theme + ".png"));
                }
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                AvaloniaRenderThread.Wait(vm.AnswerDeviceCardQuestionCommand.ExecuteAsync(vm.DeviceCards.Single(c => c.Device == Crosswind)));
                AvaloniaRenderThread.Pump();
                Assert.True(vm.ShowReferenceQuestion);
                using (HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1500, 900)))
                {
                    main.Settle();
                    using RenderTargetBitmap bitmap = main.Render();
                    bitmap.Save(Path.Combine(outDir, "device-chooser-layout-dark.png"));
                }
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            });
        }
        finally
        {
            if (File.Exists(grips))
                File.Delete(grips);
            DeviceReferenceService.CatalogFolderOverride = () => null;
            DeviceReferenceService.Reload();
        }
    }
}

// Every setting survives a restart: the game chips of Add actions and of the suggestions (ui-settings.json). Uses the test
// run's own settings folder in the "TrayMode" collection that owns it.
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class GameChipsPersistTests : IDisposable
{
    private readonly string uiSettings = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");

    public GameChipsPersistTests()
    {
        Directory.CreateDirectory(AppInfo.SettingsFolder);
        File.Delete(uiSettings);
    }

    public void Dispose() => File.Delete(uiSettings);

    private static MainWindowViewModel Vm(BuilderFakeEngine fake) =>
        new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: true) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };

    [Fact]
    public async Task ChosenGames_ComeBackAfterARestart()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel first = Vm(fake);
        first.BuilderSearchText = "flare";
        await RelationBuilderTests.WaitForAsync(() => first.BuilderLastSearchedQueryForTests == "flare" && !first.IsBuilderSearching && first.BuilderSearchGames.Count > 0);
        first.BuilderSearchGames.Single(c => c.GameId == "WarThunder").IsSelected = true;
        await RelationBuilderTests.WaitForAsync(() => !first.IsBuilderSearching && first.BuilderSearchResults.All(r => r.Game == "WarThunder"));

        BuilderFakeEngine later = new();
        MainWindowViewModel second = Vm(later);
        second.BuilderSearchText = "flare";
        await RelationBuilderTests.WaitForAsync(() => second.BuilderLastSearchedQueryForTests == "flare" && !second.IsBuilderSearching);
        Assert.True(AxisTuningTests.Seq(later.Calls.Last(c => AxisTuningTests.Seq(c, "relations", "search")), "--game", "WarThunder"));
        Assert.True(second.BuilderSearchGames.Single(c => c.GameId == "WarThunder").IsSelected);
        Assert.All(second.BuilderSearchResults, r => Assert.Equal("WarThunder", r.Game));
    }
}
