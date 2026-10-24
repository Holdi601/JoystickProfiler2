using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// One relation "Group by description" proposes (engine relations from-text --dry-run): ticked relations are created,
// unticked nodes are skipped (--select / --skip-node).
public sealed partial class DescribeGroupProposalItem : ObservableObject
{
    public DescribeGroupProposalItem(string name, string function, bool isAxis, bool existing, string why, IReadOnlyList<DescribeGroupNodeItem> nodes, IReadOnlyList<string> missing)
    {
        Name = name;
        Function = function;
        IsAxis = isAxis;
        Existing = existing;
        Why = why;
        Nodes = new ObservableCollection<DescribeGroupNodeItem>(nodes);
        Missing = missing.Count == 0
            ? string.Empty
            : T("Not in the catalog for: {0}{1}", string.Join(", ", missing.Take(20)), (missing.Count > 20 ? T(" ... and {0} more", (missing.Count - 20).ToString(CultureInfo.InvariantCulture)) : string.Empty));
        isSelected = nodes.Count > 0;
    }

    public string Name { get; }

    public string Function { get; }

    public bool IsAxis { get; }

    public bool Existing { get; }

    public string Why { get; }

    public ObservableCollection<DescribeGroupNodeItem> Nodes { get; }

    public string Missing { get; }

    public bool HasMissing => Missing.Length > 0;

    public string Header =>
        T("{0}  ({1}, {2}, {3} aircraft/games)", Name, (IsAxis ? "axis" : "button"), (Existing ? T("adds to the existing relation") : "new"), Nodes.Sum(n => n.Planes.Count).ToString(CultureInfo.InvariantCulture));

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isExpanded;
}

public sealed partial class DescribeGroupNodeItem : ObservableObject
{
    public DescribeGroupNodeItem(string spec, string game, string id, IReadOnlyList<string> planes, double confidence, IReadOnlyList<string> explanations, IReadOnlyList<string> keptIn)
    {
        Spec = spec;
        Game = game;
        Id = id;
        Planes = planes;
        Confidence = confidence;
        Explanation = string.Join("\n", explanations);
        Kept = keptIn.Count == 0 ? string.Empty : T("Stays in: {0}", string.Join("; ", keptIn));
        // Low-confidence guesses (catalog words, a game that only partly fits) start unticked.
        isSelected = confidence >= 0.6;
    }

    public string Spec { get; }

    public string Game { get; }

    public string Id { get; }

    // The action's readable title (never the raw id; MainWindowViewModel.ResolveNodeTitle).
    public string Title { get; init; } = string.Empty;

    public IReadOnlyList<string> Planes { get; }

    public double Confidence { get; }

    public string ConfidenceText => Confidence.ToString("0.00", CultureInfo.InvariantCulture);

    public string Where => Game == "DCS"
        ? (Planes.Count <= 3 ? string.Join(", ", Planes) : T("{0} DCS aircraft", Planes.Count.ToString(CultureInfo.InvariantCulture)))
        : Game;

    public string Explanation { get; }

    public string Kept { get; }

    public bool HasKept => Kept.Length > 0;

    [ObservableProperty]
    private bool isSelected;
}

// A model of the local AI catalog (engine llm status).
public sealed class LlmModelItem
{
    public LlmModelItem(string id, string label, string tier, string size, string license, string licenseUrl, string source, bool installed, bool recommended)
    {
        Id = id;
        Label = label;
        Tier = tier;
        Size = size;
        License = license;
        LicenseUrl = licenseUrl;
        Source = source;
        Installed = installed;
        Recommended = recommended;
    }

    public string Id { get; }

    public string Label { get; }

    public string Tier { get; }

    public string Size { get; }

    public string License { get; }

    public string LicenseUrl { get; }

    public string Source { get; }

    public bool Installed { get; }

    public bool Recommended { get; }

    public override string ToString() =>
        Label + " - " + Size + (Recommended ? " (recommended)" : string.Empty) + (Installed ? " - downloaded" : string.Empty);
}
