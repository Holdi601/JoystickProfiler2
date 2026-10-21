using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Settings page as sections that fold, move (drag, menu, tabs) and are searched; the Export page remembering its
// last settings per profile; hiding games on the Start page and showing them again. Fake engine. The tests that keep
// settings use the test run's own settings folder (JOYPRO_DATA_DIR\settings) and run in the "TrayMode" collection
// that owns it.
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class SettingsExportHiddenGamesTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "joypro-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string uiSettings = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");
    private readonly string exportSettings = Path.Combine(AppInfo.SettingsFolder, "export-settings.json");

    public SettingsExportHiddenGamesTests()
    {
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(AppInfo.SettingsFolder);
        File.Delete(uiSettings);
        File.Delete(exportSettings);
    }

    public void Dispose()
    {
        File.Delete(uiSettings);
        File.Delete(exportSettings);
        try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static MainWindowViewModel Vm(IEngineCliClient engine, bool persist) =>
        new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: persist) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };

    // ---- Settings sections ----

    [Fact]
    public void SectionOrder_KeepsTheUsersOrder_PutsNewSectionsWhereTheyBelong_DropsGoneOnes()
    {
        string[] defaults = ["a", "b", "c", "d"];
        Assert.Equal(defaults, SettingsSectionOrder.Merge(defaults, null));
        Assert.Equal(["b", "a", "c", "d"], SettingsSectionOrder.Merge(defaults, ["b", "a", "c", "d"]));
        // d is new in this version: after c, where it stands in JoyPro's order.
        Assert.Equal(["c", "d", "a", "b"], SettingsSectionOrder.Merge(defaults, ["c", "a", "b"]));
        // a is new and first in JoyPro's order: first.
        Assert.Equal(["a", "d", "c", "b"], SettingsSectionOrder.Merge(defaults, ["d", "c", "b"]));
        // Gone and doubled ids are dropped, case does not matter.
        Assert.Equal(["b", "c", "d", "a"], SettingsSectionOrder.Merge(defaults, ["B", "old", "b", "a"]));
    }

    [Fact]
    public void Sections_EveryExistingSettingHasOne_GamesOpenAtFirst_OpenCloseAll()
    {
        MainWindowViewModel vm = Vm(new NoviceGuiTests.NoviceFakeEngine(), persist: false);
        string[] ids = vm.SettingsSections.Select(s => s.Id).ToArray();
        Assert.Equal(MainWindowViewModel.SettingsSectionDefaults.Select(d => d.Id), ids);
        Assert.Contains("confirmations", ids);
        Assert.Contains("about", ids);
        Assert.Equal(["games"], vm.SettingsSections.Where(s => s.IsExpanded).Select(s => s.Id));
        Assert.Equal("1 of 19 sections open", vm.SettingsSectionsSummary);
        Assert.Contains(MainWindowViewModel.LocalAiSectionId, ids);
        Assert.Contains("telemetry-recording", ids);
        Assert.Contains("language", ids);
        // Every section of the page has its item (else it could not fold, move or be found) and the other way round.
        DirectoryInfo? repo = new(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "JoyPro.sln")))
            repo = repo.Parent;
        Assert.NotNull(repo);
        string xaml = File.ReadAllText(Path.Combine(repo!.FullName, "gui", "JoyPro.Gui", "Views", "MainWindow.axaml"));
        string[] onPage = System.Text.RegularExpressions.Regex.Matches(xaml, "<controls:SettingsSection SectionId=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(ids.OrderBy(i => i), onPage.OrderBy(i => i));

        vm.ExpandAllSettingsCommand.Execute(null);
        Assert.All(vm.SettingsSections, s => Assert.True(s.IsContentVisible));
        vm.CollapseAllSettingsCommand.Execute(null);
        Assert.All(vm.SettingsSections, s => Assert.False(s.IsContentVisible));
        vm.ToggleSettingsSectionCommand.Execute(vm.FindSettingsSection("privacy"));
        Assert.True(vm.FindSettingsSection("privacy")!.IsExpanded);

        // Moving: menu entries and drag targets.
        SettingsSectionItem about = vm.FindSettingsSection("about")!;
        vm.MoveSettingsSectionToTopCommand.Execute(about);
        Assert.Equal("about", vm.SettingsSections[0].Id);
        vm.MoveSettingsSectionDownCommand.Execute(about);
        Assert.Equal("about", vm.SettingsSections[1].Id);
        vm.MoveSettingsSectionUpCommand.Execute(about);
        vm.MoveSettingsSectionUpCommand.Execute(about); // already first: stays
        Assert.Equal("about", vm.SettingsSections[0].Id);
        vm.MoveSettingsSectionToBottomCommand.Execute(about);
        Assert.Equal("about", vm.SettingsSections[^1].Id);
        vm.ResetSettingsLayoutCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.SettingsSectionDefaults.Select(d => d.Id), vm.SettingsSections.Select(s => s.Id));
        Assert.Equal(["games"], vm.SettingsSections.Where(s => s.IsExpanded).Select(s => s.Id));

        // The version in the corner opens About on the Settings page.
        vm.OpenAboutCommand.Execute(null);
        Assert.Equal(UiStage.Settings, vm.CurrentStage);
        Assert.True(vm.FindSettingsSection("about")!.IsExpanded);
    }

    [Fact]
    public void Search_ShowsOnlyMatchingSectionsOpen_WithoutChangingWhatTheUserOpened()
    {
        MainWindowViewModel vm = Vm(new NoviceGuiTests.NoviceFakeEngine(), persist: false);
        vm.SettingsSearchText = "tray";
        Assert.True(vm.FindSettingsSection("tray")!.MatchesSearch);
        Assert.True(vm.FindSettingsSection("tray")!.IsContentVisible);
        Assert.False(vm.FindSettingsSection("tray")!.IsExpanded);
        Assert.False(vm.FindSettingsSection("drawings")!.MatchesSearch);
        Assert.False(vm.FindSettingsSection("games")!.MatchesSearch);
        Assert.Contains("matches \"tray\"", vm.SettingsSearchStatus);

        // The texts read from the page count too (SettingsSectionsPanel), every word must match.
        vm.FindSettingsSection("drawings")!.ViewText = "Cheat sheets and exports";
        vm.SettingsSearchText = "cheat sheets";
        Assert.Equal(["drawings"], vm.SettingsSections.Where(s => s.MatchesSearch).Select(s => s.Id));
        vm.SettingsSearchText = "update github";
        Assert.Equal(["about"], vm.SettingsSections.Where(s => s.MatchesSearch).Select(s => s.Id));
        vm.SettingsSearchText = "zzz-nothing";
        Assert.DoesNotContain(vm.SettingsSections, s => s.MatchesSearch);
        Assert.StartsWith("No setting matches", vm.SettingsSearchStatus);

        vm.ClearSettingsSearchCommand.Execute(null);
        Assert.All(vm.SettingsSections, s => Assert.True(s.MatchesSearch));
        Assert.Equal(["games"], vm.SettingsSections.Where(s => s.IsContentVisible).Select(s => s.Id));

        // A tab of a section the search hides: the search is cleared and the section opens.
        vm.SettingsSearchText = "tray";
        vm.OpenSettingsSectionCommand.Execute("devices");
        Assert.Equal(string.Empty, vm.SettingsSearchText);
        Assert.True(vm.FindSettingsSection("devices")!.IsExpanded);
    }

    [Fact]
    public void SectionLayout_IsKeptForTheNextStart()
    {
        MainWindowViewModel first = Vm(new NoviceGuiTests.NoviceFakeEngine(), persist: true);
        first.MoveSettingsSection(first.FindSettingsSection("about")!, 0);
        first.MoveSettingsSection(first.FindSettingsSection("devices")!, 1);
        first.FindSettingsSection("games")!.IsExpanded = false;
        first.FindSettingsSection("privacy")!.IsExpanded = true;
        first.CheckUpdatesOnStartup = false;
        first.UpdateRepository = "someone/JoyFork";
        using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(uiSettings)))
        {
            Assert.Equal("about", json.RootElement.GetProperty("settingsSectionOrder")[0].GetString());
            Assert.Equal(["privacy"], json.RootElement.GetProperty("settingsSectionsOpen").EnumerateArray().Select(e => e.GetString()));
            Assert.False(json.RootElement.GetProperty("checkUpdatesOnStartup").GetBoolean());
        }

        MainWindowViewModel second = Vm(new NoviceGuiTests.NoviceFakeEngine(), persist: true);
        Assert.Equal(["about", "devices", "games"], second.SettingsSections.Take(3).Select(s => s.Id));
        Assert.Equal(["privacy"], second.SettingsSections.Where(s => s.IsExpanded).Select(s => s.Id));
        Assert.False(second.CheckUpdatesOnStartup);
        Assert.Equal("someone/JoyFork", second.UpdateRepository);
        Assert.Contains("github.com/someone/JoyFork/releases", second.UpdateSourceText);
    }

    // The panel stacks the sections in the list's order, folds them, moves one by drag and by its menu, and reads their
    // texts for the search.
    [Fact]
    public void SectionsPanel_OrdersFoldsDragsAndReadsTexts()
    {
        AvaloniaRenderThread.Run(() =>
        {
            ObservableCollection<SettingsSectionItem> items = [new("a", "Alpha", ""), new("b", "Beta", ""), new("c", "Gamma", "")];
            foreach (SettingsSectionItem item in items)
                item.IsExpanded = true;
            SettingsSectionsPanel panel = new() { Sections = items, Spacing = 8 };
            foreach (SettingsSectionItem item in items)
            {
                panel.Children.Add(new SettingsSection
                {
                    SectionId = item.Id,
                    Content = new StackPanel { Children = { new TextBlock { Text = "Setting of " + item.Title, Height = 120 }, new CheckBox { Content = "Option " + item.Id } } }
                });
            }
            Window window = new() { Content = new ScrollViewer { Content = panel }, Width = 800, Height = 900 };
            void Layout()
            {
                for (int i = 0; i < 3; i++)
                {
                    window.Measure(new Size(800, 900));
                    window.Arrange(new Rect(0, 0, 800, 900));
                    foreach (Control child in panel.Children)
                        foreach (Layoutable l in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(child).OfType<Layoutable>().Prepend(child))
                            l.InvalidateMeasure();
                    panel.InvalidateMeasure();
                    panel.Measure(new Size(800, double.PositiveInfinity));
                    panel.Arrange(new Rect(0, 0, 800, panel.DesiredSize.Height));
                    AvaloniaRenderThread.Pump();
                }
            }
            Layout();
            SettingsSection Section(string id) => panel.Children.OfType<SettingsSection>().Single(s => s.SectionId == id);
            Assert.Equal("Alpha", Section("a").Title);
            Assert.True(Section("a").Bounds.Y < Section("b").Bounds.Y && Section("b").Bounds.Y < Section("c").Bounds.Y);
            double open = Section("a").Bounds.Height;

            // The list's order is the page's order.
            items.Move(2, 0);
            Layout();
            Assert.True(Section("c").Bounds.Y < Section("a").Bounds.Y, "c " + Section("c").Bounds + " a " + Section("a").Bounds + " order " + string.Join(",", panel.OrderedChildren().OfType<SettingsSection>().Select(s => s.SectionId)) + " valid " + panel.IsMeasureValid);

            // Closed: only the header.
            items[1].IsExpanded = false;
            Layout();
            Assert.True(Section("a").Bounds.Height < open - 100, "closed " + Section("a").Bounds.Height + " vs open " + open);
            Assert.False(Section("a").IsOpen);

            // Drag c (first) below b (last).
            panel.BeginDrag(Section("c"), new Point(10, Section("c").Bounds.Y + 5));
            panel.DragTo(new Point(10, Section("b").Bounds.Bottom + 4));
            Assert.True(Section("c").IsDragging);
            panel.EndDrag(commit: true);
            Assert.False(Section("c").IsDragging);
            Assert.Equal(["a", "b", "c"], items.Select(i => i.Id));
            // Drag a onto the upper half of c: before c.
            Layout();
            panel.BeginDrag(Section("a"), new Point(10, Section("a").Bounds.Y + 5));
            panel.DragTo(new Point(10, Section("c").Bounds.Y + 3));
            panel.EndDrag(commit: true);
            Assert.Equal(["b", "a", "c"], items.Select(i => i.Id));
            // A drag that was cancelled (capture lost) moves nothing.
            Layout();
            panel.BeginDrag(Section("b"), new Point(10, Section("b").Bounds.Y + 5));
            panel.DragTo(new Point(10, Section("c").Bounds.Bottom + 4));
            panel.EndDrag(commit: false);
            Assert.Equal(["b", "a", "c"], items.Select(i => i.Id));
            // The menu: move down / to the top.
            panel.MoveBy(Section("b"), 1);
            Assert.Equal(["a", "b", "c"], items.Select(i => i.Id));
            panel.MoveTo(Section("c"), 0);
            Assert.Equal(["c", "a", "b"], items.Select(i => i.Id));

            // A section the search hides takes no room.
            items[2].MatchesSearch = false;
            Layout();
            Assert.False(Section("b").IsVisible);

            // The texts of the settings in it.
            string text = Section("a").CollectSearchText();
            Assert.Contains("Setting of Alpha", text);
            Assert.Contains("Option a", text);

            // The window goes away: the sections let go of the items.
            window.Content = null;
            Assert.Null(Section("a").Item);
        });
    }

    // ---- The Export page remembers ----

    [Fact]
    public async Task ExportSettings_ModeOptionsPathsAndTheUsersTicks_ComeBackPerProfile()
    {
        string profileA = Path.Combine(temp, "a.pr0file");
        string profileB = Path.Combine(temp, "b.pr0file");
        File.WriteAllText(profileA, "{}");
        File.WriteAllText(profileB, "{}");
        string dcsFolder = Path.Combine(temp, "Saved Games", "DCS");
        Directory.CreateDirectory(dcsFolder);
        ExportFakeEngine engine = new();

        MainWindowViewModel first = Vm(engine, persist: true);
        first.ExportMode = "clean";
        first.ExportKeepKeyboardDefaults = false;
        first.ExportStarCitizenPreferLocalOrder = false;
        first.ExportDcsPath = dcsFolder;
        first.ExportIl2Path = Path.Combine(temp, "no-such-folder");
        first.ImportIncludeDisconnected = true;
        Assert.False(first.ImportIncludeSimDefaults);
        first.ImportIncludeSimDefaults = true;
        Assert.False(first.ValidationShowWarnings);
        first.ValidationShowWarnings = true;
        first.ValidationShowErrors = false;
        first.ProfilePath = profileA;
        await first.RefreshExportGamesCommand.ExecuteAsync(null);
        Assert.All(first.ExportGames, g => Assert.True(g.IsSelected));
        first.ExportGames.Single(g => g.GameId == "WarThunder").IsSelected = false;
        first.ExportGames.Single(g => g.GameId == "DCS").Planes.Single(p => p.Plane == "F-16C_50").IsSelected = false;
        Assert.Contains("WarThunder=False", first.RememberedExportSummary());

        // "Export" on a card ticks only that game for now: not remembered.
        await first.ExportGameCardCommand.ExecuteAsync(first.GameCards.FirstOrDefault() ?? new GameCardItem("DCS", "DCS World", "dcs", "dcs", true, true));
        Assert.Contains("WarThunder=False", first.RememberedExportSummary());
        Assert.DoesNotContain("DCS=", first.RememberedExportSummary());

        // A dry-run preview switches the dry run on for a moment: not remembered.
        await first.PreviewExportGameCommand.ExecuteAsync(first.ExportGames.Single(g => g.GameId == "WarThunder"));
        Assert.False(ExportSettingsStore.Load(exportSettings).RegistryDryRun);

        // The next start: mode, options and the DCS folder come back; the missing IL-2 folder does not.
        MainWindowViewModel second = Vm(engine, persist: true);
        Assert.Equal("clean", second.ExportMode);
        Assert.Equal("clean", second.SelectedExportModeOption!.Key);
        Assert.False(second.ExportKeepKeyboardDefaults);
        Assert.False(second.ExportStarCitizenPreferLocalOrder);
        Assert.True(second.ExportDcsCleanAllAircraft);
        Assert.True(second.ImportIncludeDisconnected);
        Assert.True(second.ImportIncludeSimDefaults);
        Assert.True(second.ValidationShowWarnings);
        Assert.False(second.ValidationShowErrors);
        Assert.Equal(dcsFolder, second.ExportDcsPath);
        Assert.NotEqual(Path.Combine(temp, "no-such-folder"), second.ExportIl2Path);

        // Profile A: the user's ticks; profile B: JoyPro's.
        second.ProfilePath = profileA;
        await second.RefreshExportGamesCommand.ExecuteAsync(null);
        Assert.False(second.ExportGames.Single(g => g.GameId == "WarThunder").IsSelected);
        Assert.True(second.ExportGames.Single(g => g.GameId == "DCS").IsSelected);
        Assert.False(second.ExportGames.Single(g => g.GameId == "DCS").Planes.Single(p => p.Plane == "F-16C_50").IsSelected);
        Assert.True(second.ExportGames.Single(g => g.GameId == "DCS").Planes.Single(p => p.Plane == "FA-18C_hornet").IsSelected);
        second.ProfilePath = profileB;
        await second.RefreshExportGamesCommand.ExecuteAsync(null);
        Assert.All(second.ExportGames, g => Assert.True(g.IsSelected));
        Assert.All(second.ExportGames.SelectMany(g => g.Planes), p => Assert.True(p.IsSelected));

        // Ticked again as JoyPro would: forgotten (the tick follows the game again).
        second.ProfilePath = profileA;
        await second.RefreshExportGamesCommand.ExecuteAsync(null);
        second.ExportGames.Single(g => g.GameId == "WarThunder").IsSelected = true;
        Assert.DoesNotContain("WarThunder", second.RememberedExportSummary());

        // A broken file gives the defaults.
        File.WriteAllText(exportSettings, "{ not json");
        MainWindowViewModel third = Vm(engine, persist: true);
        Assert.Equal("merge", third.ExportMode);
    }

    [Fact]
    public void ExportSettingsStore_KeepsTheLastProfiles_AndSetTickForgetsDefaults()
    {
        ExportSettings settings = new();
        for (int i = 0; i < ExportSettingsStore.MaxProfiles + 5; i++)
        {
            ProfileExportTicks ticks = ExportSettingsStore.For(settings, Path.Combine(temp, "p" + i + ".pr0file"));
            ticks.LastUsed = new DateTime(2026, 1, 1).AddDays(i);
            ExportSettingsStore.SetTick(ticks.Games, "DCS", false, defaultValue: true);
        }
        string file = Path.Combine(temp, "export-settings.json");
        ExportSettingsStore.Save(file, settings);
        ExportSettings loaded = ExportSettingsStore.Load(file);
        Assert.Equal(ExportSettingsStore.MaxProfiles, loaded.Profiles.Count);
        Assert.DoesNotContain(ExportSettingsStore.ProfileKey(Path.Combine(temp, "p0.pr0file")), loaded.Profiles.Keys);
        ProfileExportTicks last = ExportSettingsStore.For(loaded, Path.Combine(temp, "P54.PR0FILE"));
        Assert.False(last.Games["dcs"]);
        ExportSettingsStore.SetTick(last.Games, "DCS", true, defaultValue: true);
        Assert.Empty(last.Games);
    }

    // ---- Hidden games ----

    [Fact]
    public async Task HideAGameCard_ItLeavesCardsCountsWatcherAndExport_ShowAgainBringsItBack()
    {
        ExportFakeEngine engine = new();
        MainWindowViewModel vm = Vm(engine, persist: false);
        vm.ProfilePath = Path.Combine(temp, "a.pr0file");
        File.WriteAllText(vm.ProfilePath, "{}");
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        await vm.RefreshExportGamesCommand.ExecuteAsync(null);
        Assert.Contains(vm.GameCards, c => c.GameId == "WarThunder");
        Assert.Equal(2, vm.InstalledGameCount);
        Assert.False(vm.HasHiddenGames);
        Assert.Contains(vm.ExportGames, g => g.GameId == "WarThunder");

        await vm.HideGameCardCommand.ExecuteAsync(vm.GameCards.Single(c => c.GameId == "WarThunder"));
        Assert.Contains(engine.Calls, c => AxisTuningTests.Seq(c, "games", "install", "disable", "WarThunder"));
        Assert.DoesNotContain(vm.GameCards, c => c.GameId == "WarThunder");
        Assert.Equal(1, vm.InstalledGameCount);
        Assert.True(vm.HasHiddenGames);
        Assert.Equal("Hidden games (1)", vm.HiddenGamesTitle);
        Assert.Equal("WarThunder", Assert.Single(vm.HiddenGameInstalls).GameId);
        Assert.True(vm.IsGameHidden("WarThunder"));
        Assert.Contains("is hidden", vm.StatusText);
        // The export tiles and the setup suggestions follow (the profile's binds for it stay: nothing else is called).
        await WaitForAsync(() => vm.ExportGames.All(g => g.GameId != "WarThunder"));
        Assert.Contains("Hidden", vm.ExportGamesNotInstalled);
        Assert.DoesNotContain(engine.Calls, c => c.Contains("remove") || c.Contains("delete"));

        await vm.ShowHiddenGameCommand.ExecuteAsync(vm.HiddenGameInstalls.Single());
        Assert.Contains(engine.Calls, c => AxisTuningTests.Seq(c, "games", "install", "enable", "WarThunder"));
        Assert.Contains(vm.GameCards, c => c.GameId == "WarThunder");
        Assert.False(vm.HasHiddenGames);
        await WaitForAsync(() => vm.ExportGames.Any(g => g.GameId == "WarThunder"));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    // engine games installs (DCS and War Thunder installed; hidden ones Disabled), games install enable|disable, games
    // integrations, profile games (DCS with two aircraft, War Thunder).
    private sealed class ExportFakeEngine : IEngineCliClient
    {
        private readonly HashSet<string> hidden = new(StringComparer.OrdinalIgnoreCase);

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            string output = "{}";
            if (AxisTuningTests.Seq(arguments, "games", "install", "disable"))
            {
                lock (hidden)
                    hidden.Add(arguments[arguments.ToList().IndexOf("disable") + 1]);
                output = Installs();
            }
            else if (AxisTuningTests.Seq(arguments, "games", "install", "enable"))
            {
                lock (hidden)
                    hidden.Remove(arguments[arguments.ToList().IndexOf("enable") + 1]);
                output = Installs();
            }
            else if (AxisTuningTests.Seq(arguments, "games", "installs"))
            {
                output = Installs();
            }
            else if (AxisTuningTests.Seq(arguments, "games", "integrations"))
            {
                output = "{\"Integrations\":[{\"GameId\":\"WarThunder\",\"DisplayName\":\"War Thunder\",\"CommandName\":\"warthunder\",\"Aliases\":[\"warthunder\"],\"Notes\":\"\",\"SupportsCatalogRefresh\":true}]}";
            }
            else if (AxisTuningTests.Seq(arguments, "profile", "games"))
            {
                bool wtHidden;
                lock (hidden)
                    wtHidden = hidden.Contains("WarThunder");
                output = "{\"Games\":[" +
                         "{\"GameId\":\"DCS\",\"Name\":\"DCS World\",\"Exporter\":\"dcs\",\"Relations\":3,\"Bound\":3,\"Installed\":true," +
                         "\"Planes\":[{\"Plane\":\"FA-18C_hornet\",\"Relations\":2,\"Bound\":2},{\"Plane\":\"F-16C_50\",\"Relations\":1,\"Bound\":1}]}," +
                         "{\"GameId\":\"WarThunder\",\"Name\":\"War Thunder\",\"Exporter\":\"registry\",\"Relations\":2,\"Bound\":1,\"Installed\":" + (wtHidden ? "false" : "true") +
                         ",\"Disabled\":" + (wtHidden ? "true" : "false") + "}]}";
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

        private string Installs()
        {
            string Game(string id, string name, bool installed)
            {
                bool off;
                lock (hidden)
                    off = hidden.Contains(id);
                return "{\"GameId\":\"" + id + "\",\"Name\":\"" + name + "\",\"Installed\":" + (installed && !off ? "true" : "false") + ",\"Disabled\":" + (off ? "true" : "false") +
                       ",\"InstallPath\":\"\",\"Source\":\"test\",\"ConfigPaths\":[\"C:\\\\cfg\"]}";
            }
            return "{\"Games\":[" + Game("DCS", "DCS World", true) + "," + Game("WarThunder", "War Thunder", true) + "," + Game("IL2Game", "IL-2 Sturmovik: Great Battles", false) + "]}";
        }
    }
}
