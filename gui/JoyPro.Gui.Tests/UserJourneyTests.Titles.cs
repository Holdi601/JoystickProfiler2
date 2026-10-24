using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using JoyPro.Engine;
using Avalonia.Controls;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;

namespace JoyPro.Gui.Tests;

// Scenario "titles": a user's profile (a copy: JOYPRO_JOURNEY_PROFILE) with the GameData of a PC (a copy:
// JOYPRO_JOURNEY_GAMEDATA, e.g. a DCS list built before the refresh translated localization keys). No raw DCS id or lua
// key may show in the relations' actions, the catalog, the search, the Binds page's peek, the layout titles (cheat
// sheets, tables) and the describe-group proposals; "engine rpm", "rpm" and "prop pitch" find the Spitfire's actions.
public sealed partial class UserJourneyTests
{
    private sealed partial class Journey
    {
        private static readonly Regex RawText = new(@"Input\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_]|[A-Za-z0-9_\-]+:(Inp|Cat):", RegexOptions.Compiled);

        // A localization key or a DCS command id ("dnilp3007unilcd2vdnilvp-1vunil", "a3008cd2") anywhere in the text.
        private static bool LooksRaw(string text) =>
            RawText.IsMatch(text ?? string.Empty) ||
            (text ?? string.Empty).Split([' ', '\t', '\n', '(', ')', ',', '|', ':', '\'', '"'], StringSplitOptions.RemoveEmptyEntries).Any(DcsCommandNames.LooksLikeCommandId);

        // Every text the window shows (offscreen, both sizes): none may be a key or an id.
        private void CheckScreen(string slug)
        {
            foreach ((int width, int height) in Sizes)
            {
                MainWindow main = new() { DataContext = vm, Width = width, Height = height };
                try
                {
                    RenderControl(main, width, height, Path.Combine(folder, slug + "-screen-" + width + ".png"));
                    List<string> raw = main.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).Where(LooksRaw).Distinct().ToList();
                    Note(slug + " at " + width + ": " + main.GetVisualDescendants().OfType<TextBlock>().Count() + " texts on screen, raw: " + raw.Count +
                         (raw.Count > 0 ? " e.g. " + string.Join(" | ", raw.Take(6)) : string.Empty));
                    if (raw.Count > 0)
                        Failures.Add(slug + " at " + width + " shows raw ids or keys: " + string.Join(" | ", raw.Take(6)));
                }
                finally
                {
                    main.DataContext = null;
                    vm!.ConfirmHandler = Confirm;
                }
            }
        }

        private void RunTitles()
        {
            string? source = Environment.GetEnvironmentVariable("JOYPRO_JOURNEY_PROFILE");
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                Failures.Add("set JOYPRO_JOURNEY_PROFILE to a copy of a profile with DCS relations");
                return;
            }
            string profile = Path.Combine(folder, "profiles", "titles.pr0file");
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
            vm = CreateVm();
            Step("open the profile", async () =>
            {
                vm!.LastProfilePath = profile;
                await vm.RunStartupChecksAsync();
                vm.SkipGameSetup();
                await vm.AutoOpenLastProfileOnStartupAsync();
                vm.CurrentStage = UiStage.Relations;
            });
            Note("profile " + vm!.ProfilePath + ": " + vm.ProfileRelationCount + " relations; DCS folder: " + vm.DcsInstallPath);
            if (vm.ProfileRelationCount == 0)
            {
                Failures.Add("the profile did not open");
                return;
            }

            // Every relation: the title of each action in each active aircraft, through the GUI's resolver (one
            // "relations list" for the nodes; loading 500 relations one by one takes the engine ~5 s each), then the cards
            // of the relations with commands that DCS names by a localization key, loaded as the user opens them.
            string spitfireRelation = string.Empty;
            List<string> keyRelations = [];
            string[] keyPlanes = ["SpitfireLFMkIX", "Bf-109K-4", "FW-190A8", "FW-190D9", "I-16", "MosquitoFBMkVI", "P-47D-30", "Yak-52", "CH-47F", "MiG-29-Fulcrum"];
            Step("titles: the actions of every relation", async () =>
            {
                EngineCommandResult? list = await vm!.RunEngineForTestsAsync(["relations", "list"], text: false);
                List<string> raw = [];
                int titles = 0, notInList = 0, relations = 0;
                using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(list?.StandardOutput is { Length: > 0 } o ? o[o.IndexOf('{')..] : "{}"))
                {
                    if (doc.RootElement.TryGetProperty("Relations", out System.Text.Json.JsonElement all))
                    {
                        foreach (System.Text.Json.JsonElement relation in all.EnumerateArray())
                        {
                            relations++;
                            string name = relation.GetProperty("Name").GetString() ?? string.Empty;
                            foreach (System.Text.Json.JsonElement node in relation.GetProperty("Nodes").EnumerateArray())
                            {
                                string game = node.GetProperty("Game").GetString() ?? string.Empty;
                                string id = node.GetProperty("Id").GetString() ?? string.Empty;
                                if (game != "DCS")
                                    continue;
                                foreach (System.Text.Json.JsonProperty plane in node.GetProperty("Aircraft").EnumerateObject().Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.True))
                                {
                                    string title = vm.ResolveNodeTitleForTests(game, plane.Name, id);
                                    titles++;
                                    if (title.StartsWith("Command not in this aircraft's current list", StringComparison.Ordinal))
                                        notInList++;
                                    if (LooksRaw(title) || string.Equals(title, id, StringComparison.OrdinalIgnoreCase))
                                        raw.Add(name + " / " + plane.Name + ": " + title);
                                    if (keyPlanes.Contains(plane.Name, StringComparer.OrdinalIgnoreCase) && !keyRelations.Contains(name))
                                        keyRelations.Add(name);
                                    if (spitfireRelation.Length == 0 && plane.Name == "SpitfireLFMkIX")
                                        spitfireRelation = name;
                                }
                            }
                        }
                    }
                }
                Note(titles + " DCS action titles (action x aircraft) in " + relations + " relations; " + notInList +
                     " say the command is not in the aircraft's current list; raw: " + raw.Count + (raw.Count > 0 ? " e.g. " + string.Join(" | ", raw.Take(8)) : string.Empty) +
                     "; relations with key-named aircraft: " + keyRelations.Count);
                if (titles == 0)
                    Failures.Add("no DCS action titles checked");
                if (raw.Count > 0)
                    Failures.Add(raw.Count + " raw action titles in the relations, e.g. " + string.Join(" | ", raw.Take(5)));
            });
            Step("titles: the cards of the relations with key-named aircraft", async () =>
            {
                List<string> raw = [];
                List<string> shown = [];
                foreach (string name in keyRelations.Take(25))
                {
                    await vm!.ShowRelationAsync(name);
                    foreach (RelationNodeItem node in vm.RelationNodes)
                    {
                        foreach (string title in node.TitlesDisplay.Split('\n').Select(t => t.Trim()).Where(t => t.Length > 0))
                        {
                            if (LooksRaw(title) || string.Equals(title, node.Id, StringComparison.OrdinalIgnoreCase))
                                raw.Add(name + ": " + title);
                        }
                        if (shown.Count < 30 && node.Game == "DCS")
                            shown.Add(name + " = " + node.TitlesSummary);
                    }
                }
                Note("cards: " + string.Join(" | ", shown));
                if (raw.Count > 0)
                    Failures.Add(raw.Count + " raw titles on the cards, e.g. " + string.Join(" | ", raw.Take(5)));
            });
            if (spitfireRelation.Length > 0)
            {
                Step("titles: open the Spitfire relation " + spitfireRelation, async () =>
                {
                    vm!.SelectedRelation = vm.AllRelationsForTests().First(r => r.Name == spitfireRelation);
                    await WaitForAsync(() => vm.RelationNodes.Count > 0 && !vm.IsBuilderSuggestLoading, 30000);
                    Note("cards: " + string.Join(" | ", vm.RelationNodes.Select(n => n.GameLabel + ": " + n.TitlesSummary + (n.MeaningTitle.Length > 0 ? " [" + n.MeaningTitle + "]" : string.Empty))));
                    List<string> raw = vm.RelationNodes.SelectMany(n => new[] { n.TitlesSummary, n.MeaningTitle }).Where(LooksRaw).ToList();
                    if (raw.Count > 0)
                        Failures.Add("raw titles on the Spitfire relation's cards: " + string.Join(" | ", raw.Take(5)));
                });
            }

            // The catalog of the Spitfire: every category and title readable; sorted by title.
            Step("titles: catalog of the Spitfire", async () =>
            {
                await vm!.OpenCatalogCommand.ExecuteAsync(null);
                await vm.SelectCatalogAsync("DCS", "SpitfireLFMkIX");
                vm.CatalogFilterText = "rpm";
                List<string> raw = vm.CatalogCategories.Select(c => c.Name).Concat(vm.CatalogRowItems.Select(r => r.Row.DisplayTitle)).Concat(vm.CatalogRowItems.Select(r => r.Row.IdHint)).Where(LooksRaw).ToList();
                Note(vm.CatalogStatus + "; 'rpm' rows: " + string.Join(" | ", vm.CatalogCategories.SelectMany(c => c.VisibleRows).Select(r => r.Row.DisplayTitle)));
                if (raw.Count > 0)
                    Failures.Add(raw.Count + " raw titles in the Spitfire catalog, e.g. " + string.Join(" | ", raw.Take(5)));
                if (!vm.CatalogRowItems.Any(r => r.Row.DisplayTitle.Contains("Engine RPM", StringComparison.OrdinalIgnoreCase)))
                    Failures.Add("the Spitfire catalog has no 'Engine RPM' action");
            }, extraRender: CheckScreen);
            Step("titles: catalog filtered by a key (pitch_minus)", () =>
            {
                vm!.CatalogFilterText = "pitch_minus";
                List<string> rows = vm.CatalogCategories.Where(c => c.IsVisible).SelectMany(c => c.VisibleRows).Select(r => r.Row.DisplayTitle).ToList();
                Note("'pitch_minus' rows: " + string.Join(" | ", rows));
                if (!rows.Contains("Engine RPM DECREASE"))
                    Failures.Add("the catalog filter did not find 'pitch_minus'");
                return Task.CompletedTask;
            }, extraRender: CheckScreen);
            Step("titles: catalog sorted by category, then title", () =>
            {
                vm!.CatalogSort.Reset();
                vm.CatalogSort.Add("category", false);
                vm.CatalogSort.Add("name", false);
                Note("categories: " + string.Join(", ", vm.CatalogCategories.Take(12).Select(c => c.Name)));
                return Task.CompletedTask;
            });

            // The search: "engine rpm", "rpm", "prop pitch", the key "pitch_minus" and the id, with the Spitfire ticked.
            foreach (string query in new[] { "engine rpm", "rpm", "prop pitch", "pitch_minus", "dnilp3007unilcd2vdnilvp-1vunil" })
            {
                Step("titles: search '" + query + "' for the Spitfire", async () =>
                {
                    vm!.SelectedRelation = null;
                    vm.ShowBuilderFindCommand.Execute(null);
                    vm.BuilderSearchPlanes.Clear();
                    vm.BuilderSearchPlanes.Add("SpitfireLFMkIX");
                    vm.BuilderSearchText = query;
                    await vm.SearchBuilderActionsAsync();
                    List<BuilderSearchResultItem> rows = vm.BuilderSearchResults.ToList();
                    List<BuilderSearchResultItem> spitfire = rows.Where(r => r.Game == "DCS" && r.Planes.Contains("SpitfireLFMkIX", StringComparer.OrdinalIgnoreCase)).ToList();
                    Note(query + ": " + vm.BuilderSearchStatus + "; Spitfire rows: " + string.Join(" | ", spitfire.Select(r => r.DisplayTitle)));
                    List<string> raw = rows.Select(r => r.DisplayTitle).Where(LooksRaw).ToList();
                    if (raw.Count > 0)
                        Failures.Add("search '" + query + "' listed raw titles: " + string.Join(" | ", raw.Take(5)));
                    if (!spitfire.Any(r => r.DisplayTitle.Contains("Engine RPM", StringComparison.OrdinalIgnoreCase)))
                        Failures.Add("search '" + query + "' did not find the Spitfire's Engine RPM actions");
                }, extraRender: CheckScreen);
            }
            vm.BuilderSearchPlanes.Clear();

            // The Binds page's peek of every bound relation with a DCS action.
            Step("titles: the Binds page's peek", async () =>
            {
                vm!.CurrentStage = UiStage.Binds;
                vm.IsBindPeekOpen = true;
                List<string> raw = [];
                int peeked = 0;
                foreach (string relation in vm.AllRelationsForTests().Where(r => r.IsBound && r.GameList.Contains("DCS")).Select(r => r.Name).Take(12).ToList())
                {
                    await vm.LoadBindPeekAsync(relation);
                    peeked++;
                    raw.AddRange(vm.BindPeekGroups.SelectMany(g => g.Actions).Select(a => a.Title).Where(t => LooksRaw(t)).Select(t => relation + ": " + t));
                }
                // The peek of a Spitfire relation on screen (the selected bind).
                string shownPeek = spitfireRelation.Length > 0 ? spitfireRelation : vm.AllRelationsForTests().First(r => r.IsBound).Name;
                vm.SelectedBind = vm.Binds.FirstOrDefault(b => b.RelationName == shownPeek);
                await vm.LoadBindPeekAsync(shownPeek);
                if (!vm.BindPeekGroups.Any())
                    Failures.Add("the bind peek of " + shownPeek + " shows no actions");
                Note("peek on screen: " + shownPeek + " = " + string.Join(" | ", vm.BindPeekGroups.SelectMany(g => g.Actions).Select(a => a.Title)));
                Note("peeked " + peeked + " relations; raw titles: " + raw.Count + (raw.Count > 0 ? " e.g. " + string.Join(" | ", raw.Take(5)) : string.Empty));
                if (raw.Count > 0)
                    Failures.Add(raw.Count + " raw titles in the bind peek, e.g. " + string.Join(" | ", raw.Take(5)));
            }, extraRender: CheckScreen);

            // The layout titles (cheat sheets, tables, legend) of every device of the profile.
            Step("titles: layout titles of every device", async () =>
            {
                List<string> raw = [];
                int count = 0;
                foreach (string device in vm!.AllRelationsForTests().Where(r => r.IsBound).Select(r => r.BindDeviceFull).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                {
                    EngineCommandResult? result = await vm.RunEngineForTestsAsync(["layout", "titles", "--device", device]);
                    foreach (string line in (result?.StandardOutput ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Contains(" = ", StringComparison.Ordinal)))
                    {
                        count++;
                        string title = line[(line.IndexOf(" = ", StringComparison.Ordinal) + 3)..];
                        if (LooksRaw(title))
                            raw.Add(DisplayText.ShortDevice(device) + ": " + title);
                    }
                }
                Note(count + " layout titles; raw: " + raw.Count + (raw.Count > 0 ? " e.g. " + string.Join(" | ", raw.Take(5)) : string.Empty));
                if (raw.Count > 0)
                    Failures.Add(raw.Count + " raw layout titles, e.g. " + string.Join(" | ", raw.Take(5)));
            });
            // The Layout page (tables, legend) as it opens.
            Step("titles: the Layout page", () =>
            {
                vm!.CurrentStage = UiStage.Layout;
                return Task.CompletedTask;
            }, extraRender: CheckScreen);
        }
    }
}
