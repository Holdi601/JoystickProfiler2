using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// The redesigned Layout page: canvas modes, undo/redo of marker edits, what the markers do (legend), binding on the
// picture, the cheat sheet options and previews, and the picture canvas' snapping. The engine is the fake of the layout
// render test (a VKB Gunfighter layout with 47 markers for the stick of the design profile).
[SupportedOSPlatform("windows")]
public sealed class LayoutWorkspaceTests
{
    private const string Stick = AxisTuningTests.Stick;

    // layoutFile: show the stick's layout file (47 label markers); else its device reference picture.
    private static async Task<(MainWindowViewModel Vm, LayoutRedesignRenderTests.LayoutFakeEngine Fake, string Work)> CreateLoadedAsync(bool layoutFile = true)
    {
        string work = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "layout-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        // No picture file: unit tests have no picture decoder (the canvas size comes from the layout).
        string picture = Path.Combine(work, "no-picture.png");
        LayoutRedesignRenderTests.LayoutFakeEngine fake = new(picture, work);
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        string profile = Path.Combine(work, "My Sticks.pr0file");
        File.WriteAllText(profile, "{}");
        vm.ProfilePath = profile;
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        await vm.SuggestLayoutsCommand.ExecuteAsync(null);
        vm.SelectedLayoutSuggestion = vm.LayoutSuggestions.Single(s => s.Device == Stick);
        await LayoutRedesignRenderTests.WaitForAsync(() => vm.LayoutEditorMarkers.Count == 47 && vm.HasReferenceModel);
        Assert.Equal(47, vm.LayoutEditorMarkers.Count);
        Assert.True(vm.HasReferenceModel);
        // A shipped template (source catalog) is not the user's own layout: the reference picture comes first.
        Assert.True(vm.LayoutShowsReference);
        vm.LayoutShowsLayoutFile = layoutFile;
        return (vm, fake, work);
    }

    private static LayoutEditorMarkerItem Marker(MainWindowViewModel vm, string token) => vm.LayoutEditorMarkers.Single(m => m.Token == token);

    [Fact]
    public void Modes_SwitchByCommand_AndLeaveThePlacementToolsOffOutsideEdit()
    {
        MainWindowViewModel vm = new(new LayoutRedesignRenderTests.LayoutFakeEngine("none.png", Path.GetTempPath()), new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
        Assert.True(vm.IsLayoutViewMode);
        Assert.False(vm.LayoutPlaceMarkerMode);

        vm.SetLayoutCanvasModeCommand.Execute("edit");
        Assert.True(vm.IsLayoutEditMode);
        Assert.Equal(DeviceCanvasMode.EditMarkers, vm.LayoutCanvasMode);
        vm.LayoutPlaceMarkerMode = true;

        vm.SetLayoutCanvasModeCommand.Execute("bind");
        Assert.True(vm.IsLayoutBindMode);
        Assert.False(vm.LayoutPlaceMarkerMode);

        vm.SetLayoutCanvasModeCommand.Execute("anything else");
        Assert.True(vm.IsLayoutViewMode);

        vm.ShowCheatSheetsSectionCommand.Execute(null);
        Assert.True(vm.IsCheatSheetsSection);
        Assert.False(vm.IsDevicePicturesSection);
        vm.ShowDevicePicturesSectionCommand.Execute(null);
        Assert.True(vm.IsDevicePicturesSection);
    }

    [Fact]
    public async Task Markers_ShowWhatTheInputDoes_AndTheLegendFilters()
    {
        (MainWindowViewModel vm, _, _) = await CreateLoadedAsync();

        Assert.Equal("Weapon Release", Marker(vm, "JOY_BTN2").Command);
        Assert.Equal("Gun Trigger", Marker(vm, "JOY_BTN1").Command);
        Assert.Equal("Pitch", Marker(vm, "JOY_Y").Command);
        Assert.Equal(string.Empty, Marker(vm, "JOY_BTN3").Command);
        Assert.Equal("Test Stick", vm.LayoutWorkspaceDeviceTitle);
        Assert.EndsWith("inputs bound", vm.LayoutLegendSummary, StringComparison.Ordinal);
        Assert.StartsWith(vm.LayoutEditorMarkers.Count(m => m.IsBound) + " of 47", vm.LayoutLegendSummary, StringComparison.Ordinal);

        // Axes first, then sliders, buttons by number, hats.
        Assert.Equal("JOY_RX", vm.LayoutLegendItems[0].Token);
        List<string> buttons = vm.LayoutLegendItems.Where(m => m.Type == "button").Select(m => m.Token).ToList();
        Assert.True(buttons.IndexOf("JOY_BTN2") < buttons.IndexOf("JOY_BTN10"));

        vm.LayoutLegendFilter = "weapon";
        LayoutEditorMarkerItem row = Assert.Single(vm.LayoutLegendItems);
        Assert.Equal("JOY_BTN2", row.Token);
        vm.LayoutLegendFilter = string.Empty;
        Assert.Equal(47, vm.LayoutLegendItems.Count);
    }

    [Fact]
    public async Task MarkerMoves_AreUndoneAndRedone_OnlyInEditMode()
    {
        (MainWindowViewModel vm, _, _) = await CreateLoadedAsync();
        LayoutEditorMarkerItem marker = Marker(vm, "JOY_BTN5");
        double x = marker.X;
        double y = marker.Y;

        // View mode: the canvas never moves markers.
        vm.LayoutCanvasMarkerMovedCommand.Execute(new DeviceMarkerMove(marker, x, y, x + 50, y + 20));
        Assert.Equal(x, marker.X);
        Assert.False(vm.CanUndoLayoutEdit);
        Assert.False(vm.LayoutEditorHasUnsavedChanges);

        vm.SetLayoutCanvasModeCommand.Execute("edit");
        vm.LayoutCanvasMarkerMovedCommand.Execute(new DeviceMarkerMove(marker, x, y, x + 50, y + 20));
        Assert.Equal(x + 50, marker.X, 2);
        Assert.Equal(y + 20, marker.Y, 2);
        Assert.True(vm.CanUndoLayoutEdit);
        Assert.True(vm.LayoutEditorHasUnsavedChanges);
        Assert.Contains("move BTN 5", vm.LayoutUndoTip, StringComparison.Ordinal);

        vm.UndoLayoutEditCommand.Execute(null);
        Assert.Equal(x, marker.X, 2);
        Assert.Equal(y, marker.Y, 2);
        Assert.False(vm.CanUndoLayoutEdit);
        Assert.True(vm.CanRedoLayoutEdit);

        vm.RedoLayoutEditCommand.Execute(null);
        Assert.Equal(x + 50, marker.X, 2);
        Assert.True(vm.CanUndoLayoutEdit);
        Assert.False(vm.CanRedoLayoutEdit);
    }

    [Fact]
    public async Task AddAndRemove_AreUndone_WithTheSameInputAndPlace()
    {
        (MainWindowViewModel vm, _, _) = await CreateLoadedAsync();
        vm.SetLayoutCanvasModeCommand.Execute("edit");

        vm.SelectedLayoutEditorMarker = null;
        vm.LayoutEditorNewToken = "JOY_BTN99";
        vm.LayoutEditorNewType = "button";
        vm.LayoutPlaceMarkerMode = true;
        vm.LayoutCanvasClickedCommand.Execute(new DeviceCanvasClick(200, 300, false));
        Assert.Equal(48, vm.LayoutEditorMarkers.Count);
        LayoutEditorMarkerItem added = Marker(vm, "JOY_BTN99");
        Assert.Equal(200, added.X);
        Assert.Same(added, vm.SelectedLayoutEditorMarker);

        vm.UndoLayoutEditCommand.Execute(null);
        Assert.Equal(47, vm.LayoutEditorMarkers.Count);
        Assert.DoesNotContain(vm.LayoutEditorMarkers, m => m.Token == "JOY_BTN99");

        LayoutEditorMarkerItem gone = Marker(vm, "JOY_BTN6");
        (double gx, double gy) = (gone.X, gone.Y);
        vm.SelectedLayoutEditorMarker = gone;
        vm.RemoveSelectedLayoutEditorMarkerCommand.Execute(null);
        Assert.DoesNotContain(vm.LayoutEditorMarkers, m => m.Token == "JOY_BTN6");

        vm.UndoLayoutEditCommand.Execute(null);
        LayoutEditorMarkerItem back = Marker(vm, "JOY_BTN6");
        Assert.Equal(gx, back.X, 2);
        Assert.Equal(gy, back.Y, 2);
        Assert.Equal("button", back.Type);
        Assert.Same(back, vm.SelectedLayoutEditorMarker);
    }

    [Fact]
    public async Task RefreshingTheDeviceList_KeepsAllDevices_TheSelection_AndUnsavedEdits()
    {
        (MainWindowViewModel vm, _, _) = await CreateLoadedAsync();
        vm.SetLayoutCanvasModeCommand.Execute("edit");
        LayoutEditorMarkerItem marker = Marker(vm, "JOY_BTN5");
        vm.LayoutCanvasMarkerMovedCommand.Execute(new DeviceMarkerMove(marker, marker.X, marker.Y, 10, 20));
        // Ticking the stick for the cheat sheets makes the next search ask for the stick only.
        vm.ApplySelectedLayoutSuggestionsCommand.Execute(null);

        await vm.SuggestLayoutsCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.LayoutSuggestions.Count);
        Assert.Equal(Stick, vm.SelectedLayoutSuggestion?.Device);
        Assert.Same(marker, Marker(vm, "JOY_BTN5"));
        Assert.Equal(10, marker.X);
        Assert.True(vm.LayoutEditorHasUnsavedChanges);
        Assert.True(vm.CanUndoLayoutEdit);
    }

    [Fact]
    public async Task CanvasClick_OutsideEdit_OnlyClearsTheSelection()
    {
        (MainWindowViewModel vm, _, _) = await CreateLoadedAsync();
        vm.LayoutPlaceMarkerMode = true;
        vm.SetLayoutCanvasModeCommand.Execute("view");
        vm.SelectedLayoutEditorMarker = Marker(vm, "JOY_BTN1");

        vm.LayoutCanvasClickedCommand.Execute(new DeviceCanvasClick(10, 10, false));

        Assert.Null(vm.SelectedLayoutEditorMarker);
        Assert.Equal(47, vm.LayoutEditorMarkers.Count);
        Assert.False(vm.CanUndoLayoutEdit);
    }

    [Fact]
    public async Task BindMode_BindsAndFreesTheSelectedInput()
    {
        (MainWindowViewModel vm, LayoutRedesignRenderTests.LayoutFakeEngine fake, _) = await CreateLoadedAsync();
        vm.SetLayoutCanvasModeCommand.Execute("bind");

        vm.SelectedLayoutEditorMarker = Marker(vm, "JOY_BTN3");
        Assert.Contains("Canopy", vm.LayoutBindCandidates);
        Assert.DoesNotContain("Pitch", vm.LayoutBindCandidates);
        vm.LayoutBindSearch = "can";
        Assert.Equal(["Canopy"], vm.LayoutBindCandidates);

        fake.Calls.Clear();
        await vm.BindSelectedLayoutMarkerCommand.ExecuteAsync("Canopy");
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "set"));
        Assert.Contains("Canopy", set);
        Assert.True(AxisTuningTests.Seq(set, "--device", Stick));
        Assert.True(AxisTuningTests.Seq(set, "--input", "JOY_BTN3"));
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "clear"));
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "list"));

        vm.SelectedLayoutEditorMarker = Marker(vm, "JOY_BTN2");
        Assert.Equal("Weapon Release", vm.SelectedLayoutBindCandidate);
        fake.Calls.Clear();
        await vm.FreeSelectedLayoutMarkerCommand.ExecuteAsync(null);
        IReadOnlyList<string> clear = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "clear"));
        Assert.True(AxisTuningTests.Seq(clear, "Weapon Release", "--input-only"));
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "set"));
    }

    [Fact]
    public async Task DeviceWithoutLayoutFile_ShowsItsReferencePicture()
    {
        (MainWindowViewModel vm, _, _) = await CreateLoadedAsync();
        LayoutSuggestionItem throttle = vm.LayoutSuggestions.Single(s => s.Device != Stick);

        vm.SelectedLayoutSuggestion = throttle;
        await LayoutRedesignRenderTests.WaitForAsync(() => vm.DeviceReference.DeviceName == throttle.Device && vm.HasReferenceModel);

        Assert.Empty(vm.LayoutEditorMarkers);
        Assert.True(vm.LayoutShowsReference);
        Assert.True(vm.HasLayoutWorkspacePicture);
        // JoyPro's own drawing of the throttle (with the curated positions of its photo) comes first when it ships one.
        if (vm.DeviceReference.PictureKind == "lineart")
        {
            Assert.NotEmpty(vm.DeviceReference.CanvasMarkers);
            await vm.DeviceReference.SelectPictureAsync(vm.DeviceReference.Pictures.Single(p => p.Kind == "class"));
        }
        // JoyPro's schematic of a throttle with 30 parts is not recognizable: the throttle outline is shown, with no
        // marker (no position is known on it), the legend lists every input and the marker assistant is offered.
        Assert.Equal("class", vm.DeviceReference.PictureKind);
        Assert.NotNull(vm.WorkspaceSchematic);
        Assert.Empty(vm.DeviceReference.CanvasMarkers);
        Assert.NotEmpty(vm.ReferenceLegendItems);
        Assert.True(vm.ShowPlaceMarkersHint);
        Assert.Same(vm.DeviceReference.CanvasMarkers, vm.WorkspaceMarkers);
        Assert.Contains("No layout file of Throttle - HOTAS Warthog yet", vm.LayoutEditorStatus, StringComparison.Ordinal);
        Assert.Equal("Throttle - HOTAS Warthog", throttle.DisplayName);
        Assert.False(throttle.IsOwnLayout);
        Assert.DoesNotContain(vm.WorkspacePictures, p => p.IsLayoutFile);
    }

    [Fact]
    public async Task Reference_LegendShowsTheBinds_AndBindModeBindsTheSelectedInput()
    {
        (MainWindowViewModel vm, LayoutRedesignRenderTests.LayoutFakeEngine fake, _) = await CreateLoadedAsync(layoutFile: false);

        ReferenceLegendRow trigger = Assert.Single(vm.ReferenceLegendItems, r => r.Input == "JOY_BTN1");
        Assert.Equal("Gun Trigger", trigger.Command);
        Assert.EndsWith("inputs bound", vm.LayoutLegendSummary, StringComparison.Ordinal);
        vm.LayoutLegendFilter = "gun trigger";
        Assert.Contains(trigger, vm.ReferenceLegendItems);
        Assert.DoesNotContain(vm.ReferenceLegendItems, r => r.Input == "JOY_BTN3");
        vm.LayoutLegendFilter = string.Empty;

        vm.SetLayoutCanvasModeCommand.Execute("bind");
        vm.DeviceReference.SelectedLegendRow = vm.ReferenceLegendItems.Single(r => r.Input == "JOY_BTN3");
        Assert.True(vm.HasWorkspaceInput);
        Assert.Equal("BTN 3", vm.WorkspaceInputTitle);
        Assert.Contains("Canopy", vm.LayoutBindCandidates);
        Assert.DoesNotContain("Pitch", vm.LayoutBindCandidates);

        fake.Calls.Clear();
        await vm.BindSelectedLayoutMarkerCommand.ExecuteAsync("Canopy");
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "set"));
        Assert.True(AxisTuningTests.Seq(set, "--device", Stick));
        Assert.True(AxisTuningTests.Seq(set, "--input", "JOY_BTN3"));
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "clear"));

        // The trigger is bound: Free clears its relation.
        vm.DeviceReference.SelectedLegendRow = vm.ReferenceLegendItems.Single(r => r.Input == "JOY_BTN1");
        fake.Calls.Clear();
        await vm.FreeSelectedLayoutMarkerCommand.ExecuteAsync(null);
        IReadOnlyList<string> clear = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "clear"));
        Assert.True(AxisTuningTests.Seq(clear, "Gun Trigger", "--input-only"));
    }

    // Tables and lines are the default look of a reference picture with known positions: a relation dropped on a row
    // binds it (the modifier column with the sheet's modifier), right-click clears it, a preview shows the game's own
    // menu titles, and the sheet is kept for the cheat sheets.
    [Fact]
    public async Task Callouts_DropARelationOnARow_BindsIt_PreviewShowsMenuTitles()
    {
        (MainWindowViewModel vm, LayoutRedesignRenderTests.LayoutFakeEngine fake, _) = await CreateLoadedAsync(layoutFile: false);
        Assert.True(vm.LayoutShowsCallouts);
        Assert.False(vm.LayoutShowsMarkerCanvas);
        JoyPro.DeviceReference.CalloutSheet sheet = vm.LayoutCallouts.Sheet!;
        JoyPro.DeviceReference.CalloutCheck check = JoyPro.DeviceReference.CalloutLayouter.Check(sheet);
        Assert.True(check.Ok, check + " | " + string.Join("; ", check.Problems));
        Assert.NotNull(DeviceReferenceService.CalloutFileOf(Stick));
        Assert.Contains("Gun Trigger", vm.CalloutRelationItems);

        // What the trigger does, in its row.
        JoyPro.DeviceReference.CalloutTable trigger = sheet.TableOfInput("JOY_BTN1")!;
        int triggerRow = trigger.VisibleRows().FindIndex(r => r.Input == "JOY_BTN1");
        Assert.Equal("Gun Trigger", vm.LayoutCallouts.RowText(trigger, trigger.VisibleRows()[triggerRow], false));

        // A relation dropped on BTN 3: bound there, without a modifier.
        JoyPro.DeviceReference.CalloutTable button3 = sheet.TableOfInput("JOY_BTN3")!;
        int row3 = button3.VisibleRows().FindIndex(r => r.Input == "JOY_BTN3");
        fake.Calls.Clear();
        Assert.True(vm.LayoutCallouts.DropRelation(button3, row3, false, "Canopy"));
        await LayoutRedesignRenderTests.WaitForAsync(() => fake.Calls.Any(c => AxisTuningTests.Seq(c, "binds", "set")));
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "set"));
        Assert.True(AxisTuningTests.Seq(set, "Canopy", "--device", Stick));
        Assert.True(AxisTuningTests.Seq(set, "--input", "JOY_BTN3"));
        Assert.DoesNotContain("--modifier", set);

        // The modifier column binds with the sheet's modifier.
        vm.CalloutModifier = "Shift";
        fake.Calls.Clear();
        Assert.True(vm.LayoutCallouts.DropRelation(button3, row3, true, "Canopy"));
        await LayoutRedesignRenderTests.WaitForAsync(() => fake.Calls.Any(c => AxisTuningTests.Seq(c, "binds", "set")));
        IReadOnlyList<string> modSet = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "set"));
        Assert.True(AxisTuningTests.Seq(modSet, "--modifier", "Shift"));

        // Right-click > Clear frees the trigger.
        fake.Calls.Clear();
        vm.LayoutCallouts.ClearRow(trigger, triggerRow, false);
        await LayoutRedesignRenderTests.WaitForAsync(() => fake.Calls.Any(c => AxisTuningTests.Seq(c, "binds", "clear")));
        Assert.True(AxisTuningTests.Seq(Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "clear")), "Gun Trigger", "--input-only"));

        // A game and aircraft: the rows show that game's own menu titles, with a GAME / PLANE header.
        await LayoutRedesignRenderTests.WaitForAsync(() => vm.CalloutPreviewOptions.Count > 1);
        vm.SelectedCalloutPreview = "DCS · F-16C_50";
        Assert.Equal("DCS", vm.CalloutHeaderGame);
        Assert.Equal("F-16C_50", vm.CalloutHeaderPlane);
        Assert.Equal("Gun Trigger - Second Detent", vm.LayoutCallouts.RowText(trigger, trigger.VisibleRows()[triggerRow], false));
        JoyPro.DeviceReference.CalloutTable button2 = sheet.TableOfInput("JOY_BTN2")!;
        int row2 = button2.VisibleRows().FindIndex(r => r.Input == "JOY_BTN2");
        Assert.Equal("Pickle - Jettison", vm.LayoutCallouts.RowText(button2, button2.VisibleRows()[row2], true));

        // Tables off: the markers with labels again.
        vm.LayoutCalloutsEnabled = false;
        Assert.False(vm.LayoutShowsCallouts);
        Assert.True(vm.LayoutShowsMarkerCanvas);
    }

    // An edit of the sheet is undone with the page's Undo and kept with Save.
    [Fact]
    public async Task Callouts_EditsAreUndone_AndSaved()
    {
        (MainWindowViewModel vm, _, _) = await CreateLoadedAsync(layoutFile: false);
        vm.SetLayoutCanvasModeCommand.Execute("edit");
        Assert.True(vm.IsCalloutEditMode);
        JoyPro.DeviceReference.CalloutTable table = vm.LayoutCallouts.Sheet!.Tables[0];
        vm.LayoutCallouts.MoveTable(table, table.X + 200, table.Y + 100);
        Assert.True(vm.WorkspaceHasUnsavedChanges);
        Assert.True(vm.CanUndoLayoutEdit);
        vm.UndoLayoutEditCommand.Execute(null);
        Assert.False(vm.LayoutCallouts.Sheet!.Table(table.Id)!.Pinned);
        vm.RedoLayoutEditCommand.Execute(null);
        Assert.True(vm.LayoutCallouts.Sheet!.Table(table.Id)!.Pinned);
        vm.SaveWorkspaceCommand.Execute(null);
        Assert.False(vm.WorkspaceHasUnsavedChanges);
        string file = DeviceReferenceService.CalloutFileOf(Stick)!;
        try
        {
            JoyPro.DeviceReference.DeviceLayoutSet saved = JoyPro.DeviceReference.DeviceLayoutSet.Parse(File.ReadAllText(file), file);
            Assert.True(saved.Callouts!.Table(table.Id)!.Pinned);
        }
        finally
        {
            // The other tests of the run see JoyPro's own arrangement again.
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Reference_PartMoves_AreUndone_AndSaved()
    {
        (MainWindowViewModel vm, _, _) = await CreateLoadedAsync(layoutFile: false);
        vm.SetLayoutCanvasModeCommand.Execute("edit");
        Assert.True(vm.IsReferenceEditMode);
        Assert.False(vm.IsLayoutFileEditMode);
        ReferenceCanvasMarker part = vm.DeviceReference.CanvasMarkers.First();
        (double x, double y) = (part.Part.X, part.Part.Y);

        vm.LayoutCanvasMarkerMovedCommand.Execute(new DeviceMarkerMove(part, part.X, part.Y, part.X + 40, part.Y + 30));

        Assert.NotEqual(x, part.Part.X, 4);
        Assert.Equal(part.X, vm.DeviceReference.CanvasMarkers.First().X, 3);
        Assert.True(vm.WorkspaceHasUnsavedChanges);
        Assert.True(vm.CanUndoLayoutEdit);

        vm.UndoLayoutEditCommand.Execute(null);
        Assert.Equal(x, part.Part.X, 4);
        Assert.Equal(y, part.Part.Y, 4);

        vm.RedoLayoutEditCommand.Execute(null);
        Assert.NotEqual(x, part.Part.X, 4);
        vm.SaveWorkspaceCommand.Execute(null);
        Assert.False(vm.WorkspaceHasUnsavedChanges);
        Assert.False(vm.CanUndoLayoutEdit);
    }

    [Fact]
    public async Task PictureChoice_SwitchesBetweenReferenceAndLayoutFile()
    {
        (MainWindowViewModel vm, _, _) = await CreateLoadedAsync(layoutFile: false);
        Assert.Contains(vm.WorkspacePictures, p => p.Reference?.Kind == "schematic");
        // A shipped template (or a layout the engine generated) only feeds the cheat sheets: no picture of its own.
        Assert.DoesNotContain(vm.WorkspacePictures, p => p.IsLayoutFile);

        // The user's own layout (a JoyPro 1 layout) is a picture of the strip, first.
        vm.SelectedLayoutSuggestion!.Source = "legacy-layout-dir";
        await vm.ShowWorkspaceDeviceAsync(Stick);
        WorkspacePictureChoice file = Assert.Single(vm.WorkspacePictures, p => p.IsLayoutFile);
        Assert.Same(file, vm.WorkspacePictures[0]);
        Assert.Contains("VKB_GunfighterMK3", file.Title, StringComparison.Ordinal);

        vm.SelectedWorkspacePicture = file;
        Assert.True(vm.LayoutShowsLayoutFile);
        Assert.Same(vm.LayoutEditorMarkers, vm.WorkspaceMarkers);
        Assert.Null(vm.WorkspaceSchematic);

        vm.SelectedWorkspacePicture = vm.WorkspacePictures.First(p => p.Reference?.Kind == "schematic");
        Assert.True(vm.LayoutShowsReference);
        Assert.NotNull(vm.WorkspaceSchematic);
        Assert.True(vm.WorkspaceWidth > 0);
    }

    [Fact]
    public void OwnLayouts_AreShownBeforeTheReference_ShippedOnesAfter()
    {
        Assert.True(new LayoutSuggestionItem { LayoutPath = "C:/mine.layout", Source = "legacy-layout-dir" }.IsOwnLayout);
        Assert.True(new LayoutSuggestionItem { LayoutPath = "C:/mine.layout", Source = "user-picture" }.IsOwnLayout);
        Assert.False(new LayoutSuggestionItem { LayoutPath = "C:/LayoutTemplates/x.layout", Source = "catalog" }.IsOwnLayout);
        Assert.False(new LayoutSuggestionItem { LayoutPath = "C:/drawn.layout", Source = "reference-schematic" }.IsOwnLayout);
        Assert.False(new LayoutSuggestionItem { Source = "legacy-layout-dir" }.IsOwnLayout);
    }

    [Fact]
    public async Task MarkerAssistant_ClickOnThePicture_PlacesTheAskedPart()
    {
        (MainWindowViewModel vm, _, _) = await CreateLoadedAsync(layoutFile: false);
        vm.OpenReferenceAssistantCommand.Execute(null);
        Assert.True(vm.HasReferenceAssistant);
        Assert.True(vm.IsLayoutEditMode);
        vm.DeviceReference.Assistant!.Found("JOY_BTN1");
        ReferenceMarker asked = vm.DeviceReference.Assistant.Current!;

        vm.LayoutCanvasClickedCommand.Execute(new DeviceCanvasClick(vm.WorkspaceWidth * 0.25, vm.WorkspaceHeight * 0.5, false));

        Assert.Equal(0.25, asked.X, 3);
        Assert.Equal(0.5, asked.Y, 3);
        Assert.True(asked.IsPlaced);
        vm.CloseReferenceAssistantCommand.Execute(null);
        Assert.False(vm.HasReferenceAssistant);
    }

    [Fact]
    public async Task BindStudio_VisualAssign_ShowsTheSameWorkspaceDevice()
    {
        (MainWindowViewModel vm, _, _) = await CreateLoadedAsync(layoutFile: false);
        vm.OpenVisualAssignMode = true;
        vm.VisualAssignDevice = "Throttle - HOTAS Warthog {00000000-0000-0000-0000-0000000000a2}";
        await LayoutRedesignRenderTests.WaitForAsync(() => vm.DeviceReference.DeviceName == vm.VisualAssignDevice && vm.HasReferenceModel);

        Assert.Equal("Throttle - HOTAS Warthog", vm.LayoutWorkspaceDeviceTitle);
        Assert.Same(vm.LayoutSuggestions.Single(s => s.Device == vm.VisualAssignDevice), vm.SelectedLayoutSuggestion);
        Assert.NotEmpty(vm.ReferenceLegendItems);
    }
    // ---- Cheat sheets ----

    [Theory]
    [InlineData(2, 0, 3, true, true, "2 devices · an overview of each · one per aircraft (3 aircraft) → 8 pictures")]
    [InlineData(1, 0, 0, true, false, "1 device · an overview → 1 picture")]
    [InlineData(0, 1, 0, true, true, "every device with a picture · an overview of each · one per aircraft (every aircraft of 1 game)")]
    [InlineData(3, 0, 0, false, true, "3 devices · one per aircraft (every aircraft of every game)")]
    [InlineData(1, 0, 0, false, false, "Tick an overview or one picture per aircraft.")]
    public void CheatSheetPlan_SaysWhatWillBeMade(int devices, int games, int planes, bool overview, bool perAircraft, string expected)
    {
        Assert.Equal(expected, MainWindowViewModel.DescribeCheatSheetPlan(devices, games, planes, overview, perAircraft));
    }

    [Fact]
    public void LayoutRenderOutput_GivesTheFolderAndTheWrittenFiles()
    {
        string output = string.Join(Environment.NewLine,
            "Layout Render",
            "Export Path: C:\\Out",
            "Requested Joysticks: 1",
            "  - Stick",
            "Written Files: 2",
            "  - C:\\Out\\Stick.png",
            "  - C:\\Out\\DCS\\F-16C_50\\Stick.png",
            "Warnings:",
            "  - something");

        List<string> files = MainWindowViewModel.ParseLayoutRenderOutput(output, out string folder);

        Assert.Equal("C:\\Out", folder);
        Assert.Equal(["C:\\Out\\Stick.png", "C:\\Out\\DCS\\F-16C_50\\Stick.png"], files);
        Assert.Equal("Overview", MainWindowViewModel.CheatSheetSubtitle(folder, files[0]));
        Assert.Equal("DCS · F-16C_50", MainWindowViewModel.CheatSheetSubtitle(folder, files[1]));
        Assert.Empty(MainWindowViewModel.ParseLayoutRenderOutput("ok", out string none));
        Assert.Equal(string.Empty, none);
    }

    [Fact]
    public async Task CreateCheatSheets_ShowsThePreviews_AndOpensTheFolder()
    {
        (MainWindowViewModel vm, _, string work) = await CreateLoadedAsync();
        string folder = Path.Combine(work, "Sheets");
        vm.LayoutExportPath = folder;
        Assert.True(vm.HasNoCheatSheetPreviews);

        await vm.RenderLayoutCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.CheatSheetPreviews.Count);
        Assert.True(vm.HasCheatSheetPreviews);
        Assert.Equal(folder, vm.LastCheatSheetFolder);
        Assert.Contains("Made 4 cheat sheets", vm.CheatSheetStatus, StringComparison.Ordinal);
        Assert.Contains(vm.CheatSheetPreviews, p => p.Subtitle == "Overview" && p.Title == "Gunfighter");
        Assert.Contains(vm.CheatSheetPreviews, p => p.Subtitle == "DCS · F-16C_50");

        // A later visit reads the folder again.
        vm.CheatSheetPreviews.Clear();
        vm.RefreshCheatSheetPreviewsCommand.Execute(null);
        Assert.Equal(4, vm.CheatSheetPreviews.Count);

        Func<ProcessStartInfo, Process?> original = ExternalProcess.Start;
        List<string> opened = [];
        ExternalProcess.Start = info =>
        {
            opened.Add(info.FileName);
            return null;
        };
        try
        {
            vm.OpenCheatSheetFolderCommand.Execute(null);
            vm.OpenCheatSheetCommand.Execute(vm.CheatSheetPreviews[0]);
        }
        finally
        {
            ExternalProcess.Start = original;
        }
        Assert.Equal([folder, vm.CheatSheetPreviews[0].FilePath], opened);
    }

    [Fact]
    public void OpenCheatSheetFolder_WithoutFolder_Explains()
    {
        MainWindowViewModel vm = new(new LayoutRedesignRenderTests.LayoutFakeEngine("none.png", Path.GetTempPath()), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            LayoutExportPath = string.Empty
        };
        vm.OpenCheatSheetFolderCommand.Execute(null);
        Assert.Contains("Make cheat sheets first", vm.CheatSheetStatus, StringComparison.Ordinal);
        Assert.StartsWith("Default folder", vm.CheatSheetFolderDisplay, StringComparison.Ordinal);
    }

    // ---- Texts and the canvas ----

    [Theory]
    [InlineData("JOY_BTN5", "BTN 5")]
    [InlineData("JOY_BTN_POV1_U", "POV1 Up")]
    [InlineData("JOY_BTN_POV2_DL", "POV2 Down-Left")]
    [InlineData("JOY_SLIDER1", "Slider 1")]
    [InlineData("JOY_RZ", "Axis RZ")]
    [InlineData("", "?")]
    public void ShortLabels_AreReadable(string input, string expected)
    {
        Assert.Equal(expected, DeviceMarkerText.ShortLabel(input));
    }

    [Fact]
    public void DeviceNames_DropTheGuid()
    {
        Assert.Equal("Throttle - HOTAS Warthog", DeviceMarkerText.DeviceName("Throttle - HOTAS Warthog {00000000-0000-0000-0000-0000000000a2}"));
        Assert.Equal("Keyboard", DeviceMarkerText.DeviceName("Keyboard"));
    }

    [Fact]
    public void Canvas_SnapsToOtherMarkers_AndToTheGrid_ExceptWithAlt()
    {
        AvaloniaRenderThread.Run(() =>
        {
            LayoutEditorMarkerItem anchor = new() { Token = "JOY_BTN1", X = 100, Y = 200 };
            LayoutEditorMarkerItem dragged = new() { Token = "JOY_BTN2", X = 400, Y = 400 };
            DeviceMarkerCanvas canvas = new()
            {
                Markers = new ObservableCollection<LayoutEditorMarkerItem> { anchor, dragged },
                Zoom = 1,
                Mode = DeviceCanvasMode.EditMarkers
            };

            Assert.Equal(new Point(100, 300), canvas.Snap(new Point(104, 300), dragged, disable: false));
            Assert.Equal(new Point(250, 200), canvas.Snap(new Point(250, 195), dragged, disable: false));
            Assert.Equal(new Point(104, 300), canvas.Snap(new Point(104, 300), dragged, disable: true));

            canvas.ShowGrid = true;
            Assert.Equal(new Point(250, 310), canvas.Snap(new Point(252, 307), dragged, disable: false));
            canvas.SnapEnabled = false;
            Assert.Equal(new Point(104, 300), canvas.Snap(new Point(104, 300), dragged, disable: false));

            // An arrow-key nudge (Shift: 10 px) is reported as a move; the canvas does not move the marker itself.
            List<DeviceMarkerMove> moves = [];
            canvas.MarkerMoved += (_, move) => moves.Add(move);
            canvas.SelectedMarker = dragged;
            canvas.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Right, KeyModifiers = Avalonia.Input.KeyModifiers.Shift });
            DeviceMarkerMove nudge = Assert.Single(moves);
            Assert.Same(dragged, nudge.Marker);
            Assert.Equal(410, nudge.ToX);
            Assert.Equal(400, nudge.ToY);
            Assert.Equal(400, dragged.X);

            // Outside Edit mode the arrows do nothing.
            canvas.Mode = DeviceCanvasMode.Bind;
            canvas.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Right });
            Assert.Single(moves);
        });
    }

    // ---- Bind on the picture: the relation picked on the left, modifier combinations ----

    // The user's report: Pitch picked on the left, an input clicked, "Bind here" (no parameter, nothing picked in the
    // side list) did nothing. Now the relation picked on the left is bound, with a visible status.
    [Fact]
    public async Task VisualAssign_BindHere_BindsTheRelationPickedOnTheLeft()
    {
        (MainWindowViewModel vm, LayoutRedesignRenderTests.LayoutFakeEngine fake, _) = await CreateLoadedAsync(layoutFile: false);
        vm.SetLayoutCanvasModeCommand.Execute("bind");
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Canopy");
        vm.DeviceReference.SelectedLegendRow = vm.ReferenceLegendItems.Single(r => r.Input == "JOY_BTN3");
        Assert.Equal("Canopy", vm.SelectedLayoutBindCandidate);
        Assert.Equal("Canopy", vm.LayoutBindCandidates[0]);
        Assert.Contains("Canopy →", vm.VisualAssignPreview, StringComparison.Ordinal);
        Assert.True(vm.VisualAssignTargetIsFree);

        fake.Calls.Clear();
        vm.SelectedLayoutBindCandidate = null;
        await vm.BindSelectedLayoutMarkerCommand.ExecuteAsync(null);
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "set"));
        Assert.True(AxisTuningTests.Seq(set, "Canopy", "--device", Stick));
        Assert.True(AxisTuningTests.Seq(set, "--input", "JOY_BTN3"));
        Assert.DoesNotContain("--modifier", set);
        Assert.StartsWith("Bound Canopy to ", vm.VisualAssignStatus, StringComparison.Ordinal);
        Assert.Equal(vm.VisualAssignStatus, vm.StatusText);
        Assert.False(vm.VisualAssignStatusIsError);
        Assert.NotNull(vm.WorkspaceFlashMarker);
        Assert.Contains(vm.GuiTrace, l => l.Contains("[VisualAssign] Bind here", StringComparison.Ordinal));
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "list"));

        // An axis relation on a button: nothing is bound, the reason is shown.
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Pitch");
        vm.DeviceReference.SelectedLegendRow = vm.ReferenceLegendItems.Single(r => r.Input == "JOY_BTN3");
        Assert.DoesNotContain("Pitch", vm.LayoutBindCandidates);
        vm.SelectedLayoutBindCandidate = null;
        fake.Calls.Clear();
        await vm.BindSelectedLayoutMarkerCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "set"));
        Assert.True(vm.VisualAssignStatusIsError);
        Assert.Contains("Pitch is an axis relation", vm.VisualAssignStatus, StringComparison.Ordinal);
    }

    // With modifiers the side panel lists every combination of the input (none, Pinky, Shift, Pinky + Shift) with what
    // is bound there; the picked one is bound with exactly those modifiers.
    [Fact]
    public async Task VisualAssign_PickedCombination_BindsWithExactlyThoseModifiers()
    {
        (MainWindowViewModel vm, LayoutRedesignRenderTests.LayoutFakeEngine fake, _) = await CreateLoadedAsync(layoutFile: false);
        await vm.RefreshModifiersCommand.ExecuteAsync(null);
        Assert.Contains("Pinky", vm.ModifierNames);
        Assert.Contains("Shift", vm.ModifierNames);
        vm.SetLayoutCanvasModeCommand.Execute("bind");
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Canopy");
        vm.DeviceReference.SelectedLegendRow = vm.ReferenceLegendItems.Single(r => r.Input == "JOY_BTN10");
        Assert.True(vm.HasVisualAssignCombos);
        Assert.Equal(["—", "Pinky", "Shift", "Pinky + Shift"], vm.VisualAssignCombos.Select(c => c.Label).ToArray());
        // JOY_BTN10 has Trim Nose Down with Pinky only.
        Assert.Equal("free", vm.VisualAssignCombos[0].Display);
        Assert.Equal("Trim Nose Down", vm.VisualAssignCombos[1].Relation);
        Assert.True(vm.VisualAssignCombos[1].IsBound);
        Assert.Equal(string.Empty, vm.VisualAssignCombos[3].Relation);

        vm.SelectVisualAssignComboCommand.Execute(vm.VisualAssignCombos[3]);
        Assert.Equal("Pinky+Shift", vm.VisualAssignCombo);
        Assert.True(vm.VisualAssignCombos[3].IsSelected);
        Assert.Contains("Pinky + Shift", vm.VisualAssignPreview, StringComparison.Ordinal);
        fake.Calls.Clear();
        await vm.BindSelectedLayoutMarkerCommand.ExecuteAsync(null);
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "set"));
        Assert.True(AxisTuningTests.Seq(set, "--input", "JOY_BTN10"));
        Assert.True(AxisTuningTests.Seq(set, "--modifier", "Pinky"));
        Assert.True(AxisTuningTests.Seq(set, "--modifier", "Shift"));
        // Trim Nose Down (Pinky) keeps its input: another combination.
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "clear"));
    }

    // The tables of the Layout page: one column per combination, each cell the relation under exactly those modifiers,
    // a drop on a cell binds with them.
    [Fact]
    public async Task Callouts_ModifierCombinationColumns_ShowAndBindExactly()
    {
        (MainWindowViewModel vm, LayoutRedesignRenderTests.LayoutFakeEngine fake, _) = await CreateLoadedAsync(layoutFile: false);
        await vm.RefreshModifiersCommand.ExecuteAsync(null);
        try
        {
            Assert.True(vm.LayoutShowsCallouts);
            CalloutSheetViewModel callouts = vm.LayoutCallouts;
            // In use on the stick: Pinky (Trim Nose Down).
            Assert.Equal(["Pinky"], callouts.Sheet!.Combos);
            vm.CalloutModifier = MainWindowViewModel.AllCombinationsChoice;
            Assert.Equal(["Pinky", "Shift", "Pinky+Shift"], callouts.Sheet!.Combos);
            Assert.Contains("Pinky + Shift", vm.CalloutModifierInUse, StringComparison.Ordinal);
            JoyPro.DeviceReference.CalloutCheck check = JoyPro.DeviceReference.CalloutLayouter.Check(callouts.Sheet);
            Assert.True(check.Overlaps == 0, string.Join("; ", check.Problems));

            JoyPro.DeviceReference.CalloutTable table = callouts.Sheet.TableOfInput("JOY_BTN10")!;
            JoyPro.DeviceReference.CalloutRow row = table.VisibleRows().First(r => r.Input == "JOY_BTN10");
            int index = table.VisibleRows().IndexOf(row);
            Assert.Equal(string.Empty, callouts.RowText(table, row, 0));
            Assert.Equal("Trim Nose Down", callouts.RowText(table, row, 1));
            Assert.Equal(string.Empty, callouts.RowText(table, row, 2));
            Assert.Equal(string.Empty, callouts.RowText(table, row, 3));

            fake.Calls.Clear();
            Assert.True(callouts.DropRelation(table, index, 3, "Canopy"));
            await LayoutRedesignRenderTests.WaitForAsync(() => fake.Calls.Any(c => AxisTuningTests.Seq(c, "binds", "set")));
            IReadOnlyList<string> set = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "set"));
            Assert.True(AxisTuningTests.Seq(set, "--modifier", "Pinky"));
            Assert.True(AxisTuningTests.Seq(set, "--modifier", "Shift"));

            // At most 2 columns: the unmodified one and one combination.
            vm.CalloutMaxColumns = 2;
            Assert.Single(callouts.Sheet!.Combos);
            vm.CalloutMaxColumns = 8;
            vm.CalloutModifier = MainWindowViewModel.AutoModifierChoice;
            Assert.Equal(["Pinky"], callouts.Sheet!.Combos);
        }
        finally
        {
            if (DeviceReferenceService.CalloutFileOf(Stick) is { } file && File.Exists(file))
                File.Delete(file);
        }
    }
}
