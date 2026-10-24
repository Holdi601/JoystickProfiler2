using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests.QualityGate;

// I11 of the quality gate (QualityGate.cs): people find an action by its name, a synonym, slang, a paraphrase, an everyday
// sentence or German, offline (no model), for every function of the function map. engine/tests/fixtures/phrases/
// phrases.json holds the phrase set; "engine relations phrase-check" checks every phrase on the search box (top function and
// the top rows of every game), the catalog filter, the JoyPro assistant, Manage / Edit with AI and By description. The
// gate also runs the catalog filter of the real view model for one phrase and one description of every function.
// A finding: an entry point below its pass rate or precision; every failing phrase is listed in phrase-failures.txt.
[SupportedOSPlatform("windows")]
public sealed class QualityGatePhraseTests
{
    // Pass rates (percent) per entry point; precision of the search's top rows.
    internal static readonly Dictionary<string, double> MinPass = new(StringComparer.OrdinalIgnoreCase)
    {
        ["search"] = 95, ["catalog"] = 90, ["assistant"] = 95, ["plan"] = 90, ["describe"] = 90
    };

    internal const double MinPrecision = 90;
    internal const double MinCatalogView = 85;

    [Fact]
    public void I11_PhrasesFindTheirFunctionOnEveryEntryPoint()
    {
        if (!Gate.Enabled)
            return;
        string file = Path.Combine(Gate.RepoRoot(), "engine", "tests", "fixtures", "phrases", "phrases.json");
        if (!File.Exists(file))
        {
            Gate.Fail("I11", "phrase set missing", string.Empty, file);
            Gate.Finish("I11");
            return;
        }
        string profile = Gate.CopyProfile("phrases") ?? Gate.EmptyProfile("phrases");
        string json = string.Empty;
        List<(string Function, string Phrase, string Game)> samples = [];
        int viewPass = 0, viewCells = 0;
        StringBuilder failures = new();
        GateSession.Run(profile, async s =>
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            EngineCommandResult result = await s.Engine.RunAsync(Gate.EnginePath, ["--status", "false", "--profile", profile, "relations", "phrase-check", "--file", file, "--failures", "100000"]);
            Gate.Time("phrase check", "all entry points", watch.ElapsedMilliseconds, 1);
            json = MainWindowViewModel.ExtractJsonPayload(result.StandardOutput);
            if (json.Length == 0)
                return;
            using JsonDocument doc = JsonDocument.Parse(json);
            // One phrase and one description per function, in a game that has it (not DCS: no aircraft to pick).
            Dictionary<string, List<string>> games = [];
            if (doc.RootElement.TryGetProperty("FunctionGames", out JsonElement fg))
                foreach (JsonProperty p in fg.EnumerateObject())
                    games[p.Name] = p.Value.EnumerateArray().Select(g => g.GetString() ?? string.Empty).Where(g => g.Length > 0 && g != "DCS").ToList();
            using JsonDocument phrases = JsonDocument.Parse(File.ReadAllText(file));
            int turn = 0;
            foreach (JsonProperty function in phrases.RootElement.EnumerateObject().Where(p => !p.Name.StartsWith('_')))
            {
                if (!games.TryGetValue(function.Name, out List<string>? list) || list.Count == 0)
                    continue;
                string game = list[turn++ % list.Count];
                foreach (string kind in new[] { "phrases", "descriptions" })
                {
                    string? text = function.Value.TryGetProperty(kind, out JsonElement arr) ? arr.EnumerateArray().Skip(kind == "phrases" ? 2 : 0).Select(x => x.GetString()).FirstOrDefault() : null;
                    if (!string.IsNullOrWhiteSpace(text))
                        samples.Add((function.Name, text, game));
                }
            }
            // The catalog filter of the app: the game's list, the phrase typed, the rows the filter keeps.
            MainWindowViewModel vm = s.Vm;
            MainWindowViewModel.CatalogMeaningDelayMs = 0;
            vm.CurrentStage = UiStage.Relations;
            await vm.OpenCatalogCommand.ExecuteAsync(null);
            Dictionary<string, HashSet<string>> ownKeys = [];
            foreach (IGrouping<string, (string Function, string Phrase, string Game)> game in samples.GroupBy(x => x.Game))
            {
                await vm.SelectCatalogAsync(game.Key, string.Empty);
                foreach ((string function, string phrase, string _) in game)
                {
                    string ownKey = game.Key + "|" + function;
                    if (!ownKeys.TryGetValue(ownKey, out HashSet<string>? own))
                    {
                        // The function's own actions in that game: what the engine resolves for its id.
                        EngineCommandResult resolve = await s.Engine.RunAsync(Gate.EnginePath, ["--status", "false", "relations", "resolve", function.Replace('_', ' '), "--game", game.Key, "--per-game", "0"]);
                        own = ReadResolve(MainWindowViewModel.ExtractJsonPayload(resolve.StandardOutput), function);
                        ownKeys[ownKey] = own;
                    }
                    vm.CatalogFilterText = phrase;
                    await vm.RefreshCatalogMeaningAsync(phrase);
                    List<CatalogRowItem> visible = vm.CatalogCategories.Where(c => c.IsVisible).SelectMany(c => c.VisibleRows).ToList();
                    bool ok = own.Count == 0 || visible.Any(r => own.Contains(r.Row.Id));
                    viewCells++;
                    if (ok)
                        viewPass++;
                    else
                        failures.AppendLine("catalog view | " + function + " | \"" + phrase + "\" | " + game.Key + " -> " + (visible.Count == 0 ? "nothing" : string.Join("; ", visible.Take(3).Select(r => r.Row.Title))));
                }
            }
            vm.CatalogFilterText = string.Empty;
        });
        if (json.Length == 0)
        {
            Gate.Fail("I11", "phrase check gave no result", string.Empty, "engine relations phrase-check");
            Gate.Finish("I11");
            return;
        }
        using (JsonDocument doc = JsonDocument.Parse(json))
        {
            foreach (JsonElement entry in doc.RootElement.GetProperty("Entry").EnumerateArray())
            {
                string name = entry.GetProperty("Entry").GetString() ?? string.Empty;
                double pass = entry.GetProperty("PassRate").GetDouble();
                double precision = entry.GetProperty("Precision").GetDouble();
                int cells = entry.GetProperty("Cells").GetInt32();
                Gate.Note("I11: " + name + " pass " + pass.ToString("0.0", CultureInfo.InvariantCulture) + "% of " + cells + (entry.GetProperty("Rows").GetInt32() > 0 ? ", precision " + precision.ToString("0.0", CultureInfo.InvariantCulture) + "%" : string.Empty));
                if (MinPass.TryGetValue(name, out double min) && pass < min)
                    Gate.Fail("I11", "phrases below the pass rate on " + name, string.Empty, pass.ToString("0.0", CultureInfo.InvariantCulture) + "% < " + min.ToString(CultureInfo.InvariantCulture) + "%", cells + " checks");
                if (name == "search" && precision < MinPrecision)
                    Gate.Fail("I11", "search top rows below the precision", string.Empty, precision.ToString("0.0", CultureInfo.InvariantCulture) + "% < " + MinPrecision.ToString(CultureInfo.InvariantCulture) + "%");
                foreach (JsonElement failure in entry.GetProperty("Failures").EnumerateArray())
                    failures.AppendLine(name + " | " + failure.GetString());
            }
        }
        double view = viewCells == 0 ? 100 : 100.0 * viewPass / viewCells;
        Gate.Note("I11: catalog filter in the app " + view.ToString("0.0", CultureInfo.InvariantCulture) + "% of " + viewCells + " samples");
        if (view < MinCatalogView)
            Gate.Fail("I11", "catalog filter in the app below the pass rate", string.Empty, view.ToString("0.0", CultureInfo.InvariantCulture) + "% < " + MinCatalogView.ToString(CultureInfo.InvariantCulture) + "%", viewCells + " samples");
        string failureFile = Path.Combine(Gate.OutDir, "phrase-failures.txt");
        File.WriteAllText(failureFile, failures.ToString());
        Gate.Note("I11: failing phrases in " + failureFile);
        Gate.Finish("I11");
    }

    private static HashSet<string> ReadResolve(string json, string function)
    {
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        if (json.Length == 0)
            return ids;
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Games", out JsonElement games))
            return ids;
        foreach (JsonElement game in games.EnumerateArray())
            foreach (JsonElement action in game.GetProperty("Actions").EnumerateArray())
                if (string.Equals(action.GetProperty("Function").GetString(), function, StringComparison.OrdinalIgnoreCase))
                    ids.Add(action.GetProperty("Id").GetString() ?? string.Empty);
        return ids;
    }
}
