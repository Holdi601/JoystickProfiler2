using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using Xunit;

namespace JoyPro.Gui.Tests;

// "AI mode only works on DCS" (user report): every AI path must understand every game. The engine's AI matrix
// (engine relations ai-matrix, EngineAiMatrix) asks the JoyPro assistant, Manage / Edit with AI, the fill, By
// description and the relation chat typical requests for each game (flaps / landing gear / pitch / landing lights;
// boost / strafe / jump drive for the space games; game names and short names; questions) and checks what the local
// model would be shown. Offline paths are deterministic; the model path runs against the engine's fake model server and
// the test reads what it was sent.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class AiAllGamesTests
{
    private static string? Engine()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "bin")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return engine != null && File.Exists(engine) ? engine : null;
    }

    private static async Task<(int Exit, string Output)> Run(string engine, string folder, IEnumerable<string> args, IDictionary<string, string>? extra = null)
    {
        ProcessStartInfo info = new(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(engine)! };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        info.Environment["JOYPRO_DATA_DIR"] = Path.Combine(folder, "data");
        foreach (KeyValuePair<string, string> pair in extra ?? new Dictionary<string, string>())
            info.Environment[pair.Key] = pair.Value;
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await errors);
    }

    // A small profile like the user's: Roll (axis) and Gear TOGGLE (button) with the DCS aircraft only.
    private static async Task<(string Folder, string Profile)> Profile(string engine)
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "aigames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string profile = Path.Combine(folder, "games.pr0file");
        (int exit, string output) = await Run(engine, folder, ["--profile", profile, "relations", "from-text", "roll in DCS", "--name", "Roll"]);
        Assert.True(exit == 0, output);
        (exit, output) = await Run(engine, folder, ["--profile", profile, "relations", "from-text", "gear toggle in DCS", "--name", "Gear TOGGLE"]);
        Assert.True(exit == 0, output);
        return (folder, profile);
    }

    private static void Delete(string folder)
    {
        try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Json(string output)
    {
        int start = output.IndexOf('{');
        int end = output.LastIndexOf('}');
        return start >= 0 && end > start ? output[start..(end + 1)] : "{}";
    }

    [Fact]
    public async Task AiMatrix_EveryPathAnswersEveryGame()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        (string folder, string profile) = await Profile(engine);
        try
        {
            string games = "DCS|MSFS2024|XPlane12|StarCitizen|EliteDangerous|X4|ProjectWingman|IL21946|AceCombat7";
            (int exit, string output) = await Run(engine, folder, ["--profile", profile, "relations", "ai-matrix", "--games", games]);
            Assert.True(exit == 0, output);
            using JsonDocument doc = JsonDocument.Parse(Json(output));
            List<JsonElement> cells = doc.RootElement.GetProperty("Cells").EnumerateArray().ToList();
            // 17 requests per game plus the two "every game" requests.
            Assert.True(cells.Count >= 9 * 17, "cells: " + cells.Count);
            List<string> failed = cells.Where(c => !c.GetProperty("Pass").GetBoolean())
                .Select(c => c.GetProperty("Game").GetString() + " | " + c.GetProperty("Path").GetString() + " | " + c.GetProperty("Request").GetString() + " -> " +
                             c.GetProperty("Verdict").GetString() + " | " + c.GetProperty("Answer").GetString()).ToList();
            Assert.True(failed.Count == 0, "AI matrix failures:\n" + string.Join("\n", failed));
            // Every path really reached the games: actions of the named game, not of DCS only.
            foreach (string game in new[] { "XPlane12", "StarCitizen", "EliteDangerous", "MSFS2024" })
            {
                Assert.Contains(cells, c => c.GetProperty("Game").GetString() == game && c.GetProperty("Path").GetString() == "assistant:create" && c.GetProperty("GameActions").GetInt32() > 0);
                Assert.Contains(cells, c => c.GetProperty("Game").GetString() == game && c.GetProperty("Path").GetString() == "describe" && c.GetProperty("GameActions").GetInt32() > 0);
            }
            // A question gets JoyPro's answer about that game's action, by name.
            JsonElement ask = cells.First(c => c.GetProperty("Game").GetString() == "StarCitizen" && c.GetProperty("Path").GetString() == "assistant:ask");
            Assert.Contains("Star Citizen", ask.GetProperty("Answer").GetString());
            Assert.DoesNotContain("spaceship_movement", ask.GetProperty("Answer").GetString());
        }
        finally
        {
            Delete(folder);
        }
    }

    [Fact]
    public async Task Assistant_WithModel_ShowsTheNamedGameAndKeepsToIt()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        (string folder, string profile) = await Profile(engine);
        try
        {
            // The fake model looks the flaps up without a game (JoyPro keeps to the game the request names), adds the
            // set and is done.
            string script = Path.Combine(folder, "script.txt");
            File.WriteAllText(script, "{\"tool\":\"resolve\",\"query\":\"flaps\",\"kind\":\"button\"}\n#next\n{\"tool\":\"create_relation\",\"name\":\"Flaps XP\",\"kind\":\"button\"}\n#next\n" +
                                      "{\"tool\":\"add_to_relation\",\"relation\":\"Flaps XP\",\"ids\":[\"S1\"]}\n#next\n{\"tool\":\"done\",\"reply\":\"Added the X-Plane 12 flaps.\"}");
            string log = Path.Combine(folder, "fake.log");
            (int exit, string output) = await Run(engine, folder, ["--profile", profile, "relations", "assistant", "make a new relation Flaps XP with the flaps of xp12", "--llm", "on"],
                new Dictionary<string, string> { ["JOYPRO_LLM_FAKE_RESPONSE"] = script, ["JOYPRO_LLM_FAKE_LOG"] = log });
            Assert.True(exit == 0, output);
            string sent = File.ReadAllText(log);
            // The model is told which game the request means, every game JoyPro knows, and that game's own actions.
            Assert.Contains("The request names X-Plane 12", sent);
            Assert.Contains("GAMES with a command list", sent);
            Assert.Contains("Star Citizen = StarCitizen", sent);
            Assert.Contains("X-Plane 12 actions for", sent);
            using JsonDocument doc = JsonDocument.Parse(Json(output));
            List<JsonElement> added = doc.RootElement.GetProperty("Changes").EnumerateArray().Where(c => c.GetProperty("Kind").GetString() == "added").ToList();
            Assert.NotEmpty(added);
            Assert.All(added, c => Assert.Equal("XPlane12", c.GetProperty("Game").GetString()));
        }
        finally
        {
            Delete(folder);
        }
    }

    [Fact]
    public async Task ManageWithAi_WithModel_ContextHasEveryGameAndTheirActions()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        (string folder, string profile) = await Profile(engine);
        try
        {
            string answer = Path.Combine(folder, "answer.txt");
            File.WriteAllText(answer, "{\"ops\":[{\"op\":\"create\",\"name\":\"Boost SC\",\"kind\":\"button\",\"describe\":\"boost in Star Citizen\"}],\"reply\":\"Creates Boost SC.\"}");
            string log = Path.Combine(folder, "fake.log");
            (int exit, string output) = await Run(engine, folder, ["--profile", profile, "relations", "plan", "ask", "make a relation for boost in Star Citizen", "--llm", "on"],
                new Dictionary<string, string> { ["JOYPRO_LLM_FAKE_RESPONSE"] = answer, ["JOYPRO_LLM_FAKE_LOG"] = log });
            Assert.True(exit == 0, output);
            string sent = File.ReadAllText(log);
            // The profile has only DCS; the model still learns every game and the Star Citizen boost actions.
            Assert.Contains("GAMES with a command list", sent);
            Assert.Contains("X-Plane 12 = XPlane12", sent);
            Assert.Contains("Star Citizen actions for", sent);
            using JsonDocument doc = JsonDocument.Parse(Json(output));
            JsonElement step = doc.RootElement.GetProperty("Steps").EnumerateArray().First();
            Assert.True(step.GetProperty("Ok").GetBoolean(), output);
            string details = string.Join("\n", step.GetProperty("Details").EnumerateArray().Select(d => d.GetString()));
            Assert.Contains("Star Citizen: ", details);
            Assert.DoesNotContain("|", details);
            Assert.DoesNotContain("v_", details);
        }
        finally
        {
            Delete(folder);
        }
    }

    // The catalog filter finds actions by meaning, not only by the words of their titles.
    [Fact]
    public async Task CatalogFilter_FindsByMeaning()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        (string folder, string profile) = await Profile(engine);
        int delay = JoyPro.Gui.ViewModels.MainWindowViewModel.CatalogMeaningDelayMs;
        try
        {
            JoyPro.Gui.ViewModels.MainWindowViewModel.CatalogMeaningDelayMs = 0;
            JoyPro.Gui.ViewModels.MainWindowViewModel vm = new(new DataDirEngineClient(Path.Combine(folder, "data")), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            { EnginePath = engine, ProfilePath = profile };
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            await vm.OpenCatalogCommand.ExecuteAsync(null);
            await vm.SelectCatalogAsync("XPlane12", string.Empty);
            if (vm.CatalogRowItems.Count == 0)
                return; // no X-Plane 12 list in this checkout
            vm.CatalogFilterText = "drop the wheels";
            await vm.RefreshCatalogMeaningAsync("drop the wheels");
            List<string> visible = vm.CatalogCategories.Where(c => c.IsVisible).SelectMany(c => c.VisibleRows).Select(r => r.Row.Title).ToList();
            Assert.Contains(visible, t => t.Contains("gear", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(visible, t => t.Contains("flap", StringComparison.OrdinalIgnoreCase));
            vm.CatalogFilterText = string.Empty;
            await vm.RefreshCatalogMeaningAsync(string.Empty);
            Assert.True(vm.CatalogCategories.All(c => c.Matching == c.Rows.Count || !c.IsVisible || c.Matching > 0));
        }
        finally
        {
            JoyPro.Gui.ViewModels.MainWindowViewModel.CatalogMeaningDelayMs = delay;
            Delete(folder);
        }
    }

    [Fact]
    public void AssistantStatus_SaysWhatHappened()
    {
        string created = "{\"Changed\":true,\"Reply\":\"x\",\"Relations\":[{\"Name\":\"Landing Gear\",\"Created\":true,\"Added\":{\"Star Citizen\":3},\"Actions\":3}]," +
                         "\"Changes\":[{\"Relation\":\"Landing Gear\",\"Kind\":\"created\"},{\"Relation\":\"Landing Gear\",\"Kind\":\"added\"},{\"Relation\":\"Landing Gear\",\"Kind\":\"added\"},{\"Relation\":\"Landing Gear\",\"Kind\":\"added\"}]}";
        AssistantMessageItem answer = JoyPro.Gui.ViewModels.MainWindowViewModel.ParseAssistantAnswer(created);
        Assert.Equal("'Landing Gear' created, 3 actions added. Ctrl+Z undoes it.", JoyPro.Gui.ViewModels.MainWindowViewModel.AssistantStatus(answer));
        string added = "{\"Changed\":true,\"Relations\":[{\"Name\":\"Roll\",\"Added\":{\"X-Plane 12\":1},\"Actions\":20}],\"Changes\":[{\"Relation\":\"Roll\",\"Kind\":\"added\"}]}";
        Assert.Equal("'Roll': 1 action added. Ctrl+Z undoes it.", JoyPro.Gui.ViewModels.MainWindowViewModel.AssistantStatus(JoyPro.Gui.ViewModels.MainWindowViewModel.ParseAssistantAnswer(added)));
        AssistantMessageItem question = JoyPro.Gui.ViewModels.MainWindowViewModel.ParseAssistantAnswer("{\"Changed\":false,\"Question\":true,\"Reply\":\"In Elite Dangerous ...\"}");
        Assert.True(question.IsQuestion);
        Assert.False(question.Changed);
    }

    [Fact]
    public void FillPreview_NamesGamesByName()
    {
        AiFillRelationItem relation = new("Roll", []);
        AiFillActionItem xp = new(relation, "XPlane12:axis/roll:XPlane12", "XPlane12", "XPlane12", "Roll", "flight controls", "rules", "high", "", true);
        AiFillActionItem dcs = new(relation, "DCS:a1:F-16C_50", "DCS", "F-16C_50", "Roll", "", "rules", "high", "", true);
        Assert.Equal("X-Plane 12", xp.Where);
        Assert.Equal("F-16C_50", dcs.Where);
    }
}
