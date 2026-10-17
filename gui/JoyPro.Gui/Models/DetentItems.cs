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

// One proposal of "merge my per-aircraft throttle relations" (binds detent merge without --apply).
public sealed class DetentMergeProposalItem
{
    public DetentMergeProposalItem(string deviceLabel, string into, IReadOnlyList<string> relations, string calibrationText,
        IReadOnlyList<string> groupLines, IReadOnlyList<string> notes, IReadOnlyList<string> problems)
    {
        DeviceLabel = deviceLabel;
        Into = into;
        Relations = relations;
        CalibrationText = calibrationText;
        GroupLines = groupLines;
        Notes = notes;
        Problems = problems;
    }

    public string DeviceLabel { get; }

    public string Into { get; }

    public IReadOnlyList<string> Relations { get; }

    public string CalibrationText { get; }

    public IReadOnlyList<string> GroupLines { get; }

    public IReadOnlyList<string> Notes { get; }

    public IReadOnlyList<string> Problems { get; }

    public bool CanApply => Problems.Count == 0;

    public string Heading => T("{0}: {1} -> {2}", DeviceLabel, string.Join(", ", Relations), Into);

    public string GroupsText => string.Join("\n", GroupLines);

    public string NotesText => string.Join("\n", Notes.Concat(Problems));

    public bool HasNotes => Notes.Count + Problems.Count > 0;
}
