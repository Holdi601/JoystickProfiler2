using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services.Help;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Gui.Views.Controls;
using Xunit;
using Xunit.Abstractions;

namespace JoyPro.Gui.Tests;

// Every step of every tour points at a control that shows (docs/ui/help-and-tours.md): walked in a laid-out hidden
// window in three states (an empty profile, a profile with binds in several games and the "Your games already have
// binds" card, easy words). A step whose control is not there in that state points at its alternative or is left out;
// only the cards that point at nothing on purpose ("none", no target) stand in the middle.
[SupportedOSPlatform("windows")]
public sealed class TourTargetWalkTests(ITestOutputHelper output)
{
    private static List<AutoSetupGameItem> GamesWithBinds() =>
    [
        new("DCS", "DCS World", "profile", 0, 412),
        new("IL2Game", "IL-2 Sturmovik: Great Battles", "profile", 0, 57),
        new("WarThunder", "War Thunder", "profile", 0, 88)
    ];

    private static List<AutoSetupGameItem> GamesWithoutBinds() =>
    [
        new("WarThunder", "War Thunder", "empty", 0, 0),
        new("IL2Game", "IL-2 Sturmovik: Great Battles", "empty", 0, 0)
    ];

    [Fact]
    public void EveryTourStep_HighlightsAShownControl_EmptyProfile() => Walk("empty", vm => vm.ApplyAutoSetupStatus(GamesWithoutBinds()), empty: true);

    // No profile and the look at the games not done yet: no setup card at all.
    [Fact]
    public void EveryTourStep_HighlightsAShownControl_EmptyProfileBeforeTheLook() => Walk("empty-nocard", _ => { }, empty: true);

    [Fact]
    public void EveryTourStep_HighlightsAShownControl_ProfileWithBinds() => Walk("binds", vm => vm.ApplyAutoSetupStatus(GamesWithBinds()));

    [Fact]
    public void EveryTourStep_HighlightsAShownControl_EasyWords() => Walk("easy", vm =>
    {
        vm.ApplyAutoSetupStatus(GamesWithBinds());
        vm.EasyWords = true;
    });

    // The Start page tour in the owner's state: step 3 points at "Fill the gaps..." and says what it does.
    [Fact]
    public void StartTour_WithBindsEverywhere_PointsAtFillTheGaps()
    {
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                vm.CurrentStage = UiStage.Start;
                vm.ApplyAutoSetupStatus(GamesWithBinds());
                using HiddenWindow window = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 800));
                HelpLayer layer = window.Page.GetVisualDescendants().OfType<HelpLayer>().Single();
                vm.StartTour("page:start", everyFeature: true, step: 2);
                Settle(window, layer);
                Assert.Equal("Set up automatically", vm.CurrentTourStep!.Title);
                Assert.IsType<Button>(layer.TourTarget);
                Assert.Equal("FillAutoSetupGapsCommand", HelpResolver.CommandName((Button)layer.TourTarget!));
                Assert.Contains("Fill the gaps", vm.TourText);
                Assert.True(Outline(layer).IsVisible);
                Shot(window, "start-tour-step3-binds.png");
                vm.EndTourCommand.Execute(null);
            }
            finally
            {
                vm.StopGameConfigWatcher();
            }
        });
    }

    private void Walk(string state, Action<MainWindowViewModel> prepare, bool empty = false)
    {
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = empty
                ? new(new UiDesignRenderTests.DesignFakeEngine { Empty = true }, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
                {
                    EnginePath = typeof(MainWindowViewModel).Assembly.Location
                }
                : AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            if (empty)
            {
                AvaloniaRenderThread.Wait(vm.RefreshGameCardsCommand.ExecuteAsync(null));
                AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
                AvaloniaRenderThread.Wait(vm.RefreshBindsCommand.ExecuteAsync(null));
            }
            List<string> failures = [];
            List<string> leftOut = [];
            try
            {
                vm.CurrentStage = UiStage.Start;
                prepare(vm);
                using HiddenWindow window = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 800));
                HelpLayer layer = window.Page.GetVisualDescendants().OfType<HelpLayer>().Single();
                Rect windowRect = new(layer.Bounds.Size);
                IEnumerable<TourDefinition> tours = TourCatalog.Paths.Concat(TourCatalog.PageTourPages.Select(p => TourCatalog.PageTour(p)!));
                int checkedSteps = 0;
                foreach (TourDefinition tour in tours)
                {
                    IReadOnlyList<TourStep> steps = tour.StepsFor(true);
                    for (int i = 0; i < steps.Count; i++)
                    {
                        vm.CurrentStage = UiStage.Start;
                        vm.StartTour(tour.Id, everyFeature: true, step: i);
                        Settle(window, layer);
                        // A step left out in this state (its control is not there): the tour went on or ended.
                        if (vm.CurrentTourStep is not { } step || vm.TourStepIndex != i)
                        {
                            // (a step with its own condition, or one that waits for a pick already made, goes on by itself)
                            if (steps[i].SkipIf.Length == 0 && steps[i].WaitFor.Length == 0)
                                leftOut.Add($"{tour.Id} step {i + 1} \"{steps[i].Title}\"");
                            vm.EndTourCommand.Execute(null);
                            continue;
                        }
                        checkedSteps++;
                        string where = $"{state}: {tour.Id} step {i + 1} \"{step.Title}\" ({vm.TourTargetKey})";
                        if (vm.TourTargetKey is "" or "none")
                        {
                            if (step.Target is not ("" or "none"))
                                failures.Add(where + ": points at nothing");
                            vm.EndTourCommand.Execute(null);
                            continue;
                        }
                        Control? target = layer.TourTarget;
                        Border outline = Outline(layer);
                        if (target == null)
                            failures.Add(where + ": no control found");
                        else if (!target.IsEffectivelyVisible || target.Bounds.Width <= 0 || target.Bounds.Height <= 0)
                            failures.Add(where + ": control not shown");
                        else if (!outline.IsVisible)
                            failures.Add(where + ": control outside the window (not scrolled into view)");
                        else
                        {
                            Rect drawn = new(Canvas.GetLeft(outline), Canvas.GetTop(outline), outline.Width, outline.Height);
                            if (!windowRect.Intersects(drawn) || drawn.Width <= 0 || drawn.Height <= 0)
                                failures.Add(where + ": highlight outside the window");
                        }
                        if (tour.Id == "page:start")
                            Shot(window, $"{state}-start-{i + 1}.png");
                        vm.EndTourCommand.Execute(null);
                    }
                }
                output.WriteLine($"{state}: {checkedSteps} steps checked; left out: {string.Join(", ", leftOut)}");
                // With binds everywhere every step has its control (or its alternative): none is left out.
                if (!empty)
                    Assert.True(leftOut.Count == 0, state + ": left out: " + string.Join(", ", leftOut));
                Assert.True(checkedSteps > 40, state + ": too few steps walked");
            }
            finally
            {
                vm.StopGameConfigWatcher();
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        });
    }

    private static Border Outline(HelpLayer layer) => layer.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TourOutline");

    // The step's page, its control found (the layer looks once the page has its layout, and again for about 0.4 s before
    // it leaves the step out), scrolled into view, placed.
    private static void Settle(HiddenWindow window, HelpLayer layer)
    {
        for (int i = 0; i < 12; i++)
        {
            window.Settle();
            layer.Place();
            if (i >= 2 && layer.TourTarget != null && Outline(layer).IsVisible)
                break;
        }
        window.Settle();
        layer.Place();
    }

    // Looked at by eye (JOYPRO_TOUR_WALK_OUT); nothing without it.
    private static void Shot(HiddenWindow window, string name)
    {
        string? folder = Environment.GetEnvironmentVariable("JOYPRO_TOUR_WALK_OUT");
        if (string.IsNullOrWhiteSpace(folder))
            return;
        Directory.CreateDirectory(folder);
        using RenderTargetBitmap bitmap = window.Render();
        bitmap.Save(Path.Combine(folder, name));
    }
}
