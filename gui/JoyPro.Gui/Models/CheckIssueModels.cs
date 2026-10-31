using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// The check's problems on the Export page (Views/Controls/CheckProblemsPanel.axaml, MainWindowViewModel.Duplicates.cs,
// MainWindowViewModel.CheckCards.cs): sections by kind (same command on two buttons, one button does two things, ...),
// each with a count, what it means in the game and its icon (error / warning / note), and cards in them.
public sealed partial class CheckIssueSection : ObservableObject
{
    // "duplicates", "errors", "notes"
    public string Kind { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    // What it means for the game ("Two inputs do the same thing in the game ...").
    public string Severity { get; init; } = string.Empty;
    public bool IsSerious => Level != "note";
    // "error", "warning" or "note": the section's icon (errors have their own, never the warning's).
    public string Level { get; init; } = "warning";
    public bool IsError => Level == "error";
    public bool IsWarning => Level == "warning";
    public bool IsNote => Level == "note";
    // The cards shown: a page at a time ("Show more"), so thousands of findings never build thousands of cards at once
    // (a DCS import with defaults: 3365 errors froze the app).
    public ObservableCollection<CheckIssueCard> Cards { get; } = [];

    // Cards built at once and per "Show more": the check's own lines are light; a card of actions in several relations
    // has its fixes (buttons, lists) and costs about ten times as much to build.
    public const int DefaultPageSize = 25;
    public const int HeavyPageSize = 5;
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

// One problem of the check as a card, in game words (2.5.0, docs/ui/check.md): a short title, the button(s) it is about
// ("Throttle · BTN 9 ('A10 - DMS Down') ↔ Right Grip · BTN 16 ('Sensor Select Aft')"), one sentence what happens in the
// game, a table aircraft -> command (the first rows; "Show all"), what caused it (the profile history), the fixes (one
// recommended, the others, "More") and an "Open" button per relation; the rest (confidence, reasons) in the details.
// Never an id, "node" or "relation item key" outside the details' technical line.
public partial class CheckIssueCard : ObservableObject
{
    public string Kind { get; init; } = string.Empty;
    // "error", "warning" or "note": the card's icon (an error is never drawn with the warning's icon).
    public string Severity { get; init; } = "warning";
    public bool IsError => Severity == "error";
    public bool IsWarning => Severity == "warning";
    public bool IsNote => Severity == "note";
    // "Same command on two buttons"
    public string Title { get; init; } = string.Empty;
    // "Throttle · BTN 9 ('A10 - DMS Down')  ↔  Right Grip · BTN 16 ('Sensor Select Aft')" (empty: none)
    public string ButtonsLine { get; init; } = string.Empty;
    public bool HasButtonsLine => ButtonsLine.Length > 0;
    // One sentence: what happens in the game.
    public string Sentence { get; init; } = string.Empty;
    public bool HasSentence => Sentence.Length > 0;
    // The title and the sentence (tests, the search, older callers).
    public string Headline => Title;
    public string Details => Sentence;

    // Aircraft -> command (the first CollapsedRows of them until "Show all").
    public List<CheckTableRow> Rows { get; init; } = [];
    public const int CollapsedRows = 3;
    public bool HasRows => Rows.Count > 0;
    public bool HasHiddenRows => Rows.Count > CollapsedRows;
    public IReadOnlyList<CheckTableRow> ShownRows => ShowAllRows || Rows.Count <= CollapsedRows ? Rows : Rows.Take(CollapsedRows).ToList();
    public string ShowRowsText => ShowAllRows ? Localization.Loc.T("Show fewer") : Localization.Loc.T("Show all {0}", Rows.Count);
    public string RowsHeaderLeft { get; init; } = string.Empty;
    public string RowsHeaderRight { get; init; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownRows))]
    [NotifyPropertyChangedFor(nameof(ShowRowsText))]
    private bool showAllRows;

    public ICommand ToggleRowsCommand => toggleRows ??= new CommunityToolkit.Mvvm.Input.RelayCommand(() => ShowAllRows = !ShowAllRows);
    private ICommand? toggleRows;

    // "Caused by: Manage with AI (366 relation changes) on 2026-10-21" (empty: the history does not tell).
    public string CausedText { get; init; } = string.Empty;
    public bool HasCaused => CausedText.Length > 0;

    // The fixes: one recommended (drawn as the accent button), the others, and the rest under "More".
    public CheckFix? Recommended { get; init; }
    public bool HasRecommended => Recommended != null;
    public List<CheckFix> Fixes { get; init; } = [];
    public bool HasFixes => Fixes.Count > 0;
    public List<CheckFix> MoreFixes { get; init; } = [];
    public bool HasMoreFixes => MoreFixes.Count > 0;
    public bool HasAnyFix => HasRecommended || HasFixes || HasMoreFixes;
    // The recommended fix first, then the others (one row of buttons).
    public IReadOnlyList<CheckFix> MainFixes => mainFixes ??= Recommended == null ? Fixes : [Recommended, .. Fixes];
    private IReadOnlyList<CheckFix>? mainFixes;
    // "Open 'A10 - DMS Down' (Throttle · BTN 9)": one per relation, the causing actions marked there.
    public List<CheckFix> OpenButtons { get; init; } = [];
    public bool HasOpenButtons => OpenButtons.Count > 0;

    // The details (collapsed): how sure JoyPro is and why, and a technical line where one helps support.
    public List<string> DetailLines { get; init; } = [];
    // Where an unknown command came from, with the game's own id (for a support question; the only place an id shows).
    public string TechnicalLine { get; init; } = string.Empty;
    public bool HasTechnicalLine => TechnicalLine.Length > 0;
    public bool HasDetails => DetailLines.Count > 0 || TechnicalLine.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsHost))]
    private bool isDetailsOpen;

    // The card while its details are open (the panel builds them only then).
    public CheckIssueCard? DetailsHost => IsDetailsOpen ? this : null;

    // Brought by the last import or take-over.
    public bool IsNew { get; init; }

    // A header above the card when it starts a new group of the chosen order ("A10 - DMS Down", "F-16C Block 50").
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroupHeader))]
    private string groupHeader = string.Empty;

    public bool HasGroupHeader => GroupHeader.Length > 0;

    // For filter, order and groups.
    public string Game { get; init; } = string.Empty;
    public string RelationsSort { get; init; } = string.Empty;
    public string AircraftSort { get; init; } = string.Empty;
    // The group of the order "By relation" / "By aircraft" (the first relation, the first aircraft as people say it).
    public string RelationGroup { get; init; } = string.Empty;
    public string AircraftGroup { get; init; } = string.Empty;
    public string SearchText { get; init; } = string.Empty;
    // What the section's breakdown counts it under (a plain error: its device, e.g. "Keyboard"); empty: not counted.
    public string GroupLabel { get; init; } = string.Empty;
    public List<string> Keys { get; init; } = [];
    public List<string> Relations { get; init; } = [];
    public string Suggested { get; init; } = string.Empty;
    public bool SuggestOff { get; init; }
    public int Confidence { get; init; }
    // "92% sure: keep it on Throttle · BTN 9" (also in the details; empty: no suggestion).
    public string SuggestionText { get; init; } = string.Empty;
    public bool HasSuggestion => SuggestionText.Length > 0;
    public string ReasonText { get; init; } = string.Empty;

    // "Keep only on ..." per relation (a duplicate card's choices, also among Fixes).
    public List<DuplicateRelationChoice> Choices { get; init; } = [];
    public bool HasChoices => Choices.Count > 0;
    public ICommand? KeepBothCommand { get; init; }
    public ICommand? SwitchOffAllCommand { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlainShowsButtonsLine))]
    [NotifyPropertyChangedFor(nameof(PlainShowsSentence))]
    [NotifyPropertyChangedFor(nameof(PlainShowsButtons))]
    [NotifyPropertyChangedFor(nameof(PlainShowsOpenMenu))]
    private bool isExpanded = true;

    // The light card: its fixes and Open buttons in one row (with more than two relations one "Open" button with a
    // menu of them, made when it opens); what shows while it is open.
    public IReadOnlyList<CheckFix> PlainButtons => plainButtons ??= OpenButtons.Count <= 2 ? [.. Fixes, .. OpenButtons] : Fixes;
    private IReadOnlyList<CheckFix>? plainButtons;
    public bool PlainShowsButtonsLine => IsExpanded && HasButtonsLine;
    public bool PlainShowsSentence => IsExpanded && HasSentence;
    public bool PlainShowsButtons => IsExpanded && PlainButtons.Count > 0;
    public bool PlainShowsOpenMenu => IsExpanded && OpenButtons.Count > 2;
}

// A button of a card: its words, what it does in a tooltip, and the command.
public sealed class CheckFix
{
    public string Text { get; init; } = string.Empty;
    public string Tip { get; init; } = string.Empty;
    public ICommand? Command { get; init; }
    public bool IsRecommended { get; init; }
    // An "Open '<relation>'" button (drawn as a link; the light card shows it with an arrow).
    public bool IsOpen { get; init; }
    public string Label => IsOpen ? "→ " + Text : Text;
    public string RecommendedLabel => IsRecommended ? "✓ " + Text : Text;
}

// One row of a card's table: the aircraft (or game) and its command(s); an unknown command says so.
public sealed class CheckTableRow
{
    public string Left { get; init; } = string.Empty;
    public string Right { get; init; } = string.Empty;
    public bool IsUnknown { get; init; }
}

// A card of the check's own lines (errors, notes): drawn with a light template of its own (CheckProblemsPanel.axaml) -
// thousands of them can come.
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
