using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Games;
using JoyPro.Setup;

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
            public List<string> Phases = new List<string>();
            public string Crit = string.Empty;
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
            public string Phase = string.Empty;   // a position of the mode switch (a2a, a2g): a phase layer
            public bool Replica;                  // a cockpit-replica panel: only the functions its labels name
            public bool MfdDevice;                // an MFD: only MFD functions

            public string Key
            {
                get { return Device.JoyProName + "|" + Input + "|" + (Modifier == null ? string.Empty : Modifier.device + "/" + Modifier.key); }
            }

            // The input on the normal layer (no modifier).
            public string BaseKey
            {
                get { return Device.JoyProName + "|" + Input + "|"; }
            }
        }

        // Which aircraft / games already use an input (bit set over all aircraft of the profile).
        private sealed class Occupancy
        {
            private readonly Dictionary<string, ulong[]> used = new Dictionary<string, ulong[]>(StringComparer.OrdinalIgnoreCase);
            // Per key the aircraft bits of each kind of occupant: "" = needed in every phase, a2a / a2g = one combat phase only.
            private readonly Dictionary<string, Dictionary<string, ulong[]>> byPhase = new Dictionary<string, Dictionary<string, ulong[]>>(StringComparer.OrdinalIgnoreCase);
            // Normal-layer key -> the keys of the same input on the mode switch's phase layers, with their phase.
            public readonly Dictionary<string, List<KeyValuePair<string, string>>> PhaseKeys = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);

            // The normal layer and a phase layer of one input: while the switch is on that phase the input does the phase
            // layer's function, so the normal-layer function must be one of the other combat phase only.
            public bool Free(Resource resource, ulong[] planes, string exclusive = "")
            {
                if (!Free(resource.Key, planes))
                    return false;
                if (resource.Phase.Length > 0)
                {
                    Dictionary<string, ulong[]> normal;
                    if (!byPhase.TryGetValue(resource.BaseKey, out normal))
                        return true;
                    return !normal.Any(n => (n.Key.Length == 0 || n.Key == resource.Phase) && Overlaps(n.Value, planes));
                }
                List<KeyValuePair<string, string>> keys;
                if (resource.Modifier == null && PhaseKeys.TryGetValue(resource.Key, out keys))
                    return keys.All(k => (exclusive.Length > 0 && exclusive != k.Value) || Free(k.Key, planes));
                return true;
            }

            public bool Free(string key, ulong[] planes)
            {
                ulong[] bits;
                return !used.TryGetValue(key, out bits) || !Overlaps(bits, planes);
            }

            public void Add(string key, ulong[] planes, string exclusive = "")
            {
                ulong[] bits;
                if (!used.TryGetValue(key, out bits))
                    used[key] = bits = new ulong[planes.Length];
                for (int i = 0; i < planes.Length && i < bits.Length; i++)
                    bits[i] |= planes[i];
                Dictionary<string, ulong[]> kinds;
                if (!byPhase.TryGetValue(key, out kinds))
                    byPhase[key] = kinds = new Dictionary<string, ulong[]>(StringComparer.Ordinal);
                if (!kinds.TryGetValue(exclusive ?? string.Empty, out bits))
                    kinds[exclusive ?? string.Empty] = bits = new ulong[planes.Length];
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
            public List<string> Phases = new List<string>();
            public string Crit = string.Empty;
            public string Exclusive = string.Empty;   // a2a / a2g: only used in that combat phase
            public double RoleFactor = 1;             // how much its phases matter for the roles of its aircraft
            public bool ShiftOk = true;               // every game of it handles modifier binds
            public bool PhaseOk = true;               // every game of it handles held (latched) modifiers
            public string DirKey = string.Empty;      // a directional set (tms, dms, cms, trim ...) and its direction
            public string DirRole = string.Empty;
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
                Demand demand = new Demand { Relation = relation, Planes = planes.Bits(relation), Slot = slot, IsAxis = relation.ISAXIS, Previous = bound ? bind : null };
                Rank(demand, catalogAircraft);
                demands.Add(demand);
            }

            List<Resource> resources = BuildResources(usable);
            HashSet<string> directional = SetupPhases.DirectionalGroups(slotControls.Values.Select(s => s.Group));
            foreach (Demand demand in demands)
            {
                string key, role;
                if (SetupPhases.DirectionalMember(demand.Slot.Group, directional, out key, out role))
                {
                    demand.DirKey = key;
                    demand.DirRole = role;
                }
            }
            if (!options.IgnoreStyle)
                LearnStyle(demands, usable, resources);
            // New or unused aircraft: the control the user's most used aircraft of the class has for the function.
            if (!options.IgnoreStyle && Program.UsageStyleEnabled)
                LearnUsageStyle(demands, usable, resources, relationSlots, catalogAircraft, result);

            // Phase layers on a latched mode switch (up = air to air, down = air to ground) when the grips are too few
            // (or always with --phase-layers on), and shift layers (at most options.MaxModifiers) only when the grip
            // buttons do not suffice. The mode switch's layers come on top of the shift buttons: no hand holds them.
            List<Resource> modeSwitch;
            List<Modifier> phaseModifiers = ChoosePhaseModifiers(resources, demands, options, result, out modeSwitch);
            List<Resource> unswitched = resources.Where(r => !modeSwitch.Contains(r)).ToList();
            List<Modifier> modifiers = ChooseModifiers(unswitched, demands, options.MaxModifiers, usable);
            List<Resource> layered = new List<Resource>(unswitched.Where(r => !modifiers.Any(m => m.device == r.Device.JoyProName && m.key == r.Input)));
            int layer = 0;
            int phaseLayer = 50;
            Occupancy layers = occupancy;
            foreach (Modifier modifier in modifiers.Concat(phaseModifiers))
            {
                bool phase = phaseModifiers.Contains(modifier);
                int index = phase ? ++phaseLayer : ++layer;
                foreach (Resource baseResource in unswitched.Where(r => !r.IsAxis && r.Layer == 0 && r.Type != "latched" && GripFamily(r.Control)))
                {
                    if (modifiers.Any(m => m.device == baseResource.Device.JoyProName && m.key == baseResource.Input))
                        continue;
                    Resource copy = new Resource
                    {
                        Device = baseResource.Device, Input = baseResource.Input, Control = baseResource.Control, Label = baseResource.Label,
                        Type = baseResource.Type, IsAxis = false, Layer = index, Modifier = modifier, Phase = phase ? PhaseOf(modifier) : string.Empty,
                        Replica = baseResource.Replica, MfdDevice = baseResource.MfdDevice
                    };
                    layered.Add(copy);
                    if (phase)
                    {
                        List<KeyValuePair<string, string>> keys;
                        if (!layers.PhaseKeys.TryGetValue(copy.BaseKey, out keys))
                            layers.PhaseKeys[copy.BaseKey] = keys = new List<KeyValuePair<string, string>>();
                        keys.Add(new KeyValuePair<string, string>(copy.Key, copy.Phase));
                    }
                }
            }
            modifiers.AddRange(phaseModifiers);

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
                // A member of a directional set (TMS, DMS, CMS, trim ...) goes only with its set on one hat (AssignGroups).
                AssignGreedy(demands.Where(d => !placed.Contains(d) && d.DirRole.Length == 0).ToList(), layered, occupancy, assigned, strongOnly);
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
                    Where = ResourceText(item.Item2),
                    Score = Math.Round(Score(item.Item1, item.Item2), 1),
                    SameAsBefore = style
                });
            }
            foreach (Demand demand in demands.Where(d => !assigned.Any(a => a.Item1 == d)))
                result.Unassigned.Add(demand.Relation.NAME);
            foreach (Demand demand in demands.Where(d => options.Explain.Contains(d.Relation.NAME)))
                result.Explanations.AddRange(Explain(demand, layered, assigned, planes));
            result.ModifiersUsed.AddRange(modifiers.Where(m => assigned.Any(a => a.Item2.Modifier == m)).Select(m => m.name + " = " + DeviceText(m.device) + " " + JoyPro.DeviceReference.DeviceVocabulary.InputName(m.key)));

            if (!options.DryRun)
                Apply(assigned, modifiers);
            return result;
        }

        // Why a relation landed where it did: its slot, previous control and the best candidates with their score and
        // the relations of shared aircraft that occupy them.
        private static IEnumerable<string> Explain(Demand demand, List<Resource> resources, List<Tuple<Demand, Resource>> assigned, PlaneIndex planes)
        {
            Tuple<Demand, Resource> mine = assigned.FirstOrDefault(a => a.Item1 == demand);
            List<string> wanted = demand.Slot.Prefs.Where(p => p.IndexOf('*') < 0).Take(3).Select(p => JoyPro.DeviceReference.DeviceVocabulary.Describe(p, string.Empty)).ToList();
            yield return demand.Relation.NAME + " (" + OverlapCount(demand.Planes, demand.Planes).ToString(CultureInfo.InvariantCulture) + " aircraft) => " +
                         (mine == null ? "not assigned" : ResourceText(mine.Item2)) +
                         (wanted.Count > 0 ? "; wanted: " + string.Join(", ", wanted) : string.Empty) +
                         (demand.StyleControl.Length > 0 ? "; before: " + JoyPro.DeviceReference.DeviceVocabulary.Describe(demand.StyleControl, string.Empty) + (demand.StyleShifted ? " (with a modifier)" : string.Empty) : string.Empty);
            foreach (Tuple<Resource, double> candidate in resources.Select(r => Tuple.Create(r, Score(demand, r))).Where(c => c.Item2 > Forbidden).OrderByDescending(c => c.Item2).Take(10))
            {
                List<string> blockers = assigned.Where(a => a.Item1 != demand && a.Item2.Key == candidate.Item1.Key && Overlaps(a.Item1.Planes, demand.Planes))
                    .Select(a => a.Item1.Relation.NAME + " (" + planes.Shared(a.Item1.Planes, demand.Planes) + ")").ToList();
                yield return "    " + Math.Round(candidate.Item2, 1).ToString(CultureInfo.InvariantCulture) + (IsStrong(demand, candidate.Item1) ? " (strong match)" : string.Empty) + " | " +
                             ResourceText(candidate.Item1) + (blockers.Count > 0 ? " | used by " + string.Join(", ", blockers) : string.Empty);
            }
        }

        // A device without its instance id ("{...}").
        internal static string DeviceText(string device)
        {
            return Regex.Replace(device ?? string.Empty, @"\s*\{[0-9A-Fa-f-]+\}\s*$", string.Empty).Trim();
        }

        // An input in plain words: "VKB Gladiator Hat 1 down (TMS hat down) + JoyPro Shift".
        private static string ResourceText(Resource resource)
        {
            string input = JoyPro.DeviceReference.DeviceVocabulary.InputName(resource.Input);
            string part = resource.Control.Length > 0 || resource.Label.Length > 0 ? JoyPro.DeviceReference.DeviceVocabulary.Describe(resource.Control, resource.Label) : string.Empty;
            return DeviceText(resource.Device.JoyProName) + " " + input + (part.Length > 0 && !string.Equals(part, input, StringComparison.OrdinalIgnoreCase) ? " (" + part + ")" : string.Empty) +
                   (resource.Modifier != null ? " + " + resource.Modifier.name : string.Empty);
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

        // The usage ranking as the style reference (docs/usage-statistics.md#style-reference): an unbound relation whose DCS
        // aircraft the user has not flown (yet) gets the control that the same function has in the user's most used
        // aircraft of the class (the reference relation's bind, matched by function slot). It then counts like the
        // relation's own previous control; nothing changes without recorded use.
        private static void LearnUsageStyle(List<Demand> demands, List<RosterDevice> devices, List<Resource> resources, Dictionary<string, string> relationSlots,
                                            Dictionary<string, EngineAutoGroup.Aircraft> catalogAircraft, EngineAutoAssignResult result)
        {
            List<Demand> open = demands.Where(d => d.StyleControl.Length == 0 && d.Previous == null && !string.IsNullOrEmpty(d.Slot.Slot)).ToList();
            if (open.Count == 0)
                return;
            HashSet<string> profilePlanes = new HashSet<string>(InternalDataManagement.AllRelations.Values.Where(r => r != null).SelectMany(DcsPlanes), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> refs = Program.UsageStyleReferences(profilePlanes, p =>
            {
                EngineAutoGroup.Aircraft a;
                return catalogAircraft.TryGetValue(p, out a) ? a.Type : string.Empty;
            });
            if (refs.Count == 0)
                return;
            HashSet<string> refPlanes = new HashSet<string>(refs.Values, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Tuple<string, bool>> refControls = new Dictionary<string, Tuple<string, bool>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> pair in InternalDataManagement.AllRelations.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                Bind bind;
                string slot;
                if (pair.Value == null || !relationSlots.TryGetValue(pair.Key, out slot) || !InternalDataManagement.AllBinds.TryGetValue(pair.Key, out bind) || bind == null)
                    continue;
                string input = pair.Value.ISAXIS ? bind.JAxis : bind.JButton;
                if (string.IsNullOrWhiteSpace(bind.Joystick) || string.IsNullOrWhiteSpace(input))
                    continue;
                string device = MapDevice(bind.Joystick, devices);
                Resource resource = resources.FirstOrDefault(r => r.Layer == 0 && r.Device.JoyProName == device && string.Equals(r.Input, input, StringComparison.OrdinalIgnoreCase));
                if (resource == null)
                    continue;
                foreach (string plane in DcsPlanes(pair.Value).Where(refPlanes.Contains))
                {
                    string key = slot + "|" + plane;
                    if (!refControls.ContainsKey(key))
                        refControls[key] = Tuple.Create(resource.Control, bind.AllReformers != null && bind.AllReformers.Count > 0);
                }
            }
            int styled = 0;
            foreach (Demand demand in open)
            {
                List<string> planes = DcsPlanes(demand.Relation).ToList();
                if (planes.Count == 0 || !planes.All(refs.ContainsKey))
                    continue;
                string refPlane = planes.Select(p => refs[p]).GroupBy(p => p, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First().Key;
                Tuple<string, bool> control;
                if (!refControls.TryGetValue(demand.Slot.Slot + "|" + refPlane, out control))
                    continue;
                demand.StyleControl = control.Item1;
                demand.StyleShifted = control.Item2;
                styled++;
            }
            result.UsageStyled = styled;
            result.UsageReferences = refs.Values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            if (styled > 0)
                result.Warnings.Add("Usage ranking: " + styled.ToString(CultureInfo.InvariantCulture) + " function(s) of aircraft you have not flown yet follow your most used aircraft (" +
                                    string.Join(", ", result.UsageReferences) + "). Settings > Usage switches this off.");
        }

        private static IEnumerable<string> DcsPlanes(Relation relation)
        {
            return (relation.NODES ?? new List<RelationItem>()).Where(n => n != null && n.AIRCRAFT != null && EngineAutoGroup.IsDcs(string.IsNullOrWhiteSpace(n.Game) ? "DCS" : n.Game))
                .SelectMany(n => n.AIRCRAFT.Where(a => a.Value).Select(a => a.Key)).Distinct(StringComparer.OrdinalIgnoreCase);
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

        // The position word of a printed label against the position the function asks for (LAUNCH BAR EXTEND, HOOK UP).
        private static double RoleLabel(SlotControl slot, Resource resource)
        {
            if (resource.Label.Length == 0 || IsGrip(resource.Control))
                return 0;
            string wanted = SetupPhases.FunctionDirection(slot.Slot, slot.PairRole);
            string printed = SetupPhases.LabelDirection(resource.Label);
            if (wanted.Length == 0 || printed.Length == 0)
                return 0;
            return wanted == printed ? 80 : -80;
        }

        // Printed labels of panels and bases (LDG GEAR, ANTI SKID ...), whole words; grip labels are descriptions.
        private static bool LabelMatches(SlotControl slot, Resource resource)
        {
            if (resource.Label.Length == 0 || IsGrip(resource.Control))
                return false;
            return SetupPhases.LabelNames(slot.Labels, slot.Slot, resource.Label);
        }

        private static bool IsStyle(Demand demand, Resource resource)
        {
            return demand.StyleControl.Length > 0 && string.Equals(demand.StyleControl, resource.Control, StringComparison.Ordinal) &&
                   demand.StyleShifted == (resource.Layer > 0);
        }

        // How an experienced pilot ranks the function (docs/auto-assign.md#flight-phases): its phases for the roles of its
        // aircraft, how fast it must be reached; and whether its games handle modifier binds.
        private static void Rank(Demand demand, Dictionary<string, EngineAutoGroup.Aircraft> aircraft)
        {
            demand.Phases = demand.Slot.Phases ?? new List<string>();
            demand.Crit = demand.Slot.Crit ?? string.Empty;
            demand.Exclusive = SetupPhases.ExclusivePhase(demand.Phases);
            double sum = 0;
            int count = 0;
            HashSet<string> games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RelationItem node in demand.Relation.NODES ?? new List<RelationItem>())
            {
                if (node == null)
                    continue;
                string game = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game;
                games.Add(EngineAutoGroup.IsDcs(game) ? "DCS" : game);
                IEnumerable<string> keys = EngineAutoGroup.IsDcs(game)
                    ? (node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value).Select(p => p.Key)
                    : new[] { game };
                foreach (string key in keys)
                {
                    EngineAutoGroup.Aircraft plane;
                    if (!aircraft.TryGetValue(key, out plane) || plane.Type == "layer" || plane.Type == "ground")
                        continue;
                    sum += SetupPhases.RoleFactor(SetupPhases.RoleKey(plane.Role, plane.Type), demand.Phases);
                    count++;
                }
            }
            demand.RoleFactor = count > 0 ? sum / count : SetupPhases.RoleFactor(string.Empty, demand.Phases);
            foreach (string game in games)
            {
                string support = EngineGameSupport.ModifierSupport(game);
                demand.ShiftOk &= SetupPhases.AllowsLayer(support, false);
                demand.PhaseOk &= SetupPhases.AllowsLayer(support, true);
            }
        }

        private static string PhaseOf(Modifier modifier)
        {
            string name = modifier.name ?? string.Empty;
            return name.EndsWith(" A2A", StringComparison.Ordinal) ? SetupPhases.A2A : name.EndsWith(" A2G", StringComparison.Ordinal) ? SetupPhases.A2G : string.Empty;
        }

        // The latched mode switch for phase layers (docs/auto-assign.md#phase-layers): with --phase-layers on whenever the
        // setup has one, with auto when the busiest aircraft has more grip functions than grip buttons and some of them
        // belong to one combat phase only. Up = air to air, down = air to ground; one position = the phase that matters less.
        private static List<Modifier> ChoosePhaseModifiers(List<Resource> resources, List<Demand> demands, AutoAssignOptions options, EngineAutoAssignResult result, out List<Resource> modeSwitch)
        {
            modeSwitch = new List<Resource>();
            List<Modifier> modifiers = new List<Modifier>();
            string mode = (options.PhaseLayers ?? "auto").ToLowerInvariant();
            if (mode == "off")
                return modifiers;
            List<Tuple<Resource, string, string, int>> positions = new List<Tuple<Resource, string, string, int>>();
            foreach (Resource resource in resources.Where(r => r.Layer == 0 && !r.IsAxis && r.Device.Model != null && r.Device.Model.Kind != "pedals"))
            {
                string key, position;
                int rank;
                if (!SetupPhases.ModeSwitchPosition(resource.Control, resource.Type, resource.Label, out key, out position, out rank))
                    continue;
                string kind = resource.Device.Model.Kind;
                rank += kind == "throttle" || kind == "collective" || kind == "quadrant" ? 0 : kind == "panel" ? 2 : 1;
                positions.Add(Tuple.Create(resource, resource.Device.JoyProName + "|" + key, position, rank));
            }
            IGrouping<string, Tuple<Resource, string, string, int>> best = positions.GroupBy(p => p.Item2, StringComparer.Ordinal)
                .OrderBy(g => g.Min(p => p.Item4)).ThenByDescending(g => g.Select(p => p.Item3).Distinct().Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
            if (best == null)
            {
                if (mode == "on")
                    result.Warnings.Add("Phase layers: no latched mode switch on your devices (a 2- or 3-position toggle switch on a base or collective).");
                return modifiers;
            }
            List<Demand> exclusive = demands.Where(d => !d.IsAxis && d.Exclusive.Length > 0 && d.PhaseOk && d.Slot.Side != "panel").ToList();
            if (exclusive.Count == 0)
                return modifiers;
            if (mode == "auto")
            {
                int gripButtons = resources.Count(r => !r.IsAxis && r.Layer == 0 && IsGrip(r.Control) && !Dedicated.IsMatch(r.Control));
                if (BusiestGripDemand(demands) <= gripButtons)
                    return modifiers;
            }
            string prefix = best.Key.Substring(best.Key.IndexOf('|') + 1);
            Resource up = best.Where(p => p.Item3 == "up").Select(p => p.Item1).FirstOrDefault();
            Resource down = best.Where(p => p.Item3 == "down").Select(p => p.Item1).FirstOrDefault();
            List<Resource> order = new[] { up, down }.Where(r => r != null).Take(2).ToList();
            List<string> phases;
            if (order.Count >= 2)
            {
                phases = new List<string> { SetupPhases.A2A, SetupPhases.A2G };
            }
            else
            {
                double a2a = exclusive.Where(d => d.Exclusive == SetupPhases.A2A).Sum(d => Weight(d) * OverlapCount(d.Planes, d.Planes));
                double a2g = exclusive.Where(d => d.Exclusive == SetupPhases.A2G).Sum(d => Weight(d) * OverlapCount(d.Planes, d.Planes));
                phases = new List<string> { a2a < a2g ? SetupPhases.A2A : SetupPhases.A2G };
            }
            for (int i = 0; i < order.Count; i++)
            {
                modifiers.Add(new Modifier
                {
                    name = "JoyPro Mode " + phases[i].ToUpperInvariant(),
                    device = order[i].Device.JoyProName,
                    key = order[i].Input,
                    sw = true
                });
            }
            RosterDevice switchDevice = best.First().Item1.Device;
            modeSwitch.AddRange(resources.Where(r => r.Device == switchDevice && (r.Control == prefix || r.Control.StartsWith(prefix + ".", StringComparison.Ordinal))));
            return modifiers;
        }

        // Grip functions of the busiest aircraft (one bit of the aircraft index).
        private static int BusiestGripDemand(List<Demand> demands)
        {
            Dictionary<int, int> perPlane = new Dictionary<int, int>();
            foreach (Demand demand in demands.Where(d => !d.IsAxis && (d.Slot.Side == "stick" || d.Slot.Side == "throttle" || d.Slot.Side == "collective")))
            {
                for (int bit = 0; bit < demand.Planes.Length * 64; bit++)
                {
                    if ((demand.Planes[bit >> 6] & (1UL << (bit & 63))) != 0)
                        perPlane[bit] = (perPlane.ContainsKey(bit) ? perPlane[bit] : 0) + 1;
                }
            }
            return perPlane.Count == 0 ? 0 : perPlane.Values.Max();
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
                Resource resource = resources.Where(r => IsStyle(demand, r) && occupancy.Free(r, demand.Planes, demand.Exclusive))
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
            occupancy.Add(resource.Key, demand.Planes, demand.Exclusive);
        }

        // Functions that share one hat, switch or 2D stick in real aircraft (TMS up/down/left/right/press, slew X/Y ...) go
        // on one physical hat / ministick together: every group is matched to a set of controls with a common prefix
        // (stick.hat.tms.*, throttle.axis.ministick.*), members by their own position preferences.
        private static void AssignGroups(List<Demand> demands, List<Resource> resources, Occupancy occupancy, List<Tuple<Demand, Resource>> assigned, bool strongOnly)
        {
            HashSet<Demand> placed = new HashSet<Demand>(assigned.Select(a => a.Item1));
            // Placed members (the user's own control, an earlier round) anchor their group: the others join that hat.
            Func<Resource, string> setKey = r => r.Device.JoyProName + "|" + r.Layer.ToString(CultureInfo.InvariantCulture) + "|" + r.IsAxis + "|" + Prefix(r.Control);
            Dictionary<Demand, Resource> where = assigned.GroupBy(a => a.Item1).ToDictionary(g => g.Key, g => g.First().Item2);
            List<List<Demand>> all = demands.Where(d => d.Slot.Group.Length > 0)
                .GroupBy(d => d.Slot.Group.Split(':')[0], StringComparer.Ordinal).Where(g => (g.Count() >= 2 || g.First().DirRole.Length > 0) && g.Any(d => !placed.Contains(d))).Select(g => g.ToList()).ToList();
            List<List<Demand>> groups = all.Select(g => g.Where(d => !placed.Contains(d)).ToList()).ToList();
            Dictionary<List<Demand>, HashSet<string>> anchorsOf = new Dictionary<List<Demand>, HashSet<string>>();
            for (int i = 0; i < all.Count; i++)
                anchorsOf[groups[i]] = new HashSet<string>(all[i].Where(where.ContainsKey).Select(d => setKey(where[d])), StringComparer.Ordinal);
            // A directional set the profile has only one member of (TMS up alone) still goes on its direction of a fitting hat.
            groups = groups.Where(g => g.Count >= 2 || (g.Count == 1 && (anchorsOf[g].Count > 0 || g[0].DirRole.Length > 0))).ToList();
            List<List<Resource>> sets = resources.Where(r => GroupRoles.Contains(LastSegment(r.Control)))
                .GroupBy(setKey, StringComparer.Ordinal)
                .Where(g => g.Count() >= 2).Select(g => g.ToList()).ToList();
            if (groups.Count == 0 || sets.Count == 0)
                return;

            // The most important group first (frequency x aircraft: trim before a dogfight switch of three aircraft), each on
            // its best free set.
            foreach (List<Demand> group in groups.OrderByDescending(Importance).ThenBy(g => g[0].Slot.Group, StringComparer.Ordinal))
            {
                double top = Forbidden;
                List<Tuple<Demand, Resource>> topPlacement = null;
                HashSet<string> anchors = anchorsOf[group];
                foreach (List<Resource> set in sets)
                {
                    if (anchors.Count > 0 && !anchors.Contains(setKey(set[0])))
                        continue;
                    List<Tuple<Demand, Resource>> placement;
                    double value = EvaluateGroup(group, set, occupancy, strongOnly, out placement, anchors.Count > 0 || group.Count == 1 ? 1 : 2);
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
            return members.Sum(d => Weight(d) * OverlapCount(d.Planes, d.Planes));
        }

        // Members take their best free matching control of the set, best first; members of other aircraft may share one.
        // Every member left out costs, so a complete free hat (even on a modifier layer) beats a partly used one.
        private static double EvaluateGroup(List<Demand> members, List<Resource> set, Occupancy occupancy, bool strongOnly, out List<Tuple<Demand, Resource>> placement, int minimum = 2)
        {
            placement = new List<Tuple<Demand, Resource>>();
            List<Tuple<Demand, Resource, double>> options = new List<Tuple<Demand, Resource, double>>();
            foreach (Demand member in members)
            {
                foreach (Resource resource in set)
                {
                    if ((PrefIndex(member.Slot, resource.Control) >= 0 || IsStyle(member, resource)) && (!strongOnly || IsStrong(member, resource)) &&
                        occupancy.Free(resource, member.Planes, member.Exclusive))
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
            return placement.Count >= minimum ? total + 40.0 * placement.Count - 150.0 * (members.Count - placement.Count) : Forbidden;
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
                    if (value <= Forbidden || !occupancy.Free(resources[r], demands[d].Planes, demands[d].Exclusive))
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
            if (!occupancy.Free(firstResource, first.Planes, first.Exclusive) || !occupancy.Free(secondResource, second.Planes, second.Exclusive))
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
            // A phase layer only for functions of that combat phase; modifier layers only for games that handle them.
            if (resource.Phase.Length > 0 && (demand.Exclusive != resource.Phase || !demand.PhaseOk))
                return Forbidden;
            if (resource.Layer > 0 && resource.Phase.Length == 0 && !demand.ShiftOk)
                return Forbidden;
            SlotControl slot = demand.Slot;
            bool style = demand.StyleControl.Length > 0 && string.Equals(demand.StyleControl, resource.Control, StringComparison.Ordinal);
            // A directional set's member only on the matching direction of a hat or thumb stick.
            if (demand.DirRole.Length > 0 && !style && !SetupPhases.MatchesDirection(demand.DirRole, resource.Control))
                return Forbidden;
            int pref = PrefIndex(slot, resource.Control);
            string family = Family(resource.Control);
            bool grip = IsGrip(resource.Control);
            // Special devices: an MFD takes only MFD functions (in OSB order, by their position preferences); a printed
            // panel label takes only the functions it names; a cockpit replica's unlabelled controls only their own.
            bool exactPref = pref == 0 && slot.Prefs.Count > 0 && slot.Prefs[0].IndexOf('*') < 0;
            if (!style)
            {
                bool mfdFunction = SetupPhases.IsMfdFunction(slot.Prefs);
                if ((resource.MfdDevice || SetupPhases.IsMfdControl(resource.Control)) && !mfdFunction)
                    return Forbidden;
                // A cockpit label on a base that does not name the function: never for a function it contradicts or a
                // time-critical / weapon function; else only once the unlabelled inputs are used up (a strong penalty below).
                if (!grip && resource.Control.IndexOf(".base.", StringComparison.Ordinal) >= 0 && !mfdFunction && SetupPhases.IsCockpitLabel(resource.Label) && !exactPref && !LabelMatches(slot, resource) &&
                    (SetupPhases.LabelContradicts(slot.Labels, slot.Slot, resource.Label) || SetupPhases.IsCriticalFunction(slot.Slot, demand.Crit, string.Empty)))
                    return Forbidden;
                if (!grip && (family == "panel" || family == "mfd") && !mfdFunction)
                {
                    bool printed = SetupPhases.IsFunctionLabel(resource.Label);
                    if (printed && !exactPref && !LabelMatches(slot, resource))
                        return Forbidden;
                    if (printed && !exactPref && SetupPhases.DirectionClash(SetupPhases.FunctionDirection(slot.Slot, slot.PairRole), SetupPhases.LabelDirection(resource.Label)))
                        return Forbidden;
                    if (!printed && resource.Replica && !exactPref)
                        return Forbidden;
                }
            }
            if (!style && pref < 0)
            {
                if (SetupPhases.SlewTmsClash(slot.Slot, slot.Group, resource.Control))
                    return Forbidden;
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
                label = 100 + 60 * SetupPhases.LabelFit(slot.Labels, resource.Label);
            else if (resource.Label.Length > 0 && family == "panel")
                label = -60;
            else if (!grip && resource.Control.IndexOf(".base.", StringComparison.Ordinal) >= 0 && SetupPhases.IsCockpitLabel(resource.Label))
                label = -400;   // a cockpit label naming another system: only once the unlabelled inputs are used up

            double weight = Weight(demand);
            double total = fit * weight + exact + label + RoleLabel(slot, resource);
            if (style)
                total += 500 + (demand.StyleShifted == (resource.Layer > 0) ? 100 : 0);
            // Reach: grips for the time-critical functions, bases and panels for what can wait.
            if (grip)
                total += 100 * SetupPhases.GripBonus(demand.Crit);
            if (resource.Phase.Length > 0)
                total -= 10;
            else if (resource.Layer > 0)
                total -= ((15 + 10 * weight) * resource.Layer + (grip ? 0 : 20)) * SetupPhases.LayerPenaltyFactor(demand.Crit);
            if (resource.Type == "latched" && slot.PairGroup.Length == 0)
                total -= 35 * weight;
            if (grip && Weight(slot.Frequency) <= 1 && fit < 50)
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

        // Frequency x phase role x time criticality.
        private static double Weight(Demand demand)
        {
            return Weight(demand.Slot.Frequency) * demand.RoleFactor * SetupPhases.CritWeight(demand.Crit);
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

                // Cockpit replicas (printed labels) and MFDs take only their own functions.
                string kind = device.Model.Kind ?? string.Empty;
                bool mfd = kind == "mfd";
                bool replica = mfd || ((kind == "panel" || kind == "buttonbox") && SetupPhases.IsReplicaPanel(kind, resources.Where(r => r.Device == device && !r.IsAxis).Select(r => r.Label)));

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
                foreach (Resource resource in resources.Where(r => r.Device == device))
                {
                    resource.MfdDevice = mfd;
                    resource.Replica = replica;
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
                    Group = parts.Length > 7 ? parts[7].Trim() : string.Empty,
                    Phases = parts.Length > 8 ? SetupPhases.Parse(parts[8]) : new List<string>(),
                    Crit = parts.Length > 9 ? SetupPhases.NormalizeCrit(parts[9]) : string.Empty
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
        public string PhaseLayers = "auto";
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
        public string Where { get; set; }      // the device, input and control in plain words (the app shows this)
        public double Score { get; set; }
        public bool SameAsBefore { get; set; }
    }

    public sealed class EngineAutoAssignResult
    {
        public bool DryRun { get; set; }
        public int MaxModifiers { get; set; }
        public int KeptExisting { get; set; }
        public int StyleKept { get; set; }
        // Functions placed like the user's most used aircraft of their class (usage ranking), and those aircraft.
        public int UsageStyled { get; set; }
        public List<string> UsageReferences { get; set; }
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
            UsageReferences = new List<string>();
            Assignments = new List<EngineAutoAssignment>();
            Unassigned = new List<string>();
            ModifiersUsed = new List<string>();
            Explanations = new List<string>();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }
}
