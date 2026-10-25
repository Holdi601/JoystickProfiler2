using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Runtime.Versioning;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

[SupportedOSPlatform("windows")]
public sealed class MainWindowViewModelSmokeTests
{
    [Fact]
    public async Task RegistryGames_LoadDetectAndExport_BuildEngineArguments()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        await vm.RefreshRegistryGamesCommand.ExecuteAsync(null);
        // MSFS 2024 and MSFS 2020 are two games.
        Assert.Equal(3, vm.RegistryGames.Count);
        Assert.Equal("War Thunder (experimental)", vm.RegistryGames[0].DisplayName);
        Assert.Contains(vm.RegistryGames, g => g.GameId == "MSFS2020" && g.CommandName == "msfs2020");
        Assert.False(vm.SelectedRegistryGameSupportsProfiles);
        Assert.False(vm.SelectedRegistryGameSupportsAircraft);

        vm.SelectedRegistryGame = vm.RegistryGames.Single(g => g.GameId == "MSFS2024");
        Assert.True(vm.SelectedRegistryGameSupportsProfiles);
        Assert.True(vm.SelectedRegistryGameSupportsAircraft);
        Assert.Equal("MSFS notes", vm.SelectedRegistryGameNotes);

        await vm.DetectRegistryGamePathCommand.ExecuteAsync(null);
        Assert.Equal("D:/Games/remote", vm.RegistryGamePath);

        vm.RegistryGameProfileName = "Sting";
        vm.ExportMode = "clean";
        vm.RegistryGameDryRun = true;
        fake.Calls.Clear();
        await vm.ExportRegistryGameCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "msfs2024", "export"));
        Assert.True(ContainsSequence(call, "--path", "D:/Games/remote"));
        Assert.True(ContainsSequence(call, "--game-profile", "Sting"));
        Assert.True(ContainsSequence(call, "--mode", "clean"));
        Assert.Contains("--dry-run", call);
        Assert.Contains("--profile", call);
        Assert.DoesNotContain("--aircraft", call);

        // An aircraft picked for the export also writes the relations limited to it (MSFS presets apply to all aircraft).
        vm.RegistryGameAircraft = "asobo-aircraft-c152";
        fake.Calls.Clear();
        await vm.ExportRegistryGameCommand.ExecuteAsync(null);
        IReadOnlyList<string> aircraftCall = Assert.Single(fake.Calls, c => ContainsSequence(c, "msfs2024", "export"));
        Assert.True(ContainsSequence(aircraftCall, "--aircraft", "asobo-aircraft-c152"));
    }

    [Fact]
    public async Task AutoGroup_PreviewAndApply_BuildEngineArguments()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        vm.ToggleAutoGroupPanelCommand.Execute(null);
        Assert.True(vm.IsAutoGroupPanelOpen);
        Assert.Equal("all", vm.SelectedAutoGroupPreset?.Key);

        vm.SelectedAutoGroupPreset = vm.AutoGroupPresets.Single(p => p.Key == "era");
        vm.AutoGroupIncludeSubstitutes = false;
        vm.AutoGroupPlanes = "F-16C_50|FA-18C_hornet";
        fake.Calls.Clear();
        await vm.PreviewAutoGroupCommand.ExecuteAsync(null);
        IReadOnlyList<string> preview = Assert.Single(fake.Calls, c => ContainsSequence(c, "relations", "auto-group"));
        Assert.True(ContainsSequence(preview, "--preset", "era"));
        Assert.True(ContainsSequence(preview, "--planes", "F-16C_50|FA-18C_hornet"));
        Assert.Contains("--no-substitutes", preview);
        Assert.Contains("--dry-run", preview);
        Assert.Contains("--profile", preview);

        vm.AutoGroupReplace = true;
        fake.Calls.Clear();
        await vm.ApplyAutoGroupCommand.ExecuteAsync(null);
        // Other settings than the preview: a preview for the question first, then the change.
        IReadOnlyList<string> apply = Assert.Single(fake.Calls, c => ContainsSequence(c, "relations", "auto-group") && !c.Contains("--dry-run"));
        Assert.Contains("--replace", apply);
    }

    // The Auto Group answer (JSON) as a readable list and the question Apply asks with its counts.
    [Fact]
    public void AutoGroup_SummaryReadsTheEngineAnswer()
    {
        const string json = "{\"RelationsCreated\":2,\"RelationsExtended\":1,\"RelationsRemoved\":0,\"NodesAdded\":20,\"AircraftCovered\":7," +
                            "\"Relations\":[{\"Name\":\"Pitch\",\"IsAxis\":true,\"Existing\":true,\"NodesAdded\":9,\"Aircraft\":[\"FSX\",\"MSFS2020\",\"MSFS2024\",\"P3D4\",\"X4\"]}," +
                            "{\"Name\":\"Anti Skid Off\",\"IsAxis\":false,\"Existing\":false,\"NodesAdded\":3,\"Aircraft\":[\"F-14\"]}," +
                            "{\"Name\":\"AI menu down\",\"IsAxis\":false,\"Existing\":false,\"NodesAdded\":8,\"Aircraft\":[\"Default\"]}]," +
                            "\"Kept\":[\"UH-1H_Gunner a3043cd9 not added to your 'Pitch': only the function catalog reads it as this function, its title does not say so\"],\"Warnings\":[],\"Errors\":[]}";
        MainWindowViewModel.AutoGroupSummary s = MainWindowViewModel.AutoGroupSummary.Parse(json)!;
        Assert.Equal("2 new relations, 1 of yours gets more, 20 actions for 7 aircraft and games", s.Headline());
        string text = s.Describe(preview: true);
        Assert.StartsWith("Preview, nothing changed yet: 2 new relations", text);
        Assert.Contains("Your relations that get more:\n  Pitch (axis): +9 actions — FSX, MSFS2020, MSFS2024 +2 more", text.Replace("\r\n", "\n"));
        Assert.Contains("  AI menu down (button): +8 actions — Default", text);
        Assert.Contains("Left where they are (1):", text);
        Assert.DoesNotContain("{", text);
        Assert.Contains("'Pitch' +9", s.Question());
        Assert.Null(MainWindowViewModel.AutoGroupSummary.Parse("Relations Created: 2"));
    }

    [Fact]
    public async Task AutoAssign_DevicesGripsAndOptions_BuildEngineArguments()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        await vm.ToggleAutoAssignPanelCommand.ExecuteAsync(null);
        Assert.True(vm.IsAutoAssignPanelOpen);
        Assert.Single(fake.Calls, c => ContainsSequence(c, "devices", "kb", "identify"));
        Assert.Equal(2, vm.AutoAssignDevices.Count);
        AutoAssignDeviceItem stickBase = vm.AutoAssignDevices.Single(d => d.NeedsGrip);
        Assert.Equal(2, stickBase.Grips.Count);
        stickBase.SelectedGrip = stickBase.FindGrip("grip-b");
        vm.AutoAssignDevices.Single(d => !d.NeedsGrip).IsIgnored = true;

        vm.AutoAssignCatalogQuery = "warthog";
        await vm.SearchAutoAssignCatalogCommand.ExecuteAsync(null);
        IReadOnlyList<string> search = Assert.Single(fake.Calls, c => ContainsSequence(c, "devices", "kb", "list"));
        Assert.Contains("warthog", search);
        Assert.DoesNotContain(vm.AutoAssignCatalogResults, i => i.Kind == "base");
        vm.SelectedAutoAssignCatalogItem = vm.AutoAssignCatalogResults.Single(i => i.Id == "tm-warthog-throttle");
        vm.AddAutoAssignCatalogDeviceCommand.Execute(null);
        vm.AddAutoAssignCatalogDeviceCommand.Execute(null);
        Assert.Single(vm.AutoAssignAddedDevices);

        vm.AutoAssignMaxModifiers = 1;
        vm.AutoAssignKeepMyLayout = false;
        fake.Calls.Clear();
        await vm.PreviewAutoAssignCommand.ExecuteAsync(null);
        IReadOnlyList<string> preview = Assert.Single(fake.Calls, c => ContainsSequence(c, "relations", "auto-assign"));
        Assert.True(ContainsSequence(preview, "--grip", "Test Base {00000000-0000-0000-0000-0000000000a1}=grip-b"));
        Assert.True(ContainsSequence(preview, "--ignore-device", "Test Panel {00000000-0000-0000-0000-0000000000a2}"));
        Assert.True(ContainsSequence(preview, "--add-device", "tm-warthog-throttle"));
        Assert.True(ContainsSequence(preview, "--max-modifiers", "1"));
        Assert.Contains("--no-style", preview);
        Assert.Contains("--dry-run", preview);
        Assert.Contains("--profile", preview);
        Assert.DoesNotContain("--overwrite", preview);

        vm.AutoAssignOverwrite = true;
        vm.AutoAssignIncludeMissing = true;
        fake.Calls.Clear();
        await vm.ApplyAutoAssignCommand.ExecuteAsync(null);
        IReadOnlyList<string> apply = Assert.Single(fake.Calls, c => ContainsSequence(c, "relations", "auto-assign"));
        Assert.Contains("--overwrite", apply);
        Assert.Contains("--include-missing", apply);
        Assert.DoesNotContain("--dry-run", apply);

        vm.RemoveAutoAssignAddedDeviceCommand.Execute(vm.AutoAssignAddedDevices[0]);
        Assert.Empty(vm.AutoAssignAddedDevices);
    }

    [Fact]
    public async Task AutoAssign_OnlySelectedRelationsAndExplain_BuildEngineArguments()
    {
        FakeEngineCliClient fake = new();
        fake.UseMultiSortScenario = true;
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        vm.SelectedRelation = null;
        vm.SetRelationMassSelection([]);

        // Nothing selected: neither "only the selected" nor explain runs the engine.
        vm.AutoAssignOnlySelected = true;
        fake.Calls.Clear();
        await vm.PreviewAutoAssignCommand.ExecuteAsync(null);
        await vm.ExplainAutoAssignCommand.ExecuteAsync(null);
        await vm.ApplyAutoAssignCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fake.Calls, c => ContainsSequence(c, "relations", "auto-assign"));

        vm.SetRelationMassSelection(vm.Relations.Where(r => r.Name is "RelA" or "RelC"));
        fake.Calls.Clear();
        await vm.PreviewAutoAssignCommand.ExecuteAsync(null);
        IReadOnlyList<string> preview = Assert.Single(fake.Calls, c => ContainsSequence(c, "relations", "auto-assign"));
        Assert.True(ContainsSequence(preview, "--relation", "RelA"));
        Assert.True(ContainsSequence(preview, "--relation", "RelC"));
        Assert.DoesNotContain("RelB", preview);
        Assert.Contains("--dry-run", preview);
        Assert.DoesNotContain("--explain", preview);

        fake.Calls.Clear();
        await vm.ApplyAutoAssignCommand.ExecuteAsync(null);
        IReadOnlyList<string> apply = Assert.Single(fake.Calls, c => ContainsSequence(c, "relations", "auto-assign"));
        Assert.True(ContainsSequence(apply, "--relation", "RelA"));
        Assert.DoesNotContain("--dry-run", apply);

        // Explain: a dry run with --explain per selected relation, whether or not only those are assigned.
        vm.AutoAssignOnlySelected = false;
        fake.Calls.Clear();
        await vm.ExplainAutoAssignCommand.ExecuteAsync(null);
        IReadOnlyList<string> explain = Assert.Single(fake.Calls, c => ContainsSequence(c, "relations", "auto-assign"));
        Assert.True(ContainsSequence(explain, "--explain", "RelA"));
        Assert.True(ContainsSequence(explain, "--explain", "RelC"));
        Assert.DoesNotContain("--relation", explain);
        Assert.Contains("--dry-run", explain);
        Assert.Contains("--profile", explain);
        Assert.Contains("Explain:", vm.AutoAssignPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoAssign_ShowDeviceLayout_CallsDevicesKbShow()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.DetectAutoAssignDevicesCommand.ExecuteAsync(null);

        // A detected device with a catalog model.
        AutoAssignDeviceItem panel = vm.AutoAssignDevices.Single(d => d.Device.StartsWith("Test Panel", StringComparison.Ordinal));
        Assert.True(panel.HasLayout);
        fake.Calls.Clear();
        await vm.ShowAutoAssignDeviceLayoutCommand.ExecuteAsync(panel);
        IReadOnlyList<string> show = Assert.Single(fake.Calls, c => ContainsSequence(c, "devices", "kb", "show"));
        Assert.True(ContainsSequence(show, "devices", "kb", "show", "test-panel"));
        Assert.DoesNotContain("--text", show);
        Assert.True(vm.HasAutoAssignLayout);
        Assert.Equal("Test Panel (test-panel)", vm.AutoAssignLayoutTitle);
        Assert.Contains("VID:PID 1234:5678", vm.AutoAssignLayoutDetails, StringComparison.Ordinal);
        Assert.Equal(3, vm.AutoAssignLayoutRows.Count);
        AutoAssignLayoutRow axis = vm.AutoAssignLayoutRows[0];
        // Inputs and controls in plain words, never the ids.
        Assert.Equal("X axis", axis.Input);
        Assert.Equal("axis", axis.Type);
        AutoAssignLayoutRow trigger = vm.AutoAssignLayoutRows.Single(r => r.Input == "Button 1");
        Assert.Equal("Trigger first stage", trigger.Control);
        Assert.Equal("\"Trigger\", momentary, index finger", trigger.Details);

        // A base without its grip: the grip's layout once one is picked.
        AutoAssignDeviceItem stickBase = vm.AutoAssignDevices.Single(d => d.NeedsGrip);
        fake.Calls.Clear();
        await vm.ShowAutoAssignDeviceLayoutCommand.ExecuteAsync(stickBase);
        Assert.DoesNotContain(fake.Calls, c => ContainsSequence(c, "devices", "kb", "show"));
        stickBase.SelectedGrip = stickBase.FindGrip("grip-a");
        await vm.ShowAutoAssignDeviceLayoutCommand.ExecuteAsync(stickBase);
        Assert.Single(fake.Calls, c => ContainsSequence(c, "devices", "kb", "show", "grip-a"));

        // A catalog device picked in the search.
        await vm.SearchAutoAssignCatalogCommand.ExecuteAsync(null);
        vm.SelectedAutoAssignCatalogItem = vm.AutoAssignCatalogResults.Single(i => i.Id == "tm-warthog-throttle");
        fake.Calls.Clear();
        await vm.ShowAutoAssignCatalogLayoutCommand.ExecuteAsync(null);
        Assert.Single(fake.Calls, c => ContainsSequence(c, "devices", "kb", "show", "tm-warthog-throttle"));

        vm.CloseAutoAssignLayoutCommand.Execute(null);
        Assert.False(vm.HasAutoAssignLayout);
        Assert.Empty(vm.AutoAssignLayoutRows);
    }

    [Fact]
    public void HudDevice_TiltsWithItsAxesAndLightsUpButtons()
    {
        HudDevice stick = new("VKB {00000000-0000-0000-0000-000000000001}", "VKB", "MCG Pro", "stick", 4, 1,
            [("JOY_X", "stick.axis.roll", "Roll"), ("JOY_Y", "stick.axis.pitch", "Pitch"), ("JOY_Z", "stick.axis.twist", "Twist"), ("JOY_SLIDER2", "stick.axis.brake_lever", "Brake lever (DCS: wheel brake)")],
            new Dictionary<string, string> { ["JOY_BTN1"] = "Trigger" }, []);
        Assert.True(stick.HasGimbal);
        Assert.True(stick.HasTwist);
        Assert.Equal("Brake lever", Assert.Single(stick.Levers).Label);
        Assert.Equal(3, stick.Bars.Count);

        // Full right, full forward, twist right (DirectInput 0..1).
        stick.ApplyAxes([1.0, 0.0, 1.0, 0.5, 0.5, 0.5, 0.5, 0.25]);
        Assert.Equal(18, stick.Roll);
        Assert.Equal(14, stick.Pitch);
        Assert.Equal(24, stick.Twist);
        Assert.Equal(HudDevice.GimbalRadius, stick.GimbalX);
        Assert.Equal(-HudDevice.GimbalRadius, stick.GimbalY);
        Assert.Equal(0.25, stick.Levers[0].Value);
        Assert.Equal("+100", stick.Bars[0].Percent);

        stick.TiltEnabled = false;
        stick.ApplyAxes([1.0, 0.0, 1.0, 0.5, 0.5, 0.5, 0.5, 0.25]);
        Assert.Equal(0, stick.Roll);
        Assert.Equal(HudDevice.GimbalRadius, stick.GimbalX);

        stick.SetInput("JOY_BTN1", true);
        stick.SetInput("JOY_BTN3", true);
        Assert.True(stick.ButtonCells[0].IsPressed);
        Assert.Equal("Trigger · Button 3", stick.PressedText);
        Assert.Equal(["Trigger", "Button 3"], stick.PressedChips);
        stick.SetInput("JOY_BTN1", false);
        Assert.False(stick.ButtonCells[0].IsPressed);
        stick.SetInput("JOY_BTN_POV1_UR", true);
        Assert.True(stick.Hats[0].Dots.Single(d => d.Direction == "UR").IsActive);
        Assert.False(stick.Hats[0].IsCentered);
        stick.SetInput("JOY_BTN_POV1_UR", false);
        Assert.True(stick.Hats[0].IsCentered);

        // A throttle: its levers are levers, the thumb stick a centred bar; no tilt.
        HudDevice throttle = new("T {00000000-0000-0000-0000-000000000002}", "T", "MT-50", "throttle", 2, 0,
            [("JOY_RX", "throttle.axis.left", "Left throttle"), ("JOY_X", "throttle.axis.ministick.x", "Thumb X")], new Dictionary<string, string>(), []);
        Assert.Equal("Left throttle", Assert.Single(throttle.Levers).Label);
        Assert.True(Assert.Single(throttle.Bars).Bipolar);
        Assert.False(throttle.CanTilt);
    }

    [Fact]
    public void HudInput_NormalizesAxesAndCutsPlainBackgrounds()
    {
        double[] axes = JoystickCaptureService.NormalizedAxes(0, 65535, 32767, 0, 0, 0, 70000, -5);
        Assert.Equal(0, axes[0]);
        Assert.Equal(1, axes[1]);
        Assert.InRange(axes[2], 0.49, 0.51);
        Assert.Equal(1, axes[6]);
        Assert.Equal(0, axes[7]);
        Assert.False(JoystickCaptureService.AxesMoved(axes, axes.ToArray(), 0.001));

        // A dark device on white: the white around it turns transparent, the device and a white spot inside it stay.
        using SkiaSharp.SKBitmap photo = new(60, 60);
        using (SkiaSharp.SKCanvas canvas = new(photo))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            using SkiaSharp.SKPaint dark = new() { Color = new SkiaSharp.SKColor(30, 30, 40) };
            canvas.DrawRect(15, 15, 30, 30, dark);
            using SkiaSharp.SKPaint white = new() { Color = SkiaSharp.SKColors.White };
            canvas.DrawRect(27, 27, 6, 6, white);
        }
        using SkiaSharp.SKBitmap cut = ImageCutout.Cut(photo);
        Assert.Equal(0, cut.GetPixel(2, 2).Alpha);
        Assert.Equal(255, cut.GetPixel(20, 20).Alpha);
        Assert.Equal(255, cut.GetPixel(30, 30).Alpha);
    }

    [Fact]
    public async Task HudPage_ListsDevicesWithDefaultPlacesAndFeedsLiveInput()
    {
        FakeEngineCliClient fake = new();
        FakeJoystickCaptureService capture = new();
        MainWindowViewModel vm = CreateVm(fake, capture);

        vm.GoHudCommand.Execute(null);
        await Task.Delay(50);
        Assert.True(vm.IsHudStage);
        Assert.Contains(fake.Calls, c => ContainsSequence(c, "hud", "devices"));
        Assert.True(capture.Monitoring);
        Assert.True(capture.MonitoringAxes);

        Assert.Contains(vm.HudWidgets, w => w.IsHeader && w.Visible);
        Assert.Contains(vm.HudWidgets, w => w.IsBinds && w.Visible);
        HudWidget stick = vm.HudWidgets.Single(w => w.Device?.Model == "VKB MCG Pro");
        HudWidget throttle = vm.HudWidgets.Single(w => w.Device?.Model == "MT-50");
        HudWidget keyboard = vm.HudWidgets.Single(w => w.Device?.Name == "Keyboard HE");
        Assert.True(stick.Visible);
        Assert.True(throttle.Visible);
        Assert.False(keyboard.Visible);
        Assert.True(stick.X > throttle.X, "the right-hand stick sits right of the throttle");

        vm.OnHudInput(new JoystickInputEvent("Renamed {00000000-0000-0000-0000-0000000000B1}", "JOY_BTN1", true));
        Assert.True(stick.Device!.ButtonCells[0].IsPressed);
        Assert.Equal("Trigger", stick.Device.PressedText);
        vm.OnHudAxes(new JoystickAxisSnapshot("VKB Stick {00000000-0000-0000-0000-0000000000B1}", [0.0, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5]));
        Assert.Equal(-18, stick.Device.Roll);

        // A base with several grips: picking one reads the devices again with --grip.
        vm.SelectedHudWidget = vm.HudWidgets.Single(w => w.Device?.Name == "Base");
        Assert.True(vm.SelectedHudWidget.HasGripChoices);
        fake.Calls.Clear();
        vm.SelectedHudGrip = vm.SelectedHudWidget.Device!.GripChoices[0];
        await Task.Delay(50);
        IReadOnlyList<string> reload = Assert.Single(fake.Calls, c => ContainsSequence(c, "hud", "devices"));
        Assert.True(ContainsSequence(reload, "--grip", "Base {00000000-0000-0000-0000-0000000000B3}=grip-a"));

        vm.HudAccent = "#F59E0B";
        Assert.Equal(Avalonia.Media.Color.Parse("#F59E0B"), vm.HudContext.AccentColor);

        vm.GoStartCommand.Execute(null);
        Assert.False(vm.IsHudStage);
    }

    [Fact]
    public void OverlayResolver_MatchesDeviceByGuidAndPrefersHeldModifiers()
    {
        OverlayResolver.Modifier shift = new("Shift", "Stick {00000000-0000-0000-0000-000000000001}", "JOY_BTN5", false);
        OverlayResolver.Modifier latch = new("Latch", "Keyboard", "LCtrl", true);
        OverlayResolver resolver = new([
            new OverlayResolver.Entry("Plain", "", "Stick {00000000-0000-0000-0000-000000000001}", "JOY_BTN1", []),
            new OverlayResolver.Entry("Shifted", "", "Stick {00000000-0000-0000-0000-000000000001}", "JOY_BTN1", [shift]),
            new OverlayResolver.Entry("Latched", "", "Stick {00000000-0000-0000-0000-000000000001}", "JOY_BTN2", [latch]),
        ]);

        // The device name may differ (driver rename); the GUID decides.
        Assert.Equal("Plain", resolver.Handle("Renamed Stick {00000000-0000-0000-0000-000000000001}", "JOY_BTN1", true)?.Relation);
        Assert.Null(resolver.Handle("Renamed Stick {00000000-0000-0000-0000-000000000001}", "JOY_BTN1", false));
        Assert.Null(resolver.Handle("Other {00000000-0000-0000-0000-000000000002}", "JOY_BTN1", true));

        resolver.Handle("Stick {00000000-0000-0000-0000-000000000001}", "JOY_BTN5", true);
        Assert.Equal("Shifted", resolver.Handle("Stick {00000000-0000-0000-0000-000000000001}", "JOY_BTN1", true)?.Relation);
        resolver.Handle("Stick {00000000-0000-0000-0000-000000000001}", "JOY_BTN5", false);
        Assert.Equal("Plain", resolver.Handle("Stick {00000000-0000-0000-0000-000000000001}", "JOY_BTN1", true)?.Relation);

        // A switch modifier stays on after release until pressed again.
        Assert.Null(resolver.Handle("Stick {00000000-0000-0000-0000-000000000001}", "JOY_BTN2", true));
        resolver.Handle("Keyboard", "LCtrl", true);
        resolver.Handle("Keyboard", "LCtrl", false);
        Assert.Equal("Latched", resolver.Handle("Stick {00000000-0000-0000-0000-000000000001}", "JOY_BTN2", true)?.Relation);
        resolver.Handle("Keyboard", "LCtrl", true);
        Assert.Null(resolver.Handle("Stick {00000000-0000-0000-0000-000000000001}", "JOY_BTN2", true));
    }

    [Fact]
    public void JoystickMonitor_DiffStates_ReportsButtonsAndHatDirections()
    {
        List<(string Input, bool Down)> changes = JoystickCaptureService.DiffStates(
            [0, 0, 128], [128, 0, 0], [0xFFFFFFFF, 0], [9000, 4500]);
        Assert.Contains(("JOY_BTN1", true), changes);
        Assert.Contains(("JOY_BTN3", false), changes);
        Assert.Contains(("JOY_BTN_POV1_R", true), changes);
        Assert.Contains(("JOY_BTN_POV2_U", false), changes);
        Assert.Contains(("JOY_BTN_POV2_UR", true), changes);
        Assert.Equal(5, changes.Count);
    }

    [Fact]
    public async Task Overlay_StartShowsPressedBindsAndFollowsDcsAircraft()
    {
        FakeEngineCliClient fake = new();
        FakeJoystickCaptureService capture = new();
        MainWindowViewModel vm = CreateVm(fake, capture);
        FakeOverlayPresenter presenter = new();
        vm.OverlayPresenter = presenter;
        vm.OverlayOnlyInGame = false;

        await vm.ToggleOverlayCommand.ExecuteAsync(null);
        try
        {
            Assert.True(vm.IsOverlayRunning);
            Assert.True(capture.Monitoring);
            Assert.Equal(1, presenter.Shown);
            Assert.Contains(fake.Calls, c => ContainsSequence(c, "overlay", "map"));
            Assert.Contains("F-16C_50", vm.OverlayPlaneOptions);

            const string stick = "Test Stick {00000000-0000-0000-0000-000000000042}";
            vm.OnOverlayInput(new JoystickInputEvent(stick, "JOY_BTN1", true));
            OverlayLine line = Assert.Single(vm.OverlayLines);
            Assert.Equal("Gear Toggle", line.Text);
            Assert.Equal("Landing Gear Lever", line.Detail);
            vm.OnOverlayInput(new JoystickInputEvent(stick, "JOY_BTN1", false));
            vm.OnOverlayInput(new JoystickInputEvent(stick, "JOY_BTN5", true));
            vm.OnOverlayInput(new JoystickInputEvent(stick, "JOY_BTN1", true));
            Assert.Equal("Flaps Up", vm.OverlayLines[0].Text);

            fake.Calls.Clear();
            vm.OnDcsMessage("plane A-10C_2");
            await Task.Delay(50);
            IReadOnlyList<string> map = Assert.Single(fake.Calls, c => ContainsSequence(c, "overlay", "map"));
            Assert.True(ContainsSequence(map, "--unit", "A-10C_2"));
            Assert.True(ContainsSequence(map, "--game", "DCS"));
        }
        finally
        {
            await vm.ToggleOverlayCommand.ExecuteAsync(null);
        }
        Assert.False(vm.IsOverlayRunning);
        Assert.Equal(1, presenter.Hidden);
        Assert.Empty(vm.OverlayLines);

        fake.Calls.Clear();
        await vm.InstallDcsHookCommand.ExecuteAsync(null);
        IReadOnlyList<string> hook = Assert.Single(fake.Calls, c => ContainsSequence(c, "overlay", "dcs-hook", "install"));
        Assert.Contains("--path", hook);
        Assert.Contains("installed", vm.DcsHookStatus);
    }

    [Fact]
    public async Task GameInstalls_BlockGamesThatAreNotInstalledUntilTheirFolderIsSet()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        // Before the first check nothing is blocked.
        Assert.True(vm.CanRunIl2);

        await vm.RefreshGameInstallsCommand.ExecuteAsync(null);
        Assert.Equal(6, vm.GameInstalls.Count);
        Assert.True(vm.IsDcsInstalled);
        Assert.False(vm.IsIl2Installed);
        Assert.False(vm.CanRunIl2);
        Assert.True(vm.CanRunDcs);
        Assert.True(vm.CanRunStarCitizen);
        Assert.Contains("IL-2", vm.NotInstalledHint);
        Assert.Contains("4 of 6", vm.GameInstallsStatus);
        GameInstallItem il2 = vm.GameInstalls.Single(g => g.GameId == "IL2Game");
        Assert.Equal("Not installed", il2.Status);

        // Registry games: War Thunder is marked and its buttons are off.
        await vm.RefreshRegistryGamesCommand.ExecuteAsync(null);
        RegistryGameItem warThunder = vm.RegistryGames.Single(g => g.GameId == "WarThunder");
        Assert.False(warThunder.Installed);
        Assert.EndsWith("(not installed)", warThunder.Label);
        vm.SelectedRegistryGame = warThunder;
        Assert.False(vm.CanRunRegistryGame);
        vm.SelectedRegistryGame = vm.RegistryGames.Single(g => g.GameId == "MSFS2024");
        Assert.True(vm.CanRunRegistryGame);

        // A backup of a game that is not installed cannot be restored.
        vm.SelectedGameBackup = new GameBackupItem("x", "IL2Game", "2026-10-01", 1, "engine il2 export");
        Assert.False(vm.CanRestoreGameBackup);
        vm.SelectedGameBackup = new GameBackupItem("y", "DCS", "2026-10-01", 1, "engine dcs export");
        Assert.True(vm.CanRestoreGameBackup);

        // The user's folder makes IL-2 installed.
        fake.Calls.Clear();
        await vm.SetGameInstallFolderAsync(il2, "D:/Games/IL-2");
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => ContainsSequence(c, "games", "install", "set"));
        Assert.True(ContainsSequence(set, "set", "IL2Game", "D:/Games/IL-2"));
        Assert.True(vm.IsIl2Installed);
        Assert.True(vm.CanRunIl2);
        GameInstallItem il2Now = vm.GameInstalls.Single(g => g.GameId == "IL2Game");
        Assert.True(il2Now.Overridden);
        Assert.Equal(1, vm.GameInstalls.IndexOf(il2Now));

        fake.Calls.Clear();
        await vm.ClearGameInstallFolderCommand.ExecuteAsync(il2Now);
        Assert.Contains(fake.Calls, c => ContainsSequence(c, "games", "install", "clear", "IL2Game"));
    }

    [Fact]
    public async Task GameSetup_SaysWhatWasFound_LinksPortableFoldersAndHidesUntickedGames()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshGameInstallsCommand.ExecuteAsync(null);

        // Test instances never show the first-start setup (it would touch the user's settings).
        Assert.False(vm.ShouldShowFirstStartSetup);
        vm.BuildGameSetup(firstStart: true);
        Assert.Contains("Welcome", vm.GameSetupTitle);
        Assert.Equal(6, vm.GameSetupItems.Count);
        Assert.Contains("found 4 of its 6 games", vm.GameSetupSummary);
        Assert.Contains("DCS World (known place)", vm.GameSetupSummary);
        Assert.Contains("Microsoft Flight Simulator 2024 (steam)", vm.GameSetupSummary);
        Assert.Contains("Microsoft Flight Simulator 2020 (steam)", vm.GameSetupSummary);

        // Found games are ticked, the others not; found ones come first.
        GameSetupItem dcs = vm.GameSetupItems.Single(i => i.GameId == "DCS");
        GameSetupItem il2 = vm.GameSetupItems.Single(i => i.GameId == "IL2Game");
        Assert.True(dcs.HasGame);
        Assert.Equal("Found", dcs.DetectedLabel);
        Assert.False(il2.HasGame);
        Assert.Equal("Not found", il2.DetectedLabel);
        Assert.True(vm.GameSetupItems.IndexOf(il2) > vm.GameSetupItems.IndexOf(vm.GameSetupItems.Single(i => i.GameId == "MSFS2024")));

        // A portable IL-2: linking ticks the game; the marker check is the engine's (bin\game\Il-2.exe).
        string portable = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "portable-il2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(portable, "bin", "game"));
        try
        {
            il2.LinkedFolder = portable;
            Assert.True(il2.HasGame);
            Assert.True(il2.MarkerProblem);
            Assert.Contains("kept", il2.MarkerStatus);
            File.WriteAllText(Path.Combine(portable, "bin", "game", "Il-2.exe"), string.Empty);
            il2.LinkedFolder = string.Empty;
            il2.LinkedFolder = portable;
            Assert.True(il2.MarkerOk);
            // IL-2 keeps its controls in the installation: no config location of its own.
            Assert.False(il2.CanChangeConfig);
            Assert.True(il2.HasConfigNote);

            // DCS: the Saved Games variant.
            Assert.True(dcs.CanChangeConfig);
            Assert.True(dcs.ConfigIsFolder);
            dcs.ConfigLink = "D:/Saved Games/DCS.openbeta";
            Assert.Contains("(yours)", dcs.ConfigText);

            // The user does not have MSFS (found, unticked). War Thunder was not found and stays unticked: it is not
            // hidden, so JoyPro finds it once it is installed.
            vm.GameSetupItems.Single(i => i.GameId == "MSFS2020").HasGame = false;
            GameSetupItem warThunderRow = vm.GameSetupItems.Single(i => i.GameId == "WarThunder");
            Assert.False(warThunderRow.HasGame);
            Assert.True(warThunderRow.ShowHideChoice);
            Assert.False(warThunderRow.HideOnDone);

            fake.Calls.Clear();
            Assert.True(await vm.ApplyGameSetupAsync());
            Assert.Contains(fake.Calls, c => ContainsSequence(c, "games", "install", "set", "IL2Game", portable));
            Assert.Contains(fake.Calls, c => ContainsSequence(c, "games", "config", "set", "DCS", "D:/Saved Games/DCS.openbeta"));
            IReadOnlyList<string> disable = Assert.Single(fake.Calls, c => ContainsSequence(c, "games", "install", "disable"));
            Assert.Contains("MSFS2020", disable);
            Assert.DoesNotContain("MSFS2024", disable);
            Assert.DoesNotContain("WarThunder", disable);
            Assert.DoesNotContain("StarCitizen", disable);
            Assert.DoesNotContain("IL2Game", disable);
            Assert.DoesNotContain("DCS", disable);
            Assert.DoesNotContain(fake.Calls, c => ContainsSequence(c, "games", "install", "enable"));
            Assert.DoesNotContain(fake.Calls, c => ContainsSequence(c, "games", "install", "set", "DCS"));
            Assert.Contains(fake.Calls, c => ContainsSequence(c, "games", "installs"));
        }
        finally
        {
            Directory.Delete(portable, recursive: true);
        }

        // Run setup again (Settings > Games): detects again and builds the same list.
        fake.Calls.Clear();
        await vm.RunGameSetupAgainCommand.ExecuteAsync(null);
        Assert.Contains(fake.Calls, c => ContainsSequence(c, "games", "installs"));
        Assert.False(vm.GameSetupFirstStart);
        Assert.Equal(6, vm.GameSetupItems.Count);

        // "I don't have it" on a game JoyPro did not find hides it explicitly.
        vm.GameSetupItems.Single(i => i.GameId == "WarThunder").ExplicitlyHidden = true;
        fake.Calls.Clear();
        Assert.True(await vm.ApplyGameSetupAsync());
        IReadOnlyList<string> hideWt = Assert.Single(fake.Calls, c => ContainsSequence(c, "games", "install", "disable"));
        Assert.Equal("WarThunder", hideWt[^1]);

        // Later / closing writes nothing and hides nothing, even with found games unticked.
        vm.BuildGameSetup(firstStart: true);
        foreach (GameSetupItem row in vm.GameSetupItems)
            row.HasGame = false;
        fake.Calls.Clear();
        vm.SkipGameSetup();
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task GamesTheUserDoesNotHave_AreHiddenFromTheGameLists()
    {
        FakeEngineCliClient fake = new() { UseHiddenGamesScenario = true };
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshGameInstallsCommand.ExecuteAsync(null);

        // Settings > Games: War Thunder is listed apart.
        Assert.True(vm.IsGameHidden("WarThunder"));
        GameInstallItem warThunder = Assert.Single(vm.HiddenGameInstalls);
        Assert.Equal("WarThunder", warThunder.GameId);
        Assert.Equal("You do not have it", warThunder.Status);
        Assert.DoesNotContain(vm.OwnedGameInstalls, g => g.GameId == "WarThunder");
        Assert.Equal(5, vm.OwnedGameInstalls.Count);
        Assert.True(vm.HasHiddenGames);

        // The DB games list, the layout game options and the relation node game picker leave it out.
        await vm.RefreshDbGamesCommand.ExecuteAsync(null);
        Assert.DoesNotContain("WarThunder", vm.DbGames);
        Assert.Contains("DCS", vm.DbGames);
        Assert.DoesNotContain(vm.LayoutGameOptions, o => o.Value == "WarThunder");
        Assert.DoesNotContain("WarThunder", vm.RelationNodeGameOptions);

        // So do the backup list and its game filter.
        await vm.RefreshGameBackupsCommand.ExecuteAsync(null);
        Assert.DoesNotContain("WarThunder", vm.GameBackupGames);
        Assert.DoesNotContain(vm.GameBackups, b => b.Game == "WarThunder");
        Assert.Contains("DCS", vm.GameBackupGames);

        // The registry game lists leave it out.
        await vm.RefreshRegistryGamesCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.RegistryGames, g => g.GameId == "WarThunder");
        Assert.Contains(vm.RegistryGames, g => g.GameId == "MSFS2024");
        Assert.Contains(vm.RegistryGames, g => g.GameId == "MSFS2020");

        // The export page: no tile and no "not installed" warning, only a quiet note.
        await vm.RefreshExportGamesCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.ExportGames, g => g.GameId == "WarThunder");
        Assert.Contains("Hidden", vm.ExportGamesNotInstalled);
        Assert.Contains("War Thunder", vm.ExportGamesNotInstalled);
        Assert.DoesNotContain("War Thunder.", vm.ExportGamesNotInstalled.Split("Hidden")[0]);

        // "I have it" brings it back through the engine.
        fake.Calls.Clear();
        await vm.ToggleGameOwnedCommand.ExecuteAsync(warThunder);
        Assert.Contains(fake.Calls, c => ContainsSequence(c, "games", "install", "enable", "WarThunder"));

        // The setup starts with War Thunder unticked; ticking it enables it on Done.
        vm.BuildGameSetup(firstStart: false);
        GameSetupItem wt = vm.GameSetupItems.Single(i => i.GameId == "WarThunder");
        Assert.False(wt.HasGame);
        wt.HasGame = true;
        fake.Calls.Clear();
        Assert.True(await vm.ApplyGameSetupAsync());
        Assert.Contains(fake.Calls, c => ContainsSequence(c, "games", "install", "enable", "WarThunder"));
    }

    [Fact]
    public async Task ExportTiles_ListGamesAndExportTickedAircraft()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        await vm.RefreshExportGamesCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.ExportGames.Count);
        ExportGameItem dcs = vm.ExportGames.Single(g => g.GameId == "DCS");
        Assert.Equal(2, dcs.Planes.Count);
        Assert.Contains("2 aircraft", dcs.Summary);

        // Every aircraft ticked: a normal DCS export.
        fake.Calls.Clear();
        await vm.ExportGameNowCommand.ExecuteAsync(dcs);
        IReadOnlyList<string> all = Assert.Single(fake.Calls, c => ContainsSequence(c, "dcs", "export"));
        Assert.DoesNotContain("--planes", all);
        Assert.StartsWith("Exported", dcs.ExportState);

        // One aircraft unticked: only the ticked one.
        dcs.Planes.Single(p => p.Plane == "A-10C").IsSelected = false;
        Assert.Contains("1 of 2 aircraft", dcs.Summary);
        fake.Calls.Clear();
        await vm.ExportGameNowCommand.ExecuteAsync(dcs);
        IReadOnlyList<string> some = Assert.Single(fake.Calls, c => ContainsSequence(c, "dcs", "export"));
        Assert.True(ContainsSequence(some, "--planes", "F-16C_50"));

        // X4 is in the profile but not installed: no tile, a hint instead.
        Assert.DoesNotContain(vm.ExportGames, g => g.GameId == "X4");
        Assert.Contains("X4: Foundations", vm.ExportGamesNotInstalled);

        // Export ticked games: DCS and War Thunder (the IL-2 tile is unticked).
        vm.ExportGames.Single(g => g.GameId == "IL2Game").IsSelected = false;
        fake.Calls.Clear();
        await vm.ExportSelectedGamesCommand.ExecuteAsync(null);
        Assert.Contains(fake.Calls, c => ContainsSequence(c, "dcs", "export"));
        Assert.Contains(fake.Calls, c => ContainsSequence(c, "warthunder", "export"));
        Assert.DoesNotContain(fake.Calls, c => ContainsSequence(c, "il2", "export"));
        Assert.StartsWith("Exported", vm.ExportGames.Single(g => g.GameId == "WarThunder").ExportState);
    }

    [Fact]
    public async Task GameBackups_ListFilterAndRestore_BuildEngineArguments()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        vm.GoExportCommand.Execute(null);
        await vm.RefreshGameBackupsCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.GameBackups.Count);
        Assert.Contains("DCS", vm.GameBackupGames);
        Assert.Contains("X4", vm.GameBackupGames);
        Assert.Equal("20261001-120000-000_DCS", vm.SelectedGameBackup?.Id);
        Assert.Equal(3, vm.SelectedGameBackup?.FileCount);

        vm.SelectedGameBackupGame = "X4";
        await vm.RefreshGameBackupsCommand.ExecuteAsync(null);
        GameBackupItem x4 = Assert.Single(vm.GameBackups);
        Assert.Equal("before restore", x4.Kind);

        fake.Calls.Clear();
        await vm.PreviewRestoreGameBackupCommand.ExecuteAsync(null);
        IReadOnlyList<string> preview = Assert.Single(fake.Calls, c => ContainsSequence(c, "backups", "restore"));
        Assert.True(ContainsSequence(preview, "restore", "20261001-110000-000_X4", "--dry-run"));
        Assert.DoesNotContain("--profile", preview);

        fake.Calls.Clear();
        await vm.RestoreGameBackupCommand.ExecuteAsync(null);
        IReadOnlyList<string> restore = Assert.Single(fake.Calls, c => ContainsSequence(c, "backups", "restore"));
        Assert.DoesNotContain("--dry-run", restore);
        Assert.Contains(fake.Calls, c => ContainsSequence(c, "backups", "list"));
    }

    [Fact]
    public async Task RegistryGames_ImportWithoutMerge_ReplacesAndOpensRelations()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshRegistryGamesCommand.ExecuteAsync(null);
        vm.SelectedRegistryGame = vm.RegistryGames.Single(g => g.GameId == "WarThunder");
        vm.ImportMerge = false;
        vm.RegistryGameProfileName = "ignored for games without profiles";

        await vm.ImportRegistryGameCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "warthunder", "import"));
        Assert.Contains("--replace", call);
        Assert.DoesNotContain("--game-profile", call);
        Assert.Equal(UiStage.Relations, vm.CurrentStage);
    }

    // MSFS keeps the binds of every device it ever saw: import and scan take the connected devices unless the user
    // ticks "Also import devices that are not connected"; other games never get the option.
    [Fact]
    public async Task RegistryGames_MsfsImport_IncludesDisconnectedDevicesOnlyWhenTicked()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshRegistryGamesCommand.ExecuteAsync(null);
        vm.SelectedRegistryGame = vm.RegistryGames.Single(g => g.GameId == "MSFS2024");
        Assert.True(vm.SelectedRegistryGameKeepsDisconnectedDevices);
        Assert.False(vm.ImportIncludeDisconnected);

        await vm.ImportRegistryGameCommand.ExecuteAsync(null);
        Assert.DoesNotContain("--include-disconnected", Assert.Single(fake.Calls, c => ContainsSequence(c, "msfs2024", "import")));
        Assert.DoesNotContain("--include-sim-defaults", Assert.Single(fake.Calls, c => ContainsSequence(c, "msfs2024", "import")));

        fake.Calls.Clear();
        vm.ImportIncludeDisconnected = true;
        vm.ImportIncludeSimDefaults = true;
        await vm.ImportRegistryGameCommand.ExecuteAsync(null);
        Assert.Contains("--include-disconnected", Assert.Single(fake.Calls, c => ContainsSequence(c, "msfs2024", "import")));
        Assert.Contains("--include-sim-defaults", Assert.Single(fake.Calls, c => ContainsSequence(c, "msfs2024", "import")));
        await vm.ScanRegistryGameCommand.ExecuteAsync(null);
        Assert.Contains("--include-disconnected", Assert.Single(fake.Calls, c => ContainsSequence(c, "msfs2024", "scan")));
        Assert.Contains("--include-sim-defaults", Assert.Single(fake.Calls, c => ContainsSequence(c, "msfs2024", "scan")));

        fake.Calls.Clear();
        vm.SelectedRegistryGame = vm.RegistryGames.Single(g => g.GameId == "WarThunder");
        Assert.False(vm.SelectedRegistryGameKeepsDisconnectedDevices);
        await vm.ImportRegistryGameCommand.ExecuteAsync(null);
        Assert.DoesNotContain("--include-disconnected", Assert.Single(fake.Calls, c => ContainsSequence(c, "warthunder", "import")));
        Assert.DoesNotContain("--include-sim-defaults", Assert.Single(fake.Calls, c => ContainsSequence(c, "warthunder", "import")));
    }

    [Fact]
    public async Task RefreshDevices_NoProfile_DoesNotRequireProfileFlag_AndParsesSections()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.ProfilePath = string.Empty;

        await vm.RefreshDevicesCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "devices", "list"));
        Assert.DoesNotContain("--profile", call);
        Assert.Contains("--connected", call);
        Assert.DoesNotContain("--profile-devices", call);

        Assert.Contains("Test Connected Device {00000000-0000-0000-0000-000000000001}", vm.ConnectedDevices);
    }

    [Fact]
    public async Task DbGames_QuickActionFromStart_IgnoresSettingsFilter_AndParsesGameNamesOnly()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.CurrentStage = UiStage.Start;
        vm.DbGameFilter = "FILTER_GAME_SHOULD_NOT_BE_USED";

        await vm.RefreshDbGamesCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "db", "games"));
        Assert.DoesNotContain("FILTER_GAME_SHOULD_NOT_BE_USED", call);

        Assert.Contains("DCS", vm.DbGames);
        Assert.Contains("IL2Game", vm.DbGames);
        Assert.Contains("StarCitizen", vm.DbGames);
        Assert.DoesNotContain(vm.DbGames, v => v.Contains("|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DbPlanes_QuickActionFromStart_IgnoresSettingsFilters_AndParsesPlanes()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.CurrentStage = UiStage.Start;
        vm.DbPlaneFilter = "FILTER_PLANE_SHOULD_NOT_BE_USED";
        vm.SelectedDbGame = "FILTER_GAME_SHOULD_NOT_BE_USED";

        await vm.RefreshDbPlanesCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "db", "planes"));
        Assert.DoesNotContain("FILTER_PLANE_SHOULD_NOT_BE_USED", call);
        Assert.DoesNotContain("--game", call);

        Assert.Contains("A-10C_2", vm.DbPlanes);
        Assert.Contains("FA-18C_hornet", vm.DbPlanes);
    }

    [Fact]
    public async Task RefreshRelations_IgnoresTextOutputToggle_AndParsesRelations()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.UseTextOutput = false;

        await vm.RefreshRelationsCommand.ExecuteAsync(null);

        // The toggle only applies to hand-typed commands; the relation list is always asked as JSON (the games and
        // aircraft of every action for the filters, the modifiers of the bind), a text answer is still read.
        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "relations", "list"));
        Assert.DoesNotContain("--text", call);
        Assert.Contains(vm.Relations, v => string.Equals(v.Name, "TestRelation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateProfile_WithErrors_ShowsErrorIssuesDespiteExitCode()
    {
        FakeEngineCliClient fake = new() { UseValidationErrorsScenario = true };
        MainWindowViewModel vm = CreateVm(fake);
        vm.ValidationShowErrors = true;
        vm.ValidationShowWarnings = false;

        await vm.ValidateProfileCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.ValidationIssues.Count);
        Assert.All(vm.ValidationIssues, issue => Assert.Equal("Error", issue.Type));
        Assert.Contains(vm.ValidationIssues, issue => issue.Message.Contains("Missing Stick", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunEngine_WithLoadCorrectionsOff_SendsNoCorrectionOptions()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.UseLoadCorrections = false;
        fake.Calls.Clear();

        await vm.RefreshRelationsCommand.ExecuteAsync(null);

        // The engine rejects --correction-mode / --auto-add-db-items true without --load-corrections true.
        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "relations", "list"));
        Assert.True(ContainsSequence(call, "--load-corrections", "false"));
        Assert.DoesNotContain("--correction-mode", call);
        Assert.DoesNotContain("--auto-add-db-items", call);
        Assert.DoesNotContain("--remap-missing-devices", call);
    }

    [Fact]
    public async Task SetBind_OnButtonRelation_DoesNotSendAxisOptions()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);

        BindListItem? buttonBind = vm.Binds.FirstOrDefault(b => !b.IsAxis);
        Assert.NotNull(buttonBind);
        vm.SelectedBind = buttonBind;
        vm.BindInverted = true;
        fake.Calls.Clear();

        await vm.SetSelectedBindCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "binds", "set"));
        Assert.DoesNotContain("--inverted", call);
        Assert.DoesNotContain("--slider", call);
        Assert.Contains("--double-tap", call);
    }

    [Fact]
    public async Task RecordInput_OnUnboundAxisRow_CapturesAnAxisAndSavesTheBind()
    {
        const string vjoy = "vJoy Device {89D63500-B53F-11f1-8002-444553540000}";
        FakeEngineCliClient fake = new() { UseUnboundAxisScenario = true };
        FakeJoystickCaptureService capture = new()
        {
            NextCapture = new JoystickCaptureService.CaptureResult { Device = vjoy, Input = "JOY_X" }
        };
        MainWindowViewModel vm = CreateVm(fake, capture);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);

        BindListItem pitch = Assert.Single(vm.Binds, b => b.RelationName == "Pitch");
        Assert.False(pitch.HasBind);
        Assert.Equal("<unbound>", pitch.Device);
        vm.SelectedBind = pitch;
        await Task.Delay(50);
        Assert.Equal(string.Empty, vm.BindDeviceInput);
        fake.Calls.Clear();

        await vm.RecordNextInputCommand.ExecuteAsync(null);

        // An axis relation waits for an axis, and Record binds right away (no hidden Set Bind step).
        Assert.True(capture.LastOptions!.AxisMode);
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => ContainsSequence(c, "binds", "set"));
        Assert.True(ContainsSequence(set, "binds", "set", "Pitch"));
        Assert.True(ContainsSequence(set, "--device", vjoy));
        Assert.True(ContainsSequence(set, "--input", "JOY_X"));
        Assert.Contains("--profile", set);
        Assert.Contains(fake.Calls, c => ContainsSequence(c, "binds", "list"));
        Assert.Contains("Bound 'Pitch'", vm.RecordStatusText);
        Assert.False(vm.IsRecordingInput);
    }

    [Fact]
    public async Task RecordInput_OnButtonRelation_SavesTheCapturedButton()
    {
        FakeEngineCliClient fake = new();
        FakeJoystickCaptureService capture = new()
        {
            NextCapture = new JoystickCaptureService.CaptureResult { Device = "VKB Stick {00000000-0000-0000-0000-0000000000B1}", Input = "JOY_BTN97" }
        };
        MainWindowViewModel vm = CreateVm(fake, capture);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        vm.SelectedBind = vm.Binds.First(b => b.RelationName == "TestRelation");
        await Task.Delay(50);
        fake.Calls.Clear();

        await vm.RecordNextInputCommand.ExecuteAsync(null);

        Assert.False(capture.LastOptions!.AxisMode);
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => ContainsSequence(c, "binds", "set"));
        Assert.True(ContainsSequence(set, "binds", "set", "TestRelation"));
        Assert.True(ContainsSequence(set, "--input", "JOY_BTN97"));
        Assert.DoesNotContain("--inverted", set);
    }

    [Fact]
    public async Task RecordInput_WithoutInput_SavesNothingAndSaysWhatItWaitedFor()
    {
        FakeEngineCliClient fake = new() { UseUnboundAxisScenario = true };
        FakeJoystickCaptureService capture = new() { NextCapture = null, LastDiagnostics = "diag: winmm=0, directinput-enum=7, directinput-active=7" };
        MainWindowViewModel vm = CreateVm(fake, capture);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Pitch");
        await Task.Delay(50);
        fake.Calls.Clear();

        await vm.RecordNextInputCommand.ExecuteAsync(null);

        Assert.DoesNotContain(fake.Calls, c => ContainsSequence(c, "binds", "set"));
        Assert.Contains("axis", vm.RecordStatusText);
        Assert.Contains("directinput-active=7", vm.RecordStatusText);
        Assert.Equal("Record timed out.", vm.StatusText);
    }

    [Fact]
    public void RecordCapture_AxisMoveIsMeasuredFromTheBaselineAndTheFurthestAxisWins()
    {
        int[] baseline = [32767, 32767, 0, 0, 0, 0, 0, 0];
        // Small steps never trigger, however many of them there are: what counts is the distance from the baseline.
        Assert.Null(JoystickCaptureService.DetectAxisMove(baseline, [40000, 32767, 0, 0, 0, 0, 0, 0], 0, 65535, 30));
        Assert.Equal("JOY_X", JoystickCaptureService.DetectAxisMove(baseline, [55000, 36000, 0, 0, 0, 0, 0, 0], 0, 65535, 30));
        Assert.Equal("JOY_Y", JoystickCaptureService.DetectAxisMove(baseline, [55000, 65535, 0, 0, 0, 0, 0, 0], 0, 65535, 30));
        Assert.Equal("JOY_SLIDER2", JoystickCaptureService.DetectAxisMove(baseline, [32767, 32767, 0, 0, 0, 0, 0, 30000], 0, 65535, 30));
        Assert.Equal("JOY_RZ", JoystickCaptureService.DetectAxisMove(baseline, [32767, 32767, 0, 0, 0, 25000, 0, 0], 0, 65535, 30));

        // A device's first report after DirectInput's centred defaults: the jumped axes are re-centred, not recorded.
        int[] stale = [32767, 32767, 32767, 32767, 32767, 32767, 32767, 32767];
        int[] firstReport = [33000, 32767, 32767, 0, 0, 0, 0, 0];
        Assert.True(JoystickCaptureService.RebaseJumpedAxes(stale, firstReport, 0, 65535, 30));
        Assert.Null(JoystickCaptureService.DetectAxisMove(stale, firstReport, 0, 65535, 30));
        Assert.Equal("JOY_X", JoystickCaptureService.DetectAxisMove(stale, [60000, 32767, 32767, 0, 0, 0, 0, 0], 0, 65535, 30));
        Assert.False(JoystickCaptureService.RebaseJumpedAxes(stale, [33100, 32767, 32767, 0, 0, 0, 0, 0], 0, 65535, 30));
    }

    [Fact]
    public async Task DcsFfbSet_PassesProfileSoTheChangeIsSaved()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.DcsFfbDevice = "Test Device";
        fake.Calls.Clear();

        await vm.DcsFfbSetCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "dcs", "ffb", "set"));
        Assert.Contains("--profile", call);
    }

    [Fact]
    public void ExtractJsonPayload_IgnoresAutoSaveLineAfterPayload()
    {
        string output = "{\"Suggestions\":[]}\r\nAuto-saved profile: C:\\profiles\\test.pr0file\r\n";

        Assert.Equal("{\"Suggestions\":[]}", MainWindowViewModel.ExtractJsonPayload(output));
    }

    [Fact]
    public async Task LoadProfile_RefreshesRelations_EvenWhenStageAutoRefreshWouldOverlap()
    {
        FakeEngineCliClient fake = new()
        {
            DbGamesDelayMs = 120
        };
        MainWindowViewModel vm = CreateVm(fake);

        await vm.LoadProfileCommand.ExecuteAsync(null);

        Assert.Contains(fake.Calls, c => ContainsSequence(c, "profile", "load"));
        Assert.Contains(fake.Calls, c => ContainsSequence(c, "relations", "list"));
        Assert.NotEmpty(vm.Relations);
        Assert.Equal(UiStage.Relations, vm.CurrentStage);
    }

    [Fact]
    public async Task ImportDcs_SelectedPlanesAndDevices_UsesNewKeyboardAndEasyFlags()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        await vm.RefreshImportDcsPlaneOptionsCommand.ExecuteAsync(null);
        await vm.RefreshDevicesCommand.ExecuteAsync(null);

        vm.ImportAllDcsPlanes = false;
        vm.ImportUseSelectedDevices = true;
        vm.ImportKeyboardCommands = false;
        vm.ImportIgnoreEasyPlanes = true;
        vm.ImportIncludeDefaults = true;

        SelectableTagItem? plane = vm.ImportPlaneOptions.FirstOrDefault(v => v.Name == "A-10C_2");
        Assert.NotNull(plane);
        plane!.IsSelected = true;

        SelectableTagItem? device = vm.ImportDeviceOptions.FirstOrDefault(v =>
            v.Name.Contains("Test Connected Device", StringComparison.Ordinal));
        Assert.NotNull(device);
        device!.IsSelected = true;

        await vm.ImportDcsCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "dcs", "import"));
        Assert.Contains("--plane", call);
        Assert.Contains("A-10C_2", call);
        Assert.Contains("--device", call);
        Assert.Contains("Test Connected Device {00000000-0000-0000-0000-000000000001}", call);
        Assert.Contains("--include-keyboard", call);
        Assert.Contains("false", call);
        Assert.Contains("--ignore-easy-planes", call);
        Assert.Contains("true", call);
    }

    [Fact]
    public async Task ScanDcs_AllPlanes_DoesNotEmitPlaneFilters_ButEmitsToggleFlags()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        vm.ImportAllDcsPlanes = true;
        vm.ImportPlanesCsv = "A-10C_2,FA-18C_hornet";
        vm.ImportKeyboardCommands = true;
        vm.ImportIgnoreEasyPlanes = false;

        await vm.ScanDcsCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "dcs", "scan"));
        Assert.DoesNotContain("--plane", call);
        Assert.Contains("--include-keyboard", call);
        Assert.Contains("--ignore-easy-planes", call);
        Assert.Contains("true", call);
        Assert.Contains("false", call);
    }

    [Fact]
    public async Task DcsRefreshPlanes_AllNoneCheckboxes_ControlPlaneArgs()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Config", "Input", "A-10C_2"));
        Directory.CreateDirectory(Path.Combine(root, "Config", "Input", "FA-18C_hornet"));

        vm.DcsSavedGamesPath = root;

        Assert.Contains(vm.DcsRefreshPlaneOptions, v => string.Equals(v.Name, "A-10C_2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vm.DcsRefreshPlaneOptions, v => string.Equals(v.Name, "FA-18C_hornet", StringComparison.OrdinalIgnoreCase));

        vm.DcsRefreshSelectAllPlanes = true;
        fake.Calls.Clear();

        await vm.DcsRefreshIdCommand.ExecuteAsync(null);

        // "All" lets the engine refresh every DCS input profile instead of sending a possibly stale name list.
        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "dcs", "refresh", "id"));
        Assert.DoesNotContain(call, v => string.Equals(v, "--plane", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("A-10C_2,FA-18C_hornet", vm.DcsRefreshPlanesCsv, StringComparison.OrdinalIgnoreCase);

        vm.DcsRefreshSelectNonePlanes = true;
        vm.DcsRefreshPlaneOptions.First(v => string.Equals(v.Name, "A-10C_2", StringComparison.OrdinalIgnoreCase)).IsSelected = true;
        fake.Calls.Clear();

        await vm.DcsRefreshIdCommand.ExecuteAsync(null);

        call = Assert.Single(fake.Calls, c => ContainsSequence(c, "dcs", "refresh", "id"));
        Assert.Equal(1, call.Count(v => string.Equals(v, "--plane", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("A-10C_2", call);
        Assert.DoesNotContain("FA-18C_hornet", call);

        vm.DcsRefreshSelectNonePlanes = true;
        fake.Calls.Clear();

        await vm.DcsRefreshIdCommand.ExecuteAsync(null);

        call = Assert.Single(fake.Calls, c => ContainsSequence(c, "dcs", "refresh", "id"));
        Assert.DoesNotContain(call, v => string.Equals(v, "--plane", StringComparison.OrdinalIgnoreCase));
        Assert.True(string.IsNullOrWhiteSpace(vm.DcsRefreshPlanesCsv));
    }

    [Fact]
    public void DcsRefreshPaths_AutoPrefill_WhenExistingFilesAreFound()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        string savedGamesRoot = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", Guid.NewGuid().ToString("N"), "SavedGamesDcs");
        string installRoot = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", Guid.NewGuid().ToString("N"), "DcsInstall");

        string clicksXml = Path.Combine(savedGamesRoot, "Config", "Clicks.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(clicksXml)!);
        File.WriteAllText(clicksXml, "<clicks/>");

        string dcsExe = Path.Combine(installRoot, "bin", "DCS.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(dcsExe)!);
        File.WriteAllText(dcsExe, "stub");

        vm.DcsClicksXmlPath = string.Empty;
        vm.DcsClicksDirPath = string.Empty;
        vm.DcsExePath = string.Empty;

        vm.DcsInstallPath = installRoot;
        vm.DcsSavedGamesPath = savedGamesRoot;

        Assert.Equal(clicksXml, vm.DcsClicksXmlPath, StringComparer.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(vm.DcsClicksDirPath));
        Assert.True(Directory.Exists(vm.DcsClicksDirPath));
        Assert.Equal(dcsExe, vm.DcsExePath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportIl2_SelectedDevicesOnlyWithoutSelection_ShowsErrorAndSkipsCommand()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        vm.ImportUseSelectedDevices = true;
        vm.ImportDevicesCsv = string.Empty;

        await vm.ImportIl2Command.ExecuteAsync(null);

        Assert.Contains("Select at least one device", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fake.Calls, c => ContainsSequence(c, "il2", "import"));
    }

    [Fact]
    public async Task ImportStarCitizen_SelectedDevice_UsesSourceAndKeyboardFlags()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshDevicesCommand.ExecuteAsync(null);

        vm.ImportUseSelectedDevices = true;
        vm.ImportStarCitizenIncludeKeyboard = false;
        vm.ImportMerge = true;
        vm.StarCitizenSourcePath = @"C:\dummy\sc\actionmaps.xml";

        SelectableTagItem? device = vm.ImportDeviceOptions.FirstOrDefault(v =>
            v.Name.Contains("Test Connected Device", StringComparison.Ordinal));
        Assert.NotNull(device);
        device!.IsSelected = true;

        await vm.ImportStarCitizenCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "starcitizen", "import"));
        Assert.Contains("--path", call);
        Assert.Contains(vm.ImportStarCitizenPath, call);
        Assert.Contains("--source", call);
        Assert.Contains(vm.StarCitizenSourcePath, call);
        Assert.Contains("--merge", call);
        Assert.Contains("--device", call);
        Assert.Contains("Test Connected Device {00000000-0000-0000-0000-000000000001}", call);
        Assert.Contains("--include-keyboard", call);
        Assert.Contains("false", call);
    }

    [Fact]
    public async Task ScanStarCitizen_UsesIgnoreDeviceSelections()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshDevicesCommand.ExecuteAsync(null);

        SelectableTagItem? ignored = vm.ImportIgnoreDeviceOptions.FirstOrDefault(v =>
            v.Name.Contains("Test Connected Device", StringComparison.Ordinal));
        Assert.NotNull(ignored);
        ignored!.IsSelected = true;

        await vm.ScanStarCitizenCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "starcitizen", "scan"));
        Assert.Contains("--ignore-device", call);
        Assert.Contains("Test Connected Device {00000000-0000-0000-0000-000000000001}", call);
    }

    [Fact]
    public async Task ExportStarCitizen_UsesModeAndExportOptions()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.ExportMode = "merge-overwrite";
        vm.ExportStarCitizenKeepKeyboardDefaults = true;
        vm.ExportStarCitizenPreferLocalOrder = false;
        vm.StarCitizenSourcePath = @"C:\dummy\sc\defaultProfile.xml";

        await vm.ExportStarCitizenCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "starcitizen", "export"));
        Assert.Contains("--path", call);
        Assert.Contains(vm.ExportStarCitizenPath, call);
        Assert.Contains("--source", call);
        Assert.Contains(vm.StarCitizenSourcePath, call);
        Assert.Contains("--mode", call);
        Assert.Contains("merge-overwrite", call);
        Assert.Contains("--keep-keyboard-defaults", call);
        Assert.Contains("true", call);
        Assert.Contains("--prefer-local-order", call);
        Assert.Contains("false", call);
    }

    [Fact]
    public async Task AddRelation_WithEmptyProfilePath_AutoCreatesFreshProfile_NotLegacyAutosave()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.ProfilePath = string.Empty;
        string priorLastProfile = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "last-opened.pr0file");
        vm.LastProfilePath = priorLastProfile;
        vm.RelationNameInput = "FreshAutoProfileRelation";
        vm.RelationNodeGameInput = "DCS";
        vm.SelectedRelationNodeSearchResult = new DbNodeSearchResultItem
        {
            Game = "DCS",
            Plane = "A-10C",
            NodeId = "a201cdnil",
            Title = "Wheel Brake",
            Description = "Wheel brake control"
        };

        await vm.AddRelationCommand.ExecuteAsync(null);

        IReadOnlyList<string> addCall = Assert.Single(
            fake.Calls,
            c => ContainsSequence(c, "relations", "add", "FreshAutoProfileRelation"));
        Assert.Contains("--node", addCall);
        Assert.DoesNotContain(addCall, v => string.Equals(v, "--axis", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(addCall, v => string.Equals(v, "--button", StringComparison.OrdinalIgnoreCase));

        int profileIndex = addCall.ToList().FindIndex(v => string.Equals(v, "--profile", StringComparison.OrdinalIgnoreCase));
        Assert.True(profileIndex >= 0 && profileIndex + 1 < addCall.Count, "Add relation call should include --profile <path>.");
        string createdProfilePath = addCall[profileIndex + 1];
        Assert.EndsWith(".pr0file", createdProfilePath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("autosave.pr0file", createdProfilePath, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(priorLastProfile, createdProfilePath);
        Assert.Equal(createdProfilePath, vm.ProfilePath);
    }

    // The editor's Duplicate button copies exactly the open relation with one engine call (one undo step).
    [Fact]
    public async Task DuplicateSelectedRelation_RunsOneMassDuplicateForTheOpenRelation()
    {
        FakeEngineCliClient fake = new() { UseMultiSortScenario = true };
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        vm.SelectedRelation = vm.Relations.First();
        string name = vm.SelectedRelation.Name;
        fake.Calls.Clear();

        await vm.DuplicateSelectedRelationCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "relations", "mass", "duplicate"));
        Assert.True(ContainsSequence(call, "--relation", name));
        Assert.DoesNotContain("--all", call);
        Assert.DoesNotContain("--no-bind", call);
    }

    // "Duplicate without bind" (editor header menu) and the card's context menu (Duplicate / without bind / Delete on
    // the right-clicked card, which need not be the open one).
    [Fact]
    public async Task DuplicateWithoutBind_AndTheCardContextMenu_ActOnTheirRelation()
    {
        FakeEngineCliClient fake = new() { UseMultiSortScenario = true };
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        vm.SelectedRelation = vm.Relations.First();
        string open = vm.SelectedRelation.Name;
        RelationListItem other = vm.Relations.Last();
        Assert.NotEqual(open, other.Name);
        fake.Calls.Clear();

        await vm.DuplicateSelectedRelationWithoutBindCommand.ExecuteAsync(null);
        IReadOnlyList<string> header = Assert.Single(fake.Calls, c => ContainsSequence(c, "relations", "mass", "duplicate"));
        Assert.True(ContainsSequence(header, "--relation", open));
        Assert.Contains("--no-bind", header);

        fake.Calls.Clear();
        await vm.DuplicateRelationCommand.ExecuteAsync(other);
        IReadOnlyList<string> card = Assert.Single(fake.Calls, c => ContainsSequence(c, "relations", "mass", "duplicate"));
        Assert.True(ContainsSequence(card, "--relation", other.Name));
        Assert.DoesNotContain("--no-bind", card);

        fake.Calls.Clear();
        await vm.DuplicateRelationWithoutBindCommand.ExecuteAsync(other);
        IReadOnlyList<string> cardNoBind = Assert.Single(fake.Calls, c => ContainsSequence(c, "relations", "mass", "duplicate"));
        Assert.True(ContainsSequence(cardNoBind, "--relation", other.Name));
        Assert.Contains("--no-bind", cardNoBind);
    }

    [Fact]
    public async Task OpenLastProfileCommand_LoadsStoredLastProfilePath()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        string tempProfile = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", Guid.NewGuid().ToString("N") + ".pr0file");
        Directory.CreateDirectory(Path.GetDirectoryName(tempProfile)!);
        File.WriteAllText(tempProfile, "test");

        vm.ProfilePath = string.Empty;
        vm.LastProfilePath = tempProfile;

        await vm.OpenLastProfileCommand.ExecuteAsync(null);

        Assert.Equal(tempProfile, vm.ProfilePath);
        Assert.Contains(fake.Calls, c => ContainsSequence(c, "profile", "load", tempProfile));
    }

    [Fact]
    public async Task RefreshImportDcsPlaneOptions_PrefersConfigInputDirectories()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Config", "Input", "A-10C_2"));
        Directory.CreateDirectory(Path.Combine(root, "Config", "Input", "FA-18C_hornet"));
        vm.ImportDcsPath = root;

        await vm.RefreshImportDcsPlaneOptionsCommand.ExecuteAsync(null);

        Assert.Contains(vm.ImportPlaneOptions, v => string.Equals(v.Name, "A-10C_2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vm.ImportPlaneOptions, v => string.Equals(v.Name, "FA-18C_hornet", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fake.Calls, c => ContainsSequence(c, "db", "planes"));
    }

    [Fact]
    public void ImportDcsPathChange_PopulatesPlaneCheckboxes_FromConfigInputDirectories()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Config", "Input", "F-16C_50"));
        Directory.CreateDirectory(Path.Combine(root, "Config", "Input", "MiG-21Bis"));

        vm.ImportDcsPath = root;

        Assert.Contains(vm.ImportPlaneOptions, v => string.Equals(v.Name, "F-16C_50", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vm.ImportPlaneOptions, v => string.Equals(v.Name, "MiG-21Bis", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanDcs_UsesIgnoreDeviceCheckboxSelections()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshDevicesCommand.ExecuteAsync(null);

        SelectableTagItem? ignored = vm.ImportIgnoreDeviceOptions.FirstOrDefault(v =>
            v.Name.Contains("Test Connected Device", StringComparison.Ordinal));
        Assert.NotNull(ignored);
        ignored!.IsSelected = true;

        await vm.ScanDcsCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "dcs", "scan"));
        Assert.Contains("--ignore-device", call);
        Assert.Contains("Test Connected Device {00000000-0000-0000-0000-000000000001}", call);
    }

    [Fact]
    public async Task SuggestLayouts_ParsesJson_AndApplySelectedBuildsLayoutMap()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.LayoutSuggestIncludeProfileDevices = false;
        vm.LayoutSuggestApplyToProfile = false;
        vm.LayoutDevicesCsv = "Test Connected Device {00000000-0000-0000-0000-000000000001}";

        await vm.SuggestLayoutsCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "layout", "suggest"));
        Assert.DoesNotContain("--text", call);
        Assert.Contains("--device", call);
        Assert.Contains("--connected", call);
        Assert.Contains("--apply", call);
        Assert.Contains("false", call);

        Assert.Single(vm.LayoutSuggestions);
        LayoutSuggestionItem first = vm.LayoutSuggestions[0];
        Assert.Equal("VKB Gunfighter MK3", first.TemplateName);
        Assert.True(first.Score > 0);

        first.IsSelected = true;
        vm.ApplySelectedLayoutSuggestionsCommand.Execute(null);

        Assert.Contains("--layout-map", vm.LayoutMapInput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Test Connected Device", vm.LayoutDevicesCsv, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuggestLayouts_NoProfile_DoesNotSendProfileArgs_AndDisablesApply()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.ProfilePath = string.Empty;
        vm.LayoutSuggestIncludeConnected = false;
        vm.LayoutSuggestIncludeProfileDevices = true;
        vm.LayoutSuggestApplyToProfile = true;
        vm.LayoutDevicesCsv = string.Empty;

        await vm.SuggestLayoutsCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "layout", "suggest"));
        Assert.DoesNotContain("--text", call);
        Assert.DoesNotContain("--profile", call);
        Assert.Contains("--connected", call);
        Assert.DoesNotContain("--profile-devices", call);
        int applyIndex = call.ToList().FindIndex(v => string.Equals(v, "--apply", StringComparison.OrdinalIgnoreCase));
        Assert.True(applyIndex >= 0 && applyIndex + 1 < call.Count);
        Assert.Equal("false", call[applyIndex + 1], StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LayoutStage_AutoRefreshesDevicesAndSuggestions()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        vm.CurrentStage = UiStage.Layout;
        await Task.Delay(120);

        Assert.Contains(fake.Calls, c => ContainsSequence(c, "devices", "list"));
        Assert.Contains(fake.Calls, c => ContainsSequence(c, "layout", "suggest"));
    }

    [Fact]
    public async Task RenderLayout_IncludesMarkerRenderArguments()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", Guid.NewGuid().ToString("N") + ".pr0file");
        vm.LayoutDrawMarkers = true;
        vm.LayoutDrawLegendTable = true;
        vm.LayoutMarkerSize = "14";
        vm.LayoutModifierColors = "LAlt=#38BDF8";
        vm.LayoutButtonSymbol = "square";
        vm.LayoutAxisSymbol = "diamond";
        vm.LayoutPovSymbol = "triangle";
        vm.LayoutSliderSymbol = "bar";
        vm.LayoutLegendWidth = "600";
        vm.LayoutLegendMaxRows = "abc";
        vm.LayoutLegendFontSize = "12.5";

        await vm.RenderLayoutCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "layout", "render"));
        Assert.True(ContainsSequence(call, "--legend-width", "600"));
        Assert.True(ContainsSequence(call, "--legend-max-rows", "20"));
        Assert.True(ContainsSequence(call, "--legend-font-size", "12.5"));
        Assert.Contains("--draw-markers", call);
        Assert.Contains("--draw-legend-table", call);
        Assert.Contains("--marker-size", call);
        Assert.Contains("14", call);
        Assert.Contains("--modifier-colors", call);
        Assert.Contains("LAlt=#38BDF8", call);
        Assert.Contains("--button-symbol", call);
        Assert.Contains("square", call);
        Assert.Contains("--axis-symbol", call);
        Assert.Contains("--pov-symbol", call);
        Assert.Contains("--slider-symbol", call);
    }

    [Fact]
    public async Task RenderLayout_SelectedDeviceWithoutRememberedLayout_ShowsWarningAndSkipsRender()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", Guid.NewGuid().ToString("N") + ".pr0file");
        string warning = string.Empty;
        vm.WarningRequested += msg => warning = msg;

        await vm.RefreshDevicesCommand.ExecuteAsync(null);
        SelectableTagItem? device = vm.LayoutDeviceOptions.FirstOrDefault(v =>
            v.Name.Contains("Test Connected Device", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(device);
        device!.IsSelected = true;

        await vm.RenderLayoutCommand.ExecuteAsync(null);

        Assert.DoesNotContain(fake.Calls, c => ContainsSequence(c, "layout", "render"));
        Assert.Contains("missing layout template", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing layout template", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RenderLayout_SelectedDeviceWithRememberedLayout_AddsLayoutMapAndSelectedGames()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", Guid.NewGuid().ToString("N") + ".pr0file");

        string layoutPath = Path.Combine(Path.GetTempPath(), "joypro2-test-" + Guid.NewGuid().ToString("N") + ".layout");
        File.WriteAllText(layoutPath, "# test layout");
        try
        {
            await vm.RefreshDevicesCommand.ExecuteAsync(null);
            await vm.RefreshDbGamesCommand.ExecuteAsync(null);

            vm.LayoutSuggestions.Add(new LayoutSuggestionItem
            {
                Device = "Test Connected Device {00000000-0000-0000-0000-000000000001}",
                TemplateName = "TempLayout",
                LayoutPath = layoutPath,
                Score = 100,
                IsSelected = true
            });
            vm.ApplySelectedLayoutSuggestionsCommand.Execute(null);

            foreach (SelectableTagItem game in vm.LayoutGameOptions)
                game.IsSelected = string.Equals(game.Name, "DCS", StringComparison.OrdinalIgnoreCase);

            await vm.RenderLayoutCommand.ExecuteAsync(null);

            IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "layout", "render"));
            int mapIndex = call.ToList().FindIndex(v => string.Equals(v, "--layout-map", StringComparison.OrdinalIgnoreCase));
            Assert.True(mapIndex >= 0 && mapIndex + 1 < call.Count);
            Assert.Contains(layoutPath, call[mapIndex + 1], StringComparison.OrdinalIgnoreCase);
            Assert.Contains(call, v => string.Equals(v, "--game", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(call, v => string.Equals(v, "DCS", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try
            {
                if (File.Exists(layoutPath))
                    File.Delete(layoutPath);
            }
            catch
            {
                // ignored in tests
            }
        }
    }

    [Fact]
    public async Task SortBindsByColumn_MultiClick_UsesLatestAsPrimary_AndKeepsPreviousAsSecondary()
    {
        FakeEngineCliClient fake = new()
        {
            UseMultiSortScenario = true
        };
        MainWindowViewModel vm = CreateVm(fake);

        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);

        vm.SortBindsByColumnCommand.Execute("Input");
        vm.SortBindsByColumnCommand.Execute("Device");

        List<BindListItem> rows = vm.Binds.ToList();
        Assert.True(rows.Count >= 3);
        Assert.Equal("RelC", rows[0].RelationName);
        Assert.Equal("RelB", rows[1].RelationName);
        Assert.Equal("RelA", rows[2].RelationName);
    }

    [Fact]
    public async Task RecordModifierInput_EnablesKeyboardCapture_AndUpdatesModifierFields()
    {
        FakeEngineCliClient fake = new();
        FakeJoystickCaptureService capture = new()
        {
            NextCapture = new JoystickCaptureService.CaptureResult
            {
                Device = "Keyboard",
                Input = "M"
            }
        };
        MainWindowViewModel vm = CreateVm(fake, capture);

        await vm.RecordModifierInputCommand.ExecuteAsync(null);

        Assert.NotNull(capture.LastOptions);
        Assert.True(capture.LastOptions!.IncludeKeyboard);
        Assert.False(capture.LastOptions.AxisMode);
        Assert.Equal("Keyboard", vm.ModifierCreateDevice);
        Assert.Equal("M", vm.ModifierCreateInput);
        Assert.Equal("Shift button: Keyboard M", vm.ModifierCapturePreview);
    }

    [Fact]
    public async Task RecordModifierInput_Warns_WhenCapturedInputAlreadyExistsInProfile()
    {
        FakeEngineCliClient fake = new();
        FakeJoystickCaptureService capture = new()
        {
            NextCapture = new JoystickCaptureService.CaptureResult
            {
                Device = "Keyboard",
                Input = "LCtrl"
            }
        };
        MainWindowViewModel vm = CreateVm(fake, capture);
        string? warning = null;
        vm.WarningRequested += message => warning = message;

        await vm.RecordModifierInputCommand.ExecuteAsync(null);

        Assert.NotNull(warning);
        Assert.Contains("already used", warning!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Duplicate detected", vm.RecordStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.CanCreateModifier);
        Assert.Contains(fake.Calls, c => ContainsSequence(c, "binds", "list"));
        Assert.DoesNotContain(fake.Calls, c => ContainsSequence(c, "binds", "modifier", "list", "TestRelation"));
    }

    [Fact]
    public async Task RecordModifierInput_Warns_EvenWhenModifierNameDiffersFromCapturedInput()
    {
        FakeEngineCliClient fake = new()
        {
            UseMismatchedModifierNameScenario = true
        };
        FakeJoystickCaptureService capture = new()
        {
            NextCapture = new JoystickCaptureService.CaptureResult
            {
                Device = "Keyboard",
                Input = "LCtrl"
            }
        };
        MainWindowViewModel vm = CreateVm(fake, capture);
        string? warning = null;
        vm.WarningRequested += message => warning = message;

        await vm.RecordModifierInputCommand.ExecuteAsync(null);

        Assert.NotNull(warning);
        Assert.Contains("already used", warning!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Duplicate detected", vm.RecordStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.CanCreateModifier);
    }

    [Fact]
    public void MainWindowAxaml_ModifierCreateButton_DoesNotUseCreateAttachLabel()
    {
        string axaml = LoadMainWindowAxaml();

        Assert.DoesNotContain("Create + Attach", axaml);
        Assert.Contains("Content=\"{l:T 'Create Modifier'}\"", axaml);
    }

    [Fact]
    public void MainWindowAxaml_Settings_UsesDcsRefreshPlaneCheckboxes_WithAllNone()
    {
        string axaml = LoadMainWindowAxaml();

        Assert.Contains("ItemsSource=\"{Binding DcsRefreshPlaneOptions}\"", axaml);
        Assert.Contains("IsChecked=\"{Binding DcsRefreshSelectAllPlanes}\"", axaml);
        Assert.Contains("IsChecked=\"{Binding DcsRefreshSelectNonePlanes}\"", axaml);
        Assert.DoesNotContain("Text=\"{Binding DcsRefreshPlanesCsv}\"", axaml);
    }

    [Fact]
    public void MainWindowAxaml_Settings_UsesClassicDcsFfbMatrix_AndPerRowCommands()
    {
        string axaml = LoadMainWindowAxaml();

        Assert.Contains("DCS force feedback: swap and invert per device", axaml);
        Assert.Contains("Text=\"{Binding DcsFfbFilterText}\"", axaml);
        Assert.Contains("ItemsSource=\"{Binding VisibleDcsFfbDeviceSettings}\"", axaml);
        Assert.Contains("Command=\"{Binding SelectConnectedDcsFfbDevicesCommand}\"", axaml);
        Assert.Contains("Command=\"{Binding SelectProfileDcsFfbDevicesCommand}\"", axaml);
        Assert.Contains("Command=\"{Binding #RootWindow.DataContext.DcsFfbApplyDeviceCommand}\"", axaml);
        Assert.Contains("Command=\"{Binding #RootWindow.DataContext.DcsFfbClearDeviceCommand}\"", axaml);
    }

    [Fact]
    public void MainWindowAxaml_Layout_UsesPlaneCheckboxes_WithAllNone()
    {
        string axaml = LoadMainWindowAxaml();

        Assert.Contains("ItemsSource=\"{Binding LayoutPlaneOptions}\"", axaml);
        Assert.Contains("IsChecked=\"{Binding LayoutSelectAllPlanes}\"", axaml);
        Assert.Contains("IsChecked=\"{Binding LayoutSelectNonePlanes}\"", axaml);
        Assert.DoesNotContain("Text=\"{Binding LayoutPlaneCsv}\" />\r\n                            <TextBox", axaml);
    }

    [Fact]
    public void MainWindowAxaml_CheatSheets_AreInLayoutStage_NotExportStage()
    {
        // Cheat sheets (layout PNGs) change no game: they live on the Layout page, not on Export.
        string axaml = LoadMainWindowAxaml();

        int layoutStage = axaml.IndexOf("IsVisible=\"{Binding IsLayoutStage}\"", StringComparison.Ordinal);
        int exportStage = axaml.IndexOf("IsVisible=\"{Binding IsExportStage}\"", StringComparison.Ordinal);
        Assert.True(layoutStage >= 0);
        Assert.True(exportStage > layoutStage);

        string layoutSection = axaml.Substring(layoutStage, exportStage - layoutStage);
        string exportSection = axaml.Substring(exportStage);

        Assert.Contains("Command=\"{Binding RenderLayoutCommand}\"", layoutSection, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsCheatSheetsSection}\"", layoutSection, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenCheatSheetFolderCommand}\"", layoutSection, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding CheatSheetPreviews}\"", layoutSection, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding RenderLayoutCommand}\"", exportSection, StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutExportPath", exportSection, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowAxaml_LayoutStage_PutsTheDeviceCanvasAtTheCentre()
    {
        string axaml = LoadMainWindowAxaml();

        Assert.Contains("Command=\"{Binding OpenSelectedLayoutInEditorCommand}\"", axaml);
        Assert.Contains("Command=\"{Binding LoadLayoutEditorSourceCommand}\"", axaml);
        // Save keeps the reference's markers or writes the layout file, whichever picture is shown.
        Assert.Contains("Command=\"{Binding SaveWorkspaceCommand}\"", axaml);
        // The canvas contract (docs/ui/layout-redesign.md): picture, markers, selection, mode, move and click events.
        Assert.Contains("x:Name=\"DeviceReferenceHost\"", axaml, StringComparison.Ordinal);
        Assert.Contains("<controls:DeviceMarkerCanvas x:Name=\"LayoutCanvas\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Markers=\"{Binding WorkspaceMarkers}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Schematic=\"{Binding WorkspaceSchematic}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("SelectedMarker=\"{Binding WorkspaceSelectedMarker}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ReferenceLegendItems}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenReferenceAssistantCommand}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Mode=\"{Binding LayoutCanvasMode}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("MarkerMovedCommand=\"{Binding LayoutCanvasMarkerMovedCommand}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("CanvasClickedCommand=\"{Binding LayoutCanvasClickedCommand}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"view\"", axaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"edit\"", axaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"bind\"", axaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding LayoutLegendItems}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("<controls:DeviceMarkerCanvas x:Name=\"VisualAssignCanvas\"", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowAxaml_HasOpenLastButton_AndStartupLoadLastSetting()
    {
        string axaml = LoadMainWindowAxaml();

        Assert.Contains("Command=\"{Binding OpenLastProfileCommand}\"", axaml);
        Assert.Contains("IsEnabled=\"{Binding CanOpenLastProfile}\"", axaml);
        Assert.Contains("IsChecked=\"{Binding LoadLastProfileOnStartup}\"", axaml);
    }

    [Fact]
    public void JoyProGuiCsproj_ContainsPreBuildProcessStopForVsLockScenario()
    {
        string csprojPath = ResolveRepositoryFilePath("gui", "JoyPro.Gui", "JoyPro.Gui.csproj");
        string text = File.ReadAllText(csprojPath);

        Assert.Contains("StopRunningJoyProGuiBeforeBuild", text);
        Assert.Contains("BeforeTargets=\"CoreCompile;CopyFilesToOutputDirectory;Build\"", text);
        Assert.Contains("Get-Process -Name 'JoyPro.Gui'", text);
        Assert.Contains("Stop-Process -Force", text);
    }

    [Fact]
    public void MainWindowAxaml_UsesLiveValidationBindings_ForRelationAndModifierCreate()
    {
        string axaml = LoadMainWindowAxaml();

        // A new relation is created from its name and the ticked search rows (Relations page, docs/relation-builder.md).
        Assert.Contains("Command=\"{Binding AddCheckedBuilderResultsCommand}\"", axaml);
        Assert.Contains("Text=\"{Binding RelationNameInput}\"", axaml);
        Assert.Contains("Text=\"{Binding RelationCreateValidationMessage}\"", axaml);
        Assert.Contains("Text=\"{Binding ModifierCreateValidationMessage}\"", axaml);
    }

    [Fact]
    public async Task CreateModifier_Warns_WhenDeviceInputAlreadyUsedOnTargetRelation()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);

        vm.ModifierCreateTargetRelation = "TestRelation";
        vm.ModifierCreateName = "NewModifier";
        vm.ModifierCreateDevice = "Test Device";
        vm.ModifierCreateInput = "JOY_BTN1";

        await vm.CreateModifierOnSelectedBindCommand.ExecuteAsync(null);

        Assert.Contains("already used", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fake.Calls, c => ContainsSequence(c, "binds", "list"));
        Assert.DoesNotContain(fake.Calls, c => ContainsSequence(c, "binds", "modifier", "add"));
    }

    [Fact]
    public async Task CreateModifier_WithoutTarget_CreatesTemporaryModifierInUi()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        vm.ModifierCreateName = "TempMod";
        vm.ModifierCreateDevice = "Keyboard";
        vm.ModifierCreateInput = "LShift";

        await vm.CreateModifierOnSelectedBindCommand.ExecuteAsync(null);

        ModifierListItem? temp = vm.ModifierItems.FirstOrDefault(v => string.Equals(v.Name, "TempMod", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(temp);
        Assert.True(temp!.IsPending);
        Assert.Equal("Keyboard", temp.PrimaryDevice);
        Assert.Equal("LShift", temp.PrimaryInput);
        Assert.Contains("Temporary modifier", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fake.Calls, c => ContainsSequence(c, "binds", "modifier", "add"));
    }

    [Fact]
    public async Task CanCreateModifier_RequiresUniqueNameDeviceAndInput()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        await vm.RefreshModifiersCommand.ExecuteAsync(null);

        vm.ModifierCreateName = string.Empty;
        vm.ModifierCreateDevice = "Keyboard";
        vm.ModifierCreateInput = "LShift";
        Assert.False(vm.CanCreateModifier);

        vm.ModifierCreateName = "LCtrl"; // already exists
        Assert.False(vm.CanCreateModifier);

        vm.ModifierCreateName = "BrandNewModifier";
        vm.ModifierCreateDevice = "<none>";
        Assert.False(vm.CanCreateModifier);

        vm.ModifierCreateDevice = "Keyboard";
        vm.ModifierCreateInput = "LeftControl"; // duplicate input of existing LCtrl
        Assert.False(vm.CanCreateModifier);

        vm.ModifierCreateInput = "JOY_BTN99"; // unique in fake dataset
        Assert.True(vm.CanCreateModifier);
    }

    [Fact]
    public async Task ModifierCreateValidation_UpdatesImmediately_ForDuplicateNameAndInput()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        await vm.RefreshModifiersCommand.ExecuteAsync(null);

        vm.ModifierCreateName = "LCtrl";
        Assert.False(vm.CanCreateModifier);
        Assert.True(vm.HasModifierCreateValidationMessage);
        Assert.Contains("already exists", vm.ModifierCreateValidationMessage, StringComparison.OrdinalIgnoreCase);

        vm.ModifierCreateName = "BrandNewModifier";
        vm.ModifierCreateDevice = "Keyboard";
        vm.ModifierCreateInput = "LeftControl";
        Assert.False(vm.CanCreateModifier);
        Assert.True(vm.HasModifierCreateValidationMessage);
        Assert.Contains("already used", vm.ModifierCreateValidationMessage, StringComparison.OrdinalIgnoreCase);

        vm.ModifierCreateInput = "JOY_BTN99";
        Assert.True(vm.CanCreateModifier);
        Assert.False(vm.HasModifierCreateValidationMessage);
        Assert.Equal(string.Empty, vm.ModifierCreateValidationMessage);
    }

    [Fact]
    public async Task RelationCreateValidation_UpdatesImmediately_WhenNameAlreadyExists()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        await vm.RefreshRelationsCommand.ExecuteAsync(null);

        vm.RelationNameInput = "TestRelation";
        Assert.False(vm.CanCreateRelation);
        Assert.True(vm.HasRelationCreateValidationMessage);
        Assert.Contains("already exists", vm.RelationCreateValidationMessage, StringComparison.OrdinalIgnoreCase);

        vm.RelationNameInput = "BrandNewRelation";
        Assert.True(vm.CanCreateRelation);
        Assert.False(vm.HasRelationCreateValidationMessage);
        Assert.Equal(string.Empty, vm.RelationCreateValidationMessage);
    }

    [Fact]
    public async Task AddNodeToSelectedRelation_RequiresDbSearchSelection()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        vm.SelectedRelation = vm.Relations.FirstOrDefault();
        await Task.Delay(40);
        fake.Calls.Clear();

        await vm.AddNodeToSelectedRelationCommand.ExecuteAsync(null);

        Assert.Contains("search and select", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fake.Calls, c => ContainsSequence(c, "relations", "item", "add"));
    }

    [Fact]
    public async Task RefreshDbGames_PopulatesRelationNodeGameDropdown()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        await vm.RefreshDbGamesCommand.ExecuteAsync(null);

        Assert.Contains("DCS", vm.RelationNodeGameOptions);
        Assert.Contains("IL2Game", vm.RelationNodeGameOptions);
        Assert.Contains("StarCitizen", vm.RelationNodeGameOptions);
    }

    [Fact]
    public async Task SearchRelationNodes_FindsDcsRows_ByTitleDescriptionAndId()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", Guid.NewGuid().ToString("N"));
        string engineDir = Path.Combine(root, "engine");
        string dbDir = Path.Combine(engineDir, "DB", "DCS");
        Directory.CreateDirectory(dbDir);
        string engineExe = Path.Combine(engineDir, "engine.exe");
        File.WriteAllBytes(engineExe, [0x4D, 0x5A]); // Minimal MZ header placeholder.

        string html = "<table><tr><td>Flight Control</td><td>Wheel Brake</td><td>Toggle wheel brake</td><td>abc_hash</td></tr></table>";
        File.WriteAllText(Path.Combine(dbDir, "A-10C_2.html"), html);

        try
        {
            vm.EnginePath = engineExe;
            vm.RelationNodeGameInput = "DCS";
            vm.RelationNodeSearchText = "wheel";

            await vm.SearchRelationNodesCommand.ExecuteAsync(null);

            DbNodeSearchResultItem? hit = vm.RelationNodeSearchResults.FirstOrDefault(v =>
                string.Equals(v.NodeId, "abc_hash", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(hit);
            Assert.Contains("Wheel Brake", hit!.Title, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Toggle wheel brake", hit.Description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("match", vm.RelationNodeSearchStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                // Ignore test cleanup race conditions on locked temp files.
            }
        }
    }

    // The game lists of the view model's data folder (<data>\GameData, where a real engine copies them) win over the
    // engine folder's; CreateVm gives every view model of these tests an empty data folder of its own, so the lists a
    // real engine copied into the run's shared JOYPRO_DATA_DIR never reach them.
    [Fact]
    public async Task SearchRelationNodes_ReadsTheViewModelsDataFolder_BeforeTheEngineFolder()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", Guid.NewGuid().ToString("N"));
        string engineDir = Path.Combine(root, "engine");
        Directory.CreateDirectory(Path.Combine(engineDir, "DB", "DCS"));
        string engineExe = Path.Combine(engineDir, "engine.exe");
        File.WriteAllBytes(engineExe, [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(engineDir, "DB", "DCS", "A-10C_2.html"),
            "<table><tr><td>Flight Control</td><td>Wheel Brake</td><td>Toggle wheel brake</td><td>engine_hash</td></tr></table>");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(Path.Combine(data, "GameData", "DB", "DCS"));
        File.WriteAllText(Path.Combine(data, "GameData", "DB", "DCS", "F-16C_50.html"),
            "<table><tr><td>Flight Control</td><td>Wheel Brake</td><td>Toggle wheel brake</td><td>data_hash</td></tr></table>");

        try
        {
            Assert.False(string.IsNullOrWhiteSpace(vm.GameDataRootOverride));
            Assert.False(Directory.Exists(Path.Combine(vm.GameDataRootOverride, "GameData")));
            vm.EnginePath = engineExe;
            vm.GameDataRootOverride = data;
            vm.RelationNodeGameInput = "DCS";
            vm.RelationNodeSearchText = "wheel";

            await vm.SearchRelationNodesCommand.ExecuteAsync(null);

            DbNodeSearchResultItem hit = Assert.Single(vm.RelationNodeSearchResults);
            Assert.Equal("data_hash", hit.NodeId);
            Assert.Equal("F-16C_50", hit.Plane);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                // Ignore test cleanup race conditions on locked temp files.
            }
        }
    }

    [Fact]
    public async Task RelationNodePlanes_ListMsfsAircraft_FromDbAircraftList()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", Guid.NewGuid().ToString("N"));
        string engineDir = Path.Combine(root, "engine");
        string dbDir = Path.Combine(engineDir, "DB", "MSFS2024");
        Directory.CreateDirectory(dbDir);
        string engineExe = Path.Combine(engineDir, "engine.exe");
        File.WriteAllBytes(engineExe, [0x4D, 0x5A]); // Minimal MZ header placeholder.
        File.WriteAllText(Path.Combine(dbDir, "actions.tsv"), "# id\ttitle\taxis|button\tcategory\r\nKEY_GEAR_TOGGLE\tLanding gear toggle\tbutton\tAIRCRAFT\r\n");
        File.WriteAllText(Path.Combine(dbDir, "aircraft.tsv"),
            "# key\ttitle\tcategory\tsims\ticao type\tmanufacturer\tpackages\tvariants\ttitle source\r\n" +
            "asobo-aircraft-c152\tCessna 152\tairplane\t2020,2024\tC152\tCessna\tasobo-aircraft-c152\tCessna 152 Asobo\tmanifest\r\n" +
            "microsoft-aircraft-c185f-skywagon\tC185F Skywagon\t\t2024\t\t\tfs24-microsoft-aircraft-c185f-skywagon\t\tpackage name\r\n");

        try
        {
            vm.EnginePath = engineExe;
            vm.RelationNodeGameInput = "MSFS2024";
            vm.RelationNodeSearchText = "gear";
            await vm.SearchRelationNodesCommand.ExecuteAsync(null);

            DbNodeSearchResultItem hit = Assert.Single(vm.RelationNodeSearchResults);
            vm.SelectedRelationNodeSearchResult = hit;

            // The game name (all aircraft) and every aircraft of the list, with its title.
            Assert.Contains(vm.RelationNodePlaneOptions, o => o.Value == "MSFS2024");
            SelectableTagItem c152 = Assert.Single(vm.RelationNodePlaneOptions, o => o.Value == "asobo-aircraft-c152");
            Assert.Contains("Cessna 152", c152.Name);
            Assert.Contains(vm.RelationNodePlaneOptions, o => o.Value == "microsoft-aircraft-c185f-skywagon");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                // Ignore test cleanup race conditions on locked temp files.
            }
        }
    }

    [Fact]
    public async Task AddNodeToSelectedRelation_WithoutSelectedRelation_CreatesFromDraft()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        vm.RelationNameInput = "DraftRelation";
        vm.NewRelationIsAxis = false;
        vm.RelationNodeGameInput = "IL2Game";
        vm.SelectedRelationNodeSearchResult = new DbNodeSearchResultItem
        {
            Game = "IL2Game",
            Plane = "Default",
            NodeId = "mc_pitch",
            Title = "Pitch",
            Description = "Pitch control"
        };

        await vm.AddNodeToSelectedRelationCommand.ExecuteAsync(null);

        Assert.Contains(fake.Calls, c => ContainsSequence(c, "relations", "add", "DraftRelation"));
    }

    [Fact]
    public void SortRelationNodeSearchResults_SortsByNodeIdAscendingThenDescending()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        vm.RelationNodeSearchResults.Add(new DbNodeSearchResultItem { NodeId = "zulu", Title = "Z", Description = "z", Plane = "Default", Game = "DCS" });
        vm.RelationNodeSearchResults.Add(new DbNodeSearchResultItem { NodeId = "alpha", Title = "A", Description = "a", Plane = "Default", Game = "DCS" });
        vm.RelationNodeSearchResults.Add(new DbNodeSearchResultItem { NodeId = "mike", Title = "M", Description = "m", Plane = "Default", Game = "DCS" });

        vm.SortRelationNodeSearchResults("NodeId", ListSortDirection.Ascending);
        Assert.Equal(["alpha", "mike", "zulu"], vm.RelationNodeSearchResults.Select(v => v.NodeId).ToArray());

        vm.SortRelationNodeSearchResults("NodeId", ListSortDirection.Descending);
        Assert.Equal(["zulu", "mike", "alpha"], vm.RelationNodeSearchResults.Select(v => v.NodeId).ToArray());
    }

    [Fact]
    public async Task SelectingRelationNodeSearchResult_DoesNotClearSearchResults()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        DbNodeSearchResultItem dcs = new()
        {
            Game = "DCS",
            Plane = "A-10C_2",
            NodeId = "dcs_pitch",
            Title = "Pitch",
            Description = "DCS Pitch"
        };
        DbNodeSearchResultItem il2 = new()
        {
            Game = "IL2Game",
            Plane = "Default",
            NodeId = "il2_pitch",
            Title = "Pitch",
            Description = "IL2 Pitch"
        };

        vm.RelationNodeSearchResults.Add(dcs);
        vm.RelationNodeSearchResults.Add(il2);
        vm.RelationNodeGameInput = "DCS";

        vm.SelectedRelationNodeSearchResult = il2;
        await Task.Delay(40);

        Assert.Equal(2, vm.RelationNodeSearchResults.Count);
        Assert.Equal("IL2Game", vm.RelationNodeGameInput);
    }

    [Fact]
    public async Task CanRenameModifier_RequiresUniqueDifferentName()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        await vm.RefreshModifiersCommand.ExecuteAsync(null);

        vm.ModifierRenameOld = "LCtrl";
        vm.ModifierRenameNew = string.Empty;
        Assert.False(vm.CanRenameModifier);

        vm.ModifierRenameNew = "LCtrl"; // same as old
        Assert.False(vm.CanRenameModifier);

        vm.ModifierRenameNew = "ExistingMod"; // already exists
        Assert.False(vm.CanRenameModifier);

        vm.ModifierRenameNew = "LCtrlNew";
        Assert.True(vm.CanRenameModifier);
    }

    [Fact]
    public async Task RefreshModifiers_PopulatesPrimaryDeviceAndInputFromReformers()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        await vm.RefreshModifiersCommand.ExecuteAsync(null);

        ModifierListItem? lctrl = vm.ModifierItems.FirstOrDefault(v =>
            string.Equals(v.Name, "LCtrl", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(lctrl);
        Assert.Equal("Keyboard", lctrl!.PrimaryDevice);
        Assert.Equal("LeftControl", lctrl.PrimaryInput);
    }

    [Fact]
    public async Task RefreshModifiers_PopulatesPrimaryDeviceAndInput_WhenDelimiterIsCorrupted()
    {
        FakeEngineCliClient fake = new()
        {
            UseCorruptedModifierDelimiterScenario = true
        };
        MainWindowViewModel vm = CreateVm(fake);

        await vm.RefreshModifiersCommand.ExecuteAsync(null);

        ModifierListItem? lctrl = vm.ModifierItems.FirstOrDefault(v =>
            string.Equals(v.Name, "LCtrl", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(lctrl);
        Assert.Equal("Keyboard", lctrl!.PrimaryDevice);
        Assert.Equal("LeftControl", lctrl.PrimaryInput);
    }

    [Fact]
    public async Task RecordModifierInput_Warns_WhenCapturedInputAlreadyExists_WithCorruptedDelimiter()
    {
        FakeEngineCliClient fake = new()
        {
            UseCorruptedModifierDelimiterScenario = true
        };
        FakeJoystickCaptureService capture = new()
        {
            NextCapture = new JoystickCaptureService.CaptureResult
            {
                Device = "Keyboard",
                Input = "LCtrl"
            }
        };
        MainWindowViewModel vm = CreateVm(fake, capture);
        string? warning = null;
        vm.WarningRequested += message => warning = message;

        await vm.RecordModifierInputCommand.ExecuteAsync(null);

        Assert.NotNull(warning);
        Assert.Contains("already used", warning!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Duplicate detected", vm.RecordStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.CanCreateModifier);
    }

    [Fact]
    public void DetectDirectInputButtonEdge_SupportsButtonsBeyond32()
    {
        byte[] previous = new byte[128];
        byte[] current = new byte[128];
        current[63] = 0x80; // Button 64 pressed.

        string? edge = JoystickCaptureService.DetectDirectInputButtonEdge(previous, current);

        Assert.Equal("JOY_BTN64", edge);
    }

    [Fact]
    public void DetectDirectInputButtonEdge_TreatsAnyNonZeroAsPressed()
    {
        byte[] previous = new byte[128];
        byte[] current = new byte[128];
        current[95] = 0x01; // Button 96 pressed with low-bit style drivers.

        string? edge = JoystickCaptureService.DetectDirectInputButtonEdge(previous, current);

        Assert.Equal("JOY_BTN96", edge);
    }

    [Fact]
    public void DirectInputDataFormatStructs_UseNativeAlignmentSizes()
    {
        Type serviceType = typeof(JoystickCaptureService);
        Type? objectFormatType = serviceType.GetNestedType("DioObjectDataFormat", BindingFlags.NonPublic);
        Type? dataFormatType = serviceType.GetNestedType("DioDataFormat", BindingFlags.NonPublic);
        Type? joyStateType = serviceType.GetNestedType("DirectInputJoyState", BindingFlags.NonPublic);

        Assert.NotNull(objectFormatType);
        Assert.NotNull(dataFormatType);
        Assert.NotNull(joyStateType);

        int expectedObjectSize = IntPtr.Size == 8 ? 24 : 16;
        int expectedDataSize = IntPtr.Size == 8 ? 32 : 24;
        Assert.Equal(expectedObjectSize, Marshal.SizeOf(objectFormatType!));
        Assert.Equal(expectedDataSize, Marshal.SizeOf(dataFormatType!));
        Assert.Equal(272, Marshal.SizeOf(joyStateType!));
    }

    [Fact]
    public void LayoutEditorScaledCanvasSize_TracksZoomFactor()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.LayoutEditorCanvasWidth = 2095;
        vm.LayoutEditorCanvasHeight = 2000;

        vm.LayoutEditorZoom = 0.42;
        Assert.Equal(879.9, vm.LayoutEditorScaledCanvasWidth, 3);
        Assert.Equal(840.0, vm.LayoutEditorScaledCanvasHeight, 3);

        vm.LayoutEditorZoom = 1.0;
        Assert.Equal(2095.0, vm.LayoutEditorScaledCanvasWidth, 3);
        Assert.Equal(2000.0, vm.LayoutEditorScaledCanvasHeight, 3);
    }

    [Fact]
    public async Task LayoutEditorInputOptions_FilterByType_UsesCatalogAndDefaults()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);

        await vm.RefreshDeviceInputCatalogCommand.ExecuteAsync(null);

        vm.LayoutEditorNewType = "button";
        Assert.Contains("JOY_BTN1", vm.LayoutEditorInputOptions);
        Assert.DoesNotContain("JOY_X", vm.LayoutEditorInputOptions);

        vm.LayoutEditorNewType = "axis";
        Assert.Contains("JOY_X", vm.LayoutEditorInputOptions);
        Assert.DoesNotContain("JOY_BTN1", vm.LayoutEditorInputOptions);

        vm.LayoutEditorNewType = "pov";
        Assert.Contains("JOY_BTN_POV1_U", vm.LayoutEditorInputOptions);
    }

    [Fact]
    public async Task OpenLayoutBackdropWebSearch_NoDeviceContext_DoesNotAttemptLaunch()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.LayoutDevicesCsv = string.Empty;

        await vm.OpenLayoutBackdropWebSearchCommand.ExecuteAsync(null);

        Assert.Contains("Select a device", vm.LayoutEditorStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fake.Calls, c => ContainsSequence(c, "layout", "identify"));
    }

    [Fact]
    public async Task DbUpdates_ShowUpdatedGamesAndCanBeAccepted()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        string? warning = null;
        vm.WarningRequested += message => warning = message;

        await vm.CheckDbUpdatesCommand.ExecuteAsync(null);

        Assert.True(vm.HasDbUpdates);
        string message = Assert.Single(vm.DbUpdateMessages);
        Assert.Contains("DCS World was updated", message, StringComparison.Ordinal);
        Assert.Contains("F-4E", message, StringComparison.Ordinal);
        Assert.NotNull(warning);

        await vm.AcceptDbUpdatesCommand.ExecuteAsync(null);
        Assert.False(vm.HasDbUpdates);
        Assert.Contains(fake.Calls, c => ContainsSequence(c, "db", "check-updates", "--accept-all"));
    }

    [Fact]
    public async Task DbUpdates_PerGameRefreshAndKeep_BuildEngineArguments()
    {
        FakeEngineCliClient fake = new() { UseDbUpdatesPerGameScenario = true };
        MainWindowViewModel vm = CreateVm(fake);
        vm.DcsSavedGamesPath = "C:/Users/x/Saved Games/DCS";
        vm.DcsInstallPath = "G:/DCS World";
        vm.DcsRefreshLaunch = true;
        vm.DcsRefreshHeadlessLua = false;
        vm.StarCitizenRefreshBranch = "PTU";

        await vm.CheckDbUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(["DCS", "StarCitizen", "WarThunder", "NoRefreshGame"], vm.DbUpdateItems.Select(i => i.Game).ToArray());
        Assert.Equal("Star Citizen", vm.DbUpdateItems[1].DisplayName);
        Assert.True(vm.DbUpdateItems.Take(3).All(i => i.CanRefresh));
        Assert.False(vm.DbUpdateItems[3].CanRefresh);
        Assert.Equal(4, vm.DbUpdateMessages.Count);

        // DCS: refreshed from its files (headless), never by starting the game, whatever the DCS refresh settings say.
        fake.Calls.Clear();
        await vm.RefreshDbUpdateCommand.ExecuteAsync(vm.DbUpdateItems[0]);
        IReadOnlyList<string> dcs = Assert.Single(fake.Calls, c => ContainsSequence(c, "dcs", "refresh", "id"));
        Assert.True(ContainsSequence(dcs, "--headless-lua", "true"));
        Assert.True(ContainsSequence(dcs, "--launch", "false"));
        Assert.True(ContainsSequence(dcs, "--path", "C:/Users/x/Saved Games/DCS"));
        Assert.True(ContainsSequence(dcs, "--install-path", "G:/DCS World"));
        Assert.DoesNotContain("--dry-run", dcs);
        // Checked again afterwards (the refresh records the build).
        Assert.Contains(fake.Calls, c => ContainsSequence(c, "db", "check-updates") && !c.Contains("--accept"));

        fake.Calls.Clear();
        await vm.RefreshDbUpdateCommand.ExecuteAsync(vm.DbUpdateItems.Single(i => i.Game == "StarCitizen"));
        IReadOnlyList<string> sc = Assert.Single(fake.Calls, c => ContainsSequence(c, "db", "refresh-starcitizen"));
        Assert.True(ContainsSequence(sc, "--branch", "PTU"));

        fake.Calls.Clear();
        await vm.RefreshDbUpdateCommand.ExecuteAsync(vm.DbUpdateItems.Single(i => i.Game == "WarThunder"));
        Assert.Single(fake.Calls, c => ContainsSequence(c, "warthunder", "refresh-db"));

        // No refresh for this game: nothing runs.
        fake.Calls.Clear();
        await vm.RefreshDbUpdateCommand.ExecuteAsync(vm.DbUpdateItems.Single(i => i.Game == "NoRefreshGame"));
        Assert.Empty(fake.Calls);

        fake.Calls.Clear();
        await vm.AcceptDbUpdateCommand.ExecuteAsync(vm.DbUpdateItems.Single(i => i.Game == "NoRefreshGame"));
        IReadOnlyList<string> keep = Assert.Single(fake.Calls);
        Assert.True(ContainsSequence(keep, "db", "check-updates", "--accept", "NoRefreshGame"));
        Assert.DoesNotContain(vm.DbUpdateItems, i => i.Game == "NoRefreshGame");
    }

    [Fact]
    public async Task DeviceDetails_CallsGamesDevices()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.ShowDeviceDetailsCommand.ExecuteAsync(null);
        IReadOnlyList<string> call = Assert.Single(fake.Calls);
        Assert.True(ContainsSequence(call, "games", "devices"));
        Assert.DoesNotContain("--profile", call);
    }

    [Fact]
    public async Task StarCitizenDbRefresh_PassesTheBranch()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.StarCitizenRefreshBranch = " EPTU ";
        await vm.RefreshStarCitizenDbCommand.ExecuteAsync(null);
        IReadOnlyList<string> refresh = Assert.Single(fake.Calls, c => ContainsSequence(c, "db", "refresh-starcitizen"));
        Assert.True(ContainsSequence(refresh, "--branch", "EPTU"));

        vm.StarCitizenRefreshBranch = string.Empty;
        fake.Calls.Clear();
        await vm.RefreshStarCitizenDbCommand.ExecuteAsync(null);
        Assert.DoesNotContain("--branch", Assert.Single(fake.Calls));
    }

    [Fact]
    public async Task VisualAssign_ShowsTheDeviceReferenceAndBindsThePickedRelation()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        vm.VisualAssignDevice = "VKB Stick {00000000-0000-0000-0000-0000000000B1}";

        await vm.LoadVisualAssignCommand.ExecuteAsync(null);

        // No layout file of the stick: its reference picture (catalog model or drawn from its inputs) with a legend.
        Assert.True(vm.LayoutShowsReference);
        Assert.True(vm.HasReferenceModel);
        ReferenceLegendRow trigger = Assert.Single(vm.ReferenceLegendItems, r => r.Input == "JOY_BTN1");
        vm.DeviceReference.SelectedLegendRow = trigger;
        Assert.True(vm.HasWorkspaceInput);
        Assert.Contains("TestRelation", vm.LayoutBindCandidates);

        fake.Calls.Clear();
        await vm.BindSelectedLayoutMarkerCommand.ExecuteAsync("TestRelation");

        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => ContainsSequence(c, "binds", "set"));
        Assert.True(ContainsSequence(set, "TestRelation", "--device", "VKB Stick {00000000-0000-0000-0000-0000000000B1}", "--input", "JOY_BTN1"));
        Assert.DoesNotContain(fake.Calls, c => ContainsSequence(c, "binds", "clear"));
    }
    [Fact]
    public async Task UnbindSelectedInput_FreesTheInputInEveryRelation()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.SelectedBind = new BindListItem { RelationName = "Gear", HasBind = true, Device = "Test Device", Input = "JOY_BTN3" };
        fake.Calls.Clear();

        await vm.UnbindSelectedInputCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "binds", "unbind"));
        Assert.True(ContainsSequence(call, "--device", "Test Device"));
        Assert.True(ContainsSequence(call, "--input", "JOY_BTN3"));
        Assert.Contains("--all", call);
    }

    [Fact]
    public async Task UseLayoutPicture_SendsThePictureForTheSelectedDevice()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        LayoutSuggestionItem suggestion = new() { Device = "Test Stick {00000000-0000-0000-0000-000000000042}" };
        vm.LayoutSuggestions.Add(suggestion);
        vm.SelectedLayoutSuggestion = suggestion;
        vm.LayoutPictureSource = " https://example.com/stick.png ";

        await vm.UseLayoutPictureCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = Assert.Single(fake.Calls, c => ContainsSequence(c, "layout", "suggest"));
        Assert.True(ContainsSequence(call, "--device", "Test Stick {00000000-0000-0000-0000-000000000042}"));
        Assert.True(ContainsSequence(call, "--backdrop", "https://example.com/stick.png"));
    }

    [Fact]
    public void MainWindowAxaml_LayoutWorkspace_FillsTheWindowWithThreeColumns()
    {
        // Devices | picture canvas (with toolbar and status bar) | selected input + legend; the page fills the window
        // instead of scrolling, the canvas zooms and pans itself (no scroll viewer around the picture).
        string axaml = LoadMainWindowAxaml();
        int layoutStage = axaml.IndexOf("IsVisible=\"{Binding IsLayoutStage}\"", StringComparison.Ordinal);
        int exportStage = axaml.IndexOf("IsVisible=\"{Binding IsExportStage}\"", StringComparison.Ordinal);
        string layoutSection = axaml.Substring(layoutStage, exportStage - layoutStage);

        Assert.Contains("ColumnDefinitions=\"270,*,340\"", layoutSection, StringComparison.Ordinal);
        Assert.Contains("Classes=\"toolbar\"", layoutSection, StringComparison.Ordinal);
        Assert.Contains("Classes=\"statusbar\"", layoutSection, StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutEditorViewportScroll", axaml, StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutEditorHorizontalScrollBar", axaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding HasNoLayoutWorkspacePicture}\"", layoutSection, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowAxaml_LayoutEditor_EditsTheSelectedMarkerInTheSidePanel()
    {
        string axaml = LoadMainWindowAxaml();

        Assert.Contains("ItemsSource=\"{Binding LayoutEditorInputOptions}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding LayoutEditorNewType}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding LayoutEditorModifierOptions}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ApplySelectedLayoutEditorMarkerDraftCommand}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RemoveSelectedLayoutEditorMarkerCommand}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding UndoLayoutEditCommand}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RedoLayoutEditCommand}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding BindSelectedLayoutMarkerCommand}\"", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowAxaml_ExportLayout_UsesFolderBrowseAndCheckboxSelectors()
    {
        string axaml = LoadMainWindowAxaml();

        Assert.Contains("Click=\"BrowseLayoutExportFolderClicked\"", axaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding LayoutDeviceOptions}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding LayoutGameOptions}\"", axaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Devices CSV", axaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Games CSV", axaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Layout Map\"", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowCodeBehind_LayoutSelectionDoesNotRecenterViewport()
    {
        string codeBehind = LoadMainWindowCodeBehind();

        Assert.DoesNotContain("CenterLayoutViewportOnMarker(vm.SelectedLayoutEditorMarker)", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CenterLayoutViewportOnTable(vm.SelectedLayoutEditorTable)", codeBehind, StringComparison.Ordinal);
        // The overlay (tables, lines) maps the pointer through the canvas' own zoom and pan.
        Assert.Contains("LayoutCanvas.ToCanvasPoint(", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowAxaml_LayoutConnectorHasWideHitZoneAndPointDragHandles()
    {
        string axaml = LoadMainWindowAxaml();

        Assert.Contains("Stroke=\"{DynamicResource JP.HitTestBrush}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("StrokeThickness=\"{Binding HitStrokeThickness}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("PointerPressed=\"LayoutEditorConnectorPointPointerPressed\"", axaml, StringComparison.Ordinal);
        Assert.Contains("PointerMoved=\"LayoutEditorConnectorPointPointerMoved\"", axaml, StringComparison.Ordinal);
        Assert.Contains("PointerReleased=\"LayoutEditorConnectorPointPointerReleased\"", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void LayoutEditorConnectorItem_PointMutations_UpdateHandlesAndRaisePointsChanged()
    {
        LayoutEditorConnectorItem connector = new();
        int pointsChanged = 0;
        int handlesChanged = 0;
        connector.PropertyChanged += (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(LayoutEditorConnectorItem.Points), StringComparison.Ordinal))
                pointsChanged++;
            if (string.Equals(e.PropertyName, nameof(LayoutEditorConnectorItem.PointHandles), StringComparison.Ordinal))
                handlesChanged++;
        };

        connector.SetPoints([new Point(10, 20), new Point(40, 60)]);
        Assert.Equal(2, connector.Points.Count);
        Assert.Equal(2, connector.PointHandles.Count);
        Assert.True(pointsChanged >= 1);
        Assert.True(handlesChanged >= 1);

        bool moved = connector.SetPointAt(1, new Point(120, 140));
        Assert.True(moved);
        Assert.Equal(120, connector.Points[1].X, 3);
        Assert.Equal(140, connector.Points[1].Y, 3);
        Assert.Equal(120, connector.PointHandles[1].X, 3);
        Assert.Equal(140, connector.PointHandles[1].Y, 3);
        Assert.True(pointsChanged >= 2);
    }

    [Fact]
    public void MainWindowAxaml_CommandBindingsReferenceExistingViewModelCommands()
    {
        string axaml = LoadMainWindowAxaml();
        MatchCollection matches = Regex.Matches(
            axaml,
            "Command=\"\\{Binding\\s+(?:#RootWindow\\.DataContext\\.)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\\}\"",
            RegexOptions.CultureInvariant);

        List<string> names = matches
            .Cast<Match>()
            .Select(m => m.Groups["name"].Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(names);

        // Commands of a row (open the row, tick all aircraft) live on the item models used in the templates.
        // ... and on the device reference's marker assistant (its panel on the Layout page).
        List<Type> types = [typeof(MainWindowViewModel), typeof(MarkerAssistantViewModel), typeof(DeviceReferenceViewModel)];
        types.AddRange(typeof(MainWindowViewModel).Assembly.GetTypes().Where(t => t.Namespace == "JoyPro.Gui.Models"));
        List<string> missing = [];
        foreach (string name in names)
        {
            bool found = types.Any(t =>
            {
                PropertyInfo? prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                return prop != null && typeof(ICommand).IsAssignableFrom(prop.PropertyType);
            });
            if (!found)
                missing.Add(name);
        }

        Assert.True(
            missing.Count == 0,
            "Missing/invalid command property bindings: " + string.Join(", ", missing));
    }

    [Fact]
    public void MainWindowAxaml_ClickHandlersReferenceExistingCodeBehindMethods()
    {
        string axaml = LoadMainWindowAxaml();
        MatchCollection matches = Regex.Matches(
            axaml,
            "Click=\"(?<name>[A-Za-z_][A-Za-z0-9_]*)\"",
            RegexOptions.CultureInvariant);

        List<string> names = matches
            .Cast<Match>()
            .Select(m => m.Groups["name"].Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(names);

        Type windowType = typeof(MainWindow);
        List<string> missing = [];
        foreach (string name in names)
        {
            MethodInfo? method = windowType.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                missing.Add(name);
        }

        Assert.True(
            missing.Count == 0,
            "Missing click handler method(s): " + string.Join(", ", missing));
    }

    [Fact]
    public async Task AllViewModelCommands_ExecuteWithoutThrowing()
    {
        FakeEngineCliClient fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.CurrentStage = UiStage.Start;
        vm.CustomCommandText = "summary";
        vm.GlobalOutputPath = string.Empty;

        List<PropertyInfo> commandProps = typeof(MainWindowViewModel)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.Name.EndsWith("Command", StringComparison.Ordinal))
            .Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(commandProps);

        List<string> failures = [];
        foreach (PropertyInfo prop in commandProps)
        {
            object? value = prop.GetValue(vm);
            if (value == null)
            {
                failures.Add(prop.Name + ": command instance is null.");
                continue;
            }

            try
            {
                if (value is IAsyncRelayCommand asyncCommand)
                {
                    await asyncCommand.ExecuteAsync(null);
                }
                else if (value is ICommand syncCommand)
                {
                    if (syncCommand.CanExecute(null))
                        syncCommand.Execute(null);
                }
                else
                {
                    failures.Add(prop.Name + ": does not implement ICommand.");
                }
            }
            catch (Exception ex)
            {
                failures.Add(prop.Name + ": " + ex.GetType().Name + " - " + ex.Message);
            }
        }

        Assert.True(
            failures.Count == 0,
            "Command execution failures:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private static MainWindowViewModel CreateVm(FakeEngineCliClient fake, FakeJoystickCaptureService? captureService = null)
    {
        MainWindowViewModel vm = new(fake, captureService ?? new FakeJoystickCaptureService(), loadPersistedUiSettings: false);
        vm.EnginePath = typeof(MainWindowViewModel).Assembly.Location;

        string tempRoot = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests");
        Directory.CreateDirectory(tempRoot);
        // Game lists only from the engine folder a test builds: the run's shared JOYPRO_DATA_DIR gets every game list
        // as soon as any test starts the real engine (it copies them into <data>\GameData, and those win), and other
        // test classes point JOYPRO_DATA_DIR at their own folders while these run.
        vm.GameDataRootOverride = Path.Combine(tempRoot, "smoke-data-" + Guid.NewGuid().ToString("N"));
        vm.ProfilePath = Path.Combine(tempRoot, "test-profile.pr0file");
        vm.MergeRightProfilePath = Path.Combine(tempRoot, "right.pr0file");
        vm.ImportDcsPath = tempRoot;
        vm.ExportDcsPath = tempRoot;
        vm.ImportIl2Path = tempRoot;
        vm.ExportIl2Path = tempRoot;
        vm.ImportStarCitizenPath = tempRoot;
        vm.ExportStarCitizenPath = tempRoot;
        vm.StarCitizenSourcePath = string.Empty;
        return vm;
    }

    private static string LoadMainWindowAxaml()
    {
        string path = ResolveRepositoryFilePath("gui", "JoyPro.Gui", "Views", "MainWindow.axaml");
        return File.ReadAllText(path);
    }

    private static string LoadMainWindowCodeBehind()
    {
        string path = ResolveRepositoryFilePath("gui", "JoyPro.Gui", "Views", "MainWindow.axaml.cs");
        return File.ReadAllText(path);
    }

    private static string ResolveRepositoryFilePath(params string[] relativeParts)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, Path.Combine(relativeParts));
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Repository file not found for test: " + Path.Combine(relativeParts),
            Path.Combine(relativeParts));
    }

    private static bool ContainsSequence(IReadOnlyList<string> args, params string[] sequence)
    {
        if (sequence.Length == 0 || args.Count < sequence.Length)
            return false;

        for (int i = 0; i <= args.Count - sequence.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < sequence.Length; j++)
            {
                if (!string.Equals(args[i + j], sequence[j], StringComparison.OrdinalIgnoreCase))
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
                return true;
        }

        return false;
    }

    private sealed class FakeEngineCliClient : IEngineCliClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public bool UseMismatchedModifierNameScenario { get; set; }
        public bool UseCorruptedModifierDelimiterScenario { get; set; }
        public bool UseMultiSortScenario { get; set; }
        // One axis relation "Pitch" without a bind (the Bind Studio lists it as an unbound row).
        public bool UseUnboundAxisScenario { get; set; }
        public int DbGamesDelayMs { get; set; }
        public bool UseValidationErrorsScenario { get; set; }
        public bool UseHiddenGamesScenario { get; set; }
        public bool UseDbUpdatesPerGameScenario { get; set; }

        public async Task<EngineCommandResult> RunAsync(
            string engineExePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            if (DbGamesDelayMs > 0 &&
                MainWindowViewModelSmokeTests.ContainsSequence(arguments, "db", "games"))
            {
                await Task.Delay(DbGamesDelayMs, cancellationToken);
            }

            // Like the real engine, validate exits with 1 when it found errors.
            bool failedValidation = UseValidationErrorsScenario &&
                                    MainWindowViewModelSmokeTests.ContainsSequence(arguments, "profile", "validate");

            return new EngineCommandResult
            {
                ExitCode = failedValidation ? 1 : 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = BuildOutput(arguments),
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            };
        }

        private string BuildOutput(IReadOnlyList<string> args)
        {
            string keyboardModifierName = UseMismatchedModifierNameScenario ? "KeyboardHold" : "LCtrl";
            string existingModifierJson = BuildReformer("ExistingMod", "Test Device", "JOY_BTN1", false, jsonEscaped: true);
            string keyboardModifierJson = BuildReformer(keyboardModifierName, "Keyboard", "LeftControl", false, jsonEscaped: true);
            string existingModifierText = BuildReformer("ExistingMod", "Test Device", "JOY_BTN1", false, jsonEscaped: false);
            string keyboardModifierText = BuildReformer(keyboardModifierName, "Keyboard", "LeftControl", false, jsonEscaped: false);

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "relations", "suggest", "--all"))
                return "{\"Relation\":\"\",\"Suggestions\":[],\"Relations\":[{\"Relation\":\"Pitch\",\"Suggestions\":3,\"High\":2,\"Planes\":4},{\"Relation\":\"Roll\",\"Suggestions\":1,\"High\":1,\"Planes\":1}],\"Warnings\":[],\"Errors\":[]}";

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "relations", "suggest") && !args.Contains("apply"))
            {
                string relation = args[args.ToList().IndexOf("suggest") + 1];
                return "{\"Relation\":\"" + relation + "\",\"Slot\":\"pitch\",\"SlotName\":\"Pitch\",\"Suggestions\":[" +
                       "{\"Game\":\"DCS\",\"Id\":\"a2001cdnil\",\"Title\":\"Pitch\",\"Planes\":[\"FA-18C_hornet\",\"A-10C\"],\"Confidence\":\"high\",\"Reasons\":[\"'Pitch' is the 'Pitch' function in FA-18C_hornet, A-10C (function catalog, checked against the manuals).\"],\"InOtherRelations\":[]}," +
                       "{\"Game\":\"DCS\",\"Id\":\"a2013cdnil\",\"Title\":\"Flight Control Cyclic Pitch\",\"Planes\":[\"SA342\"],\"Confidence\":\"high\",\"Reasons\":[\"catalog\"],\"InOtherRelations\":[\"Cyclic Pitch\"]}," +
                       "{\"Game\":\"Arma3\",\"Id\":\"Axis.PlanePitch\",\"Title\":\"Plane pitch\",\"Planes\":[\"Arma3\"],\"Confidence\":\"low\",\"Reasons\":[\"Title matches\"],\"InOtherRelations\":[]}" +
                       "],\"Misfits\":[{\"Game\":\"DCS\",\"Id\":\"d3001\",\"Title\":\"Landing Gear Up\",\"Planes\":[\"MosquitoFBMkVI\"],\"Reason\":\"The function catalog files 'Landing Gear Up' under 'GearIn'.\",\"BelongsTo\":\"gear_in\",\"MoveTo\":\"GearIn\"}]" +
                       ",\"Relations\":[],\"Warnings\":[],\"Errors\":[]}";
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "overlay", "map"))
            {
                const string stick = "Test Stick {00000000-0000-0000-0000-000000000042}";
                return "{\"Game\":\"DCS\",\"Plane\":\"A-10C\",\"DcsPlanes\":[\"A-10C\",\"F-16C_50\"],\"ProcessNames\":{\"FakeGame\":[\"joypro_no_such_process_42\"]},\"Entries\":[" +
                       "{\"Relation\":\"Gear Toggle\",\"Title\":\"Landing Gear Lever\",\"Device\":\"" + stick + "\",\"Input\":\"JOY_BTN1\",\"Modifiers\":[]}," +
                       "{\"Relation\":\"Flaps Up\",\"Title\":\"\",\"Device\":\"" + stick + "\",\"Input\":\"JOY_BTN1\",\"Modifiers\":[{\"Name\":\"Shift\",\"Device\":\"" + stick + "\",\"Input\":\"JOY_BTN5\",\"Switch\":false}]}" +
                       "],\"Warnings\":[]}";
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "games", "install", "set"))
                return "{\"Games\":[{\"GameId\":\"IL2Game\",\"Name\":\"IL-2 Sturmovik: Great Battles\",\"Installed\":true,\"InstallPath\":\"D:/Games/IL-2\",\"Source\":\"override\",\"Overridden\":true,\"MarkerFound\":true,\"ConfigPaths\":[\"D:/Games/IL-2/data/input\"],\"Note\":\"\"}],\"Warnings\":[]}";

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "games", "installs"))
            {
                return "{\"Games\":[" +
                       "{\"GameId\":\"DCS\",\"Name\":\"DCS World\",\"Installed\":true,\"InstallPath\":\"G:/DCS World\",\"Source\":\"known place\",\"Overridden\":false,\"MarkerFound\":true,\"ConfigPaths\":[\"C:/Users/x/Saved Games/DCS\"],\"Note\":\"\"}," +
                       "{\"GameId\":\"IL2Game\",\"Name\":\"IL-2 Sturmovik: Great Battles\",\"Installed\":false,\"InstallPath\":\"\",\"Source\":\"\",\"Overridden\":false,\"MarkerFound\":false,\"Markers\":[\"bin\\\\game\\\\Il-2.exe\"],\"ConfigInInstallation\":true,\"ConfigPaths\":[],\"Note\":\"Not found.\"}," +
                       "{\"GameId\":\"StarCitizen\",\"Name\":\"Star Citizen\",\"Installed\":true,\"InstallPath\":\"C:/RSI/StarCitizen\",\"Source\":\"known place\",\"Overridden\":false,\"MarkerFound\":true,\"ConfigPaths\":[],\"Note\":\"\"}," +
                       (UseHiddenGamesScenario
                           ? "{\"GameId\":\"WarThunder\",\"Name\":\"War Thunder\",\"Installed\":false,\"InstallPath\":\"D:/WT\",\"Source\":\"steam\",\"Overridden\":false,\"MarkerFound\":true,\"Disabled\":true,\"ConfigPaths\":[],\"Note\":\"You do not have this game.\"},"
                           : "{\"GameId\":\"WarThunder\",\"Name\":\"War Thunder\",\"Installed\":false,\"InstallPath\":\"\",\"Source\":\"\",\"Overridden\":false,\"MarkerFound\":false,\"ConfigPaths\":[],\"Note\":\"Not found.\"},") +
                       "{\"GameId\":\"MSFS2024\",\"Name\":\"Microsoft Flight Simulator 2024\",\"Installed\":true,\"InstallPath\":\"D:/MSFS2024\",\"Source\":\"steam\",\"Overridden\":false,\"MarkerFound\":true,\"ConfigPaths\":[],\"Note\":\"\"}," +
                       "{\"GameId\":\"MSFS2020\",\"Name\":\"Microsoft Flight Simulator 2020\",\"Installed\":true,\"InstallPath\":\"D:/MSFS\",\"Source\":\"steam\",\"Overridden\":false,\"MarkerFound\":true,\"ConfigPaths\":[],\"Note\":\"\"}" +
                       "],\"Warnings\":[]}";
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "hud", "devices"))
            {
                return "{\"Devices\":[" +
                       "{\"Device\":\"VKB Stick {00000000-0000-0000-0000-0000000000B1}\",\"Name\":\"VKB Stick\",\"Model\":\"VKB MCG Pro\",\"ModelId\":\"vkb-mcg\",\"Kind\":\"stick\",\"Hand\":\"right\",\"Image\":\"\",\"Buttons\":4,\"Hats\":1," +
                       "\"Axes\":[{\"Input\":\"JOY_X\",\"Control\":\"stick.axis.roll\",\"Label\":\"Roll\"},{\"Input\":\"JOY_Y\",\"Control\":\"stick.axis.pitch\",\"Label\":\"Pitch\"},{\"Input\":\"JOY_Z\",\"Control\":\"stick.axis.twist\",\"Label\":\"Twist\"}]," +
                       "\"Labels\":{\"JOY_BTN1\":\"Trigger\"},\"GripChoices\":[],\"Layout\":null}," +
                       "{\"Device\":\"Throttle {00000000-0000-0000-0000-0000000000B2}\",\"Name\":\"Throttle\",\"Model\":\"MT-50\",\"ModelId\":\"mt50\",\"Kind\":\"throttle\",\"Hand\":\"left\",\"Image\":\"\",\"Buttons\":2,\"Hats\":0," +
                       "\"Axes\":[{\"Input\":\"JOY_RX\",\"Control\":\"throttle.axis.left\",\"Label\":\"Left throttle\"},{\"Input\":\"JOY_X\",\"Control\":\"throttle.axis.ministick.x\",\"Label\":\"Thumb X\"}],\"Labels\":{},\"GripChoices\":[],\"Layout\":null}," +
                       "{\"Device\":\"Base {00000000-0000-0000-0000-0000000000B3}\",\"Name\":\"Base\",\"Model\":\"\",\"ModelId\":\"\",\"Kind\":\"stick\",\"Hand\":\"\",\"Image\":\"\",\"Buttons\":0,\"Hats\":0,\"Axes\":[],\"Labels\":{}," +
                       "\"GripChoices\":[{\"Id\":\"grip-a\",\"Name\":\"Grip A\",\"Image\":\"\"}],\"Layout\":null}," +
                       "{\"Device\":\"Keyboard HE {00000000-0000-0000-0000-0000000000B4}\",\"Name\":\"Keyboard HE\",\"Model\":\"\",\"ModelId\":\"\",\"Kind\":\"other\",\"Hand\":\"\",\"Image\":\"\",\"Buttons\":12,\"Hats\":0,\"Axes\":[],\"Labels\":{},\"GripChoices\":[],\"Layout\":null}" +
                       "],\"Warnings\":[]}";
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "overlay", "dcs-hook"))
                return "{\"Action\":\"install\",\"Installed\":true,\"Changes\":[],\"Warnings\":[],\"Errors\":[]}";

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "profile", "games"))
            {
                return "{\"Games\":[" +
                       "{\"GameId\":\"DCS\",\"Name\":\"DCS World\",\"Exporter\":\"dcs\",\"Relations\":10,\"Bound\":9,\"Planes\":[{\"Plane\":\"A-10C\",\"Relations\":5,\"Bound\":5},{\"Plane\":\"F-16C_50\",\"Relations\":6,\"Bound\":5}]}," +
                       "{\"GameId\":\"IL2Game\",\"Name\":\"IL-2 Sturmovik: Great Battles\",\"Exporter\":\"il2\",\"Relations\":3,\"Bound\":3,\"Planes\":[]}," +
                       "{\"GameId\":\"WarThunder\",\"Name\":\"War Thunder\",\"Exporter\":\"registry\",\"Relations\":2,\"Bound\":1," +
                       (UseHiddenGamesScenario ? "\"Installed\":false,\"Disabled\":true," : string.Empty) + "\"Planes\":[]}," +
                       "{\"GameId\":\"X4\",\"Name\":\"X4: Foundations\",\"Exporter\":\"registry\",\"Relations\":4,\"Bound\":4,\"Installed\":false,\"Planes\":[]}]}";
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "backups", "list"))
            {
                return "[{\"Id\":\"20261001-120000-000_DCS\",\"Game\":\"DCS\",\"Command\":\"engine dcs export\",\"Created\":\"2026-10-01T12:00:00\"," +
                       "\"Files\":[{\"Original\":\"a\"},{\"Original\":\"b\"},{\"Original\":\"c\"}]}," +
                       "{\"Id\":\"20261001-110000-000_X4\",\"Game\":\"X4\",\"Command\":\"restore 20261001-100000-000_X4\",\"Created\":\"2026-10-01T11:00:00\",\"Files\":[{\"Original\":\"x\"}]}" +
                       (UseHiddenGamesScenario ? ",{\"Id\":\"20261001-100000-000_WarThunder\",\"Game\":\"WarThunder\",\"Command\":\"engine warthunder export\",\"Created\":\"2026-10-01T10:00:00\",\"Files\":[{\"Original\":\"w\"}]}" : string.Empty) + "]";
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "devices", "kb", "identify"))
            {
                return "[{\"Device\":\"Test Base {00000000-0000-0000-0000-0000000000a1}\",\"Connected\":true,\"Model\":\"\",\"ModelId\":\"\",\"Kind\":\"\",\"Generic\":false,\"Note\":\"several grips fit this base\",\"AlternativeTo\":\"\"," +
                       "\"GripChoices\":[{\"Id\":\"grip-a\",\"Name\":\"Grip A\",\"Image\":\"\"},{\"Id\":\"grip-b\",\"Name\":\"Grip B\",\"Image\":\"\"}]}," +
                       "{\"Device\":\"Test Panel {00000000-0000-0000-0000-0000000000a2}\",\"Connected\":true,\"Model\":\"Test Panel\",\"ModelId\":\"test-panel\",\"Kind\":\"panel\",\"Generic\":false,\"Note\":\"\",\"AlternativeTo\":\"\",\"GripChoices\":[]}]";
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "devices", "kb", "list"))
            {
                return "[{\"Id\":\"tm-warthog-base\",\"Name\":\"Thrustmaster Warthog Base\",\"Kind\":\"base\",\"Base\":\"\",\"Confidence\":\"high\"}," +
                       "{\"Id\":\"tm-warthog-throttle\",\"Name\":\"Thrustmaster HOTAS Warthog Throttle\",\"Kind\":\"throttle\",\"Base\":\"\",\"Confidence\":\"high\"}]";
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "games", "integrations"))
            {
                return "{\"Integrations\":[" +
                       "{\"GameId\":\"WarThunder\",\"DisplayName\":\"War Thunder\",\"Aliases\":[\"warthunder\",\"wt\"],\"Experimental\":true,\"Notes\":\"WT notes\",\"SupportsCatalogRefresh\":true,\"SupportsGameProfile\":false}," +
                       "{\"GameId\":\"MSFS2024\",\"DisplayName\":\"Microsoft Flight Simulator 2024\",\"Aliases\":[\"msfs2024\",\"fs2024\",\"msfs\"],\"Experimental\":true,\"Notes\":\"MSFS notes\",\"SupportsCatalogRefresh\":true,\"SupportsGameProfile\":true,\"SupportsAircraft\":true}," +
                       "{\"GameId\":\"MSFS2020\",\"DisplayName\":\"Microsoft Flight Simulator 2020\",\"Aliases\":[\"msfs2020\",\"fs2020\"],\"Experimental\":true,\"Notes\":\"MSFS 2020 notes\",\"SupportsCatalogRefresh\":true,\"SupportsGameProfile\":true,\"SupportsAircraft\":true}]}";
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "msfs2024", "paths"))
                return "{\"Game\":\"MSFS2024\",\"ConfigPaths\":[\"D:/Games/remote\"],\"Warnings\":[]}";

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "devices", "list"))
            {
                return string.Join(
                    Environment.NewLine,
                    "Devices",
                    "Connected: 1",
                    "  - Test Connected Device {00000000-0000-0000-0000-000000000001}",
                    "Profile: 1",
                    "  - Test Profile Device {00000000-0000-0000-0000-000000000099}",
                    "Only Connected:",
                    "  - Test Connected Device {00000000-0000-0000-0000-000000000001}",
                    "Only In Profile:",
                    "  - Test Profile Device {00000000-0000-0000-0000-000000000099}");
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "devices", "inputs"))
            {
                return string.Join(
                    Environment.NewLine,
                    "Device Inputs",
                    "  - JOY_BTN1",
                    "  - JOY_X");
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "db", "games"))
            {
                return string.Join(
                    Environment.NewLine,
                    "DB Games: 3",
                    "DCS | planes=153 | folder=DCS | db=C:\\Dummy\\DB\\DCS",
                    "IL2Game | planes=1 | folder=IL2 | db=C:\\Dummy\\DB\\IL2",
                    "StarCitizen | planes=1 | folder=StarCitizen | db=C:\\Dummy\\DB\\StarCitizen" +
                    (UseHiddenGamesScenario ? Environment.NewLine + "WarThunder | planes=1 | folder=WarThunder | db=C:\\Dummy\\DB\\WarThunder" : string.Empty));
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "db", "planes"))
            {
                return string.Join(
                    Environment.NewLine,
                    "DB Planes: 2 (Games: 1)",
                    "DCS (2)",
                    "  - A-10C_2",
                    "  - FA-18C_hornet");
            }

            if (UseDbUpdatesPerGameScenario && MainWindowViewModelSmokeTests.ContainsSequence(args, "db", "check-updates"))
            {
                string Updated(string game, string name, string refresh) =>
                    "{\"Game\":\"" + game + "\",\"DisplayName\":\"" + name + "\",\"State\":\"game-updated\",\"Message\":\"" + name + " was updated since its DB was built.\",\"RefreshCommand\":\"" + refresh + "\",\"Details\":[]}";
                List<string> games =
                [
                    Updated("DCS", "DCS World", "engine dcs refresh id"),
                    Updated("StarCitizen", "Star Citizen", "engine db refresh-starcitizen"),
                    Updated("WarThunder", "War Thunder", "engine warthunder refresh-db"),
                    "{\"Game\":\"IL2Game\",\"DisplayName\":\"IL-2 Great Battles\",\"State\":\"up-to-date\",\"Message\":\"\",\"RefreshCommand\":\"engine db refresh-il2\",\"Details\":[]}"
                ];
                if (!args.Contains("NoRefreshGame"))
                    games.Add(Updated("NoRefreshGame", "Game Without Refresh", "update JoyPro"));
                return "{\"Games\":[" + string.Join(",", games) + "],\"Outdated\":" + (games.Count - 1) + ",\"Warnings\":[]}";
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "devices", "kb", "show"))
            {
                string id = args[args.ToList().IndexOf("show") + 1];
                string name = id == "test-panel" ? "Test Panel" : id;
                return "{\"Id\":\"" + id + "\",\"Name\":\"" + name + "\",\"Vendor\":\"Test Vendor\",\"Kind\":\"panel\",\"Hand\":\"none\",\"Base\":\"\",\"Image\":\"\",\"Confidence\":\"high\"," +
                       "\"VidPids\":[\"1234:5678\"],\"Names\":[\"Test Panel\"],\"Axes\":[{\"Input\":\"JOY_X\",\"Control\":\"panel.axis.x\",\"Label\":\"\",\"Type\":\"momentary\",\"Finger\":\"\"}]," +
                       "\"Controls\":[{\"Input\":\"JOY_BTN1\",\"Control\":\"stick.trigger.stage1\",\"Label\":\"Trigger\",\"Type\":\"momentary\",\"Finger\":\"index\"}," +
                       "{\"Input\":\"\",\"Control\":\"panel.switch.unknown\",\"Label\":\"\",\"Type\":\"latching\",\"Finger\":\"\"}]}";
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "relations", "auto-assign"))
                return "Auto Assign" + Environment.NewLine + "Assignments:" + (args.Contains("--explain") ? Environment.NewLine + "Explain:" + Environment.NewLine + "  RelA: slot fire" : string.Empty);

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "db", "check-updates"))
            {
                if (args.Contains("--accept-all"))
                    return "{\"Games\":[{\"Game\":\"DCS\",\"State\":\"up-to-date\",\"Message\":\"\",\"Details\":[]}],\"Outdated\":0,\"Warnings\":[]}";
                return "{\"Games\":[{\"Game\":\"DCS\",\"State\":\"game-updated\",\"Message\":\"DCS World was updated since its DB was built.\",\"Details\":[\"new module(s): F-4E\"]}," +
                       "{\"Game\":\"IL2Game\",\"State\":\"up-to-date\",\"Message\":\"\",\"Details\":[]}],\"Outdated\":1,\"Warnings\":[]}";
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "layout", "template-inspect"))
            {
                return "{\"SourcePath\":\"C:/dummy/layouts/VKB_GunfighterMK3.layout\",\"PreviewImagePath\":\"\",\"Width\":1000,\"Height\":800," +
                       "\"Markers\":[{\"Token\":\"JOY_BTN1\",\"Type\":\"button\",\"Modifier\":\"\",\"X\":100,\"Y\":200},{\"Token\":\"JOY_X\",\"Type\":\"axis\",\"Modifier\":\"\",\"X\":400,\"Y\":60}],\"Tables\":[],\"Connectors\":[]}";
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "layout", "suggest"))
            {
                bool wantsText = args.Any(v => string.Equals(v, "--text", StringComparison.OrdinalIgnoreCase));
                if (wantsText)
                {
                    return string.Join(
                        Environment.NewLine,
                        "Layout Suggest",
                        "Requested Devices: 1",
                        "Suggestions: 1",
                        "  - Test Connected Device {00000000-0000-0000-0000-000000000001} | template=VKB Gunfighter MK3 | score=88");
                }

                return "{\"FormatVersion\":\"1\",\"GeneratedUtc\":\"2026-01-01T00:00:00Z\",\"CatalogPath\":\"C:/dummy/catalog.json\",\"LegacyLayoutDirectories\":[\"C:/dummy/layouts\"],\"LegacyOutputDirectories\":[\"C:/dummy/output\"],\"RequestedDevices\":[\"Test Connected Device {00000000-0000-0000-0000-000000000001}\"],\"CatalogEntries\":1,\"CandidateLayouts\":1,\"AppliedMappings\":1,\"Suggestions\":[{\"Device\":\"Test Connected Device {00000000-0000-0000-0000-000000000001}\",\"TemplateName\":\"VKB Gunfighter MK3\",\"LayoutPath\":\"C:/dummy/layouts/VKB_GunfighterMK3.layout\",\"BackdropPath\":\"C:/dummy/layouts/VKB_GunfighterMK3.layout\",\"Source\":\"catalog\",\"Score\":88,\"MatchReason\":\"token overlap 3\",\"LayoutMapSpec\":\"--layout-map \\\"Test Connected Device {00000000-0000-0000-0000-000000000001}=C:/dummy/layouts/VKB_GunfighterMK3.layout\\\"\",\"MarkerCount\":42,\"ButtonMarkers\":30,\"AxisMarkers\":8,\"PovMarkers\":2,\"SliderMarkers\":2,\"Markers\":[],\"Tables\":[]}],\"Warnings\":[],\"Errors\":[]}";
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "relations", "list"))
            {
                bool wantsText = args.Any(v => string.Equals(v, "--text", StringComparison.OrdinalIgnoreCase));
                if (UseUnboundAxisScenario)
                {
                    return wantsText
                        ? "Relations:" + Environment.NewLine + "Pitch | type=axis | bind=unbound | mods=0 | games=DCS | planes=1 | groups=Flight"
                        : "{\"FormatVersion\":\"1\",\"GeneratedUtc\":\"2026-01-01T00:00:00Z\",\"Relations\":[{\"Name\":\"Pitch\",\"IsAxis\":true,\"Groups\":[\"Flight\"],\"Nodes\":[{\"Id\":\"a2001cdnil\",\"Game\":\"DCS\",\"Aircraft\":{\"FA-18C_hornet\":true}}],\"Bind\":null}]}";
                }

                if (!wantsText)
                {
                    if (UseMultiSortScenario)
                    {
                        return "{\"FormatVersion\":\"1\",\"GeneratedUtc\":\"2026-01-01T00:00:00Z\",\"Relations\":[{\"Name\":\"RelA\",\"IsAxis\":false,\"Groups\":[\"Test\"],\"Nodes\":[{\"Id\":\"id_a\",\"Game\":\"DCS\",\"Aircraft\":{\"A-10C\":true}}],\"Bind\":null},{\"Name\":\"RelB\",\"IsAxis\":false,\"Groups\":[\"Test\"],\"Nodes\":[{\"Id\":\"id_b\",\"Game\":\"DCS\",\"Aircraft\":{\"A-10C\":true}}],\"Bind\":null},{\"Name\":\"RelC\",\"IsAxis\":false,\"Groups\":[\"Test\"],\"Nodes\":[{\"Id\":\"id_c\",\"Game\":\"DCS\",\"Aircraft\":{\"A-10C\":true}}],\"Bind\":null}]}";
                    }

                    return "{\"FormatVersion\":\"1\",\"GeneratedUtc\":\"2026-01-01T00:00:00Z\",\"Relations\":[{\"Name\":\"TestRelation\",\"IsAxis\":false,\"Groups\":[\"Test\"],\"Nodes\":[{\"Id\":\"dummy_id\",\"Game\":\"DCS\",\"Aircraft\":{\"A-10C\":true}}],\"Bind\":null}]}";
                }

                if (UseMultiSortScenario)
                {
                    return string.Join(
                        Environment.NewLine,
                        "Relations:",
                        "RelA | type=button | bind=unbound | mods=0 | games=DCS | planes=1 | groups=Test",
                        "RelB | type=button | bind=unbound | mods=0 | games=DCS | planes=1 | groups=Test",
                        "RelC | type=button | bind=unbound | mods=0 | games=DCS | planes=1 | groups=Test");
                }

                return string.Join(
                    Environment.NewLine,
                    "Relations:",
                    "TestRelation | type=button | bind=unbound | mods=0 | games=DCS | planes=1 | groups=Test");
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "relations", "show"))
            {
                return string.Join(
                    Environment.NewLine,
                    "Relation: TestRelation",
                    "Nodes:",
                    "  [1] game=DCS, id=dummy_id, aircraft=A-10C");
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "binds", "list"))
            {
                if (UseUnboundAxisScenario)
                {
                    return args.Any(v => string.Equals(v, "--text", StringComparison.OrdinalIgnoreCase))
                        ? "Binds:"
                        : "{\"FormatVersion\":\"1\",\"GeneratedUtc\":\"2026-01-01T00:00:00Z\",\"Binds\":[]}";
                }

                if (UseMultiSortScenario)
                {
                    return string.Join(
                        Environment.NewLine,
                        "Binds:",
                        "RelA | type=button | device=JoystickB | input=JOY_BTN2 | modifiers(0)=<none>",
                        "RelB | type=button | device=JoystickA | input=JOY_BTN3 | modifiers(0)=<none>",
                        "RelC | type=button | device=JoystickA | input=JOY_BTN1 | modifiers(0)=<none>");
                }

                bool wantsText = args.Any(v => string.Equals(v, "--text", StringComparison.OrdinalIgnoreCase));
                if (!wantsText)
                {
                    return "{\"FormatVersion\":\"1\",\"GeneratedUtc\":\"2026-01-01T00:00:00Z\",\"Binds\":[{\"RelationName\":\"TestRelation\",\"IsAxis\":false,\"Joystick\":\"Test Device\",\"JAxis\":\"\",\"JButton\":\"JOY_BTN1\",\"Inverted\":null,\"Slider\":null,\"Deadzone\":0,\"Curvature\":[],\"SaturationX\":1,\"SaturationY\":1,\"AllReformers\":[\"" + keyboardModifierJson + "\",\"" + existingModifierJson + "\"],\"AdditionalImportInfo\":\"\",\"AliasJoystick\":\"\",\"DoubleTap\":false}]}";
                }

                return string.Join(
                    Environment.NewLine,
                    "Binds:",
                    "TestRelation | type=button | device=Test Device | input=JOY_BTN1 | modifiers(2)=" +
                    keyboardModifierText + "," + existingModifierText);
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "binds", "modifier", "list"))
            {
                int listIndex = args.ToList().FindIndex(v => string.Equals(v, "list", StringComparison.OrdinalIgnoreCase));
                string scopedRelation = listIndex >= 0 && listIndex + 1 < args.Count
                    ? args[listIndex + 1]
                    : string.Empty;
                bool hasRelationSelector = !string.IsNullOrWhiteSpace(scopedRelation) &&
                                           !scopedRelation.StartsWith("-", StringComparison.Ordinal);

                if (!hasRelationSelector)
                {
                    return string.Join(
                        Environment.NewLine,
                        "Modifiers",
                        "Modifiers: 2",
                        "  - " + keyboardModifierName + " (1): TestRelation",
                        "  - ExistingMod (1): TestRelation");
                }

                if (string.Equals(scopedRelation, "TestRelation", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Join(
                        Environment.NewLine,
                        "Bind Modifiers",
                        "Relation: TestRelation",
                        "Modifiers: 2",
                        "  - ExistingMod",
                        "  - " + keyboardModifierName,
                        "Reformers: 2",
                        "  - " + existingModifierText,
                        "  - " + keyboardModifierText);
                }

                return string.Join(
                    Environment.NewLine,
                    "Bind Modifiers",
                    "Relation: SomeRelation",
                    "Modifiers: 1",
                    "  - LCtrl",
                    "Reformers: 1",
                    "  - " + BuildReformer("LCtrl", "Keyboard", "LeftControl", false, jsonEscaped: false));
            }

            if (MainWindowViewModelSmokeTests.ContainsSequence(args, "profile", "validate"))
            {
                // Mirrors ToText(ProfileValidationResult) in engine/Program.cs.
                if (UseValidationErrorsScenario)
                {
                    return string.Join(
                        Environment.NewLine,
                        "Profile Validation",
                        "Passed: False",
                        "Fail On Warnings: False",
                        "Warnings: 1",
                        "Errors: 2",
                        "Relation Warnings: 1",
                        "  - Relation 'Gear' has no active plane.",
                        "Bind Errors: 1",
                        "  - Bind 'TestRelation' uses unknown device 'Missing Stick'.",
                        "Modifier Errors: 0",
                        "Duplicate Active Errors: 1",
                        "  - Double bound. The Relations it was found in: TestRelation, Gear");
                }

                return string.Join(
                    Environment.NewLine,
                    "Profile Validation",
                    "Passed: True",
                    "Fail On Warnings: False",
                    "Warnings: 0",
                    "Errors: 0",
                    "Relation Warnings: 0",
                    "Bind Errors: 0",
                    "Modifier Errors: 0",
                    "Duplicate Active Errors: 0");
            }

            return "ok";
        }

        private string BuildReformer(string name, string device, string input, bool inverted, bool jsonEscaped)
        {
            string separator = UseCorruptedModifierDelimiterScenario
                ? (jsonEscaped ? "\\uFFFD" : "\uFFFD")
                : (jsonEscaped ? "\\u00A7" : "\u00A7");
            return name + separator + device + separator + input + separator + (inverted ? "True" : "False");
        }
    }

    private sealed class FakeJoystickCaptureService : IJoystickCaptureService
    {
        public JoystickCaptureService.CaptureOptions? LastOptions { get; private set; }
        public JoystickCaptureService.CaptureResult? NextCapture { get; set; }
        public string LastDiagnostics { get; set; } = string.Empty;

        public Task<JoystickCaptureService.CaptureResult?> CaptureNextAsync(
            JoystickCaptureService.CaptureOptions options,
            CancellationToken cancellationToken)
        {
            LastOptions = options;
            return Task.FromResult(NextCapture);
        }

        public bool Monitoring { get; private set; }

        public bool MonitoringAxes { get; private set; }

        public Task MonitorAsync(Action<JoystickInputEvent> onEvent, Action<JoystickAxisSnapshot>? onAxes, CancellationToken cancellationToken)
        {
            Monitoring = true;
            MonitoringAxes = onAxes != null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOverlayPresenter : IOverlayPresenter
    {
        public int Shown { get; private set; }
        public int Hidden { get; private set; }

        public void Show(object dataContext) => Shown++;

        public void Hide() => Hidden++;
    }
}

