using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// MSFS 2020 / 2024 import of the user's own input profiles (docs/games/msfs.md "Import"), with the real engine on
// stand-in user folders (JOYPRO_TEST_MSFS_HOME: <home>\Local = %LOCALAPPDATA%, <home>\Roaming = %APPDATA%,
// <home>\Steam = the Steam install): Microsoft Store save containers ("wgs": containers.index, container.<n>, blobs
// named by GUID) built here like the Xbox app writes them, Steam remote folders, JoyPro's preset package besides, and
// a fresh data folder (smoke\msfsuserprofiles.ps1 also builds the command list on demand in a release-like folder). The profiles are the fixtures of
// engine\tests\fixtures\msfs\user-profiles (anonymised real MSFS 2020 files; MSFS 2024 files in the sim's format).
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class MsfsProfileImportTests : IDisposable
{
    private readonly string root;
    private readonly string engine;
    private readonly string fixtures;
    // JOYPRO_FAKE_DEVICES of the engine runs (null: no device connected).
    private string? connectedDevices;

    public MsfsProfileImportTests()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        engine = Path.Combine(dir!.FullName, "engine", "bin", "x64", "Debug", "engine.exe");
        fixtures = Path.Combine(dir.FullName, "engine", "tests", "fixtures", "msfs", "user-profiles");
        // Short: the engine (.NET Framework) reads no path over 260 characters, and a blob path is long.
        root = Path.Combine(Path.GetTempPath(), "jpm" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
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

    [Fact]
    public void Store2024_WithoutJoyProPackage_ImportsTheUsersOwnProfiles()
    {
        // The report: Microsoft Store MSFS 2024, Community2024 without JoyPro's package, a fresh JoyPro (no command list).
        string home = Path.Combine(root, "h");
        string localCache = Path.Combine(home, "Local", "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache");
        string packages = Path.Combine(localCache, "Packages");
        Directory.CreateDirectory(Path.Combine(packages, "Community2024"));
        File.WriteAllText(Path.Combine(localCache, "UserCfg.opt"), "InstalledPackagesPath \"" + packages + "\"\r\n");
        string wgs = StoreWgs(home, 2024);
        DateTime now = DateTime.Now;
        WgsWriter.Write(wgs, "Microsoft.Limitless_8wekyb3d8bbwe",
        [
            new("InputProfile_1", [new("data", Fixture2024("x56-stick-custom.xml"), now.AddMinutes(-5))]),
            new("InputProfile_2", [new("data", Fixture2024("x56-stick-older.xml"), now.AddDays(-30))]),
            new("InputProfile_3", [new("data", Fixture2024("x56-stick-helicopter.xml"), now.AddDays(-2))]),
            new("InputProfile_4", [new("data", Zlib(Fixture2024("crosswind-2024.xml")), now.AddDays(-1))]),
            new("InputProfile_5", [new("data", Fixture2024("keyboard-2024.xml"), null)]),
            new("Settings", [new("data", Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray(), null)])
        ]);
        Engine(home, "games", "install", "set", "MSFS2024", Path.Combine(home, "Local", "Packages", "Microsoft.Limitless_8wekyb3d8bbwe"));

        (int code, string output) = Engine(home, "--profile", Path.Combine(root, "p.pr0file"), "msfs2024", "import", "--text");

        Assert.True(code == 0, output);
        Assert.Contains("joypro-input-profiles", output);
        Assert.Contains("MSFS device profiles read: 3 from the Microsoft Store version", output);
        Assert.Contains("imported the most recently saved one, 'X-56 Stick Custom'", output);
        Assert.Contains("KEY_AXIS_ELEVATOR_SET <- Saitek Pro Flight X-56 Rhino Stick {0000BBBB-0000-4000-8000-000000000201} JOY_Y", output);
        Assert.Contains("KEY_ROTOR_BRAKE_TOGGLE <- Saitek Pro Flight X-56 Rhino Stick {0000BBBB-0000-4000-8000-000000000201} JOY_BTN4", output);
        Assert.Contains("KEY_AXIS_RUDDER_SET <- MFG Crosswind V2 {0000BBBB-0000-4000-8000-000000000202} JOY_RZ", output);
        Assert.DoesNotContain("KEY_FLAPS_UP", output);
        // The camera bind of the profile (General category) is imported too.
        Assert.Contains("KEY_COCKPIT_QUICKVIEW_LEFT <- Saitek Pro Flight X-56 Rhino Stick {0000BBBB-0000-4000-8000-000000000201} JOY_BTN_POV1_L", output);
        Assert.DoesNotContain("non-flight contexts left out", output);
        // No device connected (the test's empty device list): every device's profile is imported.
        Assert.Contains("No game device is connected: the MSFS profiles of all devices are imported.", output);
        Assert.Contains("1 MSFS keyboard, mouse or gamepad profile(s) not imported", output);
        Assert.DoesNotContain("has no presets yet", output);

        (_, string binds) = Engine(home, "--profile", Path.Combine(root, "p.pr0file"), "binds", "list", "--text");
        Assert.Contains("JOY_BTN6", binds);
        (_, string relations) = Engine(home, "--profile", Path.Combine(root, "p.pr0file"), "relations", "list", "--text");
        string elevator = relations.Split('\n').Select(l => l.Split('|')[0].Trim()).First(n => n.Contains("Elevator", StringComparison.OrdinalIgnoreCase) && n.Contains("Axis", StringComparison.OrdinalIgnoreCase) && !n.Contains("Trim", StringComparison.OrdinalIgnoreCase));
        (_, string tuning) = Engine(home, "--profile", Path.Combine(root, "p.pr0file"), "binds", "tuning", "show", elevator, "--game", "MSFS2024", "--text");
        Assert.Contains("n.axisDeadZone=6", tuning);
        Assert.Contains("n.sensitivityPlus=35", tuning);
        Assert.Contains("n.axisOverride=1", tuning);

        (int exportCode, string export) = Engine(home, "--profile", Path.Combine(root, "p.pr0file"), "msfs2024", "export", wgs, "--text");
        Assert.NotEqual(0, exportCode);
        Assert.Contains("read only", export);
    }

    // The 2.2.1 report (MSFS 2024): a VPC VMAX Prime Throttle with three airplane axes and five General binds (cockpit
    // view zoom axis, reset / toggle cockpit view, toggle external view, active pause) gave only the three axes, and
    // devices MSFS no longer lists (an old MT-50CM3 stick) came along. Now the General binds come in with their
    // contexts, a menu bind stays out, the old device only with --include-disconnected, and the export writes the
    // General binds into a Transversal preset of JoyPro's package (the airplane binds into the airplane preset).
    [Fact]
    public void Steam2024_GeneralBindsAreImported_DisconnectedDevicesSkipped_AndExportedToTheTransversalPreset()
    {
        string home = Path.Combine(root, "h");
        string remote = Path.Combine(home, "Steam", "userdata", "42", "2537590", "remote");
        Directory.CreateDirectory(remote);
        File.WriteAllBytes(Path.Combine(remote, "inputprofile_0000000301"), Fixture2024("vmax-throttle-airplane.xml"));
        File.WriteAllBytes(Path.Combine(remote, "inputprofile_0000000302"), Fixture2024("vmax-throttle-general.xml"));
        File.WriteAllBytes(Path.Combine(remote, "inputprofile_0000000303"), Fixture2024("old-cm3-stick.xml"));
        string roaming = Path.Combine(home, "Roaming", "Microsoft Flight Simulator 2024");
        string community = Path.Combine(roaming, "Packages", "Community2024");
        Directory.CreateDirectory(community);
        File.WriteAllText(Path.Combine(roaming, "UserCfg.opt"), "InstalledPackagesPath \"" + Path.Combine(roaming, "Packages") + "\"\r\n");
        connectedDevices = Path.Combine(root, "devices.tsv");
        File.WriteAllText(connectedDevices, "VPC VMAX Prime Throttle {0000CCCC-0000-4000-8000-000000000301}\t3344\t019D\tVPC VMAX Prime Throttle\r\n");
        Engine(home, "games", "install", "set", "MSFS2024", roaming);
        string vmax = "VPC VMAX Prime Throttle {0000CCCC-0000-4000-8000-000000000301}";
        string profile = Path.Combine(root, "p.pr0file");

        (int code, string output) = Engine(home, "--profile", profile, "msfs2024", "import", "--text");

        Assert.True(code == 0, output);
        Assert.Contains("KEY_AXIS_FLAPS_SET <- " + vmax + " JOY_RZ", output);
        Assert.Contains("KEY_THROTTLE1_AXIS_SET_EX1 <- " + vmax + " JOY_RX", output);
        Assert.Contains("KEY_THROTTLE2_AXIS_SET_EX1 <- " + vmax + " JOY_RY", output);
        Assert.Contains("KEY_COCKPIT_CAMERA_ZOOM_AXIS <- " + vmax + " JOY_SLIDER2", output);
        Assert.Contains("KEY_COCKPIT_RESET <- " + vmax + " JOY_BTN10", output);
        Assert.Contains("KEY_TOGGLE_COCKPIT_CAMERA <- " + vmax + " JOY_BTN13", output);
        Assert.Contains("KEY_TOGGLE_EXTERNAL_CAMERA <- " + vmax + " JOY_BTN11", output);
        Assert.Contains("KEY_ACTIVE_PAUSE_TOGGLE <- " + vmax + " JOY_BTN26", output);
        Assert.DoesNotContain("KEY_MENU_VALID <-", output);
        Assert.Contains("1 bind(s) in menu, world map and developer contexts left out", output);
        Assert.Contains("Profiles of 1 device(s) that are not connected were left out", output);
        Assert.Contains("VPC Stick MT-50CM3", output);
        Assert.DoesNotContain("KEY_AXIS_ELEVATOR_SET <-", output);

        // The General actions are in the command list with their contexts and readable titles.
        string catalog = File.ReadAllText(Path.Combine(root, "data", "GameData", "DB", "MSFS2024", "actions.tsv"));
        Assert.Matches(@"(?m)^KEY_COCKPIT_CAMERA_ZOOM_AXIS\tCockpit Camera Zoom Axis\taxis\tCOCKPIT_GLOBAL_CAMERA\r?$", catalog);
        Assert.Matches(@"(?m)^KEY_ACTIVE_PAUSE_TOGGLE\t[^\t]+\tbutton\tINGAME_UI\r?$", catalog);

        // The old device only on request.
        (int scanCode, string scan) = Engine(home, "msfs2024", "scan", "--include-disconnected", "--text");
        Assert.True(scanCode == 0, scan);
        Assert.Contains("KEY_AXIS_ELEVATOR_SET <- VPC Stick MT-50CM3 {0000CCCC-0000-4000-8000-000000000302} JOY_Y", scan);

        // Export: JoyPro's package gets an airplane and a Transversal preset of the throttle.
        (int exportCode, string export) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");
        Assert.True(exportCode == 0, export);
        string categories = Path.Combine(community, "joypro-input-profiles", "InputProfiles", "Categories");
        string general = Assert.Single(Directory.GetFiles(Path.Combine(categories, "Transversal"), "JoyPro_*_2024_Transversal.xml"));
        string planes = Assert.Single(Directory.GetFiles(Path.Combine(categories, "Airplane"), "JoyPro_*_2024_Planes.xml"));
        string generalText = File.ReadAllText(general);
        string planesText = File.ReadAllText(planes);
        Assert.Contains("ProductID=\"019D\"", generalText);
        Assert.Matches("(?s)<Context ContextName=\"COCKPIT_GLOBAL_CAMERA\">\\s*<Action ActionName=\"KEY_COCKPIT_CAMERA_ZOOM_AXIS\"[^>]*>.*?>530</KEY>", generalText);
        Assert.Matches("(?s)<Context ContextName=\"INGAME_UI\">\\s*<Action ActionName=\"KEY_ACTIVE_PAUSE_TOGGLE\"[^>]*>.*?>25</KEY>", generalText);
        Assert.Matches("(?s)<Context ContextName=\"MODES\">\\s*<Action ActionName=\"KEY_COCKPIT_RESET\"[^>]*>.*?>9</KEY>", generalText);
        Assert.Matches("(?s)<Context ContextName=\"VIEW_MODE_SWITCHES\">.*KEY_TOGGLE_EXTERNAL_CAMERA", generalText);
        Assert.DoesNotContain("KEY_THROTTLE1_AXIS_SET_EX1", generalText);
        Assert.DoesNotContain("KEY_MENU_VALID", generalText);
        Assert.Contains("KEY_THROTTLE1_AXIS_SET_EX1", planesText);
        Assert.Contains("KEY_AXIS_FLAPS_SET", planesText);
        Assert.DoesNotContain("KEY_COCKPIT_CAMERA_ZOOM_AXIS", planesText);
        Assert.DoesNotContain("KEY_ACTIVE_PAUSE_TOGGLE", planesText);
        Assert.DoesNotContain("no MSFS profile of the device holds", export);
        Assert.Contains("transversal/joypro_", File.ReadAllText(Path.Combine(community, "joypro-input-profiles", "layout.json")));

        // Read back from the package: the same binds (no change on a second export).
        (int againCode, string again) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");
        Assert.True(againCode == 0, again);
        Assert.Contains("The MSFS profiles already match.", again);
    }

    // MSFS 2020 (Steam, profiles edited in place): a General bind the profile lost goes back into its own context.
    [Fact]
    public void Steam2020_GeneralBind_IsWrittenBackIntoItsContext()
    {
        string home = Path.Combine(root, "h");
        string remote = Path.Combine(home, "Steam", "userdata", "42", "1250410", "remote");
        Directory.CreateDirectory(remote);
        string file = Path.Combine(remote, "inputprofile_0000000102");
        File.Copy(Path.Combine(fixtures, "steam2020", "inputprofile_0000000102"), file);
        string roaming = Path.Combine(home, "Roaming", "Microsoft Flight Simulator");
        Directory.CreateDirectory(roaming);
        Engine(home, "games", "install", "set", "MSFS2020", roaming);
        string profile = Path.Combine(root, "p.pr0file");

        (int code, string output) = Engine(home, "--profile", profile, "msfs2020", "import", remote, "--text");
        Assert.True(code == 0, output);
        Assert.Contains("KEY_COCKPIT_CAMERA_ZOOM_AXIS <- LEFT VPC Throttle MT-50 CM {0000AAAA-0000-4000-8000-000000000102} JOY_Z", output);

        // The camera zoom is gone from the file (cleared in the sim); the export puts it back where it was.
        string text = File.ReadAllText(file);
        string without = System.Text.RegularExpressions.Regex.Replace(text, "\\t<Context ContextName=\"COCKPIT_GLOBAL_CAMERA\">.*?</Context>\\r?\\n", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.NotEqual(text, without);
        File.WriteAllText(file, without);

        (int exportCode, string export) = Engine(home, "--profile", profile, "msfs2020", "export", remote, "--text");
        Assert.True(exportCode == 0, export);
        string after = File.ReadAllText(file);
        Assert.Matches("(?s)<Context ContextName=\"COCKPIT_GLOBAL_CAMERA\">\\s*<Action ActionName=\"KEY_COCKPIT_CAMERA_ZOOM_AXIS\"[^>]*>.*?>1058</KEY>", after);
        Assert.DoesNotContain("goes into PLANE", export);
    }

    [Fact]
    public void Store_UnreadableIndex_EveryBlobIsChecked_AndUtf16BlobsAreRead()
    {
        string home = Path.Combine(root, "h");
        string wgs = StoreWgs(home, 2024);
        string user = WgsWriter.Write(wgs, "Microsoft.Limitless_8wekyb3d8bbwe",
        [
            new("A", [new("data", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Encoding.UTF8.GetString(Fixture2024("crosswind-2024.xml")))).ToArray(), null)])
        ]);
        File.WriteAllBytes(Path.Combine(user, "containers.index"), [255, 255, 255, 255, 1, 2]);
        Engine(home, "games", "install", "set", "MSFS2024", Path.Combine(home, "Local", "Packages", "Microsoft.Limitless_8wekyb3d8bbwe"));

        (int code, string output) = Engine(home, "msfs2024", "scan", wgs, "--text");

        Assert.True(code == 0, output);
        Assert.Contains("every saved file there is checked instead", output);
        Assert.Contains("KEY_AXIS_RUDDER_SET <- MFG Crosswind V2", output);
    }

    [Fact]
    public void NoProfileAnywhere_ErrorNamesEverySearchedFolder()
    {
        string home = Path.Combine(root, "h");
        string remote = Path.Combine(home, "Steam", "userdata", "42", "2537590", "remote");
        Directory.CreateDirectory(remote);
        string roaming = Path.Combine(home, "Roaming", "Microsoft Flight Simulator 2024");
        Directory.CreateDirectory(Path.Combine(roaming, "Packages", "Community2024"));
        File.WriteAllText(Path.Combine(roaming, "UserCfg.opt"), "InstalledPackagesPath \"" + Path.Combine(roaming, "Packages") + "\"\r\n");
        string wgs = StoreWgs(home, 2024);
        WgsWriter.Write(wgs, "Microsoft.Limitless_8wekyb3d8bbwe", [new("Settings", [new("data", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17], null)])]);
        Engine(home, "games", "install", "set", "MSFS2024", roaming);

        (int code, string output) = Engine(home, "--profile", Path.Combine(root, "p.pr0file"), "msfs2024", "import", "--text");

        Assert.NotEqual(0, code);
        Assert.Contains("No Microsoft Flight Simulator 2024 input profiles found. Searched:", output);
        Assert.Contains(remote + " (no inputprofile_* file)", output);
        Assert.Contains(wgs + " (1 saved file(s), no input profile)", output);
        Assert.Contains("joypro-input-profiles (JoyPro's presets, made by an export)", output);
        Assert.DoesNotContain("has no presets yet", output);
    }

    [Fact]
    public void Steam2024_ReadsTheRemoteFolder_AndJoyProsPresetsBesides()
    {
        string home = Path.Combine(root, "h");
        string remote = Path.Combine(home, "Steam", "userdata", "42", "2537590", "remote");
        Directory.CreateDirectory(remote);
        File.WriteAllBytes(Path.Combine(remote, "inputprofile_0000000201"), Fixture2024("x56-stick-custom.xml"));
        string roaming = Path.Combine(home, "Roaming", "Microsoft Flight Simulator 2024");
        string preset = Path.Combine(roaming, "Packages", "Community2024", "joypro-input-profiles", "InputProfiles", "Categories", "Airplane");
        Directory.CreateDirectory(preset);
        File.WriteAllBytes(Path.Combine(preset, "JoyPro_vJoy_0000bbbb_2024_Planes.xml"), Fixture2024("joypro-preset-vjoy.xml"));
        File.WriteAllText(Path.Combine(roaming, "UserCfg.opt"), "InstalledPackagesPath \"" + Path.Combine(roaming, "Packages") + "\"\r\n");
        Engine(home, "games", "install", "set", "MSFS2024", roaming);
        byte[] before = File.ReadAllBytes(Path.Combine(remote, "inputprofile_0000000201"));

        (int code, string output) = Engine(home, "--profile", Path.Combine(root, "p.pr0file"), "msfs2024", "import", "--text");

        Assert.True(code == 0, output);
        Assert.Contains("MSFS device profiles read: 1 JoyPro preset(s) (Community2024 package), 1 from Steam", output);
        Assert.Contains("KEY_TOGGLE_NAV_LIGHTS <- vJoy Device {0000BBBB-0000-4000-8000-000000000203} JOY_BTN13", output);
        Assert.Contains("KEY_GEAR_TOGGLE <- Saitek Pro Flight X-56 Rhino Stick {0000BBBB-0000-4000-8000-000000000201} JOY_BTN_POV1_D", output);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(remote, "inputprofile_0000000201")));
    }

    [Fact]
    public void Store2020_RealProfilesInSaveContainers_AreImported()
    {
        string home = Path.Combine(root, "h");
        string wgs = StoreWgs(home, 2020);
        WgsWriter.Write(wgs, "Microsoft.FlightSimulator_8wekyb3d8bbwe",
            Directory.GetFiles(Path.Combine(fixtures, "steam2020")).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => new WgsContainer(Path.GetFileName(f), [new WgsBlobData("data", File.ReadAllBytes(f), null)])).ToList());
        Engine(home, "games", "install", "set", "MSFS2020", Path.Combine(home, "Local", "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe"));

        (int code, string output) = Engine(home, "--profile", Path.Combine(root, "p.pr0file"), "msfs2020", "import", "--text");

        Assert.True(code == 0, output);
        Assert.Contains("MSFS device profiles read: 4 from the Microsoft Store version", output);
        Assert.Contains("Bindings Read: 23", output);
        Assert.Contains("KEY_CHASE_ZOOM_AXIS <- LEFT VPC Throttle MT-50 CM {0000AAAA-0000-4000-8000-000000000102} JOY_Z", output);
        Assert.Contains("KEY_THROTTLE_AXIS_SET_EX1 <- LEFT VPC Throttle MT-50 CM {0000AAAA-0000-4000-8000-000000000102} JOY_RX", output);
        Assert.Contains("KEY_AILERON_TRIM_LEFT <- VKB-Sim Gunfighter Modern Combat Pro Twist {0000AAAA-0000-4000-8000-000000000101} JOY_BTN_POV1_L", output);
    }

    [Fact]
    public void EngineOutput_CommandListBuiltOnDemand_IsRecognized()
    {
        Assert.True(MainWindowViewModel.EngineBuiltCommandList("Steps:\n  - JoyPro had no Microsoft Flight Simulator 2024 command list yet: built it from the installed game (1760 actions)."));
        Assert.True(MainWindowViewModel.EngineBuiltCommandList("  - JoyPro had no Star Citizen command list yet: built it from the installed game (1103 actions, x)."));
        Assert.False(MainWindowViewModel.EngineBuiltCommandList("JoyPro has no X-Plane 12 command list yet, and building it from the installed game did not work"));
        Assert.False(MainWindowViewModel.EngineBuiltCommandList(null));
    }

    [Fact]
    public void ImportWizard_ShowsTheWholeFailureReason()
    {
        string reason = "No Microsoft Flight Simulator 2024 input profiles found. Searched: " + string.Join("; ", Enumerable.Range(1, 6).Select(i => @"C:\Users\someone\AppData\Local\Packages\Folder" + i));
        EngineCommandResult result = new() { ExitCode = 1, StandardOutput = "Errors:\n  - " + reason + "\n" };
        Assert.Equal(reason, MainWindowViewModel.EngineFailureReason(result, 0));
        Assert.True(MainWindowViewModel.EngineFailureReason(result).Length <= 160);
    }

    private static string StoreWgs(string home, int sim) =>
        Path.Combine(home, "Local", "Packages", sim == 2020 ? "Microsoft.FlightSimulator_8wekyb3d8bbwe" : "Microsoft.Limitless_8wekyb3d8bbwe", "SystemAppData", "wgs");

    private byte[] Fixture2024(string name) => File.ReadAllBytes(Path.Combine(fixtures, "profiles2024", name));

    private static byte[] Zlib(byte[] data)
    {
        using MemoryStream output = new();
        using (ZLibStream zlib = new(output, CompressionLevel.Optimal, true))
            zlib.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private (int Code, string Output) Engine(string home, params string[] args)
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
        info.Environment["JOYPRO_DATA_DIR"] = Path.Combine(root, "data");
        info.Environment["JOYPRO_TEST_MSFS_HOME"] = home;
        info.Environment["JOYPRO_INSTALL_DETECTION"] = "off";
        info.Environment["JOYPRO_NO_INTERNET_PICTURES"] = "1";
        info.Environment["JOYPRO_LOG_DIR"] = Path.Combine(root, "logs");
        // The connected devices: none unless a test lists some (the PC's own devices never take part).
        string devicesFile = connectedDevices ?? Path.Combine(root, "no-devices.tsv");
        if (connectedDevices == null && !File.Exists(devicesFile))
            File.WriteAllText(devicesFile, "# no device connected\r\n");
        info.Environment["JOYPRO_FAKE_DEVICES"] = devicesFile;
        using Process process = Process.Start(info)!;
        System.Threading.Tasks.Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(120000), "engine " + string.Join(" ", args) + " did not finish.");
        return (process.ExitCode, stdout + stderr.Result);
    }

    internal sealed record WgsBlobData(string Name, byte[] Bytes, DateTime? Time);

    internal sealed record WgsContainer(string Name, IReadOnlyList<WgsBlobData> Blobs);

    // The Xbox connected-storage layout as the engine reads it (engine\Games\MsfsUserProfiles.cs, WgsStore).
    internal static class WgsWriter
    {
        public static string Write(string wgs, string family, IReadOnlyList<WgsContainer> containers)
        {
            string user = Path.Combine(wgs, "0009000A1B2C3D_69F80140");
            Directory.CreateDirectory(user);
            using MemoryStream index = new();
            using BinaryWriter w = new(index);
            w.Write(14u);
            w.Write(containers.Count);
            w.Write(0u);
            WriteString(w, family.Replace("_8wekyb3d8bbwe", "_1.0.0.0_x64__8wekyb3d8bbwe") + "!App");
            w.Write(133700000000000000UL);
            w.Write(0u);
            WriteString(w, Guid.Empty.ToString());
            w.Write(0UL);
            foreach (WgsContainer container in containers)
            {
                Guid folder = Guid.NewGuid();
                WriteString(w, container.Name);
                WriteString(w, container.Name);
                WriteString(w, "\"0x8DCC4B5A1E2F3A4\"");
                w.Write((byte)1);
                w.Write(0u);
                w.Write(folder.ToByteArray());
                w.Write(133700000000000000UL);
                w.Write(new byte[16]);
                string dir = Path.Combine(user, folder.ToString("N").ToUpperInvariant());
                Directory.CreateDirectory(dir);
                using MemoryStream list = new();
                using BinaryWriter cw = new(list);
                cw.Write(4u);
                cw.Write(container.Blobs.Count);
                foreach (WgsBlobData blob in container.Blobs)
                {
                    Guid file = Guid.NewGuid();
                    byte[] name = new byte[128];
                    byte[] nameBytes = Encoding.Unicode.GetBytes(blob.Name);
                    Array.Copy(nameBytes, name, Math.Min(128, nameBytes.Length));
                    cw.Write(name);
                    cw.Write(file.ToByteArray());
                    cw.Write(file.ToByteArray());
                    string path = Path.Combine(dir, file.ToString("N").ToUpperInvariant());
                    File.WriteAllBytes(path, blob.Bytes);
                    if (blob.Time is DateTime time)
                        File.SetLastWriteTime(path, time);
                }
                cw.Flush();
                File.WriteAllBytes(Path.Combine(dir, "container.1"), list.ToArray());
            }
            w.Flush();
            File.WriteAllBytes(Path.Combine(user, "containers.index"), index.ToArray());
            return user;
        }

        private static void WriteString(BinaryWriter w, string text)
        {
            w.Write(text.Length);
            w.Write(Encoding.Unicode.GetBytes(text));
        }
    }
}
