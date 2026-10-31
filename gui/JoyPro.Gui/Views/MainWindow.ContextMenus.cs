using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using JoyPro.Logging;

namespace JoyPro.Gui.Views;

// Right-clicks are logged (2.5.0 tester report: "right-click works for the top part of an action only"): which control
// owns the menu that opens, for which item, and from which element the click came; a right-click on something without a
// menu is logged too, so a support file tells a missing menu from a menu that did not open.
public partial class MainWindow
{
    private void AttachContextMenuLog() =>
        AddHandler(ContextRequestedEvent, OnContextRequestedForLog, RoutingStrategies.Bubble, handledEventsToo: true);

    private void OnContextRequestedForLog(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Visual source)
            return;
        Control? owner = ContextMenuOwner(source);
        JoyProLog.Info("ContextMenu", owner == null
            ? "Right-click without a menu on " + Describe(source) + "."
            : "Opened on " + Describe(owner) + " (right-click on " + Describe(source) + ").");
    }

    // The control whose ContextMenu opens for a right-click on `hit`: the nearest one up the tree that has one.
    internal static Control? ContextMenuOwner(Visual? hit)
    {
        for (Visual? v = hit; v != null; v = v.GetVisualParent())
        {
            if (v is Control { ContextMenu: not null } control)
                return control;
        }
        return null;
    }

    private static string Describe(Visual visual)
    {
        string text = visual.GetType().Name;
        if (visual is Control control)
        {
            if (!string.IsNullOrEmpty(control.Name))
                text += " '" + control.Name + "'";
            if (control.DataContext != null && control.DataContext is not ViewModels.MainWindowViewModel)
                text += " (" + control.DataContext.GetType().Name + ")";
        }
        return text;
    }
}
