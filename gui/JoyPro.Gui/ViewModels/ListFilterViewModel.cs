using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// The filter bar of the Relations page and the Binds page (one each, Views/Controls/ListFilterBar.axaml): the typed
// search, groups, games with their aircraft (tick DCS for every DCS relation; open it and tick A-10C, A-10C II for the
// relations with an action for those aircraft), devices, bound / not bound, axis / button. The filters combine (all of
// them must fit; several ticks of one filter: any of them), every tick shows how many relations it would show with the
// other filters as they are, and the page remembers its ticks (ListFilterState). The relations are FilterSubjects, built
// once per list: a filter change is set lookups only (524 relations / 3 300 actions: a few milliseconds).
public sealed partial class ListFilterViewModel : ObservableObject
{
    private readonly HashSet<string> groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> wholeGames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> planes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> devices = new(StringComparer.OrdinalIgnoreCase);
    private List<FilterSubject> subjects = [];
    private string[] searchWords = [];
    private int batch;

    public ListFilterViewModel(string page)
    {
        Page = page;
    }

    // "relations" or "binds": the key of the remembered state.
    public string Page { get; }

    // A filter changed: the page shows its list again.
    public event EventHandler? Changed;

    public ObservableCollection<FilterOptionItem> Groups { get; } = [];

    public ObservableCollection<FilterGameItem> Games { get; } = [];

    public ObservableCollection<FilterOptionItem> Devices { get; } = [];

    public ObservableCollection<FilterChipItem> ActiveChips { get; } = [];

    // ---- typed search (the page's search box sets it) ----

    private string searchText = string.Empty;

    public string SearchText
    {
        get => searchText;
        set
        {
            string text = value ?? string.Empty;
            if (string.Equals(searchText, text, StringComparison.Ordinal))
                return;
            searchText = text;
            searchWords = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            OnPropertyChanged();
            RaiseChanged();
        }
    }

    // What the typed search looks at (ListSearchScope; remembered with the ticks): the relation's name, its name and
    // the titles of its actions (the default), or everything (games, aircraft, groups, device, input, modifiers too).
    // Devices, games and aircraft are otherwise picked with the Devices / Games filters: searching "throttle" must
    // not list every relation bound on a device called "... Throttle".
    private string searchScope = ListSearchScope.NameAndActions;

    public string SearchScope
    {
        get => searchScope;
        set
        {
            string scope = ListSearchScope.Normalize(value);
            if (scope == searchScope)
                return;
            searchScope = scope;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SearchScopeIndex));
            OnPropertyChanged(nameof(SearchScopeTooltip));
            RaiseChanged();
        }
    }

    // The scope box next to the search box: 0 Name, 1 Name + actions, 2 Everything.
    public int SearchScopeIndex
    {
        get => Array.IndexOf(ListSearchScope.All, searchScope);
        set => SearchScope = value >= 0 && value < ListSearchScope.All.Length ? ListSearchScope.All[value] : ListSearchScope.NameAndActions;
    }

    public string SearchScopeTooltip => searchScope switch
    {
        ListSearchScope.Name => T("The search looks at the relation names only"),
        ListSearchScope.Everything => T("The search looks at everything: names, action titles, games, aircraft, groups, devices, inputs and modifiers"),
        _ => T("The search looks at the relation names and the titles of their actions; pick devices and games with the filters below")
    };

    // How well a shown relation fits the typed search (lower first): 0 its name is the search, 1 its name starts with
    // it, 2 its name has every word, 3 it was found by its actions or other parts. 0 without a search.
    public int SearchRank(FilterSubject? s)
    {
        if (s == null || searchWords.Length == 0)
            return 0;
        string whole = string.Join(' ', searchWords);
        if (s.NameText == whole)
            return 0;
        if (s.NameText.StartsWith(whole, StringComparison.Ordinal))
            return 1;
        foreach (string word in searchWords)
        {
            if (!s.NameText.Contains(word, StringComparison.Ordinal))
                return 3;
        }
        return 2;
    }

    public bool HasSearch => searchWords.Length > 0;

    // ---- bound / not bound, axis / button ----

    [ObservableProperty]
    private string bound = "all";

    [ObservableProperty]
    private string kind = "all";

    partial void OnBoundChanged(string value)
    {
        OnPropertyChanged(nameof(IsBoundAll));
        OnPropertyChanged(nameof(IsBoundOnly));
        OnPropertyChanged(nameof(IsUnboundOnly));
        RaiseChanged();
    }

    partial void OnKindChanged(string value)
    {
        OnPropertyChanged(nameof(IsKindAll));
        OnPropertyChanged(nameof(IsKindAxis));
        OnPropertyChanged(nameof(IsKindButton));
        RaiseChanged();
    }

    public bool IsBoundAll => Bound == "all";

    public bool IsBoundOnly => Bound == "bound";

    public bool IsUnboundOnly => Bound == "unbound";

    public bool IsKindAll => Kind == "all";

    public bool IsKindAxis => Kind == "axis";

    public bool IsKindButton => Kind == "button";

    // A switch clicked again goes back to "all" (the switches always show the state, also after a second click).
    [RelayCommand]
    private void SetBound(string? value)
    {
        string wanted = value is "bound" or "unbound" ? value : "all";
        Bound = Bound == wanted ? "all" : wanted;
        OnPropertyChanged(nameof(IsBoundAll));
        OnPropertyChanged(nameof(IsBoundOnly));
        OnPropertyChanged(nameof(IsUnboundOnly));
    }

    [RelayCommand]
    private void SetKind(string? value)
    {
        string wanted = value is "axis" or "button" ? value : "all";
        Kind = Kind == wanted ? "all" : wanted;
        OnPropertyChanged(nameof(IsKindAll));
        OnPropertyChanged(nameof(IsKindAxis));
        OnPropertyChanged(nameof(IsKindButton));
    }

    // ---- counts (with the other filters as they are) ----

    [ObservableProperty]
    private int shownCount;

    [ObservableProperty]
    private int totalCount;

    [ObservableProperty]
    private int boundCount;

    [ObservableProperty]
    private int unboundCount;

    [ObservableProperty]
    private int axisCount;

    [ObservableProperty]
    private int buttonCount;

    public string BoundAllLabel => T("All {0}", BoundCount + UnboundCount);

    public string BoundLabel => T("Bound {0}", BoundCount);

    public string UnboundLabel => T("Not bound {0}", UnboundCount);

    public string KindAllLabel => T("All {0}", AxisCount + ButtonCount);

    public string AxisLabel => T("Axes {0}", AxisCount);

    public string ButtonLabel => T("Buttons {0}", ButtonCount);

    public bool HasActiveFilters => groups.Count > 0 || wholeGames.Count > 0 || planes.Count > 0 || devices.Count > 0 || Bound != "all" || Kind != "all";

    public bool IsFiltered => HasActiveFilters || searchWords.Length > 0;

    // "124 of 524 shown".
    public string ResultText => IsFiltered
        ? T("{0} of {1} shown", ShownCount, TotalCount)
        : T("{0} in all", TotalCount);

    public string GroupsLabel => groups.Count == 0 ? T("Groups") : T("Groups · {0}", groups.Count);

    public string GamesLabel
    {
        get
        {
            int count = wholeGames.Count + planes.Select(p => p[..p.IndexOf('|')]).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return count == 0 ? T("Games") : T("Games · {0}", count);
        }
    }

    public string DevicesLabel => devices.Count == 0 ? T("Devices") : T("Devices · {0}", devices.Count);

    public bool HasGroupFilter => groups.Count > 0;

    public bool HasGameFilter => wholeGames.Count > 0 || planes.Count > 0;

    public bool HasDeviceFilter => devices.Count > 0;

    public bool HasGroups => Groups.Count > 0;

    public bool HasDevices => Devices.Count > 0;

    public bool HasGames => Games.Count > 0;

    // ---- the relations ----

    // A new relation list: the ticks stay where their group, game, aircraft or device is still there.
    public void SetSubjects(IEnumerable<FilterSubject> list)
    {
        subjects = list.ToList();
        RebuildOptions();
        UpdateCounts();
    }

    public bool Matches(FilterSubject? s)
    {
        if (s == null)
            return !IsFiltered;
        return PassSearch(s) && PassGroups(s) && PassGames(s) && PassDevices(s) && PassBound(s) && PassKind(s);
    }

    private bool PassSearch(FilterSubject s)
    {
        string text = searchScope switch
        {
            ListSearchScope.Name => s.NameText,
            ListSearchScope.Everything => s.SearchText,
            _ => s.ActionsText
        };
        foreach (string word in searchWords)
        {
            if (!text.Contains(word, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private bool PassGroups(FilterSubject s) => groups.Count == 0 || s.Groups.Overlaps(groups);

    private bool PassGames(FilterSubject s)
    {
        if (wholeGames.Count == 0 && planes.Count == 0)
            return true;
        foreach (string game in s.Games)
        {
            if (wholeGames.Contains(game))
                return true;
        }
        if (planes.Count == 0)
            return false;
        foreach (string plane in s.GamePlanes)
        {
            if (planes.Contains(plane))
                return true;
        }
        return false;
    }

    private bool PassDevices(FilterSubject s) => devices.Count == 0 || devices.Contains(s.Device);

    private bool PassBound(FilterSubject s) => Bound switch
    {
        "bound" => s.IsBound,
        "unbound" => !s.IsBound,
        _ => true
    };

    private bool PassKind(FilterSubject s) => Kind switch
    {
        "axis" => s.IsAxis,
        "button" => !s.IsAxis,
        _ => true
    };

    // ---- options ----

    private void RebuildOptions()
    {
        bool known = subjects.Count > 0;
        // Group chips: as the engine lists them, "No group" last.
        List<string> groupKeys = subjects.SelectMany(s => s.Groups).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g == FilterSubject.NoGroup ? 1 : 0).ThenBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        if (known)
            groups.IntersectWith(groupKeys);
        if (!Groups.Select(o => o.Key).SequenceEqual(groupKeys, StringComparer.OrdinalIgnoreCase))
        {
            Groups.Clear();
            foreach (string group in groupKeys)
                Groups.Add(new FilterOptionItem(group, group == FilterSubject.NoGroup ? T("No group") : group, groups.Contains(group), OnGroupTicked));
        }
        else
        {
            foreach (FilterOptionItem option in Groups)
                option.SetChecked(groups.Contains(option.Key));
        }

        // Games and their aircraft (games with one list have the game as their only "aircraft": no aircraft to pick).
        Dictionary<string, SortedSet<string>> byGame = new(StringComparer.OrdinalIgnoreCase);
        foreach (FilterSubject s in subjects)
        {
            foreach (string key in s.GamePlanes)
            {
                int bar = key.IndexOf('|');
                string game = key[..bar];
                if (!byGame.TryGetValue(game, out SortedSet<string>? set))
                    byGame[game] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                string plane = key[(bar + 1)..];
                if (!string.Equals(plane, game, StringComparison.OrdinalIgnoreCase))
                    set.Add(plane);
            }
        }
        if (known)
        {
            wholeGames.IntersectWith(byGame.Keys);
            planes.RemoveWhere(p => !byGame.TryGetValue(p[..p.IndexOf('|')], out SortedSet<string>? set) || !set.Contains(p[(p.IndexOf('|') + 1)..]));
        }
        List<string> gameKeys = byGame.Keys.OrderBy(g => DisplayText.Game(g), StringComparer.OrdinalIgnoreCase).ToList();
        bool sameGames = Games.Select(g => g.Game).SequenceEqual(gameKeys, StringComparer.OrdinalIgnoreCase) &&
                         Games.All(g => g.Planes.Select(p => p.Key).SequenceEqual(byGame[g.Game].Select(p => FilterSubject.Key(g.Game, p)), StringComparer.OrdinalIgnoreCase));
        if (!sameGames)
        {
            HashSet<string> expanded = Games.Where(g => g.IsExpanded).Select(g => g.Game).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Games.Clear();
            foreach (string game in gameKeys)
            {
                FilterGameItem item = new(game, DisplayText.Game(game), GameState(game), OnGameTicked) { IsExpanded = expanded.Contains(game) };
                foreach (string plane in byGame[game])
                {
                    string key = FilterSubject.Key(game, plane);
                    item.Planes.Add(new FilterOptionItem(key, plane, wholeGames.Contains(game) || planes.Contains(key), OnPlaneTicked));
                }
                item.PlanesChanged();
                Games.Add(item);
            }
        }
        else
        {
            SyncGameTicks();
        }

        // Devices by their name without the instance id; two of the same model keep a part of the id.
        List<string> deviceKeys = subjects.Select(s => s.Device).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => DisplayText.ShortDevice(d), StringComparer.OrdinalIgnoreCase).ToList();
        if (known)
            devices.IntersectWith(deviceKeys);
        if (!Devices.Select(o => o.Key).SequenceEqual(deviceKeys, StringComparer.OrdinalIgnoreCase))
        {
            Devices.Clear();
            HashSet<string> twice = deviceKeys.GroupBy(d => DisplayText.ShortDevice(d), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string device in deviceKeys)
            {
                string label = DisplayText.ShortDevice(device);
                if (twice.Contains(label))
                    label += " " + DeviceInstanceTail(device);
                Devices.Add(new FilterOptionItem(device, label, devices.Contains(device), OnDeviceTicked, device));
            }
        }
        else
        {
            foreach (FilterOptionItem option in Devices)
                option.SetChecked(devices.Contains(option.Key));
        }
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(HasDevices));
        RebuildChips();
    }

    // "{42CEC570-0A3F-...}" -> "(42CE)".
    private static string DeviceInstanceTail(string device)
    {
        int brace = device.LastIndexOf('{');
        return brace >= 0 && device.Length > brace + 5 ? "(" + device.Substring(brace + 1, 4) + ")" : string.Empty;
    }

    private bool? GameState(string game)
    {
        if (wholeGames.Contains(game))
            return true;
        string prefix = game + "|";
        return planes.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ? null : false;
    }

    private void SyncGameTicks()
    {
        foreach (FilterGameItem game in Games)
        {
            game.SetChecked(GameState(game.Game));
            foreach (FilterOptionItem plane in game.Planes)
                plane.SetChecked(wholeGames.Contains(game.Game) || planes.Contains(plane.Key));
            game.RefreshSummary();
        }
    }

    private void OnGroupTicked(FilterOptionItem option, bool value)
    {
        if (value)
            groups.Add(option.Key);
        else
            groups.Remove(option.Key);
        RaiseChanged();
    }

    private void OnDeviceTicked(FilterOptionItem option, bool value)
    {
        if (value)
            devices.Add(option.Key);
        else
            devices.Remove(option.Key);
        RaiseChanged();
    }

    // The game's tick: the whole game, or none of it.
    private void OnGameTicked(FilterGameItem game, bool? value)
    {
        string prefix = game.Game + "|";
        planes.RemoveWhere(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (value == true)
            wholeGames.Add(game.Game);
        else
            wholeGames.Remove(game.Game);
        SyncGameTicks();
        RaiseChanged();
    }

    // An aircraft's tick: a whole game loses that aircraft (the others stay ticked); every aircraft ticked is the whole
    // game again.
    private void OnPlaneTicked(FilterOptionItem option, bool value)
    {
        FilterGameItem? game = Games.FirstOrDefault(g => g.Planes.Contains(option));
        if (game == null)
            return;
        SetPlanes(game, [option], value);
    }

    private void SetPlanes(FilterGameItem game, IReadOnlyCollection<FilterOptionItem> which, bool value)
    {
        if (wholeGames.Remove(game.Game))
        {
            foreach (FilterOptionItem plane in game.Planes)
                planes.Add(plane.Key);
        }
        foreach (FilterOptionItem plane in which)
        {
            if (value)
                planes.Add(plane.Key);
            else
                planes.Remove(plane.Key);
        }
        if (game.Planes.Count > 0 && game.Planes.All(p => planes.Contains(p.Key)))
        {
            foreach (FilterOptionItem plane in game.Planes)
                planes.Remove(plane.Key);
            wholeGames.Add(game.Game);
        }
        SyncGameTicks();
        RaiseChanged();
    }

    // All / None under an open game: the aircraft shown (with the aircraft filter typed: only those).
    [RelayCommand]
    private void SelectAllPlanes(FilterGameItem? game)
    {
        if (game != null)
            SetPlanes(game, game.Planes.Where(p => p.IsVisible).ToList(), true);
    }

    [RelayCommand]
    private void SelectNoPlanes(FilterGameItem? game)
    {
        if (game != null)
            SetPlanes(game, game.Planes.Where(p => p.IsVisible).ToList(), false);
    }

    [RelayCommand]
    private void ToggleGameExpanded(FilterGameItem? game)
    {
        if (game != null)
            game.IsExpanded = !game.IsExpanded;
    }

    [RelayCommand]
    private void ClearGroups()
    {
        groups.Clear();
        foreach (FilterOptionItem option in Groups)
            option.SetChecked(false);
        RaiseChanged();
    }

    [RelayCommand]
    private void ClearGames()
    {
        wholeGames.Clear();
        planes.Clear();
        SyncGameTicks();
        RaiseChanged();
    }

    [RelayCommand]
    private void ClearDevices()
    {
        devices.Clear();
        foreach (FilterOptionItem option in Devices)
            option.SetChecked(false);
        RaiseChanged();
    }

    // Every filter off (the typed search too).
    [RelayCommand]
    public void ClearAll()
    {
        batch++;
        try
        {
            groups.Clear();
            wholeGames.Clear();
            planes.Clear();
            devices.Clear();
            foreach (FilterOptionItem option in Groups.Concat(Devices))
                option.SetChecked(false);
            SyncGameTicks();
            Bound = "all";
            Kind = "all";
            ClearSearchRequested?.Invoke(this, EventArgs.Empty);
            SearchText = string.Empty;
        }
        finally
        {
            batch--;
        }
        RaiseChanged();
    }

    // Clear all: the page empties its search box too.
    public event EventHandler? ClearSearchRequested;

    // ---- for code and tests ----

    public void SelectGroups(params string[] names)
    {
        foreach (string name in names)
            groups.Add(name);
        foreach (FilterOptionItem option in Groups)
            option.SetChecked(groups.Contains(option.Key));
        RaiseChanged();
    }

    public void SelectGame(string game)
    {
        FilterGameItem? item = Games.FirstOrDefault(g => string.Equals(g.Game, game, StringComparison.OrdinalIgnoreCase));
        if (item != null)
            item.IsChecked = true;
        else
        {
            wholeGames.Add(game);
            RaiseChanged();
        }
    }

    // Only these aircraft of the game (the game's other aircraft off).
    public void SelectOnlyPlanes(string game, params string[] names)
    {
        string prefix = game + "|";
        wholeGames.Remove(game);
        planes.RemoveWhere(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        foreach (string name in names)
            planes.Add(FilterSubject.Key(game, name));
        FilterGameItem? item = Games.FirstOrDefault(g => string.Equals(g.Game, game, StringComparison.OrdinalIgnoreCase));
        if (item != null && item.Planes.Count > 0 && item.Planes.All(p => planes.Contains(p.Key)))
        {
            planes.RemoveWhere(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            wholeGames.Add(game);
        }
        SyncGameTicks();
        RaiseChanged();
    }

    public void SelectDevices(params string[] names)
    {
        foreach (string name in names)
            devices.Add(name);
        foreach (FilterOptionItem option in Devices)
            option.SetChecked(devices.Contains(option.Key));
        RaiseChanged();
    }

    // ---- remembered per page ----

    public ListFilterState ToState() => new()
    {
        Groups = groups.OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList(),
        Games = wholeGames.OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList(),
        Planes = planes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
        Devices = devices.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(),
        Bound = Bound,
        Kind = Kind,
        SearchScope = SearchScope
    };

    public void LoadState(ListFilterState? state)
    {
        if (state == null)
            return;
        batch++;
        try
        {
            groups.Clear();
            groups.UnionWith(state.Groups ?? []);
            wholeGames.Clear();
            wholeGames.UnionWith(state.Games ?? []);
            planes.Clear();
            planes.UnionWith((state.Planes ?? []).Where(p => p.Contains('|')));
            devices.Clear();
            devices.UnionWith(state.Devices ?? []);
            Bound = state.Bound is "bound" or "unbound" ? state.Bound : "all";
            Kind = state.Kind is "axis" or "button" ? state.Kind : "all";
            SearchScope = state.SearchScope;
            if (subjects.Count > 0)
                RebuildOptions();
            else
                SyncGameTicks();
        }
        finally
        {
            batch--;
        }
        RaiseChanged();
    }

    // ---- change handling ----

    private void RaiseChanged()
    {
        if (batch > 0)
        {
            return;
        }
        UpdateCounts();
        RebuildChips();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCounts()
    {
        Dictionary<string, int> groupCounts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> gameCounts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> planeCounts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> deviceCounts = new(StringComparer.OrdinalIgnoreCase);
        int shown = 0, boundN = 0, unboundN = 0, axisN = 0, buttonN = 0;
        foreach (FilterSubject s in subjects)
        {
            if (!PassSearch(s))
                continue;
            bool g = PassGroups(s), ga = PassGames(s), d = PassDevices(s), b = PassBound(s), k = PassKind(s);
            if (ga && d && b && k)
            {
                foreach (string group in s.Groups)
                    groupCounts[group] = groupCounts.GetValueOrDefault(group) + 1;
            }
            if (g && d && b && k)
            {
                foreach (string game in s.Games)
                    gameCounts[game] = gameCounts.GetValueOrDefault(game) + 1;
                foreach (string plane in s.GamePlanes)
                    planeCounts[plane] = planeCounts.GetValueOrDefault(plane) + 1;
            }
            if (g && ga && b && k && s.Device.Length > 0)
                deviceCounts[s.Device] = deviceCounts.GetValueOrDefault(s.Device) + 1;
            if (g && ga && d && k)
            {
                if (s.IsBound)
                    boundN++;
                else
                    unboundN++;
            }
            if (g && ga && d && b)
            {
                if (s.IsAxis)
                    axisN++;
                else
                    buttonN++;
            }
            if (g && ga && d && b && k)
                shown++;
        }
        foreach (FilterOptionItem option in Groups)
            option.Count = groupCounts.GetValueOrDefault(option.Key);
        foreach (FilterOptionItem option in Devices)
            option.Count = deviceCounts.GetValueOrDefault(option.Key);
        foreach (FilterGameItem game in Games)
        {
            game.Count = gameCounts.GetValueOrDefault(game.Game);
            foreach (FilterOptionItem plane in game.Planes)
                plane.Count = planeCounts.GetValueOrDefault(plane.Key);
        }
        ShownCount = shown;
        TotalCount = subjects.Count;
        BoundCount = boundN;
        UnboundCount = unboundN;
        AxisCount = axisN;
        ButtonCount = buttonN;
        OnPropertyChanged(nameof(BoundAllLabel));
        OnPropertyChanged(nameof(BoundLabel));
        OnPropertyChanged(nameof(UnboundLabel));
        OnPropertyChanged(nameof(KindAllLabel));
        OnPropertyChanged(nameof(AxisLabel));
        OnPropertyChanged(nameof(ButtonLabel));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(IsFiltered));
    }

    private void RebuildChips()
    {
        ActiveChips.Clear();
        foreach (string group in groups.OrderBy(g => g, StringComparer.OrdinalIgnoreCase))
        {
            string name = group;
            ActiveChips.Add(new FilterChipItem(T("Group: {0}", name == FilterSubject.NoGroup ? T("no group") : name), T("Only relations in this group"),
                () => { groups.Remove(name); SyncOptionTicks(); RaiseChanged(); }));
        }
        foreach (string game in wholeGames.OrderBy(g => DisplayText.Game(g), StringComparer.OrdinalIgnoreCase))
        {
            string id = game;
            ActiveChips.Add(new FilterChipItem(DisplayText.Game(id), T("Only relations with an action of {0}", DisplayText.Game(id)),
                () => { wholeGames.Remove(id); SyncGameTicks(); RaiseChanged(); }));
        }
        foreach (IGrouping<string, string> game in planes.GroupBy(p => p[..p.IndexOf('|')], StringComparer.OrdinalIgnoreCase).OrderBy(g => DisplayText.Game(g.Key), StringComparer.OrdinalIgnoreCase))
        {
            string id = game.Key;
            List<string> names = game.Select(p => p[(p.IndexOf('|') + 1)..]).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            string shownNames = names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + " +" + (names.Count - 3).ToString(CultureInfo.InvariantCulture);
            ActiveChips.Add(new FilterChipItem(DisplayText.Game(id) + ": " + shownNames, T("Only relations with an action for {0}", string.Join(", ", names)),
                () => { string prefix = id + "|"; planes.RemoveWhere(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)); SyncGameTicks(); RaiseChanged(); }));
        }
        foreach (string device in devices.OrderBy(d => DisplayText.ShortDevice(d), StringComparer.OrdinalIgnoreCase))
        {
            string key = device;
            ActiveChips.Add(new FilterChipItem(T("Device: {0}", DisplayText.ShortDevice(key)), key, () => { devices.Remove(key); SyncOptionTicks(); RaiseChanged(); }));
        }
        if (Bound != "all")
            ActiveChips.Add(new FilterChipItem(Bound == "bound" ? T("Bound") : T("Not bound"), Bound == "bound" ? T("Only bound relations") : T("Only relations not bound yet"), () => Bound = "all"));
        if (Kind != "all")
            ActiveChips.Add(new FilterChipItem(Kind == "axis" ? T("Axes") : T("Buttons"), Kind == "axis" ? T("Only axis relations") : T("Only button relations"), () => Kind = "all"));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(GroupsLabel));
        OnPropertyChanged(nameof(GamesLabel));
        OnPropertyChanged(nameof(DevicesLabel));
        OnPropertyChanged(nameof(HasGroupFilter));
        OnPropertyChanged(nameof(HasGameFilter));
        OnPropertyChanged(nameof(HasDeviceFilter));
        OnPropertyChanged(nameof(ResultText));
    }

    private void SyncOptionTicks()
    {
        foreach (FilterOptionItem option in Groups)
            option.SetChecked(groups.Contains(option.Key));
        foreach (FilterOptionItem option in Devices)
            option.SetChecked(devices.Contains(option.Key));
    }
}
