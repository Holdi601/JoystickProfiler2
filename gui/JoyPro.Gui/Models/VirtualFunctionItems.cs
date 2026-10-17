using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Engine.VJoy;

namespace JoyPro.Gui.Models;

// Virtual functions on the Virtual Joystick page (docs/virtual-joystick.md "Virtual functions"): the list rows, the
// type picker and the editor's rows (inputs, outputs, parameters, band edges, macro steps).

// A physical game controller for the input pickers (engine vjoy status "InputDevices").
public sealed record VfDeviceOption(string Device, string Name, IReadOnlyList<string> Axes, int Buttons, int Hats)
{
    public string Display => Name.Length > 0 ? Name : Device;

    public override string ToString() => Display;
}

// One function type in the "Add function" picker.
public sealed class VfTypeOption
{
    public VfTypeOption(VfTypeInfo info) => Info = info;

    public VfTypeInfo Info { get; }

    public string Id => Info.Id;

    public string Icon => Info.Icon;

    public string Name => Info.Name;

    public string Summary => Info.Summary;

    public string Example => "e.g. " + Info.Example;
}

// One function in the page's list.
public sealed partial class VirtualFunctionListItem : ObservableObject
{
    public VirtualFunctionListItem(string game, string gameName, VirtualFunction function, IReadOnlyList<string> problems, IReadOnlyList<string> warnings, IReadOnlyList<string> outputs)
    {
        Game = game;
        GameName = gameName;
        Function = function;
        Problems = problems;
        Warnings = warnings;
        OutputsText = outputs.Count == 0 ? T("no outputs yet") : string.Join(", ", outputs);
        VfTypeInfo? type = VirtualFunctionCatalog.Get(function.Type);
        TypeName = type?.Name ?? function.Type;
        Icon = type?.Icon ?? "?";
        Summary = type?.Summary ?? string.Empty;
    }

    public string Game { get; }

    public string GameName { get; }

    public VirtualFunction Function { get; }

    public string Id => Function.Id;

    public string Name => Function.Name.Length > 0 ? Function.Name : TypeName;

    public string TypeName { get; }

    public string Icon { get; }

    public string Summary { get; }

    public bool Enabled => Function.Enabled;

    public string OutputsText { get; }

    public IReadOnlyList<string> Problems { get; }

    public IReadOnlyList<string> Warnings { get; }

    public bool HasProblems => Problems.Count > 0;

    public bool HasWarnings => Warnings.Count > 0;

    public string ProblemsText => string.Join(Environment.NewLine, Problems.Concat(Warnings));

    public string StatusText => !Enabled ? "off" : Problems.Count > 0 ? T("{0} to fix", Problems.Count.ToString(CultureInfo.InvariantCulture)) : Warnings.Count > 0 ? T("check the notes") : "ready";

    // "fn" lines of a running forwarder: what the function does right now.
    [ObservableProperty]
    private string liveText = string.Empty;
}

public sealed partial class VfInputRoleItem : ObservableObject
{
    public static readonly string[] HatChoices = ["centred", "up", "up-right", "right", "down-right", "down", "down-left", "left", "up-left"];

    private readonly Action changed;
    private bool loading;

    // Called when a preview value changes (the editor steps its preview at once, nothing is rebuilt).
    public Action? PreviewChanged { get; set; }

    public VfInputRoleItem(VfRoleInfo role, IReadOnlyList<VfDeviceOption> devices, VfInput? current, Action changed)
    {
        this.changed = changed;
        Role = role.Role;
        Label = role.Label;
        Kind = role.Kind;
        Optional = role.Optional;
        List<VfDeviceOption> options = devices.ToList();
        if (current != null && current.Device.Length > 0 && options.All(d => !string.Equals(d.Device, current.Device, StringComparison.OrdinalIgnoreCase)))
            options.Add(new VfDeviceOption(current.Device, T("{0} (not connected)", DeviceName(current.Device)), VfTokens.AxisTokens, 128, 4));
        Devices = options;
        loading = true;
        SelectedDevice = current == null ? null : options.FirstOrDefault(d => string.Equals(d.Device, current.Device, StringComparison.OrdinalIgnoreCase));
        InputOptions = BuildInputs(SelectedDevice);
        SelectedInput = current == null || current.Input.Length == 0 ? null : current.Input;
        if (SelectedInput != null && !InputOptions.Contains(SelectedInput))
            InputOptions = InputOptions.Concat([SelectedInput]).ToList();
        loading = false;
    }

    public string Role { get; }

    public string Label { get; }

    public string Kind { get; }

    public bool Optional { get; }

    public string KindText => Optional ? T("{0}, optional", VirtualFunctionCatalog.KindWord(Kind)) : VirtualFunctionCatalog.KindWord(Kind);

    public IReadOnlyList<VfDeviceOption> Devices { get; }

    [ObservableProperty]
    private VfDeviceOption? selectedDevice;

    [ObservableProperty]
    private IReadOnlyList<string> inputOptions = [];

    [ObservableProperty]
    private string? selectedInput;

    // The preview's input ("Try it"): an axis 0..1, a button, a hat direction.
    [ObservableProperty]
    private double previewValue = 0.5;

    [ObservableProperty]
    private bool previewPressed;

    [ObservableProperty]
    private int previewHatIndex;

    public IReadOnlyList<string> HatOptions => HatChoices;

    // What the chosen input is (or the role's kind while none is chosen).
    public string InputKind
    {
        get
        {
            if (SelectedInput != null && VfTokens.TryParseInput(SelectedInput, out string kind, out _, out _))
                return kind;
            return Kind == "any" ? "button" : Kind;
        }
    }

    public bool PreviewIsAxis => InputKind == "axis";

    public bool PreviewIsButton => InputKind is "button" or "hatdir";

    public bool PreviewIsHat => InputKind == "hat";

    public bool IsSet => SelectedDevice != null && !string.IsNullOrEmpty(SelectedInput);

    public string PreviewPercent => Math.Round(PreviewValue * 100).ToString(CultureInfo.InvariantCulture) + " %";

    [RelayCommand]
    private void Clear()
    {
        SelectedInput = null;
        SelectedDevice = null;
    }

    partial void OnSelectedDeviceChanged(VfDeviceOption? value)
    {
        if (loading)
            return;
        InputOptions = BuildInputs(value);
        if (SelectedInput != null && !InputOptions.Contains(SelectedInput))
            SelectedInput = null;
        changed();
    }

    partial void OnSelectedInputChanged(string? value)
    {
        OnPropertyChanged(nameof(InputKind));
        OnPropertyChanged(nameof(PreviewIsAxis));
        OnPropertyChanged(nameof(PreviewIsButton));
        OnPropertyChanged(nameof(PreviewIsHat));
        OnPropertyChanged(nameof(IsSet));
        if (!loading)
            changed();
    }

    partial void OnPreviewValueChanged(double value)
    {
        OnPropertyChanged(nameof(PreviewPercent));
        PreviewChanged?.Invoke();
    }

    partial void OnPreviewPressedChanged(bool value) => PreviewChanged?.Invoke();

    partial void OnPreviewHatIndexChanged(int value) => PreviewChanged?.Invoke();

    private IReadOnlyList<string> BuildInputs(VfDeviceOption? device)
    {
        List<string> list = [];
        IReadOnlyList<string> axes = device?.Axes ?? VfTokens.AxisTokens;
        int buttons = device == null || device.Buttons <= 0 ? 32 : Math.Min(128, device.Buttons);
        int hats = device == null ? 1 : Math.Min(4, device.Hats);
        if (Kind is "axis" or "any")
            list.AddRange(axes);
        if (Kind is "button" or "any")
        {
            for (int i = 0; i < buttons; i++)
                list.Add(VfTokens.ButtonToken(i));
            for (int h = 1; h <= hats; h++)
            {
                foreach (string direction in VfTokens.HatDirections)
                    list.Add("JOY_BTN_POV" + h.ToString(CultureInfo.InvariantCulture) + "_" + direction);
            }
        }
        if (Kind is "hat" or "any")
        {
            for (int h = 0; h < hats; h++)
                list.Add(VfTokens.HatToken(h));
        }
        return list;
    }

    private static string DeviceName(string device)
    {
        int brace = device.IndexOf('{');
        return brace > 0 ? device[..brace].Trim() : device;
    }
}

public sealed partial class VfOutputRoleItem : ObservableObject
{
    public static readonly string[] ModeNames = ["hold while inside", "pulse on entering", "pulse on leaving", "pulse on both"];
    private const string None = "(none)";

    private readonly Action changed;
    private bool loading;

    public VfOutputRoleItem(VfRoleInfo role, string requiredKind, VfOutput? current, bool xbox, bool keyboard, Action changed)
    {
        this.changed = changed;
        Role = role.Role;
        Label = role.Label;
        Kind = requiredKind;
        Optional = role.Optional;
        HasMode = role.HasMode;
        List<string> targets = [None];
        if (Kind == "axis")
            targets.AddRange(xbox ? VfTokens.XboxAxes : VfTokens.AxisTokens);
        else if (Kind == "hat")
            targets.AddRange(xbox ? ["XB_DPAD"] : Enumerable.Range(0, 4).Select(VfTokens.HatToken));
        else
        {
            if (xbox)
                targets.AddRange(["XB_A", "XB_B", "XB_X", "XB_Y", "XB_LB", "XB_RB", "XB_BACK", "XB_START", "XB_LS", "XB_RS", "XB_DPAD_UP", "XB_DPAD_DOWN", "XB_DPAD_LEFT", "XB_DPAD_RIGHT", "XB_LT", "XB_RT"]);
            else
                targets.AddRange(Enumerable.Range(0, 128).Select(VfTokens.ButtonToken));
            if (keyboard)
                targets.AddRange(VfTokens.KeyNames);
        }
        loading = true;
        string target = current?.Target ?? string.Empty;
        if (target.Length > 0 && !targets.Contains(target))
            targets.Add(target);
        Targets = targets;
        Target = target.Length == 0 ? None : target;
        VJoyDevice = current == null || current.VJoyDevice <= 0 ? 1 : current.VJoyDevice;
        ModeIndex = Math.Max(0, Array.IndexOf(VirtualFunctionCatalog.Modes, current?.Mode ?? VirtualFunctionCatalog.ModeHold));
        loading = false;
    }

    public string Role { get; }

    public string Label { get; }

    public string Kind { get; }

    public bool Optional { get; }

    public bool HasMode { get; }

    public IReadOnlyList<string> Targets { get; }

    public IReadOnlyList<string> Modes => ModeNames;

    public static IReadOnlyList<int> DeviceNumbers { get; } = Enumerable.Range(1, 16).ToList();

    public IReadOnlyList<int> Devices => DeviceNumbers;

    [ObservableProperty]
    private string target = None;

    [ObservableProperty]
    private int vJoyDevice = 1;

    [ObservableProperty]
    private int modeIndex;

    // Preview or live: is the output on, its value as text.
    [ObservableProperty]
    private bool isOn;

    [ObservableProperty]
    private string valueText = string.Empty;

    public string TargetToken => Target == None ? string.Empty : Target;

    public bool IsVJoyTarget => TargetToken.Length > 0 && !TargetToken.StartsWith("XB_", StringComparison.Ordinal) && !TargetToken.StartsWith("KEY_", StringComparison.Ordinal);

    public string Describe => TargetToken.Length == 0 ? "none" : (IsVJoyTarget ? T("vJoy {0} ", VJoyDevice.ToString(CultureInfo.InvariantCulture)) : string.Empty) + VfTokens.Describe(TargetToken);

    public string Mode => VirtualFunctionCatalog.Modes[Math.Clamp(ModeIndex, 0, 3)];

    partial void OnTargetChanged(string value)
    {
        OnPropertyChanged(nameof(TargetToken));
        OnPropertyChanged(nameof(IsVJoyTarget));
        OnPropertyChanged(nameof(Describe));
        if (!loading)
            changed();
    }

    partial void OnVJoyDeviceChanged(int value)
    {
        OnPropertyChanged(nameof(Describe));
        if (!loading)
            changed();
    }

    partial void OnModeIndexChanged(int value)
    {
        if (!loading)
            changed();
    }
}

public sealed partial class VfParamItem : ObservableObject
{
    private readonly Action changed;
    private bool loading;

    public VfParamItem(VfParamInfo info, double value, Action changed)
    {
        this.changed = changed;
        Info = info;
        loading = true;
        Value = value;
        ChoiceIndex = info.Key == "ways" ? (value >= 6 ? 1 : 0) : (int)Math.Round(value - info.Min);
        loading = false;
    }

    public VfParamInfo Info { get; }

    public string Key => Info.Key;

    public string Label => Info.Label;

    public string Help => Info.Help;

    public double Minimum => Info.Min;

    public double Maximum => Info.Max;

    public double Step => Info.Step;

    // Choices: values Min, Min + 1 ... (ways 4/8 is a choice of two numbers).
    public bool IsChoice => Info.Choices.Count > 0 || Info.Key == "ways";

    public bool IsNumber => !IsChoice;

    public IReadOnlyList<string> Choices => Info.Key == "ways"
        ? (Info.Max >= 8 && Info.Min >= 4 ? ["4 ways", "8 ways"] : ["4 ways"])
        : Info.Choices;

    [ObservableProperty]
    private double value;

    [ObservableProperty]
    private int choiceIndex;

    public string ValueText
    {
        get
        {
            string number = Info.Step >= 1 ? Math.Round(Value).ToString(CultureInfo.InvariantCulture) : Value.ToString("0.###", CultureInfo.InvariantCulture);
            if (Info.Unit == "of travel" || Info.Unit == "of half travel")
                number = Math.Round(Value * 100, 1).ToString(CultureInfo.InvariantCulture) + " %";
            else if (Info.Unit.Length > 0)
                number += " " + Info.Unit;
            return number;
        }
    }

    partial void OnValueChanged(double value)
    {
        OnPropertyChanged(nameof(ValueText));
        if (!loading)
            changed();
    }

    partial void OnChoiceIndexChanged(int value)
    {
        if (loading)
            return;
        loading = true;
        Value = Info.Key == "ways" ? (value <= 0 ? 4 : 8) : Info.Min + value;
        loading = false;
        OnPropertyChanged(nameof(ValueText));
        changed();
    }
}

// A band edge (0..1) of the axis band editor.
public sealed partial class VfEdgeItem : ObservableObject
{
    private readonly Action changed;

    public VfEdgeItem(int index, double value, Action changed)
    {
        Index = index;
        this.value = value;
        this.changed = changed;
    }

    public int Index { get; }

    public string Label => T("Edge {0}", (Index + 1).ToString(CultureInfo.InvariantCulture));

    [ObservableProperty]
    private double value;

    public string Percent => Math.Round(Value * 100, 1).ToString(CultureInfo.InvariantCulture) + " %";

    partial void OnValueChanged(double value)
    {
        OnPropertyChanged(nameof(Percent));
        changed();
    }
}

// One band of the band editor: its output and whether it fires (preview or live).
public sealed record VfBandInfo(int Index, string Label, string Output, bool On, bool Current);

public sealed partial class VfStepItem : ObservableObject
{
    public static readonly string[] ActionNames = ["tap", "press", "release"];
    private readonly Action changed;

    public VfStepItem(VfStep step, IReadOnlyList<string> outputs, Action changed)
    {
        this.changed = changed;
        Outputs = outputs;
        output = step.Output;
        actionIndex = Math.Max(0, Array.IndexOf(ActionNames, (step.Action ?? "tap").ToLowerInvariant()));
        delayMs = step.DelayMs;
        holdMs = step.HoldMs;
    }

    public IReadOnlyList<string> Outputs { get; }

    public IReadOnlyList<string> Actions => ActionNames;

    [ObservableProperty]
    private string output;

    [ObservableProperty]
    private int actionIndex;

    [ObservableProperty]
    private double delayMs;

    [ObservableProperty]
    private double holdMs;

    public VfStep ToStep() => new() { Output = Output, Action = ActionNames[Math.Clamp(ActionIndex, 0, 2)], DelayMs = (int)Math.Round(DelayMs), HoldMs = (int)Math.Round(HoldMs) };

    partial void OnOutputChanged(string value) => changed();

    partial void OnActionIndexChanged(int value) => changed();

    partial void OnDelayMsChanged(double value) => changed();

    partial void OnHoldMsChanged(double value) => changed();
}
