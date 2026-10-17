#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.VJoy
{
    // Keyboard output of macros and functions with "Keyboard output" on (JoyPro.VJoy: SendInput with scan codes; tests
    // and dry runs record). scan: set-1 scan code, + 0x100 = extended key.
    public interface IVfKeySink
    {
        void Key(int scan, bool down);
    }

    // The evaluation of the virtual functions (docs/virtual-joystick.md "Virtual functions"). JoyPro.VJoy keeps one per
    // run: it copies every physical device state that changed in (SetState, the forwarder's data format: 8 axes as int
    // at 0..31, 4 hats as uint at 32..47, 128 buttons as byte at 48..175), calls Tick every pass (also when nothing
    // changed: pulses, turbo, macros and long presses need the clock) and merges the outputs into the vJoy positions and
    // the Xbox report. Tick allocates nothing: every buffer is made when the runtime is built.
    //
    // Outputs: every vJoy button, axis and hat a function writes is "owned" (OwnedButtons, AxisOwned, HatOwned): the
    // merge replaces exactly those, so a function also overrides a slot of the forwarding table it owns (a shift layer's
    // unshifted output). Two functions on one button combine (pressed when either presses it).
    public sealed class VfRuntime
    {
        public const int StateSize = 176;
        public const int AxisOffset = 0;
        public const int HatOffset = 32;
        public const int ButtonOffset = 48;
        public const uint HatCentred = uint.MaxValue;

        internal const byte InNone = 0, InAxis = 1, InButton = 2, InHat = 3, InHatDir = 4;
        internal const byte OutNone = 0, OutVButton = 1, OutVAxis = 2, OutVHat = 3, OutXButton = 4, OutXAxis = 5, OutXDpad = 6, OutKey = 7;

        private readonly byte[][] states;
        private readonly List<int> keyScans = new List<int>();
        private bool[] keyLevel;
        private bool[] keySent;

        // Per vJoy device index d: buttons in words d*4..d*4+3, axes d*8.., hats d*4...
        public readonly int[] Buttons;
        public readonly int[] OwnedButtons;
        public readonly int[] Axes;
        public readonly bool[] AxisOwned;
        public readonly uint[] Hats;
        public readonly bool[] HatOwned;
        // The virtual Xbox pad: button mask (XINPUT_GAMEPAD), axes 0..1 (LX LY RX RY: 0.5 = centre, DirectInput
        // direction: Y up = smaller; LT RT: 0 = up).
        public ushort XboxButtons;
        public ushort XboxOwnedButtons;
        public readonly double[] XboxAxes = new double[6];
        public readonly bool[] XboxAxisOwned = new bool[6];

        public VfRuntime(IList<VirtualFunction> functions, int sourceCount, int deviceCount, int axisMin = 0, int axisMax = 32767, IVfKeySink keys = null)
        {
            AxisMin = axisMin;
            AxisMax = Math.Max(axisMin + 1, axisMax);
            DeviceCount = Math.Max(0, deviceCount);
            KeySink = keys;
            states = new byte[Math.Max(0, sourceCount)][];
            for (int i = 0; i < states.Length; i++)
            {
                states[i] = new byte[StateSize];
                Neutral(states[i]);
            }
            Buttons = new int[DeviceCount * 4];
            OwnedButtons = new int[DeviceCount * 4];
            Axes = new int[DeviceCount * 8];
            AxisOwned = new bool[DeviceCount * 8];
            Hats = new uint[DeviceCount * 4];
            HatOwned = new bool[DeviceCount * 4];
            List<VfFunction> built = new List<VfFunction>();
            List<string> problems = new List<string>();
            foreach (VirtualFunction function in functions ?? new List<VirtualFunction>())
            {
                if (function == null || !function.Enabled)
                    continue;
                VfFunction compiled = VfFunction.Create(this, function, problems);
                if (compiled != null)
                    built.Add(compiled);
            }
            Functions = built.ToArray();
            Problems = problems;
            keyLevel = new bool[keyScans.Count];
            keySent = new bool[keyScans.Count];
            Clear();
        }

        public int AxisMin { get; private set; }
        public int AxisMax { get; private set; }
        public int DeviceCount { get; private set; }
        public IVfKeySink KeySink { get; set; }
        public VfFunction[] Functions { get; private set; }
        public List<string> Problems { get; private set; }
        public bool HasXbox { get; private set; }
        public long LastTickUs { get; private set; }
        public int SourceCount
        {
            get { return states.Length; }
        }

        // The state buffer of a source (the forwarder copies the device state in).
        public byte[] State(int source)
        {
            return (uint)source < (uint)states.Length ? states[source] : null;
        }

        public void SetState(int source, byte[] state)
        {
            if ((uint)source >= (uint)states.Length || state == null || state.Length < StateSize)
                return;
            Buffer.BlockCopy(state, 0, states[source], 0, StateSize);
        }

        // A device that is gone: its buttons up, hats centred (axes keep the last value), as in the forwarding table.
        public void ReleaseSource(int source)
        {
            if ((uint)source >= (uint)states.Length)
                return;
            byte[] state = states[source];
            Array.Clear(state, ButtonOffset, 128);
            for (int i = 0; i < 4; i++)
                WriteUInt(state, HatOffset + i * 4, HatCentred);
        }

        // One pass: every function evaluates at nowUs (microseconds, any monotonic clock).
        public void Tick(long nowUs)
        {
            LastTickUs = nowUs;
            Clear();
            VfFunction[] list = Functions;
            for (int i = 0; i < list.Length; i++)
            {
                VfFunction function = list[i];
                function.Active = 0;
                function.Tick(this, nowUs);
                function.Passes++;
            }
            FlushKeys();
        }

        // The end of a run: every key the functions hold goes up, outputs neutral.
        public void ReleaseAll()
        {
            Clear();
            FlushKeys();
        }

        private void Clear()
        {
            int centre = AxisMin + (AxisMax - AxisMin + 1) / 2;
            for (int i = 0; i < Buttons.Length; i++)
                Buttons[i] = 0;
            for (int i = 0; i < Axes.Length; i++)
            {
                if (AxisOwned[i])
                    Axes[i] = centre;
            }
            for (int i = 0; i < Hats.Length; i++)
                Hats[i] = HatCentred;
            XboxButtons = 0;
            for (int i = 0; i < 6; i++)
                XboxAxes[i] = i < 4 ? 0.5 : 0.0;
            for (int i = 0; i < keyLevel.Length; i++)
                keyLevel[i] = false;
        }

        private void FlushKeys()
        {
            for (int i = 0; i < keyLevel.Length; i++)
            {
                if (keyLevel[i] == keySent[i])
                    continue;
                keySent[i] = keyLevel[i];
                IVfKeySink sink = KeySink;
                if (sink != null)
                    sink.Key(keyScans[i], keyLevel[i]);
            }
        }

        // ---- inputs

        internal double Unit(VfIn input)
        {
            if (input.Kind == InAxis)
            {
                if (input.Source < 0)
                    return 0.5;
                int raw = BitConverter.ToInt32(states[input.Source], AxisOffset + input.Index * 4);
                return (Clamp(raw, AxisMin, AxisMax) - AxisMin) / (double)(AxisMax - AxisMin);
            }
            return Pressed(input) ? 1.0 : 0.0;
        }

        internal bool Pressed(VfIn input)
        {
            if (input.Source < 0)
                return false;
            switch (input.Kind)
            {
                case InButton:
                    return (states[input.Source][ButtonOffset + input.Index] & 0x80) != 0;
                case InHatDir:
                {
                    uint hat = Hat(input);
                    return hat != HatCentred && Direction8(hat) == input.Direction;
                }
                case InHat:
                    return Hat(input) != HatCentred;
                case InAxis:
                    return Unit(input) > 0.5;
                default:
                    return false;
            }
        }

        internal uint Hat(VfIn input)
        {
            if (input.Source < 0 || (input.Kind != InHat && input.Kind != InHatDir))
                return HatCentred;
            uint raw = BitConverter.ToUInt32(states[input.Source], HatOffset + input.Index * 4);
            return (raw & 0xFFFF) == 0xFFFF ? HatCentred : raw % 36000;
        }

        public static int Direction8(uint hat)
        {
            return (int)(((hat % 36000) + 2250) / 4500 % 8);
        }

        // ---- outputs

        internal void Press(VfOut output)
        {
            switch (output.Kind)
            {
                case OutVButton:
                    Buttons[output.Device * 4 + (output.Index >> 5)] |= 1 << (output.Index & 31);
                    break;
                case OutVAxis:
                    Axes[output.Device * 8 + output.Index] = AxisMax;
                    break;
                case OutXButton:
                    XboxButtons |= (ushort)output.Mask;
                    break;
                case OutXAxis:
                    XboxAxes[output.Index] = 1.0;
                    break;
                case OutKey:
                    keyLevel[output.KeySlot] = true;
                    break;
            }
        }

        internal void SetAxis(VfOut output, double unit)
        {
            unit = unit < 0 ? 0 : unit > 1 ? 1 : unit;
            switch (output.Kind)
            {
                case OutVAxis:
                    Axes[output.Device * 8 + output.Index] = AxisMin + (int)Math.Round(unit * (AxisMax - AxisMin));
                    break;
                case OutXAxis:
                    XboxAxes[output.Index] = unit;
                    break;
                case OutNone:
                    break;
                default:
                    if (unit > 0.5)
                        Press(output);
                    break;
            }
        }

        internal void SetHat(VfOut output, uint hat)
        {
            switch (output.Kind)
            {
                case OutVHat:
                    Hats[output.Device * 4 + output.Index] = hat;
                    break;
                case OutXDpad:
                    XboxButtons |= DPadBits(hat);
                    break;
                case OutNone:
                    break;
                default:
                    if (hat != HatCentred)
                        Press(output);
                    break;
            }
        }

        public static ushort DPadBits(uint hat)
        {
            if (hat == HatCentred)
                return 0;
            switch (Direction8(hat))
            {
                case 0: return 0x0001;
                case 1: return 0x0009;
                case 2: return 0x0008;
                case 3: return 0x000A;
                case 4: return 0x0002;
                case 5: return 0x0006;
                case 6: return 0x0004;
                default: return 0x0005;
            }
        }

        // Output state, for previews and tests.
        public bool IsOn(VfOut output)
        {
            switch (output.Kind)
            {
                case OutVButton: return (Buttons[output.Device * 4 + (output.Index >> 5)] & (1 << (output.Index & 31))) != 0;
                case OutXButton: return (XboxButtons & output.Mask) != 0;
                case OutKey: return keyLevel[output.KeySlot] || keySent[output.KeySlot];
                case OutVHat: return Hats[output.Device * 4 + output.Index] != HatCentred;
                case OutXDpad: return (XboxButtons & 0x000F) != 0;
                case OutVAxis: return AxisUnit(output) > 0.5;
                case OutXAxis: return XboxAxes[output.Index] > 0.5;
                default: return false;
            }
        }

        public double AxisUnit(VfOut output)
        {
            if (output.Kind == OutVAxis)
                return (Axes[output.Device * 8 + output.Index] - AxisMin) / (double)(AxisMax - AxisMin);
            if (output.Kind == OutXAxis)
                return XboxAxes[output.Index];
            return IsOn(output) ? 1.0 : 0.0;
        }

        public uint HatOf(VfOut output)
        {
            if (output.Kind == OutVHat)
                return Hats[output.Device * 4 + output.Index];
            return HatCentred;
        }

        // ---- building

        internal VfIn MakeInput(VfInput input)
        {
            VfIn compiled = new VfIn();
            if (input == null)
                return compiled;
            switch (input.Kind ?? string.Empty)
            {
                case "axis": compiled.Kind = InAxis; break;
                case "button": compiled.Kind = InButton; break;
                case "hat": compiled.Kind = InHat; break;
                case "hatdir": compiled.Kind = InHatDir; break;
                default: return compiled;
            }
            compiled.Index = input.Index;
            compiled.Direction = input.Direction;
            compiled.Source = (uint)input.Source < (uint)states.Length ? input.Source : -1;
            int limit = compiled.Kind == InAxis ? 8 : compiled.Kind == InButton ? 128 : 4;
            if (compiled.Index < 0 || compiled.Index >= limit)
                compiled.Kind = InNone;
            return compiled;
        }

        internal VfOut MakeOutput(VfOutput output)
        {
            VfOut compiled = new VfOut();
            if (output == null)
                return compiled;
            compiled.Mode = ModeOf(output.Mode);
            switch (output.Kind ?? string.Empty)
            {
                case "button":
                case "axis":
                case "hat":
                {
                    int limit = output.Kind == "button" ? 128 : output.Kind == "axis" ? 8 : 4;
                    if (output.DeviceIndex < 0 || output.DeviceIndex >= DeviceCount || output.Index < 0 || output.Index >= limit)
                        return compiled;
                    compiled.Device = output.DeviceIndex;
                    compiled.Index = output.Index;
                    if (output.Kind == "button")
                    {
                        compiled.Kind = OutVButton;
                        OwnedButtons[compiled.Device * 4 + (compiled.Index >> 5)] |= 1 << (compiled.Index & 31);
                    }
                    else if (output.Kind == "axis")
                    {
                        compiled.Kind = OutVAxis;
                        AxisOwned[compiled.Device * 8 + compiled.Index] = true;
                    }
                    else
                    {
                        compiled.Kind = OutVHat;
                        HatOwned[compiled.Device * 4 + compiled.Index] = true;
                    }
                    break;
                }
                case "xbutton":
                    compiled.Kind = OutXButton;
                    compiled.Mask = output.Mask;
                    XboxOwnedButtons |= (ushort)output.Mask;
                    HasXbox = true;
                    break;
                case "xaxis":
                    if (output.Index < 0 || output.Index >= 6)
                        return compiled;
                    compiled.Kind = OutXAxis;
                    compiled.Index = output.Index;
                    XboxAxisOwned[output.Index] = true;
                    HasXbox = true;
                    break;
                case "xdpad":
                    compiled.Kind = OutXDpad;
                    XboxOwnedButtons |= 0x000F;
                    HasXbox = true;
                    break;
                case "key":
                    compiled.Kind = OutKey;
                    compiled.Index = output.Index;
                    compiled.KeySlot = KeySlot(output.Index);
                    break;
            }
            return compiled;
        }

        internal VfOut MakeKey(int scan)
        {
            return new VfOut { Kind = OutKey, Index = scan, KeySlot = KeySlot(scan) };
        }

        private int KeySlot(int scan)
        {
            int slot = keyScans.IndexOf(scan);
            if (slot >= 0)
                return slot;
            keyScans.Add(scan);
            return keyScans.Count - 1;
        }

        internal static byte ModeOf(string mode)
        {
            switch ((mode ?? string.Empty).Trim().ToLowerInvariant())
            {
                case VirtualFunctionCatalog.ModeEnter: return 1;
                case VirtualFunctionCatalog.ModeLeave: return 2;
                case VirtualFunctionCatalog.ModeBoth: return 3;
                default: return 0;
            }
        }

        internal static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }

        internal static double Clamp01(double value)
        {
            return value < 0 ? 0 : value > 1 ? 1 : value;
        }

        public static void Neutral(byte[] state)
        {
            Array.Clear(state, 0, state.Length);
            for (int i = 0; i < 8; i++)
                WriteInt(state, AxisOffset + i * 4, 16384);
            for (int i = 0; i < 4; i++)
                WriteUInt(state, HatOffset + i * 4, HatCentred);
        }

        public static void WriteInt(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        public static void WriteUInt(byte[] buffer, int offset, uint value)
        {
            WriteInt(buffer, offset, unchecked((int)value));
        }
    }

    // A compiled input: source index (-1: not read, reads neutral), kind, 0-based index, hat direction.
    public sealed class VfIn
    {
        public int Source = -1;
        public byte Kind;
        public int Index;
        public int Direction;

        public bool Configured
        {
            get { return Kind != VfRuntime.InNone; }
        }
    }

    // A compiled output: kind, vJoy device index, 0-based index (key: scan code), Xbox mask, key slot, band mode.
    public sealed class VfOut
    {
        public byte Kind;
        public int Device;
        public int Index;
        public int Mask;
        public int KeySlot;
        public byte Mode;
        public string Role = string.Empty;

        public bool Configured
        {
            get { return Kind != VfRuntime.OutNone; }
        }
    }

    // ---------------------------------------------------------------------------------------------- functions

    // One compiled function. State (band, step, latch ...), Input (the first input, 0..1) and Active (outputs on in
    // the last pass, bit = index in OutputRoles) are read by the live view (another thread: plain ints/doubles).
    public abstract class VfFunction
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Type = string.Empty;
        public int State;
        public double Input;
        public int Active;
        public long Passes;
        internal VfOut[] Outs = new VfOut[0];
        internal long[] PulseUntil = new long[0];
        private readonly List<VfOut> building = new List<VfOut>();

        public string[] OutputRoles { get; private set; }

        public VfOut OutputByRole(string role)
        {
            for (int i = 0; i < Outs.Length; i++)
            {
                if (string.Equals(Outs[i].Role, role, StringComparison.OrdinalIgnoreCase))
                    return Outs[i];
            }
            return null;
        }

        internal abstract void Tick(VfRuntime rt, long now);

        protected int AddOut(VfRuntime rt, VirtualFunction function, string role)
        {
            VfOut output = rt.MakeOutput(function.OutputOf(role));
            output.Role = role;
            building.Add(output);
            return building.Count - 1;
        }

        protected int AddOut(VfOut output, string role)
        {
            output.Role = role;
            building.Add(output);
            return building.Count - 1;
        }

        protected void Finish()
        {
            Outs = building.ToArray();
            PulseUntil = new long[Outs.Length];
            OutputRoles = Outs.Select(o => o.Role).ToArray();
        }

        protected void Out(VfRuntime rt, int k, bool on)
        {
            if (!on || k < 0 || k >= Outs.Length || !Outs[k].Configured)
                return;
            rt.Press(Outs[k]);
            if (k < 31)
                Active |= 1 << k;
        }

        protected void Pulse(int k, long now, long lengthUs)
        {
            if (k >= 0 && k < PulseUntil.Length && Outs[k].Configured)
                PulseUntil[k] = now + lengthUs;
        }

        protected bool Pulsing(int k, long now)
        {
            return k >= 0 && k < PulseUntil.Length && PulseUntil[k] > now;
        }

        protected void MarkAxis(int k)
        {
            if (k >= 0 && k < 31 && Outs[k].Configured)
                Active |= 1 << k;
        }

        protected static long Us(VirtualFunction f, string key, double fallbackMs)
        {
            return (long)Math.Round(Math.Max(0, f.Param(key, fallbackMs)) * 1000.0);
        }

        internal static VfFunction Create(VfRuntime rt, VirtualFunction function, List<string> problems)
        {
            VfFunction compiled;
            switch ((function.Type ?? string.Empty).Trim().ToLowerInvariant())
            {
                case VirtualFunctionCatalog.Bands: compiled = new BandsFunction(rt, function, false); break;
                case VirtualFunctionCatalog.AxisButtons: compiled = new BandsFunction(rt, function, true); break;
                case VirtualFunctionCatalog.Cycle: compiled = new CycleFunction(rt, function); break;
                case VirtualFunctionCatalog.Toggle: compiled = new ToggleFunction(rt, function); break;
                case VirtualFunctionCatalog.Switch: compiled = new SwitchFunction(rt, function); break;
                case VirtualFunctionCatalog.Presses: compiled = new PressesFunction(rt, function); break;
                case VirtualFunctionCatalog.Shift: compiled = new ShiftFunction(rt, function); break;
                case VirtualFunctionCatalog.Merge: compiled = new MergeFunction(rt, function); break;
                case VirtualFunctionCatalog.Split: compiled = new SplitFunction(rt, function); break;
                case VirtualFunctionCatalog.Curve: compiled = new CurveFunction(rt, function); break;
                case VirtualFunctionCatalog.AxesHat: compiled = new AxesHatFunction(rt, function); break;
                case VirtualFunctionCatalog.ButtonsAxis: compiled = new ButtonsAxisFunction(rt, function); break;
                case VirtualFunctionCatalog.Encoder: compiled = new EncoderFunction(rt, function); break;
                case VirtualFunctionCatalog.Turbo: compiled = new TurboFunction(rt, function); break;
                case VirtualFunctionCatalog.Macro: compiled = new MacroFunction(rt, function); break;
                case VirtualFunctionCatalog.HatButtons: compiled = new HatButtonsFunction(rt, function); break;
                case VirtualFunctionCatalog.ButtonsHat: compiled = new ButtonsHatFunction(rt, function); break;
                default:
                    problems.Add((function.Name ?? function.Id) + ": unknown function type " + function.Type + ".");
                    return null;
            }
            compiled.Id = function.Id ?? string.Empty;
            compiled.Name = function.Name ?? string.Empty;
            compiled.Type = function.Type ?? string.Empty;
            return compiled;
        }
    }

    // Axis bands (and axis ends -> buttons: three bands, the middle one without output). The band changes only when
    // the axis passes an edge by the hysteresis; a fast jump over several bands goes straight to the last one.
    internal sealed class BandsFunction : VfFunction
    {
        private readonly VfIn input;
        private readonly double[] edges;
        private readonly double hysteresis;
        private readonly long pulseUs;
        private readonly int[] outIndex;
        private int band = -1;

        public BandsFunction(VfRuntime rt, VirtualFunction f, bool ends)
        {
            input = rt.MakeInput(f.InputOf("in"));
            hysteresis = Math.Max(0, f.Param("hysteresis", ends ? 0.02 : 0.01));
            pulseUs = Us(f, "pulseMs", 80);
            if (ends)
            {
                double threshold = Math.Min(0.49, Math.Max(0.001, f.Param("threshold", 0.1)));
                edges = new[] { threshold, 1 - threshold };
                outIndex = new[] { AddOut(rt, f, "low"), -1, AddOut(rt, f, "high") };
            }
            else
            {
                edges = (f.Edges ?? new List<double>()).Select(VfRuntime.Clamp01).ToArray();
                outIndex = new int[edges.Length + 1];
                for (int i = 0; i < outIndex.Length; i++)
                    outIndex[i] = AddOut(rt, f, "band" + (i + 1).ToString(CultureInfo.InvariantCulture));
            }
            Finish();
        }

        internal override void Tick(VfRuntime rt, long now)
        {
            double u = rt.Unit(input);
            Input = u;
            if (band < 0)
            {
                band = 0;
                while (band < edges.Length && u >= edges[band])
                    band++;
            }
            else
            {
                int next = band;
                while (next < edges.Length && u >= edges[next] + hysteresis)
                    next++;
                while (next > 0 && u < edges[next - 1] - hysteresis)
                    next--;
                if (next != band)
                {
                    int left = outIndex[band];
                    int entered = outIndex[next];
                    if (left >= 0 && (Outs[left].Mode == 2 || Outs[left].Mode == 3))
                        Pulse(left, now, pulseUs);
                    if (entered >= 0 && (Outs[entered].Mode == 1 || Outs[entered].Mode == 3))
                        Pulse(entered, now, pulseUs);
                    band = next;
                }
            }
            State = band;
            for (int b = 0; b < outIndex.Length; b++)
            {
                int k = outIndex[b];
                if (k < 0)
                    continue;
                Out(rt, k, (Outs[k].Mode == 0 && b == band) || Pulsing(k, now));
            }
        }
    }

    internal sealed class CycleFunction : VfFunction
    {
        private readonly VfIn next;
        private readonly VfIn prev;
        private readonly VfIn reset;
        private readonly bool hold;
        private readonly bool wrap;
        private readonly long pulseUs;
        private int index;
        private bool lastNext;
        private bool lastPrev;
        private bool lastReset;

        public CycleFunction(VfRuntime rt, VirtualFunction f)
        {
            next = rt.MakeInput(f.InputOf("next"));
            prev = rt.MakeInput(f.InputOf("prev"));
            reset = rt.MakeInput(f.InputOf("reset"));
            hold = f.Param("hold", 0) >= 0.5;
            wrap = f.Param("wrap", 1) >= 0.5;
            pulseUs = Us(f, "pulseMs", 80);
            int count = VirtualFunctionCatalog.CountOf(f);
            for (int i = 1; i <= count; i++)
                AddOut(rt, f, "step" + i.ToString(CultureInfo.InvariantCulture));
            Finish();
            index = hold ? 0 : -1;
        }

        internal override void Tick(VfRuntime rt, long now)
        {
            int n = Outs.Length;
            bool n1 = rt.Pressed(next);
            bool p1 = rt.Pressed(prev);
            bool r1 = rt.Pressed(reset);
            Input = n1 ? 1 : 0;
            if (n > 0)
            {
                int target = index;
                if (r1 && !lastReset)
                    target = hold ? 0 : -1;
                else if (n1 && !lastNext)
                    target = index < n - 1 ? index + 1 : wrap ? 0 : index;
                else if (p1 && !lastPrev)
                    target = index > 0 ? index - 1 : wrap ? n - 1 : index;
                if (target != index)
                {
                    index = target;
                    if (!hold && index >= 0)
                        Pulse(index, now, pulseUs);
                }
                for (int k = 0; k < n; k++)
                    Out(rt, k, hold ? k == index : Pulsing(k, now));
            }
            lastNext = n1;
            lastPrev = p1;
            lastReset = r1;
            State = index;
        }
    }

    internal sealed class ToggleFunction : VfFunction
    {
        private readonly VfIn input;
        private bool on;
        private bool last;

        public ToggleFunction(VfRuntime rt, VirtualFunction f)
        {
            input = rt.MakeInput(f.InputOf("in"));
            on = f.Param("startOn", 0) >= 0.5;
            AddOut(rt, f, "out");
            Finish();
        }

        internal override void Tick(VfRuntime rt, long now)
        {
            bool pressed = rt.Pressed(input);
            if (pressed && !last)
                on = !on;
            last = pressed;
            Input = pressed ? 1 : 0;
            State = on ? 1 : 0;
            Out(rt, 0, on);
        }
    }

    internal sealed class SwitchFunction : VfFunction
    {
        private readonly VfIn input;
        private readonly long pulseUs;
        private readonly bool sync;
        private bool started;
        private bool last;

        public SwitchFunction(VfRuntime rt, VirtualFunction f)
        {
            input = rt.MakeInput(f.InputOf("in"));
            pulseUs = Us(f, "pulseMs", 80);
            sync = f.Param("syncAtStart", 0) >= 0.5;
            AddOut(rt, f, "on");
            AddOut(rt, f, "off");
            Finish();
        }

        internal override void Tick(VfRuntime rt, long now)
        {
            bool pressed = rt.Pressed(input);
            if (!started)
            {
                started = true;
                if (sync)
                    Pulse(pressed ? 0 : 1, now, pulseUs);
            }
            else if (pressed != last)
            {
                Pulse(pressed ? 0 : 1, now, pulseUs);
            }
            last = pressed;
            Input = pressed ? 1 : 0;
            State = pressed ? 1 : 0;
            Out(rt, 0, Pulsing(0, now));
            Out(rt, 1, Pulsing(1, now));
        }
    }

    // Short / long / double press. Without a double press output a short press fires at the release; with one it waits
    // doubleMs for a second press. Without a long press output a long hold is a short press.
    internal sealed class PressesFunction : VfFunction
    {
        private const int Idle = 0, Down = 1, WaitSecond = 2, SecondDown = 3, LongActive = 4;
        private readonly VfIn input;
        private readonly long longUs;
        private readonly long doubleUs;
        private readonly long pulseUs;
        private readonly bool longHold;
        private readonly int shortOut;
        private readonly int longOut;
        private readonly int doubleOut;
        private long since;

        public PressesFunction(VfRuntime rt, VirtualFunction f)
        {
            input = rt.MakeInput(f.InputOf("in"));
            longUs = Us(f, "longMs", 500);
            doubleUs = Us(f, "doubleMs", 300);
            pulseUs = Us(f, "pulseMs", 80);
            longHold = f.Param("longHold", 1) >= 0.5;
            shortOut = AddOut(rt, f, "short");
            longOut = AddOut(rt, f, "long");
            doubleOut = AddOut(rt, f, "double");
            Finish();
        }

        internal override void Tick(VfRuntime rt, long now)
        {
            bool pressed = rt.Pressed(input);
            bool hasLong = Outs[longOut].Configured;
            bool hasDouble = Outs[doubleOut].Configured;
            switch (State)
            {
                case Idle:
                    if (pressed)
                    {
                        State = Down;
                        since = now;
                    }
                    break;
                case Down:
                    if (pressed && hasLong && now - since >= longUs)
                    {
                        State = LongActive;
                        if (!longHold)
                            Pulse(longOut, now, pulseUs);
                    }
                    else if (!pressed)
                    {
                        if (hasDouble)
                        {
                            State = WaitSecond;
                            since = now;
                        }
                        else
                        {
                            Pulse(shortOut, now, pulseUs);
                            State = Idle;
                        }
                    }
                    break;
                case WaitSecond:
                    if (pressed)
                    {
                        Pulse(doubleOut, now, pulseUs);
                        State = SecondDown;
                    }
                    else if (now - since >= doubleUs)
                    {
                        Pulse(shortOut, now, pulseUs);
                        State = Idle;
                    }
                    break;
                case SecondDown:
                case LongActive:
                    if (!pressed)
                        State = Idle;
                    break;
            }
            Input = pressed ? 1 : 0;
            Out(rt, shortOut, Pulsing(shortOut, now));
            Out(rt, longOut, longHold ? State == LongActive : Pulsing(longOut, now));
            Out(rt, doubleOut, Pulsing(doubleOut, now));
        }
    }

    // Shift layer: while the shift button is held (or latched), each input drives its shifted output, else its
    // unshifted output (the input's own slot when the engine filled it). An axis keeps the last value on the output
    // it left, so nothing jumps in the game.
    internal sealed class ShiftFunction : VfFunction
    {
        private readonly VfIn shift;
        private readonly bool latch;
        private readonly VfIn[] inputs;
        private readonly int[] shiftedOut;
        private readonly int[] baseOut;
        private readonly double[] frozenShifted;
        private readonly double[] frozenBase;
        private bool shifted;
        private bool last;
        private bool started;

        public ShiftFunction(VfRuntime rt, VirtualFunction f)
        {
            shift = rt.MakeInput(f.InputOf("shift"));
            latch = f.Param("latch", 0) >= 0.5;
            int count = VirtualFunctionCatalog.CountOf(f);
            inputs = new VfIn[count];
            shiftedOut = new int[count];
            baseOut = new int[count];
            frozenShifted = new double[count];
            frozenBase = new double[count];
            for (int i = 0; i < count; i++)
            {
                string n = (i + 1).ToString(CultureInfo.InvariantCulture);
                inputs[i] = rt.MakeInput(f.InputOf("in" + n));
                shiftedOut[i] = AddOut(rt, f, "out" + n);
                baseOut[i] = AddOut(rt, f, "base" + n);
                frozenShifted[i] = 0.5;
                frozenBase[i] = 0.5;
            }
            Finish();
        }

        internal override void Tick(VfRuntime rt, long now)
        {
            bool pressed = rt.Pressed(shift);
            if (latch)
            {
                if (pressed && !last)
                    shifted = !shifted;
            }
            else
                shifted = pressed;
            last = pressed;
            for (int i = 0; i < inputs.Length; i++)
            {
                // The output the input drives now; the other one stays neutral (cleared each pass) or, for an axis,
                // keeps its frozen value.
                VfIn input = inputs[i];
                int on = shifted ? shiftedOut[i] : baseOut[i];
                switch (input.Kind)
                {
                    case VfRuntime.InAxis:
                    {
                        double u = rt.Unit(input);
                        if (!started)
                            frozenBase[i] = u;
                        if (shifted)
                        {
                            frozenShifted[i] = u;
                            rt.SetAxis(Outs[shiftedOut[i]], u);
                            rt.SetAxis(Outs[baseOut[i]], frozenBase[i]);
                        }
                        else
                        {
                            frozenBase[i] = u;
                            rt.SetAxis(Outs[baseOut[i]], u);
                            rt.SetAxis(Outs[shiftedOut[i]], frozenShifted[i]);
                        }
                        MarkAxis(on);
                        if (i == 0)
                            Input = u;
                        break;
                    }
                    case VfRuntime.InHat:
                    {
                        uint hat = rt.Hat(input);
                        if (Outs[on].Configured)
                            rt.SetHat(Outs[on], hat);
                        if (hat != VfRuntime.HatCentred)
                            MarkAxis(on);
                        if (i == 0)
                            Input = hat == VfRuntime.HatCentred ? 0 : 1;
                        break;
                    }
                    case VfRuntime.InNone:
                        break;
                    default:
                    {
                        bool down = rt.Pressed(input);
                        Out(rt, on, down);
                        if (i == 0)
                            Input = down ? 1 : 0;
                        break;
                    }
                }
            }
            started = true;
            State = shifted ? 1 : 0;
        }
    }

    internal sealed class MergeFunction : VfFunction
    {
        private readonly VfIn a;
        private readonly VfIn b;
        private readonly int mode;
        private readonly bool invertA;
        private readonly bool invertB;

        public MergeFunction(VfRuntime rt, VirtualFunction f)
        {
            a = rt.MakeInput(f.InputOf("a"));
            b = rt.MakeInput(f.InputOf("b"));
            mode = (int)Math.Round(f.Param("mode", 0));
            invertA = f.Param("invertA", 0) >= 0.5;
            invertB = f.Param("invertB", 0) >= 0.5;
            AddOut(rt, f, "out");
            Finish();
        }

        public static double Combine(int mode, double a, double b)
        {
            switch (mode)
            {
                case 1: return VfRuntime.Clamp01(a + b);
                case 2: return (a + b) / 2.0;
                case 3: return Math.Max(a, b);
                case 4: return Math.Min(a, b);
                default: return VfRuntime.Clamp01(0.5 + (a - b) / 2.0);
            }
        }

        internal override void Tick(VfRuntime rt, long now)
        {
            double ua = rt.Unit(a);
            double ub = rt.Unit(b);
            if (invertA)
                ua = 1 - ua;
            if (invertB)
                ub = 1 - ub;
            double value = Combine(mode, ua, ub);
            Input = ua;
            State = (int)Math.Round(value * 1000);
            rt.SetAxis(Outs[0], value);
            MarkAxis(0);
        }
    }

    internal sealed class SplitFunction : VfFunction
    {
        private readonly VfIn input;
        private readonly double centre;

        public SplitFunction(VfRuntime rt, VirtualFunction f)
        {
            input = rt.MakeInput(f.InputOf("in"));
            centre = Math.Min(0.95, Math.Max(0.05, f.Param("centre", 0.5)));
            AddOut(rt, f, "low");
            AddOut(rt, f, "high");
            Finish();
        }

        internal override void Tick(VfRuntime rt, long now)
        {
            double u = rt.Unit(input);
            Input = u;
            double low = u < centre ? (centre - u) / centre : 0;
            double high = u > centre ? (u - centre) / (1 - centre) : 0;
            rt.SetAxis(Outs[0], low);
            rt.SetAxis(Outs[1], high);
            State = u < centre ? 0 : u > centre ? 2 : 1;
            if (low > 0)
                MarkAxis(0);
            if (high > 0)
                MarkAxis(1);
        }
    }

    // JoyPro's axis curve (AxisModels.EvaluateDcs: dead zone, saturation, curvature or user curve, slider, invert),
    // sampled once into a table; the pass interpolates.
    internal sealed class CurveFunction : VfFunction
    {
        public const int Points = 256;
        private readonly VfIn input;
        private readonly double[] table = new double[Points + 1];

        public CurveFunction(VfRuntime rt, VirtualFunction f)
        {
            input = rt.MakeInput(f.InputOf("in"));
            Fill(table, f.Tuning);
            AddOut(rt, f, "out");
            Finish();
        }

        public static void Fill(double[] table, string tuningText)
        {
            AxisTuning tuning = string.IsNullOrWhiteSpace(tuningText) ? null : AxisTuning.Parse(tuningText);
            int last = table.Length - 1;
            for (int i = 0; i <= last; i++)
            {
                double u = i / (double)last;
                table[i] = tuning == null ? u : VfRuntime.Clamp01((AxisModels.EvaluateDcs(tuning, u * 2 - 1) + 1) / 2.0);
            }
        }

        public static double Lookup(double[] table, double u)
        {
            int last = table.Length - 1;
            double x = VfRuntime.Clamp01(u) * last;
            int i = (int)x;
            if (i >= last)
                return table[last];
            double t = x - i;
            return table[i] + (table[i + 1] - table[i]) * t;
        }

        internal override void Tick(VfRuntime rt, long now)
        {
            double u = rt.Unit(input);
            double value = Lookup(table, u);
            Input = u;
            State = (int)Math.Round(value * 1000);
            rt.SetAxis(Outs[0], value);
            MarkAxis(0);
        }
    }

    internal sealed class AxesHatFunction : VfFunction
    {
        private readonly VfIn x;
        private readonly VfIn y;
        private readonly double threshold;
        private readonly bool fourWay;

        public AxesHatFunction(VfRuntime rt, VirtualFunction f)
        {
            x = rt.MakeInput(f.InputOf("x"));
            y = rt.MakeInput(f.InputOf("y"));
            threshold = Math.Min(0.99, Math.Max(0.05, f.Param("threshold", 0.5)));
            fourWay = f.Param("ways", 8) < 6;
            AddOut(rt, f, "hat");
            Finish();
        }

        public static uint HatFrom(double ux, double uy, double threshold, bool fourWay)
        {
            double dx = (ux - 0.5) * 2;
            double up = (0.5 - uy) * 2; // DirectInput: up/forward = smaller
            if (Math.Sqrt(dx * dx + up * up) < threshold)
                return VfRuntime.HatCentred;
            double degrees = Math.Atan2(dx, up) * 180.0 / Math.PI;
            if (degrees < 0)
                degrees += 360;
            double step = fourWay ? 90 : 45;
            int sector = (int)Math.Round(degrees / step) % (int)(360 / step);
            return (uint)Math.Round(sector * step * 100);
        }

        internal override void Tick(VfRuntime rt, long now)
        {
            uint hat = HatFrom(rt.Unit(x), rt.Unit(y), threshold, fourWay);
            Input = rt.Unit(x);
            State = hat == VfRuntime.HatCentred ? -1 : (int)(hat / 4500);
            rt.SetHat(Outs[0], hat);
            if (hat != VfRuntime.HatCentred)
                MarkAxis(0);
        }
    }

    // Incremental axis from two buttons (rate in travel per second); "back to start" and an optional spring back.
    internal sealed class ButtonsAxisFunction : VfFunction
    {
        private readonly VfIn up;
        private readonly VfIn down;
        private readonly VfIn centre;
        private readonly double rate;
        private readonly double start;
        private readonly double returnRate;
        private double value;
        private long last = -1;
        private bool lastCentre;

        public ButtonsAxisFunction(VfRuntime rt, VirtualFunction f)
        {
            up = rt.MakeInput(f.InputOf("up"));
            down = rt.MakeInput(f.InputOf("down"));
            centre = rt.MakeInput(f.InputOf("centre"));
            rate = Math.Max(0, f.Param("rate", 0.5));
            start = VfRuntime.Clamp01(f.Param("start", 0.5));
            returnRate = Math.Max(0, f.Param("returnRate", 0));
            value = start;
            AddOut(rt, f, "out");
            Finish();
        }

        public double Value
        {
            get { return value; }
        }

        internal override void Tick(VfRuntime rt, long now)
        {
            double dt = last < 0 ? 0 : Math.Min(0.1, Math.Max(0, (now - last) / 1e6));
            last = now;
            bool u = rt.Pressed(up);
            bool d = rt.Pressed(down);
            bool c = rt.Pressed(centre);
            if (c && !lastCentre)
                value = start;
            lastCentre = c;
            int direction = (u ? 1 : 0) - (d ? 1 : 0);
            if (direction != 0)
                value += direction * rate * dt;
            else if (returnRate > 0 && !u && !d)
            {
                double step = returnRate * dt;
                value = Math.Abs(value - start) <= step ? start : value + (value < start ? step : -step);
            }
            value = VfRuntime.Clamp01(value);
            Input = direction;
            State = (int)Math.Round(value * 1000);
            rt.SetAxis(Outs[0], value);
            MarkAxis(0);
        }
    }

    // Relative mode: every step of axis travel queues one up or down pulse; pulses go out one after the other.
    internal sealed class EncoderFunction : VfFunction
    {
        private readonly VfIn input;
        private readonly double step;
        private readonly long pulseUs;
        private readonly long gapUs;
        private readonly int maxQueue;
        private double reference = -1;
        private int pendingUp;
        private int pendingDown;
        private int phase;
        private bool pulseUp;
        private long until;

        public EncoderFunction(VfRuntime rt, VirtualFunction f)
        {
            input = rt.MakeInput(f.InputOf("in"));
            step = Math.Max(0.001, f.Param("step", 0.05));
            pulseUs = Us(f, "pulseMs", 60);
            gapUs = Us(f, "gapMs", 60);
            maxQueue = Math.Max(1, (int)Math.Round(f.Param("maxQueue", 8)));
            AddOut(rt, f, "up");
            AddOut(rt, f, "down");
            Finish();
        }

        internal override void Tick(VfRuntime rt, long now)
        {
            double u = rt.Unit(input);
            Input = u;
            if (reference < 0)
                reference = u;
            double delta = u - reference;
            while (delta >= step)
            {
                reference += step;
                delta -= step;
                pendingDown = 0;
                if (pendingUp < maxQueue)
                    pendingUp++;
            }
            while (delta <= -step)
            {
                reference -= step;
                delta += step;
                pendingUp = 0;
                if (pendingDown < maxQueue)
                    pendingDown++;
            }
            if (phase == 1 && now >= until)
            {
                phase = 2;
                until = now + gapUs;
            }
            if (phase == 2 && now >= until)
                phase = 0;
            if (phase == 0 && (pendingUp > 0 || pendingDown > 0))
            {
                pulseUp = pendingUp > 0;
                if (pulseUp)
                    pendingUp--;
                else
                    pendingDown--;
                phase = 1;
                until = now + pulseUs;
            }
            State = pendingUp - pendingDown;
            Out(rt, 0, phase == 1 && pulseUp);
            Out(rt, 1, phase == 1 && !pulseUp);
        }
    }

    internal sealed class TurboFunction : VfFunction
    {
        private readonly VfIn input;
        private readonly long onUs;
        private readonly long offUs;
        private readonly bool toggle;
        private bool running;
        private bool last;
        private long since;

        public TurboFunction(VfRuntime rt, VirtualFunction f)
        {
            input = rt.MakeInput(f.InputOf("in"));
            onUs = Math.Max(1000, Us(f, "onMs", 50));
            offUs = Math.Max(1000, Us(f, "offMs", 50));
            toggle = f.Param("toggle", 0) >= 0.5;
            AddOut(rt, f, "out");
            Finish();
        }

        internal override void Tick(VfRuntime rt, long now)
        {
            bool pressed = rt.Pressed(input);
            bool wasRunning = running;
            if (toggle)
            {
                if (pressed && !last)
                    running = !running;
            }
            else
                running = pressed;
            last = pressed;
            if (running && !wasRunning)
                since = now;
            bool on = running && (now - since) % (onUs + offUs) < onUs;
            Input = pressed ? 1 : 0;
            State = running ? 1 : 0;
            Out(rt, 0, on);
        }
    }

    // A macro: the steps as a timeline (time, output, down/up) made once; a press plays it. A press and its release
    // never fall into one pass (the release waits for the next), so every press is seen by the game. At the end every
    // output is released.
    internal sealed class MacroFunction : VfFunction
    {
        private readonly VfIn trigger;
        private readonly bool repeat;
        private readonly long[] times;
        private readonly int[] targets;
        private readonly bool[] downs;
        private readonly bool[] levels;
        private readonly bool[] pressedThisPass;
        private bool running;
        private bool last;
        private long started;
        private int next;

        public MacroFunction(VfRuntime rt, VirtualFunction f)
        {
            trigger = rt.MakeInput(f.InputOf("trigger"));
            repeat = f.Param("repeat", 0) >= 0.5;
            int count = VirtualFunctionCatalog.CountOf(f);
            Dictionary<string, int> byRole = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i <= count; i++)
            {
                string role = "out" + i.ToString(CultureInfo.InvariantCulture);
                byRole[role] = AddOut(rt, f, role);
            }
            List<KeyValuePair<long, KeyValuePair<int, bool>>> events = new List<KeyValuePair<long, KeyValuePair<int, bool>>>();
            long cursor = 0;
            foreach (VfStep step in f.Steps ?? new List<VfStep>())
            {
                string name = (step.Output ?? string.Empty).Trim();
                int target;
                int scan;
                if (!byRole.TryGetValue(name, out target))
                {
                    if (!f.Keyboard || !VfTokens.TryKey(name, out scan))
                        continue;
                    target = AddOut(rt.MakeKey(scan), name.ToUpperInvariant());
                    byRole[name] = target;
                }
                cursor += Math.Max(0, step.DelayMs) * 1000L;
                string action = (step.Action ?? "tap").Trim().ToLowerInvariant();
                if (action == "press" || action == "tap")
                    events.Add(new KeyValuePair<long, KeyValuePair<int, bool>>(cursor, new KeyValuePair<int, bool>(target, true)));
                if (action == "tap")
                    cursor += Math.Max(1, step.HoldMs) * 1000L;
                if (action == "release" || action == "tap")
                    events.Add(new KeyValuePair<long, KeyValuePair<int, bool>>(cursor, new KeyValuePair<int, bool>(target, false)));
            }
            Finish();
            // Stable by time: the order of the steps stays for events at the same time.
            List<KeyValuePair<long, KeyValuePair<int, bool>>> sorted = events.Select((e, i) => new { e, i }).OrderBy(x => x.e.Key).ThenBy(x => x.i).Select(x => x.e).ToList();
            times = sorted.Select(e => e.Key).ToArray();
            targets = sorted.Select(e => e.Value.Key).ToArray();
            downs = sorted.Select(e => e.Value.Value).ToArray();
            levels = new bool[Outs.Length];
            pressedThisPass = new bool[Outs.Length];
            Length = times.Length == 0 ? 0 : times[times.Length - 1];
        }

        public long Length { get; private set; }

        internal override void Tick(VfRuntime rt, long now)
        {
            bool pressed = rt.Pressed(trigger);
            if (pressed && !last && !running)
                Start(now);
            last = pressed;
            if (running)
            {
                for (int i = 0; i < pressedThisPass.Length; i++)
                    pressedThisPass[i] = false;
                long elapsed = now - started;
                while (next < times.Length && times[next] <= elapsed)
                {
                    int target = targets[next];
                    if (!downs[next] && pressedThisPass[target])
                        break; // released in the next pass, so the press is seen
                    levels[target] = downs[next];
                    if (downs[next])
                        pressedThisPass[target] = true;
                    next++;
                }
                if (next >= times.Length && !AnyPressedThisPass())
                {
                    for (int i = 0; i < levels.Length; i++)
                        levels[i] = false;
                    running = false;
                    if (repeat && pressed)
                        Start(now);
                }
            }
            Input = pressed ? 1 : 0;
            State = running ? next : -1;
            for (int k = 0; k < levels.Length; k++)
                Out(rt, k, levels[k]);
        }

        private bool AnyPressedThisPass()
        {
            for (int i = 0; i < pressedThisPass.Length; i++)
            {
                if (pressedThisPass[i])
                    return true;
            }
            return false;
        }

        private void Start(long now)
        {
            running = true;
            started = now;
            next = 0;
            for (int i = 0; i < levels.Length; i++)
                levels[i] = false;
        }
    }

    internal sealed class HatButtonsFunction : VfFunction
    {
        private static readonly int[] CardinalOf = { 0, -1, 1, -1, 2, -1, 3, -1 };
        private static readonly int[] DiagonalOf = { -1, 4, -1, 5, -1, 6, -1, 7 };
        private static readonly int[] FirstNeighbour = { -1, 0, -1, 2, -1, 2, -1, 0 };
        private static readonly int[] SecondNeighbour = { -1, 1, -1, 1, -1, 3, -1, 3 };
        private readonly VfIn hat;
        private readonly bool eightWay;

        public HatButtonsFunction(VfRuntime rt, VirtualFunction f)
        {
            hat = rt.MakeInput(f.InputOf("hat"));
            eightWay = f.Param("ways", 4) >= 6;
            foreach (string role in new[] { "up", "right", "down", "left", "upright", "downright", "downleft", "upleft" })
                AddOut(rt, f, role);
            Finish();
        }

        internal override void Tick(VfRuntime rt, long now)
        {
            uint value = rt.Hat(hat);
            if (value == VfRuntime.HatCentred)
            {
                State = -1;
                Input = 0;
                return;
            }
            int direction = VfRuntime.Direction8(value);
            State = direction;
            Input = 1;
            if (CardinalOf[direction] >= 0)
            {
                Out(rt, CardinalOf[direction], true);
                return;
            }
            int diagonal = DiagonalOf[direction];
            if (eightWay && Outs[diagonal].Configured)
            {
                Out(rt, diagonal, true);
                return;
            }
            Out(rt, FirstNeighbour[direction], true);
            Out(rt, SecondNeighbour[direction], true);
        }
    }

    // Four buttons as an 8-way hat; leaving a diagonal waits settleMs before showing the remaining direction.
    internal sealed class ButtonsHatFunction : VfFunction
    {
        private readonly VfIn up;
        private readonly VfIn right;
        private readonly VfIn down;
        private readonly VfIn left;
        private readonly long settleUs;
        private int current = -1;
        private long pendingSince = -1;

        public ButtonsHatFunction(VfRuntime rt, VirtualFunction f)
        {
            up = rt.MakeInput(f.InputOf("up"));
            right = rt.MakeInput(f.InputOf("right"));
            down = rt.MakeInput(f.InputOf("down"));
            left = rt.MakeInput(f.InputOf("left"));
            settleUs = Us(f, "settleMs", 30);
            AddOut(rt, f, "hat");
            Finish();
        }

        public static int DirectionOf(bool u, bool r, bool d, bool l)
        {
            int dx = (r ? 1 : 0) - (l ? 1 : 0);
            int dy = (u ? 1 : 0) - (d ? 1 : 0);
            if (dx == 0 && dy == 0)
                return -1;
            if (dy > 0)
                return dx > 0 ? 1 : dx < 0 ? 7 : 0;
            if (dy < 0)
                return dx > 0 ? 3 : dx < 0 ? 5 : 4;
            return dx > 0 ? 2 : 6;
        }

        internal override void Tick(VfRuntime rt, long now)
        {
            int target = DirectionOf(rt.Pressed(up), rt.Pressed(right), rt.Pressed(down), rt.Pressed(left));
            bool fromDiagonal = current >= 0 && current % 2 == 1;
            bool toCardinal = target >= 0 && target % 2 == 0;
            if (fromDiagonal && toCardinal && settleUs > 0 && target != current)
            {
                if (pendingSince < 0)
                    pendingSince = now;
                if (now - pendingSince >= settleUs)
                {
                    current = target;
                    pendingSince = -1;
                }
            }
            else
            {
                current = target;
                pendingSince = -1;
            }
            State = current;
            Input = target < 0 ? 0 : 1;
            uint hat = current < 0 ? VfRuntime.HatCentred : (uint)(current * 4500);
            rt.SetHat(Outs[0], hat);
            if (current >= 0)
                MarkAxis(0);
        }
    }

    // ---------------------------------------------------------------------------------------------- benchmark mix

    // A mix of every function type for load tests (JoyPro.VJoy bench, the GUI tests' loop benchmark): count functions
    // on sources "BENCH0".."BENCH<n-1>" (resolved: source index = n), outputs on vJoy device 1 from button 33 up.
    public static class VfBenchmark
    {
        public static List<VirtualFunction> Mix(int count, int sources)
        {
            List<VirtualFunction> list = new List<VirtualFunction>();
            List<string> sourceNames = Enumerable.Range(0, Math.Max(1, sources)).Select(i => "BENCH" + i.ToString(CultureInfo.InvariantCulture)).ToList();
            IReadOnlyList<VfTypeInfo> types = VirtualFunctionCatalog.Types;
            int button = 32;
            for (int i = 0; i < count; i++)
            {
                VfTypeInfo type = types[i % types.Count];
                VirtualFunction function = VirtualFunctionCatalog.Create(type.Id, type.Name + " " + (i + 1).ToString(CultureInfo.InvariantCulture));
                function.Id = "b" + (i + 1).ToString(CultureInfo.InvariantCulture);
                if (type.Id == VirtualFunctionCatalog.Presses)
                    function.Outputs.Add(new VfOutput { Role = "double" });
                int k = 0;
                foreach (VfRoleInfo role in VirtualFunctionCatalog.ExpandInputs(function))
                {
                    VfInput input = function.InputOf(role.Role);
                    input.Device = sourceNames[(i + k) % sourceNames.Count];
                    input.Input = role.Kind == "axis" ? VfTokens.AxisTokens[(i + k) % 8] : role.Kind == "hat" ? "POV1" : VfTokens.ButtonToken((i * 3 + k) % 32);
                    k++;
                }
                int axis = 0;
                foreach (VfOutput output in function.Outputs)
                {
                    VfRoleInfo role = VirtualFunctionCatalog.ExpandOutputs(function).FirstOrDefault(r => r.Role == output.Role);
                    string kind = role == null ? "button" : role.Kind == "any" ? "button" : role.Kind;
                    output.VJoyDevice = 1;
                    if (kind == "axis")
                        output.Target = VfTokens.AxisTokens[(i + axis++) % 8];
                    else if (kind == "hat")
                        output.Target = VfTokens.HatToken(i % 4);
                    else
                    {
                        output.Target = VfTokens.ButtonToken(button);
                        button = button >= 127 ? 32 : button + 1;
                    }
                }
                VfResolver.Resolve(function, sourceNames, new List<int> { 1 });
                list.Add(function);
            }
            return list;
        }

        // A changing input stream: pass p moves every axis and toggles buttons and hats at different speeds.
        public static void Drive(byte[] state, int source, long pass, int axisMin, int axisMax)
        {
            for (int a = 0; a < 8; a++)
            {
                double phase = (pass * (a + 1 + source)) % 720 / 720.0;
                double unit = phase < 0.5 ? phase * 2 : 2 - phase * 2;
                VfRuntime.WriteInt(state, VfRuntime.AxisOffset + a * 4, axisMin + (int)(unit * (axisMax - axisMin)));
            }
            for (int b = 0; b < 32; b++)
                state[VfRuntime.ButtonOffset + b] = ((pass / (8 + b + source)) % 2) == 0 ? (byte)0 : (byte)0x80;
            long hatPhase = (pass / 24 + source) % 10;
            VfRuntime.WriteUInt(state, VfRuntime.HatOffset, hatPhase >= 8 ? VfRuntime.HatCentred : (uint)(hatPhase * 4500));
        }
    }

    // ---------------------------------------------------------------------------------------------- simulation

    // One output change in a simulation.
    public sealed class VfEvent
    {
        public double TimeMs { get; set; }
        public string Output { get; set; }
        public string Target { get; set; }
        public string Value { get; set; }
    }

    // Runs one function without devices: the inputs are driven by role (SetAxis/SetButton/SetHat), the clock by Step.
    // Unset inputs and outputs get stand-ins, so a function can be tried before it is wired. The engine's
    // "functions test", the GUI's preview and the unit tests use it.
    public sealed class VfSimulator : IVfKeySink
    {
        private const string SimDevice = "JoyPro simulator {00000000-0000-0000-0000-000000000000}";
        private readonly Dictionary<string, KeyValuePair<int, VfInput>> inputs = new Dictionary<string, KeyValuePair<int, VfInput>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> keyLog = new List<string>();
        private long nowUs;

        public VfSimulator(VirtualFunction function, int axisMin = 0, int axisMax = 32767)
        {
            Function = function.Clone();
            Function.Normalize();
            Function.Enabled = true;
            // Every input its own source: the simulation drives each role separately.
            List<string> sources = new List<string>();
            int sim = 0;
            foreach (VfRoleInfo role in VirtualFunctionCatalog.ExpandInputs(Function))
            {
                VfInput input = Function.InputOf(role.Role);
                if (input == null)
                {
                    input = new VfInput { Role = role.Role };
                    Function.Inputs.Add(input);
                }
                string kind;
                int index;
                int direction;
                if (input.Input.Length == 0 || !VfTokens.TryParseInput(input.Input, out kind, out index, out direction))
                    input.Input = role.Kind == "axis" ? "JOY_X" : role.Kind == "hat" ? "POV1" : "JOY_BTN1";
                input.Device = "SIM" + (sim++).ToString(CultureInfo.InvariantCulture);
                sources.Add(input.Device);
            }
            // Outputs without a target get one on a stand-in device: the required ones, and every role while the
            // function has no output at all yet (so a new function can be tried). An optional output the user left
            // empty stays empty otherwise: it changes what the function does (a double press output makes the short
            // press wait).
            int button = 0;
            int axis = 0;
            int hat = 0;
            List<int> devices = new List<int> { 1, 99 };
            bool anyOutput = Function.Outputs.Any(o => o.Target.Length > 0);
            foreach (VfRoleInfo role in VirtualFunctionCatalog.ExpandOutputs(Function))
            {
                VfOutput output = Function.OutputOf(role.Role);
                bool standIn = !role.Optional || !anyOutput;
                if (output == null)
                {
                    if (!standIn)
                        continue;
                    output = new VfOutput { Role = role.Role, Mode = role.HasMode ? VirtualFunctionCatalog.ModeHold : string.Empty };
                    Function.Outputs.Add(output);
                }
                if (output.Target.Length == 0 && !standIn)
                    continue;
                if (output.Target.Length > 0)
                {
                    if (!devices.Contains(output.VJoyDevice <= 0 ? 1 : output.VJoyDevice))
                        devices.Add(output.VJoyDevice <= 0 ? 1 : output.VJoyDevice);
                    continue;
                }
                string want = role.Kind;
                if (Function.Type == VirtualFunctionCatalog.Shift)
                {
                    VfInput source = Function.InputOf("in" + new string(role.Role.Where(char.IsDigit).ToArray()));
                    string sourceKind;
                    int sourceIndex;
                    int sourceDirection;
                    if (source != null && VfTokens.TryParseInput(source.Input, out sourceKind, out sourceIndex, out sourceDirection))
                        want = sourceKind == "hatdir" ? "button" : sourceKind;
                }
                output.VJoyDevice = 99;
                output.Target = want == "axis" ? VfTokens.AxisTokens[axis++ % 8] : want == "hat" ? VfTokens.HatToken(hat++ % 4) : VfTokens.ButtonToken(127 - button++ % 128);
            }
            Problems = VfResolver.Resolve(Function, sources, devices);
            foreach (VfInput input in Function.Inputs)
            {
                if (input.Source >= 0)
                    inputs[input.Role] = new KeyValuePair<int, VfInput>(input.Source, input);
            }
            Runtime = new VfRuntime(new List<VirtualFunction> { Function }, sources.Count, devices.Count, axisMin, axisMax, this);
            Problems.AddRange(Runtime.Problems);
            Compiled = Runtime.Functions.Length > 0 ? Runtime.Functions[0] : null;
        }

        public VirtualFunction Function { get; private set; }
        public VfRuntime Runtime { get; private set; }
        public VfFunction Compiled { get; private set; }
        public List<string> Problems { get; private set; }

        public IReadOnlyList<string> KeyLog
        {
            get { return keyLog; }
        }

        public double NowMs
        {
            get { return nowUs / 1000.0; }
        }

        void IVfKeySink.Key(int scan, bool down)
        {
            keyLog.Add(NowMs.ToString("0.#", CultureInfo.InvariantCulture) + " key 0x" + scan.ToString("X", CultureInfo.InvariantCulture) + (down ? " down" : " up"));
        }

        public bool HasInput(string role)
        {
            return inputs.ContainsKey(role);
        }

        public string InputKind(string role)
        {
            KeyValuePair<int, VfInput> entry;
            return inputs.TryGetValue(role, out entry) ? entry.Value.Kind : string.Empty;
        }

        public void SetAxis(string role, double unit)
        {
            KeyValuePair<int, VfInput> entry;
            if (!inputs.TryGetValue(role, out entry) || entry.Value.Kind != "axis")
                return;
            int raw = Runtime.AxisMin + (int)Math.Round(VfRuntime.Clamp01(unit) * (Runtime.AxisMax - Runtime.AxisMin));
            VfRuntime.WriteInt(Runtime.State(entry.Key), VfRuntime.AxisOffset + entry.Value.Index * 4, raw);
        }

        public void SetButton(string role, bool down)
        {
            KeyValuePair<int, VfInput> entry;
            if (!inputs.TryGetValue(role, out entry))
                return;
            byte[] state = Runtime.State(entry.Key);
            switch (entry.Value.Kind)
            {
                case "button":
                    state[VfRuntime.ButtonOffset + entry.Value.Index] = down ? (byte)0x80 : (byte)0;
                    break;
                case "hatdir":
                    VfRuntime.WriteUInt(state, VfRuntime.HatOffset + entry.Value.Index * 4, down ? (uint)(entry.Value.Direction * 4500) : VfRuntime.HatCentred);
                    break;
                case "axis":
                    SetAxis(role, down ? 1 : 0);
                    break;
                case "hat":
                    SetHat(role, down ? 0 : -1);
                    break;
            }
        }

        // Hundredths of a degree (0 = up, 9000 = right), -1 = centred.
        public void SetHat(string role, int hundredths)
        {
            KeyValuePair<int, VfInput> entry;
            if (!inputs.TryGetValue(role, out entry) || (entry.Value.Kind != "hat" && entry.Value.Kind != "hatdir"))
                return;
            VfRuntime.WriteUInt(Runtime.State(entry.Key), VfRuntime.HatOffset + entry.Value.Index * 4, hundredths < 0 ? VfRuntime.HatCentred : (uint)hundredths);
        }

        public void Step(double timeMs)
        {
            nowUs = (long)Math.Round(timeMs * 1000.0);
            Runtime.Tick(nowUs);
        }

        public VfOut Output(string role)
        {
            return Compiled == null ? null : Compiled.OutputByRole(role);
        }

        public bool IsOn(string role)
        {
            VfOut output = Output(role);
            return output != null && output.Configured && Runtime.IsOn(output);
        }

        public double AxisValue(string role)
        {
            VfOut output = Output(role);
            return output == null || !output.Configured ? 0 : Runtime.AxisUnit(output);
        }

        public int HatValue(string role)
        {
            VfOut output = Output(role);
            if (output == null || !output.Configured)
                return -1;
            if (output.Kind == VfRuntime.OutXDpad)
            {
                for (int d = 0; d < 8; d++)
                {
                    if (VfRuntime.DPadBits((uint)(d * 4500)) == (Runtime.XboxButtons & 0x000F))
                        return d * 4500;
                }
                return -1;
            }
            uint hat = Runtime.HatOf(output);
            return hat == VfRuntime.HatCentred ? -1 : (int)hat;
        }

        // The value of an output as text: "on"/"off", an axis in %, a hat in degrees or "centred".
        public string Describe(VfOut output)
        {
            if (output == null || !output.Configured)
                return "-";
            if (output.Kind == VfRuntime.OutVAxis || output.Kind == VfRuntime.OutXAxis)
                return Math.Round(Runtime.AxisUnit(output) * 100).ToString(CultureInfo.InvariantCulture) + "%";
            if (output.Kind == VfRuntime.OutVHat || output.Kind == VfRuntime.OutXDpad)
            {
                int hat = HatValue(output.Role);
                return hat < 0 ? "centred" : (hat / 100).ToString(CultureInfo.InvariantCulture) + "deg";
            }
            return Runtime.IsOn(output) ? "on" : "off";
        }

        // Plays an input stream (time ms -> role=value, see ParseStream) at rateHz until durationMs and returns every
        // output change.
        public List<VfEvent> Run(IList<KeyValuePair<double, KeyValuePair<string, string>>> stream, double rateHz, double durationMs)
        {
            List<VfEvent> events = new List<VfEvent>();
            Dictionary<string, string> last = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            double period = 1000.0 / Math.Max(1, rateHz);
            int next = 0;
            List<KeyValuePair<double, KeyValuePair<string, string>>> sorted = stream.OrderBy(s => s.Key).ToList();
            for (double t = 0; t <= durationMs + 1e-9; t += period)
            {
                while (next < sorted.Count && sorted[next].Key <= t + 1e-9)
                {
                    Apply(sorted[next].Value.Key, sorted[next].Value.Value);
                    next++;
                }
                Step(t);
                if (Compiled == null)
                    continue;
                foreach (VfOut output in Compiled.Outs)
                {
                    if (!output.Configured)
                        continue;
                    string value = Describe(output);
                    string previous;
                    if (last.TryGetValue(output.Role, out previous) && previous == value)
                        continue;
                    bool first = !last.ContainsKey(output.Role);
                    last[output.Role] = value;
                    if (first && (value == "off" || value == "centred"))
                        continue;
                    VfOutput source = Function.OutputOf(output.Role);
                    events.Add(new VfEvent { TimeMs = Math.Round(t, 3), Output = output.Role, Target = source == null ? output.Role : source.Target, Value = value });
                }
            }
            return events;
        }

        // role=value: an axis 0..1, a button 1/0 (or on/off), a hat in degrees or -1 / "c" for centred.
        public void Apply(string role, string value)
        {
            string text = (value ?? string.Empty).Trim().ToLowerInvariant();
            string kind = InputKind(role);
            double number;
            bool isNumber = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            if (kind == "axis")
            {
                if (isNumber)
                    SetAxis(role, number);
                return;
            }
            if (kind == "hat")
            {
                SetHat(role, text == "c" || text == "centre" || text == "center" || (isNumber && number < 0) ? -1 : isNumber ? (int)Math.Round(number * 100) : 0);
                return;
            }
            SetButton(role, text == "on" || text == "down" || text == "press" || (isNumber && number >= 0.5));
        }

        // "0:in=0; 200:in=0.5; 400:next=1" -> (time ms, role, value).
        public static List<KeyValuePair<double, KeyValuePair<string, string>>> ParseStream(string text)
        {
            List<KeyValuePair<double, KeyValuePair<string, string>>> output = new List<KeyValuePair<double, KeyValuePair<string, string>>>();
            foreach (string part in (text ?? string.Empty).Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string item = part.Trim();
                int colon = item.IndexOf(':');
                int eq = item.IndexOf('=');
                double time;
                if (colon <= 0 || eq <= colon + 1 || !double.TryParse(item.Substring(0, colon), NumberStyles.Float, CultureInfo.InvariantCulture, out time))
                    throw new ArgumentException("A stream item is <time ms>:<role>=<value>, e.g. 0:in=0.2; got \"" + item + "\".");
                output.Add(new KeyValuePair<double, KeyValuePair<string, string>>(time, new KeyValuePair<string, string>(item.Substring(colon + 1, eq - colon - 1).Trim(), item.Substring(eq + 1).Trim())));
            }
            return output;
        }

        // A stream that shows what the function does (engine "functions test" without --stream).
        public static string DefaultStream(VirtualFunction function, out double durationMs)
        {
            durationMs = 2000;
            switch ((function.Type ?? string.Empty).ToLowerInvariant())
            {
                case VirtualFunctionCatalog.Bands:
                case VirtualFunctionCatalog.AxisButtons:
                case VirtualFunctionCatalog.Curve:
                case VirtualFunctionCatalog.Split:
                case VirtualFunctionCatalog.Encoder:
                {
                    List<string> items = new List<string>();
                    for (int i = 0; i <= 20; i++)
                        items.Add((i * 50).ToString(CultureInfo.InvariantCulture) + ":in=" + (i / 20.0).ToString("0.###", CultureInfo.InvariantCulture));
                    for (int i = 1; i <= 20; i++)
                        items.Add((1000 + i * 50).ToString(CultureInfo.InvariantCulture) + ":in=" + (1 - i / 20.0).ToString("0.###", CultureInfo.InvariantCulture));
                    return string.Join("; ", items);
                }
                case VirtualFunctionCatalog.Merge:
                    return "0:a=0; 0:b=0; 400:a=1; 800:b=1; 1200:a=0; 1600:b=0";
                case VirtualFunctionCatalog.AxesHat:
                    return "0:x=0.5; 0:y=0.5; 200:y=0; 500:x=1; 800:y=0.5; 1100:y=1; 1400:x=0.5; 1700:y=0.5";
                case VirtualFunctionCatalog.Cycle:
                    return "100:next=1; 200:next=0; 400:next=1; 500:next=0; 700:next=1; 800:next=0; 1000:next=1; 1100:next=0; 1300:prev=1; 1400:prev=0; 1600:reset=1; 1700:reset=0";
                case VirtualFunctionCatalog.Toggle:
                case VirtualFunctionCatalog.Switch:
                    return "200:in=1; 400:in=0; 800:in=1; 1000:in=0";
                case VirtualFunctionCatalog.Presses:
                    durationMs = 3000;
                    return "100:in=1; 200:in=0; 800:in=1; 1500:in=0; 2000:in=1; 2080:in=0; 2200:in=1; 2280:in=0";
                case VirtualFunctionCatalog.Shift:
                    return "100:in1=1; 300:in1=0; 600:shift=1; 800:in1=1; 1000:in1=0; 1300:shift=0";
                case VirtualFunctionCatalog.ButtonsAxis:
                    durationMs = 3000;
                    return "100:up=1; 1100:up=0; 1500:down=1; 2000:down=0; 2500:centre=1; 2600:centre=0";
                case VirtualFunctionCatalog.Turbo:
                    return "100:in=1; 700:in=0";
                case VirtualFunctionCatalog.Macro:
                    return "100:trigger=1; 200:trigger=0";
                case VirtualFunctionCatalog.HatButtons:
                    return "100:hat=0; 300:hat=45; 500:hat=90; 700:hat=-1; 900:hat=225; 1100:hat=-1";
                case VirtualFunctionCatalog.ButtonsHat:
                    return "100:up=1; 300:right=1; 500:up=0; 700:right=0; 900:left=1; 1100:left=0";
                default:
                    return string.Empty;
            }
        }
    }
}
