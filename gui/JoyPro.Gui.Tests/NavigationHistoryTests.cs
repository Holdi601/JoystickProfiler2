using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Tests.QualityGate;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// Back and Forward in the header (docs/ui/navigation.md): the history class (push, back, forward, the cap, the same place
// twice in a row kept once, forward cleared by a new place, nothing pushed while a place is restored), the view model
// (page changes and "Open both relations" push, Back puts page, search, open cards and the relation back), and a render:
// Check list scrolled to a card -> "Open both relations" -> Back shows the same list with the same card in view. The
// header with the arrows next to Undo fits at 1280 and 2267 px in both themes.
// JOYPRO_NAV_OUT=<folder> keeps the renders.
[SupportedOSPlatform("windows")]
public sealed class NavigationHistoryTests
{
    private static NavigationEntry Place(UiStage stage, string selection = "", double scrollY = 0, string sub = "") => new()
    {
        Stage = stage,
        SubView = sub,
        Selection = selection,
        Scroll = new Dictionary<string, (double X, double Y)> { ["/0"] = (0, scrollY) }
    };

    [Fact]
    public void BackAndForward_WalkThePlaces()
    {
        NavigationHistory history = new();
        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Null(history.GoBack(Place(UiStage.Start)));

        history.Push(Place(UiStage.Start));
        history.Push(Place(UiStage.Export, scrollY: 300));
        Assert.Equal(2, history.BackCount);
        Assert.Equal(UiStage.Export, history.PeekBack!.Stage);

        NavigationEntry? back = history.GoBack(Place(UiStage.Relations, "Pitch"));
        Assert.NotNull(back);
        Assert.Equal(UiStage.Export, back!.Stage);
        Assert.Equal(300, back.Scroll["/0"].Y);
        Assert.True(history.CanGoForward);
        Assert.Equal("Pitch", history.PeekForward!.Selection);

        NavigationEntry? start = history.GoBack(back);
        Assert.Equal(UiStage.Start, start!.Stage);
        Assert.False(history.CanGoBack);
        Assert.Equal(2, history.ForwardCount);

        NavigationEntry? again = history.GoForward(start);
        Assert.Equal(UiStage.Export, again!.Stage);
        NavigationEntry? relations = history.GoForward(again);
        Assert.Equal(UiStage.Relations, relations!.Stage);
        Assert.Equal("Pitch", relations.Selection);
        Assert.False(history.CanGoForward);
        Assert.Equal(2, history.BackCount);
    }

    [Fact]
    public void NewPlace_ClearsForward()
    {
        NavigationHistory history = new();
        history.Push(Place(UiStage.Start));
        history.Push(Place(UiStage.Binds));
        _ = history.GoBack(Place(UiStage.Layout));
        Assert.True(history.CanGoForward);

        history.Push(Place(UiStage.Settings));
        Assert.False(history.CanGoForward);
        Assert.Equal(0, history.ForwardCount);
    }

    [Fact]
    public void SamePlaceTwiceInARow_IsKeptOnce_TheNewerOne()
    {
        NavigationHistory history = new();
        history.Push(Place(UiStage.Export, scrollY: 100));
        history.Push(Place(UiStage.Export, scrollY: 450));
        Assert.Equal(1, history.BackCount);
        Assert.Equal(450, history.PeekBack!.Scroll["/0"].Y);

        // Another selection is another place; the same one after it is again a new entry.
        history.Push(Place(UiStage.Relations, "Pitch"));
        history.Push(Place(UiStage.Relations, "Roll"));
        history.Push(Place(UiStage.Relations, "Roll"));
        history.Push(Place(UiStage.Layout, sub: "pictures"));
        history.Push(Place(UiStage.Layout, sub: "cheatsheets"));
        Assert.Equal(5, history.BackCount);
    }

    [Fact]
    public void Back_SkipsPlacesEqualToTheCurrentOne()
    {
        NavigationHistory history = new();
        history.Push(Place(UiStage.Export));
        // A jump that ended where it started (the relation was open already).
        history.Push(Place(UiStage.Relations, "Pitch"));
        NavigationEntry? back = history.GoBack(Place(UiStage.Relations, "Pitch"));
        Assert.Equal(UiStage.Export, back!.Stage);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void NothingIsPushed_WhileAPlaceIsRestored()
    {
        NavigationHistory history = new();
        int changes = 0;
        history.Changed += (_, _) => changes++;
        using (history.BeginRestore())
        {
            Assert.True(history.IsRestoring);
            history.Push(Place(UiStage.Binds));
            using (history.BeginRestore())
                history.Push(Place(UiStage.Layout));
            Assert.True(history.IsRestoring);
            history.Push(Place(UiStage.Modifiers));
        }
        Assert.False(history.IsRestoring);
        Assert.Equal(0, history.BackCount);
        Assert.Equal(0, changes);
        history.Push(Place(UiStage.Binds));
        Assert.Equal(1, history.BackCount);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void History_IsCapped_TheOldestGoesFirst()
    {
        NavigationHistory history = new();
        Assert.Equal(50, history.Capacity);
        for (int i = 0; i < 70; i++)
            history.Push(Place(UiStage.Relations, "Relation " + i.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(50, history.BackCount);
        Assert.Equal("Relation 20", history.BackEntries[0].Selection);
        Assert.Equal("Relation 69", history.PeekBack!.Selection);

        // Walking back through all of them fills the forward stack up to the cap too.
        NavigationEntry current = Place(UiStage.Start);
        int steps = 0;
        while (history.GoBack(current) is { } previous)
        {
            current = previous;
            steps++;
        }
        Assert.Equal(50, steps);
        Assert.Equal(50, history.ForwardCount);

        NavigationHistory small = new(capacity: 3);
        foreach (UiStage stage in new[] { UiStage.Start, UiStage.Relations, UiStage.Binds, UiStage.Modifiers, UiStage.Layout })
            small.Push(Place(stage));
        Assert.Equal([UiStage.Layout, UiStage.Modifiers, UiStage.Binds], small.BackEntries.Reverse().Select(e => e.Stage).ToArray());
    }

    // Fourteen "actions in several relations" cards over the fake profile's relations (Pitch, Roll, ...), sorted by
    // their relation pair; "Action 10" is somewhere in the middle of the list.
    internal static string DuplicatesJson(int count = 14)
    {
        string[] names = ["Pitch", "Roll", "Throttle", "Weapon Release", "Gun Trigger", "Master Arm", "TMS Up", "TMS Down"];
        StringBuilder groups = new();
        StringBuilder duplicates = new();
        for (int i = 0; i < count; i++)
        {
            string a = names[i % names.Length], b = names[(i + 3) % names.Length];
            string key = "DCS|d" + (3100 + i).ToString(CultureInfo.InvariantCulture) + "|F-16C_50";
            string title = "Action " + (i + 1).ToString("00", CultureInfo.InvariantCulture);
            if (i > 0)
            {
                groups.Append(',');
                duplicates.Append(',');
            }
            duplicates.Append("{\"Key\":\"" + key + "\",\"Suggested\":\"" + a + "\",\"Confidence\":80}");
            groups.Append("{\"Game\":\"DCS\",\"Keys\":[\"" + key + "\"],\"Suggested\":\"" + a + "\",\"Confidence\":80,\"Message\":\"m" + i.ToString(CultureInfo.InvariantCulture) + "\"," +
                          "\"Relations\":[{\"Relation\":\"" + a + "\",\"Device\":\"\",\"Input\":\"\"},{\"Relation\":\"" + b + "\",\"Device\":\"\",\"Input\":\"\"}]," +
                          "\"Actions\":[{\"Title\":\"" + title + "\",\"Planes\":[\"F-16C_50\"]}]}");
        }
        return "{\"Duplicates\":[" + duplicates + "],\"Groups\":[" + groups + "]}";
    }

    private static CheckIssueCard Card(MainWindowViewModel vm, string title) =>
        vm.CheckSections.SelectMany(s => s.Cards).Single(c => c.Headline.Contains("'" + title + "'", StringComparison.Ordinal));

    [Fact]
    public async Task ViewModel_PageChangesAndOpenBoth_Push_BackRestoresTheCheckList()
    {
        MainWindowViewModel vm = new(new UiDesignRenderTests.DesignFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location,
            ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "navigation.pr0file")
        };
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        vm.ApplyDuplicates(DuplicatesJson());
        Assert.False(vm.CanNavigateBack);
        Assert.Equal("Back (Alt+Left): nothing to go back to yet", vm.NavigateBackTip);

        // Start -> Export: the page change pushes Start.
        vm.CurrentStage = UiStage.Export;
        Assert.True(vm.CanNavigateBack);
        Assert.Equal("Back to Start (Alt+Left or the mouse's back button)", vm.NavigateBackTip);

        // The check list as the user left it: searched, one card closed.
        vm.CheckSearchText = "Action";
        CheckIssueCard closed = Card(vm, "Action 03");
        closed.IsExpanded = false;
        CheckIssueCard target = Card(vm, "Action 10");
        target.OpenAllCommand!.Execute(null);
        Assert.Equal(UiStage.Relations, vm.CurrentStage);
        Assert.Equal(target.Relations[0], vm.SelectedRelation?.Name);
        Assert.Equal("Back to Export (Alt+Left or the mouse's back button)", vm.NavigateBackTip);

        // Meanwhile the list changes: search cleared, the card open again.
        vm.CheckSearchText = string.Empty;
        Card(vm, "Action 03").IsExpanded = true;
        // A search typed on the Relations page meanwhile stays (Back restores the searches of the page it returns to).
        vm.RelationSearchText = target.Relations[0];

        vm.NavigateBackCommand.Execute(null);
        Assert.Equal(UiStage.Export, vm.CurrentStage);
        Assert.Equal("Action", vm.CheckSearchText);
        Assert.Equal(target.Relations[0], vm.RelationSearchText);
        Assert.False(Card(vm, "Action 03").IsExpanded);
        Assert.True(Card(vm, "Action 10").IsExpanded);
        Assert.True(vm.CanNavigateForward);
        Assert.Equal("Forward to Relations: '" + target.Relations[0] + "' (Alt+Right or the mouse's forward button)", vm.NavigateForwardTip);

        // Restoring pushed nothing: one more Back goes to Start.
        vm.NavigateBackCommand.Execute(null);
        Assert.Equal(UiStage.Start, vm.CurrentStage);
        Assert.False(vm.CanNavigateBack);

        // Forward twice: Export, then the relation that was open.
        vm.NavigateForwardCommand.Execute(null);
        Assert.Equal(UiStage.Export, vm.CurrentStage);
        vm.NavigateForwardCommand.Execute(null);
        Assert.Equal(UiStage.Relations, vm.CurrentStage);
        Assert.Equal(target.Relations[0], vm.SelectedRelation?.Name);
        Assert.False(vm.CanNavigateForward);

        // A new place clears Forward; Layout's Cheat sheets is a place of its own.
        vm.NavigateBackCommand.Execute(null);
        vm.CurrentStage = UiStage.Layout;
        Assert.False(vm.CanNavigateForward);
        vm.ShowCheatSheetsSectionCommand.Execute(null);
        vm.ShowDevicePicturesSectionCommand.Execute(null);
        Assert.Equal("Back to Layout: Cheat sheets (Alt+Left or the mouse's back button)", vm.NavigateBackTip);
        vm.NavigateBackCommand.Execute(null);
        Assert.True(vm.IsCheatSheetsSection);
        vm.NavigateBackCommand.Execute(null);
        Assert.False(vm.IsCheatSheetsSection);
        Assert.Equal(UiStage.Layout, vm.CurrentStage);
        vm.NavigateBackCommand.Execute(null);
        Assert.Equal(UiStage.Export, vm.CurrentStage);
        vm.StopGameConfigWatcher();
    }

    [Fact]
    public void Render_CheckList_OpenBoth_Back_ShowsTheSameCardAgain()
    {
        string? outDir = OutDir();
        List<string> problems = [];
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                vm.ApplyDuplicates(DuplicatesJson());
                vm.CurrentStage = UiStage.Export;
                using HiddenWindow window = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 800));
                Settle(window, vm);
                CheckIssueCard target = Card(vm, "Action 10");
                Border card = CardBorder(window, target) ?? throw new InvalidOperationException("the card 'Action 10' is not on the Export page");
                ScrollViewer page = card.GetVisualAncestors().OfType<ScrollViewer>().First();
                card.BringIntoView();
                Settle(window, vm);
                double offset = page.Offset.Y;
                Assert.True(offset > 200, "the Export page must be scrolled down to the card (offset " + offset + ")");
                AssertInView(card, page, "before 'Open both relations'");
                Save(window, outDir, "check-before.png");

                target.OpenAllCommand!.Execute(null);
                Settle(window, vm);
                Assert.Equal(UiStage.Relations, vm.CurrentStage);
                Assert.False(card.IsEffectivelyVisible);
                Assert.True(vm.CanNavigateBack);
                // The history knows where the list was.
                Assert.Contains(vm.Navigation.PeekBack!.Scroll.Values, v => Math.Abs(v.Y - offset) < 1);
                Save(window, outDir, "relations-opened.png");
                // While the Export page is away its list goes back to the top (as after a refresh of the page).
                page.Offset = default;

                vm.NavigateBackCommand.Execute(null);
                Settle(window, vm);
                Assert.Equal(UiStage.Export, vm.CurrentStage);
                Border again = CardBorder(window, Card(vm, "Action 10")) ?? throw new InvalidOperationException("the card is gone after Back");
                Assert.True(again.IsEffectivelyVisible);
                Assert.True(Math.Abs(page.Offset.Y - offset) < 1, "scrolled back to " + page.Offset.Y + ", was " + offset);
                AssertInView(again, page, "after Back");
                Save(window, outDir, "check-after-back.png");

                // Forward opens the relation again, Alt+Left-style Back once more returns to the card.
                vm.NavigateForwardCommand.Execute(null);
                Settle(window, vm);
                Assert.Equal(UiStage.Relations, vm.CurrentStage);
                vm.NavigateBackCommand.Execute(null);
                Settle(window, vm);
                AssertInView(CardBorder(window, Card(vm, "Action 10"))!, page, "after Forward and Back");
            }
            finally
            {
                vm.StopGameConfigWatcher();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Render_Header_BackAndForwardFit_At1280And2267_LightAndDark()
    {
        string? outDir = OutDir();
        List<string> problems = [];
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                // Back enabled (Start -> Relations -> Export), Forward disabled: both looks in one header.
                vm.CurrentStage = UiStage.Relations;
                vm.CurrentStage = UiStage.Export;
                foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach ((double width, double height) in new[] { (1280.0, 800.0), (2267.0, 1250.0), (1200.0, 760.0) })
                    {
                        string label = theme + " " + width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
                        using HiddenWindow window = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(width, height));
                        Settle(window, vm);
                        Control root = GateRender.Root(window);
                        Button? backButton = root.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "NavBackButton");
                        Button? forwardButton = root.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "NavForwardButton");
                        if (backButton == null || forwardButton == null)
                        {
                            problems.Add(label + ": no Back / Forward buttons in the header");
                            continue;
                        }
                        Border header = backButton.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("surface"));
                        foreach ((Control a, Rect ra, Control b, Rect rb) in LayoutChecks.Overlaps(root, width, height, header))
                            problems.Add(label + ": " + LayoutChecks.Describe(a) + " overlaps " + LayoutChecks.Describe(b) + " at " + ra + " / " + rb);
                        foreach ((TextBlock text, double need, double got) in LayoutChecks.ClippedTexts(root, width, height, header))
                            problems.Add(label + ": '" + text.Text + "' cut off (needs " + need.ToString("0", CultureInfo.InvariantCulture) + ", has " + got.ToString("0", CultureInfo.InvariantCulture) + ")");
                        // Every button of the header is whole inside the window (nothing pushed past its right edge).
                        Rect? headerRect = GateRender.BoundsInWindow(header, root);
                        foreach (Button button in header.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.TemplatedParent == null))
                        {
                            Rect? r = GateRender.BoundsInWindow(button, root);
                            if (r == null || headerRect == null || r.Value.Right > headerRect.Value.Right + 0.5 || r.Value.Right > width || r.Value.Width < 20)
                                problems.Add(label + ": " + LayoutChecks.Describe(button) + " does not fit in the header (" + r + ", header " + headerRect + ")");
                        }
                        Rect? back = GateRender.SeenRect(backButton, root, width, height);
                        Rect? forward = GateRender.SeenRect(forwardButton, root, width, height);
                        if (back == null || forward == null)
                            problems.Add(label + ": Back or Forward is not seen");
                        else if (back.Value.Right > forward.Value.X)
                            problems.Add(label + ": Back is not left of Forward");
                        if (!backButton.IsEffectivelyEnabled || forwardButton.IsEffectivelyEnabled)
                            problems.Add(label + ": Back must be enabled, Forward disabled (history: Start, Relations)");
                        // The open profile's name keeps room next to the buttons.
                        TextBlock? name = header.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == vm.ProfileDisplayName);
                        if (name == null || name.Bounds.Width < 60)
                            problems.Add(label + ": the profile name has no room (" + name?.Bounds.Width + " px)");
                        Save(window, outDir, "header-" + label.Replace(' ', '-') + ".png");
                    }
                }
            }
            finally
            {
                vm.StopGameConfigWatcher();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(40)));
    }

    private static string? OutDir()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_NAV_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return null;
        Directory.CreateDirectory(outDir);
        return outDir;
    }

    private static void Save(HiddenWindow window, string? outDir, string name)
    {
        if (outDir == null)
            return;
        using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = window.Render();
        bitmap.Save(Path.Combine(outDir, name));
    }

    private static void Settle(HiddenWindow window, MainWindowViewModel vm)
    {
        for (int i = 0; i < 100 && vm.IsBusy; i++)
        {
            AvaloniaRenderThread.Pump();
            Thread.Sleep(20);
        }
        window.Settle();
        window.Settle();
    }

    private static Border? CardBorder(HiddenWindow window, CheckIssueCard card) =>
        GateRender.Root(window).GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("card") && ReferenceEquals(b.DataContext, card));

    // The card lies inside the viewport of the page's scroll viewer.
    private static void AssertInView(Border card, ScrollViewer page, string when)
    {
        Matrix? m = card.TransformToVisual(page);
        Assert.True(m != null, when + ": the card is not under the page's scroll viewer");
        Rect r = new Rect(card.Bounds.Size).TransformToAABB(m!.Value);
        Assert.True(r.Y >= -1 && r.Y < page.Viewport.Height - 20, when + ": the card is at " + r + ", the viewport is " + page.Viewport);
    }
}
