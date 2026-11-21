using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.Gui.Localization;

namespace JoyPro.Gui.Models;

// One aircraft group of a throttle relation in detent mode (binds detent show): aircraft with the same afterburner
// start share one curve, e.g. "F-16C, F/A-18C: afterburner at 78 %".
public sealed partial class DetentGroupItem : ObservableObject
{
    public DetentGroupItem(string target, string kind, double? abStart, string describe, string label,
        IReadOnlyList<(string Game, string Plane, string Title, string Source, string Confidence, double? GameData)> aircraft,
        IReadOnlyList<string> gameLines, IReadOnlyList<string> warnings, IReadOnlyList<Point> throttle, double detentTravel)
    {
        Target = target;
        Kind = kind;
        AbStart = abStart;
        Describe = describe;
        Label = label;
        Aircraft = aircraft;
        GameLines = gameLines;
        Warnings = warnings;
        Throttle = throttle;
        DetentTravel = detentTravel;
        abText = abStart.HasValue ? Math.Round(abStart.Value * 100.0, 1).ToString("0.#", CultureInfo.InvariantCulture) : string.Empty;
    }

    public string Target { get; }

    // Afterburner, FullAtDetent, Linear
    public string Kind { get; }

    public double? AbStart { get; }

    public string Describe { get; }

    public string Label { get; }

    public IReadOnlyList<(string Game, string Plane, string Title, string Source, string Confidence, double? GameData)> Aircraft { get; }

    public IReadOnlyList<string> GameLines { get; }

    public IReadOnlyList<string> Warnings { get; }

    // Game throttle over the device travel, both 0..1 (the first game of the group).
    public IReadOnlyList<Point> Throttle { get; }

    public double DetentTravel { get; }

    public double AbStartOrNaN => AbStart ?? double.NaN;

    public bool IsAfterburner => Kind == "Afterburner";

    public bool IsFullAtDetent => Kind == "FullAtDetent";

    public bool HasCurve => Throttle.Count > 1;

    public bool HasWarnings => Warnings.Count > 0;

    public string WarningText => string.Join(" ", Warnings);

    public string Heading => T("{0}: {1}", Label, Loc.Translate(Describe));

    public string GamesText => string.Join("\n", GameLines);

    // Where the values come from ("game data, high", "relation", "unknown"), and the game data's value where it differs.
    public string SourceText
    {
        get
        {
            List<string> parts = [];
            foreach (IGrouping<string, (string Game, string Plane, string Title, string Source, string Confidence, double? GameData)> source in Aircraft.GroupBy(a => a.Source))
                parts.Add(source.Key switch
                {
                    "relation" => T("set in this relation"),
                    "game data" => source.Select(a => a.Confidence).FirstOrDefault(c => c.Length > 0) is { } c ? T("game data ({0} confidence)", T(c)) : T("game data"),
                    "unknown" => T("no game data: {0}", string.Join(", ", source.Select(a => a.Title))),
                    _ => source.Key
                });
            List<string> differ = Aircraft
                .Where(a => a.GameData.HasValue && AbStart.HasValue && Math.Abs(a.GameData.Value - AbStart.Value) >= 0.005)
                .Select(a => a.Title + " " + Percent(a.GameData!.Value))
                .Distinct()
                .ToList();
            if (differ.Count > 0)
                parts.Add(T("the game data says {0}", string.Join(", ", differ)));
            return string.Join("; ", parts);
        }
    }

    [ObservableProperty]
    private string abText;

    public static string Percent(double fraction) => Math.Round(fraction * 100.0, 1).ToString("0.#", CultureInfo.InvariantCulture) + " %";
}

// One relation of a merge proposal: ticked ones are merged; an unticked one stays apart with its own curve (2.6.5
// report: the merge took every throttle relation of the axis, a helicopter collective as well).
public sealed partial class DetentMergeRelationItem : ObservableObject
{
    public DetentMergeRelationItem(string name) => Name = name;

    public string Name { get; }

    [ObservableProperty]
    private bool isChecked = true;
}

// One proposal of "merge my per-aircraft throttle relations" (binds detent merge without --apply). The relations are
// ticked; unticking one plans the merge again for the ticked ones (Update).
public sealed class DetentMergeProposalItem : ObservableObject
{
    public DetentMergeProposalItem(string deviceLabel, string into, IReadOnlyList<string> relations, string calibrationText,
        IReadOnlyList<string> groupLines, IReadOnlyList<string> notes, IReadOnlyList<string> problems)
    {
        DeviceLabel = deviceLabel;
        Candidates = relations.Select(r => new DetentMergeRelationItem(r)).ToList();
        foreach (DetentMergeRelationItem candidate in Candidates)
            candidate.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(DetentMergeRelationItem.IsChecked))
                    return;
                Raise();
                TicksChanged?.Invoke(this);
            };
        Update(into, calibrationText, groupLines, notes, problems);
    }

    // A tick changed: the view model plans the merge of the ticked relations again.
    public event Action<DetentMergeProposalItem>? TicksChanged;

    public string DeviceLabel { get; }

    // Every throttle relation of the device axis, as the first plan found them.
    public IReadOnlyList<DetentMergeRelationItem> Candidates { get; }

    public string Into { get; private set; } = string.Empty;

    // The ticked relations: the ones a merge takes.
    public IReadOnlyList<string> Relations => Candidates.Where(c => c.IsChecked).Select(c => c.Name).ToList();

    public string CalibrationText { get; private set; } = string.Empty;

    public IReadOnlyList<string> GroupLines { get; private set; } = [];

    public IReadOnlyList<string> Notes { get; private set; } = [];

    public IReadOnlyList<string> Problems { get; private set; } = [];

    // The engine plans the ticked relations now (Merge waits for it).
    public bool IsPlanning
    {
        get => isPlanning;
        set
        {
            if (SetProperty(ref isPlanning, value))
                Raise();
        }
    }

    private bool isPlanning;

    public bool HasEnough => Relations.Count >= 2;

    public bool CanApply => Problems.Count == 0 && HasEnough && !IsPlanning;

    public string Heading => T("{0}: {1} -> {2}", DeviceLabel, string.Join(", ", Relations), Into);

    public string GroupsText => HasEnough ? string.Join("\n", GroupLines) : string.Empty;

    public string NotesText => HasEnough ? string.Join("\n", Notes.Concat(Problems)) : T("Tick at least two relations to merge.");

    public bool HasNotes => !HasEnough || Notes.Count + Problems.Count > 0;

    // The plan of the ticked relations (binds detent merge --relations ...).
    public void Update(string into, string calibrationText, IReadOnlyList<string> groupLines, IReadOnlyList<string> notes, IReadOnlyList<string> problems)
    {
        Into = into;
        CalibrationText = calibrationText;
        GroupLines = groupLines;
        Notes = notes;
        Problems = problems;
        Raise();
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(Into));
        OnPropertyChanged(nameof(Relations));
        OnPropertyChanged(nameof(CalibrationText));
        OnPropertyChanged(nameof(GroupLines));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(GroupsText));
        OnPropertyChanged(nameof(NotesText));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(HasEnough));
        OnPropertyChanged(nameof(CanApply));
    }
}
