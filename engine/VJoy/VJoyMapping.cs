using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JoyPro.Engine.Games;

namespace JoyPro.Engine.VJoy
{
    // Virtual joystick mode (docs/virtual-joystick.md): per game the user picks native devices or vJoy. In virtual mode
    // every physical input that the game's relations use (axes, buttons, hats, modifier buttons) gets a fixed slot on a
    // vJoy device; JoyPro.VJoy.exe forwards the physical input there live, the export writes the binds to the vJoy slot
    // and HidHide hides the physical device from the game. The table is kept in the profile and never reshuffled:
    // a slot, once given, stays (until --reset), so the game's binds do not move between runs.
    internal sealed class VJoySettings
    {
        public const int MaxDevices = 16;
        public const int MaxAxes = 8;
        public const int MaxButtons = 128;
        public const int MaxPovs = 4;

        public int Version { get; set; }
        // Game id -> "virtual" (games without an entry use their physical devices).
        public Dictionary<string, string> Games { get; set; }
        // The vJoy devices JoyPro may use (1..16).
        public int FirstDevice { get; set; }
        public int LastDevice { get; set; }
        // Start forwarding (and HidHide) by itself when a game with virtual mode starts (GUI).
        public bool AutoStart { get; set; }
        // Hide the physical devices from the games with HidHide (off: the games see both).
        public bool HideDevices { get; set; }
        public List<VJoySlot> Slots { get; set; }
        // Games with the virtual Xbox 360 pad (mode "xbox"): per game its own table (Target = XB_* tokens, see
        // XboxControls), because an XInput game takes one pad and its own layout.
        public Dictionary<string, List<VJoySlot>> XboxSlots { get; set; }
        // Virtual functions per game (VirtualFunctions.cs): evaluated by the forwarder between the physical inputs and
        // the virtual outputs. Their vJoy outputs are reserved in the table (the planner never gives them to an input).
        public Dictionary<string, List<VirtualFunction>> Functions { get; set; }

        public VJoySettings()
        {
            Version = 1;
            Games = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            FirstDevice = 1;
            LastDevice = MaxDevices;
            HideDevices = true;
            Slots = new List<VJoySlot>();
            XboxSlots = new Dictionary<string, List<VJoySlot>>(StringComparer.OrdinalIgnoreCase);
            Functions = new Dictionary<string, List<VirtualFunction>>(StringComparer.OrdinalIgnoreCase);
        }

        // The functions of a game (empty list when none; the list is the stored one).
        public List<VirtualFunction> FunctionsOf(string gameId)
        {
            List<VirtualFunction> list;
            if (Functions == null)
                Functions = new Dictionary<string, List<VirtualFunction>>(StringComparer.OrdinalIgnoreCase);
            if (!Functions.TryGetValue((gameId ?? string.Empty).Trim(), out list) || list == null)
            {
                list = new List<VirtualFunction>();
                Functions[(gameId ?? string.Empty).Trim()] = list;
            }
            return list;
        }

        // Every function with its game.
        public IEnumerable<KeyValuePair<string, VirtualFunction>> AllFunctions()
        {
            foreach (KeyValuePair<string, List<VirtualFunction>> game in (Functions ?? new Dictionary<string, List<VirtualFunction>>()).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (VirtualFunction function in game.Value ?? new List<VirtualFunction>())
                    yield return new KeyValuePair<string, VirtualFunction>(game.Key, function);
            }
        }

        public const string ModeVJoy = "virtual";
        public const string ModeXbox = "xbox";

        // The game uses the virtual Xbox 360 pad (ViGEmBus).
        public bool IsXbox(string gameId)
        {
            string mode;
            return !string.IsNullOrWhiteSpace(gameId) && Games != null &&
                   Games.TryGetValue(gameId.Trim(), out mode) && string.Equals(mode, ModeXbox, StringComparison.OrdinalIgnoreCase);
        }

        // Any virtual target (vJoy or Xbox).
        public bool IsAnyVirtual(string gameId)
        {
            return IsVirtual(gameId) || IsXbox(gameId);
        }

        public List<string> XboxGames()
        {
            return (Games ?? new Dictionary<string, string>())
                .Where(g => string.Equals(g.Value, ModeXbox, StringComparison.OrdinalIgnoreCase))
                .Select(g => g.Key)
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<string> AllVirtualGames()
        {
            return VirtualGames().Concat(XboxGames()).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public List<VJoySlot> XboxTable(string gameId)
        {
            List<VJoySlot> slots;
            return XboxSlots != null && XboxSlots.TryGetValue(gameId ?? string.Empty, out slots) && slots != null ? slots : new List<VJoySlot>();
        }

        public bool IsVirtual(string gameId)
        {
            string mode;
            return !string.IsNullOrWhiteSpace(gameId) && Games != null &&
                   Games.TryGetValue(gameId.Trim(), out mode) && string.Equals(mode, "virtual", StringComparison.OrdinalIgnoreCase);
        }

        public List<string> VirtualGames()
        {
            return (Games ?? new Dictionary<string, string>())
                .Where(g => string.Equals(g.Value, "virtual", StringComparison.OrdinalIgnoreCase))
                .Select(g => g.Key)
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public void Normalize()
        {
            if (Games == null)
                Games = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            else if (!Equals(Games.Comparer, StringComparer.OrdinalIgnoreCase))
                Games = new Dictionary<string, string>(Games, StringComparer.OrdinalIgnoreCase);
            if (Slots == null)
                Slots = new List<VJoySlot>();
            Slots.RemoveAll(s => s == null || string.IsNullOrWhiteSpace(s.Source) || string.IsNullOrWhiteSpace(s.Input) || string.IsNullOrWhiteSpace(s.Target));
            XboxSlots = XboxSlots == null
                ? new Dictionary<string, List<VJoySlot>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<VJoySlot>>(XboxSlots, StringComparer.OrdinalIgnoreCase);
            foreach (string game in XboxSlots.Keys.ToList())
            {
                List<VJoySlot> list = XboxSlots[game] ?? new List<VJoySlot>();
                list.RemoveAll(s => s == null || string.IsNullOrWhiteSpace(s.Source) || string.IsNullOrWhiteSpace(s.Input) || string.IsNullOrWhiteSpace(s.Target));
                if (list.Count == 0)
                    XboxSlots.Remove(game);
                else
                    XboxSlots[game] = list;
            }
            Functions = Functions == null
                ? new Dictionary<string, List<VirtualFunction>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<VirtualFunction>>(Functions, StringComparer.OrdinalIgnoreCase);
            foreach (string game in Functions.Keys.ToList())
            {
                List<VirtualFunction> list = Functions[game] ?? new List<VirtualFunction>();
                list.RemoveAll(f => f == null);
                foreach (VirtualFunction function in list)
                    function.Normalize();
                if (list.Count == 0)
                    Functions.Remove(game);
                else
                    Functions[game] = list;
            }
            FirstDevice = Math.Max(1, Math.Min(MaxDevices, FirstDevice <= 0 ? 1 : FirstDevice));
            LastDevice = Math.Max(FirstDevice, Math.Min(MaxDevices, LastDevice <= 0 ? MaxDevices : LastDevice));
        }

        public static VJoySettings FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Fresh();
            try
            {
                VJoySettings settings = EngineSerialization.FromJson<VJoySettings>(json) ?? Fresh();
                settings.Normalize();
                return settings;
            }
            catch (Exception)
            {
                return Fresh();
            }
        }

        public string ToJson()
        {
            Normalize();
            return EngineSerialization.ToJson(this);
        }

        [System.Web.Script.Serialization.ScriptIgnore]
        public bool IsEmpty
        {
            get { return AllVirtualGames().Count == 0 && (Slots == null || Slots.Count == 0) && (XboxSlots == null || XboxSlots.Count == 0) && (Functions == null || Functions.Count == 0) && FirstDevice == 1 && LastDevice == MaxDevices && !AutoStart && HideDevices; }
        }

        private static VJoySettings Fresh()
        {
            VJoySettings settings = new VJoySettings();
            settings.Normalize();
            return settings;
        }
    }

    // Physical input (Source device + Input) -> vJoy input (Device + Target). Input/Target: JOY_X .. JOY_SLIDER2,
    // JOY_BTN<n> or POV<n> (a whole hat: its eight directions go along).
    internal sealed class VJoySlot
    {
        public string Source { get; set; }
        public string Input { get; set; }
        public int Device { get; set; }
        public string Target { get; set; }

        public VJoySlot()
        {
            Source = Input = Target = string.Empty;
        }
    }

    internal enum VJoyInputKind
    {
        Axis = 0,
        Button = 1,
        Pov = 2
    }

    // A slot input decoded: axis 0..7 (X, Y, Z, Rx, Ry, Rz, Slider1, Slider2), button 0..127, hat 0..3.
    internal struct VJoyInput
    {
        public VJoyInputKind Kind;
        public int Index;

        public static bool TryParseSlotToken(string token, out VJoyInput input)
        {
            input = new VJoyInput();
            string value = (token ?? string.Empty).Trim().ToUpperInvariant();
            if (value.StartsWith("POV", StringComparison.Ordinal))
            {
                int hat;
                if (!int.TryParse(value.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out hat) || hat < 1)
                    return false;
                input.Kind = VJoyInputKind.Pov;
                input.Index = hat - 1;
                return true;
            }
            JoyInput joy;
            if (!GameInputTokens.TryParse(value, out joy) || joy.Kind == JoyInputKind.Pov || joy.Kind == JoyInputKind.None)
                return false;
            input.Kind = joy.Kind == JoyInputKind.Axis ? VJoyInputKind.Axis : VJoyInputKind.Button;
            input.Index = joy.Index;
            return true;
        }

        // A JoyPro bind token (JOY_BTN_POV2_UR included) as the slot it needs; direction = -1 unless a hat direction.
        public static bool TryFromBindToken(string token, out VJoyInput input, out int direction)
        {
            input = new VJoyInput();
            direction = -1;
            JoyInput joy;
            if (!GameInputTokens.TryParse(token, out joy))
                return false;
            switch (joy.Kind)
            {
                case JoyInputKind.Axis:
                    input.Kind = VJoyInputKind.Axis;
                    break;
                case JoyInputKind.Button:
                    input.Kind = VJoyInputKind.Button;
                    break;
                case JoyInputKind.Pov:
                    input.Kind = VJoyInputKind.Pov;
                    direction = joy.Direction;
                    break;
                default:
                    return false;
            }
            input.Index = joy.Index;
            return true;
        }

        public string ToSlotToken()
        {
            switch (Kind)
            {
                case VJoyInputKind.Axis: return GameInputTokens.AxisToken(Index);
                case VJoyInputKind.Button: return GameInputTokens.ButtonToken(Index);
                default: return "POV" + (Index + 1).ToString(CultureInfo.InvariantCulture);
            }
        }

        // The bind token on this input (a hat direction for hats).
        public string ToBindToken(int direction)
        {
            if (Kind == VJoyInputKind.Pov)
                return GameInputTokens.PovToken(Index, direction < 0 ? 0 : direction);
            return ToSlotToken();
        }

        public int Limit
        {
            get { return Kind == VJoyInputKind.Axis ? VJoySettings.MaxAxes : Kind == VJoyInputKind.Button ? VJoySettings.MaxButtons : VJoySettings.MaxPovs; }
        }
    }

    // One physical input the virtual-mode games use.
    internal sealed class VJoyUsedInput
    {
        public string Source { get; set; }
        public string Input { get; set; }
        public List<string> Relations { get; set; }
        public List<string> Games { get; set; }

        public VJoyUsedInput()
        {
            Source = Input = string.Empty;
            Relations = new List<string>();
            Games = new List<string>();
        }
    }

    // A vJoy device as configured (vJoyConfig / the driver's registry descriptor).
    internal sealed class VJoyDeviceCaps
    {
        public int Id { get; set; }
        public bool Configured { get; set; }
        public List<int> Axes { get; set; }
        public int Buttons { get; set; }
        public int ContinuousPovs { get; set; }
        public int DiscretePovs { get; set; }
        public bool ForceFeedback { get; set; }

        public VJoyDeviceCaps()
        {
            Axes = new List<int>();
        }

        public int Povs
        {
            get { return ContinuousPovs + DiscretePovs; }
        }

        public string Describe()
        {
            if (!Configured)
                return "not configured";
            return (Axes.Count == 0 ? "no axes" : string.Join(",", Axes.OrderBy(a => a).Select(VJoyConfigArgs.AxisName))) + ", " +
                   Buttons.ToString(CultureInfo.InvariantCulture) + " buttons, " +
                   (ContinuousPovs > 0 ? ContinuousPovs.ToString(CultureInfo.InvariantCulture) + " hats" : DiscretePovs > 0 ? DiscretePovs.ToString(CultureInfo.InvariantCulture) + " 4-way hats" : "no hats") +
                   (ForceFeedback ? ", force feedback" : string.Empty);
        }
    }

    // What one vJoy device must offer for the table, and how to get there.
    internal sealed class VJoyDeviceNeed
    {
        public int Id { get; set; }
        public List<int> Axes { get; set; }
        public int Buttons { get; set; }
        public int Povs { get; set; }
        public int Slots { get; set; }
        public int FunctionOutputs { get; set; }
        public string Current { get; set; }
        public bool Sufficient { get; set; }
        public List<string> Missing { get; set; }
        // vJoyConfig arguments that configure the device (empty when Sufficient).
        public List<string> ConfigArguments { get; set; }
        public string Recommended { get; set; }

        public VJoyDeviceNeed()
        {
            Axes = new List<int>();
            Missing = new List<string>();
            ConfigArguments = new List<string>();
            Current = Recommended = string.Empty;
        }
    }

    internal sealed class VJoyPlan
    {
        public List<VJoySlot> Slots { get; set; }
        public List<VJoySlot> Added { get; set; }
        public List<VJoySlot> Unused { get; set; }
        public List<VJoySlot> Removed { get; set; }
        public List<VJoyDeviceNeed> Devices { get; set; }
        public List<string> Errors { get; set; }
        public List<string> Warnings { get; set; }
        // vJoy axes a virtual-mode game must not see (axis index -> why), e.g. Battlefield takes a joystick with Rx/Ry
        // for an Xbox pad: new inputs never get them and configure-vjoy leaves them out (IGameDeviceLimit.AvoidVJoyAxes).
        public Dictionary<int, string> AvoidAxes { get; set; }
        // The virtual functions' vJoy outputs (Source = game, Input = "<function id>:<role>", Device, Target).
        public List<VJoySlot> FunctionTargets { get; set; }

        public VJoyPlan()
        {
            AvoidAxes = new Dictionary<int, string>();
            FunctionTargets = new List<VJoySlot>();
            Slots = new List<VJoySlot>();
            Added = new List<VJoySlot>();
            Unused = new List<VJoySlot>();
            Removed = new List<VJoySlot>();
            Devices = new List<VJoyDeviceNeed>();
            Errors = new List<string>();
            Warnings = new List<string>();
        }

        public bool Changed
        {
            get { return Added.Count > 0 || Removed.Count > 0; }
        }
    }

    internal static class VJoyPlanner
    {
        // Keeps every slot that is still valid (stable), gives the new inputs free slots and computes what the vJoy
        // devices must offer. reset: start from an empty table; prune: drop slots no virtual-mode game uses any more.
        public static VJoyPlan Plan(VJoySettings settings, List<VJoyUsedInput> used, IDictionary<int, VJoyDeviceCaps> caps, bool reset, bool prune,
                                    IDictionary<int, string> avoidAxes = null)
        {
            settings.Normalize();
            VJoyPlan plan = new VJoyPlan();
            if (avoidAxes != null)
            {
                foreach (KeyValuePair<int, string> avoid in avoidAxes)
                    plan.AvoidAxes[avoid.Key] = avoid.Value;
            }
            HashSet<string> usedKeys = new HashSet<string>(used.Select(u => Key(u.Source, u.Input)), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool> taken = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (VJoySlot slot in settings.Slots)
            {
                VJoyInput input;
                VJoyInput target;
                string key = Key(slot.Source, slot.Input);
                bool valid = !reset &&
                             VJoyInput.TryParseSlotToken(slot.Input, out input) &&
                             VJoyInput.TryParseSlotToken(slot.Target, out target) &&
                             input.Kind == target.Kind &&
                             slot.Device >= settings.FirstDevice && slot.Device <= settings.LastDevice &&
                             !seenSources.Contains(key) &&
                             !taken.ContainsKey(TargetKey(slot.Device, target));
                if (!valid || (prune && !usedKeys.Contains(key)))
                {
                    plan.Removed.Add(slot);
                    continue;
                }
                VJoyInput.TryParseSlotToken(slot.Target, out target);
                seenSources.Add(key);
                taken[TargetKey(slot.Device, target)] = true;
                VJoySlot kept = new VJoySlot { Source = slot.Source.Trim(), Input = Normalize(slot.Input), Device = slot.Device, Target = Normalize(slot.Target) };
                plan.Slots.Add(kept);
                string why;
                if (target.Kind == VJoyInputKind.Axis && plan.AvoidAxes.TryGetValue(target.Index, out why))
                    plan.Warnings.Add(kept.Source + " " + kept.Input + " sits on vJoy " + VJoyConfigArgs.AxisName(target.Index).ToUpperInvariant() + ": " + why +
                                      " Give it another axis: engine vjoy map --reset (all slots are given anew; export the virtual games again).");
                if (!usedKeys.Contains(key))
                    plan.Unused.Add(kept);
            }

            // The virtual functions' vJoy outputs are reserved: no new input gets them. A kept slot on one stays (it was
            // there first) and the function is flagged.
            Dictionary<string, VJoySlot> slotByTarget = plan.Slots
                .Select(s => { VJoyInput t; return VJoyInput.TryParseSlotToken(s.Target, out t) ? new KeyValuePair<string, VJoySlot>(TargetKey(s.Device, t), s) : new KeyValuePair<string, VJoySlot>(string.Empty, s); })
                .Where(p => p.Key.Length > 0)
                .GroupBy(p => p.Key)
                .ToDictionary(g => g.Key, g => g.First().Value);
            Dictionary<string, string> functionTaken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, VirtualFunction> entry in settings.AllFunctions())
            {
                VirtualFunction function = entry.Value;
                foreach (VfOutput output in function.Outputs)
                {
                    VJoyInput target;
                    string kind;
                    int index;
                    int mask;
                    if (output.Target.Length == 0 || !VfTokens.TryParseOutput(output.Target, out kind, out index, out mask) || (kind != "button" && kind != "axis" && kind != "hat") ||
                        !VJoyInput.TryParseSlotToken(output.Target, out target))
                        continue;
                    int device = output.VJoyDevice <= 0 ? 1 : output.VJoyDevice;
                    string label = VJoyFunctionLabel(entry.Key, function, output.Role);
                    plan.FunctionTargets.Add(new VJoySlot { Source = entry.Key, Input = function.Id + ":" + output.Role, Device = device, Target = target.ToSlotToken() });
                    if (device < settings.FirstDevice || device > settings.LastDevice)
                        plan.Warnings.Add(label + " writes vJoy device " + device.ToString(CultureInfo.InvariantCulture) + ", outside the devices JoyPro uses (" +
                                          settings.FirstDevice.ToString(CultureInfo.InvariantCulture) + "-" + settings.LastDevice.ToString(CultureInfo.InvariantCulture) + ").");
                    string key = TargetKey(device, target);
                    VJoySlot owner;
                    string other;
                    bool shiftBase = function.Type == VirtualFunctionCatalog.Shift && output.Role.StartsWith("base", StringComparison.Ordinal);
                    if (!shiftBase && slotByTarget.TryGetValue(key, out owner))
                        plan.Warnings.Add(label + " writes vJoy " + device.ToString(CultureInfo.InvariantCulture) + " " + target.ToSlotToken() + ", which is also the slot of " + owner.Source + " " + owner.Input +
                                          ": both drive it. Pick another output for the function.");
                    else if (!shiftBase && functionTaken.TryGetValue(key, out other) && other != label)
                        plan.Warnings.Add(label + " and " + other + " both write vJoy " + device.ToString(CultureInfo.InvariantCulture) + " " + target.ToSlotToken() + " (they combine).");
                    if (!functionTaken.ContainsKey(key))
                        functionTaken[key] = label;
                    taken[key] = true;
                }
            }

            // New inputs in a fixed order (device, then axes, buttons, hats by number), so the same profile always
            // gives the same table.
            List<VJoyUsedInput> fresh = used
                .Where(u => !seenSources.Contains(Key(u.Source, u.Input)))
                .OrderBy(u => u.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(u => OrderOf(u.Input))
                .ToList();
            foreach (VJoyUsedInput item in fresh)
            {
                VJoyInput input;
                if (!VJoyInput.TryParseSlotToken(item.Input, out input))
                    continue;
                VJoySlot slot = Assign(settings, plan.Slots, taken, item.Source, input, plan.AvoidAxes);
                if (slot == null)
                {
                    plan.Errors.Add("No free vJoy " + KindName(input.Kind) + " left on devices " + settings.FirstDevice.ToString(CultureInfo.InvariantCulture) + "-" +
                                    settings.LastDevice.ToString(CultureInfo.InvariantCulture) + " for " + item.Source + " " + item.Input + ".");
                    continue;
                }
                seenSources.Add(Key(item.Source, item.Input));
                plan.Slots.Add(slot);
                plan.Added.Add(slot);
            }

            plan.Slots = plan.Slots
                .OrderBy(s => s.Device)
                .ThenBy(s => OrderOf(s.Target))
                .ToList();
            // What each vJoy device must offer: the table's slots and the functions' outputs.
            foreach (IGrouping<int, VJoySlot> group in plan.Slots.Concat(plan.FunctionTargets.Where(f => f.Device >= settings.FirstDevice && f.Device <= settings.LastDevice))
                         .GroupBy(s => s.Device).OrderBy(g => g.Key))
            {
                VJoyDeviceCaps current;
                if (caps == null || !caps.TryGetValue(group.Key, out current))
                    current = new VJoyDeviceCaps { Id = group.Key };
                VJoyDeviceNeed need = Need(group.Key, group.ToList(), current, plan.AvoidAxes);
                need.Slots = group.Count(s => plan.Slots.Contains(s));
                need.FunctionOutputs = group.Count() - need.Slots;
                plan.Devices.Add(need);
            }
            return plan;
        }

        // Function "Throttle detents" (DCS) band2
        public static string VJoyFunctionLabel(string gameId, VirtualFunction function, string role)
        {
            return "Function \"" + (function.Name.Length > 0 ? function.Name : function.Id) + "\" (" + gameId + ")" + (string.IsNullOrEmpty(role) ? string.Empty : " " + role);
        }

        public static VJoyDeviceNeed Need(int id, List<VJoySlot> slots, VJoyDeviceCaps current, IDictionary<int, string> avoidAxes = null)
        {
            VJoyDeviceNeed need = new VJoyDeviceNeed { Id = id, Slots = slots.Count, Current = current.Describe() };
            foreach (VJoySlot slot in slots)
            {
                VJoyInput target;
                if (!VJoyInput.TryParseSlotToken(slot.Target, out target))
                    continue;
                if (target.Kind == VJoyInputKind.Axis && !need.Axes.Contains(target.Index))
                    need.Axes.Add(target.Index);
                else if (target.Kind == VJoyInputKind.Button)
                    need.Buttons = Math.Max(need.Buttons, target.Index + 1);
                else if (target.Kind == VJoyInputKind.Pov)
                    need.Povs = Math.Max(need.Povs, target.Index + 1);
            }
            need.Axes.Sort();
            if (!current.Configured)
                need.Missing.Add("vJoy device " + id.ToString(CultureInfo.InvariantCulture) + " is not configured");
            else
            {
                foreach (int axis in need.Axes.Where(a => !current.Axes.Contains(a)))
                    need.Missing.Add("axis " + VJoyConfigArgs.AxisName(axis));
                if (need.Buttons > current.Buttons)
                    need.Missing.Add(need.Buttons.ToString(CultureInfo.InvariantCulture) + " buttons (has " + current.Buttons.ToString(CultureInfo.InvariantCulture) + ")");
                if (need.Povs > current.Povs)
                    need.Missing.Add(need.Povs.ToString(CultureInfo.InvariantCulture) + " hats (has " + current.Povs.ToString(CultureInfo.InvariantCulture) + ")");
                if (avoidAxes != null)
                {
                    foreach (int axis in avoidAxes.Keys.Where(a => current.Axes.Contains(a) && !need.Axes.Contains(a)).OrderBy(a => a))
                        need.Missing.Add("without axis " + VJoyConfigArgs.AxisName(axis) + " (" + avoidAxes[axis] + ")");
                }
            }
            need.Sufficient = need.Missing.Count == 0;
            VJoyDeviceCaps recommended = Recommend(need, current, avoidAxes);
            need.Recommended = recommended.Describe();
            if (!need.Sufficient)
                need.ConfigArguments = VJoyConfigArgs.Create(recommended);
            return need;
        }

        // Never smaller than today (the e2e kit and other programs keep what they use): the union of the device's
        // configuration and the table's needs; all eight axes as soon as one is used, buttons in steps of 32,
        // continuous hats. The one exception: axes a virtual-mode game must not see (avoidAxes) are left out unless the
        // table uses them.
        public static VJoyDeviceCaps Recommend(VJoyDeviceNeed need, VJoyDeviceCaps current, IDictionary<int, string> avoidAxes = null)
        {
            VJoyDeviceCaps output = new VJoyDeviceCaps { Id = need.Id, Configured = true, ForceFeedback = current.Configured && current.ForceFeedback };
            HashSet<int> axes = new HashSet<int>(need.Axes);
            if (current.Configured)
                axes.UnionWith(current.Axes);
            if (axes.Count > 0)
                axes.UnionWith(Enumerable.Range(0, VJoySettings.MaxAxes));
            if (avoidAxes != null)
                axes.RemoveWhere(a => avoidAxes.ContainsKey(a) && !need.Axes.Contains(a));
            output.Axes = axes.OrderBy(a => a).ToList();
            int buttons = Math.Max(need.Buttons, current.Configured ? current.Buttons : 0);
            output.Buttons = buttons <= 0 ? 0 : Math.Min(VJoySettings.MaxButtons, ((buttons + 31) / 32) * 32);
            output.ContinuousPovs = Math.Min(VJoySettings.MaxPovs, Math.Max(need.Povs, current.Configured ? current.Povs : 0));
            return output;
        }

        // vJoy's maximum for one device (configure-vjoy --max): all 8 axes, 128 buttons, 4 continuous hats, force
        // feedback kept. An axis a virtual-mode game must not see stays out unless the table uses it (Battlefield).
        public static VJoyDeviceCaps Maximum(int id, VJoyDeviceCaps current, IEnumerable<int> usedAxes, IDictionary<int, string> avoidAxes)
        {
            HashSet<int> used = new HashSet<int>(usedAxes ?? Enumerable.Empty<int>());
            List<int> axes = Enumerable.Range(0, VJoySettings.MaxAxes).Where(a => avoidAxes == null || !avoidAxes.ContainsKey(a) || used.Contains(a)).ToList();
            return new VJoyDeviceCaps
            {
                Id = id,
                Configured = true,
                Axes = axes,
                Buttons = VJoySettings.MaxButtons,
                ContinuousPovs = VJoySettings.MaxPovs,
                ForceFeedback = current != null && current.Configured && current.ForceFeedback
            };
        }

        // Does the device offer at least what target offers (axes, buttons, hats)?
        public static bool IsAtLeast(VJoyDeviceCaps current, VJoyDeviceCaps target)
        {
            if (current == null || !current.Configured)
                return target == null || !target.Configured;
            return target.Axes.All(a => current.Axes.Contains(a)) && current.Buttons >= target.Buttons && current.Povs >= target.Povs;
        }

        // Has the device vJoy's maximum (8 axes, 128 buttons, 4 hats)?
        public static bool AtMaximum(VJoyDeviceCaps current)
        {
            return current != null && current.Configured && current.Buttons >= VJoySettings.MaxButtons && current.Povs >= VJoySettings.MaxPovs && current.Axes.Count >= VJoySettings.MaxAxes;
        }

        private static VJoySlot Assign(VJoySettings settings, List<VJoySlot> slots, Dictionary<string, bool> taken, string source, VJoyInput input,
                                       IDictionary<int, string> avoidAxes)
        {
            Func<VJoyInput, bool> allowed = t => t.Kind != VJoyInputKind.Axis || avoidAxes == null || !avoidAxes.ContainsKey(t.Index);
            // The devices this physical device already uses come first: one stick stays on one vJoy device where it can.
            List<int> order = slots.Where(s => string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Device)
                .Distinct()
                .ToList();
            for (int d = settings.FirstDevice; d <= settings.LastDevice; d++)
            {
                if (!order.Contains(d))
                    order.Add(d);
            }
            foreach (int device in order)
            {
                // An axis keeps its own axis where it is free (X stays X), else the first free one.
                if (input.Kind == VJoyInputKind.Axis && !taken.ContainsKey(TargetKey(device, input)) && allowed(input))
                    return Take(taken, source, input, device, input);
                for (int i = 0; i < input.Limit; i++)
                {
                    VJoyInput target = new VJoyInput { Kind = input.Kind, Index = i };
                    if (!taken.ContainsKey(TargetKey(device, target)) && allowed(target))
                        return Take(taken, source, input, device, target);
                }
            }
            return null;
        }

        private static VJoySlot Take(Dictionary<string, bool> taken, string source, VJoyInput input, int device, VJoyInput target)
        {
            taken[TargetKey(device, target)] = true;
            return new VJoySlot { Source = source, Input = input.ToSlotToken(), Device = device, Target = target.ToSlotToken() };
        }

        public static string Key(string source, string input)
        {
            return (source ?? string.Empty).Trim() + "\u001F" + Normalize(input);
        }

        private static string TargetKey(int device, VJoyInput target)
        {
            return device.ToString(CultureInfo.InvariantCulture) + ":" + ((int)target.Kind).ToString(CultureInfo.InvariantCulture) + ":" + target.Index.ToString(CultureInfo.InvariantCulture);
        }

        public static string Normalize(string token)
        {
            VJoyInput input;
            return VJoyInput.TryParseSlotToken(token, out input) ? input.ToSlotToken() : (token ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static int OrderOf(string token)
        {
            VJoyInput input;
            if (!VJoyInput.TryParseSlotToken(token, out input))
                return int.MaxValue;
            return (int)input.Kind * 1000 + input.Index;
        }

        public static string KindName(VJoyInputKind kind)
        {
            return kind == VJoyInputKind.Axis ? "axis" : kind == VJoyInputKind.Button ? "button" : "hat";
        }
    }

    internal static class VJoyConfigArgs
    {
        private static readonly string[] AxisNames = { "x", "y", "z", "rx", "ry", "rz", "sl0", "sl1" };

        public static string AxisName(int axis)
        {
            return axis >= 0 && axis < AxisNames.Length ? AxisNames[axis] : "?";
        }

        public static int AxisIndex(string name)
        {
            return Array.IndexOf(AxisNames, (name ?? string.Empty).Trim().ToLowerInvariant());
        }

        // "vJoyConfig Dn -f -a <axes> -b n -p m [-e all]" (vJoyConfig -h of vJoy 2.1.9): -f replaces the device.
        public static List<string> Create(VJoyDeviceCaps caps)
        {
            List<string> args = new List<string> { caps.Id.ToString(CultureInfo.InvariantCulture), "-f" };
            if (caps.Axes.Count > 0)
            {
                args.Add("-a");
                args.AddRange(caps.Axes.OrderBy(a => a).Select(AxisName));
            }
            args.Add("-b");
            args.Add(caps.Buttons.ToString(CultureInfo.InvariantCulture));
            args.Add("-p");
            args.Add(caps.ContinuousPovs.ToString(CultureInfo.InvariantCulture));
            if (caps.ForceFeedback)
            {
                args.Add("-e");
                args.Add("all");
            }
            return args;
        }
    }

    // The HID report descriptor the vJoy driver keeps per device (HKLM\SYSTEM\CurrentControlSet\services\vjoy\Parameters
    // \DeviceNN\HidReportDesctiptor, sic): a short-item parser that counts the data inputs (constant padding skipped).
    internal static class VJoyDescriptor
    {
        public static VJoyDeviceCaps Parse(int id, byte[] descriptor)
        {
            VJoyDeviceCaps caps = new VJoyDeviceCaps { Id = id, Configured = descriptor != null && descriptor.Length > 0 };
            if (!caps.Configured)
                return caps;
            int usagePage = 0;
            long logicalMax = 0;
            int reportCount = 0;
            int usageMin = 0;
            int usageMax = 0;
            List<int> usages = new List<int>();
            int i = 0;
            while (i < descriptor.Length)
            {
                byte prefix = descriptor[i];
                if (prefix == 0xFE)
                {
                    // Long item: skip.
                    if (i + 1 >= descriptor.Length)
                        break;
                    i += 3 + descriptor[i + 1];
                    continue;
                }
                int size = prefix & 0x03;
                if (size == 3)
                    size = 4;
                int type = (prefix >> 2) & 0x03;
                int tag = (prefix >> 4) & 0x0F;
                if (i + size >= descriptor.Length)
                    break;
                long data = 0;
                for (int b = 0; b < size && i + 1 + b < descriptor.Length; b++)
                    data |= (long)descriptor[i + 1 + b] << (8 * b);
                i += 1 + size;

                if (type == 1)
                {
                    // Global items.
                    if (tag == 0x0)
                        usagePage = (int)data;
                    else if (tag == 0x2)
                        logicalMax = size == 4 ? (int)data : data;
                    else if (tag == 0x9)
                        reportCount = (int)data;
                }
                else if (type == 2)
                {
                    // Local items.
                    if (tag == 0x0)
                        usages.Add((int)data);
                    else if (tag == 0x1)
                        usageMin = (int)data;
                    else if (tag == 0x2)
                        usageMax = (int)data;
                }
                else if (type == 0)
                {
                    // Main items.
                    if (tag == 0x8)
                    {
                        bool constant = (data & 0x01) != 0;
                        if (!constant)
                        {
                            if (usagePage == 0x09)
                                caps.Buttons += usageMax >= usageMin && usageMax > 0 ? usageMax - usageMin + 1 : reportCount;
                            else if (usagePage == 0x01)
                            {
                                foreach (int usage in usages)
                                {
                                    if (usage >= 0x30 && usage <= 0x37)
                                    {
                                        if (!caps.Axes.Contains(usage - 0x30))
                                            caps.Axes.Add(usage - 0x30);
                                    }
                                    else if (usage == 0x39)
                                    {
                                        if (logicalMax > 3)
                                            caps.ContinuousPovs++;
                                        else
                                            caps.DiscretePovs++;
                                    }
                                }
                            }
                        }
                    }
                    // Every main item ends the local items before it.
                    usages.Clear();
                    usageMin = 0;
                    usageMax = 0;
                }
                // The PID usage page appears only when force feedback effects are configured.
                if (usagePage == 0x0F)
                    caps.ForceFeedback = true;
            }
            caps.Axes.Sort();
            caps.ContinuousPovs = Math.Min(VJoySettings.MaxPovs, caps.ContinuousPovs);
            caps.DiscretePovs = Math.Min(VJoySettings.MaxPovs, caps.DiscretePovs);
            caps.Buttons = Math.Min(VJoySettings.MaxButtons, caps.Buttons);
            return caps;
        }
    }
}
