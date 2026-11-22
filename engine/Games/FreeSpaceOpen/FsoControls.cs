using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JoyPro.Engine.Games.FreeSpaceOpen
{
    // FreeSpace Open's controls as the engine defines them (code/controlconfig/controlsconfig.h IoActionId and
    // controlsconfigcommon.cpp control_config_common_init_bindings, release 26.0.1): the action's number is its place in
    // the pilot's and preset's lists (CCFG_MAX = 140), the name is what preset files and controlconfigdefaults.tbl use.
    // Primary / secondary are the hardcoded "default" preset as FSO builds it: a digital control gets its key on the
    // primary and its Joy-0 button on the secondary, an axis control its Joy-0 axis on the primary and a mouse axis on the
    // secondary. A mod changes texts, tabs, defaults and the disabled flag with #ControlConfigOverride (FsoTables).
    internal sealed class FsoControl
    {
        public int Id;
        public string Name;
        // T targeting, S ship, W weapons, C computer (misc), N no tab (hidden).
        public char Tab;
        // 0 no translation, 1 = XSTR id Id + 507 (CONTROL_CONFIG_XSTR), else the XSTR id itself.
        public int Xstr;
        public string Text;
        // trigger, continuous, abs (absolute axis), rel (relative axis).
        public string Type;
        public bool Disabled;
        public FsoBind Primary;
        public FsoBind Secondary;

        public bool IsAxis
        {
            get { return Type == "abs" || Type == "rel"; }
        }

        public FsoControl Copy()
        {
            return new FsoControl { Id = Id, Name = Name, Tab = Tab, Xstr = Xstr, Text = Text, Type = Type, Disabled = Disabled, Primary = Primary, Secondary = Secondary };
        }

        // The XSTR id the controls menu translates the text with (controlsconfig.cpp: > 1 the id, 1 the id + 507).
        public int XstrId
        {
            get { return Xstr > 1 ? Xstr : Xstr == 1 ? Id + 507 : 0; }
        }
    }

    // One input of a control, as a preset file writes it: "cid" (CID_NONE, CID_KEYBOARD, CID_MOUSE, CID_JOY0..3), "flags"
    // (NONE, or AXIS_BTN, RELATIVE, INVERTED, AXIS, HAT, BALL joined by ", " in that order) and "input" (KEY_T with ALT- /
    // SHIFT- in front, a mouse button name, a joystick button number 0..131 - 128..131 are the first hat - or an axis name
    // X_AXIS .. V_AXIS).
    internal struct FsoBind
    {
        public string Cid;
        public string Flags;
        public string Input;

        public static readonly FsoBind None = new FsoBind { Cid = "CID_NONE", Flags = "NONE", Input = "NONE" };

        public static FsoBind Make(string cid, string flags, string input)
        {
            FsoBind bind = new FsoBind { Cid = (cid ?? string.Empty).Trim(), Flags = FsoNames.NormalizeFlags(flags), Input = (input ?? string.Empty).Trim() };
            return bind.Validated();
        }

        // FSO's CC_bind::validate: no device or no input = unbound; a key has no flags.
        public FsoBind Validated()
        {
            if (!FsoNames.Cids.Contains(Cid ?? string.Empty) || Cid == "CID_NONE" || string.IsNullOrEmpty(Input) || Input == "NONE" || Input == "-1")
                return None;
            if (Cid == "CID_KEYBOARD")
                return new FsoBind { Cid = Cid, Flags = "NONE", Input = Input };
            return new FsoBind { Cid = Cid, Flags = string.IsNullOrEmpty(Flags) ? "NONE" : Flags, Input = Input };
        }

        public bool IsNone
        {
            get { return string.IsNullOrEmpty(Cid) || Cid == "CID_NONE"; }
        }

        public bool IsKeyboard
        {
            get { return Cid == "CID_KEYBOARD"; }
        }

        public bool IsMouse
        {
            get { return Cid == "CID_MOUSE"; }
        }

        // 0..3 for CID_JOY0..3, else -1.
        public int Joy
        {
            get { return Cid != null && Cid.Length == 8 && Cid.StartsWith("CID_JOY", StringComparison.Ordinal) && Cid[7] >= '0' && Cid[7] <= '3' ? Cid[7] - '0' : -1; }
        }

        public bool IsAxis
        {
            get { return FsoNames.HasFlag(Flags, "AXIS"); }
        }

        public bool Inverted
        {
            get { return FsoNames.HasFlag(Flags, "INVERTED"); }
        }

        public bool Same(FsoBind other)
        {
            return string.Equals(Cid ?? "CID_NONE", other.Cid ?? "CID_NONE", StringComparison.Ordinal) && string.Equals(Flags ?? "NONE", other.Flags ?? "NONE", StringComparison.Ordinal) &&
                   string.Equals(Input ?? "NONE", other.Input ?? "NONE", StringComparison.Ordinal);
        }

        // The same input of the same device, the invert flag aside (FSO's invert_agnostic_equals).
        public bool SameInput(FsoBind other)
        {
            return !IsNone && string.Equals(Cid, other.Cid, StringComparison.Ordinal) && string.Equals(Input, other.Input, StringComparison.Ordinal) && IsAxis == other.IsAxis;
        }

        public override string ToString()
        {
            return IsNone ? "none" : Cid + " " + Input + (Flags == "NONE" ? string.Empty : " (" + Flags + ")");
        }
    }

    internal static class FsoNames
    {
        public const int ControlCount = 140;
        public const int JoyButtons = 128;
        public const int Joysticks = 4;
        public static readonly HashSet<string> Cids = new HashSet<string>(StringComparer.Ordinal) { "CID_NONE", "CID_KEYBOARD", "CID_MOUSE", "CID_JOY0", "CID_JOY1", "CID_JOY2", "CID_JOY3" };
        // ValToCCF order.
        private static readonly string[] FlagOrder = { "AXIS_BTN", "RELATIVE", "INVERTED", "AXIS", "HAT", "BALL" };
        // JOY_X_AXIS .. JOY_V_AXIS: the device's present axes in SDL's order (X, Y, Z, Rx, Ry, Rz, Slider, Slider).
        public static readonly string[] Axes = { "X_AXIS", "Y_AXIS", "Z_AXIS", "RX_AXIS", "RY_AXIS", "RZ_AXIS", "U_AXIS", "V_AXIS" };
        // The first hat as buttons 128..131 (joy-sdl.cpp hatBtnToEnum): 128 down (Hat Back), 129 up (Hat Forward), 130 left,
        // 131 right. Index: JoyPro's hat direction 0 up .. 7 up-left; diagonals have none.
        public static readonly int[] HatButtonByDirection = { 129, -1, 131, -1, 128, -1, 130, -1 };

        public static bool HasFlag(string flags, string flag)
        {
            return (flags ?? string.Empty).Split(',').Select(f => f.Trim()).Any(f => string.Equals(f, flag, StringComparison.Ordinal));
        }

        // CCFToVal + ValToCCF: the flags FSO keeps, in its order ("NONE" when none). CCFToVal looks for substrings, so
        // "AXIS_BTN" also sets AXIS.
        public static string NormalizeFlags(string flags)
        {
            string text = (flags ?? string.Empty).ToUpperInvariant();
            HashSet<string> found = new HashSet<string>(StringComparer.Ordinal);
            foreach (string flag in FlagOrder)
            {
                if (text.Contains(flag))
                    found.Add(flag);
            }
            List<string> ordered = FlagOrder.Where(found.Contains).ToList();
            return ordered.Count == 0 ? "NONE" : string.Join(", ", ordered);
        }

        public static string WithFlag(string flags, string flag, bool on)
        {
            List<string> list = (flags ?? string.Empty).Split(',').Select(f => f.Trim()).Where(f => f.Length > 0 && f != "NONE" && f != flag).ToList();
            if (on)
                list.Add(flag);
            return NormalizeFlags(string.Join(", ", list));
        }

        public static string TabTitle(char tab)
        {
            switch (tab)
            {
                case 'T': return "Targeting";
                case 'S': return "Ship";
                case 'W': return "Weapons";
                case 'C': return "Computer";
                default: return "Other";
            }
        }

        // 0 when the name is no tab.
        public static char TabFromName(string name)
        {
            switch ((name ?? string.Empty).Trim())
            {
                case "TARGET_TAB": return 'T';
                case "SHIP_TAB": return 'S';
                case "WEAPON_TAB": return 'W';
                case "COMPUTER_TAB": return 'C';
                case "NO_TAB": return 'N';
                default: return (char)0;
            }
        }

        public static string TypeFromName(string name)
        {
            switch ((name ?? string.Empty).Trim())
            {
                case "CC_TYPE_TRIGGER": return "trigger";
                case "CC_TYPE_CONTINUOUS": return "continuous";
                case "CC_TYPE_AXIS_ABS": return "abs";
                case "CC_TYPE_AXIS_REL": return "rel";
                case "CC_TYPE_AXIS_BTN_NEG": return "btnneg";
                case "CC_TYPE_AXIS_BTN_POS": return "btnpos";
                default: return string.Empty;
            }
        }

        // FSO's key names (LoadEnumsIntoKeyMap) = DirectInput scan codes (io/key.h).
        public static readonly Dictionary<string, int> Keys = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "KEY_0", 0x0B }, { "KEY_1", 0x02 }, { "KEY_2", 0x03 }, { "KEY_3", 0x04 }, { "KEY_4", 0x05 }, { "KEY_5", 0x06 }, { "KEY_6", 0x07 },
            { "KEY_7", 0x08 }, { "KEY_8", 0x09 }, { "KEY_9", 0x0A },
            { "KEY_A", 0x1E }, { "KEY_B", 0x30 }, { "KEY_C", 0x2E }, { "KEY_D", 0x20 }, { "KEY_E", 0x12 }, { "KEY_F", 0x21 }, { "KEY_G", 0x22 },
            { "KEY_H", 0x23 }, { "KEY_I", 0x17 }, { "KEY_J", 0x24 }, { "KEY_K", 0x25 }, { "KEY_L", 0x26 }, { "KEY_M", 0x32 }, { "KEY_N", 0x31 },
            { "KEY_O", 0x18 }, { "KEY_P", 0x19 }, { "KEY_Q", 0x10 }, { "KEY_R", 0x13 }, { "KEY_S", 0x1F }, { "KEY_T", 0x14 }, { "KEY_U", 0x16 },
            { "KEY_V", 0x2F }, { "KEY_W", 0x11 }, { "KEY_X", 0x2D }, { "KEY_Y", 0x15 }, { "KEY_Z", 0x2C },
            { "KEY_MINUS", 0x0C }, { "KEY_EQUAL", 0x0D }, { "KEY_DIVIDE", 0x35 }, { "KEY_SLASH", 0x2B }, { "KEY_SLASH_UK", 0x56 }, { "KEY_COMMA", 0x33 },
            { "KEY_PERIOD", 0x34 }, { "KEY_SEMICOL", 0x27 }, { "KEY_LBRACKET", 0x1A }, { "KEY_RBRACKET", 0x1B }, { "KEY_RAPOSTRO", 0x28 }, { "KEY_LAPOSTRO", 0x29 },
            { "KEY_ESC", 0x01 }, { "KEY_ENTER", 0x1C }, { "KEY_BACKSP", 0x0E }, { "KEY_TAB", 0x0F }, { "KEY_SPACEBAR", 0x39 },
            { "KEY_NUMLOCK", 0x45 }, { "KEY_SCROLLOCK", 0x46 }, { "KEY_CAPSLOCK", 0x3A }, { "KEY_LSHIFT", 0x2A }, { "KEY_RSHIFT", 0x36 },
            { "KEY_LALT", 0x38 }, { "KEY_RALT", 0xB8 }, { "KEY_LCTRL", 0x1D }, { "KEY_RCTRL", 0x9D },
            { "KEY_F1", 0x3B }, { "KEY_F2", 0x3C }, { "KEY_F3", 0x3D }, { "KEY_F4", 0x3E }, { "KEY_F5", 0x3F }, { "KEY_F6", 0x40 }, { "KEY_F7", 0x41 },
            { "KEY_F8", 0x42 }, { "KEY_F9", 0x43 }, { "KEY_F10", 0x44 }, { "KEY_F11", 0x57 }, { "KEY_F12", 0x58 },
            { "KEY_PAD0", 0x52 }, { "KEY_PAD1", 0x4F }, { "KEY_PAD2", 0x50 }, { "KEY_PAD3", 0x51 }, { "KEY_PAD4", 0x4B }, { "KEY_PAD5", 0x4C },
            { "KEY_PAD6", 0x4D }, { "KEY_PAD7", 0x47 }, { "KEY_PAD8", 0x48 }, { "KEY_PAD9", 0x49 }, { "KEY_PADMINUS", 0x4A }, { "KEY_PADPLUS", 0x4E },
            { "KEY_PADPERIOD", 0x53 }, { "KEY_PADDIVIDE", 0xB5 }, { "KEY_PADMULTIPLY", 0x37 }, { "KEY_PADENTER", 0x9C },
            { "KEY_INSERT", 0xD2 }, { "KEY_HOME", 0xC7 }, { "KEY_PAGEUP", 0xC9 }, { "KEY_DELETE", 0xD3 }, { "KEY_END", 0xCF }, { "KEY_PAGEDOWN", 0xD1 },
            { "KEY_UP", 0xC8 }, { "KEY_DOWN", 0xD0 }, { "KEY_LEFT", 0xCB }, { "KEY_RIGHT", 0xCD },
            { "KEY_PRINT_SCRN", 0xB7 }, { "KEY_PAUSE", 0x45 }, { "KEY_BREAK", 0xC6 }
        };

        // ValToKeyboard picks the first name of the scan code in its (sorted) map: KEY_NUMLOCK before KEY_PAUSE.
        public static string KeyName(int dik)
        {
            return Keys.Where(k => k.Value == dik).Select(k => k.Key).OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault() ?? string.Empty;
        }

        // "ALT-SHIFT-KEY_T" -> scan code, alt, shift (KeyboardToVal: ALT- first, then SHIFT-).
        public static bool TryParseKey(string input, out int dik, out bool alt, out bool shift)
        {
            string rest = (input ?? string.Empty).Trim();
            alt = false;
            shift = false;
            if (rest.StartsWith("ALT-", StringComparison.Ordinal))
            {
                alt = true;
                rest = rest.Substring(4);
            }
            if (rest.StartsWith("SHIFT-", StringComparison.Ordinal))
            {
                shift = true;
                rest = rest.Substring(6);
            }
            return Keys.TryGetValue(rest, out dik);
        }

        public static string KeyInput(int dik, bool alt, bool shift)
        {
            string name = KeyName(dik);
            return name.Length == 0 ? string.Empty : (alt ? "ALT-" : string.Empty) + (shift ? "SHIFT-" : string.Empty) + name;
        }

        public static string JoyCid(int slot)
        {
            return "CID_JOY" + slot.ToString(CultureInfo.InvariantCulture);
        }
    }

    internal static class FsoControls
    {
        private static FsoBind B(string spec)
        {
            // "KEY:SHIFT-KEY_T", "JOY0:NONE:5", "JOY0:AXIS:X_AXIS", "MOUSE:AXIS:X_AXIS"
            if (string.IsNullOrEmpty(spec))
                return FsoBind.None;
            string[] parts = spec.Split(':');
            if (parts[0] == "KEY")
                return FsoBind.Make("CID_KEYBOARD", "NONE", parts[1]);
            return FsoBind.Make(parts[0] == "MOUSE" ? "CID_MOUSE" : "CID_" + parts[0], parts[1], parts[2]);
        }

        private static FsoControl R(int id, string name, char tab, int xstr, string text, string type, bool disabled, string primary, string secondary)
        {
            return new FsoControl { Id = id, Name = name, Tab = tab, Xstr = xstr, Text = text, Type = type, Disabled = disabled, Primary = B(primary), Secondary = B(secondary) };
        }

        // In IoActionId order (index = id). A control without a tab is disabled (CCI_builder).
        public static readonly IReadOnlyList<FsoControl> Defaults = new List<FsoControl>
        {
            R(0, "TARGET_NEXT", 'T', 1, "Target Next Ship", "trigger", false, "KEY:KEY_T", ""),
            R(1, "TARGET_PREV", 'T', 1, "Target Previous Ship", "trigger", false, "KEY:SHIFT-KEY_T", ""),
            R(2, "TARGET_NEXT_CLOSEST_HOSTILE", 'T', 1, "Target Next Closest Hostile Ship", "trigger", false, "KEY:KEY_H", "JOY0:NONE:2"),
            R(3, "TARGET_PREV_CLOSEST_HOSTILE", 'T', 1, "Target Previous Closest Hostile Ship", "trigger", false, "KEY:SHIFT-KEY_H", ""),
            R(4, "TOGGLE_AUTO_TARGETING", 'T', 1, "Toggle Auto Targeting", "trigger", false, "KEY:ALT-KEY_H", ""),
            R(5, "TARGET_NEXT_CLOSEST_FRIENDLY", 'T', 1, "Target Next Closest Friendly Ship", "trigger", false, "KEY:KEY_F", ""),
            R(6, "TARGET_PREV_CLOSEST_FRIENDLY", 'T', 1, "Target Previous Closest Friendly Ship", "trigger", false, "KEY:SHIFT-KEY_F", ""),
            R(7, "TARGET_SHIP_IN_RETICLE", 'T', 1, "Target Ship in Reticle", "trigger", false, "KEY:KEY_Y", "JOY0:NONE:4"),
            R(8, "TARGET_CLOSEST_SHIP_ATTACKING_TARGET", 'T', 1, "Target Target's Nearest Attacker", "trigger", false, "KEY:KEY_G", ""),
            R(9, "TARGET_LAST_TRANMISSION_SENDER", 'T', 1, "Target Last Ship to Send Transmission", "trigger", false, "KEY:ALT-KEY_Y", ""),
            R(10, "STOP_TARGETING_SHIP", 'T', 1, "Turn Off Targeting", "trigger", false, "KEY:ALT-KEY_T", ""),
            R(11, "TARGET_SUBOBJECT_IN_RETICLE", 'T', 1, "Target Subsystem in Reticle", "trigger", false, "KEY:KEY_V", ""),
            R(12, "TARGET_NEXT_SUBOBJECT", 'T', 1, "Target Next Subsystem", "trigger", false, "KEY:KEY_S", ""),
            R(13, "TARGET_PREV_SUBOBJECT", 'T', 1, "Target Previous Subsystem", "trigger", false, "KEY:SHIFT-KEY_S", ""),
            R(14, "STOP_TARGETING_SUBSYSTEM", 'T', 1, "Turn Off Targeting of Subsystems", "trigger", false, "KEY:ALT-KEY_S", ""),
            R(15, "MATCH_TARGET_SPEED", 'C', 1, "Match Target Speed", "trigger", false, "KEY:KEY_M", ""),
            R(16, "TOGGLE_AUTO_MATCH_TARGET_SPEED", 'C', 1, "Toggle Auto Speed Matching", "trigger", false, "KEY:ALT-KEY_M", ""),
            R(17, "FIRE_PRIMARY", 'W', 1, "Fire Primary Weapon", "continuous", false, "KEY:KEY_LCTRL", "JOY0:NONE:0"),
            R(18, "FIRE_SECONDARY", 'W', 1, "Fire Secondary Weapon", "continuous", false, "KEY:KEY_SPACEBAR", "JOY0:NONE:1"),
            R(19, "CYCLE_NEXT_PRIMARY", 'W', 1, "Cycle Primary Weapon Forward", "trigger", false, "KEY:KEY_PERIOD", ""),
            R(20, "CYCLE_PREV_PRIMARY", 'W', 1, "Cycle Primary Weapon Backward", "trigger", false, "KEY:KEY_COMMA", ""),
            R(21, "CYCLE_SECONDARY", 'W', 1, "Cycle Secondary Weapon Forward", "trigger", false, "KEY:KEY_DIVIDE", ""),
            R(22, "CYCLE_NUM_MISSLES", 'W', 1, "Cycle Secondary Weapon Firing Rate", "trigger", false, "KEY:SHIFT-KEY_DIVIDE", ""),
            R(23, "LAUNCH_COUNTERMEASURE", 'W', 1, "Launch Countermeasure", "trigger", false, "KEY:KEY_X", "JOY0:NONE:3"),
            R(24, "FORWARD_THRUST", 'S', 1, "Forward Thrust", "continuous", false, "KEY:KEY_A", ""),
            R(25, "REVERSE_THRUST", 'S', 1, "Reverse Thrust", "continuous", false, "KEY:KEY_Z", ""),
            R(26, "BANK_LEFT", 'S', 1, "Bank Left", "continuous", false, "KEY:KEY_PAD7", ""),
            R(27, "BANK_RIGHT", 'S', 1, "Bank Right", "continuous", false, "KEY:KEY_PAD9", ""),
            R(28, "PITCH_FORWARD", 'S', 1, "Pitch Forward", "continuous", false, "KEY:KEY_PAD8", ""),
            R(29, "PITCH_BACK", 'S', 1, "Pitch Backward", "continuous", false, "KEY:KEY_PAD2", ""),
            R(30, "YAW_LEFT", 'S', 1, "Yaw Left", "continuous", false, "KEY:KEY_PAD4", ""),
            R(31, "YAW_RIGHT", 'S', 1, "Yaw Right", "continuous", false, "KEY:KEY_PAD6", ""),
            R(32, "ZERO_THROTTLE", 'S', 1, "Set Throttle to Zero", "trigger", false, "KEY:KEY_BACKSP", ""),
            R(33, "MAX_THROTTLE", 'S', 1, "Set Throttle to Max", "trigger", false, "KEY:KEY_SLASH", ""),
            R(34, "ONE_THIRD_THROTTLE", 'S', 1, "Set Throttle to One-Third", "trigger", false, "KEY:KEY_LBRACKET", ""),
            R(35, "TWO_THIRDS_THROTTLE", 'S', 1, "Set Throttle to Two-Thirds", "trigger", false, "KEY:KEY_RBRACKET", ""),
            R(36, "PLUS_5_PERCENT_THROTTLE", 'S', 1, "Increase Throttle 5 Percent", "trigger", false, "KEY:KEY_EQUAL", ""),
            R(37, "MINUS_5_PERCENT_THROTTLE", 'S', 1, "Decrease Throttle 5 Percent", "trigger", false, "KEY:KEY_MINUS", ""),
            R(38, "ATTACK_MESSAGE", 'C', 1, "(Squadmate) Attack My Target", "trigger", false, "KEY:SHIFT-KEY_A", ""),
            R(39, "DISARM_MESSAGE", 'C', 1, "(Squadmate) Disarm My Target", "trigger", false, "KEY:SHIFT-KEY_Z", ""),
            R(40, "DISABLE_MESSAGE", 'C', 1, "(Squadmate) Disable My Target", "trigger", false, "KEY:SHIFT-KEY_D", ""),
            R(41, "ATTACK_SUBSYSTEM_MESSAGE", 'C', 1, "(Squadmate) Attack My Subsystem", "trigger", false, "KEY:SHIFT-KEY_V", ""),
            R(42, "CAPTURE_MESSAGE", 'C', 1, "(Squadmate) Capture My Target", "trigger", false, "KEY:SHIFT-KEY_X", ""),
            R(43, "ENGAGE_MESSAGE", 'C', 1, "(Squadmate) Engage Enemy", "trigger", false, "KEY:SHIFT-KEY_E", ""),
            R(44, "FORM_MESSAGE", 'C', 1, "(Squadmate) Form on My Wing", "trigger", false, "KEY:SHIFT-KEY_W", ""),
            R(45, "IGNORE_MESSAGE", 'C', 1, "(Squadmate) Ignore My Target", "trigger", false, "KEY:SHIFT-KEY_I", ""),
            R(46, "PROTECT_MESSAGE", 'C', 1, "(Squadmate) Protect My Target", "trigger", false, "KEY:SHIFT-KEY_P", ""),
            R(47, "COVER_MESSAGE", 'C', 1, "(Squadmate) Cover Me", "trigger", false, "KEY:SHIFT-KEY_C", ""),
            R(48, "WARP_MESSAGE", 'C', 1, "(Squadmate) Return to Base", "trigger", false, "KEY:SHIFT-KEY_J", ""),
            R(49, "REARM_MESSAGE", 'C', 1, "(Squadmate) Rearm Me", "trigger", false, "KEY:SHIFT-KEY_R", ""),
            R(50, "TARGET_CLOSEST_SHIP_ATTACKING_SELF", 'T', 1, "Target Closest Attacking Ship", "trigger", false, "KEY:KEY_R", "JOY0:NONE:6"),
            R(51, "VIEW_CHASE", 'C', 1, "Chase View", "trigger", false, "KEY:KEY_PADMULTIPLY", ""),
            R(52, "VIEW_EXTERNAL", 'C', 1, "External View", "trigger", false, "KEY:KEY_PADPERIOD", ""),
            R(53, "VIEW_EXTERNAL_TOGGLE_CAMERA_LOCK", 'C', 1, "Toggle External Camera Lock", "trigger", false, "KEY:KEY_PADENTER", ""),
            R(54, "VIEW_SLEW", 'C', 1, "Free-Look View", "continuous", false, "KEY:KEY_PAD0", ""),
            R(55, "VIEW_OTHER_SHIP", 'C', 1, "Current Target View", "trigger", false, "KEY:KEY_PADDIVIDE", ""),
            R(56, "VIEW_DIST_INCREASE", 'C', 1, "Increase View Distance", "continuous", false, "KEY:KEY_PADPLUS", ""),
            R(57, "VIEW_DIST_DECREASE", 'C', 1, "Decrease View Distance", "continuous", false, "KEY:KEY_PADMINUS", ""),
            R(58, "VIEW_CENTER", 'C', 1, "Center View", "continuous", false, "KEY:KEY_PAD5", ""),
            R(59, "PADLOCK_UP", 'C', 1, "View Up", "continuous", false, "", "JOY0:NONE:128"),
            R(60, "PADLOCK_DOWN", 'C', 1, "View Rear", "continuous", false, "", "JOY0:NONE:130"),
            R(61, "PADLOCK_LEFT", 'C', 1, "View Left", "continuous", false, "", "JOY0:NONE:131"),
            R(62, "PADLOCK_RIGHT", 'C', 1, "View Right", "continuous", false, "", "JOY0:NONE:129"),
            R(63, "RADAR_RANGE_CYCLE", 'C', 1, "Cycle Radar Range", "trigger", false, "KEY:KEY_RAPOSTRO", ""),
            R(64, "SQUADMSG_MENU", 'C', 1, "Communications Menu", "trigger", false, "KEY:KEY_C", ""),
            R(65, "SHOW_GOALS", 'N', 1, "Show Objectives", "trigger", true, "", ""),
            R(66, "END_MISSION", 'C', 1, "Enter Subspace (End Mission)", "trigger", false, "KEY:ALT-KEY_J", ""),
            R(67, "TARGET_TARGETS_TARGET", 'T', 1, "Target Target's Target", "trigger", false, "KEY:KEY_J", ""),
            R(68, "AFTERBURNER", 'S', 1, "Afterburner", "continuous", false, "KEY:KEY_TAB", "JOY0:NONE:5"),
            R(69, "INCREASE_WEAPON", 'C', 1, "Weapon Energy Increase", "trigger", false, "KEY:KEY_INSERT", ""),
            R(70, "DECREASE_WEAPON", 'C', 1, "Weapon Energy Decrease", "trigger", false, "KEY:KEY_DELETE", ""),
            R(71, "INCREASE_SHIELD", 'C', 1, "Shield Energy Increase", "trigger", false, "KEY:KEY_HOME", ""),
            R(72, "DECREASE_SHIELD", 'C', 1, "Shield Energy Decrease", "trigger", false, "KEY:KEY_END", ""),
            R(73, "INCREASE_ENGINE", 'C', 1, "Engine Energy Increase", "trigger", false, "KEY:KEY_PAGEUP", ""),
            R(74, "DECREASE_ENGINE", 'C', 1, "Engine Energy Decrease", "trigger", false, "KEY:KEY_PAGEDOWN", ""),
            R(75, "ETS_EQUALIZE", 'C', 1, "Equalize Energy Settings", "trigger", false, "KEY:ALT-KEY_D", ""),
            R(76, "SHIELD_EQUALIZE", 'C', 1, "Equalize Shields", "trigger", false, "KEY:KEY_Q", "JOY0:NONE:7"),
            R(77, "SHIELD_XFER_TOP", 'C', 1, "Augment Shield Forward", "trigger", false, "KEY:KEY_UP", ""),
            R(78, "SHIELD_XFER_BOTTOM", 'C', 1, "Augment Shield Rear", "trigger", false, "KEY:KEY_DOWN", ""),
            R(79, "SHIELD_XFER_LEFT", 'C', 1, "Augment Shield Left", "trigger", false, "KEY:KEY_LEFT", ""),
            R(80, "SHIELD_XFER_RIGHT", 'C', 1, "Augment Shield Right", "trigger", false, "KEY:KEY_RIGHT", ""),
            R(81, "XFER_SHIELD", 'C', 1, "Transfer Energy Laser->Shield", "trigger", false, "KEY:KEY_SCROLLOCK", ""),
            R(82, "XFER_LASER", 'C', 1, "Transfer Energy Shield->Laser", "trigger", false, "KEY:SHIFT-KEY_SCROLLOCK", ""),
            R(83, "GLIDE_WHEN_PRESSED", 'S', 1774, "Glide When Pressed", "continuous", false, "", ""),
            R(84, "BANK_WHEN_PRESSED", 'S', 1, "Bank When Pressed", "continuous", false, "", ""),
            R(85, "SHOW_NAVMAP", 'N', 1, "Show Nav Map", "trigger", true, "", ""),
            R(86, "ADD_REMOVE_ESCORT", 'C', 1, "Add or Remove Escort", "trigger", false, "KEY:ALT-KEY_E", ""),
            R(87, "ESCORT_CLEAR", 'C', 1, "Clear Escort List", "trigger", false, "KEY:ALT-SHIFT-KEY_E", ""),
            R(88, "TARGET_NEXT_ESCORT_SHIP", 'T', 1, "Target Next Escort Ship", "trigger", false, "KEY:KEY_E", ""),
            R(89, "TARGET_CLOSEST_REPAIR_SHIP", 'T', 1, "Target Closest Repair Ship", "trigger", false, "KEY:ALT-KEY_R", ""),
            R(90, "TARGET_NEXT_UNINSPECTED_CARGO", 'T', 1, "Target Next Uninspected Cargo", "trigger", false, "KEY:KEY_U", ""),
            R(91, "TARGET_PREV_UNINSPECTED_CARGO", 'T', 1, "Target Previous Uninspected Cargo", "trigger", false, "KEY:SHIFT-KEY_U", ""),
            R(92, "TARGET_NEWEST_SHIP", 'T', 1, "Target Newest Ship in Area", "trigger", false, "KEY:KEY_N", ""),
            R(93, "TARGET_NEXT_LIVE_TURRET", 'T', 1, "Target Next Live Turret", "trigger", false, "KEY:KEY_K", ""),
            R(94, "TARGET_PREV_LIVE_TURRET", 'T', 1, "Target Previous Live Turret", "trigger", false, "KEY:SHIFT-KEY_K", ""),
            R(95, "TARGET_NEXT_BOMB", 'T', 1, "Target Next Hostile Bomb or Bomber", "trigger", false, "KEY:KEY_B", ""),
            R(96, "TARGET_PREV_BOMB", 'T', 1, "Target Previous Hostile Bomb or Bomber", "trigger", false, "KEY:SHIFT-KEY_B", ""),
            R(97, "MULTI_MESSAGE_ALL", 'C', 1, "(Multiplayer) Message All", "trigger", false, "KEY:KEY_1", ""),
            R(98, "MULTI_MESSAGE_FRIENDLY", 'C', 1, "(Multiplayer) Message Friendly", "trigger", false, "KEY:KEY_2", ""),
            R(99, "MULTI_MESSAGE_HOSTILE", 'C', 1, "(Multiplayer) Message Hostile", "trigger", false, "KEY:KEY_3", ""),
            R(100, "MULTI_MESSAGE_TARGET", 'C', 1, "(Multiplayer) Message Target", "trigger", false, "KEY:KEY_4", ""),
            R(101, "MULTI_OBSERVER_ZOOM_TO", 'C', 1, "(Multiplayer) Observer Zoom to Target", "trigger", false, "KEY:ALT-KEY_X", ""),
            R(102, "TIME_SPEED_UP", 'C', 1, "Time Compression Increase", "trigger", false, "KEY:SHIFT-KEY_PERIOD", ""),
            R(103, "TIME_SLOW_DOWN", 'C', 1, "Time Compression Decrease", "trigger", false, "KEY:SHIFT-KEY_COMMA", ""),
            R(104, "TOGGLE_HUD_CONTRAST", 'C', 1, "Toggle High HUD Contrast", "trigger", false, "KEY:KEY_L", ""),
            R(105, "MULTI_TOGGLE_NETINFO", 'C', 1, "(Multiplayer) Toggle Network Info", "trigger", false, "KEY:SHIFT-KEY_N", ""),
            R(106, "MULTI_SELF_DESTRUCT", 'C', 1, "(Multiplayer) Self Destruct", "trigger", false, "KEY:SHIFT-KEY_END", ""),
            R(107, "TOGGLE_HUD", 'C', 1, "Toggle HUD", "trigger", false, "KEY:SHIFT-KEY_O", ""),
            R(108, "RIGHT_SLIDE_THRUST", 'S', 1, "Right Thrust", "continuous", false, "KEY:SHIFT-KEY_3", ""),
            R(109, "LEFT_SLIDE_THRUST", 'S', 1, "Left Thrust", "continuous", false, "KEY:SHIFT-KEY_1", ""),
            R(110, "UP_SLIDE_THRUST", 'S', 1, "Up Thrust", "continuous", false, "KEY:SHIFT-KEY_PADPLUS", ""),
            R(111, "DOWN_SLIDE_THRUST", 'S', 1, "Down Thrust", "continuous", false, "KEY:SHIFT-KEY_PADENTER", ""),
            R(112, "HUD_TARGETBOX_TOGGLE_WIREFRAME", 'C', 1, "Toggle HUD Wireframe Target View", "trigger", false, "KEY:ALT-SHIFT-KEY_Q", ""),
            R(113, "VIEW_TOPDOWN", 'C', 1777, "Top-Down View", "trigger", false, "", ""),
            R(114, "VIEW_TRACK_TARGET", 'C', 1778, "Target Padlock View", "trigger", false, "", ""),
            R(115, "AUTO_PILOT_TOGGLE", 'C', 1779, "Toggle Auto Pilot", "trigger", false, "KEY:ALT-KEY_A", ""),
            R(116, "NAV_CYCLE", 'C', 1780, "Cycle Nav Points", "trigger", false, "KEY:ALT-KEY_N", ""),
            R(117, "TOGGLE_GLIDING", 'S', 1775, "Toggle Gliding", "trigger", false, "KEY:ALT-KEY_G", ""),
            R(118, "CYCLE_PRIMARY_WEAPON_SEQUENCE", 'W', 1776, "Cycle Primary Weapon Firing Rate", "trigger", false, "KEY:KEY_O", ""),
            R(119, "CUSTOM_CONTROL_1", 'C', 1784, "Custom Control 1", "trigger", true, "KEY:ALT-SHIFT-KEY_1", ""),
            R(120, "CUSTOM_CONTROL_2", 'C', 1785, "Custom Control 2", "trigger", true, "KEY:ALT-SHIFT-KEY_2", ""),
            R(121, "CUSTOM_CONTROL_3", 'C', 1786, "Custom Control 3", "trigger", true, "KEY:ALT-SHIFT-KEY_3", ""),
            R(122, "CUSTOM_CONTROL_4", 'C', 1787, "Custom Control 4", "trigger", true, "KEY:ALT-SHIFT-KEY_4", ""),
            R(123, "CUSTOM_CONTROL_5", 'C', 1788, "Custom Control 5", "trigger", true, "KEY:ALT-SHIFT-KEY_5", ""),
            R(124, "JOY_HEADING_AXIS", 'S', 1016, "Turn (Yaw) Axis", "rel", false, "JOY0:AXIS:X_AXIS", "MOUSE:AXIS:X_AXIS"),
            R(125, "JOY_PITCH_AXIS", 'S', 1017, "Pitch Axis", "rel", false, "JOY0:AXIS:Y_AXIS", "MOUSE:AXIS:Y_AXIS"),
            R(126, "JOY_BANK_AXIS", 'S', 1018, "Bank Axis", "rel", false, "JOY0:AXIS:RX_AXIS", ""),
            R(127, "JOY_ABS_THROTTLE_AXIS", 'S', 1019, "Absolute Throttle Axis", "abs", false, "", ""),
            R(128, "JOY_REL_THROTTLE_AXIS", 'S', 1020, "Relative Throttle Axis", "rel", false, "", ""),
            R(129, "COMMS_MENU_MOVE_UP", 'C', 1883, "Communication Menu Move Up", "trigger", false, "", ""),
            R(130, "COMMS_MENU_MOVE_DOWN", 'C', 1884, "Communication Menu Move Down", "trigger", false, "", ""),
            R(131, "COMMS_MENU_SELECT", 'C', 1885, "Communication Menu Select", "trigger", false, "", ""),
            R(132, "TOGGLE_HUD_SHADOWS", 'C', 1781, "Toggle HUD Drop Shadows", "trigger", false, "KEY:ALT-KEY_L", ""),
            R(133, "CYCLE_PRIMARY_WEAPON_PATTERN", 'W', 1859, "Cycle Primary Weapon Firing Pattern", "trigger", false, "KEY:ALT-KEY_O", ""),
            R(134, "TOGGLE_PHOTO_MODE", 'C', 1908, "Toggle Photo Mode", "trigger", false, "KEY:ALT-KEY_1", ""),
            R(135, "PHOTO_MODE_FILTER_PREV", 'C', 1909, "Photo Mode Previous Parameter", "trigger", false, "KEY:KEY_UP", ""),
            R(136, "PHOTO_MODE_FILTER_NEXT", 'C', 1910, "Photo Mode Next Parameter", "trigger", false, "KEY:KEY_DOWN", ""),
            R(137, "PHOTO_MODE_FILTER_RESET", 'C', 1911, "Photo Mode Reset Parameters", "trigger", false, "KEY:ALT-KEY_2", ""),
            R(138, "PHOTO_MODE_PARAM_DECREASE", 'C', 1912, "Photo Mode Decrease Selected Parameter", "trigger", false, "KEY:KEY_LEFT", ""),
            R(139, "PHOTO_MODE_PARAM_INCREASE", 'C', 1913, "Photo Mode Increase Selected Parameter", "trigger", false, "KEY:KEY_RIGHT", "")
        };

        private static readonly Dictionary<string, FsoControl> ByName = Defaults.ToDictionary(c => c.Name, StringComparer.Ordinal);

        public static FsoControl Find(string name)
        {
            FsoControl control;
            return ByName.TryGetValue((name ?? string.Empty).Trim(), out control) ? control : null;
        }

        public static List<FsoControl> CopyDefaults()
        {
            return Defaults.Select(c => c.Copy()).ToList();
        }
    }
}
