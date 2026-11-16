using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.DeviceReference;
using JoyPro.Engine.Games;
using JoyPro.Setup;

namespace JoyPro.Engine
{
    // "Where should functions go?" (SetupOptions.Realism, docs/auto-setup-eval.md#where-functions-go): the planner from
    // "the same button for the same job in every aircraft" (0) to "where the real aircraft has it" (1). The sweep plans the
    // typical DCS hangar (20 modules in one plan) of every popular setup at several positions and measures how close the
    // functions sit to the real aircraft's HOTAS (cockpit-position fidelity) and how often one function sits on the same
    // input in every module (cross-aircraft consistency); the fly-and-fight gates and the destructive check hold at every
    // position.
    internal static partial class EngineSetupEval
    {
        // The planner's realism in every plan of the eval (the wizard's default is 0.5).
        internal static double RealismValue = 0.5;
        // The wizard's head-tracking answer the eval plans with (--head-tracking yes|no; default no: the user without a tracker).
        internal static bool HeadTrackingValue;

        // The fidelity gain from one end of the slider to the other that counts as a visible effect (points).
        internal static double PlacementMinEffect = 5.0;
        // Every step between two sweep positions changes fidelity or consistency by at least this many points; no step
        // carries more than this share of the fidelity gain; no gate fails in more than this share of the rows (%) beyond its best position.
        internal static double PlacementMinStep = 0.5;
        internal static double PlacementMaxStepShare = 0.6;
        internal static double PlacementMaxGateDrop = 2.0;

        internal sealed class PlacementPoint
        {
            public double Realism;
            public int Rows;
            public double HardPass;
            public int Destructive;
            public double Essentials;
            public double CriticalComplete;
            public double Fidelity;
            public double Consistency;
            public List<string> ZeroGateFails = new List<string>();
            public SortedDictionary<string, int> GateFails = new SortedDictionary<string, int>(StringComparer.Ordinal);   // failed gates by name
            // Every failed gate of the row with its reason ("setup x target: gate: detail"), for the report and placement.json.
            public List<string> GateFailRows = new List<string>();
            public Dictionary<string, double> FidelityByAircraft = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            // The other games planned alone (review 7 L41 / M12): per row ("setup x game") the status of each gate that must
            // not depend on the slider, and the failed gates with their reasons.
            public int GameRows;
            public double GameHardPass;
            public Dictionary<string, Dictionary<string, string>> GameGates = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            public List<string> GameGateFailRows = new List<string>();
        }

        // The games besides DCS the sweep plans alone at every position (review 7 M12 / L41: X4 lost roll and Elite put the
        // hardpoints on labelled base buttons at "where the real aircraft has it", and no sweep saw it). Arma 3 is scored as
        // helicopter and plane.
        internal static readonly string[] SweepGames = { "X4", "EliteDangerous", "StarCitizen", "IL2Game", "MSFS2024", "FalconBMS", "WarThunder", "Arma3" };

        // The gates of those rows that must hold at every position where they hold at one: the flight axes, the weapons,
        // the flares and no function on a switch labelled for another one.
        internal static readonly string[] SweepGameGates = { "pitch", "roll", "yaw", "throttle", "fire", "release", "countermeasures", "switches" };

        // One game alone on the setup at the sweep's position (row "game>X4", stage "sweep-game").
        internal static void RunGameAlone(EvalSetup setup, List<SetupDevice> devices, List<SetupInput> inputs, List<EvalRow> rows, EvalTarget target)
        {
            List<SetupDevice> copies = devices.Select(Copy).ToList();
            if (!copies.Any(d => SetupRoles.Binds(d.Role)) || !Applies(target, inputs))
                return;
            EvalRow row = new EvalRow { Setup = setup.Id, SetupKind = setup.Kind, Target = "game>" + target.Id, Stage = "sweep-game", Weight = target.Share };
            try
            {
                SetupOptions options = new SetupOptions { Games = new List<string> { target.Game }, Modifiers = 0, PreferFewer = true, Realism = RealismValue, HeadTracking = HeadTrackingValue, PhaseLayers = "auto", Seed = 1 };
                options.Priorities.Add(new List<string> { target.Aircraft });
                SetupRequest request = EngineSetupWizard.Request(copies, inputs, options, null, new List<string>());
                SetupPlan plan = TruthPlan(SetupPlanner.Plan(request), setup.Truth);
                Score(row, target, request, plan);
            }
            catch (Exception ex)
            {
                row.Error = ex.GetType().Name + ": " + ex.Message;
            }
            rows.Add(row);
        }

        // Cockpit-position fidelity of one module in a plan: the mean of how real its positions are, over the functions
        // whose real position the cockpit database knows.
        internal static double Fidelity(SetupPlan plan, string module, out int known)
        {
            List<SetupPlanRelation> list = plan.Relations.Where(r => r.RealKnown && !r.IsAxis && r.Device.Length > 0 && r.Aircraft.Contains(module, StringComparer.OrdinalIgnoreCase)).ToList();
            known = list.Count;
            return list.Count == 0 ? double.NaN : 100.0 * list.Average(r => r.RealMatch);
        }

        // Cross-aircraft consistency of a plan: per function bound for two or more modules, the share of those modules
        // that have it on the one input most of them use; weighted by the number of modules.
        internal static double Consistency(SetupPlan plan)
        {
            Dictionary<string, Dictionary<string, string>> inputOf = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (SetupPlanRelation r in plan.Relations.Where(r => !r.IsAxis && r.Device.Length > 0 && !string.IsNullOrEmpty(r.Function)))
            {
                Dictionary<string, string> byPlane;
                if (!inputOf.TryGetValue(r.Function, out byPlane))
                    inputOf[r.Function] = byPlane = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string plane in r.Aircraft)
                    if (!byPlane.ContainsKey(plane))
                        byPlane[plane] = r.Device + "|" + r.Input + "|" + r.Modifier;
            }
            double same = 0, total = 0;
            foreach (Dictionary<string, string> byPlane in inputOf.Values.Where(p => p.Count >= 2))
            {
                same += byPlane.Values.GroupBy(v => v).Max(g => g.Count());
                total += byPlane.Count;
            }
            return total == 0 ? double.NaN : 100.0 * same / total;
        }

        // The sweep: the popular setups' typical DCS hangar at each position.
        internal static List<PlacementPoint> PlacementSweep(List<EvalSetup> setups, List<double> positions, string workFolder, Action<string> log)
        {
            List<PlacementPoint> points = new List<PlacementPoint>();
            string previousFake = Environment.GetEnvironmentVariable(GameDeviceDirectory.FakeDevicesEnvironmentVariable);
            double previousRealism = RealismValue;
            bool previousCache = EngineSetupWizard.CacheCatalog;
            EngineSetupWizard.CacheCatalog = true;
            Directory.CreateDirectory(workFolder);
            List<EvalTarget> gameTargets = Targets(SweepGames.ToList(), new List<string>(), false).Where(t => !t.Pad).ToList();
            try
            {
                foreach (double position in positions)
                {
                    RealismValue = position;
                    EvalResult part = new EvalResult();
                    PlacementPoint point = new PlacementPoint { Realism = position };
                    List<EvalRow> gameRows = new List<EvalRow>();
                    List<double> consistency = new List<double>();
                    List<double> fidelity = new List<double>();
                    Dictionary<string, List<double>> perPlane = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
                    // (L30, review 6 M4: the random and regression setups of the run too - the F-16 and the A-10C alone, the gun
                    // and the countermeasures must not depend on the slider there either; fidelity and consistency stay the
                    // popular setups' hangar)
                    foreach (EvalSetup setup in setups.Where(s => s.Kind == "popular" || s.Kind == "random" || s.Kind == "regression"))
                    {
                        List<string> warnings = new List<string>();
                        EngineSetupWizard.DeviceChoices choices;
                        string fake = WriteFakeDevices(setup, workFolder, warnings, out choices);
                        Environment.SetEnvironmentVariable(GameDeviceDirectory.FakeDevicesEnvironmentVariable, fake);
                        List<SetupInput> inputs;
                        List<SetupDevice> devices = EngineSetupWizard.Devices(choices, warnings, out inputs);
                        // (the other games alone: popular and random setups - the regression set keeps the DCS singles)
                        if (setup.Kind != "regression")
                        {
                            foreach (EvalTarget target in gameTargets)
                                RunGameAlone(setup, devices, inputs, gameRows, target);
                        }
                        if (setup.Kind != "popular")
                        {
                            foreach (string single in SweepSingles)
                                RunSingle(setup, devices, inputs, part, single);
                            continue;
                        }
                        SetupPlan plan = RunTypical(setup, devices, inputs, part);
                        if (plan == null)
                            continue;
                        // (L21: the F-16 and the A-10C alone too - the gates must hold for a single aircraft at every position)
                        foreach (string single in SweepSingles)
                            RunSingle(setup, devices, inputs, part, single);
                        double c = Consistency(plan);
                        if (!double.IsNaN(c))
                            consistency.Add(c);
                        foreach (string module in TypicalHangar)
                        {
                            int known;
                            double f = Fidelity(plan, module, out known);
                            if (double.IsNaN(f))
                                continue;
                            fidelity.Add(f);
                            List<double> list;
                            if (!perPlane.TryGetValue(module, out list))
                                perPlane[module] = list = new List<double>();
                            list.Add(f);
                        }
                    }
                    List<EvalRow> rows = part.TypicalRows;
                    point.Rows = rows.Count;
                    point.HardPass = HardPassRate(rows);
                    point.Destructive = rows.Sum(r => r.Destructive.Count);
                    double weight = rows.Sum(r => r.Weight);
                    point.Essentials = weight <= 0 ? 0 : rows.Sum(r => r.Weight * EssentialPct(r)) / weight;
                    point.CriticalComplete = weight <= 0 ? 0 : 100.0 * rows.Where(CriticalComplete).Sum(r => r.Weight) / weight;
                    point.Fidelity = fidelity.Count == 0 ? 0 : fidelity.Average();
                    point.Consistency = consistency.Count == 0 ? 0 : consistency.Average();
                    foreach (KeyValuePair<string, List<double>> pair in perPlane)
                        point.FidelityByAircraft[pair.Key] = pair.Value.Average();
                    // the safety gates that must hold at every position: the gun, the brakes, the throttle (where the
                    // setup can carry them at all) and no destructive binding
                    // (owner 2026-11-16: looking around counts in the drop check where a grip hat was free for it - a hat no earlier
                    // essential or real HOTAS function holds; a one-hat device whose hat the trim needs is the device's limit)
                    foreach (EvalGate gate in rows.SelectMany(r => r.Gates).Where(g => g.Status == "fail" && (g.Gate != "look" || g.Detail.Contains("grip hats free for them: "))))
                        point.GateFails[gate.Gate] = (point.GateFails.ContainsKey(gate.Gate) ? point.GateFails[gate.Gate] : 0) + 1;
                    foreach (EvalRow row in rows)
                        foreach (EvalGate gate in row.Gates.Where(g => g.Status == "fail"))
                            point.GateFailRows.Add(row.Setup + " x " + row.Target + ": " + gate.Gate + ": " + gate.Detail);
                    foreach (EvalRow row in rows)
                        foreach (EvalGate gate in row.Gates.Where(g => g.Status == "fail" && (g.Gate == "fire" || g.Gate == "throttle" || g.Gate == "countermeasures" || g.Gate == "release")))
                            point.ZeroGateFails.Add(row.Setup + " x " + row.Target + ": " + gate.Gate);
                    point.GameRows = gameRows.Count;
                    point.GameHardPass = HardPassRate(gameRows);
                    point.Destructive += gameRows.Sum(r => r.Destructive.Count);
                    foreach (EvalRow row in gameRows)
                    {
                        Dictionary<string, string> gates = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (string gate in SweepGameGates)
                        {
                            List<EvalGate> list = row.Gates.Where(g => g.Gate == gate).ToList();
                            gates[gate] = row.Error.Length > 0 ? "fail" : list.Count == 0 ? "n/a" : list.Any(g => g.Status == "fail") ? "fail" : list[0].Status;
                        }
                        point.GameGates[row.Setup + " x " + row.Target.Substring("game>".Length)] = gates;
                        foreach (EvalGate gate in row.Gates.Where(g => g.Status == "fail"))
                            point.GameGateFailRows.Add(row.Setup + " x " + row.Target.Substring("game>".Length) + ": " + gate.Gate + ": " + gate.Detail);
                        if (row.Error.Length > 0)
                            point.GameGateFailRows.Add(row.Setup + " x " + row.Target.Substring("game>".Length) + ": error: " + row.Error);
                    }
                    points.Add(point);
                    log("Placement " + position.ToString("0.00", CultureInfo.InvariantCulture) + ": hard gates " + Pct(point.HardPass) + " %, destructive " + point.Destructive +
                        ", essentials " + Pct(point.Essentials) + " %, fidelity " + Pct(point.Fidelity) + " %, consistency " + Pct(point.Consistency) + " %, gate fails " + string.Join(", ", point.GateFails.Select(g => g.Key + " " + g.Value)) +
                        "; other games alone (" + string.Join(", ", SweepGames) + "): " + point.GameRows + " rows, hard gates " + Pct(point.GameHardPass) + " %");
                }
            }
            finally
            {
                RealismValue = previousRealism;
                EngineSetupWizard.CacheCatalog = previousCache;
                Environment.SetEnvironmentVariable(GameDeviceDirectory.FakeDevicesEnvironmentVariable, previousFake);
            }
            return points;
        }

        // Fidelity must rise and consistency fall (or stay) as the slider moves to the real aircraft, without jumps back.
        internal static List<string> PlacementFailures(List<PlacementPoint> points, double tolerance)
        {
            List<string> failures = new List<string>();
            for (int i = 1; i < points.Count; i++)
            {
                if (points[i].Fidelity + tolerance < points[i - 1].Fidelity)
                    failures.Add("placement: cockpit-position fidelity falls from " + Pct(points[i - 1].Fidelity) + " % at " + points[i - 1].Realism.ToString("0.00", CultureInfo.InvariantCulture) + " to " + Pct(points[i].Fidelity) + " % at " + points[i].Realism.ToString("0.00", CultureInfo.InvariantCulture));
                if (points[i].Consistency > points[i - 1].Consistency + tolerance)
                    failures.Add("placement: cross-aircraft consistency rises from " + Pct(points[i - 1].Consistency) + " % at " + points[i - 1].Realism.ToString("0.00", CultureInfo.InvariantCulture) + " to " + Pct(points[i].Consistency) + " % at " + points[i].Realism.ToString("0.00", CultureInfo.InvariantCulture));
            }
            // (the effect must be clearly larger than the tolerance: a slider that moves fidelity by less than
            // PlacementMinEffect points does nothing a user would notice)
            if (points.Count >= 2 && points.Last().Fidelity < points.First().Fidelity + PlacementMinEffect)
                failures.Add("placement: the slider barely changes where functions go (fidelity " + Pct(points.First().Fidelity) + " % at " + points.First().Realism.ToString("0.00", CultureInfo.InvariantCulture) +
                             ", " + Pct(points.Last().Fidelity) + " % at " + points.Last().Realism.ToString("0.00", CultureInfo.InvariantCulture) + "; at least " + PlacementMinEffect.ToString("0", CultureInfo.InvariantCulture) + " points wanted)");
            // (a slider, not a switch: every step between two positions changes something - fidelity up or consistency down by
            // PlacementMinStep - and no single step carries most of the effect, e.g. everything at 0.5)
            if (points.Count >= 3)
            {
                double total = points.Last().Fidelity - points.First().Fidelity;
                for (int i = 1; i < points.Count; i++)
                {
                    double rise = points[i].Fidelity - points[i - 1].Fidelity;
                    double drop = points[i - 1].Consistency - points[i].Consistency;
                    string between = points[i - 1].Realism.ToString("0.00", CultureInfo.InvariantCulture) + " and " + points[i].Realism.ToString("0.00", CultureInfo.InvariantCulture);
                    // (the last step too: "where the real aircraft has it" adds the occasional cockpit buttons at their real
                    // spots, round 7)
                    if (rise < PlacementMinStep && drop < PlacementMinStep)
                        failures.Add("placement: nothing changes between " + between + " (fidelity +" + Pct(rise) + ", consistency -" + Pct(drop) + " points; at least " + Pct(PlacementMinStep) + " wanted)");
                    if (total > 0 && rise > PlacementMaxStepShare * total)
                        failures.Add("placement: the step between " + between + " carries " + Pct(100.0 * rise / total) + " % of the whole fidelity gain (a switch, not a slider; at most " + Pct(100 * PlacementMaxStepShare) + " %)");
                }
                // (the gates hold at every position: no position fails a gate in more than PlacementMaxGateDrop % of the rows
                // beyond the position where it fails least - looking around excepted: from the middle on a real HOTAS
                // position outranks the snap views by design, and the look gate itself judges that)
                foreach (string gate in points.SelectMany(p => p.GateFails.Keys).Distinct(StringComparer.Ordinal))
                {
                    int least = points.Min(p => p.GateFails.ContainsKey(gate) ? p.GateFails[gate] : 0);
                    foreach (PlacementPoint point in points)
                    {
                        int fails = point.GateFails.ContainsKey(gate) ? point.GateFails[gate] : 0;
                        if (point.Rows > 0 && 100.0 * (fails - least) / point.Rows > PlacementMaxGateDrop)
                            failures.Add("placement " + point.Realism.ToString("0.00", CultureInfo.InvariantCulture) + ": gate " + gate + " fails in " + fails + " rows, " + (fails - least) + " more than at its best position");
                    }
                }
            }
            // (the other games alone: a flight axis, a weapon, the flares or a labelled switch that holds at one position must
            // hold at every position - review 7 M12, X4's roll on the twist and Elite's hardpoints on SYS at 1)
            foreach (string row in points.SelectMany(p => p.GameGates.Keys).Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal))
            {
                foreach (string gate in SweepGameGates)
                {
                    List<PlacementPoint> have = points.Where(p => p.GameGates.ContainsKey(row)).ToList();
                    if (!have.Any(p => p.GameGates[row][gate] == "ok"))
                        continue;
                    foreach (PlacementPoint point in have.Where(p => p.GameGates[row][gate] == "fail"))
                        failures.Add("placement " + point.Realism.ToString("0.00", CultureInfo.InvariantCulture) + ": " + row + ": " + gate + " fails here but not at every position (" +
                                     (point.GameGateFailRows.FirstOrDefault(f => f.StartsWith(row + ": " + gate + ":", StringComparison.Ordinal)) ?? string.Empty) + ")");
                }
            }
            foreach (PlacementPoint point in points)
            {
                if (point.Destructive > 0)
                    failures.Add("placement " + point.Realism.ToString("0.00", CultureInfo.InvariantCulture) + ": " + point.Destructive + " destructive bindings");
                // (a gate the setup cannot carry at any position is the setup's limit; one the slider breaks is a failure)
                foreach (string fail in point.ZeroGateFails.Where(f => points.Any(o => !o.ZeroGateFails.Contains(f))).Take(12))
                    failures.Add("placement " + point.Realism.ToString("0.00", CultureInfo.InvariantCulture) + ": " + fail + " fails here but not at every position");
            }
            return failures;
        }

        internal static string PlacementMarkdown(List<PlacementPoint> points)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Where should functions go? (placement sweep)");
            sb.AppendLine();
            sb.AppendLine("The typical DCS hangar (20 modules in one plan) of every popular setup, planned at each slider position from \"Same button for the same " +
                          "job in every aircraft\" (0) to \"Where the real aircraft has it\" (1). Fidelity = how close the functions sit to the real aircraft's HOTAS " +
                          "(cockpit database, functions whose real position is known); consistency = the share of modules that have a function on the one input most " +
                          "modules use. Fidelity must not fall and consistency must not rise as the slider moves to the right; the gun, the throttle and no destructive " +
                          "binding hold at every position.");
            sb.AppendLine();
            sb.AppendLine("| Position | Rows | Hard gates % | Destructive | Essentials % | Time-critical complete % | Fidelity % | Consistency % |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (PlacementPoint p in points)
                sb.AppendLine("| " + p.Realism.ToString("0.00", CultureInfo.InvariantCulture) + " | " + p.Rows + " | " + Pct(p.HardPass) + " | " + p.Destructive + " | " + Pct(p.Essentials) + " | " +
                              Pct(p.CriticalComplete) + " | " + Pct(p.Fidelity) + " | " + Pct(p.Consistency) + " |");
            sb.AppendLine();
            sb.AppendLine("Fidelity per aircraft:");
            sb.AppendLine();
            List<string> planes = points.SelectMany(p => p.FidelityByAircraft.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            sb.AppendLine("| Aircraft | " + string.Join(" | ", points.Select(p => p.Realism.ToString("0.00", CultureInfo.InvariantCulture))) + " |");
            sb.AppendLine("|---|" + string.Join("|", points.Select(p => "---")) + "|");
            foreach (string plane in planes)
                sb.AppendLine("| " + plane + " | " + string.Join(" | ", points.Select(p => p.FidelityByAircraft.ContainsKey(plane) ? Pct(p.FidelityByAircraft[plane]) : "-")) + " |");
            sb.AppendLine();
            if (points.Any(p => p.GameRows > 0))
            {
                sb.AppendLine("Other games alone (" + string.Join(", ", SweepGames) + "; Arma 3 as helicopter and plane): " + string.Join(", ", SweepGameGates) +
                              " must hold at every position where they hold at one.");
                sb.AppendLine();
                sb.AppendLine("| Position | Rows | Hard gates % | Failed gates (" + string.Join(", ", SweepGameGates) + ") |");
                sb.AppendLine("|---|---|---|---|");
                foreach (PlacementPoint p in points)
                    sb.AppendLine("| " + p.Realism.ToString("0.00", CultureInfo.InvariantCulture) + " | " + p.GameRows + " | " + Pct(p.GameHardPass) + " | " +
                                  string.Join(", ", SweepGameGates.Select(g => g + " " + p.GameGates.Values.Count(x => x[g] == "fail")).Where(x => !x.EndsWith(" 0", StringComparison.Ordinal))) + " |");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        internal static Dictionary<string, object> PlacementJson(List<PlacementPoint> points)
        {
            return new Dictionary<string, object>
            {
                { "Points", points.Select(p => (object)new Dictionary<string, object>
                    {
                        { "Realism", p.Realism }, { "Rows", p.Rows }, { "HardPass", Math.Round(p.HardPass, 1) }, { "Destructive", p.Destructive }, { "Essentials", Math.Round(p.Essentials, 1) },
                        { "CriticalComplete", Math.Round(p.CriticalComplete, 1) }, { "Fidelity", Math.Round(p.Fidelity, 1) }, { "Consistency", Math.Round(p.Consistency, 1) },
                        { "FidelityByAircraft", p.FidelityByAircraft.ToDictionary(x => x.Key, x => (object)Math.Round(x.Value, 1)) },
                        { "ZeroGateFails", p.ZeroGateFails.Cast<object>().ToList() },
                        { "GateFails", p.GateFails.ToDictionary(x => x.Key, x => (object)x.Value) },
                        { "GateFailRows", p.GateFailRows.Cast<object>().ToList() },
                        { "GameRows", p.GameRows }, { "GameHardPass", Math.Round(p.GameHardPass, 1) },
                        { "GameGateFailRows", p.GameGateFailRows.Cast<object>().ToList() }
                    }).ToList() }
            };
        }
    }
}
