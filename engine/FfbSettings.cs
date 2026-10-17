using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Axis
{
    // Force feedback settings of the games, per device and scope (docs/ffb.md):
    //   "<device>|game:<game>"         every aircraft of a game
    //   "<device>|plane:<game>:<plane>"  one aircraft (DCS ffDiffs are per aircraft)
    // Game-wide settings (IL-2 startup.cfg) use the device "*". The most specific scope wins; values a scope does not
    // set fall through to the next one, then to what the game file already has. DCS invert/swap still come from the
    // older per-device JoystickFFB entry when no scope sets them.
    public sealed class FfbParameter
    {
        public string Key;
        public string Label;
        public bool Toggle;
        public double Min;
        public double Max;
        public double Default;
        // Settings of the whole game (device "*") rather than of one device.
        public bool GameWide;
        public string Help;
    }

    public sealed class FfbModel
    {
        public string Game;
        public string Title;
        public string Description;
        public List<FfbParameter> Parameters = new List<FfbParameter>();
    }

    // Marks a static method in engine\FfbModels\*.cs that returns a game's settings model.
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class GameFfbModelAttribute : Attribute
    {
    }

    public static class FfbSettings
    {
        public const string AnyDevice = "*";

        private static readonly Dictionary<string, FfbModel> Models = Build();

        public static IEnumerable<FfbModel> All
        {
            get { return Models.Values; }
        }

        public static FfbModel ForGame(string game)
        {
            FfbModel model;
            return Models.TryGetValue(game ?? string.Empty, out model) ? model : null;
        }

        public static FfbParameter Parameter(string game, string key)
        {
            FfbModel model = ForGame(game);
            return model == null ? null : model.Parameters.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        public static string Key(string device, string scope)
        {
            return (string.IsNullOrWhiteSpace(device) ? AnyDevice : device.Trim()) + "|" + scope;
        }

        public static string Format(Dictionary<string, double> values)
        {
            return string.Join(";", values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Select(v => v.Key + "=" + v.Value.ToString("0.######", CultureInfo.InvariantCulture)));
        }

        public static Dictionary<string, double> Parse(string text)
        {
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (text ?? string.Empty).Split(';'))
            {
                int eq = part.IndexOf('=');
                double number;
                if (eq > 0 && double.TryParse(part.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    values[part.Substring(0, eq).Trim()] = number;
            }
            return values;
        }

        // The settings a device gets in a game (and aircraft): values of the most specific scope first.
        public static Dictionary<string, double> Resolve(Dictionary<string, string> store, string device, string game, string plane)
        {
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (store == null)
                return values;
            List<string> scopes = new List<string>();
            if (!string.IsNullOrWhiteSpace(plane))
                scopes.Add(BindTuning.PlaneScope(game, plane));
            scopes.Add(BindTuning.GameScope(game));
            foreach (string scope in scopes)
            {
                string text;
                if (!store.TryGetValue(Key(device, scope), out text))
                    continue;
                foreach (KeyValuePair<string, double> value in Parse(text))
                {
                    if (!values.ContainsKey(value.Key))
                        values[value.Key] = value.Value;
                }
            }
            return values;
        }

        public static IEnumerable<string> DevicesFor(Dictionary<string, string> store, string game)
        {
            if (store == null)
                return Enumerable.Empty<string>();
            return store.Keys
                .Where(k => k.Contains("|game:" + game) || k.Contains("|plane:" + game + ":"))
                .Select(k => k.Substring(0, k.IndexOf('|')))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static Dictionary<string, FfbModel> Build()
        {
            Dictionary<string, FfbModel> models = new Dictionary<string, FfbModel>(StringComparer.OrdinalIgnoreCase);
            models["DCS"] = new FfbModel
            {
                Game = "DCS",
                Title = "DCS World",
                Description = "Per device and aircraft, the diff.lua ffDiffs block (Options > Controls > FF Tune). Ignore switches DCS's force feedback off for that device and aircraft.",
                Parameters =
                {
                    new FfbParameter { Key = "trimmer", Label = "Trimmer force", Min = 0, Max = 1, Default = 1 },
                    new FfbParameter { Key = "shake", Label = "Shake", Min = 0, Max = 1, Default = 0.5 },
                    new FfbParameter { Key = "swapAxes", Label = "Swap axes", Toggle = true },
                    new FfbParameter { Key = "invertX", Label = "Invert X", Toggle = true },
                    new FfbParameter { Key = "invertY", Label = "Invert Y", Toggle = true },
                    new FfbParameter { Key = "ignore", Label = "Ignore (no DCS force feedback)", Toggle = true }
                }
            };
            models["IL2Game"] = new FfbModel
            {
                Game = "IL2Game",
                Title = "IL-2 Great Battles",
                Description = "Game-wide, Settings > Input devices: data\\input\\current.devices (ffEnabled, ffForce, ffShaking, joystickFilter) and data\\startup.cfg [force_feedback] (device *).",
                Parameters =
                {
                    new FfbParameter { Key = "enabled", Label = "Force feedback on", Toggle = true, Default = 1, GameWide = true },
                    new FfbParameter { Key = "force", Label = "Feedback power", Min = 0, Max = 1, Default = 1, GameWide = true },
                    new FfbParameter { Key = "amplitude", Label = "Shaking", Min = 0, Max = 1, Default = 1, GameWide = true },
                    new FfbParameter { Key = "joystickFilter", Label = "Joystick noise filter", Min = 0, Max = 1, Default = 0, GameWide = true,
                        Help = "Smooths noisy axis sensors (older or cheap sticks); the game shows it as 0..1 (current.map: use joy(1000.00,<filter>))." }
                }
            };
            models["IL2Korea"] = new FfbModel
            {
                Game = "IL2Korea",
                Title = "IL-2 Series: Korea",
                Description = "Game-wide, Settings > Input devices: data\\NSData\\UserData\\UserSettings.json \"input\" (device *), in % as the game shows them.",
                Parameters =
                {
                    new FfbParameter { Key = "ffEnabled", Label = "Force feedback on", Toggle = true, Default = 1, GameWide = true },
                    new FfbParameter { Key = "ffAmplitude", Label = "Shaking power (%)", Min = 0, Max = 100, Default = 100, GameWide = true },
                    new FfbParameter { Key = "ffForce", Label = "Feedback power (%)", Min = 0, Max = 100, Default = 100, GameWide = true },
                    new FfbParameter { Key = "ffForceFactorMin", Label = "Low-speed stick force (%)", Min = 0, Max = 200, Default = 100, GameWide = true },
                    new FfbParameter { Key = "ffForceFactorMax", Label = "High-speed stick force (%)", Min = 0, Max = 200, Default = 100, GameWide = true },
                    new FfbParameter { Key = "ffPositionFactor", Label = "Force-neutral rebalancing (%)", Min = 0, Max = 100, Default = 100, GameWide = true },
                    new FfbParameter { Key = "ffContactFactor", Label = "Terrain contact (%)", Min = 0, Max = 100, Default = 100, GameWide = true },
                    new FfbParameter { Key = "joyFilter", Label = "Axis noise filter (%, all joysticks)", Min = 0, Max = 100, Default = 0, GameWide = true,
                        Help = "Smooths noisy axis sensors (older or cheap sticks)." }
                }
            };
            // A game's own settings model lives in engine\FfbModels\<Game>.cs: a static method marked [GameFfbModel]
            // that returns it (force feedback and other game-wide control settings, device "*").
            foreach (Type type in typeof(FfbSettings).Assembly.GetTypes())
            {
                foreach (System.Reflection.MethodInfo method in type.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (method.GetCustomAttributes(typeof(GameFfbModelAttribute), false).Length == 0 || method.GetParameters().Length != 0 || !typeof(FfbModel).IsAssignableFrom(method.ReturnType))
                        continue;
                    FfbModel model = (FfbModel)method.Invoke(null, null);
                    models[model.Game] = model;
                }
            }
            return models;
        }
    }

    // IL-2 Korea's Settings > Input devices: the "input" object of data\NSData\UserData\UserSettings.json. The keys are
    // set in place (the game writes 2-space JSON with numbers like 100.0); everything else keeps its text. Values JoyPro
    // stored before (startup.cfg keys enabled/force/amplitude, 0..1) are taken over.
    public static class Il2KoreaUserSettings
    {
        private static readonly Dictionary<string, string> Legacy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "enabled", "ffEnabled" }, { "force", "ffForce" }, { "amplitude", "ffAmplitude" }
        };

        public static Dictionary<string, double> Normalize(Dictionary<string, double> values)
        {
            Dictionary<string, double> result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, double> value in values ?? new Dictionary<string, double>())
            {
                string key;
                if (Legacy.TryGetValue(value.Key, out key))
                {
                    if (!values.ContainsKey(key))
                        result[key] = key == "ffEnabled" ? value.Value : value.Value * 100.0;
                }
                else if (FfbSettings.Parameter("IL2Korea", value.Key) != null)
                    result[value.Key] = value.Value;
            }
            return result;
        }

        public static bool Apply(ref string text, Dictionary<string, double> values, List<string> changes)
        {
            Dictionary<string, double> wanted = Normalize(values);
            if (wanted.Count == 0)
                return false;
            Match input = Regex.Match(text, "\"input\"\\s*:\\s*\\{");
            if (!input.Success)
                return false;
            int open = input.Index + input.Length - 1;
            int close = text.IndexOf('}', open);
            if (close < 0)
                return false;
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            bool changed = false;
            foreach (KeyValuePair<string, double> value in wanted.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                FfbParameter parameter = FfbSettings.Parameter("IL2Korea", value.Key);
                string rendered = parameter != null && parameter.Toggle
                    ? (value.Value != 0 ? "true" : "false")
                    : (Math.Abs(value.Value - Math.Round(value.Value)) < 1e-9 ? Math.Round(value.Value).ToString("0", CultureInfo.InvariantCulture) + ".0" : value.Value.ToString("R", CultureInfo.InvariantCulture));
                string block = text.Substring(open, close - open);
                Match existing = Regex.Match(block, "\"" + Regex.Escape(value.Key) + "\"\\s*:\\s*(?<v>[^,\\r\\n}]+)");
                if (existing.Success)
                {
                    Group v = existing.Groups["v"];
                    string current = v.Value.Trim();
                    double parsed;
                    bool same = parameter != null && parameter.Toggle
                        ? string.Equals(current, rendered, StringComparison.OrdinalIgnoreCase)
                        : double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && Math.Abs(parsed - value.Value) < 1e-9;
                    if (same)
                        continue;
                    int at = open + v.Index;
                    text = text.Substring(0, at) + rendered + text.Substring(at + v.Value.TrimEnd().Length);
                }
                else
                {
                    // A new key goes last: a comma after the previous last value.
                    int last = close - 1;
                    while (last > open && char.IsWhiteSpace(text[last]))
                        last--;
                    string indent = "    ";
                    Match lineIndent = Regex.Match(block, "\\n(?<i>[ \\t]+)\"");
                    if (lineIndent.Success)
                        indent = lineIndent.Groups["i"].Value;
                    string insert = (last > open ? "," : string.Empty) + nl + indent + "\"" + value.Key + "\": " + rendered;
                    text = text.Substring(0, last + 1) + insert + text.Substring(last + 1);
                }
                close = text.IndexOf('}', open);
                changed = true;
                changes.Add("input " + value.Key + " = " + rendered);
            }
            return changed;
        }
    }

    // IL-2 Great Battles' Settings > Input devices: data\input\current.devices, one URL query line the game writes on
    // ACCEPT ("ffEnabled=1&ffShaking=1&mouseAcceleration=0&ffForce=1&mouseSensitivity=4&mouseInvertYAxis=0&
    // joystickFilter=0.13"). The keys are set in place; other keys (mouse) and their order stay.
    public static class Il2CurrentDevices
    {
        private static readonly Dictionary<string, string> Keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "enabled", "ffEnabled" }, { "force", "ffForce" }, { "amplitude", "ffShaking" }, { "joystickFilter", "joystickFilter" }
        };

        // The settings the line holds, under JoyPro's keys (engine sync compares them with the profile's).
        public static Dictionary<string, double> Read(string text)
        {
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (text ?? string.Empty).Trim().Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0)
                    continue;
                string fileKey = part.Substring(0, eq);
                KeyValuePair<string, string> key = Keys.FirstOrDefault(k => string.Equals(k.Value, fileKey, StringComparison.Ordinal));
                double value;
                if (key.Key != null && double.TryParse(part.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    values[key.Key] = value;
            }
            return values;
        }

        public static bool Apply(ref string text, Dictionary<string, double> values, List<string> changes)
        {
            if (values == null || values.Count == 0)
                return false;
            List<KeyValuePair<string, string>> pairs = new List<KeyValuePair<string, string>>();
            foreach (string part in (text ?? string.Empty).Trim().Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                pairs.Add(eq < 0 ? new KeyValuePair<string, string>(part, string.Empty) : new KeyValuePair<string, string>(part.Substring(0, eq), part.Substring(eq + 1)));
            }
            bool changed = false;
            foreach (KeyValuePair<string, double> value in values.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                string key;
                if (!Keys.TryGetValue(value.Key, out key))
                    continue;
                string rendered = key == "ffEnabled"
                    ? (value.Value != 0 ? "1" : "0")
                    : Math.Round(Math.Max(0, Math.Min(1, value.Value)), 2).ToString("0.##", CultureInfo.InvariantCulture);
                int at = pairs.FindIndex(p => string.Equals(p.Key, key, StringComparison.Ordinal));
                if (at >= 0)
                {
                    double parsed;
                    if (string.Equals(pairs[at].Value, rendered, StringComparison.Ordinal)
                        || (double.TryParse(pairs[at].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && Math.Abs(parsed - double.Parse(rendered, CultureInfo.InvariantCulture)) < 1e-9))
                        continue;
                    pairs[at] = new KeyValuePair<string, string>(key, rendered);
                }
                else
                    pairs.Add(new KeyValuePair<string, string>(key, rendered));
                changed = true;
                changes.Add(key + "=" + rendered);
            }
            if (changed)
                text = string.Join("&", pairs.Select(p => p.Key + "=" + p.Value));
            return changed;
        }
    }

    // [KEY = force_feedback] ... [END] in an IL-2 startup.cfg: the keys of the section are set in place, everything
    // else (tabs, order, other sections) stays as it is.
    public static class Il2StartupForceFeedback
    {
        private static readonly HashSet<string> SectionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "enabled", "force", "amplitude" };

        // The keys of the [KEY = force_feedback] section (engine sync compares them with the profile's).
        public static Dictionary<string, double> Read(string text)
        {
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            bool inside = false;
            foreach (string raw in (text ?? string.Empty).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Equals("[KEY = force_feedback]", StringComparison.OrdinalIgnoreCase))
                    inside = true;
                else if (line.Equals("[END]", StringComparison.OrdinalIgnoreCase))
                    inside = false;
                else if (inside)
                {
                    string[] kv = line.Split('=');
                    double value;
                    if (kv.Length == 2 && SectionKeys.Contains(kv[0].Trim()) && double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        values[kv[0].Trim()] = value;
                }
            }
            return values;
        }

        public static bool Apply(ref string text, Dictionary<string, double> values, List<string> changes)
        {
            values = (values ?? new Dictionary<string, double>()).Where(v => SectionKeys.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
            if (values.Count == 0)
                return false;
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = text.Split(new[] { nl }, StringSplitOptions.None).ToList();
            int start = lines.FindIndex(l => l.Trim().Equals("[KEY = force_feedback]", StringComparison.OrdinalIgnoreCase));
            if (start < 0)
            {
                int insert = lines.Count;
                while (insert > 0 && lines[insert - 1].Length == 0)
                    insert--;
                lines.Insert(insert, "[KEY = force_feedback]");
                lines.Insert(insert + 1, "[END]");
                start = insert;
            }
            int end = lines.FindIndex(start + 1, l => l.Trim().Equals("[END]", StringComparison.OrdinalIgnoreCase));
            if (end < 0)
                return false;
            bool changed = false;
            foreach (KeyValuePair<string, double> value in values.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                string rendered = value.Key == "enabled"
                    ? (value.Value != 0 ? "1" : "0")
                    : value.Value.ToString("0.00000", CultureInfo.InvariantCulture);
                int at = -1;
                for (int i = start + 1; i < end; i++)
                {
                    string[] kv = lines[i].Split('=');
                    if (kv.Length == 2 && kv[0].Trim().Equals(value.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        at = i;
                        break;
                    }
                }
                if (at >= 0)
                {
                    string existing = lines[at].Split('=')[1].Trim();
                    double parsed;
                    if (double.TryParse(existing, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && Math.Abs(parsed - value.Value) < 1e-9)
                        continue;
                    string indent = lines[at].Substring(0, lines[at].Length - lines[at].TrimStart().Length);
                    lines[at] = indent + value.Key + " = " + rendered;
                }
                else
                {
                    lines.Insert(end, "\t" + value.Key + " = " + rendered);
                    end++;
                }
                changed = true;
                changes.Add("force_feedback " + value.Key + " = " + rendered);
            }
            if (changed)
                text = string.Join(nl, lines);
            return changed;
        }
    }
}
