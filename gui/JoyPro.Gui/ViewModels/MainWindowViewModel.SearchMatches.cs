using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// What the search found in a relation's actions, under its card on the Relations and the Binds page (tester report on
// 2.4.2: "If I search for 'Master', 21 occurrences are found but it is impossible to find the one I want as only
// Relation names show and not the associated actions and modules"). Each line is a matching action title with the word
// marked and its game and aircraft ("Master Caution Button · DCS World · AH-64D (2)"); at most three, then "+N more".
// A click opens the relation with that action selected. Computed for the shown rows only, when the list is filtered.
public sealed partial class MainWindowViewModel
{
    internal const int MaxSearchMatchLines = 3;

    private void UpdateRelationSearchHits(IReadOnlyCollection<RelationListItem> shown)
    {
        bool active = RelationFilter.HasSearch && RelationFilter.SearchesActions;
        HashSet<RelationListItem> visible = active ? new HashSet<RelationListItem>(shown, ReferenceEqualityComparer.Instance) : [];
        foreach (RelationListItem relation in allRelations)
        {
            if (!active || !visible.Contains(relation))
            {
                relation.SearchHits.Clear();
                continue;
            }
            List<SearchMatchLine> lines = BuildSearchMatchLines(relation, RelationFilter.SearchWords, out int more);
            relation.SearchHits.Set(lines, more);
        }
    }

    private void UpdateBindSearchHits(IReadOnlyCollection<BindListItem> shown)
    {
        bool active = BindFilter.HasSearch && BindFilter.SearchesActions;
        Dictionary<string, RelationListItem> byName = new(StringComparer.OrdinalIgnoreCase);
        if (active)
        {
            foreach (RelationListItem relation in allRelations)
                byName.TryAdd(relation.Name, relation);
        }
        HashSet<BindListItem> visible = active ? new HashSet<BindListItem>(shown, ReferenceEqualityComparer.Instance) : [];
        foreach (BindListItem row in bindRows)
        {
            if (!active || !visible.Contains(row) || !byName.TryGetValue(row.RelationName, out RelationListItem? relation))
            {
                row.SearchHits.Clear();
                continue;
            }
            List<SearchMatchLine> lines = BuildSearchMatchLines(relation, BindFilter.SearchWords, out int more);
            row.SearchHits.Set(lines, more);
        }
    }

    // The titles that have a searched word: the ones with the whole search first, then the ones with more of its words;
    // the same title in one game is one line with all its aircraft.
    internal List<SearchMatchLine> BuildSearchMatchLines(RelationListItem relation, IReadOnlyList<string> words, out int more)
    {
        more = 0;
        if (words.Count == 0 || relation.ActionTitleEntries.Count == 0)
            return [];
        string whole = string.Join(' ', words);
        List<(ActionTitleEntry Entry, List<string> Planes, int Score, int Order)> found = [];
        Dictionary<string, int> byKey = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < relation.ActionTitleEntries.Count; i++)
        {
            ActionTitleEntry entry = relation.ActionTitleEntries[i];
            string lower = entry.Title.ToLowerInvariant();
            int score = 0;
            foreach (string word in words)
            {
                if (lower.Contains(word, StringComparison.Ordinal))
                    score++;
            }
            if (score == 0)
                continue;
            if (words.Count > 1 && lower.Contains(whole, StringComparison.Ordinal))
                score += words.Count + 1;
            string key = entry.Game + "|" + entry.Title;
            if (byKey.TryGetValue(key, out int at))
            {
                foreach (string plane in entry.Planes)
                {
                    if (!found[at].Planes.Contains(plane, StringComparer.OrdinalIgnoreCase))
                        found[at].Planes.Add(plane);
                }
                continue;
            }
            byKey[key] = found.Count;
            found.Add((entry, entry.Planes.ToList(), score, i));
        }
        if (found.Count == 0)
            return [];
        List<SearchMatchLine> lines = [];
        foreach ((ActionTitleEntry entry, List<string> planes, _, _) in found.OrderByDescending(f => f.Score).ThenBy(f => f.Order).Take(MaxSearchMatchLines))
        {
            (string before, string hit, string after) = MarkSearchHit(entry.Title, words, whole);
            lines.Add(new SearchMatchLine(relation.Name, before, hit, after, SearchMatchWhere(entry.Game, planes), entry.Game, entry.Node, entry.Id));
        }
        more = found.Count - lines.Count;
        return lines;
    }

    // The title split around the whole search, else around the first searched word in it.
    internal static (string Before, string Hit, string After) MarkSearchHit(string title, IReadOnlyList<string> words, string whole)
    {
        int at = title.IndexOf(whole, StringComparison.OrdinalIgnoreCase);
        int length = whole.Length;
        if (at < 0)
        {
            length = 0;
            foreach (string word in words)
            {
                int index = title.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && (at < 0 || index < at))
                {
                    at = index;
                    length = word.Length;
                }
            }
        }
        if (at < 0)
            return (title, string.Empty, string.Empty);
        return (title[..at], title.Substring(at, length), title[(at + length)..]);
    }

    // "DCS World · AH-64D BLK.II (2)", "DCS World · F-16C +2 more", "Microsoft Flight Simulator 2024".
    internal string SearchMatchWhere(string game, IReadOnlyList<string> planes)
    {
        string gameName = DisplayText.Game(game.Length == 0 ? "DCS" : game);
        if (!(game.Length == 0 || game.Equals("DCS", StringComparison.OrdinalIgnoreCase)) || planes.Count == 0)
            return gameName;
        // The module's readable name (as the Move window and the check name it); an id without one reads without underscores.
        List<string> modules = planes.Select(p => ReadableModule(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string aircraft = modules.Count == 1
            ? (planes.Count > 1 ? modules[0] + " (" + planes.Count + ")" : modules[0])
            : T("{0} +{1} more", modules[0], modules.Count - 1);
        return gameName + "  ·  " + aircraft;
    }

    private static string ReadableModule(string plane) => NamedSeat(plane).Module.Replace('_', ' ').Trim();

    // A match line was clicked: the relation opens with that action selected (and scrolled into view); on the Binds
    // page its row is selected and the peek shows the action marked.
    [RelayCommand]
    internal async Task OpenSearchMatchAsync(SearchMatchLine? line)
    {
        if (line == null || line.Relation.Length == 0)
            return;
        if (CurrentStage == UiStage.Binds)
        {
            BindListItem? row = Binds.FirstOrDefault(b => string.Equals(b.RelationName, line.Relation, StringComparison.OrdinalIgnoreCase));
            if (row == null)
                return;
            SelectedBind = row;
            IsBindPeekOpen = true;
            await LoadBindPeekAsync(row.RelationName);
            foreach (BindPeekAction action in BindPeekGroups.SelectMany(g => g.Actions))
            {
                bool hit = string.Equals(action.Id, line.Id, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(action.Game, line.Game, StringComparison.OrdinalIgnoreCase);
                action.IsSearchHit = hit;
                if (hit)
                    action.IsExpanded = true;
            }
            return;
        }
        RelationListItem? card = Relations.FirstOrDefault(r => string.Equals(r.Name, line.Relation, StringComparison.Ordinal)) ??
                                 Relations.FirstOrDefault(r => string.Equals(r.Name, line.Relation, StringComparison.OrdinalIgnoreCase));
        if (card == null)
            return;
        if (!ReferenceEquals(SelectedRelation, card) || RelationNodes.Count == 0)
        {
            suppressRelationAutoLoad = true;
            try
            {
                SelectedRelation = card;
            }
            finally
            {
                suppressRelationAutoLoad = false;
            }
            await ShowRelationAsync(card.Name);
        }
        RelationNodeItem? node = RelationNodes.FirstOrDefault(n => string.Equals(n.Id, line.Id, StringComparison.OrdinalIgnoreCase) &&
                                                                   string.Equals(n.Game, line.Game, StringComparison.OrdinalIgnoreCase))
                                 ?? (line.Node >= 0 && line.Node < RelationNodes.Count ? RelationNodes[line.Node] : null);
        foreach (RelationNodeItem other in RelationNodes)
            other.IsSearchHit = ReferenceEquals(other, node);
        if (node != null)
        {
            SelectedRelationNode = node;
            RelationNodeScrollRequested?.Invoke(this, node);
        }
    }

    // The view scrolls the relation editor's action list to the action (a match line was clicked).
    public event EventHandler<RelationNodeItem>? RelationNodeScrollRequested;
}
