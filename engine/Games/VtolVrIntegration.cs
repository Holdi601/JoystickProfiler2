using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // VTOL VR has no native HOTAS input (only rudder and toe brakes, see VtolVrNativeIntegration). Full HOTAS binds come
    // from the BYOJoystick mod (Steam Workshop 3276352374): one XML file per vehicle seat, <install>\SaveData\BYOJ_<seat>.xml,
    // devices by DirectInput instance GUID and DirectInput axis/button names (see docs/games/vtol-vr.md). A target exists
    // only in some seats (DB/VTOLVR/seats.tsv); an export writes each binding into every seat file that has its target.
    internal sealed class VtolVrIntegration : IGameIntegration, IGameOwnershipTracking, IGameCatalogRefreshFiles
    {
        public static readonly string[] Seats = { "AV42C", "FA26B", "F45A", "AH94Front", "AH94Rear", "T55Front", "T55Rear", "EF24GFront", "EF24GRear" };
        private const string WorkshopId = "3276352374";
        private const string ModifierTarget = "Modifier";

        public string GameId { get { return "VTOLVR"; } }
        public string DisplayName { get { return "VTOL VR (BYOJoystick mod)"; } }
        public IReadOnlyList<string> Aliases { get { return new[] { "vtolvr", "vtol", "byoj" }; } }
        public string DbFolder { get { return "VTOLVR"; } }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return true; } }
        public string Notes
        {
            get
            {
                return "Experimental. Needs the BYOJoystick mod (Steam Workshop, with the VTOL VR Mod Loader); JoyPro writes its files " +
                       "SaveData\\BYOJ_<seat>.xml in the VTOL VR folder, one per vehicle seat. Close VTOL VR before exporting. " +
                       "Native VTOL VR only binds rudder and toe brakes: see 'vtolvr-native'.";
            }
        }
        public IReadOnlyList<string> ProcessNames { get { return new[] { "VTOLVR" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        // Modifiers: one per bind (a joystick button for joystick binds, a key for keyboard binds).
        public GameFeature KeyboardBinds { get { return GameFeature.Yes; } }
        public GameFeature ModifierBinds { get { return GameFeature.Partial; } }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            List<string> paths = new List<string>();
            string own = GameInstalls.OverrideFolder(GameId);
            if (own != null)
                return new List<string> { Path.Combine(own, "SaveData") };
            foreach (string library in GamePathDiscovery.GetSteamLibraryRoots(warnings))
            {
                string install = Path.Combine(library, "steamapps", "common", "VTOL VR");
                if (Directory.Exists(install))
                    paths.Add(Path.Combine(install, "SaveData"));
            }
            return paths;
        }

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            if (!Directory.Exists(configPath))
            {
                result.Errors.Add("VTOL VR SaveData folder not found: " + configPath);
                return result;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string seat in Seats)
            {
                ByojFile file;
                string path = SeatPath(configPath, seat);
                if (!File.Exists(path) || !ByojFile.TryLoad(path, result.Errors, out file))
                    continue;
                foreach (ByojJoystickBinding binding in file.Joystick.Where(b => b.Target != ModifierTarget))
                {
                    string token = ToJoyProInput(binding);
                    if (token.Length == 0)
                    {
                        result.Warnings.Add(seat + ": " + binding.Target + " uses input '" + binding.Input + "' JoyPro cannot represent.");
                        continue;
                    }
                    GameDevice connected = devices == null ? null : devices.FindByInstanceGuid(binding.JoystickId);
                    string device = connected != null ? connected.JoyProName : GameDeviceDirectory.BuildJoyProName("BYOJ Device", binding.JoystickId);
                    List<string> reformers = new List<string>();
                    if (binding.RequiresModifier)
                    {
                        ByojJoystickBinding modifier = file.Joystick.FirstOrDefault(b => b.Target == ModifierTarget);
                        if (modifier != null)
                        {
                            GameDevice modifierDevice = devices == null ? null : devices.FindByInstanceGuid(modifier.JoystickId);
                            string modifierName = modifierDevice != null ? modifierDevice.JoyProName : GameDeviceDirectory.BuildJoyProName("BYOJ Device", modifier.JoystickId);
                            reformers.Add("BYOJ Modifier§" + modifierName + "§" + ToJoyProInput(modifier) + "§False");
                        }
                    }
                    string key = binding.Target + "|" + device + "|" + token + "|" + string.Join(",", reformers);
                    if (!seen.Add(key))
                        continue;
                    bool isAxis = token.StartsWith("JOY_", StringComparison.Ordinal) && !token.StartsWith("JOY_BTN", StringComparison.Ordinal);
                    // The mod's <Deadzone> is a dead zone only on slew/articulate; on the stick and throttle it is the
                    // multi-crew takeover threshold, elsewhere unused (byoj axis model).
                    AxisTuning tuning = null;
                    if (isAxis)
                    {
                        tuning = new AxisTuning { Inverted = binding.Invert };
                        string meaning = DeadzoneMeaning(binding.Target);
                        if (meaning.Length > 0 && binding.Deadzone != 0)
                        {
                            tuning.Native[meaning] = Math.Round(binding.Deadzone, 6);
                            if (meaning == "cut")
                                tuning.Native["slewAxis"] = 1;
                        }
                    }
                    result.Bindings.Add(new GameInputBinding
                    {
                        ActionId = binding.Target,
                        Device = device,
                        Input = token,
                        IsAxis = isAxis,
                        Inverted = binding.Invert,
                        Deadzone = 0.0,
                        Reformers = reformers,
                        Tuning = tuning
                    });
                }
                foreach (ByojKeyboardBinding binding in file.Keyboard)
                {
                    string key;
                    string modifier;
                    if (!UnityKeys.TryToJoyPro(binding.Key, out key))
                        continue;
                    List<string> reformers = new List<string>();
                    if (binding.ModifierKey != "None" && UnityKeys.TryToJoyPro(binding.ModifierKey, out modifier))
                        reformers.Add(modifier + "§Keyboard§" + modifier + "§False");
                    if (!seen.Add(binding.Target + "|Keyboard|" + key + "|" + string.Join(",", reformers)))
                        continue;
                    result.Bindings.Add(new GameInputBinding { ActionId = binding.Target, Device = "Keyboard", Input = key, IsAxis = false, Reformers = reformers });
                }
            }
            if (seen.Count == 0)
                result.Warnings.Add("No BYOJ_<seat>.xml files with binds in " + configPath + " (BYOJoystick writes them when you save in its window).");
            return result;
        }

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            string folder = request.ConfigPath;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? string.Empty))
            {
                result.Errors.Add("VTOL VR folder not found for " + folder + ".");
                return result;
            }
            Dictionary<string, HashSet<string>> seatsByTarget = VtolData.SeatsByTarget();
            HashSet<string> centered = VtolData.CenteredAxes();
            if (!ModInstalled(folder))
                result.Warnings.Add("BYOJoystick was not found in the Steam Workshop folder of VTOL VR; the files are written anyway and take effect once the mod is installed.");

            // Wanted bindings per seat; the devices JoyPro exports.
            Dictionary<string, List<ByojJoystickBinding>> wantedJoystick = Seats.ToDictionary(s => s, s => new List<ByojJoystickBinding>(), StringComparer.Ordinal);
            Dictionary<string, List<ByojKeyboardBinding>> wantedKeyboard = Seats.ToDictionary(s => s, s => new List<ByojKeyboardBinding>(), StringComparer.Ordinal);
            HashSet<Guid> exported = new HashSet<Guid>();
            // The profile's bind behind each wanted file entry (for the "Only add" report).
            Dictionary<object, GameInputBinding> sources = new Dictionary<object, GameInputBinding>();
            foreach (GameInputBinding binding in request.Bindings)
            {
                HashSet<string> seats;
                if (!seatsByTarget.TryGetValue(binding.ActionId, out seats))
                {
                    Skip(result, binding, "unknown BYOJoystick target.");
                    continue;
                }
                GameAction action = request.Catalog.Actions.FirstOrDefault(a => a.Id == binding.ActionId);
                bool targetIsAxis = action != null && action.IsAxis;
                if (binding.IsKeyboard)
                {
                    string reason;
                    ByojKeyboardBinding keyboard = ToKeyboard(binding, out reason);
                    if (keyboard == null)
                    {
                        Skip(result, binding, reason);
                        continue;
                    }
                    foreach (string seat in seats)
                        wantedKeyboard[seat].Add(keyboard);
                    sources[keyboard] = binding;
                    result.BindingsWritten++;
                    continue;
                }

                string skip;
                ByojJoystickBinding wanted = ToJoystick(binding, targetIsAxis, centered.Contains(binding.ActionId), out skip);
                if (wanted == null)
                {
                    Skip(result, binding, skip);
                    continue;
                }
                ByojJoystickBinding modifier = null;
                if (binding.Reformers != null && binding.Reformers.Count > 0)
                {
                    modifier = ModifierBinding(binding.Reformers[0], out skip);
                    if (modifier == null)
                    {
                        Skip(result, binding, skip);
                        continue;
                    }
                    wanted.RequiresModifier = true;
                }
                foreach (string seat in seats)
                {
                    wantedJoystick[seat].Add(wanted);
                    if (modifier != null && !wantedJoystick[seat].Any(w => w.Target == ModifierTarget && w.SameInput(modifier)))
                        wantedJoystick[seat].Add(modifier);
                }
                sources[wanted] = binding;
                exported.Add(wanted.JoystickId);
                if (modifier != null)
                    exported.Add(modifier.JoystickId);
                result.BindingsWritten++;
            }

            string ownershipPath = Path.Combine(folder, "JoyPro");
            Dictionary<string, HashSet<string>> nowOwned = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            int filesChanged = 0;
            foreach (string seat in Seats)
            {
                string path = SeatPath(folder, seat);
                ByojFile file;
                if (File.Exists(path))
                {
                    if (!ByojFile.TryLoad(path, result.Errors, out file))
                        continue;
                }
                else
                {
                    if (wantedJoystick[seat].Count == 0 && wantedKeyboard[seat].Count == 0)
                        continue;
                    file = ByojFile.Empty();
                }

                bool overwrite = request.Mode == GameExportMode.Clean || request.Mode == GameExportMode.MergeOverwrite;
                List<ByojJoystickBinding> wanted = wantedJoystick[seat];
                // The targets the profile binds in this seat (joystick and keyboard): "my profile wins" takes their other
                // inputs away.
                HashSet<string> profileTargets = new HashSet<string>(wanted.Where(w => w.Target != ModifierTarget).Select(w => w.Target)
                    .Concat(wantedKeyboard[seat].Select(k => k.Target)), StringComparer.Ordinal);
                bool profileModifier = wanted.Any(w => w.Target == ModifierTarget);
                List<ByojJoystickBinding> kept = new List<ByojJoystickBinding>();
                List<ByojJoystickBinding> dropped = new List<ByojJoystickBinding>();
                foreach (ByojJoystickBinding existing in file.Joystick)
                {
                    bool joyProDevice = exported.Contains(existing.JoystickId);
                    bool wantedHere = wanted.Any(w => w.Target == existing.Target && w.SameInput(existing));
                    bool drop;
                    if (existing.Target == ModifierTarget)
                    {
                        // The seat's modifier button is used by binds JoyPro may not touch: it only gives way to the
                        // profile's own modifier.
                        drop = profileModifier && !wantedHere && request.Mode != GameExportMode.Add && OwnsInput(request.Devices, existing);
                    }
                    else
                    {
                        drop = OwnsInput(request.Devices, existing) &&
                               ((request.Mode == GameExportMode.Clean && joyProDevice) ||
                                (request.NukeUnusedDevices && !joyProDevice) ||
                                (overwrite && profileTargets.Contains(existing.Target) && !wantedHere));
                    }
                    // Merge and "my profile wins": another target on an input (with the same modifier state) the profile
                    // binds is a conflict and goes.
                    if (!drop && !wantedHere && request.Mode != GameExportMode.Add &&
                        wanted.Any(w => w.Target != existing.Target && w.SameInput(existing) && w.RequiresModifier == existing.RequiresModifier))
                        drop = true;
                    if (!drop)
                        kept.Add(existing);
                    else
                        dropped.Add(existing);
                }
                foreach (ByojJoystickBinding want in wanted)
                {
                    if (request.Mode == GameExportMode.Add)
                    {
                        // "Only add" never changes or removes a bind: the target already on its input is nothing to do,
                        // another target there keeps the input and the profile's bind is reported (no multi-bind).
                        if (kept.Any(k => k.Target == want.Target && k.SameInput(want) && k.RequiresModifier == want.RequiresModifier))
                            continue;
                        ByojJoystickBinding taken = kept.FirstOrDefault(k => k.Target != want.Target && k.SameInput(want) && k.RequiresModifier == want.RequiresModifier);
                        GameInputBinding source;
                        if (taken != null && sources.TryGetValue(want, out source))
                        {
                            result.BindingsSkipped++;
                            result.SkippedTakenInputs.Add(SkippedMultiBind(source, seat, taken.Target));
                            continue;
                        }
                        if (taken != null)
                            continue;
                    }
                    ByojJoystickBinding same = kept.FirstOrDefault(k => k.Target == want.Target && k.SameInput(want));
                    if (same != null)
                    {
                        same.Invert = want.Invert;
                        // Targets that ignore <Deadzone> keep the file's value (nothing to manage there).
                        if (DeadzoneMeaning(want.Target).Length > 0 || want.Deadzone != 0)
                            same.Deadzone = want.Deadzone;
                        same.RequiresModifier = want.RequiresModifier;
                    }
                    else
                    {
                        // A bind JoyPro replaces on a target that ignores <Deadzone>: the file's value stays.
                        ByojJoystickBinding previous = dropped.FirstOrDefault(d => d.Target == want.Target && d.SameInput(want));
                        if (previous != null && want.Deadzone == 0 && previous.Deadzone != 0 && DeadzoneMeaning(want.Target).Length == 0)
                            kept.Add(new ByojJoystickBinding { Target = want.Target, JoystickId = want.JoystickId, Input = want.Input, RequiresModifier = want.RequiresModifier, Invert = want.Invert, Deadzone = previous.Deadzone, POVDirection = want.POVDirection });
                        else
                            kept.Add(want);
                    }
                }

                // Keyboard: the same rules on key + modifier key. "My profile wins" takes keys of the profile's targets
                // away only when the export includes the keyboard.
                List<ByojKeyboardBinding> keyboard = file.Keyboard.ToList();
                List<ByojKeyboardBinding> wantedKeys = wantedKeyboard[seat];
                if (request.Mode != GameExportMode.Add)
                {
                    keyboard.RemoveAll(k => !wantedKeys.Any(w => w.KeyId == k.KeyId) &&
                        (wantedKeys.Any(w => w.Target != k.Target && w.Key == k.Key && w.ModifierKey == k.ModifierKey) ||
                         (overwrite && request.IncludeKeyboard && profileTargets.Contains(k.Target))));
                }
                foreach (ByojKeyboardBinding want in wantedKeys)
                {
                    if (keyboard.Any(k => k.KeyId == want.KeyId))
                        continue;
                    ByojKeyboardBinding taken = keyboard.FirstOrDefault(k => k.Key == want.Key && k.ModifierKey == want.ModifierKey);
                    GameInputBinding source;
                    if (taken != null && sources.TryGetValue(want, out source))
                    {
                        result.BindingsSkipped++;
                        result.SkippedTakenInputs.Add(SkippedMultiBind(source, seat, taken.Target));
                        continue;
                    }
                    if (taken == null)
                        keyboard.Add(want);
                }
                HashSet<string> seatOwned = new HashSet<string>(StringComparer.Ordinal);
                foreach (ByojJoystickBinding want in wanted.Where(w => kept.Any(k => k.Key == w.Key)))
                    seatOwned.Add(want.Key);
                foreach (ByojKeyboardBinding want in wantedKeyboard[seat].Where(w => keyboard.Any(k => k.KeyId == w.KeyId)))
                    seatOwned.Add(want.KeyId);
                nowOwned[seat] = seatOwned;

                string before = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
                file.Joystick = kept;
                file.Keyboard = keyboard;
                string after = file.Render();
                if (string.Equals(before, after, StringComparison.Ordinal))
                    continue;
                filesChanged++;
                if (request.DryRun)
                {
                    result.Steps.Add("Dry run: " + Path.GetFileName(path) + " would change.");
                    continue;
                }
                Directory.CreateDirectory(folder);
                GameExportSafety.BackupBeforeOverwrite(path, result.Warnings);
                File.WriteAllText(path, after, new UTF8Encoding(false));
                result.FilesWritten.Add(path);
            }

            if (FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, VtolVrGameSettings.Game, null).Count > 0)
                result.Steps.Add("VTOL VR's control settings (gameSettings.cfg, Roaming) are written by 'engine vtolvr-native export', not by the BYOJoystick export.");
            if (request.DryRun)
                return result;
            Directory.CreateDirectory(folder);
            GameOwnershipStore.Save(ownershipPath, GameId, nowOwned);
            if (filesChanged == 0)
                result.Steps.Add("The BYOJoystick files already match.");
            return result;
        }

        // Binds JoyPro may release: on a connected device, and an input the import reads (others are not in the profile).
        private static bool OwnsInput(GameDeviceDirectory devices, ByojJoystickBinding binding)
        {
            return devices != null && devices.FindByInstanceGuid(binding.JoystickId) != null &&
                   (binding.Target == ModifierTarget || ToJoyProInput(binding).Length > 0);
        }

        private ExportSkippedBind SkippedMultiBind(GameInputBinding binding, string seat, string gameAction)
        {
            return new ExportSkippedBind
            {
                Game = GameId,
                Aircraft = seat ?? string.Empty,
                Device = binding.Device ?? string.Empty,
                Input = binding.Input ?? string.Empty,
                ModifiersText = binding.Reformers == null ? string.Empty : string.Join(" + ", binding.Reformers.Select(r => { string[] p = (r ?? string.Empty).Split('§'); return p.Length >= 3 ? p[1] + " " + p[2] : r; })),
                Relation = binding.RelationName ?? string.Empty,
                Action = binding.ActionId ?? string.Empty,
                GameAction = gameAction ?? string.Empty
            };
        }

        public void RecordImport(string configPath, List<GameInputBinding> applied, GameDeviceDirectory devices, GameIoResult result)
        {
            Dictionary<string, HashSet<string>> seatsByTarget = VtolData.SeatsByTarget();
            HashSet<string> centered = VtolData.CenteredAxes();
            string ownershipPath = Path.Combine(configPath, "JoyPro");
            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(ownershipPath, result.Warnings);
            foreach (GameInputBinding binding in applied)
            {
                HashSet<string> seats;
                if (!seatsByTarget.TryGetValue(binding.ActionId, out seats))
                    continue;
                string reason;
                string key = binding.IsKeyboard
                    ? (ToKeyboard(binding, out reason) ?? new ByojKeyboardBinding()).KeyId
                    : (ToJoystick(binding, !binding.Input.StartsWith("JOY_BTN", StringComparison.Ordinal), centered.Contains(binding.ActionId), out reason) ?? new ByojJoystickBinding()).Key;
                foreach (string seat in seats)
                    GameOwnershipStore.Add(owned, seat, key);
            }
            GameOwnershipStore.Save(ownershipPath, GameId, owned);
            result.FilesWritten.Add(ownershipPath + GameOwnershipStore.Suffix);
        }

        // The BYOJoystick targets are compiled into the mod's BYOJoystick.dll (string literals in its vehicle managers); the
        // game's own files do not have them. The readable source is the user's own BYOJ_<seat>.xml files: the mod drops
        // targets a vehicle does not have when it loads a file and saves only the rest, so every target found there exists
        // in that seat. New targets (title = target, category Misc, axis when bound to axes only) are added to the catalog,
        // missing seats to seats.tsv; known targets keep their title and category.
        // Sources, read only: (1) the installed mod itself (VtolVrMod: BYOJoystick.dll of the Steam Workshop item, or
        // --path with that DLL or its folder), which has the full list with seats and input types; (2) the user's own
        // BYOJ_<seat>.xml files. A release ships no list: without the mod installed only (2) is there.
        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            pendingSeats = null;
            GameCatalog catalog = new GameCatalog { GameId = GameId };
            foreach (GameAction action in current == null ? new List<GameAction>() : current.Actions)
                catalog.Add(action);
            pendingSeats = new List<KeyValuePair<string, string[]>>();
            Dictionary<string, HashSet<string>> knownSeats = VtolData.SeatsByTarget();

            bool fromMod = AddTargetsFromMod(sourcePath, catalog, knownSeats, result);
            string folder = RefreshFolder(sourcePath, result.Warnings);
            List<string> files = folder == null ? new List<string>() : Directory.GetFiles(folder, "BYOJ_*.xml")
                .Where(f => string.Equals(Path.GetExtension(f), ".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0)
            {
                if (fromMod)
                {
                    catalog.SourcePath = sourcePath ?? string.Empty;
                    return catalog;
                }
                result.Errors.Add(folder == null
                    ? "Neither the BYOJoystick mod nor a VTOL VR SaveData folder was found. Install the mod (GUI: VTOL VR card > Install BYOJoystick, or Steam Workshop item " + VtolVrMod.WorkshopItem + "), or pass --path with the mod's BYOJoystick.dll, the SaveData folder (BYOJ_<seat>.xml) or the VTOL VR folder."
                    : "The BYOJoystick mod is not installed and there is no BYOJ_<seat>.xml in " + folder + ". Install the mod (GUI: VTOL VR card > Install BYOJoystick, or Steam Workshop item " + VtolVrMod.WorkshopItem + ") and refresh again, or bind and save once in BYOJoystick's window.");
                return null;
            }

            List<string> order = new List<string>();
            Dictionary<string, HashSet<string>> foundSeats = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            Dictionary<string, bool[]> inputs = new Dictionary<string, bool[]>(StringComparer.Ordinal);
            int read = 0;
            foreach (string file in files)
            {
                string seat = Path.GetFileNameWithoutExtension(file).Substring("BYOJ_".Length);
                if (!Seats.Contains(seat, StringComparer.Ordinal))
                {
                    result.Warnings.Add(Path.GetFileName(file) + " belongs to a vehicle seat JoyPro does not know yet; skipped.");
                    continue;
                }
                ByojFile byoj;
                if (!ByojFile.TryLoad(file, result.Warnings, out byoj))
                    continue;
                read++;
                IEnumerable<KeyValuePair<string, int>> targets = byoj.Joystick.Select(b => new KeyValuePair<string, int>(b.Target, Array.IndexOf(AxisInputs, b.Input) >= 0 ? 1 : 0))
                    .Concat(byoj.Keyboard.Select(b => new KeyValuePair<string, int>(b.Target, 2)));
                foreach (KeyValuePair<string, int> target in targets.Where(t => t.Key.Length > 0))
                {
                    if (!foundSeats.ContainsKey(target.Key))
                    {
                        order.Add(target.Key);
                        foundSeats[target.Key] = new HashSet<string>(StringComparer.Ordinal);
                        inputs[target.Key] = new bool[3];
                    }
                    foundSeats[target.Key].Add(seat);
                    inputs[target.Key][target.Value] = true;
                }
            }

            catalog.SourcePath = folder;
            // Seats the mod already added for a target count as known for the files' pass.
            foreach (KeyValuePair<string, string[]> row in pendingSeats)
                knownSeats[row.Key] = new HashSet<string>(row.Value[0].Split(','), StringComparer.Ordinal);
            int added = 0;
            int seatsAdded = 0;
            foreach (string target in order)
            {
                // Axis only when the target is bound to axes and nothing else (the mod fires a button target from a button
                // or hat, an axis target only from an axis).
                bool axis = inputs[target][1] && !inputs[target][0] && !inputs[target][2];
                GameAction known = catalog.Find(target);
                if (known == null)
                {
                    catalog.Add(new GameAction { Id = target, Title = target, IsAxis = axis, Category = "Misc" });
                    added++;
                }
                HashSet<string> seats;
                knownSeats.TryGetValue(target, out seats);
                List<string> missing = Seats.Where(s => foundSeats[target].Contains(s) && (seats == null || !seats.Contains(s))).ToList();
                if (missing.Count == 0)
                    continue;
                seatsAdded += missing.Count;
                bool isAxis = known != null ? known.IsAxis : axis;
                pendingSeats.Add(new KeyValuePair<string, string[]>(target, new[] { string.Join(",", Seats.Where(s => foundSeats[target].Contains(s) || (seats != null && seats.Contains(s)))), isAxis ? "axis" : "button" }));
            }
            result.Steps.Add("Read " + read.ToString(CultureInfo.InvariantCulture) + " BYOJ_<seat>.xml file(s) in " + folder + ": " +
                             added.ToString(CultureInfo.InvariantCulture) + " new target(s), " + seatsAdded.ToString(CultureInfo.InvariantCulture) + " new seat membership(s).");
            result.Steps.Add("Only targets bound in these files can be found (the full list is compiled into the mod); a target is added with category Misc.");
            return catalog;
        }

        // The installed mod's targets (VtolVrMod.ReadTargets) into the catalog and seats.tsv: new targets with the mod's
        // category and input type, known ones keep title and category and get the seats they lack. False when no mod DLL
        // was found or it held no target.
        private bool AddTargetsFromMod(string sourcePath, GameCatalog catalog, Dictionary<string, HashSet<string>> knownSeats, GameIoResult result)
        {
            string dll = null;
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                string path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourcePath.Trim().Trim('"')));
                if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
                    dll = path;
                else if (Directory.Exists(path))
                    dll = Directory.GetFiles(path, "BYOJoystick.dll", SearchOption.TopDirectoryOnly).FirstOrDefault();
            }
            else
            {
                VtolVrMod.Status status = VtolVrMod.Find(result.Warnings);
                if (status.DllPath.Length > 0)
                    dll = status.DllPath;
                else if (status.ModInstalled)
                    result.Warnings.AddRange(status.Notes);
            }
            if (dll == null)
                return false;
            List<string> seats;
            List<VtolVrMod.Target> targets = VtolVrMod.ReadTargets(dll, result.Warnings, out seats);
            if (targets == null || targets.Count == 0)
            {
                result.Warnings.Add("No BYOJoystick targets could be read from " + dll + " (a newer mod version may build them differently); the BYOJ_<seat>.xml files are read instead.");
                return false;
            }
            int added = 0;
            int seatsAdded = 0;
            List<string> unknownSeats = seats.Where(s => !Seats.Contains(s, StringComparer.Ordinal)).ToList();
            foreach (VtolVrMod.Target target in targets)
            {
                GameAction known = catalog.Find(target.Name);
                if (known == null)
                {
                    catalog.Add(new GameAction { Id = target.Name, Title = target.Name, IsAxis = target.Kind != "button", Category = target.Category });
                    added++;
                }
                HashSet<string> had;
                knownSeats.TryGetValue(target.Name, out had);
                List<string> all = Seats.Where(s => target.Seats.Contains(s) || (had != null && had.Contains(s))).ToList();
                if (all.Count == 0 || (had != null && all.All(had.Contains)))
                    continue;
                seatsAdded += all.Count(s => had == null || !had.Contains(s));
                pendingSeats.Add(new KeyValuePair<string, string[]>(target.Name, new[] { string.Join(",", all), target.Kind }));
            }
            result.Steps.Add("Read the installed BYOJoystick mod (" + dll + "): " + targets.Count.ToString(CultureInfo.InvariantCulture) + " targets over " +
                             seats.Count.ToString(CultureInfo.InvariantCulture) + " vehicle seats; " + added.ToString(CultureInfo.InvariantCulture) + " new target(s), " +
                             seatsAdded.ToString(CultureInfo.InvariantCulture) + " new seat membership(s).");
            if (unknownSeats.Count > 0)
                result.Warnings.Add("The mod has vehicle seats JoyPro does not write yet (" + string.Join(", ", unknownSeats) + "); their targets are listed, export skips those seats.");
            return true;
        }

        private static readonly string[] AxisInputs = { "X", "Y", "Z", "RX", "RY", "RZ", "S1", "S2" };
        private List<KeyValuePair<string, string[]>> pendingSeats;

        // seats.tsv: new targets get a row, known targets the seats the files showed (in the order of Seats).
        public List<string> SaveRefreshFiles(List<string> warnings)
        {
            List<string> written = new List<string>();
            if (pendingSeats == null || pendingSeats.Count == 0)
                return written;
            string path = GameDataPaths.WritableDbFile("VTOLVR", "seats.tsv");
            List<string> lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToList() : new List<string> { "# id\tseats\tinput type" };
            foreach (KeyValuePair<string, string[]> row in pendingSeats)
            {
                int index = lines.FindIndex(l => l.Length > 0 && l[0] != '#' && l.Split('\t')[0] == row.Key);
                if (index < 0)
                {
                    lines.Add(row.Key + "\t" + row.Value[0] + "\t" + row.Value[1]);
                    continue;
                }
                string[] parts = lines[index].Split('\t');
                parts[1] = row.Value[0];
                lines[index] = string.Join("\t", parts);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
            File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
            pendingSeats = null;
            written.Add(path);
            return written;
        }

        // --path: the SaveData folder, a BYOJ file in it or the VTOL VR folder; else the discovered SaveData folder.
        private string RefreshFolder(string sourcePath, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                return DiscoverConfigPaths(warnings).FirstOrDefault(Directory.Exists);
            string path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourcePath.Trim().Trim('"')));
            if (File.Exists(path))
                return Path.GetDirectoryName(path);
            if (Directory.Exists(Path.Combine(path, "SaveData")))
                return Path.Combine(path, "SaveData");
            return Directory.Exists(path) ? path : null;
        }

        private static void Skip(GameIoResult result, GameInputBinding binding, string reason)
        {
            result.BindingsSkipped++;
            result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
        }

        private static string SeatPath(string folder, string seat)
        {
            return Path.Combine(folder, "BYOJ_" + seat + ".xml");
        }

        // <library>\steamapps\common\VTOL VR\SaveData -> <library>\steamapps\workshop\content\667970\3276352374
        private static bool ModInstalled(string saveData)
        {
            try
            {
                string install = Path.GetDirectoryName(Path.GetFullPath(saveData).TrimEnd('\\', '/'));
                string steamapps = Path.GetDirectoryName(Path.GetDirectoryName(install));
                return Directory.Exists(Path.Combine(steamapps, "workshop", "content", "667970", WorkshopId));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static ByojJoystickBinding ToJoystick(GameInputBinding binding, bool targetIsAxis, bool centered, out string reason)
        {
            reason = string.Empty;
            string name;
            Guid guid;
            if (!GameDeviceDirectory.TryParseJoyProName(binding.Device, out name, out guid))
            {
                reason = "device '" + binding.Device + "' has no DirectInput GUID.";
                return null;
            }
            JoyInput input;
            if (!GameInputTokens.TryParse(binding.Input, out input))
            {
                reason = "unsupported input '" + binding.Input + "'.";
                return null;
            }
            ByojJoystickBinding result = new ByojJoystickBinding { Target = binding.ActionId, JoystickId = guid, POVDirection = "None" };
            switch (input.Kind)
            {
                case JoyInputKind.Axis:
                    if (!targetIsAxis)
                    {
                        reason = "an axis on a button target never fires in BYOJoystick.";
                        return null;
                    }
                    string[] axes = { "X", "Y", "Z", "RX", "RY", "RZ", "S1", "S2" };
                    result.Input = axes[input.Index];
                    result.Invert = binding.Inverted;
                    result.Deadzone = DeadzoneFor(binding, centered);
                    return result;
                case JoyInputKind.Button:
                    if (targetIsAxis)
                    {
                        reason = "the target is an axis; use an axis or the Increase/Decrease buttons.";
                        return null;
                    }
                    if (input.Index >= 128)
                    {
                        reason = "BYOJoystick reads 128 buttons.";
                        return null;
                    }
                    result.Input = "B" + (input.Index + 1).ToString(CultureInfo.InvariantCulture);
                    return result;
                case JoyInputKind.Pov:
                    if (targetIsAxis || input.Index > 3 || input.Direction % 2 != 0)
                    {
                        reason = targetIsAxis ? "the target is an axis." : "BYOJoystick reads the four main directions of hats 1-4 (no diagonals).";
                        return null;
                    }
                    string[] directions = { "Up", null, "Right", null, "Down", null, "Left", null };
                    result.Input = "PoV" + (input.Index + 1).ToString(CultureInfo.InvariantCulture);
                    result.POVDirection = directions[input.Direction];
                    return result;
            }
            reason = "unsupported input '" + binding.Input + "'.";
            return null;
        }

        // What BYOJoystick does with <Deadzone> per target (master source, docs/games/vtol-vr.md): "takeover" = the
        // multi-crew takeover threshold (stick: deflection from the centre, throttle levers: change), "cut" = a dead zone
        // without rescale, "" = unused.
        private static readonly HashSet<string> TakeoverTargets = new HashSet<string>(StringComparer.Ordinal)
        {
            "Joystick Pitch", "Joystick Roll", "Joystick Yaw", "Throttle", "Power", "Collective"
        };

        private static readonly HashSet<string> CutTargets = new HashSet<string>(StringComparer.Ordinal)
        {
            "SOI Slew X", "SOI Slew Y", "Articulate Tilt"
        };

        internal static string DeadzoneMeaning(string target)
        {
            if (TakeoverTargets.Contains(target ?? string.Empty))
                return "takeover";
            return CutTargets.Contains(target ?? string.Empty) ? "cut" : string.Empty;
        }

        // The <Deadzone> to write: the byoj tuning's value for what the target uses it for; 0 where the mod ignores it.
        // A binding without a resolved tuning keeps the older rule (the relation dead zone on centred targets).
        private static double DeadzoneFor(GameInputBinding binding, bool centered)
        {
            string meaning = DeadzoneMeaning(binding.ActionId);
            if (binding.Tuning == null)
                return centered ? Math.Max(0.0, Math.Min(1.0, binding.Deadzone)) : 0.0;
            if (meaning.Length == 0)
                return 0.0;
            return Math.Round(Math.Max(0.0, Math.Min(1.0, binding.Tuning.NativeOr(meaning, binding.Tuning.Deadzone))), 4);
        }

        // A JoyPro modifier on a joystick button becomes BYOJoystick's "Modifier" target in the same seat file.
        private static ByojJoystickBinding ModifierBinding(string reformer, out string reason)
        {
            reason = string.Empty;
            string[] parts = (reformer ?? string.Empty).Split('§');
            if (parts.Length < 3 || string.Equals(parts[1], "Keyboard", StringComparison.OrdinalIgnoreCase))
            {
                reason = "BYOJoystick modifiers are joystick buttons; a keyboard modifier on a joystick bind is not possible.";
                return null;
            }
            GameInputBinding modifier = new GameInputBinding { ActionId = ModifierTarget, Device = parts[1], Input = parts[2] };
            ByojJoystickBinding result = ToJoystick(modifier, false, false, out reason);
            if (result == null)
                reason = "modifier: " + reason;
            return result;
        }

        private static ByojKeyboardBinding ToKeyboard(GameInputBinding binding, out string reason)
        {
            reason = string.Empty;
            string key;
            if (!UnityKeys.TryFromJoyPro(binding.Input, out key))
            {
                reason = "key '" + binding.Input + "' has no Unity KeyCode.";
                return null;
            }
            string modifier = "None";
            if (binding.Reformers != null && binding.Reformers.Count > 0)
            {
                string[] parts = binding.Reformers[0].Split('§');
                if (binding.Reformers.Count > 1 || parts.Length < 3 || !string.Equals(parts[1], "Keyboard", StringComparison.OrdinalIgnoreCase) ||
                    !UnityKeys.TryFromJoyPro(parts[2], out modifier))
                {
                    reason = "BYOJoystick keyboard binds take one keyboard modifier.";
                    return null;
                }
            }
            return new ByojKeyboardBinding { Target = binding.ActionId, Key = key, ModifierKey = modifier };
        }

        private static string ToJoyProInput(ByojJoystickBinding binding)
        {
            string[] axes = { "X", "Y", "Z", "RX", "RY", "RZ", "S1", "S2" };
            int axis = Array.IndexOf(axes, binding.Input);
            if (axis >= 0)
                return GameInputTokens.AxisToken(axis);
            int number;
            if (binding.Input.StartsWith("B", StringComparison.Ordinal) && int.TryParse(binding.Input.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 1 && number <= 128)
                return GameInputTokens.ButtonToken(number - 1);
            if (binding.Input.StartsWith("PoV", StringComparison.Ordinal) && int.TryParse(binding.Input.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 1 && number <= 4)
            {
                int direction = Array.IndexOf(new[] { "Up", null, "Right", null, "Down", null, "Left", null }, binding.POVDirection);
                if (direction >= 0)
                    return GameInputTokens.PovToken(number - 1, direction);
            }
            return string.Empty;
        }

        private static class VtolData
        {
            public static Dictionary<string, HashSet<string>> SeatsByTarget()
            {
                Dictionary<string, HashSet<string>> seats = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (string[] parts in Rows("seats.tsv"))
                {
                    if (parts.Length >= 2)
                        seats[parts[0]] = new HashSet<string>(parts[1].Split(',').Select(p => p.Trim()).Where(p => p.Length > 0), StringComparer.Ordinal);
                }
                return seats;
            }

            // Axes that centre (stick pitch/roll/yaw, slew, articulate tilt): where a binding without a resolved tuning
            // (callers outside the profile bridge) gets the relation dead zone, as before the byoj model.
            public static HashSet<string> CenteredAxes()
            {
                return new HashSet<string>(Rows("seats.tsv").Where(p => p.Length >= 3 && p[2] == "axis_centered").Select(p => p[0]), StringComparer.Ordinal);
            }

            public static string FilePath(string fileName)
            {
                return GameDataPaths.DbFile("VTOLVR", fileName);
            }

            private static IEnumerable<string[]> Rows(string fileName)
            {
                string path = FilePath(fileName);
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

    internal sealed class ByojJoystickBinding
    {
        public string Target = string.Empty;
        public Guid JoystickId;
        public string Input = string.Empty;
        public bool RequiresModifier;
        public bool Invert;
        public double Deadzone;
        public string POVDirection = "None";

        public string Key
        {
            get { return "joy|" + Target + "|" + JoystickId.ToString("D") + "|" + Input + "|" + POVDirection; }
        }

        public bool SameInput(ByojJoystickBinding other)
        {
            return JoystickId == other.JoystickId && Input == other.Input && POVDirection == other.POVDirection;
        }
    }

    internal sealed class ByojKeyboardBinding
    {
        public string Target = string.Empty;
        public string Key = string.Empty;
        public string ModifierKey = "None";

        public string KeyId
        {
            get { return "key|" + Target + "|" + Key + "|" + ModifierKey; }
        }
    }

    // BYOJ_<seat>.xml as BYOJoystick's XmlSerializer writes it (UTF-8 without BOM, two-space indent).
    internal sealed class ByojFile
    {
        public List<ByojKeyboardBinding> Keyboard = new List<ByojKeyboardBinding>();
        public List<ByojJoystickBinding> Joystick = new List<ByojJoystickBinding>();

        public static ByojFile Empty()
        {
            return new ByojFile();
        }

        public static bool TryLoad(string path, List<string> errors, out ByojFile file)
        {
            file = null;
            try
            {
                XDocument document = XDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                XElement root = document.Root;
                if (root == null || root.Name.LocalName != "Bindings")
                {
                    errors.Add(path + " is not a BYOJoystick bindings file.");
                    return false;
                }
                file = new ByojFile();
                foreach (XElement element in root.Elements("KeyboardBindings").Elements("KeyboardBinding"))
                {
                    file.Keyboard.Add(new ByojKeyboardBinding
                    {
                        Target = Value(element, "Target"),
                        Key = Value(element, "Key"),
                        ModifierKey = Value(element, "ModifierKey").Length > 0 ? Value(element, "ModifierKey") : "None"
                    });
                }
                foreach (XElement element in root.Elements("JoystickBindings").Elements("JoystickBinding"))
                {
                    Guid id;
                    Guid.TryParse(Value(element, "JoystickId"), out id);
                    double deadzone;
                    double.TryParse(Value(element, "Deadzone"), NumberStyles.Float, CultureInfo.InvariantCulture, out deadzone);
                    file.Joystick.Add(new ByojJoystickBinding
                    {
                        Target = Value(element, "Target"),
                        JoystickId = id,
                        Input = Value(element, "Input"),
                        RequiresModifier = Value(element, "RequiresModifier") == "true",
                        Invert = Value(element, "Invert") == "true",
                        Deadzone = deadzone,
                        POVDirection = Value(element, "POVDirection").Length > 0 ? Value(element, "POVDirection") : "None"
                    });
                }
                return true;
            }
            catch (Exception ex)
            {
                errors.Add("Could not read " + path + ": " + ex.Message);
                return false;
            }
        }

        private static string Value(XElement parent, string name)
        {
            XElement element = parent.Element(name);
            return element == null ? string.Empty : element.Value.Trim();
        }

        public string Render()
        {
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            XNamespace xsd = "http://www.w3.org/2001/XMLSchema";
            XElement root = new XElement("Bindings",
                new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xsd", xsd.NamespaceName));
            root.Add(new XElement("KeyboardBindings", Keyboard.Select(k => new XElement("KeyboardBinding",
                new XElement("Target", k.Target), new XElement("Key", k.Key), new XElement("ModifierKey", k.ModifierKey)))));
            root.Add(new XElement("JoystickBindings", Joystick.Select(j => new XElement("JoystickBinding",
                new XElement("Target", j.Target),
                new XElement("JoystickId", j.JoystickId.ToString("D")),
                new XElement("Input", j.Input),
                new XElement("RequiresModifier", j.RequiresModifier ? "true" : "false"),
                new XElement("Invert", j.Invert ? "true" : "false"),
                new XElement("Deadzone", j.Deadzone.ToString("R", CultureInfo.InvariantCulture)),
                new XElement("POVDirection", j.POVDirection)))));

            StringBuilder sb = new StringBuilder();
            XmlWriterSettings settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = "\r\n", Encoding = new UTF8Encoding(false) };
            using (StringWriterUtf8 writer = new StringWriterUtf8(sb))
            using (XmlWriter xml = XmlWriter.Create(writer, settings))
                new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(xml);
            return sb.ToString();
        }

        private sealed class StringWriterUtf8 : StringWriter
        {
            public StringWriterUtf8(StringBuilder sb) : base(sb, CultureInfo.InvariantCulture)
            {
            }

            public override Encoding Encoding
            {
                get { return new UTF8Encoding(false); }
            }
        }
    }

    // JoyPro (DirectInput) key names <-> Unity KeyCode names used by BYOJoystick.
    internal static class UnityKeys
    {
        private static readonly Dictionary<string, string> ToUnity = Build();
        private static readonly Dictionary<string, string> FromUnity = ToUnity.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);

        private static Dictionary<string, string> Build()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (char c = 'A'; c <= 'Z'; c++)
                map[c.ToString()] = c.ToString();
            for (int i = 0; i <= 9; i++)
            {
                string digit = i.ToString(CultureInfo.InvariantCulture);
                map["D" + digit] = "Alpha" + digit;
                map["NumberPad" + digit] = "Keypad" + digit;
            }
            for (int i = 1; i <= 15; i++)
                map["F" + i.ToString(CultureInfo.InvariantCulture)] = "F" + i.ToString(CultureInfo.InvariantCulture);
            string[,] pairs =
            {
                { "Space", "Space" }, { "Return", "Return" }, { "Escape", "Escape" }, { "Tab", "Tab" }, { "Back", "Backspace" },
                { "LeftShift", "LeftShift" }, { "RightShift", "RightShift" }, { "LeftControl", "LeftControl" }, { "RightControl", "RightControl" },
                { "LeftAlt", "LeftAlt" }, { "RightAlt", "RightAlt" }, { "UpArrow", "UpArrow" }, { "DownArrow", "DownArrow" },
                { "LeftArrow", "LeftArrow" }, { "RightArrow", "RightArrow" }, { "Insert", "Insert" }, { "Delete", "Delete" },
                { "Home", "Home" }, { "End", "End" }, { "PageUp", "PageUp" }, { "PageDown", "PageDown" }, { "Minus", "Minus" },
                { "Equals", "Equals" }, { "LeftBracket", "LeftBracket" }, { "RightBracket", "RightBracket" }, { "Semicolon", "Semicolon" },
                { "Apostrophe", "Quote" }, { "Grave", "BackQuote" }, { "Comma", "Comma" }, { "Period", "Period" }, { "Slash", "Slash" },
                { "Backslash", "Backslash" }, { "NumberPadPlus", "KeypadPlus" }, { "NumberPadMinus", "KeypadMinus" },
                { "NumberPadStar", "KeypadMultiply" }, { "NumberPadSlash", "KeypadDivide" }, { "NumberPadEnter", "KeypadEnter" },
                { "NumberPadPeriod", "KeypadPeriod" }, { "CapsLock", "CapsLock" }, { "ScrollLock", "ScrollLock" }, { "Pause", "Pause" }
            };
            for (int i = 0; i < pairs.GetLength(0); i++)
                map[pairs[i, 0]] = pairs[i, 1];
            return map;
        }

        public static bool TryFromJoyPro(string joyProKey, out string unityKey)
        {
            return ToUnity.TryGetValue((joyProKey ?? string.Empty).Trim(), out unityKey);
        }

        public static bool TryToJoyPro(string unityKey, out string joyProKey)
        {
            return FromUnity.TryGetValue((unityKey ?? string.Empty).Trim(), out joyProKey);
        }
    }
}
