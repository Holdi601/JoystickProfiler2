using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Tests;

// Scenario "il2-names" (opt-in, JOYPRO_JOURNEY_SCENARIOS=il2-names): the report of 2026-10-25 ("rb pitch", "Mc pitch",
// "Rpc pitch" nobody can read, no explanation). A new profile; the catalog of IL-2 Great Battles and IL-2 Korea, the
// categories (the games' Key Mapping sections) opened, every row's title checked for unexplained abbreviations and an
// explanation; the mouse turret aim, Mouse control and a plain pitch axis of both games put into one relation, and the
// relation editor shown with them. Rendered at 2267x1250 and 1280x800.
public sealed partial class UserJourneyTests
{
    private sealed partial class Journey
    {
        private void RunIl2Names()
        {
            vm = CreateVm();
            Step("start", async () =>
            {
                await vm!.RunStartupChecksAsync();
                vm.SkipGameSetup();
            });
            Step("new profile", async () => await vm!.NewProfileAutoCommand.ExecuteAsync(null));
            Step("relations: new relation 'IL-2 mouse aim'", () =>
            {
                vm!.CurrentStage = UiStage.Relations;
                vm.StartNewRelationCommand.Execute(null);
                vm.RelationNameInput = "IL-2 mouse aim";
                vm.SetBuilderKindCommand.Execute("axis");
                return Task.CompletedTask;
            });
            Step("relations: Open catalog", async () =>
            {
                await vm!.OpenCatalogCommand.ExecuteAsync(null);
                await WaitForAsync(() => !vm.IsCatalogLoading, 60000);
            });
            foreach ((string game, string[] open, string[] tick) in new[]
            {
                ("IL2Game", new[] { "Tank controls", "Plane controls" }, new[] { "rb_pitch", "mc_pitch", "rpc_pitch" }),
                ("IL2Korea", new[] { "Hidden commands", "Flight Controls" }, new[] { "rb_pitch", "mc_pitch", "rpc_pitch" }),
            })
            {
                Step("catalog: " + game + ", its menu sections", async () =>
                {
                    await vm!.SelectCatalogAsync(game, string.Empty);
                    await WaitForAsync(() => !vm.IsCatalogLoading, 60000);
                    foreach (string name in open)
                    {
                        CatalogCategoryItem? category = vm.CatalogCategories.FirstOrDefault(c => c.Name == name);
                        if (category == null)
                            Failures.Add(game + ": no catalog category '" + name + "' (" + string.Join(", ", vm.CatalogCategories.Select(c => c.Name)) + ")");
                        else if (!category.IsExpanded)
                            vm.ToggleCatalogCategoryCommand.Execute(category);
                    }
                    CheckIl2Rows(game);
                    foreach (string id in tick)
                    {
                        CatalogRowItem? row = vm.CatalogRowItems.FirstOrDefault(r => r.Row.Id == id);
                        if (row != null && row.Row.CanCheck)
                            row.Row.IsChecked = true;
                    }
                    Note(game + " categories: " + string.Join(", ", vm.CatalogCategories.Select(c => c.Name + " (" + c.CountText + ")")));
                }, extraRender: slug => RenderTall(slug, 2600));
                Step("catalog: " + game + ", add the ticked actions", async () =>
                {
                    await vm!.AddCheckedCatalogCommand.ExecuteAsync(null);
                    await WaitForAsync(() => !vm.IsBusy, 30000);
                    Note("relation " + vm.SelectedRelation?.Name + ": " + string.Join(" | ", vm.RelationNodes.Select(n => n.Game + " " + n.TitlesDisplay)));
                });
            }
            Step("relation editor: the IL-2 actions with their names and explanations", async () =>
            {
                vm!.CloseRelationToolsCommand.Execute(null);
                await WaitForAsync(() => vm.RelationNodes.Count > 0 && !vm.IsBuilderSuggestLoading, 60000);
                foreach (RelationNodeItem node in vm.RelationNodes)
                {
                    // The card's title: the first line of the node's text, without the id the tooltip adds in brackets.
                    string title = node.TitlesDisplay.Split('\n')[0].Replace(" (" + node.Id + ")", string.Empty).Trim();
                    string? why = JoyPro.Engine.ActionTexts.UnexplainedAbbreviation(node.Game, node.Id, title);
                    if (why != null || title.StartsWith("inp", StringComparison.Ordinal) || title.Contains('_'))
                        Failures.Add("relation editor: " + node.Game + " " + node.Id + " shows '" + title + "'");
                }
                Note("relation nodes: " + string.Join(" | ", vm.RelationNodes.Select(n => n.Game + ": " + n.TitlesDisplay)));
            }, extraRender: slug => RenderTall(slug, 2000));
            Step("relations: search 'mouse control'", async () =>
            {
                vm!.StartNewRelationCommand.Execute(null);
                vm.BuilderSearchText = "mouse control";
                await vm.SearchBuilderActionsCommand.ExecuteAsync(null);
                await WaitForAsync(() => !vm.IsBuilderSearching, 30000);
                List<BuilderSearchResultItem> il2 = vm.BuilderSearchResults.Where(r => r.Game.StartsWith("IL2", StringComparison.Ordinal) && !r.Game.Contains("CoD")).ToList();
                Note("IL-2 rows: " + string.Join(" | ", il2.Take(12).Select(r => r.Game + " " + r.DisplayTitle + " — " + r.Explanation)));
            });
        }

        // Every IL-2 row of the catalog: a title nobody needs the id for, and an explanation.
        private void CheckIl2Rows(string game)
        {
            int rows = 0, explained = 0;
            foreach (CatalogRowItem item in vm!.CatalogRowItems)
            {
                BuilderSearchResultItem row = item.Row;
                rows++;
                if (row.Explanation.Length > 0)
                    explained++;
                string? word = JoyPro.Engine.ActionTexts.UnexplainedAbbreviation(row.Game, row.Id, row.DisplayTitle);
                if (word != null || row.DisplayTitle.Contains('_'))
                    Failures.Add(game + " catalog: '" + row.DisplayTitle + "' (" + row.Id + ")");
                if (string.IsNullOrWhiteSpace(row.Category))
                    Failures.Add(game + " catalog: no category for " + row.Id);
            }
            Note(game + " catalog: " + rows + " rows, " + explained + " explained");
            if (rows > 0 && explained < rows)
                Failures.Add(game + " catalog: only " + explained + " of " + rows + " rows explained");
        }
    }
}
