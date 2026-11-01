using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// The 2.5.1 batch: hidden games left out of the telemetry force feedback and telemetry recording lists (and not looked
// for there), the Relations page's filter block that folds up (kept over a restart, the closed line names the filters),
// the Check card's "More" button as high as its neighbours, and the not-connected device cards readable in both themes.
// The persistence test writes the test run's list-preferences.json, so the class runs in the TrayMode collection (alone).
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class TesterBatch251Tests
{
    [Theory]
    [InlineData("WarThunder")]
    [InlineData("IL2Korea")]
    public async Task HiddenGames_AreLeftOutOfTheTelemetryLists(string hidden)
    {
        MainWindowViewModel vm = await UiDesignRenderTests.CreateLoadedVmAsync(new UiDesignRenderTests.DesignFakeEngine { HiddenGame = hidden });
        try
        {
            Assert.True(vm.IsGameHidden(hidden));
            // The page lists leave it out; the full lists keep it (its settings stay in the config file).
            Assert.DoesNotContain(vm.VisibleFfbTelemetryGames, g => g.Id == hidden);
            Assert.DoesNotContain(vm.VisibleTelemetryRecordingGames, g => g.Id == hidden);
            Assert.Contains(vm.FfbTelemetryGames, g => g.Id == hidden);
            Assert.Contains(vm.TelemetryRecordingGames, g => g.Id == hidden);
            Assert.Contains(vm.VisibleFfbTelemetryGames, g => g.Id == "DCS");
            Assert.Contains(vm.VisibleTelemetryRecordingGames, g => g.Id == "DCS");
            Assert.NotNull(vm.SelectedFfbTelemetryGame);
            Assert.NotEqual(hidden, vm.SelectedFfbTelemetryGame!.Id);
            // Not waited for either: the idle line names only the shown games' programs.
            Assert.DoesNotContain(vm.FfbTelemetryGames.Single(g => g.Id == hidden).ProcessNames.First() + ".exe", vm.FfbTelemetryStatusText);
        }
        finally
        {
            vm.StopGameConfigWatcher();
        }
    }

    [Fact]
    public async Task HiddenGame_ShownAgain_IsBackInTheTelemetryLists()
    {
        UiDesignRenderTests.DesignFakeEngine fake = new() { HiddenGame = "WarThunder" };
        MainWindowViewModel vm = await UiDesignRenderTests.CreateLoadedVmAsync(fake);
        try
        {
            Assert.DoesNotContain(vm.VisibleFfbTelemetryGames, g => g.Id == "WarThunder");
            fake.HiddenGame = string.Empty;
            await vm.RefreshGameCardsCommand.ExecuteAsync(null);
            Assert.False(vm.IsGameHidden("WarThunder"));
            Assert.Contains(vm.VisibleFfbTelemetryGames, g => g.Id == "WarThunder");
            Assert.Contains(vm.VisibleTelemetryRecordingGames, g => g.Id == "WarThunder");
        }
        finally
        {
            vm.StopGameConfigWatcher();
        }
    }

    [Fact]
    public void RelationFilters_FoldUp_AndStayFoldedAfterARestart()
    {
        string file = ListPreferences.DefaultPath;
        string? before = File.Exists(file) ? File.ReadAllText(file) : null;
        try
        {
            MainWindowViewModel vm = NewVm(new UiDesignRenderTests.DesignFakeEngine(), persisted: true);
            Assert.True(vm.IsRelationFiltersOpen);
            vm.IsRelationFiltersOpen = false;
            MainWindowViewModel restarted = NewVm(new UiDesignRenderTests.DesignFakeEngine(), persisted: true);
            Assert.False(restarted.IsRelationFiltersOpen);
            restarted.IsRelationFiltersOpen = true;
            Assert.True(NewVm(new UiDesignRenderTests.DesignFakeEngine(), persisted: true).IsRelationFiltersOpen);
        }
        finally
        {
            if (before == null)
                File.Delete(file);
            else
                File.WriteAllText(file, before);
        }
    }

    [Fact]
    public async Task RelationFilters_ClosedLine_NamesTheActiveFilters()
    {
        MainWindowViewModel vm = await UiDesignRenderTests.CreateLoadedVmAsync();
        try
        {
            Assert.StartsWith("No filters · ", vm.RelationFiltersSummary);
            int raised = 0;
            vm.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(MainWindowViewModel.RelationFiltersSummary) ? 1 : 0;
            vm.RelationFilter.Bound = "bound";
            vm.RelationFilter.Kind = "axis";
            vm.RelationFilter.SearchScopeIndex = 2;
            Assert.True(raised >= 3);
            Assert.Contains("Bound", vm.RelationFiltersSummary);
            Assert.Contains("Axes", vm.RelationFiltersSummary);
            Assert.Contains("Search: everything", vm.RelationFiltersSummary);
            Assert.Contains(" shown", vm.RelationFiltersSummary);
            Assert.DoesNotContain("No filters", vm.RelationFiltersSummary);
        }
        finally
        {
            vm.RelationFilter.Bound = "all";
            vm.RelationFilter.Kind = "all";
            vm.RelationFilter.SearchScopeIndex = 1;
            vm.StopGameConfigWatcher();
        }
    }

    [Fact]
    public void CheckCard_MoreButton_IsAsHighAsItsNeighbour()
    {
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = NewVm(new DcsDeviceCopiesTests.CopiesFakeEngine());
            AvaloniaRenderThread.Wait(vm.ValidateProfileCommand.ExecuteAsync(null));
            Assert.Contains(vm.CheckSections, s => s.Kind == "copies");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current!.RequestedThemeVariant = theme;
                try
                {
                    using HiddenWindow window = HiddenWindow.Create(new Window { DataContext = vm, Content = new Border { Padding = new Thickness(12), Child = new CheckProblemsPanel { DataContext = vm } } },
                        new Size(1280, 3000));
                    window.Settle();
                    Button more = window.Page.GetVisualDescendants().OfType<Button>()
                        .First(b => b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "More"));
                    // The fixes row (the nearest wrap panel around "More"): the recommended fix with its tick, the others and
                    // "More" with its arrow are all as high.
                    WrapPanel row = more.GetVisualAncestors().OfType<WrapPanel>().First();
                    Button[] buttons = row.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToArray();
                    Assert.True(buttons.Length >= 2);
                    foreach (Button button in buttons)
                        Assert.Equal(more.Bounds.Height, button.Bounds.Height, 0.5);
                    // No ▾ or ✓ character left on the card's fix buttons (they come from a fallback font with a taller line).
                    Assert.DoesNotContain(row.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? string.Empty).IndexOfAny(['▾', '✓']) >= 0);
                }
                finally
                {
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
                }
            }
        });
    }

    private static MainWindowViewModel NewVm(IEngineCliClient engine, bool persisted = false) => new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: persisted)
    {
        EnginePath = typeof(MainWindowViewModel).Assembly.Location,
        ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "tester-batch-251.pr0file")
    };
}
