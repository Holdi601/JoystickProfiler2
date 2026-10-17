using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Engine.Axis;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The axis curve manager (Binds page, Axis Tuning) against a fake engine with one axis relation "Pitch" that has DCS
// nodes for two aircraft and an IL-2 Korea node.
[SupportedOSPlatform("windows")]
public sealed class AxisTuningTests
{
    internal const string Stick = "Test Stick {00000000-0000-0000-0000-0000000000C1}";

    // The MSFS model (engine\AxisModels\Msfs.cs) reaches the GUI preview through [GameAxisModel]; its preview is the
    // curve measured in the game, and the base fit of a linear curve is a neutral Sensitivity page.
    [Fact]
    public void MsfsModel_LinkedForPreview()
    {
        AxisModel model = AxisModels.ForGame("MSFS2024");
        Assert.Equal("msfs", model.Id);
        Assert.Contains("sensitivityPlus", model.SharedPerAxisKeys);
        AxisTuning tuning = AxisTuning.Parse("inv=0;n.axisDeadZone=10;n.sensitivityPlus=-30");
        // MSFS works on the mirrored axis: the positive stick half takes "Sensitivity -" (0 here: linear after the dead
        // zone), the negative half "Sensitivity +" through v(1+s)/(1-s+2sv) with v = (0.5 - 0.1) / 0.9 and s = -0.3.
        double v = 0.4 / 0.9;
        Assert.Equal(v, model.Evaluate(tuning, 0.5), 9);
        Assert.Equal(-v * 0.7 / (1.3 - 0.6 * v), model.Evaluate(tuning, -0.5), 9);
        Assert.Equal(0, model.Evaluate(tuning, 0.05), 9);
        AxisTuning fitted = AxisModels.Inherit(new AxisTuning(), model);
        Assert.Equal(0, fitted.NativeOr("sensitivityPlus", 99));
        Assert.Equal(0, fitted.NativeOr("axisDeadZone", 99));
    }

    [Fact]
    public async Task ScopeList_FromDcsPlanesAndIl2KoreaNodes()
    {
        AxisTuningFakeEngine fake = new();
        MainWindowViewModel vm = await CreateLoadedVmAsync(fake);

        Assert.Equal(
            ["base", "game:DCS", "plane:DCS:A-10C", "plane:DCS:F-16C_50", "game:IL2Korea", "family:il2"],
            vm.AxisTuningScopes.Select(s => s.Scope).ToArray());
        Assert.Equal(
            ["All games (base)", "DCS", "DCS / A-10C", "DCS / F-16C_50", "IL-2 Korea", "IL-2 family (Great Battles + Korea)"],
            vm.AxisTuningScopes.Select(s => s.Label).ToArray());
        Assert.Equal("DCS", vm.AxisTuningScopes[0].Game);
        Assert.Equal("dcs", vm.AxisTuningScopes[0].Model.Id);
        Assert.Equal("il2korea", vm.AxisTuningScopes.Single(s => s.Scope == "family:il2").Model.Id);
        Assert.Equal("F-16C_50", vm.AxisTuningScopes.Single(s => s.Scope == "plane:DCS:F-16C_50").Plane);

        // The base is selected first; it is always "own" and cannot be cleared.
        Assert.Equal("base", vm.SelectedAxisTuningScope?.Scope);
        Assert.Equal("Own settings for All games (base)", vm.AxisTuningStatus);
        Assert.False(vm.CanClearAxisTuning);
        Assert.True(vm.CanSaveAxisTuning);
        Assert.Contains(fake.Calls, c => Seq(c, "binds", "tuning", "show", "Pitch") && !c.Contains("--game"));
    }

    // The X-Plane model (engine\AxisModels\XPlane.cs, linked) previews an imported curve through its exact stops and
    // fits a base without X-Plane values as LINEAR stops.
    [Fact]
    public void XPlaneModel_PreviewsStopsAndFitsTheBase()
    {
        AxisModel model = AxisModels.ForGame("XPlane12");
        Assert.Equal("xplane", model.Id);
        AxisTuning imported = new();
        imported.SetControlPoints([new(0, 0), new(0.5, 0.2), new(1, 1)]);
        Assert.Equal(0.2, model.Evaluate(imported, 0.5), 9);
        Assert.Equal(-0.2, model.Evaluate(imported, -0.5), 9);
        imported.Slider = true;
        Assert.Equal(-0.6, model.Evaluate(imported, 0.0), 9);

        AxisTuning baseTuning = new() { Deadzone = 0.1, Curvature = [0.3] };
        AxisTuning fitted = AxisModels.Inherit(baseTuning, model);
        Assert.True(fitted.HasControlPoints);
        AxisModel dcs = AxisModels.Get(AxisModels.Dcs);
        for (int i = 0; i <= 100; i++)
        {
            double x = -1 + i / 50.0;
            Assert.True(Math.Abs(model.Evaluate(fitted, x) - dcs.Evaluate(baseTuning, x)) < 0.0015, "x=" + x);
        }
    }

    [Fact]
    public void ScopeList_OtherGamesHaveNoPlaneEntries()
    {
        List<AxisTuningScopeItem> scopes = MainWindowViewModel.BuildAxisTuningScopes(
        [
            ("IL2Game", ["IL2Game"]),
            ("Arma3", ["Arma3"]),
            ("Arma2OA", ["Arma2OA"]),
            ("DCS", ["FA-18C_hornet"]),
            ("DCS", ["A-10C", "FA-18C_hornet"])
        ]);
        // Arma 3 has its own model and no family; Arma 2 and OA share family:arma2.
        Assert.Equal(
            ["base", "game:IL2Game", "game:Arma3", "game:Arma2OA", "game:DCS", "plane:DCS:A-10C", "plane:DCS:FA-18C_hornet", "family:il2", "family:arma2"],
            scopes.Select(s => s.Scope).ToArray());
        // A family with only the Great Battles is edited with the Great Battles model.
        Assert.Equal("il2", scopes.Single(s => s.Scope == "family:il2").Model.Id);
        Assert.Equal("arma2", scopes.Single(s => s.Scope == "family:arma2").Model.Id);
        Assert.Equal("arma3", scopes.Single(s => s.Scope == "game:Arma3").Model.Id);
    }

    [Fact]
    public async Task Preview_ComesFromTheLinkedModels_AndEditsDoNotCallTheEngine()
    {
        AxisTuningFakeEngine fake = new();
        MainWindowViewModel vm = await CreateLoadedVmAsync(fake);

        // IL-2 Korea has its own values: sensitivity 50.
        await SelectScopeAsync(vm, "game:IL2Korea");
        Assert.Equal("Own settings for IL-2 Korea", vm.AxisTuningStatus);
        Assert.Contains(fake.Calls, c => Seq(c, "binds", "tuning", "show", "Pitch", "--game", "IL2Korea"));
        Assert.Equal(MainWindowViewModel.AxisTuningPreviewPoints, vm.AxisTuningCurve.Count);
        Assert.Equal(0.5, vm.AxisTuningCurve[150].X, 9);
        Assert.Equal(0.328427, vm.AxisTuningCurve[150].Y, 5);
        Assert.Null(vm.AxisTuningReferenceCurve);
        Assert.Equal(["sensitivity", "deadZone0", "deadZone1", "offset", "harmonization", "normalized", "relative", "relativeDeadZone", "relativeSensitivity", "slider", "invert"], vm.AxisTuningEditors.Select(e => e.Key).ToArray());
        Assert.True(vm.CanClearAxisTuning);

        Assert.False(vm.AxisTuningHasEdits);
        int calls = fake.Calls.Count;
        AxisParameterEditor sensitivity = vm.AxisTuningEditors.Single(e => e.Key == "sensitivity");
        sensitivity.Text = "0";
        Assert.Equal(0.5, vm.AxisTuningCurve[150].Y, 5);
        Assert.True(vm.AxisTuningHasEdits);
        sensitivity.Value = 50;
        Assert.Equal("50", sensitivity.Text);
        Assert.Equal(0.328427, vm.AxisTuningCurve[150].Y, 5);
        vm.AxisTuningEditors.Single(e => e.Key == "invert").IsOn = true;
        Assert.Equal(-0.328427, vm.AxisTuningCurve[150].Y, 5);
        Assert.Equal(calls, fake.Calls.Count);

        // DCS inherits the base (curvature 0.3, dead zone 0.05); the base is drawn dashed.
        await SelectScopeAsync(vm, "game:DCS");
        Assert.Equal("Inherited from All games (base)", vm.AxisTuningStatus);
        Assert.Equal(0.1646, vm.AxisTuningCurve[150].Y, 4);
        Assert.NotNull(vm.AxisTuningReferenceCurve);
        Assert.Equal(0.1646, vm.AxisTuningReferenceCurve![150].Y, 4);
        Assert.False(vm.CanClearAxisTuning);
        Assert.False(vm.AxisTuningHasEdits);
        Assert.Equal(["deadzone", "saturationX", "saturationY", "curvature", "userCurve", "slider", "invert"], vm.AxisTuningEditors.Select(e => e.Key).ToArray());

        // The user curve starts from the curvature and replaces it.
        AxisParameterEditor userCurve = vm.AxisTuningEditors.Single(e => e.IsUserCurve);
        userCurve.IsOn = true;
        Assert.False(vm.AxisTuningEditors.Single(e => e.Key == "curvature").IsActive);
        Assert.Equal(0.1646, vm.AxisTuningCurve[150].Y, 2);
        foreach (AxisCurvePointEditor point in userCurve.Points)
            point.Value = point.Position;
        Assert.Equal((0.5 - 0.05) / 0.95, vm.AxisTuningCurve[150].Y, 4);

        // Korea without own values: the base fitted to its model.
        fake.Scopes.Remove("game:IL2Korea");
        await SelectScopeAsync(vm, "game:IL2Korea");
        Assert.Equal("Inherited from All games (base) — fitted to this game's model", vm.AxisTuningStatus);
        AxisTuning fitted = AxisModels.Inherit(AxisTuning.Parse(fake.Scopes["base"]), AxisModels.Get(AxisModels.Il2Korea));
        Assert.Equal(fitted.Native["sensitivity"], vm.AxisTuningEditors.Single(e => e.Key == "sensitivity").Value);
    }

    [Fact]
    public async Task InvalidNumbers_ShowAMessage_AndAreNotSent()
    {
        AxisTuningFakeEngine fake = new();
        MainWindowViewModel vm = await CreateLoadedVmAsync(fake);
        await SelectScopeAsync(vm, "game:IL2Korea");
        IReadOnlyList<Avalonia.Point> before = vm.AxisTuningCurve;

        AxisParameterEditor sensitivity = vm.AxisTuningEditors.Single(e => e.Key == "sensitivity");
        sensitivity.Text = "abc";
        Assert.True(sensitivity.HasValidationMessage);
        Assert.Contains("Exponent", vm.AxisTuningValidation);
        Assert.False(vm.CanSaveAxisTuning);
        Assert.Same(before, vm.AxisTuningCurve);

        sensitivity.Text = "101";
        Assert.Contains("0 .. 100", vm.AxisTuningValidation);

        fake.Calls.Clear();
        await vm.SaveAxisTuningCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fake.Calls, c => Seq(c, "binds", "tuning", "set"));

        sensitivity.Text = "22";
        Assert.Equal(string.Empty, vm.AxisTuningValidation);
        Assert.True(vm.CanSaveAxisTuning);
    }

    [Fact]
    public async Task Save_SendsTheModelsValues_AndInheritClears()
    {
        AxisTuningFakeEngine fake = new();
        MainWindowViewModel vm = await CreateLoadedVmAsync(fake);
        await SelectScopeAsync(vm, "game:IL2Korea");
        vm.AxisTuningEditors.Single(e => e.Key == "sensitivity").Text = "22";
        vm.AxisTuningEditors.Single(e => e.Key == "deadZone0").Value = 4;

        fake.Calls.Clear();
        await vm.SaveAxisTuningCommand.ExecuteAsync(null);
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => Seq(c, "binds", "tuning", "set"));
        Assert.True(Seq(set, "binds", "tuning", "set", "Pitch", "--scope", "game:IL2Korea"));
        Assert.True(Seq(set, "--param", "sensitivity=22"));
        Assert.True(Seq(set, "--param", "deadZone0=4"));
        Assert.True(Seq(set, "--param", "deadZone1=0"));
        Assert.True(Seq(set, "--param", "offset=0"));
        Assert.True(Seq(set, "--inverted", "false"));
        Assert.DoesNotContain("--deadzone", set);
        Assert.DoesNotContain("--curvature", set);
        Assert.Contains("--profile", set);
        // Reloaded afterwards.
        Assert.Contains(fake.Calls, c => Seq(c, "binds", "tuning", "show", "Pitch", "--game", "IL2Korea"));

        fake.Calls.Clear();
        await vm.ClearAxisTuningCommand.ExecuteAsync(null);
        IReadOnlyList<string> clear = Assert.Single(fake.Calls, c => Seq(c, "binds", "tuning", "clear"));
        Assert.True(Seq(clear, "binds", "tuning", "clear", "Pitch", "--scope", "game:IL2Korea"));
        Assert.Contains("--profile", clear);
        Assert.StartsWith("Inherited from All games (base)", vm.AxisTuningStatus);
        Assert.False(vm.CanClearAxisTuning);

        // A DCS aircraft: the DCS options, a user curve as 11 points.
        await SelectScopeAsync(vm, "plane:DCS:F-16C_50");
        Assert.Contains(fake.Calls, c => Seq(c, "binds", "tuning", "show", "Pitch", "--game", "DCS", "--plane", "F-16C_50"));
        vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text = "0.1";
        vm.AxisTuningEditors.Single(e => e.Key == "slider").IsOn = true;
        fake.Calls.Clear();
        await vm.SaveAxisTuningCommand.ExecuteAsync(null);
        set = Assert.Single(fake.Calls, c => Seq(c, "binds", "tuning", "set"));
        Assert.True(Seq(set, "--scope", "plane:DCS:F-16C_50"));
        Assert.True(Seq(set, "--deadzone", "0.1"));
        Assert.True(Seq(set, "--saturation-x", "1"));
        Assert.True(Seq(set, "--saturation-y", "1"));
        Assert.True(Seq(set, "--curvature", "0.3"));
        Assert.True(Seq(set, "--slider", "true"));
        Assert.True(Seq(set, "--inverted", "false"));
        Assert.DoesNotContain("--param", set);

        AxisParameterEditor userCurve = vm.AxisTuningEditors.Single(e => e.IsUserCurve);
        userCurve.IsOn = true;
        fake.Calls.Clear();
        await vm.SaveAxisTuningCommand.ExecuteAsync(null);
        set = Assert.Single(fake.Calls, c => Seq(c, "binds", "tuning", "set"));
        int curvature = set.ToList().IndexOf("--curvature");
        Assert.True(curvature > 0);
        Assert.Equal(11, set[curvature + 1].Split(',').Length);
        Assert.Single(set, a => a == "--curvature");

        // Saving the base keeps Set Bind's invert / slider in step.
        await SelectScopeAsync(vm, "base");
        vm.AxisTuningEditors.Single(e => e.Key == "invert").IsOn = true;
        await vm.SaveAxisTuningCommand.ExecuteAsync(null);
        Assert.True(vm.BindInverted);
    }

    // X-Plane's curve with free stops: the table, dragging on the preview, the engine's validation and --curve-points.
    [Fact]
    public async Task CurvePoints_EditDragValidateAndSave()
    {
        AxisTuningFakeEngine fake = new() { IncludeXPlane = true };
        MainWindowViewModel vm = await CreateLoadedVmAsync(fake);
        Assert.False(vm.AxisTuningSupportsControlPoints);
        Assert.Contains(vm.AxisTuningScopes, s => s.Scope == "game:XPlane12");

        // Inherited from the base: X-Plane gets the base as stops (fitted), shown as curve points.
        await SelectScopeAsync(vm, "game:XPlane12");
        Assert.True(vm.AxisTuningSupportsControlPoints);
        Assert.True(vm.AxisTuningUseControlPoints);
        Assert.True(vm.AxisTuningControlPoints.Count >= 2);
        Assert.False(vm.AxisTuningEditors.Single(e => e.Key == "deadzone").IsActive);
        Assert.False(vm.AxisTuningEditors.Single(e => e.Key == "curvature").IsActive);
        Assert.NotNull(vm.AxisTuningHandles);
        Assert.Equal(vm.AxisTuningControlPoints.Count, vm.AxisTuningHandles!.Count);

        // Own stops typed into the table: (0,0) (0.5,0.2) (1,1).
        vm.AxisTuningUseControlPoints = false;
        Assert.True(vm.AxisTuningEditors.Single(e => e.Key == "deadzone").IsActive);
        while (vm.AxisTuningControlPoints.Count > 0)
            vm.AxisTuningControlPoints.RemoveAt(0);
        vm.AxisTuningEditors.Single(e => e.Key == "deadzone").Text = "0";
        vm.AxisTuningEditors.Single(e => e.Key == "curvature").Text = "0";
        vm.AxisTuningUseControlPoints = true;
        Assert.Equal([0.0, 1.0], vm.AxisTuningControlPoints.Select(p => p.X).ToArray());
        vm.AddAxisTuningControlPointCommand.Execute(null);
        Assert.Equal([0.0, 0.5, 1.0], vm.AxisTuningControlPoints.Select(p => p.X).ToArray());
        vm.AxisTuningControlPoints[1].YText = "0.2";
        Assert.Equal(0.2, vm.AxisTuningCurve[150].Y, 6);
        Assert.Equal(3, vm.AxisTuningHandles!.Count);
        Assert.Equal(0.5, vm.AxisTuningHandles[1].X, 6);
        Assert.Equal(0.2, vm.AxisTuningHandles[1].Y, 6);

        // The engine's rules: 0..1, x increasing.
        vm.AxisTuningControlPoints[1].XText = "1.5";
        Assert.Contains("0..1", vm.AxisTuningValidation);
        Assert.False(vm.CanSaveAxisTuning);
        vm.AxisTuningControlPoints[1].XText = "0";
        Assert.Contains("x must increase", vm.AxisTuningValidation);
        Assert.False(vm.CanSaveAxisTuning);
        vm.AxisTuningControlPoints[1].XText = "0.5";
        Assert.Equal(string.Empty, vm.AxisTuningValidation);

        // Dragging the middle handle moves the stop (between its neighbours) and its output.
        vm.DragAxisTuningHandleCommand.Execute(new AxisCurveHandleMove(1, 0.6, 0.7));
        Assert.Equal(0.6, vm.AxisTuningControlPoints[1].X, 6);
        Assert.Equal(0.7, vm.AxisTuningControlPoints[1].Y, 6);
        Assert.Equal("0.6", vm.AxisTuningControlPoints[1].XText);
        vm.DragAxisTuningHandleCommand.Execute(new AxisCurveHandleMove(1, 1.0, 0.7));
        Assert.Equal(0.995, vm.AxisTuningControlPoints[1].X, 6);
        vm.DragAxisTuningHandleCommand.Execute(new AxisCurveHandleMove(1, 0.6, 0.7));

        // At least two stops stay.
        vm.RemoveAxisTuningControlPointCommand.Execute(vm.AxisTuningControlPoints[1]);
        vm.RemoveAxisTuningControlPointCommand.Execute(vm.AxisTuningControlPoints[1]);
        Assert.Equal(2, vm.AxisTuningControlPoints.Count);
        vm.AddAxisTuningControlPointCommand.Execute(null);
        vm.DragAxisTuningHandleCommand.Execute(new AxisCurveHandleMove(1, 0.6, 0.7));

        fake.Calls.Clear();
        await vm.SaveAxisTuningCommand.ExecuteAsync(null);
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => Seq(c, "binds", "tuning", "set"));
        Assert.True(Seq(set, "--scope", "game:XPlane12"));
        Assert.True(Seq(set, "--curve-points", "0:0,0.6:0.7,1:1"));

        // Switching the stops off drops them in the engine as well.
        fake.Scopes["game:XPlane12"] = "inv=0;sl=0;dz=0;sx=1;sy=1;cv=0;n.cpN=3;n.cpx0=0;n.cpx1=0.6;n.cpx2=1;n.cpy0=0;n.cpy1=0.7;n.cpy2=1";
        await vm.ReloadAxisTuningCommand.ExecuteAsync(null);
        Assert.True(vm.AxisTuningUseControlPoints);
        Assert.Equal(3, vm.AxisTuningControlPoints.Count);
        vm.AxisTuningUseControlPoints = false;
        fake.Calls.Clear();
        await vm.SaveAxisTuningCommand.ExecuteAsync(null);
        set = Assert.Single(fake.Calls, c => Seq(c, "binds", "tuning", "set"));
        Assert.True(Seq(set, "--curve-points", "none"));

        // DCS has no free stops: no table, no --curve-points.
        await SelectScopeAsync(vm, "game:DCS");
        Assert.False(vm.AxisTuningSupportsControlPoints);
        fake.Calls.Clear();
        await vm.SaveAxisTuningCommand.ExecuteAsync(null);
        Assert.DoesNotContain("--curve-points", Assert.Single(fake.Calls, c => Seq(c, "binds", "tuning", "set")));
    }

    // The 11 user-curve points can be dragged on the preview: the output at each point follows the mouse.
    [Fact]
    public async Task UserCurve_HandlesFollowTheDrag()
    {
        AxisTuningFakeEngine fake = new();
        MainWindowViewModel vm = await CreateLoadedVmAsync(fake);
        await SelectScopeAsync(vm, "plane:DCS:F-16C_50");
        Assert.Null(vm.AxisTuningHandles);

        AxisParameterEditor userCurve = vm.AxisTuningEditors.Single(e => e.IsUserCurve);
        userCurve.IsOn = true;
        Assert.NotNull(vm.AxisTuningHandles);
        Assert.Equal(11, vm.AxisTuningHandles!.Count);
        // Spread between dead zone (0.05) and saturation (1).
        Assert.Equal(0.05 + 0.95 * 0.5, vm.AxisTuningHandles[5].X, 6);
        Assert.Equal(userCurve.Points[5].Value, vm.AxisTuningHandles[5].Y, 4);

        int calls = fake.Calls.Count;
        vm.DragAxisTuningHandleCommand.Execute(new AxisCurveHandleMove(5, 0.2, 0.9));
        Assert.Equal(0.9, userCurve.Points[5].Value, 3);
        Assert.Equal(0.9, vm.AxisTuningHandles![5].Y, 3);
        vm.DragAxisTuningHandleCommand.Execute(new AxisCurveHandleMove(5, 0.2, 1.4));
        Assert.Equal(1.0, userCurve.Points[5].Value, 3);
        Assert.True(vm.AxisTuningHasEdits);
        // Only the preview changes; nothing is stored until Save.
        Assert.Equal(calls, fake.Calls.Count);

        // Reversed: the curve and its handles flip; dragging still sets the point's output.
        vm.AxisTuningEditors.Single(e => e.Key == "invert").IsOn = true;
        Assert.True(vm.AxisTuningHandles![5].X * vm.AxisTuningHandles[5].Y < 0 || vm.AxisTuningHandles[5].Y < 0);
        vm.DragAxisTuningHandleCommand.Execute(new AxisCurveHandleMove(5, vm.AxisTuningHandles[5].X, -0.4));
        Assert.Equal(0.4, userCurve.Points[5].Value, 3);
    }

    [Fact]
    public async Task LiveStick_FollowsTheBoundAxis_WhileExpanded()
    {
        AxisTuningFakeEngine fake = new();
        AxisTuningFakeCapture capture = new();
        MainWindowViewModel vm = await CreateLoadedVmAsync(fake, capture);
        vm.CurrentStage = UiStage.Binds;
        await SelectScopeAsync(vm, "game:DCS");
        Assert.True(capture.MonitoringAxes);

        // JOY_Y = axis 1; other devices are ignored.
        vm.OnAxisTuningAxes(new JoystickAxisSnapshot("Other {00000000-0000-0000-0000-000000000999}", [0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.True(double.IsNaN(vm.AxisTuningStickInput));
        vm.OnAxisTuningAxes(new JoystickAxisSnapshot(Stick, [0.1, 0.75, 0, 0, 0, 0, 0, 0]));
        Assert.Equal(0.5, vm.AxisTuningStickInput, 9);
        Assert.Equal(0.1646, vm.AxisTuningStickOutput, 4);
        Assert.Equal("in 0.50 -> out 0.16", vm.AxisTuningLiveText);

        // Edits move the output at once.
        vm.AxisTuningEditors.Single(e => e.Key == "curvature").Value = 0;
        Assert.Equal((0.5 - 0.05) / 0.95, vm.AxisTuningStickOutput, 4);

        // The Binds page's live input monitor (follow input) feeds the curve: one DirectInput poller, not two.
        vm.OnLiveAxes(new JoystickAxisSnapshot(Stick, [0.1, 0.25, 0, 0, 0, 0, 0, 0]));
        Assert.Equal(-0.5, vm.AxisTuningStickInput, 9);

        vm.IsAxisTuningExpanded = false;
        Assert.False(vm.IsAxisTuningMonitoring);
        Assert.True(double.IsNaN(vm.AxisTuningStickInput));

        // Without follow input the curve has its own monitor, stopped when collapsed.
        vm.FollowInputOnBinds = false;
        vm.IsAxisTuningExpanded = true;
        Assert.True(vm.IsAxisTuningMonitoring);
        Assert.False(capture.LastToken.IsCancellationRequested);
        vm.IsAxisTuningExpanded = false;
        Assert.True(capture.LastToken.IsCancellationRequested);
    }

    internal static async Task<MainWindowViewModel> CreateLoadedVmAsync(AxisTuningFakeEngine fake, AxisTuningFakeCapture? capture = null)
    {
        MainWindowViewModel vm = new(fake, capture ?? new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
        vm.EnginePath = typeof(MainWindowViewModel).Assembly.Location;
        string tempRoot = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests");
        Directory.CreateDirectory(tempRoot);
        vm.ProfilePath = Path.Combine(tempRoot, "axis-tuning.pr0file");
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Pitch");
        Assert.True(vm.SelectedBindIsAxis);
        vm.IsAxisTuningExpanded = true;
        await WaitForAsync(() => vm.AxisTuningLoaded);
        return vm;
    }

    internal static async Task SelectScopeAsync(MainWindowViewModel vm, string scope)
    {
        vm.SelectedAxisTuningScope = vm.AxisTuningScopes.Single(s => s.Scope == scope);
        await WaitForAsync(() => vm.AxisTuningLoaded && vm.SelectedAxisTuningScope?.Scope == scope);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "Timed out waiting for the axis tuning to load.");
    }

    internal static bool Seq(IReadOnlyList<string> args, params string[] sequence)
    {
        for (int i = 0; i <= args.Count - sequence.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < sequence.Length && ok; j++)
                ok = string.Equals(args[i + j], sequence[j], StringComparison.OrdinalIgnoreCase);
            if (ok)
                return true;
        }
        return false;
    }
}

// One axis relation "Pitch" bound to JOY_Y of a stick; DCS (A-10C, F-16C_50) and IL-2 Korea nodes. Scopes are the
// stored tunings as the engine reports them; show resolves like engine/BindTuning.cs.
[SupportedOSPlatform("windows")]
internal sealed class AxisTuningFakeEngine : IEngineCliClient
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

    // Adds an X-Plane node (a game curve with free control points).
    public bool IncludeXPlane { get; set; }

    public Dictionary<string, string> Scopes { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["base"] = "inv=0;sl=0;dz=0.05;sx=1;sy=1;cv=0.3",
        ["game:IL2Korea"] = "inv=0;sl=0;dz=0;sx=1;sy=1;cv=0;n.deadZone0=0;n.deadZone1=0;n.offset=0;n.sensitivity=50"
    };

    public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add(arguments.ToArray());
        return Task.FromResult(new EngineCommandResult
        {
            ExitCode = 0,
            Duration = TimeSpan.FromMilliseconds(1),
            StandardOutput = BuildOutput(arguments),
            Arguments = arguments.ToArray()
        });
    }

    private string BuildOutput(IReadOnlyList<string> args)
    {
        if (AxisTuningTests.Seq(args, "relations", "list"))
        {
            return "{\"Relations\":[{\"Name\":\"Pitch\",\"IsAxis\":true,\"Groups\":[\"Flight\"],\"Nodes\":[" +
                   "{\"Id\":\"a2001cdnil\",\"Game\":\"DCS\",\"Aircraft\":{\"A-10C\":true,\"F-16C_50\":true}}," +
                   "{\"Id\":\"pitch\",\"Game\":\"IL2Korea\",\"Aircraft\":{\"IL2Korea\":true}}]," +
                   "\"Bind\":{\"Joystick\":\"" + AxisTuningTests.Stick + "\",\"JAxis\":\"JOY_Y\",\"JButton\":\"\"}}]}";
        }
        if (AxisTuningTests.Seq(args, "relations", "show"))
        {
            return string.Join(Environment.NewLine,
                "Relation: Pitch",
                "Nodes:",
                "  [1] game=DCS, id=a2001cdnil, aircraft=F-16C_50,A-10C",
                "  [2] game=IL2Korea, id=pitch, aircraft=IL2Korea") +
                (IncludeXPlane ? Environment.NewLine + "  [3] game=XPlane12, id=sim/joystick/yoke_pitch_ratio, aircraft=XPlane12" : string.Empty);
        }
        if (AxisTuningTests.Seq(args, "binds", "list"))
        {
            return "{\"Binds\":[{\"RelationName\":\"Pitch\",\"IsAxis\":true,\"Joystick\":\"" + AxisTuningTests.Stick + "\",\"JAxis\":\"JOY_Y\",\"JButton\":\"\"," +
                   "\"Inverted\":false,\"Slider\":false,\"Deadzone\":0.05,\"Curvature\":[0.3],\"SaturationX\":1,\"SaturationY\":1,\"AllReformers\":[],\"DoubleTap\":false}]}";
        }
        if (AxisTuningTests.Seq(args, "binds", "tuning", "clear"))
        {
            Scopes.Remove(Option(args, "--scope"));
            return "{\"Relation\":\"Pitch\",\"Message\":\"Cleared.\"}";
        }
        if (AxisTuningTests.Seq(args, "binds", "tuning", "set"))
            return "{\"Relation\":\"Pitch\",\"Message\":\"Set.\"}";
        if (AxisTuningTests.Seq(args, "binds", "tuning", "show"))
            return Show(Option(args, "--game"), Option(args, "--plane"));
        return "ok";
    }

    private string Show(string game, string plane)
    {
        string resolved = "null";
        if (game.Length > 0)
        {
            AxisModel model = AxisModels.ForGame(game);
            List<string> order = [];
            if (plane.Length > 0)
                order.Add("plane:" + game + ":" + plane);
            order.Add("game:" + game);
            if (game.StartsWith("IL2", StringComparison.OrdinalIgnoreCase))
                order.Add("family:il2");
            string? source = order.FirstOrDefault(Scopes.ContainsKey);
            bool fitted = source == null && model.Fit != null;
            string tuning = source != null ? Scopes[source]
                : fitted ? AxisModels.Inherit(AxisTuning.Parse(Scopes["base"]), model).Format()
                : Scopes["base"];
            resolved = "{\"Model\":\"" + model.Id + "\",\"Source\":\"" + (source ?? "base") + "\",\"Fitted\":" + (fitted ? "true" : "false") + ",\"Tuning\":\"" + tuning + "\"}";
        }
        string scopes = string.Join(",", Scopes.Select(s => "\"" + s.Key + "\":\"" + s.Value + "\""));
        return "{\"Relation\":\"Pitch\",\"Scopes\":{" + scopes + "},\"Game\":" + (game.Length > 0 ? "\"" + game + "\"" : "null") +
               ",\"Plane\":" + (plane.Length > 0 ? "\"" + plane + "\"" : "null") + ",\"Resolved\":" + resolved + "}";
    }

    private static string Option(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return string.Empty;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class AxisTuningFakeCapture : IJoystickCaptureService
{
    public string LastDiagnostics => string.Empty;

    public bool MonitoringAxes { get; private set; }

    public CancellationToken LastToken { get; private set; }

    public Task<JoystickCaptureService.CaptureResult?> CaptureNextAsync(JoystickCaptureService.CaptureOptions options, CancellationToken cancellationToken) =>
        Task.FromResult<JoystickCaptureService.CaptureResult?>(null);

    public Task MonitorAsync(Action<JoystickInputEvent> onEvent, Action<JoystickAxisSnapshot>? onAxes, CancellationToken cancellationToken)
    {
        MonitoringAxes = onAxes != null;
        LastToken = cancellationToken;
        return Task.CompletedTask;
    }
}
