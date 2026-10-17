using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// One modifier combination of the selected input in "Bind on the picture": "—" (no modifier), "Shift A", "Shift A +
// Shift B" with the relation bound to the input under exactly those modifiers. The picked row is the combination
// "Bind here" binds with.
public sealed partial class VisualAssignComboRow : ObservableObject
{
    // The combination key (CalloutCombos.Key): "" = no modifier, "Shift A+Shift B".
    public string Combo { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBound), nameof(IsFree), nameof(Display))]
    private string relation = string.Empty;

    [ObservableProperty]
    private bool isSelected;

    // The relation just bound here (highlighted for a moment).
    [ObservableProperty]
    private bool isJustBound;

    public bool IsBound => Relation.Length > 0;

    public bool IsFree => !IsBound;

    public string Display => IsBound ? Relation : "free";
}
