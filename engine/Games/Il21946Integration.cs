using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // IL-2 Sturmovik: 1946 (Steam 15320, il2fb.exe). Binds: <install>\Users\<pilot>\settings.ini, sections
    // "[HotKey <env>]" with "<trigger>[ <trigger>]=<command>" lines; the pilot is the [current] entry of Users\all.ini.
    // Joysticks are numbered by DirectInput order (JoystickDevice0..7): "AXE_X JoystickDevice0=aileron" (a leading '-'
    // on the command inverts the axis), "Joystick3 JoystickDevice1=Gear" (0-based button), "Pov90 JoystickPov4=..."
    // (pov number = device * 4 + hat). Axis response: conf.ini [rts_joystick<JoyProfile>] "1<axis>[<device>]=" 13
    // numbers (Il21946AxisModel). Verified in Steam's 4.13.4m (2026-10-12, tools/e2e/games/IL21946.ps1, docs/games/il2-1946.md).
    internal sealed class Il21946Integration : Il2LegacyIntegration
    {
        public const string Id1946 = "IL21946";
        private static readonly string[] SectionNames = { "move", "pilot", "gunner", "misc", "aircraftView", "SnapView", "PanView", "HeadMove", "orders", "timeCompression" };
        private static readonly string[] AxisSectionNames = { "move" };

        public override string GameId { get { return Id1946; } }
        public override string DisplayName { get { return "IL-2 Sturmovik: 1946"; } }
        public override IReadOnlyList<string> Aliases { get { return new[] { "il21946", "il2-1946", "1946" }; } }
        public override string DbFolder { get { return "IL21946"; } }
        public override string Notes
        {
            get
            {
                return "Close IL-2 1946 before exporting (it rewrites settings.ini and conf.ini). Joysticks are stored by Windows' device order (JoystickDevice0..3, the game reads four): " +
                       "if the game shows binds on the wrong device after unplugging, export again. Steam Cloud syncs the Users folder.";
            }
        }
        public override IReadOnlyList<string> ProcessNames { get { return new[] { "il2fb" }; } }
        // Steam's 4.13.4m reads 4 devices (it writes conf.ini responses for JoystickDevice0..3 only, checked 2026-10-12),
        // 8 axes and 128 buttons each.
        public override int MaxJoysticks { get { return 4; } }
        // One keyboard modifier on a key; no joystick button with a modifier.
        public override GameFeature ModifierBinds { get { return GameFeature.Partial; } }
        protected override IReadOnlyList<string> Sections { get { return SectionNames; } }
        protected override IReadOnlyList<string> AxisSections { get { return AxisSectionNames; } }
        protected override string CtrlWord { get { return "Ctrl"; } }

        protected override int MaxInputsPerCommand(bool axis)
        {
            return axis ? 1 : 2;
        }

        protected override string CategoryOf(string section)
        {
            switch (section)
            {
                case "move": return "Axes";
                case "pilot": return "Aircraft";
                case "gunner": return "Gunner";
                case "misc": return "Misc";
                case "aircraftView": return "Views";
                case "SnapView": return "Snap views";
                case "PanView": return "Pan views";
                case "HeadMove": return "Head movement";
                case "orders": return "Orders";
                case "timeCompression": return "Time";
                default: return section;
            }
        }

        // ---- paths ---------------------------------------------------------------------------------------------

        public override List<string> DiscoverConfigPaths(List<string> warnings)
        {
            List<string> paths = new List<string>();
            GameInstall install = GameInstalls.Find(GameId, warnings);
            if (install == null || string.IsNullOrWhiteSpace(install.InstallPath))
                return paths;
            string users = Path.Combine(install.InstallPath, "Users");
            if (!Directory.Exists(users))
                return paths;
            string current = CurrentPilotFolder(users);
            if (current.Length > 0)
            {
                string settings = Path.Combine(users, current, "settings.ini");
                if (File.Exists(settings))
                    paths.Add(settings);
            }
            foreach (string settings in Directory.GetDirectories(users).Select(d => Path.Combine(d, "settings.ini")).Where(File.Exists).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (!paths.Contains(settings, StringComparer.OrdinalIgnoreCase))
                    paths.Add(settings);
            }
            return paths;
        }

        // Users\all.ini: "[list]" lines "<folder> <name> <callsign> <surname>", "[current]" the 0-based index.
        public static string CurrentPilotFolder(string usersFolder)
        {
            string allIni = Path.Combine(usersFolder, "all.ini");
            if (!File.Exists(allIni))
                return string.Empty;
            List<string> list = new List<string>();
            int current = 0;
            string section = string.Empty;
            foreach (string raw in File.ReadAllLines(allIni, System.Text.Encoding.GetEncoding(28591)))
            {
                string line = raw.Trim();
                string name;
                if (LegacyIniFile.IsSectionHeader(line, out name))
                {
                    section = name.ToLowerInvariant();
                    continue;
                }
                if (line.Length == 0)
                    continue;
                if (section == "list")
                    list.Add(line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0]);
                else if (section == "current")
                    int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out current);
            }
            return current >= 0 && current < list.Count ? list[current] : string.Empty;
        }

        // The install folder of a settings.ini (<install>\Users\<pilot>\settings.ini).
        public static string InstallFolderOf(string settingsPath)
        {
            string pilot = Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? string.Empty;
            string users = Path.GetDirectoryName(pilot) ?? string.Empty;
            return Path.GetDirectoryName(users) ?? string.Empty;
        }

        protected override IEnumerable<string> CatalogSources(string sourcePath, List<string> warnings)
        {
            List<string> sources = new List<string>();
            string root = string.Empty;
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                if (File.Exists(sourcePath))
                {
                    sources.Add(sourcePath);
                    root = InstallFolderOf(sourcePath);
                }
                else if (Directory.Exists(sourcePath))
                    root = sourcePath;
                else
                    warnings.Add("Not found: " + sourcePath);
            }
            else
            {
                GameInstall install = GameInstalls.Find(GameId, warnings);
                root = install == null ? string.Empty : install.InstallPath ?? string.Empty;
                sources.AddRange(DiscoverConfigPaths(warnings));
            }
            if (root.Length > 0)
            {
                // The game's own template for new pilots and the defaults it reads first.
                sources.Add(Path.Combine(root, "Users", "default.ini"));
                sources.Add(Path.Combine(root, "conf.ini"));
                string users = Path.Combine(root, "Users");
                if (Directory.Exists(users) && string.IsNullOrWhiteSpace(sourcePath) == false)
                    sources.AddRange(Directory.GetDirectories(users).Select(d => Path.Combine(d, "settings.ini")));
            }
            return sources;
        }

        // ---- inputs --------------------------------------------------------------------------------------------

        private static bool TryNumber(string token, string prefix, out int value)
        {
            value = -1;
            return token.StartsWith(prefix, StringComparison.Ordinal) && token.Length > prefix.Length &&
                   int.TryParse(token.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        protected override bool TryDecodeKey(string key, GameDeviceDirectory devices, out Il2LegacyInput2 input, out string problem)
        {
            input = null;
            problem = string.Empty;
            string[] tokens = key.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens.Length > 2)
                return false;

            int device = -1;
            int pov = -1;
            foreach (string token in tokens)
            {
                int n;
                if (TryNumber(token, "JoystickDevice", out n))
                    device = n;
                else if (TryNumber(token, "JoystickPov", out n))
                    pov = n;
            }
            if (device >= 0 || pov >= 0)
            {
                string other = tokens.FirstOrDefault(t => !t.StartsWith("JoystickDevice", StringComparison.Ordinal) && !t.StartsWith("JoystickPov", StringComparison.Ordinal)) ?? string.Empty;
                int index = device >= 0 ? device : pov / 4;
                GameDevice joystick = devices == null ? null : devices.Devices.FirstOrDefault(d => d.EnumerationIndex == index);
                string fileDevice = "JoystickDevice" + index.ToString(CultureInfo.InvariantCulture);
                if (joystick == null)
                {
                    problem = DisplayName + " " + fileDevice + " (Windows device number " + (index + 1).ToString(CultureInfo.InvariantCulture) + ") is not connected; its binds were not read.";
                    return false;
                }
                input = new Il2LegacyInput2 { Device = joystick.JoyProName, FileDevice = fileDevice };
                int number;
                int direction;
                if (device >= 0 && other.StartsWith("AXE_", StringComparison.Ordinal) && Il2LegacyInput.AxisIndex(other.Substring(4)) >= 0)
                {
                    input.Input = GameInputTokens.AxisToken(Il2LegacyInput.AxisIndex(other.Substring(4)));
                    input.IsAxis = true;
                }
                else if (device >= 0 && TryNumber(other, "Joystick", out number))
                    input.Input = GameInputTokens.ButtonToken(number);
                else if (pov >= 0 && Il2LegacyInput.TryParsePovAngle(other, out direction))
                    input.Input = GameInputTokens.PovToken(pov % 4, direction);
                else
                {
                    problem = DisplayName + ": unknown joystick input '" + key + "'; kept as it is.";
                    input = null;
                    return false;
                }
                return true;
            }

            // Keyboard: "<key>" or "<modifier> <key>" (any key can be the modifier); mouse inputs are not JoyPro's.
            string main = KeyName(tokens[tokens.Length - 1]);
            if (main == null)
                return false;
            input = new Il2LegacyInput2 { Device = "Keyboard", Input = main };
            if (tokens.Length == 2)
            {
                string modifier = Il2LegacyInput.ModifierToJoyPro(tokens[0]) ?? KeyName(tokens[0]);
                if (modifier == null)
                {
                    input = null;
                    return false;
                }
                input.Reformers.Add(Il2LegacyInput.KeyboardReformer(modifier));
            }
            return true;
        }

        private static string KeyName(string token)
        {
            return Il2LegacyInput.KeyToJoyPro(token);
        }

        protected override string DeviceTokenOf(string key)
        {
            foreach (string token in key.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int n;
                if (TryNumber(token, "JoystickDevice", out n))
                    return token;
                if (TryNumber(token, "JoystickPov", out n))
                    return "JoystickDevice" + (n / 4).ToString(CultureInfo.InvariantCulture);
            }
            return string.Empty;
        }

        protected override bool TryEncodeKey(GameInputBinding binding, GameDeviceDirectory devices, LegacyIniFile file, out string key, out string fileDevice, out string problem)
        {
            key = string.Empty;
            fileDevice = string.Empty;
            problem = string.Empty;
            if (binding.IsKeyboard)
            {
                string main = Il2LegacyInput.KeyFromJoyPro(binding.Input);
                if (main == null)
                {
                    problem = "the key '" + binding.Input + "' has no " + DisplayName + " name.";
                    return false;
                }
                List<string> reformers = binding.Reformers ?? new List<string>();
                if (reformers.Count > 1)
                {
                    problem = DisplayName + " takes one modifier per key.";
                    return false;
                }
                key = main;
                if (reformers.Count == 1)
                {
                    string device;
                    string modifierKey;
                    if (!Il2LegacyInput.TrySplitReformer(reformers[0], out device, out modifierKey) || !string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase))
                    {
                        problem = DisplayName + " cannot use a joystick button as the modifier of a key.";
                        return false;
                    }
                    string word = Il2LegacyInput.ModifierFromJoyPro(modifierKey, CtrlWord) ?? Il2LegacyInput.KeyFromJoyPro(modifierKey);
                    if (word == null)
                    {
                        problem = "the modifier key '" + modifierKey + "' has no " + DisplayName + " name.";
                        return false;
                    }
                    key = word + " " + main;
                }
                return true;
            }

            if (binding.Reformers != null && binding.Reformers.Count > 0)
            {
                problem = DisplayName + " cannot combine a joystick input with a modifier.";
                return false;
            }
            GameDevice joystick = devices == null ? null : devices.FindByJoyProName(binding.Device);
            if (joystick == null || joystick.EnumerationIndex < 0)
            {
                problem = "device '" + binding.Device + "' is not connected, so its " + DisplayName + " number (JoystickDevice0..7) is unknown; connect it and export again.";
                return false;
            }
            if (joystick.EnumerationIndex >= MaxJoysticks)
            {
                problem = "device '" + binding.Device + "' is Windows game controller " + (joystick.EnumerationIndex + 1).ToString(CultureInfo.InvariantCulture) + "; " + DisplayName + " reads the first " + MaxJoysticks.ToString(CultureInfo.InvariantCulture) + ".";
                return false;
            }
            JoyInput input;
            if (!GameInputTokens.TryParse(binding.Input, out input))
            {
                problem = "unsupported input '" + binding.Input + "'.";
                return false;
            }
            fileDevice = "JoystickDevice" + joystick.EnumerationIndex.ToString(CultureInfo.InvariantCulture);
            switch (input.Kind)
            {
                case JoyInputKind.Axis:
                    // As the game writes a bind made on its Controls page ("AXE_Z JoystickDevice3=-power4"); its own
                    // defaults (Users\default.ini) have the device first, which it keeps.
                    key = "AXE_" + Il2LegacyInput.AxisNames[input.Index] + " " + fileDevice;
                    return true;
                case JoyInputKind.Button:
                    if (input.Index >= 128)
                    {
                        problem = DisplayName + " reads buttons 1..128.";
                        return false;
                    }
                    key = "Joystick" + input.Index.ToString(CultureInfo.InvariantCulture) + " " + fileDevice;
                    return true;
                case JoyInputKind.Pov:
                    if (input.Index >= 4)
                    {
                        problem = DisplayName + " reads hats 1..4.";
                        return false;
                    }
                    key = Il2LegacyInput.PovAngle(input.Direction) + " JoystickPov" + (joystick.EnumerationIndex * 4 + input.Index).ToString(CultureInfo.InvariantCulture);
                    return true;
                default:
                    problem = "unsupported input '" + binding.Input + "'.";
                    return false;
            }
        }

        protected override string DecodeValue(string section, string value, out bool inverted)
        {
            string text = (value ?? string.Empty).Trim();
            inverted = false;
            if (string.Equals(section, "move", StringComparison.OrdinalIgnoreCase) && text.StartsWith("-", StringComparison.Ordinal))
            {
                inverted = true;
                text = text.Substring(1);
            }
            return text;
        }

        protected override string EncodeValue(string section, string command, bool axis, bool inverted)
        {
            return axis && inverted ? "-" + command : command;
        }

        // ---- axis response (conf.ini) --------------------------------------------------------------------------

        protected override void OpenExtraFiles(Il2LegacyContext context, List<string> warnings)
        {
            string conf = Path.Combine(InstallFolderOf(context.ConfigPath), "conf.ini");
            LegacyIniFile file;
            if (File.Exists(conf) && LegacyIniFile.TryLoad(conf, new List<string>(), out file))
                context.ExtraFiles[conf] = file;
        }

        private static LegacyIniFile Conf(Il2LegacyContext context, out string section)
        {
            LegacyIniFile conf = context.ExtraFiles.Values.FirstOrDefault();
            section = "rts_joystick";
            if (conf == null)
                return null;
            int profile;
            if (int.TryParse(conf.Get("rts", "JoyProfile") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out profile) && profile > 0 && profile <= 7)
                section += profile.ToString(CultureInfo.InvariantCulture);
            return conf;
        }

        public static string AxisKey(int device, int axis)
        {
            return "1" + Il2LegacyInput.AxisNames[axis] + (device > 0 ? device.ToString(CultureInfo.InvariantCulture) : string.Empty);
        }

        private static bool TryDeviceAndAxis(string fileDevice, string token, out int device, out int axis)
        {
            JoyInput input;
            device = -1;
            axis = -1;
            if (!fileDevice.StartsWith("JoystickDevice", StringComparison.Ordinal) ||
                !int.TryParse(fileDevice.Substring("JoystickDevice".Length), NumberStyles.None, CultureInfo.InvariantCulture, out device) ||
                !GameInputTokens.TryParse(token, out input) || input.Kind != JoyInputKind.Axis)
                return false;
            axis = input.Index;
            return true;
        }

        // "<dead band> <c1..c10> <filter> <one-sided>"; missing numbers get the game's defaults.
        public static int[] ParseValues(string text)
        {
            int[] values = { 0, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 0, 0 };
            string[] parts = (text ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < Math.Min(13, parts.Length); i++)
            {
                int v;
                if (int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                    values[i] = v;
            }
            return values;
        }

        protected override void ReadTuning(Il2LegacyContext context, string section, string command, Il2LegacyInput2 input, GameInputBinding binding)
        {
            string confSection;
            LegacyIniFile conf = Conf(context, out confSection);
            int device;
            int axis;
            if (conf == null || !TryDeviceAndAxis(input.FileDevice, input.Input, out device, out axis))
                return;
            string text = conf.Get(confSection, AxisKey(device, axis));
            if (text == null)
                return;
            int[] values = ParseValues(text);
            AxisTuning tuning = new AxisTuning { Inverted = binding.Inverted, Slider = values[12] > 0 };
            if (!Il21946AxisModel.IsDefault(values))
                Il21946AxisModel.SetValues(tuning, values);
            binding.Tuning = tuning;
        }

        protected override void WriteTunings(Il2LegacyContext context, List<Il2LegacyWanted> axes, GameExportRequest request, GameIoResult result)
        {
            string confSection;
            LegacyIniFile conf = Conf(context, out confSection);
            if (axes.Count == 0)
                return;
            if (conf == null)
            {
                result.Warnings.Add("conf.ini was not found next to the Users folder; the axis response (dead band, curve) was not written.");
                return;
            }
            HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Il2LegacyWanted want in axes)
            {
                int device;
                int axis;
                if (want.Binding.Tuning == null || !TryDeviceAndAxis(want.FileDevice, want.Binding.Input, out device, out axis))
                    continue;
                string key = AxisKey(device, axis);
                int[] values = Il21946AxisModel.Values(want.Binding.Tuning);
                string text = string.Join(" ", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));
                string label = "Relation '" + want.Binding.RelationName + "' (" + Il2LegacyInput.Id(want.Section, want.Command) + ")";
                if (!done.Add(key))
                {
                    result.Warnings.Add(label + ": another exported command uses the same device axis; IL-2 1946 keeps one response per axis (" + key + "), the first was written.");
                    continue;
                }
                string existing = conf.Get(confSection, key);
                if (existing != null && ParseValues(existing).SequenceEqual(values))
                    continue;
                // A response made in the game stays in merge mode when JoyPro's is the plain default.
                if (existing != null && request.Mode == GameExportMode.Merge && Il21946AxisModel.IsDefault(values))
                    continue;
                conf.Set(confSection, key, text);
                result.Steps.Add(label + ": conf.ini [" + confSection + "] " + key + "=" + text + ".");
            }
        }

        // ---- catalog -------------------------------------------------------------------------------------------

        protected override IEnumerable<GameAction> OwnActions()
        {
            List<string> rows = new List<string>
            {
                "move|aileron|a|Ailerons (roll)", "move|elevator|a|Elevator (pitch)", "move|rudder|a|Rudder (yaw)", "move|power|a|Throttle",
                "move|power1|a|Throttle engine 1", "move|power2|a|Throttle engine 2", "move|power3|a|Throttle engine 3", "move|power4|a|Throttle engine 4",
                "move|pitch|a|Propeller pitch", "move|prop1|a|Propeller pitch engine 1", "move|prop2|a|Propeller pitch engine 2",
                "move|prop3|a|Propeller pitch engine 3", "move|prop4|a|Propeller pitch engine 4", "move|mixture|a|Mixture", "move|boost|a|Supercharger / boost",
                "move|radiator|a|Radiator", "move|flaps|a|Flaps", "move|brakes|a|Wheel brakes", "move|brakes_left|a|Left wheel brake", "move|brakes_right|a|Right wheel brake",
                "move|trimaileron|a|Aileron trim", "move|trimelevator|a|Elevator trim", "move|trimrudder|a|Rudder trim", "move|zoom|a|Field of view",
                "move|LeanF|a|Head lean forward/back", "move|LeanS|a|Head lean sideways", "move|Raise|a|Head raise",

                "pilot|ElevatorUp|b|Elevator up", "pilot|ElevatorDown|b|Elevator down", "pilot|AileronLeft|b|Aileron left", "pilot|AileronRight|b|Aileron right",
                "pilot|RudderLeft|b|Rudder left", "pilot|RudderRight|b|Rudder right", "pilot|AIRCRAFT_RUDDER_CENTRE|b|Rudder centre",
                "pilot|AIRCRAFT_TRIM_V_PLUS|b|Elevator trim up", "pilot|AIRCRAFT_TRIM_V_0|b|Elevator trim neutral", "pilot|AIRCRAFT_TRIM_V_MINUS|b|Elevator trim down",
                "pilot|AIRCRAFT_TRIM_H_PLUS|b|Aileron trim right", "pilot|AIRCRAFT_TRIM_H_0|b|Aileron trim neutral", "pilot|AIRCRAFT_TRIM_H_MINUS|b|Aileron trim left",
                "pilot|AIRCRAFT_TRIM_R_PLUS|b|Rudder trim right", "pilot|AIRCRAFT_TRIM_R_0|b|Rudder trim neutral", "pilot|AIRCRAFT_TRIM_R_MINUS|b|Rudder trim left",
                "pilot|AIRCRAFT_STABILIZER|b|Stabilizer", "pilot|AIRCRAFT_TOGGLE_ENGINE|b|Engine start/stop",
                "pilot|AIRCRAFT_POWER_PLUS_5|b|Throttle up 5 %", "pilot|AIRCRAFT_POWER_MINUS_5|b|Throttle down 5 %", "pilot|Power0|b|Throttle 0 %", "pilot|Power100|b|Throttle 100 %",
                "pilot|Boost|b|Boost (WEP)", "pilot|StepAuto|b|Propeller pitch auto", "pilot|StepPlus5|b|Propeller pitch up", "pilot|StepMinus5|b|Propeller pitch down",
                "pilot|MixPlus20|b|Mixture richer", "pilot|MixMinus20|b|Mixture leaner", "pilot|MagnetoPlus|b|Magneto next", "pilot|MagnetoMinus|b|Magneto previous",
                "pilot|CompressorPlus|b|Supercharger stage up", "pilot|CompressorMinus|b|Supercharger stage down", "pilot|Radiator|b|Radiator toggle",
                "pilot|EngineSelectAll|b|Select all engines", "pilot|EngineToggleAll|b|Toggle all engines", "pilot|EngineExtinguisher|b|Fire extinguisher", "pilot|EngineFeather|b|Feather propeller",
                "pilot|AIRCRAFT_FLAPS_NOTCH_UP|b|Flaps up one notch", "pilot|AIRCRAFT_FLAPS_NOTCH_DOWN|b|Flaps down one notch", "pilot|Gear|b|Gear up/down",
                "pilot|AIRCRAFT_GEAR_UP_MANUAL|b|Gear up (manual)", "pilot|AIRCRAFT_GEAR_DOWN_MANUAL|b|Gear down (manual)", "pilot|AIRCRAFT_TOGGLE_AIRBRAKE|b|Air brake",
                "pilot|Brake|b|Wheel brakes", "pilot|AIRCRAFT_TAILWHEELLOCK|b|Tail wheel lock", "pilot|AIRCRAFT_DROP_TANKS|b|Drop tanks", "pilot|AIRCRAFT_CARRIERHOOK|b|Arrestor hook",
                "pilot|AIRCRAFT_BRAKESHOE|b|Chocks", "pilot|WINGFOLD|b|Fold wings", "pilot|COCKPITDOOR|b|Canopy", "pilot|BombBayDoor|b|Bomb bay doors",
                "pilot|Weapon0|b|Fire guns (1)", "pilot|Weapon1|b|Fire cannons (2)", "pilot|Weapon2|b|Rockets / bombs (3)", "pilot|Weapon3|b|Bombs (4)", "pilot|Weapon01|b|Fire guns and cannons",
                "pilot|GunPods|b|Gun pods on/off", "pilot|SIGHT_AUTO_ONOFF|b|Bomb sight automation",

                "gunner|Fire|b|Gunner fire",
                "misc|autopilot|b|Autopilot", "misc|autopilotAuto|b|Level autopilot", "misc|ejectPilot|b|Bail out", "misc|cockpitLight|b|Cockpit light",
                "misc|toggleNavLights|b|Navigation lights", "misc|toggleLandingLight|b|Landing light", "misc|toggleSmokes|b|Smoke", "misc|showMirror|b|Mirror", "misc|chat|b|Chat",
                "aircraftView|CockpitView|b|Cockpit view", "aircraftView|CockpitShow|b|Show cockpit", "aircraftView|cockpitAim|b|Gun sight view", "aircraftView|OutsideView|b|Outside view",
                "aircraftView|NextView|b|Next view", "aircraftView|NextViewEnemy|b|Next enemy view", "aircraftView|PadlockEnemy|b|Padlock enemy", "aircraftView|PadlockFriend|b|Padlock friend",
                "aircraftView|fovSwitch|b|Field of view switch", "aircraftView|fovInc|b|Field of view wider", "aircraftView|fovDec|b|Field of view narrower",
                // Seen in Steam's 4.13.4m (Controls page / its files).
                "aircraftView|cockpitUp|b|Toggle seat position", "aircraftView|changeCockpit|b|Change cockpit", "aircraftView|PadlockView|b|Padlock view",
                "aircraftView|PadlockViewFriend|b|Padlock friendly", "aircraftView|OutsideViewFly|b|Fly-by view", "aircraftView|OutsideViewFollow|b|Chase view",
                "aircraftView|fov30|b|Field of view 30", "aircraftView|fov70|b|Field of view 70", "aircraftView|fov90|b|Field of view 90",
                "PanView|PanLeft|b|Pan left", "PanView|PanRight|b|Pan right", "PanView|PanUp|b|Pan up", "PanView|PanDown|b|Pan down", "PanView|PanReset|b|Pan reset",
                "SnapView|SnapPanSwitch|b|Snap / pan switch",
                "HeadMove|Head_UP|b|Head up", "HeadMove|Head_DOWN|b|Head down", "HeadMove|Head_LEANL|b|Lean left", "HeadMove|Head_LEANR|b|Lean right",
                "HeadMove|Head_LEANF|b|Lean forward", "HeadMove|Head_LEANB|b|Lean back", "HeadMove|Head_RET_TO_DEF|b|Head reset",
                "orders|activate|b|Radio menu", "orders|deactivate|b|Close radio menu",
                "timeCompression|timeSpeedUp|b|Time faster", "timeCompression|timeSpeedDown|b|Time slower", "timeCompression|timeSpeedNormal|b|Time normal", "timeCompression|timeSpeedPause|b|Pause"
            };
            return ParseOwnRows(rows);
        }
    }
}
