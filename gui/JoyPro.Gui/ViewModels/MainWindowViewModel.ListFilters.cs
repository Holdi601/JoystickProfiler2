using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The filter bars of the Relations page and the Binds page (ListFilterViewModel): the same filters on both (search,
// groups, games with their aircraft, devices, bound / not bound, axis / button), each page with its own ticks, remembered
// in list-preferences.json. The relations are filter subjects built when the list or the binds change; a filter change
// only filters them (RelationFilterMilliseconds / BindFilterMilliseconds say how long, the journey test checks it).
public sealed partial class MainWindowViewModel
{
    public ListFilterViewModel RelationFilter { get; } = new("relations");

    public ListFilterViewModel BindFilter { get; } = new("binds");

    // The tiered sort of each list (ViewModels/ListSortViewModel.cs), remembered with the filters.
    public ListSortViewModel RelationSort { get; } = new("relations");

    public ListSortViewModel BindSort { get; } = new("binds");

    // The tiered sort of the action catalog and of the action search results (game, aircraft, category, title, axis /
    // button, last updated); no level = their own order (categories, best matches first).
    public ListSortViewModel CatalogSort { get; } = ListSortViewModel.ForActions("catalog");

    public ListSortViewModel BuilderSearchSort { get; } = ListSortViewModel.ForActions("action-search");

    private bool actionSortsInitialized;

    private void EnsureActionSorts()
    {
        if (actionSortsInitialized)
            return;
        actionSortsInitialized = true;
        if (ListPrefs.Sorts.TryGetValue(CatalogSort.Page, out List<string>? catalogSort))
            CatalogSort.LoadState(catalogSort);
        if (ListPrefs.Sorts.TryGetValue(BuilderSearchSort.Page, out List<string>? searchSort))
            BuilderSearchSort.LoadState(searchSort);
        CatalogSort.Changed += (_, _) =>
        {
            SetCatalogRows(catalogRows.ToList(), catalogCategoryOrder.ToList(), keepExpanded: true);
            RememberSort(CatalogSort);
        };
        BuilderSearchSort.Changed += (_, _) =>
        {
            RebuildBuilderSearchRows();
            RememberSort(BuilderSearchSort);
        };
    }

    // What an action row (catalog, search) is sorted by: its readable title, game, aircraft, category, kind and the date
    // of the game's command list.
    internal ListSortRow SortRowOfAction(BuilderSearchResultItem row, string? plane = null)
    {
        string onPlane = plane ?? (row.Game == "DCS" ? row.SelectedPlanes.Concat(row.Planes).FirstOrDefault() ?? string.Empty : string.Empty);
        return ListSortRow.ForAction(row.DisplayTitle, DisplayText.Game(row.Game), onPlane, row.Category, row.IsAxis, ActionListDate(row.Game, onPlane));
    }

    private readonly Dictionary<string, DateTime> actionListDates = new(StringComparer.OrdinalIgnoreCase);

    // When the command list of a game (DCS: of the aircraft) was last built: the DB file's time; MinValue if unknown.
    internal DateTime ActionListDate(string game, string plane)
    {
        string key = game + "|" + plane;
        if (actionListDates.TryGetValue(key, out DateTime known))
            return known;
        DateTime date = DateTime.MinValue;
        try
        {
            if (string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase))
            {
                if (TryResolveDcsPlaneFile(plane, out string file) && File.Exists(file))
                    date = File.GetLastWriteTimeUtc(file);
            }
            else
            {
                string folder = GetGameDbDirectory(game);
                if (Directory.Exists(folder))
                    date = Directory.EnumerateFiles(folder).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            }
        }
        catch
        {
            date = DateTime.MinValue;
        }
        actionListDates[key] = date;
        return date;
    }

    private ListPreferences? listPreferences;
    private bool listFiltersInitialized;
    private readonly Dictionary<string, FilterSubject> relationSubjects = new(StringComparer.OrdinalIgnoreCase);

    // How long the last filter change took to fill the list (ms), for the journey test's performance check.
    internal double RelationFilterMilliseconds { get; private set; }

    internal double BindFilterMilliseconds { get; private set; }

    private string ListPreferencesPath => ListPreferences.DefaultPath;

    internal ListPreferences ListPrefs
    {
        get
        {
            if (listPreferences == null)
                listPreferences = persistUiFiles ? ListPreferences.Load(ListPreferencesPath) : new ListPreferences();
            return listPreferences;
        }
    }

    private void SaveListPreferences()
    {
        if (persistUiFiles && listPreferences != null)
            listPreferences.Save(ListPreferencesPath);
    }

    private void EnsureListFilters()
    {
        if (listFiltersInitialized)
            return;
        listFiltersInitialized = true;
        if (ListPrefs.Filters.TryGetValue(RelationFilter.Page, out ListFilterState? relationState))
            RelationFilter.LoadState(relationState);
        if (ListPrefs.Filters.TryGetValue(BindFilter.Page, out ListFilterState? bindState))
            BindFilter.LoadState(bindState);
        RelationFilter.Changed += (_, _) =>
        {
            ApplyRelationFilter();
            RememberFilter(RelationFilter);
        };
        BindFilter.Changed += (_, _) =>
        {
            ApplyBindFilter();
            RememberFilter(BindFilter);
        };
        if (ListPrefs.Sorts.TryGetValue(RelationSort.Page, out List<string>? relationSort))
            RelationSort.LoadState(relationSort);
        if (ListPrefs.Sorts.TryGetValue(BindSort.Page, out List<string>? bindSort))
            BindSort.LoadState(bindSort);
        RelationSort.Changed += (_, _) =>
        {
            ApplyRelationFilter();
            RememberSort(RelationSort);
        };
        BindSort.Changed += (_, _) =>
        {
            ApplyBindFilter();
            RememberSort(BindSort);
        };
        RelationFilter.ClearSearchRequested += (_, _) => RelationSearchText = string.Empty;
        BindFilter.ClearSearchRequested += (_, _) => BindSearchText = string.Empty;
    }

    private void RememberFilter(ListFilterViewModel filter)
    {
        ListPrefs.Filters[filter.Page] = filter.ToState();
        SaveListPreferences();
    }

    private void RememberSort(ListSortViewModel sort)
    {
        ListPrefs.Sorts[sort.Page] = sort.ToState();
        SaveListPreferences();
    }

    // What a row is sorted by: the device as shown (its alias while Settings shows aliases), the input as it is.
    internal static ListSortRow SortRowOf(RelationListItem relation) =>
        new(relation.Name, relation.IsBound ? DisplayText.ShortDevice(relation.BindDeviceFull) : string.Empty, relation.BindInputRaw, relation.IsAxis,
            string.Join(", ", relation.GameList), int.TryParse(relation.Planes, out int planes) ? planes : 0, relation.Groups, relation.IsBound,
            string.Join(", ", relation.ModifierChips));

    internal static ListSortRow SortRowOf(BindListItem bind, RelationListItem? relation) =>
        new(bind.RelationName, bind.HasBind ? DisplayText.ShortDevice(bind.Device) : string.Empty, bind.HasBind ? bind.Input : string.Empty, bind.IsAxis,
            relation != null ? string.Join(", ", relation.GameList) : string.Empty, relation != null && int.TryParse(relation.Planes, out int planes) ? planes : 0,
            relation?.Groups ?? string.Empty, bind.HasBind, bind.HasBind ? string.Join(", ", bind.ModifierList) : string.Empty);

    // The search box's text goes into the filter (which shows the list again).
    private void SetRelationSearch(string value)
    {
        EnsureListFilters();
        RelationFilter.SearchText = value ?? string.Empty;
    }

    private void SetBindSearch(string value)
    {
        EnsureListFilters();
        BindFilter.SearchText = value ?? string.Empty;
    }

    private static FilterSubject SubjectOf(RelationListItem relation, string device, bool isBound, string input, IEnumerable<string> modifiers)
    {
        IReadOnlyCollection<string> gamePlanes = relation.ActiveGamePlanes.Count > 0
            ? relation.ActiveGamePlanes
            : DisplayText.List(relation.Games).Select(g => g.Equals("DCS", StringComparison.OrdinalIgnoreCase) ? "DCS|" : g + "|" + g).Where(k => !k.EndsWith('|')).ToList();
        List<string> words =
        [
            relation.Name,
            relation.Games,
            string.Join(" ", DisplayText.List(relation.Games).Select(DisplayText.Game)),
            string.Join(" ", gamePlanes.Select(p => p[(p.IndexOf('|') + 1)..])),
            relation.Groups,
            relation.IsAxis ? "axis" : "button"
        ];
        if (isBound)
        {
            words.Add(DisplayText.ShortDevice(device));
            words.Add(input);
            words.Add(DeviceMarkerText.ShortLabel(input));
            words.AddRange(modifiers);
        }
        else
        {
            words.Add(T("not bound unbound"));
        }
        return new FilterSubject(relation.Name, DisplayText.List(relation.Groups), gamePlanes, device, isBound, relation.IsAxis, string.Join("\u0001", words),
            relation.ActionTitles);
    }

    // With a typed search the best fits come first (the relation called so, then names starting with it, then names
    // with every word, then the ones found by their actions), each part in the page's sort.
    private static IEnumerable<T> RankBySearch<T>(ListFilterViewModel filter, IEnumerable<T> rows, Func<T, FilterSubject?> subject, Func<IEnumerable<T>, IEnumerable<T>> sort)
    {
        if (!filter.HasSearch)
            return sort(rows);
        return rows.GroupBy(r => filter.SearchRank(subject(r))).OrderBy(g => g.Key).SelectMany(g => sort(g));
    }

    // The relation list changed (parse, a bind, a card's counts): new subjects, then the filters.
    private void RebuildRelationSubjects()
    {
        EnsureListFilters();
        relationSubjects.Clear();
        foreach (RelationListItem relation in allRelations)
            relationSubjects[relation.Name] = SubjectOf(relation, relation.BindDeviceFull, relation.IsBound, relation.BindInputRaw, relation.Modifiers);
        RelationFilter.SetSubjects(relationSubjects.Values);
    }

    // Shows the relations that pass the filters (and keeps the open relation selected when it is still in the list).
    private void ApplyRelationFilter()
    {
        Stopwatch watch = Stopwatch.StartNew();
        List<RelationListItem> shown = RankBySearch(RelationFilter, allRelations
            .Where(r => RelationFilter.Matches(relationSubjects.GetValueOrDefault(r.Name)))
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase), r => relationSubjects.GetValueOrDefault(r.Name), rows => RelationSort.Sort(rows, SortRowOf))
            .ToList();
        FillRelationList(shown);
        watch.Stop();
        RelationFilterMilliseconds = watch.Elapsed.TotalMilliseconds;
        OnPropertyChanged(nameof(HasNoRelationMatches));
    }

    private List<FilterSubject> BuildBindSubjects(IEnumerable<BindListItem> rows)
    {
        Dictionary<string, RelationListItem> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (RelationListItem relation in allRelations)
            byName.TryAdd(relation.Name, relation);
        List<FilterSubject> list = [];
        foreach (BindListItem row in rows)
        {
            RelationListItem relation = byName.TryGetValue(row.RelationName, out RelationListItem? r)
                ? r
                : new RelationListItem { Name = row.RelationName, Type = row.Type, Groups = "<none>" };
            list.Add(SubjectOf(relation, row.HasBind ? row.Device : string.Empty, row.HasBind, row.HasBind ? row.Input : string.Empty, row.ModifierList));
        }
        return list;
    }

    // Unbound relations of the profile that a page's filter hides: a reason to clear the filter when one should open.
    internal bool RelationHiddenByFilter(string name) =>
        allRelations.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) &&
        !Relations.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
}
