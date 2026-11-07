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
            node.SetFilterMatch(names, filter.FilteredPlaneTest(node.Game));
            ShowNodePlanesFilterFirst(node, filter);
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

    // The action's row names the filter's aircraft first ("DCS World · F-16C_50, F-100D · axis") and shows its picture
    // (2.6.4 tester report on "THROTTLE - THRUST SINGLE ENGINE - F-100D" under "DCS World: F-16C_50": "the order!").
    // Display only: ActivePlanesText, the relation and the export keep the relation's order. No filter: as always.
    private void ShowNodePlanesFilterFirst(RelationNodeItem node, ListFilterViewModel filter)
    {
        if (node.Planes.Count == 0)
            return;
        IReadOnlyList<string> ordered = filter.FilteredFirst(node.Game, node.Planes);
        node.ShowPlanesInOrder(ordered, BuildNodeListSummary(ordered), string.Join(Environment.NewLine, ordered));
        bool dcs = node.Game.Equals("DCS", StringComparison.OrdinalIgnoreCase);
        string? lead = dcs && !string.Equals(ordered[0], node.Planes[0], StringComparison.OrdinalIgnoreCase) ? ordered[0] : null;
        if (string.Equals(node.LeadPlane, lead, StringComparison.OrdinalIgnoreCase))
            return;
        node.LeadCover = null;
        node.LeadPlane = lead;
        if (lead != null)
            _ = LoadNodeLeadCoverAsync(node, lead);
    }

    private async System.Threading.Tasks.Task LoadNodeLeadCoverAsync(RelationNodeItem node, string plane)
    {
        Avalonia.Media.Imaging.Bitmap? picture = node.PlanePictures.FirstOrDefault(p => string.Equals(p.Name, plane, StringComparison.OrdinalIgnoreCase))?.Image
                                                 ?? await LoadAircraftPictureAsync(plane);
        if (string.Equals(node.LeadPlane, plane, StringComparison.OrdinalIgnoreCase))
            node.LeadCover = picture;
    }

    // An action of the folded others was picked (a search match line, the check): the fold opens so it can be seen.
    private void RevealRelationNode(RelationNodeItem? node)
    {
        if (node == null || !IsRelationNodeListGrouped || RelationNodeOthers.IsExpanded || node.IsFilterMatch || !RelationNodes.Contains(node))
            return;
        RelationNodeOthers.IsExpanded = true;
        RebuildRelationNodesShown();
    }
}
