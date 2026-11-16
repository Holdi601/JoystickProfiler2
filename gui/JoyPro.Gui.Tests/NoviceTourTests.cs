using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Services.Help;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Tours, welcome, quick setup and F1 for someone who reads slowly (docs/ui/help-and-tours.md "Easy words"): the easy
// words switch (kept over a restart, easy texts, big cards, help with one plain sentence first), short sentences
// everywhere a novice reads (a build check: tour cards and F1's first sentences), the quick tour running after "Write to
// my game", one setup window at a time, the pad path ending with the pad steps, one rule for when to press Start, and
// German "du" only with easy words.
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class NoviceTourTests : IDisposable
{
    // The path tours a novice gets (the JoyPro 1 tour is for experts).
    private static readonly string[] NoviceTours = [TourCatalog.Pad, TourCatalog.Quick, TourCatalog.Newcomer, TourCatalog.Dcs];

    // The controls a novice clicks first: F1 leads with a plain sentence of at most 12 words for each.
    private static readonly (string Page, string Key)[] NoviceControls =
    [
        ("header", "GoStartCommand"), ("header", "GoRelationsCommand"), ("header", "GoBindsCommand"), ("header", "GoExportCommand"),
        ("header", "GoVirtualJoystickCommand"), ("header", "GoSettingsCommand"), ("header", "HelpButton"), ("header", "ToggleHelpModeCommand"),
        ("header", "StartPageTourCommand"), ("header", "OpenWelcomeCommand"), ("header", "UndoLastChangeCommand"), ("check", "ValidateProfileCommand"),
        ("start", "QuickSetupCommand"), ("start", "OpenSetupWizardCommand"), ("start", "StartImportWizardCommand"),
        ("binds", "RecordNextInputCommand"), ("export", "ExportSelectedGamesCommand"), ("export", "UndoLastExportCommand"),
        ("vjoy", "StartVirtualJoystickCommand"), ("vjoy", "TestVirtualJoystickCommand"), ("vjoy", "StopVirtualJoystickCommand"),
        ("help", "TourNextCommand"), ("help", "TourBackCommand"), ("help", "TourSkipCommand"), ("help", "EndTourCommand"),
        ("help", "StartWelcomeChoiceCommand"), ("help", "SkipWelcomeCommand"), ("help", "CloseHelpPopupCommand"), ("help", "EndHelpModeCommand"),
        ("help", "TourDoActionCommand"), ("help", "bind:EasyWords"), ("settings", "bind:EasyWords"),
        ("setup-wizard", "ApplyCommand"), ("setup-wizard", "ExportCommand"), ("setup-wizard", "ToggleChangeSomethingCommand"),
        ("setup-wizard", "QuickTryAgainCommand"), ("setup-wizard", "QuickStepByStepCommand"),
        ("first-start", "click:DoneClicked"), ("first-start", "click:QuickPathClicked"), ("first-start", "click:LaterClicked")
    ];

    private readonly string uiSettings = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");

    public NoviceTourTests()
    {
        Directory.CreateDirectory(AppInfo.SettingsFolder);
        File.Delete(uiSettings);
    }

    public void Dispose()
    {
        File.Delete(uiSettings);
        Loc.SetLanguage("en");
    }

    private static MainWindowViewModel Vm(bool persist) =>
        new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: persist)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location,
            WelcomeIgnoresSandbox = true
        };

    // ---- easy words ----

    [Fact]
    public void EasyWords_IsKeptOverARestart_AndAResetOfTheToursKeepsIt()
    {
        MainWindowViewModel first = Vm(persist: true);
        Assert.False(first.EasyWords);
        first.EasyWords = true;
        Assert.True(first.ToursState.EasyWords);

        MainWindowViewModel second = Vm(persist: true);
        Assert.True(second.EasyWords);
        second.ResetToursCommand.Execute(null);
        Assert.True(second.EasyWords);

        MainWindowViewModel third = Vm(persist: true);
        Assert.True(third.EasyWords);
        third.EasyWords = false;
        Assert.False(Vm(persist: true).EasyWords);
    }

    [Fact]
    public void EasyWords_ShowsTheEasyTexts_BigCards_AndHelpWithThePlainSentenceFirst()
    {
        MainWindowViewModel vm = Vm(persist: false);
        vm.InstalledGamesForWelcome = () => ["AceCombat8"];
        vm.StartTour(TourCatalog.Pad, everyFeature: true);
        TourStep step = vm.CurrentTourStep!;
        Assert.False(vm.TourIsBig);
        Assert.Equal(Loc.T(step.Text), vm.TourText);

        vm.EasyWords = true;
        Assert.True(vm.TourIsBig);
        Assert.Equal(Loc.T(step.Easy), vm.TourText);
        Assert.Equal(TourPictures.Pad, vm.TourPicture);
        Assert.False(string.IsNullOrEmpty(vm.TourPictureCaption));

        // The welcome's answers in easy words; the switch is on the welcome too.
        vm.OpenWelcome();
        Assert.Contains(vm.WelcomeOptions, o => o.Id == TourCatalog.Pad && o.Description == Loc.T("For games that only know an Xbox controller."));
        Assert.Contains("AceCombat8", vm.WelcomeSuggestion);

        // F1: the plain sentence leads, the details start closed with easy words, open without.
        HelpEntry record = HelpCatalog.Entries.Single(e => e.Page == "binds" && e.Key == "RecordNextInputCommand");
        vm.ShowHelp(new HelpTopic("Record input", "Button", Loc.T(record.What), "fits", "next", "", HelpSource.Catalog, [], [], "binds", record.Key) { Plain = Loc.T(record.Easy) });
        Assert.Equal(Loc.T(record.Easy), vm.HelpTopic!.Lead);
        Assert.False(vm.IsHelpDetailsOpen);
        vm.EasyWords = false;
        vm.ShowHelp(vm.HelpTopic);
        Assert.True(vm.IsHelpDetailsOpen);
        vm.EndTourCommand.Execute(null);
    }

    // ---- the build check: short sentences where a novice reads ----

    [Fact]
    public void TourCards_HaveShortSentences_AndEveryNoviceStepHasEasyWords()
    {
        List<string> problems = [];
        foreach (string id in NoviceTours)
        {
            TourDefinition tour = TourCatalog.Find(id)!;
            foreach (TourStep step in tour.Steps)
            {
                if (step.Easy.Length == 0)
                    problems.Add($"{id} \"{step.Title}\": no easy text");
                foreach (string sentence in Sentences(step.Text))
                {
                    if (Words(sentence) > 15)
                        problems.Add($"{id} \"{step.Title}\": {Words(sentence)} words: {sentence}");
                }
                foreach (string sentence in Sentences(step.Easy))
                {
                    if (Words(sentence) > 12)
                        problems.Add($"{id} \"{step.Title}\" (easy): {Words(sentence)} words: {sentence}");
                }
                if (Sentences(step.Easy).Count > 4)
                    problems.Add($"{id} \"{step.Title}\" (easy): more than 4 sentences");
                // JoyPro's words only where the card itself says what they mean.
                foreach (string word in new[] { "HidHide", "vJoy", "mapping", "driver", "catalog", "engine" })
                {
                    if (step.Easy.Contains(word, StringComparison.OrdinalIgnoreCase))
                        problems.Add($"{id} \"{step.Title}\" (easy): uses \"{word}\"");
                }
            }
            if (tour.EasyDescription.Length == 0)
                problems.Add($"{id}: no easy description for the welcome");
        }
        foreach (HelpPageText page in HelpCatalog.Pages)
        {
            if (page.Easy.Length == 0 || Sentences(page.Easy).Any(s => Words(s) > 12))
                problems.Add($"page {page.Slug}: easy text missing or over 12 words: {page.Easy}");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void F1_TheControlsANoviceClicks_LeadWithAPlainSentenceOfAtMost12Words()
    {
        List<string> problems = [];
        foreach ((string page, string key) in NoviceControls)
        {
            HelpEntry? entry = HelpCatalog.Entries.FirstOrDefault(e => e.Page == page && e.Key == key);
            if (entry == null)
                problems.Add($"{page}/{key}: not in the catalog");
            else if (entry.Easy.Length == 0)
                problems.Add($"{page}/{key}: no plain sentence (<!-- easy: ... --> in website/content/gui/{page}.md)");
        }
        foreach (HelpEntry entry in HelpCatalog.Entries.Where(e => e.Easy.Length > 0))
        {
            foreach (string sentence in Sentences(entry.Easy))
            {
                if (Words(sentence) > 12)
                    problems.Add($"{entry.Page}/{entry.Key}: {Words(sentence)} words: {sentence}");
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // ---- German: "du" only with easy words, "Sie" otherwise ----

    [Fact]
    public void German_EasyWordsSayDu_TheNormalTextsSaySie()
    {
        Loc.SetLanguage("de");
        try
        {
            // The formal address inside a sentence ("Klicken Sie", "Ihr Stick"); "Sie ist ..." at a sentence start is "it".
            Regex formal = new(@"(?<!(^|[.!?:]\s))\b(Sie|Ihr|Ihre|Ihren|Ihrem|Ihrer|Ihres)\b");
            Regex informal = new(@"\b(du|dein|deine|deinen|deinem|deiner|dich|dir)\b", RegexOptions.IgnoreCase);
            List<string> problems = [];
            foreach (string id in NoviceTours)
            {
                foreach (TourStep step in TourCatalog.Find(id)!.Steps)
                {
                    // Every part of a card: the text, a two-part step's second part, the waiting card, the skip warning.
                    foreach ((string normalText, string easyText) in new[] { (step.Text, step.Easy), (step.Then, step.ThenEasy), (step.Waiting, step.WaitingEasy), (step.WarnSkip, step.WarnSkipEasy) })
                    {
                        if (normalText.Length == 0)
                            continue;
                        string easy = Loc.T(easyText);
                        string normal = Loc.T(normalText);
                        if (easy == easyText)
                            problems.Add($"not translated: {easyText}");
                        if (normal == normalText)
                            problems.Add($"not translated: {normalText}");
                        if (formal.IsMatch(easy))
                            problems.Add($"easy says Sie: {easy}");
                        if (informal.IsMatch(normal))
                            problems.Add($"normal says du: {normal}");
                    }
                    // A title shows with both: neutral (no Sie, no du).
                    string title = Loc.T(step.Title);
                    if (formal.IsMatch(title) || informal.IsMatch(title) || Regex.IsMatch(title, @"^(Ihr|Ihre|Dein|Deine)\b"))
                        problems.Add($"title not neutral: {title}");
                }
            }
            // UI buttons are "Schaltfläche" in the help, "Taste" is a button on the stick.
            Assert.Equal("Schaltfläche", Loc.T(HelpCatalog.KindName("Button")));
            Assert.Equal("Belegung", Loc.T("Bind"));
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Distinct()));
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    }

    // ---- one rule for when to press Start (pad games: after the title screen) ----

    [Fact]
    public void StartTiming_IsTheSameInTheTour_F1_ThePageHelp_AndThePageTour()
    {
        TourStep tourStep = TourCatalog.Find(TourCatalog.Pad)!.Steps.Single(s => s.Target == "cmd:StartVirtualJoystickCommand");
        Assert.Contains("title screen", tourStep.Text);
        Assert.Contains("title screen", tourStep.Easy);
        HelpEntry start = HelpCatalog.Entries.Single(e => e.Page == "vjoy" && e.Key == "StartVirtualJoystickCommand");
        Assert.Contains("title screen", start.What + " " + start.Next);
        Assert.Contains("title screen", HelpCatalog.Page("vjoy").Next);
        TourStep pageStep = TourCatalog.PageTour("vjoy")!.Steps.Single(s => s.Target == "cmd:StartVirtualJoystickCommand");
        Assert.Contains("title screen", pageStep.Text);
        // Nobody tells a pad player to press Start before the game.
        foreach (string text in new[] { tourStep.Text, start.What, start.Next, HelpCatalog.Page("vjoy").Next, pageStep.Text })
            Assert.DoesNotMatch(@"(?i)^(press|start) it before the game", text);
        // The Overview of a pad game does not say "switch" and "export".
        string axaml = File.ReadAllText(Path.Combine(RepoRoot(), "gui", "JoyPro.Gui", "Views", "VirtualJoystickView.axaml"));
        int padSteps = axaml.IndexOf("<StackPanel Spacing=\"6\" IsVisible=\"{Binding VirtualJoystickPadOnly}\"", StringComparison.Ordinal);
        string padBlock = axaml[padSteps..axaml.IndexOf("</StackPanel>", padSteps, StringComparison.Ordinal)];
        Assert.Contains("title screen", padBlock);
        Assert.Contains("Alt and press Tab", padBlock);
        Assert.DoesNotContain("Switch the game", padBlock);
        Assert.Contains("skip steps 1 and 2", axaml);
    }

    // ---- the pad tour's quick setup, the pad path's end, the live test ----

    [Fact]
    public void PadTour_Step2_OpensTheQuickSetup_AndTheLiveTestStepAsksForTest()
    {
        TourDefinition pad = TourCatalog.Find(TourCatalog.Pad)!;
        TourStep setup = pad.Steps[1];
        Assert.Equal("cmd:QuickSetupCommand", setup.Target);
        Assert.Equal(TourActions.QuickSetup, setup.Action);
        Assert.Contains("Use these buttons", setup.Text);
        Assert.Contains("Set up the pretend controller", setup.Text);
        TourStep test = pad.Steps.Single(s => s.OpenTab == "Live test");
        Assert.Equal("cmd:TestVirtualJoystickCommand", test.Target);
        Assert.StartsWith("Click Test.", test.Text);
        Assert.StartsWith("Click Test.", test.Easy);
        Assert.True(test.WaitClick);
    }

    // M1 (second review): the pad tour never runs ahead of the quick setup window. While it is open the card waits
    // ("Finish the setup window first"), Next brings the window to the front; closed without the buttons the step shows
    // again; with the buttons applied the pad steps follow.
    [Fact]
    public async Task PadTour_SetupStep_WaitsForTheSetupWindow_UntilItIsAppliedOrClosed()
    {
        QuickSetupTests.QuickFakeEngine engine = new();
        MainWindowViewModel vm = await QuickSetupTests.NewVmAsync(engine);
        int fronted = 0;
        vm.SetupWizardRequested += () => fronted++;
        vm.StartTour(TourCatalog.Pad, everyFeature: false, step: 1);
        Assert.True(vm.TourShowsQuickSetupAction);
        vm.TourNextCommand.Execute(null);
        for (int i = 0; i < 100 && (vm.SetupWizard == null || vm.QuickSetupRunning); i++)
            await Task.Delay(30);
        Assert.NotNull(vm.SetupWizard);
        Assert.True(vm.SetupWizard!.QuickMode);
        // The card waits on its step: no glow elsewhere, no second blue "Set it all up for me".
        Assert.Equal(1, vm.TourStepIndex);
        Assert.True(vm.TourWaitsForSetup);
        Assert.Equal("Finish the setup window first", vm.TourTitle);
        Assert.Contains("This card waits for you.", vm.TourText);
        Assert.Equal("none", vm.TourTargetKey);
        Assert.False(vm.TourShowsQuickSetupAction);
        Assert.False(vm.TourShowsWaitHint);
        Assert.Equal("Show the setup window", vm.TourNextLabel);
        // A page change does not move it on either (the old card jumped to "Open Virtual Joystick").
        vm.CurrentStage = UiStage.VirtualJoystick;
        Assert.Equal(1, vm.TourStepIndex);
        // Next brings the window to the front, the card keeps waiting.
        int before = fronted;
        vm.TourNextCommand.Execute(null);
        Assert.Equal(before + 1, fronted);
        Assert.Equal(1, vm.TourStepIndex);
        Assert.True(vm.TourWaitsForSetup);
        // Closed without "Use these buttons": the setup step again.
        vm.OnSetupWizardWindowClosed();
        Assert.False(vm.TourWaitsForSetup);
        Assert.Equal(1, vm.TourStepIndex);
        Assert.True(vm.TourShowsQuickSetupAction);
        // The page's own button (a click on the glowing target) waits the same way.
        vm.TourTargetClicked();
        Assert.True(vm.TourWaitsForSetup);
        Assert.Equal(1, vm.TourStepIndex);
        // Applied, then the window closed: the pad steps follow on the Virtual Joystick page.
        await vm.SetupWizard!.ApplyCommand.ExecuteAsync(null);
        Assert.True(vm.SetupWizard.Applied, vm.SetupWizard.Status);
        vm.OnSetupWizardWindowClosed();
        Assert.False(vm.TourWaitsForSetup);
        Assert.Equal(UiStage.VirtualJoystick, vm.CurrentStage);
        Assert.Equal(UiStage.VirtualJoystick, vm.CurrentTourStep!.Stage);
        vm.EndTourCommand.Execute(null);
    }

    // B1 (second review): the helper's Install button is grey until its license box is ticked. The card says so and
    // points at the box first, then at the button; Next without the helper first says what will not work.
    [Fact]
    public void PadTour_InstallStep_TheBoxFirst_ThenInstall_AndNextWarnsWithoutTheHelper()
    {
        MainWindowViewModel vm = Vm(persist: false);
        vm.InstalledGamesForWelcome = () => ["AceCombat8"];
        List<TourStep> steps = TourCatalog.Find(TourCatalog.Pad)!.StepsFor(false).ToList();
        int install = steps.FindIndex(s => s.SkipIf == TourConditions.ViGEmInstalled);
        Assert.False(vm.ViGEmInstalled);
        ComponentItem vigem = vm.VirtualJoystickViGEmComponent!;
        vigem.Consent = false;
        vm.StartTour(TourCatalog.Pad, everyFeature: false, step: install);
        Assert.Equal(install, vm.TourStepIndex);
        Assert.Contains("tick the box", vm.TourText);
        Assert.Contains("Install ViGEmBus", vm.TourText);
        Assert.DoesNotContain("Click Install.", vm.TourText);
        Assert.StartsWith("name:VirtualJoystickViGEmNeeded>ComponentConsentBox", vm.TourTargetKey);
        Assert.True(vm.TourListensForClick);
        vm.EasyWords = true;
        Assert.Contains("Tick the box", vm.TourText);
        Assert.Contains("grown-up", vm.TourText);
        vm.EasyWords = false;
        // The box ticked: "now click Install ViGEmBus", the glow moves to the button.
        vigem.Consent = true;
        Assert.True(vm.TourInSecondPart);
        Assert.StartsWith("Good. Now click Install ViGEmBus.", vm.TourText);
        Assert.StartsWith("name:VirtualJoystickViGEmNeeded>ComponentInstallButton", vm.TourTargetKey);
        // Next without the helper: first what will not work, then on.
        vm.TourNextCommand.Execute(null);
        Assert.Equal(install, vm.TourStepIndex);
        Assert.Contains("cannot work", vm.TourText);
        Assert.Contains("Next again", vm.TourText);
        vm.TourNextCommand.Execute(null);
        Assert.Equal(install + 1, vm.TourStepIndex);
        // Skip warns the same way.
        vm.TourBackCommand.Execute(null);
        Assert.Equal(install, vm.TourStepIndex);
        vm.TourSkipCommand.Execute(null);
        Assert.Equal(install, vm.TourStepIndex);
        vm.TourSkipCommand.Execute(null);
        Assert.Equal(install + 1, vm.TourStepIndex);
        // With the helper installed the step goes on by itself, no warning.
        vm.TourBackCommand.Execute(null);
        Assert.Equal(install, vm.TourStepIndex);
        vm.ViGEmInstalled = true;
        Assert.NotEqual(install, vm.TourStepIndex);
        vigem.Consent = false;
        vm.EndTourCommand.Execute(null);
    }

    // M5 (second review): the Test card stays after the click and says to move the stick; only Next goes on.
    [Fact]
    public void PadTour_TestStep_StaysAfterTheClick_UntilNext()
    {
        MainWindowViewModel vm = Vm(persist: false);
        vm.InstalledGamesForWelcome = () => ["AceCombat8"];
        vm.ViGEmInstalled = true;
        List<TourStep> steps = TourCatalog.Find(TourCatalog.Pad)!.StepsFor(false).ToList();
        int test = steps.FindIndex(s => s.OpenTab == "Live test");
        vm.StartTour(TourCatalog.Pad, everyFeature: false, step: test);
        Assert.StartsWith("Click Test.", vm.TourText);
        Assert.True(vm.TourShowsWaitHint);
        vm.TourTargetClicked();
        Assert.Equal(test, vm.TourStepIndex);
        Assert.Equal("Now move your stick. Does the picture move? Then click Next.", vm.TourText);
        Assert.False(vm.TourShowsWaitHint);
        Assert.Equal("cmd:TestVirtualJoystickCommand", vm.TourTargetKey);
        // A second click changes nothing; Next goes on.
        vm.TourTargetClicked();
        Assert.Equal(test, vm.TourStepIndex);
        vm.TourNextCommand.Execute(null);
        Assert.Equal(test + 1, vm.TourStepIndex);
        Assert.Contains("Alt", vm.TourText);
        Assert.Equal(TourPictures.AltTab, vm.TourPicture);
        vm.EndTourCommand.Execute(null);
    }

    [Fact]
    public async Task PadGame_TheQuickSetupEndsWithThePadSteps_NotWriteToMyGame()
    {
        QuickSetupPersonaTests.PersonaEngine engine = QuickSetupPersonaTests.PersonaEngine.Create("tflight-hotas4.tsv", "AceCombat8", "ACE COMBAT 8: Wings of Theve");
        Assert.False(engine.Missing, "Build the engine first (engine/bin/x64/" + TestEngine.Configuration + "/engine.exe).");
        MainWindowViewModel vm = await QuickSetupPersonaTests.FirstStartAsync(engine, "AceCombat8");
        await vm.StartQuickSetupAsync(tourAfter: true);
        SetupWizardViewModel wizard = vm.SetupWizard!;
        Assert.True(wizard.IsReviewStep, wizard.Status + " / " + wizard.QuickSummary);
        Assert.True(wizard.PadGamesOnly);
        Assert.Equal("Set up the pretend controller", wizard.ExportText);
        // Plain names, never ids written together.
        Assert.DoesNotContain(wizard.ReviewRows, r => Regex.IsMatch(r.Name, "^[A-Z][a-z]+[A-Z][a-z]+"));
        await wizard.ApplyCommand.ExecuteAsync(null);
        Assert.True(wizard.Applied, wizard.Status);
        Assert.Contains("Set up the pretend controller", wizard.Status);
        Assert.DoesNotContain("Write to my game", wizard.Status);
        // One main button: "Use these buttons" is done.
        Assert.False(wizard.CanApply);
        bool closed = false;
        wizard.CloseRequested += () => closed = true;
        wizard.ExportCommand.Execute(null);
        Assert.True(closed);
        Assert.Equal(UiStage.VirtualJoystick, vm.CurrentStage);
        Assert.Equal(TourCatalog.Pad, vm.ActiveTour?.Id);
        Assert.Equal(UiStage.VirtualJoystick, vm.CurrentTourStep!.Stage);
        // The steps that follow: the helper (when missing), Test, and Start after the title screen.
        Assert.Contains(vm.ActiveTour!.StepsFor(false), s => s.Target == "cmd:StartVirtualJoystickCommand");
        vm.EndTourCommand.Execute(null);
    }

    // ---- the quick tour runs, one setup window at a time ----

    [Fact]
    public async Task QuickTour_RunsAfterWriteToMyGame_AndEndsWithDoneStartTheGame()
    {
        QuickSetupTests.QuickFakeEngine engine = new();
        MainWindowViewModel vm = await QuickSetupTests.NewVmAsync(engine);
        vm.ConfirmHandler = _ => Task.FromResult(true);
        vm.InstalledGamesForWelcome = () => ["IL2Game"];
        vm.OpenWelcome();
        Assert.Equal(TourCatalog.Quick, vm.WelcomeOptions.Single(o => o.IsSelected).Id);
        await vm.StartWelcomeChoiceCommand.ExecuteAsync(null);
        SetupWizardViewModel wizard = vm.SetupWizard!;
        Assert.True(wizard.IsReviewStep, wizard.Status + " / " + wizard.QuickSummary);
        Assert.Null(vm.ActiveTour);
        await wizard.ApplyCommand.ExecuteAsync(null);
        bool closed = false;
        wizard.CloseRequested += () => closed = true;
        await vm.QuickExportAsync(wizard.ChosenGames);
        Assert.Contains("IL2Game", vm.LastExportedGames);
        Assert.True(closed);
        Assert.Equal(TourCatalog.Quick, vm.ActiveTour?.Id);
        // M4 (second review): the success first, on the Start page (not on the Export page and its warnings).
        Assert.Equal("It worked!", vm.CurrentTourStep!.Title);
        Assert.Equal(UiStage.Start, vm.CurrentStage);
        Assert.StartsWith("It worked! IL-2", vm.TourText);
        Assert.Contains("start the game now", vm.TourText);
        Assert.Equal("none", vm.TourTargetKey);
        Assert.Equal("Step 1 of " + vm.ActiveTour!.StepsFor(false).Count, vm.TourCounter);
        vm.TourNextCommand.Execute(null);
        Assert.Equal("Change a button later", vm.CurrentTourStep!.Title);
        // The last card: "Done! Start <game>.", on the Start page again; Skip is not next to Finish.
        while (vm.TourStepIndex < vm.ActiveTour!.StepsFor(false).Count - 1)
            vm.TourSkipCommand.Execute(null);
        Assert.Equal("Done!", vm.CurrentTourStep!.Title);
        Assert.Equal(UiStage.Start, vm.CurrentStage);
        Assert.Contains("IL-2", vm.TourText);
        Assert.False(vm.TourShowsSkip);
        vm.TourNextCommand.Execute(null);
        Assert.Null(vm.ActiveTour);
        Assert.Contains(TourCatalog.Quick, vm.ToursState.Seen);
        // Started from Settings > Tours (nothing was written): no "It worked!".
        vm.StartTourByIdCommand.Execute(TourCatalog.Quick);
        Assert.Equal("Change a button later", vm.CurrentTourStep!.Title);
        vm.EndTourCommand.Execute(null);
    }

    // M3 (second review): Record input shows only once a game control is picked. The tours ask for the pick first (the
    // list glows, a pick goes on by itself, Next picks the first one), and the Record step picks one when none is.
    [Theory]
    [InlineData(TourCatalog.Quick)]
    [InlineData(TourCatalog.Newcomer)]
    public void RecordSteps_PickARowFirst_ThenRecordInputShows(string tourId)
    {
        MainWindowViewModel vm = Vm(persist: false);
        vm.Binds.Add(new BindListItem { RelationName = "Fire gun", Type = "Button" });
        vm.Binds.Add(new BindListItem { RelationName = "Flares", Type = "Button" });
        List<TourStep> steps = TourCatalog.Find(tourId)!.StepsFor(false).ToList();
        int pick = steps.FindIndex(s => s.WaitFor == TourConditions.BindSelected);
        int record = steps.FindIndex(s => s.Target == "cmd:RecordNextInputCommand");
        Assert.True(pick >= 0 && record == pick + 1, tourId + ": the list step comes right before Record input");
        Assert.Equal("name:BindsListBox", steps[pick].Target);
        Assert.Equal(TourPrepare.SelectBind, steps[record].Prepare);

        // A click on a row goes on by itself.
        vm.StartTour(tourId, everyFeature: false, step: pick);
        Assert.Null(vm.SelectedBind);
        Assert.True(vm.TourWaits);
        Assert.Contains("list", vm.TourWaitHint);
        vm.SelectedBind = vm.Binds[1];
        Assert.Equal(record, vm.TourStepIndex);
        Assert.True(vm.HasBindSelected);

        // Next on the list step picks the first row and goes on.
        vm.SelectedBind = null;
        vm.StartTour(tourId, everyFeature: false, step: pick);
        vm.TourNextCommand.Execute(null);
        Assert.Equal(record, vm.TourStepIndex);
        Assert.Same(vm.Binds[0], vm.SelectedBind);

        // Skipped to Record input with nothing picked: the step picks the first row, so the button is there.
        vm.SelectedBind = null;
        vm.StartTour(tourId, everyFeature: false, step: record);
        Assert.True(vm.HasBindSelected);
        vm.EndTourCommand.Execute(null);
    }

    // The F1 help of Record input and of the Binds page says to pick a row first.
    [Fact]
    public void F1_RecordInput_SaysToPickAGameControlFirst()
    {
        HelpEntry record = HelpCatalog.Entries.Single(e => e.Page == "binds" && e.Key == "RecordNextInputCommand");
        Assert.Contains("Pick a game control first", record.Easy);
        Assert.Contains("click a game control in the list", HelpCatalog.Page("binds").Next);
    }

    // M7 (second review): the quick review's status line is plain and calm: no "not placed", no "modifier".
    [Fact]
    public async Task QuickReview_StatusLine_IsPlainAndCalm()
    {
        QuickSetupTests.QuickFakeEngine engine = new();
        MainWindowViewModel vm = await QuickSetupTests.NewVmAsync(engine);
        await vm.StartQuickSetupAsync();
        SetupWizardViewModel wizard = vm.SetupWizard!;
        Assert.True(wizard.IsReviewStep, wizard.Status);
        foreach (string text in new[] { wizard.Status, wizard.PlanHeadline, wizard.QuickSummary })
        {
            Assert.DoesNotContain("not placed", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("modifier", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Done!", text, StringComparison.Ordinal);
        }
        Assert.Matches(@"^(All |\d+ game controls)", wizard.PlanHeadline);
    }

    // M9 (second review): the plain function names of the quick review in the language shown (a fixed list).
    [Fact]
    public void QuickReview_FunctionNames_AreTranslated()
    {
        Loc.SetLanguage("de");
        try
        {
            foreach (string name in new[] { "Fire gun", "Wingman order left", "Trim rudder left", "Radiator close", "High-G turn" })
                Assert.NotEqual(name, FunctionNames.Translate(name));
            Assert.Equal("Quadrat", ReadableNames.Control(string.Empty, "Square"));
            // A name the list does not know stays as the game calls it.
            Assert.Equal("AH64 CPG IAT", FunctionNames.Translate("AH64 CPG IAT"));
            // Every name of the list has a translation in every language.
            List<string> missing = [];
            foreach (string lang in LanguageCodes())
            {
                Loc.SetLanguage(lang);
                missing.AddRange(FunctionNames.All.Where(n => Loc.T(FunctionNames.KeyOf(n)) == FunctionNames.KeyOf(n)).Select(n => lang + ": " + n));
            }
            Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing.Take(40)));
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    }

    private static IEnumerable<string> LanguageCodes() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "gui", "JoyPro.Gui", "Localization", "Strings"), "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!);

    [Fact]
    public void QuickTour_PickedInSettings_RunsTheTour()
    {
        MainWindowViewModel vm = Vm(persist: false);
        vm.StartTourByIdCommand.Execute(TourCatalog.Quick);
        Assert.Equal(TourCatalog.Quick, vm.ActiveTour?.Id);
        Assert.Null(vm.SetupWizard);
        vm.EndTourCommand.Execute(null);
    }

    [Fact]
    public async Task FirstStart_QuickPath_DoesNotOpenTheWelcomeNextToTheQuickSetup()
    {
        QuickSetupTests.QuickFakeEngine engine = new();
        MainWindowViewModel vm = await QuickSetupTests.NewVmAsync(engine);
        vm.WelcomeIgnoresSandbox = true;
        vm.InstalledGamesForWelcome = () => ["IL2Game"];
        // The first start: the game setup shows, the welcome waits for it.
        vm.BuildGameSetup(firstStart: true);
        vm.ShowWelcomeAfterStartupChecks(setupShown: true);
        Assert.False(vm.IsWelcomeOpen);
        // "Done, and set everything up for me".
        vm.BeginFirstStartQuickPath();
        Assert.True(await vm.ApplyGameSetupAsync());
        Assert.False(vm.IsWelcomeOpen);
        Assert.True(vm.ToursState.WelcomeDone);
        Assert.Equal(TourCatalog.Quick, vm.ToursState.Choice);
    }

    [Fact]
    public async Task QuickSetup_AskedAgainWhileItRuns_MakesNoSecondWizard()
    {
        QuickSetupTests.QuickFakeEngine engine = new() { DevicesDelayMs = 400 };
        MainWindowViewModel vm = await QuickSetupTests.NewVmAsync(engine);
        int opened = 0;
        vm.SetupWizardRequested += () => opened++;
        Task first = vm.StartQuickSetupAsync();
        for (int i = 0; i < 100 && vm.SetupWizard == null; i++)
            await Task.Delay(10);
        SetupWizardViewModel? running = vm.SetupWizard;
        Assert.NotNull(running);
        await vm.StartQuickSetupAsync(tourAfter: true);
        Assert.Same(running, vm.SetupWizard);
        await first;
        Assert.Same(running, vm.SetupWizard);
        Assert.Equal(1, engine.Calls.Count(c => AxisTuningTests.Seq(c, "setup", "plan")));
        Assert.Equal(2, opened);
    }

    // ---- the quick review in plain words ----

    [Theory]
    [InlineData("TrimRudderLeft", "Trim rudder left")]
    [InlineData("WheelBrakeLeftAnalog", "Wheel brake left analog")]
    [InlineData("Gear TOGGLE (GearIn)", "Gear toggle")]
    [InlineData("Autopilot - Pitch ON", "Autopilot - Pitch on")]
    [InlineData("Lights - EXT - Landing-Taxi-On", "Lights - EXT - Landing-Taxi-On")]
    [InlineData("Fire gun", "Fire gun")]
    public void QuickReview_ShowsPlainFunctionNames(string name, string plain) =>
        Assert.Equal(plain, SetupReviewRow.PlainName(name));

    [Fact]
    public async Task QuickReview_KeepsTheChangeToolsBehindChangeSomething()
    {
        QuickSetupTests.QuickFakeEngine engine = new();
        MainWindowViewModel vm = await QuickSetupTests.NewVmAsync(engine);
        await vm.StartQuickSetupAsync();
        SetupWizardViewModel wizard = vm.SetupWizard!;
        Assert.True(wizard.IsReviewStep);
        Assert.False(wizard.ShowsChangeTools);
        Assert.False(wizard.ShowsSheetPanel);
        wizard.ToggleChangeSomethingCommand.Execute(null);
        Assert.True(wizard.ShowsChangeTools);
        // The full wizard always shows them.
        wizard.QuickStepByStepCommand.Execute(null);
        Assert.True(wizard.ShowsChangeTools);
        Assert.True(wizard.ShowsSheetPanel);
    }

    // ---- helpers ----

    private static List<string> Sentences(string text) =>
        Regex.Split(text ?? string.Empty, @"(?<=[.!?])\s+").Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    private static int Words(string sentence) => sentence.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "gui", "JoyPro.Gui", "Views", "VirtualJoystickView.axaml")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
