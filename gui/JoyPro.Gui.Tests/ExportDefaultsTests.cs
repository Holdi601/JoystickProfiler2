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

// 2.5.0 export and import decisions (docs/export.md): clean + nuke is the default for whoever never chose (marker like
// the log retention), include defaults on for DCS, the Move panel's line per mode, the first-export question, the
// watcher hint in the import, the vJoy device JoyPro tunnels into, paths per PC with a profile's own, and the marks of
// binds "Only add" did not write (chip, Check card and its fixes). Fake engine, temp folders, never a window.
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class ExportDefaultsTests : IDisposable
{
    private const string Stick = "Joystick - HOTAS Warthog {00000000-0000-0000-0000-0000000000a1}";
    private readonly string exportSettings = Path.Combine(AppInfo.SettingsFolder, "export-settings.json");
    private readonly string listPrefs = Path.Combine(AppInfo.SettingsFolder, "list-preferences.json");
    private readonly string uiSettings = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");
    private readonly string temp = Path.Combine(Path.GetTempPath(), "joypro-a2-" + Guid.NewGuid().ToString("N"));

    public ExportDefaultsTests()
    {
        Directory.CreateDirectory(AppInfo.SettingsFolder);
        Directory.CreateDirectory(temp);
        File.Delete(exportSettings);
        File.Delete(listPrefs);
        File.Delete(uiSettings);
    }

    public void Dispose()
    {
        File.Delete(exportSettings);
        File.Delete(listPrefs);
        File.Delete(uiSettings);
        try
        {
            Directory.Delete(temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static MainWindowViewModel Vm(IEngineCliClient engine, bool persist) =>
        new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: persist) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };

    private string Profile(string name = "a2")
    {
        string path = Path.Combine(temp, name + ".pr0file");
        File.WriteAllText(path, "{}");
        return path;
    }

    // ---- Defaults and the "chosen by user" markers ----

    [Fact]
    public void Migrate_OldDefaultsBecomeCleanAndNuke_ChosenValuesStay()
    {
        ExportSettings legacy = new() { Mode = "merge", NukeUntouchedDevices = false, DcsCleanAllAircraft = true };
        legacy.Import.IncludeDefaults = false;
        legacy.Import.VJoyTunnelDevice = 42;
        ExportSettingsStore.Migrate(legacy);
        Assert.Equal("clean", legacy.Mode);
        Assert.True(legacy.NukeUntouchedDevices);
        Assert.False(legacy.DcsCleanAllAircraft);
        Assert.True(legacy.Import.IncludeDefaults);
        Assert.Equal(0, legacy.Import.VJoyTunnelDevice);

        // A mode that was never JoyPro's default was the user's choice.
        ExportSettings picked = new() { Mode = "merge-overwrite", NukeUntouchedDevices = false };
        ExportSettingsStore.Migrate(picked);
        Assert.Equal("merge-overwrite", picked.Mode);
        Assert.False(picked.NukeUntouchedDevices);

        // With the marker everything stays, also the old default and an untick of include defaults.
        ExportSettings chosen = new() { Mode = "merge", NukeUntouchedDevices = false, DcsCleanAllAircraft = true, ModeChosenByUser = true };
        chosen.Import.IncludeDefaults = false;
        chosen.Import.IncludeDefaultsChosen = true;
        ExportSettingsStore.Migrate(chosen);
        Assert.Equal("merge", chosen.Mode);
        Assert.False(chosen.NukeUntouchedDevices);
        Assert.True(chosen.DcsCleanAllAircraft);
        Assert.False(chosen.Import.IncludeDefaults);
    }

    [Fact]
    public void FirstStart_CleanNukeIncludeDefaults_AndTheUsersChoicesSurviveARestart()
    {
        // An export-settings.json of 2.4.x with JoyPro's old defaults.
        File.WriteAllText(exportSettings, "{\"mode\":\"merge\",\"nukeUntouchedDevices\":false,\"dcsCleanAllAircraft\":true,\"import\":{\"includeDefaults\":false}}");
        MainWindowViewModel first = Vm(new NoviceGuiTests.NoviceFakeEngine(), persist: true);
        Assert.Equal("clean", first.ExportMode);
        Assert.True(first.ExportNukeUntouchedDevices);
        Assert.False(first.ExportDcsCleanAllAircraft);
        Assert.True(first.ExportKeepKeyboardDefaults);
        Assert.True(first.ImportIncludeDefaults);
        Assert.Equal(0, first.VJoyTunnelDevice);

        // The user picks "keep the game's binds", no nuke, unticks include defaults and names vJoy 3.
        first.ExportMode = "merge";
        first.ExportNukeUntouchedDevices = false;
        first.ImportIncludeDefaults = false;
        first.VJoyTunnelDevice = 3;
        ExportSettings saved = ExportSettingsStore.Load(exportSettings);
        Assert.True(saved.ModeChosenByUser);
        Assert.True(saved.Import.IncludeDefaultsChosen);

        MainWindowViewModel second = Vm(new NoviceGuiTests.NoviceFakeEngine(), persist: true);
        Assert.Equal("merge", second.ExportMode);
        Assert.False(second.ExportNukeUntouchedDevices);
        Assert.False(second.ImportIncludeDefaults);
        Assert.Equal(3, second.VJoyTunnelDevice);
    }

    // ---- The Move panel's line ----

    [Fact]
    public void MovePanel_SaysTheOldButtonKeepsTheAction_OnlyInMergeAndAdd()
    {
        MainWindowViewModel vm = Vm(new NoviceGuiTests.NoviceFakeEngine(), persist: false);
        Assert.Equal("clean", vm.ExportMode);
        Assert.False(vm.HasMoveActionGameNote);
        vm.ExportMode = "merge";
        Assert.True(vm.HasMoveActionGameNote);
        Assert.Contains("old button keeps this action", vm.MoveActionGameNote);
        Assert.Contains("my profile wins", vm.MoveActionGameNote);
        vm.ExportMode = "add";
        Assert.True(vm.HasMoveActionGameNote);
        vm.ExportMode = "merge-overwrite";
        Assert.False(vm.HasMoveActionGameNote);
    }

    // ---- The first export of a game ----

    [Theory]
    [InlineData("cancel")]
    [InlineData("import-first")]
    [InlineData("export-anyway")]
    [InlineData("dont-ask")]
    public async Task FirstExport_AsksImportFirstExportAnywayOrCancel(string answer)
    {
        A2FakeEngine fake = new() { GameState = "{\"Game\":\"WarThunder\",\"Imported\":false,\"Exported\":false,\"GameHasBinds\":true,\"GameBindCount\":37}" };
        MainWindowViewModel vm = Vm(fake, persist: false);
        vm.ProfilePath = Profile();
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        await vm.RefreshExportGamesCommand.ExecuteAsync(null);
        ExportGameItem warThunder = vm.ExportGames.Single(g => g.GameId == "WarThunder");
        List<ConfirmRequest> asked = [];
        vm.ConfirmHandler = request =>
        {
            asked.Add(request);
            if (request.SecondaryLabel.Length == 0)
                return Task.FromResult(true);
            if (answer == "import-first")
                request.SecondaryChosen = true;
            if (answer == "dont-ask")
                request.DontAskAgain = true;
            return Task.FromResult(answer != "cancel");
        };
        fake.Calls.Clear();
        await vm.ExportGameNowCommand.ExecuteAsync(warThunder);

        ConfirmRequest question = Assert.Single(asked, r => r.SecondaryLabel.Length > 0);
        Assert.Equal("Export replaces the binds in War Thunder with your profile (a backup is made, Undo export brings them back). Import first?", question.Message);
        Assert.Equal("Import first", question.SecondaryLabel);
        Assert.Equal("Export anyway", question.ConfirmLabel);
        Assert.Equal(MainWindowViewModel.ConfirmFirstExportKey, question.DontAskKey);
        Assert.Contains(fake.Calls, c => NoviceGuiTests.Seq(c, "profile", "game-state", "--game", "WarThunder"));
        bool exported = fake.Calls.Any(c => NoviceGuiTests.Seq(c, "warthunder", "export"));
        switch (answer)
        {
            case "cancel":
                Assert.False(exported);
                break;
            case "import-first":
                Assert.False(exported);
                Assert.Equal(UiStage.Start, vm.CurrentStage);
                Assert.Equal("WarThunder", vm.ImportWizardGame?.GameId);
                Assert.True(vm.IsImportWizardOptionsStep);
                break;
            default:
                Assert.True(exported);
                // "Export anyway" answers the removal question too: one question only.
                Assert.Single(asked);
                break;
        }
        Assert.Equal(answer != "dont-ask", vm.AskBeforeFirstExport);
        if (answer == "dont-ask")
        {
            asked.Clear();
            await vm.ExportGameNowCommand.ExecuteAsync(warThunder);
            Assert.DoesNotContain(asked, r => r.SecondaryLabel.Length > 0);
            vm.AskAllConfirmationsAgainCommand.Execute(null);
            Assert.True(vm.AskBeforeFirstExport);
        }
    }

    [Theory]
    [InlineData("{\"Imported\":true,\"Exported\":false,\"GameHasBinds\":true,\"GameBindCount\":3}")]
    [InlineData("{\"Imported\":false,\"Exported\":true,\"GameHasBinds\":true,\"GameBindCount\":3}")]
    [InlineData("{\"Imported\":false,\"Exported\":false,\"GameHasBinds\":false,\"GameBindCount\":0}")]
    [InlineData("ok")]
    public async Task FirstExport_NotAsked_WhenImportedExportedEmptyOrUnknown(string state)
    {
        A2FakeEngine fake = new() { GameState = state };
        MainWindowViewModel vm = Vm(fake, persist: false);
        vm.ProfilePath = Profile();
        await vm.RefreshExportGamesCommand.ExecuteAsync(null);
        List<ConfirmRequest> asked = [];
        vm.ConfirmHandler = request =>
        {
            asked.Add(request);
            return Task.FromResult(true);
        };
        await vm.ExportGameNowCommand.ExecuteAsync(vm.ExportGames.Single(g => g.GameId == "WarThunder"));
        Assert.DoesNotContain(asked, r => r.SecondaryLabel.Length > 0);
        // The removal question of clean + nuke (switchable in Settings > Confirmations).
        ConfirmRequest removal = Assert.Single(asked);
        Assert.True(removal.IsDanger);
        Assert.Equal(MainWindowViewModel.ConfirmExportRemovesKey, removal.DontAskKey);
        Assert.Contains("only in the aircraft you export", removal.Message);
        Assert.Contains(fake.Calls, c => NoviceGuiTests.Seq(c, "warthunder", "export") && NoviceGuiTests.Seq(c, "--mode", "clean") && c.Contains("--nuke-untouched-devices"));
    }

    // ---- The watcher hint in the import ----

    [Fact]
    public async Task ImportWatcherHint_OncePerGame_TurnOnOnlyWhenOff_AndDontShowAgain()
    {
        A2FakeEngine fake = new();
        MainWindowViewModel vm = Vm(fake, persist: false);
        GameCardItem dcs = new("DCS", "DCS World", "dcs", "dcs", true, true);
        GameCardItem warThunder = new("WarThunder", "War Thunder", "registry", "registry", true, false);
        vm.GameCards.Add(dcs);
        vm.GameCards.Add(warThunder);

        // No profile: no hint.
        vm.StartImportWizardCommand.Execute(dcs);
        Assert.False(vm.ImportWatcherHintVisible);
        vm.ImportWizardBackCommand.Execute(null);

        vm.ProfilePath = Profile();
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        Assert.True(vm.ProfileHasGame("DCS"));
        Assert.False(vm.ProfileHasGame("WarThunder"));

        // DCS is in the profile and not watched: the line and "Turn on watching for DCS World".
        dcs.Watch = false;
        vm.StartImportWizardCommand.Execute(dcs);
        Assert.True(vm.ImportWatcherHintVisible);
        Assert.Contains("Watch for changes", vm.ImportWatcherHintText);
        Assert.True(vm.ImportWatcherOffersTurnOn);
        Assert.Equal("Turn on watching for DCS World", vm.ImportWatcherTurnOnText);
        vm.TurnOnWatchingFromImportCommand.Execute(null);
        Assert.True(dcs.Watch);
        Assert.False(vm.ImportWatcherOffersTurnOn);
        // "Report a watcher problem": Settings > Report a problem with "Watcher: " started.
        vm.ReportWatcherProblemCommand.Execute(null);
        Assert.Equal(UiStage.Settings, vm.CurrentStage);
        Assert.StartsWith("Watcher: ", vm.ProblemDescription);

        // Once per game: the next import of DCS has no line; a game not in the profile never has one.
        vm.ImportWizardBackCommand.Execute(null);
        vm.StartImportWizardCommand.Execute(dcs);
        Assert.False(vm.ImportWatcherHintVisible);
        vm.ImportWizardBackCommand.Execute(null);
        vm.StartImportWizardCommand.Execute(warThunder);
        Assert.False(vm.ImportWatcherHintVisible);
        vm.ImportWizardBackCommand.Execute(null);

        // A watched game shows no button; "Don't show this again" switches it off (Settings > Confirmations).
        MainWindowViewModel other = Vm(fake, persist: false);
        other.GameCards.Add(dcs);
        other.ProfilePath = Profile("b");
        await other.RefreshRelationsCommand.ExecuteAsync(null);
        other.StartImportWizardCommand.Execute(dcs);
        Assert.True(other.ImportWatcherHintVisible);
        Assert.False(other.ImportWatcherOffersTurnOn);
        other.HideWatcherHintForGoodCommand.Execute(null);
        Assert.False(other.ImportWatcherHintVisible);
        Assert.False(other.ShowWatcherHintInImport);
        Assert.True(other.HasSwitchedOffConfirmations);
    }

    // ---- vJoy ----

    [Fact]
    public async Task VJoyTunnel_PassedToImportsOnlyWhenSet()
    {
        A2FakeEngine fake = new();
        MainWindowViewModel vm = Vm(fake, persist: false);
        vm.ProfilePath = Profile();
        vm.ImportDcsPath = temp;
        await vm.ImportDcsCommand.ExecuteAsync(null);
        IReadOnlyList<string> plain = Assert.Single(fake.Calls, c => NoviceGuiTests.Seq(c, "dcs", "import"));
        Assert.DoesNotContain("--vjoy-tunnel", plain);
        Assert.DoesNotContain(plain, a => a.Contains("vJoy", StringComparison.OrdinalIgnoreCase));

        vm.VJoyTunnelDevice = 3;
        fake.Calls.Clear();
        await vm.ImportDcsCommand.ExecuteAsync(null);
        await vm.ScanIl2Command.ExecuteAsync(null);
        Assert.True(NoviceGuiTests.Seq(Assert.Single(fake.Calls, c => NoviceGuiTests.Seq(c, "dcs", "import")), "--vjoy-tunnel", "3"));
        Assert.True(NoviceGuiTests.Seq(Assert.Single(fake.Calls, c => NoviceGuiTests.Seq(c, "il2", "scan")), "--vjoy-tunnel", "3"));
        Assert.Equal(17, vm.VJoyTunnelOptions.Count);
        vm.VJoyTunnelDevice = 99;
        Assert.Equal(0, vm.VJoyTunnelDevice);
    }

    // ---- Paths and ignored devices per PC, a profile's own ----

    [Fact]
    public void Paths_ArePerPc_HandPathsMarked_AndAProfileMayKeepItsOwn()
    {
        string pcDcs = Path.Combine(temp, "Saved Games", "DCS.openbeta");
        string ownDcs = Path.Combine(temp, "Other", "DCS");
        Directory.CreateDirectory(pcDcs);
        Directory.CreateDirectory(ownDcs);
        string profileA = Profile("pa");
        string profileB = Profile("pb");
        MainWindowViewModel first = Vm(new NoviceGuiTests.NoviceFakeEngine(), persist: true);
        first.ProfilePath = profileA;
        first.ExportDcsPath = pcDcs;
        first.ImportDcsPath = pcDcs;
        first.ImportIgnoreDevicesCsv = "Old Pedals";
        Assert.True(first.IsHandPath(nameof(MainWindowViewModel.ExportDcsPath)));
        Assert.False(first.ProfileOwnPaths);

        // Profile B keeps its own path and ignored devices.
        first.ProfilePath = profileB;
        Assert.Equal(pcDcs, first.ExportDcsPath);
        first.ProfileOwnPaths = true;
        first.ExportDcsPath = ownDcs;
        first.ImportIgnoreDevicesCsv = "vJoy Device";
        ExportSettings saved = ExportSettingsStore.Load(exportSettings);
        Assert.Equal(pcDcs, saved.Paths["dcs"]);
        Assert.Equal("Old Pedals", saved.Import.IgnoreDevicesCsv);
        Assert.Contains("dcs", saved.HandPaths);

        // Back to A: the PC's again; a restart with B: B's own.
        first.ProfilePath = profileA;
        Assert.Equal(pcDcs, first.ExportDcsPath);
        Assert.Equal("Old Pedals", first.ImportIgnoreDevicesCsv);
        MainWindowViewModel second = Vm(new NoviceGuiTests.NoviceFakeEngine(), persist: true);
        Assert.Equal(pcDcs, second.ExportDcsPath);
        Assert.Equal(pcDcs, second.ImportDcsPath);
        second.ProfilePath = profileB;
        Assert.True(second.ProfileOwnPaths);
        Assert.Equal(ownDcs, second.ExportDcsPath);
        Assert.Equal("vJoy Device", second.ImportIgnoreDevicesCsv);
        // Off again: the PC's paths.
        second.ProfileOwnPaths = false;
        Assert.Equal(pcDcs, second.ExportDcsPath);
        Assert.Equal("Old Pedals", second.ImportIgnoreDevicesCsv);
    }

    // ---- "Only add": binds not written ----

    private const string MarksJson = "{\"Marks\":[" +
        "{\"Game\":\"DCS\",\"Aircraft\":\"F-16C_50\",\"Relation\":\"Gear\",\"ActionId\":\"gear\",\"Action\":\"Landing Gear Up\",\"Device\":\"" + "Joystick - HOTAS Warthog {00000000-0000-0000-0000-0000000000a1}" +
        "\",\"Input\":\"JOY_BTN5\",\"Modifiers\":[],\"GameActionId\":\"d3001\",\"GameAction\":\"Weapon Release\",\"Date\":\"2026-10-31\",\"Dismissed\":false}," +
        "{\"Game\":\"DCS\",\"Aircraft\":\"A-10C\",\"Relation\":\"Old\",\"ActionId\":\"x\",\"Action\":\"X\",\"Device\":\"D\",\"Input\":\"JOY_BTN1\",\"Modifiers\":[],\"GameActionId\":\"y\",\"GameAction\":\"Y\",\"Date\":\"2026-10-01\",\"Dismissed\":true}]}";

    [Fact]
    public async Task ExportMarks_ChipOnTheAction_CardInTheCheck_AndItsFixes()
    {
        A2FakeEngine fake = new() { Marks = MarksJson };
        MainWindowViewModel vm = Vm(fake, persist: false);
        vm.ProfilePath = Profile();
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.LoadExportMarksAsync();
        Assert.Equal(2, vm.ExportMarks.Count);

        // The Check: one card (the dismissed mark has none) with its fixes.
        CheckIssueSection section = Assert.Single(vm.CheckSections, s => s.Kind == "export-skips");
        CheckIssueCard card = Assert.Single(section.AllCards);
        Assert.Equal("Not written to DCS World", card.Title);
        Assert.Equal("HOTAS Warthog · BTN 5 ('Gear')", card.ButtonsLine.Replace("Joystick - HOTAS Warthog", "HOTAS Warthog"));
        Assert.Contains("'Weapon Release'", card.Sentence);
        Assert.Contains("Landing Gear Up", card.Sentence);
        Assert.True(card.HasRecommended);
        Assert.Equal("Export with 'Update' to replace it", card.Recommended!.Text);
        Assert.Equal(["Move to another button", "Keep as is"], card.Fixes.Select(f => f.Text));
        Assert.Single(card.OpenButtons);

        // The chip on the action row of the relation.
        vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Gear");
        await WaitForAsync(() => vm.RelationNodes.Count > 0);
        RelationNodeItem node = Assert.Single(vm.RelationNodes);
        Assert.True(node.HasExportSkip);
        Assert.Equal("Not exported: input taken in DCS World by 'Weapon Release' (F-16C_50; 2026-10-31)", node.ExportSkipText);

        // "Move to another button": the Move panel for that action.
        card.Fixes[0].Command!.Execute(null);
        Assert.True(vm.IsMoveActionOpen);
        Assert.Equal("Landing Gear Up", vm.SelectedMoveAction?.Title);
        vm.CancelMoveActionCommand.Execute(null);

        // "Export with 'Update'": that game once with merge; the chosen mode stays.
        fake.Calls.Clear();
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)card.Recommended!.Command!).ExecuteAsync(null);
        Assert.True(NoviceGuiTests.Seq(Assert.Single(fake.Calls, c => NoviceGuiTests.Seq(c, "dcs", "export")), "--mode", "merge"));
        Assert.Equal("clean", vm.ExportMode);

        // "Keep as is": dismissed in the profile, no card any more, no chip.
        fake.Calls.Clear();
        fake.Marks = MarksJson.Replace("\"Dismissed\":false", "\"Dismissed\":true");
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.CheckSections.Single(s => s.Kind == "export-skips").AllCards[0].Fixes[1].Command!).ExecuteAsync(null);
        Assert.True(NoviceGuiTests.Seq(Assert.Single(fake.Calls, c => NoviceGuiTests.Seq(c, "export-marks", "dismiss")), "--game", "DCS", "--relation", "Gear", "--aircraft", "F-16C_50"));
        Assert.DoesNotContain(vm.CheckSections, s => s.Kind == "export-skips");
        Assert.False(node.HasExportSkip);
    }

    [Fact]
    public void ExportMarks_ParseToleratesOldEngines()
    {
        Assert.Empty(MainWindowViewModel.ParseExportMarks("ok"));
        Assert.Empty(MainWindowViewModel.ParseExportMarks("{}"));
        Assert.Equal(2, MainWindowViewModel.ParseExportMarks(MarksJson).Count);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    // NoviceFakeEngine plus the 2.5.0 commands: profile game-state, export-marks, a profile with a DCS relation.
    internal sealed class A2FakeEngine : IEngineCliClient
    {
        private readonly NoviceGuiTests.NoviceFakeEngine inner = new();

        public List<IReadOnlyList<string>> Calls => inner.Calls;

        public string GameState { get; set; } = "ok";

        public string Marks { get; set; } = "{\"Marks\":[]}";

        public async Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            EngineCommandResult result = await inner.RunAsync(engineExePath, arguments, cancellationToken);
            string? output = null;
            if (NoviceGuiTests.Seq(arguments, "profile", "game-state"))
                output = GameState;
            else if (NoviceGuiTests.Seq(arguments, "export-marks", "list"))
                output = Marks;
            else if (NoviceGuiTests.Seq(arguments, "export-marks", "dismiss"))
                output = "{\"Dismissed\":1}";
            else if (NoviceGuiTests.Seq(arguments, "relations", "list"))
                output = "{\"Relations\":[{\"Name\":\"Gear\",\"IsAxis\":false,\"Groups\":[],\"Nodes\":[{\"Id\":\"gear\",\"Game\":\"DCS\",\"Aircraft\":{\"F-16C_50\":true}}]," +
                         "\"Bind\":{\"Joystick\":\"" + Stick + "\",\"JAxis\":\"\",\"JButton\":\"JOY_BTN5\"}}]}";
            else if (NoviceGuiTests.Seq(arguments, "relations", "show"))
                output = string.Join(Environment.NewLine, "Relation: Gear", "Nodes:", "  [1] game=DCS, id=gear, aircraft=F-16C_50");
            if (output == null)
                return result;
            return new EngineCommandResult { ExitCode = 0, Duration = result.Duration, StandardOutput = output, StandardError = string.Empty, Arguments = result.Arguments };
        }
    }
}
