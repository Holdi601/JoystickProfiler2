using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// Same id, different meaning per aircraft (docs/relation-builder.md "Same id, different meaning"; engine
// EngineIdMeanings): a DCS id is "Pitch" in the jets and "Collective" in a helicopter, a cockpit switch id is another
// switch in every module. So every search row and every action of the open relation has an aircraft selector: every
// aircraft with that id, each with its own title and category, a tick, All / None / Only same title / Invert, a filter
// and a "differs" marker. The Relations page also shows a notice when the profile has actions with aircraft whose
// meaning of the id differs (engine relations check-aircraft) and repairs them in one step.
public sealed partial class MainWindowViewModel
{
    // ---------------------------------------------------------------------------------------------------------
    // Search rows: the engine's per-aircraft meanings, ticks that survive a new search
    // ---------------------------------------------------------------------------------------------------------

    private BuilderSearchResultItem ParseBuilderSearchHit(JsonElement item)
    {
        string function = ReadJsonString(item, "Function");
        BuilderSearchResultItem row = new(
            ReadJsonString(item, "Game"), ReadJsonString(item, "Id"), ReadJsonString(item, "Title"), ReadJsonString(item, "Category"),
            ReadJsonBool(item, "IsAxis"), ReadStringArray(item, "Planes"), ReadStringArray(item, "InRelations"), ReadJsonString(item, "Why"))
        {
            Match = ReadJsonString(item, "Match"),
            Primary = ReadJsonBool(item, "Primary"),
            Display = ReadJsonString(item, "Display"),
            FunctionName = builderFunctionGroups.FirstOrDefault(g => string.Equals(g.Id, function, StringComparison.OrdinalIgnoreCase)).Name ?? string.Empty,
            MeaningNote = ReadJsonString(item, "MeaningNote"),
            SameIdDifferent = ReadJsonInt(item, "SameIdDifferent"),
            NearCount = ReadJsonInt(item, "Near")
        };
        row.Aircraft = ParseAircraftVariants(item, row.Title);
        return row;
    }

    // "Variants" of relations search / resolve / suggest as an aircraft selector (DCS rows only).
    internal static AircraftSelector? ParseAircraftVariants(JsonElement item, string rowTitle)
    {
        if (!item.TryGetProperty("Variants", out JsonElement variants) || variants.ValueKind != JsonValueKind.Array || variants.GetArrayLength() == 0)
            return null;
        List<(string, string, string, string, bool, IReadOnlyList<string>)> list = [];
        foreach (JsonElement v in variants.EnumerateArray())
        {
            list.Add((ReadJsonString(v, "Title"), ReadJsonString(v, "Category"), ReadJsonString(v, "Match"), ReadJsonString(v, "Note"),
                ReadJsonBool(v, "Included"), ReadStringArray(v, "Planes")));
        }
        return AircraftSelector.FromVariants(rowTitle, list);
    }

    // The ticks (and the aircraft ticks the user changed) of the rows that come back.
    private static void KeepBuilderTicks(IEnumerable<BuilderSearchResultItem> before, IEnumerable<BuilderSearchResultItem> after)
    {
        Dictionary<string, BuilderSearchResultItem> old = new(StringComparer.OrdinalIgnoreCase);
        foreach (BuilderSearchResultItem row in before)
            old[row.RowKey] = row;
        foreach (BuilderSearchResultItem row in after)
        {
            if (!old.TryGetValue(row.RowKey, out BuilderSearchResultItem? previous))
                continue;
            if (previous.Aircraft != null && row.Aircraft != null && previous.Aircraft.HasChanges)
            {
                HashSet<string> ticked = previous.Aircraft.CheckedPlanes.ToHashSet(StringComparer.OrdinalIgnoreCase);
                HashSet<string> known = previous.Aircraft.Items.Select(i => i.Plane).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (AircraftChoiceItem choice in row.Aircraft.Items.Where(i => known.Contains(i.Plane)))
                    choice.IsChecked = ticked.Contains(choice.Plane);
            }
            if (row.Aircraft != null && previous.Aircraft != null)
                row.Aircraft.IsExpanded = previous.Aircraft.IsExpanded;
            row.IsChecked = previous.IsChecked;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Show more / Show less per game, Expand all / Collapse all (kept while the query is the same)
    // ---------------------------------------------------------------------------------------------------------

    private readonly HashSet<string> builderExpandedGames = new(StringComparer.OrdinalIgnoreCase);
    private string builderExpandedSearchKey = string.Empty;

    public bool CanExpandAllBuilderGames => BuilderSearchRows.OfType<BuilderSearchGroupItem>().Any(g => g.IsGame && !g.IsExpanded && g.Hidden > 0);

    public bool CanCollapseAllBuilderGames => builderExpandedGames.Count > 0;

    [RelayCommand]
    internal async Task ToggleBuilderGameExpandedAsync(BuilderSearchGroupItem? group)
    {
        if (group == null || !group.IsGame)
            return;
        if (builderExpandedGames.Contains(group.Game))
        {
            builderExpandedGames.Remove(group.Game);
            RebuildBuilderSearchRows();
            return;
        }
        await ExpandBuilderGamesAsync([group.Game]);
    }

    [RelayCommand]
    internal async Task ExpandAllBuilderGamesAsync()
    {
        List<string> games = BuilderSearchRows.OfType<BuilderSearchGroupItem>().Where(g => g.IsGame && !g.IsExpanded && g.Hidden > 0).Select(g => g.Game).ToList();
        if (games.Count > 0)
            await ExpandBuilderGamesAsync(games);
    }

    [RelayCommand]
    internal void CollapseAllBuilderGames()
    {
        builderExpandedGames.Clear();
        RebuildBuilderSearchRows();
    }

    // The rest of these games' hits for the current query (one engine run), merged into the result; ticks stay.
    private async Task ExpandBuilderGamesAsync(List<string> games)
    {
        string query = (BuilderSearchText ?? string.Empty).Trim();
        if (query.Length == 0)
            return;
        List<string> need = games.Where(g => allBuilderSearchResults.Count(r => string.Equals(r.Game, g, StringComparison.OrdinalIgnoreCase)) <
                                             (builderGameTotals.TryGetValue(g, out int total) ? total : int.MaxValue)).ToList();
        if (need.Count > 0)
        {
            List<string> args = ["relations", "search", query, "--game", string.Join("|", need)];
            if (BuilderSearchPlanes.Count > 0)
                args.AddRange(["--plane", string.Join("|", BuilderSearchPlanes)]);
            string kind = BuilderEffectiveKind;
            if (kind == "axis")
                args.Add("--axis");
            else if (kind == "button")
                args.Add("--button");
            args.AddRange(["--limit", (BuilderSearchLimit * Math.Max(1, need.Count)).ToString(CultureInfo.InvariantCulture)]);
            EngineCommandResult? result = await RunEngineAsync(args, includeProfile: HasProfile, mutatesProfile: false, busyLabel: "Loading every action of " +
                string.Join(", ", need.Select(DisplayText.Game)) + "...", forceText: false, background: true);
            if (result == null || result.ExitCode != 0)
            {
                BuilderSearchStatus = T("Could not load the rest: {0}", (result?.StandardError.Trim() ?? StatusText));
                return;
            }
            List<BuilderSearchResultItem> more = [];
            try
            {
                using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
                if (doc.RootElement.TryGetProperty("Results", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in list.EnumerateArray())
                        more.Add(ParseBuilderSearchHit(item));
                }
            }
            catch (JsonException)
            {
                BuilderSearchStatus = T("Could not read the search result.");
                return;
            }
            if (!string.Equals((BuilderSearchText ?? string.Empty).Trim(), query, StringComparison.Ordinal))
                return;
            HashSet<string> loaded = need.ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<BuilderSearchResultItem> previous = allBuilderSearchResults.Where(r => loaded.Contains(r.Game)).ToList();
            KeepBuilderTicks(previous, more);
            // The game's rows in the engine's order, the other games untouched.
            List<BuilderSearchResultItem> merged = [];
            bool inserted = false;
            foreach (BuilderSearchResultItem row in allBuilderSearchResults)
            {
                if (loaded.Contains(row.Game))
                {
                    if (!inserted)
                    {
                        merged.AddRange(more);
                        inserted = true;
                    }
                    continue;
                }
                merged.Add(row);
            }
            if (!inserted)
                merged.AddRange(more);
            foreach (BuilderSearchResultItem item in allBuilderSearchResults)
                item.PropertyChanged -= OnBuilderSearchRowChanged;
            allBuilderSearchResults.Clear();
            allBuilderSearchResults.AddRange(merged);
            foreach (BuilderSearchResultItem item in allBuilderSearchResults)
                item.PropertyChanged += OnBuilderSearchRowChanged;
            MarkBuilderRowsInRelation();
        }
        foreach (string game in games)
            builderExpandedGames.Add(game);
        RebuildBuilderSearchRows();
    }

    // ---------------------------------------------------------------------------------------------------------
    // The open relation's actions: the aircraft of each, editable at any time
    // ---------------------------------------------------------------------------------------------------------

    private int nodeAircraftVersion;

    // engine relations aircraft <relation>: every node's aircraft with on / off / absent and same / near / differs, and
    // every node's title in its game (the other games' nodes show it instead of their id).
    internal async Task LoadRelationNodeAircraftAsync(string relation)
    {
        int version = ++nodeAircraftVersion;
        if (RelationNodes.Count == 0)
            return;
        EngineCommandResult? result = await RunEngineAsync(["relations", "aircraft", relation], includeProfile: true, mutatesProfile: false,
            busyLabel: "Reading the aircraft of '" + relation + "'...", forceText: false, background: true);
        if (version != nodeAircraftVersion || result == null || result.ExitCode != 0)
            return;
        ApplyNodeAircraft(result.StandardOutput);
    }

    internal void ApplyNodeAircraft(string output)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            if (!doc.RootElement.TryGetProperty("Nodes", out JsonElement nodes) || nodes.ValueKind != JsonValueKind.Array)
                return;
            foreach (JsonElement node in nodes.EnumerateArray())
            {
                string game = ReadJsonString(node, "Game");
                string id = ReadJsonString(node, "Id");
                RelationNodeItem? item = RelationNodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase) && string.Equals(n.Game, game, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                    continue;
                if (!game.Equals("DCS", StringComparison.OrdinalIgnoreCase))
                {
                    item.UseTitle(ReadJsonString(node, "Title"));
                    continue;
                }
                List<AircraftChoiceItem> choices = [];
                if (node.TryGetProperty("Aircraft", out JsonElement aircraft) && aircraft.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement a in aircraft.EnumerateArray())
                    {
                        choices.Add(new AircraftChoiceItem(ReadJsonString(a, "Plane"), ReadJsonString(a, "Title"), ReadJsonString(a, "Category"),
                            ReadJsonString(a, "Match"), ReadJsonString(a, "Note"), ReadJsonString(a, "State") == "on", ReadJsonBool(a, "SameFunction")));
                    }
                }
                item.MeaningTitle = ReadJsonString(node, "Title");
                // Another title of the same function (a helicopter's cyclic pitch) is no mixed meaning.
                item.Differs = Math.Max(0, ReadJsonInt(node, "Differs") - ReadJsonInt(node, "DiffersSameFunction"));
                item.Excluded = ReadJsonInt(node, "Excluded");
                string key = NodeAircraftKey(item);
                bool wasExpanded = item.Aircraft?.IsExpanded ?? expandedNodeAircraft.Contains(key);
                // By meaning (AircraftSelector), in each the ticked ones first (as the node holds them), then the rest.
                AircraftSelector selector = new(item.MeaningTitle, choices.OrderBy(c => c.IsChecked ? 0 : 1).ThenBy(c => c.Plane, StringComparer.OrdinalIgnoreCase))
                {
                    IsExpanded = wasExpanded
                };
                // Open stays open over a reload of the relation (Apply, Undo, a refresh).
                selector.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(AircraftSelector.IsExpanded))
                        return;
                    if (selector.IsExpanded)
                        expandedNodeAircraft.Add(key);
                    else
                        expandedNodeAircraft.Remove(key);
                };
                item.Aircraft = selector;
                item.NotifyMeaningChanged();
            }
        }
        catch (JsonException)
        {
        }
        OnPropertyChanged(nameof(HasMixedRelationNodes));
        OnPropertyChanged(nameof(MixedRelationNodesText));
    }

    // The actions whose aircraft are open (relation|game|id): they stay open when the relation is loaded again.
    private readonly HashSet<string> expandedNodeAircraft = new(StringComparer.OrdinalIgnoreCase);

    private string NodeAircraftKey(RelationNodeItem node) => (SelectedRelation?.Name ?? string.Empty) + "|" + node.Game + "|" + node.Id;

    // A click on an action of the open relation: its aircraft open (or close). Games with one list per game (one
    // aircraft) have none. Before the aircraft are read (engine relations aircraft runs in the background) the click is
    // remembered and the list opens when they come.
    [RelayCommand]
    internal void ToggleNodeAircraft(RelationNodeItem? node)
    {
        if (node == null)
            return;
        SelectedRelationNode = node;
        if (node.Aircraft != null)
        {
            if (node.Aircraft.Total > 0)
                node.Aircraft.IsExpanded = !node.Aircraft.IsExpanded;
            return;
        }
        if (!node.Game.Equals("DCS", StringComparison.OrdinalIgnoreCase))
            return;
        string key = NodeAircraftKey(node);
        if (!expandedNodeAircraft.Remove(key))
            expandedNodeAircraft.Add(key);
    }

    public bool HasMixedRelationNodes => RelationNodes.Any(n => n.IsMixed);

    public string MixedRelationNodesText
    {
        get
        {
            List<RelationNodeItem> mixed = RelationNodes.Where(n => n.IsMixed).ToList();
            return mixed.Count == 0 ? string.Empty
                : T("{0}{1}: the same id means something else in some of its ticked aircraft. Open its aircraft and untick them.", mixed.Count, (mixed.Count == 1 ? " action mixes meanings" : " actions mix meanings"));
        }
    }

    // Apply: the node's ticks in one engine run (one undo step).
    [RelayCommand]
    internal async Task ApplyNodeAircraftAsync(RelationNodeItem? node)
    {
        if (node?.Aircraft == null || SelectedRelation == null || !node.Aircraft.HasChanges)
            return;
        List<string> on = node.Aircraft.Items.Where(i => i.IsChecked && !i.InitiallyChecked).Select(i => i.Plane).ToList();
        List<string> off = node.Aircraft.Items.Where(i => !i.IsChecked && i.InitiallyChecked).Select(i => i.Plane).ToList();
        string relation = SelectedRelation.Name;
        List<string> args = ["relations", "aircraft", "set", relation, "--node", node.Game + ":" + node.Id];
        if (on.Count > 0)
            args.AddRange(["--on", string.Join("|", on)]);
        if (off.Count > 0)
            args.AddRange(["--off", string.Join("|", off)]);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: "Changing the aircraft of '" + relation + "'...");
        if (result == null || result.ExitCode != 0)
            return;
        await ReloadBuilderRelationAsync(relation);
        StatusText = T("'{0}': {1}{2}{3}. Ctrl+Z undoes it.", relation, (on.Count > 0 ? on.Count + " aircraft added" : string.Empty), (on.Count > 0 && off.Count > 0 ? ", " : string.Empty), (off.Count > 0 ? off.Count + " switched off" : string.Empty));
    }

    [RelayCommand]
    private void ResetNodeAircraft(RelationNodeItem? node) => node?.Aircraft?.Reset();

    // ---------------------------------------------------------------------------------------------------------
    // The profile check: actions with aircraft whose meaning of the id differs (engine relations check-aircraft)
    // ---------------------------------------------------------------------------------------------------------

    public ObservableCollection<AircraftCheckItem> AircraftCheckItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAircraftCheckNotice))]
    private int aircraftCheckDrop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAircraftCheckNotice))]
    private bool aircraftCheckDismissed;

    [ObservableProperty]
    private string aircraftCheckText = string.Empty;

    // "Same id, different meaning: 360 aircraft in 165 actions"
    [ObservableProperty]
    private string aircraftCheckShort = string.Empty;

    public bool HasAircraftCheckNotice => AircraftCheckDrop > 0 && !AircraftCheckDismissed;

    private int aircraftCheckVersion;

    internal async Task RefreshAircraftCheckAsync()
    {
        if (!HasProfile)
            return;
        int version = ++aircraftCheckVersion;
        EngineCommandResult? result = await RunEngineAsync(["relations", "check-aircraft"], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Checking the aircraft of every relation..."), forceText: false, background: true);
        if (version != aircraftCheckVersion || result == null || result.ExitCode != 0)
            return;
        ApplyAircraftCheck(result.StandardOutput);
    }

    internal void ApplyAircraftCheck(string output)
    {
        AircraftCheckItems.Clear();
        int drop = 0;
        int relations = 0;
        int nodes = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            drop = ReadJsonInt(root, "AircraftToDrop");
            relations = ReadJsonInt(root, "AffectedRelations");
            nodes = ReadJsonInt(root, "AffectedNodes");
            if (root.TryGetProperty("Found", out JsonElement found) && found.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement f in found.EnumerateArray())
                {
                    List<string> planes = ReadStringArray(f, "Drop");
                    if (planes.Count == 0)
                        continue;
                    List<string> meanings = [];
                    if (f.TryGetProperty("Meanings", out JsonElement m) && m.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement v in m.EnumerateArray())
                        {
                            List<string> where = ReadStringArray(v, "Planes").Where(p => planes.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
                            if (where.Count > 0)
                                meanings.Add(T("'{0}' in {1}", ReadJsonString(v, "Title"), BuilderText.Where("DCS", where)));
                        }
                    }
                    AircraftCheckItems.Add(new AircraftCheckItem(ReadJsonString(f, "Relation"), ReadJsonString(f, "Game"), ReadJsonString(f, "Id"), ReadJsonString(f, "Title"), planes, string.Join("; ", meanings)));
                }
            }
        }
        catch (JsonException)
        {
        }
        AircraftCheckText = drop == 0 ? string.Empty
            : T("{0}{1} in {2}{3} include {4} aircraft where the same id means something else (e.g. 'Pitch' in a jet, 'Collective' in a helicopter). Switch them off? They stay listed, one tick brings one back.", nodes, (nodes == 1 ? " action" : " actions"), relations, (relations == 1 ? " relation" : " relations"), drop);
        AircraftCheckShort = drop == 0 ? string.Empty
            : T("Same id, different meaning: {0} aircraft in {1}{2}", drop, nodes, (nodes == 1 ? " action" : " actions"));
        AircraftCheckDrop = drop;
    }

    // Switch every flagged aircraft off (one engine run: one undo step).
    [RelayCommand]
    internal async Task RepairAircraftAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["relations", "check-aircraft", "--apply"], includeProfile: true, mutatesProfile: true,
            busyLabel: N("Switching off aircraft where the id means something else..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        int dropped = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            dropped = ReadJsonInt(doc.RootElement, "Dropped");
        }
        catch (JsonException)
        {
        }
        AircraftCheckDrop = 0;
        AircraftCheckItems.Clear();
        if (SelectedRelation != null)
            await ReloadBuilderRelationAsync(SelectedRelation.Name);
        StatusText = T("{0} aircraft switched off where the id means something else. Ctrl+Z undoes it.", dropped);
    }

    [RelayCommand]
    private void DismissAircraftCheck() => AircraftCheckDismissed = true;

    // Opens a relation of the check.
    [RelayCommand]
    private async Task OpenAircraftCheckRelationAsync(AircraftCheckItem? item)
    {
        if (item != null)
            await OpenBuilderRelationAsync(item.Relation);
    }
}
