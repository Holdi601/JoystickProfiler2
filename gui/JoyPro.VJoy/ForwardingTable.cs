// Explicit usings: the GUI tests link this file into a project without implicit usings.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using JoyPro.Engine.VJoy;

namespace JoyPro.VJoy;

/// <summary>vJoy's JOYSTICK_POSITION_V2 (vJoy SDK public.h), passed to UpdateVJD as is.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VJoyPosition
{
    public byte bDevice;
    public int wThrottle;
    public int wRudder;
    public int wAileron;
    public int wAxisX;
    public int wAxisY;
    public int wAxisZ;
    public int wAxisXRot;
    public int wAxisYRot;
    public int wAxisZRot;
    public int wSlider;
    public int wDial;
    public int wWheel;
    public int wAxisVX;
    public int wAxisVY;
    public int wAxisVZ;
    public int wAxisVBRX;
    public int wAxisVBRY;
    public int wAxisVBRZ;
    public int lButtons;
    public uint bHats;
    public uint bHatsEx1;
    public uint bHatsEx2;
    public uint bHatsEx3;
    public int lButtonsEx1;
    public int lButtonsEx2;
    public int lButtonsEx3;

    /// <summary>Axis 0..7 = X, Y, Z, Rx, Ry, Rz, Slider 0, Slider 1 (vJoyConfig's x y z rx ry rz sl0 sl1).</summary>
    public int GetAxis(int axis) => axis switch
    {
        0 => wAxisX,
        1 => wAxisY,
        2 => wAxisZ,
        3 => wAxisXRot,
        4 => wAxisYRot,
        5 => wAxisZRot,
        6 => wSlider,
        _ => wDial
    };

    public void SetAxis(int axis, int value)
    {
        switch (axis)
        {
            case 0: wAxisX = value; break;
            case 1: wAxisY = value; break;
            case 2: wAxisZ = value; break;
            case 3: wAxisXRot = value; break;
            case 4: wAxisYRot = value; break;
            case 5: wAxisZRot = value; break;
            case 6: wSlider = value; break;
            default: wDial = value; break;
        }
    }

    public readonly bool GetButton(int button)
    {
        var word = (button >> 5) switch { 0 => lButtons, 1 => lButtonsEx1, 2 => lButtonsEx2, _ => lButtonsEx3 };
        return (word & (1 << (button & 31))) != 0;
    }

    public void SetButton(int button, bool down)
    {
        var bit = 1 << (button & 31);
        switch (button >> 5)
        {
            case 0: lButtons = down ? lButtons | bit : lButtons & ~bit; break;
            case 1: lButtonsEx1 = down ? lButtonsEx1 | bit : lButtonsEx1 & ~bit; break;
            case 2: lButtonsEx2 = down ? lButtonsEx2 | bit : lButtonsEx2 & ~bit; break;
            default: lButtonsEx3 = down ? lButtonsEx3 | bit : lButtonsEx3 & ~bit; break;
        }
    }

    /// <summary>A continuous hat (0..3): 0..35999 hundredths of a degree, uint.MaxValue = centred.</summary>
    public readonly uint GetHat(int hat) => hat switch { 0 => bHats, 1 => bHatsEx1, 2 => bHatsEx2, _ => bHatsEx3 };

    public void SetHat(int hat, uint value)
    {
        switch (hat)
        {
            case 0: bHats = value; break;
            case 1: bHatsEx1 = value; break;
            case 2: bHatsEx2 = value; break;
            default: bHatsEx3 = value; break;
        }
    }

    /// <summary>A discrete (4-way) hat: four bits per hat in bHats, 0 N, 1 E, 2 S, 3 W, 0xF centred.</summary>
    public void SetDiscreteHat(int hat, uint direction)
    {
        var shift = (hat & 3) * 4;
        bHats = (bHats & ~(0xFu << shift)) | ((direction & 0xF) << shift);
    }

    public readonly uint GetDiscreteHat(int hat) => (bHats >> ((hat & 3) * 4)) & 0xF;
}

/// <summary>
/// The forwarding table of JoyPro.VJoy: per physical device the slots it feeds, applied to the vJoy positions.
/// </summary>
/// <remarks>
/// The device state is JoyPro's DirectInput data format (DIJOYSTATE2's first part, 176 bytes): 8 axes (X, Y, Z, Rx,
/// Ry, Rz, Slider 0, Slider 1) as int at 0..31, 4 hats as uint at 32..47 (hundredths of a degree, low word 0xFFFF =
/// centred), 128 buttons as byte at 48..175 (bit 7 = down). Axis values go over 1:1 (DirectInput is set to vJoy's
/// range, so no scaling happens; JoyPro's curves stay in the game). <see cref="Apply"/> allocates nothing.
/// </remarks>
public sealed class ForwardingTable
{
    public const int StateSize = 176;
    public const int AxisOffset = 0;
    public const int HatOffset = 32;
    public const int ButtonOffset = 48;
    public const uint HatCentred = uint.MaxValue;

    private const byte KindAxis = 0;
    private const byte KindButton = 1;
    private const byte KindHat = 2;

    private readonly Slot[][] _bySource;
    private readonly VJoyPosition[] _positions;
    private readonly bool[] _discreteHats;
    private readonly int _axisCentre;

    public ForwardingTable(ForwarderConfig config, int axisMin = 0, int axisMax = 32767, IReadOnlyDictionary<int, bool>? discreteHats = null)
    {
        Config = config;
        DeviceIds = config.VJoyDevices.ToArray();
        _positions = new VJoyPosition[DeviceIds.Length];
        _discreteHats = DeviceIds.Select(id => discreteHats != null && discreteHats.TryGetValue(id, out var discrete) && discrete).ToArray();
        _axisCentre = axisMin + (axisMax - axisMin + 1) / 2;
        AxisMin = axisMin;
        AxisMax = axisMax;
        var lists = Enumerable.Range(0, config.Sources.Count).Select(_ => new List<Slot>()).ToArray();
        foreach (var slot in config.Slots)
        {
            var device = Array.IndexOf(DeviceIds, slot.Device);
            if (device < 0 || slot.Source < 0 || slot.Source >= lists.Length)
                continue;
            var (kind, limit) = slot.Kind switch
            {
                "axis" => (KindAxis, 8),
                "button" => (KindButton, 128),
                _ => (KindHat, 4)
            };
            if (slot.Index < 0 || slot.Index >= limit || slot.Target < 0 || slot.Target >= limit)
                continue;
            lists[slot.Source].Add(new Slot(kind, (byte)slot.Index, (byte)device, (byte)slot.Target));
        }

        _bySource = lists.Select(l => l.ToArray()).ToArray();
        SlotCount = _bySource.Sum(s => s.Length);
        Reset();
    }

    public ForwarderConfig Config { get; }

    /// <summary>The vJoy device ids, index = position in <see cref="Positions"/>.</summary>
    public int[] DeviceIds { get; }

    public int AxisMin { get; }

    public int AxisMax { get; }

    public int SlotCount { get; }

    public int SourceCount => _bySource.Length;

    public Span<VJoyPosition> Positions => _positions;

    public ref VJoyPosition Position(int deviceIndex) => ref _positions[deviceIndex];

    /// <summary>Everything neutral: axes centred, buttons up, hats centred.</summary>
    public void Reset()
    {
        for (var i = 0; i < _positions.Length; i++)
        {
            ref var p = ref _positions[i];
            p = default;
            p.bDevice = (byte)DeviceIds[i];
            for (var axis = 0; axis < 8; axis++)
                p.SetAxis(axis, _axisCentre);
            if (_discreteHats[i])
                p.bHats = 0xFFFFFFFF;
            else
                p.bHats = p.bHatsEx1 = p.bHatsEx2 = p.bHatsEx3 = HatCentred;
        }
    }

    /// <summary>Copies the slots of one physical device from its state (<see cref="StateSize"/> bytes).</summary>
    public void Apply(int source, ReadOnlySpan<byte> state)
    {
        if ((uint)source >= (uint)_bySource.Length || state.Length < StateSize)
            return;
        var slots = _bySource[source];
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            ref var p = ref _positions[slot.Device];
            switch (slot.Kind)
            {
                case KindAxis:
                    var value = BinaryPrimitives.ReadInt32LittleEndian(state.Slice(AxisOffset + slot.Index * 4, 4));
                    p.SetAxis(slot.Target, Math.Clamp(value, AxisMin, AxisMax));
                    break;
                case KindButton:
                    p.SetButton(slot.Target, (state[ButtonOffset + slot.Index] & 0x80) != 0);
                    break;
                default:
                    var hat = BinaryPrimitives.ReadUInt32LittleEndian(state.Slice(HatOffset + slot.Index * 4, 4));
                    SetHat(ref p, slot.Device, slot.Target, hat);
                    break;
            }
        }
    }

    /// <summary>A device that is gone: its buttons go up and its hats centre; its axes keep the last value.</summary>
    public void Release(int source)
    {
        if ((uint)source >= (uint)_bySource.Length)
            return;
        foreach (var slot in _bySource[source])
        {
            ref var p = ref _positions[slot.Device];
            if (slot.Kind == KindButton)
                p.SetButton(slot.Target, false);
            else if (slot.Kind == KindHat)
                SetHat(ref p, slot.Device, slot.Target, HatCentred);
        }
    }

    /// <summary>
    /// The virtual functions' outputs over the table: every button, axis and hat a function owns is replaced by what
    /// the functions wrote in their last pass (device index = position in <see cref="DeviceIds"/>). Allocates nothing.
    /// </summary>
    public void MergeFunctions(VfRuntime functions)
    {
        var devices = Math.Min(_positions.Length, functions.DeviceCount);
        for (var d = 0; d < devices; d++)
        {
            ref var p = ref _positions[d];
            var w = d * 4;
            p.lButtons = (p.lButtons & ~functions.OwnedButtons[w]) | functions.Buttons[w];
            p.lButtonsEx1 = (p.lButtonsEx1 & ~functions.OwnedButtons[w + 1]) | functions.Buttons[w + 1];
            p.lButtonsEx2 = (p.lButtonsEx2 & ~functions.OwnedButtons[w + 2]) | functions.Buttons[w + 2];
            p.lButtonsEx3 = (p.lButtonsEx3 & ~functions.OwnedButtons[w + 3]) | functions.Buttons[w + 3];
            for (var axis = 0; axis < 8; axis++)
            {
                if (functions.AxisOwned[d * 8 + axis])
                    p.SetAxis(axis, Math.Clamp(functions.Axes[d * 8 + axis], AxisMin, AxisMax));
            }

            for (var hat = 0; hat < 4; hat++)
            {
                if (functions.HatOwned[w + hat])
                    SetHat(ref p, d, hat, functions.Hats[w + hat]);
            }
        }
    }

    private void SetHat(ref VJoyPosition p, int device, int target, uint raw)
    {
        var centred = (raw & 0xFFFF) == 0xFFFF;
        if (_discreteHats[device])
        {
            // Nearest of the four directions (diagonals round clockwise: NE -> E).
            p.SetDiscreteHat(target, centred ? 0xFu : ((raw % 36000) + 4500) / 9000 % 4);
            return;
        }

        p.SetHat(target, centred ? HatCentred : raw % 36000);
    }

    private readonly record struct Slot(byte Kind, byte Index, byte Device, byte Target);
}

/// <summary>The forwarding table as the engine writes it (engine vjoy forwarder-config).</summary>
public sealed class ForwarderConfig
{
    public int Version { get; init; } = 1;

    public List<int> VJoyDevices { get; init; } = [];

    public List<ForwarderSource> Sources { get; init; } = [];

    public List<ForwarderSlot> Slots { get; init; } = [];

    public List<string> VirtualGames { get; init; } = [];

    /// <summary>The game whose virtual Xbox pad table this is (empty: no Xbox pad).</summary>
    public string XboxGame { get; init; } = "";

    public List<XboxSlotConfig> Xbox { get; init; } = [];

    /// <summary>The virtual functions, resolved by the engine (source and device indices filled).</summary>
    public List<VirtualFunction> Functions { get; init; } = [];

    /// <summary>A virtual Xbox pad is needed: Xbox slots, or a function that writes the pad.</summary>
    public bool UsesXbox => Xbox.Count > 0 || Functions.Any(f => f.Enabled && f.Outputs.Any(o => o.Kind is "xbutton" or "xaxis" or "xdpad"));

    public static ForwarderConfig Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var config = new ForwarderConfig
        {
            Version = Int(root, "version", 1),
            VJoyDevices = root.TryGetProperty("vjoyDevices", out var devices) && devices.ValueKind == JsonValueKind.Array
                ? devices.EnumerateArray().Select(d => d.GetInt32()).Where(d => d is >= 1 and <= 16).Distinct().ToList()
                : [],
            VirtualGames = root.TryGetProperty("virtualGames", out var games) && games.ValueKind == JsonValueKind.Array
                ? games.EnumerateArray().Select(g => g.GetString() ?? "").ToList()
                : [],
            XboxGame = Text(root, "xboxGame")
        };
        if (root.TryGetProperty("xbox", out var xbox) && xbox.ValueKind == JsonValueKind.Array)
        {
            foreach (var slot in xbox.EnumerateArray())
                config.Xbox.Add(new XboxSlotConfig(Int(slot, "source", -1), Text(slot, "kind"), Int(slot, "index", -1), Text(slot, "target")));
        }
        if (root.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in sources.EnumerateArray())
            {
                var guidText = Text(source, "instanceGuid");
                config.Sources.Add(new ForwarderSource(Text(source, "device"), Text(source, "name"), Guid.TryParse(guidText, out var guid) ? guid : Guid.Empty));
            }
        }

        if (root.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
        {
            foreach (var slot in slots.EnumerateArray())
                config.Slots.Add(new ForwarderSlot(Int(slot, "source", -1), Text(slot, "kind"), Int(slot, "index", -1), Int(slot, "device", 0), Int(slot, "target", -1)));
        }

        if (root.TryGetProperty("functions", out var functions) && functions.ValueKind == JsonValueKind.Array)
        {
            var list = JsonSerializer.Deserialize<List<VirtualFunction>>(functions.GetRawText(), FunctionJson) ?? [];
            foreach (var function in list.Where(f => f != null))
            {
                function.Normalize();
                config.Functions.Add(function);
            }
        }

        return config;
    }

    private static readonly JsonSerializerOptions FunctionJson = new() { PropertyNameCaseInsensitive = true };

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int Int(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;
}

public sealed record ForwarderSource(string Device, string Name, Guid InstanceGuid);

/// <summary>One slot: physical input (kind axis/button/hat, 0-based index) of a source -> vJoy device + 0-based target.</summary>
public sealed record ForwarderSlot(int Source, string Kind, int Index, int Device, int Target);

/// <summary>One Xbox slot: physical input of a source -> Xbox control (XB_LX, XB_A, XB_LY+, XB_DPAD ...).</summary>
public sealed record XboxSlotConfig(int Source, string Kind, int Index, string Target);

/// <summary>The XInput report of the virtual Xbox 360 pad (XINPUT_GAMEPAD layout).</summary>
public struct XboxReport
{
    public const ushort DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008, Start = 0x0010, Back = 0x0020,
        LeftThumb = 0x0040, RightThumb = 0x0080, LeftShoulder = 0x0100, RightShoulder = 0x0200, Guide = 0x0400, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short LeftX;
    public short LeftY;
    public short RightX;
    public short RightY;
}

/// <summary>
/// The virtual Xbox pad's table: physical device states in, one XInput report out. Every change recomputes the report
/// from the last state of every source (two buttons on one control combine; a pressed button on a stick half wins over
/// the axis on that stick; triggers take the larger value). Stick Y follows XInput (up = positive), so a physical axis
/// is inverted on the Y sticks (DirectInput: forward = smaller). <see cref="Apply"/> allocates nothing.
/// </summary>
public sealed class XboxTable
{
    // Control codes: 0..5 axes LX LY RX RY LT RT; 10..25 buttons (mask); 30..37 stick halves (axis*2 + (minus ? 1 : 0)); 40 D-pad.
    private const byte KindAxis = 0, KindButton = 1, KindHat = 2;
    private const byte CodeLx = 0, CodeLy = 1, CodeRx = 2, CodeRy = 3, CodeLt = 4, CodeRt = 5, CodeTriggers = 6, CodeButton = 10, CodeHalf = 30, CodeDPad = 40;

    private static readonly (string Token, ushort Mask)[] ButtonMasks =
    [
        ("XB_A", XboxReport.A), ("XB_B", XboxReport.B), ("XB_X", XboxReport.X), ("XB_Y", XboxReport.Y), ("XB_LB", XboxReport.LeftShoulder),
        ("XB_RB", XboxReport.RightShoulder), ("XB_BACK", XboxReport.Back), ("XB_START", XboxReport.Start), ("XB_LS", XboxReport.LeftThumb),
        ("XB_RS", XboxReport.RightThumb), ("XB_GUIDE", XboxReport.Guide), ("XB_DPAD_UP", XboxReport.DPadUp), ("XB_DPAD_DOWN", XboxReport.DPadDown),
        ("XB_DPAD_LEFT", XboxReport.DPadLeft), ("XB_DPAD_RIGHT", XboxReport.DPadRight)
    ];

    private static readonly string[] AxisTokens = ["XB_LX", "XB_LY", "XB_RX", "XB_RY", "XB_LT", "XB_RT"];

    private readonly Slot[] _slots;
    private readonly byte[][] _states;
    private readonly int _axisMin;
    private readonly int _axisMax;
    private XboxReport _report;

    public XboxTable(ForwarderConfig config, int axisMin = 0, int axisMax = 32767)
    {
        _axisMin = axisMin;
        _axisMax = Math.Max(axisMin + 1, axisMax);
        Game = config.XboxGame;
        var slots = new List<Slot>();
        foreach (var slot in config.Xbox)
        {
            if (slot.Source < 0 || slot.Source >= config.Sources.Count)
                continue;
            var kind = slot.Kind switch { "axis" => KindAxis, "button" => KindButton, _ => KindHat };
            var limit = kind == KindAxis ? 8 : kind == KindButton ? 128 : 4;
            var target = (slot.Target ?? "").Trim();
            var invert = target.EndsWith('~');
            if (invert)
                target = target[..^1];
            if (slot.Index < 0 || slot.Index >= limit || !TryCode(target, out var code, out var mask))
                continue;
            var fits = kind switch
            {
                KindAxis => code <= CodeTriggers,
                KindHat => code == CodeDPad,
                _ => code == CodeButton || code >= CodeHalf && code < CodeDPad || code == CodeLt || code == CodeRt
            };
            if (fits)
                slots.Add(new Slot(kind, (byte)slot.Index, (byte)slot.Source, code, mask, invert));
        }

        _slots = slots.ToArray();
        _states = new byte[config.Sources.Count][];
        for (var i = 0; i < _states.Length; i++)
        {
            _states[i] = new byte[ForwardingTable.StateSize];
            ResetState(_states[i]);
        }

        Recompute();
    }

    public string Game { get; }

    public int SlotCount => _slots.Length;

    public bool Active => _slots.Length > 0;

    public ref XboxReport Report => ref _report;

    /// <summary>Takes the new state of one physical device and recomputes the report.</summary>
    public void Apply(int source, ReadOnlySpan<byte> state)
    {
        if ((uint)source >= (uint)_states.Length || state.Length < ForwardingTable.StateSize)
            return;
        state[..ForwardingTable.StateSize].CopyTo(_states[source]);
        Recompute();
    }

    /// <summary>
    /// The report with the virtual functions' Xbox outputs over it: owned buttons replaced, owned axes set (sticks: the
    /// function's 0..1 around the centre, Y up = positive like the table; triggers 0..255). Allocates nothing.
    /// </summary>
    public void MergeFunctions(VfRuntime functions, ref XboxReport output)
    {
        output = _report;
        output.Buttons = (ushort)((output.Buttons & ~functions.XboxOwnedButtons) | functions.XboxButtons);
        for (var i = 0; i < 6; i++)
        {
            if (!functions.XboxAxisOwned[i])
                continue;
            var unit = Math.Clamp(functions.XboxAxes[i], 0.0, 1.0);
            if (i >= 4)
            {
                var trigger = (byte)Math.Round(unit * 255);
                if (i == 4)
                    output.LeftTrigger = trigger;
                else
                    output.RightTrigger = trigger;
                continue;
            }

            var signed = (unit - 0.5) * 2;
            if (i is 1 or 3)
                signed = -signed;
            var value = (short)Math.Clamp(Math.Round(signed * 32767), -32768, 32767);
            switch (i)
            {
                case 0: output.LeftX = value; break;
                case 1: output.LeftY = value; break;
                case 2: output.RightX = value; break;
                default: output.RightY = value; break;
            }
        }
    }

    /// <summary>A device that is gone: its buttons go up and its hats centre; its axes keep the last value.</summary>
    public void Release(int source)
    {
        if ((uint)source >= (uint)_states.Length)
            return;
        var state = _states[source];
        state.AsSpan(ForwardingTable.ButtonOffset, 128).Clear();
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(ForwardingTable.HatOffset + i * 4, 4), ForwardingTable.HatCentred);
        Recompute();
    }

    private void Recompute()
    {
        _report = default;
        // Sticks centre, triggers up.
        Span<int> half = stackalloc int[4];
        Span<bool> halfSet = stackalloc bool[4];
        foreach (var slot in _slots)
        {
            var state = _states[slot.Source];
            switch (slot.Kind)
            {
                case KindAxis:
                {
                    var raw = BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(ForwardingTable.AxisOffset + slot.Index * 4, 4));
                    if (slot.Invert)
                        raw = _axisMax - (Math.Clamp(raw, _axisMin, _axisMax) - _axisMin);
                    var unit = (Math.Clamp(raw, _axisMin, _axisMax) - _axisMin) / (double)(_axisMax - _axisMin); // 0..1
                    if (slot.Code == CodeTriggers)
                    {
                        // Centre split: above the centre the right trigger, below it the left one.
                        var up = (byte)Math.Round(Math.Max(0, unit - 0.5) * 2 * 255);
                        var down = (byte)Math.Round(Math.Max(0, 0.5 - unit) * 2 * 255);
                        _report.RightTrigger = Math.Max(_report.RightTrigger, up);
                        _report.LeftTrigger = Math.Max(_report.LeftTrigger, down);
                    }
                    else if (slot.Code == CodeLt || slot.Code == CodeRt)
                    {
                        var value = (byte)Math.Round(unit * 255);
                        if (slot.Code == CodeLt)
                            _report.LeftTrigger = Math.Max(_report.LeftTrigger, value);
                        else
                            _report.RightTrigger = Math.Max(_report.RightTrigger, value);
                    }
                    else
                    {
                        // Around the axis centre, so a centred stick gives exactly 0.
                        var clamped = Math.Clamp(raw, _axisMin, _axisMax);
                        var centre = _axisMin + (_axisMax - _axisMin + 1) / 2;
                        var signed = clamped >= centre ? (clamped - centre) / (double)Math.Max(1, _axisMax - centre) : (clamped - centre) / (double)Math.Max(1, centre - _axisMin);
                        if (slot.Code == CodeLy || slot.Code == CodeRy)
                            signed = -signed;
                        SetStick(slot.Code, (short)Math.Clamp(Math.Round(signed * 32767), -32768, 32767));
                    }

                    break;
                }
                case KindButton:
                {
                    if ((state[ForwardingTable.ButtonOffset + slot.Index] & 0x80) == 0)
                        break;
                    if (slot.Code == CodeButton)
                        _report.Buttons |= slot.Mask;
                    else if (slot.Code == CodeLt)
                        _report.LeftTrigger = 255;
                    else if (slot.Code == CodeRt)
                        _report.RightTrigger = 255;
                    else
                    {
                        var axis = (slot.Code - CodeHalf) / 2;
                        half[axis] += (slot.Code - CodeHalf) % 2 == 0 ? 32767 : -32767;
                        halfSet[axis] = true;
                    }

                    break;
                }
                default:
                {
                    var raw = BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(ForwardingTable.HatOffset + slot.Index * 4, 4));
                    if ((raw & 0xFFFF) == 0xFFFF)
                        break;
                    var direction = (int)(((raw % 36000) + 2250) / 4500 % 8); // 0 U, 1 UR, 2 R ... 7 UL
                    _report.Buttons |= direction switch
                    {
                        0 => XboxReport.DPadUp,
                        1 => (ushort)(XboxReport.DPadUp | XboxReport.DPadRight),
                        2 => XboxReport.DPadRight,
                        3 => (ushort)(XboxReport.DPadDown | XboxReport.DPadRight),
                        4 => XboxReport.DPadDown,
                        5 => (ushort)(XboxReport.DPadDown | XboxReport.DPadLeft),
                        6 => XboxReport.DPadLeft,
                        _ => (ushort)(XboxReport.DPadUp | XboxReport.DPadLeft)
                    };
                    break;
                }
            }
        }

        for (byte axis = 0; axis < 4; axis++)
        {
            if (halfSet[axis])
                SetStick(axis, (short)Math.Clamp(half[axis], -32768, 32767));
        }
    }

    private void SetStick(byte code, short value)
    {
        switch (code)
        {
            case CodeLx: _report.LeftX = value; break;
            case CodeLy: _report.LeftY = value; break;
            case CodeRx: _report.RightX = value; break;
            default: _report.RightY = value; break;
        }
    }

    private static void ResetState(byte[] state)
    {
        Array.Clear(state);
        // Axes in the middle until the device reports, hats centred.
        for (var i = 0; i < 8; i++)
            BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(ForwardingTable.AxisOffset + i * 4, 4), 16384);
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(ForwardingTable.HatOffset + i * 4, 4), ForwardingTable.HatCentred);
    }

    private static bool TryCode(string token, out byte code, out ushort mask)
    {
        var value = (token ?? "").Trim().ToUpperInvariant();
        mask = 0;
        code = 0;
        var axis = Array.IndexOf(AxisTokens, value);
        if (axis >= 0)
        {
            code = (byte)axis;
            return true;
        }

        if (value == "XB_DPAD")
        {
            code = CodeDPad;
            return true;
        }

        if (value == "XB_TRIGGERS")
        {
            code = CodeTriggers;
            return true;
        }

        foreach (var (name, bit) in ButtonMasks)
        {
            if (name == value)
            {
                code = CodeButton;
                mask = bit;
                return true;
            }
        }

        if (value.Length > 2 && (value[^1] == '+' || value[^1] == '-'))
        {
            var stick = Array.IndexOf(AxisTokens, value[..^1]);
            if (stick is >= 0 and < 4)
            {
                code = (byte)(CodeHalf + stick * 2 + (value[^1] == '-' ? 1 : 0));
                return true;
            }
        }

        return false;
    }

    private readonly record struct Slot(byte Kind, byte Index, byte Source, byte Code, ushort Mask, bool Invert);
}
