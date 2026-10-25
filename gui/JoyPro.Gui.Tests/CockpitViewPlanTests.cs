using System;
using System.Collections.Generic;
using System.Linq;
using JoyPro.CockpitPictures;
using Xunit;

namespace JoyPro.Gui.Tests;

// The view planner of the cockpit pictures (docs/cockpits.md "Pictures from your games"): one picture per compact panel
// (no duplicates of a keypad split by its count), named panels keep their own page, a panel is seen face on from its
// surface (a row of knobs gives no plane), dark renders get their middle tones lifted.
public sealed class CockpitViewPlanTests
{
    private static readonly Vec3 Head = new(0, 0, 0);

    // A flat panel facing the pilot (-x) at x = 0.9, with its face as two triangles.
    private static CockpitScene PanelScene(double size = 1.2)
    {
        CockpitScene scene = new();
        scene.Materials.Add(new SceneMaterial { Name = "panel" });
        float x = 0.9f, h = (float)(size / 2);
        scene.Meshes.Add(new SceneMesh
        {
            Name = "panel", Material = 0,
            Positions = [x, -h, -h, x, h, -h, x, h, h, x, -h, h],
            Normals = [-1, 0, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0],
            Indices = [0, 1, 2, 0, 2, 3],
        });
        return scene;
    }

    // A flat quad (two triangles) with its corners.
    private static void Quad(CockpitScene scene, Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        if (scene.Materials.Count == 0)
            scene.Materials.Add(new SceneMaterial { Name = "panel" });
        scene.Meshes.Add(new SceneMesh
        {
            Name = "quad" + scene.Meshes.Count, Material = 0,
            Positions = [(float)a.X, (float)a.Y, (float)a.Z, (float)b.X, (float)b.Y, (float)b.Z, (float)c.X, (float)c.Y, (float)c.Z, (float)d.X, (float)d.Y, (float)d.Z],
            Indices = [0, 1, 2, 0, 2, 3],
        });
    }

    // Every panel picture is the right way round, as the pilot reads the panel: an overhead panel (looked up at, the head
    // tilted back) has aft at the top, a console (looked down at) has forward at the top, a front panel has up at the
    // top; the pilot's right is on the right in all of them (not mirrored, not upside down).
    [Theory]
    [InlineData("overhead")]
    [InlineData("console")]
    [InlineData("front")]
    public void PanelPictures_AreTheRightWayRound(string where)
    {
        CockpitScene scene = new();
        List<CockpitCallout> cs;
        // "top" / "bottom" as the pilot reads the panel; "left" / "right" the pilot's left and right (z: right).
        if (where == "overhead")
        {
            Quad(scene, new Vec3(0, 0.5, -0.3), new Vec3(0.6, 0.5, -0.3), new Vec3(0.6, 0.5, 0.3), new Vec3(0, 0.5, 0.3));
            cs = [At("top", "P", 0.1, 0.49, 0), At("bottom", "P", 0.4, 0.49, 0), At("left", "P", 0.25, 0.49, -0.2), At("right", "P", 0.25, 0.49, 0.2)];
        }
        else if (where == "console")
        {
            Quad(scene, new Vec3(-0.2, -0.5, 0.3), new Vec3(0.4, -0.5, 0.3), new Vec3(0.4, -0.5, 0.6), new Vec3(-0.2, -0.5, 0.6));
            cs = [At("top", "P", 0.3, -0.49, 0.45), At("bottom", "P", -0.1, -0.49, 0.45), At("left", "P", 0.1, -0.49, 0.35), At("right", "P", 0.1, -0.49, 0.55)];
        }
        else
        {
            Quad(scene, new Vec3(0.9, -0.4, -0.4), new Vec3(0.9, 0.4, -0.4), new Vec3(0.9, 0.4, 0.4), new Vec3(0.9, -0.4, 0.4));
            cs = [At("top", "P", 0.89, 0.15, 0), At("bottom", "P", 0.89, -0.15, 0), At("left", "P", 0.89, 0, -0.15), At("right", "P", 0.89, 0, 0.15)];
        }
        CockpitViewPlan v = CockpitViews.Plan(scene, Head, cs, [], 400).Single(x => x.Kind == "panel");
        Dictionary<string, (double X, double Y)> at = [];
        foreach (CockpitCallout c in v.Callouts)
        {
            Assert.True(v.Camera.Project(c.Position, out double x, out double y, out _));
            at[c.Id] = (x, y);
        }
        Assert.True(at["top"].Y < at["bottom"].Y, where + ": upside down");
        Assert.True(at["left"].X < at["right"].X, where + ": mirrored");
    }

    private static CockpitCallout At(string id, string panel, double x, double y, double z) =>
        new() { Id = id, Label = id, Panel = panel, PanelTitle = panel, Position = new Vec3(x, y, z), Kind = "button" };

    [Fact]
    public void ACompactKeypadWithManyKeys_IsOnePicture()
    {
        CockpitScene scene = PanelScene();
        List<CockpitCallout> callouts = [];
        // 60 keys on 15 x 15 cm (more than MaxPerPanel), and two switches 40 cm away on the same panel.
        for (int i = 0; i < 60; i++)
            callouts.Add(At("KEY" + i, "Pedestal", 0.89, -0.2 + (i / 8) * 0.02, -0.07 + (i % 8) * 0.02));
        callouts.Add(At("SW1", "Pedestal", 0.89, 0.25, 0.3));
        callouts.Add(At("SW2", "Pedestal", 0.89, 0.27, 0.32));
        List<CockpitViewPlan> panels = CockpitViews.Plan(scene, Head, callouts, [], 400).Where(v => v.Kind == "panel").ToList();
        Assert.Contains(panels, v => v.Callouts.Count(c => c.Id.StartsWith("KEY", StringComparison.Ordinal)) == 60);
        Assert.Equal(1, panels.Count(v => v.Callouts.Any(c => c.Id.StartsWith("KEY", StringComparison.Ordinal))));
    }

    [Fact]
    public void TwoNamedPanelsSideBySide_KeepTheirOwnPictures()
    {
        CockpitScene scene = PanelScene();
        List<CockpitCallout> callouts = [];
        for (int i = 0; i < 6; i++)
        {
            callouts.Add(At("A" + i, "ELEC Control Panel", 0.89, 0.0 + i * 0.02, -0.12));
            callouts.Add(At("B" + i, "FUEL Control Panel", 0.89, 0.0 + i * 0.02, 0.0));
        }
        List<CockpitViewPlan> panels = CockpitViews.Plan(scene, Head, callouts, [], 400).Where(v => v.Kind == "panel").ToList();
        Assert.Equal(2, panels.Count);
        Assert.Contains(panels, v => v.Title == "ELEC Control Panel" && v.Callouts.All(c => c.Id.StartsWith('A')));
        Assert.Contains(panels, v => v.Title == "FUEL Control Panel" && v.Callouts.All(c => c.Id.StartsWith('B')));
    }

    [Fact]
    public void TheHalvesOfOnePanelSplitByCount_BecomeOnePicture()
    {
        List<List<CockpitCallout>> groups =
        [
            Enumerable.Range(0, 20).Select(i => At("L" + i, "Overhead", 0.5, 0.4, -0.1 + i * 0.005)).ToList(),
            Enumerable.Range(0, 20).Select(i => At("R" + i, "Overhead", 0.5, 0.4, 0.05 + i * 0.005)).ToList(),
            Enumerable.Range(0, 3).Select(i => At("X" + i, "Radio", 0.5, 0.4, 0.2 + i * 0.01)).ToList(),
        ];
        CockpitViews.MergeCompact(groups);
        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Count == 40);
        Assert.Contains(groups, g => g.All(c => c.Panel == "Radio"));
    }

    [Fact]
    public void ARowOfKnobs_IsSeenFaceOnFromThePanelsSurface()
    {
        CockpitScene scene = PanelScene();
        // Five knobs in a straight line across the panel (no plane through them), with a little depth noise that would
        // tilt a plane fitted to them by 90 degrees.
        List<CockpitCallout> row = Enumerable.Range(0, 5).Select(i => At("K" + i, "Lights", 0.88 + (i % 2) * 0.004, 0.1, -0.1 + i * 0.05)).ToList();
        CockpitViewPlan v = CockpitViews.Plan(scene, Head, row, [], 400).Single(x => x.Kind == "panel");
        // Looking at the panel's face (+x), give or take the small turn towards the pilot.
        Assert.True(v.Camera.Forward.X > 0.9, "forward " + v.Camera.Forward);
    }

    [Fact]
    public void SurfaceNormal_IsThePanelsFace_AndZeroWithoutSurfaces()
    {
        CockpitScene scene = PanelScene();
        Vec3 n = CockpitViews.SurfaceNormal(scene, [new Vec3(0.9, 0, 0), new Vec3(0.9, 0.1, 0.1)], Head);
        Assert.True(n.X < -0.99, n.ToString());
        Assert.True(CockpitViews.SurfaceNormal(new CockpitScene(), [new Vec3(0.9, 0, 0)], Head).Length < 0.5);
    }

    private static CockpitCallout Cmd(string label, string command, double z) =>
        new() { Id = "m" + label.GetHashCode(), Label = label, Position = new Vec3(0.3, 0.4, z), Commands = [new CalloutCommand { Id = command, Name = command }] };

    [Fact]
    public void ControlSystems_NameTheSystemFromTheCommandsAndNames()
    {
        Assert.Equal("Autopilot", ControlSystems.Of(Cmd("CMD A", "laminar/B738/autopilot/cmd_a_press", 0)));
        Assert.Equal("CDU (flight management)", ControlSystems.Of(Cmd("LEGS", "sim/FMS/legs", 0)));
        Assert.Equal("Radios", ControlSystems.Of(Cmd("Mhz Knob R Comm", "laminar/B738/rtp_R/freq_MHz/sel_dial_up", 0)));
        Assert.Equal("Flight instruments", ControlSystems.Of(Cmd("Captain Chrono Start Stop Reset", "sim/instruments/chrono1_cycle", 0)));
        Assert.Equal("Electrical", ControlSystems.Of(new CockpitCallout { Id = "ELECTRICAL_Switch_Battery_Master", Label = "Battery master" }));
        // A generic folder says nothing; the button's own name does.
        Assert.Equal("Warnings", ControlSystems.Of(Cmd("Master Caution Accept", "laminar/B738/push_button/master_caution1", 0)));
        Assert.Equal(string.Empty, ControlSystems.Of(Cmd("IFE / PASS Seat Switch", "laminar/B738/switch/ife_pass_seat_off", 0)));
    }

    [Fact]
    public void ControlSystems_GroupBySystem_AndASystemWithFewControlsStaysWithItsPlace()
    {
        List<CockpitCallout> cs =
        [
            Cmd("CMD A", "laminar/B738/autopilot/cmd_a_press", 0),
            Cmd("CMD B", "laminar/B738/autopilot/cmd_b_press", 0.02),
            Cmd("VNAV", "laminar/B738/autopilot/vnav_press", 0.04),
            Cmd("Battery", "sim/electrical/battery_1_on", 0.06),
        ];
        ControlSystems.Assign(cs, Head, a => "Place " + a);
        Assert.All(cs.Take(3), c => Assert.Equal("Autopilot", c.PanelTitle));
        Assert.Equal(cs[0].Panel, cs[2].Panel);
        Assert.StartsWith("Place ", cs[3].PanelTitle);
    }

    [Fact]
    public void Quality_BigBarePolygonsAreFlatShapes_AGrainyPanelIsNot()
    {
        const int w = 320, h = 200;
        Random rnd = new(7);
        byte[] shards = new byte[w * h * 4], panel = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                // Shards: three big flat triangles-ish regions of beige, grey and black, a little detail in one corner.
                byte v = (byte)(x + y < 200 ? 190 : x > y + 60 ? 120 : 40);
                if (x < 40 && y < 40)
                    v = (byte)rnd.Next(30, 220);
                shards[o] = shards[o + 1] = shards[o + 2] = v;
                shards[o + 3] = 255;
                // A panel: grain everywhere (a texture), switches as bright spots.
                byte p = (byte)(90 + rnd.Next(0, 24) + ((x / 20 + y / 20) % 5 == 0 ? 80 : 0));
                panel[o] = panel[o + 1] = panel[o + 2] = p;
                panel[o + 3] = 255;
            }
        }
        PictureMetrics a = PictureQuality.Measure(shards, w, h, 4);
        PictureQuality.Rate(a, "panel");
        Assert.Contains("flat shapes", a.Problems);
        Assert.True(a.Score < PictureQuality.Good, "score " + a.Score);
        PictureMetrics b = PictureQuality.Measure(panel, w, h, 4);
        PictureQuality.Rate(b, "panel");
        Assert.DoesNotContain("flat shapes", b.Problems);
        Assert.True(b.Score >= PictureQuality.Good, "score " + b.Score);
    }

    [Fact]
    public void Expose_LiftsTheMiddleTonesOfADarkPicture_AndLeavesABrightOneAlone()
    {
        byte[] dark = new byte[100 * 4];
        for (int i = 0; i < 100; i++)
        {
            byte v = (byte)(i < 95 ? 40 + i % 10 : 230);
            dark[4 * i] = dark[4 * i + 1] = dark[4 * i + 2] = v;
            dark[4 * i + 3] = 255;
        }
        PictureWriter.Expose(dark);
        List<int> lum = Enumerable.Range(0, 100).Select(i => (int)dark[4 * i]).OrderBy(x => x).ToList();
        Assert.InRange(lum[50], 80, 130);
        Assert.True(lum[99] >= 225);

        byte[] bright = new byte[100 * 4];
        for (int i = 0; i < 100; i++)
        {
            byte v = (byte)(130 + i);
            bright[4 * i] = bright[4 * i + 1] = bright[4 * i + 2] = v;
            bright[4 * i + 3] = 255;
        }
        byte[] before = (byte[])bright.Clone();
        PictureWriter.Expose(bright);
        Assert.Equal(before, bright);
    }
}
