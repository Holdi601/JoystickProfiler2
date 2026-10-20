using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Relations page laid out as the whole window at 1280x800 and 1920x1080 (docs/relation-builder.md): an open relation
// with its suggestions, the search over every game with ticked rows, a new relation, and the JoyPro assistant with an
// answer. Every layout is checked: no horizontal scroll bar anywhere on the page (rows wrap or end in "..."), and every
// visible button inside the window. With JOYPRO_UI_DESIGN_OUT naming a folder the layouts are also saved as pictures:
//   set JOYPRO_UI_DESIGN_OUT=<repo>\docs\ui & dotnet test gui/JoyPro.Gui.Tests --filter RelationBuilderRenderTests
// Files: relation-builder-<width>-<theme>.png, relation-builder-add-<width>-dark.png, relation-builder-new-1280-dark.png,
// relation-builder-assistant-<width>-dark.png, relation-builder-aircraft-<width>-dark.png.
[SupportedOSPlatform("windows")]
public sealed class RelationBuilderRenderTests
{
    private static readonly (int Width, int Height)[] Sizes = [(1280, 800), (1920, 1080)];

    // The view models are prepared on the test's thread (their engine calls are awaited here); only the layout runs on
    // Avalonia's render thread, and every window lets go of its view model afterwards.
    [Fact]
    public async System.Threading.Tasks.Task RelationsPage_HasNoHorizontalScrollBarAt1280And1920()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_UI_DESIGN_OUT");
        if (!string.IsNullOrWhiteSpace(outDir))
            Directory.CreateDirectory(outDir);
        try
        {
            // An open relation: its actions and the suggestions (with a conflict and a weak match).
            MainWindowViewModel vm = await OpenRelationAsync();
            AvaloniaRenderThread.Run(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                foreach ((int width, int height) in Sizes)
                    Check(vm, width, height, outDir, "relation-builder-" + width + "-dark.png");
                if (!string.IsNullOrWhiteSpace(outDir))
                {
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                    AvaloniaRenderThread.Pump();
                    Check(vm, 1920, 1080, outDir, "relation-builder-1920-light.png");
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                }
            });

            // Add actions: the search over every game, some rows ticked.
            await SearchAsync(vm);
            AvaloniaRenderThread.Run(() =>
            {
                foreach ((int width, int height) in Sizes)
                    Check(vm, width, height, outDir, "relation-builder-add-" + width + "-dark.png");
            });

            // A new relation: its name, axes of several games ticked.
            MainWindowViewModel fresh = await NewRelationAsync();
            AvaloniaRenderThread.Run(() => Check(fresh, 1280, 800, outDir, "relation-builder-new-1280-dark.png"));

            // The JoyPro assistant with an answer (created, added per game, the steps, rejected ids, Undo).
            MainWindowViewModel ai = await AssistantAsync();
            AvaloniaRenderThread.Run(() =>
            {
                foreach ((int width, int height) in Sizes)
                    Check(ai, width, height, outDir, "relation-builder-assistant-" + width + "-dark.png");
            });
        }
        finally
        {
            await RelationBuilderTests.SettleAllAsync();
            AvaloniaRenderThread.Run(() => Application.Current!.RequestedThemeVariant = ThemeVariant.Default);
        }
    }

    // Same id, different meaning: the open relation's action with its aircraft open (badge, mixed flag, differs marker,
    // Apply), a search row with its aircraft open, and the profile check notice. File: relation-builder-aircraft-<width>-dark.png.
    [Fact]
    public async System.Threading.Tasks.Task RelationsPage_AircraftSelectorsFitAt1280And1920()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_UI_DESIGN_OUT");
        if (!string.IsNullOrWhiteSpace(outDir))
            Directory.CreateDirectory(outDir);
        try
        {
            PerAircraftFakeEngine fake = new();
            MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "relation-builder-aircraft.pr0file");
            await RelationBuilderTests.OpenAsync(vm, "Flare");
            await RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Any(n => n.HasAircraft));
            await vm.RefreshAircraftCheckAsync();
            vm.RelationCheckExpanded = true;
            RelationNodeItem node = vm.RelationNodes.First(n => n.HasAircraft);
            node.Aircraft!.IsExpanded = true;
            node.Aircraft.Items.Single(i => i.Plane == "F-14").IsChecked = true;
            vm.BuilderSearchText = "pitch";
            await vm.SearchBuilderActionsAsync();
            await RelationBuilderTests.SettleAllAsync();
            BuilderSearchResultItem row = vm.BuilderSearchResults.First(r => r.HasAircraft);
            row.Aircraft!.IsExpanded = true;
            AvaloniaRenderThread.Run(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                foreach ((int width, int height) in Sizes)
                    Check(vm, width, height, outDir, "relation-builder-aircraft-" + width + "-dark.png");
            });
        }
        finally
        {
            await RelationBuilderTests.SettleAllAsync();
            AvaloniaRenderThread.Run(() => Application.Current!.RequestedThemeVariant = ThemeVariant.Default);
        }
    }

    // The open relation in a window (its list selection bound) through a refresh of the relation list: the relation
    // stays selected and the "how to start" steps are never drawn over its actions (they used to be after any edit).
    [Fact]
    public async System.Threading.Tasks.Task RelationsPage_RefreshKeepsTheRelationAndNothingOverlaps()
    {
        try
        {
            BuilderFakeEngine fake = new();
            MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "relation-builder-refresh.pr0file");
            AvaloniaRenderThread.Run(() =>
            {
                MainWindow main = new() { DataContext = vm, Width = 1280, Height = 800 };
                try
                {
                    Lay(main, 1280, 800);
                    vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Flare");
                    AvaloniaRenderThread.Wait(RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Count > 0 && !vm.IsBusy));
                    AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
                    AvaloniaRenderThread.Pump();
                    Assert.Equal("Flare", vm.SelectedRelation?.Name);
                    Assert.True(vm.ShowRelationNodeList);
                    Assert.False(vm.HasNoRelationSelected);
                }
                finally
                {
                    main.DataContext = null;
                    AvaloniaRenderThread.Pump();
                }
                // A fresh window shows the state: the actions, not the steps.
                foreach ((int width, int height) in Sizes)
                {
                    MainWindow shown = new() { DataContext = vm, Width = width, Height = height };
                    try
                    {
                        Lay(shown, width, height);
                        Control page = (Control)shown.Content!;
                        Control steps = page.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "RelationStartSteps");
                        Control list = page.GetVisualDescendants().OfType<ListBox>().Single(g => g.Name == "RelationNodeList");
                        Assert.True(list.IsEffectivelyVisible, width + ": the open relation's actions are not shown.");
                        Assert.False(steps.IsEffectivelyVisible, width + ": the 'how to start' steps are drawn over the actions.");
                    }
                    finally
                    {
                        shown.DataContext = null;
                        AvaloniaRenderThread.Pump();
                    }
                }
            });
        }
        finally
        {
            await RelationBuilderTests.SettleAllAsync();
        }
    }

    private static void Lay(MainWindow main, int width, int height)
    {
        Size size = new(width, height);
        Control page = (Control)main.Content!;
        for (int pass = 0; pass < 6; pass++)
        {
            main.Measure(size);
            main.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
    }

    // Lays the window out at the size, checks it, saves the picture when asked, and lets go of the view model.
    private static void Check(MainWindowViewModel vm, int width, int height, string? outDir, string file)
    {
        AvaloniaRenderThread.Pump();
        MainWindow main = new() { DataContext = vm, Width = width, Height = height };
        try
        {
            Size size = new(width, height);
            Control page = (Control)main.Content!;
            for (int pass = 0; pass < 6; pass++)
            {
                main.Measure(size);
                main.Arrange(new Rect(size));
                page.Measure(size);
                page.Arrange(new Rect(size));
                AvaloniaRenderThread.Pump();
            }
            Control relations = page.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "RelationsPaneGrid");
            Assert.True(relations.IsEffectivelyVisible);
            List<ScrollBar> horizontal = relations.GetVisualDescendants().OfType<ScrollBar>()
                .Where(s => s.Orientation == Orientation.Horizontal && s.IsEffectivelyVisible && s.Bounds.Width > 0 && s.Bounds.Height > 0)
                .ToList();
            Assert.True(horizontal.Count == 0, file + ": a horizontal scroll bar is visible in " +
                                               string.Join(", ", horizontal.Select(s => s.FindAncestorOfType<ScrollViewer>()?.Name ?? "a scroll viewer")));
            foreach (ScrollViewer viewer in relations.GetVisualDescendants().OfType<ScrollViewer>().Where(v => v.IsEffectivelyVisible && v.Viewport.Width > 0))
                Assert.True(viewer.Extent.Width <= viewer.Viewport.Width + 1, file + ": " + (viewer.Name ?? "a scroll viewer") + " is wider than its view (" + viewer.Extent.Width + " > " + viewer.Viewport.Width + ")");
            foreach (Button button in relations.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0))
            {
                Point? right = button.TranslatePoint(new Point(button.Bounds.Width, 0), main);
                Assert.True(right == null || right.Value.X <= width + 1, file + ": the button '" + (button.Content as string ?? button.Name ?? "?") + "' ends outside the window (" + right?.X + ")");
            }
            if (!string.IsNullOrWhiteSpace(outDir))
            {
                IBrush background = main.TryFindResource("JP.PageBackgroundBrush", main.ActualThemeVariant, out object? found) && found is IBrush own
                    ? own
                    : new SolidColorBrush(Color.Parse("#202020"));
                using RenderTargetBitmap content = new(new PixelSize(width, height));
                content.Render(page);
                using RenderTargetBitmap bitmap = new(new PixelSize(width, height));
                using (DrawingContext context = bitmap.CreateDrawingContext())
                {
                    context.FillRectangle(background, new Rect(size));
                    context.DrawImage(content, new Rect(size), new Rect(size));
                }
                bitmap.Save(Path.Combine(outDir, file));
            }
        }
        finally
        {
            main.DataContext = null;
            AvaloniaRenderThread.Pump();
        }
    }

    private static async System.Threading.Tasks.Task<MainWindowViewModel> OpenRelationAsync()
    {
        BuilderFakeEngine fake = new() { LlmEnabled = true };
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        vm.BuilderShowWeakMatches = true;
        vm.ShowBuilderSuggestedCommand.Execute(null);
        await RelationBuilderTests.WaitForAsync(() => vm.IsBuilderChatAvailable);
        vm.StatusText = "Ready.";
        return vm;
    }

    private static async System.Threading.Tasks.Task<bool> SearchAsync(MainWindowViewModel vm)
    {
        vm.ShowBuilderFindCommand.Execute(null);
        vm.BuilderSearchText = "flare";
        await vm.SearchBuilderActionsCommand.ExecuteAsync(null);
        vm.BuilderSearchResults.Single(r => r.Id == "SimCMSUp").IsChecked = true;
        vm.BuilderSearchResults.Single(r => r.Id == "ID_FLARES").IsChecked = true;
        vm.StatusText = "Ready.";
        return true;
    }

    private static async System.Threading.Tasks.Task<MainWindowViewModel> NewRelationAsync()
    {
        BuilderFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        vm.StartNewRelationCommand.Execute(null);
        vm.RelationNameInput = "Throttle ALL";
        vm.SetBuilderKindCommand.Execute("axis");
        vm.BuilderSearchText = "throttle";
        await vm.SearchBuilderActionsCommand.ExecuteAsync(null);
        foreach (BuilderSearchResultItem row in vm.BuilderSearchResults.Take(3))
            row.IsChecked = true;
        vm.StatusText = "Ready.";
        return vm;
    }

    private static async System.Threading.Tasks.Task<MainWindowViewModel> AssistantAsync()
    {
        BuilderFakeEngine fake = new() { LlmEnabled = true };
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake, "relation-builder-render.pr0file");
        File.WriteAllText(vm.ProfilePath, "before the assistant");
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        await RelationBuilderTests.WaitForAsync(() => vm.IsBuilderChatAvailable);
        vm.ShowBuilderAssistantCommand.Execute(null);
        vm.AssistantInput = "Add all inputs that bind all thrust to one axis into one relation called Throttle ALL, including collectives and spaceship forward/backward thrusters";
        await vm.AskAssistantCommand.ExecuteAsync(null);
        vm.StatusText = "Ready.";
        return vm;
    }
}
