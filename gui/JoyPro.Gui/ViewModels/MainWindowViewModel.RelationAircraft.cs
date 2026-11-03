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
            IsNameMatch = ReadJsonBool(item, "Literal"),
            Display = ReadJsonString(item, "Display"),
            FunctionName = builderFunctionGroups.FirstOrDefault(g => string.Equals(g.Id, function, StringComparison.OrdinalIgnoreCase)).Name is { Length: > 0 } groupName ? groupName : ReadJsonString(item, "FunctionName"),
            MeaningNote = ReadJsonString(item, "MeaningNote"),
            SameIdDifferent = ReadJsonInt(item, "SameIdDifferent"),
            NearCount = ReadJsonInt(item, "Near"),
            Readable = ReadJsonString(item, "Readable"),
            IsRecommended = ReadJsonBool(item, "Recommended"),
            RecommendedFor = ReadJsonString(item, "RecommendedFor"),
            Variant = ReadJsonString(item, "Variant"),
            Explanation = ReadJsonString(item, "Explanation"),
            ExplanationSource = ReadJsonString(item, "ExplanationSource")
        };
        row.Aircraft = ParseAircraftVariants(item, row.Title);
        row.OnlyForText = OnlyForText(row.Game, row.Id);
        ReadBuilderReason(row, item);
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
            busyLabel: "Reading the aircraft of '" + relation + "'...", forceText: false, background: true, stillWanted: () => version == nodeAircraftVersion);
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
                    item.UseTitle(ReadJsonString(node, "Title"), ReadJsonString(node, "Explanation"));
                    // MSFS: the aircraft of JoyPro's aircraft list get checkmarks like DCS (every one ticked = all aircraft);
                    // a game without such a list reports only itself.
                    if (!HasOwnAircraftList(game, node))
                        continue;
                }
                List<AircraftChoiceItem> choices = [];
                if (node.TryGetProperty("Aircraft", out JsonElement aircraft) && aircraft.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement a in aircraft.EnumerateArray())
                    {
                        choices.Add(new AircraftChoiceItem(ReadJsonString(a, "Plane"), ReadJsonString(a, "Title"), ReadJsonString(a, "Category"),
                            ReadJsonString(a, "Match"), ReadJsonString(a, "Note"), ReadJsonString(a, "State") == "on", ReadJsonBool(a, "SameFunction"),
                            ReadJsonBool(a, "FromYourFiles")));
                    }
                }
                item.MeaningTitle = ReadJsonString(node, "Title");
                // Several meanings in the ticked aircraft: the main one is the title (not "A, B"), the others lines under it.
                if (game.Equals("DCS", StringComparison.OrdinalIgnoreCase))
                {
                    string meaningNote = NodeMeaningNote(game, item.MeaningTitle, choices);
                    item.UseMeanings(meaningNote.Length > 0 ? DisplayText.ActionTitle(item.MeaningTitle) : string.Empty, meaningNote);
                }
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
        // The banner's example aircraft comes from the aircraft just read.
        UpdateRelationCheck();
        // Opened from a card of the check: its aircraft are marked now that they are there.
        ApplyCheckHighlight();
    }

    // The lines under a node's title when its ticked aircraft call the id differently: another function per meaning
    // ("F-16C: KY-58 MODE Knob - RV (different function)"), the other names of the same function in one line ("also
    // called: Thrust (13), Power Levers (Both) (2)"). Empty: one meaning.
    internal string NodeMeaningNote(string game, string mainTitle, IReadOnlyList<AircraftChoiceItem> choices)
    {
        List<AircraftChoiceItem> ticked = choices.Where(c => c.IsChecked && c.Title.Length > 0 && !string.Equals(c.Title, mainTitle, StringComparison.OrdinalIgnoreCase)).ToList();
        List<string> lines = [];
        foreach (IGrouping<string, AircraftChoiceItem> other in ticked.Where(c => c.IsOtherFunction).GroupBy(c => c.Title, StringComparer.OrdinalIgnoreCase))
            lines.Add(T("{0}: {1} (different function)", PlaneNamesShort(game, other.Select(c => c.Plane).ToList()), other.First().Title));
        List<string> names = ticked.Where(c => !c.IsOtherFunction).GroupBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First().Title + " (" + g.Count().ToString(CultureInfo.CurrentCulture) + ")").ToList();
        if (names.Count > 0)
            lines.Add(T("also called: {0}", string.Join(", ", names)));
        return string.Join(Environment.NewLine, lines);
    }

    // Up to three aircraft by the names people use ("F/A-18C, F-16C Block 50 and 2 more"); another game: the game.
    internal string PlaneNamesShort(string game, IReadOnlyList<string> planes)
    {
        if (!game.Equals("DCS", StringComparison.OrdinalIgnoreCase))
            return DisplayText.Game(game);
        List<string> names = planes.Select(p => AircraftWords(game, [p])).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count <= 3)
            return CheckIssueText.Join(names, T("and"));
        return T("{0} and {1} more", string.Join(", ", names.Take(3)), names.Count - 3);
    }

    // A game node whose aircraft report lists aircraft of the game's list (MSFS), not only the game itself.
    internal static bool HasOwnAircraftList(string game, JsonElement node) =>
        node.TryGetProperty("Aircraft", out JsonElement aircraft) && aircraft.ValueKind == JsonValueKind.Array &&
        aircraft.EnumerateArray().Any(a => !IsAllAircraftScope(game, ReadJsonString(a, "Plane")));

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
        if (!node.Game.Equals("DCS", StringComparison.OrdinalIgnoreCase) && !node.Game.StartsWith("MSFS", StringComparison.OrdinalIgnoreCase))
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
    // The profile check: actions with ticked aircraft where the command does something else (engine relations
    // check-aircraft). The open relation's part is a neutral question above its actions ("In some aircraft this command
    // does something else ... Should this relation also apply there?"); the rest of the profile is the small link
    // "Check all relations (n actions)". Aircraft the user keeps ("Keep for all", "use here") are remembered
    // (list-preferences.json "KeptAircraft": relation|game|id|plane) and not asked about again.
    // ---------------------------------------------------------------------------------------------------------

    // Every flagged action of the profile, without the aircraft the user keeps.
    public ObservableCollection<AircraftCheckItem> AircraftCheckItems { get; } = [];

    // The open relation's flagged actions (the banner).
    public ObservableCollection<AircraftCheckItem> RelationCheckItems { get; } = [];

    // Aircraft to decide on in the whole profile.
    [ObservableProperty]
    private int aircraftCheckDrop;

    // Hides the banner for every relation (the journey's screenshots).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAircraftCheckNotice))]
    private bool aircraftCheckDismissed;

    // The profile-wide summary in the "Check all relations" panel.
    [ObservableProperty]
    private string aircraftCheckText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RelationCheckToggleText))]
    private bool relationCheckExpanded;

    [ObservableProperty]
    private bool profileCheckExpanded;

    // "Ask me later": the relations whose banner is hidden until the next start.
    private readonly HashSet<string> askLaterRelations = new(StringComparer.OrdinalIgnoreCase);

    public bool HasAircraftCheckNotice => RelationCheckItems.Count > 0 && !AircraftCheckDismissed && !askLaterRelations.Contains(SelectedRelation?.Name ?? string.Empty);

    public bool HasProfileCheckLink => AircraftCheckItems.Count > 0;

    public string ProfileCheckLinkText => AircraftCheckItems.Count == 1 ? T("Check all relations (1 action)") : T("Check all relations ({0} actions)", AircraftCheckItems.Count);

    public bool HasSeveralRelationCheckItems => RelationCheckItems.Count > 1;

    private int RelationCheckPlaneCount => RelationCheckItems.Sum(i => i.Planes.Count);

    public string RelationCheckHeadline => T("This button does different things in different aircraft.");

    // The open relation's button as on the key caps ("BTN 20"), else its name in quotes.
    private string RelationCheckButtonWords =>
        SelectedRelation is { IsBound: true } bound && bound.BindInput.Length > 0 ? DeviceMarkerText.ShortLabel(bound.BindInput) : "'" + (SelectedRelation?.Name ?? string.Empty) + "'";

    // The aircraft that keep the action ("Only in the ..."): the node's own meaning.
    private string KeepPlanesWords(AircraftCheckItem item)
    {
        IReadOnlyList<string> keep = item.KeepPlanes.Count > 0 ? item.KeepPlanes : OwnPlanesOf(item);
        return keep.Count > 0 ? PlaneNamesShort(item.Game, keep) : T("the other aircraft");
    }

    // "F/A-18C: RADAR Switch - EMERG · F-16C: KY-58 MODE Knob - RV": each meaning with up to three aircraft.
    private string MeaningsWords(AircraftCheckItem item)
    {
        List<string> parts = [KeepPlanesWords(item) + ": " + item.Title];
        foreach (IGrouping<string, AircraftCheckPlane> other in item.Planes.GroupBy(p => p.TitleText, StringComparer.OrdinalIgnoreCase))
            parts.Add(PlaneNamesShort(item.Game, other.Select(p => p.Plane).ToList()) + ": " + other.Key);
        return string.Join(" · ", parts);
    }

    // "BTN 20 does different things in these aircraft: F/A-18C: RADAR Switch - EMERG · F-16C: KY-58 MODE Knob - RV.
    // Where should BTN 20 work?"
    public string RelationCheckText
    {
        get
        {
            AircraftCheckItem? first = RelationCheckItems.FirstOrDefault();
            if (first == null || first.Planes.Count == 0)
                return string.Empty;
            string button = RelationCheckButtonWords;
            if (RelationCheckItems.Count == 1)
                return T("{0} does different things in these aircraft: {1}. Where should {0} work?", button, MeaningsWords(first));
            return T("{0} does different things in the aircraft of {1} of its actions, e.g. {2}. Where should {0} work?", button, RelationCheckItems.Count, MeaningsWords(first));
        }
    }

    public string RelationCheckToggleText => RelationCheckExpanded ? T("Hide the list")
        : RelationCheckItems.Count > 1 ? T("Show what these commands do in the other {0} aircraft", RelationCheckPlaneCount)
        : T("Show what this command does in the other {0} aircraft", RelationCheckPlaneCount);

    // "Only in the F/A-18C (take it off the F-16C)"
    public string RelationCheckOnlyLabel => RelationCheckItems.Count == 1
        ? T("Only in the {0} (take it off the {1})", KeepPlanesWords(RelationCheckItems[0]), PlaneNamesShort(RelationCheckItems[0].Game, RelationCheckItems[0].Drop))
        : T("Only where it does the same (take it off {0} aircraft)", RelationCheckPlaneCount);

    // "Keep it in both (F-16C keeps KY-58 MODE Knob - RV)"
    public string RelationCheckKeepLabel
    {
        get
        {
            AircraftCheckItem? first = RelationCheckItems.FirstOrDefault();
            if (first == null || RelationCheckItems.Count > 1)
                return T("Keep it everywhere");
            List<IGrouping<string, AircraftCheckPlane>> others = first.Planes.GroupBy(p => p.TitleText, StringComparer.OrdinalIgnoreCase).ToList();
            if (others.Count != 1)
                return T("Keep it in all ({0} aircraft keep their own command)", first.Planes.Count);
            return T("Keep it in both ({0} keeps {1})", PlaneNamesShort(first.Game, others[0].Select(p => p.Plane).ToList()), others[0].Key);
        }
    }

    // The aircraft of the open relation's action where the command is the action's own meaning.
    private IReadOnlyList<string> OwnPlanesOf(AircraftCheckItem item)
    {
        RelationNodeItem? node = RelationNodes.FirstOrDefault(n => string.Equals(n.Id, item.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(n.Game, item.Game, StringComparison.OrdinalIgnoreCase));
        List<string> own = node?.Aircraft?.Items.Where(i => i.IsChecked && i.IsSame).Select(i => i.Plane).ToList() ?? [];
        if (own.Count == 0)
            own = node?.Aircraft?.Items.Where(i => i.IsChecked && !i.IsDiffers).Select(i => i.Plane).ToList() ?? [];
        return own;
    }

    [RelayCommand]
    private void ToggleRelationCheckExpanded() => RelationCheckExpanded = !RelationCheckExpanded;

    // The open relation's part of the check (after the check, a new relation, its aircraft read).
    internal void UpdateRelationCheck()
    {
        string relation = SelectedRelation?.Name ?? string.Empty;
        List<AircraftCheckItem> mine = AircraftCheckItems.Where(i => string.Equals(i.Relation, relation, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!mine.SequenceEqual(RelationCheckItems))
        {
            RelationCheckItems.Clear();
            foreach (AircraftCheckItem item in mine)
                RelationCheckItems.Add(item);
            RelationCheckExpanded = false;
        }
        OnPropertyChanged(nameof(HasAircraftCheckNotice));
        OnPropertyChanged(nameof(HasSeveralRelationCheckItems));
        OnPropertyChanged(nameof(RelationCheckText));
        OnPropertyChanged(nameof(RelationCheckToggleText));
        OnPropertyChanged(nameof(RelationCheckOnlyLabel));
        OnPropertyChanged(nameof(RelationCheckKeepLabel));
        OnPropertyChanged(nameof(HasProfileCheckLink));
        OnPropertyChanged(nameof(ProfileCheckLinkText));
    }

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

    private static string KeptKey(string relation, string game, string id, string plane) => relation + "|" + game + "|" + id + "|" + plane;

    private HashSet<string> KeptAircraft => new(ListPrefs.KeptAircraft, StringComparer.OrdinalIgnoreCase);

    internal void ApplyAircraftCheck(string output)
    {
        AircraftCheckItems.Clear();
        HashSet<string> kept = KeptAircraft;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("Found", out JsonElement found) && found.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement f in found.EnumerateArray())
                {
                    string relation = ReadJsonString(f, "Relation");
                    string game = ReadJsonString(f, "Game");
                    string id = ReadJsonString(f, "Id");
                    List<string> planes = ReadStringArray(f, "Drop").Where(p => !kept.Contains(KeptKey(relation, game, id, p))).ToList();
                    if (planes.Count == 0)
                        continue;
                    // What the command is called in each of them.
                    Dictionary<string, string> titles = new(StringComparer.OrdinalIgnoreCase);
                    if (f.TryGetProperty("Meanings", out JsonElement m) && m.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement v in m.EnumerateArray())
                        {
                            foreach (string plane in ReadStringArray(v, "Planes"))
                                titles.TryAdd(plane, ReadJsonString(v, "Title"));
                        }
                    }
                    AircraftCheckItems.Add(new AircraftCheckItem(relation, game, id, ReadJsonString(f, "Title"),
                        planes.Select(p => new AircraftCheckPlane(p, titles.TryGetValue(p, out string? t) ? t : string.Empty)).ToList())
                    {
                        // The aircraft with the action's own meaning (they keep it with "Only in the ...").
                        KeepPlanes = ReadStringArray(f, "Planes")
                    });
                }
            }
        }
        catch (JsonException)
        {
        }
        UpdateAircraftCheckTotals();
    }

    private void UpdateAircraftCheckTotals()
    {
        int nodes = AircraftCheckItems.Count;
        int relations = AircraftCheckItems.Select(i => i.Relation).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        AircraftCheckDrop = AircraftCheckItems.Sum(i => i.Planes.Count);
        AircraftCheckText = nodes == 0 ? string.Empty
            : T("{0} actions in {1} relations are also ticked for aircraft where their command does something else ({2} aircraft). Open one to decide, or switch them all off.", nodes, relations, AircraftCheckDrop);
        if (nodes == 0)
            ProfileCheckExpanded = false;
        UpdateRelationCheck();
    }

    // Remembers aircraft the user keeps in a relation's action: the check does not ask about them again.
    private void KeepAircraft(AircraftCheckItem item, IEnumerable<string> planes)
    {
        HashSet<string> kept = KeptAircraft;
        foreach (string plane in planes)
        {
            if (kept.Add(KeptKey(item.Relation, item.Game, item.Id, plane)))
                ListPrefs.KeptAircraft.Add(KeptKey(item.Relation, item.Game, item.Id, plane));
        }
    }

    // One action's aircraft switched off (one engine run, one undo step).
    private async Task<bool> SwitchOffAircraftAsync(AircraftCheckItem item, IReadOnlyCollection<string> planes)
    {
        if (planes.Count == 0)
            return true;
        EngineCommandResult? result = await RunEngineAsync(["relations", "aircraft", "set", item.Relation, "--node", item.Game + ":" + item.Id, "--off", string.Join("|", planes)],
            includeProfile: true, mutatesProfile: true, busyLabel: N("Switching off aircraft where the command does something else..."), forceText: false);
        return result != null && result.ExitCode == 0;
    }

    // "Only where it is 'Rudder'": the open relation's flagged aircraft off, except those ticked "use here" (kept).
    [RelayCommand]
    internal async Task OnlyWhereSameMeaningAsync()
    {
        string relation = SelectedRelation?.Name ?? string.Empty;
        List<AircraftCheckItem> items = RelationCheckItems.ToList();
        if (relation.Length == 0 || items.Count == 0)
            return;
        int off = 0;
        int steps = 0;
        foreach (AircraftCheckItem item in items)
        {
            List<string> drop = item.Planes.Where(p => !p.UseHere).Select(p => p.Plane).ToList();
            if (!await SwitchOffAircraftAsync(item, drop))
                break;
            off += drop.Count;
            if (drop.Count > 0)
                steps++;
            KeepAircraft(item, item.Planes.Where(p => p.UseHere).Select(p => p.Plane));
            AircraftCheckItems.Remove(item);
        }
        SaveListPreferences();
        UpdateAircraftCheckTotals();
        await ReloadBuilderRelationAsync(relation);
        // One engine run per action: several actions are several undo steps.
        StatusText = steps > 1
            ? T("'{0}': switched off in {1} aircraft where the command does something else. Ctrl+Z undoes it one action at a time ({2} steps).", relation, off, steps)
            : T("'{0}': switched off in {1} aircraft where the command does something else. Ctrl+Z undoes it.", relation, off);
    }

    // "Keep for all": the relation stays for these aircraft; not asked again.
    [RelayCommand]
    internal void KeepRelationCheckForAll()
    {
        string relation = SelectedRelation?.Name ?? string.Empty;
        foreach (AircraftCheckItem item in RelationCheckItems.ToList())
        {
            KeepAircraft(item, item.Planes.Select(p => p.Plane));
            AircraftCheckItems.Remove(item);
        }
        SaveListPreferences();
        UpdateAircraftCheckTotals();
        StatusText = T("'{0}' stays as it is in all its aircraft; JoyPro does not ask about it again on this PC.", relation);
    }

    // "Ask me later": hides the open relation's banner until the next start.
    [RelayCommand]
    private void DismissAircraftCheck()
    {
        askLaterRelations.Add(SelectedRelation?.Name ?? string.Empty);
        OnPropertyChanged(nameof(HasAircraftCheckNotice));
    }

    // "Check all relations (n actions)": the profile-wide check again, its list open (or closed).
    [RelayCommand]
    internal async Task ToggleProfileCheckAsync()
    {
        if (ProfileCheckExpanded)
        {
            ProfileCheckExpanded = false;
            return;
        }
        await RefreshAircraftCheckAsync();
        ProfileCheckExpanded = AircraftCheckItems.Count > 0;
    }

    // Switch every flagged aircraft of the profile off: one engine run (one undo step) unless the user keeps some, then
    // one per action.
    [RelayCommand]
    internal async Task RepairAircraftAsync()
    {
        int dropped = 0;
        int steps = 1;
        if (ListPrefs.KeptAircraft.Count == 0)
        {
            EngineCommandResult? result = await RunEngineAsync(["relations", "check-aircraft", "--apply"], includeProfile: true, mutatesProfile: true,
                busyLabel: N("Switching off aircraft where the command does something else..."), forceText: false);
            if (result == null || result.ExitCode != 0)
                return;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
                dropped = ReadJsonInt(doc.RootElement, "Dropped");
            }
            catch (JsonException)
            {
            }
            AircraftCheckItems.Clear();
        }
        else
        {
            steps = 0;
            foreach (AircraftCheckItem item in AircraftCheckItems.ToList())
            {
                if (!await SwitchOffAircraftAsync(item, item.Planes.Select(p => p.Plane).ToList()))
                    break;
                dropped += item.Planes.Count;
                if (item.Planes.Count > 0)
                    steps++;
                AircraftCheckItems.Remove(item);
            }
        }
        UpdateAircraftCheckTotals();
        if (SelectedRelation != null)
            await ReloadBuilderRelationAsync(SelectedRelation.Name);
        StatusText = steps > 1
            ? T("{0} aircraft switched off where the command does something else. Ctrl+Z undoes it one action at a time ({1} steps).", dropped, steps)
            : T("{0} aircraft switched off where the command does something else. Ctrl+Z undoes it.", dropped);
    }

    // Opens a relation of the check.
    [RelayCommand]
    private async Task OpenAircraftCheckRelationAsync(AircraftCheckItem? item)
    {
        if (item == null)
            return;
        RememberPlace();
        await OpenBuilderRelationAsync(item.Relation);
    }
}
