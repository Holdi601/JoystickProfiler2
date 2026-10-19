using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JoyPro.Logging;

namespace JoyPro.Gui.Views;

// Keeps a window's tooltips back after follow input moved the content under the mouse (a device button or axis
// selected and scrolled to another relation): the card that slid under the resting pointer must not pop its tooltip.
// Every follow-input event arms the hold again (Hold) and closes an open tooltip. Only a real, noticeable mouse
// movement releases it: the pointer's path on the SCREEN since the hold must be at least ReleasePath long and it must
// have got at least ReleaseDistance away from where it was. Pointer events the layout raises under a still mouse
// (same screen position) and jitter within JitterRadius do not count; clicks, the wheel and keyboard focus do not either. After the
// release the normal tooltip delay applies (the pointer must rest on the item).
//
// An inactive window (another program in front, e.g. the game) never shows a tooltip, and coming back to the window
// arms the hold again, so a mouse resting where it was does not pop one either. A key pressed in the window (typing,
// keyboard navigation, a key bind that makes follow input jump) arms it too.
//
// Avalonia opens the next tooltip at once (no show delay) when another one was open or closed less than its
// BetweenShowDelay ago; that made tooltips pop instantly along a list after a jump. Every control with a tooltip gets
// BetweenShowDelay -1 here: each tooltip waits for its show delay with the mouse resting on the control.
internal sealed class TooltipHold
{
    public const double ReleasePath = 60;
    public const double ReleaseDistance = 16;
    // Movement within this radius (device-independent pixels) is jitter or the same position (synthetic events).
    public const double JitterRadius = 4;

    private static readonly ConditionalWeakTable<TopLevel, TooltipHold> Holds = new();
    private static bool tipGuardInstalled;

    private readonly Control root;
    private Point? lastScreen;
    private Point? anchor;
    private Point? stepFrom;
    private double path;
    private double farthest;
    private bool inactive;
    // Diagnostics (log): when the hold was armed and released last.
    private DateTime heldAtUtc = DateTime.MinValue;
    private DateTime releasedAtUtc = DateTime.MinValue;

    public TooltipHold(Control root)
    {
        this.root = root;
        root.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.KeyDownEvent, (_, _) => Hold("key"), RoutingStrategies.Tunnel, handledEventsToo: true);
        if (root is TopLevel topLevel)
            Holds.AddOrUpdate(topLevel, this);
        if (root is Window window)
        {
            window.Activated += (_, _) => SetActive(true);
            window.Deactivated += (_, _) => SetActive(false);
        }
        InstallTipGuard();
        // Tooltips set before the guard was there (the window's own XAML).
        foreach (Control control in root.GetLogicalDescendants().OfType<Control>().Prepend(root))
        {
            if (ToolTip.GetTip(control) != null && !control.IsSet(ToolTip.BetweenShowDelayProperty))
                ToolTip.SetBetweenShowDelay(control, -1);
        }
    }

    // Follow input moved the content: no tooltip until the mouse really moves.
    public bool IsHolding { get; private set; }

    public bool IsSuppressed => IsHolding || inactive;

    // Arms the hold (again): the movement counts from here; an open tooltip closes.
    public void Hold(string reason = "follow input")
    {
        if (!IsHolding && JoyProLog.ShouldLog("tooltip-hold-" + reason, TimeSpan.FromSeconds(2)))
            JoyProLog.Info("Tooltip", "Hold armed (" + reason + ") at " + Describe(lastScreen) + ".");
        heldAtUtc = DateTime.UtcNow;
        IsHolding = true;
        anchor = lastScreen;
        stepFrom = lastScreen;
        path = 0;
        farthest = 0;
        Apply();
        CloseOpenTooltips();
    }

    internal void SetActive(bool active)
    {
        if (inactive == !active)
            return;
        inactive = !active;
        if (active)
        {
            // Back in front: the mouse may rest on an item; it has to move first.
            Hold("window active");
            return;
        }
        Apply();
        CloseOpenTooltips();
    }

    // The pointer's screen position in device-independent pixels (tests and the handler).
    internal void PointerAtScreen(Point screen)
    {
        lastScreen = screen;
        if (!IsHolding)
            return;
        if (anchor is not { } start)
        {
            // Held before the mouse was ever seen: the first position is where it rests.
            anchor = screen;
            stepFrom = screen;
            return;
        }
        // The path counts in steps of at least JitterRadius: a resting hand's jitter (and the same place again)
        // never adds up, however long it goes on.
        Point from = stepFrom ?? start;
        double step = Length(screen - from);
        if (step >= JitterRadius)
        {
            path += step;
            stepFrom = screen;
        }
        farthest = Math.Max(farthest, Length(screen - start));
        if (path >= ReleasePath && farthest >= ReleaseDistance)
            Release();
    }

    private void Release()
    {
        JoyProLog.Info("Tooltip", "Hold released after " + Math.Round(path) + " px path, " + Math.Round(farthest) + " px away, at " + Describe(lastScreen) + ".");
        releasedAtUtc = DateTime.UtcNow;
        IsHolding = false;
        anchor = null;
        Apply();
    }

    private void Apply()
    {
        if (IsSuppressed)
            ToolTip.SetServiceEnabled(root, false);
        else
            root.ClearValue(ToolTip.ServiceEnabledProperty);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        Point local = e.GetPosition(root);
        TopLevel? top = TopLevel.GetTopLevel(root);
        if (top == null)
            return;
        PixelPoint pixels = root.PointToScreen(local);
        double scale = top.RenderScaling > 0 ? top.RenderScaling : 1;
        PointerAtScreen(new Point(pixels.X / scale, pixels.Y / scale));
    }

    private void CloseOpenTooltips()
    {
        foreach (Control control in root.GetVisualDescendants().OfType<Control>().Where(ToolTip.GetIsOpen).ToList())
            ToolTip.SetIsOpen(control, false);
    }

    private static string Describe(Point? point) =>
        point is { } p ? Math.Round(p.X) + "," + Math.Round(p.Y) : "(mouse not seen yet)";

    // One log line per tooltip that opens: which control, what it says and the hold's state (finds the tooltip that
    // still pops up after a jump).
    private static void LogOpened(Control control)
    {
        try
        {
            TopLevel? top = TopLevel.GetTopLevel(control) ?? control.FindLogicalAncestorOfType<TopLevel>();
            TooltipHold? hold = null;
            if (top != null)
                Holds.TryGetValue(top, out hold);
            object? tip = ToolTip.GetTip(control);
            string text = tip switch
            {
                string s => s,
                Control c => string.Join(" | ", c.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Take(4)),
                null => "(none)",
                _ => tip.ToString() ?? string.Empty
            };
            if (text.Length > 120)
                text = text[..120] + "...";
            string state = hold == null
                ? "window " + (top?.GetType().Name ?? "none") + " has no hold"
                : "holding " + hold.IsHolding + ", inactive " + hold.inactive + ", armed " + Ago(hold.heldAtUtc) + ", released " + Ago(hold.releasedAtUtc) +
                  ", mouse " + Describe(hold.lastScreen);
            JoyProLog.Info("Tooltip", "Opened on " + control.GetType().Name + (string.IsNullOrEmpty(control.Name) ? string.Empty : " '" + control.Name + "'") +
                                      " (" + (control.DataContext?.GetType().Name ?? "no data") + "): \"" + text.Replace('\n', ' ') + "\"; " + state + ".");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            JoyProLog.Debug("Tooltip", "Could not describe an opened tooltip: " + ex.Message);
        }
    }

    private static string Ago(DateTime utc) =>
        utc == DateTime.MinValue ? "never" : Math.Round((DateTime.UtcNow - utc).TotalMilliseconds) + " ms ago";

    private static double Length(Vector v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);

    // A show timer that started before the hold (or a tooltip opened in code) would still open it: such a tooltip
    // closes again at once while its window holds tooltips back.
    private static void InstallTipGuard()
    {
        if (tipGuardInstalled)
            return;
        tipGuardInstalled = true;
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue != null && !control.IsSet(ToolTip.BetweenShowDelayProperty))
                ToolTip.SetBetweenShowDelay(control, -1);
        });
        ToolTip.IsOpenProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is not true)
                return;
            LogOpened(control);
            if (!KeepsBack(control))
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (KeepsBack(control) && ToolTip.GetIsOpen(control))
                    ToolTip.SetIsOpen(control, false);
            }, DispatcherPriority.Send);
        });
    }

    // The control's window holds tooltips back right now (the guard; tests).
    internal static bool KeepsBack(Control control) =>
        (TopLevel.GetTopLevel(control) ?? control.FindLogicalAncestorOfType<TopLevel>()) is { } top &&
        Holds.TryGetValue(top, out TooltipHold? hold) && hold.IsSuppressed;
}
