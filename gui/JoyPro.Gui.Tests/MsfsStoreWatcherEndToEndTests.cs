using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Watch;
using Xunit;
using Xunit.Abstractions;

namespace JoyPro.Gui.Tests;

// The game config watcher end to end for MSFS 2024 from the Microsoft Store (2.2.7 / 2.2.8 report "no prompt came up in
// JP at all re changes made in MSFS"), with the real engine and the GUI's view model on stand-in user folders
// (JOYPRO_TEST_MSFS_HOME): a Store save folder ("wgs") built as the Xbox app keeps it, an import or export that records
// the game, the view model's own watcher (engine sync status -> GameConfigWatcher), a stand-in sim process (a copy of
// PING.EXE; JOYPRO_TEST_GAME_PROCESSES makes the engine report its name instead of FlightSimulator2024, or none, so a
// sim the developer plays meanwhile does not take part), the sim's save as the 2.2.7 tester logs show it (a new blob
// under a new GUID name, container.<n+1>, containers.index deleted and written again, the old container file and blob
// deleted, more index rewrites after the sim closed), then the prompt and the take-over.
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class MsfsStoreWatcherEndToEndTests : IDisposable
{
    private const string Family = "Microsoft.Limitless_8wekyb3d8bbwe";
    private const string Vmax = "VPC VMAX Prime Throttle {0000CCCC-0000-4000-8000-000000000301}";

    private readonly string root;
    private readonly string home;
    private readonly string engine;
    private readonly string profile;
    private readonly Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITestOutputHelper output;
    private Process? sim;

    public MsfsStoreWatcherEndToEndTests(ITestOutputHelper output)
    {
        this.output = output;
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        engine = Path.Combine(dir!.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        // Short: the engine (.NET Framework) reads no path over 260 characters, and a blob path is long.
        root = Path.Combine(Path.GetTempPath(), "jpw" + Guid.NewGuid().ToString("N")[..8]);
        home = Path.Combine(root, "h");
        profile = Path.Combine(root, "p.pr0file");
        Directory.CreateDirectory(root);
        string devices = Path.Combine(root, "devices.tsv");
        File.WriteAllText(devices, Vmax + "\t3344\t019D\tVPC VMAX Prime Throttle\r\n");
        environment["JOYPRO_DATA_DIR"] = Path.Combine(root, "data");
        environment["JOYPRO_TEST_MSFS_HOME"] = home;
        environment["JOYPRO_INSTALL_DETECTION"] = "off";
        environment["JOYPRO_NO_INTERNET_PICTURES"] = "1";
        environment["JOYPRO_LOG_DIR"] = Path.Combine(root, "logs");
        environment["JOYPRO_LOG_LEVEL"] = "debug";
        environment["JOYPRO_FAKE_DEVICES"] = devices;
        // The GUI side's log (process-wide, as in WatcherLogTests: this class runs with the tray tests).
        JoyPro.Logging.JoyProLog.Stop();
        JoyPro.Logging.JoyProLog.Start("gui", "test", null, Path.Combine(root, "guilogs"));
    }

    public void Dispose()
    {
        JoyPro.Logging.JoyProLog.Stop();
        try
        {
            if (sim != null && !sim.HasExited)
                sim.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        sim?.Dispose();
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // After an export: the sim (found by its exe in the Store install folder, whatever the exe is called) binds parking
    // brakes to button 9 and saves; nothing is checked while it runs, the change is offered once after it closed (and
    // after the Xbox app's later index rewrites), the take-over puts the bind into the profile.
    [Fact]
    public async Task StoreSave_AfterExport_SimFoundByItsInstallFolder_ChangeOfferedOnce_AndTakenOver()
    {
        WgsSave save = SetUpStoreSim();
        Assert.True(Engine("--profile", profile, "msfs2024", "import", "--text").Code == 0);
        (int exportCode, string export) = Engine("--profile", profile, "msfs2024", "export", Package(), "--text");
        Assert.True(exportCode == 0, export);
        JsonElement status = StatusOf("MSFS2024");
        Assert.True(status.GetProperty("Tracked").GetBoolean(), status.ToString());
        Assert.Equal("export", status.GetProperty("Reason").GetString());
        Assert.Equal("unchanged", status.GetProperty("State").GetString());
        // What the watcher gets: the Store save folder with its subfolders, the install folder for the process, a settle time.
        Assert.Contains(status.GetProperty("Watch").EnumerateArray(), w => w.GetProperty("Directory").GetString() == save.Wgs && w.GetProperty("IncludeSubdirectories").GetBoolean() &&
                                                                          w.GetProperty("Filters").EnumerateArray().Any(f => f.GetString() == "containers.index"));
        string content = Path.Combine(home, "XboxGames", "Microsoft Flight Simulator 2024", "Content");
        Assert.Contains(content + "\\", status.GetProperty("ProcessFolders").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(10, status.GetProperty("SettleSeconds").GetInt32());
        Assert.Empty(status.GetProperty("ProcessNames").EnumerateArray());

        (MainWindowViewModel vm, RecordingEngine client, List<string> notices) = await StartGuiAsync();
        try
        {
            // The sim starts (from the Store install folder, as gamelaunchhelper.exe) and saves the changed profile.
            StartSim(Path.Combine(content, "gamelaunchhelper.exe"));
            save.Save("InputProfile_1", Airplane(gearButton: 5, parkingBrakeButton: 9));
            await Task.Delay(TimeSpan.FromSeconds(13));
            Assert.Equal(0, client.Count("sync", "diff"));
            Assert.False(vm.HasGameSyncChanges);
            Assert.Contains("config changed while the game runs", GuiLog());
            Assert.Contains("found by exe in the game's folder", GuiLog());

            // The sim closes; the Xbox app writes the index twice more (3 and 6 s later): one check after the last write.
            StopSim();
            await Task.Delay(TimeSpan.FromSeconds(3));
            save.RewriteIndex();
            await Task.Delay(TimeSpan.FromSeconds(3));
            save.RewriteIndex();
            Assert.True(await WaitForAsync(() => vm.HasGameSyncChanges, 90), "no prompt: " + Environment.NewLine + GuiLog() + EngineLog());
            await Task.Delay(TimeSpan.FromSeconds(4));
            Assert.Equal(1, client.Count("sync", "diff"));
            Assert.Single(notices);
            GameSyncChangeItem change = Assert.Single(vm.GameSyncChanges);
            Assert.Equal("added", change.Kind);
            Assert.Contains("JOY_BTN9", change.Description);
            Assert.Contains("the user is asked", GuiLog());

            await vm.TakeOverGameChangesCommand.ExecuteAsync(null);
            Assert.False(vm.HasGameSyncChanges);
            (_, string binds) = Engine("--profile", profile, "binds", "list", "--text");
            Assert.Contains("JOY_BTN9", binds);
            Assert.Equal("unchanged", StatusOf("MSFS2024").GetProperty("State").GetString());
            output.WriteLine(GuiLog());
        }
        finally
        {
            vm.StopGameConfigWatcher();
        }
    }

    // Only imported (never exported, so no JoyPro package beside the save): the sim (found by its process name) removes
    // the ATC menu bind of the tester's report; the diff reports the removal, the prompt offers it, the take-over removes
    // the bind from the profile.
    [Fact]
    public async Task StoreSave_AfterImport_BindRemovedInTheSim_IsOffered_AndTakeOverRemovesIt()
    {
        WgsSave save = SetUpStoreSim();
        string name = "jpmsfs" + Guid.NewGuid().ToString("N")[..8];
        environment["JOYPRO_TEST_GAME_PROCESSES"] = "MSFS2024=" + name;
        Assert.True(Engine("--profile", profile, "msfs2024", "import", "--text").Code == 0);
        JsonElement status = StatusOf("MSFS2024");
        Assert.Equal("import", status.GetProperty("Reason").GetString());
        Assert.Equal([name], status.GetProperty("ProcessNames").EnumerateArray().Select(n => n.GetString()));
        Assert.Contains("KEY_ATC_MENU_0", File.ReadAllText(profile));

        (MainWindowViewModel vm, RecordingEngine client, List<string> notices) = await StartGuiAsync();
        try
        {
            StartSim(Path.Combine(root, name + ".exe"));
            save.Save("InputProfile_2", General(atcButton: null));
            await Task.Delay(TimeSpan.FromSeconds(12));
            Assert.False(vm.HasGameSyncChanges);
            Assert.Contains("found by process name " + name, GuiLog());
            StopSim();
            Assert.True(await WaitForAsync(() => vm.HasGameSyncChanges, 90), "no prompt: " + Environment.NewLine + GuiLog() + EngineLog());
            Assert.Single(notices);
            GameSyncChangeItem change = Assert.Single(vm.GameSyncChanges);
            Assert.Equal("removed", change.Kind);
            Assert.Contains("no longer on", change.Description);
            Assert.Contains("JOY_BTN2", change.Description);

            await vm.TakeOverGameChangesCommand.ExecuteAsync(null);
            Assert.False(vm.HasGameSyncChanges);
            Assert.DoesNotContain("KEY_ATC_MENU_0", File.ReadAllText(profile));
            (int diffCode, string diff) = Engine("--profile", profile, "sync", "diff", "--game", "MSFS2024");
            Assert.True(diffCode == 0, diff);
            Assert.Contains("\"Changes\":[]", diff);
            Assert.Equal(1, client.Count("sync", "apply"));
        }
        finally
        {
            vm.StopGameConfigWatcher();
        }
    }

    // The tester's case after an export: JoyPro's package still has the bind for the same device and category the user's
    // own profile in the Store save had; the sim removes it from the user's profile. The user's profile is the truth, so
    // the removal must be offered. Needs the profile choice of MsfsIntegration.LoadSelectedProfiles that reads no package
    // preset beside a device+category's own profile (another branch): until then the package's copy hides the removal.
    [Fact]
    public void StoreSave_AfterExport_BindRemovedInTheSim_IsReportedDespiteThePackagePreset()
    {
        WgsSave save = SetUpStoreSim();
        Assert.True(Engine("--profile", profile, "msfs2024", "import", "--text").Code == 0);
        (int exportCode, string export) = Engine("--profile", profile, "msfs2024", "export", Package(), "--text");
        Assert.True(exportCode == 0, export);
        Assert.Contains(Directory.GetFiles(Package(), "*", SearchOption.AllDirectories), f => File.ReadAllText(f).Contains("KEY_ATC_MENU_0"));
        save.Save("InputProfile_2", General(atcButton: null));
        Assert.Equal("changed", StatusOf("MSFS2024").GetProperty("State").GetString());
        (int code, string diff) = Engine("--profile", profile, "sync", "diff", "--game", "MSFS2024", "--text");
        Assert.True(code == 0, diff);
        Assert.Contains("removed", diff);
        Assert.Contains("JOY_BTN2", diff);
    }

    // The GUI's path box after "Detect" holds the first discovered path, and the import passes it as --path: the game is
    // recorded (it used to stay untracked, so never watched). An export to the Community2024 folder and one to the package
    // folder are one record: the Store save is watched once (two records gave two watch keys, two checks, two prompts).
    [Fact]
    public void Import_WithTheDetectedPath_RecordsTheGame_ExportsToCommunityOrPackageAreOneWatch()
    {
        SetUpStoreSim();
        (int pathsCode, string paths) = Engine("msfs2024", "paths");
        Assert.True(pathsCode == 0, paths);
        using JsonDocument doc = JsonDocument.Parse(SyncOutput.ExtractJson(paths));
        string detected = doc.RootElement.GetProperty("ConfigPaths")[0].GetString()!;
        Assert.EndsWith("joypro-input-profiles", detected);
        Assert.True(Engine("--profile", profile, "msfs2024", "import", "--path", detected, "--text").Code == 0);
        JsonElement status = StatusOf("MSFS2024");
        Assert.True(status.GetProperty("Tracked").GetBoolean(), status.ToString());
        Assert.Equal("import", status.GetProperty("Reason").GetString());

        // (An export given the Community2024 folder records the package folder too: MsfsIntegration's path resolution,
        // another branch; this test exports to the package folder, which works on both.)
        Assert.True(Engine("--profile", profile, "msfs2024", "export", "--path", detected, "--text").Code == 0);
        Assert.Equal("export", StatusOf("MSFS2024").GetProperty("Reason").GetString());
        (int code, string all) = Engine("sync", "status");
        Assert.True(code == 0, all);
        SyncStatusEntry entry = Assert.Single(SyncOutput.ParseStatus(all)!, e => e.GameId == "MSFS2024");
        Assert.Equal(detected, entry.ConfigPath);
    }

    // ---- The stand-in Store install -------------------------------------------------------------------------------------

    private string Community() => Path.Combine(home, "Local", "Packages", Family, "LocalCache", "Packages", "Community2024");

    // JoyPro's package in it: the first path discovery gives (what the GUI's "Detect" fills in) and the export target.
    private string Package() => Path.Combine(Community(), "joypro-input-profiles");

    private WgsSave SetUpStoreSim()
    {
        string localCache = Path.Combine(home, "Local", "Packages", Family, "LocalCache");
        Directory.CreateDirectory(Community());
        File.WriteAllText(Path.Combine(localCache, "UserCfg.opt"), "InstalledPackagesPath \"" + Path.Combine(localCache, "Packages") + "\"\r\n");
        Directory.CreateDirectory(Path.Combine(home, "XboxGames", "Microsoft Flight Simulator 2024", "Content"));
        WgsSave save = WgsSave.Create(Path.Combine(home, "Local", "Packages", Family, "SystemAppData", "wgs"), Family,
            [("InputProfile_1", Airplane(gearButton: 5, parkingBrakeButton: null)), ("InputProfile_2", General(atcButton: 2))]);
        Assert.True(Engine("games", "install", "set", "MSFS2024", Path.Combine(home, "Local", "Packages", Family)).Code == 0);
        // The sim process: none by name (only the install folder counts) unless a test names its stand-in.
        environment["JOYPRO_TEST_GAME_PROCESSES"] = "MSFS2024=";
        return save;
    }

    private static string Key(int button) => "\t\t\t<Primary>\n\t\t\t\t<KEY Information=\"Joystick Button " + button + "\">" + (button - 1) + "</KEY>\n\t\t\t</Primary>\n";

    private static string Action(string name, int? button) =>
        button == null
            ? "\t\t<Action ActionName=\"" + name + "\" ValueEvent=\"0.000000\" Delay=\"0.000000\" Flag=\"2\"/>\n"
            : "\t\t<Action ActionName=\"" + name + "\" ValueEvent=\"0.000000\" Delay=\"0.000000\" Flag=\"2\">\n" + Key(button.Value) + "\t\t</Action>\n";

    private static string Device(string friendly, string category, string contexts) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Version Num=\"-1\"/>\n<FriendlyName PlatformAvailability=\"1\" Locked=\"false\">" + friendly + "</FriendlyName>\n" +
        "<Device DeviceName=\"VPC VMAX Prime Throttle\" GUID=\"{0000CCCC-0000-4000-8000-000000000301}\" ProductID=\"413\" CompositeID=\"0\" HWVer=\"1.0.0.0\">\n" +
        "\t<AircraftInfo CategoryName=\"" + category + "\"/>\n" + contexts + "</Device>\n";

    // The airplane profile: flaps axis, gear on a button, parking brakes listed (bound or not).
    private static byte[] Airplane(int gearButton, int? parkingBrakeButton) => Encoding.UTF8.GetBytes(Device("VMAX Planes", "AIRPLANE",
        "\t<Context ContextName=\"AIRCRAFT\">\n" +
        "\t\t<Action ActionName=\"KEY_AXIS_FLAPS_SET\" ValueEvent=\"0.000000\" Delay=\"0.000000\" Flag=\"4\">\n\t\t\t<Primary>\n\t\t\t\t<KEY Information=\"Joystick R-Axis Z \">802</KEY>\n\t\t\t</Primary>\n\t\t</Action>\n" +
        Action("KEY_GEAR_TOGGLE", gearButton) + Action("KEY_PARKING_BRAKES", parkingBrakeButton) + "\t</Context>\n"));

    // The General (Transversal) profile: the ATC menu choice of the tester's report and the cockpit reset.
    private static byte[] General(int? atcButton) => Encoding.UTF8.GetBytes(Device("VMAX General", "TRANSVERSAL",
        "\t<Context ContextName=\"ATC\">\n" + Action("KEY_ATC_MENU_0", atcButton) + Action("KEY_ATC_MENU_1", null) + "\t</Context>\n" +
        "\t<Context ContextName=\"MODES\">\n" + Action("KEY_COCKPIT_RESET", 10) + "\t</Context>\n"));

    private void StartSim(string exe)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe, true);
        sim = Process.Start(new ProcessStartInfo(exe, "-n 600 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        Assert.False(sim.HasExited);
    }

    private void StopSim()
    {
        sim!.Kill();
        Assert.True(sim.WaitForExit(10000));
    }

    // ---- The GUI with the real engine -----------------------------------------------------------------------------------

    private async Task<(MainWindowViewModel Vm, RecordingEngine Client, List<string> Notices)> StartGuiAsync()
    {
        RecordingEngine client = new(environment);
        MainWindowViewModel vm = new(client, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = engine,
            UseLoadCorrections = false,
            // No UI thread here: the watcher's event is handled at once.
            GameSyncPost = action => action()
        };
        vm.ProfilePath = profile;
        List<string> notices = [];
        vm.GameChangesOffered += (title, text) => { lock (notices) notices.Add(title + ": " + text); };
        await vm.StartGameConfigWatcherAsync();
        Assert.True(vm.WatchedConfigKeys.Any(k => k.StartsWith("MSFS2024|", StringComparison.Ordinal)), "MSFS 2024 is not watched: " + vm.GameWatchStatus + Environment.NewLine + GuiLog());
        return (vm, client, notices);
    }

    // The GUI side's log of this test (watcher and view model).
    private string GuiLog() => ReadLogs(Path.Combine(root, "guilogs"), "gui-*.log");

    // The engine's log of this test (sync status, diff, apply).
    private string EngineLog() => ReadLogs(Path.Combine(root, "logs"), "engine-*.log");

    private static string ReadLogs(string folder, string pattern)
    {
        JoyPro.Logging.JoyProLog.Flush();
        StringBuilder text = new();
        foreach (string file in Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : [])
            text.AppendLine(ReadShared(file));
        return text.ToString();
    }

    private static string ReadShared(string file)
    {
        using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int seconds)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (condition())
                return true;
            await Task.Delay(100);
        }
        return condition();
    }

    private JsonElement StatusOf(string game)
    {
        (int code, string text) = Engine("sync", "status", "--game", game);
        Assert.True(code == 0, text);
        using JsonDocument doc = JsonDocument.Parse(SyncOutput.ExtractJson(text));
        return doc.RootElement.GetProperty("Games").EnumerateArray().First(g => g.GetProperty("Game").GetString() == game).Clone();
    }

    private (int Code, string Output) Engine(params string[] args)
    {
        Assert.True(File.Exists(engine), "Build the engine first: " + engine);
        ProcessStartInfo info = new(engine)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(engine)!
        };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        foreach (KeyValuePair<string, string> variable in environment)
            info.Environment[variable.Key] = variable.Value;
        using Process process = Process.Start(info)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(120000), "engine " + string.Join(" ", args) + " did not finish.");
        return (process.ExitCode, stdout + stderr.Result);
    }

    // The real engine for the view model, with the test's environment, every call recorded.
    private sealed class RecordingEngine(Dictionary<string, string> environment) : IEngineCliClient
    {
        private readonly EngineCliClient real = new();
        private readonly List<IReadOnlyList<string>> calls = [];

        public int Count(params string[] sequence)
        {
            lock (calls)
                return calls.Count(c => AxisTuningTests.Seq(c, sequence));
        }

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            RunStreamingAsync(engineExePath, arguments, new EngineRunOptions(), cancellationToken);

        public Task<EngineCommandResult> RunStreamingAsync(string engineExePath, IReadOnlyList<string> arguments, EngineRunOptions options, CancellationToken cancellationToken = default)
        {
            lock (calls)
                calls.Add(arguments.ToArray());
            Dictionary<string, string> merged = new(environment, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> variable in options?.Environment ?? new Dictionary<string, string>())
                merged[variable.Key] = variable.Value;
            return real.RunStreamingAsync(engineExePath, arguments, new EngineRunOptions { StandardErrorLine = options?.StandardErrorLine, Environment = merged }, cancellationToken);
        }
    }

    // The Xbox connected storage of the sim as the engine reads it (engine\Games\MsfsUserProfiles.cs, WgsStore), saved the
    // way the 2.2.7 tester logs show: a new blob under a new GUID name, container.<n+1> listing it, containers.index
    // deleted and created again (the Xbox app does it more often after the sim closed), the old container file and blob
    // deleted. The container folders keep their GUID names.
    private sealed class WgsSave
    {
        private sealed class Container
        {
            public string Name = string.Empty;
            public Guid Folder;
            public byte Number;
            public Guid Blob;
        }

        private readonly List<Container> containers = [];
        private string family = string.Empty;
        private long time = 133700000000000000L;

        public string Wgs { get; private set; } = string.Empty;

        public string User { get; private set; } = string.Empty;

        public static WgsSave Create(string wgs, string family, IReadOnlyList<(string Name, byte[] Bytes)> blobs)
        {
            WgsSave save = new() { Wgs = wgs, User = Path.Combine(wgs, "0009000A1B2C3D_69F80140"), family = family };
            Directory.CreateDirectory(save.User);
            foreach ((string name, byte[] bytes) in blobs)
            {
                Container container = new() { Name = name, Folder = Guid.NewGuid(), Number = 1, Blob = Guid.NewGuid() };
                Directory.CreateDirectory(save.FolderOf(container));
                File.WriteAllBytes(save.BlobOf(container), bytes);
                File.WriteAllBytes(save.ContainerFileOf(container), ContainerBytes(container.Blob));
                save.containers.Add(container);
            }
            File.WriteAllBytes(Path.Combine(save.User, "containers.index"), save.IndexBytes());
            return save;
        }

        public void Save(string name, byte[] bytes)
        {
            Container container = containers.Single(c => c.Name == name);
            string oldBlob = BlobOf(container);
            string oldContainer = ContainerFileOf(container);
            container.Blob = Guid.NewGuid();
            container.Number++;
            File.WriteAllBytes(BlobOf(container), bytes);
            File.WriteAllBytes(ContainerFileOf(container), ContainerBytes(container.Blob));
            RewriteIndex();
            File.Delete(oldContainer);
            File.Delete(oldBlob);
        }

        // containers.index deleted and written again (new times), as the sim and the Xbox app do.
        public void RewriteIndex()
        {
            time += 10000000L;
            string index = Path.Combine(User, "containers.index");
            File.Delete(index);
            File.WriteAllBytes(index, IndexBytes());
        }

        private string FolderOf(Container c) => Path.Combine(User, c.Folder.ToString("N").ToUpperInvariant());

        private string BlobOf(Container c) => Path.Combine(FolderOf(c), c.Blob.ToString("N").ToUpperInvariant());

        private string ContainerFileOf(Container c) => Path.Combine(FolderOf(c), "container." + c.Number);

        private static byte[] ContainerBytes(Guid blob)
        {
            using MemoryStream list = new();
            using BinaryWriter w = new(list);
            w.Write(4u);
            w.Write(1);
            byte[] name = new byte[128];
            byte[] nameBytes = Encoding.Unicode.GetBytes("data");
            Array.Copy(nameBytes, name, nameBytes.Length);
            w.Write(name);
            w.Write(blob.ToByteArray());
            w.Write(blob.ToByteArray());
            w.Flush();
            return list.ToArray();
        }

        private byte[] IndexBytes()
        {
            using MemoryStream index = new();
            using BinaryWriter w = new(index);
            w.Write(14u);
            w.Write(containers.Count);
            w.Write(0u);
            WriteString(w, family.Replace("_8wekyb3d8bbwe", "_1.0.0.0_x64__8wekyb3d8bbwe") + "!App");
            w.Write((ulong)time);
            w.Write(0u);
            WriteString(w, Guid.Empty.ToString());
            w.Write(0UL);
            foreach (Container container in containers)
            {
                WriteString(w, container.Name);
                WriteString(w, container.Name);
                WriteString(w, "\"0x8DCC4B5A1E2F3A4\"");
                w.Write(container.Number);
                w.Write(0u);
                w.Write(container.Folder.ToByteArray());
                w.Write((ulong)time);
                w.Write(new byte[16]);
            }
            w.Flush();
            return index.ToArray();
        }

        private static void WriteString(BinaryWriter w, string text)
        {
            w.Write(text.Length);
            w.Write(Encoding.Unicode.GetBytes(text));
        }
    }
}
