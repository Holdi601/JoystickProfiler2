using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JoyPro.Gui.Models;

// MSFS 2024 import: a bind the sim has only in the profiles of some aircraft, imported for all aircraft, with the engine
// command that limits it to them (MainWindowViewModel.MsfsAircraftOnly.cs).
public sealed record MsfsAircraftOnlyBind(string Relation, string ActionId, string Device, string Input, string AircraftText, IReadOnlyList<string> LimitArgs)
{
    public string Text => T("Only in the sim's profile(s) of {0}: {1} ({2} {3})", AircraftText, Relation, Device, Input);
}

// The relation editor (Relations page, MainWindowViewModel.RelationBuilder.cs): what fits the open relation, the actions
// found by the search, the chat's proposed changes and the game filter chips.

// One action that fits the open relation (engine relations suggest): one command of a game, with every aircraft that has
// it, the confidence, the short reason and the relation that already uses it (if any).
public sealed partial class BuilderSuggestionItem : ObservableObject
{
    public BuilderSuggestionItem(string game, string id, string title, IReadOnlyList<string> planes, string confidence, int score,
        string why, IReadOnlyList<string> reasons, IReadOnlyList<string> otherRelations)
    {
        Game = game;
        Id = id;
        Title = title;
        Planes = planes;
        Confidence = confidence;
        Score = score;
        Why = why.Length > 0 ? why : reasons.FirstOrDefault() ?? string.Empty;
        Reasons = string.Join("\n", reasons);
        OtherRelations = otherRelations;
        foreach (string plane in planes.Take(6))
            PlanePictures.Add(new PlaneChoice(plane));
    }

    public string Game { get; }

    public string Id { get; }

    public string Title { get; }

    public IReadOnlyList<string> Planes { get; }

    public string Confidence { get; }

    public int Score { get; }

    public string Why { get; }

    public string Reasons { get; }

    public IReadOnlyList<string> OtherRelations { get; }

    public bool IsDcs => Game == "DCS";

    public string GameLabel => DisplayText.Game(Game);

    public bool IsHigh => Confidence == "high";

    public bool IsMedium => Confidence == "medium";

    public bool IsLow => !IsHigh && !IsMedium;

    public string ConfidenceLabel => IsHigh ? T("High") : IsMedium ? T("Medium") : T("Low");

    public string ScoreText => Score + "%";

    public bool HasConflict => OtherRelations.Count > 0;

    // "Already in 'Flare'": the command is bound through another relation; Move here takes it over.
    public string ConflictText => HasConflict ? T("Already in '{0}'", string.Join("', '", OtherRelations)) : string.Empty;

    public string Where => BuilderText.Where(Game, Planes);

    // "game:id:plane1|plane2" for engine relations suggest apply.
    public string NodeSpec => Game + ":" + Id + ":" + string.Join("|", Planes);

    // "same id, different meaning in 3 aircraft (left out)" (relations suggest MeaningNote).
    public string MeaningNote { get; init; } = string.Empty;

    public bool HasMeaningNote => MeaningNote.Length > 0;

    // The first aircraft's module icons (DCS); the game's box art (others) in Picture.
    public ObservableCollection<PlaneChoice> PlanePictures { get; } = [];

    [ObservableProperty]
    private Bitmap? picture;

    // ---- "Suggest missing" (relations suggest Missing / NewGame / NewPlanes) ----

    // "game": a game the relation has no action of yet; "aircraft": aircraft it has none for; "": one more action for
    // aircraft it covers already.
    public string Missing { get; init; } = string.Empty;

    public IReadOnlyList<string> NewPlanes { get; init; } = [];

    public bool IsNewGame => Missing == "game";

    public bool IsNewAircraft => Missing == "aircraft";

    public bool IsMissing => Missing.Length > 0;

    // "new: F-16C_50, FA-18C_hornet +3" / "new game".
    public string MissingText => IsNewGame ? T("new game for this relation")
        : IsNewAircraft ? T("new for {0}", NewPlanes.Count <= 3 ? string.Join(", ", NewPlanes) : string.Join(", ", NewPlanes.Take(3)) + " +" + (NewPlanes.Count - 3))
        : string.Empty;

    // Ticked for "Add ticked".
    [ObservableProperty]
    private bool isChecked;

    // ---- Recommended action (relations suggest Recommended / Variant / Group; docs/relation-builder.md) ----

    // The one action to take in its game (DCS: aircraft); "for helicopters" when the game has one of each.
    public bool IsRecommended { get; init; }

    public string RecommendedFor { get; init; } = string.Empty;

    public string RecommendedLabel => RecommendedFor.Length > 0 ? T("Recommended {0}", Translate(RecommendedFor)) : T("Recommended");

    // An alternative: how it differs from the recommended action ("the game's alternate set of this control").
    public string Variant { get; init; } = string.Empty;

    public bool HasVariant => !IsRecommended && Variant.Length > 0;

    public string VariantText => T("Differs: {0}", Translate(Variant));

    // The recommended row's NodeSpec this one falls under (its own for a recommended row).
    public string Group { get; init; } = string.Empty;

    public string Readable { get; init; } = string.Empty;

    // The game's own name (or one made readable from an event id), never the raw "AXIS ELEVATOR SET".
    public string DisplayTitle => Readable.Length > 0 ? Readable : Title;

    public string FunctionName { get; init; } = string.Empty;

    // The function under the title; the game's id is never shown (only in the title's tooltip, IdTip).
    public string FunctionAndId => FunctionName;

    // The title's tooltip: the game's own id, for people who know it (never for DCS, whose ids are codes).
    public string IdTip => IsDcs || Id.Length == 0 ? DisplayTitle : DisplayTitle + "\n" + T("The game's own id: {0}", Id);

    public bool HasFunctionAndId => FunctionAndId.Length > 0;

    public string Explanation { get; init; } = string.Empty;

    public string ExplanationSource { get; init; } = string.Empty;

    public bool HasExplanation => Explanation.Length > 0;

    public string ExplanationTip => ExplanationSource switch
    {
        "guide" => T("From the cockpit guide (What does it do?)"),
        "game" => T("The game's own description"),
        "template" => T("A general explanation of this function"),
        "joypro" => T("JoyPro's explanation of this action"),
        _ => string.Empty
    };

    // The alternatives shown under this recommended row (collapsed at first).
    public List<BuilderSuggestionItem> AlternativeItems { get; } = [];

    public bool HasAlternatives => AlternativeItems.Count > 0;

    // "+3 other pitch actions in this game"
    public string AlternativesLabel => IsExpanded
        ? T("Hide the other actions")
        : Plural("+{0} other action in this game", "+{0} other actions in this game", AlternativeItems.Count);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlternativesLabel))]
    private bool isExpanded;

    // An alternative under a collapsed recommended row is hidden.
    [ObservableProperty]
    private bool isShown = true;

    public bool IsAlternative { get; set; }

    public void RefreshAlternatives()
    {
        OnPropertyChanged(nameof(HasAlternatives));
        OnPropertyChanged(nameof(AlternativesLabel));
    }

    partial void OnIsExpandedChanged(bool value)
    {
        foreach (BuilderSuggestionItem item in AlternativeItems)
            item.IsShown = value;
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleAlternatives() => IsExpanded = !IsExpanded;
}

// A section of the suggestions in "Suggest missing" ("Games not in it yet (12)").
public sealed partial class SuggestionSectionItem : ObservableObject
{
    private bool updating;

    public SuggestionSectionItem(string title, string hint, IReadOnlyList<BuilderSuggestionItem> items, bool tickable)
    {
        Title = title;
        Hint = hint;
        Items = items;
        Tickable = tickable;
        foreach (BuilderSuggestionItem item in items)
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BuilderSuggestionItem.IsChecked) && !updating)
                    OnPropertyChanged(nameof(IsChecked));
            };
    }

    public string Title { get; }

    public string Hint { get; }

    public IReadOnlyList<BuilderSuggestionItem> Items { get; }

    // Used elsewhere: Move here, one by one (no tick).
    public bool Tickable { get; }

    public string Header => Title + " (" + Items.Count + ")";

    public bool? IsChecked
    {
        get
        {
            int ticked = Items.Count(i => i.IsChecked);
            return ticked == 0 ? false : ticked == Items.Count ? true : null;
        }
        set
        {
            updating = true;
            try
            {
                // A collapsed alternative stays as it is: ticking the section takes what is shown.
                foreach (BuilderSuggestionItem item in Items.Where(i => i.IsShown || value != true))
                    item.IsChecked = value == true;
            }
            finally
            {
                updating = false;
            }
            OnPropertyChanged();
        }
    }
}

// One action found by the search over every game (engine relations search): a row with a tick box; several ticked rows
// go into the relation with one "Add selected".
public sealed partial class BuilderSearchResultItem : ObservableObject
{
    public BuilderSearchResultItem(string game, string id, string title, string category, bool isAxis, IReadOnlyList<string> planes, IReadOnlyList<string> inRelations, string why)
    {
        Game = game;
        Id = id;
        Title = title;
        Category = category;
        IsAxis = isAxis;
        Planes = planes;
        InRelations = inRelations;
        Why = why;
        foreach (string plane in planes.Take(4))
            PlanePictures.Add(new PlaneChoice(plane));
    }

    // Ticked for "Add selected".
    [ObservableProperty]
    private bool isChecked;

    // Every aircraft of it is in the open relation already: shown, not tickable.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    [NotifyPropertyChangedFor(nameof(InOtherRelationsText))]
    private bool isInOpenRelation;

    public bool CanCheck => !IsInOpenRelation;

    public string KindLabel => IsAxis ? "axis" : "button";

    // No raw id on screen (only in the title's tooltip, IdTip); the catalog filter and the search still find it.
    public string IdHint => string.Empty;

    public string IdTip => Game == "DCS" || Id.Length == 0 ? DisplayTitle : DisplayTitle + "\n" + T("The game's own id: {0}", Id);

    public bool HasIdHint => IdHint.Length > 0;

    // The aircraft (DCS) in one line, the full list in the tooltip; outside DCS the category (never the raw id).
    public string PlanesText => Game == "DCS" ? BuilderText.Where(Game, SelectedPlanes) : Category;

    public string PlanesTooltip => Game == "DCS" ? string.Join(", ", Planes) : DisplayText.Game(Game) + (Category.Length > 0 ? " · " + Category : string.Empty);

    public string InOtherRelationsText => IsInOpenRelation ? T("already in this relation") : IsInRelation ? T("also in '{0}'", string.Join("', '", InRelations)) : string.Empty;

    public bool HasNote => InOtherRelationsText.Length > 0;

    // How the engine found it (relations search "Match"): exact, function, synonym, described, words, category.
    public string Match { get; init; } = string.Empty;

    // The function of the function map it is (relations search "Function"), as its name.
    public string FunctionName { get; init; } = string.Empty;

    // The function's main control in this game (relations search "Primary": MSFS "AXIS ELEVATOR SET" for "pitch").
    public bool Primary { get; init; }

    // The title as shown: "AXIS ELEVATOR SET — Pitch axis (elevator)" when the title does not name the function
    // (relations search "Display").
    public string Display { get; init; } = string.Empty;

    public string DisplayTitle => Display.Length > 0 ? Display : Readable.Length > 0 ? Readable : Title;

    // ---- Recommended action (relations search / browse; docs/relation-builder.md "Recommended action") ----

    public string Readable { get; init; } = string.Empty;

    public bool IsRecommended { get; init; }

    public string RecommendedFor { get; init; } = string.Empty;

    public string RecommendedLabel => RecommendedFor.Length > 0 ? T("Recommended {0}", Translate(RecommendedFor)) : T("Recommended");

    public string Variant { get; init; } = string.Empty;

    public bool HasVariant => !IsRecommended && Variant.Length > 0;

    public string VariantText => T("Differs: {0}", Translate(Variant));

    public string Explanation { get; init; } = string.Empty;

    public string ExplanationSource { get; init; } = string.Empty;

    public bool HasExplanation => Explanation.Length > 0;

    public string ExplanationTip => ExplanationSource switch
    {
        "guide" => T("From the cockpit guide (What does it do?)"),
        "game" => T("The game's own description"),
        "template" => T("A general explanation of this function"),
        "joypro" => T("JoyPro's explanation of this action"),
        _ => string.Empty
    };

    // No raw id under the title (IdTip has it).
    public string IdText => string.Empty;

    public bool HasIdText => IdText.Length > 0;

    public string MatchLabel => Primary ? "main control" : Match switch
    {
        "exact" => "exact title",
        "function" => "same function",
        "synonym" => "synonym",
        "described" => "described",
        "words" => "word match",
        "catalog" => "catalog only",
        "category" => "category",
        _ => string.Empty
    };

    public bool HasMatch => MatchLabel.Length > 0;

    // Strong evidence (the title or the function itself) is shown in the accent colour.
    public bool IsStrongMatch => Primary || Match is "exact" or "function";

    public string WhyText => Why.StartsWith("function", System.StringComparison.OrdinalIgnoreCase) ? T("same function: {0}", Why[(Why.IndexOf('\'') + 1)..].TrimEnd('\'')) : Why;

    public bool HasWhy => Why.Length > 0;

    public string Game { get; }

    public string Id { get; }

    public string Title { get; }

    public string Category { get; }

    public bool IsAxis { get; }

    public IReadOnlyList<string> Planes { get; }

    public IReadOnlyList<string> InRelations { get; }

    public string Why { get; }

    public bool IsDcs => Game == "DCS";

    public string GameLabel => DisplayText.Game(Game);

    public string Where => BuilderText.Where(Game, Planes);

    public string Details => string.Join(" · ", new[] { Where, Category, IsAxis ? "axis" : string.Empty, Why }.Where(t => t.Length > 0));

    public bool IsInRelation => InRelations.Count > 0;

    public string InRelationsText => IsInRelation ? T("In '{0}'", string.Join("', '", InRelations)) : string.Empty;

    // The aircraft that go in when the row is added: the selector's ticks (DCS), else the row's.
    public IReadOnlyList<string> SelectedPlanes => Aircraft != null ? Aircraft.CheckedPlanes : Planes;

    public string NodeSpec => Game + ":" + Id + ":" + string.Join("|", SelectedPlanes);

    // The row itself across searches (its ticks survive a new search): the game, the id and its meaning.
    public string RowKey => Game + "|" + Id + "|" + Title;

    public ObservableCollection<PlaneChoice> PlanePictures { get; } = [];

    [ObservableProperty]
    private Bitmap? picture;

    // ---- Per aircraft (relations search "Variants", EngineIdMeanings) ----

    private AircraftSelector? aircraft;

    // DCS: every aircraft with this id, each with its own title and category, ticked when the id means the row's
    // function there; null for other games.
    public AircraftSelector? Aircraft
    {
        get => aircraft;
        set
        {
            if (aircraft != null)
                aircraft.SelectionChanged -= OnAircraftChanged;
            aircraft = value;
            if (aircraft != null)
                aircraft.SelectionChanged += OnAircraftChanged;
            OnPropertyChanged(nameof(HasAircraft));
            OnPropertyChanged(nameof(PlanesText));
        }
    }

    public bool HasAircraft => Aircraft != null && Aircraft.Total > 0;

    // "same id, different meaning in 14 aircraft (left out)" (relations search MeaningNote).
    public string MeaningNote { get; init; } = string.Empty;

    // MSFS: "only for: PMDG 737-800", an add-on aircraft's own action (adding it ticks those aircraft alone).
    public string OnlyForText { get; set; } = string.Empty;

    public bool HasOnlyFor => OnlyForText.Length > 0;

    public bool HasMeaningNote => MeaningNote.Length > 0;

    public int SameIdDifferent { get; init; }

    public int NearCount { get; init; }

    public bool HasNear => NearCount > 0;

    // Ticking aircraft ticks the row; no aircraft ticked unticks it.
    private void OnAircraftChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectedPlanes));
        OnPropertyChanged(nameof(NodeSpec));
        OnPropertyChanged(nameof(PlanesText));
        if (Aircraft == null || !CanCheck)
            return;
        if (Aircraft.CheckedCount == 0)
            IsChecked = false;
        else if (Aircraft.HasChanges)
            IsChecked = true;
    }
}

// The header of one game's rows in the search list ("DCS · 48"), with a box that ticks all of its rows.
public sealed partial class BuilderSearchGroupItem : ObservableObject
{
    private bool updating;

    // A game's rows (game set), or a function's rows (label set, game empty).
    public BuilderSearchGroupItem(string game, IReadOnlyList<BuilderSearchResultItem> rows, int hidden, string label = "")
    {
        Game = game;
        functionLabel = label;
        Rows = rows;
        Hidden = hidden;
        foreach (BuilderSearchResultItem row in rows)
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BuilderSearchResultItem.IsChecked))
                    Sync();
            };
        Sync();
    }

    public string Game { get; }

    public IReadOnlyList<BuilderSearchResultItem> Rows { get; }

    public int Hidden { get; }

    private readonly string functionLabel;

    public bool IsGame => functionLabel.Length == 0;

    public string Label => IsGame ? DisplayText.Game(Game) : functionLabel;

    // "12 of 44 actions" when some are not shown (the game chip says 44 too), else "12 actions".
    public string CountText => (Hidden > 0 ? T("{0} of {1}", Rows.Count, (Rows.Count + Hidden)) : Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)) +
                               (Rows.Count + Hidden == 1 ? " action" : " actions");

    // Show more / Show less (MainWindowViewModel.ToggleBuilderGameExpandedAsync): the rest of the game's hits for the
    // query, kept while the query is the same.
    public bool IsExpanded { get; init; }

    public bool CanExpand => IsGame && (Hidden > 0 || IsExpanded);

    public string ExpandLabel => IsExpanded ? T("Show less") : T("Show {0} more", Hidden);

    public string ExpandTooltip => IsExpanded ? T("Only the best rows of this game again") : T("Every action of this game for this search (ticks stay)");

    public bool HasTickable => Rows.Any(r => r.CanCheck);

    // All of the game's tickable rows ticked (null: some).
    [ObservableProperty]
    private bool? isChecked = false;

    partial void OnIsCheckedChanged(bool? value)
    {
        if (updating || value == null)
            return;
        updating = true;
        foreach (BuilderSearchResultItem row in Rows.Where(r => r.CanCheck))
            row.IsChecked = value.Value;
        updating = false;
    }

    private void Sync()
    {
        if (updating)
            return;
        List<BuilderSearchResultItem> tickable = Rows.Where(r => r.CanCheck).ToList();
        int ticked = tickable.Count(r => r.IsChecked);
        updating = true;
        IsChecked = ticked == 0 ? false : ticked == tickable.Count ? true : null;
        updating = false;
    }
}

// One turn of the JoyPro assistant (engine relations assistant): the user's message, or JoyPro's answer with what it
// created, added and removed per relation and game, the steps the AI took, and Undo for the whole turn.
public sealed partial class AssistantMessageItem : ObservableObject
{
    public AssistantMessageItem(bool isUser, string text)
    {
        IsUser = isUser;
        Text = text;
    }

    public bool IsUser { get; }

    public bool IsAssistant => !IsUser;

    public string Text { get; set; }

    public bool HasText => Text.Length > 0;

    public string Method { get; set; } = string.Empty;

    public bool HasMethod => Method.Length > 0;

    public List<AssistantRelationSummary> Relations { get; } = [];

    public bool HasRelations => Relations.Count > 0;

    // Per changed relation: created / renamed and how many actions came in or went out (the status line).
    public List<AssistantChangeCount> Changes { get; } = [];

    // A question JoyPro answered (nothing changes).
    public bool IsQuestion { get; set; }

    public List<string> Steps { get; } = [];

    public bool HasSteps => Steps.Count > 0;

    public string StepsHeader => Steps.Count == 1 ? T("What JoyPro did (1 step)") : T("What JoyPro did ({0} steps)", Steps.Count);

    public string Notes { get; set; } = string.Empty;

    public bool HasNotes => Notes.Length > 0;

    public bool NeedsModel { get; set; }

    // The relation to open from this answer.
    public string FocusRelation { get; set; } = string.Empty;

    public bool HasFocusRelation => FocusRelation.Length > 0;

    // The profile history's version this answer made (MainWindowViewModel.ProfileHistory.cs): Undo takes it back while
    // nothing changed after it.
    public int UndoVersion { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    private bool isUndone;

    public bool Changed { get; set; }

    public bool CanUndo => Changed && !IsUndone;
}

// "Throttle ALL — created · added DCS 48 aircraft, IL-2 5, Star Citizen 2".
// One relation an assistant answer changed, counted: for the status line.
public sealed class AssistantChangeCount
{
    public string Name { get; init; } = string.Empty;
    public bool Created { get; init; }
    public string RenamedFrom { get; init; } = string.Empty;
    public int Added { get; init; }
    public int Removed { get; init; }
}

public sealed class AssistantRelationSummary
{
    public AssistantRelationSummary(string name, string text)
    {
        Name = name;
        Text = text;
    }

    public string Name { get; }

    public string Text { get; }
}

// A command dismissed in this relation ("never propose it again"); Restore takes it out so it can come back.
public sealed class BuilderDismissedItem
{
    public BuilderDismissedItem(string game, string id, string title, IReadOnlyList<string> planes)
    {
        Game = game;
        Id = id;
        Title = title;
        Planes = planes;
    }

    public string Game { get; }

    public string Id { get; }

    public string Title { get; }

    public IReadOnlyList<string> Planes { get; }

    public string Where => BuilderText.Where(Game, Planes);

    public string Spec => Game + ":" + Id + ":" + string.Join("|", Planes);
}

// A game filter chip with its count ("DCS 31"); none ticked = every game.
public sealed partial class BuilderGameChip : ObservableObject
{
    public BuilderGameChip(string gameId, int count)
    {
        GameId = gameId;
        Count = count;
    }

    public string GameId { get; }

    public int Count { get; set; }

    public string Label => DisplayText.Game(GameId) + (Count > 0 ? " " + Count : string.Empty);

    [ObservableProperty]
    private bool isSelected;
}

// One aircraft of an id in the aircraft selector: its own title and category of the id, how that compares to the row's
// or node's meaning ("same", "near", "differs", "unknown") and the tick.
public sealed partial class AircraftChoiceItem : ObservableObject
{
    public AircraftChoiceItem(string plane, string title, string category, string match, string note, bool isChecked, bool sameFunction = false)
    {
        SameFunction = sameFunction;
        Plane = plane;
        Title = title;
        Category = category;
        Match = match;
        Note = note;
        this.isChecked = isChecked;
        InitiallyChecked = isChecked;
    }

    public string Plane { get; }

    public string Title { get; }

    public string Category { get; }

    public string Match { get; }

    public string Note { get; }

    public bool InitiallyChecked { get; set; }

    [ObservableProperty]
    private bool isChecked;

    public bool IsSame => Match == "same";

    public bool IsNear => Match == "near";

    // The id means something else in this aircraft: shown with a marker, never ticked by default.
    public bool IsDiffers => Match == "differs";

    // Another title, but the function map reads it as the same function ("Flight Control Cyclic Pitch" in a helicopter
    // for "Pitch"; engine relations aircraft "SameFunction"): no mixed meaning.
    public bool SameFunction { get; }

    // The id means another function here: never ticked by default, a ticked one flags the action "Mixed meanings".
    public bool IsOtherFunction => IsDiffers && !SameFunction;

    public bool IsUnknown => Match == "unknown";

    public string MarkerText => IsDiffers ? (SameFunction ? "same function" : "differs") : IsNear ? "near" : IsUnknown ? T("not in its list") : string.Empty;

    // The selector lists the aircraft by meaning: the same meaning, near, not in its list, another meaning.
    public int MeaningGroup => IsOtherFunction ? 4 : IsUnknown ? 3 : IsDiffers ? 2 : IsNear ? 1 : 0;

    // Set on the first shown aircraft of its meaning group: "Same meaning: 'Pitch' (69)".
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroupHeader))]
    private string groupHeader = string.Empty;

    public bool HasGroupHeader => GroupHeader.Length > 0;

    public bool HasMarker => MarkerText.Length > 0;

    // "Pitch · Flight Control"
    public string MeaningText => Title.Length == 0 ? Note : Category.Length > 0 ? Title + " · " + Category : Title;

    public string Tooltip => Plane + ": " + (Title.Length > 0 ? "'" + Title + "'" + (Category.Length > 0 ? " [" + Category + "]" : string.Empty) : T("the id is not in its list")) +
                             (IsOtherFunction ? T(" - this command does something else here") : IsDiffers ? T(" - another name here, but it does the same") : IsNear ? T(" - close to the row's meaning ({0})", Note) : string.Empty);
}

// The aircraft of one id with a tick each (the search rows of Add actions and the actions of the open relation):
// All / None / Only same title / Invert, a filter box, shift-click ranges, the "differs" marker, and the counts.
public sealed partial class AircraftSelector : ObservableObject
{
    private readonly string rowTitle;
    private AircraftChoiceItem? anchor;
    private bool bulk;

    public AircraftSelector(string rowTitle, IEnumerable<AircraftChoiceItem> items)
    {
        this.rowTitle = rowTitle;
        // By meaning (same, near, not in its list, another meaning), each group in the order given.
        Items = items.Select((item, index) => (item, index)).OrderBy(x => x.item.MeaningGroup).ThenBy(x => x.index).Select(x => x.item).ToList();
        foreach (AircraftChoiceItem item in Items)
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AircraftChoiceItem.IsChecked) && !bulk)
                    Changed();
            };
        ApplyFilter();
    }

    public IReadOnlyList<AircraftChoiceItem> Items { get; }

    // The items the filter lets through, in list order.
    public ObservableCollection<AircraftChoiceItem> Visible { get; } = [];

    public event EventHandler? SelectionChanged;

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private string filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public int Total => Items.Count;

    public int CheckedCount => Items.Count(i => i.IsChecked);

    public IReadOnlyList<string> CheckedPlanes => Items.Where(i => i.IsChecked).Select(i => i.Plane).ToList();

    public IReadOnlyList<string> UncheckedPlanes => Items.Where(i => !i.IsChecked).Select(i => i.Plane).ToList();

    public int DiffersCount => Items.Count(i => i.IsOtherFunction);

    // Aircraft whose title of the id is another one of the same function (a helicopter's "Cyclic Pitch" for "Pitch").
    public int SameFunctionCount => Items.Count(i => i.IsDiffers && i.SameFunction);

    public int NearCount => Items.Count(i => i.IsNear);

    // Ticked aircraft whose meaning of the id differs: the row or node mixes meanings.
    public int CheckedDiffersCount => Items.Count(i => i.IsChecked && i.IsOtherFunction);

    public bool HasMixed => CheckedDiffersCount > 0;

    public bool HasDiffers => DiffersCount > 0;

    public bool HasChanges => Items.Any(i => i.IsChecked != i.InitiallyChecked);

    // "82 of 84 aircraft"
    public string Badge => T("{0} of {1} aircraft", CheckedCount, Total);

    // "a-10a, A-10C +69 more" (the ticked ones).
    public string Summary
    {
        get
        {
            List<string> planes = CheckedPlanes.ToList();
            if (planes.Count == 0)
                return T("no aircraft ticked");
            return planes.Count <= 3 ? string.Join(", ", planes) : T("{0} +{1} more", string.Join(", ", planes.Take(2)), (planes.Count - 2));
        }
    }

    public string DiffersText => DiffersCount == 0 ? string.Empty
        : (DiffersCount == 1 ? T("1 aircraft uses this command for something else{0}", (HasMixed ? T(" ({0} ticked)", CheckedDiffersCount) : string.Empty))
            : T("{0} aircraft use this command for something else{1}", DiffersCount, (HasMixed ? T(" ({0} ticked)", CheckedDiffersCount) : string.Empty)));

    // "4 of 37 aircraft · 35 aircraft use this command for something else (show)"
    public string ExpandLabel => IsExpanded ? T("Hide aircraft") : Badge +
                                 (DiffersCount == 1 ? T(" · 1 aircraft uses this command for something else") : HasDiffers ? T(" · {0} aircraft use this command for something else", DiffersCount) : string.Empty) +
                                 (SameFunctionCount == 1 ? T(" · 1 aircraft has another name for it") : SameFunctionCount > 0 ? T(" · {0} aircraft have another name for it", SameFunctionCount) : string.Empty) +
                                 (HasDiffers || SameFunctionCount > 0 ? T(" (show)") : string.Empty);

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandLabel));

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    // All / None / Invert act on the aircraft the filter shows.
    [RelayCommand]
    public void SelectAll() => Bulk(Visible, _ => true);

    [RelayCommand]
    public void SelectNone() => Bulk(Visible, _ => false);

    [RelayCommand]
    public void Invert() => Bulk(Visible, i => !i.IsChecked);

    // Only the aircraft whose title of the id is the row's (also in another category; no word-order or one-word
    // variants, no other meaning).
    [RelayCommand]
    public void OnlySameTitle() => Bulk(Items, i => !i.IsDiffers && (i.IsSame || string.Equals(Norm(i.Title), Norm(rowTitle), StringComparison.Ordinal)));

    // Every aircraft where the id is this function, also under another title (same, near, same function).
    [RelayCommand]
    public void OnlySameFunction() => Bulk(Items, i => i.IsSame || i.IsNear || (i.IsDiffers && i.SameFunction));

    public bool HasSameFunction => SameFunctionCount > 0;

    private static string Norm(string text) => new string((text ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // Only the aircraft where the id means the row's or the action's function: same and near, never another meaning.
    [RelayCommand]
    public void OnlySameMeaning() => Bulk(Items, i => i.IsSame || i.IsNear);

    // Back to what the engine proposed.
    [RelayCommand]
    public void Reset() => Bulk(Items, i => i.InitiallyChecked);

    // A click on an aircraft's tick: with Shift every aircraft between the last clicked one and this one takes its state.
    public void Clicked(AircraftChoiceItem item, bool shift)
    {
        if (shift && anchor != null && !ReferenceEquals(anchor, item))
        {
            int a = Visible.IndexOf(anchor);
            int b = Visible.IndexOf(item);
            if (a >= 0 && b >= 0)
            {
                bool state = item.IsChecked;
                List<AircraftChoiceItem> range = Visible.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1).ToList();
                Bulk(range, _ => state);
            }
        }
        anchor = item;
    }

    private void Bulk(IEnumerable<AircraftChoiceItem> items, Func<AircraftChoiceItem, bool> state)
    {
        bulk = true;
        foreach (AircraftChoiceItem item in items.ToList())
            item.IsChecked = state(item);
        bulk = false;
        Changed();
    }

    private void ApplyFilter()
    {
        string filter = (FilterText ?? string.Empty).Trim();
        Visible.Clear();
        foreach (AircraftChoiceItem item in Items)
        {
            if (filter.Length == 0 || item.Plane.Contains(filter, StringComparison.OrdinalIgnoreCase) || item.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Category.Contains(filter, StringComparison.OrdinalIgnoreCase) || (filter.Equals("differs", StringComparison.OrdinalIgnoreCase) && item.IsDiffers))
                Visible.Add(item);
        }
        // A heading over each meaning group of the shown aircraft.
        foreach (AircraftChoiceItem item in Items)
            item.GroupHeader = string.Empty;
        foreach (IGrouping<int, AircraftChoiceItem> group in Visible.GroupBy(i => i.MeaningGroup))
            group.First().GroupHeader = GroupTitle(group.Key, group.Count());
    }

    // "Same meaning: 'Pitch' (60)", "Nearly the same (9)", "Not in its list (2)", "Does something else here (its own name shown) (14)".
    private string GroupTitle(int group, int count)
    {
        string n = " (" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        return group switch
        {
            0 => "Same meaning" + (rowTitle.Length > 0 ? ": '" + rowTitle + "'" : string.Empty) + n,
            1 => "Nearly the same" + n,
            2 => T("Another name, does the same{0}", n),
            3 => "Not in its list" + n,
            _ => T("Does something else here (its own name shown){0}", n)
        };
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(CheckedPlanes));
        OnPropertyChanged(nameof(Badge));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(CheckedDiffersCount));
        OnPropertyChanged(nameof(HasMixed));
        OnPropertyChanged(nameof(DiffersText));
        OnPropertyChanged(nameof(ExpandLabel));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // The engine's per-id meanings (relations search / resolve / suggest "Variants") as choices: the row's own aircraft
    // (same, near) ticked, the aircraft where the id means something else unticked and marked.
    public static AircraftSelector FromVariants(string rowTitle, IEnumerable<(string Title, string Category, string Match, string Note, bool Included, IReadOnlyList<string> Planes)> variants)
    {
        List<AircraftChoiceItem> items = [];
        foreach (var v in variants)
        {
            foreach (string plane in v.Planes)
            {
                if (items.Any(i => string.Equals(i.Plane, plane, StringComparison.OrdinalIgnoreCase)))
                    continue;
                items.Add(new AircraftChoiceItem(plane, v.Title, v.Category, v.Match.Length > 0 ? v.Match : "same", v.Note, v.Included && v.Match != "differs"));
            }
        }
        // The row's own aircraft first (alphabetically), then those where the id means something else.
        return new AircraftSelector(rowTitle, items.OrderBy(i => i.IsOtherFunction ? 1 : 0).ThenBy(i => i.Plane, StringComparer.OrdinalIgnoreCase));
    }
}

// One action of the profile check (engine relations check-aircraft): a relation's action that is ticked for aircraft
// where its command does something else, and those aircraft.
public sealed class AircraftCheckItem
{
    public AircraftCheckItem(string relation, string game, string id, string title, IReadOnlyList<AircraftCheckPlane> planes)
    {
        Relation = relation;
        Game = game;
        Id = id;
        Title = title;
        Planes = planes;
    }

    public string Relation { get; }

    public string Game { get; }

    public string Id { get; }

    public string Title { get; }

    public IReadOnlyList<AircraftCheckPlane> Planes { get; }

    public IReadOnlyList<string> Drop => Planes.Select(p => p.Plane).ToList();

    // "Pitch: 'Pitch' does something else in UH-1H"
    public string Text => T("{0}: '{1}' does something else in {2}", Relation, Title, BuilderText.Where(Game, Drop));

    // "'Rudder' (35 aircraft)": the heading of an action in the banner's list when the relation has several.
    public string Header => T("'{0}' ({1} aircraft)", Title, Planes.Count);
}

// One aircraft of a flagged action: what the command is called there, and "use here" (keep it ticked).
public sealed partial class AircraftCheckPlane : ObservableObject
{
    public AircraftCheckPlane(string plane, string title)
    {
        Plane = plane;
        Title = title;
    }

    public string Plane { get; }

    public string Title { get; }

    public string TitleText => Title.Length > 0 ? Title : T("not in its list");

    [ObservableProperty]
    private bool useHere;
}

internal static class BuilderText
{
    // "F-16C_50", "F-16C_50, FA-18C_hornet", "A-10C, A-10C II +2 more"; another game: the game.
    public static string Where(string game, IReadOnlyList<string> planes)
    {
        if (game != "DCS" && game.Length > 0)
            return DisplayText.Game(game);
        if (planes.Count <= 3)
            return string.Join(", ", planes);
        return T("{0} +{1} more", string.Join(", ", planes.Take(2)), (planes.Count - 2));
    }
}
