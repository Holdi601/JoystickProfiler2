using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // Project Wingman (Sector D2, Unreal Engine 4.27, no anti-cheat): a native multi-device game
    // (docs/games/project-wingman.md). Observed on the game (Anniversary Edition 2.1.1208, 2026-10-12):
    //  - Binds: %LOCALAPPDATA%\ProjectWingman\Saved\Config\WindowsNoEditor\Input.ini, [/Script/Engine.InputSettings],
    //    "ActionMappings=(ActionName="Gun Fire",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=...)" and
    //    "AxisMappings=(AxisName="Pitch Axis",Scale=1.000000,Key=...)". The game writes the whole array (plain lines, sorted)
    //    the moment a bind changes, so a plain line replaces the pak's defaults: an array the file does not have yet is
    //    filled from <install>\ProjectWingman\Content\Paks\pakchunk0-WindowsNoEditor.pak (Config/DefaultInput.ini) first.
    //  - Joystick keys come from the UE JoystickPlugin 0.9.3 (SDL2): "Joystick_<name>_<n>_Axis<a>", "..._Button<b>"
    //    ("Joystick_%s_%d_Hat%d_%s" exists but the controls menu does not take hats). <name> is the DirectInput product
    //    name without spaces ("vJoyDevice"), <n> the device's place in the game's device list (Controls > Miscellaneous),
    //    which is DirectInput's order reversed (SDL prepends each device): vJoy at DirectInput index 3 of 12 = 8.
    //    <a> counts the device's present axes in X Y Z Rx Ry Rz Slider Slider order from 0, <b> its buttons from 0.
    //    Scale -1 = the menu's Invert box. A key with a comma is written quoted.
    //  - Curves + dead zones per axis action live in SaveGames\savegame.sav (GVAS, WingmanSaveGame: PitchCurve,
    //    PitchDeadzone ... as FloatProperty, only once changed). The hat switches are not bindable: Controls >
    //    Miscellaneous "Hat Function 1/2" picks weapon or view (PovWeapon0/1, PovView0/1 in the same save).
    //  - The menu shows three key slots per action (keyboard, gamepad, mouse/joystick) and four per axis; more mappings
    //    still work but are not shown.
    internal sealed class ProjectWingmanIntegration : IGameIntegration, IGameSettingsSource
    {
        public string GameId { get { return "ProjectWingman"; } }
        public string DisplayName { get { return "Project Wingman"; } }
        public IReadOnlyList<string> Aliases { get { return new[] { "pw", "projectwingman", "wingman" }; } }
        public string DbFolder { get { return "ProjectWingman"; } }
        public bool Experimental { get { return false; } }
        public bool ExclusiveFlightSim { get { return true; } }
        public string Notes
        {
            get
            {
                return "Close the game before an export (it keeps Input.ini in memory). Turn Steam Input off for the game. " +
                       "The game names a device by its place in its device list: after adding or removing a controller, export again. " +
                       "Hat switches are set in the game (Controls > Miscellaneous > Hat Function 1/2: weapon or view).";
            }
        }
        public IReadOnlyList<string> ProcessNames { get { return new[] { "ProjectWingman-Win64-Shipping", "ProjectWingman" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        public GameFeature KeyboardBinds { get { return GameFeature.No; } }
        public GameFeature ModifierBinds { get { return GameFeature.No; } }

        // IGameSettingsSource: the curves and dead zones sit in the save game next to Input.ini's folder tree.
        public string SettingsGame { get { return GameId; } }
        public List<string> SettingsFiles(string configPath)
        {
            return new List<string> { SaveGamePath(configPath) };
        }

        internal const string SectionName = "/Script/Engine.InputSettings";
        internal const string PakDefaultInput = "ProjectWingman/Config/DefaultInput.ini";
        private const string SaveFileVariable = "JOYPRO_PW_SAVEGAME";
        private static readonly Regex JoystickKey = new Regex(@"^Joystick_(?<name>.+)_(?<n>\d+)_(?<kind>Axis|Button|Hat|Ball)(?<idx>\d+)(?:_(?<dir>\w+))?$", RegexOptions.Compiled);
        // Menu and mouse rows are not flight actions.
        private static readonly Regex NotFlight = new Regex(@"^(Menu |Mouse [XY] Axis$)", RegexOptions.Compiled);

        // Axis action -> the prefix of its curve / dead zone properties in savegame.sav and the game's defaults.
        internal static readonly Dictionary<string, string> AxisSettingPrefix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Pitch Axis", "Pitch" }, { "Roll Axis", "Roll" }, { "Thrust Axis", "Thrust" }, { "Yaw Axis", "Yaw" },
            { "View Padlock X Axis", "ViewX" }, { "View Padlock Y Axis", "ViewY" }
        };
        internal static readonly Dictionary<string, double> DefaultDeadzone = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Pitch", 0.25 }, { "Roll", 0.25 }, { "Thrust", 0.0 }, { "Yaw", 0.0 }, { "ViewX", 0.10 }, { "ViewY", 0.10 }
        };

        private static readonly Dictionary<string, string> Titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Pitch Axis", "Pitch" }, { "Roll Axis", "Roll" }, { "Yaw Axis", "Yaw" }, { "Thrust Axis", "Thrust (accelerate / brake)" },
            { "View Padlock X Axis", "Look left/right (padlock X)" }, { "View Padlock Y Axis", "Look up/down (padlock Y)" },
            { "Gun Fire", "Fire gun" }, { "Missile Launch", "Fire missile" }, { "Change Target / Padlock (Hold)", "Change target / padlock (hold)" },
            { "Weapon Cycle", "Cycle weapon" }, { "SP Module", "Special weapon (SP module)" }, { "Expand Map", "Expand map" },
            { "Change Camera", "Change camera" }, { "Hide/Unhide UI", "Hide / show HUD" }, { "Toggle Arcade Controls", "Toggle arcade controls" },
            { "Toggle Mouse Control", "Toggle mouse control" }
        };

        public static string IniPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectWingman", "Saved", "Config", "WindowsNoEditor", "Input.ini");
        }

        // <Saved>\SaveGames\savegame.sav next to the Config folder of the Input.ini (JOYPRO_PW_SAVEGAME for tests).
        public static string SaveGamePath(string configPath)
        {
            string forced = Environment.GetEnvironmentVariable(SaveFileVariable);
            if (!string.IsNullOrWhiteSpace(forced))
                return forced;
            string ini = string.IsNullOrWhiteSpace(configPath) ? IniPath() : configPath;
            DirectoryInfo windows = Directory.GetParent(Path.GetFullPath(ini));
            DirectoryInfo saved = windows != null && windows.Parent != null ? windows.Parent.Parent : null;
            return saved == null ? string.Empty : Path.Combine(saved.FullName, "SaveGames", "savegame.sav");
        }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            string path = IniPath();
            // %LOCALAPPDATA%\ProjectWingman exists after the first start (or a Steam Cloud download of the saves);
            // Input.ini only after the first change in the controls menu (the export then starts from the pak's defaults).
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectWingman");
            return File.Exists(path) || Directory.Exists(root) ? new List<string> { path } : new List<string>();
        }

        // ------------------------------------------------------------------ catalog

        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            List<PwMapping> mappings = new List<PwMapping>();
            string source = null;
            string pak = FindPak(sourcePath, result.Warnings);
            if (pak != null)
            {
                string text = ReadPakDefaults(pak, result.Errors);
                if (text != null)
                {
                    mappings.AddRange(PwIni.Parse(text).DefaultMappings());
                    source = pak;
                }
            }
            string ini = sourcePath != null && File.Exists(sourcePath) && sourcePath.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ? sourcePath : IniPath();
            PwIni user;
            if (File.Exists(ini) && PwIni.TryLoad(ini, result.Errors, out user))
            {
                mappings.AddRange(user.Mappings());
                source = source == null ? ini : source + " and " + ini;
            }
            if (mappings.Count == 0)
            {
                if (result.Errors.Count == 0)
                    result.Errors.Add("Neither the game's pak nor an Input.ini was found: install Project Wingman (or pass its folder) and start it once.");
                return null;
            }
            GameCatalog catalog = new GameCatalog { GameId = GameId };
            foreach (PwMapping m in mappings.Where(m => !NotFlight.IsMatch(m.Name)).GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                                            .OrderBy(m => m.IsAxis ? 0 : 1).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                string title;
                catalog.Add(new GameAction
                {
                    Id = m.Name,
                    Title = Titles.TryGetValue(m.Name, out title) ? title : m.Name,
                    IsAxis = m.IsAxis,
                    Category = CategoryOf(m.Name, m.IsAxis)
                });
            }
            result.Steps.Add("Read " + catalog.Actions.Count.ToString(CultureInfo.InvariantCulture) + " flight action(s) from " + source + ".");
            return catalog;
        }

        private static string CategoryOf(string name, bool axis)
        {
            if (name.StartsWith("View", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Change Camera", StringComparison.OrdinalIgnoreCase))
                return "View";
            if (axis)
                return "Flight axes";
            if (name.IndexOf("Weapon", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Fire", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Missile", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.StartsWith("SP ", StringComparison.OrdinalIgnoreCase))
                return "Weapons";
            return "Misc";
        }

        // The game's pak: a .pak file given directly, the install folder (or one given), JOYPRO install detection.
        private string FindPak(string sourcePath, List<string> warnings)
        {
            List<string> folders = new List<string>();
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                if (File.Exists(sourcePath) && sourcePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                    return sourcePath;
                if (Directory.Exists(sourcePath))
                    folders.Add(sourcePath);
            }
            if (folders.Count == 0)
            {
                GameInstall install = GameInstalls.Find(GameId, warnings);
                if (install != null && install.Installed && !string.IsNullOrWhiteSpace(install.InstallPath))
                    folders.Add(install.InstallPath);
            }
            foreach (string folder in folders)
            {
                foreach (string candidate in new[] { Path.Combine(folder, "ProjectWingman", "Content", "Paks", "pakchunk0-WindowsNoEditor.pak"), Path.Combine(folder, "Content", "Paks", "pakchunk0-WindowsNoEditor.pak"), Path.Combine(folder, "pakchunk0-WindowsNoEditor.pak") })
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            return null;
        }

        internal static string ReadPakDefaults(string pak, List<string> errors)
        {
            try
            {
                using (UnrealPak file = UnrealPak.Open(pak))
                    return file.ReadText(PakDefaultInput);
            }
            catch (Exception ex)
            {
                errors.Add("Could not read " + PakDefaultInput + " from " + pak + ": " + ex.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------ devices

        // The SDL / JoystickPlugin name of a device: the DirectInput product name without white space.
        internal static string GameName(GameDevice device)
        {
            return Regex.Replace(device.InstanceName ?? string.Empty, @"\s+", string.Empty);
        }

        // The game's device list: DirectInput game controllers without XInput pads (SDL lists those separately and the
        // game ignores them), in reverse DirectInput order.
        internal static List<GameDevice> GameOrder(GameDeviceDirectory devices, List<string> warnings)
        {
            List<GameDevice> list = new List<GameDevice>();
            foreach (GameDevice device in devices.Devices.Where(d => d.Connected).OrderBy(d => d.EnumerationIndex))
            {
                devices.EnsureCapabilities(device, warnings);
                if ((device.HidPath ?? string.Empty).IndexOf("&ig_", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                list.Add(device);
            }
            list.Reverse();
            return list;
        }

        // "Joystick_vJoyDevice_8" for a device (the part before _Axis/_Button).
        internal static string KeyPrefix(GameDevice device, List<GameDevice> order)
        {
            int n = order.IndexOf(device);
            return n < 0 ? string.Empty : "Joystick_" + GameName(device) + "_" + n.ToString(CultureInfo.InvariantCulture);
        }

        internal static string EncodeKey(GameDevice device, List<GameDevice> order, JoyInput input, List<string> warnings, out string why)
        {
            why = string.Empty;
            string prefix = KeyPrefix(device, order);
            if (prefix.Length == 0)
            {
                why = "the device is not in the game's device list (an Xbox pad?).";
                return string.Empty;
            }
            switch (input.Kind)
            {
                case JoyInputKind.Axis:
                    {
                        List<int> slots = (device.AxisSlots ?? new List<int>()).Distinct().OrderBy(s => s).ToList();
                        int a = slots.IndexOf(input.Index);
                        if (a < 0)
                        {
                            why = device.CapabilitiesKnown ? "the device has no " + GameInputTokens.AxisToken(input.Index) + "." : "the device's axes could not be read (is it connected?).";
                            return string.Empty;
                        }
                        return prefix + "_Axis" + a.ToString(CultureInfo.InvariantCulture);
                    }
                case JoyInputKind.Button:
                    return prefix + "_Button" + input.Index.ToString(CultureInfo.InvariantCulture);
                default:
                    why = "the game takes hat switches only through Controls > Miscellaneous > Hat Function 1/2 (weapon or view).";
                    return string.Empty;
            }
        }

        // A joystick key -> device + JoyPro input; false for other keys and for keys of devices that are not connected.
        internal static bool TryDecodeKey(string key, List<GameDevice> order, out GameDevice device, out string token, out string note)
        {
            device = null;
            token = string.Empty;
            note = string.Empty;
            Match match = JoystickKey.Match(UnQuote(key));
            if (!match.Success)
                return false;
            string name = match.Groups["name"].Value;
            int n = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            int index = int.Parse(match.Groups["idx"].Value, CultureInfo.InvariantCulture);
            List<GameDevice> named = order.Where(d => string.Equals(GameName(d), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count == 0)
            {
                note = "no connected device is called '" + name + "'.";
                return false;
            }
            device = n < order.Count && named.Contains(order[n]) ? order[n] : null;
            if (device == null)
            {
                if (named.Count > 1)
                {
                    note = "'" + name + "' #" + n.ToString(CultureInfo.InvariantCulture) + ": several devices have this name and the number does not fit the device list any more (a controller was added or removed).";
                    return false;
                }
                device = named[0];
                note = "'" + name + "' moved in the device list (#" + n.ToString(CultureInfo.InvariantCulture) + " -> #" + order.IndexOf(device).ToString(CultureInfo.InvariantCulture) + "); export again so the game finds it.";
            }
            switch (match.Groups["kind"].Value)
            {
                case "Axis":
                    {
                        List<int> slots = (device.AxisSlots ?? new List<int>()).Distinct().OrderBy(s => s).ToList();
                        if (index >= slots.Count)
                        {
                            note = key + ": the device has no axis " + index.ToString(CultureInfo.InvariantCulture) + ".";
                            return false;
                        }
                        token = GameInputTokens.AxisToken(slots[index]);
                        return true;
                    }
                case "Button":
                    token = GameInputTokens.ButtonToken(index);
                    return true;
                default:
                    note = key + ": hats and balls are not read.";
                    return false;
            }
        }

        private static string UnQuote(string text)
        {
            string t = (text ?? string.Empty).Trim();
            return t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"' ? t.Substring(1, t.Length - 2) : t;
        }

        // ------------------------------------------------------------------ read

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            if (!File.Exists(configPath))
            {
                result.Warnings.Add("Input.ini does not exist yet (" + configPath + "): the game has its default binds, none on a joystick.");
                return result;
            }
            PwIni ini;
            if (!PwIni.TryLoad(configPath, result.Errors, out ini))
                return result;
            List<GameDevice> order = GameOrder(devices, result.Warnings);
            Dictionary<string, PwSaveGame.Setting> settings = PwSaveGame.ReadAxisSettings(SaveGamePath(configPath), result.Warnings);
            foreach (PwMapping mapping in ini.Mappings())
            {
                GameAction action = catalog.Find(mapping.Name);
                if (action == null || !mapping.Key.StartsWith("Joystick_", StringComparison.OrdinalIgnoreCase) && !mapping.Key.StartsWith("\"Joystick_", StringComparison.OrdinalIgnoreCase))
                    continue;
                GameDevice device;
                string token;
                string note;
                if (!TryDecodeKey(mapping.Key, order, out device, out token, out note))
                {
                    if (note.Length > 0)
                        result.Warnings.Add(mapping.Name + " <- " + mapping.Key + ": " + note);
                    continue;
                }
                if (note.Length > 0)
                    result.Warnings.Add(mapping.Name + " <- " + mapping.Key + ": " + note);
                bool isAxis = token.StartsWith("JOY_", StringComparison.Ordinal) && !token.StartsWith("JOY_BTN", StringComparison.Ordinal);
                bool inverted = isAxis && mapping.Scale < 0;
                GameInputBinding binding = new GameInputBinding { ActionId = action.Id, Device = device.JoyProName, Input = token, IsAxis = isAxis, Inverted = inverted };
                string prefix;
                PwSaveGame.Setting setting;
                if (isAxis && AxisSettingPrefix.TryGetValue(action.Id, out prefix) && settings.TryGetValue(prefix, out setting))
                {
                    AxisTuning tuning = new AxisTuning { Inverted = inverted };
                    tuning.Native["curve"] = Math.Round(setting.Curve, 4);
                    tuning.Native["deadZone"] = Math.Round(setting.Deadzone, 4);
                    binding.Tuning = tuning;
                    binding.Deadzone = setting.Deadzone;
                    binding.KeepTuning = true;
                }
                result.Bindings.Add(binding);
            }
            return result;
        }

        // ------------------------------------------------------------------ write

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            PwIni ini;
            if (File.Exists(request.ConfigPath))
            {
                if (!PwIni.TryLoad(request.ConfigPath, result.Errors, out ini))
                    return result;
            }
            else
                ini = PwIni.Empty();

            // An array the file does not hold yet comes from the pak first: a plain line replaces the game's defaults.
            bool needsAction = !ini.HasArray("ActionMappings");
            bool needsAxis = !ini.HasArray("AxisMappings");
            if (needsAction || needsAxis)
            {
                string pak = FindPak(null, result.Warnings);
                string text = pak == null ? null : ReadPakDefaults(pak, result.Errors);
                if (text == null)
                {
                    result.Errors.Add("Input.ini has no " + (needsAction ? "ActionMappings" : "AxisMappings") + " yet and the game's defaults could not be read from its pak: change one bind in the game's controls menu (it then writes the list), close the game and export again.");
                    return result;
                }
                List<PwMapping> defaults = PwIni.Parse(text).DefaultMappings();
                // The Android_* keys stay: the menu shows one slot per mapping, and those are its empty slots.
                ini.Materialize(defaults.Where(m => m.IsAxis ? needsAxis : needsAction).ToList());
                result.Steps.Add("Input.ini had no " + (needsAction && needsAxis ? "binds" : needsAction ? "action list" : "axis list") + " yet: the game's defaults from its pak were written first.");
            }

            List<GameDevice> order = GameOrder(request.Devices, result.Warnings);
            // Resolve every binding to (action, key, scale).
            List<KeyValuePair<GameInputBinding, string>> resolved = new List<KeyValuePair<GameInputBinding, string>>();
            HashSet<string> writtenDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameInputBinding binding in request.Bindings)
            {
                if (binding.IsKeyboard)
                {
                    Skip(result, binding, "keyboard keys are set in the game.");
                    continue;
                }
                GameAction action = request.Catalog.Find(binding.ActionId);
                if (action == null)
                    continue;
                if (binding.Reformers != null && binding.Reformers.Count > 0)
                {
                    Skip(result, binding, "the game has no modifier buttons.");
                    continue;
                }
                GameDevice device = request.Devices.FindByJoyProName(binding.Device);
                if (device == null)
                {
                    Skip(result, binding, "device not connected (the game names devices by their place in its device list).");
                    continue;
                }
                request.Devices.EnsureCapabilities(device, result.Warnings);
                JoyInput input;
                if (!GameInputTokens.TryParse(binding.Input, out input))
                {
                    Skip(result, binding, "'" + binding.Input + "' is not a joystick input.");
                    continue;
                }
                if (action.IsAxis != (input.Kind == JoyInputKind.Axis))
                {
                    Skip(result, binding, action.IsAxis ? "an axis action needs an axis." : "a button action needs a button.");
                    continue;
                }
                string why;
                string key = EncodeKey(device, order, input, result.Warnings, out why);
                if (key.Length == 0)
                {
                    Skip(result, binding, why);
                    continue;
                }
                writtenDevices.Add(device.JoyProName);
                resolved.Add(new KeyValuePair<GameInputBinding, string>(binding, key));
            }
            // Actions with a profile bind JoyPro cannot write: which input it means is unknown, so merge-overwrite leaves
            // the game's mappings of these actions alone.
            HashSet<string> unwritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameInputBinding binding in request.Bindings.Where(b => !resolved.Any(r => r.Key == b)))
            {
                GameAction action = request.Catalog.Find(binding.ActionId);
                if (action != null && !binding.IsKeyboard)
                    unwritten.Add(action.Id);
            }

            // The game's joystick mappings JoyPro may release: flight actions of the catalog on a connected device whose
            // key the import reads (not keyboard, gamepad, mouse, hats or devices that are not connected), with the input
            // as JoyPro names it ("<device>|<input>").
            Dictionary<int, string> inputOf = new Dictionary<int, string>();
            Dictionary<int, string> deviceOf = new Dictionary<int, string>();
            List<PwMapping> mappings = ini.Mappings();
            foreach (PwMapping m in mappings.Where(m => request.Catalog.Contains(m.Name) && IsJoystickKey(m.Key)))
            {
                GameDevice device;
                string token;
                string note;
                if (!TryDecodeKey(m.Key, order, out device, out token, out note))
                    continue;
                inputOf[m.Line] = device.JoyProName + "|" + token;
                deviceOf[m.Line] = device.JoyProName;
            }
            Func<GameInputBinding, string> wantedInput = b => b.Device + "|" + b.Input;
            HashSet<string> wantedPairs = new HashSet<string>(resolved.Select(r => request.Catalog.Find(r.Key.ActionId).Id + "#" + wantedInput(r.Key)), StringComparer.OrdinalIgnoreCase);
            Func<PwMapping, bool> wantedHere = m => inputOf.ContainsKey(m.Line) && wantedPairs.Contains(m.Name + "#" + inputOf[m.Line]);
            HashSet<int> remove = new HashSet<int>();
            List<PwMapping> add = new List<PwMapping>();

            // Clean: the other joystick mappings of the devices JoyPro writes go (a flight-only game). Nuke: also those of
            // the other connected devices.
            int released = 0;
            foreach (PwMapping m in mappings.Where(m => inputOf.ContainsKey(m.Line) && !wantedHere(m)))
            {
                bool written = writtenDevices.Contains(deviceOf[m.Line]);
                if ((written && request.Mode == GameExportMode.Clean) || (!written && request.NukeUnusedDevices))
                {
                    remove.Add(m.Line);
                    released++;
                }
            }
            if (released > 0)
                result.Steps.Add(released.ToString(CultureInfo.InvariantCulture) + " joystick mapping(s) removed (" +
                                 (request.Mode == GameExportMode.Clean ? (request.NukeUnusedDevices ? "all connected devices" : "the exported devices") : "the devices the export does not use") + ").");

            // "My profile wins": an action the profile binds keeps only the profile's joystick mappings (a move fully moves).
            if (request.Mode == GameExportMode.MergeOverwrite)
            {
                HashSet<string> bound = new HashSet<string>(resolved.Select(r => request.Catalog.Find(r.Key.ActionId).Id), StringComparer.OrdinalIgnoreCase);
                foreach (PwMapping m in mappings.Where(m => inputOf.ContainsKey(m.Line) && bound.Contains(m.Name) && !unwritten.Contains(m.Name) && !wantedHere(m)))
                {
                    if (remove.Add(m.Line))
                        result.Steps.Add(m.Name + ": the game's " + UnQuote(m.Key) + " removed (the profile binds it elsewhere).");
                }
            }

            HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<GameInputBinding, string> entry in resolved)
            {
                GameInputBinding binding = entry.Key;
                GameAction action = request.Catalog.Find(binding.ActionId);
                string key = entry.Value;
                string input = wantedInput(binding);
                double scale = action.IsAxis && binding.Inverted ? -1.0 : 1.0;
                if (!done.Add(action.Id + "#" + input))
                {
                    Skip(result, binding, action.Id + " is already written on that input.");
                    continue;
                }
                // The action already on this input: the same mapping, or another form of it (Invert, or the device's older
                // place in the list) that the update modes replace and "Only add" keeps.
                List<PwMapping> same = mappings.Where(m => string.Equals(m.Name, action.Id, StringComparison.OrdinalIgnoreCase) && inputOf.ContainsKey(m.Line) && inputOf[m.Line] == input && !remove.Contains(m.Line)).ToList();
                if (same.Any(m => string.Equals(UnQuote(m.Key), key, StringComparison.OrdinalIgnoreCase) && (!action.IsAxis || Math.Sign(m.Scale) == Math.Sign(scale))))
                {
                    result.BindingsWritten++;
                    continue;
                }
                if (same.Count > 0 && request.Mode == GameExportMode.Add)
                {
                    result.ConflictsKept++;
                    Skip(result, binding, "the game already has " + action.Id + " on " + UnQuote(same[0].Key) + " (add keeps it).");
                    continue;
                }
                // A conflict: another flight action on this input that the profile does not put there.
                List<PwMapping> conflicts = mappings.Where(m => !string.Equals(m.Name, action.Id, StringComparison.OrdinalIgnoreCase) && inputOf.ContainsKey(m.Line) && inputOf[m.Line] == input &&
                                                                !wantedHere(m) && !remove.Contains(m.Line)).ToList();
                if (conflicts.Count > 0 && request.Mode == GameExportMode.Add)
                {
                    result.BindingsSkipped++;
                    result.SkippedTakenInputs.Add(new ExportSkippedBind
                    {
                        Game = GameId,
                        Device = binding.Device ?? string.Empty,
                        Input = binding.Input ?? string.Empty,
                        Relation = binding.RelationName ?? string.Empty,
                        Action = action.Id,
                        GameAction = conflicts[0].Name
                    });
                    continue;
                }
                foreach (PwMapping m in same.Concat(conflicts))
                {
                    remove.Add(m.Line);
                    if (m.Name != action.Id)
                        result.Steps.Add(m.Name + " on " + UnQuote(m.Key) + " removed: the profile puts " + action.Id + " there.");
                }
                add.Add(new PwMapping { Name = action.Id, IsAxis = action.IsAxis, Key = key, Scale = scale });
                result.BindingsWritten++;
            }

            if (remove.Count > 0)
                ini.RemoveWhere(m => remove.Contains(m.Line));
            foreach (PwMapping m in add)
                ini.Add(m);
            foreach (string name in add.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool axis = add.First(m => m.Name == name).IsAxis;
                int shown = axis ? 4 : 3;
                int count = ini.Mappings().Count(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                if (count > shown)
                    result.Warnings.Add(name + " has " + count.ToString(CultureInfo.InvariantCulture) + " mappings; the game's menu shows " + shown.ToString(CultureInfo.InvariantCulture) + " of them (all of them work).");
            }
            // Curves and dead zones of the axis actions (savegame.sav).
            Dictionary<string, PwSaveGame.Setting> axisSettings = new Dictionary<string, PwSaveGame.Setting>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<GameInputBinding, string> entry in resolved.Where(e => e.Key.Tuning != null))
            {
                string prefix;
                if (!AxisSettingPrefix.TryGetValue(entry.Key.ActionId, out prefix) || axisSettings.ContainsKey(prefix))
                    continue;
                axisSettings[prefix] = new PwSaveGame.Setting
                {
                    Curve = ProjectWingmanAxisModel.Curve(entry.Key.Tuning),
                    Deadzone = ProjectWingmanAxisModel.DeadZone(entry.Key.Tuning, prefix)
                };
            }

            if (!ini.Changed && axisSettings.Count == 0)
            {
                result.Steps.Add("Input.ini is already up to date.");
                return result;
            }
            if (request.DryRun)
            {
                result.Steps.Add("Dry run: nothing was written.");
                return result;
            }
            if (ini.Changed)
            {
                GameExportSafety.BackupBeforeOverwrite(request.ConfigPath, result.Warnings);
                string error;
                if (!ini.Save(request.ConfigPath, out error))
                {
                    result.Errors.Add(error);
                    return result;
                }
                result.FilesWritten.Add(request.ConfigPath);
            }
            else
                result.Steps.Add("Input.ini is already up to date.");
            if (axisSettings.Count > 0)
                PwSaveGame.WriteAxisSettings(SaveGamePath(request.ConfigPath), axisSettings, result);
            return result;
        }

        private static bool IsJoystickKey(string key)
        {
            return UnQuote(key).StartsWith("Joystick_", StringComparison.OrdinalIgnoreCase);
        }

        private static void Skip(GameIoResult result, GameInputBinding binding, string reason)
        {
            result.BindingsSkipped++;
            result.Warnings.Add(binding.Describe() + ": " + reason);
        }
    }

    // One ActionMappings / AxisMappings entry.
    internal sealed class PwMapping
    {
        public string Name { get; set; }
        public bool IsAxis { get; set; }
        public string Key { get; set; }
        public double Scale { get; set; }
        public int Line { get; set; }
        public string Op { get; set; }

        public PwMapping()
        {
            Name = string.Empty;
            Key = string.Empty;
            Scale = 1.0;
            Line = -1;
            Op = string.Empty;
        }

        public string ToLine()
        {
            string key = Key.IndexOf(',') >= 0 && !Key.StartsWith("\"", StringComparison.Ordinal) ? "\"" + Key + "\"" : Key;
            if (IsAxis)
                return "AxisMappings=(AxisName=\"" + Name + "\",Scale=" + Scale.ToString("0.000000", CultureInfo.InvariantCulture) + ",Key=" + key + ")";
            return "ActionMappings=(ActionName=\"" + Name + "\",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=" + key + ")";
        }

        public static bool TryParse(string line, int index, out PwMapping mapping)
        {
            mapping = null;
            string text = line.Trim();
            string op = string.Empty;
            if (text.Length > 0 && "+-.!".IndexOf(text[0]) >= 0)
            {
                op = text.Substring(0, 1);
                text = text.Substring(1);
            }
            bool axis = text.StartsWith("AxisMappings=", StringComparison.OrdinalIgnoreCase);
            if (!axis && !text.StartsWith("ActionMappings=", StringComparison.OrdinalIgnoreCase))
                return false;
            UeIniStruct value;
            if (!UeIniStruct.TryParse(text.Substring(text.IndexOf('=') + 1).Trim(), out value))
                return false;
            UeIniField name = value.Get(axis ? "AxisName" : "ActionName");
            UeIniField key = value.Get("Key");
            if (name == null || key == null)
                return false;
            double scale = 1.0;
            UeIniField scaleField = value.Get("Scale");
            if (scaleField != null)
                double.TryParse(scaleField.Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out scale);
            mapping = new PwMapping { Name = name.Text, IsAxis = axis, Key = key.Raw ?? string.Empty, Scale = scale, Line = index, Op = op };
            return true;
        }
    }

    // Input.ini kept line by line: JoyPro only removes and adds mapping lines; everything else stays.
    internal sealed class PwIni
    {
        private readonly List<string> lines = new List<string>();
        private string newline = "\r\n";
        private bool trailingNewline = true;
        private Encoding encoding = new UTF8Encoding(false);
        public bool Changed { get; private set; }

        public static PwIni Empty()
        {
            PwIni ini = new PwIni();
            ini.lines.Add("[" + ProjectWingmanIntegration.SectionName + "]");
            ini.Changed = true;
            return ini;
        }

        public static PwIni Parse(string text)
        {
            PwIni ini = new PwIni();
            ini.newline = text.Contains("\r\n") ? "\r\n" : "\n";
            ini.trailingNewline = text.EndsWith("\n", StringComparison.Ordinal);
            string[] raw = text.Replace("\r\n", "\n").Split('\n');
            int count = ini.trailingNewline ? raw.Length - 1 : raw.Length;
            for (int i = 0; i < count; i++)
                ini.lines.Add(raw[i]);
            return ini;
        }

        public static bool TryLoad(string path, List<string> errors, out PwIni ini)
        {
            ini = null;
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
            Encoding encoding = new UTF8Encoding(false);
            string text;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encoding = new UnicodeEncoding(false, true);
                text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
            else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encoding = new UTF8Encoding(true);
                text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            else
                text = Encoding.UTF8.GetString(bytes);
            ini = Parse(text);
            ini.encoding = encoding;
            return true;
        }

        // [start, end) of the InputSettings section's lines (end = next section or the end); -1 when missing.
        private void Section(out int start, out int end)
        {
            start = -1;
            end = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal))
                {
                    if (start >= 0)
                    {
                        end = i;
                        return;
                    }
                    if (string.Equals(t.Substring(1, t.Length - 2).Trim(), ProjectWingmanIntegration.SectionName, StringComparison.OrdinalIgnoreCase))
                        start = i + 1;
                }
            }
        }

        public List<PwMapping> Mappings()
        {
            List<PwMapping> list = new List<PwMapping>();
            int start, end;
            Section(out start, out end);
            if (start < 0)
                return list;
            for (int i = start; i < end; i++)
            {
                PwMapping m;
                if (PwMapping.TryParse(lines[i], i, out m) && m.Op != "-" && m.Op != "!")
                    list.Add(m);
            }
            return list;
        }

        // The pak's DefaultInput.ini with its +/- operations applied (the arrays the game starts with).
        public List<PwMapping> DefaultMappings()
        {
            List<PwMapping> list = new List<PwMapping>();
            int start, end;
            Section(out start, out end);
            if (start < 0)
                return list;
            for (int i = start; i < end; i++)
            {
                PwMapping m;
                if (!PwMapping.TryParse(lines[i], i, out m))
                    continue;
                Predicate<PwMapping> same = x => x.IsAxis == m.IsAxis && x.Name == m.Name && x.Key == m.Key && (!m.IsAxis || Math.Abs(x.Scale - m.Scale) < 1e-9);
                if (m.Op == "-")
                    list.RemoveAll(same);
                else if (m.Op == "!")
                    list.RemoveAll(x => x.IsAxis == m.IsAxis);
                else if (!list.Exists(same))
                    list.Add(m);
            }
            return list;
        }

        public bool HasArray(string name)
        {
            return Mappings().Any(m => (m.IsAxis ? "AxisMappings" : "ActionMappings") == name);
        }

        // Adds the default mappings as plain lines (the way the game writes them).
        public void Materialize(List<PwMapping> defaults)
        {
            foreach (PwMapping m in defaults.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
                Add(new PwMapping { Name = m.Name, IsAxis = m.IsAxis, Key = m.Key, Scale = m.Scale });
        }

        public int RemoveWhere(Func<PwMapping, bool> predicate)
        {
            List<int> remove = Mappings().Where(predicate).Select(m => m.Line).OrderByDescending(i => i).ToList();
            foreach (int i in remove)
                lines.RemoveAt(i);
            if (remove.Count > 0)
                Changed = true;
            return remove.Count;
        }

        // A new line after the last line of the same action, else at its sorted place in its array, else at the end of
        // the array (the array's end: the last mapping line of that kind, else the section's end).
        public void Add(PwMapping mapping)
        {
            int start, end;
            Section(out start, out end);
            if (start < 0)
            {
                lines.Insert(0, "[" + ProjectWingmanIntegration.SectionName + "]");
                start = 1;
                end = 1;
            }
            List<PwMapping> current = Mappings().Where(m => m.IsAxis == mapping.IsAxis).ToList();
            int at;
            PwMapping lastSame = current.LastOrDefault(m => string.Equals(m.Name, mapping.Name, StringComparison.OrdinalIgnoreCase));
            if (lastSame != null)
                at = lastSame.Line + 1;
            else
            {
                PwMapping next = current.FirstOrDefault(m => string.Compare(m.Name, mapping.Name, StringComparison.OrdinalIgnoreCase) > 0);
                if (next != null)
                    at = next.Line;
                else if (current.Count > 0)
                    at = current[current.Count - 1].Line + 1;
                else
                {
                    // ActionMappings before AxisMappings (the game's order), else before the section's trailing blank lines.
                    PwMapping firstAxis = mapping.IsAxis ? null : Mappings().FirstOrDefault(m => m.IsAxis);
                    at = firstAxis != null ? firstAxis.Line : end;
                    while (firstAxis == null && at - 1 >= start && lines[at - 1].Trim().Length == 0)
                        at--;
                }
            }
            lines.Insert(at, mapping.ToLine());
            Changed = true;
        }

        public bool Save(string path, out string error)
        {
            error = string.Empty;
            try
            {
                string text = string.Join(newline, lines) + (trailingNewline ? newline : string.Empty);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, text, encoding);
                    return true;
                }
                string temp = path + ".joypro-tmp";
                File.WriteAllText(temp, text, encoding);
                SafeFileIo.ReplaceFile(temp, path);
                return true;
            }
            catch (Exception ex)
            {
                error = "Input.ini could not be written: " + ex.Message;
                return false;
            }
        }
    }
}
