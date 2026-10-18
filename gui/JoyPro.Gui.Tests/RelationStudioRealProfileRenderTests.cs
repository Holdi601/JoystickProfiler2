using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Relations page with a real profile through the real engine, rendered offscreen at the user's window size
// (2267x1250) and at 1280x800: the open relation with its actions, after a refresh of the relation list (the list
// used to lose the selection and the "how to start" steps were drawn over the actions), an action's aircraft opened by
// a click on the action, and the search "pitch" with a DCS row's aircraft opened. Works on a copy of the profile and an
// own data folder. Runs only when JOYPRO_REL_REAL_OUT names an output folder, JOYPRO_REL_REAL_ENGINE the engine.exe and
// JOYPRO_REL_REAL_PROFILE a .pr0file; JOYPRO_REL_REAL_RELATION picks the relation (default: the first one):
//   set JOYPRO_REL_REAL_OUT=<work folder>\relrender & set JOYPRO_REL_REAL_ENGINE=<repo>\engine\bin\x64\Debug\engine.exe &
//   set JOYPRO_REL_REAL_PROFILE=<copy>.pr0file & dotnet test gui/JoyPro.Gui.Tests --filter RelationStudioRealProfileRenderTests
// Files: relations-real-<step>-<width>.png and report.txt (what each step showed).
[SupportedOSPlatform("windows")]
public sealed class RelationStudioRealProfileRenderTests
{
    [Fact]
    public void RenderRealProfile()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_REL_REAL_OUT");
        string? engine = Environment.GetEnvironmentVariable("JOYPRO_REL_REAL_ENGINE");
        string? source = Environment.GetEnvironmentVariable("JOYPRO_REL_REAL_PROFILE");
        if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(engine) || !File.Exists(engine) || string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return;
        Directory.CreateDirectory(outDir);
        string data = Path.Combine(outDir, "data");
        Directory.CreateDirectory(data);
        string profile = Path.Combine(outDir, "work.pr0file");
        File.Copy(source, profile, overwrite: true);
        string? oldData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", data);
        StringBuilder report = new();
        try
        {
            AvaloniaRenderThread.Run(() =>
            {
                MainWindowViewModel vm = new(new EngineCliClient(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                MainWindow? main = null;
                try
                {
                    AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
                    vm.CurrentStage = UiStage.Relations;
                    string wanted = Environment.GetEnvironmentVariable("JOYPRO_REL_REAL_RELATION") ?? string.Empty;
                    RelationListItem relation = vm.Relations.FirstOrDefault(r => r.Name == wanted) ?? vm.Relations.First();
                    // A window is bound while the relation is picked and the list refreshed, as on screen (the relation
                    // list's selection is bound to it). Every picture is a fresh window: offscreen, a window laid out
                    // once does not follow later changes of what is visible.
                    main = NewWindow(vm, 2267, 1250);
                    vm.SelectedRelation = relation;
                    AvaloniaRenderThread.Wait(SettleAsync(vm, () => vm.RelationNodes.Count > 0 && vm.RelationNodes.Where(n => n.Game == "DCS").All(n => n.HasAircraft)));
                    Describe(report, "open", vm);
                    Render(vm, outDir, "open");

                    // A refresh of the relation list (after any edit, an undo, a save): the relation stays open.
                    AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
                    AvaloniaRenderThread.Pump();
                    AvaloniaRenderThread.Wait(SettleAsync(vm, () => true));
                    Describe(report, "refreshed", vm);
                    main.DataContext = null;
                    main = null;
                    AvaloniaRenderThread.Pump();
                    Render(vm, outDir, "refreshed");

                    // A click on the first DCS action opens its aircraft.
                    RelationNodeItem? node = vm.RelationNodes.FirstOrDefault(n => n.HasAircraft && n.Aircraft!.Total > 1) ?? vm.RelationNodes.FirstOrDefault(n => n.HasAircraft);
                    if (node != null)
                    {
                        vm.ToggleNodeAircraftCommand.Execute(node);
                        AvaloniaRenderThread.Pump();
                        report.AppendLine("node aircraft: " + node.TitlesSummary + " | expanded " + node.Aircraft!.IsExpanded + " | " + node.Aircraft.Badge + " | groups " +
                                          string.Join(", ", node.Aircraft.Visible.Where(i => i.HasGroupHeader).Select(i => i.GroupHeader)));
                        Render(vm, outDir, "node-aircraft");
                        // The aircraft where the id means something else, with their own titles.
                        node.Aircraft.FilterText = "differs";
                        AvaloniaRenderThread.Pump();
                        Render(vm, outDir, "node-aircraft-differs");
                        node.Aircraft.FilterText = string.Empty;
                        node.Aircraft.IsExpanded = false;
                    }

                    // Add actions: "pitch" over every game, then the axes only, a DCS row's aircraft opened.
                    vm.ShowBuilderFindCommand.Execute(null);
                    vm.BuilderSearchText = "Pitch";
                    AvaloniaRenderThread.Wait(SettleAsync(vm, () => vm.BuilderSearchResults.Count > 0));
                    DescribeSearch(report, "search pitch", vm);
                    Render(vm, outDir, "search");
                    BuilderSearchResultItem? row = vm.BuilderSearchResults.FirstOrDefault(r => r.HasAircraft && r.Aircraft!.HasDiffers) ?? vm.BuilderSearchResults.FirstOrDefault(r => r.HasAircraft);
                    if (row != null)
                    {
                        row.Aircraft!.IsExpanded = true;
                        AvaloniaRenderThread.Pump();
                        report.AppendLine("row aircraft: " + row.Title + " | " + row.Aircraft.Badge + " | groups " +
                                          string.Join(", ", row.Aircraft.Visible.Where(i => i.HasGroupHeader).Select(i => i.GroupHeader)));
                        Render(vm, outDir, "search-aircraft");
                        row.Aircraft.IsExpanded = false;
                    }

                    // A new relation (every kind): "pitch" in the simulators only, each game's main control first.
                    vm.StartNewBuilderRelationCommand.Execute(null);
                    vm.BuilderSearchText = "pitch";
                    AvaloniaRenderThread.Wait(SettleAsync(vm, () => vm.BuilderSearchResults.Count > 0 && vm.BuilderSearchGames.Count > 0));
                    foreach (BuilderGameChip chip in vm.BuilderSearchGames.Where(c => c.GameId is "MSFS2020" or "MSFS2024" or "XPlane12" or "FSX"))
                        chip.IsSelected = true;
                    AvaloniaRenderThread.Wait(SettleAsync(vm, () => vm.BuilderSearchResults.All(r => r.Game is "MSFS2020" or "MSFS2024" or "XPlane12" or "FSX")));
                    DescribeSearch(report, "new relation, pitch in the simulators", vm);
                    Render(vm, outDir, "search-sims");
                }
                finally
                {
                    if (main != null)
                        main.DataContext = null;
                    vm.StopGameConfigWatcher();
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
                }
            });
        }
        finally
        {
            File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", oldData);
        }
    }

    private static async Task SettleAsync(MainWindowViewModel vm, Func<bool> condition)
    {
        await Task.Delay(400);
        int idle = 0;
        for (int i = 0; i < 600 && (idle < 10 || !condition()); i++)
        {
            idle = vm.IsBusy || vm.IsBuilderSuggestLoading || vm.IsBuilderSearching ? 0 : idle + 1;
            await Task.Delay(50);
        }
    }

    private static void Layout(MainWindow main, int width, int height)
    {
        Size size = new(width, height);
        Control page = (Control)main.Content!;
        for (int pass = 0; pass < 12; pass++)
        {
            main.Measure(size);
            main.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
    }

    private static MainWindow NewWindow(MainWindowViewModel vm, int width, int height)
    {
        MainWindow main = new() { DataContext = vm, Width = width, Height = height };
        // Offscreen the virtualizing lists do not learn their viewport: plain panels show their rows.
        foreach (ListBox list in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(main).OfType<ListBox>())
        {
            list.AutoScrollToSelectedItem = false;
            list.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel());
        }
        Layout(main, width, height);
        return main;
    }

    private static void Render(MainWindowViewModel vm, string outDir, string step)
    {
        foreach ((int width, int height) in new[] { (2267, 1250), (1280, 800) })
        {
            MainWindow main = NewWindow(vm, width, height);
            Snapshot(main, width, height, Path.Combine(outDir, "relations-real-" + step + "-" + width + ".png"));
            main.DataContext = null;
            AvaloniaRenderThread.Pump();
        }
    }

    // The window's page onto its background (UiDesignRenderTests.RenderWindow, on this one window: a second window
    // bound to the same view model would hold a second selection of the relation list).
    private static void Snapshot(MainWindow main, int width, int height, string file)
    {
        Size size = new(width, height);
        Control page = (Control)main.Content!;
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
        bitmap.Save(file);
    }

    private static void Describe(StringBuilder report, string step, MainWindowViewModel vm)
    {
        report.AppendLine(step + ": selected '" + vm.SelectedRelation?.Name + "' | nodes " + vm.RelationNodes.Count + " | steps shown " + vm.HasNoRelationSelected +
                          " | node list shown " + vm.ShowRelationNodeList + " | mixed " + vm.RelationNodes.Count(n => n.IsMixed));
    }

    private static void DescribeSearch(StringBuilder report, string step, MainWindowViewModel vm)
    {
        report.AppendLine(step + ": " + vm.BuilderSearchResults.Count + " rows");
        foreach (IGrouping<string, BuilderSearchResultItem> game in vm.BuilderSearchResults.GroupBy(r => r.Game))
            report.AppendLine("  " + game.Key + ": " + string.Join(" | ", game.Take(3).Select(r => r.DisplayTitle + " [" + r.MatchLabel + "]")));
    }
}
