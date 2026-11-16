using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Services.Components;
using JoyPro.Gui.Services.Help;
using JoyPro.Gui.ViewModels;
using JoyPro.Setup;
using Xunit;

namespace JoyPro.Gui.Tests;

// The third novice review's majors and minors (docs/ui/help-and-tours.md): IL-2's function names in German, the
// ViGEmBus card in the language shown, the skip warning that goes away once the box is ticked, no Skip while the card
// waits for the setup window, the "why" card where the first start joins the pad steps, one name for the pretend
// controller's page, and the quick review's plain inputs. (B1, the quick write: QuickWriteQuestionTests.)
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class ThirdNoviceReviewTests : IDisposable
{
    public void Dispose() => Loc.SetLanguage("en");

    private static MainWindowViewModel Vm() =>
        new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location,
            WelcomeIgnoresSandbox = true
        };

    // M2: the IL-2 rows of the quick review and the Binds list in German.
    [Theory]
    [InlineData("Landing gear up", "Fahrwerk einfahren")]
    [InlineData("Bomb bay doors open/close", "Bombenschacht auf/zu")]
    [InlineData("AI autopilot on/off", "KI-Autopilot an/aus")]
    [InlineData("Water radiator: close", "Wasserkühler: zu")]
    [InlineData("Snap view: centre", "Blick: Mitte")]
    [InlineData("Fire gun group 2", "Waffengruppe 2 feuern")]
    [InlineData("Elevator trim: nose up", "Höhentrimmung: Nase hoch")]
    public void Il2FunctionNames_AreGerman(string english, string german)
    {
        Loc.SetLanguage("de");
        Assert.Equal(german, FunctionNames.Translate(english));
        Assert.Equal(german, new BindListItem { RelationName = english }.DisplayName);
    }

    [Fact]
    public void Il2FunctionNames_EveryNameOfTheGamesListIsKnown_InEveryLanguage()
    {
        // Every name IL-2's setup can give a relation (the catalog's names of the slots the setup uses) is in the list,
        // and every language has a translation of it.
        string[] names = ["AI autopilot on/off", "Aileron trim: left", "Landing gear up/down", "Wheel brake: left", "Reload guns", "Command menu",
            "Padlock: enemy aircraft", "Trim: reset all", "Navigation lights on/off", "Engine 1 start/stop", "Bail out", "Fire"];
        foreach (string name in names)
            Assert.Contains(name, FunctionNames.All);
        foreach (LanguageInfo language in Languages.All.Where(l => !ReferenceEquals(l, Languages.English)))
        {
            Loc.SetLanguage(language.Code);
            foreach (string name in names)
                Assert.NotEqual(name, FunctionNames.Translate(name));
        }
    }

    [Fact]
    public void RelationNames_InTheBindsList_TranslateOnlyKnownNames()
    {
        Loc.SetLanguage("de");
        // A slot id as the setup wrote it, IL-2's one throttle lever, and a name the user gave.
        Assert.Equal(Loc.T("Function name: Launch weapon"), new BindListItem { RelationName = "LaunchWeapon" }.DisplayName);
        Assert.Equal("Schub", new BindListItem { RelationName = "ThrottleLeftNonAB" }.DisplayName);
        Assert.Equal("My gear lever", new BindListItem { RelationName = "My gear lever" }.DisplayName);
        Loc.SetLanguage("en");
        Assert.Equal("LaunchWeapon", new BindListItem { RelationName = "LaunchWeapon" }.DisplayName);
    }

    // M3: the ViGEmBus card in German, with the one term.
    [Fact]
    public void ViGEmBusCard_IsGerman_AndSaysPretendController()
    {
        Loc.SetLanguage("de");
        ComponentItem card = new(ComponentCatalog.ViGEmBus, new ComponentInstallGate());
        Assert.Equal("Ein kleines, kostenloses Hilfsprogramm von Nefarius. Damit baut JoyPro den Ersatz-Controller, den Ihr Spiel sieht.", card.WhatItIs);
        Assert.Equal("Verwendet von: dem Ersatz-Controller", card.UsedBy);
        Assert.DoesNotContain("Xbox pad", card.WhyJoyProNeedsIt);
        Assert.DoesNotContain("Hell Let Loose", card.WhyJoyProNeedsIt);
        Assert.Contains("Ersatz-Controller", card.WhyJoyProNeedsIt);
        Assert.DoesNotContain("retired", card.SourceText);
        Loc.SetLanguage("en");
        Assert.Equal("Used by: the pretend controller", card.UsedBy);
        Assert.DoesNotContain("Xbox pad", card.WhyJoyProNeedsIt);
    }

    // Minor 1: Next first, then the box ticked: the card says "Now click Install", not the skip warning.
    [Fact]
    public void PadTour_SkipWarning_GoesAway_WhenTheBoxIsTicked()
    {
        MainWindowViewModel vm = Vm();
        vm.InstalledGamesForWelcome = () => ["AceCombat8"];
        List<TourStep> steps = TourCatalog.Find(TourCatalog.Pad)!.StepsFor(false).ToList();
        int install = steps.FindIndex(s => s.SkipIf == TourConditions.ViGEmInstalled);
        ComponentItem vigem = vm.VirtualJoystickViGEmComponent!;
        vigem.Consent = false;
        vm.StartTour(TourCatalog.Pad, everyFeature: false, step: install);
        vm.TourNextCommand.Execute(null);
        Assert.Contains("Next again", vm.TourText);
        vigem.Consent = true;
        Assert.StartsWith("Good. Now click Install ViGEmBus.", vm.TourText);
        // The next Next warns again before it goes on without the helper.
        vm.TourNextCommand.Execute(null);
        Assert.Equal(install, vm.TourStepIndex);
        Assert.Contains("Next again", vm.TourText);
        vigem.Consent = false;
        vm.EndTourCommand.Execute(null);
    }

    // Minor 2: while the card waits for the setup window, there is no Skip, and Skip (a key) does what Next does.
    [Fact]
    public async Task PadTour_WaitingForTheSetupWindow_HasNoSkip()
    {
        QuickSetupTests.QuickFakeEngine engine = new();
        MainWindowViewModel vm = await QuickSetupTests.NewVmAsync(engine);
        int fronted = 0;
        vm.SetupWizardRequested += () => fronted++;
        vm.StartTour(TourCatalog.Pad, everyFeature: false, step: 1);
        Assert.True(vm.TourShowsSkip);
        vm.TourNextCommand.Execute(null);
        for (int i = 0; i < 100 && (vm.SetupWizard == null || vm.QuickSetupRunning); i++)
            await Task.Delay(30);
        Assert.True(vm.TourWaitsForSetup);
        Assert.False(vm.TourShowsSkip);
        int before = fronted;
        vm.TourSkipCommand.Execute(null);
        Assert.Equal(1, vm.TourStepIndex);
        Assert.True(vm.TourWaitsForSetup);
        Assert.Equal(before + 1, fronted);
        vm.EndTourCommand.Execute(null);
    }

    // Minor 7: the first start's quick path joins the pad steps with a card that says why; the tour from its start
    // (whose first card says it) leaves that card out.
    [Fact]
    public void PadSteps_FromTheFirstStart_BeginWithWhy()
    {
        MainWindowViewModel vm = Vm();
        vm.InstalledGamesForWelcome = () => ["AceCombat8"];
        vm.ContinueWithPadSteps(["AceCombat8"]);
        Assert.Equal("Why this page", vm.TourTitle);
        Assert.StartsWith("Your game only knows an Xbox controller.", vm.TourText);
        Assert.StartsWith("Step 1 of ", vm.TourCounter);
        Assert.False(vm.TourCanGoBack);
        vm.TourNextCommand.Execute(null);
        Assert.Equal("Install the helper", vm.TourTitle);
        vm.EndTourCommand.Execute(null);

        // From the start: the intro card, the setup, the page; then straight to the helper.
        vm.StartTour(TourCatalog.Pad, everyFeature: false);
        List<TourStep> steps = TourCatalog.Find(TourCatalog.Pad)!.StepsFor(false).ToList();
        int open = steps.FindIndex(s => s.WaitStage == UiStage.VirtualJoystick);
        vm.StartTour(TourCatalog.Pad, everyFeature: false, step: open);
        vm.TourNextCommand.Execute(null);
        Assert.Equal("Install the helper", vm.TourTitle);
        vm.EndTourCommand.Execute(null);
    }

    // Minor 3: one name for the page: the bar button says Pretend controller during the pad tour (as the page title).
    [Fact]
    public void BarButton_IsThePretendController_ForThePadPlayer()
    {
        MainWindowViewModel vm = Vm();
        vm.InstalledGamesForWelcome = () => ["IL2Game"];
        Assert.Equal("Virtual Joystick", vm.VirtualJoystickNavTitle);
        vm.StartTour(TourCatalog.Pad, everyFeature: false);
        Assert.Equal("Pretend controller", vm.VirtualJoystickNavTitle);
        Loc.SetLanguage("de");
        Assert.Equal("Ersatz-Controller", vm.VirtualJoystickNavTitle);
        vm.EndTourCommand.Execute(null);
        vm.InstalledGamesForWelcome = () => ["AceCombat8"];
        Loc.SetLanguage("en");
        Assert.Equal("Pretend controller", vm.VirtualJoystickNavTitle);
    }

    // Minor 6: the quick review shows what the user holds: an axis by its part ("Twist"), a hat by its number, never
    // the catalog's "Trim hat left" for a hat that does no trim; the full wizard keeps the expert words.
    [Fact]
    public void QuickReview_ShowsPlainInputs()
    {
        SetupPlanRelation twist = new() { Name = "Yaw", Input = "JOY_RZ", Control = "stick.axis.twist", IsAxis = true };
        SetupPlanRelation hat = new() { Name = "Change weapon", Input = "JOY_BTN_POV1_L", Control = "stick.hat.trim.left" };
        SetupPlanRelation l1 = new() { Name = "Fire missile", Input = "JOY_BTN2", Control = "stick.button.l1", Label = "L1" };
        Assert.Equal("Twist", new SetupReviewRow(twist, plain: true).Input);
        Assert.Equal(string.Empty, new SetupReviewRow(twist, plain: true).Control);
        Assert.Equal("Hat 1 left", new SetupReviewRow(hat, plain: true).Input);
        Assert.Equal(string.Empty, new SetupReviewRow(hat, plain: true).Control);
        Assert.Equal("L1", new SetupReviewRow(l1, plain: true).Input);
        Assert.Equal("Z rotation", new SetupReviewRow(twist).Input);
    }
}
