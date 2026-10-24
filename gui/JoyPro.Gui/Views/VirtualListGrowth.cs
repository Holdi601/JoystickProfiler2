using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace JoyPro.Gui.Views;

// A row of a virtualizing list that grows or shrinks in place (a relation card gets a new line of game chips after an
// add) can leave Avalonia's VirtualizingStackPanel with a wrong guess of where the viewport is: it realizes other rows
// for the same scroll offset and still draws the grown row at its old place, over its neighbours. Bringing the row
// into view again after the change re-anchors the panel, and a parked selected row is never drawn
// (RelationCardGrowthTests):
// - the selected row: after any change of its item (the open relation's card after an add, its bind);
// - after any other row changed its height for the same item (not a container that only got another item while
//   scrolling): the selected row when it is on screen, else that row.
internal static class VirtualListGrowth
{
    private sealed class Seen
    {
        public object? Item;
    }

    private sealed class State
    {
        public INotifyPropertyChanged? Selected;
        public PropertyChangedEventHandler? Handler;
        public bool Pending;
    }

    private static readonly ConditionalWeakTable<Control, Seen> Items = new();

    // followSelected: any change of the selected item brings its row into view (the relation list; not the action list,
    // whose selected action changes when its pictures and aircraft arrive while the user may scroll elsewhere).
    public static void Attach(ListBox list, bool followSelected = true)
    {
        State state = new();
        list.ContainerPrepared += (_, e) =>
        {
            e.Container.SizeChanged -= OnSizeChanged;
            e.Container.SizeChanged += OnSizeChanged;
        };
        list.ContainerClearing += (_, e) => e.Container.SizeChanged -= OnSizeChanged;
        list.LayoutUpdated += (_, _) => HideParkedRow(list);
        if (!followSelected)
            return;
        list.SelectionChanged += (_, _) => Follow(list, state);
        list.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty || e.Property == ItemsControl.ItemsSourceProperty)
                Follow(list, state);
        };
    }

    // Watches the selected item: a change of it can change its row's height.
    private static void Follow(ListBox list, State state)
    {
        INotifyPropertyChanged? selected = list.SelectedItem as INotifyPropertyChanged;
        if (ReferenceEquals(selected, state.Selected))
            return;
        if (state.Selected != null && state.Handler != null)
            state.Selected.PropertyChanged -= state.Handler;
        state.Selected = selected;
        state.Handler = null;
        if (selected == null)
            return;
        state.Handler = (_, _) =>
        {
            if (state.Pending)
                return;
            state.Pending = true;
            Dispatcher.UIThread.Post(() =>
            {
                state.Pending = false;
                BringIntoView(list, selected);
            }, DispatcherPriority.Background);
        };
        selected.PropertyChanged += state.Handler;
    }

    private static void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Control container || FindListBox(container) is not { } list)
            return;
        Seen seen = Items.GetOrCreateValue(container);
        object? item = container.DataContext;
        bool sameItem = ReferenceEquals(seen.Item, item);
        seen.Item = item;
        if (!sameItem || item == null || e.PreviousSize.Height <= 0 || Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 0.5)
            return;
        // The selected row stays where the user works when it is on screen (a stale selected row was the one drawn over
        // its neighbours); otherwise the row that changed.
        object? selected = list.SelectedItem;
        bool selectedShown = selected != null && list.ContainerFromItem(selected) is { IsVisible: true };
        object target = selectedShown ? selected! : item;
        Dispatcher.UIThread.Post(() => BringIntoView(list, target), DispatcherPriority.Background);
    }

    // VirtualizingStackPanel keeps the selected row's container alive when it leaves the realized rows (it is the
    // list's keyboard anchor) and places it at an estimated position: with rows of different heights that estimate can
    // lie on rows that are on screen, and the parked card was drawn over them. A parked container is never really on
    // screen (it would be realized then): it is made invisible until it is realized again.
    private static readonly ConditionalWeakTable<Control, object> Parked = new();

    private static void HideParkedRow(ListBox list)
    {
        if (list.ItemsPanelRoot is not VirtualizingStackPanel panel)
            return;
        System.Collections.Generic.HashSet<Control> realized = [.. list.GetRealizedContainers()];
        foreach (Control child in panel.Children)
        {
            bool parked = child.IsVisible && !realized.Contains(child);
            bool hidden = Parked.TryGetValue(child, out _);
            if (parked && !hidden)
            {
                Parked.AddOrUpdate(child, new object());
                child.Opacity = 0;
                child.IsHitTestVisible = false;
            }
            else if (!parked && hidden)
            {
                Parked.Remove(child);
                child.Opacity = 1;
                child.IsHitTestVisible = true;
            }
        }
    }

    private static void BringIntoView(ListBox list, object item)
    {
        if (list.ItemsPanelRoot is not VirtualizingStackPanel || !list.IsEffectivelyVisible)
            return;
        int index = list.ItemsView.IndexOf(item);
        if (index >= 0)
            list.ScrollIntoView(index);
    }

    private static ListBox? FindListBox(Visual visual)
    {
        for (Visual? parent = Avalonia.VisualTree.VisualExtensions.GetVisualParent(visual); parent != null; parent = Avalonia.VisualTree.VisualExtensions.GetVisualParent(parent))
        {
            if (parent is ListBox list)
                return list;
        }
        return null;
    }
}
