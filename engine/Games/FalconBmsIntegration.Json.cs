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
    // Falcon BMS 4.38.2 (Launcher 3.x) keeps the binds in User\Config\JSON: one file per device and aircraft profile,
    // DeviceBindings_<aircraft>_<VIDPID>_<device name>.json (schema_version 2, binding_type "hotas"), next to
    // KeyboardBindings_<aircraft>.json. On every close and Launch the launcher rebuilds the older files from them
    // (BMS - Auto.key, BMS - Auto-<aircraft>.key, Setup.v100.*.xml, axismapping.dat, DeviceSorting.txt, joystick.cal), so
    // with this layout JoyPro reads and writes only the JSON device files. Known from the files of a real 4.38.2 install:
    //   - axis_bindings: one entry per launcher axis function (logical_axis_name) with physical_axis_index (null = not
    //     assigned; 0..7 = X, Y, Z, Rx, Ry, Rz, Slider 1, Slider 2), saturation, deadzone, curve, center_offset, invert;
    //     the axes are shared by all aircraft (axis_binding_scope "shared_currently_f16_wins_if_files_differ"), so JoyPro
    //     reads them from the F-16 file and writes the same values into every aircraft file of the device;
    //   - button_bindings: button_index (0-based), assignment_index, shift_state, trigger, callback_name, invoke,
    //     sound_id; an unassigned button has no entry. Per aircraft.
    // Not seen in any file yet, so not written (JoyPro reports it instead): hat entries (pov_bindings), the shifted layer
    // and release entries (shift_state other than "Unshifted", trigger other than "Press"), the throttle detent values,
    // and dead zone / saturation values other than "None" (JoyPro writes the launcher's step names None, Small, Medium,
    // Large of its XML files; UNVERIFIED for the JSON files). Entries of these kinds stay as they are, in every mode.
    internal sealed partial class FalconBmsIntegration : IGameAircraftScope, IGameCatalogRefreshFiles
    {
        public const string JsonFolderName = "JSON";
        private const string DeviceFilePattern = "DeviceBindings_*.json";
        private const string KeyboardFilePattern = "KeyboardBindings_*.json";
        private const string Unshifted = "Unshifted";
        private const string Press = "Press";
        // The aircraft whose axis entries the launcher uses when the files of a device differ.
        private const string AxisAircraft = "F-16";
        private const string AircraftFileName = "aircraft.tsv";
        private static readonly Regex SourceCatalogPath = new Regex("\"source_catalog_path\"\\s*:\\s*\"(?<path>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
        private static readonly Regex AircraftKeyFile = new Regex(@"^BMS - Full-(?<suffix>.+)\.key$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private sealed class JsonFile
        {
            public string Path;
            public string Aircraft;
            public BmsJsonObject Root;
            public bool HasBom;
            public bool Changed;
        }

        private sealed class JsonDevice
        {
            // durable_device_key plus duplicate_pidvid_sequence_number: one device, its files of all aircraft.
            public string Key;
            public string Pidvid;
            public bool Duplicate;
            public Guid Guid;
            public string Name;
            public int Buttons;
            public List<JsonFile> Files = new List<JsonFile>();

            // The file whose axes count (F-16, else the first).
            public JsonFile AxisFile
            {
                get { return Files.FirstOrDefault(f => string.Equals(f.Aircraft, AxisAircraft, StringComparison.OrdinalIgnoreCase)) ?? Files.FirstOrDefault(); }
            }

            public JsonFile FileOf(string aircraft)
            {
                return Files.FirstOrDefault(f => string.Equals(f.Aircraft, aircraft, StringComparison.OrdinalIgnoreCase));
            }
        }

        private sealed class JsonTarget
        {
            public JsonDevice Device;
            public string Kind;
            public int Index;
            // Buttons: the aircraft file; axes: null (every aircraft).
            public string Aircraft;
            public string ActionId;
            public string Value;
            public GameInputBinding Binding;

            public string Key
            {
                get { return Kind + ":" + Index.ToString(CultureInfo.InvariantCulture); }
            }

            public string OwnerToken
            {
                get { return Device.Key + "|" + (Aircraft ?? "*") + "|" + Key; }
            }
        }

        // ---- layout ----------------------------------------------------------------------------------------------------

        public static string JsonFolder(string configPath)
        {
            return System.IO.Path.Combine(configPath ?? string.Empty, JsonFolderName);
        }

        // The launcher 3 layout: User\Config\JSON holds its binding files. Without them (BMS up to 4.38.1, or before the
        // first start of the new launcher, which imports the older files) JoyPro keeps using the Setup.v100 files.
        public static bool UsesJsonLayout(string configPath)
        {
            if (string.IsNullOrWhiteSpace(configPath))
                return false;
            string folder = JsonFolder(configPath);
            try
            {
                return Directory.Exists(folder) &&
                       (Directory.EnumerateFiles(folder, DeviceFilePattern).Any() || Directory.EnumerateFiles(folder, KeyboardFilePattern).Any());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static List<JsonDevice> LoadJsonDevices(string configPath, List<string> errors, List<string> warnings)
        {
            Dictionary<string, JsonDevice> devices = new Dictionary<string, JsonDevice>(StringComparer.OrdinalIgnoreCase);
            string folder = JsonFolder(configPath);
            if (!Directory.Exists(folder))
                return new List<JsonDevice>();
            foreach (string path in Directory.GetFiles(folder, DeviceFilePattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                byte[] bytes = File.ReadAllBytes(path);
                bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                string text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                BmsJsonObject root;
                try
                {
                    root = BmsJson.Parse(text) as BmsJsonObject;
                }
                catch (FormatException ex)
                {
                    errors.Add("Unreadable BMS binding file '" + path + "': " + ex.Message);
                    continue;
                }
                if (root == null || !string.Equals(root.String("binding_type"), "hotas", StringComparison.OrdinalIgnoreCase))
                    continue;
                int? schema = root.Int("schema_version");
                if (schema != 2)
                {
                    warnings.Add("BMS binding file '" + System.IO.Path.GetFileName(path) + "' has schema_version " + (schema.HasValue ? schema.Value.ToString(CultureInfo.InvariantCulture) : "?") +
                                 "; JoyPro knows version 2 only and leaves it alone.");
                    continue;
                }
                string aircraft = (root.String("aircraft_profile") ?? string.Empty).Trim();
                string pidvid = (root.String("pidvid") ?? root.String("durable_device_key") ?? string.Empty).Trim().ToUpperInvariant();
                string durable = (root.String("durable_device_key") ?? pidvid).Trim().ToUpperInvariant();
                object sequence = root.Get("duplicate_pidvid_sequence_number");
                string key = durable + (sequence is BmsJsonNumber ? "#" + ((BmsJsonNumber)sequence).Raw : string.Empty);
                if (aircraft.Length == 0 || durable.Length == 0 || root.Array("axis_bindings") == null || root.Array("button_bindings") == null)
                {
                    warnings.Add("BMS binding file '" + System.IO.Path.GetFileName(path) + "' lacks its aircraft, device key or binding lists; left alone.");
                    continue;
                }

                JsonDevice device;
                if (!devices.TryGetValue(key, out device))
                {
                    devices[key] = device = new JsonDevice { Key = key, Pidvid = pidvid, Duplicate = sequence is BmsJsonNumber };
                    device.Name = (root.String("instance_name") ?? root.String("product_name") ?? durable).Trim();
                }
                Guid guid;
                if (device.Guid == Guid.Empty && Guid.TryParse(root.String("last_seen_instance_guid") ?? string.Empty, out guid))
                    device.Guid = guid;
                device.Buttons = Math.Max(device.Buttons, root.Int("button_count") ?? 0);
                device.Files.Add(new JsonFile { Path = path, Aircraft = aircraft, Root = root, HasBom = bom });
            }
            return devices.Values.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The aircraft profiles of the JSON layout (F-16, F-15ABCD, ...), from the binding files.
        private static List<string> JsonAircraft(string configPath)
        {
            List<string> aircraft = new List<string>();
            string folder = JsonFolder(configPath);
            if (!Directory.Exists(folder))
                return aircraft;
            foreach (string file in Directory.GetFiles(folder, DeviceFilePattern).Concat(Directory.GetFiles(folder, KeyboardFilePattern)))
            {
                // DeviceBindings_<aircraft>_<VIDPID>_<name>.json / KeyboardBindings_<aircraft>.json: the name is read from
                // the file (aircraft_profile) only for device files; keyboard files carry it in their name.
                string name = System.IO.Path.GetFileNameWithoutExtension(file);
                string value = null;
                if (name.StartsWith("KeyboardBindings_", StringComparison.OrdinalIgnoreCase))
                    value = name.Substring("KeyboardBindings_".Length);
                else
                {
                    Match match = Regex.Match(name, "^DeviceBindings_(?<aircraft>.+)_[0-9A-Fa-f]{8}_");
                    if (match.Success)
                        value = match.Groups["aircraft"].Value;
                }
                if (!string.IsNullOrWhiteSpace(value) && !aircraft.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                    aircraft.Add(value.Trim());
            }
            return aircraft.OrderBy(a => string.Equals(a, AxisAircraft, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The key file of each aircraft profile: KeyboardBindings_<aircraft>.json names it (source_catalog_path; only the
        // file name is used, in the config folder), else "BMS - Full.key" for the F-16 and "BMS - Full-<aircraft without
        // '-'>.key" (F-15ABCD: BMS - Full-F15ABCD.key).
        private static Dictionary<string, string> AircraftKeyFiles(string configPath, IEnumerable<string> aircraft)
        {
            Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string folder = JsonFolder(configPath);
            foreach (string name in aircraft)
            {
                string keyboard = System.IO.Path.Combine(folder, "KeyboardBindings_" + name + ".json");
                string fileName = null;
                if (File.Exists(keyboard))
                {
                    try
                    {
                        char[] head = new char[8192];
                        int read;
                        using (StreamReader reader = new StreamReader(keyboard, Encoding.UTF8))
                            read = reader.ReadBlock(head, 0, head.Length);
                        Match match = SourceCatalogPath.Match(new string(head, 0, read));
                        if (match.Success)
                        {
                            string unescaped = Regex.Unescape(match.Groups["path"].Value);
                            fileName = System.IO.Path.GetFileName(unescaped.Replace('/', '\\'));
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                    {
                    }
                }
                if (string.IsNullOrEmpty(fileName))
                    fileName = string.Equals(name, AxisAircraft, StringComparison.OrdinalIgnoreCase) ? FullKeyFile : "BMS - Full-" + Regex.Replace(name, "[^A-Za-z0-9]", string.Empty) + ".key";
                string path = System.IO.Path.Combine(configPath, fileName);
                if (File.Exists(path))
                    files[name] = path;
            }
            return files;
        }

        // Every callback a key file lists (any section, visible or not): what the launcher can put on a button of that
        // aircraft. Null when the aircraft has no key file in the folder (then nothing is filtered).
        private static Dictionary<string, HashSet<string>> AircraftCallbacks(string configPath, IEnumerable<string> aircraft)
        {
            Dictionary<string, HashSet<string>> callbacks = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> file in AircraftKeyFiles(configPath, aircraft))
            {
                HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(file.Value, Encoding.GetEncoding(28591)))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                        continue;
                    int space = trimmed.IndexOfAny(new[] { ' ', '\t' });
                    string callback = space < 0 ? trimmed : trimmed.Substring(0, space);
                    if (callback != Nothing)
                        set.Add(callback);
                }
                callbacks[file.Key] = set;
            }
            return callbacks;
        }

        private static string VidPidOf(GameDevice device)
        {
            return device.VendorId.ToString("X4", CultureInfo.InvariantCulture) + device.ProductId.ToString("X4", CultureInfo.InvariantCulture);
        }

        // The connected device of a binding file: by its last instance GUID, else by VID:PID when exactly one device of
        // that model is connected and BMS keeps one file group for it.
        private static GameDevice ConnectedOf(JsonDevice device, GameDeviceDirectory devices)
        {
            if (devices == null)
                return null;
            GameDevice found = device.Guid != Guid.Empty ? devices.FindByInstanceGuid(device.Guid) : null;
            if (found != null || device.Duplicate || device.Pidvid.Length != 8)
                return found;
            int vid;
            int pid;
            if (!int.TryParse(device.Pidvid.Substring(0, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid) ||
                !int.TryParse(device.Pidvid.Substring(4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid))
                return null;
            List<GameDevice> sameModel = devices.FindByVidPid(vid, pid);
            return sameModel.Count == 1 ? sameModel[0] : null;
        }

        private static string JsonDeviceName(JsonDevice device, GameDeviceDirectory devices)
        {
            GameDevice connected = ConnectedOf(device, devices);
            if (connected != null)
                return connected.JoyProName;
            string name = new string(device.Name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            return GameDeviceDirectory.BuildJoyProName(name, device.Guid);
        }

        private static JsonDevice JsonDeviceFor(List<JsonDevice> files, string joyProName, GameDeviceDirectory devices)
        {
            string name;
            Guid guid;
            if (!GameDeviceDirectory.TryParseJoyProName(joyProName, out name, out guid))
                return null;
            JsonDevice byGuid = files.FirstOrDefault(d => d.Guid == guid);
            if (byGuid != null)
                return byGuid;
            GameDevice connected = devices == null ? null : devices.FindByInstanceGuid(guid);
            if (connected == null || !connected.HasVidPid)
                return null;
            List<JsonDevice> sameModel = files.Where(d => !d.Duplicate && string.Equals(d.Pidvid, VidPidOf(connected), StringComparison.OrdinalIgnoreCase)).ToList();
            return sameModel.Count == 1 && devices.FindByVidPid(connected.VendorId, connected.ProductId).Count == 1 ? sameModel[0] : null;
        }

        // ---- entries -----------------------------------------------------------------------------------------------------

        private static List<BmsJsonObject> Objects(List<object> list)
        {
            return list == null ? new List<BmsJsonObject>() : list.OfType<BmsJsonObject>().ToList();
        }

        private static BmsJsonObject AxisEntry(JsonFile file, string function)
        {
            return Objects(file.Root.Array("axis_bindings")).FirstOrDefault(a => string.Equals(a.String("logical_axis_name"), function, StringComparison.OrdinalIgnoreCase));
        }

        // The axis function on a physical axis of the device (its axis file).
        private static string AxisFunctionOn(JsonDevice device, int index)
        {
            JsonFile file = device.AxisFile;
            BmsJsonObject entry = file == null ? null : Objects(file.Root.Array("axis_bindings")).FirstOrDefault(a => a.Int("physical_axis_index") == index);
            return entry == null ? string.Empty : entry.String("logical_axis_name") ?? string.Empty;
        }

        private static bool IsPressEntry(BmsJsonObject entry)
        {
            return string.Equals(entry.String("shift_state"), Unshifted, StringComparison.Ordinal) && string.Equals(entry.String("trigger"), Press, StringComparison.Ordinal);
        }

        private static List<BmsJsonObject> PressEntries(JsonFile file, int button)
        {
            return Objects(file.Root.Array("button_bindings"))
                .Where(e => e.Int("button_index") == button && IsPressEntry(e))
                .OrderBy(e => e.Int("assignment_index") ?? 0)
                .ToList();
        }

        private static string ButtonCallback(JsonFile file, int button)
        {
            BmsJsonObject entry = PressEntries(file, button).FirstOrDefault();
            return entry == null ? string.Empty : entry.String("callback_name") ?? string.Empty;
        }

        private static bool SetJsonValue(JsonFile file, BmsJsonObject entry, string name, object value)
        {
            object current = entry.Get(name);
            bool same = current == null ? value == null : current.Equals(value) || (current is BmsJsonNumber && value is BmsJsonNumber && ((BmsJsonNumber)current).Raw == ((BmsJsonNumber)value).Raw);
            if (same && entry.Has(name))
                return false;
            entry.Set(name, value);
            file.Changed = true;
            return true;
        }

        // Unassigns an axis function the way the launcher does: no physical axis, its tuning back to the defaults.
        private static void ClearAxisEntry(JsonFile file, BmsJsonObject entry)
        {
            SetJsonValue(file, entry, "physical_axis_index", null);
            SetJsonValue(file, entry, "saturation", "None");
            SetJsonValue(file, entry, "deadzone", "None");
            if (entry.Has("curve"))
                SetJsonValue(file, entry, "curve", BmsJsonNumber.Of(1));
            SetJsonValue(file, entry, "invert", false);
        }

        // The button's press entry gets the callback: the first one changes, else a new entry like the launcher's.
        private static void SetButton(JsonFile file, int button, string callback)
        {
            BmsJsonObject entry = PressEntries(file, button).FirstOrDefault();
            if (entry != null)
            {
                if (SetJsonValue(file, entry, "callback_name", callback))
                    SetJsonValue(file, entry, "invoke", "Default");
                return;
            }
            List<object> list = file.Root.Array("button_bindings");
            BmsJsonObject created = new BmsJsonObject();
            created.Set("button_index", BmsJsonNumber.Of(button));
            created.Set("assignment_index", BmsJsonNumber.Of(0));
            created.Set("shift_state", Unshifted);
            created.Set("trigger", Press);
            created.Set("callback_name", callback);
            created.Set("invoke", "Default");
            created.Set("sound_id", BmsJsonNumber.Of(0));
            int at = list.FindIndex(o => o is BmsJsonObject && (((BmsJsonObject)o).Int("button_index") ?? 0) > button);
            if (at < 0)
                list.Add(created);
            else
                list.Insert(at, created);
            file.Changed = true;
        }

        private static void RemoveEntry(JsonFile file, BmsJsonObject entry)
        {
            if (file.Root.Array("button_bindings").Remove(entry))
                file.Changed = true;
        }

        // ---- read --------------------------------------------------------------------------------------------------------

        private GameScanResult ReadJson(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            List<JsonDevice> files = LoadJsonDevices(configPath, result.Errors, result.Warnings);
            if (files.Count == 0 && result.Errors.Count == 0)
                result.Errors.Add("No BMS binding files (JSON\\DeviceBindings_*.json) in " + configPath + ". Start the BMS launcher once with the devices connected; it writes a file per device and aircraft.");
            Dictionary<string, HashSet<string>> callbacks = AircraftCallbacks(configPath, files.SelectMany(d => d.Files.Select(f => f.Aircraft)).Distinct(StringComparer.OrdinalIgnoreCase));
            string userCfg = ReadUserCfg(configPath);
            int otherEntries = 0;
            int hats = 0;
            foreach (JsonDevice file in files)
            {
                string device = JsonDeviceName(file, devices);
                JsonFile axisFile = file.AxisFile;
                foreach (BmsJsonObject entry in Objects(axisFile.Root.Array("axis_bindings")))
                {
                    int? index = entry.Int("physical_axis_index");
                    if (!index.HasValue || index.Value < 0 || index.Value > 7)
                        continue;
                    GameAction action = catalog.Find(AxisPrefix + (entry.String("logical_axis_name") ?? string.Empty));
                    if (action == null || !action.IsAxis)
                        continue;
                    AxisTuning tuning = ReadAxisTuning(entry.Bool("invert") == true, entry.String("deadzone"), entry.String("saturation"), action.Id.Substring(AxisPrefix.Length), userCfg);
                    result.Bindings.Add(new GameInputBinding
                    {
                        ActionId = action.Id,
                        Device = device,
                        Input = GameInputTokens.AxisToken(index.Value),
                        IsAxis = true,
                        Inverted = tuning.Inverted,
                        Deadzone = tuning.Deadzone,
                        SaturationX = tuning.SaturationX,
                        SaturationY = 1.0,
                        Tuning = tuning
                    });
                }

                // Buttons per aircraft: a bind found in every aircraft whose key file has the callback is for all aircraft.
                Dictionary<string, List<string>> found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                List<string> order = new List<string>();
                foreach (JsonFile aircraftFile in file.Files)
                {
                    foreach (BmsJsonObject entry in Objects(aircraftFile.Root.Array("button_bindings")))
                    {
                        int? button = entry.Int("button_index");
                        string callback = entry.String("callback_name") ?? string.Empty;
                        if (!button.HasValue || callback.Length == 0 || callback == Nothing)
                            continue;
                        if (!IsPressEntry(entry))
                        {
                            otherEntries++;
                            continue;
                        }
                        string key = callback + "\u001F" + button.Value.ToString(CultureInfo.InvariantCulture);
                        List<string> aircraft;
                        if (!found.TryGetValue(key, out aircraft))
                        {
                            found[key] = aircraft = new List<string>();
                            order.Add(key);
                        }
                        if (!aircraft.Contains(aircraftFile.Aircraft, StringComparer.OrdinalIgnoreCase))
                            aircraft.Add(aircraftFile.Aircraft);
                    }
                    hats += Objects(aircraftFile.Root.Array("pov_bindings")).Count;
                }
                foreach (string key in order)
                {
                    string[] parts = key.Split('\u001F');
                    GameAction action = catalog.Find(parts[0]);
                    if (action == null || action.IsAxis)
                        continue;
                    List<string> aircraft = found[key];
                    List<string> possible = file.Files.Select(f => f.Aircraft)
                        .Where(a => { HashSet<string> set; return !callbacks.TryGetValue(a, out set) || set.Contains(action.Id); })
                        .ToList();
                    bool everywhere = possible.All(a => aircraft.Contains(a, StringComparer.OrdinalIgnoreCase));
                    result.Bindings.Add(new GameInputBinding
                    {
                        ActionId = action.Id,
                        Device = device,
                        Input = GameInputTokens.ButtonToken(int.Parse(parts[1], CultureInfo.InvariantCulture)),
                        Reformers = new List<string>(),
                        Aircraft = everywhere ? new List<string>() : aircraft.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList()
                    });
                }
            }
            if (otherEntries > 0)
                result.Warnings.Add(otherEntries.ToString(CultureInfo.InvariantCulture) + " shifted or release bind(s) in the BMS binding files were not read: JoyPro reads and writes the unshifted press binds of BMS 4.38.2's new files only (they stay as they are).");
            if (hats > 0)
                result.Warnings.Add(hats.ToString(CultureInfo.InvariantCulture) + " hat bind(s) in the BMS binding files were not read: JoyPro does not know how BMS 4.38.2 stores hats yet (they stay as they are).");
            return result;
        }

        // ---- write -------------------------------------------------------------------------------------------------------

        private GameIoResult WriteJson(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            List<JsonDevice> files = LoadJsonDevices(request.ConfigPath, result.Errors, result.Warnings);
            if (result.Errors.Count > 0)
                return result;
            if (files.Count == 0)
            {
                result.Errors.Add("BMS keeps the binds in " + JsonFolder(request.ConfigPath) + " (BMS 4.38.2 launcher), but there is no device file (DeviceBindings_*.json) yet. " +
                                  "Start the BMS launcher once with the devices connected and close it; it writes a file per device and aircraft. Then export again.");
                return result;
            }
            List<string> allAircraft = files.SelectMany(d => d.Files.Select(f => f.Aircraft)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Dictionary<string, HashSet<string>> callbacks = AircraftCallbacks(request.ConfigPath, allAircraft);

            Dictionary<string, JsonTarget> wanted = new Dictionary<string, JsonTarget>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> wantedAxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> notInAircraft = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string, AxisTuning>> axes = new List<KeyValuePair<string, AxisTuning>>();
            foreach (GameInputBinding binding in request.Bindings)
            {
                GameAction action = request.Catalog.Find(binding.ActionId);
                List<JsonTarget> targets = new List<JsonTarget>();
                string reason = action == null ? "not in the BMS catalog." : EncodeJson(files, request.Devices, binding, action, callbacks, notInAircraft, out targets);
                if (reason.Length == 0)
                {
                    JsonTarget taken = targets.Select(t => { JsonTarget existing; return wanted.TryGetValue(t.OwnerToken, out existing) ? existing : null; }).FirstOrDefault(t => t != null);
                    if (taken != null)
                        reason = "the input is already used by relation '" + taken.Binding.RelationName + "'.";
                    else if (action.IsAxis && !wantedAxes.Add(targets[0].Value))
                        reason = "BMS gives each axis function to one physical axis only.";
                }
                if (reason.Length > 0)
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
                    continue;
                }
                foreach (JsonTarget target in targets)
                    wanted[target.OwnerToken] = target;
            }
            if (!string.IsNullOrWhiteSpace(requestedAircraft))
                result.Steps.Add("Aircraft " + requestedAircraft.Trim() + " picked: not needed for BMS, every relation goes into the aircraft it is for.");
            foreach (KeyValuePair<string, int> skipped in notInAircraft)
                result.Steps.Add(skipped.Value.ToString(CultureInfo.InvariantCulture) + " bind(s) not written for aircraft " + skipped.Key + ": its key file has no such callback.");

            string ownershipBase = OwnershipBase(request.ConfigPath);
            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(ownershipBase, result.Warnings);
            Func<JsonDevice, bool> connected = d =>
            {
                GameDevice device = ConnectedOf(d, request.Devices);
                return device != null && !device.Kept;
            };
            List<JsonDevice> connectedFiles = files.Where(connected).ToList();
            if (request.Mode == GameExportMode.Clean || request.Mode == GameExportMode.MergeOverwrite)
            {
                if (request.Mode == GameExportMode.MergeOverwrite)
                    ReleaseOtherJsonInputs(connectedFiles, wanted);
                owned.Clear();
            }
            if (request.Mode == GameExportMode.Clean || request.NukeUnusedDevices)
            {
                // Clean takes over the devices JoyPro writes, nuke also the connected ones it does not use. Shifted,
                // release and hat entries stay (JoyPro does not write them), and so does the shift button they need.
                foreach (JsonDevice device in connectedFiles)
                {
                    bool used = wanted.Values.Any(t => t.Device == device);
                    if (used ? request.Mode != GameExportMode.Clean : !request.NukeUnusedDevices)
                        continue;
                    ReleaseJsonDevice(device, wanted, !used);
                }
            }

            HashSet<GameInputBinding> written = new HashSet<GameInputBinding>();
            foreach (JsonTarget target in wanted.Values.OrderBy(t => t.Device.Key, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Kind).ThenBy(t => t.Index).ThenBy(t => t.Aircraft ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                string current = target.Kind == "AX" ? AxisFunctionOn(target.Device, target.Index) : ButtonCallback(target.Device.FileOf(target.Aircraft), target.Index);
                bool sameAction = string.Equals(current, target.Value, StringComparison.OrdinalIgnoreCase);
                if (!sameAction && current.Length > 0 && current != Nothing)
                {
                    if (request.Mode == GameExportMode.Add)
                    {
                        result.BindingsSkipped++;
                        result.SkippedTakenInputs.Add(SkippedMultiBind(target.Binding, current));
                        continue;
                    }
                    result.Warnings.Add("Relation '" + target.Binding.RelationName + "' (" + target.ActionId + ") replaced '" + current + "' on its input" + (target.Aircraft == null ? "." : " (" + target.Aircraft + ")."));
                }
                if (target.Kind == "AX")
                {
                    string claimedBy = OtherJsonAxisClaim(files, target);
                    if (claimedBy.Length > 0 && request.Mode == GameExportMode.Add)
                    {
                        result.BindingsSkipped++;
                        result.Warnings.Add("Relation '" + target.Binding.RelationName + "' (" + target.ActionId + "): BMS already has this axis on " + claimedBy + " and gives an axis function to one axis only; add keeps it.");
                        continue;
                    }
                    if (claimedBy.Length > 0 && request.Mode == GameExportMode.Merge)
                        result.Warnings.Add("Relation '" + target.Binding.RelationName + "' (" + target.ActionId + "): moved from " + claimedBy + " (BMS gives an axis function to one axis only).");
                    foreach (JsonDevice other in files.Where(d => d != target.Device && !connectedFiles.Contains(d)))
                    {
                        JsonFile axisFile = other.AxisFile;
                        BmsJsonObject entry = axisFile == null ? null : AxisEntry(axisFile, target.Value);
                        if (entry != null && entry.Int("physical_axis_index").HasValue)
                            result.Warnings.Add(target.Value + ": BMS also has this axis function on " + other.Name + ", which is not connected; JoyPro leaves it. Unassign it in the BMS launcher if the launcher uses that one.");
                    }
                }
                // "Only add": the action already on its input is nothing to do (an axis keeps the game's settings too).
                if (request.Mode == GameExportMode.Add && sameAction)
                    continue;
                if (target.Kind == "AX")
                {
                    AxisTuning tuning = AxisTuningOf(target.Binding, target.Value);
                    if (!AssignJsonAxis(target, connectedFiles, tuning))
                    {
                        result.BindingsSkipped++;
                        result.Warnings.Add("Relation '" + target.Binding.RelationName + "' (" + target.ActionId + "): the BMS binding files of " + target.Device.Name + " have no entry for " + target.Value + ".");
                        continue;
                    }
                    axes.Add(new KeyValuePair<string, AxisTuning>(target.Value, tuning));
                }
                else
                    SetButton(target.Device.FileOf(target.Aircraft), target.Index, target.Value);
                GameOwnershipStore.Add(owned, target.ActionId, target.OwnerToken);
                // One bind written into several aircraft files counts once.
                if (written.Add(target.Binding))
                    result.BindingsWritten++;
            }

            if (HasDetentSettings())
                result.Warnings.Add("Throttle detents not written: JoyPro does not know yet how BMS 4.38.2's binding files store them. Set the AB and IDLE detents in the BMS launcher.");
            SaveJsonFiles(files, request.DryRun, result);
            if (!request.DryRun)
                GameOwnershipStore.Save(ownershipBase, GameId, owned);
            WriteGameSettings(request.ConfigPath, request.DryRun, request.Mode == GameExportMode.Merge || request.Mode == GameExportMode.Add, axes, result);
            return result;
        }

        private static bool HasDetentSettings()
        {
            Dictionary<string, string> store = InternalDataManagement.FfbOverrides;
            if (store == null)
                return false;
            foreach (string device in FfbSettings.DevicesFor(store, BmsSettings.Game))
            {
                if (device == FfbSettings.AnyDevice)
                    continue;
                Dictionary<string, double> values = FfbSettings.Resolve(store, device, BmsSettings.Game, null);
                if (values.ContainsKey(BmsSettings.AbDetent) || values.ContainsKey(BmsSettings.IdleDetent))
                    return true;
            }
            return false;
        }

        private static string EncodeJson(List<JsonDevice> files, GameDeviceDirectory devices, GameInputBinding binding, GameAction action, Dictionary<string, HashSet<string>> callbacks,
                                         Dictionary<string, int> notInAircraft, out List<JsonTarget> targets)
        {
            targets = new List<JsonTarget>();
            if (binding.IsKeyboard)
                return "keyboard keys belong to the BMS key file; JoyPro writes only joystick inputs.";
            JsonDevice device = JsonDeviceFor(files, binding.Device, devices);
            if (device == null)
                return "BMS has no binding file for '" + binding.Device + "' yet; start the BMS launcher once with it connected, close it and export again.";
            JoyInput input;
            if (!GameInputTokens.TryParse(binding.Input, out input))
                return "unsupported input '" + binding.Input + "'.";

            if (action.IsAxis)
            {
                if (input.Kind != JoyInputKind.Axis)
                    return "the action needs an axis.";
                if (binding.Reformers != null && binding.Reformers.Count > 0)
                    return "BMS axes cannot have modifiers.";
                string function = action.Id.Substring(AxisPrefix.Length);
                if (device.AxisFile == null || AxisEntry(device.AxisFile, function) == null)
                    return "the BMS binding files of " + device.Name + " have no entry for " + function + ".";
                targets.Add(new JsonTarget { Device = device, Kind = "AX", Index = input.Index, ActionId = action.Id, Value = function, Binding = binding });
                return string.Empty;
            }
            if (binding.Reformers != null && binding.Reformers.Count > 0)
                return "JoyPro does not write shifted binds (the DX shift layer) into BMS 4.38.2's new binding files yet; set it in the BMS launcher.";
            if (input.Kind == JoyInputKind.Pov)
                return "JoyPro does not write hat binds into BMS 4.38.2's new binding files yet; set it in the BMS launcher.";
            if (input.Kind != JoyInputKind.Button)
                return "'" + binding.Input + "' is an axis; bind it to an axis action.";
            if (device.Buttons > 0 && input.Index >= device.Buttons)
                return "BMS lists " + device.Buttons.ToString(CultureInfo.InvariantCulture) + " buttons for " + device.Name + ".";

            List<string> aircraft = device.Files.Select(f => f.Aircraft).ToList();
            if (binding.Aircraft != null && binding.Aircraft.Count > 0)
            {
                aircraft = aircraft.Where(a => binding.Aircraft.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
                if (aircraft.Count == 0)
                    return "limited to aircraft " + string.Join(", ", binding.Aircraft) + ", which BMS has no binding files for (BMS: " + string.Join(", ", device.Files.Select(f => f.Aircraft)) + ").";
            }
            List<string> possible = new List<string>();
            foreach (string name in aircraft)
            {
                HashSet<string> set;
                if (callbacks.TryGetValue(name, out set) && !set.Contains(action.Id))
                {
                    int count;
                    notInAircraft.TryGetValue(name, out count);
                    notInAircraft[name] = count + 1;
                    continue;
                }
                possible.Add(name);
            }
            if (possible.Count == 0)
                return "the key file of " + string.Join(", ", aircraft) + " has no such callback.";
            foreach (string name in possible)
                targets.Add(new JsonTarget { Device = device, Kind = "DX", Index = input.Index, Aircraft = name, ActionId = action.Id, Value = action.Id, Binding = binding });
            return string.Empty;
        }

        private static string OtherJsonAxisClaim(List<JsonDevice> files, JsonTarget target)
        {
            foreach (JsonDevice device in files)
            {
                JsonFile file = device.AxisFile;
                BmsJsonObject entry = file == null ? null : AxisEntry(file, target.Value);
                int? index = entry == null ? null : entry.Int("physical_axis_index");
                if (index.HasValue && (device != target.Device || index.Value != target.Index))
                    return device.Name + " " + GameInputTokens.AxisToken(index.Value);
            }
            return string.Empty;
        }

        // The function goes to the target axis in every aircraft file of the device; the same function elsewhere on the
        // connected devices and another function on the target axis are unassigned (one axis per function, one function
        // per axis, as the launcher does). False when the files have no entry for the function.
        private static bool AssignJsonAxis(JsonTarget target, List<JsonDevice> connectedFiles, AxisTuning tuning)
        {
            if (target.Device.Files.Any(f => AxisEntry(f, target.Value) == null))
                return false;
            foreach (JsonDevice device in connectedFiles.Concat(new[] { target.Device }).Distinct())
            {
                foreach (JsonFile file in device.Files)
                {
                    foreach (BmsJsonObject entry in Objects(file.Root.Array("axis_bindings")))
                    {
                        int? index = entry.Int("physical_axis_index");
                        if (!index.HasValue)
                            continue;
                        bool sameFunction = string.Equals(entry.String("logical_axis_name"), target.Value, StringComparison.OrdinalIgnoreCase);
                        bool targetAxis = device == target.Device && index.Value == target.Index;
                        if ((sameFunction && !targetAxis) || (!sameFunction && targetAxis))
                            ClearAxisEntry(file, entry);
                    }
                }
            }
            bool oneSided = BmsSettings.OneSided.Contains(target.Value);
            foreach (JsonFile file in target.Device.Files)
            {
                BmsJsonObject entry = AxisEntry(file, target.Value);
                SetJsonValue(file, entry, "physical_axis_index", BmsJsonNumber.Of(target.Index));
                SetJsonValue(file, entry, "invert", tuning.Inverted);
                SetJsonValue(file, entry, "saturation", Steps[BmsAxisModel.SaturationStep(tuning)]);
                SetJsonValue(file, entry, "deadzone", oneSided ? "None" : Steps[BmsAxisModel.DeadzoneStep(tuning)]);
            }
            return true;
        }

        // "My profile wins": inputs of connected devices that hold an action the profile binds elsewhere are released.
        private static void ReleaseOtherJsonInputs(List<JsonDevice> devices, Dictionary<string, JsonTarget> wanted)
        {
            HashSet<string> wantedAxes = new HashSet<string>(wanted.Values.Where(t => t.Kind == "AX").Select(t => t.Value), StringComparer.OrdinalIgnoreCase);
            foreach (JsonDevice device in devices)
            {
                foreach (JsonFile file in device.Files)
                {
                    foreach (BmsJsonObject entry in Objects(file.Root.Array("axis_bindings")))
                    {
                        int? index = entry.Int("physical_axis_index");
                        string function = entry.String("logical_axis_name") ?? string.Empty;
                        if (index.HasValue && wantedAxes.Contains(function) &&
                            !wanted.ContainsKey(new JsonTarget { Device = device, Kind = "AX", Index = index.Value }.OwnerToken))
                            ClearAxisEntry(file, entry);
                    }
                    HashSet<string> wantedButtons = new HashSet<string>(wanted.Values.Where(t => t.Kind == "DX" && string.Equals(t.Aircraft, file.Aircraft, StringComparison.OrdinalIgnoreCase)).Select(t => t.Value), StringComparer.OrdinalIgnoreCase);
                    foreach (BmsJsonObject entry in Objects(file.Root.Array("button_bindings")).Where(IsPressEntry).ToList())
                    {
                        int? button = entry.Int("button_index");
                        if (!button.HasValue || !wantedButtons.Contains(entry.String("callback_name") ?? string.Empty))
                            continue;
                        JsonTarget here = new JsonTarget { Device = device, Kind = "DX", Index = button.Value, Aircraft = file.Aircraft };
                        JsonTarget mine;
                        if (!wanted.TryGetValue(here.OwnerToken, out mine) || !string.Equals(mine.Value, entry.String("callback_name"), StringComparison.OrdinalIgnoreCase))
                            RemoveEntry(file, entry);
                    }
                }
            }
        }

        // Clean/nuke: every axis function (shared by all aircraft files) and unshifted press bind that JoyPro does not set
        // goes. Clean takes over the button binds only of the aircraft files JoyPro writes buttons into (an aircraft the
        // profile has no button of on this device keeps the binds made in the launcher); nuke (a device the profile does
        // not use) every file. The shift button stays while the files hold shifted binds.
        private static void ReleaseJsonDevice(JsonDevice device, Dictionary<string, JsonTarget> wanted, bool allAircraft)
        {
            HashSet<int> keepAxes = new HashSet<int>(wanted.Values.Where(t => t.Device == device && t.Kind == "AX").Select(t => t.Index));
            foreach (JsonFile file in device.Files)
            {
                foreach (BmsJsonObject entry in Objects(file.Root.Array("axis_bindings")))
                {
                    int? index = entry.Int("physical_axis_index");
                    if (index.HasValue && !keepAxes.Contains(index.Value))
                        ClearAxisEntry(file, entry);
                }
                if (!allAircraft && !wanted.Values.Any(t => t.Device == device && t.Kind == "DX" && string.Equals(t.Aircraft, file.Aircraft, StringComparison.OrdinalIgnoreCase)))
                    continue;
                bool shifted = Objects(file.Root.Array("button_bindings")).Any(e => !string.Equals(e.String("shift_state"), Unshifted, StringComparison.Ordinal));
                foreach (BmsJsonObject entry in Objects(file.Root.Array("button_bindings")).Where(IsPressEntry).ToList())
                {
                    int? button = entry.Int("button_index");
                    string callback = entry.String("callback_name") ?? string.Empty;
                    if (!button.HasValue || (shifted && callback == ShiftCallback))
                        continue;
                    JsonTarget mine;
                    if (wanted.TryGetValue(new JsonTarget { Device = device, Kind = "DX", Index = button.Value, Aircraft = file.Aircraft }.OwnerToken, out mine) &&
                        string.Equals(mine.Value, callback, StringComparison.OrdinalIgnoreCase))
                        continue;
                    RemoveEntry(file, entry);
                }
            }
        }

        private static void SaveJsonFiles(List<JsonDevice> devices, bool dryRun, GameIoResult result)
        {
            List<JsonFile> changed = devices.SelectMany(d => d.Files).Where(f => f.Changed).ToList();
            if (dryRun)
            {
                result.Steps.Add("Dry run: " + changed.Count.ToString(CultureInfo.InvariantCulture) + " BMS binding file(s) would change.");
                return;
            }
            foreach (JsonFile file in changed)
            {
                GameExportSafety.BackupBeforeOverwrite(file.Path, result.Warnings);
                byte[] text = new UTF8Encoding(false).GetBytes(BmsJson.Write(file.Root));
                byte[] bytes = file.HasBom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(text).ToArray() : text;
                FileAttributes attributes = File.GetAttributes(file.Path);
                bool readOnly = (attributes & FileAttributes.ReadOnly) != 0;
                try
                {
                    if (readOnly)
                        File.SetAttributes(file.Path, attributes & ~FileAttributes.ReadOnly);
                    File.WriteAllBytes(file.Path, bytes);
                    result.FilesWritten.Add(file.Path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    result.Errors.Add("Could not write '" + file.Path + "': " + ex.Message);
                }
                finally
                {
                    if (readOnly && File.Exists(file.Path))
                        File.SetAttributes(file.Path, attributes);
                }
            }
            result.Steps.Add(changed.Count == 0
                ? "The BMS binding files already match."
                : changed.Count.ToString(CultureInfo.InvariantCulture) + " BMS binding file(s) in User\\Config\\JSON changed. The BMS launcher builds the key files and the device setup from them when it closes or launches.");
        }

        private void RecordJsonImport(string configPath, List<GameInputBinding> applied, GameDeviceDirectory devices, GameIoResult result)
        {
            List<string> errors = new List<string>();
            List<JsonDevice> files = LoadJsonDevices(configPath, errors, new List<string>());
            if (errors.Count > 0)
                return;
            GameCatalog catalog = GameCatalogStore.Load(this, result.Warnings);
            Dictionary<string, HashSet<string>> callbacks = AircraftCallbacks(configPath, files.SelectMany(d => d.Files.Select(f => f.Aircraft)).Distinct(StringComparer.OrdinalIgnoreCase));
            string ownershipBase = OwnershipBase(configPath);
            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(ownershipBase, result.Warnings);
            foreach (GameInputBinding binding in applied)
            {
                GameAction action = catalog.Find(binding.ActionId);
                List<JsonTarget> targets;
                if (action != null && EncodeJson(files, devices, binding, action, callbacks, new Dictionary<string, int>(), out targets).Length == 0)
                {
                    foreach (JsonTarget target in targets)
                        GameOwnershipStore.Add(owned, action.Id, target.OwnerToken);
                }
            }
            GameOwnershipStore.Save(ownershipBase, GameId, owned);
            result.FilesWritten.Add(ownershipBase + GameOwnershipStore.Suffix);
        }

        // ---- aircraft (IGameAircraftScope) ---------------------------------------------------------------------------

        // --aircraft: BMS writes each relation into the aircraft it is for by itself, so the picked aircraft changes nothing
        // (an MSFS-style "export the relations limited to it for all aircraft" would put them into the wrong aircraft).
        private string requestedAircraft;

        public string ExportAircraft
        {
            get { return null; }
            set { requestedAircraft = value; }
        }

        // The aircraft list of refresh-db (DB\FalconBMS\aircraft.tsv) and the aircraft profiles on this PC.
        public IReadOnlyList<string> AircraftKeys()
        {
            List<string> keys = MsfsAircraftCatalog.Load(GameDataPaths.DbFile(DbFolder, AircraftFileName)).Select(a => a.Key).ToList();
            foreach (string config in SafeConfigPaths())
            {
                foreach (string aircraft in JsonAircraft(config))
                {
                    if (!keys.Contains(aircraft, StringComparer.OrdinalIgnoreCase))
                        keys.Add(aircraft);
                }
            }
            return keys;
        }

        private List<string> SafeConfigPaths()
        {
            try
            {
                return DiscoverConfigPaths(new List<string>());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new List<string>();
            }
        }

        public string PerAircraftNote
        {
            get
            {
                return "Falcon BMS 4.38.2 keeps the button binds of each aircraft (F-16, F-15ABCD) in files of their own: a relation limited to aircraft goes only into theirs; " +
                       "axes are shared by all aircraft. BMS up to 4.38.1 has one set of binds for every aircraft.";
            }
        }

        public bool WritesLimitedPerAircraft { get { return true; } }

        public bool HasExportAircraftOption { get { return false; } }

        // refresh-db: the aircraft profiles found with the key files (saved as DB\FalconBMS\aircraft.tsv).
        private List<string> pendingAircraft;

        public List<string> SaveRefreshFiles(List<string> warnings)
        {
            List<string> written = new List<string>();
            if (pendingAircraft == null)
                return written;
            List<string> keys = MsfsAircraftCatalog.Load(GameDataPaths.DbFile(DbFolder, AircraftFileName)).Select(a => a.Key).ToList();
            foreach (string aircraft in pendingAircraft)
            {
                if (!keys.Contains(aircraft, StringComparer.OrdinalIgnoreCase))
                    keys.Add(aircraft);
            }
            pendingAircraft = null;
            StringBuilder sb = new StringBuilder();
            sb.Append("# Falcon BMS aircraft profiles found by engine bms refresh-db (the launcher's key files BMS - Full[-<aircraft>].key; see docs/games/falcon-bms.md).\r\n");
            sb.Append("# key = the aircraft profile of BMS 4.38.2's binding files; relations use it as plane: --node FalconBMS:<action>:<key>.\r\n");
            sb.Append("# key\ttitle\r\n");
            foreach (string key in keys)
                sb.Append(key).Append('\t').Append(key).Append("\r\n");
            string path = GameDataPaths.DbFile(DbFolder, AircraftFileName);
            if (File.Exists(path) && string.Equals(File.ReadAllText(path, Encoding.UTF8), sb.ToString(), StringComparison.Ordinal))
                return written;
            string target = GameDataPaths.WritableDbFile(DbFolder, AircraftFileName);
            File.WriteAllText(target, sb.ToString(), new UTF8Encoding(false));
            written.Add(target);
            return written;
        }

        // The aircraft key files next to "BMS - Full.key" (BMS - Full-<suffix>.key) with the aircraft profile each one
        // is for: the keyboard binding file that names it (source_catalog_path), else the suffix.
        private static List<KeyValuePair<string, string>> SiblingAircraftKeyFiles(string fullKeyFile)
        {
            List<KeyValuePair<string, string>> files = new List<KeyValuePair<string, string>>();
            string folder = System.IO.Path.GetDirectoryName(fullKeyFile) ?? string.Empty;
            if (!Directory.Exists(folder))
                return files;
            Dictionary<string, string> named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in AircraftKeyFiles(folder, JsonAircraft(folder)))
                named[System.IO.Path.GetFileName(pair.Value)] = pair.Key;
            foreach (string file in Directory.GetFiles(folder, "BMS - Full-*.key").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                Match match = AircraftKeyFile.Match(System.IO.Path.GetFileName(file));
                if (!match.Success)
                    continue;
                string aircraft;
                if (!named.TryGetValue(System.IO.Path.GetFileName(file), out aircraft))
                    aircraft = match.Groups["suffix"].Value;
                files.Add(new KeyValuePair<string, string>(aircraft, file));
            }
            return files;
        }
    }
}
