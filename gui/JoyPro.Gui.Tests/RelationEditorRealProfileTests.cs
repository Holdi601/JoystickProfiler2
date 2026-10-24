using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The relation editor on a big real profile through the real engine (opt-in): the open relation's function chip and
// suggestions, and how long adding many actions and saving take. Works on a copy of the profile. Runs only when
// JOYPRO_RELBUG_OUT names an output folder, JOYPRO_RELBUG_ENGINE the engine.exe and JOYPRO_RELBUG_PROFILE a .pr0file;
// JOYPRO_RELBUG_DATA is the data folder (JOYPRO_DATA_DIR, default <out>\data); JOYPRO_RELBUG_RELATION the relation
// (default Pitch). Writes report.txt into the output folder.
[SupportedOSPlatform("windows")]
public sealed class RelationEditorRealProfileTests
{
    // JOYPRO_RELBUG_WINDOW=1: the same with the real main window's page (never shown, on Avalonia's UI thread), with the longest
    // time the UI thread did not answer per step ("UI blocked").
    [Fact]
    public void OpenRelationKeepsFunctionAndSuggestions()
    {
        if (Environment.GetEnvironmentVariable("JOYPRO_RELBUG_WINDOW") == "1")
            AvaloniaRenderThread.Run(() => AvaloniaRenderThread.Wait(ScenarioAsync(window: true)));
        else
            ScenarioAsync(window: false).GetAwaiter().GetResult();
    }

    private static async Task ScenarioAsync(bool window)
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_RELBUG_OUT");
        string? engine = Environment.GetEnvironmentVariable("JOYPRO_RELBUG_ENGINE");
        string? source = Environment.GetEnvironmentVariable("JOYPRO_RELBUG_PROFILE");
        if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(engine) || !File.Exists(engine) || string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return;
        Directory.CreateDirectory(outDir);
        string data = Environment.GetEnvironmentVariable("JOYPRO_RELBUG_DATA") ?? Path.Combine(outDir, "data");
        Directory.CreateDirectory(data);
        string profile = Path.Combine(outDir, "work.pr0file");
        File.Copy(source, profile, overwrite: true);
        string relationName = Environment.GetEnvironmentVariable("JOYPRO_RELBUG_RELATION") ?? "Pitch";
        string? oldData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", data);
        StringBuilder report = new();
        LoggingEngineClient client = new() { DumpFolder = outDir };
        MainWindowViewModel vm = new(client, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
        HiddenWindow? main = null;
        FreezeMeter? freeze = null;
        try
        {
            if (window)
            {
                vm.CurrentStage = UiStage.Relations;
                main = HiddenWindow.Create(new JoyPro.Gui.Views.MainWindow { DataContext = vm }, new Avalonia.Size(2267, 1250));
                freeze = new FreezeMeter();
            }
            Stopwatch watch = Stopwatch.StartNew();
            await vm.RefreshRelationsCommand.ExecuteAsync(null);
            report.AppendLine("refresh relations: " + watch.ElapsedMilliseconds + " ms, " + vm.Relations.Count + " relations" + freeze?.Take());
            vm.CurrentStage = UiStage.Relations;
            RelationListItem relation = vm.Relations.First(r => r.Name == relationName);
            watch.Restart();
            vm.SelectedRelation = relation;
            await SettleAsync(vm);
            report.AppendLine("open '" + relationName + "': " + watch.ElapsedMilliseconds + " ms" + freeze?.Take());
            Describe(report, vm);

            if (Environment.GetEnvironmentVariable("JOYPRO_RELBUG_ADD") is { Length: > 0 } query)
            {
                // Add actions: every shown row of a search at once (the user added pitch of many games and aircraft).
                vm.ShowBuilderFindCommand.Execute(null);
                vm.BuilderSearchText = query;
                await SettleAsync(vm);
                freeze?.Take();
                vm.CheckAllShownBuilderResultsCommand.Execute(null);
                int ticked = vm.BuilderCheckedCount;
                client.Log.AppendLine("--- add " + ticked + " rows");
                watch.Restart();
                await vm.AddCheckedBuilderResultsAsync();
                long added = watch.ElapsedMilliseconds;
                await SettleAsync(vm);
                report.AppendLine("add " + ticked + " rows ('" + query + "'): " + added + " ms until the add returned, " + watch.ElapsedMilliseconds + " ms until idle" + freeze?.Take());
                Describe(report, vm);

                if (Environment.GetEnvironmentVariable("JOYPRO_RELBUG_ADDALL") == "1")
                {
                    vm.ShowBuilderSuggestedCommand.Execute(null);
                    vm.BuilderShowWeakMatches = true;
                    int all = vm.BuilderAddAllCount;
                    client.Log.AppendLine("--- add all " + all + " suggestions");
                    watch.Restart();
                    await vm.AddAllBuilderSuggestionsCommand.ExecuteAsync(null);
                    long done = watch.ElapsedMilliseconds;
                    await SettleAsync(vm);
                    report.AppendLine("add all " + all + " suggestions: " + done + " ms until the add returned, " + watch.ElapsedMilliseconds + " ms until idle" + freeze?.Take());
                    Describe(report, vm);
                }

                // One more change afterwards (the user: "every save/addition takes forever").
                client.Log.AppendLine("--- one more change");
                watch.Restart();
                vm.SelectedRelationNode = vm.RelationNodes.First();
                await vm.DisableSelectedNodeCommand.ExecuteAsync(null);
                await SettleAsync(vm);
                report.AppendLine("switch one action off: " + watch.ElapsedMilliseconds + " ms until idle" + freeze?.Take());
            }
        }
        finally
        {
            freeze?.Dispose();
            main?.Dispose();
            vm.StopGameConfigWatcher();
            report.AppendLine().AppendLine("engine calls:").Append(client.Log);
            File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", oldData);
        }
    }

    // The longest gap between ticks of a 15 ms UI timer since the last Take: how long the UI thread did not answer.
    private sealed class FreezeMeter : IDisposable
    {
        private readonly Avalonia.Threading.DispatcherTimer timer;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long last;
        private long worst;

        public FreezeMeter()
        {
            timer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(15), Avalonia.Threading.DispatcherPriority.Background, (_, _) =>
            {
                long now = clock.ElapsedMilliseconds;
                worst = Math.Max(worst, now - last);
                last = now;
            });
            last = clock.ElapsedMilliseconds;
            timer.Start();
        }

        private TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime;

        public string Take()
        {
            TimeSpan now = Process.GetCurrentProcess().TotalProcessorTime;
            string text = " | UI blocked up to " + worst + " ms | app CPU " + (long)(now - cpu).TotalMilliseconds + " ms | managed memory " + (GC.GetTotalMemory(false) >> 20) + " MB";
            cpu = now;
            worst = 0;
            last = clock.ElapsedMilliseconds;
            return text;
        }

        public void Dispose() => timer.Stop();
    }

    internal static void Describe(StringBuilder report, MainWindowViewModel vm)
    {
        report.AppendLine("  nodes " + vm.RelationNodes.Count + " | function state '" + vm.BuilderFunctionState + "' text '" + vm.BuilderFunctionText + "' chip '" + vm.BuilderFunctionChipText +
                          "' | suggestions " + vm.BuilderSuggestions.Count + " | status '" + vm.BuilderSuggestStatus + "' | empty text shown " + vm.ShowBuilderSuggestEmpty + ": " + vm.BuilderSuggestEmptyText);
        report.AppendLine("  status line: " + vm.StatusText);
    }

    internal static async Task SettleAsync(MainWindowViewModel vm)
    {
        await Task.Delay(400);
        int idle = 0;
        for (int i = 0; i < 6000 && idle < 10; i++)
        {
            idle = vm.IsBusy || vm.IsBuilderSuggestLoading || vm.IsBuilderSearching ? 0 : idle + 1;
            await Task.Delay(50);
        }
    }

    // Every engine call with its arguments, time, exit code and output size.
    internal sealed class LoggingEngineClient : IEngineCliClient
    {
        private readonly EngineCliClient inner = new();
        public readonly StringBuilder Log = new();
        public string DumpFolder = string.Empty;
        private int dumps;

        public async Task<EngineCommandResult> RunAsync(string engineExePath, System.Collections.Generic.IReadOnlyList<string> arguments, System.Threading.CancellationToken cancellationToken = default)
        {
            Stopwatch watch = Stopwatch.StartNew();
            EngineCommandResult result = await inner.RunAsync(engineExePath, arguments, cancellationToken);
            Note(arguments, result, watch);
            return result;
        }

        public async Task<EngineCommandResult> RunStreamingAsync(string engineExePath, System.Collections.Generic.IReadOnlyList<string> arguments, EngineRunOptions options, System.Threading.CancellationToken cancellationToken = default)
        {
            Stopwatch watch = Stopwatch.StartNew();
            EngineCommandResult result = await inner.RunStreamingAsync(engineExePath, arguments, options, cancellationToken);
            Note(arguments, result, watch);
            return result;
        }

        private void Note(System.Collections.Generic.IReadOnlyList<string> arguments, EngineCommandResult result, Stopwatch watch)
        {
            string stderr = result.StandardError ?? string.Empty;
            if (DumpFolder.Length > 0)
                File.WriteAllText(Path.Combine(DumpFolder, "call-" + System.Threading.Interlocked.Increment(ref dumps).ToString("000") + "-" + string.Join("_", arguments.SkipWhile(a => a != "--load-corrections").Skip(2).Where(a => !a.StartsWith("--")).TakeLast(3)).Replace(":", "").Replace("\\", "").Replace("/", "") + ".txt"),
                    result.StandardOutput + Environment.NewLine + "----- stderr -----" + Environment.NewLine + stderr);
            lock (Log)
                Log.AppendLine("  " + watch.ElapsedMilliseconds + " ms exit " + result.ExitCode + " out " + (result.StandardOutput ?? string.Empty).Length + " | " +
                               string.Join(" ", arguments.SkipWhile(a => a != "--load-corrections").Skip(2)) +
                               (stderr.Trim().Length > 0 ? " | stderr: " + stderr.Trim().Replace(Environment.NewLine, " / ")[..Math.Min(400, stderr.Trim().Length)] : string.Empty));
        }
    }
}
