using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.DeviceReference;

namespace JoyPro.Engine
{
    // The random setups of engine setup eval (docs/auto-setup-eval.md#random-setups-and-the-regression-set): a fresh seed
    // on every run (unless --seed N), real combinations drawn from the whole device catalog across all vendors, and a
    // tracked regression set - every random setup that failed a core gate or produced a destructive binding, re-run in
    // every later eval so a fixed bug stays fixed.
    internal static partial class EngineSetupEval
    {
        private static int seedCounter;

        // A new seed per run: time, process and a counter (two runs in one process differ too).
        internal static int FreshSeed()
        {
            unchecked
            {
                long mix = DateTime.UtcNow.Ticks ^ ((long)Environment.TickCount << 20) ^ ((long)System.Diagnostics.Process.GetCurrentProcess().Id << 40) ^ (++seedCounter * 7919L);
                int seed = (int)((mix ^ (mix >> 31)) & 0x7fffffff) % 1000000000;
                return seed == 2026 ? 2027 : Math.Max(1, seed);
            }
        }

        // The shapes a random setup takes, with their weights: what people really fly with.
        private static readonly Tuple<string, double>[] Shapes =
        {
            Tuple.Create("stick+throttle+pedals", 0.24), Tuple.Create("stick only", 0.10), Tuple.Create("hotas", 0.14), Tuple.Create("yoke+quadrant", 0.14),
            Tuple.Create("dual sticks", 0.10), Tuple.Create("gamepad", 0.08), Tuple.Create("stick+throttle+panels", 0.14), Tuple.Create("helicopter", 0.06)
        };

        // Random but real setups from the whole catalog: the shape first, then for every device a vendor and a device of
        // it (so the big catalogs do not crowd out the small vendors). Seeded: the same seed gives the same setups.
        internal static List<EvalSetup> RandomSetups(int count, int seed, List<string> warnings)
        {
            // (a device whose catalog lists only part of its controls - "incomplete": true - is no fair test of the planner:
            // it is left out of the draw until its layout is known)
            List<KbDevice> kb = EngineDeviceKb.Load(warnings).Where(d => d.Id.Length > 0 && !d.Incomplete).OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
            Func<KbDevice, bool> usable = d => (d.Axes.Count + d.Controls.Count) > 0;
            List<KbDevice> sticks = kb.Where(d => d.Kind == "stick" && d.Hand != "left" && usable(d)).ToList();
            List<KbDevice> hotas = kb.Where(d => d.Kind == "hotas" && usable(d)).ToList();
            List<KbDevice> yokes = kb.Where(d => d.Kind == "yoke" && d.Controls.Count > 0).ToList();
            List<KbDevice> throttles = kb.Where(d => d.Kind == "throttle" && d.Hand != "right" && usable(d)).ToList();
            List<KbDevice> quadrants = kb.Where(d => d.Kind == "quadrant" && usable(d)).ToList();
            List<KbDevice> pedals = kb.Where(d => d.Kind == "pedals" && d.Axes.Count > 0).ToList();
            List<KbDevice> lefts = kb.Where(d => d.Kind == "stick" && d.Hand == "left" && usable(d)).ToList();
            List<KbDevice> collectives = kb.Where(d => d.Kind == "collective" && d.Controls.Count > 0).ToList();
            List<KbDevice> gamepads = kb.Where(d => d.Kind == "gamepad" && usable(d)).ToList();
            List<KbDevice> panels = kb.Where(d => (d.Kind == "panel" || d.Kind == "mfd" || d.Kind == "controller") && d.Controls.Count >= 4).ToList();
            Random rng = new Random(seed);
            Func<List<KbDevice>, KbDevice> pick = list =>
            {
                if (list.Count == 0)
                    return null;
                List<string> vendors = list.Select(d => d.Vendor ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.Ordinal).ToList();
                string vendor = vendors[rng.Next(vendors.Count)];
                List<KbDevice> of = list.Where(d => string.Equals(d.Vendor ?? string.Empty, vendor, StringComparison.OrdinalIgnoreCase)).ToList();
                return of[rng.Next(of.Count)];
            };
            Func<double, bool> chance = p => rng.NextDouble() < p;
            List<EvalSetup> setups = new List<EvalSetup>();
            for (int i = 0; i < count; i++)
            {
                double roll = rng.NextDouble() * Shapes.Sum(s => s.Item2);
                string shape = Shapes.Last().Item1;
                foreach (Tuple<string, double> s in Shapes)
                {
                    if (roll < s.Item2) { shape = s.Item1; break; }
                    roll -= s.Item2;
                }
                List<KbDevice> chosen = new List<KbDevice>();
                switch (shape)
                {
                    case "stick+throttle+pedals":
                        chosen.Add(pick(sticks)); chosen.Add(pick(throttles)); chosen.Add(pick(pedals));
                        break;
                    case "stick only":
                        chosen.Add(pick(sticks));
                        if (chance(0.3)) chosen.Add(pick(pedals));
                        break;
                    case "hotas":
                        chosen.Add(hotas.Count > 0 && chance(0.6) ? pick(hotas) : pick(sticks));
                        if (chosen[0] != null && chosen[0].Kind != "hotas") chosen.Add(pick(throttles));
                        if (chance(0.4)) chosen.Add(pick(pedals));
                        break;
                    case "yoke+quadrant":
                        chosen.Add(pick(yokes));
                        if (chance(0.85)) chosen.Add(pick(quadrants));
                        if (chance(0.6)) chosen.Add(pick(pedals));
                        if (chance(0.35)) chosen.Add(pick(panels));
                        break;
                    case "dual sticks":
                        chosen.Add(pick(sticks));
                        chosen.Add(lefts.Count > 0 ? pick(lefts) : pick(sticks));
                        if (chance(0.4)) chosen.Add(pick(pedals));
                        break;
                    case "gamepad":
                        chosen.Add(pick(gamepads));
                        if (chance(0.4)) chosen.Add(pick(panels));
                        break;
                    case "stick+throttle+panels":
                        chosen.Add(pick(sticks)); chosen.Add(pick(throttles));
                        if (chance(0.6)) chosen.Add(pick(pedals));
                        chosen.Add(pick(panels));
                        if (chance(0.4)) chosen.Add(pick(panels));
                        break;
                    default:
                        chosen.Add(pick(sticks)); chosen.Add(pick(collectives));
                        if (chance(0.7)) chosen.Add(pick(pedals));
                        break;
                }
                chosen = chosen.Where(d => d != null).GroupBy(d => d.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
                EvalSetup setup = new EvalSetup { Id = "random-" + (i + 1).ToString("D2", CultureInfo.InvariantCulture), Kind = "random", Seed = seed, Shape = shape };
                foreach (KbDevice device in chosen)
                    setup.Devices.Add(new EvalDevice { Catalog = device.Id });
                setup.Name = shape + ": " + string.Join(" + ", chosen.Select(d => d.Name));
                setups.Add(setup);
            }
            return setups;
        }

        // ---- the regression set ----

        internal static readonly string[] CoreGates = { "pitch", "roll", "yaw", "throttle", "fire", "wheel_brakes" };

        // The tracked file: engine/tests/fixtures/setup-eval-regression.tsv of the source tree the engine was built from
        // (searched upwards from the engine's folder), or the one given with --regression-file.
        internal static string FindRegressionFile()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                foreach (string candidate in new[] { Path.Combine(dir, "engine", "tests", "fixtures", "setup-eval-regression.tsv"), Path.Combine(dir, "tests", "fixtures", "setup-eval-regression.tsv") })
                    if (File.Exists(candidate))
                        return candidate;
                dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            }
            return null;
        }

        // id <TAB> seed <TAB> shape <TAB> devices (catalog[=role], comma) <TAB> failing when added (gate:target, ...) <TAB>
        // fixed since (the same, passing in a later run) <TAB> date
        internal static List<EvalSetup> LoadRegression(string file, List<string> warnings)
        {
            List<EvalSetup> list = new List<EvalSetup>();
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
                return list;
            foreach (string line in File.ReadAllLines(file))
            {
                if (line.Trim().Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                string[] c = line.Split('\t');
                if (c.Length < 4)
                {
                    warnings.Add("regression set: line skipped: " + line);
                    continue;
                }
                int seed;
                int.TryParse(c[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seed);
                EvalSetup setup = new EvalSetup { Id = c[0].Trim(), Kind = "regression", Seed = seed, Shape = c[2].Trim(), Added = c.Length > 6 ? c[6].Trim() : string.Empty };
                foreach (string device in c[3].Split(',').Select(d => d.Trim()).Where(d => d.Length > 0))
                {
                    int eq = device.IndexOf('=');
                    setup.Devices.Add(new EvalDevice { Catalog = eq > 0 ? device.Substring(0, eq) : device, Role = eq > 0 ? device.Substring(eq + 1) : string.Empty });
                }
                if (c.Length > 4) setup.Signatures.AddRange(c[4].Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
                if (c.Length > 5) setup.Fixed.AddRange(c[5].Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
                setup.Name = "regression (seed " + seed.ToString(CultureInfo.InvariantCulture) + ", " + setup.Shape + "): " + string.Join(" + ", setup.Devices.Select(d => d.Catalog));
                list.Add(setup);
            }
            return list;
        }

        // What a setup fails now: core gates of its own plans (setup alone) and destructive bindings in any stage.
        internal static HashSet<string> Signatures(EvalResult result, string setupId)
        {
            HashSet<string> found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (EvalRow row in result.Rows.Where(r => r.Setup == setupId))
            {
                foreach (EvalGate gate in row.Gates.Where(g => g.Status == "fail" && CoreGates.Contains(g.Gate)))
                    found.Add(gate.Gate + ":" + row.Target);
                if (row.Destructive.Count > 0)
                    found.Add("destructive:" + row.Target);
            }
            foreach (EvalRow row in result.PipelineRows.Concat(result.PipelinePlanRows).Concat(result.TypicalRows).Where(r => r.Setup == setupId && r.Destructive.Count > 0))
                found.Add("destructive-" + row.Stage + ":" + row.Target);
            return found;
        }

        internal static string Who(EvalResult result, string setupId)
        {
            EvalSetup setup = result.Setups.FirstOrDefault(s => s.Id == setupId);
            if (setup == null || (setup.Kind != "random" && setup.Kind != "regression"))
                return setupId;
            return setupId + " (seed " + setup.Seed.ToString(CultureInfo.InvariantCulture) + ", " + setup.Shape + ": " + string.Join(" + ", setup.Devices.Select(d => d.Catalog)) + ")";
        }

        // After a run: a regression setup that passes what it failed marks it fixed; one that fails what was fixed is a
        // failure ("fixed and broken again"). A random setup with a failure no regression setup covers yet is added.
        // Returns the failures; added / fixed are written back when the file is writable.
        internal static List<string> UpdateRegression(string file, EvalResult result, List<EvalSetup> regression, List<string> notes, bool write)
        {
            List<string> failures = new List<string>();
            if (string.IsNullOrEmpty(file))
                return failures;
            bool changed = false;
            HashSet<string> targets = new HashSet<string>(result.Targets.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
            Func<string, bool> scored = s => targets.Contains(s.Substring(s.IndexOf(':') + 1));
            foreach (EvalSetup setup in regression.Where(s => result.Setups.Contains(s)))
            {
                HashSet<string> now = Signatures(result, setup.Id);
                foreach (string broken in setup.Fixed.Where(f => now.Contains(f)))
                    failures.Add("regression " + Who(result, setup.Id) + ": " + broken + " was fixed and fails again");
                foreach (string fixedNow in setup.Signatures.Where(s => scored(s) && !now.Contains(s) && !setup.Fixed.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList())
                {
                    setup.Fixed.Add(fixedNow);
                    changed = true;
                }
                foreach (string destructive in now.Where(s => s.StartsWith("destructive", StringComparison.Ordinal)))
                    failures.Add("regression " + Who(result, setup.Id) + ": " + destructive);
            }
            HashSet<string> covered = new HashSet<string>(regression.SelectMany(s => s.Signatures.Select(Generic)), StringComparer.OrdinalIgnoreCase);
            List<EvalSetup> added = new List<EvalSetup>();
            foreach (EvalSetup setup in result.Setups.Where(s => s.Kind == "random"))
            {
                HashSet<string> now = Signatures(result, setup.Id);
                // (one setup per kind of failure - gate and target - so the set stays small and varied)
                List<string> fresh = now.Where(s => !covered.Contains(Generic(s))).ToList();
                string devices = string.Join(",", setup.Devices.Select(d => d.Catalog + (d.Role.Length > 0 ? "=" + d.Role : string.Empty)));
                if (fresh.Count == 0 || regression.Concat(added).Any(r => string.Join(",", r.Devices.Select(d => d.Catalog + (d.Role.Length > 0 ? "=" + d.Role : string.Empty))) == devices))
                    continue;
                foreach (string s in now)
                    covered.Add(Generic(s));
                EvalSetup copy = new EvalSetup { Id = "reg-" + setup.Seed.ToString(CultureInfo.InvariantCulture) + "-" + setup.Id.Replace("random-", string.Empty), Kind = "regression", Seed = setup.Seed, Shape = setup.Shape,
                                                 Added = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Devices = setup.Devices };
                copy.Signatures.AddRange(now.OrderBy(s => s, StringComparer.Ordinal));
                added.Add(copy);
            }
            if (added.Count > 0)
                notes.Add("regression set: " + added.Count.ToString(CultureInfo.InvariantCulture) + " new setup(s) " + (write ? "saved to " + file : "to add (run with --regression-file to save them)") + ": " +
                          string.Join("; ", added.Select(a => a.Id + " = " + string.Join(", ", a.Signatures.Take(3)))));
            if ((!changed && added.Count == 0) || !write)
                return failures;
            try
            {
                List<string> lines = new List<string>
                {
                    "# engine setup eval regression set (docs/auto-setup-eval.md#random-setups-and-the-regression-set): random setups that failed a core gate",
                    "# (pitch, roll, yaw, throttle, fire, wheel_brakes) or produced a destructive binding, re-run in every eval. Written by the eval itself.",
                    "# id\tseed\tshape\tdevices\tfailing when added (gate:target)\tfixed since\tadded"
                };
                foreach (EvalSetup setup in regression.Concat(added))
                    lines.Add(string.Join("\t", setup.Id, setup.Seed.ToString(CultureInfo.InvariantCulture), setup.Shape,
                                          string.Join(",", setup.Devices.Select(d => d.Catalog + (d.Role.Length > 0 ? "=" + d.Role : string.Empty))),
                                          string.Join(",", setup.Signatures), string.Join(",", setup.Fixed), setup.Added));
                File.WriteAllText(file, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                notes.Add("regression set: could not write " + file + ": " + ex.Message);
            }
            return failures;
        }

        // gate:target without the DCS module (fire:DCS:F-16C_50 -> fire:DCS) for the "already covered" test.
        private static string Generic(string signature)
        {
            return Regex.Replace(signature ?? string.Empty, @"^([^:]+:DCS):.*$", "$1");
        }
    }
}
