using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests.QualityGate;

// I5 and I6 of the quality gate (QualityGate.cs): suggestions of typical relations, readable titles of every relation
// of the profile.
[SupportedOSPlatform("windows")]
public sealed class QualityGateRelationTests
{
    // (search, kind, first game, second game): a new relation with the first game's action, then the second game's.
    internal static readonly (string Query, string Kind, string First, string Second)[] Scenarios =
    [
        ("pitch", "axis", "DCS", "WarThunder"),
        ("roll", "axis", "DCS", "MSFS2024"),
        ("throttle", "axis", "DCS", "IL2Game"),
        ("gear", "button", "DCS", "StarCitizen"),
        ("pitch", "axis", "WarThunder", "XPlane12"),
        ("flaps", "button", "MSFS2024", "XPlane12"),
    ];

    private const string UnknownFunction = "does not know this function yet";

    // One finding per unreadable suggestion title (the same rows come back after each step).
    private static readonly HashSet<string> SeenSuggestions = new(StringComparer.OrdinalIgnoreCase);

    // I5: suggestions are there after the second game's action and stay after the profile is opened again.
    [Fact]
    public void I5_SuggestionsForTypicalRelationsStayNonEmpty()
    {
        if (!Gate.Enabled)
            return;
        string profile = Gate.CopyProfile("suggestions") ?? Gate.EmptyProfile("suggestions");
        List<string> created = [];
        GateSession.Run(profile, async s =>
        {
            MainWindowViewModel vm = s.Vm;
            if (System.IO.File.Exists(profile))
                await vm.LoadProfileCommand.ExecuteAsync(null);
            vm.CurrentStage = UiStage.Relations;
            await s.SettleAsync(() => true);
            foreach ((string query, string kind, string first, string second) in Scenarios)
            {
                string name = "QG " + query + " " + first + "+" + second;
                string subject = query + " (" + first + " then " + second + ")";
                vm.StartNewBuilderRelationCommand.Execute(null);
                await s.SettleAsync(() => true);
                if (!await AddFromSearchAsync(s, query, kind, first, name, subject))
                    continue;
                if (!string.Equals(vm.SelectedRelation?.Name, name, StringComparison.Ordinal))
                {
                    Gate.Fail("I5", "the new relation is not open after Create relation", first, subject, "open: " + (vm.SelectedRelation?.Name ?? "(none)"));
                    continue;
                }
                created.Add(name);
                CheckSuggestions(vm, first, subject, "after the first action", required: false);

                if (!await AddFromSearchAsync(s, query, kind, second, null, subject))
                    continue;
                await s.SettleAsync(() => true);
                int games = vm.RelationNodes.Select(n => n.Game).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (games < 2)
                    Gate.Fail("I5", "the second game's action is not in the relation", second, subject, "games: " + string.Join(", ", vm.RelationNodes.Select(n => n.Game).Distinct()));
                CheckSuggestions(vm, second, subject, "after the second game's action", required: true);
                await s.MeasureAsync("suggestions", subject, () => vm.LoadBuilderSuggestionsAsync(name));
                CheckSuggestions(vm, second, subject, "loaded again", required: true);
            }

            // Reload: the profile opened again (as after a restart), each relation selected.
            await vm.SaveProfileCommand.ExecuteAsync(null);
            await vm.LoadProfileCommand.ExecuteAsync(null);
            vm.CurrentStage = UiStage.Relations;
            await s.SettleAsync(() => true);
            foreach (string name in created)
            {
                RelationListItem? relation = vm.Relations.FirstOrDefault(r => r.Name == name);
                if (relation == null)
                {
                    Gate.Fail("I5", "relation gone after the profile was opened again", string.Empty, name);
                    continue;
                }
                vm.SelectedRelation = relation;
                await Task.Delay(200);
                await s.SettleAsync(() => vm.RelationNodes.Count > 0 && !vm.IsBuilderSuggestLoading);
                CheckSuggestions(vm, string.Empty, name, "after the profile was opened again", required: true);
            }
        });
        Gate.Finish("I5");
    }

    private static void CheckSuggestions(MainWindowViewModel vm, string game, string subject, string when, bool required)
    {
        int visible = vm.BuilderSuggestions.Count;
        string empty = vm.ShowBuilderSuggestEmpty ? vm.BuilderSuggestEmptyText : string.Empty;
        if (visible == 0 && required)
            Gate.Fail("I5", "no suggestions " + when, game, subject, "weak hidden: " + vm.BuilderWeakCount + "; function: '" + vm.BuilderFunctionText + "'; " + Gate.Short(empty.Length > 0 ? empty : vm.BuilderSuggestStatus, 140));
        else if (visible == 0)
            Gate.Note("I5: no suggestions " + when + " for " + subject + " (" + Gate.Short(empty, 100) + ")");
        if (required && empty.Contains(UnknownFunction, StringComparison.Ordinal))
            Gate.Fail("I5", "'does not know this function yet' " + when, game, subject, "actions of " + vm.RelationNodes.Select(n => n.Game).Distinct().Count() + " games in the relation");
        foreach (BuilderSuggestionItem item in vm.BuilderSuggestions)
        {
            string? why = Gate.UnreadableTitle(item.DisplayTitle, item.Id);
            if (why != null && SeenSuggestions.Add(item.Game + "|" + item.Id + "|" + item.DisplayTitle))
                Gate.Fail("I5", "suggestion " + why, item.Game, subject + ": '" + Gate.Short(item.DisplayTitle, 60) + "'", "id " + Gate.Short(item.Id, 60));
        }
    }

    // The search under Add actions, the game's chip ticked, its best row ticked and added (a new relation when name is set).
    private static async Task<bool> AddFromSearchAsync(GateSession s, string query, string kind, string game, string? name, string subject)
    {
        MainWindowViewModel vm = s.Vm;
        vm.ShowBuilderFindCommand.Execute(null);
        if (vm.CanChangeBuilderKind && vm.BuilderKindFilter != kind)
        {
            vm.BuilderKindFilter = kind;
            await s.SettleAsync(() => true);
        }
        await QualityGateCatalogTests.SearchAsync(s, query, "relation " + subject);
        BuilderGameChip? chip = vm.BuilderSearchGames.FirstOrDefault(c => string.Equals(c.GameId, game, StringComparison.OrdinalIgnoreCase));
        if (chip == null)
        {
            string why = vm.BuilderSearchResults.Count == 0
                ? "; diagnosis: " + await s.WhyEmptyAsync(["relations", "search", query, "--" + kind, "--limit", "200", "--per-game", "12"])
                : string.Empty;
            Gate.Fail("I5", vm.BuilderSearchResults.Count == 0 ? "search shows nothing (the relation cannot be built)" : "search has no rows of the game to add", game, subject,
                "'" + query + "' (" + kind + "): " + Gate.Short(vm.BuilderSearchStatus, 120) + why);
            return false;
        }
        foreach (BuilderGameChip other in vm.BuilderSearchGames.Where(c => c.IsSelected && c != chip).ToList())
            other.IsSelected = false;
        chip.IsSelected = true;
        await Task.Delay(100);
        await s.SettleAsync(() => true);
        bool axis = kind == "axis";
        BuilderSearchResultItem? row = vm.BuilderSearchResults
            .Where(r => string.Equals(r.Game, game, StringComparison.OrdinalIgnoreCase) && r.CanCheck && r.IsAxis == axis)
            .OrderByDescending(r => r.IsRecommended).ThenByDescending(r => r.Primary).FirstOrDefault();
        if (row == null)
        {
            Gate.Fail("I5", "search has no " + kind + " row of the game to add", game, subject, "'" + query + "': " + vm.BuilderSearchResults.Count + " rows");
            chip.IsSelected = false;
            return false;
        }
        row.IsChecked = true;
        if (name != null)
            vm.RelationNameInput = name;
        await s.MeasureAsync(name != null ? "relation create" : "relation add", subject + ": " + game + " '" + Gate.Short(row.DisplayTitle, 40) + "'", () => vm.AddCheckedBuilderResultsAsync());
        BuilderGameChip? again = vm.BuilderSearchGames.FirstOrDefault(c => c.IsSelected);
        if (again != null)
        {
            again.IsSelected = false;
            await Task.Delay(100);
            await s.SettleAsync(() => true);
        }
        return true;
    }

    // I6: every relation of the profile shows readable titles in the editor, the binds peek and the layout table.
    [Fact]
    public void I6_EveryProfileRelationShowsReadableTitles()
    {
        if (!Gate.Enabled)
            return;
        string? profile = Gate.CopyProfile("titles");
        if (profile == null)
        {
            Gate.Note("I6 skipped: no profile (JOYPRO_QUALITY_GATE_PROFILE)");
            Gate.Finish("I6");
            return;
        }
        int limit = Gate.Limit("JOYPRO_QUALITY_GATE_RELATIONS");
        GateSession.Run(profile, async s =>
        {
            MainWindowViewModel vm = s.Vm;
            await vm.LoadProfileCommand.ExecuteAsync(null);
            // The Binds page: the editor's actions load with the selection, the peek is opened here; no suggestions run.
            vm.CurrentStage = UiStage.Binds;
            await s.SettleAsync(() => true);
            List<RelationListItem> relations = vm.Relations.ToList();
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            int opened = 0;
            foreach (RelationListItem relation in relations.Take(limit))
            {
                opened++;
                await s.MeasureAsync("relation open", relation.Name, async () =>
                {
                    vm.SelectedRelation = relation;
                    await Task.Delay(50);
                    await s.SettleAsync(() => vm.SelectedRelation?.Name == relation.Name, 30000);
                });
                if (vm.RelationNodes.Count == 0)
                    Gate.Fail("I6", "relation opens without actions in the editor", string.Empty, relation.Name, "the relation list says games: " + relation.Games);
                foreach (RelationNodeItem node in vm.RelationNodes.ToList())
                {
                    foreach (string title in node.TitlesDisplay.Split('\n').Select(t => t.Trim()).Where(t => t.Length > 0).DefaultIfEmpty(node.TitlesSummary))
                        CheckTitle("editor", node.Game, relation.Name, title, node.Id, seen);
                }

                // The binds peek of the same relation.
                vm.IsBindPeekOpen = true;
                await vm.LoadBindPeekAsync(relation.Name);
                foreach (BindPeekAction action in vm.BindPeekGroups.SelectMany(g => g.Actions))
                    CheckTitle("binds peek", action.Game, relation.Name, action.Title, string.Empty, seen);
            }
            Gate.Note("I6: " + opened + " of " + relations.Count + " relations opened in the editor and the binds peek");

            // The layout table's game titles ("engine layout titles", the preview of each game and aircraft) per bound device.
            List<string> devices = vm.Binds.Where(b => b.HasBind && b.Device.Length > 0).Select(b => b.Device).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string device in devices)
            {
                EngineCommandResult result = await s.Engine.RunAsync(Gate.EnginePath, ["--profile", profile, "--status", "false", "layout", "titles", "--device", device]);
                if (result.ExitCode != 0)
                {
                    Gate.Fail("I6", "layout titles fail for a device", string.Empty, device, Gate.Short(result.StandardError, 140));
                    continue;
                }
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(MainWindowViewModel.ExtractJsonPayload(result.StandardOutput));
                    if (!doc.RootElement.TryGetProperty("Targets", out JsonElement targets) || targets.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (JsonElement target in targets.EnumerateArray())
                    {
                        string game = target.TryGetProperty("Game", out JsonElement g) ? g.GetString() ?? string.Empty : string.Empty;
                        string plane = target.TryGetProperty("Plane", out JsonElement p) ? p.GetString() ?? string.Empty : string.Empty;
                        if (!target.TryGetProperty("Titles", out JsonElement titles) || titles.ValueKind != JsonValueKind.Object)
                            continue;
                        foreach (JsonProperty title in titles.EnumerateObject())
                            CheckTitle("layout table", game, DisplayDevice(device) + " · " + plane + " · " + title.Name, title.Value.GetString() ?? string.Empty, string.Empty, seen);
                    }
                }
                catch (JsonException ex)
                {
                    Gate.Fail("I6", "layout titles do not parse", string.Empty, device, Gate.Short(ex.Message, 100));
                }
            }
            Gate.Note("I6: layout titles checked for " + devices.Count + " bound devices");

            // The check's problems on the Export page (actions in several relations): every text in words, no raw id. The
            // user's own relation and device names ("keep in 'MFD__RIGHT__T2'") are his data, not ids JoyPro shows.
            await vm.ValidateProfileCommand.ExecuteAsync(null);
            List<string> userNames = vm.AllRelationsForTests().Select(r => r.Name)
                .Concat(vm.AllRelationsForTests().Select(r => r.BindDeviceFull).Concat(vm.Binds.Select(b => b.Device))
                    .Where(d => d.Length > 0).SelectMany(d => new[] { d, DisplayDevice(d) }))
                .ToList();
            int cards = 0;
            string? canaryText = null;
            foreach (CheckIssueCard card in vm.CheckSections.SelectMany(section => section.Cards))
            {
                cards++;
                foreach (string text in new[] { card.Headline, card.Details, card.SuggestionText, card.ReasonText }.Concat(card.ActionLines).Concat(card.Choices.Select(c => c.KeepText)))
                {
                    string? raw = RawIdInShownText(text, userNames);
                    if (raw != null)
                        Gate.Fail("I6", "the check's problems show a raw id", string.Empty, Gate.Short(card.Headline, 80), raw + " in: " + Gate.Short(text, 140));
                    else if (canaryText == null && RawIdIn(text) != null)
                        canaryText = text;
                }
            }
            Gate.Note("I6: " + cards + " cards of the check's problems read");
            // The exemption keeps the scan sharp: a raw DCS id put into a card text that names a relation is still caught.
            if (canaryText != null)
            {
                const string Synthetic = "d3009pnilunilcd1vd1vpnilvunil";
                if (RawIdInShownText(canaryText + " " + Synthetic, userNames) is not { } caught || !caught.Contains(Synthetic, StringComparison.Ordinal))
                    Gate.Fail("I6", "the raw-id scan misses an id next to a relation name", string.Empty, Synthetic, Gate.Short(canaryText, 140));
                else
                    Gate.Note("I6: a raw id injected into a card text with a relation name is caught");
            }
        });
        Gate.Finish("I6");
    }

    private static readonly System.Text.RegularExpressions.Regex RawIdPattern = new(
        @"\bd-?\d+p(nil|-?[\d.]+)u\S*|\b[ad]\d{3,4}cd\S*|\{[0-9A-Fa-f]{8}-[0-9A-Fa-f-]+\}|\bJOY_[A-Z0-9_]+|\S+__[A-Za-z]\S*", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // The first raw id in a shown text (an encoded DCS id, a GUID, an input token, "id__aircraft"), or null.
    internal static string? RawIdIn(string? text)
    {
        System.Text.RegularExpressions.Match m = RawIdPattern.Match(text ?? string.Empty);
        return m.Success ? m.Value : null;
    }

    // I6's scan of the check's problems: the user's own relation names (texts of the Holdi profile's cards) pass, a raw
    // id next to them, a GUID outside a device name and an unquoted "id__aircraft" are still caught. Runs without the gate.
    [Fact]
    public void RawIdScan_SkipsTheUsersOwnNames_ButCatchesIdsBesideThem()
    {
        string[] names = ["MFD__RIGHT__T2", "Airbrake toggle", "F16 MFD 1 {4F0B1A20-0000-0000-0000-504944564944}", "F16 MFD 1"];
        Assert.Null(RawIdInShownText("is in 2 relations: 'Airbrake toggle' (not bound) and 'MFD__RIGHT__T2' (F16 MFD 1 · BTN 2). DCS gets a bind on each of these inputs.", names));
        Assert.Null(RawIdInShownText("92% sure: keep in 'MFD__RIGHT__T2'", names));
        Assert.Null(RawIdInShownText("MFD__RIGHT__T2", names));
        Assert.Null(RawIdInShownText("on 'F16 MFD 1 {4F0B1A20-0000-0000-0000-504944564944}'", names));

        Assert.Equal("d3009pnilunilcd1vd1vpnilvunil", RawIdInShownText("Keep in 'MFD__RIGHT__T2' only d3009pnilunilcd1vd1vpnilvunil", names));
        Assert.Equal("'MFD__RIGHT__T3'", RawIdInShownText("Keep in 'MFD__RIGHT__T3'", names));
        Assert.Equal("iCommandPlaneAirBrake__F-16C_50", RawIdInShownText("'Airbrake toggle' has iCommandPlaneAirBrake__F-16C_50", names));
        Assert.Equal("{AAAAAAAA-0000-0000-0000-504944564944}", RawIdInShownText("on {AAAAAAAA-0000-0000-0000-504944564944}", names));
        Assert.Equal("MFD__RIGHT__T2", RawIdInShownText("Keep in MFD__RIGHT__T2 only", names));
    }

    // The first raw id in a shown text that is not one of the user's own names (relation and device names from his
    // profile): the whole text or a quoted name ('MFD__RIGHT__T2') is his data, the rest is still scanned.
    internal static string? RawIdInShownText(string? text, IEnumerable<string> userNames)
    {
        string shown = text ?? string.Empty;
        List<string> names = userNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).OrderByDescending(n => n.Length).ToList();
        if (names.Contains(shown.Trim(), StringComparer.Ordinal))
            return null;
        foreach (string name in names)
            shown = shown.Replace("'" + name + "'", "'…'", StringComparison.Ordinal);
        return RawIdIn(shown);
    }

    private static string DisplayDevice(string device)
    {
        int brace = device.IndexOf('{');
        return (brace > 0 ? device[..brace] : device).Trim();
    }

    private static void CheckTitle(string where, string game, string subject, string title, string id, HashSet<string> seen)
    {
        string text = (title ?? string.Empty).Trim();
        const string Unknown = "(not in this aircraft's current command list)";
        string? why;
        if (text.Contains(Unknown, StringComparison.Ordinal))
        {
            string shown = text.Replace(Unknown, string.Empty).Trim();
            why = Gate.UnreadableTitle(shown, id) != null ? "unknown id shown as a raw id" : "unknown id (not in the aircraft's current command list)";
        }
        else
        {
            why = Gate.UnreadableTitle(text, id);
        }
        if (why == null || !seen.Add(where + "|" + game + "|" + subject + "|" + text))
            return;
        Gate.Fail("I6", where + ": " + why, game, subject, "'" + Gate.Short(text, 80) + "'" + (id.Length > 0 && !string.Equals(id, text, StringComparison.OrdinalIgnoreCase) ? " id " + Gate.Short(id, 50) : string.Empty));
    }
}
