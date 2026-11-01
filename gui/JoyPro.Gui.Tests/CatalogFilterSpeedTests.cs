using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;
using Xunit.Abstractions;

namespace JoyPro.Gui.Tests;

// A tester (2.5.0, DCS, 638 relations, 47 aircraft) typed into the filter of the relation editor's catalog (Browse) with
// the F-4E-45MC open (1635 actions in 67 categories, 3561 rows with the actions that are in several categories) and the
// window did not answer for ten minutes until he ended JoyPro: the first key opened every category with a match and
// built every one of their rows at once (each with its tick box, chips and aircraft selector). Typed key by key into the
// real view (hidden root) with the real engine on a big DCS profile built from the development lists (Auto Group over 47
// aircraft; JOYPRO_CATALOG_SPEED_PROFILE=<file> takes a copy of a real one), then "Open all": the UI thread must answer
// within the budget and the list builds only the rows in view. JOYPRO_CATALOG_SPEED_REPORT=<file> writes the timings.
[SupportedOSPlatform("windows")]
[Collection("ResourceMeasurements")]
public sealed class CatalogFilterSpeedTests
{
    private const double UiBlockBudgetMs = 150;
    private const double MaxMs = 1000;

    private readonly ITestOutputHelper output;

    public CatalogFilterSpeedTests(ITestOutputHelper output) => this.output = output;

    private static string? Engine()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "bin")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return engine != null && File.Exists(engine) && File.Exists(Path.Combine(Path.GetDirectoryName(engine)!, "DB", "DCS", "F-4E-45MC.html")) ? engine : null;
    }

    [Fact]
    public void BigDcsCatalog_TypingInTheFilterAndOpenAll_KeepTheWindowAnswering()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "catalog-speed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string data = Path.Combine(folder, "data");
        string? oldData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", data);
        List<string> report = [];
        List<(string Line, double Block)> keys = [];
        try
        {
            string profile = Path.Combine(folder, "big.pr0file");
            string? source = Environment.GetEnvironmentVariable("JOYPRO_CATALOG_SPEED_PROFILE");
            DataDirEngineClient client = new(data);
            if (!string.IsNullOrWhiteSpace(source) && File.Exists(source))
                File.Copy(source!, profile);
            else
            {
                EngineCommandResult built = client.RunAsync(engine, ["--profile", profile, "--status", "false", "relations", "auto-group", "--planes", string.Join("|", SearchSpeedTests.Planes)]).GetAwaiter().GetResult();
                Assert.True(built.ExitCode == 0, built.StandardError);
            }
            AvaloniaRenderThread.Run(() =>
            {
                MainWindowViewModel vm = new(client, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
                try
                {
                    using HiddenWindow window = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1920, 1080));
                    AvaloniaRenderThread.Wait(EngineServerPool.PrewarmAsync(engine));
                    AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
                    vm.CurrentStage = UiStage.Relations;
                    report.Add("relations: " + vm.Relations.Count.ToString(CultureInfo.InvariantCulture));
                    // An open button relation (the tester's "COMBAT PANEL - COMM 2 - CW"): the catalog lists buttons.
                    RelationListItem open = vm.Relations.Where(r => !r.IsAxis).OrderByDescending(r => r.ActiveGamePlanes.Count).First();
                    vm.SelectedRelation = open;
                    Settle(vm, window, () => vm.BuilderRelationName == open.Name && vm.RelationNodes.Count > 0);
                    AvaloniaRenderThread.Wait(vm.OpenCatalogCommand.ExecuteAsync(null));
                    AvaloniaRenderThread.Wait(vm.SelectCatalogAsync("DCS", "F-4E-45MC"));
                    Settle(vm, window, () => !vm.IsCatalogLoading && vm.CatalogRowItems.Count > 0);
                    report.Add("catalog: " + vm.CatalogStatus);
                    Assert.True(vm.CatalogRowItems.Count >= 1000, "the F-4E catalog has only " + vm.CatalogRowItems.Count + " rows: " + vm.CatalogStatus);

                    foreach (string typed in Prefixes("tacan"))
                        keys.Add(Measure(window, "filter '" + typed + "'", () => vm.CatalogFilterText = typed, report, () => Shown(vm)));
                    // The engine's meaning search for the words (after a pause) shows its rows too.
                    AvaloniaRenderThread.Wait(Task.Delay(MainWindowViewModel.CatalogMeaningDelayMs + 200));
                    Settle(vm, window, () => true);
                    Assert.Contains(vm.CatalogCategories.Where(c => c.IsVisible).SelectMany(c => c.VisibleRows), r => r.Row.Title == "TACAN Channel (xx0x) - [Inc]");
                    foreach (string typed in Prefixes("tacan").Reverse().Skip(1).Append(string.Empty))
                        keys.Add(Measure(window, "filter '" + typed + "'", () => vm.CatalogFilterText = typed, report, () => Shown(vm)));
                    keys.Add(Measure(window, "Open all", () => vm.ExpandAllCatalogCommand.Execute(null), report, () => Shown(vm)));
                    int entries = vm.CatalogCategories.Count + vm.CatalogCategories.Sum(c => c.VisibleRows.Count);
                    Avalonia.Controls.ItemsControl list = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.Page).OfType<Avalonia.Controls.ItemsControl>().First(c => c.Name == "CatalogList");
                    int realized = list.GetRealizedContainers().Count();
                    report.Add("open all: " + realized + " of " + entries + " list items built");
                    Assert.True(realized >= 4, "only " + realized + " catalog items are built");
                    Assert.True(realized < entries / 4, realized + " of " + entries + " catalog items were built (no virtualization)");
                    keys.Add(Measure(window, "Close all", () => vm.CollapseAllCatalogCommand.Execute(null), report, () => Shown(vm)));
                    keys.Add(Measure(window, "Open all again + filter 'gear'", () => { vm.ExpandAllCatalogCommand.Execute(null); vm.CatalogFilterText = "gear"; }, report, () => Shown(vm)));
                }
                finally
                {
                    vm.StopGameConfigWatcher();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", oldData);
            EngineServerPool.Recycle("catalog speed test done");
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (string line in report)
            output.WriteLine(line);
        string? outFile = Environment.GetEnvironmentVariable("JOYPRO_CATALOG_SPEED_REPORT");
        if (!string.IsNullOrWhiteSpace(outFile))
            File.WriteAllLines(outFile!, report);
        List<double> sorted = keys.Select(k => k.Block).OrderBy(v => v).ToList();
        double median = sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
        string all = string.Join(Environment.NewLine, keys.Select(k => k.Line));
        Assert.True(median <= UiBlockBudgetMs, "median UI-thread block " + median.ToString("0", CultureInfo.InvariantCulture) + " ms > " + UiBlockBudgetMs + " ms:" + Environment.NewLine + all);
        Assert.True(!keys.Any(k => k.Block > MaxMs), "a step blocked the window for more than " + MaxMs + " ms:" + Environment.NewLine + all);
    }

    private static IEnumerable<string> Prefixes(string text) => Enumerable.Range(1, text.Length).Select(i => text[..i]);

    private static int Shown(MainWindowViewModel vm) => vm.CatalogCategories.Where(c => c.IsVisible).Sum(c => c.VisibleRows.Count);

    // One step (a key, a button) as a person does it: the change, then the layout and render of the window; the longest
    // time the UI thread did not answer a heartbeat posted every 10 ms at input priority.
    private static (string Line, double Block) Measure(HiddenWindow window, string label, Action step, List<string> report, Func<int> shown)
    {
        double longest = 0;
        Stopwatch clock = Stopwatch.StartNew();
        using CancellationTokenSource stop = new();
        Thread poster = new(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                double posted = clock.Elapsed.TotalMilliseconds;
                Dispatcher.UIThread.Post(() => longest = Math.Max(longest, clock.Elapsed.TotalMilliseconds - posted), DispatcherPriority.Input);
                Thread.Sleep(10);
            }
        }) { IsBackground = true, Name = "UI heartbeat" };
        poster.Start();
        // The change and the layout pass it causes (what blocks the window before it can answer the next key).
        Stopwatch took = Stopwatch.StartNew();
        step();
        window.Page.UpdateLayout();
        double stepMs = took.Elapsed.TotalMilliseconds;
        AvaloniaRenderThread.Wait(Task.Delay(120));
        stop.Cancel();
        poster.Join();
        AvaloniaRenderThread.Pump();
        // (settled outside the measurement: HiddenWindow.Settle waits between its passes)
        window.Settle();
        longest = Math.Max(longest, stepMs);
        string line = label + ": step and layout " + stepMs.ToString("0", CultureInfo.InvariantCulture) + " ms, longest UI-thread block " +
                      longest.ToString("0", CultureInfo.InvariantCulture) + " ms, " + shown().ToString(CultureInfo.InvariantCulture) + " rows shown";
        report.Add(line);
        return (line, longest);
    }

    private static void Settle(MainWindowViewModel vm, HiddenWindow window, Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        int idle = 0;
        while (watch.ElapsedMilliseconds < 60000)
        {
            AvaloniaRenderThread.Wait(Task.Delay(50));
            idle = !vm.IsBusy && !vm.IsCatalogLoading && !vm.IsBuilderSuggestLoading ? idle + 1 : 0;
            if (idle >= 4 && condition())
                break;
        }
        window.Settle();
    }
}
