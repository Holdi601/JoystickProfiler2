using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.DeviceReference;
using JoyPro.Engine.Games;
using JoyPro.Setup;

namespace JoyPro.Engine
{
    // engine setup eval (docs/auto-setup-eval.md): the Setup wizard's planner on many real device combinations against
    // every game the wizard supports and the main DCS aircraft families, scored automatically.
    // - Setups: 20 popular ones (fixed list below, catalog devices of DB\Devices) and N random but plausible ones (seeded).
    // - Each setup is a fake device file (JOYPRO_FAKE_DEVICES, the way Windows would report the devices) or, for catalog
    //   devices without a USB id, a catalog device added like the wizard's "add a device"; the wizard's own defaults
    //   (no modifier wanted, as few as possible, realism 50 %, phase layers automatic) and the target aircraft as priority.
    // - Coverage: can every major operation of the game be done (flight axes, throttle, weapons, gear, flaps, brakes, trim,
    //   view, comms, countermeasures, sensor slew, master modes; space: strafe, boost, shields ...)? An operation counts only
    //   when the game has a command for it; it is done when one of its alternatives is bound completely.
    // - Ergonomics: one device per hand, trim on a hat, weapons on the stick, countermeasures and comms on the throttle,
    //   axes on fitting axes, time-critical functions on the grips without a modifier, axis functions only on axes.
    // Read-only: no profile, no game folder; the report goes to --out-dir.
    internal static partial class EngineSetupEval
    {
        internal sealed class EvalDevice
        {
            public string Catalog = string.Empty;   // catalog id (a grip: its base is the USB device)
            public string Role = string.Empty;      // a role the user would choose (empty = JoyPro's proposal)
            // Reported as another device would (a USB id and name JoyPro does not know): the generic path.
            public string ReportVidPid = string.Empty;
            public string ReportName = string.Empty;
            public string ReportAxes = string.Empty;   // axes as the device reports them, when not the catalog's (X,Y,Z)
        }

        internal sealed class EvalSetup
        {
            public string Id = string.Empty;
            public string Name = string.Empty;
            public string Kind = "popular";
            public List<EvalDevice> Devices = new List<EvalDevice>();
            // Devices reported with a USB id and name JoyPro does not know: device -> input -> the real control (the
            // catalog entry), so the gates judge what the generic layout really bound.
            public Dictionary<string, Dictionary<string, KbControl>> Truth = new Dictionary<string, Dictionary<string, KbControl>>(StringComparer.Ordinal);
            // Random and regression setups: the seed that drew it, its shape, what it failed when it was saved, what of
            // that passes since, the day it was saved (EngineSetupEvalRandom).
            public int Seed;
            public string Shape = string.Empty;
            public List<string> Signatures = new List<string>();
            public List<string> Fixed = new List<string>();
            public string Added = string.Empty;
        }

        internal sealed class EvalTarget
        {
            public string Id = string.Empty;        // DCS:F-16C_50, StarCitizen
            public string Title = string.Empty;
            public string Game = string.Empty;
            public string Aircraft = string.Empty;  // the aircraft (or game) that is scored
            public List<string> Only = new List<string>();   // DCS: the modules of this family
            public bool Pad;                                  // a game on the virtual Xbox pad (scored after Auto Group with engine vjoy pad-check)
            public bool Stress;                     // every DCS module at once
            // A game with planes and helicopters in one configuration (Arma 3) is scored once per class (review 7 L40): the
            // row's class and the game catalog's vehicle category whose actions count for it (the other one's are left out).
            public string ClassOverride = string.Empty;
            public string Vehicle = string.Empty;
            public double Share = 1.0;              // the row weight's share (the game's two rows count as one game)
        }

        internal sealed class EvalOp
        {
            public string Id = string.Empty;
            public string Title = string.Empty;
            public string Classes = "fixed,rotary,space";
            public List<string[]> Alternatives = new List<string[]>();
            public bool Counted = true;             // false: reported, not part of the coverage (not on the list of major operations)
        }

        internal sealed class EvalGap
        {
            public string Op = string.Empty;
            public string Title = string.Empty;
            public bool Partial;
            public bool Counted = true;
            public string Why = string.Empty;
        }

        internal sealed class EvalRow
        {
            public string Setup = string.Empty;
            public string Zoom = string.Empty;          // zoom bound (a look essential, owner 2026-11-16): ok, missing, n/a
            public string SetupKind = string.Empty;
            public string Target = string.Empty;
            public string Class = string.Empty;
            public int Required;
            public double Done;
            public double Coverage;
            public int RulesApplicable;
            public int RulesPassed;
            public double Ergonomics;
            public double Score;
            public double Quality;
            public int Placed;
            public int Total;
            public int Modifiers;
            public int Forced;
            public int PhaseLayers;
            public long Millis;
            public List<EvalGap> Gaps = new List<EvalGap>();
            public List<string> Violations = new List<string>();
            public List<string> OpsDone = new List<string>();
            public List<string> Bindings = new List<string>();
            public string Error = string.Empty;
            // "setup": the setup's plan alone; "pipeline": the profile after setup, Auto Group and Suggest missing (Fill all).
            public string Stage = "setup";
            // Pipeline: commands in a relation that do something else than the relation (for the scored aircraft).
            public List<string> Wrong = new List<string>();
            // Hard gates (EngineSetupEvalGates.cs): the fly-and-fight functions, each "ok", "fail" or "n/a" with its reason.
            public List<EvalGate> Gates = new List<EvalGate>();
            // Destructive or contradictory bindings (engine stop on a lever detent, gear on a switch labelled for another
            // function, engine start and stop on one input ...): zero tolerance.
            public List<string> Destructive = new List<string>();
            public double Weight = 1;     // near-duplicate games (MSFS 2020 / 2024 ...) count half each in the means
            public List<string> Shifts = new List<string>();
            // In-flight essentials (EngineSetupEvalEssentials.cs): the game class and per essential ok / fail / n/a.
            public string EssentialClass = string.Empty;
            public List<EvalEssential> Essentials = new List<EvalEssential>();   // where the plan's shift buttons and mode switch positions are
            public bool HardPass { get { return Error.Length == 0 && Destructive.Count == 0 && Gates.All(g => g.Status != "fail"); } }
        }

        // ---- the setups ----

        private static EvalSetup S(string id, string name, params string[] devices)
        {
            EvalSetup setup = new EvalSetup { Id = id, Name = name };
            foreach (string device in devices)
            {
                // "catalog[=role][@VID:PID@HID name]"
                string[] report = device.Split('@');
                string[] parts = report[0].Split('=');
                setup.Devices.Add(new EvalDevice
                {
                    Catalog = parts[0], Role = parts.Length > 1 ? parts[1] : string.Empty,
                    ReportVidPid = report.Length > 1 ? report[1] : string.Empty, ReportName = report.Length > 2 ? report[2] : string.Empty,
                    ReportAxes = report.Length > 3 ? report[3] : string.Empty
                });
            }
            return setup;
        }

        // The named novice case: someone new to sims with IL-2 Great Battles only and a WinWing URSA MINOR HOTAS (stick
        // and the matching throttle), who takes the quick path at the first start. Fighter, Airline and Space sticks, and
        // the same Fighter set reported with a USB id and name JoyPro does not know (the generic path).
        internal static readonly string[] PersonaGames = { "IL2Game" };

        internal static List<EvalSetup> PersonaSetups()
        {
            List<EvalSetup> list = new List<EvalSetup>
            {
                S("ursa-fighter", "WinWing URSA MINOR Fighter stick R + URSA MINOR Combat Throttle", "winwing-ursa-minor-fighter-joystick-r", "winwing-ursa-minor-combat-throttle"),
                S("ursa-airline", "WinWing URSA MINOR Airline stick R + URSA MINOR 32 Airline Throttle", "winwing-ursa-minor-airline-joystick-r", "winwing-ursa-minor-airline-throttle"),
                S("ursa-space", "WinWing URSA MINOR Space stick R + URSA MINOR Combat Throttle", "winwing-ursa-minor-space-joystick-r", "winwing-ursa-minor-combat-throttle"),
                S("ursa-unknown", "URSA MINOR Fighter stick and Combat Throttle with a new USB product id of WinWing's and an unknown name (WinWing's devices of the same shape lend their layout)",
                  "winwing-ursa-minor-fighter-joystick-r@4098:BEEF@URSA MINOR NEXTGEN FLIGHT CONTROLLER R", "winwing-ursa-minor-combat-throttle@4098:BEF0@URSA MINOR NEXTGEN THROTTLE"),
                // A maker JoyPro does not know at all (pid.codes test VID): nothing to inherit, only the generic layout.
                S("ursa-unknown-maker", "The same URSA MINOR hardware reported with a USB vendor JoyPro does not know (1209:BEEF / 1209:BEF0, generic set up)",
                  "winwing-ursa-minor-fighter-joystick-r@1209:BEEF@USB GAME STICK@X,Y,Z,RX,RY", "winwing-ursa-minor-combat-throttle@1209:BEF0@USB GAME THROTTLE@RX,RY,Z,RZ")
            };
            foreach (EvalSetup setup in list)
                setup.Kind = "persona";
            return list;
        }

        // The 20 most common setups of sim pilots (HOTAS, sticks, yokes, space, helicopters, MFDs, button boxes).
        internal static List<EvalSetup> PopularSetups()
        {
            return new List<EvalSetup>
            {
                S("warthog-tfrp", "Thrustmaster HOTAS Warthog + T.Flight Rudder Pedals", "thrustmaster-warthog-stick", "thrustmaster-hotas-warthog-throttle", "thrustmaster-tfrp"),
                S("t16000m-fcs", "Thrustmaster T.16000M FCS HOTAS (stick, TWCS throttle, TFRP pedals)", "thrustmaster-t16000m", "thrustmaster-twcs-throttle", "thrustmaster-tfrp"),
                S("vkb-evo-stecs", "VKB Gladiator NXT EVO + STECS Standard + T-Rudder Mk.IV", "vkb-gnx-evo-scg-premium-right", "vkb-stecs-mts-standard", "vkb-t-rudder-mk4"),
                S("virpil-alpha-cm3", "Virpil Alpha on WarBRD + MongoosT-50CM3 throttle + ACE Flight pedals", "virpil-alpha-r", "virpil-mongoost-50cm3-throttle", "virpil-ace-flight-pedals"),
                S("winwing-orion2-f16", "WinWing Orion 2 F-16EX (ViperAce EX stick, ViperAce throttle) + Orion pedals", "winwing-viperace-ex-joystick-grip", "winwing-viperace-throttle-grip", "winwing-orion-metal-rudder-pedals"),
                S("winwing-orion2-f18", "WinWing Orion 2 F/A-18 (NavyAce stick and throttle) + take-off panel + UFC", "winwing-navyace-joystick-grip", "winwing-navyace-throttle-grip", "winwing-f18-takeoff-panel", "winwing-ufc1", "winwing-orion-metal-rudder-pedals"),
                S("x56", "Logitech X56 Rhino HOTAS", "logitech-x56-rhino-stick", "logitech-x56-rhino-throttle"),
                S("x52pro", "Logitech X52 Pro (one USB device)", "logitech-x52-pro"),
                S("tca-airbus", "Thrustmaster TCA Sidestick Airbus + TCA Quadrant Airbus", "thrustmaster-tca-sidestick-airbus", "thrustmaster-tca-quadrant-airbus"),
                S("honeycomb-alpha-bravo", "Honeycomb Alpha yoke + Bravo throttle quadrant + Logitech rudder pedals", "honeycomb-alpha-flight-controls", "honeycomb-bravo-throttle-quadrant", "logitech-pro-flight-rudder-pedals"),
                S("gamepad-only", "Xbox controller only", "microsoft-xbox-controller"),
                S("stick-only", "Logitech Extreme 3D Pro only", "logitech-extreme-3d-pro"),
                S("space-dual-vkb", "Two VKB Gladiator NXT (right and left) for space sims", "vkb-gladiator-nxt", "vkb-gladiator-nxt-left"),
                S("space-dual-t16000m", "Two T.16000M (the same stick twice) + TFRP pedals", "thrustmaster-t16000m", "thrustmaster-t16000m", "thrustmaster-tfrp"),
                S("warthog-mfds", "HOTAS Warthog + two Thrustmaster Cougar MFDs + TFRP pedals", "thrustmaster-warthog-stick", "thrustmaster-hotas-warthog-throttle", "thrustmaster-mfd-cougar-left", "thrustmaster-mfd-cougar-right", "thrustmaster-tfrp"),
                S("t16000m-buttonbox", "T.16000M + TWCS throttle + a 32-button box (Leo Bodnar BBI-32)", "thrustmaster-t16000m", "thrustmaster-twcs-throttle", "leobodnar-bbi32"),
                S("heli-collective", "VKB Gladiator NXT + Virpil Hawk-60 collective + MFG Crosswind pedals", "vkb-gladiator-nxt", "virpil-hawk-60-collective-grip", "mfg-crosswind-v2"),
                S("tflight-hotas4", "Thrustmaster T.Flight HOTAS 4 (stick and throttle in one)", "thrustmaster-tflight-hotas-4"),
                S("rhino-ffb-warthog", "VPforce Rhino FFB with TM F-16 grip + Warthog throttle + MFG Crosswind", "vpforce-rhino-tm-f16-grip", "thrustmaster-hotas-warthog-throttle", "mfg-crosswind-v2"),
                S("logitech-yoke", "Logitech Pro Flight yoke + throttle quadrant + rudder pedals", "logitech-pro-flight-yoke", "logitech-pro-flight-throttle-quadrant", "logitech-pro-flight-rudder-pedals")
            };
        }

        // ---- the targets ----

        // Every game the Setup wizard plans (a function table of its own: what setup games marks "Wizard"), and the DCS
        // families. ACE COMBAT 8 is scored on the virtual pad (PadGames). Review 6 (L26 / B7): the second line was planned
        // by the quick path but never evaluated - twelve of them had no flight axes.
        internal static readonly string[] WizardGames =
        {
            "MSFS2024", "MSFS2020", "XPlane12", "XPlane11", "IL2Game", "IL2Korea", "FalconBMS", "WarThunder", "VTOLVR", "VTOLVRNative",
            "NuclearOption", "Wardogs", "StarCitizen", "EliteDangerous", "X4", "Arma3", "Arma2", "Arma2OA", "ArmaReforger",
            "FSX", "FSXSE", "P3D4", "P3D5", "P3D6", "IL21946", "IL2CoD", "IL2CoDBlitz", "TinyCombatArena", "GRebels", "ProjectWingman",
            "AceCombat7", "Battlefield6", "Battlefield2042", "HLLVietnam"
        };

        // Typical stick + throttle setups: every wizard game must reach pitch, roll and throttle (or its collective / thrust)
        // there (FlightAxesFailures: the check behind "every game the quick path plans can fly").
        internal static readonly string[] StickThrottleSetups =
        {
            "warthog-tfrp", "t16000m-fcs", "vkb-evo-stecs", "virpil-alpha-cm3", "winwing-orion2-f16", "x56", "x52pro", "t16000m-buttonbox", "tflight-hotas4", "rhino-ffb-warthog"
        };

        // Setup-alone rows of the typical stick + throttle setups where pitch, roll or throttle is not reached (a gate that
        // fails; n/a only when the hardware has no such axis).
        internal static List<string> FlightAxesFailures(IEnumerable<EvalRow> rows, out int checkedRows)
        {
            List<string> failures = new List<string>();
            checkedRows = 0;
            foreach (EvalRow row in rows.Where(r => r.SetupKind == "popular" && r.Stage != "typical" && r.Error.Length == 0 && StickThrottleSetups.Contains(r.Setup, StringComparer.OrdinalIgnoreCase)))
            {
                checkedRows++;
                foreach (EvalGate gate in row.Gates.Where(g => (g.Gate == "pitch" || g.Gate == "roll" || g.Gate == "throttle") && g.Status == "fail"))
                    failures.Add("wizard flight axes: " + row.Setup + " x " + row.Target + ": " + gate.Gate + " " + gate.Detail);
            }
            return failures;
        }

        // DCS: the family's aircraft are the modules of a user who owns it; the first one is scored.
        internal static readonly string[] PadGames = { "AceCombat8" };

        // (L26, review 6: every game the quick path plans - `setup games` calls a game "Wizard" when it has a function table
        // of its own, DB/Functions/<game>.tsv - is a target too, not only the 19 above: FSX, P3D, IL-2 1946 / Cliffs of
        // Dover, Tiny Combat Arena, G-Rebels, ACE COMBAT 7, Project Wingman, Battlefield ...)
        internal static List<string> TableGames()
        {
            List<string> list = new List<string>();
            try
            {
                string folder = EngineSetupWizard.FunctionsFolder();
                if (!Directory.Exists(folder))
                    return list;
                foreach (string file in Directory.GetFiles(folder, "*.tsv").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string game = Path.GetFileNameWithoutExtension(file);
                    if (game.IndexOf('.') >= 0 || game.IndexOf('_') >= 0 || string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase) ||
                        PadGames.Contains(game, StringComparer.OrdinalIgnoreCase))
                        continue;
                    bool derived;
                    if (EngineGameSupport.FunctionRows(game, out derived) > 0 && !derived)
                        list.Add(game);
                }
            }
            catch (Exception)
            {
            }
            return list;
        }

        internal static readonly string[][] DcsFamilies =
        {
            new[] { "F-16C_50" },
            new[] { "FA-18C_hornet" },
            new[] { "A-10C II", "A-10C" },
            new[] { "F-14", "F-14_RIO" },
            new[] { "F-15E", "F-15E_WSO" },
            new[] { "F-4E-45MC", "F-4E-45MC_WSO" },
            new[] { "AV8BNA" },
            new[] { "M-2000C" },
            new[] { "F-15C", "su-27", "su-33", "J-11A", "mig-29", "MiG-29-Fulcrum", "mig-29c", "mig-29g", "su-25", "su-25T", "a-10a" },
            new[] { "AH-64D_BLK_II_PLT", "AH-64D_BLK_II_CPG", "AH-64D_BLK_II_AI_Menu" },
            new[] { "Ka-50 III", "ka-50" },
            new[] { "UH-1H", "UH-1H_Gunner" },
            new[] { "mi-8mt", "mi-8mt_Gunner" },
            new[] { "P-51D", "P-51D-30-NA", "TF-51D" },
            new[] { "SpitfireLFMkIX", "SpitfireLFMkIXCW" },
            new[] { "Bf-109K-4" }
        };

        // Games whose one configuration flies planes and helicopters, with the catalog categories of their helicopter-only
        // and plane-only actions (DB\<Game>\actions.tsv, fourth column).
        internal static readonly Dictionary<string, string[]> VehicleSplit = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Arma3", new[] { "Helicopter", "Plane" } }
        };

        private static readonly Dictionary<string, Dictionary<string, string>> categoryCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // The actions of the other vehicle class (a plane-only action in the helicopter row and the other way round).
        internal static HashSet<string> OtherVehicleActions(EvalTarget target)
        {
            HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] split;
            if (target == null || target.Vehicle.Length == 0 || !VehicleSplit.TryGetValue(target.Game, out split))
                return excluded;
            Dictionary<string, string> categories;
            lock (categoryCache)
            {
                if (!categoryCache.TryGetValue(target.Game, out categories))
                {
                    categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    IGameIntegration integration;
                    if (GameRegistry.TryResolve(target.Game, out integration))
                    {
                        try
                        {
                            foreach (GameAction action in GameCatalogStore.Load(integration, new List<string>()).Actions.Where(a => !string.IsNullOrWhiteSpace(a.Id)))
                                categories[action.Id] = (action.Category ?? string.Empty).Trim();
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
                        {
                        }
                    }
                    categoryCache[target.Game] = categories;
                }
            }
            string other = split.FirstOrDefault(c => !string.Equals(c, target.Vehicle, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            foreach (KeyValuePair<string, string> pair in categories.Where(p => string.Equals(p.Value, other, StringComparison.OrdinalIgnoreCase)))
                excluded.Add(pair.Key);
            return excluded;
        }

        internal static List<EvalTarget> Targets(List<string> games, List<string> families, bool stress)
        {
            List<EvalTarget> targets = new List<EvalTarget>();
            foreach (string[] family in DcsFamilies)
            {
                if (families != null && !families.Any(f => family.Contains(f, StringComparer.OrdinalIgnoreCase)))
                    continue;
                targets.Add(new EvalTarget { Id = "DCS:" + family[0], Title = "DCS " + family[0], Game = "DCS", Aircraft = family[0], Only = family.ToList() });
            }
            if (stress)
                targets.Add(new EvalTarget { Id = "DCS:all>F-16C_50", Title = "DCS, every module at once (F-16C first)", Game = "DCS", Aircraft = "F-16C_50", Stress = true });
            foreach (string game in WizardGames.Concat(TableGames()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (games != null && !games.Contains(game, StringComparer.OrdinalIgnoreCase))
                    continue;
                string[] split;
                if (VehicleSplit.TryGetValue(game, out split))
                {
                    // (review 7 M11 / L40: Arma 3's planes need the throttle, its helicopters the collective - one row of
                    // class rotary never saw a missing plane throttle)
                    targets.Add(new EvalTarget { Id = game, Title = game + " (helicopters)", Game = game, Aircraft = game, ClassOverride = SetupClasses.Rotary, Vehicle = split[0], Share = 0.5 });
                    targets.Add(new EvalTarget { Id = game + ":plane", Title = game + " (planes)", Game = game, Aircraft = game, ClassOverride = SetupClasses.Fixed, Vehicle = split[1], Share = 0.5 });
                    continue;
                }
                targets.Add(new EvalTarget { Id = game, Title = game, Game = game, Aircraft = game });
            }
            // Games on the virtual Xbox pad: no setup table of their own; Auto Group puts their actions into the profile's
            // relations and the pad's auto setup forwards them (engine vjoy pad-check scores that).
            foreach (string game in PadGames)
            {
                if (games != null && !games.Contains(game, StringComparer.OrdinalIgnoreCase))
                    continue;
                targets.Add(new EvalTarget { Id = game, Title = game + " (virtual Xbox pad)", Game = game, Aircraft = game, Pad = true });
            }
            return targets;
        }

        // Debugging aid: JOYPRO_EVAL_DUMP=<folder> writes each plan's relations (importance, input, score) and what was not
        // placed (with the reason) as one text file per setup and target.
        private static void DumpPlan(string name, SetupPlan plan)
        {
            string folder = Environment.GetEnvironmentVariable("JOYPRO_EVAL_DUMP");
            if (string.IsNullOrWhiteSpace(folder) || plan == null)
                return;
            StringBuilder sb = new StringBuilder();
            foreach (SetupPlanRelation r in plan.Relations.OrderByDescending(r => r.Importance))
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,7:0.000} {1} [{2}] = {3} {4} ({5} \"{6}\") {7} score {8:0.00}", r.Importance, r.Name, r.Function,
                    r.Device, r.Input, r.Control, r.Label, r.Modifier, r.Score));
            sb.AppendLine("-- not placed");
            foreach (SetupNotPlaced n in plan.NotPlaced.OrderByDescending(n => n.Importance))
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,7:0.000} {1} [{2}]: {3}", n.Importance, n.Relation, n.Function, n.Reason));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, Regex.Replace(name, @"[^A-Za-z0-9_.-]+", "_") + ".txt"), sb.ToString(), new UTF8Encoding(false));
        }

        // Debugging aid (JOYPRO_EVAL_DUMP): the profile after Auto Group and Fill all - each bound relation with its input
        // and actions - and what Auto Group left out.
        private static void DumpProfile(string name, EngineAutoGroupResult grouped)
        {
            string folder = Environment.GetEnvironmentVariable("JOYPRO_EVAL_DUMP");
            if (string.IsNullOrWhiteSpace(folder))
                return;
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, Relation> pair in InternalDataManagement.AllRelations.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                Bind bind;
                if (pair.Value == null || !InternalDataManagement.AllBinds.TryGetValue(pair.Key, out bind) || bind == null || string.IsNullOrWhiteSpace(bind.Joystick))
                    continue;
                sb.AppendLine(pair.Key + " = " + bind.Joystick + " " + (pair.Value.ISAXIS ? bind.JAxis : bind.JButton) + (bind.AllReformers != null && bind.AllReformers.Count > 0 ? " + " + string.Join(",", bind.AllReformers) : string.Empty));
                foreach (RelationItem node in pair.Value.NODES ?? new List<RelationItem>())
                    sb.AppendLine("    " + node.Game + " " + node.ID + " " + string.Join(",", (node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(x => x.Value).Select(x => x.Key)));
            }
            sb.AppendLine("-- Auto Group left out");
            foreach (string kept in grouped == null || grouped.Kept == null ? new List<string>() : grouped.Kept)
                sb.AppendLine(kept);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, Regex.Replace(name, @"[^A-Za-z0-9_.-]+", "_") + ".txt"), sb.ToString(), new UTF8Encoding(false));
        }

        // ---- the operations ----

        private static EvalOp Op(string id, string title, string classes, params string[] alternatives)
        {
            return new EvalOp { Id = id, Title = title, Classes = classes, Alternatives = alternatives.Select(a => a.Split('+')).ToList() };
        }

        private static EvalOp NotCounted(EvalOp op)
        {
            op.Counted = false;
            return op;
        }

        private const string All = "fixed,rotary,space";
        private const string Air = "fixed,rotary";

        // Every major operation with its alternatives ('+' = all of them are needed). An operation counts for an aircraft
        // only when its game has a command of it (DB\Functions); see docs/auto-setup-eval.md#operations.
        internal static readonly List<EvalOp> Ops = new List<EvalOp>
        {
            Op("pitch", "Pitch axis", All, "pitch"),
            Op("roll", "Roll axis", All, "roll"),
            Op("yaw", "Yaw / rudder axis", All, "rudder", "space_yaw"),
            Op("throttle", "Throttle / collective axis", All, "throttleleft", "throttleright", "throttleleftnonab", "throttlerightnonab", "throttlef1", "throttlejeffmirage21",
               "throttleleft29", "throttleright29", "throttleleftj11", "throttlerightj11", "space_strafe_fwdaft", "space_speed_limiter"),
            Op("prop_pitch", "Propeller pitch / RPM", "fixed", "rpm_control", "rpm_increase+rpm_decrease"),
            Op("mixture", "Mixture (nozzle lever in the Harrier)", "fixed", "nozzle_angle_mixture_control"),
            Op("strafe_lateral", "Strafe left / right", "space", "space_strafe_hor"),
            Op("strafe_vertical", "Strafe up / down", "space", "space_strafe_ver"),
            Op("fire", "Gun / primary fire", All, "trigger2ndstage", "trigger1ststage"),
            Op("release", "Weapon release / missiles", All, "launchweapon", "space_missile_cycle+launchweapon"),
            Op("weapon_select", "Weapon select", All, "weaponselector1", "weaponselector2", "weaponselector3", "select_gun", "select_heater", "select_mrm", "select_mrm_2",
               "space_missile_cycle", "space_missile_increase", "space_missile_mode", "f_16_df_df_m2k_cqb_nav_mode_next", "f_16_jf_df_m_m2k_cqa_mode_prev"),
            NotCounted(Op("master_arm", "Master arm / weapons safe (not scored)", All, "master_arm_toggle", "master_arm_on+master_arm_off", "gun_safety_lever_toggle", "gunarm+gunsafe", "space_weapons_on+space_weapons_off")),
            Op("gear", "Landing gear", All, "gear_toggle", "gearin+gearout", "space_gear_up+space_gear_down"),
            Op("flaps", "Flaps", Air, "flaps_toggle", "flapsup+flapsdown"),
            Op("airbrake", "Airbrake / speed brake", Air, "airbrake_toggle", "airbakeextend+airbrakeretract"),
            Op("wheel_brakes", "Wheel brakes", Air, "wheelbrakeleftanalog", "wheelbrakerightanalog", "wheelbrakeleftanaloginv", "wheelbrakerightanaloginv", "wheelbrakes_button"),
            Op("trim", "Trim (pitch both ways, or force trim)", Air, "trimnoseup+trim_nosedown", "trimmerset"),
            Op("view", "View / zoom / look around", All, "zoom", "space_cycle_camera_view", "space_look_ahead_toggle",
               "view_snap_forward+view_snap_back+view_snap_left+view_snap_right", "view_look_horizontal+view_look_vertical"),
            Op("comms", "Comms (radio / comm menu)", All, "comm1_up", "comm2_down", "easycomms", "comm3_aft_ics", "comm4_fwd_iff"),
            Op("countermeasures", "Countermeasures (flare / chaff)", All, "cms_fwd_start_program_or_just_flare", "cms_aft_panic_or_just_chaff"),
            Op("sensor_slew", "Sensor / cursor slew", Air, "tdc_horizontal+tdc_vertical", "tdc_horizontal+tdc_vertical_inv", "tdc_slew_up+tdc_slew_down+tdc_slew_left+tdc_slew_right",
               "hcu_x+hcu_y", "ah64_track_controller_x+ah64_track_controller_y", "f14_lantirn_yaw+f14_lantirn_pitch"),
            Op("target", "Target lock / designate", All, "target_management_system_up", "radar_lock", "space_lock_under_reticle", "space_target_closest_hostile", "space_target_hostile_next", "space_pin_selected"),
            Op("master_modes", "Master modes (A/A, A/G, NAV, dogfight)", Air, "mode_a2a_bvr", "mode_a2g", "mode_nav", "mode_a2a_cas", "f_16_df_df_m2k_cqb_nav_mode_next",
               "f_16_jf_df_m_m2k_cqa_mode_prev", "f_16_df_center_jf_nav_m2k_cmd_centerdepress"),
            Op("boost", "Boost / afterburner", "space", "space_boost", "afterburner"),
            Op("power", "Power distribution", "space", "space_power_triangle_max_engine", "space_power_triangle_max_weapons", "space_power_triangle_max_shield", "space_power_triangle_reset"),
            Op("shields", "Shields", "space", "space_shield_front", "space_shield_back", "space_shields_reset"),
            Op("jump", "Quantum / jump drive", "space", "space_quantum_engage", "space_quantum_travel_system")
        };

        private static readonly HashSet<string> Weapons = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "trigger1ststage", "trigger2ndstage", "launchweapon" };
        private static readonly HashSet<string> Cms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cms_fwd_start_program_or_just_flare", "cms_aft_panic_or_just_chaff" };
        private static readonly HashSet<string> Comms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "comm1_up", "comm2_down", "comm3_aft_ics", "comm4_fwd_iff", "easycomms" };
        private static readonly HashSet<string> Trims = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "trimnoseup", "trim_nosedown", "trimleft", "trimright" };
        private static readonly HashSet<string> Throttles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "throttleleft", "throttleright", "throttleleftnonab", "throttlerightnonab", "throttlef1", "throttlejeffmirage21", "throttleleft29", "throttleright29", "throttleleftj11", "throttlerightj11"
        };
        private static readonly Regex HatControl = new Regex(@"^(stick|throttle|yoke|collective|quadrant)\.(.*\.)?(hat|coolie|rocker|trim)\.|^(stick|throttle|yoke|collective)\.hat\.", RegexOptions.Compiled);
        private static readonly Regex FitsYaw = new Regex(@"^pedals\.axis\.rudder$|\.axis\.(twist|rudder|rocker|yaw)$|^stick\.axis\.twist$|^throttle\.axis\.ministick\.x$", RegexOptions.Compiled);
        private static readonly Regex FitsThrottle = new Regex(@"^(throttle|collective|quadrant)\.axis\.(main|left|right|throttle|lever|levers?\.\d+|throttle\.\d+|\d+)$|^(throttle|collective|quadrant|stick|yoke)\.axis\.slider(\.\d+)?$|^quadrant\.axis\.|^throttle\.axis\.(slider|rotary)|^stick\.axis\.throttle", RegexOptions.Compiled);
        private static readonly Regex FitsBrake = new Regex(@"^pedals\.axis\.brake_(left|right)$|brake_lever|\.axis\.brake", RegexOptions.Compiled);

        // ---- running ----

        internal sealed class EvalResult
        {
            public string Date = string.Empty;
            public int Seed;
            public string RegressionFile = string.Empty;
            public List<string> RegressionNotes = new List<string>();
            public List<EvalSetup> Setups = new List<EvalSetup>();
            public Dictionary<string, List<string>> Roles = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            // What the Setup wizard tells the user about the devices (SetupRoles.Rules), per setup.
            public Dictionary<string, List<string>> Messages = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            // The named novice case (PersonaSetups), run on its own.
            public EvalResult Persona;
            public List<EvalTarget> Targets = new List<EvalTarget>();
            public List<EvalRow> Rows = new List<EvalRow>();
            public List<EvalRow> PipelineRows = new List<EvalRow>();
            public List<EvalRow> PipelinePlanRows = new List<EvalRow>();   // the all-games plan before Auto Group / Fill all
            // The typical DCS player: one DCS plan for a hangar of 20 popular modules, every module scored on its own.
            public List<EvalRow> TypicalRows = new List<EvalRow>();
            // ACE COMBAT 8 alone: the quick path for a pad game (setup, Auto Group), scored with engine vjoy pad-check.
            public List<EvalRow> PadRows = new List<EvalRow>();
            // Every suggestion "Fill all" took that names another function than its relation's ("setup: ...").
            public List<string> WrongSuggestions = new List<string>();
            // Essential gates a later stage of the quick path lost (setup alone -> all-games plan -> Auto Group + Fill all).
            public List<string> StageLosses = new List<string>();
            public List<string> KnownLosses = new List<string>();   // losses the owner accepted as a known limitation
            // Grip hats and grip buttons of the hands' devices that no setup-alone plan of the setup uses (L29).
            public Dictionary<string, List<string>> UnusedInputs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            // Suggestions taken that name another function of the same kind (a flood light in an instrument light relation).
            public List<string> OtherSuggestions = new List<string>();
            public List<string> SubstituteSuggestions = new List<string>();
            public Dictionary<string, string> PipelineNotes = new Dictionary<string, string>(StringComparer.Ordinal);
            public bool Pipeline;
            public List<string> Warnings = new List<string>();
            public List<string> NotApplicable = new List<string>();   // setup x target rows left out (VTOL VR native without pedals)
            public long Millis;
        }

        internal static EvalResult Run(List<EvalSetup> setups, List<EvalTarget> targets, string workFolder, int seed, Action<string> progress, bool pipeline = false)
        {
            EvalResult result = new EvalResult { Seed = seed, Setups = setups, Targets = targets, Pipeline = pipeline, Date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            Stopwatch total = Stopwatch.StartNew();
            string previousFake = Environment.GetEnvironmentVariable(GameDeviceDirectory.FakeDevicesEnvironmentVariable);
            bool previousCache = EngineSetupWizard.CacheCatalog;
            EngineSetupWizard.CacheCatalog = true;
            Directory.CreateDirectory(workFolder);
            try
            {
                foreach (EvalSetup setup in setups)
                {
                    List<string> warnings = new List<string>();
                    EngineSetupWizard.DeviceChoices choices;
                    string fake = WriteFakeDevices(setup, workFolder, warnings, out choices);
                    Environment.SetEnvironmentVariable(GameDeviceDirectory.FakeDevicesEnvironmentVariable, fake);
                    List<SetupInput> inputs;
                    List<SetupDevice> devices = EngineSetupWizard.Devices(choices, warnings, out inputs);
                    result.Messages[setup.Id] = SetupRoles.Rules(devices);
                    result.Roles[setup.Id] = devices.Select(d => SetupRoles.Short(d) + " = " + d.Role + (d.Generic ? " (not recognised: generic layout)" : string.Empty) +
                                                                 (d.Assumed ? " (numbers assumed)" : string.Empty) + (d.Connected ? string.Empty : " (added from the catalog)") +
                                                                 " [" + inputs.Count(i => i.Device == d.Device).ToString(CultureInfo.InvariantCulture) + " inputs]").ToList();
                    result.Warnings.AddRange(warnings.Distinct().Select(w => setup.Id + ": " + w));
                    progress?.Invoke("Setup " + setup.Id + ": " + string.Join(", ", result.Roles[setup.Id]));
                    // (L29: the grip inputs no plan of this setup uses - a whole grip hat empty in every row is a finding)
                    HashSet<string> usedInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bool anyPlan = false;
                    foreach (EvalTarget target in targets)
                    {
                        if (target.Pad)
                            continue;
                        if (!Applies(target, inputs))
                        {
                            result.NotApplicable.Add(setup.Id + " x " + target.Id + ": " + NotApplicableReason(target));
                            continue;
                        }
                        EvalRow row = new EvalRow { Setup = setup.Id, SetupKind = setup.Kind, Target = target.Id, Weight = WeightOf(target.Game == "DCS" ? target.Id : target.Game) * target.Share };
                        Stopwatch watch = Stopwatch.StartNew();
                        try
                        {
                            // Fresh copies: the planner sets the roles it proposes on the devices.
                            List<SetupDevice> copies = devices.Select(Copy).ToList();
                            SetupOptions options = new SetupOptions
                            {
                                Games = new List<string> { target.Game },
                                Modifiers = 0,
                                PreferFewer = true,
                                Realism = RealismValue,
                                HeadTracking = HeadTrackingValue,
                                PhaseLayers = "auto",
                                Seed = 1
                            };
                            options.Priorities.Add(new List<string> { target.Aircraft });
                            if (target.Only.Count > 0)
                                options.OnlyAircraft.AddRange(target.Only);
                            if (!copies.Any(d => SetupRoles.Binds(d.Role)))
                            {
                                row.Error = "no device the plan can use";
                            }
                            else
                            {
                                SetupRequest request = EngineSetupWizard.Request(copies, inputs, options, null, new List<string>());
                                SetupPlan plan = SetupPlanner.Plan(request);
                                DumpPlan(setup.Id + "_" + target.Id, plan);
                                anyPlan = true;
                                foreach (SetupPlanRelation placed in plan.Placed)
                                    usedInputs.Add(placed.Device + "|" + placed.Input);
                                foreach (SetupPlanModifier modifier in plan.Modifiers)
                                    usedInputs.Add(modifier.Device + "|" + modifier.Input);
                                Score(row, target, request, TruthPlan(plan, setup.Truth));
                            }
                        }
                        catch (Exception ex)
                        {
                            row.Error = ex.GetType().Name + ": " + ex.Message;
                        }
                        row.Millis = watch.ElapsedMilliseconds;
                        if (row.Error.Length > 0)
                        {
                            row.Coverage = 0;
                            row.Ergonomics = 0;
                            row.Score = 0;
                        }
                        result.Rows.Add(row);
                    }
                    if (anyPlan)
                    {
                        // grip inputs of the hands' devices (sticks, throttles, collectives, left sticks) never used
                        HashSet<string> hands = new HashSet<string>(devices.Where(d => d.Role == SetupRoles.MainStick || d.Role == SetupRoles.HeliStick || d.Role == SetupRoles.Throttle ||
                                                                                       d.Role == SetupRoles.Collective || d.Role == SetupRoles.LeftStick).Select(d => d.Device), StringComparer.Ordinal);
                        List<SetupInput> grips = inputs.Where(i => !i.IsAxis && hands.Contains(i.Device) && SetupPlanner.IsGrip(i.Control) && !string.Equals(i.Type, "selector", StringComparison.OrdinalIgnoreCase) &&
                                                                   !string.Equals(i.Type, "detent", StringComparison.OrdinalIgnoreCase)).ToList();
                        List<string> unused = new List<string>();
                        foreach (IGrouping<string, SetupInput> hat in grips.Where(i => HatControl.IsMatch(i.Control) && i.Control.LastIndexOf('.') > 0)
                                     .GroupBy(i => i.Device + "|" + i.Control.Substring(0, i.Control.LastIndexOf('.')), StringComparer.Ordinal).Where(h => h.Count() >= 4))
                        {
                            if (hat.All(i => !usedInputs.Contains(i.Device + "|" + i.Input)))
                                unused.Add("hat " + SetupRoles.Short(devices.First(d => d.Device == hat.First().Device)) + " " + hat.Key.Substring(hat.Key.IndexOf('|') + 1) +
                                           (string.IsNullOrWhiteSpace(hat.First().Label) ? string.Empty : " \"" + hat.First().Label + "\""));
                        }
                        foreach (SetupInput button in grips.Where(i => !HatControl.IsMatch(i.Control) && !usedInputs.Contains(i.Device + "|" + i.Input)))
                            unused.Add(SetupRoles.Short(devices.First(d => d.Device == button.Device)) + " " + button.Input + " (" + button.Control + (string.IsNullOrWhiteSpace(button.Label) ? string.Empty : " \"" + button.Label + "\"") + ")");
                        if (unused.Count > 0)
                            result.UnusedInputs[setup.Id] = unused;
                    }
                    if (Typical && setup.Kind != "persona")
                        RunTypical(setup, devices, inputs, result);
                    if (PadAlone && setup.Kind != "persona")
                        RunPadAlone(setup, devices, inputs, result);
                    if (pipeline)
                    {
                        try
                        {
                            RunPipeline(setup, devices, inputs, targets, result);
                        }
                        catch (Exception ex)
                        {
                            result.Warnings.Add(setup.Id + ": the Auto Group / Suggest run failed: " + ex.GetType().Name + ": " + ex.Message);
                        }
                        finally
                        {
                            InternalDataManagement.NewFile(true);
                        }
                    }
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(GameDeviceDirectory.FakeDevicesEnvironmentVariable, previousFake);
                EngineSetupWizard.CacheCatalog = previousCache;
            }
            result.Millis = total.ElapsedMilliseconds;
            return result;
        }

        private static SetupDevice Copy(SetupDevice d)
        {
            return new SetupDevice
            {
                Device = d.Device, Model = d.Model, ModelId = d.ModelId, Kind = d.Kind, Hand = d.Hand, Connected = d.Connected, Generic = d.Generic,
                ForceFeedback = d.ForceFeedback, NeedsGrip = d.NeedsGrip, VidPid = d.VidPid, Buttons = d.Buttons, Hats = d.Hats, Axes = d.Axes,
                ProfileUses = d.ProfileUses, Role = d.Role, ProposedRole = d.ProposedRole, Reason = d.Reason, Grips = d.Grips, Grip = d.Grip, Assumed = d.Assumed
            };
        }

        // The setup as Windows would report it (VID:PID, HID name, axes, buttons, hats from the catalog); a grip is
        // reported by its base and picked like the wizard's grip choice. Catalog devices without a USB id are added from
        // the catalog (not connected), as the wizard's "add a device" does.
        private static string WriteFakeDevices(EvalSetup setup, string folder, List<string> warnings, out EngineSetupWizard.DeviceChoices choices)
        {
            choices = new EngineSetupWizard.DeviceChoices();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# JoyPro device\tVID\tPID\tHID product string\taxes\tbuttons\thats");
            int n = 0;
            foreach (EvalDevice item in setup.Devices)
            {
                n++;
                KbDevice model = EngineDeviceKb.Find(item.Catalog, warnings);
                if (model == null)
                {
                    warnings.Add("Not in the device catalog: " + item.Catalog);
                    continue;
                }
                KbDevice baseModel = model.IsGrip ? EngineDeviceKb.Find(model.Base, warnings) : null;
                string vidPid = model.VidPids.FirstOrDefault() ?? (baseModel != null ? baseModel.VidPids.FirstOrDefault() : null);
                string hidName = model.Names.FirstOrDefault() ?? (baseModel != null ? baseModel.Names.FirstOrDefault() : null) ?? model.Name;
                bool unknown = item.ReportVidPid.Length > 0;
                if (unknown)
                {
                    vidPid = item.ReportVidPid;
                    hidName = item.ReportName.Length > 0 ? item.ReportName : hidName;
                }
                if (string.IsNullOrEmpty(vidPid) || !vidPid.Contains(":"))
                {
                    choices.Added.Add(model.Id);
                    if (item.Role.Length > 0)
                        choices.Roles[model.Name] = item.Role;
                    continue;
                }
                List<KbControl> axes = (baseModel != null ? baseModel.Axes : new List<KbControl>()).Concat(model.Axes).ToList();
                List<KbControl> controls = (baseModel != null ? baseModel.Controls : new List<KbControl>()).Concat(model.Controls).ToList();
                List<string> axisNames = new List<string>();
                string[] fallback = { "X", "Y", "Z", "RX", "RY", "RZ", "SLIDER1", "SLIDER2" };
                foreach (KbControl axis in axes)
                {
                    string token = axis.Input.StartsWith("JOY_", StringComparison.OrdinalIgnoreCase) ? axis.Input.Substring(4).ToUpperInvariant() : string.Empty;
                    if (token.Length == 0)
                        token = fallback.FirstOrDefault(f => !axisNames.Contains(f)) ?? string.Empty;
                    if (token.Length > 0 && !axisNames.Contains(token))
                        axisNames.Add(token);
                }
                if (item.ReportAxes.Length > 0)
                    axisNames = item.ReportAxes.Split(',').Select(a => a.Trim().ToUpperInvariant()).Where(a => a.Length > 0).ToList();
                int buttons = 0, hats = 0;
                // Grips whose button numbers are not published (no input in the catalog): Windows still reports a button
                // per control and a POV per 4-way hat.
                List<KbControl> unnumbered = controls.Where(c => string.IsNullOrEmpty(c.Input)).ToList();
                if (unnumbered.Count > 0 && controls.All(c => string.IsNullOrEmpty(c.Input)))
                {
                    int hatDirections = unnumbered.Count(c => Regex.IsMatch(c.Control, @"\.(up|down|left|right)$") && c.Control.Contains(".hat."));
                    hats = Math.Min(4, (hatDirections + 3) / 4);
                    buttons = unnumbered.Count - hats * 4;
                }
                foreach (KbControl control in controls)
                {
                    Match button = Regex.Match(control.Input ?? string.Empty, @"^JOY_BTN(\d+)$", RegexOptions.IgnoreCase);
                    if (button.Success)
                        buttons = Math.Max(buttons, int.Parse(button.Groups[1].Value, CultureInfo.InvariantCulture));
                    Match hat = Regex.Match(control.Input ?? string.Empty, @"^JOY_BTN_POV(\d+)_", RegexOptions.IgnoreCase);
                    if (hat.Success)
                        hats = Math.Max(hats, int.Parse(hat.Groups[1].Value, CultureInfo.InvariantCulture));
                }
                string[] ids = vidPid.Split(':');
                string guid = "{e7a1e7a1-0000-0000-0000-" + (Math.Abs(setup.Id.GetHashCode()) % 1000000).ToString("D6", CultureInfo.InvariantCulture) + n.ToString("D6", CultureInfo.InvariantCulture) + "}";
                string joyProName = hidName + " " + guid;
                sb.AppendLine(joyProName + "\t" + ids[0] + "\t" + ids[1] + "\t" + hidName + "\t" + string.Join(",", axisNames) + "\t" + buttons.ToString(CultureInfo.InvariantCulture) + "\t" + hats.ToString(CultureInfo.InvariantCulture));
                if (unknown)
                {
                    Dictionary<string, KbControl> map = new Dictionary<string, KbControl>(StringComparer.OrdinalIgnoreCase);
                    foreach (KbControl control in axes.Concat(controls).Where(c => !string.IsNullOrEmpty(c.Input)))
                        map[control.Input.ToUpperInvariant()] = control;
                    setup.Truth[joyProName] = map;
                }
                if (!unknown && (model.IsGrip || (baseModel == null && EngineDeviceKb.Load(warnings).Any(k => k.Base.Length > 0 && k.VidPids.Contains(vidPid) && k.Id != model.Id))))
                    choices.Grips[joyProName] = model.Id;
                if (item.Role.Length > 0)
                    choices.Roles[joyProName] = item.Role;
            }
            string file = Path.Combine(folder, "devices-" + Safe(setup.Id) + ".tsv");
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            return file;
        }

        // ---- scoring ----

        private static void Score(EvalRow row, EvalTarget target, SetupRequest request, SetupPlan plan, List<string> wrong = null)
        {
            string plane = target.Aircraft;
            SetupAircraft known;
            string cls = target.ClassOverride.Length > 0 ? target.ClassOverride : request.Aircraft.TryGetValue(plane, out known) ? known.Class : SetupClasses.Fixed;
            row.Class = cls;
            // (a game scored once per vehicle class: the other class's own actions do not count here)
            HashSet<string> otherVehicle = OtherVehicleActions(target);
            Func<SetupNode, bool> counts = n => !string.Equals(n.Aircraft, plane, StringComparison.OrdinalIgnoreCase) || !otherVehicle.Contains(n.Action ?? string.Empty);

            HashSet<string> available = new HashSet<string>(request.Commands
                .Where(c => string.Equals(c.Aircraft, plane, StringComparison.OrdinalIgnoreCase) && !string.Equals(c.Status, "substitute", StringComparison.OrdinalIgnoreCase) && !otherVehicle.Contains(c.Action))
                .Select(c => c.Function), StringComparer.OrdinalIgnoreCase);
            // (a relation shared through a DCS input layer carries the aircraft's command in its nodes)
            List<SetupPlanRelation> mine = plan.Placed.Where(r => (r.Aircraft.Contains(plane, StringComparer.OrdinalIgnoreCase) ||
                                                                  r.Nodes.Any(n => string.Equals(n.Aircraft, plane, StringComparison.OrdinalIgnoreCase))) &&
                                                                 (otherVehicle.Count == 0 || r.Nodes.Count == 0 || r.Nodes.Any(counts))).ToList();
            HashSet<string> placed = new HashSet<string>(mine.Select(r => r.Function).Where(f => f.Length > 0), StringComparer.OrdinalIgnoreCase);
            // A game command the catalog lists under several functions is bound once (under one of them): it does all of them.
            HashSet<string> boundCommands = new HashSet<string>(mine.SelectMany(r => r.Nodes).Where(n => string.Equals(n.Aircraft, plane, StringComparison.OrdinalIgnoreCase) && counts(n))
                .Select(n => n.Game + "|" + n.Action), StringComparer.OrdinalIgnoreCase);
            foreach (SetupCommand command in request.Commands.Where(c => string.Equals(c.Aircraft, plane, StringComparison.OrdinalIgnoreCase) && boundCommands.Contains(c.Game + "|" + c.Action)))
                placed.Add(command.Function);
            Dictionary<string, string> whyNot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (SetupNotPlaced miss in plan.NotPlaced.Where(n => n.Aircraft.Contains(plane, StringComparer.OrdinalIgnoreCase)))
            {
                if (!whyNot.ContainsKey(miss.Function))
                    whyNot[miss.Function] = miss.Reason;
            }

            foreach (EvalOp op in Ops)
            {
                if (!op.Classes.Split(',').Contains(cls))
                    continue;
                List<string[]> alternatives = op.Alternatives.Select(a => a.Where(available.Contains).ToArray()).Where(a => a.Length > 0).ToList();
                if (alternatives.Count == 0)
                    continue;
                if (op.Counted)
                    row.Required++;
                if (alternatives.Any(a => a.All(placed.Contains)))
                {
                    if (op.Counted)
                        row.Done += 1;
                    row.OpsDone.Add(op.Id);
                    continue;
                }
                bool partial = alternatives.Any(a => a.Any(placed.Contains));
                if (partial && op.Counted)
                    row.Done += 0.5;
                string[] best = alternatives.OrderByDescending(a => a.Count(placed.Contains)).ThenBy(a => a.Length).First();
                List<string> missing = best.Where(f => !placed.Contains(f)).ToList();
                string why = string.Join("; ", missing.Select(f => Name(request, f) + ": " + (whyNot.ContainsKey(f) ? whyNot[f] : "no relation made for it")));
                row.Gaps.Add(new EvalGap { Op = op.Id, Title = op.Title, Partial = partial, Why = why, Counted = op.Counted });
            }
            row.Coverage = row.Required == 0 ? 100 : Math.Round(100.0 * row.Done / row.Required, 1);

            Ergonomics(row, cls, request, plan, mine);
            Func<SetupPlanRelation, string> at = r => DeviceShort(plan, r.Device) + " " + r.Control + (r.Label.Length > 0 ? " \"" + r.Label + "\"" : string.Empty) + (r.Modifier.Length > 0 ? " + " + r.Modifier : string.Empty);
            // R9 lever detents only for the lever's own functions (idle / cut-off, reverse, afterburner, engine start / stop)
            Rule(row, mine.Count > 0, DetentMisuse(mine, at), "Lever detents only for the lever's own functions");
            HardGates(row, target, request, plan, mine, cls, available);
            if (wrong != null)
            {
                row.Wrong.AddRange(wrong);
                Rule(row, true, wrong, "No wrong-kind matches");
            }
            row.Ergonomics = row.RulesApplicable == 0 ? 100 : Math.Round(100.0 * row.RulesPassed / row.RulesApplicable, 1);
            row.Score = Math.Round(0.75 * row.Coverage + 0.25 * row.Ergonomics, 1);

            SetupQuality quality = plan.Quality.FirstOrDefault(q => string.Equals(q.Aircraft, plane, StringComparison.OrdinalIgnoreCase));
            if (quality != null)
            {
                row.Quality = Math.Round(quality.Score, 1);
                row.Placed = quality.Placed;
                row.Total = quality.Total;
            }
            row.Modifiers = plan.Modifiers.Count(m => !m.Switch);
            row.Shifts.AddRange(plan.Modifiers.Select(m => m.Name + " = " + DeviceShort(plan, m.Device) + " " + m.Input + " (" + m.Control + ")"));
            row.Forced = plan.Modifiers.Count(m => m.Forced);
            row.PhaseLayers = plan.Modifiers.Count(m => m.Switch);
            foreach (SetupPlanRelation relation in mine.OrderBy(r => r.Category, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                SetupInput input = plan.Inputs.FirstOrDefault(i => i.Device == relation.Device && string.Equals(i.Input, relation.Input, StringComparison.OrdinalIgnoreCase));
                row.Bindings.Add(relation.FunctionName.Length > 0 ? relation.FunctionName : relation.Name);
                row.Bindings[row.Bindings.Count - 1] += " = " + DeviceShort(plan, relation.Device) + " " + relation.Input + " (" + relation.Control +
                                                         (relation.Label.Length > 0 ? " \"" + relation.Label + "\"" : string.Empty) + ")" +
                                                         (relation.Modifier.Length > 0 ? " + " + relation.Modifier : string.Empty);
            }
        }

        // ---- the whole path: setup, Auto Group, Suggest missing ----

        // One profile per setup, as a user who owns every game and DCS family of the run would make it: the setup (wizard
        // defaults, all games at once, the families' modules as owned), then Auto Group (preset "all", substitutes, the
        // owned aircraft and games) and Suggest missing for every relation with every suggestion taken ("Fill all",
        // medium confidence and up, 25 per relation). Then each target is scored from the profile's binds with the same
        // operations and hand rules, plus "no wrong-kind matches": a command in a relation that the function map knows
        // as something else (a brightness knob in a slew relation, a wheel brake in a throttle relation).
        private static void RunPipeline(EvalSetup setup, List<SetupDevice> devices, List<SetupInput> inputs, List<EvalTarget> targets, EvalResult result)
        {
            InternalDataManagement.NewFile(true);
            List<string> games = targets.Where(t => !t.Pad).Select(t => t.Game).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> padGames = targets.Where(t => t.Pad).Select(t => t.Game).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> owned = targets.Where(t => t.Game == "DCS" && !t.Stress).SelectMany(t => t.Only).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<SetupDevice> copies = devices.Select(Copy).ToList();
            if (!copies.Any(d => SetupRoles.Binds(d.Role)))
                return;
            SetupOptions options = new SetupOptions { Games = games, Modifiers = 0, PreferFewer = true, Realism = RealismValue, HeadTracking = HeadTrackingValue, PhaseLayers = "auto", Seed = 1 };
            options.OnlyAircraft.AddRange(owned);
            SetupRequest request = EngineSetupWizard.Request(copies, inputs, options, null, new List<string>());
            SetupPlan plan = SetupPlanner.Plan(request);
            DumpPlan(setup.Id + "_allgames", plan);
            // The all-games plan itself, before Auto Group and Fill all: what they change is the difference to it.
            SetupPlan truthPlan = TruthPlan(plan, setup.Truth);
            foreach (EvalTarget target in targets.Where(t => !t.Pad && Applies(t, inputs)))
            {
                EvalRow row = new EvalRow { Setup = setup.Id, SetupKind = setup.Kind, Target = target.Id, Stage = "pipeline-plan", Weight = WeightOf(target.Game == "DCS" ? target.Id : target.Game) * target.Share };
                try
                {
                    Score(row, target, request, truthPlan);
                }
                catch (Exception ex)
                {
                    row.Error = ex.GetType().Name + ": " + ex.Message;
                }
                result.PipelinePlanRows.Add(row);
            }
            EngineSetupWizard.ApplyResult applied = EngineSetupWizard.Apply(plan, false, true);
            Resync();
            DumpProfile(setup.Id + "_pipeline_1setup", null);
            // The function each relation was made for: the setup's slot, then Auto Group's.
            Dictionary<string, string> slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (SetupPlanRelation placed in plan.Relations.Where(r => !string.IsNullOrEmpty(r.Function)))
                if (!slots.ContainsKey(placed.Name)) slots[placed.Name] = placed.Function;
            List<string> planes = owned.Concat(games.Where(g => !string.Equals(g, "DCS", StringComparison.OrdinalIgnoreCase))).Concat(padGames).ToList();
            EngineAutoGroupResult grouped = EngineAutoGroup.Run("all", true, planes, false, false);
            Resync();
            DumpProfile(setup.Id + "_pipeline_2autogroup", grouped);
            foreach (EngineAutoGroupRelation made in grouped.Relations ?? new List<EngineAutoGroupRelation>())
                if (!string.IsNullOrEmpty(made.Name) && !string.IsNullOrEmpty(made.Slot) && !slots.ContainsKey(made.Name)) slots[made.Name] = made.Slot;

            List<string> warnings = new List<string>();
            EngineFunctionMap.Map map = EngineFunctionMap.Load(warnings);
            Dictionary<string, Judge> judges = new Dictionary<string, Judge>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> pair in InternalDataManagement.AllRelations.Where(p => p.Value != null))
                judges[pair.Key] = JudgeOf(map, pair.Value, slots);
            EngineRelationSuggestResult missing = EngineRelationSuggest.Missing("medium", 25);
            int taken = 0, wrongTaken = 0, otherTaken = 0, substituteTaken = 0;
            foreach (EngineRelationSuggestSummary summary in missing.Relations ?? new List<EngineRelationSuggestSummary>())
            {
                Relation relation;
                if (!InternalDataManagement.AllRelations.TryGetValue(summary.Relation ?? string.Empty, out relation) || relation == null)
                    continue;
                // What the relation was before Fill all: suggestions taken later do not change it.
                Judge judge;
                if (!judges.TryGetValue(summary.Relation, out judge))
                    judges[summary.Relation] = judge = JudgeOf(map, relation, slots);
                List<string> adds = new List<string>();
                foreach (EngineRelationSuggestion item in summary.Items ?? new List<EngineRelationSuggestion>())
                {
                    adds.Add(item.Game + ":" + item.Id + ":" + string.Join("|", item.Planes ?? new List<string>()));
                    List<string> fns = new List<string>();
                    foreach (string plane in (item.Planes ?? new List<string>()).DefaultIfEmpty(item.Game))
                        fns.AddRange(FunctionsOf(map, item.Game, item.Id, plane));
                    fns = fns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    int verdict = judge.Of(fns);
                    if (verdict == 0)
                        continue;
                    if ((item.Planes ?? new List<string>()).DefaultIfEmpty(item.Game).Any(plane => IsSubstitute(item.Game, item.Id, plane)))
                    {
                        substituteTaken++;
                        result.SubstituteSuggestions.Add(setup.Id + ": '" + summary.Relation + "' (" + judge.Main + ") got " + item.Game + " '" + item.Title + "' (" + string.Join("/", fns.Take(2)) + ") for " +
                                                         string.Join(", ", (item.Planes ?? new List<string>()).Take(4)));
                        continue;
                    }
                    if (verdict == 1)
                    {
                        otherTaken++;
                        result.OtherSuggestions.Add(setup.Id + ": '" + summary.Relation + "' (" + judge.Main + ") got " + item.Game + " '" + item.Title + "' (" + string.Join("/", fns.Take(2)) + ")");
                        continue;
                    }
                    wrongTaken++;
                    result.WrongSuggestions.Add(setup.Id + ": '" + summary.Relation + "' (" + judge.Main + ", " + judge.Category + ") got " + item.Game + " '" + item.Title + "' (" + string.Join("/", fns.Take(2)) + ", " +
                                                judge.CategoryOf(fns) + ") for " + string.Join(", ", (item.Planes ?? new List<string>()).Take(4)) + ((item.Planes ?? new List<string>()).Count > 4 ? " ..." : string.Empty) +
                                                " - " + item.Why);
                }
                if (adds.Count == 0)
                    continue;
                EngineRelationSuggestApplyResult done = EngineRelationSuggest.Apply(summary.Relation, adds, new List<string>());
                taken += adds.Count;
            }
            Resync();
            result.PipelineNotes[setup.Id] = "setup: " + (applied.Created + applied.Extended).ToString(CultureInfo.InvariantCulture) + " relations, " + applied.Bound.ToString(CultureInfo.InvariantCulture) +
                                              " binds; Auto Group: " + grouped.RelationsCreated.ToString(CultureInfo.InvariantCulture) + " relations made, " + grouped.RelationsExtended.ToString(CultureInfo.InvariantCulture) +
                                              " extended, " + grouped.NodesAdded.ToString(CultureInfo.InvariantCulture) + " actions; Fill all: " + taken.ToString(CultureInfo.InvariantCulture) + " suggestions taken, " +
                                              wrongTaken.ToString(CultureInfo.InvariantCulture) + " of a wrong kind, " + otherTaken.ToString(CultureInfo.InvariantCulture) + " of another function of the same kind, " + substituteTaken.ToString(CultureInfo.InvariantCulture) + " catalog substitutes of another function";

            SetupPlan profile = TruthPlan(ProfileAsPlan(plan, request, map), setup.Truth);
            DumpProfile(setup.Id + "_pipeline", grouped);
            foreach (EvalTarget target in targets)
            {
                if (target.Pad)
                {
                    EvalRow padRow = new EvalRow { Setup = setup.Id, SetupKind = setup.Kind, Target = target.Id, Stage = "pipeline", Class = SetupClasses.Fixed };
                    try
                    {
                        PadScore(padRow, target, plan);
                    }
                    catch (Exception ex)
                    {
                        padRow.Error = ex.GetType().Name + ": " + ex.Message;
                    }
                    result.PipelineRows.Add(padRow);
                    continue;
                }
                if (!Applies(target, inputs))
                    continue;
                EvalRow row = new EvalRow { Setup = setup.Id, SetupKind = setup.Kind, Target = target.Id, Stage = "pipeline", Weight = WeightOf(target.Game == "DCS" ? target.Id : target.Game) * target.Share };
                try
                {
                    Score(row, target, request, profile, WrongKinds(map, target.Aircraft, slots, null, judges));
                }
                catch (Exception ex)
                {
                    row.Error = ex.GetType().Name + ": " + ex.Message;
                }
                result.PipelineRows.Add(row);
            }
        }

        internal static bool Typical = true;
        internal static bool PadAlone = true;

        // The 20 DCS modules a typical DCS player owns some of (jets, helicopters, warbirds): one plan for all of them.
        internal static readonly string[] TypicalHangar =
        {
            "F-16C_50", "FA-18C_hornet", "A-10C II", "F-14", "AV8BNA", "Ka-50 III", "AH-64D_BLK_II_PLT", "mi-8mt", "P-51D", "SpitfireLFMkIX",
            "Bf-109K-4", "F-15E", "M-2000C", "MiG-29-Fulcrum", "su-27", "UH-1H", "F-4E-45MC", "F-5E-3", "JF-17", "Mirage-F1EE"
        };

        private static SetupPlan RunTypical(EvalSetup setup, List<SetupDevice> devices, List<SetupInput> inputs, EvalResult result)
        {
            List<SetupDevice> copies = devices.Select(Copy).ToList();
            if (!copies.Any(d => SetupRoles.Binds(d.Role)))
                return null;
            try
            {
                SetupOptions options = new SetupOptions { Games = new List<string> { "DCS" }, Modifiers = 0, PreferFewer = true, Realism = RealismValue, HeadTracking = HeadTrackingValue, PhaseLayers = "auto", Seed = 1 };
                options.OnlyAircraft.AddRange(TypicalHangar);
                SetupRequest request = EngineSetupWizard.Request(copies, inputs, options, null, new List<string>());
                SetupPlan plan = TruthPlan(SetupPlanner.Plan(request), setup.Truth);
                DumpPlan(setup.Id + "_typical_" + RealismValue.ToString("0.00", CultureInfo.InvariantCulture), plan);
                foreach (string module in TypicalHangar)
                {
                    EvalRow row = new EvalRow { Setup = setup.Id, SetupKind = setup.Kind, Target = "DCS:typical>" + module, Stage = "typical" };
                    try
                    {
                        Score(row, new EvalTarget { Id = row.Target, Game = "DCS", Aircraft = module }, request, plan);
                    }
                    catch (Exception ex)
                    {
                        row.Error = ex.GetType().Name + ": " + ex.Message;
                    }
                    result.TypicalRows.Add(row);
                }
                return plan;
            }
            catch (Exception ex)
            {
                result.Warnings.Add(setup.Id + ": the typical DCS hangar plan failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        // One DCS module alone (the placement sweep, L21: the slider must keep the gates for a single aircraft too, not only in
        // the 20-module hangar). Rows "DCS:alone>module" go with the typical rows of the sweep.
        internal static readonly string[] SweepSingles = { "F-16C_50", "A-10C II" };

        internal static void RunSingle(EvalSetup setup, List<SetupDevice> devices, List<SetupInput> inputs, EvalResult result, string module)
        {
            List<SetupDevice> copies = devices.Select(Copy).ToList();
            if (!copies.Any(d => SetupRoles.Binds(d.Role)))
                return;
            EvalRow row = new EvalRow { Setup = setup.Id, SetupKind = setup.Kind, Target = "DCS:alone>" + module, Stage = "typical" };
            try
            {
                SetupOptions options = new SetupOptions { Games = new List<string> { "DCS" }, Modifiers = 0, PreferFewer = true, Realism = RealismValue, HeadTracking = HeadTrackingValue, PhaseLayers = "auto", Seed = 1 };
                options.OnlyAircraft.Add(module);
                SetupRequest request = EngineSetupWizard.Request(copies, inputs, options, null, new List<string>());
                SetupPlan plan = TruthPlan(SetupPlanner.Plan(request), setup.Truth);
                Score(row, new EvalTarget { Id = row.Target, Game = "DCS", Aircraft = module }, request, plan);
            }
            catch (Exception ex)
            {
                row.Error = ex.GetType().Name + ": " + ex.Message;
            }
            result.TypicalRows.Add(row);
        }

        // ACE COMBAT 8 alone: a fresh profile, the setup for the game, Auto Group for it, the pad's auto setup scored.
        private static void RunPadAlone(EvalSetup setup, List<SetupDevice> devices, List<SetupInput> inputs, EvalResult result)
        {
            List<SetupDevice> copies = devices.Select(Copy).ToList();
            if (!copies.Any(d => SetupRoles.Binds(d.Role)))
                return;
            EvalRow row = new EvalRow { Setup = setup.Id, SetupKind = setup.Kind, Target = "AceCombat8 (alone)", Stage = "pad", Class = SetupClasses.Fixed };
            try
            {
                InternalDataManagement.NewFile(true);
                SetupOptions options = new SetupOptions { Games = new List<string> { "AceCombat8" }, Modifiers = 0, PreferFewer = true, Realism = RealismValue, HeadTracking = HeadTrackingValue, PhaseLayers = "auto", Seed = 1 };
                SetupRequest request = EngineSetupWizard.Request(copies, inputs, options, null, new List<string>());
                SetupPlan plan = SetupPlanner.Plan(request);
                EngineSetupWizard.Apply(plan, false, true);
                Resync();
                EngineAutoGroup.Run("all", true, new List<string> { "AceCombat8" }, false, false);
                Resync();
                PadScore(row, new EvalTarget { Id = row.Target, Game = "AceCombat8", Aircraft = "AceCombat8", Pad = true }, TruthPlan(plan, setup.Truth));
            }
            catch (Exception ex)
            {
                row.Error = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                InternalDataManagement.NewFile(true);
            }
            result.PadRows.Add(row);
        }

        private static void Resync()
        {
            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
        }

        private static string GameKey(string game)
        {
            return string.IsNullOrWhiteSpace(game) || EngineAutoGroup.IsDcs(game) ? "DCS" : game.Trim();
        }

        // The function map's functions of one command for one aircraft, the strongest first (empty: unknown).
        private static List<string> FunctionsOf(EngineFunctionMap.Map map, string game, string id, string plane)
        {
            string g = GameKey(game);
            EngineFunctionMap.Entry entry;
            if (!map.ByKey.TryGetValue(g + "|" + EngineRelationSuggest.NodeId(g, id) + "|" + (g == "DCS" ? plane : g), out entry) || entry == null)
                return new List<string>();
            return entry.Functions.Where(a => a.Weight >= 0.5).Select(a => a.Function.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // What a relation is, by the knowledge map: Main = the function most of its commands are (each command votes once
        // for every function it has, weight >= 0.5; a tie goes to the function its catalog slot names more strongly).
        // Fitting = Main, the functions that share a command with Main (the F-16's "Nosewheel Steering - Lasing" button is
        // nose wheel steering and laser), the functions of Main's category with at least half of Main's votes, and their
        // related functions. A command fits when one of its functions fits; it is "another function" when one of its
        // functions has a fitting function's category (a flood light in an instrument light relation); otherwise it is a
        // wrong kind (a wheel brake in a throttle relation, a brightness knob in a slew relation).
        private sealed class Judge
        {
            public EngineFunctionMap.Map Map;
            public string Main = string.Empty;
            public string Category = string.Empty;
            public string Slot = string.Empty;
            public HashSet<string> Fitting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public string CategoryOf(List<string> functions)
            {
                return string.Join("/", functions.Select(f => { EngineFunctionMap.Function fn; return Map.ById.TryGetValue(f, out fn) ? fn.Category : string.Empty; }).Where(c => c.Length > 0).Distinct());
            }

            // 0 = fits (or unknown), 1 = another function of the same kind, 2 = a wrong kind.
            public int Of(List<string> functions)
            {
                if (Main.Length == 0 || functions == null || functions.Count == 0)
                    return 0;
                foreach (string f in functions)
                {
                    if (Fitting.Contains(f))
                        return 0;
                    EngineFunctionMap.Function fn;
                    if (!Map.ById.TryGetValue(f, out fn))
                        continue;
                    if (fn.Related.Any(r => Fitting.Contains(r)))
                        return 0;
                    // The relation's catalog slot holds this function too (the F-16's nose wheel steering / laser button, a
                    // jettison slot for fuel dump and drop tanks, a HOTAS switch position): the catalog made them one.
                    if (Slot.Length > 0 && fn.Slots.Any(l => string.Equals(l.Slot, Slot, StringComparison.OrdinalIgnoreCase) && l.Weight >= 0.5))
                        return 0;
                }
                foreach (string f in functions)
                {
                    EngineFunctionMap.Function fn;
                    if (Map.ById.TryGetValue(f, out fn) && Categories.Contains(fn.Category))
                        return 1;
                }
                return 2;
            }
        }

        private static HashSet<string> substituteKeys;

        // A catalog substitute (DB/Functions, status "substitute"): on purpose another function, the closest one where the
        // aircraft has none of its own (the UH-1H's search light on the CMS switch). Not counted as a wrong kind.
        private static bool IsSubstitute(string game, string id, string plane)
        {
            if (substituteKeys == null)
            {
                HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, EngineAutoGroup.Slot> slots;
                Dictionary<string, EngineAutoGroup.Aircraft> aircraft;
                List<EngineAutoGroup.Row> rows;
                if (EngineAutoGroup.TryLoadCatalog(EngineNlGroup.FunctionsFolder(), new EngineAutoGroupResult(), out slots, out aircraft, out rows))
                {
                    foreach (EngineAutoGroup.Row row in rows.Where(r => string.Equals(r.Status, "substitute", StringComparison.OrdinalIgnoreCase)))
                    {
                        string g = GameKey(row.Game);
                        keys.Add(g + "|" + EngineRelationSuggest.NodeId(g, row.Action) + "|" + (g == "DCS" ? row.Plane : g));
                    }
                }
                substituteKeys = keys;
            }
            string k = GameKey(game);
            return substituteKeys.Contains(k + "|" + EngineRelationSuggest.NodeId(k, id) + "|" + (k == "DCS" ? plane : k));
        }

        private static Judge JudgeOf(EngineFunctionMap.Map map, Relation relation, Dictionary<string, string> slots)
        {
            Judge judge = new Judge { Map = map };
            string slot;
            if (slots != null && slots.TryGetValue(relation.NAME ?? string.Empty, out slot))
                judge.Slot = slot ?? string.Empty;
            Dictionary<string, int> votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<List<string>> nodes = new List<List<string>>();
            foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
            {
                if (node == null || string.IsNullOrWhiteSpace(node.ID))
                    continue;
                string game = GameKey(node.Game);
                IEnumerable<string> planes = game == "DCS" ? (node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value).Select(p => p.Key) : new[] { game };
                List<string> fns = planes.SelectMany(plane => FunctionsOf(map, game, node.ID, plane)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (fns.Count == 0)
                    continue;
                nodes.Add(fns);
                foreach (string f in fns)
                    votes[f] = (votes.ContainsKey(f) ? votes[f] : 0) + 1;
            }
            if (votes.Count == 0)
                return judge;
            Func<string, double> slotWeight = f =>
            {
                EngineFunctionMap.Function fn;
                if (judge.Slot.Length == 0 || !map.ById.TryGetValue(f, out fn))
                    return 0;
                EngineFunctionMap.SlotLink link = fn.Slots.FirstOrDefault(l => string.Equals(l.Slot, judge.Slot, StringComparison.OrdinalIgnoreCase));
                return link == null ? 0 : link.Weight;
            };
            // The function its catalog slot names wins when one of its commands is it (the relation was made for it).
            List<KeyValuePair<string, int>> ofSlot = votes.Where(v => slotWeight(v.Key) >= 0.5).ToList();
            judge.Main = (ofSlot.Count > 0 ? ofSlot : votes.ToList()).OrderByDescending(v => v.Value + 0.1 * slotWeight(v.Key)).ThenBy(v => v.Key, StringComparer.Ordinal).First().Key;
            EngineFunctionMap.Function main;
            judge.Category = map.ById.TryGetValue(judge.Main, out main) ? main.Category : string.Empty;
            int top = votes[judge.Main];
            judge.Fitting.Add(judge.Main);
            foreach (List<string> fns in nodes.Where(n => n.Contains(judge.Main, StringComparer.OrdinalIgnoreCase)))
                judge.Fitting.UnionWith(fns);
            foreach (KeyValuePair<string, int> vote in votes)
            {
                EngineFunctionMap.Function fn;
                if (vote.Value * 2 >= top && map.ById.TryGetValue(vote.Key, out fn) && fn.Category == judge.Category)
                    judge.Fitting.Add(vote.Key);
            }
            foreach (string f in judge.Fitting.ToList())
            {
                EngineFunctionMap.Function fn;
                if (map.ById.TryGetValue(f, out fn))
                {
                    judge.Fitting.UnionWith(fn.Related);
                    if (fn.Category.Length > 0)
                        judge.Categories.Add(fn.Category);
                }
            }
            return judge;
        }

        // Commands of the scored aircraft in relations of a wrong kind ("'Throttle' (throttle): Wheel Brake Left
        // (wheel_brakes)"); only commands the function map knows count.
        private static List<string> WrongKinds(EngineFunctionMap.Map map, string target, Dictionary<string, string> slots, List<string> other, Dictionary<string, Judge> judges = null)
        {
            List<string> wrong = new List<string>();
            foreach (KeyValuePair<string, Relation> pair in InternalDataManagement.AllRelations.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                Relation relation = pair.Value;
                if (relation == null || relation.NODES == null)
                    continue;
                Judge judge;
                if (judges == null || !judges.TryGetValue(pair.Key, out judge))
                    judge = JudgeOf(map, relation, slots);
                if (judge.Main.Length == 0)
                    continue;
                foreach (RelationItem node in relation.NODES.Where(n => n != null && !string.IsNullOrWhiteSpace(n.ID)))
                {
                    string game = GameKey(node.Game);
                    bool forTarget = game == "DCS" ? node.AIRCRAFT != null && node.AIRCRAFT.Any(p => p.Value && string.Equals(p.Key, target, StringComparison.OrdinalIgnoreCase))
                                                   : string.Equals(game, target, StringComparison.OrdinalIgnoreCase);
                    if (!forTarget)
                        continue;
                    List<string> fns = FunctionsOf(map, game, node.ID, game == "DCS" ? target : game);
                    int verdict = judge.Of(fns);
                    if (verdict == 0 || IsSubstitute(game, node.ID, game == "DCS" ? target : game))
                        continue;
                    EngineFunctionMap.Entry entry;
                    map.ByKey.TryGetValue(game + "|" + EngineRelationSuggest.NodeId(game, node.ID) + "|" + (game == "DCS" ? target : game), out entry);
                    string title = entry != null && entry.Command != null ? entry.Command.Title : node.ID;
                    string text = "'" + pair.Key + "' (" + judge.Main + "): " + title + " (" + string.Join("/", fns.Take(2)) + ")";
                    if (verdict == 2)
                        wrong.Add(text);
                    else if (other != null)
                        other.Add(text);
                }
            }
            return wrong;
        }

        // The profile's binds as a plan of the setup's devices: per relation and function slot of its commands (the
        // catalog's slot, else the function map's slot of the command's function and direction) one placed entry.
        private static SetupPlan ProfileAsPlan(SetupPlan plan, SetupRequest request, EngineFunctionMap.Map map)
        {
            SetupPlan profile = new SetupPlan { Devices = plan.Devices, Inputs = plan.Inputs, Modifiers = plan.Modifiers, Notes = plan.Notes };
            Dictionary<string, List<string>> slotsOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (SetupCommand command in request.Commands.Where(c => !string.Equals(c.Status, "substitute", StringComparison.OrdinalIgnoreCase)))
            {
                string key = GameKey(command.Game) + "|" + command.Action + "|" + command.Aircraft;
                List<string> list;
                if (!slotsOf.TryGetValue(key, out list))
                    slotsOf[key] = list = new List<string>();
                if (!list.Contains(command.Function, StringComparer.OrdinalIgnoreCase))
                    list.Add(command.Function);
            }
            foreach (KeyValuePair<string, Relation> pair in InternalDataManagement.AllRelations)
            {
                Relation relation = pair.Value;
                Bind bind;
                if (relation == null || relation.NODES == null || !InternalDataManagement.AllBinds.TryGetValue(pair.Key, out bind) || bind == null || string.IsNullOrWhiteSpace(bind.Joystick))
                    continue;
                string input = (relation.ISAXIS ? bind.JAxis : bind.JButton) ?? string.Empty;
                // (two identical sticks have one product name: the instance GUID tells them apart)
                Match guid = Regex.Match(bind.Joystick, @"\{[0-9A-Fa-f-]{36}\}");
                SetupDevice device = plan.Devices.FirstOrDefault(d => d.Device == bind.Joystick) ??
                                     (guid.Success ? plan.Devices.FirstOrDefault(d => d.Device.IndexOf(guid.Value, StringComparison.OrdinalIgnoreCase) >= 0) : null) ??
                                     plan.Devices.FirstOrDefault(d => EngineSetupWizard.Product(d.Device) == EngineSetupWizard.Product(bind.Joystick));
                SetupInput setupInput = device == null ? null : plan.Inputs.FirstOrDefault(i => i.Device == device.Device && string.Equals(i.Input, input.Trim(), StringComparison.OrdinalIgnoreCase));
                if (setupInput == null)
                    continue;
                Modifier modifier = bind.AllReformers != null && bind.AllReformers.Count > 0 ? Modifier.ReformerToMod(bind.AllReformers[0]) : null;
                Dictionary<string, SetupPlanRelation> bySlot = new Dictionary<string, SetupPlanRelation>(StringComparer.OrdinalIgnoreCase);
                foreach (RelationItem node in relation.NODES.Where(n => n != null && !string.IsNullOrWhiteSpace(n.ID)))
                {
                    string game = GameKey(node.Game);
                    IEnumerable<string> planesOf = game == "DCS" ? (node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value).Select(p => p.Key) : new[] { game };
                    foreach (string plane in planesOf)
                    {
                        List<string> slots;
                        if (!slotsOf.TryGetValue(game + "|" + node.ID + "|" + plane, out slots))
                        {
                            slots = new List<string>();
                            EngineFunctionMap.Entry entry;
                            if (map.ByKey.TryGetValue(game + "|" + EngineRelationSuggest.NodeId(game, node.ID) + "|" + (game == "DCS" ? plane : game), out entry) && entry != null && entry.Functions.Count > 0)
                            {
                                EngineFunctionMap.Assignment first = entry.Functions[0];
                                slots.AddRange(first.Function.Slots.Where(l => l.Dir.Length == 0 || first.Dir.Length == 0 || string.Equals(l.Dir, first.Dir, StringComparison.OrdinalIgnoreCase))
                                    .Select(l => l.Slot));
                                // The relation's kind decides among the function's slots (a button relation of the wheel
                                // brakes is the brake button, not the toe brake axes).
                                SetupFunction kindOf;
                                if (slots.Any(l => request.Functions.TryGetValue(l, out kindOf) && kindOf.IsAxis == relation.ISAXIS))
                                    slots.RemoveAll(l => !request.Functions.TryGetValue(l, out kindOf) || kindOf.IsAxis != relation.ISAXIS);
                                // (a button command the map reads by a word of its title as an axis function - the A-10C's "HOTAS
                                // Left Throttle Button" is no throttle: on a button relation it is no throttle relation)
                                else if (slots.All(l => request.Functions.TryGetValue(l, out kindOf) && kindOf.IsAxis != relation.ISAXIS))
                                    slots.Clear();
                            }
                        }
                        foreach (string slot in slots.Count > 0 ? slots : new List<string> { string.Empty })
                        {
                            SetupPlanRelation placed;
                            if (!bySlot.TryGetValue(slot, out placed))
                            {
                                SetupFunction function;
                                bySlot[slot] = placed = new SetupPlanRelation
                                {
                                    Name = pair.Key, Function = slot, IsAxis = relation.ISAXIS, Kept = true, Device = device.Device, Input = setupInput.Input,
                                    FunctionName = slot.Length > 0 && request.Functions.TryGetValue(slot, out function) ? function.Name : pair.Key,
                                    Control = setupInput.Control, Label = setupInput.Label, Modifier = modifier == null ? string.Empty : modifier.name ?? string.Empty
                                };
                            }
                            if (!placed.Aircraft.Contains(plane, StringComparer.OrdinalIgnoreCase))
                                placed.Aircraft.Add(plane);
                            placed.Nodes.Add(new SetupNode { Game = game, Action = node.ID, Aircraft = plane });
                        }
                    }
                }
                profile.Relations.AddRange(bySlot.Values);
            }
            return profile;
        }

        private static string Name(SetupRequest request, string function)
        {
            SetupFunction known;
            return request.Functions.TryGetValue(function, out known) && known.Name.Length > 0 ? known.Name : function;
        }

        private static string DeviceShort(SetupPlan plan, string device)
        {
            SetupDevice known = plan.Devices.FirstOrDefault(d => d.Device == device);
            string name = known != null ? SetupRoles.Short(known) : Regex.Replace(device ?? string.Empty, @"\s*\{[0-9A-Fa-f-]+\}\s*$", string.Empty);
            // (L32: two identical devices - two VKB sticks, two T.16000M - are told apart by their role and instance)
            if (known != null && plan.Devices.Count(d => SetupRoles.Short(d) == name) > 1)
            {
                Match instance = Regex.Match(device ?? string.Empty, @"\{[0-9A-Fa-f-]+\}");
                name += " [" + known.Role + (instance.Success ? " " + instance.Value.Substring(instance.Value.Length - 5, 4) : string.Empty) + "]";
            }
            return name;
        }

        private static void Rule(EvalRow row, bool applicable, List<string> problems, string rule)
        {
            if (!applicable)
                return;
            row.RulesApplicable++;
            if (problems.Count == 0)
                row.RulesPassed++;
            else
                row.Violations.Add(rule + ": " + string.Join("; ", problems.Distinct().Take(6)) + (problems.Distinct().Count() > 6 ? " ..." : string.Empty));
        }

        private static void Ergonomics(EvalRow row, string cls, SetupRequest request, SetupPlan plan, List<SetupPlanRelation> mine)
        {
            SetupDevice right = SetupRoles.RightHand(plan.Devices, cls);
            SetupDevice left = SetupRoles.LeftHand(plan.Devices, cls);
            Func<SetupPlanRelation, string> where = r => DeviceShort(plan, r.Device) + " " + r.Control + (r.Modifier.Length > 0 ? " + " + r.Modifier : string.Empty);
            Func<SetupPlanRelation, string> fn = r => r.FunctionName.Length > 0 ? r.FunctionName : r.Name;

            // R1 one device per hand
            List<string> doubled = new[] { SetupRoles.MainStick, SetupRoles.HeliStick, SetupRoles.LeftStick, SetupRoles.Throttle, SetupRoles.Collective, SetupRoles.Pedals }
                .Where(role => plan.Devices.Count(d => d.Role == role) > 1).Select(role => role + " twice").ToList();
            Rule(row, true, doubled, "One device per hand");

            // R2 trim on a hat
            List<SetupPlanRelation> trims = mine.Where(r => Trims.Contains(r.Function)).ToList();
            Rule(row, trims.Count > 0, trims.Where(r => !HatControl.IsMatch(r.Control) || IsPanelDevice(plan, r.Device)).Select(r => fn(r) + " on " + where(r)).ToList(), "Trim on a hat");

            // R3 weapons on the stick (right hand)
            List<SetupPlanRelation> weapons = mine.Where(r => Weapons.Contains(r.Function)).ToList();
            Rule(row, weapons.Count > 0 && right != null, weapons.Where(r => right == null || r.Device != right.Device).Select(r => fn(r) + " on " + where(r)).ToList(), "Weapons on the stick");

            // R4 countermeasures on the throttle (or the stick's own CMS hat, as in the F-16 and A-10C)
            bool leftHand = left != null && (right == null || left.Device != right.Device);
            bool combined = !leftHand && right != null && plan.Inputs.Any(i => i.Device == right.Device && i.Control.StartsWith("throttle.", StringComparison.Ordinal));
            Func<SetupPlanRelation, string, bool> onThrottle = (r, stickHat) =>
                (leftHand && r.Device == left.Device) || (combined && r.Device == right.Device && r.Control.StartsWith("throttle.", StringComparison.Ordinal)) ||
                (stickHat.Length > 0 && r.Control.StartsWith(stickHat, StringComparison.Ordinal));
            List<SetupPlanRelation> cms = mine.Where(r => Cms.Contains(r.Function)).ToList();
            Rule(row, cms.Count > 0 && (leftHand || combined), cms.Where(r => !onThrottle(r, "stick.hat.cms.")).Select(r => fn(r) + " on " + where(r)).ToList(), "Countermeasures on the throttle");

            // R5 comms on the throttle (or the stick's mic hat)
            List<SetupPlanRelation> comms = mine.Where(r => Comms.Contains(r.Function)).ToList();
            Rule(row, comms.Count > 0 && (leftHand || combined), comms.Where(r => !onThrottle(r, "stick.hat.mic.")).Select(r => fn(r) + " on " + where(r)).ToList(), "Comms on the throttle");

            // R6 axes on fitting axes
            List<string> axisProblems = new List<string>();
            foreach (SetupPlanRelation r in mine.Where(x => x.IsAxis))
            {
                string f = r.Function;
                bool ok = true;
                if (f == "pitch")
                    ok = Regex.IsMatch(r.Control, @"\.axis\.pitch$");
                else if (f == "roll")
                    ok = Regex.IsMatch(r.Control, @"\.axis\.roll$");
                else if (f == "rudder" || f == "space_yaw")
                    ok = FitsYaw.IsMatch(r.Control);
                else if (Throttles.Contains(f))
                    ok = FitsThrottle.IsMatch(r.Control) || (Role(plan, r.Device) == SetupRoles.LeftStick && r.Control == "stick.axis.pitch");
                else if (f.StartsWith("wheelbrake", StringComparison.Ordinal))
                    ok = FitsBrake.IsMatch(r.Control);
                if (!ok)
                    axisProblems.Add(fn(r) + " on " + where(r));
            }
            Rule(row, mine.Any(x => x.IsAxis), axisProblems, "Axes on fitting axes");

            // R7 time-critical functions on the grips, without a modifier
            List<string> slow = new List<string>();
            foreach (SetupPlanRelation r in mine)
            {
                SetupFunction function;
                if (!request.Functions.TryGetValue(r.Function, out function) || SetupPhases.NormalizeCrit(function.Crit) != "instant" || r.IsAxis)
                    continue;
                SetupDevice device = plan.Devices.FirstOrDefault(d => d.Device == r.Device);
                bool grip = device != null && (device.Role == SetupRoles.MainStick || device.Role == SetupRoles.HeliStick || device.Role == SetupRoles.Throttle ||
                                               device.Role == SetupRoles.Collective || device.Role == SetupRoles.LeftStick);
                if (!grip || r.Modifier.Length > 0)
                    slow.Add(fn(r) + " on " + where(r));
            }
            Rule(row, mine.Any(r => !r.IsAxis), slow, "Time-critical functions on the grips, no modifier");

            // R8 axis functions only on axes, buttons only on buttons
            List<string> types = new List<string>();
            foreach (SetupPlanRelation r in mine)
            {
                SetupInput input = plan.Inputs.FirstOrDefault(i => i.Device == r.Device && string.Equals(i.Input, r.Input, StringComparison.OrdinalIgnoreCase));
                if (input != null && input.IsAxis != r.IsAxis)
                    types.Add(fn(r) + (r.IsAxis ? " (axis) on a button " : " (button) on an axis ") + where(r));
            }
            Rule(row, mine.Count > 0, types, "No axis function on a button");
        }

        // A left stick is the throttle side of a dual-stick setup: its Y axis is the throttle / collective there.
        private static string Role(SetupPlan plan, string device)
        {
            SetupDevice known = plan.Devices.FirstOrDefault(d => d.Device == device);
            return known != null ? known.Role : string.Empty;
        }

        private static bool IsPanelDevice(SetupPlan plan, string device)
        {
            SetupDevice known = plan.Devices.FirstOrDefault(d => d.Device == device);
            return known != null && (known.Role == SetupRoles.Panel || known.Role == SetupRoles.Pedals);
        }

        private static string Safe(string name)
        {
            return Regex.Replace(name ?? string.Empty, @"[^A-Za-z0-9_.-]+", "_");
        }

        // ---- the report ----

        internal static string Pct(double value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static double Mean(IEnumerable<double> values)
        {
            List<double> list = values.ToList();
            return list.Count == 0 ? 0 : list.Average();
        }

        private static object RowJson(EvalRow r)
        {
            return new Dictionary<string, object>
            {
                { "Setup", r.Setup }, { "Kind", r.SetupKind }, { "Target", r.Target }, { "Stage", r.Stage }, { "Class", r.Class }, { "Required", r.Required }, { "Done", r.Done },
                { "Coverage", r.Coverage }, { "Ergonomics", r.Ergonomics }, { "Score", r.Score }, { "Quality", r.Quality }, { "Placed", r.Placed }, { "Total", r.Total },
                { "Modifiers", r.Modifiers }, { "Forced", r.Forced }, { "PhaseLayers", r.PhaseLayers }, { "Millis", r.Millis }, { "Error", r.Error },
                { "Gaps", r.Gaps.Select(g => (object)new Dictionary<string, object> { { "Op", g.Op }, { "Title", g.Title }, { "Partial", g.Partial }, { "Counted", g.Counted }, { "Why", g.Why } }).ToList() },
                { "Violations", r.Violations.Cast<object>().ToList() },
                { "Wrong", r.Wrong.Cast<object>().ToList() },
                { "OpsDone", r.OpsDone.Cast<object>().ToList() },
                { "Bindings", r.Bindings.Cast<object>().ToList() },
                { "Weight", r.Weight }, { "HardPass", r.HardPass },
                { "EssentialClass", r.EssentialClass }, { "EssentialsPct", Math.Round(EssentialPct(r), 1) }, { "CriticalComplete", CriticalComplete(r) },
                { "Essentials", r.Essentials.Select(e => (object)new Dictionary<string, object> { { "Id", e.Id }, { "Status", e.Status }, { "Critical", e.Critical }, { "Detail", e.Detail } }).ToList() },
                { "Gates", r.Gates.Select(g => (object)new Dictionary<string, object> { { "Gate", g.Gate }, { "Status", g.Status }, { "Detail", g.Detail } }).ToList() },
                { "Destructive", r.Destructive.Cast<object>().ToList() },
                { "Shifts", r.Shifts.Cast<object>().ToList() }
            };
        }

        internal static Dictionary<string, object> ToJson(EvalResult result)
        {
            return new Dictionary<string, object>
            {
                { "Date", result.Date },
                { "Seed", result.Seed },
                { "Millis", result.Millis },
                { "Setups", result.Setups.Select(s => (object)new Dictionary<string, object>
                    {
                        { "Id", s.Id }, { "Kind", s.Kind }, { "Name", s.Name }, { "Seed", s.Seed }, { "Shape", s.Shape }, { "Devices", s.Devices.Select(d => (object)(d.Catalog + (d.Role.Length > 0 ? "=" + d.Role : string.Empty))).ToList() },
                        { "Roles", (result.Roles.ContainsKey(s.Id) ? result.Roles[s.Id] : new List<string>()).Cast<object>().ToList() }
                    }).ToList() },
                { "Targets", result.Targets.Select(t => (object)new Dictionary<string, object> { { "Id", t.Id }, { "Game", t.Game }, { "Aircraft", t.Aircraft }, { "Only", t.Only.Cast<object>().ToList() } }).ToList() },
                { "Rows", result.Rows.Select(RowJson).ToList() },
                { "Pipeline", result.Pipeline },
                { "PipelineRows", result.PipelineRows.Select(RowJson).ToList() },
                { "PipelinePlanRows", result.PipelinePlanRows.Select(RowJson).ToList() },
                { "TypicalRows", result.TypicalRows.Select(RowJson).ToList() },
                { "PadRows", result.PadRows.Select(RowJson).ToList() },
                { "PipelineNotes", result.PipelineNotes.ToDictionary(p => p.Key, p => (object)p.Value) },
                { "WrongSuggestions", result.WrongSuggestions.Cast<object>().ToList() },
                { "StageLosses", result.StageLosses.Cast<object>().ToList() },
                { "KnownLosses", result.KnownLosses.Cast<object>().ToList() },
                { "UnusedInputs", result.UnusedInputs.ToDictionary(p => p.Key, p => (object)p.Value.Cast<object>().ToList()) },
                { "OtherSuggestions", result.OtherSuggestions.Cast<object>().ToList() },
                { "SubstituteSuggestions", result.SubstituteSuggestions.Cast<object>().ToList() },
                { "Warnings", result.Warnings.Distinct().Cast<object>().ToList() },
                { "NotApplicable", result.NotApplicable.Cast<object>().ToList() }
            };
        }

        internal sealed class BaselineRow
        {
            public double Coverage;
            public double Ergonomics;
            public double Score;
            public bool? HardPass;
        }

        internal static Dictionary<string, BaselineRow> ReadBaseline(string file, List<string> warnings)
        {
            Dictionary<string, BaselineRow> rows = new Dictionary<string, BaselineRow>(StringComparer.Ordinal);
            try
            {
                Dictionary<string, object> root = EngineSerialization.FromJson<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
                object list;
                if (!root.TryGetValue("Rows", out list) || !(list is System.Collections.IEnumerable))
                    return rows;
                foreach (object item in (System.Collections.IEnumerable)list)
                {
                    Dictionary<string, object> row = item as Dictionary<string, object>;
                    if (row == null)
                        continue;
                    string key = Convert.ToString(row["Setup"], CultureInfo.InvariantCulture) + "|" + Convert.ToString(row["Target"], CultureInfo.InvariantCulture);
                    rows[key] = new BaselineRow
                    {
                        Coverage = Convert.ToDouble(row["Coverage"], CultureInfo.InvariantCulture),
                        Ergonomics = Convert.ToDouble(row["Ergonomics"], CultureInfo.InvariantCulture),
                        Score = Convert.ToDouble(row["Score"], CultureInfo.InvariantCulture),
                        HardPass = row.ContainsKey("HardPass") ? (bool?)Convert.ToBoolean(row["HardPass"], CultureInfo.InvariantCulture) : null
                    };
                }
            }
            catch (Exception ex)
            {
                warnings.Add("The baseline could not be read: " + ex.Message);
            }
            return rows;
        }

        private static string Cell(string text)
        {
            return (text ?? string.Empty).Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        }

        internal static string Markdown(EvalResult result, Dictionary<string, BaselineRow> baseline, string commandLine, List<string> failures)
        {
            StringBuilder sb = new StringBuilder();
            List<EvalRow> rows = result.Rows;
            sb.AppendLine("# Auto setup evaluation " + result.Date);
            sb.AppendLine();
            sb.AppendLine("Made by `engine " + commandLine + "` (JoyPro " + EngineVersionText() + ") in " + (result.Millis / 1000.0).ToString("0", CultureInfo.InvariantCulture) + " s: " +
                          result.Setups.Count.ToString(CultureInfo.InvariantCulture) + " setups x " + result.Targets.Count.ToString(CultureInfo.InvariantCulture) + " targets = " +
                          rows.Count.ToString(CultureInfo.InvariantCulture) + " plans. The planner ran with the Setup wizard's defaults (no modifier wanted, as few as possible, realism 50 %, " +
                          "phase layers automatic) and the target aircraft as the only priority. Every number below is computed by the engine from the plans; nothing was judged by hand.");
            sb.AppendLine();
            sb.AppendLine("Random setups: seed **" + result.Seed.ToString(CultureInfo.InvariantCulture) + "** (a new one every run; repeat this run with `--seed " + result.Seed.ToString(CultureInfo.InvariantCulture) + "`)" +
                          (result.RegressionFile.Length > 0 ? "; regression set: `" + result.RegressionFile + "` (" + result.Setups.Count(s => s.Kind == "regression").ToString(CultureInfo.InvariantCulture) + " setups)." : "; no regression set."));
            foreach (string note in result.RegressionNotes)
                sb.AppendLine("- " + note);
            sb.AppendLine();
            sb.AppendLine("Files next to this report: `results.json` (every row with its gaps, rule problems and the bindings of the scored aircraft), `rows.csv`, " +
                          "`setups/<setup>.md` (per setup and target: what is bound where), `devices-*.tsv` (the fake device files, as Windows would report the devices).");
            sb.AppendLine();
            if (failures != null && failures.Count > 0)
            {
                sb.AppendLine("**Thresholds failed:**");
                foreach (string failure in failures)
                    sb.AppendLine("- " + failure);
                sb.AppendLine();
            }

            sb.AppendLine("## How the score is made");
            sb.AppendLine();
            sb.AppendLine("- **Coverage** = operations done / operations the game has (a partly done operation, e.g. gear up without gear down, counts half). An operation " +
                          "counts for a target only when its game has a command of it for the scored aircraft (the knowledge map `DB/Functions`); it is done when one of its " +
                          "alternatives is bound completely for that aircraft.");
            sb.AppendLine("- **Ergonomics** = hand rules kept / hand rules that apply (a rule applies when its functions are bound).");
            sb.AppendLine("- **Hard gates**: per row the fly-and-fight functions a pilot can really use (see \"Hard gates\" below); a row passes only when every gate that applies is met and no destructive binding is found. The means are weighted: MSFS 2020 / 2024, X-Plane 11 / 12 and Arma 2 / 2 OA count half each (one game in two versions).");
            sb.AppendLine("- **Score** = 0.75 x coverage + 0.25 x ergonomics. **Quality** is the planner's own measure for the aircraft (how close each function is to its best position).");
            sb.AppendLine();
            sb.AppendLine("Master arm is reported but not scored: it is not on the list of major operations, and in most aircraft it is a cockpit switch (clicked, or on a panel).");
            sb.AppendLine();
            sb.AppendLine("| Operation | Classes | Alternatives (function ids of `DB/Functions/slots.json`; `+` = all needed) |");
            sb.AppendLine("|---|---|---|");
            foreach (EvalOp op in Ops)
                sb.AppendLine("| " + op.Title + " | " + op.Classes + " | " + string.Join(" / ", op.Alternatives.Select(a => "`" + string.Join("+", a) + "`")) + " |");
            sb.AppendLine();
            sb.AppendLine("Hand rules:");
            sb.AppendLine();
            sb.AppendLine("1. **One device per hand**: no role (main stick, throttle, left stick, collective, pedals) on two devices.");
            sb.AppendLine("2. **Trim on a hat**: pitch / roll trim on a hat (or a yoke's trim rocker) of a hand device, never a plain button or a panel.");
            sb.AppendLine("3. **Weapons on the stick**: trigger and weapon release on the right-hand device.");
            sb.AppendLine("4. **Countermeasures on the throttle**: flare / chaff on the left-hand device, or on the stick's own CMS hat (F-16, A-10C grips). Applies when there is a left-hand device (or a HOTAS in one piece).");
            sb.AppendLine("5. **Comms on the throttle**: radio / comm menu on the left-hand device (or a stick's mic hat). Same condition.");
            sb.AppendLine("6. **Axes on fitting axes**: pitch / roll on a stick's or yoke's pitch / roll axis, yaw on pedals or a twist, throttle on a throttle / quadrant / collective lever or slider (or a left stick's Y in a dual-stick setup), toe brakes on pedals.");
            sb.AppendLine("7. **Time-critical functions on the grips, no modifier**: functions marked `instant` in `slot_controls.tsv` (trigger, weapon release, CMS, TMS ...) sit on a hand device and not behind a shift button.");
            sb.AppendLine("8. **No axis function on a button** (and no button function on an axis).");
            sb.AppendLine();

            sb.AppendLine("## Summary");
            sb.AppendLine();
            Action<string, IEnumerable<EvalRow>> line = (title, set) =>
            {
                List<EvalRow> list = set.ToList();
                string delta = string.Empty;
                if (baseline != null && baseline.Count > 0)
                {
                    List<BaselineRow> before = list.Select(r => baseline.ContainsKey(r.Setup + "|" + r.Target) ? baseline[r.Setup + "|" + r.Target] : null).Where(b => b != null).ToList();
                    List<BaselineRow> gated = before.Where(b => b.HardPass.HasValue).ToList();
                    if (before.Count > 0)
                        delta = " | " + (gated.Count > 0 ? Pct(100.0 * gated.Count(b => b.HardPass.Value) / gated.Count) + " gates / " : string.Empty) + Pct(Mean(before.Select(b => b.Coverage))) + " / " + Pct(Mean(before.Select(b => b.Ergonomics))) + " / " + Pct(Mean(before.Select(b => b.Score)));
                    else
                        delta = " | -";
                }
                sb.AppendLine("| " + title + " | " + list.Count.ToString(CultureInfo.InvariantCulture) + " | " + Pct(HardPassRate(list)) + " | " + Pct(WMean(list, r => r.Coverage)) + " | " + Pct(WMean(list, r => r.Ergonomics)) +
                              " | " + Pct(WMean(list, r => r.Score)) + " | " + Pct(list.Count == 0 ? 0 : list.Min(r => r.Coverage)) + " | " + list.Count(r => r.Coverage >= 99.9).ToString(CultureInfo.InvariantCulture) + delta + " |");
            };
            string head = "| Rows | n | Hard gates passed % | Coverage % | Ergonomics % | Score | Lowest coverage | Rows at 100 % coverage" + (baseline != null && baseline.Count > 0 ? " | Before (gates / cov / ergo / score)" : string.Empty) + " |";
            sb.AppendLine(head);
            sb.AppendLine(Regex.Replace(head, @"[^|]+", "---"));
            line("All (popular + random)", rows.Where(r => r.SetupKind != "regression"));
            line("Popular setups", rows.Where(r => r.SetupKind == "popular"));
            line("Random setups (seed " + result.Seed.ToString(CultureInfo.InvariantCulture) + ")", rows.Where(r => r.SetupKind == "random"));
            if (rows.Any(r => r.SetupKind == "regression"))
                line("Regression set", rows.Where(r => r.SetupKind == "regression"));
            line("DCS families", rows.Where(r => r.Target.StartsWith("DCS:", StringComparison.Ordinal)));
            line("Other games", rows.Where(r => !r.Target.StartsWith("DCS:", StringComparison.Ordinal)));
            sb.AppendLine();

            sb.AppendLine("### Per target");
            sb.AppendLine();
            sb.AppendLine(head.Replace("| Rows |", "| Target |"));
            sb.AppendLine(Regex.Replace(head, @"[^|]+", "---"));
            foreach (EvalTarget target in result.Targets)
                line(target.Id, rows.Where(r => r.Target == target.Id));
            sb.AppendLine();

            sb.AppendLine("### Per setup");
            sb.AppendLine();
            sb.AppendLine(head.Replace("| Rows |", "| Setup |"));
            sb.AppendLine(Regex.Replace(head, @"[^|]+", "---"));
            foreach (EvalSetup setup in result.Setups)
                line(setup.Id, rows.Where(r => r.Setup == setup.Id));
            sb.AppendLine();

            sb.AppendLine("### Operations missing most often");
            sb.AppendLine();
            sb.AppendLine("| Operation | Rows where it applies | Missing | Partly done | Most common reason |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (EvalOp op in Ops)
            {
                List<EvalGap> gaps = rows.SelectMany(r => r.Gaps).Where(g => g.Op == op.Id).ToList();
                int applies = rows.Count(r => r.OpsDone.Contains(op.Id) || r.Gaps.Any(g => g.Op == op.Id));
                if (applies == 0)
                    continue;
                string reason = gaps.GroupBy(g => ShortReason(g.Why)).OrderByDescending(g => g.Count()).Select(g => g.Key + " (" + g.Count().ToString(CultureInfo.InvariantCulture) + "x)").FirstOrDefault() ?? string.Empty;
                sb.AppendLine("| " + op.Title + " | " + applies.ToString(CultureInfo.InvariantCulture) + " | " + gaps.Count(g => !g.Partial).ToString(CultureInfo.InvariantCulture) + " | " +
                              gaps.Count(g => g.Partial).ToString(CultureInfo.InvariantCulture) + " | " + Cell(reason) + " |");
            }
            sb.AppendLine();

            sb.AppendLine("### Hand rules broken most often");
            sb.AppendLine();
            sb.AppendLine("| Rule | Rows |");
            sb.AppendLine("|---|---|");
            foreach (IGrouping<string, string> group in rows.SelectMany(r => r.Violations).GroupBy(v => v.Split(':')[0]).OrderByDescending(g => g.Count()))
                sb.AppendLine("| " + group.Key + " | " + group.Count().ToString(CultureInfo.InvariantCulture) + " |");
            sb.AppendLine();

            sb.AppendLine("### Worst rows");
            sb.AppendLine();
            sb.AppendLine("| Setup | Target | Coverage % | Ergonomics % | Score | Gaps | Rule problems |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (EvalRow row in rows.OrderBy(r => r.Score).ThenBy(r => r.Setup, StringComparer.Ordinal).ThenBy(r => r.Target, StringComparer.Ordinal).Take(40))
                sb.AppendLine(RowLine(row, true));
            sb.AppendLine();

            EssentialsMarkdown(sb, result);
            GatesMarkdown(sb, result);
            if (result.NotApplicable.Count > 0)
            {
                sb.AppendLine("Rows left out (not applicable): " + result.NotApplicable.Count.ToString(CultureInfo.InvariantCulture) + " (" + Cell(string.Join("; ", result.NotApplicable.Take(6))) + (result.NotApplicable.Count > 6 ? " ..." : string.Empty) + ")");
                sb.AppendLine();
            }
            if (result.UnusedInputs.Count > 0)
            {
                // (L29: a grip hat no plan of a setup ever uses - the CH Fighterstick's hats 2-4 - passes every gate unseen)
                sb.AppendLine("## Grip inputs no plan uses");
                sb.AppendLine();
                sb.AppendLine("Per setup, the grip hats (all four directions) and grip buttons of the hands' devices that no setup-alone plan of the run binds. " +
                              "A whole hat that stays empty while functions are missing is a finding (a catalog description taken for a printed label, a set that never fits).");
                sb.AppendLine();
                sb.AppendLine("| Setup | Unused grip hats | Unused grip buttons | Hats |");
                sb.AppendLine("|---|---|---|---|");
                foreach (KeyValuePair<string, List<string>> pair in result.UnusedInputs.OrderByDescending(p => p.Value.Count(u => u.StartsWith("hat ", StringComparison.Ordinal))).ThenBy(p => p.Key, StringComparer.Ordinal))
                    sb.AppendLine("| " + pair.Key + " | " + pair.Value.Count(u => u.StartsWith("hat ", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture) + " | " +
                                  pair.Value.Count(u => !u.StartsWith("hat ", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture) + " | " +
                                  Cell(string.Join("; ", pair.Value.Where(u => u.StartsWith("hat ", StringComparison.Ordinal)).Take(6))) + " |");
                sb.AppendLine();
            }
            if (result.Pipeline)
                PipelineMarkdown(sb, result);
            if (result.Persona != null)
            {
                List<string> personaFailures = PersonaFailures(result.Persona);
                sb.AppendLine("## Novice case: IL-2 Great Battles with a WinWing URSA MINOR HOTAS");
                sb.AppendLine();
                sb.AppendLine("Reported on its own in [persona.md](persona.md) (" + (personaFailures.Count == 0 ? "passed" : "failed: " + string.Join("; ", personaFailures)) + "): coverage " +
                              Pct(Mean(result.Persona.Rows.Select(r => r.Coverage))) + " % after the setup, " + Pct(Mean(result.Persona.PipelineRows.Select(r => r.Coverage))) + " % after Auto Group and Fill all.");
                sb.AppendLine();
            }

            sb.AppendLine("## Setups");
            sb.AppendLine();
            sb.AppendLine("| Setup | Kind | Devices (catalog ids) | Roles JoyPro proposed |");
            sb.AppendLine("|---|---|---|---|");
            foreach (EvalSetup setup in result.Setups)
                sb.AppendLine("| [" + setup.Id + "](setups/" + Safe(setup.Id) + ".md) | " + setup.Kind + " | " + Cell(string.Join(", ", setup.Devices.Select(d => d.Catalog + (d.Role.Length > 0 ? " (" + d.Role + ")" : string.Empty)))) +
                              " | " + Cell(string.Join("; ", result.Roles.ContainsKey(setup.Id) ? result.Roles[setup.Id] : new List<string>())) + " |");
            sb.AppendLine();

            sb.AppendLine("## Every row");
            sb.AppendLine();
            sb.AppendLine("| Setup | Target | Coverage % | Ergonomics % | Score | Quality % | Placed | Modifiers | Gaps | Rule problems |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (EvalRow row in rows)
                sb.AppendLine(RowLine(row, false));
            sb.AppendLine();
            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("## Warnings");
                sb.AppendLine();
                foreach (string warning in result.Warnings.Distinct().Take(80))
                    sb.AppendLine("- " + Cell(warning));
            }
            return sb.ToString();
        }

        // The whole path: setup + Auto Group + Fill all, per setup one profile.
        private static void PipelineMarkdown(StringBuilder sb, EvalResult result)
        {
            List<EvalRow> rows = result.PipelineRows;
            sb.AppendLine("## After Auto Group and Fill all");
            sb.AppendLine();
            sb.AppendLine("Per setup one profile, as a user who owns every game and DCS family above would make it: the setup for all games at once (wizard " +
                          "defaults, the families' aircraft as owned modules), then **Auto Group** (`relations auto-group`, preset all, with substitutes, the owned " +
                          "aircraft and games), then **Suggest missing** for every relation with every suggestion of medium confidence and up taken (**Fill all**, " +
                          "`relations suggest --missing` + `--add`). Each target is then scored from the profile's binds on the setup's devices with the same operations " +
                          "and hand rules, plus the rule **No wrong-kind matches**: a command of the scored aircraft in a relation whose function (the function most of " +
                          "its commands are, by the knowledge map) is another, unrelated one - a brightness knob in a slew relation, a wheel brake in a throttle relation.");
            sb.AppendLine();
            sb.AppendLine("| Rows | n | Coverage % | Ergonomics % | Score | Lowest coverage | Wrong-kind commands | Setup alone (cov / ergo / score) |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            Action<string, Func<EvalRow, bool>> line = (title, filter) =>
            {
                List<EvalRow> list = rows.Where(filter).ToList();
                List<EvalRow> alone = result.Rows.Where(filter).ToList();
                sb.AppendLine("| " + title + " | " + list.Count.ToString(CultureInfo.InvariantCulture) + " | " + Pct(Mean(list.Select(r => r.Coverage))) + " | " + Pct(Mean(list.Select(r => r.Ergonomics))) + " | " +
                              Pct(Mean(list.Select(r => r.Score))) + " | " + Pct(list.Count == 0 ? 0 : list.Min(r => r.Coverage)) + " | " + list.Sum(r => r.Wrong.Count).ToString(CultureInfo.InvariantCulture) + " | " +
                              Pct(Mean(alone.Select(r => r.Coverage))) + " / " + Pct(Mean(alone.Select(r => r.Ergonomics))) + " / " + Pct(Mean(alone.Select(r => r.Score))) + " |");
            };
            line("All", r => true);
            line("Popular setups", r => r.SetupKind == "popular");
            line("Random setups", r => r.SetupKind == "random");
            line("DCS families", r => r.Target.StartsWith("DCS:", StringComparison.Ordinal));
            line("Other games", r => !r.Target.StartsWith("DCS:", StringComparison.Ordinal));
            sb.AppendLine();
            sb.AppendLine("### Per target (after Auto Group and Fill all)");
            sb.AppendLine();
            sb.AppendLine("| Target | n | Coverage % | Ergonomics % | Score | Lowest coverage | Wrong-kind commands | Setup alone (cov / ergo / score) |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (EvalTarget target in result.Targets)
                line(target.Id, r => r.Target == target.Id);
            sb.AppendLine();
            sb.AppendLine("### Essentials lost on the quick path (" + result.StageLosses.Count.ToString(CultureInfo.InvariantCulture) + ")");
            sb.AppendLine();
            sb.AppendLine("An essential gate (pitch, roll, yaw, throttle, gun, weapon release, countermeasures, wheel brakes) that passed in the step before " +
                          "and fails now: the setup alone -> the all-games plan (the quick path makes one plan for every chosen game), that plan -> the profile " +
                          "after Auto Group + Fill all. Zero tolerance (`--max-all-games-lost`, `--max-pipeline-lost`).");
            sb.AppendLine();
            foreach (string lost in result.StageLosses.Take(300))
                sb.AppendLine("- " + Cell(lost));
            if (result.KnownLosses.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Known limitation, not counted (" + result.KnownLosses.Count.ToString(CultureInfo.InvariantCulture) + "): after Auto Group the MSFS throttle may sit on a slider or rotary of the throttle.");
                sb.AppendLine();
                foreach (string lost in result.KnownLosses.Take(100))
                    sb.AppendLine("- " + Cell(lost));
            }
            sb.AppendLine();
            sb.AppendLine("### What each profile got");
            sb.AppendLine();
            foreach (EvalSetup setup in result.Setups)
                sb.AppendLine("- **" + setup.Id + "**: " + (result.PipelineNotes.ContainsKey(setup.Id) ? Cell(result.PipelineNotes[setup.Id]) : "not run"));
            sb.AppendLine();
            sb.AppendLine("### Operations missing most often (after Auto Group and Fill all)");
            sb.AppendLine();
            sb.AppendLine("| Operation | Rows where it applies | Missing | Partly done |");
            sb.AppendLine("|---|---|---|---|");
            foreach (EvalOp op in Ops)
            {
                List<EvalGap> gaps = rows.SelectMany(r => r.Gaps).Where(g => g.Op == op.Id).ToList();
                int applies = rows.Count(r => r.OpsDone.Contains(op.Id) || r.Gaps.Any(g => g.Op == op.Id));
                if (applies > 0)
                    sb.AppendLine("| " + op.Title + " | " + applies.ToString(CultureInfo.InvariantCulture) + " | " + gaps.Count(g => !g.Partial).ToString(CultureInfo.InvariantCulture) + " | " + gaps.Count(g => g.Partial).ToString(CultureInfo.InvariantCulture) + " |");
            }
            sb.AppendLine();
            sb.AppendLine("### Wrong-kind commands in relations (" + rows.Sum(r => r.Wrong.Count).ToString(CultureInfo.InvariantCulture) + ")");
            sb.AppendLine();
            sb.AppendLine("Grouped over all setups: relation (its function): command (the command's function by the knowledge map) - targets.");
            sb.AppendLine();
            foreach (IGrouping<string, string> group in rows.SelectMany(r => r.Wrong.Select(w => new { w, r.Target })).GroupBy(x => x.w, x => x.Target).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(150))
                sb.AppendLine("- " + Cell(group.Key) + " - " + string.Join(", ", group.Distinct().Take(6)) + " (" + group.Count().ToString(CultureInfo.InvariantCulture) + "x)");
            sb.AppendLine();
            sb.AppendLine("### Wrong suggestions taken by Fill all (" + result.WrongSuggestions.Count.ToString(CultureInfo.InvariantCulture) + ")");
            sb.AppendLine();
            sb.AppendLine("A suggestion whose command the knowledge map knows as a function unrelated to the relation's own. Same text grouped over setups.");
            sb.AppendLine();
            foreach (IGrouping<string, string> group in result.WrongSuggestions.GroupBy(w => w.Substring(w.IndexOf(": ", StringComparison.Ordinal) + 2), w => w.Substring(0, w.IndexOf(": ", StringComparison.Ordinal)))
                                                                      .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(200))
                sb.AppendLine("- " + Cell(group.Key) + " (" + group.Distinct().Count().ToString(CultureInfo.InvariantCulture) + " setups)");
            sb.AppendLine();
            sb.AppendLine("### Suggestions taken that are another function of the same kind (" + result.OtherSuggestions.Count.ToString(CultureInfo.InvariantCulture) + ")");
            sb.AppendLine();
            sb.AppendLine("Not counted as wrong (the same category of the knowledge map, e.g. a flood light in an instrument light relation); listed so they can be checked.");
            sb.AppendLine();
            foreach (IGrouping<string, string> group in result.OtherSuggestions.GroupBy(w => w.Substring(w.IndexOf(": ", StringComparison.Ordinal) + 2), w => w.Substring(0, w.IndexOf(": ", StringComparison.Ordinal)))
                                                                      .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(120))
                sb.AppendLine("- " + Cell(group.Key) + " (" + group.Distinct().Count().ToString(CultureInfo.InvariantCulture) + " setups)");
            sb.AppendLine();
            sb.AppendLine("### Catalog substitutes taken (" + result.SubstituteSuggestions.Count.ToString(CultureInfo.InvariantCulture) + ")");
            sb.AppendLine();
            sb.AppendLine("Another function on purpose: the function catalog's closest function where the aircraft has none of its own (status `substitute` in " +
                          "`DB/Functions`, Auto Group's \"with substitutes\"). Not counted as wrong; listed so they can be checked.");
            sb.AppendLine();
            foreach (IGrouping<string, string> group in result.SubstituteSuggestions.GroupBy(w => w.Substring(w.IndexOf(": ", StringComparison.Ordinal) + 2), w => w.Substring(0, w.IndexOf(": ", StringComparison.Ordinal)))
                                                                      .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(80))
                sb.AppendLine("- " + Cell(group.Key) + " (" + group.Distinct().Count().ToString(CultureInfo.InvariantCulture) + " setups)");
            sb.AppendLine();
            sb.AppendLine("### Every row (after Auto Group and Fill all)");
            sb.AppendLine();
            sb.AppendLine("| Setup | Target | Coverage % | Ergonomics % | Score | Quality % | Placed | Modifiers | Gaps | Rule problems |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (EvalRow row in rows)
                sb.AppendLine(RowLine(row, false));
            sb.AppendLine();
        }

        // The operations the novice case must have (IL-2 Great Battles): flight axes, engine, weapons, gear, flaps,
        // brakes, trim, looking around without head tracking, comms.
        internal static readonly string[] PersonaOps = { "pitch", "roll", "yaw", "throttle", "prop_pitch", "mixture", "fire", "release", "gear", "flaps", "wheel_brakes", "trim", "view", "comms" };
        // Without these the quick path fails (the smoke test's guard).
        internal static readonly string[] PersonaMust = { "pitch", "roll", "throttle", "fire" };

        // Every device of the setup is reported with a USB vendor id no catalog device has (the generic layout only).
        internal static bool UnknownMaker(EvalSetup setup)
        {
            if (setup == null || setup.Devices.Count == 0 || setup.Devices.Any(d => d.ReportVidPid.Length == 0))
                return false;
            DeviceCatalog catalog = Program.LoadReferenceCatalog();
            HashSet<string> vendors = new HashSet<string>(catalog.Devices.SelectMany(d => d.VidPids).Select(v => v.Split(':')[0].Trim()), StringComparer.OrdinalIgnoreCase);
            return setup.Devices.All(d => !vendors.Contains(d.ReportVidPid.Split(':')[0].Trim()));
        }

        internal static List<string> PersonaFailures(EvalResult persona)
        {
            List<string> failures = new List<string>();
            if (persona == null)
                return failures;
            foreach (EvalRow row in persona.Rows.Concat(persona.PipelineRows))
            {
                if (row.Error.Length > 0)
                {
                    failures.Add("novice case " + row.Setup + " (" + row.Stage + "): " + row.Error);
                    continue;
                }
                foreach (string op in PersonaMust.Where(o => !row.OpsDone.Contains(o)))
                    failures.Add("novice case " + row.Setup + " (" + row.Stage + "): " + op + " is not bound");
                // The fly-and-fight gates and the destructive check (judged on the real device for the unknown set).
                // (review 6 M5 / L31: no setup is exempted by its id. A setup whose devices report a USB vendor JoyPro does not
                // know at all has only the generic layout, which cannot know where the trigger, the grip buttons and the
                // hats are (button 1 is a base button on this stick, its three hats report as buttons); until the wizard asks
                // the user to press them (the capture step) the gates that need those positions are reported as a known
                // limit, not failed - by that property, for any such setup. Flying and nothing destructive are required.)
                EvalSetup of = persona.Setups.FirstOrDefault(s => s.Id == row.Setup);
                bool unknownMaker = of != null && UnknownMaker(of);
                foreach (EvalGate gate in row.Gates.Where(g => g.Status == "fail" &&
                                                               !(unknownMaker && (g.Gate == "fire" || g.Gate == "release" || g.Gate == "wheel_brakes" || g.Gate == "countermeasures" || g.Gate == "look" || g.Gate == "switches"))))
                    failures.Add("novice case " + row.Setup + " (" + row.Stage + "): gate " + gate.Gate + " failed: " + gate.Detail);
                foreach (string destructive in row.Destructive)
                    failures.Add("novice case " + row.Setup + " (" + row.Stage + "): destructive binding: " + destructive);
            }
            foreach (string setup in persona.Setups.Where(s => s.Id.StartsWith("ursa-unknown", StringComparison.Ordinal)).Select(s => s.Id))
            {
                List<string> roles = persona.Roles.ContainsKey(setup) ? persona.Roles[setup] : new List<string>();
                List<string> messages = persona.Messages.ContainsKey(setup) ? persona.Messages[setup] : new List<string>();
                if (!roles.Any(r => r.Contains("not recognised")))
                    failures.Add("novice case " + setup + ": the unknown devices were not set up generically");
                if (!messages.Any(m => m.StartsWith("JoyPro does not know the exact model", StringComparison.Ordinal)))
                    failures.Add("novice case " + setup + ": the setup does not say that it does not know the devices");
            }
            return failures;
        }

        internal static string PersonaMarkdown(EvalResult persona)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Novice case: IL-2 Great Battles with a WinWing URSA MINOR HOTAS");
            sb.AppendLine();
            sb.AppendLine("Someone new to flight sims, IL-2 Great Battles is their only game, a WinWing URSA MINOR stick with the matching throttle. At the " +
                          "first start they take the quick path: the setup for the games JoyPro found, with its defaults, then Apply and Export. The rows " +
                          "below are that setup (the wizard's defaults, IL-2 the only game) and the same profile after Auto Group and Fill all. " +
                          "`ursa-unknown` is the Fighter set reported with a USB id and name JoyPro does not know: it must still be set up (stick axes " +
                          "fly, button 1 fires, the throttle lever is the throttle) and the setup must say plainly that it does not know the devices.");
            sb.AppendLine();
            List<string> failures = PersonaFailures(persona);
            sb.AppendLine(failures.Count == 0 ? "**Result: passed** (every hard gate met in every row, no destructive binding; the unknown set is set up, named, and judged on the real device's controls)." : "**Result: failed**");
            foreach (string failure in failures)
                sb.AppendLine("- " + failure);
            sb.AppendLine();
            sb.AppendLine("| Setup | Stage | Hard gates | Coverage % | Ergonomics % | " + string.Join(" | ", PersonaOps.Select(o => Ops.First(x => x.Id == o).Title)) + " |");
            sb.AppendLine("|---|---|---|---|---|" + string.Concat(PersonaOps.Select(o => "---|")));
            foreach (EvalRow row in persona.Rows.Concat(persona.PipelineRows).OrderBy(r => r.Setup, StringComparer.Ordinal).ThenBy(r => r.Stage, StringComparer.Ordinal))
            {
                List<string> cells = new List<string>();
                foreach (string op in PersonaOps)
                {
                    EvalGap gap = row.Gaps.FirstOrDefault(g => g.Op == op);
                    cells.Add(row.OpsDone.Contains(op) ? "yes" : gap != null ? (gap.Partial ? "partly" : "**no**") : "n/a");
                }
                sb.AppendLine("| " + row.Setup + " | " + (row.Stage == "pipeline" ? "after Auto Group + Fill all" : "setup") + " | " + (row.HardPass ? "passed" : UnknownMaker(persona.Setups.FirstOrDefault(s => s.Id == row.Setup)) && row.Destructive.Count == 0 && row.Gates.Where(g => g.Status == "fail").All(g => g.Gate == "fire" || g.Gate == "release" || g.Gate == "wheel_brakes" || g.Gate == "countermeasures" || g.Gate == "look" || g.Gate == "switches") ? "known limit (generic layout: the capture step)" : "**failed**") + " | " + Pct(row.Coverage) + " | " + Pct(row.Ergonomics) + " | " + string.Join(" | ", cells) + " |");
            }
            sb.AppendLine();
            sb.AppendLine("`n/a`: IL-2 has no command of it (function catalog). `partly`: one part of it (gear up without gear down).");
            sb.AppendLine();
            foreach (EvalSetup setup in persona.Setups)
            {
                sb.AppendLine("## " + setup.Id + ": " + setup.Name);
                sb.AppendLine();
                sb.AppendLine("Devices as Windows reports them: `devices-" + Safe(setup.Id) + ".tsv`. Roles JoyPro proposed: " + string.Join("; ", persona.Roles.ContainsKey(setup.Id) ? persona.Roles[setup.Id] : new List<string>()));
                sb.AppendLine();
                sb.AppendLine("What the setup tells the user:");
                sb.AppendLine();
                foreach (string message in persona.Messages.ContainsKey(setup.Id) ? persona.Messages[setup.Id] : new List<string>())
                    sb.AppendLine("- " + message);
                sb.AppendLine();
                foreach (EvalRow row in persona.Rows.Concat(persona.PipelineRows).Where(r => r.Setup == setup.Id))
                {
                    sb.AppendLine("### " + (row.Stage == "pipeline" ? "After Auto Group and Fill all" : "Setup") + " - coverage " + Pct(row.Coverage) + " %, ergonomics " + Pct(row.Ergonomics) + " %");
                    sb.AppendLine();
                    if (row.Error.Length > 0)
                        sb.AppendLine("Error: " + row.Error);
                    foreach (EvalGap gap in row.Gaps)
                        sb.AppendLine("- Gap: **" + gap.Title + "**" + (gap.Partial ? " (partly)" : string.Empty) + " - " + gap.Why);
                    foreach (string violation in row.Violations)
                        sb.AppendLine("- Rule: " + violation);
                    foreach (EvalGate gate in row.Gates)
                        sb.AppendLine("- Gate " + gate.Gate + ": " + (gate.Status == "fail" ? "**FAIL**" : gate.Status) + (gate.Detail.Length > 0 ? " - " + gate.Detail : string.Empty));
                    foreach (string destructive in row.Destructive)
                        sb.AppendLine("- **Destructive:** " + destructive);
                    sb.AppendLine();
                    sb.AppendLine("What is bound where (function = device input (control \"label\") + modifier):");
                    sb.AppendLine();
                    foreach (string binding in row.Bindings)
                        sb.AppendLine("- " + binding);
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        private static string ShortReason(string why)
        {
            string text = Regex.Replace(why ?? string.Empty, @"^[^:]+:\s*", string.Empty);
            text = Regex.Replace(text, @"\(best: .*$", string.Empty);
            text = Regex.Replace(text, @"functions of (?!the )\S+", "functions of another aircraft");
            return text.Trim().Length > 120 ? text.Trim().Substring(0, 117) + "..." : text.Trim();
        }

        private static string RowLine(EvalRow row, bool shortForm)
        {
            string gaps = row.Error.Length > 0 ? "ERROR: " + row.Error : string.Join("; ", row.Gaps.Where(g => g.Counted).Select(g => g.Title + (g.Partial ? " (partly)" : string.Empty)));
            string rules = string.Join("; ", row.Violations.Select(v => shortForm ? v.Split(':')[0] : v));
            if (shortForm)
                return "| " + row.Setup + " | " + row.Target + " | " + Pct(row.Coverage) + " | " + Pct(row.Ergonomics) + " | " + Pct(row.Score) + " | " + Cell(gaps) + " | " + Cell(rules) + " |";
            return "| " + row.Setup + " | " + row.Target + " | " + Pct(row.Coverage) + " | " + Pct(row.Ergonomics) + " | " + Pct(row.Score) + " | " + Pct(row.Quality) + " | " +
                   row.Placed.ToString(CultureInfo.InvariantCulture) + "/" + row.Total.ToString(CultureInfo.InvariantCulture) + " | " + row.Modifiers.ToString(CultureInfo.InvariantCulture) +
                   (row.Forced > 0 ? " (" + row.Forced.ToString(CultureInfo.InvariantCulture) + " forced)" : string.Empty) + (row.PhaseLayers > 0 ? " +" + row.PhaseLayers.ToString(CultureInfo.InvariantCulture) + " phase" : string.Empty) +
                   " | " + Cell(gaps) + " | " + Cell(rules) + " |";
        }

        internal static string SetupMarkdown(EvalResult result, EvalSetup setup)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# " + setup.Id + ": " + setup.Name);
            sb.AppendLine();
            sb.AppendLine("Devices: " + string.Join(", ", setup.Devices.Select(d => "`" + d.Catalog + "`")));
            sb.AppendLine();
            sb.AppendLine("Roles: " + string.Join("; ", result.Roles.ContainsKey(setup.Id) ? result.Roles[setup.Id] : new List<string>()));
            sb.AppendLine();
            if (result.PipelineNotes.ContainsKey(setup.Id))
            {
                sb.AppendLine("After Auto Group and Fill all: " + result.PipelineNotes[setup.Id]);
                sb.AppendLine();
            }
            foreach (EvalRow row in result.Rows.Where(r => r.Setup == setup.Id).Concat(result.TypicalRows.Where(r => r.Setup == setup.Id)).Concat(result.PadRows.Where(r => r.Setup == setup.Id))
                         .Concat(result.PipelineRows.Where(r => r.Setup == setup.Id)))
            {
                sb.AppendLine("## " + (row.Stage == "pipeline" ? "After Auto Group and Fill all: " : row.Stage == "typical" ? "Typical DCS hangar (20 modules, one plan): " : string.Empty) + row.Target + " - coverage " + Pct(row.Coverage) + " %, ergonomics " + Pct(row.Ergonomics) + " %, score " + Pct(row.Score));
                sb.AppendLine();
                if (row.Error.Length > 0)
                    sb.AppendLine("Error: " + row.Error);
                sb.AppendLine("Done: " + string.Join(", ", row.OpsDone));
                sb.AppendLine();
                sb.AppendLine("Hard gates: " + (row.HardPass ? "**passed**" : "**failed**") + " - " + string.Join(", ", row.Gates.Select(g => g.Gate + " " + (g.Status == "fail" ? "FAIL (" + g.Detail + ")" : g.Status))));
                if (row.Shifts.Count > 0)
                    sb.AppendLine("Shift buttons: " + string.Join("; ", row.Shifts));
                foreach (string destructive in row.Destructive)
                    sb.AppendLine("- Destructive: " + destructive);
                sb.AppendLine();
                foreach (EvalGap gap in row.Gaps)
                    sb.AppendLine("- Gap: **" + gap.Title + "**" + (gap.Partial ? " (partly)" : string.Empty) + " - " + gap.Why);
                foreach (string violation in row.Violations)
                    sb.AppendLine("- Rule: " + violation);
                sb.AppendLine();
                sb.AppendLine("<details><summary>" + row.Bindings.Count.ToString(CultureInfo.InvariantCulture) + " bindings of " + row.Target + "</summary>");
                sb.AppendLine();
                foreach (string binding in row.Bindings)
                    sb.AppendLine("- " + binding);
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string EngineVersionText()
        {
            try
            {
                return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            }
            catch
            {
                return "?";
            }
        }

        internal static string Csv(EvalResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("setup,kind,target,class,required,done,coverage,ergonomics,score,quality,placed,total,modifiers,forced,gaps,violations,hardpass,gatefails,destructive");
            foreach (EvalRow r in result.Rows)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    r.Setup, r.SetupKind, r.Target, r.Class, r.Required.ToString(CultureInfo.InvariantCulture), r.Done.ToString(CultureInfo.InvariantCulture), Pct(r.Coverage), Pct(r.Ergonomics), Pct(r.Score),
                    Pct(r.Quality), r.Placed.ToString(CultureInfo.InvariantCulture), r.Total.ToString(CultureInfo.InvariantCulture), r.Modifiers.ToString(CultureInfo.InvariantCulture), r.Forced.ToString(CultureInfo.InvariantCulture),
                    "\"" + string.Join("; ", r.Gaps.Select(g => g.Op + (g.Partial ? "(partly)" : string.Empty))).Replace("\"", "'") + "\"",
                    "\"" + string.Join("; ", r.Violations.Select(v => v.Split(':')[0])).Replace("\"", "'") + "\"",
                    r.HardPass ? "1" : "0", "\"" + string.Join("; ", r.Gates.Where(g => g.Status == "fail").Select(g => g.Gate)).Replace("\"", "'") + "\"", r.Destructive.Count.ToString(CultureInfo.InvariantCulture)
                }));
            }
            return sb.ToString();
        }
    }

    internal static partial class Program
    {
        internal const string SetupEvalUsage =
            "engine setup eval [--random N] [--seed N] [--setups popular|random|regression|<ids>] [--regression-file <file>] [--no-regression] [--games <ids>|none] [--families <aircraft>|none] [--no-stress]\n" +
            "    [--out-dir <folder>] [--baseline <results.json>] [--no-pipeline] [--no-persona] [--no-typical] [--no-pad-alone] [--head-tracking yes|no] [--placement same-button|real-aircraft|0..1] [--placement-sweep 0,0.25,0.5,0.75,1]\n" +
            "  thresholds (exit code 1 when one fails): [--min-coverage <pct>] [--min-ergonomics <pct>] [--min-popular-coverage <pct>] [--min-row-coverage <pct>] [--min-flight <pct>] [--min-hard-pass <pct>]\n" +
            "    [--min-popular-hard-pass <pct>] [--max-destructive N] [--popular-zero-gates <gates>] [--zero-gates <gates> (every row)] [--min-typical-hard-pass <pct>] [--min-pad-hard-pass <pct>] [--min-regression-hard-pass <pct>]\n" +
            "    [--min-pipeline-coverage <pct>] [--max-wrong N] [--min-pipeline-hard-pass <pct>] [--max-pipeline-destructive N] [--max-pipeline-lost N (default 0)] [--max-all-games-lost N (default 0)]\n" +
            "    [--pipeline-zero-gates <gates>] [--min-all-games-hard-pass <pct>] [--gate-baseline <json>] [--write-gate-baseline <json>]\n" +
            "  docs/auto-setup-eval.md";

        // engine setup eval [--random N] [--seed N] [--games <ids>|none] [--families <aircraft>|none] [--no-stress] [--setups popular|random|<ids>]
        //     [--out-dir <folder>] [--baseline <results.json>] [--min-coverage <pct>] [--min-ergonomics <pct>] [--min-row-coverage <pct>]
        //     [--min-popular-coverage <pct>] [--min-flight <pct>] [--min-hard-pass <pct>] [--min-popular-hard-pass <pct>] [--max-destructive N]
        private static int RunSetupEval(List<string> args, string outPath, bool textOutput)
        {
            // (review 6 m13: setup eval --help lists the options instead of "Unknown setup eval argument(s)")
            if (args.Any(a => a == "--help" || a == "-h" || a == "/?"))
            {
                Console.WriteLine(SetupEvalUsage);
                return 0;
            }
            int random = PopOptionInt(args, "--random", 20);
            // A fresh seed every run (the random setups differ each time); --seed N repeats a run.
            string seedText = PopOptionValue(args, "--seed");
            int seed;
            if (seedText == null || !int.TryParse(seedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
                seed = EngineSetupEval.FreshSeed();
            string regressionText = PopOptionValue(args, "--regression-file");
            bool noRegression = PopFlag(args, "--no-regression");
            double minRegressionHard = PopOptionDouble(args, "--min-regression-hard-pass");
            string gamesText = PopOptionValue(args, "--games");
            string familiesText = PopOptionValue(args, "--families");
            bool noStress = PopFlag(args, "--no-stress");
            string setupsText = PopOptionValue(args, "--setups");
            string outDir = PopOptionValue(args, "--out-dir");
            string baselineFile = PopOptionValue(args, "--baseline");
            double minCoverage = PopOptionDouble(args, "--min-coverage");
            double minErgonomics = PopOptionDouble(args, "--min-ergonomics");
            double minRow = PopOptionDouble(args, "--min-row-coverage");
            double minPopular = PopOptionDouble(args, "--min-popular-coverage");
            double minFlight = PopOptionDouble(args, "--min-flight");
            bool noPipeline = PopFlag(args, "--no-pipeline");
            bool noPersona = PopFlag(args, "--no-persona");
            double minPipeline = PopOptionDouble(args, "--min-pipeline-coverage");
            string maxWrongText = PopOptionValue(args, "--max-wrong");
            double minHard = PopOptionDouble(args, "--min-hard-pass");
            double minPopularHard = PopOptionDouble(args, "--min-popular-hard-pass");
            string maxDestructiveText = PopOptionValue(args, "--max-destructive");
            string zeroGatesText = PopOptionValue(args, "--popular-zero-gates");
            // (review 7 L35 / L36: zero tolerance on EVERY row - popular, random, regression, typical, pad - for the survival
            // gates: a regression setup that never got fixed fails the run too)
            string allZeroGatesText = PopOptionValue(args, "--zero-gates");
            double minPipelineHard = PopOptionDouble(args, "--min-pipeline-hard-pass");
            string maxPipelineDestructiveText = PopOptionValue(args, "--max-pipeline-destructive");
            // Review 6 B9 / B6 (L27, L34): the quick path must be lossless for the essentials. Losses of an essential gate
            // from the setup alone to the all-games plan and from that plan to the profile after Auto Group + Fill all fail
            // the run (zero tolerance unless a number is given); --pipeline-zero-gates: popular rows after the pipeline and
            // in the all-games plan may not fail these gates at all; --min-all-games-hard-pass: the all-games plan's rate.
            string maxPipelineLostText = PopOptionValue(args, "--max-pipeline-lost");
            string maxAllGamesLostText = PopOptionValue(args, "--max-all-games-lost");
            string pipelineZeroGatesText = PopOptionValue(args, "--pipeline-zero-gates");
            double minAllGamesHard = PopOptionDouble(args, "--min-all-games-hard-pass");
            double minTypicalHard = PopOptionDouble(args, "--min-typical-hard-pass");
            double minPadHard = PopOptionDouble(args, "--min-pad-hard-pass");
            // Where functions go (the planner's realism): --placement same-button | real-aircraft | <0..1> (--realism is the
            // old name); --placement-sweep 0,0.25,0.5,0.75,1 plans the popular setups' typical hangar at each position.
            string placementText = PopOptionValue(args, "--placement") ?? PopOptionValue(args, "--realism");
            string sweepText = PopOptionValue(args, "--placement-sweep");
            double placement = 0.5;
            if (placementText != null)
            {
                if (!EngineSetupWizard.TryPlacement(placementText, out placement))
                    return Fail("--placement takes same-button, real-aircraft or a number from 0 to 1, not " + placementText + ".");
            }
            EngineSetupEval.RealismValue = placement;
            // The wizard's head-tracking answer (--head-tracking yes|no, default no): with yes the look gate expects no
            // look-around buttons (only the view recenter) in the sims that support head tracking.
            string headTrackingText = PopOptionValue(args, "--head-tracking");
            bool headTracking = false;
            if (headTrackingText != null && !EngineSetupWizard.TryHeadTracking(headTrackingText, out headTracking))
                return Fail("--head-tracking takes yes or no, not " + headTrackingText + ".");
            EngineSetupEval.HeadTrackingValue = headTracking;
            List<double> sweep = new List<double>();
            foreach (string part in SplitList(sweepText))
            {
                double value;
                if (!EngineSetupWizard.TryPlacement(part, out value))
                    return Fail("--placement-sweep takes positions from 0 to 1, not " + part + ".");
                sweep.Add(value);
            }
            // Per-gate non-regression (docs/auto-setup-eval.md#thresholds): the popular setups' rows per gate and per in-flight
            // essential that pass, against a stored baseline; --write-gate-baseline stores this run's counts.
            string gateBaselineText = PopOptionValue(args, "--gate-baseline");
            string writeGateBaselineText = PopOptionValue(args, "--write-gate-baseline");
            EngineSetupEval.Typical = !PopFlag(args, "--no-typical");
            EngineSetupEval.PadAlone = !PopFlag(args, "--no-pad-alone");
            if (args.Count > 0)
                return Fail("Unknown setup eval argument(s): " + string.Join(" ", args));

            List<string> warnings = new List<string>();
            List<EngineSetupEval.EvalSetup> setups = new List<EngineSetupEval.EvalSetup>();
            List<string> wantedSetups = SplitList(setupsText);
            if (wantedSetups.Count == 0 || wantedSetups.Contains("popular", StringComparer.OrdinalIgnoreCase) || wantedSetups.Any(w => !w.StartsWith("random", StringComparison.OrdinalIgnoreCase)))
                setups.AddRange(EngineSetupEval.PopularSetups());
            if (random > 0)
                setups.AddRange(EngineSetupEval.RandomSetups(random, seed, warnings));
            // The regression set: random setups of earlier runs that failed a core gate or bound something destructive.
            string regressionFile = noRegression ? null : !string.IsNullOrWhiteSpace(regressionText) ? NormalizePath(regressionText) : EngineSetupEval.FindRegressionFile();
            List<EngineSetupEval.EvalSetup> regressionSetups = EngineSetupEval.LoadRegression(regressionFile, warnings);
            setups.AddRange(regressionSetups);
            if (wantedSetups.Count > 0 && !(wantedSetups.Count == 1 && wantedSetups[0].Equals("all", StringComparison.OrdinalIgnoreCase)))
            {
                setups = setups.Where(s => wantedSetups.Contains(s.Id, StringComparer.OrdinalIgnoreCase) ||
                                           (wantedSetups.Contains("popular", StringComparer.OrdinalIgnoreCase) && s.Kind == "popular") ||
                                           (wantedSetups.Contains("random", StringComparer.OrdinalIgnoreCase) && s.Kind == "random") ||
                                           (wantedSetups.Contains("regression", StringComparer.OrdinalIgnoreCase) && s.Kind == "regression")).ToList();
            }
            if (setups.Count == 0)
                return Fail("setup eval: no setup matches --setups " + setupsText + ".");
            List<string> games = gamesText == null ? null : gamesText.Equals("none", StringComparison.OrdinalIgnoreCase) ? new List<string>() : SplitList(gamesText);
            List<string> families = familiesText == null ? null : familiesText.Equals("none", StringComparison.OrdinalIgnoreCase) ? new List<string>() : SplitList(familiesText);
            List<EngineSetupEval.EvalTarget> targets = EngineSetupEval.Targets(games, families, !noStress && (families == null || families.Count > 0));
            if (targets.Count == 0)
                return Fail("setup eval: no target (--games / --families).");

            string folder = NormalizePath(string.IsNullOrWhiteSpace(outDir) ? Path.Combine(Path.GetTempPath(), "joypro-autosetup-eval-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)) : outDir);
            Directory.CreateDirectory(folder);
            EngineSetupEval.EvalResult result = EngineSetupEval.Run(setups, targets, folder, seed, line => { if (textOutput) Console.Error.WriteLine(line); }, !noPipeline);
            result.Warnings.InsertRange(0, warnings);
            if (!noPersona)
            {
                List<EngineSetupEval.EvalTarget> personaTargets = EngineSetupEval.Targets(EngineSetupEval.PersonaGames.ToList(), new List<string>(), false);
                result.Persona = EngineSetupEval.Run(EngineSetupEval.PersonaSetups(), personaTargets, folder, seed, line => { if (textOutput) Console.Error.WriteLine(line); }, !noPipeline);
            }

            Dictionary<string, EngineSetupEval.BaselineRow> baseline = null;
            if (!string.IsNullOrWhiteSpace(baselineFile))
            {
                if (File.Exists(NormalizePath(baselineFile)))
                    baseline = EngineSetupEval.ReadBaseline(NormalizePath(baselineFile), result.Warnings);
                else
                    result.Warnings.Add("Baseline not found: " + baselineFile);
            }

            // Thresholds (the smoke test's regression guard).
            List<string> failures = new List<string>();
            // (the regression set is judged by itself below: it holds the hardest setups on purpose)
            List<EngineSetupEval.EvalRow> rows = result.Rows.Where(r => r.SetupKind != "regression").ToList();
            List<EngineSetupEval.EvalRow> regressionRows = result.Rows.Where(r => r.SetupKind == "regression").ToList();
            Func<IEnumerable<EngineSetupEval.EvalRow>, Func<EngineSetupEval.EvalRow, double>, double> wmean = EngineSetupEval.WMean;
            if (minCoverage > 0 && wmean(rows, r => r.Coverage) < minCoverage)
                failures.Add("mean coverage " + EngineSetupEval.Pct(wmean(rows, r => r.Coverage)) + " % < " + EngineSetupEval.Pct(minCoverage) + " %");
            if (minErgonomics > 0 && wmean(rows, r => r.Ergonomics) < minErgonomics)
                failures.Add("mean ergonomics " + EngineSetupEval.Pct(wmean(rows, r => r.Ergonomics)) + " % < " + EngineSetupEval.Pct(minErgonomics) + " %");
            if (minPopular > 0 && wmean(rows.Where(r => r.SetupKind == "popular"), r => r.Coverage) < minPopular)
                failures.Add("mean coverage of the popular setups " + EngineSetupEval.Pct(wmean(rows.Where(r => r.SetupKind == "popular"), r => r.Coverage)) + " % < " + EngineSetupEval.Pct(minPopular) + " %");
            if (minHard > 0 && EngineSetupEval.HardPassRate(rows) < minHard)
                failures.Add("hard gates passed in " + EngineSetupEval.Pct(EngineSetupEval.HardPassRate(rows)) + " % of the rows < " + EngineSetupEval.Pct(minHard) + " %");
            if (minPopularHard > 0 && EngineSetupEval.HardPassRate(rows.Where(r => r.SetupKind == "popular")) < minPopularHard)
                failures.Add("hard gates passed in " + EngineSetupEval.Pct(EngineSetupEval.HardPassRate(rows.Where(r => r.SetupKind == "popular"))) + " % of the popular setups' rows < " + EngineSetupEval.Pct(minPopularHard) + " %");
            int maxDestructive;
            if (maxDestructiveText != null && int.TryParse(maxDestructiveText, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxDestructive) && rows.Sum(r => r.Destructive.Count) > maxDestructive)
                failures.Add("destructive bindings " + rows.Sum(r => r.Destructive.Count).ToString(CultureInfo.InvariantCulture) + " > " + maxDestructive.ToString(CultureInfo.InvariantCulture) + ": " +
                             string.Join("; ", rows.Where(r => r.Destructive.Count > 0).Take(5).Select(r => EngineSetupEval.Who(result, r.Setup) + " x " + r.Target + ": " + r.Destructive[0])));
            foreach (EngineSetupEval.EvalRow row in result.Rows.Where(r => r.Error.Length > 0))
                failures.Add(EngineSetupEval.Who(result, row.Setup) + " x " + row.Target + ": " + row.Error);
            if (minRow > 0)
            {
                foreach (EngineSetupEval.EvalRow row in rows.Where(r => r.Error.Length == 0 && r.Coverage < minRow))
                    failures.Add(row.Setup + " x " + row.Target + ": coverage " + EngineSetupEval.Pct(row.Coverage) + " % < " + EngineSetupEval.Pct(minRow) + " % (" + string.Join(", ", row.Gaps.Select(g => g.Op)) + ")");
            }
            if (minFlight > 0)
            {
                // The flight axes of the popular setups: pitch, roll and throttle must always be there.
                int done = 0, total = 0;
                foreach (EngineSetupEval.EvalRow row in rows.Where(r => r.SetupKind == "popular" && r.Error.Length == 0))
                {
                    foreach (string op in new[] { "pitch", "roll", "throttle" })
                    {
                        // (review 7 M10: a throttle the setup has no place for - only spring-return axes, said in the plan - is
                        // a hardware limit, not a miss)
                        if (op == "throttle" && row.Gates.Any(g => g.Gate == "throttle" && g.Status == "n/a" && g.Detail.StartsWith("hardware limit", StringComparison.Ordinal)))
                            continue;
                        if (row.OpsDone.Contains(op)) { done++; total++; }
                        else if (row.Gaps.Any(g => g.Op == op)) total++;
                    }
                }
                double pct = total == 0 ? 100 : 100.0 * done / total;
                if (pct < minFlight)
                    failures.Add("pitch / roll / throttle of the popular setups " + EngineSetupEval.Pct(pct) + " % < " + EngineSetupEval.Pct(minFlight) + " %");
            }

            // Zero tolerance on the popular setups' rows (setup alone and the typical DCS hangar): no popular setup may lose
            // the gun, the brakes or the throttle (--popular-zero-gates fire,wheel_brakes,throttle,destructive).
            List<string> zeroGates = SplitList(zeroGatesText);
            foreach (EngineSetupEval.EvalRow row in rows.Concat(result.TypicalRows).Where(r => r.SetupKind == "popular"))
            {
                // (the typical hangar: zero tolerance for destructive bindings; its gates count in --min-typical-hard-pass)
                foreach (EngineSetupEval.EvalGate gate in row.Gates.Where(g => g.Status == "fail" && zeroGates.Contains(g.Gate, StringComparer.OrdinalIgnoreCase) && row.Stage != "typical"))
                    failures.Add(row.Setup + " x " + row.Target + ": gate " + gate.Gate + " failed: " + gate.Detail);
                if (zeroGates.Contains("destructive", StringComparer.OrdinalIgnoreCase))
                    foreach (string destructive in row.Destructive)
                        failures.Add(row.Setup + " x " + row.Target + ": destructive: " + destructive);
            }
            List<string> allZeroGates = SplitList(allZeroGatesText);
            if (allZeroGates.Count > 0)
            {
                List<string> survivalFails = rows.Concat(result.TypicalRows).Concat(result.PadRows)
                    .SelectMany(row => row.Gates.Where(g => g.Status == "fail" && allZeroGates.Contains(g.Gate, StringComparer.OrdinalIgnoreCase))
                                                .Select(g => row.Setup + " x " + row.Target + ": gate " + g.Gate + " failed: " + g.Detail)).ToList();
                if (survivalFails.Count > 0)
                    failures.Add("zero tolerance (" + string.Join(", ", allZeroGates) + ") on every row: " + survivalFails.Count.ToString(CultureInfo.InvariantCulture) + " failed - " + string.Join("; ", survivalFails.Take(8)));
            }
            // Every wizard game flies on a typical stick + throttle setup (review 6 B7: twelve games had no flight axes).
            int flightAxesRows;
            List<string> flightAxesFailures = EngineSetupEval.FlightAxesFailures(rows, out flightAxesRows);
            failures.AddRange(flightAxesFailures);
            // (the pad and typical-hangar thresholds hold for the fixed popular setups; random and regression setups are reported)
            List<EngineSetupEval.EvalRow> padPopular = result.PadRows.Where(r => r.SetupKind == "popular").ToList();
            List<EngineSetupEval.EvalRow> typicalPopular = result.TypicalRows.Where(r => r.SetupKind == "popular").ToList();
            if (minPadHard > 0 && padPopular.Count > 0 && EngineSetupEval.HardPassRate(padPopular) < minPadHard)
                failures.Add("ACE COMBAT 8 alone: hard gates passed in " + EngineSetupEval.Pct(EngineSetupEval.HardPassRate(padPopular)) + " % of the popular setups < " + EngineSetupEval.Pct(minPadHard) + " %");
            if (minTypicalHard > 0 && typicalPopular.Count > 0 && EngineSetupEval.HardPassRate(typicalPopular) < minTypicalHard)
                failures.Add("typical DCS hangar: hard gates passed in " + EngineSetupEval.Pct(EngineSetupEval.HardPassRate(typicalPopular)) + " % of the popular setups' module rows < " + EngineSetupEval.Pct(minTypicalHard) + " %");
            if (minPipelineHard > 0 && result.PipelineRows.Count > 0 && EngineSetupEval.HardPassRate(result.PipelineRows) < minPipelineHard)
                failures.Add("after Auto Group + Fill all: hard gates passed in " + EngineSetupEval.Pct(EngineSetupEval.HardPassRate(result.PipelineRows)) + " % < " + EngineSetupEval.Pct(minPipelineHard) + " %");
            int maxPipelineDestructive;
            if (maxPipelineDestructiveText != null && int.TryParse(maxPipelineDestructiveText, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxPipelineDestructive) &&
                result.PipelineRows.Sum(r => r.Destructive.Count) > maxPipelineDestructive)
                failures.Add("after Auto Group + Fill all: destructive bindings " + result.PipelineRows.Sum(r => r.Destructive.Count).ToString(CultureInfo.InvariantCulture) + " > " + maxPipelineDestructive.ToString(CultureInfo.InvariantCulture));

            if (result.PipelineRows.Count > 0)
            {
                int maxPipelineLost = 0, maxAllGamesLost = 0;
                if (maxPipelineLostText != null)
                    int.TryParse(maxPipelineLostText, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxPipelineLost);
                if (maxAllGamesLostText != null)
                    int.TryParse(maxAllGamesLostText, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxAllGamesLost);
                List<string> lostByGroup = EngineSetupEval.StageLosses(result.PipelinePlanRows, result.PipelineRows.Where(r => r.Stage == "pipeline"), "after Auto Group + Fill all", null);
                // (the owner's known limitation 2026-11-15: after Auto Group the MSFS throttle may sit on a slider / rotary of
                // the throttle - still an axis, the user moves it back; listed apart, not counted)
                List<string> knownLosses = lostByGroup.Where(l => Regex.IsMatch(l, @" x MSFS20(20|24): throttle lost after Auto Group") && Regex.IsMatch(l, @"\.axis\.(slider|rotary|wheel)")).ToList();
                lostByGroup = lostByGroup.Except(knownLosses).ToList();
                result.KnownLosses.AddRange(knownLosses);
                List<string> lostByAllGames = EngineSetupEval.StageLosses(result.Rows, result.PipelinePlanRows, "in the all-games plan", null);
                result.StageLosses.AddRange(lostByAllGames);
                result.StageLosses.AddRange(lostByGroup);
                if (lostByGroup.Count > maxPipelineLost)
                    failures.Add("essentials lost after Auto Group + Fill all: " + lostByGroup.Count.ToString(CultureInfo.InvariantCulture) + " > " + maxPipelineLost.ToString(CultureInfo.InvariantCulture) + ": " + string.Join("; ", lostByGroup.Take(8)));
                if (lostByAllGames.Count > maxAllGamesLost)
                    failures.Add("essentials lost in the all-games plan (the quick path's one plan for every game): " + lostByAllGames.Count.ToString(CultureInfo.InvariantCulture) + " > " + maxAllGamesLost.ToString(CultureInfo.InvariantCulture) + ": " + string.Join("; ", lostByAllGames.Take(8)));
                List<string> pipelineZero = SplitList(pipelineZeroGatesText);
                foreach (EngineSetupEval.EvalRow row in result.PipelineRows.Concat(result.PipelinePlanRows).Where(r => r.SetupKind == "popular"))
                    foreach (EngineSetupEval.EvalGate gate in row.Gates.Where(g => g.Status == "fail" && pipelineZero.Contains(g.Gate, StringComparer.OrdinalIgnoreCase)))
                        failures.Add((row.Stage == "pipeline" ? "after Auto Group + Fill all: " : "all-games plan: ") + row.Setup + " x " + row.Target + ": gate " + gate.Gate + " failed: " + gate.Detail);
                if (minAllGamesHard > 0 && result.PipelinePlanRows.Count > 0 && EngineSetupEval.HardPassRate(result.PipelinePlanRows) < minAllGamesHard)
                    failures.Add("all games in one plan: hard gates passed in " + EngineSetupEval.Pct(EngineSetupEval.HardPassRate(result.PipelinePlanRows)) + " % < " + EngineSetupEval.Pct(minAllGamesHard) + " %");
            }

            List<EngineSetupEval.EvalRow> piped = result.PipelineRows;
            if (minPipeline > 0 && piped.Count > 0 && wmean(piped, r => r.Coverage) < minPipeline)
                failures.Add("mean coverage after Auto Group and Fill all " + EngineSetupEval.Pct(wmean(piped, r => r.Coverage)) + " % < " + EngineSetupEval.Pct(minPipeline) + " %");
            int maxWrong;
            if (maxWrongText != null && int.TryParse(maxWrongText, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxWrong))
            {
                int wrongKinds = piped.Sum(r => r.Wrong.Count);
                if (result.WrongSuggestions.Count + wrongKinds > maxWrong)
                    failures.Add("wrong-kind matches " + (result.WrongSuggestions.Count + wrongKinds).ToString(CultureInfo.InvariantCulture) + " > " + maxWrong.ToString(CultureInfo.InvariantCulture) +
                                 " (" + result.WrongSuggestions.Count.ToString(CultureInfo.InvariantCulture) + " suggestions taken, " + wrongKinds.ToString(CultureInfo.InvariantCulture) + " commands in relations)");
            }
            foreach (EngineSetupEval.EvalRow row in piped.Where(r => r.Error.Length > 0))
                failures.Add("after Auto Group: " + row.Setup + " x " + row.Target + ": " + row.Error);

            failures.AddRange(EngineSetupEval.PersonaFailures(result.Persona));

            Dictionary<string, int> gateCounts = EngineSetupEval.GateCounts(rows.Where(r => r.SetupKind == "popular"));
            if (!string.IsNullOrWhiteSpace(writeGateBaselineText))
                File.WriteAllText(NormalizePath(writeGateBaselineText), MiniJson.Write(gateCounts.ToDictionary(p => p.Key, p => (object)p.Value), true), new UTF8Encoding(false));
            if (!string.IsNullOrWhiteSpace(gateBaselineText))
            {
                string gateBaselineFile = NormalizePath(gateBaselineText);
                if (!File.Exists(gateBaselineFile))
                    failures.Add("gate baseline not found: " + gateBaselineText);
                else
                    failures.AddRange(EngineSetupEval.GateRegressions(EngineSerialization.FromJson<Dictionary<string, object>>(File.ReadAllText(gateBaselineFile)) ?? new Dictionary<string, object>(), gateCounts));
            }

            // Where functions go: the sweep over the slider positions (monotonic, safe at every position).
            List<EngineSetupEval.PlacementPoint> points = null;
            if (sweep.Count > 0)
            {
                points = EngineSetupEval.PlacementSweep(setups, sweep, Path.Combine(folder, "placement-devices"), line => { if (textOutput) Console.Error.WriteLine(line); });
                failures.AddRange(EngineSetupEval.PlacementFailures(points, 1.0));
                File.WriteAllText(Path.Combine(folder, "placement.md"), EngineSetupEval.PlacementMarkdown(points), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(folder, "placement.json"), MiniJson.Write(EngineSetupEval.PlacementJson(points), true), new UTF8Encoding(false));
            }

            // The regression set: no destructive binding, nothing fixed broken again; new failing random setups are saved.
            if (minRegressionHard > 0 && regressionRows.Count > 0 && EngineSetupEval.HardPassRate(regressionRows) < minRegressionHard)
                failures.Add("regression set: hard gates passed in " + EngineSetupEval.Pct(EngineSetupEval.HardPassRate(regressionRows)) + " % of its rows < " + EngineSetupEval.Pct(minRegressionHard) + " %");
            result.RegressionFile = regressionFile ?? string.Empty;
            // (the tracked file is only written when it is named with --regression-file: a plain run or a test leaves it alone)
            failures.AddRange(EngineSetupEval.UpdateRegression(regressionFile, result, regressionSetups, result.RegressionNotes, !string.IsNullOrWhiteSpace(regressionText)));
            if (failures.Count > 0 && result.Setups.Any(s => s.Kind == "random"))
                failures.Insert(0, "random setups of seed " + seed.ToString(CultureInfo.InvariantCulture) + " (repeat with --seed " + seed.ToString(CultureInfo.InvariantCulture) + "; failing random setups are saved to the regression set " + (regressionFile ?? "(none: --regression-file)") + ")");

            string commandLine = "setup eval" + (headTracking ? " --head-tracking yes" : string.Empty) + (random != 20 ? " --random " + random.ToString(CultureInfo.InvariantCulture) : string.Empty) + " --seed " + seed.ToString(CultureInfo.InvariantCulture) +
                                 (gamesText != null ? " --games " + gamesText : string.Empty) + (familiesText != null ? " --families \"" + familiesText + "\"" : string.Empty) +
                                 (noStress ? " --no-stress" : string.Empty) + (noPipeline ? " --no-pipeline" : string.Empty) + (noPersona ? " --no-persona" : string.Empty) + (setupsText != null ? " --setups " + setupsText : string.Empty);
            File.WriteAllText(Path.Combine(folder, "report.md"), EngineSetupEval.Markdown(result, baseline, commandLine, failures), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "results.json"), MiniJson.Write(EngineSetupEval.ToJson(result), true), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "rows.csv"), EngineSetupEval.Csv(result), new UTF8Encoding(false));
            if (result.Persona != null)
            {
                File.WriteAllText(Path.Combine(folder, "persona.md"), EngineSetupEval.PersonaMarkdown(result.Persona), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(folder, "persona.json"), MiniJson.Write(EngineSetupEval.ToJson(result.Persona), true), new UTF8Encoding(false));
            }
            string setupFolder = Path.Combine(folder, "setups");
            Directory.CreateDirectory(setupFolder);
            foreach (EngineSetupEval.EvalSetup setup in result.Setups)
                File.WriteAllText(Path.Combine(setupFolder, Regex.Replace(setup.Id, @"[^A-Za-z0-9_.-]+", "_") + ".md"), EngineSetupEval.SetupMarkdown(result, setup), new UTF8Encoding(false));

            Func<IEnumerable<EngineSetupEval.EvalRow>, string> summary = set =>
            {
                List<EngineSetupEval.EvalRow> list = set.ToList();
                return "hard gates passed " + EngineSetupEval.Pct(EngineSetupEval.HardPassRate(list)) + " %, coverage " + EngineSetupEval.Pct(wmean(list, r => r.Coverage)) + " %, ergonomics " + EngineSetupEval.Pct(wmean(list, r => r.Ergonomics)) + " %, score " +
                       EngineSetupEval.Pct(wmean(list, r => r.Score)) + " (" + list.Count.ToString(CultureInfo.InvariantCulture) + " rows)";
            };
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Setup Eval: " + result.Setups.Count.ToString(CultureInfo.InvariantCulture) + " setups x " + result.Targets.Count.ToString(CultureInfo.InvariantCulture) + " targets in " +
                              (result.Millis / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s");
                sb.AppendLine("In-flight essentials (headline), setup alone: popular " + EngineSetupEval.EssentialsSummary(rows.Where(r => r.SetupKind == "popular")) +
                              "; random " + EngineSetupEval.EssentialsSummary(rows.Where(r => r.SetupKind == "random")));
                if (piped.Count > 0)
                    sb.AppendLine("In-flight essentials after Auto Group + Fill all: " + EngineSetupEval.EssentialsSummary(piped));
                sb.AppendLine("All: " + summary(rows));
                sb.AppendLine("Popular: " + summary(rows.Where(r => r.SetupKind == "popular")));
                sb.AppendLine("Random (seed " + seed.ToString(CultureInfo.InvariantCulture) + ", repeat with --seed " + seed.ToString(CultureInfo.InvariantCulture) + "): " + summary(rows.Where(r => r.SetupKind == "random")));
                if (regressionRows.Count > 0)
                    sb.AppendLine("Regression set (" + regressionSetups.Count(s => result.Setups.Contains(s)).ToString(CultureInfo.InvariantCulture) + " setups, " + (regressionFile ?? string.Empty) + "): " + summary(regressionRows));
                foreach (string note in result.RegressionNotes)
                    sb.AppendLine(note);
                if (result.Persona != null)
                    sb.AppendLine("Novice case (IL-2 + URSA MINOR): " + (EngineSetupEval.PersonaFailures(result.Persona).Count == 0 ? "passed" : "failed") + ", " +
                                  summary(result.Persona.Rows) + "; after Auto Group + Fill all " + summary(result.Persona.PipelineRows) + " - " + Path.Combine(folder, "persona.md"));
                if (flightAxesRows > 0)
                    sb.AppendLine("Wizard flight axes (pitch, roll, throttle on stick + throttle setups): " + (flightAxesRows * 3 - flightAxesFailures.Count).ToString(CultureInfo.InvariantCulture) + " of " +
                                  (flightAxesRows * 3).ToString(CultureInfo.InvariantCulture) + " reached in " + flightAxesRows.ToString(CultureInfo.InvariantCulture) + " rows");
                {
                    List<EngineSetupEval.EvalRow> zoomRows = rows.Where(r => r.Zoom == "ok" || r.Zoom == "missing").ToList();
                    if (zoomRows.Count > 0)
                        sb.AppendLine("Zoom (look essential, gate \"zoom\"): bound in " + zoomRows.Count(r => r.Zoom == "ok").ToString(CultureInfo.InvariantCulture) + " of " + zoomRows.Count.ToString(CultureInfo.InvariantCulture) + " rows whose game has it");
                }
                if (result.TypicalRows.Count > 0)
                    sb.AppendLine("Typical DCS hangar (20 modules, one plan): " + summary(result.TypicalRows));
                if (result.PadRows.Count > 0)
                    sb.AppendLine("ACE COMBAT 8 alone: " + summary(result.PadRows));
                if (piped.Count > 0)
                {
                    sb.AppendLine("After Auto Group + Fill all: " + summary(piped));
                    sb.AppendLine("Wrong suggestions taken: " + result.WrongSuggestions.Count.ToString(CultureInfo.InvariantCulture) + ", wrong-kind commands in relations: " +
                                  piped.Sum(r => r.Wrong.Count).ToString(CultureInfo.InvariantCulture));
                }
                if (points != null)
                    foreach (EngineSetupEval.PlacementPoint p in points)
                        sb.AppendLine("Placement " + p.Realism.ToString("0.00", CultureInfo.InvariantCulture) + ": hard gates " + EngineSetupEval.Pct(p.HardPass) + " %, destructive " + p.Destructive.ToString(CultureInfo.InvariantCulture) +
                                      ", essentials " + EngineSetupEval.Pct(p.Essentials) + " %, cockpit-position fidelity " + EngineSetupEval.Pct(p.Fidelity) + " %, cross-aircraft consistency " + EngineSetupEval.Pct(p.Consistency) + " %");
                sb.AppendLine("Report: " + Path.Combine(folder, "report.md"));
                foreach (string failure in failures)
                    sb.AppendLine("Threshold failed: " + failure);
                if (failures.Count == 0)
                    sb.AppendLine("Thresholds: all passed");
                EmitPayload(sb.ToString(), outPath);
            }
            else
            {
                EmitPayload(MiniJson.Write(new Dictionary<string, object>
                {
                    { "Report", Path.Combine(folder, "report.md") }, { "Rows", rows.Count }, { "Seed", seed }, { "Failures", failures.Cast<object>().ToList() },
                    { "Coverage", wmean(rows, r => r.Coverage) }, { "Ergonomics", wmean(rows, r => r.Ergonomics) }, { "Score", wmean(rows, r => r.Score) },
                    { "HardPass", EngineSetupEval.HardPassRate(rows) }, { "PipelineHardPass", EngineSetupEval.HardPassRate(piped) }, { "Destructive", rows.Sum(r => r.Destructive.Count) },
                    { "PipelineCoverage", wmean(piped, r => r.Coverage) }, { "PipelineScore", wmean(piped, r => r.Score) },
                    { "WrongSuggestions", result.WrongSuggestions.Count }, { "WrongKinds", piped.Sum(r => r.Wrong.Count) }
                }, false), outPath);
            }
            return failures.Count == 0 ? 0 : 1;
        }

        private static double PopOptionDouble(List<string> args, string option)
        {
            string value = PopOptionValue(args, option);
            double parsed;
            return value != null && double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }
    }
}
