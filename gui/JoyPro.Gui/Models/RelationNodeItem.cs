using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

public sealed partial class RelationNodeItem : ObservableObject
{
    // The search's match line under the relation's card was clicked for this action: its card is marked.
    [ObservableProperty]
    private bool isSearchHit;

    public string Id { get; set; } = string.Empty;

    public string Game { get; set; } = string.Empty;

    public string ActivePlanesText { get; set; } = string.Empty;

    public string PlanesDisplay { get; set; } = string.Empty;

    public string TitlesDisplay { get; set; } = string.Empty;

    public string PlanesSummary { get; set; } = string.Empty;

    public string TitlesSummary { get; set; } = string.Empty;

    public string NodeSpec { get; set; } = string.Empty;

    // "axis" or "button" (the relation's kind).
    public string KindLabel { get; set; } = string.Empty;

    // The game as people say it, for the node card's game chip.
    public string GameLabel => DisplayText.Game(Game);

    // "DCS · F-16C_50, F-15C +3 · axis": the second line of the action's row.
    public string DetailsText => string.Join(" · ", new[] { GameLabel, Game == "DCS" ? PlanesSummary : string.Empty, T(KindLabel) }.Where(t => t.Length > 0));

    public bool HasNoActivePlanes => ActivePlanesText.Length == 0 && PlanesSummary.StartsWith("No active", System.StringComparison.Ordinal);

    // Pictures of the node's first aircraft (DCS module icons); the game's box art for other games.
    public ObservableCollection<PlaneChoice> PlanePictures { get; } = [];

    [ObservableProperty]
    private Bitmap? gameCover;

    // ---- Per aircraft (engine relations aircraft, EngineIdMeanings) ----

    // DCS: every aircraft that has the id, ticked when it is switched on in this node; editable at any time
    // (Apply = engine relations aircraft set, one undo step).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAircraft))]
    [NotifyPropertyChangedFor(nameof(IsMixed))]
    [NotifyPropertyChangedFor(nameof(MixedText))]
    [NotifyPropertyChangedFor(nameof(BadgeText))]
    private AircraftSelector? aircraft;

    // The node's meaning (the title most of its switched-on aircraft have for the id).
    public string MeaningTitle { get; set; } = string.Empty;

    // Switched-on aircraft whose title of the id means something else (from the engine; the selector's ticks after edits).
    public int Differs { get; set; }

    // Aircraft switched off because the meaning differs (auto-excluded or repaired): one tick re-includes one.
    public int Excluded { get; set; }

    public bool HasAircraft => Aircraft != null && Aircraft.Total > 0;

    public string BadgeText => Aircraft?.Badge ?? string.Empty;

    public bool IsMixed => Aircraft != null ? Aircraft.HasMixed : Differs > 0;

    public string MixedText => !IsMixed ? string.Empty
        : T("In {0} ticked aircraft this command is not '{1}' but something else", (Aircraft?.CheckedDiffersCount ?? Differs), MeaningTitle);

    public string ExcludedText => Excluded > 0 ? T("{0} left out (meaning differs): tick to re-include", Excluded) : string.Empty;

    public bool HasExcluded => Excluded > 0;

    // "Only add" did not write it into the game (the game uses the button for another function): the warning chip of
    // the row, per game and aircraft (engine export-marks). Empty: written.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportSkip))]
    private string exportSkipText = string.Empty;

    public bool HasExportSkip => ExportSkipText.Length > 0;

    // ---- The Relations page's Games filter (a game or aircraft ticked): the actions for it come first, marked ----

    // The action is for a game / aircraft the filter is set to: an accent edge and the aircraft named.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterMatchText))]
    [NotifyPropertyChangedFor(nameof(ShowFilterChipInHeader))]
    [NotifyPropertyChangedFor(nameof(ShowFilterChipOnAircraftLine))]
    private bool isFilterMatch;

    // The filter's aircraft of this action ("F-100D", "F-5E-3, F-16C_50"; the game for a whole game).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterMatchText))]
    private string filterMatchNames = string.Empty;

    // "Filter: F-100D"
    public string FilterMatchText => IsFilterMatch ? T("Filter: {0}", FilterMatchNames) : string.Empty;

    // The mark goes on the aircraft line ("1 of 4 aircraft"); an action without one (another game) shows it under its title.
    public bool ShowFilterChipOnAircraftLine => IsFilterMatch && HasAircraft;

    public bool ShowFilterChipInHeader => IsFilterMatch && !HasAircraft;

    // Sets the filter's mark (null or empty: not for the filter) and ticks the filter's aircraft in the aircraft list.
    public void SetFilterMatch(System.Collections.Generic.IReadOnlyList<string>? names, System.Func<string, bool>? isFilteredPlane)
    {
        bool match = names is { Count: > 0 };
        FilterMatchNames = match ? string.Join(", ", names!) : string.Empty;
        IsFilterMatch = match;
        filteredPlane = isFilteredPlane;
        MarkFilteredAircraft();
    }

    private System.Func<string, bool>? filteredPlane;

    private void MarkFilteredAircraft()
    {
        if (Aircraft == null)
            return;
        foreach (AircraftChoiceItem item in Aircraft.Items)
            item.IsFilterMatch = filteredPlane != null && filteredPlane(item.Plane);
    }

    // The game's own title of the action (engine relations aircraft), for the games whose node id is no title
    // ("KEY_AXIS_ELEVATOR_SET" -> "Elevator Axis", "elevator" -> "Pitch axis"); explanation: what it does (the cockpit
    // guide or the game's own description), in the tooltip.
    public void UseTitle(string title, string explanation = "")
    {
        if (string.IsNullOrWhiteSpace(title) || Game.Equals("DCS", System.StringComparison.OrdinalIgnoreCase))
            return;
        TitlesSummary = title.Trim();
        TitlesDisplay = title.Trim() + " (" + Id + ")" + (string.IsNullOrWhiteSpace(explanation) ? string.Empty : System.Environment.NewLine + explanation.Trim());
        OnPropertyChanged(nameof(TitlesSummary));
        OnPropertyChanged(nameof(TitlesDisplay));
    }

    // DCS, one id with several titles in its ticked aircraft: the main meaning is the title, every other meaning a line
    // of its own under it ("F-16C: KY-58 MODE Knob - RV (different function)", "also called: Thrust (13)").
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeaningNote))]
    private string meaningNote = string.Empty;

    public bool HasMeaningNote => MeaningNote.Length > 0;

    public void UseMeanings(string title, string note)
    {
        if (!string.IsNullOrWhiteSpace(title) && !string.Equals(TitlesSummary, title.Trim(), System.StringComparison.Ordinal))
        {
            TitlesSummary = title.Trim();
            OnPropertyChanged(nameof(TitlesSummary));
        }
        MeaningNote = note ?? string.Empty;
    }

    // MeaningTitle, Differs or Excluded were set (from engine relations aircraft).
    public void NotifyMeaningChanged()
    {
        OnPropertyChanged(nameof(IsMixed));
        OnPropertyChanged(nameof(MixedText));
        OnPropertyChanged(nameof(ExcludedText));
        OnPropertyChanged(nameof(HasExcluded));
        OnPropertyChanged(nameof(BadgeText));
    }

    partial void OnAircraftChanged(AircraftSelector? oldValue, AircraftSelector? newValue)
    {
        if (oldValue != null)
            oldValue.SelectionChanged -= OnSelectionChanged;
        if (newValue != null)
            newValue.SelectionChanged += OnSelectionChanged;
        MarkFilteredAircraft();
        OnPropertyChanged(nameof(ShowFilterChipInHeader));
        OnPropertyChanged(nameof(ShowFilterChipOnAircraftLine));
    }

    private void OnSelectionChanged(object? sender, System.EventArgs e)
    {
        OnPropertyChanged(nameof(IsMixed));
        OnPropertyChanged(nameof(MixedText));
        OnPropertyChanged(nameof(BadgeText));
    }
}

// The fold of the relation editor's action list under a Games filter: "Other aircraft (4)", the actions that are not for
// the filtered games / aircraft (they stay in the relation; a click shows or hides them).
public sealed partial class RelationNodeOthersHeader : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private int count;

    // "aircraft", "games" or "both": what the filter has ticked.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string kind = "aircraft";

    [ObservableProperty]
    private bool isExpanded;

    public string Label => Kind switch
    {
        "games" => T("Other games ({0})", Count),
        "both" => T("Other games and aircraft ({0})", Count),
        _ => T("Other aircraft ({0})", Count)
    };

    public void Refresh() => OnPropertyChanged(nameof(Label));
}
