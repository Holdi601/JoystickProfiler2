using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

public sealed partial class RelationNodeItem : ObservableObject
{
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
    }

    private void OnSelectionChanged(object? sender, System.EventArgs e)
    {
        OnPropertyChanged(nameof(IsMixed));
        OnPropertyChanged(nameof(MixedText));
        OnPropertyChanged(nameof(BadgeText));
    }
}
