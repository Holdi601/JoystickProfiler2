using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Engine.Axis;
using JoyPro.Engine.VJoy;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// The editor of one virtual function (docs/virtual-joystick.md "Virtual functions"): inputs, outputs, parameters, band
// edges (dragged on the axis bar), macro steps and the response curve, with a preview that runs the same evaluation as
// the forwarder (VfSimulator, engine/VJoy/VirtualFunctionRuntime.cs) on inputs the user sets with the mouse, and the
// live state of a running forwarder ("fn" lines of Test/Start). The preview timer runs only while "Try it" is on.
public sealed partial class VirtualFunctionEditorViewModel : ObservableObject
{
    private readonly VirtualFunction original;
    private readonly IReadOnlyList<VfDeviceOption> devices;
    private readonly Stopwatch clock = new();
    private VfSimulator? simulator;
    private DispatcherTimer? timer;
    private bool building;
    private long liveUntil;

    public VirtualFunctionEditorViewModel(string game, string gameName, VirtualFunction function, IReadOnlyList<VfDeviceOption> devices, bool xboxGame,
        IReadOnlyList<string>? warnings = null)
    {
        Game = game;
        GameName = gameName;
        IsXboxGame = xboxGame;
        original = function.Clone();
        original.Normalize();
        this.devices = devices;
        Type = VirtualFunctionCatalog.Get(original.Type) ?? VirtualFunctionCatalog.Types[0];
        Warnings = warnings ?? [];
        building = true;
        name = original.Name;
        enabled = original.Enabled;
        keyboard = original.Keyboard;
        LoadTuning(original.Tuning);
        building = false;
        BuildRows();
        Rebuild();
    }

    public string Game { get; }

    public string GameName { get; }

    public bool IsXboxGame { get; }

    public VfTypeInfo Type { get; }

    public string Id => original.Id;

    public string Icon => Type.Icon;

    public string TypeName => Type.Name;

    public string Summary => Type.Summary;

    public string Example => T("Use it for: {0}", Type.Example);

    public IReadOnlyList<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public string WarningsText => string.Join(Environment.NewLine, Warnings);

    public ObservableCollection<VfInputRoleItem> Inputs { get; } = [];

    public ObservableCollection<VfOutputRoleItem> Outputs { get; } = [];

    public ObservableCollection<VfParamItem> Params { get; } = [];

    public ObservableCollection<VfEdgeItem> Edges { get; } = [];

    public ObservableCollection<VfStepItem> Steps { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private bool enabled = true;

    [ObservableProperty]
    private bool keyboard;

    // Preview ("Try it").
    [ObservableProperty]
    private bool previewRunning;

    [ObservableProperty]
    private string stateText = string.Empty;

    // The first input as the band editor shows it: the preview's or the forwarder's value (0..1), NaN = none.
    [ObservableProperty]
    private double displayInput = double.NaN;

    [ObservableProperty]
    private int currentBand = -1;

    [ObservableProperty]
    private IReadOnlyList<VfBandInfo> bands = [];

    [ObservableProperty]
    private bool liveActive;

    // Curve (type "curve"): JoyPro's DCS axis parameters.
    [ObservableProperty]
    private double curveDeadzone;

    [ObservableProperty]
    private double curveSaturationX = 1;

    [ObservableProperty]
    private double curveSaturationY = 1;

    [ObservableProperty]
    private double curveCurvature;

    [ObservableProperty]
    private bool curveInvert;

    [ObservableProperty]
    private bool curveSlider;

    [ObservableProperty]
    private IReadOnlyList<Point> curvePoints = [];

    [ObservableProperty]
    private double curveOutput;

    // The curve view's live stick (-1..1), NaN without one.
    public double CurveStickInput => IsCurve && !double.IsNaN(DisplayInput) ? DisplayInput * 2 - 1 : double.NaN;

    public string CurveSummary => T("Dead zone {0} %, saturation X {1} %, saturation Y {2} %, curvature {3}{4}{5}", Math.Round(CurveDeadzone * 100).ToString(CultureInfo.InvariantCulture), Math.Round(CurveSaturationX * 100).ToString(CultureInfo.InvariantCulture), Math.Round(CurveSaturationY * 100).ToString(CultureInfo.InvariantCulture), CurveCurvature.ToString("0.00", CultureInfo.InvariantCulture), (CurveInvert ? T(", inverted") : string.Empty), (CurveSlider ? T(", slider") : string.Empty));

    partial void OnDisplayInputChanged(double value) => OnPropertyChanged(nameof(CurveStickInput));

    public bool IsBands => Type.Id == VirtualFunctionCatalog.Bands;

    public bool IsAxisEnds => Type.Id == VirtualFunctionCatalog.AxisButtons;

    public bool ShowBandEditor => IsBands || IsAxisEnds;

    public bool IsCurve => Type.Id == VirtualFunctionCatalog.Curve;

    public bool IsMacro => Type.Id == VirtualFunctionCatalog.Macro;

    // Cycle steps, shift entries, macro outputs: how many.
    public bool HasCount => Type.Id is VirtualFunctionCatalog.Cycle or VirtualFunctionCatalog.Shift or VirtualFunctionCatalog.Macro;

    public string CountLabel => Type.Id switch
    {
        VirtualFunctionCatalog.Cycle => T("Steps"),
        VirtualFunctionCatalog.Shift => "Inputs on the layer",
        _ => T("Outputs")
    };

    public int Count => IsBands ? Edges.Count + 1 : VirtualFunctionCatalog.CountOf(original);

    public string CountText => Count.ToString(CultureInfo.InvariantCulture);

    public bool HasProblems => Problems.Count > 0;

    public bool HasParams => Params.Count > 0;

    // What a macro step can press: the function's outputs, and keys with keyboard output on.
    private List<string> StepTargets() => Outputs.Select(o => o.Role).Concat(original.Keyboard ? VfTokens.KeyNames : []).ToList();

    public string ProblemsText => string.Join(Environment.NewLine, Problems);

    public double Hysteresis => Params.FirstOrDefault(p => p.Key == "hysteresis")?.Value ?? 0;

    // The band editor's edges (0..1): bands' own, or the two ends of "axis ends to buttons".
    public IReadOnlyList<double> EdgeValues
    {
        get
        {
            if (IsAxisEnds)
            {
                double threshold = Params.FirstOrDefault(p => p.Key == "threshold")?.Value ?? 0.1;
                return [threshold, 1 - threshold];
            }
            return Edges.Select(e => e.Value).ToList();
        }
    }

    // The function as edited (what Save stores).
    public VirtualFunction ToFunction()
    {
        VirtualFunction function = original.Clone();
        function.Name = (Name ?? string.Empty).Trim();
        function.Enabled = Enabled;
        function.Keyboard = Keyboard;
        foreach (VfInputRoleItem input in Inputs)
        {
            VfInput? target = function.InputOf(input.Role);
            if (target == null)
            {
                target = new VfInput { Role = input.Role };
                function.Inputs.Add(target);
            }
            target.Device = input.IsSet ? input.SelectedDevice!.Device : string.Empty;
            target.Input = input.IsSet ? input.SelectedInput! : string.Empty;
        }
        function.Inputs.RemoveAll(i => Inputs.All(r => r.Role != i.Role));
        foreach (VfOutputRoleItem output in Outputs)
        {
            VfOutput? target = function.OutputOf(output.Role);
            if (target == null)
            {
                target = new VfOutput { Role = output.Role };
                function.Outputs.Add(target);
            }
            target.Target = output.TargetToken;
            target.VJoyDevice = output.IsVJoyTarget ? output.VJoyDevice : 0;
            target.Mode = output.HasMode ? output.Mode : string.Empty;
        }
        // Outputs the editor does not show (a shift layer's unshifted output the engine fills) stay; removed roles go.
        List<VfRoleInfo> roles = VirtualFunctionCatalog.ExpandOutputs(function);
        function.Outputs.RemoveAll(o => roles.All(r => r.Role != o.Role));
        foreach (VfParamItem param in Params)
            function.Params[param.Key] = Math.Round(param.Value, 6);
        if (IsBands)
            function.Edges = Edges.Select(e => Math.Round(e.Value, 4)).ToList();
        if (IsMacro)
            function.Steps = Steps.Select(s => s.ToStep()).ToList();
        if (IsCurve)
            function.Tuning = CurveTuning().Format();
        return function;
    }

    // ---- rows

    private void BuildRows()
    {
        building = true;
        // The preview's input values stay when the rows are made anew (a band or a step added).
        Dictionary<string, (double Value, bool Pressed, int Hat)> preview = Inputs.ToDictionary(i => i.Role, i => (i.PreviewValue, i.PreviewPressed, i.PreviewHatIndex));
        Inputs.Clear();
        Outputs.Clear();
        Params.Clear();
        Edges.Clear();
        Steps.Clear();
        foreach (VfRoleInfo role in VirtualFunctionCatalog.ExpandInputs(original))
        {
            VfInputRoleItem input = new(role, devices, original.InputOf(role.Role), OnChanged);
            if (preview.TryGetValue(role.Role, out (double Value, bool Pressed, int Hat) kept))
            {
                input.PreviewValue = kept.Value;
                input.PreviewPressed = kept.Pressed;
                input.PreviewHatIndex = kept.Hat;
            }
            input.PreviewChanged = () =>
            {
                if (!building)
                    Step();
            };
            Inputs.Add(input);
        }
        foreach (VfRoleInfo role in VirtualFunctionCatalog.ExpandOutputs(original))
        {
            VfOutput? current = original.OutputOf(role.Role);
            // A shift layer's unshifted output is the input's own slot unless the user picks one: shown only when set.
            if (Type.Id == VirtualFunctionCatalog.Shift && role.Role.StartsWith("base", StringComparison.Ordinal) && (current == null || current.Target.Length == 0))
                continue;
            // Outputs left out on purpose (double press, diagonals) are offered empty and change nothing until set.
            Outputs.Add(new VfOutputRoleItem(role, RequiredKind(role), current, IsXboxGame, original.Keyboard, OnChanged));
        }
        foreach (VfParamInfo param in Type.Params)
            Params.Add(new VfParamItem(param, original.Param(param.Key, param.Default), OnChanged));
        if (IsBands)
        {
            for (int i = 0; i < original.Edges.Count; i++)
                Edges.Add(new VfEdgeItem(i, original.Edges[i], OnChanged));
        }
        if (IsMacro)
        {
            List<string> targets = StepTargets();
            foreach (VfStep step in original.Steps)
                Steps.Add(new VfStepItem(step, targets, OnChanged));
        }
        building = false;
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(EdgeValues));
    }

    private string RequiredKind(VfRoleInfo role)
    {
        if (Type.Id != VirtualFunctionCatalog.Shift || role.Kind != "any")
            return role.Kind == "any" ? "button" : role.Kind;
        string number = new(role.Role.Where(char.IsDigit).ToArray());
        VfInput? input = original.InputOf("in" + number);
        if (input != null && VfTokens.TryParseInput(input.Input, out string kind, out _, out _))
            return kind == "hatdir" ? "button" : kind;
        return "button";
    }

    private void OnChanged()
    {
        if (building)
            return;
        Rebuild();
    }

    partial void OnKeyboardChanged(bool value)
    {
        if (building)
            return;
        // The output lists offer keys only with keyboard output on.
        VirtualFunction current = ToFunction();
        original.Keyboard = value;
        CopyInto(current);
        BuildRows();
        Rebuild();
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Title));

    public string Title => (Name.Length > 0 ? Name : Type.Name) + " (" + GameName + ")";

    // Count +/- for numbered roles; bands add or remove an edge.
    [RelayCommand]
    private void AddCount() => ChangeCount(+1);

    [RelayCommand]
    private void RemoveCount() => ChangeCount(-1);

    [RelayCommand]
    private void AddBand() => ChangeCount(+1);

    [RelayCommand]
    private void RemoveBand() => ChangeCount(-1);

    private void ChangeCount(int delta)
    {
        VirtualFunction current = ToFunction();
        if (IsBands)
        {
            List<double> edges = current.Edges.ToList();
            if (delta > 0 && edges.Count < 15)
            {
                // Split the widest band.
                List<double> bounds = new List<double> { 0 }.Concat(edges).Concat([1.0]).ToList();
                int widest = Enumerable.Range(0, bounds.Count - 1).OrderByDescending(i => bounds[i + 1] - bounds[i]).First();
                edges.Insert(widest, Math.Round((bounds[widest] + bounds[widest + 1]) / 2, 3));
                current.Edges = edges;
                current.Outputs.Add(new VfOutput { Role = "band" + (edges.Count + 1).ToString(CultureInfo.InvariantCulture), Mode = VirtualFunctionCatalog.ModeHold });
            }
            else if (delta < 0 && edges.Count > 1)
            {
                edges.RemoveAt(edges.Count - 1);
                current.Edges = edges;
                current.Outputs.RemoveAll(o => o.Role == "band" + (edges.Count + 2).ToString(CultureInfo.InvariantCulture));
            }
        }
        else
        {
            int count = Math.Clamp(VirtualFunctionCatalog.CountOf(current) + delta, 1, Type.MaxCount <= 0 ? 32 : Type.MaxCount);
            if (Type.Id == VirtualFunctionCatalog.Cycle)
                count = Math.Max(2, count);
            SetCount(current, count);
        }
        CopyInto(current);
        BuildRows();
        Rebuild();
    }

    // The engine's SetCount for numbered roles (engine vjoy functions set --count), here without an engine call.
    public static void SetCount(VirtualFunction function, int n)
    {
        VfTypeInfo? info = VirtualFunctionCatalog.Get(function.Type);
        if (info == null)
            return;
        foreach (VfRoleInfo role in info.Inputs.Where(r => r.Numbered))
        {
            function.Inputs.RemoveAll(i => Number(i.Role, role.Role) > n);
            for (int i = 1; i <= n; i++)
            {
                if (function.InputOf(role.Role + i.ToString(CultureInfo.InvariantCulture)) == null)
                    function.Inputs.Add(new VfInput { Role = role.Role + i.ToString(CultureInfo.InvariantCulture) });
            }
        }
        foreach (VfRoleInfo role in info.Outputs.Where(r => r.Numbered))
        {
            function.Outputs.RemoveAll(o => Number(o.Role, role.Role) > n);
            if (role.Optional && info.Id == VirtualFunctionCatalog.Shift)
                continue;
            for (int i = 1; i <= n; i++)
            {
                if (function.OutputOf(role.Role + i.ToString(CultureInfo.InvariantCulture)) == null)
                    function.Outputs.Add(new VfOutput { Role = role.Role + i.ToString(CultureInfo.InvariantCulture), Mode = role.HasMode ? VirtualFunctionCatalog.ModeHold : string.Empty });
            }
        }
        if (info.Id == VirtualFunctionCatalog.Macro)
            function.Steps.RemoveAll(s => s.Output.StartsWith("out", StringComparison.OrdinalIgnoreCase) && Number(s.Output, "out") > n);
    }

    private static int Number(string role, string prefix) =>
        role.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(role[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

    private void CopyInto(VirtualFunction current)
    {
        original.Inputs = current.Inputs;
        original.Outputs = current.Outputs;
        original.Params = current.Params;
        original.Edges = current.Edges;
        original.Steps = current.Steps;
        original.Tuning = current.Tuning;
        original.Name = current.Name;
        original.Enabled = current.Enabled;
        original.Keyboard = current.Keyboard;
    }

    // ---- band edges (dragged on the axis bar)

    // Moves edge index to value, kept between its neighbours (a band is at least 1 % wide).
    public void MoveEdge(int index, double value)
    {
        if (IsAxisEnds)
        {
            VfParamItem? threshold = Params.FirstOrDefault(p => p.Key == "threshold");
            if (threshold != null)
                threshold.Value = Math.Round(Math.Clamp(index == 0 ? value : 1 - value, threshold.Minimum, threshold.Maximum), 3);
            OnPropertyChanged(nameof(EdgeValues));
            return;
        }
        if (index < 0 || index >= Edges.Count)
            return;
        double low = index == 0 ? 0.01 : Edges[index - 1].Value + 0.01;
        double high = index == Edges.Count - 1 ? 0.99 : Edges[index + 1].Value - 0.01;
        Edges[index].Value = Math.Round(Math.Clamp(value, low, high), 3);
        OnPropertyChanged(nameof(EdgeValues));
    }

    [RelayCommand]
    private void AddStep()
    {
        List<string> targets = StepTargets();
        Steps.Add(new VfStepItem(new VfStep { Output = targets.FirstOrDefault() ?? "out1", Action = "tap", DelayMs = 200, HoldMs = 80 }, targets, OnChanged));
        Rebuild();
    }

    [RelayCommand]
    private void RemoveStep(VfStepItem? step)
    {
        if (step != null && Steps.Remove(step))
            Rebuild();
    }

    // ---- curve

    private void LoadTuning(string text)
    {
        AxisTuning tuning = string.IsNullOrWhiteSpace(text) ? new AxisTuning() : AxisTuning.Parse(text);
        curveDeadzone = tuning.Deadzone;
        curveSaturationX = tuning.SaturationX;
        curveSaturationY = tuning.SaturationY;
        curveCurvature = tuning.Curvature == null || tuning.Curvature.Count == 0 ? 0 : tuning.Curvature[0];
        curveInvert = tuning.Inverted;
        curveSlider = tuning.Slider;
    }

    private AxisTuning CurveTuning() => new()
    {
        Deadzone = Math.Round(CurveDeadzone, 3),
        SaturationX = Math.Round(CurveSaturationX, 3),
        SaturationY = Math.Round(CurveSaturationY, 3),
        Curvature = [Math.Round(CurveCurvature, 3)],
        Inverted = CurveInvert,
        Slider = CurveSlider
    };

    partial void OnCurveDeadzoneChanged(double value) => OnChanged();

    partial void OnCurveSaturationXChanged(double value) => OnChanged();

    partial void OnCurveSaturationYChanged(double value) => OnChanged();

    partial void OnCurveCurvatureChanged(double value) => OnChanged();

    partial void OnCurveInvertChanged(bool value) => OnChanged();

    partial void OnCurveSliderChanged(bool value) => OnChanged();

    // ---- preview and validation

    // A new simulator for the edited function (after every change), then one step.
    public void Rebuild()
    {
        VirtualFunction function = ToFunction();
        Problems.Clear();
        foreach (string problem in VirtualFunctionCatalog.Validate(function))
            Problems.Add(problem);
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(ProblemsText));
        OnPropertyChanged(nameof(Hysteresis));
        OnPropertyChanged(nameof(EdgeValues));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountText));
        simulator = new VfSimulator(function);
        if (IsCurve)
        {
            OnPropertyChanged(nameof(CurveSummary));
            double[] table = new double[129];
            CurveFunctionTable(function.Tuning, table);
            CurvePoints = Enumerable.Range(0, table.Length).Select(i => new Point(i / 64.0 - 1, table[i] * 2 - 1)).ToList();
        }
        clock.Restart();
        Step();
    }

    private static void CurveFunctionTable(string tuning, double[] table)
    {
        AxisTuning? t = string.IsNullOrWhiteSpace(tuning) ? null : AxisTuning.Parse(tuning);
        for (int i = 0; i < table.Length; i++)
        {
            double u = i / (double)(table.Length - 1);
            table[i] = t == null ? u : Math.Clamp((AxisModels.EvaluateDcs(t, u * 2 - 1) + 1) / 2.0, 0, 1);
        }
    }

    partial void OnPreviewRunningChanged(bool value)
    {
        if (value)
        {
            Rebuild();
            if (timer == null && Application.Current != null)
            {
                // ~60 Hz while "Try it" is on, nothing otherwise.
                timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, (_, _) => Step());
                timer.Start();
            }
        }
        else
        {
            timer?.Stop();
            timer = null;
        }
    }

    // Stops the preview timer (the editor closes).
    public void Close() => PreviewRunning = false;

    // One preview step at the real time since the last rebuild (tests call StepAt with their own clock).
    public void Step() => StepAt(clock.Elapsed.TotalMilliseconds);

    public void StepAt(double timeMs)
    {
        if (simulator == null)
            return;
        foreach (VfInputRoleItem input in Inputs)
        {
            if (!simulator.HasInput(input.Role))
                continue;
            string kind = simulator.InputKind(input.Role);
            if (kind == "axis")
                simulator.SetAxis(input.Role, input.PreviewValue);
            else if (kind == "hat")
                simulator.SetHat(input.Role, input.PreviewHatIndex <= 0 ? -1 : (input.PreviewHatIndex - 1) * 4500);
            else
                simulator.SetButton(input.Role, input.PreviewPressed);
        }
        simulator.Step(timeMs);
        if (Stopwatch.GetTimestamp() < liveUntil)
            return; // a running forwarder's live state wins over the preview
        LiveActive = false;
        VfFunction? compiled = simulator.Compiled;
        if (compiled == null)
            return;
        foreach (VfOutputRoleItem output in Outputs)
        {
            VfOut? o = simulator.Output(output.Role);
            bool on = o != null && o.Configured && simulator.Runtime.IsOn(o);
            output.IsOn = on;
            output.ValueText = o == null || !o.Configured ? string.Empty : simulator.Describe(o);
        }
        UpdateState(compiled.State, compiled.Input, compiled.Active, compiled.OutputRoles);
    }

    // A running forwarder's "fn <id> <input> <state> <active>" line for this function.
    public void ApplyLive(int inputMilli, int state, int active)
    {
        liveUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency; // the preview waits a second after the last line
        LiveActive = true;
        string[] roles = simulator?.Compiled?.OutputRoles ?? [];
        foreach (VfOutputRoleItem output in Outputs)
        {
            int k = Array.IndexOf(roles, output.Role);
            output.IsOn = k >= 0 && k < 31 && (active & (1 << k)) != 0;
            output.ValueText = output.IsOn ? T("on (live)") : T("off (live)");
        }
        UpdateState(state, inputMilli / 1000.0, active, roles);
    }

    private void UpdateState(int state, double input, int active, string[] roles)
    {
        DisplayInput = Type.Id is VirtualFunctionCatalog.Bands or VirtualFunctionCatalog.AxisButtons or VirtualFunctionCatalog.Curve or VirtualFunctionCatalog.Split or VirtualFunctionCatalog.Encoder
            ? input
            : double.NaN;
        CurrentBand = ShowBandEditor ? state : -1;
        if (IsCurve)
            CurveOutput = state / 1000.0 * 2 - 1;
        if (ShowBandEditor)
        {
            List<VfBandInfo> list = [];
            int count = IsAxisEnds ? 3 : Edges.Count + 1;
            for (int b = 0; b < count; b++)
            {
                string role = IsAxisEnds ? (b == 0 ? "low" : b == 2 ? "high" : string.Empty) : "band" + (b + 1).ToString(CultureInfo.InvariantCulture);
                VfOutputRoleItem? output = Outputs.FirstOrDefault(o => o.Role == role);
                int k = Array.IndexOf(roles, role);
                bool on = k >= 0 && k < 31 && (active & (1 << k)) != 0;
                string label = IsAxisEnds ? (b == 0 ? "low end" : b == 2 ? "high end" : "middle") : "band " + (b + 1).ToString(CultureInfo.InvariantCulture);
                string text = output == null ? string.Empty : output.TargetToken.Length == 0 ? T("no output")
                    : VfTokens.Describe(output.TargetToken).Replace("Button ", "Btn ", StringComparison.Ordinal) + (output.HasMode && output.ModeIndex > 0 ? " (" + output.Mode + ")" : string.Empty);
                list.Add(new VfBandInfo(b, label, text, on, b == state));
            }
            Bands = list;
        }
        StateText = DescribeState(state, input);
    }

    private string DescribeState(int state, double input)
    {
        string percent = Math.Round(input * 100).ToString(CultureInfo.InvariantCulture) + " %";
        return Type.Id switch
        {
            VirtualFunctionCatalog.Bands => "Axis at " + percent + ": band " + (state + 1).ToString(CultureInfo.InvariantCulture) + " of " + (Edges.Count + 1).ToString(CultureInfo.InvariantCulture),
            VirtualFunctionCatalog.AxisButtons => T("Axis at {0}: {1}", percent, (state == 0 ? "low end" : state == 2 ? "high end" : "middle")),
            VirtualFunctionCatalog.Cycle => state < 0 ? "Not started: the next press gives step 1" : "Step " + (state + 1).ToString(CultureInfo.InvariantCulture) + " of " + Count.ToString(CultureInfo.InvariantCulture),
            VirtualFunctionCatalog.Toggle => state == 1 ? "Latched on" : "Off",
            VirtualFunctionCatalog.Switch => state == 1 ? "Switch on" : "Switch off",
            VirtualFunctionCatalog.Presses => state switch { 1 => "Button down", 2 => "Waiting for a second press", 3 => "Second press", 4 => "Long press", _ => T("Idle") },
            VirtualFunctionCatalog.Shift => state == 1 ? "Shifted: the layer's outputs" : "Not shifted: the normal outputs",
            VirtualFunctionCatalog.Merge or VirtualFunctionCatalog.Curve or VirtualFunctionCatalog.ButtonsAxis => T("Output {0} %", Math.Round(state / 10.0).ToString(CultureInfo.InvariantCulture)),
            VirtualFunctionCatalog.Split => T("Axis at {0}", percent),
            VirtualFunctionCatalog.AxesHat or VirtualFunctionCatalog.HatButtons or VirtualFunctionCatalog.ButtonsHat => state < 0 ? "Centred" : "Direction " + VfInputRoleItem.HatChoices[Math.Clamp(state + 1, 0, 8)],
            VirtualFunctionCatalog.Encoder => state == 0 ? "No pulse waiting" : Math.Abs(state).ToString(CultureInfo.InvariantCulture) + (state > 0 ? " up" : " down") + " pulse(s) waiting",
            VirtualFunctionCatalog.Turbo => state == 1 ? "Repeating" : "Idle",
            VirtualFunctionCatalog.Macro => state < 0 ? "Idle" : "Playing (event " + (state + 1).ToString(CultureInfo.InvariantCulture) + ")",
            _ => string.Empty
        };
    }

    // ---- relations: bind a relation to an output (the page runs engine binds set)

    internal Func<VfOutputRoleItem, string, System.Threading.Tasks.Task>? BindHandler { get; set; }

    [ObservableProperty]
    private string bindRelation = string.Empty;

    [RelayCommand]
    private System.Threading.Tasks.Task BindOutput(VfOutputRoleItem? output) =>
        output == null || BindHandler == null ? System.Threading.Tasks.Task.CompletedTask : BindHandler(output, BindRelation);

    // ---- tests

    internal VfSimulator? Simulator => simulator;
}
