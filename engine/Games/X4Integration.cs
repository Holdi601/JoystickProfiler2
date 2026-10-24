using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // X4: Foundations keeps every binding in <Documents>\Egosoft\X4\<SteamID32>\inputmap.xml as <action>, <state> and
    // <range> lines. A joystick bind names a slot (INPUT_SOURCE_JOYAXES / _2 ... _8), not a device; config.xml pins
    // devices to slots by DirectInput instance GUID once the user assigned them in the game. JoyPro only writes the
    // space-flight controls and only to pinned slots; the file is edited as text so every other line stays as it was
    // (see docs/games/x4.md).
    internal sealed class X4Integration : IGameIntegration, IGameSettingsExport, IGameSettingsReader, IGameSettingsSource, IGameCatalogRefreshFiles, IGameCatalogTidy
    {
        // The game's menu names of the steering controls say neither yaw nor roll: X4's primary steering axis is yaw
        // (left-right) and its secondary axis roll unless adaptive steering is on (the game's own tooltips, text page
        // 1026 ids 2629/2630). The game's name stays in brackets so it can still be found in the game's menu.
        private static readonly Dictionary<string, string> ReadableTitles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "INPUT_RANGE_STEERING_PRIMARY", "Yaw (Primary Steering Axis)" },
            { "INPUT_RANGE_STEERING_SECONDARY", "Roll (Secondary Steering Axis)" },
            { "INPUT_RANGE_STEERING_PITCH", "Pitch (Steer Up-Down)" },
            { "INPUT_RANGE_STRAFE_LEFT_RIGHT", "Strafe Left-Right (lateral thrust)" },
            { "INPUT_RANGE_STRAFE_UP_DOWN", "Strafe Up-Down (vertical thrust)" },
            { "INPUT_STATE_STEER_UP", "Pitch Up (Steer Up)" },
            { "INPUT_STATE_STEER_DOWN", "Pitch Down (Steer Down)" },
            { "INPUT_STATE_STEER_LEFT", "Yaw Left (Steer Left)" },
            { "INPUT_STATE_STEER_RIGHT", "Yaw Right (Steer Right)" },
        };

        // "space/Steering: Analog; function 16; also in ..." (lists built by older versions) -> "Steering: Analog": the menu
        // group only; the builder's notes were split into categories of their own by the catalog ("function 16").
        public void TidyCatalog(GameCatalog catalog)
        {
            foreach (GameAction action in catalog.Actions)
            {
                action.Category = TidyCategory(action.Category);
                string title;
                if (ReadableTitles.TryGetValue(action.Id, out title))
                    action.Title = title;
            }
        }

        internal static string TidyCategory(string category)
        {
            string group = (category ?? string.Empty).Split(';')[0].Trim();
            return group.StartsWith("space/", StringComparison.OrdinalIgnoreCase) ? group.Substring("space/".Length).Trim() : group;
        }

        private const int MaxSlots = 8;
        private const string ThrottleRange = "INPUT_RANGE_THROTTLE";
        private const int MaxButtons = 64;
        private static readonly string[] NamedButtons = { "A", "B", "X", "Y", "LEFT_SHOULDER", "RIGHT_SHOULDER", "BACK", "START", "LEFT_THUMB", "RIGHT_THUMB", "BIGBUTTON" };
        private static readonly string[] HatDirections = { "UP", "UP_RIGHT", "RIGHT", "DOWN_RIGHT", "DOWN", "DOWN_LEFT", "LEFT", "UP_LEFT" };
        private static readonly string[] AxisNames = { "X", "Y", "Z", "RX", "RY", "RZ", "SLIDER1", "SLIDER2" };
        private static readonly Regex EntryLine = new Regex(@"^(\s*)<(action|state|range)\s+([^>]*?)\s*/>\s*$", RegexOptions.Compiled);
        private static readonly Regex Attribute = new Regex(@"([A-Za-z_]+)=""([^""]*)""", RegexOptions.Compiled);

        public string GameId { get { return "X4"; } }
        public string DisplayName { get { return "X4: Foundations (experimental)"; } }
        public IReadOnlyList<string> Aliases { get { return new[] { "x4", "x4foundations" }; } }
        public string DbFolder { get { return "X4"; } }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return false; } }
        public string Notes
        {
            get
            {
                return "Close X4 before exporting. Assign every joystick once in X4 (Options > Controls > Manage Controllers/Joysticks): " +
                       "only devices X4 pinned to a slot can be written; X4 supports 8 devices, 64 buttons and one hat per device.";
            }
        }
        public IReadOnlyList<string> ProcessNames { get { return new[] { "X4" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        // Modifiers: Shift/Ctrl on keyboard binds only.
        public GameFeature KeyboardBinds { get { return GameFeature.Yes; } }
        public GameFeature ModifierBinds { get { return GameFeature.Partial; } }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            List<string> paths = new List<string>();
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Egosoft", "X4");
            if (!Directory.Exists(root))
                return paths;
            List<string> candidates = Directory.GetDirectories(root)
                .Select(dir => Path.Combine(dir, "inputmap.xml"))
                .Concat(new[] { Path.Combine(root, "inputmap.xml") })
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
            paths.AddRange(candidates);
            return paths;
        }

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            X4Map map;
            if (!X4Map.TryLoad(configPath, result.Errors, out map))
                return result;
            X4Slots slots = X4Slots.Load(configPath, result.Warnings);
            Dictionary<string, int> keyCodes = X4Data.KeyCodes();
            HashSet<int> reportedSlots = new HashSet<int>();

            foreach (X4Entry entry in map.Entries)
            {
                if (catalog.Actions.Count > 0 && !catalog.Contains(entry.Id))
                    continue;

                if (entry.Source == "INPUT_SOURCE_KEYBOARD")
                {
                    GameInputBinding key = ReadKeyboard(entry, keyCodes);
                    if (key != null)
                        result.Bindings.Add(key);
                    continue;
                }

                int slot;
                bool axisSource;
                if (!TryParseJoySource(entry.Source, out slot, out axisSource))
                    continue;
                if (entry.Sgn.Length > 0)
                {
                    result.Warnings.Add(entry.Id + ": half-axis bind (" + entry.Code + " sgn=" + entry.Sgn + ") is not supported by JoyPro; skipped.");
                    continue;
                }
                if (entry.Code.EndsWith("_MOD1", StringComparison.Ordinal) || entry.Code.EndsWith("_MOD2", StringComparison.Ordinal) ||
                    entry.Code.EndsWith("_MOD1_MOD2", StringComparison.Ordinal))
                {
                    result.Warnings.Add(entry.Id + ": joystick modifier bind " + entry.Code + " is not supported yet; skipped.");
                    continue;
                }

                Guid guid;
                if (!slots.TryGetDevice(slot, out guid))
                {
                    if (reportedSlots.Add(slot))
                        result.Warnings.Add("X4 joystick slot " + slot.ToString(CultureInfo.InvariantCulture) + " is not pinned to a device in config.xml (assign it in X4's Manage Controllers/Joysticks); its binds are not imported.");
                    continue;
                }
                GameDevice device = devices.FindByInstanceGuid(guid);
                if (device == null)
                {
                    if (reportedSlots.Add(slot))
                        result.Warnings.Add("X4 joystick slot " + slot.ToString(CultureInfo.InvariantCulture) + " belongs to device " + guid.ToString("B") + ", which is not connected; its binds are not imported.");
                    continue;
                }

                string token = DecodeJoystickCode(entry.Code, axisSource);
                if (token.Length == 0)
                {
                    result.Warnings.Add(entry.Id + ": unknown joystick code " + entry.Code + "; skipped.");
                    continue;
                }
                GameInputBinding binding = new GameInputBinding
                {
                    ActionId = entry.Id,
                    Device = device.JoyProName,
                    Input = token,
                    IsAxis = axisSource
                };
                if (axisSource)
                    ReadAxisSettings(map, binding);
                result.Bindings.Add(binding);
            }

            result.Steps.Add("Game settings in the file (engine ffb set --device * --game X4): " + DescribeSettings(map, configPath) + ".");
            result.BindingsRead = result.Bindings.Count;
            return result;
        }

        // The control's game-wide invert in JoyPro's direction (the file's value XOR X4's HOTAS default) and, for the
        // throttle, one-sided (Slider) when Bidirectional Throttle is off (the param missing means On, as the game's menu shows).
        private static void ReadAxisSettings(X4Map map, GameInputBinding binding)
        {
            KeyValuePair<string, bool> invert;
            AxisTuning tuning = new AxisTuning();
            if (X4GameSettings.RangeInvert.TryGetValue(binding.ActionId, out invert))
            {
                bool fileInverted = string.Equals(map.Param(X4GameSettings.Params[invert.Key]) ?? "false", "true", StringComparison.OrdinalIgnoreCase);
                binding.Inverted = fileInverted != invert.Value;
                tuning.Inverted = binding.Inverted;
            }
            if (binding.ActionId == ThrottleRange && !string.Equals(map.Param("bidirectionalthrottle") ?? "true", "true", StringComparison.OrdinalIgnoreCase))
            {
                tuning.Slider = true;
                binding.KeepTuning = true;
            }
            binding.Tuning = tuning;
        }

        private static string DescribeSettings(X4Map map, string inputmapPath)
        {
            List<string> parts = ReadSettings(map, inputmapPath).Select(v => v.Key + "=" + AxisTuning.Num(v.Value)).ToList();
            return parts.Count == 0 ? "none found" : string.Join(", ", parts);
        }

        // The game-wide settings the export writes, as both files hold them: <config> params of inputmap.xml, then the
        // config.xml elements (rumble 0..1 as %). A missing bidirectionalthrottle (On in the game) is not reported.
        private static Dictionary<string, double> ReadSettings(X4Map map, string inputmapPath)
        {
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> param in X4GameSettings.Params)
            {
                double? value = X4GameSettings.Decode(param.Key, map.Param(param.Value), false);
                if (value.HasValue)
                    values[param.Key] = value.Value;
            }
            string configPath = ConfigPath(inputmapPath);
            string config = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
            foreach (KeyValuePair<string, string> element in X4GameSettings.ConfigElements)
            {
                Match match = Regex.Match(config, "<" + element.Value + ">([^<]*)</" + element.Value + ">");
                double? value = match.Success ? X4GameSettings.Decode(element.Key, match.Groups[1].Value, true) : null;
                if (value.HasValue)
                    values[element.Key] = value.Value;
            }
            return values;
        }

        // engine sync: the game-wide settings of inputmap.xml and config.xml next to it.
        public Dictionary<string, double> ReadGameSettings(string configPath, List<string> warnings)
        {
            List<string> errors = new List<string>();
            X4Map map;
            if (!X4Map.TryLoad(configPath, errors, out map))
            {
                warnings?.AddRange(errors);
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }
            return ReadSettings(map, configPath);
        }

        public string SettingsGame { get { return X4GameSettings.Game; } }

        // Settings > Game lives in config.xml, which the config watcher has to see too.
        public List<string> SettingsFiles(string configPath)
        {
            return new List<string> { ConfigPath(configPath) };
        }

        private static GameInputBinding ReadKeyboard(X4Entry entry, Dictionary<string, int> keyCodes)
        {
            string code = entry.Code.StartsWith("INPUT_KEYCODE_", StringComparison.Ordinal) ? entry.Code.Substring("INPUT_KEYCODE_".Length) : string.Empty;
            List<string> reformers = new List<string>();
            foreach (string suffix in new[] { "_SHIFT_CONTROL", "_CONTROL", "_SHIFT" })
            {
                if (!code.EndsWith(suffix, StringComparison.Ordinal) || keyCodes.ContainsKey(code))
                    continue;
                code = code.Substring(0, code.Length - suffix.Length);
                if (suffix.Contains("SHIFT"))
                    reformers.Add("LeftShift§Keyboard§LeftShift§False");
                if (suffix.Contains("CONTROL"))
                    reformers.Add("LeftControl§Keyboard§LeftControl§False");
                break;
            }
            int dik;
            if (!keyCodes.TryGetValue(code, out dik))
                return null;
            string key = GameKeyCodes.NameFromDik(dik);
            if (key.Length == 0)
                return null;
            return new GameInputBinding { ActionId = entry.Id, Device = "Keyboard", Input = key, IsAxis = false, Reformers = reformers };
        }

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            X4Map map;
            if (!X4Map.TryLoad(request.ConfigPath, result.Errors, out map))
                return result;
            X4Slots slots = X4Slots.Load(request.ConfigPath, result.Warnings);
            Dictionary<string, List<string>> functions = X4Data.Functions();
            Dictionary<string, int> keyCodes = X4Data.KeyCodes();

            // Every id JoyPro manages: the flight controls plus the members the game's menu binds together with them
            // (the map twins of travel mode, dock/undock ...). On-foot members (INPUT_STATE_FP_*) are never touched.
            Dictionary<string, List<string>> membersOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            HashSet<string> managedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (GameAction action in request.Catalog.Actions)
            {
                List<string> members = new List<string> { action.Id };
                List<string> extra;
                if (functions.TryGetValue(action.Id, out extra))
                    members.AddRange(extra.Where(m => !m.StartsWith("INPUT_STATE_FP_", StringComparison.Ordinal) && m != action.Id));
                membersOf[action.Id] = members;
                managedIds.UnionWith(members);
            }

            List<X4Entry> desired = new List<X4Entry>();
            List<KeyValuePair<GameInputBinding, string>> axisBinds = new List<KeyValuePair<GameInputBinding, string>>();
            HashSet<int> usedSlots = new HashSet<int>();
            bool usesKeyboard = false;
            foreach (GameInputBinding binding in request.Bindings)
            {
                string source;
                string code;
                string reason = Encode(binding, slots, keyCodes, out source, out code);
                if (reason.Length > 0)
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
                    continue;
                }

                int slot;
                bool axisSource;
                if (TryParseJoySource(source, out slot, out axisSource))
                    usedSlots.Add(slot);
                else
                    usesKeyboard = true;
                if (binding.IsAxis && axisSource)
                    axisBinds.Add(new KeyValuePair<GameInputBinding, string>(binding, source));

                foreach (string id in membersOf.ContainsKey(binding.ActionId) ? membersOf[binding.ActionId] : new List<string> { binding.ActionId })
                {
                    if (!desired.Any(d => d.Id == id && d.Source == source && d.Code == code))
                        desired.Add(new X4Entry { Element = ElementOf(id), Id = id, Source = source, Code = code, RelationName = binding.RelationName });
                }
            }

            // Releases (X4 has on-foot play too, so only managed flight ids are released): clean on the slots the export
            // writes, nuke on the slots config.xml pins to devices the export does not use. Keyboard lines only with
            // --include-keyboard.
            HashSet<string> releasedSources = new HashSet<string>(StringComparer.Ordinal);
            if (request.Mode == GameExportMode.Clean)
            {
                foreach (int slot in usedSlots)
                {
                    releasedSources.Add(JoySource(slot, true));
                    releasedSources.Add(JoySource(slot, false));
                }
                if (usesKeyboard && request.IncludeKeyboard)
                    releasedSources.Add("INPUT_SOURCE_KEYBOARD");
            }
            if (request.NukeUnusedDevices)
            {
                foreach (int slot in slots.PinnedSlots().Where(s => !usedSlots.Contains(s)))
                {
                    releasedSources.Add(JoySource(slot, true));
                    releasedSources.Add(JoySource(slot, false));
                }
            }

            // A conflict is the same action on the same device (joystick slot, or the keyboard): merge-overwrite
            // replaces it, merge keeps it. Binds of the action on other devices stay.
            List<X4Entry> releasedEntries = new List<X4Entry>();
            foreach (X4Entry entry in map.Entries)
            {
                if (!managedIds.Contains(entry.Id) || desired.Any(d => d.Same(entry)))
                    continue;
                bool release = releasedSources.Contains(entry.Source);
                if (!release && request.Mode == GameExportMode.MergeOverwrite)
                    release = desired.Any(d => d.Id == entry.Id && SameDevice(d.Source, entry.Source));
                if (release)
                    releasedEntries.Add(entry);
            }
            int removed = releasedEntries.Count;
            map.RemoveAll(releasedEntries);

            int added = 0;
            foreach (X4Entry entry in desired.OrderBy(d => X4Data.Order(d.Id)))
            {
                if (map.Entries.Any(e => e.Same(entry)))
                    continue;
                if (request.Mode == GameExportMode.Merge)
                {
                    X4Entry user = map.Entries.FirstOrDefault(e => e.Id == entry.Id && SameDevice(e.Source, entry.Source));
                    if (user != null)
                    {
                        result.ConflictsKept++;
                        result.Warnings.Add(entry.Id + ": kept the game's bind " + user.Source + " " + user.Code + " (merge mode).");
                        continue;
                    }
                }
                map.Insert(entry);
                added++;
            }

            result.BindingsWritten = added;
            List<string> settingChanges = new List<string>();
            Dictionary<string, double> explicitValues = FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, GameId, null);
            // Only the axes the file now has from JoyPro (merge may have kept the game's bind instead).
            List<KeyValuePair<GameInputBinding, string>> writtenAxes = axisBinds.Where(b => map.Entries.Any(e => e.Id == b.Key.ActionId && e.Source == b.Value)).ToList();
            Dictionary<string, double> values = DeriveSettings(map, writtenAxes, explicitValues, result);
            ApplyParams(map, values, settingChanges, result);
            string configText;
            string configPath = ConfigPath(request.ConfigPath);
            bool configChanged = ApplyConfigFile(configPath, explicitValues, settingChanges, result, out configText);

            if (removed == 0 && added == 0 && !map.ConfigChanged && !configChanged)
            {
                result.Steps.Add("inputmap.xml already matches the profile.");
                return result;
            }
            string summary = added.ToString(CultureInfo.InvariantCulture) + " entr" + (added == 1 ? "y" : "ies") + " added, " +
                             removed.ToString(CultureInfo.InvariantCulture) + " removed.";
            if (request.DryRun)
            {
                result.Steps.Add("Dry run: " + summary);
                result.Steps.AddRange(settingChanges.Select(c => "Dry run: " + c));
                return result;
            }

            if (removed > 0 || added > 0 || map.ConfigChanged)
            {
                GameExportSafety.BackupBeforeOverwrite(request.ConfigPath, result.Warnings);
                map.Save(request.ConfigPath);
                result.FilesWritten.Add(request.ConfigPath);
            }
            SaveConfigFile(configPath, configChanged, configText, result);
            result.Steps.Add(summary);
            result.Steps.AddRange(settingChanges);
            return result;
        }

        // Whether the profile holds game-wide settings for the game (an export without binds still writes them).
        public bool HasSettings()
        {
            return InternalDataManagement.FfbOverrides != null && FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, GameId, null).Count > 0;
        }

        // An export without X4 binds: only the game-wide settings set in JoyPro.
        public GameIoResult WriteSettings(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            X4Map map;
            if (!X4Map.TryLoad(request.ConfigPath, result.Errors, out map))
                return result;
            List<string> changes = new List<string>();
            Dictionary<string, double> explicitValues = FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, GameId, null);
            ApplyParams(map, explicitValues, changes, result);
            string configText;
            string configPath = ConfigPath(request.ConfigPath);
            bool configChanged = ApplyConfigFile(configPath, explicitValues, changes, result, out configText);
            if (changes.Count == 0)
            {
                result.Steps.Add("The X4 settings already match.");
                return result;
            }
            if (request.DryRun)
            {
                result.Steps.AddRange(changes.Select(c => "Dry run: " + c));
                return result;
            }
            if (map.ConfigChanged)
            {
                GameExportSafety.BackupBeforeOverwrite(request.ConfigPath, result.Warnings);
                map.Save(request.ConfigPath);
                result.FilesWritten.Add(request.ConfigPath);
            }
            SaveConfigFile(configPath, configChanged, configText, result);
            result.Steps.AddRange(changes);
            return result;
        }

        private static string ConfigPath(string inputmapPath)
        {
            return Path.Combine(Path.GetDirectoryName(inputmapPath) ?? string.Empty, "config.xml");
        }

        // What JoyPro sets in <config>: the values set with engine ffb set (device *), else, per flight axis, the invert
        // the relations agree on (relation invert XOR X4's HOTAS default) and Bidirectional Throttle from the throttle
        // relation (one-sided = off).
        private Dictionary<string, double> DeriveSettings(X4Map map, List<KeyValuePair<GameInputBinding, string>> axisBinds, Dictionary<string, double> explicitValues, GameIoResult result)
        {
            Dictionary<string, double> values = new Dictionary<string, double>(explicitValues, StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, KeyValuePair<GameInputBinding, string>> range in axisBinds.GroupBy(b => b.Key.ActionId, StringComparer.Ordinal))
            {
                KeyValuePair<string, bool> invert;
                if (!X4GameSettings.RangeInvert.TryGetValue(range.Key, out invert))
                    continue;
                List<bool> wanted = range.Select(b => b.Key.Inverted != invert.Value).Distinct().ToList();
                string param = X4GameSettings.Params[invert.Key];
                if (explicitValues.ContainsKey(invert.Key))
                {
                    if (wanted.Count == 1 && (explicitValues[invert.Key] != 0) != wanted[0])
                        result.Warnings.Add(range.Key + ": " + param + " is set to " + (explicitValues[invert.Key] != 0 ? "true" : "false") + " in JoyPro's X4 settings, which reverses the relation's direction.");
                }
                else if (wanted.Count > 1)
                    result.Warnings.Add(range.Key + ": the relations disagree on invert, and X4 inverts a control for every device; " + param + " was left as it is.");
                else
                    values[invert.Key] = wanted[0] ? 1 : 0;
                // Other devices on the same control (a gamepad, a slot JoyPro does not write) get the same inversion.
                HashSet<string> ours = new HashSet<string>(range.Select(b => b.Value), StringComparer.Ordinal);
                if (map.Entries.Any(e => e.Id == range.Key && !ours.Contains(e.Source)) && values.ContainsKey(invert.Key))
                    result.Warnings.Add(range.Key + " is also bound on another device in X4; " + param + " applies to it too.");
            }
            List<KeyValuePair<GameInputBinding, string>> throttles = axisBinds.Where(b => b.Key.ActionId == ThrottleRange).ToList();
            if (throttles.Count > 0 && !explicitValues.ContainsKey("bidirectionalThrottle"))
            {
                List<bool> sliders = throttles.Select(b => b.Key.Tuning != null && b.Key.Tuning.Slider).Distinct().ToList();
                if (sliders.Count == 1)
                    values["bidirectionalThrottle"] = sliders[0] ? 0 : 1;
                else
                    result.Warnings.Add("The throttle relations disagree on one-sided (Slider); Bidirectional Throttle was left as it is.");
            }
            if (!explicitValues.ContainsKey("deadzone") && axisBinds.Any(b => b.Key.Deadzone > 0))
                result.Steps.Add("X4 has one dead zone for all joystick axes (now " + (map.Param("deadzone") ?? "?") + " %); relation dead zones are not written. Set it with: engine ffb set --device * --game X4 --param deadzone=<0..100>.");
            return values;
        }

        private static void ApplyParams(X4Map map, Dictionary<string, double> values, List<string> changes, GameIoResult result)
        {
            foreach (KeyValuePair<string, double> value in values.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                string param;
                if (!X4GameSettings.Params.TryGetValue(value.Key, out param))
                    continue;
                string rendered = X4GameSettings.EncodeParam(value.Key, value.Value);
                // A missing bidirectionalthrottle shows as On in the game's menu (checked in game, X4 9.00): the default is
                // "true", so "false" is written explicitly.
                string current = map.Param(param) ?? (param == "bidirectionalthrottle" ? "true" : null);
                double? parsed = X4GameSettings.Decode(value.Key, current, false);
                if (parsed.HasValue && Math.Abs(parsed.Value - X4GameSettings.Decode(value.Key, rendered, false).Value) < 1e-9)
                    continue;
                if (!map.SetParam(param, rendered))
                {
                    result.Warnings.Add("inputmap.xml has no <config>; " + value.Key + " was not written.");
                    continue;
                }
                changes.Add("inputmap.xml " + param + " = " + rendered);
            }
        }

        private static bool ApplyConfigFile(string configPath, Dictionary<string, double> values, List<string> changes, GameIoResult result, out string text)
        {
            text = null;
            if (!values.Keys.Any(k => X4GameSettings.ConfigElements.ContainsKey(k)))
                return false;
            if (!File.Exists(configPath))
            {
                result.Warnings.Add("X4 config.xml not found next to inputmap.xml; Settings > Game values were not written.");
                return false;
            }
            text = File.ReadAllText(configPath);
            return X4GameSettings.ApplyConfig(ref text, values, changes, result.Warnings);
        }

        // config.xml keeps its encoding bytes (no BOM is added) and every untouched character.
        private static void SaveConfigFile(string configPath, bool changed, string text, GameIoResult result)
        {
            if (!changed)
                return;
            byte[] original = File.ReadAllBytes(configPath);
            bool bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
            GameExportSafety.BackupBeforeOverwrite(configPath, result.Warnings);
            File.WriteAllText(configPath, text, new UTF8Encoding(bom));
            result.FilesWritten.Add(configPath);
        }

        private static string Encode(GameInputBinding binding, X4Slots slots, Dictionary<string, int> keyCodes, out string source, out string code)
        {
            source = string.Empty;
            code = string.Empty;
            bool range = ElementOf(binding.ActionId) == "range";

            if (binding.IsKeyboard)
            {
                if (range)
                    return "an X4 axis cannot take a key.";
                int dik;
                if (!GameKeyCodes.TryGetDik(binding.Input, out dik))
                    return "key '" + binding.Input + "' has no X4 key code.";
                string name = keyCodes.FirstOrDefault(k => k.Value == dik).Key;
                if (string.IsNullOrEmpty(name))
                    return "key '" + binding.Input + "' has no X4 key code.";
                bool shift = false;
                bool control = false;
                foreach (string reformer in binding.Reformers ?? new List<string>())
                {
                    string[] parts = reformer.Split('§');
                    string key = parts.Length >= 3 && string.Equals(parts[1], "Keyboard", StringComparison.OrdinalIgnoreCase) ? parts[2] : string.Empty;
                    if (key.EndsWith("Shift", StringComparison.OrdinalIgnoreCase))
                        shift = true;
                    else if (key.EndsWith("Control", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Ctrl", StringComparison.OrdinalIgnoreCase))
                        control = true;
                    else
                        return "X4 keyboard binds only take Shift and Ctrl as modifiers.";
                }
                source = "INPUT_SOURCE_KEYBOARD";
                code = "INPUT_KEYCODE_" + name + (shift && control ? "_SHIFT_CONTROL" : shift ? "_SHIFT" : control ? "_CONTROL" : string.Empty);
                return string.Empty;
            }

            if (binding.Reformers != null && binding.Reformers.Count > 0)
                return "X4 joystick modifiers are not supported yet.";

            Guid guid;
            string name0;
            if (!GameDeviceDirectory.TryParseJoyProName(binding.Device, out name0, out guid))
                return "device '" + binding.Device + "' has no instance GUID.";
            int slot;
            string slotReason = slots.SlotOf(guid, out slot);
            if (slotReason.Length > 0)
                return slotReason;

            JoyInput input;
            if (!GameInputTokens.TryParse(binding.Input, out input))
                return "unsupported input '" + binding.Input + "'.";
            if (range != (input.Kind == JoyInputKind.Axis))
                return range ? "'" + binding.ActionId + "' is an axis; bind an axis." : "'" + binding.ActionId + "' needs a button or hat (half axes are not supported).";

            switch (input.Kind)
            {
                case JoyInputKind.Axis:
                    if (input.Index < 0 || input.Index >= AxisNames.Length)
                        return "axis '" + binding.Input + "' does not exist in X4.";
                    source = JoySource(slot, true);
                    code = "INPUT_JOYAXIS_" + AxisNames[input.Index];
                    return string.Empty;
                case JoyInputKind.Button:
                    if (input.Index >= MaxButtons)
                        return "X4 supports buttons 1-64 only.";
                    source = JoySource(slot, false);
                    code = "INPUT_XBUTTON_" + (input.Index < NamedButtons.Length ? NamedButtons[input.Index] : (input.Index + 1).ToString(CultureInfo.InvariantCulture));
                    return string.Empty;
                case JoyInputKind.Pov:
                    if (input.Index != 0)
                        return "X4 supports only the first hat of a device.";
                    source = JoySource(slot, false);
                    code = "INPUT_XBUTTON_DPAD_" + HatDirections[input.Direction];
                    return string.Empty;
                default:
                    return "unsupported input '" + binding.Input + "'.";
            }
        }

        private static string DecodeJoystickCode(string code, bool axisSource)
        {
            if (axisSource)
            {
                int axis = Array.IndexOf(AxisNames, code.StartsWith("INPUT_JOYAXIS_", StringComparison.Ordinal) ? code.Substring("INPUT_JOYAXIS_".Length) : string.Empty);
                return axis >= 0 ? GameInputTokens.AxisToken(axis) : string.Empty;
            }
            if (!code.StartsWith("INPUT_XBUTTON_", StringComparison.Ordinal))
                return string.Empty;
            string name = code.Substring("INPUT_XBUTTON_".Length);
            if (name.StartsWith("DPAD_", StringComparison.Ordinal))
            {
                int direction = Array.IndexOf(HatDirections, name.Substring("DPAD_".Length));
                return direction >= 0 ? GameInputTokens.PovToken(0, direction) : string.Empty;
            }
            int named = Array.IndexOf(NamedButtons, name);
            if (named >= 0)
                return GameInputTokens.ButtonToken(named);
            int number;
            return int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 1 && number <= MaxButtons
                ? GameInputTokens.ButtonToken(number - 1)
                : string.Empty;
        }

        private static string ElementOf(string id)
        {
            if (id.StartsWith("INPUT_RANGE_", StringComparison.Ordinal))
                return "range";
            if (id.StartsWith("INPUT_STATE_", StringComparison.Ordinal))
                return "state";
            return "action";
        }

        private static string JoySource(int slot, bool axis)
        {
            string source = axis ? "INPUT_SOURCE_JOYAXES" : "INPUT_SOURCE_JOYBUTTONS";
            return slot <= 1 ? source : source + "_" + slot.ToString(CultureInfo.InvariantCulture);
        }

        // Same joystick slot (axes or buttons of it), or the same non-joystick source.
        private static bool SameDevice(string a, string b)
        {
            int slotA, slotB;
            bool axisA, axisB;
            bool joyA = TryParseJoySource(a ?? string.Empty, out slotA, out axisA);
            bool joyB = TryParseJoySource(b ?? string.Empty, out slotB, out axisB);
            if (joyA || joyB)
                return joyA && joyB && slotA == slotB;
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        private static bool TryParseJoySource(string source, out int slot, out bool axis)
        {
            slot = 0;
            axis = false;
            string rest;
            if (source.StartsWith("INPUT_SOURCE_JOYAXES", StringComparison.Ordinal))
            {
                axis = true;
                rest = source.Substring("INPUT_SOURCE_JOYAXES".Length);
            }
            else if (source.StartsWith("INPUT_SOURCE_JOYBUTTONS", StringComparison.Ordinal))
            {
                rest = source.Substring("INPUT_SOURCE_JOYBUTTONS".Length);
            }
            else
            {
                return false;
            }
            if (rest.Length == 0)
            {
                slot = 1;
                return true;
            }
            return rest[0] == '_' && int.TryParse(rest.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out slot) && slot >= 2 && slot <= MaxSlots;
        }

        // Reads the Controls menu from the installed game (X4CatalogSource: gameoptions.lua and the English texts from the
        // .cat/.dat archives, the numeric ids' names from X4.exe) and adds the space-flight controls the catalog does not
        // have yet; known controls keep their title and category. order.tsv gets the numeric ids X4.exe has and it does
        // not, functions.tsv the menu functions it does not list (SaveRefreshFiles).
        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            pendingOrder = null;
            pendingFunctions = null;
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                GameInstall install = GameInstalls.Find(GameId, result.Warnings);
                sourcePath = install != null && install.Installed ? install.InstallPath : null;
            }
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                result.Errors.Add("X4: Foundations was not found. Pass --path with the X4 folder (01.cat ... and X4.exe) or a folder with " + X4CatalogSource.MenuScript + " and " + X4CatalogSource.EnglishTexts + ".");
                return null;
            }
            sourcePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourcePath.Trim().Trim('"')));
            if (File.Exists(sourcePath))
                sourcePath = Path.GetDirectoryName(sourcePath);

            Dictionary<string, Dictionary<int, string>> known = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
            foreach (string[] parts in X4Data.OrderRows())
            {
                int number;
                if (parts.Length < 3 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                    continue;
                if (!known.ContainsKey(parts[0]))
                    known[parts[0]] = new Dictionary<int, string>();
                known[parts[0]][number] = parts[2];
            }
            X4CatalogSource.Result menu = X4CatalogSource.Read(sourcePath, known, result.Warnings, result.Errors);
            if (menu == null)
                return null;

            GameCatalog catalog = new GameCatalog { GameId = GameId, SourcePath = sourcePath };
            foreach (GameAction action in current == null ? new List<GameAction>() : current.Actions)
                catalog.Add(action);
            List<X4CatalogSource.Row> flight = menu.Rows.Where(r => r.Flight).ToList();
            int added = 0;
            foreach (X4CatalogSource.Row row in flight.Where(r => catalog.Find(r.Id) == null))
            {
                string readable;
                catalog.Add(new GameAction { Id = row.Id, Title = ReadableTitles.TryGetValue(row.Id, out readable) ? readable : row.Title.Replace('\t', ' '), IsAxis = row.IsAxis, Category = TidyCategory(row.Category.Replace('\t', ' ')) });
                added++;
            }

            HashSet<string> knownIds = new HashSet<string>(known.Values.SelectMany(v => v.Values), StringComparer.Ordinal);
            pendingOrder = menu.ExeControls.Where(c => !knownIds.Contains(c.Item3))
                .Select(c => c.Item1 + "\t" + c.Item2.ToString(CultureInfo.InvariantCulture) + "\t" + c.Item3).ToList();
            Dictionary<string, List<string>> functions = X4Data.Functions();
            pendingFunctions = menu.Functions.Where(f => !functions.ContainsKey(f.Key))
                .Select(f => f.Key + "\t" + string.Join(",", f.Value)).Distinct().ToList();

            result.Steps.Add("Read the Controls menu of " + sourcePath + ": " + menu.Rows.Count.ToString(CultureInfo.InvariantCulture) + " controls, " +
                             flight.Count.ToString(CultureInfo.InvariantCulture) + " space flight, " + added.ToString(CultureInfo.InvariantCulture) + " new; " +
                             (menu.ExeControls.Count > 0 ? menu.ExeControls.Count.ToString(CultureInfo.InvariantCulture) + " control names from X4.exe" : "control names from order.tsv") + ".");
            if (pendingOrder.Count > 0 || pendingFunctions.Count > 0)
                result.Steps.Add("New numeric ids for order.tsv: " + pendingOrder.Count.ToString(CultureInfo.InvariantCulture) + ", new menu functions for functions.tsv: " + pendingFunctions.Count.ToString(CultureInfo.InvariantCulture) + ".");
            HashSet<string> menuIds = new HashSet<string>(flight.Select(r => r.Id), StringComparer.Ordinal);
            foreach (GameAction gone in catalog.Actions.Where(a => !menuIds.Contains(a.Id)))
                result.Warnings.Add("'" + gone.Id + "' is no longer a space-flight control of the game's Controls menu.");
            return catalog;
        }

        private List<string> pendingOrder;
        private List<string> pendingFunctions;

        // Appends the rows RefreshCatalog prepared to order.tsv and functions.tsv (nothing else in them changes).
        public List<string> SaveRefreshFiles(List<string> warnings)
        {
            List<string> written = new List<string>();
            foreach (KeyValuePair<string, List<string>> file in new[]
            {
                new KeyValuePair<string, List<string>>("order.tsv", pendingOrder),
                new KeyValuePair<string, List<string>>("functions.tsv", pendingFunctions)
            })
            {
                if (file.Value == null || file.Value.Count == 0)
                    continue;
                string path = GameDataPaths.WritableDbFile("X4", file.Key);
                string text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
                string newline = text.Length == 0 || text.Contains("\r\n") ? "\r\n" : "\n";
                if (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal))
                    text += newline;
                File.WriteAllText(path, text + string.Join(newline, file.Value) + newline, new UTF8Encoding(false));
                written.Add(path);
            }
            pendingOrder = null;
            pendingFunctions = null;
            return written;
        }

        // One <action|state|range> line of inputmap.xml.
        private sealed class X4Entry
        {
            public string Element = string.Empty;
            public string Id = string.Empty;
            public string Source = string.Empty;
            public string Code = string.Empty;
            public string Sgn = string.Empty;
            public string RelationName = string.Empty;
            public int Line = -1;

            public bool Same(X4Entry other)
            {
                return other != null && other.Id == Id && other.Source == Source && other.Code == Code && other.Sgn == Sgn;
            }
        }

        // inputmap.xml as lines; entries are parsed from the lines, edits change only the affected lines.
        private sealed class X4Map
        {
            private List<string> lines;
            private string newline = "\n";
            private bool bom;
            private bool trailingNewline;

            public List<X4Entry> Entries { get; private set; }

            public static bool TryLoad(string path, List<string> errors, out X4Map map)
            {
                map = null;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    errors.Add("X4 inputmap.xml not found: " + path);
                    return false;
                }
                byte[] bytes = File.ReadAllBytes(path);
                X4Map result = new X4Map();
                result.bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                string text = new UTF8Encoding(false).GetString(bytes, result.bom ? 3 : 0, bytes.Length - (result.bom ? 3 : 0));
                try
                {
                    XDocument.Parse(text);
                }
                catch (Exception ex)
                {
                    errors.Add("X4 inputmap.xml is not valid XML: " + ex.Message);
                    return false;
                }
                if (text.IndexOf("<inputmap", StringComparison.Ordinal) < 0)
                {
                    errors.Add("Not an X4 inputmap: " + path);
                    return false;
                }
                result.newline = text.Contains("\r\n") ? "\r\n" : "\n";
                result.trailingNewline = text.EndsWith("\n", StringComparison.Ordinal);
                string body = result.trailingNewline ? text.Substring(0, text.Length - result.newline.Length) : text;
                result.lines = body.Split(new[] { result.newline }, StringSplitOptions.None).ToList();
                result.Reparse();
                map = result;
                return true;
            }

            private void Reparse()
            {
                Entries = new List<X4Entry>();
                for (int i = 0; i < lines.Count; i++)
                {
                    Match match = EntryLine.Match(lines[i]);
                    if (!match.Success)
                        continue;
                    Dictionary<string, string> attributes = Attribute.Matches(match.Groups[3].Value).Cast<Match>()
                        .GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
                    string id;
                    if (!attributes.TryGetValue("id", out id))
                        continue;
                    string source;
                    string code;
                    string sgn;
                    attributes.TryGetValue("source", out source);
                    attributes.TryGetValue("code", out code);
                    attributes.TryGetValue("sgn", out sgn);
                    Entries.Add(new X4Entry { Element = match.Groups[2].Value, Id = id, Source = source ?? string.Empty, Code = code ?? string.Empty, Sgn = sgn ?? string.Empty, Line = i });
                }
            }

            // Bottom-up, so the line numbers of the remaining entries stay valid.
            public void RemoveAll(IEnumerable<X4Entry> entries)
            {
                foreach (int line in entries.Select(e => e.Line).Distinct().OrderByDescending(l => l))
                    lines.RemoveAt(line);
                Reparse();
            }

            // The game sorts each section by the numeric control id; a new line goes after the last line of the same
            // element whose id sorts before or equal to it, else after the section comment, else before </inputmap>.
            public void Insert(X4Entry entry)
            {
                int order = X4Data.Order(entry.Id);
                X4Entry anchor = Entries.Where(e => e.Element == entry.Element && X4Data.Order(e.Id) <= order).OrderBy(e => e.Line).LastOrDefault();
                int index;
                string indent = "  ";
                X4Entry sample = anchor ?? Entries.FirstOrDefault(e => e.Element == entry.Element) ?? Entries.FirstOrDefault();
                if (sample != null)
                    indent = EntryLine.Match(lines[sample.Line]).Groups[1].Value;
                if (anchor != null)
                {
                    index = anchor.Line + 1;
                }
                else
                {
                    string comment = "<!-- input " + (entry.Element == "range" ? "ranges" : entry.Element == "state" ? "states" : "actions") + " -->";
                    int commentLine = lines.FindIndex(l => l.Trim() == comment);
                    X4Entry first = Entries.Where(e => e.Element == entry.Element).OrderBy(e => e.Line).FirstOrDefault();
                    if (commentLine >= 0)
                        index = commentLine + 1;
                    else if (first != null)
                        index = first.Line;
                    else
                        index = Math.Max(0, lines.FindLastIndex(l => l.Trim() == "</inputmap>"));
                }
                lines.Insert(index, indent + "<" + entry.Element + " id=\"" + entry.Id + "\" source=\"" + entry.Source + "\" code=\"" + entry.Code + "\"/>");
                Reparse();
            }

            public bool ConfigChanged { get; private set; }

            private static readonly Regex ParamLine = new Regex(@"^(\s*)<param\s+name=""([^""]+)""\s+value=""([^""]*)""\s*/>\s*$", RegexOptions.Compiled);

            private int ConfigStart()
            {
                return lines.FindIndex(l => l.Trim() == "<config>");
            }

            private int ConfigEnd(int start)
            {
                return start < 0 ? -1 : lines.FindIndex(start + 1, l => l.Trim() == "</config>");
            }

            // <config><param name value/> of the profile: null when the param is not there.
            public string Param(string name)
            {
                int start = ConfigStart();
                int end = ConfigEnd(start);
                for (int i = start + 1; start >= 0 && i < end; i++)
                {
                    Match match = ParamLine.Match(lines[i]);
                    if (match.Success && match.Groups[2].Value == name)
                        return match.Groups[3].Value;
                }
                return null;
            }

            // Sets the value of a param in place, or adds the param as the last line of <config> (as the shipped maps
            // place the params a newer version added). False when the file has no <config>.
            public bool SetParam(string name, string value)
            {
                int start = ConfigStart();
                int end = ConfigEnd(start);
                if (start < 0 || end < 0)
                    return false;
                string indent = null;
                for (int i = start + 1; i < end; i++)
                {
                    Match match = ParamLine.Match(lines[i]);
                    if (!match.Success)
                        continue;
                    indent = indent ?? match.Groups[1].Value;
                    if (match.Groups[2].Value != name)
                        continue;
                    if (match.Groups[3].Value == value)
                        return true;
                    Group current = match.Groups[3];
                    lines[i] = lines[i].Substring(0, current.Index) + value + lines[i].Substring(current.Index + current.Length);
                    ConfigChanged = true;
                    return true;
                }
                if (indent == null)
                {
                    string configIndent = lines[start].Substring(0, lines[start].Length - lines[start].TrimStart().Length);
                    indent = configIndent + "  ";
                }
                lines.Insert(end, indent + "<param name=\"" + name + "\" value=\"" + value + "\"/>");
                ConfigChanged = true;
                Reparse();
                return true;
            }

            public void Save(string path)
            {
                string text = string.Join(newline, lines) + (trailingNewline ? newline : string.Empty);
                byte[] body = new UTF8Encoding(false).GetBytes(text);
                using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    if (bom)
                        stream.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                    stream.Write(body, 0, body.Length);
                }
            }
        }

        // config.xml <joysticks>: which DirectInput device is pinned to which slot.
        private sealed class X4Slots
        {
            private readonly Dictionary<int, Guid> bySlot = new Dictionary<int, Guid>();
            private readonly HashSet<Guid> disabled = new HashSet<Guid>();

            public static X4Slots Load(string inputmapPath, List<string> warnings)
            {
                X4Slots slots = new X4Slots();
                string path = Path.Combine(Path.GetDirectoryName(inputmapPath) ?? string.Empty, "config.xml");
                if (!File.Exists(path))
                {
                    warnings.Add("X4 config.xml not found next to inputmap.xml; no joystick is pinned to a slot.");
                    return slots;
                }
                try
                {
                    XElement joysticks = XDocument.Load(path).Descendants("joysticks").FirstOrDefault();
                    if (joysticks == null)
                        return slots;
                    foreach (XElement joystick in joysticks.Elements("joystick"))
                    {
                        Guid guid;
                        if (!TryParseX4Guid(joystick.Value, out guid))
                            continue;
                        string disabledValue = (string)joystick.Attribute("disabled") ?? string.Empty;
                        if (disabledValue == "1" || string.Equals(disabledValue, "true", StringComparison.OrdinalIgnoreCase))
                        {
                            slots.disabled.Add(guid);
                            continue;
                        }
                        int num;
                        if (!int.TryParse((string)joystick.Attribute("num") ?? string.Empty, NumberStyles.Integer, CultureInfo.InvariantCulture, out num) || num < 0 || num >= MaxSlots)
                            continue;
                        string current = (string)joystick.Attribute("current") ?? "1";
                        if (current == "0" || string.Equals(current, "false", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!slots.bySlot.ContainsKey(num + 1))
                            slots.bySlot[num + 1] = guid;
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("X4 config.xml could not be read: " + ex.Message);
                }
                return slots;
            }

            public bool TryGetDevice(int slot, out Guid guid)
            {
                return bySlot.TryGetValue(slot, out guid);
            }

            public IEnumerable<int> PinnedSlots()
            {
                return bySlot.Keys;
            }

            public string SlotOf(Guid guid, out int slot)
            {
                slot = bySlot.Where(p => p.Value == guid).Select(p => p.Key).DefaultIfEmpty(0).First();
                if (slot > 0)
                    return string.Empty;
                if (disabled.Contains(guid))
                    return "the device is disabled in X4 (Options > Controls > Manage Controllers/Joysticks).";
                return "the device has no fixed X4 slot yet; assign it once in X4 (Options > Controls > Manage Controllers/Joysticks).";
            }
        }

        // X4 writes instance GUIDs as {%08lx-%04x-%04x} plus 8 bytes "-%02x".
        internal static bool TryParseX4Guid(string text, out Guid guid)
        {
            guid = Guid.Empty;
            string[] parts = (text ?? string.Empty).Trim().Trim('{', '}').Split('-');
            if (parts.Length != 11)
                return false;
            try
            {
                uint a = uint.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                ushort b = ushort.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                ushort c = ushort.Parse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte[] rest = parts.Skip(3).Select(p => byte.Parse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
                guid = new Guid(unchecked((int)a), unchecked((short)b), unchecked((short)c), rest);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        // DB\X4: functions.tsv (controls the menu binds together), order.tsv (numeric ids); the key names are JoyPro's TOOLS\Conversions\X4Keys.tsv.
        private static class X4Data
        {
            private static Dictionary<string, int> order;

            public static Dictionary<string, List<string>> Functions()
            {
                Dictionary<string, List<string>> functions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (string[] parts in Rows("functions.tsv"))
                {
                    if (parts.Length >= 2)
                        functions[parts[0]] = parts[1].Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                }
                return functions;
            }

            public static Dictionary<string, int> KeyCodes()
            {
                Dictionary<string, int> codes = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (string[] parts in ConversionRows("X4Keys.tsv"))
                {
                    int value;
                    if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && !codes.ContainsKey(parts[0]))
                        codes[parts[0]] = value;
                }
                return codes;
            }

            public static int Order(string id)
            {
                if (order == null)
                {
                    order = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (string[] parts in Rows("order.tsv"))
                    {
                        int value;
                        if (parts.Length >= 3 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                            order[parts[2]] = value;
                    }
                }
                int numeric;
                return order.TryGetValue(id ?? string.Empty, out numeric) ? numeric : int.MaxValue;
            }

            public static IEnumerable<string[]> OrderRows()
            {
                return Rows("order.tsv");
            }

            public static string FilePath(string fileName)
            {
                return GameDataPaths.DbFile("X4", fileName);
            }

            // JoyPro's own key table (TOOLS\Conversions), shipped with the engine.
            private static IEnumerable<string[]> ConversionRows(string fileName)
            {
                return RowsOf(Path.Combine(MainStructure.PROGPATH ?? string.Empty, "TOOLS", "Conversions", fileName));
            }

            private static IEnumerable<string[]> Rows(string fileName)
            {
                return RowsOf(FilePath(fileName));
            }

            private static IEnumerable<string[]> RowsOf(string path)
            {
                if (!File.Exists(path))
                    yield break;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (line.Length == 0 || line[0] == '#')
                        continue;
                    yield return line.Split('\t');
                }
            }
        }
    }
}
