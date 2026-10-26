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
    // The cards shown: a page at a time ("Show more"), so thousands of findings never build thousands of cards at once
    // (a DCS import with defaults: 3365 errors froze the app).
    public ObservableCollection<CheckIssueCard> Cards { get; } = [];

    // Cards built at once and per "Show more": the check's own lines are light; a card of actions in several relations
    // has its fixes (buttons, lists) and costs about ten times as much to build.
    public const int DefaultPageSize = 25;
    public const int HeavyPageSize = 10;
    public int PageSize { get; init; } = DefaultPageSize;

    private IReadOnlyList<CheckIssueCard> allCards = [];

    // Every card of the section (filtered and sorted); Cards holds the first ones of them.
    public IReadOnlyList<CheckIssueCard> AllCards => allCards;
    public int TotalCount => allCards.Count;

    // "Most of them: Keyboard (2919), MOZA AB9 FFB Base (130), ..." (empty: no breakdown).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBreakdown))]
    private string breakdown = string.Empty;

    public bool HasBreakdown => Breakdown.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore))]
    [NotifyPropertyChangedFor(nameof(ShowMoreText))]
    private int shownCount;

    public bool HasMore => ShownCount < TotalCount;

    public string ShowMoreText => Localization.Loc.T("Show {0} more ({1} not shown yet)",
        System.Math.Min(PageSize, TotalCount - ShownCount), TotalCount - ShownCount);

    // The view model's "Show more": remembers how many the section shows (a rebuild keeps them).
    public ICommand? ShowMoreCommand { get; init; }

    [ObservableProperty]
    private bool isExpanded = true;

    // What the count in the header counts, in words ("actions: 206, cards: 103"); empty: the number of cards.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private string countLabel = string.Empty;

    public string CountText => Title + " (" + (CountLabel.Length > 0 ? CountLabel : TotalCount.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ")";

    // The section's cards; the first 'shown' of them (at least one page) are built.
    public void SetCards(IReadOnlyList<CheckIssueCard> cards, int shown)
    {
        allCards = cards;
        // A short rest is shown with the page instead of a "Show 2 more".
        int first = System.Math.Max(PageSize, shown);
        if (cards.Count <= first + PageSize / 2)
            first = cards.Count;
        // The cards shown already that stay in their place keep what the panel built for them.
        int same = 0;
        while (same < Cards.Count && same < first && ReferenceEquals(Cards[same], cards[same]))
            same++;
        for (int i = Cards.Count - 1; i >= same; i--)
            Cards.RemoveAt(i);
        ShownCount = Cards.Count;
        ShowUpTo(first);
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CountText));
    }

    // Shows the cards up to this count (the next page with "Show more").
    public void ShowUpTo(int count)
    {
        int target = System.Math.Min(count, allCards.Count);
        for (int i = Cards.Count; i < target; i++)
            Cards.Add(allCards[i]);
        ShownCount = Cards.Count;
    }
}

public partial class CheckIssueCard : ObservableObject
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
    // What the section's breakdown counts it under (a plain error: its device, e.g. "Keyboard"); empty: not counted.
    public string GroupLabel { get; init; } = string.Empty;
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

    // "Copied relations" (engine relations merge-copies): merge this family, or every family, into one each.
    public string MergeCopiesText { get; init; } = string.Empty;
    public ICommand? MergeCopiesCommand { get; init; }
    public bool HasMergeCopies => MergeCopiesCommand != null;
    public string MergeAllCopiesText { get; init; } = string.Empty;
    public ICommand? MergeAllCopiesCommand { get; init; }
    public bool HasMergeAllCopies => MergeAllCopiesCommand != null;

    [ObservableProperty]
    private bool isExpanded = true;
}

// A card of the check's own lines (errors, notes): headline, details and "Open Issue Context" only, drawn with a light
// template of its own (CheckProblemsPanel.axaml) - the duplicate card's buttons and lists cost a lot per card.
public sealed class PlainCheckIssueCard : CheckIssueCard
{
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
