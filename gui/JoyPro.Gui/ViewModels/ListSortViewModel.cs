using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// One sort key of the Relations and Binds lists: "device", "Device".
public sealed class SortKeyOption
{
    public SortKeyOption(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }

    public string Label { get; }

    public override string ToString() => Label;
}

// One level of a list's sort ("1. Device, A to Z").
public sealed partial class SortLevelItem : ObservableObject
{
    private readonly ListSortViewModel owner;

    public SortLevelItem(ListSortViewModel owner, SortKeyOption key, bool descending)
    {
        this.owner = owner;
        this.key = key;
        this.descending = descending;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectionLabel))]
    private SortKeyOption key;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectionLabel))]
    private bool descending;

    [ObservableProperty]
    private string number = string.Empty;

    public IReadOnlyList<SortKeyOption> Options => owner.Options;

    // "A to Z" / "Z to A"; numbers and states read "low to high" / "high to low".
    public string DirectionLabel => ListSortViewModel.IsTextKey(Key.Key)
        ? (Descending ? T("Z to A") : T("A to Z"))
        : (Descending ? T("High to low") : T("Low to high"));

    partial void OnKeyChanged(SortKeyOption value) => owner.LevelChanged();

    partial void OnDescendingChanged(bool value) => owner.LevelChanged();

    [RelayCommand]
    private void ToggleDirection() => Descending = !Descending;

    [RelayCommand]
    private void Remove() => owner.RemoveLevel(this);

    [RelayCommand]
    private void MoveUp() => owner.MoveLevel(this, -1);

    [RelayCommand]
    private void MoveDown() => owner.MoveLevel(this, 1);
}

// The tiered sort of a list (Relations page, Binds page): levels in order, each a key and a direction, e.g. 1) Device,
// 2) Input (natural: BTN 2 before BTN 10, the axes X, Y, Z, RX, RY, RZ, sliders), 3) Name. No level = the list's own
// order (by name). Remembered per list (list-preferences.json "Sorts": "key:asc").
public sealed partial class ListSortViewModel : ObservableObject
{
    private bool loading;

    public ListSortViewModel(string page)
    {
        Page = page;
        Options =
        [
            new SortKeyOption("name", T("Name")),
            new SortKeyOption("device", T("Device")),
            new SortKeyOption("input", T("Input")),
            new SortKeyOption("kind", T("Axis / button")),
            new SortKeyOption("games", T("Games")),
            new SortKeyOption("aircraft", T("Aircraft count")),
            new SortKeyOption("group", T("Group")),
            new SortKeyOption("bound", T("Bound / not bound")),
            new SortKeyOption("modifiers", T("Modifiers"))
        ];
        Levels.CollectionChanged += (_, _) => Renumber();
    }

    public string Page { get; }

    public IReadOnlyList<SortKeyOption> Options { get; }

    public ObservableCollection<SortLevelItem> Levels { get; } = [];

    public event EventHandler? Changed;

    public bool HasLevels => Levels.Count > 0;

    public bool CanAddLevel => Levels.Count < Options.Count;

    // "Sort: Device, Input" / "Sort: Name".
    public string Label => Levels.Count == 0 ? T("Sort: Name") : T("Sort: {0}", string.Join(", ", Levels.Select(l => l.Key.Label + (l.Descending ? " ↓" : string.Empty))));

    internal static bool IsTextKey(string key) => key is "name" or "device" or "games" or "group" or "modifiers" or "input";

    private SortKeyOption Option(string key) => Options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Options[0];

    // A level with the first key not used yet.
    [RelayCommand]
    public void AddLevel()
    {
        SortKeyOption? next = Options.FirstOrDefault(o => Levels.All(l => l.Key.Key != o.Key));
        if (next == null)
            return;
        Levels.Add(new SortLevelItem(this, next, false));
        LevelChanged();
    }

    // Back to the list's own order.
    [RelayCommand]
    public void Reset()
    {
        if (Levels.Count == 0)
            return;
        Levels.Clear();
        LevelChanged();
    }

    public void Add(string key, bool descending)
    {
        Levels.Add(new SortLevelItem(this, Option(key), descending));
        LevelChanged();
    }

    internal void RemoveLevel(SortLevelItem level)
    {
        if (Levels.Remove(level))
            LevelChanged();
    }

    internal void MoveLevel(SortLevelItem level, int delta)
    {
        int at = Levels.IndexOf(level);
        int to = at + delta;
        if (at < 0 || to < 0 || to >= Levels.Count)
            return;
        Levels.Move(at, to);
        LevelChanged();
    }

    // A click on a column-like sort button: that key becomes the first level (a second click turns it around), the
    // others stay behind it.
    public void SetPrimary(string key)
    {
        SortLevelItem? existing = Levels.FirstOrDefault(l => string.Equals(l.Key.Key, key, StringComparison.OrdinalIgnoreCase));
        loading = true;
        try
        {
            if (existing != null && Levels.IndexOf(existing) == 0)
                existing.Descending = !existing.Descending;
            else if (existing != null)
                Levels.Move(Levels.IndexOf(existing), 0);
            else
                Levels.Insert(0, new SortLevelItem(this, Option(key), false));
        }
        finally
        {
            loading = false;
        }
        LevelChanged();
    }

    internal void LevelChanged()
    {
        Renumber();
        OnPropertyChanged(nameof(HasLevels));
        OnPropertyChanged(nameof(CanAddLevel));
        OnPropertyChanged(nameof(Label));
        if (!loading)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Renumber()
    {
        for (int i = 0; i < Levels.Count; i++)
            Levels[i].Number = (i + 1).ToString(CultureInfo.InvariantCulture) + ".";
    }

    public List<string> ToState() => Levels.Select(l => l.Key.Key + ":" + (l.Descending ? "desc" : "asc")).ToList();

    public void LoadState(IEnumerable<string>? state)
    {
        loading = true;
        try
        {
            Levels.Clear();
            foreach (string entry in state ?? [])
            {
                string[] parts = (entry ?? string.Empty).Split(':');
                if (parts.Length == 0 || Options.All(o => !string.Equals(o.Key, parts[0], StringComparison.OrdinalIgnoreCase)) ||
                    Levels.Any(l => string.Equals(l.Key.Key, parts[0], StringComparison.OrdinalIgnoreCase)))
                    continue;
                Levels.Add(new SortLevelItem(this, Option(parts[0]), parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase)));
            }
        }
        finally
        {
            loading = false;
        }
        LevelChanged();
    }

    // The rows in the levels' order; the name decides last (the list's own order), so equal rows never jump.
    public IEnumerable<T> Sort<T>(IEnumerable<T> rows, Func<T, ListSortRow> row)
    {
        List<(SortKeyOption Key, bool Descending)> levels = Levels.Select(l => (l.Key, l.Descending)).ToList();
        List<(T Item, ListSortRow Row)> list = rows.Select(r => (r, row(r))).ToList();
        list.Sort((a, b) =>
        {
            foreach ((SortKeyOption key, bool descending) in levels)
            {
                int c = ListSortRow.Compare(a.Row, b.Row, key.Key);
                if (c != 0)
                    return descending ? -c : c;
            }
            return StringComparer.OrdinalIgnoreCase.Compare(a.Row.Name, b.Row.Name);
        });
        return list.Select(x => x.Item);
    }
}

// What a list row is sorted by.
public sealed record ListSortRow(string Name, string Device, string Input, bool IsAxis, string Games, int Aircraft, string Groups, bool IsBound, string Modifiers)
{
    public static int Compare(ListSortRow a, ListSortRow b, string key) => key switch
    {
        "name" => Natural(a.Name, b.Name),
        // Unbound rows (no device, no input) after the bound ones.
        "device" => Empty(a.Device, b.Device) ?? Natural(a.Device, b.Device),
        "input" => Empty(a.Input, b.Input) ?? CompareInput(a.Input, b.Input),
        "kind" => a.IsAxis == b.IsAxis ? 0 : a.IsAxis ? -1 : 1,
        "games" => Empty(a.Games, b.Games) ?? Natural(a.Games, b.Games),
        "aircraft" => a.Aircraft.CompareTo(b.Aircraft),
        "group" => Empty(a.Groups, b.Groups) ?? Natural(a.Groups, b.Groups),
        "bound" => a.IsBound == b.IsBound ? 0 : a.IsBound ? -1 : 1,
        "modifiers" => Natural(a.Modifiers, b.Modifiers),
        _ => 0
    };

    private static int? Empty(string a, string b)
    {
        bool ea = string.IsNullOrWhiteSpace(a);
        bool eb = string.IsNullOrWhiteSpace(b);
        return ea == eb ? (ea ? 0 : null) : ea ? 1 : -1;
    }

    private static readonly string[] AxisOrder = ["X", "Y", "Z", "RX", "RY", "RZ", "SLIDER", "SLIDER1", "SLIDER2", "DIAL"];

    // Axes first (X, Y, Z, RX, RY, RZ, sliders), then buttons by number (BTN 2 before BTN 10), then hats, then the rest
    // (keys) in natural order.
    public static int CompareInput(string a, string b)
    {
        (int group, int rank, string rest) ra = InputRank(a);
        (int group, int rank, string rest) rb = InputRank(b);
        if (ra.group != rb.group)
            return ra.group.CompareTo(rb.group);
        if (ra.rank != rb.rank)
            return ra.rank.CompareTo(rb.rank);
        return Natural(ra.rest, rb.rest);
    }

    private static (int, int, string) InputRank(string input)
    {
        string s = (input ?? string.Empty).Trim().ToUpperInvariant();
        if (s.StartsWith("JOY_", StringComparison.Ordinal))
            s = s[4..];
        int axis = Array.IndexOf(AxisOrder, s);
        if (axis >= 0)
            return (0, axis, string.Empty);
        if (s.StartsWith("BTN_POV", StringComparison.Ordinal) || s.StartsWith("POV", StringComparison.Ordinal))
            return (2, 0, s);
        if (s.StartsWith("BTN", StringComparison.Ordinal) && int.TryParse(s[3..].Trim('_', ' '), NumberStyles.Integer, CultureInfo.InvariantCulture, out int button))
            return (1, button, string.Empty);
        return (3, 0, s);
    }

    // Text with numbers compared as numbers ("Panel 2" before "Panel 10"), ignoring case.
    public static int Natural(string? a, string? b)
    {
        string x = a ?? string.Empty;
        string y = b ?? string.Empty;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i]))
                    i++;
                while (j < y.Length && char.IsDigit(y[j]))
                    j++;
                string nx = x[si..i].TrimStart('0');
                string ny = y[sj..j].TrimStart('0');
                if (nx.Length != ny.Length)
                    return nx.Length.CompareTo(ny.Length);
                int n = string.CompareOrdinal(nx, ny);
                if (n != 0)
                    return n;
                continue;
            }
            int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (c != 0)
                return c;
            i++;
            j++;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
