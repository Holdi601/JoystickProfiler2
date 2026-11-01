using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Every toggle survives a restart (ui-settings.json "layout", MainWindowViewModel.LayoutUiSettings.cs): the Layout
// page's Snap, Grid, Tables, 3D view and Tables and lines, the "Where JoyPro looks" ticks, the picks of Apply Selected,
// the cheat-sheet options and output folder, and Settings > DCS defaults "Keep the UI layer's default binds" while
// stripping is off. Each is changed, the view model is made again, and all come back. Uses the test run's own settings
// folder (JOYPRO_DATA_DIR\settings) in the "TrayMode" collection that owns it.
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class LayoutSettingsPersistTests : IDisposable
{
    private readonly string uiSettings = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");
    private readonly string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "layout-settings-" + Guid.NewGuid().ToString("N"));

    public LayoutSettingsPersistTests()
    {
        Directory.CreateDirectory(AppInfo.SettingsFolder);
        Directory.CreateDirectory(folder);
        File.Delete(uiSettings);
    }

    public void Dispose()
    {
        File.Delete(uiSettings);
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
        }
    }

    private static MainWindowViewModel Vm() =>
        new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };

    [Fact]
    public void LayoutSwitches_CheatSheetOptions_AndPicks_ComeBackAfterARestart()
    {
        MainWindowViewModel first = Vm();
        // JoyPro's defaults first.
        Assert.True(first.LayoutSnapEnabled);
        Assert.False(first.LayoutShowGrid);
        Assert.True(first.LayoutCalloutsEnabled);
        Assert.True(first.WorkspaceShows3D);
        Assert.False(first.LayoutShowOverlays);
        Assert.True(first.LayoutIncludeOverview);

        first.LayoutSnapEnabled = false;
        first.LayoutShowGrid = true;
        first.LayoutCalloutsEnabled = false;
        first.WorkspaceShows3D = false;
        first.LayoutShowOverlays = true;
        first.LayoutSuggestIncludeConnected = false;
        first.LayoutSuggestIncludeProfileDevices = true;
        first.LayoutSuggestApplyToProfile = true;
        first.LayoutIncludeOverview = false;
        first.LayoutIncludePlanes = false;
        first.LayoutSkipMissing = true;
        first.LayoutCombinePages = true;
        first.LayoutDrawMarkers = false;
        first.LayoutDrawLegendTable = false;
        first.LayoutMarkerSize = "14";
        first.LayoutLegendWidth = "520";
        first.LayoutLegendMaxRows = "30";
        first.LayoutLegendFontSize = "12.5";
        first.LayoutButtonSymbol = "square";
        first.LayoutAxisSymbol = "circle";
        first.LayoutPovSymbol = "diamond";
        first.LayoutSliderSymbol = "triangle";
        first.LayoutExportPath = Path.Combine(folder, "Sheets");
        // Apply Selected: the picked picture of a device for the cheat sheets.
        string layout = Path.Combine(folder, "stick.layout");
        File.WriteAllText(layout, "{}");
        first.LayoutSuggestions.Add(new LayoutSuggestionItem { Device = "Smoke Stick", LayoutPath = layout, IsSelected = true });
        first.ApplySelectedLayoutSuggestionsCommand.Execute(null);
        // DCS defaults: the UI layer choice while stripping is off.
        Assert.False(first.DcsStripDefaults);
        first.DcsStripSkipUiLayer = true;

        MainWindowViewModel second = Vm();

        Assert.False(second.LayoutSnapEnabled);
        Assert.True(second.LayoutShowGrid);
        Assert.False(second.LayoutCalloutsEnabled);
        Assert.False(second.WorkspaceShows3D);
        Assert.True(second.LayoutShowOverlays);
        Assert.False(second.LayoutSuggestIncludeConnected);
        Assert.True(second.LayoutSuggestIncludeProfileDevices);
        Assert.True(second.LayoutSuggestApplyToProfile);
        Assert.False(second.LayoutIncludeOverview);
        Assert.False(second.LayoutIncludePlanes);
        Assert.True(second.LayoutSkipMissing);
        Assert.True(second.LayoutCombinePages);
        Assert.False(second.LayoutDrawMarkers);
        Assert.False(second.LayoutDrawLegendTable);
        Assert.Equal("14", second.LayoutMarkerSize);
        Assert.Equal("520", second.LayoutLegendWidth);
        Assert.Equal("30", second.LayoutLegendMaxRows);
        Assert.Equal("12.5", second.LayoutLegendFontSize);
        Assert.Equal("square", second.LayoutButtonSymbol);
        Assert.Equal("circle", second.LayoutAxisSymbol);
        Assert.Equal("diamond", second.LayoutPovSymbol);
        Assert.Equal("triangle", second.LayoutSliderSymbol);
        Assert.Equal(Path.Combine(folder, "Sheets"), second.LayoutExportPath);
        Assert.True(second.DcsStripSkipUiLayer);
        // The pick: the cheat sheets of that device are drawn on the picked picture after the restart too.
        Assert.Contains(second.LayoutMapCsvForTests(), m => m == "Smoke Stick=" + layout);
    }
}
