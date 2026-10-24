using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests.QualityGate;

// I9 of the quality gate (QualityGate.cs): every game the UI names - Settings > Games and the Start page (engine games
// installs), the "Supported games" page (engine games support), the game badges' labels and the trailer's game list -
// has an implementation: an integration and catalog actions (and a function map, without which search by function and
// suggestions cannot reach it). Game labels are names, not abbreviations nobody knows.
[SupportedOSPlatform("windows")]
public sealed class QualityGateUiStringsTests
{
    // Short labels everybody knows.
    private static readonly HashSet<string> KnownShortLabels = new(StringComparer.OrdinalIgnoreCase) { "DCS", "X4", "FSX", "BMS" };

    [Fact]
    public void I9_GamesNamedInTheUiHaveAnImplementation()
    {
        if (!Gate.Enabled)
            return;
        Dictionary<string, int> catalog = new(StringComparer.OrdinalIgnoreCase);
        List<(string GameId, string Name)> installs = [];
        HashSet<string> integrations = new(StringComparer.OrdinalIgnoreCase) { "DCS", "IL2Game", "StarCitizen" };
        List<JsonElement> support = [];
        string supportJson = string.Empty;
        GateSession.Run(null, async s =>
        {
            MainWindowViewModel vm = s.Vm;
            await vm.OpenCatalogCommand.ExecuteAsync(null);
            foreach (CatalogGameOption game in vm.CatalogGames)
                catalog[game.Game] = game.Actions;
            await vm.RefreshGameInstallsCommand.ExecuteAsync(null);
            foreach (GameInstallItem game in vm.GameInstalls)
                installs.Add((game.GameId, game.Name));
            await vm.RefreshRegistryGamesCommand.ExecuteAsync(null);
            System.Reflection.FieldInfo? field = typeof(MainWindowViewModel).GetField("allRegistryGames", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            foreach (RegistryGameItem game in field?.GetValue(vm) as IEnumerable<RegistryGameItem> ?? vm.RegistryGames)
                integrations.Add(game.GameId);
            // The Start page's "Supported games" button writes this page from the same engine data.
            EngineCommandResult result = await s.Engine.RunAsync(Gate.EnginePath, ["--status", "false", "games", "support"]);
            supportJson = MainWindowViewModel.ExtractJsonPayload(result.StandardOutput);
        });
        using JsonDocument doc = JsonDocument.Parse(supportJson.Length > 0 ? supportJson : "{}");
        List<(string GameId, string Title, string Level, bool InEngine, int Functions)> rows = [];
        if (doc.RootElement.TryGetProperty("Rows", out JsonElement list))
        {
            foreach (JsonElement row in list.EnumerateArray())
                rows.Add((Str(row, "GameId"), Str(row, "Title"), Str(row, "Level"), row.TryGetProperty("InEngine", out JsonElement ie) && ie.ValueKind == JsonValueKind.True,
                    row.TryGetProperty("Functions", out JsonElement fn) && fn.ValueKind == JsonValueKind.Number ? fn.GetInt32() : 0));
        }

        int Actions(string id) => catalog.TryGetValue(id, out int n) ? n : 0;

        // Settings > Games / Start page.
        foreach ((string id, string name) in installs)
        {
            if (!integrations.Contains(id))
                Gate.Fail("I9", "games list names a game without an integration", id, name);
            if (Actions(id) == 0)
                Gate.Fail("I9", "games list names a game without catalog actions", id, name, Level(rows, id));
        }
        // The supported games page: games it says the engine supports.
        foreach ((string id, string title, string level, bool inEngine, int functions) in rows.Where(r => r.InEngine))
        {
            if (!integrations.Contains(id))
                Gate.Fail("I9", "supported games page lists a game without an integration", id, title, "level " + level);
            if (Actions(id) == 0)
                Gate.Fail("I9", "supported games page lists a game without catalog actions", id, title, "level " + level);
            else if (functions == 0)
                Gate.Fail("I9", "game has catalog actions but no function map (search by function and suggestions cannot reach it)", id, title, Actions(id) + " actions, level " + level);
        }
        foreach (string id in integrations.Where(i => !rows.Any(r => string.Equals(r.GameId, i, StringComparison.OrdinalIgnoreCase))))
            Gate.Fail("I9", "integration missing on the supported games page", id, string.Empty);
        foreach (string id in catalog.Keys.Where(k => !integrations.Contains(k)))
            Gate.Fail("I9", "catalog lists a game without an integration", id, catalog[id] + " actions");

        // Game labels (badges, chips, group headers): DisplayText.Game.
        foreach (string id in installs.Select(i => i.GameId).Concat(catalog.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string label = DisplayText.Game(id);
            if (string.Equals(label, id, StringComparison.Ordinal) && id.Length > 1 && Regex.IsMatch(id[1..], "[A-Z0-9]") && !KnownShortLabels.Contains(label))
                Gate.Fail("I9", "game label is the raw game id", id, label);
            else if (label.Replace(" ", string.Empty).Length <= 3 && !KnownShortLabels.Contains(label))
                Gate.Fail("I9", "game label is an abbreviation", id, "'" + label + "'", "name: " + (installs.FirstOrDefault(i => i.GameId == id).Name ?? id));
        }

        // Two games with one label cannot be told apart in a chip or a header.
        foreach (IGrouping<string, string> same in installs.Select(i => i.GameId).Concat(catalog.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                     .GroupBy(DisplayText.Game, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Gate.Fail("I9", "two games share one label", string.Join(", ", same), "'" + same.Key + "'");

        // The trailer's and teaser's game list (tools/trailer): each game shown must be one JoyPro can do something with.
        foreach ((string name, string file) in TrailerGames())
        {
            string? id = MatchGame(name, installs, rows);
            if (id == null)
                Gate.Fail("I9", "trailer names a game JoyPro does not list", string.Empty, name, file);
            else if (Actions(id) == 0)
                Gate.Fail("I9", "trailer names a game without catalog actions", id, name, file + "; level " + Level(rows, id));
        }
        Gate.Note("I9: " + installs.Count + " games listed, " + rows.Count(r => r.InEngine) + " engine games on the supported games page, " + integrations.Count + " integrations, " + catalog.Count + " catalogs");
        Gate.Finish("I9");
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string Level(List<(string GameId, string Title, string Level, bool InEngine, int Functions)> rows, string id) =>
        rows.FirstOrDefault(r => string.Equals(r.GameId, id, StringComparison.OrdinalIgnoreCase)).Level ?? "?";

    // Games of the trailer/teaser storyboards: ('Name', '+ Older · Other', [...]) lines.
    private static IEnumerable<(string Name, string File)> TrailerGames()
    {
        string dir = Path.Combine(Gate.RepoRoot(), "tools", "trailer");
        if (!Directory.Exists(dir))
            yield break;
        Regex line = new(@"^\s*\('(?<name>[^']+)',\s*(?:'(?<side>[^']*)'|None),\s*\[\(", RegexOptions.CultureInvariant);
        foreach (string file in Directory.GetFiles(dir, "storyboard*.py"))
        {
            foreach (string text in File.ReadLines(file))
            {
                Match m = line.Match(text);
                if (!m.Success)
                    continue;
                yield return (m.Groups["name"].Value, Path.GetFileName(file));
                foreach (string side in m.Groups["side"].Value.TrimStart('+', ' ').Split('·').Select(t => t.Trim().TrimStart('+').Trim()).Where(t => t.Length > 0))
                    yield return (side, Path.GetFileName(file));
            }
        }
    }

    private static HashSet<string> Words(string text) =>
        Regex.Split((text ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]+").Where(w => w.Length > 0).ToHashSet();

    private static string Norm(string text) => Regex.Replace((text ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]", string.Empty);

    private static string? MatchGame(string name, List<(string GameId, string Name)> installs, List<(string GameId, string Title, string Level, bool InEngine, int Functions)> rows)
    {
        string n = Norm(name);
        IEnumerable<(string Id, string Name)> candidates = installs.Select(i => (i.GameId, i.Name)).Concat(rows.Where(r => r.InEngine).Select(r => (r.GameId, r.Title)))
            .Concat(installs.Select(i => (i.GameId, DisplayText.Game(i.GameId))));
        List<(string Id, string Name)> all = candidates.ToList();
        foreach ((string id, string title) in all)
        {
            string t = Norm(title);
            if (t.Length > 0 && (t == n || n == Norm(id)))
                return id;
        }
        // Every word of the name in the title ("IL-2 Great Battles" in "IL-2 Sturmovik: Great Battles").
        HashSet<string> words = Words(name);
        foreach ((string id, string title) in all)
        {
            if (words.Count > 0 && words.IsSubsetOf(Words(title)))
                return id;
        }
        return null;
    }
}
