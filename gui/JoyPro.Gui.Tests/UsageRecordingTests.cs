using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Usage;
using JoyPro.Watch;
using Xunit;

namespace JoyPro.Gui.Tests;

// The usage recording's live tail (JoyPro.Watch UsageLogTailer), its tray host part, the settings, the Statistics page
// and the engine's usage commands in a process and through the server (docs/usage-statistics.md).
[SupportedOSPlatform("windows")]
public sealed class UsageRecordingTests : IDisposable
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "usage-rec-" + Guid.NewGuid().ToString("N"));

    public UsageRecordingTests() => Directory.CreateDirectory(work);

    public void Dispose()
    {
        try
        {
            Directory.Delete(work, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Append(string file, string text)
    {
        using FileStream stream = new(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static bool WaitFor(Func<bool> condition, int ms = 8000)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms)
        {
            if (condition())
                return true;
            Thread.Sleep(25);
        }
        return condition();
    }

    private static string ReadShared(string path)
    {
        using FileStream s = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader r = new(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    // ---- live tail ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Tail_CopiesOnlyAppendedBytes_ToALiveCopy()
    {
        string logs = Path.Combine(work, "Logs");
        string live = Path.Combine(work, "Live");
        Directory.CreateDirectory(logs);
        string log = Path.Combine(logs, "dcs.log");
        File.WriteAllText(log, "=== Log opened UTC 2026-01-10 18:00:00\n");
        using UsageLogTailer tailer = new(live, TimeSpan.FromMilliseconds(50));
        tailer.Update([new UsageTailTarget("DCS", UsageSourceKinds.DcsLog, logs, "dcs.log", [])]);
        StringBuilder expected = new("=== Log opened UTC 2026-01-10 18:00:00\n");
        for (int i = 0; i < 20; i++)
        {
            string line = "2026-01-10 18:00:" + i.ToString("00") + ".000 INFO    EDCORE (Main): line " + i + "\n";
            Append(log, line);
            expected.Append(line);
            Thread.Sleep(15);
        }
        Assert.True(WaitFor(() => tailer.LiveCopies.Count == 1 && File.Exists(tailer.LiveCopies.Values.First()) && ReadShared(tailer.LiveCopies.Values.First()) == expected.ToString()),
            "live copy: " + (tailer.LiveCopies.Count == 0 ? "none" : ReadShared(tailer.LiveCopies.Values.First())));
        // Each byte read once: the copy is as long as the log, however many reads it took.
        Assert.Equal(new FileInfo(log).Length, tailer.BytesCopied);
        Assert.True(tailer.Reads >= 1);
        string copy = tailer.LiveCopies.Values.First();
        Assert.StartsWith(Path.Combine(live, "DCS"), copy);
        // The harvest reads the tailer's names.
        Assert.True(UsageHarvester.ParseLiveName(Path.GetFileName(copy), out string kind, out string original));
        Assert.Equal(UsageSourceKinds.DcsLog, kind);
        Assert.Equal("dcs.log", original);
    }

    [Fact]
    public void Tail_OverwrittenLog_FinishesTheOldCopy_StartsANewOne()
    {
        string logs = Path.Combine(work, "Logs");
        string live = Path.Combine(work, "Live");
        Directory.CreateDirectory(logs);
        string log = Path.Combine(logs, "Game.log");
        File.WriteAllText(log, "<2026-01-01T10:00:00.000Z> Log started on run 1\n");
        using UsageLogTailer tailer = new(live, TimeSpan.FromMilliseconds(50));
        tailer.Update([new UsageTailTarget("StarCitizen", UsageSourceKinds.StarCitizenLog, logs, "Game.log", [])]);
        Append(log, "<2026-01-01T10:05:00.000Z> more of run 1\n");
        Assert.True(WaitFor(() => tailer.LiveCopies.Count == 1));
        string first = tailer.LiveCopies.Values.First();
        Assert.True(WaitFor(() => ReadShared(first).Contains("more of run 1", StringComparison.Ordinal)));
        // The game starts again and writes its log from the start (other first bytes).
        File.WriteAllText(log, "<2026-01-02T20:00:00.000Z> Log started on run 2\n");
        Assert.True(WaitFor(() => tailer.LiveCopies.Count == 1 && tailer.LiveCopies.Values.First() != first));
        string done = Path.ChangeExtension(first, null) + UsageLogTailer.DoneExtension;
        Assert.True(File.Exists(done));
        Assert.Contains("run 1", File.ReadAllText(done));
        Assert.DoesNotContain("run 2", File.ReadAllText(done));
        Assert.True(WaitFor(() => ReadShared(tailer.LiveCopies.Values.First()).Contains("run 2", StringComparison.Ordinal)));
    }

    [Fact]
    public void Tail_RotatedLog_FinishesItsCopy()
    {
        string logs = Path.Combine(work, "Logs");
        string live = Path.Combine(work, "Live");
        Directory.CreateDirectory(logs);
        string log = Path.Combine(logs, "dcs.log");
        File.WriteAllText(log, "=== Log opened UTC 2026-01-10 18:00:00\n");
        using UsageLogTailer tailer = new(live, TimeSpan.FromMilliseconds(50));
        tailer.Update([new UsageTailTarget("DCS", UsageSourceKinds.DcsLog, logs, "dcs.log", [])]);
        Append(log, "2026-01-10 18:00:01.000 INFO    EDCORE (Main): one\n");
        Assert.True(WaitFor(() => tailer.LiveCopies.Count == 1));
        string copy = tailer.LiveCopies.Values.First();
        Assert.True(WaitFor(() => ReadShared(copy).Contains("one", StringComparison.Ordinal)));
        Append(log, "2026-01-10 18:00:02.000 INFO    EDCORE (Main): last line before the rotation\n");
        // DCS's next start renames dcs.log to dcs.log.old.
        File.Move(log, Path.Combine(logs, "dcs.log.old"));
        string done = Path.ChangeExtension(copy, null) + UsageLogTailer.DoneExtension;
        Assert.True(WaitFor(() => File.Exists(done)));
        Assert.Contains("last line before the rotation", File.ReadAllText(done));
    }

    [Fact]
    public void Tail_GameExit_FinishesCopies_AndRaisesSessionEnded()
    {
        string logs = Path.Combine(work, "Journals");
        string live = Path.Combine(work, "Live");
        Directory.CreateDirectory(logs);
        // A stand-in for the game: a short-lived process (ping, no window).
        using Process stand = Process.Start(new ProcessStartInfo("ping", "-n 3 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        List<string> ended = [];
        using UsageLogTailer tailer = new(live, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100),
            _ => new RunningGame(Process.GetProcessById(stand.Id), "test"));
        tailer.GameSessionEnded += game =>
        {
            lock (ended)
                ended.Add(game);
        };
        tailer.Update([new UsageTailTarget("EliteDangerous", UsageSourceKinds.EliteJournal, logs, "Journal.*.log", ["EliteDangerous64"])]);
        string journal = Path.Combine(logs, "Journal.2026-01-11T100000.01.log");
        File.WriteAllText(journal, "{ \"timestamp\":\"2026-01-11T10:00:00Z\", \"event\":\"Fileheader\" }\n");
        Append(journal, "{ \"timestamp\":\"2026-01-11T10:01:00Z\", \"event\":\"LoadGame\", \"Ship\":\"Python\" }\n");
        Assert.True(WaitFor(() => tailer.LiveCopies.Count == 1));
        string copy = tailer.LiveCopies.Values.First();
        Assert.True(WaitFor(() => { lock (ended) return ended.Count == 1; }, 15000), "no session end");
        Assert.Equal("EliteDangerous", ended[0]);
        Assert.True(File.Exists(Path.ChangeExtension(copy, null) + UsageLogTailer.DoneExtension));
        Assert.Empty(tailer.LiveCopies);
        Assert.Equal(2, tailer.HandleCount);
    }

    [Fact]
    public void Tail_UpdateAndDispose_ReleaseEverything()
    {
        string a = Path.Combine(work, "A");
        string b = Path.Combine(work, "B");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        UsageLogTailer tailer = new(Path.Combine(work, "Live"));
        tailer.Update([new UsageTailTarget("DCS", UsageSourceKinds.DcsLog, a, "dcs.log", []), new UsageTailTarget("XPlane12", UsageSourceKinds.XPlaneLog, b, "Log.txt", [])]);
        Assert.Equal(4, tailer.HandleCount);
        Assert.Equal(["DCS", "XPlane12"], tailer.Games);
        tailer.Update([new UsageTailTarget("DCS", UsageSourceKinds.DcsLog, a, "dcs.log", [])]);
        Assert.Equal(2, tailer.HandleCount);
        // A folder that does not exist (a game without logs yet) costs nothing.
        tailer.Update([new UsageTailTarget("DCS", UsageSourceKinds.DcsLog, Path.Combine(work, "missing"), "dcs.log", [])]);
        Assert.Equal(0, tailer.HandleCount);
        tailer.Dispose();
        Assert.Equal(0, tailer.HandleCount);
    }

    // Long run: a game writing its log for a while (thousands of appends, a rotation): the tail's handles and the
    // process's handles and managed memory stay flat, and every byte is copied once.
    [Fact]
    public void Tail_LongRun_HandlesAndMemoryStayFlat()
    {
        string logs = Path.Combine(work, "Logs");
        Directory.CreateDirectory(logs);
        string log = Path.Combine(logs, "Game.log");
        File.WriteAllText(log, "<2026-01-01T10:00:00.000Z> Log started\n");
        using UsageLogTailer tailer = new(Path.Combine(work, "Live"), TimeSpan.FromMilliseconds(20));
        tailer.Update([new UsageTailTarget("StarCitizen", UsageSourceKinds.StarCitizenLog, logs, "Game.log", [])]);
        Append(log, "<2026-01-01T10:00:01.000Z> warm up\n");
        Assert.True(WaitFor(() => tailer.LiveCopies.Count == 1));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Process self = Process.GetCurrentProcess();
        self.Refresh();
        int handlesBefore = self.HandleCount;
        long memoryBefore = GC.GetTotalMemory(true);
        int tailHandles = tailer.HandleCount;
        long written = new FileInfo(log).Length;
        Stopwatch clock = Stopwatch.StartNew();
        for (int i = 0; i < 3000; i++)
        {
            string line = "<2026-01-01T10:" + (i / 60 % 60).ToString("00") + ":" + (i % 60).ToString("00") + ".000Z> [Notice] <Something> line " + i + " " + new string('x', 80) + "\n";
            Append(log, line);
            written += Encoding.UTF8.GetByteCount(line);
            if (i % 50 == 0)
                Thread.Sleep(5);
        }
        Assert.True(WaitFor(() => tailer.BytesCopied == written, 15000), "copied " + tailer.BytesCopied + " of " + written);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        self.Refresh();
        Assert.Equal(tailHandles, tailer.HandleCount);
        Assert.True(self.HandleCount - handlesBefore < 25, "handles " + handlesBefore + " -> " + self.HandleCount);
        long grown = GC.GetTotalMemory(true) - memoryBefore;
        Assert.True(grown < 2_000_000, "managed heap grew by " + grown + " bytes");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Equal(written, new FileInfo(tailer.LiveCopies.Values.First()).Length);
    }

    [Fact]
    public void Sources_JsonForTheWatcher_Parses()
    {
        (string LiveFolder, List<UsageTailTarget> Targets)? parsed = UsageLogTailer.ParseSources(
            "{\"Folder\":\"C:\\\\d\\\\Usage\",\"LiveFolder\":\"C:\\\\d\\\\Usage\\\\Live\",\"Tail\":[{\"Game\":\"DCS\",\"Kind\":\"dcs-log\",\"Directory\":\"C:\\\\x\\\\Logs\",\"Filter\":\"dcs.log\",\"ProcessNames\":[\"DCS\"]}]}");
        Assert.NotNull(parsed);
        Assert.Equal(@"C:\d\Usage\Live", parsed!.Value.LiveFolder);
        UsageTailTarget t = Assert.Single(parsed.Value.Targets);
        Assert.Equal(("DCS", "dcs-log", @"C:\x\Logs", "dcs.log"), (t.GameId, t.Kind, t.Directory, t.Filter));
        Assert.Equal(["DCS"], t.ProcessNames);
        Assert.Null(UsageLogTailer.ParseSources("not json"));
    }

    // ---- tray host --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TrayHost_RecordsWhileWatching_HarvestsAfterAGame_PausesForTheGui()
    {
        TrayHostTests.FakeEngine engine = new();
        TrayHostTests.FakeSettings settings = new() { Current = new TraySettings(false, true, true, string.Empty) };
        TrayHostTests.FakeGui gui = new();
        FakeTailer? tailer = null;
        string sources = "{\"LiveFolder\":\"C:\\\\d\\\\Live\",\"Tail\":[{\"Game\":\"DCS\",\"Kind\":\"dcs-log\",\"Directory\":\"C:\\\\x\",\"Filter\":\"dcs.log\",\"ProcessNames\":[\"DCS\"]}]}";
        UsageEngine usageEngine = new(engine, sources);
        using TrayWatcherHost host = new(usageEngine, settings, gui, new TrayHostTests.FakeLauncher(), new TrayHostTests.FakeView(), () => new TrayHostTests.FakeWatcher(),
            action => action(), null, null, folder => tailer = new FakeTailer(folder));
        await host.StartAsync();
        // Recording runs although "Watching game configs" is off.
        Assert.Contains(usageEngine.Calls, c => c.Contains("harvest") && !c.Contains("--game"));
        Assert.NotNull(tailer);
        Assert.Equal(@"C:\d\Live", tailer!.Folder);
        Assert.Equal("DCS", Assert.Single(tailer.Targets).GameId);
        // A game closed: a harvest of that game.
        tailer.RaiseEnded("DCS");
        await Task.Delay(50);
        Assert.Contains(usageEngine.Calls, c => c.Contains("harvest") && c.Contains("--game") && c.Contains("DCS"));
        // The GUI starts: it records itself, the tray host's tail is released.
        gui.RaiseStarted();
        Assert.True(tailer.Disposed);
        Assert.False(host.HasUsageTailer);
    }

    private sealed class UsageEngine(TrayHostTests.FakeEngine inner, string sources) : IWatchEngine
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<EngineRun> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            if (arguments.Contains("usage") && arguments.Contains("sources"))
                return Task.FromResult(new EngineRun(0, sources, string.Empty));
            if (arguments.Contains("usage"))
                return Task.FromResult(new EngineRun(0, "{\"FilesRead\":0}", string.Empty));
            return inner.RunAsync(arguments, cancellationToken);
        }
    }

    internal sealed class FakeTailer(string folder) : IUsageTailer
    {
        public string Folder { get; } = folder;
        public List<UsageTailTarget> Targets { get; } = [];
        public bool Disposed { get; private set; }

        public event Action<string>? GameSessionEnded;

        public void Update(IEnumerable<UsageTailTarget> targets)
        {
            Targets.Clear();
            Targets.AddRange(targets);
        }

        public void RaiseEnded(string game) => GameSessionEnded?.Invoke(game);

        public void Dispose() => Disposed = true;
    }

    // ---- the engine's usage commands ---------------------------------------------------------------------------------

    // A statistics document of the fixtures (engine usage stats on tests/fixtures/usage/root, with a profile's growth
    // and exports added): the Statistics page with every section, for the contrast and layout checks.
    internal static string SampleStats => File.ReadAllText(Path.Combine(UsageStoreTests.RepoRoot(), "engine", "tests", "fixtures", "usage", "stats-sample.json"));

    internal static string? EnginePath()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return engine != null && File.Exists(engine) ? engine : null;
    }

    // The fixture folder as JOYPRO_USAGE_ROOT, a data folder of its own: harvest, then the statistics document.
    internal static async Task<(string Stats, string Ranking, Dictionary<string, string> Environment)> FixtureStatsAsync(string engine, string folder)
    {
        string root = Path.Combine(folder, "root");
        UsageStoreTests.CopyFixtures(root);
        Dictionary<string, string> environment = new()
        {
            ["JOYPRO_DATA_DIR"] = Path.Combine(folder, "data"),
            ["JOYPRO_USAGE_ROOT"] = root,
            ["JOYPRO_DEVICE_KB"] = "off"
        };
        EngineCliClient client = new();
        EngineRunOptions options = new() { Environment = environment };
        List<string> common = ["--status", "false", "--benchmark", "false", "--load-corrections", "false"];
        EngineCommandResult harvest = await client.RunProcessAsync(engine, [.. common, "usage", "harvest"], options, CancellationToken.None);
        Assert.True(harvest.ExitCode == 0, harvest.StandardError);
        // Two exports for the statistics.
        Directory.CreateDirectory(Path.Combine(folder, "data", "Usage"));
        File.AppendAllText(Path.Combine(folder, "data", "Usage", "exports.jsonl"), "{\"g\":\"DCS\",\"t\":\"2026-01-10T20:00:00Z\"}\n{\"g\":\"IL2Game\",\"t\":\"2026-01-15T22:00:00Z\"}\n");
        EngineCommandResult stats = await client.RunProcessAsync(engine, [.. common, "usage", "stats"], options, CancellationToken.None);
        Assert.True(stats.ExitCode == 0, stats.StandardError);
        EngineCommandResult ranking = await client.RunProcessAsync(engine, [.. common, "usage", "ranking"], options, CancellationToken.None);
        Assert.True(ranking.ExitCode == 0, ranking.StandardError);
        return (stats.StandardOutput, ranking.StandardOutput, environment);
    }

    [Fact]
    public async Task Engine_Stats_SameInTheServer_AndQuick()
    {
        string? engine = EnginePath();
        if (engine == null)
            return;
        (string stats, string ranking, Dictionary<string, string> environment) = await FixtureStatsAsync(engine, work);
        using (JsonDocument doc = JsonDocument.Parse(stats))
        {
            Assert.True(doc.RootElement.GetProperty("HasData").GetBoolean());
            Assert.Contains(doc.RootElement.GetProperty("Units").EnumerateArray(), u => u.GetProperty("Unit").GetString() == "F-16C_50");
            Assert.Contains(doc.RootElement.GetProperty("Sources").EnumerateArray(), s => s.GetProperty("Kind").GetString() == "sc-log");
            Assert.Equal(2, doc.RootElement.GetProperty("Exports").GetArrayLength());
        }
        using (JsonDocument doc = JsonDocument.Parse(ranking))
            Assert.Equal("F-16C_50", doc.RootElement.GetProperty("References").GetProperty("DCS|fixed_wing").GetString()!.Split('|')[1]);
        Assert.True(EngineServerCommands.IsServed(["--status", "false", "usage", "stats"]));
        Assert.True(EngineServerCommands.IsServed(["--profile", "p.pr0file", "--usage-style", "off", "usage", "ranking"]));
        Assert.False(EngineServerCommands.IsServed(["--status", "false", "usage", "harvest"]));
        Assert.True(EngineServerCommands.KeepsServer(["--status", "false", "usage", "harvest"]));
        try
        {
            EngineRunOptions options = new() { Environment = environment };
            List<string> args = ["--status", "false", "--benchmark", "false", "--load-corrections", "false", "usage", "stats"];
            EngineCommandResult? served = await EngineServerPool.TryRunAsync(engine, args, "usage-test", options, CancellationToken.None);
            Assert.NotNull(served);
            Assert.Equal(Normalize(stats), Normalize(served!.StandardOutput));
            Stopwatch clock = Stopwatch.StartNew();
            served = await EngineServerPool.TryRunAsync(engine, args, "usage-test", options, CancellationToken.None);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), "usage stats took " + clock.ElapsedMilliseconds + " ms in the server");
            // pin through the server, then the ranking shows it.
            EngineCommandResult? pin = await EngineServerPool.TryRunAsync(engine, ["--status", "false", "usage", "pin", "DCS", "FA-18C_hornet"], "usage-test", options, CancellationToken.None);
            Assert.Equal(0, pin!.ExitCode);
            EngineCommandResult? ranked = await EngineServerPool.TryRunAsync(engine, ["--status", "false", "usage", "ranking"], "usage-test", options, CancellationToken.None);
            using JsonDocument doc = JsonDocument.Parse(ranked!.StandardOutput);
            JsonElement first = doc.RootElement.GetProperty("Units")[0];
            Assert.Equal("FA-18C_hornet", first.GetProperty("Unit").GetString());
            Assert.True(first.GetProperty("Pinned").GetBoolean());
        }
        finally
        {
            EngineServerPool.StopAll();
        }
    }

    private static string Normalize(string json) => System.Text.RegularExpressions.Regex.Replace(json, "\"(Generated|Utc|Last|Seconds|Updated)\":\"[^\"]*\"", "\"$1\":\"<t>\"");

    // ---- settings ---------------------------------------------------------------------------------------------------

    [Fact]
    public void UsesUsageStyle_TheCommandsThatLayOut()
    {
        Assert.True(MainWindowViewModel.UsesUsageStyle(["--status", "false", "relations", "auto-assign", "--dry-run"]));
        Assert.True(MainWindowViewModel.UsesUsageStyle(["relations", "auto-group", "--preset", "wing"]));
        Assert.True(MainWindowViewModel.UsesUsageStyle(["setup", "plan"]));
        Assert.True(MainWindowViewModel.UsesUsageStyle(["arrange", "ask", "--text", "x"]));
        Assert.False(MainWindowViewModel.UsesUsageStyle(["relations", "show", "Pitch"]));
        Assert.False(MainWindowViewModel.UsesUsageStyle(["usage", "stats"]));
    }

    [Fact]
    public void RankingRows_FromTheEngine()
    {
        MainWindowViewModel vm = new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
        vm.ApplyUsageRanking("{\"Games\":[{\"Rank\":1,\"Game\":\"WarThunder\",\"Name\":\"War Thunder\",\"Hours\":23,\"Sessions\":0,\"Last\":\"2026-01-11T19:00:00Z\",\"Pinned\":false,\"Excluded\":false,\"Id\":\"WarThunder|\"}]," +
                             "\"Units\":[{\"Rank\":1,\"Game\":\"DCS\",\"GameName\":\"DCS World\",\"Unit\":\"F-16C_50\",\"Name\":\"F-16C_50\",\"Hours\":1.2,\"Sessions\":2,\"Evidence\":\"logs\",\"Binds\":0,\"Pinned\":true,\"Excluded\":false,\"Last\":\"\",\"Id\":\"DCS|F-16C_50\"}," +
                             "{\"Rank\":2,\"Game\":\"DCS\",\"GameName\":\"DCS World\",\"Unit\":\"F-14B\",\"Name\":\"F-14B\",\"Hours\":0,\"Sessions\":0,\"Evidence\":\"binds\",\"Binds\":120,\"Pinned\":false,\"Excluded\":false,\"Last\":\"\",\"Id\":\"DCS|F-14B\"}]}");
        Assert.True(vm.HasUsageRanking);
        Assert.Equal("F-16C_50 (DCS World)", vm.UsageRankingUnits[0].Title);
        Assert.Equal("Unpin", vm.UsageRankingUnits[0].PinText);
        Assert.Contains("2 sessions", vm.UsageRankingUnits[0].Detail);
        Assert.Contains("120 binds", vm.UsageRankingUnits[1].Detail);
        Assert.Equal("War Thunder", vm.UsageRankingGames[0].Title);
        Assert.Contains("23 h", vm.UsageRankingGames[0].Detail);
    }

    [Fact]
    public void StatisticsPage_FactsAndNumbers_InTheUsersLanguage()
    {
        Assert.Equal("You flew the F-14B more than all helicopters together (10 h against 5 h).", StatisticsViewModel.FactText("more-than-helicopters", ["F-14B", "10", "5"]));
        Assert.Equal(string.Empty, StatisticsViewModel.FactText("unknown-fact", ["x"]));
        // Every fact the engine knows has a sentence here.
        foreach (string id in new[] { "more-than-helicopters", "more-than-planes", "more-than-spaceships", "total-days", "top-game", "longest-session", "streak", "favourite-ship",
                                      "distance", "distance-short", "most-kills", "busiest-weekday", "night-owl", "first-flight", "exports", "steam-only" })
        {
            Assert.True(StatisticsViewModel.FactTexts.ContainsKey(id), id);
            Assert.False(UsageStats.FactEnglish(id, ["1", "2", "3"]).StartsWith(id + ":", StringComparison.Ordinal), id);
        }
        Assert.Equal(13, StatisticsViewModel.SourceTitles.Length);
        foreach (string kind in UsageSourceKinds.All)
            Assert.Contains(UsageSourceKinds.Title(kind), StatisticsViewModel.SourceTitles);
        Assert.Equal("2,467 h", StatisticsViewModel.HoursText(2467.2).Replace('\u00a0', ','));
        Assert.Equal("12 min", StatisticsViewModel.HoursText(0.2));
    }

    // Statistics page: the document of the fixtures on the page at 1280x800 and 2267x1250, light and dark (PNGs in
    // JOYPRO_USAGE_RENDER_OUT when set); every chart and section is there and nothing overlaps the page's edge.
    [Fact]
    public async Task StatisticsPage_Renders_LightAndDark()
    {
        string? engine = EnginePath();
        if (engine == null)
            return;
        (string stats, string ranking, _) = await FixtureStatsAsync(engine, work);
        string outDir = Environment.GetEnvironmentVariable("JOYPRO_USAGE_RENDER_OUT") ?? Path.Combine(work, "renders");
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            try
            {
                foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
                {
                    foreach ((int width, int height) in new[] { (1280, 800), (2267, 1250) })
                    {
                        MainWindowViewModel vm = new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };
                        vm.ShowStatisticsPage = true;
                        vm.Statistics.Apply(stats);
                        vm.CurrentStage = UiStage.Statistics;
                        using HiddenWindow main = NavPagesTests.Show(vm, variant, width, height);
                        main.Settle();
                        MainWindow window = (MainWindow)main.Window;
                        Assert.Contains("statistics", window.NavBarOrder());
                        StatisticsView page = main.Page.GetVisualDescendants().OfType<StatisticsView>().Single();
                        Assert.True(page.IsEffectivelyVisible);
                        List<string> texts = page.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
                        Assert.Contains("Hours per game", texts);
                        Assert.Contains("Fun facts", texts);
                        Assert.Contains(texts, t => t.StartsWith("Your longest streak", StringComparison.Ordinal));
                        Assert.True(page.GetVisualDescendants().OfType<Views.Controls.UsageBar>().Count(b => b.IsEffectivelyVisible) >= 5);
                        // No raw ids on the page (as the quality gate's I6 checks titles): "F-16C_50" shows as a name.
                        Assert.Empty(RawIds(page));
                        Assert.Contains(texts, t => t == "F-16C Block 50/52");
                        NavPagesTests.Save(main, Path.Combine(outDir, "statistics-" + width + "-" + theme + ".png"));
                        // Scrolled to the profile and the sources.
                        ScrollViewer scroll = page.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "StatisticsScroll");
                        scroll.Offset = new Vector(0, Math.Max(0, (scroll.Extent.Height - scroll.Viewport.Height) / 2));
                        main.Settle();
                        NavPagesTests.Save(main, Path.Combine(outDir, "statistics-" + width + "-" + theme + "-mid.png"));
                        scroll.Offset = new Vector(0, scroll.Extent.Height);
                        main.Settle();
                        NavPagesTests.Save(main, Path.Combine(outDir, "statistics-" + width + "-" + theme + "-end.png"));

                        // Settings > Usage statistics with the ranking.
                        vm.ApplyUsageRanking(ranking);
                        vm.CurrentStage = UiStage.Settings;
                        foreach (SettingsSectionItem section in vm.SettingsSections)
                            section.IsExpanded = section.Id == MainWindowViewModel.UsageSettingsSectionId;
                        vm.MoveSettingsSection(vm.FindSettingsSection(MainWindowViewModel.UsageSettingsSectionId)!, 0);
                        main.Settle();
                        NavPagesTests.Save(main, Path.Combine(outDir, "settings-usage-" + width + "-" + theme + ".png"));
                        Control rankingList = main.Page.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Name == "UsageRankingList");
                        Assert.Empty(RawIds(rankingList));
                        Assert.Contains(rankingList.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "F/A-18C Hornet Lot 20 (DCS World)");
                    }
                }
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
    }

    // Texts on screen that are program ids: what the quality gate's I6 calls unreadable (snake_case, hex, lua keys,
    // encoded DCS ids), and any word with an underscore ("F-16C_50", "FA-18C_hornet"). Each text is split at " (",
    // ", " and ": " so "F-16C_50 (DCS World)" is checked by its parts.
    internal static List<string> RawIds(Control root)
    {
        List<string> raw = [];
        foreach (TextBlock text in root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)))
        {
            foreach (string part in System.Text.RegularExpressions.Regex.Split(text.Text!, @" \(|\)|, |: "))
            {
                string fragment = part.Trim();
                if (fragment.Length == 0)
                    continue;
                bool underscoreWord = fragment.Split(' ').Any(w => w.Contains('_') && !w.Contains('='));
                if (QualityGate.Gate.UnreadableTitle(fragment, null) != null || underscoreWord && !fragment.Contains("mission_text_log", StringComparison.Ordinal))
                    raw.Add(text.Text!);
            }
        }
        return raw.Distinct().ToList();
    }

    [Fact]
    public void RawIdScan_FindsIds_NotNames()
    {
        // Controls only on the render thread: Avalonia objects belong to the thread its dispatcher runs on, which is not
        // the test's thread once another test has started it.
        AvaloniaRenderThread.Run(() =>
        {
            TextBlock id = new() { Text = "F-16C_50 (DCS World)" };
            TextBlock name = new() { Text = "F-16C Block 50/52 (DCS World)" };
            StackPanel panel = new();
            panel.Children.Add(id);
            panel.Children.Add(name);
            Window window = new() { Content = panel };
            using HiddenWindow hidden = HiddenWindow.Create(window, new Size(400, 200));
            Assert.Equal(["F-16C_50 (DCS World)"], RawIds(hidden.Page));
        });
    }

    [Fact]
    public void StatisticsPage_Empty_SaysWhatToDo()
    {
        StatisticsViewModel vm = new();
        vm.Apply("{\"HasData\":false,\"Totals\":{},\"Games\":[],\"Units\":[],\"Counters\":[],\"Weekdays\":[0,0,0,0,0,0,0],\"HoursOfDay\":[],\"Months\":[],\"Exports\":[],\"Devices\":[],\"Growth\":[],\"Facts\":[],\"Sources\":[]}");
        Assert.True(vm.ShowEmpty);
        Assert.False(vm.HasUnits);
        Assert.False(vm.HasGrowth);
    }
}

// Settings that must survive a restart (ui-settings.json in the test run's settings folder; the "TrayMode" collection
// owns that file).
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class UsageSettingsTests : IDisposable
{
    private readonly string uiSettings = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");

    public UsageSettingsTests()
    {
        Directory.CreateDirectory(AppInfo.SettingsFolder);
        File.Delete(uiSettings);
    }

    public void Dispose() => File.Delete(uiSettings);

    private static MainWindowViewModel Vm(bool persist) =>
        new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: persist) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };

    [Fact]
    public void StatisticsPage_OptIn_KeptForTheNextStart_AndRespectsHidePages()
    {
        MainWindowViewModel first = Vm(persist: true);
        Assert.False(first.ShowStatisticsPage);
        Assert.True(first.UseUsageStyle);
        NavPageItem page = first.FindNavPage("statistics")!;
        Assert.False(page.IsShownInBar);
        first.ShowStatisticsPage = true;
        Assert.True(page.IsAvailable);
        Assert.True(page.IsShownInBar);
        first.UseUsageStyle = false;
        first.MoveNavPage(page, 1);
        using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(uiSettings)))
        {
            Assert.True(json.RootElement.GetProperty("showStatisticsPage").GetBoolean());
            Assert.False(json.RootElement.GetProperty("usageStyle").GetBoolean());
        }

        MainWindowViewModel second = Vm(persist: true);
        Assert.True(second.ShowStatisticsPage);
        Assert.False(second.UseUsageStyle);
        Assert.Equal("statistics", second.NavPages[1].Id);
        Assert.True(second.FindNavPage("statistics")!.IsShownInBar);
        // Settings > Pages may hide it like any page.
        second.FindNavPage("statistics")!.IsVisible = false;
        Assert.False(second.FindNavPage("statistics")!.IsShownInBar);
        MainWindowViewModel third = Vm(persist: true);
        Assert.True(third.ShowStatisticsPage);
        Assert.False(third.FindNavPage("statistics")!.IsShownInBar);
        // Switched off: gone from the bar, and the page closes when it is open.
        third.FindNavPage("statistics")!.IsVisible = true;
        third.CurrentStage = UiStage.Statistics;
        third.ShowStatisticsPage = false;
        Assert.Equal(UiStage.Start, third.CurrentStage);
        Assert.False(third.FindNavPage("statistics")!.IsShownInBar);
        Assert.False(Vm(persist: true).ShowStatisticsPage);
    }
}
