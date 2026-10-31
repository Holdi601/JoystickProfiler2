using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // VTOL VR's own hardware input (Settings > Hardware Control Rudder > Bindings): only Rudder, Wheel Brake L and
    // Wheel Brake R, read through Rewired (Raw Input). The binds live in %APPDATA%\Boundless Dynamics, LLC\VTOLVR\SaveData\
    // inputbindings.cfg, a Boundless "ConfigNode" text file whose values are Rewired JoystickMap XML strings keyed by the
    // device model (hardwareIdentifier = "WindowsRawInput" + HID product string + PID + product GUID). JoyPro edits only
    // the three actions inside the Player0 joystick maps; every other line stays byte for byte (see docs/games/vtol-vr.md).
    internal sealed class VtolVrNativeIntegration : IGameIntegration, IGameSettingsExport, IGameSettingsReader, IGameSettingsSource
    {
        private static readonly Dictionary<string, int> ActionIds = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "Rudder", 0 }, { "Wheel Brake L", 5 }, { "Wheel Brake R", 6 }
        };

        // Pedals Rewired recognises (built-in definitions): their maps use the definition GUID instead of zeros.
        private static readonly Dictionary<string, string> KnownPedals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "044F:B679", "bdd20bc1-5619-4361-91e0-d652ce750d73" },
            { "06A3:0763", "01665f95-8b2d-42d0-8d54-3bc9b0d16b1d" },
            { "06A3:0764", "01665f95-8b2d-42d0-8d54-3bc9b0d16b1d" },
            { "06A3:0765", "01665f95-8b2d-42d0-8d54-3bc9b0d16b1d" },
            { "068E:00F2", "3049c96e-9ffd-4088-a136-e92a4d92e2b1" }
        };

        private const string MapPrefix = "RewiredSaveData|playerName=Player0|dataType=ControllerMap|controllerMapType=JoystickMap|categoryId=0|layoutId=0|";
        private const string KnownIdsPrefix = "RewiredSaveData|playerName=Player0|dataType=ControllerMap_KnownActionIds|controllerMapType=JoystickMap|categoryId=0|layoutId=0|";

        public string GameId { get { return "VTOLVRNative"; } }
        public string DisplayName { get { return "VTOL VR (native rudder and brakes)"; } }
        public IReadOnlyList<string> Aliases { get { return new[] { "vtolvr-native", "vtolnative" }; } }
        public string DbFolder { get { return "VTOLVRNative"; } }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return true; } }
        public string Notes
        {
            get
            {
                return "Experimental. VTOL VR itself binds only rudder and toe brakes (full HOTAS: 'vtolvr' with the BYOJoystick mod). " +
                       "Start VTOL VR once with the pedals connected, switch on Settings > Hardware Control Rudder, then close the game before exporting.";
            }
        }
        public IReadOnlyList<string> ProcessNames { get { return new[] { "VTOLVR" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        public GameFeature KeyboardBinds { get { return GameFeature.No; } }
        public GameFeature ModifierBinds { get { return GameFeature.No; } }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Boundless Dynamics, LLC", "VTOLVR", "SaveData", "inputbindings.cfg");
            return File.Exists(path) ? new List<string> { path } : new List<string>();
        }

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            ConfigNodeFile file;
            if (!ConfigNodeFile.TryLoad(configPath, "RewiredSaveData", result.Errors, out file))
                return result;
            Dictionary<string, int> actionIds = ActionIdsOf(catalog);
            foreach (ConfigNodeFile.Entry entry in file.Entries.Where(e => e.Key.StartsWith(MapPrefix, StringComparison.Ordinal)))
            {
                string identifier = KeyPart(entry.Key, "hardwareIdentifier");
                GameDevice device = devices == null ? null : devices.Devices.FirstOrDefault(d => HardwareIdentifier(d) == identifier);
                List<RewiredElementMap> maps;
                if (!RewiredJoystickMap.TryRead(entry.Value, out maps))
                {
                    result.Warnings.Add("Unreadable joystick map for " + identifier + ".");
                    continue;
                }
                foreach (RewiredElementMap map in maps.Where(m => actionIds.ContainsValue(m.ActionId)))
                {
                    string action = actionIds.First(a => a.Value == map.ActionId).Key;
                    if (device == null)
                    {
                        result.Warnings.Add(action + " is bound on '" + identifier + "', which is not connected.");
                        continue;
                    }
                    if (map.ElementType != 0)
                    {
                        result.Warnings.Add(action + " is bound to a button of " + device.JoyProName + "; JoyPro reads axes only.");
                        continue;
                    }
                    devices.EnsureCapabilities(device, result.Warnings);
                    int axis = AxisFromElement(device, map.ElementId);
                    if (axis < 0)
                    {
                        result.Warnings.Add(action + ": element " + map.ElementId.ToString(CultureInfo.InvariantCulture) + " of " + device.JoyProName + " is no axis JoyPro knows.");
                        continue;
                    }
                    // The element map's axis range (the toe brake full-travel fix) becomes the relation's VTOLVRNative
                    // tuning (vtolnative axis model); Full needs nothing.
                    AxisTuning tuning = new AxisTuning { Inverted = map.Invert, Slider = map.ActionId != 0 };
                    if (map.AxisRange > 0)
                        tuning.Native["axisRange"] = map.AxisRange;
                    result.Bindings.Add(new GameInputBinding { ActionId = action, Device = device.JoyProName, Input = GameInputTokens.AxisToken(axis), IsAxis = true, Inverted = map.Invert, Tuning = tuning });
                }
            }
            return result;
        }

        // Whether the profile holds game-wide settings for the game; an export without binds still writes them through
        // Write with SettingsOnly, which touches no bind.
        public bool HasSettings()
        {
            return InternalDataManagement.FfbOverrides != null && FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, SettingsGame, null).Count > 0;
        }

        public GameIoResult WriteSettings(GameExportRequest request)
        {
            request.SettingsOnly = true;
            return Write(request);
        }

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            ConfigNodeFile file;
            if (!ConfigNodeFile.TryLoad(request.ConfigPath, "RewiredSaveData", result.Errors, out file))
                return result;
            Dictionary<string, double> settings = FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, SettingsGame, null);
            if (request.SettingsOnly)
            {
                WriteGameSettings(request, settings, result);
                return result;
            }

            // Wanted element maps per device key; one bind per action (the game's own rule).
            Dictionary<string, List<RewiredElementMap>> wanted = new Dictionary<string, List<RewiredElementMap>>(StringComparer.Ordinal);
            Dictionary<string, string> hardwareNames = new Dictionary<string, string>(StringComparer.Ordinal);
            HashSet<int> boundActions = new HashSet<int>();
            Dictionary<string, int> actionIds = ActionIdsOf(request.Catalog);
            // The profile's bind behind each wanted element map (for the "Only add" report).
            Dictionary<RewiredElementMap, GameInputBinding> sources = new Dictionary<RewiredElementMap, GameInputBinding>();
            foreach (GameInputBinding binding in request.Bindings)
            {
                int actionId;
                if (!actionIds.TryGetValue(binding.ActionId, out actionId))
                {
                    Skip(result, binding, "VTOL VR binds only its hardware actions (Rudder, Wheel Brake L and Wheel Brake R; refresh-db adds new ones).");
                    continue;
                }
                if (boundActions.Contains(actionId))
                {
                    Skip(result, binding, "VTOL VR takes one bind per action; another relation already binds it.");
                    continue;
                }
                JoyInput input;
                if (binding.IsKeyboard || !GameInputTokens.TryParse(binding.Input, out input) || input.Kind != JoyInputKind.Axis)
                {
                    Skip(result, binding, "needs a joystick axis.");
                    continue;
                }
                string name;
                Guid guid;
                GameDevice device = GameDeviceDirectory.TryParseJoyProName(binding.Device, out name, out guid) && request.Devices != null ? request.Devices.FindByInstanceGuid(guid) : null;
                if (device == null || !device.HasVidPid)
                {
                    Skip(result, binding, "device '" + binding.Device + "' is not connected (VTOL VR keys maps by USB product, JoyPro needs it connected).");
                    continue;
                }
                string key = FindMapKey(file, device);
                if (key.Length == 0)
                {
                    Skip(result, binding, "VTOL VR has not seen '" + device.JoyProName + "' yet. Start VTOL VR once with it connected, then export again.");
                    continue;
                }
                request.Devices.EnsureCapabilities(device, result.Warnings);
                int element = ElementFromAxis(device, input.Index, result.Warnings);
                if (element < 0)
                {
                    Skip(result, binding, "the device has no " + binding.Input + ".");
                    continue;
                }
                List<RewiredElementMap> list;
                if (!wanted.TryGetValue(key, out list))
                    wanted[key] = list = new List<RewiredElementMap>();
                int axisRange = AxisRangeFor(binding, actionId, key, result);
                RewiredElementMap elementMap = new RewiredElementMap { ActionId = actionId, ElementType = 0, ElementId = element, AxisRange = axisRange, Invert = binding.Inverted };
                list.Add(elementMap);
                sources[elementMap] = binding;
                hardwareNames[key] = string.IsNullOrWhiteSpace(device.HidProductString) ? name : device.HidProductString.Trim();
                boundActions.Add(actionId);
                result.BindingsWritten++;
            }

            // Every joystick map of Player0 (the game uses every bound device at once). JoyPro releases only what the
            // import reads: axis element maps of connected devices; button maps and devices that are not connected stay.
            HashSet<int> managed = new HashSet<int>(actionIds.Values);
            bool overwrite = request.Mode == GameExportMode.Clean || request.Mode == GameExportMode.MergeOverwrite;
            int changed = 0;
            foreach (ConfigNodeFile.Entry entry in file.Entries.Where(e => e.Key.StartsWith(MapPrefix, StringComparison.Ordinal)).ToList())
            {
                List<RewiredElementMap> mine;
                bool joyPro = wanted.TryGetValue(entry.Key, out mine);
                mine = mine ?? new List<RewiredElementMap>();
                string identifier = KeyPart(entry.Key, "hardwareIdentifier");
                bool connected = request.Devices != null && request.Devices.Devices.Any(d => HardwareIdentifier(d) == identifier);
                List<RewiredElementMap> existing;
                if (!RewiredJoystickMap.TryRead(entry.Value, out existing))
                {
                    if (joyPro || overwrite || request.NukeUnusedDevices)
                        result.Warnings.Add("Unreadable joystick map left as it is: " + KeyPart(entry.Key, "hardwareIdentifier"));
                    wanted.Remove(entry.Key);
                    continue;
                }
                Func<RewiredElementMap, RewiredElementMap, bool> sameInput = (a, b) => a.ElementType == b.ElementType && a.ElementId == b.ElementId;
                List<RewiredElementMap> add;
                Func<RewiredElementMap, bool> remove;
                if (request.Mode == GameExportMode.Add)
                {
                    // "Only add" never changes or removes an element map: the action already on its axis is nothing to
                    // do, another action there keeps the axis and the profile's bind is reported (no multi-bind).
                    add = new List<RewiredElementMap>();
                    foreach (RewiredElementMap want in mine)
                    {
                        if (existing.Any(m => m.ActionId == want.ActionId && sameInput(m, want)))
                            continue;
                        RewiredElementMap taken = existing.FirstOrDefault(m => m.ActionId != want.ActionId && sameInput(m, want));
                        GameInputBinding source;
                        if (taken != null)
                        {
                            result.BindingsSkipped++;
                            if (sources.TryGetValue(want, out source))
                                result.SkippedTakenInputs.Add(SkippedMultiBind(source, ActionName(actionIds, taken.ActionId)));
                            continue;
                        }
                        add.Add(want);
                    }
                    remove = m => false;
                }
                else
                {
                    // An element map the profile has as it is stays as it is (no rewrite, the line keeps its bytes).
                    add = mine.Where(w => !existing.Any(m => m.SameBind(w))).ToList();
                    remove = m =>
                    {
                        if (mine.Any(w => w.SameBind(m)))
                            return false;
                        // The profile's action on its axis is rewritten; another action there is a conflict and goes.
                        if (mine.Any(w => sameInput(w, m)))
                            return true;
                        bool owns = connected && m.ElementType == 0 && managed.Contains(m.ActionId);
                        // Clean: the devices JoyPro writes keep only the profile's actions; nuke: connected devices
                        // without binds in the export lose theirs; "my profile wins": the profile's actions leave their
                        // other axes.
                        return owns && ((request.Mode == GameExportMode.Clean && joyPro) || (request.NukeUnusedDevices && !joyPro) ||
                                        (overwrite && boundActions.Contains(m.ActionId)));
                    };
                }
                string updated;
                if (!RewiredJoystickMap.TryEdit(entry.Value, remove, add, out updated))
                {
                    result.Warnings.Add("Unreadable joystick map left as it is: " + KeyPart(entry.Key, "hardwareIdentifier"));
                    continue;
                }
                if (updated != entry.Value)
                {
                    file.SetValue(entry.Key, updated);
                    changed++;
                }
                wanted.Remove(entry.Key);
            }            // Devices the game listed but never saved a map for: a new blank map with the wanted actions.
            foreach (KeyValuePair<string, List<RewiredElementMap>> map in wanted)
            {
                string guid = KeyPart(map.Key, "hardwareGuid");
                file.SetValue(map.Key, RewiredJoystickMap.Blank(hardwareNames[map.Key], guid, map.Value));
                file.SetValue(KnownIdsPrefix + map.Key.Substring(MapPrefix.Length), "0,2,3,4,5,6");
                changed++;
            }

            WarnBrakeR(file, result.Warnings);

            if (request.DryRun)
                result.Steps.Add("Dry run: " + changed.ToString(CultureInfo.InvariantCulture) + " joystick map(s) would change.");
            else if (changed == 0)
                result.Steps.Add("inputbindings.cfg already matches.");
            else
            {
                GameExportSafety.BackupBeforeOverwrite(request.ConfigPath, result.Warnings);
                file.Save(request.ConfigPath);
                result.FilesWritten.Add(request.ConfigPath);
                result.Steps.Add(changed.ToString(CultureInfo.InvariantCulture) + " joystick map(s) changed.");
            }
            WriteGameSettings(request, settings, result);
            if (!settings.ContainsKey("HARDWARE_RUDDER"))
                WarnHardwareRudder(request.ConfigPath, result.Warnings);
            else if (settings["HARDWARE_RUDDER"] == 0)
                result.Warnings.Add("Hardware Control Rudder is off in the profile's VTOL VR settings: the game ignores these axes until it is on.");
            return result;
        }

        private static string ActionName(Dictionary<string, int> actionIds, int actionId)
        {
            foreach (KeyValuePair<string, int> action in actionIds)
            {
                if (action.Value == actionId)
                    return action.Key;
            }
            return "action " + actionId.ToString(CultureInfo.InvariantCulture);
        }

        private ExportSkippedBind SkippedMultiBind(GameInputBinding binding, string gameAction)
        {
            return new ExportSkippedBind
            {
                Game = GameId,
                Device = binding.Device ?? string.Empty,
                Input = binding.Input ?? string.Empty,
                Relation = binding.RelationName ?? string.Empty,
                Action = binding.ActionId ?? string.Empty,
                GameAction = gameAction ?? string.Empty
            };
        }

        public string SettingsGame { get { return VtolVrGameSettings.Game; } }

        // The element map's axisRange: brakes take the vtolnative tuning's axisRange (0 Full, 1 Positive = the
        // full-travel fix); the rudder is always Full. VTRWGetAxis reads a brake as 0..1, so a Full map of a device
        // Rewired does not know (hardwareGuid zeros: no 0..1 pedal definition) brakes only on half the travel.
        private static int AxisRangeFor(GameInputBinding binding, int actionId, string mapKey, GameIoResult result)
        {
            double wanted = binding.Tuning == null ? 0 : binding.Tuning.NativeOr("axisRange", 0);
            int axisRange = wanted >= 0.5 ? 1 : 0;
            if (actionId == 0)
            {
                if (axisRange != 0)
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (Rudder): the rudder is a full axis; axisRange is written for the toe brakes only.");
                return 0;
            }
            bool unknownDevice = KeyPart(mapKey, "hardwareGuid") == Guid.Empty.ToString("D");
            if (axisRange == 0 && unknownDevice)
                result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): VTOL VR reads a toe brake as 0..1, so a pedal axis that reports -1..1 brakes only on the second half of its travel (inverted: fully at mid travel). For the whole travel set the VTOLVRNative tuning 'axisRange' to 1 (Positive; the community fix for Thrustmaster TPR, with Invert as your pedal needs): engine binds tuning set \"" + binding.RelationName + "\" --scope game:VTOLVRNative --param axisRange=1.");
            return axisRange;
        }

        // The game takes the brakes from the hardware only when Wheel Brake L (action 5) is bound (VehicleInputManager
        // checks action 5 twice, never 6): a right brake alone does nothing and both brakes stay on the throttle trigger.
        private static void WarnBrakeR(ConfigNodeFile file, List<string> warnings)
        {
            bool left = false;
            bool right = false;
            foreach (ConfigNodeFile.Entry entry in file.Entries.Where(e => e.Key.StartsWith(MapPrefix, StringComparison.Ordinal)))
            {
                List<RewiredElementMap> maps;
                if (!RewiredJoystickMap.TryRead(entry.Value, out maps))
                    continue;
                left |= maps.Any(m => m.ActionId == 5);
                right |= maps.Any(m => m.ActionId == 6);
            }
            if (right && !left)
                warnings.Add("Only Wheel Brake R is bound: VTOL VR uses the hardware brakes only when Wheel Brake L is bound (it never checks R), so both brakes stay on the throttle trigger. Bind Wheel Brake L too.");
        }

        // Settings > Controls (engine ffb set --device * --game VTOLVR): gameSettings.cfg next to inputbindings.cfg.
        private static void WriteGameSettings(GameExportRequest request, Dictionary<string, double> settings, GameIoResult result)
        {
            if (settings.Count == 0)
                return;
            string path = SettingsPath(request.ConfigPath);
            if (!File.Exists(path))
            {
                result.Warnings.Add("VTOL VR settings not written: " + path + " does not exist yet (start the game once and leave its Settings with OK).");
                return;
            }
            string text = File.ReadAllText(path, new UTF8Encoding(false));
            List<string> changes = new List<string>();
            string error;
            string updated = VtolGameSettingsFile.Apply(text, settings, changes, out error);
            if (updated == null)
            {
                result.Warnings.Add("VTOL VR settings not written: " + error);
                return;
            }
            if (changes.Count == 0)
            {
                result.Steps.Add("gameSettings.cfg already matches.");
                return;
            }
            if (request.DryRun)
            {
                result.Steps.Add("Dry run: gameSettings.cfg would change: " + string.Join(", ", changes) + ".");
                return;
            }
            GameExportSafety.BackupBeforeOverwrite(path, result.Warnings);
            string temp = path + ".jptmp";
            File.WriteAllText(temp, updated, new UTF8Encoding(false));
            SafeFileIo.CopyOverAndDelete(temp, path);
            result.FilesWritten.Add(path);
            result.Steps.Add("gameSettings.cfg: " + string.Join(", ", changes) + " (Steam Cloud syncs this file at the next start).");
        }

        // engine sync: the Settings > Controls values of gameSettings.cfg next to inputbindings.cfg (game VTOLVR).
        public Dictionary<string, double> ReadGameSettings(string configPath, List<string> warnings)
        {
            string path = SettingsPath(configPath);
            if (!File.Exists(path))
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            string error;
            Dictionary<string, double> values = VtolGameSettingsFile.Read(File.ReadAllText(path, new UTF8Encoding(false)), out error);
            if (values == null)
            {
                warnings?.Add("VTOL VR settings not read: " + error);
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }
            return values;
        }

        public List<string> SettingsFiles(string configPath)
        {
            return new List<string> { SettingsPath(configPath) };
        }

        private static string SettingsPath(string inputBindingsPath)
        {
            return Path.Combine(Path.GetDirectoryName(inputBindingsPath) ?? string.Empty, "gameSettings.cfg");
        }

        // The game's Rewired action ids: the three known ones plus catalog rows a refresh added (fifth column).
        private static Dictionary<string, int> ActionIdsOf(GameCatalog catalog)
        {
            Dictionary<string, int> ids = new Dictionary<string, int>(ActionIds, StringComparer.Ordinal);
            foreach (GameAction action in catalog == null ? new List<GameAction>() : catalog.Actions)
            {
                int id;
                if (!ids.ContainsKey(action.Id) && int.TryParse(action.NativeId, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id >= 0 && !ids.ContainsValue(id))
                    ids[action.Id] = id;
            }
            return ids;
        }

        // Reads the Rewired InputManager of the installed game (VTOLVR_Data\level0) and adds the user-assignable actions
        // of user-assignable categories the catalog does not have yet (Settings > Bindings lists exactly those). Known
        // actions keep their title and category; the mouse actions of the map editors are not assignable.
        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                GameInstall install = GameInstalls.Find(GameId, result.Warnings);
                sourcePath = install != null && install.Installed ? install.InstallPath : null;
            }
            string level0 = FindLevel0(sourcePath);
            if (level0 == null)
            {
                result.Errors.Add("VTOL VR's VTOLVR_Data\\level0 was not found. Pass --path with the game folder, its VTOLVR_Data folder or the file.");
                return null;
            }
            List<RewiredActionRecord> records;
            Dictionary<int, string> categories;
            string error;
            if (!RewiredInputManagerData.TryParse(File.ReadAllBytes(level0), RewiredLayout.VtolVr, out records, out categories, out error))
            {
                result.Errors.Add(level0 + ": " + error);
                return null;
            }

            GameCatalog catalog = new GameCatalog { GameId = GameId, SourcePath = level0 };
            foreach (GameAction action in current == null ? new List<GameAction>() : current.Actions)
                catalog.Add(action);
            // A new action takes the category of the known actions of its Rewired category (all three are "Default").
            Dictionary<int, string> knownCategory = new Dictionary<int, string>();
            foreach (RewiredActionRecord record in records)
            {
                GameAction known = catalog.Find(record.Name);
                if (known != null && !knownCategory.ContainsKey(record.CategoryId))
                    knownCategory[record.CategoryId] = known.Category;
            }
            int added = 0;
            foreach (RewiredActionRecord record in records.Where(r => r.UserAssignable && catalog.Find(r.Name) == null))
            {
                string category;
                if (!knownCategory.TryGetValue(record.CategoryId, out category) && !categories.TryGetValue(record.CategoryId, out category))
                    category = "Category " + record.CategoryId.ToString(CultureInfo.InvariantCulture);
                string title = Regex.Replace(record.DescriptiveName ?? string.Empty, @"[\t\r\n]+", " ").Trim();
                catalog.Add(new GameAction
                {
                    Id = record.Name,
                    Title = title.Length > 0 ? title : record.Name,
                    IsAxis = record.Type == 0,
                    Category = category,
                    NativeId = record.Id.ToString(CultureInfo.InvariantCulture)
                });
                added++;
            }
            foreach (GameAction gone in catalog.Actions.Where(a => !records.Any(r => r.Name == a.Id)))
                result.Warnings.Add("'" + gone.Id + "' is no longer in the game's Rewired action list.");
            result.Steps.Add("Read " + records.Count.ToString(CultureInfo.InvariantCulture) + " Rewired action(s) from " + level0 + " (" +
                             records.Count(r => r.UserAssignable).ToString(CultureInfo.InvariantCulture) + " user-assignable): " +
                             added.ToString(CultureInfo.InvariantCulture) + " new.");
            return catalog;
        }

        private static string FindLevel0(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                return null;
            string path = sourcePath.Trim().Trim('"');
            if (File.Exists(path))
                return path;
            foreach (string candidate in new[] { Path.Combine(path, "VTOLVR_Data", "level0"), Path.Combine(path, "level0") })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        private static void Skip(GameIoResult result, GameInputBinding binding, string reason)
        {
            result.BindingsSkipped++;
            result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
        }

        // Rewired's Raw Input id of a device model: "WindowsRawInput" + HID product string + PID (decimal) + product GUID,
        // all whitespace removed.
        internal static string HardwareIdentifier(GameDevice device)
        {
            if (device == null || !device.HasVidPid)
                return string.Empty;
            string product = string.IsNullOrWhiteSpace(device.HidProductString) ? "Unknown" : device.HidProductString.Replace("\x1A", string.Empty);
            string productGuid = GameDeviceDirectory.BuildPidVidGuid(device.VendorId, device.ProductId).ToString("D").ToLowerInvariant();
            return Regex.Replace("WindowsRawInput" + product + device.ProductId.ToString(CultureInfo.InvariantCulture) + productGuid, @"\s*", string.Empty);
        }

        // The map key of a device: an existing map, else the device as the game listed it (assignments, calibration).
        private static string FindMapKey(ConfigNodeFile file, GameDevice device)
        {
            string identifier = HardwareIdentifier(device);
            ConfigNodeFile.Entry map = file.Entries.FirstOrDefault(e => e.Key.StartsWith(MapPrefix, StringComparison.Ordinal) && KeyPart(e.Key, "hardwareIdentifier") == identifier);
            if (map != null)
                return map.Key;
            ConfigNodeFile.Entry calibration = file.Entries.FirstOrDefault(e => e.Key.Contains("|dataType=CalibrationMap|") && KeyPart(e.Key, "hardwareIdentifier") == identifier);
            bool assigned = file.Entries.Any(e => e.Key == "RewiredSaveData_ControllerAssignments" && e.Value.Contains("\"hardwareIdentifier\":\"" + identifier + "\""));
            if (calibration == null && !assigned)
                return string.Empty;
            string guid = calibration != null ? KeyPart(calibration.Key, "hardwareGuid") : string.Empty;
            if (guid.Length == 0)
            {
                string known;
                guid = KnownPedals.TryGetValue(GameDeviceDirectory.VidPidKey(device.VendorId, device.ProductId), out known) ? known : Guid.Empty.ToString("D");
            }
            return MapPrefix + "hardwareIdentifier=" + identifier + "|hardwareGuid=" + guid;
        }

        private static string KeyPart(string key, string name)
        {
            foreach (string part in key.Split('|'))
            {
                if (part.StartsWith(name + "=", StringComparison.Ordinal))
                    return part.Substring(name.Length + 1);
            }
            return string.Empty;
        }

        // Raw Input numbers a device's axes in HID order: Axis n = the n-th axis the device has of X, Y, Z, RX, RY, RZ,
        // Slider 1, Slider 2 (DirectInput order with missing axes skipped). Unverified until one real file confirms it.
        private static int ElementFromAxis(GameDevice device, int axis, List<string> warnings)
        {
            List<int> present = (device.AxisSlots ?? new List<int>()).Distinct().OrderBy(a => a).ToList();
            if (present.Count == 0)
            {
                warnings.Add("The axes of " + device.JoyProName + " are unknown; JoyPro assumes element = DirectInput axis number.");
                return axis;
            }
            return present.IndexOf(axis);
        }

        private static int AxisFromElement(GameDevice device, int element)
        {
            List<int> present = (device.AxisSlots ?? new List<int>()).Distinct().OrderBy(a => a).ToList();
            if (present.Count == 0)
                return element >= 0 && element < 8 ? element : -1;
            return element >= 0 && element < present.Count ? present[element] : -1;
        }

        private static void WarnHardwareRudder(string inputBindingsPath, List<string> warnings)
        {
            string settings = Path.Combine(Path.GetDirectoryName(inputBindingsPath) ?? string.Empty, "gameSettings.cfg");
            if (!File.Exists(settings))
                return;
            Match match = Regex.Match(File.ReadAllText(settings, Encoding.UTF8), @"^\s*HARDWARE_RUDDER\s*=\s*([0-9.]+)", RegexOptions.Multiline);
            double value;
            if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value <= 0)
                warnings.Add("VTOL VR ignores these axes until Settings > Hardware Control Rudder is on.");
        }
    }

    internal sealed class RewiredElementMap
    {
        public int ActionId;
        public int ElementType;
        public int ElementId;
        public int AxisRange;
        public bool Invert;
        // Pole of an axis action a button (or half axis) drives: 0 Positive, 1 Negative.
        public int AxisContribution;
        // As the game's default maps write it (0); Rewired writes -1 for entries the user rebound.
        public int ActionCategoryId;

        // Same bind (the category id is bookkeeping only).
        public bool SameBind(RewiredElementMap other)
        {
            return other != null && ActionId == other.ActionId && ElementType == other.ElementType && ElementId == other.ElementId &&
                   AxisRange == other.AxisRange && Invert == other.Invert && AxisContribution == other.AxisContribution;
        }
    }

    // Rewired JoystickMap XML (one line, as Rewired's XmlWriter writes it): read and replace ActionElementMaps of some
    // actions; everything else in the map stays.
    internal static class RewiredJoystickMap
    {
        private static readonly XNamespace Ns = "http://guavaman.com/rewired";

        public static bool TryRead(string xml, out List<RewiredElementMap> maps)
        {
            maps = new List<RewiredElementMap>();
            XDocument document;
            if (!TryParse(xml, out document))
                return false;
            maps.AddRange(document.Descendants(Ns + "ActionElementMap").Select(ReadElement));
            return true;
        }

        public static bool TrySetActions(string xml, ISet<int> replace, List<RewiredElementMap> add, out string updated)
        {
            updated = xml;
            XDocument document;
            if (!TryParse(xml, out document) || document.Root == null)
                return false;
            List<XElement> remove = document.Descendants(Ns + "ActionElementMap").Where(e => replace.Contains(Int(e, "actionId"))).ToList();
            bool alreadyThere = remove.Count == add.Count && add.All(a => remove.Any(r => Matches(r, a)));
            if (alreadyThere)
                return true;
            foreach (XElement element in remove)
                element.Remove();
            foreach (RewiredElementMap map in add)
                ListFor(document.Root, map).Add(Element(map));
            updated = Render(document);
            return true;
        }

        // Removes the element maps the predicate picks and appends the new ones (buttons to <buttonMaps>, axes to
        // <axisMaps>); every other element map, attribute and element of the map stays. Unchanged maps come back as
        // the same string.
        public static bool TryEdit(string xml, Func<RewiredElementMap, bool> remove, List<RewiredElementMap> add, out string updated)
        {
            updated = xml;
            XDocument document;
            if (!TryParse(xml, out document) || document.Root == null)
                return false;
            List<XElement> removed = document.Descendants(Ns + "ActionElementMap").Where(e => remove(ReadElement(e))).ToList();
            if (removed.Count == 0 && (add == null || add.Count == 0))
                return true;
            foreach (XElement element in removed)
                element.Remove();
            foreach (RewiredElementMap map in add ?? new List<RewiredElementMap>())
                ListFor(document.Root, map).Add(Element(map));
            updated = Render(document);
            return true;
        }

        private static XElement ListFor(XElement root, RewiredElementMap map)
        {
            string name = map.ElementType == 1 ? "buttonMaps" : "axisMaps";
            XElement list = root.Element(Ns + name);
            if (list == null)
            {
                list = new XElement(Ns + name);
                if (name == "buttonMaps" && root.Element(Ns + "axisMaps") != null)
                    root.Element(Ns + "axisMaps").AddBeforeSelf(list);
                else
                    root.Add(list);
            }
            return list;
        }

        private static RewiredElementMap ReadElement(XElement element)
        {
            return new RewiredElementMap
            {
                ActionId = Int(element, "actionId"),
                ElementType = Int(element, "elementType"),
                ElementId = Int(element, "elementIdentifierId"),
                AxisRange = Int(element, "axisRange"),
                Invert = string.Equals((string)element.Element(Ns + "invert"), "true", StringComparison.OrdinalIgnoreCase),
                AxisContribution = Int(element, "axisContribution"),
                ActionCategoryId = Int(element, "actionCategoryId")
            };
        }

        public static string Blank(string hardwareName, string hardwareGuid, List<RewiredElementMap> maps, int mapCategory = 0)
        {
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            XElement root = new XElement(Ns + "JoystickMap",
                new XAttribute("dataVersion", "2"),
                new XAttribute("hardwareGuid", hardwareGuid),
                new XAttribute("hardwareName", hardwareName),
                new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
                new XAttribute(xsi + "schemaLocation", "http://guavaman.com/rewired http://guavaman.com/schemas/rewired/1.1/JoystickMap.xsd"),
                new XAttribute("xmlns", Ns.NamespaceName),
                new XElement(Ns + "sourceMapId", "-1"),
                new XElement(Ns + "categoryId", mapCategory.ToString(CultureInfo.InvariantCulture)),
                new XElement(Ns + "layoutId", "0"),
                new XElement(Ns + "name"),
                new XElement(Ns + "hardwareGuid", hardwareGuid),
                new XElement(Ns + "enabled", "true"),
                new XElement(Ns + "buttonMaps", maps.Where(m => m.ElementType == 1).Select(Element)),
                new XElement(Ns + "axisMaps", maps.Where(m => m.ElementType != 1).Select(Element)));
            return Render(new XDocument(root));
        }

        private static bool Matches(XElement element, RewiredElementMap map)
        {
            return Int(element, "actionId") == map.ActionId && Int(element, "elementType") == map.ElementType && Int(element, "elementIdentifierId") == map.ElementId &&
                   Int(element, "axisRange") == map.AxisRange &&
                   string.Equals((string)element.Element(Ns + "invert"), map.Invert ? "true" : "false", StringComparison.Ordinal);
        }

        private static XElement Element(RewiredElementMap map)
        {
            return new XElement(Ns + "ActionElementMap",
                new XElement(Ns + "actionCategoryId", map.ActionCategoryId.ToString(CultureInfo.InvariantCulture)),
                new XElement(Ns + "actionId", map.ActionId.ToString(CultureInfo.InvariantCulture)),
                new XElement(Ns + "elementType", map.ElementType.ToString(CultureInfo.InvariantCulture)),
                new XElement(Ns + "elementIdentifierId", map.ElementId.ToString(CultureInfo.InvariantCulture)),
                new XElement(Ns + "axisRange", map.AxisRange.ToString(CultureInfo.InvariantCulture)),
                new XElement(Ns + "invert", map.Invert ? "true" : "false"),
                new XElement(Ns + "axisContribution", map.AxisContribution.ToString(CultureInfo.InvariantCulture)),
                new XElement(Ns + "keyboardKeyCode", "0"),
                new XElement(Ns + "modifierKey1", "0"),
                new XElement(Ns + "modifierKey2", "0"),
                new XElement(Ns + "modifierKey3", "0"),
                new XElement(Ns + "enabled", "true"));
        }

        private static int Int(XElement parent, string name)
        {
            int value;
            return int.TryParse((string)parent.Element(Ns + name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : -1;
        }

        private static bool TryParse(string xml, out XDocument document)
        {
            document = null;
            try
            {
                // The declaration says utf-16 although the text is UTF-8 in the file: parse the string, not bytes.
                document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                return document.Root != null;
            }
            catch (XmlException)
            {
                return false;
            }
        }

        // XmlWriter defaults over a StringWriter, like Rewired: one line, utf-16 declaration, "<x />" for empty elements.
        private static string Render(XDocument document)
        {
            StringBuilder sb = new StringBuilder();
            using (StringWriter writer = new StringWriter(sb, CultureInfo.InvariantCulture))
            using (XmlWriter xml = XmlWriter.Create(writer, new XmlWriterSettings { Indent = false }))
                document.Save(xml);
            return sb.ToString();
        }
    }

    // Boundless Dynamics "ConfigNode" text file with one root node of values: "\t{key} = {value}" lines, LF line ends,
    // ends with "}\n\r\n". Lines JoyPro does not change keep their exact text.
    internal sealed class ConfigNodeFile
    {
        internal sealed class Entry
        {
            public string Key = string.Empty;
            public string Value = string.Empty;
            public int Line = -1;
        }

        private List<string> lines;
        private string ending = string.Empty;

        public List<Entry> Entries { get; private set; }

        public static bool TryLoad(string path, string rootName, List<string> errors, out ConfigNodeFile file)
        {
            file = null;
            if (!File.Exists(path))
            {
                errors.Add("File not found: " + path);
                return false;
            }
            string text = File.ReadAllText(path, new UTF8Encoding(false));
            int close = text.LastIndexOf('}');
            if (!text.StartsWith(rootName, StringComparison.Ordinal) || close < 0)
            {
                errors.Add(path + " is not a " + rootName + " file (or it is damaged).");
                return false;
            }
            file = new ConfigNodeFile { ending = text.Substring(close + 1), Entries = new List<Entry>() };
            file.lines = text.Substring(0, close + 1).Split('\n').ToList();
            for (int i = 0; i < file.lines.Count; i++)
            {
                string key;
                string value;
                if (TryParseValue(file.lines[i], out key, out value))
                    file.Entries.Add(new Entry { Key = key, Value = value, Line = i });
            }
            return true;
        }

        private static bool TryParseValue(string line, out string key, out string value)
        {
            key = value = string.Empty;
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed == "{" || trimmed == "}")
                return false;
            if (trimmed[0] != '{')
            {
                int equals = trimmed.IndexOf('=');
                if (equals <= 0)
                    return false;
                key = trimmed.Substring(0, equals).Replace(" ", string.Empty).Replace("\t", string.Empty);
                value = trimmed.Substring(equals + 1).Trim();
                return true;
            }
            int keyEnd = ClosingBrace(trimmed, 0);
            if (keyEnd < 0)
                return false;
            key = trimmed.Substring(1, keyEnd - 1);
            int valueStart = trimmed.IndexOf('{', keyEnd + 1);
            if (valueStart < 0)
            {
                int equals = trimmed.IndexOf('=', keyEnd + 1);
                value = equals < 0 ? string.Empty : trimmed.Substring(equals + 1).Trim();
                return true;
            }
            int valueEnd = ClosingBrace(trimmed, valueStart);
            if (valueEnd < 0)
                return false;
            value = trimmed.Substring(valueStart + 1, valueEnd - valueStart - 1);
            return true;
        }

        private static int ClosingBrace(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{')
                    depth++;
                else if (text[i] == '}' && --depth == 0)
                    return i;
            }
            return -1;
        }

        public void SetValue(string key, string value)
        {
            // Key and value are both braced as soon as one of them holds a special character (the game's writer).
            bool brace = NeedsBraces(key) || NeedsBraces(value);
            string line = "\t" + (brace ? "{" + key + "}" : key) + " = " + (brace ? "{" + value + "}" : value);
            Entry entry = Entries.FirstOrDefault(e => e.Key == key);
            if (entry != null)
            {
                entry.Value = value;
                lines[entry.Line] = line;
                return;
            }
            int closing = lines.FindLastIndex(l => l.Trim() == "}");
            lines.Insert(closing, line);
            Entries.Add(new Entry { Key = key, Value = value, Line = closing });
        }

        private static bool NeedsBraces(string text)
        {
            return text.IndexOfAny(new[] { '=', '{', '}', '<', '>', '"' }) >= 0;
        }

        public void Save(string path)
        {
            string text = string.Join("\n", lines) + ending;
            string temp = path + ".jptmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            SafeFileIo.CopyOverAndDelete(temp, path);
        }
    }

    // gameSettings.cfg (GameSettings.SaveGameSettings): root GAMESETTINGS with "\tKEY = value" lines and a PLAYAREA child
    // node; floats written "G" en-US, on/off settings as 1 / -1 (on = value > 0), LF line ends, "}\n\r\n" at the end.
    // Root values are set in place; a missing key goes after the last root value. Every other byte stays.
    internal static class VtolGameSettingsFile
    {
        // The root values of the model's parameters as Apply writes them and the game loads them: on/off as 1 (value > 0)
        // or 0, numbers clamped to the parameter's range. Null (with the reason) for a file that is not GAMESETTINGS.
        public static Dictionary<string, double> Read(string text, out string error)
        {
            error = string.Empty;
            if (!(text ?? string.Empty).StartsWith("GAMESETTINGS", StringComparison.Ordinal))
            {
                error = "gameSettings.cfg does not start with GAMESETTINGS (damaged?).";
                return null;
            }
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            FfbModel model = FfbSettings.ForGame(VtolVrGameSettings.Game);
            int depth = 0;
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed == "{" || trimmed == "}")
                {
                    depth += trimmed == "{" ? 1 : -1;
                    continue;
                }
                int eq = trimmed.IndexOf('=');
                if (depth != 1 || eq <= 0)
                    continue;
                string key = trimmed.Substring(0, eq).Trim();
                FfbParameter parameter = model == null ? null : model.Parameters.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));
                double number;
                if (parameter == null || !double.TryParse(trimmed.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    continue;
                values[parameter.Key] = parameter.Toggle ? (number > 0 ? 1 : 0) : Math.Max(parameter.Min, Math.Min(parameter.Max, number));
            }
            return values;
        }

        public static string Apply(string text, Dictionary<string, double> values, List<string> changes, out string error)
        {
            error = string.Empty;
            if (!(text ?? string.Empty).StartsWith("GAMESETTINGS", StringComparison.Ordinal))
            {
                error = "gameSettings.cfg does not start with GAMESETTINGS (damaged?).";
                return null;
            }
            List<string> lines = text.Split('\n').ToList();
            FfbModel model = FfbSettings.ForGame(VtolVrGameSettings.Game);
            foreach (KeyValuePair<string, double> value in values.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                FfbParameter parameter = model == null ? null : model.Parameters.FirstOrDefault(p => string.Equals(p.Key, value.Key, StringComparison.OrdinalIgnoreCase));
                if (parameter == null)
                    continue;
                double number = parameter.Toggle ? (value.Value != 0 ? 1 : -1) : Math.Max(parameter.Min, Math.Min(parameter.Max, value.Value));
                string rendered = ((float)Math.Round(number, 4)).ToString("G", CultureInfo.InvariantCulture);
                int depth = 0;
                int found = -1;
                int lastRootValue = -1;
                int rootOpen = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    string trimmed = lines[i].Trim();
                    if (trimmed == "{")
                    {
                        depth++;
                        if (depth == 1 && rootOpen < 0)
                            rootOpen = i;
                        continue;
                    }
                    if (trimmed == "}")
                    {
                        depth--;
                        continue;
                    }
                    if (depth != 1)
                        continue;
                    int eq = trimmed.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    lastRootValue = i;
                    if (string.Equals(trimmed.Substring(0, eq).Trim(), parameter.Key, StringComparison.Ordinal))
                        found = i;
                }
                if (rootOpen < 0)
                {
                    error = "gameSettings.cfg has no GAMESETTINGS { } block.";
                    return null;
                }
                if (found >= 0)
                {
                    string line = lines[found];
                    int eq = line.IndexOf('=');
                    double existing;
                    string current = line.Substring(eq + 1).Trim().TrimEnd('\r');
                    if (double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out existing) &&
                        (parameter.Toggle ? (existing > 0) == (number > 0) : Math.Abs(existing - number) < 1e-4))
                        continue;
                    string indent = line.Substring(0, line.Length - line.TrimStart().Length);
                    lines[found] = indent + parameter.Key + " = " + rendered;
                }
                else
                    lines.Insert((lastRootValue >= 0 ? lastRootValue : rootOpen) + 1, "\t" + parameter.Key + " = " + rendered);
                changes.Add(parameter.Key + " = " + rendered);
            }
            return string.Join("\n", lines);
        }
    }
}