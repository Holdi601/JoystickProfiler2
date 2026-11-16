using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Setup;
using Xunit;

namespace JoyPro.Gui.Tests;

// The novice case of docs/auto-setup-eval.md: someone new to sims, IL-2 Great Battles is the only game, a WinWing URSA
// MINOR stick and throttle. First start (the game setup) -> "Done, and give me binds quickly" -> the setup runs by itself
// up to its review -> "Use these buttons" -> "Write to my game". The setup commands run in the REAL engine (built first)
// with the fixture's fake devices (engine\tests\fixtures\setupwizard\ursa-*.tsv), a temp profile and data folder, in the
// write sandbox; the game list and the export are a fake: nothing reaches a game folder.
[SupportedOSPlatform("windows")]
public sealed class QuickSetupPersonaTests
{
    [Fact]
    public async Task NoviceIl2UrsaMinor_FirstStartQuickPath_SetsUpAndExports()
    {
        PersonaEngine engine = PersonaEngine.Create("ursa-minor.tsv");
        Assert.False(engine.Missing, "Build the engine first (engine/bin/x64/" + TestEngine.Configuration + "/engine.exe).");
        MainWindowViewModel vm = await FirstStartAsync(engine);

        // The quick path from the first start's game setup.
        await vm.StartQuickSetupAsync();
        SetupWizardViewModel wizard = vm.SetupWizard!;
        Assert.True(wizard.QuickMode);
        Assert.Equal(["IL2Game"], wizard.ChosenGames);
        Assert.True(wizard.IsReviewStep, wizard.Status + " / " + wizard.QuickSummary);
        Assert.NotNull(wizard.Plan);
        // Both devices recognised: nothing to warn about, the words are plain (no JoyPro terms).
        Assert.Empty(wizard.QuickNotes);
        Assert.Contains("IL-2", wizard.QuickSummary);
        Assert.DoesNotContain("relation", wizard.QuickSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Use these buttons", wizard.ApplyText);
        Assert.Equal("Write to my game", wizard.ExportText);

        // What a novice needs in IL-2: flight axes, engine, weapons, gear, flaps, trim, looking around, comms.
        HashSet<string> placed = new(wizard.Plan!.Placed.Select(r => r.Function), StringComparer.OrdinalIgnoreCase);
        // Each line: one of these is enough (IL-2 has gear up / down and a toggle, a throttle per engine ...).
        foreach (string[] any in new string[][]
                 {
                     ["pitch"], ["roll"], ["rudder"], ["throttleleft", "throttleleftnonab", "throttleright", "throttlerightnonab"], ["rpm_control"], ["nozzle_angle_mixture_control"],
                     ["trigger1ststage", "trigger2ndstage"], ["launchweapon"], ["gear_toggle", "gearin"], ["flapsdown"], ["flapsup"], ["trimnoseup"], ["trim_nosedown"], ["easycomms"],
                     ["view_snap_forward", "view_look_horizontal", "zoom"]
                 })
            Assert.True(any.Any(placed.Contains), string.Join(" / ", any) + " is not placed: " + string.Join(", ", placed.OrderBy(f => f)));
        // On the stick: pitch, roll and the guns; the throttle lever on the throttle.
        SetupPlanRelation pitch = wizard.Plan.Placed.First(r => r.Function == "pitch");
        Assert.StartsWith("URSA MINOR FIGHTER R", pitch.Device);
        Assert.Equal("stick.axis.pitch", pitch.Control);
        Assert.StartsWith("URSA MINOR FIGHTER R", wizard.Plan.Placed.First(r => r.Function == "trigger2ndstage").Device);
        Assert.StartsWith("WINCTRL URSA MINOR Combat Throttle", wizard.Plan.Placed.First(r => r.Function.StartsWith("throttle", StringComparison.Ordinal)).Device);

        // "Use these buttons": the real engine writes them into the temp profile.
        await wizard.ApplyCommand.ExecuteAsync(null);
        Assert.True(wizard.Applied, wizard.Status);
        Assert.Contains("Write to my game", wizard.Status);
        Assert.DoesNotContain("relation", wizard.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(engine.RealCalls, c => c.Contains("apply"));

        // Every important button has a place (gear up/down as two buttons, the trim axes as trim buttons, the wheel
        // brakes as one button count): the calm line, not "A few important ones had no room" (third novice review M1).
        Assert.StartsWith("All important buttons are set.", wizard.PlanHeadline);

        // "Write to my game": IL-2's export (the fake: no game folder is touched). The game has nothing on its sticks
        // yet: no question at all (third novice review B1).
        wizard.ExportCommand.Execute(null);
        for (int i = 0; i < 200 && !engine.Calls.Any(c => NoviceGuiTests.Seq(c, "il2", "export")); i++)
            await Task.Delay(25);
        Assert.Contains(engine.Calls, c => NoviceGuiTests.Seq(c, "il2", "export"));
        // Written: the quick tour starts with "It worked!" on the Start page (not the Export page and its warnings).
        for (int i = 0; i < 200 && vm.ActiveTour == null; i++)
            await Task.Delay(25);
        Assert.Equal(JoyPro.Gui.Services.Help.TourCatalog.Quick, vm.ActiveTour?.Id);
        Assert.Equal("It worked!", vm.CurrentTourStep!.Title);
        Assert.Equal(UiStage.Start, vm.CurrentStage);
        Assert.Empty(engine.Asked);
        Assert.Empty(vm.AskedConfirmations);
        Assert.Contains(engine.Calls, c => NoviceGuiTests.Seq(c, "il2", "export") && c.Contains("clean"));
        vm.EndTourCommand.Execute(null);
    }

    [Fact]
    public async Task UnknownDevices_AreSetUpGenerically_AndSaidSoPlainly()
    {
        PersonaEngine engine = PersonaEngine.Create("ursa-unknown.tsv");
        Assert.False(engine.Missing, "Build the engine first (engine/bin/x64/" + TestEngine.Configuration + "/engine.exe).");
        MainWindowViewModel vm = await FirstStartAsync(engine);
        await vm.StartQuickSetupAsync();
        SetupWizardViewModel wizard = vm.SetupWizard!;
        Assert.True(wizard.IsReviewStep, wizard.Status + " / " + wizard.QuickSummary);

        // Said plainly, once per device, and still set up like a usual stick and throttle.
        Assert.Equal(2, wizard.QuickNotes.Count);
        Assert.Contains(wizard.QuickNotes, n => n.StartsWith("JoyPro does not know the exact model of URSA MINOR NEXTGEN FLIGHT CONTROLLER R", StringComparison.Ordinal) &&
                                                n.Contains("set up like a usual joystick"));
        Assert.Contains(wizard.QuickNotes, n => n.StartsWith("JoyPro does not know the exact model of URSA MINOR NEXTGEN THROTTLE", StringComparison.Ordinal) &&
                                                n.Contains("set up like a usual throttle"));
        // Short names, never the instance GUID (walk-through 2026-11-14), and no planner words on the quick path.
        Assert.DoesNotContain(wizard.QuickNotes, n => n.Contains('{'));
        Assert.DoesNotContain("{", wizard.QuickSummary);
        Assert.DoesNotContain(wizard.ReviewDevices, d => d.Title.Contains('{'));
        Assert.DoesNotContain(wizard.ReviewRows, r => r.Reason.Contains("preference", StringComparison.Ordinal));
        Assert.DoesNotContain(wizard.ReviewAircraft, a => a.Title.Contains("relation", StringComparison.OrdinalIgnoreCase));
        // Short sentences: the list is what to look at, the blue button is named by its colour.
        Assert.Contains("Look at the list.", wizard.QuickSummary);
        SetupPlan plan = wizard.Plan!;
        SetupPlanRelation pitch = plan.Placed.First(r => r.Function == "pitch");
        Assert.StartsWith("URSA MINOR NEXTGEN FLIGHT CONTROLLER R", pitch.Device);
        Assert.Equal("JOY_Y", pitch.Input);
        Assert.Equal("JOY_X", plan.Placed.First(r => r.Function == "roll").Input);
        // The real trigger fires the guns: on the URSA MINOR it is button 37 / 38, not button 1 (a base button). JoyPro takes
        // it over from WinWing's catalog sticks of the same shape (review 2026-11-14 B7). The throttle is on a lever
        // (Rx / Ry), never on the thumb wheel (Z).
        Assert.Contains(plan.Placed, r => r.Function is "trigger1ststage" or "trigger2ndstage" && r.Input is "JOY_BTN37" or "JOY_BTN38" && r.Device.StartsWith("URSA MINOR NEXTGEN FLIGHT CONTROLLER R", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Placed, r => r.Function is "trigger1ststage" or "trigger2ndstage" && r.Input == "JOY_BTN1");
        Assert.Contains(plan.Placed, r => r.Function.StartsWith("throttle", StringComparison.Ordinal) && r.Input is "JOY_RX" or "JOY_RY" &&
                                          r.Device.StartsWith("URSA MINOR NEXTGEN THROTTLE", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Placed, r => r.Function.StartsWith("throttle", StringComparison.Ordinal) && r.Input == "JOY_Z");
    }

    // The pad persona (docs/auto-setup-eval.md "ACE COMBAT 8 player"): ACE COMBAT 8 is the only game, a T.Flight HOTAS 4.
    // The quick path alone must put every flight control on the virtual Xbox pad: pitch and roll, the throttle (both
    // triggers), yaw (twist on LB / RB), gun, missile, target, flares, High-G and the wingman D-pad, with the game's names.
    [Fact]
    public async Task AceCombat8TFlightHotas4_QuickPath_PutsTheFlightControlsOnThePad()
    {
        PersonaEngine engine = PersonaEngine.Create("tflight-hotas4.tsv", "AceCombat8", "ACE COMBAT 8: Wings of Theve");
        Assert.False(engine.Missing, "Build the engine first (engine/bin/x64/" + TestEngine.Configuration + "/engine.exe).");
        MainWindowViewModel vm = await FirstStartAsync(engine, "AceCombat8");
        await vm.StartQuickSetupAsync();
        SetupWizardViewModel wizard = vm.SetupWizard!;
        Assert.Equal(["AceCombat8"], wizard.ChosenGames);
        Assert.True(wizard.IsReviewStep, wizard.Status + " / " + wizard.QuickSummary);
        SetupPlan plan = wizard.Plan!;
        HashSet<string> placed = new(plan.Placed.Select(r => r.Function), StringComparer.OrdinalIgnoreCase);
        foreach (string function in new[] { "pitch", "roll", "rudder", "throttleright", "trigger1ststage", "launchweapon", "target_management_system_right", "cms_fwd_start_program_or_just_flare", "fbw_disconnect" })
            Assert.True(placed.Contains(function), function + " is not placed: " + string.Join(", ", placed.OrderBy(f => f)));
        Assert.Equal("stick.axis.pitch", plan.Placed.First(r => r.Function == "pitch").Control);
        Assert.Equal("stick.axis.twist", plan.Placed.First(r => r.Function == "rudder").Control);
        // The stick's only hat holds the target switch, flares and weapon change (they come first); look left / right goes
        // on the throttle's spring-return rudder rocker (catalog type "centring"), the free centring axis.
        Assert.Equal("throttle.axis.wheel.1", plan.Placed.First(r => r.Function == "view_look_horizontal").Control);
        Assert.Contains(plan.Placed, r => r.Function == "target_management_system_right" && r.Control.StartsWith("stick.hat.", StringComparison.Ordinal));
        // The game's names, not the DCS slots ("Kneeboard TOGGLE" = Radar map, "Select Heater" = Change weapon).
        HashSet<string> names = new(plan.Placed.Select(r => r.Name), StringComparer.Ordinal);
        foreach (string name in new[] { "Pitch", "Roll", "Yaw", "Throttle", "Fire gun", "Fire missile", "Switch target", "Change weapon", "Radar map", "Flares", "High-G turn" })
            Assert.True(names.Contains(name), name + " is not a relation name: " + string.Join(", ", names.OrderBy(n => n)));
        Assert.DoesNotContain(names, n => n is "Kneeboard TOGGLE" or "Select Heater" or "Trigger1stStage" or "LaunchWeapon");
        // The flight axes count as essential: all of them placed, and the quality says so.
        SetupQuality quality = plan.Quality.Single(q => q.Aircraft == "AceCombat8");
        Assert.True(quality.EssentialTotal >= 6 && quality.EssentialPlaced == quality.EssentialTotal, quality.EssentialPlaced + "/" + quality.EssentialTotal);

        // "Use these buttons", then what the pad gets (the real engine on the profile): everything reaches the pad.
        await wizard.ApplyCommand.ExecuteAsync(null);
        Assert.True(wizard.Applied, wizard.Status);
        EngineCommandResult check = await engine.RunReal(["--profile", engine.Profile, "vjoy", "pad-check", "--game", "AceCombat8", "--text"]);
        Assert.Equal(0, check.ExitCode);
        Assert.Contains("score 1", check.StandardOutput);
        Assert.DoesNotContain("MISS", check.StandardOutput);
        Assert.Contains("ok   look", check.StandardOutput);
    }

    internal static async Task<MainWindowViewModel> FirstStartAsync(PersonaEngine engine, string game = "IL2Game")
    {
        // An explicit handler that records every question (a question nobody expects fails the walk's asserts).
        MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = engine.Exe,
            ConfirmHandler = request =>
            {
                lock (engine.Asked)
                    engine.Asked.Add(request);
                return Task.FromResult(true);
            }
        };
        vm.ProfilePath = engine.Profile;
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        // The first start: the game setup lists IL-2 (found), Done saves it.
        vm.BuildGameSetup(firstStart: true);
        Assert.True(vm.GameSetupFirstStart);
        Assert.Contains(vm.GameSetupItems, g => g.GameId == game);
        Assert.True(await vm.ApplyGameSetupAsync());
        return vm;
    }

    // The setup commands in the real engine, everything else answered like a PC with only IL-2 installed.
    internal sealed class PersonaEngine : IEngineCliClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public List<IReadOnlyList<string>> RealCalls { get; } = [];

        // The questions FirstStartAsync's handler was asked.
        public List<ConfirmRequest> Asked { get; } = [];
        public string Exe { get; private init; } = string.Empty;
        public string Devices { get; private init; } = string.Empty;
        public string Profile { get; private init; } = string.Empty;
        public bool Missing { get; private init; }
        // The only installed game (IL-2 Great Battles, or ACE COMBAT 8 for the pad persona) and its name.
        public string Game { get; private init; } = "IL2Game";
        public string GameName { get; private init; } = "IL-2 Sturmovik: Great Battles";

        // What engine profile game-state answers before the quick write: by default a game whose control files hold
        // nothing yet (no question then).
        public string GameStateJson { get; set; } = "{\"Game\":\"IL2Game\",\"ConfigPath\":\"\",\"Imported\":false,\"Exported\":false,\"GameHasBinds\":false,\"GameBindCount\":0,\"GameJoystickBindCount\":0,\"GameFilesRead\":false}";

        public static PersonaEngine Create(string fixture, string game = "IL2Game", string gameName = "IL-2 Sturmovik: Great Battles")
        {
            string? exe = FindRepoFile(Path.Combine("engine", "bin", "x64", TestEngine.Configuration, "engine.exe"));
            string? devices = FindRepoFile(Path.Combine("engine", "tests", "fixtures", "setupwizard", fixture));
            if (exe == null || devices == null)
                return new PersonaEngine { Missing = true };
            string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "quick-setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            PersonaEngine engine = new() { Exe = exe, Devices = devices, Profile = Path.Combine(folder, "novice.pr0file"), Game = game, GameName = gameName };
            engine.RunReal(["profile", "new", engine.Profile]).GetAwaiter().GetResult();
            return engine;
        }

        public async Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            if (NoviceGuiTests.Seq(arguments, "setup") && !NoviceGuiTests.Seq(arguments, "setup", "games"))
            {
                lock (Calls)
                    RealCalls.Add(arguments.ToArray());
                return await RunReal(arguments);
            }
            return new EngineCommandResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(1), StandardOutput = Output(arguments), StandardError = string.Empty, Arguments = arguments.ToArray() };
        }

        public async Task<EngineCommandResult> RunReal(IReadOnlyList<string> arguments)
        {
            ProcessStartInfo info = new(Exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in arguments)
                info.ArgumentList.Add(arg);
            info.Environment["JOYPRO_FAKE_DEVICES"] = Devices;
            // The sandbox is a folder (the engine refuses writes outside it), never "1"; the data folder sits beside the profile.
            info.Environment["JOYPRO_TEST_SANDBOX"] = TestEnvironment.SandboxRoot;
            info.Environment["JOYPRO_DATA_DIR"] = Path.Combine(Path.GetDirectoryName(Profile) ?? Path.GetTempPath(), "data");
            info.Environment["JOYPRO_TEST_IGNORE_RUNNING_GAMES"] = "1";
            info.Environment["JOYPRO_NO_INTERNET_PICTURES"] = "1";
            // The DCS modules of this PC never count (the aircraft question's names and ticks do not depend on the PC).
            info.Environment["JOYPRO_INSTALL_DETECTION"] = "off";
            using Process process = Process.Start(info)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new EngineCommandResult { ExitCode = process.ExitCode, StandardOutput = await output, StandardError = await error, Arguments = arguments.ToArray(), Duration = TimeSpan.FromMilliseconds(1) };
        }

        private string Output(IReadOnlyList<string> args)
        {
            if (NoviceGuiTests.Seq(args, "games", "installs") || NoviceGuiTests.Seq(args, "games", "install"))
            {
                return "{\"Games\":[" +
                       "{\"GameId\":\"" + Game + "\",\"Name\":\"" + GameName + "\",\"Installed\":true,\"InstallPath\":\"D:\\\\Games\",\"Source\":\"test\",\"ConfigPaths\":[]}," +
                       (Game == "DCS"
                           ? "{\"GameId\":\"IL2Game\",\"Name\":\"IL-2 Sturmovik: Great Battles\",\"Installed\":false,\"InstallPath\":\"\",\"Source\":\"\",\"ConfigPaths\":[]}"
                           : "{\"GameId\":\"DCS\",\"Name\":\"DCS World\",\"Installed\":false,\"InstallPath\":\"\",\"Source\":\"\",\"ConfigPaths\":[]}") +
                       "]}";
            }
            // IL-2 has its own card; another game (ACE COMBAT 8) comes as a registry game.
            if (NoviceGuiTests.Seq(args, "games", "integrations"))
                return Game is "IL2Game" or "DCS"
                    ? "{\"Integrations\":[]}"
                    : "{\"Integrations\":[{\"GameId\":\"" + Game + "\",\"DisplayName\":\"" + GameName + "\",\"CommandName\":\"" + Game.ToLowerInvariant() + "\",\"Aliases\":[\"" + Game.ToLowerInvariant() + "\"],\"Experimental\":true,\"Notes\":\"\",\"SupportsCatalogRefresh\":true}]}";
            if (NoviceGuiTests.Seq(args, "db", "check-updates"))
                return "{\"Games\":[],\"Outdated\":0}";
            if (NoviceGuiTests.Seq(args, "profile", "game-state"))
                return GameStateJson;
            // Nothing assigned in IL-2 yet (its control files were never written).
            if (NoviceGuiTests.Seq(args, "setup", "games"))
                return "{\"Games\":[{\"Game\":\"" + Game + "\",\"Status\":\"empty\",\"Wizard\":true,\"ConfigFound\":false,\"GameBinds\":0,\"GameKeyboardBinds\":0,\"ProfileBinds\":0}],\"Warnings\":[]}";
            if (NoviceGuiTests.Seq(args, "profile", "games"))
                return "{\"Games\":[{\"GameId\":\"" + Game + "\",\"Name\":\"" + GameName + "\",\"Exporter\":\"" + (Game == "IL2Game" ? "il2" : Game == "DCS" ? "dcs" : Game.ToLowerInvariant()) + "\",\"Relations\":40,\"Bound\":40,\"Installed\":true}]}";
            return "ok";
        }

        private static string? FindRepoFile(string relative)
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }
    }
}
