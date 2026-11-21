using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Logging;

namespace JoyPro.Gui.ViewModels;

// Several actions at once (docs/relation-multi-select.md): in the relation editor Ctrl/Shift+click (or Ctrl+A, Select
// all) picks actions; then Delete, Copy, Paste (into another relation) and "Move to..." a new relation (named here, the
// kind of the source) or an existing relation of the same kind (a searchable list). In the Relations list several
// relations' actions move the same way ("Move their actions to..."). Every operation is one engine call, so one profile
// version and one Ctrl+Z: relations item remove / copy-to / move-to, relations mass move-actions.
public sealed partial class MainWindowViewModel
{
    // ---- The editor's selection (set by the list, MainWindow.axaml.cs RelationNodeListSelectionChanged).

    private readonly List<RelationNodeItem> selectedRelationNodes = [];

    public int RelationNodeSelectionCount => selectedRelationNodes.Count;

    public bool HasRelationNodeMultiSelection => selectedRelationNodes.Count > 1;

    public string RelationNodeSelectionSummary => T("{0} actions selected", selectedRelationNodes.Count);

    // The view selects every action of the list (Ctrl+A, Select all) or none (Clear).
    public event EventHandler? RelationNodeSelectAllRequested;

    public event EventHandler? RelationNodeSelectionClearRequested;

    public void SetRelationNodeSelection(IEnumerable<RelationNodeItem> nodes)
    {
        selectedRelationNodes.Clear();
        selectedRelationNodes.AddRange(nodes.Where(n => n != null).Distinct());
        OnPropertyChanged(nameof(RelationNodeSelectionCount));
        OnPropertyChanged(nameof(HasRelationNodeMultiSelection));
        OnPropertyChanged(nameof(RelationNodeSelectionSummary));
    }

    [RelayCommand]
    private void SelectAllRelationNodes() => RelationNodeSelectAllRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ClearRelationNodeSelection() => RelationNodeSelectionClearRequested?.Invoke(this, EventArgs.Empty);

    // The actions an operation acts on: the right-clicked one when it is not part of the selection, else the selection
    // (or the one selected action).
    private List<RelationNodeItem> RelationNodesFor(RelationNodeItem? clicked)
    {
        if (clicked != null && !selectedRelationNodes.Contains(clicked))
            return [clicked];
        if (selectedRelationNodes.Count > 0)
            return RelationNodes.Where(selectedRelationNodes.Contains).ToList();
        return SelectedRelationNode != null ? [SelectedRelationNode] : [];
    }

    // The whole action (every aircraft, switched-off ones too): "game:id".
    private static string WholeNodeSpec(RelationNodeItem node) => BuildNodeSpec(node.Game, node.Id, string.Empty);

    private static string NodeTitle(RelationNodeItem node) =>
        node.TitlesSummary.Length > 0 && !node.TitlesSummary.StartsWith('<') ? node.TitlesSummary : node.Id;

    // ---- Delete

    [RelayCommand]
    private async Task DeleteSelectedRelationNodesAsync(RelationNodeItem? clicked)
    {
        string relation = SelectedRelation?.Name ?? string.Empty;
        List<RelationNodeItem> nodes = RelationNodesFor(clicked);
        if (relation.Length == 0 || nodes.Count == 0)
            return;
        if (nodes.Count == 1)
        {
            await RemoveNodeAsync(nodes[0]);
            return;
        }
        if (!await ConfirmAsync(T("Remove {0} actions?", nodes.Count),
                T("{0} leave the relation '{1}'. Its bind stays with the relation. Ctrl+Z undoes it.", JoinTitles(nodes), relation), T("Remove")))
            return;
        List<string> args = ["relations", "item", "remove", relation];
        foreach (RelationNodeItem node in nodes)
            args.AddRange(["--node", WholeNodeSpec(node)]);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Removing actions..."),
            historyNote: N("Remove actions"));
        if (result?.ExitCode != 0)
            return;
        await ShowRelationAsync(relation);
        StatusText = T("Removed {0} actions from '{1}'. Ctrl+Z undoes it.", nodes.Count, relation);
    }

    private static string JoinTitles(IReadOnlyList<RelationNodeItem> nodes) =>
        string.Join(", ", nodes.Take(6).Select(n => "'" + NodeTitle(n) + "'")) + (nodes.Count > 6 ? " (+" + (nodes.Count - 6) + ")" : string.Empty);

    // ---- Copy / Paste: the actions are remembered with their relation; Paste copies them from there (engine
    // relations item copy-to, with their aircraft and which of them are switched off).

    private string copiedNodesRelation = string.Empty;
    private bool copiedNodesAreAxis;
    private readonly List<(string Spec, string Title)> copiedNodes = [];

    public bool HasCopiedRelationNodes => copiedNodes.Count > 0;

    // Paste into the open relation: another relation than the copied actions' one.
    public bool CanPasteRelationNodes => HasCopiedRelationNodes && SelectedRelation != null &&
                                         !string.Equals(SelectedRelation.Name, copiedNodesRelation, StringComparison.OrdinalIgnoreCase);

    public string PasteRelationNodesLabel => T("Paste {0} action(s)", copiedNodes.Count);

    public string PasteRelationNodesTooltip => T("Copies the actions copied from '{0}' into this relation, with their aircraft (Ctrl+V; Ctrl+Z undoes it)", copiedNodesRelation);

    private void RaisePasteState()
    {
        OnPropertyChanged(nameof(HasCopiedRelationNodes));
        OnPropertyChanged(nameof(CanPasteRelationNodes));
        OnPropertyChanged(nameof(PasteRelationNodesLabel));
        OnPropertyChanged(nameof(PasteRelationNodesTooltip));
    }

    [RelayCommand]
    private void CopySelectedRelationNodes(RelationNodeItem? clicked)
    {
        string relation = SelectedRelation?.Name ?? string.Empty;
        List<RelationNodeItem> nodes = RelationNodesFor(clicked);
        if (relation.Length == 0 || nodes.Count == 0)
            return;
        copiedNodes.Clear();
        copiedNodes.AddRange(nodes.Select(n => (WholeNodeSpec(n), NodeTitle(n))));
        copiedNodesRelation = relation;
        copiedNodesAreAxis = SelectedRelation!.IsAxis;
        RaisePasteState();
        StatusText = T("Copied {0} action(s) of '{1}': open another relation and press Paste (Ctrl+V).", nodes.Count, relation);
    }

    [RelayCommand]
    private async Task PasteRelationNodesAsync()
    {
        RelationListItem? target = SelectedRelation;
        if (target == null || !HasCopiedRelationNodes)
            return;
        if (!CanPasteRelationNodes)
        {
            StatusText = T("Open another relation to paste the actions into.");
            return;
        }
        if (target.IsAxis != copiedNodesAreAxis)
        {
            StatusText = copiedNodesAreAxis
                ? T("The copied actions are axis actions: paste them into an axis relation.")
                : T("The copied actions are button actions: paste them into a button relation.");
            return;
        }
        List<string> args = ["relations", "item", "copy-to", copiedNodesRelation];
        foreach ((string spec, _) in copiedNodes)
            args.AddRange(["--node", spec]);
        args.AddRange(["--to", target.Name]);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Pasting actions..."),
            historyNote: N("Paste actions"));
        if (result?.ExitCode != 0)
            return;
        string name = target.Name;
        await RefreshRelationsAsync();
        SelectRelationByName(name);
        await ShowRelationAsync(name);
        StatusText = T("Pasted {0} action(s) into '{1}'. Ctrl+Z undoes it.", copiedNodes.Count, name);
    }

    // ---- Move to...: a new relation (its name asked here, the kind of the source) or an existing one of the same kind.

    [ObservableProperty]
    private bool isMoveNodesOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyMoveNodes))]
    [NotifyPropertyChangedFor(nameof(IsMoveNodesToExisting))]
    private bool isMoveNodesToNew = true;

    public bool IsMoveNodesToExisting
    {
        get => !IsMoveNodesToNew;
        set => IsMoveNodesToNew = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyMoveNodes))]
    private string moveNodesNewName = string.Empty;

    [ObservableProperty]
    private string moveNodesSearch = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyMoveNodes))]
    private string? selectedMoveNodesTarget;

    [ObservableProperty]
    private string moveNodesTitle = string.Empty;

    [ObservableProperty]
    private string moveNodesIntro = string.Empty;

    [ObservableProperty]
    private string moveNodesStatus = string.Empty;

    // The relations of the same kind (without the sources), filtered by the search.
    public ObservableCollection<string> MoveNodesTargets { get; } = [];

    public bool HasNoMoveNodesTargets => MoveNodesTargets.Count == 0;

    public bool HasMoveNodesStatus => MoveNodesStatus.Length > 0;

    public bool CanApplyMoveNodes => IsMoveNodesToNew ? MoveNodesNewName.Trim().Length > 0 : !string.IsNullOrWhiteSpace(SelectedMoveNodesTarget);

    // What is moved: actions of one relation (editor) or every action of several relations (Relations list).
    private string moveNodesSource = string.Empty;
    private readonly List<string> moveNodesSpecs = [];
    private readonly List<string> moveNodesSourceRelations = [];
    private bool moveNodesAreAxis;

    partial void OnMoveNodesSearchChanged(string value)
    {
        ForgetMoveNodesPressOnSearch();
        RebuildMoveNodesTargets();
    }

    partial void OnMoveNodesStatusChanged(string value) => OnPropertyChanged(nameof(HasMoveNodesStatus));

    partial void OnMoveNodesNewNameChanged(string value) => MoveNodesStatus = string.Empty;

    // The editor: the selected actions (or the right-clicked one).
    [RelayCommand]
    private void OpenMoveRelationNodes(RelationNodeItem? clicked)
    {
        RelationListItem? relation = SelectedRelation;
        List<RelationNodeItem> nodes = RelationNodesFor(clicked);
        if (relation == null || nodes.Count == 0)
            return;
        moveNodesSource = relation.Name;
        moveNodesSourceRelations.Clear();
        moveNodesSourceRelations.Add(relation.Name);
        moveNodesSpecs.Clear();
        moveNodesSpecs.AddRange(nodes.Select(WholeNodeSpec));
        moveNodesAreAxis = relation.IsAxis;
        MoveNodesTitle = T("Move {0} action(s) to...", nodes.Count);
        MoveNodesIntro = T("{0} leave '{1}' and go, with their aircraft, to:", JoinTitles(nodes), relation.Name);
        OpenMoveNodesPanel(relation.Name);
    }

    // The Relations list: every action of the selected relations.
    [RelayCommand]
    private void OpenMoveRelationsActions()
    {
        List<RelationListItem> relations = allRelations.Where(r => relationMassSelectedNames.Contains(r.Name)).ToList();
        if (relations.Count == 0)
        {
            StatusText = T("Select one or more relations first.");
            return;
        }
        if (relations.Select(r => r.IsAxis).Distinct().Count() > 1)
        {
            StatusText = T("Pick relations of one kind (all axis or all button) to move their actions together.");
            return;
        }
        moveNodesSource = string.Empty;
        moveNodesSpecs.Clear();
        moveNodesSourceRelations.Clear();
        moveNodesSourceRelations.AddRange(relations.Select(r => r.Name));
        moveNodesAreAxis = relations[0].IsAxis;
        MoveNodesTitle = T("Move the actions of {0} relation(s) to...", relations.Count);
        MoveNodesIntro = T("Every action of {0} goes, with its aircraft, to the relation below. The emptied relations are deleted with their binds.",
            string.Join(", ", relations.Take(6).Select(r => "'" + r.Name + "'")) + (relations.Count > 6 ? " (+" + (relations.Count - 6) + ")" : string.Empty));
        OpenMoveNodesPanel(relations[0].Name);
    }

    // The new relation's name starts as "<source> (2)" (the next free number).
    private void OpenMoveNodesPanel(string baseName)
    {
        MoveNodesStatus = string.Empty;
        ResetMoveNodesPress();
        MoveNodesSearch = string.Empty;
        SelectedMoveNodesTarget = null;
        string name = baseName + " (2)";
        for (int i = 3; allRelations.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
            name = baseName + " (" + i + ")";
        MoveNodesNewName = name;
        RebuildMoveNodesTargets();
        IsMoveNodesToNew = true;
        IsMoveNodesOpen = true;
        JoyProLog.Info("MoveNodes", "Opened for " + string.Join(", ", moveNodesSourceRelations) + " (" + moveNodesSpecs.Count + " action(s))");
    }

    private void RebuildMoveNodesTargets()
    {
        string keep = SelectedMoveNodesTarget ?? string.Empty;
        string[] words = (MoveNodesSearch ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<string> names = allRelations
            .Where(r => r.IsAxis == moveNodesAreAxis && !moveNodesSourceRelations.Contains(r.Name, StringComparer.OrdinalIgnoreCase))
            .Where(r => moveNodesPressed == null || moveNodesPressed.Contains(r.Name, StringComparer.OrdinalIgnoreCase))
            .Select(r => r.Name)
            .Where(n => words.All(w => n.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        MoveNodesTargets.Clear();
        foreach (string name in names)
            MoveNodesTargets.Add(name);
        SelectedMoveNodesTarget = names.FirstOrDefault(n => string.Equals(n, keep, StringComparison.OrdinalIgnoreCase)) ?? (names.Count == 1 ? names[0] : null);
        OnPropertyChanged(nameof(HasNoMoveNodesTargets));
    }

    [RelayCommand]
    private void CancelMoveNodes() => IsMoveNodesOpen = false;

    [RelayCommand]
    private async Task ApplyMoveNodesAsync()
    {
        if (!CanApplyMoveNodes)
            return;
        string newName = MoveNodesNewName.Trim();
        if (IsMoveNodesToNew && allRelations.Any(r => string.Equals(r.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            MoveNodesStatus = T("A relation named '{0}' already exists: pick it under Existing relation or type another name.", newName);
            return;
        }
        string target = IsMoveNodesToNew ? newName : SelectedMoveNodesTarget ?? string.Empty;
        List<string> args;
        if (moveNodesSource.Length > 0)
        {
            args = ["relations", "item", "move-to", moveNodesSource];
            foreach (string spec in moveNodesSpecs)
                args.AddRange(["--node", spec]);
        }
        else
        {
            args = ["relations", "mass", "move-actions", "--relations", string.Join(",", moveNodesSourceRelations), "--delete-empty"];
        }
        args.AddRange(IsMoveNodesToNew ? ["--new", newName] : ["--to", target]);
        List<PendingModifierDefinition> modifiersBefore = SnapshotProfileModifiers();
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Moving actions..."),
            historyNote: N("Move actions"));
        if (result?.ExitCode != 0)
        {
            MoveNodesStatus = (result?.StandardError ?? string.Empty).Trim() is { Length: > 0 } error ? Localization.Loc.TranslateLines(error) : T("The move did not work (see output).");
            return;
        }
        IsMoveNodesOpen = false;
        int moved = moveNodesSource.Length > 0 ? moveNodesSpecs.Count : moveNodesSourceRelations.Count;
        bool fromEditor = moveNodesSource.Length > 0;
        string source = moveNodesSource;
        await RefreshRelationsAsync();
        if (!fromEditor)
            await RefreshAndKeepModifiersAsync(modifiersBefore);
        // The editor stays on the source (what is left of it); from the list the target opens.
        string open = fromEditor && allRelations.Any(r => string.Equals(r.Name, source, StringComparison.OrdinalIgnoreCase)) ? source : target;
        SelectRelationByName(open);
        await ShowRelationAsync(open);
        StatusText = fromEditor
            ? T("Moved {0} action(s) to '{1}'. Ctrl+Z undoes it.", moved, target)
            : T("Moved the actions of {0} relation(s) to '{1}'. Ctrl+Z undoes it.", moved, target);
    }
}
