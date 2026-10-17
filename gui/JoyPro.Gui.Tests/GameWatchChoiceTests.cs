using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Watch;
using Xunit;
using Xunit.Abstractions;

namespace JoyPro.Gui.Tests;

// The Start page's per-game "Watch for changes" ticks (docs/sync-watcher.md#which-games-are-watched): defaults for
// installed and missing games, explicit choices kept in ui-settings.json ("watchedGames"), a game installed later ticked
// by itself, and the tray host reading the same ticks. Fake engine; the settings file is the test run's own
// (JOYPRO_DATA_DIR), so these run in the "TrayMode" collection that owns it.
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class GameWatchChoiceTests
{
    [Fact]
    public async Task GameCards_AreTickedForInstalledGames_AndOffForGamesNotFound()
    {
        WatchFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake, persist: false);
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);

        GameCardItem dcs = vm.GameCards.Single(c => c.GameId == "DCS");
        GameCardItem warThunder = vm.GameCards.Single(c => c.GameId == "WarThunder");
        GameCardItem il2 = vm.GameCards.Single(c => c.GameId == "IL2Game");
        Assert.True(dcs.Watch && dcs.CanWatch);
        Assert.True(warThunder.Watch && warThunder.CanWatch);
        Assert.False(il2.Watch);
        Assert.False(il2.CanWatch);
        Assert.StartsWith("Watch for changes: JoyPro notices when you change binds in this game", dcs.WatchTip);
        Assert.StartsWith("Not found on this PC", il2.WatchTip);
        Assert.True(vm.WatchAllGames);
        Assert.True(vm.HasWatchableGameCards);
        Assert.Equal(GameWatchChoices.Empty, vm.GameWatchChoices);
    }

    [Fact]
    public async Task Untick_Persists_ReTick_WatchAllNone_AndALaterInstalledGameIsTickedByItself()
    {
        string settingsFile = JoyProPaths.UiSettingsFile(JoyProPaths.SettingsFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        File.Delete(settingsFile);
        try
        {
            WatchFakeEngine fake = new();
            MainWindowViewModel first = CreateVm(fake, persist: true);
            await first.RefreshGameCardsCommand.ExecuteAsync(null);

            // Untick War Thunder: saved at once, as an explicit choice, where the tray host reads it.
            first.GameCards.Single(c => c.GameId == "WarThunder").Watch = false;
            Assert.False(first.GameWatchChoices.Choice("WarThunder"));
            Assert.Null(first.GameWatchChoices.Choice("DCS"));
            Assert.Null(first.WatchAllGames);
            using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(settingsFile)))
                Assert.False(json.RootElement.GetProperty("watchedGames").GetProperty("WarThunder").GetBoolean());
            TraySettings tray = new UiSettingsFileStore(settingsFile).Load();
            Assert.False(tray.WatchChoices.IsWatched("WarThunder", installed: true));
            Assert.True(tray.WatchChoices.IsWatched("DCS", installed: true));
            Assert.True(tray.WatchGameConfigs);

            // A new start of the GUI remembers it.
            MainWindowViewModel second = CreateVm(fake, persist: true);
            await second.RefreshGameCardsCommand.ExecuteAsync(null);
            Assert.False(second.GameCards.Single(c => c.GameId == "WarThunder").Watch);
            Assert.True(second.GameCards.Single(c => c.GameId == "DCS").Watch);

            // IL-2 is installed later: ticked by itself (the user never unticked it).
            Assert.False(second.GameCards.Single(c => c.GameId == "IL2Game").Watch);
            fake.Installed.Add("IL2Game");
            await second.RefreshGameCardsCommand.ExecuteAsync(null);
            GameCardItem il2 = second.GameCards.Single(c => c.GameId == "IL2Game");
            Assert.True(il2.Watch && il2.CanWatch);

            // War Thunder uninstalled and installed again: still unticked (the user's choice is kept).
            fake.Installed.Remove("WarThunder");
            await second.RefreshGameCardsCommand.ExecuteAsync(null);
            Assert.False(second.GameCards.Single(c => c.GameId == "WarThunder").CanWatch);
            fake.Installed.Add("WarThunder");
            await second.RefreshGameCardsCommand.ExecuteAsync(null);
            Assert.False(second.GameCards.Single(c => c.GameId == "WarThunder").Watch);

            // Ticked again: saved as watched.
            second.GameCards.Single(c => c.GameId == "WarThunder").Watch = true;
            Assert.True(second.WatchAllGames);
            Assert.True(new UiSettingsFileStore(settingsFile).Load().WatchChoices.Choice("WarThunder"));

            // Watch all / none in the section header: every installed game, saved.
            second.WatchAllGames = false;
            Assert.All(second.GameCards.Where(c => c.CanWatch), c => Assert.False(c.Watch));
            Assert.False(second.WatchAllGames);
            GameWatchChoices none = new UiSettingsFileStore(settingsFile).Load().WatchChoices;
            Assert.False(none.IsWatched("DCS", true) || none.IsWatched("WarThunder", true) || none.IsWatched("IL2Game", true));
            second.WatchAllGames = true;
            Assert.All(second.GameCards.Where(c => c.CanWatch), c => Assert.True(c.Watch));
            Assert.True(new UiSettingsFileStore(settingsFile).Load().WatchChoices.IsWatched("DCS", true));

            // The tray host's own switch keeps the ticks (it rewrites only watchGameConfigs).
            second.GameCards.Single(c => c.GameId == "DCS").Watch = false;
            new UiSettingsFileStore(settingsFile).SaveWatchGameConfigs(false);
            TraySettings after = new UiSettingsFileStore(settingsFile).Load();
            Assert.False(after.WatchGameConfigs);
            Assert.False(after.WatchChoices.Choice("DCS"));
            MainWindowViewModel third = CreateVm(fake, persist: true);
            Assert.False(third.WatchGameConfigs);
            Assert.False(third.GameWatchChoices.Choice("DCS"));
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public void GameWatchChoices_DefaultsAndValueEquality()
    {
        GameWatchChoices choices = GameWatchChoices.Empty.With("DCS", false);
        Assert.False(choices.IsWatched("dcs", installed: true));
        Assert.True(choices.IsWatched("WarThunder", installed: true));
        Assert.False(choices.IsWatched("WarThunder", installed: false));
        Assert.Same(choices, choices.With("DCS", false));
        Assert.Equal(new GameWatchChoices([new("dcs", false)]), choices);
        Assert.NotEqual(GameWatchChoices.Empty, choices);
        Assert.True(choices.With("DCS", true).IsWatched("DCS", true));
        Assert.Equal(new TraySettings(true, false, false, string.Empty), new TraySettings(true, false, false, string.Empty) { WatchChoices = new GameWatchChoices(null) });
    }

    // The tray host watches only the ticked games and reads the ticks again when the GUI (where they are changed) closed.
    [Fact]
    public async Task TrayHost_WatchesOnlyTickedGames_AndPicksUpNewTicksWhenTheGuiCloses()
    {
        TrayHostTests.Rig rig = new(new TraySettings(true, true, true, @"D:\Profiles\main.pr0file") { WatchChoices = GameWatchChoices.Empty.With("DCS", false) });
        rig.Engine.Status = WatchFakeEngine.SyncStatus(@"C:\dcs", @"C:\wt");
        await rig.Host.StartAsync();
        TrayHostTests.FakeWatcher watcher = Assert.Single(rig.Watchers);
        Assert.Equal(["WarThunder|C:\\wt\\machine.blk"], watcher.Specs.Select(s => s.GameId));
        Assert.Equal("JoyPro2: watching 1 game config", rig.View.Last.ToolTip);
        // A late event of the unticked game is dropped.
        int calls = rig.Engine.Calls.Count;
        watcher.Raise("DCS|C:\\dcs");
        Assert.Equal(calls, rig.Engine.Calls.Count);

        // The user ticks DCS in the GUI and closes it: the tray host watches both.
        rig.Gui.Running = true;
        rig.Gui.RaiseStarted();
        rig.Settings.Current = rig.Settings.Current with { WatchChoices = rig.Settings.Current.WatchChoices.With("DCS", true).With("WarThunder", false) };
        rig.Gui.Running = false;
        rig.Gui.RaiseExited();
        TrayHostTests.FakeWatcher again = rig.Watchers.Last();
        Assert.NotSame(watcher, again);
        Assert.True(watcher.Disposed);
        Assert.Equal(["DCS|C:\\dcs"], again.Specs.Select(s => s.GameId));
    }

    [Fact]
    public void RenderStartPageGameWatch()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_UI_DESIGN_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            WatchFakeEngine fake = new();
            MainWindowViewModel vm = CreateVm(fake, persist: false);
            AvaloniaRenderThread.Wait(GuiParityRenderTests.Done(vm.RefreshGameCardsCommand.ExecuteAsync(null)));
            vm.GameCards.Single(c => c.GameId == "WarThunder").Watch = false;
            vm.CurrentStage = UiStage.Start;
            try
            {
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    GuiParityRenderTests.Render(vm, Path.Combine(outDir, "start-game-watch-" + theme + ".png"), "Your games", 640, page =>
                    {
                        List<CheckBox> boxes = page.GetVisualDescendantsOfType<CheckBox>().Where(b => b.Classes.Contains("watchGame") && b.IsEffectivelyVisible).ToList();
                        Assert.Equal(vm.GameCards.Count, boxes.Count);
                        Assert.Equal(1, boxes.Count(b => !b.IsEnabled));
                        Assert.Equal(1, boxes.Count(b => b.IsEnabled && b.IsChecked == false));
                        CheckBox all = page.GetVisualDescendantsOfType<CheckBox>().Single(b => b.Name == "WatchAllGamesBox");
                        Assert.Null(all.IsChecked);
                    });
                }
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
    }

    internal static MainWindowViewModel CreateVm(IEngineCliClient fake, bool persist) =>
        new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: persist)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };

    // engine games installs / integrations / sync status for DCS, IL-2 and War Thunder; Installed decides which are found.
    internal sealed class WatchFakeEngine : IEngineCliClient
    {
        public HashSet<string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase) { "DCS", "WarThunder" };

        public string Status { get; set; } = SyncStatus(@"C:\dcs", @"C:\wt");

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            string output = "ok";
            if (NoviceGuiTests.Seq(arguments, "games", "installs"))
            {
                output = "{\"Games\":[" + string.Join(",", new[] { ("DCS", "DCS World"), ("IL2Game", "IL-2 Sturmovik: Great Battles"), ("WarThunder", "War Thunder") }
                    .Select(g => "{\"GameId\":\"" + g.Item1 + "\",\"Name\":\"" + g.Item2 + "\",\"Installed\":" + (Installed.Contains(g.Item1) ? "true" : "false") +
                                 ",\"InstallPath\":\"\",\"Source\":\"test\",\"ConfigPaths\":[]}")) + "]}";
            }
            else if (NoviceGuiTests.Seq(arguments, "games", "integrations"))
            {
                output = "{\"Integrations\":[{\"GameId\":\"WarThunder\",\"DisplayName\":\"War Thunder\",\"Aliases\":[\"warthunder\"],\"Notes\":\"\",\"SupportsCatalogRefresh\":true}]}";
            }
            else if (NoviceGuiTests.Seq(arguments, "sync", "status"))
            {
                output = Status;
            }
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = output,
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            });
        }

        // DCS (a folder of *.diff.lua) and War Thunder (machine.blk), both tracked and unchanged.
        public static string SyncStatus(string dcsFolder, string warThunderFolder)
        {
            string dcs = JsonEncodedText.Encode(dcsFolder).ToString();
            string wt = JsonEncodedText.Encode(warThunderFolder).ToString();
            string wtFile = JsonEncodedText.Encode(Path.Combine(warThunderFolder, "machine.blk")).ToString();
            return "{\"Games\":[" +
                   "{\"Game\":\"DCS\",\"DisplayName\":\"DCS World\",\"Installed\":true,\"Tracked\":true,\"State\":\"unchanged\",\"ConfigPath\":\"" + dcs + "\"," +
                   "\"Watch\":[{\"Directory\":\"" + dcs + "\",\"Filters\":[\"*.diff.lua\"],\"IncludeSubdirectories\":true}],\"RegistryKey\":\"\",\"ProcessNames\":[\"joypro-no-such-game-dcs\"]}," +
                   "{\"Game\":\"WarThunder\",\"DisplayName\":\"War Thunder\",\"Installed\":true,\"Tracked\":true,\"State\":\"unchanged\",\"ConfigPath\":\"" + wtFile + "\"," +
                   "\"Watch\":[{\"Directory\":\"" + wt + "\",\"Filters\":[\"machine.blk\"],\"IncludeSubdirectories\":false}],\"RegistryKey\":\"\",\"ProcessNames\":[\"joypro-no-such-game-wt\"]}" +
                   "]}";
        }
    }
}

// The watchers with real file watchers on temp folders: only ticked games get FileSystemWatchers, a tick takes effect at
// once (no engine call), a change in a watched game is reported and one in an unticked game is not, and ticking and
// unticking 200 times leaves the process' handles flat. Process-wide numbers, so in the "ResourceMeasurements" collection.
[SupportedOSPlatform("windows")]
[Collection("ResourceMeasurements")]
public sealed class GameWatchChoiceWatcherTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "joypro-gamewatch-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper output;

    public GameWatchChoiceWatcherTests(ITestOutputHelper output)
    {
        this.output = output;
        Directory.CreateDirectory(Path.Combine(temp, "dcs"));
        Directory.CreateDirectory(Path.Combine(temp, "wt"));
    }

    public void Dispose()
    {
        try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task GuiWatcher_OnlyTickedGamesGetFileWatchers_TicksApplyLive_AndHandlesStayFlat()
    {
        GameWatchChoiceTests.WatchFakeEngine fake = new() { Status = GameWatchChoiceTests.WatchFakeEngine.SyncStatus(Path.Combine(temp, "dcs"), Path.Combine(temp, "wt")) };
        MainWindowViewModel vm = GameWatchChoiceTests.CreateVm(fake, persist: false);
        try
        {
            await vm.RefreshGameCardsCommand.ExecuteAsync(null);
            await vm.StartGameConfigWatcherAsync();
            string dcsKey = SyncOutput.WatchKey("DCS", Path.Combine(temp, "dcs"));
            string wtKey = SyncOutput.WatchKey("WarThunder", Path.Combine(temp, "wt", "machine.blk"));
            Assert.Equal(new[] { dcsKey, wtKey }.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), vm.WatchedConfigKeys);
            int bothHandles = vm.GameConfigWatcherHandles;
            Assert.True(bothHandles >= 2, "one file watcher per game: " + bothHandles);

            // Untick War Thunder: its file watcher is released at once, without asking the engine again.
            int statusCalls = fake.Calls.Count(c => NoviceGuiTests.Seq(c, "sync", "status"));
            vm.GameCards.Single(c => c.GameId == "WarThunder").Watch = false;
            Assert.Equal([dcsKey], vm.WatchedConfigKeys);
            Assert.True(vm.GameConfigWatcherHandles < bothHandles);
            Assert.Contains("Not watched (unticked on the Start page): War Thunder", vm.GameWatchStatus);
            Assert.Equal(statusCalls, fake.Calls.Count(c => NoviceGuiTests.Seq(c, "sync", "status")));

            // The master switch still wins: off releases everything, on watches the ticked games only.
            vm.WatchGameConfigs = false;
            await WaitForAsync(() => vm.GameConfigWatcherHandles == 0);
            Assert.Empty(vm.WatchedConfigKeys);
            vm.WatchGameConfigs = true;
            await WaitForAsync(() => vm.WatchedConfigKeys.Count == 1);
            Assert.Equal([dcsKey], vm.WatchedConfigKeys);

            // 200 x untick / tick: the watchers are released and re-created, the process' handles stay flat.
            GameCardItem warThunder = vm.GameCards.Single(c => c.GameId == "WarThunder");
            warThunder.Watch = true;
            int before = Handles();
            for (int i = 0; i < 200; i++)
            {
                warThunder.Watch = false;
                warThunder.Watch = true;
            }
            int after = Handles();
            output.WriteLine($"200 x untick/tick: handles {before} -> {after}, watcher holds {vm.GameConfigWatcherHandles}");
            Assert.InRange(after - before, -50, 20);
            Assert.Equal(bothHandles, vm.GameConfigWatcherHandles);
            Assert.Equal(2, vm.WatchedConfigKeys.Count);
        }
        finally
        {
            vm.StopGameConfigWatcher();
        }
        Assert.Equal(0, vm.GameConfigWatcherHandles);
    }

    // The tray host with the real GameConfigWatcher: a write to the unticked game's config is never reported, one to the
    // ticked game's is; after the user ticked it in the GUI (and closed it), the other game's writes are reported too.
    [Fact]
    public async Task TrayHost_RealWatcher_ReportsOnlyTickedGames_AndTheNewTicksAfterTheGuiClosed()
    {
        string dcs = Path.Combine(temp, "dcs");
        string wtFile = Path.Combine(temp, "wt", "machine.blk");
        File.WriteAllText(Path.Combine(dcs, "joystick.diff.lua"), "start");
        File.WriteAllText(wtFile, "start");
        TrayHostTests.Rig rig = new(new TraySettings(true, true, true, @"D:\Profiles\main.pr0file") { WatchChoices = GameWatchChoices.Empty.With("WarThunder", false) });
        rig.Engine.Status = GameWatchChoiceTests.WatchFakeEngine.SyncStatus(dcs, Path.Combine(temp, "wt"));
        object gate = new();
        List<string> changed = [];
        List<GameConfigWatcher> real = [];
        TrayWatcherHost host = new(rig.Engine, rig.Settings, rig.Gui, rig.Launcher, rig.View, () =>
        {
            GameConfigWatcher watcher = new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50));
            watcher.ConfigChanged += key => { lock (changed) changed.Add(key); };
            lock (real)
                real.Add(watcher);
            return watcher;
        }, action => { lock (gate) action(); }, null, path => true);
        try
        {
            Task start;
            lock (gate)
                start = host.StartAsync();
            await start;
            Assert.Equal(["DCS|" + dcs], real.Single().WatchedGames);

            File.WriteAllText(wtFile, "changed in War Thunder");
            File.WriteAllText(Path.Combine(dcs, "joystick.diff.lua"), "changed in DCS");
            Assert.True(WaitFor(() => { lock (changed) return changed.Count >= 1; }, 5000), "the DCS change should be reported.");
            Thread.Sleep(600);
            lock (changed)
                Assert.Equal(["DCS|" + dcs], changed.Distinct());

            // The GUI opens (the tray pauses), the user ticks War Thunder, the GUI closes: the tray watches it now.
            rig.Gui.Running = true;
            lock (gate)
                rig.Gui.RaiseStarted();
            Assert.All(real, w => Assert.Equal(0, w.HandleCount));
            rig.Settings.Current = rig.Settings.Current with { WatchChoices = rig.Settings.Current.WatchChoices.With("WarThunder", true) };
            rig.Gui.Running = false;
            lock (gate)
                rig.Gui.RaiseExited();
            Assert.True(WaitFor(() => { lock (real) return real.Count == 2 && real[1].WatchedGames.Count == 2; }, 5000), "the tray host should watch both games again.");
            lock (changed)
                changed.Clear();
            File.WriteAllText(wtFile, "changed again in War Thunder");
            Assert.True(WaitFor(() => { lock (changed) return changed.Contains("WarThunder|" + wtFile); }, 5000), "the War Thunder change should be reported now.");
        }
        finally
        {
            lock (gate)
                host.Dispose();
        }
        Assert.All(real, w => Assert.Equal(0, w.HandleCount));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
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

    private static int Handles()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using Process self = Process.GetCurrentProcess();
        return self.HandleCount;
    }
}
