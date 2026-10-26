using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// What the filter bar of the Relations and Binds pages checks of one relation (ViewModels/ListFilterViewModel.cs),
// built once per relation list so a filter change is only set lookups.
public sealed class FilterSubject
{
    public FilterSubject(string name, IEnumerable<string> groups, IEnumerable<string> gamePlanes, string device, bool isBound, bool isAxis, string searchText,
        IEnumerable<string>? actionTitles = null)
    {
        string titles = string.Join("\u0001", (actionTitles ?? []).Where(t => !string.IsNullOrWhiteSpace(t)));
        NameText = (name ?? string.Empty).Trim().ToLowerInvariant();
        TitlesText = titles.ToLowerInvariant();
        ActionsText = titles.Length == 0 ? NameText : NameText + "\u0001" + TitlesText;
        Name = name ?? string.Empty;
        Groups = new HashSet<string>((groups ?? []).Where(g => g.Length > 0), StringComparer.OrdinalIgnoreCase);
        if (Groups.Count == 0)
            Groups.Add(NoGroup);
        GamePlanes = new HashSet<string>((gamePlanes ?? []).Where(p => p.Contains('|')), StringComparer.OrdinalIgnoreCase);
        Games = new HashSet<string>(GamePlanes.Select(p => p[..p.IndexOf('|')]), StringComparer.OrdinalIgnoreCase);
        Device = isBound ? (device ?? string.Empty).Trim() : string.Empty;
        IsBound = isBound;
        IsAxis = isAxis;
        SearchText = (titles.Length == 0 ? searchText ?? string.Empty : (searchText ?? string.Empty) + "\u0001" + titles).ToLowerInvariant();
    }

    // Lower case: the relation's name (search scope "Name").
    public string NameText { get; }

    // Lower case: the name and the titles of its actions (search scope "Name + actions", the default).
    public string ActionsText { get; }

    // Lower case: only the titles of its actions (a title with the whole search ranks first).
    public string TitlesText { get; }

    // The engine's name for relations without a group.
    public const string NoGroup = "<none>";

    public string Name { get; }

    public HashSet<string> Groups { get; }

    // Games with a switched-on action of the relation.
    public HashSet<string> Games { get; }

    // "DCS|A-10C", "IL2Game|IL2Game": the (game, aircraft) the relation's switched-on actions are for.
    public HashSet<string> GamePlanes { get; }

    // The full device name of its bind ("" when not bound).
    public string Device { get; }

    public bool IsBound { get; }

    public bool IsAxis { get; }

    // Lower case: name, action titles, games, aircraft, groups, device, input, modifiers (search scope "Everything").
    public string SearchText { get; }

    public static string Key(string game, string plane) => game + "|" + plane;
}

// One tick of a filter list (a group, a device, an aircraft) with how many relations it would show.
public sealed partial class FilterOptionItem : ObservableObject
{
    private readonly Action<FilterOptionItem, bool>? changed;
    private bool isChecked;

    public FilterOptionItem(string key, string label, bool isChecked, Action<FilterOptionItem, bool>? changed, string tooltip = "")
    {
        Key = key;
        Label = label;
        this.isChecked = isChecked;
        this.changed = changed;
        Tooltip = tooltip.Length > 0 ? tooltip : label;
    }

    public string Key { get; }

    public string Label { get; }

    public string Tooltip { get; }

    public bool IsChecked
    {
        get => isChecked;
        set
        {
            if (isChecked == value)
                return;
            isChecked = value;
            OnPropertyChanged();
            changed?.Invoke(this, value);
        }
    }

    // Set by the filter (no callback).
    internal void SetChecked(bool value)
    {
        if (isChecked == value)
            return;
        isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int count;

    public string CountText => Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Nothing would be shown with it (with the other filters as they are): drawn dimmed.
    public bool IsEmpty => Count == 0 && !IsChecked;

    [ObservableProperty]
    private bool isVisible = true;
}

// A game of the filter's game tree: tick the whole game, or open it and tick some of its aircraft.
public sealed partial class FilterGameItem : ObservableObject
{
    private readonly Action<FilterGameItem, bool?>? changed;
    private bool? isChecked;

    public FilterGameItem(string game, string label, bool? isChecked, Action<FilterGameItem, bool?>? changed)
    {
        Game = game;
        Label = label;
        this.isChecked = isChecked;
        this.changed = changed;
    }

    public string Game { get; }

    public string Label { get; }

    // true: the whole game; null: some of its aircraft; false: not filtered by this game.
    public bool? IsChecked
    {
        get => isChecked;
        set
        {
            if (isChecked == value)
                return;
            isChecked = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEmpty));
            changed?.Invoke(this, value);
        }
    }

    internal void SetChecked(bool? value)
    {
        if (isChecked == value)
            return;
        isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public ObservableCollection<FilterOptionItem> Planes { get; } = [];

    // Games with one list per game have no aircraft to pick.
    public bool HasPlanes => Planes.Count > 0;

    public bool HasManyPlanes => Planes.Count > 8;

    internal void PlanesChanged()
    {
        OnPropertyChanged(nameof(HasPlanes));
        OnPropertyChanged(nameof(HasManyPlanes));
        OnPropertyChanged(nameof(PlanesSummary));
    }

    [ObservableProperty]
    private bool isExpanded;

    // Filters the aircraft shown under the game ("A-10").
    [ObservableProperty]
    private string planeFilterText = string.Empty;

    partial void OnPlaneFilterTextChanged(string value)
    {
        string filter = (value ?? string.Empty).Trim();
        foreach (FilterOptionItem plane in Planes)
            plane.IsVisible = filter.Length == 0 || plane.Label.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int count;

    public string CountText => Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool IsEmpty => Count == 0 && IsChecked == false;

    // "3 of 108 aircraft" for a game filtered by some aircraft.
    public string PlanesSummary => !HasPlanes ? string.Empty
        : IsChecked == null ? T("{0} of {1} aircraft", Planes.Count(p => p.IsChecked), Planes.Count)
        : T("{0} aircraft", Planes.Count);

    internal void RefreshSummary() => OnPropertyChanged(nameof(PlanesSummary));
}

// An active filter under the bar ("DCS: A-10C, A-10C II", "Group HOTAS"), with x to take it off.
public sealed class FilterChipItem
{
    public FilterChipItem(string label, string tooltip, Action remove)
    {
        Label = label;
        Tooltip = tooltip;
        Remove = new CommunityToolkit.Mvvm.Input.RelayCommand(remove);
    }

    public string Label { get; }

    public string Tooltip { get; }

    public CommunityToolkit.Mvvm.Input.IRelayCommand Remove { get; }
}

// What a page remembers of its filters (list-filters.json): the ticks, not the typed search.
public sealed class ListFilterState
{
    public List<string> Groups { get; set; } = [];

    // Whole games ticked.
    public List<string> Games { get; set; } = [];

    // "game|plane" of games filtered by some aircraft.
    public List<string> Planes { get; set; } = [];

    public List<string> Devices { get; set; } = [];

    // "all", "bound", "unbound"
    public string Bound { get; set; } = "all";

    // "all", "axis", "button"
    public string Kind { get; set; } = "all";

    // What the typed search looks at: "name", "actions" (name + action titles) or "all".
    public string SearchScope { get; set; } = ListSearchScope.NameAndActions;
}

// The search scopes of the Relations and Binds pages' search box (the box next to it).
public static class ListSearchScope
{
    public const string Name = "name";
    public const string NameAndActions = "actions";
    public const string Everything = "all";

    public static readonly string[] All = [Name, NameAndActions, Everything];

    public static string Normalize(string? value) => value is Name or Everything ? value : NameAndActions;
}
