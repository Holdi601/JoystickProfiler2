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
    // War Thunder keeps controls as text BLK in Saves\<user>\production\machine.blk (controls{...} at the top), or in
    // an exported preset. Only hotkeys{} blocks of cataloged flight IDs and axes{} entries of cataloged flight axes
    // are changed; deviceMapping{} is only read, to translate between device-local and War Thunder's global indices.
    // See docs/games/war-thunder.md.
    internal sealed class WarThunderIntegration : IGameIntegration, IGameSettingsFile
    {
        public string GameId { get { return "WarThunder"; } }
        public string DisplayName { get { return "War Thunder"; } }
        public IReadOnlyList<string> Aliases { get { return new[] { "warthunder", "wt" }; } }
        public string DbFolder { get { return "WarThunder"; } }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return false; } }
        public string Notes { get { return "Experimental. Close War Thunder before exporting. Each device must have been listed by the game once (connect it and open Controls)."; } }
        public IReadOnlyList<string> ProcessNames { get { return new[] { "aces", "aces_be" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        public GameFeature KeyboardBinds { get { return GameFeature.Yes; } }
        public GameFeature ModifierBinds { get { return GameFeature.Yes; } }

        private static readonly int[] PovDirectionToButton = { 0, -1, 1, -1, 2, -1, 3, -1 };
        private static readonly int[] PovButtonToDirection = { 0, 2, 4, 6 };

        private sealed class WtDevice
        {
            public string DevId;
            public string Name;
            public int AxesOffset;
            public int AxesCount;
            public int ButtonsOffset;
            public int ButtonsCount;
        }

        private sealed class WtContext
        {
            public BlkDocument Document;
            public string[] ControlsPath;
            public List<WtDevice> Devices = new List<WtDevice>();
            // controls{basePresetPaths{default}} as a preset name ("hotkey.keyboard_ver1"); the file holds only the
            // differences from it.
            public string BasePreset = string.Empty;

            // machine.blk has content{controls{}} and the game-wide settings beside it; an exported preset only controls{}.
            public bool IsMachineBlk
            {
                get { return ControlsPath.Length == 2; }
            }
        }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            string saves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "WarThunder", "Saves");
            if (!Directory.Exists(saves))
                return new List<string>();
            // Saves\<uid> is the account the game loads (Saves\lastlogin.blk "uid:i64=<uid>"); Saves\last is a copy the
            // game keeps from before the login. The logged-in account comes first, then the newest file.
            string account = LastLoginUid(saves);
            return Directory.GetDirectories(saves)
                .Select(d => Path.Combine(d, "production", "machine.blk"))
                .Where(File.Exists)
                .OrderByDescending(p => account != null && string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(p))), account, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        }

        private static string LastLoginUid(string saves)
        {
            try
            {
                string file = Path.Combine(saves, "lastlogin.blk");
                if (!File.Exists(file))
                    return null;
                Match m = Regex.Match(File.ReadAllText(file), @"\buid:i64\s*=\s*(\d+)");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static bool TryOpen(string path, List<string> errors, out WtContext context)
        {
            context = null;
            BlkDocument document;
            if (!BlkDocument.TryLoad(path, errors, out document))
                return false;

            string[] controlsPath = document.FindBlock("controls") != null
                ? new[] { "controls" }
                : document.FindBlock("content", "controls") != null ? new[] { "content", "controls" } : null;
            if (controlsPath == null)
            {
                errors.Add("No controls{} block found in " + path + ".");
                return false;
            }

            context = new WtContext { Document = document, ControlsPath = controlsPath };
            BlkBlock basePresets = document.FindBlock(controlsPath.Concat(new[] { "basePresetPaths" }).ToArray());
            BlkParam basePreset = basePresets == null ? null : document.Params(basePresets).FirstOrDefault(p => p.Name == "default");
            context.BasePreset = WarThunderAxisSettings.PresetName(basePreset == null ? string.Empty : Unquote(basePreset.Value));
            BlkBlock mapping = document.FindBlock(controlsPath.Concat(new[] { "deviceMapping" }).ToArray());
            if (mapping != null)
            {
                foreach (BlkChild joystick in document.Children(mapping).Where(c => c.Kind == BlkChildKind.Block && c.Name == "joystick"))
                {
                    context.Devices.Add(new WtDevice
                    {
                        DevId = Unquote(Value(joystick, "devId")),
                        Name = Unquote(Value(joystick, "name")),
                        AxesOffset = Int(joystick, "axesOffset"),
                        AxesCount = Int(joystick, "axesCount"),
                        ButtonsOffset = Int(joystick, "buttonsOffset"),
                        ButtonsCount = Int(joystick, "buttonsCount")
                    });
                }
            }
            return true;
        }

        private static BlkBlock Section(WtContext context, string name)
        {
            return context.Document.FindBlock(context.ControlsPath.Concat(new[] { name }).ToArray());
        }

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            WtContext context;
            if (!TryOpen(configPath, result.Errors, out context))
                return result;

            BlkBlock axes = Section(context, "axes");
            if (axes != null)
            {
                foreach (BlkChild axis in context.Document.Children(axes).Where(c => c.Kind == BlkChildKind.Block))
                {
                    GameAction action = catalog.Find(axis.Name);
                    int axisId = AxisId(axis);
                    string device;
                    string token;
                    if (action == null || !action.IsAxis || axisId < 0 || !TryDecodeAxis(context, axisId, devices, result.Warnings, out device, out token))
                        continue;
                    // What the game uses: the file's attribute, else the base preset's, else the code default.
                    WarThunderAxisSettings.WarnUnknownPreset(context.BasePreset, result.Warnings);
                    AxisTuning tuning = new AxisTuning();
                    foreach (string key in WarThunderAxisSettings.Keys)
                        tuning.Native[key] = WarThunderAxisSettings.Effective(axis, context.BasePreset, key);
                    tuning.Inverted = WarThunderAxisSettings.Effective(axis, context.BasePreset, "inverse") >= 0.5;
                    tuning.Slider = tuning.Native["rangeMin"] >= 0;
                    result.Bindings.Add(new GameInputBinding
                    {
                        ActionId = axis.Name,
                        Device = device,
                        Input = token,
                        IsAxis = true,
                        Inverted = tuning.Inverted,
                        Deadzone = tuning.Native["innerDeadzone"],
                        Tuning = tuning
                    });
                }
            }

            BlkBlock hotkeys = Section(context, "hotkeys");
            if (hotkeys != null)
            {
                foreach (BlkChild shortcut in context.Document.Children(hotkeys).Where(c => c.Kind == BlkChildKind.Block))
                {
                    GameAction action = catalog.Find(shortcut.Name);
                    if (action == null || action.IsAxis)
                        continue;
                    GameInputBinding binding = DecodeShortcut(context, shortcut, devices, result.Warnings);
                    if (binding != null)
                        result.Bindings.Add(binding);
                }
            }

            return result;
        }

        // The last joystick button of a shortcut block is the main input; everything else in the block is a modifier.
        private static GameInputBinding DecodeShortcut(WtContext context, BlkChild shortcut, GameDeviceDirectory devices, List<string> warnings)
        {
            List<BlkParam> joyButtons = shortcut.Params.Where(p => p.Name == "joyButton").ToList();
            if (joyButtons.Count == 0)
                return null;

            BlkParam main = joyButtons[joyButtons.Count - 1];
            string device;
            string token;
            int button;
            if (!int.TryParse(main.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out button) ||
                !TryDecodeButton(context, button, devices, warnings, out device, out token))
                return null;

            List<string> reformers = new List<string>();
            foreach (BlkParam param in shortcut.Params.Where(p => !ReferenceEquals(p, main)))
            {
                int value;
                if (!int.TryParse(param.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    continue;
                if (param.Name == "keyboardKey")
                {
                    string key = GameKeyCodes.NameFromDik(value);
                    if (key.Length == 0)
                        return null;
                    reformers.Add(key + "§Keyboard§" + key + "§False");
                }
                else if (param.Name == "joyButton")
                {
                    string modifierDevice;
                    string modifierToken;
                    if (!TryDecodeButton(context, value, devices, warnings, out modifierDevice, out modifierToken))
                        return null;
                    reformers.Add(modifierToken + "§" + modifierDevice + "§" + modifierToken + "§False");
                }
                else
                {
                    return null;
                }
            }

            return new GameInputBinding { ActionId = shortcut.Name, Device = device, Input = token, IsAxis = false, Reformers = reformers };
        }

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            WtContext context;
            if (!TryOpen(request.ConfigPath, result.Errors, out context))
                return result;

            Dictionary<string, List<List<BlkParam>>> wantedShortcuts = new Dictionary<string, List<List<BlkParam>>>(StringComparer.Ordinal);
            Dictionary<string, GameInputBinding> wantedAxes = new Dictionary<string, GameInputBinding>(StringComparer.Ordinal);
            Dictionary<string, int> wantedAxisIds = new Dictionary<string, int>(StringComparer.Ordinal);
            // The game's aircraft (cataloged) binds per input, for "only add" (a bind on an input the game uses for
            // another action is not written) and the merge conflicts. Tank, ship and menu binds never count.
            Dictionary<string, List<string>> gameShortcuts = GameShortcuts(context, request.Catalog);
            Dictionary<int, List<string>> gameAxes = GameAxes(context, request.Catalog);

            foreach (GameInputBinding binding in request.Bindings)
            {
                GameAction action = request.Catalog.Find(binding.ActionId);
                string reason;
                if (action != null && action.IsAxis)
                {
                    int axisId;
                    reason = EncodeAxis(context, binding, request.Devices, result.Warnings, out axisId);
                    if (reason.Length == 0 && wantedAxes.ContainsKey(action.Id))
                        reason = "the axis is already bound by relation '" + wantedAxes[action.Id].RelationName + "'.";
                    if (reason.Length == 0 && request.Mode == GameExportMode.Add)
                    {
                        string other = OtherUser(gameAxes, axisId, action.Id);
                        int current = GameAxisId(context, action.Id);
                        if (other.Length > 0)
                        {
                            SkipMultiBind(result, binding, other);
                            continue;
                        }
                        // War Thunder has one axis per action: "only add" does not replace the game's axis.
                        if (current >= 0 && current != axisId)
                            reason = "the game already has another axis on it (add keeps the game's binds).";
                    }
                    if (reason.Length == 0)
                    {
                        wantedAxes[action.Id] = binding;
                        wantedAxisIds[action.Id] = axisId;
                    }
                }
                else
                {
                    List<BlkParam> shortcut;
                    reason = EncodeShortcut(context, binding, request.Devices, result.Warnings, out shortcut);
                    string other = reason.Length == 0 && request.Mode == GameExportMode.Add ? OtherUser(gameShortcuts, Signature(shortcut), binding.ActionId) : string.Empty;
                    if (other.Length > 0)
                    {
                        SkipMultiBind(result, binding, other);
                        continue;
                    }
                    if (reason.Length == 0)
                    {
                        List<List<BlkParam>> list;
                        if (!wantedShortcuts.TryGetValue(binding.ActionId, out list))
                        {
                            list = new List<List<BlkParam>>();
                            wantedShortcuts[binding.ActionId] = list;
                        }
                        list.Add(shortcut);
                    }
                }

                if (reason.Length > 0)
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
                    continue;
                }
                result.BindingsWritten++;
            }

            HashSet<string> usedDevices = new HashSet<string>(request.Bindings.Select(b => b.IsKeyboard ? "Keyboard" : b.Device.Trim()), StringComparer.OrdinalIgnoreCase);
            WriteHotkeys(context, request, wantedShortcuts, usedDevices, result);
            WriteAxes(context, request, wantedAxes, wantedAxisIds, usedDevices, result);
            WriteSettings(context, result);

            if (request.DryRun)
            {
                result.Steps.Add("Dry run: " + context.Document.ChangedBlocks.ToString(CultureInfo.InvariantCulture) + " controls section(s) would change.");
                return result;
            }

            Save(context, request.ConfigPath, result);
            return result;
        }

        // "Only add": the profile's bind is not written, the game uses the input for another (aircraft) action.
        private void SkipMultiBind(GameIoResult result, GameInputBinding binding, string gameAction)
        {
            result.BindingsSkipped++;
            result.SkippedTakenInputs.Add(new ExportSkippedBind
            {
                Game = GameId,
                Device = binding.Device,
                Input = binding.Input,
                ModifiersText = string.Join("+", (binding.Reformers ?? new List<string>()).Select(r => r.Split('§')).Select(p => p.Length >= 3 ? p[1] + " " + p[2] : p[0])),
                Relation = binding.RelationName,
                Action = binding.ActionId,
                GameAction = gameAction
            });
        }

        // The first action other than 'self' that uses the input, "" when none.
        private static string OtherUser<T>(Dictionary<T, List<string>> users, T input, string self)
        {
            List<string> ids;
            return users.TryGetValue(input, out ids) ? ids.FirstOrDefault(id => id != self) ?? string.Empty : string.Empty;
        }

        // Cataloged shortcut blocks of the file per signature (keys + buttons of one shortcut).
        private static Dictionary<string, List<string>> GameShortcuts(WtContext context, GameCatalog catalog)
        {
            Dictionary<string, List<string>> users = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            BlkBlock hotkeys = Section(context, "hotkeys");
            if (hotkeys == null)
                return users;
            foreach (BlkChild child in context.Document.Children(hotkeys).Where(c => c.Kind == BlkChildKind.Block && c.Params.Count > 0))
            {
                GameAction action = catalog.Find(child.Name);
                if (action == null || action.IsAxis)
                    continue;
                string signature = Signature(child.Params);
                List<string> ids;
                if (!users.TryGetValue(signature, out ids))
                    users[signature] = ids = new List<string>();
                if (!ids.Contains(child.Name))
                    ids.Add(child.Name);
            }
            return users;
        }

        // Cataloged axes of the file per axisId (unbound axes, axisId -1, are left out).
        private static Dictionary<int, List<string>> GameAxes(WtContext context, GameCatalog catalog)
        {
            Dictionary<int, List<string>> users = new Dictionary<int, List<string>>();
            BlkBlock axes = Section(context, "axes");
            if (axes == null)
                return users;
            foreach (BlkChild child in context.Document.Children(axes).Where(c => c.Kind == BlkChildKind.Block))
            {
                GameAction action = catalog.Find(child.Name);
                int axisId = AxisId(child);
                if (action == null || !action.IsAxis || axisId < 0)
                    continue;
                List<string> ids;
                if (!users.TryGetValue(axisId, out ids))
                    users[axisId] = ids = new List<string>();
                if (!ids.Contains(child.Name))
                    ids.Add(child.Name);
            }
            return users;
        }

        private static int GameAxisId(WtContext context, string axisName)
        {
            BlkBlock axes = Section(context, "axes");
            BlkChild child = axes == null ? null : context.Document.Children(axes).FirstOrDefault(c => c.Kind == BlkChildKind.Block && c.Name == axisName);
            return child == null ? -1 : AxisId(child);
        }

        private static void Save(WtContext context, string path, GameIoResult result)
        {
            if (context.Document.ChangedBlocks == 0)
            {
                result.Steps.Add("The War Thunder controls already match.");
                return;
            }

            GameExportSafety.BackupBeforeOverwrite(path, result.Warnings);
            context.Document.Save(path);
            result.FilesWritten.Add(path);
            result.Steps.Add(context.Document.ChangedBlocks.ToString(CultureInfo.InvariantCulture) + " controls section(s) changed.");
        }

        // Game-wide control options (engine ffb set --device * --game WarThunder): only the keys the profile sets.
        private void WriteSettings(WtContext context, GameIoResult result)
        {
            Dictionary<string, double> settings = FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, GameId, null);
            if (settings.Count == 0)
                return;
            if (!context.IsMachineBlk)
            {
                result.Warnings.Add("Game-wide settings (force feedback, sensitivities, ...) are only written to machine.blk, not to an exported controls preset.");
                return;
            }
            List<string> changes = new List<string>();
            WarThunderSettingsBlk.Apply(context.Document, settings, changes);
            result.Steps.AddRange(changes.Select(c => "machine.blk " + c));
        }

        public Dictionary<string, double> ReadSettings(string configPath, List<string> warnings)
        {
            WtContext context;
            List<string> errors = new List<string>();
            if (!TryOpen(configPath, errors, out context))
            {
                warnings.AddRange(errors);
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }
            return context.IsMachineBlk ? WarThunderSettingsBlk.Read(context.Document) : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        public GameIoResult WriteSettings(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            WtContext context;
            if (!TryOpen(request.ConfigPath, result.Errors, out context))
                return result;
            WriteSettings(context, result);
            if (request.DryRun)
            {
                result.Steps.Add("Dry run: " + context.Document.ChangedBlocks.ToString(CultureInfo.InvariantCulture) + " section(s) would change.");
                return result;
            }
            Save(context, request.ConfigPath, result);
            return result;
        }

        // Each ID_...{} block is one shortcut, several blocks of one ID are alternatives. Blocks of IDs outside the
        // catalog (tanks, ships, menus) are never touched; within the catalog the mode decides which existing blocks go.
        private static void WriteHotkeys(WtContext context, GameExportRequest request, Dictionary<string, List<List<BlkParam>>> wanted, HashSet<string> usedDevices, GameIoResult result)
        {
            BlkBlock hotkeys = Section(context, "hotkeys");
            if (hotkeys == null)
            {
                if (wanted.Count > 0)
                    result.Warnings.Add("The controls have no hotkeys{} section; shortcuts were not written.");
                return;
            }

            List<BlkChild> children = context.Document.Children(hotkeys);
            string indent = context.Document.ChildIndent(hotkeys);
            HashSet<string> managedIds = new HashSet<string>(request.Catalog.Actions.Where(a => !a.IsAxis).Select(a => a.Id), StringComparer.Ordinal);
            // The actions the profile puts on each input: the game's block of another action on it is a conflict.
            Dictionary<string, HashSet<string>> wantedBy = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<List<BlkParam>>> entry in wanted)
            {
                foreach (List<BlkParam> shortcut in entry.Value)
                {
                    HashSet<string> ids;
                    if (!wantedBy.TryGetValue(Signature(shortcut), out ids))
                        wantedBy[Signature(shortcut)] = ids = new HashSet<string>(StringComparer.Ordinal);
                    ids.Add(entry.Key);
                }
            }

            // New shortcuts of an ID go behind its last existing block (and the blank line after it).
            Dictionary<int, List<string>> insertAfter = new Dictionary<int, List<string>>();
            HashSet<string> present = new HashSet<string>(StringComparer.Ordinal);
            for (int i = children.Count - 1; i >= 0; i--)
            {
                BlkChild child = children[i];
                if (child.Kind != BlkChildKind.Block || !wanted.ContainsKey(child.Name) || !present.Add(child.Name))
                    continue;
                int position = i + 1 < children.Count && children[i + 1].Kind == BlkChildKind.Blank ? i + 1 : i;
                List<string> ids;
                if (!insertAfter.TryGetValue(position, out ids))
                {
                    ids = new List<string>();
                    insertAfter[position] = ids;
                }
                ids.Add(child.Name);
            }

            List<List<string>> body = new List<List<string>>();
            bool droppedBlock = false;
            for (int i = 0; i < children.Count; i++)
            {
                BlkChild child = children[i];
                bool keep;
                if (child.Kind == BlkChildKind.Blank)
                    keep = !droppedBlock;
                else
                    keep = child.Kind != BlkChildKind.Block || !managedIds.Contains(child.Name) || !DropShortcut(context, child, request, wanted, wantedBy, usedDevices);
                droppedBlock = !keep && child.RawLines.Count > 1;
                if (keep)
                    body.Add(child.RawLines);

                List<string> ids;
                if (insertAfter.TryGetValue(i, out ids))
                {
                    foreach (string id in ids)
                        AddMissingShortcuts(id, children, wanted[id], indent, body, result);
                }
            }

            foreach (KeyValuePair<string, List<List<BlkParam>>> entry in wanted.Where(e => !present.Contains(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                result.Warnings.Add(entry.Key + " was not customized in this file, so War Thunder used the base preset's shortcuts for it; JoyPro's shortcuts replace them.");
                AddMissingShortcuts(entry.Key, children, entry.Value, indent, body, result);
            }

            context.Document.ReplaceBody(hotkeys, body);
        }

        // A cataloged shortcut goes when
        //  - the profile puts another action on the same input (merge, merge-overwrite, clean: the conflict goes),
        //  - merge-overwrite or clean: its action is in the profile and this is another input of it on a device JoyPro
        //    owns in this export (a connected joystick War Thunder's device list names; the keyboard only with
        //    --include-keyboard), so a moved action fully moves,
        //  - its device is released: clean releases the devices the export writes, nuke the connected devices it does
        //    not use.
        // "Only add" removes nothing (apart from the devices nuke releases); plain merge keeps the action's old inputs.
        private static bool DropShortcut(WtContext context, BlkChild child, GameExportRequest request, Dictionary<string, List<List<BlkParam>>> wanted, Dictionary<string, HashSet<string>> wantedBy, HashSet<string> usedDevices)
        {
            List<List<BlkParam>> wantedForId;
            if (!wanted.TryGetValue(child.Name, out wantedForId))
                wantedForId = new List<List<BlkParam>>();
            if (child.Params.Count == 0)
                return wantedForId.Count > 0;

            string signature = Signature(child.Params);
            if (wantedForId.Any(w => Signature(w) == signature))
                return false;
            string device = ShortcutDevice(context, child, request.Devices);
            if (request.Mode != GameExportMode.Add)
            {
                HashSet<string> users;
                if (wantedBy.TryGetValue(signature, out users) && !users.Contains(child.Name))
                    return true;
                if (request.Mode != GameExportMode.Merge && wantedForId.Count > 0 && device.Length > 0 &&
                    (request.IncludeKeyboard || !string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return Released(device, request, usedDevices);
        }

        // War Thunder also has tanks and ships: only cataloged (aircraft) entries are ever released, keyboard ones only
        // with --include-keyboard, and only on devices that are connected (so they can be identified).
        private static bool Released(string device, GameExportRequest request, HashSet<string> usedDevices)
        {
            if (device.Length == 0)
                return false;
            if (string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase) && !request.IncludeKeyboard)
                return false;
            return usedDevices.Contains(device) ? request.Mode == GameExportMode.Clean : request.NukeUnusedDevices;
        }

        private static string ShortcutDevice(WtContext context, BlkChild shortcut, GameDeviceDirectory devices)
        {
            BlkParam main = shortcut.Params.LastOrDefault(p => p.Name == "joyButton");
            if (main == null)
                return shortcut.Params.Any(p => p.Name == "keyboardKey") ? "Keyboard" : string.Empty;
            int button;
            string device;
            string token;
            return int.TryParse(main.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out button) &&
                   TryDecodeButton(context, button, devices, new List<string>(), out device, out token)
                ? device
                : string.Empty;
        }

        private static string AxisDevice(WtContext context, BlkChild axis, GameDeviceDirectory devices)
        {
            string device;
            string token;
            int axisId = AxisId(axis);
            return axisId >= 0 && TryDecodeAxis(context, axisId, devices, new List<string>(), out device, out token) ? device : string.Empty;
        }

        // The profile's shortcuts the action does not have yet go behind its blocks (every mode: the game's other
        // shortcuts of the action are alternatives, a conflict on another action was removed or skipped before).
        private static void AddMissingShortcuts(string id, List<BlkChild> existing, List<List<BlkParam>> wanted, string indent, List<List<string>> body, GameIoResult result)
        {
            List<BlkChild> blocks = existing.Where(c => c.Kind == BlkChildKind.Block && c.Name == id).ToList();
            HashSet<string> existingSignatures = new HashSet<string>(blocks.Where(c => c.Params.Count > 0).Select(c => Signature(c.Params)), StringComparer.Ordinal);
            int count = existingSignatures.Count;
            foreach (List<BlkParam> shortcut in wanted)
            {
                if (!existingSignatures.Add(Signature(shortcut)))
                    continue;
                if (++count == 4)
                    result.Warnings.Add(id + " now has more than 3 shortcuts; War Thunder's controls menu only shows 3.");
                AppendBlock(body, BlkDocument.RenderBlock(indent, id, shortcut));
            }
        }

        // War Thunder puts a blank line after every multi-line block; keep that when appending behind one.
        private static void AppendBlock(List<List<string>> body, List<string> block)
        {
            List<string> previous = body.LastOrDefault(lines => lines.Count > 0);
            if (previous != null && previous[previous.Count - 1].Trim() == "}")
                body.Add(new List<string> { string.Empty });
            body.Add(block);
        }

        private static void WriteAxes(WtContext context, GameExportRequest request, Dictionary<string, GameInputBinding> wanted, Dictionary<string, int> wantedIds, HashSet<string> usedDevices, GameIoResult result)
        {
            BlkBlock axes = Section(context, "axes");
            if (axes == null)
            {
                if (wanted.Count > 0)
                    result.Warnings.Add("The controls have no axes{} section; axes were not written.");
                return;
            }

            List<BlkChild> children = context.Document.Children(axes);
            string indent = context.Document.ChildIndent(axes);
            List<List<string>> body = new List<List<string>>();
            HashSet<string> present = new HashSet<string>(StringComparer.Ordinal);
            foreach (BlkChild child in children)
            {
                GameAction action = child.Kind == BlkChildKind.Block ? request.Catalog.Find(child.Name) : null;
                GameInputBinding binding;
                if (action == null || !action.IsAxis || !present.Add(child.Name))
                    body.Add(child.RawLines);
                // One axis per action: merge and merge-overwrite set the profile's axis, "only add" keeps a bound one
                // as it is (a different one was skipped before).
                else if (wanted.TryGetValue(child.Name, out binding))
                    body.Add(request.Mode == GameExportMode.Add && AxisId(child) >= 0 ? child.RawLines : SetAxis(context, child.Name, child.RawLines, wantedIds[child.Name], binding, child, result.Warnings));
                // Another aircraft axis on the axis the profile uses is a conflict (unbound, except in "only add").
                else if (AxisId(child) >= 0 && ((request.Mode != GameExportMode.Add && wantedIds.ContainsValue(AxisId(child))) ||
                                                Released(AxisDevice(context, child, request.Devices), request, usedDevices)))
                    body.Add(BlkDocument.SetParams(child.RawLines, new[] { IntParam("axisId", -1) }, true));
                else
                    body.Add(child.RawLines);
            }

            foreach (KeyValuePair<string, GameInputBinding> entry in wanted.Where(e => !present.Contains(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal))
                AppendBlock(body, SetAxis(context, entry.Key, new List<string> { indent + entry.Key + "{}" }, wantedIds[entry.Key], entry.Value, null, result.Warnings));

            context.Document.ReplaceBody(axes, body);
        }

        // Sets axisId and the axis settings the tuning holds (warthunder model: invert, dead zone, nonlinearity,
        // multiplier, correction, relative control, keep value, range); attributes the tuning does not set keep their
        // lines. The file is a diff against the base preset like the game writes it: a line that already has the
        // wanted value stays as it is, a value equal to the base preset's (or the code default) drops the line, any
        // other value is written, 0 included.
        private static List<string> SetAxis(WtContext context, string axisName, List<string> rawLines, int axisId, GameInputBinding binding, BlkChild existing, List<string> warnings)
        {
            List<string> lines = BlkDocument.SetParams(rawLines, new[] { IntParam("axisId", axisId) }, true);
            List<KeyValuePair<string, double>> wanted = new List<KeyValuePair<string, double>>
            {
                new KeyValuePair<string, double>("inverse", binding.Inverted ? 1 : 0)
            };
            if (binding.Tuning != null)
            {
                foreach (string key in WarThunderAxisSettings.Keys)
                {
                    double value;
                    if (binding.Tuning.Native != null && binding.Tuning.Native.TryGetValue(key, out value))
                        wanted.Add(new KeyValuePair<string, double>(key, WarThunderAxisSettings.Clamp(key, value, binding.RelationName, warnings)));
                }
            }
            else
                wanted.Add(new KeyValuePair<string, double>("innerDeadzone", WarThunderAxisSettings.Clamp("innerDeadzone", binding.Deadzone, binding.RelationName, warnings)));

            WarThunderAxisSettings.WarnUnknownPreset(context.BasePreset, warnings);
            // Without the base preset's values (a release package ships no base-presets.tsv) every wanted value is
            // written explicitly: a value equal to the guessed base is never dropped from the file.
            bool baseKnown = WarThunderAxisSettings.IsKnownPreset(context.BasePreset);
            List<BlkParam> set = new List<BlkParam>();
            List<string> remove = new List<string>();
            foreach (KeyValuePair<string, double> value in wanted)
            {
                BlkParam line = existing == null ? null : existing.Get(value.Key);
                double current;
                if (line != null && WarThunderAxisSettings.TryParse(line, out current) && WarThunderAxisSettings.Same(current, value.Value))
                    continue;
                if (baseKnown && WarThunderAxisSettings.Same(value.Value, WarThunderAxisSettings.BaseValue(context.BasePreset, axisName, value.Key)))
                {
                    if (line != null)
                        remove.Add(value.Key);
                }
                else
                    set.Add(WarThunderAxisSettings.Render(value.Key, value.Value));
            }
            lines = BlkDocument.SetParams(lines, set, false);
            if (remove.Count > 0)
                lines = BlkDocument.RemoveParams(lines, remove);
            if (existing == null)
                lines.Add(string.Empty);
            return lines;
        }

        private static BlkParam IntParam(string name, int value)
        {
            return new BlkParam { Name = name, Type = "i", Value = value.ToString(CultureInfo.InvariantCulture) };
        }

        private static int AxisId(BlkChild axis)
        {
            int axisId;
            return int.TryParse(Value(axis, "axisId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out axisId) ? axisId : -1;
        }

        private static string Signature(IEnumerable<BlkParam> parameters)
        {
            return string.Join(";", parameters.Select(p => p.Name + "=" + p.Value).OrderBy(v => v, StringComparer.Ordinal));
        }

        private static string EncodeShortcut(WtContext context, GameInputBinding binding, GameDeviceDirectory devices, List<string> warnings, out List<BlkParam> shortcut)
        {
            shortcut = new List<BlkParam>();
            foreach (string reformer in binding.Reformers ?? new List<string>())
            {
                string[] parts = reformer.Split('§');
                if (parts.Length < 3)
                    return "unreadable modifier '" + reformer + "'.";
                if (string.Equals(parts[1], "Keyboard", StringComparison.OrdinalIgnoreCase))
                {
                    int dik;
                    if (!GameKeyCodes.TryGetDik(parts[2], out dik))
                        return "keyboard modifier '" + parts[2] + "' has no War Thunder key code.";
                    shortcut.Add(IntParam("keyboardKey", dik));
                }
                else
                {
                    int modifierButton;
                    string reason = EncodeButton(context, parts[1], parts[2], devices, warnings, out modifierButton);
                    if (reason.Length > 0)
                        return "modifier: " + reason;
                    shortcut.Add(IntParam("joyButton", modifierButton));
                }
            }

            if (binding.IsKeyboard)
            {
                int key;
                if (!GameKeyCodes.TryGetDik(binding.Input, out key))
                    return "key '" + binding.Input + "' has no War Thunder key code.";
                shortcut.Add(IntParam("keyboardKey", key));
                return string.Empty;
            }

            int button;
            string mainReason = EncodeButton(context, binding.Device, binding.Input, devices, warnings, out button);
            if (mainReason.Length > 0)
                return mainReason;
            shortcut.Add(IntParam("joyButton", button));
            return string.Empty;
        }

        // War Thunder numbers a device's buttons first, then four buttons (up, right, down, left) per hat.
        private static string EncodeButton(WtContext context, string joyProDevice, string token, GameDeviceDirectory devices, List<string> warnings, out int button)
        {
            button = -1;
            GameDevice device;
            WtDevice wtDevice;
            string reason = ResolveDevice(context, joyProDevice, devices, warnings, out device, out wtDevice);
            if (reason.Length > 0)
                return reason;

            JoyInput input;
            if (!GameInputTokens.TryParse(token, out input))
                return "unsupported input '" + token + "'.";
            if (input.Kind == JoyInputKind.Button)
            {
                if (input.Index >= device.ButtonCount)
                    return "the device has only " + device.ButtonCount.ToString(CultureInfo.InvariantCulture) + " buttons.";
                if (input.Index >= ReadableButtons)
                    return "War Thunder only reads the first " + ReadableButtons.ToString(CultureInfo.InvariantCulture) + " buttons of a device.";
                button = wtDevice.ButtonsOffset + input.Index;
                return string.Empty;
            }
            if (input.Kind == JoyInputKind.Pov)
            {
                if (input.Index >= HatSlots(device) || PovDirectionToButton[input.Direction] < 0)
                    return "only the four main directions of an existing hat can be bound.";
                button = wtDevice.ButtonsOffset + ButtonSlots(device) + 4 * input.Index + PovDirectionToButton[input.Direction];
                return string.Empty;
            }
            return "'" + token + "' is an axis; bind it to an axis action.";
        }

        private static string EncodeAxis(WtContext context, GameInputBinding binding, GameDeviceDirectory devices, List<string> warnings, out int axisId)
        {
            axisId = -1;
            if (binding.Reformers != null && binding.Reformers.Count > 0)
                return "War Thunder axes cannot have modifiers.";
            GameDevice device;
            WtDevice wtDevice;
            string reason = ResolveDevice(context, binding.Device, devices, warnings, out device, out wtDevice);
            if (reason.Length > 0)
                return reason;
            JoyInput input;
            if (!GameInputTokens.TryParse(binding.Input, out input) || input.Kind != JoyInputKind.Axis)
                return "the action needs an axis.";
            int local = device.AxisSlots.IndexOf(input.Index);
            if (local < 0)
                return "the device has no " + binding.Input + " axis.";
            axisId = wtDevice.AxesOffset + local;
            return string.Empty;
        }

        // Matches a JoyPro device to its deviceMapping entry by VID:PID (War Thunder cannot tell identical devices apart)
        // and checks that the game counted the same buttons and axes, because every global index depends on them.
        private static string ResolveDevice(WtContext context, string joyProDevice, GameDeviceDirectory devices, List<string> warnings, out GameDevice device, out WtDevice wtDevice)
        {
            wtDevice = null;
            device = devices == null ? null : devices.FindByJoyProName(joyProDevice);
            if (device == null || !device.HasVidPid)
                return "device '" + joyProDevice + "' is not connected.";
            string devId = GameDeviceDirectory.VidPidKey(device.VendorId, device.ProductId);
            if (devices.FindByVidPid(device.VendorId, device.ProductId).Count > 1)
                return "several connected devices share " + devId + "; War Thunder cannot tell them apart.";
            wtDevice = context.Devices.FirstOrDefault(d => string.Equals(d.DevId, devId, StringComparison.OrdinalIgnoreCase));
            if (wtDevice == null)
                return "War Thunder has not listed device '" + joyProDevice + "' (" + devId + ") yet; connect it and open the game's controls once.";
            if (!devices.EnsureCapabilities(device, warnings))
                return "the buttons and axes of '" + joyProDevice + "' could not be read.";
            return CountMismatch(wtDevice, device);
        }

        // War Thunder's engine keeps at most 496 buttons and 4 hats per device (the hat buttons follow the buttons),
        // but only reads the first 128 buttons.
        private const int ReadableButtons = 128;

        private static int ButtonSlots(GameDevice device)
        {
            return Math.Min(device.ButtonCount, 496);
        }

        private static int HatSlots(GameDevice device)
        {
            return Math.Min(device.PovCount, 4);
        }

        private static string CountMismatch(WtDevice wtDevice, GameDevice device)
        {
            int buttons = ButtonSlots(device) + 4 * HatSlots(device);
            int axes = device.AxisSlots.Count + 2 * HatSlots(device);
            if (wtDevice.ButtonsCount == buttons && wtDevice.AxesCount == axes)
                return string.Empty;
            return "War Thunder counts " + wtDevice.ButtonsCount.ToString(CultureInfo.InvariantCulture) + " buttons/" + wtDevice.AxesCount.ToString(CultureInfo.InvariantCulture) +
                   " axes for '" + wtDevice.Name + "', DirectInput " + buttons.ToString(CultureInfo.InvariantCulture) + "/" + axes.ToString(CultureInfo.InvariantCulture) +
                   "; skipped so no wrong input gets bound.";
        }

        private static bool TryDecodeAxis(WtContext context, int axisId, GameDeviceDirectory devices, List<string> warnings, out string joyProDevice, out string token)
        {
            joyProDevice = string.Empty;
            token = string.Empty;
            WtDevice wtDevice = context.Devices.FirstOrDefault(d => axisId >= d.AxesOffset && axisId < d.AxesOffset + d.AxesCount);
            GameDevice device = wtDevice == null ? null : FindConnected(wtDevice, devices, warnings);
            int local = wtDevice == null ? -1 : axisId - wtDevice.AxesOffset;
            if (device == null || local >= device.AxisSlots.Count)
                return false;
            joyProDevice = device.JoyProName;
            token = GameInputTokens.AxisToken(device.AxisSlots[local]);
            return true;
        }

        private static bool TryDecodeButton(WtContext context, int button, GameDeviceDirectory devices, List<string> warnings, out string joyProDevice, out string token)
        {
            joyProDevice = string.Empty;
            token = string.Empty;
            WtDevice wtDevice = context.Devices.FirstOrDefault(d => button >= d.ButtonsOffset && button < d.ButtonsOffset + d.ButtonsCount);
            GameDevice device = wtDevice == null ? null : FindConnected(wtDevice, devices, warnings);
            if (device == null)
                return false;
            int local = button - wtDevice.ButtonsOffset;
            joyProDevice = device.JoyProName;
            if (local < ButtonSlots(device))
            {
                token = GameInputTokens.ButtonToken(local);
                return true;
            }
            int hatButton = local - ButtonSlots(device);
            token = GameInputTokens.PovToken(hatButton / 4, PovButtonToDirection[hatButton % 4]);
            return true;
        }

        private static GameDevice FindConnected(WtDevice wtDevice, GameDeviceDirectory devices, List<string> warnings)
        {
            string[] parts = (wtDevice.DevId ?? string.Empty).Split(':');
            int vid;
            int pid;
            if (devices == null || parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid) ||
                !int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid))
                return null;

            List<GameDevice> sameModel = devices.FindByVidPid(vid, pid);
            GameDevice device = sameModel.Count == 1 ? sameModel[0] : null;
            string problem = sameModel.Count == 0 ? "is not connected"
                : device == null ? "matches several connected devices"
                : !devices.EnsureCapabilities(device, warnings) ? "could not be read"
                : CountMismatch(wtDevice, device).Length > 0 ? "has a different button/axis count than War Thunder recorded"
                : string.Empty;
            if (problem.Length == 0)
                return device;

            string warning = "War Thunder device '" + wtDevice.Name + "' (" + wtDevice.DevId + ") " + problem + "; its binds were not imported.";
            if (!warnings.Contains(warning))
                warnings.Add(warning);
            return null;
        }

        // Rebuilds the catalog from War Thunder's control scripts (shortcutsgroupaircraft/helicopter.nut): entries of the
        // aircraft and helicopter groups without a UI-only type; CONTROL_TYPE.AXIS becomes an axis, the rest shortcuts.
        // The scripts are packed in the game's gui.vromfs.bin, which JoyPro unpacks by default (the user approved it on
        // 2026-10-12; Gaijin's EULA restricts unpacking game files, so the user can turn it off: refresh-db
        // --no-game-archives, Settings > Games). Turned off, the list is JoyPro's own: the War Thunder actions of JoyPro's
        // function list (DB\Functions\WarThunder.tsv).
        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            // Without --path (or with the game folder): the installed game.
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                GameInstall install = GameInstalls.Find(GameId, result.Warnings);
                sourcePath = install != null && install.Installed ? install.InstallPath : null;
            }
            if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(Path.Combine(sourcePath, "gui.vromfs.bin")))
            {
                if (!GameCommands.ReadGameArchives)
                    return CatalogFromJoyProFunctions(current, result);
                string extracted = ExtractFromGame(sourcePath, result);
                if (extracted == null)
                    return null;
                // The three files live in a temporary folder only for this refresh: no game file stays on disk.
                try
                {
                    GameCatalog fromGame = CatalogFromScripts(extracted, current, result);
                    if (fromGame != null)
                        fromGame.SourcePath = sourcePath;
                    return fromGame;
                }
                finally
                {
                    try { Directory.Delete(extracted, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return CatalogFromScripts(sourcePath, current, result);
        }

        // The catalog from a folder that holds the control scripts as plain files (and optionally lang controls.csv).
        private GameCatalog CatalogFromScripts(string sourcePath, GameCatalog current, GameIoResult result)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
            {
                result.Errors.Add("War Thunder was not found. Pass --path with the game folder (gui.vromfs.bin) or a folder that holds the control scripts (shortcutsgroupaircraft.nut, shortcutsgrouphelicopter.nut, optionally lang controls.csv).");
                return null;
            }

            string csvPath = FindFile(sourcePath, "controls.csv");
            Dictionary<string, string> titles = csvPath == null ? new Dictionary<string, string>(StringComparer.Ordinal) : ReadLangTitles(csvPath);
            if (csvPath == null)
                result.Warnings.Add("controls.csv was not found; known actions keep their titles, new actions are titled by their ID.");

            GameCatalog catalog = new GameCatalog { GameId = GameId, SourcePath = sourcePath };
            foreach (string[] group in ScriptGroups)
            {
                // Older copies of the scripts carry an "sl_" prefix.
                string file = FindFile(sourcePath, group[0]) ?? FindFile(sourcePath, "sl_" + group[0]);
                string text = file == null ? string.Empty : File.ReadAllText(file, Encoding.UTF8);
                int start = text.IndexOf("return [", StringComparison.Ordinal);
                if (start < 0)
                {
                    result.Errors.Add(group[0] + " was not found or not readable under " + sourcePath + ".");
                    return null;
                }

                foreach (string entry in TopLevelEntries(text.Substring(start + "return [".Length)))
                {
                    Match id = Regex.Match(entry, "\\bid\\s*=\\s*\"([^\"]+)\"");
                    Match type = Regex.Match(entry, @"\btype\s*=\s*CONTROL_TYPE\.([A-Z_]+)");
                    string kind = type.Success ? type.Groups[1].Value : "SHORTCUT";
                    if (!id.Success || (kind != "AXIS" && kind != "SHORTCUT") || id.Groups[1].Value.StartsWith("USEROPT_", StringComparison.Ordinal))
                        continue;

                    string ident = id.Groups[1].Value;
                    bool isAxis = kind == "AXIS";
                    GameAction known = current == null ? null : current.Find(ident);
                    string title;
                    if (!titles.TryGetValue((isAxis ? "controls/" : "hotkeys/") + ident, out title) && !titles.TryGetValue("hotkeys/" + ident, out title))
                        title = known != null ? known.Title : ident;
                    catalog.Add(new GameAction { Id = ident, Title = title.Replace('\t', ' '), IsAxis = isAxis, Category = group[1] });
                }
            }
            return catalog;
        }

        // JoyPro's own War Thunder list: every action id of DB\Functions\WarThunder.tsv (JoyPro's function vocabulary)
        // with JoyPro's description; axes are the ids without the "ID_" prefix (the game names shortcuts ID_*). Actions
        // already in the catalog keep their entry. No game file is read.
        private GameCatalog CatalogFromJoyProFunctions(GameCatalog current, GameIoResult result)
        {
            GameCatalog catalog = new GameCatalog { GameId = GameId, SourcePath = "JoyPro function list" };
            if (current != null)
            {
                foreach (GameAction action in current.Actions)
                    catalog.Add(action);
            }
            string functions = Path.Combine(MainStructure.PROGPATH ?? string.Empty, "DB", "Functions", "WarThunder.tsv");
            int added = 0;
            if (File.Exists(functions))
            {
                foreach (string raw in File.ReadAllLines(functions, Encoding.UTF8))
                {
                    if (raw.Length == 0 || raw[0] == '#')
                        continue;
                    string[] parts = raw.Split('\t');
                    if (parts.Length < 3 || parts[2].Trim().Length == 0 || catalog.Contains(parts[2].Trim()))
                        continue;
                    string id = parts[2].Trim();
                    string title = parts.Length > 6 && parts[6].Trim().Length > 0 ? parts[6].Trim() : id;
                    catalog.Add(new GameAction { Id = id, Title = title.Replace('\t', ' '), IsAxis = !id.StartsWith("ID_", StringComparison.Ordinal), Category = "Aircraft" });
                    added++;
                }
            }
            result.Steps.Add("Built the War Thunder list from JoyPro's own function list (" + added.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                             " action(s)); the game's archives were not read because reading them is turned off (refresh-db --no-game-archives, " +
                             "Settings > Games in the GUI). Without that JoyPro reads the full list from War Thunder's archives (gui.vromfs.bin).");
            return catalog;
        }

        private static readonly string[][] ScriptGroups =
        {
            new[] { "shortcutsgroupaircraft.nut", "Aircraft" },
            new[] { "shortcutsgrouphelicopter.nut", "Helicopter" }
        };

        // Copies the two shortcut scripts (gui.vromfs.bin) and lang/controls.csv (lang.vromfs.bin) into a temporary
        // folder (RefreshCatalog deletes it after reading); null (with an error) when the archives cannot be read.
        private static string ExtractFromGame(string gameFolder, GameIoResult result)
        {
            string folder = Path.Combine(Path.GetTempPath(), "JoyPro_warthunder_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(folder);
                Dictionary<string, byte[]> scripts = WarThunderVromfs.ReadFiles(Path.Combine(gameFolder, "gui.vromfs.bin"),
                    name => ScriptGroups.Any(g => name.EndsWith("/" + g[0], StringComparison.OrdinalIgnoreCase)));
                foreach (KeyValuePair<string, byte[]> script in scripts)
                    File.WriteAllBytes(Path.Combine(folder, Path.GetFileName(script.Key)), script.Value);
                string lang = Path.Combine(gameFolder, "lang.vromfs.bin");
                if (File.Exists(lang))
                {
                    foreach (KeyValuePair<string, byte[]> csv in WarThunderVromfs.ReadFiles(lang, name => name.EndsWith("/controls.csv", StringComparison.OrdinalIgnoreCase)))
                        File.WriteAllBytes(Path.Combine(folder, "controls.csv"), csv.Value);
                }
                result.Steps.Add("Read " + scripts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " shortcut script(s) and the control titles from the game's archives in " + gameFolder + ".");
                return folder;
            }
            catch (Exception ex)
            {
                result.Errors.Add("Could not read War Thunder's archives in " + gameFolder + ": " + ex.Message);
                return null;
            }
        }

        private static string FindFile(string folder, string name)
        {
            return Directory.EnumerateFiles(folder, name, SearchOption.AllDirectories).FirstOrDefault();
        }

        // The { ... } entries directly inside the returned Squirrel array.
        private static IEnumerable<string> TopLevelEntries(string text)
        {
            int depth = 0;
            int start = -1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '{')
                {
                    if (++depth == 1)
                        start = i;
                }
                else if (text[i] == '}')
                {
                    if (depth == 1 && start >= 0)
                        yield return text.Substring(start, i - start + 1);
                    depth--;
                }
            }
        }

        // lang/controls.csv: "key";"English";... with quoted fields that may contain ';', '""' and line breaks.
        private static Dictionary<string, string> ReadLangTitles(string path)
        {
            Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.Ordinal);
            string text = File.ReadAllText(path, Encoding.UTF8);
            List<string> row = new List<string>();
            StringBuilder field = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i <= text.Length; i++)
            {
                char c = i < text.Length ? text[i] : '\n';
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else if (c == '"')
                        quoted = false;
                    else
                        field.Append(c);
                }
                else if (c == '"')
                    quoted = true;
                else if (c == ';')
                {
                    row.Add(field.ToString());
                    field.Clear();
                }
                else if (c == '\n')
                {
                    row.Add(field.ToString());
                    field.Clear();
                    if (row.Count > 1)
                        titles[row[0]] = row[1];
                    row.Clear();
                }
                else if (c != '\r')
                    field.Append(c);
            }
            return titles;
        }

        private static string Value(BlkChild child, string name)
        {
            BlkParam param = child.Get(name);
            return param == null ? string.Empty : param.Value;
        }

        private static int Int(BlkChild child, string name)
        {
            int value;
            return int.TryParse(Value(child, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static string Unquote(string value)
        {
            return value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"' ? value.Substring(1, value.Length - 2) : value;
        }
    }

    // The per-axis settings of War Thunder's Axis setup dialog (machine.blk axes{<axis>{...}}) and what the game uses
    // when the file does not set one: machine.blk only holds the differences from its base preset
    // (controls{basePresetPaths{default}}), whose values come from DB\WarThunder\base-presets.tsv (a development copy, not
    // shipped; without it exports write every tuned attribute explicitly) (the official presets
    // with their base chains resolved, tools\research\wt_base_presets.py), then from the code default
    // (controlsPreset.nut getDefaultAxis).
    internal static class WarThunderAxisSettings
    {
        public const string FileName = "base-presets.tsv";
        // controlsPreset.nut loadBasePresetsFromBlk: a file without base preset gets this one.
        public const string NoBasePreset = "hotkey.empty_ver1";

        public static readonly string[] Keys = { "innerDeadzone", "nonlinearity", "kMul", "kAdd", "relative", "relSens", "relStep", "keepDisabledValue", "rangeMin", "rangeMax" };
        private static readonly HashSet<string> BoolKeys = new HashSet<string>(StringComparer.Ordinal) { "inverse", "relative", "keepDisabledValue" };
        private static readonly HashSet<string> ZeroRangeMin = new HashSet<string>(StringComparer.Ordinal) { "throttle", "helicopter_collective", "human_uav_throttle", "gm_sight_distance" };

        private static string loadedFrom;
        private static Dictionary<string, Dictionary<string, double>> table;
        private static HashSet<string> known;

        public static double CodeDefault(string axis, string key)
        {
            switch (key)
            {
                case "innerDeadzone": return 0.05;
                case "rangeMin": return ZeroRangeMin.Contains(axis ?? string.Empty) ? 0.0 : -1.0;
                case "rangeMax": return 1.0;
                case "kMul": return 1.0;
                case "relSens": return 1.0;
                default: return 0.0;
            }
        }

        // "wt/config/hotkeys/hotkey.keyboard_ver1.blk" -> "hotkey.keyboard_ver1" (the game drops the legacy "wt/").
        public static string PresetName(string path)
        {
            string name = (path ?? string.Empty).Trim().Replace('\\', '/');
            if (name.Length == 0)
                return NoBasePreset;
            int slash = name.LastIndexOf('/');
            if (slash >= 0)
                name = name.Substring(slash + 1);
            foreach (string extension in new[] { ".blkx", ".blk" })
            {
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - extension.Length);
            }
            return name;
        }

        private static void Load()
        {
            string path = GameDataPaths.DbFile("WarThunder", FileName);
            if (table != null && string.Equals(loadedFrom, path, StringComparison.OrdinalIgnoreCase))
                return;
            table = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            loadedFrom = path;
            if (!File.Exists(path))
                return;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (raw.Length == 0 || raw[0] == '#')
                    continue;
                string[] parts = raw.Split('\t');
                double value;
                if (parts.Length < 4)
                    continue;
                if (parts[1] == "*")
                {
                    known.Add(parts[0]);
                    continue;
                }
                if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    continue;
                Dictionary<string, double> values;
                string axisKey = parts[0] + "|" + parts[1];
                if (!table.TryGetValue(axisKey, out values))
                {
                    values = new Dictionary<string, double>(StringComparer.Ordinal);
                    table[axisKey] = values;
                }
                values[parts[2]] = value;
            }
        }

        public static bool IsKnownPreset(string preset)
        {
            Load();
            return known.Contains(preset ?? string.Empty);
        }

        public static void WarnUnknownPreset(string preset, List<string> warnings)
        {
            if (IsKnownPreset(preset))
                return;
            string warning = "War Thunder base preset '" + preset + "' is not in JoyPro's table (" + FileName + "); axis settings the file does not set are taken as the game's code defaults, which may differ from what the game uses.";
            if (!warnings.Contains(warning))
                warnings.Add(warning);
        }

        // What the game uses for an attribute the file does not set.
        public static double BaseValue(string preset, string axis, string key)
        {
            Load();
            Dictionary<string, double> values;
            double value;
            if (table.TryGetValue((preset ?? string.Empty) + "|" + axis, out values) && values.TryGetValue(key, out value))
                return value;
            return CodeDefault(axis, key);
        }

        // What the game uses: the file's attribute, else the base preset's.
        public static double Effective(BlkChild axis, string preset, string key)
        {
            BlkParam line = axis.Get(key);
            double value;
            return line != null && TryParse(line, out value) ? value : BaseValue(preset, axis.Name, key);
        }

        public static bool TryParse(BlkParam param, out double value)
        {
            value = 0;
            if (param.Type == "b")
            {
                string text = (param.Value ?? string.Empty).Trim().ToLowerInvariant();
                if (text == "yes" || text == "true" || text == "on" || text == "1")
                    value = 1;
                else if (!(text == "no" || text == "false" || text == "off" || text == "0"))
                    return false;
                return true;
            }
            return double.TryParse(param.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public static bool Same(double a, double b)
        {
            return Math.Abs(a - b) < 5e-6;
        }

        // The game's limits: dead zone 0..0.5 (MAX_DEADZONE), nonlinearity 0..3 (MAX_NONLINEARITY 4 shown = file + 1).
        public static double Clamp(string key, double value, string relation, List<string> warnings)
        {
            double clamped = value;
            if (BoolKeys.Contains(key))
                return value >= 0.5 ? 1 : 0;
            if (key == "innerDeadzone")
                clamped = Math.Max(0.0, Math.Min(WarThunderAxisModel.MaxDeadzone, value));
            else if (key == "nonlinearity")
                clamped = Math.Max(0.0, Math.Min(WarThunderAxisModel.MaxNonlinearity, value));
            if (!Same(clamped, value))
                warnings.Add("Relation '" + relation + "': " + key + " " + AxisTuning.Num(value) + " is outside what War Thunder accepts; written as " + AxisTuning.Num(clamped) + ".");
            return clamped;
        }

        public static BlkParam Render(string key, double value)
        {
            if (BoolKeys.Contains(key))
                return new BlkParam { Name = key, Type = "b", Value = value >= 0.5 ? "yes" : "no" };
            return new BlkParam { Name = key, Type = "r", Value = value.ToString("0.#####", CultureInfo.InvariantCulture) };
        }
    }

    // Saves\<uid>\production\machine.blk and global.blk are synced with the Gaijin server per account (machine.blk also
    // per machine.id). At login the game compares the file's top-level "version:i=N" with the server's: a higher
    // server version replaces the file, an equal version with other content (or a lower server version) uploads the
    // file (aces.exe online_storage::process_gblk_meta_with_type; observed 2026-10-10 in .game_logs). JoyPro's export
    // keeps the version (equal version, new content: the file wins). A restored older copy has an older version and
    // must get one above the file it replaces.
    internal static class WarThunderCloudVersion
    {
        private static readonly Regex VersionLine = new Regex(@"\A(\uFEFF?)version:i\s*=\s*(\d+)", RegexOptions.Compiled);

        public static bool IsSyncedFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            string name = Path.GetFileName(path);
            string full = Path.GetFullPath(path);
            return (string.Equals(name, "machine.blk", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "global.blk", StringComparison.OrdinalIgnoreCase)) &&
                full.IndexOf(@"\WarThunder\Saves\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static int? Read(string path)
        {
            try
            {
                if (!IsSyncedFile(path) || !File.Exists(path))
                    return null;
                using (StreamReader reader = new StreamReader(path))
                {
                    Match m = VersionLine.Match(reader.ReadLine() ?? string.Empty);
                    return m.Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : (int?)null;
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // Sets the file's version to previous + 1 when it is not above previous; true when the file changed.
        public static bool RaiseAbove(string path, int? previous)
        {
            if (!previous.HasValue || !IsSyncedFile(path))
                return false;
            int? current = Read(path);
            if (!current.HasValue || current.Value > previous.Value)
                return false;
            byte[] bytes = File.ReadAllBytes(path);
            string text = new UTF8Encoding(false).GetString(bytes);
            Match m = VersionLine.Match(text);
            if (!m.Success)
                return false;
            string replaced = text.Substring(0, m.Groups[2].Index) + (previous.Value + 1).ToString(CultureInfo.InvariantCulture) + text.Substring(m.Groups[2].Index + m.Groups[2].Length);
            File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(replaced));
            return true;
        }
    }

    // War Thunder's game-wide control options (FfbModels\WarThunder.cs) in machine.blk: each key is set in its block
    // (created when missing); every other line keeps its text.
    internal static class WarThunderSettingsBlk
    {
        private sealed class Setting
        {
            public string Key;
            public string[] Path;
            public string BlkName;
            // File value = shown value * Scale; null = yes/no.
            public double? Scale;
        }


        private static readonly Setting[] Settings =
        {
            new Setting { Key = "joyFX", Path = new[] { "content", "gui_options", "OPTIONS_MODE_GAMEPLAY" }, BlkName = "USEROPT_JOYFX" },
            new Setting { Key = "autotrim", Path = new[] { "content", "gui_options", "OPTIONS_MODE_GAMEPLAY" }, BlkName = "USEROPT_AUTOTRIM" },
            new Setting { Key = "forceGain", Path = new[] { "content", "settings" }, BlkName = "forceGain", Scale = 1.0 / 50.0 },
            new Setting { Key = "aileronsMultiplier", Path = new[] { "content", "settings" }, BlkName = "aileronsMultiplier", Scale = 0.01 },
            new Setting { Key = "elevatorMultiplier", Path = new[] { "content", "settings" }, BlkName = "elevatorMultiplier", Scale = 0.01 },
            new Setting { Key = "rudderMultiplier", Path = new[] { "content", "settings" }, BlkName = "rudderMultiplier", Scale = 0.01 },
            new Setting { Key = "helicopterCyclicRollMultiplier", Path = new[] { "content", "settings" }, BlkName = "helicopterCyclicRollMultiplier", Scale = 0.01 },
            new Setting { Key = "helicopterCyclicPitchMultiplier", Path = new[] { "content", "settings" }, BlkName = "helicopterCyclicPitchMultiplier", Scale = 0.01 },
            new Setting { Key = "helicopterPedalsMultiplier", Path = new[] { "content", "settings" }, BlkName = "helicopterPedalsMultiplier", Scale = 0.01 },
            new Setting { Key = "invertY", Path = new[] { "content", "settings" }, BlkName = "invertY" },
            new Setting { Key = "invertYHelicopter", Path = new[] { "content", "settings" }, BlkName = "invertYHelicopter" },
            new Setting { Key = "invertT", Path = new[] { "content", "settings" }, BlkName = "invertT" },
            new Setting { Key = "holdThrottleForWEP", Path = new[] { "content", "controls", "params" }, BlkName = "holdThrottleForWEP" }
        };

        // The game reads the file value as a float and turns it back into the slider's whole percent by cutting off:
        // 0.59 is 0.58999997f and shows (and is saved again) as 58. A whole-number UI value is therefore written a hair
        // above it (0.590005); reading rounds it back. Seen in game 2026-10-11 (Roll sensitivity 59 -> 58).
        private static string ScaledValue(double wanted, double scale)
        {
            double value = wanted * scale;
            if (Math.Abs(wanted - Math.Round(wanted)) < 1e-9 && wanted != 0)
                value += scale * 0.0005 * Math.Sign(wanted);
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        public static Dictionary<string, double> Read(BlkDocument document)
        {
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (Setting setting in Settings)
            {
                BlkBlock block = document.FindBlock(setting.Path);
                BlkParam param = block == null ? null : document.Params(block).FirstOrDefault(p => p.Name == setting.BlkName);
                double value;
                if (param == null || !WarThunderAxisSettings.TryParse(param, out value))
                    continue;
                values[setting.Key] = setting.Scale.HasValue ? Math.Round(value / setting.Scale.Value, 2) : value;
            }
            return values;
        }

        public static bool Apply(BlkDocument document, Dictionary<string, double> values, List<string> changes)
        {
            bool changed = false;
            foreach (IGrouping<string, Setting> group in Settings.Where(s => values.ContainsKey(s.Key)).GroupBy(s => string.Join("/", s.Path)))
            {
                string[] path = group.First().Path;
                BlkBlock block = document.FindBlock(path);
                List<BlkParam> current = block == null ? new List<BlkParam>() : document.Params(block);
                List<BlkParam> set = new List<BlkParam>();
                foreach (Setting setting in group)
                {
                    double wanted = values[setting.Key];
                    BlkParam rendered = setting.Scale.HasValue
                        ? new BlkParam { Name = setting.BlkName, Type = "r", Value = ScaledValue(wanted, setting.Scale.Value) }
                        : new BlkParam { Name = setting.BlkName, Type = "b", Value = wanted >= 0.5 ? "yes" : "no" };
                    BlkParam existing = current.FirstOrDefault(p => p.Name == setting.BlkName);
                    double existingValue;
                    double renderedValue;
                    if (existing != null && existing.Type == rendered.Type && WarThunderAxisSettings.TryParse(existing, out existingValue) &&
                        WarThunderAxisSettings.TryParse(rendered, out renderedValue) &&
                        (WarThunderAxisSettings.Same(existingValue, renderedValue) || (setting.Scale.HasValue && WarThunderAxisSettings.Same(existingValue, wanted * setting.Scale.Value))))
                        continue;
                    set.Add(rendered);
                    changes.Add(string.Join("/", path.Skip(1)) + " " + rendered.Render());
                }
                if (set.Count > 0 && document.SetBlockParams(path, set))
                    changed = true;
            }
            if (values.ContainsKey("autotrim") && ApplyHelpersMode(document, values["autotrim"] >= 0.5, changes))
                changed = true;
            return changed;
        }

        // Autotrim is not a setting of its own: at login the game derives it from the control mode
        // (USEROPT_HELPERS_MODE 0 mouse aim, 1 simplified, 2 realistic, 3 full-real; full-real = autotrim off, every
        // other mode = on), and its Autotrim switch changes the mode instead (aircrafthelpers.nut onHelpersChanged:
        // on -> min(mode, realistic), off -> full-real). JoyPro does the same, or the game would undo the value.
        private static bool ApplyHelpersMode(BlkDocument document, bool autotrim, List<string> changes)
        {
            string[] path = { "content", "gui_options", "OPTIONS_MODE_GAMEPLAY" };
            BlkBlock block = document.FindBlock(path);
            BlkParam current = block == null ? null : document.Params(block).FirstOrDefault(p => p.Name == "USEROPT_HELPERS_MODE");
            double mode;
            int now = current != null && WarThunderAxisSettings.TryParse(current, out mode) ? (int)mode : 0;
            int wanted = autotrim ? Math.Min(now, 2) : 3;
            if (current != null && wanted == now)
                return false;
            if (current == null && autotrim)
                return false;
            BlkParam rendered = new BlkParam { Name = "USEROPT_HELPERS_MODE", Type = "i", Value = wanted.ToString(CultureInfo.InvariantCulture) };
            changes.Add("gui_options/OPTIONS_MODE_GAMEPLAY " + rendered.Render() + " (control mode " + (wanted == 3 ? "full-real" : "realistic") + ": the game ties Autotrim to it)");
            return document.SetBlockParams(path, new List<BlkParam> { rendered });
        }
    }
}
