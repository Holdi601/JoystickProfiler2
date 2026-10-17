using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // Arma Reforger joystick presets (Enfusion text, see docs/games/arma-reforger.md). JoyPro writes its own preset
    // file in customInputConfigs, which the user selects once as "Joystick preset"; InputUserSettings.conf is never
    // touched. In any preset file only the managed helicopter Action blocks (and the catalog's refreshed flight actions,
    // ReforgerActionSpec) are replaced; all other blocks stay byte-identical. Axis sources carry the bind's filter (InputFilterValue Multiplier/UseInvert, axis model
    // "reforger"). Next to the preset (profile\.save\app<id>_user<id>\settings): the engine settings
    // (ReforgerEngineSettings.conf, older builds EngineUserSettings.conf) get a JoystickOrderRule per exported device
    // (device name -> joystickN slot) and the game settings (ReforgerGameSettings.conf / GameUserSettings.conf) the
    // values of FfbModels\ArmaReforger.cs. Both settings files are only edited where their structure is already there.
    internal sealed class ArmaReforgerIntegration : IGameIntegration, IGameSettingsExport, IGameSettingsReader, IGameSettingsSource
    {
        private const string PresetFileName = "JoyPro.conf";
        private const int SlotCount = 4;
        private static readonly Regex InputRegex = new Regex(@"^joystick(?<slot>\d+):(?<input>.+)$", RegexOptions.Compiled);

        private sealed class ActionPreset
        {
            public string Preset;
            public string FilterClass;
        }

        // Filter presets of the vanilla joystick presets shipped with the game.
        private static readonly Dictionary<string, ActionPreset> ButtonPresets = new Dictionary<string, ActionPreset>(StringComparer.OrdinalIgnoreCase)
        {
            { "HelicopterWheelBrake", new ActionPreset { Preset = "pressed" } },
            { "HelicopterWheelBrakePersistent", new ActionPreset { Preset = "pressed" } },
            { "HelicopterAutohoverToggle", new ActionPreset { Preset = "click" } },
            { "HelicopterLightsTaxiToggle", new ActionPreset { Preset = "toggle", FilterClass = "InputFilterDown" } },
            { "HelicopterLightsLandingToggle", new ActionPreset { Preset = "toggle", FilterClass = "InputFilterDown" } },
            { "HelicopterEngineStart", new ActionPreset { Preset = "hold", FilterClass = "InputFilterHold" } },
            { "HelicopterEngineStop", new ActionPreset { Preset = "click", FilterClass = "InputFilterHoldOnce" } }
        };

        // Composite axes: action and preset of the negative half, then of the positive half.
        private static readonly Dictionary<string, string[]> CompositeAxes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Axis.HelicopterCyclicPitch", new[] { "HelicopterCyclicForward", "forward", "HelicopterCyclicBack", "back" } },
            { "Axis.HelicopterCyclicRoll", new[] { "HelicopterCyclicLeft", "left", "HelicopterCyclicRight", "right" } },
            { "Axis.HelicopterCollective", new[] { "HelicopterCollectiveIncrease", "up", "HelicopterCollectiveDecrease", "down" } },
            { "Axis.HelicopterAntiTorque", new[] { "HelicopterAntiTorqueLeft", "left", "HelicopterAntiTorqueRight", "right" } }
        };

        private static readonly string[] PovNames = { "pov_up", null, "pov_right", null, "pov_down", null, "pov_left", null };

        // The composite axis id a half-axis action belongs to (HelicopterCyclicForward -> Axis.HelicopterCyclicPitch), or null.
        internal static string CompositeOf(string action)
        {
            return CompositeAxes.Where(c => string.Equals(c.Value[0], action, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Value[2], action, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Key)
                .FirstOrDefault();
        }

        // Catalog actions without a hand-made rule (added by refresh-db): written and read through their ReforgerActionSpec.
        private static List<GameAction> GenericActions(GameCatalog catalog)
        {
            if (catalog == null)
                return new List<GameAction>();
            return catalog.Actions.Where(a => !CompositeAxes.ContainsKey(a.Id) && !ButtonPresets.ContainsKey(a.Id) && CompositeOf(a.Id) == null).ToList();
        }

        public string GameId { get { return "ArmaReforger"; } }
        public string DisplayName { get { return "Arma Reforger"; } }
        public IReadOnlyList<string> Aliases { get { return new[] { "reforger", "armareforger", "ar" }; } }
        public string DbFolder { get { return "ArmaReforger"; } }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return false; } }
        public string Notes { get { return "Experimental. JoyPro writes its own joystick preset (customInputConfigs\\JoyPro.conf): select it once in Settings > Controllers > Joystick preset and restart the game. joystickN slots come from the game's device order rules, which JoyPro writes to the game's engine settings (ReforgerEngineSettings.conf) when the game has saved its settings once (choose a device per slot with the joystickSlot game setting). The game uses only 4 joysticks: the first that send input after its start; hide the others (e.g. HidHide) while playing. The preset's binds are not shown in Settings > Controls (1.9)."; } }
        public IReadOnlyList<string> ProcessNames { get { return new[] { "ArmaReforgerSteam", "ArmaReforger_BE", "ArmaReforger" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        public GameFeature KeyboardBinds { get { return GameFeature.No; } }
        public GameFeature ModifierBinds { get { return GameFeature.No; } }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            List<string> paths = new List<string>();
            string presets = PresetFolder();
            if (Directory.Exists(Path.GetDirectoryName(presets)))
                paths.Add(Path.Combine(presets, PresetFileName));
            return paths;
        }

        private static string PresetFolder()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(documents, "My Games", "ArmaReforger", "profile", ".save", "settings", "customInputConfigs");
        }

        // The game's settings folders next to a preset: profile\.save\app<id>_user<id>\settings (stable and
        // experimental each have one), newest first.
        private static List<string> SettingsFolders(string presetPath)
        {
            List<string> folders = new List<string>();
            try
            {
                string presetDir = Path.GetDirectoryName(Path.GetFullPath(presetPath)) ?? string.Empty;
                DirectoryInfo save = Directory.GetParent(presetDir) == null ? null : Directory.GetParent(presetDir).Parent;
                if (save == null || !save.Exists || !string.Equals(save.Name, ".save", StringComparison.OrdinalIgnoreCase))
                    return folders;
                foreach (DirectoryInfo app in save.GetDirectories("app*_user*").OrderByDescending(d => d.LastWriteTimeUtc))
                {
                    string settings = Path.Combine(app.FullName, "settings");
                    if (Directory.Exists(settings))
                        folders.Add(settings);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return folders;
        }

        // The settings files of a settings folder: 1.9 (Steam, checked 2026-10-05) names them Reforger*Settings.conf,
        // older builds *UserSettings.conf; the content is the same (root class GameUserSettings / EngineUserSettings).
        private static string EngineSettingsFile(string folder)
        {
            return FirstExisting(folder, "ReforgerEngineSettings.conf", "EngineUserSettings.conf");
        }

        private static string GameSettingsFile(string folder)
        {
            return FirstExisting(folder, "ReforgerGameSettings.conf", "GameUserSettings.conf");
        }

        private static string FirstExisting(string folder, params string[] names)
        {
            return names.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists);
        }

        // Device -> joystickN slot. From the order rules of the first engine settings file that has them, else the
        // DirectInput enumeration order (what the game does without rules).
        private sealed class SlotMap
        {
            public List<ReforgerOrderRule> Rules = new List<ReforgerOrderRule>();
            public bool FromRules;

            public GameDevice DeviceForSlot(int slot, GameDeviceDirectory devices)
            {
                if (devices == null)
                    return null;
                if (FromRules)
                {
                    ReforgerOrderRule rule = Rules.FirstOrDefault(r => r.Slot == slot);
                    if (rule != null)
                        return devices.Devices.FirstOrDefault(d => RuleMatches(rule.NameContains, d));
                }
                List<GameDevice> sorted = GameOrder(devices.Devices.Where(d => d.Connected || d.EnumerationIndex >= 0));
                return slot >= 0 && slot < sorted.Count ? sorted[slot] : null;
            }
        }

        // The game's own device order (ArmaReforgerSteam.exe 1.9, WinInputDevice): joysticks are taken from raw input
        // (UsagePage 1 / Usage 4 only, no device notification), a device joins when it first sends input while the game
        // window is focused, up to 4; the list is kept sorted by (usage page, usage, VID, PID) and every further new
        // device overwrites entry 3. Without order rules entry i is joystick i, so the VID/PID order is the best guess.
        private static List<GameDevice> GameOrder(IEnumerable<GameDevice> devices)
        {
            return devices.OrderBy(d => d.HasVidPid ? d.VendorId : int.MaxValue).ThenBy(d => d.ProductId).ThenBy(d => d.EnumerationIndex).ToList();
        }

        // The name an order rule matches: Reforger names a device "<HID manufacturer> <HID product>" (1.9, seen in game:
        // "Gudsen MOZA R5 Base", "ATMEL/VIRPIL/200325 LEFT VPC Throttle MT-50 CM"), so JoyPro's rule holds the HID product
        // string, which that name contains; the DirectInput name differs for some devices ("vJoy Device" is
        // "vJoy - Virtual Joystick" over HID).
        private static string RuleName(GameDevice device)
        {
            return string.IsNullOrWhiteSpace(device.HidProductString) ? ProductName(device) : device.HidProductString.Trim();
        }

        // A rule written by the game holds its full name, one written by JoyPro the HID product: either contains the other.
        private static bool RuleMatches(string ruleName, GameDevice device)
        {
            string name = RuleName(device);
            return ruleName.Length > 0 && (name.IndexOf(ruleName, StringComparison.Ordinal) >= 0 || ruleName.IndexOf(name, StringComparison.Ordinal) >= 0);
        }

        private static string ProductName(GameDevice device)
        {
            string name;
            Guid guid;
            return GameDeviceDirectory.TryParseJoyProName(device.JoyProName, out name, out guid) ? name : device.JoyProName;
        }

        private static SlotMap LoadSlots(string presetPath, List<string> warnings)
        {
            SlotMap map = new SlotMap();
            foreach (string folder in SettingsFolders(presetPath))
            {
                ReforgerEngineSettings settings;
                string path = EngineSettingsFile(folder);
                if (path != null && ReforgerEngineSettings.TryLoad(path, warnings, out settings) && settings.Rules.Count > 0)
                {
                    map.Rules = settings.Rules;
                    map.FromRules = true;
                    break;
                }
            }
            return map;
        }

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            EnfusionActionFile file;
            if (!EnfusionActionFile.TryLoad(configPath, result.Errors, out file))
                return result;
            SlotMap slots = LoadSlots(configPath, result.Warnings);

            foreach (KeyValuePair<string, string[]> composite in CompositeAxes)
            {
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int half = 0; half < 2; half++)
                {
                    foreach (string[] source in file.ReadSources(composite.Value[half * 2]))
                    {
                        int slot;
                        string token;
                        if (!TryParseInput(source[1], out slot, out token) || !token.StartsWith("axis", StringComparison.OrdinalIgnoreCase))
                            continue;
                        int axis;
                        bool negative = token.EndsWith("-", StringComparison.Ordinal);
                        if (!int.TryParse(token.Substring(4).TrimEnd('+', '-'), NumberStyles.Integer, CultureInfo.InvariantCulture, out axis))
                            continue;
                        string device = DeviceForSlot(slots, slot, devices, result.Warnings);
                        if (device.Length == 0 || !seen.Add(device + "|" + axis.ToString(CultureInfo.InvariantCulture)))
                            continue;
                        bool inverted = negative ? half == 1 : half == 0;
                        GameInputBinding binding = new GameInputBinding
                        {
                            ActionId = composite.Key,
                            Device = device,
                            Input = GameInputTokens.AxisToken(axis),
                            IsAxis = true,
                            Inverted = inverted
                        };
                        AxisTuning tuning = FilterTuning(source, inverted);
                        if (tuning != null)
                            binding.Tuning = tuning;
                        result.Bindings.Add(binding);
                    }
                }
            }

            foreach (string action in ButtonPresets.Keys)
            {
                foreach (string input in file.Inputs(action))
                {
                    int slot;
                    string token;
                    if (!TryParseInput(input, out slot, out token))
                        continue;
                    string joyToken = ToJoyProButtonToken(token);
                    string device = joyToken.Length == 0 ? string.Empty : DeviceForSlot(slots, slot, devices, result.Warnings);
                    if (device.Length == 0)
                        continue;
                    result.Bindings.Add(new GameInputBinding { ActionId = action, Device = device, Input = joyToken, IsAxis = false });
                }
            }

            // Actions added by refresh-db: a source belongs to the row with its preset when an action has several rows
            // (sight range up / down), else to the action's only row.
            foreach (IGrouping<string, GameAction> rows in GenericActions(catalog).GroupBy(a => ReforgerActionSpec.Of(a).Action, StringComparer.OrdinalIgnoreCase))
            {
                foreach (string[] source in file.ReadSources(rows.Key))
                {
                    GameAction row = rows.Count() == 1 ? rows.First() : rows.FirstOrDefault(r => string.Equals(ReforgerActionSpec.Of(r).Preset, source[0], StringComparison.OrdinalIgnoreCase));
                    int slot;
                    string token;
                    if (row == null || !TryParseInput(source[1], out slot, out token))
                        continue;
                    string joyToken;
                    int axis;
                    if (row.IsAxis)
                        joyToken = token.StartsWith("axis", StringComparison.OrdinalIgnoreCase) && int.TryParse(token.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out axis) ? GameInputTokens.AxisToken(axis) : string.Empty;
                    else
                        joyToken = ToJoyProButtonToken(token);
                    string device = joyToken.Length == 0 ? string.Empty : DeviceForSlot(slots, slot, devices, result.Warnings);
                    if (device.Length == 0)
                        continue;
                    GameInputBinding binding = new GameInputBinding { ActionId = row.Id, Device = device, Input = joyToken, IsAxis = row.IsAxis };
                    if (row.IsAxis)
                    {
                        // A whole axis is inverted by a negative multiplier.
                        AxisTuning tuning = FilterTuning(source, false);
                        double multiplier = tuning == null ? 1 : tuning.NativeOr("multiplier", 1);
                        binding.Inverted = multiplier < 0;
                        if (tuning != null)
                        {
                            tuning.Inverted = binding.Inverted;
                            tuning.Native["multiplier"] = Math.Abs(multiplier);
                            if (Math.Abs(Math.Abs(multiplier) - 1) < 1e-9)
                                tuning.Native.Remove("multiplier");
                            if (tuning.Native.Count > 0)
                                binding.Tuning = tuning;
                        }
                    }
                    result.Bindings.Add(binding);
                }
            }

            return result;
        }

        // The InputFilterValue of an axis source as the bind's tuning (null without one).
        private static AxisTuning FilterTuning(string[] source, bool inverted)
        {
            if (!string.Equals(source[2], "InputFilterValue", StringComparison.Ordinal) || string.IsNullOrEmpty(source[3]))
                return null;
            AxisTuning tuning = new AxisTuning { Inverted = inverted };
            foreach (KeyValuePair<string, string> parameter in FilterParameters(source[3]))
            {
                double value;
                if (!double.TryParse(parameter.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    continue;
                if (parameter.Key == "Multiplier")
                    tuning.Native["multiplier"] = value;
                else if (parameter.Key == "UseInvert")
                    tuning.Native["useInvert"] = value != 0 ? 1 : 0;
            }
            return tuning.Native.Count == 0 ? null : tuning;
        }

        private static IEnumerable<KeyValuePair<string, string>> FilterParameters(string normalized)
        {
            foreach (string part in (normalized ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int space = part.IndexOf(' ');
                if (space > 0)
                    yield return new KeyValuePair<string, string>(part.Substring(0, space), part.Substring(space + 1));
            }
        }

        // The filter an axis bind gets: InputFilterValue with the non-default Multiplier / UseInvert, else none.
        private static string AxisFilter(AxisTuning tuning)
        {
            if (tuning == null)
                return string.Empty;
            List<string> parts = new List<string>();
            double multiplier = tuning.NativeOr("multiplier", 1);
            if (Math.Abs(multiplier - 1) > 1e-9)
                parts.Add("Multiplier " + multiplier.ToString("0.######", CultureInfo.InvariantCulture));
            if (tuning.NativeOr("useInvert", 0) != 0)
                parts.Add("UseInvert 1");
            return string.Join(";", parts);
        }

        // Whether the profile holds settings for the game (device or game-wide); an export without binds still writes
        // them through Write with SettingsOnly, which touches no bind.
        public bool HasSettings()
        {
            return InternalDataManagement.FfbOverrides != null && JoyPro.Engine.Axis.FfbSettings.DevicesFor(InternalDataManagement.FfbOverrides, GameId).Any();
        }

        public GameIoResult WriteSettings(GameExportRequest request)
        {
            request.SettingsOnly = true;
            return Write(request);
        }

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            if (!request.SettingsOnly)
                WritePreset(request, result);
            if (result.Errors.Count == 0)
                WriteGameSettings(request, result);
            return result;
        }

        private void WritePreset(GameExportRequest request, GameIoResult result)
        {
            EnfusionActionFile file;
            if (File.Exists(request.ConfigPath))
            {
                if (!EnfusionActionFile.TryLoad(request.ConfigPath, result.Errors, out file))
                    return;
            }
            else
            {
                file = EnfusionActionFile.CreateEmpty();
                result.Steps.Add("Creating " + request.ConfigPath + ".");
            }

            // joystickN of each exported device, and the order rules that pin it.
            Dictionary<string, int> slots = AssignSlots(request, result);

            // Generated sources per action: (preset, input, filter class, filter parameters).
            Dictionary<string, List<string[]>> sources = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
            foreach (GameInputBinding binding in request.Bindings)
            {
                string reason = AddSources(binding, request.Devices, request.Catalog, slots, sources);
                if (reason.Length > 0)
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
                    continue;
                }
                result.BindingsWritten++;
            }

            HashSet<string> usedDevices = new HashSet<string>(request.Bindings.Select(b => b.Device.Trim()), StringComparer.OrdinalIgnoreCase);
            SlotMap readSlots = LoadSlots(request.ConfigPath, new List<string>());
            List<string> managed = CompositeAxes.Values.SelectMany(v => new[] { v[0], v[2] })
                .Concat(ButtonPresets.Keys)
                .Concat(GenericActions(request.Catalog).Select(a => ReforgerActionSpec.Of(a).Action))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string action in managed)
            {
                List<string[]> actionSources;
                sources.TryGetValue(action, out actionSources);
                bool exists = file.HasAction(action);
                if (request.Mode == GameExportMode.Merge && exists)
                    continue;
                if (actionSources == null || actionSources.Count == 0)
                {
                    // Only clean removes managed actions that the profile does not bind; nuke drops the sources of
                    // devices the export does not use.
                    if (request.Mode == GameExportMode.Clean && exists)
                        file.RemoveAction(action);
                    else if (request.NukeUnusedDevices && exists)
                        DropUnusedDeviceSources(file, action, usedDevices, request.Devices, readSlots);
                    continue;
                }

                List<string[]> existing = file.ReadSources(action);
                List<string[]> merged = actionSources;
                if (request.Mode == GameExportMode.Add && exists)
                    merged = existing.Concat(actionSources.Where(s => !existing.Any(e => e[1] == s[1]))).ToList();

                // Keep the existing block (and its ids) when it already holds exactly these sources.
                if (exists && SameSources(existing, merged))
                    continue;
                file.SetAction(action, BuildActionBlock(action, merged, file.ActionIndent));
            }

            if (request.DryRun)
            {
                result.Steps.Add("Dry run: " + file.ChangedActionCount.ToString(CultureInfo.InvariantCulture) + " action block(s) would change.");
                return;
            }
            if (file.ChangedActionCount == 0)
            {
                result.Steps.Add("The preset already matches.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(request.ConfigPath) ?? string.Empty);
            GameExportSafety.BackupBeforeOverwrite(request.ConfigPath, result.Warnings);
            file.Save(request.ConfigPath);
            result.FilesWritten.Add(request.ConfigPath);
            result.Steps.Add(file.ChangedActionCount.ToString(CultureInfo.InvariantCulture) + " action block(s) changed.");
        }

        // Slots for the exported devices. Arma Reforger (1.9, checked in game and in ArmaReforgerSteam.exe) keeps 4
        // joysticks: the first HID joysticks (Usage 4) that send input while the game is focused, any later one replaces
        // the 4th. Order rules only sort those 4 (DeviceNameContains against "<HID manufacturer> <HID product>"); the
        // rule's slot indexes a 4-entry table unchecked, so JoyPro writes 0..3 only. With an engine settings file JoyPro pins devices by order rules:
        // first the devices with a chosen slot (ffb set --device <dev> --game ArmaReforger --param joystickSlot=<0..3>),
        // then each exported device keeps the slot an existing rule gives it, else gets the lowest slot no rule claims.
        // Without that file joystickN follows the DirectInput order.
        private Dictionary<string, int> AssignSlots(GameExportRequest request, GameIoResult result)
        {
            Dictionary<string, int> slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<GameDevice> used = request.Bindings
                .Where(b => !b.IsKeyboard)
                .Select(b => request.Devices == null ? null : request.Devices.FindByJoyProName(b.Device))
                .Where(d => d != null)
                .Distinct()
                .OrderBy(d => d.EnumerationIndex)
                .ToList();
            List<KeyValuePair<GameDevice, int>> chosen = ChosenSlots(request.Devices, result.Warnings);
            if (used.Union(chosen.Select(c => c.Key)).Distinct().Count() > SlotCount)
                result.Warnings.Add("Arma Reforger uses only " + SlotCount.ToString(CultureInfo.InvariantCulture) + " joysticks (1.9): the first ones that send input after the start, any further one takes over the fourth. Of the devices this export uses or places, the ones without a slot do nothing; choose the four with joystickSlot and disconnect (or hide) other joysticks while playing.");

            List<string> confs = SettingsFolders(request.ConfigPath)
                .Select(EngineSettingsFile)
                .Where(f => f != null)
                .ToList();
            List<ReforgerEngineSettings> files = new List<ReforgerEngineSettings>();
            foreach (string conf in confs)
            {
                ReforgerEngineSettings settings;
                if (ReforgerEngineSettings.TryLoad(conf, result.Warnings, out settings))
                    files.Add(settings);
            }
            ReforgerEngineSettings writable = files.FirstOrDefault(f => f.CanHoldRules);
            if (writable == null)
            {
                if (confs.Count > 0)
                    result.Warnings.Add(Path.GetFileName(confs[0]) + " has no InputDeviceUserSettings section JoyPro knows; device order rules not written, joystickN follows the DirectInput order.");
                else if (chosen.Count > 0)
                    result.Warnings.Add("The chosen joystick slots need the game's ReforgerEngineSettings.conf: start Arma Reforger once and open Settings > Controllers.");
                List<GameDevice> order = GameOrder(used);
                for (int i = 0; i < order.Count && i < SlotCount; i++)
                    slots[order[i].JoyProName] = i;
                result.Warnings.Add("Without order rules Arma Reforger numbers the joysticks it detected by VID/PID; with other joysticks sending input they shift. Start the game once and open Settings > Controllers so JoyPro can pin them.");
                return slots;
            }

            List<ReforgerOrderRule> rules = writable.Rules;
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<GameDevice, int> pin in chosen)
            {
                if (!names.Add(RuleName(pin.Key)))
                    continue;
                if (slots.ContainsValue(pin.Value))
                {
                    result.Warnings.Add("Two devices have joystickSlot " + pin.Value.ToString(CultureInfo.InvariantCulture) + " for Arma Reforger; '" + pin.Key.JoyProName + "' gets no slot.");
                    continue;
                }
                slots[pin.Key.JoyProName] = pin.Value;
            }
            List<GameDevice> pending = new List<GameDevice>();
            foreach (GameDevice device in used.Where(d => !slots.ContainsKey(d.JoyProName)))
            {
                string name = RuleName(device);
                if (!names.Add(name))
                {
                    result.Warnings.Add("Two exported devices are named '" + name + "'; Arma Reforger tells devices apart by name only, so '" + device.JoyProName + "' gets no slot.");
                    continue;
                }
                ReforgerOrderRule rule = rules.FirstOrDefault(r => r.Slot >= 0 && r.Slot < SlotCount && RuleMatches(r.NameContains, device) && !slots.ContainsValue(r.Slot));
                if (rule != null)
                    slots[device.JoyProName] = rule.Slot;
                else
                    pending.Add(device);
            }
            foreach (GameDevice device in pending)
            {
                // A slot no other device's rule holds (a rule of the device itself may be moved).
                int free = FirstOrDefault(Enumerable.Range(0, SlotCount), slot => !slots.ContainsValue(slot) && !rules.Any(r => r.Slot == slot && !RuleMatches(r.NameContains, device)), -1);
                if (free < 0)
                {
                    result.Warnings.Add("'" + device.JoyProName + "' gets no Arma Reforger joystick slot: all " + SlotCount.ToString(CultureInfo.InvariantCulture) + " are taken (choose them with joystickSlot); its binds are written but do nothing.");
                    continue;
                }
                slots[device.JoyProName] = free;
            }
            // A chosen slot another device's rule holds as well: that rule would compete, so it is reported.
            foreach (KeyValuePair<string, int> slot in slots)
            {
                GameDevice device = request.Devices.FindByJoyProName(slot.Key);
                foreach (ReforgerOrderRule other in rules.Where(r => r.Slot == slot.Value && !RuleMatches(r.NameContains, device)))
                    result.Warnings.Add("The game's order rule for '" + other.NameContains + "' also has slot " + slot.Value.ToString(CultureInfo.InvariantCulture) + "; change it in Settings > Controllers or give that device a joystickSlot.");
            }

            Dictionary<string, int> byName = slots.ToDictionary(s => RuleName(request.Devices.FindByJoyProName(s.Key)), s => s.Value, StringComparer.Ordinal);
            foreach (ReforgerEngineSettings settings in files)
            {
                List<string> changes = new List<string>();
                if (!settings.SetRules(byName, changes, result.Warnings))
                    continue;
                if (request.DryRun)
                {
                    result.Steps.AddRange(changes.Select(c => "Would set " + c + " in " + settings.Path));
                    continue;
                }
                GameExportSafety.BackupBeforeOverwrite(settings.Path, result.Warnings);
                settings.Save();
                result.FilesWritten.Add(settings.Path);
                result.Steps.AddRange(changes.Select(c => "Set " + c + " in " + settings.Path));
            }
            return slots;
        }

        private static int FirstOrDefault(IEnumerable<int> values, Func<int, bool> predicate, int fallback)
        {
            foreach (int value in values)
            {
                if (predicate(value))
                    return value;
            }
            return fallback;
        }

        // Devices with a slot chosen for Arma Reforger (per device game setting joystickSlot; -1 or unset = automatic).
        private List<KeyValuePair<GameDevice, int>> ChosenSlots(GameDeviceDirectory devices, List<string> warnings)
        {
            List<KeyValuePair<GameDevice, int>> chosen = new List<KeyValuePair<GameDevice, int>>();
            if (devices == null)
                return chosen;
            foreach (GameDevice device in devices.Devices.OrderBy(d => d.JoyProName, StringComparer.OrdinalIgnoreCase))
            {
                double value;
                if (!FfbSettings.Resolve(InternalDataManagement.FfbOverrides, device.JoyProName, GameId, null).TryGetValue(ArmaReforgerSettingsModel.JoystickSlot, out value) || value < 0)
                    continue;
                int slot = (int)Math.Round(value);
                if (slot >= SlotCount)
                {
                    warnings.Add("joystickSlot " + slot.ToString(CultureInfo.InvariantCulture) + " of '" + device.JoyProName + "' ignored: Arma Reforger has slots 0 to " + (SlotCount - 1).ToString(CultureInfo.InvariantCulture) + " only.");
                    continue;
                }
                chosen.Add(new KeyValuePair<GameDevice, int>(device, slot));
            }
            return chosen.OrderBy(c => c.Value).ToList();
        }

        // Settings > Gameplay HOTAS collective and (1.9) force feedback intensity in the game settings file.
        private void WriteGameSettings(GameExportRequest request, GameIoResult result)
        {
            Dictionary<string, double> values = FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, GameId, null);
            if (values.Count == 0)
                return;
            List<string> files = SettingsFolders(request.ConfigPath).Select(GameSettingsFile).Where(f => f != null).ToList();
            if (files.Count == 0)
            {
                result.Warnings.Add("Arma Reforger game settings not written: no ReforgerGameSettings.conf / GameUserSettings.conf next to the preset (profile\\.save\\app*_user*\\settings). Start the game once and change a setting.");
                return;
            }
            foreach (string path in files)
            {
                string text = File.ReadAllText(path);
                string original = text;
                List<string> changes = new List<string>();
                double value;
                if (values.TryGetValue("hotasCollective", out value))
                    ReforgerGameSettings.Set(ref text, "SCR_GameplaySettings", "m_eHotasCollective", ((int)Math.Round(Math.Max(0, Math.Min(3, value)))).ToString(CultureInfo.InvariantCulture), true, changes, result.Warnings);
                if (values.TryGetValue("forceFeedbackIntensity", out value))
                    ReforgerGameSettings.Set(ref text, "SCR_ControllerSettings", "m_fForceFeedbackIntensity", Math.Max(0, Math.Min(3, value)).ToString("0.###", CultureInfo.InvariantCulture), false, changes, result.Warnings);
                if (text == original)
                    continue;
                if (request.DryRun)
                {
                    result.Steps.AddRange(changes.Select(c => "Would set " + c + " in " + path));
                    continue;
                }
                GameExportSafety.BackupBeforeOverwrite(path, result.Warnings);
                File.WriteAllText(path, text, new UTF8Encoding(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })));
                result.FilesWritten.Add(path);
                result.Steps.AddRange(changes.Select(c => "Set " + c + " in " + path));
            }
        }

        // engine sync: what WriteGameSettings writes, from the game settings files next to the preset. The export writes
        // every settings folder (stable and experimental); the file the game saved last wins, the others fill keys it lacks.
        public Dictionary<string, double> ReadGameSettings(string configPath, List<string> warnings)
        {
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in SettingsFolders(configPath).Select(GameSettingsFile).Where(f => f != null).OrderByDescending(File.GetLastWriteTimeUtc))
            {
                string text = File.ReadAllText(path);
                double value;
                if (!values.ContainsKey("hotasCollective") && ReforgerGameSettings.TryGet(text, "m_eHotasCollective", out value))
                    values["hotasCollective"] = value;
                if (!values.ContainsKey("forceFeedbackIntensity") && ReforgerGameSettings.TryGet(text, "m_fForceFeedbackIntensity", out value))
                    values["forceFeedbackIntensity"] = value;
            }
            return values;
        }

        public string SettingsGame { get { return GameId; } }

        // The game settings files of every settings folder (either name), which the config watcher has to see too.
        public List<string> SettingsFiles(string configPath)
        {
            return SettingsFolders(configPath).SelectMany(f => new[] { Path.Combine(f, "ReforgerGameSettings.conf"), Path.Combine(f, "GameUserSettings.conf") }).ToList();
        }

        private static void DropUnusedDeviceSources(EnfusionActionFile file, string action, HashSet<string> usedDevices, GameDeviceDirectory devices, SlotMap slots)
        {
            List<string[]> existing = file.ReadSources(action);
            List<string[]> kept = existing.Where(source =>
            {
                int slot;
                string token;
                if (!TryParseInput(source[1], out slot, out token))
                    return true;
                string device = DeviceForSlot(slots, slot, devices, new List<string>());
                return device.Length == 0 || usedDevices.Contains(device);
            }).ToList();
            if (kept.Count == existing.Count)
                return;
            if (kept.Count == 0)
                file.RemoveAction(action);
            else
                file.SetAction(action, BuildActionBlock(action, kept, file.ActionIndent));
        }

        private static bool SameSources(List<string[]> left, List<string[]> right)
        {
            if (left.Count != right.Count)
                return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i][0], right[i][0], StringComparison.Ordinal) ||
                    !string.Equals(left[i][1], right[i][1], StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(left[i][2] ?? string.Empty, right[i][2] ?? string.Empty, StringComparison.Ordinal) ||
                    !string.Equals(left[i][3] ?? string.Empty, right[i][3] ?? string.Empty, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static string AddSources(GameInputBinding binding, GameDeviceDirectory devices, GameCatalog catalog, Dictionary<string, int> slots, Dictionary<string, List<string[]>> sources)
        {
            if (binding.IsKeyboard)
                return "keyboard binds stay in the game's own settings; JoyPro presets hold joystick binds only.";
            if (binding.Reformers != null && binding.Reformers.Count > 0)
                return "combos with modifiers are not supported in JoyPro's Reforger preset yet.";

            GameDevice device = devices == null ? null : devices.FindByJoyProName(binding.Device);
            if (device == null)
                return "device '" + binding.Device + "' is not connected; its joystickN slot is unknown.";
            int slot;
            if (!slots.TryGetValue(device.JoyProName, out slot))
                return "Reforger has four joystick slots; '" + binding.Device + "' got none (device " + (device.EnumerationIndex + 1).ToString(CultureInfo.InvariantCulture) + " in DirectInput order, or all slots taken by order rules).";
            string prefix = "joystick" + slot.ToString(CultureInfo.InvariantCulture) + ":";

            JoyInput input;
            if (!GameInputTokens.TryParse(binding.Input, out input))
                return "unsupported input '" + binding.Input + "'.";

            string[] composite;
            if (CompositeAxes.TryGetValue(binding.ActionId, out composite))
            {
                if (input.Kind != JoyInputKind.Axis)
                    return "'" + binding.ActionId + "' needs an axis.";
                string axis = prefix + "axis" + input.Index.ToString(CultureInfo.InvariantCulture);
                string filter = AxisFilter(binding.Tuning);
                string filterClass = filter.Length > 0 ? "InputFilterValue" : null;
                AddSource(sources, composite[0], composite[1], axis + (binding.Inverted ? "+" : "-"), filterClass, filter);
                AddSource(sources, composite[2], composite[3], axis + (binding.Inverted ? "-" : "+"), filterClass, filter);
                return string.Empty;
            }

            ActionPreset preset;
            GameAction generic = null;
            if (!ButtonPresets.TryGetValue(binding.ActionId, out preset))
            {
                generic = GenericActions(catalog).FirstOrDefault(a => string.Equals(a.Id, binding.ActionId, StringComparison.OrdinalIgnoreCase));
                if (generic == null)
                    return "'" + binding.ActionId + "' is not a managed Reforger action.";
            }

            if (generic != null && generic.IsAxis)
            {
                if (input.Kind != JoyInputKind.Axis)
                    return "'" + binding.ActionId + "' needs an axis.";
                // A whole axis: inverted by a negative multiplier.
                AxisTuning tuning = binding.Tuning ?? new AxisTuning();
                double multiplier = tuning.NativeOr("multiplier", 1) * (binding.Inverted ? -1 : 1);
                List<string> parts = new List<string>();
                if (Math.Abs(multiplier - 1) > 1e-9)
                    parts.Add("Multiplier " + multiplier.ToString("0.######", CultureInfo.InvariantCulture));
                if (tuning.NativeOr("useInvert", 0) != 0)
                    parts.Add("UseInvert 1");
                string axisFilter = string.Join(";", parts);
                AddSource(sources, ReforgerActionSpec.Of(generic).Action, string.Empty, prefix + "axis" + input.Index.ToString(CultureInfo.InvariantCulture), axisFilter.Length > 0 ? "InputFilterValue" : null, axisFilter);
                return string.Empty;
            }

            string token;
            if (input.Kind == JoyInputKind.Button)
                token = "button" + input.Index.ToString(CultureInfo.InvariantCulture);
            else if (input.Kind == JoyInputKind.Pov && input.Index == 0 && PovNames[input.Direction] != null)
                token = PovNames[input.Direction];
            else
                return "only buttons and the four main directions of the first hat are supported.";

            if (generic != null)
            {
                // The filter of the game's own default source (from refresh-db), so the button acts like the game's key.
                ReforgerActionSpec spec = ReforgerActionSpec.Of(generic);
                AddSource(sources, spec.Action, spec.Preset, prefix + token, spec.FilterClass.Length > 0 ? spec.FilterClass : null, spec.Parameters);
                return string.Empty;
            }
            AddSource(sources, binding.ActionId, preset.Preset, prefix + token, preset.FilterClass, string.Empty);
            return string.Empty;
        }

        private static void AddSource(Dictionary<string, List<string[]>> sources, string action, string preset, string input, string filterClass, string filter)
        {
            List<string[]> list;
            if (!sources.TryGetValue(action, out list))
            {
                list = new List<string[]>();
                sources[action] = list;
            }
            if (!list.Any(s => s[1] == input))
                list.Add(new[] { preset, input, filterClass, filter ?? string.Empty });
        }

        private static string BuildActionBlock(string action, List<string[]> sources, string indent)
        {
            string i1 = indent;
            string i2 = indent + " ";
            string i3 = i2 + " ";
            string i4 = i3 + " ";
            string i5 = i4 + " ";
            string i6 = i5 + " ";
            StringBuilder sb = new StringBuilder();
            sb.Append(i1).Append("Action ").Append(action).Append(" {\n");
            sb.Append(i2).Append("InputSource InputSourceSum \"").Append(NewId()).Append("\" {\n");
            sb.Append(i3).Append("Sources {\n");
            foreach (string[] source in sources)
            {
                sb.Append(i4).Append("InputSourceValue \"").Append(NewId()).Append("\" {\n");
                sb.Append(i5).Append("FilterPreset \"").Append(source[0]).Append("\"\n");
                sb.Append(i5).Append("Input \"").Append(source[1]).Append("\"\n");
                if (!string.IsNullOrEmpty(source[2]))
                {
                    sb.Append(i5).Append("Filter ").Append(source[2]).Append(" \"").Append(NewId()).Append("\" {\n");
                    foreach (KeyValuePair<string, string> parameter in FilterParameters(source.Length > 3 ? source[3] : string.Empty))
                        sb.Append(i6).Append(parameter.Key).Append(' ').Append(parameter.Value).Append('\n');
                    sb.Append(i5).Append("}\n");
                }
                sb.Append(i4).Append("}\n");
            }
            sb.Append(i3).Append("}\n");
            sb.Append(i2).Append("}\n");
            sb.Append(i1).Append("}");
            return sb.ToString();
        }

        private static string NewId()
        {
            byte[] bytes = new byte[8];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return "{" + BitConverter.ToString(bytes).Replace("-", string.Empty) + "}";
        }

        private static bool TryParseInput(string input, out int slot, out string token)
        {
            slot = -1;
            token = string.Empty;
            Match match = InputRegex.Match(input ?? string.Empty);
            if (!match.Success || !int.TryParse(match.Groups["slot"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot))
                return false;
            token = match.Groups["input"].Value;
            return true;
        }

        private static string ToJoyProButtonToken(string token)
        {
            if (token.StartsWith("button", StringComparison.OrdinalIgnoreCase))
            {
                int button;
                return int.TryParse(token.Substring(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out button) ? GameInputTokens.ButtonToken(button) : string.Empty;
            }
            int direction = Array.IndexOf(PovNames, token.ToLowerInvariant());
            return direction >= 0 ? GameInputTokens.PovToken(0, direction) : string.Empty;
        }

        private static string DeviceForSlot(SlotMap slots, int slot, GameDeviceDirectory devices, List<string> warnings)
        {
            GameDevice device = slots.DeviceForSlot(slot, devices);
            if (device == null)
            {
                warnings.Add("No connected device for Reforger joystick" + slot.ToString(CultureInfo.InvariantCulture) + "; its binds were not imported.");
                return string.Empty;
            }
            return device.JoyProName;
        }

        // From the installed game (or --path: the game folder, a .pak, a folder of extracted files, or the input config):
        // the actions of the flight contexts in Configs/System/chimeraInputCommon.conf, titled by the keybind menu and
        // the English string table; then the flight actions of the user's own input configs. Known actions stay as
        // they are; new ones are added (see ReforgerCatalogSource).
        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                GameInstall install = GameInstalls.Find(GameId, result.Warnings);
                sourcePath = install != null && install.Installed && !string.IsNullOrWhiteSpace(install.InstallPath) ? install.InstallPath : null;
                if (sourcePath == null)
                {
                    result.Errors.Add("Arma Reforger was not found. Set its folder (engine games install set reforger <folder>) or pass the game folder, a .pak file or the extracted Configs\\System\\chimeraInputCommon.conf.");
                    return null;
                }
            }
            return ReforgerCatalogSource.Refresh(GameId, sourcePath.Trim().Trim('"'), current, UserInputConfigs(), result);
        }

        // The user's joystick presets (customInputConfigs\*.conf, or the folder / file set with engine games config set)
        // and InputUserSettings.conf of each settings folder next to them.
        private List<string> UserInputConfigs()
        {
            List<string> files = new List<string>();
            try
            {
                string own = GameInstalls.ConfigOverride(GameId);
                string preset = own != null && Directory.Exists(own) ? Path.Combine(own, PresetFileName) : own ?? DiscoverConfigPaths(new List<string>()).FirstOrDefault();
                if (preset == null)
                    return files;
                string folder = Path.GetDirectoryName(Path.GetFullPath(preset)) ?? string.Empty;
                if (Directory.Exists(folder))
                    files.AddRange(Directory.GetFiles(folder, "*.conf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                files.AddRange(SettingsFolders(preset).Select(f => Path.Combine(f, "InputUserSettings.conf")).Where(File.Exists));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return files;
        }
    }

    internal sealed class ReforgerOrderRule
    {
        public string NameContains;
        public int Slot;
        // Text position of the DeviceSlotNumber value (for in-place edits).
        public int SlotIndex;
        public int SlotLength;
    }

    // EngineUserSettings.conf, reduced to the joystick order rules (format from the 1.8 scripts,
    // SCR_ControllerPresetsSettingsSubMenu.c: InputDeviceUserSettings > JoystickSettings > DeviceOrderRules >
    // JoystickOrderRule { DeviceNameContains "<name>" DeviceSlotNumber <n> }; not seen in a real file yet). JoyPro edits
    // a rule's slot in place, appends rules in that form inside DeviceOrderRules, and creates DeviceOrderRules /
    // JoystickSettings only inside an InputDeviceUserSettings block that is already there. Anything else is refused.
    internal sealed class ReforgerEngineSettings
    {
        private static readonly Regex RulePattern = new Regex("JoystickOrderRule(?<id>\\s+\"\\{[0-9A-Fa-f]+\\}\")?\\s*\\{(?<body>[^{}]*)\\}", RegexOptions.Compiled);

        private string text;
        private string newline;
        private bool bom;

        public string Path { get; private set; }
        public List<ReforgerOrderRule> Rules { get; private set; }
        public bool CanHoldRules { get; private set; }

        public static bool TryLoad(string path, List<string> warnings, out ReforgerEngineSettings settings)
        {
            settings = null;
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (IOException ex)
            {
                warnings.Add("Could not read " + path + ": " + ex.Message);
                return false;
            }
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string content = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            settings = new ReforgerEngineSettings { Path = path, bom = bom, newline = content.Contains("\r\n") ? "\r\n" : "\n", text = content.Replace("\r\n", "\n") };
            settings.Parse();
            return true;
        }

        private void Parse()
        {
            Rules = new List<ReforgerOrderRule>();
            int start;
            int end;
            CanHoldRules = FindBlock("InputDeviceUserSettings", 0, text.Length, out start, out end);
            int rulesStart;
            int rulesEnd;
            if (!FindBlock("DeviceOrderRules", 0, text.Length, out rulesStart, out rulesEnd))
                return;
            CanHoldRules = true;
            foreach (Match rule in RulePattern.Matches(text.Substring(rulesStart, rulesEnd - rulesStart)))
            {
                Match name = Regex.Match(rule.Groups["body"].Value, "DeviceNameContains\\s+\"(?<v>[^\"]*)\"");
                Match slot = Regex.Match(rule.Groups["body"].Value, "DeviceSlotNumber\\s+(?<v>-?\\d+)\\b");
                int number;
                if (!name.Success || !slot.Success || !int.TryParse(slot.Groups["v"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                    continue;
                Rules.Add(new ReforgerOrderRule
                {
                    NameContains = name.Groups["v"].Value,
                    Slot = number,
                    SlotIndex = rulesStart + rule.Groups["body"].Index + slot.Groups["v"].Index,
                    SlotLength = slot.Groups["v"].Length
                });
            }
        }

        // "<name> [class] [\"{id}\"] {" ... matching "}": body start (after "{") and the index of the closing brace.
        private bool FindBlock(string name, int from, int to, out int bodyStart, out int close)
        {
            bodyStart = -1;
            close = -1;
            // The header may hold a quoted id with braces: Name "{0123...}" {
            Match match = new Regex("(?m)^[ \\t]*" + Regex.Escape(name) + "\\b(?:[^{}\"\\n]|\"[^\"\\n]*\")*\\{").Match(text, from, to - from);
            if (!match.Success)
                return false;
            bodyStart = match.Index + match.Length;
            int depth = 1;
            bool quoted = false;
            for (int i = bodyStart; i < to; i++)
            {
                char c = text[i];
                if (c == '"')
                    quoted = !quoted;
                else if (!quoted && c == '{')
                    depth++;
                else if (!quoted && c == '}' && --depth == 0)
                {
                    close = i;
                    return true;
                }
            }
            return false;
        }

        private string IndentOfLine(int index)
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
            int i = lineStart;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
                i++;
            return text.Substring(lineStart, i - lineStart);
        }

        // Device rule name -> slot. Edits existing rules (also the game's own, which hold the longer name), appends missing
        // ones; false when nothing changed.
        public bool SetRules(Dictionary<string, int> slots, List<string> changes, List<string> warnings)
        {
            bool changed = false;
            foreach (KeyValuePair<string, int> wanted in slots.OrderBy(s => s.Value))
            {
                ReforgerOrderRule rule = Rules.FirstOrDefault(r => string.Equals(r.NameContains, wanted.Key, StringComparison.Ordinal))
                                         ?? Rules.FirstOrDefault(r => wanted.Key.IndexOf(r.NameContains, StringComparison.Ordinal) >= 0)
                                         ?? Rules.FirstOrDefault(r => r.NameContains.IndexOf(wanted.Key, StringComparison.Ordinal) >= 0);
                if (rule != null)
                {
                    if (rule.Slot == wanted.Value)
                        continue;
                    text = text.Substring(0, rule.SlotIndex) + wanted.Value.ToString(CultureInfo.InvariantCulture) + text.Substring(rule.SlotIndex + rule.SlotLength);
                    changes.Add("JoystickOrderRule \"" + rule.NameContains + "\" slot " + wanted.Value.ToString(CultureInfo.InvariantCulture) + " (was " + rule.Slot.ToString(CultureInfo.InvariantCulture) + ")");
                    changed = true;
                    Parse();
                    continue;
                }
                if (!AppendRule(wanted.Key, wanted.Value, warnings))
                    return changed;
                changes.Add("JoystickOrderRule \"" + wanted.Key + "\" slot " + wanted.Value.ToString(CultureInfo.InvariantCulture) + " (new)");
                changed = true;
                Parse();
            }
            return changed;
        }

        private bool AppendRule(string name, int slot, List<string> warnings)
        {
            int start;
            int close;
            if (!FindBlock("DeviceOrderRules", 0, text.Length, out start, out close))
            {
                // DeviceOrderRules { } inside JoystickSettings, or JoystickSettings { DeviceOrderRules { } } inside
                // InputDeviceUserSettings; nothing else is created.
                int joyStart;
                int joyClose;
                int inputStart;
                int inputClose;
                if (FindBlock("JoystickSettings", 0, text.Length, out joyStart, out joyClose))
                    InsertBlock(joyClose, IndentOfLine(joyStart) + " ", "DeviceOrderRules {", null);
                else if (FindBlock("InputDeviceUserSettings", 0, text.Length, out inputStart, out inputClose))
                    InsertBlock(inputClose, IndentOfLine(inputStart) + " ", "JoystickSettings {", "DeviceOrderRules {");
                else
                {
                    warnings.Add(Path + " has no InputDeviceUserSettings section; device order rules not written.");
                    return false;
                }
                if (!FindBlock("DeviceOrderRules", 0, text.Length, out start, out close))
                    return false;
            }
            bool withId = RulePattern.Matches(text.Substring(start, close - start)).Cast<Match>().Any(m => m.Groups["id"].Success);
            string indent = IndentOfLine(start) + " ";
            StringBuilder sb = new StringBuilder();
            sb.Append(indent).Append("JoystickOrderRule");
            if (withId)
                sb.Append(" \"").Append(NewId()).Append('"');
            sb.Append(" {\n");
            sb.Append(indent).Append(" DeviceNameContains \"").Append(name.Replace("\"", string.Empty)).Append("\"\n");
            sb.Append(indent).Append(" DeviceSlotNumber ").Append(slot.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(indent).Append("}\n");
            InsertBeforeClose(close, sb.ToString());
            return true;
        }

        // Inserts "<name> {\n [inner {\n }\n] }\n" before the closing brace at `close`.
        private void InsertBlock(int close, string indent, string open, string inner)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(indent).Append(open).Append('\n');
            if (inner != null)
                sb.Append(indent).Append(' ').Append(inner).Append('\n').Append(indent).Append(" }\n");
            sb.Append(indent).Append("}\n");
            InsertBeforeClose(close, sb.ToString());
        }

        private void InsertBeforeClose(int close, string block)
        {
            // At the start of the closing brace's line (keeps that line's indentation).
            int lineStart = text.LastIndexOf('\n', Math.Max(0, close - 1)) + 1;
            bool onOwnLine = text.Substring(lineStart, close - lineStart).Trim().Length == 0;
            if (onOwnLine)
                text = text.Substring(0, lineStart) + block + text.Substring(lineStart);
            else
                text = text.Substring(0, close) + "\n" + block + text.Substring(close);
        }

        private static string NewId()
        {
            byte[] bytes = new byte[8];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return "{" + BitConverter.ToString(bytes).Replace("-", string.Empty) + "}";
        }

        public void Save()
        {
            string content = newline == "\n" ? text : text.Replace("\n", newline);
            byte[] body = new UTF8Encoding(false).GetBytes(content);
            using (FileStream stream = new FileStream(Path, FileMode.Create, FileAccess.Write))
            {
                if (bom)
                    stream.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                stream.Write(body, 0, body.Length);
            }
        }
    }

    // GameUserSettings.conf keys of the game's settings modules (SCR_GameplaySettings, SCR_ControllerSettings; format
    // "Key Value" as in every Enfusion .conf, not seen in a real file yet). An existing key is changed in place (numbers
    // only); a missing key is added inside its module's block when that block exists; nothing else is created.
    internal static class ReforgerGameSettings
    {
        // The number of a "Key Value" line, found as Set finds it; false when the key is missing or not a number.
        public static bool TryGet(string text, string key, out double value)
        {
            value = 0;
            Match existing = Regex.Match(text ?? string.Empty, "(?m)^(?<i>[ \\t]*)" + Regex.Escape(key) + "[ \\t]+(?<v>[^\\s]+)[ \\t]*\\r?$");
            return existing.Success && double.TryParse(existing.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public static bool Set(ref string text, string module, string key, string value, bool integer, List<string> changes, List<string> warnings)
        {
            Match existing = Regex.Match(text, "(?m)^(?<i>[ \\t]*)" + Regex.Escape(key) + "[ \\t]+(?<v>[^\\s]+)[ \\t]*\\r?$");
            if (existing.Success)
            {
                double current;
                string raw = existing.Groups["v"].Value;
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out current))
                {
                    warnings.Add(key + " in GameUserSettings.conf has the value '" + raw + "', not a number; JoyPro leaves it.");
                    return false;
                }
                if (Math.Abs(current - double.Parse(value, CultureInfo.InvariantCulture)) < 1e-9)
                    return false;
                text = text.Substring(0, existing.Groups["v"].Index) + value + text.Substring(existing.Groups["v"].Index + raw.Length);
                changes.Add(module + "." + key + " = " + value + " (was " + raw + ")");
                return true;
            }
            Match block = Regex.Match(text, "(?m)^(?<i>[ \\t]*)" + Regex.Escape(module) + "\\b(?:[^{}\"\\r\\n]|\"[^\"\\r\\n]*\")*\\{");
            if (!block.Success)
            {
                warnings.Add(key + " not written: GameUserSettings.conf has no " + module + " section yet (change that setting in the game once).");
                return false;
            }
            int depth = 1;
            int close = -1;
            bool quoted = false;
            for (int i = block.Index + block.Length; i < text.Length; i++)
            {
                if (text[i] == '"')
                    quoted = !quoted;
                else if (quoted)
                    continue;
                else if (text[i] == '{')
                    depth++;
                else if (text[i] == '}' && --depth == 0)
                {
                    close = i;
                    break;
                }
            }
            if (close < 0)
                return false;
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            string line = block.Groups["i"].Value + " " + key + " " + value + nl;
            int lineStart = text.LastIndexOf('\n', Math.Max(0, close - 1)) + 1;
            if (text.Substring(lineStart, close - lineStart).Trim().Length == 0)
                text = text.Substring(0, lineStart) + line + text.Substring(lineStart);
            else
                text = text.Substring(0, close) + nl + line + text.Substring(close);
            changes.Add(module + "." + key + " = " + value + " (new)");
            return true;
        }
    }

    // An Enfusion input config reduced to what JoyPro needs: the raw text around the "Actions { ... }" list and
    // one raw text block per Action. Unchanged blocks are written back exactly as read.
    internal sealed class EnfusionActionFile
    {
        private string head;
        private string tail;
        private readonly List<string> order = new List<string>();
        private readonly Dictionary<string, string> blocks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> separators = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string newline = "\n";
        private bool bom;

        public string ActionIndent { get; private set; }

        public int ChangedActionCount
        {
            get { return changed.Count; }
        }

        public static EnfusionActionFile CreateEmpty()
        {
            EnfusionActionFile file = new EnfusionActionFile();
            file.head = "ActionManager {\n Actions {";
            file.tail = "\n }\n}\n";
            file.ActionIndent = "  ";
            return file;
        }

        public static bool TryLoad(string path, List<string> errors, out EnfusionActionFile file)
        {
            file = null;
            if (!File.Exists(path))
            {
                errors.Add("Reforger input config not found: " + path);
                return false;
            }

            byte[] bytes = File.ReadAllBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            text = text.Replace("\r\n", "\n");

            Match actions = Regex.Match(text, @"\n?(?<indent>[ \t]*)Actions\s*\{");
            if (!actions.Success)
            {
                errors.Add("No 'Actions {' list found in " + path + ".");
                return false;
            }

            file = new EnfusionActionFile { newline = newline, bom = bom, ActionIndent = actions.Groups["indent"].Value + " " };
            int position = actions.Index + actions.Length;
            file.head = text.Substring(0, position);
            Regex actionStart = new Regex(@"\G(?<sep>\s*)(?<indent>[ \t]*)Action\s+(?<name>[A-Za-z0-9_]+)\s*\{");
            while (true)
            {
                Match start = actionStart.Match(text, position);
                if (!start.Success)
                    break;
                int end = FindBlockEnd(text, start.Index + start.Length);
                if (end < 0)
                {
                    errors.Add("Unbalanced braces in " + path + ".");
                    return false;
                }
                string name = start.Groups["name"].Value;
                int blockStart = start.Index + start.Groups["sep"].Length;
                if (!file.blocks.ContainsKey(name))
                {
                    file.order.Add(name);
                    file.blocks[name] = text.Substring(blockStart, end + 1 - blockStart);
                    file.separators[name] = start.Groups["sep"].Value;
                    file.ActionIndent = start.Groups["indent"].Value;
                }
                position = end + 1;
            }
            file.tail = text.Substring(position);
            return true;
        }

        private static int FindBlockEnd(string text, int position)
        {
            int depth = 1;
            bool quoted = false;
            for (int i = position; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"')
                    quoted = !quoted;
                else if (!quoted && c == '{')
                    depth++;
                else if (!quoted && c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }
            return -1;
        }

        public bool HasAction(string action)
        {
            return blocks.ContainsKey(action);
        }

        public IEnumerable<string> Inputs(string action)
        {
            return ReadSources(action).Select(s => s[1]);
        }

        public List<string[]> ReadSources(string action)
        {
            List<string[]> output = new List<string[]>();
            string block;
            if (!blocks.TryGetValue(action, out block))
                return output;
            foreach (Match match in Regex.Matches(block, "InputSourceValue\\s+\"[^\"]*\"\\s*\\{(?<body>[^{}]*(\\{[^{}]*\\}[^{}]*)*)\\}"))
            {
                string body = match.Groups["body"].Value;
                Match preset = Regex.Match(body, "FilterPreset\\s+\"(?<v>[^\"]*)\"");
                Match input = Regex.Match(body, "Input\\s+\"(?<v>[^\"]*)\"");
                Match filter = Regex.Match(body, "Filter\\s+(?<v>[A-Za-z0-9_]+)\\s+\"[^\"]*\"\\s*\\{(?<params>[^{}]*)\\}");
                if (!filter.Success)
                    filter = Regex.Match(body, "Filter\\s+(?<v>[A-Za-z0-9_]+)\\s+\"");
                // Filter parameters normalised as "Key value;Key value" (InputFilterValue Multiplier / UseInvert).
                string parameters = filter.Success && filter.Groups["params"].Success
                    ? string.Join(";", filter.Groups["params"].Value.Split('\n').Select(l => Regex.Replace(l.Trim(), "\\s+", " ")).Where(l => l.Length > 0))
                    : string.Empty;
                if (input.Success)
                    output.Add(new[] { preset.Success ? preset.Groups["v"].Value : string.Empty, input.Groups["v"].Value, filter.Success ? filter.Groups["v"].Value : null, parameters });
            }
            return output;
        }

        public void SetAction(string action, string block)
        {
            string current;
            if (blocks.TryGetValue(action, out current) && current == block)
                return;
            if (!blocks.ContainsKey(action))
            {
                order.Add(action);
                separators[action] = "\n";
            }
            blocks[action] = block;
            changed.Add(action);
        }

        public void RemoveAction(string action)
        {
            if (blocks.Remove(action))
            {
                order.Remove(action);
                changed.Add(action);
            }
        }

        public void Save(string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(head);
            foreach (string name in order)
            {
                string separator;
                separators.TryGetValue(name, out separator);
                sb.Append(separator ?? "\n").Append(blocks[name]);
            }
            sb.Append(tail);
            string text = sb.ToString();
            if (newline != "\n")
                text = text.Replace("\n", newline);
            byte[] body = new UTF8Encoding(false).GetBytes(text);
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                if (bom)
                    stream.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                stream.Write(body, 0, body.Length);
            }
        }
    }
}
