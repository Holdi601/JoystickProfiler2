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
    // Elite Dangerous keeps its controls in %LOCALAPPDATA%\Frontier Developments\Elite Dangerous\Options\Bindings\
    // <Preset>.4.<minor>.binds; StartPreset.4.start names the preset of each category (General, Ship, SRV, OnFoot).
    // A device is its USB VID+PID ("231D0125") or a symbolic name from the game's DeviceMappings.xml. JoyPro edits the
    // Ship preset in place and only the slots of ship flight elements; every other byte stays (see
    // docs/games/elite-dangerous.md).
    internal sealed class EliteDangerousIntegration : IGameIntegration, IGameSettingsExport, IGameSettingsReader
    {
        private const string GameId_ = "EliteDangerous";
        private const string NoDevice = "{NoDevice}";
        private const int MaxModifiers = 3;
        private static readonly string[] AxisKeys = { "Joy_XAxis", "Joy_YAxis", "Joy_ZAxis", "Joy_RXAxis", "Joy_RYAxis", "Joy_RZAxis", "Joy_UAxis", "Joy_VAxis" };
        private static readonly string[] PovKeys = { "Up", null, "Right", null, "Down", null, "Left", null };
        private static readonly HashSet<string> NonJoystickDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NoDevice, "Keyboard", "Mouse", "GamePad", "GenericJoystick", "XB360 Pad"
        };
        private static readonly Regex SlotLine = new Regex(@"^(\s*)<(Binding|Primary|Secondary)\s+Device=""([^""]*)""\s+Key=""([^""]*)""([^>]*?)(/?)>\s*$", RegexOptions.Compiled);
        private static readonly Regex ValueLine = new Regex(@"^(\s*)<(Inverted|Deadzone)\s+Value=""([^""]*)""\s*/>\s*$", RegexOptions.Compiled);
        private static readonly Regex ModifierLine = new Regex(@"^(\s*)<Modifier\s+Device=""([^""]*)""\s+Key=""([^""]*)""\s*/>\s*$", RegexOptions.Compiled);
        // A value-only setting directly under <Root> (<ThrottleRange Value="" />).
        private static readonly Regex SettingLine = new Regex(@"^(\t)<([A-Za-z0-9_]+)\s+Value=""([^""]*)""\s*/>\s*$", RegexOptions.Compiled);
        private const string PlatformDeadzone = "DefaultPlatformDeadzone";
        private const string ThrottleAxis = "ThrottleAxis";
        private const string ThrottleRange = "ThrottleRange";
        private const string ForwardOnly = "Bindings_ThrottleForewardOnly";

        // The <Inverted> value Elite's own joystick presets (ControlSchemes\*.binds) use for each axis element with a
        // normal DirectInput device: pitch, throttle, vertical thrust and head look pitch are inverted there (stick
        // forward and throttle forward give DirectInput's low end). A JoyPro relation keeps DCS's direction (no invert =
        // stick forward is nose down, throttle forward is more thrust), so the file gets invert XOR this value and the
        // import reads it back the same way. Evidence: docs/games/elite-dangerous.md "Invert direction".
        private static readonly HashSet<string> NaturallyInverted = new HashSet<string>(StringComparer.Ordinal)
        {
            "PitchAxisRaw", "PitchAxisAlternate", "PitchAxis_Landing",
            "ThrottleAxis", "AheadThrust", "AheadThrust_Landing",
            "VerticalThrustRaw", "VerticalThrustAlternate", "VerticalThrust_Landing",
            "HeadLookPitchAxisRaw", "CamTranslateYAxis", "ExplorationFSSRadioTuningX_Raw"
        };

        internal static bool NaturalInvert(string element)
        {
            return NaturallyInverted.Contains(element ?? string.Empty);
        }

        public string GameId { get { return "EliteDangerous"; } }
        public string DisplayName { get { return "Elite Dangerous (experimental)"; } }
        public IReadOnlyList<string> Aliases { get { return new[] { "elite", "ed", "elitedangerous" }; } }
        public string DbFolder { get { return "EliteDangerous"; } }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return false; } }
        public string Notes
        {
            get
            {
                return "Close Elite Dangerous before exporting. JoyPro edits the Ship preset (Custom.4.x.binds); save your controls once in " +
                       "Elite (Options > Controls) so that file exists. Keep every device the preset uses connected: Elite ignores a preset " +
                       "that names a missing device. Two identical devices cannot be told apart and are skipped.";
            }
        }
        public IReadOnlyList<string> ProcessNames { get { return new[] { "EliteDangerous64", "EliteDangerous" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        public GameFeature KeyboardBinds { get { return GameFeature.Yes; } }
        public GameFeature ModifierBinds { get { return GameFeature.Yes; } }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            List<string> paths = new List<string>();
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Frontier Developments", "Elite Dangerous", "Options", "Bindings");
            if (!Directory.Exists(folder))
                return paths;
            string preset;
            string file = ShipPresetFile(folder, warnings, out preset);
            if (file.Length > 0)
                paths.Add(file);
            return paths;
        }

        // The Ship line of StartPreset.4.start and its newest <Preset>.4.<minor>.binds.
        private static string ShipPresetFile(string folder, List<string> warnings, out string preset)
        {
            preset = string.Empty;
            string start = Path.Combine(folder, "StartPreset.4.start");
            string[] lines = File.Exists(start) ? File.ReadAllText(start).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None) : new string[0];
            preset = lines.Length >= 2 ? lines[1].Trim() : lines.Length == 1 ? lines[0].Trim() : "Custom";
            string name = preset;
            string file = Directory.GetFiles(folder, name + ".4.*.binds")
                .Select(f => new { File = f, Minor = MinorVersion(Path.GetFileName(f)) })
                .Where(f => f.Minor >= 0)
                .OrderByDescending(f => f.Minor)
                .Select(f => f.File)
                .FirstOrDefault();
            if (file == null)
            {
                warnings?.Add("Elite's Ship controls use the preset '" + preset + "', which has no file in " + folder +
                              " (a stock preset). Change one control in Elite (Options > Controls, Ship) and save, so the game creates Custom.4.x.binds.");
                return string.Empty;
            }
            return file;
        }

        private static int MinorVersion(string fileName)
        {
            Match match = Regex.Match(fileName, @"\.4\.(\d+)\.binds$", RegexOptions.IgnoreCase);
            int minor;
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out minor) ? minor : -1;
        }

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            EliteFile file;
            if (!EliteFile.TryLoad(configPath, result.Errors, out file))
                return result;
            EliteDevices map = EliteDevices.Load(devices);
            WarnMissingDevices(file, map, result.Warnings);
            Dictionary<string, string> keys = EliteData.KeysToJoyPro();
            bool forwardOnly = file.Setting(ThrottleRange) == ForwardOnly;

            foreach (EliteElement element in file.Elements)
            {
                if (catalog.Actions.Count > 0 && !catalog.Contains(element.Name))
                    continue;
                foreach (EliteSlot slot in element.Slots)
                {
                    if (slot.Device == NoDevice || slot.Device.Length == 0)
                        continue;
                    GameInputBinding binding = DecodeSlot(element, slot, map, keys, result.Warnings);
                    if (binding == null)
                        continue;
                    if (binding.IsAxis)
                    {
                        // Elite's values as the relation's tuning for the game: invert in JoyPro's direction, the dead
                        // zone (or the game's default symbol), the forward-only throttle range as Slider.
                        AxisTuning tuning = new AxisTuning { Inverted = binding.Inverted, Deadzone = binding.Deadzone };
                        if (element.Deadzone.Trim() == PlatformDeadzone)
                            tuning.Native["platformDeadzone"] = 1;
                        if (element.Name == ThrottleAxis && forwardOnly)
                        {
                            tuning.Slider = true;
                            binding.KeepTuning = true;
                        }
                        binding.Tuning = tuning;
                    }
                    result.Bindings.Add(binding);
                }
            }
            string settings = EliteGameSettings.Describe(ReadSettings(file));
            result.Steps.Add("Game settings in the preset (engine ffb set --device * --game EliteDangerous): " +
                             (settings.Length > 0 ? settings : "none found") + "; Throttle Axis Range " + (forwardOnly ? "forward only." : "full range."));

            result.BindingsRead = result.Bindings.Count;
            return result;
        }

        private static GameInputBinding DecodeSlot(EliteElement element, EliteSlot slot, EliteDevices map, Dictionary<string, string> keys, List<string> warnings)
        {
            string device;
            string token;
            if (!DecodeInput(slot.Device, slot.Key, map, keys, out device, out token))
            {
                if (slot.Key.StartsWith("Pos_", StringComparison.Ordinal) || slot.Key.StartsWith("Neg_", StringComparison.Ordinal))
                    warnings.Add(element.Name + ": half-axis bind " + slot.Device + " " + slot.Key + " is not supported by JoyPro; skipped.");
                return null;
            }

            List<string> reformers = new List<string>();
            foreach (KeyValuePair<string, string> modifier in slot.Modifiers)
            {
                string modifierDevice;
                string modifierToken;
                if (!DecodeInput(modifier.Key, modifier.Value, map, keys, out modifierDevice, out modifierToken))
                {
                    warnings.Add(element.Name + ": modifier " + modifier.Key + " " + modifier.Value + " cannot be represented in JoyPro; bind skipped.");
                    return null;
                }
                reformers.Add(modifierToken + "§" + modifierDevice + "§" + modifierToken + "§False");
            }

            return new GameInputBinding
            {
                ActionId = element.Name,
                Device = device,
                Input = token,
                IsAxis = slot.Kind == "Binding",
                Reformers = reformers,
                Inverted = slot.Kind == "Binding" && ((element.Inverted.Trim() == "1") != NaturalInvert(element.Name)),
                Deadzone = slot.Kind == "Binding" ? Math.Max(0.0, Math.Min(1.0, ParseDouble(element.Deadzone))) : 0.0
            };
        }

        private static bool DecodeInput(string eliteDevice, string key, EliteDevices map, Dictionary<string, string> keys, out string device, out string token)
        {
            device = string.Empty;
            token = string.Empty;
            if (string.Equals(eliteDevice, "Keyboard", StringComparison.OrdinalIgnoreCase))
            {
                string name;
                if (!keys.TryGetValue(key, out name))
                    return false;
                device = "Keyboard";
                token = name;
                return true;
            }
            if (NonJoystickDevices.Contains(eliteDevice))
                return false;

            GameDevice joystick = map.Resolve(eliteDevice);
            if (joystick == null)
                return false;
            token = KeyToToken(key);
            if (token.Length == 0)
                return false;
            device = joystick.JoyProName;
            return true;
        }

        private static string KeyToToken(string key)
        {
            int axis = Array.IndexOf(AxisKeys, key);
            if (axis >= 0)
                return GameInputTokens.AxisToken(axis);
            Match pov = Regex.Match(key, @"^Joy_POV(\d)(Up|Right|Down|Left)$");
            if (pov.Success)
            {
                int hat = int.Parse(pov.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
                return GameInputTokens.PovToken(hat, Array.IndexOf(PovKeys, pov.Groups[2].Value));
            }
            Match button = Regex.Match(key, @"^Joy_(\d+)$");
            int number;
            if (button.Success && int.TryParse(button.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 1)
                return GameInputTokens.ButtonToken(number - 1);
            return string.Empty;
        }

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            EliteFile file;
            if (!EliteFile.TryLoad(request.ConfigPath, result.Errors, out file))
                return result;
            EliteDevices map = EliteDevices.Load(request.Devices);
            Dictionary<string, string> keys = EliteData.JoyProToKeys();

            // Galaxy map elements belong to the General preset; write them only when General and Ship share the file.
            string folder = Path.GetDirectoryName(request.ConfigPath) ?? string.Empty;
            string[] start = File.Exists(Path.Combine(folder, "StartPreset.4.start"))
                ? File.ReadAllText(Path.Combine(folder, "StartPreset.4.start")).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                : new string[0];
            bool generalInFile = start.Length < 2 || string.Equals(start[0].Trim(), start[1].Trim(), StringComparison.OrdinalIgnoreCase);

            Dictionary<string, List<EliteWanted>> wanted = new Dictionary<string, List<EliteWanted>>(StringComparer.Ordinal);
            HashSet<string> usedDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameInputBinding binding in request.Bindings)
            {
                GameAction action = request.Catalog.Find(binding.ActionId);
                if (!generalInFile && action != null && action.Category == "galaxy_map")
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): galaxy map controls live in the General preset, which is not the Ship preset file; skipped.");
                    continue;
                }
                EliteWanted want;
                string reason = Encode(binding, map, keys, out want);
                if (reason.Length > 0)
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
                    continue;
                }
                List<EliteWanted> list;
                if (!wanted.TryGetValue(binding.ActionId, out list))
                    wanted[binding.ActionId] = list = new List<EliteWanted>();
                if (!list.Any(w => w.SameInput(want)))
                    list.Add(want);
                if (want.Device != "Keyboard")
                    usedDevices.Add(want.Device);
            }

            HashSet<string> managed = new HashSet<string>(request.Catalog.Actions.Select(a => a.Id), StringComparer.Ordinal);
            int released = 0;
            int written = 0;
            foreach (EliteElement element in file.Elements.Where(e => managed.Contains(e.Name)))
            {
                List<EliteWanted> wants;
                wanted.TryGetValue(element.Name, out wants);
                wants = wants ?? new List<EliteWanted>();

                // Clean (devices the export writes) and nuke (joysticks it does not use) release ship flight slots
                // that are not part of the profile. Keyboard, mouse and pad slots are never released.
                foreach (EliteSlot slot in element.Slots)
                {
                    if (NonJoystickDevices.Contains(slot.Device) || wants.Any(w => w.Matches(slot)))
                        continue;
                    bool used = usedDevices.Contains(slot.Device);
                    // Half-axis binds and modifiers cannot live in a JoyPro profile, so clean would delete them for
                    // good; they stay (nuke, which the user picks for unused devices, still clears them).
                    if (used && request.Mode == GameExportMode.Clean && IsHalfAxis(slot))
                    {
                        result.Warnings.Add(element.Name + ": kept " + slot.Device + " " + slot.Key + " (half-axis binds and modifiers cannot be represented in JoyPro).");
                        continue;
                    }
                    if ((used && request.Mode == GameExportMode.Clean) || (!used && request.NukeUnusedDevices))
                    {
                        slot.Clear(element);
                        released++;
                    }
                }

                foreach (EliteWanted want in wants)
                {
                    bool fileInverted = want.Inverted != NaturalInvert(element.Name);
                    if (element.Slots.Any(s => want.Matches(s)))
                    {
                        if (want.IsAxis && element.SetAxisOptions(fileInverted, want.DeadzoneText))
                            written++;
                        continue;
                    }
                    EliteSlot target = PickSlot(element, want, request.Mode, usedDevices, result);
                    if (target == null)
                        continue;
                    target.Set(want);
                    if (want.IsAxis)
                        element.SetAxisOptions(fileInverted, want.DeadzoneText);
                    written++;
                }
            }

            WriteThrottleRange(file, wanted, result);
            ApplySettings(file, result);

            foreach (string id in wanted.Keys.Where(id => !file.Elements.Any(e => e.Name == id)))
            {
                result.BindingsSkipped += wanted[id].Count;
                result.Warnings.Add("'" + id + "' is not in " + Path.GetFileName(request.ConfigPath) + " (newer game version or another preset); skipped.");
            }

            result.BindingsWritten = written;
            if (!file.Changed)
            {
                result.Steps.Add(Path.GetFileName(request.ConfigPath) + " already matches the profile.");
                return result;
            }
            string summary = written.ToString(CultureInfo.InvariantCulture) + " slot(s) written, " + released.ToString(CultureInfo.InvariantCulture) + " released.";
            if (request.DryRun)
            {
                result.Steps.Add("Dry run: " + summary);
                return result;
            }

            GameExportSafety.BackupBeforeOverwrite(request.ConfigPath, result.Warnings);
            file.Save(request.ConfigPath);
            result.FilesWritten.Add(request.ConfigPath);
            result.Steps.Add(summary);
            WarnMissingDevices(file, map, result.Warnings);
            return result;
        }

        // Throttle Axis Range follows the relation that holds ThrottleAxis: one-sided (Slider) = forward only, centred =
        // full range with reverse.
        private static void WriteThrottleRange(EliteFile file, Dictionary<string, List<EliteWanted>> wanted, GameIoResult result)
        {
            EliteElement throttle = file.Elements.FirstOrDefault(e => e.Name == ThrottleAxis);
            List<EliteWanted> wants;
            if (throttle == null || !wanted.TryGetValue(ThrottleAxis, out wants))
                return;
            EliteWanted holder = wants.FirstOrDefault(w => w.IsAxis && throttle.Slots.Any(s => w.Matches(s)));
            if (holder == null)
                return;
            if (wants.Any(w => w.IsAxis && w.Slider != holder.Slider))
                result.Warnings.Add("ThrottleAxis: the throttle relations disagree on one-sided (Slider); Throttle Axis Range follows '" + holder.RelationName + "'.");
            if (!file.HasSetting(ThrottleRange))
            {
                result.Warnings.Add("The preset has no <ThrottleRange> setting; Throttle Axis Range was not written.");
                return;
            }
            string range = holder.Slider ? ForwardOnly : string.Empty;
            if (file.Setting(ThrottleRange) == range)
                return;
            file.SetSetting(ThrottleRange, range);
            result.Steps.Add("Throttle Axis Range: " + (holder.Slider ? "forward only (one-sided throttle relation)." : "full range (centred throttle relation)."));
        }

        // Game-wide options (engine ffb set --device * --game EliteDangerous) into the preset's value elements.
        private static void ApplySettings(EliteFile file, GameIoResult result)
        {
            Dictionary<string, double> values = FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, GameId_, null);
            foreach (KeyValuePair<string, double> value in values.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                string element;
                if (!EliteGameSettings.Elements.TryGetValue(value.Key, out element))
                    continue;
                if (!file.HasSetting(element))
                {
                    result.Warnings.Add("The preset has no <" + element + "> setting; " + value.Key + " was not written.");
                    continue;
                }
                if (EliteGameSettings.Same(value.Key, file.Setting(element), value.Value))
                    continue;
                string text = EliteGameSettings.Encode(value.Key, value.Value);
                file.SetSetting(element, text);
                result.Steps.Add(element + " = \"" + text + "\"");
            }
        }

        public Dictionary<string, double> ReadGameSettings(string configPath, List<string> warnings)
        {
            List<string> errors = new List<string>();
            EliteFile file;
            if (!EliteFile.TryLoad(configPath, errors, out file))
            {
                warnings?.AddRange(errors);
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }
            return ReadSettings(file);
        }

        private static Dictionary<string, double> ReadSettings(EliteFile file)
        {
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> entry in EliteGameSettings.Elements)
            {
                double? value = file.HasSetting(entry.Value) ? EliteGameSettings.Decode(entry.Key, file.Setting(entry.Value)) : null;
                if (value.HasValue)
                    values[entry.Key] = value.Value;
            }
            return values;
        }

        // Whether the profile holds game-wide settings for the game (an export without binds still writes them).
        public bool HasSettings()
        {
            return InternalDataManagement.FfbOverrides != null && FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, GameId, null).Count > 0;
        }

        // An export without Elite binds: only the game-wide options.
        public GameIoResult WriteSettings(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            EliteFile file;
            if (!EliteFile.TryLoad(request.ConfigPath, result.Errors, out file))
                return result;
            ApplySettings(file, result);
            if (!file.Changed)
            {
                result.Steps.Add(Path.GetFileName(request.ConfigPath) + ": the game settings already match.");
                return result;
            }
            if (request.DryRun)
            {
                result.Steps.Insert(0, "Dry run, would write:");
                return result;
            }
            GameExportSafety.BackupBeforeOverwrite(request.ConfigPath, result.Warnings);
            file.Save(request.ConfigPath);
            result.FilesWritten.Add(request.ConfigPath);
            return result;
        }

        // Buttons have two slots: an identical bind wins, then (overwrite modes) a slot of a joystick the export writes,
        // then the first free slot. Keyboard, mouse and other devices' slots are never overwritten.
        private static EliteSlot PickSlot(EliteElement element, EliteWanted want, GameExportMode mode, HashSet<string> usedDevices, GameIoResult result)
        {
            if (want.IsAxis)
            {
                EliteSlot binding = element.Slots.FirstOrDefault(s => s.Kind == "Binding");
                if (binding == null)
                {
                    result.Warnings.Add(element.Name + ": axis element has no <Binding>; skipped.");
                    return null;
                }
                bool free = binding.Device == NoDevice || binding.Device.Length == 0;
                bool joystick = !NonJoystickDevices.Contains(binding.Device);
                if (free || (joystick && (mode == GameExportMode.Clean || mode == GameExportMode.MergeOverwrite)))
                    return binding;
                result.ConflictsKept++;
                result.Warnings.Add(element.Name + ": kept the game's axis " + binding.Device + " " + binding.Key + " (" + ModeName(mode) + ").");
                return null;
            }

            List<EliteSlot> slots = element.Slots.Where(s => s.Kind != "Binding").ToList();
            // A conflict is the same action on the same device: merge keeps the game's bind of that device.
            if (mode == GameExportMode.Merge && want.Device != "Keyboard" && slots.Any(s => s.Device == want.Device))
            {
                result.ConflictsKept++;
                result.Warnings.Add(element.Name + ": kept the game's " + want.Device + " bind (merge mode).");
                return null;
            }
            EliteSlot target = null;
            if (want.Device != "Keyboard" && (mode == GameExportMode.Clean || mode == GameExportMode.MergeOverwrite))
                target = slots.FirstOrDefault(s => s.Device == want.Device);
            target = target ?? slots.FirstOrDefault(s => s.Device == NoDevice || s.Device.Length == 0);
            if (target == null)
                result.Warnings.Add(element.Name + ": both slots are taken by other devices or keys; " + want.Device + " " + want.Key + " skipped.");
            return target;
        }

        private static string ModeName(GameExportMode mode)
        {
            return mode == GameExportMode.Merge ? "merge mode" : mode == GameExportMode.Add ? "add mode" : "not a joystick";
        }

        private static string Encode(GameInputBinding binding, EliteDevices map, Dictionary<string, string> keys, out EliteWanted want)
        {
            AxisTuning tuning = binding.Tuning;
            bool platform = tuning != null && tuning.NativeOr("platformDeadzone", 0) >= 0.5;
            want = new EliteWanted
            {
                IsAxis = binding.IsAxis,
                Inverted = binding.Inverted,
                DeadzoneText = platform ? PlatformDeadzone : Math.Max(0.0, Math.Min(1.0, binding.Deadzone)).ToString("0.00000000", CultureInfo.InvariantCulture),
                Slider = tuning != null && tuning.Slider,
                RelationName = binding.RelationName ?? string.Empty
            };
            string device;
            string key;
            string reason = EncodeInput(binding.Device, binding.Input, binding.IsAxis, map, keys, out device, out key);
            if (reason.Length > 0)
                return reason;
            want.Device = device;
            want.Key = key;

            List<string> reformers = binding.Reformers ?? new List<string>();
            if (reformers.Count > 0 && binding.IsAxis)
                return "Elite axes take no modifiers.";
            if (reformers.Count > MaxModifiers)
                return "Elite takes at most 3 modifiers.";
            foreach (string reformer in reformers)
            {
                string[] parts = reformer.Split('§');
                if (parts.Length < 3)
                    return "unreadable modifier '" + reformer + "'.";
                string modifierDevice;
                string modifierKey;
                reason = EncodeInput(parts[1], parts[2], false, map, keys, out modifierDevice, out modifierKey);
                if (reason.Length > 0)
                    return "modifier: " + reason;
                want.Modifiers.Add(new KeyValuePair<string, string>(modifierDevice, modifierKey));
            }
            return string.Empty;
        }

        private static string EncodeInput(string joyProDevice, string input, bool axis, EliteDevices map, Dictionary<string, string> keys, out string device, out string key)
        {
            device = string.Empty;
            key = string.Empty;
            if (string.Equals((joyProDevice ?? string.Empty).Trim(), "Keyboard", StringComparison.OrdinalIgnoreCase))
            {
                if (axis)
                    return "an Elite axis cannot take a key.";
                if (!keys.TryGetValue((input ?? string.Empty).Trim(), out key))
                    return "key '" + input + "' has no Elite key name.";
                device = "Keyboard";
                return string.Empty;
            }

            string reason = map.EliteId(joyProDevice, out device);
            if (reason.Length > 0)
                return reason;
            JoyInput parsed;
            if (!GameInputTokens.TryParse(input, out parsed))
                return "unsupported input '" + input + "'.";
            if (axis != (parsed.Kind == JoyInputKind.Axis))
                return axis ? "'" + input + "' is not an axis." : "an Elite button element needs a button or hat.";
            switch (parsed.Kind)
            {
                case JoyInputKind.Axis:
                    if (parsed.Index >= AxisKeys.Length)
                        return "axis '" + input + "' does not exist in Elite.";
                    key = AxisKeys[parsed.Index];
                    return string.Empty;
                case JoyInputKind.Button:
                    key = "Joy_" + (parsed.Index + 1).ToString(CultureInfo.InvariantCulture);
                    return string.Empty;
                case JoyInputKind.Pov:
                    if (parsed.Index > 3 || PovKeys[parsed.Direction] == null)
                        return "Elite hats have four directions (no diagonals) and at most 4 hats.";
                    key = "Joy_POV" + (parsed.Index + 1).ToString(CultureInfo.InvariantCulture) + PovKeys[parsed.Direction];
                    return string.Empty;
                default:
                    return "unsupported input '" + input + "'.";
            }
        }

        private static bool IsHalfAxis(EliteSlot slot)
        {
            return IsHalfAxisKey(slot.Key) || slot.Modifiers.Any(m => IsHalfAxisKey(m.Value));
        }

        private static bool IsHalfAxisKey(string key)
        {
            return key.StartsWith("Pos_", StringComparison.Ordinal) || key.StartsWith("Neg_", StringComparison.Ordinal);
        }

        private static void WarnMissingDevices(EliteFile file, EliteDevices map, List<string> warnings)
        {
            foreach (string id in file.Elements.SelectMany(e => e.Slots.Select(s => s.Device).Concat(e.Slots.SelectMany(s => s.Modifiers.Select(m => m.Key))))
                .Where(d => !NonJoystickDevices.Contains(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (map.Resolve(id) == null && !map.IsAmbiguous(id))
                    warnings.Add("The preset uses device " + id + ", which is not connected: Elite ignores a preset with a missing device. " +
                                 "Connect it, exchange it in JoyPro (devices exchange) or export with --nuke-untouched-devices to clear its flight binds.");
            }
        }

        private static double ParseDouble(string value)
        {
            double parsed;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0.0;
        }

        // The game's binding list is packed, but every .binds file names the elements it binds: the stock presets in
        // <install>\Products\<product>\ControlSchemes and the presets the game wrote to Options\Bindings (those list
        // every element the game knows). Elements of a ship/SLF/galaxy map category that the catalog lacks are added
        // with a title made from the element name; known actions keep id, title and category.
        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            List<string> files = CatalogSourceFiles(sourcePath, result);
            if (files.Count == 0)
            {
                if (result.Errors.Count == 0)
                    result.Errors.Add("No Elite Dangerous .binds files were found. Pass the game folder, its ControlSchemes folder or a .binds file " +
                                      "(engine elite refresh-db <path>), or set the game folder (engine games install set EliteDangerous <folder>).");
                return null;
            }

            GameCatalog catalog = new GameCatalog { GameId = GameId, SourcePath = files[0] };
            if (current != null)
            {
                foreach (GameAction action in current.Actions)
                    catalog.Add(action);
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> added = new List<string>();
            int other = 0;
            int read = 0;
            foreach (string file in files)
            {
                XElement root;
                try
                {
                    root = XDocument.Load(file).Root;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add("Skipped " + file + ": " + ex.Message);
                    continue;
                }
                if (root == null)
                    continue;
                read++;
                foreach (XElement element in root.Elements())
                {
                    string id = element.Name.LocalName;
                    // Value settings (<ThrottleRange Value="" />) have no slot; an axis has <Binding>, a button <Primary>/<Secondary>.
                    bool axis = element.Element("Binding") != null;
                    if (!axis && element.Element("Primary") == null && element.Element("Secondary") == null)
                        continue;
                    if (!seen.Add(id))
                        continue;
                    string category = CatalogCategory(id);
                    if (category == null)
                    {
                        other++;
                        continue;
                    }
                    if (catalog.Contains(id))
                        continue;
                    catalog.Add(new GameAction { Id = id, Title = CatalogTitle(id, category), IsAxis = axis, Category = category });
                    added.Add(id);
                }
            }

            result.Steps.Add("Read " + seen.Count.ToString(CultureInfo.InvariantCulture) + " bindable element(s) from " + read.ToString(CultureInfo.InvariantCulture) +
                             " .binds file(s); " + other.ToString(CultureInfo.InvariantCulture) + " are not ship flight actions (SRV, on foot, camera, UI).");
            result.Steps.Add(added.Count == 0
                ? "No new ship flight elements."
                : "New ship flight element(s): " + string.Join(", ", added) + ".");
            return catalog;
        }

        // An explicit .binds file, or the .binds files of a folder (itself, ControlSchemes, Products\*\ControlSchemes);
        // without a path the installed game's stock presets and the presets in Options\Bindings.
        private static List<string> CatalogSourceFiles(string sourcePath, GameIoResult result)
        {
            List<string> files = new List<string>();
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                string path = sourcePath.Trim().Trim('"');
                if (File.Exists(path))
                    files.Add(path);
                else if (Directory.Exists(path))
                    files.AddRange(BindsFilesIn(path));
                else
                    result.Errors.Add("Not found: " + path);
                return files;
            }

            string userFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Frontier Developments", "Elite Dangerous", "Options", "Bindings");
            if (Directory.Exists(userFolder))
                files.AddRange(Directory.GetFiles(userFolder, "*.binds").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            GameInstall install = GameInstalls.Find(GameId_, result.Warnings);
            if (install != null && install.Installed && Directory.Exists(install.InstallPath))
                files.AddRange(BindsFilesIn(install.InstallPath));
            return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<string> BindsFilesIn(string folder)
        {
            List<string> folders = new List<string> { folder, Path.Combine(folder, "ControlSchemes") };
            string products = Path.Combine(folder, "Products");
            if (Directory.Exists(products))
                folders.AddRange(Directory.GetDirectories(products).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(p => Path.Combine(p, "ControlSchemes")));
            return folders.Where(Directory.Exists)
                .SelectMany(f => Directory.GetFiles(f, "*.binds").OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }

        // The catalog's categories (docs/games/elite-dangerous.md "Action catalog"); first match wins. Null = not a
        // ship, SLF or galaxy map action: SRV, on foot, settlement placement, camera suite, UI, mouse and misc
        // elements, and names no rule knows.
        private static readonly KeyValuePair<Regex, string>[] CategoryRules =
        {
            Rule(@"^Humanoid|_Humanoid$", null),
            Rule(@"_Buggy$|^Buggy|^(SteeringAxis|SteerLeftButton|SteerRightButton|ToggleDriveAssist|VerticalThrustersButton|AutoBreakBuggyButton" +
                 @"|HeadlightsBuggyButton|ToggleBuggyTurretButton|DriveSpeedAxis|IncreaseSpeedButtonMax|DecreaseSpeedButtonMax" +
                 @"|IncreaseSpeedButtonPartial|DecreaseSpeedButtonPartial|RecallDismissShip)$", null),
            Rule(@"^(PlaceSettlement|ChangeConstructionOption|RotateSettlement\w*|ExitSettlementPlacementCamera|MovePlacementCam\w*" +
                 @"|PitchPlacementCamera\w*|YawPlacementCamera\w*|PlacementCamSpeed\w*)$", null),
            Rule(@"^(PhotoCameraToggle|VanityCamera\w*|ToggleFreeCam|FreeCam\w*|MoveFreeCam\w*|ToggleReverseThrottleInputFreeCam|PitchCamera\w*" +
                 @"|YawCamera\w*|RollCamera\w*|ToggleRotationLock|FixCamera\w*|QuitCamera|ToggleAdvanceMode|FStop\w*)$", null),
            Rule(@"^Store|^CommanderCreator_", null),
            Rule(@"^(UI_\w+|Pause|FriendsMenu|GalnetAudio_\w+|HMDReset|MicrophoneMute|ShowPGScoreSummaryInput|MouseReset|BlockMouseDecay)$", null),
            Rule(@"^(Cam(Pitch|Yaw|Translate|Zoom)\w*|GalaxyMapHome)$", "galaxy_map"),
            Rule(@"^ExplorationFSS", "ship_fss"),
            Rule(@"^(ExplorationSAA|SAAThirdPerson)", "ship_dss"),
            Rule(@"^MultiCrew", "ship_multicrew"),
            Rule(@"^(Order\w+|OpenOrders)$", "ship_fighter_orders"),
            Rule(@"^HeadLook", "ship_headlook"),
            Rule(@"^(UIFocus|Focus(Left|Comms|Radar|Right)Panel|QuickCommsPanel|GalaxyMapOpen|SystemMapOpen|OpenCodexGoToDiscovery" +
                 @"|PlayerHUDModeToggle|CycleNextPanel|CyclePreviousPanel|CycleNextPage|CyclePreviousPage)$", "ship_modes"),
            Rule(@"^(IncreaseEnginesPower|IncreaseWeaponsPower|IncreaseSystemsPower|ResetPowerDistribution)$", "ship_power"),
            Rule(@"^(SelectTarget|CycleNextTarget|CyclePreviousTarget|SelectHighestThreat|CycleNextHostileTarget|CyclePreviousHostileTarget" +
                 @"|TargetWingman\d|SelectTargetsTarget|WingNavLock|CycleNextSubsystem|CyclePreviousSubsystem|TargetNextRouteSystem)$", "ship_targeting"),
            Rule(@"^(PrimaryFire|SecondaryFire|CycleFireGroupNext|CycleFireGroupPrevious|DeployHardpointToggle)$", "ship_weapons"),
            Rule(@"^(DeployHeatSink|FireChaffLauncher|ChargeECM|UseShieldCell|TriggerFieldNeutraliser|ToggleButtonUpInput)$", "ship_defence"),
            Rule(@"^(ShipSpotLightToggle|NightVisionToggle|LandingGearToggle|ToggleCargoScoop|EjectAllCargo|RadarRangeAxis|RadarIncreaseRange" +
                 @"|RadarDecreaseRange|OrbitLinesToggle|WeaponColourToggle|EngineColourToggle|TriggerColonisationModule)$", "ship_systems"),
            Rule(@"^(ToggleFlightAssist|UseBoostJuice|HyperSuperCombination|Supercruise|Hyperspace|DisableRotationCorrectToggle)$", "ship_drive"),
            Rule(@"_Landing$", "ship_flight_landing"),
            Rule(@"^(YawAxisRaw|YawLeftButton|YawRightButton|YawToRollButton|RollAxisRaw|RollLeftButton|RollRightButton|PitchAxisRaw" +
                 @"|PitchUpButton|PitchDownButton|LateralThrustRaw|LeftThrustButton|RightThrustButton|VerticalThrustRaw|UpThrustButton" +
                 @"|DownThrustButton|AheadThrust|ForwardThrustButton|BackwardThrustButton|UseAlternateFlightValuesToggle|\w+Alternate" +
                 @"|ThrottleAxis|ToggleReverseThrottleInput|ForwardKey|BackwardKey|SetSpeed\w+)$", "ship_flight")
        };

        private static KeyValuePair<Regex, string> Rule(string pattern, string category)
        {
            return new KeyValuePair<Regex, string>(new Regex(pattern, RegexOptions.CultureInvariant), category);
        }

        internal static string CatalogCategory(string id)
        {
            foreach (KeyValuePair<Regex, string> rule in CategoryRules)
            {
                if (rule.Key.IsMatch(id))
                    return rule.Value;
            }
            return null;
        }

        // "ExplorationFSSZoomIn" -> "FSS: Zoom In", "PitchAxis_Landing" -> "Pitch Axis (Landing)", like the catalog's titles.
        private static string CatalogTitle(string id, string category)
        {
            string name = id;
            string suffix = string.Empty;
            if (name.EndsWith("_Landing", StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - "_Landing".Length);
                suffix = " (Landing)";
            }
            else if (name.EndsWith("Alternate", StringComparison.Ordinal) && name.Length > "Alternate".Length)
            {
                name = name.Substring(0, name.Length - "Alternate".Length);
                suffix = " (Alternate)";
            }
            string prefix = string.Empty;
            string[] strip = new string[0];
            switch (category)
            {
                case "galaxy_map": prefix = "Galaxy Map: "; strip = new[] { "GalaxyMap", "Cam" }; break;
                case "ship_fss": prefix = "FSS: "; strip = new[] { "ExplorationFSS" }; break;
                case "ship_dss": prefix = "DSS: "; strip = new[] { "ExplorationSAA", "SAA" }; break;
                case "ship_multicrew": prefix = "Multicrew: "; strip = new[] { "MultiCrew" }; break;
                case "ship_fighter_orders": prefix = "Fighter: "; strip = new[] { "Order" }; break;
            }
            foreach (string head in strip)
            {
                if (name.StartsWith(head, StringComparison.Ordinal) && name.Length > head.Length)
                {
                    name = name.Substring(head.Length);
                    break;
                }
            }
            string words = Regex.Replace(name.Replace('_', ' '), @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
            words = Regex.Replace(words, @"\s+", " ").Trim();
            words = Regex.Replace(words, @"\bFov\b", "Field of View");
            foreach (string tail in new[] { " Raw", " Button" })
            {
                if (words.EndsWith(tail, StringComparison.Ordinal))
                    words = words.Substring(0, words.Length - tail.Length);
            }
            return prefix + (words.Length == 0 ? id : words) + suffix;
        }

        private sealed class EliteWanted
        {
            public string Device = string.Empty;
            public string Key = string.Empty;
            public bool IsAxis;
            // JoyPro's direction; the file gets it XOR the element's natural invert.
            public bool Inverted;
            public string DeadzoneText = "0.00000000";
            public bool Slider;
            public string RelationName = string.Empty;
            public List<KeyValuePair<string, string>> Modifiers = new List<KeyValuePair<string, string>>();

            public bool SameInput(EliteWanted other)
            {
                return other.Device == Device && other.Key == Key && SameModifiers(other.Modifiers);
            }

            public bool Matches(EliteSlot slot)
            {
                return string.Equals(slot.Device, Device, StringComparison.OrdinalIgnoreCase) && slot.Key == Key && SameModifiers(slot.Modifiers) &&
                       (slot.Kind == "Binding") == IsAxis;
            }

            private bool SameModifiers(List<KeyValuePair<string, string>> other)
            {
                return other.Count == Modifiers.Count &&
                       Modifiers.All(m => other.Any(o => string.Equals(o.Key, m.Key, StringComparison.OrdinalIgnoreCase) && o.Value == m.Value));
            }
        }

        // One <Binding>, <Primary> or <Secondary> slot with the lines it spans.
        private sealed class EliteSlot
        {
            public string Kind = string.Empty;
            public int StartLine;
            public int EndLine;
            public string Indent = string.Empty;
            public string ChildIndent = string.Empty;
            public string Device = string.Empty;
            public string Key = string.Empty;
            public string Extra = string.Empty;
            public List<KeyValuePair<string, string>> Modifiers = new List<KeyValuePair<string, string>>();
            public List<string> OtherChildren = new List<string>();
            public bool Dirty;

            public void Clear(EliteElement element)
            {
                Device = NoDevice;
                Key = string.Empty;
                Modifiers.Clear();
                OtherChildren.Clear();
                Dirty = true;
                if (Kind == "Binding")
                    element.SetAxisOptions(false, "0.00000000");
            }

            public void Set(EliteWanted want)
            {
                Device = want.Device;
                Key = want.Key;
                Modifiers = new List<KeyValuePair<string, string>>(want.Modifiers);
                Dirty = true;
            }

            public List<string> Render()
            {
                string open = Indent + "<" + Kind + " Device=\"" + Device + "\" Key=\"" + Key + "\"" + Extra;
                if (Modifiers.Count == 0 && OtherChildren.Count == 0)
                    return new List<string> { open + " />" };
                List<string> lines = new List<string> { open + ">" };
                foreach (KeyValuePair<string, string> modifier in Modifiers)
                    lines.Add(ChildIndent + "<Modifier Device=\"" + modifier.Key + "\" Key=\"" + modifier.Value + "\" />");
                lines.AddRange(OtherChildren);
                lines.Add(Indent + "</" + Kind + ">");
                return lines;
            }
        }

        private sealed class EliteElement
        {
            public string Name = string.Empty;
            public List<EliteSlot> Slots = new List<EliteSlot>();
            public int InvertedLine = -1;
            public int DeadzoneLine = -1;
            public string Inverted = "0";
            public string Deadzone = "0.00000000";
            public string InvertedIndent = string.Empty;
            public string DeadzoneIndent = string.Empty;
            public bool OptionsDirty;

            // inverted: the file's value; deadzoneValue: a number with 8 decimals or DefaultPlatformDeadzone. A wanted
            // dead zone of 0 keeps the game's DefaultPlatformDeadzone symbol (as before), equal numbers keep their text.
            public bool SetAxisOptions(bool inverted, string deadzoneValue)
            {
                string invertedValue = inverted ? "1" : "0";
                if (InvertedLine < 0 || DeadzoneLine < 0)
                    return false;
                bool sameDeadzone = deadzoneValue == PlatformDeadzone
                    ? Deadzone.Trim() == PlatformDeadzone
                    : Math.Abs(ParseDouble(Deadzone) - ParseDouble(deadzoneValue)) < 0.000001;
                if (Inverted.Trim() == invertedValue && sameDeadzone)
                    return false;
                if (sameDeadzone)
                    deadzoneValue = Deadzone;
                Inverted = invertedValue;
                Deadzone = deadzoneValue;
                OptionsDirty = true;
                return true;
            }
        }

        // The binds file as lines; top-level elements are tab-indented blocks under <Root>.
        private sealed class EliteFile
        {
            private List<string> lines;
            private string newline = "\r\n";
            private bool bom;
            private bool trailingNewline;

            public List<EliteElement> Elements { get; private set; }
            // Value settings under <Root> by name (first occurrence): line and value; and the ones the export changes.
            private Dictionary<string, KeyValuePair<int, string>> settings = new Dictionary<string, KeyValuePair<int, string>>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> changedSettings = new Dictionary<string, string>(StringComparer.Ordinal);

            public bool Changed
            {
                get { return changedSettings.Count > 0 || Elements.Any(e => e.OptionsDirty || e.Slots.Any(s => s.Dirty)); }
            }

            public bool HasSetting(string name)
            {
                return settings.ContainsKey(name);
            }

            public string Setting(string name)
            {
                string changed;
                if (changedSettings.TryGetValue(name, out changed))
                    return changed;
                KeyValuePair<int, string> setting;
                return settings.TryGetValue(name, out setting) ? setting.Value : null;
            }

            public void SetSetting(string name, string value)
            {
                KeyValuePair<int, string> setting;
                if (!settings.TryGetValue(name, out setting))
                    return;
                if (setting.Value == value)
                    changedSettings.Remove(name);
                else
                    changedSettings[name] = value;
            }

            public static bool TryLoad(string path, List<string> errors, out EliteFile file)
            {
                file = null;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    errors.Add("Elite Dangerous binds file not found: " + path);
                    return false;
                }
                byte[] bytes = File.ReadAllBytes(path);
                EliteFile result = new EliteFile();
                result.bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                string text = new UTF8Encoding(false).GetString(bytes, result.bom ? 3 : 0, bytes.Length - (result.bom ? 3 : 0));
                try
                {
                    XDocument document = XDocument.Parse(text);
                    if (document.Root == null || document.Root.Name.LocalName != "Root")
                    {
                        errors.Add("Not an Elite Dangerous binds file: " + path);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("Elite Dangerous binds file is not valid XML: " + ex.Message);
                    return false;
                }
                result.newline = text.Contains("\r\n") ? "\r\n" : "\n";
                result.trailingNewline = text.EndsWith("\n", StringComparison.Ordinal);
                string body = result.trailingNewline ? text.Substring(0, text.Length - result.newline.Length) : text;
                result.lines = body.Split(new[] { result.newline }, StringSplitOptions.None).ToList();
                result.Parse();
                file = result;
                return true;
            }

            private void Parse()
            {
                Elements = new List<EliteElement>();
                settings = new Dictionary<string, KeyValuePair<int, string>>(StringComparer.Ordinal);
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < lines.Count; i++)
                {
                    Match setting = SettingLine.Match(lines[i]);
                    if (setting.Success)
                    {
                        if (!settings.ContainsKey(setting.Groups[2].Value))
                            settings[setting.Groups[2].Value] = new KeyValuePair<int, string>(i, setting.Groups[3].Value);
                        continue;
                    }
                    Match open = Regex.Match(lines[i], @"^\t<([A-Za-z0-9_]+)>\s*$");
                    if (!open.Success)
                        continue;
                    string name = open.Groups[1].Value;
                    int end = lines.FindIndex(i + 1, l => Regex.IsMatch(l, @"^\t</" + Regex.Escape(name) + @">\s*$"));
                    if (end < 0)
                        break;
                    // The game uses the first of duplicate elements only.
                    if (seen.Add(name))
                        Elements.Add(ParseElement(name, i + 1, end - 1));
                    i = end;
                }
            }

            private EliteElement ParseElement(string name, int first, int last)
            {
                EliteElement element = new EliteElement { Name = name };
                for (int i = first; i <= last; i++)
                {
                    Match value = ValueLine.Match(lines[i]);
                    if (value.Success)
                    {
                        if (value.Groups[2].Value == "Inverted")
                        {
                            element.InvertedLine = i;
                            element.Inverted = value.Groups[3].Value;
                            element.InvertedIndent = value.Groups[1].Value;
                        }
                        else
                        {
                            element.DeadzoneLine = i;
                            element.Deadzone = value.Groups[3].Value;
                            element.DeadzoneIndent = value.Groups[1].Value;
                        }
                        continue;
                    }
                    Match slotMatch = SlotLine.Match(lines[i]);
                    if (!slotMatch.Success)
                        continue;
                    EliteSlot slot = new EliteSlot
                    {
                        Kind = slotMatch.Groups[2].Value,
                        StartLine = i,
                        EndLine = i,
                        Indent = slotMatch.Groups[1].Value,
                        Device = slotMatch.Groups[3].Value,
                        Key = slotMatch.Groups[4].Value,
                        Extra = slotMatch.Groups[5].Value.TrimEnd()
                    };
                    slot.ChildIndent = slot.Indent + "\t";
                    if (slotMatch.Groups[6].Value != "/")
                    {
                        int close = lines.FindIndex(i + 1, l => l.Trim() == "</" + slot.Kind + ">");
                        if (close < 0 || close > last)
                            continue;
                        for (int c = i + 1; c < close; c++)
                        {
                            Match modifier = ModifierLine.Match(lines[c]);
                            if (modifier.Success)
                            {
                                slot.ChildIndent = modifier.Groups[1].Value;
                                slot.Modifiers.Add(new KeyValuePair<string, string>(modifier.Groups[2].Value, modifier.Groups[3].Value));
                            }
                            else if (lines[c].Trim().Length > 0)
                            {
                                slot.OtherChildren.Add(lines[c]);
                            }
                        }
                        slot.EndLine = close;
                        i = close;
                    }
                    element.Slots.Add(slot);
                }
                return element;
            }

            public void Save(string path)
            {
                List<Tuple<int, int, List<string>>> edits = new List<Tuple<int, int, List<string>>>();
                foreach (EliteElement element in Elements)
                {
                    foreach (EliteSlot slot in element.Slots.Where(s => s.Dirty))
                        edits.Add(Tuple.Create(slot.StartLine, slot.EndLine, slot.Render()));
                    if (element.OptionsDirty)
                    {
                        edits.Add(Tuple.Create(element.InvertedLine, element.InvertedLine, new List<string> { element.InvertedIndent + "<Inverted Value=\"" + element.Inverted + "\" />" }));
                        edits.Add(Tuple.Create(element.DeadzoneLine, element.DeadzoneLine, new List<string> { element.DeadzoneIndent + "<Deadzone Value=\"" + element.Deadzone + "\" />" }));
                    }
                }
                foreach (KeyValuePair<string, string> setting in changedSettings)
                {
                    int line = settings[setting.Key].Key;
                    edits.Add(Tuple.Create(line, line, new List<string> { "\t<" + setting.Key + " Value=\"" + setting.Value + "\" />" }));
                }
                changedSettings.Clear();
                foreach (Tuple<int, int, List<string>> edit in edits.OrderByDescending(e => e.Item1))
                {
                    lines.RemoveRange(edit.Item1, edit.Item2 - edit.Item1 + 1);
                    lines.InsertRange(edit.Item1, edit.Item3);
                }

                string text = string.Join(newline, lines) + (trailingNewline ? newline : string.Empty);
                byte[] body = new UTF8Encoding(false).GetBytes(text);
                using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    if (bom)
                        stream.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                    stream.Write(body, 0, body.Length);
                }
                Parse();
            }
        }

        // Elite device ids (VID+PID or the symbolic names of the game's DeviceMappings.xml) <-> connected devices.
        private sealed class EliteDevices
        {
            private readonly Dictionary<string, List<int[]>> pairsByName = new Dictionary<string, List<int[]>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> nameByPair = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private GameDeviceDirectory devices;

            public static EliteDevices Load(GameDeviceDirectory devices)
            {
                EliteDevices map = new EliteDevices { devices = devices ?? new GameDeviceDirectory() };
                string path = DeviceMappingsFile();
                if (path == null)
                    return map;
                try
                {
                    XElement root = XDocument.Load(path).Root;
                    foreach (XElement entry in root == null ? Enumerable.Empty<XElement>() : root.Elements())
                    {
                        List<int[]> pairs = new List<int[]>();
                        AddPair(entry, pairs);
                        foreach (XElement alternative in entry.Elements("Alternative"))
                            AddPair(alternative, pairs);
                        string name = entry.Name.LocalName;
                        map.pairsByName[name] = pairs;
                        foreach (int[] pair in pairs)
                        {
                            string key = Key(pair[0], pair[1]);
                            if (!map.nameByPair.ContainsKey(key))
                                map.nameByPair[key] = name;
                        }
                    }
                }
                catch (Exception)
                {
                    // Without the table only plain VID+PID ids are resolved.
                }
                return map;
            }

            // The game's own DeviceMappings.xml (<install>\ControlSchemes or Products\<product>\ControlSchemes); JoyPro ships
            // no copy (docs/licensing-audit.md). A development DB copy (DB\EliteDangerous) is used when present.
            private static string DeviceMappingsFile()
            {
                string dbCopy = GameDataPaths.DbFile("EliteDangerous", "DeviceMappings.xml");
                if (File.Exists(dbCopy))
                    return dbCopy;
                try
                {
                    GameInstall install = GameInstalls.Find(GameId_, new List<string>());
                    if (install == null || !install.Installed || !Directory.Exists(install.InstallPath))
                        return null;
                    List<string> folders = new List<string> { Path.Combine(install.InstallPath, "ControlSchemes") };
                    string products = Path.Combine(install.InstallPath, "Products");
                    if (Directory.Exists(products))
                        folders.AddRange(Directory.GetDirectories(products).OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).Select(p => Path.Combine(p, "ControlSchemes")));
                    return folders.Select(f => Path.Combine(f, "DeviceMappings.xml")).FirstOrDefault(File.Exists);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            private static void AddPair(XElement element, List<int[]> pairs)
            {
                int vid;
                int pid;
                if (int.TryParse((string)element.Element("VID") ?? string.Empty, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid) &&
                    int.TryParse((string)element.Element("PID") ?? string.Empty, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid))
                    pairs.Add(new[] { vid, pid });
            }

            private static string Key(int vid, int pid)
            {
                return vid.ToString("X4", CultureInfo.InvariantCulture) + pid.ToString("X4", CultureInfo.InvariantCulture);
            }

            private List<int[]> Pairs(string eliteId)
            {
                List<int[]> pairs;
                if (pairsByName.TryGetValue(eliteId ?? string.Empty, out pairs))
                    return pairs;
                int vid;
                int pid;
                if ((eliteId ?? string.Empty).Length == 8 &&
                    int.TryParse(eliteId.Substring(0, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid) &&
                    int.TryParse(eliteId.Substring(4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid))
                    return new List<int[]> { new[] { vid, pid } };
                return new List<int[]>();
            }

            private List<GameDevice> Connected(string eliteId)
            {
                return Pairs(eliteId).SelectMany(p => devices.FindByVidPid(p[0], p[1])).Where(d => d.Connected).Distinct().ToList();
            }

            public GameDevice Resolve(string eliteId)
            {
                List<GameDevice> connected = Connected(eliteId);
                return connected.Count == 1 ? connected[0] : null;
            }

            public bool IsAmbiguous(string eliteId)
            {
                return Connected(eliteId).Count > 1;
            }

            public string EliteId(string joyProDevice, out string eliteId)
            {
                eliteId = string.Empty;
                GameDevice device = devices.FindByJoyProName(joyProDevice);
                if (device == null || !device.Connected)
                    return "device '" + joyProDevice + "' is not connected; Elite names devices by USB id, so JoyPro writes connected devices only.";
                if (!device.HasVidPid)
                    return "device '" + joyProDevice + "' has no USB vendor/product id.";
                if (devices.FindByVidPid(device.VendorId, device.ProductId).Count(d => d.Connected) > 1)
                    return "two identical devices (" + Key(device.VendorId, device.ProductId) + ") are connected; Elite cannot tell them apart.";
                string key = Key(device.VendorId, device.ProductId);
                string name;
                eliteId = nameByPair.TryGetValue(key, out name) ? name : key;
                return string.Empty;
            }
        }

        // TOOLS\Conversions\EliteKeys.tsv: JoyPro's table of Elite key names and JoyPro key names.
        private static class EliteData
        {
            public static Dictionary<string, string> KeysToJoyPro()
            {
                Dictionary<string, string> keys = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string[] parts in Rows())
                {
                    if (!keys.ContainsKey(parts[0]))
                        keys[parts[0]] = parts[1];
                }
                return keys;
            }

            public static Dictionary<string, string> JoyProToKeys()
            {
                Dictionary<string, string> keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string[] parts in Rows())
                {
                    if (!keys.ContainsKey(parts[1]))
                        keys[parts[1]] = parts[0];
                }
                return keys;
            }

            private static IEnumerable<string[]> Rows()
            {
                string path = Path.Combine(MainStructure.PROGPATH ?? string.Empty, "TOOLS", "Conversions", "EliteKeys.tsv");
                if (!File.Exists(path))
                    yield break;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (line.Length == 0 || line[0] == '#')
                        continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length >= 2)
                        yield return parts;
                }
            }
        }
    }
}
