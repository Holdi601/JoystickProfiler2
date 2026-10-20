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
public sealed partial class MsfsProfileImportTests : IDisposable
{
    private readonly string root;
    private readonly string engine;
    private readonly string fixtures;
    // JOYPRO_FAKE_DEVICES of the engine runs (null: no device connected).
    private string? connectedDevices;
    // More environment of the engine runs (test faults).
    private readonly Dictionary<string, string> environment = new();

    public MsfsProfileImportTests()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        engine = Path.Combine(dir!.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
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

    // The 2.2.3 report (MSFS 2024): a T-Pendular-Rudder the user never changed runs on the preset the sim ships (brakes
    // and rudder in Controls), but the sim saves no profile for it, so the import missed it. Now import and scan take
    // the shipped default of every connected device and category without an own profile (product ID as the sim
    // matches it, the Primary preset; not the alternatives, not helicopters, not gamepad presets); the user's own
    // profile wins over the default of its category. The shipped files stay untouched, also by an export.
    // The 2.2.4 report (MSFS 2024 Store): two MFD Cougar panels have a shipped Primary preset just like the rudder, but
    // the sim shows no binds for them, and JoyPro imported the preset. The sim's device records (profile_00) decide:
    // a device whose preset slots name the preset gets it, one whose slots are empty gets none.
    [Fact]
    public void Steam2024_DevicesOnTheSimsDefaultPreset_AreImported()
    {
        (string home, string install, string remote, string community) = SimDefaultsSetup();
        Dictionary<string, byte[]> shipped = Directory.GetFiles(install, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
        string tpr = "T-Pendular-Rudder {0000DDDD-0000-4000-8000-000000000401}";
        string vmax = "VPC VMAX Prime Throttle {0000CCCC-0000-4000-8000-000000000301}";
        string mfd = "F16 MFD 1 {0000EEEE-0000-4000-8000-000000000501}";
        // What the sim recorded: the rudder and the throttle on their presets, the MFD on none.
        File.WriteAllBytes(Path.Combine(remote, "profile_00"), SimProfile00.Build(
            new SimProfile00.Device(Rudder, 0xB68F, null, "t-pendular-rudder_2024_planes.xml"),
            new SimProfile00.Device(Throttle, 0x019D, "vmax_prime_throttle_2024_transversal.xml", "vmax_prime_throttle_2024_planes.xml"),
            new SimProfile00.Device(Mfd, 0xB351, null, null)));

        // Nothing saved yet (a fresh install): the defaults are the scan.
        (int freshCode, string fresh) = Engine(home, "msfs2024", "scan", "--text");
        Assert.True(freshCode == 0, fresh);
        Assert.Contains("No own Microsoft Flight Simulator 2024 profile saved yet", fresh);
        Assert.Contains("The sim's device records (profile_00) were read from Steam", fresh);
        Assert.Contains("T-Pendular-Rudder: no own MSFS profile, imported the sim's default preset 'T-Pendular-Rudder 2024 Planes' (Airplanes) (the sim's device records name it).", fresh);
        Assert.Contains("VPC VMAX Prime Throttle: no own MSFS profile, imported the sim's default presets 'VMAX Prime Throttle 2024 Planes' (Airplanes), 'VMAX Prime Throttle 2024 Transversal' (General) (the sim's device records name it).", fresh);
        Assert.Contains("KEY_AXIS_SPOILER_SET <- " + vmax + " JOY_X", fresh);
        Assert.Contains("F16 MFD 1: the sim has a default preset 'MFD COUGAR Left 2024 Planes' (Airplanes) but does not use it", fresh);
        Assert.DoesNotContain(mfd + " JOY_BTN", fresh);
        // --include-sim-defaults does not override what the records say.
        (_, string forced) = Engine(home, "msfs2024", "scan", "--include-sim-defaults", "--text");
        Assert.DoesNotContain(mfd + " JOY_BTN", forced);

        // The user changed the throttle's airplane binds: that profile wins, the General default still comes along.
        File.WriteAllBytes(Path.Combine(remote, "inputprofile_0000000301"), Fixture2024("vmax-throttle-airplane.xml"));
        string profile = Path.Combine(root, "p.pr0file");
        (int code, string output) = Engine(home, "--profile", profile, "msfs2024", "import", "--text");

        Assert.True(code == 0, output);
        Assert.Contains("KEY_AXIS_LEFT_BRAKE_SET <- " + tpr + " JOY_Y", output);
        Assert.Contains("KEY_AXIS_RIGHT_BRAKE_SET <- " + tpr + " JOY_X", output);
        Assert.Contains("KEY_AXIS_RUDDER_SET <- " + tpr + " JOY_Z", output);
        Assert.Contains("KEY_THROTTLE1_AXIS_SET_EX1 <- " + vmax + " JOY_RX", output);
        Assert.Contains("KEY_ACTIVE_PAUSE_TOGGLE <- " + vmax + " JOY_BTN26", output);
        Assert.Contains("VPC VMAX Prime Throttle: no own MSFS profile, imported the sim's default preset 'VMAX Prime Throttle 2024 Transversal' (General) (the sim's device records name it).", output);
        Assert.DoesNotContain(mfd + " JOY_BTN", output);
        Assert.Contains("MSFS device profiles read: 1 from Steam, 2 default preset(s) of the sim (devices without an own profile).", output);
        // Not the throttle's airplane default (its own profile wins), not the rudder's alternative or helicopter
        // presets, not a gamepad preset or the preset of a device that is not connected.
        Assert.DoesNotContain("KEY_AXIS_SPOILER_SET", output);
        Assert.DoesNotContain("KEY_ROTOR_BRAKE_TOGGLE", output);
        Assert.DoesNotContain("KEY_AXIS_AILERONS_SET", output);
        Assert.DoesNotContain("KEY_AXIS_ELEVATOR_SET", output);
        Assert.DoesNotContain("No own Microsoft Flight Simulator 2024 profile saved yet", output);

        // The brakes keep the preset's invert; the preset's <Axes> page (dead zone 6) is not the device's tuning.
        (_, string relations) = Engine(home, "--profile", profile, "relations", "list", "--text");
        string brake = relations.Split('\n').Select(l => l.Split('|')[0].Trim()).First(n => n.Contains("Left Brake", StringComparison.OrdinalIgnoreCase));
        (_, string tuning) = Engine(home, "--profile", profile, "binds", "tuning", "show", brake, "--game", "MSFS2024", "--text");
        Assert.DoesNotContain("n.axisDeadZone=6", tuning);
        Assert.Contains("n.axisDeadZone=0", tuning);

        // Export goes to JoyPro's package; the shipped presets stay as they were.
        (int exportCode, string export) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");
        Assert.True(exportCode == 0, export);
        Assert.Contains("rudder", string.Join("\n", Directory.GetFiles(Path.Combine(community, "joypro-input-profiles"), "*.xml", SearchOption.AllDirectories)), StringComparison.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, byte[]> file in shipped)
            Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Equal(shipped.Count, Directory.GetFiles(install, "*", SearchOption.AllDirectories).Length);
    }

    // Without the sim's device records (no profile_00, or a format JoyPro cannot read) it is not known whether the sim
    // uses a shipped preset: the device is left out with a step, and --include-sim-defaults (GUI checkbox) takes the
    // Primary preset of its product ID as before. The Microsoft Store keeps profile_00 as a save blob (here
    // compressed): read like the Steam file.
    [Fact]
    public void Steam2024_WithoutTheSimsRecords_DefaultPresetsOnlyOnRequest_StoreRecordsAreRead()
    {
        (string home, _, _, _) = SimDefaultsSetup();
        string tpr = "T-Pendular-Rudder {0000DDDD-0000-4000-8000-000000000401}";
        string mfd = "F16 MFD 1 {0000EEEE-0000-4000-8000-000000000501}";

        (_, string unknown) = Engine(home, "msfs2024", "scan", "--text");
        Assert.Contains("T-Pendular-Rudder: the sim has a default preset 'T-Pendular-Rudder 2024 Planes' (Airplanes) but it is not known whether the sim uses it", unknown);
        Assert.Contains("F16 MFD 1: the sim has a default preset 'MFD COUGAR Left 2024 Planes' (Airplanes) but it is not known whether the sim uses it", unknown);
        Assert.Contains("--include-sim-defaults (GUI: 'Also import default presets the sim may not use')", unknown);
        Assert.DoesNotContain(tpr + " JOY_", unknown);
        Assert.DoesNotContain(mfd + " JOY_BTN", unknown);
        Assert.DoesNotContain("None of the connected devices has a default preset", unknown);

        (int optCode, string optIn) = Engine(home, "msfs2024", "scan", "--include-sim-defaults", "--text");
        Assert.True(optCode == 0, optIn);
        Assert.Contains("T-Pendular-Rudder: no own MSFS profile, imported the sim's default preset 'T-Pendular-Rudder 2024 Planes' (Airplanes) (--include-sim-defaults: not known whether the sim uses it).", optIn);
        Assert.Contains("KEY_AXIS_RUDDER_SET <- " + tpr + " JOY_Z", optIn);
        Assert.Contains(mfd + " JOY_BTN", optIn);

        // Store: the records are a blob of the save containers.
        WgsWriter.Write(StoreWgs(home, 2024), "Microsoft.Limitless_8wekyb3d8bbwe", new[]
        {
            new WgsContainer("PROFILE", new[]
            {
                new WgsBlobData("profile_00", Zlib(SimProfile00.Build(
                    new SimProfile00.Device(Rudder, 0xB68F, null, "t-pendular-rudder_2024_planes.xml"),
                    new SimProfile00.Device(Mfd, 0xB351, null, null))), null)
            })
        });
        (int storeCode, string store) = Engine(home, "msfs2024", "scan", "--text");
        Assert.True(storeCode == 0, store);
        Assert.Contains("The sim's device records (profile_00) were read from the Microsoft Store save", store);
        Assert.Contains("KEY_AXIS_RUDDER_SET <- " + tpr + " JOY_Z", store);
        Assert.Contains("F16 MFD 1: the sim has a default preset 'MFD COUGAR Left 2024 Planes' (Airplanes) but does not use it", store);
        Assert.DoesNotContain(mfd + " JOY_BTN", store);
    }

    // The 2.2.5 report (MSFS 2024 Store): the MFD Cougar panels still came in. His profile_00 (minimal form here): the
    // rudder's category slot names its preset (with its display name) in the record of every aircraft; the MFD's slots
    // are empty (flag 1, no file) in 3 records and name the preset only in the record of the last aircraft (A320neo,
    // no display name, aircraft slot "TT:GENERIC.NONE"). Records that disagree: not imported (a step; the opt-in takes
    // it). Also: a device that is not connected gets no "has 2 MSFS profiles" warning, and JoyPro's own preset that
    // Windows cannot read (it lost its EFS encryption in a Store install) is read from JoyPro's copy with a warning.
    [Fact]
    public void Store2024_PresetsTheSimsRecordsDisagreeOn_AreNotImported_UnreadableOwnPresetFromJoyProsCopy()
    {
        (string home, _, string remote, string community) = SimDefaultsSetup();
        string tpr = "T-Pendular-Rudder {0000DDDD-0000-4000-8000-000000000401}";
        string mfd = "F16 MFD 1 {0000EEEE-0000-4000-8000-000000000501}";
        string[] aircraft = ["asobo-aircraft-bonanza-g36", "asobo-aircraft-c172sp-as1000", "asobo-aircraft-208b-grand-caravan-ex", "microsoft-aircraft-a320neo"];
        SimProfile00.Device rudder = new(Rudder, 0xB68F, null, null,
            aircraft.Select(a => new SimProfile00.Rec(a, null, "t-pendular-rudder_2024_planes.xml", GeneralName: "TT:GENERIC.NONE", AirplaneName: "T-Pendular-Rudder 2024 Planes")).ToArray());
        SimProfile00.Device panel = new(Mfd, 0xB351, null, null,
            aircraft.Select(a => a.Contains("a320neo", StringComparison.Ordinal)
                ? new SimProfile00.Rec(a, null, "mfd_cougar_left_2024_planes.xml", AircraftName: "TT:GENERIC.NONE")
                : new SimProfile00.Rec(a, null, null, Flag: 1)).ToArray());
        SimProfile00.Device throttle = new(Throttle, 0x019D, null, null,
            aircraft.Select(a => new SimProfile00.Rec(a, "vmax_prime_throttle_2024_transversal.xml", "vmax_prime_throttle_2024_planes.xml")).ToArray());
        WgsWriter.Write(StoreWgs(home, 2024), "Microsoft.Limitless_8wekyb3d8bbwe",
            [new("PROFILE", [new("profile_00", SimProfile00.Build(rudder, panel, throttle), null)])]);
        // An old stick that is not connected, with two profiles.
        File.WriteAllBytes(Path.Combine(remote, "inputprofile_0000000303"), Fixture2024("old-cm3-stick.xml"));
        File.WriteAllBytes(Path.Combine(remote, "inputprofile_0000000304"), Fixture2024("old-cm3-stick.xml"));
        string profile = Path.Combine(root, "p.pr0file");

        (int code, string output) = Engine(home, "--profile", profile, "msfs2024", "import", "--text");

        Assert.True(code == 0, output);
        Assert.Contains("The sim's device records (profile_00) were read from the Microsoft Store save", output);
        Assert.Contains("T-Pendular-Rudder: no own MSFS profile, imported the sim's default preset 'T-Pendular-Rudder 2024 Planes' (Airplanes) (the sim's device records name it).", output);
        Assert.Contains("KEY_AXIS_RUDDER_SET <- " + tpr + " JOY_Z", output);
        Assert.Contains("F16 MFD 1: the sim's records of this device (one per aircraft) name its default preset 'MFD COUGAR Left 2024 Planes' (Airplanes) in only 1 of 4, so it is not known whether the sim uses it; not imported.", output);
        Assert.DoesNotContain(mfd + " JOY_BTN", output);
        Assert.Contains("VPC VMAX Prime Throttle: no own MSFS profile, imported the sim's default presets", output);
        Assert.Contains("Profiles of 1 device(s) that are not connected were left out", output);
        Assert.DoesNotContain("MSFS profiles; imported the most recently saved one", output);
        // The relations are named after the actions, without the game in front.
        (_, string relations) = Engine(home, "--profile", profile, "relations", "list", "--text");
        Assert.DoesNotContain("Microsoft Flight Simulator 2024 ", relations.Split('\n').Select(l => l.Split('|')[0]).Aggregate("", (a, b) => a + b + "\n"));
        string rudderRelation = relations.Split('\n').First(l => l.Contains(tpr + "::JOY_Z", StringComparison.OrdinalIgnoreCase)).Split('|')[0].Trim();
        // A relation of another game with that name: the game in brackets.
        string other = Path.Combine(root, "other.pr0file");
        (int addCode, string add) = Engine(home, "--profile", other, "relations", "add", rudderRelation, "--axis", "--node", "MSFS2020:KEY_AXIS_RUDDER_SET", "--text");
        Assert.True(addCode == 0, add);
        Engine(home, "--profile", other, "msfs2024", "import", "--text");
        (_, string otherRelations) = Engine(home, "--profile", other, "relations", "list", "--text");
        Assert.Contains(rudderRelation + " (Microsoft Flight Simulator 2024) | type=axis | bind=" + tpr + "::JOY_Z", otherRelations, StringComparison.OrdinalIgnoreCase);

        // On request the preset comes along, and the step says why it is not certain.
        (_, string forced) = Engine(home, "msfs2024", "scan", "--include-sim-defaults", "--text");
        Assert.Contains(mfd + " JOY_BTN", forced);
        Assert.Contains("F16 MFD 1: no own MSFS profile, imported the sim's default preset 'MFD COUGAR Left 2024 Planes' (Airplanes) (--include-sim-defaults: the sim's records name it for 1 of 4 aircraft).", forced);

        // JoyPro's package preset of the rudder (an export), which Windows then cannot read (409, like a file that
        // lost its EFS encryption in a Store install): its binds come from JoyPro's copy.
        (int exportCode, string export) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");
        Assert.True(exportCode == 0, export);
        string preset = Assert.Single(Directory.GetFiles(Path.Combine(community, "joypro-input-profiles"), "JoyPro_T-Pendular-Rudder_*_2024_Planes.xml", SearchOption.AllDirectories));
        string backups = Path.Combine(root, "data", "Backups", "MSFS");
        Directory.CreateDirectory(backups);
        string copy = Path.Combine(backups, Path.GetFileName(preset) + ".20261019-120000.jpbak");
        File.Copy(preset, copy);
        environment["JOYPRO_TEST_FAULT_ENCRYPTION"] = "read:409:" + Path.GetFileName(preset);
        (int readCode, string read) = Engine(home, "msfs2024", "scan", "--text");
        Assert.True(readCode == 0, read);
        Assert.Contains("JoyPro's MSFS 2024 preset 'T-Pendular-Rudder (Planes)' cannot be read: the sim keeps its data encrypted (EFS) on this PC", read);
        Assert.Contains("Its binds were imported from JoyPro's copy of", read);
        Assert.DoesNotContain("Could not read the MSFS input profile", read);
        // The sim's records name the shipped preset for the rudder, not JoyPro's: JoyPro's preset does not count (the
        // sim does not use it), the rudder is on its default (tester 2026-10-20: relations came back from JoyPro's preset).
        Assert.Contains("JoyPro preset(s) of the Community2024 package not read: the sim's records of the device do not use them (T-Pendular-Rudder Airplanes", read);
        Assert.Contains("KEY_AXIS_RUDDER_SET <- " + tpr + " JOY_Z", read);

        // Without a copy: the warning says what to do, and the device is on the sim's default again.
        foreach (string backup in Directory.GetFiles(backups, Path.GetFileName(preset) + ".*.jpbak"))
            File.Delete(backup);
        (_, string lost) = Engine(home, "msfs2024", "scan", "--text");
        Assert.Contains("JoyPro has no readable copy of it, so its binds were not imported. Import with 'Merge into profile' (not --replace)", lost);
        Assert.Contains("KEY_AXIS_RUDDER_SET <- " + tpr + " JOY_Z", lost);
    }

    private static readonly Guid Rudder = new("0000DDDD-0000-4000-8000-000000000401");
    private static readonly Guid Throttle = new("0000CCCC-0000-4000-8000-000000000301");
    private static readonly Guid Mfd = new("0000EEEE-0000-4000-8000-000000000501");

    // A stand-in MSFS 2024 install with the shipped-presets fixture, a Steam remote folder without profiles and three
    // connected devices that have shipped presets: a rudder, a throttle and an MFD panel.
    private (string Home, string Install, string Remote, string Community) SimDefaultsSetup()
    {
        string home = Path.Combine(root, "h");
        string install = Path.Combine(root, "sim");
        CopyFolder(Path.Combine(Path.GetDirectoryName(fixtures)!, "shipped-presets"), install);
        string remote = Path.Combine(home, "Steam", "userdata", "42", "2537590", "remote");
        Directory.CreateDirectory(remote);
        string roaming = Path.Combine(home, "Roaming", "Microsoft Flight Simulator 2024");
        string community = Path.Combine(roaming, "Packages", "Community2024");
        Directory.CreateDirectory(community);
        File.WriteAllText(Path.Combine(roaming, "UserCfg.opt"), "InstalledPackagesPath \"" + Path.Combine(roaming, "Packages") + "\"\r\n");
        connectedDevices = Path.Combine(root, "devices.tsv");
        File.WriteAllText(connectedDevices,
            "T-Pendular-Rudder {" + Rudder.ToString().ToUpperInvariant() + "}\t044F\tB68F\tT-Pendular-Rudder\r\n" +
            "VPC VMAX Prime Throttle {" + Throttle.ToString().ToUpperInvariant() + "}\t3344\t019D\tVPC VMAX Prime Throttle\r\n" +
            "F16 MFD 1 {" + Mfd.ToString().ToUpperInvariant() + "}\t044F\tB351\tF16 MFD 1\r\n");
        Engine(home, "games", "install", "set", "MSFS2024", install);
        return (home, install, remote, community);
    }

    // profile_00 as MSFS 2024 writes it (engine\Games\MsfsSimProfile.cs; field hashes of a real Steam file): nodes of
    // an 8-byte name hash, a uint32 size (header included) and a payload (container: uint32 count + nodes). A device
    // table, then per device and aircraft a record with three preset slots (general, category, aircraft) that name
    // the preset file or hold a 0 byte; a short checksum at the end.
    internal static class SimProfile00
    {
        // One record per aircraft (Records), or one record for the C172 (General / Airplane).
        internal sealed record Device(Guid Guid, int ProductId, string? General, string? Airplane, Rec[]? Records = null);

        // A record of one aircraft: the file names of its slots, the slot flag (the sim wrote 1 with an empty file for a
        // tester's MFDs) and the display names the sim noted ("TT:GENERIC.NONE" for an aircraft slot set to none).
        internal sealed record Rec(string Aircraft, string? General, string? Airplane, int Flag = -1, string? GeneralName = null, string? AirplaneName = null, string? AircraftName = null);

        private static byte[] H(string hex) => Convert.FromHexString(hex);

        private static byte[] Node(string hash, byte[] payload)
        {
            using MemoryStream m = new();
            m.Write(H(hash));
            m.Write(BitConverter.GetBytes(12 + payload.Length));
            m.Write(payload);
            return m.ToArray();
        }

        private static byte[] Container(string hash, params byte[][] children)
        {
            using MemoryStream m = new();
            m.Write(BitConverter.GetBytes(children.Length));
            foreach (byte[] child in children)
                m.Write(child);
            return Node(hash, m.ToArray());
        }

        private static byte[] Text(string hash, string text) => Node(hash, Encoding.ASCII.GetBytes(text + "\0"));

        private static byte[] Int(string hash, int value) => Node(hash, BitConverter.GetBytes(value));

        private static byte[] Byte0(string hash) => Node(hash, new byte[] { 0 });

        private static byte[][] DeviceFields(Device d, string? preset, int flag = -1, string? name = null) =>
        [
            Int("c5e29153ab70ce33", 1),
            Int("9c6a107054980963", d.ProductId),
            Int("419c4be1028db14a", 0),
            Text("e494a07827809546", "{" + d.Guid.ToString().ToUpperInvariant() + "}"),
            Int("00d10becc027d54f", flag >= 0 ? flag : preset == null ? 0 : 1),
            preset == null ? Byte0("e5374a443ee0986e") : Text("e5374a443ee0986e", preset),
            name == null ? Byte0("877ddf5aa523fd04") : Text("877ddf5aa523fd04", name),
            Byte0("6a076b4c6e9f3687")
        ];

        public static byte[] Build(params Device[] devices)
        {
            List<byte[]> table = devices.Select((d, i) => Container("a90d92863d6d85d" + i.ToString("x"), DeviceFields(d, null))).ToList();
            List<byte[]> records = new();
            foreach (Device d in devices)
            {
                foreach (Rec r in d.Records ?? [new Rec("asobo-aircraft-c172sp-as1000", d.General, d.Airplane)])
                {
                    List<byte[]> fields = DeviceFields(d, null).ToList();
                    fields.Add(Text("6ef48b11820c125d", r.Aircraft));
                    fields.Add(Container("ae7f5b37a56a781c", DeviceFields(d, r.General, r.Flag, r.GeneralName)));
                    fields.Add(Container("8b57821791389404", DeviceFields(d, r.Airplane, r.Flag, r.AirplaneName).Append(Text("dd0e07ae6ad0a4fc", "AIRPLANE")).ToArray()));
                    fields.Add(Container("6bc4c835749736a7", DeviceFields(d, null, 0, r.AircraftName).Append(Text("6ef48b11820c125d", r.Aircraft)).ToArray()));
                    records.Add(Container("82547a6384e0e31a", fields.ToArray()));
                }
            }
            byte[] tree = Container("94438d991bb0815f",
                Int("4794bad805765141", 0),
                Container("a532ec6925941962",
                    Node("7c5253cf6543b1e1", new byte[] { 1 }),
                    Container("a46c8bf66a7db8ce", table.ToArray()),
                    Container("a9281cff465213e7", records.ToArray())));
            return tree.Concat(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 }).ToArray();
        }
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
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

    // The 2.2.6 report (MSFS 2024 Store): the relation editor found only "ATC Panel Choice 0/1" and "Display ATC" for
    // "atc", while the sim's Controls menu lists ATC PANEL CHOICE 0 ... 9. His command list was built by an older JoyPro
    // (flight contexts only), and an import added only the bound actions. MSFS 2024 profiles list every action of their
    // contexts (unbound ones as empty <Action/>): an import or scan now adds all of them with the context the sim lists
    // them in, corrects a context the list has from elsewhere (KEY_PUSHBACK_SET: PLANE from MSFS 2020, SIM_SHORTCUT in
    // 2024), keeps known titles, and leaves the debug and menu contexts out.
    [Fact]
    public void Store2024_UnboundActionsTheProfilesList_JoinTheCommandList()
    {
        string home = Path.Combine(root, "h");
        string wgs = StoreWgs(home, 2024);
        WgsWriter.Write(wgs, "Microsoft.Limitless_8wekyb3d8bbwe", [new("InputProfile_1", [new("data", Zlib(Fixture2024("panel-general-listing.xml")), null)])]);
        Engine(home, "games", "install", "set", "MSFS2024", Path.Combine(home, "Local", "Packages", "Microsoft.Limitless_8wekyb3d8bbwe"));
        string db = Path.Combine(root, "data", "GameData", "DB", "MSFS2024");
        Directory.CreateDirectory(db);
        File.WriteAllText(Path.Combine(db, "actions.tsv"),
            "# Microsoft Flight Simulator 2024 action catalog, built by an older JoyPro\r\n" +
            "KEY_AXIS_ELEVATOR_SET\tElevator Axis\taxis\tSURFACES\r\n" +
            "KEY_ATC_MENU_0\tMy ATC Choice Zero\tbutton\tATC\r\n" +
            "KEY_PUSHBACK_SET\tPushback\tbutton\tPLANE\r\n" +
            "KEY_ACTIVE_PAUSE_TOGGLE\tActive Pause\tbutton\tINGAME_UI\r\n");
        connectedDevices = Path.Combine(root, "devices.tsv");
        File.WriteAllText(connectedDevices, "VPC Panel #2 {0000CCCC-0000-4000-8000-000000000401}\t3344\t025B\tVPC Panel #2\r\n");

        (int code, string output) = Engine(home, "msfs2024", "scan", "--text");

        Assert.True(code == 0, output);
        Assert.Contains("KEY_ACTIVE_PAUSE_TOGGLE <- VPC Panel #2 {0000CCCC-0000-4000-8000-000000000401} JOY_BTN3", output);
        Assert.Contains("unbound action(s) your Microsoft Flight Simulator 2024 profiles list", output);
        string catalog = File.ReadAllText(Path.Combine(db, "actions.tsv"));
        for (int i = 1; i <= 9; i++)
            Assert.Matches(@"(?m)^KEY_ATC_MENU_" + i + @"\t[^\t]+\tbutton\tATC\r?$", catalog);
        Assert.Matches(@"(?m)^KEY_ATC_MENU_0\tMy ATC Choice Zero\tbutton\tATC\r?$", catalog);
        Assert.Matches(@"(?m)^KEY_ATC\t[^\t]+\tbutton\tINGAME_UI\r?$", catalog);
        Assert.Matches(@"(?m)^KEY_GROUND_SERVICE_ATC_MESSAGE\t[^\t]+\tbutton\tALWAYS\r?$", catalog);
        Assert.Matches(@"(?m)^KEY_COCKPIT_CAMERA_ZOOM_AXIS\t[^\t]+\taxis\tCOCKPIT_GLOBAL_CAMERA\r?$", catalog);
        Assert.Matches(@"(?m)^KEY_PUSHBACK_SET\tPushback\tbutton\tSIM_SHORTCUT\r?$", catalog);
        Assert.DoesNotContain("KEY_ATC_MENU_OPEN", catalog);
        Assert.DoesNotContain("KEY_MENU_VALID", catalog);

        // The editor's search finds every ATC choice now.
        (int searchCode, string search) = Engine(home, "relations", "search", "atc", "--plane", "MSFS2024", "--limit", "200", "--per-game", "50", "--text");
        Assert.True(searchCode == 0, search);
        for (int i = 0; i <= 9; i++)
            Assert.Contains("MSFS2024:KEY_ATC_MENU_" + i + ":MSFS2024", search);

        // A second scan adds nothing.
        (_, string again) = Engine(home, "msfs2024", "scan", "--text");
        Assert.DoesNotContain("unbound action(s)", again);
    }

    // The 2.2.6 report (MSFS 2024 Store): relations made in JoyPro never showed up in the sim. The sim applies a preset
    // of JoyPro's package only to aircraft it has not set up for a device yet, and the tester's devices had records
    // (and his own profiles) for every aircraft he flies; the Store saves are not written. Now the export also writes,
    // per device and category, a file in the sim's own format to import in Controls: the user's profile of that device
    // and category with JoyPro's binds (his other binds and the profile's layout stay), else a profile listing the
    // command list's actions. The Store save stays untouched.
    [Fact]
    public void Store2024_Export_WritesImportFilesPerDeviceAndCategory()
    {
        string home = Path.Combine(root, "h");
        string localCache = Path.Combine(home, "Local", "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache");
        string community = Path.Combine(localCache, "Packages", "Community2024");
        Directory.CreateDirectory(community);
        File.WriteAllText(Path.Combine(localCache, "UserCfg.opt"), "InstalledPackagesPath \"" + Path.Combine(localCache, "Packages") + "\"\r\n");
        string wgs = StoreWgs(home, 2024);
        string user = WgsWriter.Write(wgs, "Microsoft.Limitless_8wekyb3d8bbwe",
        [
            new("InputProfile_1", [new("data", Fixture2024("vmax-throttle-airplane.xml"), DateTime.Now.AddDays(-1))]),
            new("InputProfile_2", [new("data", Fixture2024("vmax-throttle-general.xml"), DateTime.Now.AddDays(-1))])
        ]);
        Dictionary<string, string> before = Directory.GetFiles(user, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));
        Engine(home, "games", "install", "set", "MSFS2024", Path.Combine(home, "Local", "Packages", "Microsoft.Limitless_8wekyb3d8bbwe"));
        connectedDevices = Path.Combine(root, "devices.tsv");
        File.WriteAllText(connectedDevices,
            "VPC VMAX Prime Throttle {0000CCCC-0000-4000-8000-000000000301}\t3344\t019D\tVPC VMAX Prime Throttle\r\n" +
            "Test Panel {0000CCCC-0000-4000-8000-000000000501}\t3344\t025B\tTest Panel\r\n");
        string vmax = "VPC VMAX Prime Throttle {0000CCCC-0000-4000-8000-000000000301}";
        string panel = "Test Panel {0000CCCC-0000-4000-8000-000000000501}";
        string profile = Path.Combine(root, "p.pr0file");
        (int importCode, string import) = Engine(home, "--profile", profile, "msfs2024", "import", "--text");
        Assert.True(importCode == 0, import);
        // New relations made in JoyPro: the gear on the throttle (its airplane profile), cockpit zoom (General), and a
        // panel the sim has no profile of.
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "JP Gear", "--node", "MSFS2024:KEY_GEAR_TOGGLE", "--device", vmax, "--input", "JOY_BTN5", "--text").Code);
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "JP Zoom", "--node", "MSFS2024:KEY_ZOOM_COCKPIT", "--device", vmax, "--input", "JOY_BTN30", "--text").Code);
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "JP Flaps Panel", "--node", "MSFS2024:KEY_GEAR_TOGGLE", "--device", panel, "--input", "JOY_BTN7", "--text").Code);

        (int code, string export) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");

        Assert.True(code == 0, export);
        string folder = Path.Combine(root, "data", "MSFS 2024 import files");
        Assert.Contains("MSFS 2024 import files written (3, one per device and category): " + folder, export);
        Assert.Contains("choose Import and select the file JoyPro wrote for that device and category", export);
        Assert.Contains("click the cogwheel of the profile (not the preset's)", export);
        Assert.Contains("Import these again in the sim (new or changed since the last export): ", export);
        Assert.Equal(folder, MainWindowViewModel.MsfsImportFilesFolderOf(export));
        // The same export again: no file changed, nothing to import again.
        (int againCode, string again) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");
        Assert.True(againCode == 0, again);
        Assert.Contains("No import file changed since the last export: nothing to import again in the sim.", again);
        string planes = File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "VPC_VMAX_Prime_Throttle_0000cccc_Airplanes.xml")));
        Assert.Contains("<FriendlyName PlatformAvailability=\"1\" Locked=\"false\">JoyPro VPC VMAX Prime Throttle Airplanes</FriendlyName>", planes);
        Assert.Contains("GUID=\"{0000CCCC-0000-4000-8000-000000000301}\" ProductID=\"413\"", planes);
        Assert.Contains("<AircraftInfo CategoryName=\"AIRPLANE\"/>", planes);
        Assert.Matches("(?s)<Action ActionName=\"KEY_GEAR_TOGGLE\"[^>]*>\\s*<Primary>\\s*<KEY Information=\"Joystick Button 5\">4</KEY>", planes);
        Assert.Matches("(?s)<Action ActionName=\"KEY_AXIS_FLAPS_SET\"[^>]*>.*?<KEY", planes);
        string general = File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "VPC_VMAX_Prime_Throttle_0000cccc_General.xml")));
        Assert.Contains("JoyPro VPC VMAX Prime Throttle General", general);
        Assert.Matches("(?s)<Action ActionName=\"KEY_ZOOM_COCKPIT\"[^>]*>\\s*<Primary>\\s*<KEY Information=\"Joystick Button 30\">29</KEY>", general);
        Assert.DoesNotContain("AIRPLANE", general);
        // The panel has no profile in the sim: a new one in the sim's format, decimal product ID, the command list's
        // flight actions listed.
        string panelFile = File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "Test_Panel_0000cccc_Airplanes.xml")));
        Assert.Contains("<Version Num=\"-1\"/>", panelFile);
        Assert.Contains("GUID=\"{0000CCCC-0000-4000-8000-000000000501}\" ProductID=\"603\"", panelFile);
        Assert.Matches("(?s)<Action ActionName=\"KEY_GEAR_TOGGLE\"[^>]*>\\s*<Primary>\\s*<KEY Information=\"Joystick Button 7\">6</KEY>", panelFile);
        Assert.Contains("<Action ActionName=\"KEY_AXIS_ELEVATOR_SET\" ValueEvent=\"0.000000\" Delay=\"0.000000\" Flag=\"2\"/>", panelFile);
        Assert.Contains("MSFS 2024 import file of 'Test Panel' (Airplanes, JoyPro's binds)", export);
        Assert.Contains("MSFS 2024 import file of 'VPC VMAX Prime Throttle' (Airplanes, your profile 'VMAX Planes' with JoyPro's binds)", export);

        // The Store save is never written.
        foreach (KeyValuePair<string, string> file in before)
            Assert.Equal(file.Value, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file.Key))));
        // A dry run writes no import files.
        Directory.Delete(folder, true);
        (int dryCode, string dry) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--dry-run", "--text");
        Assert.True(dryCode == 0, dry);
        Assert.False(Directory.Exists(folder));
    }

    // The 2.2.6 report: "JP does not always detect changes made in-game". The tester's logs show MSFS 2024 "untracked" all
    // day: the export's record failed on a file Windows refused to read ("not encrypted as expected", EFS), and a game
    // only imported was never recorded, so the watcher never watched it. Now an unreadable file is stamped by size and
    // write time, an import of a game without a record records it, and a Store save (new blob and container names, a new
    // containers.index) makes the state "changed" with the changed bind in the diff.
    [Fact]
    public void Store2024_WatcherRecord_SurvivesUnreadableFiles_AndASimSaveIsDetected()
    {
        string home = Path.Combine(root, "h");
        string wgs = StoreWgs(home, 2024);
        WgsWriter.Write(wgs, "Microsoft.Limitless_8wekyb3d8bbwe", [new("InputProfile_1", [new("data", Fixture2024("vmax-throttle-airplane.xml"), null)])]);
        Engine(home, "games", "install", "set", "MSFS2024", Path.Combine(home, "Local", "Packages", "Microsoft.Limitless_8wekyb3d8bbwe"));
        connectedDevices = Path.Combine(root, "devices.tsv");
        File.WriteAllText(connectedDevices, "VPC VMAX Prime Throttle {0000CCCC-0000-4000-8000-000000000301}\t3344\t019D\tVPC VMAX Prime Throttle\r\n");
        string profile = Path.Combine(root, "p.pr0file");
        (_, string untracked) = Engine(home, "sync", "status", "--game", "MSFS2024");
        Assert.Contains("\"State\":\"untracked\"", untracked);

        // Windows refuses the save index (EFS); the import still records the state.
        environment["JOYPRO_TEST_FAULT_ENCRYPTION"] = "read:409:containers.index";
        (int importCode, string import) = Engine(home, "--profile", profile, "msfs2024", "import", "--text");
        Assert.True(importCode == 0, import);
        (int statusCode, string status) = Engine(home, "sync", "status", "--game", "MSFS2024");
        Assert.True(statusCode == 0, status);
        Assert.Contains("\"State\":\"unchanged\"", status);
        environment.Remove("JOYPRO_TEST_FAULT_ENCRYPTION");

        // The sim saves a changed profile: gear on button 9 (new blob and container names, a new index).
        string changed = Encoding.UTF8.GetString(Fixture2024("vmax-throttle-airplane.xml")).Replace(
            "<Action ActionName=\"KEY_GEAR_TOGGLE\" ValueEvent=\"0.000000\" Delay=\"0.000000\" Flag=\"2\"/>",
            "<Action ActionName=\"KEY_GEAR_TOGGLE\" ValueEvent=\"0.000000\" Delay=\"0.000000\" Flag=\"2\">\n\t\t\t<Primary>\n\t\t\t\t<KEY Information=\"Joystick Button 9\">8</KEY>\n\t\t\t</Primary>\n\t\t</Action>");
        // The sim writes one kind of line end.
        changed = changed.Replace("\r\n", "\n");
        Assert.Contains("Joystick Button 9", changed);
        foreach (string folder in Directory.GetDirectories(Directory.GetDirectories(wgs)[0]))
            Directory.Delete(folder, true);
        WgsWriter.Write(wgs, "Microsoft.Limitless_8wekyb3d8bbwe", [new("InputProfile_1", [new("data", Zlib(Encoding.UTF8.GetBytes(changed)), null)])]);

        (_, string after) = Engine(home, "sync", "status", "--game", "MSFS2024");
        Assert.Contains("\"State\":\"changed\"", after);
        (int diffCode, string diff) = Engine(home, "--profile", profile, "sync", "diff", "--game", "MSFS2024", "--text");
        Assert.True(diffCode == 0, diff);
        Assert.True(diff.Contains("JOY_BTN9", StringComparison.Ordinal) && diff.Contains("Gear", StringComparison.OrdinalIgnoreCase), diff);
    }

    // Diagnostics (docs/logging.md "Game runs", "Support file"): a failed game run prints the environment of the game
    // (install, config folders with drive, file system and attributes, connected devices); the support bundle shows its
    // contents first, then holds the summary with that environment, JoyPro's backup copies of the files an export
    // changed, the MSFS profiles JoyPro reads, its package and import files and the sim's records; the user folder is
    // replaced by %USERPROFILE%.
    [Fact]
    public void Diagnostics_FailedRunPrintsTheEnvironment_SupportBundleHoldsTheGamesFiles()
    {
        string home = Path.Combine(root, "h");
        string localCache = Path.Combine(home, "Local", "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache");
        string community = Path.Combine(localCache, "Packages", "Community2024");
        Directory.CreateDirectory(community);
        File.WriteAllText(Path.Combine(localCache, "UserCfg.opt"), "InstalledPackagesPath \"" + Path.Combine(localCache, "Packages") + "\"\r\n");
        string wgs = StoreWgs(home, 2024);
        WgsWriter.Write(wgs, "Microsoft.Limitless_8wekyb3d8bbwe", [new("InputProfile_1", [new("data", Fixture2024("vmax-throttle-airplane.xml"), null)])]);
        Engine(home, "games", "install", "set", "MSFS2024", Path.Combine(home, "Local", "Packages", "Microsoft.Limitless_8wekyb3d8bbwe"));
        connectedDevices = Path.Combine(root, "devices.tsv");
        File.WriteAllText(connectedDevices, "VPC VMAX Prime Throttle {0000CCCC-0000-4000-8000-000000000301}\t3344\t019D\tVPC VMAX Prime Throttle\r\n");
        string profile = Path.Combine(root, "p.pr0file");
        Assert.Equal(0, Engine(home, "--profile", profile, "msfs2024", "import", "--text").Code);

        (int refusedCode, string refused) = Engine(home, "--profile", profile, "msfs2024", "export", wgs, "--text");
        Assert.NotEqual(0, refusedCode);
        Assert.Contains("Diagnostics (environment and files of this run):", refused);
        Assert.Contains("Environment of MSFS2024", refused);
        Assert.Contains("VPC VMAX Prime Throttle 3344:019D {0000CCCC-0000-4000-8000-000000000301}", refused);
        Assert.Matches(@"path: .*SystemAppData\\wgs \[folder\], attributes [^,]+, drive [A-Z]:\\ ", refused);

        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "JP Gear", "--node", "MSFS2024:KEY_GEAR_TOGGLE", "--device", "VPC VMAX Prime Throttle {0000CCCC-0000-4000-8000-000000000301}", "--input", "JOY_BTN5", "--text").Code);
        (int exportCode, string export) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");
        Assert.True(exportCode == 0, export);
        // A second export: the backup set holds JoyPro's copy of the preset it changed.
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "JP Gear 2", "--node", "MSFS2024:KEY_GEAR_TOGGLE", "--device", "VPC VMAX Prime Throttle {0000CCCC-0000-4000-8000-000000000301}", "--input", "JOY_BTN6", "--text").Code);
        Assert.Equal(0, Engine(home, "--profile", profile, "msfs2024", "export", community, "--text").Code);

        (int listCode, string list) = Engine(home, "support", "bundle", "--list", "--text");
        Assert.True(listCode == 0, list);
        Assert.Contains("Support file contents (not written)", list);
        List<string> entries = MainWindowViewModel.SupportFileEntries(list);
        Assert.Contains(entries, e => e.StartsWith("summary.txt (", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.StartsWith("games/MSFS2024/profiles/001-store-", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.StartsWith("games/MSFS2024/package/InputProfiles/Categories/Airplane/JoyPro_VPC_VMAX_Prime_Throttle_0000cccc_2024_Planes.xml", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.StartsWith("games/MSFS2024/import-files/VPC_VMAX_Prime_Throttle_0000cccc_Airplanes.xml", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.StartsWith("games/MSFS2024/sim-records.txt", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.StartsWith("games/MSFS2024/sync-record.json", StringComparison.Ordinal));
        Assert.Contains(entries, e => System.Text.RegularExpressions.Regex.IsMatch(e, @"^games/MSFS2024/backup-[^/]+/\d{4}_JoyPro_VPC_VMAX_Prime_Throttle_0000cccc_2024_Planes\.xml \(JoyPro's copy of "));
        Assert.False(File.Exists(Path.Combine(root, "support.zip")));

        string zip = Path.Combine(root, "support.zip");
        (int bundleCode, string bundle) = Engine(home, "--out", zip, "support", "bundle", "--text");
        Assert.True(bundleCode == 0, bundle);
        using ZipArchive archive = ZipFile.OpenRead(zip);
        Assert.Equal(entries.Count, archive.Entries.Count);
        string summary = new StreamReader(archive.GetEntry("summary.txt")!.Open()).ReadToEnd();
        Assert.Contains("Environment of MSFS2024", summary);
        Assert.Contains("VPC VMAX Prime Throttle 3344:019D", summary);
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (root.StartsWith(user, StringComparison.OrdinalIgnoreCase))
        {
            Assert.DoesNotContain(user, summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("%USERPROFILE%", summary);
        }
        string storeProfile = new StreamReader(archive.Entries.First(e => e.FullName.StartsWith("games/MSFS2024/profiles/001-store-", StringComparison.Ordinal)).Open()).ReadToEnd();
        Assert.Contains("VMAX Planes", storeProfile);
    }

    [Fact]
    public void MsfsImportFilesFolder_IsReadFromTextAndJsonOutput()
    {
        Assert.Equal(@"C:\Users\x\Documents\JoyPro\MSFS 2024 import files",
            MainWindowViewModel.MsfsImportFilesFolderOf("Steps:\r\n  - MSFS 2024 import files written (3, one per device and category): C:\\Users\\x\\Documents\\JoyPro\\MSFS 2024 import files\r\n  - next"));
        Assert.Equal(@"C:\Users\x\Documents\JoyPro\MSFS 2024 import files",
            MainWindowViewModel.MsfsImportFilesFolderOf("{\"Steps\":[\"MSFS 2024 import files written (3, one per device and category): C:\\\\Users\\\\x\\\\Documents\\\\JoyPro\\\\MSFS 2024 import files\",\"x\"]}"));
        Assert.Null(MainWindowViewModel.MsfsImportFilesFolderOf("Steps:\n  - 3 action(s) in 2 profile file(s) changed."));
    }

    // refresh-db without the user's General profile: a general event only the locPak names joins with the context of
    // the known actions of the same name group (two words at least), one without such a group stays out.
    [Fact]
    public void Refresh2024_LocPakOnlyGeneralEvents_TakeTheContextOfTheirNameGroup()
    {
        string home = Path.Combine(root, "h");
        string install = Path.Combine(root, "MSFS2024");
        string transversal = Path.Combine(install, "Packages", "fs-input-profiles", "InputProfiles", "Categories", "Transversal");
        Directory.CreateDirectory(transversal);
        File.WriteAllText(Path.Combine(transversal, "Test_2024_Transversal.xml"),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<DefaulftInput Primary=\"1\" PlatformAvailability=\"PC\">\r\n" +
            "<Device DeviceName=\"Test\" GUID=\"{00000000-0000-4000-8000-000000000999}\" ProductID=\"C215\" CompositeID=\"0\" HWVer=\"1.0.0.0\">\r\n" +
            "\t<Context ContextName=\"INSTRUMENTS_CAMERA\">\r\n" +
            "\t\t<Action ActionName=\"KEY_INSTRUMENT_LOOK_DOWN\" Flag=\"2\">\r\n\t\t\t<Primary>\r\n\t\t\t\t<KEY Information=\"Joystick Button 1\">0</KEY>\r\n\t\t\t</Primary>\r\n\t\t</Action>\r\n" +
            "\t\t<Action ActionName=\"KEY_INSTRUMENT_LOOK_LEFT\" Flag=\"2\">\r\n\t\t\t<Primary>\r\n\t\t\t\t<KEY Information=\"Joystick Button 2\">1</KEY>\r\n\t\t\t</Primary>\r\n\t\t</Action>\r\n" +
            "\t</Context>\r\n</Device>\r\n</DefaulftInput>\r\n");
        Directory.CreateDirectory(Path.Combine(install, "Packages", "fs-base"));
        File.WriteAllText(Path.Combine(install, "Packages", "fs-base", "en-US.locPak"),
            "{\"LocalisationPackage\":{\"Language\":\"en-US\",\"Strings\":{" +
            "\"INPUT.KEY_INSTRUMENT_LOOK_DOWN\":\"INSTRUMENT LOOK DOWN\",\"INPUT.KEY_INSTRUMENT_LOOK_LEFT\":\"INSTRUMENT LOOK LEFT\"," +
            "\"INPUT.KEY_INSTRUMENT_LOOK_UP\":\"INSTRUMENT LOOK UP\",\"INPUT.KEY_XCLOUD_LAYOUT_00\":\"XCLOUD LAYOUT 0\"," +
            "\"INPUT.KEY_GEAR_TOGGLE\":\"LANDING GEAR\"}}}");
        File.WriteAllBytes(Path.Combine(install, "FlightSimulator2024.exe"), [0]);
        Engine(home, "games", "install", "set", "MSFS2024", install);

        (int code, string output) = Engine(home, "msfs2024", "refresh-db", "--text");

        Assert.True(code == 0, output);
        Assert.Contains("General events the sim names but neither a preset nor a profile of yours lists: 1 new", output);
        string catalog = File.ReadAllText(Path.Combine(root, "data", "GameData", "DB", "MSFS2024", "actions.tsv"));
        Assert.Matches(@"(?m)^KEY_INSTRUMENT_LOOK_UP\tInstrument Look Up\tbutton\tINSTRUMENTS_CAMERA\r?$", catalog);
        Assert.DoesNotContain("KEY_XCLOUD_LAYOUT_00", catalog);
        Assert.Matches(@"(?m)^KEY_GEAR_TOGGLE\t[^\t]+\tbutton\tAIRCRAFT\r?$", catalog);
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
        foreach (KeyValuePair<string, string> variable in environment)
            info.Environment[variable.Key] = variable.Value;
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
            // As a tester's MSFS 2024 save has it: version 14, flags 3 (fully uploaded and downloaded), every container
            // synced (state 1) with the cloud's version tag, its blob size, times in milliseconds.
            w.Write(14u);
            w.Write(containers.Count);
            w.Write(0u);
            WriteString(w, family + "!App");
            w.Write(133700000000000000UL);
            w.Write(3u);
            WriteString(w, "63a0b2ab-e13c-4b95-b279-66b6566c660c");
            w.Write(new byte[] { 0, 0, 0, 0x10, 0, 0, 0, 0 });
            foreach (WgsContainer container in containers)
            {
                Guid folder = Guid.NewGuid();
                WriteString(w, container.Name);
                WriteString(w, container.Name);
                WriteString(w, "\"0x8DCC4B5A1E2F3A4\"");
                w.Write((byte)1);
                w.Write(1u);
                w.Write(folder.ToByteArray());
                w.Write(133700000000000000UL);
                w.Write(0UL);
                w.Write((ulong)container.Blobs.Sum(b => (long)b.Bytes.Length));
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
