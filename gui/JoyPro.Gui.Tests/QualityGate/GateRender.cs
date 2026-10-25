using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;

namespace JoyPro.Gui.Tests.QualityGate;

// The main window's page bound to a view model, laid out in a root that is never shown (HiddenWindow), with Avalonia's
// own layout passes: virtualizing lists realize what is in view. The MainWindow object itself lets go of the view model
// at once: its code-behind opens dialogs (game setup, confirmations, device replace) when the view model asks, and a
// check must never put a window on the desktop.
internal static class GateRender
{
    public static HiddenWindow NewWindow(MainWindowViewModel vm, double width, double height)
    {
        HiddenWindow view = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(width, height));
        view.Window.DataContext = null;
        view.Settle();
        return view;
    }

    public static Control Root(HiddenWindow view) => view.Page;

    // Where the control is drawn in the window (null when it is not in the window's tree any more).
    public static Rect? BoundsInWindow(Visual control, Visual root)
    {
        Matrix? m = control.TransformToVisual(root);
        return m == null ? null : new Rect(control.Bounds.Size).TransformToAABB(m.Value);
    }

    // Seen now: visible, not faded out, inside the window and inside every scroll viewport around it, not in a popup.
    public static bool IsSeen(Control control, Control root, double width, double height) => SeenRect(control, root, width, height) != null;

    // The part of the control a person sees: its bounds cut to the window and to every scroll viewport around it.
    public static Rect? SeenRect(Control control, Control root, double width, double height)
    {
        if (!control.IsEffectivelyVisible || control.Bounds.Width < 1 || control.Bounds.Height < 1)
            return null;
        double opacity = 1;
        for (Visual? v = control; v != null && v != root; v = v.GetVisualParent())
        {
            opacity *= v.Opacity;
            if (v is OverlayLayer or AdornerLayer or PopupRoot or Popup)
                return null;
        }
        if (opacity < 0.1)
            return null;
        Rect? bounds = BoundsInWindow(control, root);
        if (bounds == null)
            return null;
        Rect r = bounds.Value.Intersect(new Rect(0, 0, width, height));
        for (Visual? v = control.GetVisualParent(); v != null && v != root && r.Width > 0 && r.Height > 0; v = v.GetVisualParent())
        {
            // A scroll viewport, or a parent that clips what it holds (the pages' area above the bottom bar).
            if (v is ScrollContentPresenter || v is Control { ClipToBounds: true })
            {
                Rect? c = BoundsInWindow(v, root);
                r = c == null ? default : r.Intersect(c.Value);
            }
        }
        return r.Width >= 1 && r.Height >= 1 ? r : null;
    }

    // Name, kind and caption of a control for a report line: "Button.icon 'Delete' (#DeleteRelationButton)".
    public static string Describe(Control control)
    {
        string classes = control.Classes.Where(c => !c.StartsWith(':')).ToList() is { Count: > 0 } list ? "." + string.Join(".", list) : string.Empty;
        string caption = control switch
        {
            ContentControl { Content: string s } => s,
            TextBox t => (t.Text is { Length: > 0 } text ? text : t.Watermark ?? string.Empty),
            ComboBox c => c.SelectedItem?.ToString() ?? c.PlaceholderText ?? string.Empty,
            _ => string.Empty
        };
        if (caption.Length == 0)
            caption = control.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? string.Empty;
        if (caption.Length == 0 && ToolTip.GetTip(control) is string tip)
            caption = tip;
        string name = string.IsNullOrEmpty(control.Name) ? string.Empty : " #" + control.Name;
        return control.GetType().Name + classes + (caption.Length > 0 ? " '" + Gate.Short(caption, 40) + "'" : string.Empty) + name;
    }

    // The page as a picture at the scale (96 dpi x scale); boxes: rectangles to outline in red.
    public static void Save(HiddenWindow view, double width, double height, double scale, string file, IEnumerable<Rect>? boxes = null)
    {
        Size size = new(width, height);
        Control page = Root(view);
        IBrush background = page.TryFindResource("JP.PageBackgroundBrush", page.ActualThemeVariant, out object? found) && found is IBrush own
            ? own
            : new SolidColorBrush(page.ActualThemeVariant == ThemeVariant.Light ? Color.Parse("#F3F3F3") : Color.Parse("#202020"));
        // At 1:1 whatever the scale: the boxes are in layout units and must sit on the controls.
        PixelSize pixels = new((int)width, (int)height);
        using RenderTargetBitmap content = HiddenWindow.RenderOnBackground(page, pixels, background, view.Settle);
        using RenderTargetBitmap bitmap = new(pixels);
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(background, new Rect(size));
            context.DrawImage(content, new Rect(size));
            Pen pen = new(Brushes.Red, 2);
            foreach (Rect box in boxes ?? [])
                context.DrawRectangle(null, pen, box.Inflate(1));
        }
        bitmap.Save(file);
    }
}
