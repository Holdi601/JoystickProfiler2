using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views;

// The bottom bar in the user's order with his hidden pages left out (Settings > Pages, MainWindowViewModel.NavPages.cs),
// and the drag of a row in that list by its handle.
public partial class MainWindow
{
    private NavPageItem? navDragPage;
    private string? partDragId;
    private Point partDragStart;

    // A chip of an unplaced input (marker assistant): dragged onto the picture, its part goes where it is dropped.
    private void PartChipPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        partDragId = ((sender as Control)?.DataContext as Models.ReferenceMarker)?.Id;
        partDragStart = e.GetPosition(this);
    }

    private async void PartChipPointerMoved(object? sender, PointerEventArgs e)
    {
        if (partDragId == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - partDragStart.X) + Math.Abs(now.Y - partDragStart.Y) < 6)
            return;
        string id = partDragId;
        partDragId = null;
#pragma warning disable CS0618 // DataObject / DoDragDrop: the Avalonia 11 drag data API
        DataObject data = new();
        data.Set(DataFormats.Text, Controls.DeviceMarkerCanvas.PartDragFormat + ":" + id);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
    }

    private void OnNavBarChanged(object? sender, EventArgs e) => ApplyNavBar();

    // Orders the bar's page buttons (Nav_<id>, Output = EngineOutputQuickToggle) like NavPages and hides the hidden ones.
    // Other children (the hint) stay behind them.
    public void ApplyNavBar()
    {
        if (observedViewModel == null || NavBar == null)
            return;
        Dictionary<string, Control> buttons = new(StringComparer.OrdinalIgnoreCase);
        foreach (Control child in NavBar.Children)
        {
            string? id = child == EngineOutputQuickToggle ? "output" : child.Name is { } name && name.StartsWith("Nav_", StringComparison.Ordinal) ? name[4..] : null;
            if (id != null)
                buttons[id] = child;
        }
        int index = 0;
        foreach (NavPageItem page in observedViewModel.NavPages)
        {
            if (!buttons.TryGetValue(page.Id, out Control? button))
                continue;
            button.IsVisible = page.IsShownInBar;
            int current = NavBar.Children.IndexOf(button);
            if (current != index)
                NavBar.Children.Move(current, index);
            index++;
        }
    }

    // The ids of the bar's shown page buttons, left to right (tests).
    public IReadOnlyList<string> NavBarOrder() =>
        NavBar.Children.Where(c => c.IsVisible && (c == EngineOutputQuickToggle || c.Name?.StartsWith("Nav_", StringComparison.Ordinal) == true))
            .Select(c => c == EngineOutputQuickToggle ? "output" : c.Name![4..]).ToList();

    private void AttachNavPagesDrag()
    {
        NavPagesList.AddHandler(PointerPressedEvent, NavPagesPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        NavPagesList.AddHandler(PointerMovedEvent, NavPagesPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        NavPagesList.AddHandler(PointerReleasedEvent, NavPagesPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        NavPagesList.AddHandler(PointerCaptureLostEvent, (_, _) => navDragPage = null, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void NavPagesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || !e.GetCurrentPoint(NavPagesList).Properties.IsLeftButtonPressed)
            return;
        Border? grip = source.FindAncestorOfType<Border>(includeSelf: true);
        while (grip != null && !grip.Classes.Contains("navPageGrip"))
            grip = grip.FindAncestorOfType<Border>();
        if (grip?.DataContext is not NavPageItem page)
            return;
        navDragPage = page;
        e.Pointer.Capture(NavPagesList);
        e.Handled = true;
    }

    private void NavPagesPointerMoved(object? sender, PointerEventArgs e)
    {
        if (navDragPage == null || observedViewModel == null)
            return;
        int target = NavPageIndexAt(e.GetPosition(NavPagesList).Y);
        if (target >= 0)
            observedViewModel.MoveNavPage(navDragPage, target);
    }

    private void NavPagesPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (navDragPage == null)
            return;
        navDragPage = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // The row under a height of the list (the first or last row above or below it).
    public int NavPageIndexAt(double y)
    {
        List<Control> rows = NavPagesList.GetRealizedContainers().ToList();
        if (rows.Count == 0)
            return -1;
        for (int i = 0; i < rows.Count; i++)
        {
            Rect bounds = rows[i].Bounds;
            if (y < bounds.Bottom)
                return NavPagesList.IndexFromContainer(rows[i]);
        }
        return NavPagesList.IndexFromContainer(rows[^1]);
    }
}
