using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views;

// Back and Forward (docs/ui/navigation.md): Alt+Left / Alt+Right (a text box keeps its keys), the mouse's back and
// forward buttons (XButton1 / XButton2) anywhere in the window, and the scroll positions of the page's lists for the
// view model's navigation history (INavigationScrollHost). A list is known by its place in the page's tree (the child
// indexes from the root grid down to it), so the same list of the same page is found again after a page change.
public partial class MainWindow : INavigationScrollHost
{
    private EventHandler? pendingScrollRestore;

    private void AttachNavigationHistory()
    {
        AddHandler(KeyDownEvent, NavigationKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, NavigationPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        // The user scrolls himself: a restore that waits for its list to fill up stops.
        AddHandler(PointerWheelChangedEvent, (_, _) => CancelPendingScrollRestore(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void NavigationKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not MainWindowViewModel vm || e.KeyModifiers != KeyModifiers.Alt || e.Source is TextBox || TextBoxHasFocus())
            return;
        if (e.Key == Key.Left)
        {
            e.Handled = true;
            vm.NavigateBackCommand.Execute(null);
        }
        else if (e.Key == Key.Right)
        {
            e.Handled = true;
            vm.NavigateForwardCommand.Execute(null);
        }
    }

    private void NavigationPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        // Any other click (a scroll bar dragged, a list clicked) ends a restore that is still waiting.
        CancelPendingScrollRestore();
        if (kind == PointerUpdateKind.XButton1Pressed)
        {
            e.Handled = true;
            vm.NavigateBackCommand.Execute(null);
        }
        else if (kind == PointerUpdateKind.XButton2Pressed)
        {
            e.Handled = true;
            vm.NavigateForwardCommand.Execute(null);
        }
    }

    public IReadOnlyDictionary<string, (double X, double Y)> CaptureScroll()
    {
        Dictionary<string, (double X, double Y)> offsets = new(StringComparer.Ordinal);
        foreach ((string key, ScrollViewer viewer) in NavigationScrollViewers())
        {
            bool scrollable = viewer.Extent.Height > viewer.Viewport.Height + 1 || viewer.Extent.Width > viewer.Viewport.Width + 1;
            if (scrollable || viewer.Offset != default)
                offsets[key] = (viewer.Offset.X, viewer.Offset.Y);
        }
        return offsets;
    }

    public void RestoreScroll(IReadOnlyDictionary<string, (double X, double Y)> offsets)
    {
        CancelPendingScrollRestore();
        if (offsets.Count == 0)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (TryApplyScroll(offsets))
                return;
            // The page's list is still filling (a refresh of the page): again after each layout pass, for a while.
            Stopwatch since = Stopwatch.StartNew();
            int passes = 0;
            EventHandler? retry = null;
            retry = (_, _) =>
            {
                if (!ReferenceEquals(pendingScrollRestore, retry))
                    return;
                if (TryApplyScroll(offsets) || ++passes > 40 || since.Elapsed > TimeSpan.FromSeconds(3))
                    CancelPendingScrollRestore();
            };
            pendingScrollRestore = retry;
            RootLayoutGrid.LayoutUpdated += retry;
        }, DispatcherPriority.Loaded);
    }

    private void CancelPendingScrollRestore()
    {
        if (pendingScrollRestore == null)
            return;
        RootLayoutGrid.LayoutUpdated -= pendingScrollRestore;
        pendingScrollRestore = null;
    }

    // True when every list is where it was (or as far as it goes once its content is all there).
    private bool TryApplyScroll(IReadOnlyDictionary<string, (double X, double Y)> offsets)
    {
        int found = 0;
        bool reached = true;
        foreach ((string key, ScrollViewer viewer) in NavigationScrollViewers())
        {
            if (!offsets.TryGetValue(key, out (double X, double Y) want))
                continue;
            found++;
            double maxX = Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width);
            double maxY = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
            Vector target = new(Math.Min(want.X, maxX), Math.Min(want.Y, maxY));
            if (Math.Abs(viewer.Offset.X - target.X) > 0.5 || Math.Abs(viewer.Offset.Y - target.Y) > 0.5)
                viewer.Offset = target;
            if (want.X > maxX + 0.5 || want.Y > maxY + 0.5)
                reached = false;
        }
        return reached && found > 0;
    }

    // The visible scroll viewers of the page (not those of text boxes, combo boxes or list rows) with their keys.
    private IEnumerable<(string Key, ScrollViewer Viewer)> NavigationScrollViewers()
    {
        List<(string, ScrollViewer)> found = [];
        Walk(RootLayoutGrid, string.Empty);
        return found;

        void Walk(Visual parent, string path)
        {
            int index = 0;
            foreach (Visual child in parent.GetVisualChildren())
            {
                string childPath = path + "/" + index;
                index++;
                if (child is Control { IsVisible: false })
                    continue;
                if (child is TextBox or ComboBox or ListBoxItem or DataGridRow or Avalonia.Controls.Primitives.Popup)
                    continue;
                if (child is ScrollViewer viewer)
                    found.Add((childPath, viewer));
                Walk(child, childPath);
            }
        }
    }
}
