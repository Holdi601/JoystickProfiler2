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
    // read by either game, so a path of the other sim still works. Only actions of the managed contexts are changed
    // (flight and general ones; menus, world map and developer tools stay the sim's, see IsManagedContext);
    // JoyPro's ownership data and backups live under %LOCALAPPDATA%\JoyPro2, never in the synced folder.
    // See docs/games/msfs.md.
    internal sealed partial class MsfsIntegration : IGameIntegration, IGameImportReading, IGameDisconnectedDevices, IGameSimDefaults, IGameOwnershipTracking, IGameProfileSelection, IGameAircraftScope, IGameCatalogRefreshFiles, IGameCatalogTidy
    {
        private readonly int sim;

        // A list built before the readable names (or without the sim's locPak) has raw event names ("ADF STBY SET"): the
        // other sim's menu name of the same event when its list has one, else the name made from the event
        // (MsfsGameData.TitleFromEventName). The ids and contexts stay as they are.
        public void TidyCatalog(GameCatalog catalog)
        {
            Dictionary<string, string> other = null;
            foreach (GameAction action in catalog.Actions)
            {
                if (action.Title.Length > 0 && action.Title != MsfsGameData.RawTitle(action.Id) && action.Title.Any(char.IsLower))
                    continue;
                if (other == null)
                    other = SiblingTitles();
                string title;
                if (other.TryGetValue(action.Id, out title) && title.Any(char.IsLower))
                    action.Title = title;
                else
                {
                    string made = MsfsGameData.TitleFromEventName(action.Id);
                    if (made.Length > 0)
                        action.Title = made;
                }
            }
        }

        // The other sim's titles (read straight from its list, so the two never read each other in turn).
        private Dictionary<string, string> SiblingTitles()
        {
            Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = GameDataPaths.DbFile("MSFS" + (sim == 2020 ? "2024" : "2020"), GameCatalogStore.FileName);
                if (!File.Exists(path))
                    return titles;
                foreach (string line in File.ReadAllLines(path, System.Text.Encoding.UTF8))
                {
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                        continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length > 1 && parts[0].Trim().Length > 0 && parts[1].Trim().Length > 0)
                        titles[parts[0].Trim()] = parts[1].Trim();
                }
            }
            catch (IOException)
            {
            }
            return titles;
        }

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

        // Import and scan: also the profiles of devices that are not connected (--include-disconnected).
        public bool IncludeDisconnectedDevices { get; set; }

        // Import and scan: also the sim's default presets of devices whose use the sim's records do not show
        // (--include-sim-defaults; GUI: "Also import default presets the sim may not use").
        public bool IncludeUnknownSimDefaults { get; set; }

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
                    : "MSFS 2024: relations limited to aircraft go only into your own MSFS profiles (direct write) that the sim uses for those aircraft, not into JoyPro's preset or the import files. " +
                      "With the direct write off (--user-profiles false), export with --aircraft <key> to write the relations limited to one aircraft (for all aircraft).";
            }
        }

        // MSFS 2024 with the direct write: every own profile gets the relations valid for all of its aircraft.
        public bool WritesLimitedPerAircraft { get { return sim == 2024 && WriteUserProfiles; } }

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
                    : "Experimental. Close MSFS 2024 first. JoyPro writes your binds into the profile the sim uses for each device and category (Steam remote folder or Microsoft Store save, backed up before every write; --user-profiles false turns it off) and its presets as a package into the sim's Community2024 folder (joypro-input-profiles) for aircraft the sim has not set up for a device yet. Where your profile cannot be written, the export writes a file per device and category into Documents\\JoyPro2\\MSFS 2024 import files: import it in the sim (Controls > device > category > cogwheel of the profile > Import). Import reads your own profiles (Steam and Microsoft Store), and JoyPro's presets only for devices without an own profile. The sim keeps a profile per device and aircraft: every own profile its records name gets the relations for all of its aircraft; a relation limited to some aircraft goes only into the profiles of those aircraft.";
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

        // Flight contexts of MSFS 2020 and 2024 (the aircraft categories of the Controls menu).
        private static readonly HashSet<string> FlightContexts = new HashSet<string>(StringComparer.Ordinal)
        {
            "AIRCRAFT", "AIRCRAFT_PC", "AIRCRAFT_ACTIVE_INTERACTION", "BASIC_CONTROL", "BASIC_CONTROL_HELICOPTER",
            "BASIC_CONTROL_LIGHTER_THAN_AIR", "INSTRUMENTS_CONTROL", "PLANE", "HELICOPTER", "HELICOPTER_ACTIVE_INTERACTION",
            "GLIDER", "LIGHTER_THAN_AIR", "RUDDER_AXIS", "SURFACES", "SURFACES_GYRO", "SURFACES_HELICOPTER",
            "SURFACES_LIGHTER_THAN_AIR", "THROTTLE"
        };

        // Contexts JoyPro leaves alone: menu and world map navigation, developer mode, its editors and debug tools (the
        // sim's own UI binds, not the user's). Every other context is managed: the flight contexts and the general ones
        // (the sim's "General" / Transversal category: cockpit and external cameras, views, active pause, drone, slew,
        // EFB, ATC, toolbar, photo mode, the on-foot character, ...). Import reads them, export writes them back.
        private static readonly Regex UnmanagedContext = new Regex("^(MENU|MENU_\\w+|WORLDMAP_\\w+|DEBUG|DEVMODE\\w*|EDITOR_\\w+|MISSION_FLOW_TOOLS)$", RegexOptions.Compiled);

        internal static bool IsManagedContext(string context)
        {
            return !string.IsNullOrWhiteSpace(context) && !UnmanagedContext.IsMatch(context);
        }

        // A managed context outside the flight ones: the sim's "General" binds (MSFS 2024: the Transversal category).
        internal static bool IsGeneralContext(string context)
        {
            return IsManagedContext(context) && !FlightContexts.Contains(context);
        }

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
        // keep: drops profiles before the choice (import: devices that are not connected, so a skipped device gets no
        // "has N MSFS profiles" warning).
        private List<MsfsProfileFile> LoadSelectedProfiles(string configPath, List<string> errors, List<string> warnings, bool forImport = false, List<string> notes = null,
                                                           Func<List<MsfsProfileFile>, List<MsfsProfileFile>> keep = null)
        {
            List<MsfsProfileFile> profiles = LoadProfiles(configPath, errors, warnings, forImport, notes);
            if (keep != null)
                profiles = keep(profiles);
            // The user's own profile is the truth for its device and category: JoyPro's package preset of the same device
            // and category is not read beside it (a bind the user removed in the sim must not come back from the preset;
            // tester report "deleted ATC Panel Choice 0 in MSFS, JP did not see it"). The package stays for devices
            // without an own profile.
            MsfsSimProfile activeRecords = null;
            if (forImport && sim == 2024)
            {
                HashSet<string> ownKeys = new HashSet<string>(profiles.Where(p => p.Source != MsfsProfileSourceKind.Package && p.Source != MsfsProfileSourceKind.SimDefault).Select(CategorySlotKey), StringComparer.Ordinal);
                List<MsfsProfileFile> shadowed = profiles.Where(p => p.Source == MsfsProfileSourceKind.Package && ownKeys.Contains(CategorySlotKey(p))).ToList();
                if (shadowed.Count > 0)
                {
                    profiles = profiles.Except(shadowed).ToList();
                    JoyPro.Logging.JoyProLog.Info("MSFS", "Import: JoyPro's package presets left out where the user has an own profile of the device and category: " +
                        string.Join("; ", shadowed.Select(p => p.DeviceName + " " + (p.CategoryKey.Length == 0 ? "General" : p.CategoryKey) + " " + p.FilePath)));
                    notes?.Add(shadowed.Count.ToString(CultureInfo.InvariantCulture) + " JoyPro preset(s) of the Community2024 package not read: your own MSFS profile of that device and category is what the sim uses (" +
                               string.Join(", ", shadowed.Select(p => p.DeviceName.Trim() + " " + (IsGeneralSlot(p) ? "General" : "Airplanes")).Distinct()) + ").");
                }
                activeRecords = MsfsSimProfile.Load(sim, new[] { MsfsUserProfiles.WgsFolderOf(configPath) });
                // A package preset of a device the sim has records of counts only where a record names it (the sim
                // applies it); tester: two JoyPro-made relations came back from the preset on an import into a clean
                // profile although the sim shows them nowhere. No records of the device (never set up): the sim takes
                // the preset the first time, so it counts.
                List<MsfsProfileFile> unused = new List<MsfsProfileFile>();
                foreach (MsfsProfileFile preset in profiles.Where(p => p.Source == MsfsProfileSourceKind.Package))
                {
                    List<MsfsSimProfile.Record> deviceRecords = activeRecords.RecordsOf(preset.DeviceGuid)
                        .Where(r => IsGeneralSlot(preset) || string.IsNullOrEmpty(r.Category) || string.Equals(r.Category, "AIRPLANE", StringComparison.OrdinalIgnoreCase)).ToList();
                    string file = Path.GetFileName(preset.FilePath).ToLowerInvariant();
                    if (deviceRecords.Count > 0 && !deviceRecords.Any(r => string.Equals(IsGeneralSlot(preset) ? r.General : (r.AircraftPreset ?? r.CategoryPreset), file, StringComparison.OrdinalIgnoreCase)))
                        unused.Add(preset);
                }
                if (unused.Count > 0)
                {
                    profiles = profiles.Except(unused).ToList();
                    JoyPro.Logging.JoyProLog.Info("MSFS", "Import: JoyPro's package presets the sim's records do not use left out: " + string.Join("; ", unused.Select(p => p.DeviceName + " " + p.FilePath)));
                    notes?.Add(unused.Count.ToString(CultureInfo.InvariantCulture) + " JoyPro preset(s) of the Community2024 package not read: the sim's records of the device do not use them (" +
                               string.Join(", ", unused.Select(p => p.DeviceName.Trim() + " " + (IsGeneralSlot(p) ? "General" : "Airplanes")).Distinct()) + ").");
                }
            }
            if (!string.IsNullOrWhiteSpace(SelectedProfile))
            {
                profiles = profiles.Where(p => string.Equals(p.FriendlyName, SelectedProfile, StringComparison.OrdinalIgnoreCase)).ToList();
                if (profiles.Count == 0 && errors.Count == 0)
                    errors.Add("No MSFS profile named '" + SelectedProfile + "' in " + configPath + ".");
            }

            List<MsfsProfileFile> selected = new List<MsfsProfileFile>();
            foreach (IGrouping<string, MsfsProfileFile> group in profiles.GroupBy(p => (p.Source == MsfsProfileSourceKind.Package ? "package" : "user") + "|" + p.DeviceGuid.ToString() + "|" + p.CategoryKey))
            {
                if (group.Count() == 1)
                {
                    selected.Add(group.First());
                    JoyPro.Logging.JoyProLog.Debug("MSFS", "Profile of " + group.First().DeviceName + " (" + (group.First().CategoryKey.Length == 0 ? "no category" : group.First().CategoryKey) + ", " +
                                                           group.First().Source + "): '" + group.First().FriendlyName + "' (the only one) " + group.First().FilePath);
                    continue;
                }
                JoyPro.Logging.JoyProLog.Debug("MSFS", "Profiles of " + group.First().DeviceName + " (" + (group.First().CategoryKey.Length == 0 ? "no category" : group.First().CategoryKey) + "): " +
                    string.Join("; ", group.OrderByDescending(p => SavedAt(p.FilePath)).Select(p => "'" + p.FriendlyName + "' saved " + SavedAt(p.FilePath).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z " + p.FilePath)) +
                    (forImport ? " -> the newest is imported" : " -> skipped for the export (several)"));
                MsfsProfileFile first = group.First();
                if (forImport && activeRecords != null && first.Source != MsfsProfileSourceKind.Package)
                {
                    // Several own profiles the sim uses, each for its aircraft (records per device and aircraft): all of
                    // them are read; a bind in only some of them is for those aircraft (Read).
                    int slotTotal;
                    List<KeyValuePair<MsfsProfileFile, List<string>>> perAircraft = OwnProfilesByAircraft(activeRecords, first.DeviceGuid, IsGeneralSlot(first), out slotTotal)
                        .Select(n => new KeyValuePair<MsfsProfileFile, List<string>>(group.FirstOrDefault(p => string.Equals(SimProfileName(p), n.Key, StringComparison.OrdinalIgnoreCase)), n.Value))
                        .Where(n => n.Key != null).ToList();
                    if (perAircraft.Count >= 2)
                    {
                        foreach (KeyValuePair<MsfsProfileFile, List<string>> slotProfile in perAircraft)
                        {
                            slotProfile.Key.RecordAircraft = slotProfile.Value;
                            slotProfile.Key.SlotProfiles = perAircraft.Count;
                            selected.Add(slotProfile.Key);
                        }
                        JoyPro.Logging.JoyProLog.Info("MSFS", "Import: " + first.DeviceName + " " + (IsGeneralSlot(first) ? "General" : "Airplanes") + ": the sim uses " +
                            perAircraft.Count.ToString(CultureInfo.InvariantCulture) + " own profiles per aircraft: " +
                            string.Join("; ", perAircraft.Select(u => "'" + u.Key.FriendlyName + "' " + SimProfileName(u.Key) + " (" + string.Join(", ", u.Value) + ")")));
                        notes?.Add("Device '" + first.DeviceName.Trim() + "' uses " + perAircraft.Count.ToString(CultureInfo.InvariantCulture) + " of its MSFS profiles" + (first.Category.Length > 0 ? " (" + first.Category + ")" : " (General)") +
                                   " for different aircraft; imported all of them (" + string.Join(", ", perAircraft.Select(u => "'" + u.Key.FriendlyName + "': " + string.Join(", ", u.Value))) +
                                   "): a bind in only some of them is imported for all aircraft and listed, to limit it to those aircraft.");
                        continue;
                    }
                    // The profile the sim uses (its device records name it) wins over the newest.
                    int naming, total;
                    List<string> others;
                    string active = ActiveOwnProfile(activeRecords, first.DeviceGuid, IsGeneralSlot(first), out naming, out total, out others);
                    MsfsProfileFile used = active == null ? null : group.FirstOrDefault(p => string.Equals(SimProfileName(p), active, StringComparison.OrdinalIgnoreCase));
                    if (used != null)
                    {
                        selected.Add(used);
                        JoyPro.Logging.JoyProLog.Info("MSFS", "Import: " + used.DeviceName + " " + (IsGeneralSlot(used) ? "General" : "Airplanes") + ": the sim uses '" + used.FriendlyName + "' (" + active + ", " +
                                                              naming.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture) + " records) " + used.FilePath);
                        notes?.Add("Device '" + first.DeviceName.Trim() + "' has " + group.Count().ToString(CultureInfo.InvariantCulture) + " MSFS profiles" + (first.Category.Length > 0 ? " (" + first.Category + ")" : " (General)") +
                                   "; imported the one the sim uses, '" + used.FriendlyName + "' (others: " + string.Join(", ", group.Where(p => p != used).Select(p => "'" + p.FriendlyName + "'")) + ").");
                        continue;
                    }
                }
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

        // MSFS 2024: the General (Transversal) slot of a device: a profile without category, or the Transversal preset.
        private static bool IsGeneralSlot(MsfsProfileFile profile)
        {
            return profile.CategoryKey.Length == 0 || profile.IsGeneralCategory;
        }

        private static string CategorySlotKey(MsfsProfileFile profile)
        {
            return profile.DeviceGuid.ToString("N") + "|" + (IsGeneralSlot(profile) ? "G" : profile.CategoryKey == "AIRPLANE" ? "A" : profile.CategoryKey);
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
            // Store profiles: file -> (container, user folder of the save), for ownership and the sim's records.
            Dictionary<string, KeyValuePair<string, string>> storeContainers = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase);
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
                    {
                        files.AddRange(source.Files.Select(f => new KeyValuePair<string, MsfsProfileSourceKind>(f, source.Kind)));
                        foreach (KeyValuePair<string, KeyValuePair<string, string>> container in source.Containers)
                            storeContainers[container.Key] = container.Value;
                    }
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
                foreach (KeyValuePair<string, KeyValuePair<string, string>> container in store.Containers)
                    storeContainers[container.Key] = container.Value;
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

            // Where the profiles came from (diagnostics): every source folder and how many files it gave.
            JoyPro.Logging.JoyProLog.Debug("MSFS", DisplayName + " profiles for " + (forImport ? "import/scan" : "export") + " from " + configPath + ": " + files.Count.ToString(CultureInfo.InvariantCulture) + " file(s)" +
                (userSources == null ? string.Empty : "; your profiles: " + string.Join("; ", MsfsUserProfiles.SearchedFolders(sim, new List<string>()).Select(folder =>
                {
                    MsfsProfileSource found = userSources.FirstOrDefault(s => string.Equals(s.Folder, folder, StringComparison.OrdinalIgnoreCase));
                    return folder + " (" + (found == null ? (Directory.Exists(folder) ? "no input profile" : "not there") : found.Kind + ", " + found.Files.Count.ToString(CultureInfo.InvariantCulture) + " profile(s)" +
                        (found.Kind == MsfsProfileSourceKind.Store ? ", " + found.OtherBlobs.ToString(CultureInfo.InvariantCulture) + " other saved file(s)" : string.Empty)) + ")";
                }))));
            List<MsfsProfileFile> profiles = new List<MsfsProfileFile>();
            int keyboardOrPad = 0;
            foreach (KeyValuePair<string, MsfsProfileSourceKind> file in files)
            {
                MsfsProfileFile profile;
                List<string> loadErrors = new List<string>();
                Exception readError;
                if (!MsfsProfileFile.TryLoad(file.Key, file.Key, loadErrors, out profile, out readError))
                {
                    // Import: JoyPro's own preset that lost its EFS encryption (JoyPro 2.1.1 to 2.2.1, Store install) is
                    // repaired or read from JoyPro's backup copy, so its binds are not dropped (a warning says what to do).
                    bool ownUnreadable = forImport && file.Value == MsfsProfileSourceKind.Package && readError != null && SafeFileIo.IsEncryptionFailure(readError);
                    if (!ownUnreadable)
                    {
                        (File.Exists(configPath) ? errors : warnings).AddRange(loadErrors);
                        continue;
                    }
                    if (!TryUnreadablePreset(file.Key, notes ?? warnings, warnings, out profile))
                        continue;
                }
                profile.Source = file.Value;
                KeyValuePair<string, string> storeContainer;
                if (storeContainers.TryGetValue(file.Key, out storeContainer))
                {
                    profile.StoreContainer = storeContainer.Key;
                    profile.StoreUserFolder = storeContainer.Value;
                }
                EngineDiagnostics.NoteRead(file.Key, DisplayName + " " + file.Value + " profile '" + profile.FriendlyName + "': " + profile.DeviceName +
                    (profile.HasDeviceGuid ? " {" + profile.DeviceGuid.ToString().ToUpperInvariant() + "}" : " (keyboard/mouse/gamepad)") + ", " +
                    (profile.CategoryKey.Length == 0 ? "no category" : profile.CategoryKey) + (profile.IsPreset ? ", preset" : string.Empty) + ", " +
                    profile.Actions.Count(a => a.Combos.Count > 0).ToString(CultureInfo.InvariantCulture) + " bound of " + profile.Actions.Count.ToString(CultureInfo.InvariantCulture) + " listed action(s)");
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

        // JoyPro's own preset that Windows cannot read: in a Store install's encrypted data a file without the EFS mark
        // is refused (409; JoyPro 2.1.1 to 2.2.1 removed the mark), by the sim too. The import encrypts it again where
        // Windows can, else reads JoyPro's newest readable copy of it (the backup an export made before writing it, or
        // the file an export moved out of the package); without one its binds are left out. Each case says what to do.
        private static bool TryUnreadablePreset(string path, List<string> notes, List<string> warnings, out MsfsProfileFile profile)
        {
            profile = null;
            Exception error;
            string label = MsfsCommunityPackage.PresetDeviceLabel(path);
            if (SafeFileIo.FolderOfIsEncrypted(path) && !SafeFileIo.IsEncrypted(path) && SafeFileIo.TryEncryptPath(path) == null &&
                MsfsProfileFile.TryLoad(path, path, new List<string>(), out profile, out error))
            {
                JoyPro.Logging.JoyProLog.Info("MSFS", "Package encryption: encrypted " + path + " (import).");
                notes.Add("JoyPro's MSFS 2024 preset '" + label + "' had lost its encryption (EFS) and is encrypted again, so the sim and JoyPro can read it.");
                return true;
            }
            string why = "JoyPro's MSFS 2024 preset '" + label + "' cannot be read: the sim keeps its data encrypted (EFS) on this PC and this file is not " +
                         "encrypted as expected (JoyPro 2.1.1 to 2.2.1 removed it), so the sim does not use it either (" + path + "). ";
            foreach (string copy in CopiesOf(path))
            {
                if (!MsfsProfileFile.TryLoad(copy, path, new List<string>(), out profile, out error))
                    continue;
                warnings.Add(why + "Its binds were imported from JoyPro's copy of " + SavedAt(copy).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                             " (" + copy + "). Export from JoyPro once: it writes the preset again so the sim uses it.");
                return true;
            }
            warnings.Add(why + "JoyPro has no readable copy of it, so its binds were not imported. Import with 'Merge into profile' (not --replace) to keep " +
                         "the binds of your JoyPro profile, then export from JoyPro once: it writes the preset again.");
            return false;
        }

        // JoyPro's copies of a package file, newest first: export backups (<data>\Backups\MSFS\<file>.<time>.jpbak) and
        // files an export moved out of the package (<data>\Backups\MSFS\package-<time>\<file>).
        private static List<string> CopiesOf(string path)
        {
            List<string> copies = new List<string>();
            try
            {
                string backups = JoyProDataFolder("Backups");
                string name = Path.GetFileName(path);
                copies.AddRange(Directory.GetFiles(backups, name + ".*.jpbak"));
                foreach (string moved in Directory.GetDirectories(backups, "package-*"))
                {
                    string file = Path.Combine(moved, name);
                    if (File.Exists(file))
                        copies.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                JoyPro.Logging.JoyProLog.Debug("MSFS", "No copies of " + path + ": " + ex.Message);
            }
            return copies.OrderByDescending(SavedAt).ToList();
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
                    case MsfsProfileSourceKind.SimDefault: parts.Add(count + " default preset(s) of the sim (devices without an own profile)"); break;
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
            bool simDefaults = UsesSimDefaults(configPath);
            List<string> loadErrors = simDefaults ? new List<string>() : result.Errors;
            List<MsfsProfileFile> profiles = LoadSelectedProfiles(configPath, loadErrors, result.Warnings, ImportReading, result.Steps,
                ImportReading ? (Func<List<MsfsProfileFile>, List<MsfsProfileFile>>)(all => WithoutDisconnectedDevices(all, devices, result)) : null);
            if (simDefaults)
            {
                List<MsfsProfileFile> defaults = SimDefaultProfiles(profiles, devices, configPath, result);
                if (defaults.Count > 0 && profiles.Count == 0)
                {
                    // Nothing saved yet: the defaults are the import, the "no profile found" reason only a log line.
                    result.Steps.Add("No own " + DisplayName + " profile saved yet (the sim saves one once you change a bind in Controls): the connected devices use the sim's default presets.");
                    foreach (string error in loadErrors)
                        JoyPro.Logging.JoyProLog.Info("MSFS", error);
                }
                else
                    result.Errors.AddRange(loadErrors);
                profiles.AddRange(defaults);
            }
            if (profiles.Count > 0)
                result.Steps.Add(DescribeSources(profiles));
            int otherContexts = 0;
            HashSet<string> unknown = new HashSet<string>(StringComparer.Ordinal);
            List<GameAction> added = new List<GameAction>();
            List<GameAction> movedToGeneral = new List<GameAction>();
            Dictionary<string, string> menuTitles = null;
            List<KeyValuePair<GameInputBinding, MsfsProfileFile>> perAircraftBinds = new List<KeyValuePair<GameInputBinding, MsfsProfileFile>>();
            foreach (MsfsProfileFile profile in profiles)
            {
                string device = DeviceNameFor(profile, devices);
                // Flight actions first: an input bound to a flight action and a camera action (throttle axis and
                // cockpit zoom) gives one relation, named after the flight action.
                foreach (MsfsAction action in profile.Actions.OrderBy(a => IsGeneralContext(a.Context) ? 1 : 0))
                {
                    if (action.Combos.Count == 0)
                        continue;
                    if (!IsManagedContext(action.Context))
                    {
                        otherContexts += action.Combos.Count;
                        continue;
                    }
                    GameAction cataloged = catalog.Find(action.Name);
                    if (cataloged == null && ImportReading && EventName.IsMatch(action.Name))
                    {
                        // An action the sim binds that JoyPro's list does not have yet (a list built without the sim's
                        // presets, a newer sim, a camera or view bind of the General category): it joins the list with
                        // its context, so the user's bind is not lost.
                        // The name the sim's Controls menu shows (en-US.locPak), else one from the event name.
                        if (menuTitles == null)
                            menuTitles = MenuTitles(result.Warnings);
                        string menuTitle;
                        cataloged = new GameAction
                        {
                            Id = action.Name,
                            Title = menuTitles.TryGetValue(action.Name, out menuTitle) && menuTitle.Length > 0 ? menuTitle : MsfsGameData.TitleFromEventName(action.Name),
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
                    if (ImportReading && MovesToGeneral(cataloged, action, profile))
                    {
                        // The list has it in a flight context (built from the other sim's profiles: MSFS 2020 binds
                        // active pause in PLANE), this sim in a general one (2024: INGAME_UI): export writes it there.
                        cataloged.Category = action.Context;
                        movedToGeneral.Add(cataloged);
                    }
                    foreach (MsfsCombo combo in action.Combos)
                    {
                        GameInputBinding binding = Decode(cataloged, action, combo, device);
                        if (binding == null)
                            continue;
                        if (binding.IsAxis)
                            binding.Tuning = ReadTuning(profile, action, combo, binding);
                        if (profile.RecordAircraft != null)
                            perAircraftBinds.Add(new KeyValuePair<GameInputBinding, MsfsProfileFile>(binding, profile));
                        else
                            result.Bindings.Add(binding);
                    }
                }
            }
            result.Bindings.AddRange(MergePerAircraft(perAircraftBinds));
            if (otherContexts > 0)
                result.Steps.Add(otherContexts.ToString(CultureInfo.InvariantCulture) + " bind(s) in menu, world map and developer contexts left out (the sim's own navigation and debug binds).");
            if (unknown.Count > 0)
                result.Warnings.Add(unknown.Count.ToString(CultureInfo.InvariantCulture) + " bound action(s) are not in JoyPro's " + DisplayName + " command list and were left out (" +
                                    string.Join(", ", unknown.OrderBy(u => u, StringComparer.Ordinal).Take(8)) + (unknown.Count > 8 ? ", ..." : string.Empty) + "). A DB refresh adds them.");
            // MSFS 2024 profiles list every action of their contexts, bound or not: the list gets all of them with the
            // context the sim lists them in, so the relation editor offers what the sim's Controls menu offers (a list
            // built from the sim's presets alone, or by an older JoyPro, has only the bound ones of the general page).
            List<GameAction> listedAdded = new List<GameAction>();
            if (ImportReading)
            {
                Dictionary<string, SortedSet<string>> listed = ListedContexts(profiles);
                AddListedActions(catalog, listed, () => menuTitles ?? (menuTitles = MenuTitles(result.Warnings)), listedAdded, movedToGeneral, ListedFlags(profiles));
            }
            NoteActionAircraft(added.Concat(listedAdded), profiles);
            if (added.Count > 0 || movedToGeneral.Count > 0 || listedAdded.Count > 0)
                SaveAddedActions(added, movedToGeneral, result, listedAdded);
            return result;
        }

        // An action new to the list that only some of the own profiles of a device and category have (the sim keeps one
        // per aircraft) exists for those aircraft only (an add-on aircraft's own action): the list records them, so the
        // relation editor offers it for them and the import limits its relation to them.
        private static void NoteActionAircraft(IEnumerable<GameAction> actions, List<MsfsProfileFile> profiles)
        {
            foreach (GameAction action in actions)
            {
                List<MsfsProfileFile> having = profiles.Where(p => p.Actions.Any(a => string.Equals(a.Name, action.Id, StringComparison.Ordinal))).ToList();
                if (having.Count == 0 || having.Any(p => p.RecordAircraft == null || p.RecordAircraft.Count == 0))
                    continue;
                if (!having.GroupBy(CategorySlotKey, StringComparer.OrdinalIgnoreCase).All(g => g.Count() < g.First().SlotProfiles))
                    continue;
                action.Aircraft = having.SelectMany(p => p.RecordAircraft).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        // The binds of a device and category the sim keeps in several own profiles (one per aircraft or group): once each,
        // for every aircraft (Aircraft empty) when every profile has it, else for the aircraft of the profiles that have it.
        internal static List<GameInputBinding> MergePerAircraft(List<KeyValuePair<GameInputBinding, MsfsProfileFile>> binds)
        {
            List<GameInputBinding> merged = new List<GameInputBinding>();
            foreach (IGrouping<string, KeyValuePair<GameInputBinding, MsfsProfileFile>> same in binds.GroupBy(b => CategorySlotKey(b.Value) + "|" + b.Key.ActionId + "|" + b.Key.Device + "|" + b.Key.Input + "|" +
                                                                                                                   string.Join("+", (b.Key.Reformers ?? new List<string>()).OrderBy(r => r, StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase))
            {
                GameInputBinding binding = same.First().Key;
                List<MsfsProfileFile> profiles = same.Select(b => b.Value).Distinct().ToList();
                binding.Aircraft = profiles.Count >= profiles[0].SlotProfiles
                    ? new List<string>()
                    : profiles.SelectMany(p => p.RecordAircraft).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
                merged.Add(binding);
            }
            return merged;
        }

        // Import and scan of what the sim uses (JoyPro's package, the Steam remote folder, the Store save folder; not a
        // file or folder given with --path, not with --game-profile): connected devices without an own profile run on
        // the preset the sim ships for them (MsfsSimDefaults), and their binds come along.
        private bool UsesSimDefaults(string configPath)
        {
            if (!ImportReading || !string.IsNullOrWhiteSpace(SelectedProfile) || string.IsNullOrWhiteSpace(configPath) || File.Exists(configPath))
                return false;
            string package;
            return MsfsCommunityPackage.TryGetPackageFolder(configPath, out package) || MsfsUserProfiles.WgsFolderOf(configPath) != null ||
                   configPath.IndexOf("\\userdata\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The sim's default preset of every connected device and category without an own profile (the user's profile
        // first, then JoyPro's package preset, then the shipped default the sim's records show it uses).
        private List<MsfsProfileFile> SimDefaultProfiles(List<MsfsProfileFile> own, GameDeviceDirectory devices, string configPath, GameScanResult result)
        {
            if (devices == null || !devices.Devices.Any(d => d.Connected))
                return new List<MsfsProfileFile>();
            List<MsfsSimDefaults.Preset> presets = MsfsSimDefaults.ShippedPresets(sim, new List<string>());
            if (presets.Count == 0)
                return new List<MsfsProfileFile>();
            // The sim's device records (profile_00, Steam and Store) say which preset it applies to a device.
            MsfsSimProfile simProfile = MsfsSimProfile.Load(sim, new[] { MsfsUserProfiles.WgsFolderOf(configPath) });
            if (simProfile.Sources.Count > 0)
                result.Steps.Add("The sim's device records (profile_00) were read from " +
                                 string.Join(" and ", simProfile.Sources.Select(s => MsfsUserProfiles.IsStorePath(s) ? "the Microsoft Store save" : "Steam").Distinct()) +
                                 ": they say which default preset the sim uses for a device.");
            int withPresets;
            List<MsfsProfileFile> defaults = MsfsSimDefaults.ForConnectedDevices(sim, presets, own, devices.Devices, simProfile, IncludeUnknownSimDefaults, result.Steps, out withPresets)
                .Select(a => a.Profile).ToList();
            if (defaults.Count == 0 && own.Count == 0 && withPresets == 0)
                result.Steps.Add("None of the connected devices has a default preset of the sim (" + presets.Count.ToString(CultureInfo.InvariantCulture) +
                                 " shipped presets checked by product ID): the sim shows no binds for them until you bind something in Controls.");
            return defaults;
        }

        // A profile of this sim binds an action of the list in a general context (cameras, views, pause) while the
        // list has it in a flight context.
        private bool MovesToGeneral(GameAction cataloged, MsfsAction action, MsfsProfileFile profile)
        {
            return profile.Is2024 == (sim == 2024) && IsGeneralContext(action.Context) && FlightContexts.Contains(cataloged.Category ?? string.Empty);
        }

        // An MSFS 2024 profile the user saved (Steam, Microsoft Store, an exported file; not a preset) lists every action
        // of its contexts, the unbound ones as empty <Action/>: together they are what the sim's Controls menu offers.
        internal static bool ListsEveryAction(MsfsProfileFile profile)
        {
            return profile != null && profile.Is2024 && !profile.IsPreset && profile.Source != MsfsProfileSourceKind.SimDefault && profile.Source != MsfsProfileSourceKind.Package;
        }

        // Action id -> the managed contexts the listing profiles put it in.
        private Dictionary<string, SortedSet<string>> ListedContexts(IEnumerable<MsfsProfileFile> profiles)
        {
            Dictionary<string, SortedSet<string>> listed = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            if (sim != 2024)
                return listed;
            foreach (MsfsProfileFile profile in profiles.Where(ListsEveryAction))
            {
                foreach (MsfsAction action in profile.Actions.Where(a => IsManagedContext(a.Context) && EventName.IsMatch(a.Name)))
                {
                    SortedSet<string> contexts;
                    if (!listed.TryGetValue(action.Name, out contexts))
                        listed[action.Name] = contexts = new SortedSet<string>(StringComparer.Ordinal);
                    contexts.Add(action.Context);
                }
            }
            return listed;
        }

        // The context a listed action gets in the list: a flight one when the sim lists it in one, else its general one.
        private static string ListedContext(SortedSet<string> contexts)
        {
            return contexts.FirstOrDefault(c => FlightContexts.Contains(c)) ?? contexts.First();
        }

        // The sim's listing is its own word on an action's context: an action the list has in a context the sim does not
        // list it in, while the sim lists it only in general ones, gets the general one (a flight context taken from
        // the other sim's list, a general one guessed by JoyPro). Flight contexts are never moved (an airplane profile
        // does not list the helicopter contexts). Null: no change.
        private static string CorrectedContext(GameAction cataloged, SortedSet<string> contexts)
        {
            if (contexts == null || contexts.Count == 0 || contexts.Contains(cataloged.Category ?? string.Empty) || contexts.Any(c => FlightContexts.Contains(c)))
                return null;
            return contexts.First();
        }

        // Adds the listed actions the list lacks (title from the sim's menu names, else from the event name) and gives
        // known ones the context the sim lists them in. added / moved collect what changed.
        private void AddListedActions(GameCatalog catalog, Dictionary<string, SortedSet<string>> listed, Func<Dictionary<string, string>> titles, List<GameAction> added, List<GameAction> moved,
                                      Dictionary<string, int> flags)
        {
            foreach (KeyValuePair<string, SortedSet<string>> entry in listed.OrderBy(l => l.Key, StringComparer.Ordinal))
            {
                GameAction known = catalog.Find(entry.Key);
                if (known != null)
                {
                    string corrected = CorrectedContext(known, entry.Value);
                    if (corrected != null)
                    {
                        known.Category = corrected;
                        moved.Add(known);
                    }
                    continue;
                }
                string context = ListedContext(entry.Value);
                int flag;
                flags.TryGetValue(entry.Key, out flag);
                string title;
                GameAction action = new GameAction
                {
                    Id = entry.Key,
                    Title = titles().TryGetValue(entry.Key, out title) && title.Length > 0 ? title : MsfsGameData.TitleFromEventName(entry.Key),
                    IsAxis = AxisAction.IsMatch(entry.Key) || (IsGeneralContext(context) && (flag & FlagAxis) != 0),
                    Category = context
                };
                if (action.Title.Length == 0)
                    action.Title = MsfsGameData.RawTitle(entry.Key);
                catalog.Add(action);
                added.Add(action);
            }
        }

        // The flag each listed action has (an unbound axis action keeps its axis flag).
        private static Dictionary<string, int> ListedFlags(IEnumerable<MsfsProfileFile> profiles)
        {
            Dictionary<string, int> flags = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (MsfsProfileFile profile in profiles.Where(ListsEveryAction))
            {
                foreach (MsfsAction action in profile.Actions)
                {
                    int flag;
                    flags[action.Name] = flags.TryGetValue(action.Name, out flag) ? flag | action.Flag : action.Flag;
                }
            }
            return flags;
        }

        // An action name of an input profile (KEY_ events and the sim's own actions such as DRONE_ZOOM_IN).
        private static readonly Regex EventName = new Regex("^[A-Z][A-Z0-9_]*$", RegexOptions.Compiled);

        // The Controls menu names of the sim (read only; empty without a locPak).
        private Dictionary<string, string> MenuTitles(List<string> warnings)
        {
            try
            {
                return MsfsGameData.Titles(MsfsGameData.Roots(sim, new List<string>()), new List<string>());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                JoyPro.Logging.JoyProLog.Debug("MSFS", "No menu names for new actions: " + ex.Message);
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        // MSFS keeps a profile of every device that was ever connected (Store saves, Steam remote files), while the
        // sim lists only the connected ones. Import takes the profiles of connected devices; the others only with
        // --include-disconnected (GUI: "Also import devices that are not connected"). With no device connected at
        // all everything is imported (an import on another PC, devices unplugged).
        private List<MsfsProfileFile> WithoutDisconnectedDevices(List<MsfsProfileFile> profiles, GameDeviceDirectory devices, GameScanResult result)
        {
            if (profiles.Count == 0 || IncludeDisconnectedDevices)
                return profiles;
            if (devices == null || !devices.Devices.Any(d => d.Connected))
            {
                result.Steps.Add("No game device is connected: the MSFS profiles of all devices are imported.");
                return profiles;
            }
            List<MsfsProfileFile> kept = new List<MsfsProfileFile>();
            List<string> skipped = new List<string>();
            foreach (MsfsProfileFile profile in profiles)
            {
                GameDevice connected = devices.FindByInstanceGuid(profile.DeviceGuid);
                if (connected != null && connected.Connected)
                    kept.Add(profile);
                else
                {
                    string name = string.IsNullOrWhiteSpace(profile.DeviceName) ? "{" + profile.DeviceGuid.ToString().ToUpperInvariant() + "}" : profile.DeviceName.Trim();
                    if (!skipped.Contains(name, StringComparer.OrdinalIgnoreCase))
                        skipped.Add(name);
                }
            }
            if (skipped.Count > 0)
                result.Steps.Add("Profiles of " + skipped.Count.ToString(CultureInfo.InvariantCulture) + " device(s) that are not connected were left out (MSFS keeps the profiles of every device it ever saw): " +
                                 string.Join(", ", skipped) + ". To import them too: --include-disconnected (GUI: 'Also import devices that are not connected').");
            return kept;
        }

        // The actions an import found in the user's profiles go into the stored command list (the import names its
        // relations after it, and an export writes them back).
        private void SaveAddedActions(List<GameAction> added, List<GameAction> movedToGeneral, GameScanResult result, List<GameAction> listed = null)
        {
            try
            {
                GameCatalog stored = GameCatalogStore.Load(this, new List<string>());
                int before = stored.Actions.Count;
                foreach (GameAction action in added.Where(a => !stored.Contains(a.Id)))
                    stored.Add(new GameAction { Id = action.Id, Title = action.Title, IsAxis = action.IsAxis, Category = action.Category, Aircraft = new List<string>(action.Aircraft) });
                int boundAdded = stored.Actions.Count - before;
                foreach (GameAction action in (listed ?? new List<GameAction>()).Where(a => !stored.Contains(a.Id)))
                    stored.Add(new GameAction { Id = action.Id, Title = action.Title, IsAxis = action.IsAxis, Category = action.Category, Aircraft = new List<string>(action.Aircraft) });
                int listedAdded = stored.Actions.Count - before - boundAdded;
                int moved = 0;
                foreach (GameAction action in movedToGeneral)
                {
                    GameAction entry = stored.Find(action.Id);
                    if (entry == null || string.Equals(entry.Category, action.Category, StringComparison.Ordinal))
                        continue;
                    entry.Category = action.Category;
                    moved++;
                }
                if (stored.Actions.Count == before && moved == 0)
                    return;
                GameCatalogStore.Save(this, stored, DisplayName + " action catalog, actions added from your profiles " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                if (boundAdded > 0)
                    result.Steps.Add(boundAdded.ToString(CultureInfo.InvariantCulture) + " action(s) of your profiles added to JoyPro's " + DisplayName + " command list (" +
                                     string.Join(", ", added.Select(a => a.Id).Take(6)) + (added.Count > 6 ? ", ..." : string.Empty) + ").");
                if (listedAdded > 0)
                    result.Steps.Add(listedAdded.ToString(CultureInfo.InvariantCulture) + " unbound action(s) your " + DisplayName + " profiles list (the sim's Controls menu) added to JoyPro's command list with their context (" +
                                     string.Join(", ", listed.Select(a => a.Id).Take(6)) + (listed.Count > 6 ? ", ..." : string.Empty) + ").");
                if (moved > 0)
                    result.Steps.Add(moved.ToString(CultureInfo.InvariantCulture) + " action(s) of JoyPro's " + DisplayName + " command list now have the general context your profiles use (" +
                                     string.Join(", ", movedToGeneral.Select(a => a.Id + " -> " + a.Category).Distinct().Take(6)) + ").");
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException)
                    throw;
                result.Warnings.Add("Could not add the new actions to JoyPro's " + DisplayName + " command list: " + ex.Message);
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
            // The sim ignores the <Axes> page of a preset it ships (and of JoyPro's package presets).
            if (line == null && profile.Source != MsfsProfileSourceKind.SimDefault)
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
            // The aircraft keys the relation is limited to (null = every aircraft; GameInputBinding.Aircraft).
            public List<string> Aircraft;
            // Axis binds: the resolved MSFS tuning and the device axis (AxisName of <Axes>).
            public AxisTuning Tuning;
            public string AxisName;
            // The profile's bind (for the "Only add" report).
            public GameInputBinding Binding;
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
                Tuning = isAxis ? binding.Tuning : null, AxisName = isAxis ? AxisNameOf(binding.Input) : null, Binding = binding
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
                // Devices with binds of the general contexts (cameras, views, pause, ...) also get a Transversal preset.
                HashSet<Guid> generalDevices = new HashSet<Guid>();
                foreach (GameInputBinding binding in request.Bindings)
                {
                    GameAction action = request.Catalog.Find(binding.ActionId);
                    string deviceName;
                    Guid deviceGuid;
                    if (action != null && IsGeneralContext(action.Category) && GameDeviceDirectory.TryParseJoyProName(binding.Device ?? string.Empty, out deviceName, out deviceGuid))
                        generalDevices.Add(deviceGuid);
                }
                if (request.DryRun)
                    result.Steps.Add("Dry run: presets would be created in " + package + " for devices without one.");
                else
                {
                    // The package matches its Community folder's encryption (a Store install on another drive keeps
                    // its data encrypted; JoyPro 2.1.1 to 2.2.1 had removed the mark). Never decrypted here.
                    MsfsCommunityPackage.MatchFolderEncryption(package, Path.Combine(JoyProDataFolder("Backups"), "package-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)), result);
                    try
                    {
                        MsfsCommunityPackage.EnsureDevicePresets(package, exportDevices, generalDevices, result);
                    }
                    catch (IOException ex) when (SafeFileIo.IsEncryptionFailure(ex))
                    {
                        result.Errors.Add(SafeFileIo.Explain(ex, package).Message);
                        return result;
                    }
                }
            }
            List<MsfsProfileFile> profiles = LoadSelectedProfiles(request.ConfigPath, result.Errors, result.Warnings);
            if (result.Errors.Count > 0)
            {
                // No preset in the package because none of the exported devices is connected (the sim matches a preset
                // by the device's product ID): the import files still carry the binds (they start from the user's own
                // profiles), so the export is no failure when they were written.
                if (isPackage && profiles.Count == 0 && result.Errors.Count == 1 && result.Errors[0].Contains("has no presets yet"))
                {
                    List<string> packageErrors = new List<string>(result.Errors);
                    result.Errors.Clear();
                    HashSet<string> notWritten = WriteUserProfilesOf2024(request, result);
                    if (request.DryRun)
                        return result;
                    bool direct = result.ProfileUpdates.Any(u => u.Status == "updated" || u.Status == "unchanged");
                    if (WriteSimImportFiles(request, result, AlwaysWriteImportFiles ? null : notWritten) > 0 || direct)
                        result.Warnings.Add("JoyPro's MSFS 2024 package got no presets: none of the exported devices is connected (the sim matches a preset by the device's product ID). " +
                                            (direct ? "Your own MSFS profiles have the binds." : "The import files carry the binds."));
                    else
                        result.Errors.AddRange(packageErrors);
                }
                return result;
            }

            Dictionary<Guid, Dictionary<string, List<MsfsWanted>>> wantedByDevice = new Dictionary<Guid, Dictionary<string, List<MsfsWanted>>>();
            foreach (GameInputBinding binding in request.Bindings)
            {
                // Limited to some aircraft: only the direct write into the sim's own profiles of those aircraft (the
                // preset applies to every aircraft). Another target (a profile file or folder) does not get it.
                if (IsLimited(binding))
                {
                    if (!isPackage || !WriteUserProfiles)
                    {
                        result.BindingsSkipped++;
                        string warning = "Relation '" + binding.RelationName + "' is limited to " + DisplayName + " aircraft " + string.Join(", ", binding.Aircraft) +
                                         " and was not exported. MSFS 2024 input presets from JoyPro apply to every aircraft: only the export to the Community2024 folder writes relations " +
                                         "per aircraft (into your own profiles of those aircraft); else export with --aircraft <key> to write the relations limited to one aircraft (for all aircraft).";
                        if (!result.Warnings.Contains(warning))
                            result.Warnings.Add(warning);
                    }
                    continue;
                }
                GameAction action = request.Catalog.Find(binding.ActionId);
                Guid guid = Guid.Empty;
                MsfsWanted wanted = null;
                string reason = action == null ? "not in the " + DisplayName + " catalog." : Encode(binding, action, out guid, out wanted);
                if (reason.Length == 0 && !profiles.Any(p => p.DeviceGuid == guid))
                    reason = isPackage
                        ? "JoyPro's MSFS 2024 package has no preset of device '" + binding.Device + "' (the device must be connected once for it); your own MSFS profile or the import file still gets the bind (see below)."
                        : "no MSFS profile of device '" + binding.Device + "' in " + request.ConfigPath + " (create one in the sim first).";
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
            // MSFS 2024 presets: an action goes into the context the user's own profiles list it in (they list every
            // action of their contexts; KEY_ACTIVE_PAUSE_TOGGLE is INGAME_UI there, a General bind, while the shared
            // command list may have it in PLANE from MSFS 2020).
            if (isPackage && sim == 2024)
            {
                foreach (KeyValuePair<string, SortedSet<string>> listed in ListedContexts(OwnProfiles2024()))
                {
                    if (!learnedContexts.ContainsKey(listed.Key))
                        learnedContexts[listed.Key] = ListedContext(listed.Value);
                }
            }
            // "<device guid>|<action id>" of every wanted action a profile took (an action goes to one profile of its
            // device: MSFS 2024 keeps a profile per category).
            HashSet<string> placed = new HashSet<string>(StringComparer.Ordinal);
            // "My profile wins": the actions the export binds (every aircraft; this target has no per-aircraft profiles).
            HashSet<string> profileActions = new HashSet<string>(wantedByDevice.Values.SelectMany(d => d.Keys), StringComparer.Ordinal);
            int changedFiles = 0;
            foreach (MsfsProfileFile profile in profiles)
            {
                Dictionary<string, List<MsfsWanted>> wanted;
                if (!wantedByDevice.TryGetValue(profile.DeviceGuid, out wanted))
                    wanted = new Dictionary<string, List<MsfsWanted>>(StringComparer.Ordinal);
                string ownershipKey = OwnershipKey(profile.FilePath);
                Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(ownershipKey, result.Warnings);
                ApplyToProfile(profile, request, wanted, profileActions, owned, learnedContexts, placed, result);
                if (request.DryRun)
                    continue;

                GameOwnershipStore.Save(ownershipKey, GameId, owned);
                if (profile.ChangedActions == 0 && profile.ChangedAxes == 0)
                    continue;
                try
                {
                    BackUp(profile.FilePath, result);
                    profile.Save(profile.FilePath);
                    JoyPro.Logging.JoyProLog.Debug("MSFS", "Export wrote " + (profile.IsPreset ? "JoyPro package preset" : MsfsUserProfiles.IsStorePath(profile.FilePath) ? "Store profile" : "profile file") + " " + profile.FilePath +
                        " (" + profile.DeviceName + " {" + profile.DeviceGuid.ToString().ToUpperInvariant() + "}, " + (profile.CategoryKey.Length == 0 ? "no category" : profile.CategoryKey) + "): " +
                        profile.ChangedActions.ToString(CultureInfo.InvariantCulture) + " action(s) changed (" + string.Join(", ", profile.Actions.Where(a => a.Changed).Select(a => a.Name + "=" + (a.Combos.Count == 0 ? "-" : string.Join("+", a.Combos.Select(c => c.Signature)))).Take(40)) +
                        "), " + profile.ChangedAxes.ToString(CultureInfo.InvariantCulture) + " device axis setting(s)");
                }
                catch (IOException ex) when (SafeFileIo.IsEncryptionFailure(ex))
                {
                    result.Errors.Add((ex is EncryptionTargetException ? ex : SafeFileIo.Explain(ex, profile.FilePath)).Message);
                    return result;
                }
                result.FilesWritten.Add(profile.FilePath);
                changedFiles++;
            }

            // Wanted actions no profile of their device could take (a 2024 profile of another category, no general
            // preset because the device is not connected, ...): reported once, not counted as written.
            foreach (KeyValuePair<Guid, Dictionary<string, List<MsfsWanted>>> device in wantedByDevice)
            {
                foreach (KeyValuePair<string, List<MsfsWanted>> entry in device.Value.Where(e => !placed.Contains(device.Key.ToString("N") + "|" + e.Key)))
                {
                    GameAction action = request.Catalog.Find(entry.Key);
                    bool general = action != null && IsGeneralContext(action.Category);
                    if (isPackage && request.DryRun)
                    {
                        result.Steps.Add("Dry run: " + entry.Key + " would go into a new " + (general ? "general (transversal)" : "airplane") + " preset of the device.");
                        continue;
                    }
                    List<string> files = profiles.Where(p => p.DeviceGuid == device.Key).Select(p => Path.GetFileName(p.FilePath)).ToList();
                    result.Warnings.Add("Relation '" + entry.Value[0].RelationName + "' (" + entry.Key + "): no MSFS profile of the device holds " +
                                        (general ? "the general binds (" + action.Category + "; MSFS 2024: the Transversal category)" : "this action") +
                                        " (" + string.Join(", ", files) + "); not written. " +
                                        (isPackage ? "Connect the device and export again." : "Bind any action of that category to the device in the sim once, then export again."));
                    result.BindingsWritten -= entry.Value.Count;
                    result.BindingsSkipped += entry.Value.Count;
                }
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
            {
                try
                {
                    MsfsCommunityPackage.WriteLayoutAndManifest(package, result);
                }
                catch (IOException ex) when (SafeFileIo.IsEncryptionFailure(ex))
                {
                    result.Errors.Add(SafeFileIo.Explain(ex, Path.Combine(package, "layout.json")).Message);
                }
            }
            // The package reaches only aircraft the sim has not set up for a device yet: the binds go into the profile
            // the sim uses for each device and category (MsfsIntegration.UserProfiles.cs), and where that cannot be
            // done into files in the sim's own format, to import in Controls (MsfsIntegration.ImportFiles.cs).
            if (isPackage && result.Errors.Count == 0)
            {
                HashSet<string> notWritten = WriteUserProfilesOf2024(request, result);
                if (!request.DryRun)
                    WriteSimImportFiles(request, result, AlwaysWriteImportFiles ? null : notWritten);
            }
            return result;
        }

        // "My profile wins": the actions the export binds on any device for a profile the given aircraft use (null: a
        // profile of every aircraft; binds limited to some aircraft count only for profiles of those aircraft).
        private static HashSet<string> ProfileActions(Dictionary<Guid, Dictionary<string, List<MsfsWanted>>> wantedByDevice, ICollection<string> aircraft)
        {
            HashSet<string> actions = new HashSet<string>(StringComparer.Ordinal);
            foreach (Dictionary<string, List<MsfsWanted>> device in wantedByDevice.Values)
            {
                foreach (KeyValuePair<string, List<MsfsWanted>> entry in device)
                {
                    if (entry.Value.Any(w => w.Aircraft == null || (aircraft != null && aircraft.Count > 0 && aircraft.All(a => w.Aircraft.Contains(a, StringComparer.OrdinalIgnoreCase)))))
                        actions.Add(entry.Key);
                }
            }
            return actions;
        }

        // JoyPro clears binds only in profiles of connected devices (the import leaves the others out).
        private static bool OwnsDevice(GameExportRequest request, Guid device)
        {
            GameDevice connected = request.Devices == null ? null : request.Devices.FindByInstanceGuid(device);
            return connected != null && connected.Connected;
        }

        private ExportSkippedBind SkippedMultiBind(MsfsProfileFile profile, MsfsWanted wanted, string gameAction)
        {
            List<string> infos = wanted.Combo.Infos.ToList();
            string input = infos.Count > 0 ? infos[infos.Count - 1] : string.Empty;
            GameInputBinding binding = wanted.Binding;
            return new ExportSkippedBind
            {
                Game = GameId,
                Aircraft = profile.RecordAircraft != null ? string.Join(", ", profile.RecordAircraft) : string.Empty,
                Device = binding != null ? binding.Device ?? string.Empty : GameDeviceDirectory.BuildJoyProName(profile.DeviceName, profile.DeviceGuid),
                Input = binding != null ? binding.Input ?? string.Empty : input,
                ModifiersText = binding == null || binding.Reformers == null ? string.Empty : string.Join(" + ", binding.Reformers.Select(r => { string[] p = (r ?? string.Empty).Split('§'); return p.Length >= 3 ? p[1] + " " + p[2] : r; })),
                Relation = wanted.RelationName ?? string.Empty,
                Action = wanted.ActionId ?? string.Empty,
                GameAction = gameAction ?? string.Empty
            };
        }

        // profileActions: the actions the export binds for this profile's aircraft on any device ("my profile wins"
        // takes their other inputs away).
        private void ApplyToProfile(MsfsProfileFile profile, GameExportRequest request, Dictionary<string, List<MsfsWanted>> wanted, HashSet<string> profileActions, Dictionary<string, HashSet<string>> owned,
            Dictionary<string, string> learnedContexts, HashSet<string> placed, GameIoResult result)
        {
            Dictionary<string, HashSet<string>> ownedBefore = owned.ToDictionary(e => e.Key, e => new HashSet<string>(e.Value), StringComparer.OrdinalIgnoreCase);
            bool replaceOwned = request.Mode == GameExportMode.Clean || request.Mode == GameExportMode.MergeOverwrite;
            if (replaceOwned)
                owned.Clear();
            bool owns = OwnsDevice(request, profile.DeviceGuid);
            // MSFS is flight only, so JoyPro takes over whole devices: clean clears every other flight bind in the
            // profiles of the devices it writes, nuke clears the profiles of devices the export does not use. Only
            // connected devices; the menu, world map and developer contexts stay (the import leaves them out).
            bool clean = owns && (wanted.Count > 0 ? request.Mode == GameExportMode.Clean : request.NukeUnusedDevices);
            List<MsfsDeviceAxisRequest> deviceRequests = new List<MsfsDeviceAxisRequest>();

            string placedPrefix = profile.DeviceGuid.ToString("N") + "|";
            foreach (string id in wanted.Keys.Where(id => profile.Find(id) != null && !IsManagedContext(profile.Find(id).Context)))
            {
                result.Warnings.Add(id + " sits in the " + profile.Find(id).Context + " context of '" + Path.GetFileName(profile.FilePath) + "'; JoyPro leaves the sim's menu, world map and developer contexts alone.");
                placed.Add(placedPrefix + id);
            }

            // Actions the profile does not list yet are added first (empty), so the conflicts below see every action
            // of the profile.
            foreach (KeyValuePair<string, List<MsfsWanted>> entry in wanted.Where(e => profile.Find(e.Key) == null).ToList())
            {
                GameAction catalogedNew = request.Catalog.Find(entry.Key);
                // The sim's "General" binds (cameras, views, pause, ...) keep their own context.
                bool general = IsGeneralContext(catalogedNew.Category);
                if (profile.IsPreset)
                {
                    // MSFS 2024 package: general binds go into the device's Transversal preset, flight binds into its
                    // airplane preset; the context the user's own profiles list the action in wins.
                    string listedContext;
                    bool listed = learnedContexts.TryGetValue(entry.Key, out listedContext) && IsManagedContext(listedContext);
                    if (listed)
                        general = IsGeneralContext(listedContext);
                    if (general != profile.IsGeneralCategory)
                        continue;
                    string presetContext;
                    if (listed)
                        presetContext = listedContext;
                    else if (general || FlightContexts.Contains(catalogedNew.Category ?? string.Empty))
                        presetContext = catalogedNew.Category;
                    else
                        presetContext = "PLANE";
                    MsfsAction addedPreset = profile.AddAction(presetContext, entry.Key, catalogedNew.IsAxis ? FlagAxis : FlagDigital);
                    addedPreset.Attributes += " ValueEvent=\"0\"";
                    continue;
                }
                // A 2024 profile lists every action of its category; one that is missing belongs to another category
                // (another profile of the device takes it, else Write reports it).
                if (profile.Is2024)
                    continue;
                // 2020: the context the action has in the other profiles of the folder, else its own (general binds),
                // else PLANE.
                string context;
                if (!learnedContexts.TryGetValue(entry.Key, out context))
                {
                    if (general)
                        context = catalogedNew.Category;
                    else
                    {
                        context = "PLANE";
                        result.Warnings.Add(entry.Key + " is new in '" + Path.GetFileName(profile.FilePath) + "'; its MSFS 2020 context is unknown, so it goes into PLANE.");
                    }
                }
                profile.AddAction(context, entry.Key, catalogedNew.IsAxis ? FlagAxis : FlagDigital);
            }

            // The inputs (combination signatures) the profile's actions get in this file, and which action of the
            // sim's flight contexts holds each input now.
            Dictionary<string, HashSet<string>> wantedOn = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            Dictionary<string, string> heldBy = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (MsfsAction action in profile.Actions.Where(a => IsManagedContext(a.Context)))
            {
                List<MsfsWanted> list;
                if (wanted.TryGetValue(action.Name, out list))
                {
                    foreach (MsfsWanted item in list)
                    {
                        HashSet<string> names;
                        if (!wantedOn.TryGetValue(item.Combo.Signature, out names))
                            wantedOn[item.Combo.Signature] = names = new HashSet<string>(StringComparer.Ordinal);
                        names.Add(action.Name);
                    }
                }
                foreach (MsfsCombo combo in action.Combos.Where(c => !heldBy.ContainsKey(c.Signature)))
                    heldBy[combo.Signature] = action.Name;
            }
            // Merge and "my profile wins": a combination the profile gives another action is a conflict and goes.
            Func<MsfsAction, MsfsCombo, bool> conflict = (action, combo) =>
            {
                HashSet<string> names;
                return request.Mode != GameExportMode.Add && wantedOn.TryGetValue(combo.Signature, out names) && !names.Contains(action.Name);
            };
            // "Only add": the action of the sim that holds an input now (null: free, or the action itself).
            Func<MsfsAction, MsfsCombo, string> takenBy = (action, combo) =>
            {
                string holder;
                return heldBy.TryGetValue(combo.Signature, out holder) && holder != action.Name && !action.Combos.Any(c => c.Signature == combo.Signature) ? holder : null;
            };

            foreach (MsfsAction action in profile.Actions.ToList())
            {
                GameAction cataloged = request.Catalog.Find(action.Name);
                if (!IsManagedContext(action.Context))
                    continue;
                if (cataloged == null)
                {
                    List<MsfsCombo> kept = clean ? new List<MsfsCombo>() : action.Combos.Where(c => !conflict(action, c)).ToList();
                    if (kept.Count != action.Combos.Count)
                    {
                        action.Combos = kept;
                        action.Changed = true;
                    }
                    continue;
                }
                List<MsfsWanted> wantedForAction;
                if (wanted.TryGetValue(action.Name, out wantedForAction))
                    placed.Add(placedPrefix + action.Name);
                bool overwrite = owns && replaceOwned && profileActions != null && profileActions.Contains(action.Name);
                UpdateAction(profile, action, cataloged, wantedForAction ?? new List<MsfsWanted>(), request.Mode, clean, overwrite, conflict, takenBy, ownedBefore, owned, deviceRequests, result);
            }

            ApplyDeviceAxes(profile, request.Mode, deviceRequests, ownedBefore, owned, result);
        }

        // clean: every combination the profile does not want goes; overwrite: the action is bound by the export ("my
        // profile wins": its other inputs go); conflict: a combination another action of the profile gets.
        private void UpdateAction(MsfsProfileFile profile, MsfsAction action, GameAction cataloged, List<MsfsWanted> wanted, GameExportMode mode, bool clean, bool overwrite,
            Func<MsfsAction, MsfsCombo, bool> conflict, Func<MsfsAction, MsfsCombo, string> takenBy, Dictionary<string, HashSet<string>> ownedBefore, Dictionary<string, HashSet<string>> owned,
            List<MsfsDeviceAxisRequest> deviceRequests, GameIoResult result)
        {
            HashSet<string> wantedSignatures = new HashSet<string>(wanted.Select(w => w.Combo.Signature), StringComparer.Ordinal);
            List<MsfsCombo> combos = action.Combos.Where(c => wantedSignatures.Contains(c.Signature) || !(clean || overwrite || conflict(action, c))).ToList();
            // "Only add" never changes a bind that is there: only new combinations get the profile's axis settings.
            HashSet<string> added = new HashSet<string>(StringComparer.Ordinal);
            foreach (MsfsWanted item in wanted)
            {
                if (combos.Any(c => c.Signature == item.Combo.Signature))
                    continue;
                if (mode == GameExportMode.Add)
                {
                    // Another action of the sim on that input: not written (no multi-bind), reported.
                    string holder = takenBy(action, item.Combo);
                    if (holder != null)
                    {
                        result.BindingsSkipped++;
                        result.SkippedTakenInputs.Add(SkippedMultiBind(profile, item, holder));
                        continue;
                    }
                }
                if (combos.Count >= 2)
                {
                    result.Warnings.Add("Relation '" + item.RelationName + "' (" + action.Name + "): both MSFS input slots of '" + Path.GetFileName(profile.FilePath) + "' are taken; not written.");
                    continue;
                }
                combos.Add(item.Combo);
                added.Add(item.Combo.Signature);
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
            MsfsWanted axis = wanted.FirstOrDefault(w => w.IsAxis && combos.Any(c => c.Signature == w.Combo.Signature) && (mode != GameExportMode.Add || added.Contains(w.Combo.Signature)));
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
                if (changes && line != null && (mode == GameExportMode.Merge || mode == GameExportMode.Add) && !IsDefaultPage(existing) && !GameOwnershipStore.Contains(ownedBefore, ownerKey, AxisToken(existing)))
                {
                    result.Warnings.Add("Device '" + profile.DeviceName + "' axis " + axisName + " has Sensitivity settings made in the game; " + (mode == GameExportMode.Add ? "add" : "merge") + " keeps them.");
                    continue;
                }
                GameOwnershipStore.Add(owned, ownerKey, AxisToken(after));
                if (!changes)
                    continue;
                profile.SetDeviceAxis(axisName, values);
                result.Steps.Add("Sensitivity of '" + profile.DeviceName + "' axis " + axisName + " -> " + Path.GetFileName(profile.FilePath) + ": " + AxisToken(after) +
                                 " (sensitivity +, -, neutral, dead zone, extremity dead zone, reactivity).");
                int code = AxisCodes[Array.IndexOf(MsfsProfileFile.AxisNames, axisName)];
                // Other actions on the same axis whose relations did not ask for the changed values (a camera zoom on
                // the throttle axis, ...): the page applies to them too.
                HashSet<string> changers = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < MsfsAxisModel.PageKeys.Length; i++)
                {
                    string attribute = MsfsAxisModel.PageAttributes[i];
                    int before;
                    if (chosenFrom[i] != null && values.Any(v => v.Key == attribute && !(existing.TryGetValue(attribute, out before) && before == v.Value)))
                        changers.Add(chosenFrom[i]);
                }
                HashSet<string> requested = new HashSet<string>(list.Where(r => changers.Contains(r.Wanted.RelationName)).Select(r => r.ActionName), StringComparer.Ordinal);
                List<string> others = profile.Actions.Where(a => !requested.Contains(a.Name) && a.Combos.Any(c => c.Codes.Contains(code)))
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
                foreach (MsfsAction action in profile.Actions.Where(a => IsManagedContext(a.Context)))
                    if (!contexts.ContainsKey(action.Name))
                        contexts[action.Name] = action.Context;
            return contexts;
        }

        // <data>\<kind>\MSFS (JOYPRO_DATA_DIR, used by the smoke test, else %LOCALAPPDATA%\JoyPro2).
        private static string JoyProDataFolder(string kind)
        {
            string folder = Path.Combine(global::JoyPro.Logging.JoyProDataFolder.Root(), kind, "MSFS");
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
            SafeFileIo.CopyFile(profilePath, backup, true, ownTarget: true);
            GameBackups.Protect(profilePath, result.Warnings);
            result.Steps.Add("Backup: " + backup);
        }

        public void RecordImport(string configPath, List<GameInputBinding> applied, GameDeviceDirectory devices, GameIoResult result)
        {
            List<MsfsProfileFile> profiles = LoadSelectedProfiles(configPath, result.Errors, new List<string>(), ImportReading);
            GameCatalog catalog = GameCatalogStore.Load(this, result.Warnings);
            // The imported binds are JoyPro's in that profile (an export may replace them later). Store profiles get a
            // key by save and container: their blob gets a new file name on every save.
            foreach (MsfsProfileFile profile in profiles.Where(p => p.Source != MsfsProfileSourceKind.Store || !string.IsNullOrEmpty(p.StoreContainer)))
            {
                string ownershipKey = OwnershipKey(StableProfilePath(profile));
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

        // Add-on aircraft with their own controls (MsfsGameData.AddonPresetPackages): the actions their presets bind or
        // list that the list lacks join it with their context, for the aircraft of the package (an action several
        // add-ons have is for all of them; one of a package that is no aircraft for every aircraft).
        private void AddAddonActions(GameCatalog catalog, List<string> roots, GameIoResult result)
        {
            List<string> encrypted = new List<string>();
            List<MsfsGameData.AddonPresets> packages = MsfsGameData.AddonPresetPackages(sim, roots, encrypted);
            int added = 0;
            List<string> withActions = new List<string>();
            foreach (MsfsGameData.AddonPresets package in packages)
            {
                int before = added;
                foreach (string file in package.Files)
                {
                    EngineProgress.ThrowIfCancelled("reading the add-on input presets");
                    MsfsProfileFile profile;
                    if (!MsfsProfileFile.TryLoad(file, new List<string>(), out profile))
                        continue;
                    foreach (MsfsAction action in profile.Actions.Where(a => IsManagedContext(a.Context) && EventName.IsMatch(a.Name)))
                    {
                        GameAction known = catalog.Find(action.Name);
                        if (known != null)
                        {
                            // An add-on action an earlier package (or refresh) had: this aircraft too; the sim's own stay general.
                            if (known.Aircraft.Count > 0 && package.AircraftKey.Length > 0 && !known.Aircraft.Contains(package.AircraftKey, StringComparer.OrdinalIgnoreCase))
                                known.Aircraft.Add(package.AircraftKey);
                            else if (known.Aircraft.Count > 0 && package.AircraftKey.Length == 0)
                                known.Aircraft.Clear();
                            continue;
                        }
                        catalog.Add(new GameAction
                        {
                            Id = action.Name,
                            Title = MsfsGameData.TitleFromEventName(action.Name),
                            IsAxis = AxisAction.IsMatch(action.Name) || (action.Flag & FlagAxis) != 0,
                            Category = action.Context,
                            Aircraft = package.AircraftKey.Length > 0 ? new List<string> { package.AircraftKey } : new List<string>()
                        });
                        added++;
                    }
                }
                if (added > before)
                    withActions.Add(package.Package);
            }
            if (packages.Count > 0 || encrypted.Count > 0)
                result.Steps.Add("Add-on input presets (Community and Marketplace packages): " + packages.Count.ToString(CultureInfo.InvariantCulture) + " package(s) read, " +
                                 added.ToString(CultureInfo.InvariantCulture) + " new action(s)" + (withActions.Count > 0 ? " (" + string.Join(", ", withActions.Take(6)) + (withActions.Count > 6 ? ", ..." : string.Empty) + ")" : string.Empty) +
                                 (encrypted.Count > 0 ? "; " + encrypted.Count.ToString(CultureInfo.InvariantCulture) + " packed package(s) skipped (.fsarchive, not readable: " +
                                                        string.Join(", ", encrypted.Take(6)) + (encrypted.Count > 6 ? ", ..." : string.Empty) + ")" : string.Empty) + ".");
        }

        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            List<string> roots = MsfsGameData.Roots(sim, result.Warnings);
            List<MsfsProfileFile> profiles;
            bool fromSim = string.IsNullOrWhiteSpace(sourcePath);
            if (fromSim)
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
                // JoyPro's own presets (Community2024 package): the actions an earlier export wrote.
                if (sim == 2024 && MsfsGameData.UserProfilesAllowed())
                {
                    string community = MsfsCommunityPackage.FindCommunityFolder();
                    string package = string.IsNullOrEmpty(community) ? null : Path.Combine(community, MsfsCommunityPackage.PackageName);
                    if (package != null && Directory.Exists(package))
                        files.AddRange(MsfsCommunityPackage.PresetFiles(package));
                }
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
                    catalog.Add(new GameAction { Id = action.Id, Title = action.Title, IsAxis = action.IsAxis, Category = action.Category, NativeId = action.NativeId, Aircraft = new List<string>(action.Aircraft) });
            }
            foreach (MsfsProfileFile profile in profiles)
            {
                // Every action of a managed context: the flight ones and the general ones (cameras, views, pause, ...;
                // the sim's Transversal presets), each with its context. Menus, world map and developer tools stay out.
                foreach (MsfsAction action in profile.Actions.Where(a => IsManagedContext(a.Context) && EventName.IsMatch(a.Name)))
                {
                    GameAction known = catalog.Find(action.Name);
                    if (known != null)
                    {
                        if (MovesToGeneral(known, action, profile))
                            known.Category = action.Context;
                        continue;
                    }
                    string title = (action.Name.StartsWith("KEY_", StringComparison.Ordinal) ? action.Name.Substring(4) : action.Name).Replace('_', ' ');
                    catalog.Add(new GameAction { Id = action.Name, Title = title, IsAxis = AxisAction.IsMatch(action.Name) || (IsGeneralContext(action.Context) && (action.Flag & FlagAxis) != 0), Category = action.Context });
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
            if (fromSim)
                AddAddonActions(catalog, roots, result);
            // The contexts the user's MSFS 2024 profiles list the actions in (every action of their contexts) are the
            // sim's word: a context the list has from elsewhere (the other sim, a guess) is corrected.
            Dictionary<string, SortedSet<string>> listed = ListedContexts(profiles);
            List<GameAction> corrected = new List<GameAction>();
            AddListedActions(catalog, listed, () => titles, new List<GameAction>(), corrected, ListedFlags(profiles));
            if (corrected.Count > 0)
                result.Steps.Add("Actions moved to the context your profiles list them in: " + corrected.Count.ToString(CultureInfo.InvariantCulture));
            // General events (cameras, views, UI, ...) only the locPak names: a full General profile of the user lists
            // every general action the sim offers, so with one read they stay out (the sim's menu does not offer them);
            // without one they join with the context of the known actions of the same name group, when that is clear
            // (MsfsGameData.GuessGeneralContext), else they stay out.
            bool fullGeneralListing = profiles.Any(p => ListsEveryAction(p) && (p.CategoryKey.Length == 0 || p.IsGeneralCategory) &&p.Actions.Any(a => IsGeneralContext(a.Context)));
            if (sim == 2024 && titles.Count > 0 && !fullGeneralListing)
            {
                List<KeyValuePair<string, string>> general = catalog.Actions.Where(a => IsGeneralContext(a.Category)).Select(a => new KeyValuePair<string, string>(a.Id, a.Category)).ToList();
                int guessed = 0;
                foreach (KeyValuePair<string, string> named1 in titles.OrderBy(t => t.Key, StringComparer.Ordinal))
                {
                    if (catalog.Contains(named1.Key) || MsfsGameData.IsFlightEvent(named1.Key) || !EventName.IsMatch(named1.Key))
                        continue;
                    string context = MsfsGameData.GuessGeneralContext(named1.Key, general);
                    if (context == null || !IsGeneralContext(context))
                        continue;
                    catalog.Add(new GameAction { Id = named1.Key, Title = MsfsGameData.RawTitle(named1.Key), IsAxis = AxisAction.IsMatch(named1.Key), Category = context });
                    guessed++;
                }
                result.Steps.Add("General events the sim names but neither a preset nor a profile of yours lists: " + guessed.ToString(CultureInfo.InvariantCulture) +
                                 " new (context from the actions of the same name group)");
            }
            int named = 0;
            int fromEvent = 0;
            foreach (GameAction action in catalog.Actions.Where(a => current == null || !current.Contains(a.Id) || a.Title == MsfsGameData.RawTitle(a.Id) || a.Title == MsfsGameData.TitleFromEventName(a.Id)))
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
