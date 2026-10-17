#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.DeviceReference;

namespace JoyPro.Setup
{
    // The knowledge map the wizard plans with (DB\Functions, docs/auto-group.md): functions (slots.json), where each
    // belongs physically (slot_controls.tsv), the aircraft (aircraft.json) and per game the commands of every aircraft
    // with their real HOTAS side and frequency (<Game>.tsv). Read-only, no engine dependencies.
    public static class SetupCatalog
    {
        public static bool Load(string folder, List<string> errors, out Dictionary<string, SetupFunction> functions,
            out Dictionary<string, SetupAircraft> aircraft, out List<SetupCommand> commands)
        {
            functions = new Dictionary<string, SetupFunction>(StringComparer.OrdinalIgnoreCase);
            aircraft = new Dictionary<string, SetupAircraft>(StringComparer.OrdinalIgnoreCase);
            commands = new List<SetupCommand>();
            string slotsFile = Path.Combine(folder ?? string.Empty, "slots.json");
            string aircraftFile = Path.Combine(folder ?? string.Empty, "aircraft.json");
            if (!File.Exists(slotsFile) || !File.Exists(aircraftFile))
            {
                errors.Add("Function catalog missing in " + folder + " (slots.json, aircraft.json); update JoyPro.");
                return false;
            }
            try
            {
                foreach (IDictionary<string, object> item in (MiniJson.Parse(File.ReadAllText(slotsFile, Encoding.UTF8)) as List<object> ?? new List<object>()).OfType<IDictionary<string, object>>())
                {
                    SetupFunction function = new SetupFunction
                    {
                        Id = Text(item, "id"),
                        Name = Text(item, "name"),
                        Kind = Text(item, "kind"),
                        IsAxis = Text(item, "axis") == "True" || Text(item, "axis") == "true",
                        Frequency = Text(item, "frequency").Length > 0 ? Text(item, "frequency") : "occasional",
                        Side = Text(item, "hotas").Length > 0 ? Text(item, "hotas") : "any"
                    };
                    if (function.Id.Length > 0)
                        functions[function.Id] = function;
                }
                IDictionary<string, object> planes = MiniJson.Parse(File.ReadAllText(aircraftFile, Encoding.UTF8)) as IDictionary<string, object>;
                foreach (KeyValuePair<string, object> plane in planes ?? new Dictionary<string, object>())
                {
                    IDictionary<string, object> value = plane.Value as IDictionary<string, object>;
                    string game = Text(value, "game");
                    aircraft[plane.Key] = new SetupAircraft
                    {
                        Key = plane.Key,
                        Game = game.Length > 0 ? game : "DCS",
                        Type = Text(value, "type"),
                        Era = Text(value, "era"),
                        VariantOf = Text(value, "variant_of")
                    };
                }

                string controlsFile = Path.Combine(folder, "slot_controls.tsv");
                if (File.Exists(controlsFile))
                {
                    foreach (string line in File.ReadAllLines(controlsFile, Encoding.UTF8))
                    {
                        if (line.Length == 0 || line[0] == '#')
                            continue;
                        string[] parts = line.Split('\t');
                        if (parts.Length < 7)
                            continue;
                        SetupFunction function;
                        if (!functions.TryGetValue(parts[0], out function))
                            functions[parts[0]] = function = new SetupFunction { Id = parts[0], Name = parts[0] };
                        string[] pair = parts[3].Split(':');
                        function.Side = parts[1].Length > 0 ? parts[1] : function.Side;
                        function.Prefs = parts[2].Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                        function.PairGroup = pair.Length == 2 ? pair[0] : string.Empty;
                        function.PairRole = pair.Length == 2 ? pair[1] : string.Empty;
                        function.Labels = parts[4].Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                        if (parts[5].Length > 0)
                            function.Frequency = parts[5];
                        function.IsAxis = parts[6] == "axis";
                        function.Group = parts.Length > 7 ? parts[7].Trim() : string.Empty;
                    }
                }
                else
                {
                    errors.Add("slot_controls.tsv missing in " + folder + ": every function is treated as a plain button.");
                }
                DerivePairs(functions);
                ReadMapCategories(Path.Combine(folder, "function-map.json"), functions, errors);

                foreach (string file in Directory.GetFiles(folder, "*.tsv").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileName(file);
                    if (name.EndsWith(".rejected.tsv", StringComparison.OrdinalIgnoreCase) || name.Equals("slot_controls.tsv", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string game = Path.GetFileNameWithoutExtension(file);
                    foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                    {
                        if (line.Length == 0 || line[0] == '#')
                            continue;
                        string[] parts = line.Split('\t');
                        if (parts.Length < 4)
                            continue;
                        commands.Add(new SetupCommand
                        {
                            Game = game,
                            Aircraft = parts[0],
                            Function = parts[1],
                            Action = parts[2],
                            Status = parts[3],
                            Hotas = parts.Length > 4 ? parts[4].Trim() : string.Empty,
                            Frequency = parts.Length > 5 ? parts[5].Trim() : string.Empty
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add("Function catalog could not be read: " + ex.Message);
                return false;
            }
            return true;
        }

        // The function knowledge map (DB\Functions\function-map.json, docs/relation-builder.md): each function names the
        // catalog slots it links to; the slot takes the function's category (the importance ranking).
        private static void ReadMapCategories(string file, Dictionary<string, SetupFunction> functions, List<string> errors)
        {
            if (!File.Exists(file))
                return;
            try
            {
                IDictionary<string, object> root = MiniJson.Parse(File.ReadAllText(file, Encoding.UTF8)) as IDictionary<string, object>;
                object list;
                if (root == null || !root.TryGetValue("functions", out list) || !(list is List<object>))
                    return;
                foreach (IDictionary<string, object> item in ((List<object>)list).OfType<IDictionary<string, object>>())
                {
                    object slots;
                    if (!item.TryGetValue("slots", out slots) || !(slots is IDictionary<string, object>))
                        continue;
                    foreach (string slot in ((IDictionary<string, object>)slots).Keys)
                    {
                        SetupFunction function;
                        if (functions.TryGetValue(slot, out function) && function.MapFunction.Length == 0)
                        {
                            function.MapFunction = Text(item, "id");
                            function.MapCategory = Text(item, "category");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add("function-map.json could not be read: " + ex.Message);
            }
        }

        // On/off functions without a pair in the map (lights_ext_landing_taxi_on / _off, ..._on1 / _off1): a latched pair
        // like gear up / down, so both go on the two positions of one switch.
        private static void DerivePairs(Dictionary<string, SetupFunction> functions)
        {
            Regex pattern = new Regex(@"^(.+?)_?(on|off)(\d*)$", RegexOptions.IgnoreCase);
            List<Tuple<SetupFunction, string, string>> derived = new List<Tuple<SetupFunction, string, string>>();
            foreach (SetupFunction function in functions.Values.Where(f => !f.IsAxis && f.PairGroup.Length == 0))
            {
                Match m = pattern.Match(function.Id);
                if (!m.Success)
                    continue;
                string counter = m.Groups[1].Value + (function.Id.Substring(m.Groups[1].Value.Length).StartsWith("_", StringComparison.Ordinal) ? "_" : string.Empty) +
                                 (m.Groups[2].Value.Equals("on", StringComparison.OrdinalIgnoreCase) ? "off" : "on") + m.Groups[3].Value;
                SetupFunction other;
                if (!functions.TryGetValue(counter, out other) || other.IsAxis || other.PairGroup.Length > 0)
                    continue;
                derived.Add(Tuple.Create(function, "derived_" + m.Groups[1].Value.ToLowerInvariant() + m.Groups[3].Value, m.Groups[2].Value.ToLowerInvariant()));
            }
            foreach (Tuple<SetupFunction, string, string> item in derived)
            {
                item.Item1.PairGroup = item.Item2;
                item.Item1.PairRole = item.Item3;
            }
        }

        // The aircraft of the chosen games (base aircraft only; variants follow their base aircraft's priority).
        public static List<SetupAircraft> AircraftOf(Dictionary<string, SetupAircraft> aircraft, IEnumerable<string> games, Func<SetupAircraft, bool> available)
        {
            HashSet<string> wanted = new HashSet<string>(games ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            return aircraft.Values
                .Where(a => wanted.Contains(a.Game) && !a.SharedLayer && a.VariantOf.Length == 0 && (available == null || available(a)))
                .OrderBy(a => a.Game, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string Text(IDictionary<string, object> item, string key)
        {
            object value;
            return item != null && item.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture).Trim() : string.Empty;
        }
    }
}
