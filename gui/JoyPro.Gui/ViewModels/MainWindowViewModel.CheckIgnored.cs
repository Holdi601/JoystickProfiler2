using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The check's ignored warnings (tester wish on 2.6.0: "an add to ignore list button and a way to edit the ignore list,
// and it should reappear when it has been bound to one more place, so you can work through the warnings and see new
// ones"). "Ignore" on a card (same command on several buttons, one button does two things, one button runs two
// commands) keeps the binds and hides the warning; the engine remembers it in the profile (EngineIgnoredWarnings) and
// shows it again when one more button, relation or command joins it, marked "Shown again: now also on ..." and new.
// "Ignored warnings (n)" on the Check opens the list of the hidden ones in the cards' words, each with "Show again", and
// "Show all again". Every change is one engine call, so one undo step. The check's search finds the list by what
// people call it ("ignore list", "hidden warnings", "dismissed", "muted").
public sealed partial class MainWindowViewModel
{
    // The ignored ones of the check's last run (built with their sections, off the UI thread).
    private readonly List<CheckIssueCard> ignoredDuplicateCards = [];
    private readonly List<CheckIssueCard> ignoredConflictCards = [];

    // The list as shown (filtered by the check's search while it has words that are not about the list itself).
    public ObservableCollection<CheckIssueCard> IgnoredWarnings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIgnoredWarnings))]
    [NotifyPropertyChangedFor(nameof(IgnoredWarningsText))]
    private int ignoredWarningCount;

    [ObservableProperty]
    private bool isIgnoredWarningsOpen;

    // "2 of 5 match the search" while the search filters the list (empty: all shown).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIgnoredFilterText))]
    private string ignoredFilterText = string.Empty;

    public bool HasIgnoredWarnings => IgnoredWarningCount > 0;
    public bool HasIgnoredFilterText => IgnoredFilterText.Length > 0;
    public string IgnoredWarningsText => T("Ignored warnings ({0})", IgnoredWarningCount);

    // Words of a search for the list itself, by stem: "ignore list", "hidden warnings", "dismissed", "muted", "keep both".
    private static readonly string[] IgnoredSearchStems =
        ["ignor", "hidden", "hide", "dismiss", "mute", "muting", "silenc", "suppress", "snooz", "whitelist", "allowlist", "exception", "kept", "accepted"];

    private static readonly HashSet<string> IgnoredSearchFiller = new(StringComparer.OrdinalIgnoreCase)
    {
        "list", "lists", "warning", "warnings", "problem", "problems", "the", "my", "show", "keep", "both", "again", "on", "purpose", "all", "of", "card", "cards"
    };

    // A search for the list: every word is one of its words (a stem above or a word of its buttons in the language
    // shown), and at least one is not filler.
    internal static bool IsIgnoredWarningsSearch(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
            return false;
        // The button's name before 2.6.1.
        if (string.Join(" ", words).Contains("keep both", StringComparison.OrdinalIgnoreCase))
            return true;
        string own = string.Join(" ", T("Ignored warnings ({0})", string.Empty), T("Ignore"), T("Show again"), T("Show all again"));
        bool Strong(string w) => IgnoredSearchStems.Any(s => w.StartsWith(s, StringComparison.OrdinalIgnoreCase)) ||
                                 (w.Length >= 3 && own.Contains(w, StringComparison.CurrentCultureIgnoreCase) && !IgnoredSearchFiller.Contains(w));
        return words.Any(Strong) && words.All(w => Strong(w) || IgnoredSearchFiller.Contains(w));
    }

    // After the check's sections: the list's count and its cards (conflicts first, like the sections).
    private void UpdateIgnoredWarnings(IReadOnlyList<string> words)
    {
        string sort = CheckSortRule;
        List<CheckIssueCard> all = [.. SortCheckCards(ignoredConflictCards, sort), .. SortCheckCards(ignoredDuplicateCards, sort)];
        IgnoredWarningCount = all.Count;
        if (all.Count == 0)
            IsIgnoredWarningsOpen = false;
        bool aboutList = IsIgnoredWarningsSearch(words);
        if (aboutList && all.Count > 0)
            IsIgnoredWarningsOpen = true;
        List<CheckIssueCard> shown = aboutList || words.Count == 0
            ? all
            : all.Where(c => words.All(w => c.SearchText.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        IgnoredFilterText = shown.Count == all.Count ? string.Empty : T("{0} of {1} match the search", shown.Count, all.Count);
        if (shown.SequenceEqual(IgnoredWarnings))
            return;
        IgnoredWarnings.Clear();
        foreach (CheckIssueCard card in shown)
            IgnoredWarnings.Add(card);
    }

    [RelayCommand]
    private void ToggleIgnoredWarnings() => IsIgnoredWarningsOpen = HasIgnoredWarnings && !IsIgnoredWarningsOpen;

    // An ignored warning in the list: the card's words, no fixes, "Show again".
    private CheckIssueCard IgnoredCard(string kind, string severity, string title, string buttons, string sentence, List<CheckTableRow> rows, string rowsLeft, string rowsRight,
        List<string> keys, string game, List<string> relations, List<string> planes) => new()
    {
        Kind = kind,
        Severity = severity,
        Title = title,
        ButtonsLine = buttons,
        Sentence = sentence,
        Rows = rows,
        RowsHeaderLeft = rowsLeft,
        RowsHeaderRight = rowsRight,
        ShowAgain = new CheckFix
        {
            Text = T("Show again"),
            Tip = T("The warning comes back on the check; the binds do not change. Ctrl+Z undoes it."),
            Command = new AsyncRelayCommand(() => ShowIgnoredAgainAsync(kind, keys))
        },
        Game = game,
        RelationsSort = string.Join(" / ", relations),
        AircraftSort = string.Join(", ", planes),
        RelationGroup = relations.FirstOrDefault() ?? string.Empty,
        AircraftGroup = FirstPlaneWords(game, planes),
        SearchText = string.Join("\n", new[] { title, buttons, sentence, string.Join(" ", rows.Select(r => r.Left + " " + r.Right)), string.Join(" ", planes), string.Join(" ", relations) }),
        Keys = keys,
        Relations = relations
    };

    // "Shown again: now also on Throttle · BTN 9 ('Hat Up')" / "...now also runs 'Gear Up'".
    private static string ShownAgainWords(bool runs, IReadOnlyList<string> joined)
    {
        if (joined.Count == 0)
            return T("Shown again: something new joined it since you ignored it");
        string list = CheckIssueText.Join(joined, T("and"));
        return runs ? T("Shown again: now also runs {0}", list) : T("Shown again: now also on {0}", list);
    }

    private static string IgnoredStatus() => T("Ignored. The warning comes back when something new joins it; Ignored warnings lists it. Ctrl+Z undoes it.");

    // "Show again" of one ignored warning (one undo step).
    internal async Task ShowIgnoredAgainAsync(string kind, IReadOnlyList<string> keys)
    {
        List<string> args = kind == "duplicates"
            ? ["relations", "duplicates", "--forget-kept", "--keys", KeysArg(keys)]
            : ["relations", "conflicts", "--forget-kept", "--keys", KeysArg(keys)];
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Showing the warning again..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        await AfterDuplicatesChangedAsync();
        StatusText = T("The warning is shown on the check again. Ctrl+Z undoes it.");
    }

    // "Show all again": every ignored warning (the engine forgets the conflicts' too; one undo step).
    [RelayCommand]
    private async Task ShowAllIgnoredAgainAsync()
    {
        if (!HasIgnoredWarnings)
            return;
        EngineCommandResult? result = await RunEngineAsync(["relations", "duplicates", "--forget-kept"], includeProfile: true, mutatesProfile: true,
            busyLabel: N("Showing the warnings again..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        IsIgnoredWarningsOpen = false;
        await AfterDuplicatesChangedAsync();
        StatusText = T("All ignored warnings are shown on the check again. Ctrl+Z undoes it.");
    }
}
