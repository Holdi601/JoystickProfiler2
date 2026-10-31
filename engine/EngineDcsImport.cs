using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    internal static class EngineDcsImport
    {
        private const string SignatureSeparator = "\u00A7";
        private static readonly Regex NameRegex = new Regex("\\[\"name\"\\]\\s*=\\s*\"(?<name>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled);
        private static void ReportStatus(EngineDcsImportOptions options, string message)
        {
            if (options == null || options.StatusSink == null || string.IsNullOrWhiteSpace(message))
                return;
            options.StatusSink(message);
        }

        public static EngineDcsImportResult Scan(string savedGamesPath, EngineDcsImportOptions options = null)
        {
            return Execute(savedGamesPath, false, false, options);
        }

        public static EngineDcsImportResult Import(string savedGamesPath, bool merge, EngineDcsImportOptions options = null)
        {
            return Execute(savedGamesPath, true, merge, options);
        }

        public static string ToText(EngineDcsImportResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DCS Import");
            sb.AppendLine("Saved Games Path: " + Safe(result.SavedGamesPath));
            sb.AppendLine("Input Path: " + Safe(result.InputPath));
            sb.AppendLine("Merge: " + result.Merge);
            sb.AppendLine("Selected Planes: " + (result.SelectedPlanes == null || result.SelectedPlanes.Count == 0 ? "<all>" : string.Join(", ", result.SelectedPlanes)));
            sb.AppendLine("Selected Devices: " + (result.SelectedDevices == null || result.SelectedDevices.Count == 0 ? "<all>" : string.Join(", ", result.SelectedDevices)));
            sb.AppendLine("Ignored Devices: " + (result.IgnoredDevices == null || result.IgnoredDevices.Count == 0 ? "<none>" : string.Join(", ", result.IgnoredDevices)));
            sb.AppendLine("Matched Planes: " + (result.MatchedPlanes == null || result.MatchedPlanes.Count == 0 ? "<none>" : string.Join(", ", result.MatchedPlanes)));
            sb.AppendLine("Matched Devices: " + (result.MatchedDevices == null || result.MatchedDevices.Count == 0 ? "<none>" : string.Join(", ", result.MatchedDevices)));
            sb.AppendLine("Include Defaults: " + result.IncludeDefaults);
            sb.AppendLine("Include Keyboard: " + result.IncludeKeyboard);
            sb.AppendLine("Ignore _easy Planes: " + result.IgnoreEasyPlanes);
            sb.AppendLine("Import Inverted: " + result.ImportInverted);
            sb.AppendLine("Import Slider: " + result.ImportSlider);
            sb.AppendLine("Import Curvature: " + result.ImportCurvature);
            sb.AppendLine("Import Deadzone: " + result.ImportDeadzone);
            sb.AppendLine("Import Saturation X: " + result.ImportSaturationX);
            sb.AppendLine("Import Saturation Y: " + result.ImportSaturationY);
            sb.AppendLine("Files Scanned: " + result.FilesScanned);
            sb.AppendLine("Backup Files Scanned (.jp): " + result.BackupFilesScanned);
            sb.AppendLine("Entries Parsed: " + result.EntriesParsed);
            sb.AppendLine("Force Feedback Entries Parsed: " + result.ForceFeedbackEntriesParsed);
            sb.AppendLine("Default Entries Parsed: " + result.DefaultEntriesParsed);
            if (result.IncludeDefaults)
            {
                sb.AppendLine("Defaults Removed In DCS (skipped): " + result.DefaultEntriesRemovedByUser);
                sb.AppendLine("Defaults Replaced By Own Binds (skipped): " + result.DefaultEntriesReplacedByOwnBinds);
                sb.AppendLine("Defaults On Inputs With Own Binds (skipped): " + result.DefaultEntriesOnOwnInputs);
                sb.AppendLine("Defaults On An Input DCS Gives Two Actions (skipped): " + result.DefaultEntriesOnSharedInputs);
                sb.AppendLine("Keyboard Defaults Of An Action A Joystick Default Has (skipped): " + result.DefaultEntriesOnOtherDevice);
                sb.AppendLine("Generic Joystick Defaults (default.lua, imported): " + result.GenericJoystickDefaultsImported);
                sb.AppendLine("Connected Devices Without A File (defaults imported): " + result.DefaultDevicesWithoutFile);
            }
            sb.AppendLine("Imported Relations: " + result.ImportedRelations);
            sb.AppendLine("Imported Binds: " + result.ImportedBinds);
            if (result.JoinedExistingRelations > 0)
                sb.AppendLine("Joined Relations On The Same Input: " + result.JoinedExistingRelations);
            sb.AppendLine("Force Feedback Devices Imported: " + result.ForceFeedbackDevicesImported);
            sb.AppendLine("Skipped Entries: " + result.SkippedEntries);
            sb.AppendLine("Backup Binds Removed In DCS (.jp, skipped): " + result.StaleBackupEntriesSkipped);
            sb.AppendLine("Treat Same-Name Devices As One: " + result.MergeSameNameDevices);
            sb.AppendLine("Only Connected Devices: " + result.OnlyConnected);
            sb.AppendLine("Older Copies Of A Device Skipped: " + result.OlderDeviceCopyEntriesSkipped + " bind(s) in " + result.OlderDeviceCopyFilesSkipped + " file(s)");
            if (result.MergedDevices != null && result.MergedDevices.Count > 0)
            {
                sb.AppendLine("Devices Read As One (new ID over time):");
                foreach (string merged in result.MergedDevices)
                    sb.AppendLine("  - " + merged);
            }
            if (result.TwinDevices != null && result.TwinDevices.Count > 0)
                sb.AppendLine("Twin Devices Kept Apart: " + string.Join(", ", result.TwinDevices));
            if (result.OnlyConnected)
            {
                sb.AppendLine("Not Connected Devices Skipped: " + result.NotConnectedEntriesSkipped + " bind(s) in " + result.NotConnectedFilesSkipped + " file(s)" +
                              (result.NotConnectedDevices == null || result.NotConnectedDevices.Count == 0 ? string.Empty : " (" + string.Join(", ", result.NotConnectedDevices) + ")"));
            }

            if (result.Errors != null && result.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                for (int i = 0; i < result.Errors.Count; i++)
                {
                    sb.AppendLine("  - " + result.Errors[i]);
                }
            }

            if (result.Warnings != null && result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < result.Warnings.Count; i++)
                {
                    sb.AppendLine("  - " + result.Warnings[i]);
                }
            }

            return sb.ToString();
        }

        private static EngineDcsImportOptions NormalizeImportOptions(EngineDcsImportOptions options)
        {
            EngineDcsImportOptions normalized = options ?? new EngineDcsImportOptions();
            normalized.PlaneFilter = (normalized.PlaneFilter ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            normalized.DeviceFilter = (normalized.DeviceFilter ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            normalized.IgnoreDeviceFilter = (normalized.IgnoreDeviceFilter ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return normalized;
        }

        private static EngineDcsImportResult Execute(string savedGamesPath, bool applyImport, bool merge, EngineDcsImportOptions options)
        {
            EngineDcsImportOptions effectiveOptions = NormalizeImportOptions(options);
            EngineDcsImportResult result = new EngineDcsImportResult();
            ReportStatus(effectiveOptions, applyImport ? "Starting DCS import." : "Starting DCS scan.");
            result.Merge = merge;
            result.SavedGamesPath = Normalize(savedGamesPath);
            result.SelectedPlanes = new List<string>(effectiveOptions.PlaneFilter);
            result.SelectedDevices = new List<string>(effectiveOptions.DeviceFilter);
            result.IgnoredDevices = new List<string>(effectiveOptions.IgnoreDeviceFilter);
            result.IncludeDefaults = effectiveOptions.IncludeDefaults;
            result.IncludeKeyboard = effectiveOptions.IncludeKeyboard;
            result.IgnoreEasyPlanes = effectiveOptions.IgnoreEasyPlanes;
            result.ImportInverted = effectiveOptions.ImportInverted;
            result.ImportSlider = effectiveOptions.ImportSlider;
            result.ImportCurvature = effectiveOptions.ImportCurvature;
            result.ImportDeadzone = effectiveOptions.ImportDeadzone;
            result.ImportSaturationX = effectiveOptions.ImportSaturationX;
            result.ImportSaturationY = effectiveOptions.ImportSaturationY;

            string inputPath = ResolveInputPath(result.SavedGamesPath);
            result.InputPath = inputPath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(inputPath))
            {
                result.Errors.Add("Could not find DCS input folder under: " + result.SavedGamesPath);
                return result;
            }

            Dictionary<string, ParsedFfbSetting> parsedFfb;
            ReportStatus(effectiveOptions, "Parsing DCS input files...");
            DefaultsBasis defaultsBasis = new DefaultsBasis();
            List<ParsedBind> parsed = ParseAllInputFiles(inputPath, effectiveOptions, result, out parsedFfb, defaultsBasis);
            ReportStatus(
                effectiveOptions,
                "Parsed " +
                result.FilesScanned.ToString() +
                " file(s), entries: " +
                result.EntriesParsed.ToString() +
                ", ffb entries: " +
                result.ForceFeedbackEntriesParsed.ToString() + ".");
            if (effectiveOptions.IncludeDefaults)
            {
                ReportStatus(effectiveOptions, "Appending defaults from clean profile database.");
                parsed = AppendDefaultsFromCleanProfiles(parsed, effectiveOptions, result, defaultsBasis);
                ReportStatus(
                    effectiveOptions,
                    "Default entries appended: " + result.DefaultEntriesParsed.ToString() + ".");
            }

            if (!applyImport)
                return result;

            ReportStatus(effectiveOptions, "Applying parsed bindings to in-memory profile.");
            ApplyImport(parsed, parsedFfb, merge, effectiveOptions, result);
            ReportStatus(
                effectiveOptions,
                "DCS import complete. Relations: " +
                result.ImportedRelations.ToString() +
                ", binds: " +
                result.ImportedBinds.ToString() +
                (result.OlderDeviceCopyFilesSkipped > 0 ? ", older copies of a device skipped: " + result.OlderDeviceCopyEntriesSkipped.ToString() + " bind(s)" : string.Empty) +
                (result.OnlyConnected ? ", not connected devices skipped: " + result.NotConnectedEntriesSkipped.ToString() + " bind(s)" : string.Empty) + ".");
            return result;
        }

        private static List<ParsedBind> ParseAllInputFiles(
            string inputPath,
            EngineDcsImportOptions options,
            EngineDcsImportResult result,
            out Dictionary<string, ParsedFfbSetting> parsedFfbByDevice,
            DefaultsBasis defaultsBasis = null)
        {
            Dictionary<string, ParsedBind> parsed = new Dictionary<string, ParsedBind>(StringComparer.OrdinalIgnoreCase);
            parsedFfbByDevice = new Dictionary<string, ParsedFfbSetting>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, string>> modifierLookupByPlane =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (defaultsBasis != null)
                defaultsBasis.ModifierLookupByPlane = modifierLookupByPlane;
            Dictionary<string, EngineDcsFfbPlaneCorrection> ffbCorrections =
                EngineDcsFfbCorrectionCatalog.Load(result.Warnings);
            HashSet<string> matchedPlanes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> matchedDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> planeFilter = new HashSet<string>(
                options == null ? new List<string>() : options.PlaneFilter ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> deviceFilter = new HashSet<string>(
                options == null ? new List<string>() : options.DeviceFilter ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> deviceFilterByName = BuildGuidlessDeviceFilterSet(deviceFilter);
            HashSet<string> ignoredDeviceFilter = new HashSet<string>(
                options == null ? new List<string>() : options.IgnoreDeviceFilter ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> ignoredDeviceFilterByName = BuildGuidlessDeviceFilterSet(ignoredDeviceFilter);
            string[] files;
            try
            {
                files = Directory.GetFiles(inputPath, "*.*", SearchOption.AllDirectories)
                    .Where(IsSupportedInputExtension)
                    .Where(IsSupportedInputFile)
                    .ToArray();
            }
            catch (Exception ex)
            {
                result.Errors.Add("Failed to enumerate DCS input files: " + ex.Message);
                return new List<ParsedBind>();
            }

            ReportStatus(options, "Found " + files.Length.ToString() + " candidate DCS input file(s).");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            // Every file this import reads (the input files, a backup's own .diff.lua, the clean profiles and modifiers
            // of their planes) is read once, several at once; the parse below then runs on the texts in memory.
            List<string> prefetch = PrefetchList(files, options, planeFilter, deviceFilter, deviceFilterByName, ignoredDeviceFilter, ignoredDeviceFilterByName);
            RunFileCache.Prefetch(prefetch);
            HashSet<string> fileSet = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, FilePreParse> preParsed = PreParse(prefetch.Where(fileSet.Contains).ToList());
            DevicePlan devicePlan = BuildDevicePlan(prefetch.Where(fileSet.Contains).ToList(), options, result);
            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                string plane = ExtractPlaneName(file);
                string device = ExtractDeviceName(file);
                if (devicePlan.SkippedOlderCopies.Contains(file))
                {
                    result.OlderDeviceCopyFilesSkipped++;
                    result.OlderDeviceCopyEntriesSkipped += CountEntries(file, preParsed);
                    continue;
                }
                if (devicePlan.SkippedNotConnected.Contains(file))
                {
                    result.NotConnectedFilesSkipped++;
                    result.NotConnectedEntriesSkipped += CountEntries(file, preParsed);
                    continue;
                }
                if (options != null && options.IgnoreEasyPlanes && IsEasyPlaneToken(plane))
                    continue;
                if (options != null && !options.IncludeKeyboard && string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (planeFilter.Count > 0 && !planeFilter.Contains(plane))
                    continue;
                if (deviceFilter.Count > 0 && !MatchesDeviceFilter(device, deviceFilter, deviceFilterByName))
                    continue;
                if (ignoredDeviceFilter.Count > 0 && MatchesDeviceFilter(device, ignoredDeviceFilter, ignoredDeviceFilterByName))
                    continue;

                EngineProgress.Report(i + 1, files.Length, "DCS World, reading your binds", plane + " - " + device);
                bool isBackup = IsBackupFile(file);
                result.FilesScanned++;
                if (isBackup)
                    result.BackupFilesScanned++;
                string liveDevice = devicePlan.LiveName(device);
                matchedPlanes.Add(plane);
                matchedDevices.Add(liveDevice);
                ParseInputFile(file, isBackup, parsed, parsedFfbByDevice, modifierLookupByPlane, ffbCorrections, result, preParsed, devicePlan, defaultsBasis);

                int done = i + 1;
                if (done == files.Length || done % 200 == 0)
                {
                    ReportStatus(
                        options,
                        "Processed " +
                        done.ToString() +
                        "/" +
                        files.Length.ToString() +
                        " files.");
                }
            }

            result.MatchedPlanes = matchedPlanes.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            result.MatchedDevices = matchedDevices.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

            return parsed.Values
                .OrderBy(v => v.RelationName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Device ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Key ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Plane ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // The files ParseAllInputFiles will read for these input files (same filters).
        private static List<string> PrefetchList(string[] files, EngineDcsImportOptions options, HashSet<string> planeFilter, HashSet<string> deviceFilter,
            HashSet<string> deviceFilterByName, HashSet<string> ignoredDeviceFilter, HashSet<string> ignoredDeviceFilterByName)
        {
            List<string> list = new List<string>();
            HashSet<string> planeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                string plane = ExtractPlaneName(file);
                string device = ExtractDeviceName(file);
                if (options != null && options.IgnoreEasyPlanes && IsEasyPlaneToken(plane))
                    continue;
                if (options != null && !options.IncludeKeyboard && string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (planeFilter.Count > 0 && !planeFilter.Contains(plane))
                    continue;
                if (deviceFilter.Count > 0 && !MatchesDeviceFilter(device, deviceFilter, deviceFilterByName))
                    continue;
                if (ignoredDeviceFilter.Count > 0 && MatchesDeviceFilter(device, ignoredDeviceFilter, ignoredDeviceFilterByName))
                    continue;
                list.Add(file);
                try
                {
                    string deviceDir = Path.GetDirectoryName(file);
                    string planeDir = string.IsNullOrEmpty(deviceDir) ? null : Path.GetDirectoryName(deviceDir);
                    if (!string.IsNullOrEmpty(planeDir) && planeDirs.Add(planeDir))
                        list.Add(Path.Combine(planeDir, "modifiers.lua"));
                    if (IsBackupFile(file))
                    {
                        list.Add(file.Substring(0, file.Length - ".jp".Length) + ".diff.lua");
                        bool keyboard = string.Equals(new DirectoryInfo(deviceDir ?? string.Empty).Name, "keyboard", StringComparison.OrdinalIgnoreCase);
                        list.Add(DefaultsProfilePath(file, plane, keyboard));
                    }
                }
                catch (Exception)
                {
                }
            }
            return list;
        }

        // One device under several instance GUIDs (Windows re-enumerated it over time: "Joystick (Razer Tartarus V2)
        // {0CFE...}", "{29E6...}", "{4050...}"). DCS keeps a "<device> {GUID}.diff.lua" per GUID but loads only the
        // file of the GUID that is attached; the others are dead leftovers. With MergeSameNameDevices the GUIDs of one
        // name become one device: the connected GUID, else the GUID of the newest file. Per aircraft only one file is
        // read (the live GUID's, else the newest of that aircraft); the other files of that aircraft are older copies
        // and skipped. Two or more same-name devices connected at once are real twins (two identical sticks) and stay
        // apart. OnlyConnected skips the files of devices that are not connected (after the merge).
        internal sealed class DevicePlan
        {
            public readonly Dictionary<string, string> Remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> SkippedOlderCopies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> SkippedNotConnected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, Dictionary<string, string>> remappedLookups =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            public bool HasRemaps
            {
                get { return Remap.Count > 0; }
            }

            public string LiveName(string device)
            {
                string live;
                return device != null && Remap.TryGetValue(device, out live) ? live : device;
            }

            // The modifiers of an aircraft on an old GUID of a merged device move to the live GUID (once per aircraft).
            public Dictionary<string, string> RemapModifierLookup(string plane, Dictionary<string, string> lookup)
            {
                if (lookup == null || lookup.Count == 0)
                    return lookup;
                Dictionary<string, string> cached;
                if (remappedLookups.TryGetValue(plane ?? string.Empty, out cached))
                    return cached;
                Dictionary<string, string> remapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> entry in lookup)
                    remapped[entry.Key] = RemapReformer(entry.Value);
                remappedLookups[plane ?? string.Empty] = remapped;
                return remapped;
            }

            public string RemapReformer(string reformer)
            {
                if (string.IsNullOrEmpty(reformer) || Remap.Count == 0)
                    return reformer;
                string[] parts = reformer.Split(new[] { SignatureSeparator }, StringSplitOptions.None);
                if (parts.Length < 2)
                    return reformer;
                string live;
                if (!Remap.TryGetValue(DcsDeviceNames.JoyProName(parts[1].Trim()) ?? string.Empty, out live))
                    return reformer;
                parts[1] = live;
                return string.Join(SignatureSeparator, parts);
            }
        }

        private static DevicePlan BuildDevicePlan(List<string> files, EngineDcsImportOptions options, EngineDcsImportResult result)
        {
            DevicePlan plan = new DevicePlan();
            bool merge = options == null || options.MergeSameNameDevices;
            bool onlyConnected = options != null && options.OnlyConnected;
            result.MergeSameNameDevices = merge;
            result.OnlyConnected = onlyConnected;

            // name without GUID -> GUID -> files; GUID -> the device as JoyPro names it.
            Dictionary<string, Dictionary<Guid, List<string>>> byName = new Dictionary<string, Dictionary<Guid, List<string>>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<Guid, string> deviceOfGuid = new Dictionary<Guid, string>();
            foreach (string file in files)
            {
                string device = ExtractDeviceName(file);
                Guid guid;
                if (string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase) || !DcsDeviceNames.TryGetInstanceGuid(device, out guid))
                    continue;
                string name = StripDeviceGuidSuffix(device);
                Dictionary<Guid, List<string>> guids;
                if (!byName.TryGetValue(name, out guids))
                    byName[name] = guids = new Dictionary<Guid, List<string>>();
                List<string> list;
                if (!guids.TryGetValue(guid, out list))
                    guids[guid] = list = new List<string>();
                list.Add(file);
                if (!deviceOfGuid.ContainsKey(guid))
                    deviceOfGuid[guid] = device;
            }

            bool severalIds = merge && byName.Values.Any(g => g.Count > 1);
            if (!onlyConnected && !severalIds)
                return plan;

            HashSet<Guid> connected = ConnectedInstanceGuids();
            if (severalIds)
            {
                foreach (KeyValuePair<string, Dictionary<Guid, List<string>>> named in byName.OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase))
                {
                    Dictionary<Guid, List<string>> guids = named.Value;
                    if (guids.Count < 2)
                        continue;
                    List<Guid> live = guids.Keys.Where(connected.Contains).ToList();
                    if (live.Count >= 2)
                    {
                        result.TwinDevices.Add(named.Key + " (" + live.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " connected)");
                        continue;
                    }
                    Guid liveGuid = live.Count == 1
                        ? live[0]
                        : guids.OrderByDescending(g => NewestWrite(g.Value)).ThenBy(g => g.Key.ToString(), StringComparer.Ordinal).First().Key;
                    string liveName = deviceOfGuid[liveGuid];
                    foreach (Guid other in guids.Keys.Where(g => g != liveGuid).OrderBy(g => g.ToString(), StringComparer.Ordinal))
                    {
                        plan.Remap[deviceOfGuid[other]] = liveName;
                        result.MergedDevices.Add(deviceOfGuid[other] + " -> " + liveName + (live.Count == 1 ? " (connected)" : " (newest file)"));
                    }

                    // Per aircraft: one file set (DCS loads one); the others of that aircraft are older copies.
                    foreach (IGrouping<string, KeyValuePair<Guid, string>> plane in guids
                                 .SelectMany(g => g.Value.Select(f => new KeyValuePair<Guid, string>(g.Key, f)))
                                 .GroupBy(p => ExtractPlaneName(p.Value), StringComparer.OrdinalIgnoreCase))
                    {
                        List<Guid> inPlane = plane.Select(p => p.Key).Distinct().ToList();
                        if (inPlane.Count < 2)
                            continue;
                        Guid keep = inPlane.Contains(liveGuid)
                            ? liveGuid
                            : inPlane.OrderByDescending(g => NewestWrite(plane.Where(p => p.Key == g).Select(p => p.Value)))
                                .ThenBy(g => g.ToString(), StringComparer.Ordinal).First();
                        foreach (KeyValuePair<Guid, string> entry in plane)
                        {
                            if (entry.Key != keep)
                                plan.SkippedOlderCopies.Add(entry.Value);
                        }
                    }
                }
            }

            if (onlyConnected)
            {
                if (connected.Count == 0)
                    result.Warnings.Add("No connected game devices were found: only the keyboard was read (Only import connected devices).");
                HashSet<string> notConnected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    if (plan.SkippedOlderCopies.Contains(file))
                        continue;
                    string device = plan.LiveName(ExtractDeviceName(file));
                    Guid guid;
                    if (string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase) || !DcsDeviceNames.TryGetInstanceGuid(device, out guid))
                        continue;
                    if (connected.Contains(guid))
                        continue;
                    plan.SkippedNotConnected.Add(file);
                    notConnected.Add(device);
                }
                result.NotConnectedDevices = notConnected.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            }
            return plan;
        }

        // The instance GUIDs of the connected game devices (JOYPRO_FAKE_DEVICES in tests).
        private static HashSet<Guid> ConnectedInstanceGuids()
        {
            HashSet<Guid> connected = new HashSet<Guid>();
            try
            {
                JoyPro.Engine.Games.GameDeviceDirectory directory = JoyPro.Engine.Games.GameDeviceDirectory.Discover(new List<string>());
                foreach (JoyPro.Engine.Games.GameDevice device in directory.Devices)
                {
                    if (!device.Connected)
                        continue;
                    Guid guid = device.InstanceGuid;
                    if (guid == Guid.Empty)
                        DcsDeviceNames.TryGetInstanceGuid(device.JoyProName, out guid);
                    if (guid != Guid.Empty)
                        connected.Add(guid);
                }
            }
            catch (Exception)
            {
            }
            return connected;
        }

        private static DateTime NewestWrite(IEnumerable<string> files)
        {
            DateTime newest = DateTime.MinValue;
            foreach (string file in files)
            {
                try
                {
                    DateTime written = File.GetLastWriteTimeUtc(file);
                    if (written > newest)
                        newest = written;
                }
                catch (Exception)
                {
                }
            }
            return newest;
        }

        // The binds of a skipped file (a .jp backup only when its .diff.lua is not read either: it repeats it).
        private static int CountEntries(string file, Dictionary<string, FilePreParse> preParsed)
        {
            FilePreParse pre;
            if (preParsed == null || !preParsed.TryGetValue(file, out pre) || pre == null)
                return 0;
            if (IsBackupFile(file) && File.Exists(file.Substring(0, file.Length - ".jp".Length) + ".diff.lua"))
                return 0;
            int count = 0;
            foreach (SectionParse section in new[] { pre.Axis, pre.Keys })
            {
                if (section == null || !section.Found)
                    continue;
                foreach (List<AddedEntry> added in section.Added)
                    count += added.Count(a => !string.IsNullOrWhiteSpace(a.Key));
            }
            return count;
        }

        private static HashSet<string> BuildGuidlessDeviceFilterSet(IEnumerable<string> filters)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (filters == null)
                return output;

            foreach (string raw in filters)
            {
                string token = (raw ?? string.Empty).Trim();
                if (token.Length == 0 || token.Contains("{"))
                    continue;
                output.Add(token);
            }

            return output;
        }

        private static bool MatchesDeviceFilter(string device, HashSet<string> fullFilters, HashSet<string> guidlessFilters)
        {
            string value = (device ?? string.Empty).Trim();
            if (value.Length == 0)
                return false;
            if (fullFilters == null || fullFilters.Count == 0)
                return true;
            if (fullFilters.Contains(value))
                return true;
            if (guidlessFilters == null || guidlessFilters.Count == 0)
                return false;

            string baseName = StripDeviceGuidSuffix(value);
            return guidlessFilters.Contains(baseName);
        }

        private static string StripDeviceGuidSuffix(string device)
        {
            string value = (device ?? string.Empty).Trim();
            int split = value.LastIndexOf(" {", StringComparison.Ordinal);
            if (split > 0 && value.EndsWith("}", StringComparison.Ordinal))
                return value.Substring(0, split).Trim();
            return value;
        }

        private static bool IsEasyPlaneToken(string plane)
        {
            string token = (plane ?? string.Empty).Trim();
            return token.EndsWith("_easy", StringComparison.OrdinalIgnoreCase);
        }

        // "Include defaults": every bind DCS has active by default on an imported device, so that an import followed by a
        // clean + nuke export leaves exactly the binds the game had (docs/games/dcs.md "Import of the default binds").
        // Imported devices are the joysticks whose files the import reads (after the device filters, --only-connected
        // and the same-name merge) and the connected joysticks that pass the same filters (DCS gives them its defaults
        // without any file); per imported aircraft (an aircraft with a file of the user that passed the filters) each of
        // them gets the defaults DCS applies to it: the ones DCS ships for the device's model, else DCS's generic
        // joystick defaults (default.lua), and the keyboard's. All keys of an action with their modifiers, except:
        // - defaults the user's file removes (DCS writes a "removed" entry when a default is cleared or rebound);
        // - defaults of an action the user bound himself on that device in that aircraft (his binds replace them); for the
        //   keyboard, of an action he bound on any device there (the keyboard's other defaults stay in the game anyway);
        // - defaults on an input (key and modifiers) the user uses for another action on that device in that aircraft:
        //   the user's bind wins, so no input gets two relations because of a default;
        // - the second action of an input DCS ships for two on one device: one action per input, as in a profile;
        // - a keyboard default of an action a joystick got a default for in that aircraft (one is enough in the profile;
        //   the keyboard's default keys stay in the game anyway).
        // Aircraft without a file of the user are not added (nothing says he flies them).
        private static List<ParsedBind> AppendDefaultsFromCleanProfiles(
            List<ParsedBind> parsed,
            EngineDcsImportOptions options,
            EngineDcsImportResult result,
            DefaultsBasis basis)
        {
            Dictionary<string, ParsedBind> merged = new Dictionary<string, ParsedBind>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> ownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> ownInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < parsed.Count; i++)
            {
                ParsedBind bind = parsed[i];
                UpsertParsedBind(merged, bind);
                ownActions.Add(BuildPlaneDeviceIdKey(bind.Plane, bind.Device, bind.Id, bind.IsAxis));
                ownActions.Add(BuildPlaneDeviceIdKey(bind.Plane, string.Empty, bind.Id, bind.IsAxis));
                ownInputs.Add(DefaultInputKey(bind.Plane, bind.Device, bind.Key, bind.Reformers));
            }

            HashSet<string> matchedPlanes = new HashSet<string>(result.MatchedPlanes ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> matchedDevices = new HashSet<string>(result.MatchedDevices ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<DefaultBind>> defaultFiles = new Dictionary<string, List<DefaultBind>>(StringComparer.OrdinalIgnoreCase);
            // plane|device|input of the defaults added so far.
            HashSet<string> defaultOnInput = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // plane|id|axis of the joystick defaults added: the keyboard's default of such an action is left out.
            HashSet<string> joystickDefaultActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<DefaultsPair> pairs = basis == null ? new List<DefaultsPair>() : basis.Pairs.Values.ToList();
            AddDevicesWithoutFile(pairs, options, result);
            foreach (DefaultsPair pair in pairs
                .OrderBy(p => p.Plane, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => string.Equals(p.Device, "Keyboard", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(p => p.Device, StringComparer.OrdinalIgnoreCase))
            {
                bool keyboard = string.Equals(pair.Device, "Keyboard", StringComparison.OrdinalIgnoreCase);
                if (keyboard && !options.IncludeKeyboard)
                    continue;
                string defaultsFile = DefaultsProfilePath(pair.SourceFile, pair.Plane, keyboard);
                List<DefaultBind> defaults;
                if (!defaultFiles.TryGetValue(defaultsFile, out defaults))
                {
                    defaults = ParseDefaultFile(defaultsFile, result);
                    defaultFiles[defaultsFile] = defaults;
                }
                // DCS's generic joystick defaults (default.lua) are active on every device without defaults of its own:
                // imported like the others (counted apart).
                bool generic = !keyboard && string.Equals(Path.GetFullPath(defaultsFile), Path.GetFullPath(Path.Combine(GameDataPaths.CleanProfileFolder("DCS", false), pair.Plane + ".cf")), StringComparison.OrdinalIgnoreCase);

                Dictionary<string, string> modifierLookup = null;
                if (basis.ModifierLookupByPlane != null)
                    basis.ModifierLookupByPlane.TryGetValue(pair.Plane, out modifierLookup);
                foreach (DefaultBind entry in defaults)
                {
                    if (pair.RemovesDefault(entry.Id, entry.IsAxis, entry.Key, entry.Reformers))
                    {
                        result.DefaultEntriesRemovedByUser++;
                        continue;
                    }
                    // The keyboard is never taken over (its other default keys stay in the game anyway): a keyboard
                    // default of an action the user bound on any device of the aircraft is left out, as up to 2.4.3.
                    string actionKey = BuildPlaneDeviceIdKey(pair.Plane, keyboard ? string.Empty : pair.Device, entry.Id, entry.IsAxis);
                    if (ownActions.Contains(actionKey))
                    {
                        result.DefaultEntriesReplacedByOwnBinds++;
                        continue;
                    }
                    string planeActionKey = BuildPlaneDeviceIdKey(pair.Plane, string.Empty, entry.Id, entry.IsAxis);
                    if (keyboard && joystickDefaultActions.Contains(planeActionKey))
                    {
                        result.DefaultEntriesOnOtherDevice++;
                        continue;
                    }
                    List<string> reformers = NormalizeImportedReformers(entry.Reformers, modifierLookup);
                    string inputKey = DefaultInputKey(pair.Plane, pair.Device, entry.Key, reformers);
                    if (ownInputs.Contains(inputKey))
                    {
                        result.DefaultEntriesOnOwnInputs++;
                        continue;
                    }
                    // DCS ships a few inputs for two actions of one aircraft; a profile has one action per input and
                    // aircraft, so the first default keeps the input.
                    if (defaultOnInput.Contains(inputKey))
                    {
                        result.DefaultEntriesOnSharedInputs++;
                        continue;
                    }
                    defaultOnInput.Add(inputKey);
                    if (!keyboard)
                        joystickDefaultActions.Add(planeActionKey);
                    if (generic)
                        result.GenericJoystickDefaultsImported++;

                    ParsedBind bind = new ParsedBind();
                    bind.Id = entry.Id;
                    bind.Name = entry.Name;
                    bind.IsAxis = entry.IsAxis;
                    bind.Plane = pair.Plane;
                    bind.Device = pair.Device;
                    bind.Key = entry.Key;
                    bind.Reformers = reformers;
                    bind.RelationName = null;
                    bind.Groups = new List<string> { "DCS" };
                    bind.MetadataPriority = 0;
                    bind.SourceFile = string.Empty;
                    bind.IsDefault = true;
                    result.DefaultEntriesParsed++;
                    UpsertParsedBind(merged, bind);
                    matchedPlanes.Add(bind.Plane);
                    matchedDevices.Add(bind.Device);
                }
            }

            result.MatchedPlanes = matchedPlanes.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            result.MatchedDevices = matchedDevices.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

            return merged.Values
                .OrderBy(v => v.RelationName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Device ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Key ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Plane ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // The aircraft/device pairs DCS gives defaults to without a file of the user: every imported aircraft times every
        // imported joystick (read from files) and every connected joystick that passes the import's device filters.
        private static void AddDevicesWithoutFile(List<DefaultsPair> pairs, EngineDcsImportOptions options, EngineDcsImportResult result)
        {
            if (string.IsNullOrWhiteSpace(result.InputPath))
                return;
            HashSet<string> deviceFilter = new HashSet<string>(options.DeviceFilter ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> deviceFilterByName = BuildGuidlessDeviceFilterSet(deviceFilter);
            HashSet<string> ignored = new HashSet<string>(options.IgnoreDeviceFilter ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> ignoredByName = BuildGuidlessDeviceFilterSet(ignored);
            HashSet<string> devices = new HashSet<string>(
                (result.MatchedDevices ?? new List<string>()).Where(d => !string.Equals(d, "Keyboard", StringComparison.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (JoyPro.Engine.Games.GameDevice device in JoyPro.Engine.Games.GameDeviceDirectory.Discover(new List<string>()).Devices)
                {
                    string name = (device.JoyProName ?? string.Empty).Trim();
                    if (!device.Connected || name.Length == 0 || string.Equals(name, "Keyboard", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (deviceFilter.Count > 0 && !MatchesDeviceFilter(name, deviceFilter, deviceFilterByName))
                        continue;
                    if (ignored.Count > 0 && MatchesDeviceFilter(name, ignored, ignoredByName))
                        continue;
                    devices.Add(name);
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add("The connected devices could not be listed for their DCS defaults: " + ex.Message);
            }

            HashSet<string> known = new HashSet<string>(pairs.Select(p => p.Plane.Trim().ToLowerInvariant() + SignatureSeparator + p.Device.Trim().ToLowerInvariant()), StringComparer.Ordinal);
            foreach (string plane in (result.MatchedPlanes ?? new List<string>()).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                foreach (string device in devices.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    string key = plane.Trim().ToLowerInvariant() + SignatureSeparator + device.Trim().ToLowerInvariant();
                    if (!known.Add(key))
                        continue;
                    pairs.Add(new DefaultsPair
                    {
                        Plane = plane.Trim(),
                        Device = device.Trim(),
                        SourceFile = Path.Combine(result.InputPath, plane.Trim(), "joystick", device.Trim() + ".diff.lua")
                    });
                    result.DefaultDevicesWithoutFile++;
                }
            }
        }

        // An input of an aircraft and device as DCS tells inputs apart: key and the physical modifier keys (a modifier is
        // compared by its device and key, so "LCtrl" and a modifiers.lua "KeyboardLeftControl" on LCtrl are the same).
        private static string DefaultInputKey(string plane, string device, string key, List<string> reformers)
        {
            List<string> mods = new List<string>();
            foreach (string reformer in reformers ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(reformer))
                    continue;
                string[] parts = reformer.Split('§');
                mods.Add((parts.Length >= 3 ? parts[1].Trim() + SignatureSeparator + parts[2].Trim() : "Keyboard" + SignatureSeparator + parts[0].Trim()).ToLowerInvariant());
            }
            mods = mods.Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList();
            return (plane ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator +
                   (device ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator +
                   (key ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator +
                   string.Join("+", mods);
        }

        // One default bind of a clean profile file (the file "removes" every default of DCS; each removed key is one).
        private sealed class DefaultBind
        {
            public string Id;
            public string Name;
            public bool IsAxis;
            public string Key;
            public List<string> Reformers;
        }

        // An aircraft and device the user has an input file for, and the defaults that file removes.
        private sealed class DefaultsPair
        {
            public string Plane;
            public string Device;
            public string SourceFile;
            // "axis§id§key" -> the modifier sets of the removed entries (an empty set matches any).
            public Dictionary<string, List<List<string>>> Removed = new Dictionary<string, List<List<string>>>(StringComparer.OrdinalIgnoreCase);

            public bool RemovesDefault(string id, bool isAxis, string key, List<string> reformers)
            {
                List<List<string>> sets;
                if (!Removed.TryGetValue(RemovedKey(id, isAxis, key), out sets))
                    return false;
                List<string> wanted = NormalizeReformerNames(reformers);
                foreach (List<string> set in sets)
                {
                    if (set.Count == 0 || set.SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            public static string RemovedKey(string id, bool isAxis, string key)
            {
                return (isAxis ? "1" : "0") + SignatureSeparator + (id ?? string.Empty).Trim() + SignatureSeparator + (key ?? string.Empty).Trim();
            }
        }

        // What "include defaults" needs from the parse: the aircraft/device pairs and their removed defaults.
        private sealed class DefaultsBasis
        {
            public Dictionary<string, DefaultsPair> Pairs = new Dictionary<string, DefaultsPair>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, Dictionary<string, string>> ModifierLookupByPlane;

            public void Record(string plane, string device, string file, bool isBackup, SectionParse axis, SectionParse keys)
            {
                if (string.IsNullOrWhiteSpace(plane) || string.IsNullOrWhiteSpace(device))
                    return;
                string pairKey = plane.Trim().ToLowerInvariant() + SignatureSeparator + device.Trim().ToLowerInvariant();
                DefaultsPair pair;
                if (!Pairs.TryGetValue(pairKey, out pair))
                {
                    pair = new DefaultsPair { Plane = plane.Trim(), Device = device.Trim(), SourceFile = file };
                    Pairs[pairKey] = pair;
                }
                // DCS's own .diff.lua says what was removed; JoyPro's .jp copy does not count for that.
                if (isBackup)
                    return;
                if (IsBackupFile(pair.SourceFile ?? string.Empty))
                    pair.SourceFile = file;
                AddRemoved(pair, axis, true);
                AddRemoved(pair, keys, false);
            }

            private static void AddRemoved(DefaultsPair pair, SectionParse section, bool isAxis)
            {
                if (section == null || !section.Found)
                    return;
                for (int i = 0; i < section.Entries.Count && i < section.Removed.Count; i++)
                {
                    foreach (AddedEntry removed in section.Removed[i])
                    {
                        string key = DefaultsPair.RemovedKey(section.Entries[i].Id, isAxis, removed.Key);
                        List<List<string>> sets;
                        if (!pair.Removed.TryGetValue(key, out sets))
                        {
                            sets = new List<List<string>>();
                            pair.Removed[key] = sets;
                        }
                        sets.Add(NormalizeReformerNames(removed.Reformers));
                    }
                }
            }
        }

        private static List<DefaultBind> ParseDefaultFile(string filePath, EngineDcsImportResult result)
        {
            List<DefaultBind> output = new List<DefaultBind>();
            if (!File.Exists(filePath))
                return output;

            string text;
            try
            {
                text = RunFileCache.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Skipping unreadable clean profile file: " + filePath + " (" + ex.Message + ")");
                return output;
            }

            ParseDefaultSection(text, "axisDiffs", true, output);
            ParseDefaultSection(text, "keyDiffs", false, output);
            return output;
        }

        private static void ParseDefaultSection(string fileText, string sectionName, bool isAxis, List<DefaultBind> output)
        {
            string sectionBlock;
            if (!TryExtractNamedBlock(fileText, sectionName, out sectionBlock))
                return;

            foreach (SectionEntry entry in ParseTopLevelEntries(sectionBlock))
            {
                string displayName = ExtractEntryName(entry.Block);
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (AddedEntry removed in ExtractRemovedEntries(entry.Block))
                {
                    if (string.IsNullOrWhiteSpace(removed.Key))
                        continue;
                    List<string> reformers = (removed.Reformers ?? new List<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();
                    if (!seen.Add(removed.Key.Trim() + SignatureSeparator + string.Join("+", NormalizeReformerNames(reformers))))
                        continue;
                    output.Add(new DefaultBind
                    {
                        Id = entry.Id,
                        Name = string.IsNullOrWhiteSpace(displayName) ? entry.Id : displayName,
                        IsAxis = isAxis,
                        Key = removed.Key.Trim(),
                        Reformers = reformers
                    });
                }
            }
        }

        private static string BuildPlaneDeviceIdKey(string plane, string device, string id, bool isAxis)
        {
            return (plane ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator +
                   (device ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator +
                   (id ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator +
                   (isAxis ? "1" : "0");
        }

        private static void ParseInputFile(
            string filePath,
            bool isBackupFile,
            Dictionary<string, ParsedBind> parsedBySignature,
            Dictionary<string, ParsedFfbSetting> parsedFfbByDevice,
            Dictionary<string, Dictionary<string, string>> modifierLookupByPlane,
            Dictionary<string, EngineDcsFfbPlaneCorrection> ffbCorrections,
            EngineDcsImportResult result,
            Dictionary<string, FilePreParse> preParsed = null,
            DevicePlan devicePlan = null,
            DefaultsBasis defaultsBasis = null)
        {
            string text;
            try
            {
                text = RunFileCache.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Skipping unreadable file: " + filePath + " (" + ex.Message + ")");
                return;
            }

            string plane = ExtractPlaneName(filePath);
            string device = ExtractDeviceName(filePath);
            if (devicePlan != null)
                device = devicePlan.LiveName(device);
            Dictionary<string, string> modifierLookup = GetModifierLookupForPlane(filePath, plane, modifierLookupByPlane, result);
            if (devicePlan != null && devicePlan.HasRemaps)
                modifierLookup = devicePlan.RemapModifierLookup(plane, modifierLookup);
            FilePreParse pre = null;
            if (preParsed != null && preParsed.TryGetValue(filePath, out pre) && !ReferenceEquals(pre.Text, text))
                pre = null;
            HashSet<string> backupAllowed = pre != null ? pre.BackupAllowed : isBackupFile ? BuildBackupAllowedBinds(filePath, plane) : null;
            ParseSection(text, "axisDiffs", true, plane, device, filePath, isBackupFile, modifierLookup, parsedBySignature, result, backupAllowed, pre?.Axis, devicePlan);
            ParseSection(text, "keyDiffs", false, plane, device, filePath, isBackupFile, modifierLookup, parsedBySignature, result, backupAllowed, pre?.Keys, devicePlan);
            ParseForceFeedbackSection(text, plane, device, filePath, isBackupFile, ffbCorrections, parsedFfbByDevice, result);
            if (defaultsBasis != null)
                defaultsBasis.Record(plane, device, filePath, isBackupFile, pre?.Axis ?? PreParseSection(text, "axisDiffs"), pre?.Keys ?? PreParseSection(text, "keyDiffs"));
        }

        // The text work of one input file (sections, entries, binds, a backup's allowed binds), done for many files at
        // once before the files are merged one after another in their usual order (ParseAllInputFiles).
        private sealed class FilePreParse
        {
            public string Text;
            public SectionParse Axis;
            public SectionParse Keys;
            public HashSet<string> BackupAllowed;
        }

        private sealed class SectionParse
        {
            public bool Found;
            public List<SectionEntry> Entries = new List<SectionEntry>();
            public List<List<AddedEntry>> Added = new List<List<AddedEntry>>();
            public List<string> Names = new List<string>();
            public List<List<AddedEntry>> Removed = new List<List<AddedEntry>>();
        }

        private static SectionParse PreParseSection(string fileText, string sectionName)
        {
            SectionParse parse = new SectionParse();
            string sectionBlock;
            if (!TryExtractNamedBlock(fileText, sectionName, out sectionBlock))
                return parse;
            parse.Found = true;
            parse.Entries = ParseTopLevelEntries(sectionBlock);
            foreach (SectionEntry entry in parse.Entries)
            {
                parse.Added.Add(ExtractAddedEntries(entry.Block));
                parse.Names.Add(ExtractEntryName(entry.Block));
                parse.Removed.Add(ExtractRemovedEntries(entry.Block));
            }
            return parse;
        }

        private static Dictionary<string, FilePreParse> PreParse(List<string> files)
        {
            System.Collections.Concurrent.ConcurrentDictionary<string, FilePreParse> parsed =
                new System.Collections.Concurrent.ConcurrentDictionary<string, FilePreParse>(StringComparer.OrdinalIgnoreCase);
            System.Threading.Tasks.Parallel.ForEach(files, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(16, Environment.ProcessorCount)) }, file =>
            {
                try
                {
                    string text = RunFileCache.ReadAllText(file);
                    FilePreParse pre = new FilePreParse { Text = text };
                    pre.Axis = PreParseSection(text, "axisDiffs");
                    pre.Keys = PreParseSection(text, "keyDiffs");
                    if (IsBackupFile(file))
                        pre.BackupAllowed = BuildBackupAllowedBinds(file, ExtractPlaneName(file));
                    parsed[file] = pre;
                }
                catch (Exception)
                {
                    // The sequential pass reads and reports this file itself.
                }
            });
            return new Dictionary<string, FilePreParse>(parsed, StringComparer.OrdinalIgnoreCase);
        }

        private static void ParseSection(
            string fileText,
            string sectionName,
            bool isAxis,
            string plane,
            string device,
            string filePath,
            bool isBackupFile,
            Dictionary<string, string> modifierLookup,
            Dictionary<string, ParsedBind> parsedBySignature,
            EngineDcsImportResult result,
            HashSet<string> backupAllowed = null,
            SectionParse pre = null,
            DevicePlan devicePlan = null)
        {
            if (pre == null)
                pre = PreParseSection(fileText, sectionName);
            if (!pre.Found)
                return;

            List<SectionEntry> entries = pre.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                SectionEntry entry = entries[i];
                List<AddedEntry> added = pre.Added[i];
                if (backupAllowed != null)
                {
                    int before = added.Count;
                    added = added.Where(a => backupAllowed.Contains(BackupBindKey(entry.Id, isAxis, a.Key, a.Reformers)) ||
                                             backupAllowed.Contains(BackupBindKey(entry.Id, isAxis, a.Key, null))).ToList();
                    result.StaleBackupEntriesSkipped += before - added.Count;
                    if (before > 0 && added.Count == 0)
                        continue;
                }
                if (added.Count == 0)
                {
                    result.SkippedEntries++;
                    continue;
                }

                string displayName = pre.Names[i];
                for (int a = 0; a < added.Count; a++)
                {
                    AddedEntry addedEntry = added[a];
                    if (string.IsNullOrWhiteSpace(addedEntry.Key))
                    {
                        result.SkippedEntries++;
                        continue;
                    }

                    ParsedBind bind = new ParsedBind();
                    bind.Id = entry.Id;
                    bind.Name = string.IsNullOrWhiteSpace(displayName) ? entry.Id : displayName;
                    bind.IsAxis = isAxis;
                    bind.Plane = string.IsNullOrWhiteSpace(plane) ? "UnknownPlane" : plane;
                    bind.Device = string.IsNullOrWhiteSpace(device) ? "UnknownDevice" : device;
                    // JoyPro 1's key names (LeftShift, Oem102) and DCS's (LShift, OEM102) name one key: DCS's spelling.
                    bind.Key = KeyboardKeyNames.IsKeyboard(bind.Device) ? KeyboardKeyNames.ToDcs(addedEntry.Key) : addedEntry.Key.Trim();
                    bind.Reformers = NormalizeImportedReformers(addedEntry.Reformers, modifierLookup);
                    if (devicePlan != null && devicePlan.HasRemaps && bind.Reformers.Count > 0)
                        bind.Reformers = bind.Reformers.Select(devicePlan.RemapReformer).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    bind.RelationName = NormalizeOptionalString(addedEntry.RelationName);
                    bind.Groups = NormalizeGroups(addedEntry.Groups);
                    bind.HasInverted = addedEntry.HasInverted;
                    bind.Inverted = addedEntry.Inverted;
                    bind.HasSlider = addedEntry.HasSlider;
                    bind.Slider = addedEntry.Slider;
                    bind.HasDeadzone = addedEntry.HasDeadzone;
                    bind.Deadzone = addedEntry.Deadzone;
                    bind.Curvature = addedEntry.Curvature == null
                        ? new List<double>()
                        : new List<double>(addedEntry.Curvature);
                    bind.HasSaturationX = addedEntry.HasSaturationX;
                    bind.SaturationX = addedEntry.SaturationX;
                    bind.HasSaturationY = addedEntry.HasSaturationY;
                    bind.SaturationY = addedEntry.SaturationY;
                    bind.SourceFile = filePath;
                    bind.MetadataPriority = isBackupFile ? 2 : 1;

                    result.EntriesParsed++;
                    UpsertParsedBind(parsedBySignature, bind);
                }
            }
        }

        private static void ParseForceFeedbackSection(
            string fileText,
            string plane,
            string device,
            string filePath,
            bool isBackupFile,
            Dictionary<string, EngineDcsFfbPlaneCorrection> ffbCorrections,
            Dictionary<string, ParsedFfbSetting> parsedFfbByDevice,
            EngineDcsImportResult result)
        {
            if (parsedFfbByDevice == null)
                return;
            if (string.IsNullOrWhiteSpace(device))
                return;
            if (string.Equals(device.Trim(), "Keyboard", StringComparison.OrdinalIgnoreCase))
                return;

            string ffBlock;
            if (!TryExtractNamedBlock(fileText, "ffDiffs", out ffBlock))
                return;

            bool hasInvertX = CachedRegex.IsMatch(ffBlock, "\\[\"invertX\"\\]\\s*=", RegexOptions.IgnoreCase);
            bool hasInvertY = CachedRegex.IsMatch(ffBlock, "\\[\"invertY\"\\]\\s*=", RegexOptions.IgnoreCase);
            bool hasSwapAxes = CachedRegex.IsMatch(ffBlock, "\\[\"swapAxes\"\\]\\s*=", RegexOptions.IgnoreCase);
            bool hasSwapAxis = CachedRegex.IsMatch(ffBlock, "\\[\"swapAxis\"\\]\\s*=", RegexOptions.IgnoreCase);
            if (!hasInvertX && !hasInvertY && !hasSwapAxes && !hasSwapAxis)
                return;

            ParsedFfbSetting setting = new ParsedFfbSetting();
            setting.Device = device.Trim();
            setting.Plane = string.IsNullOrWhiteSpace(plane) ? "UnknownPlane" : plane.Trim();
            setting.InvertX = hasInvertX && ExtractNamedBool(ffBlock, "invertX", false);
            setting.InvertY = hasInvertY && ExtractNamedBool(ffBlock, "invertY", false);
            setting.SwapAxis =
                hasSwapAxes
                    ? ExtractNamedBool(ffBlock, "swapAxes", false)
                    : (hasSwapAxis && ExtractNamedBool(ffBlock, "swapAxis", false));
            setting.SourceFile = filePath ?? string.Empty;
            setting.MetadataPriority = isBackupFile ? 2 : 1;

            ApplyInverseFfbPlaneCorrection(setting, ffbCorrections);
            result.ForceFeedbackEntriesParsed++;
            UpsertParsedFfbSetting(parsedFfbByDevice, setting, result);
        }

        private static void ApplyInverseFfbPlaneCorrection(
            ParsedFfbSetting setting,
            Dictionary<string, EngineDcsFfbPlaneCorrection> ffbCorrections)
        {
            if (setting == null || ffbCorrections == null || ffbCorrections.Count == 0)
                return;
            if (!ffbCorrections.TryGetValue(setting.Plane ?? string.Empty, out EngineDcsFfbPlaneCorrection correction))
                return;
            if (correction == null)
                return;

            if (correction.InvertX)
                setting.InvertX = !setting.InvertX;
            if (correction.InvertY)
                setting.InvertY = !setting.InvertY;
            if (correction.SwapAxis)
                setting.SwapAxis = !setting.SwapAxis;
        }

        private static void UpsertParsedFfbSetting(
            Dictionary<string, ParsedFfbSetting> parsedFfbByDevice,
            ParsedFfbSetting incoming,
            EngineDcsImportResult result)
        {
            if (parsedFfbByDevice == null || incoming == null || string.IsNullOrWhiteSpace(incoming.Device))
                return;

            string key = incoming.Device.Trim();
            if (!parsedFfbByDevice.TryGetValue(key, out ParsedFfbSetting existing))
            {
                parsedFfbByDevice[key] = incoming;
                return;
            }

            if (incoming.MetadataPriority > existing.MetadataPriority)
            {
                parsedFfbByDevice[key] = incoming;
                return;
            }

            if (incoming.MetadataPriority == existing.MetadataPriority && !SameForceFeedback(existing, incoming))
            {
                result.Warnings.Add(
                    "Conflicting DCS ffDiffs for device '" + key + "': " +
                    existing.SourceFile + " vs " + incoming.SourceFile + ". Keeping first value.");
            }
        }

        private static bool SameForceFeedback(ParsedFfbSetting left, ParsedFfbSetting right)
        {
            if (left == null || right == null)
                return false;
            return left.InvertX == right.InvertX &&
                   left.InvertY == right.InvertY &&
                   left.SwapAxis == right.SwapAxis;
        }

        // One bind found in several files of a plane: the .diff.lua (priority 1) is what DCS uses, so its axis filter wins,
        // also when it was changed in DCS after JoyPro's export; the .jp backup (priority 2) adds JoyPro's relation name
        // and groups. Without a diff entry the highest priority gives the filter, and lower ones only fill gaps.
        private static void UpsertParsedBind(Dictionary<string, ParsedBind> parsedBySignature, ParsedBind incoming)
        {
            string signature = BuildParsedSignature(incoming);
            if (!parsedBySignature.ContainsKey(signature))
            {
                incoming.FilterFromDiff = incoming.MetadataPriority == 1;
                parsedBySignature.Add(signature, incoming);
                return;
            }

            ParsedBind existing = parsedBySignature[signature];
            if (string.IsNullOrWhiteSpace(existing.Name) && !string.IsNullOrWhiteSpace(incoming.Name))
                existing.Name = incoming.Name;

            bool higher = incoming.MetadataPriority >= existing.MetadataPriority;
            if (higher)
            {
                if (!string.IsNullOrWhiteSpace(incoming.RelationName))
                    existing.RelationName = incoming.RelationName;
                if (incoming.Groups != null && incoming.Groups.Count > 0)
                    existing.Groups = new List<string>(incoming.Groups);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(existing.RelationName) && !string.IsNullOrWhiteSpace(incoming.RelationName))
                    existing.RelationName = incoming.RelationName;
                if ((existing.Groups == null || existing.Groups.Count == 0) && incoming.Groups != null && incoming.Groups.Count > 0)
                    existing.Groups = new List<string>(incoming.Groups);
            }

            if (incoming.MetadataPriority == 1)
            {
                CopyFilter(existing, incoming);
                existing.FilterFromDiff = true;
            }
            else if (higher && !existing.FilterFromDiff)
                CopyFilter(existing, incoming);
            else if (!existing.FilterFromDiff)
                FillFilterGaps(existing, incoming);

            if (higher)
                existing.MetadataPriority = incoming.MetadataPriority;
        }

        private static void CopyFilter(ParsedBind target, ParsedBind source)
        {
            target.HasInverted = source.HasInverted;
            target.Inverted = source.Inverted;
            target.HasSlider = source.HasSlider;
            target.Slider = source.Slider;
            target.HasDeadzone = source.HasDeadzone;
            target.Deadzone = source.Deadzone;
            target.Curvature = source.Curvature == null ? new List<double>() : new List<double>(source.Curvature);
            target.HasSaturationX = source.HasSaturationX;
            target.SaturationX = source.SaturationX;
            target.HasSaturationY = source.HasSaturationY;
            target.SaturationY = source.SaturationY;
        }

        private static void FillFilterGaps(ParsedBind existing, ParsedBind incoming)
        {
            if (!existing.HasInverted && incoming.HasInverted)
            {
                existing.HasInverted = true;
                existing.Inverted = incoming.Inverted;
            }
            if (!existing.HasSlider && incoming.HasSlider)
            {
                existing.HasSlider = true;
                existing.Slider = incoming.Slider;
            }
            if (!existing.HasDeadzone && incoming.HasDeadzone)
            {
                existing.HasDeadzone = true;
                existing.Deadzone = incoming.Deadzone;
            }
            if ((existing.Curvature == null || existing.Curvature.Count == 0) &&
                incoming.Curvature != null &&
                incoming.Curvature.Count > 0)
            {
                existing.Curvature = new List<double>(incoming.Curvature);
            }
            if (!existing.HasSaturationX && incoming.HasSaturationX)
            {
                existing.HasSaturationX = true;
                existing.SaturationX = incoming.SaturationX;
            }
            if (!existing.HasSaturationY && incoming.HasSaturationY)
            {
                existing.HasSaturationY = true;
                existing.SaturationY = incoming.SaturationY;
            }
        }

        private static void ApplyImport(
            List<ParsedBind> parsed,
            Dictionary<string, ParsedFfbSetting> parsedFfbByDevice,
            bool merge,
            EngineDcsImportOptions options,
            EngineDcsImportResult result)
        {
            Dictionary<string, Relation> relations = merge
                ? new Dictionary<string, Relation>(InternalDataManagement.AllRelations, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Relation>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Bind> binds = merge
                ? new Dictionary<string, Bind>(InternalDataManagement.AllBinds, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase);

            // The profile's relations before this import (merge): the ones a bind on the same input joins.
            HashSet<string> preexisting = merge ? new HashSet<string>(relations.Keys, StringComparer.OrdinalIgnoreCase) : null;
            Dictionary<string, int> nextSuffix = new Dictionary<string, int>(StringComparer.Ordinal);
            // (requested name, device, input, axis, modifiers) -> the relation that has that bind: the base relation or
            // a "<name>__N" copy made before (earlier in this import, or by an earlier import when merging). One lookup
            // per entry; without it every further aircraft of a bind that differs from the base made one more copy.
            Dictionary<string, Relation> family = BuildFamilyLookup(relations, binds);
            // (action title, device, input, modifiers, axis) -> a relation of this import with it: a default joins the
            // relation of the same function and input of another aircraft (the user's or another default's), like the
            // user's own relations hold one function across aircraft, instead of one relation per aircraft's action id.
            Dictionary<string, Relation> byTitle = new Dictionary<string, Relation>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < parsed.Count; i++)
            {
                ParsedBind parsedBind = parsed[i];
                if (string.IsNullOrWhiteSpace(parsedBind.Id) || string.IsNullOrWhiteSpace(parsedBind.Key))
                {
                    result.SkippedEntries++;
                    continue;
                }

                string titleKey = string.IsNullOrWhiteSpace(parsedBind.Name)
                    ? null
                    : FamilyKey(parsedBind.Name.Trim(), parsedBind.Device, parsedBind.Key, parsedBind.IsAxis, parsedBind.Reformers);
                Relation sameFunction;
                Bind sameFunctionBind;
                if (parsedBind.IsDefault && parsedBind.RelationName == null && titleKey != null && byTitle.TryGetValue(titleKey, out sameFunction) &&
                    relations.TryGetValue(sameFunction.NAME ?? string.Empty, out Relation sameStillThere) && ReferenceEquals(sameStillThere, sameFunction) &&
                    binds.TryGetValue(sameFunction.NAME, out sameFunctionBind) && sameFunctionBind != null && sameFunction.ISAXIS == parsedBind.IsAxis)
                {
                    MergeIntoExistingRelation(parsedBind, sameFunction, sameFunctionBind, options);
                    continue;
                }

                string preferredName = parsedBind.IsDefault && parsedBind.RelationName == null ? BuildDefaultRelationName(parsedBind) : BuildRequestedRelationName(parsedBind);
                string relationName = preferredName;
                string familyKey = FamilyKey(preferredName, parsedBind.Device, parsedBind.Key, parsedBind.IsAxis, parsedBind.Reformers);

                Relation familyRelation;
                Bind familyBind;
                if (family.TryGetValue(familyKey, out familyRelation) && familyRelation != null &&
                    relations.TryGetValue(familyRelation.NAME ?? string.Empty, out Relation stillThere) && ReferenceEquals(stillThere, familyRelation) &&
                    binds.TryGetValue(familyRelation.NAME, out familyBind) && familyBind != null && familyRelation.ISAXIS == parsedBind.IsAxis)
                {
                    MergeIntoExistingRelation(parsedBind, familyRelation, familyBind, options);
                    if (titleKey != null && !byTitle.ContainsKey(titleKey))
                        byTitle[titleKey] = familyRelation;
                    continue;
                }

                if (TryGetCompatibleExistingRelation(relationName, parsedBind, relations, binds, out Relation existingRelation, out Bind existingBind))
                {
                    MergeIntoExistingRelation(parsedBind, existingRelation, existingBind, options);
                    family[familyKey] = existingRelation;
                    if (titleKey != null && !byTitle.ContainsKey(titleKey))
                        byTitle[titleKey] = existingRelation;
                    continue;
                }

                // Adding to the profile: a bind on an input one of the profile's relations already uses joins that
                // relation (also when it holds the bind already) instead of becoming a second relation on the input.
                if (preexisting != null && !parsedBind.IsDefault)
                {
                    string joinName = RelationToJoin(relations, binds, preexisting, parsedBind.Device, parsedBind.IsAxis, parsedBind.Key, parsedBind.Reformers,
                        "DCS", new[] { parsedBind.Plane }, id => string.Equals(id, (parsedBind.Id ?? string.Empty).Trim(), StringComparison.Ordinal), parsedBind.Name);
                    Relation joined;
                    Bind joinedBind;
                    if (joinName != null && relations.TryGetValue(joinName, out joined) && joined != null && binds.TryGetValue(joinName, out joinedBind) && joinedBind != null)
                    {
                        MergeIntoExistingRelation(parsedBind, joined, joinedBind, options, addGroups: false);
                        family[familyKey] = joined;
                        if (titleKey != null && !byTitle.ContainsKey(titleKey))
                            byTitle[titleKey] = joined;
                        result.JoinedExistingRelations++;
                        continue;
                    }
                }

                if (relations.ContainsKey(relationName))
                    relationName = BuildUniqueRelationNameFromBase(preferredName, relations, nextSuffix);

                Relation relation = CreateRelation(parsedBind, relationName);
                Bind bind = CreateBind(parsedBind, relation, options);
                relation.bind = bind;

                relations[relationName] = relation;
                binds[relationName] = bind;
                family[familyKey] = relation;
                if (titleKey != null && !byTitle.ContainsKey(titleKey))
                    byTitle[titleKey] = relation;
                result.ImportedRelations++;
                result.ImportedBinds++;

                int done = i + 1;
                if (done == parsed.Count || done % 250 == 0)
                {
                    ReportStatus(
                        options,
                        "Applied " +
                        done.ToString() +
                        "/" +
                        parsed.Count.ToString() +
                        " parsed entries.");
                }
            }

            Dictionary<string, ForceFeedbackS> ffb = merge
                ? new Dictionary<string, ForceFeedbackS>(InternalDataManagement.JoystickFFB, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ForceFeedbackS>(StringComparer.OrdinalIgnoreCase);
            if (parsedFfbByDevice != null)
            {
                foreach (KeyValuePair<string, ParsedFfbSetting> entry in parsedFfbByDevice)
                {
                    if (entry.Value == null || string.IsNullOrWhiteSpace(entry.Key))
                        continue;

                    ForceFeedbackS setting = new ForceFeedbackS();
                    setting.invertX = entry.Value.InvertX;
                    setting.invertY = entry.Value.InvertY;
                    setting.swapAxis = entry.Value.SwapAxis;
                    ffb[entry.Key.Trim()] = setting;
                }
            }

            InternalDataManagement.AllRelations = relations;
            InternalDataManagement.AllBinds = binds;
            InternalDataManagement.JoystickFFB = ffb;
            result.ForceFeedbackDevicesImported = parsedFfbByDevice == null ? 0 : parsedFfbByDevice.Count;
            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
        }

        private static readonly Regex CopySuffixRegex = new Regex(@"^(?<base>.+?)__(?<n>\d+)$", RegexOptions.Compiled);

        // "<name>__N" -> "<name>" (a copy the import made because the name was taken); other names as they are.
        internal static string StripCopySuffix(string relationName)
        {
            string name = (relationName ?? string.Empty).Trim();
            Match m = CopySuffixRegex.Match(name);
            return m.Success ? m.Groups["base"].Value : name;
        }

        internal static bool IsCopyName(string relationName)
        {
            return CopySuffixRegex.IsMatch((relationName ?? string.Empty).Trim());
        }

        // A relation's bind as the import compares it: name of the relation family, device, input, axis/button and the
        // modifier names (SameReformers compares names too).
        internal static string FamilyKey(string requestedName, string device, string input, bool isAxis, List<string> reformers)
        {
            return (requestedName ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator +
                   (device ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator +
                   (input ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator +
                   (isAxis ? "1" : "0") + SignatureSeparator +
                   string.Join(SignatureSeparator, NormalizeReformerNames(reformers)).ToLowerInvariant();
        }

        private static Dictionary<string, Relation> BuildFamilyLookup(Dictionary<string, Relation> relations, Dictionary<string, Bind> binds)
        {
            Dictionary<string, Relation> family = new Dictionary<string, Relation>(StringComparer.Ordinal);
            if (relations.Count == 0)
                return family;
            // Base names first, so a base relation wins over its copies; then the copies in name order.
            foreach (KeyValuePair<string, Relation> entry in relations.OrderBy(r => IsCopyName(r.Key) ? 1 : 0).ThenBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
            {
                Bind bind;
                if (entry.Value == null || !binds.TryGetValue(entry.Key, out bind) || bind == null)
                    continue;
                string input = entry.Value.ISAXIS ? bind.JAxis : bind.JButton;
                if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(bind.Joystick))
                    continue;
                string key = FamilyKey(StripCopySuffix(entry.Key), bind.Joystick, input, entry.Value.ISAXIS, bind.AllReformers);
                if (!family.ContainsKey(key))
                    family[key] = entry.Value;
                string own = FamilyKey(entry.Key, bind.Joystick, input, entry.Value.ISAXIS, bind.AllReformers);
                if (!family.ContainsKey(own))
                    family[own] = entry.Value;
            }
            return family;
        }

        private static bool TryGetCompatibleExistingRelation(
            string relationName,
            ParsedBind parsedBind,
            Dictionary<string, Relation> relations,
            Dictionary<string, Bind> binds,
            out Relation relation,
            out Bind bind)
        {
            relation = null;
            bind = null;

            if (string.IsNullOrWhiteSpace(relationName))
                return false;
            if (!relations.ContainsKey(relationName) || !binds.ContainsKey(relationName))
                return false;

            relation = relations[relationName];
            bind = binds[relationName];
            if (relation == null || bind == null)
                return false;

            if (relation.ISAXIS != parsedBind.IsAxis)
                return false;
            if (!string.Equals(bind.Joystick ?? string.Empty, parsedBind.Device ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                return false;

            string bindInput = parsedBind.IsAxis ? (bind.JAxis ?? string.Empty) : (bind.JButton ?? string.Empty);
            if (!string.Equals(bindInput, parsedBind.Key, StringComparison.OrdinalIgnoreCase))
                return false;

            return SameReformers(bind.AllReformers, parsedBind.Reformers);
        }

        // The relation a bind added to the profile joins (DCS import "add to my profile", taking over a game's change, moving
        // an action to another button): a relation that already uses exactly this input with these modifiers, unless the
        // aircraft is bound there to another action of the game already. Of several, the one that holds the action (for
        // other aircraft or this one) or is named like it, else the one with the most aircraft. In JoyPro a relation is one
        // input, so a new bind on a used button belongs to that button's relation (tester report on 2.4.1). null: none.
        internal static string RelationToJoin(
            Dictionary<string, Relation> relations,
            Dictionary<string, Bind> binds,
            ICollection<string> onlyThese,
            string device,
            bool isAxis,
            string input,
            IEnumerable<string> reformers,
            string gameId,
            IEnumerable<string> planes,
            Func<string, bool> isThisAction,
            string title,
            string except = null)
        {
            if (relations == null || string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(input) || isThisAction == null)
                return null;
            string signature = Games.GameProfileBridge.BuildSignature(device, isAxis, input, reformers);
            string game = string.IsNullOrWhiteSpace(gameId) ? "DCS" : gameId.Trim();
            bool dcs = string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase);
            string cleanTitle = (title ?? string.Empty).Trim();
            List<string> forPlanes = (planes ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
            if (forPlanes.Count == 0)
                forPlanes.Add(game);
            string best = null;
            int bestRank = -1;
            int bestPlanes = -1;
            foreach (KeyValuePair<string, Relation> entry in relations.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
            {
                Relation relation = entry.Value;
                if (relation == null || relation.ISAXIS != isAxis || (onlyThese != null && !onlyThese.Contains(entry.Key)) ||
                    (except != null && string.Equals(entry.Key, except, StringComparison.OrdinalIgnoreCase)))
                    continue;
                Bind bind = relation.bind;
                if ((bind == null || string.IsNullOrWhiteSpace(bind.Joystick)) && binds != null)
                    binds.TryGetValue(entry.Key, out bind);
                string bound = bind == null ? null : (isAxis ? bind.JAxis : bind.JButton);
                if (bind == null || string.IsNullOrWhiteSpace(bind.Joystick) || string.IsNullOrWhiteSpace(bound) ||
                    !string.Equals(Games.GameProfileBridge.BuildSignature(bind.Joystick, isAxis, bound, bind.AllReformers), signature, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool holdsHere = false, holdsElsewhere = false, blocked = false;
                HashSet<string> relationPlanes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
                {
                    if (node == null || string.IsNullOrWhiteSpace(node.ID))
                        continue;
                    foreach (KeyValuePair<string, bool> p in node.AIRCRAFT ?? new Dictionary<string, bool>())
                        if (p.Value && !string.IsNullOrWhiteSpace(p.Key))
                            relationPlanes.Add(p.Key.Trim());
                    if (!SameGame(node.Game, game))
                        continue;
                    bool active = forPlanes.Any(plane => NodeActiveFor(node, game, plane, dcs));
                    bool same = isThisAction(node.ID.Trim());
                    if (same && active)
                        holdsHere = true;
                    else if (same)
                        holdsElsewhere = true;
                    else if (active)
                        blocked = true;
                }
                if (blocked && !holdsHere)
                    continue;
                bool named = cleanTitle.Length > 0 &&
                             (string.Equals(StripCopySuffix(entry.Key), cleanTitle, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals((bind.AdditionalImportInfo ?? string.Empty).Trim(), cleanTitle, StringComparison.OrdinalIgnoreCase));
                int rank = holdsHere ? 3 : holdsElsewhere ? 2 : named ? 1 : 0;
                if (rank > bestRank || (rank == bestRank && relationPlanes.Count > bestPlanes))
                {
                    best = entry.Key;
                    bestRank = rank;
                    bestPlanes = relationPlanes.Count;
                }
            }
            return best;
        }

        private static bool SameGame(string nodeGame, string gameId)
        {
            string value = string.IsNullOrWhiteSpace(nodeGame) ? "DCS" : nodeGame.Trim();
            if (string.Equals(value, gameId, StringComparison.OrdinalIgnoreCase))
                return true;
            string resolved = GameInstalls.Resolve(value);
            return resolved.Length > 0 && string.Equals(resolved, gameId, StringComparison.OrdinalIgnoreCase);
        }

        // DCS: switched on for the aircraft; other games: for the game (no list = all aircraft).
        private static bool NodeActiveFor(RelationItem node, string gameId, string plane, bool dcs)
        {
            bool value;
            if (dcs)
                return node.AIRCRAFT != null && !string.IsNullOrWhiteSpace(plane) && node.AIRCRAFT.TryGetValue(plane.Trim(), out value) && value;
            if (node.AIRCRAFT == null || node.AIRCRAFT.Count == 0)
                return true;
            if (!string.IsNullOrWhiteSpace(plane) && node.AIRCRAFT.TryGetValue(plane.Trim(), out value))
                return value;
            if (node.AIRCRAFT.TryGetValue(gameId, out value))
                return value;
            return node.AIRCRAFT.Values.Any(v => v);
        }

        private static void MergeIntoExistingRelation(ParsedBind parsedBind, Relation relation, Bind bind, EngineDcsImportOptions options, bool addGroups = true)
        {
            relation.AddNode(parsedBind.Id, "DCS", parsedBind.IsAxis, parsedBind.Plane, false);

            // This plane's axis filter: if it differs from the relation's (the first plane's), it becomes the
            // relation's tuning for this aircraft instead of being dropped.
            if (parsedBind.IsAxis && bind != null)
            {
                Bind planeBind = new Bind();
                ApplyAxisImportSettings(planeBind, parsedBind, options);
                JoyPro.Engine.Axis.AxisTuning planeTuning = JoyPro.Engine.Axis.BindTuning.Base(planeBind);
                if (planeTuning.Format() != JoyPro.Engine.Axis.BindTuning.Base(bind).Format())
                    JoyPro.Engine.Axis.BindTuning.Set(bind, JoyPro.Engine.Axis.BindTuning.PlaneScope("DCS", parsedBind.Plane), planeTuning);
            }

            List<string> groups = NormalizeGroups(parsedBind.Groups);
            // A relation JoyPro exported without groups (its .jp names it) stays without; others get "DCS". A relation of
            // the user's that the bind joined because it uses the same input keeps its groups.
            if (!addGroups)
                groups.Clear();
            else if (groups.Count == 0 && string.IsNullOrWhiteSpace(parsedBind.RelationName))
                AddUniqueGroup(relation.Groups, "DCS");
            if (groups.Count > 0)
            {
                for (int i = 0; i < groups.Count; i++)
                    AddUniqueGroup(relation.Groups, groups[i]);
            }

            if (string.IsNullOrWhiteSpace(bind.AdditionalImportInfo) && !string.IsNullOrWhiteSpace(parsedBind.Name))
                bind.AdditionalImportInfo = parsedBind.Name;
        }

        private static Relation CreateRelation(ParsedBind parsedBind, string relationName)
        {
            Relation relation = new Relation();
            relation.NAME = relationName;
            relation.ISAXIS = parsedBind.IsAxis;
            relation.Groups = NormalizeGroups(parsedBind.Groups);
            if (relation.Groups.Count == 0 && string.IsNullOrWhiteSpace(parsedBind.RelationName))
                relation.Groups.Add("DCS");
            relation.NODES = new List<RelationItem>();
            relation.AddNode(parsedBind.Id, "DCS", parsedBind.IsAxis, parsedBind.Plane, false);
            return relation;
        }

        private static Bind CreateBind(ParsedBind parsedBind, Relation relation, EngineDcsImportOptions options)
        {
            Bind bind = new Bind(relation);
            bind.Joystick = parsedBind.Device;
            bind.JoyBackup = parsedBind.Device;
            if (parsedBind.IsAxis)
            {
                bind.JAxis = parsedBind.Key;
                bind.JButton = string.Empty;
            }
            else
            {
                bind.JButton = parsedBind.Key;
                bind.JAxis = string.Empty;
            }
            bind.AdditionalImportInfo = parsedBind.Name;
            bind.AllReformers = parsedBind.Reformers == null
                ? new List<string>()
                : new List<string>(parsedBind.Reformers);
            ApplyAxisImportSettings(bind, parsedBind, options);
            return bind;
        }

        private static void ApplyAxisImportSettings(Bind bind, ParsedBind parsedBind, EngineDcsImportOptions options)
        {
            if (bind == null || parsedBind == null || !parsedBind.IsAxis)
                return;

            EngineDcsImportOptions effective = options ?? new EngineDcsImportOptions();
            // A bind JoyPro exported itself (its .jp backup names the relation) carries JoyPro's own filter: that is
            // restored whatever the import options say; the options only decide about filters made in DCS.
            bool own = parsedBind.MetadataPriority >= 2 && !string.IsNullOrWhiteSpace(parsedBind.RelationName);
            bind.Inverted = (own || effective.ImportInverted) && parsedBind.HasInverted
                ? parsedBind.Inverted
                : Bind.Inverted_Default;
            bind.Slider = (own || effective.ImportSlider) && parsedBind.HasSlider
                ? parsedBind.Slider
                : Bind.Slider_Default;
            bind.Deadzone = (own || effective.ImportDeadzone) && parsedBind.HasDeadzone
                ? parsedBind.Deadzone
                : Bind.Deadzone_Default;
            bind.Curvature = (own || effective.ImportCurvature) && parsedBind.Curvature != null && parsedBind.Curvature.Count > 0
                ? new List<double>(parsedBind.Curvature)
                : new List<double>(Bind.Curvature_Default);
            bind.SaturationX = (own || effective.ImportSaturationX) && parsedBind.HasSaturationX
                ? parsedBind.SaturationX
                : Bind.SaturationX_Default;
            bind.SaturationY = (own || effective.ImportSaturationY) && parsedBind.HasSaturationY
                ? parsedBind.SaturationY
                : Bind.SaturationY_Default;
        }

        private static bool IsSupportedInputFile(string filePath)
        {
            DirectoryInfo deviceDir = new DirectoryInfo(Path.GetDirectoryName(filePath) ?? string.Empty);
            string deviceFolder = deviceDir.Name;
            return string.Equals(deviceFolder, "joystick", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(deviceFolder, "keyboard", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSupportedInputExtension(string filePath)
        {
            return filePath.EndsWith(".diff.lua", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".jp", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBackupFile(string filePath)
        {
            return filePath.EndsWith(".jp", StringComparison.OrdinalIgnoreCase);
        }

        // The .jp is JoyPro's copy of what it last wrote; the .diff.lua is what DCS uses. When both exist, a .jp bind
        // counts only if the diff has it (the .jp adds relation names and groups) or if it is a default of the device
        // the diff does not remove (DCS drops binds equal to a default when it saves: what counts is default + diff, not
        // the diff alone). Other .jp binds were removed in DCS.
        // Returns null when there is no diff: then the .jp is all there is.
        private static HashSet<string> BuildBackupAllowedBinds(string backupPath, string plane)
        {
            string diffPath = backupPath.Substring(0, backupPath.Length - ".jp".Length) + ".diff.lua";
            if (!File.Exists(diffPath))
                return null;

            string diffText;
            try
            {
                diffText = RunFileCache.ReadAllText(diffPath);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            HashSet<string> allowed = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> removed = new HashSet<string>(StringComparer.Ordinal);
            CollectBackupBindKeys(diffText, allowed, removed);

            bool keyboard = string.Equals(new DirectoryInfo(Path.GetDirectoryName(backupPath) ?? string.Empty).Name, "keyboard", StringComparison.OrdinalIgnoreCase);
            string cleanPath = DefaultsProfilePath(backupPath, plane, keyboard);
            if (File.Exists(cleanPath))
            {
                HashSet<string> defaults = new HashSet<string>(StringComparer.Ordinal);
                HashSet<string> unused = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    CollectBackupBindKeys(RunFileCache.ReadAllText(cleanPath), unused, defaults);
                }
                catch (IOException)
                {
                }
                foreach (string bind in defaults)
                {
                    if (!removed.Contains(bind))
                        allowed.Add(bind);
                }
            }
            return allowed;
        }

        // The defaults DCS applies to the device of an input file, as the DCS list refresh stored them: a device type
        // with defaults of its own (a "<type>.lua" of the plane, or an entry of DefaultAssignments.lua such as the
        // T-Pendular-Rudder's rudder on JOY_Z) has CleanProfile\DCS\<plane>\<type>.cf; every other device uses the
        // plane's generic <plane>.cf (default.lua). DCS keeps the spaces at the ends of a device name, the type is
        // matched without them.
        internal static string DefaultsProfilePath(string inputFile, string plane, bool keyboard)
        {
            string root = GameDataPaths.CleanProfileFolder("DCS", keyboard);
            string generic = Path.Combine(root, plane + ".cf");
            if (keyboard)
                return generic;
            string name = Path.GetFileName(inputFile ?? string.Empty);
            if (name.EndsWith(".diff.lua", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ".diff.lua".Length);
            else if (name.EndsWith(".jp", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ".jp".Length);
            string template = DcsDeviceNames.TemplateName(name).Trim();
            if (template.Length == 0)
                return generic;
            try
            {
                string folder = Path.Combine(root, plane);
                if (!Directory.Exists(folder))
                    return generic;
                foreach (string file in RunFileCache.ListFiles(folder, "*.cf"))
                {
                    string type = Path.GetFileName(file);
                    type = type.Substring(0, type.Length - ".cf".Length);
                    if (string.Equals(type.Trim(), template, StringComparison.Ordinal))
                        return file;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
            }
            return generic;
        }

        // Adds every added bind of a diff text to added and every removed bind to removed (removed without reformers).
        private static void CollectBackupBindKeys(string text, HashSet<string> added, HashSet<string> removed)
        {
            foreach (bool isAxis in new[] { true, false })
            {
                string sectionBlock;
                if (!TryExtractNamedBlock(text, isAxis ? "axisDiffs" : "keyDiffs", out sectionBlock))
                    continue;
                foreach (SectionEntry entry in ParseTopLevelEntries(sectionBlock))
                {
                    foreach (AddedEntry add in ExtractAddedEntries(entry.Block))
                        added.Add(BackupBindKey(entry.Id, isAxis, add.Key, add.Reformers));
                    foreach (AddedEntry remove in ExtractRemovedEntries(entry.Block))
                        removed.Add(BackupBindKey(entry.Id, isAxis, remove.Key, null));
                }
            }
        }

        private static string BackupBindKey(string id, bool isAxis, string key, List<string> reformers)
        {
            string reformerPart = string.Join(
                SignatureSeparator,
                (reformers ?? new List<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(v => v, StringComparer.Ordinal));
            return (id ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator + (isAxis ? "1" : "0") + SignatureSeparator +
                   (key ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator + reformerPart;
        }

        private static string ExtractPlaneName(string filePath)
        {
            try
            {
                DirectoryInfo deviceDir = new DirectoryInfo(Path.GetDirectoryName(filePath) ?? string.Empty);
                DirectoryInfo planeDir = deviceDir.Parent;
                if (planeDir == null)
                    return "UnknownPlane";
                return planeDir.Name;
            }
            catch
            {
                return "UnknownPlane";
            }
        }

        private static string ExtractDeviceName(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            if (fileName.EndsWith(".diff.lua", StringComparison.OrdinalIgnoreCase))
                fileName = fileName.Substring(0, fileName.Length - ".diff.lua".Length);
            else if (fileName.EndsWith(".jp", StringComparison.OrdinalIgnoreCase))
                fileName = fileName.Substring(0, fileName.Length - ".jp".Length);

            DirectoryInfo deviceDir = new DirectoryInfo(Path.GetDirectoryName(filePath) ?? string.Empty);
            if (string.Equals(deviceDir.Name, "keyboard", StringComparison.OrdinalIgnoreCase))
                return "Keyboard";

            // DCS keeps the spaces of the DirectInput name (" VKB-Sim ...  {GUID}"); JoyPro's device names trim them.
            return DcsDeviceNames.JoyProName(fileName);
        }

        private static Dictionary<string, string> GetModifierLookupForPlane(
            string filePath,
            string plane,
            Dictionary<string, Dictionary<string, string>> modifierLookupByPlane,
            EngineDcsImportResult result)
        {
            if (modifierLookupByPlane.ContainsKey(plane))
                return modifierLookupByPlane[plane];

            Dictionary<string, string> empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                DirectoryInfo deviceDir = new DirectoryInfo(Path.GetDirectoryName(filePath) ?? string.Empty);
                DirectoryInfo planeDir = deviceDir.Parent;
                if (planeDir == null)
                {
                    modifierLookupByPlane[plane] = empty;
                    return empty;
                }

                string modifiersPath = Path.Combine(planeDir.FullName, "modifiers.lua");
                if (!File.Exists(modifiersPath))
                {
                    modifierLookupByPlane[plane] = empty;
                    return empty;
                }

                string text = RunFileCache.ReadAllText(modifiersPath);
                Dictionary<string, string> parsed = ParseModifierLookup(text);
                modifierLookupByPlane[plane] = parsed;
                return parsed;
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Failed to read modifiers.lua for plane '" + plane + "': " + ex.Message);
                modifierLookupByPlane[plane] = empty;
                return empty;
            }
        }

        private static Dictionary<string, string> ParseModifierLookup(string text)
        {
            Dictionary<string, string> lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text))
                return lookup;

            int openBrace = text.IndexOf('{');
            if (openBrace < 0)
                return lookup;

            int closeBrace;
            if (!TryFindMatchingBrace(text, openBrace, out closeBrace))
                return lookup;

            string tableBlock = text.Substring(openBrace, closeBrace - openBrace + 1);
            List<SectionEntry> entries = ParseTopLevelEntries(tableBlock);
            for (int i = 0; i < entries.Count; i++)
            {
                SectionEntry entry = entries[i];
                string name = NormalizeOptionalString(ExtractNamedString(entry.Block, "JPK"));
                if (string.IsNullOrWhiteSpace(name))
                    name = NormalizeOptionalString(entry.Id);

                string device = NormalizeOptionalString(ExtractNamedString(entry.Block, "device"));
                string key = NormalizeOptionalString(ExtractNamedString(entry.Block, "key"));
                bool sw = ExtractNamedBool(entry.Block, "switch", false);

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(key))
                    continue;

                string reformer = BuildReformerString(name, device, key, sw);
                lookup[name] = reformer;
            }

            return lookup;
        }

        private static string ResolveInputPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string[] candidates = new[]
            {
                Path.Combine(path, "Config", "Input"),
                Path.Combine(path, "Input"),
                path
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (!Directory.Exists(candidate))
                    continue;
                if (LooksLikeInputRoot(candidate))
                    return candidate;
            }

            return null;
        }

        private static bool LooksLikeInputRoot(string path)
        {
            try
            {
                if (Directory.GetFiles(path, "*.diff.lua", SearchOption.AllDirectories).Length > 0)
                    return true;
                return Directory.GetFiles(path, "*.jp", SearchOption.AllDirectories).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;
            return Path.GetFullPath(path);
        }
        private static bool TryExtractNamedBlock(string text, string name, out string block)
        {
            block = null;
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(name))
                return false;

            string token = "[\"" + name + "\"]";
            int tokenIndex = text.IndexOf(token, StringComparison.Ordinal);
            if (tokenIndex < 0)
                return false;

            int openBrace = text.IndexOf('{', tokenIndex);
            if (openBrace < 0)
                return false;

            int closeBrace;
            if (!TryFindMatchingBrace(text, openBrace, out closeBrace))
                return false;

            block = text.Substring(openBrace, closeBrace - openBrace + 1);
            return true;
        }

        private static List<SectionEntry> ParseTopLevelEntries(string block)
        {
            List<SectionEntry> entries = new List<SectionEntry>();
            if (string.IsNullOrWhiteSpace(block) || block.Length < 2)
                return entries;

            string inner = block.Substring(1, block.Length - 2);
            int i = 0;
            int depth = 0;

            while (i < inner.Length)
            {
                char c = inner[i];
                if (c == '{')
                {
                    depth++;
                    i++;
                    continue;
                }
                if (c == '}')
                {
                    if (depth > 0)
                        depth--;
                    i++;
                    continue;
                }

                if (depth == 0 && c == '[')
                {
                    int closeBracket = inner.IndexOf(']', i + 1);
                    if (closeBracket < 0)
                    {
                        i++;
                        continue;
                    }

                    string rawId = inner.Substring(i + 1, closeBracket - i - 1).Trim();
                    string id = Unquote(rawId);

                    int cursor = closeBracket + 1;
                    while (cursor < inner.Length && char.IsWhiteSpace(inner[cursor]))
                        cursor++;
                    if (cursor >= inner.Length || inner[cursor] != '=')
                    {
                        i++;
                        continue;
                    }

                    cursor++;
                    while (cursor < inner.Length && char.IsWhiteSpace(inner[cursor]))
                        cursor++;
                    if (cursor >= inner.Length || inner[cursor] != '{')
                    {
                        i++;
                        continue;
                    }

                    int end;
                    if (!TryFindMatchingBrace(inner, cursor, out end))
                    {
                        i++;
                        continue;
                    }

                    SectionEntry entry = new SectionEntry();
                    entry.Id = id;
                    entry.Block = inner.Substring(cursor, end - cursor + 1);
                    entries.Add(entry);
                    i = end + 1;
                    continue;
                }

                i++;
            }

            return entries;
        }

        // The binds an entry has: "added" ones, and "changed" ones - a bind the aircraft has by default whose filter was
        // changed (DCS saves JoyPro's added axis that way when it equals a default bind, with the filter set in its menu).
        private static List<AddedEntry> ExtractAddedEntries(string entryBlock)
        {
            List<AddedEntry> result = ExtractBindEntries(entryBlock, "added");
            foreach (AddedEntry changed in ExtractBindEntries(entryBlock, "changed"))
            {
                if (!result.Any(a => string.Equals(a.Key, changed.Key, StringComparison.OrdinalIgnoreCase)))
                    result.Add(changed);
            }
            return result;
        }

        private static List<AddedEntry> ExtractBindEntries(string entryBlock, string blockName)
        {
            List<AddedEntry> result = new List<AddedEntry>();
            string addedBlock;
            if (!TryExtractNamedBlock(entryBlock, blockName, out addedBlock))
                return result;

            List<SectionEntry> addedItems = ParseTopLevelEntries(addedBlock);
            for (int i = 0; i < addedItems.Count; i++)
            {
                SectionEntry item = addedItems[i];
                string key = ExtractNamedString(item.Block, "key");
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                AddedEntry entry = new AddedEntry();
                entry.Key = key.Trim();
                entry.Reformers = ExtractNamedStringArray(item.Block, "reformers");
                entry.RelationName = NormalizeOptionalString(ExtractNamedString(item.Block, "JPK"));
                entry.Groups = ExtractNamedStringArray(item.Block, "JGRP");
                PopulateAxisFilter(entry, item.Block);
                result.Add(entry);
            }

            return result;
        }

        private static List<AddedEntry> ExtractRemovedEntries(string entryBlock)
        {
            List<AddedEntry> result = new List<AddedEntry>();
            string removedBlock;
            if (!TryExtractNamedBlock(entryBlock, "removed", out removedBlock))
                return result;

            List<SectionEntry> removedItems = ParseTopLevelEntries(removedBlock);
            for (int i = 0; i < removedItems.Count; i++)
            {
                SectionEntry item = removedItems[i];
                string key = ExtractNamedString(item.Block, "key");
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                AddedEntry entry = new AddedEntry();
                entry.Key = key.Trim();
                entry.Reformers = ExtractNamedStringArray(item.Block, "reformers");
                result.Add(entry);
            }

            return result;
        }

        private static string ExtractEntryName(string entryBlock)
        {
            Match match = NameRegex.Match(entryBlock ?? string.Empty);
            if (!match.Success)
                return string.Empty;
            return UnescapeLuaString(match.Groups["name"].Value ?? string.Empty);
        }

        private static string ExtractNamedString(string text, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(fieldName))
                return string.Empty;

            string pattern = "\\[\"" + Regex.Escape(fieldName) + "\"\\]\\s*=\\s*\"(?<value>(?:\\\\.|[^\"])*)\"";
            Match match = CachedRegex.Match(text, pattern);
            if (!match.Success)
                return string.Empty;
            return UnescapeLuaString(match.Groups["value"].Value ?? string.Empty);
        }

        private static List<string> ExtractNamedStringArray(string text, string fieldName)
        {
            List<string> values = new List<string>();
            string block;
            if (!TryExtractNamedBlock(text, fieldName, out block))
                return values;

            MatchCollection matches = CachedRegex.Matches(
                block,
                "\\[\\s*(?:\\d+|\"[^\"]+\")\\s*\\]\\s*=\\s*\"(?<value>(?:\\\\.|[^\"])*)\"");
            for (int i = 0; i < matches.Count; i++)
            {
                string value = UnescapeLuaString(matches[i].Groups["value"].Value ?? string.Empty).Trim();
                if (value.Length == 0)
                    continue;
                if (!values.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)))
                    values.Add(value);
            }
            return values;
        }

        private static bool ExtractNamedBool(string text, string fieldName, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(fieldName))
                return defaultValue;

            string pattern = "\\[\"" + Regex.Escape(fieldName) + "\"\\]\\s*=\\s*(?<value>true|false|1|0)";
            Match match = CachedRegex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
                return defaultValue;

            string raw = match.Groups["value"].Value;
            bool parsedBool;
            if (bool.TryParse(raw, out parsedBool))
                return parsedBool;

            int parsedInt;
            if (int.TryParse(raw, out parsedInt))
                return parsedInt != 0;

            return defaultValue;
        }

        private static bool TryExtractNamedDouble(string text, string fieldName, out double value)
        {
            value = 0.0;
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(fieldName))
                return false;

            string pattern = "\\[\"" + Regex.Escape(fieldName) + "\"\\]\\s*=\\s*(?<value>-?\\d+(?:\\.\\d+)?)";
            Match match = CachedRegex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;

            return double.TryParse(
                match.Groups["value"].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
        }

        private static List<double> ExtractNamedDoubleArray(string text, string fieldName)
        {
            List<double> values = new List<double>();
            string block;
            if (!TryExtractNamedBlock(text, fieldName, out block))
                return values;

            MatchCollection matches = CachedRegex.Matches(
                block,
                "\\[\\s*(?:\\d+|\"[^\"]+\")\\s*\\]\\s*=\\s*(?<value>-?\\d+(?:\\.\\d+)?)",
                RegexOptions.IgnoreCase);
            for (int i = 0; i < matches.Count; i++)
            {
                if (!double.TryParse(
                    matches[i].Groups["value"].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double parsed))
                {
                    continue;
                }
                values.Add(parsed);
            }
            return values;
        }

        private static void PopulateAxisFilter(AddedEntry entry, string entryBlock)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entryBlock))
                return;

            string filterBlock;
            if (!TryExtractNamedBlock(entryBlock, "filter", out filterBlock))
                return;

            bool hasInvert = CachedRegex.IsMatch(filterBlock, "\\[\"invert\"\\]\\s*=", RegexOptions.IgnoreCase);
            if (hasInvert)
            {
                entry.HasInverted = true;
                entry.Inverted = ExtractNamedBool(filterBlock, "invert", Bind.Inverted_Default);
            }

            bool hasSlider = CachedRegex.IsMatch(filterBlock, "\\[\"slider\"\\]\\s*=", RegexOptions.IgnoreCase);
            if (hasSlider)
            {
                entry.HasSlider = true;
                entry.Slider = ExtractNamedBool(filterBlock, "slider", Bind.Slider_Default);
            }

            if (TryExtractNamedDouble(filterBlock, "deadzone", out double deadzone))
            {
                entry.HasDeadzone = true;
                entry.Deadzone = deadzone;
            }

            List<double> curvature = ExtractNamedDoubleArray(filterBlock, "curvature");
            if (curvature.Count > 0)
                entry.Curvature = curvature;

            if (TryExtractNamedDouble(filterBlock, "saturationX", out double satX))
            {
                entry.HasSaturationX = true;
                entry.SaturationX = satX;
            }

            if (TryExtractNamedDouble(filterBlock, "saturationY", out double satY))
            {
                entry.HasSaturationY = true;
                entry.SaturationY = satY;
            }
        }

        private static List<string> NormalizeImportedReformers(List<string> source, Dictionary<string, string> modifierLookup)
        {
            List<string> normalized = new List<string>();
            if (source == null || source.Count == 0)
                return normalized;

            for (int i = 0; i < source.Count; i++)
            {
                string raw = source[i];
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string token = raw.Trim();
                Modifier parsed = Modifier.ReformerToMod(token);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.name))
                {
                    normalized.Add(parsed.toReformerString());
                    continue;
                }

                if (modifierLookup != null && modifierLookup.ContainsKey(token))
                {
                    normalized.Add(modifierLookup[token]);
                    continue;
                }

                if (InternalDataManagement.AllModifiers.ContainsKey(token) && InternalDataManagement.AllModifiers[token] != null)
                {
                    normalized.Add(InternalDataManagement.AllModifiers[token].toReformerString());
                    continue;
                }

                normalized.Add(BuildFallbackReformerString(token));
            }

            // A keyboard modifier key in DCS's spelling (JoyPro 1 wrote LeftAlt, DCS writes LAlt).
            return normalized
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(KeyboardKeyNames.NormalizeReformer)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string BuildReformerString(string name, string device, string key, bool sw)
        {
            return name.Trim() + SignatureSeparator +
                   device.Trim() + SignatureSeparator +
                   key.Trim() + SignatureSeparator +
                   sw.ToString();
        }

        private static string BuildFallbackReformerString(string name)
        {
            string safe = string.IsNullOrWhiteSpace(name) ? "Modifier" : name.Trim();
            return BuildReformerString(safe, "Keyboard", safe, false);
        }

        private static bool TryFindMatchingBrace(string text, int openIndex, out int closeIndex)
        {
            closeIndex = -1;
            if (string.IsNullOrWhiteSpace(text) || openIndex < 0 || openIndex >= text.Length || text[openIndex] != '{')
                return false;

            int depth = 0;
            for (int i = openIndex; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{')
                    depth++;
                else if (c == '}')
                    depth--;

                if (depth == 0)
                {
                    closeIndex = i;
                    return true;
                }
            }

            return false;
        }
        private static string BuildRequestedRelationName(ParsedBind bind)
        {
            if (!string.IsNullOrWhiteSpace(bind.RelationName))
                return bind.RelationName.Trim();

            string baseName = SanitizeName(string.IsNullOrWhiteSpace(bind.Name) ? bind.Id : bind.Name);
            string device = SanitizeName(bind.Device);
            string key = SanitizeName(bind.Key);
            string id = SanitizeName(bind.Id);
            string isAxis = bind.IsAxis ? "axis" : "button";
            return baseName + "__" + device + "__" + key + "__" + id + "__" + isAxis;
        }

        // A default's relation: title, device, modifiers and input, without the action id (the relation holds the same
        // function of several aircraft, each with its own id).
        private static string BuildDefaultRelationName(ParsedBind bind)
        {
            string baseName = SanitizeName(string.IsNullOrWhiteSpace(bind.Name) ? bind.Id : bind.Name);
            string device = SanitizeName(bind.Device);
            List<string> mods = NormalizeReformerNames(bind.Reformers);
            string key = SanitizeName((mods.Count > 0 ? string.Join("+", mods) + "+" : string.Empty) + bind.Key);
            return baseName + "__" + device + "__" + key + "__default__" + (bind.IsAxis ? "axis" : "button");
        }

        // The first free "<base>__<n>" (n from 1). The import only adds relations, so the search for a name goes on where
        // the last one for the same base stopped (nextSuffix); a case-insensitive dictionary answers with one lookup.
        // (Was a scan of every key for every candidate: minutes on a big Saved Games folder.)
        private static string BuildUniqueRelationNameFromBase(string baseName, Dictionary<string, Relation> current, Dictionary<string, int> nextSuffix = null)
        {
            string candidate = string.IsNullOrWhiteSpace(baseName) ? "Relation" : baseName.Trim();
            bool ignoreCase = StringComparer.OrdinalIgnoreCase.Equals(current.Comparer);
            Func<string, bool> taken = ignoreCase
                ? (Func<string, bool>)current.ContainsKey
                : name => current.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            int count = 1;
            string memoKey = baseName ?? string.Empty;
            int remembered;
            if (ignoreCase && nextSuffix != null && taken(candidate) && nextSuffix.TryGetValue(memoKey, out remembered) && remembered > 1)
            {
                count = remembered;
                candidate = baseName + "__" + (count - 1);
            }

            while (taken(candidate))
            {
                candidate = baseName + "__" + count;
                count++;
            }

            if (nextSuffix != null)
                nextSuffix[memoKey] = count;
            return candidate;
        }

        private static string BuildParsedSignature(ParsedBind bind)
        {
            string reformerSignature = string.Join(
                SignatureSeparator,
                (bind.Reformers ?? new List<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase));

            return (bind.Plane ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator +
                   (bind.Device ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator +
                   (bind.Id ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator +
                   (bind.IsAxis ? "1" : "0") + SignatureSeparator +
                   (bind.Key ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator +
                   reformerSignature.ToLowerInvariant();
        }

        private static List<string> NormalizeList(List<string> source)
        {
            if (source == null || source.Count == 0)
                return new List<string>();

            List<string> result = new List<string>();
            for (int i = 0; i < source.Count; i++)
            {
                string value = source[i];
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                string trimmed = value.Trim();
                if (trimmed.Length == 0)
                    continue;
                if (!result.Any(v => string.Equals(v, trimmed, StringComparison.OrdinalIgnoreCase)))
                    result.Add(trimmed);
            }
            return result;
        }

        private static List<string> NormalizeGroups(List<string> groups)
        {
            return NormalizeList(groups);
        }

        private static bool SameReformers(List<string> existing, List<string> incoming)
        {
            List<string> left = NormalizeReformerNames(existing);
            List<string> right = NormalizeReformerNames(incoming);
            if (left.Count != right.Count)
                return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private static List<string> NormalizeReformerNames(List<string> reformers)
        {
            if (reformers == null || reformers.Count == 0)
                return new List<string>();

            List<string> values = new List<string>();
            for (int i = 0; i < reformers.Count; i++)
            {
                string raw = reformers[i];
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                string[] parts = raw.Split('\u00A7');
                string name = parts.Length == 0 ? raw : parts[0];
                name = name.Trim();
                if (name.Length == 0)
                    continue;
                if (!values.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase)))
                    values.Add(name);
            }

            return values.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string NormalizeOptionalString(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return value.Trim();
        }

        private static void AddUniqueGroup(List<string> groups, string group)
        {
            if (groups == null || string.IsNullOrWhiteSpace(group))
                return;
            if (!groups.Any(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase)))
                groups.Add(group.Trim());
        }

        private static string SanitizeName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "item";

            StringBuilder sb = new StringBuilder();
            char previous = '\0';
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                char outChar;
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    outChar = c;
                else
                    outChar = '_';

                if (outChar == '_' && previous == '_')
                    continue;

                sb.Append(outChar);
                previous = outChar;
            }

            string value = sb.ToString().Trim('_');
            return string.IsNullOrWhiteSpace(value) ? "item" : value;
        }

        private static string Unquote(string value)
        {
            string text = (value ?? string.Empty).Trim();
            if (text.Length >= 2 && text.StartsWith("\"", StringComparison.Ordinal) && text.EndsWith("\"", StringComparison.Ordinal))
                text = text.Substring(1, text.Length - 2);
            return UnescapeLuaString(text);
        }

        private static string UnescapeLuaString(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<none>" : value;
        }

        private sealed class SectionEntry
        {
            public string Id { get; set; }
            public string Block { get; set; }
        }

        private sealed class AddedEntry
        {
            public string Key { get; set; }
            public List<string> Reformers { get; set; }
            public string RelationName { get; set; }
            public List<string> Groups { get; set; }
            public bool HasInverted { get; set; }
            public bool Inverted { get; set; }
            public bool HasSlider { get; set; }
            public bool Slider { get; set; }
            public bool HasDeadzone { get; set; }
            public double Deadzone { get; set; }
            public List<double> Curvature { get; set; }
            public bool HasSaturationX { get; set; }
            public double SaturationX { get; set; }
            public bool HasSaturationY { get; set; }
            public double SaturationY { get; set; }

            public AddedEntry()
            {
                Key = string.Empty;
                Reformers = new List<string>();
                RelationName = null;
                Groups = new List<string>();
                HasInverted = false;
                Inverted = Bind.Inverted_Default;
                HasSlider = false;
                Slider = Bind.Slider_Default;
                HasDeadzone = false;
                Deadzone = Bind.Deadzone_Default;
                Curvature = new List<double>();
                HasSaturationX = false;
                SaturationX = Bind.SaturationX_Default;
                HasSaturationY = false;
                SaturationY = Bind.SaturationY_Default;
            }
        }

        private sealed class ParsedBind
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public bool IsAxis { get; set; }
            public string Plane { get; set; }
            public string Device { get; set; }
            public string Key { get; set; }
            public List<string> Reformers { get; set; }
            public string RelationName { get; set; }
            public List<string> Groups { get; set; }
            public string SourceFile { get; set; }
            public int MetadataPriority { get; set; }
            // A DCS default added by "include defaults" (not in the user's files).
            public bool IsDefault { get; set; }
            // The filter came from the .diff.lua (DCS's own state), not from a .jp backup or the clean defaults.
            public bool FilterFromDiff { get; set; }
            public bool HasInverted { get; set; }
            public bool Inverted { get; set; }
            public bool HasSlider { get; set; }
            public bool Slider { get; set; }
            public bool HasDeadzone { get; set; }
            public double Deadzone { get; set; }
            public List<double> Curvature { get; set; }
            public bool HasSaturationX { get; set; }
            public double SaturationX { get; set; }
            public bool HasSaturationY { get; set; }
            public double SaturationY { get; set; }

            public ParsedBind()
            {
                Id = string.Empty;
                Name = string.Empty;
                Plane = string.Empty;
                Device = string.Empty;
                Key = string.Empty;
                Reformers = new List<string>();
                RelationName = null;
                Groups = new List<string>();
                SourceFile = string.Empty;
                MetadataPriority = 0;
                HasInverted = false;
                Inverted = Bind.Inverted_Default;
                HasSlider = false;
                Slider = Bind.Slider_Default;
                HasDeadzone = false;
                Deadzone = Bind.Deadzone_Default;
                Curvature = new List<double>();
                HasSaturationX = false;
                SaturationX = Bind.SaturationX_Default;
                HasSaturationY = false;
                SaturationY = Bind.SaturationY_Default;
            }
        }

        private sealed class ParsedFfbSetting
        {
            public string Device { get; set; }
            public string Plane { get; set; }
            public bool InvertX { get; set; }
            public bool InvertY { get; set; }
            public bool SwapAxis { get; set; }
            public string SourceFile { get; set; }
            public int MetadataPriority { get; set; }

            public ParsedFfbSetting()
            {
                Device = string.Empty;
                Plane = string.Empty;
                InvertX = false;
                InvertY = false;
                SwapAxis = false;
                SourceFile = string.Empty;
                MetadataPriority = 0;
            }
        }
    }
}
