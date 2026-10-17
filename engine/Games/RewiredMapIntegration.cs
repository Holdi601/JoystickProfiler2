using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Games
{
    // Where a Rewired game keeps its user data: key/value pairs with Rewired's own keys
    // ("RewiredSaveData|playerName=Player0|dataType=ControllerMap|kv=2|..."), each value a string (the joystick maps
    // are JoystickMap XML). Nuclear Option: Unity PlayerPrefs in the registry (value name = key + "_h<hash>");
    // Tiny Combat Arena: Rewired's UserDataStore_File (RewiredSaveData.json, value name = key).
    internal abstract class RewiredStore
    {
        // The stored value names, in their stored order.
        public abstract IEnumerable<string> Names { get; }
        // Rewired's key of a stored value name, and the value name a key is stored under.
        public abstract string KeyOf(string name);
        public abstract string NameOf(string key);
        public abstract bool Contains(string name);
        public abstract bool TryGetText(string name, out string text);
        // Before the first write; returns where the backup is ("" when none was made).
        public abstract string Backup(string game, List<string> warnings);
        // Writes the changed values (value name -> text); nothing else changes.
        public abstract bool Write(Dictionary<string, string> changes, out string error);
    }

    // The JoyPro side of a Unity game that saves Rewired joystick maps (Nuclear Option, Tiny Combat Arena): read and
    // write Player0's maps of unrecognized joysticks in map category 0, build the catalog from the game's Rewired
    // InputManager (numeric action ids in the 5th column of actions.tsv). Only the element maps of the actions JoyPro
    // writes change (clean / nuke: only the categories the game marks as JoyPro's); every other value stays.
    // Docs: docs/games/nuclear-option.md, docs/games/tiny-combat-arena.md.
    internal abstract class RewiredMapIntegration : IGameIntegration
    {
        // The values JoyPro may change or create: the player's controller maps and their known action ids.
        public static string OwnedPrefix(string player)
        {
            return "RewiredSaveData|playerName=" + player + "|dataType=ControllerMap";
        }

        private string MapKey(int mapCategory, string suffix, bool knownIds)
        {
            return "RewiredSaveData|playerName=" + PlayerName + "|dataType=ControllerMap" + (knownIds ? "_KnownActionIds" : string.Empty) +
                   "|kv=2|controllerMapType=JoystickMap|categoryId=" + mapCategory.ToString(CultureInfo.InvariantCulture) + "|layoutId=0|" + suffix;
        }

        protected const string ZeroGuid = "00000000-0000-0000-0000-000000000000";
        protected const string PositiveSuffix = ".Positive";
        protected const string NegativeSuffix = ".Negative";
        // Rewired's element ids of an unrecognized joystick (its "Unknown Controller" definition): Axis 0..7 =
        // DirectInput X, Y, Z, Rx, Ry, Rz, Slider 1, 2; Button n = 31 + n; hat h (0-based) Up/Right/Down/Left =
        // 160 + 4h + 0..3 (hats are written only by games that confirmed them, HatsVerified).
        private const int AxisElements = 8;
        private const int FirstSliderElement = 6;
        private const int FirstButtonElement = 32;
        private const int ButtonElements = 128;
        private const int FirstHatElement = 160;
        private const int HatElements = 16;

        public abstract string GameId { get; }
        public abstract string DisplayName { get; }
        public abstract IReadOnlyList<string> Aliases { get; }
        public abstract string DbFolder { get; }
        public abstract bool Experimental { get; }
        public abstract bool ExclusiveFlightSim { get; }
        public abstract string Notes { get; }
        public abstract IReadOnlyList<string> ProcessNames { get; }
        public bool SupportsCatalogRefresh { get { return true; } }
        public GameFeature KeyboardBinds { get { return GameFeature.No; } }
        public GameFeature ModifierBinds { get { return GameFeature.No; } }

        public abstract List<string> DiscoverConfigPaths(List<string> warnings);
        protected abstract bool TryOpenStore(string configPath, List<string> errors, out RewiredStore store);
        // The game's action categories by Rewired id (used when refresh-db finds no category list).
        protected abstract IReadOnlyDictionary<int, string> DefaultCategories { get; }
        // Categories whose actions clean releases on the written devices and nuke on the others.
        protected abstract bool IsCleanCategory(string category);
        // The InputManager's serialized layout and the file that holds it (null when not found).
        protected abstract RewiredLayout CatalogLayout { get; }
        protected abstract string FindCatalogAssets(string sourcePath);
        // Hat elements were seen in a map the game saved: JoyPro reads and writes the four main directions.
        protected virtual bool HatsVerified { get { return false; } }
        // The Rewired player whose maps the game saves (Nuclear Option "Player0", Tiny Combat Arena "Player").
        protected virtual string PlayerName { get { return "Player0"; } }
        // Rewired map category ("tab" of the Control Mapper) an action of this action category is saved in; -1 when
        // the action has no joystick map. Nuclear Option keeps every joystick action in map category 0.
        protected virtual int MapCategoryOf(string actionCategory)
        {
            return 0;
        }
        // axisRange the game writes for a button element (Nuclear Option's default maps 0, Tiny Combat Arena's mapper 1;
        // Rewired ignores it for buttons, JoyPro compares buttons without it).
        protected virtual int ButtonAxisRange { get { return 0; } }
        // Game-specific notes before the write (Nuclear Option: the throttle range setting).
        protected virtual void BeforeWrite(RewiredStore store, GameExportRequest request, GameIoResult result)
        {
        }

        private string RefreshHint
        {
            get { return "engine " + Aliases[0] + " refresh-db"; }
        }

        // ---- Read ----------------------------------------------------------------------------------------------------

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            ActionTable actions;
            if (!ActionTable.TryBuild(catalog, DisplayName, RefreshHint, result.Errors, out actions))
                return result;
            RewiredStore store;
            if (!TryOpenStore(configPath, result.Errors, out store))
                return result;

            foreach (MapValue value in MapValues(store))
            {
                List<RewiredElementMap> maps;
                if (!RewiredJoystickMap.TryRead(value.Xml, out maps))
                {
                    result.Warnings.Add("Unreadable joystick map of '" + value.Identifier + "'; skipped.");
                    continue;
                }
                int known = maps.Count(m => actions.ById.ContainsKey(m.ActionId));
                if (known == 0)
                    continue;
                string reason;
                GameDevice device = FindDevice(devices, value.Identifier, value.Duplicate, out reason);
                if (device == null)
                {
                    result.Warnings.Add(known.ToString(CultureInfo.InvariantCulture) + " bind(s) of '" + value.Identifier + "' not read: " + reason);
                    continue;
                }
                foreach (RewiredElementMap map in maps)
                {
                    GameInputBinding binding = ToBinding(map, actions, device, result.Warnings);
                    if (binding != null)
                        result.Bindings.Add(binding);
                }
            }
            return result;
        }

        private GameInputBinding ToBinding(RewiredElementMap map, ActionTable actions, GameDevice device, List<string> warnings)
        {
            GameAction action;
            if (!actions.ById.TryGetValue(map.ActionId, out action))
                return null; // mod actions and actions of a newer game version: not JoyPro's
            string where = action.Id + " on " + device.JoyProName;
            if (map.ElementType == 0)
            {
                if (map.ElementId < 0 || map.ElementId >= AxisElements)
                {
                    warnings.Add(where + ": axis element " + map.ElementId.ToString(CultureInfo.InvariantCulture) + " has no JoyPro axis; skipped.");
                    return null;
                }
                if (!action.IsAxis || map.AxisRange != 0)
                {
                    warnings.Add(where + ": half-axis binds (" + GameInputTokens.AxisToken(map.ElementId) + ") are not read; skipped.");
                    return null;
                }
                return new GameInputBinding
                {
                    ActionId = action.Id,
                    Device = device.JoyProName,
                    Input = GameInputTokens.AxisToken(map.ElementId),
                    IsAxis = true,
                    Inverted = map.Invert
                };
            }
            if (map.ElementType != 1)
            {
                warnings.Add(where + ": element type " + map.ElementType.ToString(CultureInfo.InvariantCulture) + " is not read; skipped.");
                return null;
            }
            string input;
            if (map.ElementId >= FirstHatElement)
            {
                int hat = (map.ElementId - FirstHatElement) / 4;
                if (!HatsVerified || hat >= HatElements)
                {
                    warnings.Add(where + ": hat element " + map.ElementId.ToString(CultureInfo.InvariantCulture) + " is not read (" +
                                 (HatsVerified ? "only the four main directions are" : "the hat element ids are unverified") + "); skipped.");
                    return null;
                }
                input = "JOY_BTN_POV" + (hat + 1).ToString(CultureInfo.InvariantCulture) + "_" + HatDirections[(map.ElementId - FirstHatElement) % 4];
            }
            else if (map.ElementId < FirstButtonElement || map.ElementId >= FirstButtonElement + ButtonElements)
            {
                warnings.Add(where + ": element " + map.ElementId.ToString(CultureInfo.InvariantCulture) + " is no button JoyPro knows; skipped.");
                return null;
            }
            else
            {
                input = GameInputTokens.ButtonToken(map.ElementId - FirstButtonElement);
            }
            string id = action.Id;
            if (action.IsAxis)
            {
                id += map.AxisContribution == 1 ? NegativeSuffix : PositiveSuffix;
                if (!actions.ByName.ContainsKey(id))
                {
                    warnings.Add(where + ": the catalog has no " + id + "; skipped.");
                    return null;
                }
            }
            return new GameInputBinding
            {
                ActionId = id,
                Device = device.JoyProName,
                Input = input,
                IsAxis = false
            };
        }

        // Rewired's hat elements in id order: Up, Right, Down, Left.
        private static readonly string[] HatDirections = { "U", "R", "D", "L" };

        // ---- Write ---------------------------------------------------------------------------------------------------

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            ActionTable actions;
            if (!ActionTable.TryBuild(request.Catalog, DisplayName, RefreshHint, result.Errors, out actions))
                return result;
            RewiredStore store;
            if (!TryOpenStore(request.ConfigPath, result.Errors, out store))
                return result;

            // Wanted element maps per device identifier and map category.
            Dictionary<Tuple<string, int>, List<RewiredElementMap>> wanted = new Dictionary<Tuple<string, int>, List<RewiredElementMap>>();
            Dictionary<string, string> hardwareNames = new Dictionary<string, string>(StringComparer.Ordinal);
            bool sliderWarned = false;
            foreach (GameInputBinding binding in request.Bindings)
            {
                RewiredElementMap map;
                string reason = Encode(binding, actions, out map);
                if (reason.Length > 0)
                {
                    Skip(result, binding, reason);
                    continue;
                }
                int mapCategory = MapCategoryOf(actions.ById[map.ActionId].Category);
                if (mapCategory < 0)
                {
                    Skip(result, binding, "the action has no joystick map in " + DisplayName + ".");
                    continue;
                }
                GameDevice device = ResolveDevice(binding.Device, request.Devices);
                if (device == null || !device.HasVidPid || string.IsNullOrWhiteSpace(device.HidProductString))
                {
                    Skip(result, binding, "device '" + binding.Device + "' is not connected (" + DisplayName + " keys its maps by the USB product, JoyPro needs it connected).");
                    continue;
                }
                string identifier = HardwareIdentifier(device);
                if (request.Devices.Devices.Count(d => HardwareIdentifier(d) == identifier) > 1)
                {
                    Skip(result, binding, "two connected devices are the same model ('" + identifier + "'); Rewired tells them apart only by its own order.");
                    continue;
                }
                if (map.ElementType == 0 && map.ElementId >= FirstSliderElement && !sliderWarned)
                {
                    sliderWarned = true;
                    result.Warnings.Add("Slider binds use Rewired's elements 6 and 7 (its Unknown Controller layout); check them once in the game's bindings menu.");
                }
                List<RewiredElementMap> list;
                Tuple<string, int> slot = Tuple.Create(identifier, mapCategory);
                if (!wanted.TryGetValue(slot, out list))
                    wanted[slot] = list = new List<RewiredElementMap>();
                if (!list.Any(m => Same(m, map)))
                    list.Add(map);
                hardwareNames[identifier] = device.HidProductString.Replace("\0", string.Empty);
                result.BindingsWritten++;
            }

            HashSet<int> owned = new HashSet<int>(actions.ById.Where(a => IsCleanCategory(a.Value.Category)).Select(a => a.Key));
            Dictionary<string, string> changes = new Dictionary<string, string>(StringComparer.Ordinal);
            int removed = 0;
            int added = 0;
            List<MapValue> values = MapValues(store);
            foreach (MapValue value in values)
            {
                Tuple<string, int> slot = Tuple.Create(value.Identifier, value.Category);
                bool joyPro = value.Duplicate == "0" && wanted.ContainsKey(slot);
                // Clean: also the maps of the written devices JoyPro has no binds for in this map category.
                bool written = value.Duplicate == "0" && hardwareNames.ContainsKey(value.Identifier);
                List<RewiredElementMap> mine = joyPro ? wanted[slot] : new List<RewiredElementMap>();
                if (!joyPro && !request.NukeUnusedDevices && !(written && request.Mode == GameExportMode.Clean))
                    continue;

                List<RewiredElementMap> existing;
                if (!RewiredJoystickMap.TryRead(value.Xml, out existing))
                {
                    result.Warnings.Add("Unreadable joystick map of '" + value.Identifier + "' left as it is.");
                    continue;
                }
                List<RewiredElementMap> add = new List<RewiredElementMap>();
                HashSet<RewiredElementMap> drop = new HashSet<RewiredElementMap>();
                if (!joyPro && !written)
                {
                    // Nuke: JoyPro's actions leave the devices the export does not use.
                    foreach (RewiredElementMap map in existing.Where(m => owned.Contains(m.ActionId)))
                        drop.Add(map);
                }
                else
                {
                    PlanDevice(request.Mode, existing, mine, owned, actions, value.Identifier, drop, add, result);
                }
                string updated;
                if (!RewiredJoystickMap.TryEdit(value.Xml, m => drop.Any(d => Same(d, m)), add, out updated))
                {
                    result.Warnings.Add("Unreadable joystick map of '" + value.Identifier + "' left as it is.");
                    continue;
                }
                if (joyPro)
                    wanted.Remove(slot);
                if (updated == value.Xml)
                    continue;
                removed += existing.Count(m => drop.Any(d => Same(d, m)));
                added += add.Count;
                changes[value.Name] = updated;
            }

            // Devices without a saved map (the game uses its default map for them): a new map with only JoyPro's binds,
            // plus the list of action ids the map knows (else Rewired adds the default binds of "new" actions).
            List<string> knownIds = actions.ById.Keys.OrderBy(k => k).Select(k => k.ToString(CultureInfo.InvariantCulture)).ToList();
            foreach (KeyValuePair<Tuple<string, int>, List<RewiredElementMap>> map in wanted)
            {
                AddBlankMap(store, changes, map.Key.Item1, map.Key.Item2, hardwareNames[map.Key.Item1], map.Value, knownIds);
                added += map.Value.Count;
                result.Steps.Add("New joystick map for '" + hardwareNames[map.Key.Item1].Trim() + "'" + MapCategoryNote(map.Key.Item2) + " (the game's default binds of this device are replaced).");
            }
            // Nuke: connected devices the game has no saved map for would keep the game's default binds (axes and buttons
            // of every device); an empty map (per map category of JoyPro's actions) switches them off.
            if (request.NukeUnusedDevices && request.Devices != null)
            {
                HashSet<int> categories = new HashSet<int>(actions.ById.Values.Where(a => IsCleanCategory(a.Category)).Select(a => MapCategoryOf(a.Category)).Where(c => c >= 0));
                foreach (GameDevice device in request.Devices.Devices.Where(d => d.HasVidPid && !string.IsNullOrWhiteSpace(d.HidProductString)))
                {
                    string identifier = HardwareIdentifier(device);
                    if (hardwareNames.ContainsKey(identifier) || request.Devices.Devices.Count(d => HardwareIdentifier(d) == identifier) > 1)
                        continue;
                    foreach (int category in categories.OrderBy(c => c))
                    {
                        if (values.Any(v => v.Identifier == identifier && v.Category == category))
                            continue;
                        AddBlankMap(store, changes, identifier, category, device.HidProductString.Replace("\0", string.Empty), new List<RewiredElementMap>(), knownIds);
                        result.Steps.Add("Empty joystick map for unused '" + device.HidProductString.Trim() + "'" + MapCategoryNote(category) + " (switches off the game's default binds).");
                    }
                }
            }

            BeforeWrite(store, request, result);
            string summary = changes.Count.ToString(CultureInfo.InvariantCulture) + " stored value(s); " + removed.ToString(CultureInfo.InvariantCulture) +
                             " bind(s) removed, " + added.ToString(CultureInfo.InvariantCulture) + " added";
            if (changes.Count == 0)
            {
                result.Steps.Add("The " + DisplayName + " bindings already match.");
                return result;
            }
            if (request.DryRun)
            {
                result.Steps.Add("Dry run: would change " + summary + ".");
                return result;
            }
            string backup = store.Backup(GameId, result.Warnings);
            if (backup.Length > 0)
                result.Steps.Add("Backup of the bindings before the export: " + backup);
            string error;
            if (!store.Write(changes, out error))
            {
                result.Errors.Add(error);
                return result;
            }
            result.FilesWritten.Add(request.ConfigPath);
            result.Steps.Add(summary + ".");
            return result;
        }

        // One device JoyPro writes. A conflict is the same action (and pole) on the same device: merge-overwrite replaces
        // it, merge keeps it, add keeps both. Clean releases every other owned action of the device. In clean and
        // merge-overwrite an element JoyPro uses leaves the other owned actions it drove.
        private static void PlanDevice(GameExportMode mode, List<RewiredElementMap> existing, List<RewiredElementMap> mine, HashSet<int> owned, ActionTable actions,
                                       string identifier, HashSet<RewiredElementMap> drop, List<RewiredElementMap> add, GameIoResult result)
        {
            bool overwrite = mode == GameExportMode.Clean || mode == GameExportMode.MergeOverwrite;
            foreach (RewiredElementMap map in existing)
            {
                if (mine.Any(m => Same(m, map)))
                    continue;
                if (mode == GameExportMode.Clean && owned.Contains(map.ActionId))
                    drop.Add(map);
                else if (overwrite && mine.Any(m => SameSlot(m, map)))
                    drop.Add(map);
                else if (overwrite && owned.Contains(map.ActionId) && mine.Any(m => SameElement(m, map)))
                    drop.Add(map);
            }
            foreach (RewiredElementMap map in mine)
            {
                if (existing.Any(m => Same(m, map)))
                    continue;
                if (mode == GameExportMode.Merge && existing.Any(m => SameSlot(m, map)))
                {
                    result.ConflictsKept++;
                    result.Warnings.Add(ActionName(actions, map) + " on '" + identifier + "': kept the game's bind (merge mode).");
                    continue;
                }
                RewiredElementMap other = existing.FirstOrDefault(m => !drop.Contains(m) && SameElement(m, map) && m.ActionId != map.ActionId);
                if (other != null)
                    result.Warnings.Add(ActionName(actions, map) + " on '" + identifier + "': the element also drives " + ActionName(actions, other) + " (kept: " +
                                        (owned.Contains(other.ActionId) ? GameCommands.ModeToString(mode) + " mode" : "not an action JoyPro manages") + ").");
                add.Add(map);
            }
        }

        // Same bind; for button elements without axisRange (games write 0 or 1 there, Rewired ignores it).
        private static bool Same(RewiredElementMap a, RewiredElementMap b)
        {
            if (a == null || b == null)
                return false;
            if (a.ElementType == 1 && b.ElementType == 1)
                return a.ActionId == b.ActionId && a.ElementId == b.ElementId && a.AxisContribution == b.AxisContribution;
            return a.SameBind(b);
        }

        // " (map category n)" for games that keep their joystick maps in several map categories.
        private static string MapCategoryNote(int mapCategory)
        {
            return mapCategory == 0 ? string.Empty : " (map category " + mapCategory.ToString(CultureInfo.InvariantCulture) + ")";
        }

        private static bool SameSlot(RewiredElementMap a, RewiredElementMap b)
        {
            if (a.ActionId != b.ActionId || a.ElementType != b.ElementType)
                return false;
            return a.ElementType != 1 || a.AxisContribution == b.AxisContribution;
        }

        private static bool SameElement(RewiredElementMap a, RewiredElementMap b)
        {
            return a.ElementType == b.ElementType && a.ElementId == b.ElementId;
        }

        private static string ActionName(ActionTable actions, RewiredElementMap map)
        {
            GameAction action;
            string name = actions.ById.TryGetValue(map.ActionId, out action) ? action.Id : "action " + map.ActionId.ToString(CultureInfo.InvariantCulture);
            if (map.ElementType == 1 && action != null && action.IsAxis)
                name += map.AxisContribution == 1 ? NegativeSuffix : PositiveSuffix;
            return name;
        }

        private void AddBlankMap(RewiredStore store, Dictionary<string, string> changes, string identifier, int mapCategory, string hardwareName, List<RewiredElementMap> maps, List<string> knownIds)
        {
            string suffix = "hardwareGuid=" + ZeroGuid + "|hardwareIdentifier=" + identifier + "|duplicate=0";
            changes[store.NameOf(MapKey(mapCategory, suffix, false))] = RewiredJoystickMap.Blank(hardwareName, ZeroGuid, maps, mapCategory);
            string knownName = store.NameOf(MapKey(mapCategory, suffix, true));
            if (!store.Contains(knownName))
                changes[knownName] = string.Join(",", knownIds);
        }

        // Turns a JoyPro bind into a Rewired element map; returns the reason when it cannot.
        private string Encode(GameInputBinding binding, ActionTable actions, out RewiredElementMap map)
        {
            map = null;
            if (binding.IsKeyboard)
                return "keyboard binds are not written to " + DisplayName + " yet (the keyboard element ids are unverified).";
            if (binding.Reformers != null && binding.Reformers.Count > 0)
                return "Rewired joystick maps have no modifiers; the bind needs a button of its own.";
            int actionId;
            GameAction action;
            int pole = -1;
            string name = binding.ActionId ?? string.Empty;
            if (name.EndsWith(PositiveSuffix, StringComparison.OrdinalIgnoreCase) || name.EndsWith(NegativeSuffix, StringComparison.OrdinalIgnoreCase))
            {
                pole = name.EndsWith(NegativeSuffix, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                name = name.Substring(0, name.LastIndexOf('.'));
            }
            if (!actions.ByName.TryGetValue(name, out action) || !actions.IdOf.TryGetValue(action, out actionId))
                return "'" + binding.ActionId + "' has no Rewired action id in the catalog (run " + RefreshHint + ").";
            if (pole >= 0 && !action.IsAxis)
                return "'" + name + "' is a button action and has no poles.";

            JoyInput input;
            if (!GameInputTokens.TryParse(binding.Input, out input))
                return "input '" + binding.Input + "' is not a joystick input.";
            switch (input.Kind)
            {
                case JoyInputKind.Axis:
                    if (!action.IsAxis || pole >= 0)
                        return "an axis needs an axis action (half axes are not written).";
                    map = new RewiredElementMap { ActionId = actionId, ElementType = 0, ElementId = input.Index, AxisRange = 0, Invert = binding.Inverted };
                    return string.Empty;
                case JoyInputKind.Button:
                case JoyInputKind.Pov:
                    if (action.IsAxis && pole < 0)
                        return "a button drives one pole of the axis action: bind it to " + name + PositiveSuffix + " or " + name + NegativeSuffix + ".";
                    int element;
                    string why = ButtonElement(binding.Input, input, out element);
                    if (why.Length > 0)
                        return why;
                    map = new RewiredElementMap { ActionId = actionId, ElementType = 1, ElementId = element, AxisRange = ButtonAxisRange, AxisContribution = Math.Max(0, pole) };
                    return string.Empty;
                default:
                    return "input '" + binding.Input + "' is not supported.";
            }
        }

        // Rewired element of a button or hat direction; returns the reason when there is none.
        private string ButtonElement(string token, JoyInput input, out int element)
        {
            element = -1;
            if (input.Kind == JoyInputKind.Button)
            {
                if (input.Index >= ButtonElements)
                    return "Rewired knows 128 buttons per device.";
                element = FirstButtonElement + input.Index;
                return string.Empty;
            }
            if (!HatsVerified)
                return "hats are not written yet (Rewired's hat element ids are unverified in this game).";
            Match match = Regex.Match(token ?? string.Empty, @"^JOY_BTN_POV(\d+)_(U|R|D|L)$", RegexOptions.IgnoreCase);
            int hat;
            if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out hat) || hat < 1 || hat > HatElements)
                return "only the four main hat directions (U, R, D, L) of hats 1-16 are written (Rewired keeps diagonals as elements of their own).";
            element = FirstHatElement + 4 * (hat - 1) + Array.IndexOf(HatDirections, match.Groups[2].Value.ToUpperInvariant());
            return string.Empty;
        }

        private static void Skip(GameIoResult result, GameInputBinding binding, string reason)
        {
            result.BindingsSkipped++;
            result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
        }

        // ---- Devices -------------------------------------------------------------------------------------------------

        // Rewired's Raw Input id of an unrecognized joystick model: "WindowsRawInput" + HID product string without any
        // whitespace + PID + VID (4 hex digits each, upper case).
        internal static string HardwareIdentifier(GameDevice device)
        {
            if (device == null || !device.HasVidPid || string.IsNullOrWhiteSpace(device.HidProductString))
                return string.Empty;
            string product = Regex.Replace(device.HidProductString.Replace("\0", string.Empty), @"\s", string.Empty);
            return "WindowsRawInput" + product + device.ProductId.ToString("X4", CultureInfo.InvariantCulture) + device.VendorId.ToString("X4", CultureInfo.InvariantCulture);
        }

        private static GameDevice ResolveDevice(string joyProDevice, GameDeviceDirectory devices)
        {
            if (devices == null)
                return null;
            string name;
            Guid guid;
            GameDevice device = GameDeviceDirectory.TryParseJoyProName(joyProDevice, out name, out guid) ? devices.FindByInstanceGuid(guid) : null;
            return device ?? devices.FindByJoyProName(joyProDevice);
        }

        private static GameDevice FindDevice(GameDeviceDirectory devices, string identifier, string duplicate, out string reason)
        {
            reason = string.Empty;
            List<GameDevice> matches = devices == null ? new List<GameDevice>() : devices.Devices.Where(d => HardwareIdentifier(d) == identifier).ToList();
            if (matches.Count == 0)
            {
                reason = "the device is not connected.";
                return null;
            }
            if (matches.Count > 1 || duplicate != "0")
            {
                reason = "several devices of this model; Rewired tells them apart only by its own order.";
                return null;
            }
            return matches[0];
        }

        // ---- Stored values -------------------------------------------------------------------------------------------

        private sealed class MapValue
        {
            public string Name = string.Empty;
            public string Identifier = string.Empty;
            public string Duplicate = string.Empty;
            public int Category;
            public string Xml = string.Empty;
        }

        // The player's saved joystick maps of unrecognized joysticks (layout 0, key version 2), every map category.
        private List<MapValue> MapValues(RewiredStore store)
        {
            List<MapValue> values = new List<MapValue>();
            foreach (string name in store.Names)
            {
                Dictionary<string, string> parts = KeyParts(store.KeyOf(name));
                string text;
                int category;
                if (Part(parts, "playerName") != PlayerName || Part(parts, "dataType") != "ControllerMap" || Part(parts, "kv") != "2" ||
                    Part(parts, "controllerMapType") != "JoystickMap" || Part(parts, "layoutId") != "0" || Part(parts, "hardwareGuid") != ZeroGuid ||
                    !int.TryParse(Part(parts, "categoryId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out category) ||
                    Part(parts, "hardwareIdentifier").Length == 0 || !store.TryGetText(name, out text))
                    continue;
                values.Add(new MapValue { Name = name, Identifier = Part(parts, "hardwareIdentifier"), Duplicate = Part(parts, "duplicate"), Category = category, Xml = text });
            }
            return values;
        }

        private static Dictionary<string, string> KeyParts(string key)
        {
            Dictionary<string, string> parts = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] split = key.Split('|');
            if (split.Length == 0 || split[0] != "RewiredSaveData")
                return parts;
            foreach (string part in split.Skip(1))
            {
                int equals = part.IndexOf('=');
                if (equals > 0)
                    parts[part.Substring(0, equals)] = part.Substring(equals + 1);
            }
            return parts;
        }

        private static string Part(Dictionary<string, string> parts, string name)
        {
            string value;
            return parts.TryGetValue(name, out value) ? value : string.Empty;
        }

        // ---- Catalog -------------------------------------------------------------------------------------------------

        private sealed class ActionTable
        {
            // Rewired id -> the action row (not the pole rows).
            public readonly Dictionary<int, GameAction> ById = new Dictionary<int, GameAction>();
            public readonly Dictionary<GameAction, int> IdOf = new Dictionary<GameAction, int>();
            // Every catalog row by name (pole rows included).
            public readonly Dictionary<string, GameAction> ByName = new Dictionary<string, GameAction>(StringComparer.OrdinalIgnoreCase);

            public static bool TryBuild(GameCatalog catalog, string game, string refreshHint, List<string> errors, out ActionTable table)
            {
                table = new ActionTable();
                foreach (GameAction action in catalog == null ? new List<GameAction>() : catalog.Actions)
                {
                    table.ByName[action.Id] = action;
                    int id;
                    bool pole = action.Id.EndsWith(PositiveSuffix, StringComparison.OrdinalIgnoreCase) || action.Id.EndsWith(NegativeSuffix, StringComparison.OrdinalIgnoreCase);
                    if (pole || !int.TryParse(action.NativeId, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || table.ById.ContainsKey(id))
                        continue;
                    table.ById[id] = action;
                    table.IdOf[action] = id;
                }
                if (table.ById.Count == 0)
                {
                    errors.Add("The " + game + " catalog has no Rewired action ids. Run '" + refreshHint + "' (reads them from the installed game).");
                    return false;
                }
                return true;
            }
        }

        // Reads the action list of the game's Rewired InputManager. Actions the player cannot assign (the layout's
        // userAssignable flag, where it has one) are left out: the game's bindings menu does not show them.
        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                GameInstall install = GameInstalls.Find(GameId, result.Warnings);
                sourcePath = install != null && install.Installed ? install.InstallPath : null;
            }
            string assets = FindCatalogAssets(sourcePath);
            if (assets == null)
            {
                result.Errors.Add(DisplayName + "'s Rewired InputManager was not found. Pass --path with the game folder, its data folder or the .assets file.");
                return null;
            }
            byte[] data = File.ReadAllBytes(assets);
            List<RewiredActionRecord> records;
            Dictionary<int, string> categories;
            string error;
            if (!RewiredInputManagerData.TryParse(data, CatalogLayout, out records, out categories, out error))
            {
                result.Errors.Add(assets + ": " + error);
                return null;
            }
            if (categories.Count == 0)
            {
                categories = new Dictionary<int, string>(DefaultCategories.ToDictionary(c => c.Key, c => c.Value));
                result.Warnings.Add("The action categories were not found next to the actions; the known category names were used.");
            }

            GameCatalog catalog = new GameCatalog { GameId = GameId, SourcePath = assets };
            int hidden = 0;
            foreach (RewiredActionRecord record in records)
            {
                if (!record.UserAssignable)
                {
                    hidden++;
                    continue;
                }
                string category;
                if (!categories.TryGetValue(record.CategoryId, out category) && !DefaultCategories.TryGetValue(record.CategoryId, out category))
                    category = "Category " + record.CategoryId.ToString(CultureInfo.InvariantCulture);
                string id = record.Id.ToString(CultureInfo.InvariantCulture);
                bool axis = record.Type == 0;
                string title = Clean(record.DescriptiveName).Length > 0 ? Clean(record.DescriptiveName) : record.Name;
                catalog.Add(new GameAction { Id = record.Name, Title = title, IsAxis = axis, Category = category, NativeId = id });
                if (!axis)
                    continue;
                string positive = Clean(record.PositiveName);
                string negative = Clean(record.NegativeName);
                catalog.Add(new GameAction { Id = record.Name + PositiveSuffix, Title = positive.Length > 0 ? positive : title + " +", IsAxis = false, Category = category, NativeId = id });
                catalog.Add(new GameAction { Id = record.Name + NegativeSuffix, Title = negative.Length > 0 ? negative : title + " -", IsAxis = false, Category = category, NativeId = id });
            }
            result.Steps.Add("Read " + records.Count.ToString(CultureInfo.InvariantCulture) + " Rewired action(s) from " + assets +
                             (hidden > 0 ? " (" + hidden.ToString(CultureInfo.InvariantCulture) + " not assignable by the player, left out)" : string.Empty) + ".");
            foreach (GameAction gone in current.Actions.Where(a => catalog.Find(a.Id) == null))
                result.Warnings.Add("'" + gone.Id + "' is no longer in the game's action list.");
            return catalog;
        }

        private static string Clean(string text)
        {
            return Regex.Replace(text ?? string.Empty, @"[\t\r\n]+", " ").Trim();
        }
    }
}
