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
        private static readonly Regex CenteringAxis = new Regex(@"^(pitch|roll|rudder|space_yaw)$", RegexOptions.Compiled);
        private static readonly Regex ToeBrake = new Regex(@"^wheelbrake(left|right)analog(inv)?$", RegexOptions.Compiled);
        private static readonly Regex BrakeAxis = new Regex(@"\.axis\.brake|brake_lever", RegexOptions.Compiled);
        private static readonly Regex NonCentering =new Regex(@"\.axis\.(lever|slider|wheel|flaps|throttle|main|left|right|detent|rotary|trim|friction|prop|mixture|spoiler|condition)", RegexOptions.Compiled);
        private static readonly Regex HatControl = new Regex(@"\.(hat|coolie)\.", RegexOptions.Compiled);
        private static readonly Regex NumberedThrottleButton = new Regex(@"^(throttle|collective)\.button\.\d+$", RegexOptions.Compiled);
        // Throttle levers (not a knob, thumb wheel or thumb stick): where an engine's throttle belongs.
        private static readonly Regex LeverAxis = new Regex(@"^(throttle|quadrant)\.axis\.(main|left|right|throttle|lever(\.\d+)?)$", RegexOptions.Compiled);
        private static readonly Regex KnobAxis = new Regex(@"\.axis\.(rotary|wheel|ministick2?)(\.|$)", RegexOptions.Compiled);
        private static readonly Regex ThrottleFunction = new Regex(@"^throttle(left|right|f1|jeffmirage21)", RegexOptions.Compiled);
        private static readonly Regex CombinedThrottleText = new Regex(@"all.engine|one axis for all|combined|both engines|single lever", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CollectiveText = new Regex(@"^collective|helicopter: collective$|\bcollective\b(?!.*throttle)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // A lever detent (TCA / Bravo / URSA airline detents, the Warthog's idle stops): pressed by moving the lever, so
        // only the lever's own functions (idle / cut-off, reverse, afterburner, autothrottle / TOGA, engine start / stop).
        private static readonly Regex DetentControl = new Regex(@"\.detent(\.|$)", RegexOptions.Compiled);
        private static readonly Regex DetentLabel = new Regex(@"(?<!\btrigger\b.*)\bdetent\b|\bIDLE\b|\bTOGA\b|\bREV\b|\bCL\b|\bMCT\b|\bFLX\b|\bCLB\b|\bCUT ?OFF\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LeverFamily = new Regex(@"afterburner|idle|cut_?off|reverse|reverser|autothrottle|atc_|toga|throttle|engine.*(start|stop)|(start|stop).*engine", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Engine switches: start / stop, the engine master (MSFS "Engine Master 1 Toggle" is filed as the engine stop), the
        // starter, magnetos, ignition, fuel cut-off.
        private static readonly Regex EngineStartStop = new Regex(@"engine.*(start|stop|master)|(start|stop|master).*engine|starter|magneto|ignition|cut_?off|fuel_?cut", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // A command that toggles (one press on, the next off) or must be held (a starter engaged while held): on one
        // position of a latched switch it gets out of step with the switch, or stays engaged all flight.
        private static readonly Regex ToggleOrHoldText = new Regex(@"toggle|start\s*/\s*stop|on\s*/\s*off|engage|\bhold\b|\bheld\b|press and hold", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static bool EngineFunction(string functionId)
        {
            return EngineStartStop.IsMatch(functionId ?? string.Empty);
        }

        internal static bool ToggleOrHold(string action, string control, string title = null)
        {
            return ToggleOrHoldText.IsMatch(action ?? string.Empty) || ToggleOrHoldText.IsMatch(control ?? string.Empty) || ToggleOrHoldText.IsMatch(title ?? string.Empty);
        }
        // (Every jettison function, the selector positions and the cover too: Auto Group and Fill all add the real
        // jettison of other aircraft to such a relation.)
        private static readonly Regex Irreversible = new Regex(@"engine.*(start|stop)|(start|stop).*engine|magneto|starter|jett|eject|canopy|apu_(start|stop)|fuel_?cut|shutdown|parachute|dragchute|hook_?release|cargo_?release", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TrimFunction = new Regex(@"^(trimnoseup|trim_nosedown|trimleft|trimright)$", RegexOptions.Compiled);

        // The fly-and-fight functions (docs/auto-setup-eval.md#hard-gates): gun, release, flares, brake button, PTT, gear,
        // flaps, trim.
        private static bool FlyFight(string id)
        {
            return !string.IsNullOrEmpty(id) && (NoLayer.Contains(id) || GearFlaps.IsMatch(id) || TrimFunction.IsMatch(id));
        }

        private static readonly Regex GearFlaps = new Regex(@"^(gear_toggle|gearin|gearout|flaps_toggle|flapsup|flapsdown)$", RegexOptions.Compiled);
        private static readonly Regex SliderAxis = new Regex(@"\.axis\.slider(\.\d+)?$", RegexOptions.Compiled);
        private static readonly Regex IdleDetent = new Regex(@"\bIDLE\b|\bOFF\b|\bCUT ?OFF\b|detent\.(idle|off|cutoff)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Never behind a shift button or a mode-switch layer: the gun, weapon release, countermeasures, the wheel brake
        // button and the push-to-talk / comm switches (a pilot reaches for them without thinking).
        private static readonly HashSet<string> NoLayer = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "trigger1ststage", "trigger2ndstage", "launchweapon", "cms_fwd_start_program_or_just_flare", "cms_aft_panic_or_just_chaff", "wheelbrakes_button",
            "comm1_up", "comm2_down", "comm3_aft_ics", "comm4_fwd_iff", "easycomms"
        };
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
            public ulong[] DirectBits;   // its own aircraft only (Bits adds every aircraft a DCS input layer serves)
            public string ShareKey = string.Empty;
            public bool Redundant;       // its aircraft already have it on another input (DropRedundant)
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
            public bool Lone;                                  // the only member of its set its aircraft have (ACE COMBAT's target switch = TMS right): any free button
            public bool AnyDirection;                          // RescueGripEssentials only: a set member may take any grip hat direction
            public bool IgnorePanelLabels;                     // RescueSurvivalAnywhere's last try only: a printed panel label, a replica key or a
                                                               // dedicated control (trim rocker) does not veto
            public bool CameraDetent;                          // trigger1ststage that is a detent before the gun (F-16, A-10C ...)
            public bool ReleaseStage;                          // trigger2ndstage that drops bombs / fires rockets (FW 190, MiG-21)
            public string ThrottleRole = string.Empty;         // twin-engine throttles: L, R or C (both / all engines); empty otherwise
            public bool SplitAllowed;                          // its game splits a twin's engines over two levers (not IL-2: all-engines axis)
            public bool HasCombined;                           // its aircraft have a both-engines throttle
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
            public List<string> RealNames = new List<string>();  // the real-HOTAS positions it stands in for (catalog "hotas", else Names)
            public string Family = string.Empty;
            public bool Grip;
            public string Phase = string.Empty;          // a position of the mode switch (a2a, a2g): a phase layer
            public string BaseKey = string.Empty;        // device|input: the normal and the phase layers of one input
            public bool Replica;                         // a cockpit-replica panel: only the functions its labels name
            public bool MfdDevice;                       // an MFD: only MFD functions
        }

        internal sealed class Run
        {
            // The positions of each switch (device|layer|switch, SwitchOf), built on first use.
            public Dictionary<string, List<Slot>> SwitchSlots;
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
            public bool GripsForPanel;   // too few panel / base buttons: panel functions may use the grips
            public bool GripsForGearFlaps;   // too few base buttons free for any function: gear and flaps may use the grips
            public HashSet<string> NotHere = new HashSet<string>(StringComparer.Ordinal);
            public Dictionary<int, Slot> DetentLever = new Dictionary<int, Slot>();
            public Dictionary<string, List<Demand>> SetMates = new Dictionary<string, List<Demand>>(StringComparer.Ordinal);   // directional set key -> its members   // demand|slot pairs a post check took back (a detent of another lever)
            public HashSet<string> TwoStage = new HashSet<string>(StringComparer.Ordinal);   // devices with a two-stage trigger
            public HashSet<string> WithTrigger = new HashSet<string>(StringComparer.Ordinal); // devices with a trigger at all
            public bool TwinLevers;      // a hand device with two throttle levers (or more)
            public bool AnyLever;        // any throttle lever
            public bool CollectiveLever; // a collective lever (a fixed-wing throttle's lever where there is no throttle)
            public bool PedalRudder;     // rudder pedals with a rudder axis
            public bool OneHat;          // the hands' devices have one grip hat at most (a gamepad, a TCA sidestick alone)
            public bool Pad;             // a gamepad flies (no joystick)
            public Dictionary<int, string> ReservedFor = new Dictionary<int, string>();   // slot -> the game whose own look-around keeps it free
            public bool PadStickThrottle;    // some aircraft of a gamepad plan has no throttle buttons: its throttle is the right stick
            public HashSet<string> PadLookAircraft = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // a gamepad's right stick looks around for them
            public HashSet<string> ThrottleButtonAircraft = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // aircraft with throttle increase / decrease buttons
            public HashSet<string> GunAircraft = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // aircraft with a gun / trigger command
            public HashSet<string> Armed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // aircraft with a gun, release or countermeasures
            public Dictionary<string, HashSet<string>> LabelOwners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
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
            string fewSets = string.Empty;
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
                    {
                        // Hat sets (TMS, DMS, CMS ...) of the top aircraft without a free hat: a modifier layer gives each
                        // hat a second use.
                        if (!FewHats(run, out fewSets, out fewAircraft))
                            break;
                        fewNeed = 0;
                    }
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
                if (fewNeed == 0 && fewSets.Length > 0)
                    plan.Notes.Insert(0, "No free hat for " + fewSets + (fewAircraft.Length > 0 ? " of " + fewAircraft : string.Empty) + ": JoyPro added " + (used - preferred).ToString(CultureInfo.InvariantCulture) +
                                         " modifier(s) although you asked for " + (preferred == 0 ? "none" : "at most " + preferred.ToString(CultureInfo.InvariantCulture)) + ".");
                else
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
            foreach (Demand demand in run.Demands.Where(d => !d.Kept && !d.Redundant && !d.IsAxis && d.Exclusive.Length > 0 && d.PhaseOk && IsTop(run, d) && SetupImportance.FrequencyRank(d.Frequency) >= 2))
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
            return run.Demands.Count(d => !d.Kept && !d.Redundant && !run.Placed.ContainsKey(d) && (SetupImportance.Essential(d.Category, d.Frequency) || d.DirKey == "snapview") && IsTop(run, d) && !d.IsAxis && CouldUseGrip(run, d));
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

        // Essential hat sets (TMS, DMS, CMS, trim ...) of the top aircraft that found no hat while the hands' devices have hats:
        // another modifier layer would give them one.
        private static bool FewHats(Run run, out string sets, out string aircraft)
        {
            sets = string.Empty;
            aircraft = string.Empty;
            // (Looking around without head tracking counts: the snap views need a hat too.)
            List<Demand> missing = run.Demands.Where(d => !d.Kept && !d.Redundant && d.DirRole.Length > 0 && !run.Placed.ContainsKey(d) && IsTop(run, d) &&
                                                          (SetupImportance.Essential(d.Category, d.Frequency) || d.DirKey == "snapview") && run.Slots.Any(s => s.Layer == 0 && s.Grip && HatControl.IsMatch(s.RuleControl) && d.Reachable.Contains(s.Device.Device))).ToList();
            if (missing.Count == 0)
                return false;
            sets = string.Join(", ", missing.Select(d => d.DirKey.ToUpperInvariant()).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal));
            Dictionary<string, int> perAircraft = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Demand demand in missing)
            {
                foreach (string plane in demand.Aircraft)
                    perAircraft[plane] = (perAircraft.ContainsKey(plane) ? perAircraft[plane] : 0) + 1;
            }
            aircraft = perAircraft.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key).FirstOrDefault() ?? string.Empty;
            return true;
        }

        // A function its aircraft already have on another input gives its input back: an axis of the same kind and side
        // (the F/A-18's third throttle axis next to its two levers, the inverted toe brake next to the normal one) and a
        // toggle whose positions are already there (gear toggle next to gear up and down on a switch). Returns the function
        // that does it, or null.
        private static Demand RedundantBy(Run run, Demand demand)
        {
            SetupFunction f = demand.Function;
            if (f == null || f.Id.Length == 0 || demand.Aircraft.Count == 0)
                return null;
            // Roll / pitch / yaw buttons (Elite's roll left / right) are not needed where the axis is placed for every aircraft.
            Match axisButton = Regex.Match(f.Id, "^(roll|pitch|yaw)_[a-z]+_button$");
            string axisWord = axisButton.Success ? axisButton.Groups[1].Value : (f.MapFunction == "roll" || f.MapFunction == "pitch" || f.MapFunction == "yaw") && !demand.IsAxis ? f.MapFunction : string.Empty;
            if (!demand.IsAxis && axisWord.Length > 0)
            {
                List<Demand> axes = run.Placed.Keys.Where(o => o.IsAxis && o.Function != null &&
                                                               (o.Function.MapFunction == axisWord || o.Function.Id == axisWord || (axisWord == "yaw" && o.Function.Id == "rudder"))).ToList();
                if (axes.Count > 0 && demand.Aircraft.All(plane => axes.Any(o => o.Aircraft.Contains(plane, StringComparer.OrdinalIgnoreCase))))
                    return axes.OrderByDescending(o => o.Importance).First();
                return null;
            }
            // Throttle increase / decrease buttons are not needed where a throttle axis is placed for the aircraft.
            if (!demand.IsAxis && (f.Id == "throttle_up_button" || f.Id == "throttle_down_button"))
            {
                List<Demand> axes = run.Placed.Keys.Where(o => o.IsAxis && ThrottleFunction.IsMatch(o.FunctionId)).ToList();
                if (axes.Count > 0 && demand.Aircraft.All(plane => axes.Any(o => o.Aircraft.Contains(plane, StringComparer.OrdinalIgnoreCase) || SharedLayer(run, o, plane))))
                    return axes.OrderByDescending(o => o.Importance).First();
                return null;
            }
            // Snap views are not needed where both look axes are on centring axes (a gamepad's right stick).
            if (!demand.IsAxis && f.Id.StartsWith("view_snap_", StringComparison.Ordinal))
            {
                List<Demand> axes = run.Placed.Where(p => p.Key.IsAxis && (p.Key.FunctionId == "view_look_horizontal" || p.Key.FunctionId == "view_look_vertical") && (CentringAxis(p.Value.Control) || CentringAxis(p.Value.Input))).Select(p => p.Key).ToList();
                if (demand.Aircraft.All(plane => new[] { "view_look_horizontal", "view_look_vertical" }.All(id => axes.Any(o => o.FunctionId == id && (o.Aircraft.Contains(plane, StringComparer.OrdinalIgnoreCase) || SharedLayer(run, o, plane))))))
                    return axes.OrderByDescending(o => o.Importance).FirstOrDefault();
                return null;
            }
            // The wheel brake button is not needed where toe brakes are on pedals or a brake lever.
            if (!demand.IsAxis && string.Equals(f.Id, "wheelbrakes_button", StringComparison.OrdinalIgnoreCase))
            {
                List<Demand> toes = run.Placed.Where(p => p.Key.IsAxis && p.Key.Function != null && ToeBrake.IsMatch(p.Key.Function.Id)).Select(p => p.Key).ToList();
                if (toes.Count > 0 && demand.Aircraft.All(plane => toes.Any(t => t.Aircraft.Contains(plane, StringComparer.OrdinalIgnoreCase) || SharedLayer(run, t, plane))))
                    return toes.OrderByDescending(t => t.Importance).First();
                return null;
            }
            // A gear / flaps toggle is not needed where both directions are placed (flaps up and down on the FLAPS switch).
            if (!demand.IsAxis && (f.Id == "gear_toggle" || f.Id == "flaps_toggle"))
            {
                string[] both = f.Id == "gear_toggle" ? new[] { "gearin", "gearout" } : new[] { "flapsup", "flapsdown" };
                List<Demand> placed = run.Placed.Keys.Where(o => !o.Redundant && both.Contains(o.FunctionId)).ToList();
                if (demand.Aircraft.All(plane => both.All(id => placed.Any(o => o.FunctionId == id && (o.Aircraft.Contains(plane, StringComparer.OrdinalIgnoreCase) || SharedLayer(run, o, plane))))))
                    return placed.OrderByDescending(o => o.Importance).FirstOrDefault();
                return null;
            }
            // TDC / cursor slew buttons are not needed where the slew axes are placed (the thumb stick): the hat they would
            // take goes to an essential (trim, looking around) instead.
            Match slew = Regex.Match(f.Id, "^tdc_slew_(up|down|left|right)$");
            if (!demand.IsAxis && slew.Success)
            {
                bool vertical = slew.Groups[1].Value == "up" || slew.Groups[1].Value == "down";
                List<Demand> axes = run.Placed.Keys.Where(o => o.IsAxis && o.Function != null && !o.Redundant &&
                                                               (vertical ? o.Function.Id.StartsWith("tdc_vertical", StringComparison.Ordinal) : o.Function.Id == "tdc_horizontal")).ToList();
                if (axes.Count > 0 && demand.Aircraft.All(plane => axes.Any(o => o.Aircraft.Contains(plane, StringComparer.OrdinalIgnoreCase) || SharedLayer(run, o, plane))))
                    return axes.OrderByDescending(o => o.Importance).First();
                return null;
            }
            if (f.MapFunction.Length == 0 || f.MapDir.IndexOf('@') >= 0 || f.Group.Length > 0)
                return null;
            bool toggle = !demand.IsAxis && f.MapDir == "toggle";
            if (!demand.IsAxis && !toggle)
                return null;
            List<Demand> others = run.Placed.Keys.Where(o => o != demand && !o.Redundant && o.IsAxis == demand.IsAxis && o.Function != null &&
                                                              string.Equals(o.Function.MapFunction, f.MapFunction, StringComparison.OrdinalIgnoreCase) && o.Function.MapDir.IndexOf('@') < 0 &&
                                                              (toggle ? o.Function.MapDir.Length > 0 && o.Function.MapDir != "toggle" : o.Function.MapDir == f.MapDir)).ToList();
            if (others.Count == 0)
                return null;
            Demand by = null;
            foreach (string plane in demand.Aircraft)
            {
                SetupAircraft known;
                bool layer = run.Request.Aircraft.TryGetValue(plane, out known) && known.SharedLayer;
                List<Demand> covering = others.Where(o => layer || o.Aircraft.Contains(plane, StringComparer.OrdinalIgnoreCase)).ToList();
                if (toggle ? covering.Select(o => o.Function.MapDir).Distinct(StringComparer.Ordinal).Count() < 2 : covering.Count == 0)
                    return null;
                by = by ?? covering.OrderByDescending(o => o.Importance).First();
            }
            return by;
        }

        // The quality's essential count only (placement does not use it): an essential the aircraft can still do another
        // way is no gap. Dropped as redundant (the gear toggle next to gear up and down); an axis whose function is placed
        // in another form (the wheel brake axes next to the "Wheel brakes" button, the engine 2 throttle next to the one
        // lever IL-2 moves all engines with); an axis named N next to placed buttons named "N: ..." (the trim axis
        // "Elevator trim" next to "Elevator trim: nose up / down").
        private static bool EssentialCoveredAnotherWay(Run run, Demand demand, string plane)
        {
            if (demand.Redundant)
                return true;
            if (!demand.IsAxis)
                return false;
            Func<Demand, bool> mine = o => o != demand && (o.Aircraft.Contains(plane, StringComparer.OrdinalIgnoreCase) || SharedLayer(run, o, plane));
            string map = demand.Function == null ? string.Empty : demand.Function.MapFunction;
            if (map.Length > 0 && run.Placed.Keys.Any(o => mine(o) && o.Function != null && string.Equals(o.Function.MapFunction, map, StringComparison.OrdinalIgnoreCase)))
                return true;
            string prefix = demand.Name.Trim() + ":";
            return demand.Name.Trim().Length > 0 &&
                   run.Placed.Keys.Count(o => mine(o) && !o.IsAxis && o.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) >= 2;
        }

        // A demand of a DCS input layer (Default ...) that the aircraft shares.
        private static bool SharedLayer(Run run, Demand demand, string plane)
        {
            SetupAircraft known;
            return run.Request.Aircraft.TryGetValue(plane, out known) && known.SharedLayer && demand.Aircraft.Count > 0;
        }

        // Drops the functions the aircraft already have another way (RedundantBy), least important first.
        private static bool DropRedundant(Run run)
        {
            bool any = false;
            // (the unplaced ones too: a gear / flaps toggle or a slew button that found no input now would take the input
            // the next pass frees for an essential)
            foreach (Demand demand in run.Demands.Where(d => !d.Kept && !d.Redundant && d.StyleDevice.Length == 0).OrderBy(d => d.Importance).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList())
            {
                Demand by = RedundantBy(run, demand);
                if (by == null)
                    continue;
                if (run.Placed.ContainsKey(demand))
                    Unplace(run, demand);
                demand.Redundant = true;
                demand.Reason = "the aircraft already have it on another input (" + by.Name + ")";
                any = true;
            }
            return any;
        }

        // A lever detent (Bravo "Lever 3 detent", TCA "lever 1 detent 2", "THR 1 IDLE") belongs to its own lever: its
        // throttle functions (afterburner, TOGA, reverse, A/T disconnect) only where that lever carries the aircraft's
        // throttle, not on the mixture or prop lever's detent. Returns whether any function left a detent.
        private static readonly Regex DetentLever = new Regex(@"\b(?:lever|thr)\s*(\d)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The lever axis of a numbered lever detent (Bravo "Lever 3 detent", TCA "lever 1 detent 2", "THR 1 IDLE"), or null.
        private static Slot LeverOfDetent(Run run, Slot slot)
        {
            Slot cached;
            if (run.DetentLever.TryGetValue(slot.Index, out cached))
                return cached;
            Slot axis = null;
            string label = slot.Input.Label ?? string.Empty;
            bool detent = !slot.Input.IsAxis && (DetentControl.IsMatch(slot.Control) || DetentLabel.IsMatch(label) || string.Equals(slot.Input.Type, "detent", StringComparison.OrdinalIgnoreCase));
            Match lever = detent ? DetentLever.Match(label) : Match.Empty;
            if (lever.Success)
            {
                string n = lever.Groups[1].Value;
                axis = run.Slots.FirstOrDefault(s => s.Layer == 0 && s.Input.IsAxis && s.Device == slot.Device &&
                                                     (s.Control.EndsWith(".lever." + n, StringComparison.Ordinal) || Regex.IsMatch(s.Input.Label ?? string.Empty, @"\b(?:lever|thr|throttle)\s*" + n + @"\b", RegexOptions.IgnoreCase)));
            }
            run.DetentLever[slot.Index] = axis;
            return axis;
        }

        private static bool DetentsOfThrottleLevers(Run run)
        {
            bool any = false;
            foreach (KeyValuePair<Demand, Slot> pair in run.Placed.Where(p => !p.Key.IsAxis && !p.Key.Kept && !HasWish(p.Key)).ToList())
            {
                Slot slot = pair.Value;
                string label = slot.Input.Label ?? string.Empty;
                bool detent = DetentControl.IsMatch(slot.Control) || DetentLabel.IsMatch(label) || string.Equals(slot.Input.Type, "detent", StringComparison.OrdinalIgnoreCase);
                Match lever = DetentLever.Match(label);
                if (!detent || !lever.Success)
                    continue;
                string n = lever.Groups[1].Value;
                Slot axis = run.Slots.FirstOrDefault(s => s.Layer == 0 && s.Input.IsAxis && s.Device == slot.Device &&
                                                          (s.Control.EndsWith(".lever." + n, StringComparison.Ordinal) || Regex.IsMatch(s.Input.Label ?? string.Empty, @"\b(?:lever|thr|throttle)\s*" + n + @"\b", RegexOptions.IgnoreCase)));
                if (axis == null)
                    continue;
                bool own = run.Occupants[axis.Index].Any(o => o.IsAxis && ThrottleFunction.IsMatch(o.FunctionId) && Overlaps(o.Bits, pair.Key.Bits));
                if (own)
                    continue;
                Unplace(run, pair.Key);
                run.NotHere.Add(pair.Key.Index.ToString(CultureInfo.InvariantCulture) + "|" + slot.Index.ToString(CultureInfo.InvariantCulture));
                pair.Key.Reason = "a lever detent belongs to its own lever (" + label + "), which does not carry the throttle";
                any = true;
            }
            return any;
        }

        // The two positions of an on / off pair (gear up / down, flaps up / down) on push buttons belong together: on one
        // device, the same layer. A pair split over two devices (gear up on the stick base, gear down on the throttle) moves
        // to the best two free buttons of one device.
        private static void RejoinPairs(Run run)
        {
            List<Demand> placed = run.Placed.Keys.Where(d => !d.IsAxis && !d.Kept && d.Function != null && d.Function.PairGroup.Length > 0 && !HasWish(d)).ToList();
            foreach (IGrouping<string, Demand> group in placed.GroupBy(d => d.Function.PairGroup, StringComparer.Ordinal))
            {
                List<IGrouping<string, Demand>> roles = group.GroupBy(d => d.Function.PairRole, StringComparer.Ordinal).ToList();
                if (roles.Count != 2)
                    continue;
                foreach (Demand a in roles[0])
                {
                    Demand b = roles[1].FirstOrDefault(x => Overlaps(a.Bits, x.Bits) && run.Placed.ContainsKey(x));
                    Slot sa, sb;
                    if (b == null || !run.Placed.TryGetValue(a, out sa) || !run.Placed.TryGetValue(b, out sb))
                        continue;
                    if (sa.Device.Device == sb.Device.Device && sa.Layer == sb.Layer && sa.Grip == sb.Grip)
                        continue;
                    double now = S(run, a, sa) + S(run, b, sb);
                    Tuple<Slot, Slot> best = null;
                    double top = Forbidden;
                    foreach (IGrouping<string, Slot> device in run.Slots.Where(s => !s.Input.IsAxis).GroupBy(s => s.Device.Device + "|" + s.Layer.ToString(CultureInfo.InvariantCulture) + "|" + s.Grip, StringComparer.Ordinal))
                    {
                        List<Slot> forA = device.Where(s => (s == sa || Free(run, s, a, b)) && S(run, a, s) > Forbidden).OrderByDescending(s => S(run, a, s)).Take(6).ToList();
                        List<Slot> forB = device.Where(s => (s == sb || Free(run, s, b, a)) && S(run, b, s) > Forbidden).OrderByDescending(s => S(run, b, s)).Take(6).ToList();
                        foreach (Slot x in forA)
                        {
                            foreach (Slot y in forB.Where(y => y != x && y.BaseKey != x.BaseKey))
                            {
                                double value = S(run, a, x) + S(run, b, y);
                                if (value > top + Eps)
                                {
                                    top = value;
                                    best = Tuple.Create(x, y);
                                }
                            }
                        }
                    }
                    // (only when keeping them together costs at most half of their score)
                    if (best == null || top < now - 0.5 * Math.Abs(now))
                        continue;
                    Unplace(run, a);
                    Unplace(run, b);
                    if (Free(run, best.Item1, a, null) && Free(run, best.Item2, b, null))
                    {
                        Place(run, a, best.Item1, "with its other position on one device");
                        Place(run, b, best.Item2, "with its other position on one device");
                        a.Fixed = b.Fixed = true;
                    }
                    else
                    {
                        Place(run, a, sa, null);
                        Place(run, b, sb, null);
                    }
                }
            }
        }

        // The snap-view sets (per game) that found no hat take one from less important single functions: every occupant of
        // the hat's four directions (on the normal layer) is neither kept, fixed (a pair, a group, the user's own) nor more
        // important than the set's members. The hat whose occupants weigh least is taken. Returns whether any set moved.
        private static bool EssentialSets(Run run)
        {
            bool any = false;
            // (every directional set - trim, CMS, snap views ... - by its weakest member, the most important first: a single
            // hat that a less important set took by its name, the TMS on an Extreme 3D's only hat, goes to the trim)
            foreach (List<Demand> set in run.Demands.Where(d => d.DirKey.Length > 0 && !d.Kept && !d.Redundant && d.DirRole.Length > 0 && d.DirRole != "press" && !d.IsAxis)
                         .GroupBy(d => d.DirKey + "|" + d.HandKey + "|" + Prefix(d.RealKey.Split('+')[0]) + "|" + (d.DirKey == "snapview" ? SnapKey(run, d) : string.Empty), StringComparer.Ordinal)
                         .Select(g => g.ToList()).Where(g => g.Count >= 2 && (g.All(d => !run.Placed.ContainsKey(d)) || ShiftedSnaps(run, g))).OrderByDescending(g => g.Min(d => d.Importance)).ToList())
            {
                // (snap views a run left behind a shift button look for an unshifted hat again)
                bool shifted = ShiftedSnaps(run, set);
                double floor = set.Min(d => d.Importance);
                List<Tuple<Demand, Slot>> best = null;
                double bestCost = double.MaxValue;
                // (the snap views of a small device that found no free hat: a hat behind a shift button, slow but far better
                // than no looking around at all)
                bool snaps = set.All(d => d.DirKey == "snapview");
                foreach (IGrouping<string, Slot> hat in run.Slots.Where(s => s.Phase.Length == 0 && s.Grip && !s.Input.IsAxis && HatControl.IsMatch(s.RuleControl) && GroupRoles.Contains(LastSegment(s.RuleControl)) &&
                                                                 (s.Layer == 0 || (snaps && !shifted)))
                             .GroupBy(s => s.Device.Device + "|" + s.Layer.ToString(CultureInfo.InvariantCulture) + "|" + Prefix(s.RuleControl), StringComparer.Ordinal)
                             .OrderBy(h => h.First().Layer))
                {
                    // (an unshifted hat always before a shifted one)
                    if (best != null && hat.First().Layer > 0 && best[0].Item2.Layer == 0)
                        break;
                    List<Tuple<Demand, Slot>> placement = new List<Tuple<Demand, Slot>>();
                    double cost = 0;
                    bool ok = true;
                    HashSet<Demand> evicted = new HashSet<Demand>();
                    foreach (Demand member in set)
                    {
                        Slot slot = hat.FirstOrDefault(s => LastSegment(s.RuleControl) == member.DirRole);
                        if (slot == null || S(run, member, slot) <= Forbidden)
                        {
                            ok = false;
                            break;
                        }
                        foreach (Demand other in run.Occupants[slot.Index].Where(o => Conflict(o, member)))
                        {
                            // (never the user's own, a kept bind, a wish or a pair; a set that is less important leaves the hat
                            // as a whole)
                            if (!MayEvict(run, member, other, floor, slot))
                            {
                                ok = false;
                                break;
                            }
                            if (evicted.Add(other))
                                cost += other.Importance;
                            if (other.DirKey.Length > 0)
                            {
                                foreach (Demand mate in run.Placed.Where(p => p.Key != other && p.Key.DirKey == other.DirKey && p.Value.Device == slot.Device && Prefix(p.Value.RuleControl) == Prefix(slot.RuleControl) && p.Value.Layer == slot.Layer).Select(p => p.Key).ToList())
                                {
                                    if (!MayEvict(run, member, mate, floor, run.Placed[mate]) && !(mate.Fixed && mate.DirKey == other.DirKey && MayEvict(run, member, other, floor, slot)))
                                    {
                                        ok = false;
                                        break;
                                    }
                                    if (evicted.Add(mate))
                                        cost += mate.Importance;
                                }
                                if (!ok)
                                    break;
                            }
                        }
                        if (!ok)
                            break;
                        placement.Add(Tuple.Create(member, slot));
                    }
                    if (ok && placement.Count == set.Count && cost < bestCost)
                    {
                        bestCost = cost;
                        best = placement;
                    }
                }
                if (best == null)
                    continue;
                Dictionary<Demand, Slot> before = new Dictionary<Demand, Slot>();
                if (shifted)
                {
                    foreach (Demand member in set)
                    {
                        before[member] = run.Placed[member];
                        Unplace(run, member);
                    }
                }
                HashSet<Demand> leaving = new HashSet<Demand>();
                foreach (Tuple<Demand, Slot> item in best)
                {
                    foreach (Demand other in run.Occupants[item.Item2.Index].Where(o => Conflict(o, item.Item1)).ToList())
                    {
                        leaving.Add(other);
                        if (other.DirKey.Length > 0)
                            foreach (Demand mate in run.Placed.Where(p => p.Key.DirKey == other.DirKey && p.Value.Device == item.Item2.Device && Prefix(p.Value.RuleControl) == Prefix(item.Item2.RuleControl) && p.Value.Layer == item.Item2.Layer).Select(p => p.Key))
                                leaving.Add(mate);
                    }
                }
                // (what leaves is put back when the set cannot go there after all - the CMS left / right of a Foxtrot left
                // the trim off the POV hat, did not fit either, and both were lost)
                Dictionary<Demand, Tuple<Slot, bool, string>> left = leaving.Where(run.Placed.ContainsKey).ToDictionary(d => d, d => Tuple.Create(run.Placed[d], d.Fixed, d.Reason));
                foreach (Demand other in leaving)
                {
                    Unplace(run, other);
                    other.Fixed = false;
                }
                if (!best.All(item => Free(run, item.Item2, item.Item1, null)))
                {
                    foreach (KeyValuePair<Demand, Slot> back in before)
                        Place(run, back.Key, back.Value, null);
                    foreach (KeyValuePair<Demand, Tuple<Slot, bool, string>> back in left)
                    {
                        Place(run, back.Key, back.Value.Item1, back.Value.Item3);
                        back.Key.Fixed = back.Value.Item2;
                    }
                    continue;
                }
                foreach (Tuple<Demand, Slot> item in best)
                {
                    Place(run, item.Item1, item.Item2, item.Item2.Layer > 0 ? "the whole set on one hat behind " + item.Item2.Modifier.Name + " (no hat free without a modifier: slower, but looking around stays)" : "the whole set on one hat (it outranks what was there)");
                    item.Item1.Fixed = true;
                }
                any = true;
            }
            return any;
        }

        // Every aircraft of a throttle demand has throttle increase / decrease buttons (a DCS input layer's throttle: when
        // some aircraft of the plan have them).
        private static bool ThrottleButtons(Run run, Demand demand)
        {
            List<string> own = demand.Aircraft.Where(p => !SharedLayer(run, demand, p)).ToList();
            return own.Count > 0 ? own.All(run.ThrottleButtonAircraft.Contains) : run.ThrottleButtonAircraft.Count > 0;
        }

        // A gamepad whose game has throttle increase / decrease buttons: they go on the D-pad's up and down (the right
        // stick looks around), before anything else takes the D-pad.
        private static void PadThrottleButtons(Run run, List<Demand> open)
        {
            if (!run.Pad)
                return;
            foreach (Demand demand in open.Where(d => !run.Placed.ContainsKey(d) && (d.FunctionId == "throttle_up_button" || d.FunctionId == "throttle_down_button")).OrderByDescending(d => d.Importance).ToList())
            {
                string direction = demand.FunctionId == "throttle_up_button" ? "up" : "down";
                Slot slot = run.Slots.FirstOrDefault(s => s.Layer == 0 && string.Equals(s.Device.Kind, "gamepad", StringComparison.OrdinalIgnoreCase) && s.Control == "stick.hat.1." + direction &&
                                                          S(run, demand, s) > Forbidden && Free(run, s, demand, null));
                if (slot == null)
                    continue;
                Place(run, demand, slot, "a gamepad: throttle up / down on the D-pad, the right stick looks around");
                demand.Fixed = true;
            }
        }

        // A gamepad's right stick looks around for the aircraft that have look axes and throttle buttons: nothing else
        // goes there for them.
        private static bool PadLookStick(Run run, Demand demand, Slot slot)
        {
            if (!run.Pad || !demand.IsAxis || !string.Equals(slot.Device.Kind, "gamepad", StringComparison.OrdinalIgnoreCase) || !slot.Control.StartsWith("throttle.axis.ministick.", StringComparison.Ordinal))
                return false;
            List<string> own = demand.Aircraft.Where(p => !SharedLayer(run, demand, p)).ToList();
            bool look = demand.FunctionId == "view_look_horizontal" || demand.FunctionId == "view_look_vertical";
            // (an aircraft without throttle buttons keeps the right stick's up / down as its throttle)
            if (slot.Control == "throttle.axis.ministick.y" && !ThrottleFunction.IsMatch(demand.FunctionId) && demand.FunctionId != "space_strafe_fwdaft" &&
                (own.Count > 0 ? own.Any(p => !run.ThrottleButtonAircraft.Contains(p)) : run.PadStickThrottle))
                return true;
            if (look || run.Options.HeadTracking)
                return false;
            // (review 6 M2: a throttle demand that holds an aircraft without throttle buttons - the F-15C among FC3 modules
            // that have them - keeps the right stick's up / down: a throttle beats looking up and down)
            if (slot.Control == "throttle.axis.ministick.y" && (ThrottleFunction.IsMatch(demand.FunctionId) || demand.FunctionId == "space_strafe_fwdaft") && own.Any(p => !run.ThrottleButtonAircraft.Contains(p)))
                return false;
            return own.Count > 0 ? own.Any(run.PadLookAircraft.Contains) : run.PadLookAircraft.Count > 0;
        }

        // The games of the plan that support head tracking but whose look-around commands JoyPro does not manage (no
        // look command in the catalog for them): their own default look-around needs a free grip hat.
        private static List<string> CameraGames(Run run)
        {
            if (run.Options.HeadTracking)
                return new List<string>();
            return (run.Options.Games ?? new List<string>()).Where(g => !string.Equals(g, "DCS", StringComparison.OrdinalIgnoreCase) && HeadTrackingGame(g) &&
                                                                       run.Request.Commands.Any(c => string.Equals(c.Game, g, StringComparison.OrdinalIgnoreCase)) &&
                                                                       !run.Request.Commands.Any(c => string.Equals(c.Game, g, StringComparison.OrdinalIgnoreCase) && LookFunction.IsMatch(c.Function))).ToList();
        }

        // Games on the virtual Xbox pad whose looking around is the pad's right stick (look axes in the catalog, no snap
        // views) and found no thumb stick for it: a free hat becomes the right stick there (PadAutoSetup "Hat to axes").
        private static List<string> PadLookGames(Run run)
        {
            return (run.Options.Games ?? new List<string>()).Where(g => !HeadTrackingGame(g) && !string.Equals(g, "DCS", StringComparison.OrdinalIgnoreCase) &&
                                                                       run.Request.Commands.Any(c => string.Equals(c.Game, g, StringComparison.OrdinalIgnoreCase) && (c.Function == "view_look_horizontal" || c.Function == "view_look_vertical")) &&
                                                                       !run.Request.Commands.Any(c => string.Equals(c.Game, g, StringComparison.OrdinalIgnoreCase) && c.Function.StartsWith("view_snap_", StringComparison.Ordinal)) &&
                                                                       !run.Placed.Keys.Any(d => d.IsAxis && (d.FunctionId == "view_look_horizontal" || d.FunctionId == "view_look_vertical") && d.Nodes.Any(n => string.Equals(n.Game, g, StringComparison.OrdinalIgnoreCase))))
                .ToList();
        }

        // Games whose looking around is axes only (no snap views in the catalog) with a look axis of a top demand unplaced.
        internal static List<string> LookAxesGames(Run run)
        {
            return (run.Options.Games ?? new List<string>()).Where(g => !string.Equals(g, "DCS", StringComparison.OrdinalIgnoreCase) &&
                                                                       !run.Request.Commands.Any(c => string.Equals(c.Game, g, StringComparison.OrdinalIgnoreCase) && c.Function.StartsWith("view_snap_", StringComparison.Ordinal)) &&
                                                                       run.Demands.Any(d => d.IsAxis && !d.Kept && !d.Redundant && (d.FunctionId == "view_look_horizontal" || d.FunctionId == "view_look_vertical") &&
                                                                                            d.Nodes.Any(n => string.Equals(n.Game, g, StringComparison.OrdinalIgnoreCase)) && IsTop(run, d) && !run.Placed.ContainsKey(d)))
                .ToList();
        }

        // A four-way grip hat of the hands' devices with nothing of the game on it (unshifted), or empty.
        private static string FreeHatFor(Run run, string game)
        {
            foreach (IGrouping<string, Slot> hat in run.Slots.Where(s => s.Layer == 0 && s.Grip && !s.Input.IsAxis && HatControl.IsMatch(s.RuleControl) && GroupRoles.Contains(LastSegment(s.RuleControl)))
                         .GroupBy(s => s.Device.Device + "|" + Prefix(s.RuleControl), StringComparer.Ordinal).Where(h => h.Count() >= 4)
                         .OrderBy(h => h.First().Role == SetupRoles.MainStick ? 0 : 1).ThenBy(h => h.Key, StringComparer.Ordinal))
            {
                if (hat.All(s => !run.Occupants[s.Index].Any(o => o.Nodes.Any(n => string.Equals(n.Game, game, StringComparison.OrdinalIgnoreCase)))))
                    return SetupRoles.Short(hat.First().Device) + " " + Prefix(hat.First().RuleControl);
            }
            return string.Empty;
        }

        // For each such game: when the hands' devices have two grip hats or more and none is free of the game's functions,
        // the hat whose occupants weigh least (no trim, no essential set of the fight) is cleared for the game's own
        // look-around. Returns whether anything moved.
        private static bool ReserveGameLookHat(Run run)
        {
            bool any = false;
            List<string> camera = CameraGames(run);
            foreach (string game in camera.Concat(PadLookGames(run)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                // (a game on the virtual Xbox pad turns a free hat into the pad's right stick)
                bool pad = !camera.Contains(game, StringComparer.OrdinalIgnoreCase);
                List<IGrouping<string, Slot>> hats = run.Slots.Where(s => s.Layer == 0 && s.Grip && !s.Input.IsAxis && HatControl.IsMatch(s.RuleControl) && GroupRoles.Contains(LastSegment(s.RuleControl)) &&
                                                                          (s.Role == SetupRoles.MainStick || s.Role == SetupRoles.Throttle || s.Role == SetupRoles.HeliStick || s.Role == SetupRoles.Collective || s.Role == SetupRoles.LeftStick))
                    .GroupBy(s => s.Device.Device + "|" + Prefix(s.RuleControl), StringComparer.Ordinal).Where(h => h.Count() >= 4).ToList();
                // (one hat stays with the essentials: on a one-hat stick the target switch, flares and weapon switch need it more)
                if (hats.Count < 2)
                    continue;
                Func<Demand, bool> mine = d => d.Nodes.Any(n => string.Equals(n.Game, game, StringComparison.OrdinalIgnoreCase));
                // (the occupants of a hat on every layer: the game's look-around is held without a modifier)
                Func<IGrouping<string, Slot>, List<Demand>> occupants = h => run.Slots.Where(s => s.Device.Device + "|" + Prefix(s.RuleControl) == h.Key && !s.Input.IsAxis && s.Layer == 0)
                    .SelectMany(s => run.Occupants[s.Index]).Where(mine).Distinct().ToList();
                if (hats.Any(h => occupants(h).Count == 0))
                    continue;
                // (a set's lone member - ACE COMBAT's flares, target, weapon switch - takes any free button instead)
                IGrouping<string, Slot> best = hats.Where(h => occupants(h).All(d => !d.Kept && !HasWish(d) && (EssentialRank(d.FunctionId) > EssentialRank("target_management_system_up") || (pad && d.Lone))))
                    .OrderBy(h => h.First().Role == SetupRoles.MainStick ? 0 : 1).ThenBy(h => occupants(h).Sum(d => d.Importance)).ThenBy(h => h.Key, StringComparer.Ordinal).FirstOrDefault();
                if (best == null)
                    continue;
                foreach (Slot slot in run.Slots.Where(s => s.Device.Device + "|" + Prefix(s.RuleControl) == best.Key && s.Layer == 0))
                    run.ReservedFor[slot.Index] = game;
                // (a directional set leaves as a whole: its members never end up on two hats)
                List<Demand> leaving = occupants(best);
                foreach (Demand member in leaving.Where(d => d.DirKey.Length > 0).ToList())
                    leaving.AddRange(run.Placed.Keys.Where(o => o.DirKey == member.DirKey && Overlaps(o.Bits, member.Bits) && !leaving.Contains(o)).ToList());
                foreach (Demand demand in leaving.Distinct().ToList())
                {
                    Unplace(run, demand);
                    demand.Fixed = false;
                    demand.Reason = "the hat stays free for " + game + "'s own look-around (JoyPro does not manage its camera)";
                    any = true;
                }
            }
            return any;
        }

        // A gear toggle of a top aircraft that found no input (and no gear up / down pair for its aircraft) takes the input of a
        // less important function that is no in-flight essential. Returns whether anything moved.
        private static bool RescueGear(Run run)
        {
            bool any = false;
            // (and a lone member of an essential set - ACE COMBAT's target switch - that found no button)
            // (and gear up / down or flaps up / down that a real position of a function that is no essential left without a
            // place: the far end of "Where should functions go?")
            // (and with head tracking the recenter of a head-tracking sim: the tracker needs it - review 5 m2)
            foreach (Demand gear in run.Demands.Where(d => !d.Kept && !d.Redundant && (d.FunctionId == "gear_toggle" || Regex.IsMatch(d.FunctionId, "^(gearin|gearout|flapsup|flapsdown)$") || (d.Lone && !d.IsAxis && EssentialRank(d.FunctionId) < 99) ||
                                                                                      (d.FunctionId == "view_center" && (!run.Options.HeadTracking || d.Nodes.Any(n => HeadTrackingGame(n.Game)))) ||
                                                                                      // (owner 2026-11-16: zoom is a look essential with and without head tracking - the zoom-in
                                                                                      // button where no zoom axis is placed for the aircraft)
                                                                                      (d.FunctionId == "fov_toggle_china_hat_fwd" && !ZoomAxisPlaced(run, d))) &&
                                                         !run.Placed.ContainsKey(d) && IsTop(run, d)).OrderByDescending(d => d.Importance).ToList())
            {
                if (gear.FunctionId == "gear_toggle" && gear.Aircraft.All(p => run.Placed.Keys.Count(o => (o.FunctionId == "gearin" || o.FunctionId == "gearout") && o.Aircraft.Contains(p, StringComparer.OrdinalIgnoreCase)) >= 2))
                    continue;
                Slot best = null;
                double cost = double.MaxValue;
                foreach (Slot slot in run.Slots.Where(s => !s.Input.IsAxis && S(run, gear, s) > Forbidden))
                {
                    // (not a hat kept free for a game's own look-around, not where a check took it back)
                    string reserved;
                    if ((run.ReservedFor.TryGetValue(slot.Index, out reserved) && gear.Nodes.Any(n => string.Equals(n.Game, reserved, StringComparison.OrdinalIgnoreCase))) ||
                        run.NotHere.Contains(gear.Index.ToString(CultureInfo.InvariantCulture) + "|" + slot.Index.ToString(CultureInfo.InvariantCulture)))
                        continue;
                    List<Demand> others = run.Occupants[slot.Index].Where(o => Conflict(o, gear)).ToList();
                    // (review 6 M1: the head-tracking recenter may take a shifted slot from any function that is no in-flight
                    // essential, a non-essential pair or group too - the F-14's wing sweep, the parking brake - which then
                    // leaves whole; a 20-module hangar on a T.Flight or a gamepad had no other slot left)
                    bool recenter = gear.FunctionId == "view_center";
                    if (others.Any(o => o.Kept || (o.Fixed && (o.StyleDevice.Length > 0 || (!(recenter && slot.Layer > 0) && (o.Function.PairGroup.Length > 0 || o.Function.Group.Length > 0)))) || HasWish(o) || (o.DirKey.Length > 0 && GroupRoles.Contains(LastSegment(slot.RuleControl))) || EssentialRank(o.FunctionId) < 99 || (!recenter && o.Importance >= gear.Importance) || o.FunctionId.StartsWith("gear", StringComparison.Ordinal) ||
                                        // (a spaceship's nearest hostile the grip rescue placed is survival-critical: no recenter or lone member takes it)
                                        (NearestHostile(o) && o.Fixed)))
                        continue;
                    double c = others.Sum(o => o.Importance) - 0.01 * S(run, gear, slot) + (recenter && slot.Layer == 0 ? 1000 : 0);
                    if (c < cost)
                    {
                        cost = c;
                        best = slot;
                    }
                }
                if (best == null)
                    continue;
                foreach (Demand other in run.Occupants[best.Index].Where(o => Conflict(o, gear)).ToList())
                {
                    Unplace(run, other);
                    run.NotHere.Add(other.Index.ToString(CultureInfo.InvariantCulture) + "|" + best.Index.ToString(CultureInfo.InvariantCulture));
                    // (a pair or group leaves whole: its mates are placed again with it or not at all)
                    string together = other.Function.PairGroup.Length > 0 ? other.Function.PairGroup : other.Function.Group;
                    if (together.Length > 0 && gear.FunctionId == "view_center")
                    {
                        foreach (Demand mate in run.Placed.Keys.Where(m => m != other && (m.Function.PairGroup == together || m.Function.Group == together) && Overlaps(m.Bits, other.Bits)).ToList())
                            Unplace(run, mate);
                    }
                }
                if (Free(run, best, gear, null))
                {
                    Place(run, gear, best, "an in-flight essential takes the place of a function that is none");
                    gear.Fixed = true;
                    any = true;
                }
            }
            return any;
        }

        // Time-critical button essentials that belong on a grip without a modifier (the eval's release and wheel brake gates).
        private static readonly HashSet<string> GripEssentials = new HashSet<string>(StringComparer.Ordinal) { "launchweapon", "wheelbrakes_button" };
        // (and the flares - CMS forward - of an aircraft whose countermeasures found no hat at all: a plain grip button from a
        // function that is no essential, review 5 #7: TCA A-10C without countermeasures while quadrant buttons hold ICS / FoV)
        private const string FlareFunction = "cms_fwd_start_program_or_just_flare";

        // A weapon release or wheel brake button of a top aircraft that is not on a grip without a modifier while the hands
        // have grips takes a grip button from a function that is no in-flight essential (an occasional function on its
        // real position, a mode button). Returns whether anything moved.
        private static bool RescueGripEssentials(Run run)
        {
            bool any = false;
            if (!run.Slots.Any(s => s.Grip && !s.Input.IsAxis && s.Layer == 0))
                return false;
            foreach (Demand essential in run.Demands.Where(d => !d.Kept && !d.Redundant && !d.IsAxis && d.StyleDevice.Length == 0 && IsTop(run, d) &&
                                                                (GripEssentials.Contains(d.FunctionId) || d.FunctionId == FlareFunction || GunDemand(run, d) || NearestHostile(d)))
                                                       .OrderBy(d => NearestHostile(d) ? 50 : EssentialRank(d.FunctionId)).ThenByDescending(d => d.Importance).ToList())
            {
                Slot now;
                if (run.Placed.TryGetValue(essential, out now) && now.Grip && now.Layer == 0 && now.Phase.Length == 0)
                    continue;
                // (owner 2026-11-22: a spaceship's nearest hostile target is survival-critical with the guns, the missiles and
                // the countermeasures - an unplaced one takes any grip hat direction or button from what is no earlier
                // essential: the target cycle set, energy, views; a CH GameStick's only hat had secondary fire up and the
                // next / previous / attacker cycle on the rest)
                if (NearestHostile(essential) && run.Placed.ContainsKey(essential))
                    continue;
                bool nearest = NearestHostile(essential);
                // (the flares: judged now, not when the list was made - a weapon release of the same pass may just have
                // taken their grip button)
                if (essential.FunctionId == FlareFunction && DispenseOnGrip(run, essential))
                    continue;
                // (review 6 M4: the gun too - a stick without a trigger, the Yawman Arrow, lost it at "same button" to the
                // nose-wheel steering on its bumper; it takes a grip button back at every slider position)
                if (GunDemand(run, essential) && GunOnGrip(run, essential))
                    continue;
                Slot best = null;
                double cost = double.MaxValue;
                // (the gun and the release keep their grip buttons; the countermeasures and later essentials give way - the
                // eval's grip priority: gun, release, countermeasures, wheel brakes ...; review 7 B13: the survival functions
                // beat the wheel brake button, a ground function, unless the aircraft is a civil one)
                int rank = EssentialRank("cms_");
                // (review 7 B13: the weapon release and the wheel brake button too - a grip with a trigger and one hat, the VPforce
                // FC-Tech, lost the release to IL-2's trim on the hat; the trim comes after both in the documented order)
                // (review 6 B8: the flares are survival-critical - they may use a hat direction too, and every essential
                // after them in the documented order gives way: trim, target / TMS, snap views, gear, flaps, airbrake, comm;
                // a set on a hat leaves whole and is placed again)
                bool flare = essential.FunctionId == FlareFunction;
                bool civilBrake = essential.FunctionId == "wheelbrakes_button" && CivilOnly(run, essential);
                // (review 7 M14: the flares also take a momentary button of the throttle hand - the TCA quadrant's lever-end buttons -
                // before they go behind a shift)
                foreach (Slot slot in run.Slots.Where(s => (s.Grip || (flare && s.Role == SetupRoles.Throttle && string.Equals(s.Input.Type, "momentary", StringComparison.OrdinalIgnoreCase) && !HatControl.IsMatch(s.Control))) &&
                                                          !s.Input.IsAxis && s.Layer == 0 && s.Phase.Length == 0 && (flare || nearest || GripEssentials.Contains(essential.FunctionId) || !HatControl.IsMatch(s.Control)) &&
                                                          RescueScore(run, essential, s, nearest) > Forbidden))
                {
                    string reserved;
                    if ((run.ReservedFor.TryGetValue(slot.Index, out reserved) && essential.Nodes.Any(n => string.Equals(n.Game, reserved, StringComparison.OrdinalIgnoreCase))) ||
                        run.NotHere.Contains(essential.Index.ToString(CultureInfo.InvariantCulture) + "|" + slot.Index.ToString(CultureInfo.InvariantCulture)))
                        continue;
                    List<Demand> others = GivingWay(run, slot, essential);
                    // (an essential of its own aircraft that comes later in the grip order gives way too: on a stick with
                    // three grip buttons the release and the wheel brake button outrank the countermeasures)
                    if (others.Any(o => o.Kept || (o.Fixed && o.StyleDevice.Length > 0) || HasWish(o) || EssentialRank(o.FunctionId) < rank ||
                                        // (owner 2026-11-16: the weapon release is survival-critical, a wheel brake button is a ground
                                        // function - on a stick with three grip buttons in a plan for many games the release takes it;
                                        // the wheel brake button never takes the countermeasures' button)
                                        (GripEssentials.Contains(o.FunctionId) && !((essential.FunctionId == "launchweapon" || flare) && o.FunctionId == "wheelbrakes_button" && !CivilOnly(run, o)) && !civilBrake) ||
                                        (essential.FunctionId == "wheelbrakes_button" && EssentialRank(o.FunctionId) == rank && !civilBrake) ||
                                        // (a civil sim's wheel brake takes its grip button back from that sim's incidental weapons only)
                                        (civilBrake && !(CivilOnly(run, o) && Regex.IsMatch(o.FunctionId, "^(launchweapon|cms_)")) && (EssentialRank(o.FunctionId) < rank || GripEssentials.Contains(o.FunctionId) || EssentialRank(o.FunctionId) == rank)) ||
                                        (flare && EssentialRank(o.FunctionId) == rank && !(essential.DirKey.Length > 0 && o.DirKey == essential.DirKey)) ||
                                        // (looking around keeps its hat: the nearest hostile never takes the snap views or the recenter)
                                        (nearest && (o.DirKey == "snapview" || o.FunctionId == "view_center")) ||
                                        // (and never the countermeasures: they come before it in the survival order)
                                        (nearest && EssentialRank(o.FunctionId) <= rank) ||
                                        // (the nearest hostile moves a hat set of no essential - energy, target cycle - whole; never a pair)
                                        (EssentialRank(o.FunctionId) == 99 && o.Fixed && (o.Function.PairGroup.Length > 0 || o.Function.Group.Length > 0) &&
                                         !(nearest && o.DirKey.Length > 0 && o.Function.PairGroup.Length == 0))))
                        continue;
                    // (a hat direction costs a little more than a free push button: the set it breaks is placed again)
                    double c = others.Sum(o => o.Importance * (EssentialRank(o.FunctionId) < 99 ? 4 : 1)) - 0.01 * RescueScore(run, essential, slot, nearest) + (HatControl.IsMatch(slot.Control) ? 0.5 : 0);
                    if (c < cost)
                    {
                        cost = c;
                        best = slot;
                    }
                }
                if (best == null)
                    continue;
                foreach (Demand other in GivingWay(run, best, essential))
                {
                    Slot was;
                    if (run.Placed.TryGetValue(other, out was))
                        run.NotHere.Add(other.Index.ToString(CultureInfo.InvariantCulture) + "|" + was.Index.ToString(CultureInfo.InvariantCulture));
                    Unplace(run, other);
                }
                if (run.Placed.ContainsKey(essential))
                    Unplace(run, essential);
                if (Free(run, best, essential, null))
                {
                    Place(run, essential, best, "an in-flight essential takes the place of a function that is none");
                    essential.Fixed = true;
                    any = true;
                }
                else if (now != null && Free(run, now, essential, null))
                    Place(run, essential, now, essential.Reason);
            }
            return any;
        }

        // A demand of civil aircraft only: a civil sim's, or an aircraft without gun, release or countermeasures.
        private static bool CivilOnly(Run run, Demand demand)
        {
            return demand.Nodes.Count > 0 && demand.Nodes.All(n => CivilSims.Contains(n.Game ?? string.Empty) || !run.Armed.Contains(NodeKey(n)));
        }

        // The civil sims (MSFS, X-Plane, Prepar3D, FSX): a weapon command in their catalogs is incidental, their wheel brake is
        // the airliner's / light aircraft's and keeps its grip button.
        internal static readonly HashSet<string> CivilSims = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MSFS2024", "MSFS2020", "XPlane12", "XPlane11", "FSX", "FSXSE", "P3D4", "P3D5", "P3D6" };

        // A zoom axis placed for every aircraft of the demand.
        private static bool ZoomAxisPlaced(Run run, Demand demand)
        {
            return demand.Aircraft.Count > 0 && demand.Aircraft.All(p => run.Placed.Keys.Any(o => o.IsAxis && o.FunctionId == "zoom" && o.Aircraft.Contains(p, StringComparer.OrdinalIgnoreCase)));
        }

        // (owner 2026-11-16) A wheel brake button of a top aircraft with no grip button left unshifted (the gun, the release and
        // the flares keep the head buttons in a combat aircraft) falls back to a momentary, unshifted button of the throttle
        // hand - the TCA quadrant's lever-end buttons - taken from a function that comes later in the documented order.
        private static bool RescueWheelBrake(Run run)
        {
            bool any = false;
            int rank = EssentialRank("wheelbrakes_button");
            foreach (Demand brake in run.Demands.Where(d => d.FunctionId == "wheelbrakes_button" && !d.Kept && !d.Redundant && !d.IsAxis && d.StyleDevice.Length == 0 && IsTop(run, d)).ToList())
            {
                Slot now;
                if (run.Placed.TryGetValue(brake, out now) && now.Layer == 0 && now.Phase.Length == 0 && (now.Grip || now.Role == SetupRoles.Throttle))
                    continue;
                Slot best = null;
                double cost = double.MaxValue;
                foreach (Slot slot in run.Slots.Where(s => s.Layer == 0 && s.Phase.Length == 0 && !s.Input.IsAxis && !s.Grip && s.Role == SetupRoles.Throttle &&
                                                          string.Equals(s.Input.Type, "momentary", StringComparison.OrdinalIgnoreCase) && !HatControl.IsMatch(s.Control) &&
                                                          S(run, brake, s) > Forbidden))
                {
                    if (run.NotHere.Contains(brake.Index.ToString(CultureInfo.InvariantCulture) + "|" + slot.Index.ToString(CultureInfo.InvariantCulture)))
                        continue;
                    List<Demand> others = GivingWay(run, slot, brake);
                    if (others.Any(o => o.Kept || o.StyleDevice.Length > 0 || HasWish(o) || EssentialRank(o.FunctionId) <= rank ||
                                        (o.Fixed && (o.Function.PairGroup.Length > 0 || o.Function.Group.Length > 0))))
                        continue;
                    double c = others.Sum(o => o.Importance) - 0.01 * S(run, brake, slot);
                    if (c < cost)
                    {
                        cost = c;
                        best = slot;
                    }
                }
                if (best == null)
                    continue;
                foreach (Demand other in GivingWay(run, best, brake))
                {
                    run.NotHere.Add(other.Index.ToString(CultureInfo.InvariantCulture) + "|" + best.Index.ToString(CultureInfo.InvariantCulture));
                    Unplace(run, other);
                }
                if (run.Placed.ContainsKey(brake))
                    Unplace(run, brake);
                if (Free(run, best, brake, null))
                {
                    Place(run, brake, best, "the wheel brake on the throttle hand (the grip buttons hold the gun, the release and the flares)");
                    brake.Fixed = true;
                    any = true;
                }
                else if (now != null && Free(run, now, brake, null))
                    Place(run, brake, now, brake.Reason);
            }
            return any;
        }

        // (owner 2026-11-16) Flares that found no grip input without a modifier - a 3-button yoke whose second button is
        // printed PTT - go behind a shift on a grip: survival-critical beats "no modifier". A printed label keeps its button;
        // only free slots or slots of functions that are no in-flight essential are taken. Returns whether anything moved.
        private static bool RescueShiftedFlares(Run run)
        {
            bool any = false;
            foreach (Demand flare in run.Demands.Where(d => d.FunctionId == FlareFunction && !d.Kept && !d.Redundant && d.StyleDevice.Length == 0 && IsTop(run, d)).ToList())
            {
                if (DispenseOnGrip(run, flare) || FlareOnShiftedGrip(run, flare))
                    continue;
                Slot best = null;
                double cost = double.MaxValue;
                foreach (Slot slot in run.Slots.Where(s => s.Grip && !s.Input.IsAxis && s.Layer > 0 && s.Phase.Length == 0 && s.Control.IndexOf(".trigger.", StringComparison.Ordinal) < 0))
                {
                    // (on a hat only its up direction - CMS forward - or a push button)
                    if (HatControl.IsMatch(slot.Control) && HatWord(LastSegment(slot.Control)) != "up")
                        continue;
                    // (never for a game without modifier binds: the export could not write the shifted bind)
                    if (!flare.ShiftOk || LabelTaken(run, flare, slot) || run.NotHere.Contains(flare.Index.ToString(CultureInfo.InvariantCulture) + "|" + slot.Index.ToString(CultureInfo.InvariantCulture)))
                        continue;
                    List<Demand> others = run.Occupants[slot.Index].Where(o => Conflict(o, flare)).ToList();
                    if (others.Any(o => o.Kept || HasWish(o) || EssentialRank(o.FunctionId) < 99 || o.DirKey.Length > 0 || (o.Fixed && (o.Function.PairGroup.Length > 0 || o.Function.Group.Length > 0))))
                        continue;
                    double c = others.Sum(o => o.Importance) + (HatControl.IsMatch(slot.Control) ? 0.5 : 0);
                    if (c < cost)
                    {
                        cost = c;
                        best = slot;
                    }
                }
                if (best == null)
                    continue;
                foreach (Demand other in run.Occupants[best.Index].Where(o => Conflict(o, flare)).ToList())
                {
                    Unplace(run, other);
                    run.NotHere.Add(other.Index.ToString(CultureInfo.InvariantCulture) + "|" + best.Index.ToString(CultureInfo.InvariantCulture));
                }
                if (run.Placed.ContainsKey(flare))
                    Unplace(run, flare);
                if (Free(run, best, flare, null))
                {
                    Place(run, flare, best, "survival-critical: behind " + (best.Modifier == null ? "a modifier" : best.Modifier.Name) + " (no grip input left without a modifier)");
                    flare.Fixed = true;
                    any = true;
                }
            }
            return any;
        }

        private static bool FlareOnShiftedGrip(Run run, Demand flare)
        {
            return run.Placed.Any(p => (p.Key == flare || p.Key.FunctionId == FlareFunction || p.Key.FunctionId == "cms_aft_panic_or_just_chaff") && p.Value.Grip && p.Value.Phase.Length == 0 &&
                                       Covers(p.Key.DirectBits ?? p.Key.Bits, flare.DirectBits ?? flare.Bits));
        }

        // What must leave a slot for an essential: the conflicting occupants, and with a member of a directional set (trim,
        // TMS, snap views on a hat) the whole set on that hat - a set never ends up split.
        private static List<Demand> GivingWay(Run run, Slot slot, Demand essential)
        {
            List<Demand> others = run.Occupants[slot.Index].Where(o => Conflict(o, essential)).ToList();
            string hat = Prefix(slot.RuleControl);
            foreach (Demand member in others.Where(o => o.DirKey.Length > 0).ToList())
            {
                foreach (KeyValuePair<Demand, Slot> placed in run.Placed.Where(p => p.Key.DirKey == member.DirKey && Overlaps(p.Key.Bits, member.Bits) &&
                                                                                    p.Value.Device == slot.Device && p.Value.Layer == slot.Layer && Prefix(p.Value.RuleControl) == hat).ToList())
                {
                    if (!others.Contains(placed.Key))
                        others.Add(placed.Key);
                }
            }
            // (the essential's own set mates on another hat or layer - CMS left / right behind a shift on the other hat -
            // leave too: a set never ends up on two hats, they are placed again around it)
            if (essential.DirKey.Length > 0 && GroupRoles.Contains(LastSegment(slot.RuleControl)))
            {
                foreach (KeyValuePair<Demand, Slot> mate in run.Placed.Where(p => p.Key != essential && p.Key.DirKey == essential.DirKey && Overlaps(p.Key.DirectBits ?? p.Key.Bits, essential.DirectBits ?? essential.Bits) &&
                                                                                 !p.Value.Input.IsAxis && GroupRoles.Contains(LastSegment(p.Value.RuleControl)) &&
                                                                                 (p.Value.Device != slot.Device || Prefix(p.Value.RuleControl) != hat || p.Value.Layer != slot.Layer)).ToList())
                {
                    if (!others.Contains(mate.Key))
                        others.Add(mate.Key);
                }
            }
            return others;
        }

        // The aircraft of a flare demand dispense already from a grip without a modifier (CMS forward or aft, the functions
        // that release flares / chaff; CMS left / right / ECM select programs or jammers - the Mi-8's UV-26 SERIYA / ZALP).
        // A grip input or a momentary push button of the throttle hand (the quadrant's lever-end buttons).
        private static bool InHand(Slot slot)
        {
            return slot.Grip || (slot.Role == SetupRoles.Throttle && !slot.Input.IsAxis && string.Equals(slot.Input.Type, "momentary", StringComparison.OrdinalIgnoreCase) && !HatControl.IsMatch(slot.Control));
        }

        private static bool DispenseOnGrip(Run run, Demand flare)
        {
            Slot at;
            // (a momentary button of the throttle hand counts too: the TCA quadrant's lever-end buttons, review 7 M14)
            if (run.Placed.TryGetValue(flare, out at) && InHand(at) && at.Layer == 0 && at.Phase.Length == 0)
                return true;
            return run.Placed.Any(p => p.Key != flare && (p.Key.FunctionId == FlareFunction || p.Key.FunctionId == "cms_aft_panic_or_just_chaff") && InHand(p.Value) && p.Value.Layer == 0 &&
                                       p.Value.Phase.Length == 0 && Covers(p.Key.DirectBits ?? p.Key.Bits, flare.DirectBits ?? flare.Bits));
        }

        // A demand that fires an aircraft's gun (a trigger stage that is no camera / laser detent and drops no bombs).
        // The survival order of the last pass: gun (primary fire), weapon release (secondary fire), a spaceship's nearest
        // hostile; 99 = none of them.
        private static int SurvivalOrder(Run run, Demand d)
        {
            if (d.FunctionId == "trigger1ststage" && !d.CameraDetent && !d.ReleaseStage)
                return 0;
            if (d.FunctionId == "launchweapon")
                return 1;
            return NearestHostile(d) ? 2 : 99;
        }

        // (owner 2026-11-22) Primary fire, secondary fire and the nearest hostile that found no input take, in this order, any
        // unshifted button or hat direction the hands reach (grips first, then bases, throttles and panels): a free one, else
        // one whose functions are no survival function before them (a set or group there leaves whole). Never a printed
        // label of another function, a kept bind or a wish; the nearest hostile also never takes the countermeasures or
        // looking around. Returns whether anything moved.
        private static bool RescueSurvivalAnywhere(Run run)
        {
            bool any = false;
            foreach (Demand essential in run.Demands.Where(d => !d.Kept && !d.Redundant && !d.IsAxis && d.StyleDevice.Length == 0 && IsTop(run, d) && SurvivalOrder(run, d) < 99 && !run.Placed.ContainsKey(d))
                                                       .OrderBy(d => SurvivalOrder(run, d)).ThenByDescending(d => d.Importance).ToList())
            {
                if (run.Placed.ContainsKey(essential))
                    continue;
                int order = SurvivalOrder(run, essential);
                bool nearest = order == 2;
                Slot best = null;
                double cost = double.MaxValue;
                // (first the inputs the normal rules allow; only when none is left a momentary input the rules keep for
                // something else: a panel key whose printed label names another function, a replica's key, a dedicated
                // control the game has no function for (the YOKO's trim rocker in a space game) - never a labelled grip
                // button (a PTT keeps its comms) and never a latched switch)
                for (int attempt = 0; attempt < 2 && best == null; attempt++)
                foreach (Slot slot in run.Slots.Where(s => !s.Input.IsAxis && s.Layer == 0 && s.Phase.Length == 0 && (attempt == 0 || !string.Equals(s.Input.Type, "latched", StringComparison.OrdinalIgnoreCase))))
                {
                    double score = RescueScore(run, essential, slot, nearest, attempt == 1);
                    if (score <= Forbidden)
                        continue;
                    string reserved;
                    if ((run.ReservedFor.TryGetValue(slot.Index, out reserved) && essential.Nodes.Any(n => string.Equals(n.Game, reserved, StringComparison.OrdinalIgnoreCase))) ||
                        run.NotHere.Contains(essential.Index.ToString(CultureInfo.InvariantCulture) + "|" + slot.Index.ToString(CultureInfo.InvariantCulture)) ||
                        LabelTakenFor(run, essential, slot, attempt == 1))
                        continue;
                    List<Demand> others = GivingWay(run, slot, essential);
                    if (others.Any(o => o.Kept || HasWish(o) || (o.Fixed && o.StyleDevice.Length > 0) || o.Function.PairGroup.Length > 0 ||
                                        SurvivalOrder(run, o) <= order || EssentialRank(o.FunctionId) == 0 ||
                                        (nearest && (EssentialRank(o.FunctionId) <= EssentialRank("cms_") || o.DirKey == "snapview" || o.FunctionId == "view_center"))))
                        continue;
                    double c = others.Sum(o => o.Importance * (EssentialRank(o.FunctionId) < 99 ? 4 : 1)) + (slot.Grip ? 0 : 2) - 0.01 * score;
                    if (c < cost)
                    {
                        cost = c;
                        best = slot;
                    }
                }
                if (best == null)
                    continue;
                foreach (Demand other in GivingWay(run, best, essential))
                {
                    Slot was;
                    if (run.Placed.TryGetValue(other, out was))
                        run.NotHere.Add(other.Index.ToString(CultureInfo.InvariantCulture) + "|" + was.Index.ToString(CultureInfo.InvariantCulture));
                    Unplace(run, other);
                }
                if (Free(run, best, essential, null))
                {
                    Place(run, essential, best, "survival-critical: the gun, the release and the nearest hostile get an input before anything else");
                    essential.Fixed = true;
                    any = true;
                }
            }
            return any;
        }

        private static bool LabelTakenFor(Run run, Demand essential, Slot slot, bool lastTry)
        {
            essential.IgnorePanelLabels = lastTry;
            try
            {
                return LabelTaken(run, essential, slot);
            }
            finally
            {
                essential.IgnorePanelLabels = false;
            }
        }

        // A spaceship's "target the nearest hostile" (Star Citizen, Elite, X4, Fate of the Galaxy): survival-critical.
        private static bool NearestHostile(Demand d)
        {
            return d.FunctionId == "space_target_closest_hostile" && d.Classes.Contains(SetupClasses.Space);
        }

        // The rescue's score: the nearest hostile may take any grip hat direction there (its set's direction is held by an
        // earlier essential on a one-hat stick); computed without the score cache, which holds the normal rule.
        private static double RescueScore(Run run, Demand essential, Slot slot, bool anyDirection, bool ignorePanelLabels = false)
        {
            if (!anyDirection && !ignorePanelLabels)
                return S(run, essential, slot);
            essential.AnyDirection = anyDirection;
            essential.IgnorePanelLabels = ignorePanelLabels;
            try
            {
                return Score(run, essential, slot);
            }
            finally
            {
                essential.AnyDirection = false;
                essential.IgnorePanelLabels = false;
            }
        }

        private static bool GunDemand(Run run, Demand d)
        {
            return (d.FunctionId == "trigger2ndstage" || d.FunctionId == "trigger1ststage") && !d.CameraDetent && !d.ReleaseStage && d.Aircraft.Any(run.GunAircraft.Contains);
        }

        // Its aircraft fire the gun from a grip without a modifier already.
        private static bool GunOnGrip(Run run, Demand gun)
        {
            Slot at;
            if (run.Placed.TryGetValue(gun, out at) && at.Grip && at.Layer == 0 && at.Phase.Length == 0)
                return true;
            return run.Placed.Any(p => p.Key != gun && GunDemand(run, p.Key) && p.Value.Grip && p.Value.Layer == 0 && p.Value.Phase.Length == 0 &&
                                       Covers(p.Key.DirectBits ?? p.Key.Bits, gun.DirectBits ?? gun.Bits));
        }

        // Every aircraft of b is in a.
        private static bool Covers(ulong[] a, ulong[] b)
        {
            for (int i = 0; i < b.Length; i++)
            {
                if ((b[i] & ~(i < a.Length ? a[i] : 0UL)) != 0)
                    return false;
            }
            return true;
        }

        private static bool SnapsToFreeHat(Run run)
        {
            bool any = false;
            int snapRank = EssentialRank("view_snap_forward");
            // (with head tracking only in the games the tracker does not reach, review 7 M17)
            foreach (List<Demand> set in run.Demands.Where(d => d.DirKey == "snapview" && !d.Kept && !d.Redundant && d.DirRole.Length > 0 && !d.IsAxis && IsTop(run, d) &&
                                                                (!run.Options.HeadTracking || !d.Nodes.Any(n => HeadTrackingGame(n.Game))))
                         .GroupBy(d => d.HandKey + "|" + SnapKey(run, d), StringComparer.Ordinal).Select(g => g.ToList())
                         .Where(g => g.Count >= 2 && (g.All(d => !run.Placed.ContainsKey(d)) || ShiftedSnaps(run, g))).ToList())
            {
                List<Tuple<Demand, Slot>> best = null;
                HashSet<Demand> bestOut = null;
                double bestCost = double.MaxValue;
                // (an unplaced set may also take a hat behind a shift - slow, but looking around stays; a shifted set only an
                // unshifted hat; behind a shift the snap views' veto on layers is waived, as EssentialSets' fallback does)
                bool shiftedNow = ShiftedSnaps(run, set);
                foreach (IGrouping<string, Slot> hat in run.Slots.Where(s => (s.Layer == 0 || (!shiftedNow && s.Modifier != null && !s.Modifier.Switch)) && s.Phase.Length == 0 && s.Grip && !s.Input.IsAxis &&
                                                                 HatControl.IsMatch(s.RuleControl) && GroupRoles.Contains(LastSegment(s.RuleControl)))
                             .GroupBy(s => s.Device.Device + "|" + s.Layer.ToString(CultureInfo.InvariantCulture) + "|" + Prefix(s.RuleControl), StringComparer.Ordinal))
                {
                    List<Tuple<Demand, Slot>> placement = new List<Tuple<Demand, Slot>>();
                    HashSet<Demand> leaving = new HashSet<Demand>();
                    bool ok = true;
                    foreach (Demand member in set)
                    {
                        Slot slot = hat.FirstOrDefault(s => LastSegment(s.RuleControl) == member.DirRole);
                        // (a shifted hat never for a game without modifier binds: FotG, X-Plane, Arma could not write it)
                        if (slot == null || (slot.Layer == 0 ? S(run, member, slot) <= Forbidden : (!member.ShiftOk || LabelTaken(run, member, slot))))
                        {
                            ok = false;
                            break;
                        }
                        // (what holds the direction for these aircraft gives way when it comes after looking around in the
                        // documented order - target pods, weapon select, radar modes, comm; its whole set on that hat leaves;
                        // never the user's own, a wish, a pair or an earlier essential)
                        foreach (Demand other in run.Occupants[slot.Index].Where(o => o != member && Conflict(o, member)))
                        {
                            // (a pair of no essential - exterior lights on / off - leaves whole: looking around outranks it)
                            bool loosePair = other.Function.PairGroup.Length > 0 && EssentialRank(other.FunctionId) == 99 && !RealHeld(run, other, slot);
                            if (other.Kept || HasWish(other) || (other.Function.PairGroup.Length > 0 && !loosePair) || EssentialRank(other.FunctionId) <= snapRank || other.StyleDevice.Length > 0)
                            {
                                ok = false;
                                break;
                            }
                            leaving.Add(other);
                            if (loosePair)
                                foreach (Demand mate in run.Placed.Keys.Where(m => m != other && m.Function.PairGroup == other.Function.PairGroup && Overlaps(m.Bits, other.Bits)).ToList())
                                    leaving.Add(mate);
                            if (other.DirKey.Length > 0)
                                foreach (Demand mate in run.Placed.Where(p => p.Key.DirKey == other.DirKey && Overlaps(p.Key.Bits, other.Bits) && p.Value.Device == slot.Device && p.Value.Layer == 0 &&
                                                                              Prefix(p.Value.RuleControl) == Prefix(slot.RuleControl)).Select(p => p.Key).ToList())
                                    leaving.Add(mate);
                        }
                        if (!ok)
                            break;
                        placement.Add(Tuple.Create(member, slot));
                    }
                    if (!ok || placement.Count != set.Count || leaving.Any(o => o.Kept || HasWish(o) || EssentialRank(o.FunctionId) <= snapRank))
                        continue;
                    double cost = leaving.Sum(o => o.Importance) + 1000 * hat.First().Layer;
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = placement;
                        bestOut = leaving;
                    }
                }
                if (best == null)
                    continue;
                Dictionary<Demand, Slot> before = set.Where(run.Placed.ContainsKey).ToDictionary(d => d, d => run.Placed[d]);
                Dictionary<Demand, Slot> outBefore = bestOut.Where(run.Placed.ContainsKey).ToDictionary(d => d, d => run.Placed[d]);
                foreach (Demand member in before.Keys)
                    Unplace(run, member);
                foreach (Demand other in outBefore.Keys)
                    Unplace(run, other);
                if (best.All(p => Free(run, p.Item2, p.Item1, null)))
                {
                    foreach (Tuple<Demand, Slot> p in best)
                        Place(run, p.Item1, p.Item2, p.Item2.Layer > 0 ? "the whole set on one hat behind " + p.Item2.Modifier.Name + " (no hat free without a modifier: slower, but looking around stays)" : "the whole set on one hat (it outranks what was there)");
                    foreach (KeyValuePair<Demand, Slot> gone in outBefore)
                        run.NotHere.Add(gone.Key.Index.ToString(CultureInfo.InvariantCulture) + "|" + gone.Value.Index.ToString(CultureInfo.InvariantCulture));
                    any = true;
                    continue;
                }
                foreach (KeyValuePair<Demand, Slot> back in outBefore)
                    Place(run, back.Key, back.Value, back.Key.Reason);
                foreach (KeyValuePair<Demand, Slot> back in before)
                    Place(run, back.Key, back.Value, back.Key.Reason);
            }
            return any;
        }

        // A snap-view or trim set placed whole behind a shift button (it looks for an unshifted hat again).
        private static bool ShiftedSnaps(Run run, List<Demand> set)
        {
            Slot at;
            return set.All(d => (d.DirKey == "snapview" || TrimFunction.IsMatch(d.FunctionId)) && run.Placed.TryGetValue(d, out at) && at.Layer > 0 && at.Phase.Length == 0);
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
            PadThrottleButtons(run, open);
            // Round 1 places only strong matches (a panel label naming the function, its exact position, the real
            // aircraft's position, the left stick's translation axes), so a labelled switch goes to its own function
            // before a more important pair or group takes it; round 2 the rest.
            // Realism from 50 % on: the real aircraft's positions (cockpit database) go first of all.
            foreach (int strength in run.Realism >= RealThresholdHotas ? new[] { 2, 1, 0 } : new[] { 1, 0 })
            {
                AssignPairs(run, open, strength);
                AssignGroups(run, open, strength);
                AssignSingles(run, open, strength);
            }
            LocalSearch(run, open);
            // What the aircraft already have another way gives its input back; the rest is placed again with it.
            if (DropRedundant(run))
            {
                List<Demand> rest = open.Where(d => !d.Redundant).ToList();
                AssignPairs(run, rest, 0);
                AssignGroups(run, rest, 0);
                AssignSingles(run, rest, 0);
                LocalSearch(run, rest);
            }
            // Looking around (the snap views, a whole set on one hat) displaces less important single functions from a
            // hat when no hat is free for it; they are placed again elsewhere.
            RejoinPairs(run);
            bool detentsFreed = DetentsOfThrottleLevers(run);
            // (a set that lost its hat to an earlier essential may take another one in the next pass)
            bool setsMoved = false;
            for (int pass = 0; pass < 3 && EssentialSets(run); pass++)
                setsMoved = true;
            if (setsMoved || detentsFreed)
            {
                List<Demand> rest = open.Where(d => !d.Redundant && !run.Placed.ContainsKey(d)).ToList();
                AssignGroups(run, rest, 0);
                AssignSingles(run, rest, 0);
                LocalSearch(run, rest);
                RejoinPairs(run);
            }


            // A sim whose camera JoyPro does not manage (MSFS, X-Plane, Falcon BMS ...) keeps its own look-around on a
            // grip hat: without head tracking one hat stays free for it.
            if (ReserveGameLookHat(run))
            {
                List<Demand> rest = open.Where(d => !d.Redundant && !run.Placed.ContainsKey(d)).ToList();
                AssignGroups(run, rest, 0);
                AssignSingles(run, rest, 0);
            }

            // The weapon release and the wheel brake button are time-critical: off the grips or behind a shift (a real position
            // of an occasional function took their grip button) they take a grip button back from a function that is no
            // in-flight essential, at every slider position.
            if (RescueGripEssentials(run))
            {
                // (a set the flares took a hat direction from - snap views, TMS - looks for another hat or the shift layer)
                for (int pass = 0; pass < 3 && EssentialSets(run); pass++)
                {
                }
                List<Demand> rest = open.Where(d => !d.Redundant && !run.Placed.ContainsKey(d)).ToList();
                AssignGroups(run, rest, 0);
                AssignSingles(run, rest, 0);
                RejoinPairs(run);
            }

            // Flares with no grip input left without a modifier: behind a shift on a grip (survival-critical, owner 2026-11-16).
            if (RescueShiftedFlares(run))
            {
                List<Demand> rest = open.Where(d => !d.Redundant && !run.Placed.ContainsKey(d)).ToList();
                AssignSingles(run, rest, 0);
            }

            // A wheel brake button the gun, release and flares left without a grip button: a momentary button of the throttle
            // hand (the TCA quadrant's lever-end buttons) unshifted (owner 2026-11-16; toe brakes on pedals make it redundant).
            if (RescueWheelBrake(run))
            {
                List<Demand> rest = open.Where(d => !d.Redundant && !run.Placed.ContainsKey(d)).ToList();
                AssignSingles(run, rest, 0);
            }

            // The gear (or a lone essential) without any place takes one from a function that is no essential (behind a shift button on a gamepad).
            if (RescueGear(run))
            {
                List<Demand> rest = open.Where(d => !d.Redundant && !run.Placed.ContainsKey(d)).ToList();
                AssignSingles(run, rest, 0);
            }

            // (owner 2026-11-16) Looking around on a grip hat survives at every slider position when a hat is free: a snap-view
            // set left behind a shift or without a place moves to a grip hat none of whose directions holds anything of its
            // aircraft. Without head tracking only.
            if (SnapsToFreeHat(run))
            {
                List<Demand> rest = open.Where(d => !d.Redundant && !run.Placed.ContainsKey(d)).ToList();
                AssignGroups(run, rest, 0);
                AssignSingles(run, rest, 0);
            }

            // (owner 2026-11-22) The last word: while the devices have any button or hat direction the hands reach, the gun,
            // then the weapon release, then a spaceship's nearest hostile get one - before views, comms, PTT or anything else.
            if (RescueSurvivalAnywhere(run))
            {
                List<Demand> rest = open.Where(d => !d.Redundant && !run.Placed.ContainsKey(d)).ToList();
                AssignSingles(run, rest, 0);
            }

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
            foreach (SetupCommand command in request.Commands.Where(c => (c.Function == "trigger1ststage" || c.Function == "trigger2ndstage") && games.Contains(c.Game)))
                run.GunAircraft.Add(command.Aircraft);
            // (aircraft with weapons: gun, release or countermeasures - a civil aircraft's wheel brake is a demand of its own)
            HashSet<string> armed = run.Armed;
            armed.UnionWith(request.Commands.Where(c => games.Contains(c.Game) && Regex.IsMatch(c.Function ?? string.Empty, "^(trigger1ststage|trigger2ndstage|launchweapon|cms_fwd_start_program_or_just_flare|cms_aft_panic_or_just_chaff)$"))
                .Select(c => c.Aircraft));
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
            // (a hangar of many modules: the wheel brake button per module - one for 20 modules found no button free of every
            // module's flares and release; a plan for one or a few aircraft keeps one)
            bool hangar = scopeAircraft.Where(a => request.Aircraft.ContainsKey(a) && !request.Aircraft[a].SharedLayer).Select(a => SeatBase.Replace(a, string.Empty)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 3;

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
            // One relation per game command: a command the catalog lists under several functions (Elite's yaw axis as
            // "rudder" and "space_yaw", Arma's throttle as four throttles) goes to one of them, so no command is bound twice.
            // The two positions of a latched pair (canopy open / close on one toggle command) stay: a switch flips it both ways.
            Dictionary<string, string> commandFunction = OneFunctionPerCommand(request, games);
            HashSet<string> binding = new HashSet<string>(request.Devices.Where(d => SetupRoles.Binds(d.Role)).Select(d => d.Device), StringComparer.Ordinal);
            run.Pad = request.Devices.Any(d => d.Role == SetupRoles.MainStick && string.Equals(d.Kind, "gamepad", StringComparison.OrdinalIgnoreCase));
            // (review 7 M10: a left stick's spring-return Y is no throttle axis - only its slider / wheel is)
            bool throttleAxis =                                 request.Inputs.Any(i => i.IsAxis && binding.Contains(i.Device) && (LeverAxis.IsMatch(i.Control) || SliderAxis.IsMatch(i.Control) || i.Control.StartsWith("collective.axis.", StringComparison.Ordinal)));
            if (!throttleAxis)
            {
                foreach (IGrouping<string, SetupCommand> plane in request.Commands.Where(c => games.Contains(c.Game) && (c.Function == "throttle_up_button" || c.Function == "throttle_down_button"))
                             .GroupBy(c => c.Aircraft, StringComparer.OrdinalIgnoreCase))
                {
                    if (plane.Select(c => c.Function).Distinct(StringComparer.Ordinal).Count() == 2)
                        run.ThrottleButtonAircraft.Add(plane.Key);
                }
                run.PadStickThrottle = run.Pad && request.Commands.Any(c => games.Contains(c.Game) && ThrottleFunction.IsMatch(c.Function) && !run.ThrottleButtonAircraft.Contains(c.Aircraft) &&
                                                                       !(request.Aircraft.ContainsKey(c.Aircraft) && request.Aircraft[c.Aircraft].SharedLayer));
                if (run.Pad && !options.HeadTracking)
                {
                    foreach (IGrouping<string, SetupCommand> plane in request.Commands.Where(c => games.Contains(c.Game) && (c.Function == "view_look_horizontal" || c.Function == "view_look_vertical"))
                                 .GroupBy(c => c.Aircraft, StringComparer.OrdinalIgnoreCase))
                    {
                        if (plane.Select(c => c.Function).Distinct(StringComparer.Ordinal).Count() == 2 && run.ThrottleButtonAircraft.Contains(plane.Key))
                            run.PadLookAircraft.Add(plane.Key);
                    }
                }
            }
            Dictionary<string, bool> cameraCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            Func<string, bool> cameraDetent = plane =>
            {
                bool value;
                if (!cameraCache.TryGetValue(plane, out value))
                    cameraCache[plane] = value = IsCameraDetent(request, plane);
                return value;
            };
            Dictionary<string, bool> twinCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            Func<SetupCommand, string> throttleRole = command =>
            {
                string role = ThrottleRoleOf(command);
                if (role.Length == 0)
                    return role;
                // A helicopter's engine power / condition levers (Mi-8, Ka-50, AH-64) are set once for the flight: they
                // never take the throttle levers from the collective (role H: any free axis, not a lever).
                SetupAircraft heli;
                if (request.Aircraft.TryGetValue(command.Aircraft, out heli) && ClassOf(request, heli) == SetupClasses.Rotary && !command.Function.EndsWith("nonab", StringComparison.Ordinal) &&
                    !request.Commands.Any(c => c.Game == command.Game && c.Action == command.Action && string.Equals(c.Aircraft, command.Aircraft, StringComparison.OrdinalIgnoreCase) && c.Function.EndsWith("nonab", StringComparison.Ordinal)))
                    return "H";
                bool twin;
                if (!twinCache.TryGetValue(command.Game + "|" + command.Aircraft, out twin))
                {
                    List<SetupCommand> mine = request.Commands.Where(c => c.Game == command.Game && string.Equals(c.Aircraft, command.Aircraft, StringComparison.OrdinalIgnoreCase)).ToList();
                    HashSet<string> left = new HashSet<string>(mine.Where(c => ThrottleRoleOf(c) == "L").Select(c => c.Action), StringComparer.OrdinalIgnoreCase);
                    HashSet<string> right = new HashSet<string>(mine.Where(c => ThrottleRoleOf(c) == "R").Select(c => c.Action), StringComparer.OrdinalIgnoreCase);
                    twinCache[command.Game + "|" + command.Aircraft] = twin = left.Count > 0 && right.Count > 0 && !left.Overlaps(right);
                }
                return twin ? role : string.Empty;
            };
            // (Lay out with AI without "add missing": only the profile's own relations.)
            foreach (SetupCommand command in request.Commands.Where(c => games.Contains(c.Game) && !options.ExistingOnly).OrderBy(c => c.Function, StringComparer.Ordinal).ThenBy(c => c.Aircraft, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Action, StringComparer.Ordinal))
            {
                string chosen;
                if (commandFunction.TryGetValue(CommandKey(command.Game, command.Action, command.Aircraft), out chosen) && !string.Equals(chosen, command.Function, StringComparison.OrdinalIgnoreCase))
                    continue;
                SetupFunction function;
                SetupAircraft plane;
                if (string.Equals(command.Status, "substitute", StringComparison.OrdinalIgnoreCase) || command.Function == "dummyunbindrelation" ||
                    !request.Functions.TryGetValue(command.Function, out function) || !request.Aircraft.TryGetValue(command.Aircraft, out plane))
                    continue;
                string key = CommandKey(command.Game, command.Action, command.Aircraft);
                if (owner.ContainsKey(key) || request.Disabled.Contains(key))
                    continue;
                // With head tracking or VR (or planned) no input is spent on looking around in the sims that support it:
                // only the view recenter stays.
                if (options.HeadTracking && HeadTrackingLook.IsMatch(command.Function) && HeadTrackingGame(command.Game))
                    continue;
                // Throttle increase / decrease buttons only where there is no lever, slider or left stick for the throttle
                // (a gamepad): elsewhere they would take hats from the essentials.
                if ((command.Function == "throttle_up_button" || command.Function == "throttle_down_button") && throttleAxis)
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
                string side = RealOn(run, function) && command.Hotas.Length > 0 ? SideOfHotas(command.Hotas) : NormalizeSide(function.Side);
                SetupDevice hand = side == "stick" ? SetupRoles.RightHand(request.Devices, cls) : side == "throttle" ? SetupRoles.LeftHand(request.Devices, cls) : null;
                string handKey = hand != null ? hand.Role : side;
                string realKey = string.Empty;
                List<string> real;
                // (the gun stays on the trigger at every slider position: the Mi-8's real fire button under its cover, the
                // UH-1H's on top of the cyclic, do not split the trigger relation)
                if (RealOn(run, function) && function.Id != "trigger1ststage" && function.Id != "trigger2ndstage" && TryReal(request, plane, function.Id, out real))
                    realKey = string.Join("+", real.OrderBy(r => r, StringComparer.OrdinalIgnoreCase));
                // (at every slider position: a helicopter's engine levers never share a demand with a jet's throttle, and a
                // second trigger stage that drops bombs never with one that fires the gun - one aircraft of the other kind
                // would push the whole group off the lever / trigger)
                string kindKey = (function.IsAxis && ThrottleFunction.IsMatch(function.Id) ? cls + (plane.SharedLayer ? "layer" : string.Empty) : string.Empty) +
                                 (function.Id == "trigger2ndstage" && Regex.IsMatch(command.Control ?? string.Empty, @"bomb|rocket|release|missile|launch|ffar|hvar", RegexOptions.IgnoreCase) ? "rel" : string.Empty) +
                                 // (owner 2026-11-16: a civil aircraft without weapons keeps its wheel brake on a grip button; in a plan
                                 // with combat aircraft their gun, release and flares take the head buttons and their brake moves to
                                 // the throttle hand - the two never share a demand)
                                 (function.Id == "wheelbrakes_button" && (!armed.Contains(command.Aircraft) || CivilSims.Contains(command.Game)) ? "civil" : string.Empty) +
                                 // (and a civil sim's incidental weapon commands never share a demand with the combat aircraft's: its wheel
                                 // brake may take their grip button back)
                                 (CivilSims.Contains(command.Game) && Regex.IsMatch(function.Id, "^(launchweapon|cms_fwd_start_program_or_just_flare|cms_aft_panic_or_just_chaff)$") ? "civil" : string.Empty);
                // (review 6 B6: IL-2's engine throttles never share a demand with another game's twin throttles - IL-2 keeps
                // its all-engines axis on the lever, the veto then pushed the F/A-18's left / right throttles off every lever
                // in the quick path's one plan for DCS and IL-2)
                string role = throttleRole(command);
                if (role.Length > 0 && command.Game.StartsWith("IL2", StringComparison.OrdinalIgnoreCase))
                    role += "il2";
                // (and in a plan for many games every other game's throttle axes are a demand of their own: one game files
                // its throttles under several slots - X-Plane's engine 1 and all-engines throttle - and in a shared demand it
                // glued the DCS warbirds' and helicopters' throttles to the jets' lever, where none of them fitted)
                if (function.IsAxis && ThrottleFunction.IsMatch(function.Id) && !string.Equals(command.Game, "DCS", StringComparison.OrdinalIgnoreCase) && games.Count > 2)
                    role += "|" + command.Game;
                string cluster = function.Id + "|" + handKey + "|" + realKey + "|" + (function.Id == "trigger1ststage" && cameraDetent(command.Aircraft) ? "cam" : string.Empty) + "|" + role + "|" + kindKey +
                                 // (snap views, countermeasures, gear and flaps per aircraft at every slider position: each finds an
                                 // input free for it, in a hangar of many modules too - one shared set for 20 modules found no input
                                 // free in all of them; free inputs alike still give them the same one)
                                 (function.Id.StartsWith("view_snap_", StringComparison.Ordinal) || PerAircraftFunction.IsMatch(function.Id) ? "|" + command.Game + "|" +
                                  // (the wheel brake button per module: both seats and the easy variant of one aircraft share it)
                                  (function.Id == "wheelbrakes_button" ? (plane.SharedLayer || !hangar ? "all" : SeatBase.Replace(command.Aircraft ?? string.Empty, string.Empty)) : command.Aircraft) : string.Empty);
                List<SetupCommand> list;
                if (!clusters.TryGetValue(cluster, out list))
                {
                    clusters[cluster] = list = new List<SetupCommand>();
                    clusterInfo[cluster] = new[] { function.Id, handKey, realKey, side, cluster.EndsWith("|cam|", StringComparison.Ordinal) || cluster.Contains("|cam|") ? "cam" : string.Empty, throttleRole(command) };
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
                        Side = RealOn(run, function) && info[3] != "panel" ? info[3] : function.Side,
                        CameraDetent = info[4] == "cam",
                        ThrottleRole = info[5]
                    };
                    if (function.Id == "trigger2ndstage")
                        demand.ReleaseStage = part.Value.Any(c => Regex.IsMatch(c.Control ?? string.Empty, @"bomb|rocket|release|missile|launch|ffar|hvar", RegexOptions.IgnoreCase));
                    if (demand.ThrottleRole.Length > 0)
                    {
                        // (review 7 M15: a civil sim's one configuration serves every aircraft, most of them single-engine -
                        // its all-engines throttle takes the lever, an engine-2 lever does nothing in a Cessna)
                        demand.SplitAllowed = part.Value.All(c => !c.Game.StartsWith("IL2", StringComparison.OrdinalIgnoreCase) && !CivilSims.Contains(c.Game ?? string.Empty));
                        demand.HasCombined = part.Value.All(c => request.Commands.Any(o => o.Game == c.Game && string.Equals(o.Aircraft, c.Aircraft, StringComparison.OrdinalIgnoreCase) && ThrottleRoleOf(o) == "C"));
                    }
                    foreach (SetupCommand command in part.Value)
                        demand.Nodes.Add(new SetupNode { Game = command.Game, Action = command.Action, Aircraft = command.Aircraft });
                    demand.Aircraft = part.Value.Select(c => c.Aircraft).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
                    string freq = part.Value.Select(c => c.Frequency).Where(f => f.Length > 0).OrderByDescending(SetupImportance.FrequencyRank).FirstOrDefault();
                    demand.Frequency = freq ?? function.Frequency;
                    // The fly-and-fight functions (gun, release, flares, brake button, PTT, gear, flaps) weigh at least what
                    // the function catalog gives them, whatever one aircraft's row says.
                    if (FlyFight(function.Id) && SetupImportance.FrequencyRank(function.Frequency) > SetupImportance.FrequencyRank(demand.Frequency))
                        demand.Frequency = function.Frequency;
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
                demand.DirectBits = Bits(run, demand.Aircraft, false);
                demand.ShareKey = ShareKey(demand.Function);
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
                // A fighter needs its wheel brakes, gear, flaps and trim as much as a trainer: no role discount for them.
                if (FlyFight(demand.FunctionId))
                    demand.RoleFactor = Math.Max(1.0, demand.RoleFactor);
                // A gamepad has no hat to spare: without head tracking its right stick looks around, weighted like the snap
                // views (the look axes outrank the TDC slew there).
                bool padLook = run.Pad && !options.HeadTracking && (demand.FunctionId == "view_look_horizontal" || demand.FunctionId == "view_look_vertical") &&
                               demand.Aircraft.Any(run.PadLookAircraft.Contains);
                if (padLook)
                {
                    demand.Frequency = "frequent";
                    demand.Crit = "instant";
                }
                demand.Importance = SetupImportance.CategoryWeight(demand.Category) * SetupImportance.FrequencyWeight(demand.Frequency) * (maxWeight + 0.15 * breadth) *
                                    demand.RoleFactor * SetupPhases.CritWeight(demand.Crit);
                // Looking around (snap views) is an in-flight essential without head tracking: it ranks with the sensor
                // functions of the grip, not with the camera modes of the view category.
                if (demand.FunctionId.StartsWith("view_snap_", StringComparison.Ordinal) || padLook)
                    demand.Importance *= SnapViewBoost;
                // A head tracker needs its recenter button (reachable, low priority).
                if (options.HeadTracking && demand.FunctionId == "view_center")
                    demand.Importance *= SnapViewBoost / 2;

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
            // A set needs only the members its game has: a lone member (one direction of a set, nothing else of it for its
            // aircraft) is a plain function and may take any free button.
            foreach (IGrouping<string, Demand> set in demands.Where(d => d.DirKey.Length > 0 && !d.IsAxis).GroupBy(d => d.DirKey, StringComparer.Ordinal))
            {
                Dictionary<string, int> perPlane = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (Demand demand in set)
                    foreach (string plane in demand.Aircraft.Distinct(StringComparer.OrdinalIgnoreCase))
                        perPlane[plane] = (perPlane.ContainsKey(plane) ? perPlane[plane] : 0) + 1;
                foreach (Demand demand in set)
                    demand.Lone = demand.Aircraft.All(plane => perPlane[plane] <= 1);
            }
            run.Demands = demands;
            foreach (IGrouping<string, Demand> set in demands.Where(d => d.DirKey.Length > 0 && !d.IsAxis).GroupBy(d => d.DirKey, StringComparer.Ordinal))
                run.SetMates[set.Key] = set.ToList();
        }

        // The function each command listed under several functions keeps: a confirmed catalog row before a new one, a
        // spaceship's own function in space games, the gun (one "Fire" for the gun and the weapons: Nuclear Option's goes on
        // the trigger), then the first by id. Latched pairs (both positions on one command) keep both.
        private static Dictionary<string, string> OneFunctionPerCommand(SetupRequest request, HashSet<string> games)
        {
            Dictionary<string, string> chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Func<string, int> rank = status =>
            {
                switch ((status ?? string.Empty).ToLowerInvariant())
                {
                    case "confirmed": return 0;
                    case "corrected": return 1;
                    case "new": return 2;
                    default: return 3;
                }
            };
            foreach (IGrouping<string, SetupCommand> group in request.Commands
                         .Where(c => games.Contains(c.Game) && !string.Equals(c.Status, "substitute", StringComparison.OrdinalIgnoreCase) && request.Functions.ContainsKey(c.Function))
                         .GroupBy(c => CommandKey(c.Game, c.Action, c.Aircraft), StringComparer.OrdinalIgnoreCase))
            {
                List<SetupCommand> list = group.GroupBy(c => c.Function, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                if (list.Count < 2)
                    continue;
                List<SetupFunction> functions = list.Select(c => request.Functions[c.Function]).ToList();
                if (functions.All(f => !f.IsAxis && f.PairGroup.Length > 0) && functions.Select(f => f.PairGroup).Distinct(StringComparer.Ordinal).Count() == 1)
                    continue;
                SetupAircraft plane;
                bool space = request.Aircraft.TryGetValue(list[0].Aircraft, out plane) && plane.Class == SetupClasses.Space;
                SetupCommand best = list.OrderBy(c => rank(c.Status))
                    .ThenBy(c => space && c.Function.StartsWith("space_", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(c => string.Equals(c.Function, "trigger1ststage", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(c => c.Function, StringComparer.Ordinal).First();
                chosen[group.Key] = best.Function;
            }
            return chosen;
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

        // The trigger's first stage of this fixed-wing aircraft is a detent before the gun (camera, sight recorder, laser:
        // F-16, A-10C, F-14, Mirage ...), not the gun; the gun is the second stage. The catalog's own description says so
        // (helicopters fire on the first detent: AH-64, OH-58).
        private static readonly Regex CameraDetentText = new Regex(@"first detent|1st detent|half action|sight recorder|gun camera", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static bool IsCameraDetent(SetupRequest request, string plane)
        {
            SetupAircraft known;
            if (request == null || !request.Aircraft.TryGetValue(plane ?? string.Empty, out known) || ClassOf(request, known) != SetupClasses.Fixed)
                return false;
            List<SetupCommand> first = request.Commands.Where(c => string.Equals(c.Aircraft, plane, StringComparison.OrdinalIgnoreCase) && c.Function == "trigger1ststage").ToList();
            return first.Count > 0 && first.All(c => CameraDetentText.IsMatch(c.Control)) &&
                   request.Commands.Any(c => string.Equals(c.Aircraft, plane, StringComparison.OrdinalIgnoreCase) && c.Function == "trigger2ndstage");
        }

        // An engine throttle command's side: L (left / engine 1), R (right / engine 2), C (both / all engines); empty for
        // the other throttles (a collective, a single engine's throttle is decided by the aircraft: see BuildDemands).
        internal static string ThrottleRoleOf(SetupCommand command)
        {
            string f = command.Function ?? string.Empty;
            if (!ThrottleFunction.IsMatch(f))
                return string.Empty;
            string text = command.Control ?? string.Empty;
            if (CollectiveText.IsMatch(text) && !Regex.IsMatch(text, @"throttle", RegexOptions.IgnoreCase))
                return string.Empty;
            if (f == "throttlef1" || f == "throttlejeffmirage21" || CombinedThrottleText.IsMatch(text))
                return "C";
            return f.IndexOf("left", StringComparison.Ordinal) >= 0 ? "L" : f.IndexOf("right", StringComparison.Ordinal) >= 0 ? "R" : string.Empty;
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

        private static ulong[] Bits(Run run, List<string> aircraft, bool layers = true)
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
                if (layers && run.Request.Aircraft.TryGetValue(key, out plane) && plane.SharedLayer)
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

        // Two functions that must not share an input: an aircraft in common, unless they meet only through a DCS input
        // layer (Default, NS430 ...) and do the same thing there (the same function, or the same kind of axis: the layer's
        // throttle and the aircraft's throttle on one lever).
        internal static bool Conflict(Demand a, Demand b)
        {
            if (!Overlaps(a.Bits, b.Bits))
                return false;
            if (a.DirectBits == null || b.DirectBits == null || Overlaps(a.DirectBits, b.DirectBits))
                return true;
            return !Compatible(a.ShareKey, b.ShareKey);
        }

        // "fn:<function id>" for every function; axes of the function map's kinds "ax:<map function>|<direction>|<inv>"
        // (rudder and the layer's rudder, the throttle and the layer's throttle). Space functions stay by themselves.
        public static string ShareKey(SetupFunction function)
        {
            if (function == null || string.IsNullOrEmpty(function.Id))
                return string.Empty;
            if (function.IsAxis && function.MapFunction.Length > 0 && function.MapDir.IndexOf('@') < 0 && !function.Id.StartsWith("space_", StringComparison.OrdinalIgnoreCase))
                return "ax:" + function.MapFunction + "|" + function.MapDir + "|" + (function.Id.EndsWith("inv", StringComparison.OrdinalIgnoreCase) ? "inv" : string.Empty);
            return "fn:" + function.Id;
        }

        // Whether two share keys do the same thing: the same function, or axes of one kind and direction (an axis without
        // a direction fits both sides, left and right of one kind fit each other), never a normal and an inverted one.
        public static bool Compatible(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!a.StartsWith("ax:", StringComparison.Ordinal) || !b.StartsWith("ax:", StringComparison.Ordinal))
                return false;
            string[] x = a.Substring(3).Split('|');
            string[] y = b.Substring(3).Split('|');
            if (x.Length < 3 || y.Length < 3 || !string.Equals(x[0], y[0], StringComparison.OrdinalIgnoreCase) || !string.Equals(x[2], y[2], StringComparison.OrdinalIgnoreCase))
                return false;
            return x[1] == y[1] || x[1].Length == 0 || y[1].Length == 0;
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
            // (a shift button is held by hand: only a momentary push button - never a lever detent, an axis zone button, a
            // latched switch, a selector position or a control whose printed label names a function)
            Func<Slot, bool> candidate = s => s.Layer == 0 && !s.Input.IsAxis && ModifierInput(s.Input) && !modifiers.Any(m => m.Device == s.Device.Device && m.Input == s.Input.Input);
            foreach (SetupModifierChoice existing in request.ExistingModifiers.OrderByDescending(m => m.Uses).ThenBy(m => m.Name, StringComparer.Ordinal))
            {
                if (modifiers.Count(m => !m.Switch) >= layers)
                    break;
                Slot position = slots.FirstOrDefault(s => candidate(s) && s.Device.Device == existing.Device && string.Equals(s.Input.Input, existing.Input, StringComparison.OrdinalIgnoreCase) && s.Grip);
                if (position != null)
                    modifiers.Add(new SetupPlanModifier { Name = existing.Name, Device = position.Device.Device, Input = position.Input.Input, Control = position.Control, Existing = true, Reason = "your own modifier" });
            }
            // (A yoke's and a quadrant's buttons come last: they have no pinky.)
            // (L22, review 5 M4: a shift is held while the other hand presses the shifted button - on a grip: the pinky, a
            // throttle grip button; a stick's base button only on a stick without a throttle, where the left hand is free)
            string[] order = { "stick.pinky.lever", "stick.pinky.button", "stick.button.index_lower", "throttle.pinky.lever", "throttle.pinky.button", "throttle.pinky.fwd", "throttle.button.*", "stick.button.index.*",
                               "yoke.button.*", "quadrant.button.*", "stick.base.button.*", "throttle.base.button.*" };
            bool leftHandBusy = request.Devices.Any(d => SetupRoles.Binds(d.Role) && (d.Role == SetupRoles.Throttle || d.Role == SetupRoles.Collective || d.Role == SetupRoles.LeftStick));
            // (a stick with three grip push buttons or fewer besides the trigger - TCA sidestick, T.16000M, Extreme 3D - needs
            // them for the weapon release, the countermeasures and the wheel brake button: its index button is no shift there)
            Func<SetupDevice, bool> fewGripButtons = device => device != null &&
                slots.Count(s => s.Device.Device == device.Device && s.Layer == 0 && s.Grip && !s.Input.IsAxis && ModifierInput(s.Input) &&
                                 !s.Control.Contains(".trigger.") && !HatControl.IsMatch(s.Control)) <= 3;
            foreach (string pattern in order)
            {
                // (a left stick is the left hand's device too: its grip buttons hold a shift like a throttle's)
                foreach (Slot slot in slots.Where(s => candidate(s) && (s.Role == SetupRoles.MainStick || s.Role == SetupRoles.Throttle || s.Role == SetupRoles.HeliStick || (s.Role == SetupRoles.LeftStick && s.Grip)))
                             .OrderBy(s => main != null && s.Device.Device == main.Device ? 0 : 1).ThenByDescending(s => InputOrder(s.Input.Input)).ToList())
                {
                    if (modifiers.Count(m => !m.Switch) >= layers)
                        break;
                    if (!candidate(slot) || !PatternMatch(run, pattern, slot.RuleControl))
                        continue;
                    // (review 7 B13: none of them but the pinky, the classic shift - the index_lower of a T.Flight Stick X is its only
                    // grip push button besides the trigger; the release needs it, the shift goes to the base)
                    if (slot.Grip && (slot.Role == SetupRoles.MainStick || slot.Role == SetupRoles.HeliStick) && slot.Control.IndexOf(".pinky.", StringComparison.Ordinal) < 0 && fewGripButtons(slot.Device))
                        continue;
                    if (pattern == "stick.base.button.*" && leftHandBusy)
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
            foreach (Slot slot in slots.Where(s => s.Layer == 0))
            {
                if (slot.Control == "stick.trigger.stage2" || slot.Control == "yoke.trigger.stage2")
                    run.TwoStage.Add(slot.Device.Device);
                if (slot.Control.EndsWith(".trigger.stage1", StringComparison.Ordinal))
                    run.WithTrigger.Add(slot.Device.Device);
                if (slot.Input.IsAxis && slot.Control == "pedals.axis.rudder")
                    run.PedalRudder = true;
            }
            run.OneHat = slots.Where(s => s.Layer == 0 && s.Grip && !s.Input.IsAxis && HatControl.IsMatch(s.RuleControl) &&
                                          (s.Role == SetupRoles.MainStick || s.Role == SetupRoles.Throttle || s.Role == SetupRoles.HeliStick || s.Role == SetupRoles.Collective || s.Role == SetupRoles.LeftStick))
                              .Select(s => s.Device.Device + "|" + Prefix(s.RuleControl)).Distinct(StringComparer.Ordinal).Count() <= 1;
            run.CollectiveLever = slots.Any(s => s.Layer == 0 && s.Input.IsAxis && (s.Control == "collective.axis.main" || s.Control == "collective.axis.throttle_twist"));
            foreach (IGrouping<string, Slot> device in slots.Where(s => s.Layer == 0 && s.Input.IsAxis && LeverAxis.IsMatch(s.Control)).GroupBy(s => s.Device.Device))
            {
                run.AnyLever = true;
                if (device.Select(s => s.Input.Input).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2)
                    run.TwinLevers = true;
            }
            // No panel and (almost) no base buttons (a gamepad, a small HOTAS): the cockpit's switch functions (gear, flaps
            // ...) may use the grips, else they would have no place at all.
            HashSet<string> modifierInputs = new HashSet<string>(modifiers.Where(m => !m.Switch).Select(m => m.Device + "|" + m.Input), StringComparer.OrdinalIgnoreCase);
            run.GripsForPanel = slots.Count(s => s.Layer == 0 && !s.Grip && !s.Input.IsAxis && s.Device.Role != SetupRoles.Pedals &&
                                                 !modifierInputs.Contains(s.Device.Device + "|" + s.Input.Input)) < 4;
            // Gear and flaps (a pilot must be able to work them) may use the grips when the base buttons free for any function
            // are almost none: a latched switch, a lever detent, a switch with a cockpit label of its own - the Warthog
            // throttle's FUEL FLOW, EAC ARM, APU START - and an MFD key take no gear toggle.
            run.GripsForGearFlaps = run.GripsForPanel || slots.Count(s => s.Layer == 0 && !s.Grip && !s.Input.IsAxis && s.Device.Role != SetupRoles.Pedals &&
                                                 !modifierInputs.Contains(s.Device.Device + "|" + s.Input.Input) &&
                                                 !string.Equals(s.Input.Type, "latched", StringComparison.OrdinalIgnoreCase) &&
                                                 !string.Equals(s.Input.Type, "detent", StringComparison.OrdinalIgnoreCase) &&
                                                 !DetentControl.IsMatch(s.Control) && !SetupPhases.IsCockpitLabel(s.Input.Label) &&
                                                 !s.MfdDevice && !SetupPhases.IsMfdControl(s.Control)) < 4;
        }

        // An input JoyPro may hold as a shift button: momentary, no detent / zone / selector, no printed function label
        // (a label saying SHIFT or MODIFIER is fine).
        internal static bool ModifierInput(SetupInput input)
        {
            if (input == null || input.IsAxis)
                return false;
            string type = (input.Type ?? string.Empty).Trim();
            if (type.Length > 0 && !string.Equals(type, "momentary", StringComparison.OrdinalIgnoreCase))
                return false;
            string label = input.Label ?? string.Empty;
            if (DetentControl.IsMatch(input.Control ?? string.Empty) || DetentLabel.IsMatch(label) || Regex.IsMatch(label, @"\bzone\b|virtual button|reverser", RegexOptions.IgnoreCase))
                return false;
            return Regex.IsMatch(label, @"\bshift\b|modifier", RegexOptions.IgnoreCase) || !FunctionCockpitLabel(label);
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
            else if (string.Equals(device.Kind, "gamepad", StringComparison.OrdinalIgnoreCase) && slot.Control == "throttle.axis.ministick.y")
            {
                // A gamepad's right stick up / down is its throttle where the game has no throttle buttons.
                slot.Names.Add("throttle.axis.main");
            }
            else if (slot.Control.StartsWith("yoke.", StringComparison.Ordinal))
            {
                // A yoke is the right hand's grip: the stick's positions fit its hats and buttons (CMS, TMS, trim ...).
                slot.Names.Add("stick." + slot.Control.Substring("yoke.".Length));
            }
            else if (slot.Control.StartsWith("quadrant.", StringComparison.Ordinal))
            {
                // A throttle quadrant is the left hand's: the throttle's positions fit its levers and buttons.
                slot.Names.Add("throttle." + slot.Control.Substring("quadrant.".Length));
            }
            string family = Family(slot.RuleControl);
            slot.Family = family;
            slot.Grip = IsGrip(slot.RuleControl);
            // The real-position match ("Where should functions go?"): the device catalog's HOTAS equivalence where it has
            // one (a T.16000M's only hat is the trim hat in DCS's own default profile, whatever its control name says).
            slot.RealNames = input.Hotas != null && input.Hotas.Count > 0 ? input.Hotas.ToList() : slot.Names;
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
                        if (slot.RealNames.Any(n => SamePosition(n, control)))
                            m = Math.Max(m, 1);
                        else if (slot.RealNames.Any(n => string.Equals(Prefix(n), Prefix(control), StringComparison.OrdinalIgnoreCase)))
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

        // One position in both vocabularies: the cockpit database writes a single button with ".press" (the Hornet's
        // "stick.weapon_release.press"), the device catalog without; a single-stage "trigger" is either stage of a device
        // trigger.
        internal static bool SamePosition(string device, string real)
        {
            string a = PressLess(device), b = PressLess(real);
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;
            return Regex.IsMatch(b, @"^(stick|yoke)\.trigger$", RegexOptions.IgnoreCase) && a.StartsWith(b + ".stage", StringComparison.OrdinalIgnoreCase);
        }

        private static string PressLess(string control)
        {
            string c = control ?? string.Empty;
            // (a hat's own press direction stays: "stick.hat.cms.press" is not "stick.hat.cms")
            return c.EndsWith(".press", StringComparison.OrdinalIgnoreCase) && c.IndexOf(".hat.", StringComparison.OrdinalIgnoreCase) < 0 && c.IndexOf(".coolie", StringComparison.OrdinalIgnoreCase) < 0
                ? c.Substring(0, c.Length - ".press".Length) : c;
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
            bool realExact = RealOn(run, demand.Function) && real >= 0.99 && demand.Real.All(g => g.Controls != null);
            string control = slot.RuleControl;
            string family = slot.Family;
            bool grip = slot.Grip;
            string side = demand.Side ?? "any";

            // A directional set's member only on the matching direction of a hat or thumb stick (never on another hat's press
            // or a plain button as a last resort).
            if (demand.DirRole.Length > 0 && !style && !SetupPhases.MatchesDirection(demand.DirRole, slot.RuleControl) && !SetupPhases.MatchesDirection(demand.DirRole, slot.Control) &&
                !((NoLayer.Contains(demand.FunctionId) || demand.Lone) && slot.Grip && !slot.Input.IsAxis && !HatControl.IsMatch(control) && Regex.IsMatch(control, @"\.button\.")) &&
                !(demand.AnyDirection && slot.Grip && !slot.Input.IsAxis))
                return Forbidden;

            if (!style && Veto(run, demand, slot))
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
                // A cockpit label on a base (FUEL FLOW L, EAC ARM, RDR ALTM NRM ...) is that switch's function: another
                // function never goes there, the switch stays free (walk 2026-11-14: rudder trim on FUEL FLOW, lights on
                // LDG GEAR WARN SILENCE).
                if (OtherBaseLabel(function, slot, control, exactPref, mfdFunction))
                    return Forbidden;
                if (!grip && (family == "panel" || family == "mfd") && !mfdFunction && !demand.IgnorePanelLabels)
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
                if ((Dedicated.IsMatch(control) && !demand.IgnorePanelLabels) || (demand.IsAxis && DedicatedAxis.IsMatch(control)))
                    return Forbidden;
                // Pitch, roll and yaw spring back to the middle: never on a lever, slider, wheel or knob that stays where
                // it is left (rudder on a quadrant lever or the flaps lever).
                if (demand.IsAxis && CenteringAxis.IsMatch(demand.FunctionId) && NonCentering.IsMatch(control))
                    return Forbidden;
                // Toe brakes only on brake axes (pedals, a brake lever): a slider or knob left forward brakes the whole
                // flight. Without them the wheel brake button does it.
                if (demand.IsAxis && ToeBrake.IsMatch(demand.FunctionId) && !BrakeAxis.IsMatch(control))
                    return Forbidden;
                if (function.Prefs.Count > 0 && function.Prefs.All(p => p.StartsWith("mfd.", StringComparison.Ordinal)))
                    return Forbidden;
                // (Gear and flaps may take a grip button behind a shift button when the panel buttons are used up: a pilot
                // must be able to work them.)
                // (Gear up / down and flaps up / down as a pair on an unlabelled latched switch of a grip - the Warthog's pinky or
                // boat switch - when the base has none.)
                if (grip && (side == "panel" || side == "any") && (slot.Layer > 0 || !NumberedThrottleButton.IsMatch(control)) && !run.GripsForPanel &&
                    !(run.GripsForGearFlaps && GearFlaps.IsMatch(demand.FunctionId)) &&
                    !(slot.Layer > 0 && slot.Phase.Length == 0 && GearFlaps.IsMatch(demand.FunctionId)) &&
                    !(slot.Layer == 0 && GearFlaps.IsMatch(demand.FunctionId) && function.PairGroup.Length > 0 &&
                      string.Equals(slot.Input.Type, "latched", StringComparison.OrdinalIgnoreCase) && !SetupPhases.IsFunctionLabel(slot.Input.Label)))
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
            if (slot.Input.Type == "latched" && !LatchedFits(function, slot.Input))
                comfort -= 0.35 * (0.5 + f);
            if (grip && f <= 0.2 && fit < 0.5)
                comfort -= 0.2;
            if (style)
                comfort += 4 + (demand.StyleShifted == (slot.Layer > 0) ? 1 : 0);
            return demand.Importance * (comfort + run.Realism * RealismWeight * real);
        }


        // What a pilot would never accept, wherever the scores point (docs/auto-setup-eval.md#hard-gates):
        // - the gun, weapon release, countermeasures, the brake button and PTT behind a shift button or a mode-switch layer,
        //   and no time-critical function behind a shift button;
        // - the camera / first detent of an F-16 or A-10C trigger on a single-stage trigger (the gun goes there);
        // - a twin's engines on a knob, wheel or thumb stick, or split over one lever and something else (one lever: the
        //   both-engines throttle; two levers: left and right), any throttle on a knob while a lever exists;
        // - the rudder elsewhere than on the pedals when there are pedals;
        // - a lever detent for anything but the lever's own functions; engine start / stop on a grip hat;
        // - a control whose printed label names another function (PTT, TRIM, ENG OPER L ...).
        private static bool Veto(Run run, Demand demand, Slot slot)
        {
            string id = demand.FunctionId ?? string.Empty;
            string control = slot.Control;
            bool style = demand.StyleDevice.Length > 0 && demand.StyleDevice == slot.Device.Device && string.Equals(demand.StyleInput, slot.Input.Input, StringComparison.OrdinalIgnoreCase);
            // (looking around behind a shift button is slow but far better than none: a small device's snap views go there
            // when no hat is free)
            if (slot.Layer > 0 && (NoLayer.Contains(id) || (slot.Phase.Length == 0 && SetupPhases.NormalizeCrit(demand.Crit) == "instant" && demand.DirKey != "snapview")))
                return true;
            // Trim is flown all the time: never behind a shift button (the A-10C, a warbird).
            // (on a device with one grip hat - a gamepad whose D-pad carries the throttle buttons - behind the shift is
            // better than no trim at all)
            if (slot.Layer > 0 && slot.Phase.Length == 0 && TrimFunction.IsMatch(id) && !run.OneHat)
                return true;
            // A camera / laser first detent only on the first stage of a two-stage trigger: elsewhere it would take a button
            // the gun needs (a yoke without a trigger).
            if (demand.CameraDetent && !(Regex.IsMatch(control, @"^(stick|yoke)\.trigger\.stage1$") && run.TwoStage.Contains(slot.Device.Device)))
                return true;
            // A single-stage trigger is the gun's: no camera / laser detent and nothing else of an aircraft that has a gun.
            if (Regex.IsMatch(control, @"^(stick|yoke)\.trigger\.stage1$") && !run.TwoStage.Contains(slot.Device.Device) &&
                (demand.CameraDetent || demand.ReleaseStage || (id != "trigger1ststage" && id != "trigger2ndstage" && demand.Aircraft.Any(run.GunAircraft.Contains))))
                return true;
            // Both stages of a two-stage trigger are the trigger functions' too (the gun, the camera / laser first detent of
            // the aircraft that have one): no radio / ICS switch (the Mi-8's real cyclic trigger) or other function of an
            // aircraft with a gun, at any slider position.
            if (Regex.IsMatch(control, @"^(stick|yoke)\.trigger\.stage[12]$") && run.TwoStage.Contains(slot.Device.Device) &&
                id != "trigger1ststage" && id != "trigger2ndstage" && demand.Aircraft.Any(run.GunAircraft.Contains) &&
                !(id == "laser_1nd_detent" && control.EndsWith(".stage1", StringComparison.Ordinal) && demand.Aircraft.All(p => IsCameraDetent(run.Request, p))))
                return true;
            // A yoke without a trigger: its first handle button is the most trigger-like and the gun's (F-4E on a yoke), unless
            // its printed label names another function (PTT).
            if (!demand.IsAxis && control == "yoke.button.1" && !run.WithTrigger.Contains(slot.Device.Device) && !YokeButtonFunctionLabel.IsMatch(slot.Input.Label ?? string.Empty) &&
                id != "trigger1ststage" && id != "trigger2ndstage" && demand.Aircraft.Any(run.GunAircraft.Contains))
                return true;
            if (demand.IsAxis && ThrottleFunction.IsMatch(id))
            {
                bool lever = LeverAxis.IsMatch(slot.RuleControl) || LeverAxis.IsMatch(control);
                if (demand.ThrottleRole == "H")
                    return lever || control.StartsWith("collective.axis.main", StringComparison.Ordinal) || CenteringStickAxis(control) ||
                           string.Equals(slot.Input.Type, CentringType, StringComparison.OrdinalIgnoreCase) || (!style && FlapsLabel.IsMatch(slot.Input.Label ?? string.Empty));
                if (demand.ThrottleRole == "L" || demand.ThrottleRole == "R")
                {
                    if (!lever || (demand.HasCombined && (!run.TwinLevers || !demand.SplitAllowed)))
                        return true;
                }
                else if (demand.ThrottleRole == "C" && run.TwinLevers && demand.SplitAllowed)
                {
                    return true;
                }
                // (a collective's twist grip / knob is a helicopter's throttle, never a fixed-wing one while a lever - the
                // collective itself - exists: review 6 M8, Spitfire / IL-2 / MSFS throttle on the Ka-50 collective's rotary)
                if ((run.AnyLever || (run.CollectiveLever && !demand.Classes.Contains(SetupClasses.Rotary))) && (KnobAxis.IsMatch(control) || SliderAxis.IsMatch(control)) && slot.Role != SetupRoles.LeftStick &&
                    (!control.StartsWith("collective.", StringComparison.Ordinal) || !demand.Classes.Contains(SetupClasses.Rotary)))
                    return true;
                // A throttle never on a spring-centred thumb stick or twist (it would snap back to half power) - nor on a left
                // stick's spring-return Y, X or twist (review 7 M10, owner: no throttle on a spring axis in any game; its
                // slider / wheel is the accepted place).
                // (a gamepad's right stick only where the game has no throttle buttons for the aircraft)
                if ((Regex.IsMatch(control, @"\.axis\.(ministick2?\.[xy]|twist)$") || string.Equals(slot.Input.Type, CentringType, StringComparison.OrdinalIgnoreCase) ||
                     (slot.Role == SetupRoles.LeftStick && Regex.IsMatch(control, @"^stick\.axis\.(pitch|roll|twist)$"))) &&
                    !(string.Equals(slot.Device.Kind, "gamepad", StringComparison.OrdinalIgnoreCase) && control == "throttle.axis.ministick.y" && !ThrottleButtons(run, demand)))
                    return true;
            }
            // Space strafe by its direction: forward / aft thrust never on a sideways or twisting axis (AheadThrust on a thumb
            // stick's X), sideways never on a forward / back axis, up / down never on a sideways one.
            if (demand.IsAxis && id.StartsWith("space_strafe_", StringComparison.Ordinal))
            {
                bool sideways = Regex.IsMatch(control, @"\.x$|\.axis\.(roll|twist|rudder)$");
                bool forwardBack = Regex.IsMatch(control, @"\.y$|\.axis\.pitch$") || LeverAxis.IsMatch(control);
                if ((id == "space_strafe_fwdaft" && (sideways || control.EndsWith(".axis.twist", StringComparison.Ordinal))) ||
                    (id == "space_strafe_hor" && forwardBack) || (id == "space_strafe_ver" && Regex.IsMatch(control, @"\.x$|\.axis\.roll$")))
                    return true;
            }
            if (PadLookStick(run, demand, slot))
                return true;
            // (owner 2026-11-16: RPM, mixture and condition levers stay where they are set too - never on a twist, rudder,
            // rocker or the axes of a stick; the throttles have their own rule above)
            if (demand.IsAxis && !ThrottleFunction.IsMatch(id) && StayAxisFunction.IsMatch(id) && !style &&
                (Regex.IsMatch(control, @"\.axis\.(twist|rudder|roll|pitch|rocker|ministick2?\.[xy])$") || string.Equals(slot.Input.Type, CentringType, StringComparison.OrdinalIgnoreCase)))
                return true;
            // A centred rate function (TDC slew, strafe sideways / up-down, head look) only on a centring axis.
            if (demand.IsAxis && CentredFunction.IsMatch(id) && !CentringAxis(control) && !CentringAxis(slot.RuleControl) && !CentringAxis(slot.Input))
                return true;
            if (demand.IsAxis && id == "rudder" && run.PedalRudder && control != "pedals.axis.rudder" && (demand.Classes.Contains(SetupClasses.Fixed) || demand.Classes.Contains(SetupClasses.Rotary)))
                return true;
            // A lever printed "Flaps" (Virpil CM3 slider, the Logitech quadrant's flaps lever) is the flaps' own: no RPM,
            // zoom or lights there.
            if (demand.IsAxis && !style && FlapsLabel.IsMatch(slot.Input.Label ?? string.Empty) && id.IndexOf("flap", StringComparison.OrdinalIgnoreCase) < 0)
                return true;
            // (review 6 m3: a lever the catalog names the throttle - the Logitech yoke's "Z axis lever (throttle)" - holds a
            // throttle, collective or engine power, never instrument lights, the HMD or the nose-wheel steering)
            if (demand.IsAxis && !style && Regex.IsMatch(slot.Input.Label ?? string.Empty, @"\bthrottle\b", RegexOptions.IgnoreCase) && LeverAxis.IsMatch(slot.RuleControl) &&
                !ThrottleFunction.IsMatch(id) && !Regex.IsMatch(id, "throttle|collective|power|thrust|space_strafe_fwdaft", RegexOptions.IgnoreCase))
                return true;
            if (!demand.IsAxis)
            {
                // A printed gear lever (the Bravo's, a heli's Echo / WINGFLEX lever) is the gear's: no landing lights there
                // (lever up = lights on would be backwards), nothing else.
                if (!style && GearLever(slot) && !GearFlaps.IsMatch(id) && !Regex.IsMatch(id, "^gear", RegexOptions.IgnoreCase))
                    return true;
                // Gear on a grip only behind a shift button: one slip of the thumb at 400 kt would overspeed it (the boat
                // switch, a pad's face button, a front trigger) - a base switch or the gear lever unshifted.
                // (the stick's grip - the hand that flies - and a latched grip switch such as the Warthog's boat switch; a
                // plain button of the throttle grip stays fine)
                if (!style && Regex.IsMatch(id, "^(gear_toggle|gearin|gearout)$") && slot.Grip && slot.Layer == 0 && demand.ShiftOk && !GearLever(slot) &&
                    (slot.Role == SetupRoles.MainStick || slot.Role == SetupRoles.HeliStick || slot.Control.IndexOf(".trigger", StringComparison.Ordinal) >= 0 ||
                     string.Equals(slot.Input.Type, "latched", StringComparison.OrdinalIgnoreCase)))
                    return true;
                // (owner 2026-11-16: the gear never on the press of an analog thumb stick or slew control, on any layer - it is
                // pressed by accident while slewing; the china hat and plain throttle buttons stay allowed)
                if (!style && Regex.IsMatch(id, "^(gear_toggle|gearin|gearout)$") && Regex.IsMatch(slot.Control, @"\.(ministick2?|thumbstick|slew)\.press$"))
                    return true;
                // An up / down / left / right function of a set never on the opposite direction of a hat, coolie, china hat or
                // thumb stick (radar elevation down on the coolie's UP).
                if (!style && OppositeHatDirection(demand.Function, control))
                    return true;
                // A device JoyPro does not know: its buttons may be lever detents or switches of something else, so nothing
                // that cannot be undone (jettison, eject, canopy, engine stop ...) goes there.
                if (slot.Device.Generic && Irreversible.IsMatch(id))
                    return true;
                bool zone = string.Equals(slot.Input.Type, "detent", StringComparison.OrdinalIgnoreCase) && !DetentControl.IsMatch(control);
                bool detent = DetentControl.IsMatch(control) || DetentLabel.IsMatch(slot.Input.Label ?? string.Empty) || zone;
                // A zone button of an axis (the TCA's reverser zone in the first 20 % of the slider) fires whenever the
                // axis passes it, whatever the axis does: nothing goes there. Nor on a position of a mode / profile
                // selector (the X56 MODE wheel M1 / M2 / S1): one of them is always held.
                if (zone || IsSelector(slot.Input))
                    return true;
                if (detent)
                {
                    // A lever detent only for the lever's own functions; engine functions only as a one-way stop / cut-off
                    // on the lever's own OFF / CUTOFF detent - never a start / stop toggle, never IDLE or REV IDLE.
                    if (!LeverOwn(id))
                        return true;
                    // (afterburner / TOGA / WEP only on a detent known to be at the top of the travel, never on a reverse or
                    // below-idle detent)
                    if (TopEnd(id) && !TopDetent(control, slot.Input.Label))
                        return true;
                    if (EngineStartStop.IsMatch(id) && !CutoffOnOwnLever(run, demand, slot))
                        return true;
                    if (Irreversible.IsMatch(id) && !EngineStartStop.IsMatch(id))
                        return true;
                    return LabelTaken(run, demand, slot);
                }
                // Canopy, tail hook and refuel probe only on a switch whose printed label names them (review 5 m1): an unlabelled
                // switch in reach of the left hand would open the canopy in flight.
                if (!style && LabelOnlyFunction.IsMatch(id) && string.Equals(slot.Input.Type, "latched", StringComparison.OrdinalIgnoreCase) && !SetupPhases.LabelNames(demand.Function, slot.Input.Label ?? string.Empty))
                    return true;
                // A one-way engine start / stop never on the centre of a 3-way switch, a position labelled for another system
                // (BAT center), ENG CRANK for a stop, or any position of a 3-way switch not labelled for the engine (B5).
                if (!style && EngineSwitchMisuse(id, slot.Input, run.Request.Inputs).Length > 0)
                    return true;
                // A latched switch (it stays where it is put) only for a function with an ON / OFF pair (gear up / down,
                // lights on / off): a held function would be held all flight, a one-shot or toggle fires at every flip.
                if (string.Equals(slot.Input.Type, "latched", StringComparison.OrdinalIgnoreCase) && !LatchedFits(demand.Function, slot.Input) && !style)
                    return true;
                // An engine start / stop TOGGLE (IL-2's "Engine 1 start/stop") never on one position of a latched switch:
                // every second flip would stop the engine (only a one-way start or stop takes a switch position; a switch
                // labelled for the engine - ENG 1 MASTER - carries the toggle on both positions, in step with the engine).
                // (and no engine master / starter / magneto toggle or held starter there either, labelled or not: a toggle on
                // a latched switch gets out of step after one missed flip - the game's ON / OFF commands or a push button
                // instead - and a held starter stays engaged as long as the switch is up)
                if (string.Equals(slot.Input.Type, "latched", StringComparison.OrdinalIgnoreCase) && EngineStartStop.IsMatch(id) && !style && EngineToggle(run, demand))
                    return true;
                // (review 7 B14: any command whose game title calls it a toggle - IL-2 Cliffs of Dover's "Engine start/stop" behind
                // the id pilot.enginei01 - never on a latched position either: it gets out of step after one flip)
                if (string.Equals(slot.Input.Type, "latched", StringComparison.OrdinalIgnoreCase) && !style && TitleToggle(run, demand))
                    return true;
                // Eject, jettison, canopy, engine start / stop: only on a switch whose printed label names it, or as a pair
                // position (start / stop, open / close) on a latched switch - never on a plain push button (one press).
                if (!slot.Grip && Irreversible.IsMatch(id) && !SetupPhases.LabelNames(demand.Function, slot.Input.Label ?? string.Empty) &&
                    !(string.Equals(slot.Input.Type, "latched", StringComparison.OrdinalIgnoreCase) && demand.Function != null && demand.Function.PairGroup.Length > 0))
                    return true;
                // Nothing that cannot be undone, and nothing for the ground crew (crit idle: engine start / stop, APU,
                // canopy), on a grip - trigger, button, ministick, scroll wheel or hat, on any layer: a base, a guarded
                // switch or nothing.
                if (slot.Grip && (Irreversible.IsMatch(id) || SetupPhases.NormalizeCrit(demand.Crit) == "idle"))
                    return true;
                // A knob's click (rotary / encoder cw / ccw) only for a function that steps up and down.
                if (Encoder.IsMatch(control) && !Stepping.IsMatch(id))
                    return true;
                if (LabelTaken(run, demand, slot))
                    return true;
                // (review 7 M16) A printed PTT button holds the game's push to talk / radio transmit or stays empty: the comm
                // switch functions it shares a label keyword with also carry IFF, transponder ident and radar filter commands
                // (War Thunder's "Next IFF radar filter" on the YOKO Neo's PTT).
                if (!style && PttLabel.IsMatch(slot.Input.Label ?? string.Empty) && NotVoice(run, demand))
                    return true;
            }
            return false;
        }

        private static readonly Regex PttLabel = new Regex(@"\bPTT\b|push.to.talk", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NoVoiceText = new Regex(@"\bIFF\b|\bident\b|transponder|xpndr|squawk|interrogat|radar", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // One of its commands is an IFF, transponder or radar command, not a voice / radio transmit (title, catalog text, id).
        private static bool NotVoice(Run run, Demand demand)
        {
            return demand.Nodes.Any(node => NoVoiceText.IsMatch(node.Action ?? string.Empty) ||
                                            run.Request.Commands.Any(c => c.Game == node.Game && c.Action == node.Action && string.Equals(c.Aircraft, node.Aircraft, StringComparison.OrdinalIgnoreCase) &&
                                                                          (NoVoiceText.IsMatch(c.Title ?? string.Empty) || NoVoiceText.IsMatch(c.Control ?? string.Empty))));
        }

        private static readonly Regex FlapsLabel = new Regex(@"\bflaps?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A printed gear lever or gear switch (control "lever.gear", label naming the gear).
        private static bool GearLever(Slot slot)
        {
            return slot.Control.IndexOf(".lever.gear", StringComparison.Ordinal) >= 0 || (Regex.IsMatch(slot.Input.Label ?? string.Empty, @"\b(gear|ldg gear|landing gear)\b", RegexOptions.IgnoreCase) &&
                                                                                       !Regex.IsMatch(slot.Input.Label ?? string.Empty, @"warn|silence|horn|test", RegexOptions.IgnoreCase));
        }

        // A directional set member of the fly-and-fight functions (CMS forward / aft) on a plain grip button: the planner's
        // last resort when no hat is free (S, the directional set rule).
        internal static bool LastResortButton(string functionId, string control)
        {
            return NoLayer.Contains(functionId ?? string.Empty) && IsGrip(control) && !HatControl.IsMatch(control ?? string.Empty) && Regex.IsMatch(control ?? string.Empty, @"\.button\.");
        }

        // What a latched switch (it stays where it is put) may hold: a position of an ON / OFF pair (gear up / down, lights on /
        // off), or a function its printed label names in the label's own direction (the Warthog's FLAPS UP / DN switch: flaps
        // up / down) - never a toggle, a held or a one-shot function of something else.
        internal static bool LatchedFits(SetupFunction function, SetupInput input)
        {
            if (function == null)
                return false;
            string label = input == null ? string.Empty : input.Label ?? string.Empty;
            // (a latched push button - the TCA's ENG 1 MASTER - is pressed while ON: an OFF / stop function never goes there,
            // it would fire when the switch is turned on)
            string control = input == null ? string.Empty : input.Control ?? string.Empty;
            if (SetupPhases.LabelDirection(label).Length == 0 && !Regex.IsMatch(control, @"\.(up|down|fwd|aft|left|right|center|centre|pos\.\d+)$") &&
                SetupPhases.FunctionDirection(function.Id, function.PairRole) == "off")
                return false;
            // (a printed function label owns the switch: the Warthog's SPEED BRAKE is no gear switch, whatever pair fits)
            if (function.PairGroup.Length > 0)
                return !FunctionCockpitLabel(label) || SetupPhases.LabelNames(function, label);
            // (on a grip too: the Warthog throttle's latched SPEED BRAKE switch is the speed brake's)
            if (label.Length == 0 || function.Id.IndexOf("toggle", StringComparison.OrdinalIgnoreCase) >= 0 || !SetupPhases.LabelNames(function, label))
                return false;
            string wanted = SetupPhases.FunctionDirection(function.Id, function.PairRole);
            // (a set command without a direction - IGNITION L / R / BOTH - fits the position its label names; a toggle or a
            // step up / down never does)
            if (wanted.Length == 0)
                return !string.Equals(function.MapDir, "toggle", StringComparison.OrdinalIgnoreCase) &&
                       !Regex.IsMatch(function.Id, "inc|dec|increase|decrease|_2$|plus|minus|next|prev|cycle", RegexOptions.IgnoreCase);
            return !SetupPhases.DirectionClash(wanted, SetupPhases.LabelDirection(label));
        }

        // The direction of a set member (group role up / down / left / right; fwd = up, aft = down) against the direction of
        // a hat-like control's position: opposite is wrong.
        internal static bool OppositeHatDirection(SetupFunction function, string control)
        {
            if (function == null || string.IsNullOrEmpty(control) || control.IndexOf(".axis.", StringComparison.Ordinal) >= 0)
                return false;
            string[] parts = (function.Group ?? string.Empty).Split(':');
            string want = parts.Length == 2 ? HatWord(parts[1]) : string.Empty;
            // (review 6 m2: gear and flaps up / down too - gear up on the coolie's DOWN, gear down on the china hat FWD)
            if (want.Length == 0 && Regex.IsMatch(function.Id ?? string.Empty, "^(gearin|gearout|flapsup|flapsdown)$"))
                want = function.Id == "gearin" || function.Id == "flapsup" ? "up" : "down";
            if (want.Length == 0)
                return false;
            string have = HatWord(LastSegment(control));
            if (want.Length == 0 || have.Length == 0 || !Regex.IsMatch(control, @"\.(hat|coolie|china|ministick2?|rocker|dms|tms|cms|trim|speedbrake|boat|pinky)\b"))
                return false;
            return (want == "up" && have == "down") || (want == "down" && have == "up") || (want == "left" && have == "right") || (want == "right" && have == "left");
        }

        private static string HatWord(string word)
        {
            switch ((word ?? string.Empty).ToLowerInvariant())
            {
                case "up": case "fwd": case "forward": return "up";
                case "down": case "aft": case "back": return "down";
                case "left": return "left";
                case "right": return "right";
                default: return string.Empty;
            }
        }

        // A lever detent's own functions (afterburner, idle cut-off, reverser, TOGA ...), not a throttle step key (Elite's
        // throttle up / down buttons) that a detent would fire whenever the lever passes it.
        private static bool LeverOwn(string id)
        {
            return LeverFamily.IsMatch(id ?? string.Empty) && !Regex.IsMatch(id ?? string.Empty, "_(up|down)_button$|increase|decrease|_inc$|_dec$");
        }

        // "Where should functions go?" is a slider, not a switch: the real aircraft's positions take over function by function
        // as it moves - from a quarter on the HOTAS sets used often (TMS, DMS, CMS, trim, comm), from the middle every function
        // used often, from three quarters their axes, the occasional ones and the fight's occasional cockpit buttons, at the end every function - and the continuous pull below grows with it.
        internal const double RealThresholdHotas = 0.25;

        internal static double RealThreshold(SetupFunction function)
        {
            if (function == null)
                return 0.5;
            int rank = SetupImportance.FrequencyRank(function.Frequency);
            if (function.Group.Length > 0 && !function.IsAxis && rank >= 3)
                return RealThresholdHotas;
            if (rank >= 3)
                return function.IsAxis ? 0.75 : 0.5;
            // (occasional cockpit buttons - a switch of the real cockpit, not of its HOTAS - of the fight (weapons, countermeasures,
            // sensors) from three quarters, the rest only at "fully real": each step of the slider moves something, 2026-11-16)
            if (rank >= 2)
                return string.Equals(function.Kind, "cockpit", StringComparison.Ordinal) && !function.IsAxis && !Regex.IsMatch(function.MapCategory ?? string.Empty, "^(weapons|countermeasures|sensors)$") ? 1.0 : 0.75;
            return 1.0;
        }

        private static bool RealOn(Run run, SetupFunction function)
        {
            return run.Realism >= RealThreshold(function) - 1e-9;
        }

        // How strongly the real aircraft's position pulls at slider position 1 ("where the real aircraft has it"); the pull
        // grows evenly with the slider (docs/auto-setup-eval.md#where-functions-go).
        internal static double RealismWeight = 4.0;

        // Fly-and-fight functions planned per aircraft (see BuildDemands): countermeasures, gear, flaps, the wheel brake button
        // (review 7 B13: one for 20 modules found no button free of every module's flares and release).
        private static readonly Regex SeatBase = new Regex(@"_(CPG|PLT|RIO|WSO|operator|pilot|Pilot|Copilot|LoadMaster|GUN|easy)$", RegexOptions.Compiled);
        private static readonly Regex PerAircraftFunction = new Regex(@"^(cms_|gear_toggle$|gearin$|gearout$|flaps_toggle$|flapsup$|flapsdown$|wheelbrakes_button$)", RegexOptions.Compiled);

        // How much the snap views (looking around) weigh above their view category (docs/auto-setup-eval.md#in-flight-essentials).
        internal static double SnapViewBoost = 8.0;

        // The in-flight essentials before looking around (docs/auto-setup-eval.md#in-flight-essentials) and the comm
        // switch: a set never takes their hat.
        private static readonly Regex EarlierEssential = new Regex(@"^(trigger1ststage|trigger2ndstage|launchweapon|cms_|wheelbrake|gear|flaps|trim|airb|target_management_system_|radar_lock|comm\d|easycomms)", RegexOptions.Compiled);

        // The snap views that belong together on one hat: one aircraft's of one game.
        private static string SnapKey(Run run, Demand d)
        {
            return (d.Nodes.Count > 0 ? d.Nodes[0].Game : string.Empty) + "|" + string.Join(",", d.Aircraft);
        }

        // Looking around: the snap views and the look axes (not zoom, not the recenter).
        internal static readonly Regex LookFunction = new Regex(@"^(view_snap_(forward|back|left|right)|view_look_(horizontal|vertical))$", RegexOptions.Compiled);
        // What head tracking / VR replaces (review 5 m2, harness L24): looking around plus the padlock and Star Citizen's
        // look-ahead (the head turns there itself); the recenter stays and is needed.
        internal static readonly Regex HeadTrackingLook = new Regex(@"^(view_snap_(forward|back|left|right)|view_look_(horizontal|vertical)|view_padlock|space_look_ahead_toggle)$", RegexOptions.Compiled);

        // The sims a pilot flies with head tracking (TrackIR, face tracking, Tobii) or VR: the wizard's head-tracking
        // question concerns them (not the arcade and pad games, not X4).
        private static readonly HashSet<string> HeadTrackingGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DCS", "IL2Game", "IL2Korea", "IL2CoD", "IL2CoDBlitz", "IL21946", "FalconBMS", "MSFS2020", "MSFS2024", "FSX", "FSXSE", "P3D4", "P3D5", "P3D6",
            "XPlane11", "XPlane12", "WarThunder", "Arma2", "Arma2OA", "Arma3", "ArmaReforger", "StarCitizen", "EliteDangerous", "VTOLVR", "VTOLVRNative",
            // FreeSpace Open reads TrackIR and runs in VR (Knossos' "Play in VR").
            "FateOfTheGalaxy"
        };

        internal static bool HeadTrackingGame(string game)
        {
            return HeadTrackingGames.Contains(string.IsNullOrWhiteSpace(game) ? "DCS" : game);
        }

        // The rank of a function in the documented order of the in-flight essentials (what must fit first on a small
        // device); 99 = no essential. Looking around (without head tracking) comes right after the fight essentials (gun,
        // release, countermeasures, brakes), the trim (flown all the time) and the target, before gear, flaps, airbrake and comms.
        private static readonly string[] EssentialRanks = { @"^throttle_(up|down)_button$", "^(trigger1ststage|trigger2ndstage)$", "^launchweapon$", "^cms_", "^wheelbrake", "^trim", "^(target_management_system_|radar_lock)", "^(view_snap_|view_center$)", "^(zoom|fov_toggle_china_hat_fwd)$", "^gear", "^flaps", "^airb", @"^(comm\d|easycomms)" };

        internal static int EssentialRank(string functionId)
        {
            for (int i = 0; i < EssentialRanks.Length; i++)
            {
                if (Regex.IsMatch(functionId ?? string.Empty, EssentialRanks[i]))
                    return i;
            }
            return 99;
        }

        // A set may take a hat from another function: never the user's own, a kept bind, a wish or a pair; an earlier
        // essential always, else a less important one that is no earlier essential.
        private static bool MayEvict(Run run, Demand member, Demand other, double floor, Slot at)
        {
            // (a pair member - gear up / down - only gives way to an earlier essential set: the snap views take the coolie
            // from a gear-down button at "fully real", the pair is placed again together)
            bool laterPair = other.Function.PairGroup.Length > 0 && EssentialRank(other.FunctionId) < 99 && EssentialRank(member.FunctionId) < EssentialRank(other.FunctionId) && !other.Fixed;
            // (owner 2026-11-16: looking around on a grip hat survives at every slider position - a single later essential
            // placed there (a comm switch, the airbrake) gives way to the snap views; a real HOTAS position still keeps it)
            if (member.DirKey == "snapview" && !run.Options.HeadTracking && !other.Kept && !HasWish(other) && other.Function.PairGroup.Length == 0 && other.DirKey.Length == 0 &&
                EssentialRank(other.FunctionId) > EssentialRank(member.FunctionId) && !(at != null && RealHeld(run, other, at)))
                return true;
            // (a group of no essential - the dogfight switch's next / prev placed together - gives way to an essential set: the
            // trim takes back the Extreme 3D's only hat in Falcon BMS)
            bool looseGroup = other.Fixed && other.DirKey.Length == 0 && other.Function.Group.Length > 0 && other.StyleDevice.Length == 0 && EssentialRank(other.FunctionId) == 99 &&
                              EssentialRank(member.FunctionId) < 99 && !(at != null && RealHeld(run, other, at));
            if (other.Kept || HasWish(other) || (other.Function.PairGroup.Length > 0 && !laterPair) || (other.Fixed && other.DirKey.Length == 0 && !looseGroup))
                return false;
            // (a nearest hostile the grip rescue placed stays: only the snap views and the survival functions before it - gun,
            // release, countermeasures - may move it; the target cycle and energy sets do not take that hat back)
            if (NearestHostile(other) && other.Fixed && member.DirKey != "snapview" && EssentialRank(member.FunctionId) > EssentialRank("cms_"))
                return false;
            // From the middle of "Where should functions go?" on, a function on its real aircraft's HOTAS position (the
            // Warthog's DMS in the F-16 / A-10C) keeps it: the snap views find a free hat or a shift layer instead.
            if (member.DirKey == "snapview" && at != null && RealHeld(run, other, at))
                return false;
            int mine = EssentialRank(member.FunctionId), theirs = EssentialRank(other.FunctionId);
            // (a radar lock button where the aircraft's TMS up is placed is no essential of its own any more)
            if (other.FunctionId == "radar_lock" && other.Aircraft.Where(p => !SharedLayer(run, other, p)).All(p => run.Placed.Keys.Any(d => d.FunctionId == "target_management_system_up" && d.Aircraft.Contains(p, StringComparer.OrdinalIgnoreCase))))
                theirs = 99;
            // (the rest of the CMS set that is split already - left / right / ECM while forward / aft dispense from their own
            // buttons or hat - is no essential of its own, either way: the trim takes the CH Flightstick Pro's only hat from
            // it, and it never takes the trim's hat on an Extreme 3D)
            if (SplitRemainder(run, other))
                theirs = 99;
            if (SplitRemainder(run, member))
                mine = 99;
            // (an essential before any function that is none: sensor select, DF, radar modes go to the shift layer
            // before looking around does - in a 20-module hangar too, where they weigh more by their many aircraft)
            if (mine < theirs)
                return true;
            return other.Importance < floor && theirs >= mine;
        }

        private static bool SplitRemainder(Run run, Demand demand)
        {
            if (!Regex.IsMatch(demand.FunctionId ?? string.Empty, "^cms_(left|right|depress)"))
                return false;
            Slot at;
            bool placed = run.Placed.TryGetValue(demand, out at);
            return run.Placed.Any(p => (p.Key.FunctionId == FlareFunction || p.Key.FunctionId == "cms_aft_panic_or_just_chaff") && Overlaps(p.Key.Bits, demand.Bits) &&
                                       !(placed && p.Value.Device == at.Device && p.Value.Layer == at.Layer && Prefix(p.Value.RuleControl) == Prefix(at.RuleControl)));
        }

        // A function that sits where its real aircraft has it (every aircraft's position known) while the slider is at least
        // in the middle.
        private static bool RealHeld(Run run, Demand demand, Slot slot)
        {
            return run.Realism >= 0.5 && demand.Real.Count > 0 && demand.Real.All(g => g.Controls != null) && RealMatch(run, demand, slot) >= 0.99;
        }

        private static readonly Regex YokeButtonFunctionLabel = new Regex(@"\bPTT\b|TRIM|\bA/?P\b|AUTOPILOT|DISC|MIC|RADIO|\bCOMM?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Encoder = new Regex(@"\.(rotary|encoder)\.[^.]+\.(cw|ccw)$", RegexOptions.Compiled);
        private static readonly Regex Stepping = new Regex(@"(^|_)(inc|dec|increase|decrease|up|down|left|right|cw|ccw|next|prev|plus|minus|fwd|aft|in|out|more|less)(_|$)|(inc|dec)$|increase|decrease|rpm_|zoom", RegexOptions.Compiled);
        private static readonly Regex OneWayStop = new Regex(@"stop|off|cut ?off|cutoff|shut", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex StartWords = new Regex(@"start|toggle|on/off|start/stop|ign", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ToggleTitle = new Regex(@"toggle|\w\s*/\s*(stop|off|close|retract|disarm|disengage|down|out)\b|\b(on|open|extend|start|arm|engage)\s*/\s*\w", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The game's own title of a command names a toggle ("Engine start/stop", "Gear up/down", "Canopy open/close").
        private static bool TitleToggle(Run run, Demand demand)
        {
            return demand.Nodes.Any(node => run.Request.Commands.Any(c => c.Game == node.Game && c.Action == node.Action && string.Equals(c.Aircraft, node.Aircraft, StringComparison.OrdinalIgnoreCase) &&
                                                                          c.Title.Length > 0 && ToggleTitle.IsMatch(c.Title)));
        }

        // Its commands start and stop the engine with one command (a toggle: "start/stop", "on/off").
        private static bool EngineToggle(Run run, Demand demand)
        {
            return demand.Nodes.Any(node => ToggleOrHold(node.Action, string.Empty) ||
                                            run.Request.Commands.Any(c => c.Game == node.Game && c.Action == node.Action && string.Equals(c.Aircraft, node.Aircraft, StringComparison.OrdinalIgnoreCase) &&
                                                                          ToggleOrHold(c.Action, c.Control, c.Title)));
        }

        // An engine function on a detent: a stop (never a start or a start / stop toggle) on an OFF / CUTOFF detent, and
        // the same engine as the lever (THR 1 / Lever 1 = left, 2 = right).
        private static bool CutoffOnOwnLever(Run run, Demand demand, Slot slot)
        {
            // Its commands must be one-way stops, not also the start of the same engine (IL-2's engine_1_switch).
            bool startToo = demand.Nodes.Any(node => run.Request.Commands.Any(c => c.Game == node.Game && c.Action == node.Action && string.Equals(c.Aircraft, node.Aircraft, StringComparison.OrdinalIgnoreCase) &&
                                                                                    (Regex.IsMatch(c.Function, "start", RegexOptions.IgnoreCase) || StartWords.IsMatch(c.Control) || ToggleOrHold(c.Action, c.Control, c.Title))));
            return !startToo && CutoffDetent(demand.FunctionId, slot.Control, slot.Input.Label);
        }

        // The detent side of it: a stop / cut-off function on an OFF / CUTOFF detent (not IDLE) of its own lever (THR 1 /
        // THR L / Lever 1 = left, 2 / R = right).
        internal static bool CutoffDetent(string functionId, string control, string label)
        {
            string id = functionId ?? string.Empty;
            string where = (control ?? string.Empty) + " " + (label ?? string.Empty);
            if (!Regex.IsMatch(id, @"stop|cut_?off", RegexOptions.IgnoreCase) || Regex.IsMatch(where, @"\bIDLE\b", RegexOptions.IgnoreCase) ||
                !Regex.IsMatch(where, @"\bOFF\b|CUT ?OFF|detent\.(off|cutoff)", RegexOptions.IgnoreCase))
                return false;
            string n = LeverNumber(label);
            if ((id.StartsWith("left", StringComparison.OrdinalIgnoreCase) && n == "2") || (id.StartsWith("right", StringComparison.OrdinalIgnoreCase) && n == "1"))
                return false;
            return true;
        }

        // "1" (left) or "2" (right) for a lever's printed number or side (THR 1, THR L, LEVER 2, ENG R), else empty.
        internal static string LeverNumber(string label)
        {
            Match lever = Regex.Match(label ?? string.Empty, @"\b(THR|LEVER|ENG)\s*([12LR])\b", RegexOptions.IgnoreCase);
            if (!lever.Success)
                return string.Empty;
            string n = lever.Groups[2].Value.ToUpperInvariant();
            return n == "L" ? "1" : n == "R" ? "2" : n;
        }

        // A relation already bound to an input that gets one more command (Auto Group, Fill all): the rules the plan keeps
        // for that input (docs/auto-setup-eval.md#hard-gates). Returns why not, or empty. startToo: the command also starts
        // the engine or toggles it (start / stop, on / off).
        // Axis functions that stay where they are set (throttles, a helicopter's collective / engine levers, RPM, mixture,
        // condition): never on an axis that springs back to the centre.
        internal static readonly Regex StayAxisFunction = new Regex(@"^(throttle(left|right|f1|jeffmirage21)|rpm_control|nozzle_angle_mixture_control|condition_lever_axis)", RegexOptions.Compiled);

        internal static string BoundInputVeto(SetupFunction function, string functionId, SetupInput input, bool startToo, ICollection<SetupFunction> catalog = null, bool toggleOrHold = false)
        {
            // (owner 2026-11-16: a throttle never on a spring-return axis in any game - Auto Group put the MSFS throttle on
            // a left stick's twist; review 7 M10: and on a stick's pitch / Y - Auto Group put IL-2 1946's throttles back on the
            // dual VKB's left stick; a spaceship's forward / aft thrust stays fine there; a gamepad stick is judged at setup)
            if (input != null && input.IsAxis && !string.IsNullOrEmpty(functionId) && (StayAxisFunction.IsMatch(functionId) || functionId == "space_strafe_fwdaft") &&
                (Regex.IsMatch(input.Control ?? string.Empty, @"\.axis\.(twist|rudder|roll|rocker)$") || string.Equals(input.Type, CentringType, StringComparison.OrdinalIgnoreCase) ||
                 (functionId != "space_strafe_fwdaft" && (input.Control ?? string.Empty) == "stick.axis.pitch")))
                return "a throttle never on a spring-return axis (it would snap back to half power)";
            if (input == null || input.IsAxis || string.IsNullOrEmpty(functionId))
                return string.Empty;
            string id = functionId;
            string control = input.Control ?? string.Empty;
            string label = input.Label ?? string.Empty;
            bool zone = string.Equals(input.Type, "detent", StringComparison.OrdinalIgnoreCase) && !DetentControl.IsMatch(control);
            if (zone)
                return "an axis zone button fires whenever its axis passes it";
            if (IsSelector(input))
                return "a position of a mode selector is always held by one of them";
            if (DetentControl.IsMatch(control) || DetentLabel.IsMatch(label))
            {
                if (!LeverOwn(id))
                    return "a lever detent takes only the lever's own functions";
                if (TopEnd(id) && !TopDetent(control, label))
                    return "afterburner / TOGA / WEP only on a detent at the top of the lever";
                if (EngineStartStop.IsMatch(id) && (startToo || toggleOrHold || !CutoffDetent(id, control, label)))
                    return "an engine function on a lever detent only as the one-way cut-off of its own lever";
                if (Irreversible.IsMatch(id) && !EngineStartStop.IsMatch(id))
                    return "nothing irreversible on a lever detent";
                return string.Empty;
            }
            if (string.Equals(input.Type, "latched", StringComparison.OrdinalIgnoreCase) && function != null && !LatchedFits(function, input))
                return "a latched switch holds only an on / off pair or what its label names";
            if (string.Equals(input.Type, "latched", StringComparison.OrdinalIgnoreCase) && toggleOrHold && EngineStartStop.IsMatch(id))
                return "no engine toggle or held starter on a latched switch (it gets out of step or stays engaged)";
            string engineSwitch = EngineSwitchMisuse(id, input, null);
            if (engineSwitch.Length > 0)
                return engineSwitch;
            if (IsGrip(control) && Irreversible.IsMatch(id))
                return "nothing irreversible on a grip";
            // A printed cockpit label on a base or a panel (APU START, FUEL FLOW) keeps its switch for what it names.
            if (!IsGrip(control) && (SetupPhases.IsCockpitLabel(label) || (control.StartsWith("panel.", StringComparison.Ordinal) && SetupPhases.IsFunctionLabel(label))) &&
                label.IndexOf('=') < 0 && label.Length <= 48 && function != null && !SetupPhases.LabelNames(function, label))
                return "a switch labelled for another function (" + label + ")";
            // A printed function label on a grip (SPEED BRAKE) owns its control too, when the catalog has its function.
            if (IsGrip(control) && catalog != null && function != null && FunctionCockpitLabel(label) && !SetupPhases.LabelNames(function, label) &&
                catalog.Any(f => f.Labels.Count > 0 && SetupPhases.LabelNames(f, label)))
                return "a grip control labelled for another function (" + label + ")";
            return string.Empty;
        }

        // Functions that go on a latched switch only when its printed label names them.
        internal static readonly Regex LabelOnlyFunction = new Regex(@"canopy|tail_?hook|hook_(up|down|extend|retract)|refuel|probe", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static bool SwitchMixFree(Run run, Slot slot, Demand demand, Demand except)
        {
            string sw = SwitchOf(slot.Control);
            if (sw.Length == 0)
                return true;
            if (run.SwitchSlots == null)
            {
                run.SwitchSlots = new Dictionary<string, List<Slot>>(StringComparer.Ordinal);
                foreach (Slot s in run.Slots.Where(s => !s.Input.IsAxis))
                {
                    string key = SwitchOf(s.Control);
                    if (key.Length == 0)
                        continue;
                    key = s.Device.Device + "|" + s.Layer.ToString(CultureInfo.InvariantCulture) + "|" + key;
                    List<Slot> list;
                    if (!run.SwitchSlots.TryGetValue(key, out list))
                        run.SwitchSlots[key] = list = new List<Slot>();
                    list.Add(s);
                }
            }
            List<Slot> positions;
            if (!run.SwitchSlots.TryGetValue(slot.Device.Device + "|" + slot.Layer.ToString(CultureInfo.InvariantCulture) + "|" + sw, out positions))
                return true;
            bool engine = EngineOneWay.IsMatch(demand.FunctionId ?? string.Empty);
            foreach (Slot other in positions.Where(p => p != slot && p.BaseKey != slot.BaseKey))
            {
                foreach (Demand o in run.Occupants[other.Index])
                {
                    if (o == except || o == demand || !Overlaps(o.Bits, demand.Bits))
                        continue;
                    bool otherEngine = EngineOneWay.IsMatch(o.FunctionId ?? string.Empty) || EngineStartStop.IsMatch(o.FunctionId ?? string.Empty);
                    if (engine != otherEngine && (engine || EngineOneWay.IsMatch(o.FunctionId ?? string.Empty)))
                        return false;
                }
            }
            return true;
        }

        // ---- which end of the lever a detent is at (review 5 M2, harness L20) ----

        // Functions of the lever's top end: afterburner, TOGA, war emergency power, boost.
        // (review 6 m1: the autothrottle engage / arm too - never on a reverse or below-idle detent)
        private static readonly Regex TopEndFunction = new Regex(@"afterburner|toga|\bwep\b|war_?emergency|emergency_?power|boost|nitrous|water_?inj|atc_engage|auto_?throttle_?(arm|engage|on)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // A detent at the top end of the travel: its control or printed label says so (AFTERBURNER, TOGA, FLX / MCT, CL,
        // MAX); a plain "Lever N detent" (Honeycomb Bravo), "below detent" (Logitech quadrant) or a numbered TCA detent is
        // not known to be at the top.
        private static readonly Regex TopDetentText = new Regex(@"detent\.(afterburner|top|max|toga)$|\b(TOGA|FLX|MCT|CL|CLB|MAX|AFTERBURNER|AFTER BURNER|AB|BURNER|WEP|FULL POWER)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BottomDetentText = new Regex(@"\b(below|reverse|REV|idle|cut ?off|off|0)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static bool TopEnd(string functionId)
        {
            return TopEndFunction.IsMatch((functionId ?? string.Empty).Replace('_', ' ')) || TopEndFunction.IsMatch(functionId ?? string.Empty);
        }

        internal static bool TopDetent(string control, string label)
        {
            string text = (control ?? string.Empty) + " " + (label ?? string.Empty);
            return TopDetentText.IsMatch(text) && !BottomDetentText.IsMatch(label ?? string.Empty);
        }

        // ---- engine start / stop on switch positions (review 5 B5, harness L18) ----

        // A one-way engine start / stop / cut-off (not the master, magnetos or ignition: those are labelled set switches).
        private static readonly Regex EngineOneWay = new Regex(@"engine.*(start|stop)|(start|stop).*engine|starter|cut_?off|fuel_?cut|shutdown", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // A label that names the engine (or its start / fuel / ignition), not another system.
        private static readonly Regex EngineLabelWords = new Regex(@"\b(ENG|ENGINE|ENGINES|IGN|IGNITION|START|STARTER|STRT|MASTER|FUEL|THROTTLE|THR|MAG|MAGS|MAGNETO|JFS|CUT ?OFF|CUTOFF|MOTOR|OPER)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CentreWords = new Regex(@"\b(center|centre|mid|middle)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // The printed words of an engine stop position and of a start position.
        private static readonly Regex EngineStopWords = new Regex(@"\b(STOP|OFF|CUT ?-?OFF|CUTOFF|SHUT ?DOWN|SHUTDOWN|MOTOR)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EngineStartWords = new Regex(@"\b(START|STARTER|STRT|IGN|IGNITION|CRANK|ON|RUN)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PositionSuffix = new Regex(@"\.(up|down|center|centre|mid|fwd|aft|left|right|pos\.\d+)$", RegexOptions.Compiled);

        internal static bool EngineOneWayFunction(string functionId)
        {
            return EngineOneWay.IsMatch(functionId ?? string.Empty);
        }

        // The switch a position belongs to (throttle.base.toggle.2.center -> throttle.base.toggle.2, panel.selector.3.pos.1 ->
        // panel.selector.3); empty for a plain button.
        internal static string SwitchOf(string control)
        {
            string c = control ?? string.Empty;
            return PositionSuffix.IsMatch(c) && Regex.IsMatch(c, @"\.(toggle\d?|switch|selector|tgl|rocker)\.") ? PositionSuffix.Replace(c, string.Empty) : string.Empty;
        }

        // Why a one-way engine start / stop must not go on this switch position, or empty: never the centre of a 3-way
        // switch (every flip of its up / down passes it), never a position labelled for another system (BAT, ALT, APU, GEN,
        // lights ...), never an ENG CRANK position for a stop or for the other engine, never any position of a 3-way switch
        // that is not labelled for the engine. others: the functions on the switch's other positions (eval; null = unknown):
        // a position of a switch whose other positions carry unrelated functions is no engine switch either.
        internal static string EngineSwitchMisuse(string functionId, SetupInput input, IEnumerable<SetupInput> inputs, IEnumerable<string> others = null)
        {
            string id = functionId ?? string.Empty;
            if (input == null || input.IsAxis || !EngineOneWay.IsMatch(id))
                return string.Empty;
            string control = input.Control ?? string.Empty;
            string label = input.Label ?? string.Empty;
            if (DetentControl.IsMatch(control) || DetentLabel.IsMatch(label) || string.Equals(input.Type, "detent", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            string sw = SwitchOf(control);
            bool latched = string.Equals(input.Type, "latched", StringComparison.OrdinalIgnoreCase) || IsSelector(input);
            if (sw.Length == 0 && !latched)
                return string.Empty;
            bool engineLabel = EngineLabelWords.IsMatch(label);
            bool stop = Regex.IsMatch(id, @"stop|cut_?off|shutdown", RegexOptions.IgnoreCase);
            if (Regex.IsMatch(label, @"\bCRANK\b", RegexOptions.IgnoreCase))
            {
                if (stop)
                    return "an engine stop on ENG CRANK (crank is start only)";
                string side = Regex.IsMatch(label, @"\b(L|LEFT|1)\b", RegexOptions.IgnoreCase) ? "left" : Regex.IsMatch(label, @"\b(R|RIGHT|2)\b", RegexOptions.IgnoreCase) ? "right" : string.Empty;
                if (Regex.IsMatch(label, @"\bOFF\b", RegexOptions.IgnoreCase) || (side.Length > 0 && Regex.IsMatch(id, "^(left|right)_", RegexOptions.IgnoreCase) && !id.StartsWith(side, StringComparison.OrdinalIgnoreCase)))
                    return "an engine start on the other engine's or the OFF position of ENG CRANK";
            }
            if (Regex.IsMatch(control, @"\.(center|centre|mid)$") || (CentreWords.IsMatch(label) && !engineLabel))
                return "an engine start / stop on the centre of a 3-way switch";
            // (review 6 B5b / M9: a one-way engine stop only on a position whose printed label names the stop - STOP, OFF,
            // CUT-OFF, SHUTDOWN, MOTOR - and no start: never "L ENG AUTO IGNITION", never an unlabelled panel switch (Bravo
            // "Switch 5 down", Virpil "T7", STECS "STEM TGL"), as the canopy needs its label; a start never on OFF / STOP)
            bool stopWord = EngineStopWords.IsMatch(label), startWord = EngineStartWords.IsMatch(label);
            if (stop && (!stopWord || startWord))
                return label.Length == 0 || ControlNameLabel(label) ? "an engine stop on an unlabelled switch position" : "an engine stop on a position not labelled as the stop (" + label + ")";
            if (!stop && stopWord && !startWord)
                return "an engine start on a position labelled OFF / STOP (" + label + ")";
            if (label.Length > 0 && !engineLabel && !ControlNameLabel(label) && !CentreWords.IsMatch(label))
                return "an engine start / stop on a switch labelled for another system (" + label + ")";
            if (sw.Length > 0 && !engineLabel && inputs != null && inputs.Any(i => i != null && i.Device == input.Device && SwitchOf(i.Control) == sw && Regex.IsMatch(i.Control ?? string.Empty, @"\.(center|centre|mid)$")))
                return "an engine start / stop on a 3-way switch";
            if (others != null && others.Any(f => !string.IsNullOrEmpty(f) && !EngineOneWay.IsMatch(f) && !EngineStartStop.IsMatch(f)))
                return "an engine start / stop on a switch whose other positions do something else";
            return string.Empty;
        }

        // A position of a multi-position mode / profile selector (catalog type "selector").
        internal static bool IsSelector(SetupInput input)
        {
            return input != null && string.Equals(input.Type, "selector", StringComparison.OrdinalIgnoreCase);
        }

        // A printed label that names a control, not a function ("BOAT SWITCH", "CHINA HAT", "COOLIE SWITCH", "PINKY
        // SWITCH", "SLEW CONTROL"): it does not own the control for a function.
        private static readonly Regex ControlNameWords = new Regex(@"^(BOAT|PINKY|CHINA|COOLIE|HAT|SWITCH|BUTTON|BTN|LEVER|PADDLE|ROCKER|SLEW|CONTROL|CASTLE|THUMB|INDEX|TRIGGER|KNOB|WHEEL|DIAL|STICK|MINI|MINISTICK|ANALOG|POV|TOGGLE|TGL|LATCHED|STEM|MIDDLE|SW|PUSH|PRESS|L|R|LEFT|RIGHT|UP|DOWN|DN|FWD|AFT|CENTER|CENTRE|\d+[A-Z]?|[A-Z]\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static bool ControlNameLabel(string label)
        {
            string[] words = Regex.Split((label ?? string.Empty).Trim(), @"[^A-Za-z0-9]+").Where(w => w.Length > 0).ToArray();
            return words.Length > 0 && words.All(w => ControlNameWords.IsMatch(w));
        }

        // A printed cockpit label that names a function ("SPEED BRAKE", "ENG 1 MASTER", "LDG GEAR"): all capitals, not a
        // control's own name, not a catalog description.
        internal static bool FunctionCockpitLabel(string label)
        {
            string text = (label ?? string.Empty).Trim();
            return SetupPhases.IsCockpitLabel(text) && !ControlNameLabel(text) && text.IndexOf('=') < 0 && text.Length <= 48;
        }

        // Functions whose value is a rate around a centre (TDC / cursor slew, strafe left-right / up-down, head look):
        // on an axis that stays where it is left (a lever, slider, rotary, wheel) the cursor or the view drifts unless the
        // axis sits exactly in the middle.
        internal static readonly Regex CentredFunction = new Regex(@"^(tdc_horizontal|tdc_vertical|tdc_vertical_inv|space_strafe_hor|space_strafe_ver|view_look_horizontal|view_look_vertical|ah64_track_controller_[xy]|hcu_[xy]|f14_lantirn_(pitch|yaw))$", RegexOptions.Compiled);
        // Axes that spring back to the centre: a stick's or yoke's pitch / roll / twist, thumb sticks, rudder, rockers.
        internal static readonly Regex CentringAxisControl = new Regex(@"\.axis\.(pitch|roll|twist|rudder|rocker|x|y|ministick2?\.[xy]|thumbstick\.[xy]|stick\.[xy])$", RegexOptions.Compiled);

        internal static bool CentringAxis(string control)
        {
            return CentringAxisControl.IsMatch(control ?? string.Empty);
        }

        // A spring-return axis whose control name does not say so: the catalog types it "centring" (the T.Flight / TWCS
        // rudder rocker, throttle.axis.wheel.1).
        public const string CentringType = "centring";

        internal static string AxisType(string catalogType)
        {
            return string.Equals(catalogType, CentringType, StringComparison.OrdinalIgnoreCase) ? CentringType : "analog";
        }

        internal static bool CentringAxis(SetupInput input)
        {
            return input != null && (CentringAxis(input.Control) || string.Equals(input.Type, CentringType, StringComparison.OrdinalIgnoreCase));
        }

        private static bool CenteringStickAxis(string control)
        {
            return Regex.IsMatch(control ?? string.Empty, @"\.axis\.(pitch|roll|twist|ministick2?\.[xy])$");
        }

        // The printed label names other functions and not this one: the control is theirs (or stays free).
        private static bool LabelTaken(Run run, Demand demand, Slot slot)
        {
            string label = (slot.Input.Label ?? string.Empty).Trim();
            // (A gamepad's "A", "B", "X", "Y", "L1" are button names, not function labels; a catalog's description of a
            // trigger - "Trigger (Mode 1 Dogfight = 14 ...)" - is prose, not a printed label: the trigger stays the gun's.)
            if (label.Length < 3 || !SetupPhases.IsFunctionLabel(label) || demand.Function == null || slot.Control.IndexOf(".trigger.", StringComparison.Ordinal) >= 0 ||
                label.IndexOf('=') >= 0 || label.Length > 48)
                return false;
            HashSet<string> owners;
            if (!run.LabelOwners.TryGetValue(label, out owners))
                run.LabelOwners[label] = owners = new HashSet<string>(run.Request.Functions.Values.Where(f => f.Labels.Count > 0 && SetupPhases.LabelNames(f, label)).Select(f => f.Id), StringComparer.OrdinalIgnoreCase);
            if (owners.Count == 0 || owners.Contains(demand.FunctionId) || SetupPhases.LabelNames(demand.Function, label))
                return false;
            // (RescueSurvivalAnywhere's last try, owner 2026-11-22: a label holds its button only in a plan whose games have
            // its function - the YOKO's "Electric trim" or "AP disconnect" in a space game; a PTT keeps a game's comms)
            if (demand.IgnorePanelLabels)
                return run.Demands.Any(d => d != demand && !d.Redundant && owners.Contains(d.FunctionId));
            return true;
        }

        // The position word of a printed label against the position the function asks for (LAUNCH BAR EXTEND, HOOK UP).
        private static double RoleLabel(SetupFunction function, Slot slot)
        {
            if (slot.Grip)
                return 0;
            string wanted = SetupPhases.FunctionDirection(function.Id, function.PairRole);
            string printed = SetupPhases.LabelDirection(slot.Input.Label);
            // (review 6 m5: an unlabelled two-way switch's position names the direction too - the X56's TailHook Down on
            // SW1 up, Up on SW2 down was upside down)
            if (printed.Length == 0 && SwitchOf(slot.Control).Length > 0)
                printed = SetupPhases.LabelDirection(LastSegment(slot.Control));
            if (wanted.Length == 0 || printed.Length == 0)
                return 0;
            return SetupPhases.SameDirection(wanted, printed) ? 0.8 : -0.8;
        }

        // A cockpit label on a base that does not name the function (not its exact position, not an MFD function).
        private static bool OtherBaseLabel(SetupFunction function, Slot slot, string control, bool exactPref, bool mfdFunction)
        {
            // (review 6 M7, one rule with the eval's switches gate: a printed cockpit label on any control off the grips - a
            // base, the TCA Boeing quadrant's "IAS/MACH" / "HDG/TRK" buttons - holds only what it names; lever detents keep
            // their own rules)
            return !slot.Grip && (control.IndexOf(".base.", StringComparison.Ordinal) >= 0 || !(DetentControl.IsMatch(control) || DetentLabel.IsMatch(slot.Input.Label ?? string.Empty) ||
                                                                                                  string.Equals(slot.Input.Type, "detent", StringComparison.OrdinalIgnoreCase))) &&
                   !mfdFunction && !exactPref &&
                   SetupPhases.IsCockpitLabel(slot.Input.Label) && !GamepadButtonLabel(slot.Input.Label) && !LabelMatches(function, slot);
        }

        // A console-style button name printed on a HOTAS base (the T.Flight's "SHARE / SE", "OPTIONS / ST"): the button's
        // name, no cockpit function.
        internal static bool GamepadButtonLabel(string label)
        {
            return Regex.IsMatch((label ?? string.Empty).Trim(), @"^(SHARE|OPTIONS|SELECT|MENU|HOME|PS|VIEW|BACK)( */ *(SE|ST|SL))?$", RegexOptions.IgnoreCase);
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
            if (run.NotHere.Count > 0 && run.NotHere.Contains(demand.Index.ToString(CultureInfo.InvariantCulture) + "|" + slot.Index.ToString(CultureInfo.InvariantCulture)))
                return false;
            string reservedGame;
            if (run.ReservedFor.Count > 0 && run.ReservedFor.TryGetValue(slot.Index, out reservedGame) && demand.Nodes.Any(n => string.Equals(n.Game, reservedGame, StringComparison.OrdinalIgnoreCase)))
                return false;
            // (a set member never on another hat than its placed mates: CMS forward on one hat, CMS left on another)
            // (a thumb stick's directions count as a hat: CMS forward on the hat, CMS left on the ministick is a split too)
            if (demand.DirKey.Length > 0 && !demand.IsAxis && !slot.Input.IsAxis && GroupRoles.Contains(LastSegment(slot.RuleControl)))
            {
                List<Demand> mates;
                if (run.SetMates.TryGetValue(demand.DirKey, out mates))
                {
                    string hat = Prefix(slot.RuleControl);
                    foreach (Demand mate in mates)
                    {
                        Slot at;
                        if (mate != demand && mate != except && run.Placed.TryGetValue(mate, out at) && !at.Input.IsAxis && GroupRoles.Contains(LastSegment(at.RuleControl)) && Overlaps(mate.DirectBits ?? mate.Bits, demand.DirectBits ?? demand.Bits) &&
                            (at.Device != slot.Device || Prefix(at.RuleControl) != hat || at.Layer != slot.Layer))
                            return false;
                    }
                }
            }
            // (B5, L18: a one-way engine start / stop and another function never share one switch for an aircraft - flipping
            // the switch to "turn off" the other function would stop the engine)
            if (!demand.IsAxis && !SwitchMixFree(run, slot, demand, except))
                return false;
            // (a lever detent only for the aircraft whose throttle that lever carries)
            if (!demand.IsAxis && slot.Layer == 0)
            {
                Slot lever = LeverOfDetent(run, slot);
                if (lever != null && !run.Occupants[lever.Index].Any(o => o.IsAxis && ThrottleFunction.IsMatch(o.FunctionId) && Overlaps(o.Bits, demand.Bits)))
                    return false;
            }
            foreach (Demand other in run.Occupants[slot.Index])
            {
                if (other != except && other != demand && Conflict(other, demand) && !SharedBrakeLever(other, demand, slot) && !SharedGunTrigger(run, other, demand, slot))
                    return false;
            }
            List<Demand> kept;
            if (run.KeptAt.TryGetValue(slot.Key, out kept))
            {
                foreach (Demand other in kept)
                {
                    if (other != except && Conflict(other, demand))
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
                        if (other != except && other != demand && Conflict(other, demand) && !OtherPhaseOnly(normal, layerPhase))
                            return false;
                    }
                    if (run.KeptAt.TryGetValue(sibling.Key, out kept) && kept.Any(other => other != except && Conflict(other, demand)))
                        return false;
                }
            }
            return true;
        }

        // A single-stage trigger fires every module's gun: the first stage where it is the gun (P-51D, Bf 109, IL-2) and
        // the second stage where the first is a camera / laser detent (F-16, A-10C) share it. A second stage that drops
        // bombs or fires rockets (FW 190, MiG-21) does not.
        private static bool SharedGunTrigger(Run run, Demand a, Demand b, Slot slot)
        {
            // (a yoke without a trigger: its gun button is shared the same way)
            bool gunButton = Regex.IsMatch(slot.Control, @"^(stick|yoke)\.trigger\.stage1$") ? !run.TwoStage.Contains(slot.Device.Device)
                : slot.Control.StartsWith("yoke.button.", StringComparison.Ordinal) && !run.WithTrigger.Contains(slot.Device.Device);
            if (slot.Input.IsAxis || !gunButton)
                return false;
            Func<Demand, bool> gun = d => !d.CameraDetent && !d.ReleaseStage && (d.FunctionId == "trigger1ststage" || d.FunctionId == "trigger2ndstage");
            return gun(a) && gun(b) && a.FunctionId != b.FunctionId;
        }

        // One brake lever (a stick's brake lever, a single brake axis) brakes both wheels: the left and the right toe
        // brake of an aircraft share it.
        private static bool SharedBrakeLever(Demand a, Demand b, Slot slot)
        {
            if (!slot.Input.IsAxis || !BrakeAxis.IsMatch(slot.Control) || slot.Control.StartsWith("pedals.axis.brake_", StringComparison.Ordinal) || a.Function == null || b.Function == null)
                return false;
            Match x = ToeBrake.Match(a.Function.Id), y = ToeBrake.Match(b.Function.Id);
            return x.Success && y.Success && x.Groups[1].Value != y.Groups[1].Value && x.Groups[2].Value == y.Groups[2].Value;
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
            // (the first round only for functions used often: an occasional function on its real position never takes a grip
            // button before the gun, the weapon release or the countermeasures are placed)
            if (strength >= 2)
                return RealOn(run, demand.Function) && RealThreshold(demand.Function) <= 0.5 && demand.Real.Count > 0 && demand.Real.All(g => g.Controls != null) && RealMatch(run, demand, slot) >= 0.99;
            if (LabelMatches(demand.Function, slot) || SpaceRule(demand, slot) > 0 || Wanted(run, demand, slot))
                return true;
            int pref = PrefIndex(run, demand.Function, slot);
            if (pref == 0 && demand.Function.Prefs[0].IndexOf('*') < 0)
                return true;
            return RealOn(run, demand.Function) && demand.Real.All(g => g.Controls != null) && RealMatch(run, demand, slot) >= 0.99;
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
            // (base toggles and the gear lever; on a grip the latched two-way switches too - the Warthog's boat and pinky
            // switch - so a pair never ends up split over two controls)
            List<Tuple<Slot, Slot>> toggles = run.Slots.Where(s => s.Layer == 0 && (Regex.IsMatch(s.RuleControl, @"\.(toggle3?\.\d+|lever\.gear)\.up$") ||
                                                                                     (string.Equals(s.Input.Type, "latched", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(s.RuleControl, @"\.(up|fwd)$"))))
                .Select(up => Tuple.Create(up, run.Slots.FirstOrDefault(d => d.Layer == 0 && d.Device == up.Device && d.RuleControl == Regex.Replace(up.RuleControl, @"\.up$", ".down") &&
                                                                         d.RuleControl != up.RuleControl) ??
                                               run.Slots.FirstOrDefault(d => d.Layer == 0 && d.Device == up.Device && up.RuleControl.EndsWith(".fwd", StringComparison.Ordinal) &&
                                                                         d.RuleControl == up.RuleControl.Substring(0, up.RuleControl.Length - 4) + ".aft")))
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
                .GroupBy(d => d.Function.Group.Split(':')[0] + "|" + d.HandKey + "|" + Prefix(d.RealKey.Split('+')[0]) +
                              (d.DirKey == "snapview" ? "|" + SnapKey(run, d) : string.Empty), StringComparer.Ordinal)
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
            // Looking around needs all four snap views on one hat: half a set is no look-around.
            if (members.Count > 0 && members.All(m => m.DirKey == "snapview"))
                minimum = Math.Max(minimum, members.Count);
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
                if (done.Contains(option.Item1) || placement.Any(p => p.Item2 == option.Item2 && Conflict(p.Item1, option.Item1)))
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
            // (Flare / chaff without a free hat: a plain grip button, never behind a modifier.)
            foreach (Demand demand in open.Where(d => !run.Placed.ContainsKey(d) && (d.DirRole.Length == 0 || (strength == 0 && (NoLayer.Contains(d.FunctionId) || d.Lone)))).OrderByDescending(HasWish).ThenByDescending(d => d.Importance).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList())
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
            List<Demand> singles = open.Where(d => !d.Fixed && (d.DirRole.Length == 0 || ((NoLayer.Contains(d.FunctionId) || d.Lone) && !run.Placed.ContainsKey(d)))).OrderByDescending(d => d.Importance).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
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
                List<Demand> blockers = run.Occupants[slot.Index].Where(o => Conflict(o, demand)).ToList();
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
                List<Demand> blockers = run.Occupants[slot.Index].Where(o => Conflict(o, demand)).ToList();
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
                    Importance = Math.Round(demand.Importance, 3),
                    Share = demand.ShareKey
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
                    relation.RealKnown = members.Any(m => m.RealWeight > 0 && m.Real.Any(g => g.Controls != null));
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
                        Reason = demand.Redundant ? demand.Reason : WhyNot(run, demand),
                        // (reported only: the quality's essentials, less the ones covered another way)
                        Essential = SetupImportance.Essential(demand.Category, demand.Frequency) && demand.Aircraft.Any(a => !EssentialCoveredAnotherWay(run, demand, a))
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
                    else if (isEssential && EssentialCoveredAnotherWay(run, demand, key))
                        essentialPlaced++;
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
            // (review 6 M5: without head tracking a snap-view set that found no hat and no shift button is said too)
            if (!options.HeadTracking && run.Modifiers.Count(m => !m.Switch) == 0)
            {
                List<string> noLook = run.Demands.Where(d => d.DirKey == "snapview" && !d.Kept && !d.Redundant && !run.Placed.ContainsKey(d) && IsTop(run, d))
                    .Where(d => !run.Placed.Keys.Any(o => (o.FunctionId == "view_look_horizontal" || o.FunctionId == "view_look_vertical") && Overlaps(o.Bits, d.Bits)))
                    .SelectMany(d => d.Aircraft.Where(p => request.Aircraft.ContainsKey(p) && !request.Aircraft[p].SharedLayer))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                if (noLook.Count > 0)
                    plan.Notes.Insert(0, "Looking around: no free hat and no button for a shift on your devices (" + (noLook.Count <= 3 ? string.Join(", ", noLook) : string.Join(", ", noLook.Take(2)) + " +" + (noLook.Count - 2).ToString(CultureInfo.InvariantCulture) + " more") +
                                         "): use the game's own view keys or the mouse, or set up head tracking.");
            }
            // (owner 2026-11-16: a game that looks around with axes only - Project Wingman, Battlefield - and found no
            // spring-centred axis for them: a grip hat none of the game's functions uses is named for it (Hat to axes, or the
            // game's own view hat); without one it is a hardware limit, said, not silent)
            // (review 7 M17: with head tracking too in a game the tracker does not reach - Battlefield, Project Wingman, AC7)
            {
                foreach (string game in LookAxesGames(run).Where(g => !options.HeadTracking || !HeadTrackingGame(g)))
                {
                    string hat = FreeHatFor(run, game);
                    plan.Notes.Insert(0, hat.Length > 0
                        ? "Looking around in " + game + ": no spring-centred axis is left for the look axes - " + hat + " stays free for it: turn it into the look axes with Hat to axes (virtual functions) or set it as the view hat in the game."
                        : "Looking around in " + game + ": no spring-centred axis and no free grip hat on your devices (a hardware limit): use the mouse, the game's view keys or head tracking.");
                }
            }
            // (review 7 B13: flares behind a shift are said too - the user holds the shift button first)
            {
                List<string> shiftedFlares = run.Demands.Where(d => d.FunctionId == FlareFunction && !d.Kept && !d.Redundant && IsTop(run, d) && !DispenseOnGrip(run, d) && FlareOnShiftedGrip(run, d))
                    .SelectMany(d => d.Aircraft.Where(p => request.Aircraft.ContainsKey(p) && !request.Aircraft[p].SharedLayer))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                if (shiftedFlares.Count > 0)
                    plan.Notes.Insert(0, "Countermeasures behind a shift (" + (shiftedFlares.Count <= 3 ? string.Join(", ", shiftedFlares) : string.Join(", ", shiftedFlares.Take(2)) + " +" + (shiftedFlares.Count - 2).ToString(CultureInfo.InvariantCulture)) +
                                         "): no grip button was left for them without a modifier - hold the shift button, then press them.");
            }
            // (review 7 M10: no throttle on a spring-return axis in any game - a setup without a lever, slider or collective
            // has no place for it; said, not silent)
            {
                bool throttlePlace = request.Inputs.Any(i => i.IsAxis && run.Slots.Any(s => s.Device.Device == i.Device && SetupRoles.Binds(s.Role)) &&
                                                             (LeverAxis.IsMatch(i.Control) || SliderAxis.IsMatch(i.Control) || i.Control.StartsWith("collective.axis.", StringComparison.Ordinal)));
                List<string> noThrottle = throttlePlace ? new List<string>() : run.Demands.Where(d => d.IsAxis && ThrottleFunction.IsMatch(d.FunctionId) && !d.Kept && !d.Redundant && IsTop(run, d) && !run.Placed.ContainsKey(d))
                    .SelectMany(d => d.Aircraft.Where(p => request.Aircraft.ContainsKey(p) && !request.Aircraft[p].SharedLayer))
                    .Where(p => !run.Placed.Keys.Any(o => (o.IsAxis && ThrottleFunction.IsMatch(o.FunctionId) || o.FunctionId == "throttle_up_button") && o.Aircraft.Contains(p, StringComparer.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                if (noThrottle.Count > 0)
                    plan.Notes.Insert(0, "Throttle: no lever, slider or throttle wheel on your devices (" + (noThrottle.Count <= 3 ? string.Join(", ", noThrottle) : string.Join(", ", noThrottle.Take(2)) + " +" + (noThrottle.Count - 2).ToString(CultureInfo.InvariantCulture)) +
                                         "): a throttle on a spring-return stick axis would snap back - use the game's throttle keys or add a throttle.");
            }
            // (owner 2026-11-16: looking around is pan + recenter + zoom; a recenter or a zoom with no input left is said)
            {
                Func<Demand, bool> lookTop = d => !d.Kept && !d.Redundant && IsTop(run, d);
                List<string> noRecenter = options.HeadTracking ? new List<string>() : run.Demands.Where(d => d.FunctionId == "view_center" && lookTop(d) && !run.Placed.ContainsKey(d))
                    .SelectMany(d => d.Aircraft.Where(p => request.Aircraft.ContainsKey(p) && !request.Aircraft[p].SharedLayer))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                if (noRecenter.Count > 0)
                    plan.Notes.Insert(0, "Looking around: no input is left for the view recenter (" + (noRecenter.Count <= 3 ? string.Join(", ", noRecenter) : string.Join(", ", noRecenter.Take(2)) + " +" + (noRecenter.Count - 2).ToString(CultureInfo.InvariantCulture)) +
                                         "): use the game's own recenter key.");
                HashSet<string> zoomed = new HashSet<string>(run.Placed.Keys.Where(d => d.FunctionId == "zoom" || d.FunctionId == "fov_toggle_china_hat_fwd").SelectMany(d => d.Aircraft), StringComparer.OrdinalIgnoreCase);
                List<string> noZoom = run.Demands.Where(d => (d.FunctionId == "zoom" || d.FunctionId == "fov_toggle_china_hat_fwd") && lookTop(d))
                    .SelectMany(d => d.Aircraft.Where(p => request.Aircraft.ContainsKey(p) && !request.Aircraft[p].SharedLayer && !zoomed.Contains(p)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                if (noZoom.Count > 0)
                    plan.Notes.Insert(0, "Zoom: no input is left for zooming (" + (noZoom.Count <= 3 ? string.Join(", ", noZoom) : string.Join(", ", noZoom.Take(2)) + " +" + (noZoom.Count - 2).ToString(CultureInfo.InvariantCulture)) +
                                         "): use the game's own zoom keys or the mouse wheel.");
            }
            // (owner 2026-11-16: the flares are never missing without a word either)
            {
                List<string> noFlares = run.Demands.Where(d => d.FunctionId == FlareFunction && !d.Kept && !d.Redundant && IsTop(run, d) && !FlareOnShiftedGrip(run, d))
                    .SelectMany(d => d.Aircraft.Where(p => request.Aircraft.ContainsKey(p) && !request.Aircraft[p].SharedLayer))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                if (noFlares.Count > 0)
                    plan.Notes.Insert(0, "Countermeasures: no grip button is left for the flares, not even behind a shift (" + (noFlares.Count <= 3 ? string.Join(", ", noFlares) : string.Join(", ", noFlares.Take(2)) + " +" + (noFlares.Count - 2).ToString(CultureInfo.InvariantCulture) + " more") +
                                         "): put them on a button yourself in the review - they keep you alive.");
            }
            // (review 6 M1: with head tracking the view recenter is never missing without a word - a modifier combo is fine,
            // and where no input is left at all the user is told to use the tracker's own recenter key)
            if (options.HeadTracking)
            {
                List<string> noRecenter = run.Demands.Where(d => d.FunctionId == "view_center" && !d.Kept && !d.Redundant && !run.Placed.ContainsKey(d) && d.Nodes.Any(n => HeadTrackingGame(n.Game)))
                    .SelectMany(d => d.Aircraft.Where(p => request.Aircraft.ContainsKey(p) && !request.Aircraft[p].SharedLayer))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                if (noRecenter.Count > 0)
                    plan.Notes.Insert(0, "Head tracking: no input is left for the view recenter (" + (noRecenter.Count <= 3 ? string.Join(", ", noRecenter) : string.Join(", ", noRecenter.Take(2)) + " +" + (noRecenter.Count - 2).ToString(CultureInfo.InvariantCulture) + " more") +
                                         "): use the recenter key of your tracking software (TrackIR, OpenTrack, Tobii, the VR headset).");
            }
            plan.Notes.Add(run.Realism < 0.5
                ? "Placement " + Math.Round(run.Realism * 100).ToString(CultureInfo.InvariantCulture) + " % toward the real aircraft: the same button for the same job in every aircraft."
                : "Placement " + Math.Round(run.Realism * 100).ToString(CultureInfo.InvariantCulture) + " % toward the real aircraft: aircraft whose real HOTAS has a function elsewhere got their own relation (" +
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
            if (RealOn(run, demand.Function) && real >= 0.99)
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
            Demand blocker = run.Occupants[best.Index].Where(o => Conflict(o, demand)).OrderByDescending(o => o.Importance).FirstOrDefault();
            List<Demand> kept;
            if (blocker == null && run.KeptAt.TryGetValue(best.Key, out kept))
                blocker = kept.FirstOrDefault(o => Conflict(o, demand));
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
