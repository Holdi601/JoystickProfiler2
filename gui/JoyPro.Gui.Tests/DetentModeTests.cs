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

// Detent mode (engine\AxisDetents.cs, linked into the GUI): the curve math per game model and the Binds page's detent
// panel, calibration wizard and merge helper against a fake engine.
[SupportedOSPlatform("windows")]
public sealed class DetentModeTests
{
    internal const string Throttle = "Test Throttle {00000000-0000-0000-0000-0000000000D1}";

    private static readonly AxisModel Dcs = AxisModels.Get(AxisModels.Dcs);

    // The user's March 2025 profile: a slider, inverted, with a small idle zone (0.1 travel -> 2 %), linear above.
    private static AxisTuning UserLinear() => AxisTuning.Parse("inv=1;sl=1;dz=0;sx=1;sy=1;cv=0|0.02|0.12|0.23|0.34|0.45|0.56|0.67|0.78|0.89|1");

    // The same profile's relation for aircraft without afterburner: full power from about 78 % of the travel on.
    private static AxisTuning UserNoAb() => AxisTuning.Parse("inv=1;sl=1;dz=0;sx=1;sy=1;cv=0|0.02|0.13|0.26|0.41|0.58|0.76|0.94|1|1|1");

    private static AxisTuning Linear(bool inverted = true) => new() { Slider = true, Inverted = inverted };

    // DCS thrust: -1 is full (the user's VPC throttle rises towards full and is inverted in DCS).
    private static double DcsThrottle(AxisTuning t, double raw) => (1.0 - Dcs.Evaluate(t, 2.0 * raw - 1.0)) / 2.0;

    private static DetentCalibration Calibration(double idle = 0.0, double detent = 0.8) => new() { Idle = idle, Detent = detent };

    [Fact]
    public void Calibration_FormatParseValidate()
    {
        DetentCalibration c = new() { Idle = 0.05, Detent = 0.8, Full = 0.99, Cutoff = 0.0, Source = "wizard", When = "2026-10-17" };
        Assert.Equal("idle=0.05;det=0.8;full=0.99;cut=0;src=wizard;at=2026-10-17", c.Format());
        DetentCalibration back = DetentCalibration.Parse(c.Format())!;
        Assert.Equal(0.8, back.Detent);
        Assert.Equal(0.0, back.Cutoff);
        Assert.True(back.HasCutoff);
        Assert.True(back.Increasing);
        Assert.Equal(string.Empty, back.Validate());
        Assert.Null(DetentCalibration.Parse("det=0.8"));
        Assert.Contains("2 %", new DetentCalibration { Idle = 0.5, Detent = 0.51 }.Validate());
        Assert.Contains("beyond the detent", new DetentCalibration { Idle = 0.1, Detent = 0.8, Full = 0.7 }.Validate());
        Assert.Contains("behind idle", new DetentCalibration { Idle = 0.1, Detent = 0.8, Cutoff = 0.2 }.Validate());
        // A reversed axis (the value falls towards full): travel runs from 1 to 0.
        DetentCalibration reversed = new() { Idle = 1.0, Detent = 0.25 };
        Assert.False(reversed.Increasing);
        Assert.Equal(0.75, reversed.Travel(reversed.Detent), 9);
        Assert.Equal(0.0, reversed.FullOrEnd);
    }

    [Fact]
    public void TargetsAndSettings_FormatParse()
    {
        Assert.Equal("ab:0.78", DetentTarget.Parse("ab:0.78")!.Format());
        Assert.Equal("ab:0.8/idle:0.05", DetentTarget.Parse("AB:0.8/idle:0.05")!.Format());
        Assert.Equal(DetentTargetKind.FullAtDetent, DetentTarget.Parse("full")!.Kind);
        Assert.Equal(DetentTargetKind.Linear, DetentTarget.Parse("linear")!.Kind);
        Assert.Null(DetentTarget.Parse("ab:1.5"));
        Assert.Null(DetentTarget.Parse("ab:0.3/idle:0.4"));
        Assert.Equal("afterburner at 78 %", DetentTarget.Afterburner(0.78).Describe());

        DetentSettings s = new() { NoAfterburner = DetentTargetKind.Linear, Margin = 0.02 };
        s.Targets[DetentSettings.TargetKey("DCS", "F-16C_50")] = "ab:0.79";
        s.Targets[DetentSettings.TargetKey("DCS", "OH-58D Copilot")] = "full";
        string text = s.Format();
        Assert.Equal("on=1;noab=linear;unknown=linear;margin=0.02;t.DCS:F-16C_50=ab:0.79;t.DCS:OH-58D Copilot=full", text);
        DetentSettings back = DetentSettings.Parse(text)!;
        Assert.Equal(0.79, back.OwnTarget("DCS", "F-16C_50")!.AbStart);
        Assert.Equal(DetentTargetKind.FullAtDetent, back.OwnTarget("dcs", "oh-58d copilot")!.Kind);
        Assert.Equal(text, back.Format());
    }

    // The bend: the device's detent gives the afterburner start minus the margin, full stays full, idle stays idle.
    [Fact]
    public void WantedThrottle_PutsTheDetentOnTheAfterburnerStart()
    {
        AxisTuning baseTuning = Linear();
        DetentCalibration cal = Calibration(detent: 0.7);
        Func<double, double> g = DetentMath.WantedThrottle(Dcs, baseTuning, cal, DetentTarget.Afterburner(0.8), 0.01);
        Assert.Equal(0.0, g(0.0), 9);
        Assert.Equal(0.79, g(0.7), 9);
        Assert.Equal(1.0, g(1.0), 9);
        Assert.Equal(0.79 / 2, g(0.35), 9);
        Assert.Equal(0.79 + 0.21 * 0.5, g(0.85), 9);

        // Full at the detent: everything beyond it stays full.
        Func<double, double> full = DetentMath.WantedThrottle(Dcs, baseTuning, cal, DetentTarget.Full, 0.01);
        Assert.Equal(1.0, full(0.7), 9);
        Assert.Equal(1.0, full(0.9), 9);
        Assert.Equal(0.5, full(0.35), 9);
    }

    // The user's base curve is kept inside each segment (scaled), not replaced by a straight line.
    [Fact]
    public void WantedThrottle_ScalesTheUsersCurve()
    {
        AxisTuning baseTuning = UserLinear();
        DetentCalibration cal = Calibration(detent: 0.8);
        Func<double, double> g = DetentMath.WantedThrottle(Dcs, baseTuning, cal, DetentTarget.Afterburner(0.61), 0.01);
        double baseAtDetent = DcsThrottle(baseTuning, 0.8);
        Assert.Equal(0.78, baseAtDetent, 6);
        Assert.Equal(0.60, g(0.8), 9);
        // The idle zone (2 % at 10 % travel) scales with the segment.
        Assert.Equal(0.02 * 0.60 / 0.78, g(0.1), 6);
    }

    // DCS: full at the detent from a straight base is exact with dead zone + saturation (no user curve needed).
    [Fact]
    public void DcsFit_FullAtDetent_IsDeadZoneAndSaturation()
    {
        DetentFit fit = DetentMath.Fit(Dcs, Linear(), Calibration(detent: 0.78), DetentTarget.Full, 0.01);
        Assert.Equal("dead zone + saturation", fit.Method);
        Assert.True(fit.MaxError < DetentMath.Exact, "error " + fit.MaxError);
        Assert.True(fit.Tuning.Slider);
        Assert.Equal(0.78, fit.Tuning.SaturationX, 3);
        Assert.Equal(1.0, DcsThrottle(fit.Tuning, 0.78), 3);
        Assert.Equal(1.0, DcsThrottle(fit.Tuning, 0.95), 6);
        Assert.Equal(string.Empty, fit.Warning);
    }

    // DCS: an afterburner start other than the detent needs the 11-point user curve; it passes exactly through the
    // detent point (resting in the detent = just below the afterburner) and stays close everywhere else.
    [Theory]
    [InlineData(0.8, 0.78, true)]
    [InlineData(0.8, 0.61, true)]
    [InlineData(0.777, 0.92, true)]
    [InlineData(0.7, 0.88, false)]
    public void DcsFit_Afterburner_UserCurveExactAtTheDetent(double detent, double ab, bool inverted)
    {
        DetentCalibration cal = Calibration(detent: detent);
        DetentFit fit = DetentMath.Fit(Dcs, Linear(inverted), cal, DetentTarget.Afterburner(ab), 0.01);
        Assert.Equal("user curve", fit.Method);
        Assert.Equal(11, fit.Tuning.Curvature.Count);
        Assert.True(fit.MaxError < DetentMath.Acceptable, "error " + fit.MaxError);
        AxisTuning t = fit.Tuning;
        double sigma;
        Func<double, double> g = DetentMath.BaseThrottle(Dcs, t, cal, out sigma);
        Assert.Equal(ab - 0.01, g(detent), 3);
        Assert.Equal(1.0, g(1.0), 3);
        Assert.True(g(0.0) < 0.005);
        // Rising over the whole travel.
        for (int i = 1; i <= 100; i++)
            Assert.True(g(i / 100.0) >= g((i - 1) / 100.0) - 0.002, "falls at " + i);
    }

    // A reversed device axis (the value falls towards full) with a non-inverted relation: same result in travel.
    [Fact]
    public void DcsFit_ReversedAxis()
    {
        DetentCalibration cal = new() { Idle = 1.0, Detent = 0.2 };
        AxisTuning baseTuning = Linear(inverted: false);
        DetentFit fit = DetentMath.Fit(Dcs, baseTuning, cal, DetentTarget.Afterburner(0.85), 0.01);
        Assert.True(fit.MaxError < DetentMath.Acceptable, "error " + fit.MaxError);
        double travelAtDetent = DcsThrottle(fit.Tuning, 0.2);
        Assert.Equal(0.84, travelAtDetent, 3);
        Assert.True(DcsThrottle(fit.Tuning, 1.0) < 0.005);
        Assert.True(DcsThrottle(fit.Tuning, 0.0) > 0.995);
    }

    // 2.7.1 report "does detent mode respect the deadzone?": the bend is applied on top of the base curve as the game
    // evaluates it, so the dead zone stays at the idle end of the travel (exactly zero throttle there, not a spline that
    // wobbles around it) and the device's detent still gives the afterburner start minus the margin.
    [Theory]
    [InlineData(0.08, 0.7, 0.8, true)]
    [InlineData(0.04, 0.708, 0.71, true)]
    [InlineData(0.15, 0.75, 0.9, false)]
    public void DcsFit_DeadZoneStaysFlatAtIdle(double deadzone, double detent, double ab, bool inverted)
    {
        AxisTuning baseTuning = Linear(inverted);
        baseTuning.Deadzone = deadzone;
        DetentCalibration cal = Calibration(detent: detent);
        foreach (DetentTarget target in new[] { DetentTarget.Afterburner(ab), DetentTarget.Full })
        {
            DetentFit fit = DetentMath.Fit(Dcs, baseTuning, cal, target, 0.01);
            Assert.True(fit.MaxError < DetentMath.Acceptable, target.Describe() + ": error " + fit.MaxError);
            AxisTuning t = fit.Tuning;
            Assert.Equal(deadzone, t.Deadzone, 3);
            double sigma;
            Func<double, double> g = DetentMath.BaseThrottle(Dcs, t, cal, out sigma);
            for (double u = 0; u <= deadzone - 0.002; u += 0.002)
                Assert.Equal(0.0, g(u), 9);
            Assert.True(g(deadzone + 0.02) > 0.0, target.Describe() + ": the throttle should move right after the dead zone");
            double atDetent = target.Kind == DetentTargetKind.Afterburner ? ab - 0.01 : 1.0;
            Assert.Equal(atDetent, g(detent), 3);
            Assert.Equal(1.0, g(1.0), 3);
            for (int i = 1; i <= 200; i++)
                Assert.True(g(i / 200.0) >= g((i - 1) / 200.0) - 0.002, target.Describe() + ": falls at " + i);
        }
    }

    // The tester's VPC throttle (cut-off 0.1 %, idle detent 4.3 %, afterburner detent 70.8 %, full 99.9 %) with a dead
    // zone of 4 % on the default curve: 2.7.1 dropped the bend for the F/A-18C (DCS "cannot express the detent curve":
    // the dead zone squeezed the cut-off ramp to 0.3 % of the travel). The calibrated idle detent always gives the
    // aircraft's idle; a dead zone reaching past it keeps the throttle at idle up to its end, never at the cut-off.
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.04)]
    [InlineData(0.08)]
    public void DcsFit_DeadZoneWithCutoff_IdleDetentGivesIdle(double deadzone)
    {
        AxisTuning baseTuning = Linear();
        baseTuning.Deadzone = deadzone;
        DetentCalibration cal = new() { Cutoff = 0.001, Idle = 0.043, Detent = 0.708, Full = 0.999 };
        DetentTarget hornet = DetentTarget.Afterburner(0.77, idleGate: 0.1);
        Func<double, double> wanted = DetentMath.WantedThrottle(Dcs, baseTuning, cal, hornet, 0.01);
        Assert.Equal(0.0, wanted(0.001), 9);
        Assert.Equal(0.1, wanted(0.043), 9);
        Assert.Equal(0.76, wanted(0.708), 9);
        Assert.Equal(1.0, wanted(0.999), 9);
        if (deadzone > cal.Idle)
            Assert.Equal(0.1, wanted(deadzone - 0.005), 9);

        DetentFit fit = DetentMath.Fit(Dcs, baseTuning, cal, hornet, 0.01);
        Assert.False(fit.Fallback, fit.Warning);
        Assert.True(fit.MaxError < DetentMath.Acceptable, "error " + fit.MaxError);
        double sigma;
        Func<double, double> g = DetentMath.BaseThrottle(Dcs, fit.Tuning, cal, out sigma);
        // Resting in the idle detent (and a little jitter behind it) gives the aircraft's idle, never the cut-off; over
        // the idle detent towards the cut-off it is zero.
        Assert.InRange(g(0.043), 0.1, 0.11);
        Assert.InRange(g(0.04), 0.1, 0.11);
        Assert.Equal(0.0, g(0.03), 9);
        Assert.Equal(0.0, g(0.001), 9);
        Assert.Equal(0.76, g(0.708), 3);
        Assert.True(g(0.999) > 0.995);
        Assert.Equal(1.0, g(1.0), 3);
        for (int i = 9; i <= 200; i++)
            Assert.True(g(i / 200.0) >= g((i - 1) / 200.0) - 0.002, "falls at " + i);
    }

    // An idle cut-off behind the device's idle detent: flat zero there; an aircraft with its cut-off on the axis gets
    // its idle point at the device's idle detent.
    [Fact]
    public void Cutoff_FlatBelowIdleDetent_IdleGateAtTheIdleDetent()
    {
        DetentCalibration cal = new() { Cutoff = 0.0, Idle = 0.12, Detent = 0.8 };
        Func<double, double> g = DetentMath.WantedThrottle(Dcs, Linear(), cal, DetentTarget.Afterburner(0.8), 0.0);
        Assert.Equal(0.0, g(0.06), 9);
        Assert.Equal(0.0, g(0.12), 9);
        Assert.Equal(0.8, g(0.8), 9);
        Func<double, double> gated = DetentMath.WantedThrottle(Dcs, Linear(), cal, DetentTarget.Afterburner(0.8, idleGate: 0.1), 0.0);
        Assert.Equal(0.1, gated(0.12), 9);
        Assert.Equal(0.05, gated(0.06), 9);
    }

    // X-Plane takes free curve points: exact for any bend (stops at the calibrated positions).
    [Fact]
    public void XPlaneFit_CurvePointsExact()
    {
        AxisModel xp = AxisModels.ForGame("XPlane12");
        AxisTuning baseTuning = AxisModels.Inherit(Linear(), xp);
        DetentFit fit = DetentMath.Fit(xp, baseTuning, Calibration(detent: 0.75), DetentTarget.Afterburner(0.85), 0.01);
        Assert.Equal("curve points", fit.Method);
        Assert.True(fit.MaxError < DetentMath.Exact, "error " + fit.MaxError);
        Assert.True(fit.Tuning.HasControlPoints);
        Assert.InRange(fit.Tuning.ControlPoints().Count, 3, 12);
    }

    // IL-2 Great Battles has one-sided rows with low/high dead zones only: full at the detent is exact (high dead zone),
    // a bend for an afterburner is not, and the game keeps its normal curve with a warning.
    [Fact]
    public void Il2Fit_FullAtDetentExact_AfterburnerFallsBack()
    {
        AxisModel il2 = AxisModels.ForGame("IL2Game");
        AxisTuning baseTuning = AxisModels.Inherit(Linear(inverted: false), il2);
        DetentFit full = DetentMath.Fit(il2, baseTuning, Calibration(detent: 0.8), DetentTarget.Full, 0.01);
        Assert.False(full.Fallback);
        Assert.True(full.MaxError < 0.011, "error " + full.MaxError);
        Assert.Equal(0.2, full.Tuning.NativeOr("highValuesDeadZone", 0), 2);

        DetentFit ab = DetentMath.Fit(il2, baseTuning, Calibration(detent: 0.6), DetentTarget.Afterburner(0.95), 0.01);
        Assert.True(ab.Fallback || ab.MaxError > DetentMath.Acceptable);
        Assert.NotEqual(string.Empty, ab.Warning);
    }

    // Reading the user's old curves: the device's detent from the relation that gives full power there, the
    // afterburner start of another relation from its throttle at that point.
    [Fact]
    public void ProfileCurves_DetentAndAfterburnerStart()
    {
        double detent = DetentMath.DetentFromFullCurve(u => DcsThrottle(UserNoAb(), u));
        Assert.InRange(detent, 0.74, 0.80);
        Assert.True(double.IsNaN(DetentMath.DetentFromFullCurve(u => DcsThrottle(UserLinear(), u))));
        AxisTuning mig29 = AxisTuning.Parse("inv=1;sl=1;dz=0;sx=1;sy=1;cv=0|0.02|0.1|0.17|0.25|0.34|0.43|0.52|0.6|0.7|1");
        Assert.InRange(DcsThrottle(mig29, detent), 0.55, 0.61);
        double at = DetentMath.PositionOf(u => DcsThrottle(UserLinear(), u), 0.78);
        Assert.Equal(0.8, at, 2);
    }

    // The 11-point fit reproduces a curve that is a natural spline already.
    [Fact]
    public void UserCurveFit_ReproducesASpline()
    {
        List<double> ys = [0, 0.05, 0.12, 0.2, 0.3, 0.42, 0.55, 0.68, 0.8, 0.9, 1];
        List<double> fitted = DetentMath.FitUserCurve(u => AxisModels.NaturalSpline(ys, u), [0.37]);
        for (int i = 0; i < ys.Count; i++)
            Assert.Equal(ys[i], fitted[i], 3);
    }

    // ---- the Binds page -----------------------------------------------------------------------------------------------

    [Fact]
    public Task Panel_ShowsGroupsCalibrationAndWarnings() => UiThread.RunAsync(async () =>
    {
        DetentFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        Assert.True(vm.DetentLoaded);
        Assert.True(vm.DetentOn);
        Assert.True(vm.DetentNoAbFull);
        Assert.Equal(3, vm.DetentGroups.Count);
        DetentGroupItem ab = vm.DetentGroups[0];
        Assert.Equal("F-16C Viper, F/A-18C Hornet: afterburner at 78 %", ab.Heading);
        Assert.Equal("78", ab.AbText);
        Assert.Equal(0.78, ab.AbStartOrNaN, 9);
        Assert.True(ab.HasCurve);
        Assert.Contains("DCS: user curve, off by up to", ab.GamesText);
        Assert.Contains("game data (high confidence)", ab.SourceText);
        Assert.Contains("the game data says F/A-18C Hornet 79 %", ab.SourceText);
        DetentGroupItem il2 = vm.DetentGroups[2];
        Assert.True(il2.HasWarnings);
        Assert.Contains("keeps its normal curve", il2.GamesText);
        Assert.Contains("afterburner detent at 80 % of the travel (measured 2026-10-17)", vm.DetentCalibrationText);
        Assert.Equal(0.8, vm.DetentCalibratedTravel, 9);
        Assert.StartsWith("Detent mode is on: 4 aircraft in 3 groups.", vm.DetentStatus);
    });

    [Fact]
    public Task Panel_SwitchesAndGroupValues_RunTheEngine() => UiThread.RunAsync(async () =>
    {
        DetentFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        vm.DetentNoAbFull = false;
        await WaitForAsync(() => fake.Calls.Any(c => AxisTuningTests.Seq(c, "binds", "detent", "set", "Throttle", "--no-ab", "linear")));
        vm.DetentOn = false;
        await WaitForAsync(() => fake.Calls.Any(c => AxisTuningTests.Seq(c, "binds", "detent", "set", "Throttle", "--off")));

        DetentGroupItem group = vm.DetentGroups[0];
        group.AbText = "80,5";
        await vm.SetDetentGroupAbCommand.ExecuteAsync(group);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "--target", "DCS:F-16C_50=ab:0.805") && AxisTuningTests.Seq(c, "--target", "DCS:FA-18C_hornet=ab:0.805"));
        group = vm.DetentGroups[0];
        group.AbText = "";
        await vm.SetDetentGroupAbCommand.ExecuteAsync(group);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "--target", "DCS:F-16C_50=default"));
        group = vm.DetentGroups[0];
        group.AbText = "abc";
        int calls = fake.Calls.Count;
        await vm.SetDetentGroupAbCommand.ExecuteAsync(group);
        Assert.Equal(calls, fake.Calls.Count);
    });

    // 2.7.1 report: "is it not possible to copy detent mode to another axis? The button is greyed out". The curve's copy
    // stays off for a curve detent mode makes (its tip says where to go); the detent section copies detent mode itself.
    [Fact]
    public Task CopyDetentMode_ToAnotherAxis() => UiThread.RunAsync(async () =>
    {
        DetentFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        Assert.True(vm.CanPrepareDetentCopy);
        vm.PrepareDetentCopyCommand.Execute(null);
        Assert.Equal(["ThrottleMig29", "ThrottleNoAB"], vm.DetentCopyTargets.Select(t => t.Relation).ToArray());
        Assert.True(vm.HasDetentCopyTargets);
        vm.DetentCopyTarget = vm.DetentCopyTargets.Single(t => t.Relation == "ThrottleNoAB");
        Assert.True(vm.CanCopyDetent);
        Assert.True(vm.DetentCopyCalibration);
        await vm.CopyDetentToAxisCommand.ExecuteAsync(null);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "detent", "copy", "Throttle", "--to", "ThrottleNoAB", "--calibration"));
        Assert.StartsWith("Detent mode of 'Throttle' is copied to 'ThrottleNoAB'. Ctrl+Z undoes it.", vm.StatusText);

        vm.DetentCopyCalibration = false;
        Assert.Equal(["binds", "detent", "copy", "Throttle", "--to", "ThrottleNoAB"], vm.BuildDetentCopyArguments("ThrottleNoAB").ToArray());

        // What the other axis still needs: a calibration, and its own curves win there.
        string uncalibrated = "{\"Relation\":\"ThrottleNoAB\",\"Calibration\":null,\"Groups\":[{\"Games\":[{\"Title\":\"DCS\",\"OwnScope\":\"game:DCS\"}]}]}";
        string text = vm.DescribeDetentCopy("Throttle", "ThrottleNoAB", uncalibrated);
        Assert.Contains("select 'ThrottleNoAB' and run Calibrate detent... there", text);
        Assert.Contains("Its own curves for DCS win over detent mode there.", text);
        string lent = "{\"Relation\":\"ThrottleNoAB\",\"Calibration\":{\"Source\":\"copy\"},\"Groups\":[]}";
        Assert.Contains("uses this axis's calibration until you calibrate it", vm.DescribeDetentCopy("Throttle", "ThrottleNoAB", lent));

        // The curve's copy button points at it while detent mode makes the curve.
        vm.AxisTuningDetentLocked = true;
        Assert.Contains("Copy detent mode to another axis...", vm.AxisTuningCopyTip);
        vm.AxisTuningDetentLocked = false;
        Assert.StartsWith("The same dead zone", vm.AxisTuningCopyTip);
    });

    // 2.7.1 report: helicopters had no game data and got "no detent" (the tester typed an afterburner start for them).
    // A group can be marked as without afterburner; a lent calibration says so; an own curve that wins says how to undo it.
    [Fact]
    public Task Group_NoAfterburner_CopiedCalibration_OwnCurveHint() => UiThread.RunAsync(async () =>
    {
        DetentFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        DetentGroupItem ab = vm.DetentGroups[0];
        Assert.False(ab.IsFullAtDetent);
        Assert.True(vm.DetentGroups[1].IsFullAtDetent);
        await vm.SetDetentGroupNoAfterburnerCommand.ExecuteAsync(ab);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "--target", "DCS:F-16C_50=full") && AxisTuningTests.Seq(c, "--target", "DCS:FA-18C_hornet=full"));

        vm.ApplyDetentShow(DetentFakeEngine.ShowJson.Replace("\"Source\":\"wizard\"", "\"Source\":\"copy\"")
            .Replace("\"OwnScope\":\"\",\"PlaneCurves\":true,\"Fit\":{\"Method\":\"user curve\"", "\"OwnScope\":\"game:DCS\",\"PlaneCurves\":true,\"Fit\":{\"Method\":\"user curve\""));
        Assert.Contains("uses the calibration copied from another lever", vm.DetentCalibrationText);
        Assert.Contains("DCS: own tuning (game:DCS) wins over detent mode. To let detent mode bend it, tick DCS in the curve above and press Use the default curve again.", vm.DetentGroups[0].GamesText);
    });

    // 2.7.1 report: "make visible in the graph where the afterburner starts": binds tuning show's Resolved.Detent.
    [Fact]
    public void CurveMarks_ReadFromTuningShow()
    {
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(
            "{\"Source\":\"detent\",\"Detent\":{\"Input\":0.416,\"AfterburnerOutput\":-0.54,\"FullOutput\":-1}}");
        (double input, double ab, double full) = MainWindowViewModel.ReadAxisTuningDetentMarks(doc.RootElement);
        Assert.Equal(0.416, input, 9);
        Assert.Equal(-0.54, ab, 9);
        Assert.Equal(-1.0, full, 9);
        using System.Text.Json.JsonDocument noAb = System.Text.Json.JsonDocument.Parse("{\"Detent\":{\"Input\":0.5,\"AfterburnerOutput\":null,\"FullOutput\":1}}");
        Assert.True(double.IsNaN(MainWindowViewModel.ReadAxisTuningDetentMarks(noAb.RootElement).Afterburner));
        using System.Text.Json.JsonDocument none = System.Text.Json.JsonDocument.Parse("{\"Source\":\"base\",\"Detent\":null}");
        Assert.True(double.IsNaN(MainWindowViewModel.ReadAxisTuningDetentMarks(none.RootElement).Input));
    }

    // The wizard reads the bound axis live (JOY_RX = axis 3), refuses impossible steps and sends raw positions.
    [Fact]
    public Task Wizard_StepsValidateAndSave() => UiThread.RunAsync(async () =>
    {
        DetentFakeEngine fake = new();
        AxisTuningFakeCapture capture = new();
        MainWindowViewModel vm = await CreateVmAsync(fake, capture);
        vm.StartDetentWizardCommand.Execute(null);
        Assert.True(vm.IsDetentWizardOpen);
        Assert.True(capture.MonitoringAxes);
        Assert.Equal("Step 1 of 4: idle", vm.DetentWizardStepTitle);
        Assert.False(vm.CanConfirmDetentWizardStep);

        vm.OnDetentAxes(new JoystickAxisSnapshot("Other {00000000-0000-0000-0000-000000000999}", [0, 0, 0, 0.5, 0, 0, 0, 0]));
        Assert.True(double.IsNaN(vm.DetentWizardRaw));
        Feed(vm, 0.03);
        Assert.True(vm.CanConfirmDetentWizardStep);
        vm.ConfirmDetentWizardStepCommand.Execute(null);
        Assert.Equal(1, vm.DetentWizardStep);
        Assert.False(vm.CanSkipDetentWizardStep);

        Feed(vm, 0.04);
        vm.ConfirmDetentWizardStepCommand.Execute(null);
        Assert.Equal(1, vm.DetentWizardStep);
        Assert.Contains("still at idle", vm.DetentWizardError);
        Feed(vm, 0.79);
        vm.ConfirmDetentWizardStepCommand.Execute(null);
        Assert.Equal(2, vm.DetentWizardStep);

        Feed(vm, 0.6);
        vm.ConfirmDetentWizardStepCommand.Execute(null);
        Assert.Contains("not beyond the detent", vm.DetentWizardError);
        Feed(vm, 0.995);
        vm.ConfirmDetentWizardStepCommand.Execute(null);
        Assert.Equal(3, vm.DetentWizardStep);
        Assert.True(vm.CanSkipDetentWizardStep);
        vm.SkipDetentWizardStepCommand.Execute(null);
        Assert.True(vm.IsDetentWizardSummary);
        Assert.Contains("Idle at 3 %, afterburner detent at 79 % of the travel, full at 99.5 %", vm.DetentWizardStepText);
        Assert.Equal(["idle", "detent", "full"], vm.DetentWizardMarks.Select(m => m.Label).ToArray());
        Assert.Equal(["binds", "detent", "calibrate", "Throttle", "--idle", "0.03", "--detent", "0.79", "--source", "wizard", "--full", "0.995", "--cutoff", "none"],
            vm.BuildDetentCalibrateArguments().ToArray());

        await vm.SaveDetentWizardCommand.ExecuteAsync(null);
        Assert.False(vm.IsDetentWizardOpen);
        Assert.True(capture.LastToken.IsCancellationRequested);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "detent", "calibrate", "Throttle", "--idle", "0.03"));
    });

    // A reversed axis and a cut-off behind the idle detent.
    [Fact]
    public Task Wizard_ReversedAxisWithCutoff() => UiThread.RunAsync(async () =>
    {
        DetentFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        vm.StartDetentWizardCommand.Execute(null);
        Feed(vm, 0.9);
        vm.ConfirmDetentWizardStepCommand.Execute(null);
        Feed(vm, 0.25);
        vm.ConfirmDetentWizardStepCommand.Execute(null);
        vm.SkipDetentWizardStepCommand.Execute(null);
        Feed(vm, 0.8);
        vm.ConfirmDetentWizardStepCommand.Execute(null);
        Assert.Contains("behind idle", vm.DetentWizardError);
        Feed(vm, 0.99);
        vm.ConfirmDetentWizardStepCommand.Execute(null);
        Assert.True(vm.IsDetentWizardSummary);
        Assert.Contains("Idle at 10 %, afterburner detent at 75 % of the travel, cut-off at 1 %", vm.DetentWizardStepText);
        Assert.Contains("reversed axis", vm.DetentWizardStepText);
        Assert.Equal(["--full", "none", "--cutoff", "0.99"], vm.BuildDetentCalibrateArguments().Skip(10).ToArray());
        vm.BackDetentWizardStepCommand.Execute(null);
        Assert.Equal(3, vm.DetentWizardStep);
        vm.CancelDetentWizardCommand.Execute(null);
        Assert.False(vm.IsDetentWizardOpen);
    });

    [Fact]
    public Task Merge_PreviewApplyUndo() => UiThread.RunAsync(async () =>
    {
        DetentFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await vm.OpenDetentMergeCommand.ExecuteAsync(null);
        Assert.True(vm.IsDetentMergeOpen);
        DetentMergeProposalItem proposal = Assert.Single(vm.DetentMergeProposals);
        Assert.Equal("Test Throttle JOY_RX: Throttle, ThrottleMig29, ThrottleNoAB -> Throttle", proposal.Heading);
        Assert.Contains("Device detent at 77.7 % of the travel (read from the profile", proposal.CalibrationText);
        Assert.Contains("MiG-29: afterburner at 59.4 % (from profile)", proposal.GroupsText);
        Assert.True(proposal.CanApply);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("--apply"));

        await vm.ApplyDetentMergeCommand.ExecuteAsync(proposal);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "detent", "merge", "--relations", "Throttle,ThrottleMig29,ThrottleNoAB", "--into", "Throttle", "--apply"));
        Assert.True(vm.DetentMergeUndoAvailable);
        Assert.Empty(vm.DetentMergeProposals);
        Assert.StartsWith("Merged Throttle, ThrottleMig29, ThrottleNoAB into Throttle.", vm.DetentMergeStatus);
    });

    // 2.6.5 report: the merge took every throttle relation of the axis (a helicopter collective too). Each relation is
    // ticked; an unticked one is planned out and stays apart; fewer than two ticked: nothing to merge.
    [Fact]
    public Task Merge_UntickedRelationStaysApart() => UiThread.RunAsync(async () =>
    {
        DetentFakeEngine fake = new();
        MainWindowViewModel vm = await CreateVmAsync(fake);
        await vm.OpenDetentMergeCommand.ExecuteAsync(null);
        DetentMergeProposalItem proposal = Assert.Single(vm.DetentMergeProposals);
        Assert.Equal(["Throttle", "ThrottleMig29", "ThrottleNoAB"], proposal.Candidates.Select(c => c.Name).ToArray());
        Assert.All(proposal.Candidates, c => Assert.True(c.IsChecked));

        proposal.Candidates.Single(c => c.Name == "ThrottleMig29").IsChecked = false;
        await WaitForAsync(() => fake.Calls.Any(c => AxisTuningTests.Seq(c, "binds", "detent", "merge", "--relations", "Throttle,ThrottleNoAB")) && !proposal.IsPlanning);
        Assert.Equal("Test Throttle JOY_RX: Throttle, ThrottleNoAB -> Throttle", proposal.Heading);
        Assert.True(proposal.CanApply);

        // One left: Merge waits and says why.
        proposal.Candidates.Single(c => c.Name == "ThrottleNoAB").IsChecked = false;
        Assert.False(proposal.CanApply);
        Assert.Equal("Tick at least two relations to merge.", proposal.NotesText);
        proposal.Candidates.Single(c => c.Name == "ThrottleNoAB").IsChecked = true;
        await WaitForAsync(() => !proposal.IsPlanning && proposal.CanApply);

        await vm.ApplyDetentMergeCommand.ExecuteAsync(proposal);
        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "binds", "detent", "merge", "--relations", "Throttle,ThrottleNoAB", "--into", "Throttle", "--apply"));
        Assert.DoesNotContain(fake.Calls, c => c.Contains("--apply") && c.Any(a => a.Contains("ThrottleMig29", StringComparison.Ordinal)));
    });

    // The panel's texts go through the localization; the engine's English group texts are translated by pattern.
    [Fact]
    public void Texts_AreLocalized()
    {
        try
        {
            JoyPro.Gui.Localization.Loc.SetLanguage("de");
            Assert.Equal("Schritt 1 von 4: Leerlauf", JoyPro.Gui.Localization.Loc.T("Step 1 of 4: idle"));
            Assert.Equal("Nachbrenner bei 78 %", JoyPro.Gui.Localization.Loc.Translate("afterburner at 78 %"));
            Assert.Equal("kein Nachbrenner: Vollschub an der Raste", JoyPro.Gui.Localization.Loc.Translate("no afterburner: full power at the detent"));
            Assert.Equal("3 Gruppen", JoyPro.Gui.Localization.Loc.Plural("{0} group", "{0} groups", 3));
        }
        finally
        {
            JoyPro.Gui.Localization.Loc.SetLanguage("en");
        }
    }

    private static void Feed(MainWindowViewModel vm, double rx) =>
        vm.OnDetentAxes(new JoystickAxisSnapshot(Throttle, [0, 0, 0, rx, 0, 0, 0, 0]));

    private static async Task<MainWindowViewModel> CreateVmAsync(DetentFakeEngine fake, AxisTuningFakeCapture? capture = null)
    {
        MainWindowViewModel vm = new(fake, capture ?? new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
        vm.EnginePath = typeof(MainWindowViewModel).Assembly.Location;
        string tempRoot = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests");
        Directory.CreateDirectory(tempRoot);
        vm.ProfilePath = Path.Combine(tempRoot, "detent.pr0file");
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        vm.CurrentStage = UiStage.Binds;
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Throttle");
        Assert.True(vm.SelectedBindIsAxis);
        vm.IsDetentExpanded = true;
        await WaitForAsync(() => vm.DetentLoaded);
        return vm;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "Timed out.");
    }
}

// Three throttle relations on JOY_RX of a throttle; detent show/set/calibrate answer one canned state.
[SupportedOSPlatform("windows")]
internal sealed class DetentFakeEngine : IEngineCliClient
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

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

    private static string Relation(string name) =>
        "{\"Name\":\"" + name + "\",\"IsAxis\":true,\"Groups\":[\"Flight\"],\"Nodes\":[{\"Id\":\"a2004cdnil\",\"Game\":\"DCS\",\"Aircraft\":{\"F-16C_50\":true}}]," +
        "\"Bind\":{\"Joystick\":\"" + DetentModeTests.Throttle + "\",\"JAxis\":\"JOY_RX\",\"JButton\":\"\"}}";

    private static string Bind(string name) =>
        "{\"RelationName\":\"" + name + "\",\"IsAxis\":true,\"Joystick\":\"" + DetentModeTests.Throttle + "\",\"JAxis\":\"JOY_RX\",\"JButton\":\"\"," +
        "\"Inverted\":true,\"Slider\":true,\"Deadzone\":0,\"Curvature\":[0],\"SaturationX\":1,\"SaturationY\":1,\"AllReformers\":[],\"DoubleTap\":false}";

    private string BuildOutput(IReadOnlyList<string> args)
    {
        if (AxisTuningTests.Seq(args, "relations", "list"))
            return "{\"Relations\":[" + Relation("Throttle") + "," + Relation("ThrottleMig29") + "," + Relation("ThrottleNoAB") + "]}";
        if (AxisTuningTests.Seq(args, "binds", "list"))
            return "{\"Binds\":[" + Bind("Throttle") + "," + Bind("ThrottleMig29") + "," + Bind("ThrottleNoAB") + "]}";
        if (AxisTuningTests.Seq(args, "relations", "show"))
            return "Relation: Throttle\nNodes:\n  [1] game=DCS, id=a2004cdnil, aircraft=F-16C_50";
        if (AxisTuningTests.Seq(args, "binds", "tuning", "show"))
            return "{\"Relation\":\"Throttle\",\"Scopes\":{\"base\":\"inv=1;sl=1;dz=0;sx=1;sy=1;cv=0\"},\"Game\":null,\"Plane\":null,\"Resolved\":null}";
        if (AxisTuningTests.Seq(args, "binds", "detent", "merge"))
            return args.Contains("--apply") ? "{\"Applied\":true,\"Proposals\":[]}" : MergePlan;
        if (AxisTuningTests.Seq(args, "binds", "detent"))
            return Show;
        return "ok";
    }

    private const string Throttle51 = "[[0,0],[0.5,0.48],[0.8,0.77],[1,1]]";

    internal static string ShowJson => Show;

    private const string Show = "{\"Relation\":\"Throttle\",\"Message\":\"\",\"Device\":\"" + DetentModeTests.Throttle + "\",\"Axis\":\"JOY_RX\",\"DeviceLabel\":\"Test Throttle JOY_RX\"," +
        "\"Settings\":{\"Stored\":true,\"On\":true,\"NoAfterburner\":\"full\",\"Unknown\":\"linear\",\"Margin\":0.01,\"Text\":\"on=1;noab=full\"}," +
        "\"Calibration\":{\"Idle\":0.02,\"Detent\":0.8,\"Full\":null,\"Cutoff\":null,\"Source\":\"wizard\",\"When\":\"2026-10-17\",\"Increasing\":true,\"IdleTravel\":0.02,\"DetentTravel\":0.8,\"Text\":\"idle=0.02;det=0.8\"}," +
        "\"GatesFile\":\"x\",\"Groups\":[" +
        "{\"Target\":\"ab:0.78\",\"Kind\":\"Afterburner\",\"AbStart\":0.78,\"IdleGate\":0,\"Describe\":\"afterburner at 78 %\",\"Label\":\"F-16C Viper, F/A-18C Hornet\"," +
        "\"Aircraft\":[{\"Game\":\"DCS\",\"Plane\":\"F-16C_50\",\"Title\":\"F-16C Viper\",\"Source\":\"game data\",\"Confidence\":\"high\",\"GameData\":0.78,\"GameDataSource\":\"x\"}," +
        "{\"Game\":\"DCS\",\"Plane\":\"FA-18C_hornet\",\"Title\":\"F/A-18C Hornet\",\"Source\":\"game data\",\"Confidence\":\"high\",\"GameData\":0.79,\"GameDataSource\":\"x\"}]," +
        "\"Games\":[{\"Game\":\"DCS\",\"Title\":\"DCS\",\"Model\":\"dcs\",\"OwnScope\":\"\",\"PlaneCurves\":true,\"Fit\":{\"Method\":\"user curve\",\"MaxError\":0.006,\"Fallback\":false,\"Warning\":\"\",\"Tuning\":\"inv=1\",\"Throttle\":" + Throttle51 + "}}]}," +
        "{\"Target\":\"full\",\"Kind\":\"FullAtDetent\",\"AbStart\":null,\"IdleGate\":0,\"Describe\":\"no afterburner: full power at the detent\",\"Label\":\"A-10C\"," +
        "\"Aircraft\":[{\"Game\":\"DCS\",\"Plane\":\"A-10C\",\"Title\":\"A-10C\",\"Source\":\"game data\",\"Confidence\":\"high\",\"GameData\":null,\"GameDataSource\":\"\"}]," +
        "\"Games\":[{\"Game\":\"DCS\",\"Title\":\"DCS\",\"Model\":\"dcs\",\"OwnScope\":\"\",\"PlaneCurves\":true,\"Fit\":{\"Method\":\"dead zone + saturation\",\"MaxError\":0.0001,\"Fallback\":false,\"Warning\":\"\",\"Tuning\":\"inv=1\",\"Throttle\":" + Throttle51 + "}}]}," +
        "{\"Target\":\"full\",\"Kind\":\"FullAtDetent\",\"AbStart\":null,\"IdleGate\":0,\"Describe\":\"no afterburner: full power at the detent\",\"Label\":\"IL-2 Great Battles\"," +
        "\"Aircraft\":[{\"Game\":\"IL2Game\",\"Plane\":\"IL2Game\",\"Title\":\"IL-2 Great Battles\",\"Source\":\"game data\",\"Confidence\":\"high\",\"GameData\":null,\"GameDataSource\":\"\"}]," +
        "\"Games\":[{\"Game\":\"IL2Game\",\"Title\":\"IL-2 Great Battles\",\"Model\":\"il2\",\"OwnScope\":\"\",\"PlaneCurves\":false,\"Fit\":{\"Method\":\"game parameters\",\"MaxError\":0.2,\"Fallback\":true,\"Warning\":\"IL-2 Great Battles cannot express the detent curve\",\"Tuning\":\"inv=1\",\"Throttle\":" + Throttle51 + "}}]}" +
        "],\"Warnings\":[]}";

    private const string MergePlan = "{\"Applied\":false,\"AbSource\":\"profile\",\"Proposals\":[{\"Device\":\"" + DetentModeTests.Throttle + "|JOY_RX\",\"DeviceLabel\":\"Test Throttle JOY_RX\",\"Into\":\"Throttle\"," +
        "\"Sources\":[{\"Relation\":\"Throttle\",\"Planes\":2,\"AtDetent\":0.755,\"FullAt\":null},{\"Relation\":\"ThrottleMig29\",\"Planes\":1,\"AtDetent\":0.584,\"FullAt\":null},{\"Relation\":\"ThrottleNoAB\",\"Planes\":1,\"AtDetent\":1,\"FullAt\":0.777}]," +
        "\"Calibration\":{\"Idle\":0,\"Detent\":0.777,\"DetentTravel\":0.777,\"Inferred\":true,\"Note\":\"read from the profile: ThrottleNoAB gives full power there; confirm it with the detent wizard\",\"Text\":\"idle=0;det=0.777\"}," +
        "\"NoAfterburner\":\"full\",\"Targets\":[" +
        "{\"Game\":\"DCS\",\"Plane\":\"F-16C_50\",\"Title\":\"F-16C Viper\",\"Relation\":\"Throttle\",\"Target\":\"ab:0.765\",\"Describe\":\"afterburner at 76.5 %\",\"From\":\"profile\",\"AtDetent\":0.755,\"GameData\":0.78,\"Confidence\":\"high\"}," +
        "{\"Game\":\"DCS\",\"Plane\":\"mig-29\",\"Title\":\"MiG-29\",\"Relation\":\"ThrottleMig29\",\"Target\":\"ab:0.594\",\"Describe\":\"afterburner at 59.4 %\",\"From\":\"profile\",\"AtDetent\":0.584,\"GameData\":null,\"Confidence\":\"\"}," +
        "{\"Game\":\"DCS\",\"Plane\":\"A-10C\",\"Title\":\"A-10C\",\"Relation\":\"ThrottleNoAB\",\"Target\":\"full\",\"Describe\":\"no afterburner: full power at the detent\",\"From\":\"profile\",\"AtDetent\":1,\"GameData\":null,\"Confidence\":\"high\"}" +
        "],\"Notes\":[],\"Problems\":[]}]}";
}
