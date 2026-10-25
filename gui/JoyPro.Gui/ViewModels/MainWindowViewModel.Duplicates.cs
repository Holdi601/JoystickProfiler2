using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The check's problems on the Export page (Views/Controls/CheckProblemsPanel.axaml): sections by kind with a count and
// what they mean for the game, a search, a filter, sorting, collapse/expand; and for "actions in several relations"
// (engine relations duplicates) one card per relation pair with its actions and aircraft in words (never ids), the
// suggestion with its confidence ("92% sure: keep in 'Trim Right'") and the fixes: keep in one relation, keep both
// (saved: no warning any more), open the relations, switch off in all. Fixes for many show a preview with ticks first
// and apply as one profile version (one undo step): the sure ones above a threshold (saved in the settings; optionally
// right after imports and take-overs), a rule, or an instruction in words (phrase rules, the local AI model only for
// what they do not understand). Imports and take-overs say when they put an action into a second relation.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<CheckIssueSection> CheckSections { get; } = [];

    private readonly List<CheckIssueCard> duplicateCards = [];
    private readonly HashSet<string> duplicateGroupMessages = new(StringComparer.Ordinal);
    // Keys of the duplicates the last import or take-over brought.
    private readonly HashSet<string> newDuplicateKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> duplicateConfidence = new(StringComparer.OrdinalIgnoreCase);
    // "game|plane" -> the aircraft as people call it and its seat (the engine's cockpit catalog).
    private readonly Dictionary<string, (string Name, string Seat)> duplicatePlaneNames = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private string checkSearchText = string.Empty;

    [ObservableProperty]
    private DuplicateRuleChoice? selectedCheckKind;

    [ObservableProperty]
    private DuplicateRuleChoice? selectedCheckSort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDuplicateSuggestions))]
    [NotifyPropertyChangedFor(nameof(ResolveDuplicatesText))]
    private int duplicateSuggestionCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDuplicates))]
    [NotifyPropertyChangedFor(nameof(FixSureOnesText))]
    private int duplicateActionCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FixSureOnesText))]
    [NotifyPropertyChangedFor(nameof(HasSureDuplicates))]
    private int sureDuplicateCount;

    // "Fix automatically when JoyPro is at least X% sure" (Settings: saved).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoFixThresholdText))]
    [NotifyPropertyChangedFor(nameof(FixSureOnesText))]
    private int duplicateAutoFixThreshold = 90;

    // Fix the sure ones right after imports and take-overs (Settings: saved, off at first).
    [ObservableProperty]
    private bool autoFixDuplicatesAfterImport;

    public bool HasDuplicateSuggestions => DuplicateSuggestionCount > 0;
    public bool HasDuplicates => DuplicateActionCount > 0;
    public bool HasSureDuplicates => SureDuplicateCount > 0;
    public string ResolveDuplicatesText => T("Resolve as suggested ({0})", DuplicateSuggestionCount);
    public string AutoFixThresholdText => T("Fix automatically when JoyPro is at least {0}% sure", DuplicateAutoFixThreshold);
    public string FixSureOnesText => T("Fix the sure ones ({0} of {1} at {2}% or more)", SureDuplicateCount, DuplicateActionCount, DuplicateAutoFixThreshold);

    public IReadOnlyList<DuplicateRuleChoice> CheckKindChoices { get; } =
    [
        new DuplicateRuleChoice { Rule = "all", Label = N("All problems") },
        new DuplicateRuleChoice { Rule = "duplicates", Label = N("Actions in several relations") },
        new DuplicateRuleChoice { Rule = "errors", Label = N("Errors") },
        new DuplicateRuleChoice { Rule = "notes", Label = N("Notes") }
    ];

    public IReadOnlyList<DuplicateRuleChoice> CheckSortChoices { get; } =
    [
        new DuplicateRuleChoice { Rule = "relation", Label = N("By relation") },
        new DuplicateRuleChoice { Rule = "aircraft", Label = N("By aircraft") },
        new DuplicateRuleChoice { Rule = "game", Label = N("By game") },
        new DuplicateRuleChoice { Rule = "confidence", Label = N("By confidence") }
    ];

    public IReadOnlyList<DuplicateRuleChoice> DuplicateRuleChoices { get; } =
    [
        new DuplicateRuleChoice { Rule = "name", Label = N("Keep it in the relation whose function or name matches the action") },
        new DuplicateRuleChoice { Rule = "first", Label = N("Keep it where it was first (profile history)") },
        new DuplicateRuleChoice { Rule = "device", Label = N("Keep it on a device of this kind"), NeedsDevice = true },
        new DuplicateRuleChoice { Rule = "relation", Label = N("Keep it in the relation whose name has these words"), NeedsWords = true },
        new DuplicateRuleChoice { Rule = "suggested", Label = N("Keep it where JoyPro suggests (any confidence)") }
    ];

    public IReadOnlyList<DuplicateRuleChoice> DuplicateDeviceChoices { get; } =
    [
        new DuplicateRuleChoice { Rule = "stick", Label = N("Stick") },
        new DuplicateRuleChoice { Rule = "throttle", Label = N("Throttle") },
        new DuplicateRuleChoice { Rule = "panel", Label = N("Panel or button box") },
        new DuplicateRuleChoice { Rule = "pedals", Label = N("Pedals") },
        new DuplicateRuleChoice { Rule = "collective", Label = N("Collective") }
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuleNeedsDevice))]
    [NotifyPropertyChangedFor(nameof(RuleNeedsWords))]
    private DuplicateRuleChoice? selectedDuplicateRule;

    [ObservableProperty]
    private DuplicateRuleChoice? selectedDuplicateDevice;

    [ObservableProperty]
    private string duplicateRuleWords = string.Empty;

    [ObservableProperty]
    private bool isDuplicateRuleOpen;

    [ObservableProperty]
    private bool isDuplicateAiOpen;

    [ObservableProperty]
    private string duplicateInstruction = string.Empty;

    public bool RuleNeedsDevice => SelectedDuplicateRule?.NeedsDevice == true;
    public bool RuleNeedsWords => SelectedDuplicateRule?.NeedsWords == true;

    // The preview of a fix for many: every change with a tick; Apply makes the ticked ones (one undo step).
    public ObservableCollection<DuplicateChangeItem> DuplicatePreview { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDuplicatePreview))]
    private string duplicatePreviewTitle = string.Empty;

    [ObservableProperty]
    private string duplicatePreviewNotes = string.Empty;

    public bool HasDuplicatePreview => DuplicatePreviewTitle.Length > 0;

    // The import / take-over summary line about new duplicates.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDuplicateImportNotice))]
    private string duplicateImportNotice = string.Empty;

    public bool HasDuplicateImportNotice => DuplicateImportNotice.Length > 0;

    [ObservableProperty]
    private string checkSummaryText = string.Empty;

    partial void OnCheckSearchTextChanged(string value) => BuildCheckSections();
    partial void OnSelectedCheckKindChanged(DuplicateRuleChoice? value) => BuildCheckSections();
    partial void OnSelectedCheckSortChanged(DuplicateRuleChoice? value) => BuildCheckSections();

    partial void OnDuplicateAutoFixThresholdChanged(int value)
    {
        int clamped = Math.Clamp(value, 50, 100);
        if (clamped != value)
        {
            DuplicateAutoFixThreshold = clamped;
            return;
        }
        SureDuplicateCount = duplicateConfidence.Values.Count(c => c >= clamped);
        SaveUiSettings();
    }

    partial void OnAutoFixDuplicatesAfterImportChanged(bool value) => SaveUiSettings();

    // ---------------------------------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------------------------------

    // The duplicates as cards (one more served engine call after the check).
    private async Task LoadDuplicateIssuesAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["relations", "duplicates"], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Looking for actions on in several relations..."), forceText: false, background: true);
        if (result == null || result.ExitCode != 0)
            return;
        ApplyDuplicates(result.StandardOutput);
    }

    // The engine's answer -> cards, counts, and the check's plain duplicate lines out of the notes.
    public void ApplyDuplicates(string output)
    {
        duplicateCards.Clear();
        duplicateGroupMessages.Clear();
        duplicateConfidence.Clear();
        int actions = 0, suggested = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            ReadPlaneNames(root);
            if (root.TryGetProperty("Duplicates", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement d in list.EnumerateArray())
                {
                    actions++;
                    bool hasSuggestion = ReadJsonString(d, "Suggested").Length > 0 || ReadJsonBool(d, "SuggestOff");
                    if (hasSuggestion)
                    {
                        suggested++;
                        duplicateConfidence[ReadJsonString(d, "Key")] = ReadJsonIntValue(d, "Confidence");
                    }
                }
            }
            if (root.TryGetProperty("Groups", out JsonElement groups) && groups.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement g in groups.EnumerateArray())
                {
                    duplicateGroupMessages.Add(ReadJsonString(g, "Message"));
                    duplicateCards.Add(BuildDuplicateCard(g));
                }
            }
        }
        catch (JsonException)
        {
        }
        DuplicateActionCount = actions;
        DuplicateSuggestionCount = suggested;
        SureDuplicateCount = duplicateConfidence.Values.Count(c => c >= DuplicateAutoFixThreshold);
        allValidationIssues.RemoveAll(i => i.Kind == "Warning" && duplicateGroupMessages.Contains(i.Message));
        BuildCheckSections();
    }

    private void ReadPlaneNames(JsonElement root)
    {
        if (!root.TryGetProperty("Groups", out JsonElement groups) || groups.ValueKind != JsonValueKind.Array)
            return;
        foreach (JsonElement g in groups.EnumerateArray())
        {
            string game = ReadJsonString(g, "Game");
            if (!g.TryGetProperty("PlaneNames", out JsonElement names) || names.ValueKind != JsonValueKind.Array)
                continue;
            foreach (JsonElement n in names.EnumerateArray())
                duplicatePlaneNames[game + "|" + ReadJsonString(n, "Plane")] = (ReadJsonString(n, "Name"), ReadJsonString(n, "Seat"));
        }
    }

    private static int ReadJsonIntValue(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) ? n : 0;

    private CheckIssueCard BuildDuplicateCard(JsonElement g)
    {
        string game = ReadJsonString(g, "Game");
        List<string> keys = ReadStringArray(g, "Keys");
        string suggestedRelation = ReadJsonString(g, "Suggested");
        bool suggestOff = ReadJsonBool(g, "SuggestOff");
        int confidence = ReadJsonIntValue(g, "Confidence");
        List<(string Relation, string Bind)> places = [];
        if (g.TryGetProperty("Relations", out JsonElement rel) && rel.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement r in rel.EnumerateArray())
                places.Add((ReadJsonString(r, "Relation"), DisplayText.BindLabel(ReadJsonString(r, "Device"), ReadJsonString(r, "Input"), null)));
        }
        List<(string Title, List<string> Planes)> actions = [];
        if (g.TryGetProperty("Actions", out JsonElement acts) && acts.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement a in acts.EnumerateArray())
                actions.Add((ReadJsonString(a, "Title"), ReadStringArray(a, "Planes")));
        }
        List<string> allPlanes = actions.SelectMany(a => a.Planes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string placesText = CheckIssueText.Join(places.Select(p => p.Bind.Length > 0 ? T("'{0}' ({1})", p.Relation, p.Bind) : T("'{0}' (not bound)", p.Relation)).ToList(), T("and"));
        string gameName = game.Equals("DCS", StringComparison.OrdinalIgnoreCase) ? "DCS" : DisplayText.Game(game);
        string headline = actions.Count == 1
            ? T("{0}: '{1}'", AircraftWords(game, actions[0].Planes), actions[0].Title)
            : T("{0}: {1} actions", AircraftWords(game, allPlanes), actions.Count);
        string details = T("is in {0} relations: {1}. {2} gets a bind on each of these inputs.", places.Count, placesText, gameName);
        List<string> actionLines = actions.Count == 1 ? [] : actions.Select(a => T("{0}: '{1}'", AircraftWords(game, a.Planes), a.Title)).ToList();
        string suggestion = suggestedRelation.Length > 0 ? T("{0}% sure: keep in '{1}'", confidence, suggestedRelation)
            : suggestOff ? T("{0}% sure: switch it off in all of them", confidence) : string.Empty;
        List<string> reasons = [];
        if (g.TryGetProperty("Reasons", out JsonElement rs) && rs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement r in rs.EnumerateArray())
            {
                string text = ReasonWords(ReadJsonString(r, "Code"), ReadJsonString(r, "Relation"), ReadJsonString(r, "Detail"));
                if (text.Length > 0 && !reasons.Contains(text))
                    reasons.Add(text);
            }
        }
        bool isNew = keys.Any(newDuplicateKeys.Contains);
        List<string> relationNames = places.Select(p => p.Relation).ToList();
        CheckIssueCard card = new()
        {
            Kind = "duplicates",
            Headline = headline,
            Details = details,
            ActionLines = actionLines,
            SuggestionText = suggestion,
            Confidence = confidence,
            ReasonText = string.Join(" ", reasons.Take(3)),
            IsNew = isNew,
            Game = game,
            RelationsSort = string.Join(" / ", relationNames),
            AircraftSort = string.Join(", ", allPlanes),
            SearchText = string.Join("\n", new[] { headline, details, string.Join(" ", actionLines), string.Join(" ", allPlanes) }),
            Keys = keys,
            Relations = relationNames,
            Suggested = suggestedRelation,
            SuggestOff = suggestOff,
            Choices = places.Select(p => new DuplicateRelationChoice
            {
                Relation = p.Relation,
                BindLabel = p.Bind,
                IsSuggested = p.Relation == suggestedRelation,
                KeepText = T("Keep in '{0}' only", p.Relation),
                Tip = T("The action stays on in '{0}'; the other relations keep their binds for the other aircraft and get these aircraft switched off (a tick brings them back). Ctrl+Z undoes it.", p.Relation),
                KeepCommand = new AsyncRelayCommand(() => KeepDuplicatesInAsync(keys, p.Relation))
            }).ToList(),
            KeepBothCommand = new AsyncRelayCommand(() => KeepBothDuplicatesAsync(keys)),
            OpenAllCommand = new RelayCommand(() => OpenRelations(relationNames)),
            SwitchOffAllCommand = new AsyncRelayCommand(() => SwitchOffDuplicatesAsync(keys))
        };
        return card;
    }

    // "AH-64D Block II (CPG and Pilot)", "F-14 (Pilot and RIO)", "F-16C Block 50 and F/A-18C"; other games: the game.
    internal string AircraftWords(string game, IReadOnlyList<string> planes)
    {
        if (!game.Equals("DCS", StringComparison.OrdinalIgnoreCase))
            return DisplayText.Game(game);
        List<string> parts = [];
        // The duplicates answer's names first, then the modules' names (the check's other cards), else the id split at its seat.
        IEnumerable<(string Module, string Seat)> named = planes.Select(p => duplicatePlaneNames.TryGetValue(game + "|" + p, out var n) && n.Name.Length > 0
            ? (n.Name, SeatWord(n.Seat))
            : NamedSeat(p));
        foreach (IGrouping<string, (string Module, string Seat)> module in named.GroupBy(s => s.Module, StringComparer.OrdinalIgnoreCase))
        {
            List<string> seats = module.Select(s => s.Seat).Distinct().ToList();
            if (seats.Count == 1 && seats[0].Length == 0)
            {
                parts.Add(module.Key);
                continue;
            }
            List<string> names = seats.Select(s => s.Length == 0 ? T("Pilot") : s).Distinct().OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToList();
            parts.Add(module.Key + " (" + CheckIssueText.Join(names, T("and")) + ")");
        }
        return CheckIssueText.Join(parts, T("and"));
    }

    private static string SeatWord(string seat) => seat switch
    {
        "Pilot" => T("Pilot"),
        "Operator" => T("Operator"),
        "Gunner" => T("Gunner"),
        "Loadmaster" => T("Loadmaster"),
        "Copilot" => T("Copilot"),
        "Engineer" => T("Engineer"),
        _ => seat
    };

    // The id split at its seat, the module by its readable name when the modules' names have it ("AH-64D_BLK_II_CPG" ->
    // "AH-64D BLK.II" + "CPG"); a name that already says the seat keeps no second one.
    private static (string Module, string Seat) NamedSeat(string plane)
    {
        (string module, string seat) = SplitSeat(plane);
        string name = KnownAircraftName(plane);
        if (name.Length == 0 && seat.Length > 0)
            name = KnownAircraftName(module);
        if (name.Length == 0)
            return (module, seat);
        return (name, seat.Length > 0 && name.Contains(seat, StringComparison.OrdinalIgnoreCase) ? string.Empty : seat);
    }

    private static string KnownAircraftName(string id) =>
        RelationListItem.AircraftNames.TryGetValue(id, out string? name) && !string.IsNullOrWhiteSpace(name) ? name.Trim() : string.Empty;

    private static (string Module, string Seat) SplitSeat(string plane)
    {
        int bar = plane.LastIndexOf('_');
        if (bar > 0)
        {
            string seat = plane[(bar + 1)..];
            string? name = seat.ToUpperInvariant() switch
            {
                "CPG" => "CPG",
                "PLT" => T("Pilot"),
                "WSO" => "WSO",
                "RIO" => "RIO",
                "PILOT" => T("Pilot"),
                "OPERATOR" => T("Operator"),
                "GUN" => T("Gunner"),
                "LOADMASTER" => T("Loadmaster"),
                _ => null
            };
            if (name != null)
                return (plane[..bar], name);
        }
        return (plane, string.Empty);
    }

    private static string ReasonWords(string code, string relation, string detail) => code switch
    {
        "history" => T("It was only in '{0}' before ({1}).", relation, detail),
        "history-none" => T("None of them had it before ({0}).", detail),
        "history-deliberate" => T("You added it to '{0}' yourself ({1}).", relation, detail),
        "function" => T("'{0}' stands for this function.", relation),
        "name" => T("The name '{0}' says this function.", relation),
        "other-function" => T("'{0}' stands for another function.", relation),
        "meaning" => T("Its meaning fits the other aircraft in '{0}'.", relation),
        "differs" => T("In '{0}' it means something else than for the other aircraft there.", relation),
        "position" => T("'{0}' is a button position (different functions per aircraft).", relation),
        "dismissed-pair" => T("You kept both for this pair of relations before."),
        _ => string.Empty
    };

    // The sections of the panel from the check's issues and the duplicate cards, filtered, searched and sorted.
    internal void BuildCheckSections()
    {
        string kind = SelectedCheckKind?.Rule ?? "all";
        string sort = SelectedCheckSort?.Rule ?? "relation";
        string[] words = (CheckSearchText ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(CheckIssueCard c) => words.All(w => c.SearchText.Contains(w, StringComparison.CurrentCultureIgnoreCase));
        IEnumerable<CheckIssueCard> Sorted(IEnumerable<CheckIssueCard> cards) => sort switch
        {
            "aircraft" => cards.OrderBy(c => c.AircraftSort, StringComparer.CurrentCultureIgnoreCase),
            "game" => cards.OrderBy(c => c.Game, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.RelationsSort, StringComparer.CurrentCultureIgnoreCase),
            "confidence" => cards.OrderByDescending(c => c.Confidence).ThenBy(c => c.RelationsSort, StringComparer.CurrentCultureIgnoreCase),
            _ => cards.OrderBy(c => c.RelationsSort, StringComparer.CurrentCultureIgnoreCase)
        };

        Dictionary<string, bool> expanded = CheckSections.ToDictionary(s => s.Kind, s => s.IsExpanded);
        CheckSections.Clear();
        if (kind is "all" or "duplicates")
            AddSection("duplicates", T("Actions in several relations"), T("Two inputs do the same thing in the game: usually one of them is left over."), true, Sorted(duplicateCards.Where(Matches)));
        if (kind is "all" or "duplicates")
            AddSection("copies", T("Copied relations"), T("An import made copies of a relation with the same input: they can be merged back into one."), true, Sorted(copyCards.Where(Matches)));
        if (kind is "all" or "errors")
            AddSection("errors", T("Errors"), T("The export cannot write these as they are."), true,
                Sorted(allValidationIssues.Where(i => i.Kind == "Error").Select(PlainCard).Where(Matches)));
        if (kind is "all" or "notes")
            AddSection("notes", T("Notes"), T("Harmless, worth a look."), false,
                Sorted(allValidationIssues.Where(i => i.Kind == "Warning").Select(PlainCard).Where(Matches)));
        CheckSummaryText = duplicateCards.Count == 0 && copyCards.Count == 0 && !allValidationIssues.Any() ? string.Empty
            : T("{0} actions in several relations ({1} cards), {2} errors, {3} notes", DuplicateActionCount, duplicateCards.Count,
                allValidationIssues.Count(i => i.Kind == "Error"), allValidationIssues.Count(i => i.Kind == "Warning")) +
              (copyCards.Count == 0 ? string.Empty : ", " + T("{0} copied relations", copyCards.Sum(c => c.Relations.Count - 1)));

        void AddSection(string sectionKind, string title, string severity, bool serious, IEnumerable<CheckIssueCard> cards)
        {
            CheckIssueSection section = new() { Kind = sectionKind, Title = title, Severity = severity, IsSerious = serious };
            foreach (CheckIssueCard card in cards)
                section.Cards.Add(card);
            if (section.Cards.Count == 0)
                return;
            section.IsExpanded = !expanded.TryGetValue(sectionKind, out bool open) || open;
            CheckSections.Add(section);
        }
    }

    internal CheckIssueCard PlainCard(ValidationIssueItem issue)
    {
        InputConflict? conflict = ParseInputConflict(issue.Message, allRelations.Select(r => r.Name));
        string headline = issue.Message;
        string details = string.Empty;
        if (conflict != null)
        {
            string aircraft = AircraftWords(conflict.Game, [conflict.Plane]);
            string input = DisplayText.BindLabel(conflict.Device, conflict.Input, conflict.Modifiers);
            headline = T("{0}: {1}", aircraft, input.Length > 0 ? input : DisplayText.ShortDevice(conflict.Device));
            details = T("This input is bound in {0} relations: {1}. In the game it does all of them at once.", conflict.Relations.Count,
                CheckIssueText.Join(conflict.Relations.Select(r => "'" + r + "'").ToList(), T("and")));
        }
        return new CheckIssueCard
        {
            Kind = issue.Kind == "Error" ? "errors" : "notes",
            Headline = headline,
            Details = details,
            Game = conflict?.Game ?? string.Empty,
            // The engine's line stays searchable (relation names, the aircraft id); it is not shown.
            SearchText = conflict == null ? issue.Message : headline + "\n" + details + "\n" + issue.Message,
            RelationsSort = conflict == null ? issue.Message : string.Join(" / ", conflict.Relations),
            AircraftSort = conflict?.Plane ?? issue.Message,
            Relations = conflict?.Relations.ToList() ?? [],
            OpenContextCommand = issue.Message.Contains("The Relations it was found in:", StringComparison.OrdinalIgnoreCase)
                ? new RelayCommand(() => { SelectedValidationIssue = issue; OpenIssueContext(); })
                : null
        };
    }

    internal sealed record InputConflict(string Game, string Plane, string Device, string Input, IReadOnlyList<string> Modifiers, IReadOnlyList<string> Relations);

    // The check's line for one input bound in several relations on one aircraft (engine ValidateProfile):
    // "Button Item key found in multiple Relation: <game>:<plane>§<device>§<input>[§<modifier>§<device>§<key>§<switch>]...
    // The Relations it was found in: A, B". Relation names are matched against the profile's (a name may hold ", ").
    internal static InputConflict? ParseInputConflict(string message, IEnumerable<string> knownRelations)
    {
        const string Prefix = "Button Item key found in multiple Relation: ";
        const string Found = " The Relations it was found in: ";
        string text = message ?? string.Empty;
        int found = text.IndexOf(Found, StringComparison.Ordinal);
        if (!text.StartsWith(Prefix, StringComparison.Ordinal) || found < Prefix.Length)
            return null;
        string[] parts = text[Prefix.Length..found].Split('§');
        int colon = parts[0].IndexOf(':');
        if (parts.Length < 3 || colon <= 0)
            return null;
        List<string> modifiers = [];
        for (int i = 3; i < parts.Length; i += 4)
        {
            if (parts[i].Trim().Length > 0)
                modifiers.Add(parts[i].Trim());
        }
        return new InputConflict(parts[0][..colon].Trim(), parts[0][(colon + 1)..].Trim(), parts[1].Trim(), parts[2].Trim(), modifiers,
            SplitRelationNames(text[(found + Found.Length)..], knownRelations));
    }

    // "A, B, C" -> names, joining pieces back together where the profile has a relation named "A, B".
    private static List<string> SplitRelationNames(string list, IEnumerable<string> knownRelations)
    {
        HashSet<string> known = new(knownRelations.Where(n => !string.IsNullOrEmpty(n)), StringComparer.OrdinalIgnoreCase);
        string[] pieces = list.Trim().Split(", ");
        List<string> names = [];
        for (int i = 0; i < pieces.Length; i++)
        {
            int take = 1;
            for (int n = pieces.Length - i; n > 1; n--)
            {
                if (known.Contains(string.Join(", ", pieces, i, n)))
                {
                    take = n;
                    break;
                }
            }
            string name = string.Join(", ", pieces, i, take).Trim();
            if (name.Length > 0)
                names.Add(name);
            i += take - 1;
        }
        return names;
    }

    [RelayCommand]
    private void ExpandAllCheckCards() => SetCheckExpanded(true);

    [RelayCommand]
    private void CollapseAllCheckCards() => SetCheckExpanded(false);

    private void SetCheckExpanded(bool open)
    {
        foreach (CheckIssueSection section in CheckSections)
        {
            section.IsExpanded = open;
            foreach (CheckIssueCard card in section.Cards)
                card.IsExpanded = open;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // One card's fixes (each one engine call, one undo step)
    // ---------------------------------------------------------------------------------------------------------

    private static string KeysArg(IEnumerable<string> keys) => string.Join(";", keys);

    internal async Task KeepDuplicatesInAsync(IReadOnlyList<string> keys, string relation)
    {
        EngineCommandResult? result = await RunEngineAsync(["relations", "duplicates", "--keep", relation, "--keys", KeysArg(keys)],
            includeProfile: true, mutatesProfile: true, busyLabel: N("Keeping the action in one relation..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        await AfterDuplicatesChangedAsync();
        StatusText = T("Now on in '{0}' only. Ctrl+Z undoes it.", relation);
    }

    internal async Task KeepBothDuplicatesAsync(IReadOnlyList<string> keys)
    {
        EngineCommandResult? result = await RunEngineAsync(["relations", "duplicates", "--keep-both", "--keys", KeysArg(keys)],
            includeProfile: true, mutatesProfile: true, busyLabel: N("Keeping both..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        await AfterDuplicatesChangedAsync();
        StatusText = T("Kept in both relations; the check does not warn about it any more. Ctrl+Z undoes it.");
    }

    internal async Task SwitchOffDuplicatesAsync(IReadOnlyList<string> keys)
    {
        EngineCommandResult? result = await RunEngineAsync(["relations", "duplicates", "--off", "--keys", KeysArg(keys)],
            includeProfile: true, mutatesProfile: true, busyLabel: N("Switching the action off..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        await AfterDuplicatesChangedAsync();
        StatusText = T("Switched off in every relation. Ctrl+Z undoes it.");
    }

    // Opens the first relation in the editor and puts all of them into the relation list's search.
    private void OpenRelations(IReadOnlyList<string> relations)
    {
        RelationListItem? first = relations.Select(r => allRelations.FirstOrDefault(v => string.Equals(v.Name, r, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(m => m != null);
        if (first == null)
            return;
        // Back (header, Alt+Left) returns to this Check list, scrolled to this card.
        RememberPlace();
        CurrentStage = UiStage.Relations;
        SelectedRelation = first;
        StatusText = T("Opened '{0}'. The other one: {1}", first.Name, string.Join(", ", relations.Where(r => !string.Equals(r, first.Name, StringComparison.OrdinalIgnoreCase)).Select(r => "'" + r + "'")));
    }

    private async Task AfterDuplicatesChangedAsync()
    {
        DuplicatePreview.Clear();
        DuplicatePreviewTitle = string.Empty;
        DuplicatePreviewNotes = string.Empty;
        await RefreshRelationsAsync();
        if (SelectedRelation != null)
            await ReloadBuilderRelationAsync(SelectedRelation.Name);
        await ValidateProfileAsync();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Fixes for many: preview with ticks, then apply as one version
    // ---------------------------------------------------------------------------------------------------------

    private List<string> previewBaseArgs = [];

    [RelayCommand]
    private async Task PreviewSureDuplicatesAsync()
    {
        await PreviewDuplicatesAsync(["relations", "duplicates", "--rule", "suggested", "--min-confidence", DuplicateAutoFixThreshold.ToString(CultureInfo.InvariantCulture)],
            T("Fix the sure ones: {0} changes at {1}% or more", "{0}", DuplicateAutoFixThreshold));
    }

    // Kept for the one-click button of older layouts and the tests: every suggestion.
    [RelayCommand]
    private async Task ResolveDuplicatesAsSuggestedAsync()
    {
        int count = DuplicateSuggestionCount;
        EngineCommandResult? result = await RunEngineAsync(["relations", "duplicates", "--apply-suggested"], includeProfile: true, mutatesProfile: true,
            busyLabel: N("Resolving actions on in several relations..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        await AfterDuplicatesChangedAsync();
        StatusText = T("Resolved as suggested: {0}. Export again so the game loses the second binds. Ctrl+Z undoes it.", count);
    }

    [RelayCommand]
    private void ToggleDuplicateRule()
    {
        IsDuplicateRuleOpen = !IsDuplicateRuleOpen;
        if (IsDuplicateRuleOpen)
        {
            IsDuplicateAiOpen = false;
            SelectedDuplicateRule ??= DuplicateRuleChoices[0];
            SelectedDuplicateDevice ??= DuplicateDeviceChoices[0];
        }
    }

    [RelayCommand]
    private void ToggleDuplicateAi()
    {
        IsDuplicateAiOpen = !IsDuplicateAiOpen;
        if (IsDuplicateAiOpen)
            IsDuplicateRuleOpen = false;
    }

    [RelayCommand]
    private async Task PreviewDuplicateRuleAsync()
    {
        DuplicateRuleChoice rule = SelectedDuplicateRule ?? DuplicateRuleChoices[0];
        string value = rule.Rule;
        if (rule.NeedsDevice)
            value = "device:" + (SelectedDuplicateDevice ?? DuplicateDeviceChoices[0]).Rule;
        else if (rule.NeedsWords)
        {
            if (string.IsNullOrWhiteSpace(DuplicateRuleWords))
            {
                StatusText = T("Type words of the relation names first.");
                return;
            }
            value = "relation:" + DuplicateRuleWords.Trim();
        }
        await PreviewDuplicatesAsync(["relations", "duplicates", "--rule", value], T("{0} changes by the rule: {1}", "{0}", T(rule.Label)));
    }

    [RelayCommand]
    private async Task PreviewDuplicateInstructionAsync()
    {
        if (string.IsNullOrWhiteSpace(DuplicateInstruction))
        {
            StatusText = T("Describe in words where the actions belong first.");
            return;
        }
        await PreviewDuplicatesAsync(["relations", "duplicates", "--instruction", DuplicateInstruction.Trim(), "--llm", "auto"], T("{0} changes by your instruction", "{0}"));
    }

    private async Task PreviewDuplicatesAsync(List<string> args, string titleFormat)
    {
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: false,
            busyLabel: N("Preparing the preview..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        previewBaseArgs = args;
        DuplicatePreview.Clear();
        List<string> notes = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            JsonElement root = doc.RootElement;
            ReadPlaneNames(root);
            if (root.TryGetProperty("Changes", out JsonElement changes) && changes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement c in changes.EnumerateArray())
                {
                    string game = ReadJsonString(c, "Game");
                    string plane = ReadJsonString(c, "Plane");
                    string title = ReadJsonString(c, "Title");
                    string keep = ReadJsonString(c, "Keep");
                    bool off = ReadJsonBool(c, "Off");
                    string offIn = string.Join(", ", ReadStringArray(c, "SwitchOffIn").Select(r => "'" + r + "'"));
                    DuplicatePreview.Add(new DuplicateChangeItem
                    {
                        Key = ReadJsonString(c, "Key") + "=>" + (off ? "-" : keep),
                        Confidence = ReadJsonIntValue(c, "Confidence"),
                        Text = off ? T("{0} '{1}': off in {2}", AircraftWords(game, [plane]), title, offIn)
                                   : T("{0} '{1}': keep in '{2}', off in {3}", AircraftWords(game, [plane]), title, keep, offIn)
                    });
                }
            }
            foreach (string phrase in ReadStringArray(root, "Understood"))
                notes.Add(T("Understood: {0}", phrase));
            foreach (string phrase in ReadStringArray(root, "NotUnderstood"))
                notes.Add(T("Not understood: {0}", phrase));
            foreach (string warning in ReadStringArray(root, "Warnings"))
                notes.Add(Localization.Loc.Translate(warning));
        }
        catch (JsonException)
        {
        }
        DuplicatePreviewNotes = string.Join(Environment.NewLine, notes);
        DuplicatePreviewTitle = string.Format(CultureInfo.CurrentCulture, titleFormat, DuplicatePreview.Count);
        if (DuplicatePreview.Count == 0)
            StatusText = T("This fix changes nothing.");
    }

    [RelayCommand]
    private async Task ApplyDuplicatePreviewAsync()
    {
        List<string> ticked = DuplicatePreview.Where(c => c.IsTicked).Select(c => c.Key).ToList();
        if (ticked.Count == 0)
        {
            StatusText = T("Tick at least one change.");
            return;
        }
        EngineCommandResult? result = await RunEngineAsync(["relations", "duplicates", "--changes", string.Join(";", ticked)],
            includeProfile: true, mutatesProfile: true, busyLabel: N("Resolving actions on in several relations..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        await AfterDuplicatesChangedAsync();
        StatusText = T("Resolved: {0}. Export again so the game loses the second binds. Ctrl+Z undoes it.", ticked.Count);
    }

    [RelayCommand]
    private void CancelDuplicatePreview()
    {
        DuplicatePreview.Clear();
        DuplicatePreviewTitle = string.Empty;
        DuplicatePreviewNotes = string.Empty;
    }

    [RelayCommand]
    private void TickAllDuplicatePreview()
    {
        bool all = DuplicatePreview.All(c => c.IsTicked);
        foreach (DuplicateChangeItem change in DuplicatePreview)
            change.IsTicked = !all;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Imports and take-overs: say when they put an action into a second relation
    // ---------------------------------------------------------------------------------------------------------

    // The keys of the duplicates now (a served engine call; empty without a profile).
    public async Task<HashSet<string>?> DuplicateKeysAsync()
    {
        if (string.IsNullOrWhiteSpace(ProfilePath) || !System.IO.File.Exists(ProfilePath))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EngineCommandResult? result = await RunEngineAsync(["relations", "duplicates", "--keys-only"], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Looking for actions on in several relations..."), forceText: false, background: true);
        if (result == null || result.ExitCode != 0)
            return null;
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            if (doc.RootElement.TryGetProperty("Duplicates", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                foreach (JsonElement d in list.EnumerateArray())
                    keys.Add(ReadJsonString(d, "Key"));
        }
        catch (JsonException)
        {
            return null;
        }
        return keys;
    }

    // After an import or take-over: the duplicates it brought, in the summary line, fixed at once when wanted.
    public async Task ReportNewDuplicatesAsync(HashSet<string>? before, string what)
    {
        if (before == null)
            return;
        HashSet<string>? after = await DuplicateKeysAsync();
        if (after == null)
            return;
        List<string> added = after.Where(k => !before.Contains(k)).ToList();
        newDuplicateKeys.Clear();
        foreach (string key in added)
            newDuplicateKeys.Add(key);
        if (added.Count == 0)
        {
            DuplicateImportNotice = string.Empty;
            return;
        }
        int fixedCount = 0;
        if (AutoFixDuplicatesAfterImport)
        {
            EngineCommandResult? result = await RunEngineAsync(["relations", "duplicates", "--rule", "suggested", "--min-confidence", DuplicateAutoFixThreshold.ToString(CultureInfo.InvariantCulture),
                    "--keys", KeysArg(added), "--apply"],
                includeProfile: true, mutatesProfile: true, busyLabel: N("Fixing the sure ones..."), forceText: false);
            if (result != null && result.ExitCode == 0)
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
                    fixedCount = doc.RootElement.TryGetProperty("Resolved", out JsonElement r) && r.TryGetInt32(out int n) ? n : 0;
                }
                catch (JsonException)
                {
                }
                await RefreshRelationsAsync();
            }
        }
        DuplicateImportNotice = fixedCount > 0
            ? T("{0} put {1} actions into a second relation; {2} of them were fixed at once ({3}% or more sure). The rest is on the Export page under 'Problems found by the check'.", what, added.Count, fixedCount, DuplicateAutoFixThreshold)
            : T("{0} put {1} actions into a second relation: the game would get them on two inputs. Review them on the Export page under 'Problems found by the check'.", what, added.Count);
    }

    [RelayCommand]
    private async Task ReviewNewDuplicatesAsync()
    {
        DuplicateImportNotice = string.Empty;
        CurrentStage = UiStage.Export;
        await ValidateProfileAsync();
    }

    [RelayCommand]
    private void DismissDuplicateImportNotice() => DuplicateImportNotice = string.Empty;
}
