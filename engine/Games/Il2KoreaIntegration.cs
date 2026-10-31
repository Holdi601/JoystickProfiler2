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
    // IL-2 Series: Korea keeps its key mapping as JSON in <install>\data\input\<profile>.actions (profile "General" =
    // general.actions): {"devices":[{deviceId, model, ident, name}], "modifiers":[...], "actions":{"gear":["key_g",
    // "dev2_b16", "-dev2_axis_s", "key_down/key_up"]}}. A joystick token names the device by its device number
    // ("dev2"); known.devices.json next to it gives each DirectInput device (instance GUID, IL-2 byte order) its
    // number, -1 = none. JoyPro gives a device it writes a free number itself, like the game's "assign device number"
    // dialog. current.map is compiled by the game from these files. See docs/games/il2-korea.md.
    internal sealed class Il2KoreaIntegration : IGameIntegration, IGameOwnershipTracking, IGameConfigPathResolver
    {
        public string GameId { get { return "IL2Korea"; } }
        public string DisplayName { get { return "IL-2 Series: Korea"; } }
        public IReadOnlyList<string> Aliases { get { return new[] { "il2korea", "korea", "il2series", "il2k" }; } }
        public string DbFolder { get { return "IL2Korea"; } }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return true; } }
        public string Notes { get { return "Experimental. JoyPro writes the game's key mapping profile (data\\input\\general.actions) and gives every device it writes a device number in known.devices.json. Close the game before exporting; it reads both files on start."; } }
        public IReadOnlyList<string> ProcessNames { get { return new[] { "IL2Series" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        public GameFeature KeyboardBinds { get { return GameFeature.Yes; } }
        public GameFeature ModifierBinds { get { return GameFeature.Yes; } }

        private const string KnownDevicesFile = "known.devices.json";
        private static readonly string[] AxisLetters = { "x", "y", "z", "w", "s", "t", "p", "q" };
        private static readonly Regex DeviceToken = new Regex(@"^dev(?<id>\d+)_(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            List<string> paths = new List<string>();
            GameInstall install = GameInstalls.Find(GameId, warnings);
            if (install == null || string.IsNullOrWhiteSpace(install.InstallPath))
                return paths;
            string input = InputFolder(install.InstallPath);
            if (input == null)
            {
                if (install.Installed)
                    warnings?.Add("IL-2 Korea: no data\\input folder in " + install.InstallPath + ". Start the game once (Settings > Key mapping, ACCEPT) so it writes its key mapping.");
                return paths;
            }
            string general = Path.Combine(input, "general.actions");
            if (File.Exists(general))
                paths.Add(general);
            paths.AddRange(Directory.GetFiles(input, "*.actions")
                .Where(p => !string.Equals(p, general, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc));
            if (paths.Count == 0)
                warnings?.Add("IL-2 Korea has no key mapping file yet (" + general + "). Start the game, open Settings > Key mapping and press ACCEPT once; then JoyPro can import and export.");
            return paths;
        }

        // The key mapping folder of an installation: <game folder>\data\input (Steam: steamapps\common\IL2Series,
        // standalone: <launcher folder>\game). Null when there is none.
        internal static string InputFolder(string installFolder)
        {
            if (string.IsNullOrWhiteSpace(installFolder))
                return null;
            foreach (string candidate in new[] { Path.Combine(installFolder, "data", "input"), Path.Combine(installFolder, "game", "data", "input") })
            {
                if (Directory.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        // A key mapping file given as a folder (the installation, the standalone launcher folder, data or data\input)
        // or as the game's / launcher's executable: that folder's general.actions (or its newest *.actions). Anything
        // else (a file, a path that does not exist yet) stays as it is.
        public string ResolveConfigPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;
            string trimmed = path.Trim().Trim('"');
            bool isExe = File.Exists(trimmed) && string.Equals(Path.GetExtension(trimmed), ".exe", StringComparison.OrdinalIgnoreCase);
            if (!isExe && !Directory.Exists(trimmed))
                return trimmed;
            string folder = trimmed.TrimEnd('\\', '/');
            List<string> inputs = new List<string>();
            if (!isExe)
            {
                inputs.Add(folder);
                inputs.Add(Path.Combine(folder, "input"));
            }
            string root = GameInstalls.ResolveInstallFolder(GameInstalls.MarkersOf(GameId), trimmed);
            string rootInput = InputFolder(root);
            if (rootInput != null)
                inputs.Add(rootInput);
            string fromFolder = isExe ? null : InputFolder(folder);
            if (fromFolder != null)
                inputs.Add(fromFolder);
            foreach (string input in inputs)
            {
                string general = Path.Combine(input, "general.actions");
                if (File.Exists(general))
                    return general;
                string newest = Directory.Exists(input) ? Directory.GetFiles(input, "*.actions").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
                if (newest != null)
                    return newest;
            }
            // No key mapping yet: name the file the game will write, so the error says where JoyPro looked.
            string target = rootInput ?? fromFolder ?? folder;
            return Path.Combine(target, "general.actions");
        }

        // ---- tokens --------------------------------------------------------------------------------------------

        // One input of an action list: "[mod+...+]main", main optionally "-" (inverted axis) and "*" (Relative Control:
        // the axis sets the speed of change). Button pairs ("a/b") and mouse/TrackIR inputs are not JoyPro inputs and
        // are kept as they are.
        private sealed class KoreaInput
        {
            public string Main = string.Empty;
            public List<string> Modifiers = new List<string>();
            public bool Inverted;
            public bool Relative;

            // "key" or "devN"; empty for inputs JoyPro does not manage.
            public string DeviceKey
            {
                get { return DeviceKeyOf(Main); }
            }

            public string Render()
            {
                return string.Join("+", Modifiers.Concat(new[] { (Relative ? "*" : string.Empty) + (Inverted ? "-" : string.Empty) + Main }));
            }
        }

        private static string DeviceKeyOf(string main)
        {
            if (main.StartsWith("key_", StringComparison.OrdinalIgnoreCase))
                return "key";
            Match match = DeviceToken.Match(main);
            return match.Success ? "dev" + match.Groups["id"].Value : string.Empty;
        }

        private static KoreaInput ParseInput(string raw)
        {
            string value = (raw ?? string.Empty).Trim();
            if (value.Length == 0 || value.IndexOf('/') >= 0)
                return null;
            string[] parts = value.Split('+');
            KoreaInput input = new KoreaInput();
            string main = parts[parts.Length - 1].Trim();
            while (main.StartsWith("-", StringComparison.Ordinal) || main.StartsWith("*", StringComparison.Ordinal))
            {
                if (main[0] == '-')
                    input.Inverted = true;
                else
                    input.Relative = true;
                main = main.Substring(1);
            }
            input.Main = main.ToLowerInvariant();
            for (int i = 0; i < parts.Length - 1; i++)
                input.Modifiers.Add(parts[i].Trim().ToLowerInvariant());
            return input;
        }

        // "b16" -> JOY_BTN17, "axis_s" -> JOY_RY, "pov0_90" -> JOY_BTN_POV1_R.
        private static bool TryDecodeJoystickPart(string rest, out string token, out bool axis)
        {
            token = string.Empty;
            axis = false;
            int number;
            if (rest.StartsWith("axis_", StringComparison.OrdinalIgnoreCase))
            {
                int index = Array.IndexOf(AxisLetters, rest.Substring(5).ToLowerInvariant());
                if (index < 0)
                    return false;
                token = GameInputTokens.AxisToken(index);
                axis = true;
                return true;
            }
            if (rest.StartsWith("pov", StringComparison.OrdinalIgnoreCase))
            {
                int split = rest.IndexOf('_');
                int hat;
                int angle;
                if (split < 4 ||
                    !int.TryParse(rest.Substring(3, split - 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out hat) ||
                    !int.TryParse(rest.Substring(split + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out angle) ||
                    angle < 0 || angle % 45 != 0 || angle >= 360)
                    return false;
                token = GameInputTokens.PovToken(hat, angle / 45);
                return true;
            }
            if (rest.StartsWith("b", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(rest.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 0)
            {
                token = GameInputTokens.ButtonToken(number);
                return true;
            }
            return false;
        }

        private static string EncodeJoystickPart(JoyInput input)
        {
            switch (input.Kind)
            {
                case JoyInputKind.Axis:
                    return input.Index < AxisLetters.Length ? "axis_" + AxisLetters[input.Index] : string.Empty;
                case JoyInputKind.Button:
                    return "b" + input.Index.ToString(CultureInfo.InvariantCulture);
                case JoyInputKind.Pov:
                    return "pov" + input.Index.ToString(CultureInfo.InvariantCulture) + "_" + (input.Direction * 45).ToString(CultureInfo.InvariantCulture);
                default:
                    return string.Empty;
            }
        }

        private static string KeyToJoyPro(string key, Dictionary<string, string> il2ToDx)
        {
            string name;
            if (il2ToDx.TryGetValue(key, out name))
                return name;
            return key.Length == 1 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key.Substring(1);
        }

        private static string KeyFromJoyPro(string key, Dictionary<string, string> dxToIl)
        {
            string name;
            return dxToIl.TryGetValue(key, out name) ? name : key.ToLowerInvariant();
        }

        // ---- device numbers ------------------------------------------------------------------------------------

        // IL-2 writes the instance GUID with its last 8 bytes reversed: {89D63500-B53F-11F1-8002-444553540000} ->
        // 89d63500-b53f-11f1-0000545345440280.
        internal static string ToKoreaGuid(Guid guid)
        {
            string d = guid.ToString("D").ToLowerInvariant();
            string tail = d.Substring(19, 4) + d.Substring(24);
            StringBuilder reversed = new StringBuilder();
            for (int i = tail.Length - 2; i >= 0; i -= 2)
                reversed.Append(tail, i, 2);
            return d.Substring(0, 19) + reversed;
        }

        internal static bool TryFromKoreaGuid(string value, out Guid guid)
        {
            guid = Guid.Empty;
            string v = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (v.Length != 35 || v[8] != '-' || v[13] != '-' || v[18] != '-')
                return false;
            string tail = v.Substring(19);
            StringBuilder reversed = new StringBuilder();
            for (int i = tail.Length - 2; i >= 0; i -= 2)
                reversed.Append(tail, i, 2);
            string r = reversed.ToString();
            return Guid.TryParse(v.Substring(0, 19) + r.Substring(0, 4) + "-" + r.Substring(4), out guid);
        }

        private static string Ident(GameDevice device)
        {
            return device.VendorId.ToString("x4", CultureInfo.InvariantCulture) + "_" + device.ProductId.ToString("x4", CultureInfo.InvariantCulture);
        }

        private static string ModelOf(GameDevice device)
        {
            if (!string.IsNullOrEmpty(device.InstanceName))
                return device.InstanceName;
            string name;
            Guid guid;
            return GameDeviceDirectory.TryParseJoyProName(device.JoyProName, out name, out guid) ? name : device.JoyProName;
        }

        private sealed class KnownDevice
        {
            public string Key = string.Empty;
            public Guid Guid;
            public Dictionary<string, object> Entry;

            public int DeviceId
            {
                get { return IntOf(Entry, "deviceId", -1); }
                set { Entry["deviceId"] = value; }
            }

            public string Model
            {
                get { return StringOf(Entry, "model"); }
            }
        }

        // known.devices.json: {"knownDevices": {"<guid>": {"deviceId": 2, "model": ..., "ident": ...}}}.
        private sealed class KnownDevices
        {
            public string Path = string.Empty;
            public Dictionary<string, object> Root;
            public Dictionary<string, object> Map;
            public bool Changed;

            public List<KnownDevice> All()
            {
                List<KnownDevice> list = new List<KnownDevice>();
                foreach (KeyValuePair<string, object> entry in Map)
                {
                    Dictionary<string, object> fields = entry.Value as Dictionary<string, object>;
                    if (fields == null)
                        continue;
                    Guid guid;
                    TryFromKoreaGuid(entry.Key, out guid);
                    list.Add(new KnownDevice { Key = entry.Key, Guid = guid, Entry = fields });
                }
                return list;
            }

            public static KnownDevices Load(string actionsPath, List<string> warnings)
            {
                KnownDevices known = new KnownDevices { Path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(actionsPath) ?? string.Empty, KnownDevicesFile) };
                if (File.Exists(known.Path))
                {
                    try
                    {
                        known.Root = KoreaJson.Parse(File.ReadAllText(known.Path, Encoding.UTF8)) as Dictionary<string, object>;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("Could not read " + known.Path + ": " + ex.Message);
                    }
                }
                if (known.Root == null)
                    known.Root = new Dictionary<string, object>(StringComparer.Ordinal);
                object map;
                known.Map = known.Root.TryGetValue("knownDevices", out map) ? map as Dictionary<string, object> : null;
                if (known.Map == null)
                {
                    known.Map = new Dictionary<string, object>(StringComparer.Ordinal);
                    known.Root["knownDevices"] = known.Map;
                }
                return known;
            }
        }

        // ---- the actions file ----------------------------------------------------------------------------------

        private sealed class ActionsFile
        {
            public Dictionary<string, object> Root;
            public List<object> Devices;
            public List<object> Modifiers;
            public Dictionary<string, object> Actions;
            public string Original = string.Empty;

            public static bool TryLoad(string path, List<string> errors, out ActionsFile file)
            {
                file = null;
                if (!File.Exists(path))
                {
                    errors.Add("IL-2 Korea key mapping file not found: " + path + ". The game writes it when you press ACCEPT in Settings > Key mapping; start the game once and do that, then try again.");
                    return false;
                }
                try
                {
                    string text = File.ReadAllText(path, Encoding.UTF8);
                    Dictionary<string, object> root = KoreaJson.Parse(text) as Dictionary<string, object>;
                    if (root == null)
                    {
                        errors.Add(path + " is not an IL-2 Korea key mapping file.");
                        return false;
                    }
                    file = new ActionsFile { Root = root, Original = text };
                    file.Devices = Ensure<List<object>>(root, "devices", () => new List<object>());
                    file.Modifiers = Ensure<List<object>>(root, "modifiers", () => new List<object>());
                    file.Actions = Ensure<Dictionary<string, object>>(root, "actions", () => new Dictionary<string, object>(StringComparer.Ordinal));
                    return true;
                }
                catch (Exception ex)
                {
                    errors.Add("Could not read " + path + ": " + ex.Message);
                    return false;
                }
            }

            public List<string> Inputs(string action)
            {
                object value;
                List<object> list = Actions.TryGetValue(action, out value) ? value as List<object> : null;
                return list == null ? new List<string>() : list.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)).ToList();
            }

            // Relative Control's dead zone and sensitivity (the axis's diamond dialog): "additive": {"<action>":
            // {"deadzone": 0.10, "sensitivity": 0.05}}, both the shown value / 100 (dead zone 0..80 %, sensitivity 1..20).
            public Dictionary<string, object> Additive(string action)
            {
                object all;
                Dictionary<string, object> map = Root.TryGetValue("additive", out all) ? all as Dictionary<string, object> : null;
                object value;
                return map != null && map.TryGetValue(action, out value) ? value as Dictionary<string, object> : null;
            }

            public string DeviceModel(int id)
            {
                Dictionary<string, object> entry = Devices.OfType<Dictionary<string, object>>().FirstOrDefault(d => IntOf(d, "deviceId", -2) == id);
                return entry == null ? string.Empty : StringOf(entry, "model");
            }
        }

        private static T Ensure<T>(Dictionary<string, object> root, string key, Func<T> create) where T : class
        {
            object value;
            T typed = root.TryGetValue(key, out value) ? value as T : null;
            if (typed == null)
            {
                typed = create();
                root[key] = typed;
            }
            return typed;
        }

        private static int IntOf(Dictionary<string, object> entry, string key, int fallback)
        {
            object value;
            if (entry == null || !entry.TryGetValue(key, out value) || value == null)
                return fallback;
            int parsed;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static double DoubleOf(Dictionary<string, object> entry, string key, double fallback)
        {
            object value;
            if (entry == null || !entry.TryGetValue(key, out value) || value == null)
                return fallback;
            KoreaJson.JsonNumber number = value as KoreaJson.JsonNumber;
            string text = number != null ? number.Text : Convert.ToString(value, CultureInfo.InvariantCulture);
            double parsed;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static string StringOf(Dictionary<string, object> entry, string key)
        {
            object value;
            return entry != null && entry.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty;
        }

        // Device number -> JoyPro device name: the connected device with that number first, else the known one.
        private static Dictionary<int, string> DeviceNames(ActionsFile file, KnownDevices known, GameDeviceDirectory devices)
        {
            Dictionary<int, string> names = new Dictionary<int, string>();
            foreach (KnownDevice entry in known.All().Where(k => k.DeviceId >= 0 && k.Guid != Guid.Empty))
            {
                GameDevice connected = devices == null ? null : devices.FindByInstanceGuid(entry.Guid);
                string name = connected != null ? connected.JoyProName : GameDeviceDirectory.BuildJoyProName(entry.Model.Length > 0 ? entry.Model : file.DeviceModel(entry.DeviceId), entry.Guid);
                if (!names.ContainsKey(entry.DeviceId) || connected != null)
                    names[entry.DeviceId] = name;
            }
            return names;
        }

        // ---- read ----------------------------------------------------------------------------------------------

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            configPath = ResolveConfigPath(configPath);
            GameScanResult result = new GameScanResult();
            ActionsFile file;
            if (!ActionsFile.TryLoad(configPath, result.Errors, out file))
                return result;
            KnownDevices known = KnownDevices.Load(configPath, result.Warnings);
            Dictionary<int, string> names = DeviceNames(file, known, devices);
            Dictionary<string, string> il2ToDx;
            Dictionary<string, string> dxToIl;
            EngineIl2.LoadKeyboardConversions(out il2ToDx, out dxToIl, result.Warnings);

            HashSet<GameInputBinding> relative = new HashSet<GameInputBinding>();
            foreach (string action in file.Actions.Keys)
            {
                GameAction cataloged = catalog.Find(action);
                if (cataloged == null)
                    continue;
                foreach (string raw in file.Inputs(action))
                {
                    KoreaInput input = ParseInput(raw);
                    if (input == null || input.DeviceKey.Length == 0)
                        continue;
                    string device;
                    string token;
                    bool axis;
                    if (!TryDecode(input.Main, names, il2ToDx, out device, out token, out axis))
                    {
                        result.Warnings.Add(action + ": '" + raw + "' cannot be represented in JoyPro; skipped.");
                        continue;
                    }
                    List<string> reformers = new List<string>();
                    bool ok = true;
                    foreach (string modifier in input.Modifiers)
                    {
                        string modifierDevice;
                        string modifierToken;
                        bool modifierAxis;
                        if (!TryDecode(modifier, names, il2ToDx, out modifierDevice, out modifierToken, out modifierAxis) || modifierAxis)
                        {
                            ok = false;
                            break;
                        }
                        reformers.Add(modifierToken + "§" + modifierDevice + "§" + modifierToken + "§False");
                    }
                    if (!ok)
                    {
                        result.Warnings.Add(action + ": modifier in '" + raw + "' cannot be represented in JoyPro; skipped.");
                        continue;
                    }
                    GameInputBinding imported = new GameInputBinding
                    {
                        ActionId = action,
                        Device = device,
                        Input = token,
                        IsAxis = axis,
                        Inverted = input.Inverted && axis,
                        Reformers = reformers,
                        Title = cataloged.Title
                    };
                    if (axis && input.Relative)
                        relative.Add(imported);
                    result.Bindings.Add(imported);
                }
            }

            // The response settings of the bound axes (general.pitch for pitch, shared.responses for the others), the
            // one-sided kind of throttle-like axes (the game's curve over the whole travel) and Relative Control.
            List<CurveFile> curves = CurveFile.LoadBoth(configPath, result.Warnings);
            foreach (GameInputBinding binding in result.Bindings.Where(b => b.IsAxis))
            {
                Dictionary<string, object> entry = CurveFile.For(curves, binding.ActionId).Entry(binding.ActionId);
                bool isRelative = relative.Contains(binding);
                if (entry == null && !isRelative)
                    continue;
                AxisTuning tuning = new AxisTuning { Inverted = binding.Inverted, Slider = OneSidedAxes.Contains(binding.ActionId) };
                if (isRelative)
                {
                    tuning.Native["relative"] = 1;
                    Dictionary<string, object> additive = file.Additive(binding.ActionId);
                    if (additive != null && additive.ContainsKey("deadzone"))
                        tuning.Native["relativeDeadZone"] = Math.Round(DoubleOf(additive, "deadzone", 0.1) * 100, 2);
                    if (additive != null && additive.ContainsKey("sensitivity"))
                        tuning.Native["relativeSensitivity"] = Math.Round(DoubleOf(additive, "sensitivity", 0.05) * 100, 2);
                }
                if (entry == null)
                {
                    binding.Tuning = tuning;
                    continue;
                }
                foreach (string key in CurveKeys)
                {
                    if (entry.ContainsKey(key))
                        tuning.Native[key] = IntOf(entry, key, 0);
                }
                binding.Tuning = tuning;
            }
            return result;
        }

        // ---- axis response files -------------------------------------------------------------------------------

        private static readonly string[] CurveKeys = { "deadZone0", "deadZone1", "sensitivity", "offset", "harmonization", "normalized" };

        // The axis kinds of the game's response editor (docs/games/il2-korea.md, KoreaAxesSurvey.ps1): one-sided axes run
        // the curve over the whole travel (Dead zone: Low/High); the primary controls also have the built-in curve and
        // control surface loading drop-downs; camera and head movement axes have no editor and no table.
        internal static readonly HashSet<string> OneSidedAxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "rpc_all_engines_throttles", "rpc_all_engines_props_pitch", "rpc_all_engines_mixtures", "rpc_all_engines_water_radiators_shutters",
            "rpc_all_engines_oil_radiators_shutters", "rpc_all_engines_in_cowls_shutters", "rpc_all_engines_out_cowls_shutters",
            "rpc_all_engines_turbosuperchargers", "rpc_wheel_brakes", "rpc_wheel_brakes_left", "rpc_wheel_brakes_right", "rpc_wheel_brakes_central",
            "rpc_yaw_left", "rpc_yaw_right"
        };
        internal static readonly HashSet<string> BuiltInCurveAxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rpc_pitch", "rpc_roll", "rpc_yaw" };
        internal static readonly HashSet<string> NoCurveAxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cam_rotate_pitch", "cam_rotate_yaw", "cam_rotate_roll", "cam_rotate_ext_pitch", "cam_rotate_ext_yaw", "cam_trans_fb", "cam_trans_lr",
            "cam_trans_ud", "cam_rotate_hf_yaw", "cam_rotate_hf_pitch", "bc_head_roll", "bc_head_yaw_wrapped", "bc_head_trans_x", "bc_head_trans_y",
            "bc_head_trans_z", "cam_turret_pitch", "cam_turret_yaw"
        };
        private const string PitchAction = "rpc_pitch";

        // general.pitch (the profile's pitch curve) or shared.responses: {"curves": {"<action>": {...}}}.
        private sealed class CurveFile
        {
            public string Path = string.Empty;
            public bool Pitch;
            public Dictionary<string, object> Root;
            public Dictionary<string, object> Curves;
            public string Original = string.Empty;
            public bool Changed;

            public static List<CurveFile> LoadBoth(string actionsPath, List<string> warnings)
            {
                string dir = System.IO.Path.GetDirectoryName(actionsPath) ?? string.Empty;
                string profile = System.IO.Path.GetFileNameWithoutExtension(actionsPath);
                return new List<CurveFile>
                {
                    Load(System.IO.Path.Combine(dir, profile + ".pitch"), true, warnings),
                    Load(System.IO.Path.Combine(dir, "shared.responses"), false, warnings)
                };
            }

            private static CurveFile Load(string path, bool pitch, List<string> warnings)
            {
                CurveFile file = new CurveFile { Path = path, Pitch = pitch };
                if (File.Exists(path))
                {
                    try
                    {
                        file.Original = File.ReadAllText(path, Encoding.UTF8);
                        file.Root = KoreaJson.Parse(file.Original) as Dictionary<string, object>;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("Could not read " + path + ": " + ex.Message);
                    }
                }
                if (file.Root == null)
                    file.Root = new Dictionary<string, object>(StringComparer.Ordinal);
                file.Curves = Ensure<Dictionary<string, object>>(file.Root, "curves", () => new Dictionary<string, object>(StringComparer.Ordinal));
                return file;
            }

            public static CurveFile For(List<CurveFile> files, string action)
            {
                return string.Equals(action, PitchAction, StringComparison.OrdinalIgnoreCase) ? files[0] : files[1];
            }

            public Dictionary<string, object> Entry(string action)
            {
                object value;
                return Curves.TryGetValue(action, out value) ? value as Dictionary<string, object> : null;
            }
        }

        // The response settings JoyPro writes for an axis action (file units, whole numbers as the game stores them).
        private static Dictionary<string, object> CurveEntry(AxisTuning tuning, bool pitch, Dictionary<string, object> existing)
        {
            Dictionary<string, object> entry = new Dictionary<string, object>(StringComparer.Ordinal);
            entry["deadZone0"] = (int)Math.Round(AxisModels.KoreaValue(tuning, "deadZone0"));
            entry["deadZone1"] = (int)Math.Round(AxisModels.KoreaValue(tuning, "deadZone1"));
            entry["sensitivity"] = (int)Math.Round(AxisModels.KoreaValue(tuning, "sensitivity"));
            if (pitch)
                entry["offset"] = (int)Math.Round(tuning.NativeOr("offset", 0));
            // Built-in Response Curve / Control Surface Loading: the tuning's value, else what the file has, else the game's
            // default (Built-in Static Curve, Full Stick Travel).
            entry["harmonization"] = tuning.Native.ContainsKey("harmonization") ? (int)Math.Round(tuning.Native["harmonization"]) : existing != null && existing.ContainsKey("harmonization") ? IntOf(existing, "harmonization", 1) : 1;
            entry["normalized"] = tuning.Native.ContainsKey("normalized") ? (int)Math.Round(tuning.Native["normalized"]) : existing != null && existing.ContainsKey("normalized") ? IntOf(existing, "normalized", 1) : 1;
            return entry;
        }

        private static bool SameCurve(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            if (a == null || b == null)
                return a == b;
            return CurveKeys.All(k => a.ContainsKey(k) == b.ContainsKey(k) && IntOf(a, k, 0) == IntOf(b, k, 0));
        }

        private static string CurveToken(Dictionary<string, object> entry)
        {
            return string.Join(",", CurveKeys.Where(entry.ContainsKey).Select(k => k + "=" + IntOf(entry, k, 0).ToString(CultureInfo.InvariantCulture)));
        }

        private static bool IsNeutral(Dictionary<string, object> entry)
        {
            // The game's defaults: no exponent, dead zones or offset, Built-in Static Curve, Full Stick Travel.
            return IntOf(entry, "deadZone0", 0) == 0 && IntOf(entry, "deadZone1", 0) == 0 && IntOf(entry, "sensitivity", 0) == 0 && IntOf(entry, "offset", 0) == 0
                && IntOf(entry, "harmonization", 1) == 1 && IntOf(entry, "normalized", 1) == 1;
        }

        // Writes the response settings of the axis binds JoyPro exports. Merge keeps settings made in the game.
        private static void UpdateCurves(GameExportRequest request, List<CurveFile> curves, Dictionary<string, HashSet<string>> owned,
            Dictionary<string, HashSet<string>> ownedBefore, GameIoResult result)
        {
            foreach (GameInputBinding binding in request.Bindings.Where(b => b.IsAxis && b.Tuning != null && !NoCurveAxes.Contains(b.ActionId)).GroupBy(b => b.ActionId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                string action = binding.ActionId;
                CurveFile file = CurveFile.For(curves, action);
                Dictionary<string, object> existing = file.Entry(action);
                Dictionary<string, object> wanted = CurveEntry(binding.Tuning, file.Pitch, existing);
                string ownerKey = "curve:" + action;
                // JoyPro's when it still has the values JoyPro wrote (a change made in the game makes it the user's).
                bool ours = existing != null && GameOwnershipStore.Contains(ownedBefore, ownerKey, CurveToken(existing));
                if (existing == null && IsNeutral(wanted))
                    continue;
                if (existing != null && request.Mode == GameExportMode.Merge && !ours && !SameCurve(existing, wanted))
                {
                    result.Warnings.Add(action + " has response settings made in the game; merge keeps them.");
                    continue;
                }
                GameOwnershipStore.Add(owned, ownerKey, CurveToken(wanted));
                if (SameCurve(existing, wanted))
                    continue;
                file.Curves[action] = wanted;
                file.Changed = true;
                result.Steps.Add("Response of " + action + " -> " + System.IO.Path.GetFileName(file.Path) + ": exponent " + wanted["sensitivity"] + ", dead zones " + wanted["deadZone0"] + "/" + wanted["deadZone1"] + (file.Pitch ? ", offset " + wanted["offset"] : string.Empty) + ".");
            }
        }

        // Writes Relative Control's dead zone and sensitivity of the relative axis binds JoyPro exports (general.actions
        // "additive"); without values in the tuning the game's defaults (10 %, 5) stay. Merge keeps settings made in the game.
        private static bool UpdateAdditive(GameExportRequest request, ActionsFile file, Dictionary<string, HashSet<string>> owned,
            Dictionary<string, HashSet<string>> ownedBefore, GameIoResult result)
        {
            bool changed = false;
            foreach (GameInputBinding binding in request.Bindings.Where(b => b.IsAxis && b.Tuning != null && b.Tuning.NativeOr("relative", 0) >= 0.5)
                .GroupBy(b => b.ActionId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                AxisTuning tuning = binding.Tuning;
                if (!tuning.Native.ContainsKey("relativeDeadZone") && !tuning.Native.ContainsKey("relativeSensitivity"))
                    continue;
                string action = binding.ActionId;
                Dictionary<string, object> existing = file.Additive(action);
                double deadZone = Math.Round(Math.Max(0, Math.Min(80, tuning.NativeOr("relativeDeadZone", existing != null ? DoubleOf(existing, "deadzone", 0.1) * 100 : 10))) / 100.0, 2);
                double sensitivity = Math.Round(Math.Max(1, Math.Min(20, tuning.NativeOr("relativeSensitivity", existing != null ? DoubleOf(existing, "sensitivity", 0.05) * 100 : 5))) / 100.0, 2);
                string token = AdditiveToken(deadZone, sensitivity);
                string existingToken = existing == null ? null : AdditiveToken(DoubleOf(existing, "deadzone", -1), DoubleOf(existing, "sensitivity", -1));
                string ownerKey = "additive:" + action;
                bool ours = existing != null && GameOwnershipStore.Contains(ownedBefore, ownerKey, existingToken);
                if (existing != null && request.Mode == GameExportMode.Merge && !ours && existingToken != token)
                {
                    result.Warnings.Add(action + " has Relative Control settings made in the game; merge keeps them.");
                    continue;
                }
                GameOwnershipStore.Add(owned, ownerKey, token);
                if (existingToken == token)
                    continue;
                Dictionary<string, object> map = Ensure<Dictionary<string, object>>(file.Root, "additive", () => new Dictionary<string, object>(StringComparer.Ordinal));
                Dictionary<string, object> entry = new Dictionary<string, object>(StringComparer.Ordinal);
                entry["deadzone"] = new KoreaJson.JsonNumber(deadZone.ToString("0.0#", CultureInfo.InvariantCulture));
                entry["sensitivity"] = new KoreaJson.JsonNumber(sensitivity.ToString("0.0#", CultureInfo.InvariantCulture));
                map[action] = entry;
                changed = true;
                result.Steps.Add("Relative Control of " + action + " -> general.actions: dead zone " + Math.Round(deadZone * 100).ToString(CultureInfo.InvariantCulture)
                    + " %, sensitivity " + Math.Round(sensitivity * 100).ToString(CultureInfo.InvariantCulture) + ".");
            }
            return changed;
        }

        private static string AdditiveToken(double deadZone, double sensitivity)
        {
            return deadZone.ToString("0.00", CultureInfo.InvariantCulture) + "/" + sensitivity.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static bool TryDecode(string main,Dictionary<int, string> names, Dictionary<string, string> il2ToDx, out string device, out string token, out bool axis)
        {
            device = string.Empty;
            token = string.Empty;
            axis = false;
            if (main.StartsWith("key_", StringComparison.OrdinalIgnoreCase))
            {
                string key = main.Substring(4);
                if (key.Length == 0)
                    return false;
                device = "Keyboard";
                token = KeyToJoyPro(key, il2ToDx);
                return true;
            }
            Match match = DeviceToken.Match(main);
            int id;
            if (!match.Success || !int.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || !names.TryGetValue(id, out device))
                return false;
            return TryDecodeJoystickPart(match.Groups["rest"].Value, out token, out axis);
        }

        // ---- write ---------------------------------------------------------------------------------------------

        private sealed class Wanted
        {
            public GameInputBinding Binding;
            public KoreaInput Input;
            public string Rendered = string.Empty;
        }

        // The input itself (device, input, modifiers), without the axis flags "-" and "*": two lines with the same
        // identity use the same input.
        private static string Identity(KoreaInput input)
        {
            return (string.Join("+", input.Modifiers.OrderBy(m => m, StringComparer.OrdinalIgnoreCase)) + "|" + input.Main).ToLowerInvariant();
        }

        private static bool WantsInput(Dictionary<string, List<Wanted>> byAction, string action, string identity)
        {
            List<Wanted> list;
            return byAction.TryGetValue(action, out list) && list.Any(w => Identity(w.Input) == identity);
        }

        // JoyPro's modifiers of a bind as the export result names them ("Keyboard LeftShift+...").
        private static string ModifierText(GameInputBinding binding)
        {
            return string.Join("+", (binding.Reformers ?? new List<string>()).Select(r => r.Split('§')).Where(p => p.Length >= 3).Select(p => p[1] + " " + p[2]));
        }

        // Device number of a JoyPro device; a connected device without one gets the lowest free number (from 1 up,
        // as the game's dialog offers) in known.devices.json.
        private static string DeviceNumber(string joyProDevice, GameDeviceDirectory devices, KnownDevices known, ActionsFile file, GameIoResult result, out int id)
        {
            id = -1;
            string name;
            Guid guid;
            GameDevice connected = devices == null ? null : devices.FindByJoyProName(joyProDevice);
            if (connected != null)
                guid = connected.InstanceGuid;
            else if (!GameDeviceDirectory.TryParseJoyProName(joyProDevice, out name, out guid))
                return "'" + joyProDevice + "' is not a joystick JoyPro can name.";

            KnownDevice entry = known.All().FirstOrDefault(k => k.Guid == guid);
            if (entry != null && entry.DeviceId >= 0)
            {
                id = entry.DeviceId;
                return string.Empty;
            }
            if (connected == null)
                return "'" + joyProDevice + "' has no device number in the game and is not connected; connect it and export again.";

            HashSet<int> used = new HashSet<int>(known.All().Select(k => k.DeviceId).Where(n => n >= 0));
            foreach (Dictionary<string, object> d in file.Devices.OfType<Dictionary<string, object>>())
                used.Add(IntOf(d, "deviceId", -1));
            id = 1;
            while (used.Contains(id))
                id++;

            string now = DateTime.Now.ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture);
            if (entry == null)
            {
                Dictionary<string, object> fields = new Dictionary<string, object>(StringComparer.Ordinal);
                fields["deviceId"] = id;
                fields["model"] = ModelOf(connected);
                fields["ident"] = Ident(connected);
                fields["isNew"] = false;
                fields["isGamepad"] = false;
                fields["created"] = now;
                fields["updated"] = now;
                fields["lastAttachedId"] = known.All().Select(k => IntOf(k.Entry, "lastAttachedId", -1)).DefaultIfEmpty(-1).Max() + 1;
                known.Map[ToKoreaGuid(guid)] = fields;
            }
            else
            {
                entry.DeviceId = id;
                entry.Entry["updated"] = now;
            }
            known.Changed = true;
            result.Steps.Add("Gave " + joyProDevice + " device number " + id.ToString(CultureInfo.InvariantCulture) + " in the game (joy " + id.ToString(CultureInfo.InvariantCulture) + ").");
            return string.Empty;
        }

        private static string EncodeBinding(GameInputBinding binding, GameDeviceDirectory devices, KnownDevices known, ActionsFile file,
            Dictionary<string, string> dxToIl, Dictionary<string, int> numbers, GameIoResult result, bool assign, out KoreaInput input)
        {
            input = new KoreaInput();
            string main;
            string reason = EncodeInput(binding.Device, binding.Input, binding.IsAxis, devices, known, file, dxToIl, numbers, result, assign, out main);
            if (reason.Length > 0)
                return reason;
            input.Main = main;
            input.Inverted = binding.IsAxis && binding.Inverted;
            input.Relative = binding.IsAxis && binding.Tuning != null && binding.Tuning.NativeOr("relative", 0) >= 0.5;
            foreach (string reformer in binding.Reformers ?? new List<string>())
            {
                if (binding.IsAxis)
                    return "IL-2 Korea axes take no modifiers.";
                string[] parts = reformer.Split('§');
                if (parts.Length < 3)
                    return "unreadable modifier '" + reformer + "'.";
                string modifier;
                reason = EncodeInput(parts[1], parts[2], false, devices, known, file, dxToIl, numbers, result, assign, out modifier);
                if (reason.Length > 0)
                    return "modifier: " + reason;
                input.Modifiers.Add(modifier);
            }
            return string.Empty;
        }

        private static string EncodeInput(string joyProDevice, string token, bool axis, GameDeviceDirectory devices, KnownDevices known, ActionsFile file,
            Dictionary<string, string> dxToIl, Dictionary<string, int> numbers, GameIoResult result, bool assign, out string main)
        {
            main = string.Empty;
            if (string.Equals((joyProDevice ?? string.Empty).Trim(), "Keyboard", StringComparison.OrdinalIgnoreCase))
            {
                if (axis)
                    return "a keyboard key cannot drive an axis.";
                main = "key_" + KeyFromJoyPro(token ?? string.Empty, dxToIl);
                return string.Empty;
            }
            JoyInput input;
            if (!GameInputTokens.TryParse(token, out input))
                return "input '" + token + "' is not a joystick input.";
            if (axis != (input.Kind == JoyInputKind.Axis))
                return axis ? "an axis action needs a joystick axis." : "a button action needs a button or hat.";
            string part = EncodeJoystickPart(input);
            if (part.Length == 0)
                return "input '" + token + "' has no IL-2 Korea name.";
            int id;
            if (!numbers.TryGetValue(joyProDevice, out id))
            {
                if (!assign)
                {
                    KnownDevice entry = null;
                    GameDevice connected = devices == null ? null : devices.FindByJoyProName(joyProDevice);
                    string name;
                    Guid guid;
                    if (connected != null)
                        entry = known.All().FirstOrDefault(k => k.Guid == connected.InstanceGuid);
                    else if (GameDeviceDirectory.TryParseJoyProName(joyProDevice, out name, out guid))
                        entry = known.All().FirstOrDefault(k => k.Guid == guid);
                    if (entry == null || entry.DeviceId < 0)
                        return "'" + joyProDevice + "' has no device number in the game.";
                    id = entry.DeviceId;
                }
                else
                {
                    string reason = DeviceNumber(joyProDevice, devices, known, file, result, out id);
                    if (reason.Length > 0)
                        return reason;
                }
                numbers[joyProDevice] = id;
            }
            main = "dev" + id.ToString(CultureInfo.InvariantCulture) + "_" + part;
            return string.Empty;
        }

        public GameIoResult Write(GameExportRequest request)
        {
            request.ConfigPath = ResolveConfigPath(request.ConfigPath);
            GameIoResult result = new GameIoResult();
            ActionsFile file;
            if (!ActionsFile.TryLoad(request.ConfigPath, result.Errors, out file))
                return result;
            KnownDevices known = KnownDevices.Load(request.ConfigPath, result.Warnings);
            Dictionary<string, string> il2ToDx;
            Dictionary<string, string> dxToIl;
            EngineIl2.LoadKeyboardConversions(out il2ToDx, out dxToIl, result.Warnings);

            Dictionary<string, int> numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<Wanted> wanted = new List<Wanted>();
            // Actions with a profile bind JoyPro cannot write (a key on an axis action): which input it means is unknown,
            // so merge conflicts and merge-overwrite leave the game's inputs of these actions alone.
            HashSet<string> unwritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameInputBinding binding in request.Bindings)
            {
                KoreaInput input;
                string reason = EncodeBinding(binding, request.Devices, known, file, dxToIl, numbers, result, true, out input);
                if (reason.Length > 0)
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
                    unwritten.Add(binding.ActionId);
                    continue;
                }
                wanted.Add(new Wanted { Binding = binding, Input = input, Rendered = input.Render() });
            }

            // Devices JoyPro writes: clean releases their other binds; nuke also releases joysticks the export does
            // not use. Clean and nuke never clear the keyboard (the game's keyboard defaults stay).
            HashSet<string> written = new HashSet<string>(wanted.Select(w => w.Input.DeviceKey).Where(k => k != "key"), StringComparer.OrdinalIgnoreCase);
            // The device numbers of connected devices: only their binds are JoyPro's to release (the import reads a device
            // that is not connected after known.devices.json, but its binds are not the export's to clear).
            HashSet<string> connectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KnownDevice entry in known.All().Where(k => k.DeviceId >= 0 && k.Guid != Guid.Empty && request.Devices != null && request.Devices.FindByInstanceGuid(k.Guid) != null))
                connectedIds.Add("dev" + entry.DeviceId.ToString(CultureInfo.InvariantCulture));
            Dictionary<int, string> names = DeviceNames(file, known, request.Devices);
            // An input JoyPro may release: an action of the catalog, every part readable by the import, the main input on
            // a connected joystick or (merge conflicts and merge-overwrite only) the keyboard when the export includes it,
            // joystick modifiers on connected devices. Mouse, TrackIR and button pairs never.
            Func<string, KoreaInput, bool> releasable = (action, input) =>
            {
                if (input == null || request.Catalog.Find(action) == null)
                    return false;
                string device = input.DeviceKey;
                if (device.Length == 0 || (device == "key" ? !request.IncludeKeyboard : !connectedIds.Contains(device)))
                    return false;
                string d;
                string t;
                bool a;
                if (!TryDecode(input.Main, names, il2ToDx, out d, out t, out a))
                    return false;
                foreach (string modifier in input.Modifiers)
                {
                    string modifierDevice = DeviceKeyOf(modifier);
                    if (!TryDecode(modifier, names, il2ToDx, out d, out t, out a) || a || (modifierDevice != "key" && !connectedIds.Contains(modifierDevice)))
                        return false;
                }
                return true;
            };
            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(request.ConfigPath, result.Warnings);
            Dictionary<string, HashSet<string>> ownedBefore = owned.ToDictionary(e => e.Key, e => new HashSet<string>(e.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            if (request.Mode != GameExportMode.Add && request.Mode != GameExportMode.Merge)
                owned.Clear();
            else
            {
                // Response settings stay JoyPro's until an export no longer writes them.
                foreach (string key in ownedBefore.Keys.Where(k => k.StartsWith("curve:", StringComparison.Ordinal) || k.StartsWith("additive:", StringComparison.Ordinal)).ToList())
                    owned.Remove(key);
            }
            List<CurveFile> curves = CurveFile.LoadBoth(request.ConfigPath, result.Warnings);
            UpdateCurves(request, curves, owned, ownedBefore, result);
            bool changed = UpdateAdditive(request, file, owned, ownedBefore, result);

            Dictionary<string, List<Wanted>> byAction = wanted.GroupBy(w => w.Binding.ActionId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            List<string> actionIds = file.Actions.Keys.ToList();
            foreach (string action in byAction.Keys)
            {
                if (!actionIds.Contains(action, StringComparer.OrdinalIgnoreCase))
                    actionIds.Add(action);
            }
            // The inputs (device, input, modifiers) the profile binds, and the game's: input -> (action, line).
            HashSet<string> wantedIdentities = new HashSet<string>(wanted.Select(w => Identity(w.Input)), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<KeyValuePair<string, string>>> gameInputs = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (string action in file.Actions.Keys)
            {
                foreach (string raw in file.Inputs(action))
                {
                    KoreaInput input = ParseInput(raw);
                    if (input == null)
                        continue;
                    List<KeyValuePair<string, string>> list;
                    if (!gameInputs.TryGetValue(Identity(input), out list))
                        gameInputs[Identity(input)] = list = new List<KeyValuePair<string, string>>();
                    list.Add(new KeyValuePair<string, string>(action, raw));
                }
            }

            foreach (string action in actionIds)
            {
                string key = file.Actions.Keys.FirstOrDefault(k => string.Equals(k, action, StringComparison.OrdinalIgnoreCase)) ?? action;
                List<string> before = file.Inputs(key);
                List<string> after = new List<string>();
                List<Wanted> mine;
                byAction.TryGetValue(action, out mine);
                mine = mine ?? new List<Wanted>();

                HashSet<Wanted> placed = new HashSet<Wanted>();
                foreach (string raw in before)
                {
                    KoreaInput input = ParseInput(raw);
                    string device = input == null ? string.Empty : input.DeviceKey;
                    string identity = input == null ? string.Empty : Identity(input);
                    bool isOwned = GameOwnershipStore.Contains(ownedBefore, key, raw);
                    bool canRelease = releasable(key, input);
                    // The profile's bind of this action on this input; a different form of it (invert, Relative Control)
                    // is the profile's update of the same bind, written in its place (not by "Only add").
                    // A key or button has no form JoyPro could change ("-key_v" on an axis action is the game's own).
                    Wanted same = input == null ? null : mine.FirstOrDefault(w => !placed.Contains(w) && Identity(w.Input) == identity);
                    bool sameForm = same != null && (string.Equals(same.Rendered, raw, StringComparison.OrdinalIgnoreCase) || !same.Binding.IsAxis);
                    if (same != null && (sameForm || request.Mode == GameExportMode.Add || !canRelease))
                    {
                        if (sameForm)
                            placed.Add(same);
                        after.Add(raw);
                        continue;
                    }
                    if (same != null)
                    {
                        placed.Add(same);
                        after.Add(same.Rendered);
                        continue;
                    }
                    bool drop = false;
                    if (canRelease)
                    {
                        bool sure = !unwritten.Contains(key);
                        // A conflict: the profile puts another action on this input (all modes but "Only add").
                        bool conflict = sure && request.Mode != GameExportMode.Add && wantedIdentities.Contains(identity);
                        // Nuke: joysticks the export does not use; clean: the other binds of the joysticks it writes.
                        bool nuked = device != "key" && request.NukeUnusedDevices && !written.Contains(device);
                        bool cleaned = device != "key" && request.Mode == GameExportMode.Clean && written.Contains(device);
                        // "My profile wins": an action the profile binds keeps only the profile's inputs (a move fully moves).
                        bool moved = sure && request.Mode == GameExportMode.MergeOverwrite && mine.Count > 0;
                        drop = conflict || nuked || cleaned || moved;
                    }
                    if (drop)
                        continue;
                    after.Add(raw);
                    if (isOwned && (request.Mode == GameExportMode.Add || request.Mode == GameExportMode.Merge))
                        GameOwnershipStore.Add(owned, key, raw);
                }

                foreach (Wanted w in mine)
                {
                    if (placed.Contains(w) || after.Any(a => string.Equals(a, w.Rendered, StringComparison.OrdinalIgnoreCase)))
                    {
                        GameOwnershipStore.Add(owned, key, w.Rendered);
                        result.BindingsWritten++;
                        continue;
                    }
                    if (after.Any(a => { KoreaInput other = ParseInput(a); return other != null && Identity(other) == Identity(w.Input); }))
                    {
                        // "Only add" (or a bind JoyPro may not release) keeps the game's form of this bind.
                        result.BindingsSkipped++;
                        result.Warnings.Add(key + " already has its own form of '" + w.Rendered + "' in the game; " + (request.Mode == GameExportMode.Add ? "add" : "the export") + " keeps it.");
                        continue;
                    }
                    // The game uses this input for another action the profile does not put there.
                    List<KeyValuePair<string, string>> holders;
                    KeyValuePair<string, string> held = gameInputs.TryGetValue(Identity(w.Input), out holders)
                        ? holders.FirstOrDefault(h => !string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase) && !WantsInput(byAction, h.Key, Identity(w.Input)) &&
                                                       (request.Mode == GameExportMode.Add || !releasable(h.Key, ParseInput(h.Value))))
                        : default(KeyValuePair<string, string>);
                    if (held.Key != null)
                    {
                        result.BindingsSkipped++;
                        if (request.Mode == GameExportMode.Add)
                        {
                            result.SkippedTakenInputs.Add(new ExportSkippedBind
                            {
                                Game = GameId,
                                Device = w.Binding.Device,
                                Input = w.Binding.Input,
                                ModifiersText = ModifierText(w.Binding),
                                Relation = w.Binding.RelationName ?? string.Empty,
                                Action = key,
                                GameAction = held.Key
                            });
                        }
                        else
                        {
                            result.ConflictsKept++;
                            result.Warnings.Add(key + ": the game uses '" + held.Value + "' for " + held.Key + ", which JoyPro does not release (another device, the keyboard without --include-keyboard or not a flight action); kept.");
                        }
                        continue;
                    }
                    after.Add(w.Rendered);
                    GameOwnershipStore.Add(owned, key, w.Rendered);
                    result.BindingsWritten++;
                }

                if (!before.SequenceEqual(after, StringComparer.Ordinal) || !file.Actions.ContainsKey(key))
                {
                    if (after.Count == 0 && !file.Actions.ContainsKey(key))
                        continue;
                    file.Actions[key] = after.Cast<object>().ToList();
                    changed = true;
                }
            }

            // Every device number in use is listed under "devices"; joystick modifiers are listed under "modifiers".
            foreach (KeyValuePair<string, int> number in numbers)
            {
                if (file.Devices.OfType<Dictionary<string, object>>().Any(d => IntOf(d, "deviceId", -2) == number.Value))
                    continue;
                GameDevice connected = request.Devices == null ? null : request.Devices.FindByJoyProName(number.Key);
                KnownDevice entry = known.All().FirstOrDefault(k => k.DeviceId == number.Value);
                Dictionary<string, object> device = new Dictionary<string, object>(StringComparer.Ordinal);
                device["deviceId"] = number.Value;
                device["model"] = entry != null ? entry.Model : connected != null ? ModelOf(connected) : number.Key;
                device["ident"] = entry != null ? StringOf(entry.Entry, "ident") : connected != null ? Ident(connected) : string.Empty;
                device["name"] = string.Empty;
                file.Devices.Add(device);
                changed = true;
            }
            foreach (string modifier in wanted.SelectMany(w => w.Input.Modifiers).Where(m => m.StartsWith("dev", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!file.Modifiers.Any(m => string.Equals(Convert.ToString(m, CultureInfo.InvariantCulture), modifier, StringComparison.OrdinalIgnoreCase)))
                {
                    file.Modifiers.Add(modifier);
                    changed = true;
                }
            }

            if (request.DryRun)
            {
                result.Steps.Add(changed || known.Changed || curves.Any(c => c.Changed) ? "Dry run: the IL-2 Korea key mapping would change." : "Dry run: nothing would change.");
                return result;
            }

            // The standalone installs to Program Files by default: say so instead of failing half-way when Windows
            // does not let JoyPro write there (opening for write changes nothing).
            try
            {
                using (new FileStream(request.ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                result.Errors.Add("JoyPro may not write " + request.ConfigPath + " (Windows protects this folder, e.g. Program Files). " +
                                  "Give your Windows user write access to the game's folder, or move the game out of Program Files, then export again.");
                return result;
            }
            GameOwnershipStore.Save(request.ConfigPath, GameId, owned);
            string text = KoreaJson.Write(file.Root);
            if (changed && !string.Equals(text, file.Original, StringComparison.Ordinal))
            {
                GameExportSafety.BackupBeforeOverwrite(request.ConfigPath, result.Warnings);
                File.WriteAllText(request.ConfigPath, text, new UTF8Encoding(false));
                result.FilesWritten.Add(request.ConfigPath);
            }
            if (known.Changed)
            {
                GameExportSafety.BackupBeforeOverwrite(known.Path, result.Warnings);
                File.WriteAllText(known.Path, KoreaJson.Write(known.Root), new UTF8Encoding(false));
                result.FilesWritten.Add(known.Path);
            }
            // Game-wide force feedback and the axis noise filter (engine ffb set --device * --game IL2Korea): Settings >
            // Input devices, which the game keeps in data\NSData\UserData\UserSettings.json (not startup.cfg).
            string inputDir = Path.GetDirectoryName(request.ConfigPath) ?? string.Empty;
            string settingsPath = Path.Combine(Path.GetDirectoryName(inputDir) ?? string.Empty, "NSData", "UserData", "UserSettings.json");
            Dictionary<string, double> ffb = FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, GameId, null);
            if (ffb.Count > 0)
            {
                if (!File.Exists(settingsPath))
                    result.Warnings.Add("Force feedback settings not written: " + settingsPath + " does not exist yet (start the game once).");
                else
                {
                    string settings = File.ReadAllText(settingsPath);
                    List<string> ffbChanges = new List<string>();
                    if (Il2KoreaUserSettings.Apply(ref settings, ffb, ffbChanges))
                    {
                        GameExportSafety.BackupBeforeOverwrite(settingsPath, result.Warnings);
                        File.WriteAllText(settingsPath, settings, new UTF8Encoding(false));
                        result.FilesWritten.Add(settingsPath);
                        result.Steps.AddRange(ffbChanges.Select(c => "UserSettings.json " + c));
                    }
                }
            }
            foreach (CurveFile curve in curves.Where(c => c.Changed))
            {
                string curveText = KoreaJson.Write(curve.Root);
                if (string.Equals(curveText, curve.Original, StringComparison.Ordinal))
                    continue;
                if (File.Exists(curve.Path))
                    GameExportSafety.BackupBeforeOverwrite(curve.Path, result.Warnings);
                File.WriteAllText(curve.Path, curveText, new UTF8Encoding(false));
                result.FilesWritten.Add(curve.Path);
            }
            result.Steps.Add(result.FilesWritten.Count > 0 ? "IL-2 Korea key mapping written. Start the game; it compiles current.map from it." : "The IL-2 Korea key mapping already matches.");
            return result;
        }

        public void RecordImport(string configPath, List<GameInputBinding> applied, GameDeviceDirectory devices, GameIoResult result)
        {
            configPath = ResolveConfigPath(configPath);
            ActionsFile file;
            if (!ActionsFile.TryLoad(configPath, result.Errors, out file))
                return;
            KnownDevices known = KnownDevices.Load(configPath, result.Warnings);
            Dictionary<string, string> il2ToDx;
            Dictionary<string, string> dxToIl;
            EngineIl2.LoadKeyboardConversions(out il2ToDx, out dxToIl, result.Warnings);
            Dictionary<string, int> numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(configPath, result.Warnings);
            foreach (GameInputBinding binding in applied)
            {
                KoreaInput input;
                if (EncodeBinding(binding, devices, known, file, dxToIl, numbers, result, false, out input).Length == 0)
                    GameOwnershipStore.Add(owned, binding.ActionId, input.Render());
            }
            GameOwnershipStore.Save(configPath, GameId, owned);
            result.FilesWritten.Add(configPath + GameOwnershipStore.Suffix);
        }

        // ---- catalog -------------------------------------------------------------------------------------------

        // The action ids come from a key mapping file of the game (its defaults list every action that has an input);
        // names and menu sections from JoyPro's own table (DB\Functions\ActionTexts\IL2Korea.tsv: Korea's own text sits
        // in encrypted archives, and IL-2 Great Battles names some shared ids differently, bc_signal_* are orders there
        // and gestures here), else IL-2 Great Battles' descriptions where the ids are the same, else from the id.
        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            string path = sourcePath;
            if (string.IsNullOrWhiteSpace(path))
                path = DiscoverConfigPaths(result.Warnings).FirstOrDefault() ?? string.Empty;
            else
                path = ResolveConfigPath(path);
            ActionsFile file;
            if (!ActionsFile.TryLoad(path, result.Errors, out file))
                return null;
            result.ConfigPath = path;

            Dictionary<string, KeyValuePair<bool, string>> greatBattles = GreatBattlesDescriptions(result.Warnings);
            GameCatalog catalog = new GameCatalog { GameId = GameId };
            foreach (string id in file.Actions.Keys)
            {
                GameAction existing = current == null ? null : current.Find(id);
                List<string> inputs = file.Inputs(id);
                KeyValuePair<bool, string> gb;
                bool known = greatBattles.TryGetValue(id, out gb);
                bool axis = inputs.Any(i => i.IndexOf('/') >= 0 || i.IndexOf("axis", StringComparison.OrdinalIgnoreCase) >= 0) || (known && gb.Key);
                ActionTexts.Entry own = EngineActionTexts.Find(GameId, id);
                catalog.Add(new GameAction
                {
                    Id = id,
                    Title = own != null && own.Name.Length > 0 ? own.Name : existing != null ? existing.Title : known && gb.Value.Length > 0 ? gb.Value : OwnTitle(id),
                    IsAxis = existing != null ? existing.IsAxis : axis,
                    Category = own != null && own.Category.Length > 0 ? own.Category : existing != null ? existing.Category : CategoryOf(id)
                });
            }
            if (current != null)
            {
                foreach (GameAction action in current.Actions)
                    catalog.Add(action);
            }
            result.Steps.Add(catalog.Actions.Count.ToString(CultureInfo.InvariantCulture) + " actions from " + path + ".");
            return catalog;
        }

        // Titles of the ids IL-2 Great Battles shares with Korea: JoyPro's IL-2 GB list when it was built, and the key
        // mapping files the installed IL-2 GB writes (data\input\*.actions, "id, input, 0| // description"), so Korea
        // gets them even before the IL-2 GB list is built. A description the game left as "inp<id>" is no title.
        // (Korea's own text is in its encrypted .gtp archives, which JoyPro does not read.)
        private static Dictionary<string, KeyValuePair<bool, string>> GreatBattlesDescriptions(List<string> warnings)
        {
            Dictionary<string, KeyValuePair<bool, string>> map = new Dictionary<string, KeyValuePair<bool, string>>(StringComparer.OrdinalIgnoreCase);
            string path = GameDataPaths.DbFile("IL2", "IL2.actions");
            if (File.Exists(path))
            {
                Regex line = new Regex(@"^(?<id>[a-z0-9_]+),.*//(?<kind>[ab])\s*(?<title>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    Match match = line.Match(raw.Trim());
                    if (match.Success && !map.ContainsKey(match.Groups["id"].Value) && UsableTitle(match.Groups["id"].Value, match.Groups["title"].Value))
                        map[match.Groups["id"].Value] = new KeyValuePair<bool, string>(match.Groups["kind"].Value == "a", match.Groups["title"].Value.Trim());
                }
            }
            try
            {
                GameInstall gb = GameInstalls.Find("IL2Game", warnings);
                string input = gb != null && gb.Installed ? Path.Combine(gb.InstallPath, "data", "input") : null;
                if (input != null && Directory.Exists(input))
                {
                    Regex written = new Regex(@"^(?<id>[a-z0-9_]+),\s*(?<input>[^,]*),.*\|\s*//\s*(?<title>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    foreach (string file in Directory.GetFiles(input, "*.actions").OrderBy(f => !f.EndsWith("global.actions", StringComparison.OrdinalIgnoreCase)))
                    {
                        foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                        {
                            Match match = written.Match(raw.Trim());
                            string id = match.Success ? match.Groups["id"].Value : null;
                            if (id == null || map.ContainsKey(id) || !UsableTitle(id, match.Groups["title"].Value))
                                continue;
                            bool axis = match.Groups["input"].Value.IndexOf("axis", StringComparison.OrdinalIgnoreCase) >= 0 || id.StartsWith("rpc_", StringComparison.OrdinalIgnoreCase);
                            map[id] = new KeyValuePair<bool, string>(axis, match.Groups["title"].Value.Trim());
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                warnings.Add("Could not read IL-2 Great Battles' key mapping files: " + ex.Message);
            }
            return map;
        }

        private static bool UsableTitle(string id, string title)
        {
            string text = (title ?? string.Empty).Trim();
            return text.Length > 0 && !string.Equals(text, "inp" + id, StringComparison.OrdinalIgnoreCase);
        }

        // An id neither JoyPro's table nor IL-2 Great Battles names (a newer game version): the generic slots, else the
        // id in words.
        private static string OwnTitle(string id)
        {
            Match slot = Regex.Match(id, @"^ext_action(\d)$");
            if (slot.Success)
                return "Mission panel key " + slot.Groups[1].Value;
            slot = Regex.Match(id, @"^cockpit_input_command_(\d+)$");
            if (slot.Success)
                return "Cockpit command " + slot.Groups[1].Value;
            return Humanize(id);
        }

        private static string Humanize(string id)
        {
            string text = id.Replace('_', ' ').Trim();
            return text.Length == 0 ? id : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        private static string CategoryOf(string id)
        {
            string i = id.ToLowerInvariant();
            if (i.StartsWith("gui_") || i == "escape" || i == "timepause" || i == "screenshot" || i.StartsWith("chat") || i == "quickmenu" || i == "skipallmedia")
                return "User Interface";
            if (i.StartsWith("cam_") || i.StartsWith("bc_") || i.StartsWith("trackir") || i.Contains("view"))
                return "Camera";
            if (i.Contains("engine") || i.Contains("throttle") || i.Contains("mixture") || i.Contains("prop") || i.Contains("radiator") || i.Contains("cowl") || i.Contains("boost") || i.Contains("magneto") || i.Contains("supercharger"))
                return "Engine";
            if (i.Contains("gun") || i.Contains("bomb") || i.Contains("rocket") || i.Contains("fire") || i.Contains("jettison") || i.StartsWith("gs_") || i.StartsWith("tc_") || i.StartsWith("ai_turret") || i.StartsWith("weapon"))
                return "Weapons";
            if (i.StartsWith("rpc_") || i.Contains("trim") || i.Contains("flaps") || i.Contains("gear") || i.Contains("brake") || i.Contains("autolevel") || i.Contains("autoclimb") || i.Contains("stabilizer"))
                return "Flight Controls";
            return "Aircraft";
        }
    }

    // JSON as IL-2 Korea writes it: objects keep their key order, 2-space indent, "key": value, LF line ends, no BOM.
    internal static class KoreaJson
    {
        public static object Parse(string text)
        {
            int i = 0;
            string s = (text ?? string.Empty).TrimStart('﻿');
            object value = ParseValue(s, ref i);
            SkipWhitespace(s, ref i);
            if (i != s.Length)
                throw new FormatException("unexpected text at " + i.ToString(CultureInfo.InvariantCulture));
            return value;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
                i++;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length)
                throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{')
            {
                Dictionary<string, object> obj = new Dictionary<string, object>(StringComparer.Ordinal);
                i++;
                SkipWhitespace(s, ref i);
                if (s[i] == '}')
                {
                    i++;
                    return obj;
                }
                while (true)
                {
                    SkipWhitespace(s, ref i);
                    string key = ParseString(s, ref i);
                    SkipWhitespace(s, ref i);
                    Expect(s, ref i, ':');
                    obj[key] = ParseValue(s, ref i);
                    SkipWhitespace(s, ref i);
                    if (s[i] == ',')
                    {
                        i++;
                        continue;
                    }
                    Expect(s, ref i, '}');
                    return obj;
                }
            }
            if (c == '[')
            {
                List<object> list = new List<object>();
                i++;
                SkipWhitespace(s, ref i);
                if (s[i] == ']')
                {
                    i++;
                    return list;
                }
                while (true)
                {
                    list.Add(ParseValue(s, ref i));
                    SkipWhitespace(s, ref i);
                    if (s[i] == ',')
                    {
                        i++;
                        continue;
                    }
                    Expect(s, ref i, ']');
                    return list;
                }
            }
            if (c == '"')
                return ParseString(s, ref i);
            if (s.Substring(i).StartsWith("true", StringComparison.Ordinal)) { i += 4; return true; }
            if (s.Substring(i).StartsWith("false", StringComparison.Ordinal)) { i += 5; return false; }
            if (s.Substring(i).StartsWith("null", StringComparison.Ordinal)) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0)
                i++;
            string number = s.Substring(start, i - start);
            long whole;
            if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out whole))
                return whole >= int.MinValue && whole <= int.MaxValue ? (object)(int)whole : whole;
            double real;
            if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out real))
                return new JsonNumber(number);
            throw new FormatException("bad value at " + start.ToString(CultureInfo.InvariantCulture));
        }

        // A non-integer number, kept as written.
        internal sealed class JsonNumber
        {
            public string Text;

            public JsonNumber(string text)
            {
                Text = text;
            }
        }

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c)
                throw new FormatException("expected '" + c + "' at " + i.ToString(CultureInfo.InvariantCulture));
            i++;
        }

        private static string ParseString(string s, ref int i)
        {
            Expect(s, ref i, '"');
            StringBuilder sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            Expect(s, ref i, '"');
            return sb.ToString();
        }

        public static string Write(object value)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value, int depth)
        {
            string indent = new string(' ', (depth + 1) * 2);
            string closing = new string(' ', depth * 2);
            Dictionary<string, object> obj = value as Dictionary<string, object>;
            if (obj != null)
            {
                if (obj.Count == 0)
                {
                    sb.Append("{}");
                    return;
                }
                sb.Append("{\n");
                int n = 0;
                foreach (KeyValuePair<string, object> entry in obj)
                {
                    sb.Append(indent);
                    WriteString(sb, entry.Key);
                    sb.Append(": ");
                    WriteValue(sb, entry.Value, depth + 1);
                    sb.Append(++n < obj.Count ? ",\n" : "\n");
                }
                sb.Append(closing).Append('}');
                return;
            }
            List<object> list = value as List<object>;
            if (list != null)
            {
                if (list.Count == 0)
                {
                    sb.Append("[]");
                    return;
                }
                sb.Append("[\n");
                for (int k = 0; k < list.Count; k++)
                {
                    sb.Append(indent);
                    WriteValue(sb, list[k], depth + 1);
                    sb.Append(k + 1 < list.Count ? ",\n" : "\n");
                }
                sb.Append(closing).Append(']');
                return;
            }
            if (value == null)
                sb.Append("null");
            else if (value is bool)
                sb.Append((bool)value ? "true" : "false");
            else if (value is string)
                WriteString(sb, (string)value);
            else if (value is JsonNumber)
                sb.Append(((JsonNumber)value).Text);
            else
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
