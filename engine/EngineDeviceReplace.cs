using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using JoyPro.DeviceReference;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    // Replace a device (JoyPro 1's "replace entire joystick device", docs/devices.md "Replace a device"):
    //   - devices auto-remap: a profile device that is not connected while the same product is connected under a new
    //     instance GUID (new PC, Windows reinstall, other USB port) gets that device: same USB id or same name, the
    //     connected device not used in the profile yet, and exactly one such pair (several of a kind are told apart by
    //     the inputs the profile uses). Also what the load correction does (--remap-missing-devices).
    //   - devices replace <old> <new>: moves every bind and modifier of one device to another, input by input: the same
    //     input ids (same product) or by layout (the device catalog's parts: trigger -> trigger, hat 2 -> hat 2, roll ->
    //     roll), with a preview (--dry-run), inputs that do not fit stay on the old device, and a choice when the new
    //     device already has binds (--merge add|keep|replace).
    internal static partial class Program
    {
        // Set by a command that found nothing to change: the profile is then not saved (no empty version).
        private static bool CommandChangedNothing;

        internal sealed class AutoRemapEntry
        {
            public string OldDevice { get; set; }
            public string NewDevice { get; set; }
            public string Reason { get; set; }
            public int Binds { get; set; }
            public int Modifiers { get; set; }
        }

        internal sealed class AutoRemapSkip
        {
            public string OldDevice { get; set; }
            public string Reason { get; set; }
            public List<string> Candidates { get; set; }
        }

        internal sealed class AutoRemapResult
        {
            public bool DryRun { get; set; }
            public bool Applied { get; set; }
            public List<AutoRemapEntry> Remapped { get; set; }
            public List<AutoRemapSkip> Skipped { get; set; }
            public List<string> Missing { get; set; }
            public List<string> Warnings { get; set; }

            public AutoRemapResult()
            {
                Remapped = new List<AutoRemapEntry>();
                Skipped = new List<AutoRemapSkip>();
                Missing = new List<string>();
                Warnings = new List<string>();
            }
        }

        internal sealed class ReplaceMapping
        {
            public string OldInput { get; set; }
            public string NewInput { get; set; }
            public string OldPart { get; set; }
            public string NewPart { get; set; }
            // same input, same part, same kind, by order, same id, yours, none
            public string Method { get; set; }
            public bool Axis { get; set; }
            public List<string> Relations { get; set; }
            public List<string> Modifiers { get; set; }
            // Relations already bound to the new input on the new device.
            public List<string> TargetRelations { get; set; }
            public string Note { get; set; }

            public bool Mapped { get { return !string.IsNullOrEmpty(NewInput); } }
        }

        internal sealed class ReplacePlan
        {
            public string OldDevice { get; set; }
            public string NewDevice { get; set; }
            public string RequestedMode { get; set; }
            public string Mode { get; set; }
            public string ModeReason { get; set; }
            public string OldModel { get; set; }
            public string NewModel { get; set; }
            public bool OldConnected { get; set; }
            public bool NewConnected { get; set; }
            public int OldBinds { get; set; }
            public int OldModifiers { get; set; }
            public int TargetBinds { get; set; }
            public bool NeedsMergeChoice { get; set; }
            public string Merge { get; set; }
            public bool DryRun { get; set; }
            public bool Applied { get; set; }
            public int MovedBinds { get; set; }
            public int KeptOnOld { get; set; }
            public int RemovedTargetBinds { get; set; }
            public int MappedInputs { get; set; }
            public int UnmappedInputs { get; set; }
            public List<ReplaceMapping> Mappings { get; set; }
            public List<string> NewInputs { get; set; }
            public List<string> Steps { get; set; }
            public List<string> Warnings { get; set; }

            public ReplacePlan()
            {
                OldDevice = NewDevice = RequestedMode = Mode = ModeReason = OldModel = NewModel = Merge = string.Empty;
                Mappings = new List<ReplaceMapping>();
                NewInputs = new List<string>();
                Steps = new List<string>();
                Warnings = new List<string>();
            }
        }

        // What the profile uses of one device instance.
        private sealed class DeviceUse
        {
            public string Device = string.Empty;
            public int Binds;
            public int Modifiers;
            public HashSet<string> Inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // ---- device identity ----------------------------------------------------------------------------------------

        private static string ReplaceNameOf(string device)
        {
            return CollapseSpaces(StripDeviceGuidSuffix(CollapseSpaces(device)));
        }

        private static string ReplaceNameKey(string device)
        {
            return new string(ReplaceNameOf(device).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        private static bool TryDeviceGuid(string device, out Guid guid)
        {
            string name;
            return GameDeviceDirectory.TryParseJoyProName(CollapseSpaces(device), out name, out guid);
        }

        // The same device instance: the same instance GUID when both have one, else the same name.
        private static bool ReplaceSameInstance(string a, string b)
        {
            Guid ga, gb;
            bool hasA = TryDeviceGuid(a, out ga), hasB = TryDeviceGuid(b, out gb);
            if (hasA && hasB)
                return ga == gb;
            if (hasA != hasB)
                return false;
            return string.Equals(CollapseSpaces(a), CollapseSpaces(b), StringComparison.OrdinalIgnoreCase);
        }

        // VID:PID the profile remembers for a device (its product GUID, JoyPro 1 profiles and remapped devices).
        private static string RememberedVidPid(string device)
        {
            if (InternalDataManagement.LocalJoystickPGUID == null)
                return string.Empty;
            foreach (KeyValuePair<string, string> entry in InternalDataManagement.LocalJoystickPGUID)
            {
                if (!ReplaceSameInstance(entry.Key, device))
                    continue;
                Guid product;
                int vid, pid;
                if (Guid.TryParse((entry.Value ?? string.Empty).Trim().Trim('{', '}'), out product) && GameDeviceDirectory.TryGetVidPid(product, out vid, out pid))
                    return GameDeviceDirectory.VidPidKey(vid, pid);
            }
            return string.Empty;
        }

        private static string VidPidOf(GameDevice device)
        {
            return device != null && device.HasVidPid ? GameDeviceDirectory.VidPidKey(device.VendorId, device.ProductId) : string.Empty;
        }

        private static string DeviceNameOf(GameDevice device)
        {
            return device.InstanceName.Length > 0 ? device.InstanceName : ReplaceNameOf(device.JoyProName);
        }

        private static GameDevice ConnectedDevice(GameDeviceDirectory directory, string device)
        {
            return directory.Devices.FirstOrDefault(d => d.Connected && ReplaceSameInstance(d.JoyProName, device));
        }

        // Every device instance the profile uses (binds, modifiers on binds) with the inputs it uses.
        private static List<DeviceUse> ProfileDeviceUse()
        {
            List<DeviceUse> uses = new List<DeviceUse>();
            Func<string, DeviceUse> useOf = device =>
            {
                DeviceUse use = uses.FirstOrDefault(u => ReplaceSameInstance(u.Device, device));
                if (use == null)
                {
                    use = new DeviceUse { Device = device.Trim() };
                    uses.Add(use);
                }
                return use;
            };
            foreach (Bind bind in InternalDataManagement.AllBinds.Values)
            {
                if (bind == null)
                    continue;
                if (!string.IsNullOrWhiteSpace(bind.Joystick) && !IsKeyboardDevice(bind.Joystick))
                {
                    DeviceUse use = useOf(bind.Joystick);
                    use.Binds++;
                    string input = BindInput(bind);
                    if (input.Length > 0)
                        use.Inputs.Add(input);
                }
                foreach (string reformer in bind.AllReformers ?? new List<string>())
                {
                    Modifier modifier = Modifier.ReformerToMod(reformer);
                    if (modifier == null || string.IsNullOrWhiteSpace(modifier.device) || IsKeyboardDevice(modifier.device))
                        continue;
                    DeviceUse use = useOf(modifier.device);
                    if (!string.IsNullOrWhiteSpace(modifier.key))
                        use.Inputs.Add(modifier.key.Trim());
                }
            }
            foreach (Modifier modifier in InternalDataManagement.AllModifiers.Values)
            {
                if (modifier == null || string.IsNullOrWhiteSpace(modifier.device) || IsKeyboardDevice(modifier.device))
                    continue;
                DeviceUse use = useOf(modifier.device);
                use.Modifiers++;
                if (!string.IsNullOrWhiteSpace(modifier.key))
                    use.Inputs.Add(modifier.key.Trim());
            }
            return uses;
        }

        private static bool IsKeyboardDevice(string device)
        {
            string d = (device ?? string.Empty).Trim();
            return d.Length == 0 || string.Equals(d, "Keyboard", StringComparison.OrdinalIgnoreCase) || string.Equals(d, "Mouse", StringComparison.OrdinalIgnoreCase);
        }

        private static string BindInput(Bind bind)
        {
            string axis = (bind.JAxis ?? string.Empty).Trim();
            return axis.Length > 0 ? axis : (bind.JButton ?? string.Empty).Trim();
        }

        // The device reports the input (unknown capabilities count as yes).
        private static bool DeviceHasInput(GameDevice device, string input)
        {
            if (device == null || !device.CapabilitiesKnown)
                return true;
            JoyInput parsed;
            if (!GameInputTokens.TryParse(input, out parsed))
                return true;
            switch (parsed.Kind)
            {
                case JoyInputKind.Axis: return device.AxisSlots.Contains(parsed.Index);
                case JoyInputKind.Button: return parsed.Index < device.ButtonCount;
                case JoyInputKind.Pov: return parsed.Index < device.PovCount;
                default: return true;
            }
        }

        // ---- auto remap ------------------------------------------------------------------------------------------------

        internal static AutoRemapResult FindAutoRemaps(GameDeviceDirectory directory, List<string> warnings)
        {
            AutoRemapResult result = new AutoRemapResult();
            List<DeviceUse> uses = ProfileDeviceUse();
            List<GameDevice> connected = directory.Devices.Where(d => d.Connected).ToList();
            List<DeviceUse> missing = uses.Where(u => connected.All(c => !ReplaceSameInstance(c.JoyProName, u.Device))).ToList();
            List<GameDevice> unused = connected.Where(c => uses.All(u => !ReplaceSameInstance(c.JoyProName, u.Device))).ToList();
            result.Missing = missing.Select(m => m.Device).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();

            Dictionary<DeviceUse, List<GameDevice>> pairs = new Dictionary<DeviceUse, List<GameDevice>>();
            Dictionary<DeviceUse, string> reasons = new Dictionary<DeviceUse, string>();
            foreach (DeviceUse old in missing)
            {
                Guid guid;
                if (!TryDeviceGuid(old.Device, out guid))
                    continue;
                string key = ReplaceNameKey(old.Device);
                string vidPid = RememberedVidPid(old.Device);
                List<GameDevice> byName = unused.Where(c => key.Length > 0 && (ReplaceNameKey(c.JoyProName) == key ||
                                                                             new string(c.HidProductString.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant() == key)).ToList();
                List<GameDevice> candidates = byName;
                string reason = "same name";
                if (vidPid.Length > 0)
                {
                    List<GameDevice> byId = unused.Where(c => VidPidOf(c) == vidPid).ToList();
                    if (byName.Count > 0)
                    {
                        // A remembered USB id that differs from the connected device of the same name: another product.
                        List<GameDevice> both = byName.Where(c => !c.HasVidPid || VidPidOf(c) == vidPid).ToList();
                        candidates = both;
                        reason = "same name and USB id " + vidPid;
                    }
                    else
                    {
                        candidates = byId;
                        reason = "same USB id " + vidPid;
                    }
                }
                if (candidates.Count == 0)
                    continue;
                if (candidates.Count > 1)
                {
                    // Several of a kind (a keyboard's game controller interfaces, two identical pedals): the ones that
                    // have every input the profile uses.
                    foreach (GameDevice candidate in candidates)
                        directory.EnsureCapabilities(candidate, warnings);
                    List<GameDevice> fitting = candidates.Where(c => c.CapabilitiesKnown && old.Inputs.All(i => DeviceHasInput(c, i)) &&
                                                                     (c.ButtonCount > 0 || c.AxisSlots.Count > 0 || c.PovCount > 0)).ToList();
                    if (fitting.Count == 1)
                    {
                        reason += ", the only one with the inputs the profile uses";
                        candidates = fitting;
                    }
                    else
                    {
                        result.Skipped.Add(new AutoRemapSkip
                        {
                            OldDevice = old.Device,
                            Reason = candidates.Count + " connected devices of this kind fit; pick one with Replace device",
                            Candidates = candidates.Select(c => c.JoyProName).ToList()
                        });
                        continue;
                    }
                }
                pairs[old] = candidates;
                reasons[old] = reason;
            }

            foreach (KeyValuePair<DeviceUse, List<GameDevice>> pair in pairs)
            {
                GameDevice target = pair.Value[0];
                List<DeviceUse> rivals = pairs.Where(p => p.Key != pair.Key && p.Value[0] == target).Select(p => p.Key).ToList();
                if (rivals.Count > 0)
                {
                    result.Skipped.Add(new AutoRemapSkip
                    {
                        OldDevice = pair.Key.Device,
                        Reason = (rivals.Count + 1) + " old devices of this kind in the profile, one connected; pick which one it is with Replace device",
                        Candidates = new List<string> { target.JoyProName }
                    });
                    continue;
                }
                result.Remapped.Add(new AutoRemapEntry
                {
                    OldDevice = pair.Key.Device,
                    NewDevice = target.JoyProName,
                    Reason = reasons[pair.Key],
                    Binds = pair.Key.Binds,
                    Modifiers = pair.Key.Modifiers
                });
            }
            result.Remapped = result.Remapped.OrderBy(r => r.OldDevice, StringComparer.OrdinalIgnoreCase).ToList();
            result.Warnings.AddRange(warnings ?? new List<string>());
            return result;
        }

        internal static void ApplyAutoRemap(AutoRemapEntry entry, GameDeviceDirectory directory)
        {
            ExchangeDeviceReferences(entry.OldDevice, entry.NewDevice);
            RememberProductGuid(entry.NewDevice, ConnectedDevice(directory, entry.NewDevice));
        }

        // The new device's product GUID (USB id), so a later remap knows the product even when it is not connected.
        private static void RememberProductGuid(string device, GameDevice connected)
        {
            if (connected == null || connected.ProductGuid == Guid.Empty || InternalDataManagement.LocalJoystickPGUID == null)
                return;
            foreach (string key in InternalDataManagement.LocalJoystickPGUID.Keys.Where(k => ReplaceSameInstance(k, device)).ToList())
                InternalDataManagement.LocalJoystickPGUID.Remove(key);
            InternalDataManagement.LocalJoystickPGUID[device] = connected.ProductGuid.ToString("B").ToUpperInvariant();
        }

        // ---- replace plan ----------------------------------------------------------------------------------------------

        private sealed class LayoutControl
        {
            public string Input = string.Empty;
            public string Control = string.Empty;
            public string Element = string.Empty;
            public string Direction = string.Empty;
            public string Type = string.Empty;
            public int Ordinal;
            public string Label = string.Empty;
        }

        private sealed class DeviceLayout
        {
            public string ModelName = string.Empty;
            public string ModelId = string.Empty;
            public bool Generic;
            public List<LayoutControl> Controls = new List<LayoutControl>();

            public LayoutControl ByInput(string input)
            {
                return Controls.FirstOrDefault(c => string.Equals(c.Input, input, StringComparison.OrdinalIgnoreCase));
            }
        }

        // The device's parts: its catalog model (with the base of a grip), or a generic layout from what it reports or,
        // for a device that is not connected, from the inputs the profile uses.
        private static DeviceLayout LayoutOf(string device, GameDevice connected, IEnumerable<string> usedInputs, IDictionary<string, string> grips, List<string> warnings)
        {
            GameDevice probe = connected;
            if (probe == null)
            {
                probe = new GameDevice { JoyProName = device, InstanceName = ReplaceNameOf(device), HidProductString = ReplaceNameOf(device), ProductName = ReplaceNameOf(device), Connected = true };
                string vidPid = RememberedVidPid(device);
                int vid, pid;
                if (vidPid.Length == 9 && int.TryParse(vidPid.Substring(0, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid) &&
                    int.TryParse(vidPid.Substring(5, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid))
                {
                    probe.VendorId = vid;
                    probe.ProductId = pid;
                }
                List<int> axes = new List<int>();
                int buttons = 0, povs = 0;
                foreach (string input in usedInputs ?? Enumerable.Empty<string>())
                {
                    JoyInput parsed;
                    if (!GameInputTokens.TryParse(input, out parsed))
                        continue;
                    if (parsed.Kind == JoyInputKind.Axis && !axes.Contains(parsed.Index))
                        axes.Add(parsed.Index);
                    else if (parsed.Kind == JoyInputKind.Button)
                        buttons = Math.Max(buttons, parsed.Index + 1);
                    else if (parsed.Kind == JoyInputKind.Pov)
                        povs = Math.Max(povs, parsed.Index + 1);
                }
                probe.AxisSlots = axes.OrderBy(a => a).ToList();
                probe.ButtonCount = buttons;
                probe.PovCount = povs;
                probe.CapabilitiesKnown = true;
            }

            GameDeviceDirectory single = new GameDeviceDirectory();
            single.Devices.Add(probe);
            KbDevice model = null, baseModel = null;
            bool generic = false;
            try
            {
                RosterDevice entry = EngineDeviceKb.BuildRoster(single, grips, null, null, warnings).FirstOrDefault();
                if (entry != null && entry.Model != null)
                {
                    model = entry.Model;
                    baseModel = entry.BaseModel;
                    generic = entry.Generic;
                }
                else if (entry != null && entry.GripChoices.Count > 0)
                {
                    model = entry.GripChoices[0];
                    baseModel = model.IsGrip ? EngineDeviceKb.Find(model.Base, warnings) : null;
                    warnings.Add(ReplaceNameOf(device) + ": the grip is not chosen; JoyPro assumes " + model.Name + " (pick yours on the Layout page or with --grip).");
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Device catalog: " + ex.Message);
            }
            if ((model == null || generic) && connected == null)
            {
                // A device that is not connected has no USB id here; DirectInput names it differently from its HID name
                // ("Joystick - HOTAS Warthog" is "HOTAS Warthog Joystick"): the same words name the model.
                KbDevice byWords = ModelByWords(ReplaceNameOf(device), warnings);
                if (byWords != null)
                {
                    model = byWords;
                    baseModel = model.IsGrip ? EngineDeviceKb.Find(model.Base, warnings) : null;
                    generic = false;
                }
            }
            if (model == null)
            {
                single.EnsureCapabilities(probe, warnings);
                model = EngineDeviceKb.GenericModel(probe);
                generic = true;
            }

            DeviceLayout layout = new DeviceLayout { ModelName = generic ? "generic layout (" + model.Kind + ")" : model.Name, ModelId = generic ? string.Empty : model.Id, Generic = generic };
            IEnumerable<KbControl> all = model.Axes.Concat(model.Controls)
                .Concat(baseModel == null ? Enumerable.Empty<KbControl>() : baseModel.Axes.Concat(baseModel.Controls));
            Dictionary<string, List<string>> elementsByType = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (KbControl control in all)
            {
                if (string.IsNullOrWhiteSpace(control.Input) || layout.ByInput(control.Input) != null)
                    continue;
                string direction;
                string element = DeviceVocabulary.ElementOf(control.Control, out direction);
                string type = element.Length > 0 ? DeviceVocabulary.ElementType(element) : string.Empty;
                bool axisInput = IsAxisInput(control.Input);
                if (axisInput && type != "gimbal" && type != "ministick")
                {
                    // An axis is a part of its own ("throttle.axis.left" is the left lever, not a direction).
                    element = (control.Control ?? string.Empty).ToLowerInvariant();
                    direction = string.Empty;
                    type = element.Length > 0 ? DeviceVocabulary.ElementType(element) : string.Empty;
                }
                else if (!axisInput && type == "ministick")
                {
                    // A four-way ministick that reports buttons or a POV works like a hat.
                    type = "hat";
                }
                if (type.Length > 0)
                {
                    List<string> list;
                    if (!elementsByType.TryGetValue(type, out list))
                        elementsByType[type] = list = new List<string>();
                    if (!list.Contains(element))
                        list.Add(element);
                }
                layout.Controls.Add(new LayoutControl
                {
                    Input = control.Input.Trim(),
                    Control = control.Control ?? string.Empty,
                    Element = element,
                    Direction = direction,
                    Type = type,
                    Label = control.Label ?? string.Empty
                });
            }
            // Order of each kind of part as the catalog lists them (hat 1, hat 2, ...; left brake before right brake).
            foreach (LayoutControl control in layout.Controls)
            {
                List<string> list;
                control.Ordinal = control.Type.Length > 0 && elementsByType.TryGetValue(control.Type, out list) ? list.IndexOf(control.Element) : -1;
            }
            // POV hats the device reports that the catalog does not list: hats of their own, after the listed ones.
            if (probe.CapabilitiesKnown)
            {
                string[] directions = { "up", "right", "down", "left" };
                for (int hat = 0; hat < probe.PovCount; hat++)
                {
                    if (layout.ByInput(GameInputTokens.PovToken(hat, 0)) != null)
                        continue;
                    string element = "pov." + (hat + 1).ToString(CultureInfo.InvariantCulture);
                    for (int d = 0; d < 4; d++)
                        layout.Controls.Add(new LayoutControl { Input = GameInputTokens.PovToken(hat, d * 2), Control = element, Element = element, Direction = directions[d], Type = "hat", Ordinal = 100 + hat });
                }
            }
            return layout;
        }

        private static KbDevice ModelByWords(string name, List<string> warnings)
        {
            Func<string, string> words = text => string.Join(" ", System.Text.RegularExpressions.Regex.Split((text ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]+")
                .Where(w => w.Length > 0).OrderBy(w => w, StringComparer.Ordinal));
            string key = words(name);
            if (key.Length == 0)
                return null;
            List<KbDevice> found = EngineDeviceKb.Load(warnings).Where(k => k.Kind != "base" && k.Names.Concat(new[] { k.Name }).Any(n => words(n) == key)).ToList();
            // Grips that report the stock product's name (the Warthog base takes other grips): the stock product.
            if (found.Count > 1 && found.Count(k => !k.IsGrip) == 1)
                return found.First(k => !k.IsGrip);
            return found.Count == 1 ? found[0] : null;
        }

        private static string PartName(LayoutControl control)
        {
            if (control == null || control.Control.Length == 0)
                return string.Empty;
            if (control.Control.StartsWith("pov.", StringComparison.Ordinal))
                return "POV hat " + control.Control.Substring(4) + " " + control.Direction;
            return DeviceVocabulary.Describe(control.Control, control.Label);
        }

        private static bool IsAxisInput(string input)
        {
            JoyInput parsed;
            return GameInputTokens.TryParse(input, out parsed) && parsed.Kind == JoyInputKind.Axis;
        }

        internal static ReplacePlan PlanDeviceReplace(string oldDevice, string newDevice, string mode, IDictionary<string, string> overrides, IDictionary<string, string> grips, GameDeviceDirectory directory, List<string> warnings)
        {
            ReplacePlan plan = new ReplacePlan { OldDevice = oldDevice, NewDevice = newDevice, RequestedMode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant() };
            List<DeviceUse> uses = ProfileDeviceUse();
            DeviceUse oldUse = uses.FirstOrDefault(u => ReplaceSameInstance(u.Device, oldDevice)) ?? new DeviceUse { Device = oldDevice };
            GameDevice oldConnected = ConnectedDevice(directory, oldDevice);
            GameDevice newConnected = ConnectedDevice(directory, newDevice);
            plan.OldConnected = oldConnected != null;
            plan.NewConnected = newConnected != null;
            plan.OldBinds = oldUse.Binds;
            plan.OldModifiers = oldUse.Modifiers;
            if (newConnected != null)
                directory.EnsureCapabilities(newConnected, warnings);

            DeviceLayout oldLayout = LayoutOf(oldDevice, oldConnected, oldUse.Inputs, grips, warnings);
            DeviceUse newUse = uses.FirstOrDefault(u => ReplaceSameInstance(u.Device, newDevice));
            DeviceLayout newLayout = LayoutOf(newDevice, newConnected, newUse == null ? null : newUse.Inputs, grips, warnings);
            plan.OldModel = oldLayout.ModelName;
            plan.NewModel = newLayout.ModelName;

            string oldVidPid = oldConnected != null ? VidPidOf(oldConnected) : RememberedVidPid(oldDevice);
            string newVidPid = newConnected != null ? VidPidOf(newConnected) : RememberedVidPid(newDevice);
            bool sameProduct = ReplaceNameKey(oldDevice) == ReplaceNameKey(newDevice) && (oldVidPid.Length == 0 || newVidPid.Length == 0 || oldVidPid == newVidPid) ||
                               oldVidPid.Length > 0 && oldVidPid == newVidPid ||
                               oldLayout.ModelId.Length > 0 && oldLayout.ModelId == newLayout.ModelId;
            if (plan.RequestedMode == "same" || plan.RequestedMode == "layout")
            {
                plan.Mode = plan.RequestedMode;
                plan.ModeReason = plan.Mode == "same" ? "you chose the same input ids" : "you chose to map by layout";
            }
            else
            {
                plan.Mode = sameProduct ? "same" : "layout";
                plan.ModeReason = sameProduct ? "the same product: every input keeps its id" : "a different device: inputs are mapped by their part on the device";
            }

            // Inputs of the new device (for the preview's choices): what it reports, else its layout.
            List<string> newInputs = new List<string>();
            if (newConnected != null && newConnected.CapabilitiesKnown)
            {
                newInputs.AddRange(newConnected.AxisSlots.Distinct().OrderBy(a => a).Select(GameInputTokens.AxisToken));
                for (int b = 0; b < newConnected.ButtonCount; b++)
                    newInputs.Add(GameInputTokens.ButtonToken(b));
                for (int h = 0; h < newConnected.PovCount; h++)
                    for (int d = 0; d < 8; d += 2)
                        newInputs.Add(GameInputTokens.PovToken(h, d));
            }
            foreach (LayoutControl control in newLayout.Controls)
            {
                if (!newInputs.Contains(control.Input, StringComparer.OrdinalIgnoreCase))
                    newInputs.Add(control.Input);
            }
            plan.NewInputs = newInputs;

            // Who uses which old input.
            Dictionary<string, ReplaceMapping> mappings = new Dictionary<string, ReplaceMapping>(StringComparer.OrdinalIgnoreCase);
            Func<string, ReplaceMapping> mappingOf = input =>
            {
                ReplaceMapping m;
                if (!mappings.TryGetValue(input, out m))
                {
                    m = new ReplaceMapping { OldInput = input, NewInput = string.Empty, Method = "none", Axis = IsAxisInput(input), Relations = new List<string>(), Modifiers = new List<string>(), TargetRelations = new List<string>(), Note = string.Empty };
                    m.OldPart = PartName(OldControlOf(oldLayout, input));
                    mappings[input] = m;
                }
                return m;
            };
            foreach (KeyValuePair<string, Bind> entry in InternalDataManagement.AllBinds)
            {
                Bind bind = entry.Value;
                if (bind == null)
                    continue;
                if (ReplaceSameInstance(bind.Joystick, oldDevice) && BindInput(bind).Length > 0)
                    mappingOf(BindInput(bind)).Relations.Add(entry.Key);
                foreach (string reformer in bind.AllReformers ?? new List<string>())
                {
                    Modifier modifier = Modifier.ReformerToMod(reformer);
                    if (modifier != null && ReplaceSameInstance(modifier.device, oldDevice) && !string.IsNullOrWhiteSpace(modifier.key))
                    {
                        ReplaceMapping m = mappingOf(modifier.key.Trim());
                        if (!m.Modifiers.Contains(modifier.name, StringComparer.OrdinalIgnoreCase))
                            m.Modifiers.Add(modifier.name);
                    }
                }
            }

            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<ReplaceMapping> ordered = mappings.Values.OrderBy(m => m.Axis ? 0 : 1).ThenBy(m => DeviceVocabulary.NaturalKey(m.OldInput), StringComparer.Ordinal).ToList();
            Action<ReplaceMapping, string, string, string> assign = (m, input, method, note) =>
            {
                m.NewInput = input;
                m.Method = method;
                m.Note = note ?? string.Empty;
                taken.Add(input);
            };
            Func<ReplaceMapping, string, bool> fits = (m, input) =>
                !string.IsNullOrEmpty(input) && !taken.Contains(input) && IsAxisInput(input) == m.Axis && (newConnected == null || DeviceHasInput(newConnected, input));

            // Yours first (--map OLD=NEW, the preview's choices).
            foreach (ReplaceMapping m in ordered)
            {
                string mine;
                if (overrides != null && overrides.TryGetValue(m.OldInput, out mine))
                {
                    if (string.IsNullOrWhiteSpace(mine) || string.Equals(mine, "none", StringComparison.OrdinalIgnoreCase))
                        m.Method = "yours";
                    else if (IsAxisInput(mine) != m.Axis)
                        warnings.Add(m.OldInput + " -> " + mine + ": an axis can only go to an axis, a button only to a button or hat.");
                    else
                        assign(m, mine.Trim().ToUpperInvariant(), "yours", "chosen by you");
                }
            }

            if (plan.Mode == "same")
            {
                foreach (ReplaceMapping m in ordered.Where(x => x.Method == "none"))
                {
                    if (fits(m, m.OldInput))
                        assign(m, m.OldInput, "same input", string.Empty);
                    else
                        m.Note = "the new device has no " + m.OldInput;
                }
            }
            else
            {
                MapByLayout(ordered, oldLayout, newLayout, fits, assign);
            }

            foreach (ReplaceMapping m in ordered)
            {
                m.NewPart = m.Mapped ? PartName(newLayout.ByInput(m.NewInput)) : string.Empty;
                if (!m.Mapped && m.Note.Length == 0 && m.Method != "yours")
                    m.Note = "no matching part on the new device";
                if (!m.Mapped && m.Method == "yours")
                    m.Note = "left out by you";
                if (m.Mapped)
                {
                    m.TargetRelations = InternalDataManagement.AllBinds
                        .Where(b => b.Value != null && ReplaceSameInstance(b.Value.Joystick, newDevice) && string.Equals(BindInput(b.Value), m.NewInput, StringComparison.OrdinalIgnoreCase))
                        .Select(b => b.Key).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
            plan.Mappings = ordered;
            plan.MappedInputs = ordered.Count(m => m.Mapped);
            plan.UnmappedInputs = ordered.Count(m => !m.Mapped);
            plan.TargetBinds = newUse == null ? 0 : newUse.Binds;
            plan.NeedsMergeChoice = plan.TargetBinds > 0;
            plan.Warnings.AddRange(warnings.Distinct());
            return plan;
        }

        // By layout: the same part (control id), the same part on the other family's device (stick.* vs throttle.*),
        // the same kind of part in the same order with the same direction (hat 2 up -> hat 2 up, left brake -> left
        // brake), buttons in order as a guess, and the same input id when nothing else fits.
        private static void MapByLayout(List<ReplaceMapping> mappings, DeviceLayout oldLayout, DeviceLayout newLayout,
            Func<ReplaceMapping, string, bool> fits, Action<ReplaceMapping, string, string, string> assign)
        {
            Func<string, string> withoutFamily = control =>
            {
                int dot = (control ?? string.Empty).IndexOf('.');
                return dot > 0 ? control.Substring(dot + 1).ToLowerInvariant() : (control ?? string.Empty).ToLowerInvariant();
            };
            // Which new part each old part went to: all directions of a hat go to one hat.
            Dictionary<string, string> partMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> usedParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Func<ReplaceMapping, LayoutControl> oldOf = m => OldControlOf(oldLayout, m.OldInput);

            // 1. The same part: the same control id, or the same id on the other family's device.
            List<Func<LayoutControl, LayoutControl, bool>> exact = new List<Func<LayoutControl, LayoutControl, bool>>
            {
                (o, n) => string.Equals(o.Control, n.Control, StringComparison.OrdinalIgnoreCase),
                (o, n) => withoutFamily(o.Control) == withoutFamily(n.Control)
            };
            foreach (Func<LayoutControl, LayoutControl, bool> tier in exact)
            {
                foreach (ReplaceMapping m in mappings.Where(x => x.Method == "none"))
                {
                    LayoutControl old = oldOf(m);
                    if (old == null || old.Control.Length == 0 || old.Direction.StartsWith("diagonal", StringComparison.Ordinal))
                        continue;
                    LayoutControl match = newLayout.Controls.FirstOrDefault(n => n.Control.Length > 0 && tier(old, n) && fits(m, n.Input));
                    if (match == null)
                        continue;
                    assign(m, match.Input, "same part", string.Empty);
                    if (!partMap.ContainsKey(old.Element))
                        partMap[old.Element] = match.Element;
                    usedParts.Add(match.Element);
                }
            }

            // 2. The same kind of part (hat, trigger, lever, brake, ...): each old part to one new part of its kind, the
            //    same place in the order first, then the one that has the most of its directions.
            List<string> oldParts = mappings.Select(oldOf).Where(o => o != null && o.Type.Length > 0 && o.Type != "button")
                .OrderBy(o => o.Ordinal).Select(o => o.Element).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string part in oldParts)
            {
                List<ReplaceMapping> open = mappings.Where(x => x.Method == "none" && oldOf(x) != null && string.Equals(oldOf(x).Element, part, StringComparison.OrdinalIgnoreCase)).ToList();
                if (open.Count == 0)
                    continue;
                LayoutControl sample = oldOf(open[0]);
                string target;
                if (!partMap.TryGetValue(part, out target))
                {
                    target = newLayout.Controls
                        .Where(n => n.Type == sample.Type && n.Element.Length > 0 && !usedParts.Contains(n.Element))
                        .GroupBy(n => n.Element, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new
                        {
                            Element = g.Key,
                            Ordinal = g.First().Ordinal,
                            Covered = open.Count(x => g.Any(n => DirectionMatches(oldOf(x), n) && fits(x, PovInput(x.OldInput, oldOf(x), n))))
                        })
                        .Where(c => c.Covered > 0)
                        .OrderByDescending(c => c.Ordinal == sample.Ordinal ? 1 : 0)
                        .ThenByDescending(c => c.Covered)
                        .ThenBy(c => c.Ordinal)
                        .Select(c => c.Element)
                        .FirstOrDefault();
                    if (target == null)
                        continue;
                    partMap[part] = target;
                    usedParts.Add(target);
                }
                foreach (ReplaceMapping m in open)
                {
                    LayoutControl old = oldOf(m);
                    LayoutControl match = newLayout.Controls.FirstOrDefault(n => string.Equals(n.Element, target, StringComparison.OrdinalIgnoreCase) &&
                                                                                 DirectionMatches(old, n) && fits(m, PovInput(m.OldInput, old, n)));
                    if (match != null)
                        assign(m, PovInput(m.OldInput, old, match), "same kind", DeviceVocabulary.ElementName(old.Element) + " -> " + DeviceVocabulary.ElementName(match.Element));
                }
            }

            // 3. Buttons in their order (a guess the preview shows as such).
            foreach (ReplaceMapping m in mappings.Where(x => x.Method == "none"))
            {
                LayoutControl old = oldOf(m);
                if (old == null || (old.Type != "button" && old.Type != "release" && old.Type != "paddle"))
                    continue;
                LayoutControl match = newLayout.Controls.FirstOrDefault(n => n.Type == "button" && n.Ordinal == old.Ordinal && !usedParts.Contains(n.Element) && fits(m, n.Input))
                                      ?? newLayout.Controls.FirstOrDefault(n => n.Type == "button" && !usedParts.Contains(n.Element) && fits(m, n.Input));
                if (match == null)
                    continue;
                usedParts.Add(match.Element);
                assign(m, match.Input, "by order", "a guess: no part of this kind on the new device matched");
            }

            foreach (ReplaceMapping m in mappings.Where(x => x.Method == "none"))
            {
                if (fits(m, m.OldInput))
                    assign(m, m.OldInput, "same id", "no matching part found; the same input id");
            }
        }

        // A new control is the old one's direction in its part (a hat's diagonal goes to the up input of a POV hat).
        private static bool DirectionMatches(LayoutControl old, LayoutControl candidate)
        {
            if (old.Direction.StartsWith("diagonal", StringComparison.Ordinal))
            {
                JoyInput parsed;
                return string.Equals(candidate.Direction, "up", StringComparison.OrdinalIgnoreCase) && GameInputTokens.TryParse(candidate.Input, out parsed) && parsed.Kind == JoyInputKind.Pov;
            }
            string a = NormalDirection(old.Direction), b = NormalDirection(candidate.Direction);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalDirection(string direction)
        {
            switch ((direction ?? string.Empty).ToLowerInvariant())
            {
                case "push": return "press";
                case "fwd": return "up";
                case "aft": return "down";
                case "ul": return "upleft";
                case "ur": return "upright";
                case "dl": return "downleft";
                case "dr": return "downright";
                default: return (direction ?? string.Empty).ToLowerInvariant();
            }
        }

        // The old input's part: its own entry, or for a hat's diagonal (POV1_UR) the hat it belongs to.
        private static LayoutControl OldControlOf(DeviceLayout layout, string input)
        {
            LayoutControl own = layout.ByInput(input);
            if (own != null)
                return own;
            JoyInput parsed;
            if (!GameInputTokens.TryParse(input, out parsed) || parsed.Kind != JoyInputKind.Pov)
                return null;
            LayoutControl up = layout.ByInput(GameInputTokens.PovToken(parsed.Index, 0));
            if (up != null)
                return new LayoutControl { Input = input, Control = up.Control, Element = up.Element, Direction = "diagonal" + parsed.Direction, Type = up.Type, Ordinal = up.Ordinal, Label = up.Label };
            // A POV hat the catalog does not list: a hat of its own, after the listed ones.
            string[] directions = { "up", null, "right", null, "down", null, "left", null };
            return new LayoutControl
            {
                Input = input,
                Control = "pov." + (parsed.Index + 1).ToString(CultureInfo.InvariantCulture),
                Element = "pov." + (parsed.Index + 1).ToString(CultureInfo.InvariantCulture),
                Direction = directions[parsed.Direction] ?? "diagonal" + parsed.Direction,
                Type = "hat",
                Ordinal = 100 + parsed.Index
            };
        }

        // A hat's diagonal goes to the same diagonal of the matching POV hat; everything else to the matched input.
        private static string PovInput(string oldInput, LayoutControl old, LayoutControl match)
        {
            JoyInput o, n;
            if (old.Direction.StartsWith("diagonal", StringComparison.Ordinal) && GameInputTokens.TryParse(oldInput, out o) && o.Kind == JoyInputKind.Pov &&
                GameInputTokens.TryParse(match.Input, out n) && n.Kind == JoyInputKind.Pov)
                return GameInputTokens.PovToken(n.Index, o.Direction);
            return match.Input;
        }

        // ---- apply ------------------------------------------------------------------------------------------------------

        // merge: add (both stay where they overlap), keep (the new device's binds win: an old bind on an input the new
        // device already uses stays on the old device), replace (the old device's binds win: the new device's binds on
        // those inputs are removed).
        internal static void ApplyDeviceReplace(ReplacePlan plan, string merge)
        {
            plan.Merge = string.IsNullOrWhiteSpace(merge) ? "add" : merge.Trim().ToLowerInvariant();
            Dictionary<string, ReplaceMapping> byInput = plan.Mappings.Where(m => m.Mapped).ToDictionary(m => m.OldInput, StringComparer.OrdinalIgnoreCase);
            HashSet<string> removeTarget = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (plan.Merge == "replace")
            {
                foreach (ReplaceMapping m in byInput.Values)
                    removeTarget.UnionWith(m.TargetRelations);
            }

            foreach (string relationName in removeTarget)
            {
                Bind bind;
                if (!InternalDataManagement.AllBinds.TryGetValue(relationName, out bind) || bind == null || !ReplaceSameInstance(bind.Joystick, plan.NewDevice))
                    continue;
                InternalDataManagement.AllBinds.Remove(relationName);
                if (InternalDataManagement.AllRelations.ContainsKey(relationName) && InternalDataManagement.AllRelations[relationName] != null)
                    InternalDataManagement.AllRelations[relationName].bind = null;
                plan.RemovedTargetBinds++;
            }

            foreach (string relationName in InternalDataManagement.AllBinds.Keys.ToList())
            {
                Bind bind = InternalDataManagement.AllBinds[relationName];
                if (bind == null)
                    continue;
                bool changed = false;
                if (ReplaceSameInstance(bind.Joystick, plan.OldDevice))
                {
                    ReplaceMapping m;
                    string input = BindInput(bind);
                    bool blocked = plan.Merge == "keep" && byInput.TryGetValue(input, out m) && m.TargetRelations.Count > 0;
                    if (byInput.TryGetValue(input, out m) && !blocked)
                    {
                        bind.Joystick = plan.NewDevice;
                        if (!string.IsNullOrWhiteSpace(bind.JAxis))
                            bind.JAxis = m.NewInput;
                        else
                            bind.JButton = m.NewInput;
                        if (ReplaceSameInstance(bind.JoyBackup, plan.OldDevice))
                            bind.JoyBackup = plan.NewDevice;
                        if (ReplaceSameInstance(bind.AliasJoystick, plan.OldDevice))
                            bind.AliasJoystick = plan.NewDevice;
                        plan.MovedBinds++;
                        changed = true;
                    }
                    else
                        plan.KeptOnOld++;
                }
                if (bind.AllReformers != null)
                {
                    for (int i = 0; i < bind.AllReformers.Count; i++)
                    {
                        Modifier modifier = Modifier.ReformerToMod(bind.AllReformers[i]);
                        ReplaceMapping m;
                        if (modifier == null || !ReplaceSameInstance(modifier.device, plan.OldDevice) || !byInput.TryGetValue((modifier.key ?? string.Empty).Trim(), out m))
                            continue;
                        modifier.device = plan.NewDevice;
                        modifier.key = m.NewInput;
                        bind.AllReformers[i] = modifier.toReformerString();
                        changed = true;
                    }
                }
                if (!changed)
                    continue;
                InternalDataManagement.AllBinds[relationName] = bind;
                if (InternalDataManagement.AllRelations.ContainsKey(relationName) && InternalDataManagement.AllRelations[relationName] != null)
                    InternalDataManagement.AllRelations[relationName].bind = bind;
            }

            // Nothing left on the old device: its other settings follow (name, force feedback; a picture and USB id only
            // for the same product).
            bool emptied = ProfileDeviceUse().All(u => !ReplaceSameInstance(u.Device, plan.OldDevice));
            if (emptied)
            {
                MoveDeviceInstanceKeyEntries(InternalDataManagement.JoystickAliases, plan.OldDevice, plan.NewDevice);
                MoveDeviceInstanceKeyEntries(InternalDataManagement.JoystickFFB, plan.OldDevice, plan.NewDevice);
                MoveFfbOverrideDevice(plan.OldDevice, plan.NewDevice);
                MoveDeviceInstanceKeyEntries(InternalDataManagement.JoystickActivity, plan.OldDevice, plan.NewDevice);
                if (plan.Mode == "same")
                {
                    MoveDeviceInstanceKeyEntries(InternalDataManagement.JoystickFileImages, plan.OldDevice, plan.NewDevice);
                    MoveDeviceInstanceKeyEntries(InternalDataManagement.LocalJoystickPGUID, plan.OldDevice, plan.NewDevice);
                }
                ReplaceLocalJoystickInstance(plan.OldDevice, plan.NewDevice);
            }
            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
            plan.Applied = true;
            plan.Steps.Add(plan.MovedBinds + " bind(s) moved to " + plan.NewDevice + (plan.KeptOnOld > 0 ? ", " + plan.KeptOnOld + " stay on " + plan.OldDevice : string.Empty) +
                           (plan.RemovedTargetBinds > 0 ? ", " + plan.RemovedTargetBinds + " bind(s) of the new device replaced" : string.Empty) + ".");
        }

        // ---- CLI ---------------------------------------------------------------------------------------------------------

        // engine devices auto-remap [--dry-run]
        private static int RunDevicesAutoRemap(List<string> args, string outPath, bool textOutput)
        {
            bool dryRun = PopFlag(args, "--dry-run");
            if (args.Count > 0)
                return Fail("Unknown devices auto-remap argument(s): " + string.Join(" ", args));
            List<string> warnings = new List<string>();
            GameDeviceDirectory directory = GameDeviceDirectory.Discover(warnings);
            AutoRemapResult result = FindAutoRemaps(directory, warnings);
            result.DryRun = dryRun;
            if (!dryRun)
            {
                foreach (AutoRemapEntry entry in result.Remapped)
                    ApplyAutoRemap(entry, directory);
                result.Applied = result.Remapped.Count > 0;
            }
            if (!result.Applied)
                CommandChangedNothing = true;
            EmitPayload(textOutput ? AutoRemapText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }

        // engine devices replace <old> <new> [--mode auto|same|layout] [--map OLD=NEW]... [--merge add|keep|replace]
        //     [--grip <device>=<grip id>]... [--dry-run]
        private static int RunDevicesReplace(List<string> args, string outPath, bool textOutput)
        {
            bool dryRun = PopFlag(args, "--dry-run");
            string mode = PopOptionValue(args, "--mode") ?? "auto";
            string merge = PopOptionValue(args, "--merge");
            string oldToken = PopOptionValue(args, "--old") ?? PopOptionValue(args, "--from");
            string newToken = PopOptionValue(args, "--new") ?? PopOptionValue(args, "--to");
            Dictionary<string, string> overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string map in PopOptionValues(args, "--map"))
            {
                int split = map.IndexOf('=');
                if (split <= 0)
                    return Fail("--map expects OLD=NEW, e.g. JOY_BTN3=JOY_BTN5 (NEW = none leaves the input out).");
                overrides[map.Substring(0, split).Trim()] = map.Substring(split + 1).Trim();
            }
            Dictionary<string, string> grips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string grip in PopOptionValues(args, "--grip"))
            {
                int split = grip.LastIndexOf('=');
                if (split <= 0)
                    return Fail("--grip expects \"<device or VID:PID>=<grip id>\".");
                grips[grip.Substring(0, split).Trim()] = grip.Substring(split + 1).Trim();
            }
            List<string> positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (positional.Count != args.Count)
                return Fail("Unknown devices replace argument(s): " + string.Join(" ", args.Where(a => a.StartsWith("--", StringComparison.Ordinal))));
            if (oldToken == null && positional.Count > 0)
                oldToken = positional[0];
            if (newToken == null && positional.Count > 1)
                newToken = positional[1];
            if (string.IsNullOrWhiteSpace(oldToken) || string.IsNullOrWhiteSpace(newToken) || positional.Count > 2)
                return Fail("devices replace requires <old device> <new device> [--mode auto|same|layout] [--map OLD=NEW] [--merge add|keep|replace] [--dry-run].");
            mode = mode.Trim().ToLowerInvariant();
            if (mode != "auto" && mode != "same" && mode != "layout")
                return Fail("--mode must be auto, same or layout.");
            if (merge != null && merge != "add" && merge != "keep" && merge != "replace")
                return Fail("--merge must be add, keep or replace.");

            List<string> warnings = new List<string>();
            GameDeviceDirectory directory = GameDeviceDirectory.Discover(warnings);
            List<DeviceUse> uses = ProfileDeviceUse();
            string oldDevice = uses.Select(u => u.Device).FirstOrDefault(d => ReplaceSameInstance(d, oldToken))
                               ?? uses.Select(u => u.Device).FirstOrDefault(d => string.Equals(ReplaceNameOf(d), ReplaceNameOf(oldToken), StringComparison.OrdinalIgnoreCase) && uses.Count(u => ReplaceNameKey(u.Device) == ReplaceNameKey(oldToken)) == 1);
            if (oldDevice == null)
                return Fail("The profile has no binds on '" + oldToken + "'. Devices with binds: " + string.Join(", ", uses.Select(u => u.Device)));
            string newDevice = directory.Devices.Where(d => d.Connected).Select(d => d.JoyProName).FirstOrDefault(d => ReplaceSameInstance(d, newToken))
                               ?? uses.Select(u => u.Device).FirstOrDefault(d => ReplaceSameInstance(d, newToken))
                               ?? directory.Devices.Where(d => d.Connected).Select(d => d.JoyProName).FirstOrDefault(d => string.Equals(ReplaceNameOf(d), ReplaceNameOf(newToken), StringComparison.OrdinalIgnoreCase));
            Guid newGuid;
            if (newDevice == null && TryDeviceGuid(newToken, out newGuid))
                newDevice = CollapseSpaces(newToken);
            if (newDevice == null)
                return Fail("'" + newToken + "' is neither connected nor in the profile. Connected: " + string.Join(", ", directory.Devices.Where(d => d.Connected).Select(d => d.JoyProName)));
            if (ReplaceSameInstance(oldDevice, newDevice))
                return Fail("The old and the new device are the same device: " + oldDevice + ".");

            ReplacePlan plan = PlanDeviceReplace(oldDevice, newDevice, mode, overrides, grips, directory, warnings);
            plan.DryRun = dryRun;
            plan.Merge = merge ?? string.Empty;
            if (!dryRun)
            {
                if (plan.NeedsMergeChoice && merge == null)
                    return Fail(newDevice + " already has " + plan.TargetBinds + " bind(s). Choose how to merge: --merge add (keep both), keep (its binds win) or replace (the old device's binds win); --dry-run shows the overlaps.");
                if (plan.MappedInputs == 0)
                    return Fail("No input of " + oldDevice + " fits " + newDevice + "; nothing was moved. Map inputs yourself with --map OLD=NEW.");
                ApplyDeviceReplace(plan, merge);
                RememberProductGuid(newDevice, ConnectedDevice(directory, newDevice));
            }
            else
                CommandChangedNothing = true;
            EmitPayload(textOutput ? ReplaceText(plan) : EngineSerialization.ToJson(plan), outPath);
            return 0;
        }

        private static string AutoRemapText(AutoRemapResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Device Auto Remap" + (result.DryRun ? " (dry run)" : string.Empty));
            sb.AppendLine("Remapped: " + result.Remapped.Count.ToString(CultureInfo.InvariantCulture) + (result.Applied ? " (applied)" : string.Empty));
            foreach (AutoRemapEntry entry in result.Remapped)
                sb.AppendLine("  " + entry.OldDevice + " -> " + entry.NewDevice + " | " + entry.Binds + " bind(s), " + entry.Modifiers + " modifier(s) | " + entry.Reason);
            sb.AppendLine("Skipped: " + result.Skipped.Count.ToString(CultureInfo.InvariantCulture));
            foreach (AutoRemapSkip skip in result.Skipped)
                sb.AppendLine("  " + skip.OldDevice + " | " + skip.Reason + (skip.Candidates.Count > 0 ? " | " + string.Join(", ", skip.Candidates) : string.Empty));
            sb.AppendLine("Missing: " + result.Missing.Count.ToString(CultureInfo.InvariantCulture));
            foreach (string device in result.Missing)
                sb.AppendLine("  " + device);
            AppendWarnings(sb, result.Warnings);
            return sb.ToString().TrimEnd();
        }

        private static string ReplaceText(ReplacePlan plan)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Device Replace" + (plan.DryRun ? " (preview)" : plan.Applied ? " (applied)" : string.Empty));
            sb.AppendLine("Old: " + plan.OldDevice + " | " + plan.OldModel + (plan.OldConnected ? string.Empty : " | not connected") + " | " + plan.OldBinds + " bind(s), " + plan.OldModifiers + " modifier(s)");
            sb.AppendLine("New: " + plan.NewDevice + " | " + plan.NewModel + (plan.NewConnected ? string.Empty : " | not connected") + " | " + plan.TargetBinds + " bind(s)");
            sb.AppendLine("Mode: " + plan.Mode + " (" + plan.ModeReason + ")");
            sb.AppendLine("Mapped: " + plan.MappedInputs + ", not mapped: " + plan.UnmappedInputs);
            foreach (ReplaceMapping m in plan.Mappings)
            {
                sb.AppendLine("  " + m.OldInput + (m.OldPart.Length > 0 ? " (" + m.OldPart + ")" : string.Empty) + " -> " +
                              (m.Mapped ? m.NewInput + (m.NewPart.Length > 0 ? " (" + m.NewPart + ")" : string.Empty) : "-") +
                              " | " + m.Method + (m.Note.Length > 0 ? ": " + m.Note : string.Empty) +
                              " | " + m.Relations.Count + " relation(s)" + (m.Modifiers.Count > 0 ? ", modifier " + string.Join(", ", m.Modifiers) : string.Empty) +
                              (m.TargetRelations.Count > 0 ? " | already on the new device: " + string.Join(", ", m.TargetRelations) : string.Empty));
            }
            if (plan.NeedsMergeChoice)
                sb.AppendLine("Merge: " + (plan.Merge.Length > 0 ? plan.Merge : "choose add, keep or replace (the new device already has binds)"));
            foreach (string step in plan.Steps)
                sb.AppendLine(step);
            AppendWarnings(sb, plan.Warnings);
            return sb.ToString().TrimEnd();
        }

        private static void AppendWarnings(StringBuilder sb, List<string> warnings)
        {
            if (warnings == null || warnings.Count == 0)
                return;
            sb.AppendLine("Warnings:");
            foreach (string warning in warnings.Distinct())
                sb.AppendLine("  - " + warning);
        }
    }
}
