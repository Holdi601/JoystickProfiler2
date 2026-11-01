using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace JoyPro.Gui.Tests;

// Offscreen renders: a virtualizing list does not learn its viewport without a shown window and builds only its first
// item. The ListBoxes get a plain panel in each render helper; this does the same for the other item lists with a
// virtualizing panel (the Relations page's Add actions list since 2.5.1).
internal static class RenderPanels
{
    public static void UnvirtualizeItemsControls(ILogical root)
    {
        foreach (ItemsControl items in root.GetLogicalDescendants().OfType<ItemsControl>().Where(i => i is not ListBox && i.ItemsPanel?.Build() is VirtualizingPanel))
            items.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel());
    }
}
