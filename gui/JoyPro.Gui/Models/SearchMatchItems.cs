using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// One title of a relation's action ("relations list --titles" TitleList): the action (its index in the relation and its
// id), its game and, in DCS, the aircraft that have this title.
public sealed record ActionTitleEntry(string Title, string Game, int Node, string Id, IReadOnlyList<string> Planes);

// What a search found in a relation's actions, shown under its card (tester report on 2.4.2: "Master" found 21 relations
// but only their names showed). The word that matched is Hit; Where says the game and the aircraft.
public sealed class SearchMatchLine
{
    public SearchMatchLine(string relation, string before, string hit, string after, string where, string game, int node, string id)
    {
        Relation = relation;
        Before = before;
        Hit = hit;
        After = after;
        Where = where;
        Game = game;
        Node = node;
        Id = id;
    }

    public string Relation { get; }

    public string Before { get; }

    public string Hit { get; }

    public string After { get; }

    public string Where { get; }

    public string WhereText => Where.Length > 0 ? "  ·  " + Where : string.Empty;

    public string Game { get; }

    public int Node { get; }

    public string Id { get; }

    public string Title => Before + Hit + After;

    public string Tooltip => T("{0} ({1}): open it in the relation", Title, Where);
}

// The match lines of one relation on one page (its Relations card and its Binds row each have their own: the two pages
// search separately). Observable: a new search changes the lines of a card that stays in the list.
public sealed class SearchMatchInfo : ObservableObject
{
    private IReadOnlyList<SearchMatchLine> lines = [];
    private int more;

    public IReadOnlyList<SearchMatchLine> Lines => lines;

    public bool HasLines => lines.Count > 0;

    public bool HasMore => more > 0;

    public string MoreText => more > 0 ? T("+{0} more", more) : string.Empty;

    public bool IsEmpty => lines.Count == 0 && more == 0;

    public void Set(IReadOnlyList<SearchMatchLine> value, int moreCount)
    {
        if (value.Count == 0 && lines.Count == 0 && moreCount == more)
            return;
        lines = value;
        more = moreCount;
        OnPropertyChanged(nameof(Lines));
        OnPropertyChanged(nameof(HasLines));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(MoreText));
    }

    public void Clear() => Set([], 0);
}
