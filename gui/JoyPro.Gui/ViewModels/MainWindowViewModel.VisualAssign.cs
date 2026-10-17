using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// "Bind on the picture" in Bind Studio: the Layout workspace's picture of a device (its reference or its layout file,
// MainWindowViewModel.LayoutReference.cs) in Bind mode. Picking a device here shows it on the Layout page too; clicking an
// input and picking a relation binds it (and frees the input from the relation that had it).
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<string> VisualAssignDeviceOptions { get; } = [];

    [ObservableProperty]
    private string visualAssignDevice = string.Empty;

    // The zoom of Bind Studio's canvas (the Layout page's canvas keeps its own).
    [ObservableProperty]
    private double visualAssignZoom = 0.5;

    // The modifier combination "Bind here" binds with on a reference picture ("" = no modifier, "Shift A+Shift B").
    [ObservableProperty]
    private string visualAssignCombo = string.Empty;

    // What happened last ("Bound Pitch to ...", or why nothing could be bound), shown in the side panel.
    [ObservableProperty]
    private string visualAssignStatus = string.Empty;

    [ObservableProperty]
    private bool visualAssignStatusIsError;

    // The marker just bound: the canvas rings it for a moment.
    [ObservableProperty]
    private object? workspaceFlashMarker;

    // Every modifier combination of the selected input with what is bound there (only with modifiers in the profile).
    public ObservableCollection<VisualAssignComboRow> VisualAssignCombos { get; } = [];

    public bool HasVisualAssignCombos => VisualAssignCombos.Count > 1;

    public bool HasVisualAssignStatus => VisualAssignStatus.Length > 0;

    // The trace of the visual assign steps ([VisualAssign] lines, also in the session log).
    internal List<string> GuiTrace { get; } = [];

    private int flashGeneration;
    private bool rebuildingCombos;

    // "Pitch -> L-VPC Stick WarBRD · Axis Y" (what Bind here does), "" when nothing is picked.
    public string VisualAssignPreview
    {
        get
        {
            if (WorkspaceBindTarget() is not { } target)
                return string.Empty;
            string relation = (SelectedLayoutBindCandidate ?? string.Empty).Trim();
            if (relation.Length == 0)
                relation = LeftPickedRelation();
            // Nothing picked, or the relation on the left does not fit (an axis relation on a button).
            if (relation.Length == 0 || RelationIsAxis(relation) != target.Axis)
                return string.Empty;
            string device = ResolveLayoutEditorContextDevice();
            return relation + " → " + (device.Length > 0 ? DeviceDisplayName(device) + " · " : string.Empty) + target.Label;
        }
    }

    public bool HasVisualAssignPreview => VisualAssignPreview.Length > 0;

    // "Bound: Pitch" / "Free" of the selected input under the picked combination.
    public string VisualAssignBoundState
    {
        get
        {
            if (WorkspaceBindTarget() is not { } target)
                return string.Empty;
            string current = CurrentRelationOf(target.Input, target.Modifier);
            return current.Length > 0 ? T("Bound: {0}", current) : T("Free");
        }
    }

    public bool VisualAssignTargetIsBound => WorkspaceBindTarget() is { } target && CurrentRelationOf(target.Input, target.Modifier).Length > 0;

    public bool VisualAssignTargetIsFree => WorkspaceBindTarget() != null && !VisualAssignTargetIsBound;

    partial void OnVisualAssignStatusChanged(string value) => OnPropertyChanged(nameof(HasVisualAssignStatus));

    partial void OnVisualAssignComboChanged(string value)
    {
        if (rebuildingCombos)
            return;
        foreach (VisualAssignComboRow row in VisualAssignCombos)
            row.IsSelected = string.Equals(row.Combo, value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        RebuildLayoutBindCandidates();
    }

    partial void OnSelectedLayoutBindCandidateChanged(string? value) => NotifyVisualAssignPreview();

    private void NotifyVisualAssignPreview()
    {
        OnPropertyChanged(nameof(VisualAssignPreview));
        OnPropertyChanged(nameof(HasVisualAssignPreview));
        OnPropertyChanged(nameof(VisualAssignBoundState));
        OnPropertyChanged(nameof(VisualAssignTargetIsBound));
        OnPropertyChanged(nameof(VisualAssignTargetIsFree));
    }

    // A modifier combination clicked in the side panel: "Bind here" binds with it.
    [RelayCommand]
    private void SelectVisualAssignCombo(VisualAssignComboRow? row)
    {
        if (row == null)
            return;
        VisualAssignCombo = row.Combo;
        Log("[VisualAssign] combination picked: " + row.Label);
    }

    // The combinations of the profile's modifiers for the selected input of a reference picture (a layout file's
    // markers carry their own modifier).
    private void RebuildVisualAssignCombos()
    {
        List<string> combos = [string.Empty];
        ReferenceLegendRow? row = LayoutShowsReference ? SelectedReferenceRow : null;
        if (row is { HasInput: true })
            combos.AddRange(JoyPro.DeviceReference.CalloutCombos.AllOf(ModifierNames, 15));
        if (!combos.Contains(VisualAssignCombo ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            // The combination is gone (a modifier deleted): no modifier.
            rebuildingCombos = true;
            try
            {
                VisualAssignCombo = string.Empty;
            }
            finally
            {
                rebuildingCombos = false;
            }
        }
        string device = ResolveLayoutEditorContextDevice();
        List<VisualAssignComboRow> rows = combos.Select(c => new VisualAssignComboRow
        {
            Combo = c,
            Label = JoyPro.DeviceReference.CalloutCombos.Label(c),
            Relation = row is { HasInput: true } && device.Length > 0 ? FindRelationBoundTo(device, row.Input, c) : string.Empty,
            IsSelected = string.Equals(c, VisualAssignCombo ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        }).ToList();
        if (rows.Count == VisualAssignCombos.Count && rows.Zip(VisualAssignCombos).All(p => p.First.Combo == p.Second.Combo))
        {
            for (int i = 0; i < rows.Count; i++)
            {
                VisualAssignCombos[i].Relation = rows[i].Relation;
                VisualAssignCombos[i].IsSelected = rows[i].IsSelected;
            }
        }
        else
        {
            VisualAssignCombos.Clear();
            foreach (VisualAssignComboRow item in rows)
                VisualAssignCombos.Add(item);
        }
        OnPropertyChanged(nameof(HasVisualAssignCombos));
    }

    // The relation picked on the left of Bind Studio (the bind list, else the relation list).
    private string LeftPickedRelation() => (SelectedBind?.RelationName ?? SelectedRelation?.Name ?? string.Empty).Trim();

    private bool RelationIsAxis(string relation) =>
        IsAxisRelation(relation) || allRelations.Any(r => string.Equals(r.Name, relation, StringComparison.OrdinalIgnoreCase) && r.Type.Contains("axis", StringComparison.OrdinalIgnoreCase));

    // Another relation picked on the left while the picture is open: it is the one Bind here binds.
    private void OnVisualAssignRelationPicked()
    {
        if (!OpenVisualAssignMode && !IsLayoutBindMode)
            return;
        RebuildLayoutBindCandidates();
    }

    // The side panel's and the window's status line, and the trace.
    private void ReportVisualAssign(string message, bool ok)
    {
        VisualAssignStatusIsError = !ok;
        VisualAssignStatus = message;
        LayoutEditorStatus = message;
        StatusText = message;
        Log("[VisualAssign] " + (ok ? "status: " : "not bound: ") + message);
        NotifyVisualAssignPreview();
    }

    // The marker of the input just bound is ringed for a few seconds; its combination row too.
    private void FlashWorkspaceInput(string input)
    {
        object? marker = LayoutShowsLayoutFile
            ? (object?)LayoutEditorMarkers.FirstOrDefault(m => string.Equals(m.Token, input, StringComparison.OrdinalIgnoreCase) &&
                                                              string.Equals(m.Modifier ?? string.Empty, SelectedLayoutEditorMarker?.Modifier ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            : deviceReference?.CanvasMarkers.FirstOrDefault(m => m.Part.HasInput(input));
        WorkspaceFlashMarker = marker;
        foreach (VisualAssignComboRow row in VisualAssignCombos)
            row.IsJustBound = row.IsSelected;
        int generation = ++flashGeneration;
        _ = ClearFlashLaterAsync(generation);
    }

    private async Task ClearFlashLaterAsync(int generation)
    {
        await Task.Delay(4000);
        if (generation != flashGeneration)
            return;
        WorkspaceFlashMarker = null;
        foreach (VisualAssignComboRow row in VisualAssignCombos)
            row.IsJustBound = false;
    }

    private void Log(string line)
    {
        string stamped = DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " " + line;
        lock (GuiTrace)
        {
            GuiTrace.Add(stamped);
            if (GuiTrace.Count > 500)
                GuiTrace.RemoveAt(0);
        }
        System.Diagnostics.Debug.WriteLine(stamped);
        AppendSessionLog(line);
    }

    partial void OnOpenVisualAssignModeChanged(bool value)
    {
        UpdateLiveInputMonitor();
        if (!value)
            return;
        RebuildVisualAssignDeviceOptions();
        string current = ResolveLayoutEditorContextDevice();
        if (string.IsNullOrWhiteSpace(VisualAssignDevice))
        {
            VisualAssignDevice = SelectedBind != null && SelectedBind.HasBind && !string.IsNullOrWhiteSpace(SelectedBind.Device)
                ? SelectedBind.Device
                : current.Length > 0 ? current : VisualAssignDeviceOptions.FirstOrDefault() ?? string.Empty;
        }
        else if (!SameDevice(VisualAssignDevice, current))
        {
            _ = LoadVisualAssignAsync();
        }
    }

    partial void OnVisualAssignDeviceChanged(string value)
    {
        ClearWorkspaceLiveInput();
        if (OpenVisualAssignMode && !string.IsNullOrWhiteSpace(value))
            _ = LoadVisualAssignAsync();
    }

    private void RebuildVisualAssignDeviceOptions()
    {
        IEnumerable<string> devices = allBinds
            .Where(b => b.HasBind && !string.IsNullOrWhiteSpace(b.Device) && !string.Equals(b.Device, "Keyboard", StringComparison.OrdinalIgnoreCase))
            .Select(b => b.Device)
            .Concat(QuickBindDeviceOptions.Where(d => !string.Equals(d, "Keyboard", StringComparison.OrdinalIgnoreCase)))
            .Concat(LayoutSuggestions.Select(s => s.Device));
        List<string> ordered = devices
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
        VisualAssignDeviceOptions.Clear();
        foreach (string device in ordered)
            VisualAssignDeviceOptions.Add(device);
    }

    // Shows the picked device's picture (reference, or the user's layout file) with what its inputs do.
    [RelayCommand]
    private async Task LoadVisualAssignAsync()
    {
        string device = (VisualAssignDevice ?? string.Empty).Trim();
        if (device.Length == 0)
        {
            LayoutEditorStatus = T("Pick a device first.");
            return;
        }
        if (allRelations.Count == 0)
            await RefreshRelationsAsync();
        if (allBinds.Count == 0)
            await RefreshBindsAsync();
        await ShowWorkspaceDeviceAsync(device);
    }

    private List<string> RelationNamesOfKind(bool axis)
    {
        HashSet<string> axisByBind = new(allBinds.Where(b => b.IsAxis).Select(b => b.RelationName), StringComparer.OrdinalIgnoreCase);
        List<string> names = allRelations
            .Where(r => (r.Type.Contains("axis", StringComparison.OrdinalIgnoreCase) || axisByBind.Contains(r.Name)) == axis)
            .Select(r => r.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        names.Insert(0, NoRelation);
        return names;
    }

    private string FindRelationBoundTo(string device, string input, string modifier)
    {
        BindListItem? bind = allBinds.FirstOrDefault(b =>
            b.HasBind &&
            SameDevice(b.Device, device) &&
            string.Equals((b.Input ?? string.Empty).Trim(), input.Trim(), StringComparison.OrdinalIgnoreCase) &&
            SameModifiers(b.ModifiersSummary, modifier));
        // A profile made on another PC (or before Windows gave the device a new GUID): the same device name.
        bind ??= allBinds.FirstOrDefault(b =>
            b.HasBind &&
            SameDeviceName(b.Device, device) &&
            string.Equals((b.Input ?? string.Empty).Trim(), input.Trim(), StringComparison.OrdinalIgnoreCase) &&
            SameModifiers(b.ModifiersSummary, modifier));
        return bind?.RelationName ?? string.Empty;
    }

    // The same device name without the GUID; Virpil's hand prefixes "LEFT " and "L-" (and "RIGHT "/"R-") count as one.
    internal static bool SameDeviceName(string? a, string? b)
    {
        (string name, string hand) Split(string? value)
        {
            string text = DeviceMarkerText.DeviceName(value);
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(text, @"^(left|right|l|r)(\s*-\s*|\s+|_)(?=\S)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string hand = m.Success ? (char.ToLowerInvariant(m.Groups[1].Value[0]) == 'l' ? "left" : "right") : string.Empty;
            string rest = m.Success ? text[m.Length..] : text;
            return (new string(rest.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant(), hand);
        }
        (string nameA, string handA) = Split(a);
        (string nameB, string handB) = Split(b);
        return nameA.Length > 0 && nameA == nameB && (handA.Length == 0 || handB.Length == 0 || handA == handB);
    }

    private static bool SameDevice(string? a, string? b)
    {
        string left = (a ?? string.Empty).Trim();
        string right = (b ?? string.Empty).Trim();
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;
        int l = left.LastIndexOf('{');
        int r = right.LastIndexOf('{');
        return l >= 0 && r >= 0 && string.Equals(left[l..], right[r..], StringComparison.OrdinalIgnoreCase);
    }

    // A bind's modifiers are exactly the combination ("Shift A+Shift B" = both, "" = none); order and case do not matter.
    internal static bool SameModifiers(string? summary, string modifier) => JoyPro.DeviceReference.CalloutCombos.Same(summary ?? string.Empty, modifier ?? string.Empty);
}
