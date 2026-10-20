using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace JoyPro.Engine.Games
{
    // MSFS 2024: JoyPro's binds go straight into the profile the sim uses for each device and category (docs/games/msfs.md
    // "Writing your own MSFS 2024 profiles"). The sim's device records (profile_00, read only) name, per device and
    // aircraft, the profile of each slot (general = Transversal, category = Airplanes): an own profile is
    // "inputprofile_<n>", the file name in the Steam remote folder and the container name in the Microsoft Store save.
    // That profile is edited like a 2020 profile (MsfsProfileFile, ownership: JoyPro replaces only its own binds), the
    // user's other binds stay. Safety: the sim (and its helper processes) must be closed, every file is backed up
    // (GameBackups, "engine backups restore"), the Store save is written in the save tools' order (blob, container file,
    // index last, WgsWriter), read back and checked, and rolled back when the check fails. Anything unexpected writes
    // nothing for that device and category: its binds go into an import file instead, with the reason.
    internal sealed partial class MsfsIntegration : IGameUserProfileWrite, IGameConfigPathResolver
    {
        // MSFS 2024: a Community2024 folder given as the path means JoyPro's package in it. One config path per target
        // for every command: an export to the Community folder and an import (the package) kept two watcher records,
        // and the diff compared with the older one (its baseline lacked the binds of the last export).
        public string ResolveConfigPath(string path)
        {
            string package;
            return sim == 2024 && MsfsCommunityPackage.TryGetPackageFolder(path, out package) ? package : path;
        }

        // Export: write into the user's own active profiles (default on; --user-profiles false turns it off).
        public bool WriteUserProfiles { get; set; } = true;

        // Export: import files for every device and category, not only where the direct write could not update.
        public bool AlwaysWriteImportFiles { get; set; }

        // Processes that keep the sim's save open or write it back on exit: the sim itself; the Microsoft Store build
        // starts through the GDK launch helper (an exe path inside the sim's package or install).
        private static readonly string[] SimHelperNames = { "gamelaunchhelper", "GameLaunchHelper" };

        // One profile JoyPro writes (or would write): a device and category of one source.
        private sealed class UserProfileTarget
        {
            public Guid Device;
            public bool General;
            public string DeviceName = string.Empty;
            public MsfsProfileSourceKind Kind;
            // Steam: the remote folder; Store: the user folder of the save (<wgs>\<user>_<title>).
            public string Folder = string.Empty;
            // inputprofile_<n> (file name / container name, as the records write it).
            public string ProfileName = string.Empty;
            public string FilePath = string.Empty;
            public MsfsProfileFile Profile;
            public int RecordsNaming;
            public int Records;
            // The aircraft whose records name this profile (keys as the catalog has them, MsfsAircraftCatalog.KeyOf).
            public List<string> Aircraft = new List<string>();
            public string AircraftText = string.Empty;
            public Dictionary<string, List<MsfsWanted>> Wanted = new Dictionary<string, List<MsfsWanted>>(StringComparer.Ordinal);
            public GameIoResult Scratch = new GameIoResult();
            public byte[] NewBytes;
            public string Status = string.Empty;
            public string Reason = string.Empty;
            public List<string> NotWritten = new List<string>();
        }

        // The device's records (one source) say which own profile the sim uses for a category: the one most records
        // name (Airplanes: the category slot of records with category AIRPLANE; General: the general slot of every
        // record). Null when no record names an own profile.
        internal static string ActiveOwnProfile(MsfsSimProfile records, Guid device, bool general, out int naming, out int total, out List<string> others)
        {
            naming = 0;
            others = new List<string>();
            List<MsfsSimProfile.Record> list = records.RecordsOf(device)
                .Where(r => general || string.IsNullOrEmpty(r.Category) || string.Equals(r.Category, "AIRPLANE", StringComparison.OrdinalIgnoreCase)).ToList();
            total = list.Count;
            List<IGrouping<string, string>> votes = list.Select(r => general ? r.General : r.CategoryPreset).Where(IsOwnProfile)
                .GroupBy(p => p, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            if (votes.Count == 0)
                return null;
            naming = votes[0].Count();
            others = votes.Skip(1).Select(g => g.Key).ToList();
            return votes[0].Key;
        }

        // Every own profile the device's records name for a category (as ActiveOwnProfile counts them), each with the
        // aircraft of its records (catalog keys; "" for a record without aircraft), the most used first.
        internal static List<KeyValuePair<string, List<string>>> OwnProfilesByAircraft(MsfsSimProfile records, Guid device, bool general, out int total)
        {
            List<MsfsSimProfile.Record> list = records.RecordsOf(device)
                .Where(r => general || string.IsNullOrEmpty(r.Category) || string.Equals(r.Category, "AIRPLANE", StringComparison.OrdinalIgnoreCase)).ToList();
            total = list.Count;
            return list.Where(r => IsOwnProfile(general ? r.General : r.CategoryPreset))
                .GroupBy(r => general ? r.General : r.CategoryPreset, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new KeyValuePair<string, List<string>>(g.Key, g.Select(r => MsfsAircraftCatalog.KeyOf(r.Aircraft)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
                .ToList();
        }

        // A bind goes into a profile when it is for every aircraft, or ticked for every aircraft that profile is used by.
        private static bool FitsProfile(MsfsWanted wanted, UserProfileTarget target)
        {
            return wanted.Aircraft == null || (target.Aircraft.Count > 0 && target.Aircraft.All(a => wanted.Aircraft.Contains(a, StringComparer.OrdinalIgnoreCase)));
        }

        // The aircraft as the user knows them: the catalog's titles, the key for an aircraft not in the list.
        private static string AircraftNames(IEnumerable<string> keys, Dictionary<string, string> titles, int max = 6)
        {
            List<string> names = keys.Select(k => k.Length == 0 ? "a record without aircraft" : titles.ContainsKey(k) && titles[k].Length > 0 ? titles[k] : k).ToList();
            return names.Count <= max ? string.Join(", ", names)
                : string.Join(", ", names.Take(max)) + " and " + (names.Count - max).ToString(CultureInfo.InvariantCulture) + " more";
        }

        // The context the sim lists an action in for this device (its own profiles list every action of their
        // contexts): General when only a general context lists it, Airplanes when a flight one does, else the catalog's.
        private static bool GoesToGeneral(string actionId, GameAction cataloged, MsfsProfileFile generalProfile, MsfsProfileFile airplaneProfile)
        {
            MsfsAction inAirplane = airplaneProfile == null ? null : airplaneProfile.Find(actionId);
            MsfsAction inGeneral = generalProfile == null ? null : generalProfile.Find(actionId);
            if (inAirplane != null && FlightContexts.Contains(inAirplane.Context ?? string.Empty))
                return false;
            if (inGeneral != null && IsManagedContext(inGeneral.Context))
                return true;
            if (inAirplane != null)
                return false;
            return cataloged != null && IsGeneralContext(cataloged.Category);
        }

        // A bind of a relation limited to some aircraft (GameInputBinding.Aircraft).
        private static bool IsLimited(GameInputBinding binding)
        {
            return binding.Aircraft != null && binding.Aircraft.Count > 0;
        }

        // Binds of the export per device (instance GUID) and action, as Write encodes them. includeLimited: also the
        // binds limited to some aircraft (only the direct write places them, per profile).
        private Dictionary<Guid, Dictionary<string, List<MsfsWanted>>> WantedByDevice(GameExportRequest request, bool includeLimited = true)
        {
            Dictionary<Guid, Dictionary<string, List<MsfsWanted>>> wantedByDevice = new Dictionary<Guid, Dictionary<string, List<MsfsWanted>>>();
            foreach (GameInputBinding binding in request.Bindings ?? new List<GameInputBinding>())
            {
                if (!includeLimited && IsLimited(binding))
                    continue;
                GameAction action = request.Catalog.Find(binding.ActionId);
                Guid guid;
                MsfsWanted wanted;
                if (action == null || Encode(binding, action, out guid, out wanted).Length > 0)
                    continue;
                Dictionary<string, List<MsfsWanted>> byAction;
                if (!wantedByDevice.TryGetValue(guid, out byAction))
                    wantedByDevice[guid] = byAction = new Dictionary<string, List<MsfsWanted>>(StringComparer.Ordinal);
                List<MsfsWanted> list;
                if (!byAction.TryGetValue(action.Id, out list))
                    byAction[action.Id] = list = new List<MsfsWanted>();
                wanted.Aircraft = IsLimited(binding) ? new List<string>(binding.Aircraft) : null;
                MsfsWanted same = list.FirstOrDefault(w => w.Combo.Signature == wanted.Combo.Signature);
                if (same == null)
                    list.Add(wanted);
                else if (same.Aircraft != null)
                    // The same bind of two relations: for the aircraft of both (every aircraft when one has no limit).
                    same.Aircraft = wanted.Aircraft == null ? null : same.Aircraft.Union(wanted.Aircraft, StringComparer.OrdinalIgnoreCase).ToList();
            }
            return wantedByDevice;
        }

        private static string Label(bool general)
        {
            return general ? "General" : "Airplanes";
        }

        private static string BindName(MsfsWanted wanted, GameAction action)
        {
            string title = action != null && !string.IsNullOrWhiteSpace(action.Title) ? action.Title : wanted.ActionId;
            return string.IsNullOrWhiteSpace(wanted.RelationName) || string.Equals(wanted.RelationName, title, StringComparison.OrdinalIgnoreCase)
                ? title : wanted.RelationName + " (" + title + ")";
        }

        // Running processes that block a write into the sim's save: the sim and its launch helper.
        internal List<string> RunningSimProcesses()
        {
            List<string> running = GameProcessGuard.FindRunning(ProcessNames);
            if (Environment.GetEnvironmentVariable(GameProcessGuard.IgnoreRunningGamesVariable) == "1")
                return running;
            foreach (string name in SimHelperNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                System.Diagnostics.Process[] processes = System.Diagnostics.Process.GetProcessesByName(name);
                foreach (System.Diagnostics.Process process in processes)
                {
                    string image = GameProcessGuard.ImagePath(process);
                    // The helper of another Store game does not count; one whose exe path cannot be read does (safe side).
                    if (image.Length == 0 || image.IndexOf("Flight Simulator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        image.IndexOf("Limitless", StringComparison.OrdinalIgnoreCase) >= 0 || image.IndexOf("MSFS", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!running.Contains(name, StringComparer.OrdinalIgnoreCase))
                            running.Add(name + (image.Length > 0 ? " (" + image + ")" : string.Empty));
                    }
                    process.Dispose();
                }
            }
            return running;
        }

        // Writes the export's binds into the user's active profiles. Returns the devices and categories that were not
        // updated (the caller writes import files for them), as "<guid N>|G" / "<guid N>|A".
        private HashSet<string> WriteUserProfilesOf2024(GameExportRequest request, GameIoResult result)
        {
            HashSet<string> fallback = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<Guid, Dictionary<string, List<MsfsWanted>>> wantedByDevice = WantedByDevice(request);
            if (sim != 2024 || wantedByDevice.Count == 0)
                return fallback;
            if (!WriteUserProfiles)
            {
                foreach (Guid device in wantedByDevice.Keys)
                {
                    fallback.Add(device.ToString("N") + "|G");
                    fallback.Add(device.ToString("N") + "|A");
                }
                result.Steps.Add("Writing into your own MSFS 2024 profiles is off (--user-profiles false): JoyPro's binds are in the import files.");
                JoyPro.Logging.JoyProLog.Info("MSFS", "Direct write into the user's profiles is off.");
                return fallback;
            }

            List<UserProfileTarget> targets = new List<UserProfileTarget>();
            List<string> sourceNotes = new List<string>();
            List<KeyValuePair<MsfsProfileSourceKind, string>> sources = new List<KeyValuePair<MsfsProfileSourceKind, string>>();
            foreach (string remote in MsfsUserProfiles.SteamRemoteFolders(sim, new List<string>()))
                sources.Add(new KeyValuePair<MsfsProfileSourceKind, string>(MsfsProfileSourceKind.Steam, remote));
            try
            {
                foreach (string user in WgsWriter.UserFolders(MsfsUserProfiles.StoreWgsFolder(sim)))
                    sources.Add(new KeyValuePair<MsfsProfileSourceKind, string>(MsfsProfileSourceKind.Store, user));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                sourceNotes.Add("the Microsoft Store save folder could not be listed (" + ex.Message + ")");
            }
            JoyPro.Logging.JoyProLog.Info("MSFS", "Direct write: " + sources.Count.ToString(CultureInfo.InvariantCulture) + " source(s) of own profiles: " +
                string.Join("; ", sources.Select(s => s.Key + " " + s.Value)));

            // Per device and category: every own profile the records of the first source that names one name, each
            // with the aircraft it is used by (MSFS 2024 keeps a profile choice per device and aircraft).
            Dictionary<string, string> aircraftTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (MsfsAircraft aircraft in MsfsAircraftCatalog.Load(MsfsAircraftCatalog.FilePath(DbFolder)))
                aircraftTitles[aircraft.Key] = aircraft.Title;
            HashSet<string> unknownAircraft = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<UserProfileTarget>> chosen = new Dictionary<string, List<UserProfileTarget>>(StringComparer.Ordinal);
            Dictionary<string, string> noTarget = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<MsfsProfileSourceKind, string> source in sources)
            {
                Dictionary<string, string> files;
                MsfsSimProfile records = SourceRecords(source.Key, source.Value, out files, sourceNotes);
                if (records == null)
                    continue;
                // The export's devices, and every device whose profiles hold binds JoyPro wrote (a relation deleted in
                // JoyPro takes its bind out of the profile again; merge-overwrite).
                foreach (Guid device in wantedByDevice.Keys.Concat(records.Records.Keys).Distinct().ToList())
                {
                    foreach (bool general in new[] { true, false })
                    {
                        string key = device.ToString("N") + "|" + (general ? "G" : "A");
                        if (chosen.ContainsKey(key))
                            continue;
                        bool exported = wantedByDevice.ContainsKey(device);
                        int total;
                        List<KeyValuePair<string, List<string>>> named = OwnProfilesByAircraft(records, device, general, out total);
                        if (named.Count == 0)
                        {
                            if (exported && !noTarget.ContainsKey(key))
                                noTarget[key] = total == 0
                                    ? "the sim has no record of this device yet"
                                    : "the sim uses no own profile of this device for " + Label(general) + " (its " + total.ToString(CultureInfo.InvariantCulture) + " record(s) name a default preset or none)";
                            continue;
                        }
                        List<UserProfileTarget> found = new List<UserProfileTarget>();
                        List<string> problems = new List<string>();
                        foreach (KeyValuePair<string, List<string>> entry in named)
                        {
                            string active = entry.Key;
                            string file;
                            files.TryGetValue(active, out file);
                            if (!exported && !OwnsBinds(source.Key == MsfsProfileSourceKind.Store ? Path.Combine(source.Value, active) : file ?? string.Empty))
                                continue;
                            string used = " (used by " + AircraftNames(entry.Value, aircraftTitles) + ")";
                            if (file == null)
                            {
                                problems.Add("the sim's records name your profile " + active + used + ", but it is not in " + source.Value);
                                continue;
                            }
                            MsfsProfileFile profile;
                            List<string> errors = new List<string>();
                            if (!MsfsProfileFile.TryLoad(file, errors, out profile))
                            {
                                problems.Add("your profile " + active + used + " could not be read (" + string.Join(" ", errors) + ")");
                                continue;
                            }
                            bool categoryFits = general ? profile.CategoryKey.Length == 0 || profile.IsGeneralCategory : profile.CategoryKey == "AIRPLANE";
                            if (!profile.HasDeviceGuid || profile.DeviceGuid != device || !profile.Is2024 || profile.IsPreset || !categoryFits)
                            {
                                problems.Add("your profile " + active + used + " is not a " + Label(general) + " profile of this device (device {" + profile.DeviceGuid.ToString().ToUpperInvariant() +
                                             "}, category " + (profile.CategoryKey.Length == 0 ? "none" : profile.CategoryKey) + ")");
                                continue;
                            }
                            profile.Source = source.Key;
                            foreach (string aircraft in entry.Value.Where(a => a.Length > 0 && !aircraftTitles.ContainsKey(a)))
                                unknownAircraft.Add(aircraft);
                            found.Add(new UserProfileTarget
                            {
                                Device = device, General = general, Kind = source.Key, Folder = source.Value, ProfileName = active, FilePath = file, Profile = profile,
                                RecordsNaming = entry.Value.Count, Records = total, Aircraft = entry.Value, AircraftText = AircraftNames(entry.Value, aircraftTitles),
                                DeviceName = profile.DeviceName.Trim()
                            });
                            EngineDiagnostics.NoteRead(file, DisplayName + " " + Label(general) + " profile '" + profile.FriendlyName + "' (" + active + ") of " + profile.DeviceName);
                        }
                        if (found.Count == 0)
                        {
                            if (problems.Count > 0)
                                noTarget[key] = string.Join("; ", problems);
                            continue;
                        }
                        chosen[key] = found;
                        foreach (string problem in problems)
                            result.Warnings.Add("MSFS 2024 '" + found[0].DeviceName + "' (" + Label(general) + "): " + problem + "; the aircraft of that profile do not get JoyPro's binds.");
                    }
                }
            }
            foreach (string note in sourceNotes)
                result.Steps.Add("Your MSFS 2024 profiles: " + note + ".");
            if (unknownAircraft.Count > 0)
                result.Steps.Add("MSFS 2024 aircraft in the sim's records that JoyPro's aircraft list does not have (refresh the DB to tick them in a relation): " +
                                 string.Join(", ", unknownAircraft.OrderBy(a => a, StringComparer.OrdinalIgnoreCase)) + ".");

            // Every bind of a device goes to the profiles of the category the sim lists its action in: a bind for every
            // aircraft into each of them, a bind limited to aircraft only into the profiles whose aircraft all have it.
            foreach (KeyValuePair<Guid, Dictionary<string, List<MsfsWanted>>> device in wantedByDevice)
            {
                List<UserProfileTarget> generalTargets, airplaneTargets;
                chosen.TryGetValue(device.Key.ToString("N") + "|G", out generalTargets);
                chosen.TryGetValue(device.Key.ToString("N") + "|A", out airplaneTargets);
                foreach (KeyValuePair<string, List<MsfsWanted>> entry in device.Value)
                {
                    GameAction cataloged = request.Catalog.Find(entry.Key);
                    bool general = GoesToGeneral(entry.Key, cataloged, generalTargets == null ? null : generalTargets[0].Profile, airplaneTargets == null ? null : airplaneTargets[0].Profile);
                    List<UserProfileTarget> slot = general ? generalTargets : airplaneTargets;
                    string key = device.Key.ToString("N") + "|" + (general ? "G" : "A");
                    if (slot == null)
                    {
                        if (entry.Value.Any(w => w.Aircraft == null))
                            fallback.Add(key);
                        foreach (MsfsWanted limited in entry.Value.Where(w => w.Aircraft != null))
                        {
                            result.BindingsSkipped++;
                            result.Warnings.Add("MSFS 2024 '" + DeviceLabel(request, device.Key) + "' (" + Label(general) + "): '" + BindName(limited, cataloged) + "' is only for " +
                                                AircraftNames(limited.Aircraft, aircraftTitles) + " and was not written: no own profile of this device to write it into (" +
                                                (noTarget.ContainsKey(key) ? noTarget[key] : "the sim's device records were not found") + ").");
                        }
                        continue;
                    }
                    foreach (UserProfileTarget target in slot)
                    {
                        List<MsfsWanted> fits = entry.Value.Where(w => FitsProfile(w, target)).ToList();
                        if (fits.Count > 0)
                            target.Wanted[entry.Key] = fits;
                    }
                    foreach (MsfsWanted limited in entry.Value.Where(w => w.Aircraft != null))
                    {
                        string name = BindName(limited, cataloged);
                        List<UserProfileTarget> into = slot.Where(t => FitsProfile(limited, t)).ToList();
                        HashSet<string> covered = new HashSet<string>(into.SelectMany(t => t.Aircraft), StringComparer.OrdinalIgnoreCase);
                        // A profile shared with aircraft the relation is not for: not written there, the user splits it.
                        foreach (UserProfileTarget shared in slot.Where(t => !into.Contains(t) && t.Aircraft.Any(a => limited.Aircraft.Contains(a, StringComparer.OrdinalIgnoreCase))))
                        {
                            string only = AircraftNames(shared.Aircraft.Where(a => limited.Aircraft.Contains(a, StringComparer.OrdinalIgnoreCase)), aircraftTitles);
                            string others = AircraftNames(shared.Aircraft.Where(a => !limited.Aircraft.Contains(a, StringComparer.OrdinalIgnoreCase)), aircraftTitles);
                            result.Warnings.Add("MSFS 2024 '" + shared.DeviceName + "' (" + Label(general) + "): '" + name + "' is only for " + only + ", but your profile '" + shared.Profile.FriendlyName +
                                                "' is also used by " + others + ": not written there. Give the " + only + " its own profile in the sim (Controls, with the " + only +
                                                " loaded: duplicate the profile), then export again.");
                            foreach (string aircraft in shared.Aircraft)
                                covered.Add(aircraft);
                        }
                        List<string> missing = limited.Aircraft.Where(a => !covered.Contains(a)).ToList();
                        if (missing.Count > 0)
                            result.Steps.Add("MSFS 2024 '" + DeviceLabel(request, device.Key) + "' (" + Label(general) + "): '" + name + "' is not written for " + AircraftNames(missing, aircraftTitles) +
                                             ": the sim's records name no own profile of this device for " + (missing.Count == 1 ? "it" : "them") +
                                             " (fly it once with the device and change a bind in Controls, then export again).");
                        if (into.Count == 0)
                            result.BindingsSkipped++;
                    }
                }
            }
            foreach (string key in fallback.ToList())
            {
                Guid device = Guid.ParseExact(key.Substring(0, 32), "N");
                bool general = key.EndsWith("|G", StringComparison.Ordinal);
                string reason;
                if (!noTarget.TryGetValue(key, out reason))
                    reason = sources.Count == 0 ? "no own MSFS 2024 profiles on this PC (Steam remote folder or Microsoft Store save)" : "the sim's device records (profile_00) were not found";
                AddUpdate(result, DeviceLabel(request, device), Label(general), string.Empty, "import-file", reason, 0, new List<string>(), string.Empty);
                result.Steps.Add("MSFS 2024 '" + DeviceLabel(request, device) + "' (" + Label(general) + "): not written into your profiles: " + reason + "; the binds are in the import file.");
            }
            // Also a profile without binds of the export: JoyPro's earlier binds there go (merge-overwrite).
            targets.AddRange(chosen.Values.SelectMany(t => t).Where(t => t.Wanted.Count > 0 || OwnsBinds(StableProfilePath(t))));
            if (targets.Count == 0)
                return fallback;

            // Apply the binds (ownership kept per profile, stable over the Store's new blob names). The user's own
            // profiles are never cleared for devices without binds (--nuke-untouched-devices is for JoyPro's presets).
            GameExportRequest userRequest = new GameExportRequest
            {
                ConfigPath = request.ConfigPath, Mode = request.Mode, DryRun = request.DryRun, IncludeKeyboard = request.IncludeKeyboard, NukeUnusedDevices = false,
                SettingsOnly = request.SettingsOnly, Catalog = request.Catalog, Devices = request.Devices, Bindings = request.Bindings
            };
            foreach (UserProfileTarget target in targets)
            {
                string ownershipKey = OwnershipKey(StableProfilePath(target));
                Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(ownershipKey, result.Warnings);
                ApplyToProfile(target.Profile, userRequest, target.Wanted, owned, new Dictionary<string, string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), target.Scratch);
                target.NewBytes = target.Profile.ToBytes();
                if (!request.DryRun)
                    GameOwnershipStore.Save(ownershipKey, GameId, owned);
            }

            if (request.DryRun)
            {
                foreach (UserProfileTarget target in targets)
                {
                    target.Status = "dry-run";
                    Report(request, result, target, "would be written");
                }
                return fallback;
            }

            List<string> running = RunningSimProcesses();
            if (running.Count > 0)
            {
                foreach (UserProfileTarget target in targets)
                {
                    fallback.Add(target.Device.ToString("N") + "|" + (target.General ? "G" : "A"));
                    target.Status = "import-file";
                    target.Reason = "MSFS 2024 is running (" + string.Join(", ", running) + "); close it and export again";
                    Report(request, result, target, null);
                }
                result.Warnings.Add("MSFS 2024 is running (" + string.Join(", ", running) + "): JoyPro did not write into your MSFS profiles (the sim writes its save back on exit). Close the sim and export again; the binds are in the import files meanwhile.");
                return fallback;
            }

            // Steam: one file per profile.
            foreach (UserProfileTarget target in targets.Where(t => t.Kind == MsfsProfileSourceKind.Steam))
            {
                if (target.Profile.ChangedActions == 0 && target.Profile.ChangedAxes == 0)
                {
                    target.Status = "unchanged";
                    CheckComplete(request, target, File.ReadAllBytes(target.FilePath));
                    Report(request, result, target, null);
                    continue;
                }
                byte[] before = File.ReadAllBytes(target.FilePath);
                try
                {
                    BackUp(target.FilePath, result);
                    File.WriteAllBytes(target.FilePath, target.NewBytes);
                    byte[] reread = File.ReadAllBytes(target.FilePath);
                    string invalid = ValidateWritten(target, reread);
                    if (invalid != null)
                    {
                        File.WriteAllBytes(target.FilePath, before);
                        throw new IOException("the written profile did not read back as expected (" + invalid + "); the previous file was put back");
                    }
                    target.Status = "updated";
                    result.FilesWritten.Add(target.FilePath);
                    CheckComplete(request, target, reread);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    fallback.Add(target.Device.ToString("N") + "|" + (target.General ? "G" : "A"));
                    target.Status = "import-file";
                    target.Reason = "the file could not be written (" + ex.Message + ")";
                }
                Report(request, result, target, null);
            }

            // Microsoft Store: per save (user folder) one write of every changed container.
            foreach (IGrouping<string, UserProfileTarget> save in targets.Where(t => t.Kind == MsfsProfileSourceKind.Store).GroupBy(t => t.Folder, StringComparer.OrdinalIgnoreCase))
                WriteStoreSave(request, result, save.Key, save.ToList(), fallback);
            return fallback;
        }

        private void WriteStoreSave(GameExportRequest request, GameIoResult result, string userFolder, List<UserProfileTarget> targets, HashSet<string> fallback)
        {
            List<UserProfileTarget> changed = targets.Where(t => t.Profile.ChangedActions > 0 || t.Profile.ChangedAxes > 0).ToList();
            foreach (UserProfileTarget target in targets.Except(changed))
            {
                target.Status = "unchanged";
                CheckComplete(request, target, File.ReadAllBytes(target.FilePath));
                Report(request, result, target, null);
            }
            if (changed.Count == 0)
                return;
            Action<string> fail = reason =>
            {
                foreach (UserProfileTarget target in changed)
                {
                    fallback.Add(target.Device.ToString("N") + "|" + (target.General ? "G" : "A"));
                    target.Status = "import-file";
                    target.Reason = reason;
                    Report(request, result, target, null);
                }
            };
            Dictionary<string, WgsContainerState> states;
            try
            {
                WgsWriter.ReadChecked(userFolder, changed.Select(t => t.ProfileName), out states);
                foreach (UserProfileTarget target in changed)
                {
                    // The blob must be the plain profile JoyPro read (no compressed or other content).
                    if (!string.Equals(Path.GetFullPath(states[target.ProfileName].BlobPath), Path.GetFullPath(target.FilePath), StringComparison.OrdinalIgnoreCase))
                        throw new WgsUnexpectedException("the save of " + target.ProfileName + " is not a plain profile file");
                }
            }
            catch (WgsUnexpectedException ex)
            {
                JoyPro.Logging.JoyProLog.Warn("MSFS", "Store save not written (" + userFolder + "): " + ex.Message);
                fail("JoyPro found something unexpected in the Microsoft Store save and wrote nothing: " + ex.Message);
                return;
            }

            // Full backup of the save (every file of the user folder, the index last), then the new files as "new".
            byte[] indexBytes = File.ReadAllBytes(WgsWriter.IndexPath(userFolder));
            List<string> files = new List<string>();
            foreach (string folder in Directory.GetDirectories(userFolder))
                files.AddRange(Directory.GetFiles(folder));
            files.AddRange(Directory.GetFiles(userFolder).Where(f => !string.Equals(Path.GetFileName(f), "containers.index", StringComparison.OrdinalIgnoreCase)));
            files.Add(WgsWriter.IndexPath(userFolder));
            List<string> backupWarnings = new List<string>();
            foreach (string file in files)
                GameBackups.Protect(GameId, file, backupWarnings);
            if (backupWarnings.Count > 0)
            {
                result.Warnings.AddRange(backupWarnings);
                fail("the Microsoft Store save could not be backed up, so JoyPro wrote nothing (" + string.Join(" ", backupWarnings) + ")");
                return;
            }
            string backupSet = GameBackups.OpenedSets().FirstOrDefault() ?? string.Empty;

            Dictionary<string, byte[]> contents = changed.ToDictionary(t => t.ProfileName, t => t.NewBytes, StringComparer.OrdinalIgnoreCase);
            List<string> log = new List<string>();
            try
            {
                Dictionary<string, string> blobs = WgsWriter.Write(userFolder, contents, DateTime.UtcNow,
                    () => File.WriteAllBytes(WgsWriter.IndexPath(userFolder), indexBytes), log,
                    path => GameBackups.Protect(GameId, path, result.Warnings),
                    (name, bytes) =>
                    {
                        UserProfileTarget target = changed.First(t => string.Equals(t.ProfileName, name, StringComparison.OrdinalIgnoreCase));
                        return ValidateWritten(target, bytes);
                    });
                // Read again the way import does (index, container, blob): the profile must have the binds.
                List<WgsBlob> listed = WgsStore.Blobs(userFolder, new List<string>());
                foreach (UserProfileTarget target in changed)
                {
                    target.FilePath = blobs[target.ProfileName];
                    WgsBlob blob = listed.FirstOrDefault(b => string.Equals(b.Container, target.ProfileName, StringComparison.OrdinalIgnoreCase));
                    if (blob == null || !string.Equals(Path.GetFullPath(blob.FilePath), Path.GetFullPath(target.FilePath), StringComparison.OrdinalIgnoreCase))
                        JoyPro.Logging.JoyProLog.Warn("MSFS", "Store save: the reader finds " + (blob == null ? "no blob" : blob.FilePath) + " for " + target.ProfileName + " instead of " + target.FilePath);
                    target.Status = "updated";
                    result.FilesWritten.Add(target.FilePath);
                    CheckComplete(request, target, File.ReadAllBytes(target.FilePath));
                    Report(request, result, target, null);
                }
                result.FilesWritten.Add(WgsWriter.IndexPath(userFolder));
                JoyPro.Logging.JoyProLog.Info("MSFS", "Store save written (" + userFolder + ", backup set " + backupSet + "): " + string.Join("; ", log));
                result.Steps.Add("Microsoft Store save written (" + changed.Count.ToString(CultureInfo.InvariantCulture) + " profile(s)); backup of the whole save: set " + backupSet +
                                 " (restore: engine backups restore " + backupSet + ", or Export > Game config backups). The Xbox app uploads the changed profiles to the cloud on the next start of the sim.");
            }
            catch (WgsUnexpectedException ex)
            {
                JoyPro.Logging.JoyProLog.Warn("MSFS", "Store save not written (" + userFolder + "): " + ex.Message + "; " + string.Join("; ", log));
                fail("JoyPro found something unexpected in the Microsoft Store save and wrote nothing: " + ex.Message);
            }
            catch (WgsWriteFailedException ex)
            {
                JoyPro.Logging.JoyProLog.Warn("MSFS", "Store save write failed and was rolled back (" + userFolder + ", backup set " + backupSet + "): " + ex.Message + "; " + string.Join("; ", log));
                fail("writing the Microsoft Store save failed (" + ex.Message + "); JoyPro put the save back as it was");
            }
        }

        // JoyPro has written binds into this profile before (its ownership record is not empty).
        private static bool OwnsBinds(string stablePath)
        {
            if (string.IsNullOrEmpty(stablePath))
                return false;
            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(OwnershipKey(stablePath), new List<string>());
            return owned.Values.Any(v => v.Count > 0);
        }

        // A key for ownership that stays when the Store gives the profile a new blob name.
        private static string StableProfilePath(UserProfileTarget target)
        {
            return target.Kind == MsfsProfileSourceKind.Store ? Path.Combine(target.Folder, target.ProfileName) : target.FilePath;
        }

        internal static string StableProfilePath(MsfsProfileFile profile)
        {
            return profile.Source == MsfsProfileSourceKind.Store && !string.IsNullOrEmpty(profile.StoreContainer) && !string.IsNullOrEmpty(profile.StoreUserFolder)
                ? Path.Combine(profile.StoreUserFolder, profile.StoreContainer) : profile.FilePath;
        }

        // The written bytes as a profile: same device, same category, the wanted binds there. Null when fine.
        private static string ValidateWritten(UserProfileTarget target, byte[] bytes)
        {
            if (!bytes.SequenceEqual(target.NewBytes))
                return "the bytes differ from what JoyPro wrote";
            string temp = Path.Combine(Path.GetTempPath(), "joypro-msfs-check-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllBytes(temp, bytes);
                MsfsProfileFile profile;
                if (!MsfsProfileFile.TryLoad(temp, new List<string>(), out profile))
                    return "not a readable profile";
                if (profile.DeviceGuid != target.Device || profile.CategoryKey != target.Profile.CategoryKey)
                    return "another device or category";
                if (profile.Actions.Count != target.Profile.Actions.Count)
                    return "the profile lists " + profile.Actions.Count.ToString(CultureInfo.InvariantCulture) + " actions instead of " + target.Profile.Actions.Count.ToString(CultureInfo.InvariantCulture);
                return null;
            }
            finally
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
        }

        // Completeness: every wanted bind of the target is in the profile as written; the others get their reason.
        private void CheckComplete(GameExportRequest request, UserProfileTarget target, byte[] bytes)
        {
            string temp = Path.Combine(Path.GetTempPath(), "joypro-msfs-check-" + Guid.NewGuid().ToString("N") + ".xml");
            MsfsProfileFile profile = null;
            try
            {
                File.WriteAllBytes(temp, bytes);
                MsfsProfileFile.TryLoad(temp, new List<string>(), out profile);
            }
            finally
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
            foreach (KeyValuePair<string, List<MsfsWanted>> entry in target.Wanted)
            {
                GameAction cataloged = request.Catalog.Find(entry.Key);
                MsfsAction action = profile == null ? null : profile.Find(entry.Key);
                foreach (MsfsWanted wanted in entry.Value)
                {
                    if (action != null && action.Combos.Any(c => c.Signature == wanted.Combo.Signature))
                        continue;
                    string reason = target.Scratch.Warnings.FirstOrDefault(w => w.Contains("(" + entry.Key + ")") || w.StartsWith(entry.Key + " ", StringComparison.Ordinal));
                    if (reason == null)
                        reason = action == null ? "the profile does not list " + entry.Key + " (another category of the sim)" : "the profile kept another bind there";
                    target.NotWritten.Add(BindName(wanted, cataloged) + " (" + Label(target.General) + ") - " + reason);
                }
            }
        }

        private static string DeviceLabel(GameExportRequest request, Guid device)
        {
            GameDevice connected = request.Devices == null ? null : request.Devices.Devices.FirstOrDefault(d => d.InstanceGuid == device);
            if (connected != null && !string.IsNullOrWhiteSpace(connected.InstanceName))
                return connected.InstanceName.Trim();
            // Not connected: the name the profile's binds carry ("MFG Crosswind V2 {GUID}").
            foreach (GameInputBinding binding in request.Bindings ?? new List<GameInputBinding>())
            {
                string name;
                Guid guid;
                if (GameDeviceDirectory.TryParseJoyProName(binding.Device ?? string.Empty, out name, out guid) && guid == device && !string.IsNullOrWhiteSpace(name))
                    return name.Trim();
            }
            return "{" + device.ToString().ToUpperInvariant() + "}";
        }

        private void Report(GameExportRequest request, GameIoResult result, UserProfileTarget target, string dryRunText)
        {
            string device = target.DeviceName.Length > 0 ? target.DeviceName : DeviceLabel(request, target.Device);
            int binds = target.Wanted.Values.Sum(v => v.Count) - target.NotWritten.Count;
            string active = target.AircraftText.Length > 0 ? target.AircraftText : "active";
            string where = (target.Kind == MsfsProfileSourceKind.Store ? "Microsoft Store save" : "Steam") + ", " + target.ProfileName;
            string text;
            switch (target.Status)
            {
                case "updated":
                    text = "your profile '" + target.Profile.FriendlyName + "' (" + active + "): " + binds.ToString(CultureInfo.InvariantCulture) + " bind(s), " +
                           target.Profile.ChangedActions.ToString(CultureInfo.InvariantCulture) + " action(s) changed (" + where + ")";
                    break;
                case "unchanged":
                    text = "your profile '" + target.Profile.FriendlyName + "' (" + active + ") already has JoyPro's " + binds.ToString(CultureInfo.InvariantCulture) + " bind(s) (" + where + ")";
                    break;
                case "dry-run":
                    text = "dry run: your profile '" + target.Profile.FriendlyName + "' (" + active + ") " + dryRunText + ": " + target.Profile.ChangedActions.ToString(CultureInfo.InvariantCulture) +
                           " action(s) would change (" + where + ")";
                    break;
                default:
                    text = "your profile '" + target.Profile.FriendlyName + "' was not changed: " + target.Reason + "; the binds are in the import file";
                    break;
            }
            result.Steps.Add("MSFS 2024 '" + device + "' (" + Label(target.General) + "): " + text + ".");
            foreach (string missing in target.NotWritten)
                result.Warnings.Add("MSFS 2024 '" + device + "': not written: " + missing + ".");
            AddUpdate(result, device, Label(target.General), target.Profile.FriendlyName, target.Status, target.Reason, Math.Max(0, binds), target.NotWritten, target.AircraftText);
            JoyPro.Logging.JoyProLog.Info("MSFS", "Direct write " + target.Status + ": " + device + " {" + target.Device.ToString().ToUpperInvariant() + "} " + Label(target.General) + " -> '" +
                target.Profile.FriendlyName + "' " + target.ProfileName + " (" + target.Kind + ", " + target.Folder + ", records " + target.RecordsNaming.ToString(CultureInfo.InvariantCulture) + "/" +
                target.Records.ToString(CultureInfo.InvariantCulture) + "): " + target.Profile.ChangedActions.ToString(CultureInfo.InvariantCulture) + " action(s) changed (" +
                string.Join(", ", target.Profile.Actions.Where(a => a.Changed).Select(a => a.Name).Take(40)) + ")" + (target.Reason.Length > 0 ? "; " + target.Reason : string.Empty) +
                (target.NotWritten.Count > 0 ? "; not written: " + string.Join(" | ", target.NotWritten) : string.Empty));
        }

        private static void AddUpdate(GameIoResult result, string device, string category, string profile, string status, string reason, int binds, List<string> notWritten, string aircraft)
        {
            result.ProfileUpdates.Add(new GameProfileUpdate
            {
                Device = device, Category = category, Profile = profile ?? string.Empty, Status = status ?? string.Empty, Reason = reason ?? string.Empty,
                Binds = binds, NotWritten = new List<string>(notWritten ?? new List<string>()), Aircraft = aircraft ?? string.Empty
            });
        }

        // The sim's records and the own profiles of one source (name inputprofile_<n> -> readable file). Null when the
        // source has no records.
        private MsfsSimProfile SourceRecords(MsfsProfileSourceKind kind, string folder, out Dictionary<string, string> files, List<string> notes)
        {
            files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            MsfsSimProfile records = new MsfsSimProfile();
            try
            {
                if (kind == MsfsProfileSourceKind.Steam)
                {
                    string profile00 = Path.Combine(folder, "profile_00");
                    if (!File.Exists(profile00) || !records.Read(File.ReadAllBytes(profile00)))
                    {
                        notes.Add("no device records (profile_00) in " + folder);
                        return null;
                    }
                    records.Sources.Add(profile00);
                    foreach (string file in MsfsUserProfiles.SteamProfileFiles(folder))
                        files[Path.GetFileName(file)] = file;
                    return records;
                }
                List<WgsBlob> blobs = WgsStore.Blobs(folder, new List<string>());
                WgsBlob profile00Blob = blobs.FirstOrDefault(b => string.Equals(b.Container, "profile_00", StringComparison.OrdinalIgnoreCase));
                byte[] profile00Bytes = profile00Blob == null ? null : File.ReadAllBytes(profile00Blob.FilePath);
                if (profile00Bytes == null || !(records.Read(profile00Bytes) || records.Read(MsfsSimProfile.Inflate(profile00Bytes))))
                {
                    notes.Add("no device records (profile_00) in the Microsoft Store save " + folder);
                    return null;
                }
                records.Sources.Add(profile00Blob.FilePath);
                foreach (WgsBlob blob in blobs.Where(b => b.Container != null && b.Container.StartsWith("inputprofile_", StringComparison.OrdinalIgnoreCase)))
                {
                    // Only plain profiles are written (a compressed blob would be read from JoyPro's cache).
                    if (MsfsProfileFile.LooksLikeProfile(blob.FilePath))
                        files[blob.Container] = blob.FilePath;
                }
                return records;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                notes.Add(folder + " could not be read (" + ex.Message + ")");
                return null;
            }
        }
    }
}
