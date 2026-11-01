using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace JoyPro.Gui.Tests;

// 2.5.1: the vJoy device JoyPro tunnels into (Settings, --vjoy-tunnel) is never cleared by any export of any game, like
// a device on the import's ignore list (GameDevice.Kept, one rule in GameCommands.RunExport for every exporter; IL-2 and
// Star Citizen get the same list). Before, the live export of 2026-10-31 cleared the tunnel device's binds in the games
// other than DCS. Per game class a fake vJoy device (JOYPRO_FAKE_DEVICES, a configured vJoy device 1 in JOYPRO_VJOY_FAKE)
// holds game binds in the fixture; the profile comes from an import with --vjoy-tunnel 1 (so it has no binds there), and
// a clean + nuke export with --vjoy-tunnel 1 leaves every bind of the vJoy device in the file. The same export without
// the option clears them (proves the case exercises the clearing). A real engine on copies in the temp folder, inside
// the test sandbox; nothing touches a game folder.
public sealed class ExportKeptDevicesTests : IDisposable
{
    private const string VJoyName = "vJoy Device";
    private readonly string root = Path.Combine(Path.GetTempPath(), "joypro-kept-" + Guid.NewGuid().ToString("N"));

    public ExportKeptDevicesTests()
    {
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // area, fixture folder (engine\tests\fixtures\...), config file in it, device list, the device that becomes the vJoy
    // device (its line keeps GUID, VID/PID and HID name), extra import options, and a bind the profile gets on another
    // device when all other binds of the fixture are on the vJoy device ("node|device|input").
    public sealed record GameCase(string Area, string Fixture, string Config, string Devices, string Tunnel, string[] ImportOptions, string ExtraBind = "")
    {
        public override string ToString() => Area;
    }

    private static readonly Dictionary<string, GameCase> Cases = new GameCase[]
    {
        new("warthunder", "warthunder", "machine.blk", "warthunder\\devices.tsv", "Smoke Throttle", []),
        new("xplane12", "xplane", "X-Plane Joystick Settings.prf", "xplane\\devices.tsv", "Smoke Throttle", []),
        new("bms", "bms", "Config", "bms\\devices.tsv", "Smoke Throttle", []),
        new("wardogs", "wardogs", "GameUserSettings.ini", "wardogs\\devices.tsv", "Smoke Throttle", []),
        new("nuclearoption", "nuclearoption", "registry.json", "nuclearoption\\devices.tsv", VJoyName, []),
        new("fsx", "fsx", "Microsoft\\FSX\\Controls\\Standard.XML", "fsx\\devices.tsv", "Smoke Throttle", []),
        new("il2korea", "il2korea", "input\\general.actions", "il2korea\\devices.tsv", "Smoke Stick", [],
            "IL2Korea:airbrakes_on|Smoke Throttle {66666666-7777-8888-9999-000000000000}|JOY_BTN9"),
        new("elite", "elite", "Custom.4.2.binds", "bms\\devices.tsv", "Smoke Throttle", ["--replace"]),
        new("arma3", "arma3", "Smoke.Arma3Profile", "wardogs\\devices.tsv", "Smoke Throttle", []),
        new("x4", "x4", "1234\\inputmap.xml", "bms\\devices.tsv", "Smoke Throttle", ["--replace"]),
        // No Arma Reforger case: the game never sees a vJoy device, so its export writes no vJoy bind (skipped with
        // "Arma Reforger does not see vJoy devices"), gives vJoy no joystickN slot and the import and scan read none
        // (docs/games/arma-reforger.md). There is no vJoy bind to keep; engine\tests\smoke\reforger.ps1 checks that the
        // vJoy bind is skipped and the other devices keep their slots.
    }.ToDictionary(c => c.Area, StringComparer.Ordinal);

    public static IEnumerable<object[]> GameAreas() => Cases.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(GameAreas))]
    public void TunnelDevice_SurvivesCleanAndNukeExport(string area)
    {
        string? engine = Engine();
        if (engine == null)
            return;
        GameCase game = Cases[area];
        string fixtures = FixturesFolder();
        string devices = Path.Combine(root, "devices.tsv");
        string vjoy = TunnelDevices(Path.Combine(fixtures, game.Devices), game.Tunnel, devices);
        string vjoyFake = VJoyFake();

        string Copy(string name)
        {
            string folder = Path.Combine(root, name);
            CopyFolder(Path.Combine(fixtures, game.Fixture), folder);
            return Path.Combine(folder, game.Config);
        }

        string original = Copy("original");
        List<string> before = TunnelBinds(Run(engine, devices, vjoyFake, game.Area, "scan", original, "--text"), vjoy);
        Assert.True(before.Count > 0, game.Area + ": the fixture must hold binds of the device that becomes the vJoy device (" + vjoy + ").");

        string profile = Path.Combine(root, "profile.pr0file");
        string import = Run(engine, devices, vjoyFake, ["--profile", profile, "--vjoy-tunnel", "1", game.Area, "import", original, .. game.ImportOptions, "--text"]);
        Assert.DoesNotContain(vjoy, TunnelBinds(import, vjoy));
        if (game.ExtraBind.Length > 0)
        {
            string[] extra = game.ExtraBind.Split('|');
            Run(engine, devices, vjoyFake, "--profile", profile, "relations", "add", "Kept Extra", "--button", "--node", extra[0], "--device", extra[1], "--input", extra[2], "--text");
        }

        string kept = Copy("kept");
        string keptExport = Run(engine, devices, vjoyFake, ["--profile", profile, "--vjoy-tunnel", "1", game.Area, "export", kept, "--mode", "clean", "--nuke-untouched-devices", "--text"]);
        Assert.Contains("Never cleared", keptExport);
        List<string> after = TunnelBinds(Run(engine, devices, vjoyFake, game.Area, "scan", kept, "--text"), vjoy);
        Assert.True(before.SequenceEqual(after), game.Area + ": clean + nuke with the vJoy device JoyPro tunnels into must keep its binds." + Environment.NewLine +
                                                 "before: " + string.Join(" | ", before) + Environment.NewLine + "after: " + string.Join(" | ", after) + Environment.NewLine + keptExport);

        // Control: without the option the same export clears that device (it is a connected device the profile does not use).
        string cleared = Copy("cleared");
        Run(engine, devices, vjoyFake, ["--profile", profile, game.Area, "export", cleared, "--mode", "clean", "--nuke-untouched-devices", "--text"]);
        List<string> without = TunnelBinds(Run(engine, devices, vjoyFake, game.Area, "scan", cleared, "--text"), vjoy);
        Assert.True(without.Count < before.Count, game.Area + ": without --vjoy-tunnel nuke must clear the device (the case must exercise the clearing): " + string.Join(" | ", without));
    }

    // The import's ignored devices take the same rule (--ignore-device on the export).
    [Fact]
    public void IgnoredDevice_SurvivesCleanAndNukeExport()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        string fixtures = FixturesFolder();
        string devices = Path.Combine(fixtures, "warthunder", "devices.tsv");
        string folder = Path.Combine(root, "ignored");
        CopyFolder(Path.Combine(fixtures, "warthunder"), folder);
        string blk = Path.Combine(folder, "machine.blk");
        const string throttle = "Smoke Throttle {66666666-7777-8888-9999-000000000000}";
        string vjoyFake = VJoyFake();
        List<string> before = TunnelBinds(Run(engine, devices, vjoyFake, "warthunder", "scan", blk, "--text"), throttle);
        Assert.NotEmpty(before);
        string profile = Path.Combine(root, "ignored.pr0file");
        Run(engine, devices, vjoyFake, "--profile", profile, "relations", "add", "Kept Guns", "--button", "--node", "WarThunder:ID_FIRE_MGUNS",
            "--device", "Smoke Stick {11111111-2222-3333-4444-555555555555}", "--input", "JOY_BTN3", "--text");
        string export = Run(engine, devices, vjoyFake, "--profile", profile, "warthunder", "export", blk, "--mode", "clean", "--nuke-untouched-devices", "--ignore-device", throttle, "--text");
        Assert.Contains("Never cleared", export);
        Assert.Equal(before, TunnelBinds(Run(engine, devices, vjoyFake, "warthunder", "scan", blk, "--text"), throttle));
    }

    // IL-2 Great Battles (its own exporter): the vJoy device's lines in global.actions stay.
    [Fact]
    public void Il2_TunnelDevice_SurvivesCleanAndNukeExport()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        string fixtures = FixturesFolder();
        string devices = Path.Combine(root, "il2-devices.tsv");
        TunnelDevices(Path.Combine(fixtures, "roundtrip", "il2-devices.tsv"), "Smoke Pedals", devices);
        string vjoyFake = VJoyFake();

        string Copy(string name)
        {
            string game = Path.Combine(root, name);
            CopyFolder(Path.Combine(fixtures, "roundtrip", "il2"), game);
            // IL-2 names the device in devices.txt as DirectInput does.
            string list = Path.Combine(game, "data", "input", "devices.txt");
            File.WriteAllText(list, File.ReadAllText(list).Replace("Smoke%20Pedals", "vJoy%20Device", StringComparison.Ordinal));
            return game;
        }

        // joy1 is the vJoy device (devices.txt), its readable line rpc_yaw joy1_axis_z is released by nuke without the option.
        Func<string, bool> hasVJoyLine = game => Regex.IsMatch(File.ReadAllText(Path.Combine(game, "data", "input", "global.actions")), @"(?m)^rpc_yaw,\s+joy1_axis_z,");
        string original = Copy("il2-original");
        Assert.True(hasVJoyLine(original));
        // A profile with one bind on the stick (joy0), none on the vJoy device.
        string profile = Path.Combine(root, "il2.pr0file");
        Run(engine, devices, vjoyFake, "--profile", profile, "relations", "add", "IL2 Gear", "--button", "--node", "IL2Game:gear",
            "--device", "Test Stick {00000000-0000-0000-0000-000000000000}", "--input", "JOY_BTN5", "--text");

        string kept = Copy("il2-kept");
        Run(engine, devices, vjoyFake, "--profile", profile, "--vjoy-tunnel", "1", "il2", "export", "--path", kept, "--mode", "clean", "--nuke-untouched-devices", "true", "--text");
        Assert.True(hasVJoyLine(kept), "IL-2: clean + nuke must keep the line of the vJoy device JoyPro tunnels into: " + File.ReadAllText(Path.Combine(kept, "data", "input", "global.actions")));

        string cleared = Copy("il2-cleared");
        Run(engine, devices, vjoyFake, "--profile", profile, "il2", "export", "--path", cleared, "--mode", "clean", "--nuke-untouched-devices", "true", "--text");
        Assert.False(hasVJoyLine(cleared), "IL-2: without --vjoy-tunnel nuke releases the device (control).");
    }

    // Star Citizen (its own exporter): the js slot of the vJoy device keeps its rebind.
    [Fact]
    public void StarCitizen_TunnelDevice_SurvivesCleanAndNukeExport()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        string fixtures = FixturesFolder();
        const string stick = "Test Stick {11111111-1111-1111-1111-111111111111}";
        const string vjoy = "vJoy Device {89D63500-B53F-11F1-8002-444553540000}";
        string devices = Path.Combine(root, "sc-devices.tsv");
        File.WriteAllLines(devices, [stick + "\t1234\t5678\tTest Stick", vjoy + "\t1234\tBEAD\tvJoy - Virtual Joystick"]);
        string vjoyFake = VJoyFake();
        // Its own data folder: the test catalog replaces the Star Citizen list there (not the run's shared one).
        string data = Path.Combine(root, "sc-data");
        RunIn(engine, devices, vjoyFake, data, "games", "integrations", "--text");
        string catalog = Path.Combine(data, "GameData", "DB", "StarCitizen", "AllBinds.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(catalog)!);
        File.Copy(Path.Combine(fixtures, "roundtrip", "starcitizen", "catalog.xml"), catalog, true);
        string actionmaps = string.Join("\n",
            "<ActionMaps>",
            " <ActionProfiles version=\"1\" optionsVersion=\"2\" rebindVersion=\"2\" profileName=\"default\">",
            "  <options type=\"keyboard\" instance=\"1\" Product=\"Keyboard  {6F1D2B61-D5A0-11CF-BFC7-444553540000}\"/>",
            "  <options type=\"joystick\" instance=\"1\" Product=\"" + stick + "\"/>",
            "  <options type=\"joystick\" instance=\"2\" Product=\"" + vjoy + "\"/>",
            "  <modifiers />",
            "  <actionmap name=\"spaceship_movement\">",
            "   <action name=\"v_boost\">",
            "    <rebind input=\"js1_button7\"/>",
            "   </action>",
            "   <action name=\"v_yaw\">",
            "    <rebind input=\"js2_rotz\"/>",
            "   </action>",
            "  </actionmap>",
            " </ActionProfiles>",
            "</ActionMaps>", string.Empty);

        string Copy(string name)
        {
            string folder = Path.Combine(root, name, "default");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "actionmaps.xml"), actionmaps);
            return folder;
        }

        string profile = Path.Combine(root, "sc.pr0file");
        RunIn(engine, devices, vjoyFake, data, "--profile", profile, "--vjoy-tunnel", "1", "starcitizen", "import", "--path", Copy("sc-original"), "--text");

        string kept = Copy("sc-kept");
        RunIn(engine, devices, vjoyFake, data, "--profile", profile, "--vjoy-tunnel", "1", "starcitizen", "export", "--path", kept, "--mode", "clean", "--nuke-untouched-devices", "true", "--text");
        string keptText = File.ReadAllText(Path.Combine(kept, "actionmaps.xml"));
        Assert.Contains("<rebind input=\"js2_rotz\"/>", keptText);

        string cleared = Copy("sc-cleared");
        RunIn(engine, devices, vjoyFake, data, "--profile", profile, "starcitizen", "export", "--path", cleared, "--mode", "clean", "--nuke-untouched-devices", "true", "--text");
        Assert.DoesNotContain("js2_rotz", File.ReadAllText(Path.Combine(cleared, "actionmaps.xml")));
    }

    // ---- helpers ----

    private static string? Engine()
    {
        string? engine = DuplicateFixture.EnginePath();
        return engine != null && Directory.Exists(Path.Combine(Path.GetDirectoryName(engine)!, "DB", "WarThunder")) ? engine : null;
    }

    private static string FixturesFolder()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "engine", "tests", "fixtures");
    }

    // The device list with one device renamed to a vJoy device (GUID, VID/PID, HID name kept); returns its JoyPro name.
    private static string TunnelDevices(string source, string device, string target)
    {
        string? name = null;
        List<string> lines = [];
        foreach (string line in File.ReadAllLines(source))
        {
            string copy = line;
            if (!line.StartsWith('#') && line.StartsWith(device + " {", StringComparison.Ordinal))
            {
                copy = VJoyName + line[device.Length..];
                name ??= copy.Split('\t')[0].Trim();
            }
            lines.Add(copy);
        }
        Assert.NotNull(name);
        File.WriteAllLines(target, lines);
        return name!;
    }

    // One configured vJoy device (number 1), never the PC's vJoy.
    private string VJoyFake()
    {
        string folder = Path.Combine(root, "vjoy-fake");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "vjoy.txt"), "installed=2.1.9.1\r\ndevice=1 axes=x,y,z,rx,ry,rz buttons=32 povs=0 discrete=0\r\n");
        return folder;
    }

    // "  - <action> <- <device> <input>" lines of a scan whose main device is the given one, sorted.
    private static List<string> TunnelBinds(string output, string device) =>
        output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains(" <- " + device, StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, true);
        }
    }

    private string Run(string engine, string devices, string vjoyFake, params string[] args) => RunIn(engine, devices, vjoyFake, null, args);

    private string RunIn(string engine, string devices, string vjoyFake, string? data, params string[] args)
    {
        ProcessStartInfo info = new(engine)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(engine)!
        };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        if (data != null)
            info.Environment["JOYPRO_DATA_DIR"] = data;
        info.Environment["JOYPRO_INSTALL_DETECTION"] = "off";
        info.Environment["JOYPRO_NO_INTERNET_PICTURES"] = "1";
        info.Environment["JOYPRO_TEST_ASSUME_INSTALLED"] = "1";
        info.Environment["JOYPRO_TEST_IGNORE_RUNNING_GAMES"] = "1";
        info.Environment["JOYPRO_LOG_DIR"] = Path.Combine(root, "logs");
        info.Environment["JOYPRO_FAKE_DEVICES"] = devices;
        info.Environment["JOYPRO_VJOY_FAKE"] = vjoyFake;
        using Process process = Process.Start(info)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(180000), "engine " + string.Join(" ", args) + " did not finish.");
        string output = stdout + stderr.Result;
        Assert.True(process.ExitCode == 0, "engine " + string.Join(" ", args) + " failed (" + process.ExitCode + "): " + output);
        return output;
    }
}
