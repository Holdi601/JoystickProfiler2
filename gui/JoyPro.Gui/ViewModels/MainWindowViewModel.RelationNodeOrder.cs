using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// The order of the open relation's actions (docs/relation-builder.md "Order of the actions"): dragged by the grip of an
// action card, or Alt+Up / Alt+Down (and the card's context menu) for the keyboard. The list moves at once; the engine
// keeps the order in the profile (relations item move ... --to <position>), one undo step per move.
public sealed partial class MainWindowViewModel
{
    [RelayCommand]
    private Task MoveRelationNodeUpAsync(RelationNodeItem? node) => MoveRelationNodeByAsync(node, -1);

    [RelayCommand]
    private Task MoveRelationNodeDownAsync(RelationNodeItem? node) => MoveRelationNodeByAsync(node, 1);

    [RelayCommand]
    private Task MoveRelationNodeToTopAsync(RelationNodeItem? node) => MoveRelationNodeAsync(node ?? SelectedRelationNode, 0);

    [RelayCommand]
    private Task MoveRelationNodeToBottomAsync(RelationNodeItem? node) => MoveRelationNodeAsync(node ?? SelectedRelationNode, RelationNodes.Count - 1);

    private Task MoveRelationNodeByAsync(RelationNodeItem? node, int step)
    {
        node ??= SelectedRelationNode;
        int from = node == null ? -1 : RelationNodes.IndexOf(node);
        return from < 0 ? Task.CompletedTask : MoveRelationNodeAsync(node, from + step);
    }

    // Moves the action to the place `target` (0 = first) of the open relation. False when nothing moved or the engine
    // refused (the list then shows the relation's real order again).
    internal async Task<bool> MoveRelationNodeAsync(RelationNodeItem? node, int target)
    {
        RelationListItem? relation = SelectedRelation;
        if (relation == null || node == null)
            return false;
        int from = RelationNodes.IndexOf(node);
        if (from < 0)
            return false;
        target = Math.Max(0, Math.Min(RelationNodes.Count - 1, target));
        if (target == from)
            return false;
        RelationNodes.Move(from, target);
        SelectedRelationNode = node;
        EngineCommandResult? result = await RunEngineAsync(
            ["relations", "item", "move", relation.Name, "--node", BuildNodeSpec(node.Game, node.Id, string.Empty), "--to", (target + 1).ToString(CultureInfo.InvariantCulture)],
            includeProfile: true, mutatesProfile: true, busyLabel: N("Moving the action..."));
        if (result == null || result.ExitCode != 0)
        {
            await ShowRelationAsync(relation.Name);
            return false;
        }
        string what = node.TitlesSummary.Length > 0 && !node.TitlesSummary.StartsWith('<') ? node.TitlesSummary : node.Id;
        StatusText = T("'{0}' is now action {1} of {2}. Ctrl+Z undoes it.", what, (target + 1).ToString(CultureInfo.InvariantCulture),
            RelationNodes.Count.ToString(CultureInfo.InvariantCulture));
        return true;
    }
}
