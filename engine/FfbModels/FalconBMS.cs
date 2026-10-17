using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

// Falcon BMS game-wide control settings (device "*") and per-device throttle detents (docs/games/falcon-bms.md).
// Everything here lives in files the BMS launcher keeps on Launch: Falcon BMS User.cfg above the launcher's marker line,
// Feedback.ini, byte 388 of the pilot file and the launcher's device files.
namespace JoyPro.Engine.Axis
{
    public static class BmsSettings
    {
        public const string Game = "FalconBMS";
        public const string UserCfg = "Falcon BMS User.cfg";
        public const string FeedbackIni = "Feedback.ini";
        // OverrideSetting.OverwriteCfg keeps every line before this one and rebuilds everything after it on Launch; it
        // also drops lines that contain "// SETUP OVERRIDE" or "// LAUNCHER OVERRIDE" anywhere in the file.
        public const string LauncherMarker = "// LAUNCHER OVERRIDES BEGIN HERE - DO NOT EDIT OR ADD BELOW THIS LINE";
        public const int PopSize = 468;
        public const int PopEnableFfb = 388;

        // Game-wide cfg settings: parameter key -> cfg variable (Technical Manual 4.38.1).
        public static readonly Dictionary<string, string> CfgKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "flightControlsDeadband", "g_fFlightControlsDeadband" },
            { "ffbDeadBand", "g_nForceFeedbackDeadBand" },
            { "analogIdleCutoff", "g_bUseAnalogIdleCutoff" },
            { "abDetentClick", "g_bAfterburnerDetentClick" },
            { "virtualRudder", "g_bUseVirtualRudder" },
            { "deprRetAsBrake", "g_bUseDeprRetAsBrake" }
        };

        // Feedback.ini effects (shipped comments): parameter key -> [EffectN] sections whose gain it sets.
        public static readonly Dictionary<string, int[]> GainKeys = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "gunGain", new[] { 0 } },
            { "bounceGain", new[] { 1 } },
            { "autoCenterGain", new[] { 2 } },
            { "runwayGain", new[] { 3, 4 } },
            { "bombDropGain", new[] { 5, 6 } },
            { "stallGain", new[] { 7, 8 } }
        };

        public const string AutoCenterStrength = "autoCenterStrength";
        public const string FfbEnabled = "ffbEnabled";
        public const string AbDetent = "abDetent";
        public const string IdleDetent = "idleDetent";

        // User.cfg variables of the exponential curve per in-game axis (launcher AxisName -> cfg suffix).
        public static readonly Dictionary<string, string> ExpAxes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Pitch", "PITCH" }, { "Roll", "ROLL" }, { "Yaw", "YAW" }, { "Cursor_X", "CURSOR_X" }, { "Cursor_Y", "CURSOR_Y" }
        };

        // The launcher hides the dead zone dropdown of these functions (AxisAssignWindow): one-sided axes and knobs.
        public static readonly HashSet<string> OneSided = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Throttle", "Throttle_Right", "Toe_Brake", "Toe_Brake_Right", "HUD_Brightness", "HMS_Brightness", "FLIR_Brightness",
            "Reticle_Depression", "AI_vs_IVC", "MSL_Volume", "Intercom", "COMM_Channel_1", "COMM_Channel_2", "Threat_Volume",
            "FOV", "Camera_Distance", "Altimeter_Knob", "HSI_Course_Knob", "HSI_Heading_Knob"
        };

        [GameFfbModel]
        private static FfbModel Build()
        {
            return new FfbModel
            {
                Game = Game,
                Title = "Falcon BMS",
                Description = "Game-wide (device *): Falcon BMS User.cfg lines above the launcher's override block (anti-jitter deadband, force feedback dead band, analog idle cutoff, AB detent click, virtual rudder, Depr-Ret as brake), Feedback.ini effect gains (DirectInput 0..10000) and Enable Force Feedback (pilot file <callsign>.pop byte 388). Per throttle device: the launcher's AB and IDLE detents in % of the throttle travel from idle (device file <detentPosition>). Only the primary controller (the Pitch device) plays force feedback.",
                Parameters =
                {
                    new FfbParameter { Key = FfbEnabled, Label = "Enable Force Feedback", Toggle = true, Default = 0, GameWide = true,
                        Help = "Setup > Controllers > Advanced 'Enable Force Feedback', saved in User\\Config\\<callsign>.pop (byte 388). Written only when the pilot file exists (start BMS once with that pilot)." },
                    new FfbParameter { Key = "gunGain", Label = "Gun fire effect gain", Min = 0, Max = 10000, Default = 10000, GameWide = true, Help = "Feedback.ini [Effect0] gain." },
                    new FfbParameter { Key = "bounceGain", Label = "Bounce / missile impact effect gain", Min = 0, Max = 10000, Default = 10000, GameWide = true, Help = "Feedback.ini [Effect1] gain." },
                    new FfbParameter { Key = "autoCenterGain", Label = "Auto-centre spring gain", Min = 0, Max = 10000, Default = 8000, GameWide = true, Help = "Feedback.ini [Effect2] gain." },
                    new FfbParameter { Key = AutoCenterStrength, Label = "Auto-centre spring coefficient", Min = 0, Max = 10000, Default = 10000, GameWide = true,
                        Help = "Feedback.ini [Effect2] conditionPositiveCoefficient and conditionNegativeCoefficient." },
                    new FfbParameter { Key = "runwayGain", Label = "Runway rumble effect gain", Min = 0, Max = 10000, Default = 10000, GameWide = true, Help = "Feedback.ini [Effect3] and [Effect4] gain (concrete strip)." },
                    new FfbParameter { Key = "bombDropGain", Label = "Bomb drop effect gain", Min = 0, Max = 10000, Default = 10000, GameWide = true, Help = "Feedback.ini [Effect5] and [Effect6] gain (left/right wing)." },
                    new FfbParameter { Key = "stallGain", Label = "Stall effect gain", Min = 0, Max = 10000, Default = 10000, GameWide = true, Help = "Feedback.ini [Effect7] and [Effect8] gain." },
                    new FfbParameter { Key = "ffbDeadBand", Label = "Force feedback centre dead band", Min = 0, Max = 10000, Default = 100, GameWide = true,
                        Help = "g_nForceFeedbackDeadBand: for sticks with magnetic auto-centring; 0 recommended for modern FFB sticks (Technical Manual)." },
                    new FfbParameter { Key = "flightControlsDeadband", Label = "Flight controls anti-jitter deadband", Min = 0, Max = 0.2, Default = 0.05, GameWide = true,
                        Help = "g_fFlightControlsDeadband for pitch, roll and yaw, 0..0.20, default 0.05; the Technical Manual recommends 0 and the launcher dead zone instead. Its exact effect is UNVERIFIED (not in JoyPro's curve preview)." },
                    new FfbParameter { Key = "analogIdleCutoff", Label = "Analog idle cutoff", Toggle = true, Default = 0, GameWide = true,
                        Help = "g_bUseAnalogIdleCutoff: the throttle's IDLE detent starts and stops the engine instead of the idle detent callback." },
                    new FfbParameter { Key = "abDetentClick", Label = "Click at the AB detent", Toggle = true, Default = 0, GameWide = true, Help = "g_bAfterburnerDetentClick." },
                    new FfbParameter { Key = "virtualRudder", Label = "Virtual rudder (two toe brakes)", Toggle = true, Default = 0, GameWide = true, Help = "g_bUseVirtualRudder." },
                    new FfbParameter { Key = "deprRetAsBrake", Label = "Depr-Ret axis as brake", Toggle = true, Default = 0, GameWide = true, Help = "g_bUseDeprRetAsBrake (needs the virtual rudder)." },
                    new FfbParameter { Key = AbDetent, Label = "AB detent (% of throttle travel)", Min = 0, Max = 100, Default = 100,
                        Help = "Per throttle device: the launcher's SET AB position, % of the travel from idle (100 = no detent). Device file <detentPosition><AB> (0..65536)." },
                    new FfbParameter { Key = IdleDetent, Label = "IDLE detent (% of throttle travel)", Min = 0, Max = 100, Default = 0,
                        Help = "Per throttle device: the launcher's IDLE position (right-click SET AB), % of the travel (0 = no detent). Device file <detentPosition><IDLE>." }
                }
            };
        }

        public static string Num(double value)
        {
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        // ---- Falcon BMS User.cfg ----------------------------------------------------------------------------------

        // "set g_x value" lines above the launcher's marker are set in place; a variable without a line gets one right
        // before the marker (or at the end). Lines are written in the launcher's normal form (trimmed, single spaces) so
        // its own rewrite keeps them as they are. keepExisting: a variable that already has a different value stays.
        public static bool ApplyUserCfg(ref string text, IList<KeyValuePair<string, string>> values, bool keepExisting, List<string> changes, List<string> warnings)
        {
            // The launcher writes CRLF (Environment.NewLine); a new file gets the same.
            string nl = text.Contains("\r\n") || !text.Contains("\n") ? "\r\n" : "\n";
            bool endsWithNewline = text.Length == 0 || text.EndsWith("\n", StringComparison.Ordinal);
            List<string> lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            if (endsWithNewline && lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                lines.RemoveAt(lines.Count - 1);
            int marker = lines.FindIndex(l => l.Contains(LauncherMarker));
            int end = marker < 0 ? lines.Count : marker;
            bool changed = false;
            foreach (KeyValuePair<string, string> value in values)
            {
                Regex line = new Regex(@"^\s*set\s+" + Regex.Escape(value.Key) + @"\s+(?<v>[^\s/]+)(?<rest>.*)$", RegexOptions.IgnoreCase);
                List<int> found = new List<int>();
                for (int i = 0; i < end; i++)
                {
                    if (line.IsMatch(lines[i]))
                        found.Add(i);
                }
                if (found.Count > 0)
                {
                    Match last = line.Match(lines[found[found.Count - 1]]);
                    if (SameValue(last.Groups["v"].Value, value.Value))
                        continue;
                    if (keepExisting)
                    {
                        warnings.Add(UserCfg + ": " + value.Key + " stays " + last.Groups["v"].Value + " (merge keeps what is set; JoyPro has " + value.Value + ").");
                        continue;
                    }
                    foreach (int i in found)
                    {
                        Match m = line.Match(lines[i]);
                        string rest = m.Groups["rest"].Value.Trim();
                        lines[i] = "set " + value.Key + " " + value.Value + (rest.Length > 0 ? " " + rest : string.Empty);
                    }
                }
                else
                {
                    lines.Insert(end, "set " + value.Key + " " + value.Value + " // JoyPro");
                    end++;
                    if (marker >= 0)
                        marker++;
                }
                changed = true;
                changes.Add(UserCfg + ": set " + value.Key + " " + value.Value);
            }
            if (changed)
                text = string.Join(nl, lines) + (endsWithNewline ? nl : string.Empty);
            return changed;
        }

        // The value of a variable as the cfg holds it above the marker (last line wins), or null.
        public static string ReadUserCfg(string text, string variable)
        {
            string result = null;
            Regex line = new Regex(@"^\s*set\s+" + Regex.Escape(variable) + @"\s+(?<v>[^\s/]+)", RegexOptions.IgnoreCase);
            foreach (string l in (text ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (l.Contains(LauncherMarker))
                    break;
                Match m = line.Match(l);
                if (m.Success)
                    result = m.Groups["v"].Value;
            }
            return result;
        }

        private static bool SameValue(string a, string b)
        {
            double x;
            double y;
            if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out x) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                return Math.Abs(x - y) < 1e-9;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        public static string CfgValue(string key, double value)
        {
            string variable = CfgKeys[key];
            if (variable.StartsWith("g_b", StringComparison.Ordinal))
                return value != 0 ? "1" : "0";
            if (variable.StartsWith("g_n", StringComparison.Ordinal))
                return ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
            return Math.Round(value, 4).ToString("0.0###", CultureInfo.InvariantCulture);
        }

        // ---- Feedback.ini -----------------------------------------------------------------------------------------

        // Sets "gain=" (and the auto-centre coefficients) in the [EffectN] sections in place; spacing, the trailing ";"
        // of the shipped condition lines and comments stay.
        // The gain of every effect section the file has (key -> the first section's "gain="), for the import.
        public static Dictionary<string, double> ReadFeedback(string text)
        {
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text))
                return values;
            foreach (KeyValuePair<string, int[]> key in GainKeys)
            {
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(text,
                    @"(?ims)^\s*\[Effect" + key.Value[0].ToString(CultureInfo.InvariantCulture) + @"\]\s*$(?:(?!^\s*\[).)*?^\s*gain\s*=\s*(-?\d+)");
                double number;
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    values[key.Key] = number;
            }
            return values;
        }

        public static bool ApplyFeedback(ref string text, Dictionary<string, double> values, List<string> changes, List<string> warnings)
        {
            Dictionary<int, List<KeyValuePair<string, int>>> wanted = new Dictionary<int, List<KeyValuePair<string, int>>>();
            foreach (KeyValuePair<string, double> value in values)
            {
                int number = (int)Math.Round(Math.Max(0, Math.Min(10000, value.Value)));
                int[] effects;
                if (GainKeys.TryGetValue(value.Key, out effects))
                {
                    foreach (int effect in effects)
                        Add(wanted, effect, "gain", number);
                }
                else if (string.Equals(value.Key, AutoCenterStrength, StringComparison.OrdinalIgnoreCase))
                {
                    Add(wanted, 2, "conditionPositiveCoefficient", number);
                    Add(wanted, 2, "conditionNegativeCoefficient", number);
                }
            }
            if (wanted.Count == 0)
                return false;
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = text.Split(new[] { nl }, StringSplitOptions.None).ToList();
            bool changed = false;
            foreach (KeyValuePair<int, List<KeyValuePair<string, int>>> effect in wanted.OrderBy(e => e.Key))
            {
                int start = lines.FindIndex(l => l.Trim().Equals("[Effect" + effect.Key.ToString(CultureInfo.InvariantCulture) + "]", StringComparison.OrdinalIgnoreCase));
                if (start < 0)
                {
                    warnings.Add(FeedbackIni + " has no [Effect" + effect.Key.ToString(CultureInfo.InvariantCulture) + "]; its settings were not written.");
                    continue;
                }
                int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith("[", StringComparison.Ordinal));
                if (end < 0)
                    end = lines.Count;
                foreach (KeyValuePair<string, int> key in effect.Value)
                {
                    Regex pattern = new Regex(@"^(?<pre>\s*" + Regex.Escape(key.Key) + @"\s*=\s*)(?<v>-?\d+)(?<rest>.*)$", RegexOptions.IgnoreCase);
                    int at = -1;
                    for (int i = start + 1; i < end; i++)
                    {
                        if (pattern.IsMatch(lines[i]))
                        {
                            at = i;
                            break;
                        }
                    }
                    string rendered = key.Value.ToString(CultureInfo.InvariantCulture);
                    if (at >= 0)
                    {
                        Match m = pattern.Match(lines[at]);
                        if (m.Groups["v"].Value == rendered)
                            continue;
                        lines[at] = m.Groups["pre"].Value + rendered + m.Groups["rest"].Value;
                    }
                    else
                    {
                        lines.Insert(start + 1, key.Key + "=" + rendered);
                        end++;
                    }
                    changed = true;
                    changes.Add(FeedbackIni + ": [Effect" + effect.Key.ToString(CultureInfo.InvariantCulture) + "] " + key.Key + "=" + rendered);
                }
            }
            if (changed)
                text = string.Join(nl, lines);
            return changed;
        }

        private static void Add(Dictionary<int, List<KeyValuePair<string, int>>> wanted, int effect, string key, int value)
        {
            List<KeyValuePair<string, int>> list;
            if (!wanted.TryGetValue(effect, out list))
                wanted[effect] = list = new List<KeyValuePair<string, int>>();
            list.RemoveAll(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
            list.Add(new KeyValuePair<string, int>(key, value));
        }

        // ---- pilot file ---------------------------------------------------------------------------------------------

        // The pilot file BMS uses: <callsign>.pop of the installer registry's PilotCallsign when that install's config
        // folder is this one, else the only .pop in the folder. Null when there is none or it is ambiguous.
        public static string FindPilotFile(string configPath, List<string> warnings)
        {
            string full = Path.GetFullPath(configPath).TrimEnd('\\');
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey sims = baseKey.OpenSubKey(@"SOFTWARE\Benchmark Sims"))
                    {
                        if (sims == null)
                            continue;
                        foreach (string name in sims.GetSubKeyNames())
                        {
                            using (RegistryKey install = sims.OpenSubKey(name))
                            {
                                string dir = install == null ? null : install.GetValue("baseDir") as string;
                                string callsign = install == null ? null : install.GetValue("PilotCallsign") as string;
                                if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(callsign))
                                    continue;
                                string config = Path.Combine(dir.Trim(), "User", "Config").TrimEnd('\\');
                                if (string.Equals(Path.GetFullPath(config), full, StringComparison.OrdinalIgnoreCase))
                                {
                                    string pop = Path.Combine(full, callsign.Trim() + ".pop");
                                    if (File.Exists(pop))
                                        return pop;
                                }
                            }
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            string[] pops = Directory.Exists(full) ? Directory.GetFiles(full, "*.pop") : new string[0];
            if (pops.Length == 1)
                return pops[0];
            if (pops.Length > 1)
                warnings.Add("Several pilot files (*.pop) in " + full + " and none matches the registry's PilotCallsign; Enable Force Feedback was not written.");
            return null;
        }
    }
}
