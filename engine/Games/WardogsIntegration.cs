using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // WARDOGS stores joystick binds as text in GameUserSettings.ini, section [/Script/WDJoystick.WDJoystickSettings].
    // Several field names are obfuscated hashes, so fields are located relative to readable anchors
    // (see docs/games/wardogs.md). Only lines of that section change, plus the HOTAS switches of
    // [/Script/WDGame.WDUserSettings] (Enable HOTAS, Self-Centering Collective; engine ffb set --device * --game Wardogs).
    internal sealed class WardogsIntegration : IGameIntegration, IGameSettingsExport, IGameSettingsReader
    {
        public string SettingsGame { get { return WardogsGameSettings.Game; } }

        private const string SectionHeader = "[/Script/WDJoystick.WDJoystickSettings]";
        private const string ActionBindingsKey = "ActionBindings";
        private const string PositiveSuffix = ".Positive";
        private const string NegativeSuffix = ".Negative";
        private static readonly string[] AxisNames = { "Pitch", "Throttle", "Roll", "Yaw", "LookPitch", "LookYaw" };

        public string GameId { get { return "Wardogs"; } }
        public string DisplayName { get { return "WARDOGS"; } }
        public IReadOnlyList<string> Aliases { get { return new[] { "wardogs", "wd" }; } }
        public string DbFolder { get { return "Wardogs"; } }
        public bool Experimental { get { return false; } }
        public bool ExclusiveFlightSim { get { return false; } }
        public string Notes { get { return "Close WARDOGS before exporting. Joystick binds need the HOTAS option enabled in the game's controls."; } }
        public IReadOnlyList<string> ProcessNames { get { return new[] { "WardogsClient-Win64-Shipping", "WardogsClient" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        public GameFeature KeyboardBinds { get { return GameFeature.No; } }
        public GameFeature ModifierBinds { get { return GameFeature.No; } }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            List<string> paths = new List<string>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string path = Path.Combine(local, "Wardogs", "Saved", "Config", "WindowsClient", "GameUserSettings.ini");
            if (File.Exists(path))
                paths.Add(path);
            return paths;
        }

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            WardogsSection section;
            if (!WardogsSection.TryLoad(configPath, result.Errors, out section))
                return result;

            WarnIfHotasDisabled(section, result.Warnings);

            foreach (string axisName in AxisNames)
            {
                WardogsLine line = section.FindAxis(axisName);
                if (line == null)
                    continue;

                UeIniStruct axis = line.Value;
                UeIniField axisIndexField = axis.After("DeviceIdentifier");
                int axisIndex;
                string deviceId = FieldText(axis, "DeviceIdentifier");
                if (deviceId.Length > 0 && axisIndexField != null && axisIndexField.TryGetInt(out axisIndex) && axisIndex >= 0)
                {
                    string device = ResolveDevice(deviceId, devices, result.Warnings);
                    if (device.Length > 0)
                    {
                        bool inverted;
                        UeIniField invertField = axis.Get("bInvert");
                        double deadzone;
                        double sensitivity;
                        bool isInverted = invertField != null && invertField.TryGetBool(out inverted) && inverted;
                        bool hasDeadZone = TryGetDouble(axis.Get("DeadZone"), out deadzone);
                        // The HOTAS page's values become the relation's WARDOGS tuning (game:Wardogs), not its base.
                        AxisTuning tuning = new AxisTuning { Inverted = isInverted };
                        if (TryGetDouble(axis.Get("Sensitivity"), out sensitivity))
                            tuning.Native["sensitivity"] = Math.Round(sensitivity, 6);
                        if (hasDeadZone)
                            tuning.Native["deadZone"] = Math.Round(deadzone, 6);
                        result.Bindings.Add(new GameInputBinding
                        {
                            ActionId = axisName,
                            Device = device,
                            Input = GameInputTokens.AxisToken(axisIndex),
                            IsAxis = true,
                            Inverted = isInverted,
                            Deadzone = hasDeadZone ? deadzone : 0.0,
                            Tuning = tuning
                        });
                    }
                }

                bool useButtons;
                UeIniField useButtonsField = axis.After("bInvert");
                if (useButtonsField != null && useButtonsField.TryGetBool(out useButtons) && useButtons)
                {
                    AddButtonBinding(axisName + PositiveSuffix, axis.Get("Positive"), devices, result);
                    AddButtonBinding(axisName + NegativeSuffix, axis.Get("Negative"), devices, result);
                }
            }

            foreach (WardogsLine line in section.Lines.Where(l => l.Key == ActionBindingsKey && l.Value != null))
            {
                string actionName = FieldText(line.Value, "Action");
                if (actionName.Length == 0)
                    continue;
                if (catalog.Actions.Count > 0 && !catalog.Contains(actionName))
                    result.Warnings.Add("Action '" + actionName + "' is not in the WARDOGS catalog yet; run 'engine wardogs refresh-db' to add it.");
                AddButtonBinding(actionName, line.Value.Get("Binding"), devices, result);
            }

            return result;
        }

        private static void AddButtonBinding(string actionId, UeIniField buttonField, GameDeviceDirectory devices, GameScanResult result)
        {
            if (buttonField == null || buttonField.Struct == null)
                return;

            string deviceId = FieldText(buttonField.Struct, "DeviceIdentifier");
            UeIniField indexField = buttonField.Struct.After("DeviceIdentifier");
            int button;
            if (deviceId.Length == 0 || indexField == null || !indexField.TryGetInt(out button) || button < 0)
                return;

            string device = ResolveDevice(deviceId, devices, result.Warnings);
            if (device.Length == 0)
                return;

            result.Bindings.Add(new GameInputBinding
            {
                ActionId = actionId,
                Device = device,
                Input = GameInputTokens.ButtonToken(button),
                IsAxis = false
            });
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
            WardogsSection section;
            if (!WardogsSection.TryLoad(request.ConfigPath, result.Errors, out section))
                return result;

            Dictionary<string, double> settings = FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, GameId, null);
            if (request.SettingsOnly)
            {
                ApplyGameSettings(section, settings, result);
                return Finish(section, request, result);
            }

            if (!settings.ContainsKey("enableHotas"))
                WarnIfHotasDisabled(section, result.Warnings);
            RepairIdentifiers(section, request.Devices, result);

            string axisIndexKey;
            string buttonIndexKey;
            if (!section.TryLearnFieldKeys(out axisIndexKey, out buttonIndexKey))
            {
                result.Errors.Add("The WARDOGS joystick section has an unexpected layout (did a game update rename its fields?). Nothing was written.");
                return result;
            }

            Dictionary<string, GameInputBinding> desired = new Dictionary<string, GameInputBinding>(StringComparer.OrdinalIgnoreCase);
            foreach (GameInputBinding binding in request.Bindings)
            {
                string skipReason = ValidateBinding(binding, request.Catalog);
                if (skipReason.Length > 0)
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + skipReason);
                    continue;
                }

                if (desired.ContainsKey(binding.ActionId))
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("'" + binding.ActionId + "' is bound by several relations; WARDOGS takes one bind per action, kept '" + desired[binding.ActionId].RelationName + "'.");
                    continue;
                }

                desired[binding.ActionId] = binding;
            }

            // WARDOGS also has infantry, so only cataloged (helicopter) actions are released: clean on the devices the
            // export writes, nuke on the devices it does not use. Other actions keep their binds.
            HashSet<string> managedActions = new HashSet<string>(request.Catalog.Actions.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            HashSet<string> usedIdentifiers = new HashSet<string>(StringComparer.Ordinal);
            foreach (string device in desired.Values.Select(b => b.Device).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string identifier;
                if (TryBuildIdentifier(device, request.Devices, section, out identifier, new List<string>()))
                    usedIdentifiers.Add(identifier);
            }
            // Nuke releases only devices JoyPro can identify (connected); a device that is not connected is never cleared.
            Func<string, bool> release = identifier => identifier.Length > 0 &&
                (usedIdentifiers.Contains(identifier) ? request.Mode == GameExportMode.Clean
                    : request.NukeUnusedDevices && ResolveDevice(identifier, request.Devices, new List<string>()).Length > 0);

            // The inputs the profile uses ("A|identifier|axis", "B|identifier|button") and the game's cataloged slots on
            // them: "only add" skips a bind on an input another action uses (no multi-bind), the other modes clear that
            // slot (a conflict). Nothing else of the game is touched by a conflict.
            Dictionary<string, string> wantedInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameInputBinding binding in desired.Values)
            {
                string key = InputKey(binding, request.Devices, section);
                if (key.Length > 0 && !wantedInputs.ContainsKey(key))
                    wantedInputs[key] = binding.ActionId;
            }
            if (request.Mode == GameExportMode.Add)
            {
                Dictionary<string, string> gameInputs = GameInputs(section, axisIndexKey, buttonIndexKey, action => managedActions.Contains(action));
                foreach (GameInputBinding binding in desired.Values.ToList())
                {
                    string key = InputKey(binding, request.Devices, section);
                    string other;
                    if (key.Length > 0 && gameInputs.TryGetValue(key, out other) && !string.Equals(other, binding.ActionId, StringComparison.OrdinalIgnoreCase))
                    {
                        SkipMultiBind(result, binding, other);
                        desired.Remove(binding.ActionId);
                    }
                }
            }
            Func<string, string, string, string, bool> conflict = (kind, identifier, index, action) =>
            {
                string user;
                return request.Mode != GameExportMode.Add && identifier.Length > 0 &&
                       wantedInputs.TryGetValue(kind + "|" + identifier + "|" + index, out user) && !string.Equals(user, action, StringComparison.OrdinalIgnoreCase);
            };

            foreach (string axisName in AxisNames)
                ApplyAxis(section, axisName, desired, request, axisIndexKey, buttonIndexKey, release, conflict, result);

            ApplyActionBindings(section, desired, request, buttonIndexKey, action => managedActions.Contains(action), release, conflict, result);

            if (result.Errors.Count > 0)
                return result;
            ApplyGameSettings(section, settings, result);
            if (!settings.ContainsKey("enableHotas"))
                EnableHotasForBinds(section, result.BindingsWritten, result);
            else if (settings["enableHotas"] == 0 && result.BindingsWritten > 0)
                result.Warnings.Add("'Enable HOTAS' is off in the profile's WARDOGS settings: the game ignores the joystick binds until it is on.");
            return Finish(section, request, result);
        }

        // "A|identifier|axis" or "B|identifier|button" of a profile bind, "" when the device is not connected.
        private static string InputKey(GameInputBinding binding, GameDeviceDirectory devices, WardogsSection section)
        {
            string identifier;
            JoyInput input;
            if (!GameInputTokens.TryParse(binding.Input, out input) || !TryBuildIdentifier(binding.Device, devices, section, out identifier, new List<string>()))
                return string.Empty;
            return (input.Kind == JoyInputKind.Axis ? "A|" : "B|") + identifier + "|" + input.Index.ToString(CultureInfo.InvariantCulture);
        }

        // The inputs the game's cataloged slots use, with their action: axis slots, the button halves of axes that use
        // buttons (bUseButtons True), action lines.
        private static Dictionary<string, string> GameInputs(WardogsSection section, string axisIndexKey, string buttonIndexKey, Func<string, bool> managed)
        {
            Dictionary<string, string> inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Action<string, UeIniStruct, string, string> add = (kind, slot, indexKey, action) =>
            {
                string identifier = FieldText(slot, "DeviceIdentifier");
                int index;
                if (identifier.Length > 0 && int.TryParse(FieldText(slot, indexKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0 && managed(action))
                {
                    string key = kind + "|" + identifier + "|" + index.ToString(CultureInfo.InvariantCulture);
                    if (!inputs.ContainsKey(key))
                        inputs[key] = action;
                }
            };
            foreach (string axisName in AxisNames)
            {
                WardogsLine line = section.FindAxis(axisName);
                if (line == null)
                    continue;
                add("A", line.Value, axisIndexKey, axisName);
                bool useButtons;
                UeIniField useButtonsField = line.Value.After("bInvert");
                if (useButtonsField == null || !useButtonsField.TryGetBool(out useButtons) || !useButtons)
                    continue;
                foreach (string half in new[] { "Positive", "Negative" })
                {
                    UeIniField field = line.Value.Get(half);
                    if (field != null && field.Struct != null)
                        add("B", field.Struct, buttonIndexKey, axisName + (half == "Positive" ? PositiveSuffix : NegativeSuffix));
                }
            }
            foreach (WardogsLine line in section.Lines.Where(l => l.Key == ActionBindingsKey && l.Value != null))
            {
                UeIniField bound = line.Value.Get("Binding");
                if (bound != null && bound.Struct != null)
                    add("B", bound.Struct, buttonIndexKey, FieldText(line.Value, "Action"));
            }
            return inputs;
        }

        // "Only add": the profile's bind is not written, the game uses the input for another action.
        private void SkipMultiBind(GameIoResult result, GameInputBinding binding, string gameAction)
        {
            result.BindingsSkipped++;
            result.SkippedTakenInputs.Add(new ExportSkippedBind
            {
                Game = GameId,
                Device = binding.Device,
                Input = binding.Input,
                Relation = binding.RelationName,
                Action = binding.ActionId,
                GameAction = gameAction
            });
        }

        private static GameIoResult Finish(WardogsSection section, GameExportRequest request, GameIoResult result)
        {
            if (request.DryRun)
            {
                result.Steps.Add("Dry run: " + section.ChangedLineCount.ToString(CultureInfo.InvariantCulture) + " line(s) of the joystick section would change.");
                return result;
            }

            if (section.ChangedLineCount == 0)
            {
                result.Steps.Add("The WARDOGS joystick section already matches the profile.");
                return result;
            }

            GameExportSafety.BackupBeforeOverwrite(request.ConfigPath, result.Warnings);
            section.Save(request.ConfigPath);
            result.FilesWritten.Add(request.ConfigPath);
            result.Steps.Add(section.ChangedLineCount.ToString(CultureInfo.InvariantCulture) + " line(s) of the joystick section changed.");
            return result;
        }

        // Game-wide HOTAS switches (device "*"): set in place where the game wrote them; a key the file does not have
        // yet is not added (the game writes its settings section itself).
        private static void ApplyGameSettings(WardogsSection section, Dictionary<string, double> settings, GameIoResult result)
        {
            foreach (KeyValuePair<string, double> setting in settings.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                string iniKey;
                if (!WardogsGameSettings.IniKeys.TryGetValue(setting.Key, out iniKey))
                    continue;
                string value = setting.Value != 0 ? "True" : "False";
                bool? changed = section.SetUserSetting(iniKey, value);
                if (changed == null)
                    result.Warnings.Add("WARDOGS setting " + iniKey + " is not in the file yet (open Settings > GAMEPAD > HOTAS in the game once); not written.");
                else if (changed == true)
                    result.Steps.Add("Set " + iniKey + "=" + value + ".");
            }
        }

        // The HOTAS switches ApplyGameSettings writes (True/False), as the ini holds them now (engine sync).
        public Dictionary<string, double> ReadGameSettings(string configPath, List<string> warnings)
        {
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            List<string> errors = new List<string>();
            WardogsSection section;
            if (!WardogsSection.TryLoad(configPath, errors, out section))
            {
                warnings?.AddRange(errors);
                return values;
            }
            foreach (KeyValuePair<string, string> setting in WardogsGameSettings.IniKeys)
            {
                string text = section.UserSetting(setting.Value);
                if (string.Equals(text, "True", StringComparison.OrdinalIgnoreCase) || text == "1")
                    values[setting.Key] = 1;
                else if (string.Equals(text, "False", StringComparison.OrdinalIgnoreCase) || text == "0")
                    values[setting.Key] = 0;
            }
            return values;
        }

        private static string ValidateBinding(GameInputBinding binding, GameCatalog catalog)
        {
            if (binding.IsKeyboard)
                return "WARDOGS joystick binds cannot use the keyboard.";
            if (binding.Reformers != null && binding.Reformers.Count > 0)
                return "WARDOGS has no joystick modifiers.";

            JoyInput input;
            if (!GameInputTokens.TryParse(binding.Input, out input))
                return "unsupported input '" + binding.Input + "'.";
            if (input.Kind == JoyInputKind.Pov)
                return "hat switches are not supported yet (their WARDOGS encoding is unconfirmed).";

            bool axisAction = IsAxisAction(binding.ActionId);
            if (axisAction && input.Kind != JoyInputKind.Axis)
                return "'" + binding.ActionId + "' is an axis; bind an axis or use " + binding.ActionId + ".Positive/.Negative for buttons.";
            if (!axisAction && input.Kind != JoyInputKind.Button)
                return "'" + binding.ActionId + "' needs a button.";
            return string.Empty;
        }

        private static bool IsAxisAction(string actionId)
        {
            return AxisNames.Any(a => string.Equals(a, actionId, StringComparison.OrdinalIgnoreCase));
        }

        private void ApplyAxis(WardogsSection section, string axisName, Dictionary<string, GameInputBinding> desired, GameExportRequest request, string axisIndexKey, string buttonIndexKey, Func<string, bool> release, Func<string, string, string, string, bool> conflict, GameIoResult result)
        {
            WardogsLine line = section.FindAxis(axisName);
            if (line == null)
                return;

            UeIniStruct axis = line.Value;
            bool changed = false;
            GameInputBinding axisBinding;
            bool hasAxis = desired.TryGetValue(axisName, out axisBinding);
            bool currentlyBound = FieldText(axis, "DeviceIdentifier").Length > 0;

            // One slot per axis: "only add" never replaces the game's bind; merge and merge-overwrite write the
            // profile's axis into the slot (there is no second slot to keep the old one in).
            if (hasAxis && currentlyBound && KeepsBoundSlot(request.Mode) && !HoldsBind(axis, axisBinding, request, section, axisIndexKey))
            {
                result.BindingsSkipped++;
                result.Warnings.Add("Relation '" + axisBinding.RelationName + "' (" + axisName + "): WARDOGS has one slot for this axis and it is bound; " + ModeName(request.Mode) + " keeps the game's bind.");
            }
            else if (hasAxis)
            {
                string identifier;
                if (TryBuildIdentifier(axisBinding.Device, request.Devices, section, out identifier, result.Warnings))
                {
                    JoyInput input;
                    GameInputTokens.TryParse(axisBinding.Input, out input);
                    changed |= SetQuoted(axis.Get("DeviceIdentifier"), identifier);
                    changed |= SetRaw(axis.Get(axisIndexKey), input.Index.ToString(CultureInfo.InvariantCulture));
                    changed |= SetRaw(axis.Get("bInvert"), axisBinding.Inverted ? "True" : "False");
                    changed |= ApplyResponse(axis, axisBinding, axisName, result);
                    result.BindingsWritten++;
                }
                else
                {
                    result.BindingsSkipped++;
                }
            }
            else if (release(FieldText(axis, "DeviceIdentifier")) ||
                     conflict("A", FieldText(axis, "DeviceIdentifier"), FieldText(axis, axisIndexKey), axisName))
            {
                changed |= SetQuoted(axis.Get("DeviceIdentifier"), string.Empty);
            }

            // The button halves: a half the profile binds is written, another one goes when its device is released or
            // when the profile puts another action on its button (a conflict; only while the axis uses its buttons).
            GameInputBinding positive;
            GameInputBinding negative;
            bool hasPositive = desired.TryGetValue(axisName + PositiveSuffix, out positive);
            bool hasNegative = desired.TryGetValue(axisName + NegativeSuffix, out negative);
            UeIniField useButtons = axis.After("bInvert");
            bool useButtonsValue;
            bool buttonsActive = useButtons != null && useButtons.TryGetBool(out useButtonsValue) && useButtonsValue;
            bool cleared = false;
            foreach (string half in new[] { "Positive", "Negative" })
            {
                UeIniField field = axis.Get(half);
                GameInputBinding wanted = half == "Positive" ? positive : negative;
                if (wanted != null)
                    changed |= ApplyButtonStruct(field, wanted, request, section, buttonIndexKey, result);
                else if (field != null && field.Struct != null &&
                         (release(FieldText(field.Struct, "DeviceIdentifier")) ||
                          (buttonsActive && conflict("B", FieldText(field.Struct, "DeviceIdentifier"), FieldText(field.Struct, buttonIndexKey), axisName + (half == "Positive" ? PositiveSuffix : NegativeSuffix)))))
                    cleared |= ClearButtonStruct(field, buttonIndexKey);
            }
            if (hasPositive || hasNegative)
                changed |= SetRaw(useButtons, "True");
            else
            {
                bool anyHalfBound = new[] { "Positive", "Negative" }.Any(h => axis.Get(h) != null && axis.Get(h).Struct != null && FieldText(axis.Get(h).Struct, "DeviceIdentifier").Length > 0);
                if (cleared && !anyHalfBound)
                    cleared |= SetRaw(useButtons, "False");
            }
            changed |= cleared;

            if (changed)
                line.MarkChanged();
        }

        // Sensitivity and DeadZone of the axis line, in the HOTAS page's units (wardogs axis model). Both are always
        // written (a dead zone of 0 too), rounded to the menu's two decimals and kept in the menu's ranges. A binding
        // without a resolved tuning (callers outside the profile bridge) keeps the old rule: dead zone only when > 0.
        private static bool ApplyResponse(UeIniStruct axis, GameInputBinding binding, string axisName, GameIoResult result)
        {
            AxisTuning tuning = binding.Tuning;
            if (tuning == null)
                return binding.Deadzone > 0 && SetRaw(axis.Get("DeadZone"), GameText.FormatFloat(binding.Deadzone, 6));

            double sensitivity = Math.Round(WardogsAxisModel.Sensitivity(tuning), 2);
            double deadZone = Math.Round(WardogsAxisModel.DeadZone(tuning), 2);
            bool changed = SetRaw(axis.Get("Sensitivity"), GameText.FormatFloat(sensitivity, 6));
            changed |= SetRaw(axis.Get("DeadZone"), GameText.FormatFloat(deadZone, 6));
            if (axis.Get("Sensitivity") == null || axis.Get("DeadZone") == null)
                result.Warnings.Add("The WARDOGS " + axisName + " line has no Sensitivity/DeadZone field; the response was not written.");
            if (tuning.IsUserCurve || (tuning.Curvature != null && tuning.Curvature.Count > 0 && Math.Abs(tuning.Curvature[0]) > 1e-9))
                result.Warnings.Add("Relation '" + binding.RelationName + "' (" + axisName + "): WARDOGS has no response curve; only sensitivity " +
                                    AxisTuning.Num(sensitivity) + " and dead zone " + AxisTuning.Num(deadZone) + " are written, the curvature is not.");
            if (tuning.Slider && deadZone > 0 && axisName == "Throttle")
                result.Warnings.Add("Relation '" + binding.RelationName + "': a WARDOGS dead zone on the collective probably sits at mid travel (the game treats it as a centred axis); a throttle lever gets a flat spot there.");
            return changed;
        }

        private static bool ApplyButtonStruct(UeIniField field, GameInputBinding binding, GameExportRequest request, WardogsSection section, string buttonIndexKey, GameIoResult result)
        {
            if (field == null || field.Struct == null || binding == null)
                return false;
            bool currentlyBound = FieldText(field.Struct, "DeviceIdentifier").Length > 0;
            // One slot per axis half: merge keeps the game's bind, and add never removes one.
            if (currentlyBound && KeepsBoundSlot(request.Mode))
            {
                if (!HoldsBind(field.Struct, binding, request, section, buttonIndexKey))
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): WARDOGS has one slot for it and it is bound; " + ModeName(request.Mode) + " keeps the game's bind.");
                }
                return false;
            }

            string identifier;
            if (!TryBuildIdentifier(binding.Device, request.Devices, section, out identifier, result.Warnings))
            {
                result.BindingsSkipped++;
                return false;
            }

            JoyInput input;
            GameInputTokens.TryParse(binding.Input, out input);
            bool changed = SetQuoted(field.Struct.Get("DeviceIdentifier"), identifier);
            changed |= SetRaw(field.Struct.Get(buttonIndexKey), input.Index.ToString(CultureInfo.InvariantCulture));
            result.BindingsWritten++;
            return changed;
        }

        // "Only add" never replaces a bound slot; merge updates it (it keeps the game's other binds, not this slot's).
        private static bool KeepsBoundSlot(GameExportMode mode)
        {
            return mode == GameExportMode.Add;
        }

        private static string ModeName(GameExportMode mode)
        {
            return "add";
        }

        // The slot already holds this bind (re-export of the same profile): not a conflict.
        private static bool HoldsBind(UeIniStruct slot, GameInputBinding binding, GameExportRequest request, WardogsSection section, string indexKey)
        {
            string identifier;
            JoyInput input;
            return TryBuildIdentifier(binding.Device, request.Devices, section, out identifier, new List<string>()) &&
                   GameInputTokens.TryParse(binding.Input, out input) &&
                   string.Equals(FieldText(slot, "DeviceIdentifier"), identifier, StringComparison.OrdinalIgnoreCase) &&
                   FieldText(slot, indexKey) == input.Index.ToString(CultureInfo.InvariantCulture);
        }

        private static bool ClearButtonStruct(UeIniField field, string buttonIndexKey)
        {
            if (field == null || field.Struct == null)
                return false;
            bool changed = SetQuoted(field.Struct.Get("DeviceIdentifier"), string.Empty);
            changed |= SetRaw(field.Struct.Get(buttonIndexKey), "-1");
            return changed;
        }

        private void ApplyActionBindings(WardogsSection section, Dictionary<string, GameInputBinding> desired, GameExportRequest request, string buttonIndexKey, Func<string, bool> managed, Func<string, bool> release, Func<string, string, string, string, bool> conflict, GameIoResult result)
        {
            HashSet<string> handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (WardogsLine line in section.Lines.Where(l => l.Key == ActionBindingsKey && l.Value != null).ToList())
            {
                string actionName = FieldText(line.Value, "Action");
                GameInputBinding binding;
                if (desired.TryGetValue(actionName, out binding) && !IsAxisAction(actionName) && !actionName.Contains("."))
                {
                    // A second line of the action (left by an older add export): clean / merge-overwrite keep one, merge
                    // and add leave it as it is.
                    if (handled.Contains(actionName))
                    {
                        if (request.Mode == GameExportMode.Clean || request.Mode == GameExportMode.MergeOverwrite)
                            section.Remove(line);
                        continue;
                    }
                    handled.Add(actionName);
                    // One bind per action (the game shows and uses one): "only add" never replaces it, merge updates it.
                    if (request.Mode == GameExportMode.Add)
                    {
                        if (!LineHolds(line, binding, request, section, buttonIndexKey))
                        {
                            result.ConflictsKept++;
                            result.Warnings.Add("Relation '" + binding.RelationName + "' (" + actionName + "): WARDOGS has one bind per action and it is bound; " + ModeName(request.Mode) + " keeps the game's bind.");
                        }
                        continue;
                    }

                    string identifier;
                    if (!TryBuildIdentifier(binding.Device, request.Devices, section, out identifier, result.Warnings))
                    {
                        result.BindingsSkipped++;
                        continue;
                    }
                    UeIniField bindingField = line.Value.Get("Binding");
                    JoyInput input;
                    GameInputTokens.TryParse(binding.Input, out input);
                    if (bindingField == null || bindingField.Struct == null || bindingField.Struct.Get(buttonIndexKey) == null)
                    {
                        section.ReplaceActionBinding(line, actionName, identifier, buttonIndexKey, input.Index);
                    }
                    else
                    {
                        bool changed = SetQuoted(bindingField.Struct.Get("DeviceIdentifier"), identifier);
                        changed |= SetRaw(bindingField.Struct.Get(buttonIndexKey), input.Index.ToString(CultureInfo.InvariantCulture));
                        if (changed)
                            line.MarkChanged();
                    }
                    result.BindingsWritten++;
                }
                else if (managed(actionName))
                {
                    UeIniField bound = line.Value.Get("Binding");
                    // Released device, or the profile puts another action on this button (a conflict).
                    if (bound != null && bound.Struct != null &&
                        (release(FieldText(bound.Struct, "DeviceIdentifier")) ||
                         conflict("B", FieldText(bound.Struct, "DeviceIdentifier"), FieldText(bound.Struct, buttonIndexKey), actionName)))
                        section.Remove(line);
                }
            }

            foreach (KeyValuePair<string, GameInputBinding> entry in desired.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (IsAxisAction(entry.Key) || entry.Key.Contains(".") || handled.Contains(entry.Key))
                    continue;

                string identifier;
                if (!TryBuildIdentifier(entry.Value.Device, request.Devices, section, out identifier, result.Warnings))
                {
                    result.BindingsSkipped++;
                    continue;
                }

                JoyInput input;
                GameInputTokens.TryParse(entry.Value.Input, out input);
                section.AppendActionBinding(entry.Key, identifier, buttonIndexKey, input.Index);
                result.BindingsWritten++;
            }
        }

        private static bool LineHolds(WardogsLine line, GameInputBinding binding, GameExportRequest request, WardogsSection section, string buttonIndexKey)
        {
            UeIniField bound = line.Value.Get("Binding");
            return bound != null && bound.Struct != null && HoldsBind(bound.Struct, binding, request, section, buttonIndexKey);
        }

        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            // The action enum lives in the encrypted game binaries; the user's own ini is the only readable source.
            string path = string.IsNullOrWhiteSpace(sourcePath) ? DiscoverConfigPaths(result.Warnings).FirstOrDefault() : sourcePath;
            WardogsSection section;
            if (string.IsNullOrWhiteSpace(path) || !WardogsSection.TryLoad(path, result.Errors, out section))
                return null;

            GameCatalog catalog = new GameCatalog { GameId = GameId };
            foreach (GameAction action in current.Actions)
                catalog.Add(action);

            // The axes and their button halves come from the code, so a list built from nothing (a release package ships
            // no catalog) has them as well.
            foreach (string axis in AxisNames)
            {
                string category = axis.StartsWith("Look", StringComparison.Ordinal) ? "Camera" : "Flight";
                if (!catalog.Contains(axis))
                    catalog.Add(new GameAction { Id = axis, Title = SplitCamelCase(axis), IsAxis = true, Category = category });
                if (!catalog.Contains(axis + PositiveSuffix))
                    catalog.Add(new GameAction { Id = axis + PositiveSuffix, Title = SplitCamelCase(axis) + " positive (button)", IsAxis = false, Category = category });
                if (!catalog.Contains(axis + NegativeSuffix))
                    catalog.Add(new GameAction { Id = axis + NegativeSuffix, Title = SplitCamelCase(axis) + " negative (button)", IsAxis = false, Category = category });
            }

            int added = 0;
            foreach (WardogsLine line in section.Lines.Where(l => l.Key == ActionBindingsKey && l.Value != null))
            {
                string actionName = FieldText(line.Value, "Action");
                if (actionName.Length == 0 || catalog.Contains(actionName))
                    continue;
                catalog.Add(new GameAction { Id = actionName, Title = SplitCamelCase(actionName), IsAxis = false, Category = "Flight" });
                added++;
            }

            result.Steps.Add("Read " + path + ": " + added.ToString(CultureInfo.InvariantCulture) + " new action(s).");
            return catalog;
        }

        private static void WarnIfHotasDisabled(WardogsSection section, List<string> warnings)
        {
            if (section.HotasEnabled == false)
                warnings.Add("WARDOGS setting bUseHOTASHelicopters is False; enable HOTAS in the game's controls if joystick input does not react.");
        }

        // With HOTAS off the game ignores (and does not even list) the joystick binds: an export that writes binds
        // turns the game's "Enable HOTAS" switch on.
        private static void EnableHotasForBinds(WardogsSection section, int bindsWritten, GameIoResult result)
        {
            if (bindsWritten > 0 && section.HotasEnabled == false && section.EnableHotas())
                result.Steps.Add("Turned on 'Enable HOTAS' (bUseHOTASHelicopters), so WARDOGS uses the joystick binds.");
        }

        // DeviceIdentifier is "VID:PID:<HID product string>" (never trimmed). The HID string comes from the
        // connected device; an identifier already in the file for the same VID:PID is reused verbatim.
        private static bool TryBuildIdentifier(string joyProDevice, GameDeviceDirectory devices, WardogsSection section, out string identifier, List<string> warnings)
        {
            identifier = string.Empty;
            GameDevice device = devices == null ? null : devices.FindByJoyProName(joyProDevice);
            if (device == null || !device.HasVidPid)
            {
                warnings.Add("Device '" + joyProDevice + "' is not connected, so its WARDOGS identifier (VID:PID:name) is unknown; connect it and export again.");
                return false;
            }

            string prefix = GameDeviceDirectory.VidPidKey(device.VendorId, device.ProductId) + ":";
            string name = WardogsDeviceName(device);
            if (string.IsNullOrEmpty(name))
            {
                warnings.Add("The DirectInput name of '" + joyProDevice + "' could not be read; WARDOGS needs it in the device identifier.");
                return false;
            }
            if (name.Any(c => c > 127))
            {
                warnings.Add("'" + name + "' contains non-ASCII characters; writing it would make WARDOGS save its ini as UTF-16. Skipped.");
                return false;
            }

            identifier = prefix + name;
            return true;
        }

        private static string ResolveDevice(string identifier, GameDeviceDirectory devices, List<string> warnings)
        {
            string[] parts = identifier.Split(new[] { ':' }, 3);
            int vid;
            int pid;
            if (parts.Length < 3 ||
                !int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid) ||
                !int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid))
            {
                warnings.Add("Unrecognized WARDOGS device identifier '" + identifier + "'.");
                return string.Empty;
            }

            List<GameDevice> matches = devices == null ? new List<GameDevice>() : devices.FindByVidPid(vid, pid);
            GameDevice exact = matches.FirstOrDefault(d => string.Equals(WardogsDeviceName(d), parts[2], StringComparison.Ordinal)) ??
                               matches.FirstOrDefault(d => string.Equals(d.HidProductString, parts[2], StringComparison.Ordinal)) ??
                               matches.FirstOrDefault();
            if (exact == null)
            {
                warnings.Add("WARDOGS device '" + identifier + "' is not connected; its binds were not imported.");
                return string.Empty;
            }
            if (matches.Count > 1)
                warnings.Add("Several connected devices match WARDOGS device '" + identifier + "'; used '" + exact.JoyProName + "'.");
            return exact.JoyProName;
        }

        // WARDOGS names a device "VID:PID:<DirectInput instance name>" (the game wrote "1234:BEAD:vJoy Device" for vJoy,
        // whose HID product string is "vJoy - Virtual Joystick"); a name it does not know shows "NOT CONNECTED".
        private static string WardogsDeviceName(GameDevice device)
        {
            if (device == null)
                return string.Empty;
            return string.IsNullOrEmpty(device.InstanceName) ? (device.HidProductString ?? string.Empty) : device.InstanceName;
        }

        // Identifiers whose VID:PID belongs to one connected device but whose name is not the one WARDOGS uses (older
        // JoyPro versions wrote the HID product string) get the right name, so the game finds the device again.
        private static void RepairIdentifiers(WardogsSection section, GameDeviceDirectory devices, GameIoResult result)
        {
            if (devices == null)
                return;
            foreach (string identifier in section.AllDeviceIdentifiers().Distinct(StringComparer.Ordinal).ToList())
            {
                string[] parts = identifier.Split(new[] { ':' }, 3);
                int vid, pid;
                if (parts.Length < 3 ||
                    !int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid) ||
                    !int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid))
                    continue;
                List<string> names = devices.FindByVidPid(vid, pid).Select(WardogsDeviceName).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                if (names.Count != 1 || names[0] == parts[2] || names[0].Any(c => c > 127))
                    continue;
                string fixedId = parts[0] + ":" + parts[1] + ":" + names[0];
                if (section.RenameIdentifier(identifier, fixedId) > 0)
                    result.Warnings.Add("Fixed the WARDOGS device name '" + identifier + "' -> '" + fixedId + "' (the game showed it as not connected).");
            }
        }

        private static string FieldText(UeIniStruct value, string key)
        {
            UeIniField field = value == null ? null : value.Get(key);
            return field == null ? string.Empty : field.Text;
        }

        private static bool TryGetDouble(UeIniField field, out double value)
        {
            value = 0;
            return field != null && double.TryParse(field.Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool SetQuoted(UeIniField field, string value)
        {
            if (field == null)
                return false;
            string raw = "\"" + value + "\"";
            if (field.Raw == raw)
                return false;
            field.Raw = raw;
            return true;
        }

        private static bool SetRaw(UeIniField field, string value)
        {
            if (field == null || field.Raw == value)
                return false;
            field.Raw = value;
            return true;
        }

        private static string SplitCamelCase(string value)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                if (i > 0 && char.IsUpper(value[i]) && !char.IsUpper(value[i - 1]))
                    sb.Append(' ');
                sb.Append(value[i]);
            }
            return sb.ToString();
        }
    }

    internal sealed class WardogsLine
    {
        public string Key { get; set; }
        public string OriginalText { get; set; }
        public UeIniStruct Value { get; set; }
        public bool Changed { get; private set; }
        public bool Removed { get; set; }

        public void MarkChanged()
        {
            Changed = true;
        }

        public string Render()
        {
            return Changed && Value != null ? Key + "=" + Value.ToString() : OriginalText;
        }
    }

    // The joystick section of GameUserSettings.ini; everything outside of it is kept as raw text.
    internal sealed class WardogsSection
    {
        private List<string> before;
        private List<string> after;
        private string newline;
        private Encoding encoding;
        private int addedLines;

        public List<WardogsLine> Lines { get; private set; }
        public bool? HotasEnabled { get; private set; }

        public int ChangedLineCount
        {
            get { return Lines.Count(l => l.Changed || l.Removed) + addedLines + changedSettingRows.Count; }
        }

        private readonly HashSet<string> changedSettingRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Sets bUseHOTASHelicopters=True (it lives in another section of the same file).
        public bool EnableHotas()
        {
            return SetUserSetting("bUseHOTASHelicopters", "True") == true;
        }

        // Sets "<key>=<value>" in [/Script/WDGame.WDUserSettings] (outside the joystick section). Returns null when the
        // file has no such row, false when it already has the value.
        public bool? SetUserSetting(string key, string value)
        {
            string prefix = key + "=";
            List<string> rows = before;
            int start = rows.FindIndex(r => string.Equals(r.Trim(), "[/Script/WDGame.WDUserSettings]", StringComparison.OrdinalIgnoreCase));
            if (start < 0)
            {
                rows = after;
                start = rows.FindIndex(r => string.Equals(r.Trim(), "[/Script/WDGame.WDUserSettings]", StringComparison.OrdinalIgnoreCase));
            }
            if (start < 0)
                return null;
            for (int i = start + 1; i < rows.Count && !rows[i].StartsWith("[", StringComparison.Ordinal); i++)
            {
                if (!rows[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(rows[i].Substring(prefix.Length).Trim(), value, StringComparison.OrdinalIgnoreCase))
                    return false;
                rows[i] = rows[i].Substring(0, prefix.Length) + value;
                changedSettingRows.Add(key);
                if (string.Equals(key, "bUseHOTASHelicopters", StringComparison.OrdinalIgnoreCase))
                    HotasEnabled = string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
                return true;
            }
            return null;
        }

        // The value of "<key>=" in [/Script/WDGame.WDUserSettings]; null when the file has no such row.
        public string UserSetting(string key)
        {
            string prefix = key + "=";
            foreach (List<string> rows in new[] { before, after })
            {
                int start = rows.FindIndex(r => string.Equals(r.Trim(), "[/Script/WDGame.WDUserSettings]", StringComparison.OrdinalIgnoreCase));
                for (int i = start + 1; start >= 0 && i < rows.Count && !rows[i].StartsWith("[", StringComparison.Ordinal); i++)
                {
                    if (rows[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return rows[i].Substring(prefix.Length).Trim();
                }
                if (start >= 0)
                    return null;
            }
            return null;
        }

        public static bool TryLoad(string path, List<string> errors, out WardogsSection section)
        {
            section = null;
            if (!File.Exists(path))
            {
                errors.Add("WARDOGS config not found: " + path);
                return false;
            }

            byte[] bytes = File.ReadAllBytes(path);
            Encoding encoding = DetectEncoding(bytes);
            string text = encoding.GetString(bytes);
            if (text.Length > 0 && text[0] == '﻿')
                text = text.Substring(1);
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            string[] rows = text.Split(new[] { newline }, StringSplitOptions.None);

            int start = Array.FindIndex(rows, r => string.Equals(r.Trim(), "[/Script/WDJoystick.WDJoystickSettings]", StringComparison.OrdinalIgnoreCase));
            if (start < 0)
            {
                errors.Add("WARDOGS has no joystick section yet. Open the HOTAS settings in the game once, close the game, then try again.");
                return false;
            }

            int end = start + 1;
            while (end < rows.Length && !rows[end].StartsWith("[", StringComparison.Ordinal))
                end++;
            // Blank lines before the next section header stay outside the section.
            int lastContent = end - 1;
            while (lastContent > start && rows[lastContent].Trim().Length == 0)
                lastContent--;

            section = new WardogsSection
            {
                encoding = encoding,
                newline = newline,
                before = rows.Take(start + 1).ToList(),
                after = rows.Skip(lastContent + 1).ToList(),
                Lines = new List<WardogsLine>()
            };

            for (int i = start + 1; i <= lastContent; i++)
            {
                string row = rows[i];
                int equals = row.IndexOf('=');
                WardogsLine line = new WardogsLine { OriginalText = row, Key = equals > 0 ? row.Substring(0, equals) : string.Empty };
                UeIniStruct value;
                if (equals > 0 && UeIniStruct.TryParse(row.Substring(equals + 1), out value))
                    line.Value = value;
                section.Lines.Add(line);
            }

            foreach (string row in rows)
            {
                if (row.StartsWith("bUseHOTASHelicopters=", StringComparison.OrdinalIgnoreCase))
                    section.HotasEnabled = row.EndsWith("True", StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        private static Encoding DetectEncoding(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return new UnicodeEncoding(false, true);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(true);
            return new UTF8Encoding(false);
        }

        public WardogsLine FindAxis(string axisName)
        {
            return Lines.FirstOrDefault(l => l.Value != null && string.Equals(l.Key, axisName, StringComparison.Ordinal));
        }

        // Sets every DeviceIdentifier equal to $from to $to; returns how many fields changed.
        public int RenameIdentifier(string from, string to)
        {
            int count = 0;
            foreach (WardogsLine line in Lines.Where(l => l.Value != null && !l.Removed))
            {
                int changed = RenameIn(line.Value, from, to);
                if (changed > 0)
                {
                    line.MarkChanged();
                    count += changed;
                }
            }
            return count;
        }

        private static int RenameIn(UeIniStruct value, string from, string to)
        {
            int count = 0;
            foreach (UeIniField field in value.Fields)
            {
                if (field.Struct != null)
                    count += RenameIn(field.Struct, from, to);
                else if (field.Key == "DeviceIdentifier" && field.Text == from)
                {
                    field.Raw = "\"" + to + "\"";
                    count++;
                }
            }
            return count;
        }

        public IEnumerable<string> AllDeviceIdentifiers()
        {
            foreach (WardogsLine line in Lines.Where(l => l.Value != null && !l.Removed))
            {
                foreach (string id in Identifiers(line.Value))
                    yield return id;
            }
        }

        private static IEnumerable<string> Identifiers(UeIniStruct value)
        {
            foreach (UeIniField field in value.Fields)
            {
                if (field.Struct != null)
                {
                    foreach (string nested in Identifiers(field.Struct))
                        yield return nested;
                }
                else if (field.Key == "DeviceIdentifier" && field.Text.Length > 0)
                {
                    yield return field.Text;
                }
            }
        }

        // The obfuscated keys of the axis index and the button index, read from the first axis line.
        public bool TryLearnFieldKeys(out string axisIndexKey, out string buttonIndexKey)
        {
            axisIndexKey = string.Empty;
            buttonIndexKey = string.Empty;
            WardogsLine axisLine = Lines.FirstOrDefault(l => l.Value != null && l.Value.Get("bInvert") != null && l.Value.Get("Positive") != null);
            if (axisLine == null)
                return false;

            UeIniField axisIndex = axisLine.Value.After("DeviceIdentifier");
            UeIniField positive = axisLine.Value.Get("Positive");
            UeIniField buttonIndex = positive.Struct == null ? null : positive.Struct.After("DeviceIdentifier");
            int ignored;
            if (axisIndex == null || !axisIndex.TryGetInt(out ignored) || buttonIndex == null || !buttonIndex.TryGetInt(out ignored))
                return false;

            axisIndexKey = axisIndex.Key;
            buttonIndexKey = buttonIndex.Key;
            return true;
        }

        public void Remove(WardogsLine line)
        {
            line.Removed = true;
        }

        public void ReplaceActionBinding(WardogsLine line, string action, string identifier, string buttonIndexKey, int button)
        {
            UeIniStruct value;
            UeIniStruct.TryParse(BuildActionBinding(action, identifier, buttonIndexKey, button), out value);
            line.Value = value;
            line.MarkChanged();
        }

        public void AppendActionBinding(string action, string identifier, string buttonIndexKey, int button)
        {
            string text = BuildActionBinding(action, identifier, buttonIndexKey, button);
            UeIniStruct value;
            UeIniStruct.TryParse(text, out value);
            WardogsLine line = new WardogsLine { Key = "ActionBindings", OriginalText = "ActionBindings=" + text, Value = value };
            int lastBinding = Lines.FindLastIndex(l => l.Key == "ActionBindings");
            int lastAxis = Lines.FindLastIndex(l => l.Value != null && l.Value.Get("bInvert") != null);
            int insertAt = Math.Max(lastBinding, lastAxis) + 1;
            Lines.Insert(insertAt, line);
            addedLines++;
        }

        private static string BuildActionBinding(string action, string identifier, string buttonIndexKey, int button)
        {
            return "(Action=" + action + ",Binding=(DeviceIdentifier=\"" + identifier + "\"," + buttonIndexKey + "=" +
                   button.ToString(CultureInfo.InvariantCulture) + "))";
        }

        public void Save(string path)
        {
            List<string> rows = new List<string>(before);
            rows.AddRange(Lines.Where(l => !l.Removed).Select(l => l.Render()));
            rows.AddRange(after);
            byte[] preamble = encoding.GetPreamble();
            byte[] body = encoding.GetBytes(string.Join(newline, rows));
            string temp = path + ".jptmp";
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                stream.Write(preamble, 0, preamble.Length);
                stream.Write(body, 0, body.Length);
            }
            SafeFileIo.CopyOverAndDelete(temp, path);
        }
    }
}
