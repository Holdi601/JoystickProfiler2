using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Games
{
    // Hell Let Loose: Vietnam reads keyboard/mouse and XInput gamepads only (no DirectInput, no HID): a "virtual-only"
    // game (docs/games/hll-vietnam.md, docs/games/one-device-games.md).
    //  - Gamepad: the game has three fixed gamepad layouts (Options > Controls, GameUserSettings.ini
    //    SelectedGamepadLayout=LayoutA|B|C). JoyPro drives it through the virtual Xbox 360 pad (engine vjoy mode
    //    HLLVietnam xbox): the catalog's "Pad_*" actions carry the default gamepad key per layout, so the planner puts
    //    each physical input on the control the game expects. Nothing is written for the gamepad.
    //  - Keyboard: the helicopter pilot keys ("<Action>_Keyboard" mappings in
    //    %LOCALAPPDATA%\HLLVietnam\Saved\SaveGames\EnhancedInputUserSettings.sav), only with --include-keyboard.
    //  - Catalog: JoyPro's own table of the default helicopter gamepad layouts and pilot keys. engine hllv refresh-db
    //    reads the input assets from the installed game's IoStore container (UnrealIoStore) with the game's own Oodle
    //    library, or, only with the user's consent (--oodle-dll), one another installed game ships (FindOodle,
    //    AllowedForeignOodle); the build of 2026-10 ships none, so without a yes the table is used.
    internal sealed class HllVietnamIntegration : IGameIntegration, IGameOwnershipTracking, IGameGamepadLayout, IGameDeviceLimit
    {
        public int MaxJoysticks { get { return 0; } }
        public string SuggestedMode { get { return "xbox"; } }
        public string DeviceLimitNote { get { return "Reads no joystick, only Xbox controllers (XInput): fly it with the virtual Xbox pad."; } }
        public IReadOnlyList<string> AvoidVJoyAxes { get { return new string[0]; } }

        public string GameId { get { return "HLLVietnam"; } }
        public string DisplayName { get { return "Hell Let Loose: Vietnam"; } }
        public IReadOnlyList<string> Aliases { get { return new[] { "hllv", "hllvietnam", "hellletloosevietnam" }; } }
        public string DbFolder { get { return "HLLVietnam"; } }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return false; } }
        public string Notes { get { return "XInput only: fly it with the virtual Xbox pad (engine vjoy mode HLLVietnam xbox; needs ViGEmBus): JoyPro puts your HOTAS on the controls of the gamepad layout you picked in the game. Keyboard pilot keys are written only with --include-keyboard. Close the game first."; } }
        public IReadOnlyList<string> ProcessNames { get { return new[] { "HLLVietnam-Win64-Shipping", "HLLVietnam" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        public GameFeature KeyboardBinds { get { return GameFeature.Yes; } }
        public GameFeature ModifierBinds { get { return GameFeature.No; } }

        public const string PadPrefix = "Pad_";
        private const string KeyboardSuffix = "_Keyboard";
        private const string OodleVariable = "JOYPRO_OODLE_DLL";

        // The default helicopter gamepad layouts (read from the game's IMC_Gamepad_ControlScheme_1/_B/_C and helicopter
        // seat contexts, build of 2026-10): JoyPro's fallback when the game files cannot be read.
        private static readonly string[][] DefaultPadActions =
        {
            //          id                                  title                                   kind     A                               B                           C
            new[] { "Pad_Helicopter_Ascend", "Collective up (ascend)", "axis", "Gamepad_RightTriggerAxis", "Gamepad_LeftTriggerAxis", "Gamepad_RightTriggerAxis" },
            new[] { "Pad_Helicopter_Descend", "Collective down (descend)", "axis", "Gamepad_LeftTriggerAxis", "Gamepad_RightTriggerAxis", "Gamepad_LeftTriggerAxis" },
            new[] { "Pad_Helicopter_PitchForward", "Cyclic pitch (forward = nose down)", "axis", "Gamepad_LeftY", "Gamepad_RightY", "Gamepad_LeftY" },
            new[] { "Pad_Helicopter_RollRight", "Cyclic roll", "axis", "Gamepad_LeftX", "Gamepad_RightX", "Gamepad_LeftX" },
            new[] { "Pad_Helicopter_YawRight_Gamepad", "Yaw (pedals)", "axis", "Gamepad_RightX", "Gamepad_LeftX", "" },
            new[] { "Pad_Helicopter_YawRotateLeft", "Yaw left (layout C)", "button", "", "", "Gamepad_LeftShoulder" },
            new[] { "Pad_Helicopter_YawRotateRight", "Yaw right (layout C)", "button", "", "", "Gamepad_RightShoulder" },
            new[] { "Pad_HelicopterStabilityHold", "Stability (hold)", "button", "Gamepad_FaceButton_Right", "Gamepad_FaceButton_Right", "Gamepad_FaceButton_Right" },
            new[] { "Pad_HelicopterStabilityToggle", "Auto-level (toggle)", "button", "Gamepad_LeftThumbstick", "Gamepad_LeftThumbstick", "Gamepad_LeftThumbstick" },
            new[] { "Pad_Helicopter_SupplyDrop", "Supply drop", "button", "Gamepad_DPad_Right", "Gamepad_DPad_Right", "Gamepad_DPad_Right" },
            new[] { "Pad_Helicopter_FireFlare", "Fire recon flare", "button", "Gamepad_DPad_Up", "Gamepad_DPad_Up", "Gamepad_DPad_Right" },
            new[] { "Pad_Helicopter_MedicGamepad", "Medic", "button", "Gamepad_FaceButton_Bottom", "Gamepad_FaceButton_Bottom", "Gamepad_FaceButton_Bottom" },
            new[] { "Pad_Helicopter_MedicRadial", "Medic radial", "button", "Gamepad_RightShoulder", "Gamepad_RightShoulder", "Gamepad_RightShoulder" },
            new[] { "Pad_DriverLookRightRate", "Look left/right", "axis", "Gamepad_RightX", "Gamepad_LeftX", "Gamepad_RightX" },
            new[] { "Pad_DriverLookUpRate", "Look up/down", "axis", "Gamepad_RightY", "Gamepad_LeftY", "Gamepad_RightY" },
            new[] { "Pad_EnableSeatFreeLook", "Free look (hold)", "button", "Gamepad_RightShoulder", "Gamepad_LeftShoulder", "" },
            new[] { "Pad_SwitchSeat_0", "Next seat", "button", "Gamepad_FaceButton_Top", "Gamepad_FaceButton_Top", "Gamepad_FaceButton_Top" }
        };

        private static readonly string[] DefaultKeyboardActions =
        {
            "Driver_IncreaseCollective", "Driver_ReduceCollective", "Driver_PitchForwards", "Driver_PitchBackwards", "Driver_RollLeft", "Driver_RollRight",
            "Driver_YawLeft", "Driver_YawRight", "Driver_Auto-Level", "Driver_FireReconFlare", "Driver_HelicopterSupplyDrop"
        };

        private static readonly string[] Layouts = { "A", "B", "C" };

        // The gamepad contexts per layout (HLL_GameInstance holds them in this order).
        private static readonly Dictionary<string, string> LayoutContexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "IMC_Gamepad_ControlScheme_1", "A" }, { "IMC_Gamepad_ControlScheme_B", "B" }, { "IMC_Gamepad_ControlScheme_C", "C" }
        };

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HLLVietnam", "Saved", "SaveGames", "EnhancedInputUserSettings.sav");
            return File.Exists(path) ? new List<string> { path } : new List<string>();
        }

        // ------------------------------------------------------------------ gamepad layout

        // The layout the user picked in the game (GameUserSettings.ini SelectedGamepadLayout), "A" when unknown.
        public static string SelectedLayout(List<string> warnings)
        {
            string ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HLLVietnam", "Saved", "Config", "Windows", "GameUserSettings.ini");
            string overridePath = Environment.GetEnvironmentVariable("JOYPRO_HLLV_SETTINGS");
            if (!string.IsNullOrWhiteSpace(overridePath))
                ini = overridePath;
            try
            {
                if (File.Exists(ini))
                {
                    Match match = Regex.Match(File.ReadAllText(ini), @"^\s*SelectedGamepadLayout\s*=\s*Layout([A-Za-z])\s*$", RegexOptions.Multiline);
                    if (match.Success && Layouts.Contains(match.Groups[1].Value.ToUpperInvariant()))
                        return match.Groups[1].Value.ToUpperInvariant();
                }
            }
            catch (IOException ex)
            {
                warnings?.Add("GameUserSettings.ini could not be read: " + ex.Message);
            }
            return "A";
        }

        public Dictionary<string, string> GamepadLayout(GameCatalog catalog, List<string> warnings)
        {
            string layout = SelectedLayout(warnings);
            Dictionary<string, string> output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameAction action in catalog.Actions.Where(a => a.Id.StartsWith(PadPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                string key = KeyForLayout(action.NativeId, layout);
                if (key.Length > 0)
                    output[action.Id] = key;
            }
            return output;
        }

        // "A:Gamepad_LeftY|B:Gamepad_RightY|C:Gamepad_LeftY" (or "*:key" for every layout) -> the key of the layout.
        public static string KeyForLayout(string nativeId, string layout)
        {
            string fallback = string.Empty;
            foreach (string part in (nativeId ?? string.Empty).Split('|'))
            {
                int colon = part.IndexOf(':');
                if (colon <= 0)
                    continue;
                string which = part.Substring(0, colon).Trim();
                string key = part.Substring(colon + 1).Trim();
                if (string.Equals(which, layout, StringComparison.OrdinalIgnoreCase))
                    return key;
                if (which == "*")
                    fallback = key;
            }
            return fallback;
        }

        // ------------------------------------------------------------------ keyboard keys (unchanged behaviour)

        private static GvasKeyMapping FindMapping(GvasKeyMappings file, string actionId)
        {
            return file.Mappings.FirstOrDefault(m => m.Slot == 0 && string.Equals(m.MappingName, actionId + KeyboardSuffix, StringComparison.Ordinal));
        }

        private static bool IsPadAction(string id)
        {
            return (id ?? string.Empty).StartsWith(PadPrefix, StringComparison.OrdinalIgnoreCase);
        }

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            GvasKeyMappings file;
            if (!GvasKeyMappings.TryLoad(configPath, result.Errors, out file))
                return result;

            foreach (GameAction action in catalog.Actions.Where(a => !IsPadAction(a.Id)))
            {
                GvasKeyMapping mapping = FindMapping(file, action.Id);
                if (mapping == null)
                    continue;
                string key = GameKeyCodes.NameFromUnrealKey(mapping.CurrentKey);
                if (key.Length == 0)
                {
                    result.Warnings.Add(action.Id + " uses '" + mapping.CurrentKey + "', which is not a keyboard key JoyPro knows; skipped.");
                    continue;
                }
                result.Bindings.Add(new GameInputBinding { ActionId = action.Id, Device = "Keyboard", Input = key, IsAxis = false });
            }
            return result;
        }

        private static string Encode(GameInputBinding binding, out string unrealKey)
        {
            unrealKey = string.Empty;
            if (!binding.IsKeyboard)
                return "the game has no joystick input; bind a keyboard key or use the virtual Xbox pad (engine vjoy mode HLLVietnam xbox).";
            if (binding.Reformers != null && binding.Reformers.Count > 0)
                return "the game does not support key combinations.";
            if (!GameKeyCodes.TryGetUnrealKey(binding.Input, out unrealKey))
                return "key '" + binding.Input + "' has no Unreal key name.";
            return string.Empty;
        }

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            int padBinds = request.Bindings.Count(b => IsPadAction(b.ActionId));
            if (padBinds > 0)
                result.Steps.Add(padBinds.ToString(CultureInfo.InvariantCulture) + " gamepad action bind(s): the game's gamepad layouts are fixed, so nothing is written for them; " +
                                 "they reach the game through the virtual Xbox pad (engine vjoy mode HLLVietnam xbox, layout " + SelectedLayout(result.Warnings) + ").");
            GvasKeyMappings file;
            if (!GvasKeyMappings.TryLoad(request.ConfigPath, result.Errors, out file))
                return result;

            Dictionary<string, GameInputBinding> wanted = new Dictionary<string, GameInputBinding>(StringComparer.Ordinal);
            Dictionary<string, string> wantedKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (GameInputBinding binding in request.Bindings.Where(b => !IsPadAction(b.ActionId)))
            {
                string unrealKey;
                string reason = Encode(binding, out unrealKey);
                if (reason.Length == 0 && wanted.ContainsKey(binding.ActionId))
                    reason = "the game keeps one key per action; relation '" + wanted[binding.ActionId].RelationName + "' is used.";
                if (reason.Length > 0)
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
                    continue;
                }
                wanted[binding.ActionId] = binding;
                wantedKeys[binding.ActionId] = unrealKey;
            }

            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(request.ConfigPath, result.Warnings);
            Dictionary<string, HashSet<string>> ownedBefore = owned.ToDictionary(e => e.Key, e => new HashSet<string>(e.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            if (request.Mode == GameExportMode.Clean || request.Mode == GameExportMode.MergeOverwrite)
                owned.Clear();

            foreach (GameAction action in request.Catalog.Actions.Where(a => !IsPadAction(a.Id)))
            {
                GvasKeyMapping mapping = FindMapping(file, action.Id);
                string key;
                if (wantedKeys.TryGetValue(action.Id, out key))
                {
                    if (mapping != null && string.Equals(mapping.CurrentKey, key, StringComparison.OrdinalIgnoreCase))
                    {
                        GameOwnershipStore.Add(owned, action.Id, key);
                        result.BindingsWritten++;
                        continue;
                    }
                    if (mapping != null && request.Mode == GameExportMode.Merge && !GameOwnershipStore.Contains(ownedBefore, action.Id, mapping.CurrentKey))
                    {
                        result.BindingsSkipped++;
                        result.Warnings.Add(action.Id + " already has '" + mapping.CurrentKey + "' in the game; merge keeps it.");
                        continue;
                    }
                    if (mapping == null)
                    {
                        mapping = new GvasKeyMapping { MappingName = action.Id + KeyboardSuffix };
                        file.Mappings.Add(mapping);
                    }
                    mapping.CurrentKey = key;
                    mapping.Raw = null;
                    file.Changed = true;
                    GameOwnershipStore.Add(owned, action.Id, key);
                    result.BindingsWritten++;
                    continue;
                }

                // Not bound in JoyPro: drop JoyPro's old key (merge-overwrite) or every changed key (clean), so the
                // game falls back to its default.
                bool release = mapping != null &&
                               (request.Mode == GameExportMode.Clean ||
                                (request.Mode == GameExportMode.MergeOverwrite && GameOwnershipStore.Contains(ownedBefore, action.Id, mapping.CurrentKey)));
                if (release)
                {
                    file.Mappings.Remove(mapping);
                    file.Changed = true;
                }
                else if (mapping != null && request.Mode != GameExportMode.MergeOverwrite && GameOwnershipStore.Contains(ownedBefore, action.Id, mapping.CurrentKey))
                {
                    GameOwnershipStore.Add(owned, action.Id, mapping.CurrentKey);
                }
            }

            if (request.DryRun)
            {
                result.Steps.Add(file.Changed ? "Dry run: the pilot keys would change." : "Dry run: nothing would change.");
                return result;
            }

            GameOwnershipStore.Save(request.ConfigPath, GameId, owned);
            if (!file.Changed)
            {
                result.Steps.Add("The pilot keys already match.");
                return result;
            }

            GameExportSafety.BackupBeforeOverwrite(request.ConfigPath, result.Warnings);
            file.Save(request.ConfigPath);
            result.FilesWritten.Add(request.ConfigPath);
            result.Steps.Add("Pilot keys written.");
            return result;
        }

        public void RecordImport(string configPath, List<GameInputBinding> applied, GameDeviceDirectory devices, GameIoResult result)
        {
            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(configPath, result.Warnings);
            foreach (GameInputBinding binding in applied)
            {
                string key;
                if (Encode(binding, out key).Length == 0)
                    GameOwnershipStore.Add(owned, binding.ActionId, key);
            }
            GameOwnershipStore.Save(configPath, GameId, owned);
            result.FilesWritten.Add(configPath + GameOwnershipStore.Suffix);
        }

        // ------------------------------------------------------------------ catalog from the game files

        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            string utoc = FindUtoc(sourcePath, result.Warnings);
            GameCatalog catalog = null;
            if (utoc == null)
            {
                result.Warnings.Add("The game's input container (HLLVietnam\\Content\\Paks\\HLLVietnam-Windows.utoc) was not found; pass --path with the game folder.");
            }
            else
            {
                string oodlePath = FindOodle(utoc, result.Warnings);
                if (oodlePath == null)
                    oodlePath = AllowedForeignOodle(utoc, result);
                if (oodlePath == null)
                {
                    string candidate = ForeignOodleCandidate(utoc, out string candidateGame);
                    result.Steps.Add("The game's input assets are Oodle-compressed and the game ships no Oodle library of its own, " +
                                     "so JoyPro's own list is used: in Xbox-pad mode only the pad keys of the three layouts matter, and JoyPro knows them." +
                                     (candidate != null ? " (" + candidateGame + " has one, " + Path.GetFileName(candidate) + "; JoyPro uses it only when you allow it: refresh-db --oodle-dll <path>, the GUI asks.)" : string.Empty));
                }
                else
                {
                    try
                    {
                        catalog = ReadCatalog(utoc, oodlePath, result);
                    }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is IndexOutOfRangeException || ex is System.Runtime.InteropServices.SEHException)
                    {
                        result.Warnings.Add("The game's input assets could not be read (" + ex.Message + "); JoyPro's built-in table of the default layouts is used.");
                        catalog = null;
                    }
                }
            }
            if (catalog == null)
            {
                catalog = BuiltInCatalog();
                result.Steps.Add("Catalog: JoyPro's built-in table of the default helicopter gamepad layouts A, B, C (build of 2026-10) and the keyboard pilot keys.");
            }
            catalog.GameId = GameId;
            return catalog;
        }

        public static GameCatalog BuiltInCatalog()
        {
            GameCatalog catalog = new GameCatalog { GameId = "HLLVietnam" };
            foreach (string id in DefaultKeyboardActions)
                catalog.Add(new GameAction { Id = id, Title = Title(id) + " (keyboard)", IsAxis = false, Category = "Helicopter keyboard" });
            foreach (string[] row in DefaultPadActions)
            {
                List<string> keys = new List<string>();
                for (int i = 0; i < 3; i++)
                {
                    if (row[3 + i].Length > 0)
                        keys.Add(Layouts[i] + ":" + row[3 + i]);
                }
                catalog.Add(new GameAction { Id = row[0], Title = row[1] + " (gamepad)", IsAxis = row[2] == "axis", Category = "Helicopter gamepad", NativeId = string.Join("|", keys) });
            }
            return catalog;
        }

        // Reads the Enhanced Input contexts and actions from the IoStore container: keyboard pilot mappings and the
        // gamepad keys of the helicopter actions per layout.
        private GameCatalog ReadCatalog(string utoc, string oodlePath, GameIoResult result)
        {
            List<string> warnings = new List<string>();
            using (OodleDecompressor oodle = OodleDecompressor.TryLoad(oodlePath, warnings))
            {
                result.Warnings.AddRange(warnings);
                if (oodle == null)
                    return null;
                using (UnrealIoStore store = UnrealIoStore.Open(utoc, oodle))
                {
                    Dictionary<string, int> packages = store.Files
                        .Where(f => f.Key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && f.Key.IndexOf("/Content/_WFL/Input/", StringComparison.OrdinalIgnoreCase) >= 0)
                        .GroupBy(f => Path.GetFileNameWithoutExtension(f.Key), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.OrderBy(f => f.Key.IndexOf("/Legacy/", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0).First().Value, StringComparer.OrdinalIgnoreCase);
                    List<InputKeyMapping> mappings = new List<InputKeyMapping>();
                    Dictionary<string, int> valueTypes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, int> package in packages)
                    {
                        bool layoutContext = LayoutContexts.ContainsKey(package.Key);
                        bool helicopterContext = package.Key.StartsWith("IMC_Helicopter", StringComparison.OrdinalIgnoreCase);
                        if (layoutContext || helicopterContext)
                            mappings.AddRange(EnhancedInputAssets.Mappings(new ZenPackage(store.ReadChunk(package.Value)), package.Key));
                    }
                    foreach (string action in mappings.Select(m => m.Action).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        int chunk;
                        if (packages.TryGetValue(action, out chunk))
                            valueTypes[action] = EnhancedInputAssets.ValueType(new ZenPackage(store.ReadChunk(chunk)));
                    }
                    GameCatalog catalog = BuildCatalog(mappings, valueTypes);
                    if (catalog.Actions.Count(a => IsPadAction(a.Id)) == 0)
                        throw new InvalidDataException("no helicopter gamepad mapping was found");
                    result.Steps.Add("Catalog read from " + utoc + " (" + mappings.Count.ToString(CultureInfo.InvariantCulture) + " mappings of the helicopter and gamepad contexts; Oodle: " + oodlePath + ").");
                    return catalog;
                }
            }
        }

        // The mappings -> catalog rows. Keyboard: "<Name>_Keyboard" mapping names of the helicopter contexts. Gamepad:
        // helicopter actions (and the seat camera) with Gamepad_* keys, per layout (the layout contexts) or for every
        // layout (the helicopter seat contexts).
        internal static GameCatalog BuildCatalog(List<InputKeyMapping> mappings, Dictionary<string, int> valueTypes)
        {
            GameCatalog catalog = new GameCatalog { GameId = "HLLVietnam" };
            foreach (string name in mappings
                         .Where(m => m.Context.StartsWith("IMC_Helicopter", StringComparison.OrdinalIgnoreCase) && m.MappingName.EndsWith(KeyboardSuffix, StringComparison.Ordinal))
                         .Select(m => m.MappingName.Substring(0, m.MappingName.Length - KeyboardSuffix.Length))
                         .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
                catalog.Add(new GameAction { Id = name, Title = Title(name) + " (keyboard)", IsAxis = false, Category = "Helicopter keyboard" });

            Dictionary<string, Dictionary<string, string>> pad = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (InputKeyMapping mapping in mappings.Where(m => m.Key.StartsWith("Gamepad_", StringComparison.OrdinalIgnoreCase) && m.Action.StartsWith("IA_", StringComparison.OrdinalIgnoreCase)))
            {
                string layout;
                bool layoutContext = LayoutContexts.TryGetValue(mapping.Context, out layout);
                bool flight = mapping.Action.IndexOf("Helicopter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              (layoutContext && (mapping.Action.StartsWith("IA_DriverLook", StringComparison.OrdinalIgnoreCase) ||
                                                 mapping.Action.Equals("IA_EnableSeatFreeLook", StringComparison.OrdinalIgnoreCase) ||
                                                 mapping.Action.StartsWith("IA_SwitchSeat", StringComparison.OrdinalIgnoreCase)));
                if (!flight || !(layoutContext || mapping.Context.StartsWith("IMC_Helicopter", StringComparison.OrdinalIgnoreCase)))
                    continue;
                string id = PadPrefix + mapping.Action.Substring(3);
                Dictionary<string, string> keys;
                if (!pad.TryGetValue(id, out keys))
                {
                    keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    pad[id] = keys;
                }
                string which = layoutContext ? layout : "*";
                if (!keys.ContainsKey(which))
                    keys[which] = mapping.Key;
            }
            foreach (KeyValuePair<string, Dictionary<string, string>> entry in pad.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                string action = "IA_" + entry.Key.Substring(PadPrefix.Length);
                int valueType;
                bool axis = valueTypes.TryGetValue(action, out valueType) ? valueType >= 1 : entry.Value.Values.Any(k => XboxKeyIsAxis(k));
                string nativeId = string.Join("|", Layouts.Concat(new[] { "*" }).Where(entry.Value.ContainsKey).Select(l => l + ":" + entry.Value[l]));
                string[] known = DefaultPadActions.FirstOrDefault(r => string.Equals(r[0], entry.Key, StringComparison.OrdinalIgnoreCase));
                catalog.Add(new GameAction
                {
                    Id = entry.Key,
                    Title = (known != null ? known[1] : Title(entry.Key.Substring(PadPrefix.Length))) + " (gamepad)",
                    IsAxis = axis,
                    Category = "Helicopter gamepad",
                    NativeId = nativeId
                });
            }
            return catalog;
        }

        private static bool XboxKeyIsAxis(string key)
        {
            return key.EndsWith("X", StringComparison.Ordinal) || key.EndsWith("Y", StringComparison.Ordinal) || key.EndsWith("Axis", StringComparison.Ordinal);
        }

        // "Driver_IncreaseCollective" -> "Increase collective".
        private static string Title(string id)
        {
            string value = id;
            foreach (string prefix in new[] { "Driver_", "Helicopter_" })
            {
                if (value.StartsWith(prefix, StringComparison.Ordinal))
                    value = value.Substring(prefix.Length);
            }
            value = Regex.Replace(value.Replace('_', ' ').Replace("-", " "), "(?<=[a-z])(?=[A-Z])", " ").Trim();
            return value.Length == 0 ? id : char.ToUpperInvariant(value[0]) + value.Substring(1).ToLowerInvariant();
        }

        private string FindUtoc(string sourcePath, List<string> warnings)
        {
            string folder = sourcePath;
            if (string.IsNullOrWhiteSpace(folder))
            {
                GameInstall install = GameInstalls.Find(GameId, warnings);
                folder = install != null && install.Installed ? install.InstallPath : null;
            }
            if (string.IsNullOrWhiteSpace(folder))
                return null;
            if (File.Exists(folder) && folder.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
                return folder;
            foreach (string candidate in new[] { Path.Combine(folder, "HLLVietnam", "Content", "Paks", "HLLVietnam-Windows.utoc"), Path.Combine(folder, "Content", "Paks", "HLLVietnam-Windows.utoc"), Path.Combine(folder, "HLLVietnam-Windows.utoc") })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        // Oodle (decision 2026-10-12, docs/games/hll-vietnam.md "Oodle"): first the Oodle library Hell Let Loose: Vietnam
        // itself ships (oo2core_*_win64.dll inside the game's own folder; no question needed). The build of 2026-10 ships
        // none (Oodle is linked into its exe). Then, only with the user's consent (the GUI asks once and passes
        // refresh-db --oodle-dll <path>), the library another installed game ships, loaded from where it is (never
        // copied), only for this refresh, and logged. Without either: JoyPro's own table.
        // JOYPRO_OODLE_DLL=none turns every Oodle library off (tests); other values are ignored.
        private static bool OodleOff(List<string> warnings)
        {
            string configured = Environment.GetEnvironmentVariable(OodleVariable);
            if (string.IsNullOrWhiteSpace(configured))
                return false;
            if (configured.Trim() == "none")
                return true;
            warnings?.Add(OodleVariable + " is ignored: an Oodle library of another game is used only with your consent (refresh-db --oodle-dll).");
            return false;
        }

        private static DirectoryInfo GameFolder(string utoc, out bool steam)
        {
            // The game folder: the one under "...\steamapps\common", else three levels above Content\Paks.
            DirectoryInfo paks = new FileInfo(utoc).Directory;
            DirectoryInfo game = paks;
            while (game != null && game.Parent != null && !string.Equals(game.Parent.Name, "common", StringComparison.OrdinalIgnoreCase))
                game = game.Parent;
            steam = game != null && game.Parent != null;
            if (!steam)
                game = paks != null && paks.Parent != null && paks.Parent.Parent != null && paks.Parent.Parent.Parent != null ? paks.Parent.Parent.Parent : paks;
            return game;
        }

        // The game's own Oodle library.
        private static string FindOodle(string utoc, List<string> warnings)
        {
            if (OodleOff(warnings))
                return null;
            bool steam;
            DirectoryInfo game = GameFolder(utoc, out steam);
            return game == null ? null : FindFile(game.FullName, "oo2core_*_win64.dll", 4);
        }

        // An Oodle library another game of the same Steam library ships (the one JoyPro asks the user about), with the
        // name of that game's folder; null when there is none.
        internal static string ForeignOodleCandidate(string utoc, out string gameName)
        {
            gameName = string.Empty;
            if (OodleOff(null))
                return null;
            bool steam;
            DirectoryInfo game = GameFolder(utoc, out steam);
            if (game == null || !steam)
                return null;
            foreach (string other in SafeDirectories(game.Parent.FullName).Where(d => !string.Equals(d, game.FullName, StringComparison.OrdinalIgnoreCase)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string found = FindFile(other, "oo2core_*_win64.dll", 3);
                if (found != null)
                {
                    gameName = Path.GetFileName(other);
                    return found;
                }
            }
            return null;
        }

        // The foreign library the user allowed (--oodle-dll): only an oo2core_*_win64.dll that exists, outside the game's
        // own folder; used where it is.
        private static string AllowedForeignOodle(string utoc, GameIoResult result)
        {
            string allowed = GameCommands.ForeignOodleDll;
            if (string.IsNullOrWhiteSpace(allowed) || OodleOff(null))
                return null;
            string path = Path.GetFullPath(allowed.Trim().Trim('"'));
            if (!File.Exists(path) || !Regex.IsMatch(Path.GetFileName(path), @"^oo2core_\d+_win64\.dll$", RegexOptions.IgnoreCase))
            {
                result.Warnings.Add("--oodle-dll must name an existing oo2core_<n>_win64.dll; JoyPro's own list is used.");
                return null;
            }
            string message = "Oodle: " + path + " (another game's library, used with your consent from where it is, not copied, only for this refresh).";
            result.Steps.Add(message);
            JoyPro.Logging.JoyProLog.Info("HLLVietnam", message);
            return path;
        }

        private static IEnumerable<string> SafeDirectories(string folder)
        {
            try
            {
                return Directory.GetDirectories(folder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new string[0];
            }
        }

        // engine hllv oodle [--path <game folder>]: what the GUI needs to ask (docs/games/hll-vietnam.md "Oodle").
        public string OodleStatus(string sourcePath, bool textOutput)
        {
            List<string> warnings = new List<string>();
            string utoc = FindUtoc(sourcePath, warnings);
            string own = utoc == null ? null : FindOodle(utoc, warnings);
            string gameName = string.Empty;
            string foreign = utoc == null || own != null ? null : ForeignOodleCandidate(utoc, out gameName);
            if (!textOutput)
            {
                return EngineSerialization.ToJson(new
                {
                    Container = utoc ?? string.Empty,
                    OwnDll = own ?? string.Empty,
                    ForeignDll = foreign ?? string.Empty,
                    ForeignGame = gameName,
                    Maker = "RAD Game Tools / Epic Games",
                    Warnings = warnings
                });
            }
            return "Hell Let Loose: Vietnam Oodle" + Environment.NewLine +
                   "Container: " + (utoc ?? "not found") + Environment.NewLine +
                   "Own library: " + (own ?? "none") + Environment.NewLine +
                   "Another game's library: " + (foreign == null ? "none" : foreign + " (" + gameName + ")") + Environment.NewLine;
        }

        private static string FindFile(string folder, string pattern, int depth)
        {
            try
            {
                string hit = Directory.GetFiles(folder, pattern).OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (hit != null)
                    return hit;
                if (depth <= 0)
                    return null;
                foreach (string sub in Directory.GetDirectories(folder))
                {
                    hit = FindFile(sub, pattern, depth - 1);
                    if (hit != null)
                        return hit;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
            return null;
        }
    }
}
