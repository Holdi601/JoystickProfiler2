using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The novice paths of the GUI (docs/gui-cli-parity.md) with a fake engine: the Start page's game cards and import
// wizard, "Update" (command list refresh) and "Undo export" per game, the export modes in plain words, confirmations
// before actions that change game files or the profile, and device pickers. Nothing touches the user's files:
// TestEnvironment points JOYPRO_DATA_DIR at a temp folder and the engine is a fake.
// The render test writes PNGs only when JOYPRO_GUI_PARITY_PREVIEW names a folder (run it alone):
//   set JOYPRO_GUI_PARITY_PREVIEW=%TEMP%\JoyPro2.Gui.Tests\gui-parity & dotnet test gui/JoyPro.Gui.Tests --filter RenderNovicePages
[SupportedOSPlatform("windows")]
public sealed class NoviceGuiTests
{
    [Fact]
    public async Task GameCards_FollowInstallsAndIntegrations()
    {
        NoviceFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        await vm.CheckDbUpdatesCommand.ExecuteAsync(null);

        // Installed first; the hidden game (Disabled) has no card; a registry game gets its engine command name.
        Assert.Equal(["DCS World", "War Thunder", "IL-2 Sturmovik: Great Battles"], vm.GameCards.Select(c => c.Name).ToArray());
        GameCardItem dcs = vm.GameCards.Single(c => c.GameId == "DCS");
        Assert.Equal("dcs", dcs.Kind);
        Assert.True(dcs.NeedsDbUpdate);
        GameCardItem warThunder = vm.GameCards.Single(c => c.GameId == "WarThunder");
        Assert.Equal("registry", warThunder.Kind);
        Assert.Equal("warthunder", warThunder.CommandName);
        Assert.True(warThunder.CanRefreshNow);
        Assert.False(vm.GameCards.Single(c => c.GameId == "IL2Game").CanImport);
    }

    [Fact]
    public async Task ImportWizard_AddsToProfileByDefault_AndShowsTheResult()
    {
        NoviceFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        vm.CurrentStage = UiStage.Start;

        GameCardItem warThunder = vm.GameCards.Single(c => c.GameId == "WarThunder");
        vm.StartImportWizardCommand.Execute(warThunder);
        Assert.True(vm.IsImportWizardOptionsStep);
        Assert.True(vm.ImportWizardAddToProfile);

        vm.ImportWizardKeyboard = true;
        fake.Calls.Clear();
        await vm.RunImportWizardCommand.ExecuteAsync(null);

        IReadOnlyList<string> import = Assert.Single(fake.Calls, c => Seq(c, "warthunder", "import"));
        Assert.DoesNotContain("--replace", import);
        Assert.Contains("--include-keyboard", import);
        Assert.True(vm.IsImportWizardDoneStep);
        Assert.False(vm.ImportWizardFailed);
        Assert.Equal(UiStage.Start, vm.CurrentStage);
        Assert.StartsWith("Imported", warThunder.Result);

        // "Start over" replaces the profile's relations: asked first, and nothing runs when the user says no.
        vm.RestartImportWizardCommand.Execute(null);
        vm.StartImportWizardCommand.Execute(warThunder);
        vm.ImportWizardStartOver = true;
        vm.ConfirmHandler = _ => Task.FromResult(false);
        vm.Relations.Add(new RelationListItem { Name = "Gear" });
        fake.Calls.Clear();
        await vm.RunImportWizardCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fake.Calls, c => Seq(c, "warthunder", "import"));
        Assert.Contains(vm.AskedConfirmations, r => r.Title.StartsWith("Start over", StringComparison.Ordinal) && r.IsDanger);

        vm.ConfirmHandler = _ => Task.FromResult(true);
        await vm.RunImportWizardCommand.ExecuteAsync(null);
        Assert.Contains("--replace", Assert.Single(fake.Calls, c => Seq(c, "warthunder", "import")));
    }

    // Tester report (2.1.0): after an import the Start page's games were gone until JoyPro was restarted. The cards stay
    // under the import's result (success, failure, an exception), and leaving the page closes the result.
    [Fact]
    public async Task ImportWizard_TheGamesStayOnTheStartPage_AfterEveryImport()
    {
        NoviceFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        vm.CurrentStage = UiStage.Start;
        GameCardItem warThunder = vm.GameCards.Single(c => c.GameId == "WarThunder");
        Assert.True(vm.ShowGameCardList);

        // Choosing how: the options take the cards' place (Back brings them).
        vm.StartImportWizardCommand.Execute(warThunder);
        Assert.False(vm.ShowGameCardList);

        // Success: the result over the cards.
        await vm.RunImportWizardCommand.ExecuteAsync(null);
        Assert.True(vm.IsImportWizardDoneStep);
        Assert.True(vm.ShowGameCardList);
        Assert.NotEmpty(vm.GameCards);
        Assert.Equal(UiStage.Start, vm.CurrentStage);

        // Away and back: the result is closed, the games are there (the card keeps "Imported hh:mm").
        vm.CurrentStage = UiStage.Relations;
        vm.CurrentStage = UiStage.Start;
        Assert.True(vm.IsImportWizardPickStep);
        Assert.True(vm.ShowGameCardList);
        Assert.StartsWith("Imported", warThunder.Result);

        // Failure: the same.
        fake.FailImport = true;
        vm.StartImportWizardCommand.Execute(warThunder);
        await vm.RunImportWizardCommand.ExecuteAsync(null);
        Assert.True(vm.ImportWizardFailed);
        Assert.True(vm.ShowGameCardList);
        Assert.Equal(UiStage.Start, vm.CurrentStage);

        // The engine call throws: still a result, still the games.
        fake.FailImport = false;
        fake.ThrowOnImport = true;
        vm.RestartImportWizardCommand.Execute(null);
        vm.StartImportWizardCommand.Execute(warThunder);
        await vm.RunImportWizardCommand.ExecuteAsync(null);
        Assert.True(vm.IsImportWizardDoneStep);
        Assert.True(vm.ImportWizardFailed);
        Assert.True(vm.ShowGameCardList);
        Assert.Equal(UiStage.Start, vm.CurrentStage);

        // Close: the plain cards.
        vm.RestartImportWizardCommand.Execute(null);
        Assert.True(vm.IsImportWizardPickStep);
        Assert.True(vm.ShowGameCardList);
    }

    [Fact]
    public async Task ImportWizard_Dcs_LooksFirstWithScan()
    {
        NoviceFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);

        vm.StartImportWizardCommand.Execute(vm.GameCards.Single(c => c.GameId == "DCS"));
        Assert.True(vm.ImportWizardIsDcs);
        vm.ImportWizardKeyboard = false;
        fake.Calls.Clear();
        await vm.PreviewImportWizardCommand.ExecuteAsync(null);
        IReadOnlyList<string> scan = Assert.Single(fake.Calls, c => Seq(c, "dcs", "scan"));
        Assert.DoesNotContain(fake.Calls, c => Seq(c, "dcs", "import"));
        Assert.True(vm.EngineOutputExpanded);

        fake.Calls.Clear();
        await vm.RunImportWizardCommand.ExecuteAsync(null);
        Assert.True(fake.Calls.Any(c => Seq(c, "dcs", "import")), vm.StatusText + " / " + string.Join(" ; ", fake.Calls.Select(c => string.Join(" ", c))));
        IReadOnlyList<string> import = Assert.Single(fake.Calls, c => Seq(c, "dcs", "import"));
        Assert.Contains("--merge", import);
        Assert.True(Seq(import, "--include-keyboard", "false"));
        Assert.NotNull(scan);
    }

    [Fact]
    public async Task GameCard_UpdateCommandList_RefreshesTheGamesDb()
    {
        NoviceFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);

        fake.Calls.Clear();
        await vm.UpdateGameCommandListCommand.ExecuteAsync(vm.GameCards.Single(c => c.GameId == "WarThunder"));
        Assert.Single(fake.Calls, c => Seq(c, "warthunder", "refresh-db"));
        Assert.Single(fake.Calls, c => Seq(c, "db", "check-updates"));

        fake.Calls.Clear();
        await vm.UpdateGameCommandListCommand.ExecuteAsync(vm.GameCards.Single(c => c.GameId == "DCS"));
        IReadOnlyList<string> dcs = Assert.Single(fake.Calls, c => Seq(c, "dcs", "refresh", "id"));
        Assert.True(Seq(dcs, "--headless-lua", "true"));
        Assert.True(Seq(dcs, "--launch", "false"));
        Assert.StartsWith("Command list updated", vm.GameCards.Single(c => c.GameId == "DCS").Result);
    }

    // A fresh install ships no game data: games without a command list ("no-catalog") are offered "Build List"; building
    // all missing lists runs each game's refresh, for DCS the command list and then its clean profiles.
    [Fact]
    public async Task MissingCommandLists_AreBuiltFromTheGames()
    {
        const string noCatalog = """
            {"Games":[
              {"Game":"DCS","DisplayName":"DCS World","State":"no-catalog","Message":"JoyPro has no command list of DCS World yet.","RefreshCommand":"engine dcs refresh id","Details":[]},
              {"Game":"WarThunder","DisplayName":"War Thunder","State":"no-catalog","Message":"JoyPro has no command list of War Thunder yet.","RefreshCommand":"engine warthunder refresh-db","Details":[]},
              {"Game":"MSFS","DisplayName":"Microsoft Flight Simulator","State":"up-to-date","Message":"","RefreshCommand":"engine msfs refresh-db","Details":[]}
            ],"Outdated":2}
            """;
        NoviceFakeEngine fake = new() { CheckUpdates = noCatalog };
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        await vm.CheckDbUpdatesCommand.ExecuteAsync(null);

        Assert.True(vm.HasMissingCommandLists);
        Assert.Equal(2, vm.DbUpdateItems.Count);
        Assert.All(vm.DbUpdateItems, i => Assert.True(i.MissingList));
        Assert.Equal("Build List", vm.DbUpdateItems[0].RefreshLabel);
        Assert.False(vm.DbUpdateItems[0].CanKeep);
        GameCardItem dcsCard = vm.GameCards.Single(c => c.GameId == "DCS");
        Assert.True(dcsCard.MissingCommandList);
        Assert.StartsWith("No command list yet", dcsCard.UpdateHint);

        fake.Calls.Clear();
        await vm.BuildMissingCommandListsCommand.ExecuteAsync(null);
        int id = fake.Calls.FindIndex(c => Seq(c, "dcs", "refresh", "id"));
        int clean = fake.Calls.FindIndex(c => Seq(c, "dcs", "refresh", "clean"));
        Assert.True(id >= 0 && clean > id, string.Join(" ; ", fake.Calls.Select(c => string.Join(" ", c))));
        Assert.True(Seq(fake.Calls[clean], "--headless-lua", "true"));
        // War Thunder's archives are read by default (the engine's default; no switch).
        Assert.True(vm.ReadWarThunderArchives);
        IReadOnlyList<string> wt = Assert.Single(fake.Calls, c => Seq(c, "warthunder", "refresh-db"));
        Assert.DoesNotContain("--no-game-archives", wt);

        // The user turns archive reading off: the refresh says so.
        vm.ReadWarThunderArchives = false;
        fake.Calls.Clear();
        await vm.BuildMissingCommandListsCommand.ExecuteAsync(null);
        Assert.Contains("--no-game-archives", Assert.Single(fake.Calls, c => Seq(c, "warthunder", "refresh-db")));
    }

    [Fact]
    public async Task UndoExport_AsksFirst_ThenRestoresTheNewestBackupOfTheGame()
    {
        NoviceFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        GameCardItem dcs = vm.GameCards.Single(c => c.GameId == "DCS");

        vm.ConfirmHandler = _ => Task.FromResult(false);
        fake.Calls.Clear();
        await vm.UndoGameCardExportCommand.ExecuteAsync(dcs);
        Assert.DoesNotContain(fake.Calls, c => Seq(c, "backups", "restore"));

        vm.ConfirmHandler = _ => Task.FromResult(true);
        await vm.UndoGameCardExportCommand.ExecuteAsync(dcs);
        IReadOnlyList<string> restore = Assert.Single(fake.Calls, c => Seq(c, "backups", "restore"));
        Assert.True(Seq(restore, "backups", "restore", "latest", "--game", "DCS"));
        Assert.Equal("Last export undone", dcs.Result);

        // The same from an export tile.
        vm.ProfilePath = TempProfile();
        await vm.RefreshExportGamesCommand.ExecuteAsync(null);
        fake.Calls.Clear();
        await vm.UndoLastExportCommand.ExecuteAsync(vm.ExportGames.Single(g => g.GameId == "WarThunder"));
        Assert.True(Seq(Assert.Single(fake.Calls, c => Seq(c, "backups", "restore")), "latest", "--game", "WarThunder"));
    }

    [Fact]
    public async Task ExportModes_InPlainWords_AndRiskyExportsAskFirst()
    {
        NoviceFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.ProfilePath = TempProfile();
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        await vm.RefreshExportGamesCommand.ExecuteAsync(null);

        // Every engine mode has a described choice; the default (2.5.0) is clean + nuke, as in JoyPro 1.
        Assert.Equal(["clean", "merge", "merge-overwrite", "add"], vm.ExportModeOptions.Select(o => o.Key).ToArray());
        Assert.Equal("clean", vm.SelectedExportModeOption?.Key);
        Assert.True(vm.ExportNukeUntouchedDevices);
        Assert.False(vm.ExportDcsCleanAllAircraft);
        Assert.True(vm.ExportIsRisky);

        vm.SelectedExportModeOption = vm.ExportModeOptions.Single(o => o.Key == "clean");
        Assert.Equal("clean", vm.ExportMode);
        Assert.True(vm.ExportIsRisky);
        Assert.Contains("removed", vm.ExportModeExplanation);

        ExportGameItem warThunder = vm.ExportGames.Single(g => g.GameId == "WarThunder");
        vm.ConfirmHandler = _ => Task.FromResult(false);
        fake.Calls.Clear();
        await vm.ExportGameNowCommand.ExecuteAsync(warThunder);
        Assert.DoesNotContain(fake.Calls, c => Seq(c, "warthunder", "export"));
        Assert.True(vm.AskedConfirmations.Last().IsDanger);

        vm.ConfirmHandler = _ => Task.FromResult(true);
        await vm.ExportGameNowCommand.ExecuteAsync(warThunder);
        Assert.True(Seq(Assert.Single(fake.Calls, c => Seq(c, "warthunder", "export")), "--mode", "clean"));
        Assert.True(warThunder.ExportSucceeded);

        // A preview never writes: --dry-run, and the dry-run setting is back afterwards.
        fake.Calls.Clear();
        await vm.PreviewExportGameCommand.ExecuteAsync(warThunder);
        Assert.Contains("--dry-run", Assert.Single(fake.Calls, c => Seq(c, "warthunder", "export")));
        Assert.False(vm.RegistryGameDryRun);

        // A safe mode does not ask.
        vm.SelectedExportModeOption = vm.ExportModeOptions.Single(o => o.Key == "merge");
        vm.ExportNukeUntouchedDevices = false;
        int asked = vm.AskedConfirmations.Count;
        await vm.ExportGameNowCommand.ExecuteAsync(warThunder);
        Assert.Equal(asked, vm.AskedConfirmations.Count);
    }

    [Fact]
    public async Task DestructiveActions_AskFirst_AndDoNothingOnNo()
    {
        NoviceFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.ProfilePath = TempProfile();
        vm.ConfirmHandler = _ => Task.FromResult(false);

        vm.DeviceRemoveName = "Throttle {1}";
        vm.ExchangeOldDevice = "Stick {1}";
        vm.ExchangeNewDevice = "Stick {2}";
        vm.MergeRightProfilePath = TempProfile("other");
        fake.Calls.Clear();
        await vm.RemoveDeviceFromProfileCommand.ExecuteAsync(null);
        await vm.ExchangeDeviceCommand.ExecuteAsync(null);
        await vm.MergeProfileCommand.ExecuteAsync(null);
        await vm.DcsDefaultsCutCommand.ExecuteAsync(null);
        await vm.DcsFfbClearAllCommand.ExecuteAsync(null);
        Assert.Empty(fake.Calls);
        Assert.Equal(5, vm.AskedConfirmations.Count);

        // Yes: the command runs, and the profile is copied first.
        vm.ConfirmHandler = _ => Task.FromResult(true);
        await vm.MergeProfileCommand.ExecuteAsync(null);
        IReadOnlyList<string> merge = Assert.Single(fake.Calls, c => Seq(c, "profile", "merge"));
        Assert.True(Seq(merge, "--mode", "right"));
        string folder = Path.GetDirectoryName(vm.ProfilePath)!;
        Assert.NotEmpty(Directory.GetFiles(folder, Path.GetFileNameWithoutExtension(vm.ProfilePath) + ".before-merge-*.pr0file"));

        vm.SelectedMergeModeOption = vm.MergeModeOptions.Single(o => o.Key == "left");
        Assert.Equal("left", vm.MergeMode);
    }

    [Fact]
    public async Task DeviceCards_ShowConnectedAndProfileDevices_AndFillThePickers()
    {
        NoviceFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.ProfilePath = TempProfile();

        await vm.LoadDeviceCardsCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.DeviceCards.Count);
        DeviceCardItem stick = vm.DeviceCards.Single(c => c.DeviceName == "HOTAS Warthog Joystick");
        Assert.True(stick.Connected);
        Assert.True(stick.InProfile);
        Assert.Equal("2 axes · 19 buttons · 1 hats", stick.InputsText);
        Assert.False(vm.DeviceCards.Single(c => c.DeviceName == "HOTAS Warthog Throttle").InProfile);
        DeviceCardItem pedals = vm.DeviceCards.Single(c => c.Device.StartsWith("Old Pedals", StringComparison.Ordinal));
        Assert.False(pedals.Connected);
        Assert.True(pedals.InProfile);

        // The card fills the exchange picker; the new device comes from the connected ones.
        vm.MoveDeviceBindsFromCommand.Execute(pedals);
        Assert.Equal(pedals.Device, vm.ExchangeOldDevice);
        vm.ExchangeNewDevice = vm.ConnectedDevices.Single(d => d.StartsWith("Joystick", StringComparison.Ordinal));
        vm.ConfirmHandler = _ => Task.FromResult(true);
        fake.Calls.Clear();
        await vm.ExchangeDeviceCommand.ExecuteAsync(null);
        Assert.True(Seq(Assert.Single(fake.Calls, c => Seq(c, "devices", "exchange")), pedals.Device, vm.ExchangeNewDevice));

        fake.Calls.Clear();
        await vm.RemoveDeviceCardCommand.ExecuteAsync(pedals);
        Assert.True(Seq(Assert.Single(fake.Calls, c => Seq(c, "devices", "remove")), "devices", "remove", pedals.Device));
    }

    // Every supported game has its own badge (colour, motif, short name) for when Steam has no cover of it on this PC.
    [Fact]
    public void GameBadges_CoverEverySupportedGame()
    {
        string[] games = ["DCS", "IL2Game", "IL2Korea", "MSFS", "XPlane", "FalconBMS", "WarThunder", "VTOLVR", "VTOLVRNative", "NuclearOption", "Wardogs",
            "Arma2", "Arma2OA", "Arma3", "ArmaReforger", "HLLVietnam", "Battlefield6", "Battlefield2042", "AceCombat7", "AceCombat8", "ProjectWingman", "X4", "EliteDangerous", "StarCitizen",
            "IL21946", "IL2CoD", "IL2CoDBlitz", "FSX", "FSXSE", "P3D4", "P3D5", "P3D6"];
        foreach (string game in games)
        {
            (string text, Avalonia.Media.Color _, string motif) = JoyPro.Gui.Views.Controls.GameBadge.StyleOf(game, string.Empty);
            Assert.False(string.IsNullOrWhiteSpace(text), game);
            Assert.StartsWith("Icon.", motif);
        }
        Assert.Equal("Arma 3", JoyPro.Gui.Views.Controls.GameBadge.StyleOf("arma3", string.Empty).Short);
        Assert.Equal("Icon.Rocket", JoyPro.Gui.Views.Controls.GameBadge.StyleOf("EliteDangerous", string.Empty).Motif);
        // A game JoyPro learns later: its name and a stable colour.
        (string newText, Avalonia.Media.Color first, _) = JoyPro.Gui.Views.Controls.GameBadge.StyleOf("SomeNewGame", "Some New Game");
        Assert.Equal("Some New Game", newText);
        Assert.Equal(first, JoyPro.Gui.Views.Controls.GameBadge.StyleOf("SomeNewGame", "Some New Game").Color);
    }

    [Fact]
    public void ProfileDisplay_ShowsTheProfileName()
    {
        MainWindowViewModel vm = CreateVm(new NoviceFakeEngine());
        vm.ProfilePath = string.Empty;
        Assert.False(vm.HasProfile);
        Assert.Equal("No profile open", vm.ProfileDisplayName);
        vm.ProfilePath = @"C:\Profiles\My Sticks.pr0file";
        Assert.True(vm.HasProfile);
        Assert.Equal("My Sticks", vm.ProfileDisplayName);
    }

    [Fact]
    public void RenderNovicePages()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_GUI_PARITY_PREVIEW");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);

        AvaloniaRenderThread.Run(() =>
        {
            // Settings with its own view model (the device cards of a profile with an old pedal set).
            MainWindowViewModel settings = new(new NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = typeof(MainWindowViewModel).Assembly.Location,
                ProfilePath = TempProfile()
            };
            AvaloniaRenderThread.Wait(GuiParityRenderTests.Done(settings.LoadDeviceCardsCommand.ExecuteAsync(null)));
            settings.CurrentStage = UiStage.Settings;
            settings.ExpandAllSettingsCommand.Execute(null);
            GuiParityRenderTests.Render(settings, Path.Combine(outDir, "novice-settings.png"), "Games", 2400, page =>
            {
                Assert.Contains(page.GetVisualDescendantsOfType<Button>(), b => (b.Content as string) == "Move binds" && b.IsEffectivelyVisible);
            });


            NoviceFakeEngine fake = new();
            MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = typeof(MainWindowViewModel).Assembly.Location,
                ProfilePath = TempProfile()
            };
            AvaloniaRenderThread.Wait(GuiParityRenderTests.Done(vm.RefreshGameCardsCommand.ExecuteAsync(null)));
            AvaloniaRenderThread.Wait(GuiParityRenderTests.Done(vm.CheckDbUpdatesCommand.ExecuteAsync(null)));
            vm.GameCards.Single(c => c.GameId == "WarThunder").Result = "Imported 14:02";

            vm.CurrentStage = UiStage.Start;
            GuiParityRenderTests.Render(vm, Path.Combine(outDir, "novice-start-games.png"), "Start", 1100, page =>
            {
                List<Button> buttons = page.GetVisualDescendantsOfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
                Assert.Equal(3, buttons.Count(b => (b.Content as string) == "Import"));
                Assert.Contains(buttons, b => (b.Content as string) == "Undo export");
                Assert.Contains(buttons, b => (b.Content as string) == "Open profile...");
            });

            vm.StartImportWizardCommand.Execute(vm.GameCards.Single(c => c.GameId == "DCS"));
            GuiParityRenderTests.Render(vm, Path.Combine(outDir, "novice-import-wizard.png"), "Your games", 700, page =>
            {
                Assert.Contains(page.GetVisualDescendantsOfType<Button>(), b => (b.Content as string) == "Import now" && b.IsEffectivelyVisible);
            });

            vm.CurrentStage = UiStage.Export;
            AvaloniaRenderThread.Wait(GuiParityRenderTests.Done(vm.RefreshExportGamesCommand.ExecuteAsync(null)));
            vm.ExportGames.Single(g => g.GameId == "DCS").ExportState = "Exported 14:05";
            GuiParityRenderTests.Render(vm, Path.Combine(outDir, "novice-export.png"), "Export", 1300, page =>
            {
                Assert.Contains(page.GetVisualDescendantsOfType<TextBlock>(), t => t.Text == "Replace on my devices");
            });
        });
    }

    internal static bool Seq(IReadOnlyList<string> args, params string[] sequence) => AxisTuningTests.Seq(args, sequence);

    private static MainWindowViewModel CreateVm(NoviceFakeEngine fake)
    {
        return new MainWindowViewModel(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
    }

    private static string TempProfile(string name = "novice")
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "novice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name + ".pr0file");
        File.WriteAllText(path, "{}");
        return path;
    }

    internal sealed class NoviceFakeEngine : IEngineCliClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        // The answer to "db check-updates" when set (else the default: DCS updated).
        public string? CheckUpdates { get; set; }

        // "<game> import" ends with exit code 1 / throws.
        public bool FailImport { get; set; }

        public bool ThrowOnImport { get; set; }

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            bool import = arguments.Count > 1 && arguments.Contains("import");
            if (import && ThrowOnImport)
                throw new InvalidOperationException("The engine could not be started.");
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = import && FailImport ? 1 : 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = CheckUpdates != null && Seq(arguments, "db", "check-updates") ? CheckUpdates : Output(arguments),
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            });
        }

        private static string Output(IReadOnlyList<string> args)
        {
            if (Seq(args, "games", "installs") || Seq(args, "games", "install"))
            {
                return "{\"Games\":[" +
                       "{\"GameId\":\"DCS\",\"Name\":\"DCS World\",\"Installed\":true,\"InstallPath\":\"D:\\\\DCS World\",\"Source\":\"test\",\"ConfigPaths\":[]}," +
                       "{\"GameId\":\"IL2Game\",\"Name\":\"IL-2 Sturmovik: Great Battles\",\"Installed\":false,\"InstallPath\":\"\",\"Source\":\"\",\"ConfigPaths\":[]}," +
                       "{\"GameId\":\"WarThunder\",\"Name\":\"War Thunder\",\"Installed\":true,\"InstallPath\":\"D:\\\\War Thunder\",\"Source\":\"test\",\"ConfigPaths\":[]}," +
                       "{\"GameId\":\"MSFS\",\"Name\":\"Microsoft Flight Simulator\",\"Installed\":false,\"Disabled\":true,\"InstallPath\":\"\",\"Source\":\"\",\"ConfigPaths\":[]}" +
                       "]}";
            }
            if (Seq(args, "games", "integrations"))
            {
                return "{\"Integrations\":[" +
                       "{\"GameId\":\"WarThunder\",\"DisplayName\":\"War Thunder\",\"Aliases\":[\"warthunder\"],\"Notes\":\"\",\"SupportsCatalogRefresh\":true}," +
                       "{\"GameId\":\"MSFS\",\"DisplayName\":\"Microsoft Flight Simulator\",\"Aliases\":[\"msfs\"],\"Notes\":\"\",\"SupportsCatalogRefresh\":true}" +
                       "]}";
            }
            if (Seq(args, "db", "check-updates"))
                return "{\"Games\":[{\"Game\":\"DCS\",\"DisplayName\":\"DCS World\",\"State\":\"game-updated\",\"Message\":\"DCS World was updated.\",\"RefreshCommand\":\"engine dcs refresh id\",\"Details\":[]}],\"Outdated\":1}";
            if (Seq(args, "profile", "games"))
            {
                return "{\"Games\":[" +
                       "{\"GameId\":\"DCS\",\"Name\":\"DCS World\",\"Exporter\":\"dcs\",\"Relations\":12,\"Bound\":9,\"Installed\":true,\"Planes\":[{\"Plane\":\"F-16C_50\",\"Relations\":12,\"Bound\":9}]}," +
                       "{\"GameId\":\"WarThunder\",\"Name\":\"War Thunder\",\"Exporter\":\"registry\",\"Relations\":4,\"Bound\":4,\"Installed\":true}" +
                       "]}";
            }
            if (Seq(args, "devices", "list"))
            {
                return string.Join(Environment.NewLine, "Connected:", "  - Joystick - HOTAS Warthog {00000000-0000-0000-0000-0000000000a1}", "  - Throttle - HOTAS Warthog {00000000-0000-0000-0000-0000000000a2}",
                    "Profile:", "  - Joystick - HOTAS Warthog {00000000-0000-0000-0000-0000000000a1}", "  - Old Pedals {00000000-0000-0000-0000-0000000000a9}");
            }
            if (Seq(args, "hud", "devices"))
            {
                return "{\"Devices\":[" +
                       "{\"Device\":\"Joystick - HOTAS Warthog {00000000-0000-0000-0000-0000000000a1}\",\"Name\":\"HOTAS Warthog Joystick\",\"Model\":\"Thrustmaster HOTAS Warthog Joystick\",\"Kind\":\"stick\",\"Buttons\":19,\"Hats\":1,\"Axes\":[{\"Input\":\"JOY_X\"},{\"Input\":\"JOY_Y\"}],\"Image\":\"\"}," +
                       "{\"Device\":\"Throttle - HOTAS Warthog {00000000-0000-0000-0000-0000000000a2}\",\"Name\":\"HOTAS Warthog Throttle\",\"Model\":\"\",\"Kind\":\"throttle\",\"Buttons\":32,\"Hats\":1,\"Axes\":[{\"Input\":\"JOY_Z\"}],\"Image\":\"\"}" +
                       "]}";
            }
            if (Seq(args, "backups", "list"))
                return "[{\"Id\":\"20261011-140200-DCS\",\"Game\":\"DCS\",\"Created\":\"2026-10-11T14:02:00\",\"Files\":[\"a\",\"b\"],\"Command\":\"dcs export\"}]";
            return "ok";
        }
    }
}
