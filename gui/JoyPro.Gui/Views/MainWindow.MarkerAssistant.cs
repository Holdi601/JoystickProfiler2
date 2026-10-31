using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views;

// The marker assistant's panel on the Layout page is dragged by its grip to any place over the picture (2.5.0 tester
// report: it covered the middle of the picture); the place is kept when the panel is let go.
public partial class MainWindow
{
    private bool markerAssistantDragging;
    private Point markerAssistantDragStart;
    private double markerAssistantStartX, markerAssistantStartY;

    private void MarkerAssistantGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || sender is not Control grip || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
            return;
        Control? host = MarkerAssistantPanel?.Parent as Control;
        if (host == null)
            return;
        markerAssistantDragging = true;
        markerAssistantDragStart = e.GetPosition(host);
        markerAssistantStartX = vm.MarkerAssistantOffsetX;
        markerAssistantStartY = vm.MarkerAssistantOffsetY;
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void MarkerAssistantGripMoved(object? sender, PointerEventArgs e)
    {
        if (!markerAssistantDragging || DataContext is not MainWindowViewModel vm || MarkerAssistantPanel?.Parent is not Control host)
            return;
        Point now = e.GetPosition(host);
        (double x, double y) = ClampMarkerAssistant(markerAssistantStartX + now.X - markerAssistantDragStart.X, markerAssistantStartY + now.Y - markerAssistantDragStart.Y,
            host.Bounds.Size, MarkerAssistantPanel.Bounds.Size);
        vm.SetMarkerAssistantOffset(x, y, keep: false);
        e.Handled = true;
    }

    private void MarkerAssistantGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!markerAssistantDragging)
            return;
        markerAssistantDragging = false;
        e.Pointer.Capture(null);
        if (DataContext is MainWindowViewModel vm)
            vm.SetMarkerAssistantOffset(vm.MarkerAssistantOffsetX, vm.MarkerAssistantOffsetY, keep: true);
        e.Handled = true;
    }

    // A kept place from a larger window: the panel is pulled back into the picture area (not saved, the window may grow).
    private void AttachMarkerAssistantPlace()
    {
        if (MarkerAssistantPanel == null)
            return;
        MarkerAssistantPanel.PropertyChanged += (_, e) =>
        {
            if ((e.Property == IsVisibleProperty || e.Property == BoundsProperty) && !markerAssistantDragging && MarkerAssistantPanel.IsVisible &&
                MarkerAssistantPanel.Parent is Control host && host.Bounds.Width > 0 && DataContext is MainWindowViewModel vm)
            {
                (double x, double y) = ClampMarkerAssistant(vm.MarkerAssistantOffsetX, vm.MarkerAssistantOffsetY, host.Bounds.Size, MarkerAssistantPanel.Bounds.Size);
                if (Math.Abs(x - vm.MarkerAssistantOffsetX) > 0.5 || Math.Abs(y - vm.MarkerAssistantOffsetY) > 0.5)
                    vm.SetMarkerAssistantOffset(x, y, keep: false);
            }
        };
    }

    // The panel stays inside the picture area (its 12 px margin included), whatever was dragged or saved.
    internal static (double X, double Y) ClampMarkerAssistant(double x, double y, Size host, Size panel)
    {
        const double margin = 12;
        double maxX = Math.Max(0, host.Width - panel.Width - 2 * margin);
        double maxY = Math.Max(0, host.Height - panel.Height - 2 * margin);
        return (Math.Clamp(x, 0, maxX), Math.Clamp(y, 0, maxY));
    }
}
