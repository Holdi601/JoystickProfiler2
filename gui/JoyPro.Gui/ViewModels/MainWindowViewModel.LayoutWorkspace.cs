using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// The Layout page: "Device pictures" (one device's picture with its markers in three modes: View, Edit markers, Bind)
// and "Cheat sheets" (MainWindowViewModel.CheatSheets.cs). The picture is the device reference or the device's layout
// file (MainWindowViewModel.LayoutReference.cs); the picture canvas (DeviceMarkerCanvas) binds the Workspace*
// properties and LayoutCanvasMode and reports moves and clicks to LayoutCanvasMarkerMovedCommand /
// LayoutCanvasClickedCommand (contract: docs/ui/layout-redesign.md). Marker moves and edits can be undone (Ctrl+Z) and
// redone (Ctrl+Y) until the next load, save or picture switch.
public sealed partial class MainWindowViewModel
{
    // A relation choice that means "nothing": frees the input.
    public const string NoRelation = "(none)";

    private const int LayoutUndoLimit = 200;
    private readonly List<LayoutEditSnapshot> layoutUndo = [];
    private readonly List<LayoutEditSnapshot> layoutRedo = [];

    public ObservableCollection<LayoutEditorMarkerItem> LayoutLegendItems { get; } = [];

    public ObservableCollection<string> LayoutBindCandidates { get; } = [];

    [ObservableProperty]
    private bool isCheatSheetsSection;

    [ObservableProperty]
    private DeviceCanvasMode layoutCanvasMode = DeviceCanvasMode.View;

    [ObservableProperty]
    private bool layoutSnapEnabled = true;

    [ObservableProperty]
    private bool layoutShowGrid;

    [ObservableProperty]
    private bool layoutShowOverlays;

    [ObservableProperty]
    private bool layoutEditorHasUnsavedChanges;

    [ObservableProperty]
    private string layoutLegendFilter = string.Empty;

    [ObservableProperty]
    private string layoutBindSearch = string.Empty;

    [ObservableProperty]
    private string? selectedLayoutBindCandidate;

    public bool IsDevicePicturesSection => !IsCheatSheetsSection;

    public bool HasNoLayoutSuggestions => LayoutSuggestions.Count == 0;

    public bool IsLayoutViewMode => LayoutCanvasMode == DeviceCanvasMode.View;

    public bool IsLayoutEditMode => LayoutCanvasMode == DeviceCanvasMode.EditMarkers;

    public bool IsLayoutBindMode => LayoutCanvasMode == DeviceCanvasMode.Bind;

    // The layout file's own tools (place marker, tables and lines, the marker fields) in Edit mode.
    public bool IsLayoutFileEditMode => IsLayoutEditMode && LayoutShowsLayoutFile;

    // The reference's own tools (photo, reset, assistant, definitions) in Edit mode.
    public bool IsReferenceEditMode => IsLayoutEditMode && LayoutShowsReference;

    // On the callout sheet the sheet's own edits (tables, lines, spots) are undone.
    private bool UndoesCallouts => LayoutShowsCallouts && LayoutCallouts.CanUndo;

    private bool RedoesCallouts => LayoutShowsCallouts && LayoutCallouts.CanRedo;

    public bool CanUndoLayoutEdit => UndoesCallouts || layoutUndo.Count > 0;

    public bool CanRedoLayoutEdit => RedoesCallouts || layoutRedo.Count > 0;

    public string LayoutUndoTip => UndoesCallouts ? LayoutCallouts.UndoTip : layoutUndo.Count > 0 ? T("Undo: {0} (Ctrl+Z)", layoutUndo[^1].Description) : T("Nothing to undo");

    public string LayoutRedoTip => RedoesCallouts ? LayoutCallouts.RedoTip : layoutRedo.Count > 0 ? T("Redo: {0} (Ctrl+Y)", layoutRedo[^1].Description) : T("Nothing to redo");

    public bool HasLayoutWorkspacePicture => LayoutShowsLayoutFile
        ? LayoutEditorPreviewImage != null || LayoutEditorMarkers.Count > 0
        : deviceReference?.HasModel == true;

    public bool HasNoLayoutWorkspacePicture => !HasLayoutWorkspacePicture;

    // The empty picture area (no device picked yet).
    public string LayoutEmptyTitle => T("Pick a device on the left");

    public string LayoutEmptyHint =>
        T("Its picture shows up here at once (your photo, the maker's photo or JoyPro's drawing) with a marker on every button and axis and what each one does.");

    public bool HasSelectedLayoutMarker => SelectedLayoutEditorMarker != null;

    public bool HasNoSelectedLayoutMarker => SelectedLayoutEditorMarker == null;

    public string LayoutWorkspaceDeviceTitle
    {
        get
        {
            string device = ResolveLayoutEditorContextDevice();
            return device.Length == 0 ? T("No device picked") : DeviceDisplayName(device);
        }
    }

    public string LayoutWorkspaceTemplateName =>
        string.IsNullOrWhiteSpace(LayoutEditorSourcePath) ? string.Empty : Path.GetFileNameWithoutExtension(LayoutEditorSourcePath.Trim().Trim('"'));

    public string LayoutLegendSummary
    {
        get
        {
            if (LayoutShowsReference)
                return ReferenceLegendSummary();
            int bound = LayoutEditorMarkers.Count(m => m.IsBound);
            return LayoutEditorMarkers.Count == 0
                ? T("No markers yet")
                : T("{0} of {1} inputs bound", bound.ToString(CultureInfo.InvariantCulture), LayoutEditorMarkers.Count.ToString(CultureInfo.InvariantCulture));
        }
    }

    // "Throttle - HOTAS Warthog {GUID}" -> "Throttle - HOTAS Warthog".
    public static string DeviceDisplayName(string? device) => DeviceMarkerText.DeviceName(device);

    private void InitializeLayoutWorkspace()
    {
        LayoutEditorMarkers.CollectionChanged += (_, _) =>
        {
            RebuildLayoutLegend();
            OnPropertyChanged(nameof(HasLayoutWorkspacePicture));
            OnPropertyChanged(nameof(HasNoLayoutWorkspacePicture));
        };
        LayoutSuggestions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoLayoutSuggestions));
        PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(LayoutEditorPreviewImage):
                case nameof(LayoutEditorCanvasWidth):
                case nameof(LayoutEditorCanvasHeight):
                case nameof(LayoutEditorSourcePath):
                    if (LayoutShowsLayoutFile)
                        NotifyWorkspacePicture();
                    if (e.PropertyName is nameof(LayoutEditorPreviewImage) or nameof(LayoutEditorSourcePath))
                        RebuildWorkspacePictures();
                    break;
                case nameof(LayoutEditorStatus):
                    OnPropertyChanged(nameof(WorkspaceStatus));
                    break;
                case nameof(LayoutEditorHasUnsavedChanges):
                    OnPropertyChanged(nameof(WorkspaceHasUnsavedChanges));
                    break;
                case nameof(CanSaveLayoutEditorTemplate):
                    OnPropertyChanged(nameof(CanSaveWorkspace));
                    break;
                case nameof(LayoutShowsLayoutFile):
                case nameof(LayoutCanvasMode):
                    OnPropertyChanged(nameof(IsLayoutFileEditMode));
                    OnPropertyChanged(nameof(IsReferenceEditMode));
                    break;
            }
        };
    }

    partial void OnIsCheatSheetsSectionChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDevicePicturesSection));
        UpdateLiveInputMonitor();
        if (value)
            RefreshCheatSheetPreviewsFromFolder();
    }

    [RelayCommand]
    private void ShowDevicePicturesSection() => IsCheatSheetsSection = false;

    [RelayCommand]
    private void ShowCheatSheetsSection() => IsCheatSheetsSection = true;

    partial void OnLayoutCanvasModeChanged(DeviceCanvasMode value)
    {
        OnPropertyChanged(nameof(IsLayoutViewMode));
        OnPropertyChanged(nameof(IsLayoutEditMode));
        OnPropertyChanged(nameof(IsLayoutBindMode));
        OnPropertyChanged(nameof(ShowWorkspace3D));
        if (value != DeviceCanvasMode.EditMarkers)
        {
            LayoutPlaceMarkerMode = false;
            LayoutPlaceTableMode = false;
            LayoutPlaceConnectorSourceMode = false;
            LayoutPlaceConnectorPointMode = false;
        }
        if (deviceReference != null)
            deviceReference.IsEditable = value == DeviceCanvasMode.EditMarkers;
        OnPropertyChanged(nameof(IsCalloutEditMode));
        OnPropertyChanged(nameof(ShowCalloutPreviewBox));
        LayoutCallouts.Refresh();
        if (value == DeviceCanvasMode.Bind)
            RebuildLayoutBindCandidates();
    }

    // "view", "edit" or "bind" (the mode buttons and the keys V, E, B).
    [RelayCommand]
    private void SetLayoutCanvasMode(string? mode)
    {
        LayoutCanvasMode = (mode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "edit" or "editmarkers" => DeviceCanvasMode.EditMarkers,
            "bind" => DeviceCanvasMode.Bind,
            _ => DeviceCanvasMode.View
        };
    }

    partial void OnLayoutLegendFilterChanged(string value)
    {
        RebuildLayoutLegend();
        RebuildReferenceLegend();
    }

    partial void OnLayoutBindSearchChanged(string value) => RebuildLayoutBindCandidates();

    // Called when the selected layout marker changes (OnSelectedLayoutEditorMarkerChanged).
    private void OnLayoutWorkspaceSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelectedLayoutMarker));
        OnPropertyChanged(nameof(HasNoSelectedLayoutMarker));
        OnPropertyChanged(nameof(WorkspaceSelectedMarker));
        NotifyWorkspaceInput();
        LayoutBindSearch = string.Empty;
        RebuildLayoutBindCandidates();
    }

    private void RebuildLayoutLegend()
    {
        string filter = (LayoutLegendFilter ?? string.Empty).Trim();
        List<LayoutEditorMarkerItem> rows = LayoutEditorMarkers
            .Where(m => filter.Length == 0 ||
                        (m.Token ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        m.ShortLabel.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        (m.Command ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        (m.Modifier ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => DeviceMarkerText.SortKey(m.Token, m.Type), StringComparer.Ordinal)
            .ThenBy(m => m.Modifier, StringComparer.OrdinalIgnoreCase)
            .ToList();
        LayoutLegendItems.Clear();
        foreach (LayoutEditorMarkerItem row in rows)
            LayoutLegendItems.Add(row);
        OnPropertyChanged(nameof(LayoutLegendSummary));
    }

    // What every marker of the editor does for the layout's device (the bound relation).
    private void RefreshLayoutMarkerCommands()
    {
        string device = ResolveLayoutEditorContextDevice();
        foreach (LayoutEditorMarkerItem marker in LayoutEditorMarkers)
            marker.Command = device.Length == 0 ? string.Empty : FindRelationBoundTo(device, marker.Token ?? string.Empty, marker.Modifier ?? string.Empty);
        RebuildLayoutLegend();
        OnPropertyChanged(nameof(LayoutWorkspaceDeviceTitle));
        OnPropertyChanged(nameof(LayoutWorkspaceTemplateName));
        NotifyWorkspaceInput();
    }

    // After every binds refresh: the pictures show what the inputs do now.
    private void OnBindsReloadedForPictures()
    {
        if (LayoutEditorMarkers.Count > 0)
            RefreshLayoutMarkerCommands();
        if (deviceReference != null && deviceReference.DeviceName.Length > 0)
        {
            deviceReference.SetBindings(DeviceReferenceBindings(deviceReference.DeviceName));
            NotifyWorkspaceInput();
            OnPropertyChanged(nameof(LayoutLegendSummary));
        }
        RebuildLayoutBindCandidates();
        OnBindsReloadedForCallouts();
    }

    // The input the Bind panel works on: the selected layout marker, or the selected input of the reference's part.
    private (string Input, string Modifier, string Label, bool Axis)? WorkspaceBindTarget()
    {
        if (LayoutShowsLayoutFile)
        {
            LayoutEditorMarkerItem? marker = SelectedLayoutEditorMarker;
            if (marker == null)
                return null;
            string type = (marker.Type ?? "button").Trim().ToLowerInvariant();
            string label = (marker.Modifier ?? string.Empty).Length > 0 ? marker.Modifier + " + " + marker.ShortLabel : marker.ShortLabel;
            return (marker.Token ?? string.Empty, marker.Modifier ?? string.Empty, label, type is "axis" or "slider");
        }
        ReferenceLegendRow? row = SelectedReferenceRow;
        if (row == null || !row.HasInput)
            return null;
        // On the reference picture the modifier combination is picked in the side panel (VisualAssignCombo).
        string combo = VisualAssignCombo ?? string.Empty;
        string name = row.Name.Length > 0 ? row.Name + " (" + DeviceMarkerText.ShortLabel(row.Input) + ")" : DeviceMarkerText.ShortLabel(row.Input);
        return (row.Input, combo, combo.Length > 0 ? CalloutCombos.Label(combo) + " + " + name : name, DeviceVocabulary.InputKind(row.Input) == "axis");
    }

    // The relations that fit the selected input (axes for an axis); the relation picked on the left comes first and is
    // picked when it fits, else the one the input has now.
    private void RebuildLayoutBindCandidates()
    {
        LayoutBindCandidates.Clear();
        RebuildVisualAssignCombos();
        if (WorkspaceBindTarget() is not { } target)
        {
            NotifyVisualAssignPreview();
            return;
        }
        string left = LeftPickedRelation();
        bool leftFits = left.Length > 0 && RelationIsAxis(left) == target.Axis;
        List<string> names = FilterRelationCandidates(RelationNamesOfKind(axis: target.Axis), LayoutBindSearch).ToList();
        if (leftFits && names.Remove(left))
            names.Insert(0, left);
        foreach (string name in names)
            LayoutBindCandidates.Add(name);
        string current = CurrentRelationOf(target.Input, target.Modifier);
        string wanted = leftFits ? left : current;
        SelectedLayoutBindCandidate = LayoutBindCandidates.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
        NotifyVisualAssignPreview();
    }

    // The relation the profile has on an input of the workspace's device ("" = free).
    private string CurrentRelationOf(string input, string modifier)
    {
        string device = ResolveLayoutEditorContextDevice();
        return device.Length == 0 || input.Length == 0 ? string.Empty : FindRelationBoundTo(device, input, modifier);
    }

    private static IEnumerable<string> FilterRelationCandidates(IEnumerable<string> names, string? search)
    {
        string filter = (search ?? string.Empty).Trim();
        return names.Where(n => !string.Equals(n, NoRelation, StringComparison.Ordinal) &&
                                (filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- The canvas events ----

    // The end of a drag or an arrow-key nudge on the canvas (Edit markers mode only): a layout marker or a part of
    // the reference, with undo.
    [RelayCommand]
    private void LayoutCanvasMarkerMoved(DeviceMarkerMove? move)
    {
        if (move == null || !IsLayoutEditMode)
            return;
        if (move.Marker is LayoutEditorMarkerItem marker)
        {
            PushLayoutUndo("move " + marker.ShortLabel);
            MoveLayoutEditorMarker(marker, move.ToX, move.ToY);
            LayoutEditorStatus = T("Moved {0} to {1}.", marker.ShortLabel, marker.PositionLabel);
        }
        else if (move.Marker is ReferenceCanvasMarker part && deviceReference != null)
        {
            PushLayoutUndo("move " + part.Label);
            deviceReference.MoveMarkerToPixels(part.Part, move.ToX, move.ToY);
            OnPropertyChanged(nameof(WorkspaceHasUnsavedChanges));
        }
    }

    // A click on the picture outside the markers: the part the marker assistant asks for goes there; in Edit mode the
    // layout file's tool places a marker / table / connector point; else the selection is cleared.
    [RelayCommand]
    private void LayoutCanvasClicked(DeviceCanvasClick? click)
    {
        if (click == null)
            return;
        if (!click.IsRightButton && LayoutShowsReference && deviceReference != null && deviceReference.PlaceAssistantPart(click.X, click.Y))
            return;
        if (click.IsRightButton || !IsLayoutEditMode || LayoutShowsReference)
        {
            SelectedLayoutEditorMarker = null;
            SelectedLayoutEditorTable = null;
            SelectedLayoutEditorConnector = null;
            if (deviceReference != null && LayoutShowsReference)
                deviceReference.SelectedMarker = null;
            return;
        }
        HandleLayoutCanvasClick(click.X, click.Y);
    }

    // ---- Undo / redo of marker edits ----

    private sealed record LayoutMarkerState(string Token, string Type, string Modifier, double X, double Y);

    private sealed record ReferencePartState(string Id, double X, double Y, bool IsPlaced);

    // The markers of the shown picture before a change: the layout file's markers, or the reference's parts.
    private sealed record LayoutEditSnapshot(string Description, bool Reference, IReadOnlyList<LayoutMarkerState> Markers, IReadOnlyList<ReferencePartState> Parts, string SelectedToken);

    private LayoutEditSnapshot CaptureLayoutSnapshot(string description)
    {
        if (LayoutShowsReference && deviceReference != null)
            return new(description, true, [], deviceReference.Markers.Select(m => new ReferencePartState(m.Id, m.X, m.Y, m.IsPlaced)).ToList(), deviceReference.SelectedMarker?.Id ?? string.Empty);
        return new(description, false,
            LayoutEditorMarkers.Select(m => new LayoutMarkerState(m.Token ?? string.Empty, m.Type ?? "button", m.Modifier ?? string.Empty, m.X, m.Y)).ToList(), [],
            SelectedLayoutEditorMarker?.Token ?? string.Empty);
    }

    // Before every marker change: remember the markers as they are.
    private void PushLayoutUndo(string description)
    {
        layoutUndo.Add(CaptureLayoutSnapshot(description));
        if (layoutUndo.Count > LayoutUndoLimit)
            layoutUndo.RemoveAt(0);
        layoutRedo.Clear();
        if (LayoutShowsLayoutFile)
            LayoutEditorHasUnsavedChanges = true;
        NotifyLayoutUndoState();
    }

    private void ClearLayoutUndo()
    {
        layoutUndo.Clear();
        layoutRedo.Clear();
        LayoutEditorHasUnsavedChanges = false;
        NotifyLayoutUndoState();
    }

    private void NotifyLayoutUndoState()
    {
        OnPropertyChanged(nameof(CanUndoLayoutEdit));
        OnPropertyChanged(nameof(CanRedoLayoutEdit));
        OnPropertyChanged(nameof(LayoutUndoTip));
        OnPropertyChanged(nameof(LayoutRedoTip));
    }

    [RelayCommand]
    private void UndoLayoutEdit()
    {
        if (UndoesCallouts)
        {
            LayoutCallouts.Undo();
            SyncPositionsFromCallouts();
            NotifyLayoutUndoState();
            return;
        }
        if (layoutUndo.Count == 0)
        {
            LayoutEditorStatus = T("Nothing to undo.");
            return;
        }
        LayoutEditSnapshot snapshot = layoutUndo[^1];
        layoutUndo.RemoveAt(layoutUndo.Count - 1);
        layoutRedo.Add(CaptureLayoutSnapshot(snapshot.Description));
        RestoreLayoutSnapshot(snapshot);
        NotifyLayoutUndoState();
        LayoutEditorStatus = T("Undone: {0}.", snapshot.Description);
    }

    [RelayCommand]
    private void RedoLayoutEdit()
    {
        if (RedoesCallouts)
        {
            LayoutCallouts.Redo();
            SyncPositionsFromCallouts();
            NotifyLayoutUndoState();
            return;
        }
        if (layoutRedo.Count == 0)
        {
            LayoutEditorStatus = T("Nothing to redo.");
            return;
        }
        LayoutEditSnapshot snapshot = layoutRedo[^1];
        layoutRedo.RemoveAt(layoutRedo.Count - 1);
        layoutUndo.Add(CaptureLayoutSnapshot(snapshot.Description));
        RestoreLayoutSnapshot(snapshot);
        NotifyLayoutUndoState();
        LayoutEditorStatus = T("Redone: {0}.", snapshot.Description);
    }

    private void RestoreLayoutSnapshot(LayoutEditSnapshot snapshot)
    {
        if (snapshot.Reference)
        {
            RestoreReferenceSnapshot(snapshot);
            return;
        }
        LayoutEditorHasUnsavedChanges = true;
        // The same objects where the input still exists (connector lines follow their moves), removed and added ones by
        // input name.
        Dictionary<string, LayoutMarkerState> wanted = new(StringComparer.OrdinalIgnoreCase);
        foreach (LayoutMarkerState state in snapshot.Markers)
            wanted[state.Token] = state;

        foreach (LayoutEditorMarkerItem marker in LayoutEditorMarkers.ToList())
        {
            if (!wanted.ContainsKey(marker.Token ?? string.Empty))
                LayoutEditorMarkers.Remove(marker);
        }

        for (int i = 0; i < snapshot.Markers.Count; i++)
        {
            LayoutMarkerState state = snapshot.Markers[i];
            LayoutEditorMarkerItem? marker = LayoutEditorMarkers.FirstOrDefault(m => string.Equals(m.Token, state.Token, StringComparison.OrdinalIgnoreCase));
            if (marker == null)
            {
                marker = new LayoutEditorMarkerItem { Token = state.Token, Type = state.Type, Modifier = state.Modifier, X = state.X, Y = state.Y };
                LayoutEditorMarkers.Insert(Math.Min(i, LayoutEditorMarkers.Count), marker);
                continue;
            }
            marker.Type = state.Type;
            marker.Modifier = state.Modifier;
            if (Math.Abs(marker.X - state.X) > 0.001 || Math.Abs(marker.Y - state.Y) > 0.001)
                MoveLayoutEditorMarker(marker, state.X, state.Y);
        }

        RefreshLayoutMarkerCommands();
        SelectedLayoutEditorMarker = LayoutEditorMarkers.FirstOrDefault(m => string.Equals(m.Token, snapshot.SelectedToken, StringComparison.OrdinalIgnoreCase));
    }

    private void RestoreReferenceSnapshot(LayoutEditSnapshot snapshot)
    {
        if (deviceReference == null)
            return;
        foreach (ReferencePartState state in snapshot.Parts)
        {
            ReferenceMarker? part = deviceReference.Markers.FirstOrDefault(m => m.Id == state.Id);
            if (part == null)
                continue;
            if (Math.Abs(part.X - state.X) > 1e-6 || Math.Abs(part.Y - state.Y) > 1e-6)
                deviceReference.MoveMarker(part, state.X, state.Y);
            part.IsPlaced = state.IsPlaced;
        }
        deviceReference.SelectedMarker = deviceReference.Markers.FirstOrDefault(m => m.Id == snapshot.SelectedToken);
        deviceReference.HasUnsavedChanges = true;
    }

    // ---- Bind on the picture ----

    // Binds a relation to the selected input ("Bind here"): the parameter, else the one picked in the side panel's list,
    // else the relation picked on the left (Bind Studio). With the picked modifier combination on a reference picture.
    [RelayCommand]
    private async Task BindSelectedLayoutMarkerAsync(string? relation)
    {
        (string Input, string Modifier, string Label, bool Axis)? target = WorkspaceBindTarget();
        string picked = (SelectedLayoutBindCandidate ?? string.Empty).Trim();
        string left = LeftPickedRelation();
        string next = (relation ?? (picked.Length > 0 ? picked : left)).Trim();
        Log("[VisualAssign] Bind here: parameter='" + relation + "', list='" + picked + "', left='" + left + "' -> relation '" + next + "'; input " +
            (target is { } t0 ? "'" + t0.Input + "' modifiers '" + t0.Modifier + "' (" + (t0.Axis ? "axis" : "button") + ")" : "(none)") +
            "; device '" + ResolveLayoutEditorContextDevice() + "'");
        if (target is not { } t)
        {
            ReportVisualAssign(SelectedReferenceRow is { HasInput: false } row
                ? T("{0} has no input number on this device yet: pick another one.", row.Name)
                : T("Click an input on the picture first."), ok: false);
            return;
        }
        if (next.Length == 0)
        {
            ReportVisualAssign(T("Pick a relation on the left (or in the list) first, then Bind here."), ok: false);
            return;
        }
        if (!string.Equals(next, NoRelation, StringComparison.Ordinal) && allRelations.Count > 0 &&
            !allRelations.Any(r => string.Equals(r.Name, next, StringComparison.OrdinalIgnoreCase)))
        {
            ReportVisualAssign(T("There is no relation '{0}' in this profile.", next), ok: false);
            return;
        }
        if (RelationIsAxis(next) != t.Axis)
        {
            ReportVisualAssign(T("{0} is {1} relation and {2} is {3}: click {4} on the picture, or pick a fitting relation in the list.", next, (t.Axis ? "a button" : "an axis"), t.Label, (t.Axis ? "an axis" : "a button"), (t.Axis ? "a button" : "an axis")), ok: false);
            return;
        }
        if (string.Equals(CurrentRelationOf(t.Input, t.Modifier), next, StringComparison.OrdinalIgnoreCase))
        {
            ReportVisualAssign(T("{0} is on {1} already.", next, t.Label), ok: true);
            FlashWorkspaceInput(t.Input);
            return;
        }
        await AssignWorkspaceInputAsync(t, next);
    }

    [RelayCommand]
    private async Task FreeSelectedLayoutMarkerAsync()
    {
        if (WorkspaceBindTarget() is not { } target)
        {
            ReportVisualAssign(T("Click an input on the picture first."), ok: false);
            return;
        }
        if (CurrentRelationOf(target.Input, target.Modifier).Length == 0)
        {
            ReportVisualAssign(T("{0} is free already.", target.Label), ok: true);
            return;
        }
        await AssignWorkspaceInputAsync(target, NoRelation);
    }

    private async Task AssignWorkspaceInputAsync((string Input, string Modifier, string Label, bool Axis) target, string next)
    {
        string device = ResolveLayoutEditorContextDevice();
        if (device.Length == 0)
        {
            ReportVisualAssign(T("Pick the device first."), ok: false);
            return;
        }
        string current = CurrentRelationOf(target.Input, target.Modifier);
        string previous = current.Length > 0 ? current : NoRelation;
        bool free = string.Equals(next, NoRelation, StringComparison.Ordinal);
        Log("[VisualAssign] " + (free ? "free" : "bind '" + next + "'") + " on device '" + device + "' input '" + target.Input + "' modifiers '" + target.Modifier +
            "' (now: '" + current + "')");
        bool ok = await AssignInputAsync(device, target.Input, target.Modifier, target.Label, previous, next);
        string bound = CurrentRelationOf(target.Input, target.Modifier);
        Log("[VisualAssign] result: ok=" + ok + ", the input now has '" + bound + "'");
        if (!ok)
        {
            ReportVisualAssign(T("The change failed; see Engine Output."), ok: false);
            return;
        }
        if (free)
        {
            ReportVisualAssign(T("{0} is free now.", target.Label), ok: true);
            return;
        }
        ReportVisualAssign(T("Bound {0} to {1} {2}.", next, DeviceDisplayName(device), target.Label), ok: true);
        FlashWorkspaceInput(target.Input);
    }

    // Moves the bind of an input: frees it from the relation that had it, binds the new relation (or nothing for
    // NoRelation), then reloads the binds.
    private async Task<bool> AssignInputAsync(string device, string input, string modifier, string label, string previous, string next)
    {
        bool ok = true;
        if (!string.Equals(previous, NoRelation, StringComparison.Ordinal) && previous.Length > 0)
        {
            EngineCommandResult? clear = await RunEngineAsync(["binds", "clear", previous, "--input-only"], includeProfile: true, mutatesProfile: true, busyLabel: "Freeing " + label + "...");
            ok = clear?.ExitCode == 0;
        }

        if (ok && !string.Equals(next, NoRelation, StringComparison.Ordinal))
        {
            List<string> args = ["binds", "set", next, "--device", device, "--input", input, "--clear-modifiers"];
            // Every modifier of the combination ("Shift A+Shift B" = both held).
            foreach (string name in CalloutCombos.Split(modifier))
                args.AddRange(["--modifier", name]);
            EngineCommandResult? set = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: "Binding " + next + "...");
            ok = set?.ExitCode == 0;
        }

        // The same refresh as Set Bind / Record: binds, the relations' bound state and counts, the modifiers' uses.
        await RefreshBindsAndModifiersAfterChangeAsync();
        return ok;
    }

    // Bind Studio's picture -> the Layout page with the same device (marker editing, the assistant, cheat sheets).
    [RelayCommand]
    private async Task OpenVisualAssignInLayoutAsync()
    {
        string device = (VisualAssignDevice ?? string.Empty).Trim();
        CurrentStage = UiStage.Layout;
        IsCheatSheetsSection = false;
        if (device.Length > 0)
            await ShowWorkspaceDeviceAsync(device);
        LayoutCanvasMode = DeviceCanvasMode.EditMarkers;
    }
}
