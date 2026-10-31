using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using Xunit;

namespace JoyPro.Gui.Tests;

// One action of one aircraft on in two relations is the same game command on two inputs. A tester's profile got 82 of
// them from the load correction's "add the DB's other aircraft" step (--auto-add-db-items, sent by the app with every
// command), which switched a module on in every node of the id that did not mention it yet, even when another relation
// had that action of that aircraft on: his DCS then had the AH-64's "Force Trim/Hold Mode Switch - AL/Right" on a MFBB
// and on a MOZA button. These run the real engine (DCS command list of the development build) in its own process and
// in the engine server, which must give the same answers and the same saved profile.
[SupportedOSPlatform("windows")]
[Collection("EngineServer")]
public sealed class DuplicateActionEngineTests
{
    private const string TrimId = "d3007pnilu3007cd25vd1vpnilvu0";

    // The tester's case in small: the AH-64 force trim right in "Trim Right" (MOZA), the same id for the Hornet in
    // "Mode Button 07" (MFBB), the AJS37 thrust in "Throttle Right" and the Apache collective (same id) in "Flaps Axis".
    // A JSON profile has no DB fingerprint, so the load correction runs on it.
    private const string Profile = """
        {
          "Relations": {
            "Trim Right": { "NAME": "Trim Right", "ISAXIS": false, "Groups": [], "NODES": [
              { "ID": "d3007pnilu3007cd25vd1vpnilvu0", "Game": "DCS", "AIRCRAFT": { "AH-64D_BLK_II_CPG": true, "AH-64D_BLK_II_PLT": true } } ] },
            "Mode Button 07": { "NAME": "Mode Button 07", "ISAXIS": false, "Groups": [], "NODES": [
              { "ID": "d3001pnilu3001cd28vd1vpnilvu0", "Game": "DCS", "AIRCRAFT": { "F-16C_50": true } },
              { "ID": "d3007pnilu3007cd25vd1vpnilvu0", "Game": "DCS", "AIRCRAFT": { "FA-18C_hornet": true } } ] },
            "Throttle Right": { "NAME": "Throttle Right", "ISAXIS": true, "Groups": [], "NODES": [
              { "ID": "a2004cdnil", "Game": "DCS", "AIRCRAFT": { "AJS37": true } } ] },
            "Flaps Axis": { "NAME": "Flaps Axis", "ISAXIS": true, "Groups": [], "NODES": [
              { "ID": "a2004cdnil", "Game": "DCS", "AIRCRAFT": { "AH-64D_BLK_II_CPG": true, "AH-64D_BLK_II_PLT": true } } ] }
          },
          "Binds": {
            "Trim Right": { "Joystick": "MOZA AB9 FFB Base {021126F0-9255-11f1-8001-444553540000}", "JButton": "JOY_BTN4", "JAxis": "" },
            "Mode Button 07": { "Joystick": "Total Controls MFBB {037D9BD0-D2C1-11f0-8011-444553540000}", "JButton": "JOY_BTN39", "JAxis": "" },
            "Throttle Right": { "Joystick": "VPC VMAX Prime Throttle {0373B0C0-D2C1-11f0-8006-444553540000}", "JButton": "", "JAxis": "JOY_RY" },
            "Flaps Axis": { "Joystick": "VPC VMAX Prime Throttle {0373B0C0-D2C1-11f0-8006-444553540000}", "JButton": "", "JAxis": "JOY_RZ" }
          }
        }
        """;

    private static readonly string[] Corrections =
        ["--load-corrections", "true", "--auto-add-db-items", "true", "--remap-missing-devices", "false", "--correction-mode", "balanced", "--confirm-corrections", "false"];

    private static readonly string[] NoCorrections = ["--load-corrections", "false"];

    private static string? EnginePath()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        // The DCS command lists live only in the development checkout (a public one has none: nothing to correct against).
        return engine != null && File.Exists(engine) && Directory.Exists(Path.Combine(Path.GetDirectoryName(engine)!, "DB", "DCS")) ? engine : null;
    }

    [Fact]
    public async Task LoadCorrection_NeverSwitchesOnAnActionAnotherRelationHas_ProcessAndServer()
    {
        string? engine = EnginePath();
        if (engine == null)
            return;
        string root = NewRoot("dup-corrections");
        try
        {
            List<(string[] Options, string[] Command)> sequence =
            [
                // A mutating command: the load correction runs and the profile is saved.
                (Corrections, ["relations", "aircraft", "set", "Mode Button 07", "--node", "DCS:" + TrimId, "--on", "FA-18C_hornet"]),
                (NoCorrections, ["relations", "list"]),
                (NoCorrections, ["relations", "duplicates"]),
                (NoCorrections, ["--text", "profile", "validate", "--fail-on-warnings", "false"])
            ];
            Side process = await RunSideAsync(engine, Path.Combine(root, "process"), sequence, server: false);
            Side server = await RunSideAsync(engine, Path.Combine(root, "server"), sequence, server: true);
            for (int i = 0; i < sequence.Count; i++)
                Assert.True(process.Outputs[i] == server.Outputs[i], "'" + string.Join(" ", sequence[i].Command) + "' differs:\nprocess: " + process.Outputs[i] + "\nserver:  " + server.Outputs[i]);
            Assert.True(File.ReadAllBytes(process.Profile).SequenceEqual(File.ReadAllBytes(server.Profile)), "the saved profiles differ");

            Dictionary<string, Dictionary<string, bool>> nodes = Nodes(process.Raw[1]);
            // The Apache's trim stays in "Trim Right" only; in "Mode Button 07" it is there switched off (left out).
            Assert.True(nodes["Trim Right|" + TrimId]["AH-64D_BLK_II_PLT"]);
            Assert.False(nodes["Mode Button 07|" + TrimId].GetValueOrDefault("AH-64D_BLK_II_PLT"));
            Assert.False(nodes["Mode Button 07|" + TrimId].GetValueOrDefault("AH-64D_BLK_II_CPG"));
            // The Hornet's id stays in "Mode Button 07" only.
            Assert.False(nodes["Trim Right|" + TrimId].GetValueOrDefault("FA-18C_hornet"));
            // The AJS37's thrust stays on "Throttle Right" (not also on the Apache collective's "Flaps Axis"), and the
            // jets' thrust is no Apache collective.
            Assert.True(nodes["Throttle Right|a2004cdnil"]["AJS37"]);
            Assert.False(nodes["Flaps Axis|a2004cdnil"].GetValueOrDefault("AJS37"));
            Assert.False(nodes["Flaps Axis|a2004cdnil"].GetValueOrDefault("F-16C_50"));
            Assert.False(nodes["Throttle Right|a2004cdnil"].GetValueOrDefault("AH-64D_BLK_II_PLT"));

            using JsonDocument duplicates = JsonDocument.Parse(MainWindowViewModelJson(process.Raw[2]));
            Assert.Equal(0, duplicates.RootElement.GetProperty("Count").GetInt32());
            Assert.Contains("Warnings: 0", process.Raw[3]);
        }
        finally
        {
            EngineServerPool.StopAll();
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Duplicates_NameTheActionAndAircraft_SuggestFromTheHistory_AndACleanExportLosesTheSecondBind()
    {
        string? engine = EnginePath();
        if (engine == null)
            return;
        string root = NewRoot("dup-resolve");
        try
        {
            string[] keepTrim = ["relations", "duplicates", "--keep", "Trim Right", "--node", "DCS:" + TrimId, "--plane", "AH-64D_BLK_II_CPG"];
            List<(string[] Options, string[] Command)> sequence =
            [
                // A first saved version without a duplicate (the history's "before"), then the Apache's trim also in
                // "Mode Button 07" by an import (its version names no relation): a duplicate.
                (NoCorrections, ["relations", "aircraft", "set", "Mode Button 07", "--node", "DCS:" + TrimId, "--on", "FA-18C_hornet"]),
                (["--load-corrections", "false", "--history-note", "Importing from DCS"], ["relations", "aircraft", "set", "Mode Button 07", "--node", "DCS:" + TrimId, "--on", "AH-64D_BLK_II_PLT|AH-64D_BLK_II_CPG"]),
                (NoCorrections, ["relations", "duplicates"]),
                (NoCorrections, ["--text", "profile", "validate", "--fail-on-warnings", "false"]),
                (NoCorrections, ["dcs", "export", "--path", "<sg1>", "--mode", "clean", "--keep-keyboard-defaults", "false"]),
                // One by hand, the rest as suggested.
                (NoCorrections, keepTrim),
                (NoCorrections, ["relations", "duplicates", "--apply-suggested"]),
                (NoCorrections, ["relations", "list"]),
                (NoCorrections, ["dcs", "export", "--path", "<sg2>", "--mode", "clean", "--keep-keyboard-defaults", "false"])
            ];
            Side process = await RunSideAsync(engine, Path.Combine(root, "process"), sequence, server: false);
            Side server = await RunSideAsync(engine, Path.Combine(root, "server"), sequence, server: true);
            for (int i = 0; i < sequence.Count; i++)
                Assert.True(process.Outputs[i] == server.Outputs[i], "'" + string.Join(" ", sequence[i].Command) + "' differs:\nprocess: " + process.Outputs[i] + "\nserver:  " + server.Outputs[i]);

            using (JsonDocument found = JsonDocument.Parse(MainWindowViewModelJson(process.Raw[2])))
            {
                JsonElement list = found.RootElement.GetProperty("Duplicates");
                Assert.Equal(2, list.GetArrayLength());
                foreach (JsonElement d in list.EnumerateArray())
                {
                    Assert.Equal("Trim Right", d.GetProperty("Suggested").GetString());
                    Assert.Equal("history", d.GetProperty("SuggestionSource").GetString());
                    string message = d.GetProperty("Message").GetString()!;
                    Assert.Contains("'Force Trim/Hold Mode Switch - AL/Right' of AH-64D_BLK_II_", message);
                    Assert.Contains("Mode Button 07 (Total Controls MFBB \u00B7 BTN 39), Trim Right (MOZA AB9 FFB Base \u00B7 BTN 4)", message);
                    Assert.Equal(96, d.GetProperty("Confidence").GetInt32());
                    Assert.DoesNotContain(TrimId, message);
                }
            }
            // The check's warning: readable, no raw id.
            // One line per relation pair: the aircraft as people call it, the action, the relations with device and button.
            Assert.Contains("AH-64D Block II (CPG and Pilot): 'Force Trim/Hold Mode Switch - AL/Right' is in 2 relations: 'Mode Button 07' (Total Controls MFBB \u00B7 BTN 39) and 'Trim Right' (MOZA AB9 FFB Base \u00B7 BTN 4). DCS gets two binds for it. 96% sure: keep it in 'Trim Right'.", process.Raw[3]);
            Assert.DoesNotContain(TrimId + "__", process.Raw[3]);

            // Before: the Apache gets the trim on both devices; after resolving, a clean export writes it on the MOZA only.
            Assert.True(PlaneFileHas(process.Folder, "sg1", "AH-64D_BLK_II_PLT", "Total Controls MFBB", TrimId));
            Assert.True(PlaneFileHas(process.Folder, "sg1", "AH-64D_BLK_II_PLT", "MOZA AB9 FFB Base", TrimId));
            Assert.False(PlaneFileHas(process.Folder, "sg2", "AH-64D_BLK_II_PLT", "Total Controls MFBB", TrimId));
            Assert.False(PlaneFileHas(process.Folder, "sg2", "AH-64D_BLK_II_CPG", "Total Controls MFBB", TrimId));
            Assert.True(PlaneFileHas(process.Folder, "sg2", "AH-64D_BLK_II_PLT", "MOZA AB9 FFB Base", TrimId));
            // The Hornet keeps its bind of the id on the MFBB.
            Assert.True(PlaneFileHas(process.Folder, "sg2", "FA-18C_hornet", "Total Controls MFBB", TrimId));

            Dictionary<string, Dictionary<string, bool>> nodes = Nodes(process.Raw[7]);
            Assert.False(nodes["Mode Button 07|" + TrimId]["AH-64D_BLK_II_PLT"]);
            Assert.False(nodes["Mode Button 07|" + TrimId]["AH-64D_BLK_II_CPG"]);
            Assert.True(nodes["Mode Button 07|" + TrimId]["FA-18C_hornet"]);
            Assert.True(nodes["Trim Right|" + TrimId]["AH-64D_BLK_II_PLT"]);
        }
        finally
        {
            EngineServerPool.StopAll();
            TryDelete(root);
        }
    }

    // The user put the action into the second relation himself (the version is his edit of that relation): the history
    // does not decide against him, it only says so.
    [Fact]
    public async Task Duplicates_TheUsersOwnEdit_IsNoHistorySuggestion()
    {
        string? engine = EnginePath();
        if (engine == null)
            return;
        string root = NewRoot("dup-deliberate");
        try
        {
            List<(string[] Options, string[] Command)> sequence =
            [
                (NoCorrections, ["relations", "aircraft", "set", "Mode Button 07", "--node", "DCS:" + TrimId, "--on", "FA-18C_hornet"]),
                (NoCorrections, ["relations", "aircraft", "set", "Mode Button 07", "--node", "DCS:" + TrimId, "--on", "AH-64D_BLK_II_PLT"]),
                (NoCorrections, ["relations", "duplicates"])
            ];
            Side process = await RunSideAsync(engine, Path.Combine(root, "process"), sequence, server: false);
            using JsonDocument found = JsonDocument.Parse(MainWindowViewModelJson(process.Raw[2]));
            JsonElement d = Assert.Single(found.RootElement.GetProperty("Duplicates").EnumerateArray());
            Assert.NotEqual("history", d.GetProperty("SuggestionSource").GetString());
            Assert.Contains(d.GetProperty("Reasons").EnumerateArray(), r => r.GetProperty("Code").GetString() == "history-deliberate" && r.GetProperty("Relation").GetString() == "Mode Button 07");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private sealed record Side(string Folder, string Profile, List<string> Outputs, List<string> Raw);

    private static async Task<Side> RunSideAsync(string engine, string folder, List<(string[] Options, string[] Command)> sequence, bool server)
    {
        Directory.CreateDirectory(folder);
        string profile = Path.Combine(folder, "p.pr0file");
        File.WriteAllText(profile, Profile);
        Dictionary<string, string> environment = new()
        {
            ["JOYPRO_DATA_DIR"] = Path.Combine(folder, "data"),
            ["JOYPRO_DEVICE_KB"] = "off",
            ["JOYPRO_PROFILE_HISTORY"] = "on",
            ["JOYPRO_TEST_ASSUME_INSTALLED"] = "1",
            ["JOYPRO_TEST_IGNORE_RUNNING_GAMES"] = "1",
            ["JOYPRO_INSTALL_DETECTION"] = "off"
        };
        EngineCliClient client = new();
        List<string> outputs = [];
        List<string> raw = [];
        foreach ((string[] options, string[] command) in sequence)
        {
            List<string> args = ["--profile", profile, "--status", "false", "--benchmark", "false"];
            args.AddRange(options);
            args.AddRange(command.Select(a => a.StartsWith('<') ? Path.Combine(folder, a.Trim('<', '>')) : a));
            EngineRunOptions runOptions = new() { Environment = environment };
            EngineCommandResult result;
            if (server)
            {
                EngineCommandResult? served = await EngineServerPool.TryRunAsync(engine, args, "test", runOptions, CancellationToken.None);
                Assert.NotNull(served);
                result = served!;
            }
            else
            {
                result = await client.RunProcessAsync(engine, args, runOptions, CancellationToken.None);
            }
            Assert.True(result.ExitCode == 0, string.Join(" ", command) + " failed: " + result.StandardError + result.StandardOutput);
            raw.Add(result.StandardOutput);
            outputs.Add(System.Text.RegularExpressions.Regex.Replace(
                result.StandardOutput.Replace(folder, "<side>", StringComparison.OrdinalIgnoreCase).Replace(folder.Replace('\\', '/'), "<side>", StringComparison.OrdinalIgnoreCase)
                    .Replace(folder.Replace("\\", "\\\\"), "<side>", StringComparison.OrdinalIgnoreCase),
                "\"(GeneratedUtc|CreatedUtc|RecordedUtc|SuggestionVersion|Detail|CausedAt)\":\"[^\"]*\"|\"(\\w*Milliseconds)\":\\d+|(the only one with it on) [^)]*", "<x>"));
        }
        return new Side(folder, profile, outputs, raw);
    }

    // "relation|id" -> the node's aircraft.
    private static Dictionary<string, Dictionary<string, bool>> Nodes(string relationsList)
    {
        Dictionary<string, Dictionary<string, bool>> nodes = new(StringComparer.OrdinalIgnoreCase);
        using JsonDocument doc = JsonDocument.Parse(MainWindowViewModelJson(relationsList));
        foreach (JsonElement relation in doc.RootElement.GetProperty("Relations").EnumerateArray())
        {
            foreach (JsonElement node in relation.GetProperty("Nodes").EnumerateArray())
            {
                Dictionary<string, bool> aircraft = new(StringComparer.OrdinalIgnoreCase);
                if (node.TryGetProperty("Aircraft", out JsonElement planes) && planes.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty plane in planes.EnumerateObject())
                        aircraft[plane.Name] = plane.Value.GetBoolean();
                }
                nodes[relation.GetProperty("Name").GetString() + "|" + node.GetProperty("Id").GetString()] = aircraft;
            }
        }
        return nodes;
    }

    // The JSON of an answer (a mutating command adds an "Auto-saved profile" line after it).
    private static string MainWindowViewModelJson(string output)
    {
        int start = output.IndexOf('{');
        int end = output.LastIndexOf('}');
        return start >= 0 && end > start ? output[start..(end + 1)] : output;
    }

    // Whether the exported diff.lua of the aircraft and device names the command id.
    private static bool PlaneFileHas(string side, string savedGames, string plane, string device, string id)
    {
        string folder = Path.Combine(side, savedGames, "Config", "Input", plane, "joystick");
        if (!Directory.Exists(folder))
            return false;
        return Directory.GetFiles(folder, device + " {*}.diff.lua").Any(f => File.ReadAllText(f).Contains("[\"" + id + "\"]", StringComparison.Ordinal));
    }

    private static string NewRoot(string name)
    {
        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception)
        {
        }
    }
}
