using System;
using System.IO;
using System.Collections.Generic;
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

// The Relations list of the real main window's page (laid out in a hidden root, never shown) shows its relations: at first, with the Auto-build wizard open and after it
// closed, after another page, and with a sort set (a user saw an empty list with "Buttons 484" on the filter chips).
[SupportedOSPlatform("windows")]
public sealed class RelationListVisibleTests
{
    private static int Shown(HiddenWindow main)
    {
        main.Settle();
        ListBox list = main.Window.FindControl<ListBox>("RelationsListBox")!;
        Rect view = new(list.TranslatePoint(new Point(0, 0), main.Page)!.Value, list.Bounds.Size);
        return list.GetVisualDescendants().OfType<ListBoxItem>().Count(item =>
            item.IsEffectivelyVisible && item.Opacity > 0 && item.Bounds.Height > 0 && item.TranslatePoint(new Point(0, 0), main.Page) is { } at && view.Intersects(new Rect(at, item.Bounds.Size)));
    }

    [Fact]
    public void RelationsList_ShowsItsRelations_WithTheWizardOpen_AfterPagesAndWithASort()
    {
        AvaloniaRenderThread.Run(() =>
        {
            LocalAiFakeEngine engine = new() { RelationCount = 484 };
            MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = typeof(MainWindowViewModel).Assembly.Location,
                ProfilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "relations-visible.pr0file")
            };
            // Like the app: the window starts on the Start page, the profile loads, then the user opens Relations.
            vm.CurrentStage = UiStage.Start;
            using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 800));
            AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
            Assert.Equal(484, vm.Relations.Count);
            main.Settle();
            vm.CurrentStage = UiStage.Relations;
            List<string> problems = [];
            void Check(string when)
            {
                int shown = Shown(main);
                if (shown < 3)
                    problems.Add(when + ": " + shown + " rows on screen of " + vm.Relations.Count);
            }
            Check("at first");
            main.Snap("relations-first.png");

            vm.ToggleOrganizeCommand.Execute(null);
            Assert.True(vm.IsAnyRelationToolOpen);
            Check("wizard open");
            main.Snap("relations-wizard.png");
            vm.ShowOrganizeByDescriptionCommand.Execute(null);
            Check("wizard, By description");
            vm.CloseRelationToolsCommand.Execute(null);
            Check("wizard closed");

            vm.CurrentStage = UiStage.Binds;
            main.Settle();
            vm.CurrentStage = UiStage.Relations;
            Check("back from Binds");

            // A sort (the tiered sort of the list): newest key first.
            ListSortBarCheck(vm);
            Check("sorted");
            vm.ToggleOrganizeCommand.Execute(null);
            Check("sorted, wizard open");
            vm.CloseRelationToolsCommand.Execute(null);

            // Scrolled down, then a filter makes the list short.
            ListBox list = main.Window.FindControl<ListBox>("RelationsListBox")!;
            list.ScrollIntoView(vm.Relations[^1]);
            main.Settle();
            vm.RelationSearchText = "Relation 00";
            Check("scrolled down, then searched");
            vm.RelationSearchText = string.Empty;
            Check("search cleared");

            // Scrolled to the end, then the list is filled again (a refresh after an engine call: new rows).
            list.ScrollIntoView(vm.Relations[^1]);
            main.Settle();
            vm.ToggleOrganizeCommand.Execute(null);
            AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
            Check("scrolled to the end, refreshed with the wizard open");
            main.Snap("relations-refreshed.png");
            engine.RelationCount = 30;
            AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
            Check("scrolled to the end, refreshed to fewer relations");
            vm.CloseRelationToolsCommand.Execute(null);
            Check("closed after the refresh");
            Assert.True(problems.Count == 0, string.Join("; ", problems));
        });
    }

    // The same with a real big profile and the real engine (opt-in: JOYPRO_JOURNEY_PROFILE names a copy of one, e.g.
    // 500 relations; JOYPRO_WORK_DIR gets the data folder): opened like at a start, the Relations page, the wizard.
    [Fact]
    public void RelationsList_BigRealProfile_ShowsItsRelations_WithTheWizardOpen()
    {
        string? source = Environment.GetEnvironmentVariable("JOYPRO_JOURNEY_PROFILE");
        string? work = Environment.GetEnvironmentVariable("JOYPRO_WORK_DIR");
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "bin")))
            dir = dir.Parent;
        string engine = dir == null ? string.Empty : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source) || string.IsNullOrWhiteSpace(work) || !File.Exists(engine))
            return;
        string folder = Path.Combine(work, "relation-list-big-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string profile = Path.Combine(folder, "big.pr0file");
        File.Copy(source, profile);
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = new(new DataDirEngineClient(Path.Combine(folder, "data")), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = engine
            };
            vm.CurrentStage = UiStage.Start;
            using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(2267, 1250));
            vm.ProfilePath = profile;
            AvaloniaRenderThread.Wait(GuiParityRenderTests.Done(vm.LoadProfileCommand.ExecuteAsync(null)));
            Assert.True(vm.Relations.Count > 50, "relations shown: " + vm.Relations.Count);
            vm.CurrentStage = UiStage.Relations;
            List<string> problems = [];
            void Check(string when)
            {
                int shown = Shown(main);
                if (shown < 3)
                    problems.Add(when + ": " + shown + " rows on screen of " + vm.Relations.Count);
            }
            Check("at first");
            main.Snap("big-relations-first.png");
            vm.ToggleOrganizeCommand.Execute(null);
            Check("wizard open");
            main.Snap("big-relations-wizard.png");
            vm.ShowOrganizeByDescriptionCommand.Execute(null);
            Check("wizard, By description");
            vm.CloseRelationToolsCommand.Execute(null);
            Check("wizard closed");
            Assert.True(problems.Count == 0, string.Join("; ", problems));
        });
        try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void ListSortBarCheck(MainWindowViewModel vm)
    {
        ListSortViewModel sort = vm.RelationSort;
        sort.Add(sort.Options.Last().Key, true);
        sort.Add(sort.Options.First().Key, false);
        Assert.True(sort.HasLevels);
    }
}
