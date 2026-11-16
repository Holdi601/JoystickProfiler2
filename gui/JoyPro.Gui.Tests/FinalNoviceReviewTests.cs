using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Services.Help;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Setup;
using Xunit;

namespace JoyPro.Gui.Tests;

// The final beginner review of 2.7 (findings F01-F11) on its three personas with
// the real engine (fake devices, write sandbox, no install detection): a child with only an Xbox controller and ACE
// COMBAT 8, a DCS beginner with a WinWing URSA MINOR stick and throttle who flies the F-16, a T.Flight HOTAS 4 with IL-2,
// and nobody's stick plugged in. Opt-in renders of every screen in en / de / ja: JOYPRO_NOVICE_RENDER_OUT=<folder>.
[SupportedOSPlatform("windows")]
public sealed class FinalNoviceReviewTests
{
    private static string? Out => Environment.GetEnvironmentVariable("JOYPRO_NOVICE_RENDER_OUT") is { Length: > 0 } dir ? dir : null;

    // F04: only an Xbox controller and only pad games: the quick path says it already works and stops (no remap, no
    // driver, no grown-up); "Close and play" closes it; the pad remap stays an optional extra.
    [Fact]
    public async Task XboxOnly_PadGame_QuickPath_SaysTheControllerAlreadyWorks()
    {
        QuickSetupPersonaTests.PersonaEngine engine = QuickSetupPersonaTests.PersonaEngine.Create("xbox-one.tsv", "AceCombat8", "ACE COMBAT 8: Wings of Theve");
        Assert.False(engine.Missing, "Build the engine first (engine/bin/x64/" + TestEngine.Configuration + "/engine.exe).");
        MainWindowViewModel vm = await QuickSetupPersonaTests.FirstStartAsync(engine, "AceCombat8");
        await vm.StartQuickSetupAsync();
        SetupWizardViewModel wizard = vm.SetupWizard!;
        Assert.True(wizard.QuickPadWorks, wizard.QuickSummary);
        Assert.False(wizard.QuickStuck);
        Assert.False(wizard.IsReviewStep);
        Assert.Null(wizard.Plan);
        Assert.StartsWith("Good news: your controller already works in ACE COMBAT 8", wizard.QuickSummary);
        Assert.Equal("Your controller already works", wizard.StepHeading);
        foreach (string word in new[] { "ViGEm", "grown-up", "pretend", "driver" })
            Assert.DoesNotContain(word, wizard.QuickSummary, StringComparison.OrdinalIgnoreCase);
        Assert.False(wizard.ShowsDevicesStep);
        Assert.False(wizard.ShowsNext);
        Assert.False(wizard.ShowsQuickHeadTracking);
        Assert.DoesNotContain(engine.Calls, c => NoviceGuiTests.Seq(c, "setup", "plan"));
        Assert.Equal(["Close and play"], VisibleAccentButtons(wizard));

        // The optional extra: the pad remap as before (the pretend controller), never the default.
        await wizard.QuickPadExtrasCommand.ExecuteAsync(null);
        Assert.True(wizard.IsReviewStep, wizard.Status + " / " + wizard.QuickSummary);
        Assert.False(wizard.QuickPadWorks);
        Assert.Equal("Set up the pretend controller", wizard.ExportText);
        // F05: the controller is a controller held with both hands, by a short name; its parts in the language shown.
        Assert.Equal("Xbox controller (Controller)", wizard.SelectedReviewDevice?.Title);
        Assert.Contains("Xbox controller", wizard.QuickSummary);
        Assert.DoesNotContain("wired or wireless", wizard.QuickSummary);
        Loc.SetLanguage("de");
        try
        {
            wizard.RefreshReviewRows();
            Assert.Contains(wizard.ReviewRows, r => r.Input == "Rechter Stick links/rechts");
            Assert.DoesNotContain(wizard.ReviewRows, r => r.Input is "Right stick left/right" or "Left stick up/down" or "Triggers LT/RT (one axis)");
        }
        finally
        {
            Loc.SetLanguage("en");
        }

        // "Close and play" closes the window and leaves the pretend controller alone.
        bool closed = false;
        wizard.CloseRequested += () => closed = true;
        await vm.StartQuickSetupAsync();
        wizard = vm.SetupWizard!;
        wizard.CloseRequested += () => closed = true;
        Assert.True(wizard.QuickPadWorks);
        wizard.QuickPlayAsIsCommand.Execute(null);
        Assert.True(closed);
        Assert.NotEqual(UiStage.VirtualJoystick, vm.CurrentStage);
        Assert.DoesNotContain(engine.Calls, c => NoviceGuiTests.Seq(c, "vjoy", "install") || NoviceGuiTests.Seq(c, "components", "install"));
        vm.StopGameConfigWatcher();
    }

    // F04 / F11: the welcome follows the devices and the games' binds.
    [Fact]
    public async Task Welcome_FollowsTheDevices_AndADcsBeginnerGetsTheQuickWay()
    {
        QuickSetupPersonaTests.PersonaEngine engine = QuickSetupPersonaTests.PersonaEngine.Create("xbox-one.tsv", "AceCombat8", "ACE COMBAT 8: Wings of Theve");
        Assert.False(engine.Missing, "Build the engine first.");
        MainWindowViewModel vm = await QuickSetupPersonaTests.FirstStartAsync(engine, "AceCombat8");
        try
        {
            vm.InstalledGamesForWelcome = () => ["AceCombat8"];
            vm.GamesWithoutBindsForWelcome = () => [];
            // Only an Xbox controller: no flight-stick way, no tour suggested, "it already works".
            vm.DevicesForWelcome = () => (false, true);
            vm.WelcomeOptions.Clear();
            vm.OpenWelcome();
            Assert.DoesNotContain(vm.WelcomeOptions, o => o.Id == TourCatalog.Pad);
            Assert.Equal(TourCatalog.NoTour, vm.WelcomeOptions.Single(o => o.IsSelected).Id);
            Assert.Contains("already works in ACE COMBAT 8", vm.WelcomeSuggestion);
            Assert.Equal("Close", vm.WelcomeStartLabel);
            // Nothing connected (yet): never the flight-stick way.
            vm.DevicesForWelcome = () => (false, false);
            vm.WelcomeOptions.Clear();
            vm.OpenWelcome();
            Assert.DoesNotContain(vm.WelcomeOptions, o => o.Id == TourCatalog.Pad);
            // A flight stick: the pad way as before.
            vm.DevicesForWelcome = () => (true, false);
            vm.WelcomeOptions.Clear();
            vm.OpenWelcome();
            Assert.Equal(TourCatalog.Pad, vm.WelcomeOptions.Single(o => o.IsSelected).Id);
            // F11: DCS without binds -> the quick way; with binds -> the DCS tour.
            vm.InstalledGamesForWelcome = () => ["DCS"];
            vm.GamesWithoutBindsForWelcome = () => ["DCS"];
            vm.WelcomeOptions.Clear();
            vm.OpenWelcome();
            Assert.Equal(TourCatalog.Quick, vm.WelcomeOptions.Single(o => o.IsSelected).Id);
            vm.GamesWithoutBindsForWelcome = () => [];
            vm.WelcomeOptions.Clear();
            vm.OpenWelcome();
            Assert.Equal(TourCatalog.Dcs, vm.WelcomeOptions.Single(o => o.IsSelected).Id);
            vm.IsWelcomeOpen = false;
        }
        finally
        {
            vm.StopGameConfigWatcher();
        }
    }

    // F01 / F02 / F03: DCS asks which aircraft (display names, the most popular ticked when none is on this PC), shows
    // only those with plain translated names, and the head-tracking answer keeps one count and says what changed.
    [Fact]
    public async Task DcsF16_QuickPath_AsksTheAircraft_AndShowsPlainNames()
    {
        QuickSetupPersonaTests.PersonaEngine engine = QuickSetupPersonaTests.PersonaEngine.Create("ursa-minor.tsv", "DCS", "DCS World");
        Assert.False(engine.Missing, "Build the engine first.");
        MainWindowViewModel vm = await QuickSetupPersonaTests.FirstStartAsync(engine, "DCS");
        try
        {
            await vm.StartQuickSetupAsync();
            SetupWizardViewModel wizard = vm.SetupWizard!;
            Assert.True(wizard.QuickAsksAircraft, wizard.Status + " / " + wizard.QuickSummary);
            Assert.Equal("Which aircraft do you fly?", wizard.StepHeading);
            Assert.DoesNotContain(engine.Calls, c => NoviceGuiTests.Seq(c, "setup", "plan"));
            // Names a person reads; no AI menu, gunner, RIO or UI seats; the most popular first and ticked.
            Assert.Equal("F-16C Block 50/52", wizard.QuickAircraft[0].Name);
            Assert.Equal(["F-16C Block 50/52"], wizard.QuickAircraft.Where(a => a.IsChosen).Select(a => a.Name));
            Assert.DoesNotContain(wizard.QuickAircraft, a => Regex.IsMatch(a.Name, @"_|AI Menu|\bUI\b|Gunner|RIO\)|WSO\)"));
            Assert.Contains(wizard.QuickAircraft, a => a.Name == "F/A-18C Hornet Lot 20");
            Assert.Contains(wizard.QuickAircraft.Single(a => a.Name == "F-14B Tomcat").Keys, k => k == "F-14_RIO");
            Assert.Equal(["Next"], VisibleAccentButtons(wizard));

            await wizard.QuickAircraftNextCommand.ExecuteAsync(null);
            Assert.True(wizard.IsReviewStep, wizard.Status + " / " + wizard.QuickSummary);
            Assert.Contains(engine.Calls, c => NoviceGuiTests.Seq(c, "setup", "plan") && c.Contains("--only-aircraft") && c[c.ToList().IndexOf("--only-aircraft") + 1] == "F-16C_50");
            // Only the chosen aircraft, by its name.
            Assert.Equal(["F-16C Block 50/52"], wizard.ReviewAircraft.Select(a => a.Title));
            List<string> names = AllRows(wizard).Select(r => r.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains('[') || n.Contains('_') || Regex.IsMatch(n, @"^[A-Za-z]+[a-z][A-Z]\w*$"));
            foreach (string name in new[] { "Landing gear up", "Landing gear down", "Trim nose down", "Fire gun", "Launch weapon", "Pitch", "Throttle" })
                Assert.Contains(name, names);
            Assert.DoesNotContain(names, n => n is "GearIn" or "Trim_NoseDown" or "LaunchWeapon" or "Trigger1stStage" or "Throttle right");
            // The flight controls first.
            Assert.Equal(SetupImportance.Flight, wizard.ReviewRows[0].Relation.Category);
            Loc.SetLanguage("de");
            try
            {
                List<string> german = AllRows(wizard).Select(r => r.Name).ToList();
                Assert.Contains("Fahrwerk einfahren", german);
                Assert.DoesNotContain("Landing gear up", german);
            }
            finally
            {
                Loc.SetLanguage("en");
            }

            // F03: head tracking yes: one count everywhere, one line on what changed and where the recenter went.
            int before = wizard.Plan!.Placed.Count(r => !r.Kept);
            wizard.HeadTrackingYes = true;
            for (int i = 0; i < 400 && (wizard.IsWorking || wizard.QuickLines.Count == 0); i++)
                await Task.Delay(25);
            int after = wizard.Plan!.Placed.Count(r => !r.Kept);
            Assert.True(after < before, before + " -> " + after);
            Assert.Contains(after + " game controls", wizard.QuickSummary);
            Assert.Contains(wizard.QuickLines, l => l.StartsWith("Head tracking: the look-around buttons are gone. Re-centre the view with ", StringComparison.Ordinal));
            Assert.DoesNotContain(wizard.QuickLines, l => l.Contains("JoyPro Shift 1 ", StringComparison.Ordinal) && !l.StartsWith("Shift button", StringComparison.Ordinal));
            // Back goes to the aircraft question (a new answer makes the setup again).
            Assert.True(wizard.ShowsBack);
            wizard.BackCommand.Execute(null);
            Assert.True(wizard.QuickAsksAircraft);
            Assert.Empty(wizard.QuickLines);
        }
        finally
        {
            vm.StopGameConfigWatcher();
        }
    }

    // F06: no stick plugged in: one plain sentence, one blue button (Try again), nothing else of the wizard.
    [Fact]
    public async Task NoDevices_QuickPath_ShowsOnePlainScreen()
    {
        QuickSetupPersonaTests.PersonaEngine engine = QuickSetupPersonaTests.PersonaEngine.Create("no-devices.tsv");
        Assert.False(engine.Missing, "Build the engine first.");
        MainWindowViewModel vm = await QuickSetupPersonaTests.FirstStartAsync(engine);
        try
        {
            await vm.StartQuickSetupAsync();
            SetupWizardViewModel wizard = vm.SetupWizard!;
            Assert.True(wizard.QuickStuck);
            Assert.Equal("JoyPro can't see a stick yet. Plug it in (USB), wait 5 seconds, then click Try again.", wizard.QuickSummary);
            Assert.DoesNotMatch(@"\d", wizard.StepHeading);
            Assert.False(wizard.ShowsDevicesStep);
            Assert.False(wizard.ShowsNext);
            Assert.False(wizard.ShowsBack);
            Assert.False(wizard.ShowsQuickHeadTracking);
            Assert.Equal(string.Empty, wizard.Status);
            Assert.Equal(["Try again"], VisibleAccentButtons(wizard));
            Assert.Contains("Set it up step by step", VisibleButtons(wizard));
            Assert.DoesNotContain("Detect again", VisibleButtons(wizard));
            // The hand rules of the full wizard are in the language shown.
            Loc.SetLanguage("de");
            try
            {
                await wizard.DetectDevicesCommand.ExecuteAsync(null);
                Assert.DoesNotContain(wizard.Rules, r => r.StartsWith("Right hand", StringComparison.Ordinal) || r.StartsWith("Left hand", StringComparison.Ordinal));
            }
            finally
            {
                Loc.SetLanguage("en");
            }
        }
        finally
        {
            vm.StopGameConfigWatcher();
        }
    }

    // F07: a T.Flight in IL-2: the wheel brakes are on a button and the stick has no spare lever for propeller and
    // mixture: that is no "important, no room".
    [Fact]
    public async Task TFlightIl2_Headline_DoesNotCountWhatTheStickCannotHave()
    {
        QuickSetupPersonaTests.PersonaEngine engine = QuickSetupPersonaTests.PersonaEngine.Create("tflight-hotas4.tsv");
        Assert.False(engine.Missing, "Build the engine first.");
        MainWindowViewModel vm = await QuickSetupPersonaTests.FirstStartAsync(engine);
        try
        {
            await vm.StartQuickSetupAsync();
            SetupWizardViewModel wizard = vm.SetupWizard!;
            Assert.True(wizard.IsReviewStep, wizard.Status);
            Assert.StartsWith("All important buttons are set.", wizard.PlanHeadline);
            Assert.DoesNotContain(wizard.NotPlaced, r => r.Item.Function.StartsWith("wheelbrake", StringComparison.Ordinal));
            Assert.False(wizard.QuickAsksAircraft);
        }
        finally
        {
            vm.StopGameConfigWatcher();
        }
    }

    // Independent review 7 B11: the quick review shows the plan's notes a beginner needs, in plain words; the flares behind
    // a shift and no button for the flares cannot be missed.
    [Fact]
    public void QuickReview_ShowsThePlanNotes_AndTheSurvivalWarnings()
    {
        SetupWizardViewModel wizard = new((_, _, _, _, _) => Task.FromResult<EngineCommandResult?>(null), [new SetupGameChoice("DCS", "DCS World", true)]) { QuickMode = true };
        SetupDevice stick = new() { Device = "Stick {1}", Model = "Test stick", Kind = "stick", Role = SetupRoles.MainStick };
        SetupPlan plan = new() { Games = ["DCS"], Devices = [stick] };
        plan.Inputs.Add(new SetupInput { Device = stick.Device, Input = "JOY_BTN21", Control = "stick.button.pinky" });
        plan.Modifiers.Add(new SetupPlanModifier { Name = "JoyPro Shift 1", Device = stick.Device, Input = "JOY_BTN21", Control = "stick.button.pinky" });
        plan.Relations.Add(new SetupPlanRelation
        {
            Name = "CMS FWD (Start Program or just Flare)", Function = "cms_fwd_start_program_or_just_flare", FunctionName = "CMS FWD (Start Program or just Flare)", Category = SetupImportance.Weapons,
            Device = stick.Device, Input = "JOY_BTN5", Modifier = "JoyPro Shift 1", Aircraft = ["F-16C_50"], Reason = "survival-critical: behind JoyPro Shift 1 (no grip input left without a modifier)"
        });
        plan.Notes.Add("Head tracking: no input is left for the view recenter (F-16C_50): use the recenter key of your tracking software (TrackIR, OpenTrack, Tobii, the VR headset).");
        plan.Notes.Add("Countermeasures: no grip button is left for the flares, not even behind a shift (A-10C): put them on a button yourself in the review - they keep you alive.");
        wizard.LoadPlan(plan);
        wizard.UpdateQuickSummary();
        Assert.Contains(wizard.QuickWarnings, w => w.StartsWith("Important: no button was left for the flares.", StringComparison.Ordinal));
        Assert.Contains(wizard.QuickWarnings, w => w.StartsWith("Important: Flares and chaff program (CMS forward) is behind the shift button. Press Button 5", StringComparison.Ordinal) && w.Contains("while you hold Button 21"));
        Assert.Contains("Head tracking: no button was left to re-centre the view. Use the re-centre key of your head tracker.", wizard.QuickLines);
        Assert.True(wizard.HasQuickWarnings);
    }

    // ---- the renders (opt-in) ----

    [Fact]
    public async Task PersonaRenders()
    {
        if (Out is not { } outDir)
            return;
        Directory.CreateDirectory(outDir);
        StringBuilder log = new();
        string[] langs = (Environment.GetEnvironmentVariable("JOYPRO_NOVICE_RENDER_LANGS") is { Length: > 0 } l ? l : "en,de,ja").Split(',');
        try
        {
            foreach (string lang in langs)
            {
                foreach (bool easy in new[] { false, true })
                {
                    string mode = (easy ? "easy-" : "normal-") + lang;
                    // P2: only an Xbox controller, ACE COMBAT 8.
                    {
                        QuickSetupPersonaTests.PersonaEngine engine = QuickSetupPersonaTests.PersonaEngine.Create("xbox-one.tsv", "AceCombat8", "ACE COMBAT 8: Wings of Theve");
                        MainWindowViewModel vm = await QuickSetupPersonaTests.FirstStartAsync(engine, "AceCombat8");
                        vm.ApplyLanguageChoice(lang);
                        vm.EasyWords = easy;
                        vm.InstalledGamesForWelcome = () => ["AceCombat8"];
                        vm.DevicesForWelcome = () => (false, true);
                        vm.WelcomeOptions.Clear();
                        AvaloniaRenderThread.Run(() =>
                        {
                            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                            vm.OpenWelcome();
                            NoviceRenderTests.Shoot(vm, 1280, 800, Path.Combine(outDir, $"p2-welcome-{mode}.png"), _ => { });
                            vm.IsWelcomeOpen = false;
                        });
                        log.AppendLine($"[{mode}] P2 WELCOME {vm.WelcomeSuggestion} | {string.Join(" / ", vm.WelcomeOptions.Select(o => o.Id + (o.IsSelected ? "*" : "")))}");
                        await vm.StartQuickSetupAsync();
                        SetupWizardViewModel wizard = vm.SetupWizard!;
                        Shot(wizard, outDir, $"p2-1-pad-works-{mode}", log);
                        await wizard.QuickPadExtrasCommand.ExecuteAsync(null);
                        Shot(wizard, outDir, $"p2-2-extras-review-{mode}", log);
                        vm.ApplyLanguageChoice("en");
                        vm.StopGameConfigWatcher();
                    }
                    // P3: URSA MINOR stick and throttle, DCS, the F-16, head tracking.
                    {
                        QuickSetupPersonaTests.PersonaEngine engine = QuickSetupPersonaTests.PersonaEngine.Create("ursa-minor.tsv", "DCS", "DCS World");
                        MainWindowViewModel vm = await QuickSetupPersonaTests.FirstStartAsync(engine, "DCS");
                        vm.ApplyLanguageChoice(lang);
                        vm.EasyWords = easy;
                        vm.InstalledGamesForWelcome = () => ["DCS"];
                        vm.GamesWithoutBindsForWelcome = () => ["DCS"];
                        vm.DevicesForWelcome = () => (true, false);
                        vm.WelcomeOptions.Clear();
                        AvaloniaRenderThread.Run(() =>
                        {
                            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                            vm.OpenWelcome();
                            NoviceRenderTests.Shoot(vm, 1280, 800, Path.Combine(outDir, $"p3-welcome-{mode}.png"), _ => { });
                            vm.IsWelcomeOpen = false;
                        });
                        log.AppendLine($"[{mode}] P3 WELCOME {vm.WelcomeSuggestion} | {string.Join(" / ", vm.WelcomeOptions.Select(o => o.Id + (o.IsSelected ? "*" : "")))}");
                        await vm.StartQuickSetupAsync();
                        SetupWizardViewModel wizard = vm.SetupWizard!;
                        Shot(wizard, outDir, $"p3-1-aircraft-{mode}", log);
                        await wizard.QuickAircraftNextCommand.ExecuteAsync(null);
                        Shot(wizard, outDir, $"p3-2-review-{mode}", log);
                        wizard.HeadTrackingYes = true;
                        for (int i = 0; i < 400 && (wizard.IsWorking || wizard.QuickLines.Count == 0); i++)
                            await Task.Delay(25);
                        Shot(wizard, outDir, $"p3-3-headtracking-yes-{mode}", log);
                        if (wizard.ReviewDevices.Count > 1)
                        {
                            wizard.SelectedReviewDevice = wizard.ReviewDevices[1];
                            Shot(wizard, outDir, $"p3-4-throttle-{mode}", log);
                        }
                        // The full wizard's priorities (F08: the head-tracking answers wrap).
                        wizard.QuickStepByStepCommand.Execute(null);
                        wizard.CurrentStep = 2;
                        wizard.SetAircraftForTests([new SetupAircraftItem("F-16C_50", "DCS", "Fixed wing", "F-16C Block 50/52")], "DCS");
                        Shot(wizard, outDir, $"p3-5-wizard-priorities-{mode}", log);
                        vm.ApplyLanguageChoice("en");
                        vm.StopGameConfigWatcher();
                    }
                    // P1: T.Flight HOTAS 4, IL-2.
                    {
                        QuickSetupPersonaTests.PersonaEngine engine = QuickSetupPersonaTests.PersonaEngine.Create("tflight-hotas4.tsv");
                        MainWindowViewModel vm = await QuickSetupPersonaTests.FirstStartAsync(engine);
                        vm.ApplyLanguageChoice(lang);
                        vm.EasyWords = easy;
                        await vm.StartQuickSetupAsync();
                        SetupWizardViewModel wizard = vm.SetupWizard!;
                        Shot(wizard, outDir, $"p1-1-review-{mode}", log);
                        wizard.ChangeSomething = true;
                        Shot(wizard, outDir, $"p1-2-change-{mode}", log);
                        vm.ApplyLanguageChoice("en");
                        vm.StopGameConfigWatcher();
                    }
                    // Nothing plugged in.
                    {
                        QuickSetupPersonaTests.PersonaEngine engine = QuickSetupPersonaTests.PersonaEngine.Create("no-devices.tsv");
                        MainWindowViewModel vm = await QuickSetupPersonaTests.FirstStartAsync(engine);
                        vm.ApplyLanguageChoice(lang);
                        vm.EasyWords = easy;
                        await vm.StartQuickSetupAsync();
                        Shot(vm.SetupWizard!, outDir, $"stuck-no-devices-{mode}", log);
                        vm.ApplyLanguageChoice("en");
                        vm.StopGameConfigWatcher();
                    }
                }
            }
        }
        finally
        {
            Loc.SetLanguage("en");
            AvaloniaRenderThread.Run(() => Application.Current!.RequestedThemeVariant = ThemeVariant.Default);
            File.WriteAllText(Path.Combine(outDir, "final-fixes-log.txt"), log.ToString(), new UTF8Encoding(false));
        }
    }

    private static void Shot(SetupWizardViewModel wizard, string outDir, string name, StringBuilder log)
    {
        AvaloniaRenderThread.Run(() =>
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            NoviceRenderTests.Render(wizard, Path.Combine(outDir, name + ".png"));
        });
        log.AppendLine($"[{name}] HEADING {wizard.StepHeading} | SUMMARY {wizard.QuickSummary} | STATUS {wizard.Status}");
        foreach (string line in wizard.QuickNotes.Concat(wizard.QuickLines))
            log.AppendLine("    LINE " + line);
        log.AppendLine("    BUTTONS " + string.Join(" | ", VisibleButtons(wizard)) + " | BLUE " + string.Join(" | ", VisibleAccentButtons(wizard)));
        if (wizard.QuickAsksAircraft)
            log.AppendLine("    AIRCRAFT " + string.Join(", ", wizard.QuickAircraft.Take(25).Select(a => (a.IsChosen ? "[x] " : "[ ] ") + a.Name)));
        if (wizard.IsReviewStep)
        {
            log.AppendLine("    DEVICE " + wizard.SelectedReviewDevice?.Title + " | AIRCRAFT " + string.Join(", ", wizard.ReviewAircraft.Select(a => a.Title)) + " | HEADLINE " + wizard.PlanHeadline);
            foreach (SetupReviewRow row in wizard.ReviewRows.Take(40))
                log.AppendLine($"    ROW {row.Input} -> {row.Name}");
        }
    }

    // Every review row of every device.
    private static List<SetupReviewRow> AllRows(SetupWizardViewModel wizard)
    {
        List<SetupReviewRow> rows = [];
        SetupRoleOption? shown = wizard.SelectedReviewDevice;
        foreach (SetupRoleOption device in wizard.ReviewDevices.ToList())
        {
            wizard.SelectedReviewDevice = device;
            rows.AddRange(wizard.ReviewRows);
        }
        wizard.SelectedReviewDevice = shown;
        return rows;
    }

    private static List<string> VisibleButtons(SetupWizardViewModel wizard, bool accentOnly = false)
    {
        List<string> texts = [];
        AvaloniaRenderThread.Run(() =>
        {
            SetupWizardWindow window = new() { DataContext = wizard, Width = 1240, Height = 860 };
            Size size = new(1240, 860);
            Control content = (Control)window.Content!;
            for (int pass = 0; pass < 4; pass++)
            {
                window.Measure(size);
                window.Arrange(new Rect(size));
                content.Measure(size);
                content.Arrange(new Rect(size));
                AvaloniaRenderThread.Pump();
            }
            foreach (Button button in content.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && (!accentOnly || b.Classes.Contains("accent"))))
            {
                if (button.Content is string text)
                    texts.Add(text);
            }
            window.DataContext = null;
        });
        return texts;
    }

    private static List<string> VisibleAccentButtons(SetupWizardViewModel wizard) => VisibleButtons(wizard, accentOnly: true);
}
