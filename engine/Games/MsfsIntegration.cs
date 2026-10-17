using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // Microsoft Flight Simulator 2020 and 2024 input profiles, one game per sim (each with its own installation,
    // export target, catalog and aircraft list): one XML file per device and profile, identified by the DirectInput
    // instance GUID. The config path is a file or a folder (MSFS 2020: the Steam "remote" folder; MSFS 2024: JoyPro's
    // preset package in Community2024, or a folder of files exported via Controls > Export). Both file formats are
    // read by either game, so a path of the other sim still works. Only actions of the flight contexts are changed;
    // JoyPro's ownership data and backups live under %LOCALAPPDATA%\JoyPro, never in the synced folder.
    // See docs/games/msfs.md.
    internal sealed class MsfsIntegration : IGameIntegration, IGameImportReading, IGameOwnershipTracking, IGameProfileSelection, IGameAircraftScope, IGameCatalogRefreshFiles
    {
        private readonly int sim;

        // sim 2020 or 2024.
        public MsfsIntegration(int sim)
        {
            this.sim = sim == 2020 ? 2020 : 2024;
        }

        public int Sim { get { return sim; } }

        public string SelectedProfile { get; set; }

        // Import and scan: the user's own profiles (Steam, Microsoft Store) are read beside JoyPro's preset package,
        // and a device with several profiles gives its most recently saved one. Export, its read-back and the
        // watcher read only the export target.
        public bool ImportReading { get; set; }

        // --aircraft: the relations limited to this aircraft are exported too (for every aircraft; see PerAircraftNote).
        public string ExportAircraft { get; set; }

        public IReadOnlyList<string> AircraftKeys()
        {
            return MsfsAircraftCatalog.Keys(DbFolder);
        }

        public string PerAircraftNote
        {
            get
            {
                return sim == 2020
                    ? "MSFS 2020 input profiles from JoyPro apply to every aircraft: MSFS 2020 keeps one profile per device. Export with --aircraft <key> to write the relations limited to one aircraft (for all aircraft)."
                    : "MSFS 2024 input presets from JoyPro apply to every aircraft: JoyPro's preset (Community2024 package) is the device's default for every aircraft the sim has not configured yet. " +
                      "MSFS 2024 keeps its per-aircraft choice in profile_00, which JoyPro never writes, and aircraft-specific presets only come with the aircraft's own package. " +
                      "Export with --aircraft <key> to write the relations limited to one aircraft (for all aircraft).";
            }
        }

        public string GameId { get { return "MSFS" + sim.ToString(CultureInfo.InvariantCulture); } }
        public string DisplayName { get { return "Microsoft Flight Simulator " + sim.ToString(CultureInfo.InvariantCulture); } }
        // "msfs" (the CLI area of the single MSFS game before 2020 and 2024 were split) stays with MSFS 2024.
        public IReadOnlyList<string> Aliases { get { return sim == 2020 ? new[] { "msfs2020", "fs2020" } : new[] { "msfs2024", "fs2024", "msfs" }; } }
        public string DbFolder { get { return GameId; } }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return true; } }
        public string Notes
        {
            get
            {
                return sim == 2020
                    ? "Experimental. Close MSFS 2020 first. JoyPro edits the device profiles in the Steam remote folder (inputprofile_*); Steam Cloud can restore older profiles. The Microsoft Store version's profiles are imported, not written. Presets apply to every aircraft: relations limited to MSFS aircraft are only exported for the aircraft picked with --aircraft."
                    : "Experimental. Close MSFS 2024 first. JoyPro writes its presets as a package into the sim's Community2024 folder (joypro-input-profiles); the sim uses them for every aircraft it has not configured for that device yet. Aircraft configured before keep their settings until Controls > Reset to Defaults (which resets the presets of all devices). Import reads your own profiles (Steam and Microsoft Store) and JoyPro's presets. Presets apply to every aircraft: relations limited to MSFS aircraft are only exported for the aircraft picked with --aircraft.";
            }
        }
        public IReadOnlyList<string> ProcessNames { get { return sim == 2020 ? new[] { "FlightSimulator" } : new[] { "FlightSimulator2024" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        // Modifiers: buttons of the same device only.
        public GameFeature KeyboardBinds { get { return GameFeature.No; } }
        public GameFeature ModifierBinds { get { return GameFeature.Partial; } }

        private const int FlagDigital = 2;
        private const int FlagAxis = 4;
        private const int FlagInvert = 128;
        private const int FlagNoSensitivityCurve = 1024;
        private const int FlagAxisOverride = 4096;

        // Flight contexts of MSFS 2020 and 2024; cameras, menus, drone, slew, ATC etc. are never touched.
        private static readonly HashSet<string> FlightContexts = new HashSet<string>(StringComparer.Ordinal)
        {
            "AIRCRAFT", "AIRCRAFT_PC", "AIRCRAFT_ACTIVE_INTERACTION", "BASIC_CONTROL", "BASIC_CONTROL_HELICOPTER",
            "BASIC_CONTROL_LIGHTER_THAN_AIR", "INSTRUMENTS_CONTROL", "PLANE", "HELICOPTER", "HELICOPTER_ACTIVE_INTERACTION",
            "GLIDER", "LIGHTER_THAN_AIR", "RUDDER_AXIS", "SURFACES", "SURFACES_GYRO", "SURFACES_HELICOPTER",
            "SURFACES_LIGHTER_THAN_AIR", "THROTTLE"
        };

        private static readonly Regex AxisAction = new Regex(@"AXIS|^KEY_(THROTTLE\d?|MIXTURE\d?|PROP_PITCH\d?|ELEVATOR|AILERON|RUDDER|FLAPS|SPOILERS|ELEVATOR_TRIM|AILERON_TRIM|RUDDER_TRIM|BRAKES_LEFT|BRAKES_RIGHT)_SET$", RegexOptions.Compiled);

        // KEY codes of the DIJOYSTATE2 axis slots (full axis) and their Information texts (trailing space included).
        private static readonly int[] AxisCodes = { 1026, 1042, 1058, 770, 786, 802, 514, 530 };
        private static readonly string[] AxisInfos =
        {
            "Joystick L-Axis X ", "Joystick L-Axis Y ", "Joystick L-Axis Z ", "Joystick R-Axis X ", "Joystick R-Axis Y ",
            "Joystick R-Axis Z ", "Joystick Slider X ", "Joystick Slider Y "
        };
        private static readonly string[] PovInfos = { "Joystick Pov Up", "Joystick Pov Right", "Joystick Pov Down", "Joystick Pov Left" };

        // MSFS 2020: the Steam remote folders (app 1250410) with input profiles. MSFS 2024: JoyPro's preset package in
        // the Community2024 folder (created by the first export), then the Steam remote folders of app 2537590.
        // MSFS 2024 also lists the Steam remote folders and the Store save folder (wgs), which import reads; MSFS 2020
        // lists its Steam remote folders, then the Store save folder (read only: import and scan).
        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            List<string> paths = new List<string>();
            foreach (string remote in MsfsUserProfiles.SteamRemoteFolders(sim, warnings))
            {
                if (MsfsUserProfiles.SteamProfileFiles(remote).Count > 0)
                    paths.Add(remote);
            }
            string wgs = MsfsUserProfiles.StoreWgsFolder(sim);
            if (Directory.Exists(wgs))
                paths.Add(wgs);
            if (sim == 2024)
            {
                string community = MsfsCommunityPackage.FindCommunityFolder();
                if (!string.IsNullOrEmpty(community) && Directory.Exists(community))
                    paths.Insert(0, Path.Combine(community, MsfsCommunityPackage.PackageName));
            }
            return paths;
        }

        // A folder holds every MSFS profile of every device. JoyPro keeps one bind per device, so per device (and
        // 2024 category) exactly one profile is used: the one named with --game-profile, or the only one there is.
        // Import (forImport) takes the most recently saved one when there are several and says which; an export skips
        // the device. JoyPro's package presets and the user's own profiles are chosen separately (import reads both).
        private List<MsfsProfileFile> LoadSelectedProfiles(string configPath, List<string> errors, List<string> warnings, bool forImport = false, List<string> notes = null)
        {
            List<MsfsProfileFile> profiles = LoadProfiles(configPath, errors, warnings, forImport, notes);
            if (!string.IsNullOrWhiteSpace(SelectedProfile))
            {
                profiles = profiles.Where(p => string.Equals(p.FriendlyName, SelectedProfile, StringComparison.OrdinalIgnoreCase)).ToList();
                if (profiles.Count == 0 && errors.Count == 0)
                    errors.Add("No MSFS profile named '" + SelectedProfile + "' in " + configPath + ".");
            }

            List<MsfsProfileFile> selected = new List<MsfsProfileFile>();
            foreach (IGrouping<string, MsfsProfileFile> group in profiles.GroupBy(p => (p.Source == MsfsProfileSourceKind.Package ? "package" : "user") + "|" + p.DeviceGuid.ToString() + "|" + p.Category))
            {
                if (group.Count() == 1)
                {
                    selected.Add(group.First());
                    continue;
                }
                MsfsProfileFile first = group.First();
                if (forImport)
                {
                    List<MsfsProfileFile> newestFirst = group.OrderByDescending(p => SavedAt(p.FilePath)).ToList();
                    selected.Add(newestFirst[0]);
                    warnings.Add("Device '" + first.DeviceName + "' has " + group.Count().ToString(CultureInfo.InvariantCulture) + " MSFS profiles" +
                                 (first.Category.Length > 0 ? " (" + first.Category + ")" : string.Empty) + "; imported the most recently saved one, '" + newestFirst[0].FriendlyName +
                                 "' (others: " + string.Join(", ", newestFirst.Skip(1).Select(p => "'" + p.FriendlyName + "'")) + "). Pick another with --game-profile <name>.");
                    continue;
                }
                warnings.Add("Device '" + first.DeviceName + "' has " + group.Count().ToString(CultureInfo.InvariantCulture) + " MSFS profiles (" +
                             string.Join(", ", group.Select(p => "'" + p.FriendlyName + "'")) + "); skipped. Pick one with --game-profile <name> or pass its file with --path.");
            }
            return selected;
        }

        private static DateTime SavedAt(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path);
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        private List<MsfsProfileFile> LoadProfiles(string configPath, List<string> errors, List<string> warnings, bool forImport = false, List<string> notes = null)
        {
            List<KeyValuePair<string, MsfsProfileSourceKind>> files = new List<KeyValuePair<string, MsfsProfileSourceKind>>();
            List<MsfsProfileSource> userSources = null;
            string package;
            string wgs = MsfsUserProfiles.WgsFolderOf(configPath);
            if (MsfsCommunityPackage.TryGetPackageFolder(configPath, out package))
            {
                files.AddRange(MsfsCommunityPackage.PresetFiles(package).Select(f => new KeyValuePair<string, MsfsProfileSourceKind>(f, MsfsProfileSourceKind.Package)));
                // Import reads what the user set up in the sim (Steam or Microsoft Store), and JoyPro's presets besides;
                // a missing package is no error then.
                if (forImport)
                {
                    userSources = MsfsUserProfiles.UserSources(sim, warnings);
                    foreach (MsfsProfileSource source in userSources)
                        files.AddRange(source.Files.Select(f => new KeyValuePair<string, MsfsProfileSourceKind>(f, source.Kind)));
                }
                if (files.Count == 0)
                {
                    errors.Add(forImport
                        ? NoProfilesMessage(package, userSources, warnings)
                        : "The JoyPro MSFS 2024 package has no presets yet: " + package + " (an export creates them).");
                    return new List<MsfsProfileFile>();
                }
            }
            else if (wgs != null)
            {
                MsfsProfileSource store = MsfsUserProfiles.StoreSource(wgs, warnings);
                files.AddRange(store.Files.Select(f => new KeyValuePair<string, MsfsProfileSourceKind>(f, MsfsProfileSourceKind.Store)));
                if (files.Count == 0)
                {
                    errors.Add("No " + DisplayName + " input profile in the Microsoft Store save folder " + wgs + " (" + store.OtherBlobs.ToString(CultureInfo.InvariantCulture) +
                               " saved file(s) there, none is an input profile). The sim saves a device's profile once you change a bind in Controls.");
                    return new List<MsfsProfileFile>();
                }
            }
            else if (Directory.Exists(configPath))
            {
                MsfsProfileSourceKind kind = configPath.IndexOf("\\userdata\\", StringComparison.OrdinalIgnoreCase) >= 0 ? MsfsProfileSourceKind.Steam : MsfsProfileSourceKind.Given;
                files.AddRange(Directory.GetFiles(configPath).Where(f => !f.EndsWith(".jpbak", StringComparison.OrdinalIgnoreCase) && MsfsProfileFile.LooksLikeProfile(f))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => new KeyValuePair<string, MsfsProfileSourceKind>(f, kind)));
            }
            else if (File.Exists(configPath))
                files.Add(new KeyValuePair<string, MsfsProfileSourceKind>(configPath, MsfsUserProfiles.IsStorePath(configPath) ? MsfsProfileSourceKind.Store : MsfsProfileSourceKind.Given));
            else
            {
                errors.Add("MSFS profile file or folder not found: " + configPath);
                return new List<MsfsProfileFile>();
            }

            List<MsfsProfileFile> profiles = new List<MsfsProfileFile>();
            int keyboardOrPad = 0;
            foreach (KeyValuePair<string, MsfsProfileSourceKind> file in files)
            {
                MsfsProfileFile profile;
                if (!MsfsProfileFile.TryLoad(file.Key, File.Exists(configPath) ? errors : warnings, out profile))
                    continue;
                profile.Source = file.Value;
                if (profile.HasDeviceGuid)
                    profiles.Add(profile);
                else
                    keyboardOrPad++;
            }
            if (forImport && keyboardOrPad > 0 && notes != null)
                notes.Add(keyboardOrPad.ToString(CultureInfo.InvariantCulture) + " MSFS keyboard, mouse or gamepad profile(s) not imported (JoyPro imports the binds of joystick devices).");
            if (profiles.Count == 0 && errors.Count == 0)
                errors.Add("No MSFS device profiles (with a joystick GUID) found in " + (userSources != null ? string.Join(", ", files.Select(f => Path.GetDirectoryName(f.Key)).Distinct(StringComparer.OrdinalIgnoreCase)) : configPath) + ".");
            return profiles;
        }

        // "Profiles read: 3 from Steam, 2 from the Microsoft Store, 1 JoyPro preset" (a note in the import's output).
        private static string DescribeSources(List<MsfsProfileFile> profiles)
        {
            List<string> parts = new List<string>();
            foreach (IGrouping<MsfsProfileSourceKind, MsfsProfileFile> group in profiles.GroupBy(p => p.Source).OrderBy(g => g.Key))
            {
                string count = group.Count().ToString(CultureInfo.InvariantCulture);
                switch (group.Key)
                {
                    case MsfsProfileSourceKind.Package: parts.Add(count + " JoyPro preset(s) (Community2024 package)"); break;
                    case MsfsProfileSourceKind.Steam: parts.Add(count + " from Steam"); break;
                    case MsfsProfileSourceKind.Store: parts.Add(count + " from the Microsoft Store version"); break;
                    default: parts.Add(count + " from the given path"); break;
                }
            }
            return "MSFS device profiles read: " + string.Join(", ", parts) + ".";
        }

        private string NoProfilesMessage(string package, List<MsfsProfileSource> userSources, List<string> warnings)
        {
            List<string> searched = new List<string> { package + " (JoyPro's presets, made by an export)" };
            foreach (string folder in MsfsUserProfiles.SearchedFolders(sim, warnings))
            {
                MsfsProfileSource found = userSources == null ? null : userSources.FirstOrDefault(s => string.Equals(s.Folder, folder, StringComparison.OrdinalIgnoreCase));
                string note = found == null ? (Directory.Exists(folder) || folder.StartsWith("<", StringComparison.Ordinal) ? string.Empty : " (not there)")
                    : found.Kind == MsfsProfileSourceKind.Store ? " (" + found.OtherBlobs.ToString(CultureInfo.InvariantCulture) + " saved file(s), no input profile)"
                    : " (no inputprofile_* file)";
                searched.Add(folder + note);
            }
            return "No " + DisplayName + " input profiles found. Searched: " + string.Join("; ", searched) +
                   ". The sim saves a device's profile once you change a bind in Controls; you can also export a profile in the sim (Controls > cogwheel > Export) and import that file.";
        }

        private static string DeviceNameFor(MsfsProfileFile profile, GameDeviceDirectory devices)
        {
            GameDevice connected = devices == null ? null : devices.FindByInstanceGuid(profile.DeviceGuid);
            return connected != null ? connected.JoyProName : GameDeviceDirectory.BuildJoyProName(profile.DeviceName, profile.DeviceGuid);
        }

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            List<MsfsProfileFile> profiles = LoadSelectedProfiles(configPath, result.Errors, result.Warnings, ImportReading, result.Steps);
            if (profiles.Count > 0)
                result.Steps.Add(DescribeSources(profiles));
            int otherContexts = 0;
            HashSet<string> unknown = new HashSet<string>(StringComparer.Ordinal);
            List<GameAction> added = new List<GameAction>();
            foreach (MsfsProfileFile profile in profiles)
            {
                string device = DeviceNameFor(profile, devices);
                foreach (MsfsAction action in profile.Actions)
                {
                    if (action.Combos.Count == 0)
                        continue;
                    if (!FlightContexts.Contains(action.Context))
                    {
                        otherContexts += action.Combos.Count;
                        continue;
                    }
                    GameAction cataloged = catalog.Find(action.Name);
                    if (cataloged == null && ImportReading && action.Name.StartsWith("KEY_", StringComparison.Ordinal))
                    {
                        // A flight action the sim binds that JoyPro's list does not have yet (a list built without the
                        // sim's presets, or a newer sim): it joins the list, so the user's bind is not lost.
                        cataloged = new GameAction
                        {
                            Id = action.Name,
                            Title = MsfsGameData.TitleFromEventName(action.Name),
                            IsAxis = AxisAction.IsMatch(action.Name) || (action.Flag & FlagAxis) != 0,
                            Category = action.Context
                        };
                        if (cataloged.Title.Length == 0)
                            cataloged.Title = MsfsGameData.RawTitle(action.Name);
                        catalog.Add(cataloged);
                        added.Add(cataloged);
                    }
                    if (cataloged == null)
                    {
                        unknown.Add(action.Name);
                        continue;
                    }
                    foreach (MsfsCombo combo in action.Combos)
                    {
                        GameInputBinding binding = Decode(cataloged, action, combo, device);
                        if (binding == null)
                            continue;
                        if (binding.IsAxis)
                            binding.Tuning = ReadTuning(profile, action, combo, binding);
                        result.Bindings.Add(binding);
                    }
                }
            }
            if (otherContexts > 0)
                result.Steps.Add(otherContexts.ToString(CultureInfo.InvariantCulture) + " bind(s) in camera, menu and other non-flight contexts left out (JoyPro manages the flight controls).");
            if (unknown.Count > 0)
                result.Warnings.Add(unknown.Count.ToString(CultureInfo.InvariantCulture) + " bound flight action(s) are not in JoyPro's " + DisplayName + " command list and were left out (" +
                                    string.Join(", ", unknown.OrderBy(u => u, StringComparer.Ordinal).Take(8)) + (unknown.Count > 8 ? ", ..." : string.Empty) + "). A DB refresh adds them.");
            if (added.Count > 0)
                SaveAddedActions(added, result);
            return result;
        }

        // The actions an import found in the user's profiles go into the stored command list (the import names its
        // relations after it, and an export writes them back).
        private void SaveAddedActions(List<GameAction> added, GameScanResult result)
        {
            try
            {
                GameCatalog stored = GameCatalogStore.Load(this, new List<string>());
                int before = stored.Actions.Count;
                foreach (GameAction action in added.Where(a => !stored.Contains(a.Id)))
                    stored.Add(new GameAction { Id = action.Id, Title = action.Title, IsAxis = action.IsAxis, Category = action.Category });
                if (stored.Actions.Count == before)
                    return;
                GameCatalogStore.Save(this, stored, DisplayName + " action catalog, actions added from your profiles " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                result.Steps.Add((stored.Actions.Count - before).ToString(CultureInfo.InvariantCulture) + " flight action(s) of your profiles added to JoyPro's " + DisplayName + " command list (" +
                                 string.Join(", ", added.Select(a => a.Id).Take(6)) + (added.Count > 6 ? ", ..." : string.Empty) + ").");
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException)
                    throw;
                result.Warnings.Add("Could not add the new flight actions to JoyPro's " + DisplayName + " command list: " + ex.Message);
            }
        }

        // The last KEY of a combination is the main input, the others (buttons of the same device) are modifiers.
        private static GameInputBinding Decode(GameAction cataloged, MsfsAction action, MsfsCombo combo, string device)
        {
            string main = DecodeCode(combo.Codes[combo.Codes.Count - 1]);
            if (main.Length == 0)
                return null;
            JoyInput input;
            bool isAxis = GameInputTokens.TryParse(main, out input) && input.Kind == JoyInputKind.Axis;
            if (isAxis != cataloged.IsAxis)
                return null;

            List<string> reformers = new List<string>();
            foreach (int code in combo.Codes.Take(combo.Codes.Count - 1))
            {
                string modifier = DecodeCode(code);
                if (modifier.Length == 0 || !modifier.StartsWith("JOY_BTN", StringComparison.Ordinal))
                    return null;
                reformers.Add(modifier + "§" + device + "§" + modifier + "§False");
            }

            return new GameInputBinding
            {
                ActionId = cataloged.Id,
                Device = device,
                Input = main,
                IsAxis = isAxis,
                Inverted = isAxis && (action.Flag & FlagInvert) != 0,
                Reformers = reformers
            };
        }

        // The axis settings the action gets: its own <Axis/> with flag 4096, else the device's Sensitivity page of the
        // bound axis; flags 128 (invert) and 1024 (no sensitivity curve). Only values that differ from the game's
        // defaults are kept, so an untouched page leaves no MSFS tuning on the relation.
        private static AxisTuning ReadTuning(MsfsProfileFile profile, MsfsAction action, MsfsCombo combo, GameInputBinding binding)
        {
            AxisTuning tuning = new AxisTuning { Inverted = binding.Inverted };
            string line = null;
            if ((action.Flag & FlagAxisOverride) != 0)
            {
                line = action.OverrideAxisLine ?? combo.ExtraLines.FirstOrDefault(MsfsProfileFile.IsOverrideAxisLine);
                if (line != null)
                    tuning.Native[MsfsAxisModel.AxisOverride] = 1;
            }
            if (line == null)
            {
                string axisName = AxisNameOf(binding.Input);
                line = axisName == null ? null : profile.DeviceAxisLine(axisName);
            }
            Dictionary<string, int> values = MsfsProfileFile.AxisValues(line);
            for (int i = 0; i < MsfsAxisModel.PageKeys.Length; i++)
            {
                int value;
                if (values.TryGetValue(MsfsAxisModel.PageAttributes[i], out value) && value != MsfsAxisModel.PageDefaults[i])
                    tuning.Native[MsfsAxisModel.PageKeys[i]] = value;
            }
            if ((action.Flag & FlagNoSensitivityCurve) != 0)
                tuning.Native[MsfsAxisModel.NoSensitivityCurve] = 1;
            return tuning;
        }

        // JoyPro axis token -> AxisName of the device's <Axes> (X, Y, Z, rX, rY, rZ, SliderX, SliderY).
        private static string AxisNameOf(string token)
        {
            JoyInput input;
            if (!GameInputTokens.TryParse(token, out input) || input.Kind != JoyInputKind.Axis || input.Index < 0 || input.Index >= MsfsProfileFile.AxisNames.Length)
                return null;
            return MsfsProfileFile.AxisNames[input.Index];
        }

        private static string DecodeCode(int code)
        {
            if (code >= 0 && code < 256)
                return GameInputTokens.ButtonToken(code);
            if (code >= 256 && code <= 259)
                return GameInputTokens.PovToken(0, (code - 256) * 2);
            int slot = Array.IndexOf(AxisCodes, code);
            return slot >= 0 ? GameInputTokens.AxisToken(slot) : string.Empty;
        }

        private static bool TryEncode(string token, out int code, out string info)
        {
            code = -1;
            info = string.Empty;
            JoyInput input;
            if (!GameInputTokens.TryParse(token, out input))
                return false;
            switch (input.Kind)
            {
                case JoyInputKind.Button:
                    if (input.Index >= 256)
                        return false;
                    code = input.Index;
                    info = "Joystick Button " + (input.Index + 1).ToString(CultureInfo.InvariantCulture);
                    return true;
                case JoyInputKind.Pov:
                    if (input.Index != 0 || input.Direction % 2 != 0)
                        return false;
                    code = 256 + input.Direction / 2;
                    info = PovInfos[input.Direction / 2];
                    return true;
                case JoyInputKind.Axis:
                    if (input.Index < 0 || input.Index >= AxisCodes.Length)
                        return false;
                    code = AxisCodes[input.Index];
                    info = AxisInfos[input.Index];
                    return true;
            }
            return false;
        }

        private sealed class MsfsWanted
        {
            public string ActionId;
            public MsfsCombo Combo;
            public bool IsAxis;
            public bool Inverted;
            public string RelationName;
            // Axis binds: the resolved MSFS tuning and the device axis (AxisName of <Axes>).
            public AxisTuning Tuning;
            public string AxisName;
        }

        // An axis bind whose values go to the device's Sensitivity page (not to a per-action override).
        private sealed class MsfsDeviceAxisRequest
        {
            public string AxisName;
            public string ActionName;
            public MsfsWanted Wanted;
        }

        private static string Encode(GameInputBinding binding, GameAction action, out Guid deviceGuid, out MsfsWanted wanted)
        {
            wanted = null;
            deviceGuid = Guid.Empty;
            if (binding.IsKeyboard)
                return "MSFS keyboard profiles are not written by JoyPro.";
            string name;
            Guid guid;
            if (!GameDeviceDirectory.TryParseJoyProName(binding.Device, out name, out guid))
                return "device '" + binding.Device + "' has no DirectInput GUID.";

            MsfsCombo combo = new MsfsCombo();
            foreach (string reformer in binding.Reformers ?? new List<string>())
            {
                string[] parts = reformer.Split('§');
                string modifierName;
                Guid modifierGuid;
                int modifierCode;
                string modifierInfo;
                if (parts.Length < 3 || !GameDeviceDirectory.TryParseJoyProName(parts[1], out modifierName, out modifierGuid) || modifierGuid != guid ||
                    !parts[2].StartsWith("JOY_BTN", StringComparison.Ordinal) || !TryEncode(parts[2], out modifierCode, out modifierInfo))
                    return "MSFS combinations can only use buttons of the same device.";
                combo.Codes.Add(modifierCode);
                combo.Infos.Add(modifierInfo);
            }

            int code;
            string info;
            JoyInput input;
            if (!TryEncode(binding.Input, out code, out info) || !GameInputTokens.TryParse(binding.Input, out input))
                return "'" + binding.Input + "' has no MSFS code (hat diagonals and hats after the first are not supported).";
            bool isAxis = input.Kind == JoyInputKind.Axis;
            if (isAxis != action.IsAxis)
                return action.IsAxis ? "the action needs an axis." : "'" + binding.Input + "' is an axis; bind it to an axis action.";
            if (isAxis && combo.Codes.Count > 0)
                return "MSFS axes cannot have modifiers.";
            combo.Codes.Add(code);
            combo.Infos.Add(info);

            deviceGuid = guid;
            wanted = new MsfsWanted
            {
                ActionId = action.Id, Combo = combo, IsAxis = isAxis, Inverted = binding.Inverted, RelationName = binding.RelationName,
                Tuning = isAxis ? binding.Tuning : null, AxisName = isAxis ? AxisNameOf(binding.Input) : null
            };
            return string.Empty;
        }

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            // The Microsoft Store keeps the profiles in its save containers, which get new file names on every save
            // and sync with the Xbox cloud: read only.
            if (MsfsUserProfiles.WgsFolderOf(request.ConfigPath) != null || MsfsUserProfiles.IsStorePath(request.ConfigPath))
            {
                result.Errors.Add(sim == 2024
                    ? "The Microsoft Store profiles of MSFS 2024 are read only for JoyPro. Export to JoyPro's preset package instead (the Community2024 folder, see 'engine msfs2024 paths'); the Store version loads it too."
                    : "The Microsoft Store profiles of MSFS 2020 are read only for JoyPro: JoyPro imports them, but can only export to the Steam version's profiles.");
                return result;
            }
            // MSFS 2024: the presets live in JoyPro's Community package; every device of the export gets one.
            string package;
            bool isPackage = MsfsCommunityPackage.TryGetPackageFolder(request.ConfigPath, out package);
            if (isPackage)
            {
                List<GameDevice> exportDevices = new List<GameDevice>();
                foreach (string deviceName in request.Bindings.Select(b => b.Device).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    GameDevice device = request.Devices == null ? null : request.Devices.Devices.FirstOrDefault(d => string.Equals(d.JoyProName, deviceName, StringComparison.OrdinalIgnoreCase));
                    if (device != null)
                        exportDevices.Add(device);
                }
                if (request.DryRun)
                    result.Steps.Add("Dry run: presets would be created in " + package + " for devices without one.");
                else
                    MsfsCommunityPackage.EnsureDevicePresets(package, exportDevices, result);
            }
            List<MsfsProfileFile> profiles = LoadSelectedProfiles(request.ConfigPath, result.Errors, result.Warnings);
            if (result.Errors.Count > 0)
                return result;

            Dictionary<Guid, Dictionary<string, List<MsfsWanted>>> wantedByDevice = new Dictionary<Guid, Dictionary<string, List<MsfsWanted>>>();
            foreach (GameInputBinding binding in request.Bindings)
            {
                GameAction action = request.Catalog.Find(binding.ActionId);
                Guid guid = Guid.Empty;
                MsfsWanted wanted = null;
                string reason = action == null ? "not in the " + DisplayName + " catalog." : Encode(binding, action, out guid, out wanted);
                if (reason.Length == 0 && !profiles.Any(p => p.DeviceGuid == guid))
                    reason = "no MSFS profile of device '" + binding.Device + "' in " + request.ConfigPath + " (create one in the sim first).";
                if (reason.Length > 0)
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
                    continue;
                }

                Dictionary<string, List<MsfsWanted>> byAction;
                if (!wantedByDevice.TryGetValue(guid, out byAction))
                {
                    byAction = new Dictionary<string, List<MsfsWanted>>(StringComparer.Ordinal);
                    wantedByDevice[guid] = byAction;
                }
                List<MsfsWanted> list;
                if (!byAction.TryGetValue(action.Id, out list))
                {
                    list = new List<MsfsWanted>();
                    byAction[action.Id] = list;
                }
                if (list.Any(w => w.Combo.Signature == wanted.Combo.Signature))
                    continue;
                list.Add(wanted);
                result.BindingsWritten++;
            }

            Dictionary<string, string> learnedContexts = LearnContexts(LoadProfiles(request.ConfigPath, new List<string>(), new List<string>()));
            int changedFiles = 0;
            foreach (MsfsProfileFile profile in profiles)
            {
                Dictionary<string, List<MsfsWanted>> wanted;
                if (!wantedByDevice.TryGetValue(profile.DeviceGuid, out wanted))
                    wanted = new Dictionary<string, List<MsfsWanted>>(StringComparer.Ordinal);
                string ownershipKey = OwnershipKey(profile.FilePath);
                Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(ownershipKey, result.Warnings);
                ApplyToProfile(profile, request, wanted, owned, learnedContexts, result);
                if (request.DryRun)
                    continue;

                GameOwnershipStore.Save(ownershipKey, GameId, owned);
                if (profile.ChangedActions == 0 && profile.ChangedAxes == 0)
                    continue;
                BackUp(profile.FilePath, result);
                profile.Save(profile.FilePath);
                result.FilesWritten.Add(profile.FilePath);
                changedFiles++;
            }

            int changedActions = profiles.Sum(p => p.ChangedActions);
            int changedAxes = profiles.Sum(p => p.ChangedAxes);
            string axesText = changedAxes > 0 ? " and " + changedAxes.ToString(CultureInfo.InvariantCulture) + " device axis setting(s)" : string.Empty;
            if (request.DryRun)
                result.Steps.Add("Dry run: " + changedActions.ToString(CultureInfo.InvariantCulture) + " action(s)" + axesText + " in " + profiles.Count(p => p.ChangedActions > 0 || p.ChangedAxes > 0).ToString(CultureInfo.InvariantCulture) + " profile file(s) would change.");
            else if (changedFiles == 0)
                result.Steps.Add("The MSFS profiles already match.");
            else
                result.Steps.Add(changedActions.ToString(CultureInfo.InvariantCulture) + " action(s)" + axesText + " in " + changedFiles.ToString(CultureInfo.InvariantCulture) + " profile file(s) changed.");
            if (isPackage && !request.DryRun && Directory.Exists(package))
                MsfsCommunityPackage.WriteLayoutAndManifest(package, result);
            return result;
        }

        private void ApplyToProfile(MsfsProfileFile profile, GameExportRequest request, Dictionary<string, List<MsfsWanted>> wanted, Dictionary<string, HashSet<string>> owned, Dictionary<string, string> learnedContexts, GameIoResult result)
        {
            Dictionary<string, HashSet<string>> ownedBefore = owned.ToDictionary(e => e.Key, e => new HashSet<string>(e.Value), StringComparer.OrdinalIgnoreCase);
            bool replaceOwned = request.Mode == GameExportMode.Clean || request.Mode == GameExportMode.MergeOverwrite;
            if (replaceOwned)
                owned.Clear();
            // MSFS is flight only, so JoyPro takes over whole devices: clean clears every other bind in the profiles of
            // the devices it writes, nuke clears the profiles of devices the export does not use.
            bool clean = wanted.Count > 0 ? request.Mode == GameExportMode.Clean : request.NukeUnusedDevices;
            List<MsfsDeviceAxisRequest> deviceRequests = new List<MsfsDeviceAxisRequest>();

            foreach (MsfsAction action in profile.Actions.ToList())
            {
                GameAction cataloged = request.Catalog.Find(action.Name);
                if (cataloged == null || !FlightContexts.Contains(action.Context))
                {
                    if (clean && action.Combos.Count > 0)
                    {
                        action.Combos = new List<MsfsCombo>();
                        action.Changed = true;
                    }
                    continue;
                }
                List<MsfsWanted> wantedForAction;
                wanted.TryGetValue(action.Name, out wantedForAction);
                UpdateAction(profile, action, cataloged, wantedForAction ?? new List<MsfsWanted>(), request.Mode, clean, ownedBefore, owned, deviceRequests, result);
            }

            foreach (string id in wanted.Keys.Where(id => profile.Find(id) != null && !FlightContexts.Contains(profile.Find(id).Context)))
                result.Warnings.Add(id + " sits in the " + profile.Find(id).Context + " context of '" + Path.GetFileName(profile.FilePath) + "'; JoyPro only edits flight contexts.");

            foreach (KeyValuePair<string, List<MsfsWanted>> entry in wanted.Where(e => profile.Find(e.Key) == null))
            {
                GameAction catalogedNew = request.Catalog.Find(entry.Key);
                if (profile.IsPreset)
                {
                    string presetContext;
                    if (catalogedNew != null && FlightContexts.Contains(catalogedNew.Category ?? string.Empty))
                        presetContext = catalogedNew.Category;
                    else if (!learnedContexts.TryGetValue(entry.Key, out presetContext))
                        presetContext = "PLANE";
                    MsfsAction addedPreset = profile.AddAction(presetContext, entry.Key, catalogedNew.IsAxis ? FlagAxis : FlagDigital);
                    addedPreset.Attributes += " ValueEvent=\"0\"";
                    UpdateAction(profile, addedPreset, catalogedNew, entry.Value, request.Mode, clean, ownedBefore, owned, deviceRequests, result);
                    continue;
                }
                if (profile.Is2024)
                {
                    result.Warnings.Add(entry.Key + " is not part of profile '" + Path.GetFileName(profile.FilePath) + "' (" + (profile.Category.Length > 0 ? profile.Category : "general") + " category); not written there.");
                    continue;
                }
                string context;
                if (!learnedContexts.TryGetValue(entry.Key, out context))
                {
                    context = "PLANE";
                    result.Warnings.Add(entry.Key + " is new in '" + Path.GetFileName(profile.FilePath) + "'; its MSFS 2020 context is unknown, so it goes into PLANE.");
                }
                GameAction cataloged = request.Catalog.Find(entry.Key);
                MsfsAction added = profile.AddAction(context, entry.Key, cataloged.IsAxis ? FlagAxis : FlagDigital);
                UpdateAction(profile, added, cataloged, entry.Value, request.Mode, clean, ownedBefore, owned, deviceRequests, result);
            }

            ApplyDeviceAxes(profile, request.Mode, deviceRequests, ownedBefore, owned, result);
        }

        private static void UpdateAction(MsfsProfileFile profile, MsfsAction action, GameAction cataloged, List<MsfsWanted> wanted, GameExportMode mode, bool clean, Dictionary<string, HashSet<string>> ownedBefore, Dictionary<string, HashSet<string>> owned, List<MsfsDeviceAxisRequest> deviceRequests, GameIoResult result)
        {
            HashSet<string> wantedSignatures = new HashSet<string>(wanted.Select(w => w.Combo.Signature), StringComparer.Ordinal);
            List<MsfsCombo> combos = action.Combos.Where(c =>
                wantedSignatures.Contains(c.Signature) ||
                !(clean || (mode == GameExportMode.MergeOverwrite && GameOwnershipStore.Contains(ownedBefore, action.Name, c.Signature)))).ToList();

            if (!(mode == GameExportMode.Merge && combos.Count > 0 && combos.All(c => !wantedSignatures.Contains(c.Signature))))
            {
                foreach (MsfsWanted item in wanted)
                {
                    if (combos.Any(c => c.Signature == item.Combo.Signature))
                        continue;
                    if (combos.Count >= 2)
                    {
                        result.Warnings.Add("Relation '" + item.RelationName + "' (" + action.Name + "): both MSFS input slots of '" + Path.GetFileName(profile.FilePath) + "' are taken; not written.");
                        continue;
                    }
                    combos.Add(item.Combo);
                }
            }
            else if (wanted.Count > 0)
            {
                result.Warnings.Add(action.Name + " is already bound in '" + Path.GetFileName(profile.FilePath) + "'; merge keeps it.");
            }

            foreach (MsfsCombo combo in combos.Where(c => wantedSignatures.Contains(c.Signature)))
                GameOwnershipStore.Add(owned, action.Name, combo.Signature);
            if (!(mode == GameExportMode.Clean || mode == GameExportMode.MergeOverwrite))
            {
                HashSet<string> previous;
                if (ownedBefore.TryGetValue(action.Name, out previous))
                    foreach (string signature in previous.Where(s => combos.Any(c => c.Signature == s)))
                        GameOwnershipStore.Add(owned, action.Name, signature);
            }

            int flag = action.Flag;
            List<string> otherLines = new List<string>(action.OtherLines);
            int overrideIndex = action.OverrideAxisIndex;
            MsfsWanted axis = wanted.FirstOrDefault(w => w.IsAxis && combos.Any(c => c.Signature == w.Combo.Signature));
            if (axis != null)
            {
                flag = ((flag & ~(FlagDigital | 1)) | FlagAxis) & ~FlagInvert | (axis.Inverted ? FlagInvert : 0);
                AxisTuning tuning = axis.Tuning ?? new AxisTuning();
                // Flag 1024 only when the tuning sets it (else the file's bit stays).
                if (tuning.Native.ContainsKey(MsfsAxisModel.NoSensitivityCurve))
                    flag = tuning.Native[MsfsAxisModel.NoSensitivityCurve] >= 0.5 ? flag | FlagNoSensitivityCurve : flag & ~FlagNoSensitivityCurve;
                // Per-action override: as the tuning says, else as the file has it (the values then go where the game
                // reads them for this action). MSFS documents it for 2024 profiles only.
                bool hadOverride = (action.Flag & FlagAxisOverride) != 0;
                // A package preset always uses it: MSFS 2024 keeps the device page (Hardware Settings) per device and
                // ignores a preset's <Axes> block (in-sim check 2026-10-10).
                bool useOverride = tuning.Native.ContainsKey(MsfsAxisModel.AxisOverride) ? tuning.Native[MsfsAxisModel.AxisOverride] >= 0.5 : (hadOverride || profile.IsPreset);
                if (useOverride && !profile.Is2024 && !hadOverride)
                {
                    result.Warnings.Add("Relation '" + axis.RelationName + "' (" + action.Name + "): per-action axis settings (axis override) are an MSFS 2024 feature; '" +
                                        Path.GetFileName(profile.FilePath) + "' is an MSFS 2020 profile, so the values go to the device's Sensitivity page.");
                    useOverride = false;
                }
                flag = useOverride ? flag | FlagAxisOverride : flag & ~FlagAxisOverride;
                if (useOverride)
                {
                    if (overrideIndex >= 0)
                        otherLines[overrideIndex] = MsfsProfileFile.WithAxisValues(otherLines[overrideIndex], AxisAttributeValues(tuning, MsfsProfileFile.AxisValues(otherLines[overrideIndex])));
                    else
                        otherLines.Insert(0, MsfsProfileFile.NewAxisLine(MsfsProfileFile.ChildIndent(action), null, AxisAttributeValues(tuning, null)));
                }
                else
                {
                    if (overrideIndex >= 0)
                        otherLines.RemoveAt(overrideIndex);
                    if (axis.AxisName != null)
                        deviceRequests.Add(new MsfsDeviceAxisRequest { AxisName = axis.AxisName, ActionName = action.Name, Wanted = axis });
                }
            }
            else if ((combos.Count == 0 && (flag & FlagAxis) != 0) || (combos.Count > 0 && !cataloged.IsAxis && (flag & FlagAxis) != 0))
            {
                // Released axis (or the action became digital): no axis flags and no per-action <Axis/> left behind.
                flag = (flag & ~(FlagAxis | FlagInvert | FlagAxisOverride)) | FlagDigital;
                if (overrideIndex >= 0)
                    otherLines.RemoveAt(overrideIndex);
            }

            bool same = flag == action.Flag && combos.Count == action.Combos.Count &&
                        combos.Select(c => c.Signature).SequenceEqual(action.Combos.Select(c => c.Signature)) &&
                        otherLines.SequenceEqual(action.OtherLines);
            if (same && !action.IsNew)
                return;
            action.Combos = combos;
            action.Flag = flag;
            action.OtherLines = otherLines;
            action.Changed = true;
        }

        // The <Axis/> attributes JoyPro writes for a tuning. Keys the tuning does not set keep the file's value; a new
        // line (existing == null) gets the game's defaults for them. An attribute missing from an older line (MSFS 2020
        // files of version 1238 only have AxisSensitivy and AxisDeadZone) is only added when its value is not the default.
        private static List<KeyValuePair<string, int>> AxisAttributeValues(AxisTuning tuning, Dictionary<string, int> existing)
        {
            List<KeyValuePair<string, int>> values = new List<KeyValuePair<string, int>>();
            for (int i = 0; i < MsfsAxisModel.PageKeys.Length; i++)
            {
                string attribute = MsfsAxisModel.PageAttributes[i];
                int? wanted = MsfsAxisModel.FileValue(tuning, MsfsAxisModel.PageKeys[i]);
                if (existing == null)
                {
                    values.Add(new KeyValuePair<string, int>(attribute, wanted ?? MsfsAxisModel.PageDefaults[i]));
                    continue;
                }
                if (wanted == null)
                    continue;
                if (!existing.ContainsKey(attribute) && wanted.Value == MsfsAxisModel.PageDefaults[i])
                    continue;
                values.Add(new KeyValuePair<string, int>(attribute, wanted.Value));
            }
            return values;
        }

        private static string AxisToken(Dictionary<string, int> values)
        {
            List<string> parts = new List<string>();
            for (int i = 0; i < MsfsAxisModel.PageAttributes.Length; i++)
            {
                int value;
                parts.Add(values.TryGetValue(MsfsAxisModel.PageAttributes[i], out value) ? value.ToString(CultureInfo.InvariantCulture) : MsfsAxisModel.PageDefaults[i].ToString(CultureInfo.InvariantCulture));
            }
            return string.Join(",", parts);
        }

        private static bool IsDefaultPage(Dictionary<string, int> values)
        {
            for (int i = 0; i < MsfsAxisModel.PageAttributes.Length; i++)
            {
                int value;
                if (values.TryGetValue(MsfsAxisModel.PageAttributes[i], out value) && value != MsfsAxisModel.PageDefaults[i])
                    return false;
            }
            return true;
        }

        // The Sensitivity page holds one set of values per physical axis of the device, shared by every action bound to
        // it. JoyPro writes the values of the axis binds that do not use a per-action override; binds on the same axis
        // with different values are reported (the first relation's values are written). Merge keeps a page changed in
        // the game.
        private static void ApplyDeviceAxes(MsfsProfileFile profile, GameExportMode mode, List<MsfsDeviceAxisRequest> requests, Dictionary<string, HashSet<string>> ownedBefore,
            Dictionary<string, HashSet<string>> owned, GameIoResult result)
        {
            foreach (IGrouping<string, MsfsDeviceAxisRequest> group in requests.GroupBy(r => r.AxisName, StringComparer.Ordinal))
            {
                string axisName = group.Key;
                List<MsfsDeviceAxisRequest> list = group.ToList();
                int?[] chosen = new int?[MsfsAxisModel.PageKeys.Length];
                string[] chosenFrom = new string[MsfsAxisModel.PageKeys.Length];
                List<string> conflicts = new List<string>();
                foreach (MsfsDeviceAxisRequest request in list)
                {
                    AxisTuning tuning = request.Wanted.Tuning ?? new AxisTuning();
                    for (int i = 0; i < MsfsAxisModel.PageKeys.Length; i++)
                    {
                        int? value = MsfsAxisModel.FileValue(tuning, MsfsAxisModel.PageKeys[i]);
                        if (value == null)
                            continue;
                        if (chosen[i] == null)
                        {
                            chosen[i] = value;
                            chosenFrom[i] = request.Wanted.RelationName;
                        }
                        else if (chosen[i] != value && chosenFrom[i] != request.Wanted.RelationName)
                        {
                            conflicts.Add("'" + request.Wanted.RelationName + "' (" + request.ActionName + ") " + MsfsAxisModel.PageKeys[i] + "=" + value.Value.ToString(CultureInfo.InvariantCulture) +
                                          " vs '" + chosenFrom[i] + "' " + chosen[i].Value.ToString(CultureInfo.InvariantCulture));
                        }
                    }
                }
                if (conflicts.Count > 0)
                    result.Warnings.Add("Device '" + profile.DeviceName + "' axis " + axisName + " in '" + Path.GetFileName(profile.FilePath) + "': MSFS has one Sensitivity page per device axis, but the relations on it differ (" +
                                        string.Join("; ", conflicts.Distinct()) + "). The first relation's values were written; give them the same MSFS values" +
                                        (profile.Is2024 ? " or set axisOverride=1 for per-action values." : "."));

                string line = profile.DeviceAxisLine(axisName);
                Dictionary<string, int> existing = MsfsProfileFile.AxisValues(line);
                AxisTuning merged = new AxisTuning();
                for (int i = 0; i < MsfsAxisModel.PageKeys.Length; i++)
                    if (chosen[i] != null)
                        merged.Native[MsfsAxisModel.PageKeys[i]] = chosen[i].Value;
                List<KeyValuePair<string, int>> values = AxisAttributeValues(merged, line == null ? null : existing);
                // A new line uses the attributes of the file's other lines (plus any value that is not the default).
                HashSet<string> fileAttributes = profile.DeviceAxisAttributes;
                if (line == null && fileAttributes.Count > 0)
                    values = values.Where(v => fileAttributes.Contains(v.Key) || v.Value != MsfsAxisModel.PageDefaults[Array.IndexOf(MsfsAxisModel.PageAttributes, v.Key)]).ToList();
                Dictionary<string, int> after = new Dictionary<string, int>(existing, StringComparer.Ordinal);
                foreach (KeyValuePair<string, int> value in values)
                    after[value.Key] = value.Value;
                string ownerKey = "axis:" + axisName;
                if (line == null && IsDefaultPage(after))
                    continue;
                bool changes = line == null || values.Any(v => !existing.ContainsKey(v.Key) || existing[v.Key] != v.Value);
                if (changes && line != null && mode == GameExportMode.Merge && !IsDefaultPage(existing) && !GameOwnershipStore.Contains(ownedBefore, ownerKey, AxisToken(existing)))
                {
                    result.Warnings.Add("Device '" + profile.DeviceName + "' axis " + axisName + " has Sensitivity settings made in the game; merge keeps them.");
                    continue;
                }
                GameOwnershipStore.Add(owned, ownerKey, AxisToken(after));
                if (!changes)
                    continue;
                profile.SetDeviceAxis(axisName, values);
                result.Steps.Add("Sensitivity of '" + profile.DeviceName + "' axis " + axisName + " -> " + Path.GetFileName(profile.FilePath) + ": " + AxisToken(after) +
                                 " (sensitivity +, -, neutral, dead zone, extremity dead zone, reactivity).");
                int code = AxisCodes[Array.IndexOf(MsfsProfileFile.AxisNames, axisName)];
                List<string> others = profile.Actions.Where(a => !FlightContexts.Contains(a.Context) && a.Combos.Any(c => c.Codes.Contains(code)))
                    .Select(a => a.Name + " (" + a.Context + ")").Distinct().ToList();
                if (others.Count > 0)
                    result.Warnings.Add("The Sensitivity page of '" + profile.DeviceName + "' axis " + axisName + " also applies to " + string.Join(", ", others) + ".");
            }
        }

        // For 2020 files: the context each action uses in the other profiles of the folder.
        private static Dictionary<string, string> LearnContexts(List<MsfsProfileFile> profiles)
        {
            Dictionary<string, string> contexts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (MsfsProfileFile profile in profiles.Where(p => !p.Is2024))
                foreach (MsfsAction action in profile.Actions.Where(a => FlightContexts.Contains(a.Context)))
                    if (!contexts.ContainsKey(action.Name))
                        contexts[action.Name] = action.Context;
            return contexts;
        }

        // JOYPRO_DATA_DIR replaces %LOCALAPPDATA%\JoyPro (used by the smoke test).
        private static string JoyProDataFolder(string kind)
        {
            string root = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro");
            string folder = Path.Combine(root, kind, "MSFS");
            Directory.CreateDirectory(folder);
            return folder;
        }

        // Sidecars next to the profiles would end up in Steam Cloud, so ownership is kept per profile path locally.
        private static string OwnershipKey(string profilePath)
        {
            string full = Path.GetFullPath(profilePath).ToLowerInvariant();
            uint hash = 2166136261;
            foreach (char c in full)
                hash = (hash ^ c) * 16777619;
            return Path.Combine(JoyProDataFolder("Ownership"), Path.GetFileName(profilePath) + "_" + hash.ToString("X8", CultureInfo.InvariantCulture));
        }

        private static void BackUp(string profilePath, GameIoResult result)
        {
            string backup = Path.Combine(JoyProDataFolder("Backups"), Path.GetFileName(profilePath) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jpbak");
            File.Copy(profilePath, backup, true);
            GameBackups.Protect(profilePath, result.Warnings);
            result.Steps.Add("Backup: " + backup);
        }

        public void RecordImport(string configPath, List<GameInputBinding> applied, GameDeviceDirectory devices, GameIoResult result)
        {
            List<MsfsProfileFile> profiles = LoadSelectedProfiles(configPath, result.Errors, new List<string>(), ImportReading);
            GameCatalog catalog = GameCatalogStore.Load(this, result.Warnings);
            // Store profiles are never written, so JoyPro owns nothing there.
            foreach (MsfsProfileFile profile in profiles.Where(p => p.Source != MsfsProfileSourceKind.Store))
            {
                string ownershipKey = OwnershipKey(profile.FilePath);
                Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(ownershipKey, result.Warnings);
                foreach (GameInputBinding binding in applied)
                {
                    GameAction action = catalog.Find(binding.ActionId);
                    Guid guid;
                    MsfsWanted wanted;
                    MsfsAction existing = profile.Find(binding.ActionId);
                    if (action != null && existing != null && Encode(binding, action, out guid, out wanted).Length == 0 && guid == profile.DeviceGuid &&
                        existing.Combos.Any(c => c.Signature == wanted.Combo.Signature))
                    {
                        GameOwnershipStore.Add(owned, action.Id, wanted.Combo.Signature);
                        // The imported Sensitivity page is JoyPro's now: a later merge may change it.
                        string axisLine = wanted.AxisName == null ? null : profile.DeviceAxisLine(wanted.AxisName);
                        if (axisLine != null)
                            GameOwnershipStore.Add(owned, "axis:" + wanted.AxisName, AxisToken(MsfsProfileFile.AxisValues(axisLine)));
                    }
                }
                GameOwnershipStore.Save(ownershipKey, GameId, owned);
            }
        }

        // Rebuilds the catalog from MSFS input profiles: every action found in a flight context. MSFS 2024 exports
        // list all actions of their contexts (export one per category, e.g. airplanes and helicopters). Without a
        // path the sims' own data is read (MsfsGameData: Asobo's device presets of both sims, which bind the actions
        // of every aircraft type), and the menu names come from fs-base\en-US.locPak either way.
        // The installed aircraft (DB\MSFS\aircraft.tsv), found by RefreshCatalog and saved by refresh-db.
        private List<MsfsAircraft> pendingAircraft;

        public List<string> SaveRefreshFiles(List<string> warnings)
        {
            List<string> written = new List<string>();
            if (pendingAircraft != null && MsfsAircraftCatalog.Save(pendingAircraft, DbFolder))
                written.Add(MsfsAircraftCatalog.FilePath(DbFolder));
            pendingAircraft = null;
            return written;
        }

        // Read only: the aircraft of this sim, merged into the stored list (aircraft found before stay). Both sims are
        // read (a streamed 2024 aircraft takes its title from the same 2020 package), the list keeps this sim's.
        private void FindAircraft(GameIoResult result)
        {
            string simKey = sim.ToString(CultureInfo.InvariantCulture);
            List<MsfsAircraft> stored = MsfsAircraftCatalog.Load(MsfsAircraftCatalog.FilePath(DbFolder));
            List<MsfsAircraft> all = MsfsAircraftCatalog.Discover(result.Warnings, result.Steps);
            List<MsfsAircraft> found = all.Where(a => a.Sims.Contains(simKey)).ToList();
            pendingAircraft = MsfsAircraftCatalog.Merge(stored, found);
            result.Steps.Add("MSFS " + simKey + " aircraft found: " + found.Count.ToString(CultureInfo.InvariantCulture) +
                             " (both sims: " + all.Count.ToString(CultureInfo.InvariantCulture) +
                             "); aircraft list: " + stored.Count.ToString(CultureInfo.InvariantCulture) + " -> " + pendingAircraft.Count.ToString(CultureInfo.InvariantCulture));
        }

        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            List<string> roots = MsfsGameData.Roots(sim, result.Warnings);
            List<MsfsProfileFile> profiles;
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                List<string> files = MsfsGameData.PresetFiles(roots);
                // The user's own profiles (Steam remote folders, Microsoft Store saves) list the actions they bind too:
                // with them a list comes together even where the sim's presets are not readable.
                List<string> userFiles = MsfsGameData.UserProfilesAllowed()
                    ? MsfsUserProfiles.UserSources(sim, result.Warnings).SelectMany(s => s.Files).ToList()
                    : new List<string>();
                if (files.Count == 0 && userFiles.Count == 0)
                {
                    result.Errors.Add("No " + DisplayName + " input presets found: neither the sim's own (Steam: <install>\\Packages\\fs-input-profiles, Microsoft Store: " +
                                      "<drive>\\XboxGames\\" + (sim == 2020 ? "Microsoft Flight Simulator" : "Microsoft Flight Simulator 2024") + "\\Content\\Packages, MSFS 2020: <install>\\Input; searched: " +
                                      (roots.Count == 0 ? "no install found" : string.Join(", ", roots)) + ") nor your own profiles (" +
                                      string.Join(", ", MsfsUserProfiles.SearchedFolders(sim, result.Warnings)) + "). Set the install folder or pass --path with exported profiles.");
                    return null;
                }
                if (files.Count == 0)
                    result.Warnings.Add("The sim's own input presets were not found (searched: " + (roots.Count == 0 ? "no install found" : string.Join(", ", roots)) +
                                        "); the list is built from your own profiles and the sim's menu names.");
                files.AddRange(userFiles);
                profiles = new List<MsfsProfileFile>();
                int fileIndex = 0;
                foreach (string file in files)
                {
                    EngineProgress.ThrowIfCancelled("reading " + DisplayName + "'s input presets");
                    EngineProgress.Report(++fileIndex, files.Count, DisplayName + ", reading the input presets", Path.GetFileName(file));
                    MsfsProfileFile profile;
                    if (MsfsProfileFile.TryLoad(file, new List<string>(), out profile))
                        profiles.Add(profile);
                }
                result.Steps.Add("Game presets read: " + profiles.Count.ToString(CultureInfo.InvariantCulture) + " of " + files.Count.ToString(CultureInfo.InvariantCulture) +
                                 (userFiles.Count > 0 ? " (" + userFiles.Count.ToString(CultureInfo.InvariantCulture) + " of them your own profiles)" : string.Empty));
                sourcePath = string.Join(";", roots);
            }
            else
            {
                profiles = LoadProfiles(sourcePath, result.Errors, result.Warnings, ImportReading);
                if (profiles.Count == 0)
                    return null;
            }

            GameCatalog catalog = new GameCatalog { GameId = GameId, SourcePath = sourcePath };
            if (current != null)
            {
                // Copies: a renamed title must not change the loaded catalog (refresh-db compares the two).
                foreach (GameAction action in current.Actions)
                    catalog.Add(new GameAction { Id = action.Id, Title = action.Title, IsAxis = action.IsAxis, Category = action.Category, NativeId = action.NativeId });
            }
            foreach (MsfsProfileFile profile in profiles)
            {
                foreach (MsfsAction action in profile.Actions.Where(a => FlightContexts.Contains(a.Context)))
                {
                    string title = (action.Name.StartsWith("KEY_", StringComparison.Ordinal) ? action.Name.Substring(4) : action.Name).Replace('_', ' ');
                    catalog.Add(new GameAction { Id = action.Name, Title = title, IsAxis = AxisAction.IsMatch(action.Name), Category = action.Context });
                }
            }
            // New actions and those still titled by their raw event name ("AXIS ELEVATOR SET") get the name the sim's
            // Controls menu shows, readable ("Elevator Axis"); without one (no locPak, an event it does not list) a
            // readable title from the event name. Every other known title stays (import names new relations after it;
            // relations made before keep their names, the action ids do not change).
            Dictionary<string, string> titles = MsfsGameData.Titles(roots, result.Warnings);
            // The flight events the locPak names but no preset binds (most of the sim's aircraft systems): JoyPro's
            // rules pick the flight ones and their context (MsfsGameData.IsFlightEvent / GuessContext).
            int fromNames = 0;
            foreach (KeyValuePair<string, string> named0 in titles.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                if (catalog.Contains(named0.Key) || !MsfsGameData.IsFlightEvent(named0.Key))
                    continue;
                catalog.Add(new GameAction { Id = named0.Key, Title = named0.Key.Substring(4).Replace('_', ' '), IsAxis = AxisAction.IsMatch(named0.Key), Category = MsfsGameData.GuessContext(named0.Key) });
                fromNames++;
            }
            if (titles.Count > 0)
                result.Steps.Add("Flight events the sim names but no preset binds: " + fromNames.ToString(CultureInfo.InvariantCulture) + " new");
            int named = 0;
            int fromEvent = 0;
            foreach (GameAction action in catalog.Actions.Where(a => current == null || !current.Contains(a.Id) || a.Title == MsfsGameData.RawTitle(a.Id)))
            {
                string title;
                bool menu = titles.TryGetValue(action.Id, out title);
                if (!menu)
                    title = MsfsGameData.TitleFromEventName(action.Id);
                if (title.Length == 0 || title == action.Title)
                    continue;
                action.Title = title;
                if (menu)
                    named++;
                else
                    fromEvent++;
            }
            result.Steps.Add("Readable names (new actions and raw event names): " + named.ToString(CultureInfo.InvariantCulture) + " from the game's menu, " +
                             fromEvent.ToString(CultureInfo.InvariantCulture) + " from the event name" + (titles.Count == 0 ? " (no en-US.locPak found)" : string.Empty));
            result.Steps.Add("Profiles read: " + profiles.Count.ToString(CultureInfo.InvariantCulture) + ", catalog actions: " + catalog.Actions.Count.ToString(CultureInfo.InvariantCulture));
            FindAircraft(result);
            return catalog;
        }
    }
}
