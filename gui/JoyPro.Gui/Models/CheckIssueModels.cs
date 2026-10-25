using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// The check's problems on the Export page (Views/Controls/CheckProblemsPanel.axaml, MainWindowViewModel.Duplicates.cs):
// sections by kind (actions in several relations, errors, notes), each with a count and what it means, and cards in
// them; a card of "actions in several relations" is one relation pair with its actions and aircraft and its fixes.
public sealed partial class CheckIssueSection : ObservableObject
{
    // "duplicates", "errors", "notes"
    public string Kind { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    // What it means for the game ("Two inputs do the same thing in the game ...").
    public string Severity { get; init; } = string.Empty;
    public bool IsSerious { get; init; }
    public ObservableCollection<CheckIssueCard> Cards { get; } = [];

    [ObservableProperty]
    private bool isExpanded = true;

    public string CountText => Title + " (" + Cards.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
}

public sealed partial class CheckIssueCard : ObservableObject
{
    public string Kind { get; init; } = string.Empty;
    // The headline: "AH-64D_BLK_II (CPG and Pilot): 'Force Trim/Hold Mode Switch - AL/Right'" or a plain issue's text.
    public string Headline { get; init; } = string.Empty;
    // "is in two relations: 'Mode Button 07' (Total Controls MFBB · BTN 39) and 'Trim Right' (MOZA AB9 · BTN 4). DCS gets two binds for it."
    public string Details { get; init; } = string.Empty;
    // More actions of the same relation pair: one line each.
    public List<string> ActionLines { get; init; } = [];
    public bool HasActionLines => ActionLines.Count > 0;
    // "92% sure: keep in 'Trim Right'" (empty: no suggestion).
    public string SuggestionText { get; init; } = string.Empty;
    public bool HasSuggestion => SuggestionText.Length > 0;
    public int Confidence { get; init; }
    // Why: "It was only in 'Trim Right' before (21 Oct 10:21)."
    public string ReasonText { get; init; } = string.Empty;
    public bool HasReason => ReasonText.Length > 0;
    // Brought by the last import or take-over.
    public bool IsNew { get; init; }

    // For filter and sort.
    public string Game { get; init; } = string.Empty;
    public string RelationsSort { get; init; } = string.Empty;
    public string AircraftSort { get; init; } = string.Empty;
    public string SearchText { get; init; } = string.Empty;
    public List<string> Keys { get; init; } = [];
    public List<string> Relations { get; init; } = [];
    public string Suggested { get; init; } = string.Empty;
    public bool SuggestOff { get; init; }

    public List<DuplicateRelationChoice> Choices { get; init; } = [];
    public bool HasChoices => Choices.Count > 0;
    public ICommand? KeepBothCommand { get; init; }
    public ICommand? OpenAllCommand { get; init; }
    public ICommand? SwitchOffAllCommand { get; init; }
    public ICommand? OpenContextCommand { get; init; }
    public bool HasOpenContext => OpenContextCommand != null;

    [ObservableProperty]
    private bool isExpanded = true;
}

// "Keep in 'Trim Right' only" for one relation of a card.
public sealed class DuplicateRelationChoice
{
    public string Relation { get; init; } = string.Empty;
    public string BindLabel { get; init; } = string.Empty;
    public bool IsSuggested { get; init; }
    public string KeepText { get; init; } = string.Empty;
    public string Tip { get; init; } = string.Empty;
    public ICommand? KeepCommand { get; init; }
}

// One change of a fix for many, ticked to apply.
public sealed partial class DuplicateChangeItem : ObservableObject
{
    public string Key { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public int Confidence { get; init; }
    public string ConfidenceText => Confidence > 0 ? Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%" : string.Empty;

    [ObservableProperty]
    private bool isTicked = true;
}

// A rule of "Fix all with a rule" (the engine's --rule value and its label).
public sealed class DuplicateRuleChoice
{
    public string Rule { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public bool NeedsDevice { get; init; }
    public bool NeedsWords { get; init; }
    public override string ToString() => Label;
}

public static class CheckIssueText
{
    public static string Join(IReadOnlyList<string> items, string and)
    {
        if (items.Count <= 1)
            return items.FirstOrDefault() ?? string.Empty;
        return string.Join(", ", items.Take(items.Count - 1)) + " " + and + " " + items[^1];
    }
}
