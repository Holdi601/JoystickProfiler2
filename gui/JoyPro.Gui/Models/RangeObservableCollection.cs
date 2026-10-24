using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace JoyPro.Gui.Models;

// An ObservableCollection that can take all of its items at once with one Reset notification. Filling a list of a few
// hundred rows item by item raised a change per row: every handler (the relation editor re-marked the search rows for
// each action) and the list control ran once per row (a relation of 200+ actions made the editor hang).
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public RangeObservableCollection()
    {
    }

    public RangeObservableCollection(IEnumerable<T> items) : base(items)
    {
    }

    // The items become exactly these (one Reset), unless they are these already.
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        List<T> next = [.. items];
        if (next.Count == Items.Count)
        {
            bool same = true;
            for (int i = 0; i < next.Count && same; i++)
                same = EqualityComparer<T>.Default.Equals(next[i], Items[i]);
            if (same)
                return;
        }
        Items.Clear();
        foreach (T item in next)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
