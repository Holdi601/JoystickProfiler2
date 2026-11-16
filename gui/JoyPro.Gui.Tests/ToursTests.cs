using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Services.Help;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// The welcome and the tours (docs/ui/help-and-tours.md): asked once per user (also after an update from 2.6), never on
// sandbox runs, always with --welcome; the answer suggested from the installed games; tours that wait for the user's
// click or page change, keep their step over a restart, and find their controls whatever the bar order, hidden pages,
// size and scale. The tests that keep settings use the test run's own settings folder ("TrayMode" collection).
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class ToursTests : IDisposable
{
    private readonly string uiSettings = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");

    public ToursTests()
    {
        Directory.CreateDirectory(AppInfo.SettingsFolder);
        File.Delete(uiSettings);
    }

    public void Dispose() => File.Delete(uiSettings);

    private static MainWindowViewModel Vm(bool persist, bool allowWelcome = true) =>
        new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: persist)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location,
            WelcomeIgnoresSandbox = allowWelcome
        };

    [Fact]
    public void Welcome_OncePerUser_RememberedAfterARestart()
    {
        MainWindowViewModel first = Vm(persist: true);
        first.InstalledGamesForWelcome = () => ["DCS"];
        Assert.True(first.ShouldShowWelcome);
        first.ShowWelcomeIfNeeded();
        Assert.True(first.IsWelcomeOpen);
        first.SkipWelcomeCommand.Execute(null);
        Assert.False(first.IsWelcomeOpen);

        MainWindowViewModel second = Vm(persist: true);
        Assert.False(second.ShouldShowWelcome);
        second.ShowWelcomeIfNeeded();
        Assert.False(second.IsWelcomeOpen);
        Assert.Equal(TourCatalog.NoTour, second.ToursState.Choice);
    }

    // Someone coming from 2.6: settings of their own, the game setup done, but no answer to the welcome yet.
    [Fact]
    public void Welcome_ShowsOnceForUpgradersFrom26()
    {
        File.WriteAllText(uiSettings, "{\"lastProfilePath\":\"\",\"gameSetupDone\":true,\"themeMode\":\"Dark\"}");
        MainWindowViewModel vm = Vm(persist: true);
        Assert.True(vm.ShouldShowWelcome);
        vm.InstalledGamesForWelcome = () => ["IL2Game"];
        vm.OpenWelcome();
        vm.WelcomeOptions.Single(o => o.Id == TourCatalog.JoyPro1).IsSelected = true;
        foreach (WelcomeOption other in vm.WelcomeOptions.Where(o => o.Id != TourCatalog.JoyPro1))
            other.IsSelected = false;
        vm.StartWelcomeChoiceCommand.Execute(null);
        Assert.True(vm.IsTourActive);
        Assert.Equal(TourCatalog.JoyPro1, vm.ActiveTour!.Id);
        Assert.Contains("\"welcomeDone\": true", File.ReadAllText(uiSettings));
        Assert.False(Vm(persist: true).ShouldShowWelcome);
        // The rest of the 2.6 settings stay.
        Assert.Contains("\"themeMode\": \"Dark\"", File.ReadAllText(uiSettings));
    }

    [Fact]
    public void Welcome_NeverOnSandboxOrTestRuns()
    {
        // The test run has a sandbox (JOYPRO_TEST_SANDBOX): without the test's own switch, no welcome.
        Assert.True(UserFolders.IsSandboxed);
        Assert.False(Vm(persist: true, allowWelcome: false).ShouldShowWelcome);
        // A view model without its own settings (every other test) never shows it either.
        Assert.False(Vm(persist: false).ShouldShowWelcome);
    }

    [Fact]
    public void WelcomeOption_AlwaysOpensTheWelcome_AlsoWhenAnswered()
    {
        Assert.True(StartupOptions.Parse(["--welcome"]).ShowWelcome);
        Assert.True(StartupOptions.Parse(["/tour"]).ShowWelcome);
        Assert.False(StartupOptions.Parse(["--tray"]).ShowWelcome);
        Assert.False(StartupOptions.Parse([]).ShowWelcome);

        MainWindowViewModel vm = Vm(persist: true);
        vm.ShowWelcomeIfNeeded();
        vm.SkipWelcomeCommand.Execute(null);
        MainWindowViewModel again = Vm(persist: true, allowWelcome: false);
        Assert.False(again.ShouldShowWelcome);
        again.RequestWelcome();
        Assert.True(again.IsWelcomeOpen);

        // The "JoyPro 2 - Tour" entry next to the exe starts JoyPro with --welcome.
        string? cmd = FindRepoFile(Path.Combine("gui", "JoyPro.Gui", "Resources", "JoyPro 2 - Tour.cmd"));
        Assert.NotNull(cmd);
        Assert.Contains("JoyPro.Gui.exe\" --welcome", File.ReadAllText(cmd!));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "JoyPro 2 - Tour.cmd")) ||
                    File.Exists(Path.Combine(Path.GetDirectoryName(typeof(MainWindowViewModel).Assembly.Location)!, "JoyPro 2 - Tour.cmd")));
    }

    [Theory]
    [InlineData(new[] { "AceCombat8" }, TourCatalog.Pad)]
    [InlineData(new[] { "AceCombat7", "HLLVietnam" }, TourCatalog.Pad)]
    [InlineData(new[] { "DCS" }, TourCatalog.Dcs)]
    [InlineData(new[] { "AceCombat8", "DCS", "WarThunder" }, TourCatalog.Dcs)]
    [InlineData(new[] { "FalconBMS" }, TourCatalog.Dcs)]
    [InlineData(new[] { "IL2Game" }, TourCatalog.Quick)]
    [InlineData(new[] { "AceCombat8", "WarThunder" }, TourCatalog.Quick)]
    [InlineData(new string[0], TourCatalog.Quick)]
    public void WelcomePath_SuggestedFromTheInstalledGames(string[] games, string expected)
    {
        Assert.Equal(expected, TourCatalog.SuggestPath(games));
        MainWindowViewModel vm = Vm(persist: false);
        vm.InstalledGamesForWelcome = () => games;
        vm.OpenWelcome();
        WelcomeOption picked = vm.WelcomeOptions.Single(o => o.IsSelected);
        Assert.Equal(expected, picked.Id);
        Assert.True(picked.IsSuggested);
        // Every answer is there to pick another: pad, DCS, quick, new, JoyPro 1, no tour.
        Assert.Equal([TourCatalog.Pad, TourCatalog.Dcs, TourCatalog.Quick, TourCatalog.Newcomer, TourCatalog.JoyPro1, TourCatalog.NoTour],
            vm.WelcomeOptions.Select(o => o.Id).ToArray());
        Assert.False(string.IsNullOrWhiteSpace(vm.WelcomeSuggestion));
    }

    [Fact]
    public void Essentials_HasFewerSteps_ThanEveryFeature()
    {
        foreach (TourDefinition tour in TourCatalog.Paths)
        {
            Assert.True(tour.StepsFor(false).Count <= tour.StepsFor(true).Count, tour.Id);
            Assert.NotEmpty(tour.StepsFor(false));
        }
        Assert.True(TourCatalog.Find(TourCatalog.Dcs)!.StepsFor(false).Count < TourCatalog.Find(TourCatalog.Dcs)!.StepsFor(true).Count);
        foreach (string page in TourCatalog.PageTourPages)
            Assert.True(TourCatalog.PageTour(page)!.Steps.Count >= 2, page);
    }

    [Fact]
    public void Tour_WaitsForThePageClick_ThenGoesOn_BackNextSkipEnd()
    {
        MainWindowViewModel vm = Vm(persist: false);
        vm.StartTour(TourCatalog.Newcomer, everyFeature: false);
        Assert.True(vm.IsTourActive);
        Assert.True(vm.TourIsBig);
        // Go to "Now click Relations": the tour waits for that page.
        while (vm.CurrentTourStep!.WaitStage != UiStage.Relations)
            vm.TourNextCommand.Execute(null);
        int waiting = vm.TourStepIndex;
        Assert.Equal("nav:relations", vm.TourTargetKey);
        Assert.True(vm.TourWaits);
        vm.CurrentStage = UiStage.Binds;
        Assert.Equal(waiting, vm.TourStepIndex);
        vm.CurrentStage = UiStage.Relations;
        Assert.Equal(waiting + 1, vm.TourStepIndex);
        vm.TourBackCommand.Execute(null);
        Assert.Equal(waiting, vm.TourStepIndex);
        // Next on a waiting step whose page is already open goes on; Skip leaves a step out.
        vm.TourNextCommand.Execute(null);
        Assert.Equal(waiting + 1, vm.TourStepIndex);
        vm.TourSkipCommand.Execute(null);
        Assert.Equal(waiting + 2, vm.TourStepIndex);
        // A click-waiting step goes on after the click.
        while (!vm.CurrentTourStep!.WaitClick)
            vm.TourSkipCommand.Execute(null);
        int click = vm.TourStepIndex;
        vm.TourTargetClicked();
        Assert.Equal(click + 1, vm.TourStepIndex);
        vm.EndTourCommand.Execute(null);
        Assert.False(vm.IsTourActive);
    }

    [Fact]
    public void TourProgressAndSeen_SurviveARestart()
    {
        MainWindowViewModel vm = Vm(persist: true);
        vm.StartTour(TourCatalog.Dcs, everyFeature: true);
        vm.TourNextCommand.Execute(null);
        vm.TourNextCommand.Execute(null);
        Assert.Equal(2, vm.TourStepIndex);

        MainWindowViewModel restarted = Vm(persist: true);
        Assert.False(restarted.IsTourActive);
        restarted.ResumeSavedTour();
        Assert.True(restarted.IsTourActive);
        Assert.Equal(TourCatalog.Dcs, restarted.ActiveTour!.Id);
        Assert.Equal(2, restarted.TourStepIndex);
        while (restarted.IsTourActive)
            restarted.TourSkipCommand.Execute(null);

        MainWindowViewModel third = Vm(persist: true);
        third.ResumeSavedTour();
        Assert.False(third.IsTourActive);
        Assert.Contains(third.TourList, t => t.Id == TourCatalog.Dcs && t.Seen);
        // Settings > Tours: essentials / every feature is kept too.
        third.ToursEveryFeature = true;
        Assert.True(Vm(persist: true).ToursEveryFeature);
    }

    // The tour's controls are found by name, command, tab or section: the user's bar order, a hidden page, the window
    // size and the UI scale do not matter; a hidden page's step goes on with Next (which opens the page).
    [Fact]
    public void TourTargets_FoundWhateverTheBarOrderSizeAndScale()
    {
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                vm.MoveNavPage(vm.FindNavPage("export")!, 0);
                vm.MoveNavPage(vm.FindNavPage("relations")!, 9);
                vm.UiScale = 1.5;
                vm.StartTour(TourCatalog.Newcomer, everyFeature: true);
                while (vm.CurrentTourStep!.WaitStage != UiStage.Relations)
                    vm.TourNextCommand.Execute(null);
                foreach ((int w, int h) in new[] { (1280, 800), (1920, 1080), (2560, 1440) })
                {
                    MainWindow main = HelpModeTests.Layout(vm, w, h);
                    HelpLayer layer = ((Control)main.Content!).GetVisualDescendants().OfType<HelpLayer>().Single();
                    Control? target = layer.FindTourTarget();
                    Assert.NotNull(target);
                    Assert.Equal("Nav_relations", target!.Name);
                    layer.Place();
                    HelpModeTests.Relayout(main, w, h);
                    Border outline = layer.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TourOutline");
                    Assert.True(outline.IsVisible);
                    Border card = layer.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TourCard");
                    Avalonia.Rect cardRect = new(card.TranslatePoint(new Avalonia.Point(0, 0), layer)!.Value, card.Bounds.Size);
                    Avalonia.Rect navRect = new(target.TranslatePoint(new Avalonia.Point(0, 0), layer)!.Value, target.Bounds.Size);
                    Assert.False(cardRect.Intersects(navRect), "the tour card covers its button at " + w);
                    Assert.True(new Avalonia.Rect(layer.Bounds.Size).Contains(cardRect), "the tour card is cut off at " + w);
                    main.DataContext = null;
                }

                // Relations hidden in the bar: no button to point at; Next opens the page and the tour goes on.
                vm.FindNavPage("relations")!.IsVisible = false;
                MainWindow hidden = HelpModeTests.Layout(vm, 1280, 800);
                HelpLayer hiddenLayer = ((Control)hidden.Content!).GetVisualDescendants().OfType<HelpLayer>().Single();
                Assert.Null(hiddenLayer.FindTourTarget());
                int step = vm.TourStepIndex;
                vm.TourNextCommand.Execute(null);
                Assert.Equal(UiStage.Relations, vm.CurrentStage);
                Assert.Equal(step + 1, vm.TourStepIndex);
                vm.EndTourCommand.Execute(null);
            }
            finally
            {
                vm.UiScale = 1.0;
                vm.StopGameConfigWatcher();
            }
        });
    }

    private static string? FindRepoFile(string relative)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
