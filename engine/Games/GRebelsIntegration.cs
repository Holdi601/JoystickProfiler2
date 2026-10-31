using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Games
{
    // G-Rebels (Reakktor / Senatis, UE 5.8, raw input through the EnhancedRawInput plugin; docs/games/g-rebels.md).
    // The game gives every joystick switched on in OPTIONS > INPUT > DEVICES a channel (0..5) and its own controls tab
    // "JST<channel>":
    //   - axes: SaveOptions.sav AxisOptions, one entry per axis key "RawInput_Controller<channel>_Axis<X|Y|Z|Rx|Ry|Rz|Slider|Dial>"
    //     with a role (Yaw, Thrust, Roll, Pitch, Side strafe, Up/down, None), Inverted and DeadZone. JoyPro edits the role
    //     and Inverted of entries the game already saved (in place, GRebelsSaveOptions); it never adds entries.
    //   - buttons: EnhancedInputUserSettings.sav (next to it), player-mapped keys "<ACTION>JST<channel>" =
    //     "RawInput_Controller<channel>_Button<n>" (GvasKeyMappings, as in Hell Let Loose: Vietnam).
    // The device's channel comes from SaveOptions' Joysticks list (DeviceID = the HID path). A device that is not switched
    // on in the game is skipped with a note. Catalog: DB\GRebels\actions.tsv (JoyPro's table: the six axis roles and the
    // button actions seen on the game's joystick tab, 5th column = the role number / the mapping name).
    internal sealed class GRebelsIntegration : IGameIntegration
    {
        private const string ButtonsFile = "EnhancedInputUserSettings.sav";
        private const int RoleNone = 6;
        private static readonly string[] AxisNames = { "X", "Y", "Z", "Rx", "Ry", "Rz", "Slider", "Dial" };

        public string GameId { get { return "GRebels"; } }
        public string DisplayName { get { return "G-Rebels"; } }
        public IReadOnlyList<string> Aliases { get { return new[] { "grebels", "g-rebels" }; } }
        public string DbFolder { get { return "GRebels"; } }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return true; } }
        public string Notes
        {
            get
            {
                return "Experimental. Switch each stick on once in the game (OPTIONS > INPUT > DEVICES, a channel each) and change one axis role on its JST tab (the game saves the channel's axis rows only then); " +
                       "JoyPro then sets axis roles, invert and button binds of those channels. Close the game before exporting.";
            }
        }
        public IReadOnlyList<string> ProcessNames { get { return new[] { "G_Rebels-Win64-Shipping", "G_Rebels" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        public GameFeature KeyboardBinds { get { return GameFeature.No; } }
        public GameFeature ModifierBinds { get { return GameFeature.No; } }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "G_Rebels", "Saved", "SaveGames", "SaveOptions.sav");
            return File.Exists(path) ? new List<string> { path } : new List<string>();
        }

        // JoyPro's own table (the six axis roles of the JST tab and the button actions seen on it, 5th column = role number
        // / mapping name). A release ships no DB\GRebels, so refresh-db writes this table on the user's PC; rows the list
        // already has keep their entry. The game's input assets are not read.
        private static readonly string[][] OwnActions =
        {
            new[] { "Axis_Yaw", "Yaw", "axis", "Axis roles", "role:0" },
            new[] { "Axis_Thrust", "Thrust", "axis", "Axis roles", "role:1" },
            new[] { "Axis_Roll", "Roll", "axis", "Axis roles", "role:2" },
            new[] { "Axis_Pitch", "Pitch", "axis", "Axis roles", "role:3" },
            new[] { "Axis_SideStrafe", "Side strafe", "axis", "Axis roles", "role:4" },
            new[] { "Axis_UpDown", "Up / down", "axis", "Axis roles", "role:5" },
            new[] { "PrimaryFire", "Primary Fire", "button", "Buttons", "PRIMARYFIRE" }
        };

        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            GameCatalog catalog = new GameCatalog { GameId = GameId, SourcePath = "JoyPro's own G-Rebels table" };
            if (current != null)
            {
                foreach (GameAction action in current.Actions)
                    catalog.Add(action);
            }
            int added = 0;
            foreach (string[] row in OwnActions)
            {
                if (catalog.Contains(row[0]))
                    continue;
                catalog.Add(new GameAction { Id = row[0], Title = row[1], IsAxis = row[2] == "axis", Category = row[3], NativeId = row[4] });
                added++;
            }
            result.Steps.Add("JoyPro's own G-Rebels table: " + added.ToString(CultureInfo.InvariantCulture) + " new action(s); the game's input assets are not read.");
            return catalog;
        }

        private static string ButtonsPath(string configPath)
        {
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? string.Empty, ButtonsFile);
        }

        // ---- devices -----------------------------------------------------------------------------------------------

        private static GameDevice DeviceOfPath(GameDeviceDirectory devices, string path)
        {
            if (devices == null || string.IsNullOrWhiteSpace(path))
                return null;
            // The HID path is read with the device's capabilities (DirectInput), only when asked for.
            foreach (GameDevice device in devices.Devices.Where(d => string.IsNullOrWhiteSpace(d.HidPath)))
                devices.EnsureCapabilities(device, new List<string>());
            return devices.Devices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.HidPath) && string.Equals(d.HidPath.Trim(), path.Trim(), StringComparison.OrdinalIgnoreCase));
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

        // The channel the game gave the device (switched on in INPUT > DEVICES), -1 when none.
        private static int ChannelOf(GRebelsSaveOptions options, GameDevice device)
        {
            if (device == null || string.IsNullOrWhiteSpace(device.HidPath))
                return -1;
            GRebelsSaveOptions.Device saved = options.Devices.FirstOrDefault(d => d.Enabled && string.Equals(d.Path.Trim(), device.HidPath.Trim(), StringComparison.OrdinalIgnoreCase));
            return saved == null ? -1 : saved.Channel;
        }

        private static string AxisKey(int channel, int axisIndex)
        {
            return "RawInput_Controller" + channel.ToString(CultureInfo.InvariantCulture) + "_Axis" + AxisNames[axisIndex];
        }

        // ---- read ------------------------------------------------------------------------------------------------------

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            GRebelsSaveOptions options;
            if (!GRebelsSaveOptions.TryLoad(configPath, result.Errors, out options))
                return result;
            Dictionary<int, GameAction> roles = RoleActions(catalog);
            Dictionary<string, GameAction> buttons = ButtonActions(catalog);
            Dictionary<int, GameDevice> channels = new Dictionary<int, GameDevice>();
            foreach (GRebelsSaveOptions.Device saved in options.Devices.Where(d => d.Enabled))
            {
                GameDevice device = DeviceOfPath(devices, saved.Path);
                if (device == null)
                {
                    result.Warnings.Add("Channel " + saved.Channel.ToString(CultureInfo.InvariantCulture) + " (" + saved.Path + ") is not connected; its binds are not read.");
                    continue;
                }
                if (!channels.ContainsKey(saved.Channel))
                    channels[saved.Channel] = device;
            }
            foreach (GRebelsSaveOptions.Axis axis in options.Axes)
            {
                Match match = Regex.Match(axis.Key, @"^RawInput_Controller(\d+)_Axis(\w+)$");
                GameAction action;
                GameDevice device;
                if (!match.Success || !roles.TryGetValue(axis.Role, out action) || !channels.TryGetValue(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), out device))
                    continue;
                int index = Array.FindIndex(AxisNames, n => string.Equals(n, match.Groups[2].Value, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    continue;
                result.Bindings.Add(new GameInputBinding { ActionId = action.Id, Device = device.JoyProName, Input = GameInputTokens.AxisToken(index), IsAxis = true, Inverted = axis.Inverted });
            }
            string buttonsPath = ButtonsPath(configPath);
            GvasKeyMappings mappings;
            List<string> ignore = new List<string>();
            if (File.Exists(buttonsPath) && GvasKeyMappings.TryLoad(buttonsPath, ignore, out mappings))
            {
                foreach (GvasKeyMapping mapping in mappings.Mappings)
                {
                    Match name = Regex.Match(mapping.MappingName, @"^(.+)JST(\d+)$");
                    Match key = Regex.Match(mapping.CurrentKey, @"^RawInput_Controller(\d+)_Button(\d+)$");
                    GameAction action;
                    GameDevice device;
                    if (!name.Success || !key.Success || name.Groups[2].Value != key.Groups[1].Value || !buttons.TryGetValue(name.Groups[1].Value, out action) ||
                        !channels.TryGetValue(int.Parse(key.Groups[1].Value, CultureInfo.InvariantCulture), out device))
                        continue;
                    int button = int.Parse(key.Groups[2].Value, CultureInfo.InvariantCulture);
                    if (button < 1)
                        continue;
                    result.Bindings.Add(new GameInputBinding { ActionId = action.Id, Device = device.JoyProName, Input = GameInputTokens.ButtonToken(button - 1), IsAxis = false });
                }
            }
            return result;
        }

        // ---- write -----------------------------------------------------------------------------------------------------

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            GRebelsSaveOptions options;
            if (!GRebelsSaveOptions.TryLoad(request.ConfigPath, result.Errors, out options))
                return result;
            Dictionary<string, GameAction> byId = request.Catalog.Actions.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
            // channel -> axis key -> (role, inverted); channel -> mapping name -> key
            Dictionary<int, Dictionary<string, KeyValuePair<int, bool>>> axisWanted = new Dictionary<int, Dictionary<string, KeyValuePair<int, bool>>>();
            Dictionary<string, string> buttonWanted = new Dictionary<string, string>(StringComparer.Ordinal);
            HashSet<int> written = new HashSet<int>();
            Dictionary<int, GameAction> roleActions = RoleActions(request.Catalog);
            Dictionary<string, GameAction> buttonActions = ButtonActions(request.Catalog);
            // The game's button binds (read before the decisions: "only add" must not make a multi-bind).
            string buttonsPath = ButtonsPath(request.ConfigPath);
            GvasKeyMappings mappings = null;
            if (File.Exists(buttonsPath) && !GvasKeyMappings.TryLoad(buttonsPath, result.Warnings, out mappings))
                mappings = null;
            foreach (GameInputBinding binding in request.Bindings)
            {
                GameAction action;
                string reason = string.Empty;
                if (!byId.TryGetValue(binding.ActionId ?? string.Empty, out action))
                    reason = "not in the catalog.";
                else if (binding.IsKeyboard)
                    reason = "keyboard binds are not written to G-Rebels.";
                else if (binding.Reformers != null && binding.Reformers.Count > 0)
                    reason = "the game has no modifiers.";
                GameDevice device = reason.Length == 0 ? ResolveDevice(binding.Device, request.Devices) : null;
                if (device != null && string.IsNullOrWhiteSpace(device.HidPath))
                    request.Devices.EnsureCapabilities(device, new List<string>());
                int channel = device == null ? -1 : ChannelOf(options, device);
                if (reason.Length == 0 && device == null)
                    reason = "device '" + binding.Device + "' is not connected (the game keys its devices by their HID path).";
                else if (reason.Length == 0 && channel < 0)
                    reason = "'" + device.JoyProName + "' is not switched on in the game (OPTIONS > INPUT > DEVICES: give it a channel once).";
                JoyInput input = default(JoyInput);
                if (reason.Length == 0 && !GameInputTokens.TryParse(binding.Input, out input))
                    reason = "input '" + binding.Input + "' is not a joystick input.";
                string otherAction = string.Empty;
                if (reason.Length == 0 && action.IsAxis)
                {
                    int role;
                    if (input.Kind != JoyInputKind.Axis || input.Index < 0 || input.Index >= AxisNames.Length)
                        reason = "an axis role needs one of the axes X, Y, Z, Rx, Ry, Rz, Slider, Dial.";
                    else if (!TryRole(action, out role))
                        reason = "the catalog row has no role number.";
                    else
                    {
                        string key = AxisKey(channel, input.Index);
                        GRebelsSaveOptions.Axis saved = options.Axes.FirstOrDefault(a => a.Key == key && a.RoleDigit >= 0);
                        if (saved == null)
                            reason = "the game has not saved axis " + key + " yet (change one axis role on the JST" + channel.ToString(CultureInfo.InvariantCulture) + " tab once: the game saves the axis rows only then).";
                        // One role per axis: "only add" does not replace another role on it.
                        else if (request.Mode == GameExportMode.Add && saved.Role != RoleNone && saved.Role != role)
                            otherAction = roleActions.ContainsKey(saved.Role) ? roleActions[saved.Role].Id : "role " + saved.Role.ToString(CultureInfo.InvariantCulture);
                        else
                        {
                            if (!axisWanted.ContainsKey(channel))
                                axisWanted[channel] = new Dictionary<string, KeyValuePair<int, bool>>(StringComparer.Ordinal);
                            axisWanted[channel][key] = new KeyValuePair<int, bool>(role, binding.Inverted);
                        }
                    }
                }
                else if (reason.Length == 0)
                {
                    if (input.Kind != JoyInputKind.Button)
                        reason = "only buttons are written (hats go to the JST tab's POV rows, not yet supported).";
                    else
                    {
                        string key = "RawInput_Controller" + channel.ToString(CultureInfo.InvariantCulture) + "_Button" + (input.Index + 1).ToString(CultureInfo.InvariantCulture);
                        GvasKeyMapping user = request.Mode == GameExportMode.Add && mappings != null
                            ? mappings.Mappings.FirstOrDefault(m => string.Equals(m.CurrentKey, key, StringComparison.OrdinalIgnoreCase) && NativeOf(m.MappingName) != action.NativeId && buttonActions.ContainsKey(NativeOf(m.MappingName)))
                            : null;
                        if (user != null)
                            otherAction = buttonActions[NativeOf(user.MappingName)].Id;
                        else
                            buttonWanted[action.NativeId + "JST" + channel.ToString(CultureInfo.InvariantCulture)] = key;
                    }
                }
                if (otherAction.Length > 0)
                {
                    result.BindingsSkipped++;
                    result.SkippedTakenInputs.Add(new ExportSkippedBind
                    {
                        Game = GameId,
                        Device = binding.Device,
                        Input = binding.Input,
                        Relation = binding.RelationName,
                        Action = binding.ActionId,
                        GameAction = otherAction
                    });
                    continue;
                }
                if (reason.Length > 0)
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
                    continue;
                }
                written.Add(channel);
                result.BindingsWritten++;
            }

            // Channels JoyPro owns: switched on in the game and connected now. Merge-overwrite and nuke touch only these.
            HashSet<int> owned = new HashSet<int>(options.Devices.Where(d => d.Enabled && DeviceOfPath(request.Devices, d.Path) != null).Select(d => d.Channel));
            HashSet<int> nukeChannels = new HashSet<int>(request.NukeUnusedDevices ? owned.Where(c => !written.Contains(c)) : new int[0]);
            HashSet<int> wantedRoles = new HashSet<int>(axisWanted.Values.SelectMany(w => w.Values.Select(v => v.Key)));

            // Axes (one role per physical axis): JoyPro's role replaces the axis' role (merge, merge-overwrite, clean:
            // the profile wins on its input; "only add" wrote only free axes). Merge-overwrite and clean: another axis of
            // an owned channel holding a role JoyPro sets goes to None (a moved role fully moves); clean: every other
            // axis of a written channel; nuke: the owned channels without binds.
            foreach (GRebelsSaveOptions.Axis axis in options.Axes)
            {
                Match match = Regex.Match(axis.Key, @"^RawInput_Controller(\d+)_Axis");
                if (!match.Success || axis.RoleDigit < 0)
                    continue;
                int channel = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                Dictionary<string, KeyValuePair<int, bool>> wanted;
                KeyValuePair<int, bool> mine;
                if (axisWanted.TryGetValue(channel, out wanted) && wanted.TryGetValue(axis.Key, out mine))
                {
                    if (request.Mode == GameExportMode.Add && axis.Role == mine.Key)
                        continue;
                    options.SetRole(axis, mine.Key);
                    options.SetInverted(axis, mine.Value);
                }
                else if (axis.Role != RoleNone &&
                         ((wanted != null && request.Mode == GameExportMode.Clean) ||
                          ((request.Mode == GameExportMode.MergeOverwrite || request.Mode == GameExportMode.Clean) && owned.Contains(channel) && wantedRoles.Contains(axis.Role)) ||
                          nukeChannels.Contains(channel)))
                    options.SetRole(axis, RoleNone);
            }

            // Buttons: one key per action and channel. A mapping of another action on a key JoyPro writes goes (a
            // conflict; "only add" skipped those binds); merge-overwrite and clean: the action's mappings on other owned
            // channels go; clean: the other cataloged mappings of written channels; nuke: those of owned channels
            // without binds.
            bool buttonsChanged = false;
            if (mappings == null && buttonWanted.Count > 0)
                result.Warnings.Add("Button binds not written: " + ButtonsFile + " has no key profile yet (bind one button in the game once).");
            if (mappings != null)
            {
                HashSet<string> catalogNames = new HashSet<string>(request.Catalog.Actions.Where(a => !a.IsAxis).Select(a => a.NativeId), StringComparer.Ordinal);
                HashSet<string> wantedNatives = new HashSet<string>(buttonWanted.Keys.Select(NativeOf), StringComparer.Ordinal);
                foreach (GvasKeyMapping mapping in mappings.Mappings.ToList())
                {
                    Match name = Regex.Match(mapping.MappingName, @"^(.+)JST(\d+)$");
                    if (!name.Success || !catalogNames.Contains(name.Groups[1].Value) || buttonWanted.ContainsKey(mapping.MappingName))
                        continue;
                    int channel = int.Parse(name.Groups[2].Value, CultureInfo.InvariantCulture);
                    bool release = (written.Contains(channel) && request.Mode == GameExportMode.Clean) || nukeChannels.Contains(channel);
                    if (!release && request.Mode != GameExportMode.Add)
                        release = buttonWanted.Values.Contains(mapping.CurrentKey, StringComparer.OrdinalIgnoreCase);
                    if (!release && (request.Mode == GameExportMode.MergeOverwrite || request.Mode == GameExportMode.Clean) && owned.Contains(channel))
                        release = wantedNatives.Contains(name.Groups[1].Value);
                    if (release)
                    {
                        mappings.Mappings.Remove(mapping);
                        buttonsChanged = true;
                    }
                }
                foreach (KeyValuePair<string, string> wanted in buttonWanted)
                {
                    GvasKeyMapping mapping = mappings.Mappings.FirstOrDefault(m => m.MappingName == wanted.Key && m.Slot == 0);
                    if (mapping != null && string.Equals(mapping.CurrentKey, wanted.Value, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (mapping != null && request.Mode == GameExportMode.Add)
                    {
                        result.ConflictsKept++;
                        result.Warnings.Add(wanted.Key + " keeps the game's '" + mapping.CurrentKey + "' (add mode).");
                        continue;
                    }
                    if (mapping == null)
                    {
                        mapping = new GvasKeyMapping { MappingName = wanted.Key };
                        mappings.Mappings.Add(mapping);
                    }
                    mapping.CurrentKey = wanted.Value;
                    mapping.Raw = null;
                    buttonsChanged = true;
                }
            }

            if (!options.Changed && !buttonsChanged)
            {
                result.Steps.Add("The G-Rebels binds already match.");
                return result;
            }
            if (request.DryRun)
            {
                result.Steps.Add("Dry run: would change" + (options.Changed ? " SaveOptions.sav (axis roles)" : string.Empty) + (buttonsChanged ? " " + ButtonsFile + " (buttons)" : string.Empty) + ".");
                return result;
            }
            if (options.Changed)
            {
                GameExportSafety.BackupBeforeOverwrite(request.ConfigPath, result.Warnings);
                File.WriteAllBytes(request.ConfigPath, options.Bytes());
                result.FilesWritten.Add(request.ConfigPath);
                result.Steps.Add("Axis roles written (SaveOptions.sav, in place).");
            }
            if (buttonsChanged)
            {
                GameExportSafety.BackupBeforeOverwrite(buttonsPath, result.Warnings);
                mappings.Save(buttonsPath);
                result.FilesWritten.Add(buttonsPath);
                result.Steps.Add("Button binds written (" + ButtonsFile + ").");
            }
            return result;
        }

        // ---- catalog ---------------------------------------------------------------------------------------------------

        private static bool TryRole(GameAction action, out int role)
        {
            role = -1;
            Match match = Regex.Match(action.NativeId ?? string.Empty, @"^role:(\d)$");
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out role);
        }

        // "<native>" of a button mapping name "<native>JST<channel>", "" for other names.
        private static string NativeOf(string mappingName)
        {
            Match match = Regex.Match(mappingName ?? string.Empty, @"^(.+)JST(\d+)$");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static Dictionary<int, GameAction> RoleActions(GameCatalog catalog)
        {
            Dictionary<int, GameAction> roles = new Dictionary<int, GameAction>();
            foreach (GameAction action in catalog.Actions.Where(a => a.IsAxis))
            {
                int role;
                if (TryRole(action, out role) && !roles.ContainsKey(role))
                    roles[role] = action;
            }
            return roles;
        }

        private static Dictionary<string, GameAction> ButtonActions(GameCatalog catalog)
        {
            Dictionary<string, GameAction> buttons = new Dictionary<string, GameAction>(StringComparer.Ordinal);
            foreach (GameAction action in catalog.Actions.Where(a => !a.IsAxis && !string.IsNullOrWhiteSpace(a.NativeId)))
            {
                if (!buttons.ContainsKey(action.NativeId))
                    buttons[action.NativeId] = action;
            }
            return buttons;
        }
    }
}
