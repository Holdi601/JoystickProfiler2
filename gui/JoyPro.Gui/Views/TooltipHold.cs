using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace JoyPro.Gui.Views;

// Holds a window's tooltips back after the content moved under a mouse that did not move (follow input selected and
// scrolled to another relation): the card that slid under the resting pointer must not pop its tooltip. Tooltips come
// back as soon as the mouse really moves (more than MoveThreshold pixels), is pressed or leaves the window.
internal sealed class TooltipHold
{
    public const double MoveThreshold = 3;

    private readonly Control root;
    private Point? lastPointer;
    private Point? anchor;

    public TooltipHold(Control root)
    {
        this.root = root;
        root.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerPressedEvent, (_, _) => Release(), RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerWheelChangedEvent, (_, _) => Release(), RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerExitedEvent, (_, e) =>
        {
            if (ReferenceEquals(e.Source, root))
                Release();
        }, RoutingStrategies.Direct, handledEventsToo: true);
    }

    public bool IsHolding { get; private set; }

    // The content moved under the mouse: no tooltip until the mouse moves; an open one closes.
    public void Hold()
    {
        IsHolding = true;
        anchor = lastPointer;
        ToolTip.SetServiceEnabled(root, false);
        CloseOpenTooltips();
    }

    public void Release()
    {
        if (!IsHolding)
            return;
        IsHolding = false;
        anchor = null;
        root.ClearValue(ToolTip.ServiceEnabledProperty);
    }

    // The pointer's position in the window (tests and the handler).
    internal void PointerAt(Point position)
    {
        lastPointer = position;
        if (!IsHolding)
            return;
        if (anchor is not { } start)
        {
            // Held before the mouse was ever seen: the first position is where it rests.
            anchor = position;
            return;
        }
        Vector moved = position - start;
        if (Math.Abs(moved.X) > MoveThreshold || Math.Abs(moved.Y) > MoveThreshold)
            Release();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e) => PointerAt(e.GetPosition(root));

    private void CloseOpenTooltips()
    {
        foreach (Control control in root.GetVisualDescendants().OfType<Control>().Where(ToolTip.GetIsOpen).ToList())
            ToolTip.SetIsOpen(control, false);
    }
}
