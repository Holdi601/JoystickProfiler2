using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JoyPro.Gui.Models;

// One game on the virtual joystick page: native (its physical devices), virtual vJoy (DirectInput) or the virtual Xbox
// 360 pad (XInput, ViGEmBus).
public sealed partial class VirtualJoystickGameItem : ObservableObject
{
    public const string ModeNative = "native";
    public const string ModeVJoy = "virtual";
    public const string ModeXbox = "xbox";

    public static IReadOnlyList<string> ModeNames { get; } = ["Native (real devices)", "vJoy (DirectInput)", "Xbox pad (XInput)"];

    private readonly Action<VirtualJoystickGameItem, string>? onModeChanged;
    private bool loading;

    public VirtualJoystickGameItem(string id, string name, int relations, string mode, IReadOnlyList<string> exes, Action<VirtualJoystickGameItem, string>? onModeChanged,
        int gamepadLayout = 0, bool writesGamepadBinds = false, string suggestedMode = "", string deviceLimitNote = "")
    {
        SuggestedMode = suggestedMode ?? string.Empty;
        DeviceLimitNote = deviceLimitNote ?? string.Empty;
        Id = id;
        Name = name;
        Relations = relations;
        Exes = exes;
        GamepadLayout = gamepadLayout;
        WritesGamepadBinds = writesGamepadBinds;
        this.onModeChanged = onModeChanged;
        loading = true;
        ModeIndex = ToIndex(mode);
        loading = false;
    }

    public int GamepadLayout { get; }

    public bool WritesGamepadBinds { get; }

    // One-device games (the engine's IGameDeviceLimit): the mode JoyPro suggests ("virtual" / "xbox"), empty otherwise.
    public string SuggestedMode { get; }

    public string DeviceLimitNote { get; }

    public bool ShowSuggestion => SuggestedMode.Length > 0 && Mode != SuggestedMode;

    public string SuggestionText => !ShowSuggestion
        ? string.Empty
        : T("One-device game: {0} Suggested: {1}.", DeviceLimitNote, (SuggestedMode == ModeXbox ? T("Xbox pad") : "vJoy"));

    [RelayCommand]
    private void ApplySuggestion() => ModeIndex = ToIndex(SuggestedMode);

    public IReadOnlyList<string> Modes => ModeNames;

    [ObservableProperty]
    private int modeIndex;

    // What the game reads of one joystick (engine vjoy limits) and the table's/functions' inputs above it.
    [ObservableProperty]
    private string inputLimitText = string.Empty;

    [ObservableProperty]
    private string limitWarningsText = string.Empty;

    [ObservableProperty]
    private int functionCount;

    public bool HasLimitWarnings => LimitWarningsText.Length > 0;

    public string FunctionsText => FunctionCount == 0 ? string.Empty : FunctionCount == 1 ? "1 virtual function" : FunctionCount.ToString(CultureInfo.InvariantCulture) + " virtual functions";

    partial void OnLimitWarningsTextChanged(string value) => OnPropertyChanged(nameof(HasLimitWarnings));

    partial void OnFunctionCountChanged(int value) => OnPropertyChanged(nameof(FunctionsText));

    public string Mode => ModeIndex == 1 ? ModeVJoy : ModeIndex == 2 ? ModeXbox : ModeNative;

    public bool IsVirtual => ModeIndex != 0;

    public bool IsXbox => ModeIndex == 2;

    public string XboxNote => !IsXbox
        ? string.Empty
        : (GamepadLayout > 0 ? T("Follows the game's own gamepad layout ({0} actions known). ", GamepadLayout.ToString(CultureInfo.InvariantCulture)) : T("JoyPro does not know this game's gamepad layout: the controls are given in order; check them in the game. ")) +
          (WritesGamepadBinds ? T("The export writes the gamepad binds.") : T("JoyPro writes no gamepad binds for this game."));

    private static int ToIndex(string mode) => mode == ModeVJoy ? 1 : mode == ModeXbox ? 2 : 0;

    public string Id { get; }

    public string Name { get; }

    public int Relations { get; }

    public IReadOnlyList<string> Exes { get; }

    public string RelationsText => Relations == 1 ? "1 relation" : Relations.ToString(CultureInfo.InvariantCulture) + " relations";

    public string ExesText => !IsVirtual
        ? string.Empty
        : Exes.Count == 0
            ? T("No game program found yet: JoyPro adds it when the game runs, or set it with engine vjoy exe.")
            : T("Hidden from: {0}", string.Join("; ", Exes));

    // Set from the engine's answer without asking the engine again.
    public void SetQuietly(string mode)
    {
        loading = true;
        ModeIndex = ToIndex(mode);
        loading = false;
    }

    partial void OnModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsVirtual));
        OnPropertyChanged(nameof(IsXbox));
        OnPropertyChanged(nameof(ExesText));
        OnPropertyChanged(nameof(XboxNote));
        OnPropertyChanged(nameof(ShowSuggestion));
        OnPropertyChanged(nameof(SuggestionText));
        if (!loading)
            onModeChanged?.Invoke(this, Mode);
    }
}

// One row of the mapping table: physical input -> vJoy input.
public sealed record VirtualJoystickSlotItem(
    string Source,
    string SourceName,
    string Input,
    int Device,
    string Target,
    bool Connected,
    bool Used,
    bool New,
    IReadOnlyList<string> Relations,
    string Game = "",
    string TargetName = "",
    bool Pinned = false)
{
    public string PhysicalText => (string.IsNullOrWhiteSpace(SourceName) ? Source : SourceName) + "  " + Input;

    public bool IsXbox => Device == 0;

    public string VirtualText => IsXbox
        ? T("Xbox pad  {0}{1}{2}", (string.IsNullOrWhiteSpace(TargetName) ? Target : TargetName), (Pinned ? T(" (game layout)") : string.Empty), (Game.Length > 0 ? "  [" + Game + "]" : string.Empty))
        : T("vJoy {0}  {1}", Device.ToString(CultureInfo.InvariantCulture), Target);

    public string StateText => !Connected ? T("device not connected") : New ? T("new (saved on the next mapping update)") : !Used ? T("not used any more") : "ok";

    public string RelationsText => Relations.Count == 0 ? string.Empty : string.Join(", ", Relations);
}

// One axis bar of the live view.
public sealed partial class VirtualJoystickAxisItem : ObservableObject
{
    public VirtualJoystickAxisItem(string name)
    {
        Name = name;
    }

    public string Name { get; }

    [ObservableProperty]
    private double value = 50;

    [ObservableProperty]
    private string valueText = "50 %";
}

// One vJoy device in the live view: its axes, pressed buttons and hats, as JoyPro.VJoy writes them.
public sealed partial class VirtualJoystickLiveDevice : ObservableObject
{
    private static readonly string[] AxisNames = ["X", "Y", "Z", "RX", "RY", "RZ", "SL0", "SL1"];
    private static readonly string[] XboxAxisNames = ["LX", "LY", "RX", "RY", "LT", "RT"];

    // Id 0 = the virtual Xbox 360 pad.
    public VirtualJoystickLiveDevice(int id)
    {
        Id = id;
        foreach (string name in id == 0 ? XboxAxisNames : AxisNames)
            Axes.Add(new VirtualJoystickAxisItem(name));
    }

    public int Id { get; }

    public string Title => Id == 0 ? T("Virtual Xbox 360 pad") : T("vJoy device {0}", Id.ToString(CultureInfo.InvariantCulture));

    public ObservableCollection<VirtualJoystickAxisItem> Axes { get; } = [];

    [ObservableProperty]
    private string buttonsText = "none";

    [ObservableProperty]
    private string hatsText = "centred";
}
