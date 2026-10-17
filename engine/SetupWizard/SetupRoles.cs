#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.Setup
{
    // Device roles of the Setup wizard. One device per hand and aircraft class: the right hand holds the main stick
    // (or the helicopter stick), the left hand the throttle for aircraft, the collective for helicopters and the left
    // stick for spaceships; the feet the pedals; panels take system switches. See docs/setup-wizard.md#roles.
    public static class SetupRoles
    {
        public const string MainStick = "main-stick";
        public const string HeliStick = "heli-stick";
        public const string LeftStick = "left-stick";
        public const string Throttle = "throttle";
        public const string Collective = "collective";
        public const string Pedals = "pedals";
        public const string Panel = "panel";
        public const string KeyboardJoystick = "keyboard-joystick";
        public const string Virtual = "virtual";
        public const string Unused = "unused";

        public static readonly string[] All = { MainStick, HeliStick, LeftStick, Throttle, Collective, Pedals, Panel, KeyboardJoystick, Virtual, Unused };

        public static string Title(string role)
        {
            switch (role)
            {
                case MainStick: return "Main stick (right hand)";
                case HeliStick: return "Stick for helicopters";
                case LeftStick: return "Left stick (space sims)";
                case Throttle: return "Throttle (left hand)";
                case Collective: return "Collective (helicopters)";
                case Pedals: return "Pedals";
                case Panel: return "Panel / button box";
                case KeyboardJoystick: return "Keyboard joystick (extra)";
                case Virtual: return "Virtual device (kept as is)";
                default: return "Not used";
            }
        }

        // Roles whose inputs the plan binds.
        public static bool Binds(string role)
        {
            return role == MainStick || role == HeliStick || role == LeftStick || role == Throttle || role == Collective || role == Pedals || role == Panel;
        }

        // The proposal: catalog kind and hand first, then the device the profile already uses most, a known layout and
        // more inputs. Every role but panel is taken once; a second right-hand stick is an alternative (unused) the user
        // can turn into the helicopter stick.
        public static void Propose(List<SetupDevice> devices)
        {
            foreach (SetupDevice device in devices)
            {
                device.ProposedRole = string.Empty;
                device.Reason = string.Empty;
            }

            foreach (SetupDevice device in devices)
            {
                string name = (device.Model + " " + device.Device).ToLowerInvariant();
                string kind = (device.Kind ?? string.Empty).ToLowerInvariant();
                if (kind == "virtual" || Regex.IsMatch(name, @"\bvjoy\b|vigem|virtual (joystick|device|controller)") || device.VidPid.Equals("1234:BEAD", StringComparison.OrdinalIgnoreCase))
                    Set(device, Virtual, "virtual device: binds on it are kept, the plan does not use it");
                else if (kind == "keyboard" || Regex.IsMatch(name, @"wooting|keyboard"))
                    Set(device, KeyboardJoystick, "keyboard with joystick mode: an extra, kept for your keyboard binds");
                else if (kind == "wheel" || kind == "gamepad" || kind == "other")
                    Set(device, Unused, kind == "wheel" ? "racing wheel" : kind == "gamepad" ? "gamepad" : "not a flight device");
            }

            List<SetupDevice> open = devices.Where(d => d.ProposedRole.Length == 0).ToList();
            Func<IEnumerable<SetupDevice>, List<SetupDevice>> ranked = list => list
                .OrderByDescending(d => d.Connected)
                .ThenByDescending(d => d.ProfileUses)
                .ThenByDescending(d => !d.Generic && !d.NeedsGrip)
                .ThenByDescending(d => d.Buttons + d.Hats * 4 + d.Axes)
                .ThenBy(d => d.Device, StringComparer.Ordinal).ToList();

            List<SetupDevice> pedals = ranked(open.Where(d => d.Kind == "pedals"));
            TakeFirst(pedals, Pedals, "rudder pedals: yaw / rudder and toe brakes");
            List<SetupDevice> collectives = ranked(open.Where(d => d.Kind == "collective"));
            TakeFirst(collectives, Collective, "collective: the helicopters' collective and throttle");
            List<SetupDevice> throttles = ranked(open.Where(d => d.Kind == "throttle" || d.Kind == "quadrant"));
            TakeFirst(throttles, Throttle, "throttle: the left hand for aircraft");

            List<SetupDevice> sticks = open.Where(d => d.Kind == "stick" || d.Kind == "hotas" || d.Kind == "yoke" || d.Kind == "base").ToList();
            List<SetupDevice> left = ranked(sticks.Where(IsLeftHand));
            List<SetupDevice> right = ranked(sticks.Where(d => !IsLeftHand(d)));
            if (right.Count == 0 && left.Count > 0)
            {
                // Only left-hand sticks: the first one is the main stick (left-handed pilot).
                right.Add(left[0]);
                left.RemoveAt(0);
            }
            TakeFirst(right, MainStick, "main stick: the right hand in every aircraft");
            TakeFirst(left, LeftStick, "left-hand stick: translation in space sims" + (throttles.Count > 0 ? string.Empty : "; also the throttle side (no throttle)"));

            foreach (SetupDevice device in devices.Where(d => d.ProposedRole.Length == 0))
            {
                string kind = device.Kind ?? string.Empty;
                if (kind == "panel" || kind == "mfd" || kind == "buttonbox" || kind == "controller")
                    Set(device, Panel, "panel / button box: system switches where their type and label fit");
                else if (kind == "stick" || kind == "hotas" || kind == "yoke" || kind == "base")
                    Set(device, Unused, "a second " + (IsLeftHand(device) ? "left" : "right") + "-hand stick: an alternative (one device per hand)" +
                                        (device.ForceFeedback ? "; choose \"Stick for helicopters\" to fly helicopters with it" : string.Empty));
                else if (kind == "throttle" || kind == "quadrant" || kind == "collective" || kind == "pedals")
                    Set(device, Unused, "a second " + kind + ": an alternative (one device per hand)");
                else
                    Set(device, Panel, "unknown device: used like a button box");
            }

            foreach (SetupDevice device in devices)
            {
                if (device.Role.Length == 0 || !All.Contains(device.Role))
                    device.Role = device.ProposedRole;
            }
        }

        public static bool IsLeftHand(SetupDevice device)
        {
            if (device.Hand == "left")
                return true;
            if (device.Hand == "right")
                return false;
            string name = (device.Model + " " + device.Device).Trim().ToLowerInvariant();
            return Regex.IsMatch(name, @"^(l-|l\s)|\bleft\b|\blh\b|\[l\]");
        }

        private static void TakeFirst(List<SetupDevice> list, string role, string reason)
        {
            if (list.Count > 0 && list[0].ProposedRole.Length == 0)
                Set(list[0], role, reason);
        }

        private static void Set(SetupDevice device, string role, string reason)
        {
            device.ProposedRole = role;
            device.Reason = reason;
        }

        // ---- Hands per aircraft class ----

        public static SetupDevice RoleDevice(IEnumerable<SetupDevice> devices, string role)
        {
            return devices.FirstOrDefault(d => d.Role == role);
        }

        // The device in the right hand for a class.
        public static SetupDevice RightHand(List<SetupDevice> devices, string cls)
        {
            if (cls == SetupClasses.Rotary)
            {
                SetupDevice heli = RoleDevice(devices, HeliStick);
                if (heli != null)
                    return heli;
            }
            return RoleDevice(devices, MainStick) ?? (cls == SetupClasses.Rotary ? null : RoleDevice(devices, HeliStick));
        }

        // The device in the left hand for a class: helicopters take the collective, spaceships the left stick, aircraft the
        // throttle; each falls back to the others.
        public static SetupDevice LeftHand(List<SetupDevice> devices, string cls)
        {
            string[] order;
            if (cls == SetupClasses.Rotary)
                order = new[] { Collective, Throttle, LeftStick };
            else if (cls == SetupClasses.Space)
                order = new[] { LeftStick, Throttle, Collective };
            else
                order = new[] { Throttle, LeftStick, Collective };
            foreach (string role in order)
            {
                SetupDevice device = RoleDevice(devices, role);
                if (device != null)
                    return device;
            }
            return null;
        }

        // Devices the hands, feet and panels reach for an aircraft class.
        public static HashSet<string> Reachable(List<SetupDevice> devices, string cls)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            SetupDevice right = RightHand(devices, cls);
            SetupDevice left = LeftHand(devices, cls);
            if (right != null)
                set.Add(right.Device);
            if (left != null)
                set.Add(left.Device);
            foreach (SetupDevice device in devices.Where(d => d.Role == Pedals || d.Role == Panel))
                set.Add(device.Device);
            return set;
        }

        // The rules the plan follows for this setup, in words (the wizard shows them under the device list).
        public static List<string> Rules(List<SetupDevice> devices)
        {
            List<string> rules = new List<string>();
            SetupDevice main = RoleDevice(devices, MainStick);
            SetupDevice heli = RoleDevice(devices, HeliStick);
            SetupDevice throttle = RoleDevice(devices, Throttle);
            SetupDevice collective = RoleDevice(devices, Collective);
            SetupDevice leftStick = RoleDevice(devices, LeftStick);
            SetupDevice pedals = RoleDevice(devices, Pedals);
            List<SetupDevice> panels = devices.Where(d => d.Role == Panel).ToList();

            rules.Add(main != null ? "Right hand: " + Short(main) + " in every aircraft" + (heli != null ? " except helicopters (" + Short(heli) + ")" : string.Empty) + "."
                : "Right hand: no main stick chosen.");
            SetupDevice fixedLeft = LeftHand(devices, SetupClasses.Fixed);
            rules.Add(fixedLeft != null ? "Left hand in aircraft: " + Short(fixedLeft) + " (throttle, airbrake, sensors)." : "Left hand: no throttle: throttle functions go on the main stick and panels.");
            if (collective != null)
                rules.Add("Helicopters: the collective is " + Short(collective) + "; the throttle " + (throttle != null ? Short(throttle) + " stays with the aircraft" : "is not used") + ".");
            else if (throttle != null)
                rules.Add("Helicopters: no collective: the collective goes on the throttle axis of " + Short(throttle) + ".");
            if (leftStick != null)
                rules.Add("Space sims: translation on the left stick " + Short(leftStick) + " (X = strafe left/right, Y = forward/back, twist = up/down), its buttons for the ship.");
            else if (throttle != null)
                rules.Add("Space sims: no left stick: translation on the throttle's mini-stick, axes and hats (" + Short(throttle) + ").");
            if (pedals != null)
                rules.Add("Pedals: yaw / rudder and toe brakes on " + Short(pedals) + ".");
            if (panels.Count > 0)
                rules.Add("Panels: system switches (gear, lights, master arm ...) where their type and label fit: " + string.Join(", ", panels.Select(Short)) + ".");
            foreach (SetupDevice device in devices.Where(d => d.Role == KeyboardJoystick))
                rules.Add(Short(device) + ": an extra, kept for your keyboard binds (the plan does not bind it).");
            foreach (SetupDevice device in devices.Where(d => d.Role == Virtual))
                rules.Add(Short(device) + ": virtual, its binds are kept.");
            List<SetupDevice> unused = devices.Where(d => d.Role == Unused).ToList();
            if (unused.Count > 0)
                rules.Add("Not used: " + string.Join(", ", unused.Select(Short)) + ".");
            return rules;
        }

        public static string Short(SetupDevice device)
        {
            string name = device.Model.Length > 0 ? device.Model : Regex.Replace(device.Device, @"\s*\{[0-9A-Fa-f-]+\}\s*$", string.Empty);
            return name.Trim();
        }
    }
}
