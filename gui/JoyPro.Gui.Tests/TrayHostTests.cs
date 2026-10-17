using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Watch;
using Xunit;
using Xunit.Abstractions;

namespace JoyPro.Gui.Tests;

// The tray host's logic (JoyPro.Watch.TrayWatcherHost, docs/sync-watcher.md#tray-host) with fakes: engine, settings,
// GUI presence, launcher, tray view and watcher. Nothing here starts a process, shows a window or touches the registry.
[SupportedOSPlatform("windows")]
public sealed class TrayHostTests
{
    private const string DcsKey = "DCS|C:\\Saved Games\\DCS";
    private const string Profile = @"D:\Profiles\main.pr0file";

    [Fact]
    public async Task Start_WatchesTrackedConfigs_OffersWhatChangedMeanwhile_AndNotifiesOnce()
    {
        Rig rig = new();
        await rig.Host.StartAsync();

        FakeWatcher watcher = Assert.Single(rig.Watchers);
        Assert.Equal([DcsKey], watcher.Specs.Select(s => s.GameId));
        Assert.Equal([DcsKey], watcher.Triggered);
        Assert.Equal("JoyPro2: watching 1 game config", rig.View.Last.ToolTip);
        Assert.True(rig.View.Last.WatchingChecked);
        Assert.True(rig.View.Last.WatchingEnabled);
        Assert.Null(rig.View.Last.ReviewText);
        Assert.True(rig.Trims > 0);

        // The game closed after changing its controls.
        watcher.Raise(DcsKey);
        Assert.Contains(rig.Engine.Calls, c => c.SequenceEqual(new[] { "--status", "false", "--benchmark", "false", "--load-corrections", "false", "sync", "status", "--game", "DCS", "--path", "C:\\Saved Games\\DCS" }));
        IReadOnlyList<string> diff = Assert.Single(rig.Engine.Calls, c => c.Contains("diff"));
        Assert.Equal(["--profile", Profile], diff.Take(2));
        (string title, string text) = Assert.Single(rig.View.Notes);
        Assert.Equal(TrayWatcherHost.NotificationTitle, title);
        Assert.Equal("DCS World changed 2 controls JoyPro manages. Click here to take them over or ignore them in JoyPro2.", text);
        Assert.Equal("Review the changes made in DCS World (2)", rig.View.Last.ReviewText);
        Assert.Equal("JoyPro2: DCS World changed controls - click to review", rig.View.Last.ToolTip);
        PendingReview pending = Assert.Single(rig.Host.Pending);
        Assert.Equal(DcsKey, pending.WatchKey);

        // The same config again: still one entry (the count updated).
        rig.Engine.DiffChanges = 3;
        watcher.Raise(DcsKey);
        Assert.Equal(3, Assert.Single(rig.Host.Pending).Changes);
    }

    [Fact]
    public async Task OnlyChangesJoyProDoesNotManage_AreAcceptedSilently()
    {
        Rig rig = new();
        rig.Engine.DiffChanges = 0;
        await rig.Host.StartAsync();
        rig.Watchers[0].Raise(DcsKey);
        Assert.Contains(rig.Engine.Calls, c => c.Contains("accept"));
        Assert.Empty(rig.View.Notes);
        Assert.Empty(rig.Host.Pending);
    }

    [Fact]
    public async Task WithoutAProfile_NothingIsDiffedOrAsked()
    {
        Rig rig = new(new TraySettings(true, true, true, string.Empty));
        await rig.Host.StartAsync();
        rig.Watchers[0].Raise(DcsKey);
        Assert.DoesNotContain(rig.Engine.Calls, c => c.Contains("diff"));
        Assert.Empty(rig.View.Notes);

        Rig missing = new(new TraySettings(true, true, true, @"D:\gone.pr0file"));
        await missing.Host.StartAsync();
        missing.Watchers[0].Raise(DcsKey);
        Assert.DoesNotContain(missing.Engine.Calls, c => c.Contains("diff"));
    }

    [Fact]
    public async Task ReviewAndOpen_StartTheGui()
    {
        Rig rig = new();
        await rig.Host.StartAsync();
        rig.Host.Review();
        Assert.Equal([], Assert.Single(rig.Launcher.Starts));

        rig.Watchers[0].Raise(DcsKey);
        rig.Host.Review();
        Assert.Equal(["--review", DcsKey], rig.Launcher.Starts[1]);
        rig.Host.Open();
        Assert.Equal([], rig.Launcher.Starts[2]);
    }

    [Fact]
    public async Task WhileTheGuiRuns_TheTrayPauses_AndResumesWithoutAskingAgain()
    {
        Rig rig = new();
        rig.Gui.Running = true;
        await rig.Host.StartAsync();
        Assert.True(rig.Host.Paused);
        Assert.Empty(rig.Engine.Calls);
        Assert.Empty(rig.Watchers);
        Assert.False(rig.View.Last.WatchingEnabled);
        Assert.Equal("JoyPro2 is open and watches the games itself", rig.View.Last.ToolTip);
        rig.Host.ToggleWatching();
        Assert.Empty(rig.Settings.Saved);

        // The GUI closed (close to tray on): watch again, but do not re-offer what the user postponed in the GUI.
        rig.Gui.Running = false;
        rig.Gui.RaiseExited();
        Assert.False(rig.Host.Paused);
        FakeWatcher watcher = Assert.Single(rig.Watchers);
        Assert.Empty(watcher.Triggered);
        Assert.True(rig.View.Last.WatchingEnabled);
        Assert.Equal(2, rig.Settings.Loads);

        // The GUI starts again: the watcher goes away (no double prompts), pending reviews are the GUI's now.
        watcher.Raise(DcsKey);
        Assert.Single(rig.Host.Pending);
        rig.Gui.Running = true;
        rig.Gui.RaiseStarted();
        Assert.True(rig.Host.Paused);
        Assert.True(watcher.Disposed);
        Assert.Equal(0, watcher.Subscribers);
        Assert.Empty(rig.Host.Pending);
        Assert.False(rig.Host.HasWatcher);
        int calls = rig.Engine.Calls.Count;
        watcher.Raise(DcsKey);
        Assert.Equal(calls, rig.Engine.Calls.Count);
    }

    [Fact]
    public async Task GuiClosedWithCloseToTrayOff_EndsTheTrayHost()
    {
        Rig rig = new();
        rig.Gui.Running = true;
        await rig.Host.StartAsync();
        rig.Settings.Current = rig.Settings.Current with { CloseToTray = false };
        rig.Gui.Running = false;
        rig.Gui.RaiseExited();
        Assert.Equal(1, rig.View.Closed);
        Assert.Empty(rig.Watchers);
    }

    [Fact]
    public async Task ToggleWatching_SavesTheSetting_AndStopsOrRestartsTheWatcher()
    {
        Rig rig = new();
        await rig.Host.StartAsync();
        rig.Watchers[0].Raise(DcsKey);

        rig.Host.ToggleWatching();
        Assert.Equal([false], rig.Settings.Saved);
        Assert.False(rig.Host.Watching);
        Assert.True(rig.Watchers[0].Disposed);
        Assert.Empty(rig.Host.Pending);
        Assert.False(rig.View.Last.WatchingChecked);
        Assert.Equal("JoyPro2: not watching game configs", rig.View.Last.ToolTip);

        rig.Host.ToggleWatching();
        Assert.Equal([false, true], rig.Settings.Saved);
        Assert.Equal(2, rig.Watchers.Count);
        Assert.Equal([DcsKey], rig.Watchers[1].Triggered);

        rig.Host.Exit();
        Assert.Equal(1, rig.View.Closed);
    }

    [Fact]
    public async Task WatchingOff_AtStart_RunsNothing_AndDisposeUnsubscribes()
    {
        Rig rig = new(new TraySettings(false, true, true, Profile));
        await rig.Host.StartAsync();
        Assert.Empty(rig.Engine.Calls);
        Assert.Empty(rig.Watchers);
        Assert.Equal(1, rig.Gui.StartedSubscribers);
        rig.Host.Dispose();
        Assert.Equal(0, rig.Gui.StartedSubscribers);
        Assert.Equal(0, rig.Gui.ExitedSubscribers);
        Assert.True(rig.Gui.Disposed);
    }

    // ---- Fakes ---------------------------------------------------------------------------------------------------------

    internal sealed class Rig
    {
        public Rig(TraySettings? settings = null)
        {
            Settings.Current = settings ?? new TraySettings(true, true, true, Profile);
            Host = new TrayWatcherHost(Engine, Settings, Gui, Launcher, View, () =>
            {
                FakeWatcher watcher = new();
                Watchers.Add(watcher);
                return watcher;
            }, action => action(), () => Trims++, path => path == Profile);
        }

        public FakeEngine Engine { get; } = new();
        public FakeSettings Settings { get; } = new();
        public FakeGui Gui { get; } = new();
        public FakeLauncher Launcher { get; } = new();
        public FakeView View { get; } = new();
        public List<FakeWatcher> Watchers { get; } = [];
        public int Trims { get; set; }
        public TrayWatcherHost Host { get; }
    }

    internal sealed class FakeEngine : IWatchEngine
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public int DiffChanges { get; set; } = 2;
        public string Status { get; set; } = TrayModeTests.StatusJson;

        // What sync status --game says ("unchanged": the game wrote the same content again).
        public string GameState { get; set; } = "changed";

        public Task<EngineRun> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            string output = "{}";
            if (arguments.Contains("status"))
                output = arguments.Contains("--game") ? "{\"Games\":[{\"Game\":\"DCS\",\"State\":\"" + GameState + "\"}]}" : Status;
            else if (arguments.Contains("diff"))
                output = "{\"Game\":\"DCS\",\"DisplayName\":\"DCS World\",\"Changes\":[" + string.Join(",", Enumerable.Repeat("{\"Id\":\"c\"}", DiffChanges)) + "]}";
            return Task.FromResult(new EngineRun(0, output, string.Empty));
        }
    }

    internal sealed class FakeSettings : ITraySettingsStore
    {
        public TraySettings Current { get; set; } = new(true, true, true, Profile);
        public List<bool> Saved { get; } = [];
        public int Loads { get; private set; }

        public TraySettings Load()
        {
            Loads++;
            return Current;
        }

        public void SaveWatchGameConfigs(bool value)
        {
            Saved.Add(value);
            Current = Current with { WatchGameConfigs = value };
        }
    }

    internal sealed class FakeGui : IGuiPresence
    {
        public bool Running { get; set; }
        public bool Disposed { get; private set; }
        public bool IsRunning => Running;
        public int StartedSubscribers => started?.GetInvocationList().Length ?? 0;
        public int ExitedSubscribers => exited?.GetInvocationList().Length ?? 0;
        private Action? started;
        private Action? exited;

        public event Action? Started { add => started += value; remove => started -= value; }
        public event Action? Exited { add => exited += value; remove => exited -= value; }

        public void Start()
        {
        }

        public void RaiseStarted() => started?.Invoke();

        public void RaiseExited() => exited?.Invoke();

        public void Dispose() => Disposed = true;
    }

    internal sealed class FakeLauncher : IJoyProLauncher
    {
        public List<IReadOnlyList<string>> Starts { get; } = [];

        public void OpenGui(IReadOnlyList<string> arguments) => Starts.Add(arguments.ToArray());
    }

    internal sealed class FakeView : ITrayView
    {
        public TrayViewState Last { get; private set; } = new(string.Empty, null, string.Empty, false, false);
        public List<(string Title, string Text)> Notes { get; } = [];
        public int Closed { get; private set; }

        public void Show(TrayViewState state) => Last = state;

        public void Notify(string title, string text) => Notes.Add((title, text));

        public void Close() => Closed++;
    }

    internal sealed class FakeWatcher : IGameWatcher
    {
        private Action<string>? changed;
        public List<GameWatchSpec> Specs { get; } = [];
        public List<string> Triggered { get; } = [];
        public bool Disposed { get; private set; }
        public int Subscribers => changed?.GetInvocationList().Length ?? 0;

        public event Action<string>? ConfigChanged { add => changed += value; remove => changed -= value; }

        public void Update(IEnumerable<GameWatchSpec> specs)
        {
            Specs.Clear();
            Specs.AddRange(specs);
        }

        public void Trigger(string gameId) => Triggered.Add(gameId);

        public void Raise(string key) => changed?.Invoke(key);

        public void Dispose() => Disposed = true;
    }
}

// The tray host with the real GameConfigWatcher on a temp folder: 1000 writes to the watched file, and 200 GUI
// start/exit hand-overs, must leave handles and the managed heap flat (process-wide numbers, so it runs alone).
[SupportedOSPlatform("windows")]
[Collection("ResourceMeasurements")]
public sealed class TrayHostResourceTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "joypro-trayhost-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper output;

    public TrayHostResourceTests(ITestOutputHelper output)
    {
        this.output = output;
        Directory.CreateDirectory(temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ThousandFileEvents_AndTwoHundredHandOvers_KeepHandlesAndMemoryFlat()
    {
        string config = Path.Combine(temp, "config.blk");
        File.WriteAllText(config, "start");
        TrayHostTests.Rig rig = new();
        string status = "{\"Games\":[{\"Game\":\"TestGame\",\"DisplayName\":\"Test Game\",\"Installed\":true,\"Tracked\":true,\"State\":\"unchanged\",\"ConfigPath\":\"" +
                        JsonEncodedText.Encode(config) + "\",\"Watch\":[{\"Directory\":\"" + JsonEncodedText.Encode(temp) +
                        "\",\"Filters\":[\"config.blk\"],\"IncludeSubdirectories\":false}],\"RegistryKey\":\"\",\"ProcessNames\":[\"joypro-no-such-game-process\"]}]}";
        rig.Engine.Status = status;
        object gate = new();
        int notes = 0;
        List<GameConfigWatcher> real = [];
        TrayWatcherHost host = new(rig.Engine, rig.Settings, rig.Gui, rig.Launcher, new CountingView(() => Interlocked.Increment(ref notes)), () =>
        {
            GameConfigWatcher watcher = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100));
            lock (real)
                real.Add(watcher);
            return watcher;
        }, action => { lock (gate) action(); }, null, path => true);
        // The fake engine answers at once, so the start completes inside the lock.
        Task start;
        lock (gate)
            start = host.StartAsync();
        await start;
        Assert.True(host.HasWatcher);

        File.WriteAllText(config, "warm-up");
        Assert.True(WaitFor(() => Volatile.Read(ref notes) == 1, 5000), "the first change should be reported once.");
        (int handlesBefore, long managedBefore) = Measure();
        for (int i = 0; i < 1000; i++)
            File.WriteAllText(config, "value " + i);
        Assert.True(WaitFor(() => Volatile.Read(ref notes) >= 2, 5000), "the writes should be reported.");
        Thread.Sleep(800);
        int reports = Volatile.Read(ref notes) - 1;
        Assert.InRange(reports, 1, 5);
        (int handlesAfter, long managedAfter) = Measure();
        output.WriteLine($"1000 writes: notifications={reports}, handles {handlesBefore} -> {handlesAfter}, managed {managedBefore / 1024} KB -> {managedAfter / 1024} KB");
        Assert.InRange(handlesAfter - handlesBefore, -50, 20);
        Assert.InRange(managedAfter - managedBefore, long.MinValue, 2 * 1024 * 1024);

        // 200 GUI starts and exits: the watcher is released and re-created each time.
        for (int i = 0; i < 200; i++)
        {
            rig.Gui.Running = true;
            lock (gate)
                rig.Gui.RaiseStarted();
            rig.Gui.Running = false;
            lock (gate)
                rig.Gui.RaiseExited();
        }
        await Task.Delay(200);
        (int handlesCycled, long managedCycled) = Measure();
        output.WriteLine($"200 hand-overs: handles {handlesAfter} -> {handlesCycled}, managed {managedAfter / 1024} KB -> {managedCycled / 1024} KB, watchers created {real.Count}");
        Assert.InRange(handlesCycled - handlesAfter, -50, 20);
        Assert.Equal(1, real.Count(w => w.HandleCount > 0));
        lock (gate)
            host.Dispose();
        Assert.All(real, w => Assert.Equal(0, w.HandleCount));
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

    private static (int Handles, long Managed) Measure()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using Process self = Process.GetCurrentProcess();
        return (self.HandleCount, GC.GetGCMemoryInfo(GCKind.FullBlocking).HeapSizeBytes);
    }

    private sealed class CountingView(Action notified) : ITrayView
    {
        public void Show(TrayViewState state)
        {
        }

        public void Notify(string title, string text) => notified();

        public void Close()
        {
        }
    }
}
