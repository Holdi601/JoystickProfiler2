using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        RelationFilter.ClearSearchRequested += (_, _) => RelationSearchText = string.Empty;
        BindFilter.ClearSearchRequested += (_, _) => BindSearchText = string.Empty;
    }

    private void RememberFilter(ListFilterViewModel filter)
    {
        ListPrefs.Filters[filter.Page] = filter.ToState();
        SaveListPreferences();
    }

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
        return new FilterSubject(relation.Name, DisplayText.List(relation.Groups), gamePlanes, device, isBound, relation.IsAxis, string.Join("\u0001", words));
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
        List<RelationListItem> shown = allRelations
            .Where(r => RelationFilter.Matches(relationSubjects.GetValueOrDefault(r.Name)))
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
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
