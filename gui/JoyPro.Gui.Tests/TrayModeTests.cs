using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Watch;
using Microsoft.Win32;
using Xunit;

namespace JoyPro.Gui.Tests;

// Tray mode on the GUI side: the autostart entry (always an in-memory Run key, never HKCU) that starts the tray host,
// the settings in ui-settings.json, --review / --tray, the hand-over of a second start's review list, single instance
// and the shared sync output parsing (docs/sync-watcher.md#tray-and-autostart). The persistence test writes the test
// run's ui-settings.json, so the class runs alone.
[CollectionDefinition("TrayMode", DisableParallelization = true)]
public sealed class TrayModeCollection
{
}

[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class TrayModeTests
{
    private const string TrayExe = @"C:\Program Files\JoyPro2\tray\JoyPro.Tray.exe";
    private const string PortableTrayExe = @"D:\Games\Tools\JoyPro2 portable\tray\JoyPro.Tray.exe";

    // ---- Autostart entry ------------------------------------------------------------------------------------------

    [Fact]
    public void TestsNeverGetTheRealRunKey()
    {
        Assert.IsType<InMemoryRunKeyStore>(AutostartService.StoreFactory());
        MainWindowViewModel vm = CreateVm();
        Assert.IsType<InMemoryRunKeyStore>(StoreOf(vm.Autostart));
    }

    [Fact]
    public void BuildCommand_QuotesThePathAndAddsTray_AndStartsTheTrayHost()
    {
        Assert.Equal("\"" + TrayExe + "\" --tray", AutostartService.BuildCommand(TrayExe));
        Assert.Equal("\"" + PortableTrayExe + "\" --tray", AutostartService.BuildCommand(PortableTrayExe));
        Assert.Equal("\"C:\\Program Files\\dotnet\\dotnet.exe\" \"D:\\JoyPro 2\\JoyPro.Tray.dll\" --tray",
            AutostartService.BuildCommand(@"C:\Program Files\dotnet\dotnet.exe", @"D:\JoyPro 2\JoyPro.Tray.dll"));
        // A stray quote cannot break the command line.
        Assert.Equal("\"" + TrayExe + "\" --tray", AutostartService.BuildCommand("\"" + TrayExe + "\""));
        Assert.Throws<ArgumentException>(() => AutostartService.BuildCommand("  "));

        // The Run entry starts the small tray host next to the GUI, not the GUI.
        string current = AutostartService.CurrentLaunchCommand();
        Assert.Equal("\"" + Path.Combine(AppContext.BaseDirectory, "tray", "JoyPro.Tray.exe") + "\" --tray", current);
        Assert.Equal(TrayHostLauncher.TrayExePath, AutostartService.ExecutableOf(current));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\JoyPro2\\tray\\JoyPro.Tray.exe\" --tray", @"C:\Program Files\JoyPro2\tray\JoyPro.Tray.exe")]
    [InlineData("C:\\Program Files\\JoyPro2\\tray\\JoyPro.Tray.exe --tray", @"C:\Program Files\JoyPro2\tray\JoyPro.Tray.exe")]
    [InlineData("C:\\Tools\\joypro.cmd --tray", @"C:\Tools\joypro.cmd")]
    [InlineData("", "")]
    public void ExecutableOf_ReadsTheProgramLikeWindows(string command, string expected)
    {
        Assert.Equal(expected, AutostartService.ExecutableOf(command));
    }

    [Fact]
    public void EnableDisable_WriteOnlyTheJoyPro2Value()
    {
        InMemoryRunKeyStore store = new();
        store.Set("OtherApp", "\"C:\\other.exe\"");
        AutostartService service = new(store, AutostartService.BuildCommand(TrayExe));

        Assert.False(service.IsRegistered);
        service.Enable();
        Assert.Equal("\"" + TrayExe + "\" --tray", store.Get(AutostartService.ValueName));
        Assert.True(service.IsRegistered);
        Assert.True(service.PointsHere);
        Assert.Null(service.ApprovedByWindows);

        service.Disable();
        Assert.Null(store.Get(AutostartService.ValueName));
        Assert.Equal("\"C:\\other.exe\"", store.Get("OtherApp"));
        service.Disable(); // nothing to remove: no error
    }

    [Fact]
    public void Repair_RewritesMissingMovedOrGuiEntries_LeavesAnotherExistingTrayHost()
    {
        InMemoryRunKeyStore store = new();
        HashSet<string> existing = new(StringComparer.OrdinalIgnoreCase) { TrayExe, @"C:\Program Files\JoyPro2\JoyPro.Gui.exe" };
        AutostartService portable = new(store, AutostartService.BuildCommand(PortableTrayExe), existing.Contains);

        Assert.True(portable.Repair());
        Assert.True(portable.PointsHere);
        Assert.False(portable.Repair());

        // The portable folder was moved: the entry starts a JoyPro2 that is gone.
        store.Set(AutostartService.ValueName, AutostartService.BuildCommand(@"E:\old place\tray\JoyPro.Tray.exe"));
        Assert.True(portable.Repair());
        Assert.Equal(AutostartService.BuildCommand(PortableTrayExe), store.Get(AutostartService.ValueName));

        // The first tray mode started the whole GUI: replaced by the tray host.
        store.Set(AutostartService.ValueName, AutostartService.BuildCommand(@"C:\Program Files\JoyPro2\JoyPro.Gui.exe"));
        Assert.True(portable.Repair());
        Assert.Equal(AutostartService.BuildCommand(PortableTrayExe), store.Get(AutostartService.ValueName));

        // Another install's tray host that exists keeps its entry (no flip-flopping between two copies).
        store.Set(AutostartService.ValueName, AutostartService.BuildCommand(TrayExe));
        Assert.False(portable.Repair());
        Assert.Equal(AutostartService.BuildCommand(TrayExe), store.Get(AutostartService.ValueName));
    }

    // ---- Settings in the view model -------------------------------------------------------------------------------

    [Fact]
    public void StartWithWindows_WritesAndRemovesTheEntry_TurnsCloseToTrayOn_AndStartsTheTrayHost()
    {
        MainWindowViewModel vm = CreateVm();
        int trayStarts = 0;
        vm.EnsureTrayHost = () => ++trayStarts > 0;
        InMemoryRunKeyStore store = new();
        vm.Autostart = new AutostartService(store, AutostartService.BuildCommand(TrayExe), _ => true);
        Assert.False(vm.StartWithWindows);
        Assert.False(vm.CloseToTray);
        Assert.StartsWith("Off", vm.AutostartStatus);
        vm.ApplyAutostartAtStartup();
        Assert.Equal(0, trayStarts);

        vm.StartWithWindows = true;
        Assert.Equal(AutostartService.BuildCommand(TrayExe), store.Get(AutostartService.ValueName));
        Assert.True(vm.CloseToTray);
        Assert.Equal(1, trayStarts);
        Assert.Equal("On: JoyPro2's tray icon starts when you sign in to Windows.", vm.AutostartStatus);

        store.Approved[AutostartService.ValueName] = false;
        vm.ApplyAutostartAtStartup();
        Assert.Contains("Windows Settings > Apps > Startup", vm.AutostartStatus);
        Assert.Equal(2, trayStarts);
        store.Approved.Clear();

        store.Set(AutostartService.ValueName, AutostartService.BuildCommand(PortableTrayExe));
        vm.ApplyAutostartAtStartup();
        Assert.Contains(PortableTrayExe, vm.AutostartStatus);

        vm.CloseToTray = false;
        vm.StartWithWindows = false;
        Assert.Null(store.Get(AutostartService.ValueName));
        Assert.False(vm.CloseToTray);
        Assert.StartsWith("Off", vm.AutostartStatus);
        int before = trayStarts;
        vm.ApplyAutostartAtStartup();
        Assert.Empty(store.Values);
        Assert.Equal(before, trayStarts);

        vm.CloseToTray = true;
        Assert.Equal(before + 1, trayStarts);
    }

    [Fact]
    public void TraySettings_PersistInUiSettingsJson_UnderJoyProDataDir_AndTheTrayHostReadsThem()
    {
        string? realBefore = ReadRealRunValue();
        string settingsFile = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");
        Assert.Equal(JoyProPaths.UiSettingsFile(JoyProPaths.SettingsFolder), settingsFile);
        string? dataDir = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Assert.False(string.IsNullOrWhiteSpace(dataDir));
        Assert.StartsWith(Path.GetFullPath(dataDir!), Path.GetFullPath(settingsFile), StringComparison.OrdinalIgnoreCase);
        TestEnvironment.SharedRunKey.Remove(AutostartService.ValueName);
        try
        {
            MainWindowViewModel first = new(new TrayFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
            first.StartWithWindows = true;
            first.CloseToTray = false;
            first.WatchGameConfigs = false;
            Assert.NotNull(TestEnvironment.SharedRunKey.Get(AutostartService.ValueName));

            // What the tray host sees.
            UiSettingsFileStore store = new(settingsFile);
            Assert.Equal(new TraySettings(false, true, false, string.Empty), store.Load());

            // The tray host's "Watching" switch changes only that field; the GUI reads it back with everything else.
            store.SaveWatchGameConfigs(true);
            MainWindowViewModel second = new(new TrayFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
            Assert.True(second.StartWithWindows);
            Assert.False(second.CloseToTray);
            Assert.True(second.WatchGameConfigs);

            second.StartWithWindows = false;
            Assert.Null(TestEnvironment.SharedRunKey.Get(AutostartService.ValueName));
            MainWindowViewModel third = new(new TrayFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
            Assert.False(third.StartWithWindows);
        }
        finally
        {
            File.Delete(settingsFile);
            TestEnvironment.SharedRunKey.Remove(AutostartService.ValueName);
        }
        // Read only: the real key is exactly as before.
        Assert.Equal(realBefore, ReadRealRunValue());
    }

    [Fact]
    public void UiSettingsFileStore_DefaultsWithoutFile_AndKeepsOtherFields()
    {
        string folder = TempFolder();
        try
        {
            string file = Path.Combine(folder, "ui-settings.json");
            UiSettingsFileStore store = new(file);
            Assert.Equal(new TraySettings(true, false, false, string.Empty), store.Load());

            File.WriteAllText(file, "{\n  \"lastProfilePath\": \"D:\\\\p.pr0file\",\n  \"themeMode\": \"Dark\",\n  \"WatchGameConfigs\": true,\n  \"closeToTray\": true\n}");
            Assert.Equal(new TraySettings(true, false, true, @"D:\p.pr0file"), store.Load());
            store.SaveWatchGameConfigs(false);
            string json = File.ReadAllText(file);
            Assert.Contains("\"themeMode\": \"Dark\"", json);
            Assert.Contains("\"WatchGameConfigs\": false", json);
            Assert.DoesNotContain("watchGameConfigs", json);
            Assert.False(store.Load().WatchGameConfigs);

            File.WriteAllText(file, "not json");
            Assert.Equal(new TraySettings(true, false, false, string.Empty), store.Load());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // ---- --review and the hand-over ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(new string[0], false, "")]
    [InlineData(new[] { "--tray" }, true, "")]
    [InlineData(new[] { "/Tray" }, true, "")]
    [InlineData(new[] { "--review", "DCS|C:\\Saved Games\\DCS", "--review", "XPlane|H:\\x.prf", "--review", "DCS|C:\\Saved Games\\DCS" }, false, "DCS|C:\\Saved Games\\DCS;XPlane|H:\\x.prf")]
    [InlineData(new[] { "--review" }, false, "")]
    public void StartupOptions_TrayAndReview(string[] args, bool tray, string review)
    {
        StartupOptions options = StartupOptions.Parse(args);
        Assert.Equal(tray, options.StartInTray);
        Assert.Equal(review, string.Join(";", options.ReviewKeys));
    }

    [Fact]
    public void SecondStart_HandsItsReviewListToTheRunningGui()
    {
        string folder = TempFolder();
        try
        {
            InstanceNames names = new(folder);
            Assert.Empty(AppShell.TakeForwardedReviewKeys(names));
            AppShell.ForwardReviewKeys(names, ["DCS|C:\\Saved Games\\DCS"]);
            AppShell.ForwardReviewKeys(names, ["XPlane|H:\\x.prf"]);
            Assert.Equal(["DCS|C:\\Saved Games\\DCS", "XPlane|H:\\x.prf"], AppShell.TakeForwardedReviewKeys(names));
            Assert.False(File.Exists(names.GuiRequestFile));
            Assert.Empty(AppShell.TakeForwardedReviewKeys(names));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Review_LoadsTheLastProfile_AndShowsTheGamesChanges()
    {
        string profile = Path.Combine(TempFolder(), "tray.pr0file");
        File.WriteAllText(profile, "{}");
        try
        {
            TrayFakeEngine engine = new();
            MainWindowViewModel vm = CreateVm(engine);
            vm.ProfilePath = profile;

            await vm.ReviewGameChangesAsync(["DCS|C:\\Saved Games\\DCS", " "]);
            for (int i = 0; i < 50 && !vm.HasGameSyncChanges; i++)
                await Task.Delay(20);

            Assert.True(vm.HasGameSyncChanges);
            Assert.Equal("DCS World: 2 changes made in the game", vm.GameSyncTitle);
            IReadOnlyList<string> diff = Assert.Single(engine.Calls, c => Seq(c, "sync", "diff"));
            Assert.True(Seq(diff, "--game", "DCS", "--path", "C:\\Saved Games\\DCS"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(profile)!, true);
        }
    }

    // The dialog came up: the window gets one notice (information, "A game changed its controls"; MainWindow shows it and
    // flashes the taskbar button when the window is not active). Nothing to ask: no notice, the state is accepted.
    [Fact]
    public async Task Review_WithChanges_RaisesOneNotice_WithoutChanges_None()
    {
        string profile = Path.Combine(TempFolder(), "notice.pr0file");
        File.WriteAllText(profile, "{}");
        try
        {
            TrayFakeEngine engine = new();
            MainWindowViewModel vm = CreateVm(engine);
            vm.ProfilePath = profile;
            List<(string Title, string Text)> notices = [];
            vm.GameChangesOffered += (title, text) => notices.Add((title, text));

            await vm.ReviewGameChangesAsync(["DCS|C:\\Saved Games\\DCS"]);
            for (int i = 0; i < 50 && !vm.HasGameSyncChanges; i++)
                await Task.Delay(20);
            (string title, string text) = Assert.Single(notices);
            Assert.Equal("A game changed its controls", title);
            Assert.Equal("DCS World changed controls JoyPro manages. Review them in JoyPro.", text);

            await vm.IgnoreGameChangesCommand.ExecuteAsync(null);
            Assert.False(vm.HasGameSyncChanges);
            Assert.Contains(engine.Calls, c => Seq(c, "sync", "accept"));

            engine.DiffChanges = 0;
            int accepts = engine.Calls.Count(c => Seq(c, "sync", "accept"));
            await vm.ReviewGameChangesAsync(["DCS|C:\\Saved Games\\DCS"]);
            for (int i = 0; i < 50 && engine.Calls.Count(c => Seq(c, "sync", "accept")) == accepts; i++)
                await Task.Delay(20);
            Assert.Single(notices);
            Assert.False(vm.HasGameSyncChanges);
            Assert.Equal(accepts + 1, engine.Calls.Count(c => Seq(c, "sync", "accept")));

            // A config written again with the same content: not even diffed.
            engine.GameState = "unchanged";
            int diffs = engine.Calls.Count(c => Seq(c, "sync", "diff"));
            int statuses = engine.Calls.Count(c => Seq(c, "sync", "status"));
            await vm.ReviewGameChangesAsync(["DCS|C:\\Saved Games\\DCS"]);
            for (int i = 0; i < 50 && engine.Calls.Count(c => Seq(c, "sync", "status")) == statuses; i++)
                await Task.Delay(20);
            await Task.Delay(50);
            Assert.Equal(diffs, engine.Calls.Count(c => Seq(c, "sync", "diff")));
            Assert.Single(notices);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(profile)!, true);
        }
    }

    [Fact]
    public void TaskbarFlash_WithoutAWindow_DoesNothing()
    {
        TaskbarFlash.Flash(IntPtr.Zero);
    }

    // ---- Single instance ----------------------------------------------------------------------------------------------

    [Fact]
    public void SingleInstance_SecondStartSignalsTheFirst()
    {
        string name = "JoyPro2.Gui.Tests." + Guid.NewGuid().ToString("N");
        Assert.False(SingleInstanceGuard.SignalExisting(name));

        using (SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire(name))
        {
            Assert.NotNull(first);
            using ManualResetEventSlim activated = new(false);
            int count = 0;
            first!.ActivationRequested += () =>
            {
                Interlocked.Increment(ref count);
                activated.Set();
            };

            Assert.Null(SingleInstanceGuard.TryAcquire(name));
            Assert.True(SingleInstanceGuard.SignalExisting(name));
            Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, count);

            activated.Reset();
            Assert.True(SingleInstanceGuard.SignalExisting(name));
            Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, count);
        }

        Assert.False(SingleInstanceGuard.SignalExisting(name));
        using SingleInstanceGuard? again = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public void InstanceNames_DependOnTheSettingsFolder_GuiAndTrayDiffer()
    {
        InstanceNames user = new(@"C:\Users\me\AppData\Roaming\JoyPro2");
        Assert.Equal(user.Gui, new InstanceNames(@"c:\users\ME\AppData\Roaming\JoyPro2\").Gui);
        Assert.NotEqual(user.Gui, new InstanceNames(@"C:\Temp\JoyPro2.Gui.Tests\data-1\settings").Gui);
        Assert.StartsWith("JoyPro2.Gui.", user.Gui);
        Assert.StartsWith("JoyPro2.Tray.", user.Tray);
        Assert.Equal(user.Gui["JoyPro2.Gui.".Length..], user.Tray["JoyPro2.Tray.".Length..]);
        Assert.Equal(user.Gui, SingleInstanceGuard.NameFor(@"C:\Users\me\AppData\Roaming\JoyPro2"));
        // The test run's names never meet the user's JoyPro2.
        Assert.NotEqual(new InstanceNames(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JoyPro2")).Gui,
            InstanceNames.Current.Gui);
    }

    [Fact]
    public void GuiPresence_SeesTheGuiStart_ByItsMutexAndAnnouncement()
    {
        string folder = TempFolder();
        try
        {
            InstanceNames names = new(folder);
            using GuiPresence presence = new(names);
            Assert.False(presence.IsRunning);
            using ManualResetEventSlim started = new(false);
            presence.Started += started.Set;
            presence.Start();
            Assert.False(started.Wait(200));

            // A "GUI" (this test process) takes its single-instance mutex and announces itself.
            using (SingleInstanceGuard? gui = SingleInstanceGuard.TryAcquire(names.Gui))
            {
                Assert.NotNull(gui);
                Assert.True(presence.IsRunning);
                GuiPresence.Announce(names);
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(Environment.ProcessId.ToString(), File.ReadAllText(names.GuiPidFile).Trim());
                Assert.False(TrayHostLauncher.IsRunning(names));
            }
            Assert.False(presence.IsRunning);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // ---- Shared sync output -------------------------------------------------------------------------------------------

    [Fact]
    public void SyncOutput_ParsesWatchedConfigs()
    {
        string output = "Loading...\n" + StatusJson;
        List<SyncStatusEntry> entries = SyncOutput.ParseStatus(output)!;
        SyncStatusEntry dcs = Assert.Single(entries);
        Assert.Equal("DCS|C:\\Saved Games\\DCS", dcs.WatchKey);
        Assert.Equal("DCS World", dcs.DisplayName);
        Assert.True(dcs.Changed);
        Assert.Equal(dcs.WatchKey, dcs.Spec.GameId);
        GameWatchFolder folder = Assert.Single(dcs.Spec.Folders);
        Assert.Equal(["*.diff.lua", "modifiers.lua"], folder.Filters);
        Assert.True(folder.IncludeSubdirectories);
        Assert.Equal(["DCS"], dcs.Spec.ProcessNames);

        Assert.Null(SyncOutput.ParseStatus("no json"));
        Assert.True(SyncOutput.StatusSaysChanged(StatusJson));
        Assert.False(SyncOutput.StatusSaysChanged("{\"Games\":[{\"State\":\"unchanged\"}]}"));
        (string DisplayName, int Changes) summary = SyncOutput.ParseDiffSummary(TrayFakeEngine.DiffJson(2))!.Value;
        Assert.Equal("DCS World", summary.DisplayName);
        Assert.Equal(2, summary.Changes);
        Assert.Null(SyncOutput.ParseDiffSummary("nothing"));
        Assert.Equal(["--game", "DCS", "--path", "C:\\Saved Games\\DCS"], SyncOutput.GameArguments("DCS|C:\\Saved Games\\DCS"));
        Assert.Equal(["--game", "NuclearOption"], SyncOutput.GameArguments("NuclearOption"));
        Assert.Equal("{\"Suggestions\":[]}", SyncOutput.ExtractJson("{\"Suggestions\":[]}\nAuto-saved profile."));
    }

    internal const string StatusJson =
        "{\"Games\":[" +
        "{\"Game\":\"DCS\",\"DisplayName\":\"DCS World\",\"Installed\":true,\"Disabled\":false,\"Tracked\":true,\"State\":\"changed\",\"ConfigPath\":\"C:\\\\Saved Games\\\\DCS\"," +
        "\"Watch\":[{\"Directory\":\"C:\\\\Saved Games\\\\DCS\\\\Config\\\\Input\",\"Filters\":[\"*.diff.lua\",\"modifiers.lua\"],\"IncludeSubdirectories\":true}],\"RegistryKey\":\"\",\"ProcessNames\":[\"DCS\"]}," +
        "{\"Game\":\"IL2Game\",\"Installed\":true,\"Tracked\":false,\"State\":\"untracked\",\"ConfigPath\":\"H:\\\\IL-2\"}," +
        "{\"Game\":\"WarThunder\",\"Installed\":true,\"Disabled\":true,\"Tracked\":true,\"State\":\"disabled\",\"ConfigPath\":\"x\"}," +
        "{\"Game\":\"MSFS\",\"Installed\":true,\"Tracked\":true,\"State\":\"missing\",\"ConfigPath\":\"y\"}]}";

    // ---- Helpers --------------------------------------------------------------------------------------------------------

    private static string TempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "tray-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static MainWindowViewModel CreateVm(TrayFakeEngine? engine = null)
    {
        return new MainWindowViewModel(engine ?? new TrayFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
    }

    private static IRunKeyStore StoreOf(AutostartService service) =>
        (IRunKeyStore)typeof(AutostartService).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static string? ReadRealRunValue()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(WindowsRunKeyStore.RunKeyPath, false);
        return key?.GetValue(AutostartService.ValueName) as string;
    }

    private static bool Seq(IReadOnlyList<string> args, params string[] sequence) => AxisTuningTests.Seq(args, sequence);

    private sealed class TrayFakeEngine : IEngineCliClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = Output(arguments),
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            });
        }

        internal static string DiffJson(int changes) =>
            "{\"Game\":\"DCS\",\"DisplayName\":\"DCS World\",\"Changes\":[" +
            string.Join(",", Enumerable.Range(1, changes).Select(i =>
                "{\"Id\":\"c" + i + "\",\"Kind\":\"added\",\"Description\":\"F-16C_50: change " + i + "\",\"Candidates\":[\"Weapon Release\"],\"Suggested\":\"Weapon Release\",\"NewRelationName\":\"Weapon Release 2\"}")) +
            "]}";

        // What engine sync diff lists (0: only things JoyPro does not manage) and what sync status says for a game.
        public int DiffChanges { get; set; } = 2;

        public string GameState { get; set; } = "changed";

        private string Output(IReadOnlyList<string> args)
        {
            if (Seq(args, "sync", "status"))
                return args.Contains("--game") ? "{\"Games\":[{\"Game\":\"DCS\",\"State\":\"" + GameState + "\"}]}" : "{\"Games\":[]}";
            if (Seq(args, "sync", "diff"))
                return DiffJson(DiffChanges);
            return "{}";
        }
    }
}
