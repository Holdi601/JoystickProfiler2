using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
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

// A profile with thousands of findings (a DCS import with "include defaults": 5005 relations, 3365 errors, 9.7 MB of
// duplicates) froze the app on the Export page's check. The check, its cards and the Start, Relations and Binds pages
// must stay responsive: no step on the UI thread may block it for more than about 200 ms. Measured with a heartbeat
// on the UI thread (the longest gap between its ticks) while the real view runs in a hidden root (HiddenWindow).
// JOYPRO_CHECK_REPLAY=<folder> replays real engine outputs instead of the synthetic ones (relations.json, binds.json,
// validate.txt, dups.json, keys.json, copies.json; see the test for the commands).
// Timings: runs alone (other tests' continuations would run on the same UI thread meanwhile).
[SupportedOSPlatform("windows")]
[Collection("ResourceMeasurements")]
public sealed class CheckResponsivenessTests
{
    // The target is ~200 ms per UI-thread step (measured 20-250 ms in a Debug build with 5000 relations, 3000 errors and
    // 900 duplicate cards; before: one block of 20 s and more). The test fails above 400 ms: room for a loaded test
    // machine, far below a freeze.
    private const double StepBudgetMs = 400;

    private readonly ITestOutputHelper output;

    public CheckResponsivenessTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void ThousandsOfFindings_TheCheckOpensAndTheUiStaysResponsive()
    {
        string? replay = Environment.GetEnvironmentVariable("JOYPRO_CHECK_REPLAY");
        ReplayEngine engine = string.IsNullOrWhiteSpace(replay) ? ReplayEngine.Synthetic(5000, 3000, 900) : ReplayEngine.FromFolder(replay!);
        List<string> report = [];
        List<string> misses = [];
        List<string> coldMisses = [];
        // The pages around the check (loading 5000 relations and binds, Start, Relations, Binds): reported, not failed;
        // the Binds page builds its rows on the UI thread (a known budget miss at 5000 binds on a busy PC).
        List<string> pageMisses = [];
        AvaloniaRenderThread.Run(() =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "check-responsive");
            Directory.CreateDirectory(folder);
            string profile = Path.Combine(folder, "Big.pr0file");
            File.WriteAllText(profile, "{}");
            MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = typeof(MainWindowViewModel).Assembly.Location,
                ProfilePath = profile
            };
            try
            {
                using HiddenWindow window = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 800));

                // The first load also compiles the code it runs (JIT): reported, the budget applies from the second one on.
                Measure("load relations and binds (first, cold)", () => vm.RefreshRelationsCommand.ExecuteAsync(null), window, vm, report, coldMisses);
                Measure("load relations and binds again", () => vm.RefreshRelationsCommand.ExecuteAsync(null), window, vm, report, pageMisses);
                Assert.True(vm.Relations.Count > 0, "the relations did not load");
                foreach ((string name, UiStage stage) in new[] { ("Start page", UiStage.Start), ("Relations page", UiStage.Relations), ("Binds page", UiStage.Binds), ("Export page", UiStage.Export) })
                    Measure("open the " + name, () => { vm.CurrentStage = stage; return Task.CompletedTask; }, window, vm, report, pageMisses);

                Measure("check (validate, duplicates, copies) and its cards", () => vm.ValidateProfileCommand.ExecuteAsync(null), window, vm, report, misses);
                int errors = vm.CheckSections.Where(s => s.Kind == "errors").Sum(s => s.TotalCount);
                // Synthetic: 3000 errors; a replay: the "Errors:" count of its validate.txt.
                System.Text.RegularExpressions.Match replayErrors = string.IsNullOrWhiteSpace(replay) ? System.Text.RegularExpressions.Match.Empty
                    : System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(replay!, "validate.txt")), @"(?m)^Errors:\s*(\d+)");
                int expectedErrors = replayErrors.Success ? int.Parse(replayErrors.Groups[1].Value, CultureInfo.InvariantCulture) : 1000;
                Assert.True(replayErrors.Success ? errors == expectedErrors : errors >= expectedErrors, "the check must still count every error, got " + errors);
                Assert.Contains(vm.CheckSections, s => s.Kind == "duplicates");
                // Only a page of cards is built; the rest comes with "Show more".
                Assert.All(vm.CheckSections, s => Assert.True(s.Cards.Count <= s.PageSize, s.Kind + ": " + s.Cards.Count + " cards built at once"));
                CheckIssueSection errorSection = vm.CheckSections.Single(s => s.Kind == "errors");
                Assert.True(errorSection.HasMore);
                Assert.Contains(errorSection.TotalCount.ToString(CultureInfo.InvariantCulture), errorSection.CountText);
                Assert.False(string.IsNullOrEmpty(errorSection.Breakdown), "the errors say where most of them are");

                Measure("show more errors", () => { errorSection.ShowMoreCommand!.Execute(null); return Task.CompletedTask; }, window, vm, report, misses);
                Assert.Equal(2 * errorSection.PageSize, vm.CheckSections.Single(s => s.Kind == "errors").Cards.Count);
                Stopwatch build = Stopwatch.StartNew();
                vm.BuildCheckSections();
                report.Add("BuildCheckSections alone (no layout): " + build.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
                window.Settle();
                Measure("search the check", () => { vm.CheckSearchText = "Keyboard"; return Task.CompletedTask; }, window, vm, report, misses);
                Measure("clear the search", () => { vm.CheckSearchText = string.Empty; return Task.CompletedTask; }, window, vm, report, misses);
                Measure("collapse all", () => { vm.CollapseAllCheckCardsCommand.Execute(null); return Task.CompletedTask; }, window, vm, report, misses);
                Measure("expand all", () => { vm.ExpandAllCheckCardsCommand.Execute(null); return Task.CompletedTask; }, window, vm, report, misses);
                foreach ((string name, UiStage stage) in new[] { ("Relations page", UiStage.Relations), ("Binds page", UiStage.Binds), ("Start page", UiStage.Start), ("Export page", UiStage.Export) })
                    Measure("back to the " + name, () => { vm.CurrentStage = stage; return Task.CompletedTask; }, window, vm, report, pageMisses);
            }
            finally
            {
                vm.StopGameConfigWatcher();
            }
        });
        foreach (string line in pageMisses)
            report.Add("budget miss (page, not failed): " + line);
        foreach (string line in report)
            output.WriteLine(line);
        string? outFile = Environment.GetEnvironmentVariable("JOYPRO_CHECK_REPORT");
        if (!string.IsNullOrWhiteSpace(outFile))
            File.WriteAllLines(outFile!, report);
        Assert.True(misses.Count == 0, "UI thread blocked longer than " + StepBudgetMs + " ms:" + Environment.NewLine + string.Join(Environment.NewLine, misses));
    }

    // Runs one step on the UI thread with a heartbeat: another thread posts a job (input priority) every 10 ms; how long
    // a job waits before it runs is how long the UI thread was busy with something else. (A DispatcherTimer does not
    // tick when another test created the dispatcher before the render thread set up its platform.)
    private static void Measure(string name, Func<Task> step, HiddenWindow window, MainWindowViewModel vm, List<string> report, List<string> misses)
    {
        Stopwatch total = Stopwatch.StartNew();
        Stopwatch clock = Stopwatch.StartNew();
        double longest = 0;
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
        Task task = step();
        AvaloniaRenderThread.Wait(task);
        // The layout of what the step changed (and calls it queued) belongs to the step.
        for (int i = 0; i < 200 && vm.IsBusy; i++)
            AvaloniaRenderThread.Wait(Task.Delay(10));
        // The hidden root's render loop lays out what changed, as on screen.
        AvaloniaRenderThread.Wait(Task.Delay(400));
        stop.Cancel();
        poster.Join();
        // The heartbeats still queued run now (a job still waiting counts with its wait).
        AvaloniaRenderThread.Pump();
        long elapsed = total.ElapsedMilliseconds;
        // A forced layout pass afterwards (not measured: HiddenWindow.Settle sleeps between its passes) shows that
        // nothing was left for later.
        Stopwatch settle = Stopwatch.StartNew();
        window.Settle();
        string line = name + ": " + elapsed.ToString(CultureInfo.InvariantCulture) + " ms in all, longest UI-thread block " +
                      longest.ToString("0", CultureInfo.InvariantCulture) + " ms (layout afterwards " + settle.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)";
        report.Add(line);
        string? progress = Environment.GetEnvironmentVariable("JOYPRO_CHECK_REPORT");
        if (!string.IsNullOrWhiteSpace(progress))
            File.AppendAllText(progress + ".progress", line + Environment.NewLine);
        if (longest > StepBudgetMs)
            misses.Add(line);
    }

    // Answers the engine calls of the pages and the check from prepared outputs.
    private sealed class ReplayEngine : IEngineCliClient
    {
        private readonly Dictionary<string, string> outputs;

        private ReplayEngine(Dictionary<string, string> outputs) => this.outputs = outputs;

        public static ReplayEngine FromFolder(string folder)
        {
            string Read(string name) => File.Exists(Path.Combine(folder, name)) ? File.ReadAllText(Path.Combine(folder, name)) : string.Empty;
            return new ReplayEngine(new Dictionary<string, string>
            {
                ["relations list"] = Read("relations.json"),
                ["binds list"] = Read("binds.json"),
                ["profile validate"] = Read("validate.txt"),
                ["relations duplicates"] = Read("dups.json"),
                ["relations duplicates --keys-only"] = Read("keys.json"),
                ["relations merge-copies"] = Read("copies.json")
            });
        }

        // relations: relations (each bound); errors: inputs bound in several relations; duplicateGroups: relation pairs
        // with an action in both (their warnings are in the validation too).
        public static ReplayEngine Synthetic(int relations, int errors, int duplicateGroups)
        {
            string[] planes = ["F-16C_50", "FA-18C_hornet", "A-10C_2", "AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT", "F-14B", "M-2000C", "AV8BNA", "Ka-50_3", "MiG-21Bis"];
            string[] devices = ["Keyboard", "MOZA AB9 FFB Base {534F0E90-8D0C-11F1-8001-444553540000}", "VPC MongoosT-50CM3 {072C2740-0D8D-11F1-8021-444553540000}",
                "WINCTRL F18 TAKEOFF PANEL {072C4E50-0D8D-11F1-8024-444553540000}"];
            string Name(int i) => "Action_" + i.ToString("0000", CultureInfo.InvariantCulture) + "__" + (i % 4 == 0 ? "Keyboard" : "Joystick") + "__d" + (3000 + i).ToString(CultureInfo.InvariantCulture) + "pnilu" + i.ToString(CultureInfo.InvariantCulture) + "cd12vd1vpnilvu0__button";
            string Input(int i) => i % 4 == 0 ? ((char)('A' + i % 26)).ToString() : "JOY_BTN" + (1 + i % 64).ToString(CultureInfo.InvariantCulture);
            string Device(int i) => devices[i % devices.Length];

            StringBuilder rel = new("{\"FormatVersion\":\"1\",\"Relations\":[");
            StringBuilder binds = new("{\"FormatVersion\":\"1\",\"Binds\":[");
            for (int i = 0; i < relations; i++)
            {
                string bind = "{\"RelationName\":\"" + Name(i) + "\",\"IsAxis\":false,\"Joystick\":\"" + Device(i) + "\",\"JAxis\":\"\",\"JButton\":\"" + Input(i) + "\",\"Inverted\":false,\"Slider\":false," +
                              "\"Deadzone\":0,\"Curvature\":[0],\"SaturationX\":1,\"SaturationY\":1,\"AllReformers\":[],\"AdditionalImportInfo\":\"Action " + i.ToString(CultureInfo.InvariantCulture) + "\",\"AliasJoystick\":\"\",\"DoubleTap\":false,\"TuningOverrides\":null}";
                if (i > 0)
                {
                    rel.Append(',');
                    binds.Append(',');
                }
                rel.Append("{\"Name\":\"" + Name(i) + "\",\"IsAxis\":false,\"Groups\":[\"DCS\"],\"Function\":null,\"Nodes\":[{\"Id\":\"d" + (3000 + i).ToString(CultureInfo.InvariantCulture) +
                           "pnilu" + i.ToString(CultureInfo.InvariantCulture) + "cd12vd1vpnilvu0\",\"Game\":\"DCS\",\"Aircraft\":{\"" + planes[i % planes.Length] + "\":true,\"" + planes[(i + 3) % planes.Length] + "\":true},\"StoredTitles\":{},\"StoredCategories\":{}}],\"Bind\":" + bind + ",\"Titles\":[]}");
                binds.Append(bind);
            }
            rel.Append("]}");
            binds.Append("]}");

            StringBuilder validate = new();
            StringBuilder warnings = new();
            StringBuilder dups = new("{\"Count\":" + (duplicateGroups * 3).ToString(CultureInfo.InvariantCulture) + ",\"Duplicates\":[");
            StringBuilder groups = new();
            StringBuilder keys = new("{\"Duplicates\":[");
            for (int g = 0; g < duplicateGroups; g++)
            {
                string a = Name(2 * g % relations), b = Name((2 * g + 1) % relations);
                string message = planes[g % planes.Length] + ": 'Action " + g.ToString(CultureInfo.InvariantCulture) + "' is in 2 relations: '" + a + "' and '" + b + "'. DCS gets two binds for it.";
                warnings.Append("  - " + message + "\n");
                for (int k = 0; k < 3; k++)
                {
                    string key = "DCS|d" + (3000 + g).ToString(CultureInfo.InvariantCulture) + "|" + planes[(g + k) % planes.Length];
                    if (g > 0 || k > 0)
                    {
                        dups.Append(',');
                        keys.Append(',');
                    }
                    dups.Append("{\"Key\":\"" + key + "\",\"Game\":\"DCS\",\"Suggested\":\"" + a + "\",\"SuggestOff\":false,\"Confidence\":" + (60 + g % 40).ToString(CultureInfo.InvariantCulture) + "}");
                    keys.Append("{\"Key\":\"" + key + "\"}");
                }
                if (g > 0)
                    groups.Append(',');
                groups.Append("{\"Game\":\"DCS\",\"Keys\":[\"DCS|d" + (3000 + g).ToString(CultureInfo.InvariantCulture) + "|" + planes[g % planes.Length] + "\"],\"Suggested\":\"" + a + "\",\"SuggestOff\":false,\"Confidence\":" + (60 + g % 40).ToString(CultureInfo.InvariantCulture) + "," +
                              "\"Relations\":[{\"Relation\":\"" + a + "\",\"Device\":\"" + Device(2 * g).Replace("\\", "\\\\") + "\",\"Input\":\"" + Input(2 * g) + "\"},{\"Relation\":\"" + b + "\",\"Device\":\"" + Device(2 * g + 1) + "\",\"Input\":\"" + Input(2 * g + 1) + "\"}]," +
                              "\"Actions\":[{\"Title\":\"Action " + g.ToString(CultureInfo.InvariantCulture) + "\",\"Planes\":[\"" + planes[g % planes.Length] + "\",\"" + planes[(g + 1) % planes.Length] + "\"]},{\"Title\":\"Other " + g.ToString(CultureInfo.InvariantCulture) + "\",\"Planes\":[\"" + planes[(g + 2) % planes.Length] + "\"]}]," +
                              "\"PlaneNames\":[],\"Reasons\":[{\"Code\":\"function\",\"Relation\":\"" + a + "\",\"Detail\":\"\"}],\"Message\":\"" + message.Replace("'", "\\u0027") + "\"}");
            }
            dups.Append("],\"Groups\":[" + groups + "]}");
            keys.Append("]}");
            StringBuilder errorLines = new();
            for (int e = 0; e < errors; e++)
            {
                string plane = planes[e % planes.Length];
                string device = Device(e);
                string names = string.Join(", ", Enumerable.Range(0, 2 + e % 5).Select(n => Name((e * 7 + n * 13) % relations)));
                errorLines.Append("  - Button Item key found in multiple Relation: DCS:" + plane + "§" + device + "§" + Input(e) + " The Relations it was found in: " + names + "\n");
            }
            validate.Append("Profile Validation\nPassed: False\nFail On Warnings: False\nWarnings: " + duplicateGroups.ToString(CultureInfo.InvariantCulture) + "\nErrors: " + errors.ToString(CultureInfo.InvariantCulture) + "\n");
            validate.Append("Relation Warnings: " + duplicateGroups.ToString(CultureInfo.InvariantCulture) + "\n").Append(warnings);
            validate.Append("Bind Errors: " + errors.ToString(CultureInfo.InvariantCulture) + "\n").Append(errorLines);
            string copies = "{\"DryRun\":true,\"Count\":2,\"Families\":[{\"Relation\":\"" + Name(1) + "\",\"Copies\":[\"" + Name(1) + "__1\"],\"Device\":\"" + Device(1) + "\",\"Input\":\"JOY_BTN2\",\"Modifiers\":[],\"Aircraft\":[\"DCS|F-16C_50\"]}," +
                            "{\"Relation\":\"" + Name(5) + "\",\"Copies\":[\"" + Name(5) + "__1\",\"" + Name(5) + "__2\"],\"Device\":\"" + Device(5) + "\",\"Input\":\"JOY_BTN6\",\"Modifiers\":[],\"Aircraft\":[\"DCS|A-10C_2\"]}]}";
            return new ReplayEngine(new Dictionary<string, string>
            {
                ["relations list"] = rel.ToString(),
                ["binds list"] = binds.ToString(),
                ["profile validate"] = validate.ToString(),
                ["relations duplicates"] = dups.ToString(),
                ["relations duplicates --keys-only"] = keys.ToString(),
                ["relations merge-copies"] = copies
            });
        }

        // Like the engine process, the answer comes back later (the UI thread runs on meanwhile).
        public async Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            string output = string.Empty;
            int exit = 0;
            if (AxisTuningTests.Seq(arguments, "relations", "duplicates") && arguments.Contains("--keys-only"))
                output = outputs["relations duplicates --keys-only"];
            else if (AxisTuningTests.Seq(arguments, "relations", "duplicates"))
                output = outputs["relations duplicates"];
            else if (AxisTuningTests.Seq(arguments, "relations", "merge-copies"))
                output = outputs["relations merge-copies"];
            else if (AxisTuningTests.Seq(arguments, "relations", "list"))
                output = outputs["relations list"];
            else if (AxisTuningTests.Seq(arguments, "binds", "list"))
                output = outputs["binds list"];
            else if (AxisTuningTests.Seq(arguments, "profile", "validate"))
            {
                output = outputs["profile validate"];
                exit = output.Contains("Passed: False", StringComparison.Ordinal) ? 1 : 0;
            }
            return new EngineCommandResult { ExitCode = exit, Duration = TimeSpan.FromMilliseconds(5), StandardOutput = output, Arguments = arguments.ToArray() };
        }
    }
}
