#if NET
#nullable disable
#endif
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JoyPro.DeviceReference;

namespace JoyPro.Setup
{
    // Where a function sits in the real aircraft (the "Realism" slider of the Setup wizard). The cockpit database
    // (engine cockpits get <aircraft> [<variant>] --json, docs/setup-wizard.md#cockpit-database) gives, per control
    // position of the real HOTAS, the function ids there. Without an entry the planner falls back to the knowledge map:
    // the function's own position (slot_controls.tsv) on the side the aircraft has it (the HOTAS column of <Game>.tsv).
    public interface ICockpitPositions
    {
        // The positions (device vocabulary: stick.hat.tms.up, throttle.speedbrake.aft ...) of a function in an aircraft.
        bool TryGet(string aircraft, string function, out List<string> controls);

        bool Knows(string aircraft);

        string Source { get; }
    }

    // Cockpit entries read from the JSON of "engine cockpits get". Accepted shape (keys in any case):
    //   { "aircraft": "F-16C_50", "variant": "", "game": "DCS",
    //     "positions": [ { "control": "stick.hat.tms.up", "functions": ["target_management_system_up"] }, ... ] }
    // "controls" / "hotas" are accepted for "positions", "position" / "id" for "control", "function" (one id) for
    // "functions", and a list of such entries (several aircraft) as the root.
    public sealed class CockpitPositionTable : ICockpitPositions
    {
        private readonly Dictionary<string, Dictionary<string, List<string>>> byAircraft =
            new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> sources = new List<string>();

        public string Source
        {
            get { return sources.Count == 0 ? "knowledge map (no cockpit database entry)" : "cockpit database: " + string.Join(", ", sources); }
        }

        public int Count { get { return byAircraft.Count; } }

        public IEnumerable<string> AircraftKeys { get { return byAircraft.Keys; } }

        public bool Knows(string aircraft)
        {
            return !string.IsNullOrEmpty(aircraft) && byAircraft.ContainsKey(aircraft);
        }

        public bool TryGet(string aircraft, string function, out List<string> controls)
        {
            controls = null;
            Dictionary<string, List<string>> map;
            return !string.IsNullOrEmpty(aircraft) && byAircraft.TryGetValue(aircraft, out map) && map.TryGetValue(function ?? string.Empty, out controls) && controls.Count > 0;
        }

        public void Add(string aircraft, string control, string function)
        {
            if (string.IsNullOrWhiteSpace(aircraft) || string.IsNullOrWhiteSpace(control) || string.IsNullOrWhiteSpace(function))
                return;
            Dictionary<string, List<string>> map;
            if (!byAircraft.TryGetValue(aircraft.Trim(), out map))
                byAircraft[aircraft.Trim()] = map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            List<string> list;
            if (!map.TryGetValue(function.Trim(), out list))
                map[function.Trim()] = list = new List<string>();
            if (!list.Contains(control.Trim(), StringComparer.OrdinalIgnoreCase))
                list.Add(control.Trim());
        }

        // Adds the entries of one JSON document; returns the aircraft read (none = not a cockpit document).
        public List<string> AddJson(string json, string sourceName, List<string> warnings)
        {
            List<string> read = new List<string>();
            object root;
            try
            {
                root = MiniJson.Parse(json ?? string.Empty);
            }
            catch (Exception ex)
            {
                if (warnings != null)
                    warnings.Add("Cockpit entry " + sourceName + " could not be read: " + ex.Message);
                return read;
            }
            IEnumerable<object> entries = root is IDictionary<string, object> ? new[] { root } : (root as IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>();
            foreach (IDictionary<string, object> entry in entries.OfType<IDictionary<string, object>>())
            {
                // The answer of "engine cockpits get <aircraft> --json" (format joypro-cockpit-layout, docs/cockpits.md).
                if (string.Equals(Text(entry, "format"), "joypro-cockpit-layout", StringComparison.OrdinalIgnoreCase))
                {
                    read.AddRange(AddLayout(entry));
                    continue;
                }
                // "engine cockpits get" may wrap the entry ({ "Cockpit": {...} }).
                IDictionary<string, object> body = Get(entry, "cockpit") as IDictionary<string, object> ?? entry;
                string aircraft = Text(body, "aircraft");
                if (aircraft.Length == 0)
                    aircraft = Text(body, "name");
                object positions = Get(body, "positions") ?? Get(body, "controls") ?? Get(body, "hotas");
                if (aircraft.Length == 0 || !(positions is IEnumerable))
                    continue;
                foreach (IDictionary<string, object> position in ((IEnumerable)positions).Cast<object>().OfType<IDictionary<string, object>>())
                {
                    string control = Text(position, "control");
                    if (control.Length == 0)
                        control = Text(position, "position");
                    if (control.Length == 0)
                        control = Text(position, "id");
                    object functions = Get(position, "functions");
                    if (functions is IEnumerable && !(functions is string))
                    {
                        foreach (object function in (IEnumerable)functions)
                            Add(aircraft, control, Convert.ToString(function, CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        Add(aircraft, control, Text(position, "function"));
                    }
                }
                read.Add(aircraft);
            }
            if (read.Count > 0)
                sources.Add(sourceName);
            return read;
        }

        // A joypro-cockpit-layout: its game aircraft names (Games[].Plane, else the game for a game-wide entry) get the
        // positions Devices[].Controls[].Functions[] (Vocabulary -> Fn).
        private List<string> AddLayout(IDictionary<string, object> layout)
        {
            List<string> keys = new List<string>();
            foreach (IDictionary<string, object> game in List(layout, "games"))
            {
                string key = Text(game, "plane");
                if (key.Length == 0)
                    key = Text(game, "game");
                if (key.Length > 0 && !keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    keys.Add(key);
            }
            IDictionary<string, object> variant = Get(layout, "variant") as IDictionary<string, object>;
            if (variant != null && Text(variant, "id").Length > 0)
                keys.Add(Text(variant, "id"));
            foreach (IDictionary<string, object> device in List(layout, "devices"))
            {
                foreach (IDictionary<string, object> control in List(device, "controls"))
                {
                    foreach (IDictionary<string, object> function in List(control, "functions"))
                    {
                        foreach (string key in keys)
                            Add(key, Text(function, "vocabulary"), Text(function, "fn"));
                    }
                }
            }
            return keys.Where(Knows).ToList();
        }

        private static IEnumerable<IDictionary<string, object>> List(IDictionary<string, object> item, string key)
        {
            IEnumerable list = Get(item, key) as IEnumerable;
            return list == null || list is string ? Enumerable.Empty<IDictionary<string, object>>() : list.Cast<object>().OfType<IDictionary<string, object>>();
        }

        // The cockpit database itself (DB\Cockpits): every variant that names a game aircraft gives that aircraft its
        // real positions (a game-wide entry, empty plane, goes to the game's key, first variant only). Aircraft already
        // known (from a file the caller passed) keep their entry.
        public int AddCatalog(CockpitCatalog catalog, string sourceName)
        {
            if (catalog == null)
                return 0;
            HashSet<string> before = new HashSet<string>(byAircraft.Keys, StringComparer.OrdinalIgnoreCase);
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (CockpitAircraft aircraft in catalog.Aircraft.Where(a => !a.Hidden))
            {
                foreach (CockpitVariant raw in aircraft.Variants)
                {
                    CockpitVariant v = catalog.Resolve(raw);
                    foreach (CockpitGameRef game in v.Games)
                    {
                        string key = game.Plane.Length > 0 ? game.Plane : game.Game;
                        if (key.Length == 0 || before.Contains(key) || !taken.Add(key))
                            continue;
                        foreach (CockpitControl c in v.Controls)
                        {
                            foreach (CockpitFunction f in c.Functions.Where(x => x.Fn.Length > 0))
                                Add(key, CockpitSchematic.Vocabulary(c, f), f.Fn);
                        }
                        added++;
                    }
                }
            }
            if (added > 0)
                sources.Add(sourceName);
            return added;
        }

        // Aircraft-specific HOTAS positions and the generic F-16C / A-10C II position they correspond to
        // (DB\Functions\hotas-equivalence.json, from ED's own HOTAS Warthog default profile of each module): the F/A-18's
        // sensor control switch is where the F-16's TMS is, so a device control that stands in for the TMS is also the
        // Hornet's SCS. Each position gets its generic name next to its own; a key with a direction (or a full name) wins
        // over a shorter prefix key. Returns the positions added.
        public int AddEquivalence(string json, List<string> warnings)
        {
            object root;
            try
            {
                root = MiniJson.Parse(json ?? string.Empty);
            }
            catch (Exception ex)
            {
                if (warnings != null)
                    warnings.Add("hotas-equivalence.json could not be read: " + ex.Message);
                return 0;
            }
            IDictionary<string, object> planes = Get(root as IDictionary<string, object> ?? new Dictionary<string, object>(), "aircraft") as IDictionary<string, object>;
            if (planes == null)
                return 0;
            int added = 0;
            foreach (KeyValuePair<string, object> plane in planes)
            {
                IDictionary<string, object> pairs = plane.Value as IDictionary<string, object>;
                Dictionary<string, List<string>> map;
                if (pairs == null || !byAircraft.TryGetValue(plane.Key, out map))
                    continue;
                List<KeyValuePair<string, string>> keys = pairs.Select(p => new KeyValuePair<string, string>(p.Key.Trim(), Convert.ToString(p.Value, CultureInfo.InvariantCulture).Trim()))
                    .Where(p => p.Key.Length > 0 && p.Value.Length > 0).OrderByDescending(p => p.Key.Length).ToList();
                foreach (List<string> controls in map.Values)
                {
                    foreach (string control in controls.ToList())
                    {
                        string generic = Generic(control, keys);
                        if (generic.Length > 0 && !controls.Contains(generic, StringComparer.OrdinalIgnoreCase))
                        {
                            controls.Add(generic);
                            added++;
                        }
                    }
                }
            }
            return added;
        }

        internal static string Generic(string control, List<KeyValuePair<string, string>> keys)
        {
            foreach (KeyValuePair<string, string> key in keys)
            {
                if (string.Equals(control, key.Key, StringComparison.OrdinalIgnoreCase))
                    return key.Value;
                if (control.StartsWith(key.Key + ".", StringComparison.OrdinalIgnoreCase))
                    return key.Value + control.Substring(key.Key.Length);
            }
            return string.Empty;
        }

        // The equivalence file next to the function catalog (missing: nothing added).
        public int AddEquivalenceFile(string functionsFolder, List<string> warnings)
        {
            string path = System.IO.Path.Combine(functionsFolder ?? string.Empty, "hotas-equivalence.json");
            return System.IO.File.Exists(path) ? AddEquivalence(System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8), warnings) : 0;
        }

        private static object Get(IDictionary<string, object> item, string key)
        {
            foreach (KeyValuePair<string, object> pair in item)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }
            return null;
        }

        private static string Text(IDictionary<string, object> item, string key)
        {
            object value = Get(item, key);
            return value == null || value is IDictionary<string, object> || (value is IEnumerable && !(value is string))
                ? string.Empty
                : Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
        }
    }
}
