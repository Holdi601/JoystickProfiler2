using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using JoyPro.Engine.Axis;
using Xunit;

namespace JoyPro.Gui.Tests;

// 2.7.4 report (speed-of-heat, 2.7.3 support files): the MiG-21's exported DCS curve fell (80 % point below the 70 %
// point) and two levers resting in their detents gave one engine afterburner and not the other. Every detent curve
// rises along the travel; resting in the detent band stays below the afterburner on both levers; two levers give the
// same output at the same position relative to their own detent.
[SupportedOSPlatform("windows")]
public sealed class DetentMonotoneTests
{
    private static readonly AxisModel Dcs = AxisModels.Get(AxisModels.Dcs);

    // His VPC MongoosT-50CM3: Thrust Left on JOY_RX, Thrust Right on JOY_RY, the MiG-21 throttle on JOY_RX (2.7.3 wizard).
    private static DetentCalibration Left() => DetentCalibration.Parse("idle=0.0408;det=0.7132;full=0.9994;cut=0.0006;src=wizard")!;
    private static DetentCalibration Right() => DetentCalibration.Parse("idle=0.0433;det=0.7094;full=0.9993;cut=0;src=wizard")!;
    private static DetentCalibration MiG21Lever() => DetentCalibration.Parse("idle=0.0416;det=0.7193;full=0.9999;cut=0.0009;src=wizard")!;

    // His relations' own curves: the F-5E's has 80 % at the detent; the MiG-21's dips (92 % at 70 %, 91 % at 80 %).
    private static AxisTuning F5Base() => AxisTuning.Parse("inv=1;sl=1;dz=0.04;sx=1;sy=1;cv=0|0.08|0.2|0.32|0.44|0.56|0.68|0.8|0.88|0.94|1");
    private static AxisTuning MiG21Base() => AxisTuning.Parse("inv=1;sl=1;dz=0.04;sx=1;sy=1;cv=0|0.13|0.26|0.4|0.53|0.66|0.79|0.92|0.91|0.97|1");

    private static double Throttle(AxisModel model, AxisTuning tuning, AxisTuning baseTuning, DetentCalibration cal, double raw)
    {
        DetentMath.BaseThrottle(model, baseTuning, cal, out double sigma);
        double s = sigma == 0.0 ? 1.0 : sigma;
        return (1.0 + s * model.Evaluate(tuning, 2.0 * raw - 1.0)) / 2.0;
    }

    private static double AtTravel(AxisModel model, AxisTuning tuning, AxisTuning baseTuning, DetentCalibration cal, double travel) =>
        Throttle(model, tuning, baseTuning, cal, cal.RawAt(travel));

    [Fact]
    public void MiG21_DippingBase_GivesARisingCurve()
    {
        DetentCalibration cal = MiG21Lever();
        AxisTuning baseTuning = MiG21Base();
        // The relation's own curve falls (that is what 2.7.3 bent and exported).
        Assert.True(DetentMath.LargestDrop(Dcs, baseTuning, baseTuning, cal) > 0.005);

        DetentFit fit = DetentMath.Fit(Dcs, baseTuning, cal, DetentTarget.Afterburner(0.91), DetentSettings.DefaultMargin, DetentSettings.DefaultBand);
        Assert.False(fit.Fallback, fit.Warning);
        Assert.Equal("user curve", fit.Method);
        List<double> points = fit.Tuning.Curvature;
        for (int i = 1; i < points.Count; i++)
            Assert.True(points[i] >= points[i - 1], "point " + i + " (" + points[i] + ") below point " + (i - 1) + " (" + points[i - 1] + ")");
        Assert.True(DetentMath.LargestDrop(Dcs, fit.Tuning, baseTuning, cal) <= 1e-4, "the throttle falls");
        Assert.InRange(Throttle(Dcs, fit.Tuning, baseTuning, cal, cal.Detent), 0.88, 0.89 + 0.002);
        double detent = cal.Travel(cal.Detent);
        for (double t = detent; t <= detent + DetentSettings.DefaultBand; t += 0.001)
            Assert.True(AtTravel(Dcs, fit.Tuning, baseTuning, cal, t) <= DetentMath.BandCeiling(DetentTarget.Afterburner(0.91), DetentSettings.DefaultMargin) + 1e-4);
    }

    // Both levers in their detents: neither lights the afterburner, also a little past the detent (the band), and
    // the same position relative to each lever's own detent gives the same throttle; the afterburner starts at the
    // same distance past the detent on both despite different calibrations and dead zones.
    [Fact]
    public void LeftAndRight_StayInSyncAroundTheDetent()
    {
        DetentTarget f5 = DetentTarget.Afterburner(0.8);
        DetentCalibration left = Left();
        DetentCalibration right = Right();
        DetentFit l = DetentMath.Fit(Dcs, F5Base(), left, f5, DetentSettings.DefaultMargin, DetentSettings.DefaultBand);
        DetentFit r = DetentMath.Fit(Dcs, F5Base(), right, f5, DetentSettings.DefaultMargin, DetentSettings.DefaultBand);
        Assert.False(l.Fallback || r.Fallback);
        Assert.True(l.MaxError < DetentMath.Acceptable && r.MaxError < DetentMath.Acceptable, l.MaxError + " / " + r.MaxError);
        double ceiling = DetentMath.BandCeiling(f5, DetentSettings.DefaultMargin);
        double dl = left.Travel(left.Detent);
        double dr = right.Travel(right.Detent);
        for (double d = -0.3; d <= 0.25; d += 0.0025)
        {
            double tl = AtTravel(Dcs, l.Tuning, F5Base(), left, dl + d);
            double tr = AtTravel(Dcs, r.Tuning, F5Base(), right, dr + d);
            Assert.True(Math.Abs(tl - tr) <= 0.004, "detent " + d.ToString("+0.000;-0.000") + ": left " + tl + ", right " + tr);
            if (d >= 0 && d <= DetentSettings.DefaultBand)
                Assert.True(tl <= ceiling + 1e-4 && tr <= ceiling + 1e-4, "in the detent band (" + d + ") the afterburner must stay off: " + tl + " / " + tr);
        }
        // Resting in the detent: at most the afterburner start minus the margin (a little less: his curve rises steeply
        // past the detent, so the band's front edge would pass 79.5 % otherwise).
        Assert.InRange(AtTravel(Dcs, l.Tuning, F5Base(), left, dl), 0.76, 0.78 + 0.002);
        Assert.InRange(AtTravel(Dcs, r.Tuning, F5Base(), right, dr), 0.76, 0.78 + 0.002);
        double abLeft = FirstTravel(t => AtTravel(Dcs, l.Tuning, F5Base(), left, t), 0.8) - dl;
        double abRight = FirstTravel(t => AtTravel(Dcs, r.Tuning, F5Base(), right, t), 0.8) - dr;
        Assert.InRange(abLeft, DetentSettings.DefaultBand, 0.05);
        Assert.True(Math.Abs(abLeft - abRight) < 0.002, "afterburner past the detent: left " + abLeft + ", right " + abRight);
    }

    // Settings written up to 2.7.3 always carried margin=0.01 (the old default, never offered in the GUI): read as the
    // new default; texts with a band keep their margin.
    [Fact]
    public void LegacyMargin_ReadsAsTheNewDefault()
    {
        DetentSettings old = DetentSettings.Parse("on=1;noab=linear;unknown=linear;margin=0.01;t.DCS:F-5E-3=ab:0.8")!;
        Assert.Equal(DetentSettings.DefaultMargin, old.Margin);
        Assert.Equal(DetentSettings.DefaultBand, old.Band);
        DetentSettings chosen = DetentSettings.Parse(old.Format().Replace("margin=0.02", "margin=0.01"))!;
        Assert.Equal(0.01, chosen.Margin);
        Assert.Contains("band=0.02", old.Format());
        Assert.Equal(0.0, DetentSettings.Parse("on=1;band=0")!.Band);
    }

    // Every aircraft of the shipped gate data x typical calibrations (with and without cut-off, reversed axis, the
    // tester's levers) x bases (dead zone 0 .. 10 %, a user curve, a dipping curve): the bent curve never falls, DCS
    // always gets one (no fallback), and resting in the detent band stays below the afterburner.
    [Fact]
    public void AllGates_AllCalibrations_CurvesRiseAndTheBandStaysBelowTheAfterburner()
    {
        List<(string Game, string Plane, DetentTarget Target)> aircraft = GateTargets();
        Assert.True(aircraft.Count(a => a.Game == "DCS") >= 40, "gate data not found");
        DetentCalibration[] calibrations =
        [
            new() { Idle = 0.0, Detent = 0.8 },
            new() { Idle = 0.05, Detent = 0.7, Full = 0.98 },
            new() { Idle = 1.0, Detent = 0.25 },
            new() { Cutoff = 0.001, Idle = 0.043, Detent = 0.708, Full = 0.999 },
            Left(),
            Right(),
            MiG21Lever()
        ];
        List<(string Name, AxisTuning Tuning)> bases = [];
        foreach (double dz in new[] { 0.0, 0.04, 0.1 })
            bases.Add(("linear dz " + dz, new AxisTuning { Slider = true, Inverted = true, Deadzone = dz }));
        bases.Add(("user curve", AxisTuning.Parse("inv=1;sl=1;dz=0;sx=1;sy=1;cv=0|0.02|0.12|0.23|0.34|0.45|0.56|0.67|0.78|0.89|1")));
        bases.Add(("F-5E curve", F5Base()));
        bases.Add(("MiG-21 curve", MiG21Base()));

        List<string> failures = [];
        int fits = 0;
        foreach ((string game, string plane, DetentTarget target) in aircraft)
        {
            AxisModel model = AxisModels.ForGame(game);
            bool dcs = model.Id == AxisModels.Dcs;
            bool exactModel = dcs || model.CurveStops != null;
            // Other games' parameter searches are slow and tested elsewhere: two calibrations and two bases there.
            IEnumerable<DetentCalibration> cals = dcs ? calibrations : calibrations.Take(1).Concat(calibrations.Skip(3).Take(1));
            IEnumerable<(string Name, AxisTuning Tuning)> bs = dcs || exactModel ? bases : bases.Take(1).Concat(bases.Skip(5));
            foreach (DetentCalibration cal in cals)
            {
                foreach ((string baseName, AxisTuning dcsBase) in bs)
                {
                    AxisTuning baseTuning = dcs ? dcsBase : AxisModels.Inherit(dcsBase, model);
                    string what = game + "/" + plane + " " + target.Format() + ", " + cal.Format() + ", " + baseName + ": ";
                    DetentFit fit = DetentMath.Fit(model, baseTuning, cal, target, DetentSettings.DefaultMargin, DetentSettings.DefaultBand);
                    fits++;
                    if (fit.Fallback || fit.Tuning == null)
                    {
                        if (dcs)
                            failures.Add(what + "fell back: " + fit.Warning);
                        continue;
                    }
                    double drop = DetentMath.LargestDrop(model, fit.Tuning, baseTuning, cal);
                    if (drop > 1e-4)
                        failures.Add(what + "the throttle falls by " + drop);
                    if (dcs && fit.Tuning.IsUserCurve)
                    {
                        List<double> p = fit.Tuning.Curvature;
                        for (int i = 1; i < p.Count; i++)
                        {
                            if (p[i] < p[i - 1])
                                failures.Add(what + "point " + i + " below point " + (i - 1) + ": " + string.Join("|", p));
                        }
                    }
                    if (!exactModel || target.Kind != DetentTargetKind.Afterburner)
                        continue;
                    double rest = DetentMath.WantedThrottle(model, baseTuning, cal, target, DetentSettings.DefaultMargin, DetentSettings.DefaultBand)(cal.Detent);
                    if (rest > target.AbStart - DetentSettings.DefaultMargin + 1e-9)
                        failures.Add(what + "the detent gives " + rest + ", above the afterburner start minus the margin");
                    double atDetent = Throttle(model, fit.Tuning, baseTuning, cal, cal.Detent);
                    if (Math.Abs(atDetent - rest) > 0.003)
                        failures.Add(what + "at the detent " + atDetent + " instead of " + rest);
                    double band = DetentMath.EffectiveBand(cal, target, DetentSettings.DefaultBand);
                    double ceiling = DetentMath.BandCeiling(target, DetentSettings.DefaultMargin);
                    double detent = cal.Travel(cal.Detent);
                    for (double t = detent; t <= detent + band + 1e-9; t += band / 20.0)
                    {
                        double v = AtTravel(model, fit.Tuning, baseTuning, cal, t);
                        if (v > ceiling + 1e-4)
                        {
                            failures.Add(what + "in the detent band at +" + (t - detent).ToString("0.000") + " the throttle is " + v + " (afterburner " + target.AbStart + ")");
                            break;
                        }
                    }
                }
            }
        }
        Assert.True(fits > 2000, "only " + fits + " fits");
        Assert.True(failures.Count == 0, failures.Count + " of " + fits + " fits:\n" + string.Join("\n", failures.Take(40)));
    }

    private static double FirstTravel(Func<double, double> throttleAtTravel, double level)
    {
        for (int i = 0; i <= 10000; i++)
        {
            if (throttleAtTravel(i / 10000.0) >= level)
                return i / 10000.0;
        }
        return double.NaN;
    }

    // The shipped gate data: afterburner start (with idle gate), no afterburner = full at the detent, unknown = the
    // tester's 91 % (his MiG-21) and a low 61 % (a MiG-29 style start).
    private static List<(string Game, string Plane, DetentTarget Target)> GateTargets()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine(dir!.FullName, "EngineOnly_Runtime", "DB", "Detents", "throttle-gates.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        List<(string, string, DetentTarget)> list = [];
        foreach (JsonElement a in doc.RootElement.GetProperty("aircraft").EnumerateArray())
        {
            string game = a.GetProperty("game").GetString()!;
            string plane = a.GetProperty("plane").GetString()!;
            double idleGate = a.TryGetProperty("idleGate", out JsonElement ig) ? ig.GetDouble() : 0.0;
            if (a.TryGetProperty("noAfterburner", out JsonElement noAb) && noAb.GetBoolean())
                list.Add((game, plane, DetentTarget.Full));
            else if (a.TryGetProperty("afterburner", out JsonElement ab))
                list.Add((game, plane, DetentTarget.Afterburner(ab.GetDouble(), idleGate)));
            else
            {
                list.Add((game, plane, DetentTarget.Afterburner(0.91)));
                list.Add((game, plane, DetentTarget.Afterburner(0.61)));
            }
        }
        return list;
    }
}
