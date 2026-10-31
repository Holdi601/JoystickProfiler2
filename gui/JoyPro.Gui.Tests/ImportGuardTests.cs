using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Tester's log on 2.4.2 (imacken): he moved the AH-64D's Master Warning to another button, pressed Import on the DCS
// card before exporting, and the import put the old bind back (twice). Now an import asks first when the profile has
// changes for the game that were never exported (engine sync pending): Export first, Import anyway or Cancel. The
// watcher's take-over offers only what the game itself changed, never a revert of the user's unexported JoyPro edit.
// Real engine on temp folders (never the user's Saved Games); never shows a window.
[Collection("ProcessEnvironment")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ImportGuardTests
{
    private const string Panel = "VPC Panel #2 {03725130-D2C1-11f0-8003-444553540000}";
    private const string Warning = "d3019pnilu3019cd11vd1vpnilvu0";
    private const string Caution = "d3018pnilu3018cd11vd1vpnilvu0";
    private const string Apaches = "AH-64D_BLK_II_CPG|AH-64D_BLK_II_PLT";
    private const string Planes = "AH-64D_BLK_II_CPG,AH-64D_BLK_II_PLT,F-16C_50";

    // B1 / B3 exported to a temp Saved Games, then Master Warning and Master Caution swapped in JoyPro (not exported).
    private static void Prepare(string engine, TestFolder folder)
    {
        Engine(engine, folder, "--profile", folder.Profile, "relations", "add", "Button CP2 B1", "--button", "--node", "DCS:" + Warning + ":" + Apaches,
            "--node", "DCS:d3001pnilu3001cd12vd1vpnilvu0:F-16C_50", "--device", Panel, "--input", "JOY_BTN1");
        Engine(engine, folder, "--profile", folder.Profile, "relations", "add", "Button CP2 B3", "--button", "--node", "DCS:" + Caution + ":" + Apaches,
            "--device", Panel, "--input", "JOY_BTN3");
        Engine(engine, folder, "--profile", folder.Profile, "dcs", "export", folder.SavedGames, "--planes", Planes, "--text");
        Assert.Contains("\"Count\":0", Engine(engine, folder, "--profile", folder.Profile, "sync", "pending", "--game", "DCS", "--path", folder.SavedGames));
        Engine(engine, folder, "--profile", folder.Profile, "relations", "item", "move-to-input", "Button CP2 B1", "--node", "DCS:" + Warning + ":" + Apaches,
            "--device", Panel, "--input", "JOY_BTN3", "--if-taken", "swap");
    }

    [Fact]
    public void Engine_Pending_NamesTheMove_AndAnExportClearsIt()
    {
        string? engine = FindEngine();
        if (engine == null)
            return;
        using TestFolder folder = new();
        Prepare(engine, folder);
        string pending = Engine(engine, folder, "--profile", folder.Profile, "sync", "pending", "--game", "DCS", "--path", folder.SavedGames);
        Assert.Contains("\"Known\":true", pending);
        Assert.Contains("\"Count\":2", pending);
        Assert.Contains("\"Kind\":\"moved\"", pending);
        Assert.Contains(Warning, pending);
        Engine(engine, folder, "--profile", folder.Profile, "dcs", "export", folder.SavedGames, "--planes", Planes, "--text");
        Assert.Contains("\"Count\":0", Engine(engine, folder, "--profile", folder.Profile, "sync", "pending", "--game", "DCS", "--path", folder.SavedGames));
    }

    // The game changes one bind itself (Press To Test on BTN 9 in the pilot's file): the take-over offers that one, not
    // a revert of the swap the user made in JoyPro.
    [Fact]
    public void Engine_TakeOver_OffersOnlyTheGameChange_NotARevertOfTheUnexportedMove()
    {
        string? engine = FindEngine();
        if (engine == null)
            return;
        using TestFolder folder = new();
        Prepare(engine, folder);
        string file = Directory.GetFiles(Path.Combine(folder.SavedGames, "Config", "Input", "AH-64D_BLK_II_PLT", "joystick"), "*.diff.lua").Single();
        string text = File.ReadAllText(file).Replace("\r\n", "\n");
        Assert.Contains("  [\"keyDiffs\"] = {\n", text);
        text = text.Replace("  [\"keyDiffs\"] = {\n", "  [\"keyDiffs\"] = {\n    [\"d3020pnilu3020cd11vd1vpnilvu0\"] = {\n      [\"name\"] = \"Press To Test Button\",\n      [\"added\"] = {\n        [1] = {\n          [\"key\"] = \"JOY_BTN9\",\n        },\n      },\n    },\n");
        File.WriteAllText(file, text.Replace("\n", "\r\n"));
        string diff = Engine(engine, folder, "--profile", folder.Profile, "sync", "diff", "--game", "DCS", "--path", folder.SavedGames);
        Assert.Contains("d3020pnilu3020cd11vd1vpnilvu0", diff);
        Assert.DoesNotContain(Warning, diff);
        Assert.DoesNotContain(Caution, diff);
        // The take-over keeps the user's edit pending: Warning stays where JoyPro moved it.
        Assert.Contains("\"Count\":2", Engine(engine, folder, "--profile", folder.Profile, "sync", "pending", "--game", "DCS", "--path", folder.SavedGames));
    }

    // Import on the DCS card: asked once; Cancel imports nothing, Export first exports and imports nothing, Import anyway
    // imports (and the old binds come back, as the question said).
    [Fact]
    public async Task Import_WithUnexportedChanges_AsksExportFirstImportAnywayOrCancel()
    {
        string? engine = FindEngine();
        if (engine == null || !Directory.Exists(Path.Combine(Path.GetDirectoryName(engine)!, "DB", "DCS")))
            return;
        foreach (string answer in new[] { "cancel", "export", "import", "import-dont-ask" })
        {
            using TestFolder folder = new();
            Prepare(engine, folder);
            MainWindowViewModel vm = new(new EngineCliClient(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = engine,
                ProfilePath = folder.Profile
            };
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            List<ConfirmRequest> asked = [];
            vm.ConfirmHandler = request =>
            {
                asked.Add(request);
                if (request.SecondaryLabel.Length == 0)
                    return Task.FromResult(true);
                if (answer == "export")
                    request.SecondaryChosen = true;
                if (answer == "import-dont-ask")
                    request.DontAskAgain = true;
                return Task.FromResult(answer != "cancel");
            };
            // Both paths on the temp Saved Games: "Export first" must never write the real DCS config of this PC.
            vm.ImportDcsPath = folder.SavedGames;
            vm.ExportDcsPath = folder.SavedGames;
            string realInput = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "DCS", "Config", "Input");
            DateTime started = DateTime.Now.AddSeconds(-1);
            vm.ImportAllDcsPlanes = true;
            vm.ImportMerge = true;
            await vm.ImportDcsCommand.ExecuteAsync(null);

            Assert.False(Directory.Exists(realInput) && Directory.EnumerateFiles(realInput, "*", SearchOption.AllDirectories).Any(f => File.GetLastWriteTime(f) > started),
                "the test wrote into the real DCS config");
            ConfirmRequest question = Assert.Single(asked, r => r.SecondaryLabel.Length > 0);
            Assert.Contains("DCS", question.Title);
            Assert.Contains("2 binds", question.Message);
            Assert.Contains("Master", question.Message);
            Assert.Equal("Export first", question.SecondaryLabel);
            Assert.Equal("Import (drop my changes for DCS)", question.ConfirmLabel);
            Assert.False(question.IsDanger);
            Assert.Contains("History", question.Message);
            Assert.Equal(MainWindowViewModel.ConfirmImportUnexportedKey, question.DontAskKey);

            string b3 = Engine(engine, folder, "--profile", folder.Profile, "relations", "show", "Button CP2 B3", "--text");
            string b1 = Engine(engine, folder, "--profile", folder.Profile, "relations", "show", "Button CP2 B1", "--text");
            string pending = Engine(engine, folder, "--profile", folder.Profile, "sync", "pending", "--game", "DCS", "--path", folder.SavedGames);
            switch (answer)
            {
                case "cancel":
                    Assert.Contains(Warning, b3);
                    Assert.DoesNotContain(Warning, b1);
                    Assert.Contains("\"Count\":2", pending);
                    break;
                case "export":
                    // The game has the swap now; nothing came back.
                    Assert.Contains(Warning, b3);
                    Assert.DoesNotContain(Warning, b1);
                    Assert.Contains("\"Count\":0", pending);
                    // (2.5.0: "Export first" uses the default clean + nuke: the other devices of this PC get files of their own.)
                    string plt = File.ReadAllText(Directory.GetFiles(Path.Combine(folder.SavedGames, "Config", "Input", "AH-64D_BLK_II_PLT", "joystick"), "*.diff.lua")
                        .Single(f => Path.GetFileName(f).StartsWith("VPC Panel #2", StringComparison.Ordinal)));
                    int at = plt.IndexOf(Warning, StringComparison.Ordinal);
                    int next = plt.IndexOf("[\"d", at + Warning.Length, StringComparison.Ordinal);
                    string block = at < 0 ? string.Empty : plt[at..(next < 0 ? plt.Length : next)];
                    Assert.Contains("JOY_BTN3", block);
                    // Nothing unexported any more: the next import does not ask.
                    await vm.ImportDcsCommand.ExecuteAsync(null);
                    Assert.Single(asked, r => r.SecondaryLabel.Length > 0);
                    break;
                case "import-dont-ask":
                    // Switched off: the next import with a new unexported change imports without asking; Settings >
                    // Confirmations switches it on again.
                    Assert.False(vm.AskBeforeImportDropsChanges);
                    Engine(engine, folder, "--profile", folder.Profile, "relations", "item", "move-to-input", "Button CP2 B1", "--node", "DCS:" + Warning + ":" + Apaches,
                        "--device", Panel, "--input", "JOY_BTN3", "--if-taken", "swap");
                    await vm.ImportDcsCommand.ExecuteAsync(null);
                    Assert.Single(asked, r => r.SecondaryLabel.Length > 0);
                    vm.AskBeforeImportDropsChanges = true;
                    Assert.True(vm.AskBeforeImportDropsChanges);
                    break;
                default:
                    // What DCS still had came back (Warning on BTN 1 again): the question said so.
                    Assert.Contains(Warning, b1);
                    break;
            }
        }
    }

    // The search's match lines name the aircraft readably: the module's name when JoyPro knows it, else the id without
    // underscores (2.4.3 render: "AH-64D_BLK_II (2)", "F-16C_50").
    [Fact]
    public void MatchLines_NameTheAircraftReadably()
    {
        MainWindowViewModel vm = new(new AxisTuningFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
        IReadOnlyDictionary<string, string> before = RelationListItem.AircraftNames;
        try
        {
            RelationListItem.AircraftNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Assert.Equal("DCS World  ·  AH-64D BLK II (2)", vm.SearchMatchWhere("DCS", ["AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT"]));
            Assert.DoesNotContain("_", vm.SearchMatchWhere("DCS", ["F-16C_50"]));
            RelationListItem.AircraftNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AH-64D_BLK_II"] = "AH-64D BLK.II",
                ["F-16C_50"] = "F-16C bl.50"
            };
            Assert.Equal("DCS World  ·  AH-64D BLK.II (2)", vm.SearchMatchWhere("DCS", ["AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT"]));
            Assert.Equal("DCS World  ·  F-16C bl.50", vm.SearchMatchWhere("DCS", ["F-16C_50"]));
        }
        finally
        {
            RelationListItem.AircraftNames = before;
        }
    }

    private static string? FindEngine()
    {
        if (Environment.GetEnvironmentVariable("JOYPRO_TEST_ENGINE") is { Length: > 0 } other)
            return File.Exists(other) ? other : null;
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        string? path = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return path != null && File.Exists(path) ? path : null;
    }

    private static string Engine(string engine, TestFolder folder, params string[] args)
    {
        ProcessStartInfo info = new(engine) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        foreach ((string name, string value) in TestFolder.Variables(folder.Data))
            info.Environment[name] = value;
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "engine " + string.Join(" ", args) + " failed: " + output);
        return output;
    }

    private sealed class TestFolder : IDisposable
    {
        private readonly Dictionary<string, string?> previous = new();

        public TestFolder()
        {
            Root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "import-guard-" + Guid.NewGuid().ToString("N"));
            Data = Path.Combine(Root, "data");
            SavedGames = Path.Combine(Root, "SavedGames");
            Profile = Path.Combine(Root, "test.pr0file");
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(SavedGames);
            foreach ((string name, string value) in Variables(Data))
            {
                previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public static IEnumerable<(string Name, string Value)> Variables(string data) =>
        [
            ("JOYPRO_DATA_DIR", data), ("JOYPRO_ENGINE_SERVER", "off"), ("JOYPRO_TEST_IGNORE_RUNNING_GAMES", "1"),
            ("JOYPRO_TEST_ASSUME_INSTALLED", "1")
        ];

        public string Root { get; }

        public string Data { get; }

        public string SavedGames { get; }

        public string Profile { get; }

        public void Dispose()
        {
            foreach (KeyValuePair<string, string?> pair in previous)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
