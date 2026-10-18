using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// A first-time user, step by step, with the REAL engine and the devices of this PC: first start (Start page, the game
// setup), New profile, relations made on the Relations page (search, ticks, create; Auto Group), binds picked by hand on
// the Binds page, Modifiers, Layout, HUD, Export (DCS and IL-2 into temp folders, the other games as a dry run), History,
// Settings, Cockpits, Virtual Joystick. After every step the whole window is rendered offscreen (no window is shown, no
// input reaches the desktop) at 2267x1250 and 1280x800, dark theme. Also the "empty" (no profile) and "all unbound"
// (a restart with a profile whose relations have no bind) states.
// Per scenario: <out>\<scenario>\NN-step-<width>.png and journey.md (time per step, status line, every engine call
// with exit code and time, Avalonia binding errors, overlapping and cut-off texts found in the render).
// Opt-in, as it needs the built engine and takes minutes:
//   dotnet build engine\engine.csproj -c Debug -p:Platform=x64
//   set JOYPRO_JOURNEY_OUT=<work folder>\journey\out & dotnet test gui/JoyPro.Gui.Tests --filter UserJourneyTests
// Optional: JOYPRO_JOURNEY_ENGINE (engine.exe, default engine\bin\x64\Debug\engine.exe of this checkout),
// JOYPRO_JOURNEY_SCENARIOS (comma list of empty,new-user,unbound,visual-assign,settings-updates,device-replace; also
// msfs-store-import, best with a release-like engine folder as JOYPRO_JOURNEY_ENGINE so the MSFS command list is
// built on demand),
// JOYPRO_JOURNEY_OLD_PROFILE (the old-PC profile of device-replace, default <JOYPRO_WORK_DIR>\device-replace\oldpc-last.pr0file).
// Opt-in also detent: a copy of JOYPRO_JOURNEY_PROFILE (default <JOYPRO_WORK_DIR>\detent\Holdi-2025-03.pr0file), through
// detent mode: merge preview and merge of the per-aircraft throttle relations, the calibration wizard step by step,
// the aircraft groups, undo.
// Safety: every engine call goes through JourneyEngine, which refuses game writes (export without --dry-run outside the
// output folder, restore, refresh, nuke, defaults, mod, undo, ffb set); the engine and the GUI keep their data in
// <out>\<scenario>\data (JOYPRO_DATA_DIR); the game config locations are pointed at temp folders before the export.
[SupportedOSPlatform("windows")]
public sealed partial class UserJourneyTests
{
    [Fact]
    public void FirstTimeUserJourney()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_JOURNEY_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        string engine = Environment.GetEnvironmentVariable("JOYPRO_JOURNEY_ENGINE") is { Length: > 0 } e ? e : FindEngine();
        Assert.True(File.Exists(engine), "Build the engine first (engine.exe not found at " + engine + ").");
        string[] scenarios = (Environment.GetEnvironmentVariable("JOYPRO_JOURNEY_SCENARIOS") is { Length: > 0 } s ? s : "empty,new-user,unbound,visual-assign,settings-updates,device-replace,telemetry-recording")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Directory.CreateDirectory(outDir);

        List<string> failures = [];
        foreach (string scenario in scenarios)
        {
            Journey journey = new(Path.Combine(outDir, scenario), engine, scenario);
            try
            {
                journey.Run();
            }
            finally
            {
                journey.WriteReport();
            }
            failures.AddRange(journey.Failures.Select(f => scenario + ": " + f));
        }
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), failures.Count == 0 ? "All steps ran." : string.Join(Environment.NewLine, failures));
    }

    private static string FindEngine()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        return dir == null ? "engine.exe" : Path.Combine(dir.FullName, "engine", "bin", "x64", "Debug", "engine.exe");
    }

    private sealed partial class Journey
    {
        private static readonly (int Width, int Height)[] Sizes = [(2267, 1250), (1280, 800)];
        private static readonly string[] JourneyGames = ["DCS", "MSFS2024", "XPlane12", "IL2Game", "WarThunder"];

        private readonly string folder;
        private readonly string engine;
        private readonly string scenario;
        private readonly string data;
        private readonly string gameCopies;
        private readonly StringBuilder report = new();
        private readonly JourneyEngine cli;
        private readonly JourneyLogSink logSink = new();
        private int stepNumber;
        private MainWindowViewModel? vm;

        public List<string> Failures { get; } = [];

        public Journey(string folder, string engine, string scenario)
        {
            this.folder = folder;
            this.engine = engine;
            this.scenario = scenario;
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            data = Path.Combine(folder, "data");
            gameCopies = Path.Combine(folder, "game-copies");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(gameCopies);
            cli = new JourneyEngine(folder);
        }

        public void Run()
        {
            string? oldData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
            string? oldProfiles = Environment.GetEnvironmentVariable("JOYPRO_PROFILES_DIR");
            ILogSink? oldSink = Logger.Sink;
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", data);
            Environment.SetEnvironmentVariable("JOYPRO_PROFILES_DIR", Path.Combine(folder, "profiles"));
            report.AppendLine("# User journey: " + scenario).AppendLine();
            ResetGuiSettingsFiles();
            report.AppendLine("Engine: `" + engine + "`, data folder `" + data + "`, started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).AppendLine();
            try
            {
                AvaloniaRenderThread.Run(() =>
                {
                    Logger.Sink = logSink;
                    Avalonia.Threading.Dispatcher.UIThread.UnhandledException += OnUnhandled;
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                    try
                    {
                        switch (scenario)
                        {
                            case "empty":
                                RunEmpty();
                                break;
                            case "new-user":
                                RunNewUser();
                                break;
                            case "unbound":
                                RunUnbound();
                                break;
                            case "visual-assign":
                                RunVisualAssign();
                                break;
                            case "detent":
                                RunDetent();
                                break;
                            case "msfs-store-import":
                                RunMsfsStoreImport();
                                break;
                            case "start-import":
                                RunStartImport();
                                break;
                            case "big-profile":
                                RunBigProfile();
                                break;
                            case "settings-updates":
                                RunSettingsUpdates();
                                break;
                            case "device-replace":
                                RunDeviceReplace();
                                break;
                            case "telemetry-recording":
                                RunTelemetryRecording();
                                break;
                            case "trailer":
                                RunTrailer();
                                break;
                            default:
                                throw new ArgumentException("Unknown scenario " + scenario);
                        }
                    }
                    finally
                    {
                        Shutdown();
                        Avalonia.Threading.Dispatcher.UIThread.UnhandledException -= OnUnhandled;
                        Logger.Sink = oldSink;
                        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
                    }
                });
            }
            finally
            {
                Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", oldData);
                Environment.SetEnvironmentVariable("JOYPRO_PROFILES_DIR", oldProfiles);
            }
        }

        // The view model's settings files (ui-settings.json, engine-output.log, hud.json) follow the JOYPRO_DATA_DIR in
        // force when it is made: each scenario's view models use <scenario>\data\settings, which starts empty.
        private void ResetGuiSettingsFiles()
        {
            report.AppendLine("- settings folder: `" + AppInfo.SettingsFolder + "`").AppendLine();
        }

        private void OnUnhandled(object? sender, Avalonia.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            Failures.Add("Unhandled UI exception: " + e.Exception.GetType().Name + ": " + e.Exception.Message);
            report.AppendLine("**UNHANDLED UI EXCEPTION**: " + e.Exception).AppendLine();
        }

        private void Shutdown()
        {
            if (vm == null)
                return;
            try
            {
                vm.CurrentStage = UiStage.Start;
                AvaloniaRenderThread.Wait(SettleAsync(vm, 20000));
                AvaloniaRenderThread.Wait(vm.FlushAutosaveAsync());
            }
            catch (Exception ex)
            {
                report.AppendLine("Shutdown: " + ex.Message);
            }
            vm.StopGameConfigWatcher();
        }

        // ---- scenarios ----

        // The report of 2026-10-17: MSFS 2024 from the Microsoft Store, the user's profiles in the Store's save
        // containers, no JoyPro package in Community2024, and (with a release-like JOYPRO_JOURNEY_ENGINE) no command list
        // yet. Stand-in user folders (JOYPRO_TEST_MSFS_HOME) with the fixtures of engine\tests\fixtures\msfs\user-profiles;
        // then the same import with no profile anywhere (the message names the folders searched).
        private void RunMsfsStoreImport()
        {
            DirectoryInfo? repo = new(AppContext.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "JoyPro.sln")))
                repo = repo.Parent;
            string fixtures = Path.Combine(repo!.FullName, "engine", "tests", "fixtures", "msfs", "user-profiles", "profiles2024");
            string home = Path.Combine(folder, "h");
            string package = Path.Combine(home, "Local", "Packages", "Microsoft.Limitless_8wekyb3d8bbwe");
            string packages = Path.Combine(package, "LocalCache", "Packages");
            Directory.CreateDirectory(Path.Combine(packages, "Community2024"));
            File.WriteAllText(Path.Combine(package, "LocalCache", "UserCfg.opt"), "InstalledPackagesPath \"" + packages + "\"\r\n");
            MsfsProfileImportTests.WgsWriter.Write(Path.Combine(package, "SystemAppData", "wgs"), "Microsoft.Limitless_8wekyb3d8bbwe",
                new[] { "x56-stick-custom.xml", "crosswind-2024.xml", "keyboard-2024.xml" }
                    .Select((f, i) => new MsfsProfileImportTests.WgsContainer("InputProfile_" + i, [new MsfsProfileImportTests.WgsBlobData("data", File.ReadAllBytes(Path.Combine(fixtures, f)), DateTime.Now.AddMinutes(-i))])).ToList());
            string? oldHome = Environment.GetEnvironmentVariable("JOYPRO_TEST_MSFS_HOME");
            string? oldDetection = Environment.GetEnvironmentVariable("JOYPRO_INSTALL_DETECTION");
            Environment.SetEnvironmentVariable("JOYPRO_TEST_MSFS_HOME", home);
            Environment.SetEnvironmentVariable("JOYPRO_INSTALL_DETECTION", "off");
            try
            {
                EngineCommandResult install = AvaloniaRenderThread.Wait(cli.RunDirectAsync(engine, ["games", "install", "set", "MSFS2024", package]));
                Note("games install set MSFS2024 -> exit " + install.ExitCode);
                FirstStart(answerSetup: "later");
                Step("new profile", async () => await vm!.NewProfileAutoCommand.ExecuteAsync(null));
                Step("start page: your games", async () =>
                {
                    vm!.CurrentStage = UiStage.Start;
                    await vm.RefreshGameCardsCommand.ExecuteAsync(null);
                });
                Step("import: pick MSFS 2024", () =>
                {
                    GameCardItem? card = vm!.GameCards.FirstOrDefault(c => c.GameId == "MSFS2024");
                    if (card == null)
                        throw new InvalidOperationException("no MSFS 2024 card: " + string.Join(", ", vm.GameCards.Select(c => c.GameId)));
                    vm.StartImportWizardCommand.Execute(card);
                    return Task.CompletedTask;
                });
                Step("import: run (Store profiles, no JoyPro package)", async () => await vm!.RunImportWizardCommand.ExecuteAsync(null));
                Note("import result: " + vm!.ImportWizardResult + " | failed: " + vm.ImportWizardFailed);
                if (vm.ImportWizardFailed || vm.Relations.Count == 0)
                    Failures.Add("MSFS 2024 Store import: " + vm.ImportWizardResult);
                Step("relations after the import", () => { vm!.CurrentStage = UiStage.Relations; return Task.CompletedTask; });
                Step("binds after the import", () => { vm!.CurrentStage = UiStage.Binds; return Task.CompletedTask; });

                // No profile anywhere: the error says where JoyPro looked.
                string empty = Path.Combine(folder, "h0");
                string emptyPackage = Path.Combine(empty, "Local", "Packages", "Microsoft.Limitless_8wekyb3d8bbwe");
                Directory.CreateDirectory(Path.Combine(emptyPackage, "SystemAppData", "wgs"));
                Directory.CreateDirectory(Path.Combine(emptyPackage, "LocalCache", "Packages", "Community2024"));
                File.WriteAllText(Path.Combine(emptyPackage, "LocalCache", "UserCfg.opt"), "InstalledPackagesPath \"" + Path.Combine(emptyPackage, "LocalCache", "Packages") + "\"\r\n");
                Environment.SetEnvironmentVariable("JOYPRO_TEST_MSFS_HOME", empty);
                Step("import again: no MSFS 2024 profile anywhere", async () =>
                {
                    vm!.CurrentStage = UiStage.Start;
                    vm.RestartImportWizardCommand.Execute(null);
                    vm.StartImportWizardCommand.Execute(vm.GameCards.First(c => c.GameId == "MSFS2024"));
                    vm.ImportWizardAddToProfile = true;
                    await vm.RunImportWizardCommand.ExecuteAsync(null);
                });
                Note("second import result: " + vm.ImportWizardResult);
                if (!vm.ImportWizardFailed || !vm.ImportWizardResult.Contains("Searched:", StringComparison.Ordinal))
                    Failures.Add("MSFS 2024 import without profiles should fail naming the folders: " + vm.ImportWizardResult);
            }
            finally
            {
                Environment.SetEnvironmentVariable("JOYPRO_TEST_MSFS_HOME", oldHome);
                Environment.SetEnvironmentVariable("JOYPRO_INSTALL_DETECTION", oldDetection);
            }
        }

        // Tester report on 2.1.0: after an import from Start > Your games the games were gone until a restart. Imports
        // of the installed games from their cards (read-only on the game; IL-2 has no saved binds, so it fails): after
        // each the result is over the cards, leaving and coming back shows the cards.
        private void RunStartImport()
        {
            FirstStart(answerSetup: "later");
            Step("new profile", async () => await vm!.NewProfileAutoCommand.ExecuteAsync(null));
            Step("start page: your games", async () =>
            {
                vm!.CurrentStage = UiStage.Start;
                await vm.RefreshGameCardsCommand.ExecuteAsync(null);
                Note("cards: " + string.Join(", ", vm.GameCards.Select(c => c.GameId + (c.CanImport ? "" : " (no import)"))));
            }, extraRender: slug => RenderTall(slug, 3000));
            foreach (string gameId in new[] { "IL2Game", "DCS" })
            {
                GameCardItem? card = vm!.GameCards.FirstOrDefault(c => c.GameId == gameId && c.CanImport);
                if (card == null)
                {
                    Note(gameId + ": no card to import from");
                    continue;
                }
                Step("import " + gameId + ": choose how", () =>
                {
                    vm!.RestartImportWizardCommand.Execute(null);
                    vm.StartImportWizardCommand.Execute(card);
                    return Task.CompletedTask;
                });
                Step("import " + gameId + ": run", async () =>
                {
                    await vm!.RunImportWizardCommand.ExecuteAsync(null);
                    await SettleAsync(vm, 60000);
                    Note(gameId + " import: failed=" + vm.ImportWizardFailed + " result '" + Short(vm.ImportWizardResult, 300) + "'; stage " + vm.CurrentStage +
                         "; cards shown " + vm.ShowGameCardList + " (" + vm.GameCards.Count + ")");
                    if (!vm.ShowGameCardList || vm.GameCards.Count == 0 || vm.CurrentStage != UiStage.Start)
                        Failures.Add(gameId + " import: the Start page lost its games");
                }, extraRender: slug => RenderTall(slug, 3000));
                Step("import " + gameId + ": relations page and back to Start", () =>
                {
                    vm!.CurrentStage = UiStage.Relations;
                    vm.CurrentStage = UiStage.Start;
                    Note("after coming back: step " + vm.ImportWizardStep + ", cards shown " + vm.ShowGameCardList + "; card result '" + card.Result + "'");
                    if (!vm.IsImportWizardPickStep || !vm.ShowGameCardList)
                        Failures.Add(gameId + ": coming back to Start does not show the games");
                    return Task.CompletedTask;
                }, extraRender: slug => RenderTall(slug, 3000));
            }
        }

        // A fresh PC, no profile: the first start and then every page with nothing in it.
        private void RunEmpty()
        {
            FirstStart(answerSetup: "later");
            foreach ((string name, UiStage stage) in AllPages())
                Step("empty " + name, () => { vm!.CurrentStage = stage; return Task.CompletedTask; });
            Step("history panel open, no profile", () => { vm!.CurrentStage = UiStage.Start; vm.IsHistoryPanelOpen = true; return Task.CompletedTask; });
            Step("relations: search pitch without a profile", async () =>
            {
                vm!.IsHistoryPanelOpen = false;
                vm.CurrentStage = UiStage.Relations;
                vm.BuilderSearchText = "pitch";
                await vm.SearchBuilderActionsCommand.ExecuteAsync(null);
                Note("search results without profile: " + vm.BuilderSearchResults.Count + "; status: " + vm.BuilderSearchStatus);
            });
            Step("setup wizard (Start > set up my devices)", () =>
            {
                vm!.OpenSetupWizardCommand.Execute(null);
                return Task.CompletedTask;
            }, extraRender: RenderSetupWizard);
        }

        // The whole first session of a new user.
        private void RunNewUser()
        {
            FirstStart(answerSetup: "done");
            RedirectGameConfigs();
            Step("new profile", async () => await vm!.NewProfileAutoCommand.ExecuteAsync(null));
            Note("profile: " + vm!.ProfilePath);

            // Relations: "Pitch" from several games.
            Step("relations: new relation form", () =>
            {
                vm!.CurrentStage = UiStage.Relations;
                vm.StartNewRelationCommand.Execute(null);
                vm.RelationNameInput = "Pitch";
                return Task.CompletedTask;
            });
            Step("relations: search 'pitch'", async () =>
            {
                vm!.BuilderSearchText = "pitch";
                await vm.SearchBuilderActionsCommand.ExecuteAsync(null);
                await WaitForAsync(() => !vm.IsBuilderSearching, 30000);
                DescribeSearch();
            });
            Step("relations: axes only", async () =>
            {
                vm!.SetBuilderKindCommand.Execute("axis");
                await Task.Delay(400);
                await WaitForAsync(() => !vm.IsBuilderSearching, 30000);
                DescribeSearch();
            });
            Step("relations: tick one pitch axis per game", () =>
            {
                TickPerGame(axis: true);
                return Task.CompletedTask;
            });
            Step("relations: create Pitch", async () =>
            {
                await vm!.AddCheckedBuilderResultsCommand.ExecuteAsync(null);
                Note("relations now: " + string.Join(", ", vm.Relations.Select(r => r.Name)) + "; open: " + vm.SelectedRelation?.Name + " with " + vm.RelationNodes.Count + " nodes");
            });

            // A button relation: "Gear".
            Step("relations: new relation Gear, search 'gear'", async () =>
            {
                vm!.StartNewRelationCommand.Execute(null);
                vm.RelationNameInput = "Gear";
                vm.BuilderSearchText = "gear";
                await vm.SearchBuilderActionsCommand.ExecuteAsync(null);
                await WaitForAsync(() => !vm.IsBuilderSearching, 30000);
                vm.SetBuilderKindCommand.Execute("button");
                await Task.Delay(400);
                await WaitForAsync(() => !vm.IsBuilderSearching, 30000);
                DescribeSearch();
                TickPerGame(axis: false);
            });
            Step("relations: create Gear", async () =>
            {
                await vm!.AddCheckedBuilderResultsCommand.ExecuteAsync(null);
                Note("relations now: " + string.Join(", ", vm.Relations.Select(r => r.Name)) + "; open: " + vm.SelectedRelation?.Name + " with " + vm.RelationNodes.Count + " nodes");
            });

            // Group all my actions automatically.
            Step("relations: open 'Group all my actions automatically'", () =>
            {
                vm!.ToggleOrganizeCommand.Execute(null);
                return Task.CompletedTask;
            });
            Step("relations: auto group preview", async () =>
            {
                await vm!.PreviewAutoGroupCommand.ExecuteAsync(null);
                Note("auto group preview: " + Short(vm.AutoGroupPreview, 600));
            });
            Step("relations: auto group apply", async () =>
            {
                await vm!.ApplyAutoGroupCommand.ExecuteAsync(null);
                Note("relations after auto group: " + vm.Relations.Count + " (" + string.Join(", ", vm.Relations.Take(15).Select(r => r.Name)) + ")");
            });
            Step("relations: close the tool, open Pitch", async () =>
            {
                vm!.CloseRelationToolsCommand.Execute(null);
                vm.SelectedRelation = vm.Relations.FirstOrDefault(r => r.Name == "Pitch") ?? vm.Relations.FirstOrDefault();
                await WaitForAsync(() => vm.RelationNodes.Count > 0, 5000);
            });

            // Binds.
            Step("binds page", () => { vm!.CurrentStage = UiStage.Binds; return Task.CompletedTask; });
            Note("bind rows: " + vm!.Binds.Count + ", unbound " + vm.Binds.Count(b => b.IsUnbound) + ", HasNoBinds=" + vm.HasNoBinds);
            BindByHand("Pitch", axis: true);
            BindByHand("Gear", axis: false);
            Step("modifiers page", () => { vm!.CurrentStage = UiStage.Modifiers; return Task.CompletedTask; });

            // Layout with the real devices.
            Step("layout page", () => { vm!.CurrentStage = UiStage.Layout; return Task.CompletedTask; });
            Note("layout suggestions: " + string.Join(" | ", vm.LayoutSuggestions.Select(l => l.DisplayName)));
            foreach (LayoutSuggestionItem item in vm.LayoutSuggestions.Skip(1).Take(2).ToList())
                Step("layout: show " + item.DisplayName, async () => await vm!.ShowWorkspaceDeviceAsync(item.Device));
            // The 3D views are packs of JoyPro's releases: from the local release folder (tools\build_device_3d_packs.ps1),
            // offered for the connected devices plus a Warthog stick, downloaded, shown as the default view.
            string feed3d = Path.Combine(Environment.GetEnvironmentVariable("JOYPRO_RELEASE_DIR") ?? string.Empty, "device-3d");
            if (File.Exists(Path.Combine(feed3d, "manifest.json")))
            {
                vm!.Device3DPacksFactory = () => new DeviceTurntablePacks(feed3d);
                vm.Device3DMode = () => CockpitPicturePacks.Ask;
                Step("layout: 3D view offered", async () =>
                    await vm.CheckDevice3DAsync(vm.DeviceCards.Where(c => c.Connected).Select(c => c.ModelId).Append("thrustmaster-warthog-stick").ToList()));
                Note("3D offer: " + vm.Device3DOfferText);
                Step("layout: 3D view downloaded and shown", async () =>
                {
                    await vm.AcceptDevice3DCommand.ExecuteAsync(null);
                    await vm.ShowDeviceReferenceModelAsync("thrustmaster-warthog-stick");
                });
                Note("3D: " + vm.Device3DStatus + "; shows 3D " + vm.ShowWorkspace3D + "; model " + vm.DeviceReference.Model?.Id + "; reference " + vm.LayoutShowsReference +
                     "; installed " + Directory.Exists(Path.Combine(DeviceTurntablePacks.InstalledRoot(), "thrustmaster-warthog-stick")) + "; turntable " + vm.HasWorkspaceTurntable);
            }
            Step("hud page", () => { vm!.CurrentStage = UiStage.Hud; return Task.CompletedTask; });

            // Export: DCS and IL-2 into temp folders, the others as a dry run.
            Step("export page", () => { vm!.CurrentStage = UiStage.Export; return Task.CompletedTask; });
            Note("export tiles: " + string.Join(" | ", vm.ExportGames.Select(g => g.Name + " (" + g.Exporter + ", " + g.Relations + " relations, " + g.Bound + " bound" + (g.IsSelected ? ", ticked" : string.Empty) + ")")) +
                 "; status: " + vm.ExportGamesStatus + " " + vm.ExportGamesNotInstalled);
            Step("export: each game (DCS/IL-2 to temp copies, others dry run)", async () =>
            {
                vm!.ExportDcsPath = Path.Combine(gameCopies, "DCS");
                vm.ExportIl2Path = Path.Combine(gameCopies, "IL2Game");
                vm.RegistryGameDryRun = true;
                foreach (ExportGameItem game in vm.ExportGames.ToList())
                {
                    if (!game.CanExport)
                        continue;
                    if (game.Exporter == "starcitizen")
                        continue;
                    Stopwatch sw = Stopwatch.StartNew();
                    await vm.ExportGameNowCommand.ExecuteAsync(game);
                    Note("export " + game.Name + ": " + game.ExportState + (game.ExportFailed ? " (FAILED)" : string.Empty) + " in " + sw.ElapsedMilliseconds + " ms");
                }
            }, extraRender: slug => RenderTall(slug, 5200));
            Step("export: Export ticked games", async () =>
            {
                // Star Citizen would be written in place (the journey engine refuses it): not ticked here.
                foreach (ExportGameItem game in vm!.ExportGames.Where(g => g.Exporter == "starcitizen"))
                    game.IsSelected = false;
                Note("ticked: " + string.Join(", ", vm.ExportGames.Where(g => g.IsSelected).Select(g => g.Name)));
                await vm.ExportSelectedGamesCommand.ExecuteAsync(null);
                Note("status: " + vm.StatusText + " | tiles: " + vm.ExportGamesStatus + " | backups listed: " + vm.GameBackups.Count +
                     " | undo enabled on: " + string.Join(", ", vm.ExportGames.Where(g => g.CanUndoExport).Select(g => g.Name)));
            }, extraRender: slug => RenderTall(slug, 5200));
            Step("history panel", () => { vm!.CurrentStage = UiStage.Start; vm.IsHistoryPanelOpen = true; return Task.CompletedTask; });
            Note("history versions: " + vm.HistoryVersions.Count);
            Step("history closed, start page with a profile", () => { vm!.IsHistoryPanelOpen = false; return Task.CompletedTask; },
                extraRender: slug => RenderTall(slug, 3000));
            foreach ((string name, UiStage stage) in new[] { ("settings", UiStage.Settings), ("cockpits", UiStage.Cockpits), ("virtual joystick", UiStage.VirtualJoystick), ("ffb telemetry", UiStage.FfbTelemetry) })
                Step(name + " page", () => { vm!.CurrentStage = stage; return Task.CompletedTask; });
            // Cockpits with the pictures made from the user's games (JOYPRO_JOURNEY_PICTURES names the folder with the
            // games' picture folders DCS, XPlane12, as "engine cockpits pictures build --out <folder>" makes it): overview,
            // a game panel, the stick, then JoyPro's drawing of the panel.
            string? pictures = Environment.GetEnvironmentVariable("JOYPRO_JOURNEY_PICTURES");
            if (!string.IsNullOrWhiteSpace(pictures) && Directory.Exists(pictures))
            {
                Step("cockpits: F-16 with the pictures from DCS", () =>
                {
                    vm!.CurrentStage = UiStage.Cockpits;
                    vm.Cockpits.PicturesRoot = pictures;
                    vm.Cockpits.RefreshPicturesStatus();
                    vm.Cockpits.Show("f-16", "f-16c-block-50");
                    Note("pictures: " + vm.Cockpits.PicturesStatus + " | " + vm.Cockpits.GamePicturesText + " | pages " + vm.Cockpits.Pages.Count);
                    return Task.CompletedTask;
                }, extraRender: slug => RenderTall(slug, 3000));
                Step("cockpits: a DCS panel page", () =>
                {
                    CockpitPageItem? panel = vm!.Cockpits.PanelPages.FirstOrDefault(p => p.Page.Id.StartsWith("game:", StringComparison.Ordinal) && p.Page.Title.Contains("ENG", StringComparison.Ordinal))
                                             ?? vm.Cockpits.PanelPages.FirstOrDefault(p => p.Page.Id.StartsWith("game:", StringComparison.Ordinal));
                    if (panel != null)
                        vm.Cockpits.SelectedPage = panel;
                    Note("panel page: " + (panel?.Title ?? "none"));
                    return Task.CompletedTask;
                }, extraRender: slug => RenderTall(slug, 3000));
                // The cockpit guide: search "battery", pick the control, its callout is marked and its explanation open.
                Step("cockpits: the guide finds the battery and marks it", () =>
                {
                    vm!.Cockpits.PageSearchText = "battery";
                    CockpitGuideRow? row = vm.Cockpits.GuideRows.FirstOrDefault(r => r.IsControl && r.Node.Hits.Any(h => h.Tag.StartsWith("click:", StringComparison.Ordinal)));
                    if (row != null)
                    {
                        vm.Cockpits.SelectTag(row.Node.Hits.First(h => h.Tag.StartsWith("click:", StringComparison.Ordinal)).Tag);
                        vm.Cockpits.ShowNode(row.Node);
                    }
                    Note("guide: " + vm.Cockpits.GuideStatus + " | " + vm.Cockpits.GuideSearchStatus + " | picked: " + (row?.Name ?? "none") + " | page: " + vm.Cockpits.CurrentPage?.Title + " | mark: " + vm.Cockpits.SelectedTag);
                    if (vm.Cockpits.HasGuideTree && row == null)
                        Failures.Add("cockpits: the guide found no battery control with a callout");
                    return Task.CompletedTask;
                }, extraRender: slug => RenderTall(slug, 3000));
                Step("cockpits: the guide's search cleared", () => { vm!.Cockpits.PageSearchText = string.Empty; return Task.CompletedTask; });
                Step("cockpits: the DCS stick page", () => { vm!.Cockpits.OpenPage("device:stick"); return Task.CompletedTask; }, extraRender: slug => RenderTall(slug, 3000));
                Step("cockpits: JoyPro's drawing of the panel", () =>
                {
                    vm!.Cockpits.PictureModeIndex = CockpitsViewModel.ModeDrawing;
                    CockpitPageItem? panel = vm.Cockpits.PanelPages.FirstOrDefault(p => p.Page.Id.StartsWith("game:", StringComparison.Ordinal) && p.Page.Title.Contains("ENG", StringComparison.Ordinal));
                    if (panel != null)
                        vm.Cockpits.SelectedPage = panel;
                    return Task.CompletedTask;
                }, extraRender: slug => RenderTall(slug, 3000));
                // The other games' pictures where the folder has them: MSFS 2020 (rendered from its glTF interior),
                // MSFS 2024 (screenshots taken in the sim, the aircraft's input events listed), Nuclear Option.
                foreach ((string game, string aircraft, string variant, string what) in new[]
                {
                    ("MSFS2020", "c152", "c152-classic", "Cessna 152 with the pictures from MSFS 2020"),
                    ("MSFS2024", "h125", "h125-as350b3e", "H125 with the screenshots from MSFS 2024"),
                    ("NuclearOption", "no-a-19-brawler", "", "A-19 Brawler with the pictures from Nuclear Option")
                })
                {
                    if (!Directory.Exists(Path.Combine(pictures, game)))
                        continue;
                    Step("cockpits: " + what, () =>
                    {
                        vm!.Cockpits.PictureModeIndex = CockpitsViewModel.ModePicture;
                        if (variant.Length > 0)
                            vm.Cockpits.Show(aircraft, variant);
                        else
                            vm.Cockpits.Show(aircraft);
                        Note(game + ": " + vm.Cockpits.GamePicturesText + " | pages " + vm.Cockpits.Pages.Count);
                        if (!vm.Cockpits.HasGamePictures)
                            Failures.Add("cockpits: no pictures from " + game + " for " + aircraft);
                        return Task.CompletedTask;
                    }, extraRender: slug => RenderTall(slug, 3000));
                    Step("cockpits: a " + game + " panel page", () =>
                    {
                        CockpitPageItem? panel = vm!.Cockpits.PanelPages.FirstOrDefault(p => p.Page.Id.StartsWith("game:", StringComparison.Ordinal));
                        if (panel != null)
                            vm.Cockpits.SelectedPage = panel;
                        Note("panel page: " + (panel?.Title ?? "none"));
                        return Task.CompletedTask;
                    }, extraRender: slug => RenderTall(slug, 3000));
                }
            }
            // Several games with the same aircraft: the Boeing 737-800 from X-Plane 12 (rendered) and MSFS 2024
            // (screenshots); "Pictures from" switches between them (docs/cockpits.md "Picture quality").
            if (!string.IsNullOrWhiteSpace(pictures) && Directory.Exists(Path.Combine(pictures, "XPlane12")) && Directory.Exists(Path.Combine(pictures, "MSFS2024")))
            {
                Step("cockpits: Boeing 737-800 with the pictures from X-Plane 12", () =>
                {
                    vm!.CurrentStage = UiStage.Cockpits;
                    vm.Cockpits.PicturesRoot = pictures;
                    vm.Cockpits.PictureModeIndex = CockpitsViewModel.ModePicture;
                    vm.Cockpits.Show("b737", "b737-800");
                    Note("737: " + vm.Cockpits.GamePicturesText + " | from: " + string.Join(", ", vm.Cockpits.GamePictureSources) + " | pages " + vm.Cockpits.Pages.Count);
                    if (!vm.Cockpits.HasGamePictureChoice)
                        Failures.Add("cockpits: the 737 offers no choice of the games' pictures");
                    return Task.CompletedTask;
                }, extraRender: slug => RenderTall(slug, 3000));
                Step("cockpits: Boeing 737-800, an X-Plane 12 panel page", () =>
                {
                    CockpitPageItem? panel = vm!.Cockpits.PanelPages.FirstOrDefault(p => p.Page.Id.StartsWith("game:", StringComparison.Ordinal) && p.Page.Title.Contains("Pedestal", StringComparison.Ordinal))
                                             ?? vm.Cockpits.PanelPages.FirstOrDefault(p => p.Page.Id.StartsWith("game:", StringComparison.Ordinal));
                    if (panel != null)
                        vm.Cockpits.SelectedPage = panel;
                    Note("panel page: " + (panel?.Title ?? "none"));
                    return Task.CompletedTask;
                }, extraRender: slug => RenderTall(slug, 3000));
                Step("cockpits: Boeing 737-800, pictures from MSFS 2024 chosen", () =>
                {
                    vm!.Cockpits.GamePictureSourceIndex = 1;
                    Note("737: " + vm.Cockpits.GamePicturesText);
                    return Task.CompletedTask;
                }, extraRender: slug => RenderTall(slug, 3000));
            }
            // The photos at the bottom of the page (the 747: 2 of 5 used to load): served by a local stand-in for
            // Wikimedia that answers the first request of each photo with "429 too many requests", as Wikimedia does
            // with a program that asks for several at once; the page waits and loads every one.
            Step("cockpits: the 747's photos (local stand-in server, 429 first)", async () =>
            {
                string cache = Path.Combine(folder, "photo-cache");
                string? oldCache = Environment.GetEnvironmentVariable("JOYPRO_DEVICE_IMAGE_CACHE");
                Environment.SetEnvironmentVariable("JOYPRO_DEVICE_IMAGE_CACHE", cache);
                bool enabled = DeviceImageCache.InternetPicturesEnabled, blocked = DeviceImageCache.InternetPicturesBlocked;
                TimeSpan retry = DeviceImageCache.FirstRetryDelay;
                try
                {
                    DeviceImageCache.InternetPicturesEnabled = true;
                    DeviceImageCache.InternetPicturesBlocked = false;
                    DeviceImageCache.FirstRetryDelay = TimeSpan.FromMilliseconds(50);
                    DeviceImageCache.TestHandler = new JourneyPhotoServer();
                    vm!.CurrentStage = UiStage.Cockpits;
                    vm.Cockpits.Show("b747", "b747-8");
                    vm.Cockpits.PhotoAnswerCommand.Execute("yes");
                    await vm.Cockpits.PhotoLoad;
                    int loaded = vm.Cockpits.Photos.Count(p => p.Image != null), withFile = vm.Cockpits.Photos.Count(p => p.HasPhoto);
                    Note("747 photos: " + vm.Cockpits.PhotoStatus + " (" + loaded + " of " + withFile + ")");
                    if (loaded != withFile || withFile == 0)
                        Failures.Add("cockpits: the 747 shows " + loaded + " of " + withFile + " photos");
                }
                finally
                {
                    DeviceImageCache.TestHandler = null;
                    DeviceImageCache.FirstRetryDelay = retry;
                    DeviceImageCache.InternetPicturesEnabled = enabled;
                    DeviceImageCache.InternetPicturesBlocked = blocked;
                    Environment.SetEnvironmentVariable("JOYPRO_DEVICE_IMAGE_CACHE", oldCache);
                }
            }, extraRender: slug => RenderTall(slug, 9000));
            // Cockpits with a picture pack of a release (docs/cockpits.md "Picture packs") from a local feed: the
            // F-16 without pictures gets the offer, then the downloaded pictures; the list of the release's games.
            // JOYPRO_JOURNEY_PICTURE_FEED names a pack folder as tools/build_cockpit_packs.ps1 makes it; without it a
            // feed of the synthetic test cockpit is made in the scenario folder.
            string packFeed = Environment.GetEnvironmentVariable("JOYPRO_JOURNEY_PICTURE_FEED") is { Length: > 0 } f && Directory.Exists(f)
                ? f
                : FakePictureFeed.Create(Path.Combine(folder, "picture-feed"), Path.GetDirectoryName(Path.GetDirectoryName(CockpitPictureTests.WriteSyntheticSet(Path.Combine(folder, "picture-source")))!)!, [("DCS", "F-16C_50", 1)]);
            Step("cockpits: picture pack offered for the F-16", async () =>
            {
                vm!.CurrentStage = UiStage.Cockpits;
                vm.Cockpits.PictureFeed = packFeed;
                vm.Cockpits.PackMode = () => CockpitPicturePacks.Ask;
                vm.Cockpits.PicturesRoot = Path.Combine(folder, "pack-pictures");
                vm.Cockpits.Show("a-10", null);
                await vm.Cockpits.PackCheck;
                vm.Cockpits.Show("f-16", "f-16c-block-50");
                await vm.Cockpits.PackCheck;
                Note("offer: " + vm.Cockpits.HasPackOffer + " | " + vm.Cockpits.PackOfferText + " | pictures: " + vm.Cockpits.HasGamePictures);
                if (!vm.Cockpits.HasPackOffer)
                    Failures.Add("cockpits: no picture pack offer for the F-16 from " + packFeed);
            });
            Step("cockpits: F-16 pictures downloaded from the pack", async () =>
            {
                await vm!.Cockpits.DownloadOfferedPacksCommand.ExecuteAsync(null);
                Note("after download: " + vm.Cockpits.PackStatus + " | " + vm.Cockpits.GamePicturesText + " | pages " + vm.Cockpits.Pages.Count);
                if (!vm.Cockpits.HasGamePictures)
                    Failures.Add("cockpits: the downloaded pack shows no pictures (" + vm.Cockpits.PackStatus + ")");
            }, extraRender: slug => RenderTall(slug, 3000));
            Step("cockpits: the release's picture packs", async () =>
            {
                vm!.Cockpits.PacksExpanded = true;
                await vm.Cockpits.PackGamesLoad;
                Note("pack games: " + string.Join(", ", vm.Cockpits.PackGames.Select(g => g.Name + " " + g.Summary)));
            });
            // Settings > DCS: default binds, read only: the status of the real DCS install and the dry-run list of
            // the files the strip option would change (the journey guard refuses every write to the install).
            Step("settings: DCS default binds (status and preview)", async () =>
            {
                vm!.CurrentStage = UiStage.Settings;
                foreach (var section in vm.SettingsSections)
                    section.IsExpanded = section.Id == "dcs-defaults";
                await vm.RefreshDcsDefaultsStatusCommand.ExecuteAsync(null);
                await vm.PreviewDcsStripDefaultsCommand.ExecuteAsync(null);
                Note("DCS default binds: " + vm.DcsDefaultsStatus + " | preview: " + vm.DcsDefaultsPreviewTitle + " (" + vm.DcsDefaultsPreviewFiles.Count + " files)" +
                     (vm.HasDcsDefaultsAttention ? " | attention: " + vm.DcsDefaultsAttention : string.Empty));
            }, extraRender: slug => RenderTall(slug, 5200));
        }

        // A profile made earlier whose relations have no bind, opened at the next start (last profile).
        private void RunUnbound()
        {
            // Session 1, not rendered: a profile with a few relations from the search, nothing bound.
            MainWindowViewModel first = CreateVm();
            vm = first;
            AvaloniaRenderThread.Wait(first.RunStartupChecksAsync());
            first.SkipGameSetup();
            AvaloniaRenderThread.Wait(first.NewProfileAutoCommand.ExecuteAsync(null));
            foreach ((string name, string query, bool axis) in new[] { ("Pitch", "pitch", true), ("Roll", "roll", true), ("Gear", "gear", false), ("Flaps Down", "flaps down", false) })
                AvaloniaRenderThread.Wait(CreateRelationAsync(first, name, query, axis));
            AvaloniaRenderThread.Wait(SettleAsync(first, 20000));
            AvaloniaRenderThread.Wait(first.FlushAutosaveAsync());
            first.StopGameConfigWatcher();
            Note("session 1 made " + first.Relations.Count + " relations in " + first.ProfilePath);

            // Session 2: JoyPro starts again and opens the last profile.
            vm = CreateVm();
            Step("restart: startup with the last profile", async () =>
            {
                await vm!.RunStartupChecksAsync();
                await vm.AutoOpenLastProfileOnStartupAsync();
            });
            Note("profile: " + vm!.ProfilePath + "; relations " + vm.Relations.Count + "; binds rows " + vm.Binds.Count + "; HasNoBinds=" + vm.HasNoBinds);
            foreach ((string name, UiStage stage) in AllPages())
                Step("unbound " + name, () => { vm!.CurrentStage = stage; return Task.CompletedTask; });
            Step("unbound: binds, select first row", () =>
            {
                vm!.CurrentStage = UiStage.Binds;
                vm.SelectedBind = vm.Binds.FirstOrDefault();
                return Task.CompletedTask;
            });
            Step("unbound: history panel", () => { vm!.CurrentStage = UiStage.Start; vm.IsHistoryPanelOpen = true; return Task.CompletedTask; });
        }

        // Bind on the picture (Bind Studio > Visual assign) with the real devices of this PC: Pitch and three buttons,
        // two modifiers, binds with and without modifiers, then the Layout page's tables with one column per modifier
        // combination and the engine's cheat sheet of them.
        // A profile from an old PC (a copy of JOYPRO_JOURNEY_OLD_PROFILE, default <JOYPRO_WORK_DIR>\device-replace\oldpc-last.pr0file,
        // skipped without it): opening it moves the devices that came back with a new ID (notice with Undo); then the
        // Replace device window for a stick that was replaced by another one (by layout), Move binds, and Undo.
        private void RunDeviceReplace()
        {
            string source = JourneyInput("JOYPRO_JOURNEY_OLD_PROFILE", "device-replace", "oldpc-last.pr0file");
            if (!File.Exists(source))
            {
                Note("no old profile at " + source + "; scenario skipped");
                return;
            }
            FirstStart(answerSetup: "later");
            string profile = Path.Combine(folder, "profiles", "old-pc.pr0file");
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            File.Copy(source, profile, overwrite: true);
            Step("open the old PC's profile", async () =>
            {
                vm!.ProfilePath = profile;
                await vm.LoadProfileCommand.ExecuteAsync(null);
                Note("notice: " + vm.HasDeviceRemapNotice + " | " + vm.DeviceRemapNoticeTitle + " | " + string.Join(" / ", vm.DeviceRemapNoticeItems.Select(i => i.Text)) + " | " + vm.DeviceRemapSkippedText);
            });
            Step("settings: devices with the notice", async () =>
            {
                vm!.CurrentStage = UiStage.Settings;
                await vm.LoadDeviceCardsCommand.ExecuteAsync(null);
            }, extraRender: slug => RenderTall(slug, 2600));
            foreach ((string oldPart, string newPart) in new[] { ("CLSE", "Rhino"), ("Gunfighter", "L-VPC Stick") })
            {
                Step("replace device window: " + oldPart + " -> " + newPart, async () =>
                {
                    await vm!.PrepareReplaceDeviceAsync(null, null);
                    string? old = vm.ReplaceOldChoices.FirstOrDefault(d => d.Contains(oldPart, StringComparison.OrdinalIgnoreCase));
                    string? target = vm.ReplaceNewChoices.FirstOrDefault(d => d.Contains(newPart, StringComparison.OrdinalIgnoreCase));
                    Note("old choices: " + string.Join(" | ", vm.ReplaceOldChoices) + "; new choices: " + string.Join(" | ", vm.ReplaceNewChoices));
                    if (old == null || target == null)
                    {
                        Note("no " + oldPart + " / " + newPart + " here");
                        return;
                    }
                    await vm.PrepareReplaceDeviceAsync(old, target);
                    Note("preview: " + vm.ReplaceSummary + " | " + vm.ReplaceModels + " | " + vm.ReplaceCounts + " | merge: " + vm.ReplaceNeedsMerge + " | warnings: " + vm.ReplaceWarnings);
                    foreach (DeviceReplaceRow row in vm.ReplaceRows.Take(60))
                        Note("  " + row.OldText + " -> " + row.ChosenInput + " " + row.NewPartText + " | " + row.MethodText + " | " + row.UsedByText + (row.HasOverlap ? " | " + row.OverlapText : string.Empty));
                }, extraRender: slug =>
                {
                    RenderDialog(slug + " window 2267", new ReplaceDeviceWindow(), 2267, 1250);
                    RenderDialog(slug + " window 1280", new ReplaceDeviceWindow(), 1280, 800);
                });
            }
            Step("replace: CLSE -> Rhino, move binds", async () =>
            {
                await vm!.PrepareReplaceDeviceAsync(vm.ReplaceOldChoices.FirstOrDefault(d => d.Contains("CLSE", StringComparison.OrdinalIgnoreCase)),
                    vm.ReplaceNewChoices.FirstOrDefault(d => d.Contains("Rhino", StringComparison.OrdinalIgnoreCase)));
                bool ok = await vm.ApplyReplaceDeviceAsync();
                Note("applied: " + ok + " | " + vm.ReplaceStatus + " | notice: " + vm.DeviceRemapNoticeTitle + " | undo: " + vm.UndoLabel);
            });
            Step("notice: undo the replace", async () =>
            {
                await vm!.UndoDeviceRemapCommand.ExecuteAsync(null);
                Note("after undo: " + vm.StatusText + " | binds on CLSE: " + vm.Binds.Count(b => b.Device.Contains("CLSE", StringComparison.OrdinalIgnoreCase)));
            });
        }

        private void RunVisualAssign()
        {
            FirstStart(answerSetup: "later");
            Step("new profile", async () => await vm!.NewProfileAutoCommand.ExecuteAsync(null));
            foreach ((string name, string query, bool axis) in new[] { ("Pitch", "pitch", true), ("Gear", "gear", false), ("Flaps Down", "flaps down", false), ("Canopy", "canopy", false) })
                Step("relations: create " + name, async () => await CreateRelationAsync(vm!, name, query, axis));
            Note("relations: " + string.Join(", ", vm!.Relations.Select(r => r.Name + " (" + r.Type + ")")));

            string device = string.Empty;
            Step("binds: select Pitch, open Visual assign", async () =>
            {
                vm!.CurrentStage = UiStage.Binds;
                vm.SelectedBind = vm.Binds.FirstOrDefault(b => b.RelationName == "Pitch");
                vm.ToggleVisualAssignModeCommand.Execute(null);
                await WaitForAsync(() => vm.VisualAssignDeviceOptions.Count > 0, 10000);
                if (vm.VisualAssignDeviceOptions.Count == 0)
                    await vm.RefreshQuickBindOptionsCommand.ExecuteAsync(null);
                Note("device options: " + string.Join(" | ", vm.VisualAssignDeviceOptions));
                device = vm.VisualAssignDeviceOptions.FirstOrDefault(d => d.Contains("WarBRD", StringComparison.OrdinalIgnoreCase))
                         ?? vm.VisualAssignDeviceOptions.FirstOrDefault(d => d.Contains("Gunfighter", StringComparison.OrdinalIgnoreCase))
                         ?? vm.VisualAssignDeviceOptions.FirstOrDefault() ?? string.Empty;
                vm.VisualAssignDevice = device;
                await WaitForAsync(() => vm.ReferenceLegendItems.Count > 0, 20000);
                Note("device: " + device + "; legend rows " + vm.ReferenceLegendItems.Count + "; picture " + vm.LayoutWorkspacePictureCaption);
            });
            if (device.Length == 0)
            {
                Failures.Add("no device for visual assign");
                return;
            }

            VisualAssign("Pitch", "JOY_Y", string.Empty);
            VisualAssign("Gear", "JOY_BTN3", string.Empty);
            VisualAssign("Canopy", "JOY_BTN13", string.Empty);
            VisualAssign("Flaps Down", "JOY_BTN14", string.Empty);
            // A button relation on an axis: refused with a reason, nothing changes.
            Step("visual assign: Gear on an axis is refused", async () =>
            {
                vm!.SelectedBind = vm.Binds.FirstOrDefault(b => b.RelationName == "Gear");
                vm.DeviceReference.SelectedLegendRow = vm.ReferenceLegendItems.FirstOrDefault(r => r.Input == "JOY_X");
                vm.SelectedLayoutBindCandidate = null;
                await vm.BindSelectedLayoutMarkerCommand.ExecuteAsync(null);
                Note("status: " + vm.VisualAssignStatus);
            });

            // Two modifiers (on the stick's pinky button and index rocker), put on Canopy and Flaps Down.
            foreach ((string name, string input, string relation) in new[] { ("Shift A", "JOY_BTN30", "Canopy"), ("Shift B", "JOY_BTN22", "Flaps Down") })
            {
                Step("modifiers: create " + name + " on " + relation, async () =>
                {
                    vm!.CurrentStage = UiStage.Modifiers;
                    vm.ModifierCreateName = name;
                    vm.ModifierCreateDevice = device;
                    vm.ModifierCreateInput = input;
                    vm.ModifierCreateTargetRelation = relation;
                    await vm.CreateModifierOnSelectedBindCommand.ExecuteAsync(null);
                    Note("status: " + vm.StatusText + "; modifiers: " + string.Join(", ", vm.ModifierNames));
                });
            }
            Step("binds: back to the picture", () =>
            {
                vm!.CurrentStage = UiStage.Binds;
                return Task.CompletedTask;
            });
            VisualAssign("Gear", "JOY_BTN8", "Shift A+Shift B");
            VisualAssign("Pitch", "JOY_X", "Shift B");

            // Live input (docs/ui/live-input.md): the device's button held and its Y axis moved light up on the picture.
            Step("live: hold Gear's button, move Y", () =>
            {
                vm!.WorkspaceLiveInput.FrozenAt = vm.WorkspaceLiveInput.Now;
                vm.OnLiveInput(new JoystickInputEvent(device, "JOY_BTN3", true));
                vm.OnLiveAxes(new JoystickAxisSnapshot(device, [0.5, 0.5, 0, 0, 0, 0, 0, 0]));
                vm.OnLiveAxes(new JoystickAxisSnapshot(device, [0.5, 0.15, 0, 0, 0, 0, 0, 0]));
                Note("live: pressed " + string.Join(",", vm.WorkspaceLiveInput.Pressed) + "; axes in use " + string.Join(",", vm.WorkspaceLiveInput.ActiveAxes()) +
                     "; lit rows " + string.Join(",", vm.ReferenceLegendItems.Where(r => r.IsLive).Select(r => r.Input)));
                if (!vm.ReferenceLegendItems.Any(r => r.IsLive && r.Input == "JOY_BTN3"))
                    Failures.Add("live: the row of JOY_BTN3 does not light up");
                return Task.CompletedTask;
            });
            // Follow input: the picture closed, the same button selects its relations (unmodified first).
            Step("follow input: press Gear's button", async () =>
            {
                vm!.OnLiveInput(new JoystickInputEvent(device, "JOY_BTN3", false));
                vm.WorkspaceLiveInput.FrozenAt = null;
                vm.OpenVisualAssignMode = false;
                // A button of the device bound right now (the binds above moved Gear to BTN 8 with both modifiers).
                BindListItem? bound = vm.Binds.FirstOrDefault(b => b.HasBind && !b.IsAxisType && OverlayResolver.SameDevice(b.Device, device));
                vm.SelectedBind = vm.Binds.FirstOrDefault(b => !ReferenceEquals(b, bound));
                vm.OnLiveInput(new JoystickInputEvent(device, "JOY_BTN3", true));
                Note("follow BTN 3 (nothing bound now): selected " + vm.SelectedBind?.RelationName + "; note: " + vm.FollowInputToast);
                vm.OnLiveInput(new JoystickInputEvent(device, "JOY_BTN3", false));
                if (bound == null)
                {
                    Failures.Add("follow input: no bound button on " + device);
                    return;
                }
                await Task.Delay(200);
                vm.OnLiveInput(new JoystickInputEvent(device, bound.Input, true));
                Note("follow " + bound.Input + ": selected " + vm.SelectedBind?.RelationName + "; note: " + vm.FollowInputToast);
                string expected = vm.RelationsOnInput(device, bound.Input).Select(r => r.Relation).FirstOrDefault() ?? bound.RelationName;
                if (vm.SelectedBind?.RelationName != expected)
                    Failures.Add("follow input: " + bound.Input + " did not select " + expected);
                vm.OnLiveInput(new JoystickInputEvent(device, bound.Input, false));
            });
            Step("binds: picture again", () =>
            {
                vm!.OpenVisualAssignMode = true;
                return Task.CompletedTask;
            });
            Note("binds now: " + string.Join(" | ", vm!.Binds.Where(b => b.HasBind).Select(b => b.RelationName + " = " + b.InputLabel + " [" + b.ModifiersSummary + "]")) +
                 "; header: " + vm.ProfileBoundText);

            // The Relations page: each card shows device, input and every modifier; the aircraft chip names its aircraft;
            // follow input jumps to the relation here too (2.1.0 tester feedback).
            Step("relations: cards with device, input and modifiers", async () =>
            {
                vm!.OpenVisualAssignMode = false;
                vm.CurrentStage = UiStage.Relations;
                await vm.RefreshRelationsCommand.ExecuteAsync(null);
                await Task.Delay(500);
                foreach (RelationListItem card in vm.Relations.Where(r => r.IsBound))
                    Note("card " + card.Name + ": '" + card.BindDevice + "' '" + card.BindInputPart + "' modifiers [" + string.Join(", ", card.ModifierChips) + "]; tooltip '" + Short(card.PlanesTooltip.Replace("\n", " / "), 200) + "'");
                RelationListItem? gear = vm.Relations.FirstOrDefault(r => r.Name == "Gear");
                if (gear == null || gear.ModifierChips.Count != 2)
                    Failures.Add("relations: Gear's card does not show its two modifiers");
                if (vm.Relations.Any(r => r.HasPlanes && !r.PlanesTooltip.Contains(':')))
                    Failures.Add("relations: an aircraft chip without its list");
            }, extraRender: slug => RenderTall(slug, 2400));
            Step("relations: follow input jumps to Gear", async () =>
            {
                BindListItem? gearBind = vm!.Binds.FirstOrDefault(b => b.RelationName == "Gear" && b.HasBind);
                vm.SelectedRelation = vm.Relations.FirstOrDefault(r => r.Name == "Pitch");
                await Task.Delay(300);
                if (gearBind == null)
                {
                    Failures.Add("relations follow: Gear is not bound");
                    return;
                }
                bool jumped = false;
                void OnJump(object? s, EventArgs e) => jumped = true;
                vm.FollowInputJumped += OnJump;
                vm.OnLiveInput(new JoystickInputEvent(device, gearBind.Input, true));
                vm.FollowInputJumped -= OnJump;
                Note("follow on Relations: selected " + vm.SelectedRelation?.Name + "; note '" + vm.FollowInputToast + "'; jump event " + jumped + "; monitor " + vm.IsLiveInputMonitoring);
                string expected = vm.RelationsOnInput(device, gearBind.Input).Select(r => r.Relation).FirstOrDefault() ?? "Gear";
                if (vm.SelectedRelation?.Name != expected || !jumped)
                    Failures.Add("relations follow: " + gearBind.Input + " did not select " + expected);
                vm.OnLiveInput(new JoystickInputEvent(device, gearBind.Input, false));
                await Task.Delay(300);
            }, extraRender: slug => RenderTall(slug, 2400));
            Step("binds: back to the picture after the Relations page", () =>
            {
                vm!.CurrentStage = UiStage.Binds;
                vm.OpenVisualAssignMode = true;
                return Task.CompletedTask;
            });

            // The Layout page: the tables with a column per modifier combination.
            Step("layout: the device's tables", async () =>
            {
                vm!.ToggleVisualAssignModeCommand.Execute(null);
                vm.CurrentStage = UiStage.Layout;
                await vm.ShowWorkspaceDeviceAsync(device);
                await WaitForAsync(() => vm.LayoutShowsCallouts, 10000);
                // The grip question over the picture ("Which grip is on your base?"): Not now.
                if (vm.ShowReferenceQuestion)
                    vm.DeviceReference.DismissQuestionCommand.Execute(null);
                DescribeCallouts();
            });
            Step("layout: all combinations", () =>
            {
                vm!.CalloutModifier = MainWindowViewModel.AllCombinationsChoice;
                DescribeCallouts();
                return Task.CompletedTask;
            });
            Step("layout: at most 3 columns", () =>
            {
                vm!.CalloutMaxColumns = 3;
                DescribeCallouts();
                return Task.CompletedTask;
            });
            Step("layout: combinations in use again", () =>
            {
                vm!.CalloutMaxColumns = 8;
                vm.CalloutModifier = MainWindowViewModel.AutoModifierChoice;
                vm.SaveWorkspaceCommand.Execute(null);
                DescribeCallouts();
                return Task.CompletedTask;
            });
            // The device icon: the picker offers the device's pictures; the cel-shaded drawing becomes the icon.
            Step("layout: device icon picker", async () =>
            {
                await vm!.OpenDeviceIconPickerCommand.ExecuteAsync(null);
                Note("icon options: " + string.Join(", ", vm.DeviceIconOptions.Select(o => o.Title)) + "; " + vm.DeviceIconStatus);
                if (vm.DeviceIconOptions.Count < 2)
                    Failures.Add("device icon: no picture offered");
            });
            Step("layout: choose the cel-shaded icon", () =>
            {
                DeviceIconOption? pick = vm!.DeviceIconOptions.FirstOrDefault(o => o.Kind == "celart") ?? vm.DeviceIconOptions.LastOrDefault();
                vm.ChooseDeviceIconCommand.Execute(pick);
                Note("icon: " + vm.WorkspaceDeviceIconTitle + "; " + vm.DeviceIconStatus);
                return Task.CompletedTask;
            });
            Step("start: the device card shows the icon", async () =>
            {
                vm!.IsDeviceIconPickerOpen = false;
                vm.CurrentStage = UiStage.Start;
                await Task.Delay(300);
                Note("cards: " + string.Join(" | ", vm.DeviceCards.Select(c => c.Title + " = " + c.PictureSource)));
            });
            Step("binds: the bind list's device chips", () =>
            {
                vm!.OpenVisualAssignMode = false;
                vm.CurrentStage = UiStage.Binds;
                Note("rows with an icon: " + vm.Binds.Count(b => b.HasDeviceIcon) + " of " + vm.Binds.Count(b => b.HasBind));
                return Task.CompletedTask;
            });
            Step("layout: back to the device", () =>
            {
                vm!.CurrentStage = UiStage.Layout;
                return Task.CompletedTask;
            });
            Step("cheat sheet of the tables (engine layout callouts)", async () =>
            {
                string? file = DeviceReferenceService.CalloutFileOf(device);
                if (file == null)
                {
                    Note("no callout file");
                    return;
                }
                string png = Path.Combine(folder, "cheatsheet-" + Slug(DeviceMarkerText.DeviceName(device)) + ".png");
                EngineCommandResult result = await cli.RunDirectAsync(engine, ["layout", "callouts", "--layout", file, "--device", device, "--png", png, "--print-width", "2400", "--profile", vm!.ProfilePath]);
                Note("engine layout callouts -> " + result.ExitCode + " " + Short(result.StandardError + " " + result.StandardOutput, 400) + "; png " + (File.Exists(png) ? png : "(none)"));
            });
            Note("trace:\n" + string.Join("\n", vm!.GuiTrace.Where(l => l.Contains("[VisualAssign]") || l.Contains("[Callouts]"))));
        }

        // The user's big profile (a copy: JOYPRO_JOURNEY_PROFILE, e.g. 524 relations, 3 300 actions): the filter bar on
        // the Relations and Binds pages (DCS, then only some A-10 versions, a device, bound; each change timed, under
        // 300 ms), the bind labels with device and modifiers, the catalog of an aircraft, Suggest missing for one
        // relation and for every relation, the question before removing an action and Settings > Confirmations.
        private void RunBigProfile()
        {
            string? source = Environment.GetEnvironmentVariable("JOYPRO_JOURNEY_PROFILE");
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                Failures.Add("set JOYPRO_JOURNEY_PROFILE to a copy of a big profile");
                return;
            }
            string profile = Path.Combine(folder, "profiles", "big.pr0file");
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            File.Copy(source, profile, overwrite: true);
            // JOYPRO_JOURNEY_GAMEDATA: a copy of a PC's GameData (DB with the game lists built there, CockpitPictures
            // json files) so the lists look as on that PC (MSFS menu names, the installed games' catalogs).
            if (Environment.GetEnvironmentVariable("JOYPRO_JOURNEY_GAMEDATA") is { Length: > 0 } gameData && Directory.Exists(gameData))
            {
                foreach (string file in Directory.GetFiles(gameData, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(data, "GameData", Path.GetRelativePath(gameData, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                }
            }
            vm = CreateVm();
            Step("open the big profile (as the last profile at start)", async () =>
            {
                vm!.LastProfilePath = profile;
                await vm.RunStartupChecksAsync();
                vm.SkipGameSetup();
                await vm.AutoOpenLastProfileOnStartupAsync();
                vm.CurrentStage = UiStage.Relations;
            });
            Note("profile " + vm!.ProfilePath + ": " + vm.ProfileRelationCount + " relations, " + vm.Relations.Count + " shown; games in the filter: " +
                 string.Join(", ", vm.RelationFilter.Games.Select(g => g.Label + " " + g.Count + (g.HasPlanes ? " (" + g.Planes.Count + " aircraft)" : string.Empty))) +
                 "; devices: " + string.Join(", ", vm.RelationFilter.Devices.Select(d => d.Label + " " + d.Count)) + "; groups: " + vm.RelationFilter.Groups.Count);
            Note("bind labels: " + string.Join(" | ", vm.Relations.Where(r => r.IsBound).Take(8).Select(r => r.Name + " = " + r.BindLabel)));
            if (vm.ProfileRelationCount == 0)
            {
                Failures.Add("the big profile did not open");
                return;
            }

            void Timed(string name, Action change, ListFilterViewModel filter, bool binds = false)
            {
                Step(name, () =>
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    change();
                    sw.Stop();
                    double fill = binds ? vm!.BindFilterMilliseconds : vm!.RelationFilterMilliseconds;
                    Note(name + ": " + filter.ResultText + " in " + sw.ElapsedMilliseconds + " ms (list fill " + fill.ToString("0.0") + " ms); chips: " +
                         string.Join(" | ", filter.ActiveChips.Select(c => c.Label)));
                    if (sw.ElapsedMilliseconds >= 300)
                        Failures.Add(name + " took " + sw.ElapsedMilliseconds + " ms (limit 300)");
                    return Task.CompletedTask;
                });
            }

            ListFilterViewModel relations = vm.RelationFilter;
            Timed("relations: filter DCS", () => relations.SelectGame("DCS"), relations);
            FilterGameItem? dcs = relations.Games.FirstOrDefault(g => g.Game == "DCS");
            string[] a10 = dcs?.Planes.Select(p => p.Label).Where(p => p.StartsWith("A-10", StringComparison.OrdinalIgnoreCase) && !p.EndsWith("_easy", StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
            Note("A-10 versions in the profile: " + string.Join(", ", a10));
            Timed("relations: only " + string.Join(", ", a10), () => relations.SelectOnlyPlanes("DCS", a10), relations);
            if (dcs != null)
            {
                dcs.IsExpanded = true;
                dcs.PlaneFilterText = "A-10";
                RenderFilterGames(relations, "relations-games-dropdown");
                dcs.PlaneFilterText = string.Empty;
            }
            string device = relations.Devices.OrderByDescending(d => d.Count).Select(d => d.Key).FirstOrDefault(d => !d.Contains("Keyboard", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            Timed("relations: and the device " + DisplayText.ShortDevice(device), () => relations.SelectDevices(device), relations);
            Timed("relations: and bound only, buttons", () => { relations.SetBoundCommand.Execute("bound"); relations.SetKindCommand.Execute("button"); }, relations);
            Timed("relations: typed search 'dms'", () => vm!.RelationSearchText = "dms", relations);
            Timed("relations: clear all", () => relations.ClearAll(), relations);
            Timed("relations: group of the first relation", () => relations.SelectGroups(relations.Groups.First().Key), relations);
            Timed("relations: clear all again", () => relations.ClearAll(), relations);

            // Open an A-10 relation with a bind and modifiers.
            RelationListItem? open = vm.Relations.FirstOrDefault(r => r.Name.StartsWith("A10", StringComparison.OrdinalIgnoreCase) && r.IsBound && r.Modifiers.Count > 0)
                                     ?? vm.Relations.FirstOrDefault(r => r.IsBound && r.Modifiers.Count > 0) ?? vm.Relations.First();
            Step("relations: open " + open.Name, async () =>
            {
                vm!.SelectedRelation = open;
                await WaitForAsync(() => vm.RelationNodes.Count > 0 && !vm.IsBuilderSuggestLoading, 30000);
                Note("open " + open.Name + ": " + vm.RelationNodes.Count + " actions; bind " + open.BindLabel + "; " + vm.SuggestMissingLabel);
            });
            Step("relations: Suggest missing for " + open.Name, async () =>
            {
                await vm!.SuggestMissingCommand.ExecuteAsync(null);
                Note("sections: " + string.Join(" | ", vm.BuilderSuggestionSections.Select(s => s.Header + ": " + string.Join(", ", s.Items.Take(4).Select(i => i.GameLabel + " '" + i.Title + "' " + i.MissingText)))));
                Note("status: " + vm.StatusText);
            }, extraRender: slug => RenderTall(slug, 2400));
            // "Pitch": Suggest missing lists the pitch axes of the games it lacks, one recommended per game (ticked), the
            // variants collapsed under it with how they differ; MSFS by its menu name.
            RelationListItem? pitch = vm.Relations.FirstOrDefault(r => r.Name == "Pitch");
            if (pitch != null)
            {
                Step("relations: open Pitch and Suggest missing", async () =>
                {
                    vm!.SelectedRelation = pitch;
                    await WaitForAsync(() => vm.RelationNodes.Count > 0 && !vm.IsBuilderSuggestLoading, 60000);
                    await vm.SuggestMissingCommand.ExecuteAsync(null);
                    Note("Pitch function: " + vm.BuilderFunctionText + "; looks wrong: " + vm.RelationMisfits.Count + "; ticked " + vm.BuilderTickedCount);
                    Note("Pitch sections: " + string.Join(" | ", vm.BuilderSuggestionSections.Select(s => s.Header + ": " + string.Join(", ", s.Items.Take(14).Select(i =>
                        i.GameLabel + " '" + i.DisplayTitle + "'" + (i.IsRecommended ? " [recommended" + (i.RecommendedFor.Length > 0 ? " " + i.RecommendedFor : string.Empty) + "]" : i.HasVariant ? " (" + i.Variant + ")" : string.Empty))))));
                    // The other relation's list must never show here (the user saw 'UFC 3 Key' buttons under Pitch).
                    if (vm.BuilderSuggestions.Any(s => s.Why.Contains("UFC 3", StringComparison.OrdinalIgnoreCase)) || vm.BuilderFunctionText.Contains("UFC", StringComparison.OrdinalIgnoreCase))
                        Failures.Add("Pitch shows another relation's suggestions");
                }, extraRender: slug => RenderTall(slug, 2400));
                Step("relations: Pitch, show the other Elite and MSFS actions", () =>
                {
                    foreach (BuilderSuggestionItem item in vm!.BuilderSuggestions.Where(i => i.HasAlternatives && (i.Game == "EliteDangerous" || i.Game == "MSFS2024" || i.Game == "IL2Korea")))
                        item.IsExpanded = true;
                    return Task.CompletedTask;
                }, extraRender: slug => RenderTall(slug, 2400));
            }
            Step("relations: Open catalog", async () =>
            {
                await vm!.OpenCatalogCommand.ExecuteAsync(null);
                await WaitForAsync(() => !vm.IsCatalogLoading, 30000);
                Note("catalog: " + vm.CatalogStatus + "; categories: " + string.Join(", ", vm.CatalogCategories.Take(12).Select(c => c.Name + " (" + c.CountText + ")")));
            });
            Step("relations: catalog, open HOTAS, tick two free actions", () =>
            {
                CatalogCategoryItem? hotas = vm!.CatalogCategories.FirstOrDefault(c => c.Name.Equals("HOTAS", StringComparison.OrdinalIgnoreCase)) ?? vm.CatalogCategories.FirstOrDefault();
                if (hotas != null)
                {
                    vm.ToggleCatalogCategoryCommand.Execute(hotas);
                    foreach (CatalogRowItem row in hotas.VisibleRows.Where(r => !r.IsUsed && r.Row.CanCheck).Take(2))
                        row.Row.IsChecked = true;
                    Note("HOTAS rows: " + string.Join(" | ", hotas.VisibleRows.Take(12).Select(r => r.Row.Title + (r.IsUsed ? " [" + r.UsedText + (r.IsBound ? ", " + r.BoundText : string.Empty) + "]" : string.Empty))));
                }
                Note("add label: " + vm.CatalogAddLabel);
                return Task.CompletedTask;
            }, extraRender: slug => RenderTall(slug, 2400));
            Step("relations: catalog, Add selected", async () =>
            {
                int before = vm!.RelationNodes.Count;
                await vm.AddCheckedCatalogCommand.ExecuteAsync(null);
                await WaitForAsync(() => !vm.IsBusy, 30000);
                Note("actions " + before + " -> " + vm.RelationNodes.Count + "; status: " + vm.StatusText + " / " + vm.CatalogStatus);
            });
            Step("relations: x on an action asks first", async () =>
            {
                ConfirmRequest? asked = null;
                vm!.ConfirmHandler = request => { asked = request; request.DontAskAgain = true; return Task.FromResult(true); };
                int before = vm.RelationNodes.Count;
                await vm.RemoveNodeCommand.ExecuteAsync(vm.RelationNodes.Last());
                await WaitForAsync(() => !vm.IsBusy, 30000);
                Note("asked: " + (asked == null ? "(nothing)" : asked.Title + " / " + asked.Message + " / key " + asked.DontAskKey) + "; actions " + before + " -> " + vm.RelationNodes.Count +
                     "; ask again: " + vm.AskBeforeRemovingAction);
                if (asked == null)
                    Failures.Add("removing an action did not ask");
                if (asked != null)
                    RenderConfirm(asked, "remove-action-question");
            });
            Step("settings: Confirmations", () =>
            {
                vm!.CurrentStage = UiStage.Settings;
                return Task.CompletedTask;
            });
            Step("settings: ask again", () =>
            {
                vm!.AskAllConfirmationsAgainCommand.Execute(null);
                Note("ask before removing: " + vm.AskBeforeRemovingAction);
                vm.CurrentStage = UiStage.Relations;
                return Task.CompletedTask;
            });
            Step("relations: Suggest missing for every relation", async () =>
            {
                Stopwatch sw = Stopwatch.StartNew();
                await vm!.SuggestMissingForAllCommand.ExecuteAsync(null);
                Note("in " + sw.ElapsedMilliseconds + " ms: " + vm.MissingReviewStatus);
                Note("first: " + string.Join(" | ", vm.MissingReview.Take(6).Select(r => r.Name + " (" + r.Summary + "): " + string.Join(", ", r.Items.Take(3).Select(i => i.GameLabel + " '" + i.Title + "'")))));
            }, extraRender: slug => RenderTall(slug, 2400));

            // Binds page: its own filters.
            Step("binds page", () => { vm!.CurrentStage = UiStage.Binds; return Task.CompletedTask; });
            ListFilterViewModel binds = vm.BindFilter;
            Note("bind rows: " + vm.Binds.Count + "; first: " + string.Join(" | ", vm.Binds.Where(b => b.HasBind).Take(6).Select(b => b.RelationName + " = " + b.BindLabel)));
            Timed("binds: filter DCS F-16C", () => binds.SelectOnlyPlanes("DCS", "F-16C_50"), binds, binds: true);
            Timed("binds: and the device " + DisplayText.ShortDevice(device), () => binds.SelectDevices(device), binds, binds: true);
            Timed("binds: not bound", () => { binds.ClearAll(); binds.SetBoundCommand.Execute("unbound"); }, binds, binds: true);
            Timed("binds: clear all", () => binds.ClearAll(), binds, binds: true);
        }

        private void RenderFilterGames(ListFilterViewModel filter, string name)
        {
            stepNumber++;
            string file = Path.Combine(folder, stepNumber.ToString("D2") + "-" + name + ".png");
            Window window = new() { Content = new Border { Padding = new Thickness(12), Child = new JoyPro.Gui.Views.Controls.ListFilterGamesPanel() }, DataContext = filter };
            List<string> problems = RenderControl(window, 380, 1100, file);
            report.AppendLine("## " + Path.GetFileName(file) + " (the Games drop-down)").AppendLine();
            foreach (string problem in problems.Take(25))
                report.AppendLine("  - " + problem);
            report.AppendLine();
        }

        private void RenderConfirm(ConfirmRequest request, string name)
        {
            stepNumber++;
            string file = Path.Combine(folder, stepNumber.ToString("D2") + "-" + name + ".png");
            List<string> problems = RenderControl(new ConfirmWindow(request), 520, 260, file);
            report.AppendLine("## " + Path.GetFileName(file) + " (the question)").AppendLine();
            foreach (string problem in problems.Take(25))
                report.AppendLine("  - " + problem);
            report.AppendLine();
        }

        // Hidden games on the Start page, the Settings page's sections (closed, open, moved, searched, kept for the next
        // start), About from the version in the corner, every state of the update dialog against local fake release
        // feeds (no release, same version, no internet, newer, a bad checksum, update now, skip this version) and the
        // Export page's remembered settings after a restart.
        private void RunSettingsUpdates()
        {
            string feeds = Path.Combine(folder, "release-feeds");
            FakeReleaseFeed same = FakeReleaseFeed.Create(Path.Combine(feeds, "same"), AppInfo.Version);
            FakeReleaseFeed newer = FakeReleaseFeed.Create(Path.Combine(feeds, "newer"), "2.1.0");
            FakeReleaseFeed bad = FakeReleaseFeed.Create(Path.Combine(feeds, "bad"), "2.1.0", badHash: true);
            string? oldFeed = Environment.GetEnvironmentVariable(UpdateService.FeedVariable);
            Environment.SetEnvironmentVariable(UpdateService.FeedVariable, same.FeedPath);
            try
            {
                FirstStart(answerSetup: "done");
                Note("update check at start: " + vm!.UpdateStatus + "; dialog shown: " + vm.UpdatePromptVisible);
                if (vm.UpdatePromptVisible)
                    Failures.Add("the update dialog opened at start for the same version");

                // ---- Hidden games ----
                List<string> hide = [];
                Step("start: hide two games", async () =>
                {
                    vm!.CurrentStage = UiStage.Start;
                    Note("cards before: " + string.Join(", ", vm.GameCards.Select(c => c.GameId)) + "; installed count " + vm.InstalledGameCount);
                    foreach (string id in new[] { "WarThunder", "MSFS2020", "Arma3", "XPlane11" })
                    {
                        if (hide.Count < 2 && vm.GameCards.Any(c => c.GameId == id))
                            hide.Add(id);
                    }
                    foreach (GameCardItem card in vm.GameCards.Reverse())
                    {
                        if (hide.Count < 2 && !hide.Contains(card.GameId) && card.GameId is not "DCS" and not "IL2Game")
                            hide.Add(card.GameId);
                    }
                    foreach (string id in hide)
                        await vm.HideGameCardCommand.ExecuteAsync(vm.GameCards.Single(c => c.GameId == id));
                    vm.HiddenGamesExpanded = true;
                    Note("hidden: " + string.Join(", ", hide) + "; cards now: " + string.Join(", ", vm.GameCards.Select(c => c.GameId)) + "; installed count " + vm.InstalledGameCount +
                         "; " + vm.HiddenGamesTitle + "; status: " + vm.StatusText);
                    if (hide.Any(id => vm.GameCards.Any(c => c.GameId == id)) || vm.HiddenGameInstalls.Count(h => hide.Contains(h.GameId)) != hide.Count)
                        Failures.Add("hidden games still on the cards or not listed as hidden");
                }, extraRender: slug => RenderTall(slug, 3200));
                Step("start: show one again", async () =>
                {
                    GameInstallItem? back = vm!.HiddenGameInstalls.FirstOrDefault(h => hide.Count > 0 && h.GameId == hide[0]);
                    if (back != null)
                        await vm.ShowHiddenGameCommand.ExecuteAsync(back);
                    Note("shown again: " + back?.GameId + "; cards: " + vm.GameCards.Count + "; " + vm.HiddenGamesTitle + "; setup wizard games come from the cards");
                    if (back != null && vm.GameCards.All(c => c.GameId != back.GameId))
                        Failures.Add("shown game not back on the cards");
                }, extraRender: slug => RenderTall(slug, 3200));

                // ---- Settings ----
                Step("settings: first look (Games open)", () => { vm!.CurrentStage = UiStage.Settings; return Task.CompletedTask; });
                Step("settings: close all", () => { vm!.CollapseAllSettingsCommand.Execute(null); return Task.CompletedTask; });
                Step("settings: open all", () => { vm!.ExpandAllSettingsCommand.Execute(null); return Task.CompletedTask; }, extraRender: slug => RenderTall(slug, 9000));
                Step("settings: moved (About first, Devices second) and only those open", () =>
                {
                    vm!.CollapseAllSettingsCommand.Execute(null);
                    vm.MoveSettingsSectionToTopCommand.Execute(vm.FindSettingsSection("about"));
                    vm.MoveSettingsSection(vm.FindSettingsSection("devices")!, 1);
                    vm.MoveSettingsSectionUpCommand.Execute(vm.FindSettingsSection("confirmations"));
                    vm.FindSettingsSection("about")!.IsExpanded = true;
                    vm.FindSettingsSection("devices")!.IsExpanded = true;
                    Note("order: " + string.Join(", ", vm.SettingsSections.Select(x => x.Id)));
                    return Task.CompletedTask;
                }, extraRender: slug => RenderTall(slug, 2600));
                foreach (string search in new[] { "update", "tray", "photos", "zzz nothing" })
                {
                    Step("settings: search '" + search + "'", () =>
                    {
                        vm!.SettingsSearchText = search;
                        Note("matches: " + string.Join(", ", vm.SettingsSections.Where(x => x.MatchesSearch).Select(x => x.Id)) + "; " + vm.SettingsSearchStatus);
                        return Task.CompletedTask;
                    });
                }
                Step("settings: search cleared", () => { vm!.ClearSettingsSearchCommand.Execute(null); return Task.CompletedTask; });
                List<string> order = vm!.SettingsSections.Select(x => x.Id).ToList();
                List<string> open = vm.SettingsSections.Where(x => x.IsExpanded).Select(x => x.Id).ToList();
                Restart("restart: settings come back as left", async () =>
                {
                    await Task.CompletedTask;
                    vm!.CurrentStage = UiStage.Settings;
                    List<string> orderNow = vm.SettingsSections.Select(x => x.Id).ToList();
                    List<string> openNow = vm.SettingsSections.Where(x => x.IsExpanded).Select(x => x.Id).ToList();
                    Note("order after restart: " + string.Join(", ", orderNow) + "; open: " + string.Join(", ", openNow));
                    if (!orderNow.SequenceEqual(order) || !openNow.SequenceEqual(open))
                        Failures.Add("settings layout not kept over a restart");
                    List<string> hiddenNow = vm.HiddenGameInstalls.Select(h => h.GameId).ToList();
                    Note("hidden after restart: " + string.Join(", ", hiddenNow));
                });

                // ---- Version and About ----
                Step("about: from the version in the corner", () =>
                {
                    vm!.CurrentStage = UiStage.Start;
                    Note("corner: " + vm.VersionCornerText + " / " + vm.VersionCornerTip);
                    vm.OpenAboutCommand.Execute(null);
                    return Task.CompletedTask;
                });

                // ---- Updates ----
                foreach ((string name, string feed) in new[]
                         {
                             ("no release", Path.Combine(feeds, "none", "latest.json")), ("same version", same.FeedPath),
                             ("no internet", FakeReleaseFeed.UnreachableFeed()), ("newer version", newer.FeedPath),
                         })
                {
                    Step("updates: check, " + name, async () =>
                    {
                        Environment.SetEnvironmentVariable(UpdateService.FeedVariable, feed);
                        await vm!.CheckForUpdatesCommand.ExecuteAsync(null);
                        Note("dialog: " + vm.UpdatePromptVisible + " '" + vm.UpdatePromptTitle + "' " + vm.UpdatePromptMessage + " | offers update: " + vm.UpdatePromptOffersUpdate +
                             " | corner: " + vm.VersionCornerText);
                    });
                }
                Step("german: the update dialog and the settings in German", async () =>
                {
                    JoyPro.Gui.Localization.Loc.SetLanguage("de");
                    await vm!.CheckForUpdatesCommand.ExecuteAsync(null);
                    Note("dialog: '" + vm.UpdatePromptTitle + "' " + vm.UpdatePromptMessage + "; sections open: " + vm.SettingsSectionsSummary);
                });
                Step("updates: update now, bad checksum", async () =>
                {
                    JoyPro.Gui.Localization.Loc.SetLanguage("en");
                    Environment.SetEnvironmentVariable(UpdateService.FeedVariable, bad.FeedPath);
                    await vm!.CheckForUpdatesCommand.ExecuteAsync(null);
                    await vm.UpdateNowCommand.ExecuteAsync(null);
                    Note("dialog: '" + vm.UpdatePromptTitle + "' " + vm.UpdatePromptMessage);
                    if (!vm.UpdatePromptIsError)
                        Failures.Add("a bad checksum did not stop the update");
                });
                string staged = string.Empty;
                Step("updates: update now (download, check, install, restart)", async () =>
                {
                    Environment.SetEnvironmentVariable(UpdateService.FeedVariable, newer.FeedPath);
                    bool closed = false;
                    vm!.ShutdownRequested += () => closed = true;
                    vm.UpdateInstallerOverride = dir => staged = dir;
                    await vm.CheckForUpdatesCommand.ExecuteAsync(null);
                    await vm.UpdateNowCommand.ExecuteAsync(null);
                    Note("dialog: '" + vm.UpdatePromptTitle + "' " + vm.UpdatePromptMessage + "; staged " + staged + "; JoyPro asked to close: " + closed);
                    if (!closed || !File.Exists(Path.Combine(staged, "JoyPro.Gui.exe")))
                        Failures.Add("update now did not stage the package or did not close JoyPro");
                });
                Step("updates: newer again, Skip this version", async () =>
                {
                    vm!.UpdateInstallerOverride = dir => Note("update: the copy script would start now for " + dir);
                    await vm.CheckForUpdatesCommand.ExecuteAsync(null);
                    vm.SkipUpdateVersionCommand.Execute(null);
                    Note("skipped: " + vm.SkippedUpdateVersion + "; dialog " + vm.UpdatePromptVisible);
                });
                Restart("restart: the skipped version is not offered at start", async () =>
                {
                    await Task.CompletedTask;
                    Note("at start: dialog " + vm!.UpdatePromptVisible + "; status " + vm.UpdateStatus + "; corner " + vm.VersionCornerText);
                    if (vm.UpdatePromptVisible)
                        Failures.Add("the skipped version was offered at start");
                    vm.CurrentStage = UiStage.Settings;
                });
                Step("updates: check at start finds a newer version (dialog over the Start page)", async () =>
                {
                    vm!.ClearSkippedUpdateCommand.Execute(null);
                    vm.CurrentStage = UiStage.Start;
                    await vm.CheckForUpdatesCoreAsync(manual: false);
                    Note("dialog " + vm.UpdatePromptVisible + " '" + vm.UpdatePromptTitle + "'");
                });
                Step("updates: Later", () => { vm!.UpdateLaterCommand.Execute(null); return Task.CompletedTask; });
                Environment.SetEnvironmentVariable(UpdateService.FeedVariable, same.FeedPath);

                // ---- The Export page remembers ----
                Step("export: new profile with Pitch and Gear", async () =>
                {
                    await vm!.NewProfileAutoCommand.ExecuteAsync(null);
                    await CreateRelationAsync(vm, "Pitch", "pitch", axis: true);
                    await CreateRelationAsync(vm, "Gear", "gear", axis: false);
                    Note("profile " + vm.ProfilePath + ": " + vm.Relations.Count + " relations");
                });
                string unticked = string.Empty;
                bool wanted = false;
                Step("export: mode 'my profile wins', keyboard defaults off, one game's tick changed", async () =>
                {
                    vm!.CurrentStage = UiStage.Export;
                    await vm.RefreshExportGamesCommand.ExecuteAsync(null);
                    vm.ExportMode = "merge-overwrite";
                    vm.ExportKeepKeyboardDefaults = false;
                    ExportGameItem? tile = vm.ExportGames.FirstOrDefault(g => g.CanExport && g.GameId != "DCS") ?? vm.ExportGames.FirstOrDefault(g => g.CanExport);
                    if (tile != null)
                    {
                        wanted = !tile.IsSelected;
                        tile.IsSelected = wanted;
                        unticked = tile.GameId;
                    }
                    Note("tiles: " + string.Join(", ", vm.ExportGames.Select(g => g.GameId + (g.IsSelected ? "+" : "-"))) + "; remembered: " + vm.RememberedExportSummary());
                }, extraRender: slug => RenderTall(slug, 3600));
                Restart("restart: the export page as it was left", async () =>
                {
                    vm!.CurrentStage = UiStage.Export;
                    await vm.RefreshExportGamesCommand.ExecuteAsync(null);
                    ExportGameItem? tile = vm.ExportGames.FirstOrDefault(g => g.GameId == unticked);
                    Note("after restart: mode " + vm.ExportMode + ", keep keyboard " + vm.ExportKeepKeyboardDefaults + ", tiles " +
                         string.Join(", ", vm.ExportGames.Select(g => g.GameId + (g.IsSelected ? "+" : "-"))) + "; remembered: " + vm.RememberedExportSummary());
                    if (vm.ExportMode != "merge-overwrite" || vm.ExportKeepKeyboardDefaults || (unticked.Length > 0 && tile?.IsSelected != wanted))
                        Failures.Add("export settings not remembered over a restart");
                }, tall: 3600);
            }
            finally
            {
                Environment.SetEnvironmentVariable(UpdateService.FeedVariable, oldFeed);
                JoyPro.Gui.Localization.Loc.SetLanguage("en");
            }
        }

        // JoyPro closed and started again with the same data folder: the startup, the last profile, then the check.
        private void Restart(string name, Func<Task> check, int tall = 0)
        {
            if (vm != null)
            {
                AvaloniaRenderThread.Wait(SettleAsync(vm, 20000));
                AvaloniaRenderThread.Wait(vm.FlushAutosaveAsync());
                vm.StopGameConfigWatcher();
            }
            vm = CreateVm();
            Step(name, async () =>
            {
                await vm!.RunStartupChecksAsync();
                await vm.AutoOpenLastProfileOnStartupAsync();
                await check();
            }, extraRender: tall > 0 ? slug => RenderTall(slug, tall) : null);
        }

        private void DescribeCallouts()
        {
            JoyPro.DeviceReference.CalloutSheet? sheet = vm!.LayoutCallouts.Sheet;
            if (sheet == null)
            {
                Note("no callout sheet (LayoutShowsCallouts=" + vm.LayoutShowsCallouts + ")");
                return;
            }
            Note("callouts: combos [" + string.Join(" | ", sheet.Combos) + "]; choice " + vm.CalloutModifier + "; max " + vm.CalloutMaxColumns + "; " + vm.CalloutModifierInUse +
                 "; sheet " + (int)sheet.Width + "x" + (int)sheet.Height + "; check: " + JoyPro.DeviceReference.CalloutLayouter.Check(sheet));
        }

        // Bind on the picture as the user does it: the relation picked on the left, the input clicked on the picture
        // (its legend row), the modifier combination picked in the side panel, Bind here (no parameter, as the button).
        private void VisualAssign(string relation, string input, string combo)
        {
            string label = relation + " on " + input + (combo.Length > 0 ? " with " + combo : string.Empty);
            Step("visual assign: pick " + relation + ", click " + input + (combo.Length > 0 ? ", " + combo : string.Empty), () =>
            {
                vm!.SelectedBind = vm.Binds.FirstOrDefault(b => b.RelationName == relation);
                vm.DeviceReference.SelectedLegendRow = vm.ReferenceLegendItems.FirstOrDefault(r => r.Input == input);
                VisualAssignComboRow? row = vm.VisualAssignCombos.FirstOrDefault(c => string.Equals(c.Combo, combo, StringComparison.OrdinalIgnoreCase));
                if (row != null)
                    vm.SelectVisualAssignComboCommand.Execute(row);
                Note("before " + label + ": selected input " + vm.WorkspaceInputTitle + "; preview '" + vm.VisualAssignPreview + "'; state '" + vm.VisualAssignBoundState +
                     "'; list pick '" + vm.SelectedLayoutBindCandidate + "'; combos " + string.Join(" | ", vm.VisualAssignCombos.Select(c => c.Label + "=" + c.Display + (c.IsSelected ? "*" : string.Empty))));
                return Task.CompletedTask;
            });
            Step("visual assign: Bind here (" + label + ")", async () =>
            {
                await vm!.BindSelectedLayoutMarkerCommand.ExecuteAsync(null);
                BindListItem? bind = vm.Binds.FirstOrDefault(b => b.RelationName == relation);
                Note("after " + label + ": status '" + vm.StatusText + "'; panel '" + vm.VisualAssignStatus + "'; state '" + vm.VisualAssignBoundState + "'; bind " +
                     (bind == null ? "(none)" : bind.HasBind + " " + bind.InputLabel + " [" + bind.ModifiersSummary + "]") + "; header " + vm.ProfileBoundText +
                     "; legend " + vm.LayoutLegendSummary + "; flash " + (vm.WorkspaceFlashMarker != null));
                if (bind == null || !bind.HasBind || !string.Equals(bind.Input, input, StringComparison.OrdinalIgnoreCase) ||
                    !JoyPro.DeviceReference.CalloutCombos.Same(bind.ModifiersSummary, combo))
                    Failures.Add("visual assign did not bind " + label);
            });
        }

        // Settings > Telemetry recording: the section off, switched on with three recorded sessions (DCS with a
        // position, IL-2 without, X-Plane), the timeline of one, the Tacview and CSV exports, delete. The process manager
        // never looks at the PC's processes here (no game is recorded for real).
        private void RunTelemetryRecording()
        {
            FirstStart(answerSetup: "later");
            vm!.FfbFindGameProcess = _ => null;
            string recordings = Path.Combine(data, "Telemetry", "Recordings");
            Step("telemetry recording: the section, off", () =>
            {
                vm!.CurrentStage = UiStage.Settings;
                vm.CollapseAllSettingsCommand.Execute(null);
                vm.MoveSettingsSectionToTopCommand.Execute(vm.FindSettingsSection("telemetry-recording"));
                vm.FindSettingsSection("telemetry-recording")!.IsExpanded = true;
                Note("state: " + vm.TelemetryRecordingStateText + "; folder " + vm.TelemetryRecordingFolderText + "; sessions: " + vm.TelemetrySessionsSummary);
                if (vm.TelemetryRecordingEnabled)
                    Failures.Add("telemetry recording is on by default");
                return Task.CompletedTask;
            }, extraRender: slug => RenderTall(slug, 2400));
            Step("telemetry recording: on, three sessions", async () =>
            {
                TelemetryRecordingTests.WriteSession(recordings, "DCS", "F-16C_50", DateTime.UtcNow.AddHours(-26), seconds: 900);
                TelemetryRecordingTests.WriteSession(recordings, "IL2Game", "Bf 109 G-14", DateTime.UtcNow.AddHours(-5), seconds: 420, position: false);
                TelemetryRecordingTests.WriteSession(recordings, "XPlane12", "Cessna 172 SP", DateTime.UtcNow.AddMinutes(-40), seconds: 1800);
                vm!.TelemetryRecordingEnabled = true;
                await vm.RefreshTelemetrySessionsAsync();
                vm.SelectedTelemetrySession = vm.TelemetrySessions.FirstOrDefault(s => s.Info.Game == "DCS");
                await vm.LoadTelemetryPreviewAsync(vm.SelectedTelemetrySession);
                Note("state: " + vm.TelemetryRecordingStateText + "; " + vm.TelemetrySessionsSummary + "; rows: " +
                     string.Join(" | ", vm.TelemetrySessions.Select(s => s.Date + " " + s.Game + " " + s.Aircraft + " " + s.Duration + " " + s.Size)) +
                     "; preview: " + vm.TelemetryPreviewText + " (" + (vm.TelemetryPreviewTracks?.Count ?? 0) + " tracks)");
                if (vm.TelemetrySessions.Count != 3 || vm.TelemetryPreviewTracks is not { Count: > 0 })
                    Failures.Add("telemetry recording: sessions or preview missing");
                if (!File.Exists(Path.Combine(data, "Telemetry", "telemetry-recording.json")))
                    Failures.Add("telemetry recording: settings file not written");
            }, extraRender: slug =>
            {
                RenderTall(slug, 2400);
                RenderWindow(vm!, 1280, 2900, Path.Combine(folder, slug + "-tall-1280.png"));
            });
            Step("telemetry recording: export for Tacview and CSV", async () =>
            {
                string? acmi = await vm!.ExportSelectedTelemetryAsync("acmi-zip");
                string? csv = await vm.ExportSelectedTelemetryAsync("csv");
                Note("acmi: " + acmi + "; csv: " + csv + "; message: " + vm.TelemetryRecordingMessage);
                if (acmi == null || !File.Exists(acmi) || csv == null || !Directory.Exists(csv))
                    Failures.Add("telemetry recording: export failed: " + vm.TelemetryRecordingMessage);
            }, extraRender: slug => RenderTall(slug, 2400));
            Step("telemetry recording: an IL-2 session (no Tacview export)", async () =>
            {
                vm!.SelectedTelemetrySession = vm.TelemetrySessions.FirstOrDefault(s => s.Info.Game == "IL2Game");
                await vm.LoadTelemetryPreviewAsync(vm.SelectedTelemetrySession);
                Note("ACMI possible: " + vm.CanExportSelectedTelemetryAcmi + "; preview " + vm.TelemetryPreviewText);
                if (vm.CanExportSelectedTelemetryAcmi)
                    Failures.Add("telemetry recording: ACMI offered without a position");
            });
            Step("telemetry recording: delete it", async () =>
            {
                await vm!.DeleteTelemetrySessionAsync();
                Note("after delete: " + vm.TelemetrySessionsSummary + "; " + vm.TelemetryRecordingMessage);
                if (vm.TelemetrySessions.Count != 2)
                    Failures.Add("telemetry recording: delete did not remove the session");
            }, extraRender: slug => RenderTall(slug, 2400));
            Step("telemetry recording: switched off again", () =>
            {
                vm!.TelemetryRecordingEnabled = false;
                Note("state: " + vm.TelemetryRecordingStateText);
                return Task.CompletedTask;
            });
        }

        private static (string, UiStage)[] AllPages() =>
        [
            ("start", UiStage.Start), ("relations", UiStage.Relations), ("binds", UiStage.Binds), ("modifiers", UiStage.Modifiers),
            ("layout", UiStage.Layout), ("hud", UiStage.Hud), ("export", UiStage.Export), ("settings", UiStage.Settings),
            ("cockpits", UiStage.Cockpits), ("virtual joystick", UiStage.VirtualJoystick), ("ffb telemetry", UiStage.FfbTelemetry),
        ];

        // ---- building blocks ----

        private MainWindowViewModel CreateVm()
        {
            // Persisted settings as for a real user: they live in <data>\settings (JOYPRO_DATA_DIR), so the first start
            // shows the game setup like on a new PC.
            MainWindowViewModel created = new(cli, new AxisTuningFakeCapture(), loadPersistedUiSettings: true)
            {
                EnginePath = engine,
                OverlayPresenter = new NoOverlay(),
                AutoImportOldLayouts = false,
            };
            created.ConfirmHandler = Confirm;
            // Never the real copy script: it would copy a test package over this test run's program folder.
            created.UpdateInstallerOverride = staged => Note("update: the copy script would start now for " + staged);
            return created;
        }

        private Task<bool> Confirm(ConfirmRequest request)
        {
            Note("confirmation asked: " + request.Title + " / " + request.Message);
            return Task.FromResult(true);
        }

        private void FirstStart(string answerSetup)
        {
            vm = CreateVm();
            bool setupAsked = false;
            vm.GameSetupRequested += () => setupAsked = true;
            Step("first start", async () =>
            {
                await vm!.RunStartupChecksAsync();
                await vm.AutoOpenLastProfileOnStartupAsync();
            });
            Note("game setup offered: " + setupAsked + "; status: " + vm.StatusText);
            if (!setupAsked)
                return;
            RenderDialog("first-start game setup", new GameSetupWindow(), 1040, 780);
            RenderDialog("first-start game setup (large)", new GameSetupWindow(), 1040, 1250);
            if (answerSetup == "done")
            {
                Step("game setup: Done", async () =>
                {
                    bool ok = await vm!.ApplyGameSetupAsync();
                    Note("ApplyGameSetupAsync: " + ok + " " + vm.GameSetupStatus + " / " + vm.StatusText);
                });
            }
            else
            {
                Step("game setup: Later", () => { vm!.SkipGameSetup(); return Task.CompletedTask; });
            }
        }

        // Export targets of the journey: the games' config locations point at empty temp folders (data folder only).
        private void RedirectGameConfigs()
        {
            // X-Plane and IL-2 keep their controls in the installation (config set refuses them): IL-2 exports with
            // ExportIl2Path, the registry games only as a dry run.
            foreach (string game in new[] { "DCS", "MSFS2024", "MSFS2020", "WarThunder", "StarCitizen" })
            {
                string target = Path.Combine(gameCopies, game);
                Directory.CreateDirectory(target);
                EngineCommandResult result = AvaloniaRenderThread.Wait(cli.RunDirectAsync(engine, ["games", "config", "set", game, target]));
                Note("games config set " + game + " -> exit " + result.ExitCode + " " + Short(result.StandardError + result.StandardOutput, 200));
            }
            AvaloniaRenderThread.Wait(vm!.RefreshGameInstallsCommand.ExecuteAsync(null));
            Note("ExportDcsPath after redirect: " + vm.ExportDcsPath);
        }

        private async Task CreateRelationAsync(MainWindowViewModel target, string name, string query, bool axis)
        {
            target.CurrentStage = UiStage.Relations;
            target.StartNewRelationCommand.Execute(null);
            target.RelationNameInput = name;
            target.SetBuilderKindCommand.Execute(axis ? "axis" : "button");
            target.BuilderSearchText = query;
            await target.SearchBuilderActionsCommand.ExecuteAsync(null);
            await WaitForAsync(() => !target.IsBuilderSearching, 30000);
            foreach (string game in JourneyGames)
            {
                BuilderSearchResultItem? hit = target.BuilderSearchResults.FirstOrDefault(r => r.Game == game && r.IsAxis == axis && r.CanCheck);
                if (hit != null)
                    hit.IsChecked = true;
            }
            await target.AddCheckedBuilderResultsCommand.ExecuteAsync(null);
            await SettleAsync(target, 30000);
        }

        private void DescribeSearch()
        {
            List<BuilderSearchResultItem> results = vm!.BuilderSearchResults.ToList();
            Note("search '" + vm.BuilderSearchText + "': " + results.Count + " rows; status: " + vm.BuilderSearchStatus + "; games: " +
                 string.Join(", ", results.GroupBy(r => r.Game).Select(g => g.Key + "=" + g.Count())));
            foreach (string game in JourneyGames)
            {
                List<BuilderSearchResultItem> top = results.Where(r => r.Game == game).Take(3).ToList();
                Note("  top " + game + ": " + (top.Count == 0 ? "(none)" : string.Join(" | ", top.Select(r => r.Title + " [" + r.KindLabel + ", " + r.Id + "]"))));
            }
        }

        private void TickPerGame(bool axis)
        {
            List<string> ticked = [];
            foreach (string game in JourneyGames)
            {
                BuilderSearchResultItem? hit = vm!.BuilderSearchResults.FirstOrDefault(r => r.Game == game && r.IsAxis == axis && r.CanCheck);
                if (hit == null)
                {
                    Note("  no " + (axis ? "axis" : "button") + " row for " + game);
                    continue;
                }
                hit.IsChecked = true;
                ticked.Add(game + ": " + hit.Title);
            }
            Note("ticked: " + string.Join(" | ", ticked) + "; button label: " + vm!.BuilderAddButtonLabel);
        }

        // Detent mode with the user's own profile (a copy): one throttle relation per afterburner start becomes one
        // relation per throttle axis, the wizard calibrates the detent (positions fed as the capture service would), the
        // groups show what each aircraft gets, and undo takes the merge back.
        private void RunDetent()
        {
            string source = JourneyInput("JOYPRO_JOURNEY_PROFILE", "detent", "Holdi-2025-03.pr0file");
            if (!File.Exists(source))
            {
                Failures.Add("detent: no profile at " + source);
                return;
            }
            string profile = Path.Combine(folder, "profiles", "detent.pr0file");
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            File.Copy(source, profile, overwrite: true);
            vm = CreateVm();
            Step("detent: open a copy of the March 2025 profile", async () =>
            {
                vm!.ProfilePath = profile;
                await vm.RefreshRelationsCommand.ExecuteAsync(null);
                await vm.RefreshBindsCommand.ExecuteAsync(null);
                vm.CurrentStage = UiStage.Binds;
            });
            Step("detent: ThrottleLeft, open detent mode", async () =>
            {
                vm!.SelectedBind = vm.Binds.FirstOrDefault(b => b.RelationName == "ThrottleLeft");
                vm.IsDetentExpanded = true;
                await Task.Delay(200);
                Note("detent status: " + vm.DetentStatus + " | " + vm.DetentCalibrationText + " | groups " + vm.DetentGroups.Count);
            }, extraRender: slug => RenderTall(slug, 3600));
            Step("detent: merge preview", async () =>
            {
                await vm!.OpenDetentMergeCommand.ExecuteAsync(null);
                foreach (DetentMergeProposalItem item in vm.DetentMergeProposals)
                    Note("proposal: " + item.Heading + " | " + item.CalibrationText + " | " + item.GroupsText.Replace("\n", " / "));
            }, extraRender: slug => RenderTall(slug, 4200));
            Step("detent: merge the JOY_RX throttle relations", async () =>
            {
                DetentMergeProposalItem? rx = vm!.DetentMergeProposals.FirstOrDefault(item => item.DeviceLabel.EndsWith("JOY_RX", StringComparison.Ordinal));
                if (rx == null)
                {
                    Failures.Add("detent: no JOY_RX proposal");
                    return;
                }
                await vm.ApplyDetentMergeCommand.ExecuteAsync(rx);
                Note("after merge: " + vm.DetentStatus + " | groups: " + string.Join(" / ", vm.DetentGroups.Select(g => g.Heading)));
            }, extraRender: slug => RenderTall(slug, 4200));
            Step("detent: wizard step 1, throttle at idle", () =>
            {
                vm!.IsDetentMergeOpen = false;
                vm.StartDetentWizardCommand.Execute(null);
                FeedThrottle(0.015);
                return Task.CompletedTask;
            }, extraRender: slug => RenderTall(slug, 3600));
            Step("detent: wizard step 2, throttle in the detent", () =>
            {
                vm!.ConfirmDetentWizardStepCommand.Execute(null);
                FeedThrottle(0.79);
                return Task.CompletedTask;
            }, extraRender: slug => RenderTall(slug, 3600));
            Step("detent: wizard step 3, full", () =>
            {
                vm!.ConfirmDetentWizardStepCommand.Execute(null);
                FeedThrottle(1.0);
                return Task.CompletedTask;
            }, extraRender: slug => RenderTall(slug, 3600));
            Step("detent: wizard step 4, cut-off", () =>
            {
                vm!.ConfirmDetentWizardStepCommand.Execute(null);
                return Task.CompletedTask;
            }, extraRender: slug => RenderTall(slug, 3600));
            Step("detent: wizard summary (no cut-off: skip)", () =>
            {
                vm!.SkipDetentWizardStepCommand.Execute(null);
                Note("summary: " + vm.DetentWizardStepText);
                return Task.CompletedTask;
            }, extraRender: slug => RenderTall(slug, 3600));
            Step("detent: save the calibration, groups", async () =>
            {
                await vm!.SaveDetentWizardCommand.ExecuteAsync(null);
                Note("after save: " + vm.DetentCalibrationText + " | groups: " + string.Join(" / ", vm.DetentGroups.Select(g => g.Heading + " [" + g.GamesText.Replace("\n", "; ") + "]")));
            }, extraRender: slug =>
            {
                RenderTall(slug, 4200);
                RenderWindow(vm!, 1280, 5200, Path.Combine(folder, slug + "-tall-1280.png"));
            });
            Step("detent: undo the merge", async () =>
            {
                vm!.IsDetentMergeOpen = true;
                await vm.UndoDetentMergeCommand.ExecuteAsync(null);
                Note("after undo: throttle relations: " + string.Join(", ", vm.Relations.Where(r => r.Name.StartsWith("Throttle", StringComparison.Ordinal)).Select(r => r.Name)));
            }, extraRender: slug => RenderTall(slug, 3600));
        }

        private void FeedThrottle(double rx)
        {
            BindListItem? bind = vm!.SelectedBind;
            if (bind == null)
                return;
            double[] axes = new double[8];
            axes[3] = rx;
            vm.OnDetentAxes(new JoystickAxisSnapshot(bind.Device, axes));
        }

        // Binds page, "Pick the input by hand": device and input from the lists, Set Bind.
        private void BindByHand(string relation, bool axis)
        {
            Step("binds: select " + relation, () =>
            {
                vm!.SelectedBind = vm.Binds.FirstOrDefault(b => b.RelationName == relation);
                Note("selected bind: " + (vm.SelectedBind?.RelationName ?? "(none: no row for " + relation + ")"));
                return Task.CompletedTask;
            });
            if (vm!.SelectedBind == null)
                return;
            Step("binds: pick by hand for " + relation, async () =>
            {
                vm!.ShowQuickBindActions = true;
                if (vm.BindDeviceChoices.Count == 0)
                    await vm.RefreshQuickBindOptionsCommand.ExecuteAsync(null);
                Note("device options: " + string.Join(" | ", vm.BindDeviceChoices.Select(c => c.Label)));
                BindDeviceChoice? device = vm.BindDeviceChoices.FirstOrDefault(d => d.Label.Contains(axis ? "WarBRD" : "Throttle", StringComparison.OrdinalIgnoreCase))
                                           ?? vm.BindDeviceChoices.FirstOrDefault(d => !d.IsKeyboard);
                if (device == null)
                {
                    Note("no device to pick");
                    return;
                }
                vm.SelectedBindDeviceChoice = device;
                await Task.Delay(300);
                Note("input options for " + device.Label + ": " + string.Join(" ", vm.BindInputChoices.Take(40)) + (vm.BindInputChoices.Count > 40 ? " ..." : string.Empty));
                string? input = axis
                    ? vm.BindInputChoices.FirstOrDefault(i => i == "JOY_Y") ?? vm.BindInputChoices.FirstOrDefault()
                    : vm.BindInputChoices.FirstOrDefault(i => i == "JOY_BTN5") ?? vm.BindInputChoices.FirstOrDefault();
                vm.BindInputValue = input ?? (axis ? "JOY_Y" : "JOY_BTN5");
            });
            Step("binds: Set Bind for " + relation, async () =>
            {
                await vm!.SetSelectedBindCommand.ExecuteAsync(null);
                BindListItem? row = vm.Binds.FirstOrDefault(b => b.RelationName == relation);
                Note("after Set Bind: " + relation + " bound=" + (row?.HasBind ?? false) + " device=" + row?.Device + " input=" + row?.InputLabel + "; status: " + vm.StatusText +
                     "; header: " + vm.ProfileBoundText);
            }, extraRender: slug => RenderTall(slug, 2600));
        }

        // ---- steps, waiting, rendering ----

        private void Step(string name, Func<Task> action, Action<string>? extraRender = null)
        {
            stepNumber++;
            string slug = stepNumber.ToString("D2") + "-" + Slug(name);
            int callsBefore = cli.Calls.Count;
            int logBefore = logSink.Count;
            Stopwatch sw = Stopwatch.StartNew();
            string error = string.Empty;
            try
            {
                AvaloniaRenderThread.Wait(action());
                AvaloniaRenderThread.Wait(SettleAsync(vm!, 90000));
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                Failures.Add(slug + ": " + error);
            }
            sw.Stop();
            report.AppendLine("## " + slug + " (" + sw.ElapsedMilliseconds + " ms" + (sw.ElapsedMilliseconds > 2000 ? ", SLOW" : string.Empty) + ")").AppendLine();
            if (error.Length > 0)
                report.AppendLine("**STEP FAILED**: " + error).AppendLine();
            report.AppendLine("- page: " + vm!.CurrentStage + "; status: `" + vm.StatusText + "`; busy: " + vm.IsBusy + "; profile: " + (vm.HasProfile ? Path.GetFileName(vm.ProfilePath) : "(none)"));
            List<EngineCall> calls = cli.Calls.Skip(callsBefore).ToList();
            foreach (EngineCall call in calls)
                report.AppendLine("- engine " + call.Describe());
            foreach (string line in logSink.Since(logBefore).Distinct().Take(15))
                report.AppendLine("- avalonia: " + line);
            foreach ((int width, int height) in renderSteps ? Sizes : [])
            {
                string file = Path.Combine(folder, slug + "-" + width + ".png");
                try
                {
                    List<string> problems = RenderWindow(vm, width, height, file);
                    report.AppendLine("- render " + Path.GetFileName(file) + (problems.Count == 0 ? string.Empty : ": " + problems.Count + " text problems"));
                    foreach (string problem in problems.Take(25))
                        report.AppendLine("  - " + problem);
                }
                catch (Exception ex)
                {
                    Failures.Add(slug + " render " + width + ": " + ex.Message);
                    report.AppendLine("- render " + width + " FAILED: " + ex);
                }
            }
            extraRender?.Invoke(slug);
            report.AppendLine();
        }

        // The whole page at once (what the user finds after scrolling), e.g. the export tiles below the modes.
        private void RenderTall(string slug, int height)
        {
            try
            {
                RenderWindow(vm!, Sizes[0].Width, height, Path.Combine(folder, slug + "-tall.png"));
            }
            catch (Exception ex)
            {
                report.AppendLine("- tall render FAILED: " + ex.Message);
            }
        }

        // An input file of an opt-in scenario: the environment variable, else the file below JOYPRO_WORK_DIR (the
        // maintainer's work folder), else "(set <variable>)", which never exists.
        private static string JourneyInput(string variable, params string[] underWorkDir)
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } path)
                return path;
            if (Environment.GetEnvironmentVariable("JOYPRO_WORK_DIR") is { Length: > 0 } work)
                return Path.Combine(new[] { work }.Concat(underWorkDir).ToArray());
            return "(set " + variable + " or JOYPRO_WORK_DIR)";
        }

        private void Note(string text) => report.AppendLine("> " + text.Replace("\n", " ").Replace("\r", string.Empty)).AppendLine();

        private void RenderSetupWizard(string slug)
        {
            if (vm?.SetupWizard == null)
            {
                Note("no setup wizard view model");
                return;
            }
            SetupWizardWindow window = new() { DataContext = vm.SetupWizard };
            RenderControl(window, 1400, 900, Path.Combine(folder, slug + "-wizard.png"));
        }

        private void RenderDialog(string name, Window window, int width, int height)
        {
            stepNumber++;
            string slug = stepNumber.ToString("D2") + "-" + Slug(name);
            window.DataContext = vm;
            List<string> problems = RenderControl(window, width, height, Path.Combine(folder, slug + ".png"));
            report.AppendLine("## " + slug + " (dialog " + width + "x" + height + ")").AppendLine();
            foreach (string problem in problems.Take(25))
                report.AppendLine("  - " + problem);
            report.AppendLine();
            window.DataContext = null;
        }

        private List<string> RenderWindow(MainWindowViewModel model, int width, int height, string file)
        {
            MainWindow main = new() { DataContext = model, Width = width, Height = height };
            try
            {
                return RenderControl(main, width, height, file);
            }
            finally
            {
                // The offscreen window lets go of the view model (events, the confirmation handler).
                main.DataContext = null;
                model.ConfirmHandler = Confirm;
            }
        }

        private static List<string> RenderControl(Window window, int width, int height, string file)
        {
            window.Width = width;
            window.Height = height;
            Size size = new(width, height);
            Control page = (Control)window.Content!;
            // Offscreen, the virtualizing lists do not learn their viewport: they show their rows in a plain panel. A list
            // with its own panel (the export modes' WrapPanel) keeps it, as the user sees it.
            foreach (ListBox list in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<ListBox>())
            {
                list.AutoScrollToSelectedItem = false;
                if (list.ItemsPanel?.Build() is VirtualizingPanel)
                    list.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel());
            }
            for (int pass = 0; pass < 6; pass++)
            {
                window.Measure(size);
                window.Arrange(new Rect(size));
                page.Measure(size);
                page.Arrange(new Rect(size));
                AvaloniaRenderThread.Pump();
            }
            IBrush background = window.TryFindResource("JP.PageBackgroundBrush", window.ActualThemeVariant, out object? found) && found is IBrush own
                ? own
                : new SolidColorBrush(Color.Parse("#202020"));
            using RenderTargetBitmap content = new(new PixelSize(width, height));
            content.Render(page);
            using RenderTargetBitmap bitmap = new(new PixelSize(width, height));
            using (DrawingContext context = bitmap.CreateDrawingContext())
            {
                context.FillRectangle(background, new Rect(size));
                context.DrawImage(content, new Rect(size), new Rect(size));
            }
            bitmap.Save(file);
            return FindTextProblems(page, size);
        }

        // Texts that overlap another text, and texts cut off with an ellipsis, as the user sees them (clipped by their
        // scroll viewers and panels).
        private static List<string> FindTextProblems(Control root, Size size)
        {
            List<(TextBlock Block, Rect Rect, string Text)> texts = [];
            List<string> problems = [];
            foreach (TextBlock block in root.GetVisualDescendants().OfType<TextBlock>())
            {
                string text = block.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text) || !block.IsEffectivelyVisible || block.Bounds.Width < 1 || block.Bounds.Height < 1)
                    continue;
                if (block.GetVisualAncestors().OfType<Visual>().Any(a => a.Opacity < 0.05))
                    continue;
                Point? origin = block.TranslatePoint(new Point(0, 0), root);
                if (origin == null)
                    continue;
                double textWidth = block.Bounds.Width;
                try
                {
                    if (block.TextAlignment is TextAlignment.Left or TextAlignment.Start)
                        textWidth = Math.Min(textWidth, block.TextLayout.WidthIncludingTrailingWhitespace + block.Padding.Left + block.Padding.Right);
                }
                catch (Exception)
                {
                }
                Rect rect = new(origin.Value, new Size(textWidth, block.Bounds.Height));
                foreach (Visual ancestor in block.GetVisualAncestors())
                {
                    if (!ancestor.ClipToBounds)
                        continue;
                    Point? a = ancestor.TranslatePoint(new Point(0, 0), root);
                    if (a == null)
                        continue;
                    rect = rect.Intersect(new Rect(a.Value, ancestor.Bounds.Size));
                }
                rect = rect.Intersect(new Rect(size));
                if (rect.Width < 2 || rect.Height < 2)
                    continue;
                texts.Add((block, rect, text));
                try
                {
                    if (block.TextLayout.TextLines.Any(l => l.HasCollapsed))
                        problems.Add("cut off: \"" + Short(text, 90) + "\" at " + Describe(rect));
                }
                catch (Exception)
                {
                }
            }
            for (int i = 0; i < texts.Count; i++)
            {
                for (int j = i + 1; j < texts.Count; j++)
                {
                    Rect overlap = texts[i].Rect.Intersect(texts[j].Rect);
                    if (overlap.Width < 4 || overlap.Height < 4)
                        continue;
                    if (texts[i].Block.GetVisualAncestors().Contains(texts[j].Block) || texts[j].Block.GetVisualAncestors().Contains(texts[i].Block))
                        continue;
                    problems.Add("overlap: \"" + Short(texts[i].Text, 60) + "\" " + Describe(texts[i].Rect) + " x \"" + Short(texts[j].Text, 60) + "\" " + Describe(texts[j].Rect));
                }
            }
            return problems;
        }

        private static string Describe(Rect r) => "(" + (int)r.X + "," + (int)r.Y + " " + (int)r.Width + "x" + (int)r.Height + ")";

        private static async Task WaitForAsync(Func<bool> condition, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
                await Task.Delay(25);
        }

        // Idle: no engine call running, nothing busy, for 400 ms in a row.
        private async Task SettleAsync(MainWindowViewModel model, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            int idle = 0;
            await Task.Delay(50);
            while (idle < 8 && sw.ElapsedMilliseconds < timeoutMs)
            {
                bool busy = model.IsBusy || model.IsBuilderSearching || model.IsBuilderSuggestLoading || model.IsAssistantRunning || cli.Running > 0;
                idle = busy ? 0 : idle + 1;
                await Task.Delay(50);
            }
            if (idle < 8)
                report.AppendLine("- still busy after " + timeoutMs + " ms (engine calls running: " + cli.Running + ")");
        }

        public void WriteReport()
        {
            StringBuilder all = new();
            all.Append(report);
            all.AppendLine("## All engine calls").AppendLine();
            foreach (EngineCall call in cli.Calls)
                all.AppendLine("- " + call.Describe());
            all.AppendLine().AppendLine("## Failures").AppendLine();
            foreach (string failure in Failures)
                all.AppendLine("- " + failure);
            File.WriteAllText(Path.Combine(folder, "journey.md"), all.ToString());
        }

        private static string Slug(string text)
        {
            StringBuilder sb = new();
            foreach (char c in text.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            string slug = sb.ToString();
            while (slug.Contains("--"))
                slug = slug.Replace("--", "-");
            slug = slug.Trim('-');
            return slug.Length > 60 ? slug[..60] : slug;
        }
    }

    private static string Short(string? text, int max)
    {
        string value = (text ?? string.Empty).Replace("\r", string.Empty).Replace("\n", " ").Trim();
        return value.Length <= max ? value : value[..max] + "...";
    }

    private sealed record EngineCall(string Args, int ExitCode, long Ms, string Error, bool Blocked)
    {
        public string Describe() =>
            (Blocked ? "BLOCKED " : string.Empty) + "`" + Short(Args, 220) + "` -> " + ExitCode + " in " + Ms + " ms" + (Ms > 2000 ? " (SLOW)" : string.Empty) +
            (Error.Length > 0 ? " stderr: " + Short(Error, 300) : string.Empty);
    }

    // The real engine, every call recorded; game writes are refused.
    private sealed class JourneyEngine(string safeRoot) : IEngineCliClient
    {
        private static readonly HashSet<string> WriteVerbs = new(StringComparer.OrdinalIgnoreCase)
        {
            "export", "restore", "refresh", "refresh-db", "refresh-il2", "refresh-starcitizen", "nuke", "defaults", "mod", "undo", "prepare",
            "install-byoj", "hidhide",
        };

        private readonly EngineCliClient inner = new();
        private readonly ConcurrentQueue<EngineCall> calls = new();
        private int running;

        public IReadOnlyList<EngineCall> Calls => calls.ToArray();

        public int Running => Volatile.Read(ref running);

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            RunGuardedAsync(arguments, () => inner.RunAsync(engineExePath, arguments, cancellationToken));

        public Task<EngineCommandResult> RunStreamingAsync(string engineExePath, IReadOnlyList<string> arguments, EngineRunOptions options, CancellationToken cancellationToken = default) =>
            RunGuardedAsync(arguments, () => inner.RunStreamingAsync(engineExePath, arguments, options, cancellationToken));

        // Calls of the journey itself (not of the GUI).
        public Task<EngineCommandResult> RunDirectAsync(string engineExePath, IReadOnlyList<string> arguments) =>
            RunGuardedAsync(arguments, () => inner.RunAsync(engineExePath, arguments));

        private async Task<EngineCommandResult> RunGuardedAsync(IReadOnlyList<string> arguments, Func<Task<EngineCommandResult>> run)
        {
            string joined = string.Join(" ", WithoutGlobalOptions(arguments).Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
            string? refusal = Refusal(arguments);
            if (refusal != null)
            {
                calls.Enqueue(new EngineCall(joined, 99, 0, refusal, Blocked: true));
                return new EngineCommandResult { ExitCode = 99, StandardOutput = string.Empty, StandardError = "Journey guard: " + refusal, Arguments = arguments.ToArray() };
            }
            Interlocked.Increment(ref running);
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                EngineCommandResult result = await run();
                calls.Enqueue(new EngineCall(joined, result.ExitCode, sw.ElapsedMilliseconds, result.ExitCode == 0 ? string.Empty : ErrorText(result), Blocked: false));
                return result;
            }
            catch (Exception ex)
            {
                calls.Enqueue(new EngineCall(joined, -1, sw.ElapsedMilliseconds, ex.Message, Blocked: false));
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }

        // What the user would read: stderr without the live status lines, then the output.
        private static string ErrorText(EngineCommandResult result)
        {
            IEnumerable<string> lines = ((result.StandardError ?? string.Empty) + "\n" + (result.StandardOutput ?? string.Empty)).Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("[engine-status]", StringComparison.Ordinal) && !l.StartsWith("[engine-progress]", StringComparison.Ordinal));
            return string.Join(" | ", lines);
        }

        // The GUI's options on every call (--status, --load-corrections ...) hide the command in the report.
        private static readonly HashSet<string> GlobalOptions = new(StringComparer.Ordinal)
        {
            "--status", "--benchmark", "--load-corrections", "--auto-add-db-items", "--remap-missing-devices", "--confirm-device-remap",
            "--correction-mode", "--confirm-corrections", "--profile", "--history-note",
        };

        private static IEnumerable<string> WithoutGlobalOptions(IReadOnlyList<string> args)
        {
            for (int i = 0; i < args.Count; i++)
            {
                if (GlobalOptions.Contains(args[i]))
                {
                    i++;
                    continue;
                }
                yield return args[i];
            }
        }

        private string? Refusal(IReadOnlyList<string> arguments)
        {
            List<string> verbs = arguments.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
            string? write = verbs.FirstOrDefault(v => WriteVerbs.Contains(v));
            bool ffbWrite = verbs.Contains("ffb", StringComparer.OrdinalIgnoreCase) && verbs.Any(v => v is "set" or "clear" or "apply");
            // The DCS install options: only status, list and dry runs may run.
            bool installWrite = verbs.Any(v => v is "strip-defaults" or "device-luas") && !verbs.Any(v => v is "status" or "list");
            if (installWrite)
                write ??= verbs.First(v => v is "strip-defaults" or "device-luas");
            if (write == null && !ffbWrite)
                return null;
            if (arguments.Contains("--dry-run") && !arguments.Any(a => a == "false"))
                return null;
            // relations/binds export write a file of the user's choice: allowed into the journey folder.
            int path = IndexOf(arguments, "--path");
            int file = IndexOf(arguments, "--file");
            int at = path >= 0 ? path : file;
            if (string.Equals(write, "export", StringComparison.OrdinalIgnoreCase) && at >= 0 && at + 1 < arguments.Count &&
                Path.GetFullPath(arguments[at + 1]).StartsWith(Path.GetFullPath(safeRoot), StringComparison.OrdinalIgnoreCase))
                return null;
            return "'" + string.Join(" ", verbs) + "' would write game files outside the journey folder";
        }

        private static int IndexOf(IReadOnlyList<string> args, string name)
        {
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] == name)
                    return i;
            }
            return -1;
        }
    }

    private sealed class JourneyLogSink : ILogSink
    {
        private readonly ConcurrentQueue<string> lines = new();

        public int Count => lines.Count;

        public IEnumerable<string> Since(int index) => lines.Skip(index);

        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Add(level, area, source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Add(level, area, source, messageTemplate, propertyValues);

        private void Add(LogEventLevel level, string area, object? source, string template, object?[] values)
        {
            if (lines.Count > 5000)
                return;
            string text = template;
            foreach (object? value in values)
            {
                int open = text.IndexOf('{');
                int close = open >= 0 ? text.IndexOf('}', open) : -1;
                if (open < 0 || close < 0)
                    break;
                text = text[..open] + value + text[(close + 1)..];
            }
            lines.Enqueue(level + " " + area + " (" + source?.GetType().Name + "): " + Short(text, 300));
        }
    }

    private sealed class NoOverlay : IOverlayPresenter
    {
        public void Show(object dataContext)
        {
        }

        public void Hide()
        {
        }
    }
}

// A stand-in for Wikimedia in the journey: the first request of every photo gets "429 too many requests", the next one
// a picture (a cockpit-like pattern drawn here, so nothing is downloaded).
internal sealed class JourneyPhotoServer : System.Net.Http.HttpMessageHandler
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> seen = new();

    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
    {
        string url = request.RequestUri?.ToString() ?? string.Empty;
        int n = seen.AddOrUpdate(url, 1, (_, c) => c + 1);
        if (n == 1)
            return Task.FromResult(new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)429));
        int w = 480, h = 300, seed = url.Length;
        byte[] rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                bool screen = (x / 80 + seed) % 3 == 0 && y % 100 is > 20 and < 80;
                bool knob = (x % 40 - 20) * (x % 40 - 20) + (y % 50 - 25) * (y % 50 - 25) < 30;
                byte v = (byte)(screen ? 30 : knob ? 220 : 60 + (x + y + seed) % 40);
                rgba[o] = v;
                rgba[o + 1] = (byte)(screen ? 120 : v);
                rgba[o + 2] = (byte)(screen ? 60 : v + 10);
                rgba[o + 3] = 255;
            }
        }
        System.Net.Http.HttpResponseMessage ok = new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(JoyPro.CockpitPictures.PngWriter.Encode(rgba, w, h, 4)) };
        ok.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return Task.FromResult(ok);
    }
}
