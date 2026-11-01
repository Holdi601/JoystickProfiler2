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

// A tester (2.5.0, DCS, 638 relations, 47 aircraft) waited minutes for the relation editor's Add actions search. Typed
// key by key into the real view (hidden root) with the real engine and its server on a big DCS profile built from the
// development lists (Auto Group over 47 aircraft: ~440 relations, ~6700 actions; JOYPRO_SEARCH_SPEED_PROFILE=<file> takes
// a copy of a real one): every keystroke of "tacan channel xx0x inc" with and without an aircraft filter, through the
// same path as the app (debounce, engine search, the result rows, the open relation's marks and suggestions). Budget per
// keystroke: the results within SearchBudgetMs of the debounce and UI-thread blocks under UiBlockBudgetMs as the median of
// the run, no single keystroke over MaxMs (the tests run at below-normal priority next to whatever the PC does, so single
// keystrokes vary). Measured before the fix (2.5.0): 0.6-1.7 s per result list without a filter, median ~0.7 s (every row
// built at once); after it ~0.1-0.2 s. JOYPRO_SEARCH_SPEED_REPORT=<file> writes the timings.
[SupportedOSPlatform("windows")]
[Collection("ResourceMeasurements")]
public sealed class SearchSpeedTests
{
    // From the debounce's end to the rows of the last key in the list (engine search, parsing, rows, layout).
    private const double SearchBudgetMs = 300;
    private const double UiBlockBudgetMs = 150;
    private const double MaxMs = 1000;

    internal static readonly string[] Planes =
    [
        "A-10C II", "A-10C", "A-4E-C", "AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT", "AJS37", "AV8BNA", "Bf-109K-4", "C-101CC", "CH-47F", "F-14", "F-14_RIO",
        "F-15C", "F-15E", "F-15E_WSO", "F-16C_50", "F-4E-45MC", "F-4E-45MC_WSO", "F-5E-3", "F-86F Sabre", "F4U-1D", "FA-18C_hornet", "FW-190A8", "FW-190D9",
        "I-16", "JF-17", "Ka-50 III", "L-39C", "M-2000C", "MB-339A", "Mi-24P_pilot", "MiG-15bis", "MiG-19P", "MiG-21Bis", "Mirage-F1CE", "MosquitoFBMkVI",
        "OH-58D Pilot", "P-47D-30", "P-51D", "SA342 Pilot", "SpitfireLFMkIX", "UH-1H", "Yak-52", "mi-8mt", "su-25T", "su-27", "su-33"
    ];

    private readonly ITestOutputHelper output;

    public SearchSpeedTests(ITestOutputHelper output) => this.output = output;

    private static string? Engine()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "bin")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return engine != null && File.Exists(engine) && Directory.Exists(Path.Combine(Path.GetDirectoryName(engine)!, "DB", "DCS")) ? engine : null;
    }

    [Fact]
    public void BigDcsProfile_EveryKeystrokeOfTheSearch_StaysWithinBudget()
    {
        string? engine = Engine();
        if (engine == null)
            return;
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "search-speed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string data = Path.Combine(folder, "data");
        string? oldData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", data);
        List<string> report = [];
        List<(string Line, double Shown, double Block)> misses = [];
        try
        {
            string profile = Path.Combine(folder, "big.pr0file");
            string? source = Environment.GetEnvironmentVariable("JOYPRO_SEARCH_SPEED_PROFILE");
            DataDirEngineClient client = new(data);
            if (!string.IsNullOrWhiteSpace(source) && File.Exists(source))
                File.Copy(source!, profile);
            else
            {
                EngineCommandResult built = client.RunAsync(engine, ["--profile", profile, "--status", "false", "relations", "auto-group", "--planes", string.Join("|", Planes)]).GetAwaiter().GetResult();
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
                    Assert.True(vm.Relations.Count >= 300, "the big profile has only " + vm.Relations.Count + " relations");
                    // An open button relation with actions of many aircraft (as the tester's "COMBAT PANEL - COMM 2 - CW").
                    RelationListItem open = vm.Relations.Where(r => !r.IsAxis).OrderByDescending(r => r.ActiveGamePlanes.Count).First();
                    vm.SelectedRelation = open;
                    Settle(vm, window, () => vm.BuilderRelationName == open.Name && vm.RelationNodes.Count > 0);
                    vm.ShowBuilderFindCommand.Execute(null);
                    Settle(vm, window, () => true);
                    // The server builds its word index with its first search (once per server, ~0.2-0.4 s): done here, as
                    // the relation's own name is searched when it opens.
                    vm.BuilderSearchText = "gear";
                    Settle(vm, window, () => vm.BuilderLastSearchedQueryForTests == "gear");
                    foreach (bool filtered in new[] { false, true })
                    {
                        vm.BuilderSearchText = string.Empty;
                        Settle(vm, window, () => true);
                        if (filtered)
                        {
                            AvaloniaRenderThread.Wait(vm.AddBuilderSearchPlaneCommand.ExecuteAsync("F-4E-45MC"));
                            AvaloniaRenderThread.Wait(vm.AddBuilderSearchPlaneCommand.ExecuteAsync("F-4E-45MC_WSO"));
                            Settle(vm, window, () => true);
                        }
                        Type(vm, window, "tacan channel xx0x inc", filtered ? "F-4E filter" : "no filter", report, misses);
                        // The rows in view are really built (the list is virtualized, not empty).
                        Avalonia.Controls.ItemsControl list = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.Page).OfType<Avalonia.Controls.ItemsControl>().First(c => c.Name == "BuilderSearchList");
                        int realized = list.GetRealizedContainers().Count();
                        report.Add((filtered ? "F-4E filter" : "no filter") + ": " + realized + " of " + vm.BuilderSearchRows.Count + " list items built");
                        // (a screen of rows: about 7 at 1920x1080; not all 100 of them)
                        Assert.True(realized >= Math.Min(4, vm.BuilderSearchRows.Count), "only " + realized + " of " + vm.BuilderSearchRows.Count + " search rows are built");
                        Assert.True(vm.BuilderSearchRows.Count < 30 || realized < vm.BuilderSearchRows.Count, "every one of " + realized + " search rows was built (no virtualization)");
                        if (filtered)
                        {
                            BuilderSearchResultItem? inc = vm.BuilderSearchResults.FirstOrDefault(r => r.Title == "TACAN Channel (xx0x) - [Inc]");
                            Assert.True(inc != null, "TACAN Channel (xx0x) - [Inc] is not in the results: " + vm.BuilderSearchStatus);
                        }
                    }
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
            EngineServerPool.Recycle("search speed test done");
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (string line in report)
            output.WriteLine(line);
        string? outFile = Environment.GetEnvironmentVariable("JOYPRO_SEARCH_SPEED_REPORT");
        if (!string.IsNullOrWhiteSpace(outFile))
            File.WriteAllLines(outFile!, report);
        static double Median(IEnumerable<double> values)
        {
            List<double> sorted = values.OrderBy(v => v).ToList();
            return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
        }
        double shownMedian = Median(misses.Where(m => m.Shown >= 0).Select(m => m.Shown));
        double blockMedian = Median(misses.Select(m => m.Block));
        string all = string.Join(Environment.NewLine, misses.Select(m => m.Line));
        Assert.True(shownMedian <= SearchBudgetMs, "median time to the results " + shownMedian.ToString("0", CultureInfo.InvariantCulture) + " ms > " + SearchBudgetMs + " ms:" + Environment.NewLine + all);
        Assert.True(blockMedian <= UiBlockBudgetMs, "median UI-thread block " + blockMedian.ToString("0", CultureInfo.InvariantCulture) + " ms > " + UiBlockBudgetMs + " ms:" + Environment.NewLine + all);
        Assert.True(!misses.Any(m => m.Shown > MaxMs || m.Block > MaxMs), "a keystroke took more than " + MaxMs + " ms:" + Environment.NewLine + all);
    }

    // Types the text key by key (as fast as a person: 120 ms between keys, so the debounce lets most keys go); after
    // every key with a pause (a person waiting for the list) the time until the list shows the key's results.
    private static void Type(MainWindowViewModel vm, HiddenWindow window, string text, string label, List<string> report, List<(string Line, double Shown, double Block)> misses)
    {
        Stopwatch typing = Stopwatch.StartNew();
        double longestAll = 0;
        for (int i = 1; i <= text.Length; i++)
        {
            string typed = text[..i];
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
            vm.BuilderSearchText = typed;
            // Every third key the person waits for the list; the keys between come 120 ms apart.
            bool wait = i % 3 == 0 || i == text.Length;
            double shown = -1;
            if (wait)
            {
                // The debounce (300 ms) is the app's choice, not the search's time.
                AvaloniaRenderThread.Wait(Task.Delay(300));
                Stopwatch result = Stopwatch.StartNew();
                while (result.ElapsedMilliseconds < 120000)
                {
                    AvaloniaRenderThread.Wait(Task.Delay(5));
                    if (!vm.IsBuilderSearching && !vm.IsBusy && vm.BuilderSearchStatus.Length > 0 && LastQuery(vm) == typed.Trim())
                        break;
                }
                shown = result.Elapsed.TotalMilliseconds;
                // The rows laid out (part of what the person waits for).
                AvaloniaRenderThread.Wait(Task.Delay(50));
            }
            else
                AvaloniaRenderThread.Wait(Task.Delay(120));
            stop.Cancel();
            poster.Join();
            AvaloniaRenderThread.Pump();
            longestAll = Math.Max(longestAll, longest);
            string line = label + " '" + typed + "': " + (wait ? "results after " + shown.ToString("0", CultureInfo.InvariantCulture) + " ms, " : string.Empty) +
                          "longest UI-thread block " + longest.ToString("0", CultureInfo.InvariantCulture) + " ms, " + vm.BuilderSearchResults.Count.ToString(CultureInfo.InvariantCulture) + " rows";
            report.Add(line);
            misses.Add((line, wait ? shown : -1, longest));
        }
        report.Add(label + ": typed in " + typing.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms, longest UI-thread block " + longestAll.ToString("0", CultureInfo.InvariantCulture) + " ms");
    }

    private static string LastQuery(MainWindowViewModel vm) => vm.BuilderLastSearchedQueryForTests;

    private static void Settle(MainWindowViewModel vm, HiddenWindow window, Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        int idle = 0;
        while (watch.ElapsedMilliseconds < 60000)
        {
            AvaloniaRenderThread.Wait(Task.Delay(50));
            idle = !vm.IsBusy && !vm.IsBuilderSearching && !vm.IsBuilderSuggestLoading ? idle + 1 : 0;
            if (idle >= 4 && condition())
                break;
        }
        window.Settle();
    }
}
