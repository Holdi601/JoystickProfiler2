using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using JoyPro.Engine.VJoy;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// Virtual functions on the Virtual Joystick page (docs/virtual-joystick.md "Virtual functions"): the list from the
// engine's status, Add (engine picks the outputs), the editor (inputs, outputs, band edges, preview with the
// forwarder's evaluation, live "fn" lines), Save (the spec the engine gets), Remove, binding a relation to an output,
// "Set up vJoy for maximum" (confirm with the restart note; never run for real) and the per-game limit notes. The
// editors are rendered offscreen to PNG (%TEMP%\JoyPro2.Gui.Tests\vjoy-functions or JOYPRO_VJOY_FUNCTIONS_PREVIEW).
// Nothing here starts JoyPro.VJoy or touches vJoy.
[SupportedOSPlatform("windows")]
public sealed class VirtualFunctionsGuiTests : IDisposable
{
    internal const string Stick = "VKB Stick {11111111-2222-3333-4444-000000000001}";
    internal const string Throttle = "VPC Throttle {11111111-2222-3333-4444-000000000002}";

    private readonly string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "vf-" + Guid.NewGuid().ToString("N"));

    public static string OutputFolder
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("JOYPRO_VJOY_FUNCTIONS_PREVIEW");
            return string.IsNullOrWhiteSpace(configured) ? Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "vjoy-functions") : configured;
        }
    }

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

    internal static async Task<(MainWindowViewModel Vm, VfFakeEngine Engine, List<ConfirmRequest> Confirms)> CreateVmAsync(string folder, VfFakeEngine? engine = null)
    {
        engine ??= new VfFakeEngine();
        List<ConfirmRequest> confirms = [];
        MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            VirtualJoystickDispatch = action => action(),
            VirtualJoystickPollInterval = TimeSpan.FromHours(1),
            VirtualJoystickDataFolder = Path.Combine(folder, "data"),
            VirtualJoystickFindGame = _ => null,
            VirtualJoystickProcessPath = _ => null,
            ConfirmHandler = request =>
            {
                confirms.Add(request);
                return Task.FromResult(true);
            }
        };
        vm.EnginePath = typeof(MainWindowViewModel).Assembly.Location;
        vm.ProfilePath = Path.Combine(folder, "vf.pr0file");
        await vm.RefreshVirtualJoystickCommand.ExecuteAsync(null);
        return (vm, engine, confirms);
    }

    [Fact]
    public async Task StatusFillsTheListLimitsAndTheMaximumNote()
    {
        (MainWindowViewModel vm, _, _) = await CreateVmAsync(folder);
        VirtualFunctionListItem item = Assert.Single(vm.VirtualFunctions);
        Assert.Equal("Throttle detents", item.Name);
        Assert.Equal("Axis bands", item.TypeName);
        Assert.Equal("≡", item.Icon);
        Assert.Contains("band1 vJoy 1 Button 33", item.OutputsText);
        Assert.Equal(2, vm.VirtualFunctionDevices.Count);
        Assert.Equal("DCS", vm.VirtualFunctionGame?.Id);
        VirtualJoystickGameItem dcs = vm.VirtualJoystickGames.First(g => g.Id == "DCS");
        Assert.Contains("128 buttons", dcs.InputLimitText);
        Assert.Equal("1 virtual function", dcs.FunctionsText);
        VirtualJoystickGameItem arma = vm.VirtualJoystickGames.First(g => g.Id == "Arma2OA");
        Assert.True(arma.HasLimitWarnings);
        Assert.Contains("reads only the first 32 buttons", arma.LimitWarningsText);
        Assert.False(vm.VJoyAtMaximum);
        Assert.Contains("32 buttons", vm.VJoyMaximumText);
        Assert.True(vm.CanStartVirtualJoystick);
    }

    [Fact]
    public async Task AddAsksTheEngineAndOpensTheEditor()
    {
        (MainWindowViewModel vm, VfFakeEngine engine, _) = await CreateVmAsync(folder);
        vm.NewVirtualFunctionType = vm.VirtualFunctionTypes.First(t => t.Id == VirtualFunctionCatalog.Cycle);
        Assert.True(vm.CanAddVirtualFunction);
        await vm.AddVirtualFunctionCommand.ExecuteAsync(null);
        Assert.Contains(engine.Calls, c => VirtualJoystickTests.Seq(c, "vjoy", "functions", "add", "--game", "DCS", "--type", "cycle"));
        Assert.NotNull(vm.VirtualFunctionEditor);
        Assert.Equal("f2", vm.VirtualFunctionEditor!.Id);
        Assert.Equal(3, vm.VirtualFunctionEditor.Outputs.Count);
        Assert.Equal("JOY_BTN34", vm.VirtualFunctionEditor.Outputs[0].TargetToken);
        Assert.Equal(2, vm.VirtualFunctions.Count);
    }

    [Fact]
    public async Task EditorEditsPreviewsAndSaves()
    {
        (MainWindowViewModel vm, VfFakeEngine engine, _) = await CreateVmAsync(folder);
        vm.EditVirtualFunctionCommand.Execute(vm.VirtualFunctions[0]);
        VirtualFunctionEditorViewModel editor = vm.VirtualFunctionEditor!;
        Assert.True(editor.ShowBandEditor);
        Assert.Equal([0.1, 0.9], editor.EdgeValues);
        // The input: the throttle's Z.
        VfInputRoleItem input = editor.Inputs.Single();
        Assert.Equal(Throttle, input.SelectedDevice?.Device);
        Assert.Contains("JOY_Z", input.InputOptions);
        Assert.DoesNotContain("JOY_BTN1", input.InputOptions); // an axis role offers axes only
        // Dragging an edge stays between its neighbours.
        editor.MoveEdge(0, 0.95);
        Assert.Equal(0.89, editor.EdgeValues[0], 3);
        editor.MoveEdge(0, 0.2);
        Assert.Equal(0.2, editor.EdgeValues[0], 3);
        editor.AddBandCommand.Execute(null);
        Assert.Equal(3, editor.Edges.Count);
        Assert.Equal(4, editor.Outputs.Count);
        Assert.Equal(string.Empty, editor.Outputs[3].TargetToken); // the new band has no output yet
        editor.Outputs[3].Target = "JOY_BTN36";
        // Preview: the axis at 95 % fires the last band (the rows were made anew for the new band).
        input = editor.Inputs.Single();
        input.PreviewValue = 0.95;
        editor.StepAt(10);
        Assert.Equal(3, editor.CurrentBand);
        Assert.True(editor.Outputs[3].IsOn);
        Assert.False(editor.Outputs[0].IsOn);
        Assert.Contains("band 4 of 4", editor.StateText);
        Assert.Equal(0.95, editor.DisplayInput, 3);
        Assert.Equal(4, editor.Bands.Count);
        Assert.True(editor.Bands[3].On && editor.Bands[3].Current);
        Assert.False(editor.HasProblems);
        editor.Name = "Detents";
        await vm.SaveVirtualFunctionCommand.ExecuteAsync(null);
        IReadOnlyList<string> set = engine.Calls.Last(c => VirtualJoystickTests.Seq(c, "vjoy", "functions", "set"));
        Assert.True(VirtualJoystickTests.Seq(set, "--game", "DCS", "--id", "f1"));
        string spec = set[set.ToList().IndexOf("--spec") + 1].TrimStart('@');
        VirtualFunction saved = JsonSerializer.Deserialize<VirtualFunction>(File.ReadAllText(spec))!;
        Assert.Equal("Detents", saved.Name);
        Assert.Equal(3, saved.Edges.Count);
        Assert.Equal(0.2, saved.Edges[0], 3);
        Assert.Equal("JOY_BTN36", saved.OutputOf("band4")!.Target);
        Assert.Equal(Throttle, saved.InputOf("in")!.Device);
        Assert.Equal("Detents", vm.VirtualFunctions[0].Name); // the engine's answer is shown
    }

    [Fact]
    public async Task LiveLinesDriveTheEditorAndTheList()
    {
        (MainWindowViewModel vm, _, _) = await CreateVmAsync(folder);
        vm.EditVirtualFunctionCommand.Execute(vm.VirtualFunctions[0]);
        VirtualFunctionEditorViewModel editor = vm.VirtualFunctionEditor!;
        // "fn <id> <input> <state> <active>": the axis at 50 %, band 2, output index 1 on.
        vm.ApplyVirtualFunctionLive("fn f1 500 1 2");
        Assert.True(editor.LiveActive);
        Assert.Equal(0.5, editor.DisplayInput, 3);
        Assert.Equal(1, editor.CurrentBand);
        Assert.True(editor.Outputs[1].IsOn);
        Assert.False(editor.Outputs[0].IsOn);
        Assert.Contains("1 output(s) on", vm.VirtualFunctions[0].LiveText);
        // The live state wins over the preview for a second.
        editor.Inputs[0].PreviewValue = 0.0;
        editor.StepAt(20);
        Assert.Equal(1, editor.CurrentBand);
    }

    [Fact]
    public async Task RemoveAsksFirstAndBindingUsesTheVJoyDevice()
    {
        (MainWindowViewModel vm, VfFakeEngine engine, List<ConfirmRequest> confirms) = await CreateVmAsync(folder);
        vm.EditVirtualFunctionCommand.Execute(vm.VirtualFunctions[0]);
        VirtualFunctionEditorViewModel editor = vm.VirtualFunctionEditor!;
        editor.BindRelation = "Afterburner detent";
        await editor.BindOutputCommand.ExecuteAsync(editor.Outputs[2]);
        Assert.Contains(engine.Calls, c => VirtualJoystickTests.Seq(c, "binds", "set", "Afterburner detent", "--device", "vJoy Device {89D63500-B53F-11f1-8002-444553540000}", "--input", "JOY_BTN35"));
        Assert.Contains("Export the game again", vm.VirtualFunctionMessage);

        await vm.DeleteVirtualFunctionCommand.ExecuteAsync(vm.VirtualFunctions[0]);
        Assert.Contains(confirms, c => c.Title.StartsWith("Remove Throttle detents", StringComparison.Ordinal) && c.IsDanger);
        Assert.Contains(engine.Calls, c => VirtualJoystickTests.Seq(c, "vjoy", "functions", "remove", "--game", "DCS", "--id", "f1"));
        Assert.Null(vm.VirtualFunctionEditor);
        Assert.Empty(vm.VirtualFunctions);
    }

    [Fact]
    public async Task SetUpForMaximumSaysWindowsMustRestart()
    {
        (MainWindowViewModel vm, VfFakeEngine engine, List<ConfirmRequest> confirms) = await CreateVmAsync(folder);
        await vm.SetUpVJoyMaximumCommand.ExecuteAsync(null);
        ConfirmRequest confirm = Assert.Single(confirms);
        Assert.Contains("128 buttons", confirm.Message);
        Assert.Contains("Windows must restart", confirm.Message);
        Assert.Contains("never restarts Windows", confirm.Message);
        Assert.Contains(engine.Calls, c => VirtualJoystickTests.Seq(c, "vjoy", "configure-vjoy", "--max"));
        Assert.Contains("restart", vm.VirtualJoystickMessage);
    }

    [Fact]
    public async Task EditorCountKeyboardAndShiftOutputs()
    {
        (MainWindowViewModel vm, _, _) = await CreateVmAsync(folder);
        VirtualFunction cycle = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.Cycle, "Radio presets");
        cycle.Id = "f9";
        VirtualFunctionEditorViewModel editor = new("DCS", "DCS World", cycle, vm.VirtualFunctionDevices, false);
        Assert.True(editor.HasCount);
        Assert.Equal("3", editor.CountText);
        editor.AddCountCommand.Execute(null);
        Assert.Equal(4, editor.Outputs.Count);
        editor.RemoveCountCommand.Execute(null);
        editor.RemoveCountCommand.Execute(null);
        editor.RemoveCountCommand.Execute(null);
        Assert.Equal(2, editor.Outputs.Count); // a cycle keeps two steps at least
        Assert.DoesNotContain(editor.Outputs[0].Targets, t => t.StartsWith("KEY_", StringComparison.Ordinal));
        editor.Keyboard = true;
        Assert.Contains(editor.Outputs[0].Targets, t => t == "KEY_F1");
        Assert.True(editor.ToFunction().Keyboard);

        VirtualFunction shift = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.Shift, "Pinky layer");
        shift.Id = "f10";
        shift.InputOf("in1")!.Device = Stick;
        shift.InputOf("in1")!.Input = "JOY_X";
        VirtualFunctionEditorViewModel shiftEditor = new("DCS", "DCS World", shift, vm.VirtualFunctionDevices, false);
        // The shifted output of an axis is an axis; the unshifted one (the slot) is not shown until set.
        VfOutputRoleItem output = Assert.Single(shiftEditor.Outputs);
        Assert.Equal("axis", output.Kind);
        Assert.Contains("JOY_SLIDER1", output.Targets);
        Assert.DoesNotContain("JOY_BTN1", output.Targets);
    }

    [Fact]
    public async Task XboxGameOffersPadOutputs()
    {
        (MainWindowViewModel vm, _, _) = await CreateVmAsync(folder);
        VirtualFunction toggle = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.Toggle, "Lamp");
        toggle.Id = "f11";
        VirtualFunctionEditorViewModel editor = new("HLLVietnam", "Hell Let Loose: Vietnam", toggle, vm.VirtualFunctionDevices, true);
        Assert.Contains("XB_A", editor.Outputs[0].Targets);
        Assert.DoesNotContain("JOY_BTN1", editor.Outputs[0].Targets);
        editor.Outputs[0].Target = "XB_Y";
        Assert.False(editor.Outputs[0].IsVJoyTarget);
        Assert.Equal(0, editor.ToFunction().OutputOf("out")!.VJoyDevice);
    }

    // ---- offscreen renders of the editors

    [Fact]
    public void RenderEditors()
    {
        string outDir = OutputFolder;
        Directory.CreateDirectory(outDir);
        List<VfDeviceOption> devices =
        [
            new(Stick, "VKB Stick", VfTokens.AxisTokens, 32, 1),
            new(Throttle, "VPC Throttle", ["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_SLIDER1"], 64, 2)
        ];
        AvaloniaRenderThread.Run(() =>
        {
            // Axis bands: a throttle with idle cutoff and afterburner gate, the preview in the last band.
            VirtualFunctionEditorViewModel bands = new("DCS", "DCS World", VfFakeEngine.Detents(), devices, false,
                ["Function \"Throttle detents\" (DCS): band3 sits on vJoy button 40; Arma 2 reads only the first 32 buttons of a joystick."]);
            bands.Inputs[0].PreviewValue = 0.96;
            bands.StepAt(10);
            Render(new AxisBandEditor { Editor = bands }, new Size(820, 170), Path.Combine(outDir, "vf-band-editor.png"));
            Render(new VirtualFunctionFlowView { Editor = bands }, new Size(820, 190), Path.Combine(outDir, "vf-flow-bands.png"));
            Assert.Equal(2, bands.CurrentBand);
            RenderView(bands, Path.Combine(outDir, "vf-editor-bands.png"), 1250);
            RenderView(bands, Path.Combine(outDir, "vf-editor-bands-light.png"), 1250, light: true);

            // Button cycle with live state.
            VirtualFunction cycle = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.Cycle, "Radio presets");
            cycle.Id = "f2";
            cycle.InputOf("next")!.Device = Stick;
            cycle.InputOf("next")!.Input = "JOY_BTN5";
            for (int i = 0; i < 3; i++)
                cycle.Outputs[i].Target = VfTokens.ButtonToken(33 + i);
            VirtualFunctionEditorViewModel cycleEditor = new("DCS", "DCS World", cycle, devices, false);
            cycleEditor.ApplyLive(1000, 1, 2);
            RenderView(cycleEditor, Path.Combine(outDir, "vf-editor-cycle.png"), 1250);

            // Short / long / double, axis curve, macro, shift layer, hat to buttons.
            VirtualFunction presses = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.Presses, "Waypoint");
            presses.Id = "f3";
            presses.OutputOf("short")!.Target = "JOY_BTN40";
            presses.OutputOf("long")!.Target = "JOY_BTN41";
            RenderView(new VirtualFunctionEditorViewModel("DCS", "DCS World", presses, devices, false), Path.Combine(outDir, "vf-editor-presses.png"), 1150);
            VirtualFunction curve = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.Curve, "Rudder curve");
            curve.Id = "f4";
            VirtualFunctionEditorViewModel curveEditor = new("DCS", "DCS World", curve, devices, false);
            curveEditor.Inputs[0].PreviewValue = 0.8;
            curveEditor.StepAt(5);
            RenderView(curveEditor, Path.Combine(outDir, "vf-editor-curve.png"), 1100);
            VirtualFunction macro = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.Macro, "Start-up");
            macro.Id = "f5";
            RenderView(new VirtualFunctionEditorViewModel("DCS", "DCS World", macro, devices, false), Path.Combine(outDir, "vf-editor-macro.png"), 1150);
            VirtualFunction shift = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.Shift, "Pinky layer");
            shift.Id = "f6";
            VirtualFunctionEditorViewModel.SetCount(shift, 3);
            RenderView(new VirtualFunctionEditorViewModel("DCS", "DCS World", shift, devices, false), Path.Combine(outDir, "vf-editor-shift.png"), 1150);
            VirtualFunction hat = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.HatButtons, "Hat as buttons");
            hat.Id = "f7";
            VirtualFunctionEditorViewModel hatEditor = new("DCS", "DCS World", hat, devices, false);
            hatEditor.Inputs[0].PreviewHatIndex = 2;
            hatEditor.StepAt(5);
            Assert.True(hatEditor.Outputs[0].IsOn && hatEditor.Outputs[1].IsOn);
            RenderView(hatEditor, Path.Combine(outDir, "vf-editor-hat.png"), 1150);
        });
        foreach (string file in Directory.GetFiles(outDir, "vf-*.png"))
            Assert.True(new FileInfo(file).Length > 2000, file);
    }

    // The page's Functions tab (needs the whole page; renders only when JOYPRO_VJOY_FUNCTIONS_PAGE_PREVIEW is set):
    //   set JOYPRO_VJOY_FUNCTIONS_PAGE_PREVIEW=1 & dotnet test gui/JoyPro.Gui.Tests --filter RenderFunctionsPage
    [Fact]
    public void RenderFunctionsPage()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOYPRO_VJOY_FUNCTIONS_PAGE_PREVIEW")))
            return;
        string outDir = OutputFolder;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            (MainWindowViewModel vm, _, _) = AvaloniaRenderThread.Wait(CreateVmAsync(folder));
            vm.EditVirtualFunctionCommand.Execute(vm.VirtualFunctions[0]);
            vm.VirtualFunctionEditor!.Inputs[0].PreviewValue = 0.5;
            vm.VirtualFunctionEditor.StepAt(5);
            vm.VirtualFunctionMessage = "Added the function f1 \"Throttle detents\" (Axis bands) for DCS World. Choose its inputs below; JoyPro picked free virtual outputs.";
            VirtualJoystickView view = new() { DataContext = vm };
            RenderPage(view, 2, Path.Combine(outDir, "vf-page-functions.png"), new Size(1500, 1300));
            RenderPage(new VirtualJoystickView { DataContext = vm }, 0, Path.Combine(outDir, "vf-page-overview.png"), new Size(1500, 1800));
        });
    }

    private static void RenderPage(VirtualJoystickView view, int tab, string file, Size size)
    {
        // On a solid background (text drawn on a transparent bitmap loses its antialiasing).
        Border root = new() { Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#111A2D")), Child = view };
        Window window = new() { Content = root, Width = size.Width, Height = size.Height, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark };
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

    // In a window (never shown): the application's theme styles the controls only inside a top level. Dark = the app's
    // navy pages (the theme follows Windows), light for the other look.
    private static void RenderView(VirtualFunctionEditorViewModel editor, string file, double height, bool light = false)
    {
        Size size = new(1100, height);
        Border root = new()
        {
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(light ? "#F8FAFC" : "#111A2D")),
            Padding = new Thickness(12),
            Child = new VirtualFunctionEditorView { DataContext = editor }
        };
        Window window = new()
        {
            Content = root,
            Width = size.Width,
            Height = size.Height,
            RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark
        };
        window.Measure(size);
        window.Arrange(new Rect(size));
        for (int pass = 0; pass < 3; pass++)
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

    private static void Render(Control control, Size size, string file)
    {
        control.Measure(size);
        control.Arrange(new Rect(size));
        using RenderTargetBitmap bitmap = new(new PixelSize((int)size.Width, (int)size.Height));
        bitmap.Render(control);
        bitmap.Save(file);
    }
}

// `engine vjoy status|functions|configure-vjoy` and `engine binds set` as the engine answers them, with one stored
// function (DCS throttle detents) to start with.
[SupportedOSPlatform("windows")]
internal sealed class VfFakeEngine : IEngineCliClient
{
    private readonly Dictionary<string, List<VirtualFunction>> functions = new(StringComparer.OrdinalIgnoreCase) { ["DCS"] = [Detents()] };
    private int nextButton = 34;

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public static VirtualFunction Detents()
    {
        VirtualFunction function = VirtualFunctionCatalog.Create(VirtualFunctionCatalog.Bands, "Throttle detents");
        function.Id = "f1";
        function.InputOf("in")!.Device = VirtualFunctionsGuiTests.Throttle;
        function.InputOf("in")!.Input = "JOY_Z";
        function.OutputOf("band1")!.Target = "JOY_BTN33";
        function.OutputOf("band1")!.VJoyDevice = 1;
        function.OutputOf("band2")!.Target = string.Empty;
        function.OutputOf("band3")!.Target = "JOY_BTN35";
        function.OutputOf("band3")!.VJoyDevice = 1;
        function.OutputOf("band3")!.Mode = VirtualFunctionCatalog.ModeEnter;
        return function;
    }

    public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add(arguments.ToArray());
        (int code, string output) = Answer(arguments.ToList());
        return Task.FromResult(new EngineCommandResult
        {
            ExitCode = code,
            Duration = TimeSpan.FromMilliseconds(1),
            StandardOutput = output,
            StandardError = code == 0 ? string.Empty : "failed",
            Arguments = arguments.ToArray()
        });
    }

    private static string Value(List<string> args, string name) => args[args.IndexOf(name) + 1];

    private (int, string) Answer(List<string> args)
    {
        if (args.Contains("binds"))
            return (0, Json(new { Message = "Bound." }));
        int at = args.IndexOf("vjoy");
        if (at < 0 || at + 1 >= args.Count)
            return (0, "{}");
        switch (args[at + 1])
        {
            case "status":
                return (0, Status());
            case "configure-vjoy":
                return (0, Json(new { Message = "Dry run: vJoyConfig 1 -f -a x y z rx ry rz sl0 sl1 -b 128 -p 4. Windows must restart before the new vJoy setup takes effect.", RestartNeeded = true }));
            case "functions":
            {
                string action = args[at + 2];
                List<VirtualFunction> list = functions.TryGetValue(Value(args, "--game"), out List<VirtualFunction>? found) ? found : functions[Value(args, "--game")] = [];
                if (action == "add")
                {
                    VirtualFunction added = VirtualFunctionCatalog.Create(Value(args, "--type"), null!);
                    added.Id = "f" + (functions.Values.Sum(l => l.Count) + 1);
                    foreach (VfOutput output in added.Outputs)
                    {
                        output.Target = VfTokens.ButtonToken(nextButton++ - 1);
                        output.VJoyDevice = 1;
                    }
                    list.Add(added);
                    return (0, Json(new { Message = "Added the function " + added.Id + ".", Item = new { added.Id } }));
                }
                if (action == "set")
                {
                    string id = Value(args, "--id");
                    int index = list.FindIndex(f => f.Id == id);
                    if (args.Contains("--spec"))
                    {
                        VirtualFunction spec = JsonSerializer.Deserialize<VirtualFunction>(File.ReadAllText(Value(args, "--spec").TrimStart('@')))!;
                        spec.Normalize();
                        spec.Id = id;
                        list[index] = spec;
                    }
                    if (args.Contains("--enabled"))
                        list[index].Enabled = Value(args, "--enabled") == "on";
                    return (0, Json(new { Message = "Updated the function " + id + "." }));
                }
                if (action == "remove")
                {
                    list.RemoveAll(f => f.Id == Value(args, "--id"));
                    return (0, Json(new { Message = "Removed." }));
                }
                return (0, "{}");
            }
            default:
                return (0, Json(new { Message = "ok" }));
        }
    }

    private string Status()
    {
        object Limit(string describe, bool known) => new { Known = known, Describe = describe };
        object status = new
        {
            VJoy = new
            {
                Installed = true,
                Version = "2.1.9.1",
                Devices = new[] { new { Id = 1, Configured = true, AtMaximum = false, Describe = "x,y,z,rx,ry,rz,sl0,sl1, 32 buttons, 4 hats", DirectInputName = "vJoy Device {89D63500-B53F-11f1-8002-444553540000}" } }
            },
            HidHide = new { Installed = true, Version = "1.5.230.0", CloakOn = false, InverseOn = true, Devices = Array.Empty<string>(), Apps = Array.Empty<string>(), Unreadable = Array.Empty<string>() },
            Session = new { Phase = "none", Stale = false },
            Settings = new { FirstDevice = 1, LastDevice = 16, AutoStart = false, HideDevices = true },
            Games = new object[]
            {
                new { Id = "DCS", Name = "DCS World", Mode = "virtual", Relations = 12, ProcessNames = new[] { "DCS" }, Exes = Array.Empty<string>(),
                      InputLimit = Limit("reads 128 buttons, 8 axes, 4 hats per joystick (documented)", true), LimitWarnings = Array.Empty<string>(), Functions = functions["DCS"].Count },
                new { Id = "Arma2OA", Name = "Arma 2: Operation Arrowhead", Mode = "virtual", Relations = 3, ProcessNames = new[] { "arma2oa" }, Exes = Array.Empty<string>(),
                      InputLimit = Limit("reads 32 buttons, 8 axes, 1 hat per joystick (verified)", true),
                      LimitWarnings = new[] { "Arma 2: Operation Arrowhead reads only the first 32 buttons of a joystick: the inputs on buttons 33, 40 on vJoy 1 will not work there." }, Functions = 0 },
                new { Id = "HLLVietnam", Name = "Hell Let Loose: Vietnam", Mode = "xbox", Relations = 4, ProcessNames = new[] { "HLLVietnam" }, Exes = Array.Empty<string>(), Functions = 0 }
            },
            Functions = functions.SelectMany(g => g.Value.Select(f => (object)new
            {
                Game = g.Key,
                GameName = g.Key == "DCS" ? "DCS World" : g.Key,
                f.Id,
                Function = f,
                Outputs = f.Outputs.Select(o => new { o.Role, o.Target, Describe = o.Target.Length == 0 ? "none" : "vJoy 1 " + VfTokens.Describe(o.Target) }).ToArray(),
                Problems = Array.Empty<string>(),
                Warnings = Array.Empty<string>()
            })).ToArray(),
            InputDevices = new object[]
            {
                new { Device = VirtualFunctionsGuiTests.Stick, Name = "VKB Stick", Axes = new[] { "JOY_X", "JOY_Y", "JOY_RZ" }, Buttons = 32, Hats = 1 },
                new { Device = VirtualFunctionsGuiTests.Throttle, Name = "VPC Throttle", Axes = new[] { "JOY_X", "JOY_Y", "JOY_Z" }, Buttons = 64, Hats = 2 }
            },
            Mapping = Array.Empty<object>(),
            Needs = Array.Empty<object>(),
            RefreshRateHz = 144,
            Errors = Array.Empty<string>(),
            Warnings = Array.Empty<string>()
        };
        return Json(status);
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
