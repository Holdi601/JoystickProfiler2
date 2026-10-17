using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // Microsoft Flight Simulator X (boxed and Steam Edition) and Lockheed Martin Prepar3D v4/v5/v6: one SimControls file
    // per sim (%APPDATA%\Microsoft\FSX[-SE]\Controls\Standard.XML, %APPDATA%\Lockheed Martin\Prepar3D vN\Controls\
    // Standard.xml; the cfg's [CONTROLS] Controls_Current names the active one). Each device has a <SimControls.Map>
    // named "<DirectInput instance name>{<instance GUID>}" with <Axis> (Index = DIJOYSTATE2 slot, AxEvent, AxScale =
    // sensitivity, negative = reversed, AxNull = null zone), <Entry> (0-based button Index, Down/Up events) and <POV>
    // (a whole hat, normally PAN_VIEW) blocks; the keyboard is the KEYBOARD_MAIN map with <Key>CTRL+SHIFT+X</Key>.
    // Implemented from public user files and forum notes only: NOT YET VERIFIED on a real install (docs/games/fsx-p3d.md).
    internal sealed class FsxIntegration : IGameIntegration, IGameOwnershipTracking
    {
        public enum Sim { Fsx, FsxSe, P3D4, P3D5, P3D6 }

        private static readonly string[] AxisNames = { "XAxis", "YAxis", "ZAxis", "RxAxis", "RyAxis", "RzAxis", "Slider", "NULL" };
        private const string KeyboardMap = "KEYBOARD_MAIN";
        private static readonly Regex MapNameRegex = new Regex(@"^(?<name>.*?)\{(?<guid>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}(?<suffix>\..*)?$", RegexOptions.Compiled);

        private readonly Sim sim;

        public FsxIntegration(Sim sim)
        {
            this.sim = sim;
        }

        public string GameId
        {
            get
            {
                switch (sim)
                {
                    case Sim.Fsx: return "FSX";
                    case Sim.FsxSe: return "FSXSE";
                    case Sim.P3D4: return "P3D4";
                    case Sim.P3D5: return "P3D5";
                    default: return "P3D6";
                }
            }
        }

        public string DisplayName
        {
            get
            {
                switch (sim)
                {
                    case Sim.Fsx: return "Microsoft Flight Simulator X";
                    case Sim.FsxSe: return "Microsoft Flight Simulator X: Steam Edition";
                    case Sim.P3D4: return "Prepar3D v4";
                    case Sim.P3D5: return "Prepar3D v5";
                    default: return "Prepar3D v6";
                }
            }
        }

        public IReadOnlyList<string> Aliases
        {
            get
            {
                switch (sim)
                {
                    case Sim.Fsx: return new[] { "fsx" };
                    case Sim.FsxSe: return new[] { "fsxse", "fsx-se" };
                    case Sim.P3D4: return new[] { "p3d4", "p3dv4", "prepar3dv4" };
                    case Sim.P3D5: return new[] { "p3d5", "p3dv5", "prepar3dv5" };
                    default: return new[] { "p3d6", "p3dv6", "prepar3dv6" };
                }
            }
        }

        public string DbFolder { get { return GameId; } }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return true; } }
        public string Notes
        {
            get
            {
                return "Close the sim before exporting (it rewrites its control file on exit). Not yet verified on a real install: check the binds in the sim's Controls page after the first export.";
            }
        }
        public IReadOnlyList<string> ProcessNames { get { return IsP3D ? new[] { "Prepar3D" } : new[] { "fsx" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        // Modifiers: Ctrl/Shift/Alt on keys only.
        public GameFeature KeyboardBinds { get { return GameFeature.Yes; } }
        public GameFeature ModifierBinds { get { return GameFeature.Partial; } }
        // Every DirectInput device gets its own map; no documented limit.
        public int MaxJoysticks { get { return 16; } }

        private bool IsP3D
        {
            get { return sim == Sim.P3D4 || sim == Sim.P3D5 || sim == Sim.P3D6; }
        }

        // ---- paths ---------------------------------------------------------------------------------------------

        // The sim's folder under %APPDATA% (FSX-SE installed alone uses the FSX folder).
        public List<string> DataFolders()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            switch (sim)
            {
                case Sim.Fsx: return new List<string> { Path.Combine(appData, "Microsoft", "FSX") };
                case Sim.FsxSe: return new List<string> { Path.Combine(appData, "Microsoft", "FSX-SE"), Path.Combine(appData, "Microsoft", "FSX") };
                case Sim.P3D4: return new List<string> { Path.Combine(appData, "Lockheed Martin", "Prepar3D v4") };
                case Sim.P3D5: return new List<string> { Path.Combine(appData, "Lockheed Martin", "Prepar3D v5") };
                default: return new List<string> { Path.Combine(appData, "Lockheed Martin", "Prepar3D v6") };
            }
        }

        private IEnumerable<string> CfgNames()
        {
            if (IsP3D)
                return new[] { "Prepar3D.cfg" };
            return sim == Sim.FsxSe ? new[] { "fsx_se.cfg", "fsx.cfg" } : new[] { "fsx.cfg" };
        }

        // [CONTROLS] Controls_Current=<name> of the sim's cfg (the active control file, without .xml).
        public static string CurrentControls(string cfgPath)
        {
            if (!File.Exists(cfgPath))
                return null;
            LegacyIniFile cfg;
            return LegacyIniFile.TryLoad(cfgPath, new List<string>(), out cfg) ? cfg.Get("CONTROLS", "Controls_Current") : null;
        }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            List<string> paths = new List<string>();
            foreach (string folder in DataFolders())
            {
                string controls = Path.Combine(folder, "Controls");
                if (!Directory.Exists(controls))
                    continue;
                string current = CfgNames().Select(n => CurrentControls(Path.Combine(folder, n))).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "Standard";
                string active = Path.Combine(controls, current.Trim() + ".xml");
                if (File.Exists(active))
                    paths.Add(active);
                foreach (string xml in Directory.GetFiles(controls, "*.xml").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    if (!paths.Contains(xml, StringComparer.OrdinalIgnoreCase))
                        paths.Add(xml);
                }
                if (paths.Count > 0)
                    break;
            }
            return paths;
        }

        // ---- devices -------------------------------------------------------------------------------------------

        private sealed class MapDevice
        {
            public FsxMap Map;
            public GameDevice Device;
        }

        private static bool IsDeviceMap(FsxMap map, out string name, out Guid guid)
        {
            name = map.Name;
            guid = Guid.Empty;
            if (map.Name.StartsWith("KEYBOARD_", StringComparison.OrdinalIgnoreCase) || map.Name.StartsWith("Mouse ", StringComparison.OrdinalIgnoreCase))
                return false;
            Match m = MapNameRegex.Match(map.Name);
            if (m.Success)
            {
                if (m.Groups["suffix"].Success && m.Groups["suffix"].Value.Length > 0)
                    return false;
                name = m.Groups["name"].Value;
                Guid.TryParse(m.Groups["guid"].Value, out guid);
            }
            else if (map.Name.Contains("."))
                return false;
            // The slew maps ("<name> SLEW {GUID}") hold the slew mode's controls.
            return !Regex.IsMatch(name, @"\sSLEW\s*(#\d+)?\s*$");
        }

        // The device maps of the file, each with the connected device it belongs to (by instance GUID, else by a name
        // only one connected device has).
        private static List<MapDevice> DeviceMaps(FsxControlsFile file, GameDeviceDirectory devices, List<string> warnings)
        {
            List<MapDevice> result = new List<MapDevice>();
            foreach (FsxMap map in file.Maps)
            {
                string name;
                Guid guid;
                if (!IsDeviceMap(map, out name, out guid))
                    continue;
                GameDevice device = guid == Guid.Empty || devices == null ? null : devices.FindByInstanceGuid(guid);
                if (device == null && devices != null)
                {
                    List<GameDevice> named = devices.Devices.Where(d => string.Equals(d.InstanceName.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                    if (named.Count == 1 && !file.Maps.Any(other => other != map && MapGuid(other) == named[0].InstanceGuid))
                    {
                        device = named[0];
                        warnings?.Add("The control map '" + map.Name + "' matched '" + device.JoyProName + "' by name only (its GUID changed?).");
                    }
                }
                if (device == null)
                    warnings?.Add("Device map '" + map.Name + "' is not connected; its binds were not read.");
                result.Add(new MapDevice { Map = map, Device = device });
            }
            return result;
        }

        private static Guid MapGuid(FsxMap map)
        {
            Match m = MapNameRegex.Match(map.Name);
            Guid guid;
            return m.Success && Guid.TryParse(m.Groups["guid"].Value, out guid) ? guid : Guid.Empty;
        }

        public static string MapNameOf(GameDevice device)
        {
            return device.InstanceName + "{" + device.InstanceGuid.ToString("D").ToUpperInvariant() + "}";
        }

        // ---- keyboard ------------------------------------------------------------------------------------------

        private static readonly Dictionary<string, string> KeyNames = BuildKeyNames();
        private static readonly Dictionary<string, string> KeyNamesBack = KeyNames.GroupBy(e => e.Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, string> BuildKeyNames()
        {
            Dictionary<string, string> keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Backspace", "Backspace" }, { "Enter", "Return" }, { "Esc", "Escape" }, { "Space", "Space" }, { "Tab", "Tab" },
                { "Caps_Lock", "CapsLock" }, { "Scroll_Lock", "ScrollLock" }, { "Num_Del", "NumberPadPeriod" },
                { "VK_COMMA", "Comma" }, { "VK_DIVIDE", "NumberPadSlash" }, { "VK_LBRACKET", "LeftBracket" }, { "VK_RBRACKET", "RightBracket" },
                { "VK_MINUS", "Minus" }, { "VK_MULTIPLY", "NumberPadStar" }, { "VK_PAUSE", "Pause" }, { "VK_PERIOD", "Period" }, { "VK_PLUS", "Equals" },
                { "VK_QUOTE", "Apostrophe" }, { "VK_SEMICOLON", "Semicolon" }, { "VK_SLASH", "Slash" }, { "VK_SUBTRACT", "NumberPadMinus" },
                { "VK_ADD", "NumberPadPlus" }, { "VK_TILDE", "Grave" }, { "VK_BACKSLASH", "Backslash" }
            };
            for (char c = 'A'; c <= 'Z'; c++)
                keys[c.ToString()] = c.ToString();
            for (int i = 0; i <= 9; i++)
            {
                string d = i.ToString(CultureInfo.InvariantCulture);
                keys[d] = "D" + d;
                keys["Num_" + d] = "NumberPad" + d;
            }
            for (int i = 1; i <= 15; i++)
                keys["F" + i.ToString(CultureInfo.InvariantCulture)] = "F" + i.ToString(CultureInfo.InvariantCulture);
            return keys;
        }

        private static string ModifierFromJoyPro(string key)
        {
            switch (key)
            {
                case "LeftControl":
                case "RightControl": return "CTRL";
                case "LeftShift":
                case "RightShift": return "SHIFT";
                case "LeftAlt":
                case "RightAlt": return "ALT";
                default: return null;
            }
        }

        private static string ModifierToJoyPro(string token)
        {
            switch (token.ToUpperInvariant())
            {
                case "CTRL": return "LeftControl";
                case "SHIFT": return "LeftShift";
                case "ALT": return "LeftAlt";
                default: return null;
            }
        }

        // "CTRL+SHIFT+X" -> X with modifiers; chords of two plain keys (Num_6+Num_5) are not JoyPro inputs.
        private static bool TryDecodeKey(string text, out string key, out List<string> reformers)
        {
            key = null;
            reformers = new List<string>();
            string[] parts = (text ?? string.Empty).Split('+');
            if (parts.Length == 0)
                return false;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string modifier = ModifierToJoyPro(parts[i].Trim());
                if (modifier == null)
                    return false;
                reformers.Add(Il2LegacyInput.KeyboardReformer(modifier));
            }
            return KeyNames.TryGetValue(parts[parts.Length - 1].Trim(), out key);
        }

        private static bool TryEncodeKey(GameInputBinding binding, out string text, out string problem)
        {
            text = null;
            problem = string.Empty;
            string main;
            if (!KeyNamesBack.TryGetValue(binding.Input ?? string.Empty, out main))
            {
                problem = "the key '" + binding.Input + "' has no FSX/Prepar3D name JoyPro knows.";
                return false;
            }
            List<string> modifiers = new List<string>();
            foreach (string reformer in binding.Reformers ?? new List<string>())
            {
                string device;
                string modifierKey;
                string word = Il2LegacyInput.TrySplitReformer(reformer, out device, out modifierKey) && string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase)
                    ? ModifierFromJoyPro(modifierKey) : null;
                if (word == null)
                {
                    problem = "FSX/Prepar3D keys take Ctrl, Shift and Alt as modifiers only.";
                    return false;
                }
                modifiers.Add(word);
            }
            string[] order = { "CTRL", "SHIFT", "ALT" };
            text = string.Join("+", order.Where(modifiers.Contains).Concat(new[] { main }));
            return true;
        }

        // ---- read ----------------------------------------------------------------------------------------------

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            FsxControlsFile file;
            if (!FsxControlsFile.TryLoad(configPath, result.Errors, out file))
                return result;
            foreach (MapDevice entry in DeviceMaps(file, devices, result.Warnings).Where(m => m.Device != null))
            {
                foreach (FsxChild child in entry.Map.Children)
                {
                    GameInputBinding binding = BindingOf(child, entry.Device);
                    if (binding == null)
                        continue;
                    if (catalog.Actions.Count > 0 && !catalog.Contains(binding.ActionId))
                        result.Warnings.Add("'" + binding.ActionId + "' is not in the " + DisplayName + " catalog yet; run 'engine " + Aliases[0] + " refresh-db' to add it.");
                    result.Bindings.Add(binding);
                }
            }
            FsxMap keyboard = file.Maps.FirstOrDefault(m => string.Equals(m.Name, KeyboardMap, StringComparison.OrdinalIgnoreCase));
            if (keyboard != null)
            {
                foreach (FsxChild child in keyboard.Children.Where(c => c.Kind == "Entry" && !string.IsNullOrEmpty(c.Key) && !string.IsNullOrEmpty(c.Down)))
                {
                    string key;
                    List<string> reformers;
                    if (TryDecodeKey(child.Key, out key, out reformers))
                        result.Bindings.Add(new GameInputBinding { ActionId = child.Down, Device = "Keyboard", Input = key, Reformers = reformers });
                }
            }
            return result;
        }

        private static GameInputBinding BindingOf(FsxChild child, GameDevice device)
        {
            if (child.Index == null || string.IsNullOrEmpty(child.Event))
                return null;
            int index = child.Index.Value;
            switch (child.Kind)
            {
                case "Axis":
                    if (index < 0 || index > 7)
                        return null;
                    int scale = child.AxScale ?? FsxAxisModel.Max;
                    bool inverted = scale < 0;
                    AxisTuning tuning = new AxisTuning { Inverted = inverted };
                    if (Math.Abs(scale) != FsxAxisModel.Max || (child.AxNull ?? 0) != 0)
                    {
                        tuning.Native[FsxAxisModel.Scale] = Math.Abs(scale);
                        tuning.Native[FsxAxisModel.NullZone] = child.AxNull ?? 0;
                    }
                    return new GameInputBinding { ActionId = child.AxEvent, Device = device.JoyProName, Input = GameInputTokens.AxisToken(index), IsAxis = true, Inverted = inverted, Tuning = tuning };
                case "Entry":
                    return index < 0 ? null : new GameInputBinding { ActionId = child.Down, Device = device.JoyProName, Input = GameInputTokens.ButtonToken(index) };
                case "POV":
                    // The sim assigns the whole hat; JoyPro shows it on the hat's up direction.
                    return index < 0 ? null : new GameInputBinding { ActionId = child.Down, Device = device.JoyProName, Input = GameInputTokens.PovToken(index, 0) };
                default:
                    return null;
            }
        }

        // ---- write ---------------------------------------------------------------------------------------------

        private sealed class Wanted
        {
            public GameInputBinding Binding;
            public GameDevice Device;
            public string Kind;      // Axis, Entry, POV, Key
            public int Index;
            public string KeyText;
            public string Token;     // ownership / conflict token: "<device guid>|A3" or "KEYBOARD|CTRL+X"
        }

        private static string DeviceToken(GameDevice device)
        {
            return device.InstanceGuid.ToString("D").ToUpperInvariant();
        }

        private static string Token(string device, string kind, string index)
        {
            return device + "|" + kind.Substring(0, 1) + index;
        }

        // Events the sims repeat while the button is held (the default file marks them with <DownRpt>1</DownRpt>).
        private static bool Repeats(string ev)
        {
            string e = ev ?? string.Empty;
            return e.StartsWith("BRAKES", StringComparison.Ordinal) || e.Contains("_TRIM_") || e.StartsWith("PAN_", StringComparison.Ordinal) ||
                   e.StartsWith("ZOOM_", StringComparison.Ordinal) || e.EndsWith("_INCR", StringComparison.Ordinal) || e.EndsWith("_DECR", StringComparison.Ordinal) ||
                   e.EndsWith("_INCR_SMALL", StringComparison.Ordinal) || e.EndsWith("_DECR_SMALL", StringComparison.Ordinal) || e.EndsWith("_INC", StringComparison.Ordinal) ||
                   e.EndsWith("_DEC", StringComparison.Ordinal) || e == "ELEV_UP" || e == "ELEV_DOWN" || e == "AILERONS_LEFT" || e == "AILERONS_RIGHT" ||
                   e == "RUDDER_LEFT" || e == "RUDDER_RIGHT" || e == "PAN_VIEW";
        }

        private static string Block(string indent, string nl, string kind, IEnumerable<KeyValuePair<string, string>> fields)
        {
            string inner = indent + "    ";
            return indent + "<" + kind + ">" + nl +
                   string.Concat(fields.Select(f => inner + "<" + f.Key + ">" + FsxControlsFile.Escape(f.Value) + "</" + f.Key + ">" + nl)) +
                   indent + "</" + kind + ">" + nl;
        }

        private static List<KeyValuePair<string, string>> AxisFields(int index, string ev, int scale, int nullZone)
        {
            List<KeyValuePair<string, string>> fields = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("AxName", AxisNames[index]),
                new KeyValuePair<string, string>("Index", index.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("AxEvent", ev),
                new KeyValuePair<string, string>("AxScale", scale.ToString(CultureInfo.InvariantCulture))
            };
            if (nullZone > 0)
                fields.Add(new KeyValuePair<string, string>("AxNull", nullZone.ToString(CultureInfo.InvariantCulture)));
            return fields;
        }

        private static List<KeyValuePair<string, string>> EventFields(string indexOrKey, bool isKey, string ev)
        {
            List<KeyValuePair<string, string>> fields = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(isKey ? "Key" : "Index", indexOrKey),
                new KeyValuePair<string, string>("Down", ev)
            };
            if (Repeats(ev))
                fields.Add(new KeyValuePair<string, string>("DownRpt", "1"));
            return fields;
        }

        private string Encode(GameInputBinding binding, GameAction action, GameDeviceDirectory devices, out Wanted wanted)
        {
            wanted = new Wanted { Binding = binding };
            if (binding.IsKeyboard)
            {
                if (action.IsAxis)
                    return "an axis event needs an axis.";
                string text;
                string problem;
                if (!TryEncodeKey(binding, out text, out problem))
                    return problem;
                wanted.Kind = "Key";
                wanted.KeyText = text;
                wanted.Token = Token("KEYBOARD", "Key", text.ToUpperInvariant());
                return string.Empty;
            }
            if (binding.Reformers != null && binding.Reformers.Count > 0)
                return "FSX/Prepar3D joystick binds have no modifiers.";
            GameDevice device = devices == null ? null : devices.FindByJoyProName(binding.Device);
            if (device == null || device.InstanceGuid == Guid.Empty)
                return "device '" + binding.Device + "' is not connected, so its control map name (instance GUID) is unknown; connect it and export again.";
            JoyInput input;
            if (!GameInputTokens.TryParse(binding.Input, out input))
                return "unsupported input '" + binding.Input + "'.";
            wanted.Device = device;
            wanted.Index = input.Index;
            switch (input.Kind)
            {
                case JoyInputKind.Axis:
                    if (!action.IsAxis)
                        return "'" + action.Id + "' is a button event; bind a button.";
                    wanted.Kind = "Axis";
                    break;
                case JoyInputKind.Button:
                    if (action.IsAxis)
                        return "'" + action.Id + "' is an axis event; bind an axis.";
                    wanted.Kind = "Entry";
                    break;
                case JoyInputKind.Pov:
                    if (action.IsAxis)
                        return "'" + action.Id + "' is an axis event; bind an axis.";
                    if (!string.Equals(action.Id, "PAN_VIEW", StringComparison.OrdinalIgnoreCase))
                        return "FSX/Prepar3D assign a hat as a whole (normally to PAN_VIEW), not a direction of it.";
                    wanted.Kind = "POV";
                    break;
                default:
                    return "unsupported input '" + binding.Input + "'.";
            }
            wanted.Token = Token(DeviceToken(device), wanted.Kind, wanted.Index.ToString(CultureInfo.InvariantCulture));
            return string.Empty;
        }

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            FsxControlsFile file;
            if (!FsxControlsFile.TryLoad(request.ConfigPath, result.Errors, out file))
                return result;

            Dictionary<string, Wanted> wanted = new Dictionary<string, Wanted>(StringComparer.OrdinalIgnoreCase);
            foreach (GameInputBinding binding in request.Bindings)
            {
                GameAction action = request.Catalog.Find(binding.ActionId) ?? (request.Catalog.Actions.Count == 0 ? FsxEvents.FromId(binding.ActionId) : null);
                Wanted want = null;
                string reason = action == null ? "not an event of the " + DisplayName + " catalog." : Encode(binding, action, request.Devices, out want);
                Wanted other;
                if (reason.Length == 0 && wanted.TryGetValue(want.Token, out other))
                    reason = "the input is already used by relation '" + other.Binding.RelationName + "'.";
                if (reason.Length > 0)
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
                    continue;
                }
                want.Binding.ActionId = action.Id;
                wanted[want.Token] = want;
            }

            List<MapDevice> maps = DeviceMaps(file, request.Devices, null);
            FsxMap keyboard = file.Maps.FirstOrDefault(m => string.Equals(m.Name, KeyboardMap, StringComparison.OrdinalIgnoreCase));
            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(request.ConfigPath, result.Warnings);
            bool overwrite = request.Mode == GameExportMode.Clean || request.Mode == GameExportMode.MergeOverwrite;
            Dictionary<string, HashSet<string>> newOwned = overwrite
                ? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
                : owned.ToDictionary(e => e.Key, e => new HashSet<string>(e.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

            // Existing blocks with their tokens.
            List<KeyValuePair<string, FsxChild>> existing = new List<KeyValuePair<string, FsxChild>>();
            Dictionary<FsxChild, MapDevice> mapOf = new Dictionary<FsxChild, MapDevice>();
            foreach (MapDevice entry in maps.Where(m => m.Device != null))
            {
                foreach (FsxChild child in entry.Map.Children.Where(c => c.Index != null && (c.Kind == "Axis" || c.Kind == "Entry" || c.Kind == "POV")))
                {
                    existing.Add(new KeyValuePair<string, FsxChild>(Token(DeviceToken(entry.Device), child.Kind, child.Index.Value.ToString(CultureInfo.InvariantCulture)), child));
                    mapOf[child] = entry;
                }
            }
            if (keyboard != null)
            {
                foreach (FsxChild child in keyboard.Children.Where(c => c.Kind == "Entry" && !string.IsNullOrEmpty(c.Key)))
                {
                    string key;
                    List<string> reformers;
                    string canonical = child.Key.ToUpperInvariant();
                    if (TryDecodeKey(child.Key, out key, out reformers))
                    {
                        string text;
                        string problem;
                        if (TryEncodeKey(new GameInputBinding { Input = key, Reformers = reformers }, out text, out problem))
                            canonical = text.ToUpperInvariant();
                    }
                    existing.Add(new KeyValuePair<string, FsxChild>(Token("KEYBOARD", "Key", canonical), child));
                }
            }

            // Inputs JoyPro wrote before and no longer wants (clean, merge-overwrite), unless changed in the sim.
            if (overwrite)
            {
                foreach (KeyValuePair<string, FsxChild> e in existing)
                {
                    if (e.Key.StartsWith("KEYBOARD|", StringComparison.Ordinal) && !request.IncludeKeyboard)
                    {
                        if (GameOwnershipStore.Contains(owned, e.Value.Event ?? string.Empty, e.Key))
                            GameOwnershipStore.Add(newOwned, e.Value.Event, e.Key);
                        continue;
                    }
                    Wanted want;
                    bool still = wanted.TryGetValue(e.Key, out want) && string.Equals(want.Binding.ActionId, e.Value.Event, StringComparison.OrdinalIgnoreCase);
                    if (!still && GameOwnershipStore.Contains(owned, e.Value.Event ?? string.Empty, e.Key) && !e.Value.Removed)
                        file.Remove(e.Value);
                }
            }

            // Clean: written devices keep only the profile's binds; nuke: the other connected devices lose theirs.
            HashSet<string> usedDevices = new HashSet<string>(wanted.Values.Where(w => w.Device != null).Select(w => DeviceToken(w.Device)), StringComparer.OrdinalIgnoreCase);
            int released = 0;
            foreach (KeyValuePair<string, FsxChild> e in existing.Where(x => !x.Key.StartsWith("KEYBOARD|", StringComparison.Ordinal)))
            {
                if (e.Value.Removed || wanted.ContainsKey(e.Key))
                    continue;
                bool used = usedDevices.Contains(DeviceToken(mapOf[e.Value].Device));
                if ((used && request.Mode == GameExportMode.Clean) || (!used && request.NukeUnusedDevices))
                {
                    file.Remove(e.Value);
                    released++;
                }
            }
            if (released > 0)
                result.Steps.Add("Released " + released.ToString(CultureInfo.InvariantCulture) + " bind(s) of " + (request.Mode == GameExportMode.Clean ? "the written devices" : "unused devices") + ".");

            // The profile's binds: per device map (a new map for a device the file does not have yet).
            Dictionary<string, List<string>> appendTo = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, FsxMap> mapByDevice = new Dictionary<string, FsxMap>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, GameDevice> newMaps = new Dictionary<string, GameDevice>(StringComparer.OrdinalIgnoreCase);
            foreach (MapDevice entry in maps.Where(m => m.Device != null))
            {
                string token = DeviceToken(entry.Device);
                if (!mapByDevice.ContainsKey(token) || MapGuid(entry.Map) == entry.Device.InstanceGuid)
                    mapByDevice[token] = entry.Map;
            }
            foreach (Wanted want in wanted.Values.OrderBy(w => w.Kind == "Key" ? 1 : 0).ThenBy(w => w.Kind).ThenBy(w => w.Index))
            {
                string label = "Relation '" + want.Binding.RelationName + "' (" + want.Binding.ActionId + ")";
                List<FsxChild> same = existing.Where(e => e.Key == want.Token && !e.Value.Removed).Select(e => e.Value).ToList();
                FsxChild current = same.FirstOrDefault();
                AxisTuning tuning = want.Binding.Tuning;
                int scale = 0;
                int nullZone = 0;
                if (want.Kind == "Axis")
                {
                    scale = tuning == null ? FsxAxisModel.Max : FsxAxisModel.ScaleOf(tuning);
                    nullZone = tuning == null ? 0 : FsxAxisModel.NullOf(tuning);
                    if (want.Binding.Inverted)
                        scale = -scale;
                }
                if (current != null)
                {
                    bool sameEvent = string.Equals(current.Event, want.Binding.ActionId, StringComparison.OrdinalIgnoreCase);
                    if (!sameEvent && !overwrite)
                    {
                        result.ConflictsKept++;
                        result.BindingsSkipped++;
                        result.Warnings.Add(label + ": the input already has '" + current.Event + "'; " + (request.Mode == GameExportMode.Add ? "add" : "merge") + " keeps the sim's bind.");
                        continue;
                    }
                    if (!sameEvent)
                        result.Warnings.Add(label + " replaced '" + current.Event + "' on its input.");
                    bool rewrite = !sameEvent;
                    if (sameEvent && want.Kind == "Axis")
                    {
                        int fileScale = current.AxScale ?? FsxAxisModel.Max;
                        int fileNull = current.AxNull ?? 0;
                        bool plainDefault = Math.Abs(scale) == FsxAxisModel.Max && nullZone == 0;
                        if (fileScale != scale || fileNull != nullZone)
                        {
                            // Merge keeps sliders set in the sim when JoyPro's values are the plain default (only the direction follows).
                            if (request.Mode == GameExportMode.Merge && plainDefault)
                            {
                                if (Math.Sign(fileScale) != Math.Sign(scale))
                                {
                                    scale = -fileScale;
                                    nullZone = fileNull;
                                    rewrite = true;
                                }
                            }
                            else
                                rewrite = true;
                        }
                    }
                    if (rewrite)
                    {
                        string blockText = want.Kind == "Axis"
                            ? Block(current.Lead, file.NewLine, "Axis", AxisFields(want.Index, want.Binding.ActionId, scale, nullZone))
                            : want.Kind == "Key"
                                ? Block(current.Lead, file.NewLine, "Entry", EventFields(want.KeyText, true, want.Binding.ActionId))
                                : Block(current.Lead, file.NewLine, want.Kind, EventFields(want.Index.ToString(CultureInfo.InvariantCulture), false, want.Binding.ActionId));
                        file.Replace(current, blockText);
                    }
                    foreach (FsxChild duplicate in same.Skip(1))
                        file.Remove(duplicate);
                }
                else
                {
                    string target;
                    FsxMap map;
                    if (want.Kind == "Key")
                    {
                        target = KeyboardMap;
                        map = keyboard;
                    }
                    else
                    {
                        target = DeviceToken(want.Device);
                        mapByDevice.TryGetValue(target, out map);
                        if (map == null)
                            newMaps[target] = want.Device;
                    }
                    if (map == null && want.Kind == "Key")
                    {
                        result.BindingsSkipped++;
                        result.Warnings.Add(label + ": the file has no " + KeyboardMap + " map.");
                        continue;
                    }
                    string indent = map == null ? "        " : map.ChildIndent;
                    string blockText = want.Kind == "Axis"
                        ? Block(indent, file.NewLine, "Axis", AxisFields(want.Index, want.Binding.ActionId, scale, nullZone))
                        : want.Kind == "Key"
                            ? Block(indent, file.NewLine, "Entry", EventFields(want.KeyText, true, want.Binding.ActionId))
                            : Block(indent, file.NewLine, want.Kind, EventFields(want.Index.ToString(CultureInfo.InvariantCulture), false, want.Binding.ActionId));
                    List<string> list;
                    if (!appendTo.TryGetValue(target, out list))
                    {
                        list = new List<string>();
                        appendTo[target] = list;
                    }
                    list.Add(blockText);
                }
                GameOwnershipStore.Add(newOwned, want.Binding.ActionId, want.Token);
                result.BindingsWritten++;
            }
            foreach (KeyValuePair<string, List<string>> append in appendTo)
            {
                if (append.Key == KeyboardMap)
                    file.Append(keyboard, string.Concat(append.Value));
                else if (mapByDevice.ContainsKey(append.Key))
                    file.Append(mapByDevice[append.Key], string.Concat(append.Value));
                else
                {
                    file.AppendMap(MapNameOf(newMaps[append.Key]), string.Concat(append.Value));
                    result.Steps.Add("Added a control map for '" + newMaps[append.Key].JoyProName + "'.");
                }
            }

            if (request.DryRun)
            {
                result.Steps.Add("Dry run: " + file.ChangeCount.ToString(CultureInfo.InvariantCulture) + " block change(s) in " + Path.GetFileName(request.ConfigPath) + ".");
                return result;
            }
            GameOwnershipStore.Save(request.ConfigPath, GameId, newOwned);
            if (file.ChangeCount == 0)
            {
                result.Steps.Add("The " + DisplayName + " control file already matches the profile.");
                return result;
            }
            GameExportSafety.BackupBeforeOverwrite(request.ConfigPath, result.Warnings);
            file.Save(request.ConfigPath);
            result.FilesWritten.Add(request.ConfigPath);
            result.Steps.Add(file.ChangeCount.ToString(CultureInfo.InvariantCulture) + " block change(s) in " + Path.GetFileName(request.ConfigPath) + ".");
            return result;
        }

        public void RecordImport(string configPath, List<GameInputBinding> applied, GameDeviceDirectory devices, GameIoResult result)
        {
            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(configPath, result.Warnings);
            foreach (GameInputBinding binding in applied)
            {
                Wanted want;
                GameAction action = FsxEvents.FromId(binding.ActionId);
                action.IsAxis = binding.IsAxis;
                if (Encode(binding, action, devices, out want).Length == 0)
                    GameOwnershipStore.Add(owned, binding.ActionId, want.Token);
            }
            GameOwnershipStore.Save(configPath, GameId, owned);
        }

        // ---- catalog -------------------------------------------------------------------------------------------

        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            GameCatalog catalog = new GameCatalog { GameId = GameId };
            foreach (GameAction action in current.Actions)
                catalog.Add(action);
            int own = 0;
            foreach (GameAction action in FsxEvents.All())
            {
                if (!catalog.Contains(action.Id))
                {
                    catalog.Add(action);
                    own++;
                }
            }
            result.Steps.Add("JoyPro's own FSX/Prepar3D event list: " + own.ToString(CultureInfo.InvariantCulture) + " new event(s).");

            List<string> sources = new List<string>();
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                if (Directory.Exists(sourcePath))
                    sources.AddRange(Directory.GetFiles(sourcePath, "*.xml*"));
                else if (File.Exists(sourcePath))
                    sources.Add(sourcePath);
                else
                    result.Warnings.Add("Not found: " + sourcePath);
            }
            else
            {
                sources.AddRange(DiscoverConfigPaths(result.Warnings));
                // The sims' presets: FSX's Standard.xml in the install folder, Prepar3D's Standard.xmlx next to the user file.
                GameInstall install = GameInstalls.Find(GameId, result.Warnings);
                if (install != null && !string.IsNullOrWhiteSpace(install.InstallPath))
                    sources.Add(Path.Combine(install.InstallPath, "Standard.xml"));
                foreach (string folder in DataFolders())
                    sources.Add(Path.Combine(folder, "Controls", "Standard.xmlx"));
            }
            foreach (string path in sources.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                FsxControlsFile file;
                List<string> errors = new List<string>();
                if (!FsxControlsFile.TryLoad(path, errors, out file))
                {
                    result.Warnings.AddRange(errors);
                    continue;
                }
                int added = 0;
                foreach (FsxMap map in file.Maps)
                {
                    string name;
                    Guid guid;
                    bool deviceMap = IsDeviceMap(map, out name, out guid);
                    if (!deviceMap && !string.Equals(map.Name, KeyboardMap, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (FsxChild child in map.Children)
                    {
                        foreach (string ev in new[] { child.AxEvent, child.Down, child.Up })
                        {
                            if (string.IsNullOrWhiteSpace(ev) || !Regex.IsMatch(ev, "^[A-Z0-9_]+$") || catalog.Contains(ev))
                                continue;
                            GameAction action = FsxEvents.FromId(ev);
                            action.IsAxis = child.Kind == "Axis" && ev == child.AxEvent;
                            catalog.Add(action);
                            added++;
                        }
                    }
                }
                result.Steps.Add("Read " + path + ": " + added.ToString(CultureInfo.InvariantCulture) + " new event(s).");
            }
            return catalog;
        }
    }
}
