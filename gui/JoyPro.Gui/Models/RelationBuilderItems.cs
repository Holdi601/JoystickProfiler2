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

// An action that other relations have switched on: one tag in Add actions and in the suggestions ("in 'X'" opens it,
// "in 3 relations" lists them; MainWindowViewModel.OpenOtherRelationsTagAsync).
public interface IOtherRelationsTag
{
    IReadOnlyList<string> OtherRelations { get; }

    bool HasSeveralOtherRelations { get; }

    bool IsOtherRelationsExpanded { get; set; }
}

internal static class OtherRelationsWords
{
    // "in 'Button CP2 B1'" or "in 3 relations".
    public static string Tag(IReadOnlyList<string> relations) => relations.Count switch
    {
        0 => string.Empty,
        1 => T("in '{0}'", relations[0]),
        _ => Plural("in {0} relation", "in {0} relations", relations.Count)
    };

    public static string Tip(IReadOnlyList<string> relations) => relations.Count switch
    {
        0 => string.Empty,
        1 => T("Also switched on in '{0}'. Click to open that relation.", relations[0]),
        _ => T("Also switched on in: {0}. Click to list them; a name opens that relation.", string.Join(", ", relations))
    };
}

// One action that fits the open relation (engine relations suggest): one command of a game, with every aircraft that has
// it, the confidence, the short reason and the relation that already uses it (if any).
public sealed partial class BuilderSuggestionItem : ObservableObject, IOtherRelationsTag
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

    // The same tag as in Add actions (the other relations that have it switched on).
    public bool HasOtherRelations => OtherRelations.Count > 0;

    public bool HasSeveralOtherRelations => OtherRelations.Count > 1;

    public string OtherRelationsTag => OtherRelationsWords.Tag(OtherRelations);

    public string OtherRelationsTip => OtherRelationsWords.Tip(OtherRelations);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOtherRelationsList))]
    private bool isOtherRelationsExpanded;

    public bool ShowOtherRelationsList => IsOtherRelationsExpanded && HasSeveralOtherRelations;

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
public sealed partial class BuilderSearchResultItem : ObservableObject, IOtherRelationsTag
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
        otherRelations = OtherOf(string.Empty);
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
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private bool isInOpenRelation;

    public bool CanCheck => !IsInOpenRelation;

    // How many of the row's aircraft the open relation has already, of how many: 19 of 20 is no "new" action, and no
    // "already in this relation" either: it stays addable and offers only the aircraft still free.
    public int InRelationPlanes { get; private set; }

    public int RowPlanes { get; private set; }

    public bool IsPartlyInOpenRelation => !IsInOpenRelation && InRelationPlanes > 0;

    // The row's own aircraft (same or nearly the same meaning of the id): what "already in this relation" is about.
    private IReadOnlyList<string> OwnPlanes => Aircraft != null ? Aircraft.OwnPlanes : Planes;

    // The open relation's aircraft of this action (null: it does not have the action). Returns whether all of the row's
    // aircraft are in it (another game: the action is).
    internal bool MarkInOpenRelation(HashSet<string>? present)
    {
        IReadOnlyList<string> own = OwnPlanes;
        bool dcs = Game == "DCS";
        int inside = present == null || !dcs ? 0 : own.Count(present.Contains);
        Aircraft?.MarkInOpenRelation(dcs ? present : null);
        InRelationPlanes = inside;
        RowPlanes = own.Count;
        bool all = present != null && (!dcs || (own.Count > 0 && inside == own.Count));
        IsInOpenRelation = all;
        OnPropertyChanged(nameof(IsPartlyInOpenRelation));
        OnPropertyChanged(nameof(InOtherRelationsText));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(SelectedPlanes));
        OnPropertyChanged(nameof(NodeSpec));
        OnPropertyChanged(nameof(PlanesText));
        return all;
    }

    public string KindLabel => IsSimPitSwitchAxis ? T("axis (sim-pit switch)") : IsAxis ? "axis" : "button";

    // A DCS axis command that sets a cockpit switch (the F-4E's "[SimPit] Landing Gear (Switch)", 199 of them): made
    // for home cockpits that wire a switch or a potentiometer as an axis. The game lists it as an axis, so it is one;
    // the label says what it is.
    public bool IsSimPitSwitchAxis => IsAxis && Game == "DCS" &&
                                      (Title.StartsWith("[SimPit]", System.StringComparison.OrdinalIgnoreCase) ||
                                       System.Text.RegularExpressions.Regex.IsMatch(Title, @"\((Switch|Button|Knob|Selector|Lever|Handle)\)\s*$"));

    public string? KindTip => IsSimPitSwitchAxis
        ? T("DCS lists this as an axis command: home cockpit builders move this cockpit switch with an analog input (a potentiometer, or a switch wired as an axis). It is no flight control axis.")
        : null;

    // No raw id on screen (only in the title's tooltip, IdTip); the catalog filter and the search still find it.
    public string IdHint => string.Empty;

    public string IdTip => Game == "DCS" || Id.Length == 0 ? DisplayTitle : DisplayTitle + "\n" + T("The game's own id: {0}", Id);

    public bool HasIdHint => IdHint.Length > 0;

    // The aircraft (DCS) in one line, the full list in the tooltip; outside DCS the category (never the raw id). A row that
    // can be added names what goes in; a row wholly in the open relation has nothing to add and names its own aircraft (two
    // rows of one title are told apart by this line).
    public string PlanesText => Game == "DCS" ? BuilderText.Where(Game, IsInOpenRelation ? OwnPlanes : SelectedPlanes) : Category;

    public string PlanesTooltip => Game == "DCS" ? string.Join(", ", Planes) : DisplayText.Game(Game) + (Category.Length > 0 ? " · " + Category : string.Empty);

    public string InOtherRelationsText => IsInOpenRelation
        ? T("already in this relation")
        : IsPartlyInOpenRelation ? T("already in this relation for {0} of {1} aircraft", InRelationPlanes, RowPlanes) : string.Empty;

    public bool HasNote => InOtherRelationsText.Length > 0;

    // ---- Used in other relations: one tag (docs/relation-builder.md "Make a relation, step by step") ----

    // The open relation (MarkBuilderRowsInRelation): it is not "another" relation.
    private string openRelationName = string.Empty;

    private IReadOnlyList<string> otherRelations;

    public string OpenRelationName
    {
        get => openRelationName;
        set
        {
            string name = value ?? string.Empty;
            if (string.Equals(openRelationName, name, System.StringComparison.Ordinal))
                return;
            openRelationName = name;
            otherRelations = OtherOf(name);
            OnPropertyChanged(nameof(OtherRelations));
            OnPropertyChanged(nameof(HasOtherRelations));
            OnPropertyChanged(nameof(HasSeveralOtherRelations));
            OnPropertyChanged(nameof(OtherRelationsTag));
            OnPropertyChanged(nameof(OtherRelationsTip));
            OnPropertyChanged(nameof(ShowOtherRelationsList));
        }
    }

    private IReadOnlyList<string> OtherOf(string open) =>
        InRelations.Where(r => !string.Equals(r, open, System.StringComparison.OrdinalIgnoreCase)).Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();

    // The other relations that have it switched on (for at least one of its aircraft), by name.
    public IReadOnlyList<string> OtherRelations => otherRelations;

    public bool HasOtherRelations => otherRelations.Count > 0;

    public bool HasSeveralOtherRelations => otherRelations.Count > 1;

    // "in 'Button CP2 B1'" or "in 3 relations" (a click lists them).
    public string OtherRelationsTag => OtherRelationsWords.Tag(otherRelations);

    public string OtherRelationsTip => OtherRelationsWords.Tip(otherRelations);

    // The names of the other relations, listed under the row (several only).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOtherRelationsList))]
    private bool isOtherRelationsExpanded;

    public bool ShowOtherRelationsList => IsOtherRelationsExpanded && HasSeveralOtherRelations;

    // How the engine found it (relations search "Match"): exact, function, synonym, described, words, category.
    public string Match { get; init; } = string.Empty;

    // The function of the function map it is (relations search "Function"), as its name.
    public string FunctionName { get; init; } = string.Empty;

    // The function's main control in this game (relations search "Primary": MSFS "AXIS ELEVATOR SET" for "pitch").
    public bool Primary { get; init; }

    // The title has every word of the query (relations search "Literal"): listed under "Name has every word", above
    // the rows that only mean the same, in every sort and grouping.
    public bool IsNameMatch { get; init; }

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

    // The catalog (docs/relation-builder.md "Browse the catalog"): its rows come from relations browse, whose Recommended
    // is the best action of each function. While a filter text is on, the chip says what the search says for these words.
    private bool? queryRecommended;
    private string queryRecommendedFor = string.Empty;

    public bool ShownRecommended => queryRecommended ?? IsRecommended;

    public string ShownRecommendedLabel
    {
        get
        {
            string recommendedFor = queryRecommended == null ? RecommendedFor : queryRecommendedFor;
            return recommendedFor.Length > 0 ? T("Recommended {0}", Translate(recommendedFor)) : T("Recommended");
        }
    }

    public bool ShownHasVariant => !ShownRecommended && Variant.Length > 0;

    // null: no filter text, the row's own value again.
    internal void SetQueryRecommended(bool? recommended, string recommendedFor = "")
    {
        if (queryRecommended == recommended && queryRecommendedFor == recommendedFor)
            return;
        queryRecommended = recommended;
        queryRecommendedFor = recommendedFor;
        OnPropertyChanged(nameof(ShownRecommended));
        OnPropertyChanged(nameof(ShownRecommendedLabel));
        OnPropertyChanged(nameof(ShownHasVariant));
    }

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

    // The chip: how it was found, in words (its tooltip, ReasonText, says why in a sentence). The engine's "Title match
    // (32%)" and "word match" are gone from the row (2.6.2): they said nothing, and a category word was called a title
    // match.
    public string MatchLabel => Primary ? T("main control") : SectionKind switch
    {
        "name" => Match == "exact" ? T("exact title") : Match is "function" or "synonym" or "described" ? T("same function") : T("name match"),
        "function" => Match == "exact" ? T("exact title") : Match == "synonym" ? T("synonym") : Match == "described" ? T("by description") : T("same function"),
        "related" => T("related"),
        _ => string.Empty
    };

    public bool HasMatch => MatchLabel.Length > 0;

    // Strong evidence (the title or the function itself) is shown in the accent colour.
    public bool IsStrongMatch => Primary || (Match is "exact" or "function" && !IsRelated);

    // The line under the title: the function it was found as ("same function: Wheel brakes axis"), or for a related row
    // why it is only related (the whole sentence in the tooltip).
    public string WhyText => IsRelated
        ? ReasonText
        : ActionFunction.Length > 0 ? T("same function: {0}", ActionFunction)
        : Why.StartsWith("function", System.StringComparison.OrdinalIgnoreCase) ? T("same function: {0}", Why[(Why.IndexOf('\'') + 1)..].TrimEnd('\'')) : string.Empty;

    // The part of a DCS category ("Gears, Brakes, Hook; Left Console") that has the word.
    private string CategoryWithWord()
    {
        string[] parts = Category.Split(';', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            if (CategoryWords.Any(w => part.Contains(Views.Controls.HighlightTextBlock.Singular(w), System.StringComparison.OrdinalIgnoreCase)))
                return part;
        }
        return Category;
    }

    public bool HasWhy => WhyText.Length > 0;

    // ---- The section and why it was found (relations search "Section", docs/relation-builder.md "Same function and
    // Related") ----

    // "name", "function" or "related" (an older engine without it: from Literal and Match).
    public string Section { get; set; } = string.Empty;

    // The words typed in the search box (the reasons quote them).
    public string Query { get; set; } = string.Empty;

    // The function the query was read as ("Wheel brakes"), how ("name", "synonym", "description", "related") and by
    // which word ("thrust"); the row's own function ("Wheel brakes axis") and the game's word for it ("collective").
    public string Understood { get; set; } = string.Empty;

    public string UnderstoodBy { get; set; } = string.Empty;

    public string QueryTerm { get; set; } = string.Empty;

    public string ActionFunction { get; set; } = string.Empty;

    public string GameTerm { get; set; } = string.Empty;

    // A related row: "function", "catalog", "category", "some-words" or "similar"; the typed words in its title, only in
    // its category, or only as something similar (a word start, a typo, the game's id).
    public string RelatedBy { get; set; } = string.Empty;

    public IReadOnlyList<string> TitleWords { get; set; } = [];

    public IReadOnlyList<string> CategoryWords { get; set; } = [];

    public IReadOnlyList<string> CloseWords { get; set; } = [];

    // "lacks-word": the number or side typed that its title does not have ("LMFD").
    public IReadOnlyList<string> MissingWords { get; set; } = [];

    public string SectionKind => IsNameMatch ? "name"
        : Section is "function" or "related" ? Section
        : Match is "exact" or "function" or "synonym" or "described" ? "function" : "related";

    public bool IsSameFunction => SectionKind == "function";

    public bool IsRelated => SectionKind == "related";

    // Why it was found, in plain words (the tooltip of the row's chip and of the line under its title).
    public string ReasonText
    {
        get
        {
            string query = Query.Trim();
            string function = ActionFunction.Length > 0 ? ActionFunction : FunctionName;
            static string Words(IReadOnlyList<string> words) => string.Join("', '", words);
            switch (SectionKind)
            {
                case "name":
                    return function.Length > 0 ? T("The title has every word you typed; its function is '{0}'.", function) : T("The title has every word you typed.");
                case "function":
                {
                    string head = Understood.Length == 0
                        ? T("You typed the game's own name or id of this action.")
                        : UnderstoodBy switch
                        {
                            "synonym" => T("'{0}' is a synonym of '{1}'; this action's function is '{2}'.", QueryTerm.Length > 0 ? QueryTerm : query, Understood, function),
                            "description" => T("What you typed describes '{0}'; this action's function is '{1}'.", Understood, function),
                            _ => T("'{0}' is understood as '{1}'; this action's function is '{2}'.", query, Understood, function)
                        };
                    return GameTerm.Length > 0 ? head + " " + T("The game calls it '{0}'.", GameTerm) : head;
                }
                default:
                    return RelatedBy switch
                    {
                        "function" when Understood.Length > 0 => T("Related: '{0}' is close to what you typed, but it is not the same function; this action is '{1}'.", Understood, function),
                        "catalog" when function.Length > 0 => T("Related: JoyPro's function list reads this action as '{0}', but its title does not say so.", function),
                        "category" => T("Related: its category '{0}' has the word '{1}'; its title has none of the words you typed.", CategoryWithWord(), Words(CategoryWords)),
                        "similar" => T("Related: only something like '{0}' was found (a word start, a typo or the game's id), not the word itself.", Words(CloseWords)),
                        "some-words" when TitleWords.Count > 0 => T("Related: its title has only some of the words you typed ('{0}').", Words(TitleWords)),
                        "lacks-word" when MissingWords.Count > 0 => T("Related: it is an action of '{0}', but its title does not have '{1}'.", Understood.Length > 0 ? Understood : function, Words(MissingWords)),
                        _ => T("Related: found by some of the words, not by its name or its function.")
                    };
            }
        }
    }

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

// A section of the search list (docs/relation-builder.md "Same function and Related"): "Name has every word (2)" (titles
// with every typed word), "Same function (9)" (actions of the function the words were read as: its name, a synonym, a
// description) and "Related (7)" (looser links: a related function, a category word, some of the words), folded until
// opened. The game or function headers and their rows follow it; the actions already in the open relation stay greyed
// at the bottom, after all of them.
public sealed class BuilderSearchSectionItem
{
    public BuilderSearchSectionItem(string kind, int count, bool expanded = true)
    {
        Kind = kind;
        Count = count;
        IsExpanded = expanded;
    }

    // "name", "function" or "related".
    public string Kind { get; }

    public bool IsNameMatches => Kind == "name";

    public bool IsRelated => Kind == "related";

    public int Count { get; }

    // Only "Related" folds; folded, its rows are not in the list.
    public bool IsExpanded { get; }

    public bool CanFold => IsRelated;

    public bool IsFixed => !CanFold;

    public string Label => Kind switch
    {
        "name" => T("Name has every word ({0})", Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        "function" => T("Same function ({0})", Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        _ => T("Related ({0})", Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
    };

    public string Tooltip => Kind switch
    {
        "name" => T("The action's title has every word you typed"),
        "function" => T("The action does what your words mean: the same function, found by its name, a synonym or a description. Hover a row to see why."),
        _ => T("Looser links: a related function, a word of the category, some of your words or a similar word. Hover a row to see why. A click shows or hides them.")
    };
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

    // The actions of the search already in the open relation: greyed at the bottom of the list, nothing to tick.
    public static BuilderSearchGroupItem InRelation(IReadOnlyList<BuilderSearchResultItem> rows) =>
        new(string.Empty, rows, 0, T("Already in this relation")) { IsInRelationGroup = true };

    public string Game { get; }

    public IReadOnlyList<BuilderSearchResultItem> Rows { get; }

    public int Hidden { get; }

    private readonly string functionLabel;

    public bool IsInRelationGroup { get; private init; }

    public bool IsGame => functionLabel.Length == 0;

    public bool IsFunctionGroup => !IsGame && !IsInRelationGroup;

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
    public AircraftChoiceItem(string plane, string title, string category, string match, string note, bool isChecked, bool sameFunction = false, bool fromYourFiles = false)
    {
        SameFunction = sameFunction;
        FromYourFiles = fromYourFiles;
        Plane = plane;
        Title = title;
        Category = category;
        Match = match;
        Note = note;
        this.isChecked = isChecked;
        InitiallyChecked = isChecked;
        Offered = isChecked;
    }

    public string Plane { get; }

    public string Title { get; }

    public string Category { get; }

    public string Match { get; }

    public string Note { get; }

    public bool InitiallyChecked { get; set; }

    // The row's own proposal for this aircraft, whatever the open relation has.
    internal bool Offered { get; set; }

    [ObservableProperty]
    private bool isChecked;

    // The open relation has it already (the rows of Add actions): marked and no tick to change, it is not offered.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChange))]
    [NotifyPropertyChangedFor(nameof(MarkerText))]
    [NotifyPropertyChangedFor(nameof(HasMarker))]
    private bool isInOpenRelation;

    // A row wholly in the open relation adds nothing, so a tick on its other aircraft would do nothing: the list only shows.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChange))]
    private bool isShowOnly;

    public bool CanChange => !IsInOpenRelation && !IsShowOnly;

    // Opened from a card of the check: this aircraft is one the card is about (tinted).
    [ObservableProperty]
    private bool isHighlighted;

    // The Relations page's Games filter is set to this aircraft (the relation editor marks it in the action's list).
    [ObservableProperty]
    private bool isFilterMatch;

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

    // A DCS command JoyPro knows from the user's own bind files, not from the game's list: a mod added it after the list
    // was built (engine "FromYourFiles"; the next rebuild of the list brings it with its category).
    public bool FromYourFiles { get; }

    public string MarkerText => IsInOpenRelation ? T("already in this relation") : IsDiffers ? (SameFunction ? "same function" : "differs") : IsNear ? "near" : IsUnknown ? T("not in its list") :
        FromYourFiles ? T("from your DCS files") : string.Empty;

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
                             (IsOtherFunction ? T(" - this command does something else here") : IsDiffers ? T(" - another name here, but it does the same") : IsNear ? T(" - close to the row's meaning ({0})", Note) : string.Empty) +
                             (FromYourFiles ? T(" - found in your DCS bind files (from a mod); rebuild the DCS command list to get it from DCS") : string.Empty);
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

    // The aircraft the row stands for (same or nearly the same meaning), with or without a tick.
    internal IReadOnlyList<string> OwnPlanes => Items.Where(i => i.Offered).Select(i => i.Plane).ToList();

    public int InRelationCount => Items.Count(i => i.IsInOpenRelation);

    // Every aircraft the row stands for is in the open relation: the row is not addable, and neither is anything in its
    // list (BuilderSearchResultItem.MarkInOpenRelation decides the same way).
    public bool IsWhollyInOpenRelation => Items.Any(i => i.Offered) && Items.Where(i => i.Offered).All(i => i.IsInOpenRelation);

    public bool CanTick => !IsWhollyInOpenRelation;

    // The open relation's aircraft of this action are not offered: unticked, no tick to change, marked. Aircraft that
    // left it (another relation opened) get the row's own proposal back.
    internal void MarkInOpenRelation(HashSet<string>? present)
    {
        bool changed = false;
        bulk = true;
        foreach (AircraftChoiceItem item in Items)
        {
            bool inside = present != null && present.Contains(item.Plane);
            if (item.IsInOpenRelation == inside)
                continue;
            changed = true;
            item.IsInOpenRelation = inside;
            item.InitiallyChecked = !inside && item.Offered;
            item.IsChecked = !inside && item.Offered;
        }
        bool showOnly = IsWhollyInOpenRelation;
        foreach (AircraftChoiceItem item in Items)
        {
            if (item.IsShowOnly != showOnly)
                changed = true;
            item.IsShowOnly = showOnly;
            if (showOnly && item.IsChecked)
            {
                changed = true;
                item.IsChecked = false;
            }
        }
        bulk = false;
        if (changed)
            Changed();
    }

    // "82 of 84 aircraft"; with aircraft the open relation has already, only the others count ("1 of 1 free aircraft"); with
    // all the row's aircraft in it nothing can be added, so it says what the relation has ("1 of 3 aircraft in this relation").
    public string Badge => IsWhollyInOpenRelation ? T("{0} of {1} aircraft in this relation", InRelationCount, Total)
        : InRelationCount > 0 ? T("{0} of {1} free aircraft", CheckedCount, Total - InRelationCount) : T("{0} of {1} aircraft", CheckedCount, Total);

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

    public bool CanTickSameFunction => HasSameFunction && CanTick;

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
        {
            if (item.CanChange)
                item.IsChecked = state(item);
        }
        bulk = false;
        Changed();
    }

    // The Relations page's Games filter (null: none): its aircraft are marked and listed first under "Filter: F-16C_50",
    // the others after them by meaning as always (2.6.4 tester report: "the order!"). Only the list shown changes; Items
    // and the ticks keep their order.
    internal void LeadWithFilter(Func<string, bool>? isFiltered)
    {
        bool changed = false;
        foreach (AircraftChoiceItem item in Items)
        {
            bool match = isFiltered != null && isFiltered(item.Plane);
            if (item.IsFilterMatch == match)
                continue;
            item.IsFilterMatch = match;
            changed = true;
        }
        if (changed)
            ApplyFilter();
    }

    // The filter's aircraft lead the list (when some, not all of them are the filter's).
    public bool HasFilterLead => Visible.Any(i => i.IsFilterMatch) && Visible.Any(i => !i.IsFilterMatch);

    private void ApplyFilter()
    {
        string filter = (FilterText ?? string.Empty).Trim();
        List<AircraftChoiceItem> shown = [];
        foreach (AircraftChoiceItem item in Items)
        {
            if (filter.Length == 0 || item.Plane.Contains(filter, StringComparison.OrdinalIgnoreCase) || item.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Category.Contains(filter, StringComparison.OrdinalIgnoreCase) || (filter.Equals("differs", StringComparison.OrdinalIgnoreCase) && item.IsDiffers))
                shown.Add(item);
        }
        List<AircraftChoiceItem> lead = shown.Where(i => i.IsFilterMatch).ToList();
        bool leads = lead.Count > 0 && lead.Count < shown.Count;
        List<AircraftChoiceItem> rest = leads ? shown.Where(i => !i.IsFilterMatch).ToList() : shown;
        Visible.Clear();
        foreach (AircraftChoiceItem item in leads ? lead.Concat(rest) : shown)
            Visible.Add(item);
        // A heading over each meaning group of the shown aircraft; the filter's aircraft first under their own.
        foreach (AircraftChoiceItem item in Items)
            item.GroupHeader = string.Empty;
        if (leads)
            lead[0].GroupHeader = T("Filter: {0}", string.Join(", ", lead.Select(i => i.Plane)));
        foreach (IGrouping<int, AircraftChoiceItem> group in rest.GroupBy(i => i.MeaningGroup))
            group.First().GroupHeader = GroupTitle(group.Key, group.Count());
        OnPropertyChanged(nameof(HasFilterLead));
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
        OnPropertyChanged(nameof(IsWhollyInOpenRelation));
        OnPropertyChanged(nameof(CanTick));
        OnPropertyChanged(nameof(CanTickSameFunction));
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

    // The switched-on aircraft with the action's own meaning (engine check-aircraft "Planes"): they keep it.
    public IReadOnlyList<string> KeepPlanes { get; init; } = [];

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
