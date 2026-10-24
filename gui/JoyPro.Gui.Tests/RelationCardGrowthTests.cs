using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// A relation card of the Relations list grows when the relation gets actions of more games (a new line of game chips):
// the cards above and below move, nothing overlaps (a user saw the grown card drawn over its neighbours). The real
// window, never shown (laid out and rendered in memory), with the virtualizing list. JOYPRO_GUI_PARITY_PREVIEW names a folder for the pictures.
[SupportedOSPlatform("windows")]
public sealed class RelationCardGrowthTests
{
    private static readonly string[] ManyGames =
        ["DCS", "IL2Game", "IL2Korea", "MSFS2020", "MSFS2024", "XPlane12", "WarThunder", "StarCitizen", "EliteDangerous", "FalconBMS", "NuclearOption", "IL21946", "IL2CoD", "Arma3"];

    // Overlaps of neighbouring cards that are drawn (top of the next one above the bottom of this one).
    internal static List<string> Overlaps(ListBox list)
    {
        List<(ListBoxItem Item, Rect Box)> cards = list.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(i => i.IsEffectivelyVisible && i.Opacity > 0 && i.Bounds.Height > 0)
            .Select(i => (i, new Rect(i.TranslatePoint(new Point(0, 0), list) ?? default, i.Bounds.Size)))
            .OrderBy(c => c.Item2.Top).ToList();
        List<string> problems = [];
        for (int i = 0; i + 1 < cards.Count; i++)
        {
            // The content of a card must fit into it, and the next card starts below it.
            Control? content = cards[i].Item.GetVisualDescendants().OfType<Grid>().FirstOrDefault();
            double contentBottom = content == null ? 0 : (content.TranslatePoint(new Point(0, content.Bounds.Height), list)?.Y ?? 0);
            if (cards[i + 1].Box.Top < cards[i].Box.Bottom - 0.5 || cards[i + 1].Box.Top < contentBottom - 0.5)
                problems.Add((cards[i].Item.DataContext as RelationListItem)?.Name + " (" + cards[i].Box + ", content to " + contentBottom.ToString("0") + ") overlaps " +
                             (cards[i + 1].Item.DataContext as RelationListItem)?.Name + " (" + cards[i + 1].Box + ")");
        }
        return problems;
    }

    private static string Realized(ListBox list) =>
        string.Join(", ", list.GetRealizedContainers().OrderBy(c => c.Bounds.Top).Select(c => (c.DataContext as RelationListItem)?.Name + "@" + c.Bounds.Top.ToString("0") + "+" + c.Bounds.Height.ToString("0"))) +
        " offset " + list.GetVisualDescendants().OfType<ScrollViewer>().First().Offset.Y.ToString("0");

    [Fact]
    public void GrowingCard_MovesItsNeighbours_NothingOverlaps()
    {
        AvaloniaRenderThread.Run(() =>
        {
            foreach ((int width, int height) in new[] { (1280, 800), (2267, 1250) })
            {
                LocalAiFakeEngine engine = new() { RelationCount = 60 };
                MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
                {
                    EnginePath = typeof(MainWindowViewModel).Assembly.Location,
                    ProfilePath = Path.Combine(Path.GetTempPath(), "relations-card-growth.pr0file")
                };
                vm.CurrentStage = UiStage.Relations;
                using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(width, height));
                AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
                main.Settle();
                ListBox list = main.Window.FindControl<ListBox>("RelationsListBox")!;
                RelationListItem grown = vm.Relations[3];
                vm.SelectedRelation = grown;
                main.Settle();
                Assert.Empty(Overlaps(list));
                double before = list.ContainerFromItem(grown)!.Bounds.Height;

                // Its actions now come from many games, then from one again: the card grows and shrinks in place.
                grown.Games = string.Join(", ", ManyGames);
                grown.Planes = "14";
                main.Settle();
                main.Snap("relation-card-grown-" + width + ".png");
                double after = list.ContainerFromItem(grown)!.Bounds.Height;
                Assert.True(after > before + 10, "the card did not grow: " + before + " -> " + after);
                List<string> grownProblems = Overlaps(list);
                Assert.True(grownProblems.Count == 0, "grown: " + string.Join("; ", grownProblems));

                // A card further down grows too (the one under the selection).
                vm.Relations[5].Games = string.Join(", ", ManyGames.Take(9));
                main.Settle();
                Assert.Empty(Overlaps(list));

                grown.Games = "DCS";
                main.Settle();
                Assert.Empty(Overlaps(list));
                Assert.True(list.ContainerFromItem(grown)!.Bounds.Height < after - 10);

                // Scrolled into the middle of the list: a card there grows, with its aircraft (as after an add).
                RelationListItem middle = vm.Relations[30];
                list.ScrollIntoView(middle);
                vm.SelectedRelation = middle;
                main.Settle();
                middle.Games = string.Join(", ", ManyGames);
                middle.PlaneIds = ["F-16C_50", "FA-18C_hornet", "A-10C", "F-14", "AV8BNA", "JF-17"];
                middle.Planes = "19";
                main.Settle();
                main.Snap("relation-card-grown-middle-" + width + ".png");
                List<string> middleProblems = Overlaps(list);
                Assert.True(middleProblems.Count == 0, "scrolled: " + string.Join("; ", middleProblems));
                // The card above it grows too.
                vm.Relations[29].Games = string.Join(", ", ManyGames);
                main.Settle();
                middleProblems = Overlaps(list);
                Assert.True(middleProblems.Count == 0, "scrolled, card above: " + string.Join("; ", middleProblems) + " | realized " + Realized(list));

                // Scrolled with the wheel (no "bring into view"), a card on screen picked and grown.
                ScrollViewer scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
                scroller.Offset = new Vector(0, scroller.Offset.Y + 900);
                main.Settle();
                RelationListItem onScreen = (RelationListItem)list.GetRealizedContainers().OrderBy(c => c.Bounds.Top).Skip(1).First().DataContext!;
                vm.SelectedRelation = onScreen;
                main.Settle();
                string realizedBefore = Realized(list);
                onScreen.Games = string.Join(", ", ManyGames);
                main.Settle();
                main.Snap("relation-card-grown-wheel-" + width + ".png");
                middleProblems = Overlaps(list);
                Assert.True(middleProblems.Count == 0, "wheel-scrolled: " + string.Join("; ", middleProblems));
                Assert.True(list.GetRealizedContainers().Any(c => ReferenceEquals(c.DataContext, onScreen)), "the grown card left the view: " + onScreen.Name + " | before " + realizedBefore + " | after " + Realized(list));
            }
        });
    }
}
