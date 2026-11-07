using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Engine.Axis;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// The axis response's "Applies to" list (2.6.5 tester report: "I don't understand what the scope does"): instead of a
// scope dropdown, checkboxes for all games, each game of the relation and each DCS aircraft, each with what it uses today
// ("own curve", "same as ...", "uses DCS"). Only DCS writes a curve per aircraft (EngineSync resolves the other games
// without an aircraft), so only DCS has aircraft rows. A family row shows where it applies: two of its games in the
// relation, or a curve stored for it.
//
// What Save does (the save plan, said in plain words above the buttons):
// - all games, a game without aircraft rows, a family: that one scope;
// - a game with aircraft rows: by default its aircraft that use the game's curve are ticked (a fresh relation: all of
//   them; aircraft with their own curve stay unticked). Saving that set, or more, stores the GAME's curve
//   (binds tuning set --scope game:DCS); ticked aircraft with their own curve lose it in the same run (--clear-scope), and
//   the page says so before. Only a real subset gets a curve per aircraft (--scope plane:DCS:A --scope plane:DCS:B).
// Every save and every clear is one engine run: one history version, one Ctrl+Z.
// The aircraft are also listed in groups by the curve they share ("A, B: same curve", "C: uses DCS"); a click ticks a
// group. The ticks last saved are kept per relation (list-preferences.json "AxisCurveTicks") and come back when the
// relation is opened again, also after a restart.
public sealed partial class MainWindowViewModel
{
    // What the engine's binds tuning show answered last: every stored scope and per game / DCS aircraft its source.
    private Dictionary<string, string> axisTuningStored = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (string Source, bool Detent)> axisTuningUses = new(StringComparer.OrdinalIgnoreCase);
    private bool axisTuningTicking;
    // The shown row inherits a curve fitted to its game's model (binds tuning show's Resolved.Fitted for that row).
    private string axisTuningFittedScope = string.Empty;
    private bool axisTuningFitted;

    public ObservableCollection<AxisTuningGroupItem> AxisTuningGroups { get; } = [];

    public bool HasAxisTuningGroups => AxisTuningGroups.Count > 0;

    [ObservableProperty]
    private string axisTuningSaveText = string.Empty;

    [ObservableProperty]
    private string axisTuningClearText = string.Empty;

    // "Save stores it for DCS, all its aircraft (5)." / "Save stores it for these aircraft (3 of 5): A, B, C."
    [ObservableProperty]
    private string axisTuningSavePlanText = string.Empty;

    // "This replaces the own curve of SA342 Pilot." (empty: nothing is replaced)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAxisTuningReplaceText))]
    private string axisTuningReplaceText = string.Empty;

    public bool HasAxisTuningReplaceText => AxisTuningReplaceText.Length > 0;

    // What a save writes now: the scopes set, the scopes cleared in the same run, the row whose curve is shown.
    internal sealed record AxisTuningSavePlan(string Kind, AxisTuningScopeItem? Lead, IReadOnlyList<string> Set, IReadOnlyList<string> Clear,
        IReadOnlyList<AxisTuningScopeItem> Aircraft, IReadOnlyList<AxisTuningScopeItem> Replaced, int Following, int Total, IReadOnlyList<AxisTuningScopeItem> KeepOwn)
    {
        public static readonly AxisTuningSavePlan None = new("none", null, [], [], [], [], 0, 0, []);
    }

    // The ticked rows, in the list's order.
    internal List<AxisTuningScopeItem> AxisTuningTicked => AxisTuningScopes.Where(s => s.IsChecked && s.IsShown).ToList();

    private void HookAxisTuningRow(AxisTuningScopeItem item) => item.PropertyChanged += OnAxisTuningRowChanged;

    private void ClearAxisTuningRows()
    {
        foreach (AxisTuningScopeItem item in AxisTuningScopes)
            item.PropertyChanged -= OnAxisTuningRowChanged;
        AxisTuningScopes.Clear();
        AxisTuningGroups.Clear();
        OnPropertyChanged(nameof(HasAxisTuningGroups));
    }

    // ---- rows of a game with aircraft ------------------------------------------------------------------------------

    private List<AxisTuningScopeItem> AxisTuningAircraftOf(string game) =>
        AxisTuningScopes.Where(s => s.IsAircraft && s.IsShown && string.Equals(s.Game, game, StringComparison.OrdinalIgnoreCase)).ToList();

    // A game row with aircraft rows under it (DCS).
    private bool IsAxisTuningAircraftGame(AxisTuningScopeItem row) =>
        row.Scope.StartsWith("game:", StringComparison.Ordinal) && AxisTuningAircraftOf(row.Game).Count > 0;

    private AxisTuningScopeItem? AxisTuningGameRow(string game) =>
        AxisTuningScopes.FirstOrDefault(s => string.Equals(s.Scope, "game:" + game, StringComparison.OrdinalIgnoreCase));

    // The aircraft that use the game's curve (no curve of their own): ticked by default.
    private List<AxisTuningScopeItem> AxisTuningDefaultSet(string game) => AxisTuningAircraftOf(game).Where(a => !a.IsOwn).ToList();

    // Rows ticked together: a game with aircraft and its aircraft are one unit; every other row stands alone.
    private string AxisTuningUnit(AxisTuningScopeItem row) =>
        row.IsAircraft || IsAxisTuningAircraftGame(row) ? "aircraft:" + row.Game.ToUpperInvariant() : row.Scope;

    private void OnAxisTuningRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AxisTuningScopeItem.IsChecked) || sender is not AxisTuningScopeItem item || axisTuningTicking)
            return;
        axisTuningTicking = true;
        try
        {
            if (item.IsChecked)
            {
                string unit = AxisTuningUnit(item);
                foreach (AxisTuningScopeItem other in AxisTuningScopes.Where(s => s.IsChecked && !ReferenceEquals(s, item) && AxisTuningUnit(s) != unit).ToList())
                    other.IsChecked = false;
            }
            // The game row of a game with aircraft: on = its aircraft that use the game's curve, off = none of them.
            if (IsAxisTuningAircraftGame(item))
            {
                HashSet<AxisTuningScopeItem> wanted = item.IsChecked ? [.. AxisTuningDefaultSet(item.Game)] : [];
                foreach (AxisTuningScopeItem aircraft in AxisTuningAircraftOf(item.Game))
                    aircraft.IsChecked = wanted.Contains(aircraft);
            }
            SyncAxisTuningGameRows();
        }
        finally
        {
            axisTuningTicking = false;
        }
        OnAxisTuningTicksChanged();
    }

    // A game row with aircraft is ticked when its curve is what a save writes: all aircraft that use it are ticked (or,
    // when none uses it any more, the row was ticked by itself and no aircraft is).
    private void SyncAxisTuningGameRows()
    {
        bool was = axisTuningTicking;
        axisTuningTicking = true;
        try
        {
            foreach (AxisTuningScopeItem game in AxisTuningScopes.Where(IsAxisTuningAircraftGame).ToList())
            {
                List<AxisTuningScopeItem> ticked = AxisTuningAircraftOf(game.Game).Where(a => a.IsChecked).ToList();
                List<AxisTuningScopeItem> defaults = AxisTuningDefaultSet(game.Game);
                game.IsChecked = defaults.Count > 0 ? defaults.All(ticked.Contains) : game.IsChecked && ticked.Count == 0;
            }
        }
        finally
        {
            axisTuningTicking = was;
        }
    }

    // What a save writes for the ticks now.
    internal AxisTuningSavePlan AxisTuningPlan()
    {
        List<AxisTuningScopeItem> ticked = AxisTuningTicked;
        AxisTuningScopeItem? single = ticked.FirstOrDefault(t => !t.IsAircraft && !IsAxisTuningAircraftGame(t));
        if (single != null)
            return new AxisTuningSavePlan(single.IsBase ? "base" : single.IsFamily ? "family" : "game", single, [single.Scope], [], [], [], 0, 0, []);
        AxisTuningScopeItem? anyRow = ticked.FirstOrDefault();
        if (anyRow == null)
            return AxisTuningSavePlan.None;
        string gameId = anyRow.Game;
        AxisTuningScopeItem? gameRow = AxisTuningGameRow(gameId);
        List<AxisTuningScopeItem> all = AxisTuningAircraftOf(gameId);
        List<AxisTuningScopeItem> aircraft = all.Where(a => a.IsChecked).ToList();
        List<AxisTuningScopeItem> defaults = all.Where(a => !a.IsOwn).ToList();
        if (gameRow != null && gameRow.IsChecked)
        {
            // The game's curve: what the default set follows; ticked aircraft with an own curve follow it again.
            List<AxisTuningScopeItem> replaced = aircraft.Where(a => a.IsOwn).ToList();
            List<AxisTuningScopeItem> keep = all.Where(a => a.IsOwn && !a.IsChecked).ToList();
            return new AxisTuningSavePlan("gameAll", gameRow, [gameRow.Scope], replaced.Select(r => r.Scope).ToList(), aircraft, replaced,
                defaults.Count + replaced.Count, all.Count, keep);
        }
        // A real subset: a curve per ticked aircraft. Shown is the first one's; another ticked aircraft whose own curve
        // differs from it is named (one with the same curve is what the user groups on purpose).
        AxisTuningScopeItem lead = aircraft[0];
        string leadCurve = AxisTuningCurveKey(AxisTuningSourceOf(lead, axisTuningStored, axisTuningUses).Source);
        return new AxisTuningSavePlan("aircraft", lead, aircraft.Select(a => a.Scope).ToList(), [], aircraft,
            aircraft.Where(a => a.IsOwn && !ReferenceEquals(a, lead) && AxisTuningCurveKey(a.Scope) != leadCurve).ToList(), aircraft.Count, all.Count, []);
    }

    // The ticks changed: the plan's row leads. Its curve is loaded when nothing is edited or the game model changes;
    // edits made before ticking more aircraft stay (they are what will be saved for all of them).
    private void OnAxisTuningTicksChanged()
    {
        AxisTuningScopeItem? lead = AxisTuningPlan().Lead;
        AxisTuningScopeItem? previous = SelectedAxisTuningScope;
        bool reload = lead != null && !ReferenceEquals(lead, previous) &&
                      (!AxisTuningHasEdits || !AxisTuningLoaded || previous == null || !string.Equals(lead.Model.Id, previous.Model.Id, StringComparison.Ordinal) || lead.IsBase != previous.IsBase);
        SetAxisTuningLead(lead);
        if (reload)
            _ = LoadAxisTuningScopeAsync();
        else
            UpdateAxisTuningSelectionTexts();
    }

    private void SetAxisTuningLead(AxisTuningScopeItem? lead)
    {
        bool was = axisTuningTicking;
        axisTuningTicking = true;
        try
        {
            SelectedAxisTuningScope = lead;
        }
        finally
        {
            axisTuningTicking = was;
        }
    }

    // SelectedAxisTuningScope set from outside (a test, a script): that row is ticked as a click would tick it alone.
    private void TickOnlyAxisTuningRow(AxisTuningScopeItem? value)
    {
        ApplyAxisTuningTicks(value == null ? [] : [value.Scope]);
        AxisTuningScopeItem? lead = AxisTuningPlan().Lead;
        if (!ReferenceEquals(lead, value))
            SetAxisTuningLead(lead);
    }

    // Ticks these rows (scope keys) and loads the curve of the result; "game:DCS" stands for the aircraft that use DCS's
    // curve. Rows that may not be ticked together: the first one's unit wins.
    internal void TickAxisTuningRows(IEnumerable<string>? scopes)
    {
        ApplyAxisTuningTicks(scopes ?? []);
        OnAxisTuningTicksChanged();
    }

    [RelayCommand]
    private void TickAxisTuningGroup(AxisTuningGroupItem? group)
    {
        if (group != null)
            TickAxisTuningRows(group.Scopes);
    }

    private void ApplyAxisTuningTicks(IEnumerable<string> scopes)
    {
        List<AxisTuningScopeItem> rows = scopes.Select(s => AxisTuningScopes.FirstOrDefault(r => r.IsShown && string.Equals(r.Scope, s, StringComparison.OrdinalIgnoreCase)))
            .OfType<AxisTuningScopeItem>().ToList();
        string unit = rows.Count == 0 ? string.Empty : AxisTuningUnit(rows[0]);
        rows = rows.Where(r => AxisTuningUnit(r) == unit).ToList();
        bool was = axisTuningTicking;
        axisTuningTicking = true;
        try
        {
            foreach (AxisTuningScopeItem item in AxisTuningScopes)
                item.IsChecked = rows.Contains(item);
            foreach (AxisTuningScopeItem game in rows.Where(IsAxisTuningAircraftGame).ToList())
            {
                foreach (AxisTuningScopeItem aircraft in AxisTuningDefaultSet(game.Game))
                    aircraft.IsChecked = true;
            }
            SyncAxisTuningGameRows();
        }
        finally
        {
            axisTuningTicking = was;
        }
    }

    // The ticks as remembered: "game:DCS" for a game's curve, else the ticked rows' scopes.
    internal List<string> AxisTuningTickDescriptor()
    {
        AxisTuningSavePlan plan = AxisTuningPlan();
        return plan.Kind == "aircraft" ? plan.Set.ToList() : plan.Lead == null ? [] : [plan.Lead.Scope];
    }

    // Which rows to tick when a relation opens: the ones last saved for it, else a game's aircraft that use its curve,
    // else all games.
    private List<string> AxisTuningStartTicks(string relation)
    {
        if (ListPrefs.AxisCurveTicks.TryGetValue(relation, out List<string>? remembered) && remembered != null &&
            remembered.Any(s => AxisTuningScopes.Any(r => r.IsShown && string.Equals(r.Scope, s, StringComparison.OrdinalIgnoreCase))))
        {
            return remembered;
        }
        AxisTuningScopeItem? game = AxisTuningScopes.FirstOrDefault(IsAxisTuningAircraftGame);
        return game != null ? [game.Scope] : ["base"];
    }

    private void RememberAxisTuningTicks()
    {
        if (axisTuningRelation.Length == 0)
            return;
        List<string> ticks = AxisTuningTickDescriptor();
        if (ticks.Count == 0)
            return;
        ListPrefs.AxisCurveTicks[axisTuningRelation] = ticks;
        SaveListPreferences();
    }

    // ---- what each row uses ---------------------------------------------------------------------------------------

    // binds tuning show's "Scopes" and "Uses" (Uses: per game / DCS aircraft the scope its curve comes from).
    private void ReadAxisTuningShow(JsonElement root)
    {
        Dictionary<string, string> stored = new(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("Scopes", out JsonElement scopes) && scopes.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty entry in scopes.EnumerateObject())
                stored[entry.Name] = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() ?? string.Empty : string.Empty;
        }
        Dictionary<string, (string, bool)> uses = new(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("Uses", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement use in list.EnumerateArray())
            {
                string game = GetJsonString(use, "Game");
                string plane = GetJsonString(use, "Plane");
                string source = GetJsonString(use, "Source");
                if (game.Length == 0 || source.Length == 0)
                    continue;
                bool detent = use.TryGetProperty("Detent", out JsonElement d) && d.ValueKind == JsonValueKind.True;
                uses[plane.Length > 0 ? "plane:" + game + ":" + plane : "game:" + game] = (source, detent);
            }
        }
        axisTuningStored = stored;
        axisTuningUses = uses;
    }

    // Where a row's curve comes from today: the engine's answer, else the order plane, game, family, all games.
    internal static (string Source, bool Detent) AxisTuningSourceOf(AxisTuningScopeItem row, IReadOnlyDictionary<string, string> stored,
        IReadOnlyDictionary<string, (string Source, bool Detent)> uses)
    {
        if (row.IsBase)
            return ("base", false);
        if (!row.IsFamily && uses.TryGetValue(row.Scope, out (string Source, bool Detent) known))
            return known;
        if (stored.ContainsKey(row.Scope))
            return (row.Scope, false);
        if (!row.IsFamily)
        {
            if (row.IsAircraft && stored.ContainsKey("game:" + row.Game))
                return ("game:" + row.Game, false);
            string family = AxisTuningFamilies.FirstOrDefault(f => f.Games.Contains(row.Game, StringComparer.OrdinalIgnoreCase)).Family ?? string.Empty;
            if (family.Length > 0 && stored.ContainsKey("family:" + family))
                return ("family:" + family, false);
        }
        return ("base", false);
    }

    // The curve a source stands for, comparable between rows (every stored value goes through the same format).
    private string AxisTuningCurveKey(string source) =>
        axisTuningStored.TryGetValue(source, out string? text) ? AxisTuning.Parse(text).Format() : source;

    // "own curve" / "same as A, B" / "uses DCS" with its tooltip, for every row; the groups; the lines about the ticks.
    internal void UpdateAxisTuningRowStatuses()
    {
        foreach (AxisTuningScopeItem row in AxisTuningScopes)
        {
            row.Label = AxisTuningScopeLabelNow(row);
            if (row.IsFamily)
                row.IsShown = row.IsShown || axisTuningStored.ContainsKey(row.Scope);
        }
        DisambiguateAxisTuningAircraftLabels();
        Dictionary<AxisTuningScopeItem, (string Source, bool Detent)> sources = AxisTuningScopes.ToDictionary(r => r, r => AxisTuningSourceOf(r, axisTuningStored, axisTuningUses));
        foreach (AxisTuningScopeItem row in AxisTuningScopes)
        {
            (string source, bool detent) = sources[row];
            bool own = row.IsBase || string.Equals(source, row.Scope, StringComparison.OrdinalIgnoreCase);
            row.IsOwn = own;
            if (row.IsBase)
            {
                row.UsesText = T("for all without their own curve");
                row.UsesTip = T("The curve every game and aircraft uses unless it has its own.");
                continue;
            }
            if (!own)
            {
                string from = AxisTuningScopeLabel(source);
                row.UsesText = detent ? T("uses {0}, bent at the detent", from) : T("uses {0}", from);
                row.UsesTip = T("{0} has no curve of its own and uses the one of {1}: a change there changes it too. Tick it and save to give it its own.", row.Label, from) +
                              (detent ? " " + T("Detent mode bends it so your throttle's detent is where this aircraft's afterburner starts.") : string.Empty);
                continue;
            }
            // Aircraft of one game with the same own curve: "same as".
            List<string> same = row.IsAircraft
                ? AxisTuningScopes.Where(o => !ReferenceEquals(o, row) && o.IsAircraft && o.IsShown && string.Equals(o.Game, row.Game, StringComparison.OrdinalIgnoreCase) &&
                                              string.Equals(sources[o].Source, o.Scope, StringComparison.OrdinalIgnoreCase) &&
                                              AxisTuningCurveKey(o.Scope) == AxisTuningCurveKey(row.Scope))
                    .Select(o => o.Label).ToList()
                : [];
            if (same.Count > 0)
            {
                row.UsesText = T("same as {0}", string.Join(", ", same));
                row.UsesTip = T("{0} has its own curve, the same as {1}. Tick them together to change them in one step.", row.Label, string.Join(", ", same));
            }
            else
            {
                row.UsesText = T("own curve");
                row.UsesTip = row.IsAircraft
                    ? T("{0} has its own curve: a change for its game or for all games does not reach it.", row.Label)
                    : T("{0} has its own curve: a change for all games does not reach it.", row.Label);
            }
        }
        BuildAxisTuningGroups();
        SyncAxisTuningGameRows();
        // The plan may lead with another row now (after a save or a clear): the curve shown stays (it is that row's now).
        AxisTuningScopeItem? lead = AxisTuningPlan().Lead;
        if (lead != null && !ReferenceEquals(lead, SelectedAxisTuningScope) && SelectedAxisTuningScope != null && lead.Model.Id == SelectedAxisTuningScope.Model.Id)
            SetAxisTuningLead(lead);
        UpdateAxisTuningSelectionTexts();
    }

    // Per game with aircraft: the aircraft that use the game's curve, then each set of aircraft with the same own curve.
    private void BuildAxisTuningGroups()
    {
        AxisTuningGroups.Clear();
        foreach (AxisTuningScopeItem game in AxisTuningScopes.Where(IsAxisTuningAircraftGame))
        {
            List<AxisTuningScopeItem> all = AxisTuningAircraftOf(game.Game);
            List<AxisTuningScopeItem> following = all.Where(a => !a.IsOwn).ToList();
            if (following.Count > 0)
                AxisTuningGroups.Add(new AxisTuningGroupItem(string.Join(", ", following.Select(a => a.Label)) + ": " + following[0].UsesText,
                    T("Ticks these aircraft: their curve is shown, and Save changes it for all of them."), [game.Scope]));
            foreach (IGrouping<string, AxisTuningScopeItem> same in all.Where(a => a.IsOwn).GroupBy(a => AxisTuningCurveKey(a.Scope), StringComparer.Ordinal))
            {
                List<AxisTuningScopeItem> members = same.ToList();
                AxisTuningGroups.Add(new AxisTuningGroupItem(string.Join(", ", members.Select(a => a.Label)) + ": " + (members.Count > 1 ? T("same curve") : T("own curve")),
                    T("Ticks these aircraft: their curve is shown, and Save changes it for all of them."), members.Select(a => a.Scope).ToList()));
            }
        }
        OnPropertyChanged(nameof(HasAxisTuningGroups));
    }

    // The bold line about the curve shown, the plan line, the Save and the clear buttons' texts, the current group.
    private void UpdateAxisTuningSelectionTexts()
    {
        AxisTuningSavePlan plan = AxisTuningPlan();
        AxisTuningScopeItem? lead = plan.Lead;
        List<string> descriptor = AxisTuningTickDescriptor();
        // The group of the ticks (the game's curve with an own-curve aircraft ticked as well is none of them).
        foreach (AxisTuningGroupItem group in AxisTuningGroups)
            group.IsCurrent = (plan.Kind != "gameAll" || plan.Replaced.Count == 0) &&
                              group.Scopes.Count == descriptor.Count && group.Scopes.All(s => descriptor.Contains(s, StringComparer.OrdinalIgnoreCase));
        string names = string.Join(", ", plan.Aircraft.Select(a => a.Label));
        switch (plan.Kind)
        {
            case "base":
                AxisTuningSaveText = T("Save for all games");
                AxisTuningSavePlanText = T("Save stores it for all games.");
                break;
            case "game":
            case "family":
                AxisTuningSaveText = T("Save for {0}", lead!.Label);
                AxisTuningSavePlanText = T("Save stores it for {0}.", lead.Label);
                break;
            case "gameAll":
                AxisTuningSaveText = T("Save for {0}", lead!.Label);
                AxisTuningSavePlanText = plan.KeepOwn.Count == 0
                    ? T("Save stores it for {0}, all its aircraft ({1}).", lead.Label, plan.Total)
                    : T("Save stores it for {0}: {1} of its {2} aircraft follow it; {3} keep their own curve.", lead.Label, plan.Following, plan.Total,
                        string.Join(", ", plan.KeepOwn.Select(a => a.Label)));
                break;
            case "aircraft":
                AxisTuningSaveText = T("Save for the ticked aircraft ({0} of {1})", plan.Aircraft.Count, plan.Total);
                AxisTuningSavePlanText = T("Save stores it for these aircraft ({0} of {1}): {2}.", plan.Aircraft.Count, plan.Total, names);
                break;
            default:
                AxisTuningSaveText = T("Save");
                AxisTuningSavePlanText = T("Tick where this curve should apply.");
                break;
        }
        AxisTuningReplaceText = plan.Replaced.Count == 0 ? string.Empty : T("This replaces the own curve of {0}.", string.Join(", ", plan.Replaced.Select(a => a.Label)));
        List<AxisTuningScopeItem> clearing = AxisTuningClearRows();
        AxisTuningClearText = clearing.Count > 0 && clearing.All(t => t.IsAircraft) ? T("Use the game's curve again") : T("Use the curve for all games again");
        RaiseAxisTuningCommandState();
        OnPropertyChanged(nameof(AxisTuningCopyText));
        if (lead == null)
        {
            AxisTuningStatus = T("Tick where this curve should apply.");
            return;
        }
        if (!AxisTuningLoaded)
            return;
        bool differ = plan.Kind == "aircraft" && plan.Aircraft
            .Select(t => AxisTuningCurveKey(AxisTuningSourceOf(t, axisTuningStored, axisTuningUses).Source)).Distinct(StringComparer.Ordinal).Count() > 1;
        bool fitted = axisTuningFitted && string.Equals(axisTuningFittedScope, lead.Scope, StringComparison.OrdinalIgnoreCase);
        AxisTuningStatus = differ ? T("Shown: the curve of {0}. The ticked aircraft have different curves today.", lead.Label)
            : lead.IsBase ? T("Shown: the curve for all games.")
            : lead.IsOwn ? T("Shown: the own curve of {0}.", lead.Label)
            : T("Shown: the curve {0} uses now ({1}{2}).", lead.Label, lead.UsesText, fitted ? T(" — fitted to this game's model") : string.Empty);
    }

    // The rows "Use the game's curve again" takes the own curve from: the ticked ones that have one (not all games).
    internal List<AxisTuningScopeItem> AxisTuningClearRows() => AxisTuningTicked.Where(t => !t.IsBase && t.IsOwn).ToList();

    // "AH-64D CPG, Ka-50 III": what the curve shown is for.
    internal string AxisTuningTickedLabels()
    {
        AxisTuningSavePlan plan = AxisTuningPlan();
        return plan.Kind == "aircraft" ? string.Join(", ", plan.Aircraft.Select(a => a.Label)) : plan.Lead?.Label ?? T("All games");
    }

    // A DCS aircraft by the module's name (from the media index) with its seat ("AH-64D BLK.II CPG"), else its id.
    internal static string AxisTuningAircraftLabel(string plane)
    {
        (string module, string seat) = NamedSeat(plane);
        return seat.Length > 0 ? module + " " + seat : module;
    }

    // Aircraft rows of one game with the same name (two seats of one module): the id itself when it starts with the name
    // ("SA342 Copilot"), else the id in brackets tells them apart.
    private void DisambiguateAxisTuningAircraftLabels()
    {
        foreach (IGrouping<string, AxisTuningScopeItem> group in AxisTuningScopes.Where(s => s.IsAircraft).GroupBy(s => s.Game + "|" + s.Label, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            foreach (AxisTuningScopeItem row in group)
            {
                if (string.Equals(row.Label, row.Plane, StringComparison.OrdinalIgnoreCase))
                    continue;
                row.Label = row.Plane.StartsWith(row.Label, StringComparison.OrdinalIgnoreCase) ? row.Plane : row.Label + " (" + row.Plane + ")";
            }
        }
    }
}
