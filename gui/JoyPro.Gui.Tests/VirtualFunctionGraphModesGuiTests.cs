using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using JoyPro.Engine.VJoy;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Functions tab's Joystick Gremlin parts against the fake engine (docs/virtual-joystick.md "Modes", "Function
// chaining", "Coming from Joystick Gremlin"): the modes card (add, parent, start, rename, remove, the live mode), the
// editor's modes and function outputs as input sources, the editable graph (connect by dragging, refuse a wrong kind
// or a loop, select and disconnect a wire, remove, open), the Gremlin import (dry run with its report, then the import)
// and the vJoy device JoyPro tunnels into on every vjoy command. Renders of the tab at 1280 and 2267, dark and light,
// go to JOYPRO_VJOY_RENDER_OUT (else %TEMP%\JoyPro2.Gui.Tests\vjoy-renders).
[SupportedOSPlatform("windows")]
public sealed class VirtualFunctionGraphModesGuiTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "vfg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string RenderFolder
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("JOYPRO_VJOY_RENDER_OUT");
            return string.IsNullOrWhiteSpace(configured) ? Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "vjoy-renders") : configured;
        }
    }

    // The fake engine with a second DCS function: a toggle whose input is free (chaining targets), and a curve.
    internal static VfFakeEngine EngineWithChain()
    {
        VfFakeEngine engine = new();
        VirtualFunction toggle = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.Toggle, "Afterburner latch");
        toggle.Id = "f2";
        toggle.OutputOf("out")!.Target = "JOY_BTN40";
        toggle.OutputOf("out")!.VJoyDevice = 1;
        VirtualFunction curve = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.Curve, "Rudder curve");
        curve.Id = "f3";
        curve.OutputOf("out")!.Target = "JOY_RZ";
        curve.OutputOf("out")!.VJoyDevice = 1;
        engine.FunctionsOf("DCS").Add(toggle);
        engine.FunctionsOf("DCS").Add(curve);
        return engine;
    }

    [Fact]
    public async Task ModesCardManagesTheGamesModes()
    {
        (MainWindowViewModel vm, VfFakeEngine engine, List<ConfirmRequest> confirms) = await VirtualFunctionsGuiTests.CreateVmAsync(folder);
        Assert.False(vm.HasVirtualModes);
        Assert.Equal("Modes of DCS World", vm.VirtualModesHeader);
        vm.NewVirtualModeName = "Default";
        await vm.AddVirtualModeCommand.ExecuteAsync(null);
        vm.NewVirtualModeName = "Combat";
        vm.NewVirtualModeParent = "Default";
        await vm.AddVirtualModeCommand.ExecuteAsync(null);
        Assert.Contains(engine.Calls, c => VirtualJoystickTests.Seq(c, "vjoy", "modes", "add", "--game", "DCS", "--name", "Combat", "--parent", "Default"));
        Assert.Equal(["Default", "Combat"], vm.VirtualFunctionModes.Select(m => m.Name).ToArray());
        Assert.Equal("Default", vm.VirtualFunctionModes[1].ParentName);
        Assert.True(vm.VirtualFunctionModes[0].IsStart);
        Assert.Equal(string.Empty, vm.NewVirtualModeName);
        // A mode cannot inherit from one that inherits from it.
        Assert.DoesNotContain("Combat", vm.VirtualFunctionModes[0].ParentOptions);

        await vm.SetVirtualStartModeCommand.ExecuteAsync(vm.VirtualFunctionModes[1]);
        Assert.True(vm.VirtualFunctionModes.Single(m => m.Name == "Combat").IsStart);
        vm.VirtualFunctionModes.Single(m => m.Name == "Combat").EditName = "Dogfight";
        await vm.RenameVirtualModeCommand.ExecuteAsync(vm.VirtualFunctionModes.Single(m => m.Name == "Combat"));
        Assert.Contains(engine.Calls, c => VirtualJoystickTests.Seq(c, "vjoy", "modes", "set", "--game", "DCS", "--name", "Combat", "--rename", "Dogfight"));
        vm.VirtualFunctionModes.Single(m => m.Name == "Dogfight").Parent = VfModeItem.None;
        Assert.Contains(engine.Calls, c => VirtualJoystickTests.Seq(c, "vjoy", "modes", "set", "--game", "DCS", "--name", "Dogfight", "--parent", ""));

        // The forwarder's live mode.
        vm.ApplyVirtualModeLive("mode DCS Dogfight");
        Assert.Contains("Dogfight", vm.VirtualFunctionCurrentModeText);
        Assert.True(vm.VirtualFunctionModes.Single(m => m.Name == "Dogfight").IsCurrent);

        await vm.RemoveVirtualModeCommand.ExecuteAsync(vm.VirtualFunctionModes.Single(m => m.Name == "Dogfight"));
        Assert.Contains(confirms, c => c.Title.Contains("Dogfight", StringComparison.Ordinal) && c.IsDanger);
        Assert.Equal(["Default"], vm.VirtualFunctionModes.Select(m => m.Name).ToArray());
    }

    [Fact]
    public async Task EditorOffersModesAndOtherFunctionsAsInputs()
    {
        VfFakeEngine engine = EngineWithChain();
        engine.Modes.Modes.Add(new VfMode { Name = "Default" });
        engine.Modes.Modes.Add(new VfMode { Name = "Combat", Parent = "Default" });
        (MainWindowViewModel vm, _, _) = await VirtualFunctionsGuiTests.CreateVmAsync(folder, engine);
        vm.EditVirtualFunctionCommand.Execute(vm.VirtualFunctions.Single(f => f.Id == "f2"));
        VirtualFunctionEditorViewModel editor = vm.VirtualFunctionEditor!;
        Assert.False(vm.VirtualFunctionShowGraph);
        Assert.True(editor.HasModes);
        Assert.Equal(["Default", "Combat"], editor.ModeChoices.Select(m => m.Name).ToArray());
        // The detents' bands are offered as button sources; the curve's axis is not (a toggle takes a button).
        VfInputRoleItem input = editor.Inputs.Single();
        VfDeviceOption detents = input.Devices.Single(d => d.Device == "fn:f1");
        Assert.True(detents.IsFunction);
        input.SelectedDevice = detents;
        Assert.Equal(["BAND1", "BAND2", "BAND3"], input.InputOptions.ToArray());
        Assert.DoesNotContain(input.Devices, d => d.Device == "fn:f2"); // never itself
        input.SelectedInput = "BAND2";
        Assert.True(input.IsChained);
        editor.ModeChoices.Single(m => m.Name == "Combat").IsChecked = true;
        Assert.Contains("Combat", editor.ModesSummary);
        VirtualFunction edited = editor.ToFunction();
        Assert.Equal(("fn:f1", "BAND2"), (edited.InputOf("in")!.Device, edited.InputOf("in")!.Input));
        Assert.Equal(["Combat"], edited.Modes);
        Assert.False(editor.HasProblems);

        // The detents' editor: band2 has no vJoy target and feeds the toggle only (after the save above).
        await vm.SaveVirtualFunctionCommand.ExecuteAsync(null);
        vm.EditVirtualFunctionCommand.Execute(vm.VirtualFunctions.Single(f => f.Id == "f1"));
        VfOutputRoleItem band2 = vm.VirtualFunctionEditor!.Outputs.Single(o => o.Role == "band2");
        Assert.Equal("feeds f2 only", band2.ChainNote);
        Assert.DoesNotContain(vm.VirtualFunctionEditor.Inputs.Single().Devices, d => d.Device == "fn:f2"); // f2 reads f1: offering it would make a loop

        // A mode switch picks its target modes.
        VirtualFunction mode = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.ModeSwitch, "Combat on the pinky");
        mode.Id = "f9";
        VirtualFunctionEditorViewModel modeEditor = new("DCS", "DCS World", mode, vm.VirtualFunctionDevices, false, null, vm.ModesOfGame("DCS"), vm.VirtualFunctionGraph);
        Assert.True(modeEditor.ShowSwitchModes);
        Assert.Contains(modeEditor.Problems, p => p.Contains("no mode chosen"));
        modeEditor.SwitchModeChoices.Single(m => m.Name == "Combat").IsChecked = true;
        Assert.Equal(["Combat"], modeEditor.ToFunction().SwitchModes);
        Assert.DoesNotContain(modeEditor.Problems, p => p.Contains("no mode chosen"));
    }

    [Fact]
    public void GraphConnectsDisconnectsAndRefusesWhatCannotWork()
    {
        AvaloniaRenderThread.Run(() =>
        {
            (MainWindowViewModel vm, VfFakeEngine engine, List<ConfirmRequest> confirms) = AvaloniaRenderThread.Wait(VirtualFunctionsGuiTests.CreateVmAsync(folder, EngineWithChain()));
            VirtualFunctionGraphView graph = new() { Host = vm, Functions = vm.VirtualFunctionGraph };
            Assert.Equal(3, graph.Nodes.Count);
            Assert.Empty(graph.Wires);
            VfGraphNode detents = graph.Nodes.Single(n => n.Function.Id == "f1");
            VfGraphNode latch = graph.Nodes.Single(n => n.Function.Id == "f2");
            VfGraphNode curve = graph.Nodes.Single(n => n.Function.Id == "f3");

            // A band (a button) onto the curve's axis input: nothing happens.
            VfGraphPort band2 = detents.Outputs.Single(p => p.Role == "band2");
            graph.PressAt(band2.Anchor, 1);
            Assert.False(AvaloniaRenderThread.Wait(graph.ReleaseAt(curve.Inputs.Single().Anchor)));
            Assert.DoesNotContain(engine.Calls, c => c.Contains("--input"));

            // The band onto the toggle: connected through the engine, the graph shows the wire and a second column.
            graph.PressAt(band2.Anchor, 1);
            graph.MoveTo(new Point(latch.Bounds.X + 20, latch.Inputs.Single().Anchor.Y));
            Assert.True(AvaloniaRenderThread.Wait(graph.ReleaseAt(latch.Inputs.Single().Anchor)));
            Assert.Contains(engine.Calls, c => VirtualJoystickTests.Seq(c, "vjoy", "functions", "set", "--game", "DCS", "--id", "f2", "--input", "in=fn:f1|BAND2"));
            graph.Functions = vm.VirtualFunctionGraph;
            VfGraphWire wire = Assert.Single(graph.Wires);
            Assert.Equal(("f1", "band2", "f2", "in"), (wire.From.Node.Function.Id, wire.From.Role, wire.To.Node.Function.Id, wire.To.Role));
            Assert.Equal(1, graph.Nodes.Single(n => n.Function.Id == "f2").Column);
            Assert.Contains("f2", vm.VirtualFunctions.Single(f => f.Id == "f1").ChainText);

            // The toggle's output back into the detents would be a loop: refused here, the engine is not asked.
            int calls = engine.Calls.Count;
            Assert.False(AvaloniaRenderThread.Wait(vm.ConnectVirtualFunctionsAsync("f2", "out", "f1", "in")));
            Assert.Equal(calls, engine.Calls.Count); // and an axis input refuses a button anyway
            VirtualFunction loopTarget = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.Toggle, "Back");
            loopTarget.Id = "f4";
            loopTarget.InputOf("in")!.Device = "fn:f2";
            loopTarget.InputOf("in")!.Input = "OUT";
            engine.FunctionsOf("DCS").Add(loopTarget);
            AvaloniaRenderThread.Wait(vm.RefreshVirtualJoystickCommand.ExecuteAsync(null));
            int inputCalls = engine.Calls.Count(c => c.Contains("--input"));
            Assert.False(AvaloniaRenderThread.Wait(vm.ConnectVirtualFunctionsAsync("f4", "out", "f2", "in")));
            Assert.Contains("loop", vm.VirtualFunctionMessage);
            Assert.Equal(inputCalls, engine.Calls.Count(c => c.Contains("--input")));

            // Select the wire by its input, then its × disconnects.
            graph.Functions = vm.VirtualFunctionGraph;
            VfGraphNode latchNow = graph.Nodes.Single(n => n.Function.Id == "f2");
            graph.PressAt(latchNow.Inputs.Single().Anchor, 1);
            Assert.NotNull(graph.SelectedWire);
            Point mid = VfGraphLayout.WirePoint(graph.SelectedWire!.From.Anchor, graph.SelectedWire.To.Anchor, 0.5);
            graph.PressAt(mid, 1);
            Assert.Contains(engine.Calls, c => VirtualJoystickTests.Seq(c, "vjoy", "functions", "set", "--game", "DCS", "--id", "f2", "--input", "in="));

            // Double-click opens the editor; the node's × removes (the page asks first).
            graph.Functions = vm.VirtualFunctionGraph;
            VfGraphNode curveNow = graph.Nodes.Single(n => n.Function.Id == "f3");
            graph.PressAt(new Point(curveNow.Bounds.X + 40, curveNow.Bounds.Y + 30), 2);
            Assert.Equal("f3", vm.VirtualFunctionEditor?.Id);
            graph.PressAt(curveNow.Close.Center, 1);
            Assert.Contains(confirms, c => c.Title.StartsWith("Remove Rudder curve", StringComparison.Ordinal));
            Assert.Contains(engine.Calls, c => VirtualJoystickTests.Seq(c, "vjoy", "functions", "remove", "--game", "DCS", "--id", "f3"));
        });
    }

    [Fact]
    public async Task GremlinImportShowsTheReportBeforeSaving()
    {
        (MainWindowViewModel vm, VfFakeEngine engine, _) = await VirtualFunctionsGuiTests.CreateVmAsync(folder);
        string profile = Path.Combine(folder, "gremlin", "profile.xml");
        vm.GremlinReplace = true;
        await vm.PreviewGremlinImportCommand.ExecuteAsync(profile);
        Assert.Contains(engine.Calls, c => VirtualJoystickTests.Seq(c, "vjoy", "import-gremlin", profile, "--game", "DCS", "--replace", "--dry-run"));
        Assert.True(vm.HasGremlinReport);
        Assert.Contains("Joystick Gremlin R13", vm.GremlinReportText);
        Assert.Contains("3 function(s), 2 mode(s)", vm.GremlinReportText);
        Assert.Contains("not found", vm.GremlinReportText);
        Assert.Equal(2, vm.GremlinNotImported.Count);
        Assert.Single(vm.GremlinNotes);
        await vm.ConfirmGremlinImportCommand.ExecuteAsync(null);
        IReadOnlyList<string> import = engine.Calls.Last(c => c.Contains("import-gremlin"));
        Assert.DoesNotContain("--dry-run", import);
        Assert.Contains("Imported 3 virtual function(s)", vm.VirtualFunctionMessage);
        Assert.False(vm.HasGremlinReport);

        await vm.PreviewGremlinImportCommand.ExecuteAsync(profile);
        vm.CancelGremlinImportCommand.Execute(null);
        Assert.False(vm.HasGremlinReport);
        Assert.Empty(vm.GremlinNotImported);
    }

    [Fact]
    public async Task TheTunnelDeviceGoesToEveryVJoyCommand()
    {
        VfFakeEngine engine = new();
        (MainWindowViewModel vm, _, _) = await VirtualFunctionsGuiTests.CreateVmAsync(folder, engine);
        Assert.Equal(string.Empty, vm.VirtualJoystickTunnelText); // the warning says it
        Assert.Contains("Choose vJoy 1", vm.VirtualJoystickTunnelWarnings);
        Assert.DoesNotContain(engine.Calls, c => c.Contains("--vjoy-tunnel"));
        vm.VJoyTunnelDevice = 2;
        engine.TunnelDevice = 2;
        await vm.RefreshVirtualJoystickCommand.ExecuteAsync(null);
        vm.NewVirtualModeName = "Combat";
        await vm.AddVirtualModeCommand.ExecuteAsync(null);
        Assert.Contains(engine.Calls, c => VirtualJoystickTests.Seq(c, "vjoy", "status") && VirtualJoystickTests.Seq(c, "--vjoy-tunnel", "2"));
        Assert.Contains(engine.Calls, c => VirtualJoystickTests.Seq(c, "vjoy", "modes", "add") && VirtualJoystickTests.Seq(c, "--vjoy-tunnel", "2"));
        Assert.Contains("tunnels into vJoy 2", vm.VirtualJoystickTunnelText);
        Assert.Equal(string.Empty, vm.VirtualJoystickTunnelWarnings);
    }

    // ---- renders of the changed views (look at them: docs/testing.md)

    [Fact]
    public void RenderFunctionsTabGraphEditorAndImport()
    {
        string outDir = RenderFolder;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            VfFakeEngine engine = EngineWithChain();
            engine.Modes.Modes.Add(new VfMode { Name = "Default" });
            engine.Modes.Modes.Add(new VfMode { Name = "Combat", Parent = "Default" });
            engine.Modes.Modes.Add(new VfMode { Name = "Landing", Parent = "Default" });
            engine.Modes.Start = "Combat";
            VirtualFunction toggle = engine.FunctionsOf("DCS").Single(f => f.Id == "f2");
            toggle.InputOf("in")!.Device = "fn:f1";
            toggle.InputOf("in")!.Input = "BAND2";
            toggle.Modes = ["Combat"];
            VirtualFunction pinky = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.ModeSwitch, "Landing while held");
            pinky.Id = "f4";
            pinky.Params["action"] = VirtualFunctionCatalog.ModeActionHold;
            pinky.SwitchModes = ["Landing"];
            pinky.InputOf("in")!.Device = VirtualFunctionsGuiTests.Stick;
            pinky.InputOf("in")!.Input = "JOY_BTN5";
            engine.FunctionsOf("DCS").Add(pinky);
            VirtualFunction remap = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.Remap, "Gear (Landing)");
            remap.Id = "f5";
            remap.Modes = ["Landing"];
            remap.InputOf("in")!.Device = "fn:f2";
            remap.InputOf("in")!.Input = "OUT";
            remap.OutputOf("out1")!.Target = "JOY_BTN41";
            remap.OutputOf("out1")!.VJoyDevice = 1;
            engine.FunctionsOf("DCS").Add(remap);
            (MainWindowViewModel vm, _, _) = AvaloniaRenderThread.Wait(VirtualFunctionsGuiTests.CreateVmAsync(folder, engine));
            vm.ApplyVirtualModeLive("mode DCS Combat");
            vm.VirtualFunctionMessage = "f2 \"Afterburner latch\" Button now reads the output band2 of Throttle detents.";
            Assert.Equal(3, vm.VirtualFunctionModes.Count);

            foreach ((string theme, bool light) in new[] { ("dark", false), ("light", true) })
            {
                RenderPage(vm, 2, Path.Combine(outDir, "vjoy-functions-graph-1280-" + theme + ".png"), new Size(1280, 900), light);
                RenderPage(vm, 2, Path.Combine(outDir, "vjoy-functions-graph-2267-" + theme + ".png"), new Size(2267, 1300), light);
            }

            // The editor of the chained toggle with its modes.
            vm.EditVirtualFunctionCommand.Execute(vm.VirtualFunctions.Single(f => f.Id == "f2"));
            Assert.True(vm.VirtualFunctionShowEditor);
            foreach ((string theme, bool light) in new[] { ("dark", false), ("light", true) })
            {
                RenderPage(vm, 2, Path.Combine(outDir, "vjoy-functions-editor-1280-" + theme + ".png"), new Size(1280, 1100), light);
                RenderPage(vm, 2, Path.Combine(outDir, "vjoy-functions-editor-2267-" + theme + ".png"), new Size(2267, 1300), light);
            }
            vm.EditVirtualFunctionCommand.Execute(vm.VirtualFunctions.Single(f => f.Id == "f4"));
            RenderPage(vm, 2, Path.Combine(outDir, "vjoy-functions-modeswitch-1280-dark.png"), new Size(1280, 1000), false);

            // The Gremlin report.
            vm.CloseVirtualFunctionEditorCommand.Execute(null);
            AvaloniaRenderThread.Wait(vm.PreviewGremlinImportCommand.ExecuteAsync(Path.Combine(folder, "profile.xml")));
            foreach ((string theme, bool light) in new[] { ("dark", false), ("light", true) })
                RenderPage(vm, 2, Path.Combine(outDir, "vjoy-functions-gremlin-1280-" + theme + ".png"), new Size(1280, 900), light);
            vm.CancelGremlinImportCommand.Execute(null);

            // The Overview's status with the tunnel note.
            foreach ((string theme, bool light) in new[] { ("dark", false), ("light", true) })
                RenderPage(vm, 0, Path.Combine(outDir, "vjoy-overview-1280-" + theme + ".png"), new Size(1280, 1500), light);
        });
        foreach (string file in Directory.GetFiles(outDir, "vjoy-*.png"))
            Assert.True(new FileInfo(file).Length > 5000, file);
    }

    private static void RenderPage(MainWindowViewModel vm, int tab, string file, Size size, bool light)
    {
        VirtualJoystickView view = new() { DataContext = vm };
        Border root = new() { Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(light ? "#F8FAFC" : "#111A2D")), Child = view };
        Window window = new()
        {
            Content = root,
            Width = size.Width,
            Height = size.Height,
            RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark
        };
        window.Measure(size);
        window.Arrange(new Rect(size));
        TabControl tabs = view.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = tab;
        for (int pass = 0; pass < 4; pass++)
        {
            root.Measure(size);
            root.Arrange(new Rect(size));
            window.UpdateLayout();
            AvaloniaRenderThread.Pump();
        }
        using RenderTargetBitmap bitmap = new(new PixelSize((int)size.Width, (int)size.Height));
        bitmap.Render(root);
        bitmap.Save(file);
    }
}
