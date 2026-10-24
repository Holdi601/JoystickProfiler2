using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests.QualityGate;

// I1-I4 of the quality gate (QualityGate.cs): the catalog, the titles, the games list and the common searches.
[SupportedOSPlatform("windows")]
public sealed class QualityGateCatalogTests
{
    // Common searches and the words a game's own titles use for them (does the game have it at all?).
    internal static readonly (string Query, string[] Words)[] CommonSearches =
    [
        ("pitch", ["pitch", "elevator"]),
        ("roll", ["roll", "aileron", "bank"]),
        ("throttle", ["throttle", "thrust", "power lever"]),
        ("flaps", ["flap", "flaps"]),
        ("gear", ["gear", "undercarriage"]),
        ("engine rpm", ["rpm", "propeller", "prop pitch", "engine speed"]),
        ("fire", ["fire", "trigger", "weapon release", "shoot"]),
        ("view", ["view", "camera", "look"]),
        ("zoom", ["zoom"]),
    ];

    // DCS aircraft the searches also run with as an aircraft filter (the ones most profiles have).
    private static readonly string[] FilterPlanes = ["F-16C_50", "FA-18C_hornet", "A-10C II", "Ka-50"];

    internal static bool HasWord(string title, string[] words) =>
        words.Any(w => Regex.IsMatch(title ?? string.Empty, @"(?<![A-Za-z])" + Regex.Escape(w) + @"(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    // I1: the catalog shows > 0 actions for every game it lists and every DCS aircraft, as many as its overview says.
    [Fact]
    public void I1_CatalogShowsActionsForEveryGameAndAircraft()
    {
        if (!Gate.Enabled)
            return;
        CatalogWalk.Result walk = CatalogWalk.Get();
        if (walk.Overview.Count == 0)
            Gate.Fail("I1", "catalog lists no game", string.Empty, "Open catalog", walk.OverviewStatus);
        foreach ((string game, int actions, _) in walk.Overview.Where(o => o.Actions == 0))
            Gate.Fail("I1", "game in the catalog list with 0 actions", game, "overview");
        foreach (CatalogWalk.Entry entry in walk.Entries)
        {
            string subject = entry.Plane.Length > 0 ? entry.Plane : "catalog";
            if (entry.RowCount == 0)
                Gate.Fail("I1", "catalog shows 0 actions", entry.Game, subject, "overview says " + entry.OverviewCount + "; status: " + Gate.Short(entry.Status, 140));
            else if (entry.OverviewCount > 0 && entry.RowCount < entry.OverviewCount)
                Gate.Fail("I1", "catalog shows fewer actions than its overview", entry.Game, subject, entry.RowCount + " of " + entry.OverviewCount);
            if (entry.Plane.Length > 0 && !string.Equals(entry.ShownPlane, entry.Plane, StringComparison.OrdinalIgnoreCase))
                Gate.Fail("I1", "catalog opened another aircraft than picked", entry.Game, entry.Plane, "shows " + entry.ShownPlane);
            if (entry.RowCount > 0 && entry.CategoryCount == 0)
                Gate.Fail("I1", "catalog rows without any category group", entry.Game, subject);
        }
        Gate.Note("I1: " + walk.Overview.Count + " games in the catalog, " + walk.Entries.Count + " catalogs opened (" +
                  walk.Entries.Count(e => e.Plane.Length > 0) + " DCS aircraft), " + walk.Entries.Sum(e => e.RowCount) + " rows");
        Gate.Finish("I1");
    }

    // I2: no title a person sees in the catalog is empty, the id, a lua key, an encoded/hex id or a program identifier;
    // every action has a category, and categories are words.
    [Fact]
    public void I2_CatalogTitlesAreReadableAndCategorized()
    {
        if (!Gate.Enabled)
            return;
        CatalogWalk.Result walk = CatalogWalk.Get();
        // One finding per game, plane and id (DCS lists the same id in many aircraft).
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogWalk.Row row in walk.Entries.SelectMany(e => e.Rows))
        {
            string where = row.Plane.Length > 0 ? row.Plane + ": " : string.Empty;
            string? why = Gate.UnreadableTitle(row.Display, row.Id);
            if (why != null && seen.Add("t|" + row.Game + "|" + row.Plane + "|" + row.Id))
                Gate.Fail("I2", why, row.Game, where + "'" + Gate.Short(row.Display, 60) + "'", "id " + Gate.Short(row.Id, 60));
            string? category = Gate.UnreadableCategory(row.Category);
            if (category != null && seen.Add("c|" + row.Game + "|" + row.Plane + "|" + row.Id))
                Gate.Fail("I2", category, row.Game, where + "'" + Gate.Short(row.Display, 50) + "'", category == "no category" ? "id " + Gate.Short(row.Id, 50) : "'" + Gate.Short(row.Category, 50) + "'");
        }
        Gate.Finish("I2");
    }

    // I3: every game the Start page or Settings > Games lists has a catalog with actions, and its actions are found by
    // the search over every game (a title of its own catalog finds it).
    [Fact]
    public void I3_EveryListedGameHasCatalogActionsFoundBySearch()
    {
        if (!Gate.Enabled)
            return;
        CatalogWalk.Result walk = CatalogWalk.Get();
        HashSet<string> catalogGames = walk.Overview.Where(o => o.Actions > 0).Select(o => o.Game).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, string name, string kind) in walk.StartPageGames)
        {
            if (!catalogGames.Contains(id))
                Gate.Fail("I3", "Start page lists a game without catalog actions", id, name, "card kind " + kind + "; catalog has " + walk.ActionsOf(id) + " actions");
        }
        foreach ((string id, string name, bool installed) in walk.GamesList)
        {
            if (!catalogGames.Contains(id))
                Gate.Fail("I3", "games list (Settings > Games) has a game without catalog actions", id, name, installed ? "installed" : "not installed");
        }
        if (walk.StartPageGames.Count == 0)
            Gate.Note("I3: the Start page shows no game card on this PC (no game installed?)");

        // Findable: a title of each game's own catalog (preferably a flight control) searched over every game.
        List<string> games = walk.GamesList.Select(g => g.GameId).Concat(walk.StartPageGames.Select(g => g.GameId)).Concat(catalogGames)
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(catalogGames.Contains).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        GateSession.Run(Gate.CopyProfile("search-findable"), async s =>
        {
            MainWindowViewModel vm = s.Vm;
            if (s.HasProfile)
                await vm.LoadProfileCommand.ExecuteAsync(null);
            vm.CurrentStage = UiStage.Relations;
            vm.StartNewBuilderRelationCommand.Execute(null);
            await s.SettleAsync(() => true);
            foreach (string game in games)
            {
                List<CatalogWalk.Row> rows = walk.RowsOf(game).Where(r => Gate.UnreadableTitle(r.Display, r.Id) == null && r.Display.Length >= 4).ToList();
                CatalogWalk.Row? sample = rows.FirstOrDefault(r => HasWord(r.Display, ["pitch", "throttle", "gear", "flaps", "roll"])) ?? rows.FirstOrDefault();
                if (sample == null)
                {
                    Gate.Fail("I3", "no readable catalog title to search for", game, "catalog");
                    continue;
                }
                await SearchAsync(s, sample.Display, "findable " + game);
                bool found = vm.BuilderSearchResults.Any(r => string.Equals(r.Game, game, StringComparison.OrdinalIgnoreCase));
                if (!found)
                    Gate.Fail("I3", "a catalog title of the game does not find the game in the search", game, "'" + Gate.Short(sample.Display, 60) + "'",
                        vm.BuilderSearchResults.Count + " results in " + vm.BuilderSearchGames.Count + " games; status: " + Gate.Short(vm.BuilderSearchStatus, 100));
            }
        });
        Gate.Finish("I3");
    }

    internal static async Task SearchAsync(GateSession s, string query, string subject, string operation = "search")
    {
        MainWindowViewModel vm = s.Vm;
        vm.BuilderSearchText = query;
        await s.MeasureAsync(operation, subject + ": '" + query + "'", () => vm.SearchBuilderActionsAsync());
    }

    // I4: the common searches find readable rows in every game that has the function (its catalog has a title with the
    // word), every game chip shows rows when ticked, and a DCS aircraft filter keeps the DCS rows.
    [Fact]
    public void I4_CommonSearchesReturnReadableResultsPerGame()
    {
        if (!Gate.Enabled)
            return;
        CatalogWalk.Result walk = CatalogWalk.Get();
        List<string> games = walk.Overview.Where(o => o.Actions > 0).Select(o => o.Game).ToList();
        GateSession.Run(Gate.CopyProfile("search-common"), async s =>
        {
            MainWindowViewModel vm = s.Vm;
            if (s.HasProfile)
                await vm.LoadProfileCommand.ExecuteAsync(null);
            vm.CurrentStage = UiStage.Relations;
            vm.StartNewBuilderRelationCommand.Execute(null);
            await s.SettleAsync(() => true);
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string query, string[] words) in CommonSearches)
            {
                await SearchAsync(s, query, "every game");
                if (vm.BuilderSearchResults.Count == 0)
                    Gate.Fail("I4", "search over every game shows nothing", string.Empty, "'" + query + "'",
                        Gate.Short(vm.BuilderSearchStatus, 100) + "; diagnosis: " + await s.WhyEmptyAsync(["relations", "search", query, "--limit", "200", "--per-game", "12"]));
                CheckRows(vm.BuilderSearchResults, query, "every game", seen);
                HashSet<string> inResult = vm.BuilderSearchResults.Select(r => r.Game).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string game in games)
                {
                    int having = walk.RowsOf(game).Where(r => HasWord(r.Display, words)).Select(r => r.Id).Distinct().Count();
                    if (having > 0 && !inResult.Contains(game))
                    {
                        // Is it found with the game ticked (the GUI's ticked-chip search)? Then the search over every game hides it.
                        int ticked = await TickedSearchCountAsync(s, query, game);
                        Gate.Fail("I4", ticked > 0 ? "search over every game leaves out a game that has it (found only with the game ticked)" : "search finds nothing in a game that has it",
                            game, "'" + query + "'", "catalog has " + having + " titles with " + string.Join("/", words) + (ticked > 0 ? "; ticked search finds " + ticked : string.Empty));
                    }
                }

                // Every game chip of the result, ticked alone: rows of that game, readable.
                foreach (string chipGame in vm.BuilderSearchGames.Select(c => c.GameId).ToList())
                {
                    BuilderGameChip? chip = vm.BuilderSearchGames.FirstOrDefault(c => c.GameId == chipGame);
                    if (chip == null)
                        continue;
                    int expected = chip.Count;
                    await s.MeasureAsync("search game chip", chipGame + ": '" + query + "'", async () =>
                    {
                        chip.IsSelected = true;
                        await Task.Delay(100);
                        await s.SettleAsync(() => true);
                    });
                    List<BuilderSearchResultItem> rows = vm.BuilderSearchResults.ToList();
                    if (rows.Count == 0)
                        Gate.Fail("I1", "game chip ticked shows 0 actions", chipGame, "search '" + query + "'", "chip said " + expected + "; status: " + Gate.Short(vm.BuilderSearchStatus, 100));
                    else if (rows.Any(r => !string.Equals(r.Game, chipGame, StringComparison.OrdinalIgnoreCase)))
                        Gate.Fail("I1", "game chip ticked shows rows of other games", chipGame, "search '" + query + "'", string.Join(", ", rows.Select(r => r.Game).Distinct().Take(5)));
                    CheckRows(rows, query, "chip " + chipGame, seen);
                    chip.IsSelected = false;
                    await Task.Delay(100);
                    await s.SettleAsync(() => true);
                }

                // With DCS aircraft as a filter: DCS rows stay where the aircraft has the function.
                foreach (string plane in FilterPlanes)
                {
                    int having = walk.Entries.Where(e => e.Game == "DCS" && string.Equals(e.Plane, plane, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(e => e.Rows).Count(r => HasWord(r.Display, words));
                    if (having == 0)
                        continue;
                    await vm.AddBuilderSearchPlaneCommand.ExecuteAsync(plane);
                    await s.SettleAsync(() => true);
                    await SearchAsync(s, query, "aircraft filter " + plane);
                    List<BuilderSearchResultItem> rows = vm.BuilderSearchResults.Where(r => r.Game == "DCS").ToList();
                    if (rows.Count == 0)
                        Gate.Fail("I1", "aircraft filter shows 0 DCS actions", "DCS", plane + ": '" + query + "'", "the aircraft's catalog has " + having + " titles with the word");
                    else if (rows.Any(r => !r.Planes.Contains(plane, StringComparer.OrdinalIgnoreCase)))
                        Gate.Fail("I1", "aircraft filter shows rows of other aircraft", "DCS", plane + ": '" + query + "'",
                            Gate.Short(string.Join("; ", rows.Where(r => !r.Planes.Contains(plane, StringComparer.OrdinalIgnoreCase)).Take(3).Select(r => r.DisplayTitle + " (" + string.Join(",", r.Planes.Take(3)) + ")")), 160));
                    CheckRows(rows, query, "aircraft filter " + plane, seen);
                    await vm.RemoveBuilderSearchPlaneCommand.ExecuteAsync(plane);
                    await s.SettleAsync(() => true);
                }
            }
        });
        Gate.Finish("I4");
    }

    private static void CheckRows(IEnumerable<BuilderSearchResultItem> rows, string query, string where, HashSet<string> seen)
    {
        foreach (BuilderSearchResultItem row in rows)
        {
            string? why = Gate.UnreadableTitle(row.DisplayTitle, row.Id);
            if (why != null && seen.Add(row.Game + "|" + row.Id + "|" + row.DisplayTitle))
                Gate.Fail("I4", "search result " + why, row.Game, "'" + query + "' (" + where + "): '" + Gate.Short(row.DisplayTitle, 60) + "'", "id " + Gate.Short(row.Id, 60));
        }
    }

    // The search the GUI runs when this game's chip is ticked (engine relations search --game, the same options).
    private static async Task<int> TickedSearchCountAsync(GateSession s, string query, string game)
    {
        List<string> args = ["relations", "search", query, "--game", game, "--limit", "400"];
        if (s.HasProfile)
            args.InsertRange(0, ["--profile", s.Profile]);
        JoyPro.Gui.Models.EngineCommandResult result = await s.Engine.RunAsync(Gate.EnginePath, args);
        if (result.ExitCode != 0)
            return 0;
        try
        {
            string text = result.StandardOutput;
            int start = text.IndexOf('{');
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(start >= 0 ? text[start..] : text);
            return doc.RootElement.TryGetProperty("Results", out System.Text.Json.JsonElement list) ? list.GetArrayLength() : 0;
        }
        catch (System.Text.Json.JsonException)
        {
            return 0;
        }
    }
}
