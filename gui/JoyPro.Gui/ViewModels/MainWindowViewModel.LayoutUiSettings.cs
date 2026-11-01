using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace JoyPro.Gui.ViewModels;

// The Layout page's switches and the cheat-sheet options survive a restart (ui-settings.json "layout"): the picture's
// Snap, Grid, Tables, 3D view and Tables and lines, the "Where JoyPro looks" ticks, the picks of Apply Selected, what to
// make, the look and the output folder. Every change is saved at once (the same file as the other settings).
public sealed partial class MainWindowViewModel
{
    private static readonly HashSet<string> PersistedLayoutProperties = new(StringComparer.Ordinal)
    {
        nameof(LayoutSnapEnabled), nameof(LayoutShowGrid), nameof(LayoutCalloutsEnabled), nameof(WorkspaceShows3D), nameof(LayoutShowOverlays),
        nameof(LayoutSuggestIncludeConnected), nameof(LayoutSuggestIncludeProfileDevices), nameof(LayoutSuggestApplyToProfile),
        nameof(LayoutIncludeOverview), nameof(LayoutIncludePlanes), nameof(LayoutSkipMissing), nameof(LayoutCombinePages),
        nameof(LayoutDrawMarkers), nameof(LayoutDrawLegendTable), nameof(LayoutMarkerSize), nameof(LayoutLegendWidth),
        nameof(LayoutLegendMaxRows), nameof(LayoutLegendFontSize), nameof(LayoutButtonSymbol), nameof(LayoutAxisSymbol),
        nameof(LayoutPovSymbol), nameof(LayoutSliderSymbol), nameof(LayoutExportPath)
    };

    // The pictures picked with Apply Selected (device -> layout file); they go into layoutTemplateByDevice at start.
    private readonly Dictionary<string, string> layoutPicks = new(StringComparer.OrdinalIgnoreCase);

    private void RememberLayoutPick(string device, string layoutPath)
    {
        if (device.Trim().Length > 0 && layoutPath.Trim().Length > 0)
            layoutPicks[device.Trim()] = layoutPath.Trim();
    }

    // A device's pick (null: every pick) goes when its picture is reset or another layout replaces it.
    private void ForgetLayoutPick(string? device)
    {
        bool changed = device == null ? layoutPicks.Count > 0 : layoutPicks.Remove(device.Trim());
        if (device == null)
            layoutPicks.Clear();
        if (changed)
            SaveUiSettings();
    }

    // The device -> layout file map the cheat sheets use ("device=file"), for tests.
    internal System.Collections.Generic.List<string> LayoutMapCsvForTests() => layoutTemplateByDevice.Select(kv => kv.Key + "=" + kv.Value).ToList();

    private void SaveOnPersistedLayoutChange(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != null && PersistedLayoutProperties.Contains(e.PropertyName))
            SaveUiSettings();
    }

    private LayoutUiSettings LayoutUiSettingsForSave() => new()
    {
        Snap = LayoutSnapEnabled,
        Grid = LayoutShowGrid,
        Tables = LayoutCalloutsEnabled,
        View3D = WorkspaceShows3D,
        TablesAndLines = LayoutShowOverlays,
        SuggestConnected = LayoutSuggestIncludeConnected,
        SuggestProfileDevices = LayoutSuggestIncludeProfileDevices,
        SuggestStoreInProfile = LayoutSuggestApplyToProfile,
        Picks = layoutPicks.Count == 0 ? null : new Dictionary<string, string>(layoutPicks, StringComparer.OrdinalIgnoreCase),
        SheetOverview = LayoutIncludeOverview,
        SheetPerAircraft = LayoutIncludePlanes,
        SheetSkipMissing = LayoutSkipMissing,
        SheetAllDevices = LayoutCombinePages,
        SheetMarkers = LayoutDrawMarkers,
        SheetLegend = LayoutDrawLegendTable,
        SheetMarkerSize = LayoutMarkerSize,
        SheetLegendWidth = LayoutLegendWidth,
        SheetLegendRows = LayoutLegendMaxRows,
        SheetLegendFont = LayoutLegendFontSize,
        SheetButtonSymbol = LayoutButtonSymbol,
        SheetAxisSymbol = LayoutAxisSymbol,
        SheetHatSymbol = LayoutPovSymbol,
        SheetSliderSymbol = LayoutSliderSymbol,
        SheetFolder = LayoutExportPath
    };

    // Called while the settings load (loadingUiSettings: nothing is saved meanwhile); a value not in the file keeps
    // JoyPro's default.
    private void ApplyLayoutUiSettings(LayoutUiSettings? s)
    {
        if (s == null)
            return;
        LayoutSnapEnabled = s.Snap ?? LayoutSnapEnabled;
        LayoutShowGrid = s.Grid ?? LayoutShowGrid;
        LayoutCalloutsEnabled = s.Tables ?? LayoutCalloutsEnabled;
        WorkspaceShows3D = s.View3D ?? WorkspaceShows3D;
        LayoutShowOverlays = s.TablesAndLines ?? LayoutShowOverlays;
        LayoutSuggestIncludeConnected = s.SuggestConnected ?? LayoutSuggestIncludeConnected;
        LayoutSuggestIncludeProfileDevices = s.SuggestProfileDevices ?? LayoutSuggestIncludeProfileDevices;
        LayoutSuggestApplyToProfile = s.SuggestStoreInProfile ?? LayoutSuggestApplyToProfile;
        foreach (KeyValuePair<string, string> pick in s.Picks ?? [])
        {
            if (string.IsNullOrWhiteSpace(pick.Key) || string.IsNullOrWhiteSpace(pick.Value))
                continue;
            RememberLayoutPick(pick.Key, pick.Value);
            RememberLayoutDeviceMapping(pick.Key, pick.Value);
        }
        LayoutIncludeOverview = s.SheetOverview ?? LayoutIncludeOverview;
        LayoutIncludePlanes = s.SheetPerAircraft ?? LayoutIncludePlanes;
        LayoutSkipMissing = s.SheetSkipMissing ?? LayoutSkipMissing;
        LayoutCombinePages = s.SheetAllDevices ?? LayoutCombinePages;
        LayoutDrawMarkers = s.SheetMarkers ?? LayoutDrawMarkers;
        LayoutDrawLegendTable = s.SheetLegend ?? LayoutDrawLegendTable;
        LayoutMarkerSize = Text(s.SheetMarkerSize, LayoutMarkerSize);
        LayoutLegendWidth = Text(s.SheetLegendWidth, LayoutLegendWidth);
        LayoutLegendMaxRows = Text(s.SheetLegendRows, LayoutLegendMaxRows);
        LayoutLegendFontSize = Text(s.SheetLegendFont, LayoutLegendFontSize);
        LayoutButtonSymbol = Symbol(s.SheetButtonSymbol, LayoutButtonSymbol);
        LayoutAxisSymbol = Symbol(s.SheetAxisSymbol, LayoutAxisSymbol);
        LayoutPovSymbol = Symbol(s.SheetHatSymbol, LayoutPovSymbol);
        LayoutSliderSymbol = Symbol(s.SheetSliderSymbol, LayoutSliderSymbol);
        LayoutExportPath = s.SheetFolder?.Trim() ?? LayoutExportPath;

        static string Text(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        static string Symbol(string? value, string fallback) =>
            value != null && LayoutSymbols.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase) ? value.Trim().ToLowerInvariant() : fallback;
    }

    // The marker symbols of the cheat sheets' look (the choices of the Layout page).
    private static readonly string[] LayoutSymbols = ["circle", "square", "triangle", "diamond", "bar"];

    private sealed class LayoutUiSettings
    {
        // The picture's switches (Layout page).
        public bool? Snap { get; set; }

        public bool? Grid { get; set; }

        public bool? Tables { get; set; }

        public bool? View3D { get; set; }

        public bool? TablesAndLines { get; set; }

        // "Where JoyPro looks" and the picks of Apply Selected (device -> layout file the cheat sheets use).
        public bool? SuggestConnected { get; set; }

        public bool? SuggestProfileDevices { get; set; }

        public bool? SuggestStoreInProfile { get; set; }

        public Dictionary<string, string>? Picks { get; set; }

        // Cheat sheets: what to make, the look and the output folder (empty = JoyPro's LayoutExport folder).
        public bool? SheetOverview { get; set; }

        public bool? SheetPerAircraft { get; set; }

        public bool? SheetSkipMissing { get; set; }

        public bool? SheetAllDevices { get; set; }

        public bool? SheetMarkers { get; set; }

        public bool? SheetLegend { get; set; }

        public string? SheetMarkerSize { get; set; }

        public string? SheetLegendWidth { get; set; }

        public string? SheetLegendRows { get; set; }

        public string? SheetLegendFont { get; set; }

        public string? SheetButtonSymbol { get; set; }

        public string? SheetAxisSymbol { get; set; }

        public string? SheetHatSymbol { get; set; }

        public string? SheetSliderSymbol { get; set; }

        public string? SheetFolder { get; set; }
    }
}
