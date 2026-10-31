using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The check's cards in plain words (docs/ui/check.md): for a first-time PC user every card says what is wrong in game
// words (the aircraft, the buttons as device · button, the commands as the game calls them; never an id, "node" or
// "relation item key"), why it matters in the game, and what to click. Errors have their own icon and fixes like the
// warnings:
//   - "Same command on two buttons" (engine relations duplicates): the two buttons with their relations, what happens,
//     aircraft -> command, the history entry that caused it, one recommended fix, Keep only on X / Keep both / More;
//   - "One button does two things" (engine relations conflicts: an input bound in two relations that both have an
//     action of the same aircraft on) and "One button runs two commands" (a relation with two actions of one
//     aircraft): keep one, keep both, move one to another button, switch off;
//   - "Not written to the game" ("Only add" left a bind out), copied relations, the other errors and notes.
// Every card has an "Open" button per relation: the relation opens with exactly the causing actions and aircraft
// marked and scrolled into view; Back returns to the card. An unknown command (not in the aircraft's current list)
// shows the name the relation stored and where it is, with Remove / Find similar.
public sealed partial class MainWindowViewModel
{
    // The input conflicts and relations with two actions as cards (engine relations conflicts), and the validate lines
    // they stand for (those lines get no plain card of their own).
    private readonly List<CheckIssueCard> inputConflictCards = [];
    private readonly List<CheckIssueCard> actionConflictCards = [];
    private readonly HashSet<string> conflictMessages = new(StringComparer.Ordinal);

    // ---------------------------------------------------------------------------------------------------------
    // Words
    // ---------------------------------------------------------------------------------------------------------

    // "Throttle · BTN 9" (the device as people call it, the button as on the key caps), or "no button yet".
    private static string ButtonWords(string device, string input, IReadOnlyList<string>? modifiers)
    {
        string label = DisplayText.BindLabel(device, input, modifiers);
        return label.Length > 0 ? label : T("no button yet");
    }

    // "Throttle · BTN 9 ('A10 - DMS Down')"
    private static string SideWords(string button, string relation) => T("{0} ('{1}')", button, relation);

    // "'Castle Switch AFT'", or for a command the game's list no longer has: "unknown command 'Old name'".
    private static string CommandWords(string title, bool unknown)
    {
        string shown = DisplayText.ActionTitle(title);
        if (!unknown)
            return shown.Length > 0 ? "'" + shown + "'" : T("a command without a name");
        return shown.Length > 0 ? T("unknown command '{0}'", shown) : T("an unknown command without a name");
    }

    // "Caused by: Manage with AI (366 relation changes) on 21.10.2026" (the history's label and summary in words).
    internal static string CausedWords(string label, string summary, string at)
    {
        string what = Localization.Loc.Translate((label ?? string.Empty).Trim());
        if (what.Length == 0)
            return string.Empty;
        string date = (at ?? string.Empty).Trim();
        if (DateTime.TryParseExact(date.Length >= 10 ? date[..10] : date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
            date = day.ToString("d", CultureInfo.CurrentCulture);
        string more = Localization.Loc.Translate((summary ?? string.Empty).Trim());
        return more.Length > 0 && !string.Equals(more, what, StringComparison.OrdinalIgnoreCase)
            ? T("Caused by: {0} ({1}) on {2}", what, more, date)
            : T("Caused by: {0} on {1}", what, date);
    }

    // The aircraft of a game as people say them: DCS's modules with their seats, another game by its name.
    private string PlanesWords(string game, IReadOnlyList<string> planes) =>
        game.Equals("DCS", StringComparison.OrdinalIgnoreCase) && planes.Count > 0 ? AircraftWords(game, planes) : DisplayText.Game(game);

    // The first aircraft as people say it (the group of "By aircraft").
    private string FirstPlaneWords(string game, IReadOnlyList<string> planes) =>
        planes.Count == 0 ? DisplayText.Game(game) : PlanesWords(game, [planes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).First()]);

    private CheckFix OpenFix(string relation, string button, string game, IReadOnlyCollection<string> ids, IReadOnlyCollection<string> planes) => new()
    {
        Text = T("Open '{0}' ({1})", relation, button),
        Tip = T("Opens the relation with the actions of this card marked; Back returns here."),
        Command = new AsyncRelayCommand(() => OpenCheckRelationAsync(relation, game, ids, planes)),
        IsOpen = true
    };

    // ---------------------------------------------------------------------------------------------------------
    // Same command on two buttons (engine relations duplicates: one group = one card)
    // ---------------------------------------------------------------------------------------------------------

    private CheckIssueCard BuildDuplicateCard(JsonElement g, HashSet<string> newKeys)
    {
        string game = ReadJsonString(g, "Game");
        List<string> keys = ReadStringArray(g, "Keys");
        string suggestedRelation = ReadJsonString(g, "Suggested");
        bool suggestOff = ReadJsonBool(g, "SuggestOff");
        int confidence = ReadJsonIntValue(g, "Confidence");
        List<(string Relation, string Button, bool Bound)> sides = [];
        if (g.TryGetProperty("Relations", out JsonElement rel) && rel.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement r in rel.EnumerateArray())
            {
                string bind = DisplayText.BindLabel(ReadJsonString(r, "Device"), ReadJsonString(r, "Input"), null);
                sides.Add((ReadJsonString(r, "Relation"), bind.Length > 0 ? bind : T("no button yet"), bind.Length > 0));
            }
        }
        List<(string Id, string Title, List<string> Planes, List<string> Keys, bool Unknown)> actions = [];
        if (g.TryGetProperty("Actions", out JsonElement acts) && acts.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement a in acts.EnumerateArray())
                actions.Add((ReadJsonString(a, "Id"), DisplayText.ActionTitle(ReadJsonString(a, "Title")), ReadStringArray(a, "Planes"), ReadStringArray(a, "Keys"), ReadJsonBool(a, "Unknown")));
        }
        List<string> allPlanes = actions.SelectMany(a => a.Planes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        List<string> ids = actions.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // A side is named by its button; by its relation where two sides have the same words (or no button).
        string Label((string Relation, string Button, bool Bound) side) =>
            side.Bound && sides.Count(s => string.Equals(s.Button, side.Button, StringComparison.OrdinalIgnoreCase)) == 1 ? side.Button : "'" + side.Relation + "'";

        string title = sides.Count > 2 ? T("Same command on {0} buttons", sides.Count) : T("Same command on two buttons");
        string buttons = string.Join("  ↔  ", sides.Select(s => SideWords(s.Button, s.Relation)));
        string sentence = actions.Count == 1
            ? (sides.Count > 2
                ? T("In {0}, all {1} buttons do {2}. Usually only one of them is wanted.", PlanesWords(game, actions[0].Planes), sides.Count, CommandWords(actions[0].Title, actions[0].Unknown))
                : T("In {0}, both buttons do {1}. Usually only one of them is wanted.", PlanesWords(game, actions[0].Planes), CommandWords(actions[0].Title, actions[0].Unknown)))
            : T("These buttons send the same {0} commands to the game (aircraft below). Usually only one of them is wanted.", actions.Count);
        List<CheckTableRow> rows = actions.Select(a => new CheckTableRow { Left = PlanesWords(game, a.Planes), Right = CommandWords(a.Title, a.Unknown), IsUnknown = a.Unknown }).ToList();

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
        (string Relation, string Button, bool Bound) suggestedSide = sides.FirstOrDefault(s => string.Equals(s.Relation, suggestedRelation, StringComparison.OrdinalIgnoreCase));
        string suggestion = suggestedSide.Relation != null ? T("{0}% sure: keep it only on {1}", confidence, Label(suggestedSide))
            : suggestOff ? T("{0}% sure: switch it off on all of these buttons", confidence) : string.Empty;

        List<DuplicateRelationChoice> choices = sides.Select(s => new DuplicateRelationChoice
        {
            Relation = s.Relation,
            BindLabel = s.Bound ? s.Button : string.Empty,
            IsSuggested = string.Equals(s.Relation, suggestedRelation, StringComparison.OrdinalIgnoreCase),
            KeepText = T("Keep only on {0}", Label(s)),
            Tip = T("The command stays on {0} ('{1}'); on the other buttons these aircraft get it switched off (a tick brings it back). Ctrl+Z undoes it.", s.Button, s.Relation),
            KeepCommand = new AsyncRelayCommand(() => KeepDuplicatesInAsync(keys, s.Relation))
        }).ToList();
        CheckFix keepBoth = new()
        {
            Text = T("Keep both"),
            Tip = T("Both buttons keep it; JoyPro remembers this in your profile and does not show it again. Ctrl+Z undoes it."),
            Command = new AsyncRelayCommand(() => KeepBothDuplicatesAsync(keys))
        };
        CheckFix switchOff = new()
        {
            Text = sides.Count > 2 ? T("Switch it off on all {0} buttons", sides.Count) : T("Switch it off on both buttons"),
            Tip = T("None of these relations keeps it for these aircraft (a tick brings it back). Ctrl+Z undoes it."),
            Command = new AsyncRelayCommand(() => SwitchOffDuplicatesAsync(keys)),
            IsRecommended = suggestOff
        };
        DuplicateRelationChoice? recommendedChoice = choices.FirstOrDefault(c => c.IsSuggested);
        CheckFix? recommended = recommendedChoice != null
            ? new CheckFix { Text = recommendedChoice.KeepText, Tip = recommendedChoice.Tip, Command = recommendedChoice.KeepCommand, IsRecommended = true }
            : suggestOff ? switchOff : null;
        List<CheckFix> fixes = choices.Where(c => !ReferenceEquals(c, recommendedChoice)).Select(c => new CheckFix { Text = c.KeepText, Tip = c.Tip, Command = c.KeepCommand }).ToList();
        fixes.Add(keepBoth);
        List<CheckFix> more = [];
        if (!suggestOff)
            more.Add(switchOff);
        AddUnknownFixes(more, game, actions.Where(a => a.Unknown).Select(a => (a.Title, a.Keys)).ToList(), sides.Select(s => s.Relation).FirstOrDefault() ?? string.Empty);

        List<string> details = [];
        if (suggestion.Length > 0)
            details.Add(suggestion);
        details.AddRange(reasons.Take(4));
        string caused = CausedWords(ReadJsonString(g, "CausedBy"), ReadJsonString(g, "CausedSummary"), ReadJsonString(g, "CausedAt"));
        List<string> relationNames = sides.Select(s => s.Relation).ToList();
        return new CheckIssueCard
        {
            Kind = "duplicates",
            Severity = "warning",
            Title = title,
            ButtonsLine = buttons,
            Sentence = sentence,
            Rows = rows,
            RowsHeaderLeft = T("Aircraft"),
            RowsHeaderRight = T("Command"),
            CausedText = caused,
            Recommended = recommended,
            Fixes = fixes,
            MoreFixes = more,
            OpenButtons = sides.Select(s => OpenFix(s.Relation, s.Button, game, ids, allPlanes)).ToList(),
            DetailLines = details,
            TechnicalLine = UnknownTechnicalLine(game, actions.Where(a => a.Unknown).Select(a => (a.Id, a.Title, a.Planes)).ToList(), relationNames),
            SuggestionText = suggestion,
            ReasonText = string.Join(" ", reasons.Take(3)),
            Confidence = confidence,
            IsNew = keys.Any(newKeys.Contains),
            Game = game,
            RelationsSort = string.Join(" / ", relationNames),
            AircraftSort = string.Join(", ", allPlanes),
            RelationGroup = relationNames.FirstOrDefault() ?? string.Empty,
            AircraftGroup = FirstPlaneWords(game, allPlanes),
            SearchText = string.Join("\n", new[] { title, buttons, sentence, caused, string.Join(" ", rows.Select(r => r.Left + " " + r.Right)), string.Join(" ", allPlanes), string.Join(" ", relationNames) }),
            Keys = keys,
            Relations = relationNames,
            Suggested = suggestedRelation,
            SuggestOff = suggestOff,
            Choices = choices,
            KeepBothCommand = keepBoth.Command,
            SwitchOffAllCommand = switchOff.Command
        };
    }

    // An unknown command: "Remove the unknown command" takes it out of every relation (one undo step), "Find similar"
    // opens the relation with the stored name in the action search.
    private void AddUnknownFixes(List<CheckFix> more, string game, List<(string Title, List<string> Keys)> unknown, string relation)
    {
        if (unknown.Count == 0)
            return;
        List<string> keys = unknown.SelectMany(u => u.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        more.Add(new CheckFix
        {
            Text = unknown.Count == 1 ? T("Remove the unknown command") : T("Remove the {0} unknown commands", unknown.Count),
            Tip = T("The game's current list has no such command for these aircraft any more. JoyPro takes it out of every relation. Ctrl+Z undoes it."),
            Command = new AsyncRelayCommand(() => RemoveUnknownActionsAsync(keys))
        });
        string name = unknown.Select(u => u.Title).FirstOrDefault(t => t.Length > 0) ?? string.Empty;
        if (name.Length > 0 && relation.Length > 0)
        {
            more.Add(new CheckFix
            {
                Text = T("Find similar to '{0}'", name),
                Tip = T("Opens the relation and searches the game's list for commands like it."),
                Command = new AsyncRelayCommand(() => FindSimilarActionAsync(relation, name))
            });
        }
    }

    // "Where it came from" of unknown commands, for the details: "'Old name' (DCS · F-14BU · 'Radar Lock', game id d3097...)".
    private string UnknownTechnicalLine(string game, List<(string Id, string Title, List<string> Planes)> unknown, IReadOnlyList<string> relations)
    {
        if (unknown.Count == 0)
            return string.Empty;
        return T("Unknown commands (not in the game's current list): {0}", string.Join("; ", unknown.Select(u =>
            T("{0} in {1} · {2} · {3}, game id {4}", u.Title.Length > 0 ? "'" + u.Title + "'" : T("no name"), DisplayText.Game(game), PlanesWords(game, u.Planes),
                string.Join(", ", relations.Select(r => "'" + r + "'")), u.Id))));
    }

    // ---------------------------------------------------------------------------------------------------------
    // One button does two things / one button runs two commands (engine relations conflicts)
    // ---------------------------------------------------------------------------------------------------------

    private async Task LoadConflictIssuesAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["relations", "conflicts"], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Looking at buttons that do two things..."), forceText: false, background: true);
        string output = result == null || result.ExitCode != 0 ? string.Empty : result.StandardOutput;
        string sort = CheckSortRule;
        (List<CheckIssueCard> Inputs, List<CheckIssueCard> Actions, HashSet<string> Messages) data = await Task.Run(() => ParseConflicts(output));
        ApplyConflictData(data, sort);
    }

    public void ApplyConflicts(string output) => ApplyConflictData(ParseConflicts(output), CheckSortRule);

    private void ApplyConflictData((List<CheckIssueCard> Inputs, List<CheckIssueCard> Actions, HashSet<string> Messages) data, string sort)
    {
        inputConflictCards.Clear();
        inputConflictCards.AddRange(data.Inputs);
        actionConflictCards.Clear();
        actionConflictCards.AddRange(data.Actions);
        conflictMessages.Clear();
        conflictMessages.UnionWith(data.Messages);
        checkPresortedCards["input-conflicts"] = (sort, SortCheckCards(inputConflictCards, sort));
        checkPresortedCards["action-conflicts"] = (sort, SortCheckCards(actionConflictCards, sort));
        checkDataVersion++;
        BuildCheckSections();
    }

    private sealed class ConflictSide
    {
        public string Relation = string.Empty;
        public List<(string Id, string Title, bool Unknown)> Actions = [];
    }

    // Reads the answer; one card per input (with its modifiers) and set of relations, the aircraft in its table.
    private (List<CheckIssueCard> Inputs, List<CheckIssueCard> Actions, HashSet<string> Messages) ParseConflicts(string output)
    {
        List<CheckIssueCard> inputs = [];
        List<CheckIssueCard> actions = [];
        HashSet<string> messages = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(output))
            return (inputs, actions, messages);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            ReadPlaneNameList(root, "PlaneNames", "DCS");
            List<(string Key, string Game, string Plane, string Device, string Input, List<string> Modifiers, List<ConflictSide> Sides)> found = [];
            if (root.TryGetProperty("InputConflicts", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement c in list.EnumerateArray())
                {
                    messages.Add(ReadJsonString(c, "Message"));
                    List<ConflictSide> sides = [];
                    if (c.TryGetProperty("Sides", out JsonElement ss) && ss.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement s in ss.EnumerateArray())
                            sides.Add(new ConflictSide { Relation = ReadJsonString(s, "Relation"), Actions = ReadConflictActions(s) });
                    found.Add((ReadJsonString(c, "Key"), ReadJsonString(c, "Game"), ReadJsonString(c, "Plane"), ReadJsonString(c, "Device"), ReadJsonString(c, "Input"),
                        ReadStringArray(c, "Modifiers"), sides));
                }
            }
            foreach (var group in found.GroupBy(f => f.Game + "|" + f.Device + "|" + f.Input + "|" + string.Join("+", f.Modifiers) + "|" + string.Join("\t", f.Sides.Select(s => s.Relation)), StringComparer.OrdinalIgnoreCase))
                inputs.Add(BuildInputConflictCard(group.ToList()));
            List<(string Key, string Game, string Plane, string Relation, string Device, string Input, List<string> Modifiers, List<(string Id, string Title, bool Unknown)> Actions)> twice = [];
            if (root.TryGetProperty("ActionConflicts", out JsonElement alist) && alist.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement c in alist.EnumerateArray())
                {
                    messages.Add(ReadJsonString(c, "Message"));
                    twice.Add((ReadJsonString(c, "Key"), ReadJsonString(c, "Game"), ReadJsonString(c, "Plane"), ReadJsonString(c, "Relation"), ReadJsonString(c, "Device"),
                        ReadJsonString(c, "Input"), ReadStringArray(c, "Modifiers"), ReadConflictActions(c)));
                }
            }
            foreach (var group in twice.GroupBy(t => t.Relation + "|" + string.Join("\t", t.Actions.Select(a => a.Id).OrderBy(i => i, StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase))
                actions.Add(BuildActionConflictCard(group.ToList()));
        }
        catch (JsonException)
        {
        }
        return (inputs, actions, messages);
    }

    private static List<(string Id, string Title, bool Unknown)> ReadConflictActions(JsonElement item)
    {
        List<(string Id, string Title, bool Unknown)> list = [];
        if (item.TryGetProperty("Actions", out JsonElement acts) && acts.ValueKind == JsonValueKind.Array)
            foreach (JsonElement a in acts.EnumerateArray())
                list.Add((ReadJsonString(a, "Id"), DisplayText.ActionTitle(ReadJsonString(a, "Title")), ReadJsonBool(a, "Unknown")));
        return list;
    }

    private static string CommandsWords(IReadOnlyList<(string Id, string Title, bool Unknown)> actions) =>
        CheckIssueText.Join(NumberedCommands(actions), T("and"));

    // Each action in words; two different commands with the same title are told apart ("'Pitch axis' (1)", "(2)").
    private static List<string> NumberedCommands(IReadOnlyList<(string Id, string Title, bool Unknown)> actions)
    {
        List<string> words = actions.Select(a => CommandWords(a.Title, a.Unknown)).ToList();
        foreach (IGrouping<string, int> same in Enumerable.Range(0, words.Count).GroupBy(i => words[i], StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList())
        {
            int n = 1;
            foreach (int i in same)
                words[i] = T("{0} ({1})", words[i], n++);
        }
        return words;
    }

    private CheckIssueCard BuildInputConflictCard(List<(string Key, string Game, string Plane, string Device, string Input, List<string> Modifiers, List<ConflictSide> Sides)> group)
    {
        var first = group[0];
        string game = first.Game;
        string button = ButtonWords(first.Device, first.Input, first.Modifiers);
        List<string> relations = first.Sides.Select(s => s.Relation).ToList();
        List<string> keys = group.Select(g => g.Key).ToList();
        List<string> planes = group.Select(g => g.Plane).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string title = relations.Count > 2 ? T("One button does {0} things", relations.Count) : T("One button does two things");
        string buttons = T("{0} in {1}", button, CheckIssueText.Join(relations.Select(r => "'" + r + "'").ToList(), T("and")));
        string sentence;
        if (group.Count == 1)
            sentence = T("In {0}, one press runs {1} at the same time.", PlanesWords(game, [first.Plane]),
                CheckIssueText.Join(first.Sides.Select(s => CommandsWords(s.Actions)).ToList(), T("and")));
        else
            sentence = T("In {0} aircraft one press runs {1} commands at the same time (list below).", planes.Count, relations.Count);
        List<CheckTableRow> rows = group.Select(g => new CheckTableRow
        {
            Left = PlanesWords(game, [g.Plane]),
            Right = string.Join("  +  ", g.Sides.Select(s => CommandsWords(s.Actions))),
            IsUnknown = g.Sides.Any(s => s.Actions.Any(a => a.Unknown))
        }).ToList();
        // Rows of the same commands in several aircraft are one row ("F-16C Block 50 and F/A-18C").
        rows = rows.GroupBy(r => r.Right).Select(r => new CheckTableRow
        {
            Left = r.Count() == 1 ? r.First().Left : PlanesWords(game, group.Where(g => string.Join("  +  ", g.Sides.Select(s => CommandsWords(s.Actions))) == r.Key).Select(g => g.Plane).ToList()),
            Right = r.Key,
            IsUnknown = r.First().IsUnknown
        }).ToList();
        List<CheckFix> fixes = relations.Select(r => new CheckFix
        {
            Text = T("Keep only '{0}' on this button", r),
            Tip = T("The other relations switch these aircraft off, so the button only runs '{0}'. Ctrl+Z undoes it.", r),
            Command = new AsyncRelayCommand(() => FixConflictsAsync(["--keep", r], keys, T("The button now runs only '{0}' for these aircraft. Ctrl+Z undoes it.", r)))
        }).ToList();
        fixes.Add(new CheckFix
        {
            Text = T("Keep both (it is intended)"),
            Tip = T("Both stay on this button; JoyPro remembers it in your profile and does not show it again. Ctrl+Z undoes it."),
            Command = new AsyncRelayCommand(() => FixConflictsAsync(["--keep-both"], keys, T("Kept both; the check does not show it again. Ctrl+Z undoes it.")))
        });
        List<CheckFix> more = relations.Select(r => new CheckFix
        {
            Text = T("Move '{0}' to another button", r),
            Tip = T("Opens the move dialog: press the new button for these aircraft."),
            Command = new RelayCommand(() => OpenMoveForConflict(r, game, first.Sides.First(s => s.Relation == r).Actions.Select(a => (a.Id, a.Title)).ToList(), planes))
        }).ToList();
        more.Add(new CheckFix
        {
            Text = T("Switch off all of them for these aircraft"),
            Tip = T("None of these relations keeps an action for these aircraft on this button (a tick brings it back). Ctrl+Z undoes it."),
            Command = new AsyncRelayCommand(() => FixConflictsAsync(["--off"], keys, T("Switched off for these aircraft. Ctrl+Z undoes it.")))
        });
        List<(string Title, List<string> Keys)> unknown = group.SelectMany(g => g.Sides.SelectMany(s => s.Actions.Where(a => a.Unknown).Select(a => (a.Title, Key: game + "|" + a.Id + "|" + g.Plane))))
            .GroupBy(u => u.Title).Select(u => (u.Key, u.Select(x => x.Key).ToList())).ToList();
        AddUnknownFixes(more, game, unknown, relations.FirstOrDefault() ?? string.Empty);
        List<string> unknownIds = group.SelectMany(g => g.Sides.SelectMany(s => s.Actions.Where(a => a.Unknown))).Select(a => a.Id).Distinct().ToList();
        return new CheckIssueCard
        {
            Kind = "input-conflicts",
            Severity = "error",
            Title = title,
            ButtonsLine = buttons,
            Sentence = sentence,
            Rows = rows,
            RowsHeaderLeft = T("Aircraft"),
            RowsHeaderRight = T("Commands on this button"),
            Fixes = fixes,
            MoreFixes = more,
            OpenButtons = first.Sides.Select(s => OpenFix(s.Relation, button, game, s.Actions.Select(a => a.Id).ToList(), planes)).ToList(),
            DetailLines = [T("In the game every press of this button runs each of these commands. Keep one, move one to another button, or keep both if you want it like that.")],
            TechnicalLine = unknownIds.Count == 0 ? string.Empty
                : UnknownTechnicalLine(game, group.SelectMany(g => g.Sides.SelectMany(s => s.Actions.Where(a => a.Unknown).Select(a => (a.Id, a.Title, Plane: g.Plane))))
                    .GroupBy(a => a.Id).Select(a => (a.Key, a.First().Title, a.Select(x => x.Plane).Distinct().ToList())).ToList(), relations),
            Game = game,
            GroupLabel = DisplayText.ShortDevice(first.Device),
            RelationsSort = string.Join(" / ", relations),
            AircraftSort = string.Join(", ", planes),
            RelationGroup = relations.FirstOrDefault() ?? string.Empty,
            AircraftGroup = FirstPlaneWords(game, planes),
            SearchText = string.Join("\n", new[] { title, buttons, sentence, string.Join(" ", rows.Select(r => r.Left + " " + r.Right)), string.Join(" ", planes), string.Join(" ", relations) }),
            Keys = keys,
            Relations = relations
        };
    }

    private CheckIssueCard BuildActionConflictCard(List<(string Key, string Game, string Plane, string Relation, string Device, string Input, List<string> Modifiers, List<(string Id, string Title, bool Unknown)> Actions)> group)
    {
        var first = group[0];
        string relation = first.Relation;
        string button = ButtonWords(first.Device, first.Input, first.Modifiers);
        List<string> keys = group.Select(g => g.Key).ToList();
        List<string> games = group.Select(g => g.Game).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        List<string> planes = group.Select(g => g.Plane).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string Where(IEnumerable<(string Game, string Plane)> items) => CheckIssueText.Join(items.GroupBy(i => i.Game, StringComparer.OrdinalIgnoreCase)
            .Select(i => PlanesWords(i.Key, i.Select(x => x.Plane).ToList())).ToList(), T("and"));
        string title = first.Actions.Count > 2 ? T("One button runs {0} commands", first.Actions.Count) : T("One button runs two commands");
        string buttons = SideWords(button, relation);
        string sentence = T("In {0}, '{1}' runs {2} on the same button.", Where(group.Select(g => (g.Game, g.Plane))), relation, CommandsWords(first.Actions));
        List<CheckTableRow> rows = group.GroupBy(g => g.Game, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CheckTableRow { Left = PlanesWords(g.Key, g.Select(x => x.Plane).ToList()), Right = string.Join("  +  ", NumberedCommands(g.First().Actions)), IsUnknown = g.First().Actions.Any(a => a.Unknown) })
            .ToList();
        List<string> named = NumberedCommands(first.Actions);
        List<CheckFix> fixes = first.Actions.Select((a, i) => new CheckFix
        {
            Text = T("Keep only {0}", named[i]),
            Tip = T("The other commands of '{0}' are switched off for these aircraft (a tick brings them back). Ctrl+Z undoes it.", relation),
            Command = new AsyncRelayCommand(() => FixConflictsAsync(["--keep-action", a.Id], keys, T("'{0}' now runs one command for these aircraft. Ctrl+Z undoes it.", relation)))
        }).ToList();
        fixes.Add(new CheckFix
        {
            Text = T("Keep both (it is intended)"),
            Tip = T("Both commands stay; JoyPro remembers it in your profile and does not show it again. Ctrl+Z undoes it."),
            Command = new AsyncRelayCommand(() => FixConflictsAsync(["--keep-both"], keys, T("Kept both; the check does not show it again. Ctrl+Z undoes it.")))
        });
        List<CheckFix> more =
        [
            new CheckFix
            {
                Text = T("Move one to another button"),
                Tip = T("Opens the move dialog: pick the command and press the new button."),
                Command = new RelayCommand(() => OpenMoveForConflict(relation, first.Game, first.Actions.Select(a => (a.Id, a.Title)).ToList(), planes))
            },
            new CheckFix
            {
                Text = T("Switch all of them off for these aircraft"),
                Tip = T("'{0}' keeps none of these commands for these aircraft (a tick brings them back). Ctrl+Z undoes it.", relation),
                Command = new AsyncRelayCommand(() => FixConflictsAsync(["--off"], keys, T("Switched off for these aircraft. Ctrl+Z undoes it.")))
            }
        ];
        AddUnknownFixes(more, first.Game, group.SelectMany(g => g.Actions.Where(a => a.Unknown).Select(a => (a.Title, Key: g.Game + "|" + a.Id + "|" + g.Plane)))
            .GroupBy(u => u.Title).Select(u => (u.Key, u.Select(x => x.Key).ToList())).ToList(), relation);
        return new CheckIssueCard
        {
            Kind = "action-conflicts",
            Severity = "error",
            Title = title,
            ButtonsLine = buttons,
            Sentence = sentence,
            Rows = rows,
            RowsHeaderLeft = games.Count > 1 || !games[0].Equals("DCS", StringComparison.OrdinalIgnoreCase) ? T("Game") : T("Aircraft"),
            RowsHeaderRight = T("Commands on this button"),
            Fixes = fixes,
            MoreFixes = more,
            OpenButtons = [OpenFix(relation, button, first.Game, first.Actions.Select(a => a.Id).ToList(), planes)],
            DetailLines = [T("One relation is one function on one button. When it has two commands for the same aircraft, the game runs both on every press.")],
            Game = first.Game,
            GroupLabel = DisplayText.ShortDevice(first.Device),
            RelationsSort = relation,
            AircraftSort = string.Join(", ", planes),
            RelationGroup = relation,
            AircraftGroup = FirstPlaneWords(first.Game, planes),
            SearchText = string.Join("\n", new[] { title, buttons, sentence, string.Join(" ", rows.Select(r => r.Left + " " + r.Right)), string.Join(" ", planes) }),
            Keys = keys,
            Relations = [relation]
        };
    }

    private void OpenMoveForConflict(string relation, string game, List<(string Id, string Title)> actions, List<string> planes)
    {
        string where = game.Equals("DCS", StringComparison.OrdinalIgnoreCase) ? PlanesWords(game, planes) : string.Empty;
        OpenMoveAction(relation, actions.Select(a => new MoveActionChoice(a.Title.Length > 0 ? a.Title : T("a command without a name"), game,
            BuildNodeSpec(game, a.Id, game.Equals("DCS", StringComparison.OrdinalIgnoreCase) ? string.Join(",", planes) : string.Empty), where)).ToList());
    }

    internal async Task FixConflictsAsync(IReadOnlyList<string> fix, IReadOnlyList<string> keys, string done)
    {
        List<string> args = ["relations", "conflicts", .. fix, "--keys", KeysArg(keys)];
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Fixing the button..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        await AfterDuplicatesChangedAsync();
        StatusText = done;
    }

    internal async Task RemoveUnknownActionsAsync(IReadOnlyList<string> keys)
    {
        EngineCommandResult? result = await RunEngineAsync(["relations", "conflicts", "--remove-actions", "--keys", KeysArg(keys)], includeProfile: true, mutatesProfile: true,
            busyLabel: N("Removing unknown commands..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        await AfterDuplicatesChangedAsync();
        StatusText = T("Removed the unknown commands. Ctrl+Z undoes it.");
    }

    // "Find similar": the relation opens with the action search on the stored name.
    internal async Task FindSimilarActionAsync(string relation, string name)
    {
        await OpenCheckRelationAsync(relation, string.Empty, [], []);
        SetBuilderPanel(BuilderPanelKind.Add);
        BuilderSearchText = name;
    }

    // ---------------------------------------------------------------------------------------------------------
    // The check's other lines (errors, notes) in words
    // ---------------------------------------------------------------------------------------------------------

    private static readonly Regex ModifierErrorLine = new(
        @"^Modifier error, Modifier used on craft, which also uses modifier as a regular button\. Relation: (?<relation>.*) with Joystick: (?<device>.*) with Button: (?<button>.*) and Modifier: (?<modifier>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TwoActionsLine = new(@"^The game (?<game>\S+) has multiple active items for the plane (?<plane>.+) on the Relation (?<relation>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A plain card of a line no detailed card stands for: the known engine lines in words, the rest as they are.
    private CheckIssueCard PlainLineCard(ValidationIssueItem issue, InputConflict? conflict)
    {
        bool error = issue.Kind == "Error";
        string message = issue.Message ?? string.Empty;
        if (conflict != null)
        {
            string button = ButtonWords(conflict.Device, conflict.Input, conflict.Modifiers);
            string aircraft = PlanesWords(conflict.Game, [conflict.Plane]);
            string relations = CheckIssueText.Join(conflict.Relations.Select(r => "'" + r + "'").ToList(), T("and"));
            return new PlainCheckIssueCard
            {
                Kind = "errors",
                Severity = "error",
                Title = T("One button does two things"),
                ButtonsLine = T("{0} in {1}", button, relations),
                Sentence = T("In {0}, one press runs the commands of {1} at the same time.", aircraft, relations),
                Game = conflict.Game,
                GroupLabel = DisplayText.ShortDevice(conflict.Device),
                SearchText = button + "\n" + aircraft + "\n" + relations + "\n" + message,
                RelationsSort = string.Join(" / ", conflict.Relations),
                AircraftSort = conflict.Plane,
                RelationGroup = conflict.Relations.FirstOrDefault() ?? string.Empty,
                AircraftGroup = aircraft,
                Relations = conflict.Relations.ToList(),
                OpenButtons = conflict.Relations.Select(r => OpenFix(r, button, conflict.Game, [], [conflict.Plane])).ToList()
            };
        }
        Match modifier = ModifierErrorLine.Match(message);
        if (modifier.Success)
        {
            string relation = modifier.Groups["relation"].Value.Trim();
            string button = ButtonWords(modifier.Groups["device"].Value.Trim(), modifier.Groups["button"].Value.Trim(), null);
            string name = modifier.Groups["modifier"].Value.Trim();
            return new PlainCheckIssueCard
            {
                Kind = "errors",
                Severity = "error",
                Title = T("A modifier button is also a normal button"),
                ButtonsLine = SideWords(button, relation),
                Sentence = T("This button is the modifier '{0}' and also runs '{1}': every time you hold it as a modifier, the game runs '{1}' too.", name, relation),
                GroupLabel = DisplayText.ShortDevice(modifier.Groups["device"].Value),
                SearchText = button + "\n" + name + "\n" + relation + "\n" + message,
                RelationsSort = relation,
                AircraftSort = relation,
                RelationGroup = relation,
                Relations = [relation],
                Fixes = [new CheckFix { Text = T("Move '{0}' to another button", relation), Tip = T("Opens the move dialog: press the new button."), Command = new AsyncRelayCommand(() => OpenMoveForRelationAsync(relation)) }],
                OpenButtons = [OpenFix(relation, button, string.Empty, [], [])]
            };
        }
        Match two = TwoActionsLine.Match(message);
        if (two.Success)
        {
            string relation = two.Groups["relation"].Value.Trim();
            string game = two.Groups["game"].Value.Trim();
            string plane = two.Groups["plane"].Value.Trim();
            return new PlainCheckIssueCard
            {
                Kind = "errors",
                Severity = "error",
                Title = T("One button runs two commands"),
                ButtonsLine = "'" + relation + "'",
                Sentence = T("In {0}, '{1}' runs two commands on the same button.", PlanesWords(game, [plane]), relation),
                Game = game,
                SearchText = relation + "\n" + PlanesWords(game, [plane]) + "\n" + message,
                RelationsSort = relation,
                AircraftSort = plane,
                RelationGroup = relation,
                Relations = [relation],
                OpenButtons = [OpenFix(relation, T("its button"), game, [], [plane])]
            };
        }
        return new PlainCheckIssueCard
        {
            Kind = error ? "errors" : "notes",
            Severity = error ? "error" : "note",
            Title = error ? T("Problem in the profile") : T("Worth a look"),
            Sentence = Localization.Loc.Translate(message),
            SearchText = message,
            RelationsSort = message,
            AircraftSort = message,
            OpenButtons = []
        };
    }

    private async Task OpenMoveForRelationAsync(string relation)
    {
        List<MoveActionChoice> choices = await LoadMoveActionChoicesAsync(relation);
        if (choices.Count > 0)
            OpenMoveAction(relation, choices);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Opening a relation from a card: the causing actions and aircraft marked, Back returns to the card
    // ---------------------------------------------------------------------------------------------------------

    private (string Relation, string Game, HashSet<string> Ids, HashSet<string> Planes)? pendingCheckHighlight;

    internal async Task OpenCheckRelationAsync(string relation, string game, IReadOnlyCollection<string> ids, IReadOnlyCollection<string> planes)
    {
        RelationListItem? item = allRelations.FirstOrDefault(r => string.Equals(r.Name, relation, StringComparison.Ordinal)) ??
                                 allRelations.FirstOrDefault(r => string.Equals(r.Name, relation, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            StatusText = T("The relation '{0}' is not there any more.", relation);
            return;
        }
        // Back (header, Alt+Left) returns to this Check list, scrolled to this card.
        RememberPlace();
        pendingCheckHighlight = (item.Name, game, new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase), new HashSet<string>(planes, StringComparer.OrdinalIgnoreCase));
        CurrentStage = UiStage.Relations;
        suppressRelationAutoLoad = true;
        try
        {
            SelectedRelation = item;
        }
        finally
        {
            suppressRelationAutoLoad = false;
        }
        await ShowRelationAsync(item.Name);
        ApplyCheckHighlight();
        StatusText = ids.Count > 0
            ? T("Opened '{0}': the commands of the check are marked. Back returns to the check.", item.Name)
            : T("Opened '{0}'. Back returns to the check.", item.Name);
    }

    // The relation's actions of the card get the mark (and the first one is scrolled into view); their aircraft open
    // with the card's aircraft marked. Called again when the aircraft of the actions arrive.
    internal void ApplyCheckHighlight()
    {
        if (pendingCheckHighlight is not { } mark)
            return;
        if (!string.Equals(SelectedRelation?.Name, mark.Relation, StringComparison.OrdinalIgnoreCase))
        {
            pendingCheckHighlight = null;
            return;
        }
        if (mark.Ids.Count == 0)
            return;
        RelationNodeItem? firstHit = null;
        foreach (RelationNodeItem node in RelationNodes)
        {
            bool hit = mark.Ids.Contains(node.Id) && (mark.Game.Length == 0 || string.Equals(node.Game, mark.Game, StringComparison.OrdinalIgnoreCase));
            node.IsSearchHit = hit;
            if (!hit)
                continue;
            firstHit ??= node;
            if (node.Aircraft != null)
            {
                bool any = false;
                foreach (AircraftChoiceItem plane in node.Aircraft.Items)
                {
                    plane.IsHighlighted = mark.Planes.Contains(plane.Plane);
                    any |= plane.IsHighlighted;
                }
                if (any)
                    node.Aircraft.IsExpanded = true;
            }
        }
        if (firstHit != null)
        {
            SelectedRelationNode = firstHit;
            RelationNodeScrollRequested?.Invoke(this, firstHit);
        }
    }
}
