using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace JoyPro.Gui.Views;

// Every window and dialog of JoyPro fits the working area of the screen it opens on, at any scaling (a 1280 x 720
// screen at 150 % leaves 853 x 448 units): its size and its minimum size are shrunk to the working area (minus the
// title bar and frame Windows adds), a window sized by its content grows at most to it. The dialogs scroll their
// content and keep their buttons in a footer below the scrolling part, so the buttons stay reachable
// (DialogFitTests renders each dialog at 1280 x 720, 150 %).
internal static class WindowFit
{
    // The title bar and the frame around the client area, in units.
    public const double DecorationWidth = 16;
    public const double DecorationHeight = 40;

    // The largest client size that fits into a working area (pixels) at a scaling.
    public static Size ClientSizeFor(PixelRect workingArea, double scaling)
    {
        double scale = scaling > 0 ? scaling : 1;
        return new Size(Math.Max(240, workingArea.Width / scale - DecorationWidth), Math.Max(160, workingArea.Height / scale - DecorationHeight));
    }

    // Shrinks the window's size, minimum size and (sized by content) maximum size to the client size that fits.
    public static void Apply(Window window, Size fit)
    {
        if (window.MinWidth > fit.Width)
            window.MinWidth = fit.Width;
        if (window.MinHeight > fit.Height)
            window.MinHeight = fit.Height;
        if (!double.IsNaN(window.Width) && window.Width > fit.Width)
            window.Width = fit.Width;
        if (!double.IsNaN(window.Height) && window.Height > fit.Height)
            window.Height = fit.Height;
        if (window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight && window.MaxHeight > fit.Height)
            window.MaxHeight = fit.Height;
        if (window.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight && window.MaxWidth > fit.Width)
            window.MaxWidth = fit.Width;
    }

    // Before a dialog shows: the screen of its owner (else its own, else the primary one).
    public static void ToScreen(Window window, Window? owner)
    {
        Screen? screen = ScreenOf(owner) ?? ScreenOf(window);
        if (screen != null)
            Apply(window, ClientSizeFor(screen.WorkingArea, screen.Scaling));
    }

    internal static Screen? ScreenOf(Window? window)
    {
        if (window?.Screens == null)
            return null;
        try
        {
            return window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        }
        catch (InvalidOperationException)
        {
            return window.Screens.Primary;
        }
    }
}
