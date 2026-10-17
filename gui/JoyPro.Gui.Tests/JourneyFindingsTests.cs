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

// The user journey's findings (E:\JoyProWork\journey\findings.md, 2026-10-13): bound counts after Set Bind, the
// Binds page's hand pick (real game controllers, inputs by device and relation kind), the export tiles (ticks, failure
// reason, X of Y, Undo only after an export, the backups list) and a status line that says what happened.
[SupportedOSPlatform("windows")]
public sealed class JourneyFindingsTests : IDisposable
{
    private const string Throttle = "LEFT VPC Throttle MT-50 CM {42CEC570-0A3F-11f0-800B-444553540000}";
    private const string WarBrd = "L-VPC Stick WarBRD {42CE9E60-0A3F-11f0-8009-444553540000}";
    private const string VJoy = "vJoy Device {89D63500-B53F-11f1-8002-444553540000}";

    private readonly string temp = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "journey-findings-" + Guid.NewGuid().ToString("N"));

    public JourneyFindingsTests()
    {
        Directory.CreateDirectory(temp);
        DeviceReferenceService.CatalogFolderOverride = () => DeviceReferenceTests.ShippedCatalog;
        DeviceReferenceService.Reload();
    }

    public void Dispose()
    {
        DeviceReferenceService.CatalogFolderOverride = () => null;
        DeviceReferenceService.Reload();
        try
        {
            Directory.Delete(temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- B1: bound counts follow Set Bind, Clear and Unbind ----

    [Fact]
    public async Task SetBind_UpdatesBoundCounts_NextSteps_ProfileDevices_AndSaysWhatHappened()
    {
        ProfileFakeEngine fake = new();
        MainWindowViewModel vm = await LoadedVmAsync(fake);
        Assert.Equal(0, vm.ProfileBoundCount);
        Assert.Equal("0 of 2 bound", vm.ProfileBoundText);

        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Pitch");
        vm.SelectedBindDeviceChoice = vm.BindDeviceChoices.Single(c => c.Device == WarBrd);
        vm.BindInputValue = "JOY_Y";
        await vm.SetSelectedBindCommand.ExecuteAsync(null);

        Assert.True(IsBound(fake, "Pitch"));
        Assert.Equal(1, vm.ProfileBoundCount);
        Assert.Equal("1 of 2 bound", vm.ProfileBoundText);
        Assert.True(vm.Relations.Single(r => r.Name == "Pitch").IsBound);
        Assert.Contains(vm.NextSteps, s => s.Title == "1 relation has no button yet");
        Assert.Contains(vm.NextSteps, s => s.Title == "Write your binds into your games");
        // The device bound now is in the profile: its card says so.
        Assert.Contains(WarBrd, vm.ProfileDevices);
        for (int i = 0; i < 100 && !vm.DeviceCards.Single(c => c.Device == WarBrd).InProfile; i++)
            await Task.Delay(10);
        Assert.True(vm.DeviceCards.Single(c => c.Device == WarBrd).InProfile);
        Assert.StartsWith("Bound Pitch to L-VPC Stick WarBRD", vm.StatusText);
        Assert.EndsWith("JOY_Y.", vm.StatusText);
        Assert.DoesNotContain("{", vm.StatusText);

        // Clear: the count goes back.
        await vm.ClearSelectedBindCommand.ExecuteAsync(null);
        Assert.False(IsBound(fake, "Pitch"));
        Assert.Equal(0, vm.ProfileBoundCount);
        Assert.False(vm.Relations.Single(r => r.Name == "Pitch").IsBound);
        Assert.Equal("Cleared the bind of Pitch.", vm.StatusText);
    }

    // ---- B2: the hand pick ----

    [Fact]
    public async Task HandPick_RealControllersOnly_InputsByDeviceAndRelationKind()
    {
        ProfileFakeEngine fake = new();
        MainWindowViewModel vm = await LoadedVmAsync(fake);

        // An axis relation: the devices with axes, readable, no driver entries, no vJoy, no keyboard, each once.
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Pitch");
        List<string> labels = vm.BindDeviceChoices.Select(c => c.Label).ToList();
        Assert.Contains(vm.BindDeviceChoices, c => c.Device == WarBrd);
        Assert.DoesNotContain(vm.BindDeviceChoices, c => c.IsKeyboard);
        Assert.DoesNotContain(vm.BindDeviceChoices, c => c.Device.Contains("vJoy", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(labels, l => l.Contains("HID-compliant", StringComparison.Ordinal) || l.Contains("driver", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(labels, l => l.Contains('{'));
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Single(vm.BindDeviceChoices, c => c.Device.Contains("Wooting", StringComparison.Ordinal));
        Assert.Empty(vm.BindInputChoices);

        vm.SelectedBindDeviceChoice = vm.BindDeviceChoices.Single(c => c.Device == WarBrd);
        Assert.Equal(WarBrd, vm.BindDeviceInput);
        Assert.Equal(["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ"], vm.BindInputChoices.ToArray());

        // A button relation: that device's buttons and hat directions; the keyboard last, its keys only when picked.
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Gear");
        Assert.True(vm.BindDeviceChoices.Last().IsKeyboard);
        vm.SelectedBindDeviceChoice = vm.BindDeviceChoices.Single(c => c.Device == Throttle);
        Assert.Equal(94, vm.BindInputChoices.Count);
        Assert.Equal("JOY_BTN1", vm.BindInputChoices[0]);
        Assert.DoesNotContain(vm.BindInputChoices, i => !i.StartsWith("JOY_BTN", StringComparison.Ordinal));
        Assert.DoesNotContain("A", vm.BindInputChoices);
        vm.SelectedBindDeviceChoice = vm.BindDeviceChoices.Single(c => c.IsKeyboard);
        Assert.Equal("Keyboard", vm.BindDeviceInput);
        Assert.Contains("LShift", vm.BindInputChoices);
        Assert.DoesNotContain("JOY_BTN1", vm.BindInputChoices);
    }

    [Fact]
    public void HandPick_Lists_AreBuiltFromTheDevices()
    {
        BindDeviceInputs stickA = new() { Device = "Stick {A1}", Name = "Stick", Axes = ["JOY_X", "JOY_Y"], Buttons = 4, Hats = 1, Connected = true };
        BindDeviceInputs stickB = new() { Device = "Stick {B2}", Name = "Stick", Axes = ["JOY_X"], Buttons = 2, Connected = true };
        BindDeviceInputs panel = new() { Device = "Panel {C3}", Name = "Panel", Buttons = 30, Connected = true };
        BindDeviceInputs offline = new() { Device = "Old Pedals {D4}", Name = "Old Pedals" };

        List<BindDeviceChoice> axis = MainWindowViewModel.BuildBindDeviceChoices([stickA, stickB, panel, offline], axis: true, currentDevice: null);
        // Two alike: the GUID tells them apart; a device without axes is no axis choice; one not connected stays.
        Assert.Equal(["Old Pedals (not connected)", "Stick {A1}", "Stick {B2}"], axis.Select(c => c.Label).ToArray());

        List<BindDeviceChoice> button = MainWindowViewModel.BuildBindDeviceChoices([stickA, panel], axis: false, currentDevice: null);
        Assert.Equal(["Panel", "Stick", "Keyboard"], button.Select(c => c.Label).ToArray());

        // The bound device is shown even when JoyPro would not offer it (vJoy).
        List<BindDeviceChoice> current = MainWindowViewModel.BuildBindDeviceChoices([stickA], axis: true, currentDevice: "vJoy Device {E5}");
        Assert.Contains(current, c => c.Device == "vJoy Device {E5}" && c.Label == "vJoy Device (not connected)");

        Assert.Equal(["JOY_X", "JOY_Y"], MainWindowViewModel.BuildBindInputChoices(stickA, false, axis: true, []));
        List<string> buttons = MainWindowViewModel.BuildBindInputChoices(stickA, false, axis: false, []);
        Assert.Equal(["JOY_BTN1", "JOY_BTN2", "JOY_BTN3", "JOY_BTN4", "JOY_BTN_POV1_U", "JOY_BTN_POV1_UR", "JOY_BTN_POV1_R", "JOY_BTN_POV1_DR",
            "JOY_BTN_POV1_D", "JOY_BTN_POV1_DL", "JOY_BTN_POV1_L", "JOY_BTN_POV1_UL"], buttons);
        // Not connected: the usual inputs.
        Assert.Equal(8, MainWindowViewModel.BuildBindInputChoices(offline, false, axis: true, []).Count);
        Assert.Equal(40, MainWindowViewModel.BuildBindInputChoices(offline, false, axis: false, []).Count);
        // The keyboard: keys for buttons, nothing for an axis.
        Assert.Equal(["A", "B"], MainWindowViewModel.BuildBindInputChoices(null, true, axis: false, ["A", "B", "A"]));
        Assert.Empty(MainWindowViewModel.BuildBindInputChoices(null, true, axis: true, ["A"]));
    }

    [Fact]
    public void KeyboardKeys_AreReadFromTheDirectInputSection()
    {
        string output = string.Join(Environment.NewLine, "Device Input Catalog", "Joystick Axes: 1", "  - JOY_X", "Keyboard Keys (DirectInput): 2", "  - A", "  - LShift",
            "Keyboard Keys (DCS): 1", "  - LShift", "Keyboard Keys (IL2 format, use with --format il2): 1", "  - key_a");
        Assert.Equal(["A", "LShift"], MainWindowViewModel.ParseKeyboardKeyCatalog(output));
    }

    // ---- B3, B4: export tiles ----

    [Fact]
    public void ExportTiles_TickOnlyGamesWithBindsThatWereStarted()
    {
        Assert.True(new ExportGameItem("DCS", "DCS World", "dcs", 10, 2).IsSelected);
        Assert.False(new ExportGameItem("Arma3", "Arma 3", "registry", 10, 0).IsSelected);
        Assert.False(new ExportGameItem("IL2CoD", "Cliffs of Dover", "registry", 10, 2, started: false).IsSelected);
        Assert.False(new ExportGameItem("X", "Not installed", "registry", 10, 2, installed: false).IsSelected);
        ExportGameItem fresh = new("DCS", "DCS World", "dcs", 10, 2);
        Assert.False(fresh.CanUndoExport);
        fresh.HasBackup = true;
        Assert.True(fresh.CanUndoExport);

        ExportGameItem ok = new("DCS", "DCS World", "dcs", 10, 2);
        ExportGameItem bad = new("MSFS2020", "MSFS 2020", "registry", 10, 2) { ExportFailed = true, ExportState = "Failed: No MSFS device profiles found." };
        Assert.Equal("Exported 1 of 2 games, 1 failed: MSFS 2020 (No MSFS device profiles found).",
            MainWindowViewModel.ExportRunStatus([ok, bad], [ok], dryRun: false));
        Assert.Equal("Exported 1 of 1 game.", MainWindowViewModel.ExportRunStatus([ok], [ok], dryRun: false));
    }

    [Fact]
    public void FailureReason_ComesFromTheErrorsSection_OnStdoutToo()
    {
        EngineCommandResult result = new()
        {
            ExitCode = 1,
            StandardOutput = string.Join(Environment.NewLine, "War Thunder Export", "Config: X", "Bindings:", "  - ID_GEAR <- Throttle JOY_BTN5", "Errors:",
                "  - War Thunder file not found: E:\\x\\WarThunder", ""),
            StandardError = string.Empty
        };
        Assert.Equal("War Thunder file not found: E:\\x\\WarThunder", MainWindowViewModel.EngineFailureReason(result));
        Assert.Equal("Unknown option", MainWindowViewModel.EngineFailureReason(new EngineCommandResult { ExitCode = 1, StandardError = "engine error: Unknown option" }));
    }

    [Fact]
    public async Task Export_TilesShowWhy_StatusSaysXofY_UndoAndBackupsFollow()
    {
        ProfileFakeEngine fake = new();
        MainWindowViewModel vm = await LoadedVmAsync(fake);
        vm.ConfirmHandler = _ => Task.FromResult(true);
        await vm.RefreshGameInstallsCommand.ExecuteAsync(null);
        await vm.RefreshExportGamesCommand.ExecuteAsync(null);
        await vm.RefreshGameBackupsCommand.ExecuteAsync(null);

        ExportGameItem dcs = vm.ExportGames.Single(g => g.GameId == "DCS");
        ExportGameItem wt = vm.ExportGames.Single(g => g.GameId == "WarThunder");
        ExportGameItem arma = vm.ExportGames.Single(g => g.GameId == "Arma3");
        ExportGameItem cod = vm.ExportGames.Single(g => g.GameId == "IL2CoD");
        Assert.True(dcs.IsSelected);
        Assert.True(wt.IsSelected);
        Assert.False(arma.IsSelected);
        Assert.False(cod.IsSelected);
        Assert.Contains("2 ticked", vm.ExportGamesStatus);
        Assert.False(dcs.CanUndoExport);
        Assert.False(wt.CanUndoExport);
        Assert.Empty(vm.GameBackups);

        await vm.ExportSelectedGamesCommand.ExecuteAsync(null);
        Assert.True(dcs.ExportSucceeded);
        Assert.True(wt.ExportFailed);
        Assert.Equal("Failed: War Thunder file not found: E:\\games\\WarThunder", wt.ExportState);
        Assert.Equal("Exported 1 of 2 games, 1 failed: War Thunder (War Thunder file not found: E:\\games\\WarThunder).", vm.StatusText);
        Assert.Equal(vm.StatusText, vm.ExportGamesStatus);
        // The backup of the export is listed, and only that game can undo.
        Assert.Single(vm.GameBackups);
        Assert.True(dcs.CanUndoExport);
        Assert.False(wt.CanUndoExport);
        Assert.False(arma.CanUndoExport);

        // A refresh keeps the results and the user's ticks.
        arma.IsSelected = true;
        await vm.RefreshExportGamesCommand.ExecuteAsync(null);
        Assert.True(vm.ExportGames.Single(g => g.GameId == "Arma3").IsSelected);
        Assert.True(vm.ExportGames.Single(g => g.GameId == "WarThunder").ExportFailed);
        Assert.True(vm.ExportGames.Single(g => g.GameId == "DCS").CanUndoExport);
    }

    // ---- A6: the status line ----

    [Fact]
    public async Task StatusLine_AReadKeepsTheLastResult()
    {
        ProfileFakeEngine fake = new();
        MainWindowViewModel vm = await LoadedVmAsync(fake);
        vm.StatusText = "Bound Pitch to Stick JOY_Y.";
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        Assert.Equal("Bound Pitch to Stick JOY_Y.", vm.StatusText);
    }

    // ---- A4: no command line in the banner ----

    [Fact]
    public void DbUpdateBanner_LeavesTheCommandLineOut()
    {
        Assert.Equal("DCS World was updated since its DB was built (57 modules -> 59 modules). New key commands may be missing; refresh the DCS World DB.",
            MainWindowViewModel.FriendlyDbUpdateMessage("DCS World was updated since its DB was built (57 modules -> 59 modules). New key commands may be missing; refresh the DCS World DB (engine dcs refresh id)."));
    }

    // ---- A12: small ones ----

    [Fact]
    public void SetupWizard_TicksOnlyWatchedGamesThatWereStarted_AndChipsAndPreviewsReadWell()
    {
        Assert.True(new SetupGameChoice("DCS", "DCS World", watched: true).IsChosen);
        SetupGameChoice never = new("IL2CoD", "Cliffs of Dover", watched: true, started: false);
        Assert.False(never.IsChosen);
        Assert.Equal("not started yet", never.Hint);
        Assert.Equal("No group", new SelectableTagItem("<none>").Label);
        Assert.Equal("Flight", new SelectableTagItem("Flight").Label);
    }

    [Fact]
    public async Task ModifierPreview_SaysWhatWasPicked_WithoutRawPlaceholders()
    {
        MainWindowViewModel vm = await LoadedVmAsync(new ProfileFakeEngine());
        vm.ModifierCreateDevice = string.Empty;
        vm.ModifierCreateInput = string.Empty;
        Assert.DoesNotContain("<none>", vm.ModifierCapturePreview);
        vm.ModifierCreateDevice = WarBrd;
        vm.ModifierCreateInput = "JOY_BTN3";
        Assert.Equal("Shift button: L-VPC Stick WarBRD JOY_BTN3", vm.ModifierCapturePreview);
    }

    // ---- A10: the settings files follow JOYPRO_DATA_DIR ----

    [Fact]
    public void SettingsFiles_FollowTheDataFolderOfEachViewModel()
    {
        string? previous = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        try
        {
            foreach (string name in new[] { "a", "b" })
            {
                string data = Path.Combine(temp, name);
                Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", data);
                MainWindowViewModel vm = new(new ProfileFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true)
                {
                    EnginePath = typeof(MainWindowViewModel).Assembly.Location
                };
                vm.SkipGameSetup();
                vm.StopGameConfigWatcher();
                Assert.True(File.Exists(Path.Combine(data, "settings", "ui-settings.json")), "no ui-settings.json in " + data);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previous);
        }
    }

    // ---- helpers ----

    private static bool IsBound(ProfileFakeEngine fake, string relation)
    {
        lock (fake.Bound)
            return fake.Bound.ContainsKey(relation);
    }

    private async Task<MainWindowViewModel> LoadedVmAsync(ProfileFakeEngine fake)
    {
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        vm.ProfilePath = Path.Combine(temp, "journey.pr0file");
        File.WriteAllText(vm.ProfilePath, "{}");
        await vm.LoadDeviceCardsCommand.ExecuteAsync(null);
        await vm.RefreshDeviceInputCatalogCommand.ExecuteAsync(null);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        vm.ConfirmHandler = _ => Task.FromResult(true);
        return vm;
    }

    // A profile with the axis "Pitch" and the button "Gear" (nothing bound), the devices of the user's PC
    // (DeviceCardsTests.RealPcFakeEngine), binds that the engine keeps, games for the export tiles and backups made by
    // the exports.
    private sealed class ProfileFakeEngine : IEngineCliClient
    {
        private readonly DeviceCardsTests.RealPcFakeEngine devices = new();
        private readonly List<string> backups = [];

        public Dictionary<string, (string Device, string Input)> Bound { get; } = [];

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            int exit = 0;
            string output = Output(arguments, ref exit);
            if (output == "\u0000")
                return devices.RunAsync(engineExePath, arguments, cancellationToken);
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = exit,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = output,
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            });
        }

        private static string Value(IReadOnlyList<string> args, string option)
        {
            int index = args.ToList().IndexOf(option);
            return index >= 0 && index + 1 < args.Count ? args[index + 1] : string.Empty;
        }

        private string BindJson(string relation, bool axis)
        {
            if (!Bound.TryGetValue(relation, out (string Device, string Input) bind))
                return "null";
            return "{\"Joystick\":\"" + bind.Device + "\",\"JAxis\":\"" + (axis ? bind.Input : string.Empty) + "\",\"JButton\":\"" + (axis ? string.Empty : bind.Input) + "\"}";
        }

        private string Output(IReadOnlyList<string> args, ref int exit)
        {
            lock (Bound)
            {
                if (NoviceGuiTests.Seq(args, "relations", "list"))
                {
                    return "{\"Relations\":[" +
                           "{\"Name\":\"Pitch\",\"IsAxis\":true,\"Groups\":[\"Flight\"],\"Nodes\":[{\"Id\":\"a2001cdnil\",\"Game\":\"DCS\",\"Aircraft\":{\"A-10C\":true}}],\"Bind\":" + BindJson("Pitch", true) + "}," +
                           "{\"Name\":\"Gear\",\"IsAxis\":false,\"Groups\":[\"Flight\"],\"Nodes\":[{\"Id\":\"d68\",\"Game\":\"DCS\",\"Aircraft\":{\"A-10C\":true}}],\"Bind\":" + BindJson("Gear", false) + "}]}";
                }
                if (NoviceGuiTests.Seq(args, "binds", "list"))
                {
                    return "{\"Binds\":[" + string.Join(",", Bound.Select(b =>
                    {
                        bool axis = b.Key == "Pitch";
                        return "{\"RelationName\":\"" + b.Key + "\",\"IsAxis\":" + (axis ? "true" : "false") + ",\"Joystick\":\"" + b.Value.Device + "\",\"JAxis\":\"" +
                               (axis ? b.Value.Input : string.Empty) + "\",\"JButton\":\"" + (axis ? string.Empty : b.Value.Input) + "\",\"AllReformers\":[],\"Curvature\":[]}";
                    })) + "]}";
                }
                if (NoviceGuiTests.Seq(args, "binds", "set"))
                {
                    Bound[args[args.ToList().IndexOf("set") + 1]] = (Value(args, "--device"), Value(args, "--input"));
                    return "ok";
                }
                if (NoviceGuiTests.Seq(args, "binds", "clear"))
                {
                    Bound.Remove(args[args.ToList().IndexOf("clear") + 1]);
                    return "ok";
                }
                if (NoviceGuiTests.Seq(args, "binds", "modifier", "list"))
                    return "Modifiers" + Environment.NewLine + "Modifiers: 0";
                if (NoviceGuiTests.Seq(args, "devices", "inputs"))
                {
                    return string.Join(Environment.NewLine, "Device Input Catalog", "Joystick Axes: 1", "  - JOY_X", "Joystick Buttons: 1", "  - JOY_BTN1",
                        "Keyboard Keys (DirectInput): 3", "  - A", "  - LShift", "  - Space", "Keyboard Keys (DCS): 1", "  - LShift");
                }
                if (NoviceGuiTests.Seq(args, "games", "installs"))
                {
                    static string Game(string id, string name, bool config) =>
                        "{\"GameId\":\"" + id + "\",\"Name\":\"" + name + "\",\"Installed\":true,\"InstallPath\":\"E:/games/" + id + "\",\"Source\":\"test\",\"Overridden\":false,\"MarkerFound\":true," +
                        "\"ConfigPaths\":[" + (config ? "\"E:/games/" + id + "/config\"" : string.Empty) + "],\"Note\":\"\"}";
                    return "{\"Games\":[" + Game("DCS", "DCS World", true) + "," + Game("WarThunder", "War Thunder", true) + "," + Game("Arma3", "Arma 3", true) + "," +
                           Game("IL2CoD", "IL-2 Sturmovik: Cliffs of Dover", false) + "],\"Warnings\":[]}";
                }
                if (NoviceGuiTests.Seq(args, "games", "integrations"))
                {
                    return "{\"Integrations\":[{\"GameId\":\"WarThunder\",\"DisplayName\":\"War Thunder\",\"Aliases\":[\"warthunder\"],\"Experimental\":true,\"Notes\":\"\"}," +
                           "{\"GameId\":\"Arma3\",\"DisplayName\":\"Arma 3\",\"Aliases\":[\"arma3\"],\"Experimental\":false,\"Notes\":\"\"}," +
                           "{\"GameId\":\"IL2CoD\",\"DisplayName\":\"IL-2 Sturmovik: Cliffs of Dover\",\"Aliases\":[\"il2cod\"],\"Experimental\":false,\"Notes\":\"\"}]}";
                }
                if (NoviceGuiTests.Seq(args, "profile", "games"))
                {
                    return "{\"Games\":[" +
                           "{\"GameId\":\"DCS\",\"Name\":\"DCS World\",\"Exporter\":\"dcs\",\"Relations\":2,\"Bound\":2,\"Planes\":[]}," +
                           "{\"GameId\":\"WarThunder\",\"Name\":\"War Thunder\",\"Exporter\":\"registry\",\"Relations\":2,\"Bound\":1,\"Planes\":[]}," +
                           "{\"GameId\":\"Arma3\",\"Name\":\"Arma 3\",\"Exporter\":\"registry\",\"Relations\":1,\"Bound\":0,\"Planes\":[]}," +
                           "{\"GameId\":\"IL2CoD\",\"Name\":\"IL-2 Sturmovik: Cliffs of Dover\",\"Exporter\":\"registry\",\"Relations\":2,\"Bound\":2,\"Planes\":[]}]}";
                }
                if (NoviceGuiTests.Seq(args, "dcs", "export"))
                {
                    backups.Add("{\"Id\":\"20261013-100000-000_DCS\",\"Game\":\"DCS\",\"Command\":\"engine dcs export\",\"Created\":\"2026-10-13T10:00:00\",\"Files\":[{\"Original\":\"a\"}]}");
                    return "DCS Export" + Environment.NewLine + "Written: 2";
                }
                if (NoviceGuiTests.Seq(args, "warthunder", "export"))
                {
                    exit = 1;
                    return string.Join(Environment.NewLine, "War Thunder Export", "Config: E:\\games\\WarThunder", "Errors:", "  - War Thunder file not found: E:\\games\\WarThunder");
                }
                if (NoviceGuiTests.Seq(args, "backups", "list"))
                    return "[" + string.Join(",", backups) + "]";
                if (NoviceGuiTests.Seq(args, "devices", "list") || NoviceGuiTests.Seq(args, "hud", "devices"))
                    return "\u0000";
                return "ok";
            }
        }
    }
}
