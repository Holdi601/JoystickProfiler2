#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.Setup
{
    // The Setup wizard's planner (docs/setup-wizard.md#how-the-plan-is-made). Deterministic, no AI:
    // 1. Relations: one per function of the knowledge map for the chosen games' aircraft. Realism below 50 % groups by
    //    function (one relation for all aircraft, same button everywhere); from 50 % on, aircraft whose real HOTAS has the
    //    function elsewhere get their own relation. Relations are also split where the aircraft classes use different
    //    devices (helicopters on the collective, spaceships on the left stick). Commands the profile already has stay in
    //    their relation; its binds stay (unless the user asks for a new layout).
    // 2. Importance: category (flight > weapons/countermeasures/sensors > systems > views/comms) x frequency x the
    //    priority of its aircraft.
    // 3. Score of a function on an input = importance x (comfort + realism x 2 x real-position match). Comfort is the
    //    knowledge map's position preference, labels, reach (grip / base / panel) and the modifier layer.
    // 4. Constraints: input type, the devices the hands reach for the aircraft class (one device per hand), dedicated
    //    controls, an input serves several relations only without aircraft in common, the modifier budget.
    // 5. Greedy start (kept binds, the user's own controls, on/off pairs on toggles, hats as groups, then single
    //    functions by importance), then local search (move, swap, displace) with a fixed seed.
    public static class SetupPlanner
    {
        internal const double Forbidden = -1e9;
        private const double Eps = 1e-6;

        private static readonly Regex Dedicated = new Regex(@"^stick\.trigger\.|^stick\.weapon_release$|\.pinky\.|^throttle\.detent\.|^throttle\.cage$", RegexOptions.Compiled);
        private static readonly Regex Named = new Regex(@"\.(hat\.(tms|dms|cms|trim|mic)|coolie|china|boat|speedbrake|dogfight)\.", RegexOptions.Compiled);
        private static readonly Regex DedicatedAxis = new Regex(@"^(stick\.axis\.(pitch|roll|twist|brake_lever|ministick2?\.[xy])|pedals\.axis\.|throttle\.axis\.(main|left|right|ministick2?\.[xy])|collective\.axis\.main|yoke\.axis\.(pitch|roll))", RegexOptions.Compiled);
        private static readonly Regex NumberedThrottleButton = new Regex(@"^(throttle|collective)\.button\.\d+$", RegexOptions.Compiled);
        private static readonly HashSet<string> GroupRoles = new HashSet<string>(StringComparer.Ordinal) { "up", "down", "left", "right", "press", "fwd", "aft", "center", "x", "y", "cw", "ccw" };

        // ---- internal model ----

        internal sealed class Demand
        {
            public int Index;
            public string Name = string.Empty;
            public SetupFunction Function;
            public string FunctionId = string.Empty;
            public string Side = "any";
            public bool IsAxis;
            public List<SetupNode> Nodes = new List<SetupNode>();
            public List<string> Aircraft = new List<string>();
            public ulong[] Bits;
            public HashSet<string> Classes = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> Reachable = new HashSet<string>(StringComparer.Ordinal);
            public string Category = SetupImportance.Systems;
            public string Frequency = "occasional";
            public double Importance;
            public double WeightMax;
            public bool Existing;
            public bool Kept;
            public string KeptKey = string.Empty;
            public SetupExistingRelation Source;
            public string StyleDevice = string.Empty;
            public string StyleInput = string.Empty;
            public bool StyleShifted;
            public string HandKey = string.Empty;
            public string RealKey = string.Empty;
            public bool SpaceLeftStick;
            // Aircraft with the same real position, with their summed weight: (positions or empty, side, weight).
            public List<RealGroup> Real = new List<RealGroup>();
            public double RealWeight;
            public bool Fixed;           // kept, the user's own control, group or pair member: the local search leaves it
            public string Reason = string.Empty;
            public List<string> Phases = new List<string>();   // flight phases (SetupPhases)
            public string Crit = string.Empty;                 // time criticality
            public string Exclusive = string.Empty;            // a2a / a2g: only used in that combat phase (phase layers)
            public double RoleFactor = 1;                      // how much its phases matter for its aircraft's roles
            public bool ShiftOk = true;                        // every game of it handles modifier binds
            public bool PhaseOk = true;                        // every game of it handles held (latched) modifiers
            public List<SetupRule> Rules = new List<SetupRule>();   // the user's preference rules that concern it
            public string DirKey = string.Empty;               // a directional set (tms, dms, cms, trim ...): its key
            public string DirRole = string.Empty;              // and its direction (up, down, left, right, press)
        }

        internal sealed class RealGroup
        {
            public List<string> Controls;   // cockpit database positions, null = unknown (fallback on the side)
            public string Side = string.Empty;
            public double Weight;
        }

        internal sealed class Slot
        {
            public int Index;
            public SetupInput Input;
            public SetupDevice Device;
            public string Role = string.Empty;
            public int Layer;
            public SetupPlanModifier Modifier;
            public string Key = string.Empty;
            public string Control = string.Empty;
            public string RuleControl = string.Empty;    // the control the rules see (left stick as the throttle side)
            public List<string> Names = new List<string>();  // control and its aliases for the position preferences
            public string Family = string.Empty;
            public bool Grip;
            public string Phase = string.Empty;          // a position of the mode switch (a2a, a2g): a phase layer
            public string BaseKey = string.Empty;        // device|input: the normal and the phase layers of one input
            public bool Replica;                         // a cockpit-replica panel: only the functions its labels name
            public bool MfdDevice;                       // an MFD: only MFD functions
        }

        internal sealed class Run
        {
            public SetupRequest Request;
            public SetupOptions Options;
            public double Realism;
            public List<Demand> Demands = new List<Demand>();
            public List<Slot> Slots = new List<Slot>();
            public List<SetupPlanModifier> Modifiers = new List<SetupPlanModifier>();
            public Dictionary<Demand, Slot> Placed = new Dictionary<Demand, Slot>();
            public List<Demand>[] Occupants;
            public Dictionary<string, List<Demand>> KeptAt = new Dictionary<string, List<Demand>>(StringComparer.OrdinalIgnoreCase);
            public double[][] Scores;
            public Dictionary<string, bool> PatternCache = new Dictionary<string, bool>(StringComparer.Ordinal);
            public Dictionary<string, int> AircraftIndex;
            public int Words;
            public Dictionary<string, List<Slot>> Siblings = new Dictionary<string, List<Slot>>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Directional = new HashSet<string>(StringComparer.Ordinal);
            public Dictionary<string, System.Text.RegularExpressions.Regex> RuleRegex = new Dictionary<string, System.Text.RegularExpressions.Regex>(StringComparer.Ordinal);
        }

        // ---- entry point ----

        public static SetupPlan Plan(SetupRequest request)
        {
            SetupOptions options = request.Options ?? new SetupOptions();
            request.Cockpits = request.Cockpits ?? new CockpitPositionTable();
            SetupRoles.Propose(request.Devices);
            int preferred = Math.Max(0, options.Modifiers);
            int forcedLimit = Math.Max(preferred, Math.Max(0, options.ForcedModifierLimit));
            int start = options.PreferFewer ? 0 : preferred;

            Run best = null;
            int bestMissing = int.MaxValue;
            List<string> notes = new List<string>();
            int fewNeed = 0, fewHave = 0;
            string fewAircraft = string.Empty;
            for (int layers = start; layers <= forcedLimit; layers++)
            {
                Run run = Execute(request, options, layers, 0, null);
                int missing = EssentialMissing(run);
                if (best == null || missing < bestMissing)
                {
                    best = run;
                    bestMissing = missing;
                }
                if (missing == 0)
                    break;
                if (layers >= preferred)
                {
                    // More modifiers than the user asked for only when the devices have very few inputs for the most
                    // important functions of the top aircraft.
                    if (!FewInputs(run, out fewNeed, out fewHave, out fewAircraft))
                        break;
                    if (layers + 1 > forcedLimit)
                        break;
                }
            }

            // Phase layers (a latched mode switch: up = one combat phase, down = the other) when the grips are too few
            // for the functions of one phase, or when the user asks for them.
            string phaseMode = NormalizePhaseMode(options.PhaseLayers);
            string phaseNote = string.Empty;
            if (phaseMode != "off")
            {
                ModeSwitch modeSwitch = FindModeSwitch(request);
                int need = PhaseNeed(best);
                if (modeSwitch == null)
                {
                    if (phaseMode == "on")
                        phaseNote = "Phase layers: no latched mode switch on your devices (a 2- or 3-position toggle switch on a base or collective).";
                }
                else if (phaseMode == "on" || need > 0)
                {
                    // The mode switch's layers come on top of the shift buttons (no hand holds them); at most two.
                    int phases = Math.Min(modeSwitch.Positions.Count, 2);
                    int shifts = best.Modifiers.Count(m => !m.Switch);
                    List<string> order = PhaseOrder(best, phases);
                    Run phased = Execute(request, options, shifts, phases, new PhasePlan { Switch = modeSwitch, Phases = order });
                    if (phaseMode == "on" || Better(phased, best))
                        best = phased;
                    else
                        phaseNote = "Phase layers: not needed, the functions fit without the mode switch.";
                }
            }

            SetupPlan plan = Build(best, request, options);
            plan.PhaseLayers = phaseMode;
            if (phaseNote.Length > 0)
                plan.Notes.Add(phaseNote);
            int used = plan.Modifiers.Count(m => !m.Switch);
            if (used > preferred)
            {
                foreach (SetupPlanModifier modifier in plan.Modifiers.Where(m => !m.Switch).Skip(preferred))
                    modifier.Forced = true;
                plan.Notes.Insert(0, "Only " + fewHave.ToString(CultureInfo.InvariantCulture) + " free buttons on the sticks for the " + fewNeed.ToString(CultureInfo.InvariantCulture) +
                                     " most important functions" + (fewAircraft.Length > 0 ? " of " + fewAircraft : string.Empty) + ": JoyPro added " + (used - preferred).ToString(CultureInfo.InvariantCulture) +
                                     " modifier(s) although you asked for " + (preferred == 0 ? "none" : "at most " + preferred.ToString(CultureInfo.InvariantCulture)) + ".");
            }
            else if (preferred > 0 && used < preferred)
            {
                plan.Notes.Insert(0, used == 0 ? "No modifier needed: every function fits without one." : "Only " + used.ToString(CultureInfo.InvariantCulture) + " of the " + preferred.ToString(CultureInfo.InvariantCulture) + " modifiers you allowed were needed.");
            }
            plan.Notes.AddRange(notes);
            return plan;
        }

        internal static string NormalizePhaseMode(string mode)
        {
            switch ((mode ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "on":
                case "true":
                case "yes":
                    return "on";
                case "off":
                case "false":
                case "no":
                    return "off";
                default:
                    return "auto";
            }
        }

        // ---- phase layers ----

        internal sealed class ModeSwitch
        {
            public string Device = string.Empty;
            public string Key = string.Empty;
            public List<SetupInput> Positions = new List<SetupInput>();   // up first, then down
            public List<SetupInput> All = new List<SetupInput>();         // every position (centre too): none is bound
        }

        internal sealed class PhasePlan
        {
            public ModeSwitch Switch;
            public List<string> Phases = new List<string>();   // phase per position (up, down)
        }

        // The best latched mode switch of the hands' devices (or a panel's unlabelled toggle): a switch labelled MODE
        // first, then base switches with two positions, the throttle before the main stick.
        internal static ModeSwitch FindModeSwitch(SetupRequest request)
        {
            Dictionary<string, SetupDevice> devices = request.Devices.Where(d => SetupRoles.Binds(d.Role) && d.Role != SetupRoles.Pedals).ToDictionary(d => d.Device, StringComparer.Ordinal);
            Dictionary<string, ModeSwitch> found = new Dictionary<string, ModeSwitch>(StringComparer.Ordinal);
            Dictionary<string, int> rankOf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (SetupInput input in request.Inputs.Where(i => !i.IsAxis && devices.ContainsKey(i.Device)).OrderBy(i => i.Device, StringComparer.Ordinal).ThenBy(i => i.Control, StringComparer.Ordinal))
            {
                string key, position;
                int rank;
                if (!SetupPhases.ModeSwitchPosition(input.Control, input.Type, input.Label, out key, out position, out rank))
                    continue;
                string id = input.Device + "|" + key;
                ModeSwitch entry;
                if (!found.TryGetValue(id, out entry))
                {
                    found[id] = entry = new ModeSwitch { Device = input.Device, Key = key };
                    SetupDevice device = devices[input.Device];
                    rankOf[id] = rank + (device.Role == SetupRoles.Throttle || device.Role == SetupRoles.Collective ? 0 : device.Role == SetupRoles.Panel ? 2 : 1);
                }
                if (entry.Positions.Count >= 2)
                    continue;
                if (position == "up")
                    entry.Positions.Insert(0, input);
                else
                    entry.Positions.Add(input);
            }
            foreach (ModeSwitch entry in found.Values)
            {
                foreach (SetupInput input in request.Inputs.Where(i => i.Device == entry.Device && !i.IsAxis && i.Control.StartsWith(entry.Key + ".", StringComparison.Ordinal)))
                    entry.All.Add(input);
            }
            return found.Where(f => f.Value.Positions.Count > 0)
                .OrderBy(f => rankOf[f.Key]).ThenByDescending(f => f.Value.Positions.Count).ThenBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => f.Value).FirstOrDefault();
        }

        // Functions of one combat phase only (exclusive a2a / a2g) that did not get a grip input on the normal layer
        // (not placed or behind a shift button): what a mode switch would help.
        internal static int PhaseNeed(Run run)
        {
            int need = 0;
            foreach (Demand demand in run.Demands.Where(d => !d.Kept && !d.IsAxis && d.Exclusive.Length > 0 && d.PhaseOk && IsTop(run, d) && SetupImportance.FrequencyRank(d.Frequency) >= 2))
            {
                Slot slot;
                if (!run.Placed.TryGetValue(demand, out slot))
                {
                    if (CouldUseGrip(run, demand))
                        need++;
                }
                else if (slot.Layer > 0 && slot.Phase.Length == 0)
                {
                    need++;
                }
            }
            return need;
        }

        // With one position the layer goes to the phase whose functions matter less (the other stays always at hand);
        // with two positions up = air to air, down = air to ground.
        private static List<string> PhaseOrder(Run run, int positions)
        {
            if (positions >= 2)
                return new List<string> { SetupPhases.A2A, SetupPhases.A2G };
            double a2a = run.Demands.Where(d => d.Exclusive == SetupPhases.A2A).Sum(d => d.Importance);
            double a2g = run.Demands.Where(d => d.Exclusive == SetupPhases.A2G).Sum(d => d.Importance);
            return new List<string> { a2a < a2g ? SetupPhases.A2A : SetupPhases.A2G };
        }

        // A phased run is better with fewer essential functions missing, or as many and fewer combat functions left
        // without a grip input, or a clearly better total score.
        private static bool Better(Run phased, Run plain)
        {
            int a = EssentialMissing(phased), b = EssentialMissing(plain);
            if (a != b)
                return a < b;
            int pa = PhaseNeed(phased), pb = PhaseNeed(plain);
            if (pa != pb)
                return pa < pb;
            return Total(phased) > Total(plain) * 1.02;
        }

        private static double Total(Run run)
        {
            return run.Placed.Sum(p => S(run, p.Key, p.Value));
        }

        // Essential functions of the top aircraft (tier 0, or all without a priority list) that are not placed.
        private static int EssentialMissing(Run run)
        {
            return run.Demands.Count(d => !d.Kept && !run.Placed.ContainsKey(d) && SetupImportance.Essential(d.Category, d.Frequency) && IsTop(run, d) && !d.IsAxis && CouldUseGrip(run, d));
        }

        private static bool IsTop(Run run, Demand demand)
        {
            if (run.Options.Priorities == null || run.Options.Priorities.Count == 0)
                return true;
            return demand.WeightMax >= 0.999;
        }

        private static bool CouldUseGrip(Run run, Demand demand)
        {
            return run.Slots.Any(s => s.Layer == 0 && s.Grip && !s.Input.IsAxis && demand.Reachable.Contains(s.Device.Device));
        }

        // Very few inputs: some top aircraft has more essential grip functions than free grip buttons.
        private static bool FewInputs(Run run, out int need, out int have, out string aircraft)
        {
            need = 0;
            have = 0;
            aircraft = string.Empty;
            bool few = false;
            foreach (IGrouping<string, Demand> byClass in run.Demands.Where(d => !d.Kept && !d.IsAxis && SetupImportance.Essential(d.Category, d.Frequency) && IsTop(run, d))
                         .GroupBy(d => d.Classes.OrderBy(c => c, StringComparer.Ordinal).FirstOrDefault() ?? SetupClasses.Fixed))
            {
                // per aircraft of the class: its essential functions against the grip buttons its hands reach
                Dictionary<string, int> perAircraft = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (Demand demand in byClass)
                {
                    foreach (string plane in demand.Aircraft)
                        perAircraft[plane] = (perAircraft.ContainsKey(plane) ? perAircraft[plane] : 0) + 1;
                }
                if (perAircraft.Count == 0)
                    continue;
                KeyValuePair<string, int> busiest = perAircraft.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).First();
                HashSet<string> reach = byClass.First().Reachable;
                int buttons = run.Slots.Count(s => s.Layer == 0 && s.Grip && !s.Input.IsAxis && reach.Contains(s.Device.Device) && !Dedicated.IsMatch(s.RuleControl));
                if (busiest.Value > buttons && busiest.Value - buttons > need - have)
                {
                    few = true;
                    need = busiest.Value;
                    have = buttons;
                    aircraft = busiest.Key;
                }
            }
            return few;
        }

        // ---- one planning run with a given number of modifier layers ----

        private static Run Execute(SetupRequest request, SetupOptions options, int layers, int phaseLayers, PhasePlan phasePlan)
        {
            Run run = new Run { Request = request, Options = options, Realism = Math.Max(0, Math.Min(1, options.Realism)) };
            BuildDemands(run);
            BuildSlots(run, layers, phaseLayers > 0 ? phasePlan : null);
            run.Occupants = new List<Demand>[run.Slots.Count];
            for (int i = 0; i < run.Slots.Count; i++)
                run.Occupants[i] = new List<Demand>();
            run.Scores = new double[run.Demands.Count][];

            // Kept binds occupy their inputs first.
            foreach (Demand demand in run.Demands.Where(d => d.Kept))
            {
                List<Demand> list;
                if (!run.KeptAt.TryGetValue(demand.KeptKey, out list))
                    run.KeptAt[demand.KeptKey] = list = new List<Demand>();
                list.Add(demand);
            }

            List<Demand> open = run.Demands.Where(d => !d.Kept).ToList();
            AssignStyle(run, open);
            // Round 1 places only strong matches (a panel label naming the function, its exact position, the real
            // aircraft's position, the left stick's translation axes), so a labelled switch goes to its own function
            // before a more important pair or group takes it; round 2 the rest.
            // Realism from 50 % on: the real aircraft's positions (cockpit database) go first of all.
            foreach (int strength in run.Realism >= 0.5 ? new[] { 2, 1, 0 } : new[] { 1, 0 })
            {
                AssignPairs(run, open, strength);
                AssignGroups(run, open, strength);
                AssignSingles(run, open, strength);
            }
            LocalSearch(run, open);

            // Modifier layers nobody uses are not created.
            HashSet<SetupPlanModifier> usedModifiers = new HashSet<SetupPlanModifier>(run.Placed.Values.Where(s => s.Modifier != null).Select(s => s.Modifier));
            run.Modifiers = run.Modifiers.Where(usedModifiers.Contains).ToList();
            return run;
        }

        // ---- demands ----

        private static void BuildDemands(Run run)
        {
            SetupRequest request = run.Request;
            SetupOptions options = run.Options;
            HashSet<string> games = new HashSet<string>(options.Games ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            // Aircraft index over the scope and the profile's relations (an input is shared only without aircraft in common).
            Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Action<string> addKey = key =>
            {
                if (!string.IsNullOrEmpty(key) && !index.ContainsKey(key))
                    index[key] = index.Count;
            };
            foreach (SetupCommand command in request.Commands.Where(c => games.Contains(c.Game)).OrderBy(c => c.Aircraft, StringComparer.OrdinalIgnoreCase))
                addKey(command.Aircraft);
            foreach (SetupExistingRelation relation in request.Existing)
            {
                foreach (SetupNode node in relation.Nodes)
                    addKey(NodeKey(node));
            }
            run.AircraftIndex = index;
            run.Words = Math.Max(1, (index.Count + 63) / 64);
            run.Directional = SetupPhases.DirectionalGroups(request.Functions.Values.Select(f => f.Group));

            Dictionary<string, int> tierOf;
            Dictionary<string, double> weights = SetupImportance.Weights(options.Priorities, request.Aircraft.Values, out tierOf);
            Func<string, double> weightOf = key =>
            {
                double w;
                return weights.TryGetValue(key ?? string.Empty, out w) ? w : (options.Priorities == null || options.Priorities.Count == 0 ? 1.0 : 0.05);
            };

            // Commands the profile has: they stay in their relation.
            Dictionary<string, SetupExistingRelation> owner = new Dictionary<string, SetupExistingRelation>(StringComparer.OrdinalIgnoreCase);
            foreach (SetupExistingRelation relation in request.Existing)
            {
                foreach (SetupNode node in relation.Nodes)
                {
                    string key = CommandKey(node.Game, node.Action, NodeKey(node));
                    if (!owner.ContainsKey(key))
                        owner[key] = relation;
                }
            }

            List<Demand> demands = new List<Demand>();
            Dictionary<string, SetupExistingRelation> existingByName = request.Existing.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> scopeAircraft = new HashSet<string>(request.Commands.Where(c => games.Contains(c.Game)).Select(c => c.Aircraft), StringComparer.OrdinalIgnoreCase);

            // 1. The profile's relations.
            foreach (SetupExistingRelation relation in request.Existing.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                bool bound = relation.Device.Length > 0 && relation.Input.Length > 0;
                if (bound && !relation.OnSetup)
                    continue;   // on the keyboard, vJoy or a device that is not part of this setup: stays as it is
                bool inScope = relation.Nodes.Any(node => scopeAircraft.Contains(NodeKey(node)) || IsLayerOfScope(request, node, games) ||
                                                          (options.ExistingOnly && games.Contains(string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game)));
                if (!bound && !inScope)
                    continue;
                Demand demand = new Demand
                {
                    Name = relation.Name,
                    IsAxis = relation.IsAxis,
                    Existing = true,
                    Source = relation,
                    FunctionId = relation.Function ?? string.Empty
                };
                demand.Aircraft = relation.Nodes.Select(NodeKey).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                bool replace = options.ReplaceExisting && (options.ReplaceOnly == null || options.ReplaceOnly.Contains(relation.Name));
                if (bound && (!replace || !inScope))
                {
                    demand.Kept = true;
                    demand.KeptKey = relation.Device + "|" + relation.Input + "|" + relation.Modifier;
                }
                else if (bound)
                {
                    demand.StyleDevice = relation.Device;
                    demand.StyleInput = relation.Input;
                    demand.StyleShifted = relation.Modifier.Length > 0;
                }
                demands.Add(demand);
            }

            // 2. New relations from the knowledge map.
            Dictionary<string, List<SetupCommand>> clusters = new Dictionary<string, List<SetupCommand>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string[]> clusterInfo = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            int skippedDisplays = 0;
            HashSet<string> displayChecked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> displayUsable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // (Lay out with AI without "add missing": only the profile's own relations.)
            foreach (SetupCommand command in request.Commands.Where(c => games.Contains(c.Game) && !options.ExistingOnly).OrderBy(c => c.Function, StringComparer.Ordinal).ThenBy(c => c.Aircraft, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Action, StringComparer.Ordinal))
            {
                SetupFunction function;
                SetupAircraft plane;
                if (string.Equals(command.Status, "substitute", StringComparison.OrdinalIgnoreCase) || command.Function == "dummyunbindrelation" ||
                    !request.Functions.TryGetValue(command.Function, out function) || !request.Aircraft.TryGetValue(command.Aircraft, out plane))
                    continue;
                string key = CommandKey(command.Game, command.Action, command.Aircraft);
                if (owner.ContainsKey(key) || request.Disabled.Contains(key))
                    continue;
                // Display keys and keypads only where the setup has such a device.
                if (function.Kind == "mfd-osb" || function.Kind == "ufc-key")
                {
                    // its own position (an MFD key, a keypad key) must exist; a plain panel button does not make a keypad
                    if (displayChecked.Add(function.Id) && function.Prefs.Count > 0 &&
                        request.Inputs.Any(i => PatternMatch(run, function.Prefs[0], i.Control) || (!IsGrip(i.Control) && SetupPhases.LabelNames(function, i.Label))))
                        displayUsable.Add(function.Id);
                    if (!displayUsable.Contains(function.Id))
                    {
                        skippedDisplays++;
                        continue;
                    }
                }
                string cls = ClassOf(request, plane);
                string side = run.Realism >= 0.5 && command.Hotas.Length > 0 ? SideOfHotas(command.Hotas) : NormalizeSide(function.Side);
                SetupDevice hand = side == "stick" ? SetupRoles.RightHand(request.Devices, cls) : side == "throttle" ? SetupRoles.LeftHand(request.Devices, cls) : null;
                string handKey = hand != null ? hand.Role : side;
                string realKey = string.Empty;
                List<string> real;
                if (run.Realism >= 0.5 && TryReal(request, plane, function.Id, out real))
                    realKey = string.Join("+", real.OrderBy(r => r, StringComparer.OrdinalIgnoreCase));
                string cluster = function.Id + "|" + handKey + "|" + realKey;
                List<SetupCommand> list;
                if (!clusters.TryGetValue(cluster, out list))
                {
                    clusters[cluster] = list = new List<SetupCommand>();
                    clusterInfo[cluster] = new[] { function.Id, handKey, realKey, side };
                }
                list.Add(command);
            }
            if (skippedDisplays > 0)
                request.Warnings.Add(skippedDisplays.ToString(CultureInfo.InvariantCulture) + " display / keypad commands left out: no MFD or keypad device in this setup.");

            foreach (IGrouping<string, KeyValuePair<string, List<SetupCommand>>> byFunction in clusters.GroupBy(c => clusterInfo[c.Key][0], StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                SetupFunction function = request.Functions[byFunction.Key];
                List<KeyValuePair<string, List<SetupCommand>>> parts = byFunction.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
                foreach (KeyValuePair<string, List<SetupCommand>> part in parts)
                {
                    string[] info = clusterInfo[part.Key];
                    Demand demand = new Demand
                    {
                        Function = function,
                        FunctionId = function.Id,
                        IsAxis = function.IsAxis,
                        HandKey = info[1],
                        RealKey = info[2],
                        Side = run.Realism >= 0.5 && info[3] != "panel" ? info[3] : function.Side
                    };
                    foreach (SetupCommand command in part.Value)
                        demand.Nodes.Add(new SetupNode { Game = command.Game, Action = command.Action, Aircraft = command.Aircraft });
                    demand.Aircraft = part.Value.Select(c => c.Aircraft).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
                    string freq = part.Value.Select(c => c.Frequency).Where(f => f.Length > 0).OrderByDescending(SetupImportance.FrequencyRank).FirstOrDefault();
                    demand.Frequency = freq ?? function.Frequency;
                    demand.Name = function.Name + (parts.Count > 1 ? " [" + PartLabel(request, part.Value, parts.Select(p => p.Value).ToList()) + "]" : string.Empty);
                    SetupExistingRelation same;
                    if (existingByName.TryGetValue(demand.Name, out same))
                    {
                        if (same.IsAxis != demand.IsAxis)
                        {
                            demand.Name += demand.IsAxis ? " (axis)" : " (button)";
                        }
                        else
                        {
                            // The profile has a relation of this name: the new commands join it and its bind.
                            demand.Existing = true;
                            demand.Source = same;
                            Demand old = demands.FirstOrDefault(d => d.Source == same);
                            if (old != null)
                            {
                                old.Nodes.AddRange(demand.Nodes);
                                old.Aircraft = old.Aircraft.Concat(demand.Aircraft).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
                                old.Function = old.Function ?? function;
                                old.FunctionId = old.FunctionId.Length > 0 ? old.FunctionId : function.Id;
                                continue;
                            }
                        }
                    }
                    demands.Add(demand);
                }
            }

            // Finish: function, side, aircraft bits, classes, reach, real positions, importance.
            Dictionary<string, string> sideOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (SetupCommand command in request.Commands)
            {
                string key = command.Function + "|" + command.Aircraft;
                if (!sideOf.ContainsKey(key) && command.Hotas.Length > 0)
                    sideOf[key] = SideOfHotas(command.Hotas);
            }
            int n = 0;
            foreach (Demand demand in demands)
            {
                demand.Index = n++;
                if (demand.Function == null)
                {
                    SetupFunction known;
                    demand.Function = demand.FunctionId.Length > 0 && request.Functions.TryGetValue(demand.FunctionId, out known)
                        ? known
                        : new SetupFunction
                        {
                            Id = string.Empty,
                            Name = demand.Name,
                            IsAxis = demand.IsAxis,
                            Side = "panel",
                            Prefs = demand.IsAxis ? new List<string> { "*.axis.*" } : new List<string> { "panel.button.*", "*.base.button.*" }
                        };
                    if (demand.Function.Id.Length > 0)
                    {
                        demand.Side = demand.Function.Side;
                        if (demand.Frequency == "occasional" || demand.Frequency.Length == 0)
                            demand.Frequency = demand.Function.Frequency;
                    }
                    else
                    {
                        demand.Side = "panel";
                        demand.Frequency = "occasional";
                    }
                }
                demand.Bits = Bits(run, demand.Aircraft);
                foreach (string plane in demand.Aircraft)
                {
                    SetupAircraft known;
                    if (request.Aircraft.TryGetValue(plane, out known))
                        demand.Classes.Add(ClassOf(request, known));
                }
                if (demand.Classes.Count == 0)
                    demand.Classes.Add(SetupClasses.Fixed);
                HashSet<string> reach = null;
                foreach (string cls in demand.Classes.OrderBy(c => c, StringComparer.Ordinal))
                {
                    HashSet<string> set = SetupRoles.Reachable(request.Devices, cls);
                    if (reach == null)
                        reach = set;
                    else
                        reach.IntersectWith(set);
                }
                demand.Reachable = reach ?? new HashSet<string>(StringComparer.Ordinal);
                demand.SpaceLeftStick = demand.Classes.Count == 1 && demand.Classes.Contains(SetupClasses.Space) && SetupRoles.RoleDevice(request.Devices, SetupRoles.LeftStick) != null;

                demand.Category = SetupImportance.Category(demand.Function);
                double maxWeight = 0, sum = 0;
                Dictionary<string, RealGroup> groups = new Dictionary<string, RealGroup>(StringComparer.Ordinal);
                foreach (string plane in demand.Aircraft)
                {
                    double w = weightOf(plane);
                    maxWeight = Math.Max(maxWeight, w);
                    sum += w;
                    SetupAircraft known;
                    List<string> real = null;
                    if (demand.FunctionId.Length > 0 && request.Aircraft.TryGetValue(plane, out known))
                        TryReal(request, known, demand.FunctionId, out real);
                    string side;
                    if (!sideOf.TryGetValue(demand.FunctionId + "|" + plane, out side))
                        side = NormalizeSide(demand.Function.Side);
                    string key = (real == null ? "?" : string.Join("+", real)) + "|" + side;
                    RealGroup group;
                    if (!groups.TryGetValue(key, out group))
                        groups[key] = group = new RealGroup { Controls = real, Side = side };
                    group.Weight += w;
                }
                demand.Real = groups.Values.ToList();
                demand.RealWeight = demand.Real.Sum(g => g.Weight);
                demand.WeightMax = maxWeight;
                double breadth = Math.Min(Math.Max(0, sum - maxWeight), 5);

                // How an experienced pilot ranks it: its flight phases for the roles of its aircraft (priority weighted)
                // and how fast it must be reached.
                demand.Phases = demand.Function.Phases ?? new List<string>();
                demand.Crit = demand.Function.Crit ?? string.Empty;
                demand.Exclusive = SetupPhases.ExclusivePhase(demand.Phases);
                double roleSum = 0, roleWeight = 0;
                foreach (string plane in demand.Aircraft)
                {
                    SetupAircraft known;
                    if (!request.Aircraft.TryGetValue(plane, out known) || known.SharedLayer)
                        continue;
                    double w = Math.Max(0.01, weightOf(plane));
                    roleSum += w * SetupPhases.RoleFactor(SetupPhases.RoleKey(known.Role, ClassOf(request, known) == SetupClasses.Space ? "spaceship" : known.Type), demand.Phases);
                    roleWeight += w;
                }
                demand.RoleFactor = roleWeight > 0 ? roleSum / roleWeight : SetupPhases.RoleFactor(string.Empty, demand.Phases);
                demand.Importance = SetupImportance.CategoryWeight(demand.Category) * SetupImportance.FrequencyWeight(demand.Frequency) * (maxWeight + 0.15 * breadth) *
                                    demand.RoleFactor * SetupPhases.CritWeight(demand.Crit);

                // Modifier layers only for games that handle modifier binds (a mode switch: held modifiers).
                foreach (string game in demand.Nodes.Concat(demand.Source != null ? demand.Source.Nodes : new List<SetupNode>()).Select(node => string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string support;
                    if (!request.ModifierSupport.TryGetValue(game, out support))
                        continue;
                    demand.ShiftOk &= SetupPhases.AllowsLayer(support, false);
                    demand.PhaseOk &= SetupPhases.AllowsLayer(support, true);
                }
                foreach (SetupRule rule in options.Rules ?? new List<SetupRule>())
                {
                    if (RuleConcerns(run, rule, demand))
                        demand.Rules.Add(rule);
                }
                string dirKey, dirRole;
                if (SetupPhases.DirectionalMember(demand.Function.Group, run.Directional, out dirKey, out dirRole))
                {
                    demand.DirKey = dirKey;
                    demand.DirRole = dirRole;
                }
            }
            run.Demands = demands;
        }

        private static bool IsLayerOfScope(SetupRequest request, SetupNode node, HashSet<string> games)
        {
            SetupAircraft plane;
            return games.Contains(node.Game) && request.Aircraft.TryGetValue(NodeKey(node), out plane) && plane.SharedLayer;
        }

        private static string PartLabel(SetupRequest request, List<SetupCommand> part, List<List<SetupCommand>> all)
        {
            Func<List<SetupCommand>, HashSet<string>> classes = commands => new HashSet<string>(commands.Select(c =>
            {
                SetupAircraft plane;
                return request.Aircraft.TryGetValue(c.Aircraft, out plane) ? ClassOf(request, plane) : SetupClasses.Fixed;
            }), StringComparer.Ordinal);
            List<HashSet<string>> sets = all.Select(classes).ToList();
            bool disjoint = true;
            for (int i = 0; i < sets.Count && disjoint; i++)
            {
                for (int j = i + 1; j < sets.Count && disjoint; j++)
                    disjoint = !sets[i].Overlaps(sets[j]);
            }
            HashSet<string> mine = classes(part);
            if (disjoint)
                return string.Join(", ", new[] { SetupClasses.Fixed, SetupClasses.Rotary, SetupClasses.Space }.Where(mine.Contains).Select(SetupClasses.Title));
            List<string> planes = part.Select(c =>
            {
                SetupAircraft plane;
                return request.Aircraft.TryGetValue(c.Aircraft, out plane) && plane.VariantOf.Length > 0 ? plane.VariantOf : c.Aircraft;
            }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
            return SetupText.Join(planes, 3);
        }

        internal static bool TryReal(SetupRequest request, SetupAircraft plane, string function, out List<string> controls)
        {
            controls = null;
            if (request.Cockpits == null || plane == null)
                return false;
            if (request.Cockpits.TryGet(plane.Key, function, out controls))
                return true;
            return plane.VariantOf.Length > 0 && request.Cockpits.TryGet(plane.VariantOf, function, out controls);
        }

        internal static string ClassOf(SetupRequest request, SetupAircraft plane)
        {
            SetupAircraft basePlane;
            if (plane.VariantOf.Length > 0 && request.Aircraft.TryGetValue(plane.VariantOf, out basePlane))
                return basePlane.Class;
            return plane.Class;
        }

        // DCS nodes name their aircraft; the other games have one configuration (the game id stands for it).
        internal static string NodeKey(SetupNode node)
        {
            if (node == null)
                return string.Empty;
            return string.IsNullOrWhiteSpace(node.Aircraft) ? node.Game ?? string.Empty : node.Aircraft;
        }

        internal static string CommandKey(string game, string action, string aircraft)
        {
            return (string.IsNullOrWhiteSpace(game) ? "DCS" : game) + "|" + action + "|" + aircraft;
        }

        internal static string SideOfHotas(string hotas)
        {
            string h = (hotas ?? string.Empty).Trim().ToLowerInvariant();
            if (h.StartsWith("stick", StringComparison.Ordinal) || h == "cyclic" || h == "yoke")
                return "stick";
            if (h.StartsWith("throttle", StringComparison.Ordinal) || h == "collective" || h == "quadrant")
                return "throttle";
            if (h.StartsWith("pedal", StringComparison.Ordinal) || h == "rudder")
                return "pedals";
            return "panel";
        }

        internal static string NormalizeSide(string side)
        {
            switch ((side ?? string.Empty).ToLowerInvariant())
            {
                case "stick":
                case "yoke":
                case "cyclic":
                    return "stick";
                case "throttle":
                case "collective":
                case "quadrant":
                    return "throttle";
                case "pedals":
                    return "pedals";
                default:
                    return "panel";
            }
        }

        private static ulong[] Bits(Run run, List<string> aircraft)
        {
            ulong[] bits = new ulong[run.Words];
            bool any = false;
            foreach (string key in aircraft)
            {
                int bit;
                if (!run.AircraftIndex.TryGetValue(key, out bit))
                    continue;
                any = true;
                bits[bit >> 6] |= 1UL << (bit & 63);
                SetupAircraft plane;
                if (run.Request.Aircraft.TryGetValue(key, out plane) && plane.SharedLayer)
                {
                    // A DCS input layer is active next to every aircraft of the game.
                    foreach (KeyValuePair<string, int> other in run.AircraftIndex)
                    {
                        SetupAircraft o;
                        if (run.Request.Aircraft.TryGetValue(other.Key, out o) && string.Equals(o.Game, plane.Game, StringComparison.OrdinalIgnoreCase))
                            bits[other.Value >> 6] |= 1UL << (other.Value & 63);
                    }
                }
            }
            if (!any)
            {
                for (int i = 0; i < bits.Length; i++)
                    bits[i] = ulong.MaxValue;
            }
            return bits;
        }

        internal static bool Overlaps(ulong[] a, ulong[] b)
        {
            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                if ((a[i] & b[i]) != 0)
                    return true;
            }
            return false;
        }

        // ---- inputs ----

        private static void BuildSlots(Run run, int layers, PhasePlan phasePlan)
        {
            SetupRequest request = run.Request;
            List<Slot> slots = new List<Slot>();
            Dictionary<string, SetupDevice> devices = request.Devices.Where(d => SetupRoles.Binds(d.Role)).ToDictionary(d => d.Device, StringComparer.Ordinal);
            foreach (SetupInput input in request.Inputs.Where(i => devices.ContainsKey(i.Device)).OrderBy(i => i.Device, StringComparer.Ordinal).ThenBy(i => InputOrder(i.Input)).ThenBy(i => i.Input, StringComparer.Ordinal))
            {
                if (slots.Any(s => s.Device.Device == input.Device && string.Equals(s.Input.Input, input.Input, StringComparison.OrdinalIgnoreCase)))
                    continue;
                SetupDevice device = devices[input.Device];
                slots.Add(MakeSlot(input, device, 0, null));
            }

            // Cockpit replicas (printed labels) and MFDs take only their own functions.
            foreach (IGrouping<string, Slot> device in slots.GroupBy(s => s.Device.Device))
            {
                string kind = (device.First().Device.Kind ?? string.Empty).ToLowerInvariant();
                bool mfd = kind == "mfd";
                bool replica = mfd || ((kind == "panel" || kind == "buttonbox") && SetupPhases.IsReplicaPanel(kind, device.Where(s => !s.Input.IsAxis).Select(s => s.Input.Label)));
                foreach (Slot slot in device)
                {
                    slot.MfdDevice = mfd;
                    slot.Replica = replica;
                }
            }

            // Phase layers first (the positions of the mode switch); the whole switch stays unbound (its centre is the
            // normal layer).
            List<SetupPlanModifier> modifiers = new List<SetupPlanModifier>();
            if (phasePlan != null && phasePlan.Switch != null)
            {
                for (int i = 0; i < phasePlan.Phases.Count && i < phasePlan.Switch.Positions.Count; i++)
                {
                    SetupInput position = phasePlan.Switch.Positions[i];
                    string phase = phasePlan.Phases[i];
                    modifiers.Add(new SetupPlanModifier
                    {
                        Name = "JoyPro Mode " + phase.ToUpperInvariant(),
                        Device = position.Device,
                        Input = position.Input,
                        Control = position.Control,
                        Switch = true,
                        Phase = phase,
                        Reason = "mode switch " + (i == 0 ? "up" : "down") + ": the " + SetupPhases.Title(phase).ToLowerInvariant() + " layer"
                    });
                }
                slots.RemoveAll(s => s.Device.Device == phasePlan.Switch.Device && phasePlan.Switch.All.Any(i => string.Equals(i.Input, s.Input.Input, StringComparison.OrdinalIgnoreCase)));
            }

            // Modifier buttons: the profile's own first, then the pinky (the classic shift), then other grip buttons;
            // on the main stick before the throttle (every aircraft class reaches the main stick).
            SetupDevice main = SetupRoles.RoleDevice(request.Devices, SetupRoles.MainStick);
            Func<Slot, bool> candidate = s => s.Layer == 0 && !s.Input.IsAxis && s.Input.Type != "latched" && !modifiers.Any(m => m.Device == s.Device.Device && m.Input == s.Input.Input);
            foreach (SetupModifierChoice existing in request.ExistingModifiers.OrderByDescending(m => m.Uses).ThenBy(m => m.Name, StringComparer.Ordinal))
            {
                if (modifiers.Count(m => !m.Switch) >= layers)
                    break;
                Slot position = slots.FirstOrDefault(s => candidate(s) && s.Device.Device == existing.Device && string.Equals(s.Input.Input, existing.Input, StringComparison.OrdinalIgnoreCase) && s.Grip);
                if (position != null)
                    modifiers.Add(new SetupPlanModifier { Name = existing.Name, Device = position.Device.Device, Input = position.Input.Input, Control = position.Control, Existing = true, Reason = "your own modifier" });
            }
            string[] order = { "stick.pinky.lever", "stick.pinky.button", "stick.button.index_lower", "throttle.pinky.lever", "throttle.pinky.button", "throttle.pinky.fwd", "stick.button.index.*", "stick.base.button.*", "throttle.button.*", "throttle.base.button.*" };
            foreach (string pattern in order)
            {
                foreach (Slot slot in slots.Where(s => candidate(s) && (s.Role == SetupRoles.MainStick || s.Role == SetupRoles.Throttle || s.Role == SetupRoles.HeliStick))
                             .OrderBy(s => main != null && s.Device.Device == main.Device ? 0 : 1).ThenByDescending(s => InputOrder(s.Input.Input)).ToList())
                {
                    if (modifiers.Count(m => !m.Switch) >= layers)
                        break;
                    if (!candidate(slot) || !PatternMatch(run, pattern, slot.RuleControl))
                        continue;
                    modifiers.Add(new SetupPlanModifier
                    {
                        Name = "JoyPro Shift " + (modifiers.Count(m => !m.Switch) + 1).ToString(CultureInfo.InvariantCulture),
                        Device = slot.Device.Device,
                        Input = slot.Input.Input,
                        Control = slot.Control,
                        Reason = slot.Control.IndexOf("pinky", StringComparison.Ordinal) >= 0 ? "the pinky lever / button, the classic shift" : "a grip button JoyPro reserved as shift"
                    });
                }
            }
            slots.RemoveAll(s => modifiers.Any(m => m.Device == s.Device.Device && string.Equals(m.Input, s.Input.Input, StringComparison.OrdinalIgnoreCase)));
            int layer = 0;
            int phaseLayer = 50;
            List<Slot> baseSlots = slots.ToList();
            foreach (SetupPlanModifier modifier in modifiers)
            {
                // Shift layers 1, 2 ...; phase layers 51, 52 (a mode switch position, no hand needed).
                int index = modifier.Switch ? ++phaseLayer : ++layer;
                foreach (Slot slot in baseSlots.Where(s => !s.Input.IsAxis && s.Input.Type != "latched" && s.Grip))
                {
                    Slot copy = MakeSlot(slot.Input, slot.Device, index, modifier);
                    copy.Phase = modifier.Switch ? modifier.Phase : string.Empty;
                    copy.Replica = slot.Replica;
                    copy.MfdDevice = slot.MfdDevice;
                    slots.Add(copy);
                }
            }
            for (int i = 0; i < slots.Count; i++)
            {
                slots[i].Index = i;
                // The normal layer and the phase layers of one input: a function on the normal layer would not work in
                // a switch position that has its own function there, so per aircraft an input is one or the other.
                if (slots[i].Layer == 0 || slots[i].Phase.Length > 0)
                {
                    slots[i].BaseKey = slots[i].Device.Device + "|" + slots[i].Input.Input.ToUpperInvariant();
                    List<Slot> siblings;
                    if (!run.Siblings.TryGetValue(slots[i].BaseKey, out siblings))
                        run.Siblings[slots[i].BaseKey] = siblings = new List<Slot>();
                    siblings.Add(slots[i]);
                }
            }
            run.Slots = slots;
            run.Modifiers = modifiers;
        }

        private static int InputOrder(string input)
        {
            Match m = Regex.Match(input ?? string.Empty, @"^JOY_BTN(\d+)$");
            if (m.Success)
                return 100 + int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if ((input ?? string.Empty).StartsWith("JOY_BTN_POV", StringComparison.Ordinal))
                return 1000;
            return 0;
        }

        private static Slot MakeSlot(SetupInput input, SetupDevice device, int layer, SetupPlanModifier modifier)
        {
            Slot slot = new Slot { Input = input, Device = device, Role = device.Role, Layer = layer, Modifier = modifier, Control = input.Control ?? string.Empty };
            slot.Key = device.Device + "|" + input.Input + "|" + (modifier == null ? string.Empty : modifier.Name);
            slot.Names.Add(slot.Control);
            slot.RuleControl = slot.Control;
            if (device.Role == SetupRoles.LeftStick && slot.Control.StartsWith("stick.", StringComparison.Ordinal))
            {
                // A left-hand stick works as the throttle side (dual-stick setups, like Auto Assign): its hats, buttons and
                // axes match the throttle's positions, not the main stick's (no pitch on the left stick). Its own axes
                // carry the spaceships' translation (SpaceRule).
                string alias = LeftStickAlias(slot.Control);
                slot.Names.Clear();
                slot.Names.Add(alias);
                slot.RuleControl = alias;
            }
            else if (device.Role == SetupRoles.Collective && slot.Control.StartsWith("collective.", StringComparison.Ordinal))
            {
                slot.Names.Add("throttle." + slot.Control.Substring("collective.".Length));
            }
            string family = Family(slot.RuleControl);
            slot.Family = family;
            slot.Grip = IsGrip(slot.RuleControl);
            return slot;
        }

        internal static string LeftStickAlias(string control)
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

        internal static string Family(string control)
        {
            int dot = (control ?? string.Empty).IndexOf('.');
            return dot > 0 ? control.Substring(0, dot) : control ?? string.Empty;
        }

        internal static bool IsGrip(string control)
        {
            string family = Family(control);
            return (family == "stick" || family == "throttle" || family == "collective" || family == "yoke") && control.IndexOf(".base.", StringComparison.Ordinal) < 0;
        }

        // Hand of a control family / function side: right (stick, yoke), left (throttle, collective, quadrant), feet, panel.
        internal static string Hand(string familyOrSide)
        {
            switch (familyOrSide)
            {
                case "stick":
                case "yoke":
                case "cyclic":
                    return "right";
                case "throttle":
                case "collective":
                case "quadrant":
                    return "left";
                case "pedals":
                    return "feet";
                default:
                    return "panel";
            }
        }

        // Prefs use '*' for "any number / any part" (stick.hat.*.up, panel.button.*, *.axis.*).
        internal static bool PatternMatch(Run run, string pattern, string control)
        {
            if (pattern.IndexOf('*') < 0)
                return string.Equals(pattern, control, StringComparison.OrdinalIgnoreCase);
            string key = pattern + "\u001F" + control;
            bool value;
            if (run != null && run.PatternCache.TryGetValue(key, out value))
                return value;
            string regex = "^" + Regex.Escape(pattern).Replace("\\*", "[^.]+(\\.[^.]+)*?") + "$";
            value = Regex.IsMatch(control ?? string.Empty, regex, RegexOptions.IgnoreCase);
            if (run != null)
                run.PatternCache[key] = value;
            return value;
        }

        private static int PrefIndex(Run run, SetupFunction function, Slot slot)
        {
            int best = -1;
            for (int i = 0; i < function.Prefs.Count && (best < 0 || i < best); i++)
            {
                foreach (string name in slot.Names)
                {
                    if (PatternMatch(run, function.Prefs[i], name))
                    {
                        best = i;
                        break;
                    }
                }
            }
            return best;
        }

        // ---- scoring ----

        private static double S(Run run, Demand demand, Slot slot)
        {
            double[] row = run.Scores[demand.Index];
            if (row == null)
            {
                row = new double[run.Slots.Count];
                for (int i = 0; i < row.Length; i++)
                    row[i] = double.NaN;
                run.Scores[demand.Index] = row;
            }
            double value = row[slot.Index];
            if (double.IsNaN(value))
            {
                value = Score(run, demand, slot);
                row[slot.Index] = value;
            }
            return value;
        }

        // Left-stick translation in space sims: X = strafe left/right, Y = forward/back, twist = up/down.
        private static double SpaceRule(Demand demand, Slot slot)
        {
            if (!demand.SpaceLeftStick || slot.Role != SetupRoles.LeftStick || !demand.IsAxis)
                return 0;
            switch (demand.FunctionId)
            {
                case "space_strafe_hor": return slot.Control == "stick.axis.roll" ? 1 : 0;
                case "space_strafe_fwdaft": return slot.Control == "stick.axis.pitch" ? 1 : 0;
                case "space_strafe_ver": return slot.Control == "stick.axis.twist" ? 1 : slot.Control == "stick.axis.ministick.y" || slot.Control == "stick.axis.slider" ? 0.7 : 0;
            }
            return 0;
        }

        // 0..1: how much this input is where the real aircraft have the function (weighted by aircraft priority).
        internal static double RealMatch(Run run, Demand demand, Slot slot)
        {
            if (demand.RealWeight <= 0 || demand.Function == null || demand.FunctionId.Length == 0)
                return 0;
            double total = 0;
            string slotHand = Hand(slot.Family);
            foreach (RealGroup group in demand.Real)
            {
                double m = 0;
                if (group.Controls != null)
                {
                    foreach (string control in group.Controls)
                    {
                        if (slot.Names.Any(n => string.Equals(n, control, StringComparison.OrdinalIgnoreCase)))
                            m = Math.Max(m, 1);
                        else if (slot.Names.Any(n => string.Equals(Prefix(n), Prefix(control), StringComparison.OrdinalIgnoreCase)))
                            m = Math.Max(m, 0.5);
                        else if (Hand(Family(control)) == slotHand)
                            m = Math.Max(m, 0.2);
                    }
                }
                else
                {
                    // No cockpit entry: the knowledge map's own position on the side this aircraft has the function.
                    bool sideMatch = Hand(group.Side) == slotHand;
                    int pref = PrefIndex(run, demand.Function, slot);
                    string first = demand.Function.Prefs.Count > 0 ? demand.Function.Prefs[0] : string.Empty;
                    if (pref == 0 && first.IndexOf('*') < 0 && sideMatch)
                        m = 0.8;
                    else if (pref >= 0 && sideMatch)
                        m = 0.5;
                    else if (sideMatch)
                        m = 0.25;
                }
                total += group.Weight * m;
            }
            return total / demand.RealWeight;
        }

        private static string Prefix(string control)
        {
            int dot = (control ?? string.Empty).LastIndexOf('.');
            return dot > 0 ? control.Substring(0, dot) : control ?? string.Empty;
        }

        private static string LastSegment(string control)
        {
            int dot = (control ?? string.Empty).LastIndexOf('.');
            return dot >= 0 ? control.Substring(dot + 1) : control ?? string.Empty;
        }

        internal static double Score(Run run, Demand demand, Slot slot)
        {
            if (demand.IsAxis != slot.Input.IsAxis)
                return Forbidden;
            if (!demand.Reachable.Contains(slot.Device.Device))
                return Forbidden;
            if (slot.Modifier != null && !demand.Reachable.Contains(slot.Modifier.Device))
                return Forbidden;
            // A phase layer only for functions of that combat phase; modifier layers only for games that handle them.
            if (slot.Phase.Length > 0 && (demand.Exclusive != slot.Phase || !demand.PhaseOk))
                return Forbidden;
            if (slot.Layer > 0 && slot.Phase.Length == 0 && !demand.ShiftOk)
                return Forbidden;
            SetupFunction function = demand.Function;
            bool style = demand.StyleDevice.Length > 0 && demand.StyleDevice == slot.Device.Device && string.Equals(demand.StyleInput, slot.Input.Input, StringComparison.OrdinalIgnoreCase);
            int pref = PrefIndex(run, function, slot);
            double space = SpaceRule(demand, slot);
            double real = RealMatch(run, demand, slot);
            bool realExact = run.Realism >= 0.5 && real >= 0.99 && demand.Real.All(g => g.Controls != null);
            string control = slot.RuleControl;
            string family = slot.Family;
            bool grip = slot.Grip;
            string side = demand.Side ?? "any";

            // A directional set's member only on the matching direction of a hat or thumb stick (never on another hat's press
            // or a plain button as a last resort).
            if (demand.DirRole.Length > 0 && !style && !SetupPhases.MatchesDirection(demand.DirRole, slot.RuleControl) && !SetupPhases.MatchesDirection(demand.DirRole, slot.Control))
                return Forbidden;

            // The user's preference rules (Lay out with AI): a bonus where the user wants a function, never where not.
            double ruleBonus = 0;
            bool wanted = false;
            foreach (SetupRule rule in demand.Rules)
            {
                bool hit = RuleHits(run, rule, slot);
                if ((hit && rule.Forbid) || (!hit && rule.Only))
                    return Forbidden;
                if (hit && rule.Bonus > 0)
                {
                    ruleBonus += rule.Bonus;
                    wanted |= rule.Controls.Length > 0;
                }
            }

            // Special devices: an MFD takes only MFD functions (in OSB order, by their position preferences); a printed
            // panel label takes only the functions it names; a cockpit replica's unlabelled controls only their own.
            bool exactPref = pref == 0 && function.Prefs.Count > 0 && function.Prefs[0].IndexOf('*') < 0;
            if (!style && !realExact && !wanted)
            {
                bool mfdFunction = SetupPhases.IsMfdFunction(function.Prefs);
                if ((slot.MfdDevice || SetupPhases.IsMfdControl(control)) && !mfdFunction)
                    return Forbidden;
                // A cockpit label on a base (the Warthog throttle's "APU START", "LDG GEAR WARN SILENCE") that does not name the
                // function: never for a function it contradicts or a time-critical / weapon function; else only once the
                // unlabelled inputs are used up (a strong penalty below).
                if (OtherBaseLabel(function, slot, control, exactPref, mfdFunction) &&
                    (SetupPhases.LabelContradicts(function.Labels, function.Id + " " + function.Name, slot.Input.Label) || SetupPhases.IsCriticalFunction(function.Id, demand.Crit, demand.Category)))
                    return Forbidden;
                if (!grip && (family == "panel" || family == "mfd") && !mfdFunction)
                {
                    bool printed = SetupPhases.IsFunctionLabel(slot.Input.Label);
                    if (printed && !exactPref && !LabelMatches(function, slot))
                        return Forbidden;
                    if (printed && !exactPref && SetupPhases.DirectionClash(SetupPhases.FunctionDirection(function.Id, function.PairRole), SetupPhases.LabelDirection(slot.Input.Label)))
                        return Forbidden;
                    if (!printed && slot.Replica && !exactPref)
                        return Forbidden;
                }
            }

            if (!style && !wanted && pref < 0 && space <= 0 && !realExact)
            {
                if (SetupPhases.SlewTmsClash(demand.FunctionId, function.Group, control))
                    return Forbidden;
                // Dedicated controls only for their functions, MFD keys only on MFD keys, cockpit panel functions off the grips.
                if (Dedicated.IsMatch(control) || (demand.IsAxis && DedicatedAxis.IsMatch(control)))
                    return Forbidden;
                if (function.Prefs.Count > 0 && function.Prefs.All(p => p.StartsWith("mfd.", StringComparison.Ordinal)))
                    return Forbidden;
                if (grip && (side == "panel" || side == "any") && (slot.Layer > 0 || !NumberedThrottleButton.IsMatch(control)))
                    return Forbidden;
            }

            double f = SetupImportance.FrequencyWeight(demand.Frequency) / 5.0;
            double fit;
            double exact = 0;
            if (space > 0)
            {
                fit = space;
                exact = 0.3;
            }
            else if (pref >= 0)
            {
                fit = 1.0 - 0.08 * pref;
                if (pref == 0 && function.Prefs[0].IndexOf('*') < 0)
                    exact = 0.3;
                else if (Named.IsMatch(control))
                    fit -= 0.4;
            }
            else
            {
                if (Hand(side) == Hand(family) && Hand(side) != "panel")
                    fit = 0.25;
                else if (side == "any" || side == "panel")
                    fit = family == "panel" || family == "mfd" ? 0.18 : 0.12;
                else
                    fit = demand.IsAxis ? 0.04 : 0.08;
                if (demand.IsAxis && function.Prefs.Count > 0 && function.Prefs.All(p => !p.StartsWith("*", StringComparison.Ordinal)) && side != "panel" && side != "any")
                    fit = 0.02;
            }

            double label = 0;
            if (LabelMatches(function, slot))
                label = 1.0 + 0.6 * SetupPhases.LabelFit(function.Labels, slot.Input.Label);
            else if (slot.Input.Label.Length > 0 && family == "panel")
                label = -0.6;
            else if (OtherBaseLabel(function, slot, control, exactPref, false))
                label = -4.0;
            double comfort = fit * (0.5 + f) + exact + label + RoleLabel(function, slot);
            // Reach: grips for the frequent and the time-critical functions, bases and panels for what can wait.
            if (grip)
                comfort += 0.25 * f + SetupPhases.GripBonus(demand.Crit);
            else if (control.IndexOf(".base.", StringComparison.Ordinal) >= 0)
                comfort += 0.1 * f;
            if (slot.Phase.Length > 0)
                comfort -= 0.15;
            else if (slot.Layer > 0)
                comfort -= ((0.25 + 0.3 * f) * slot.Layer + (grip ? 0 : 0.2)) * (run.Options.PreferFewer ? 1.6 : 1.0) * SetupPhases.LayerPenaltyFactor(demand.Crit);
            comfort += ruleBonus;
            if (slot.Input.Type == "latched" && function.PairGroup.Length == 0)
                comfort -= 0.35 * (0.5 + f);
            if (grip && f <= 0.2 && fit < 0.5)
                comfort -= 0.2;
            if (style)
                comfort += 4 + (demand.StyleShifted == (slot.Layer > 0) ? 1 : 0);
            return demand.Importance * (comfort + run.Realism * 2.0 * real);
        }


        // The position word of a printed label against the position the function asks for (LAUNCH BAR EXTEND, HOOK UP).
        private static double RoleLabel(SetupFunction function, Slot slot)
        {
            if (slot.Input.Label.Length == 0 || slot.Grip)
                return 0;
            string wanted = SetupPhases.FunctionDirection(function.Id, function.PairRole);
            string printed = SetupPhases.LabelDirection(slot.Input.Label);
            if (wanted.Length == 0 || printed.Length == 0)
                return 0;
            return wanted == printed ? 0.8 : -0.8;
        }

        // A cockpit label on a base that does not name the function (not its exact position, not an MFD function).
        private static bool OtherBaseLabel(SetupFunction function, Slot slot, string control, bool exactPref, bool mfdFunction)
        {
            return !slot.Grip && control.IndexOf(".base.", StringComparison.Ordinal) >= 0 && !mfdFunction && !exactPref &&
                   SetupPhases.IsCockpitLabel(slot.Input.Label) && !LabelMatches(function, slot);
        }

        private static bool LabelMatches(SetupFunction function, Slot slot)
        {
            if (slot.Input.Label.Length == 0 || slot.Grip)
                return false;
            return SetupPhases.LabelNames(function, slot.Input.Label);
        }

        // ---- the user's preference rules ----

        private static Regex RuleRegex(Run run, string pattern)
        {
            Regex regex;
            if (!run.RuleRegex.TryGetValue(pattern, out regex))
            {
                try
                {
                    regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                catch (ArgumentException)
                {
                    regex = new Regex("(?!)");
                }
                run.RuleRegex[pattern] = regex;
            }
            return regex;
        }

        // A rule concerns a function by its id (or, for the profile's own relations without one, the relation's name),
        // its category and the aircraft classes of its relation.
        private static bool RuleConcerns(Run run, SetupRule rule, Demand demand)
        {
            if (rule.Category.Length > 0 && !string.Equals(rule.Category, demand.Category, StringComparison.OrdinalIgnoreCase))
                return false;
            if (rule.Class.Length > 0 && !(demand.Classes.Count == 1 && demand.Classes.Contains(rule.Class)))
                return false;
            if (rule.Functions.Length == 0)
                return true;
            Regex regex = RuleRegex(run, rule.Functions);
            return regex.IsMatch(demand.FunctionId) || (demand.FunctionId.Length == 0 && regex.IsMatch(demand.Name.Replace(' ', '_').ToLowerInvariant()));
        }

        private static bool RuleHits(Run run, SetupRule rule, Slot slot)
        {
            if (rule.Role.Length > 0 && !string.Equals(rule.Role, slot.Role, StringComparison.Ordinal))
                return false;
            if (rule.Controls.Length == 0)
                return true;
            Regex regex = RuleRegex(run, rule.Controls);
            return regex.IsMatch(slot.Control) || regex.IsMatch(slot.RuleControl);
        }

        // ---- occupancy ----

        private static bool Free(Run run, Slot slot, Demand demand, Demand except)
        {
            foreach (Demand other in run.Occupants[slot.Index])
            {
                if (other != except && other != demand && Overlaps(other.Bits, demand.Bits))
                    return false;
            }
            List<Demand> kept;
            if (run.KeptAt.TryGetValue(slot.Key, out kept))
            {
                foreach (Demand other in kept)
                {
                    if (other != except && Overlaps(other.Bits, demand.Bits))
                        return false;
                }
            }
            List<Slot> siblings;
            if (slot.BaseKey.Length > 0 && run.Siblings.TryGetValue(slot.BaseKey, out siblings) && siblings.Count > 1)
            {
                bool phase = slot.Phase.Length > 0;
                foreach (Slot sibling in siblings)
                {
                    if (sibling == slot || (sibling.Phase.Length > 0) == phase)
                        continue;
                    // The normal-layer function and the phase layer's function of one input: while the switch is on that
                    // phase the input does the phase layer's function, so the normal one must not be needed in that
                    // phase (it is a function of the other combat phase only).
                    string layerPhase = phase ? slot.Phase : sibling.Phase;
                    foreach (Demand other in run.Occupants[sibling.Index])
                    {
                        Demand normal = phase ? other : demand;
                        if (other != except && other != demand && Overlaps(other.Bits, demand.Bits) && !OtherPhaseOnly(normal, layerPhase))
                            return false;
                    }
                    if (run.KeptAt.TryGetValue(sibling.Key, out kept) && kept.Any(other => other != except && Overlaps(other.Bits, demand.Bits)))
                        return false;
                }
            }
            return true;
        }

        private static bool OtherPhaseOnly(Demand normal, string layerPhase)
        {
            return normal != null && !normal.Kept && normal.Exclusive.Length > 0 && normal.Exclusive != layerPhase;
        }

        private static void Place(Run run, Demand demand, Slot slot, string reason)
        {
            Unplace(run, demand);
            run.Placed[demand] = slot;
            run.Occupants[slot.Index].Add(demand);
            if (reason != null)
                demand.Reason = reason;
        }

        private static void Unplace(Run run, Demand demand)
        {
            Slot old;
            if (run.Placed.TryGetValue(demand, out old))
            {
                run.Occupants[old.Index].Remove(demand);
                run.Placed.Remove(demand);
            }
        }

        // ---- greedy start ----

        private static void AssignStyle(Run run, List<Demand> open)
        {
            foreach (Demand demand in open.Where(d => d.StyleDevice.Length > 0).OrderByDescending(d => d.Importance).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                Slot slot = run.Slots.Where(s => s.Device.Device == demand.StyleDevice && string.Equals(s.Input.Input, demand.StyleInput, StringComparison.OrdinalIgnoreCase) &&
                                                 (s.Layer > 0) == demand.StyleShifted && S(run, demand, s) > Forbidden && Free(run, s, demand, null))
                    .OrderByDescending(s => S(run, demand, s)).FirstOrDefault();
                if (slot == null)
                    continue;
                // A preference that wants the function elsewhere (Lay out with AI) beats the previous control.
                if (demand.Rules.Any(r => r.Bonus > 0 && r.Controls.Length > 0) && !demand.Rules.Any(r => r.Bonus > 0 && r.Controls.Length > 0 && RuleHits(run, r, slot)))
                    continue;
                Place(run, demand, slot, "your own control from before");
                demand.Fixed = true;
            }
        }

        // A strong match: the function's own label on a panel, its exact position, where the real aircraft has it, the
        // left stick's translation axes.
        // strength 2: only where the real aircraft has it (cockpit database); 1: any strong match.
        private static bool IsStrong(Run run, Demand demand, Slot slot, int strength = 1)
        {
            if (demand.IsAxis != slot.Input.IsAxis)
                return false;
            if (strength >= 2)
                return run.Realism >= 0.5 && demand.Real.Count > 0 && demand.Real.All(g => g.Controls != null) && RealMatch(run, demand, slot) >= 0.99;
            if (LabelMatches(demand.Function, slot) || SpaceRule(demand, slot) > 0 || Wanted(run, demand, slot))
                return true;
            int pref = PrefIndex(run, demand.Function, slot);
            if (pref == 0 && demand.Function.Prefs[0].IndexOf('*') < 0)
                return true;
            return run.Realism >= 0.5 && demand.Real.All(g => g.Controls != null) && RealMatch(run, demand, slot) >= 0.99;
        }

        // The user asked for this function on this control (Lay out with AI).
        private static bool Wanted(Run run, Demand demand, Slot slot)
        {
            return demand.Rules.Count > 0 && demand.Rules.Any(r => r.Bonus > 0 && r.Controls.Length > 0 && RuleHits(run, r, slot));
        }

        private static bool HasWish(Demand demand)
        {
            return demand.Rules.Any(r => r.Bonus > 0 && r.Controls.Length > 0);
        }

        private static void AssignPairs(Run run, List<Demand> open, int strength)
        {
            List<List<Demand>> pairs = new List<List<Demand>>();
            foreach (IGrouping<string, Demand> group in open.Where(d => !d.IsAxis && d.Function.PairGroup.Length > 0 && !run.Placed.ContainsKey(d))
                         .GroupBy(d => d.Function.PairGroup + "|" + d.HandKey, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                List<IGrouping<string, Demand>> roles = group.GroupBy(d => d.Function.PairRole, StringComparer.Ordinal).OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
                if (roles.Count != 2)
                    continue;
                foreach (Demand a in roles[0].OrderBy(d => d.Name, StringComparer.Ordinal))
                {
                    Demand b = roles[1].Where(x => Overlaps(a.Bits, x.Bits) && !pairs.Any(p => p.Contains(x)))
                        .OrderByDescending(x => x.Aircraft.Intersect(a.Aircraft, StringComparer.OrdinalIgnoreCase).Count()).ThenBy(x => x.Name, StringComparer.Ordinal).FirstOrDefault();
                    if (b != null)
                        pairs.Add(new List<Demand> { a, b });
                }
            }
            List<Tuple<Slot, Slot>> toggles = run.Slots.Where(s => s.Layer == 0 && Regex.IsMatch(s.RuleControl, @"\.(toggle3?\.\d+|lever\.gear)\.up$"))
                .Select(up => Tuple.Create(up, run.Slots.FirstOrDefault(d => d.Layer == 0 && d.Device == up.Device && d.RuleControl == Regex.Replace(up.RuleControl, @"\.up$", ".down"))))
                .Where(t => t.Item2 != null).ToList();
            if (pairs.Count == 0 || toggles.Count == 0)
                return;
            foreach (List<Demand> pair in pairs.OrderByDescending(p => p.Any(HasWish)).ThenByDescending(p => p.Sum(d => d.Importance)).ThenBy(p => p[0].Name, StringComparer.Ordinal))
            {
                double top = Forbidden;
                Tuple<Slot, Slot> best = null;
                bool swap = false;
                if (run.Placed.ContainsKey(pair[0]) || run.Placed.ContainsKey(pair[1]))
                    continue;
                foreach (Tuple<Slot, Slot> toggle in toggles)
                {
                    double straight = PairScore(run, pair[0], toggle.Item1, pair[1], toggle.Item2, strength);
                    double reverse = PairScore(run, pair[0], toggle.Item2, pair[1], toggle.Item1, strength);
                    double value = Math.Max(straight, reverse);
                    if (value > top + Eps)
                    {
                        top = value;
                        best = toggle;
                        swap = reverse > straight;
                    }
                }
                if (best == null || top <= Forbidden)
                    continue;
                // A pair only goes on a toggle when that is better than the two functions' own best places.
                Place(run, pair[0], swap ? best.Item2 : best.Item1, "on/off pair on one switch");
                Place(run, pair[1], swap ? best.Item1 : best.Item2, "on/off pair on one switch");
                pair[0].Fixed = pair[1].Fixed = true;
            }
        }

        private static double PairScore(Run run, Demand first, Slot firstSlot, Demand second, Slot secondSlot, int strength)
        {
            if (!Free(run, firstSlot, first, null) || !Free(run, secondSlot, second, null))
                return Forbidden;
            if (strength > 0 && (!IsStrong(run, first, firstSlot, strength) || !IsStrong(run, second, secondSlot, strength)))
                return Forbidden;
            double a = S(run, first, firstSlot);
            double b = S(run, second, secondSlot);
            return a <= Forbidden || b <= Forbidden ? Forbidden : a + b + 0.4 * (first.Importance + second.Importance) / 2.0;
        }

        // Functions that share one hat / switch / 2D stick in real aircraft go on one physical hat together.
        private static void AssignGroups(Run run, List<Demand> open, int strength)
        {
            // The left stick's translation axes are single functions (X, Y, twist), not a thumb stick group.
            // Placed members (an earlier round, the user's own control) anchor their group: the others join that hat.
            List<List<Demand>> groups = open.Where(d => d.Function.Group.Length > 0 && !(d.SpaceLeftStick && d.FunctionId.StartsWith("space_strafe", StringComparison.Ordinal)))
                .GroupBy(d => d.Function.Group.Split(':')[0] + "|" + d.HandKey + "|" + Prefix(d.RealKey.Split('+')[0]), StringComparer.Ordinal)
                .Where(g => (g.Count() >= 2 || g.First().DirRole.Length > 0) && g.Any(d => !run.Placed.ContainsKey(d))).Select(g => g.OrderBy(d => d.Name, StringComparer.Ordinal).ToList()).ToList();
            Func<Slot, string> setKey = s => s.Device.Device + "|" + s.Layer.ToString(CultureInfo.InvariantCulture) + "|" + s.Input.IsAxis + "|" + Prefix(s.RuleControl);
            List<List<Slot>> sets = run.Slots.Where(s => GroupRoles.Contains(LastSegment(s.RuleControl)))
                .GroupBy(setKey, StringComparer.Ordinal)
                .Where(g => g.Count() >= 2).Select(g => g.ToList()).ToList();
            if (groups.Count == 0 || sets.Count == 0)
                return;
            // The user's wishes first, then the most important group.
            foreach (List<Demand> group in groups.OrderByDescending(g => g.Any(HasWish)).ThenByDescending(g => g.Sum(d => d.Importance)).ThenBy(g => g[0].Name, StringComparer.Ordinal))
            {
                double top = Forbidden;
                List<Tuple<Demand, Slot>> best = null;
                List<Demand> members = group.Where(d => !run.Placed.ContainsKey(d)).ToList();
                HashSet<string> anchors = new HashSet<string>(group.Where(d => run.Placed.ContainsKey(d)).Select(d => setKey(run.Placed[d])), StringComparer.Ordinal);
                // A directional set with one member (TMS up alone) still goes on its direction of a fitting hat.
                if (members.Count == 0 || (members.Count < 2 && anchors.Count == 0 && members[0].DirRole.Length == 0))
                    continue;
                foreach (List<Slot> set in sets)
                {
                    if (anchors.Count > 0 && !anchors.Contains(setKey(set[0])))
                        continue;
                    List<Tuple<Demand, Slot>> placement;
                    double value = EvaluateGroup(run, members, set, strength, out placement, anchors.Count > 0 || members.Count == 1 ? 1 : 2);
                    if (value > top + Eps)
                    {
                        top = value;
                        best = placement;
                    }
                }
                if (best == null)
                    continue;
                foreach (Tuple<Demand, Slot> item in best)
                {
                    Place(run, item.Item1, item.Item2, "together with its group on one hat");
                    item.Item1.Fixed = true;
                }
            }
        }

        private static double EvaluateGroup(Run run, List<Demand> members, List<Slot> set, int strength, out List<Tuple<Demand, Slot>> placement, int minimum = 2)
        {
            placement = new List<Tuple<Demand, Slot>>();
            List<Tuple<Demand, Slot, double>> options = new List<Tuple<Demand, Slot, double>>();
            foreach (Demand member in members)
            {
                foreach (Slot slot in set)
                {
                    if ((PrefIndex(run, member.Function, slot) >= 0 || SpaceRule(member, slot) > 0 || Wanted(run, member, slot)) && (strength == 0 || IsStrong(run, member, slot, strength)) && Free(run, slot, member, null))
                    {
                        double value = S(run, member, slot);
                        if (value > Forbidden)
                            options.Add(Tuple.Create(member, slot, value));
                    }
                }
            }
            double total = 0;
            HashSet<Demand> done = new HashSet<Demand>();
            foreach (Tuple<Demand, Slot, double> option in options.OrderByDescending(o => o.Item3).ThenBy(o => o.Item1.Name, StringComparer.Ordinal).ThenBy(o => o.Item2.Index))
            {
                if (done.Contains(option.Item1) || placement.Any(p => p.Item2 == option.Item2 && Overlaps(p.Item1.Bits, option.Item1.Bits)))
                    continue;
                placement.Add(Tuple.Create(option.Item1, option.Item2));
                done.Add(option.Item1);
                total += option.Item3;
            }
            double average = members.Average(m => m.Importance);
            return placement.Count >= minimum ? total + 0.2 * average * placement.Count - 0.6 * average * (members.Count - placement.Count) : Forbidden;
        }

        // Single functions by importance: each on its best free input.
        private static void AssignSingles(Run run, List<Demand> open, int strength)
        {
            // A member of a directional set (TMS, DMS, CMS, trim ...) is placed only with its set on one hat (AssignGroups).
            foreach (Demand demand in open.Where(d => !run.Placed.ContainsKey(d) && d.DirRole.Length == 0).OrderByDescending(HasWish).ThenByDescending(d => d.Importance).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList())
            {
                Slot best = BestFree(run, demand, null, strength);
                if (best != null)
                    Place(run, demand, best, null);
            }
        }

        private static Slot BestFree(Run run, Demand demand, Demand except, int strength = 0)
        {
            Slot best = null;
            double top = Forbidden;
            foreach (Slot slot in run.Slots)
            {
                double value = S(run, demand, slot);
                if (value <= Forbidden || value <= top + Eps)
                    continue;
                if (strength > 0 && !IsStrong(run, demand, slot, strength))
                    continue;
                if (!Free(run, slot, demand, except))
                    continue;
                top = value;
                best = slot;
            }
            return best;
        }

        // ---- local search: move, swap, displace (fixed seed) ----

        private static void LocalSearch(Run run, List<Demand> open)
        {
            List<Demand> singles = open.Where(d => !d.Fixed && d.DirRole.Length == 0).OrderByDescending(d => d.Importance).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Random random = new Random(run.Options.Seed);
            for (int pass = 0; pass < 4; pass++)
            {
                bool changed = false;
                List<Demand> order = singles.ToList();
                if (pass > 0)
                {
                    // Fisher-Yates with the fixed seed: the same plan for the same input.
                    for (int i = order.Count - 1; i > 0; i--)
                    {
                        int j = random.Next(i + 1);
                        Demand t = order[i];
                        order[i] = order[j];
                        order[j] = t;
                    }
                }
                foreach (Demand demand in order)
                {
                    Slot current;
                    run.Placed.TryGetValue(demand, out current);
                    double currentScore = current == null ? 0 : S(run, demand, current);

                    // move to a better free input
                    Slot better = BestFree(run, demand, null);
                    if (better != null && better != current && S(run, demand, better) > currentScore + Eps)
                    {
                        Place(run, demand, better, null);
                        changed = true;
                        continue;
                    }
                    if (current != null)
                    {
                        if (TrySwap(run, demand, current))
                            changed = true;
                    }
                    else if (TryDisplace(run, demand))
                    {
                        changed = true;
                    }
                }
                if (!changed)
                    break;
            }
        }

        private static bool TrySwap(Run run, Demand demand, Slot current)
        {
            double currentScore = S(run, demand, current);
            double bestGain = Eps;
            Slot bestSlot = null;
            Demand bestOther = null;
            foreach (Slot slot in run.Slots)
            {
                if (slot == current)
                    continue;
                double mine = S(run, demand, slot);
                if (mine <= Forbidden || mine <= currentScore)
                    continue;
                List<Demand> blockers = run.Occupants[slot.Index].Where(o => Overlaps(o.Bits, demand.Bits)).ToList();
                if (blockers.Count != 1 || blockers[0].Fixed || blockers[0].Kept)
                    continue;
                Demand other = blockers[0];
                if (!Free(run, slot, demand, other) || !Free(run, current, other, demand))
                    continue;
                double theirs = S(run, other, current);
                if (theirs <= Forbidden)
                    continue;
                double gain = mine + theirs - currentScore - S(run, other, slot);
                if (gain > bestGain)
                {
                    bestGain = gain;
                    bestSlot = slot;
                    bestOther = other;
                }
            }
            if (bestSlot == null)
                return false;
            Unplace(run, bestOther);
            Place(run, demand, bestSlot, null);
            Place(run, bestOther, current, null);
            return true;
        }

        // An unplaced function takes the input of a less valuable one that can move elsewhere.
        private static bool TryDisplace(Run run, Demand demand)
        {
            List<Slot> candidates = run.Slots.Where(s => S(run, demand, s) > Forbidden).OrderByDescending(s => S(run, demand, s)).ThenBy(s => s.Index).Take(30).ToList();
            double bestGain = Eps;
            Slot bestSlot = null;
            Demand bestOther = null;
            Slot bestTarget = null;
            foreach (Slot slot in candidates)
            {
                List<Demand> blockers = run.Occupants[slot.Index].Where(o => Overlaps(o.Bits, demand.Bits)).ToList();
                if (blockers.Count != 1 || blockers[0].Fixed || blockers[0].Kept || !Free(run, slot, demand, blockers[0]))
                    continue;
                Demand other = blockers[0];
                Slot target = BestFree(run, other, demand);
                double moved = target == null || target == slot ? 0 : S(run, other, target);
                double gain = S(run, demand, slot) + moved - S(run, other, slot);
                if (gain > bestGain)
                {
                    bestGain = gain;
                    bestSlot = slot;
                    bestOther = other;
                    bestTarget = target == slot ? null : target;
                }
            }
            if (bestSlot == null)
                return false;
            Unplace(run, bestOther);
            Place(run, demand, bestSlot, null);
            if (bestTarget != null && Free(run, bestTarget, bestOther, null))
                Place(run, bestOther, bestTarget, null);
            return true;
        }

        // ---- the plan ----

        private static SetupPlan Build(Run run, SetupRequest request, SetupOptions options)
        {
            SetupPlan plan = new SetupPlan
            {
                Games = (options.Games ?? new List<string>()).ToList(),
                Priorities = (options.Priorities ?? new List<List<string>>()).Select(t => t.ToList()).ToList(),
                ModifiersWanted = options.Modifiers,
                PreferFewer = options.PreferFewer,
                Realism = run.Realism,
                Seed = options.Seed,
                CockpitSource = request.Cockpits.Source,
                Devices = request.Devices,
                Rules = SetupRoles.Rules(request.Devices),
                Modifiers = run.Modifiers
            };
            HashSet<string> binding = new HashSet<string>(request.Devices.Where(d => SetupRoles.Binds(d.Role)).Select(d => d.Device), StringComparer.Ordinal);
            plan.Inputs = request.Inputs.Where(i => binding.Contains(i.Device)).ToList();
            plan.Warnings.AddRange(request.Warnings.Distinct());

            Dictionary<int, string> keyOf = run.AircraftIndex.ToDictionary(p => p.Value, p => p.Key);
            Func<ulong[], List<string>> keysOf = bits =>
            {
                List<string> list = new List<string>();
                if (bits.All(w => w == ulong.MaxValue))
                    return new List<string> { "*" };
                for (int bit = 0; bit < bits.Length * 64; bit++)
                {
                    string key;
                    if ((bits[bit >> 6] & (1UL << (bit & 63))) != 0 && keyOf.TryGetValue(bit, out key))
                        list.Add(key);
                }
                return list;
            };

            // Parts of one function that ended on the same input become one relation again (fewer, clearer relations);
            // a function placed in one piece keeps its plain name.
            Dictionary<Demand, List<Demand>> merged = new Dictionary<Demand, List<Demand>>();
            Dictionary<Demand, string> names = new Dictionary<Demand, string>();
            HashSet<Demand> absorbed = new HashSet<Demand>();
            HashSet<string> taken = new HashSet<string>(request.Existing.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            foreach (Demand demand in run.Demands.Where(d => d.Kept || d.Existing || d.FunctionId.Length == 0))
                taken.Add(demand.Name);
            foreach (IGrouping<string, Demand> byFunction in run.Demands.Where(d => !d.Kept && !d.Existing && d.FunctionId.Length > 0).GroupBy(d => d.FunctionId, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                List<Demand> parts = byFunction.ToList();
                List<List<Demand>> groups = parts.Where(d => run.Placed.ContainsKey(d)).GroupBy(d => run.Placed[d].Key, StringComparer.Ordinal)
                    .Select(g => g.OrderByDescending(x => x.Importance).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList()).ToList();
                int pieces = groups.Count + parts.Count(d => !run.Placed.ContainsKey(d));
                foreach (List<Demand> group in groups)
                {
                    Demand leader = group[0];
                    if (group.Count > 1)
                    {
                        merged[leader] = group;
                        absorbed.UnionWith(group.Skip(1));
                    }
                    string name = leader.Name;
                    if (pieces == 1)
                        name = leader.Function.Name;
                    else if (group.Count > 1)
                        name = leader.Function.Name + " [" + SetupText.Join(group.SelectMany(d => d.Aircraft).Select(a =>
                        {
                            SetupAircraft plane;
                            return request.Aircraft.TryGetValue(a, out plane) && plane.VariantOf.Length > 0 ? plane.VariantOf : a;
                        }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase), 3) + "]";
                    if (name != leader.Name && taken.Contains(name))
                        name = leader.Name;
                    string unique = name;
                    for (int i = 2; taken.Contains(unique) && unique != leader.Name; i++)
                        unique = name + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
                    names[leader] = unique;
                    taken.Add(unique);
                }
            }

            foreach (Demand demand in run.Demands.OrderByDescending(d => d.Kept ? 0 : 1).ThenByDescending(d => d.Importance).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (absorbed.Contains(demand))
                    continue;
                Slot slot;
                run.Placed.TryGetValue(demand, out slot);
                List<Demand> members;
                if (!merged.TryGetValue(demand, out members))
                    members = new List<Demand> { demand };
                ulong[] bits = new ulong[run.Words];
                foreach (Demand member in members)
                {
                    for (int i = 0; i < bits.Length; i++)
                        bits[i] |= member.Bits[i];
                }
                string relationName;
                SetupPlanRelation relation = new SetupPlanRelation
                {
                    Name = names.TryGetValue(demand, out relationName) ? relationName : demand.Name,
                    Function = demand.FunctionId,
                    FunctionName = demand.Function != null && demand.FunctionId.Length > 0 ? demand.Function.Name : string.Empty,
                    Category = demand.Category,
                    IsAxis = demand.IsAxis,
                    Existing = demand.Existing,
                    Kept = demand.Kept,
                    Nodes = members.SelectMany(m => m.Nodes).ToList(),
                    Aircraft = members.SelectMany(m => m.Aircraft).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList(),
                    Overlap = keysOf(bits),
                    Importance = Math.Round(demand.Importance, 3)
                };
                if (demand.Kept && demand.Source != null)
                {
                    relation.Device = demand.Source.Device;
                    relation.Input = demand.Source.Input;
                    relation.Modifier = demand.Source.Modifier;
                    SetupInput input = request.Inputs.FirstOrDefault(i => i.Device == relation.Device && string.Equals(i.Input, relation.Input, StringComparison.OrdinalIgnoreCase));
                    relation.Control = input != null ? input.Control : string.Empty;
                    relation.Label = input != null ? input.Label : string.Empty;
                    relation.Reason = "your bind, kept";
                }
                else if (slot != null)
                {
                    relation.Device = slot.Device.Device;
                    relation.Input = slot.Input.Input;
                    relation.Control = slot.Control;
                    relation.Label = slot.Input.Label;
                    relation.Modifier = slot.Modifier == null ? string.Empty : slot.Modifier.Name;
                    relation.Score = Math.Round(members.Sum(m => S(run, m, slot)), 3);
                    relation.RealMatch = Math.Round(members.Average(m => RealMatch(run, m, slot)), 3);
                    relation.Phase = demand.Exclusive;
                    relation.Reason = demand.Reason.Length > 0 ? demand.Reason : Why(run, demand, slot);
                    if (slot.Phase.Length > 0)
                        relation.Reason += "; " + SetupPhases.Title(slot.Phase).ToLowerInvariant() + " layer of the mode switch";
                    else if (demand.Rules.Any(r => r.Bonus > 0 && RuleHits(run, r, slot)))
                        relation.Reason += "; your preference";
                }
                plan.Relations.Add(relation);
                if (!demand.Kept && slot == null)
                {
                    plan.NotPlaced.Add(new SetupNotPlaced
                    {
                        Relation = demand.Name,
                        Function = demand.FunctionId,
                        Category = demand.Category,
                        Importance = Math.Round(demand.Importance, 3),
                        Aircraft = demand.Aircraft.ToList(),
                        Reason = WhyNot(run, demand)
                    });
                }
            }

            // Quality per aircraft: how close each function is to its best position, weighted by importance.
            Dictionary<string, int> tierOf;
            Dictionary<string, double> weights = SetupImportance.Weights(options.Priorities, request.Aircraft.Values, out tierOf);
            HashSet<string> games = new HashSet<string>(options.Games ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            Dictionary<Demand, double> bestScore = new Dictionary<Demand, double>();
            foreach (Demand demand in run.Demands.Where(d => !d.Kept))
            {
                double best = 0;
                foreach (Slot s in run.Slots.Where(x => x.Layer == 0))
                    best = Math.Max(best, S(run, demand, s));
                bestScore[demand] = best;
            }
            foreach (IGrouping<string, string> aircraft in run.Demands.SelectMany(d => d.Aircraft).Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(a =>
                         {
                             SetupAircraft plane;
                             return request.Aircraft.TryGetValue(a, out plane) && games.Contains(plane.Game) && !plane.SharedLayer && plane.VariantOf.Length == 0;
                         })
                         .GroupBy(a => a, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                string key = aircraft.Key;
                List<Demand> mine = run.Demands.Where(d => d.Aircraft.Contains(key, StringComparer.OrdinalIgnoreCase)).ToList();
                if (mine.Count == 0)
                    continue;
                double sum = 0, total = 0;
                int placed = 0, essential = 0, essentialPlaced = 0;
                foreach (Demand demand in mine)
                {
                    double w = SetupImportance.CategoryWeight(demand.Category) * SetupImportance.FrequencyWeight(demand.Frequency);
                    total += w;
                    bool isEssential = SetupImportance.Essential(demand.Category, demand.Frequency);
                    if (isEssential)
                        essential++;
                    Slot slot;
                    double q = 0;
                    if (demand.Kept)
                        q = 1;
                    else if (run.Placed.TryGetValue(demand, out slot))
                    {
                        double best = bestScore[demand];
                        double actual = S(run, demand, slot);
                        q = best <= 0 ? 1 : Math.Max(0, Math.Min(1, actual / best));
                    }
                    if (demand.Kept || run.Placed.ContainsKey(demand))
                    {
                        placed++;
                        if (isEssential)
                            essentialPlaced++;
                    }
                    sum += w * q;
                }
                SetupAircraft plane = request.Aircraft[key];
                int tier;
                plan.Quality.Add(new SetupQuality
                {
                    Aircraft = key,
                    Game = plane.Game,
                    Tier = tierOf.TryGetValue(key, out tier) ? tier : -1,
                    Weight = Math.Round(weights.ContainsKey(key) ? weights[key] : 0, 3),
                    Score = Math.Round(total <= 0 ? 100 : 100 * sum / total, 1),
                    Placed = placed,
                    Total = mine.Count,
                    EssentialPlaced = essentialPlaced,
                    EssentialTotal = essential
                });
            }
            plan.Quality = plan.Quality.OrderBy(q => q.Tier < 0 ? int.MaxValue : q.Tier).ThenBy(q => q.Game, StringComparer.OrdinalIgnoreCase).ThenBy(q => q.Aircraft, StringComparer.OrdinalIgnoreCase).ToList();

            int created = plan.Relations.Count(r => !r.Kept && !r.Existing);
            int placedNew = plan.Relations.Count(r => !r.Kept && r.Device.Length > 0);
            plan.Notes.Add(created.ToString(CultureInfo.InvariantCulture) + " new relations for " + plan.Quality.Count.ToString(CultureInfo.InvariantCulture) + " aircraft / games; " +
                           placedNew.ToString(CultureInfo.InvariantCulture) + " functions placed, " + plan.NotPlaced.Count.ToString(CultureInfo.InvariantCulture) + " not placed, " +
                           run.Demands.Count(d => d.Kept).ToString(CultureInfo.InvariantCulture) + " of your binds kept.");
            plan.Notes.AddRange(request.Notes);
            plan.Notes.Add(run.Realism < 0.5
                ? "Realism " + Math.Round(run.Realism * 100).ToString(CultureInfo.InvariantCulture) + " %: grouped by function, one button per function in every aircraft."
                : "Realism " + Math.Round(run.Realism * 100).ToString(CultureInfo.InvariantCulture) + " %: aircraft whose real HOTAS has a function elsewhere got their own relation (" +
                  plan.Relations.Count(r => !r.Kept && !r.Existing && r.Name.EndsWith("]", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture) + " split relations). Positions from the " + request.Cockpits.Source + ".");
            if (plan.Modifiers.Any(m => m.Switch))
            {
                SetupPlanModifier first = plan.Modifiers.First(m => m.Switch);
                plan.Notes.Add("Phase layers on the mode switch " + SetupRoles.Short(request.Devices.First(d => d.Device == first.Device)) + " (" + JoyPro.DeviceReference.DeviceVocabulary.ElementName(Prefix(first.Control)) + "): " +
                               string.Join(", ", plan.Modifiers.Where(m => m.Switch).Select(m => (m.Control.EndsWith(".down", StringComparison.Ordinal) || m.Control.EndsWith(".right", StringComparison.Ordinal) || m.Control.EndsWith(".pos.2", StringComparison.Ordinal) ? "down" : "up") + " = " + SetupPhases.Title(m.Phase).ToLowerInvariant())) +
                               "; the centre / the other position is the normal layer. Flight, take-off and landing functions stay on the normal layer.");
            }
            foreach (SetupPlanModifier modifier in plan.Modifiers)
            {
                int uses = run.Placed.Values.Count(s => s.Modifier == modifier);
                plan.Notes.Add("Modifier \"" + modifier.Name + "\" = " + SetupRoles.Short(request.Devices.First(d => d.Device == modifier.Device)) + " " + Readable(modifier.Input, modifier.Control, string.Empty) +
                               ": " + modifier.Reason + "; " + uses.ToString(CultureInfo.InvariantCulture) + " functions on its layer.");
            }
            return plan;
        }

        // An input in plain words for the notes and reasons: "Button 5 (Pinky lever)", "Hat 1 down".
        internal static string Readable(string input, string control, string label)
        {
            string name = JoyPro.DeviceReference.DeviceVocabulary.InputName(input);
            string part = (control ?? string.Empty).Length > 0 || (label ?? string.Empty).Trim().Length > 0 ? JoyPro.DeviceReference.DeviceVocabulary.Describe(control, label) : string.Empty;
            return part.Length > 0 && !string.Equals(part, name, StringComparison.OrdinalIgnoreCase) ? name + " (" + part + ")" : name;
        }

        private static string Why(Run run, Demand demand, Slot slot)
        {
            if (SpaceRule(demand, slot) > 0)
                return "translation on the left stick (space sims)";
            if (LabelMatches(demand.Function, slot))
                return "the panel label \"" + slot.Input.Label + "\"";
            if (!slot.Grip && slot.Control.IndexOf(".base.", StringComparison.Ordinal) >= 0 && SetupPhases.IsCockpitLabel(slot.Input.Label))
                return "on a labelled switch (" + slot.Input.Label + "): no free unlabelled input left";
            double real = RealMatch(run, demand, slot);
            if (run.Realism >= 0.5 && real >= 0.99)
                return "where the real aircraft has it";
            int pref = PrefIndex(run, demand.Function, slot);
            if (pref == 0 && demand.Function.Prefs.Count > 0 && demand.Function.Prefs[0].IndexOf('*') < 0)
                return "its own position";
            if (pref >= 0)
                return "a fitting position (preference " + (pref + 1).ToString(CultureInfo.InvariantCulture) + ")";
            return slot.Layer > 0 ? "a free grip button behind " + slot.Modifier.Name : "the best free input left";
        }

        private static string WhyNot(Run run, Demand demand)
        {
            if (demand.DirRole.Length > 0)
                return "no hat with the free " + demand.DirRole + " direction for its set (" + demand.DirKey.ToUpperInvariant() + "): a set goes together on one hat or not at all";
            List<Slot> possible = run.Slots.Where(s => S(run, demand, s) > Forbidden).ToList();
            if (possible.Count == 0)
            {
                if (demand.Function.Prefs.Count > 0 && demand.Function.Prefs.All(p => p.StartsWith("mfd.", StringComparison.Ordinal)))
                    return "needs an MFD device";
                bool anyType = run.Slots.Any(s => s.Input.IsAxis == demand.IsAxis && demand.Reachable.Contains(s.Device.Device));
                if (!anyType)
                    return "no " + (demand.IsAxis ? "axis" : "button") + " on the devices the hands reach for " + string.Join(" / ", demand.Classes.Select(SetupClasses.Title));
                return demand.IsAxis ? "no fitting axis (flight axes only take their own functions)" : "no fitting input (grips keep cockpit panel functions off; no panel button left)";
            }
            Slot best = possible.OrderByDescending(s => S(run, demand, s)).First();
            Demand blocker = run.Occupants[best.Index].Where(o => Overlaps(o.Bits, demand.Bits)).OrderByDescending(o => o.Importance).FirstOrDefault();
            List<Demand> kept;
            if (blocker == null && run.KeptAt.TryGetValue(best.Key, out kept))
                blocker = kept.FirstOrDefault(o => Overlaps(o.Bits, demand.Bits));
            if (blocker != null)
            {
                string shared = demand.Aircraft.Intersect(blocker.Aircraft, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                return "every fitting input is taken by more important functions of " + (shared ?? "the same aircraft") + " (best: " + best.Device.Model + " " + JoyPro.DeviceReference.DeviceVocabulary.InputName(best.Input.Input) +
                       (best.Modifier != null ? " + " + best.Modifier.Name : string.Empty) + " = " + blocker.Name + ")";
            }
            return "every fitting input is taken";
        }
    }
}
