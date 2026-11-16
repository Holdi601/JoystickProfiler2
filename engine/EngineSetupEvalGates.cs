using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using JoyPro.Setup;

namespace JoyPro.Engine
{
    // Hard gates of the auto setup evaluation (docs/auto-setup-eval.md#hard-gates): per row, can a pilot fly and fight
    // with what the plan bound? Coverage counts any binding anywhere; a gate only counts a binding a pilot can use:
    // - pitch / roll on a stick's (or yoke's) own axes, yaw on the rudder pedals when there are pedals (else a twist,
    //   rocker or thumb stick X), the throttle on a lever (a slider only when there is no lever; never on a rotary knob,
    //   thumb wheel or thumb stick while a lever exists), the engines of a twin on levers of the same kind;
    // - the gun on the trigger (in the F-16, A-10C ... the trigger's second detent, not the camera / first detent),
    //   weapon release and countermeasures on a grip, wheel brakes (both toe brakes, or the game's wheel brake button),
    //   none of them behind a modifier or a mode-switch layer;
    // - gear and flaps bound; looking around: snap views on a hat or the look axes (zoom alone is no look-around);
    // - an axis command only on an axis and a button command only on a button.
    // Destructive bindings (zero tolerance): engine stop / start, jettison, eject, canopy, gear on a lever detent, on a
    // switch whose printed label names another function, or on a grip hat; any function sharing one input with its
    // opposite (engine start and stop, light on and off).
    internal static partial class EngineSetupEval
    {
        internal sealed class EvalGate
        {
            public string Gate = string.Empty;
            public string Status = "ok";        // ok, fail, n/a
            public string Detail = string.Empty;
        }

        internal static readonly string[] GateIds = { "pitch", "roll", "yaw", "throttle", "fire", "release", "countermeasures", "wheel_brakes", "gear", "flaps", "look", "zoom", "kinds", "switches", "modifiers" };

        // Games the gates treat as combat games (a gun / weapon release must be there, the catalog notwithstanding).
        private static readonly HashSet<string> CombatGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DCS", "IL2Game", "IL2Korea", "FalconBMS", "WarThunder", "VTOLVR", "NuclearOption", "Wardogs", "StarCitizen", "EliteDangerous", "X4", "Arma3", "Arma2", "Arma2OA",
            "IL21946", "IL2CoD", "IL2CoDBlitz", "TinyCombatArena", "GRebels", "ProjectWingman", "AceCombat7", "Battlefield6", "Battlefield2042"
        };
        // Games where wheel brakes matter (fixed wing on wheels).
        private static readonly HashSet<string> BrakeGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DCS", "IL2Game", "IL2Korea", "FalconBMS", "WarThunder", "MSFS2020", "MSFS2024", "XPlane11", "XPlane12", "VTOLVRNative",
            "FSX", "FSXSE", "P3D4", "P3D5", "P3D6", "IL21946", "IL2CoD", "IL2CoDBlitz", "TinyCombatArena"
        };
        // Near-duplicate games: each counts half in the means (one game, two versions).
        private static readonly Dictionary<string, double> GameWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "MSFS2020", 0.5 }, { "MSFS2024", 0.5 }, { "XPlane11", 0.5 }, { "XPlane12", 0.5 }, { "Arma2", 0.5 }, { "Arma2OA", 0.5 },
            { "FSX", 0.5 }, { "FSXSE", 0.5 }, { "P3D4", 1 / 3.0 }, { "P3D5", 1 / 3.0 }, { "P3D6", 1 / 3.0 }, { "IL2CoD", 0.5 }, { "IL2CoDBlitz", 0.5 },
            { "Battlefield6", 0.5 }, { "Battlefield2042", 0.5 }
        };

        internal static double WeightOf(string target)
        {
            double w;
            return GameWeights.TryGetValue(target ?? string.Empty, out w) ? w : 1.0;
        }

        internal static double WMean(IEnumerable<EvalRow> rows, Func<EvalRow, double> value)
        {
            List<EvalRow> list = rows.ToList();
            double weight = list.Sum(r => r.Weight);
            return weight <= 0 ? 0 : list.Sum(r => r.Weight * value(r)) / weight;
        }

        private static readonly Regex Lever = new Regex(@"^(throttle|collective|quadrant)\.axis\.(main|left|right|throttle|throttle_twist|lever(\.\d+)?|levers?\.\d+|throttle\.\d+|\d+)$|^stick\.axis\.throttle$", RegexOptions.Compiled);
        private static readonly Regex SliderAxis = new Regex(@"\.axis\.slider(\.\d+)?$", RegexOptions.Compiled);
        private static readonly Regex GunTriggerControl = new Regex(@"^(stick|yoke)\.trigger\.", RegexOptions.Compiled);
        private static readonly Regex DetentControl = new Regex(@"\.detent(\.|$)", RegexOptions.Compiled);
        private static readonly Regex DetentLabel = new Regex(@"(?<!\btrigger\b.*)\bdetent\b|\bIDLE\b|\bTOGA\b|\bREV\b|\bCL\b|\bMCT\b|\bFLX\b|\bCLB\b|\bCUT ?OFF\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex IdleDetent = new Regex(@"\bIDLE\b|\bOFF\b|\bCUT ?OFF\b|detent\.idle|detent\.off|detent\.cutoff", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // (The jettison selector's positions - RACK, L FUS, R FUS, SAFE, STORES, selective - only choose what a jettison would drop.)
        internal static readonly Regex DestructiveFunction = new Regex(@"engine.*(start|stop|master)|(start|stop|master).*engine|starter|magneto|jett|eject|canopy|^gear|_gear|apu_(start|stop)|fuel_?cut|shutdown|parachute|dragchute|hook_?release|cargo_?release|mixture.*cut", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // A jettison function is only a selector position when the aircraft's own command says so (knob / selector /
        // position), not by its slot id: the P-51D's bomb salvo and the P-47D's tank release sit in those slots too.
        private static readonly Regex SelectorText = new Regex(@"selector|knob|position|select\b|cover|guard", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ReleaseText = new Regex(@"salvo|release|drop|unhook|emergency|jettison (button|stores|tank)|pull", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ThrottleFamily = new Regex(@"afterburner|idle|cut_?off|reverse|reverser|autothrottle|atc_|toga|throttle|engine.*(start|stop)|(start|stop).*engine", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[][] OppositeWords =
        {
            new[] { "start", "stop" }, new[] { "on", "off" }, new[] { "open", "close" }, new[] { "in", "out" }, new[] { "up", "down" }, new[] { "extend", "retract" },
            new[] { "arm", "safe" }, new[] { "increase", "decrease" }, new[] { "inc", "dec" }, new[] { "fwd", "aft" }, new[] { "left", "right" }, new[] { "lock", "unlock" },
            new[] { "engage", "disengage" }, new[] { "noseup", "nosedown" }, new[] { "deploy", "stow" }
        };

        // Two functions that do the opposite (engine start / stop, light on / off): the same words but one opposite pair.
        internal static bool Opposite(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return false;
            string[] x = Regex.Split(a.ToLowerInvariant().Replace("nose_down", "nosedown").Replace("nose_up", "noseup"), @"[^a-z0-9]+").Where(t => t.Length > 0).ToArray();
            string[] y = Regex.Split(b.ToLowerInvariant().Replace("nose_down", "nosedown").Replace("nose_up", "noseup"), @"[^a-z0-9]+").Where(t => t.Length > 0).ToArray();
            if (x.Length != y.Length)
                return false;
            int differ = 0;
            bool opposite = false;
            for (int i = 0; i < x.Length; i++)
            {
                if (x[i] == y[i])
                    continue;
                differ++;
                opposite = OppositeWords.Any(p => (p[0] == x[i] && p[1] == y[i]) || (p[1] == x[i] && p[0] == y[i]));
                // Left and right are a direction only at the end (trim left / right), not a side (left / right engine start).
                if (opposite && (x[i] == "left" || x[i] == "right") && i != x.Length - 1)
                    opposite = false;
            }
            return differ == 1 && opposite;
        }

        // The trigger's first stage of this fixed-wing aircraft is a detent before the gun (camera, sight recorder,
        // laser), not the gun: the catalog's own description says so.
        internal static bool CameraDetent(SetupRequest request, string plane)
        {
            return SetupPlanner.IsCameraDetent(request, plane);
        }

        // VTOL VR's own (native) joystick input has three axes only: rudder and the two toe brakes. Without pedals there is
        // nothing to set up there, so the row is left out (n/a) instead of scoring 0 %.
        internal static bool Applies(EvalTarget target, List<SetupInput> inputs)
        {
            if (string.Equals(target.Game, "VTOLVRNative", StringComparison.OrdinalIgnoreCase))
                return inputs.Any(i => i.IsAxis && i.Control.StartsWith("pedals.axis.", StringComparison.Ordinal));
            return true;
        }

        internal static string NotApplicableReason(EvalTarget target)
        {
            return string.Equals(target.Game, "VTOLVRNative", StringComparison.OrdinalIgnoreCase) ? "VTOL VR's native input is rudder and toe brakes only; no pedals in this setup" : "not applicable";
        }

        private static void Gate(EvalRow row, string gate, string status, string detail)
        {
            row.Gates.Add(new EvalGate { Gate = gate, Status = status, Detail = detail ?? string.Empty });
        }

        // The gates and the destructive check of one row (setup or pipeline).
        private static void HardGates(EvalRow row, EvalTarget target, SetupRequest request, SetupPlan plan, List<SetupPlanRelation> mine, string cls, HashSet<string> available)
        {
            string game = target.Game;
            string plane = target.Aircraft;
            bool nativeVtol = string.Equals(game, "VTOLVRNative", StringComparison.OrdinalIgnoreCase);
            Func<SetupPlanRelation, SetupInput> inputOf = r => plan.Inputs.FirstOrDefault(i => i.Device == r.Device && string.Equals(i.Input, r.Input, StringComparison.OrdinalIgnoreCase));
            Func<SetupPlanRelation, SetupDevice> deviceOf = r => plan.Devices.FirstOrDefault(d => d.Device == r.Device);
            Func<SetupPlanRelation, string> where = r => DeviceShort(plan, r.Device) + " " + r.Input + " (" + r.Control + (r.Label.Length > 0 ? " \"" + r.Label + "\"" : string.Empty) + ")" +
                                                        (r.Modifier.Length > 0 ? " + " + r.Modifier : string.Empty);
            // A relation does every function its commands are catalogued under for this aircraft (one command, several
            // functions: the UH-1H's armament switch is its weapon release and more).
            Dictionary<string, HashSet<string>> functionsOf = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (SetupCommand command in request.Commands.Where(c => string.Equals(c.Aircraft, plane, StringComparison.OrdinalIgnoreCase)))
            {
                HashSet<string> set;
                string key = command.Game + "|" + command.Action;
                if (!functionsOf.TryGetValue(key, out set))
                    functionsOf[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(command.Function);
            }
            Func<SetupPlanRelation, string, bool> does = (r, id) => string.Equals(r.Function, id, StringComparison.OrdinalIgnoreCase) ||
                r.Nodes.Any(n => string.Equals(n.Aircraft, plane, StringComparison.OrdinalIgnoreCase) && functionsOf.ContainsKey(n.Game + "|" + n.Action) && functionsOf[n.Game + "|" + n.Action].Contains(id));
            Func<string, List<SetupPlanRelation>> of = id => mine.Where(r => does(r, id)).ToList();
            Func<SetupPlanRelation, bool> clean = r => r.Modifier.Length == 0;
            Func<SetupPlanRelation, bool> onAxis = r => { SetupInput i = inputOf(r); return i != null ? i.IsAxis : r.IsAxis; };
            Func<SetupPlanRelation, bool> gripOf = r =>
            {
                SetupDevice d = deviceOf(r);
                return d != null && (d.Role == SetupRoles.MainStick || d.Role == SetupRoles.HeliStick || d.Role == SetupRoles.Throttle || d.Role == SetupRoles.Collective || d.Role == SetupRoles.LeftStick) &&
                       r.Control.IndexOf(".base.", StringComparison.Ordinal) < 0 && !r.Control.StartsWith("panel.", StringComparison.Ordinal);
            };
            List<SetupInput> axes = plan.Inputs.Where(i => i.IsAxis && plan.Devices.Any(d => d.Device == i.Device && SetupRoles.Binds(d.Role))).ToList();
            bool space = cls == SetupClasses.Space;
            bool air = cls == SetupClasses.Fixed || cls == SetupClasses.Rotary;

            // pitch / roll
            foreach (string axis in new[] { "pitch", "roll" })
            {
                if (nativeVtol)
                    break;
                List<SetupPlanRelation> list = of(axis);
                if (list.Any(r => onAxis(r) && Regex.IsMatch(r.Control, @"\.axis\." + axis + "$") && (deviceOf(r) == null || deviceOf(r).Role != SetupRoles.LeftStick)))
                    Gate(row, axis, "ok", where(list.First()));
                else if (!axes.Any(i => Regex.IsMatch(i.Control, @"\.axis\." + axis + "$")))
                    Gate(row, axis, "n/a", "hardware: no " + axis + " axis");
                else
                    Gate(row, axis, "fail", list.Count == 0 ? "not bound" : "on " + string.Join(", ", list.Select(where)));
            }

            // yaw: the pedals' rudder axis when there are pedals
            {
                List<SetupPlanRelation> list = of("rudder").Concat(of("space_yaw")).ToList();
                bool pedals = axes.Any(i => i.Control == "pedals.axis.rudder");
                bool fits = axes.Any(i => FitsYaw.IsMatch(i.Control));
                if (!fits && list.Count == 0)
                    Gate(row, "yaw", "n/a", "hardware: no rudder pedals, twist or rocker axis");
                else if (list.Count == 0)
                    Gate(row, "yaw", "fail", "not bound");
                else if (pedals && air && !list.Any(r => r.Control == "pedals.axis.rudder"))
                    Gate(row, "yaw", "fail", "rudder pedals connected but the rudder is on " + string.Join(", ", list.Select(where)));
                else if (!list.Any(r => onAxis(r) && FitsYaw.IsMatch(r.Control)))
                    Gate(row, "yaw", "fail", "on " + string.Join(", ", list.Select(where)));
                else
                    Gate(row, "yaw", "ok", where(list.First(r => FitsYaw.IsMatch(r.Control))));
            }

            // throttle: on a lever; a slider only without a lever; twin engines both on levers
            if (!nativeVtol)
            {
                // (a helicopter's engine power / condition levers are set once: the collective is its throttle gate)
                List<SetupPlanRelation> list = mine.Where(r => (Throttles.Contains(r.Function) && !(cls == SetupClasses.Rotary && !r.Function.EndsWith("nonab", StringComparison.Ordinal) &&
                                                                                                    !does(r, "throttleleftnonab") && !does(r, "throttlerightnonab"))) ||
                                                               (space && r.Function == "space_strafe_fwdaft")).ToList();
                // (review 7 M10 / L39: a left stick's spring-return Y only for a spaceship's forward / aft thrust, never a throttle)
                Func<SetupPlanRelation, bool> leftStickY = r => { SetupDevice d = deviceOf(r); return d != null && d.Role == SetupRoles.LeftStick && r.Control == "stick.axis.pitch" && r.Function == "space_strafe_fwdaft"; };
                Func<SetupPlanRelation, bool> gamepadY = r => { SetupDevice d = deviceOf(r); return d != null && string.Equals(d.Kind, "controller", StringComparison.OrdinalIgnoreCase) || (d != null && d.ModelId.IndexOf("xbox", StringComparison.OrdinalIgnoreCase) >= 0); };
                bool anyLever = axes.Any(i => Lever.IsMatch(i.Control));
                bool anySlider = axes.Any(i => SliderAxis.IsMatch(i.Control));
                // (A helicopter's engine levers on the collective's knobs are fine: set once for the flight.)
                Func<SetupPlanRelation, bool> good = r => onAxis(r) && (Lever.IsMatch(r.Control) || leftStickY(r) || gamepadY(r) || (!anyLever && SliderAxis.IsMatch(r.Control)) ||
                                                          (cls == SetupClasses.Rotary && r.Control.StartsWith("collective.axis.", StringComparison.Ordinal)));
                // (a gamepad without a throttle axis: the game's throttle increase / decrease buttons, unshifted on the pad)
                Func<SetupPlanRelation, bool> buttonGood = r => !onAxis(r) && clean(r) && gripOf(r);
                bool throttleButtons = list.Count == 0 && of("throttle_up_button").Any(buttonGood) && of("throttle_down_button").Any(buttonGood);
                if (throttleButtons)
                    Gate(row, "throttle", "ok", "throttle up / down buttons " + where(of("throttle_up_button").First(buttonGood)) + " / " + where(of("throttle_down_button").First(buttonGood)));
                else if (list.Count == 0 && !anyLever && !anySlider && !axes.Any(i => i.Control.StartsWith("stick.axis.ministick", StringComparison.Ordinal) || (i.Control.StartsWith("throttle.axis.ministick", StringComparison.Ordinal) && plan.Devices.Any(d => d.Device == i.Device && string.Equals(d.Kind, "gamepad", StringComparison.OrdinalIgnoreCase)))) && !plan.Devices.Any(d => d.Role == SetupRoles.LeftStick))
                    // (every flying game needs a throttle: a setup without any is unflyable there, not "n/a")
                    Gate(row, "throttle", air || space ? "fail" : "n/a", "hardware: no throttle lever, slider or left stick");
                // (review 7 M10: no lever or slider, only spring-return axes - a hardware limit when the plan says so)
                else if (list.Count == 0 && !anyLever && !anySlider && plan.Notes.Any(n => n.StartsWith("Throttle: no lever, slider or throttle wheel", StringComparison.Ordinal) && (n.IndexOf(plane, StringComparison.OrdinalIgnoreCase) >= 0 || n.Contains(" +"))))
                    Gate(row, "throttle", "n/a", "hardware limit: no lever or slider, only spring-return axes - the plan tells the user");
                else if (list.Count == 0)
                    Gate(row, "throttle", "fail", "not bound");
                else if (space ? !list.Any(good) : list.Any(r => !good(r)))
                    Gate(row, "throttle", "fail", (anyLever ? "a lever exists but " : string.Empty) + string.Join(", ", list.Where(r => !good(r)).Select(r => (r.FunctionName.Length > 0 ? r.FunctionName : r.Function) + " on " + where(r))));
                else
                    Gate(row, "throttle", "ok", string.Join(", ", list.Select(where).Distinct()));
            }

            // fire: the gun on the trigger, not behind a modifier
            bool combat = CombatGames.Contains(game) && !nativeVtol;
            if (combat)
            {
                bool has1 = available.Contains("trigger1ststage"), has2 = available.Contains("trigger2ndstage");
                string gun = has2 && (!has1 || CameraDetent(request, plane)) ? "trigger2ndstage" : has1 ? "trigger1ststage" : has2 ? "trigger2ndstage" : string.Empty;
                List<SetupInput> triggers = plan.Inputs.Where(i => !i.IsAxis && GunTriggerControl.IsMatch(i.Control) && plan.Devices.Any(d => d.Device == i.Device && (d.Role == SetupRoles.MainStick || d.Role == SetupRoles.HeliStick))).ToList();
                if (gun.Length == 0)
                {
                    if (game == "DCS")
                        Gate(row, "fire", "n/a", "the aircraft has no gun / trigger command");
                    else
                        Gate(row, "fire", "fail", "catalog: " + game + " has no gun / primary fire command");
                }
                else
                {
                    // Without a camera detent both stages fire guns (IL-2 groups 1 / 2, Bf 109 MG / cannon, one Fire command).
                    List<SetupPlanRelation> list = gun == "trigger2ndstage" && has2 && has1 && CameraDetent(request, plane) ? of(gun) : of("trigger1ststage").Concat(of("trigger2ndstage")).Distinct().ToList();
                    Func<SetupPlanRelation, bool> good = r => clean(r) && !onAxis(r) && (triggers.Count > 0 ? GunTriggerControl.IsMatch(r.Control) : gripOf(r)) &&
                                                         // (a trigger's catalog name - "1 (trigger)" - is no label of another function)
                                                         (GunTriggerControl.IsMatch(r.Control) || !LabelVeto(request, gun, r.Label));
                    // Nothing else on the gun's trigger (an eject, an engine stop, another function of this aircraft).
                    Func<SetupPlanRelation, List<SetupPlanRelation>> alsoOn = g => mine.Where(o => o.Device == g.Device && string.Equals(o.Input, g.Input, StringComparison.OrdinalIgnoreCase) && o.Modifier == g.Modifier &&
                                                                                                !string.IsNullOrEmpty(o.Function) && o.Function != "trigger1ststage" && o.Function != "trigger2ndstage" && o.Function != "launchweapon" &&
                                                                                                !does(o, "trigger1ststage") && !does(o, "trigger2ndstage")).ToList();
                    SetupPlanRelation onTrigger = list.FirstOrDefault(good);
                    if (onTrigger != null && alsoOn(onTrigger).Count > 0)
                        Gate(row, "fire", "fail", "the trigger also does " + string.Join(", ", alsoOn(onTrigger).Select(o => o.FunctionName.Length > 0 ? o.FunctionName : o.Function).Distinct()) + ": " + where(onTrigger));
                    else if (list.Any(good))
                        Gate(row, "fire", "ok", where(list.First(good)));
                    else if (list.Count == 0)
                        Gate(row, "fire", "fail", Name(request, gun) + " not bound" + (gun == "trigger2ndstage" && of("trigger1ststage").Any(r => GunTriggerControl.IsMatch(r.Control)) ? " (the camera / first detent is on the trigger)" : string.Empty));
                    else
                        Gate(row, "fire", "fail", Name(request, gun) + " on " + string.Join(", ", list.Select(where)) + (triggers.Count > 0 ? " (not the trigger" + (list.Any(r => !clean(r)) ? ", behind a modifier" : string.Empty) + ")" : string.Empty));
                }

                // weapon release
                if (available.Contains("launchweapon"))
                {
                    List<SetupPlanRelation> list = of("launchweapon");
                    Func<SetupPlanRelation, bool> good = r => clean(r) && !onAxis(r) && gripOf(r) && !LabelVeto(request, "launchweapon", r.Label);
                    Gate(row, "release", list.Any(good) ? "ok" : "fail", list.Any(good) ? where(list.First(good)) : list.Count == 0 ? "not bound" : "on " + string.Join(", ", list.Select(where)));
                }
                else if (game != "DCS" && !space)
                {
                    Gate(row, "release", "n/a", "the game has no weapon release command");
                }
            }

            // countermeasures (where the aircraft has them)
            // (L33: not for a warbird or an airliner - the Bf 109's flare pistol is a signal flare - and only the functions that
            // dispense count: CMS forward / aft; CMS left / right / ECM select programs or jammers - the Mi-8's UV-26 SERIYA /
            // ZALP buttons release nothing)
            string cmsClass = EssentialClass(target, cls);
            if (cmsClass == "prop" || cmsClass == "civil")
            {
                if (available.Contains("cms_fwd_start_program_or_just_flare") || available.Contains("cms_aft_panic_or_just_chaff"))
                    Gate(row, "countermeasures", "n/a", "no countermeasures in this class of aircraft (a signal flare is no decoy)");
            }
            else if (!nativeVtol && (available.Contains("cms_fwd_start_program_or_just_flare") || available.Contains("cms_aft_panic_or_just_chaff")))
            {
                List<SetupPlanRelation> list = mine.Where(r => Cms.Contains(r.Function) &&
                                                               (r.Function == "cms_fwd_start_program_or_just_flare" || r.Function == "cms_aft_panic_or_just_chaff")).ToList();
                Func<SetupPlanRelation, bool> good = r => clean(r) && !onAxis(r) && (gripOf(r) || ThrottleHandButton(plan, inputOf(r)));
                // (owner 2026-11-16: on a grip behind a shift is reachable, degraded - not missing; a phase layer is not)
                Func<SetupPlanRelation, bool> shiftedGrip = r => !onAxis(r) && gripOf(r) && r.Modifier.Length > 0 && plan.Modifiers.Any(m => m.Name == r.Modifier && !m.Switch);
                if (list.Any(good))
                    Gate(row, "countermeasures", "ok", where(list.First(good)));
                // (review 7 M14 / L38: behind a shift only when no unshifted push button of the hands - grip or the throttle
                // hand's quadrant buttons - was left for them, and the plan says so)
                else if (list.Any(shiftedGrip) && HandButtonsHeld(request, plan, mine) &&
                         plan.Notes.Any(n => n.StartsWith("Countermeasures behind a shift", StringComparison.Ordinal)))
                    Gate(row, "countermeasures", "ok", "degraded: behind a shift " + where(list.First(shiftedGrip)));
                else if (list.Any(shiftedGrip))
                    Gate(row, "countermeasures", "fail", "behind a shift " + where(list.First(shiftedGrip)) + " while an unshifted hand button holds a lesser function (or the plan does not say so)");
                else
                    Gate(row, "countermeasures", "fail", list.Count == 0 ? "not bound" + (plan.Notes.Any(n => n.StartsWith("Countermeasures: no grip button is left", StringComparison.Ordinal)) ? " (the plan says so)" : string.Empty) : "on " + string.Join(", ", list.Select(where)));
            }

            // wheel brakes: both toe brakes on brake axes (one brake lever for both counts), or the button, unshifted
            bool wheels = BrakeGames.Contains(game) && (cls == SetupClasses.Fixed || available.Any(a => a.StartsWith("wheelbrake", StringComparison.OrdinalIgnoreCase)));
            if (wheels)
            {
                List<SetupPlanRelation> left = mine.Where(r => r.Function.StartsWith("wheelbrakeleft", StringComparison.OrdinalIgnoreCase)).ToList();
                List<SetupPlanRelation> right = mine.Where(r => r.Function.StartsWith("wheelbrakeright", StringComparison.OrdinalIgnoreCase)).ToList();
                List<SetupPlanRelation> button = of("wheelbrakes_button");
                Func<SetupPlanRelation, bool> toeOk = r => onAxis(r) && FitsBrake.IsMatch(r.Control);
                bool gripButtons = plan.Inputs.Any(i => !i.IsAxis && SetupPlanner.IsGrip(i.Control) && plan.Devices.Any(d => d.Device == i.Device && (d.Role == SetupRoles.MainStick || d.Role == SetupRoles.Throttle || d.Role == SetupRoles.HeliStick || d.Role == SetupRoles.Collective || d.Role == SetupRoles.LeftStick)));
                Func<SetupPlanRelation, bool> buttonOk = r => clean(r) && !onAxis(r) && (!gripButtons || gripOf(r));
                bool anyBrakeCommand = available.Any(a => a.StartsWith("wheelbrake", StringComparison.OrdinalIgnoreCase));
                bool brakeAxes = axes.Any(i => FitsBrake.IsMatch(i.Control));
                // Aircraft with one brake lever for both wheels (Spitfire, Ka-50, Mi-8: the catalog has one side only).
                bool oneSided = !available.Any(f => f.StartsWith("wheelbrakeleft", StringComparison.OrdinalIgnoreCase)) || !available.Any(f => f.StartsWith("wheelbrakeright", StringComparison.OrdinalIgnoreCase));
                if ((left.Any(toeOk) && right.Any(toeOk)) || (oneSided && left.Concat(right).Any(toeOk)))
                    Gate(row, "wheel_brakes", "ok", string.Join(" / ", left.Concat(right).Where(toeOk).Select(where).Distinct()));
                else if (button.Any(buttonOk))
                    Gate(row, "wheel_brakes", "ok", where(button.First(buttonOk)));
                // (owner 2026-11-16: the gun, the release and the flares keep the grip buttons of a combat aircraft; the wheel
                // brake button then sits on a momentary button of the throttle hand - the TCA quadrant's lever-end buttons)
                else if (button.Any(r => clean(r) && !onAxis(r) && ThrottleHandButton(plan, inputOf(r))) && GripButtonsHeldByWeapons(plan, mine))
                    Gate(row, "wheel_brakes", "ok", "degraded: on the throttle hand (the grip buttons hold the gun, the release and the flares) " + where(button.First(r => clean(r) && !onAxis(r) && ThrottleHandButton(plan, inputOf(r)))));
                else if (!anyBrakeCommand)
                    Gate(row, "wheel_brakes", nativeVtol ? "n/a" : "fail", nativeVtol ? "no brake axes" : "catalog: no wheel brake command for " + plane);
                else if (!available.Contains("wheelbrakes_button") && !brakeAxes)
                    Gate(row, "wheel_brakes", "n/a", "game limit: only brake axes (no button brake command in the game) and the devices have no toe brakes or brake lever");
                else if (left.Concat(right).Any(r => !onAxis(r)))
                    Gate(row, "wheel_brakes", "fail", "toe brake axis command on a button: " + string.Join(", ", left.Concat(right).Where(r => !onAxis(r)).Select(where)));
                else if (left.Any(toeOk) != right.Any(toeOk))
                    Gate(row, "wheel_brakes", "fail", "one toe brake only (" + string.Join(", ", left.Concat(right).Select(where)) + ")");
                else
                    Gate(row, "wheel_brakes", "fail", left.Count + right.Count + button.Count == 0 ? "not bound" : "on " + string.Join(", ", left.Concat(right).Concat(button).Select(where)));
            }

            // gear and flaps (bound; where they sit is the destructive check's business)
            if (!nativeVtol && (air || space))
            {
                foreach (string op in new[] { "gear", "flaps" })
                {
                    if (op == "flaps" && cls != SetupClasses.Fixed)
                        continue;
                    if (row.OpsDone.Contains(op))
                        Gate(row, op, "ok", string.Empty);
                    else if (row.Gaps.Any(g => g.Op == op))
                        Gate(row, op, "fail", row.Gaps.First(g => g.Op == op).Why);
                }
            }

            // looking around: snap views (all four) or the look axes; zoom alone does not count
            if (!nativeVtol && !string.Equals(game, "VTOLVR", StringComparison.OrdinalIgnoreCase))
            {
                string[] snaps = { "view_snap_forward", "view_snap_back", "view_snap_left", "view_snap_right" };
                string[] look = { "view_look_horizontal", "view_look_vertical" };
                bool catalog = snaps.Concat(look).Any(available.Contains);
                // (looking around is time-critical: snap views count on a grip without a modifier, look axes on axes that
                // spring back to the centre)
                Func<SetupPlanRelation, bool> snapGood = r => !onAxis(r) && clean(r) && gripOf(r);
                Func<SetupPlanRelation, bool> lookGood = r => onAxis(r) && (SetupPlanner.CentringAxis(r.Control) || SetupPlanner.CentringAxis(inputOf(r)));
                List<string> snapIds = snaps.Where(available.Contains).ToList();
                bool snapOk = snapIds.Count > 0 && snapIds.All(f => of(f).Any(snapGood));
                bool lookOk = look.Where(available.Contains).Any() && look.Where(available.Contains).All(f => of(f).Any(lookGood));
                bool anyLook = snaps.Concat(look).Any(f => of(f).Count > 0);
                // The grip hats of the hands' devices (any layer): a device with fewer than two has no hat to spare.
                HashSet<string> gripHats = new HashSet<string>(plan.Inputs.Where(i => !i.IsAxis && HatControl.IsMatch(i.Control) && SetupPlanner.IsGrip(i.Control) && HandDevice(plan, i.Device))
                    .GroupBy(i => i.Device + "|" + i.Control.Substring(0, i.Control.LastIndexOf('.'))).Where(h => h.Count() >= 4).Select(h => h.Key), StringComparer.Ordinal);
                // (a grip hat the snap views cannot have whole: one of its directions holds an earlier essential - trim, target,
                // countermeasures - or, from the middle of the slider on, its real aircraft function)
                // (and a hat whose printed label names another function - the Warthog throttle's MIC SWITCH - is no free hat:
                // the label keeps it)
                // (and a relation holding a DCS input layer - "Default"'s countermeasures or trim - is active next to every
                // aircraft of the game, as the planner counts it: at "fully real" it holds the dual T.16000M's hats in eleven
                // modules)
                List<SetupPlanRelation> heldBy = mine.Concat(plan.Placed.Where(r => !mine.Contains(r) && r.Aircraft.Any(a =>
                {
                    SetupAircraft layer;
                    return request.Aircraft.TryGetValue(a, out layer) && layer.SharedLayer && string.Equals(layer.Game, game, StringComparison.OrdinalIgnoreCase);
                }))).ToList();
                Func<string, bool> heldHat = h => plan.Inputs.Any(i => !i.IsAxis && i.Control.LastIndexOf('.') > 0 && h == i.Device + "|" + i.Control.Substring(0, i.Control.LastIndexOf('.')) &&
                                                                       SetupPlanner.FunctionCockpitLabel(i.Label) && LabelVeto(request, "view_snap_forward", i.Label)) || heldBy.Any(r => r.Modifier.Length == 0 && !onAxis(r) &&
                                                               h == r.Device + "|" + r.Control.Substring(0, Math.Max(0, r.Control.LastIndexOf('.'))) &&
                                                               ((RealismValue >= 0.5 && r.RealKnown && r.RealMatch >= 0.99) ||
                                                                SetupPlanner.EssentialRank(r.Function) < SetupPlanner.EssentialRank("view_snap_forward")));
                // Snap views behind one modifier, all on one grip hat: slow, but on a one-hat device the only way.
                string shiftedAt = string.Empty;
                if (!snapOk && snapIds.Count > 0)
                {
                    foreach (SetupPlanRelation first in of(snapIds[0]).Where(r => !onAxis(r) && !clean(r) && gripOf(r)))
                    {
                        string hat = first.Device + "|" + first.Control.Substring(0, Math.Max(0, first.Control.LastIndexOf('.'))) + "|" + first.Modifier;
                        if (snapIds.All(f => of(f).Any(r => !onAxis(r) && gripOf(r) && r.Device + "|" + r.Control.Substring(0, Math.Max(0, r.Control.LastIndexOf('.'))) + "|" + r.Modifier == hat)))
                        {
                            shiftedAt = where(first);
                            break;
                        }
                    }
                }
                if (HeadTrackingValue && SetupPlanner.HeadTrackingGame(game))
                {
                    // With head tracking or VR the plan spends no input on looking around (the recenter is fine).
                    // (L24: the padlock and the look-ahead count as look buttons there; the recenter must be bound)
                    List<SetupPlanRelation> htLook = snaps.Concat(look).Concat(new[] { "view_padlock", "space_look_ahead_toggle" }).SelectMany(f => of(f)).ToList();
                    if (htLook.Count > 0)
                        Gate(row, "look", "fail", "head tracking / VR: no look-around buttons wanted, but " + string.Join(", ", htLook.Select(where).Distinct().Take(3)));
                    else if (available.Contains("view_center") && of("view_center").Count == 0 && plan.Notes.Any(n => n.StartsWith("Head tracking: no input is left for the view recenter", StringComparison.Ordinal)))
                        Gate(row, "look", "n/a", "head tracking / VR: no input left for the recenter - the plan tells the user to use the tracking software's own recenter key");
                    else if (available.Contains("view_center") && of("view_center").Count == 0)
                        Gate(row, "look", "fail", "head tracking / VR: the recenter is not bound (and the plan does not say so)");
                    else
                        Gate(row, "look", "n/a", "head tracking / VR: no buttons for looking around" + (of("view_center").Count > 0 ? " (recenter on " + where(of("view_center").First()) + ")" : string.Empty));
                }
                else if (snapOk || lookOk)
                    Gate(row, "look", "ok", snapOk ? "snap views " + where(of(snapIds[0]).First(snapGood)) : "look axes " + where(of(look.First(available.Contains)).First(lookGood)));
                else if (shiftedAt.Length > 0 && gripHats.Count < 2)
                    Gate(row, "look", "ok", "slow: snap views behind a modifier (" + gripHats.Count.ToString(CultureInfo.InvariantCulture) + " grip hat on the hands' devices) " + shiftedAt);
                // (every grip hat holds an earlier essential - trim, target, countermeasures - or, from the middle of the slider on,
                // its real aircraft function (the Warthog in the A-10C): the snap views go behind a modifier by the documented
                // order, as the wizard promises)
                else if (shiftedAt.Length > 0 && gripHats.All(heldHat))
                    Gate(row, "look", "ok", "slow: snap views behind a modifier (every grip hat holds an earlier essential or its real aircraft function) " + shiftedAt);
                // (owner 2026-11-16: an axes-only game whose look left / right is on a spring-centred axis while up / down found
                // none - a gamepad's right stick Y holds the throttle: slower, said in the plan)
                else if (snapIds.Count == 0 && of("view_look_horizontal").Any(lookGood) && plan.Notes.Any(n => n.StartsWith("Looking around in " + game + ": no spring-centred axis", StringComparison.Ordinal)))
                    Gate(row, "look", "ok", "degraded: look left / right " + where(of("view_look_horizontal").First(lookGood)) + ", up / down found no spring-centred axis (the plan says so)");
                else if (anyLook)
                    Gate(row, "look", "fail", "looking around only " + string.Join(", ", snaps.Concat(look).SelectMany(f => of(f)).Select(where).Distinct().Take(3)) + " (snap views need a grip without a modifier, look axes a centring axis" +
                                              (shiftedAt.Length > 0 ? "; grip hats free for them: " + string.Join(", ", gripHats.Where(h => !heldHat(h)).Take(4)) : string.Empty) + ")");
                else if (!catalog)
                {
                    // JoyPro does not manage this game's camera: its own look-around needs a grip hat the plan left free.
                    string free = FreeGripHat(plan, plane);
                    if (free.Length == 0 && gripHats.Count < 2)
                        Gate(row, "look", "n/a", "device too small: JoyPro does not manage " + game + "'s camera and the hands' devices have " + gripHats.Count.ToString(CultureInfo.InvariantCulture) + " grip hat");
                    else
                    Gate(row, "look", free.Length > 0 ? "ok" : "fail", free.Length > 0
                        ? "the game's own look-around: JoyPro does not manage " + game + "'s camera, " + free + " is left free for it"
                        : "JoyPro does not manage " + game + "'s camera and no grip hat is left free for the game's own look-around");
                }
                else
                    // (review 6 M5 / L31, by rule for every setup: one four-way grip hat on the hands, held by an earlier
                    // essential, no shift layer in the plan and the plan tells the user - a hardware limit, said, not silent)
                    if (gripHats.Count <= 1 && gripHats.All(heldHat) && plan.Modifiers.Count(m => !m.Switch) == 0 &&
                        plan.Notes.Any(n => n.StartsWith("Looking around: no free hat and no button for a shift", StringComparison.Ordinal)))
                        Gate(row, "look", "n/a", "hardware limit: one grip hat (an earlier essential's) and no button for a shift - the plan tells the user");
                    // (owner 2026-11-16: a game that looks around with axes only and found no spring-centred axis: a free grip hat
                    // named for Hat to axes counts (slower); with neither it is a hardware limit when the plan says so)
                    else if (snapIds.Count == 0 && FreeGripHat(plan, plane).Length > 0 &&
                             plan.Notes.Any(n => n.StartsWith("Looking around in " + game + ": no spring-centred axis is left", StringComparison.Ordinal)))
                        Gate(row, "look", "ok", "degraded: no spring-centred axis left, " + FreeGripHat(plan, plane) + " stays free for Hat to axes (the plan says so)");
                    else if (snapIds.Count == 0 && FreeGripHat(plan, plane).Length == 0 && !FreeCentringAxis(plan, plane) &&
                             plan.Notes.Any(n => n.StartsWith("Looking around in " + game + ": no spring-centred axis and no free grip hat", StringComparison.Ordinal)))
                        Gate(row, "look", "n/a", "hardware limit: no spring-centred axis and no free grip hat - the plan tells the user");
                    else
                    Gate(row, "look", "fail", "no snap views or look axes bound" + (available.Contains("zoom") && of("zoom").Count > 0 ? " (zoom only)" : string.Empty) +
                                              (gripHats.Any(h => !heldHat(h)) ? " (grip hats free for them: " + string.Join(", ", gripHats.Where(h => !heldHat(h)).Take(4)) + ")" : string.Empty));
                // (owner 2026-11-16: looking around is the pan left / right / up / down AND the recenter - without head tracking a
                // look that cannot be brought back to centre is no look-around; the recenter may sit behind a modifier)
                if (!(HeadTrackingValue && SetupPlanner.HeadTrackingGame(game)) && available.Contains("view_center") && of("view_center").Count(r => !onAxis(r)) == 0)
                {
                    EvalGate lookGate = row.Gates.LastOrDefault(g => g.Gate == "look");
                    // (a hardware limit the plan tells the user about - the game's own recenter key - is degraded, not failed)
                    bool said = plan.Notes.Any(n => n.StartsWith("Looking around: no input is left for the view recenter", StringComparison.Ordinal) && n.IndexOf(plane, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                plan.Notes.Any(n => n.StartsWith("Looking around: no input is left for the view recenter", StringComparison.Ordinal) && n.Contains(" +"));
                    if (lookGate != null && lookGate.Status == "ok")
                    {
                        lookGate.Status = said ? "ok" : "fail";
                        lookGate.Detail = (said ? "degraded: no input left for the view recenter (the plan says so) - " : "the view recenter is not bound - ") + lookGate.Detail;
                    }
                }
                // (owner 2026-11-16: zoom is a look essential with and without head tracking - a zoom axis or the zoom-in button;
                // with no input left the plan says so)
                bool zoomAvailable = available.Contains("zoom") || available.Contains("fov_toggle_china_hat_fwd");
                bool zoomBound = of("zoom").Count > 0 || of("fov_toggle_china_hat_fwd").Count > 0;
                row.Zoom = !zoomAvailable ? "n/a" : zoomBound ? "ok" : "missing";
                if (zoomAvailable)
                {
                    bool zoomSaid = plan.Notes.Any(n => n.StartsWith("Zoom: no input is left for zooming", StringComparison.Ordinal) && (n.IndexOf(plane, StringComparison.OrdinalIgnoreCase) >= 0 || n.Contains(" +")));
                    if (zoomBound)
                        Gate(row, "zoom", "ok", where(of("zoom").Concat(of("fov_toggle_china_hat_fwd")).First()));
                    else
                        Gate(row, "zoom", zoomSaid ? "n/a" : "fail", zoomSaid ? "hardware limit: no input left for zooming (the plan says so)" : "zoom not bound");
                }
            }

            // axis commands on axes, button commands on buttons (a key brake command is a button command)
            {
                List<string> wrongKind = new List<string>();
                // (L19: a thumb stick's hat / direction mode and its analog mode are one physical control - VKB A1 - used in
                // one mode only, never trim on the hat and look axes on the analog mode)
                foreach (IGrouping<string, SetupPlanRelation> stick in mine.Where(r => Regex.IsMatch(r.Control ?? string.Empty, @"^\w+\.(axis\.)?ministick2?\."))
                             .GroupBy(r => r.Device + "|" + Regex.Replace(Regex.Replace(r.Control, @"\.axis\.", "."), @"\.[^.]+$", string.Empty)))
                {
                    List<SetupPlanRelation> both = stick.ToList();
                    if (both.Any(r => r.Control.Contains(".axis.ministick")) && both.Any(r => !r.Control.Contains(".axis.") && !r.Control.EndsWith(".press", StringComparison.Ordinal)))
                        wrongKind.Add("one thumb stick in both modes (" + string.Join(", ", both.Select(r => r.FunctionName.Length > 0 ? r.FunctionName : r.Function).Distinct().Take(4)) + ") " + where(both[0]));
                }
                foreach (SetupPlanRelation r in mine)
                {
                    SetupFunction function;
                    SetupInput input = inputOf(r);
                    if (input == null || !request.Functions.TryGetValue(r.Function ?? string.Empty, out function))
                        continue;
                    // The game's own command decides: a relation's commands that are of the input's kind are right there,
                    // whatever catalog slot they share with the scored aircraft (X-Plane's flaps axis, a roll-left button).
                    if (function.IsAxis != input.IsAxis && r.Nodes.Count > 0 && r.Nodes.All(n => CommandKind(n.Game, n.Action) == input.IsAxis))
                        continue;
                    if (function.IsAxis != input.IsAxis)
                        wrongKind.Add((r.FunctionName.Length > 0 ? r.FunctionName : r.Function) + (function.IsAxis ? " (axis) on a button " : " (button) on an axis ") + where(r));
                    // A centred rate (TDC slew, strafe sideways / up-down, head look) on an axis that stays where it is left
                    // drifts; a throttle on a spring-centred stick or twist snaps back to half power (a gamepad and a left
                    // stick's Y are the exception).
                    else if (function.IsAxis && input.IsAxis && SetupPlanner.CentredFunction.IsMatch(r.Function) && !SetupPlanner.CentringAxis(input))
                        wrongKind.Add((r.FunctionName.Length > 0 ? r.FunctionName : r.Function) + " (centred) on an axis that does not centre " + where(r));
                    else if (function.IsAxis && input.IsAxis && Throttles.Contains(r.Function) && (Regex.IsMatch(input.Control, @"\.axis\.(ministick2?\.[xy]|twist|rudder)$") || string.Equals(input.Type, SetupPlanner.CentringType, StringComparison.OrdinalIgnoreCase) ||
                                                                                                 (deviceOf(r) != null && deviceOf(r).Role == SetupRoles.LeftStick && Regex.IsMatch(input.Control, @"^stick\.axis\.(pitch|roll)$"))) &&
                             !(deviceOf(r) != null && (string.Equals(deviceOf(r).Kind, "controller", StringComparison.OrdinalIgnoreCase) || deviceOf(r).ModelId.IndexOf("xbox", StringComparison.OrdinalIgnoreCase) >= 0)))
                        wrongKind.Add((r.FunctionName.Length > 0 ? r.FunctionName : r.Function) + " on a spring-centred axis " + where(r));
                }
                Gate(row, "kinds", wrongKind.Count == 0 ? "ok" : "fail", string.Join("; ", wrongKind.Distinct().Take(6)));
            }

            // Switches: a latched switch holds only an ON / OFF pair position (a held or one-shot function there is held all
            // flight or fires at every flip); a cockpit label on a base or panel (FUEL FLOW L, EAC ARM) holds only what it
            // names.
            {
                List<string> misuse = new List<string>();
                foreach (SetupPlanRelation r in mine.Where(x => !x.IsAxis && !string.IsNullOrEmpty(x.Function)))
                {
                    SetupInput input = inputOf(r);
                    SetupFunction function;
                    if (input == null || !request.Functions.TryGetValue(r.Function, out function))
                        continue;
                    string name = r.FunctionName.Length > 0 ? r.FunctionName : r.Function;
                    // (a lever detent is held while the lever sits there: the lever's own function - afterburner, TOGA - belongs)
                    if (string.Equals(input.Type, "latched", StringComparison.OrdinalIgnoreCase) && !SetupPlanner.LatchedFits(function, input) && !DetentControl.IsMatch(input.Control) &&
                        !mine.Any(o => o.Name == r.Name && o.Device == r.Device && o.Input == r.Input && request.Functions.ContainsKey(o.Function ?? string.Empty) && request.Functions[o.Function].PairGroup.Length > 0))
                        misuse.Add(name + " (no on / off pair) on the latched switch " + where(r));
                    else if (!SetupPlanner.IsGrip(input.Control) && !DetentControl.IsMatch(input.Control) && !DetentLabel.IsMatch(input.Label ?? string.Empty) && SetupPhases.IsCockpitLabel(input.Label) && !SetupPlanner.GamepadButtonLabel(input.Label) && !SetupPhases.LabelNames(function, input.Label) &&
                             !mine.Any(o => o.Name == r.Name && o.Device == r.Device && o.Input == r.Input && request.Functions.ContainsKey(o.Function ?? string.Empty) && SetupPhases.LabelNames(request.Functions[o.Function], input.Label)))
                        misuse.Add(name + " on the switch labelled for another function " + where(r));
                    // A printed function label on a grip (the Warthog throttle's SPEED BRAKE) holds only what it names.
                    else if (SetupPlanner.IsGrip(input.Control) && SetupPlanner.FunctionCockpitLabel(input.Label) && LabelVeto(request, r.Function, input.Label) &&
                             !mine.Any(o => o.Name == r.Name && o.Device == r.Device && o.Input == r.Input && !string.IsNullOrEmpty(o.Function) && !LabelVeto(request, o.Function, input.Label)))
                        misuse.Add(name + " on the grip control labelled for another function " + where(r));
                    // A position of a mode / profile selector is always held by one of them: nothing goes there.
                    else if (SetupPlanner.IsSelector(input))
                        misuse.Add(name + " on a mode selector position " + where(r));
                    // (canopy, tail hook, refuel probe only on a latched switch whose label names them - review 5 m1)
                    else if (string.Equals(input.Type, "latched", StringComparison.OrdinalIgnoreCase) && SetupPlanner.LabelOnlyFunction.IsMatch(r.Function) && !SetupPhases.LabelNames(function, input.Label ?? string.Empty))
                        misuse.Add(name + " on an unlabelled latched switch " + where(r));
                    // (L20: afterburner / TOGA / WEP only on a detent known to be at the lever's top end - not the Bravo's or the
                    // Logitech quadrant's reverse / below-idle detent)
                    else if ((DetentControl.IsMatch(input.Control) || DetentLabel.IsMatch(input.Label ?? string.Empty) || string.Equals(input.Type, "detent", StringComparison.OrdinalIgnoreCase)) &&
                             SetupPlanner.TopEnd(r.Function) && !SetupPlanner.TopDetent(input.Control, input.Label))
                        misuse.Add(name + " on a detent not at the lever's top end " + where(r));
                }
                // A lever printed "Flaps" holds only flaps; a printed gear lever only the gear (no landing lights there).
                foreach (SetupPlanRelation r in mine.Where(x => !string.IsNullOrEmpty(x.Function)))
                {
                    SetupInput input = inputOf(r);
                    if (input == null)
                        continue;
                    string name = r.FunctionName.Length > 0 ? r.FunctionName : r.Function;
                    if (input.IsAxis && Regex.IsMatch(input.Label ?? string.Empty, @"\bflaps?\b", RegexOptions.IgnoreCase) && r.Function.IndexOf("flap", StringComparison.OrdinalIgnoreCase) < 0)
                        misuse.Add(name + " on the axis labelled for the flaps " + where(r));
                    else if (!input.IsAxis && (input.Control.IndexOf(".lever.gear", StringComparison.Ordinal) >= 0 ||
                                               (Regex.IsMatch(input.Label ?? string.Empty, @"\b(gear|landing gear)\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(input.Label ?? string.Empty, @"warn|silence|horn|test", RegexOptions.IgnoreCase))) &&
                             !Regex.IsMatch(r.Function, "^(gear|gearin|gearout|gear_toggle)", RegexOptions.IgnoreCase) && !r.Function.StartsWith("gear", StringComparison.OrdinalIgnoreCase))
                        misuse.Add(name + " on the gear lever " + where(r));
                    // (owner 2026-11-16: the gear never on the press of an analog thumb stick / slew - pressed while slewing)
                    else if (!input.IsAxis && Regex.IsMatch(r.Function, "^(gear_toggle|gearin|gearout)$") && Regex.IsMatch(input.Control, @"\.(ministick2?|thumbstick|slew)\.press$"))
                        misuse.Add(name + " on the press of a thumb stick " + where(r));
                }
                Gate(row, "switches", misuse.Count == 0 ? "ok" : "fail", string.Join("; ", misuse.Distinct().Take(6)));
            }

            // Modifiers: a shift button is held by hand - a momentary push button without a function label, never a lever
            // detent, an axis zone button, a latched switch or a selector position; the mode switch (phase layers) is no
            // switch labelled for a function (ENG OPER L, ENG 1 MASTER).
            {
                List<string> wrongModifier = new List<string>();
                foreach (SetupPlanModifier m in plan.Modifiers)
                {
                    SetupInput input = plan.Inputs.FirstOrDefault(i => i.Device == m.Device && string.Equals(i.Input, m.Input, StringComparison.OrdinalIgnoreCase));
                    if (input == null || m.Existing)
                        continue;
                    bool modeLabel = Regex.IsMatch(input.Label ?? string.Empty, @"\bmode\b", RegexOptions.IgnoreCase);
                    string at = m.Name + " = " + DeviceShort(plan, m.Device) + " " + m.Input + " (" + input.Control + ((input.Label ?? string.Empty).Length > 0 ? " \"" + input.Label + "\"" : string.Empty) + ")";
                    if (!m.Switch && !SetupPlanner.ModifierInput(input))
                        wrongModifier.Add(at);
                    else if (m.Switch && !modeLabel && SetupPhases.IsCockpitLabel(input.Label))
                        wrongModifier.Add(at + " (a switch labelled for a function)");
                    // (a guarded switch, or one whose label names anything but the control - RUD TRIM - is no mode switch)
                    else if (m.Switch && !SetupPhases.ModeSwitchPosition(input.Control, "latched", input.Label, out string _, out string _, out int _))
                        wrongModifier.Add(at + " (a guarded or labelled switch)");
                    // (L22: a shift is held while the other hand presses the shifted button - never on the stick's base while the
                    // left hand holds a throttle, collective or left stick)
                    else if (!m.Switch && input.Control.StartsWith("stick.base.", StringComparison.Ordinal) &&
                             plan.Devices.Any(d => SetupRoles.Binds(d.Role) && (d.Role == SetupRoles.Throttle || d.Role == SetupRoles.Collective || d.Role == SetupRoles.LeftStick)))
                        wrongModifier.Add(at + " (on the stick's base while the left hand holds the throttle)");
                }
                Gate(row, "modifiers", wrongModifier.Count == 0 ? "ok" : "fail", string.Join("; ", wrongModifier.Distinct().Take(4)));
            }

            Destructive(row, request, plan, mine, where, available);
            // (the headline: judged before a small device's waiver, which only explains why a gate could not be met)
            Essentials(row, target, request, plan, cls, available, of, clean, onAxis, deviceOf, inputOf, where);
            WaiveTooSmall(row, plan, mine, does);
        }

        // The documented priority of the fly-and-fight functions for grip buttons when a device has too few: gun, wheel
        // brake button, weapon release, flare / chaff, trim, gear, flaps, look-around. A gate of this list that is not met
        // is waived (n/a, "device too small") only when every grip button it may use holds a function earlier in the list
        // (or a modifier): the planner then had no room. One grip button holding anything else (TMS, airbrake, a
        // mode) and the gate fails: the planner gave a less important function the place.
        // (the order of the in-flight essentials, docs/auto-setup-eval.md#in-flight-essentials: airbrake and target are no
        // gates of their own, they only count as earlier functions for the look-around gate)
        internal static readonly string[] GripPriority = { "fire", "release", "countermeasures", "wheel_brakes", "trim", "target", "look", "gear", "flaps", "airbrake" };
        private static readonly Dictionary<string, string[]> PriorityFunctions = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "fire", new[] { "trigger1ststage", "trigger2ndstage" } },
            { "wheel_brakes", new[] { "wheelbrakes_button" } },
            { "release", new[] { "launchweapon" } },
            { "countermeasures", new[] { "cms_fwd_start_program_or_just_flare", "cms_aft_panic_or_just_chaff", "cms_left", "cms_right", "cms_depress_ecm" } },
            { "trim", new[] { "trimnoseup", "trim_nosedown", "trimleft", "trimright", "trimreset", "trimmerset" } },
            { "gear", new[] { "gear_toggle", "gearin", "gearout" } },
            { "airbrake", new[] { "airbrake_toggle", "airbakeextend", "airbrakeretract" } },
            { "target", new[] { "target_management_system_up", "target_management_system_down", "target_management_system_left", "target_management_system_right", "radar_lock", "space_target_closest_hostile", "space_target_hostile_next" } },
            { "flaps", new[] { "flaps_toggle", "flapsup", "flapsdown", "flapsneutral" } },
            { "look", new[] { "view_snap_forward", "view_snap_back", "view_snap_left", "view_snap_right" } }
        };

        private static void WaiveTooSmall(EvalRow row, SetupPlan plan, List<SetupPlanRelation> mine, Func<SetupPlanRelation, string, bool> does)
        {
            HashSet<string> modifierInputs = new HashSet<string>(plan.Modifiers.Select(m => m.Device + "|" + m.Input), StringComparer.OrdinalIgnoreCase);
            List<SetupInput> grips = plan.Inputs.Where(i => !i.IsAxis && SetupPlanner.IsGrip(i.Control) && !modifierInputs.Contains(i.Device + "|" + i.Input) &&
                                                            plan.Devices.Any(d => d.Device == i.Device && (d.Role == SetupRoles.MainStick || d.Role == SetupRoles.HeliStick || d.Role == SetupRoles.Throttle ||
                                                                                                           d.Role == SetupRoles.Collective || d.Role == SetupRoles.LeftStick))).ToList();
            List<string> layers = new List<string> { string.Empty }.Concat(plan.Modifiers.Where(m => !m.Switch).Select(m => m.Name)).ToList();
            foreach (EvalGate gate in row.Gates.Where(g => g.Status == "fail" && PriorityFunctions.ContainsKey(g.Gate)).ToList())
            {
                int rank = Array.IndexOf(GripPriority, gate.Gate);
                HashSet<string> before = new HashSet<string>(GripPriority.Take(rank).SelectMany(g => PriorityFunctions[g]), StringComparer.OrdinalIgnoreCase);
                // The gun, release, flares and the brake button never sit behind a modifier: only the plain layer counts.
                bool plainOnly = gate.Gate == "fire" || gate.Gate == "wheel_brakes" || gate.Gate == "release" || gate.Gate == "countermeasures";
                bool full = grips.Count > 0;
                // (a gate that may use a shift layer is waived only once the shift layers JoyPro may add are used up: no
                // look-around while a layer is free is the planner's fault, not the device's)
                if (!plainOnly && layers.Count - 1 < new SetupOptions().ForcedModifierLimit)
                    full = false;
                foreach (SetupInput input in grips)
                {
                    foreach (string layer in plainOnly ? new List<string> { string.Empty } : layers)
                    {
                        List<SetupPlanRelation> on = mine.Where(r => r.Device == input.Device && string.Equals(r.Input, input.Input, StringComparison.OrdinalIgnoreCase) && r.Modifier == layer).ToList();
                        if (on.Count == 0 || !on.Any(r => before.Any(f => does(r, f))))
                            full = false;
                    }
                }
                if (!full)
                    continue;
                gate.Status = "n/a";
                gate.Detail = "device too small: all " + grips.Count.ToString(CultureInfo.InvariantCulture) + " grip buttons" + (plainOnly ? string.Empty : " on every layer") +
                              " hold functions before it in the priority (" + string.Join(", ", GripPriority.Take(rank)) + ") - " + gate.Detail;
            }
        }

        // A device a hand holds in flight (stick, throttle, collective, left stick).
        private static bool HandDevice(SetupPlan plan, string device)
        {
            return plan.Devices.Any(d => d.Device == device && (d.Role == SetupRoles.MainStick || d.Role == SetupRoles.HeliStick || d.Role == SetupRoles.Throttle ||
                                                                d.Role == SetupRoles.Collective || d.Role == SetupRoles.LeftStick));
        }

        // A momentary button (no hat) of the throttle device off its grip: the TCA quadrant's lever-end buttons.
        private static bool ThrottleHandButton(SetupPlan plan, SetupInput input)
        {
            return input != null && !input.IsAxis && !SetupPlanner.IsGrip(input.Control) && !HatControl.IsMatch(input.Control) &&
                   string.Equals(input.Type, "momentary", StringComparison.OrdinalIgnoreCase) && plan.Devices.Any(d => d.Device == input.Device && d.Role == SetupRoles.Throttle);
        }

        // Every unshifted push button of the hands (grip buttons, no hat, and the throttle hand's momentary buttons) holds the
        // gun, the release or the flares of this aircraft, or carries a printed label of another function.
        private static bool HandButtonsHeld(SetupRequest request, SetupPlan plan, List<SetupPlanRelation> mine)
        {
            HashSet<string> modifierInputs = new HashSet<string>(plan.Modifiers.Select(m => m.Device + "|" + m.Input), StringComparer.OrdinalIgnoreCase);
            List<SetupInput> buttons = plan.Inputs.Where(i => !i.IsAxis && !HatControl.IsMatch(i.Control) && !modifierInputs.Contains(i.Device + "|" + i.Input) &&
                                                              ((SetupPlanner.IsGrip(i.Control) && HandDevice(plan, i.Device)) || ThrottleHandButton(plan, i))).ToList();
            int rank = SetupPlanner.EssentialRank("cms_");
            return buttons.All(i => (SetupPlanner.FunctionCockpitLabel(i.Label) && LabelVeto(request, "cms_fwd_start_program_or_just_flare", i.Label)) ||
                                    mine.Any(r => r.Modifier.Length == 0 && r.Device == i.Device && r.Input == i.Input && SetupPlanner.EssentialRank(r.Function) <= rank));
        }

        // Every unshifted grip push button (no hat) of the hands' devices holds the gun, the release or the flares of this aircraft.
        private static bool GripButtonsHeldByWeapons(SetupPlan plan, List<SetupPlanRelation> mine)
        {
            HashSet<string> modifierInputs = new HashSet<string>(plan.Modifiers.Select(m => m.Device + "|" + m.Input), StringComparer.OrdinalIgnoreCase);
            List<SetupInput> grips = plan.Inputs.Where(i => !i.IsAxis && SetupPlanner.IsGrip(i.Control) && !HatControl.IsMatch(i.Control) && HandDevice(plan, i.Device) &&
                                                            !modifierInputs.Contains(i.Device + "|" + i.Input)).ToList();
            int rank = SetupPlanner.EssentialRank("cms_");
            return grips.Count > 0 && grips.All(i => mine.Any(r => r.Modifier.Length == 0 && r.Device == i.Device && r.Input == i.Input && SetupPlanner.EssentialRank(r.Function) <= rank));
        }

        // A spring-centred axis of the hands' devices that holds nothing of this aircraft / game.
        private static bool FreeCentringAxis(SetupPlan plan, string plane)
        {
            HashSet<string> used = new HashSet<string>(plan.Placed.Where(r => r.Aircraft.Contains(plane, StringComparer.OrdinalIgnoreCase) || r.Nodes.Any(n => string.Equals(n.Aircraft, plane, StringComparison.OrdinalIgnoreCase) || string.Equals(n.Game, plane, StringComparison.OrdinalIgnoreCase)))
                .Select(r => r.Device + "|" + r.Input), StringComparer.OrdinalIgnoreCase);
            return plan.Inputs.Any(i => i.IsAxis && SetupPlanner.CentringAxis(i) && HandDevice(plan, i.Device) && !used.Contains(i.Device + "|" + i.Input));
        }

        // A grip hat of the hands' devices with nothing of this aircraft / game on any of its directions (unshifted), or
        // empty: where a game whose camera JoyPro does not manage keeps its own look-around.
        private static string FreeGripHat(SetupPlan plan, string plane)
        {
            // (unshifted: the game's own look-around is held without a modifier)
            HashSet<string> used = new HashSet<string>(plan.Placed.Where(r => r.Modifier.Length == 0 && (r.Aircraft.Contains(plane, StringComparer.OrdinalIgnoreCase) || r.Nodes.Any(n => string.Equals(n.Aircraft, plane, StringComparison.OrdinalIgnoreCase))))
                .Select(r => r.Device + "|" + r.Input), StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, SetupInput> hat in plan.Inputs.Where(i => !i.IsAxis && HatControl.IsMatch(i.Control) && SetupPlanner.IsGrip(i.Control) && HandDevice(plan, i.Device))
                         .GroupBy(i => i.Device + "|" + i.Control.Substring(0, i.Control.LastIndexOf('.')), StringComparer.Ordinal)
                         .OrderBy(h => plan.Devices.Any(d => d.Device == h.First().Device && d.Role == SetupRoles.MainStick) ? 0 : 1).ThenBy(h => h.Key, StringComparer.Ordinal))
            {
                if (hat.Count() >= 4 && hat.All(i => !used.Contains(i.Device + "|" + i.Input)))
                    return DeviceShort(plan, hat.First().Device) + " " + hat.Key.Substring(hat.Key.IndexOf('|') + 1);
            }
            return string.Empty;
        }

        // A printed label naming another function (PTT, TRIM, ENG OPER ...): the control is that function's.
        internal static bool LabelVeto(SetupRequest request, string function, string label)
        {
            // (a catalog's description - "Trigger (Mode 1 Dogfight = 14 ...)" - is prose, not a printed label)
            if (string.IsNullOrWhiteSpace(label) || label.Trim().Length < 3 || !SetupPhases.IsFunctionLabel(label) || label.IndexOf('=') >= 0 || label.Length > 48)
                return false;
            SetupFunction own;
            if (request.Functions.TryGetValue(function ?? string.Empty, out own) && SetupPhases.LabelNames(own, label))
                return false;
            return request.Functions.Values.Any(f => f.Labels.Count > 0 && SetupPhases.LabelNames(f, label));
        }

        private static readonly Dictionary<string, bool?> commandKinds = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);

        // Whether a game command is an axis (null: unknown). IL-2's plane-control axes take a button too (rpc_*).
        private static bool? CommandKind(string game, string action)
        {
            string key = (game ?? string.Empty) + "|" + (action ?? string.Empty);
            bool? kind;
            lock (commandKinds)
            {
                if (commandKinds.TryGetValue(key, out kind))
                    return kind;
            }
            kind = EngineAutoGroup.IsDcs(game) ? (bool?)((action ?? string.Empty).StartsWith("a", StringComparison.OrdinalIgnoreCase)) : EngineAutoGroup.GameCommandAxis(game, action);
            lock (commandKinds)
                commandKinds[key] = kind;
            return kind;
        }

        // ---- engine start / stop on switch positions, the eval's own judgement (L18, L28) ----

        // A one-way engine start / stop / cut-off command (catalog function ids: left_engine_start, engine_stop,
        // fuel_cutoff, starter_on ...).
        private static readonly Regex EvalEngineOneWay = new Regex(@"(engine.*(start|stop))|((start|stop).*engine)|starter|cut_?off|fuel_?cut|shut_?down", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EvalSwitchPosition = new Regex(@"^(?<switch>.*\.(toggle\d?|switch|selector|tgl|rocker)\.[^.]+)\.(up|down|center|centre|mid|fwd|aft|left|right|pos\.\d+)$", RegexOptions.Compiled);
        private static readonly Regex EvalStopWords = new Regex(@"\b(STOP|OFF|CUT ?-?OFF|CUTOFF|SHUT ?DOWN|MOTOR)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EvalStartWords = new Regex(@"\b(START|STARTER|STRT|IGN|IGNITION|CRANK|ON|RUN)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EvalEngineWords = new Regex(@"\b(ENG|ENGINE|ENGINES|MOTOR|START|STARTER|IGN|IGNITION|FUEL|THROTTLE|THR|MAG|MAGS|MAGNETO|JFS|MASTER|CUT ?OFF|CUTOFF|OPER|CRANK)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Words that only name the control or its position (no function label): "Switch 5 down", "T7", "STEM TGL", "SW1".
        // (and the catalog's descriptions of the control itself: "Latched toggle 2 up", "Momentary switch 3 down")
        private static readonly Regex EvalControlWords = new Regex(@"^(SWITCH|SWITCHES|SW|TOGGLE|TGL|STEM|BUTTON|BTN|ROCKER|LEVER|BASE|PANEL|UP|DOWN|DN|FWD|AFT|LEFT|RIGHT|L|R|CENTER|CENTRE|MID|MIDDLE|POSITION|POS|LATCHED|LATCHING|MOMENTARY|SPRING|RETURN|LOADED|LOCKING|GUARDED|COVERED|HAT|ENCODER|ROTARY|KNOB|WAY|TWO|THREE|TOP|BOTTOM|FRONT|REAR|SIDE|UPPER|LOWER|FIRST|SECOND|THIRD|THUMB|INDEX|PINKY|BOAT|CHINA|COOLIE|SLEW|STICK|GRIP|SMALL|BIG|LARGE|MINI|OF|THE|ON THE|[A-Z]?\d+[A-Z]?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static string EvalSwitchOf(string control)
        {
            Match m = EvalSwitchPosition.Match(control ?? string.Empty);
            return m.Success ? m.Groups["switch"].Value : string.Empty;
        }

        private static bool EvalUnlabelled(string label)
        {
            string[] words = Regex.Split((label ?? string.Empty).Trim(), @"[^A-Za-z0-9]+").Where(w => w.Length > 0).ToArray();
            return words.Length == 0 || words.All(w => EvalControlWords.IsMatch(w));
        }

        // Why a one-way engine start / stop on this switch position (or latched input) is destructive, or empty. The owner's
        // rule: an engine stop only where the printed label names the stop (STOP / OFF / CUT-OFF / SHUTDOWN / MOTOR) and not
        // a start; a start never on OFF / STOP, never on a position labelled for another system or for the other engine;
        // never the centre of a 3-way switch; never a switch whose other positions do something else.
        internal static string EvalEngineSwitch(string functionId, SetupInput input, IEnumerable<SetupInput> inputs, IEnumerable<string> others, bool twin = true)
        {
            string id = functionId ?? string.Empty;
            if (input == null || input.IsAxis || !EvalEngineOneWay.IsMatch(id))
                return string.Empty;
            string control = input.Control ?? string.Empty;
            string label = (input.Label ?? string.Empty).Trim();
            bool detent = control.IndexOf("detent", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("detent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          string.Equals(input.Type, "detent", StringComparison.OrdinalIgnoreCase);
            string sw = EvalSwitchOf(control);
            bool latched = string.Equals(input.Type, "latched", StringComparison.OrdinalIgnoreCase) || string.Equals(input.Type, "selector", StringComparison.OrdinalIgnoreCase);
            if (detent || (sw.Length == 0 && !latched))
                return string.Empty;
            bool stop = Regex.IsMatch(id, @"stop|cut_?off|fuel_?cut|shut_?down", RegexOptions.IgnoreCase);
            bool unlabelled = EvalUnlabelled(label);
            bool stopWord = EvalStopWords.IsMatch(label), startWord = EvalStartWords.IsMatch(label), engineWord = EvalEngineWords.IsMatch(label);
            if (Regex.IsMatch(control, @"\.(center|centre|mid)$", RegexOptions.IgnoreCase) || (!engineWord && Regex.IsMatch(label, @"\b(center|centre|mid|middle)\b", RegexOptions.IgnoreCase)))
                return "an engine start / stop on the centre of a 3-way switch";
            if (stop && unlabelled)
                return "an engine stop on an unlabelled switch position";
            if (stop && (!stopWord || startWord))
                return "an engine stop on a position not labelled as the stop (" + label + ")";
            if (!stop && stopWord && !startWord)
                return "an engine start on an OFF / STOP position (" + label + ")";
            if (!unlabelled && !engineWord)
                return "an engine start / stop on a position labelled for another system (" + label + ")";
            Match side = Regex.Match(id, @"^(left|right)_", RegexOptions.IgnoreCase);
            if (twin && side.Success && !unlabelled)
            {
                bool labelLeft = Regex.IsMatch(label, @"\b(L|LEFT|LH|1)\b", RegexOptions.IgnoreCase), labelRight = Regex.IsMatch(label, @"\b(R|RIGHT|RH|2)\b", RegexOptions.IgnoreCase);
                if ((side.Groups[1].Value.Equals("left", StringComparison.OrdinalIgnoreCase) && labelRight && !labelLeft) ||
                    (side.Groups[1].Value.Equals("right", StringComparison.OrdinalIgnoreCase) && labelLeft && !labelRight))
                    return "an engine start / stop on the other engine's position (" + label + ")";
            }
            if (sw.Length > 0 && !engineWord && inputs != null && inputs.Any(i => i != null && i.Device == input.Device && EvalSwitchOf(i.Control) == sw &&
                                                                                   Regex.IsMatch(i.Control ?? string.Empty, @"\.(center|centre|mid)$", RegexOptions.IgnoreCase)))
                return "an engine start on a 3-way switch not labelled for the engine";
            if (others != null && others.Any(f => !string.IsNullOrEmpty(f) && !EvalEngineOneWay.IsMatch(f) && f.IndexOf("engine", StringComparison.OrdinalIgnoreCase) < 0))
                return "an engine start / stop on a switch whose other positions do something else";
            return string.Empty;
        }

        private static void Destructive(EvalRow row, SetupRequest request, SetupPlan plan, List<SetupPlanRelation> mine, Func<SetupPlanRelation, string> where, HashSet<string> available)
        {
            // A single-engine aircraft's engine start / stop is catalogued as the left engine's: any engine switch is its.
            bool singleEngine = !available.Any(f => f.StartsWith("right_engine_", StringComparison.OrdinalIgnoreCase));
            // (the F-16 files the same commands under the left and the right engine: one engine, any side)
            string planeOfRow = row.Target.Contains(">") ? row.Target.Substring(row.Target.IndexOf('>') + 1) : row.Target.StartsWith("DCS:", StringComparison.Ordinal) ? row.Target.Substring(4) : row.Target;
            HashSet<string> leftActions = new HashSet<string>(request.Commands.Where(c => string.Equals(c.Aircraft, planeOfRow, StringComparison.OrdinalIgnoreCase) && c.Function.StartsWith("left_engine_", StringComparison.OrdinalIgnoreCase)).Select(c => c.Action), StringComparer.OrdinalIgnoreCase);
            HashSet<string> rightActions = new HashSet<string>(request.Commands.Where(c => string.Equals(c.Aircraft, planeOfRow, StringComparison.OrdinalIgnoreCase) && c.Function.StartsWith("right_engine_", StringComparison.OrdinalIgnoreCase)).Select(c => c.Action), StringComparer.OrdinalIgnoreCase);
            bool twinEngines = !singleEngine && !(rightActions.Count > 0 && rightActions.All(leftActions.Contains));
            string plane = row.Target.Contains(">") ? row.Target.Substring(row.Target.IndexOf('>') + 1) : row.Target.StartsWith("DCS:", StringComparison.Ordinal) ? row.Target.Substring(4) : row.Target;
            List<string> found = new List<string>();
            foreach (SetupPlanRelation r in mine)
            {
                string id = r.Function ?? string.Empty;
                if (id.Length == 0)
                    continue;
                string name = r.FunctionName.Length > 0 ? r.FunctionName : id;
                List<SetupCommand> commands = request.Commands.Where(c => string.Equals(c.Function, id, StringComparison.OrdinalIgnoreCase) && r.Nodes.Any(n => n.Game == c.Game && n.Action == c.Action && string.Equals(n.Aircraft, c.Aircraft, StringComparison.OrdinalIgnoreCase))).ToList();
                bool destructive = DestructiveFunction.IsMatch(id) &&
                                   !(id.IndexOf("jett", StringComparison.OrdinalIgnoreCase) >= 0 && commands.Count > 0 && commands.All(c => SelectorText.IsMatch(c.Control) && !ReleaseText.IsMatch(c.Control)));
                SetupInput input = plan.Inputs.FirstOrDefault(i => i.Device == r.Device && string.Equals(i.Input, r.Input, StringComparison.OrdinalIgnoreCase));
                bool zone = input != null && string.Equals(input.Type, "detent", StringComparison.OrdinalIgnoreCase) && !DetentControl.IsMatch(r.Control);
                bool detent = DetentControl.IsMatch(r.Control) || DetentLabel.IsMatch(r.Label) || zone;
                // (an engine master / starter / magneto toggle or a held starter on one position of a latched switch: every
                // second flip shuts the engine down, a missed one leaves switch and engine out of step, a held starter stays
                // engaged as long as the switch is up)
                bool toggleOrHold = r.Nodes.Any(n => SetupPlanner.ToggleOrHold(n.Action, string.Empty)) || commands.Any(c => SetupPlanner.ToggleOrHold(c.Action, c.Control, c.Title));
                // (L18, review 5 B5: a one-way engine start / stop on the centre of a 3-way switch, on a position labelled for
                // another system - BAT center -, on ENG CRANK as a stop or for the other engine, or on a switch whose other
                // positions carry unrelated functions - the gear on STEM SW1 up / down)
                // (L28, review 6: judged here by the owner's rule, independently of the planner's own EngineSwitchMisuse - a
                // blind spot of the planner must not be one of the harness)
                string switchOf = input == null ? string.Empty : EvalSwitchOf(input.Control);
                string engineSwitch = input == null || r.IsAxis ? string.Empty
                    : EvalEngineSwitch(id, input, plan.Inputs, switchOf.Length == 0 ? null
                        : mine.Where(o => o.Device == r.Device && !string.Equals(o.Input, r.Input, StringComparison.OrdinalIgnoreCase) && EvalSwitchOf(o.Control) == switchOf).Select(o => o.Function).ToList(), twinEngines);
                if (input != null && !r.IsAxis && string.Equals(input.Type, "latched", StringComparison.OrdinalIgnoreCase) && SetupPlanner.EngineFunction(id) && toggleOrHold)
                {
                    found.Add(name + " on a latched switch (a toggle or held command): " + where(r));
                }
                else if (engineSwitch.Length > 0)
                {
                    found.Add(name + " on an engine-unfit switch position (" + engineSwitch + "): " + where(r));
                }
                else if (input != null && SetupPlanner.IsSelector(input) && !r.IsAxis && destructive)
                {
                    // One position of a mode selector is always held: turning the wheel would run it.
                    found.Add(name + " on a mode selector position: " + where(r));
                }
                else if (zone && !r.IsAxis)
                {
                    // A zone button fires whenever its axis passes the zone: anything there is a side effect.
                    found.Add(name + " on an axis zone button: " + where(r));
                }
                else if (detent && destructive)
                {
                    // Only a one-way engine stop / cut-off on the lever's own OFF / CUTOFF detent; never a start / stop
                    // toggle, never IDLE or REV IDLE.
                    // (the lever by its printed number or side: THR 1 / THR L = left, THR 2 / THR R = right)
                    // (the scored aircraft's own commands of the relation: other aircraft's do not decide for it)
                    List<SetupNode> own = r.Nodes.Where(n => string.Equals(n.Aircraft, plane, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (own.Count == 0)
                        own = r.Nodes;
                    bool toggle = own.Any(n => request.Commands.Any(c => c.Game == n.Game && c.Action == n.Action && string.Equals(c.Aircraft, n.Aircraft, StringComparison.OrdinalIgnoreCase) &&
                                                                            (Regex.IsMatch(c.Function, "start", RegexOptions.IgnoreCase) || Regex.IsMatch(c.Control ?? string.Empty, @"start|toggle|on/off", RegexOptions.IgnoreCase))));
                    // (one command the catalog files under both engines - a single-engine aircraft's stop - is the engine of
                    // any lever)
                    Match side = Regex.Match(id, "^(left|right)_(.+)$", RegexOptions.IgnoreCase);
                    string otherSide = side.Success ? (side.Groups[1].Value.ToLowerInvariant() == "left" ? "right_" : "left_") + side.Groups[2].Value : string.Empty;
                    bool bothSides = otherSide.Length > 0 && own.All(n => request.Commands.Any(c => c.Game == n.Game && c.Action == n.Action && string.Equals(c.Aircraft, n.Aircraft, StringComparison.OrdinalIgnoreCase) &&
                                                                                                    string.Equals(c.Function, otherSide, StringComparison.OrdinalIgnoreCase)));
                    bool twoEngines = available.Any(f => f.StartsWith("left_engine_", StringComparison.OrdinalIgnoreCase)) && available.Any(f => f.StartsWith("right_engine_", StringComparison.OrdinalIgnoreCase));
                    bool cutoffOk = !toggle && SetupPlanner.CutoffDetent((bothSides || !twoEngines) && side.Success ? side.Groups[2].Value : id, r.Control, r.Label);
                    if (!cutoffOk)
                        found.Add(name + " on a lever detent: " + where(r));
                }
                else if (destructive && !r.IsAxis && LabelVeto(request, id, r.Label) &&
                         !(singleEngine && id.StartsWith("left_engine_", StringComparison.OrdinalIgnoreCase) && LabelVeto(request, "right" + id.Substring(4), r.Label) == false) &&
                         // (one relation whose command the catalog files under both engines - a single-engine aircraft's
                         // start - and that is the labelled function too: the switch does what its label says)
                         !mine.Any(o => o.Name == r.Name && o.Device == r.Device && string.Equals(o.Input, r.Input, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(o.Function) && !LabelVeto(request, o.Function, r.Label)))
                {
                    found.Add(name + " on a switch labelled for another function: " + where(r));
                }
                else if (destructive && !Regex.IsMatch(id, @"^gear|_gear", RegexOptions.IgnoreCase) && SetupPlanner.IsGrip(r.Control))
                {
                    // On any grip input - trigger, button, ministick, scroll wheel, hat - on any layer.
                    found.Add(name + " on a grip " + (Regex.IsMatch(r.Control, @"\.(hat|coolie)\.") ? "hat" : Regex.IsMatch(r.Control, @"\.trigger\.") ? "trigger" : "control") +
                              (r.Modifier.Length == 0 ? " (unshifted)" : string.Empty) + ": " + where(r));
                }
            }
            // opposites on one input (device, input and modifier), unless both are one toggle command of the game
            foreach (IGrouping<string, SetupPlanRelation> same in mine.Where(r => !string.IsNullOrEmpty(r.Function)).GroupBy(r => r.Device + "|" + r.Input.ToUpperInvariant() + "|" + r.Modifier))
            {
                List<SetupPlanRelation> list = same.ToList();
                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if (!Opposite(list[i].Function, list[j].Function))
                            continue;
                        HashSet<string> a = new HashSet<string>(list[i].Nodes.Select(n => n.Game + "|" + n.Action), StringComparer.OrdinalIgnoreCase);
                        if (list[j].Nodes.Any(n => a.Contains(n.Game + "|" + n.Action)))
                            continue;
                        found.Add((list[i].FunctionName.Length > 0 ? list[i].FunctionName : list[i].Function) + " and " + (list[j].FunctionName.Length > 0 ? list[j].FunctionName : list[j].Function) + " on one input: " + where(list[i]));
                    }
                }
            }
            row.Destructive.AddRange(found.Distinct());
        }

        // Functions on a lever detent that are not the lever's own (throttle family): an ergonomic rule.
        private static List<string> DetentMisuse(List<SetupPlanRelation> mine, Func<SetupPlanRelation, string> where)
        {
            return mine.Where(r => !string.IsNullOrEmpty(r.Function) && (DetentControl.IsMatch(r.Control) || DetentLabel.IsMatch(r.Label)) && !ThrottleFamily.IsMatch(r.Function))
                .Select(r => (r.FunctionName.Length > 0 ? r.FunctionName : r.Function) + " on " + where(r)).ToList();
        }

        // ---- games on the virtual Xbox pad (ACE COMBAT 8) ----

        // The profile after Auto Group and Fill all, for a game the physical devices reach through the virtual Xbox pad:
        // the pad's own sticks (pitch, roll) from the stick's axes, the gun on the trigger and missiles on a grip, unshifted,
        // and what engine vjoy pad-check finds (the throttle split on both triggers, yaw on the pad's buttons, the flare
        // chord, the menu D-pad).
        private static void PadScore(EvalRow row, EvalTarget target, SetupPlan plan)
        {
            string game = target.Game;
            List<Tuple<string, Bind, SetupInput, Relation>> bound = new List<Tuple<string, Bind, SetupInput, Relation>>();
            foreach (KeyValuePair<string, Relation> pair in InternalDataManagement.AllRelations)
            {
                Relation relation = pair.Value;
                Bind bind;
                if (relation == null || relation.NODES == null || !InternalDataManagement.AllBinds.TryGetValue(pair.Key, out bind) || bind == null || string.IsNullOrWhiteSpace(bind.Joystick))
                    continue;
                string input = ((relation.ISAXIS ? bind.JAxis : bind.JButton) ?? string.Empty).Trim();
                SetupDevice device = plan.Devices.FirstOrDefault(d => d.Device == bind.Joystick) ?? plan.Devices.FirstOrDefault(d => EngineSetupWizard.Product(d.Device) == EngineSetupWizard.Product(bind.Joystick));
                SetupInput setupInput = device == null ? null : plan.Inputs.FirstOrDefault(i => i.Device == device.Device && string.Equals(i.Input, input, StringComparison.OrdinalIgnoreCase));
                if (setupInput == null)
                    continue;
                foreach (RelationItem node in relation.NODES.Where(n => n != null && string.Equals(n.Game, game, StringComparison.OrdinalIgnoreCase)))
                    bound.Add(Tuple.Create(node.ID ?? string.Empty, bind, setupInput, relation));
            }
            Func<Tuple<string, Bind, SetupInput, Relation>, bool> clean = t => t.Item2.AllReformers == null || t.Item2.AllReformers.Count == 0;
            Func<Tuple<string, Bind, SetupInput, Relation>, string> where = t => DeviceShort(plan, t.Item3.Device) + " " + t.Item3.Input + " (" + t.Item3.Control + ")" + (clean(t) ? string.Empty : " + modifier") + " in '" + t.Item4.NAME + "'";
            Action<string, string, Func<Tuple<string, Bind, SetupInput, Relation>, bool>> gate = (name, action, ok) =>
            {
                List<Tuple<string, Bind, SetupInput, Relation>> list = bound.Where(t => string.Equals(t.Item1, action, StringComparison.OrdinalIgnoreCase)).ToList();
                Gate(row, name, list.Any(ok) ? "ok" : "fail", list.Any(ok) ? where(list.First(ok)) : list.Count == 0 ? action + " not bound" : action + " on " + string.Join(", ", list.Select(where)));
            };
            gate("pitch", "Pad_Pitch", t => t.Item3.IsAxis && Regex.IsMatch(t.Item3.Control, @"\.axis\.pitch$"));
            gate("roll", "Pad_Roll", t => t.Item3.IsAxis && Regex.IsMatch(t.Item3.Control, @"\.axis\.roll$"));
            // (review 6 m11: on a stick / yoke without a trigger the gun on a grip button counts, as in the main fire gate)
            bool padTriggers = plan.Inputs.Any(i => !i.IsAxis && GunTriggerControl.IsMatch(i.Control) && plan.Devices.Any(d => d.Device == i.Device && (d.Role == SetupRoles.MainStick || d.Role == SetupRoles.HeliStick)));
            gate("fire", "Pad_FireMachineGun", t => !t.Item3.IsAxis && clean(t) && (padTriggers ? GunTriggerControl.IsMatch(t.Item3.Control) : SetupPlanner.IsGrip(t.Item3.Control)));
            gate("release", "Pad_FireMissile", t => !t.Item3.IsAxis && clean(t) && SetupPlanner.IsGrip(t.Item3.Control));
            gate("pad target", "Pad_SwitchTarget", t => !t.Item3.IsAxis && clean(t) && SetupPlanner.IsGrip(t.Item3.Control));

            string payload;
            EngineVJoy.PadCheckCommand(new List<string> { "--game", game }, false, out payload);
            Dictionary<string, object> root = EngineSerialization.FromJson<Dictionary<string, object>>(payload ?? "{}") ?? new Dictionary<string, object>();
            object games;
            Dictionary<string, object> item = root.TryGetValue("Games", out games) && games is System.Collections.IEnumerable
                ? ((System.Collections.IEnumerable)games).OfType<Dictionary<string, object>>().FirstOrDefault()
                : null;
            if (item == null || !Convert.ToBoolean(item["OnPad"], CultureInfo.InvariantCulture))
            {
                Gate(row, "pad", "fail", game + " is not on the virtual Xbox pad");
            }
            else
            {
                object checks;
                foreach (Dictionary<string, object> check in (item.TryGetValue("Checks", out checks) && checks is System.Collections.IEnumerable ? ((System.Collections.IEnumerable)checks).OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>()))
                {
                    if (!Convert.ToBoolean(check["Applies"], CultureInfo.InvariantCulture))
                        continue;
                    string name = Convert.ToString(check["Name"], CultureInfo.InvariantCulture);
                    // Look around is no flight control (the gates are the in-flight essentials): pad-check reports it.
                    if (name == "look")
                        continue;
                    string gateName = name == "throttle-split" ? "throttle" : name == "yaw" ? "yaw" : name.StartsWith("chord:flares", StringComparison.Ordinal) ? "countermeasures" :
                                      name == "gun" ? "fire" : name == "missile" ? "release" : name == "pitch" || name == "roll" ? name : "pad " + name;
                    bool ok = Convert.ToBoolean(check["Ok"], CultureInfo.InvariantCulture);
                    string detail = Convert.ToString(check["Detail"], CultureInfo.InvariantCulture);
                    // pad-check's must-haves and the eval's own pad gates name the same thing: one gate, failed when either fails.
                    EvalGate same = row.Gates.FirstOrDefault(g => g.Gate == gateName);
                    if (same != null)
                    {
                        if (!ok && same.Status == "ok")
                        {
                            same.Status = "fail";
                            same.Detail = detail;
                        }
                        continue;
                    }
                    Gate(row, gateName, ok ? "ok" : "fail", detail);
                }
            }
            row.Required = row.Gates.Count;
            row.Done = row.Gates.Count(g => g.Status == "ok");
            row.Coverage = row.Required == 0 ? 0 : Math.Round(100.0 * row.Done / row.Required, 1);
            row.Ergonomics = 100;
            row.Score = Math.Round(0.75 * row.Coverage + 25, 1);
            row.OpsDone.AddRange(row.Gates.Where(g => g.Status == "ok").Select(g => g.Gate));
            row.Bindings.AddRange(bound.Select(t => t.Item1 + " = " + where(t)));
            PadEssentials(row);
        }

        // ---- unknown devices: scored against the real device ----

        // A plan whose controls and labels are the real device's (the catalog entry of a device reported with an unknown
        // USB id and name), so the gates see what the generic layout really bound: the thumb wheel, a base button.
        private static SetupPlan TruthPlan(SetupPlan plan, Dictionary<string, Dictionary<string, KbControl>> truth)
        {
            if (truth == null || truth.Count == 0)
                return plan;
            Func<string, string, KbControl> real = (device, input) =>
            {
                Dictionary<string, KbControl> map;
                KbControl control;
                return truth.TryGetValue(device ?? string.Empty, out map) && map.TryGetValue((input ?? string.Empty).ToUpperInvariant(), out control) ? control : null;
            };
            SetupPlan copy = new SetupPlan { Devices = plan.Devices, Modifiers = plan.Modifiers, NotPlaced = plan.NotPlaced, Quality = plan.Quality, Notes = plan.Notes, Warnings = plan.Warnings };
            foreach (SetupInput input in plan.Inputs)
            {
                KbControl control = real(input.Device, input.Input);
                copy.Inputs.Add(control == null ? input : new SetupInput { Device = input.Device, Input = input.Input, Control = control.Control, Label = control.Label ?? string.Empty, Type = input.Type, IsAxis = input.IsAxis });
            }
            foreach (SetupPlanRelation r in plan.Relations)
            {
                KbControl control = real(r.Device, r.Input);
                if (control == null)
                {
                    copy.Relations.Add(r);
                    continue;
                }
                copy.Relations.Add(new SetupPlanRelation
                {
                    Name = r.Name, Function = r.Function, FunctionName = r.FunctionName, Category = r.Category, IsAxis = r.IsAxis, Existing = r.Existing, Kept = r.Kept, Removed = r.Removed,
                    Nodes = r.Nodes, Aircraft = r.Aircraft, Overlap = r.Overlap, Importance = r.Importance, Device = r.Device, Input = r.Input, Control = control.Control,
                    Label = control.Label ?? string.Empty, Modifier = r.Modifier, Score = r.Score, RealMatch = r.RealMatch, Phase = r.Phase, Reason = r.Reason, Share = r.Share
                });
            }
            return copy;
        }

        // ---- the report ----

        internal static string GateCell(EvalRow row, string gate)
        {
            EvalGate g = row.Gates.FirstOrDefault(x => x.Gate == gate);
            return g == null ? "-" : g.Status == "ok" ? "ok" : g.Status == "n/a" ? "n/a" : "**FAIL**";
        }

        // The gates a pilot's survival or flight depends on: the quick path (one plan for all games, Auto Group, Fill all)
        // must never lose one the step before had (review 6 B9 / B6, L27 / L34).
        internal static readonly string[] EssentialGates = { "pitch", "roll", "yaw", "throttle", "fire", "release", "countermeasures", "wheel_brakes" };

        // Every essential gate a later stage lost against an earlier one of the same setup and target: the setup alone ->
        // the all-games plan ("planning all games at once"), the all-games plan -> the profile after Auto Group and Fill all.
        internal static List<string> StageLosses(IEnumerable<EvalRow> before, IEnumerable<EvalRow> after, string what, IEnumerable<string> gates)
        {
            List<string> lost = new List<string>();
            HashSet<string> wanted = new HashSet<string>(gates ?? EssentialGates, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, EvalRow> earlier = new Dictionary<string, EvalRow>(StringComparer.Ordinal);
            foreach (EvalRow row in before.Where(r => r.Error.Length == 0))
                earlier[row.Setup + "|" + row.Target] = row;
            foreach (EvalRow row in after.Where(r => r.Error.Length == 0))
            {
                EvalRow was;
                if (!earlier.TryGetValue(row.Setup + "|" + row.Target, out was))
                    continue;
                foreach (EvalGate gate in row.Gates.Where(g => g.Status == "fail" && wanted.Contains(g.Gate)))
                {
                    if (was.Gates.Any(g => g.Gate == gate.Gate && g.Status == "ok"))
                        lost.Add(row.Setup + " x " + row.Target + ": " + gate.Gate + " lost " + what + " (" + gate.Detail + ")");
                }
            }
            return lost;
        }

        internal static double HardPassRate(IEnumerable<EvalRow> rows)
        {
            List<EvalRow> list = rows.ToList();
            double weight = list.Sum(r => r.Weight);
            return weight <= 0 ? 0 : 100.0 * list.Where(r => r.HardPass).Sum(r => r.Weight) / weight;
        }

        internal static void GatesMarkdown(System.Text.StringBuilder sb, EvalResult result)
        {
            sb.AppendLine("## Hard gates (fly and fight)");
            sb.AppendLine();
            sb.AppendLine("A row passes when every gate that applies is met and no destructive binding is found (see the top of `engine/EngineSetupEvalGates.cs` and docs/auto-setup-eval.md#hard-gates). " +
                          "A binding behind a modifier or a mode-switch layer does not count for fire, release, countermeasures and the brake button; an axis command on a button does not count; one toe brake is no brake; " +
                          "the camera / first detent is not the gun; a throttle on a knob, thumb wheel or thumb stick does not count while a lever exists. `n/a` = the hardware or the game cannot do it (named in the reason).");
            sb.AppendLine();
            foreach (KeyValuePair<string, List<EvalRow>> stage in new[] { new KeyValuePair<string, List<EvalRow>>("Setup alone", result.Rows), new KeyValuePair<string, List<EvalRow>>("Typical DCS hangar (20 modules, one plan, every module scored)", result.TypicalRows),
                                                                          new KeyValuePair<string, List<EvalRow>>("ACE COMBAT 8 alone (setup + Auto Group + the pad's auto setup)", result.PadRows),
                                                                          new KeyValuePair<string, List<EvalRow>>("All games at once, the plan before Auto Group", result.PipelinePlanRows),
                                                                          new KeyValuePair<string, List<EvalRow>>("After Auto Group + Fill all", result.PipelineRows) })
            {
                if (stage.Value.Count == 0)
                    continue;
                sb.AppendLine("### " + stage.Key + ": pass rate " + Pct(HardPassRate(stage.Value)) + " % (" + stage.Value.Count(r => r.HardPass).ToString(CultureInfo.InvariantCulture) + " of " + stage.Value.Count.ToString(CultureInfo.InvariantCulture) + " rows)");
                sb.AppendLine();
                sb.AppendLine("| Gate | Applies | Fails | Waived (device too small) | Most common reason |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (string gate in GateIds)
                {
                    List<EvalGate> all = stage.Value.SelectMany(r => r.Gates).Where(g => g.Gate == gate && g.Status != "n/a").ToList();
                    List<EvalGate> fails = all.Where(g => g.Status == "fail").ToList();
                    string reason = fails.GroupBy(g => Regex.Replace(g.Detail, @"\s*\(.*$|:.*$", string.Empty)).OrderByDescending(g => g.Count()).Select(g => g.Key + " (" + g.Count().ToString(CultureInfo.InvariantCulture) + "x)").FirstOrDefault() ?? string.Empty;
                    int waived = stage.Value.SelectMany(r => r.Gates).Count(g => g.Gate == gate && g.Status == "n/a" && g.Detail.StartsWith("device too small", StringComparison.Ordinal));
                    sb.AppendLine("| " + gate + " | " + all.Count.ToString(CultureInfo.InvariantCulture) + " | " + fails.Count.ToString(CultureInfo.InvariantCulture) + " | " + waived.ToString(CultureInfo.InvariantCulture) + " | " + Cell(reason) + " |");
                }
                sb.AppendLine("| destructive bindings | " + stage.Value.Count.ToString(CultureInfo.InvariantCulture) + " | " + stage.Value.Count(r => r.Destructive.Count > 0).ToString(CultureInfo.InvariantCulture) + " rows (" +
                              stage.Value.Sum(r => r.Destructive.Count).ToString(CultureInfo.InvariantCulture) + " bindings) | - | " +
                              Cell(stage.Value.SelectMany(r => r.Destructive).GroupBy(d => Regex.Replace(d, @":.*$", string.Empty)).OrderByDescending(g => g.Count()).Select(g => g.Key + " (" + g.Count().ToString(CultureInfo.InvariantCulture) + "x)").FirstOrDefault() ?? string.Empty) + " |");
                sb.AppendLine();
                List<string> kinds = stage.Value.SelectMany(r => r.Destructive).Select(d => Regex.Match(d, @" on (an axis zone button|a lever detent|a switch labelled for another function|a grip \w+( \(unshifted\))?|one input|a latched switch|a mode selector position|an engine-unfit switch position)").Groups[1].Value)
                    .Select(k => k.Length == 0 ? "other" : k).ToList();
                if (kinds.Count > 0)
                {
                    sb.AppendLine("Destructive bindings by kind: " + string.Join(", ", kinds.GroupBy(k => k).OrderByDescending(g => g.Count()).Select(g => g.Key + " " + g.Count().ToString(CultureInfo.InvariantCulture))));
                    sb.AppendLine();
                }
                sb.AppendLine("| Setup | Pass rate % | Rows passing | Gates failing (count) |");
                sb.AppendLine("|---|---|---|---|");
                foreach (EvalSetup setup in result.Setups)
                {
                    List<EvalRow> rows = stage.Value.Where(r => r.Setup == setup.Id).ToList();
                    if (rows.Count == 0)
                        continue;
                    string failing = string.Join(", ", rows.SelectMany(r => r.Gates.Where(g => g.Status == "fail").Select(g => g.Gate)).Concat(rows.Where(r => r.Destructive.Count > 0).Select(r => "destructive"))
                        .GroupBy(g => g).OrderByDescending(g => g.Count()).Select(g => g.Key + " " + g.Count().ToString(CultureInfo.InvariantCulture)));
                    sb.AppendLine("| " + setup.Id + " | " + Pct(HardPassRate(rows)) + " | " + rows.Count(r => r.HardPass).ToString(CultureInfo.InvariantCulture) + "/" + rows.Count.ToString(CultureInfo.InvariantCulture) + " | " + Cell(failing) + " |");
                }
                sb.AppendLine();
            }
            sb.AppendLine("### Every hard-gate failure");
            sb.AppendLine();
            foreach (EvalRow row in result.Rows.Concat(result.TypicalRows).Concat(result.PadRows).Concat(result.PipelineRows).Where(r => !r.HardPass))
            {
                List<string> items = row.Gates.Where(g => g.Status == "fail").Select(g => g.Gate + ": " + g.Detail).Concat(row.Destructive.Select(d => "destructive: " + d)).ToList();
                if (row.Error.Length > 0)
                    items.Insert(0, "error: " + row.Error);
                sb.AppendLine("- " + row.Setup + " x " + row.Target + (row.Stage == "pipeline" ? " (after Auto Group + Fill all)" : row.Stage == "typical" ? " (typical DCS hangar)" : string.Empty) + ": " + Cell(string.Join("; ", items)));
            }
            sb.AppendLine();
        }
    }
}
