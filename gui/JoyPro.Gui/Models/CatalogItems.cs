using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// The catalog browser of the Relations page ("Open catalog", engine relations browse): a game, its aircraft, the game's
// own categories and every action in them, with what is used already. ViewModels/MainWindowViewModel.Catalog.cs.

// A game of the catalog's game picker (with how many actions and aircraft it has).
public sealed class CatalogGameOption
{
    public CatalogGameOption(string game, int actions, IReadOnlyList<CatalogPlaneOption> planes)
    {
        Game = game;
        Actions = actions;
        Planes = planes;
    }

    public string Game { get; }

    public int Actions { get; }

    public IReadOnlyList<CatalogPlaneOption> Planes { get; }

    public bool HasPlanes => Planes.Count > 0;

    public string Label => DisplayText.Game(Game) + " (" + (HasPlanes ? T("{0} aircraft", Planes.Count) : T("{0} actions", Actions)) + ")";

    public override string ToString() => Label;
}

public sealed class CatalogPlaneOption
{
    public CatalogPlaneOption(string plane, int actions, int inRelations, bool inProfile)
    {
        Plane = plane;
        Actions = actions;
        InRelations = inRelations;
        InProfile = inProfile;
    }

    public string Plane { get; }

    public int Actions { get; }

    public int InRelations { get; }

    // A relation of the profile has an action for it.
    public bool InProfile { get; }

    public string Label => Plane + "  (" + (InRelations > 0 ? T("{0} actions, {1} used", Actions, InRelations) : T("{0} actions", Actions)) + ")";

    public override string ToString() => Label;
}

// One action of the catalog: the search row (tick, aircraft selector, node spec) and what uses it already.
public sealed class CatalogRowItem
{
    public CatalogRowItem(BuilderSearchResultItem row, string usedText, string boundText)
    {
        Row = row;
        UsedText = usedText;
        BoundText = boundText;
    }

    public BuilderSearchResultItem Row { get; }

    // "in 'Gear', 'Gear DCS'" (the relations that use it); empty when free.
    public string UsedText { get; }

    // "bound: WarBRD · BTN 8" of the first relation using it that is bound.
    public string BoundText { get; }

    public bool IsUsed => UsedText.Length > 0;

    public bool IsBound => BoundText.Length > 0;
}

// A category of the game ("HOTAS", "Landing gear panel") with its actions; the rows are made when it opens.
public sealed partial class CatalogCategoryItem : ObservableObject
{
    private bool updating;

    public CatalogCategoryItem(string name, IReadOnlyList<CatalogRowItem> rows)
    {
        Name = name;
        Rows = rows;
        foreach (CatalogRowItem row in rows)
            row.Row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(BuilderSearchResultItem.IsChecked) or nameof(BuilderSearchResultItem.IsInOpenRelation))
                    RefreshTick();
            };
    }

    public string Name { get; }

    public IReadOnlyList<CatalogRowItem> Rows { get; }

    // The rows shown (open and through the filters).
    public ObservableCollection<CatalogRowItem> VisibleRows { get; } = [];

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private bool isVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int matching;

    public int UsedCount => Rows.Count(r => r.IsUsed);

    public int InOpenRelationCount => Rows.Count(r => r.Row.IsInOpenRelation);

    // "20 actions · 3 used · 1 here" (with a filter: "4 of 20").
    public string CountText => (Matching != Rows.Count ? T("{0} of {1} actions", Matching, Rows.Count) : Plural("{0} action", "{0} actions", Rows.Count)) +
                               (UsedCount > 0 ? " · " + T("{0} used", UsedCount) : string.Empty) +
                               (InOpenRelationCount > 0 ? " · " + T("{0} in this relation", InOpenRelationCount) : string.Empty);

    private bool? isChecked = false;

    // Tick every action of the category shown (or none); a mix shows as a partial tick.
    public bool? IsChecked
    {
        get => isChecked;
        set
        {
            bool tick = value == true;
            updating = true;
            try
            {
                foreach (CatalogRowItem row in (VisibleRows.Count > 0 ? VisibleRows : (IEnumerable<CatalogRowItem>)Rows).Where(r => r.Row.CanCheck))
                    row.Row.IsChecked = tick;
            }
            finally
            {
                updating = false;
            }
            RefreshTick();
        }
    }

    public bool HasTickable => Rows.Any(r => r.Row.CanCheck);

    internal void RefreshTick()
    {
        if (updating)
            return;
        List<CatalogRowItem> tickable = Rows.Where(r => r.Row.CanCheck).ToList();
        int ticked = tickable.Count(r => r.Row.IsChecked);
        bool? state = ticked == 0 ? false : ticked == tickable.Count ? true : null;
        if (isChecked != state)
        {
            isChecked = state;
            OnPropertyChanged(nameof(IsChecked));
        }
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasTickable));
    }
}
