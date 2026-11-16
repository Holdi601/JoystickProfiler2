using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Services.Help;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// The 2.7 beginner screens of the wiki (website/content/gui/help.md, quick-setup, vjoy, layout): the welcome with its
// answers, a tour card, F1's popup (normal and Easy words), the quick setup's result list and its "It worked!" after
// "Write to my game", the Virtual Joystick page in pad mode (the pretend controller's steps and the ViGEmBus card),
// Layout > Kneeboard and "Where is what?". Groups of JOYPRO_WEBSITE_SHOTS_ONLY: welcome, tour, f1-help, quick-setup,
// vjoy-pad, layout-kneeboard, where-is-what (all of them: novice). Offscreen only: the main window's page in the hidden
// root of HiddenWindow (the help layer places its cards there), the other windows measured and rendered, never shown.
public sealed partial class WebsiteScreenshotTests
{
    internal static readonly string[] NoviceGroups = ["welcome", "tour", "f1-help", "quick-setup", "vjoy-pad", "layout-kneeboard", "where-is-what"];

    // The Start page's setup card of the demo PC: two games with nothing on the sticks yet, two already in the profile.
    internal static List<AutoSetupGameItem> DemoAutoSetupGames() =>
    [
        new("StarCitizen", "Star Citizen", "empty", 0, 0),
        new("MSFS2024", "Microsoft Flight Simulator 2024", "empty", 0, 0),
        new("DCS", "DCS World", "profile", 0, 57),
        new("WarThunder", "War Thunder", "profile", 0, 14)
    ];

    // ---- The welcome, a tour card and F1 (the design profile) ----

    private static void HelpLayerShots(string outDir, List<string> leaks, bool welcome, bool tour, bool f1)
    {
        MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
        try
        {
            vm.StatusText = "Ready.";
            vm.ApplyAutoSetupStatus(DemoAutoSetupGames());
            foreach (string theme in Themes)
            {
                SetTheme(theme);
                if (welcome)
                {
                    // A first open with the demo PC's games: the answer JoyPro suggests for them is picked.
                    vm.CurrentStage = UiStage.Start;
                    List<string> installed = vm.GameCards.Where(c => c.Installed).Select(c => c.GameId).ToList();
                    vm.InstalledGamesForWelcome = () => installed;
                    vm.WelcomeOptions.Clear();
                    vm.OpenWelcome();
                    LayerShot(vm, "welcome", theme, 1280, 800, outDir, leaks, _ => { });
                    vm.IsWelcomeOpen = false;
                }
                if (tour)
                {
                    // The newcomer tour's second card, on the control it explains.
                    vm.CurrentStage = UiStage.Start;
                    vm.StartTour(TourCatalog.Newcomer, everyFeature: false, step: 1);
                    vm.StatusText = "Ready.";
                    LayerShot(vm, "tour", theme, 1280, 800, outDir, leaks, layer => layer.FindTourTarget());
                    vm.EndTourCommand.Execute(null);
                }
                if (f1)
                {
                    // F1 on "Set it all up for me" of the Start page, then the same with Easy words.
                    foreach ((string name, bool easy) in new[] { ("f1-help", false), ("f1-help-easy", true) })
                    {
                        vm.EasyWords = easy;
                        vm.CurrentStage = UiStage.Start;
                        vm.IsHelpModeOn = true;
                        LayerShot(vm, name, theme, 1280, 800, outDir, leaks, layer =>
                        {
                            Control? target = layer.FindTarget("cmd:QuickSetupCommand");
                            Assert.NotNull(target);
                            layer.ShowHelpFor(target, HelpLayer.PageOf(target, "start"));
                        });
                        vm.IsHelpModeOn = false;
                    }
                    vm.EasyWords = false;
                    vm.StatusText = "Ready.";
                }
            }
        }
        finally
        {
            vm.EasyWords = false;
            vm.InstalledGamesForWelcome = null;
            vm.StopGameConfigWatcher();
        }
    }

    // ---- The quick setup: the result list, then "It worked!" after "Write to my game" ----

    private static void QuickSetupShots(string outDir, List<string> leaks)
    {
        // The real engine's setup (IL-2 Great Battles and a WinWing URSA MINOR stick and throttle, the write sandbox);
        // the game list and the export are a fake: nothing reaches a game folder.
        // (made off the render thread: its engine call waits synchronously)
        QuickSetupPersonaTests.PersonaEngine engine = AvaloniaRenderThread.Wait(Task.Run(() => QuickSetupPersonaTests.PersonaEngine.Create("ursa-minor.tsv")));
        Assert.False(engine.Missing, "Build the engine first (engine/bin/x64/" + TestEngine.Configuration + "/engine.exe).");
        MainWindowViewModel vm = AvaloniaRenderThread.Wait(QuickSetupPersonaTests.FirstStartAsync(engine));
        try
        {
            AvaloniaRenderThread.Wait(Done(vm.StartQuickSetupAsync()));
            SetupWizardViewModel wizard = vm.SetupWizard!;
            Assert.True(wizard.IsReviewStep, wizard.Status + " / " + wizard.QuickSummary);
            CurrentRealProfile = vm.ProfilePath;
            foreach (string theme in Themes)
            {
                SetTheme(theme);
                DialogShot(new SetupWizardWindow { DataContext = wizard }, new Size(1240, 860), Path.Combine(outDir, "quick-setup-" + theme + ".png"), "quick-setup", leaks);
            }

            // "Use these buttons", then "Write to my game": the quick tour starts with "It worked!" on the Start page.
            AvaloniaRenderThread.Wait(Done(wizard.ApplyCommand.ExecuteAsync(null)));
            Assert.True(wizard.Applied, wizard.Status);
            wizard.ExportCommand.Execute(null);
            Stopwatch clock = Stopwatch.StartNew();
            while (vm.ActiveTour == null && clock.Elapsed < TimeSpan.FromSeconds(20))
                AvaloniaRenderThread.Wait(Task.Delay(25));
            Assert.Equal(TourCatalog.Quick, vm.ActiveTour?.Id);
            Assert.Equal(UiStage.Start, vm.CurrentStage);
            vm.EndTourCommand.Execute(null);
        }
        finally
        {
            CurrentRealProfile = string.Empty;
            vm.StopGameConfigWatcher();
        }

        // The same "It worked!" in the picture over the demo profile's Start page (the persona's fake profile answers
        // no relations, so its page behind the card would say 0): War Thunder written.
        MainWindowViewModel demo = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
        try
        {
            demo.ApplyAutoSetupStatus(DemoAutoSetupGames());
            demo.StartQuickTourAfterSetup(["WarThunder"]);
            Assert.Equal(TourCatalog.Quick, demo.ActiveTour?.Id);
            demo.StatusText = "Ready.";
            foreach (string theme in Themes)
            {
                SetTheme(theme);
                LayerShot(demo, "quick-setup-written", theme, 1280, 800, outDir, leaks, layer => layer.FindTourTarget());
            }
            demo.EndTourCommand.Execute(null);
        }
        finally
        {
            demo.StopGameConfigWatcher();
        }
    }

    // ---- Virtual Joystick in pad mode: only ACE COMBAT 8 is virtual, ViGEmBus not installed yet ----

    private static void VJoyPadShots(string outDir, List<string> leaks)
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "website-vjoy-pad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        VJoyFakeEngine engine = new(folder) { AceCombat8Relations = 6, ViGEmInstalled = false };
        engine.Modes["DCS"] = "native";
        string exe = Path.Combine(folder, "vjoy", "JoyPro.VJoy.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, string.Empty);
        MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            VirtualJoystickLauncher = new FakeFfbLauncher(),
            VirtualJoystickDispatch = action => action(),
            VirtualJoystickPollInterval = TimeSpan.FromHours(1),
            VirtualJoystickGuiPid = 777,
            VirtualJoystickGuiExe = @"C:\Program Files\JoyPro2\JoyPro.Gui.exe",
            VirtualJoystickExePath = exe,
            VirtualJoystickDataFolder = Path.Combine(folder, "data"),
            VirtualJoystickFindGame = _ => null,
            VirtualJoystickProcessPath = _ => null,
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        vm.ProfilePath = Path.Combine(folder, "My Sticks.pr0file");
        engine.Session = new JoyPro.Engine.VJoy.HidHideSession(engine.Cli, Path.Combine(folder, "data", "hidhide-session.txt"), (_, _) => engine.OwnerAlive);
        try
        {
            AvaloniaRenderThread.Wait(Done(vm.RefreshVirtualJoystickCommand.ExecuteAsync(null)));
            Assert.True(vm.VirtualJoystickPadOnly);
            Assert.True(vm.VirtualJoystickNeedsViGEm);
            vm.StatusText = "Ready.";
            vm.CurrentStage = UiStage.VirtualJoystick;
            foreach (string theme in Themes)
            {
                SetTheme(theme);
                Shot(vm, "vjoy-pad", theme, 1280, 1300, outDir, leaks);
            }
        }
        finally
        {
            vm.StopGameConfigWatcher();
        }
    }

    // ---- Layout > Kneeboard (the layout renders' profile, the Cheat sheets section) ----

    private static void KneeboardShot(MainWindowViewModel vm, string work, string theme, string outDir, List<string> leaks)
    {
        AvaloniaRenderThread.Wait(LayoutRedesignRenderTests.LayoutPages.ShowAsync(vm, "cheatsheets"));
        // The cheat sheets' folder of the page (above the card) as the demo user's.
        string folder = vm.LayoutExportPath;
        string status = vm.CheatSheetStatus;
        vm.LayoutExportPath = @"C:\Users\Pilot\Documents\JoyPro\Cheat sheets";
        vm.CheatSheetStatus = status.Replace(Path.Combine(work, "Cheat sheets"), vm.LayoutExportPath, StringComparison.OrdinalIgnoreCase);
        if (folder.Length > 0)
            vm.CheatSheetStatus = vm.CheatSheetStatus.Replace(folder, vm.LayoutExportPath, StringComparison.OrdinalIgnoreCase);
        vm.StatusText = "Ready.";
        Settle();
        try
        {
            Shot(vm, "layout-kneeboard", theme, 1280, 2600, outDir, leaks,
                crop: main => main.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.Text == JoyPro.Gui.Localization.Loc.T("Kneeboard") && t.Classes.Contains("h2"))
                    .Select(t => t.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("surface")))
                    .FirstOrDefault(b => b != null));
        }
        finally
        {
            vm.LayoutExportPath = folder;
            vm.CheatSheetStatus = status;
        }
    }

    // ---- "Where is what?": the layout window with the real engine's plan of a stick, throttle and pedals ----

    private static void WhereIsWhatShots(string outDir, List<string> leaks)
    {
        string? exe = FindUp(Path.Combine("engine", "bin", "x64", TestEngine.Configuration, "engine.exe"));
        string? devices = FindUp(Path.Combine("engine", "tests", "fixtures", "autoassign-matrix", "stick-throttle-pedals.tsv"));
        Assert.True(exe != null && devices != null, "Build the engine first; the fixture stick-throttle-pedals.tsv is needed.");
        string work = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "website-where-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string plan = Path.Combine(work, "plan.json");

        async Task<EngineCommandResult?> Run(IReadOnlyList<string> args, bool includeProfile, bool mutatesProfile, string label, bool quiet)
        {
            ProcessStartInfo info = new(exe!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--status");
            info.ArgumentList.Add("false");
            foreach (string arg in args)
                info.ArgumentList.Add(arg);
            info.Environment["JOYPRO_FAKE_DEVICES"] = devices!;
            info.Environment["JOYPRO_TEST_SANDBOX"] = TestEnvironment.SandboxRoot;
            info.Environment["JOYPRO_DATA_DIR"] = Path.Combine(work, "data");
            info.Environment["JOYPRO_TEST_IGNORE_RUNNING_GAMES"] = "1";
            info.Environment["JOYPRO_NO_INTERNET_PICTURES"] = "1";
            using Process process = Process.Start(info)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new EngineCommandResult { ExitCode = process.ExitCode, StandardOutput = await output, StandardError = await error, Arguments = args.ToArray() };
        }

        EngineCommandResult? made = AvaloniaRenderThread.Wait(Run(["setup", "plan", "--games", "DCS", "--only-aircraft", "F-16C_50,FA-18C_hornet", "--priorities", "F-16C_50",
            "--grip", "044F:0402=thrustmaster-warthog-stick", "--out-plan", plan], false, false, string.Empty, true));
        Assert.True(made != null && made.ExitCode == 0, made?.StandardError);
        foreach (string theme in Themes)
        {
            SetTheme(theme);
            LayoutWindowSettings settings = new() { Game = "DCS", Aircraft = "F-16C_50", Device = string.Empty };
            LayoutWindowViewModel vm = new(Run, plan, id => id == "DCS" ? "DCS World" : id, settings, () => { }, theme == "light", post: a => a(),
                workFolder: Path.Combine(work, theme));
            AvaloniaRenderThread.Wait(vm.LoadAsync());
            Stopwatch clock = Stopwatch.StartNew();
            while ((vm.IsWorking || vm.Devices.Count == 0) && clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                AvaloniaRenderThread.Pump();
                System.Threading.Thread.Sleep(50);
            }
            Assert.True(vm.Devices.Count >= 2, vm.Status);
            // TMS up pressed: its row lights up and the function is named at the top.
            LayoutWindowDevice stick = vm.Devices.First(d => d.Role == "main-stick");
            stick.Live.FrozenAt = 0;
            vm.OnInput(new JoystickInputEvent(stick.Device, "JOY_BTN7", true));
            DialogShot(new LayoutWindow { DataContext = vm }, new Size(1280, 900), Path.Combine(outDir, "where-is-what-" + theme + ".png"), "where-is-what", leaks);
        }
    }

    private static string? FindUp(string relative)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    // The main window's page in the hidden root (never on the screen), with the help layer's welcome, tour card or
    // popup placed; the demo paths in the texts. The window lets go of the view model afterwards: a later request of the
    // model (setup wizard, layout window) must never reach a window of this render.
    private static void LayerShot(MainWindowViewModel vm, string name, string theme, int width, int height, string outDir, List<string> leaks,
        Action<HelpLayer> prepare)
    {
        Settle();
        string realProfile = vm.ProfilePath;
        CurrentRealProfile = realProfile;
        vm.ProfilePath = DemoProfilePath;
        MainWindow main = new() { DataContext = vm };
        try
        {
            using HiddenWindow window = HiddenWindow.Create(main, new Size(width, height));
            try
            {
                HelpLayer layer = window.Page.GetVisualDescendants().OfType<HelpLayer>().Single();
                prepare(layer);
                for (int i = 0; i < 3; i++)
                {
                    window.Settle();
                    layer.Place();
                }
                if (Neutralize(window.Page))
                {
                    window.Settle();
                    layer.Place();
                }
                window.Settle();
                using RenderTargetBitmap bitmap = window.Render();
                string file = Path.Combine(outDir, name + "-" + theme + ".png");
                bitmap.Save(file);
                Assert.True(new FileInfo(file).Length > 1000);
                Scan(window.Page, name, leaks);
            }
            finally
            {
                window.Page.DataContext = null;
                main.DataContext = null;
            }
        }
        finally
        {
            if (vm.ProfilePath != realProfile)
                vm.ProfilePath = realProfile;
        }
    }
}
