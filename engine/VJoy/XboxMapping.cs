using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JoyPro.Engine.VJoy
{
    // The virtual Xbox 360 pad (ViGEmBus) as a virtual-mode target, for games that only read XInput or one device
    // (docs/virtual-joystick.md, "Virtual Xbox pad"). Its controls, as JoyPro tokens:
    //   axes:    XB_LX XB_LY XB_RX XB_RY (sticks, -32768..32767) and XB_LT XB_RT (triggers, 0..255)
    //   buttons: XB_A XB_B XB_X XB_Y XB_LB XB_RB XB_BACK XB_START XB_LS XB_RS XB_DPAD_UP/DOWN/LEFT/RIGHT (XB_GUIDE only
    //            when a game asks for it: Windows opens the Game Bar on it)
    //   hat:     XB_DPAD (a whole physical hat drives the D-pad, diagonals included)
    //   a button can also push a stick half (XB_LX+ / XB_LY- ...) or a trigger (pressed = full)
    internal static class XboxControls
    {
        public static readonly string[] Axes = { "XB_LX", "XB_LY", "XB_RX", "XB_RY", "XB_LT", "XB_RT" };
        public static readonly string[] Sticks = { "XB_LX", "XB_LY", "XB_RX", "XB_RY" };
        public static readonly string[] Triggers = { "XB_LT", "XB_RT" };
        public static readonly string[] FreeButtons = { "XB_A", "XB_B", "XB_X", "XB_Y", "XB_LB", "XB_RB", "XB_BACK", "XB_START", "XB_LS", "XB_RS", "XB_DPAD_UP", "XB_DPAD_DOWN", "XB_DPAD_LEFT", "XB_DPAD_RIGHT" };
        public static readonly string[] Buttons = FreeButtons.Concat(new[] { "XB_GUIDE" }).ToArray();
        public const string DPad = "XB_DPAD";
        // One physical axis on both triggers, split at its centre: above -> right trigger, below -> left trigger (games
        // that put one control on two trigger actions, e.g. a collective as Ascend = RT / Descend = LT).
        public const string Triggers2 = "XB_TRIGGERS";

        private static readonly Dictionary<string, string> UnrealKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Gamepad_LeftX", "XB_LX" }, { "Gamepad_LeftY", "XB_LY" }, { "Gamepad_RightX", "XB_RX" }, { "Gamepad_RightY", "XB_RY" },
            { "Gamepad_LeftTriggerAxis", "XB_LT" }, { "Gamepad_RightTriggerAxis", "XB_RT" },
            { "Gamepad_LeftTrigger", "XB_LT" }, { "Gamepad_RightTrigger", "XB_RT" },
            { "Gamepad_FaceButton_Bottom", "XB_A" }, { "Gamepad_FaceButton_Right", "XB_B" }, { "Gamepad_FaceButton_Left", "XB_X" }, { "Gamepad_FaceButton_Top", "XB_Y" },
            { "Gamepad_LeftShoulder", "XB_LB" }, { "Gamepad_RightShoulder", "XB_RB" },
            { "Gamepad_Special_Left", "XB_BACK" }, { "Gamepad_Special_Right", "XB_START" },
            { "Gamepad_LeftThumbstick", "XB_LS" }, { "Gamepad_RightThumbstick", "XB_RS" },
            { "Gamepad_DPad_Up", "XB_DPAD_UP" }, { "Gamepad_DPad_Down", "XB_DPAD_DOWN" }, { "Gamepad_DPad_Left", "XB_DPAD_LEFT" }, { "Gamepad_DPad_Right", "XB_DPAD_RIGHT" },
            { "Gamepad_LeftStick_Up", "XB_LY+" }, { "Gamepad_LeftStick_Down", "XB_LY-" }, { "Gamepad_LeftStick_Left", "XB_LX-" }, { "Gamepad_LeftStick_Right", "XB_LX+" },
            { "Gamepad_RightStick_Up", "XB_RY+" }, { "Gamepad_RightStick_Down", "XB_RY-" }, { "Gamepad_RightStick_Left", "XB_RX-" }, { "Gamepad_RightStick_Right", "XB_RX+" }
        };

        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "XB_LX", "Left stick X" }, { "XB_LY", "Left stick Y" }, { "XB_RX", "Right stick X" }, { "XB_RY", "Right stick Y" },
            { "XB_LT", "Left trigger" }, { "XB_RT", "Right trigger" }, { "XB_A", "A" }, { "XB_B", "B" }, { "XB_X", "X" }, { "XB_Y", "Y" },
            { "XB_LB", "LB" }, { "XB_RB", "RB" }, { "XB_BACK", "Back" }, { "XB_START", "Start" }, { "XB_LS", "Left stick click" }, { "XB_RS", "Right stick click" },
            { "XB_DPAD_UP", "D-pad up" }, { "XB_DPAD_DOWN", "D-pad down" }, { "XB_DPAD_LEFT", "D-pad left" }, { "XB_DPAD_RIGHT", "D-pad right" },
            { "XB_GUIDE", "Guide" }, { "XB_DPAD", "D-pad" }, { "XB_TRIGGERS", "Triggers (centre split: up = RT, down = LT)" }
        };

        // "Gamepad_LeftX" -> "XB_LX" ("" when it is not a gamepad key).
        public static string FromUnrealKey(string key)
        {
            string token;
            return UnrealKeys.TryGetValue((key ?? string.Empty).Trim(), out token) ? token : string.Empty;
        }

        public static string ToUnrealKey(string token, bool digitalTrigger)
        {
            string value = (token ?? string.Empty).Trim();
            if (digitalTrigger && value == "XB_LT")
                return "Gamepad_LeftTrigger";
            if (digitalTrigger && value == "XB_RT")
                return "Gamepad_RightTrigger";
            foreach (KeyValuePair<string, string> pair in UnrealKeys)
            {
                if (string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase) && !pair.Key.EndsWith("Trigger", StringComparison.Ordinal))
                    return pair.Key;
            }
            return string.Empty;
        }

        // "XB_LX~": the axis reversed (the relation's Invert); the control itself is "XB_LX".
        public static string Control(string token)
        {
            string value = (token ?? string.Empty).Trim();
            return value.EndsWith("~", StringComparison.Ordinal) ? value.Substring(0, value.Length - 1) : value;
        }

        public static bool IsInverted(string token)
        {
            return (token ?? string.Empty).Trim().EndsWith("~", StringComparison.Ordinal);
        }

        public static string Describe(string token)
        {
            bool inverted = IsInverted(token);
            string text = DescribeControl(Control(token));
            return inverted ? text + " (reversed)" : text;
        }

        private static string DescribeControl(string token)
        {
            string value = (token ?? string.Empty).Trim().ToUpperInvariant();
            string half = value.EndsWith("+", StringComparison.Ordinal) ? " +" : value.EndsWith("-", StringComparison.Ordinal) ? " -" : string.Empty;
            string name;
            return Names.TryGetValue(half.Length > 0 ? value.Substring(0, value.Length - 1) : value, out name) ? name + half : value;
        }

        public static bool IsAxis(string token)
        {
            string value = (token ?? string.Empty).Trim();
            return Axes.Contains(value, StringComparer.OrdinalIgnoreCase) || string.Equals(value, Triggers2, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsTrigger(string token)
        {
            return Triggers.Contains((token ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsButton(string token)
        {
            return Buttons.Contains((token ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);
        }

        // "XB_LY+" -> axis XB_LY, direction +1.
        public static bool IsHalf(string token, out string axis, out int direction)
        {
            string value = (token ?? string.Empty).Trim().ToUpperInvariant();
            axis = string.Empty;
            direction = 0;
            if (value.Length < 2 || (value[value.Length - 1] != '+' && value[value.Length - 1] != '-'))
                return false;
            axis = value.Substring(0, value.Length - 1);
            direction = value[value.Length - 1] == '+' ? 1 : -1;
            return Sticks.Contains(axis);
        }

        public static bool IsValid(string token)
        {
            string axis;
            int direction;
            return IsAxis(token) || IsButton(token) || string.Equals(token, DPad, StringComparison.OrdinalIgnoreCase) || IsHalf(token, out axis, out direction);
        }

        // Can a physical input of this kind drive the control?
        public static bool Fits(VJoyInputKind kind, string token)
        {
            token = Control(token);
            string axis;
            int direction;
            switch (kind)
            {
                case VJoyInputKind.Axis:
                    return IsAxis(token);
                case VJoyInputKind.Pov:
                    return string.Equals(token, DPad, StringComparison.OrdinalIgnoreCase);
                default:
                    return IsButton(token) || IsTrigger(token) || IsHalf(token, out axis, out direction);
            }
        }
    }

    // One physical input an Xbox-target game uses, and the control the game's own layout wants for it (if known).
    internal sealed class XboxUse
    {
        public string Source { get; set; }
        public string Input { get; set; }
        public List<string> Relations { get; set; }
        public List<string> Actions { get; set; }
        public string Pinned { get; set; }
        public string PinnedAction { get; set; }
        // The relation's Invert: the forwarder reverses the axis.
        public bool Invert { get; set; }

        public XboxUse()
        {
            Source = Input = Pinned = PinnedAction = string.Empty;
            Relations = new List<string>();
            Actions = new List<string>();
        }
    }

    internal sealed class XboxPlan
    {
        public string Game { get; set; }
        public List<VJoySlot> Slots { get; set; }
        public List<VJoySlot> Added { get; set; }
        public List<VJoySlot> Removed { get; set; }
        // Inputs that found no control, in plain words.
        public List<string> Unfit { get; set; }
        public List<string> Warnings { get; set; }

        public XboxPlan()
        {
            Game = string.Empty;
            Slots = new List<VJoySlot>();
            Added = new List<VJoySlot>();
            Removed = new List<VJoySlot>();
            Unfit = new List<string>();
            Warnings = new List<string>();
        }

        public bool Changed
        {
            get { return Added.Count > 0 || Removed.Count > 0; }
        }
    }

    // Per game: physical input -> Xbox control. The game's own gamepad layout (when JoyPro knows it, e.g. the default
    // layout read from the game files) pins an input to the control its action uses; the rest is given in a fixed order
    // (axes: left stick, right stick, triggers; buttons: A B X Y LB RB Back Start LS RS, then the D-pad; a hat: the
    // D-pad). Slots stay where they are; a pinned control wins over an old free slot.
    internal static class XboxPlanner
    {
        public static XboxPlan Plan(string game, List<VJoySlot> existing, List<XboxUse> used, bool reset, bool prune)
        {
            XboxPlan plan = new XboxPlan { Game = game };
            Dictionary<string, XboxUse> byKey = used.ToDictionary(u => VJoyPlanner.Key(u.Source, u.Input), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> takenBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Pinned controls: what the game's layout asks for.
            foreach (XboxUse use in used.Where(u => u.Pinned.Length > 0).OrderBy(u => u.Source, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.Input, StringComparer.OrdinalIgnoreCase))
            {
                VJoyInput input;
                if (!VJoyInput.TryParseSlotToken(use.Input, out input))
                    continue;
                string key = VJoyPlanner.Key(use.Source, use.Input);
                if (!XboxControls.Fits(input.Kind, use.Pinned))
                {
                    plan.Warnings.Add(Name(use) + " is a " + VJoyPlanner.KindName(input.Kind) + ", but the game's action " + use.PinnedAction + " sits on " +
                                      XboxControls.Describe(use.Pinned) + "; it gets a free control instead.");
                    continue;
                }
                string other;
                if (takenBy.TryGetValue(use.Pinned, out other) && input.Kind == VJoyInputKind.Axis)
                    plan.Warnings.Add(Name(use) + " and " + other + " both drive " + XboxControls.Describe(use.Pinned) + " (the action " + use.PinnedAction + "); the one moved last wins.");
                takenBy[use.Pinned] = Name(use);
                if (string.Equals(use.Pinned, XboxControls.Triggers2, StringComparison.OrdinalIgnoreCase))
                {
                    takenBy["XB_LT"] = Name(use);
                    takenBy["XB_RT"] = Name(use);
                }
                assigned.Add(key);
                plan.Slots.Add(new VJoySlot { Source = use.Source, Input = use.Input, Target = use.Pinned });
            }

            // 2. Existing free slots that are still valid.
            foreach (VJoySlot slot in reset ? new List<VJoySlot>() : existing)
            {
                string key = VJoyPlanner.Key(slot.Source, slot.Input);
                VJoyInput input;
                bool valid = VJoyInput.TryParseSlotToken(slot.Input, out input) && XboxControls.Fits(input.Kind, slot.Target) && !assigned.Contains(key) && !takenBy.ContainsKey(XboxControls.Control(slot.Target));
                if (!valid || (prune && !byKey.ContainsKey(key)))
                    continue;
                takenBy[XboxControls.Control(slot.Target)] = slot.Source + " " + slot.Input;
                assigned.Add(key);
                plan.Slots.Add(new VJoySlot { Source = slot.Source, Input = slot.Input, Target = slot.Target });
            }

            // 3. Free controls for the rest.
            foreach (XboxUse use in used.Where(u => !assigned.Contains(VJoyPlanner.Key(u.Source, u.Input)))
                         .OrderBy(u => u.Source, StringComparer.OrdinalIgnoreCase).ThenBy(u => OrderOf(u.Input)))
            {
                VJoyInput input;
                if (!VJoyInput.TryParseSlotToken(use.Input, out input))
                    continue;
                string target = null;
                if (input.Kind == VJoyInputKind.Axis)
                    target = XboxControls.Axes.FirstOrDefault(a => !takenBy.ContainsKey(a));
                else if (input.Kind == VJoyInputKind.Pov)
                    target = !takenBy.ContainsKey(XboxControls.DPad) && !takenBy.Keys.Any(k => k.StartsWith("XB_DPAD_", StringComparison.OrdinalIgnoreCase)) ? XboxControls.DPad : null;
                else
                {
                    target = XboxControls.FreeButtons.FirstOrDefault(b => !takenBy.ContainsKey(b) && !(b.StartsWith("XB_DPAD_", StringComparison.Ordinal) && takenBy.ContainsKey(XboxControls.DPad)));
                    if (target == null)
                        target = XboxControls.Triggers.FirstOrDefault(t => !takenBy.ContainsKey(t));
                }
                if (target == null)
                {
                    plan.Unfit.Add(Name(use) + " (" + string.Join(", ", use.Relations) + ") does not fit: every Xbox " +
                                   (input.Kind == VJoyInputKind.Axis ? "axis (2 sticks, 2 triggers)" : input.Kind == VJoyInputKind.Pov ? "D-pad" : "button (14 and the triggers)") + " is taken.");
                    continue;
                }
                takenBy[target] = Name(use);
                plan.Slots.Add(new VJoySlot { Source = use.Source, Input = use.Input, Target = target });
            }

            // The relation's Invert decides the "~" of an axis slot (not a move: the control stays).
            foreach (VJoySlot slot in plan.Slots)
            {
                XboxUse use;
                VJoyInput input;
                if (!byKey.TryGetValue(VJoyPlanner.Key(slot.Source, slot.Input), out use) || !VJoyInput.TryParseSlotToken(slot.Input, out input) || input.Kind != VJoyInputKind.Axis)
                    continue;
                slot.Target = XboxControls.Control(slot.Target) + (use.Invert ? "~" : string.Empty);
            }

            Func<VJoySlot, VJoySlot, bool> same = (a, b) => VJoyPlanner.Key(a.Source, a.Input) == VJoyPlanner.Key(b.Source, b.Input) && string.Equals(a.Target, b.Target, StringComparison.OrdinalIgnoreCase);
            plan.Added = plan.Slots.Where(s => !existing.Any(e => same(e, s))).ToList();
            plan.Removed = existing.Where(e => !plan.Slots.Any(s => same(e, s))).ToList();
            plan.Slots = plan.Slots.OrderBy(s => Array.IndexOf(XboxControls.Axes, s.Target) < 0 ? 1 : 0).ThenBy(s => s.Target, StringComparer.OrdinalIgnoreCase).ToList();
            return plan;
        }

        private static string Name(XboxUse use)
        {
            return use.Source + " " + use.Input;
        }

        private static int OrderOf(string token)
        {
            VJoyInput input;
            return VJoyInput.TryParseSlotToken(token, out input) ? (int)input.Kind * 1000 + input.Index : int.MaxValue;
        }
    }
}
