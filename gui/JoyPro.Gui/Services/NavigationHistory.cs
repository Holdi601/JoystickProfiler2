using System;
using System.Collections.Generic;
using System.Linq;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Services;

// Back and Forward through the places the user was (docs/ui/navigation.md), like a browser, apart from the profile's
// Undo: a place is the page, its sub-view (Layout's Device pictures / Cheat sheets, the relation editor's panel, ...),
// the open relation or device, the page's search texts, which cards were open and the scroll positions of its lists.
//   - Leaving a place pushes it onto the back stack (a page change does it by itself, a jump such as "Open both
//     relations" on a Check card does it first); a new place clears the forward stack.
//   - The same place twice in a row is kept once (the newer one: its scroll position and search).
//   - At most Capacity places are kept; the oldest goes first.
//   - While a place is being restored nothing is pushed (restoring changes the page, the relation, ...).
public sealed class NavigationHistory
{
    public const int DefaultCapacity = 50;

    private readonly LinkedList<NavigationEntry> back = new();
    private readonly LinkedList<NavigationEntry> forward = new();
    private int restoring;

    public NavigationHistory(int capacity = DefaultCapacity)
    {
        Capacity = Math.Max(1, capacity);
    }

    public int Capacity { get; }

    public bool CanGoBack => back.Count > 0;

    public bool CanGoForward => forward.Count > 0;

    public int BackCount => back.Count;

    public int ForwardCount => forward.Count;

    // The place Back would go to (null: none).
    public NavigationEntry? PeekBack => back.Last?.Value;

    public NavigationEntry? PeekForward => forward.First?.Value;

    public IReadOnlyList<NavigationEntry> BackEntries => back.ToList();

    public IReadOnlyList<NavigationEntry> ForwardEntries => forward.ToList();

    public bool IsRestoring => restoring > 0;

    // The stacks changed (the header's buttons and tooltips follow).
    public event EventHandler? Changed;

    // The place being left. Ignored while a place is restored.
    public void Push(NavigationEntry leaving)
    {
        ArgumentNullException.ThrowIfNull(leaving);
        if (IsRestoring)
            return;
        AddBack(leaving);
        forward.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // One step back from the current place: the place to restore (null: nothing to go back to). The current place goes
    // onto the forward stack. Places equal to the current one are skipped (a jump that ended where it started).
    public NavigationEntry? GoBack(NavigationEntry current)
    {
        ArgumentNullException.ThrowIfNull(current);
        NavigationEntry? target = PopSkipping(back, fromEnd: true, current);
        if (target == null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }
        forward.AddFirst(current);
        while (forward.Count > Capacity)
            forward.RemoveLast();
        Changed?.Invoke(this, EventArgs.Empty);
        return target;
    }

    public NavigationEntry? GoForward(NavigationEntry current)
    {
        ArgumentNullException.ThrowIfNull(current);
        NavigationEntry? target = PopSkipping(forward, fromEnd: false, current);
        if (target == null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }
        AddBack(current);
        Changed?.Invoke(this, EventArgs.Empty);
        return target;
    }

    // Restoring a place: no pushes until the returned scope ends (nested scopes allowed).
    public IDisposable BeginRestore()
    {
        restoring++;
        return new RestoreScope(this);
    }

    public void Clear()
    {
        back.Clear();
        forward.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void AddBack(NavigationEntry entry)
    {
        if (back.Last != null && back.Last.Value.IsSamePlace(entry))
            back.RemoveLast();
        back.AddLast(entry);
        while (back.Count > Capacity)
            back.RemoveFirst();
    }

    private static NavigationEntry? PopSkipping(LinkedList<NavigationEntry> list, bool fromEnd, NavigationEntry current)
    {
        while (list.Count > 0)
        {
            NavigationEntry entry = fromEnd ? list.Last!.Value : list.First!.Value;
            if (fromEnd)
                list.RemoveLast();
            else
                list.RemoveFirst();
            if (!entry.IsSamePlace(current))
                return entry;
        }
        return null;
    }

    private sealed class RestoreScope(NavigationHistory owner) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            owner.restoring = Math.Max(0, owner.restoring - 1);
        }
    }
}

// One place of the navigation history. The place itself is Stage + SubView + Selection; the rest is how it looked.
public sealed record NavigationEntry
{
    public UiStage Stage { get; init; }

    // The page's sub-view: "cheatsheets" / "pictures" on Layout, the relation editor's panel ("find", "fits", ...),
    // the force feedback tab; empty when the page has none.
    public string SubView { get; init; } = string.Empty;

    // The open relation (Relations), the selected device (Layout); empty when none.
    public string Selection { get; init; } = string.Empty;

    // The selection in words for the header's tooltip ("Pitch", "HOTAS Warthog Throttle"); not part of the place.
    public string Label { get; init; } = string.Empty;

    // Search texts and filters of the page by name ("relations", "binds", "check", "check-kind", "settings").
    public IReadOnlyDictionary<string, string> Texts { get; init; } = new Dictionary<string, string>();

    // Cards and sections that are open (true) or closed (false) by key ("check-card:...", "settings:games").
    public IReadOnlyDictionary<string, bool> Expanded { get; init; } = new Dictionary<string, bool>();

    // The scroll offsets of the page's lists by a key of the view (MainWindow): x, y.
    public IReadOnlyDictionary<string, (double X, double Y)> Scroll { get; init; } = new Dictionary<string, (double X, double Y)>();

    public bool IsSamePlace(NavigationEntry other) =>
        other.Stage == Stage &&
        string.Equals(other.SubView, SubView, StringComparison.Ordinal) &&
        string.Equals(other.Selection, Selection, StringComparison.Ordinal);
}

// The window's side of the history: the scroll positions of the page's lists (the view model has no view in tests).
public interface INavigationScrollHost
{
    IReadOnlyDictionary<string, (double X, double Y)> CaptureScroll();

    // After the restored page has been laid out (the window posts it at Loaded priority).
    void RestoreScroll(IReadOnlyDictionary<string, (double X, double Y)> offsets);
}
