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

// The check's problems on the Export page (Views/Controls/CheckProblemsPanel.axaml): "actions in several relations" as
// cards per relation pair, in words (the aircraft as people call it, the action's title, relation, device and button;
// never an id), with the confidence of the suggestion and one-click fixes; fixes for many with a preview of ticked
// changes applied in one engine call (one undo step).
[SupportedOSPlatform("windows")]
public sealed class DuplicateActionViewModelTests
{
    private const string TrimId = "d3007pnilu3007cd25vd1vpnilvu0";

    private static MainWindowViewModel NewVm(IEngineCliClient engine) => new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
    {
        EnginePath = typeof(MainWindowViewModel).Assembly.Location,
        ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "duplicates.pr0file")
    };

    [Fact]
    public async Task Check_ShowsCardsInWords_WithConfidence_AndOneClickFixes()
    {
        DuplicatesFakeEngine fake = new();
        MainWindowViewModel vm = NewVm(fake);

        await vm.ValidateProfileCommand.ExecuteAsync(null);

        CheckIssueSection duplicates = Assert.Single(vm.CheckSections, s => s.Kind == "duplicates");
        Assert.True(duplicates.IsWarning);
        Assert.Equal("Same command on two buttons (actions: 2, cards: 1)", duplicates.CountText);
        Assert.StartsWith("The game gets the same command from two buttons", duplicates.Severity);
        CheckIssueCard card = Assert.Single(duplicates.Cards);
        // The card in words: a short title, the two buttons with their relations, one sentence, the table, the fixes.
        Assert.True(card.IsWarning);
        Assert.Equal("Same command on two buttons", card.Title);
        Assert.Equal("Total Controls MFBB · BTN 39 ('Mode Button 07')  ↔  MOZA AB9 FFB Base · BTN 4 ('Trim Right')", card.ButtonsLine);
        Assert.Equal("In AH-64D Block II (CPG and Pilot), both buttons do 'Force Trim/Hold Mode Switch - AL/Right'. Usually only one of them is wanted.", card.Sentence);
        CheckTableRow row = Assert.Single(card.Rows);
        Assert.Equal("AH-64D Block II (CPG and Pilot)", row.Left);
        Assert.Equal("'Force Trim/Hold Mode Switch - AL/Right'", row.Right);
        Assert.Equal("96% sure: keep it only on MOZA AB9 FFB Base · BTN 4", card.SuggestionText);
        Assert.Contains("It was only in 'Trim Right' before", card.ReasonText);
        Assert.Equal("Keep only on MOZA AB9 FFB Base · BTN 4", card.Recommended!.Text);
        Assert.Equal(["Keep only on Total Controls MFBB · BTN 39", "Keep both"], card.Fixes.Select(f => f.Text));
        Assert.Equal(["Switch it off on both buttons"], card.MoreFixes.Select(f => f.Text));
        Assert.Equal(["Open 'Mode Button 07' (Total Controls MFBB · BTN 39)", "Open 'Trim Right' (MOZA AB9 FFB Base · BTN 4)"], card.OpenButtons.Select(f => f.Text));
        Assert.Empty(CheckTextGate.Problems([card]));
        // The engine's plain line is not listed again under the notes; the other note stays.
        CheckIssueSection notes = Assert.Single(vm.CheckSections, s => s.Kind == "notes");
        Assert.True(notes.IsNote);
        Assert.Equal("Bind without relation: Old", Assert.Single(notes.Cards).Sentence);
        Assert.Equal(2, card.Choices.Count);
        DuplicateRelationChoice trim = card.Choices.Single(c => c.Relation == "Trim Right");
        Assert.True(trim.IsSuggested);
        Assert.Equal("Keep only on MOZA AB9 FFB Base · BTN 4", trim.KeepText);

        // The threshold: one of two actions at 90% or more.
        Assert.Equal(90, vm.DuplicateAutoFixThreshold);
        Assert.Equal("Fix the sure ones (1 of 2 at 90% or more)", vm.FixSureOnesText);
        vm.DuplicateAutoFixThreshold = 50;
        Assert.Equal("Fix the sure ones (2 of 2 at 50% or more)", vm.FixSureOnesText);
        vm.DuplicateAutoFixThreshold = 90;

        // Search, kind filter, collapse.
        vm.CheckSearchText = "Viggen";
        Assert.DoesNotContain(vm.CheckSections, s => s.Kind == "duplicates");
        vm.CheckSearchText = "trim right";
        Assert.Single(vm.CheckSections, s => s.Kind == "duplicates");
        vm.CheckSearchText = string.Empty;
        vm.SelectedCheckKind = vm.CheckKindChoices.Single(c => c.Rule == "notes");
        Assert.Equal(["notes"], vm.CheckSections.Select(s => s.Kind).ToArray());
        vm.SelectedCheckKind = vm.CheckKindChoices[0];
        vm.CollapseAllCheckCardsCommand.Execute(null);
        Assert.All(vm.CheckSections, s => Assert.False(s.IsExpanded));
        vm.ExpandAllCheckCardsCommand.Execute(null);

        // Keep in one relation: one engine call with the card's actions; checked again, the card is gone.
        card = vm.CheckSections.Single(s => s.Kind == "duplicates").Cards.Single();
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)card.Choices.Single(c => c.Relation == "Trim Right").KeepCommand!).ExecuteAsync(null);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "duplicates", "--keep", "Trim Right", "--keys",
            "DCS|" + TrimId + "|AH-64D_BLK_II_CPG;DCS|" + TrimId + "|AH-64D_BLK_II_PLT"));
        Assert.DoesNotContain(vm.CheckSections, s => s.Kind == "duplicates");
    }

    [Fact]
    public async Task KeepBoth_SwitchOff_AndThePreviewApplyExactlyTheTickedChanges()
    {
        DuplicatesFakeEngine fake = new();
        MainWindowViewModel vm = NewVm(fake);
        await vm.ValidateProfileCommand.ExecuteAsync(null);
        CheckIssueCard card = vm.CheckSections.Single(s => s.Kind == "duplicates").Cards.Single();
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)card.KeepBothCommand!).ExecuteAsync(null);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "duplicates", "--keep-both", "--keys"));

        fake.Resolved = false;
        await vm.ValidateProfileCommand.ExecuteAsync(null);
        card = vm.CheckSections.Single(s => s.Kind == "duplicates").Cards.Single();
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)card.SwitchOffAllCommand!).ExecuteAsync(null);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "duplicates", "--off", "--keys"));

        // Fix the sure ones: a preview at the threshold, one change unticked, Apply sends the ticked one only.
        fake.Resolved = false;
        await vm.ValidateProfileCommand.ExecuteAsync(null);
        await vm.PreviewSureDuplicatesCommand.ExecuteAsync(null);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "duplicates", "--rule", "suggested", "--min-confidence", "90"));
        Assert.True(vm.HasDuplicatePreview);
        Assert.Equal("Fix the sure ones: 2 changes at 90% or more", vm.DuplicatePreviewTitle);
        Assert.Equal("AH-64D Block II (CPG) 'Force Trim/Hold Mode Switch - AL/Right': keep in 'Trim Right', off in 'Mode Button 07'", vm.DuplicatePreview[0].Text);
        vm.DuplicatePreview[1].IsTicked = false;
        await vm.ApplyDuplicatePreviewCommand.ExecuteAsync(null);
        IReadOnlyList<string> apply = fake.Calls.Last(c => c.Contains("--changes"));
        Assert.Equal("DCS|" + TrimId + "|AH-64D_BLK_II_CPG=>Trim Right", apply[apply.ToList().IndexOf("--changes") + 1]);
        Assert.False(vm.HasDuplicatePreview);

        // An instruction in words: the preview names what JoyPro understood.
        fake.Resolved = false;
        await vm.ValidateProfileCommand.ExecuteAsync(null);
        vm.DuplicateInstruction = "keep trim functions in the Trim relations";
        await vm.PreviewDuplicateInstructionCommand.ExecuteAsync(null);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "duplicates", "--instruction", "keep trim functions in the Trim relations", "--llm", "auto"));
        Assert.Contains("Understood: keep trim functions in the Trim relations", vm.DuplicatePreviewNotes);
        vm.CancelDuplicatePreviewCommand.Execute(null);
        Assert.Empty(vm.DuplicatePreview);
    }

    [Fact]
    public async Task ImportReport_NamesTheNewDuplicates_AndFixesTheSureOnesWhenWanted()
    {
        DuplicatesFakeEngine fake = new() { Resolved = true };
        MainWindowViewModel vm = NewVm(fake);
        Directory.CreateDirectory(Path.GetDirectoryName(vm.ProfilePath)!);
        File.WriteAllText(vm.ProfilePath, "x");
        HashSet<string>? before = await vm.DuplicateKeysAsync();
        Assert.NotNull(before);
        Assert.Empty(before!);
        fake.Resolved = false;
        await vm.ReportNewDuplicatesAsync(before, "The DCS import");
        Assert.True(vm.HasDuplicateImportNotice);
        Assert.StartsWith("The DCS import: 2 actions are now in several relations (each action of an aircraft counts once)", vm.DuplicateImportNotice);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("--apply"));
        // The same number as the check on the Export page.
        await vm.ValidateProfileCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.DuplicateActionCount);
        Assert.Equal("Same command on two buttons (actions: 2, cards: 1)", vm.CheckSections.Single(s => s.Kind == "duplicates").CountText);

        vm.AutoFixDuplicatesAfterImport = true;
        fake.Resolved = true;
        before = await vm.DuplicateKeysAsync();
        fake.Resolved = false;
        await vm.ReportNewDuplicatesAsync(before, "Taking over the game's changes");
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "--rule", "suggested", "--min-confidence", "90") && c.Contains("--apply"));
        Assert.Contains("were fixed at once", vm.DuplicateImportNotice);
        Assert.Contains("actions in several relations are left on the Export page", vm.DuplicateImportNotice);
    }

    // The notice of an import into a profile that had some already: the new ones and all of them, as the check counts.
    [Fact]
    public void DuplicateNotice_NamesTheNewOnesAndAllOfThem()
    {
        Assert.Equal("The DCS import: 3 more actions are now in several relations, 10 in all (each action of an aircraft counts once). Review them on the Export page under 'Problems found by the check'.",
            MainWindowViewModel.DuplicateNoticeText("The DCS import", 3, 10, 0, 90));
        Assert.StartsWith("The DCS import: 10 actions were in several relations; 4 of them were fixed at once (90% or more sure). 6 actions in several relations are left",
            MainWindowViewModel.DuplicateNoticeText("The DCS import", 10, 10, 4, 90));
    }

    // The check's "one input in several relations" line (engine: "Button Item key found in multiple Relation: DCS:plane§
    // device {GUID}§JOY_BTN7§modifier§...") as a card in words: aircraft, device and button, the relations; no GUID, no
    // JOY_ token, no § key. A relation name with ", " stays one name.
    [Fact]
    public void InputInSeveralRelations_IsACardInWords()
    {
        MainWindowViewModel vm = NewVm(new DuplicatesFakeEngine());
        // A device no test gives an alias (DeviceAliases is shared by the tests).
        const string Device = "QA Test Throttle {42CEC570-0A3F-11f0-800B-444553540000}";
        string message = "Button Item key found in multiple Relation: DCS:ZZ-Test_CPG§" + Device + "§JOY_BTN7§Shift A§" + Device +
                         "§JOY_BTN1§True The Relations it was found in: Weapons, guns, MFD__RIGHT__T2";

        MainWindowViewModel.InputConflict conflict = MainWindowViewModel.ParseInputConflict(message, ["Weapons, guns", "MFD__RIGHT__T2"])!;
        Assert.Equal("DCS", conflict.Game);
        Assert.Equal("ZZ-Test_CPG", conflict.Plane);
        Assert.Equal(Device, conflict.Device);
        Assert.Equal("JOY_BTN7", conflict.Input);
        Assert.Equal(["Shift A"], conflict.Modifiers);
        Assert.Equal(["Weapons, guns", "MFD__RIGHT__T2"], conflict.Relations);
        Assert.Null(MainWindowViewModel.ParseInputConflict("Bind without relation: Old", []));

        // The view model has no relation "Weapons, guns": the pieces stay apart.
        CheckIssueCard card = vm.PlainCard(new ValidationIssueItem { Kind = "Error", Message = message });
        // An aircraft no name table has (RelationListItem.AircraftNames is shared by the tests): its id at the seat.
        Assert.True(card.IsError);
        Assert.Equal("One button does two things", card.Title);
        Assert.Equal("QA Test Throttle · BTN 7 · Shift A in 'Weapons', 'guns' and 'MFD__RIGHT__T2'", card.ButtonsLine);
        Assert.Equal("In ZZ-Test (CPG), one press runs the commands of 'Weapons', 'guns' and 'MFD__RIGHT__T2' at the same time.", card.Sentence);
        Assert.Empty(CheckTextGate.Problems([card]));
        Assert.Contains("MFD__RIGHT__T2", card.SearchText);
        // One "Open" per relation (never one button that opens only the first).
        Assert.Equal(3, card.OpenButtons.Count);
        Assert.StartsWith("Open 'Weapons' (QA Test Throttle · BTN 7 · Shift A)", card.OpenButtons[0].Text);
        CheckIssueCard note = vm.PlainCard(new ValidationIssueItem { Kind = "Warning", Message = "Bind without relation: Old" });
        Assert.True(note.IsNote);
        Assert.Equal("Bind without relation: Old", note.Sentence);
    }

    // profile validate and relations duplicates of one relation pair until a fix resolves it.
    private sealed class DuplicatesFakeEngine : IEngineCliClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public bool Resolved { get; set; }

        private const string GroupMessage = "AH-64D Block II (CPG and Pilot): 'Force Trim/Hold Mode Switch - AL/Right' is in 2 relations: 'Mode Button 07' (Total Controls MFBB · BTN 39) and 'Trim Right' (MOZA AB9 FFB Base · BTN 4). DCS gets two binds for it. 96% sure: keep it in 'Trim Right'.";

        private static string Duplicate(string plane, int confidence) =>
            "{\"Key\":\"DCS|" + TrimId + "|" + plane + "\",\"Game\":\"DCS\",\"Id\":\"" + TrimId + "\",\"Plane\":\"" + plane + "\",\"Title\":\"Force Trim/Hold Mode Switch - AL/Right\"," +
            "\"Suggested\":\"Trim Right\",\"SuggestOff\":false,\"Confidence\":" + confidence + ",\"SuggestionSource\":\"history\"}";

        private static string Change(string plane, int confidence) =>
            "{\"Key\":\"DCS|" + TrimId + "|" + plane + "\",\"Game\":\"DCS\",\"Id\":\"" + TrimId + "\",\"Plane\":\"" + plane + "\",\"Title\":\"Force Trim/Hold Mode Switch - AL/Right\"," +
            "\"Keep\":\"Trim Right\",\"Off\":false,\"SwitchOffIn\":[\"Mode Button 07\"],\"Confidence\":" + confidence + ",\"Rule\":\"suggested\"}";

        private string DuplicatesJson(string changes, string understood) => Resolved
            ? "{\"Count\":0,\"Duplicates\":[],\"Groups\":[],\"Changes\":[" + changes + "],\"Understood\":[" + understood + "]}"
            : "{\"Count\":2,\"Duplicates\":[" + Duplicate("AH-64D_BLK_II_CPG", 96) + "," + Duplicate("AH-64D_BLK_II_PLT", 60) + "],\"Groups\":[{" +
              "\"Key\":\"DCS|Mode Button 07\\tTrim Right\",\"Game\":\"DCS\",\"Relations\":[" +
              "{\"Relation\":\"Mode Button 07\",\"Device\":\"Total Controls MFBB {037D9BD0-D2C1-11f0-8011-444553540000}\",\"Input\":\"JOY_BTN39\"}," +
              "{\"Relation\":\"Trim Right\",\"Device\":\"MOZA AB9 FFB Base {021126F0-9255-11f1-8001-444553540000}\",\"Input\":\"JOY_BTN4\"}]," +
              "\"Actions\":[{\"Id\":\"" + TrimId + "\",\"Title\":\"Force Trim/Hold Mode Switch - AL/Right\",\"Planes\":[\"AH-64D_BLK_II_CPG\",\"AH-64D_BLK_II_PLT\"]}]," +
              "\"Planes\":[\"AH-64D_BLK_II_CPG\",\"AH-64D_BLK_II_PLT\"],\"PlaneNames\":[{\"Plane\":\"AH-64D_BLK_II_CPG\",\"Name\":\"AH-64D Block II\",\"Seat\":\"CPG\"},{\"Plane\":\"AH-64D_BLK_II_PLT\",\"Name\":\"AH-64D Block II\",\"Seat\":\"Pilot\"}]," +
              "\"Keys\":[\"DCS|" + TrimId + "|AH-64D_BLK_II_CPG\",\"DCS|" + TrimId + "|AH-64D_BLK_II_PLT\"],\"Suggested\":\"Trim Right\",\"SuggestOff\":false,\"Confidence\":96," +
              "\"Reasons\":[{\"Code\":\"history\",\"Relation\":\"Trim Right\",\"Detail\":\"21.10.2026 10:21\"},{\"Code\":\"position\",\"Relation\":\"Mode Button 07\"}]," +
              "\"Message\":\"" + GroupMessage.Replace("'", "\\u0027") + "\"}],\"Changes\":[" + changes + "],\"Understood\":[" + understood + "]}";

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            string output = string.Empty;
            if (AxisTuningTests.Seq(arguments, "profile", "validate"))
            {
                output = "Profile Validation\nPassed: True\nFail On Warnings: False\nWarnings: " + (Resolved ? "1" : "2") + "\nErrors: 0\nRelation Warnings: " + (Resolved ? "1" : "2") + "\n" +
                         (Resolved ? string.Empty : "  - " + GroupMessage + "\n") + "  - Bind without relation: Old\n";
            }
            else if (AxisTuningTests.Seq(arguments, "relations", "duplicates"))
            {
                bool fixes = arguments.Contains("--keep") || arguments.Contains("--off") || arguments.Contains("--keep-both") || arguments.Contains("--changes") || arguments.Contains("--apply");
                string changes = arguments.Contains("--rule") || arguments.Contains("--instruction") ? Change("AH-64D_BLK_II_CPG", 96) + "," + Change("AH-64D_BLK_II_PLT", 92) : string.Empty;
                string understood = arguments.Contains("--instruction") ? "\"keep trim functions in the Trim relations\"" : string.Empty;
                if (arguments.Contains("--keys-only"))
                    output = Resolved ? "{\"Count\":0,\"Duplicates\":[]}" : "{\"Count\":2,\"Duplicates\":[{\"Key\":\"DCS|" + TrimId + "|AH-64D_BLK_II_CPG\"},{\"Key\":\"DCS|" + TrimId + "|AH-64D_BLK_II_PLT\"}]}";
                else
                    output = DuplicatesJson(changes, understood);
                if (fixes)
                {
                    Resolved = true;
                    output = output.Replace("{\"Count\":", "{\"Resolved\":2,\"Count\":");
                }
            }
            else if (AxisTuningTests.Seq(arguments, "relations", "list"))
            {
                output = "{\"Relations\":[]}";
            }
            else if (AxisTuningTests.Seq(arguments, "binds", "list"))
            {
                output = "{\"Binds\":[]}";
            }
            return Task.FromResult(new EngineCommandResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(1), StandardOutput = output, Arguments = arguments.ToArray() });
        }
    }
}
