using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.VisualTree;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// 2.5.0 tester report: the context menu of a relation's action ("Move to another button...") opened only on the upper
// title line of the card; the aircraft line below it and the card's edges had none. Right-clicks at several points of
// every card (title, game line, aircraft line, the empty right part, the padding at the edges) must reach the card's
// menu, on the relation editor's action list and on the Binds page.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class ContextMenuRowTests
{
    // Points of a card (fractions of its size): corners inside the padding, the title, the middle, the bottom line,
    // the right part (left of the list's scroll bar, which lies over the cards' right edge).
    private static readonly (double X, double Y)[] Points =
    [
        (0.01, 0.06), (0.25, 0.2), (0.5, 0.5), (0.3, 0.92), (0.12, 0.97), (0.93, 0.5), (0.7, 0.95)
    ];

    private static void AssertEveryPointOpensTheRowMenu(HiddenWindow main, string rowName, int minRows)
    {
        main.Settle();
        // The cards wholly inside their list's visible part (a card scrolled half out is cut off by the list).
        List<Border> rows = main.Page.GetVisualDescendants().OfType<Border>().Where(b => b.Name == rowName && b.IsEffectivelyVisible && b.Bounds.Height > 0 && InView(b)).ToList();
        Assert.True(rows.Count >= minRows, rowName + ": " + rows.Count + " rows realized");
        List<string> misses = [];
        foreach (Border row in rows)
        {
            Assert.NotNull(row.ContextMenu);
            foreach ((double fx, double fy) in Points)
            {
                Point? inPage = row.TranslatePoint(new Point(row.Bounds.Width * fx, row.Bounds.Height * fy), main.Page);
                Assert.NotNull(inPage);
                Visual? hit = HitAt(main.Page, inPage!.Value);
                Control? owner = MainWindow.ContextMenuOwner(hit);
                if (!ReferenceEquals(owner, row))
                    misses.Add(rowName + " at " + fx + "," + fy + " (" + hit?.GetType().Name + ") opens " + (owner == null ? "no menu" : owner.GetType().Name + " '" + owner.Name + "'"));
            }
        }
        Assert.True(misses.Count == 0, string.Join(" | ", misses));
    }

    private static bool InView(Border row)
    {
        ScrollViewer? viewer = row.FindAncestorOfType<ScrollViewer>();
        if (viewer == null)
            return true;
        Point? topLeft = row.TranslatePoint(new Point(0, 0), viewer);
        return topLeft is { } p && p.Y >= 0 && p.Y + row.Bounds.Height <= viewer.Bounds.Height;
    }

    // What a right-click at `point` lands on, as Avalonia's hit test decides it on screen: the topmost visible,
    // hit-testable element there that draws something (a background, even a transparent one, text, a shape, a picture),
    // inside every clip on the way. (The hidden test host's own hit test answers from its last composited frame, which
    // lacks rows a list realized later.)
    internal static Visual? HitAt(Visual visual, Point point)
    {
        if (!visual.IsVisible || visual is InputElement { IsHitTestVisible: false } || visual.Opacity <= 0)
            return null;
        bool inside = new Rect(visual.Bounds.Size).Contains(point);
        if (visual.ClipToBounds && !inside)
            return null;
        foreach (Visual child in visual.GetVisualChildren().Reverse().OrderByDescending(c => c.ZIndex))
        {
            Point? inChild = visual.TranslatePoint(point, child);
            if (inChild is { } p && HitAt(child, p) is { } hit)
                return hit;
        }
        return inside && DrawsSomething(visual) ? visual : null;
    }

    private static bool DrawsSomething(Visual visual) => visual switch
    {
        Border b => b.Background != null || b.BorderBrush != null && b.BorderThickness != default,
        Panel p => p.Background != null,
        ContentPresenter c => c.Background != null,
        TemplatedControl t => t.Background != null,
        TextBlock or Shape or Image => true,
        _ => false
    };

    [Fact]
    public void RelationActionCard_EveryPartOpensItsMenu()
    {
        AvaloniaRenderThread.Run(() =>
        {
            BuilderFakeEngine fake = new();
            fake.AddFiveActionRelation();
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(RelationBuilderTests.CreateVmAsync(fake, "context-menu-rows.pr0file"));
            using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 900));
            AvaloniaRenderThread.Wait(RelationBuilderTests.OpenAsync(vm, "Button CP2 B3"));
            Assert.Equal(5, vm.RelationNodes.Count);
            AssertEveryPointOpensTheRowMenu(main, "RelationNodeRow", 4);
            main.Snap("context-menu-relation-rows.png");
            AvaloniaRenderThread.Wait(RelationBuilderTests.SettleAllAsync());
        });
    }

    [Fact]
    public void BindCard_EveryPartOpensItsMenu()
    {
        AvaloniaRenderThread.Run(() =>
        {
            (MainWindowViewModel vm, _) = AvaloniaRenderThread.Wait(FollowInputTests.CreateBindsPageAsync());
            using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 800));
            Assert.True(vm.Binds.Count >= 2);
            AssertEveryPointOpensTheRowMenu(main, "BindRow", 2);
        });
    }
}
