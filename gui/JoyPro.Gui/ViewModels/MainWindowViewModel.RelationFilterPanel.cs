using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// The Relations page's filter block (groups, games, devices, bound / not bound, axes / buttons, the sort and what the
// search looks at) folds up into one line: at 1280x800 it took a third of the narrow pane's height. Open / closed is
// remembered (list-preferences.json "RelationFiltersOpen"); closed, the line says which filters are on and how many
// relations are shown.
public sealed partial class MainWindowViewModel
{
    private bool relationFilterSummaryWired;

    public bool IsRelationFiltersOpen
    {
        get => ListPrefs.RelationFiltersOpen;
        set
        {
            if (ListPrefs.RelationFiltersOpen == value)
                return;
            ListPrefs.RelationFiltersOpen = value;
            SaveListPreferences();
            OnPropertyChanged();
        }
    }

    // "DCS · Bound · Sort: Device · 124 of 524 shown"; "No filters · 524 in all".
    public string RelationFiltersSummary
    {
        get
        {
            List<string> parts = RelationFilter.ActiveChips.Select(c => c.Label).ToList();
            if (RelationFilter.SearchScope == ListSearchScope.Name)
                parts.Add(T("Search: names only"));
            else if (RelationFilter.SearchScope == ListSearchScope.Everything)
                parts.Add(T("Search: everything"));
            if (RelationSort.Levels.Count > 0)
                parts.Add(RelationSort.Label);
            if (parts.Count == 0)
                parts.Add(T("No filters"));
            parts.Add(RelationFilter.ResultText);
            return string.Join(" · ", parts);
        }
    }

    // The relation filter and sort say when the summary changes (called once the filters are set up).
    private void WireRelationFilterSummary()
    {
        if (relationFilterSummaryWired)
            return;
        relationFilterSummaryWired = true;
        RelationFilter.PropertyChanged += OnRelationFilterSummaryPartChanged;
        RelationSort.PropertyChanged += OnRelationFilterSummaryPartChanged;
    }

    private void OnRelationFilterSummaryPartChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ListFilterViewModel.ResultText) or nameof(ListFilterViewModel.IsFiltered) or nameof(ListFilterViewModel.SearchScopeIndex)
            or nameof(ListSortViewModel.Label))
            OnPropertyChanged(nameof(RelationFiltersSummary));
    }
}
