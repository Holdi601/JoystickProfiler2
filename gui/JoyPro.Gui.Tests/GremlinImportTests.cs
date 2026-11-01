using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JoyPro.Engine.VJoy;
using Xunit;

namespace JoyPro.Gui.Tests;

// Joystick Gremlin profiles into virtual functions and modes (engine/VJoy/GremlinImport.cs, engine vjoy
// import-gremlin) on the fixtures in engine/tests/fixtures/gremlin: R13 remaps, curves, keys, merged axes and device
// matching; R13 modes with inheritance, mode changes and every container type; an R14 profile; the report of what
// JoyPro cannot do; and the imported functions running in the runtime (docs/virtual-joystick.md "Coming from Joystick
// Gremlin").
public sealed class GremlinImportTests
{
    private const string Stick = "VJ Stick {11111111-2222-3333-4444-000000000001}";
    private const string Throttle = "VJ Throttle {11111111-2222-3333-4444-000000000002}";
    private static readonly List<string> Known = [Stick, Throttle];

    private static string Fixture(string name)
    {
        DirectoryInfo? folder = new(AppContext.BaseDirectory);
        while (folder != null && !Directory.Exists(Path.Combine(folder.FullName, "engine", "tests", "fixtures", "gremlin")))
            folder = folder.Parent;
        Assert.NotNull(folder);
        return File.ReadAllText(Path.Combine(folder!.FullName, "engine", "tests", "fixtures", "gremlin", name));
    }

    private static VirtualFunction Single(GremlinImportResult result, string type, Func<VirtualFunction, bool>? where = null)
    {
        List<VirtualFunction> found = result.Functions.Where(f => f.Type == type && (where == null || where(f))).ToList();
        Assert.True(found.Count == 1, type + ": expected one, found " + found.Count + " (" + string.Join(", ", result.Functions.Select(f => f.Type + " " + f.Name)) + ")");
        return found[0];
    }

    private static bool Reads(VirtualFunction function, string device, string input) =>
        function.Inputs.Any(i => i.Device == device && i.Input == input);

    [Fact]
    public void R13RemapsCurvesKeysAndMergedAxes()
    {
        GremlinImportResult result = GremlinImporter.Import(Fixture("r13-basic.xml"), Known, 0, ["f1"]);
        Assert.StartsWith("Joystick Gremlin R13", result.Format);
        Assert.Empty(result.Modes); // one mode: no modes in JoyPro
        Assert.DoesNotContain(result.Functions, f => f.Id == "f1"); // used ids are skipped
        Assert.Equal("guid", result.Devices.Single(d => d.GremlinName == "VJ Stick").Match);
        GremlinDeviceMatch throttle = result.Devices.Single(d => d.GremlinName == "VJ Throttle");
        Assert.Equal(("name", Throttle), (throttle.Match, throttle.JoyProDevice));
        GremlinDeviceMatch pedals = result.Devices.Single(d => d.GremlinName.StartsWith("Saitek", StringComparison.Ordinal));
        Assert.Equal("none", pedals.Match);
        Assert.Equal("Saitek Pro Flight Rudder Pedals {BBBBBBBB-0000-0000-0000-00000000CAFE}", pedals.JoyProDevice);

        VirtualFunction curve = Single(result, VirtualFunctionCatalog.Curve);
        Assert.True(Reads(curve, Stick, "JOY_X"));
        Assert.Equal(("JOY_X", 1), (curve.OutputOf("out")!.Target, curve.OutputOf("out")!.VJoyDevice));
        Engine.Axis.AxisTuning tuning = Engine.Axis.AxisTuning.Parse(curve.Tuning);
        Assert.Equal(0.05, tuning.Deadzone, 3);
        Assert.True(tuning.IsUserCurve);
        Assert.Equal(25, tuning.Curvature[5], 0); // the spline's (0.5, 0.25) point
        Assert.Equal("Roll", curve.Name);

        VirtualFunction keys = Single(result, VirtualFunctionCatalog.Remap, f => Reads(f, Stick, "JOY_BTN2"));
        Assert.True(keys.Keyboard);
        Assert.Equal(["JOY_BTN2", "KEY_LSHIFT", "KEY_A"], keys.Outputs.Select(o => o.Target).ToArray());
        Assert.Equal("POV1", Single(result, VirtualFunctionCatalog.Remap, f => Reads(f, Stick, "POV1")).OutputOf("out1")!.Target);
        Assert.Equal("JOY_Z", Single(result, VirtualFunctionCatalog.Remap, f => Reads(f, Throttle, "JOY_Z")).OutputOf("out1")!.Target);
        Assert.Equal("JOY_RZ", Single(result, VirtualFunctionCatalog.Remap, f => Reads(f, pedals.JoyProDevice, "JOY_Z")).OutputOf("out1")!.Target);

        VirtualFunction merge = Single(result, VirtualFunctionCatalog.Merge);
        Assert.Equal(1, merge.Param("mode", -1)); // sum
        Assert.Equal("JOY_SLIDER1", merge.OutputOf("out")!.Target);
        Assert.True(Reads(merge, pedals.JoyProDevice, "JOY_X") && Reads(merge, pedals.JoyProDevice, "JOY_Y"));

        Assert.Contains(result.NotImported, n => n.Contains("map-to-mouse") && n.Contains("no mouse"));
        Assert.Contains(result.NotImported, n => n.Contains("plugins") || n.Contains("Python"));
        Assert.Contains(result.NotImported, n => n.Contains("not connected"));
        Assert.Contains(result.Notes, n => n.Contains("relative axis remap"));
        Assert.Contains(result.Notes, n => n.Contains("axis scaling 0.5"));
        foreach (VirtualFunction function in result.Functions)
            Assert.Empty(VirtualFunctionCatalog.Validate(function, result.Functions, VfChain.ChainedRoles(function, result.Functions)));
    }

    [Fact]
    public void R13ModesContainersAndMacrosBecomeFunctions()
    {
        GremlinImportResult result = GremlinImporter.Import(Fixture("r13-modes.xml"), Known, 0, []);
        Assert.Equal(["Default", "Combat", "Landing"], result.Modes.Select(m => m.Name).ToArray());
        Assert.Equal(["", "Default", "Default"], result.Modes.Select(m => m.Parent).ToArray());
        Assert.Equal("Combat", result.StartMode);
        Assert.All(result.Functions, f => Assert.Single(f.Modes));

        VirtualFunction hold = Single(result, VirtualFunctionCatalog.ModeSwitch, f => Reads(f, Stick, "JOY_BTN5"));
        Assert.Equal(VirtualFunctionCatalog.ModeActionHold, hold.Param("action", -1));
        Assert.Equal(["Landing"], hold.SwitchModes);
        Assert.Equal(["Default"], hold.Modes);
        VirtualFunction cycle = Single(result, VirtualFunctionCatalog.ModeSwitch, f => Reads(f, Stick, "JOY_BTN6"));
        Assert.Equal(["Default", "Combat", "Landing"], cycle.SwitchModes);
        Assert.Equal(VirtualFunctionCatalog.ModeActionPrevious, Single(result, VirtualFunctionCatalog.ModeSwitch, f => Reads(f, Stick, "JOY_BTN4")).Param("action", -1));

        // Tempo: short straight on vJoy 10, long feeds the macro.
        VirtualFunction tempo = Single(result, VirtualFunctionCatalog.Presses, f => Reads(f, Stick, "JOY_BTN7"));
        Assert.Equal(600, tempo.Param("longMs", 0));
        Assert.Equal("JOY_BTN10", tempo.OutputOf("short")!.Target);
        Assert.Equal("", tempo.OutputOf("long")!.Target);
        VirtualFunction macro = Single(result, VirtualFunctionCatalog.Macro);
        Assert.Equal((VfChain.Device(tempo.Id), "LONG"), (macro.InputOf("trigger")!.Device, macro.InputOf("trigger")!.Input));
        Assert.True(macro.Keyboard);
        Assert.Equal(["KEY_LCTRL press 0", "out1 press 100", "out1 release 50", "KEY_LCTRL release 0"], macro.Steps.Select(s => s.Output + " " + s.Action + " " + s.DelayMs).ToArray());
        Assert.Equal("JOY_BTN11", macro.OutputOf("out1")!.Target);

        VirtualFunction chain = Single(result, VirtualFunctionCatalog.Cycle);
        Assert.Equal(["JOY_BTN12", "JOY_BTN13", "JOY_BTN14"], chain.Outputs.Select(o => o.Target).ToArray());
        Assert.Contains(result.NotImported, n => n.Contains("timeout"));
        Assert.Equal("JOY_BTN15", Single(result, VirtualFunctionCatalog.Toggle).OutputOf("out")!.Target);
        VirtualFunction doubleTap = Single(result, VirtualFunctionCatalog.Presses, f => Reads(f, Stick, "JOY_BTN10"));
        Assert.Equal("JOY_BTN16", doubleTap.OutputOf("short")!.Target);
        Assert.Equal(300, doubleTap.Param("doubleMs", 0));
        VirtualFunction toCombat = Single(result, VirtualFunctionCatalog.ModeSwitch, f => f.InputOf("in")!.Device == VfChain.Device(doubleTap.Id));
        Assert.Equal(["Combat"], toCombat.SwitchModes);
        VirtualFunction hat = Single(result, VirtualFunctionCatalog.HatButtons);
        Assert.Equal(["JOY_BTN20", "JOY_BTN21", "JOY_BTN22", "JOY_BTN23"], hat.Outputs.Select(o => o.Target).ToArray());

        // Axis ranges: band functions; the idle range remaps, the top range switches to Landing through a chain.
        List<VirtualFunction> bands = result.Functions.Where(f => f.Type == VirtualFunctionCatalog.Bands).ToList();
        Assert.Equal(2, bands.Count);
        Assert.Equal([0.1], bands[0].Edges);
        Assert.Equal([0.95], bands[1].Edges);
        Assert.Equal("JOY_BTN31", Single(result, VirtualFunctionCatalog.Remap, f => f.InputOf("in")!.Device == VfChain.Device(bands[0].Id)).OutputOf("out1")!.Target);
        VirtualFunction toLanding = Single(result, VirtualFunctionCatalog.ModeSwitch, f => f.InputOf("in")!.Device == VfChain.Device(bands[1].Id));
        Assert.Equal("BAND2", toLanding.InputOf("in")!.Input);

        Assert.Contains(result.NotImported, n => n.Contains("activation conditions"));
        Assert.Contains(result.NotImported, n => n.Contains("Keyboard (keyboard)"));
        Assert.Equal(string.Empty, VfChain.LoopProblem(result.Functions));
        foreach (VirtualFunction function in result.Functions)
            Assert.Empty(VirtualFunctionCatalog.Validate(function, result.Functions, VfChain.ChainedRoles(function, result.Functions)));
    }

    [Fact]
    public void AnImportedProfileRunsWithItsModes()
    {
        GremlinImportResult result = GremlinImporter.Import(Fixture("r13-modes.xml"), Known, 0, []);
        List<string> sources = [Stick, Throttle];
        foreach (VirtualFunction function in result.Functions)
            Assert.Empty(VfResolver.Resolve(function, sources, [1]));
        VfModeSet modes = new() { Group = "DCS", Modes = result.Modes, Start = result.StartMode };
        VfRuntime rt = new(result.Functions, sources.Count, 1, 0, 32767, null, [modes]);
        Assert.Empty(rt.Problems);
        static bool On(VfRuntime r, int button) => (r.Buttons[(button - 1) >> 5] & (1 << ((button - 1) & 31))) != 0;
        void Press(int button, bool down) => rt.State(0)[VfRuntime.ButtonOffset + button - 1] = down ? (byte)0x80 : (byte)0;

        Assert.Equal("Combat", rt.CurrentModeName(0));
        Press(1, true);
        rt.Tick(0);
        Assert.True(On(rt, 30)); // Combat's trigger
        Assert.False(On(rt, 1));
        Press(5, true); // hold Landing
        rt.Tick(10_000);
        rt.Tick(20_000);
        Assert.Equal("Landing", rt.CurrentModeName(0));
        Assert.True(On(rt, 1)); // Landing inherits Default's trigger
        Press(5, false);
        rt.Tick(30_000);
        Assert.Equal("Combat", rt.CurrentModeName(0));
        rt.Tick(35_000);
        // The throttle at the top of its travel switches to Landing (band -> mode switch, chained).
        VfRuntime.WriteInt(rt.State(1), VfRuntime.AxisOffset + 2 * 4, 32767);
        rt.Tick(40_000);
        Assert.Equal("Landing", rt.CurrentModeName(0));
    }

    [Fact]
    public void R14ProfilesAreReadThroughTheirLibrary()
    {
        GremlinImportResult result = GremlinImporter.Import(Fixture("r14-profile.xml"), Known, 3, []);
        Assert.StartsWith("Joystick Gremlin R14", result.Format);
        Assert.Equal(["Default", "Gear"], result.Modes.Select(m => m.Name).ToArray());
        Assert.Equal("Default", result.Modes.Single(m => m.Name == "Gear").Parent);

        VirtualFunction remap = Single(result, VirtualFunctionCatalog.Remap, f => Reads(f, Stick, "JOY_BTN3"));
        Assert.Equal(["JOY_BTN40", "KEY_SPACE"], remap.Outputs.Select(o => o.Target).ToArray());
        Assert.Equal(3, remap.OutputOf("out1")!.VJoyDevice); // --vjoy-device 3: the profile's vJoy 1 moves to 3
        VirtualFunction tempo = Single(result, VirtualFunctionCatalog.Presses);
        Assert.Equal(["Gear"], tempo.Modes);
        Assert.Equal(500, tempo.Param("longMs", 0));
        Assert.Equal("JOY_BTN41", tempo.OutputOf("short")!.Target);
        Assert.Equal(["Default"], Single(result, VirtualFunctionCatalog.ModeSwitch).SwitchModes);
        VirtualFunction curve = Single(result, VirtualFunctionCatalog.Curve);
        Assert.True(Reads(curve, Stick, "JOY_Y"));
        Assert.Equal("JOY_RY", curve.OutputOf("out")!.Target); // vJoy axis 5
        Assert.Equal(0.1, Engine.Axis.AxisTuning.Parse(curve.Tuning).Deadzone, 3);
        VirtualFunction hatDirection = Single(result, VirtualFunctionCatalog.Remap, f => Reads(f, Stick, "JOY_BTN_POV1_U"));
        Assert.Equal("JOY_BTN42", hatDirection.OutputOf("out1")!.Target);
        Assert.Contains(result.Notes, n => n.Contains("hat direction north only"));
        Assert.Contains(result.NotImported, n => n.Contains("play-sound"));
        Assert.Contains(result.NotImported, n => n.Contains("Keyboard input"));
    }

    [Fact]
    public void NotAGremlinProfileIsRefused()
    {
        Assert.Throws<ArgumentException>(() => GremlinImporter.Import("<notxml", Known, 0, []));
        Assert.Throws<ArgumentException>(() => GremlinImporter.Import("<profile version=\"9\"/>", Known, 0, []));
        Assert.Throws<ArgumentException>(() => GremlinImporter.Import("<settings/>", Known, 0, []));
    }
}
