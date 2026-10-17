using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Games
{
    // ACE COMBAT 7: Skies Unknown (Unreal Engine 4, no anti-cheat): a native multi-device game (docs/games/ace-combat.md).
    //  - %LOCALAPPDATA%\BANDAI NAMCO Entertainment\ACE COMBAT 7\Config\Input.ini, written by the game when it starts with
    //    the devices connected: [JoystickSetting] (EnableJoystick ...), one section per device model
    //    "[Joystick-<DirectInput product GUID>]" with ProductName= and the binds "Flight_Pitch=Y:R", "Flight_Gun=Button1",
    //    "Flight_HatSwitchUp=POV_U1" (empty = unbound). Value syntax (the file's own header): object
    //    (X Y Z Rx Ry Rz Slider1 Slider2, Button1-32, POV_U/D/L/R1-4) with options ":P" positive half, ":N" negative half,
    //    ":R" reversed, ":C" -1..1 as 0..1, and combinations with & ! + > <.
    //  - The game averages the inputs of one action over all devices, so an overwrite export clears the axis it writes in
    //    the other device sections (the game's own presets for the connected sticks).
    //  - Two devices of one model (same VID/PID) share one section: the game cannot tell them apart (virtual mode helps).
    //  - Catalog: engine ac7 refresh-db reads the Flight_* names from the user's Input.ini.
    // Checked on the installed game (2026-10-12): the game writes Input.ini when it closes (not at start), one section per
    // connected DirectInput model with every Flight_* / UI_* key empty for unlicensed devices, after a
    // [JoystickMappingFormat] block of HelpComment= lines; ProductName is quoted when it has outer spaces; XInput-capable
    // devices get no section. Values JoyPro wrote survive the game's rewrite on exit. Experimental until the controls
    // menu / a flight confirmed that the game acts on them.
    internal sealed class AceCombat7Integration : IGameIntegration
    {
        public string GameId { get { return "AceCombat7"; } }
        public string DisplayName { get { return "ACE COMBAT 7: Skies Unknown"; } }
        public IReadOnlyList<string> Aliases { get { return new[] { "ac7", "acecombat7" }; } }
        public string DbFolder { get { return "AceCombat7"; } }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return true; } }
        public string Notes
        {
            get
            {
                return "Experimental. Start the game once with every device connected (it writes one section per device into Input.ini), close it, then export. " +
                       "Turn Steam Input off for the game. The game averages one action over all devices: an overwrite export clears that axis on the other devices.";
            }
        }
        public IReadOnlyList<string> ProcessNames { get { return new[] { "Ace7Game", "Ace7Game-Win64-Shipping" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        public GameFeature KeyboardBinds { get { return GameFeature.No; } }
        public GameFeature ModifierBinds { get { return GameFeature.No; } }

        private const string ActionPrefix = "Flight_";
        private const string SectionPrefix = "Joystick-";
        private static readonly string[] AxisNames = { "X", "Y", "Z", "Rx", "Ry", "Rz", "Slider1", "Slider2" };
        private static readonly string[] PovNames = { "U", "", "R", "", "D", "", "L", "" };
        private static readonly HashSet<string> AxisActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Flight_Pitch", "Flight_Roll", "Flight_Yaw", "Flight_Throttle", "Flight_CameraPitch", "Flight_CameraYaw"
        };
        private static readonly Dictionary<string, string> Titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Flight_Pitch", "Pitch" }, { "Flight_Roll", "Roll" }, { "Flight_Yaw", "Yaw" }, { "Flight_Throttle", "Throttle" },
            { "Flight_CameraPitch", "Look up/down" }, { "Flight_CameraYaw", "Look left/right" }, { "Flight_Gun", "Fire gun" },
            { "Flight_Missile", "Fire missile / special weapon" }, { "Flight_Target", "Switch target" }, { "Flight_Weapon", "Change weapon" },
            { "Flight_Radar", "Radar map" }, { "Flight_Flare", "Flares" }, { "Flight_View", "Change view" },
            { "Flight_AccelerationDeceleration", "Throttle (accelerate / brake as one input)" }, { "Flight_AutoPilot", "Autopilot" }, { "Flight_Pause", "Pause" },
            { "Flight_HatSwitchUp", "Hat up" }, { "Flight_HatSwitchDown", "Hat down" }, { "Flight_HatSwitchLeft", "Hat left" }, { "Flight_HatSwitchRight", "Hat right" }
        };

        public static string IniPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BANDAI NAMCO Entertainment", "ACE COMBAT 7", "Config", "Input.ini");
        }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            string path = IniPath();
            return File.Exists(path) ? new List<string> { path } : new List<string>();
        }

        // ------------------------------------------------------------------ catalog

        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            string path = string.IsNullOrWhiteSpace(sourcePath) || Directory.Exists(sourcePath) ? IniPath() : sourcePath;
            Ac7Ini ini;
            if (!File.Exists(path) || !Ac7Ini.TryLoad(path, result.Errors, out ini))
            {
                if (result.Errors.Count == 0)
                    result.Errors.Add("Input.ini not found (" + path + "): start ACE COMBAT 7 once with your devices connected.");
                return null;
            }
            GameCatalog catalog = new GameCatalog { GameId = GameId };
            foreach (string key in ini.Sections.Where(s => s.Name.StartsWith(SectionPrefix, StringComparison.OrdinalIgnoreCase))
                                               .SelectMany(s => s.Keys()).Where(k => k.StartsWith(ActionPrefix, StringComparison.OrdinalIgnoreCase))
                                               .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string title;
                catalog.Add(new GameAction
                {
                    Id = key,
                    Title = Titles.TryGetValue(key, out title) ? title : key.Substring(ActionPrefix.Length),
                    IsAxis = AxisActions.Contains(key),
                    Category = key.StartsWith("Flight_HatSwitch", StringComparison.OrdinalIgnoreCase) ? "Hat" : AxisActions.Contains(key) ? "Flight axes" : "Flight"
                });
            }
            if (catalog.Actions.Count == 0)
            {
                result.Errors.Add(path + " has no device section with Flight_ binds yet: start ACE COMBAT 7 once with your devices connected.");
                return null;
            }
            result.Steps.Add("Read " + catalog.Actions.Count.ToString(CultureInfo.InvariantCulture) + " flight action(s) from " + path + ".");
            return catalog;
        }

        // ------------------------------------------------------------------ inputs

        internal static string Encode(JoyInput input, bool inverted)
        {
            switch (input.Kind)
            {
                case JoyInputKind.Axis:
                    return AxisNames[input.Index] + (inverted ? ":R" : string.Empty);
                case JoyInputKind.Button:
                    return input.Index < 32 ? "Button" + (input.Index + 1).ToString(CultureInfo.InvariantCulture) : string.Empty;
                case JoyInputKind.Pov:
                    return input.Index < 4 && PovNames[input.Direction].Length > 0 ? "POV_" + PovNames[input.Direction] + (input.Index + 1).ToString(CultureInfo.InvariantCulture) : string.Empty;
                default:
                    return string.Empty;
            }
        }

        // "Y", "Y:R", "Button3", "POV_U1" -> JoyPro token; combinations and half axes are not read.
        internal static bool TryDecode(string value, out string token, out bool inverted)
        {
            token = string.Empty;
            inverted = false;
            string text = (value ?? string.Empty).Trim();
            Match match = Regex.Match(text, @"^(?<obj>[A-Za-z_]+?)(?<num>\d*)(:(?<opt>[PNRC]+))?$");
            if (!match.Success)
                return false;
            string obj = match.Groups["obj"].Value;
            string num = match.Groups["num"].Value;
            string options = match.Groups["opt"].Value.ToUpperInvariant();
            if (options.Contains("P") || options.Contains("N") || options.Contains("C"))
                return false;
            inverted = options.Contains("R");
            int number;
            if (string.Equals(obj, "Button", StringComparison.OrdinalIgnoreCase) && int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 1)
            {
                token = GameInputTokens.ButtonToken(number - 1);
                return true;
            }
            if (obj.StartsWith("POV_", StringComparison.OrdinalIgnoreCase) && int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 1)
            {
                int direction = Array.FindIndex(PovNames, p => p.Length > 0 && string.Equals(p, obj.Substring(4), StringComparison.OrdinalIgnoreCase));
                if (direction < 0)
                    return false;
                token = GameInputTokens.PovToken(number - 1, direction);
                return true;
            }
            int axis = Array.FindIndex(AxisNames, a => string.Equals(a, obj + num, StringComparison.OrdinalIgnoreCase));
            if (axis < 0)
                return false;
            token = GameInputTokens.AxisToken(axis);
            return true;
        }

        private static string SectionOf(GameDevice device)
        {
            return SectionPrefix + device.ProductGuid.ToString("D").ToUpperInvariant();
        }

        // ------------------------------------------------------------------ read

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            Ac7Ini ini;
            if (!Ac7Ini.TryLoad(configPath, result.Errors, out ini))
                return result;
            foreach (Ac7Section section in ini.Sections.Where(s => s.Name.StartsWith(SectionPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                Guid product;
                if (!Guid.TryParse(section.Name.Substring(SectionPrefix.Length), out product))
                    continue;
                List<GameDevice> owners = devices.Devices.Where(d => d.ProductGuid == product && !string.IsNullOrEmpty(d.JoyProName)).ToList();
                if (owners.Count == 0)
                {
                    result.Warnings.Add("[" + section.Name + "] (" + section.ProductName() + "): no such device is connected; its binds were not read.");
                    continue;
                }
                if (owners.Count > 1)
                    result.Warnings.Add("[" + section.Name + "]: " + owners.Count.ToString(CultureInfo.InvariantCulture) + " devices of this model share one section in the game; read for " + owners[0].JoyProName + ".");
                foreach (GameAction action in catalog.Actions)
                {
                    string value = section.Get(action.Id);
                    if (string.IsNullOrWhiteSpace(value))
                        continue;
                    string token;
                    bool inverted;
                    if (!TryDecode(value, out token, out inverted))
                    {
                        result.Warnings.Add(action.Id + "=" + value + " on " + owners[0].JoyProName + ": a combination or half axis JoyPro does not read.");
                        continue;
                    }
                    bool isAxis = !token.StartsWith("JOY_BTN", StringComparison.Ordinal);
                    result.Bindings.Add(new GameInputBinding { ActionId = action.Id, Device = owners[0].JoyProName, Input = token, IsAxis = isAxis, Inverted = isAxis && inverted });
                }
            }
            return result;
        }

        // ------------------------------------------------------------------ write

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            Ac7Ini ini;
            if (!Ac7Ini.TryLoad(request.ConfigPath, result.Errors, out ini))
                return result;
            bool overwrite = request.Mode == GameExportMode.MergeOverwrite || request.Mode == GameExportMode.Clean;
            List<Ac7Section> deviceSections = ini.Sections.Where(s => s.Name.StartsWith(SectionPrefix, StringComparison.OrdinalIgnoreCase)).ToList();

            // The binds per section (one section per device model).
            Dictionary<string, List<GameInputBinding>> bySection = new Dictionary<string, List<GameInputBinding>>(StringComparer.OrdinalIgnoreCase);
            foreach (GameInputBinding binding in request.Bindings)
            {
                if (binding.IsKeyboard)
                {
                    Skip(result, binding, "keyboard keys are set in the game.");
                    continue;
                }
                GameDevice device = request.Devices.FindByJoyProName(binding.Device);
                if (device == null || device.ProductGuid == Guid.Empty)
                {
                    Skip(result, binding, "device not connected (JoyPro needs its model id for the Input.ini section).");
                    continue;
                }
                string name = SectionOf(device);
                if (ini.Find(name) == null)
                {
                    Skip(result, binding, "Input.ini has no section [" + name + "] yet: start the game once with this device connected.");
                    continue;
                }
                List<GameInputBinding> list;
                if (!bySection.TryGetValue(name, out list))
                    bySection[name] = list = new List<GameInputBinding>();
                list.Add(binding);
            }

            if (request.Mode == GameExportMode.Clean)
            {
                // Clean: the flight actions of the sections JoyPro writes start empty.
                foreach (string name in bySection.Keys)
                {
                    Ac7Section section = ini.Find(name);
                    foreach (GameAction action in request.Catalog.Actions)
                        if (section.Has(action.Id))
                            section.Set(action.Id, string.Empty);
                }
            }

            HashSet<string> written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<GameInputBinding>> entry in bySection)
            {
                Ac7Section section = ini.Find(entry.Key);
                foreach (GameInputBinding binding in entry.Value)
                {
                    if (binding.Reformers != null && binding.Reformers.Count > 0)
                    {
                        Skip(result, binding, "modifier combinations are not written (the game's & syntax works per device only).");
                        continue;
                    }
                    JoyInput input;
                    if (!GameInputTokens.TryParse(binding.Input, out input))
                    {
                        Skip(result, binding, "'" + binding.Input + "' is not a joystick input.");
                        continue;
                    }
                    GameAction action = request.Catalog.Find(binding.ActionId);
                    if (action == null)
                        continue;
                    if (action.IsAxis != (input.Kind == JoyInputKind.Axis))
                    {
                        Skip(result, binding, action.IsAxis ? "an axis action needs an axis." : "a button action needs a button or hat direction.");
                        continue;
                    }
                    string value = Encode(input, binding.Inverted);
                    if (value.Length == 0)
                    {
                        Skip(result, binding, "the game reads buttons 1-32, hats 1-4 and only the four main hat directions.");
                        continue;
                    }
                    string key = entry.Key + "|" + action.Id;
                    string existing = section.Get(action.Id);
                    if (!written.Contains(key) && !overwrite && !string.IsNullOrWhiteSpace(existing) && !string.Equals(existing.Trim(), value, StringComparison.OrdinalIgnoreCase))
                    {
                        result.ConflictsKept++;
                        Skip(result, binding, "the game already has " + action.Id + "=" + existing + " on this device (merge keeps it).");
                        continue;
                    }
                    if (written.Contains(key))
                    {
                        Skip(result, binding, action.Id + " is already written for this device; one input per action and device.");
                        continue;
                    }
                    section.Set(action.Id, value);
                    written.Add(key);
                    result.BindingsWritten++;
                    // The game averages one axis over all devices: the other sections give it up.
                    if (overwrite && action.IsAxis)
                    {
                        foreach (Ac7Section other in deviceSections.Where(s => !string.Equals(s.Name, entry.Key, StringComparison.OrdinalIgnoreCase)))
                        {
                            if (!string.IsNullOrWhiteSpace(other.Get(action.Id)) && !bySection.ContainsKey(other.Name))
                            {
                                result.Steps.Add(action.Id + " cleared on [" + other.Name + "] (" + other.ProductName() + "): the game would average it with " + binding.Device + ".");
                                other.Set(action.Id, string.Empty);
                            }
                        }
                    }
                }
            }

            if (!ini.Changed)
            {
                result.Steps.Add("Input.ini is already up to date.");
                return result;
            }
            if (request.DryRun)
            {
                result.Steps.Add("Dry run: Input.ini was not written.");
                return result;
            }
            GameExportSafety.BackupBeforeOverwrite(request.ConfigPath, result.Warnings);
            string error;
            if (!ini.Save(request.ConfigPath, out error))
            {
                result.Errors.Add(error);
                return result;
            }
            result.FilesWritten.Add(request.ConfigPath);
            return result;
        }

        private static void Skip(GameIoResult result, GameInputBinding binding, string reason)
        {
            result.BindingsSkipped++;
            result.Warnings.Add(binding.Describe() + ": " + reason);
        }
    }

    // Input.ini kept line by line: only the values JoyPro sets change; comments, order and line ends stay.
    internal sealed class Ac7Ini
    {
        private readonly List<string> lines = new List<string>();
        private string newline = "\r\n";
        private bool trailingNewline = true;
        private Encoding encoding = new UTF8Encoding(false);
        public readonly List<Ac7Section> Sections = new List<Ac7Section>();
        public bool Changed { get; set; }

        public static bool TryLoad(string path, List<string> errors, out Ac7Ini ini)
        {
            ini = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                errors.Add("Input.ini not found: " + path);
                return false;
            }
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                errors.Add("Input.ini could not be read: " + ex.Message);
                return false;
            }
            Ac7Ini result = new Ac7Ini();
            string text;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                result.encoding = new UnicodeEncoding(false, true);
                text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
            else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                result.encoding = new UTF8Encoding(true);
                text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            else
                text = Encoding.UTF8.GetString(bytes);
            result.newline = text.Contains("\r\n") ? "\r\n" : "\n";
            result.trailingNewline = text.EndsWith("\n", StringComparison.Ordinal);
            string[] raw = text.Replace("\r\n", "\n").Split('\n');
            int count = result.trailingNewline ? raw.Length - 1 : raw.Length;
            Ac7Section currentSection = null;
            for (int i = 0; i < count; i++)
            {
                result.lines.Add(raw[i]);
                string line = raw[i].Trim();
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    currentSection = new Ac7Section(result, line.Substring(1, line.Length - 2).Trim(), i);
                    result.Sections.Add(currentSection);
                }
                else if (currentSection != null)
                    currentSection.LastLine = i;
            }
            ini = result;
            return true;
        }

        internal List<string> Lines { get { return lines; } }

        public Ac7Section Find(string name)
        {
            return Sections.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        internal void InsertLine(int index, string text)
        {
            lines.Insert(index, text);
            foreach (Ac7Section section in Sections)
            {
                if (section.HeaderLine >= index)
                    section.HeaderLine++;
                if (section.LastLine >= index)
                    section.LastLine++;
            }
        }

        public bool Save(string path, out string error)
        {
            error = string.Empty;
            try
            {
                string text = string.Join(newline, lines) + (trailingNewline ? newline : string.Empty);
                string temp = path + ".joypro-tmp";
                File.WriteAllText(temp, text, encoding);
                File.Replace(temp, path, null);
                return true;
            }
            catch (Exception ex)
            {
                error = "Input.ini could not be written: " + ex.Message;
                return false;
            }
        }
    }

    internal sealed class Ac7Section
    {
        private readonly Ac7Ini owner;
        public string Name { get; private set; }
        public int HeaderLine { get; set; }
        public int LastLine { get; set; }

        public Ac7Section(Ac7Ini owner, string name, int header)
        {
            this.owner = owner;
            Name = name;
            HeaderLine = header;
            LastLine = header;
        }

        private int LineOf(string key)
        {
            for (int i = HeaderLine + 1; i <= LastLine && i < owner.Lines.Count; i++)
            {
                string line = owner.Lines[i];
                int eq = line.IndexOf('=');
                if (eq > 0 && string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        public IEnumerable<string> Keys()
        {
            for (int i = HeaderLine + 1; i <= LastLine && i < owner.Lines.Count; i++)
            {
                string line = owner.Lines[i];
                int eq = line.IndexOf('=');
                if (eq > 0 && !line.TrimStart().StartsWith(";", StringComparison.Ordinal))
                    yield return line.Substring(0, eq).Trim();
            }
        }

        public bool Has(string key)
        {
            return LineOf(key) >= 0;
        }

        // The DirectInput product name; the game quotes it when it has outer spaces (" VKB-Sim Gunfighter ... ").
        public string ProductName()
        {
            string value = Get("ProductName");
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                value = value.Substring(1, value.Length - 2);
            return value.Trim();
        }

        public string Get(string key)
        {
            int index = LineOf(key);
            if (index < 0)
                return string.Empty;
            string line = owner.Lines[index];
            return line.Substring(line.IndexOf('=') + 1).Trim();
        }

        public void Set(string key, string value)
        {
            int index = LineOf(key);
            string text = key + "=" + value;
            if (index >= 0)
            {
                if (owner.Lines[index] != text)
                {
                    owner.Lines[index] = text;
                    owner.Changed = true;
                }
                return;
            }
            // A key the section does not have yet: after its last key line.
            int at = LastLine + 1;
            while (at - 1 > HeaderLine && owner.Lines[at - 1].Trim().Length == 0)
                at--;
            owner.InsertLine(at, text);
            if (LastLine < at)
                LastLine = at;
            owner.Changed = true;
        }
    }
}
