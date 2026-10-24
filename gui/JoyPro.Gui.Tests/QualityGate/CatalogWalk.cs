using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Tests.QualityGate;

// What the catalog ("Open catalog" on the Relations page) shows for every game and every DCS aircraft, read once per run
// through the view model (with the profile open when the gate has one, as the user has it). I1, I2, I3 and I4 read it.
internal static class CatalogWalk
{
    public sealed record Row(string Game, string Plane, string Id, string Title, string Display, string Category, bool IsAxis);

    public sealed record Entry(string Game, string Plane, int OverviewCount, int RowCount, int CategoryCount, string Status, string ShownPlane, List<Row> Rows);

    public sealed record Result(List<(string Game, int Actions, List<(string Plane, int Actions)> Planes)> Overview, List<Entry> Entries,
        List<(string GameId, string Name, bool Installed)> GamesList, List<(string GameId, string Name, string Kind)> StartPageGames,
        List<(string GameId, string Command, bool Catalog)> Integrations, string OverviewStatus);

    private static Result? result;
    private static readonly object Sync = new();

    // Runs the walk the first time (never from inside a session: it opens its own).
    public static Result Get()
    {
        lock (Sync)
        {
            if (result != null)
                return result;
            result = Walk();
            Gate.WriteReport();
            return result;
        }
    }

    private static Result Walk()
    {
        List<(string, int, List<(string, int)>)> overview = [];
        List<Entry> entries = [];
        List<(string, string, bool)> gamesList = [];
        List<(string, string, string)> startPage = [];
        List<(string, string, bool)> integrations = [];
        string overviewStatus = string.Empty;
        int planeLimit = Gate.Limit("JOYPRO_QUALITY_GATE_PLANES");
        GateSession.Run(Gate.CopyProfile("catalog"), async s =>
        {
            MainWindowViewModel vm = s.Vm;
            if (s.HasProfile)
                await s.MeasureAsync("profile load", "catalog session", () => vm.LoadProfileCommand.ExecuteAsync(null));

            // The Start page's games and Settings > Games (installs and the engine's integrations).
            await s.MeasureAsync("games list", "games installs + integrations", async () =>
            {
                await vm.RefreshGameInstallsCommand.ExecuteAsync(null);
                await vm.RefreshRegistryGamesCommand.ExecuteAsync(null);
            });
            foreach (GameInstallItem game in vm.GameInstalls.Where(g => !g.Disabled))
                gamesList.Add((game.GameId, game.Name, game.Installed));
            foreach (GameCardItem card in vm.GameCards)
                startPage.Add((card.GameId, card.Name, card.Kind));
            integrations.Add(("DCS", "dcs", true));
            integrations.Add(("IL2Game", "il2", true));
            integrations.Add(("StarCitizen", "starcitizen", true));
            foreach (RegistryGameItem game in AllRegistryGames(vm))
                integrations.Add((game.GameId, game.CommandName, game.SupportsCatalogRefresh));

            vm.CurrentStage = UiStage.Relations;
            await s.SettleAsync(() => true);
            await s.MeasureAsync("catalog overview", "all games", () => vm.OpenCatalogCommand.ExecuteAsync(null));
            overviewStatus = vm.CatalogStatus;
            foreach (CatalogGameOption game in vm.CatalogGames.ToList())
                overview.Add((game.Game, game.Actions, game.Planes.Select(p => (p.Plane, p.Actions)).ToList()));

            foreach (CatalogGameOption game in vm.CatalogGames.ToList())
            {
                if (game.HasPlanes)
                {
                    foreach (CatalogPlaneOption plane in game.Planes.OrderBy(p => p.Plane, StringComparer.OrdinalIgnoreCase).Take(planeLimit).ToList())
                        entries.Add(await OpenAsync(s, game.Game, plane.Plane, plane.Actions));
                }
                else
                {
                    entries.Add(await OpenAsync(s, game.Game, string.Empty, game.Actions));
                }
            }
        });
        return new Result(overview, entries, gamesList, startPage, integrations, overviewStatus);
    }

    private static async Task<Entry> OpenAsync(GateSession s, string game, string plane, int overviewCount)
    {
        MainWindowViewModel vm = s.Vm;
        await s.MeasureAsync("catalog open", game + (plane.Length > 0 ? " " + plane : string.Empty), () => vm.SelectCatalogAsync(game, plane),
            "overview says " + overviewCount + " actions");
        List<Row> rows = vm.CatalogRowItems.Select(r => new Row(r.Row.Game, plane, r.Row.Id, r.Row.Title, r.Row.DisplayTitle, r.Row.Category, r.Row.IsAxis)).ToList();
        string status = vm.CatalogStatus;
        if (rows.Count == 0 && overviewCount > 0)
            status += " | diagnosis: " + await DiagnoseEmptyAsync(s, game, plane);
        return new Entry(game, plane, overviewCount, rows.Count, vm.CatalogCategories.Count, status, vm.CatalogPlane?.Plane ?? string.Empty, rows);
    }

    private static Task<string> DiagnoseEmptyAsync(GateSession s, string game, string plane)
    {
        List<string> args = ["relations", "browse", "--game", game];
        if (plane.Length > 0)
            args.AddRange(["--plane", plane]);
        return s.WhyEmptyAsync(args);
    }

    // Every integration of the engine (the view model's own list; RegistryGames leaves out hidden ones).
    private static IEnumerable<RegistryGameItem> AllRegistryGames(MainWindowViewModel vm)
    {
        System.Reflection.FieldInfo? field = typeof(MainWindowViewModel).GetField("allRegistryGames", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(vm) as IEnumerable<RegistryGameItem> ?? vm.RegistryGames;
    }

    // The rows of one game (all aircraft for DCS).
    public static IEnumerable<Row> RowsOf(this Result r, string game) =>
        r.Entries.Where(e => string.Equals(e.Game, game, StringComparison.OrdinalIgnoreCase)).SelectMany(e => e.Rows);

    public static int ActionsOf(this Result r, string game) =>
        r.Overview.Where(o => string.Equals(o.Game, game, StringComparison.OrdinalIgnoreCase)).Select(o => o.Actions).FirstOrDefault();
}
