using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    // "Auto Assign": binds the profile's relations to the physical controls of the user's devices. Each relation's
    // function slot (DB\Functions\slot_controls.tsv) names the controls it belongs on in the real aircraft ("TMS up" ->
    // stick.hat.tms.up); the device knowledge base says which input of which device is that control. The layout works
    // for any combination of devices: exact positions first, hats and 2D sticks as a whole, latched on/off pairs on
    // toggle switches, frequent functions on the grips, rarer ones behind up to two modifier layers or on panels. An
    // input serves several relations as long as they have no aircraft / game in common (like ThrottleLeft and
    // ThrottleLeft29 on one axis); the most constrained function is placed first. Existing binds are kept and their inputs are not reused; the controls the profile
    // used before (matched by product name, so a re-plugged device or a new grip keeps the user's style) win ties.
    internal static class EngineAutoAssign
    {
        private const double Forbidden = -1e9;

        // Trigger stages, weapon release, pinky and detents only take functions made for them.
        private static readonly Regex Dedicated = new Regex(@"^stick\.trigger\.|^stick\.weapon_release$|\.pinky\.|^throttle\.detent\.|^throttle\.cage$", RegexOptions.Compiled);
        // Hats and switches with a fixed role in real aircraft: a function that only asks for "any hat" ranks lower there.
        private static readonly Regex Named = new Regex(@"\.(hat\.(tms|dms|cms|trim|mic)|coolie|china|boat|speedbrake|dogfight)\.", RegexOptions.Compiled);
        // Flight axes and self-centring thumb sticks only take functions that ask for them (no RPM on a ministick).
        private static readonly Regex DedicatedAxis = new Regex(@"^(stick\.axis\.(pitch|roll|twist|brake_lever|ministick2?\.[xy])|pedals\.axis\.|throttle\.axis\.(main|left|right|ministick2?\.[xy])|collective\.axis\.main|yoke\.axis\.(pitch|roll))", RegexOptions.Compiled);
        private static readonly Regex NumberedThrottleButton = new Regex(@"^(throttle|collective)\.button\.\d+$", RegexOptions.Compiled);
        private static readonly Regex Diagonal = new Regex(@"\.(up|down)(left|right)$", RegexOptions.Compiled);
        private static readonly HashSet<string> GroupRoles = new HashSet<string>(StringComparer.Ordinal) { "up", "down", "left", "right", "press", "fwd", "aft", "center", "x", "y", "cw", "ccw" };

        private sealed class SlotControl
        {
            public string Slot = string.Empty;
            public string Side = "any";
            public List<string> Prefs = new List<string>();
            public string PairGroup = string.Empty;
            public string PairRole = string.Empty;
            public List<string> Labels = new List<string>();
            public string Frequency = "occasional";
            public bool Axis;
            public string Group = string.Empty;
        }

        private sealed class Resource
        {
            public RosterDevice Device;
            public string Input = string.Empty;
            public string Control = string.Empty;
            public string Label = string.Empty;
            public string Type = "momentary";
            public bool IsAxis;
            public int Layer;
            public Modifier Modifier;

            public string Key
            {
                get { return Device.JoyProName + "|" + Input + "|" + (Modifier == null ? string.Empty : Modifier.device + "/" + Modifier.key); }
            }
        }

        // Which aircraft / games already use an input (bit set over all aircraft of the profile).
        private sealed class Occupancy
        {
            private readonly Dictionary<string, ulong[]> used = new Dictionary<string, ulong[]>(StringComparer.OrdinalIgnoreCase);

            public bool Free(Resource resource, ulong[] planes)
            {
                return Free(resource.Key, planes);
            }

            public bool Free(string key, ulong[] planes)
            {
                ulong[] bits;
                return !used.TryGetValue(key, out bits) || !Overlaps(bits, planes);
            }

            public void Add(string key, ulong[] planes)
            {
                ulong[] bits;
                if (!used.TryGetValue(key, out bits))
                    used[key] = bits = new ulong[planes.Length];
                for (int i = 0; i < planes.Length && i < bits.Length; i++)
                    bits[i] |= planes[i];
            }
        }

        // Bit per DCS aircraft / other game of the profile. DCS layers active next to every aircraft (Default UI layer, NS430,
        // voice) overlap with all DCS aircraft; a relation without any aircraft overlaps with everything.
        private sealed class PlaneIndex
        {
            private readonly Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly int words;
            private readonly ulong[] dcs;

            public PlaneIndex(IEnumerable<Relation> relations, Dictionary<string, EngineAutoGroup.Aircraft> aircraft)
            {
                foreach (KeyValuePair<string, EngineAutoGroup.Aircraft> plane in aircraft.Where(a => a.Value.Type == "layer"))
                    layers.Add(plane.Key);
                foreach (Relation relation in relations.Where(r => r != null))
                {
                    foreach (string key in Keys(relation))
                    {
                        if (!index.ContainsKey(key))
                            index[key] = index.Count;
                    }
                }
                words = Math.Max(1, (index.Count + 63) / 64);
                dcs = new ulong[words];
                foreach (KeyValuePair<string, int> entry in index.Where(e => e.Key.StartsWith("DCS|", StringComparison.Ordinal)))
                    dcs[entry.Value >> 6] |= 1UL << (entry.Value & 63);
            }

            public string Shared(ulong[] a, ulong[] b)
            {
                foreach (KeyValuePair<string, int> entry in index.OrderBy(e => e.Value))
                {
                    int word = entry.Value >> 6;
                    ulong mask = 1UL << (entry.Value & 63);
                    if (word < a.Length && word < b.Length && (a[word] & mask) != 0 && (b[word] & mask) != 0)
                        return entry.Key;
                }
                return a.Any(w => w == ulong.MaxValue) || b.Any(w => w == ulong.MaxValue) ? "(no aircraft = all)" : "?";
            }

            public ulong[] Bits(Relation relation)
            {
                ulong[] bits = new ulong[words];
                bool any = false;
                foreach (string key in Keys(relation))
                {
                    int bit;
                    if (!index.TryGetValue(key, out bit))
                        continue;
                    bits[bit >> 6] |= 1UL << (bit & 63);
                    any = true;
                    if (key.StartsWith("DCS|", StringComparison.Ordinal) && layers.Contains(key.Substring(4)))
                    {
                        for (int i = 0; i < words; i++)
                            bits[i] |= dcs[i];
                    }
                }
                if (!any)
                {
                    for (int i = 0; i < words; i++)
                        bits[i] = ulong.MaxValue;
                }
                return bits;
            }

            // DCS binds per aircraft; the other games have one input configuration for the whole game.
            private static IEnumerable<string> Keys(Relation relation)
            {
                foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
                {
                    if (node == null)
                        continue;
                    if (!EngineAutoGroup.IsDcs(node.Game))
                    {
                        yield return node.Game + "|*";
                        continue;
                    }
                    foreach (KeyValuePair<string, bool> plane in (node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value))
                        yield return "DCS|" + plane.Key;
                }
            }
        }

        private static int OverlapCount(ulong[] a, ulong[] b)
        {
            int count = 0;
            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                ulong both = a[i] & b[i];
                while (both != 0)
                {
                    both &= both - 1;
                    count++;
                }
            }
            return count;
        }

        private static bool Overlaps(ulong[] a, ulong[] b)
        {
            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                if ((a[i] & b[i]) != 0)
                    return true;
            }
            return false;
        }

        private sealed class Demand
        {
            public Relation Relation;
            public ulong[] Planes = new ulong[0];
            public SlotControl Slot;
            public bool IsAxis;
            public Bind Previous;
            public string StyleControl = string.Empty;
            public bool StyleShifted;
        }

        public static EngineAutoAssignResult Run(AutoAssignOptions options)
        {
            EngineAutoAssignResult result = new EngineAutoAssignResult { DryRun = options.DryRun, MaxModifiers = options.MaxModifiers };
            GameDeviceDirectory directory = GameDeviceDirectory.Discover(result.Warnings);
            List<RosterDevice> roster = EngineDeviceKb.BuildRoster(directory, options.GripChoices, options.AddedDevices, options.IgnoredDevices, result.Warnings)
                .OrderBy(r => r.JoyProName, StringComparer.Ordinal).ToList();
            Dictionary<RosterDevice, RosterDevice> alternatives;
            result.Devices.AddRange(DescribeRoster(roster, out alternatives));

            List<RosterDevice> usable = roster.Where(r => r.Model != null && !alternatives.ContainsKey(r)).ToList();
            if (roster.Any(r => r.Model == null && r.GripChoices.Count > 0))
                result.Warnings.Add("Pick the grip of every device with several grip choices (--grip \"<device>=<grip id>\"); those devices are left out until then.");
            if (alternatives.Count > 0)
                result.Warnings.Add("Devices with the same role as another one are left out (one bind per function): " +
                                    string.Join(", ", alternatives.Select(a => a.Key.JoyProName)) + ". Use --ignore-device on the other one to use them instead.");
            if (usable.Count == 0)
            {
                result.Errors.Add("No usable device: connect your devices or add them from the catalog (--add-device).");
                return result;
            }

            Dictionary<string, SlotControl> slotControls = LoadSlotControls(result);
            EngineAutoGroupResult catalogErrors = new EngineAutoGroupResult();
            Dictionary<string, EngineAutoGroup.Slot> catalogSlots;
            Dictionary<string, EngineAutoGroup.Aircraft> catalogAircraft;
            List<EngineAutoGroup.Row> catalogRows;
            if (!EngineAutoGroup.TryLoadCatalog(EngineNlGroup.FunctionsFolder(), catalogErrors, out catalogSlots, out catalogAircraft, out catalogRows))
            {
                result.Warnings.AddRange(catalogErrors.Errors);
                catalogSlots = new Dictionary<string, EngineAutoGroup.Slot>();
                catalogAircraft = new Dictionary<string, EngineAutoGroup.Aircraft>();
                catalogRows = new List<EngineAutoGroup.Row>();
            }
            Dictionary<string, string> relationSlots = RelationSlots(catalogSlots, catalogRows);
            PlaneIndex planes = new PlaneIndex(InternalDataManagement.AllRelations.Values, catalogAircraft);

            // Inputs the profile already uses stay taken for the aircraft of their relation, unless it is re-assigned.
            Dictionary<string, Relation> relations = InternalDataManagement.AllRelations;
            List<Demand> demands = new List<Demand>();
            Occupancy occupancy = new Occupancy();
            foreach (KeyValuePair<string, Relation> pair in relations.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                Relation relation = pair.Value;
                if (relation == null || relation.NODES == null || relation.NODES.Count == 0)
                    continue;
                Bind bind;
                InternalDataManagement.AllBinds.TryGetValue(pair.Key, out bind);
                bool bound = bind != null && !string.IsNullOrWhiteSpace(bind.Joystick) &&
                             !string.IsNullOrWhiteSpace(relation.ISAXIS ? bind.JAxis : bind.JButton);
                // --overwrite re-lays out the devices of this setup; binds on the keyboard, virtual devices (vJoy) and
                // devices that are not connected stay, unless --include-missing moves the latter (a replaced device).
                bool onSetup = bound && usable.Any(d => d.JoyProName == MapDevice(bind.Joystick, usable));
                bool movable = onSetup || (bound && options.IncludeMissing && !IsKeyboardOrVirtual(bind.Joystick));
                if (bound && (!options.Overwrite || !movable))
                {
                    occupancy.Add(BindKey(bind, relation.ISAXIS, usable), planes.Bits(relation));
                    result.KeptExisting++;
                    continue;
                }
                if (options.Relations.Count > 0 && !options.Relations.Contains(relation.NAME))
                {
                    if (bound)
                        occupancy.Add(BindKey(bind, relation.ISAXIS, usable), planes.Bits(relation));
                    continue;
                }
                string slotId;
                SlotControl slot;
                if (!relationSlots.TryGetValue(relation.NAME, out slotId) || !slotControls.TryGetValue(slotId, out slot))
                    slot = new SlotControl { Slot = string.Empty, Axis = relation.ISAXIS, Prefs = relation.ISAXIS ? new List<string> { "*.axis.*" } : new List<string> { "panel.button.*", "*.base.button.*" } };
                if (slot.Slot == "dummyunbindrelation")
                    continue;
                demands.Add(new Demand { Relation = relation, Planes = planes.Bits(relation), Slot = slot, IsAxis = relation.ISAXIS, Previous = bound ? bind : null });
            }

            List<Resource> resources = BuildResources(usable);
            if (!options.IgnoreStyle)
                LearnStyle(demands, usable, resources);

            // Modifier layers only when the grip buttons do not suffice, at most options.MaxModifiers.
            List<Modifier> modifiers = ChooseModifiers(resources, demands, options.MaxModifiers, usable);
            List<Resource> layered = new List<Resource>(resources.Where(r => !modifiers.Any(m => m.device == r.Device.JoyProName && m.key == r.Input)));
            int layer = 0;
            foreach (Modifier modifier in modifiers)
            {
                layer++;
                foreach (Resource baseResource in resources.Where(r => !r.IsAxis && r.Layer == 0 && r.Type != "latched" && GripFamily(r.Control)))
                {
                    if (modifiers.Any(m => m.device == baseResource.Device.JoyProName && m.key == baseResource.Input))
                        continue;
                    Resource copy = new Resource
                    {
                        Device = baseResource.Device, Input = baseResource.Input, Control = baseResource.Control, Label = baseResource.Label,
                        Type = baseResource.Type, IsAxis = false, Layer = layer, Modifier = modifier
                    };
                    layered.Add(copy);
                }
            }

            List<Tuple<Demand, Resource>> assigned = new List<Tuple<Demand, Resource>>();
            // The user's own layout first: every relation back on the control (and shift state) it had before.
            if (!options.IgnoreStyle)
                AssignStyle(demands, layered, occupancy, assigned);

            // Round 1 places only strong matches (the user's previous control, a matching panel label, the exact position),
            // so a labelled switch goes to its own function before any other pair or group can take it; round 2 the rest.
            foreach (bool strongOnly in new[] { true, false })
            {
                AssignPairs(demands, layered, occupancy, assigned, strongOnly);
                AssignGroups(demands, layered, occupancy, assigned, strongOnly);
                HashSet<Demand> placed = new HashSet<Demand>(assigned.Select(a => a.Item1));
                AssignGreedy(demands.Where(d => !placed.Contains(d)).ToList(), layered, occupancy, assigned, strongOnly);
            }

            foreach (Tuple<Demand, Resource> item in assigned.OrderBy(a => a.Item1.Relation.NAME, StringComparer.OrdinalIgnoreCase))
            {
                bool style = IsStyle(item.Item1, item.Item2);
                if (style)
                    result.StyleKept++;
                result.Assignments.Add(new EngineAutoAssignment
                {
                    Relation = item.Item1.Relation.NAME,
                    Slot = item.Item1.Slot.Slot,
                    Device = item.Item2.Device.JoyProName,
                    Input = item.Item2.Input,
                    Modifier = item.Item2.Modifier == null ? string.Empty : item.Item2.Modifier.name,
                    Control = item.Item2.Control + (item.Item2.Label.Length > 0 ? " \"" + item.Item2.Label + "\"" : string.Empty),
                    Score = Math.Round(Score(item.Item1, item.Item2), 1),
                    SameAsBefore = style
                });
            }
            foreach (Demand demand in demands.Where(d => !assigned.Any(a => a.Item1 == d)))
                result.Unassigned.Add(demand.Relation.NAME);
            foreach (Demand demand in demands.Where(d => options.Explain.Contains(d.Relation.NAME)))
                result.Explanations.AddRange(Explain(demand, layered, assigned, planes));
            result.ModifiersUsed.AddRange(modifiers.Where(m => assigned.Any(a => a.Item2.Modifier == m)).Select(m => m.name + " = " + m.device + " " + m.key));

            if (!options.DryRun)
                Apply(assigned, modifiers);
            return result;
        }

        // Why a relation landed where it did: its slot, previous control and the best candidates with their score and
        // the relations of shared aircraft that occupy them.
        private static IEnumerable<string> Explain(Demand demand, List<Resource> resources, List<Tuple<Demand, Resource>> assigned, PlaneIndex planes)
        {
            Tuple<Demand, Resource> mine = assigned.FirstOrDefault(a => a.Item1 == demand);
            yield return demand.Relation.NAME + " (" + OverlapCount(demand.Planes, demand.Planes).ToString(CultureInfo.InvariantCulture) + " aircraft bits): slot " + (demand.Slot.Slot.Length > 0 ? demand.Slot.Slot : "(unknown)") + " [" + demand.Slot.Side + ", " + demand.Slot.Frequency +
                         (demand.Slot.Group.Length > 0 ? ", group " + demand.Slot.Group : string.Empty) + (demand.Slot.PairGroup.Length > 0 ? ", pair " + demand.Slot.PairGroup + ":" + demand.Slot.PairRole : string.Empty) +
                         "], prefs " + string.Join(",", demand.Slot.Prefs) + (demand.StyleControl.Length > 0 ? ", before: " + demand.StyleControl + (demand.StyleShifted ? " (shifted)" : string.Empty) : string.Empty) +
                         " => " + (mine == null ? "not assigned" : mine.Item2.Device.JoyProName + " " + mine.Item2.Input + (mine.Item2.Modifier != null ? " + " + mine.Item2.Modifier.name : string.Empty));
            foreach (Tuple<Resource, double> candidate in resources.Select(r => Tuple.Create(r, Score(demand, r))).Where(c => c.Item2 > Forbidden).OrderByDescending(c => c.Item2).Take(10))
            {
                List<string> blockers = assigned.Where(a => a.Item1 != demand && a.Item2.Key == candidate.Item1.Key && Overlaps(a.Item1.Planes, demand.Planes))
                    .Select(a => a.Item1.Relation.NAME + " (" + planes.Shared(a.Item1.Planes, demand.Planes) + ")").ToList();
                yield return "    " + Math.Round(candidate.Item2, 1).ToString(CultureInfo.InvariantCulture) + (IsStrong(demand, candidate.Item1) ? " strong" : string.Empty) + " | " +
                             candidate.Item1.Device.JoyProName + " " + candidate.Item1.Input + (candidate.Item1.Modifier != null ? " + " + candidate.Item1.Modifier.name : string.Empty) + " (" + candidate.Item1.Control + ")" +
                             (blockers.Count > 0 ? " | used by " + string.Join(", ", blockers) : string.Empty);
            }
        }

        private static bool IsKeyboardOrVirtual(string device)
        {
            return string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(device ?? string.Empty, @"^(vjoy|vigem|virtual)", RegexOptions.IgnoreCase);
        }

        // One device per hand role takes part: a relation has one bind, so a second right-hand stick is an alternative,
        // not a second set of inputs. Preferred: devices added on purpose, the device the profile already uses, known
        // layouts, a throttle over a left-hand stick, more inputs.
        private static Dictionary<RosterDevice, RosterDevice> ChooseActive(List<RosterDevice> roster)
        {
            Dictionary<RosterDevice, RosterDevice> alternatives = new Dictionary<RosterDevice, RosterDevice>();
            Dictionary<string, int> profileUse = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Bind bind in InternalDataManagement.AllBinds.Values.Where(b => b != null && !string.IsNullOrWhiteSpace(b.Joystick)))
            {
                string product = ProductName(bind.Joystick);
                profileUse[product] = (profileUse.ContainsKey(product) ? profileUse[product] : 0) + 1;
            }
            foreach (IGrouping<string, RosterDevice> role in roster.Where(r => r.Model != null && HandRole(r).Length > 0).GroupBy(HandRole))
            {
                List<RosterDevice> ranked = role.OrderByDescending(r => RoleRank(r, profileUse)).ThenBy(r => r.JoyProName, StringComparer.Ordinal).ToList();
                foreach (RosterDevice other in ranked.Skip(1))
                    alternatives[other] = ranked[0];
            }
            return alternatives;
        }

        private static string HandRole(RosterDevice device)
        {
            switch (device.Model.Kind)
            {
                case "stick": return device.Model.Hand == "left" ? "left" : "right";
                case "yoke":
                case "hotas": return "right";
                case "throttle": return "left";
                case "collective": return "collective";
                case "pedals": return "pedals";
                default: return string.Empty;
            }
        }

        private static double RoleRank(RosterDevice device, Dictionary<string, int> profileUse)
        {
            double rank = 0;
            int uses;
            if (!device.Connected)
                rank += 1e7;
            if (profileUse.TryGetValue(ProductName(device.JoyProName), out uses))
                rank += 1e5 + uses * 10;
            if (!device.Generic)
                rank += 5000;
            if (device.Model.Kind == "throttle")
                rank += 1000;
            rank += device.Model.Axes.Count(a => a.Input.Length > 0) + device.Model.Controls.Count(c => c.Input.Length > 0);
            return rank;
        }

        // "VKB-Sim Gunfighter  Modern Combat Pro Twist {GUID}" -> "vkb-sim gunfighter modern combat pro twist".
        private static string ProductName(string joyProName)
        {
            string name = Regex.Replace(joyProName ?? string.Empty, @"\{[0-9A-Fa-f-]+\}\s*$", string.Empty);
            return Regex.Replace(name, @"\s+", " ").Trim().ToLowerInvariant();
        }

        // A device named in the profile -> the device of the setup: same name, else the only one with that product name
        // (the instance GUID changes when a device is re-plugged or Windows is reinstalled).
        private static string MapDevice(string profileDevice, List<RosterDevice> devices)
        {
            if (string.IsNullOrWhiteSpace(profileDevice) || devices.Any(d => d.JoyProName == profileDevice))
                return profileDevice;
            string product = ProductName(profileDevice);
            List<RosterDevice> same = devices.Where(d => ProductName(d.JoyProName) == product).ToList();
            return same.Count == 1 ? same[0].JoyProName : profileDevice;
        }

        // The user's style: the control each relation was bound to before (on the same product) gets a bonus.
        private static void LearnStyle(List<Demand> demands, List<RosterDevice> devices, List<Resource> resources)
        {
            foreach (Demand demand in demands.Where(d => d.Previous != null))
            {
                string device = MapDevice(demand.Previous.Joystick, devices);
                string input = demand.IsAxis ? demand.Previous.JAxis : demand.Previous.JButton;
                Resource resource = resources.FirstOrDefault(r => r.Layer == 0 && r.Device.JoyProName == device && string.Equals(r.Input, input, StringComparison.OrdinalIgnoreCase));
                if (resource == null)
                    continue;
                demand.StyleControl = resource.Control;
                demand.StyleShifted = demand.Previous.AllReformers != null && demand.Previous.AllReformers.Count > 0;
            }
        }

        // A strong match: the control the relation used before, a panel label naming the function, or its exact position.
        private static bool IsStrong(Demand demand, Resource resource)
        {
            if (demand.IsAxis != resource.IsAxis)
                return false;
            if (demand.StyleControl.Length > 0 && string.Equals(demand.StyleControl, resource.Control, StringComparison.Ordinal))
                return true;
            if (LabelMatches(demand.Slot, resource))
                return true;
            // the primary position of the function, not a generic alternative
            int pref = PrefIndex(demand.Slot, resource.Control);
            return pref == 0 && demand.Slot.Prefs[0].IndexOf('*') < 0;
        }

        // Position words on a panel label (LAUNCH BAR EXTEND, HOOK UP, ANTI SKID ON) for the role of an on/off pair.
        private static readonly Dictionary<string, string[]> RoleWords = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "on", new[] { "ON" } }, { "off", new[] { "OFF" } }, { "open", new[] { "OPEN" } }, { "close", new[] { "CLOSE", "CLOSED" } },
            { "extend", new[] { "EXTEND", "EXT" } }, { "retract", new[] { "RETRACT", "RET" } }, { "up", new[] { "UP" } }, { "down", new[] { "DOWN", "DN" } },
            { "in", new[] { "IN", "RETRACT", "UP" } }, { "out", new[] { "OUT", "EXTEND", "DOWN" } }, { "arm", new[] { "ARM" } }, { "safe", new[] { "SAFE" } },
            { "start", new[] { "START" } }, { "stop", new[] { "STOP", "OFF" } }
        };

        private static readonly Dictionary<string, string> CounterRole = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "on", "off" }, { "off", "on" }, { "open", "close" }, { "close", "open" }, { "extend", "retract" }, { "retract", "extend" }, { "up", "down" },
            { "down", "up" }, { "in", "out" }, { "out", "in" }, { "arm", "safe" }, { "safe", "arm" }, { "start", "stop" }, { "stop", "start" }
        };

        private static double RoleLabel(SlotControl slot, Resource resource)
        {
            string[] words;
            string counter;
            if (slot.PairRole.Length == 0 || resource.Label.Length == 0 || IsGrip(resource.Control) || !RoleWords.TryGetValue(slot.PairRole, out words))
                return 0;
            Func<string[], bool> has = list => list.Any(w => Regex.IsMatch(resource.Label, @"(^|[^A-Za-z0-9])" + w + @"($|[^A-Za-z0-9])", RegexOptions.IgnoreCase));
            if (has(words))
                return 80;
            return CounterRole.TryGetValue(slot.PairRole, out counter) && has(RoleWords[counter]) ? -80 : 0;
        }

        // Printed labels of panels and bases (LDG GEAR, ANTI SKID ...), whole words; grip labels are descriptions.
        private static bool LabelMatches(SlotControl slot, Resource resource)
        {
            if (resource.Label.Length == 0 || IsGrip(resource.Control))
                return false;
            return slot.Labels.Any(k => Regex.IsMatch(resource.Label, @"(^|[^A-Za-z0-9])" + Regex.Escape(k) + @"($|[^A-Za-z0-9])", RegexOptions.IgnoreCase));
        }

        private static bool IsStyle(Demand demand, Resource resource)
        {
            return demand.StyleControl.Length > 0 && string.Equals(demand.StyleControl, resource.Control, StringComparison.Ordinal) &&
                   demand.StyleShifted == (resource.Layer > 0);
        }

        private static void Apply(List<Tuple<Demand, Resource>> assigned, List<Modifier> modifiers)
        {
            foreach (Modifier modifier in modifiers.Where(m => assigned.Any(a => a.Item2.Modifier == m)))
            {
                if (!InternalDataManagement.AllModifiers.ContainsKey(modifier.name))
                    InternalDataManagement.AllModifiers.Add(modifier.name, modifier);
            }
            foreach (Tuple<Demand, Resource> item in assigned)
            {
                Relation relation = item.Item1.Relation;
                Bind bind = new Bind(relation);
                bind.Joystick = item.Item2.Device.JoyProName;
                bind.JoyBackup = bind.Joystick;
                if (relation.ISAXIS)
                {
                    bind.JAxis = item.Item2.Input;
                    bind.JButton = string.Empty;
                }
                else
                {
                    bind.JButton = item.Item2.Input;
                    bind.JAxis = string.Empty;
                }
                bind.AllReformers = item.Item2.Modifier == null ? new List<string>() : new List<string> { item.Item2.Modifier.toReformerString() };
                relation.bind = bind;
                InternalDataManagement.AllBinds[relation.NAME] = bind;
            }
        }

        // Previous binds (matched by product and control) are taken over as they were, including inputs the user bound to
        // relations with aircraft in common on purpose; only binds kept from the profile block them.
        private static void AssignStyle(List<Demand> demands, List<Resource> resources, Occupancy occupancy, List<Tuple<Demand, Resource>> assigned)
        {
            List<Tuple<Demand, Resource>> placements = new List<Tuple<Demand, Resource>>();
            foreach (Demand demand in demands.Where(d => d.StyleControl.Length > 0))
            {
                Resource resource = resources.Where(r => IsStyle(demand, r) && occupancy.Free(r, demand.Planes))
                    .OrderByDescending(r => Score(demand, r)).FirstOrDefault();
                if (resource != null)
                    placements.Add(Tuple.Create(demand, resource));
            }
            foreach (Tuple<Demand, Resource> placement in placements)
                Place(placement.Item1, placement.Item2, occupancy, assigned);
        }

        // Latched on/off pairs (gear up/down, master arm on/off ...) want both positions of one toggle switch. Each
        // pair is scored in both orientations; the function's own position preferences decide which way round.
        private static void AssignPairs(List<Demand> demands, List<Resource> resources, Occupancy occupancy, List<Tuple<Demand, Resource>> assigned, bool strongOnly)
        {
            HashSet<Demand> placed = new HashSet<Demand>(assigned.Select(a => a.Item1));
            List<List<Demand>> pairs = new List<List<Demand>>();
            foreach (IGrouping<string, Demand> group in demands.Where(d => !d.IsAxis && d.Slot.PairGroup.Length > 0 && !placed.Contains(d)).GroupBy(d => d.Slot.PairGroup, StringComparer.Ordinal))
            {
                // on/off of the same aircraft belong together (several relations per role when presets split by aircraft)
                List<IGrouping<string, Demand>> roles = group.GroupBy(d => d.Slot.PairRole, StringComparer.Ordinal).OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
                if (roles.Count != 2)
                    continue;
                List<Demand> first = roles[0].ToList();
                List<Demand> second = roles[1].ToList();
                foreach (Tuple<Demand, Demand, int> candidate in first.SelectMany(a => second.Select(b => Tuple.Create(a, b, OverlapCount(a.Planes, b.Planes))))
                             .Where(c => c.Item3 > 0).OrderByDescending(c => c.Item3).ThenBy(c => c.Item1.Relation.NAME, StringComparer.Ordinal).ThenBy(c => c.Item2.Relation.NAME, StringComparer.Ordinal).ToList())
                {
                    if (pairs.Any(p => p.Contains(candidate.Item1) || p.Contains(candidate.Item2)))
                        continue;
                    pairs.Add(new List<Demand> { candidate.Item1, candidate.Item2 });
                }
            }
            List<Tuple<Resource, Resource>> toggles = resources.Where(r => r.Layer == 0 && Regex.IsMatch(r.Control, @"\.(toggle3?\.\d+|lever\.gear)\.up$"))
                .Select(up => Tuple.Create(up, resources.FirstOrDefault(d => d.Layer == 0 && d.Device == up.Device && d.Control == Regex.Replace(up.Control, @"\.up$", ".down"))))
                .Where(t => t.Item2 != null).ToList();
            if (pairs.Count == 0 || toggles.Count == 0)
                return;

            foreach (List<Demand> pair in pairs.OrderByDescending(Importance).ThenBy(p => p[0].Relation.NAME, StringComparer.Ordinal))
            {
                double top = Forbidden;
                int topToggle = -1;
                bool topSwap = false;
                for (int t = 0; t < toggles.Count; t++)
                {
                    double straight = PairScore(pair[0], toggles[t].Item1, pair[1], toggles[t].Item2, occupancy, strongOnly);
                    double reverse = PairScore(pair[0], toggles[t].Item2, pair[1], toggles[t].Item1, occupancy, strongOnly);
                    double value = Math.Max(straight, reverse);
                    if (value > top)
                    {
                        top = value;
                        topToggle = t;
                        topSwap = reverse > straight;
                    }
                }
                if (topToggle < 0 || top <= Forbidden)
                    continue;
                Tuple<Resource, Resource> toggle = toggles[topToggle];
                Place(pair[0], topSwap ? toggle.Item2 : toggle.Item1, occupancy, assigned);
                Place(pair[1], topSwap ? toggle.Item1 : toggle.Item2, occupancy, assigned);
            }
        }

        private static void Place(Demand demand, Resource resource, Occupancy occupancy, List<Tuple<Demand, Resource>> assigned)
        {
            assigned.Add(Tuple.Create(demand, resource));
            occupancy.Add(resource.Key, demand.Planes);
        }

        // Functions that share one hat, switch or 2D stick in real aircraft (TMS up/down/left/right/press, slew X/Y ...) go
        // on one physical hat / ministick together: every group is matched to a set of controls with a common prefix
        // (stick.hat.tms.*, throttle.axis.ministick.*), members by their own position preferences.
        private static void AssignGroups(List<Demand> demands, List<Resource> resources, Occupancy occupancy, List<Tuple<Demand, Resource>> assigned, bool strongOnly)
        {
            HashSet<Demand> placed = new HashSet<Demand>(assigned.Select(a => a.Item1));
            List<List<Demand>> groups = demands.Where(d => d.Slot.Group.Length > 0 && !placed.Contains(d))
                .GroupBy(d => d.Slot.Group.Split(':')[0], StringComparer.Ordinal).Where(g => g.Count() >= 2).Select(g => g.ToList()).ToList();
            List<List<Resource>> sets = resources.Where(r => GroupRoles.Contains(LastSegment(r.Control)))
                .GroupBy(r => r.Device.JoyProName + "|" + r.Layer.ToString(CultureInfo.InvariantCulture) + "|" + r.IsAxis + "|" + Prefix(r.Control), StringComparer.Ordinal)
                .Where(g => g.Count() >= 2).Select(g => g.ToList()).ToList();
            if (groups.Count == 0 || sets.Count == 0)
                return;

            // The most important group first (frequency x aircraft: trim before a dogfight switch of three aircraft), each on
            // its best free set.
            foreach (List<Demand> group in groups.OrderByDescending(Importance).ThenBy(g => g[0].Slot.Group, StringComparer.Ordinal))
            {
                double top = Forbidden;
                List<Tuple<Demand, Resource>> topPlacement = null;
                foreach (List<Resource> set in sets)
                {
                    List<Tuple<Demand, Resource>> placement;
                    double value = EvaluateGroup(group, set, occupancy, strongOnly, out placement);
                    if (value > top)
                    {
                        top = value;
                        topPlacement = placement;
                    }
                }
                if (topPlacement == null)
                    continue;
                foreach (Tuple<Demand, Resource> item in topPlacement)
                    Place(item.Item1, item.Item2, occupancy, assigned);
            }
        }

        private static double Importance(List<Demand> members)
        {
            return members.Sum(d => Weight(d.Slot.Frequency) * OverlapCount(d.Planes, d.Planes));
        }

        // Members take their best free matching control of the set, best first; members of other aircraft may share one.
        // Every member left out costs, so a complete free hat (even on a modifier layer) beats a partly used one.
        private static double EvaluateGroup(List<Demand> members, List<Resource> set, Occupancy occupancy, bool strongOnly, out List<Tuple<Demand, Resource>> placement)
        {
            placement = new List<Tuple<Demand, Resource>>();
            List<Tuple<Demand, Resource, double>> options = new List<Tuple<Demand, Resource, double>>();
            foreach (Demand member in members)
            {
                foreach (Resource resource in set)
                {
                    if ((PrefIndex(member.Slot, resource.Control) >= 0 || IsStyle(member, resource)) && (!strongOnly || IsStrong(member, resource)) &&
                        occupancy.Free(resource, member.Planes))
                    {
                        double value = Score(member, resource);
                        if (value > Forbidden)
                            options.Add(Tuple.Create(member, resource, value));
                    }
                }
            }
            double total = 0;
            HashSet<Demand> done = new HashSet<Demand>();
            foreach (Tuple<Demand, Resource, double> option in options.OrderByDescending(o => o.Item3))
            {
                if (done.Contains(option.Item1) || placement.Any(p => p.Item2 == option.Item2 && Overlaps(p.Item1.Planes, option.Item1.Planes)))
                    continue;
                placement.Add(Tuple.Create(option.Item1, option.Item2));
                done.Add(option.Item1);
                total += option.Item3;
            }
            return placement.Count >= 2 ? total + 40.0 * placement.Count - 150.0 * (members.Count - placement.Count) : Forbidden;
        }

        // Single functions: the most constrained one first (largest gap between its best and second best free input,
        // "regret"), each on its best input that no relation of the same aircraft uses yet.
        private static void AssignGreedy(List<Demand> demands, List<Resource> resources, Occupancy occupancy, List<Tuple<Demand, Resource>> assigned, bool strongOnly)
        {
            int count = demands.Count;
            if (count == 0 || resources.Count == 0)
                return;
            double[][] score = new double[count][];
            for (int d = 0; d < count; d++)
            {
                score[d] = new double[resources.Count];
                for (int r = 0; r < resources.Count; r++)
                    score[d][r] = strongOnly && !IsStrong(demands[d], resources[r]) ? Forbidden : Score(demands[d], resources[r]);
            }
            int[] best = new int[count];
            int[] second = new int[count];
            bool[] done = new bool[count];
            int[] breadth = demands.Select(d => OverlapCount(d.Planes, d.Planes)).ToArray();
            Action<int> refresh = d =>
            {
                best[d] = -1;
                second[d] = -1;
                for (int r = 0; r < resources.Count; r++)
                {
                    double value = score[d][r];
                    if (value <= Forbidden || !occupancy.Free(resources[r], demands[d].Planes))
                        continue;
                    if (best[d] < 0 || value > score[d][best[d]])
                    {
                        second[d] = best[d];
                        best[d] = r;
                    }
                    else if (second[d] < 0 || value > score[d][second[d]])
                    {
                        second[d] = r;
                    }
                }
            };
            for (int d = 0; d < count; d++)
                refresh(d);
            while (true)
            {
                int pick = -1;
                double pickRegret = 0;
                double pickTop = 0;
                for (int d = 0; d < count; d++)
                {
                    if (done[d] || best[d] < 0)
                        continue;
                    double top = score[d][best[d]];
                    double regret = second[d] < 0 ? 1e6 : top - score[d][second[d]];
                    if (pick < 0 || regret > pickRegret || (regret == pickRegret && (top > pickTop || (top == pickTop && breadth[d] > breadth[pick]))))
                    {
                        pick = d;
                        pickRegret = regret;
                        pickTop = top;
                    }
                }
                if (pick < 0)
                    break;
                int chosen = best[pick];
                Place(demands[pick], resources[chosen], occupancy, assigned);
                done[pick] = true;
                for (int d = 0; d < count; d++)
                {
                    if (!done[d] && (best[d] == chosen || second[d] == chosen))
                        refresh(d);
                }
            }
        }

        private static string LastSegment(string control)
        {
            int dot = control.LastIndexOf('.');
            return dot >= 0 ? control.Substring(dot + 1) : control;
        }

        private static string Prefix(string control)
        {
            int dot = control.LastIndexOf('.');
            return dot >= 0 ? control.Substring(0, dot) : string.Empty;
        }

        private static double PairScore(Demand first, Resource firstResource, Demand second, Resource secondResource, Occupancy occupancy, bool strongOnly)
        {
            if (!occupancy.Free(firstResource, first.Planes) || !occupancy.Free(secondResource, second.Planes))
                return Forbidden;
            if (strongOnly && (!IsStrong(first, firstResource) || !IsStrong(second, secondResource)))
                return Forbidden;
            double a = Score(first, firstResource);
            double b = Score(second, secondResource);
            return a <= Forbidden || b <= Forbidden ? Forbidden : (a + b) / 2.0 + 40.0;
        }

        // How well a physical control fits a function: its rank in the function's preferred controls (exact positions above
        // wildcards), the panel label, the user's previous bind, how often the function is used, and the modifier layer.
        private static double Score(Demand demand, Resource resource)
        {
            if (demand.IsAxis != resource.IsAxis)
                return Forbidden;
            SlotControl slot = demand.Slot;
            bool style = demand.StyleControl.Length > 0 && string.Equals(demand.StyleControl, resource.Control, StringComparison.Ordinal);
            int pref = PrefIndex(slot, resource.Control);
            string family = Family(resource.Control);
            bool grip = IsGrip(resource.Control);
            if (!style && pref < 0)
            {
                // Dedicated controls only for their functions, MFD keys only on MFD keys, cockpit panel functions off the grips.
                if (Dedicated.IsMatch(resource.Control) || (demand.IsAxis && DedicatedAxis.IsMatch(resource.Control)))
                    return Forbidden;
                if (slot.Prefs.Count > 0 && slot.Prefs.All(p => p.StartsWith("mfd.", StringComparison.Ordinal)))
                    return Forbidden;
                if (grip && (slot.Side == "panel" || slot.Side == "any") && (resource.Layer > 0 || !NumberedThrottleButton.IsMatch(resource.Control)))
                    return Forbidden;
            }

            double fit;
            double exact = 0;
            if (pref >= 0)
            {
                fit = 100 - 8 * pref;
                if (pref == 0 && slot.Prefs[0].IndexOf('*') < 0)
                    exact = 100;
                else if (Named.IsMatch(resource.Control))
                    fit -= 40;
            }
            else
            {
                if (slot.Side == family || (slot.Side == "collective" && family == "throttle"))
                    fit = 25;
                else if (slot.Side == "any" || slot.Side == "panel")
                    fit = family == "panel" || family == "mfd" ? 18 : 12;
                else
                    fit = demand.IsAxis ? 4 : 8;
                // A specific axis (pitch, rudder ...) never goes on an unrelated axis family.
                if (demand.IsAxis && slot.Prefs.Count > 0 && slot.Prefs.All(p => !p.StartsWith("*")) && slot.Side != "panel" && slot.Side != "any")
                    fit = 2;
            }

            double label = 0;
            if (LabelMatches(slot, resource))
                label = 150;
            else if (resource.Label.Length > 0 && family == "panel")
                label = -60;

            double weight = Weight(slot.Frequency);
            double total = fit * weight + exact + label + RoleLabel(slot, resource);
            if (style)
                total += 500 + (demand.StyleShifted == (resource.Layer > 0) ? 100 : 0);
            if (resource.Layer > 0)
                total -= (15 + 10 * weight) * resource.Layer + (grip ? 0 : 20);
            if (resource.Type == "latched" && slot.PairGroup.Length == 0)
                total -= 35 * weight;
            if (grip && weight <= 1 && fit < 50)
                total -= 20;
            return total;
        }

        private static int PrefIndex(SlotControl slot, string control)
        {
            for (int i = 0; i < slot.Prefs.Count; i++)
            {
                if (PatternMatch(slot.Prefs[i], control))
                    return i;
            }
            return -1;
        }

        private static double Weight(string frequency)
        {
            switch (frequency)
            {
                case "constant": return 5;
                case "frequent": return 3;
                case "occasional": return 2;
                default: return 1;
            }
        }

        private static string Family(string control)
        {
            int dot = control.IndexOf('.');
            return dot > 0 ? control.Substring(0, dot) : control;
        }

        private static bool GripFamily(string control)
        {
            string family = Family(control);
            return family == "stick" || family == "throttle" || family == "collective" || family == "yoke";
        }

        private static bool IsGrip(string control)
        {
            string family = Family(control);
            return (family == "stick" || family == "throttle" || family == "collective" || family == "yoke") && control.IndexOf(".base.", StringComparison.Ordinal) < 0;
        }

        // Prefs use '*' for "any number / any part" (stick.hat.*.up, panel.button.*, *.axis.*).
        private static bool PatternMatch(string pattern, string control)
        {
            if (pattern.IndexOf('*') < 0)
                return string.Equals(pattern, control, StringComparison.OrdinalIgnoreCase);
            string regex = "^" + Regex.Escape(pattern).Replace("\\*", "[^.]+(\\.[^.]+)*?") + "$";
            return Regex.IsMatch(control, regex, RegexOptions.IgnoreCase);
        }

        // Modifier buttons, only when the grip buttons do not suffice for the grip functions: the positions of the profile's
        // own modifiers first (by product, most used first), then the pinky lever/button (the classic shift), then others.
        private static List<Modifier> ChooseModifiers(List<Resource> resources, List<Demand> demands, int maxModifiers, List<RosterDevice> roster)
        {
            List<Modifier> modifiers = new List<Modifier>();
            int gripButtons = resources.Count(r => !r.IsAxis && r.Layer == 0 && IsGrip(r.Control) && !Dedicated.IsMatch(r.Control));
            // The busiest aircraft decides: grip functions of one aircraft that do not fit on the grip buttons.
            Dictionary<int, int> perPlane = new Dictionary<int, int>();
            foreach (Demand demand in demands.Where(d => !d.IsAxis && (d.Slot.Side == "stick" || d.Slot.Side == "throttle" || d.Slot.Side == "collective")))
            {
                for (int bit = 0; bit < demand.Planes.Length * 64; bit++)
                {
                    if ((demand.Planes[bit >> 6] & (1UL << (bit & 63))) != 0)
                        perPlane[bit] = (perPlane.ContainsKey(bit) ? perPlane[bit] : 0) + 1;
                }
            }
            int gripDemands = perPlane.Count == 0 ? 0 : perPlane.Values.Max();
            if (gripDemands <= gripButtons || maxModifiers <= 0 || gripButtons == 0)
                return modifiers;
            int wanted = Math.Min(maxModifiers, (int)Math.Ceiling((gripDemands - gripButtons) / (double)gripButtons));

            Func<Resource, bool> free = r => !r.IsAxis && r.Layer == 0 && !modifiers.Any(m => m.device == r.Device.JoyProName && m.key == r.Input);
            Dictionary<string, int> uses = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Bind bind in InternalDataManagement.AllBinds.Values.Where(b => b != null && b.AllReformers != null))
            {
                foreach (string reformer in bind.AllReformers)
                {
                    Modifier parsed = Modifier.ReformerToMod(reformer);
                    if (parsed != null)
                        uses[parsed.name ?? string.Empty] = (uses.ContainsKey(parsed.name ?? string.Empty) ? uses[parsed.name ?? string.Empty] : 0) + 1;
                }
            }
            foreach (Modifier existing in InternalDataManagement.AllModifiers.Values.Where(m => m != null)
                         .OrderByDescending(m => uses.ContainsKey(m.name ?? string.Empty) ? uses[m.name ?? string.Empty] : 0).ThenBy(m => m.name, StringComparer.Ordinal))
            {
                if (modifiers.Count >= wanted)
                    break;
                string device = MapDevice(existing.device, roster);
                Resource position = resources.FirstOrDefault(r => free(r) && r.Device.JoyProName == device && string.Equals(r.Input, existing.key, StringComparison.OrdinalIgnoreCase) && IsGrip(r.Control));
                if (position == null)
                    continue;
                modifiers.Add(device == existing.device ? existing : new Modifier { name = existing.name + " (" + ProductName(device) + ")", device = device, key = existing.key, sw = existing.sw });
            }

            string[] order = { "stick.pinky.lever", "stick.pinky.button", "throttle.pinky.lever", "throttle.pinky.button", "throttle.pinky.fwd", "stick.button.index_lower", "stick.button.index.*", "throttle.button.*" };
            foreach (string pattern in order)
            {
                while (modifiers.Count < wanted)
                {
                    Resource candidate = resources.FirstOrDefault(r => free(r) && PatternMatch(pattern, r.Control));
                    if (candidate == null)
                        break;
                    modifiers.Add(new Modifier
                    {
                        name = "JoyPro Shift " + (modifiers.Count + 1).ToString(CultureInfo.InvariantCulture),
                        device = candidate.Device.JoyProName,
                        key = candidate.Input,
                        sw = false
                    });
                }
            }
            return modifiers;
        }

        private static List<Resource> BuildResources(List<RosterDevice> roster)
        {
            List<Resource> resources = new List<Resource>();
            bool hasRightStick = roster.Any(r => r.Model.Kind == "stick" && r.Model.Hand != "left") || roster.Any(r => r.Model.Kind == "hotas");
            foreach (RosterDevice device in roster)
            {
                // A left-hand stick next to a right-hand one works as the throttle side (dual-stick setups).
                bool leftStick = device.Model.Kind == "stick" && device.Model.Hand == "left" && hasRightStick &&
                                 roster.Count(r => r.Model.Kind == "stick" || r.Model.Kind == "hotas") > 1;
                IEnumerable<KbControl> axes = (device.BaseModel != null ? device.BaseModel.Axes : Enumerable.Empty<KbControl>()).Concat(device.Model.Axes);
                IEnumerable<KbControl> controls = (device.BaseModel != null ? device.BaseModel.Controls : Enumerable.Empty<KbControl>()).Concat(device.Model.Controls);
                foreach (KbControl axis in axes.Where(a => a.Input.Length > 0))
                    resources.Add(new Resource { Device = device, Input = axis.Input, Control = leftStick ? LeftStick(axis.Control) : axis.Control, IsAxis = true, Type = "analog" });
                foreach (KbControl control in controls.Where(c => c.Input.Length > 0 && !Diagonal.IsMatch(c.Control)))
                {
                    if (resources.Any(r => r.Device == device && r.Input == control.Input))
                        continue;
                    resources.Add(new Resource
                    {
                        Device = device, Input = control.Input, Control = leftStick ? LeftStick(control.Control) : control.Control,
                        Label = control.Label ?? string.Empty, Type = control.Type, IsAxis = false
                    });
                }

                // Inputs the device reports but its catalog entry does not describe (unknown buttons, an extra POV hat):
                // usable as plain base buttons / hats, and the user's previous binds on them are recognised.
                foreach (KeyValuePair<string, string> extra in ExtraInputs(device))
                {
                    if (resources.Any(r => r.Device == device && string.Equals(r.Input, extra.Key, StringComparison.OrdinalIgnoreCase)) ||
                        axes.Any(a => string.Equals(a.Input, extra.Key, StringComparison.OrdinalIgnoreCase)) ||
                        controls.Any(c => string.Equals(c.Input, extra.Key, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    bool isAxis = extra.Value.IndexOf(".axis.", StringComparison.Ordinal) >= 0;
                    resources.Add(new Resource
                    {
                        Device = device, Input = extra.Key, Control = leftStick ? LeftStick(extra.Value) : extra.Value,
                        IsAxis = isAxis, Type = isAxis ? "analog" : "momentary"
                    });
                }
            }
            return resources;
        }

        private static IEnumerable<KeyValuePair<string, string>> ExtraInputs(RosterDevice device)
        {
            string kind = device.Model.Kind;
            string family = kind == "stick" || kind == "throttle" || kind == "collective" || kind == "yoke" ? kind : kind == "pedals" ? "pedals" : "panel";
            bool grip = family != "panel" && family != "pedals";
            int n = 0;
            if (family == "pedals")
                yield break; // pedal buttons are calibration / mode buttons
            foreach (int axis in device.AxisSlots.Where(a => a >= 0 && a <= 7))
                yield return new KeyValuePair<string, string>(GameInputTokens.AxisToken(axis), (family == "pedals" ? "panel" : family) + ".axis.extra" + (++n).ToString(CultureInfo.InvariantCulture));
            for (int b = 0; b < device.ButtonCount; b++)
            {
                string number = (b + 1).ToString(CultureInfo.InvariantCulture);
                yield return new KeyValuePair<string, string>(GameInputTokens.ButtonToken(b), grip ? family + ".base.button.in" + number : "panel.button.in" + number);
            }
            string[] dirs = { "up", null, "right", null, "down", null, "left", null };
            for (int h = 0; h < device.PovCount; h++)
            {
                string hat = (h + 1).ToString(CultureInfo.InvariantCulture);
                for (int d = 0; d < 8; d += 2)
                    yield return new KeyValuePair<string, string>(GameInputTokens.PovToken(h, d), grip ? family + ".hat.pov" + hat + "." + dirs[d] : "panel.button.pov" + hat + dirs[d]);
            }
        }

        private static string LeftStick(string control)
        {
            switch (control)
            {
                case "stick.axis.pitch": return "throttle.axis.main";
                case "stick.axis.roll": return "throttle.axis.ministick.x";
                case "stick.axis.twist": return "throttle.axis.rotary.1";
            }
            if (control.StartsWith("stick.axis.", StringComparison.Ordinal))
                return "throttle.axis.slider.1";
            if (control.StartsWith("stick.trigger.", StringComparison.Ordinal) || control == "stick.weapon_release")
                return "throttle.button.1" + control.Length.ToString(CultureInfo.InvariantCulture);
            if (control.StartsWith("stick.hat.", StringComparison.Ordinal))
                return "throttle.hat." + control.Substring("stick.hat.".Length);
            if (control.StartsWith("stick.pinky.", StringComparison.Ordinal))
                return "throttle.pinky." + control.Substring("stick.pinky.".Length);
            return "throttle." + control.Substring("stick.".Length);
        }

        private static string BindKey(Bind bind, bool axis, List<RosterDevice> devices)
        {
            string modifier = bind.AllReformers == null || bind.AllReformers.Count == 0 ? string.Empty : bind.AllReformers[0];
            Modifier parsed = Modifier.ReformerToMod(modifier);
            return MapDevice(bind.Joystick, devices) + "|" + (axis ? bind.JAxis : bind.JButton) + "|" +
                   (parsed == null ? string.Empty : MapDevice(parsed.device, devices) + "/" + parsed.key);
        }

        // relation -> function slot (EngineRelationPurpose): the user's choice, the relation's name (Auto Group, optionally
        // with a preset suffix "[...]"), or the function (nearly) all of its actions are. A button position (each
        // aircraft uses it for its own function) and a relation set to "None" have no slot: they are placed like any
        // other relation of their kind, by the user's own layout first.
        private static Dictionary<string, string> RelationSlots(Dictionary<string, EngineAutoGroup.Slot> slots, List<EngineAutoGroup.Row> rows)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            EngineRelationPurpose.Index index = new EngineRelationPurpose.Index(slots, rows);
            foreach (Relation relation in InternalDataManagement.AllRelations.Values.Where(r => r != null))
            {
                EngineRelationPurpose.Verdict verdict = EngineRelationPurpose.Judge(relation, index);
                if (verdict.Slot != null)
                    map[relation.NAME] = verdict.Slot.Id;
            }
            return map;
        }

        private static Dictionary<string, SlotControl> LoadSlotControls(EngineAutoAssignResult result)
        {
            Dictionary<string, SlotControl> map = new Dictionary<string, SlotControl>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(EngineNlGroup.FunctionsFolder(), "slot_controls.tsv");
            if (!File.Exists(path))
            {
                result.Warnings.Add("slot_controls.tsv missing in DB\\Functions; every function is treated as a generic button.");
                return map;
            }
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;
                string[] parts = line.Split('\t');
                if (parts.Length < 7)
                    continue;
                string[] pair = parts[3].Split(':');
                map[parts[0]] = new SlotControl
                {
                    Slot = parts[0],
                    Side = parts[1],
                    Prefs = parts[2].Split(',').Where(p => p.Length > 0).ToList(),
                    PairGroup = pair.Length == 2 ? pair[0] : string.Empty,
                    PairRole = pair.Length == 2 ? pair[1] : string.Empty,
                    Labels = parts[4].Split(';').Where(p => p.Length > 0).ToList(),
                    Frequency = parts[5],
                    Axis = parts[6] == "axis",
                    Group = parts.Length > 7 ? parts[7].Trim() : string.Empty
                };
            }
            return map;
        }

        // The setup as shown to the user: every device with its model, grip choices and whether it takes part.
        internal static List<EngineAutoAssignDevice> DescribeRoster(List<RosterDevice> roster, out Dictionary<RosterDevice, RosterDevice> alternatives)
        {
            alternatives = ChooseActive(roster);
            List<EngineAutoAssignDevice> devices = new List<EngineAutoAssignDevice>();
            foreach (RosterDevice device in roster.OrderBy(r => r.JoyProName, StringComparer.Ordinal))
            {
                EngineAutoAssignDevice described = Describe(device);
                RosterDevice primary;
                if (alternatives.TryGetValue(device, out primary))
                    described.AlternativeTo = primary.JoyProName;
                devices.Add(described);
            }
            return devices;
        }

        private static EngineAutoAssignDevice Describe(RosterDevice device)
        {
            return new EngineAutoAssignDevice
            {
                Device = device.JoyProName,
                Connected = device.Connected,
                Model = device.Model == null ? string.Empty : device.Model.Name,
                ModelId = device.Model == null ? string.Empty : device.Model.Id,
                Kind = device.Model == null ? string.Empty : device.Model.Kind,
                Generic = device.Generic,
                Note = device.Note,
                AlternativeTo = string.Empty,
                GripChoices = device.GripChoices.Select(g => new EngineAutoAssignGrip { Id = g.Id, Name = g.Name, Image = g.Image }).ToList()
            };
        }

        public static string ToText(EngineAutoAssignResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Auto Assign");
            sb.AppendLine("Dry Run: " + result.DryRun);
            AppendDevices(sb, result.Devices);
            sb.AppendLine("Kept Existing Binds: " + result.KeptExisting.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Assigned: " + result.Assignments.Count.ToString(CultureInfo.InvariantCulture) +
                          (result.StyleKept > 0 ? " (" + result.StyleKept.ToString(CultureInfo.InvariantCulture) + " on the same control as before)" : string.Empty));
            sb.AppendLine("Unassigned: " + result.Unassigned.Count.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Modifiers: " + (result.ModifiersUsed.Count == 0 ? "none" : string.Join("; ", result.ModifiersUsed)));
            sb.AppendLine("Assignments:");
            foreach (EngineAutoAssignment a in result.Assignments)
                sb.AppendLine("  - " + a.Relation + " => " + a.Device + " " + a.Input + (a.Modifier.Length > 0 ? " + " + a.Modifier : string.Empty) + " (" + a.Control + ")");
            if (result.Unassigned.Count > 0)
            {
                sb.AppendLine("Not Assigned (no fitting free input):");
                foreach (string name in result.Unassigned)
                    sb.AppendLine("  - " + name);
            }
            if (result.Explanations.Count > 0)
            {
                sb.AppendLine("Explain:");
                foreach (string line in result.Explanations)
                    sb.AppendLine("  " + line);
            }
            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                foreach (string warning in result.Warnings.Distinct())
                    sb.AppendLine("  - " + warning);
            }
            if (result.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                foreach (string error in result.Errors)
                    sb.AppendLine("  - " + error);
            }
            return sb.ToString();
        }

        public static string DevicesToText(List<EngineAutoAssignDevice> devices, List<string> warnings)
        {
            StringBuilder sb = new StringBuilder();
            AppendDevices(sb, devices);
            foreach (string warning in warnings.Distinct())
                sb.AppendLine("Warning: " + warning);
            return sb.ToString();
        }

        private static void AppendDevices(StringBuilder sb, List<EngineAutoAssignDevice> devices)
        {
            sb.AppendLine("Devices:");
            foreach (EngineAutoAssignDevice device in devices)
            {
                sb.AppendLine("  - " + device.Device + (device.Connected ? string.Empty : " (not connected)") + " => " +
                              (device.Model.Length > 0 ? device.Model + " [" + device.Kind + "]" : "grip needed") + (device.Generic ? " (generic)" : string.Empty) +
                              (device.AlternativeTo.Length > 0 ? " - left out, same role as " + device.AlternativeTo : string.Empty) +
                              (device.Note.Length > 0 ? " - " + device.Note : string.Empty));
                foreach (EngineAutoAssignGrip grip in device.GripChoices)
                    sb.AppendLine("      grip: " + grip.Id + " | " + grip.Name + (grip.Image.Length > 0 ? " | " + grip.Image : string.Empty));
            }
        }
    }

    internal sealed class AutoAssignOptions
    {
        public Dictionary<string, string> GripChoices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> AddedDevices = new List<string>();
        public List<string> IgnoredDevices = new List<string>();
        public HashSet<string> Relations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public int MaxModifiers = 2;
        public bool Overwrite;
        public bool IgnoreStyle;
        public bool IncludeMissing;
        public bool DryRun;
        public HashSet<string> Explain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class EngineAutoAssignGrip
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Image { get; set; }
    }

    public sealed class EngineAutoAssignDevice
    {
        public EngineAutoAssignDevice()
        {
            Device = Model = ModelId = Kind = Note = AlternativeTo = string.Empty;
            GripChoices = new List<EngineAutoAssignGrip>();
        }

        public string Device { get; set; }
        public bool Connected { get; set; }
        public string Model { get; set; }
        public string ModelId { get; set; }
        public string Kind { get; set; }
        public bool Generic { get; set; }
        public string Note { get; set; }
        public string AlternativeTo { get; set; }
        public List<EngineAutoAssignGrip> GripChoices { get; set; }
    }

    public sealed class EngineAutoAssignment
    {
        public string Relation { get; set; }
        public string Slot { get; set; }
        public string Device { get; set; }
        public string Input { get; set; }
        public string Modifier { get; set; }
        public string Control { get; set; }
        public double Score { get; set; }
        public bool SameAsBefore { get; set; }
    }

    public sealed class EngineAutoAssignResult
    {
        public bool DryRun { get; set; }
        public int MaxModifiers { get; set; }
        public int KeptExisting { get; set; }
        public int StyleKept { get; set; }
        public List<EngineAutoAssignDevice> Devices { get; set; }
        public List<EngineAutoAssignment> Assignments { get; set; }
        public List<string> Unassigned { get; set; }
        public List<string> ModifiersUsed { get; set; }
        public List<string> Explanations { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public EngineAutoAssignResult()
        {
            Devices = new List<EngineAutoAssignDevice>();
            Assignments = new List<EngineAutoAssignment>();
            Unassigned = new List<string>();
            ModifiersUsed = new List<string>();
            Explanations = new List<string>();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }
}
