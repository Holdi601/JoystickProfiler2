using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// The Binds page's axis response, 2.6.2 tester report ("the slider is difficult to duplicate between left and right"):
// - Copy to another axis: the values shown for the scope (dead zone, saturations, curvature, user curve, slider, the
//   model's own settings, invert only when ticked: left and right often differ there) go to the same scope of another
//   bound axis in one engine run (binds tuning set <other> --scope ...), so one Ctrl+Z undoes it.
// - The section opens by itself for an axis; closing it is remembered (list-preferences.json "AxisResponseOpen").
// - The "Modifiers on this bind" panel has a close button; closed, only its title line stays, with Show
//   (list-preferences.json "BindModifiersOpen").
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<AxisTuningCopyTarget> AxisTuningCopyTargets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopyAxisTuning))]
    private AxisTuningCopyTarget? axisTuningCopyTarget;

    // Invert goes along only when ticked.
    [ObservableProperty]
    private bool axisTuningCopyInvert;

    public bool HasAxisTuningCopyTargets => AxisTuningCopyTargets.Count > 0;

    public bool CanCopyAxisTuning => CanSaveAxisTuning && AxisTuningCopyTarget != null;

    // "Copies the settings of DCS / F-5E-3 shown here to the same scope of:"
    public string AxisTuningCopyText => T("Copies the settings of {0} shown here to the same scope of:",
        SelectedAxisTuningScope?.Label ?? T("All games (base)"));

    // The other bound axes, picked again each time the copy opens (binds change on the way).
    [RelayCommand]
    private void PrepareAxisTuningCopy()
    {
        string current = axisTuningRelation.Length > 0 ? axisTuningRelation : SelectedBind?.RelationName ?? string.Empty;
        string? previous = AxisTuningCopyTarget?.Relation;
        List<AxisTuningCopyTarget> targets = Binds
            .Where(b => b.IsAxisType && b.HasBind && !string.Equals(b.RelationName, current, StringComparison.OrdinalIgnoreCase))
            .GroupBy(b => b.RelationName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(b => b.RelationName, StringComparer.OrdinalIgnoreCase)
            .Select(b => new AxisTuningCopyTarget(b.RelationName, string.Join(" · ", new[] { b.RelationName, b.DeviceShort, b.InputLabel }.Where(t => !string.IsNullOrWhiteSpace(t)))))
            .ToList();
        AxisTuningCopyTargets.Clear();
        foreach (AxisTuningCopyTarget target in targets)
            AxisTuningCopyTargets.Add(target);
        AxisTuningCopyTarget = targets.FirstOrDefault(t => string.Equals(t.Relation, previous, StringComparison.OrdinalIgnoreCase)) ?? BestCopyTarget(current, targets);
        OnPropertyChanged(nameof(HasAxisTuningCopyTargets));
        OnPropertyChanged(nameof(AxisTuningCopyText));
        OnPropertyChanged(nameof(CanCopyAxisTuning));
    }

    // "Thrust Left" -> "Thrust Right" (and back), "Brake L" -> "Brake R": the other side first; else the first one.
    internal static AxisTuningCopyTarget? BestCopyTarget(string current, IReadOnlyList<AxisTuningCopyTarget> targets)
    {
        foreach ((string a, string b) in new[] { ("Left", "Right"), ("left", "right"), ("LEFT", "RIGHT"), (" L", " R"), ("_L", "_R"), ("Links", "Rechts") })
        {
            foreach ((string from, string to) in new[] { (a, b), (b, a) })
            {
                if (!current.Contains(from, StringComparison.Ordinal))
                    continue;
                string other = current.Replace(from, to, StringComparison.Ordinal);
                AxisTuningCopyTarget? match = targets.FirstOrDefault(t => string.Equals(t.Relation, other, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }
        }
        return targets.FirstOrDefault();
    }

    [RelayCommand]
    private async Task CopyAxisTuningToAxisAsync()
    {
        AxisTuningScopeItem? scope = SelectedAxisTuningScope;
        AxisTuningCopyTarget? target = AxisTuningCopyTarget;
        if (scope == null || target == null || axisTuningRelation.Length == 0)
            return;
        List<string>? args = BuildAxisTuningSetArguments(out string error, target.Relation, AxisTuningCopyInvert);
        if (args == null)
        {
            AxisTuningValidation = error;
            StatusText = error;
            return;
        }
        string from = axisTuningRelation;
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Copying the axis response..."));
        if (result?.ExitCode != 0)
            return;
        StatusText = AxisTuningCopyInvert
            ? T("The axis response of '{0}' ({1}) is copied to '{2}', with invert. Ctrl+Z undoes it.", from, scope.Label, target.Relation)
            : T("The axis response of '{0}' ({1}) is copied to '{2}'; its invert stays as it was. Ctrl+Z undoes it.", from, scope.Label, target.Relation);
    }

    // ---- the section opens by itself for an axis ------------------------------------------------------------------

    private bool applyingAxisResponseOpen;

    // The bind selection changed to an axis: open or closed as the user left it last (open at first). True when that
    // opened the section now (which loads it).
    private bool ApplyAxisResponseOpen()
    {
        if (!SelectedBindIsAxis || SelectedBind == null || IsAxisTuningExpanded == ListPrefs.AxisResponseOpen)
            return false;
        applyingAxisResponseOpen = true;
        try
        {
            IsAxisTuningExpanded = ListPrefs.AxisResponseOpen;
        }
        finally
        {
            applyingAxisResponseOpen = false;
        }
        return IsAxisTuningExpanded;
    }

    // The user opened or closed the section (not the automatic open): kept for the next axis and the next start.
    private void RememberAxisResponseOpen(bool open)
    {
        if (applyingAxisResponseOpen || !SelectedBindIsAxis || ListPrefs.AxisResponseOpen == open)
            return;
        ListPrefs.AxisResponseOpen = open;
        SaveListPreferences();
    }

    // ---- the "Modifiers on this bind" panel ---------------------------------------------------------------------

    public bool IsBindModifiersOpen
    {
        get => ListPrefs.BindModifiersOpen;
        set
        {
            if (ListPrefs.BindModifiersOpen == value)
                return;
            ListPrefs.BindModifiersOpen = value;
            SaveListPreferences();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void CloseBindModifiers() => IsBindModifiersOpen = false;

    [RelayCommand]
    private void OpenBindModifiers() => IsBindModifiersOpen = true;
}
