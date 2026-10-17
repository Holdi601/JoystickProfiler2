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
    // Shared by Arma 3, Arma 2 and Arma 2: Operation Arrowhead (docs/games/arma3.md, docs/games/arma2.md): binds are
    // "key<Action>[]={code,...};" lines in the player profile; joystick codes are 0x20000 / 0x30000 / 0x40000 + device
    // offset + index; class JoysticksList names each device by its DirectInput instance GUID. Axes are bound per half
    // axis, so JoyPro axis relations use composite catalog entries ("Axis.*"). Only lines of cataloged flight actions are
    // rewritten, and only joystick codes of the devices being exported are replaced; keyboard, mouse and other codes stay.
    // The response curve is per device and physical axis (JoysticksList arrays, shared by every vehicle and on foot):
    // the relations' tunings for the game are written there, one per physical axis. Device settings (mode, analog click)
    // and game options come from the FfbSettings store (engine ffb set --game <game>).
    internal abstract class ArmaProfileIntegration : IGameIntegration, IGameOwnershipTracking, IGameSettingsExport, IGameSettingsReader
    {
        private const int ButtonBase = 0x00020000;
        private const int AxisBase = 0x00030000;
        private const int PovBase = 0x00040000;

        // A JoyPro axis relation on Arma's half-axis actions. Pushing a stick forward / left reports the negative half.
        // Split: negative half to one action, positive half to the other. One action: the half the lever moves into
        // (forward = negative). Full axis: both halves on one action, the first code decides the direction.
        protected sealed class Composite
        {
            public Composite(string negative, string positive, bool fullAxis = false)
            {
                Negative = negative;
                Positive = positive;
                FullAxis = fullAxis;
            }

            public string Negative { get; private set; }
            public string Positive { get; private set; }
            public bool FullAxis { get; private set; }

            public bool SameAction
            {
                get { return string.Equals(Negative, Positive, StringComparison.OrdinalIgnoreCase); }
            }
        }

        public abstract string GameId { get; }
        public abstract string DisplayName { get; }
        public abstract IReadOnlyList<string> Aliases { get; }
        public abstract string DbFolder { get; }
        public abstract string Notes { get; }
        public abstract IReadOnlyList<string> ProcessNames { get; }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return false; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        public GameFeature KeyboardBinds { get { return GameFeature.No; } }
        public GameFeature ModifierBinds { get { return GameFeature.No; } }

        protected abstract IReadOnlyDictionary<string, Composite> CompositeAxes { get; }
        // Profile folders below Documents: the default folder (top level) and the "other profiles" folder (recursive).
        protected abstract string DefaultProfileFolder { get; }
        protected abstract string OtherProfilesFolder { get; }
        protected abstract string ProfileExtension { get; }
        protected virtual int MaxButtons { get { return 256; } }
        // Merged full-axis codes (local index 16..31) appear in profiles of games that support full axes.
        protected virtual bool ReadsMergedAxes { get { return false; } }
        // The mode string JoyPro writes to switch a device off.
        protected virtual string DisabledModeName { get { return "Disabled"; } }

        protected virtual bool IsDisabledMode(string mode)
        {
            return string.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase);
        }

        protected virtual string[] FlightPrefixes
        {
            get { return new[] { "Heli", "Air", "Plane", "vtol", "Flaps", "LandGear", "AutoHover" }; }
        }

        // The installed game's controls data (ArmaGameData), relative to the install folder: the PBO with the game config
        // (config.bin), the class that lists every action with its default binds, and the PBOs with the stringtable of
        // the action names (first found wins).
        // Non-flight actions JoyPro manages as well (weapons, view, comms a HOTAS often carries), JoyPro's own choice;
        // kept when the game's config or the profile names them.
        protected virtual string[] ExtraManagedActions { get { return new[] { "Eject", "EngineToggle", "LaunchCM", "NextCM" }; } }

        protected abstract string[] ConfigPbos { get; }
        protected abstract string DefaultKeysClass { get; }
        protected abstract string[] LanguagePbos { get; }

        // The response parameters (AxisTuning.Native keys of the game's model) kept per physical axis.
        protected abstract string[] AxisTuningKeys { get; }
        // Import: the device's values for one physical axis into the bind's tuning.
        protected abstract void ReadAxisTuning(ArmaProfile profile, ArmaJoystick joystick, int axis, AxisTuning tuning);
        // Export: one physical axis's values (only the keys the tuning has) into the device's arrays.
        protected abstract void WriteAxisTuning(ArmaProfile profile, Guid device, int axis, AxisTuning tuning, string deviceLabel, GameIoResult result);

        // Export: per device settings other than mode (Arma 3: analog click thresholds).
        protected virtual void WriteDeviceSettings(ArmaProfile profile, Guid device, Dictionary<string, double> values, string deviceLabel, GameIoResult result)
        {
        }

        // Export: game-wide settings (device "*").
        protected virtual void WriteGameSettings(ArmaProfile profile, Dictionary<string, double> values, GameIoResult result)
        {
        }

        // engine sync: the game-wide settings WriteGameSettings writes, as the profile holds them now (Arma 2 has none).
        protected virtual void ReadGameSettings(ArmaProfile profile, Dictionary<string, double> values)
        {
        }

        public Dictionary<string, double> ReadGameSettings(string configPath, List<string> warnings)
        {
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            List<string> errors = new List<string>();
            ArmaProfile profile;
            if (!ArmaProfile.TryLoad(configPath, DisplayName, errors, out profile))
            {
                warnings?.AddRange(errors);
                return values;
            }
            ReadGameSettings(profile, values);
            return values;
        }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            List<FileInfo> files = new List<FileInfo>();
            AddProfiles(Path.Combine(documents, DefaultProfileFolder), SearchOption.TopDirectoryOnly, files);
            AddProfiles(Path.Combine(documents, OtherProfilesFolder), SearchOption.AllDirectories, files);
            return files.OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName).ToList();
        }

        private void AddProfiles(string folder, SearchOption option, List<FileInfo> output)
        {
            if (!Directory.Exists(folder))
                return;
            foreach (string path in Directory.GetFiles(folder, "*" + ProfileExtension, option))
            {
                string name = Path.GetFileName(path);
                if (!name.EndsWith(ProfileExtension, StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".vars" + ProfileExtension, StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".3den" + ProfileExtension, StringComparison.OrdinalIgnoreCase))
                    continue;
                output.Add(new FileInfo(path));
            }
        }

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            ArmaProfile profile;
            if (!ArmaProfile.TryLoad(configPath, DisplayName, result.Errors, out profile))
                return result;

            foreach (KeyValuePair<string, Composite> composite in CompositeAxes)
            {
                // Every joystick axis half found in the action(s) of the composite; the first code of an axis decides.
                Dictionary<string, GameInputBinding> byAxis = new Dictionary<string, GameInputBinding>(StringComparer.OrdinalIgnoreCase);
                string[] actions = { composite.Value.Negative, composite.Value.Positive };
                for (int half = 0; half < 2; half++)
                {
                    ArmaKeyLine line = profile.FindKey(actions[half]);
                    if (line == null)
                        continue;
                    foreach (int code in line.Codes)
                    {
                        ArmaCode decoded;
                        if (!ArmaCode.TryDecode(code, ReadsMergedAxes, out decoded) || decoded.Kind != JoyInputKind.Axis)
                            continue;
                        string device = ResolveDevice(profile, decoded.Offset, devices, result.Warnings);
                        if (device.Length == 0)
                            continue;
                        string key = device + "|" + decoded.Index.ToString(CultureInfo.InvariantCulture);
                        if (byAxis.ContainsKey(key))
                            continue;
                        // The negative half on the "negative" action is not inverted; on one action the axis is inverted
                        // when its (first) code is the positive half.
                        bool inverted = composite.Value.SameAction ? !decoded.NegativeHalf : (decoded.NegativeHalf ? half == 1 : half == 0);
                        GameInputBinding binding = new GameInputBinding
                        {
                            ActionId = composite.Key,
                            Device = device,
                            Input = GameInputTokens.AxisToken(decoded.Index),
                            IsAxis = true,
                            Inverted = inverted
                        };
                        // The device's response settings of this physical axis (stored as the game's tuning).
                        ArmaJoystick joystick = profile.Joysticks.FirstOrDefault(j => j.Offset == decoded.Offset);
                        if (joystick != null)
                        {
                            AxisTuning tuning = new AxisTuning { Inverted = inverted, Slider = composite.Value.FullAxis };
                            ReadAxisTuning(profile, joystick, decoded.Index, tuning);
                            if (tuning.Native.Count > 0)
                                binding.Tuning = tuning;
                        }
                        byAxis[key] = binding;
                    }
                }
                result.Bindings.AddRange(byAxis.Values);
            }

            foreach (GameAction action in catalog.Actions.Where(a => !a.IsAxis))
            {
                ArmaKeyLine line = profile.FindKey(action.Id);
                if (line == null)
                    continue;
                foreach (int code in line.Codes)
                {
                    ArmaCode decoded;
                    if (!ArmaCode.TryDecode(code, ReadsMergedAxes, out decoded) || decoded.Kind == JoyInputKind.Axis)
                        continue;
                    string device = ResolveDevice(profile, decoded.Offset, devices, result.Warnings);
                    if (device.Length == 0)
                        continue;
                    result.Bindings.Add(new GameInputBinding
                    {
                        ActionId = action.Id,
                        Device = device,
                        Input = decoded.Kind == JoyInputKind.Pov ? GameInputTokens.PovToken(0, decoded.Index) : GameInputTokens.ButtonToken(decoded.Index),
                        IsAxis = false
                    });
                }
            }

            return result;
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
            ArmaProfile profile;
            if (!ArmaProfile.TryLoad(request.ConfigPath, DisplayName, result.Errors, out profile))
                return result;

            HashSet<int> exportedOffsets = new HashSet<int>();
            // Physical axis (device GUID, axis) -> the tuning written there and the relation it came from.
            Dictionary<string, KeyValuePair<AxisTuning, string>> axisTunings = new Dictionary<string, KeyValuePair<AxisTuning, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> owned = null;
            if (!request.SettingsOnly)
            {
                // Desired codes per Arma action, and the device offsets JoyPro owns in this export.
                Dictionary<string, List<int>> desired = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (GameInputBinding binding in request.Bindings)
                {
                    int offset;
                    string reason = TryResolveOffset(binding, profile, request.Devices, out offset);
                    if (reason.Length == 0)
                        reason = AddDesiredCodes(binding, offset, desired);
                    if (reason.Length > 0)
                    {
                        result.BindingsSkipped++;
                        result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
                        continue;
                    }
                    exportedOffsets.Add(offset);
                    result.BindingsWritten++;
                    CollectAxisTuning(binding, profile.Joysticks.First(j => j.Offset == offset), axisTunings, result);
                }

                owned = GameOwnershipStore.Load(request.ConfigPath, result.Warnings);
                HashSet<string> managedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> fullAxisActions = new HashSet<string>(CompositeAxes.Values.Where(c => c.FullAxis).Select(c => c.Negative), StringComparer.OrdinalIgnoreCase);
                foreach (GameAction action in request.Catalog.Actions)
                {
                    Composite composite;
                    if (CompositeAxes.TryGetValue(action.Id, out composite))
                    {
                        managedActions.Add(composite.Negative);
                        managedActions.Add(composite.Positive);
                    }
                    else
                    {
                        managedActions.Add(action.Id);
                    }
                }

                foreach (string actionName in managedActions.OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
                {
                    List<int> wanted;
                    desired.TryGetValue(actionName, out wanted);
                    wanted = wanted ?? new List<int>();
                    HashSet<string> ownedCodes;
                    owned.TryGetValue(actionName, out ownedCodes);
                    ownedCodes = ownedCodes ?? new HashSet<string>();

                    ArmaKeyLine line = profile.FindKey(actionName);
                    List<int> updated = MergeCodes(line == null ? new List<int>() : line.Codes, wanted, ownedCodes, exportedOffsets, request.Mode,
                        request.NukeUnusedDevices, fullAxisActions.Contains(actionName));
                    if (line == null)
                    {
                        if (updated.Count > 0)
                            profile.AppendKey(actionName, updated);
                    }
                    else if (!updated.SequenceEqual(line.Codes))
                    {
                        profile.SetCodes(line, updated);
                    }

                    HashSet<string> nowOwned = request.Mode == GameExportMode.Clean || request.Mode == GameExportMode.MergeOverwrite
                        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(ownedCodes, StringComparer.OrdinalIgnoreCase);
                    foreach (int code in wanted.Where(updated.Contains))
                        nowOwned.Add(code.ToString(CultureInfo.InvariantCulture));
                    owned[actionName] = nowOwned;
                }
            }

            WriteDevices(profile, exportedOffsets, result);
            foreach (KeyValuePair<string, KeyValuePair<AxisTuning, string>> entry in axisTunings)
            {
                string[] parts = entry.Key.Split('|');
                Guid guid = Guid.Parse(parts[0]);
                ArmaJoystick joystick = profile.FindJoystick(guid);
                WriteAxisTuning(profile, guid, int.Parse(parts[1], CultureInfo.InvariantCulture), entry.Value.Key,
                    (joystick == null ? guid.ToString() : joystick.Name) + " " + GameInputTokens.AxisToken(int.Parse(parts[1], CultureInfo.InvariantCulture)), result);
            }
            Dictionary<string, double> gameWide = FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, GameId, null);
            if (gameWide.Count > 0)
                WriteGameSettings(profile, gameWide, result);

            int changes = profile.ChangedLineCount + profile.SettingChanges.Count;
            if (request.DryRun)
            {
                result.Steps.Add("Dry run: " + profile.ChangedLineCount.ToString(CultureInfo.InvariantCulture) + " key line(s) and " +
                                 profile.SettingChanges.Count.ToString(CultureInfo.InvariantCulture) + " setting(s) would change.");
                result.Steps.AddRange(profile.SettingChanges.Select(c => "Would set " + c));
                return result;
            }

            if (owned != null)
                GameOwnershipStore.Save(request.ConfigPath, GameId, owned);
            if (changes == 0)
            {
                result.Steps.Add("The " + DisplayName + " profile already matches.");
                return result;
            }

            GameExportSafety.BackupBeforeOverwrite(request.ConfigPath, result.Warnings);
            profile.Save(request.ConfigPath);
            result.FilesWritten.Add(request.ConfigPath);
            result.Steps.Add(profile.ChangedLineCount.ToString(CultureInfo.InvariantCulture) + " key line(s) changed.");
            result.Steps.AddRange(profile.SettingChanges.Select(c => "Set " + c));
            return result;
        }

        // One tuning per physical axis: Arma applies the device's curve to every action the axis is bound to.
        private void CollectAxisTuning(GameInputBinding binding, ArmaJoystick joystick, Dictionary<string, KeyValuePair<AxisTuning, string>> axisTunings, GameIoResult result)
        {
            JoyInput input;
            if (binding.Tuning == null || !binding.IsAxis || !GameInputTokens.TryParse(binding.Input, out input) || input.Kind != JoyInputKind.Axis)
                return;
            AxisTuning values = new AxisTuning();
            foreach (string key in AxisTuningKeys)
            {
                double value;
                if (binding.Tuning.Native != null && binding.Tuning.Native.TryGetValue(key, out value))
                    values.Native[key] = value;
            }
            if (values.Native.Count == 0)
                return;
            string id = joystick.Guid.ToString() + "|" + input.Index.ToString(CultureInfo.InvariantCulture);
            KeyValuePair<AxisTuning, string> first;
            if (!axisTunings.TryGetValue(id, out first))
            {
                axisTunings[id] = new KeyValuePair<AxisTuning, string>(values, binding.RelationName);
                return;
            }
            List<string> conflicts = new List<string>();
            foreach (KeyValuePair<string, double> value in values.Native)
            {
                double kept;
                if (!first.Key.Native.TryGetValue(value.Key, out kept))
                    first.Key.Native[value.Key] = value.Value;
                else if (Math.Abs(kept - value.Value) > 1e-6)
                    conflicts.Add(value.Key + " " + AxisTuning.Num(kept) + " / " + AxisTuning.Num(value.Value));
            }
            if (conflicts.Count > 0)
                result.Warnings.Add("Relations '" + first.Value + "' and '" + binding.RelationName + "' set different response settings for " + joystick.Name + " " +
                                    binding.Input + " (" + string.Join(", ", conflicts) + "). " + DisplayName + " has one curve per physical axis: '" + first.Value + "' is written.");
        }

        // Device switch: devices that get binds are enabled (mode Custom) unless the user set them off; explicit
        // per-device settings (engine ffb set --device <device> --game <game> --param enabled=0|1) win.
        private void WriteDevices(ArmaProfile profile, HashSet<int> exportedOffsets, GameIoResult result)
        {
            Dictionary<Guid, KeyValuePair<string, Dictionary<string, double>>> settings = new Dictionary<Guid, KeyValuePair<string, Dictionary<string, double>>>();
            foreach (string device in FfbSettings.DevicesFor(InternalDataManagement.FfbOverrides, GameId))
            {
                if (device == FfbSettings.AnyDevice)
                    continue;
                string name;
                Guid guid;
                if (!GameDeviceDirectory.TryParseJoyProName(device, out name, out guid))
                {
                    result.Warnings.Add("Device settings for '" + device + "' not written: the device has no DirectInput GUID.");
                    continue;
                }
                if (profile.FindJoystick(guid) == null)
                {
                    result.Warnings.Add("Device settings for '" + device + "' not written: " + DisplayName + " has not seen the device yet (start the game once with it connected).");
                    continue;
                }
                settings[guid] = new KeyValuePair<string, Dictionary<string, double>>(device, FfbSettings.Resolve(InternalDataManagement.FfbOverrides, device, GameId, null));
            }

            foreach (Guid guid in profile.Joysticks.Select(j => j.Guid).ToList())
            {
                ArmaJoystick joystick = profile.FindJoystick(guid);
                KeyValuePair<string, Dictionary<string, double>> own;
                Dictionary<string, double> values = settings.TryGetValue(guid, out own) ? own.Value : new Dictionary<string, double>();
                double enabled;
                bool explicitMode = values.TryGetValue("enabled", out enabled);
                bool exported = exportedOffsets.Contains(joystick.Offset);
                string wanted = null;
                if (explicitMode)
                    wanted = enabled != 0 ? "Custom" : DisabledModeName;
                else if (exported && IsDisabledMode(joystick.Mode))
                    wanted = "Custom";
                if (explicitMode && enabled == 0 && exported)
                    result.Warnings.Add(joystick.Name + " gets binds but is set to disabled for " + DisplayName + "; the game ignores its binds.");
                if (wanted != null && IsDisabledMode(wanted) != IsDisabledMode(joystick.Mode))
                {
                    profile.SetJoystickValue(guid, "mode", "\"" + wanted + "\"", new[] { "steepness", "deadZone", "sensitivity", "isXInput", "offset" },
                        joystick.Name + " mode \"" + wanted + "\" (was \"" + joystick.Mode + "\")");
                    if (!explicitMode)
                        result.Steps.Add(joystick.Name + " was switched off in " + DisplayName + " (mode \"" + joystick.Mode + "\"); JoyPro enabled it (mode \"Custom\") because it gets binds.");
                }
                if (values.Count > 0)
                    WriteDeviceSettings(profile, guid, values, joystick.Name, result);
            }
        }

        // Keeps existing codes in place (so unchanged lines stay byte-identical). Overwrite only drops codes JoyPro owns
        // (written earlier or taken over on import), so binds the user added in the game survive. Arma also has infantry
        // and ground vehicles, so only cataloged (aircraft) actions come here: clean drops the other joystick codes of the
        // devices the export writes, nuke those of the devices it does not use. On a full-axis action the order of the
        // two halves is the direction, so JoyPro's codes go to the end in the wanted order. Merge keeps the action as it is
        // when it already has an input of the same kind (button, axis, hat) on a written device: Arma's defaults put axis
        // codes of all 16 joystick slots on many helicopter actions, which must not block a button bind.
        private static List<int> MergeCodes(List<int> existing, List<int> wanted, HashSet<string> ownedCodes, HashSet<int> exportedOffsets,
            GameExportMode mode, bool nuke, bool keepWantedOrder)
        {
            List<int> result = new List<int>();
            HashSet<JoyInputKind> wantedKinds = new HashSet<JoyInputKind>();
            foreach (int code in wanted)
            {
                ArmaCode w;
                if (ArmaCode.TryDecode(code, true, out w))
                    wantedKinds.Add(w.Kind);
            }
            bool hasJoyPro = existing.Any(c => IsJoystickCodeOf(c, exportedOffsets, wantedKinds));
            foreach (int code in existing)
            {
                ArmaCode decoded;
                bool joystick = ArmaCode.TryDecode(code, true, out decoded);
                bool other = joystick && !wanted.Contains(code);
                bool owned = other && ownedCodes.Contains(code.ToString(CultureInfo.InvariantCulture)) &&
                             (mode == GameExportMode.Clean || mode == GameExportMode.MergeOverwrite);
                bool released = other && (exportedOffsets.Contains(decoded.Offset) ? mode == GameExportMode.Clean : nuke);
                bool drop = owned || released || (keepWantedOrder && wanted.Contains(code) && !(mode == GameExportMode.Merge && hasJoyPro));
                if (!drop)
                    result.Add(code);
            }

            if (mode == GameExportMode.Merge && hasJoyPro)
                return result;

            foreach (int code in wanted)
            {
                if (!result.Contains(code))
                    result.Add(code);
            }
            return result;
        }

        private static bool IsJoystickCodeOf(int code, HashSet<int> offsets, HashSet<JoyInputKind> kinds)
        {
            ArmaCode decoded;
            return ArmaCode.TryDecode(code, true, out decoded) && offsets.Contains(decoded.Offset) && kinds.Contains(decoded.Kind);
        }

        private string TryResolveOffset(GameInputBinding binding, ArmaProfile profile, GameDeviceDirectory devices, out int offset)
        {
            offset = 0;
            if (binding.IsKeyboard)
                return "keyboard binds are not exported to " + DisplayName + " (joystick only).";
            if (binding.Reformers != null && binding.Reformers.Count > 0)
                return DisplayName + " has no joystick combos with modifiers (only keyboard + keyboard / mouse).";

            string name;
            Guid guid;
            if (!GameDeviceDirectory.TryParseJoyProName(binding.Device, out name, out guid))
                return "device '" + binding.Device + "' has no DirectInput GUID.";

            ArmaJoystick joystick = profile.Joysticks.FirstOrDefault(j => j.Guid == guid);
            if (joystick == null)
                return DisplayName + " has not seen device '" + binding.Device + "' yet. Start " + DisplayName + " once with it connected (Controls > Controllers), then export again.";
            // A disabled device is enabled by the export (WriteDevices).
            offset = joystick.Offset;
            return string.Empty;
        }

        private string AddDesiredCodes(GameInputBinding binding, int offset, Dictionary<string, List<int>> desired)
        {
            JoyInput input;
            if (!GameInputTokens.TryParse(binding.Input, out input))
                return "unsupported input '" + binding.Input + "'.";

            Composite composite;
            if (CompositeAxes.TryGetValue(binding.ActionId, out composite))
            {
                if (input.Kind != JoyInputKind.Axis)
                    return "'" + binding.ActionId + "' needs an axis.";
                int negative = AxisBase + offset + input.Index + 8;
                int positive = AxisBase + offset + input.Index;
                if (composite.FullAxis)
                {
                    // Both halves; the first one is the end where the action is at maximum (lever forward = negative).
                    AddCode(desired, composite.Negative, binding.Inverted ? positive : negative);
                    AddCode(desired, composite.Negative, binding.Inverted ? negative : positive);
                }
                else if (composite.SameAction)
                {
                    // One-action axes (throttle, airbrake) use the half the lever moves into: forward = negative.
                    AddCode(desired, composite.Negative, binding.Inverted ? positive : negative);
                }
                else
                {
                    AddCode(desired, composite.Negative, binding.Inverted ? positive : negative);
                    AddCode(desired, composite.Positive, binding.Inverted ? negative : positive);
                }
                return string.Empty;
            }

            switch (input.Kind)
            {
                case JoyInputKind.Button:
                    if (input.Index >= MaxButtons)
                        return DisplayName + " reads only the first " + MaxButtons.ToString(CultureInfo.InvariantCulture) + " buttons of a device.";
                    AddCode(desired, binding.ActionId, ButtonBase + offset + input.Index);
                    return string.Empty;
                case JoyInputKind.Pov:
                    if (input.Index != 0)
                        return DisplayName + " only encodes the first hat switch.";
                    AddCode(desired, binding.ActionId, PovBase + offset + input.Direction);
                    return string.Empty;
                default:
                    return "'" + binding.ActionId + "' needs a button; axes go to the Axis.* entries.";
            }
        }

        private static void AddCode(Dictionary<string, List<int>> desired, string action, int code)
        {
            List<int> codes;
            if (!desired.TryGetValue(action, out codes))
            {
                codes = new List<int>();
                desired[action] = codes;
            }
            if (!codes.Contains(code))
                codes.Add(code);
        }

        private string ResolveDevice(ArmaProfile profile, int offset, GameDeviceDirectory devices, List<string> warnings)
        {
            ArmaJoystick joystick = profile.Joysticks.FirstOrDefault(j => j.Offset == offset);
            if (joystick == null)
            {
                warnings.Add(DisplayName + " code uses joystick offset " + offset.ToString(CultureInfo.InvariantCulture) + ", which is not in JoysticksList.");
                return string.Empty;
            }

            GameDevice connected = devices == null ? null : devices.FindByInstanceGuid(joystick.Guid);
            return connected != null ? connected.JoyProName : GameDeviceDirectory.BuildJoyProName(joystick.Name, joystick.Guid);
        }

        public void RecordImport(string configPath, List<GameInputBinding> applied, GameDeviceDirectory devices, GameIoResult result)
        {
            ArmaProfile profile;
            if (!ArmaProfile.TryLoad(configPath, DisplayName, result.Errors, out profile))
                return;

            Dictionary<string, List<int>> codes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (GameInputBinding binding in applied)
            {
                int offset;
                if (TryResolveOffset(binding, profile, devices, out offset).Length == 0)
                    AddDesiredCodes(binding, offset, codes);
            }

            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(configPath, result.Warnings);
            foreach (KeyValuePair<string, List<int>> entry in codes)
            {
                ArmaKeyLine line = profile.FindKey(entry.Key);
                foreach (int code in entry.Value.Where(c => line != null && line.Codes.Contains(c)))
                    GameOwnershipStore.Add(owned, entry.Key, code.ToString(CultureInfo.InvariantCulture));
            }
            GameOwnershipStore.Save(configPath, GameId, owned);
            result.FilesWritten.Add(configPath + GameOwnershipStore.Suffix);
        }

        // Sources, read only: (1) the installed game's config (every action the game defines, with the English menu names
        // of its stringtable), so the list is complete without a first start; (2) the player profile when there is one
        // (Arma writes a key line for every known action, mods included). A path given with --path is a profile or the
        // game folder. Kept: the flight actions (FlightPrefixes) and the other actions JoyPro's function list
        // (DB\Functions\<game>.tsv) uses; actions already in the list keep their entry.
        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            string profilePath = null;
            string installPath = null;
            if (!string.IsNullOrWhiteSpace(sourcePath) && Directory.Exists(sourcePath))
                installPath = sourcePath;
            else if (!string.IsNullOrWhiteSpace(sourcePath))
                profilePath = sourcePath;
            else
            {
                profilePath = DiscoverConfigPaths(result.Warnings).FirstOrDefault();
                GameInstall install = GameInstalls.Find(GameId, result.Warnings);
                installPath = install != null && install.Installed ? install.InstallPath : null;
            }

            List<string> gameActions = null;
            Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(installPath))
            {
                foreach (string pbo in ConfigPbos)
                {
                    gameActions = ArmaGameData.ConfigEntryNames(ArmaGameData.ReadPboFile(Path.Combine(installPath, pbo), "config.bin", result.Warnings), DefaultKeysClass);
                    if (gameActions != null)
                    {
                        result.Steps.Add("Read " + Path.Combine(installPath, pbo) + ": " + gameActions.Count.ToString(CultureInfo.InvariantCulture) + " actions (" + DefaultKeysClass + ").");
                        break;
                    }
                }
                foreach (string pbo in LanguagePbos)
                {
                    titles = ArmaGameData.ActionTitles(ArmaGameData.ReadPboFile(Path.Combine(installPath, pbo), "stringtable.xml", result.Warnings));
                    if (titles.Count > 0)
                        break;
                }
                if (gameActions == null)
                    result.Warnings.Add("The action list of " + DisplayName + " was not found in its config (" + string.Join(", ", ConfigPbos) + " under " + installPath + ").");
            }

            ArmaProfile profile = null;
            if (!string.IsNullOrWhiteSpace(profilePath))
            {
                List<string> profileErrors = new List<string>();
                if (!ArmaProfile.TryLoad(profilePath, DisplayName, profileErrors, out profile))
                {
                    profile = null;
                    if (gameActions == null)
                        result.Errors.AddRange(profileErrors);
                    else
                        result.Warnings.AddRange(profileErrors);
                }
            }
            if (gameActions == null && profile == null)
            {
                if (result.Errors.Count == 0)
                    result.Errors.Add(DisplayName + ": neither the game's config nor a profile was found. Set the game folder (Settings > Games) or pass --path.");
                return null;
            }

            GameCatalog catalog = new GameCatalog { GameId = GameId };
            foreach (GameAction action in current.Actions)
                catalog.Add(action);

            // JoyPro's axis relations (Axis.*) come from the code, so a list built from nothing (a release package ships
            // no catalog) has them as well; export manages only cataloged actions.
            foreach (KeyValuePair<string, Composite> composite in CompositeAxes)
            {
                if (catalog.Contains(composite.Key))
                    continue;
                string name = composite.Key.StartsWith("Axis.", StringComparison.OrdinalIgnoreCase) ? composite.Key.Substring(5) : composite.Key;
                string halves = composite.Value.Negative == composite.Value.Positive
                    ? composite.Value.Negative + ", full axis"
                    : composite.Value.Negative + " / " + composite.Value.Positive;
                catalog.Add(new GameAction { Id = composite.Key, Title = System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ") + " (" + halves + ")", IsAxis = true, Category = "Flight" });
            }

            // The halves of JoyPro's axis relations (Axis.*) are analogue actions, not buttons of their own (the continuous
            // halves); the digital ones (the composite parts the game also binds to keys) stay bindable as buttons.
            HashSet<string> compositeParts = new HashSet<string>(CompositeAxes.Values.SelectMany(c => new[] { c.Negative, c.Positive }), StringComparer.OrdinalIgnoreCase);
            HashSet<string> functionIds = FunctionListIds();
            functionIds.UnionWith(ExtraManagedActions);
            int fromGame = 0;
            if (gameActions != null)
            {
                foreach (string raw in gameActions)
                {
                    // Config names start lower case (heliCyclicLeft); the profile and the list use the capitalized form.
                    string name = raw.Length > 0 ? char.ToUpperInvariant(raw[0]) + raw.Substring(1) : raw;
                    bool flight = FlightPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                    if (name.Length == 0 || catalog.Contains(name) || (!flight && !functionIds.Contains(name)) || AnalogueHalf(compositeParts, name))
                        continue;
                    string title = ArmaGameData.Title(titles, raw) ?? ArmaGameData.Title(titles, name) ?? Humanize(name);
                    catalog.Add(new GameAction { Id = name, Title = title, IsAxis = false, Category = flight ? "Flight" : "Other" });
                    fromGame++;
                }
                result.Steps.Add("From the game's config: " + fromGame.ToString(CultureInfo.InvariantCulture) + " new action(s)" +
                                 (titles.Count > 0 ? ", names from its stringtable" : "") + ".");
            }

            int fromProfile = 0;
            if (profile != null)
            {
                foreach (ArmaKeyLine line in profile.KeyLines)
                {
                    bool flight = FlightPrefixes.Any(p => line.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                    if (catalog.Contains(line.Name) || AnalogueHalf(compositeParts, line.Name) || (!flight && !functionIds.Contains(line.Name)))
                        continue;
                    catalog.Add(new GameAction { Id = line.Name, Title = ArmaGameData.Title(titles, line.Name) ?? Humanize(line.Name), IsAxis = false, Category = flight ? "Flight (from profile)" : "Other (from profile)" });
                    fromProfile++;
                }
                result.Steps.Add("Read " + profilePath + ": " + fromProfile.ToString(CultureInfo.InvariantCulture) + " new flight action(s).");
            }
            return catalog;
        }

        // The action ids JoyPro's own function list maps for this game (DB\Functions\<GameId>.tsv, column 3).
        private HashSet<string> FunctionListIds()
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string file = Path.Combine(MainStructure.PROGPATH ?? string.Empty, "DB", "Functions", GameId + ".tsv");
            if (!File.Exists(file))
                return ids;
            foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (raw.Length == 0 || raw[0] == '#')
                    continue;
                string[] parts = raw.Split('\t');
                if (parts.Length > 2 && parts[2].Trim().Length > 0 && !parts[2].StartsWith("Axis.", StringComparison.OrdinalIgnoreCase))
                    ids.Add(parts[2].Trim());
            }
            return ids;
        }

        // A half of a JoyPro axis relation that only an axis drives (the "...Cont" continuous actions and the analogue
        // thrust/brake): not a button of its own. The digital halves (HeliForward, HeliCyclicLeft, ...) stay bindable.
        private static bool AnalogueHalf(HashSet<string> compositeParts, string name)
        {
            return compositeParts.Contains(name) &&
                   (name.IndexOf("Cont", StringComparison.OrdinalIgnoreCase) >= 0 || name.StartsWith("HeliThrottle", StringComparison.OrdinalIgnoreCase));
        }

        private static string Humanize(string name)
        {
            return Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");
        }

        // ---- array helpers for the per-device response arrays ------------------------------------------------------

        // The array's values as text tokens, padded to the given length with the engine's defaults (null when absent).
        protected static List<string> ArrayTokens(ArmaProfile profile, Guid device, string key)
        {
            ArmaJoystick joystick = profile.FindJoystick(device);
            string raw;
            if (joystick == null || !joystick.Values.TryGetValue(key, out raw))
                return null;
            raw = raw.Trim();
            if (raw.StartsWith("{", StringComparison.Ordinal))
                raw = raw.Substring(1);
            if (raw.EndsWith("}", StringComparison.Ordinal))
                raw = raw.Substring(0, raw.Length - 1);
            return raw.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        }

        protected static double TokenValue(List<string> tokens, int index, double fallback)
        {
            double value;
            return tokens != null && index < tokens.Count && double.TryParse(tokens[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        // Sets one entry; true when it changed (values compared as the game's 32-bit floats).
        protected static bool SetToken(List<string> tokens, int index, double value)
        {
            double current;
            if (double.TryParse(tokens[index], NumberStyles.Float, CultureInfo.InvariantCulture, out current) && (float)current == (float)value)
                return false;
            tokens[index] = ArmaProfile.FormatFloat(value);
            return true;
        }

        protected static string RenderArray(List<string> tokens)
        {
            return "{" + string.Join(",", tokens) + "}";
        }
    }

    internal struct ArmaCode
    {
        public JoyInputKind Kind;
        public int Offset;
        public int Index;
        public bool NegativeHalf;

        // Plain joystick codes only (device type byte 0x02 button, 0x03 axis, 0x04 POV, no combo byte). Axis locals
        // 0..7 are positive halves, 8..15 negative halves; with mergedAxes also 16..23 (full axis, maximum at the
        // positive end) and 24..31 (maximum at the negative end).
        public static bool TryDecode(int code, bool mergedAxes, out ArmaCode decoded)
        {
            decoded = new ArmaCode();
            if (code < 0 || (code >> 24) != 0)
                return false;
            int type = (code >> 16) & 0xFF;
            int offset = code & 0xFF00;
            int local = code & 0xFF;
            if (offset == 0)
                return false;
            decoded.Offset = offset;
            switch (type)
            {
                case 0x02:
                    decoded.Kind = JoyInputKind.Button;
                    decoded.Index = local;
                    return true;
                case 0x03:
                    if (local > (mergedAxes ? 31 : 15))
                        return false;
                    decoded.Kind = JoyInputKind.Axis;
                    decoded.Index = local & 7;
                    decoded.NegativeHalf = (local >= 8 && local < 16) || local >= 24;
                    return true;
                case 0x04:
                    if (local > 7)
                        return false;
                    decoded.Kind = JoyInputKind.Pov;
                    decoded.Index = local;
                    return true;
                default:
                    return false;
            }
        }
    }

    internal sealed class ArmaJoystick
    {
        public Guid Guid { get; set; }
        public string Name { get; set; }
        public int Offset { get; set; }
        public string Mode { get; set; }
        // Rows of the entry: the closing "};" and each property ("sensitivity" for sensitivity[]=...).
        public int CloseRow { get; set; }
        public string Indent { get; set; }
        public Dictionary<string, int> Rows { get; private set; }
        // Raw value text of each property (without "=" and ";").
        public Dictionary<string, string> Values { get; private set; }

        public ArmaJoystick()
        {
            Rows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Indent = "\t\t";
        }
    }

    internal sealed class ArmaKeyLine
    {
        public int LineIndex { get; set; }
        public string Indent { get; set; }
        public string Name { get; set; }
        public List<int> Codes { get; set; }
    }

    // A text profile (ParamFile) of Arma 2 / OA / 3: key lines, top-level values and JoysticksList are parsed; every
    // other byte stays.
    internal sealed class ArmaProfile
    {
        private static readonly Regex KeyLinePattern = new Regex(@"^(?<indent>\s*)key(?<name>[A-Za-z0-9_]+)\[\]=\{(?<codes>[^}]*)\};\s*$", RegexOptions.Compiled);
        private static readonly Regex ValuePattern = new Regex(@"^(?<key>[A-Za-z_][A-Za-z0-9_]*)(?<array>\[\])?=(?<value>.*);$", RegexOptions.Compiled);

        private List<string> rows;
        private string newline;
        private Encoding encoding;
        private readonly HashSet<int> changedRows = new HashSet<int>();
        private readonly Dictionary<string, int> topLevel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public List<ArmaJoystick> Joysticks { get; private set; }
        public List<ArmaKeyLine> KeyLines { get; private set; }
        // Settings changed besides key lines (device arrays, mode, game options), as "what = value" for the report.
        public List<string> SettingChanges { get; private set; }

        public int ChangedLineCount
        {
            get { return changedRows.Count; }
        }

        public static bool TryLoad(string path, string gameName, List<string> errors, out ArmaProfile profile)
        {
            profile = null;
            if (!File.Exists(path))
            {
                errors.Add(gameName + " profile not found: " + path);
                return false;
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x72 && bytes[2] == 0x61 && bytes[3] == 0x50)
            {
                errors.Add("The " + gameName + " profile is binarized (raP); JoyPro only edits text profiles.");
                return false;
            }

            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            Encoding encoding = new UTF8Encoding(bom);
            string text = encoding.GetString(bytes);
            if (bom && text.Length > 0 && text[0] == '﻿')
                text = text.Substring(1);

            profile = new ArmaProfile
            {
                encoding = encoding,
                newline = text.Contains("\r\n") ? "\r\n" : "\n",
                SettingChanges = new List<string>()
            };
            profile.rows = text.Split(new[] { profile.newline }, StringSplitOptions.None).ToList();
            profile.Parse();
            return true;
        }

        private void Parse()
        {
            Joysticks = new List<ArmaJoystick>();
            KeyLines = new List<ArmaKeyLine>();
            topLevel.Clear();
            int depth = 0;
            int joysticksDepth = -1;
            ArmaJoystick current = null;
            for (int i = 0; i < rows.Count; i++)
            {
                string row = rows[i];
                string trimmed = row.Trim();

                if (depth == 0)
                {
                    Match match = KeyLinePattern.Match(row);
                    if (match.Success)
                    {
                        List<int> codes = new List<int>();
                        foreach (string part in match.Groups["codes"].Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            int value;
                            if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                                codes.Add(value);
                        }
                        KeyLines.Add(new ArmaKeyLine { LineIndex = i, Indent = match.Groups["indent"].Value, Name = match.Groups["name"].Value, Codes = codes });
                    }
                    else
                    {
                        Match top = ValuePattern.Match(trimmed);
                        if (top.Success && top.Groups["array"].Length == 0 && !top.Groups["value"].Value.Contains("{") && !topLevel.ContainsKey(top.Groups["key"].Value))
                            topLevel[top.Groups["key"].Value] = i;
                    }
                }

                if (trimmed.StartsWith("class JoysticksList", StringComparison.Ordinal))
                    joysticksDepth = depth;
                else if (joysticksDepth >= 0 && depth == joysticksDepth + 1 && trimmed.StartsWith("class Joystick", StringComparison.Ordinal))
                    current = new ArmaJoystick { Mode = "Custom", Indent = row.Substring(0, row.Length - row.TrimStart().Length) + "\t" };
                else if (current != null && depth == joysticksDepth + 2)
                {
                    Match value = ValuePattern.Match(trimmed);
                    if (value.Success)
                    {
                        string key = value.Groups["key"].Value;
                        if (current.Rows.Count == 0)
                            current.Indent = row.Substring(0, row.Length - row.TrimStart().Length);
                        if (!current.Rows.ContainsKey(key))
                        {
                            current.Rows[key] = i;
                            current.Values[key] = value.Groups["value"].Value;
                        }
                        string raw = value.Groups["value"].Value.Trim().Trim('"');
                        switch (key)
                        {
                            case "guid":
                                Guid guid;
                                if (Guid.TryParse(raw, out guid))
                                    current.Guid = guid;
                                break;
                            case "name":
                                current.Name = raw;
                                break;
                            case "offset":
                                int offset;
                                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset))
                                    current.Offset = offset;
                                break;
                            case "mode":
                                current.Mode = raw;
                                break;
                        }
                    }
                }

                depth += row.Count(c => c == '{') - row.Count(c => c == '}');
                if (current != null && joysticksDepth >= 0 && depth <= joysticksDepth + 1 && trimmed.StartsWith("}", StringComparison.Ordinal))
                {
                    current.CloseRow = i;
                    if (current.Offset > 0 && current.Guid != Guid.Empty)
                        Joysticks.Add(current);
                    current = null;
                }
                if (joysticksDepth >= 0 && depth <= joysticksDepth && trimmed.StartsWith("}", StringComparison.Ordinal))
                    joysticksDepth = -1;
            }
        }

        public ArmaKeyLine FindKey(string actionName)
        {
            return KeyLines.FirstOrDefault(k => string.Equals(k.Name, actionName, StringComparison.OrdinalIgnoreCase));
        }

        public ArmaJoystick FindJoystick(Guid guid)
        {
            return Joysticks.FirstOrDefault(j => j.Guid == guid);
        }

        public void SetCodes(ArmaKeyLine line, List<int> codes)
        {
            line.Codes = codes;
            rows[line.LineIndex] = Render(line.Indent, line.Name, codes);
            changedRows.Add(line.LineIndex);
        }

        public void AppendKey(string actionName, List<int> codes)
        {
            int after = KeyLines.Count == 0 ? rows.Count - 1 : KeyLines.Max(k => k.LineIndex);
            string indent = KeyLines.Count == 0 ? string.Empty : KeyLines[0].Indent;
            rows.Insert(after + 1, Render(indent, actionName, codes));
            foreach (ArmaKeyLine line in KeyLines.Where(k => k.LineIndex > after))
                line.LineIndex++;
            ShiftChangedRows(after + 1);
            KeyLines.Add(new ArmaKeyLine { LineIndex = after + 1, Indent = indent, Name = actionName, Codes = codes });
            changedRows.Add(after + 1);
            // Joystick and top-level rows below moved; key line objects stay valid.
            List<ArmaKeyLine> keep = KeyLines;
            Parse();
            KeyLines = keep;
        }

        private void ShiftChangedRows(int at)
        {
            List<int> moved = changedRows.Where(r => r >= at).ToList();
            foreach (int row in moved)
                changedRows.Remove(row);
            foreach (int row in moved)
                changedRows.Add(row + 1);
        }

        // Sets a property of a JoysticksList entry ("mode", "sensitivity" for an array: value text "{...}"). A missing
        // property goes after the last present one of `after` (the game's save order), else before the entry's end.
        public bool SetJoystickValue(Guid device, string key, string value, string[] after, string description)
        {
            ArmaJoystick joystick = FindJoystick(device);
            if (joystick == null)
                return false;
            bool array = value.StartsWith("{", StringComparison.Ordinal);
            string text = key + (array ? "[]" : string.Empty) + "=" + value + ";";
            int row;
            if (joystick.Rows.TryGetValue(key, out row))
            {
                string indent = rows[row].Substring(0, rows[row].Length - rows[row].TrimStart().Length);
                if (rows[row] == indent + text)
                    return false;
                rows[row] = indent + text;
            }
            else
            {
                int at = joystick.CloseRow;
                foreach (string previous in after ?? new string[0])
                {
                    int previousRow;
                    if (joystick.Rows.TryGetValue(previous, out previousRow))
                    {
                        at = previousRow + 1;
                        break;
                    }
                }
                InsertRow(at, joystick.Indent + text);
            }
            SettingChanges.Add(description);
            Parse();
            return true;
        }

        public string TopLevelValue(string key)
        {
            int row;
            if (!topLevel.TryGetValue(key, out row))
                return null;
            Match match = ValuePattern.Match(rows[row].Trim());
            return match.Success ? match.Groups["value"].Value.Trim() : null;
        }

        // Sets a top-level "key=value;" line in place, or adds it after the last present key of `after`, else at the end.
        public bool SetTopLevel(string key, string value, string[] after, string description)
        {
            int row;
            if (topLevel.TryGetValue(key, out row))
            {
                string indent = rows[row].Substring(0, rows[row].Length - rows[row].TrimStart().Length);
                string text = indent + key + "=" + value + ";";
                if (rows[row] == text)
                    return false;
                rows[row] = text;
            }
            else
            {
                int at = -1;
                foreach (string previous in after ?? new string[0])
                {
                    int previousRow;
                    if (topLevel.TryGetValue(previous, out previousRow) && previousRow + 1 > at)
                        at = previousRow + 1;
                }
                string indent = string.Empty;
                int versionRow;
                if (topLevel.TryGetValue("version", out versionRow))
                    indent = rows[versionRow].Substring(0, rows[versionRow].Length - rows[versionRow].TrimStart().Length);
                if (at < 0)
                {
                    at = rows.Count;
                    while (at > 0 && rows[at - 1].Trim().Length == 0)
                        at--;
                }
                InsertRow(at, indent + key + "=" + value + ";");
            }
            SettingChanges.Add(description);
            Parse();
            return true;
        }

        private void InsertRow(int at, string text)
        {
            rows.Insert(at, text);
            ShiftChangedRows(at);
        }

        // A float as the game writes it (32-bit, up to 8 significant digits: 0.40000001, 0.07, 1).
        public static string FormatFloat(double value)
        {
            string text = ((float)value).ToString("G8", CultureInfo.InvariantCulture);
            if (text.IndexOf('E') >= 0)
                text = value.ToString("0.##########", CultureInfo.InvariantCulture);
            return text;
        }

        private static string Render(string indent, string name, List<int> codes)
        {
            return indent + "key" + name + "[]={" + string.Join(",", codes.Select(c => c.ToString(CultureInfo.InvariantCulture))) + "};";
        }

        public void Save(string path)
        {
            byte[] preamble = encoding.GetPreamble();
            byte[] body = encoding.GetBytes(string.Join(newline, rows));
            string temp = path + ".jptmp";
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                stream.Write(preamble, 0, preamble.Length);
                stream.Write(body, 0, body.Length);
            }
            File.Copy(temp, path, true);
            File.Delete(temp);
        }
    }
}
