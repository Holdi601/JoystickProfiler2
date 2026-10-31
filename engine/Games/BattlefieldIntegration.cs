using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine.Games
{
    // Battlefield 6 and Battlefield 2042 (Frostbite): one-device games (docs/games/battlefield.md,
    // docs/games/one-device-games.md). The game reads one DirectInput joystick and stores no device in its binds, so a
    // HOTAS of several devices goes through the virtual joystick (engine vjoy mode Battlefield6 virtual).
    //  - Binds: the text profile "Documents\Battlefield 6\settings\steam\PROFSAVEbf6mp_profile" (and PROFSAVE_profile;
    //    2042: "Documents\Battlefield 2042\settings\PROFSAVE_profile"), lines
    //    "GstKeyBinding.<category>.<Concept>.<slot>.<axis|button|negate|type> <int>", sorted. type: 0 mouse, 1 pad,
    //    2 keyboard (button = DIK code, 255 = none), 3 joystick (axis = Frostbite IDA_* code, button = IDB_* code).
    //    "Unbound" is the enum's count: axis 56 (BF6) / 27 (2042), button 60.
    //  - Catalog: engine bf6 refresh-db reads the jet and helicopter concepts from the user's own profile file.
    //  - Joystick numbering: which IDA/IDB code a DirectInput axis, button or hat gets is not confirmed yet. JoyPro has a
    //    provisional table (BattlefieldJoystickMap) and writes joystick binds only once the table is confirmed by a live
    //    bind per input (tools/e2e/games/BattlefieldJoystickSurvey.ps1 writes TOOLS\Conversions\<game>JoystickMap.tsv,
    //    JoyPro's own shipped data, not game data). Until then
    //    it writes keyboard keys (--include-keyboard) and reads joystick binds with a warning.
    internal sealed class BattlefieldIntegration : IGameIntegration, IGameDeviceLimit, IGameBuiltInCatalog
    {
        public const int TypeMouse = 0;
        public const int TypePad = 1;
        public const int TypeKeyboard = 2;
        public const int TypeJoystick = 3;
        public const int UnboundButton = 60;
        public const int UnboundKey = 255;
        private const string Prefix = "GstKeyBinding.";
        private static readonly string[] FlightCategories = { "jet", "helicopter" };

        private readonly int version;

        public BattlefieldIntegration(int version)
        {
            this.version = version;
        }

        public int Version { get { return version; } }
        public string GameId { get { return "Battlefield" + version.ToString(CultureInfo.InvariantCulture); } }
        public string DisplayName { get { return "Battlefield " + version.ToString(CultureInfo.InvariantCulture); } }
        public IReadOnlyList<string> Aliases
        {
            get { return version == 6 ? new[] { "bf6", "battlefield6" } : new[] { "bf2042", "battlefield2042", "2042" }; }
        }
        public string DbFolder { get { return GameId; } }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return false; } }
        public string Notes
        {
            get
            {
                return "Experimental. " + DisplayName + " reads one joystick: use the virtual joystick (engine vjoy mode " + GameId + " virtual) for a HOTAS of several devices. " +
                       "Close the game first. The settings folder is synced by Steam Cloud / EA: start the game once after the export and check the binds.";
            }
        }
        public IReadOnlyList<string> ProcessNames { get { return version == 6 ? new[] { "bf6" } : new[] { "BF2042" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        public GameFeature KeyboardBinds { get { return GameFeature.Yes; } }
        public GameFeature ModifierBinds { get { return GameFeature.No; } }

        public int MaxJoysticks { get { return 1; } }
        public string SuggestedMode { get { return "virtual"; } }
        public string DeviceLimitNote { get { return "Reads one joystick and stores no device in its binds: put the whole HOTAS on one vJoy device."; } }
        // Players report that the game takes a joystick with Rx/Ry for an Xbox pad and hides its joystick page.
        public IReadOnlyList<string> AvoidVJoyAxes { get { return new[] { "rx", "ry" }; } }

        // The enum's count = "no axis" (BF6 knows IDA_Axis0..7, 2042 IDA_Axis0..3).
        public int UnboundAxis { get { return version == 6 ? 56 : 27; } }

        private string SettingsRoot()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(documents, version == 6 ? "Battlefield 6" : "Battlefield 2042", "settings");
        }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            List<string> found = new List<string>();
            string root = SettingsRoot();
            if (!Directory.Exists(root))
                return found;
            try
            {
                foreach (string file in Directory.GetFiles(root, "*_profile", SearchOption.AllDirectories))
                {
                    if (BattlefieldProfile.HasBinds(file))
                        found.Add(file);
                }
            }
            catch (Exception ex)
            {
                warnings?.Add(DisplayName + " settings could not be listed: " + ex.Message);
            }
            // The multiplayer profile first (BF6: PROFSAVEbf6mp_profile), then the newest.
            return found.OrderByDescending(f => Path.GetFileName(f).IndexOf("mp_profile", StringComparison.OrdinalIgnoreCase) >= 0)
                        .ThenByDescending(f => File.GetLastWriteTimeUtc(f)).ToList();
        }

        // The other profile files of the same folder: the game keeps one per mode (BF6: PROFSAVE_profile for the
        // campaign, PROFSAVEbf6mp_profile for multiplayer); an export writes all of them.
        private static List<string> SiblingProfiles(string configPath)
        {
            List<string> output = new List<string>();
            string folder = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? string.Empty;
            if (!Directory.Exists(folder))
                return output;
            foreach (string file in Directory.GetFiles(folder, "*_profile"))
            {
                if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(configPath), StringComparison.OrdinalIgnoreCase) && BattlefieldProfile.HasBinds(file))
                    output.Add(file);
            }
            return output;
        }

        // ------------------------------------------------------------------ catalog

        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            string path = sourcePath;
            if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
            {
                List<string> found = DiscoverConfigPaths(result.Warnings);
                path = found.FirstOrDefault();
            }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                result.Errors.Add("No " + DisplayName + " settings profile found (start the game once, or pass the PROFSAVE..._profile file).");
                return null;
            }
            BattlefieldProfile profile;
            if (!BattlefieldProfile.TryLoad(path, result.Errors, out profile))
                return null;

            GameCatalog catalog = new GameCatalog { GameId = GameId };
            foreach (string category in FlightCategories)
            {
                foreach (string concept in profile.Concepts(category))
                {
                    List<BattlefieldSlot> slots = profile.Slots(category, concept);
                    catalog.Add(new GameAction
                    {
                        Id = category + "." + concept,
                        Title = (category == "jet" ? "Jet" : "Helicopter") + ": " + ConceptTitle(concept),
                        IsAxis = IsAxisConcept(concept, slots, UnboundAxis),
                        Category = category == "jet" ? "Jet" : "Helicopter"
                    });
                }
            }
            result.Steps.Add("Read the jet and helicopter controls from " + path + " (" + catalog.Actions.Count.ToString(CultureInfo.InvariantCulture) + " action(s)).");
            return catalog;
        }

        // Before the game wrote its settings profile: the jet and helicopter concepts JoyPro knows (seen in Battlefield 6's
        // profile, 2026-10), the axes as the profile marks them ("negate 1"). The concepts of the user's own profile
        // replace them with the first refresh, import or export.
        private static readonly string[] BuiltInAxes = { "ConceptMoveFB", "ConceptPitch", "ConceptRoll", "ConceptYaw", "ConceptCameraPitch", "ConceptCameraYaw" };
        private static readonly string[] BuiltInButtons = { "ConceptFire", "ConceptZoom", "ConceptToggleCamera", "ConceptChangeVehicle", "ConceptSwitchToDynamicGadget1", "ConceptSwitchToDynamicGadget2" };

        public GameCatalog BuiltInCatalog()
        {
            GameCatalog catalog = new GameCatalog { GameId = GameId };
            foreach (string category in FlightCategories)
            {
                IEnumerable<KeyValuePair<string, bool>> concepts = BuiltInAxes.Select(a => new KeyValuePair<string, bool>(a, true))
                    .Concat(category == "helicopter" ? new[] { new KeyValuePair<string, bool>("ConceptBrake", true) } : new KeyValuePair<string, bool>[0])
                    .Concat(BuiltInButtons.Select(b => new KeyValuePair<string, bool>(b, false)));
                foreach (KeyValuePair<string, bool> concept in concepts)
                {
                    catalog.Add(new GameAction
                    {
                        Id = category + "." + concept.Key,
                        Title = (category == "jet" ? "Jet" : "Helicopter") + ": " + ConceptTitle(concept.Key),
                        IsAxis = concept.Value,
                        Category = category == "jet" ? "Jet" : "Helicopter"
                    });
                }
            }
            return catalog;
        }

        // Axis concepts: a slot with "negate 1" (the two halves of an axis) or a mouse slot on an axis.
        internal static bool IsAxisConcept(string concept, List<BattlefieldSlot> slots, int unboundAxis)
        {
            if (slots.Any(s => s.Negate == 1))
                return true;
            // mouse X / Y (Axis0X / Axis0Y); the wheel (another axis) steps through lists
            if (slots.Any(s => s.Type == TypeMouse && (s.Axis == 0 || s.Axis == 1) && s.Button >= 8))
                return true;
            return false;
        }

        private static readonly Dictionary<string, string> KnownTitles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "ConceptMoveFB", "Throttle (forward/back)" }, { "ConceptMoveLR", "Move left/right" }, { "ConceptPitch", "Pitch" }, { "ConceptRoll", "Roll" },
            { "ConceptYaw", "Yaw" }, { "ConceptCameraPitch", "Look up/down" }, { "ConceptCameraYaw", "Look left/right" }, { "ConceptFire", "Fire" },
            { "ConceptZoom", "Zoom" }, { "ConceptToggleCamera", "Change camera" }, { "ConceptBrake", "Brake" },
            { "ConceptFireCountermeasure", "Countermeasures" }, { "ConceptChangeVehicle", "Change seat / vehicle" }
        };

        internal static string ConceptTitle(string concept)
        {
            string title;
            if (KnownTitles.TryGetValue(concept, out title))
                return title;
            string name = concept.StartsWith("Concept", StringComparison.Ordinal) ? concept.Substring(7) : concept;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (i > 0 && ((char.IsUpper(c) && !char.IsUpper(name[i - 1])) || (char.IsDigit(c) && !char.IsDigit(name[i - 1]))))
                    sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ device

        // The one joystick the binds belong to: the vJoy device in virtual mode, else the profile's joystick (the
        // device of the most binds), else the first connected game controller.
        private string JoystickDevice(GameDeviceDirectory devices, List<string> warnings)
        {
            List<GameDevice> connected = devices == null ? new List<GameDevice>() : devices.Devices.Where(d => d.Connected && !string.IsNullOrEmpty(d.JoyProName)).ToList();
            bool virtualMode = EngineVJoy.LoadSettings().IsVirtual(GameId);
            if (virtualMode)
            {
                GameDevice vjoy = connected.FirstOrDefault(d => EngineVJoy.IsVJoyName(d.JoyProName));
                if (vjoy != null)
                    return vjoy.JoyProName;
                warnings.Add("Virtual mode: no vJoy device is connected; the joystick binds are read for the first game controller.");
            }
            GameDevice first = connected.FirstOrDefault(d => !EngineVJoy.IsVJoyName(d.JoyProName)) ?? connected.FirstOrDefault();
            if (first == null)
                return string.Empty;
            if (connected.Count(d => !EngineVJoy.IsVJoyName(d.JoyProName)) > 1 && !virtualMode)
                warnings.Add(DisplayName + " stores no device in its binds and reads one joystick; JoyPro reads them for " + first.JoyProName +
                             ". With several devices use the virtual joystick (engine vjoy mode " + GameId + " virtual).");
            return first.JoyProName;
        }

        // ------------------------------------------------------------------ read

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            BattlefieldProfile profile;
            if (!BattlefieldProfile.TryLoad(configPath, result.Errors, out profile))
                return result;
            BattlefieldJoystickMap map = BattlefieldJoystickMap.Load(this, result.Warnings);
            string device = null;
            bool provisionalNoted = false;
            foreach (GameAction action in catalog.Actions)
            {
                string category, concept;
                if (!SplitId(action.Id, out category, out concept))
                    continue;
                List<BattlefieldSlot> slots = profile.Slots(category, concept);

                // Joystick: every bound slot decoded; an axis action pairs the halves of one axis.
                List<KeyValuePair<BattlefieldSlot, BattlefieldCode>> decoded = new List<KeyValuePair<BattlefieldSlot, BattlefieldCode>>();
                foreach (BattlefieldSlot slot in slots.Where(s => s.Type == TypeJoystick && !IsFree(s, TypeJoystick)))
                {
                    BattlefieldCode code = map.Decode(slot, UnboundAxis, UnboundButton);
                    if (code == null)
                    {
                        result.Warnings.Add(action.Id + " slot " + slot.Slot.ToString(CultureInfo.InvariantCulture) + ": joystick code axis " + slot.Axis.ToString(CultureInfo.InvariantCulture) +
                                            " / button " + slot.Button.ToString(CultureInfo.InvariantCulture) + " is not in JoyPro's table; skipped.");
                        continue;
                    }
                    decoded.Add(new KeyValuePair<BattlefieldSlot, BattlefieldCode>(slot, code));
                }
                if (decoded.Count > 0)
                {
                    if (!map.Verified && !provisionalNoted)
                    {
                        provisionalNoted = true;
                        result.Warnings.Add(DisplayName + ": JoyPro's joystick numbering for this game is provisional (not confirmed by a live bind yet); check the imported joystick inputs.");
                    }
                    if (device == null)
                    {
                        device = JoystickDevice(devices, result.Warnings);
                        if (device.Length == 0)
                            result.Warnings.Add("No game controller is connected; the joystick binds were not read.");
                    }
                }
                if (!string.IsNullOrEmpty(device))
                {
                    if (action.IsAxis)
                        ReadAxis(action, decoded, device, result);
                    else
                    {
                        foreach (KeyValuePair<BattlefieldSlot, BattlefieldCode> entry in decoded)
                        {
                            if (entry.Value.Half != 0 || entry.Value.Token.StartsWith("JOY_BTN", StringComparison.Ordinal) == false)
                            {
                                result.Warnings.Add(action.Id + ": an axis (" + entry.Value.Name + ") on a button action is not read.");
                                continue;
                            }
                            result.Bindings.Add(new GameInputBinding { ActionId = action.Id, Device = device, Input = entry.Value.Token, IsAxis = false });
                        }
                    }
                }

                // Keyboard keys of button actions.
                if (!action.IsAxis)
                {
                    foreach (BattlefieldSlot slot in slots.Where(s => s.Type == TypeKeyboard && s.Button > 0 && s.Button != UnboundKey))
                    {
                        string key = GameKeyCodes.NameFromDik(slot.Button);
                        if (!string.IsNullOrEmpty(key))
                            result.Bindings.Add(new GameInputBinding { ActionId = action.Id, Device = "Keyboard", Input = key, IsAxis = false });
                    }
                }
            }
            return result;
        }

        // The game stores an axis as its two halves: the half that moves the action up in a slot with negate 0, the other
        // half in a slot with negate 1 (or one whole axis, negate = inverted). Halves of one axis in the opposite slots
        // = inverted.
        private void ReadAxis(GameAction action, List<KeyValuePair<BattlefieldSlot, BattlefieldCode>> decoded, string device, GameScanResult result)
        {
            foreach (IGrouping<string, KeyValuePair<BattlefieldSlot, BattlefieldCode>> axis in decoded.GroupBy(d => d.Value.Token, StringComparer.Ordinal))
            {
                List<KeyValuePair<BattlefieldSlot, BattlefieldCode>> parts = axis.ToList();
                if (axis.Key.StartsWith("JOY_BTN", StringComparison.Ordinal))
                {
                    result.Warnings.Add(action.Id + ": a button or hat (" + axis.Key + ") on an axis action is not read (bind it in the game).");
                    continue;
                }
                KeyValuePair<BattlefieldSlot, BattlefieldCode> whole = parts.FirstOrDefault(p => p.Value.Half == 0);
                bool inverted;
                if (whole.Value != null)
                    inverted = whole.Key.Negate == 1;
                else
                {
                    // + half in the negate-0 slot (or - half in the negate-1 slot) = normal.
                    int normal = parts.Count(p => (p.Value.Half > 0) == (p.Key.Negate != 1));
                    inverted = normal * 2 < parts.Count;
                    if (parts.Count == 1)
                        result.Warnings.Add(action.Id + " uses only one half of " + axis.Key + "; read as the whole axis.");
                }
                result.Bindings.Add(new GameInputBinding { ActionId = action.Id, Device = device, Input = axis.Key, IsAxis = true, Inverted = inverted });
            }
        }

        internal static bool SplitId(string id, out string category, out string concept)
        {
            category = string.Empty;
            concept = string.Empty;
            int dot = (id ?? string.Empty).IndexOf('.');
            if (dot <= 0 || dot >= id.Length - 1)
                return false;
            category = id.Substring(0, dot);
            concept = id.Substring(dot + 1);
            return true;
        }

        // ------------------------------------------------------------------ write

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            List<string> paths = new List<string> { request.ConfigPath };
            paths.AddRange(SiblingProfiles(request.ConfigPath));
            BattlefieldJoystickMap map = BattlefieldJoystickMap.Load(this, result.Warnings);

            // The joystick binds of one device (the game reads one; in virtual mode that is the vJoy device).
            List<GameInputBinding> joystick = request.Bindings.Where(b => !b.IsKeyboard).ToList();
            List<GameInputBinding> keyboard = request.Bindings.Where(b => b.IsKeyboard).ToList();
            string device = joystick.GroupBy(b => b.Device, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            foreach (GameInputBinding other in joystick.Where(b => !string.Equals(b.Device, device, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                Skip(result, other, DisplayName + " reads one joystick (" + device + " has the most binds); use the virtual joystick (engine vjoy mode " + GameId + " virtual).");
                joystick.Remove(other);
            }
            if (joystick.Count > 0 && !map.Verified)
            {
                result.Warnings.Add(joystick.Count.ToString(CultureInfo.InvariantCulture) + " joystick bind(s) not written: JoyPro's joystick numbering for " + DisplayName +
                                    " is not confirmed yet. One live bind per input settles it: tools\\e2e\\games\\BattlefieldJoystickSurvey.ps1 (docs/games/battlefield.md).");
                result.BindingsSkipped += joystick.Count;
                joystick.Clear();
            }

            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                BattlefieldProfile profile;
                if (!BattlefieldProfile.TryLoad(path, result.Errors, out profile))
                    return result;
                int written = Apply(profile, request, joystick, keyboard, map, result, i == 0);
                if (!profile.Changed)
                {
                    result.Steps.Add(path + ": already up to date.");
                    continue;
                }
                if (request.DryRun)
                {
                    result.Steps.Add("Dry run: " + path + " would get " + written.ToString(CultureInfo.InvariantCulture) + " bind(s).");
                    continue;
                }
                GameExportSafety.BackupBeforeOverwrite(path, result.Warnings);
                string error;
                if (!profile.Save(path, out error))
                {
                    result.Errors.Add(path + ": " + error);
                    return result;
                }
                result.FilesWritten.Add(path);
            }
            return result;
        }

        private int Apply(BattlefieldProfile profile, GameExportRequest request, List<GameInputBinding> joystick, List<GameInputBinding> keyboard, BattlefieldJoystickMap map,
                          GameIoResult result, bool count)
        {
            int written = 0;
            if (request.Mode == GameExportMode.Clean)
            {
                // Clean: JoyPro takes over the slots of the flight actions on the devices the export writes: the
                // joystick when it gets binds, the keyboard when keyboard keys are exported (keys of button actions that
                // JoyPro can read).
                bool keys = keyboard.Count > 0 && request.IncludeKeyboard;
                foreach (GameAction action in request.Catalog.Actions)
                {
                    string category, concept;
                    if (!SplitId(action.Id, out category, out concept))
                        continue;
                    foreach (BattlefieldSlot slot in profile.Slots(category, concept))
                    {
                        if (slot.Type == TypeJoystick && joystick.Count > 0)
                        {
                            profile.Set(slot, "axis", UnboundAxis);
                            profile.Set(slot, "button", UnboundButton);
                        }
                        else if (slot.Type == TypeKeyboard && keys && !action.IsAxis && !IsFree(slot, TypeKeyboard) && !string.IsNullOrEmpty(GameKeyCodes.NameFromDik(slot.Button)) &&
                                 !keyboard.Any(b => string.Equals(b.ActionId, action.Id, StringComparison.Ordinal) && string.Equals(b.Input, GameKeyCodes.NameFromDik(slot.Button), StringComparison.OrdinalIgnoreCase)))
                        {
                            profile.Set(slot, "button", UnboundKey);
                        }
                    }
                }
            }

            // "Only add" skips a bind on an input another flight action of the same vehicle uses (no multi-bind); the other
            // modes free that slot (a conflict). Merge-overwrite and clean also free the other slots of an action the
            // profile binds (keyboard ones only with --include-keyboard), so a moved action fully moves. Soldier and
            // ground vehicle binds never count.
            Dictionary<string, string> gameInputs = GameInputs(profile, request.Catalog, map);
            Func<GameInputBinding, string> otherUser = b =>
            {
                GameAction a = request.Catalog.Find(b.ActionId);
                if (a == null)
                    return string.Empty;
                foreach (string key in InputKeys(b, a, map))
                {
                    string user;
                    if (gameInputs.TryGetValue(key, out user) && !string.Equals(user, a.Id, StringComparison.Ordinal))
                        return user;
                }
                return string.Empty;
            };
            if (request.Mode == GameExportMode.Add)
            {
                foreach (GameInputBinding binding in joystick.Concat(keyboard).ToList())
                {
                    string other = otherUser(binding);
                    if (other.Length == 0)
                        continue;
                    joystick.Remove(binding);
                    keyboard.Remove(binding);
                    if (!count)
                        continue;
                    result.BindingsSkipped++;
                    result.SkippedTakenInputs.Add(new ExportSkippedBind
                    {
                        Game = GameId,
                        Device = binding.Device,
                        Input = binding.Input,
                        Relation = binding.RelationName,
                        Action = binding.ActionId,
                        GameAction = other
                    });
                }
            }
            else
            {
                FreeConflicts(profile, request, joystick.Concat(keyboard).ToList(), map);
            }

            HashSet<string> taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (GameInputBinding binding in joystick)
            {
                GameAction action = request.Catalog.Find(binding.ActionId);
                string category, concept;
                if (action == null || !SplitId(action.Id, out category, out concept))
                {
                    if (count) Skip(result, binding, "not a " + DisplayName + " action.");
                    continue;
                }
                JoyInput input;
                if (!GameInputTokens.TryParse(binding.Input, out input))
                {
                    if (count) Skip(result, binding, "'" + binding.Input + "' is not a joystick input.");
                    continue;
                }
                if (binding.Reformers != null && binding.Reformers.Count > 0)
                {
                    if (count) Skip(result, binding, "the game has no modifier buttons.");
                    continue;
                }
                string reason;
                if (action.IsAxis)
                {
                    BattlefieldCode positive, negative, whole;
                    if (!map.TryHalves(input, out positive, out negative, out reason))
                    {
                        // Battlefield 6 stores a bound axis as one whole axis in one slot, negate = inverted (seen by
                        // the survey); a table with only the whole axis code is written that way.
                        if (map.TryWhole(input, out whole))
                        {
                            BattlefieldSlot slot = PickSlot(profile, category, concept, TypeJoystick, 0, whole, request.Mode, taken);
                            if (slot == null)
                            {
                                if (count)
                                {
                                    result.ConflictsKept++;
                                    Skip(result, binding, "the joystick slots of " + action.Id + " are taken (merge keeps them).");
                                }
                                continue;
                            }
                            SetCode(profile, slot, whole);
                            profile.Set(slot, "negate", binding.Inverted ? 1 : 0);
                            // The action's other joystick slots (halves or another axis) would add to it: released
                            // unless the mode keeps the game's binds.
                            if (request.Mode == GameExportMode.Clean || request.Mode == GameExportMode.MergeOverwrite)
                            {
                                foreach (BattlefieldSlot other in profile.Slots(category, concept).Where(s => s.Type == TypeJoystick && s.Key != slot.Key && !taken.Contains(s.Key)))
                                {
                                    profile.Set(other, "axis", UnboundAxis);
                                    profile.Set(other, "button", UnboundButton);
                                }
                            }
                            written++;
                            continue;
                        }
                        if (count) Skip(result, binding, reason);
                        continue;
                    }
                    if (binding.Inverted)
                    {
                        BattlefieldCode swap = positive;
                        positive = negative;
                        negative = swap;
                    }
                    BattlefieldSlot up = PickSlot(profile, category, concept, TypeJoystick, 0, positive, request.Mode, taken);
                    BattlefieldSlot down = up == null ? null : PickSlot(profile, category, concept, TypeJoystick, 1, negative, request.Mode, taken);
                    if (up == null || down == null)
                    {
                        if (count)
                        {
                            result.ConflictsKept++;
                            Skip(result, binding, "the joystick slots of " + action.Id + " are taken (merge keeps them).");
                        }
                        continue;
                    }
                    SetCode(profile, up, positive);
                    SetCode(profile, down, negative);
                }
                else
                {
                    BattlefieldCode code;
                    if (!map.TryButton(input, out code, out reason))
                    {
                        if (count) Skip(result, binding, reason);
                        continue;
                    }
                    BattlefieldSlot target = PickSlot(profile, category, concept, TypeJoystick, 0, code, request.Mode, taken);
                    if (target == null)
                    {
                        if (count)
                        {
                            result.ConflictsKept++;
                            Skip(result, binding, "every joystick slot of " + action.Id + " is taken (merge keeps them).");
                        }
                        continue;
                    }
                    SetCode(profile, target, code);
                }
                written++;
            }

            foreach (GameInputBinding binding in keyboard)
            {
                GameAction action = request.Catalog.Find(binding.ActionId);
                string category, concept;
                if (action == null || !SplitId(action.Id, out category, out concept))
                    continue;
                if (action.IsAxis)
                {
                    if (count) Skip(result, binding, "keyboard keys on an axis are not written (bind the key in the game).");
                    continue;
                }
                int dik;
                if (!GameKeyCodes.TryGetDik(binding.Input, out dik))
                {
                    if (count) Skip(result, binding, "key '" + binding.Input + "' has no DirectInput code.");
                    continue;
                }
                if (binding.Reformers != null && binding.Reformers.Count > 0)
                {
                    if (count) Skip(result, binding, "the game has no key combinations.");
                    continue;
                }
                BattlefieldSlot target = PickSlot(profile, category, concept, TypeKeyboard, 0, new BattlefieldCode { Property = "button", Value = dik }, request.Mode, taken);
                if (target == null)
                {
                    if (count)
                    {
                        result.ConflictsKept++;
                        Skip(result, binding, "every keyboard slot of " + action.Id + " is taken (merge keeps them).");
                    }
                    continue;
                }
                profile.Set(target, "button", dik);
                profile.Set(target, "axis", 0);
                written++;
            }
            if (count)
                result.BindingsWritten += written;
            return written;
        }

        // The inputs of a profile bind as slot keys ("<vehicle>|<type>|<property>|<value>"), empty when not encodable.
        private static List<string> InputKeys(GameInputBinding binding, GameAction action, BattlefieldJoystickMap map)
        {
            List<string> keys = new List<string>();
            string category, concept;
            if (!SplitId(action.Id, out category, out concept))
                return keys;
            if (binding.IsKeyboard)
            {
                int dik;
                if (!action.IsAxis && GameKeyCodes.TryGetDik(binding.Input, out dik))
                    keys.Add(category + "|" + TypeKeyboard.ToString(CultureInfo.InvariantCulture) + "|button|" + dik.ToString(CultureInfo.InvariantCulture));
                return keys;
            }
            JoyInput input;
            if (!GameInputTokens.TryParse(binding.Input, out input))
                return keys;
            string reason;
            BattlefieldCode positive, negative, code;
            List<BattlefieldCode> codes = new List<BattlefieldCode>();
            if (action.IsAxis && map.TryHalves(input, out positive, out negative, out reason))
                codes.AddRange(new[] { positive, negative });
            else if (action.IsAxis && map.TryWhole(input, out code))
                codes.Add(code);
            else if (!action.IsAxis && map.TryButton(input, out code, out reason))
                codes.Add(code);
            foreach (BattlefieldCode c in codes)
                keys.Add(category + "|" + TypeJoystick.ToString(CultureInfo.InvariantCulture) + "|" + c.Property + "|" + c.Value.ToString(CultureInfo.InvariantCulture));
            return keys;
        }

        // The keys of a bound slot JoyPro reads (a joystick slot holds an axis or a button value JoyPro's table knows, a
        // keyboard slot of a button action a key); slots the import does not read never count.
        private List<string> SlotKeys(BattlefieldSlot slot, GameAction action, BattlefieldJoystickMap map)
        {
            List<string> keys = new List<string>();
            string prefix = slot.Category + "|" + slot.Type.ToString(CultureInfo.InvariantCulture) + "|";
            if (slot.Type == TypeKeyboard)
            {
                if (!action.IsAxis && !IsFree(slot, TypeKeyboard) && !string.IsNullOrEmpty(GameKeyCodes.NameFromDik(slot.Button)))
                    keys.Add(prefix + "button|" + slot.Button.ToString(CultureInfo.InvariantCulture));
            }
            else if (slot.Type == TypeJoystick && !IsFree(slot, TypeJoystick) && map.Decode(slot, UnboundAxis, UnboundButton) != null)
            {
                if (slot.Axis >= 0 && slot.Axis != UnboundAxis)
                    keys.Add(prefix + "axis|" + slot.Axis.ToString(CultureInfo.InvariantCulture));
                if (slot.Button >= 0 && slot.Button != UnboundButton)
                    keys.Add(prefix + "button|" + slot.Button.ToString(CultureInfo.InvariantCulture));
            }
            return keys;
        }

        // The flight actions' bound keyboard and joystick slots per key, with their action.
        private Dictionary<string, string> GameInputs(BattlefieldProfile profile, GameCatalog catalog, BattlefieldJoystickMap map)
        {
            Dictionary<string, string> inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (GameAction action in catalog.Actions)
            {
                string category, concept;
                if (!SplitId(action.Id, out category, out concept))
                    continue;
                foreach (BattlefieldSlot slot in profile.Slots(category, concept))
                    foreach (string key in SlotKeys(slot, action, map).Where(k => !inputs.ContainsKey(k)))
                        inputs[key] = action.Id;
            }
            return inputs;
        }

        // Merge, merge-overwrite, clean: a flight action's slot on an input the profile gives another action is freed
        // (a conflict). Merge-overwrite and clean: an action the profile binds loses its other slots on the joystick
        // (codes JoyPro's table reads, with a game controller connected) and, with --include-keyboard, its keyboard keys (button actions:
        // keys on an axis are not read by the import, so they stay).
        private void FreeConflicts(BattlefieldProfile profile, GameExportRequest request, List<GameInputBinding> bindings, BattlefieldJoystickMap map)
        {
            Dictionary<string, string> wanted = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, HashSet<string>> wantedOf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (GameInputBinding binding in bindings)
            {
                GameAction action = request.Catalog.Find(binding.ActionId);
                if (action == null)
                    continue;
                HashSet<string> own;
                if (!wantedOf.TryGetValue(action.Id, out own))
                    wantedOf[action.Id] = own = new HashSet<string>(StringComparer.Ordinal);
                foreach (string key in InputKeys(binding, action, map))
                {
                    own.Add(key);
                    if (!wanted.ContainsKey(key))
                        wanted[key] = action.Id;
                }
            }
            bool moves = request.Mode == GameExportMode.MergeOverwrite || request.Mode == GameExportMode.Clean;
            bool joystickOwned = moves && JoystickDevice(request.Devices, new List<string>()).Length > 0;
            foreach (GameAction action in request.Catalog.Actions)
            {
                string category, concept;
                if (!SplitId(action.Id, out category, out concept))
                    continue;
                HashSet<string> own;
                wantedOf.TryGetValue(action.Id, out own);
                foreach (BattlefieldSlot slot in profile.Slots(category, concept).Where(s => s.Type == TypeJoystick || s.Type == TypeKeyboard).ToList())
                {
                    List<string> keys = SlotKeys(slot, action, map);
                    if (keys.Count == 0 || (own != null && keys.Any(own.Contains)))
                        continue;
                    string user;
                    bool conflict = keys.Any(k => wanted.TryGetValue(k, out user) && !string.Equals(user, action.Id, StringComparison.Ordinal));
                    bool moved = own != null && moves &&
                                 (slot.Type == TypeKeyboard ? request.IncludeKeyboard : joystickOwned);
                    if (!conflict && !moved)
                        continue;
                    if (slot.Type == TypeKeyboard)
                        profile.Set(slot, "button", UnboundKey);
                    else
                    {
                        profile.Set(slot, "axis", UnboundAxis);
                        profile.Set(slot, "button", UnboundButton);
                    }
                }
            }
        }

        // A joystick code into a slot: the code's property gets the value, the other one "nothing".
        private void SetCode(BattlefieldProfile profile, BattlefieldSlot slot, BattlefieldCode code)
        {
            profile.Set(slot, "axis", code.Property == "axis" ? code.Value : UnboundAxis);
            profile.Set(slot, "button", code.Property == "button" ? code.Value : UnboundButton);
        }

        // The slot a bind goes to (of the device type and with the wanted negate): one already holding the same code,
        // else a free one, else (merge overwrite / clean) the first one; when the action has none, a new slot.
        private BattlefieldSlot PickSlot(BattlefieldProfile profile, string category, string concept, int type, int negate, BattlefieldCode code,
                                         GameExportMode mode, HashSet<string> taken)
        {
            List<BattlefieldSlot> all = profile.Slots(category, concept).Where(s => s.Type == type && (s.Negate == 1) == (negate == 1)).ToList();
            List<BattlefieldSlot> slots = all.Where(s => !taken.Contains(s.Key)).ToList();
            BattlefieldSlot pick = slots.FirstOrDefault(s => s.Get(code.Property) == code.Value)
                                   ?? slots.FirstOrDefault(s => IsFree(s, type))
                                   ?? (mode == GameExportMode.MergeOverwrite || mode == GameExportMode.Clean ? slots.FirstOrDefault() : null);
            if (pick == null && (slots.Count == 0 || mode == GameExportMode.MergeOverwrite || mode == GameExportMode.Clean))
            {
                pick = profile.AddSlot(category, concept, type, type == TypeKeyboard ? 0 : UnboundAxis, type == TypeKeyboard ? UnboundKey : UnboundButton);
                if (negate == 1)
                    profile.Set(pick, "negate", 1);
            }
            if (pick != null)
                taken.Add(pick.Key);
            return pick;
        }

        private bool IsFree(BattlefieldSlot slot, int type)
        {
            if (type == TypeKeyboard)
                return slot.Button == UnboundKey || slot.Button <= 0;
            return (slot.Axis == UnboundAxis || slot.Axis < 0) && (slot.Button == UnboundButton || slot.Button < 0);
        }

        private static void Skip(GameIoResult result, GameInputBinding binding, string reason)
        {
            result.BindingsSkipped++;
            result.Warnings.Add(binding.Describe() + ": " + reason);
        }
    }
    // One bind slot: GstKeyBinding.<category>.<Concept>.<slot>.<property> lines.
    internal sealed class BattlefieldSlot
    {
        public string Category;
        public string Concept;
        public int Slot;
        public readonly Dictionary<string, int> Values = new Dictionary<string, int>(StringComparer.Ordinal);

        public string Key { get { return Category + "." + Concept + "." + Slot.ToString(CultureInfo.InvariantCulture); } }

        public int Get(string property)
        {
            int value;
            return Values.TryGetValue(property, out value) ? value : -1;
        }

        public int Type { get { return Get("type"); } }
        public int Axis { get { return Get("axis"); } }
        public int Button { get { return Get("button"); } }
        public int Negate { get { return Get("negate"); } }
    }

    // The text profile: every line kept as it is; the GstKeyBinding lines indexed. New lines go to their sorted place
    // (the game writes the GstKeyBinding lines sorted).
    internal sealed class BattlefieldProfile
    {
        private const string Prefix = "GstKeyBinding.";
        private readonly List<string> lines = new List<string>();
        private readonly Dictionary<string, BattlefieldSlot> slots = new Dictionary<string, BattlefieldSlot>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> lineOf = new Dictionary<string, int>(StringComparer.Ordinal);
        private bool trailingNewline;

        public bool Changed { get; private set; }

        public static bool HasBinds(string path)
        {
            try
            {
                using (StreamReader reader = new StreamReader(path, Encoding.UTF8))
                {
                    for (int i = 0; i < 20000; i++)
                    {
                        string line = reader.ReadLine();
                        if (line == null)
                            return false;
                        if (line.StartsWith(Prefix, StringComparison.Ordinal))
                            return true;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return false;
        }

        public static bool TryLoad(string path, List<string> errors, out BattlefieldProfile profile)
        {
            profile = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                errors.Add("Battlefield profile not found: " + path);
                return false;
            }
            string text;
            try
            {
                text = File.ReadAllText(path, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                errors.Add("Battlefield profile could not be read: " + ex.Message);
                return false;
            }
            if (text.IndexOf(Prefix, StringComparison.Ordinal) < 0)
            {
                errors.Add(path + " holds no GstKeyBinding lines; it is not a Battlefield controls profile.");
                return false;
            }
            BattlefieldProfile p = new BattlefieldProfile();
            p.trailingNewline = text.EndsWith("\n", StringComparison.Ordinal);
            string[] raw = text.Replace("\r\n", "\n").Split('\n');
            int count = p.trailingNewline ? raw.Length - 1 : raw.Length;
            for (int i = 0; i < count; i++)
                p.lines.Add(raw[i]);
            p.Reindex();
            profile = p;
            return true;
        }

        private void Reindex()
        {
            slots.Clear();
            lineOf.Clear();
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (!line.StartsWith(Prefix, StringComparison.Ordinal))
                    continue;
                int space = line.IndexOf(' ');
                if (space < 0)
                    continue;
                string key = line.Substring(0, space);
                string[] parts = key.Split('.');
                if (parts.Length != 5)
                    continue;
                int slot, value;
                if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out slot) ||
                    !int.TryParse(line.Substring(space + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    continue;
                string slotKey = parts[1] + "." + parts[2] + "." + parts[3];
                BattlefieldSlot entry;
                if (!slots.TryGetValue(slotKey, out entry))
                {
                    entry = new BattlefieldSlot { Category = parts[1], Concept = parts[2], Slot = slot };
                    slots[slotKey] = entry;
                }
                entry.Values[parts[4]] = value;
                lineOf[key] = i;
            }
        }

        public IEnumerable<string> Concepts(string category)
        {
            return slots.Values.Where(s => s.Category == category).Select(s => s.Concept).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal);
        }

        public List<BattlefieldSlot> Slots(string category, string concept)
        {
            return slots.Values.Where(s => s.Category == category && s.Concept == concept).OrderBy(s => s.Slot).ToList();
        }

        public void Set(BattlefieldSlot slot, string property, int value)
        {
            string key = Prefix + slot.Key + "." + property;
            string text = key + " " + value.ToString(CultureInfo.InvariantCulture);
            int index;
            if (lineOf.TryGetValue(key, out index))
            {
                if (lines[index] != text)
                {
                    lines[index] = text;
                    Changed = true;
                }
            }
            else
            {
                InsertSorted(text);
                Reindex();
                Changed = true;
            }
            slot.Values[property] = value;
        }

        // A new slot after the concept's last one, with the game's "nothing bound" values.
        public BattlefieldSlot AddSlot(string category, string concept, int type, int axis, int button)
        {
            int next = Slots(category, concept).Select(s => s.Slot + 1).DefaultIfEmpty(0).Max();
            BattlefieldSlot slot = new BattlefieldSlot { Category = category, Concept = concept, Slot = next };
            foreach (KeyValuePair<string, int> value in new[]
                     {
                         new KeyValuePair<string, int>("axis", axis), new KeyValuePair<string, int>("button", button),
                         new KeyValuePair<string, int>("negate", 0), new KeyValuePair<string, int>("type", type)
                     })
                InsertSorted(Prefix + slot.Key + "." + value.Key + " " + value.Value.ToString(CultureInfo.InvariantCulture));
            Reindex();
            Changed = true;
            return slots[slot.Key];
        }

        private void InsertSorted(string line)
        {
            string key = line.Substring(0, line.IndexOf(' '));
            int first = -1, last = -1, at = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!lines[i].StartsWith(Prefix, StringComparison.Ordinal))
                    continue;
                if (first < 0)
                    first = i;
                last = i;
                int space = lines[i].IndexOf(' ');
                string other = space < 0 ? lines[i] : lines[i].Substring(0, space);
                if (at < 0 && string.CompareOrdinal(other, key) > 0)
                    at = i;
            }
            if (at < 0)
                at = last < 0 ? lines.Count : last + 1;
            lines.Insert(at, line);
        }

        public bool Save(string path, out string error)
        {
            error = string.Empty;
            try
            {
                string text = string.Join("\n", lines) + (trailingNewline ? "\n" : string.Empty);
                string temp = path + ".joypro-tmp";
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                SafeFileIo.ReplaceFile(temp, path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    // One joystick code in a slot: the property that holds it ("axis" = IDA_*, "button" = IDB_*) and its value.
    internal sealed class BattlefieldCode
    {
        public string Property = "button";
        public int Value;
        // JoyPro's token (the whole axis for a half), +1 / -1 for a half, 0 for a whole axis, a button or a hat direction.
        public string Token = string.Empty;
        public int Half;

        public string Name { get { return Token + (Half > 0 ? "+" : Half < 0 ? "-" : string.Empty); } }
    }

    // DirectInput input <-> Frostbite joystick code.
    //  - The enums, from the game's own type names in order: IDA_Axis0X,0Y,0Z,0XPos,0YPos,0ZPos,0XNeg,0YNeg,0ZNeg (0-8),
    //    Axis1X,1Y,1XPos,1YPos,1XNeg,1YNeg (9-14), Axis2 (15-20), Axis3 (21-26), BF6 also Axis4-6 (27-53, with Z) and
    //    Axis7X/Y (54-55); IDB_Lup .. Rtrigger2 (0-17), IDB_XButton1..18 (18-35), IDB_PosZAxis, NegZAxis, XRotationPos,
    //    XRotationNeg, YRotationPos/Neg, ZRotationPos/Neg (36-43), IDB_Pov1North, South, West, East .. Pov4East (44-59).
    //  - Provisional (the scheme of Battlefield 4, whose joystick binds a community editor decoded, moved onto the newer
    //    axis enum): X / Y = IDA_Axis1X / Axis1Y (halves Axis1XPos/XNeg/YPos/YNeg); Z, Rx, Ry, Rz only as analog halves
    //    on the button codes 36-43; sliders as the trigger codes 14 (+) / 15 (-); DirectInput button n = code n - 1
    //    (0-35); hat n = 44 + 4 (n - 1) + North/South/West/East. Not confirmed for Battlefield 6 / 2042.
    //  - TOOLS\Conversions\<game>JoystickMap.tsv (Battlefield6JoystickMap.tsv, Battlefield2042JoystickMap.tsv) replaces
    //    the table and marks it confirmed. It is JoyPro's own measured numbering and ships with JoyPro (release
    //    packaging strips the game DB folders, tools/ReleaseContent.psm1, but keeps TOOLS\Conversions); the live survey
    //    (tools/e2e/games/BattlefieldJoystickSurvey.ps1) writes it: "JOY_X<TAB>axis<TAB>9", "JOY_X+<TAB>axis<TAB>11",
    //    "JOY_Z-<TAB>button<TAB>37", "JOY_BTN1<TAB>button<TAB>0", "JOY_BTN_POV1_U<TAB>button<TAB>44".
    //    JOYPRO_BATTLEFIELD_MAP=<tsv> points at another file (tests).
    internal sealed class BattlefieldJoystickMap
    {
        public const string FileSuffix = "JoystickMap.tsv";
        public const string OverrideVariable = "JOYPRO_BATTLEFIELD_MAP";
        private static readonly string[] AxisTokens = { "JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1", "JOY_SLIDER2" };

        private readonly Dictionary<string, BattlefieldCode> byName = new Dictionary<string, BattlefieldCode>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BattlefieldCode> byCode = new Dictionary<string, BattlefieldCode>(StringComparer.Ordinal);

        public bool Verified { get; private set; }
        public string Source { get; private set; }
        public int MaxButtons { get; private set; }

        // <engine>\TOOLS\Conversions\<GameId>JoystickMap.tsv
        public static string MapPath(BattlefieldIntegration game)
        {
            return Path.Combine(MainStructure.PROGPATH ?? string.Empty, "TOOLS", "Conversions", game.GameId + FileSuffix);
        }

        public static BattlefieldJoystickMap Load(BattlefieldIntegration game, List<string> warnings)
        {
            BattlefieldJoystickMap map = new BattlefieldJoystickMap();
            string file = Environment.GetEnvironmentVariable(OverrideVariable);
            if (string.IsNullOrWhiteSpace(file))
                file = MapPath(game);
            if (File.Exists(file) && map.LoadFile(file, game, warnings))
            {
                map.Verified = true;
                map.Source = file;
                return map;
            }
            map.byName.Clear();
            map.byCode.Clear();
            map.LoadProvisional();
            map.Source = "provisional table";
            return map;
        }

        private static string CodeKey(string property, int value)
        {
            return property + ":" + value.ToString(CultureInfo.InvariantCulture);
        }

        private void Put(string name, string property, int value)
        {
            name = name.Trim().ToUpperInvariant();
            int half = name.EndsWith("+", StringComparison.Ordinal) ? 1 : name.EndsWith("-", StringComparison.Ordinal) ? -1 : 0;
            BattlefieldCode code = new BattlefieldCode
            {
                Property = property,
                Value = value,
                Token = half == 0 ? name : name.Substring(0, name.Length - 1),
                Half = half
            };
            byName[name] = code;
            string key = CodeKey(property, value);
            if (!byCode.ContainsKey(key))
                byCode[key] = code;
            JoyInput input;
            if (half == 0 && GameInputTokens.TryParse(name, out input) && input.Kind == JoyInputKind.Button)
                MaxButtons = Math.Max(MaxButtons, input.Index + 1);
        }

        private void LoadProvisional()
        {
            Put("JOY_X", "axis", 9);
            Put("JOY_Y", "axis", 10);
            Put("JOY_X+", "axis", 11);
            Put("JOY_Y+", "axis", 12);
            Put("JOY_X-", "axis", 13);
            Put("JOY_Y-", "axis", 14);
            string[] analog = { "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ" };
            for (int i = 0; i < analog.Length; i++)
            {
                Put(analog[i] + "+", "button", 36 + 2 * i);
                Put(analog[i] + "-", "button", 37 + 2 * i);
            }
            Put("JOY_SLIDER1+", "button", 14);
            Put("JOY_SLIDER1-", "button", 15);
            for (int b = 0; b < 36; b++)
            {
                if (b == 14 || b == 15)
                    continue;   // shared with the slider halves; JoyPro writes no button there
                Put(GameInputTokens.ButtonToken(b), "button", b);
            }
            for (int h = 0; h < 4; h++)
            {
                string hat = "JOY_BTN_POV" + (h + 1).ToString(CultureInfo.InvariantCulture) + "_";
                Put(hat + "U", "button", 44 + 4 * h);
                Put(hat + "D", "button", 45 + 4 * h);
                Put(hat + "L", "button", 46 + 4 * h);
                Put(hat + "R", "button", 47 + 4 * h);
            }
        }

        private bool LoadFile(string file, BattlefieldIntegration game, List<string> warnings)
        {
            try
            {
                foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                        continue;
                    string[] parts = line.Split('\t');
                    int value;
                    if (parts.Length < 3 || !int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                        continue;
                    string property = parts[1].Trim().ToLowerInvariant();
                    if (property != "axis" && property != "button")
                        continue;
                    Put(parts[0], property, value);
                }
            }
            catch (Exception ex)
            {
                warnings.Add(game.DisplayName + " joystick table " + file + " could not be read: " + ex.Message);
                return false;
            }
            return byName.Count > 0;
        }

        // A bound joystick slot -> JoyPro's input (null when the code is not in the table).
        public BattlefieldCode Decode(BattlefieldSlot slot, int unboundAxis, int unboundButton)
        {
            BattlefieldCode code;
            if (slot.Axis >= 0 && slot.Axis != unboundAxis && byCode.TryGetValue(CodeKey("axis", slot.Axis), out code))
                return code;
            if (slot.Button >= 0 && slot.Button != unboundButton && byCode.TryGetValue(CodeKey("button", slot.Button), out code))
                return code;
            return null;
        }

        // The two halves of an axis (the game binds an axis as "+ half moves the action up, - half down").
        public bool TryHalves(JoyInput input, out BattlefieldCode positive, out BattlefieldCode negative, out string reason)
        {
            positive = null;
            negative = null;
            reason = string.Empty;
            if (input.Kind != JoyInputKind.Axis)
            {
                reason = "an axis action needs an axis.";
                return false;
            }
            string token = AxisTokens[input.Index];
            if (!byName.TryGetValue(token + "+", out positive) || !byName.TryGetValue(token + "-", out negative))
            {
                reason = "the game cannot read " + token + " (JoyPro's table has no code for it); use another axis (in virtual mode JoyPro's table decides the vJoy axis).";
                return false;
            }
            return true;
        }

        // The whole-axis code of an axis ("JOY_Y<TAB>axis<TAB>10" in the table), for games that store a bound axis whole.
        public bool TryWhole(JoyInput input, out BattlefieldCode code)
        {
            code = null;
            if (input.Kind != JoyInputKind.Axis || input.Index < 0 || input.Index >= AxisTokens.Length)
                return false;
            return byName.TryGetValue(AxisTokens[input.Index], out code) && code.Property == "axis" && code.Half == 0;
        }

        public bool TryButton(JoyInput input, out BattlefieldCode code, out string reason)
        {
            code = null;
            reason = string.Empty;
            if (input.Kind == JoyInputKind.Axis)
            {
                reason = "a button action needs a button or a hat direction.";
                return false;
            }
            if (input.Kind == JoyInputKind.Pov && input.Direction % 2 == 1)
            {
                reason = "the game knows only the four main hat directions.";
                return false;
            }
            string token = GameInputTokens.ToToken(input);
            if (!byName.TryGetValue(token, out code))
            {
                reason = input.Kind == JoyInputKind.Button
                    ? "the game reads only buttons 1-" + MaxButtons.ToString(CultureInfo.InvariantCulture) + " (" + token + " has no code); use a lower button."
                    : "'" + token + "' has no joystick code in JoyPro's table.";
                return false;
            }
            return true;
        }
    }
}
