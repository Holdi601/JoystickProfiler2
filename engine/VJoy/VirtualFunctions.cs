#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JoyPro.Engine.VJoy
{
    // Virtual functions (docs/virtual-joystick.md "Virtual functions"): per game in virtual mode the user adds functions
    // that JoyPro.VJoy evaluates every pass between the physical inputs and the virtual outputs (vJoy buttons, axes and
    // hats, or the virtual Xbox pad): axis bands -> buttons, button cycles, toggles, short/long/double press, shift layers,
    // axis tools, turbo, macros and hat helpers - Joystick Gremlin-like features inside JoyPro's virtual mode.
    //
    // This file (model, catalog, tokens, resolution) and VirtualFunctionRuntime.cs (the evaluation) are plain C# 7.3:
    // the engine (CLI, storage, planner, "functions test"), JoyPro.VJoy (the forwarder loop) and the GUI (editor
    // preview) compile the same code, so a function behaves the same in the test, the preview and the game.

    // One function as stored in the profile (VJoySettings.Functions[game]) and passed to the forwarder.
    public sealed class VirtualFunction
    {
        public string Id { get; set; }
        public string Name { get; set; }
        // A VirtualFunctionCatalog type id: bands, cycle, toggle, switch, presses, shift, merge, split, curve,
        // axis-buttons, axes-hat, buttons-axis, encoder, turbo, macro, hat-buttons, buttons-hat.
        public string Type { get; set; }
        public bool Enabled { get; set; }
        // Keyboard outputs (KEY_* targets) only when the user switched this on for the function.
        public bool Keyboard { get; set; }
        public List<VfInput> Inputs { get; set; }
        public List<VfOutput> Outputs { get; set; }
        public Dictionary<string, double> Params { get; set; }
        // Band edges 0..1 ascending (bands: N edges -> N + 1 bands).
        public List<double> Edges { get; set; }
        // Macro steps.
        public List<VfStep> Steps { get; set; }
        // Response curve (curve): JoyPro's axis tuning text (AxisTuning.Format, DCS parameters).
        public string Tuning { get; set; }

        public VirtualFunction()
        {
            Id = Name = Type = Tuning = string.Empty;
            Enabled = true;
            Inputs = new List<VfInput>();
            Outputs = new List<VfOutput>();
            Params = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Edges = new List<double>();
            Steps = new List<VfStep>();
        }

        public void Normalize()
        {
            Id = (Id ?? string.Empty).Trim();
            Name = (Name ?? string.Empty).Trim();
            Type = (Type ?? string.Empty).Trim().ToLowerInvariant();
            Tuning = Tuning ?? string.Empty;
            if (Inputs == null)
                Inputs = new List<VfInput>();
            if (Outputs == null)
                Outputs = new List<VfOutput>();
            Inputs.RemoveAll(i => i == null);
            Outputs.RemoveAll(o => o == null);
            Params = Params == null
                ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(Params, StringComparer.OrdinalIgnoreCase);
            if (Edges == null)
                Edges = new List<double>();
            if (Steps == null)
                Steps = new List<VfStep>();
            Steps.RemoveAll(s => s == null);
            foreach (VfInput input in Inputs)
            {
                input.Role = (input.Role ?? string.Empty).Trim().ToLowerInvariant();
                input.Device = (input.Device ?? string.Empty).Trim();
                input.Input = (input.Input ?? string.Empty).Trim().ToUpperInvariant();
            }
            foreach (VfOutput output in Outputs)
            {
                output.Role = (output.Role ?? string.Empty).Trim().ToLowerInvariant();
                output.Target = (output.Target ?? string.Empty).Trim().ToUpperInvariant();
                output.Mode = (output.Mode ?? string.Empty).Trim().ToLowerInvariant();
            }
        }

        public double Param(string key, double fallback)
        {
            double value;
            return Params != null && Params.TryGetValue(key, out value) && !double.IsNaN(value) && !double.IsInfinity(value) ? value : fallback;
        }

        public VfInput InputOf(string role)
        {
            return Inputs == null ? null : Inputs.FirstOrDefault(i => string.Equals(i.Role, role, StringComparison.OrdinalIgnoreCase));
        }

        public VfOutput OutputOf(string role)
        {
            return Outputs == null ? null : Outputs.FirstOrDefault(o => string.Equals(o.Role, role, StringComparison.OrdinalIgnoreCase));
        }

        public VirtualFunction Clone()
        {
            VirtualFunction copy = (VirtualFunction)MemberwiseClone();
            copy.Inputs = (Inputs ?? new List<VfInput>()).Select(i => i.Clone()).ToList();
            copy.Outputs = (Outputs ?? new List<VfOutput>()).Select(o => o.Clone()).ToList();
            copy.Params = new Dictionary<string, double>(Params ?? new Dictionary<string, double>(), StringComparer.OrdinalIgnoreCase);
            copy.Edges = new List<double>(Edges ?? new List<double>());
            copy.Steps = (Steps ?? new List<VfStep>()).Select(s => s.Clone()).ToList();
            return copy;
        }
    }

    // A physical input of a function: the JoyPro device ("Name {GUID}") and the input token (JOY_X .. JOY_SLIDER2,
    // JOY_BTN<n>, POV<n> for a whole hat, JOY_BTN_POV<n>_<U|UR|R|DR|D|DL|L|UL> for one hat direction).
    public sealed class VfInput
    {
        public string Role { get; set; }
        public string Device { get; set; }
        public string Input { get; set; }
        // Resolved for the forwarder (engine vjoy forwarder-config): source index, kind axis|button|hat|hatdir, index
        // (0-based), hat direction 0..7. Empty/-1 in the profile.
        public int Source { get; set; }
        public string Kind { get; set; }
        public int Index { get; set; }
        public int Direction { get; set; }

        public VfInput()
        {
            Role = Device = Input = Kind = string.Empty;
            Source = Index = Direction = -1;
        }

        public VfInput Clone()
        {
            return (VfInput)MemberwiseClone();
        }
    }

    // A virtual output: a vJoy input (Target JOY_BTN<n>, JOY_X .. JOY_SLIDER2, POV<n> on vJoy device VJoyDevice), a
    // control of the virtual Xbox pad (XB_A .. XB_DPAD_RIGHT, XB_LX .. XB_RT, XB_DPAD; VJoyDevice 0) or a keyboard key
    // (KEY_A, KEY_LCTRL, KEY_F1 ...; only when the function's Keyboard switch is on). Mode (bands, axis-buttons): hold
    // (while inside), enter (pulse on entering), leave (pulse on leaving), both.
    public sealed class VfOutput
    {
        public string Role { get; set; }
        public string Target { get; set; }
        public int VJoyDevice { get; set; }
        public string Mode { get; set; }
        // Resolved for the forwarder: kind button|axis|hat (vJoy) | xbutton|xaxis|xdpad (Xbox pad) | key, the index of
        // the vJoy device in the forwarder's device list, the 0-based index (key: scan code), the Xbox button mask.
        public string Kind { get; set; }
        public int DeviceIndex { get; set; }
        public int Index { get; set; }
        public int Mask { get; set; }

        public VfOutput()
        {
            Role = Target = Mode = Kind = string.Empty;
            DeviceIndex = Index = -1;
        }

        public VfOutput Clone()
        {
            return (VfOutput)MemberwiseClone();
        }
    }

    // One macro step: after DelayMs, press / release / tap (press, HoldMs, release) an output (its role, e.g. "out1")
    // or a key (KEY_*; only with the function's Keyboard switch on).
    public sealed class VfStep
    {
        public string Output { get; set; }
        public string Action { get; set; }
        public int DelayMs { get; set; }
        public int HoldMs { get; set; }

        public VfStep()
        {
            Output = string.Empty;
            Action = "tap";
            HoldMs = 80;
        }

        public VfStep Clone()
        {
            return (VfStep)MemberwiseClone();
        }
    }

    // ---------------------------------------------------------------------------------------------- catalog

    public sealed class VfRoleInfo
    {
        public string Role { get; set; }
        public string Label { get; set; }
        // axis | button | hat | any
        public string Kind { get; set; }
        public bool Optional { get; set; }
        // Numbered roles (band1, band2 ...): Role is the prefix.
        public bool Numbered { get; set; }
        public bool HasMode { get; set; }
    }

    public sealed class VfParamInfo
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Step { get; set; }
        public double Default { get; set; }
        public string Unit { get; set; }
        public string Help { get; set; }
        // Choices by value 0, 1, 2 ... (empty: a number).
        public List<string> Choices { get; set; }

        public VfParamInfo()
        {
            Key = Label = Unit = Help = string.Empty;
            Choices = new List<string>();
        }
    }

    public sealed class VfTypeInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Icon { get; set; }
        public string Summary { get; set; }
        public string Example { get; set; }
        public List<VfRoleInfo> Inputs { get; set; }
        public List<VfRoleInfo> Outputs { get; set; }
        public List<VfParamInfo> Params { get; set; }
        // Numbered roles: how many by default and at most.
        public int DefaultCount { get; set; }
        public int MaxCount { get; set; }

        public VfTypeInfo()
        {
            Inputs = new List<VfRoleInfo>();
            Outputs = new List<VfRoleInfo>();
            Params = new List<VfParamInfo>();
        }

        public VfParamInfo ParamInfo(string key)
        {
            return Params.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static class VirtualFunctionCatalog
    {
        public const string Bands = "bands";
        public const string Cycle = "cycle";
        public const string Toggle = "toggle";
        public const string Switch = "switch";
        public const string Presses = "presses";
        public const string Shift = "shift";
        public const string Merge = "merge";
        public const string Split = "split";
        public const string Curve = "curve";
        public const string AxisButtons = "axis-buttons";
        public const string AxesHat = "axes-hat";
        public const string ButtonsAxis = "buttons-axis";
        public const string Encoder = "encoder";
        public const string Turbo = "turbo";
        public const string Macro = "macro";
        public const string HatButtons = "hat-buttons";
        public const string ButtonsHat = "buttons-hat";

        public const string ModeHold = "hold";
        public const string ModeEnter = "enter";
        public const string ModeLeave = "leave";
        public const string ModeBoth = "both";
        public static readonly string[] Modes = { ModeHold, ModeEnter, ModeLeave, ModeBoth };

        private static List<VfTypeInfo> types;

        public static IReadOnlyList<VfTypeInfo> Types
        {
            get
            {
                if (types == null)
                    types = Build();
                return types;
            }
        }

        public static VfTypeInfo Get(string id)
        {
            string key = (id ?? string.Empty).Trim();
            return Types.FirstOrDefault(t => string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase));
        }

        private static VfRoleInfo Role(string role, string label, string kind, bool optional = false, bool numbered = false, bool mode = false)
        {
            return new VfRoleInfo { Role = role, Label = label, Kind = kind, Optional = optional, Numbered = numbered, HasMode = mode };
        }

        private static VfParamInfo P(string key, string label, double min, double max, double step, double value, string unit, string help, params string[] choices)
        {
            return new VfParamInfo { Key = key, Label = label, Min = min, Max = max, Step = step, Default = value, Unit = unit ?? string.Empty, Help = help ?? string.Empty, Choices = choices.ToList() };
        }

        private static VfParamInfo Pulse()
        {
            return P("pulseMs", "Pulse length", 20, 1000, 10, 80, "ms", "How long a pulse holds the virtual button (games that poll slowly need 50 ms or more).");
        }

        private static List<VfTypeInfo> Build()
        {
            List<VfTypeInfo> list = new List<VfTypeInfo>();
            list.Add(new VfTypeInfo
            {
                Id = Bands, Name = "Axis bands", Icon = "≡",
                Summary = "Splits an axis into bands; each band holds or pulses its own virtual button.",
                Example = "Throttle detents (idle cutoff, afterburner gate), flap lever positions, collective zones.",
                Inputs = { Role("in", "Axis", "axis") },
                Outputs = { Role("band", "Band", "button", optional: true, numbered: true, mode: true) },
                Params = { P("hysteresis", "Hysteresis", 0, 0.1, 0.005, 0.01, "of travel", "An edge must be passed by this much before the band changes, so a lever resting on an edge does not flicker."), Pulse() },
                DefaultCount = 3, MaxCount = 16
            });
            list.Add(new VfTypeInfo
            {
                Id = Cycle, Name = "Button cycle", Icon = "↻",
                Summary = "Each press steps through a list of virtual buttons (A, B, C, A ...).",
                Example = "Master modes, radio presets, view cycling on one button.",
                Inputs = { Role("next", "Next", "button"), Role("prev", "Back (optional)", "button", optional: true), Role("reset", "Reset (optional)", "button", optional: true) },
                Outputs = { Role("step", "Step", "button", numbered: true) },
                Params =
                {
                    P("hold", "Output", 0, 1, 1, 0, "", "Pulse: each press pulses the new step's button. Hold: the current step's button stays pressed (a selector).", "Pulse", "Hold"),
                    P("wrap", "At the end", 0, 1, 1, 1, "", "Start over after the last step, or stay there.", "Stay", "Start over"),
                    Pulse()
                },
                DefaultCount = 3, MaxCount = 32
            });
            list.Add(new VfTypeInfo
            {
                Id = Toggle, Name = "Toggle (latch)", Icon = "◐",
                Summary = "A momentary press switches a virtual button on, the next press switches it off.",
                Example = "Push button as a maintained switch: gear horn silence, a lamp test that stays on.",
                Inputs = { Role("in", "Button", "button") },
                Outputs = { Role("out", "Latched button", "button") },
                Params = { P("startOn", "At start", 0, 1, 1, 0, "", "The state when the forwarder starts.", "Off", "On") }
            });
            list.Add(new VfTypeInfo
            {
                Id = Switch, Name = "Switch to on/off pulses", Icon = "⇅",
                Summary = "A 2-position switch gives a pulse on one virtual button when switched on and on another when switched off.",
                Example = "Games that need a separate bind for each switch position (gear up / gear down).",
                Inputs = { Role("in", "Switch", "button") },
                Outputs = { Role("on", "When switched on", "button", optional: true), Role("off", "When switched off", "button", optional: true) },
                Params = { Pulse(), P("syncAtStart", "At start", 0, 1, 1, 0, "", "Pulse the current position once when the forwarder starts, so the game matches the switch.", "Nothing", "Pulse the position") }
            });
            list.Add(new VfTypeInfo
            {
                Id = Presses, Name = "Short / long / double press", Icon = "⋯",
                Summary = "One button, up to three virtual buttons: a short press, a long press and a double press.",
                Example = "Short: next waypoint, long: previous; double: mark point.",
                Inputs = { Role("in", "Button", "button") },
                Outputs = { Role("short", "Short press", "button", optional: true), Role("long", "Long press", "button", optional: true), Role("double", "Double press", "button", optional: true) },
                Params =
                {
                    P("longMs", "Long press after", 150, 3000, 10, 500, "ms", "Held this long it is a long press."),
                    P("doubleMs", "Double press within", 100, 1000, 10, 300, "ms", "A second press within this time after the first release is a double press (only with a double press output; the short press then waits this long)."),
                    P("longHold", "Long press output", 0, 1, 1, 1, "", "Pulse once, or hold the virtual button while the button stays down.", "Pulse", "Hold while held"),
                    Pulse()
                }
            });
            list.Add(new VfTypeInfo
            {
                Id = Shift, Name = "Shift layer", Icon = "⇧",
                Summary = "While a shift button is held, other inputs drive other virtual outputs (a modifier for games without modifiers).",
                Example = "Pinky switch held: the hat moves the radar cursor instead of trimming.",
                Inputs = { Role("shift", "Shift button", "button"), Role("in", "Input", "any", numbered: true) },
                Outputs = { Role("out", "Shifted output", "any", numbered: true), Role("base", "Unshifted output", "any", optional: true, numbered: true) },
                Params = { P("latch", "Shift button", 0, 1, 1, 0, "", "Hold: the layer is on while the button is held. Toggle: each press switches the layer on or off.", "Hold", "Toggle") },
                DefaultCount = 1, MaxCount = 32
            });
            list.Add(new VfTypeInfo
            {
                Id = Merge, Name = "Merge two axes", Icon = "⋈",
                Summary = "Two axes into one: difference (toe brakes as rudder), sum, average, larger or smaller.",
                Example = "Toe brakes into one brake axis, or two levers as one differential axis.",
                Inputs = { Role("a", "Axis A", "axis"), Role("b", "Axis B", "axis") },
                Outputs = { Role("out", "Merged axis", "axis") },
                Params =
                {
                    P("mode", "How", 0, 4, 1, 0, "", "Difference: centre + (A - B) / 2. Sum: A + B. Average. Larger. Smaller.", "Difference", "Sum", "Average", "Larger", "Smaller"),
                    P("invertA", "Invert A", 0, 1, 1, 0, "", "", "No", "Yes"),
                    P("invertB", "Invert B", 0, 1, 1, 0, "", "", "No", "Yes")
                }
            });
            list.Add(new VfTypeInfo
            {
                Id = Split, Name = "Split an axis", Icon = "⋔",
                Summary = "One axis into two: below the centre drives one virtual axis, above it the other.",
                Example = "One rocker as two separate brake axes; a centred lever as two throttles.",
                Inputs = { Role("in", "Axis", "axis") },
                Outputs = { Role("low", "Lower half", "axis", optional: true), Role("high", "Upper half", "axis", optional: true) },
                Params = { P("centre", "Split at", 0.1, 0.9, 0.01, 0.5, "of travel", "Where the axis is split; both outputs are 0 there.") }
            });
            list.Add(new VfTypeInfo
            {
                Id = Curve, Name = "Axis curve", Icon = "∿",
                Summary = "Invert, dead zone, saturation and response curve on an axis, with JoyPro's axis curves.",
                Example = "A curve for a game without curve settings; invert a lever the game cannot invert.",
                Inputs = { Role("in", "Axis", "axis") },
                Outputs = { Role("out", "Shaped axis", "axis") }
            });
            list.Add(new VfTypeInfo
            {
                Id = AxisButtons, Name = "Axis ends to buttons", Icon = "⇔",
                Summary = "A virtual button at each end of an axis (hold or pulse), with hysteresis.",
                Example = "Throttle at idle presses 'engine stop'; a pedal pushed fully presses a button.",
                Inputs = { Role("in", "Axis", "axis") },
                Outputs = { Role("low", "At the low end", "button", optional: true, mode: true), Role("high", "At the high end", "button", optional: true, mode: true) },
                Params =
                {
                    P("threshold", "End zone", 0.01, 0.45, 0.01, 0.1, "of travel", "How far from each end the button fires."),
                    P("hysteresis", "Hysteresis", 0, 0.1, 0.005, 0.02, "of travel", ""),
                    Pulse()
                }
            });
            list.Add(new VfTypeInfo
            {
                Id = AxesHat, Name = "Axes to hat", Icon = "✛",
                Summary = "Two axes (a thumb stick or slew control) as an 8-way or 4-way hat.",
                Example = "A mini stick on the throttle as a hat for a game that only takes hats there.",
                Inputs = { Role("x", "Left/right axis", "axis"), Role("y", "Up/down axis", "axis") },
                Outputs = { Role("hat", "Hat", "hat") },
                Params =
                {
                    P("threshold", "Deflection", 0.1, 0.95, 0.05, 0.5, "of half travel", "How far from the centre counts as a direction."),
                    P("ways", "Directions", 4, 8, 4, 8, "", "4: up, right, down, left only. 8: diagonals too.")
                }
            });
            list.Add(new VfTypeInfo
            {
                Id = ButtonsAxis, Name = "Buttons to axis", Icon = "±",
                Summary = "Two buttons move a virtual axis up and down at a rate (incremental), optionally back to its start.",
                Example = "Trim or zoom on a hat; a throttle on two buttons.",
                Inputs = { Role("up", "Up", "button"), Role("down", "Down", "button"), Role("centre", "Back to start (optional)", "button", optional: true) },
                Outputs = { Role("out", "Axis", "axis") },
                Params =
                {
                    P("rate", "Speed", 0.05, 5, 0.05, 0.5, "travel/s", "How much of the full travel per second while a button is held."),
                    P("start", "Start", 0, 1, 0.05, 0.5, "of travel", "The value at start and after 'back to start'."),
                    P("returnRate", "Spring back", 0, 10, 0.1, 0, "travel/s", "0: the axis stays where it is (trim). More: it goes back to the start at this speed when no button is held.")
                }
            });
            list.Add(new VfTypeInfo
            {
                Id = Encoder, Name = "Axis to encoder pulses", Icon = "⟳",
                Summary = "Moving an axis gives up/down button pulses, one per step (relative mode).",
                Example = "A rotary axis turns a radio or altimeter knob that only has increase/decrease binds.",
                Inputs = { Role("in", "Axis", "axis") },
                Outputs = { Role("up", "Up pulse", "button"), Role("down", "Down pulse", "button") },
                Params =
                {
                    P("step", "Step", 0.005, 0.25, 0.005, 0.05, "of travel", "Axis travel per pulse."),
                    P("pulseMs", "Pulse length", 20, 500, 10, 60, "ms", ""),
                    P("gapMs", "Gap", 20, 500, 10, 60, "ms", "Time between two pulses."),
                    P("maxQueue", "Queue", 1, 50, 1, 8, "pulses", "Pulses that may wait when the axis moves fast.")
                }
            });
            list.Add(new VfTypeInfo
            {
                Id = Turbo, Name = "Turbo (repeat)", Icon = "»",
                Summary = "Repeats a virtual button press at a rate while the button is held (or until pressed again).",
                Example = "Countermeasure bursts, zoom steps, a weapon that needs one press per shot.",
                Inputs = { Role("in", "Button", "button") },
                Outputs = { Role("out", "Repeated button", "button") },
                Params =
                {
                    P("onMs", "Pressed for", 20, 1000, 10, 50, "ms", ""),
                    P("offMs", "Released for", 20, 1000, 10, 50, "ms", ""),
                    P("toggle", "Runs", 0, 1, 1, 0, "", "While held, or from one press to the next.", "While held", "Press on, press off")
                }
            });
            list.Add(new VfTypeInfo
            {
                Id = Macro, Name = "Macro", Icon = "▶",
                Summary = "One press plays a short sequence of virtual presses with delays (keys only when switched on).",
                Example = "Start-up sequence: battery, generator, APU with pauses.",
                Inputs = { Role("trigger", "Trigger", "button") },
                Outputs = { Role("out", "Output", "button", optional: true, numbered: true) },
                Params = { P("repeat", "Runs", 0, 1, 1, 0, "", "Once per press, or again and again while held.", "Once per press", "Repeat while held") },
                DefaultCount = 2, MaxCount = 16
            });
            list.Add(new VfTypeInfo
            {
                Id = HatButtons, Name = "Hat to buttons", Icon = "✣",
                Summary = "A hat as 4 or 8 separate virtual buttons.",
                Example = "Games that cannot bind hat directions, or need a diagonal as its own button.",
                Inputs = { Role("hat", "Hat", "hat") },
                Outputs =
                {
                    Role("up", "Up", "button", optional: true), Role("right", "Right", "button", optional: true),
                    Role("down", "Down", "button", optional: true), Role("left", "Left", "button", optional: true),
                    Role("upright", "Up-right", "button", optional: true), Role("downright", "Down-right", "button", optional: true),
                    Role("downleft", "Down-left", "button", optional: true), Role("upleft", "Up-left", "button", optional: true)
                },
                Params = { P("ways", "Diagonals", 4, 8, 4, 4, "", "4: a diagonal presses both neighbours. 8: a diagonal presses its own button (when it has one).") }
            });
            list.Add(new VfTypeInfo
            {
                Id = ButtonsHat, Name = "Buttons to hat (8-way)", Icon = "⊕",
                Summary = "Four buttons (or a 4-way switch) as one 8-way hat: two neighbours at once give the diagonal.",
                Example = "A 4-way switch made of buttons as a hat; an 8-way hat from a 4-way one.",
                Inputs = { Role("up", "Up", "button", optional: true), Role("right", "Right", "button", optional: true), Role("down", "Down", "button", optional: true), Role("left", "Left", "button", optional: true) },
                Outputs = { Role("hat", "Hat", "hat") },
                Params = { P("settleMs", "Diagonal settle", 0, 200, 5, 30, "ms", "When one of two buttons of a diagonal is let go, the hat waits this long before it shows the other direction, so letting go of a diagonal does not flick.") }
            });
            return list;
        }

        // The roles a function of this type uses now (numbered roles expanded by the function's count).
        public static List<VfRoleInfo> ExpandInputs(VirtualFunction function)
        {
            return Expand(Get(function.Type), function, true);
        }

        public static List<VfRoleInfo> ExpandOutputs(VirtualFunction function)
        {
            return Expand(Get(function.Type), function, false);
        }

        public static int CountOf(VirtualFunction function)
        {
            VfTypeInfo info = Get(function.Type);
            if (info == null)
                return 0;
            if (info.Id == Bands)
                return Math.Max(1, (function.Edges == null ? 0 : function.Edges.Count) + 1);
            VfRoleInfo numbered = info.Outputs.Concat(info.Inputs).FirstOrDefault(r => r.Numbered);
            if (numbered == null)
                return 0;
            int count = 0;
            foreach (string role in (function.Outputs ?? new List<VfOutput>()).Select(o => o.Role).Concat((function.Inputs ?? new List<VfInput>()).Select(i => i.Role)))
            {
                int number;
                string prefix = info.Inputs.Concat(info.Outputs).Where(r => r.Numbered).Select(r => r.Role).FirstOrDefault(p => role != null && role.StartsWith(p, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(role.Substring(p.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out number));
                if (prefix != null && int.TryParse(role.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                    count = Math.Max(count, number);
            }
            return Math.Max(count, 1);
        }

        private static List<VfRoleInfo> Expand(VfTypeInfo info, VirtualFunction function, bool inputs)
        {
            List<VfRoleInfo> output = new List<VfRoleInfo>();
            if (info == null)
                return output;
            int count = CountOf(function);
            foreach (VfRoleInfo role in inputs ? info.Inputs : info.Outputs)
            {
                if (!role.Numbered)
                {
                    output.Add(role);
                    continue;
                }
                for (int i = 1; i <= count; i++)
                {
                    output.Add(new VfRoleInfo
                    {
                        Role = role.Role + i.ToString(CultureInfo.InvariantCulture),
                        Label = role.Label + " " + i.ToString(CultureInfo.InvariantCulture),
                        Kind = role.Kind,
                        Optional = role.Optional,
                        HasMode = role.HasMode
                    });
                }
            }
            return output;
        }

        // A new function of a type with its defaults (inputs and outputs empty: the user or the engine fills them).
        public static VirtualFunction Create(string type, string name)
        {
            VfTypeInfo info = Get(type);
            if (info == null)
                throw new ArgumentException("Unknown function type: " + type + ". Types: " + string.Join(", ", Types.Select(t => t.Id)));
            VirtualFunction function = new VirtualFunction { Type = info.Id, Name = string.IsNullOrWhiteSpace(name) ? info.Name : name.Trim() };
            foreach (VfParamInfo param in info.Params)
                function.Params[param.Key] = param.Default;
            if (info.Id == Bands)
                function.Edges = new List<double> { 0.1, 0.9 };
            if (info.Id == Curve)
                function.Tuning = "inv=0;sl=0;dz=0.05;sx=1;sy=1;cv=0.2";
            int count = info.DefaultCount;
            foreach (VfRoleInfo role in info.Inputs)
            {
                if (role.Numbered)
                {
                    for (int i = 1; i <= Math.Max(1, count); i++)
                        function.Inputs.Add(new VfInput { Role = role.Role + i.ToString(CultureInfo.InvariantCulture) });
                }
                else
                    function.Inputs.Add(new VfInput { Role = role.Role });
            }
            foreach (VfRoleInfo role in info.Outputs)
            {
                // Left out until the user adds them: a double press output makes the short press wait, the diagonals
                // of a hat are only wanted for 8 buttons.
                if ((info.Id == Presses && role.Role == "double") || (info.Id == HatButtons && role.Role.Length > 5))
                    continue;
                if (role.Numbered)
                {
                    if (role.Optional && info.Id == Shift)
                        continue; // unshifted outputs: the input's own slot unless the user picks one
                    for (int i = 1; i <= Math.Max(1, count); i++)
                        function.Outputs.Add(new VfOutput { Role = role.Role + i.ToString(CultureInfo.InvariantCulture), Mode = role.HasMode ? ModeHold : string.Empty });
                }
                else
                    function.Outputs.Add(new VfOutput { Role = role.Role, Mode = role.HasMode ? ModeHold : string.Empty });
            }
            if (info.Id == Macro)
            {
                function.Steps.Add(new VfStep { Output = "out1", Action = "tap", DelayMs = 0, HoldMs = 80 });
                function.Steps.Add(new VfStep { Output = "out2", Action = "tap", DelayMs = 500, HoldMs = 80 });
            }
            return function;
        }

        // What the function is missing or does wrong, in plain words (empty: fine). Unresolved devices are not checked
        // here (a device may be unplugged); the engine adds that.
        public static List<string> Validate(VirtualFunction function)
        {
            List<string> problems = new List<string>();
            VfTypeInfo info = Get(function.Type);
            if (info == null)
            {
                problems.Add("Unknown function type \"" + function.Type + "\".");
                return problems;
            }
            foreach (VfRoleInfo role in ExpandInputs(function))
            {
                VfInput input = function.InputOf(role.Role);
                if (input == null || input.Input.Length == 0 || input.Device.Length == 0)
                {
                    if (!role.Optional)
                        problems.Add(role.Label + ": no input chosen.");
                    continue;
                }
                string kind;
                int index;
                int direction;
                if (!VfTokens.TryParseInput(input.Input, out kind, out index, out direction))
                    problems.Add(role.Label + ": \"" + input.Input + "\" is not a joystick input (JOY_X, JOY_BTN5, POV1, JOY_BTN_POV1_U ...).");
                else if (!VfTokens.Fits(role.Kind, kind, true))
                    problems.Add(role.Label + ": needs " + KindWord(role.Kind) + ", " + input.Input + " is " + KindWord(kind) + ".");
            }
            foreach (VfRoleInfo role in ExpandOutputs(function))
            {
                VfOutput output = function.OutputOf(role.Role);
                if (output == null || output.Target.Length == 0)
                {
                    if (!role.Optional)
                        problems.Add(role.Label + ": no virtual output.");
                    continue;
                }
                string kind;
                int index;
                int mask;
                if (!VfTokens.TryParseOutput(output.Target, out kind, out index, out mask))
                {
                    problems.Add(role.Label + ": \"" + output.Target + "\" is not a virtual output (JOY_BTN30, JOY_Z, POV2, XB_A, KEY_F1 ...).");
                    continue;
                }
                if (kind == "key" && !function.Keyboard)
                    problems.Add(role.Label + ": " + output.Target + " is a keyboard key; switch on \"Keyboard output\" for this function to allow keys.");
                string required = role.Kind;
                if (function.Type == Shift && role.Role.StartsWith("out", StringComparison.Ordinal) || function.Type == Shift && role.Role.StartsWith("base", StringComparison.Ordinal))
                {
                    VfInput source = function.InputOf("in" + role.Role.Substring(role.Role.StartsWith("out", StringComparison.Ordinal) ? 3 : 4));
                    string sourceKind;
                    int sourceIndex;
                    int sourceDirection;
                    if (source != null && VfTokens.TryParseInput(source.Input, out sourceKind, out sourceIndex, out sourceDirection))
                        required = sourceKind == "hatdir" ? "button" : sourceKind;
                }
                if (!VfTokens.Fits(required, VfTokens.OutputClass(kind), false))
                    problems.Add(role.Label + ": needs " + KindWord(required) + ", " + output.Target + " is " + KindWord(VfTokens.OutputClass(kind)) + ".");
                if (role.HasMode && output.Mode.Length > 0 && !Modes.Contains(output.Mode))
                    problems.Add(role.Label + ": mode \"" + output.Mode + "\" is not hold, enter, leave or both.");
            }
            if (info.Id == Bands)
            {
                for (int i = 0; i < function.Edges.Count; i++)
                {
                    if (function.Edges[i] <= 0 || function.Edges[i] >= 1)
                        problems.Add("Band edge " + (i + 1).ToString(CultureInfo.InvariantCulture) + " must lie inside the travel (0..1).");
                    if (i > 0 && function.Edges[i] <= function.Edges[i - 1])
                        problems.Add("Band edges must go up: edge " + (i + 1).ToString(CultureInfo.InvariantCulture) + " is not above edge " + i.ToString(CultureInfo.InvariantCulture) + ".");
                }
            }
            if (info.Id == Macro)
            {
                if (function.Steps.Count == 0)
                    problems.Add("The macro has no steps.");
                foreach (VfStep step in function.Steps)
                {
                    string target = (step.Output ?? string.Empty).Trim();
                    if (target.StartsWith("KEY_", StringComparison.OrdinalIgnoreCase))
                    {
                        int scan;
                        if (!VfTokens.TryKey(target, out scan))
                            problems.Add("Macro step: unknown key " + target + ".");
                        else if (!function.Keyboard)
                            problems.Add("Macro step " + target + " is a keyboard key; switch on \"Keyboard output\" for this function to allow keys.");
                    }
                    else if (function.OutputOf(target) == null || function.OutputOf(target).Target.Length == 0)
                        problems.Add("Macro step: output \"" + target + "\" has no virtual output.");
                    string action = (step.Action ?? string.Empty).Trim().ToLowerInvariant();
                    if (action != "tap" && action != "press" && action != "release")
                        problems.Add("Macro step: action \"" + step.Action + "\" is not tap, press or release.");
                    if (step.DelayMs < 0 || step.DelayMs > 60000 || step.HoldMs < 0 || step.HoldMs > 60000)
                        problems.Add("Macro step: delays are 0..60000 ms.");
                }
            }
            foreach (VfParamInfo param in info.Params)
            {
                double value = function.Param(param.Key, param.Default);
                if (value < param.Min - 1e-9 || value > param.Max + 1e-9)
                    problems.Add(param.Label + ": " + value.ToString(CultureInfo.InvariantCulture) + " is outside " + param.Min.ToString(CultureInfo.InvariantCulture) + ".." + param.Max.ToString(CultureInfo.InvariantCulture) + ".");
            }
            return problems;
        }

        public static string KindWord(string kind)
        {
            switch (kind)
            {
                case "axis": return "an axis";
                case "button": return "a button";
                case "hat": return "a hat";
                case "hatdir": return "a hat direction";
                case "any": return "any input";
                default: return kind;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------- tokens

    public static class VfTokens
    {
        public static readonly string[] AxisTokens = { "JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1", "JOY_SLIDER2" };
        public static readonly string[] AxisNames = { "X", "Y", "Z", "Rx", "Ry", "Rz", "Slider 1", "Slider 2" };
        public static readonly string[] HatDirections = { "U", "UR", "R", "DR", "D", "DL", "L", "UL" };
        public static readonly string[] XboxAxes = { "XB_LX", "XB_LY", "XB_RX", "XB_RY", "XB_LT", "XB_RT" };

        // XINPUT_GAMEPAD button masks (XboxReport).
        private static readonly string[] XboxButtonTokens = { "XB_DPAD_UP", "XB_DPAD_DOWN", "XB_DPAD_LEFT", "XB_DPAD_RIGHT", "XB_START", "XB_BACK", "XB_LS", "XB_RS", "XB_LB", "XB_RB", "XB_GUIDE", "XB_A", "XB_B", "XB_X", "XB_Y" };
        private static readonly int[] XboxButtonMasks = { 0x0001, 0x0002, 0x0004, 0x0008, 0x0010, 0x0020, 0x0040, 0x0080, 0x0100, 0x0200, 0x0400, 0x1000, 0x2000, 0x4000, 0x8000 };

        // Keyboard scan codes (set 1; + 0x100 = extended key).
        private static readonly Dictionary<string, int> Keys = BuildKeys();

        private static Dictionary<string, int> BuildKeys()
        {
            Dictionary<string, int> keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string row1 = "1234567890";
            for (int i = 0; i < row1.Length; i++)
                keys[row1[i].ToString()] = 0x02 + i;
            string[] letters = { "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM" };
            int[] starts = { 0x10, 0x1E, 0x2C };
            for (int r = 0; r < letters.Length; r++)
            {
                for (int i = 0; i < letters[r].Length; i++)
                    keys[letters[r][i].ToString()] = starts[r] + i;
            }
            for (int i = 1; i <= 10; i++)
                keys["F" + i.ToString(CultureInfo.InvariantCulture)] = 0x3A + i;
            keys["F11"] = 0x57;
            keys["F12"] = 0x58;
            keys["ESC"] = 0x01; keys["ESCAPE"] = 0x01; keys["MINUS"] = 0x0C; keys["EQUALS"] = 0x0D; keys["BACK"] = 0x0E; keys["BACKSPACE"] = 0x0E;
            keys["TAB"] = 0x0F; keys["LBRACKET"] = 0x1A; keys["RBRACKET"] = 0x1B; keys["ENTER"] = 0x1C; keys["RETURN"] = 0x1C; keys["LCTRL"] = 0x1D; keys["LCONTROL"] = 0x1D;
            keys["SEMICOLON"] = 0x27; keys["APOSTROPHE"] = 0x28; keys["GRAVE"] = 0x29; keys["LSHIFT"] = 0x2A; keys["BACKSLASH"] = 0x2B; keys["COMMA"] = 0x33; keys["PERIOD"] = 0x34;
            keys["SLASH"] = 0x35; keys["RSHIFT"] = 0x36; keys["NUMMULTIPLY"] = 0x37; keys["LALT"] = 0x38; keys["SPACE"] = 0x39; keys["CAPSLOCK"] = 0x3A; keys["NUMLOCK"] = 0x45;
            keys["SCROLLLOCK"] = 0x46; keys["NUM7"] = 0x47; keys["NUM8"] = 0x48; keys["NUM9"] = 0x49; keys["NUMMINUS"] = 0x4A; keys["NUM4"] = 0x4B; keys["NUM5"] = 0x4C;
            keys["NUM6"] = 0x4D; keys["NUMPLUS"] = 0x4E; keys["NUM1"] = 0x4F; keys["NUM2"] = 0x50; keys["NUM3"] = 0x51; keys["NUM0"] = 0x52; keys["NUMDECIMAL"] = 0x53;
            keys["RCTRL"] = 0x11D; keys["RCONTROL"] = 0x11D; keys["RALT"] = 0x138; keys["UP"] = 0x148; keys["DOWN"] = 0x150; keys["LEFT"] = 0x14B; keys["RIGHT"] = 0x14D;
            keys["HOME"] = 0x147; keys["END"] = 0x14F; keys["PAGEUP"] = 0x149; keys["PAGEDOWN"] = 0x151; keys["INSERT"] = 0x152; keys["DELETE"] = 0x153;
            keys["NUMENTER"] = 0x11C; keys["NUMDIVIDE"] = 0x135;
            return keys;
        }

        public static IEnumerable<string> KeyNames
        {
            get { return Keys.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Select(k => "KEY_" + k.ToUpperInvariant()); }
        }

        public static bool TryKey(string token, out int scan)
        {
            scan = 0;
            string value = (token ?? string.Empty).Trim().ToUpperInvariant();
            return value.StartsWith("KEY_", StringComparison.Ordinal) && Keys.TryGetValue(value.Substring(4), out scan);
        }

        // A physical input token: kind axis|button|hat|hatdir, 0-based index, hat direction (hatdir) 0..7.
        public static bool TryParseInput(string token, out string kind, out int index, out int direction)
        {
            kind = string.Empty;
            index = direction = -1;
            string value = (token ?? string.Empty).Trim().ToUpperInvariant();
            int axis = Array.IndexOf(AxisTokens, value);
            if (axis >= 0)
            {
                kind = "axis";
                index = axis;
                return true;
            }
            int number;
            if (value.StartsWith("JOY_BTN_POV", StringComparison.Ordinal))
            {
                string rest = value.Substring("JOY_BTN_POV".Length);
                int split = rest.IndexOf('_');
                if (split <= 0 || !int.TryParse(rest.Substring(0, split), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number < 1 || number > 4)
                    return false;
                direction = Array.IndexOf(HatDirections, rest.Substring(split + 1));
                if (direction < 0)
                    return false;
                kind = "hatdir";
                index = number - 1;
                return true;
            }
            if (value.StartsWith("JOY_BTN", StringComparison.Ordinal) && int.TryParse(value.Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 1 && number <= 128)
            {
                kind = "button";
                index = number - 1;
                return true;
            }
            if (value.StartsWith("POV", StringComparison.Ordinal) && int.TryParse(value.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 1 && number <= 4)
            {
                kind = "hat";
                index = number - 1;
                return true;
            }
            return false;
        }

        // A virtual output token: kind button|axis|hat (vJoy) | xbutton|xaxis|xdpad (Xbox pad) | key; index (key: scan
        // code), mask (xbutton).
        public static bool TryParseOutput(string token, out string kind, out int index, out int mask)
        {
            mask = 0;
            string value = (token ?? string.Empty).Trim().ToUpperInvariant();
            string inputKind;
            int direction;
            if (TryParseInput(value, out inputKind, out index, out direction) && inputKind != "hatdir")
            {
                kind = inputKind;
                return true;
            }
            index = Array.IndexOf(XboxAxes, value);
            if (index >= 0)
            {
                kind = "xaxis";
                return true;
            }
            if (value == "XB_DPAD")
            {
                kind = "xdpad";
                index = 0;
                return true;
            }
            index = Array.IndexOf(XboxButtonTokens, value);
            if (index >= 0)
            {
                kind = "xbutton";
                mask = XboxButtonMasks[index];
                return true;
            }
            int scan;
            if (TryKey(value, out scan))
            {
                kind = "key";
                index = scan;
                return true;
            }
            kind = string.Empty;
            index = -1;
            return false;
        }

        // The role class of an output kind (button, axis or hat).
        public static string OutputClass(string kind)
        {
            switch (kind)
            {
                case "axis":
                case "xaxis":
                    return "axis";
                case "hat":
                case "xdpad":
                    return "hat";
                default:
                    return "button";
            }
        }

        // Does an input kind (or an output's class, OutputClass) fit a role kind (axis|button|hat|any)? A hat direction
        // counts as a button input.
        public static bool Fits(string roleKind, string kind, bool input)
        {
            if (roleKind == "any")
                return true;
            if (roleKind == "button")
                return kind == "button" || (input && kind == "hatdir");
            return roleKind == kind;
        }

        public static string ButtonToken(int zeroBased)
        {
            return "JOY_BTN" + (zeroBased + 1).ToString(CultureInfo.InvariantCulture);
        }

        public static string HatToken(int zeroBased)
        {
            return "POV" + (zeroBased + 1).ToString(CultureInfo.InvariantCulture);
        }

        // "JOY_BTN30" -> "Button 30", "JOY_Z" -> "Z axis", "POV2" -> "Hat 2", "XB_A" -> "Xbox A", "KEY_F1" -> "Key F1".
        public static string Describe(string token)
        {
            string value = (token ?? string.Empty).Trim().ToUpperInvariant();
            string kind;
            int index;
            int mask;
            int direction;
            if (TryParseInput(value, out kind, out index, out direction))
            {
                switch (kind)
                {
                    case "axis": return AxisNames[index] + " axis";
                    case "button": return "Button " + (index + 1).ToString(CultureInfo.InvariantCulture);
                    case "hat": return "Hat " + (index + 1).ToString(CultureInfo.InvariantCulture);
                    default: return "Hat " + (index + 1).ToString(CultureInfo.InvariantCulture) + " " + HatDirections[direction];
                }
            }
            if (TryParseOutput(value, out kind, out index, out mask))
            {
                if (kind == "key")
                    return "Key " + value.Substring(4);
                return "Xbox " + value.Substring(3).Replace('_', ' ');
            }
            return value;
        }
    }

    // ---------------------------------------------------------------------------------------------- resolution

    public static class VfResolver
    {
        // Fills the resolved fields of the inputs (source index in sources, kind, index, direction) and outputs (index of
        // the vJoy device in vjoyDevices, kind, index, mask) for the forwarder. Problems are returned in plain words; an
        // input on a device that is not among the sources gets Source -1 (it reads neutral).
        public static List<string> Resolve(VirtualFunction function, IList<string> sources, IList<int> vjoyDevices)
        {
            List<string> problems = new List<string>();
            foreach (VfInput input in function.Inputs)
            {
                string kind;
                int index;
                int direction;
                input.Source = -1;
                input.Kind = string.Empty;
                input.Index = input.Direction = -1;
                if (input.Input.Length == 0 || input.Device.Length == 0)
                    continue;
                if (!VfTokens.TryParseInput(input.Input, out kind, out index, out direction))
                {
                    problems.Add(function.Name + ": " + input.Input + " is not a joystick input.");
                    continue;
                }
                input.Kind = kind;
                input.Index = index;
                input.Direction = direction;
                for (int s = 0; s < sources.Count; s++)
                {
                    if (string.Equals((sources[s] ?? string.Empty).Trim(), input.Device, StringComparison.OrdinalIgnoreCase))
                    {
                        input.Source = s;
                        break;
                    }
                }
                if (input.Source < 0)
                    problems.Add(function.Name + ": " + input.Device + " is not read by the forwarder.");
            }
            foreach (VfOutput output in function.Outputs)
            {
                string kind;
                int index;
                int mask;
                output.Kind = string.Empty;
                output.DeviceIndex = output.Index = -1;
                output.Mask = 0;
                if (output.Target.Length == 0)
                    continue;
                if (!VfTokens.TryParseOutput(output.Target, out kind, out index, out mask))
                {
                    problems.Add(function.Name + ": " + output.Target + " is not a virtual output.");
                    continue;
                }
                if (kind == "key" && !function.Keyboard)
                {
                    problems.Add(function.Name + ": " + output.Target + " is a key and keyboard output is off.");
                    continue;
                }
                output.Kind = kind;
                output.Index = index;
                output.Mask = mask;
                if (kind == "button" || kind == "axis" || kind == "hat")
                {
                    int device = output.VJoyDevice <= 0 ? 1 : output.VJoyDevice;
                    output.DeviceIndex = vjoyDevices == null ? -1 : vjoyDevices.IndexOf(device);
                    if (output.DeviceIndex < 0)
                    {
                        problems.Add(function.Name + ": vJoy device " + device.ToString(CultureInfo.InvariantCulture) + " is not written by the forwarder.");
                        output.Kind = string.Empty;
                    }
                }
            }
            return problems;
        }

        // The vJoy targets a function writes (device, kind button|axis|hat, 0-based index): what the planner reserves.
        public static List<KeyValuePair<int, string>> VJoyTargets(VirtualFunction function)
        {
            List<KeyValuePair<int, string>> targets = new List<KeyValuePair<int, string>>();
            foreach (VfOutput output in function.Outputs ?? new List<VfOutput>())
            {
                string kind;
                int index;
                int mask;
                if (output.Target.Length == 0 || !VfTokens.TryParseOutput(output.Target, out kind, out index, out mask))
                    continue;
                if (kind == "button" || kind == "axis" || kind == "hat")
                    targets.Add(new KeyValuePair<int, string>(output.VJoyDevice <= 0 ? 1 : output.VJoyDevice, output.Target));
            }
            return targets;
        }
    }
}
