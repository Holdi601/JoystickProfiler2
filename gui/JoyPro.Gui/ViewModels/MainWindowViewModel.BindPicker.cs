using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// One device in the Binds page's "Pick the input by hand": the device name the profile uses (with its instance GUID)
// and what the user reads (the Windows name; the model when it says more; the GUID only to tell two alike apart).
public sealed class BindDeviceChoice
{
    public BindDeviceChoice(string device, string label, bool isKeyboard = false)
    {
        Device = device;
        Label = label;
        IsKeyboard = isKeyboard;
    }

    public string Device { get; }

    public string Label { get; }

    public bool IsKeyboard { get; }

    public override string ToString() => Label;
}

// What a device offers to pick from: its axes (names), buttons and hats (counts); null counts = not connected now.
public sealed class BindDeviceInputs
{
    public string Device { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public IReadOnlyList<string> Axes { get; init; } = [];
    public int Buttons { get; init; }
    public int Hats { get; init; }
    public bool Connected { get; init; }
}

// The Bind Studio's hand pick: only real game controllers (no driver entries, no vJoy, one entry per device), the
// inputs of the picked device that fit the relation (an axis relation: the device's axes; a button relation: its
// buttons and hat directions), the keyboard only for a button relation and only when the user picks it. After every
// bind change the relation list's bind column is taken from the engine's bind list, so every count (Bind Studio header,
// Start page, next steps, device cards) follows at once.
public sealed partial class MainWindowViewModel
{
    internal const string KeyboardDevice = "Keyboard";

    private static readonly string[] StandardAxes = ["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1", "JOY_SLIDER2"];

    private static readonly string[] HatDirectionSuffixes = ["U", "UR", "R", "DR", "D", "DL", "L", "UL"];

    public ObservableCollection<BindDeviceChoice> BindDeviceChoices { get; } = [];

    public ObservableCollection<string> BindInputChoices { get; } = [];

    [ObservableProperty]
    private BindDeviceChoice? selectedBindDeviceChoice;

    // The keyboard keys of the engine's input catalog (DirectInput names, "devices inputs").
    private readonly List<string> keyboardKeyCatalog = [];

    private bool syncingBindDeviceChoice;

    partial void OnSelectedBindDeviceChoiceChanged(BindDeviceChoice? value)
    {
        if (syncingBindDeviceChoice || value == null)
            return;
        // The user picked a device: an input that the device does not have (or that does not fit) is cleared.
        BindDeviceInput = value.Device;
        RebuildBindInputChoices();
        if (BindInputValue.Length > 0 && !BindInputChoices.Contains(BindInputValue, StringComparer.OrdinalIgnoreCase))
            BindInputValue = string.Empty;
    }

    partial void OnSelectedBindIsAxisChanged(bool value)
    {
        OnPropertyChanged(nameof(BindInputPlaceholder));
        RebuildBindPickOptions();
    }

    public string BindInputPlaceholder => SelectedBindIsAxis ? T("Pick an axis") : T("Pick a button or hat");

    // The devices of the hand pick from the device cards (connected, deduplicated, without driver entries) and the
    // profile's devices that are not connected now; the current bind's device always (it is what the bind shows).
    private void RebuildBindPickOptions()
    {
        List<BindDeviceInputs> known = BindPickDevices();
        List<BindDeviceChoice> choices = BuildBindDeviceChoices(known, SelectedBindIsAxis, BindDeviceInput);
        if (!choices.Select(c => c.Device + "\n" + c.Label).SequenceEqual(BindDeviceChoices.Select(c => c.Device + "\n" + c.Label)))
        {
            syncingBindDeviceChoice = true;
            try
            {
                BindDeviceChoices.Clear();
                foreach (BindDeviceChoice choice in choices)
                    BindDeviceChoices.Add(choice);
            }
            finally
            {
                syncingBindDeviceChoice = false;
            }
        }
        SyncBindDeviceChoice();
        RebuildBindInputChoices();
    }

    // The choice that shows BindDeviceInput (set by a selected bind, by Record or typed).
    private void SyncBindDeviceChoice()
    {
        string device = (BindDeviceInput ?? string.Empty).Trim();
        BindDeviceChoice? match = device.Length == 0
            ? null
            : BindDeviceChoices.FirstOrDefault(c => string.Equals(c.Device, device, StringComparison.OrdinalIgnoreCase))
              ?? BindDeviceChoices.FirstOrDefault(c => !c.IsKeyboard && SameDevice(c.Device, device));
        if (ReferenceEquals(match, SelectedBindDeviceChoice))
            return;
        syncingBindDeviceChoice = true;
        try
        {
            SelectedBindDeviceChoice = match;
        }
        finally
        {
            syncingBindDeviceChoice = false;
        }
    }

    private void RebuildBindInputChoices()
    {
        string device = (BindDeviceInput ?? string.Empty).Trim();
        BindDeviceInputs? info = BindPickDevices().FirstOrDefault(d => string.Equals(d.Device, device, StringComparison.OrdinalIgnoreCase))
                                 ?? BindPickDevices().FirstOrDefault(d => SameDevice(d.Device, device));
        List<string> inputs = device.Length == 0
            ? []
            : BuildBindInputChoices(info, string.Equals(device, KeyboardDevice, StringComparison.OrdinalIgnoreCase), SelectedBindIsAxis, keyboardKeyCatalog);
        // The current bind's input stays visible (an input JoyPro does not list for the device, e.g. bound elsewhere).
        string current = (BindInputValue ?? string.Empty).Trim();
        if (current.Length > 0 && !inputs.Contains(current, StringComparer.OrdinalIgnoreCase) && BindInputMatchesKind(current, SelectedBindIsAxis))
            inputs.Insert(0, current);
        if (inputs.SequenceEqual(BindInputChoices))
            return;
        string keep = BindInputValue ?? string.Empty;
        BindInputChoices.Clear();
        foreach (string input in inputs)
            BindInputChoices.Add(input);
        // Clearing the items source of the combo box must not take the picked input away.
        if (BindInputValue != keep)
            BindInputValue = keep;
    }

    private static bool BindInputMatchesKind(string input, bool axis)
    {
        bool isAxisToken = input.StartsWith("JOY_", StringComparison.OrdinalIgnoreCase) &&
                           !input.StartsWith("JOY_BTN", StringComparison.OrdinalIgnoreCase);
        return axis ? isAxisToken : !isAxisToken;
    }

    // The game controllers JoyPro knows now: the device cards (the Start page's "Your devices"), else the connected
    // devices of the engine's device list without driver entries.
    private List<BindDeviceInputs> BindPickDevices()
    {
        List<BindDeviceInputs> devices = [];
        foreach (DeviceCardItem card in DeviceCards)
        {
            if (card.IsVirtual)
                continue;
            HudDeviceInfo? info = lastHudDevices.FirstOrDefault(h => string.Equals(h.Device, card.Device, StringComparison.OrdinalIgnoreCase));
            devices.Add(new BindDeviceInputs
            {
                Device = card.Device,
                Name = card.DeviceName,
                Model = card.ModelName,
                Axes = info?.Axes.Where(a => a.Length > 0).ToList() ?? [],
                Buttons = card.Buttons,
                Hats = card.Hats,
                Connected = card.Connected
            });
        }
        if (devices.Count > 0)
            return devices;
        foreach (string device in ConnectedDevices.Concat(ProfileDevices))
        {
            string text = (device ?? string.Empty).Trim();
            if (text.Length == 0 || IsPseudoDevice(text) || text.Contains("vJoy", StringComparison.OrdinalIgnoreCase) ||
                devices.Any(d => SameDevice(d.Device, text)))
                continue;
            HudDeviceInfo? info = lastHudDevices.FirstOrDefault(h => SameDevice(h.Device, text));
            devices.Add(new BindDeviceInputs
            {
                Device = text,
                Name = DeviceProbe.RemoveGuid(text),
                Axes = info?.Axes.Where(a => a.Length > 0).ToList() ?? [],
                Buttons = info?.Buttons ?? 0,
                Hats = info?.Hats ?? 0,
                Connected = info != null || ConnectedDevices.Contains(text, StringComparer.OrdinalIgnoreCase)
            });
        }
        return devices;
    }

    // The device list of the hand pick. An axis relation: devices with axes (and devices JoyPro cannot look into now);
    // a button relation: devices with buttons or hats, then the keyboard. Names without GUID unless two are alike.
    internal static List<BindDeviceChoice> BuildBindDeviceChoices(IReadOnlyList<BindDeviceInputs> devices, bool axis, string? currentDevice)
    {
        List<BindDeviceInputs> fitting = [];
        foreach (BindDeviceInputs device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.Device) || fitting.Any(d => string.Equals(d.Device, device.Device, StringComparison.OrdinalIgnoreCase)))
                continue;
            bool known = device.Connected && (device.Axes.Count > 0 || device.Buttons > 0 || device.Hats > 0);
            bool fits = !known || (axis ? device.Axes.Count > 0 : device.Buttons > 0 || device.Hats > 0);
            if (fits)
                fitting.Add(device);
        }

        string current = (currentDevice ?? string.Empty).Trim();
        bool currentIsKeyboard = string.Equals(current, KeyboardDevice, StringComparison.OrdinalIgnoreCase);
        if (current.Length > 0 && !currentIsKeyboard && !fitting.Any(d => string.Equals(d.Device, current, StringComparison.OrdinalIgnoreCase)) &&
            !fitting.Any(d => SameDevice(d.Device, current)))
            fitting.Add(new BindDeviceInputs { Device = current, Name = DeviceProbe.RemoveGuid(current) });

        string BaseLabel(BindDeviceInputs d)
        {
            string name = d.Name.Length > 0 ? DeviceProbe.RemoveGuid(d.Name) : DeviceProbe.RemoveGuid(d.Device);
            // The name Windows shows (the user may have named it, "L-VPC Stick WarBRD"); a nameless device by its model.
            if (name.Length == 0)
                name = d.Model.Length > 0 ? d.Model : d.Device;
            return d.Connected || d.Axes.Count > 0 || d.Buttons > 0 ? name : T("{0} (not connected)", name);
        }

        List<(BindDeviceInputs Device, string Label)> labelled = fitting.Select(d => (d, BaseLabel(d))).ToList();
        List<BindDeviceChoice> choices = [];
        foreach ((BindDeviceInputs device, string label) in labelled)
        {
            bool alike = labelled.Count(l => string.Equals(l.Label, label, StringComparison.OrdinalIgnoreCase)) > 1;
            string shown = label;
            if (alike)
            {
                int open = device.Device.LastIndexOf('{');
                shown = open >= 0 ? label + " " + device.Device[open..] : label;
            }
            choices.Add(new BindDeviceChoice(device.Device, shown));
        }
        choices = choices.OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
        if (!axis || currentIsKeyboard)
            choices.Add(new BindDeviceChoice(KeyboardDevice, "Keyboard", isKeyboard: true));
        return choices;
    }

    // The inputs of the picked device for the relation's kind. Unknown inputs (not connected): the usual axes, or
    // 32 buttons and one hat.
    internal static List<string> BuildBindInputChoices(BindDeviceInputs? device, bool isKeyboard, bool axis, IReadOnlyList<string> keyboardKeys)
    {
        List<string> inputs = [];
        if (isKeyboard)
        {
            if (!axis)
                inputs.AddRange(keyboardKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase));
            return inputs;
        }
        bool known = device is { Connected: true } && (device.Axes.Count > 0 || device.Buttons > 0 || device.Hats > 0);
        if (axis)
        {
            inputs.AddRange(known ? device!.Axes.Distinct(StringComparer.OrdinalIgnoreCase) : StandardAxes);
            return inputs;
        }
        int buttons = known ? Math.Min(128, device!.Buttons) : 32;
        int hats = known ? Math.Min(4, device!.Hats) : 1;
        for (int i = 1; i <= buttons; i++)
            inputs.Add("JOY_BTN" + i.ToString(CultureInfo.InvariantCulture));
        for (int h = 1; h <= hats; h++)
        {
            foreach (string direction in HatDirectionSuffixes)
                inputs.Add("JOY_BTN_POV" + h.ToString(CultureInfo.InvariantCulture) + "_" + direction);
        }
        return inputs;
    }

    // "devices inputs" (text): the DirectInput keyboard names are the keys a bind takes ("Keyboard Keys (DirectInput):").
    internal static List<string> ParseKeyboardKeyCatalog(string output)
    {
        List<string> keys = [];
        bool inSection = false;
        foreach (string raw in (output ?? string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                inSection = trimmed.StartsWith("Keyboard Keys (DirectInput)", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (inSection)
                keys.Add(trimmed[2..].Trim());
        }
        return keys;
    }

    // The name of a device in the status line: as the hand pick shows it, else the name without its instance GUID.
    private string DeviceLabelForStatus(string? device)
    {
        string text = (device ?? string.Empty).Trim();
        BindDeviceChoice? choice = BindDeviceChoices.FirstOrDefault(c => string.Equals(c.Device, text, StringComparison.OrdinalIgnoreCase));
        if (choice != null)
            return choice.Label;
        DeviceCardItem? card = DeviceCards.FirstOrDefault(c => string.Equals(c.Device, text, StringComparison.OrdinalIgnoreCase));
        return card != null ? card.DeviceName : DeviceProbe.RemoveGuid(text);
    }

    // "Bound Pitch to L-VPC Stick WarBRD JOY_Y." after Set Bind or Record (from the engine's bind list).
    private string BoundStatus(string relation)
    {
        BindListItem? bind = allBinds.FirstOrDefault(b => string.Equals(b.RelationName, relation, StringComparison.OrdinalIgnoreCase));
        if (bind == null || !bind.HasBind)
            return T("Saved the bind of {0}.", relation);
        return T("Bound {0} to {1} {2}.", relation, DeviceLabelForStatus(bind.Device), bind.Input);
    }

    // ---- bound counts ----

    // The engine's bind list is the truth after Set Bind, Record, Clear and Unbind: the relations take their bind from
    // it (the relation list's "bind" column was read before the change), and the profile's devices gain the devices
    // bound now. True when anything changed.
    private bool SyncRelationBindsFromBinds()
    {
        Dictionary<string, BindListItem> byRelation = allBinds
            .Where(b => !string.IsNullOrWhiteSpace(b.RelationName))
            .GroupBy(b => b.RelationName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (RelationListItem relation in allRelations)
        {
            string bind = byRelation.TryGetValue(relation.Name.Trim(), out BindListItem? item) && item.HasBind &&
                          !string.IsNullOrWhiteSpace(item.Device) && !string.IsNullOrWhiteSpace(item.Input) &&
                          !item.Device.Equals("<unbound>", StringComparison.OrdinalIgnoreCase) && !item.Input.Equals("<unbound>", StringComparison.OrdinalIgnoreCase)
                ? item.Device.Trim() + "::" + item.Input.Trim()
                : "unbound";
            if (string.Equals(relation.Bind, bind, StringComparison.Ordinal))
                continue;
            // "unbound" and an empty bind column mean the same.
            if (bind == "unbound" && !relation.IsBound)
                continue;
            relation.Bind = bind;
            changed = true;
        }

        bool devicesChanged = false;
        foreach (BindListItem bind in allBinds)
        {
            string device = (bind.Device ?? string.Empty).Trim();
            if (!bind.HasBind || device.Length == 0 || device.Equals("<unbound>", StringComparison.OrdinalIgnoreCase) ||
                device.Equals(KeyboardDevice, StringComparison.OrdinalIgnoreCase))
                continue;
            if (ProfileDevices.Any(d => string.Equals(d, device, StringComparison.OrdinalIgnoreCase)))
                continue;
            ProfileDevices.Add(device);
            devicesChanged = true;
        }
        if (devicesChanged && (lastHudDevices.Count > 0 || DeviceCards.Count > 0))
            _ = RebuildDeviceCardsAsync(announce: false);
        return changed || devicesChanged;
    }
}
