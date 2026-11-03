using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// The relation editor's action list under the Relations page's Games filter (2.6.2 tester report: "DCS World: F-100D"
// filtered the list, but the open relation's actions still showed every aircraft). The relation stays complete (hiding
// actions would invite wrong edits): the actions for the filtered games / aircraft come first with an accent edge and the
// aircraft named ("Filter: F-100D", also in their aircraft line), the others below in a folded "Other aircraft (n)",
// which opens with a click. No filter: the list as always. The order of the actions in the relation does not change
// (RelationNodes keeps it; dragging and Alt+Up / Alt+Down still move them there).
public sealed partial class MainWindowViewModel
{
    // The list the editor shows: RelationNodes itself without a filter, else the filtered actions, the fold and (opened)
    // the others.
    public RangeObservableCollection<object> RelationNodesShown { get; } = [];

    public RelationNodeOthersHeader RelationNodeOthers { get; } = new();

    // The editor's action list groups by the filter (some actions are for it, some are not).
    public bool IsRelationNodeListGrouped { get; private set; }

    public IEnumerable RelationNodeListItems => IsRelationNodeListGrouped ? RelationNodesShown : RelationNodes;

    // How many of the open relation's actions are for the filter (0 without a filter).
    public int RelationNodeFilterMatchCount { get; private set; }

    private string relationNodeOthersRelation = string.Empty;

    [RelayCommand]
    private void ToggleRelationNodeOthers()
    {
        RelationNodeOthers.IsExpanded = !RelationNodeOthers.IsExpanded;
        RebuildRelationNodesShown();
    }

    // The filter, the open relation or its actions changed.
    internal void RebuildRelationNodesShown()
    {
        string relation = SelectedRelation?.Name ?? string.Empty;
        // Another relation (or the filter changed): the others start folded.
        if (!string.Equals(relation, relationNodeOthersRelation, StringComparison.Ordinal))
        {
            relationNodeOthersRelation = relation;
            RelationNodeOthers.IsExpanded = false;
        }
        ListFilterViewModel filter = RelationFilter;
        bool filtered = filter.HasGameFilter;
        List<RelationNodeItem> matches = [];
        List<RelationNodeItem> others = [];
        foreach (RelationNodeItem node in RelationNodes)
        {
            IReadOnlyList<string> names = filtered ? filter.GameFilterMatches(node.Game, NodePlanes(node)) : [];
            string game = node.Game;
            node.SetFilterMatch(names, filtered ? plane => filter.IsFilteredPlane(game, plane) : null);
            (names.Count > 0 ? matches : others).Add(node);
        }
        RelationNodeFilterMatchCount = matches.Count;
        // Grouped only when the filter splits the relation: all or none of its actions for it show as always (all marked).
        bool grouped = filtered && matches.Count > 0 && others.Count > 0;
        RelationNodeOthers.Kind = filter.HasOnlyPlaneFilter ? "aircraft" : filter.HasOnlyWholeGameFilter ? "games" : "both";
        RelationNodeOthers.Count = others.Count;
        if (grouped)
        {
            List<object> shown = [.. matches, RelationNodeOthers];
            if (RelationNodeOthers.IsExpanded)
                shown.AddRange(others);
            if (!shown.SequenceEqual(RelationNodesShown))
            {
                RelationNodeItem? selected = SelectedRelationNode;
                RelationNodesShown.ReplaceAll(shown);
                if (selected != null && shown.Contains(selected) && !ReferenceEquals(SelectedRelationNode, selected))
                    SelectedRelationNode = selected;
            }
        }
        else if (RelationNodesShown.Count > 0)
        {
            RelationNodesShown.Clear();
        }
        if (grouped != IsRelationNodeListGrouped)
        {
            IsRelationNodeListGrouped = grouped;
            OnPropertyChanged(nameof(IsRelationNodeListGrouped));
            OnPropertyChanged(nameof(RelationNodeListItems));
        }
        OnPropertyChanged(nameof(RelationNodeFilterMatchCount));
    }

    // The action's switched-on aircraft (DCS); another game's action is for the game itself.
    private static IEnumerable<string> NodePlanes(RelationNodeItem node) =>
        node.ActivePlanesText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // An action of the folded others was picked (a search match line, the check): the fold opens so it can be seen.
    private void RevealRelationNode(RelationNodeItem? node)
    {
        if (node == null || !IsRelationNodeListGrouped || RelationNodeOthers.IsExpanded || node.IsFilterMatch || !RelationNodes.Contains(node))
            return;
        RelationNodeOthers.IsExpanded = true;
        RebuildRelationNodesShown();
    }
}
