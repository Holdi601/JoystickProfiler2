using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace JoyPro.Gui.Tests;

// 2.5.1: actionmaps.xml can list one connected device under several joystick slots (stale entries the game kept from
// older places of the device). They used up the 8 js slots, so a device with binds (the author's VPforce Rhino) got
// none; the slot plan already skips them (2.5.1, smoke scslots), and now the export also removes them from the written
// file: the slot the device keeps stays, the stale ones go, their rebinds move to the kept slot (also from a stale slot
// the plan gave the new device). A device that is not connected keeps its single entry; keyboard and gamepad options and
// rebinds stay as they are. A real engine on a temp copy (fake devices with the game's product names), in the sandbox.
public sealed class StarCitizenStaleSlotsTests : IDisposable
{
    private const int Vid = 0x3344;
    private readonly string root = Path.Combine(Path.GetTempPath(), "joypro-scstale-" + Guid.NewGuid().ToString("N"));

    public StarCitizenStaleSlotsTests()
    {
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Instance(string name, int pid) => name + " {" + pid.ToString("X8") + "-0000-0000-0000-000000000000}";

    private static string Product(string name, int pid) => name + "  {" + pid.ToString("X4") + Vid.ToString("X4") + "-0000-0000-0000-504944564944}";

    [Fact]
    public void Export_RemovesStaleDuplicateSlots_KeepsUnpluggedAndOtherSections()
    {
        string? engine = DuplicateFixture.EnginePath();
        if (engine == null)
            return;
        string stick = Instance("SC Stick", 0xA5), throttle = Instance("SC Throttle", 0xA7), rhino = Instance("VPforce Rhino", 0xB1);
        string stickProduct = Product("SC Stick", 0xA5), throttleProduct = Product("SC Throttle", 0xA7), pedalsProduct = Product("Old Pedals", 0xC1);
        string devices = Path.Combine(root, "devices.tsv");
        File.WriteAllLines(devices,
        [
            stick + "\t3344\t00A5\tSC Stick",
            throttle + "\t3344\t00A7\tSC Throttle",
            rhino + "\t3344\t00B1\tVPforce Rhino"
        ]);
        const string keyboard = "  <options type=\"keyboard\" instance=\"1\" Product=\"Keyboard  {6F1D2B61-D5A0-11CF-BFC7-444553540000}\"/>";
        const string gamepad = "  <options type=\"gamepad\" instance=\"1\" Product=\"Controller (Gamepad)\"/>";
        string actionmaps = string.Join("\n",
            "<ActionMaps>",
            " <ActionProfiles version=\"1\" optionsVersion=\"2\" rebindVersion=\"2\" profileName=\"default\">",
            keyboard,
            gamepad,
            "  <options type=\"joystick\" instance=\"1\" Product=\"" + stickProduct + "\"/>",
            "  <options type=\"joystick\" instance=\"2\" Product=\"" + throttleProduct + "\"/>",
            "  <options type=\"joystick\" instance=\"3\" Product=\"" + stickProduct + "\"/>",
            "  <options type=\"joystick\" instance=\"4\" Product=\"" + pedalsProduct + "\"/>",
            "  <options type=\"joystick\" instance=\"5\" Product=\"" + throttleProduct + "\"/>",
            "  <options type=\"joystick\" instance=\"6\" Product=\"" + stickProduct + "\"/>",
            "  <options type=\"joystick\" instance=\"7\" Product=\"" + throttleProduct + "\"/>",
            "  <options type=\"joystick\" instance=\"8\" Product=\"" + stickProduct + "\"/>",
            "  <modifiers />",
            "  <actionmap name=\"spaceship_movement\">",
            "   <action name=\"v_boost\">",
            "    <rebind input=\"js3_button4\"/>",
            "   </action>",
            "   <action name=\"v_roll\">",
            "    <rebind input=\"js4_x\"/>",
            "   </action>",
            "  </actionmap>",
            "  <actionmap name=\"player\">",
            "   <action name=\"attack1\">",
            "    <rebind input=\"js5_button2\"/>",
            "    <rebind input=\"kb1_f\"/>",
            "   </action>",
            "  </actionmap>",
            " </ActionProfiles>",
            "</ActionMaps>", string.Empty);
        string folder = Path.Combine(root, "sc", "default");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "actionmaps.xml");
        File.WriteAllText(file, actionmaps);

        // Its own data folder: the test catalog replaces the Star Citizen list there.
        string data = Path.Combine(root, "data");
        Run(engine, devices, data, "games", "integrations", "--text");
        string catalog = Path.Combine(data, "GameData", "DB", "StarCitizen", "AllBinds.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(catalog)!);
        File.Copy(Path.Combine(FixturesFolder(), "roundtrip", "starcitizen", "catalog.xml"), catalog, true);

        // The Rhino (no slot in the file) gets a bind.
        string profile = Path.Combine(root, "sc.pr0file");
        Run(engine, devices, data, "--profile", profile, "relations", "add", "SC Yaw", "--axis", "--node", "StarCitizen:spaceship_movement|v_yaw:StarCitizen",
            "--device", rhino, "--input", "JOY_Z", "--text");
        string export = Run(engine, devices, data, "--profile", profile, "starcitizen", "export", "--path", folder, "--mode", "merge", "--text");
        string written = File.ReadAllText(file);

        Assert.DoesNotContain("no free joystick slot", export);
        Assert.Contains("Stale Joystick Slots Removed", export);
        int Count(string product) => Regex.Matches(written, "Product=\"" + Regex.Escape(product) + "\"").Count;
        Assert.True(Count(stickProduct) == 1, "the stick keeps one slot: " + written);
        Assert.True(Count(throttleProduct) == 1, "the throttle keeps one slot: " + written);
        Assert.Matches("<options type=\"joystick\" instance=\"1\" Product=\"" + Regex.Escape(stickProduct) + "\"/>", written);
        Assert.Matches("<options type=\"joystick\" instance=\"2\" Product=\"" + Regex.Escape(throttleProduct) + "\"/>", written);
        // The device that is not connected keeps its entry and its rebind.
        Assert.Matches("<options type=\"joystick\" instance=\"4\" Product=\"" + Regex.Escape(pedalsProduct) + "\"/>", written);
        Assert.Contains("<rebind input=\"js4_x\"/>", written);
        // The Rhino got a slot and its bind.
        Match rhinoSlot = Regex.Match(written, "<options type=\"joystick\" instance=\"(\\d)\" Product=\"VPforce Rhino[^\"]*\"/>");
        Assert.True(rhinoSlot.Success, "the Rhino must get a joystick slot: " + written);
        Assert.Matches("<action name=\"v_yaw\">\\s*<rebind input=\"js" + rhinoSlot.Groups[1].Value + "_z\"/>", written);
        // The stale slots' rebinds follow their device: js3 (stick, now the Rhino's) -> js1, js5 (throttle) -> js2.
        Assert.Matches("<action name=\"v_boost\">\\s*<rebind input=\"js1_button4\"/>", written);
        Assert.Contains("<rebind input=\"js2_button2\"/>", written);
        Assert.DoesNotContain("js3_button4", written);
        Assert.DoesNotContain("js5_button2", written);
        // Keyboard and gamepad stay as they were.
        Assert.Contains(keyboard, written);
        Assert.Contains(gamepad, written);
        Assert.Contains("<rebind input=\"kb1_f\"/>", written);
        Assert.True(Regex.Matches(written, "<options type=\"joystick\"").Count <= 8, written);

        // A second export changes nothing more.
        Run(engine, devices, data, "--profile", profile, "starcitizen", "export", "--path", folder, "--mode", "merge", "--text");
        Assert.Equal(written, File.ReadAllText(file));
    }

    private static string FixturesFolder()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "engine", "tests", "fixtures");
    }

    private string Run(string engine, string devices, string data, params string[] args)
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
        info.Environment["JOYPRO_DATA_DIR"] = data;
        info.Environment["JOYPRO_INSTALL_DETECTION"] = "off";
        info.Environment["JOYPRO_NO_INTERNET_PICTURES"] = "1";
        info.Environment["JOYPRO_TEST_ASSUME_INSTALLED"] = "1";
        info.Environment["JOYPRO_TEST_IGNORE_RUNNING_GAMES"] = "1";
        info.Environment["JOYPRO_LOG_DIR"] = Path.Combine(root, "logs");
        info.Environment["JOYPRO_FAKE_DEVICES"] = devices;
        // The game's product names ("<HID name>  {PIDVID}") as on a PC, and the startup order of the devices.
        info.Environment["JOYPRO_FAKE_SC_PRODUCTS"] = "pidvid";
        info.Environment["JOYPRO_DI_ORDER_OVERRIDE"] = string.Join(";", Instance("SC Stick", 0xA5), Instance("SC Throttle", 0xA7), Instance("VPforce Rhino", 0xB1));
        using Process process = Process.Start(info)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(180000), "engine " + string.Join(" ", args) + " did not finish.");
        string output = stdout + stderr.Result;
        Assert.True(process.ExitCode == 0, "engine " + string.Join(" ", args) + " failed (" + process.ExitCode + "): " + output);
        return output;
    }
}
