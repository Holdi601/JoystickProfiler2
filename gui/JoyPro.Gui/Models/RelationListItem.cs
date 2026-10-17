using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.Gui.Models;

// Observable for the counts: the relation editor updates the selected card's games and aircraft after an add without
// reloading (and reselecting) the whole list.
public sealed class RelationListItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private string games = string.Empty;
    private string planes = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    private string bind = string.Empty;

    // Set again after every bind change (binds list): the cards, the Bind Studio and the Start page counts follow.
    public string Bind
    {
        get => bind;
        set
        {
            if (SetProperty(ref bind, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsBound));
                OnPropertyChanged(nameof(IsUnbound));
                OnPropertyChanged(nameof(BindInput));
                OnPropertyChanged(nameof(BindDevice));
                OnPropertyChanged(nameof(BindLabel));
                OnPropertyChanged(nameof(BindTooltip));
            }
        }
    }

    private IReadOnlyList<string> modifiers = [];

    // The modifiers of its bind ("Shift A"): held with the input.
    public IReadOnlyList<string> Modifiers
    {
        get => modifiers;
        set
        {
            if (SetProperty(ref modifiers, value ?? []))
            {
                OnPropertyChanged(nameof(BindLabel));
                OnPropertyChanged(nameof(BindTooltip));
            }
        }
    }

    // "WarBRD · BTN 8 · Shift A": which device, which input, which modifiers (an input alone says nothing).
    public string BindLabel => IsBound ? DisplayText.BindLabel(BindDeviceFull, BindInputRaw, Modifiers) : string.Empty;

    public string BindTooltip => IsBound ? DisplayText.BindTooltip(BindDeviceFull, BindInputRaw, Modifiers) : T("Not on a button or axis yet: bind it on the Binds page");

    public string BindDeviceFull => IsBound ? Bind[..Bind.LastIndexOf("::", StringComparison.Ordinal)] : string.Empty;

    public string BindInputRaw => IsBound ? Bind[(Bind.LastIndexOf("::", StringComparison.Ordinal) + 2)..] : string.Empty;

    // "DCS|A-10C", "IL2Game|IL2Game": the (game, aircraft) of its switched-on actions, for the filters.
    public IReadOnlyCollection<string> ActiveGamePlanes { get; set; } = [];

    public string Games
    {
        get => games;
        set
        {
            if (SetProperty(ref games, value ?? string.Empty))
                OnPropertyChanged(nameof(GameList));
        }
    }

    public string Planes
    {
        get => planes;
        set
        {
            if (SetProperty(ref planes, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(PlanesText));
                OnPropertyChanged(nameof(HasPlanes));
            }
        }
    }

    public string Groups { get; set; } = string.Empty;

    // ---- For the relation list's cards (chips and the input key cap) ----

    public bool IsAxis => Type.Equals("axis", StringComparison.OrdinalIgnoreCase);

    public string TypeLabel => IsAxis ? T("Axis") : T("Button");

    // Bind is "<device>::<input>" or "unbound".
    public bool IsBound => Bind.Contains("::", StringComparison.Ordinal);

    public bool IsUnbound => !IsBound;

    public string BindInput => IsBound ? DisplayText.Input(Bind[(Bind.LastIndexOf("::", StringComparison.Ordinal) + 2)..]) : string.Empty;

    public string BindDevice => IsBound ? DisplayText.ShortDevice(Bind[..Bind.LastIndexOf("::", StringComparison.Ordinal)]) : string.Empty;

    public IReadOnlyList<string> GameList => DisplayText.List(Games).Select(DisplayText.Game).ToList();

    public IReadOnlyList<string> GroupList => DisplayText.List(Groups);

    public string PlanesText => int.TryParse(Planes, out int count) && count > 0 ? Plural("{0} aircraft", "{0} aircraft", count) : string.Empty;

    public bool HasPlanes => PlanesText.Length > 0;
}
