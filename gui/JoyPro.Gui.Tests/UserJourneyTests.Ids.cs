using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using JoyPro.Engine;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;

namespace JoyPro.Gui.Tests;

// Scenario "ids": no game's raw id on screen (the user's rule: never in search, catalog, binds, layout or export
// results). A user's profile (a copy: JOYPRO_JOURNEY_PROFILE) with a PC's GameData (a copy: JOYPRO_JOURNEY_GAMEDATA): an
// axis relation open, the catalog of every game (every row's texts checked, the first categories rendered offscreen and
// every TextBlock scanned), then the search, also by an id: searching or filtering by a game's id still finds the row.
public sealed partial class UserJourneyTests
{
    private sealed partial class Journey
    {
        // MSFS events, X4 controls, Star Citizen "map|action" and v_ ids, snake_case and SCREAMING_SNAKE ids, long hex.
        private static readonly Regex GameId = new(
            @"\b(KEY|INPUT_RANGE|INPUT_STATE|INPUT_ACTION)_[A-Z0-9_]+|\b[a-z][A-Za-z0-9]*(_[A-Za-z0-9]+)*\|[A-Za-z0-9_]+|\bv_[a-z0-9_]+|\b[a-z][a-z0-9]*_[a-z0-9_]+\b|\b[A-Z][A-Z0-9]*_[A-Z0-9_]+\b|\b[0-9a-f]{16,}\b",
            RegexOptions.Compiled);

        internal static bool LooksLikeGameId(string text) =>
            GameId.IsMatch(text ?? string.Empty) ||
            (text ?? string.Empty).Split([' ', '\t', '\n', '(', ')', ',', '|', ':', '\'', '"'], StringSplitOptions.RemoveEmptyEntries).Any(DcsCommandNames.LooksLikeCommandId);

        // Every visible TextBlock of the window (offscreen, 1280x800): none may show an id. The user's own relation names
        // ("in 'SPACE_SPEED_LIMITER'" after a Star Citizen import) are his data, not an id JoyPro shows.
        private void CheckIdsOnScreen(string slug)
        {
            MainWindow main = new() { DataContext = vm, Width = 1280, Height = 800 };
            try
            {
                RenderControl(main, 1280, 800, Path.Combine(folder, slug + "-screen-1280.png"));
                List<TextBlock> texts = main.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToList();
                // The user's own relation names (imported as "HCU_X", "ai_menu_down") are his data, not an id JoyPro shows.
                HashSet<string> relationNames = vm!.AllRelationsForTests().Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
                List<string> raw = texts.Select(t => t.Text ?? string.Empty).Where(t => !relationNames.Contains(t) && !relationNames.Any(n => t.Contains("'" + n + "'", StringComparison.Ordinal))).Where(LooksLikeGameId).Distinct().ToList();
                Note(slug + ": " + texts.Count + " texts on screen, ids: " + raw.Count + (raw.Count > 0 ? " e.g. " + string.Join(" | ", raw.Take(6)) : string.Empty));
                if (raw.Count > 0)
                    Failures.Add(slug + " shows ids: " + string.Join(" | ", raw.Take(6)));
            }
            finally
            {
                main.DataContext = null;
            }
        }

        private void RunIds()
        {
            string? source = Environment.GetEnvironmentVariable("JOYPRO_JOURNEY_PROFILE");
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                Failures.Add("set JOYPRO_JOURNEY_PROFILE to a copy of a profile");
                return;
            }
            string profile = Path.Combine(folder, "profiles", "ids.pr0file");
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            File.Copy(source, profile, overwrite: true);
            if (Environment.GetEnvironmentVariable("JOYPRO_JOURNEY_GAMEDATA") is { Length: > 0 } gameData && Directory.Exists(gameData))
            {
                foreach (string file in Directory.GetFiles(gameData, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(data, "GameData", Path.GetRelativePath(gameData, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                }
            }
            renderSteps = false;
            vm = CreateVm();
            Step("open the profile", async () =>
            {
                vm!.LastProfilePath = profile;
                await vm.RunStartupChecksAsync();
                vm.SkipGameSetup();
                await vm.AutoOpenLastProfileOnStartupAsync();
                vm.CurrentStage = UiStage.Relations;
            });
            if (vm!.ProfileRelationCount == 0)
            {
                Failures.Add("the profile did not open");
                return;
            }
            RelationListItem open = vm.AllRelationsForTests().FirstOrDefault(r => r.Name == "Pitch") ?? vm.AllRelationsForTests().First();
            Step("open the relation " + open.Name, async () =>
            {
                vm!.SelectedRelation = vm.Relations.FirstOrDefault(r => r.Name == open.Name) ?? open;
                await WaitForAsync(() => vm.RelationNodes.Count > 0 && !vm.IsBuilderSuggestLoading, 60000);
                await vm.OpenCatalogCommand.ExecuteAsync(null);
                await WaitForAsync(() => !vm.IsCatalogLoading, 60000);
            }, extraRender: CheckIdsOnScreen);

            // The catalog of every game: every row (title, caption, used, bound) and category; the first categories on screen.
            foreach (CatalogGameOption game in vm.CatalogGames.ToList())
            {
                Step("ids: catalog of " + game.Game, async () =>
                {
                    string plane = game.HasPlanes ? game.Planes.FirstOrDefault(p => p.InProfile)?.Plane ?? game.Planes[0].Plane : string.Empty;
                    await vm!.SelectCatalogAsync(game.Game, plane);
                    await WaitForAsync(() => !vm.IsCatalogLoading, 60000);
                    foreach (CatalogCategoryItem category in vm.CatalogCategories.Take(4))
                    {
                        if (!category.IsExpanded)
                            vm.ToggleCatalogCategoryCommand.Execute(category);
                    }
                    List<string> texts = vm.CatalogCategories.Select(c => c.Name)
                        .Concat(vm.CatalogRowItems.SelectMany(r => new[] { r.Row.DisplayTitle, r.Row.PlanesText, r.Row.IdHint }))
                        .Where(t => !string.IsNullOrEmpty(t)).ToList();
                    List<string> raw = texts.Where(LooksLikeGameId).Distinct().ToList();
                    Note(game.Game + (plane.Length > 0 ? " " + plane : string.Empty) + ": " + vm.CatalogRowItems.Count + " rows, ids: " + raw.Count +
                         (raw.Count > 0 ? " e.g. " + string.Join(" | ", raw.Take(5)) : string.Empty) + "; categories: " + string.Join(", ", vm.CatalogCategories.Take(6).Select(c => c.Name)));
                    if (raw.Count > 0)
                        Failures.Add("catalog " + game.Game + " shows ids: " + string.Join(" | ", raw.Take(5)));
                }, extraRender: CheckIdsOnScreen);
            }

            // Filtering the catalog by an id still finds the row.
            Step("ids: catalog filtered by an MSFS event id", async () =>
            {
                await vm!.SelectCatalogAsync("MSFS2024", string.Empty);
                await WaitForAsync(() => !vm.IsCatalogLoading, 60000);
                vm.CatalogFilterText = "KEY_AXIS_ELEVATOR_SET";
                List<string> rows = vm.CatalogCategories.Where(c => c.IsVisible).SelectMany(c => c.VisibleRows).Select(r => r.Row.DisplayTitle).ToList();
                Note("filter 'KEY_AXIS_ELEVATOR_SET': " + string.Join(" | ", rows));
                if (!rows.Contains("Elevator Axis"))
                    Failures.Add("the catalog filter by the id KEY_AXIS_ELEVATOR_SET did not find 'Elevator Axis'");
                vm.CatalogFilterText = string.Empty;
            }, extraRender: CheckIdsOnScreen);

            // The search over every game, also by ids: the rows come, their ids do not show.
            foreach ((string query, string game, string title) in new[]
                     {
                         ("pitch", string.Empty, string.Empty), ("throttle", string.Empty, string.Empty), ("gear", string.Empty, string.Empty),
                         ("KEY_AXIS_ELEVATOR_SET", "MSFS2024", "Elevator Axis"), ("v_pitch", "StarCitizen", "Pitch"), ("INPUT_RANGE_STEERING_PRIMARY", "X4", "Yaw (Primary Steering Axis)")
                     })
            {
                Step("ids: search '" + query + "'", async () =>
                {
                    vm!.ShowBuilderFindCommand.Execute(null);
                    vm.BuilderSearchPlanes.Clear();
                    vm.BuilderSearchText = query;
                    await vm.SearchBuilderActionsAsync();
                    List<BuilderSearchResultItem> rows = vm.BuilderSearchResults.ToList();
                    List<string> raw = rows.SelectMany(r => new[] { r.DisplayTitle, r.PlanesText, r.IdText, r.IdHint }).Where(t => !string.IsNullOrEmpty(t)).Where(LooksLikeGameId).Distinct().ToList();
                    Note("'" + query + "': " + vm.BuilderSearchStatus + "; ids: " + raw.Count + (raw.Count > 0 ? " e.g. " + string.Join(" | ", raw.Take(5)) : string.Empty) +
                         "; first rows: " + string.Join(" | ", rows.Take(6).Select(r => r.Game + " " + r.DisplayTitle)));
                    if (raw.Count > 0)
                        Failures.Add("search '" + query + "' shows ids: " + string.Join(" | ", raw.Take(5)));
                    if (game.Length > 0 && !rows.Any(r => r.Game == game && r.DisplayTitle == title))
                        Failures.Add("search by the id '" + query + "' did not find " + game + " '" + title + "'");
                }, extraRender: CheckIdsOnScreen);
            }
        }
    }
}
