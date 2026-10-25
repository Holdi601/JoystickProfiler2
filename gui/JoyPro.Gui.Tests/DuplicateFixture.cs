using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using Xunit;

namespace JoyPro.Gui.Tests;

// A small copy of a tester's profile of 24 Oct (v73) for the duplicate tests: position relations of his devices
// ("Mode Button 07" on the MFBB, the CP2 and VMAX buttons) next to function relations ("Trim Right", "Throttle Right
// Inverted DZ"), his real DCS ids, and the history as it happened: a clean version, then a DCS import whose load
// correction switched the AH-64, F-4E and AJS37 on in relations that did not have them (Before applies it the same
// way through the engine). Needs the development DCS command lists (engine\bin\...\DB\DCS).
internal static class DuplicateFixture
{
    public const string TrimId = "d3007pnilu3007cd25vd1vpnilvu0";
    public const string TrimUpId = "d3004pnilu3004cd25vd1vpnilvu0";
    public const string CautionId = "d3001pnilu3001cd12vd1vpnilvu0";

    public const string Profile = """
        {
          "Relations": {
            "Trim Right": { "NAME": "Trim Right", "ISAXIS": false, "Groups": [], "NODES": [
              { "ID": "d3007pnilu3007cd25vd1vpnilvu0", "Game": "DCS", "AIRCRAFT": { "AH-64D_BLK_II_CPG": true, "AH-64D_BLK_II_PLT": true } } ] },
            "Mode Button 07": { "NAME": "Mode Button 07", "ISAXIS": false, "Groups": [], "NODES": [
              { "ID": "d3001pnilu3001cd28vd1vpnilvu0", "Game": "DCS", "AIRCRAFT": { "F-16C_50": true } },
              { "ID": "d3007pnilu3007cd25vd1vpnilvu0", "Game": "DCS", "AIRCRAFT": { "FA-18C_hornet": true } } ] },
            "Trim Nose Down": { "NAME": "Trim Nose Down", "ISAXIS": false, "Groups": [], "NODES": [
              { "ID": "d3004pnilu3004cd25vd1vpnilvu0", "Game": "DCS", "AIRCRAFT": { "AH-64D_BLK_II_CPG": true, "AH-64D_BLK_II_PLT": true } } ] },
            "Mode Button 04": { "NAME": "Mode Button 04", "ISAXIS": false, "Groups": [], "NODES": [
              { "ID": "d3004pnilu3004cd25vd1vpnilvu0", "Game": "DCS", "AIRCRAFT": { "FA-18C_hornet": true } } ] },
            "Throttle Right Inverted DZ": { "NAME": "Throttle Right Inverted DZ", "ISAXIS": true, "Groups": [], "NODES": [
              { "ID": "a2004cdnil", "Game": "DCS", "AIRCRAFT": { "AJS37": true, "M-2000C": true } } ] },
            "Flaps Axis": { "NAME": "Flaps Axis", "ISAXIS": true, "Groups": [], "NODES": [
              { "ID": "a2004cdnil", "Game": "DCS", "AIRCRAFT": { "AH-64D_BLK_II_CPG": true, "AH-64D_BLK_II_PLT": true } } ] },
            "Button CP2 B1": { "NAME": "Button CP2 B1", "ISAXIS": false, "Groups": [], "NODES": [
              { "ID": "d3001pnilu3001cd12vd1vpnilvu0", "Game": "DCS", "AIRCRAFT": { "F-16C_50": true } } ] },
            "Button VMAX 40": { "NAME": "Button VMAX 40", "ISAXIS": false, "Groups": [], "NODES": [
              { "ID": "d3001pnilu3001cd12vd1vpnilvu0", "Game": "DCS", "AIRCRAFT": { "FA-18C_hornet": true } } ] }
          },
          "Binds": {
            "Trim Right": { "Joystick": "MOZA AB9 FFB Base {021126F0-9255-11f1-8001-444553540000}", "JButton": "JOY_BTN4", "JAxis": "" },
            "Mode Button 07": { "Joystick": "Total Controls MFBB {037D9BD0-D2C1-11f0-8011-444553540000}", "JButton": "JOY_BTN39", "JAxis": "" },
            "Trim Nose Down": { "Joystick": "MOZA AB9 FFB Base {021126F0-9255-11f1-8001-444553540000}", "JButton": "JOY_BTN3", "JAxis": "" },
            "Mode Button 04": { "Joystick": "Total Controls MFBB {037D9BD0-D2C1-11f0-8011-444553540000}", "JButton": "JOY_BTN36", "JAxis": "" },
            "Throttle Right Inverted DZ": { "Joystick": "VPC VMAX Prime Throttle {0373B0C0-D2C1-11f0-8006-444553540000}", "JButton": "", "JAxis": "JOY_RY" },
            "Flaps Axis": { "Joystick": "VPC VMAX Prime Throttle {0373B0C0-D2C1-11f0-8006-444553540000}", "JButton": "", "JAxis": "JOY_RZ" },
            "Button CP2 B1": { "Joystick": "VPC Panel #2 {03725130-D2C1-11f0-8003-444553540000}", "JButton": "JOY_BTN1", "JAxis": "" },
            "Button VMAX 40": { "Joystick": "VPC VMAX Prime Throttle {0373B0C0-D2C1-11f0-8006-444553540000}", "JButton": "JOY_BTN40", "JAxis": "" }
          }
        }
        """;

    // The engine of the development build with its DCS lists, or null (a public checkout: the tests do nothing).
    public static string? EnginePath()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return engine != null && File.Exists(engine) && Directory.Exists(Path.Combine(Path.GetDirectoryName(engine)!, "DB", "DCS")) ? engine : null;
    }

    // The profile in folder with its history: a clean saved version, then the import of 21 Oct (the duplicates).
    public static async Task<string> PrepareAsync(string engine, string folder)
    {
        Directory.CreateDirectory(folder);
        string profile = Path.Combine(folder, "IGM2 1.pr0file");
        File.WriteAllText(profile, Profile);
        await RunAsync(engine, folder, profile, "Opening the profile", ["relations", "aircraft", "set", "Mode Button 07", "--node", "DCS:" + TrimId, "--on", "FA-18C_hornet"]);
        string[][] import =
        [
            ["relations", "aircraft", "set", "Mode Button 07", "--node", "DCS:" + TrimId, "--on", "AH-64D_BLK_II_CPG|AH-64D_BLK_II_PLT|F-4E-45MC"],
            ["relations", "aircraft", "set", "Mode Button 04", "--node", "DCS:" + TrimUpId, "--on", "AH-64D_BLK_II_CPG|AH-64D_BLK_II_PLT"],
            ["relations", "aircraft", "set", "Flaps Axis", "--node", "DCS:a2004cdnil", "--on", "AJS37|M-2000C"],
            ["relations", "aircraft", "set", "Button VMAX 40", "--node", "DCS:" + CautionId, "--on", "F-16C_50"]
        ];
        // One version like the import's (its label names no relation): every step without its own version.
        foreach (string[] step in import)
            await RunAsync(engine, folder, profile, "Importing from DCS", step);
        return profile;
    }

    public static Dictionary<string, string> Environment(string folder) => new()
    {
        ["JOYPRO_DATA_DIR"] = Path.Combine(folder, "data"),
        ["JOYPRO_DEVICE_KB"] = "off",
        ["JOYPRO_PROFILE_HISTORY"] = "on"
    };

    public static async Task<EngineCommandResult> RunAsync(string engine, string folder, string profile, string note, IEnumerable<string> command)
    {
        List<string> args = ["--profile", profile, "--status", "false", "--benchmark", "false", "--load-corrections", "false", "--history-note", note];
        args.AddRange(command);
        EngineCommandResult result = await new EngineCliClient().RunProcessAsync(engine, args, new EngineRunOptions { Environment = Environment(folder) }, CancellationToken.None);
        Assert.True(result.ExitCode == 0, string.Join(" ", command) + ": " + result.StandardError + result.StandardOutput);
        return result;
    }
}
