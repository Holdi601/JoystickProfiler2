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
                OnPropertyChanged(nameof(BindInputPart));
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
                OnPropertyChanged(nameof(ModifierChips));
                OnPropertyChanged(nameof(HasModifierChips));
            }
        }
    }

    // "WarBRD · BTN 8 · Shift A": which device, which input, which modifiers (an input alone says nothing).
    public string BindLabel => IsBound ? DisplayText.BindLabel(BindDeviceFull, BindInputRaw, Modifiers) : string.Empty;

    // The card shows the device (it may shorten), the input after it, and every modifier as its own chip: the input
    // and the modifiers are never cut off. " · BTN 8" (the input part of the key cap).
    public string BindInputPart
    {
        get
        {
            string key = DisplayText.Input(BindInputRaw);
            if (!IsBound || key.Length == 0)
                return string.Empty;
            return (BindDevice.Length > 0 ? " · " : string.Empty) + DeviceMarkerText.ShortLabel(key);
        }
    }

    public IReadOnlyList<string> ModifierChips => Modifiers.Select(m => (m ?? string.Empty).Trim()).Where(m => m.Length > 0 && !m.StartsWith('<')).ToList();

    public bool HasModifierChips => IsBound && ModifierChips.Count > 0;

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
            {
                OnPropertyChanged(nameof(GameList));
                OnPropertyChanged(nameof(PlanesTooltip));
            }
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
                OnPropertyChanged(nameof(PlanesTooltip));
            }
        }
    }

    private IReadOnlyList<string> planeIds = [];

    // The aircraft of its switched-on actions as the engine names them ("FA-18C_hornet"), for the chip's tooltip.
    public IReadOnlyList<string> PlaneIds
    {
        get => planeIds;
        set
        {
            if (SetProperty(ref planeIds, value ?? []))
                OnPropertyChanged(nameof(PlanesTooltip));
        }
    }

    // The aircraft names as the modules call them ("F/A-18C Hornet"), set by the main window once it has them.
    public static IReadOnlyDictionary<string, string> AircraftNames { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Tooltip of the "19 aircraft" chip: every aircraft by its readable name; beyond MaxTooltipAircraft "+N more".
    public const int MaxTooltipAircraft = 40;

    // A game without aircraft lists itself as its one "aircraft" ("IL2Game"): named as the game.
    public string PlanesTooltip => AircraftListText(PlaneIds, PlanesText, DisplayText.List(Games));

    internal static string AircraftListText(IReadOnlyList<string> ids, string countText, IReadOnlyCollection<string>? gameIds = null)
    {
        HashSet<string> games = new(gameIds ?? [], StringComparer.OrdinalIgnoreCase);
        List<string> names = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => AircraftNames.TryGetValue(id.Trim(), out string? name) && !string.IsNullOrWhiteSpace(name) ? name.Trim()
                : games.Contains(id.Trim()) ? DisplayText.Game(id.Trim()) : id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (names.Count == 0)
            return countText;
        string list = string.Join(", ", names.Take(MaxTooltipAircraft));
        if (names.Count > MaxTooltipAircraft)
            list = T("{0} +{1} more", list, names.Count - MaxTooltipAircraft);
        return T("{0}:\n{1}", countText, list);
    }

    // The readable names arrived: the tooltip follows.
    internal void RefreshAircraftNames() => OnPropertyChanged(nameof(PlanesTooltip));

    public string Groups { get; set; } = string.Empty;

    // The titles of its actions ("relations list --titles"), for the search.
    public IReadOnlyList<string> ActionTitles { get; set; } = [];

    // Each title with its action, game and aircraft ("relations list --titles" TitleList): the search's match lines.
    public IReadOnlyList<ActionTitleEntry> ActionTitleEntries { get; set; } = [];

    // What the Relations page's search found in its actions (set by the list's filter; empty without a search).
    public SearchMatchInfo SearchHits { get; set; } = new();

    // ---- For the relation list's cards (chips and the input key cap) ----

    public bool IsAxis => Type.Equals("axis", StringComparison.OrdinalIgnoreCase);

    public string TypeLabel => IsAxis ? T("Axis") : T("Button");

    // Bind is "<device>::<input>" or "unbound".
    public bool IsBound => Bind.Contains("::", StringComparison.Ordinal);

    public bool IsUnbound => !IsBound;

    public string BindInput => IsBound ? DisplayText.Input(Bind[(Bind.LastIndexOf("::", StringComparison.Ordinal) + 2)..]) : string.Empty;

    public string BindDevice => IsBound ? DisplayText.ShortDevice(Bind[..Bind.LastIndexOf("::", StringComparison.Ordinal)]) : string.Empty;

    // A device alias changed or Settings switched between aliases and real names.
    public void RefreshDeviceName()
    {
        OnPropertyChanged(nameof(BindDevice));
        OnPropertyChanged(nameof(BindLabel));
        OnPropertyChanged(nameof(BindTooltip));
        OnPropertyChanged(nameof(BindInputPart));
    }

    public IReadOnlyList<string> GameList => DisplayText.List(Games).Select(DisplayText.Game).ToList();

    public IReadOnlyList<string> GroupList => DisplayText.List(Groups);

    public string PlanesText => int.TryParse(Planes, out int count) && count > 0 ? Plural("{0} aircraft", "{0} aircraft", count) : string.Empty;

    public bool HasPlanes => PlanesText.Length > 0;
}
