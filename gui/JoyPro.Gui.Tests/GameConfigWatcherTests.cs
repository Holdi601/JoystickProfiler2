using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using JoyPro.Gui.Services;
using JoyPro.Watch;
using Microsoft.Win32;
using Xunit;
using Xunit.Abstractions;

namespace JoyPro.Gui.Tests;

// The game config watcher must cost nothing while idle and never leak: these tests touch a watched file 1000 times,
// re-create the watchers 200 times and wait for a process, and check that events are debounced, handles and memory
// stay flat and the idle CPU time is ~0 (docs/sync-watcher.md has the measured numbers).
// The process-wide numbers (handles, heap, CPU) are only meaningful while no other test runs.
[CollectionDefinition("ResourceMeasurements", DisableParallelization = true)]
public sealed class ResourceMeasurementsCollection
{
}

[SupportedOSPlatform("windows")]
[Collection("ResourceMeasurements")]
public sealed class GameConfigWatcherTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "joypro-watcher-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper output;

    public GameConfigWatcherTests(ITestOutputHelper output)
    {
        this.output = output;
        Directory.CreateDirectory(temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private GameWatchSpec Spec(string gameId, params string[] processNames)
    {
        return new GameWatchSpec(gameId, [new GameWatchFolder(temp, ["config.blk"], false)], string.Empty, processNames);
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    // Waits until value() stayed the same for quietMs (or timeoutMs passed) and returns it.
    private static int WaitForQuiet(Func<int> value, int quietMs, int timeoutMs)
    {
        Stopwatch total = Stopwatch.StartNew();
        Stopwatch quiet = Stopwatch.StartNew();
        int last = value();
        while (quiet.ElapsedMilliseconds < quietMs && total.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(50);
            int now = value();
            if (now != last)
            {
                last = now;
                quiet.Restart();
            }
        }
        return last;
    }

    private static (int Handles, long Managed, long WorkingSet) Measure()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using Process self = Process.GetCurrentProcess();
        // The live managed heap after a full blocking collection.
        return (self.HandleCount, GC.GetGCMemoryInfo(GCKind.FullBlocking).HeapSizeBytes, self.WorkingSet64);
    }

    [Fact]
    public void ThousandWrites_AreDebounced_AndHandlesAndMemoryStayFlat()
    {
        string config = Path.Combine(temp, "config.blk");
        string other = Path.Combine(temp, "other.txt");
        File.WriteAllText(config, "start");
        int raised = 0;
        using GameConfigWatcher watcher = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(200));
        watcher.ConfigChanged += _ => Interlocked.Increment(ref raised);
        watcher.Update([Spec("TestGame", "joypro-no-such-game-process")]);
        Assert.Equal(["TestGame"], watcher.WatchedGames);

        File.WriteAllText(config, "warm-up");
        Assert.True(WaitFor(() => Volatile.Read(ref raised) == 1, 5000), "the first change should be reported once.");
        int watcherHandles = watcher.HandleCount;

        // One burst: 1000 writes of the watched file and 1000 of a file next to it. It is reported (debounced) and,
        // once the reports stopped, measured. The process-wide numbers (handles, heap) include what earlier tests of a
        // full run still wind down or start meanwhile (thread-pool and render threads, engine servers): a burst that
        // shows growth is repeated (up to three bursts), and the smallest growth of a burst is judged. A watcher that
        // leaks grows with every burst; the others' noise does not repeat. Fewer handles than before are no leak.
        // The watcher's own handle count stays exact in every burst.
        int reportsSoFar = 1;
        int handleGrowth = int.MaxValue;
        long managedGrowth = long.MaxValue;
        for (int burst = 0; burst < 3 && (handleGrowth > 20 || managedGrowth > 2 * 1024 * 1024); burst++)
        {
            (int handlesBefore, long managedBefore, long workingSetBefore) = Measure();
            for (int i = 0; i < 1000; i++)
            {
                File.WriteAllText(config, "value " + burst + " " + i);
                File.WriteAllText(other, "not watched " + i);
            }
            int before = reportsSoFar;
            Assert.True(WaitFor(() => Volatile.Read(ref raised) > before, 10000), "the writes should be reported.");
            // Quiet: no report for 1.5 s (five debounce times), so a burst a busy disk split is counted whole.
            int reports = WaitForQuiet(() => Volatile.Read(ref raised), 1500, 15000);
            int burstReports = reports - reportsSoFar;
            reportsSoFar = reports;
            // 1000 writes in a burst -> one report (a slow disk may split the burst; never one report per write).
            Assert.InRange(burstReports, 1, 5);

            // Not watched: no report.
            File.WriteAllText(other, "still not watched");
            Thread.Sleep(800);
            Assert.Equal(reportsSoFar, Volatile.Read(ref raised));

            (int handlesAfter, long managedAfter, long workingSetAfter) = Measure();
            output.WriteLine($"burst {burst + 1}, 1000 writes: reports={burstReports}, handles {handlesBefore} -> {handlesAfter}, managed {managedBefore / 1024} KB -> {managedAfter / 1024} KB, working set {workingSetBefore / 1024 / 1024} MB -> {workingSetAfter / 1024 / 1024} MB");
            Assert.Equal(watcherHandles, watcher.HandleCount);
            handleGrowth = Math.Min(handleGrowth, handlesAfter - handlesBefore);
            managedGrowth = Math.Min(managedGrowth, managedAfter - managedBefore);
        }
        Assert.True(handleGrowth <= 20, "process handles grew by " + handleGrowth + " in every burst of 1000 writes.");
        Assert.True(managedGrowth <= 2 * 1024 * 1024, "the managed heap grew by " + managedGrowth / 1024 + " KB in every burst of 1000 writes.");

        // Idle: nothing runs (no timer, no polling).
        // The number is for the whole test process, so a full run's leftovers of earlier tests (render threads, timers)
        // can fall into one window. A polling or busy watcher uses CPU in every window: the quietest of up to five
        // windows is judged.
        using Process self = Process.GetCurrentProcess();
        double idleCpuMs = double.MaxValue;
        for (int window = 0; window < 5 && idleCpuMs >= 300; window++)
        {
            self.Refresh();
            TimeSpan cpuBefore = self.TotalProcessorTime;
            Thread.Sleep(3000);
            self.Refresh();
            idleCpuMs = Math.Min(idleCpuMs, (self.TotalProcessorTime - cpuBefore).TotalMilliseconds);
        }
        output.WriteLine($"idle 3 s with the watcher on: {idleCpuMs:0} ms CPU for the whole test process");
        Assert.True(idleCpuMs < 300, "the watcher must not use CPU while idle (" + idleCpuMs + " ms).");
    }

    [Fact]
    public void RecreatingWatchers_ReleasesEveryHandle()
    {
        File.WriteAllText(Path.Combine(temp, "config.blk"), "start");
        using GameConfigWatcher watcher = new(TimeSpan.FromMilliseconds(100));
        watcher.Update([Spec("TestGame")]);
        int perGame = watcher.HandleCount;
        Assert.True(perGame >= 2, "a game with a folder holds a file watcher and its debounce timer.");
        // The same spec again keeps the watchers.
        watcher.Update([Spec("TestGame")]);
        Assert.Equal(perGame, watcher.HandleCount);

        // Warm-up (first-time allocations of the runtime), then the measured round.
        for (int i = 0; i < 20; i++)
        {
            watcher.Update([]);
            watcher.Update([Spec("TestGame"), new GameWatchSpec("Other", [new GameWatchFolder(temp, ["x.cfg"], true)], string.Empty, [])]);
        }
        (int handlesBefore, long managedBefore, _) = Measure();
        for (int i = 0; i < 200; i++)
        {
            watcher.Update([]);
            watcher.Update([Spec("TestGame"), new GameWatchSpec("Other", [new GameWatchFolder(temp, ["x.cfg"], true)], string.Empty, [])]);
        }
        watcher.Update([]);
        Assert.Equal(0, watcher.HandleCount);
        (int handlesAfter, long managedAfter, _) = Measure();
        output.WriteLine($"200 x re-create: handles {handlesBefore} -> {handlesAfter}, managed {managedBefore / 1024} KB -> {managedAfter / 1024} KB");
        Assert.InRange(handlesAfter - handlesBefore, -50, 20);
        Assert.InRange(managedAfter - managedBefore, long.MinValue, 2 * 1024 * 1024);
    }

    [Fact]
    public void ChangeWhileTheGameRuns_IsReportedAfterItExits()
    {
        string config = Path.Combine(temp, "config.blk");
        File.WriteAllText(config, "start");
        // A stand-in "game": a copy of ping with a name no other process has.
        string name = "jpwatch" + Guid.NewGuid().ToString("N")[..8];
        string exe = Path.Combine(temp, name + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        int raised = 0;
        using GameConfigWatcher watcher = new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
        watcher.ConfigChanged += _ => Interlocked.Increment(ref raised);
        watcher.Update([Spec("TestGame", name)]);
        using Process game = Process.Start(new ProcessStartInfo(exe, "-n 4 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            File.WriteAllText(config, "changed in game");
            Thread.Sleep(1200);
            Assert.Equal(0, Volatile.Read(ref raised));
            Assert.True(game.WaitForExit(15000), "the stand-in game should exit.");
            Assert.True(WaitFor(() => Volatile.Read(ref raised) == 1, 5000), "the change should be reported once the game has closed.");
        }
        finally
        {
            if (!game.HasExited)
                game.Kill();
        }
    }

    // An MSFS Microsoft Store save (Xbox connected storage): the sim writes a new blob under a new GUID name, a new
    // container.<n>, removes the old ones and replaces containers.index (written beside it and renamed over it, or
    // rewritten in place). Each way of saving is reported once, after the burst of writes.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StoreSave_NewBlobNamesAndReplacedIndex_IsReportedOnce(bool replaceByRename)
    {
        string wgs = Path.Combine(temp, "wgs");
        string user = Path.Combine(wgs, "0009000A1B2C3D_69F80140");
        string container = Path.Combine(user, Guid.NewGuid().ToString("N").ToUpperInvariant());
        Directory.CreateDirectory(container);
        string index = Path.Combine(user, "containers.index");
        File.WriteAllBytes(index, [14, 0, 0, 0, 1]);
        string oldBlob = Path.Combine(container, Guid.NewGuid().ToString("N").ToUpperInvariant());
        File.WriteAllText(oldBlob, "<Device/> old");
        File.WriteAllBytes(Path.Combine(container, "container.1"), [4, 0, 0, 0]);
        int raised = 0;
        using GameConfigWatcher watcher = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(200));
        watcher.ConfigChanged += _ => Interlocked.Increment(ref raised);
        watcher.Update([new GameWatchSpec("MSFS2024", [new GameWatchFolder(wgs, ["containers.index"], true)], string.Empty, ["joypro-no-such-game-process"])]);

        File.WriteAllText(Path.Combine(container, Guid.NewGuid().ToString("N").ToUpperInvariant()), "<Device/> new");
        File.WriteAllBytes(Path.Combine(container, "container.2"), [4, 0, 0, 0, 1]);
        File.Delete(oldBlob);
        File.Delete(Path.Combine(container, "container.1"));
        // Only the blobs changed so far: nothing is reported for them (the index says the save is complete).
        Thread.Sleep(700);
        Assert.Equal(0, Volatile.Read(ref raised));
        if (replaceByRename)
        {
            string written = index + ".tmp";
            File.WriteAllBytes(written, [14, 0, 0, 0, 2]);
            File.Move(written, index, true);
        }
        else
            File.WriteAllBytes(index, [14, 0, 0, 0, 2]);

        Assert.True(WaitFor(() => Volatile.Read(ref raised) == 1, 5000), "the Store save should be reported.");
        Thread.Sleep(600);
        Assert.Equal(1, Volatile.Read(ref raised));
    }

    // MSFS from the Microsoft Store: the sim is any exe in its install folder (here a copy of ping called
    // gamelaunchhelper.exe, no process name given), and the save folder waits the engine's settle time after the last
    // write, also after the sim closed (the Xbox app rewrites containers.index for seconds after it).
    [Fact]
    public void GameFoundByItsInstallFolder_AndTheSettleTimeOfTheSpec_AreUsed()
    {
        string config = Path.Combine(temp, "config.blk");
        File.WriteAllText(config, "start");
        string content = Path.Combine(temp, "XboxGames", "Microsoft Flight Simulator 2024", "Content");
        Directory.CreateDirectory(content);
        string exe = Path.Combine(content, "gamelaunchhelper.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        int raised = 0;
        using GameConfigWatcher watcher = new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        watcher.ConfigChanged += _ => Interlocked.Increment(ref raised);
        watcher.Update([new GameWatchSpec("MSFS2024", [new GameWatchFolder(temp, ["config.blk"], false)], string.Empty, [], [content + "\\"], 2)]);

        // Settle: two writes 1 s apart are one check, 2 s after the last one.
        Stopwatch since = Stopwatch.StartNew();
        File.WriteAllText(config, "first");
        Thread.Sleep(1000);
        File.WriteAllText(config, "second");
        since.Restart();
        Assert.True(WaitFor(() => Volatile.Read(ref raised) == 1, 6000), "the change should be reported.");
        Assert.True(since.ElapsedMilliseconds >= 1800, "reported after " + since.ElapsedMilliseconds + " ms, before the settle time");
        Thread.Sleep(500);
        Assert.Equal(1, Volatile.Read(ref raised));

        using Process game = Process.Start(new ProcessStartInfo(exe, "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            File.WriteAllText(config, "changed in the sim");
            Thread.Sleep(3500);
            Assert.Equal(1, Volatile.Read(ref raised));
            game.Kill();
            Assert.True(game.WaitForExit(10000));
            since.Restart();
            Assert.True(WaitFor(() => Volatile.Read(ref raised) == 2, 8000), "the change should be reported once the sim has closed.");
            Assert.True(since.ElapsedMilliseconds >= 1800, "reported " + since.ElapsedMilliseconds + " ms after the exit, before the settle time");
        }
        finally
        {
            if (!game.HasExited)
                game.Kill();
        }
    }

    [Fact]
    public void RegistryChanges_AreReported_AndTheWatchIsReArmed()
    {
        string sub = @"Software\JoyProTests\Watcher" + Guid.NewGuid().ToString("N");
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(sub);
        try
        {
            int raised = 0;
            using GameConfigWatcher watcher = new(TimeSpan.FromMilliseconds(200));
            watcher.ConfigChanged += _ => Interlocked.Increment(ref raised);
            watcher.Update([new GameWatchSpec("RegGame", [], @"HKCU\" + sub, [])]);
            key.SetValue("ControllerMap", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            Assert.True(WaitFor(() => Volatile.Read(ref raised) == 1, 5000), "a registry change should be reported.");
            key.SetValue("ControllerMap", new byte[] { 4, 5, 6 }, RegistryValueKind.Binary);
            Assert.True(WaitFor(() => Volatile.Read(ref raised) == 2, 5000), "the next registry change should be reported too.");
            watcher.Update([]);
            Assert.Equal(0, watcher.HandleCount);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sub, false);
        }
    }
}
