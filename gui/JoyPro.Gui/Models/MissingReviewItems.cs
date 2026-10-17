using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// A relation of "Suggest missing for every relation": what it lacks (actions of games and aircraft it has none for, no
// other relation uses them), with a tick each and one for all.
public sealed partial class MissingReviewRelationItem : ObservableObject
{
    private bool updating;

    public MissingReviewRelationItem(string name, bool isAxis, IReadOnlyList<BuilderSuggestionItem> items, int newGames, int usedElsewhere, int total)
    {
        Name = name;
        IsAxis = isAxis;
        NewGames = newGames;
        UsedElsewhere = usedElsewhere;
        Total = total;
        foreach (BuilderSuggestionItem item in items)
        {
            Items.Add(item);
            item.PropertyChanged += OnItemChanged;
        }
    }

    public string Name { get; }

    public bool IsAxis { get; }

    public int NewGames { get; }

    public int UsedElsewhere { get; }

    // Every free missing action (Items holds the best of them).
    public int Total { get; }

    public ObservableCollection<BuilderSuggestionItem> Items { get; } = [];

    public event EventHandler? TicksChanged;

    // "5 new games · 12 more aircraft · 3 used elsewhere".
    public string Summary
    {
        get
        {
            int aircraft = Items.Where(i => i.IsNewAircraft).SelectMany(i => i.NewPlanes).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            List<string> parts = [];
            if (NewGames > 0)
                parts.Add(Plural("{0} new game", "{0} new games", NewGames));
            if (aircraft > 0)
                parts.Add(T("{0} more aircraft", aircraft));
            if (Total > Items.Count)
                parts.Add(T("best {0} of {1}", Items.Count, Total));
            if (UsedElsewhere > 0)
                parts.Add(T("{0} used elsewhere", UsedElsewhere));
            return string.Join(" · ", parts);
        }
    }

    [ObservableProperty]
    private bool isExpanded;

    public bool? IsChecked
    {
        get
        {
            int ticked = Items.Count(i => i.IsChecked);
            return ticked == 0 ? false : ticked == Items.Count ? true : null;
        }
        set
        {
            updating = true;
            try
            {
                foreach (BuilderSuggestionItem item in Items)
                    item.IsChecked = value == true;
            }
            finally
            {
                updating = false;
            }
            OnPropertyChanged();
            TicksChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void RemoveItems(IEnumerable<BuilderSuggestionItem> done)
    {
        foreach (BuilderSuggestionItem item in done.ToList())
        {
            item.PropertyChanged -= OnItemChanged;
            Items.Remove(item);
        }
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(Summary));
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BuilderSuggestionItem.IsChecked) || updating)
            return;
        OnPropertyChanged(nameof(IsChecked));
        TicksChanged?.Invoke(this, EventArgs.Empty);
    }
}
