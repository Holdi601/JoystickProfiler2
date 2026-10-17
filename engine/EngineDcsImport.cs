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
            sb.AppendLine("Imported Relations: " + result.ImportedRelations);
            sb.AppendLine("Imported Binds: " + result.ImportedBinds);
            sb.AppendLine("Force Feedback Devices Imported: " + result.ForceFeedbackDevicesImported);
            sb.AppendLine("Skipped Entries: " + result.SkippedEntries);
            sb.AppendLine("Backup Binds Removed In DCS (.jp, skipped): " + result.StaleBackupEntriesSkipped);

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
            List<ParsedBind> parsed = ParseAllInputFiles(inputPath, effectiveOptions, result, out parsedFfb);
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
                parsed = AppendDefaultsFromCleanProfiles(parsed, effectiveOptions, result);
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
                result.ImportedBinds.ToString() + ".");
            return result;
        }

        private static List<ParsedBind> ParseAllInputFiles(
            string inputPath,
            EngineDcsImportOptions options,
            EngineDcsImportResult result,
            out Dictionary<string, ParsedFfbSetting> parsedFfbByDevice)
        {
            Dictionary<string, ParsedBind> parsed = new Dictionary<string, ParsedBind>(StringComparer.OrdinalIgnoreCase);
            parsedFfbByDevice = new Dictionary<string, ParsedFfbSetting>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, string>> modifierLookupByPlane =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
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
            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
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

                EngineProgress.Report(i + 1, files.Length, "DCS World, reading your binds", plane + " - " + device);
                bool isBackup = IsBackupFile(file);
                result.FilesScanned++;
                if (isBackup)
                    result.BackupFilesScanned++;
                matchedPlanes.Add(plane);
                matchedDevices.Add(device);
                ParseInputFile(file, isBackup, parsed, parsedFfbByDevice, modifierLookupByPlane, ffbCorrections, result);

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

        private static List<ParsedBind> AppendDefaultsFromCleanProfiles(
            List<ParsedBind> parsed,
            EngineDcsImportOptions options,
            EngineDcsImportResult result)
        {
            Dictionary<string, ParsedBind> merged = new Dictionary<string, ParsedBind>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> addedIdsByPlaneDevice = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < parsed.Count; i++)
            {
                ParsedBind bind = parsed[i];
                UpsertParsedBind(merged, bind);
                addedIdsByPlaneDevice.Add(BuildPlaneDeviceIdKey(bind.Plane, bind.Device, bind.Id, bind.IsAxis));
            }

            string cleanProfileRoot = GameDataPaths.CleanProfileFolder("DCS", false);
            string keyboardCleanRoot = GameDataPaths.CleanProfileFolder("DCS", true);
            List<string> planes = options.PlaneFilter == null || options.PlaneFilter.Count == 0
                ? CollectCleanProfilePlanes(cleanProfileRoot, keyboardCleanRoot)
                : new List<string>(options.PlaneFilter);
            if (options.IgnoreEasyPlanes)
            {
                planes = planes
                    .Where(p => !IsEasyPlaneToken(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            List<string> joystickTargets;
            bool includeKeyboardDefaults;
            if (options.DeviceFilter != null && options.DeviceFilter.Count > 0)
            {
                List<string> matchedJoystickTargets = (result.MatchedDevices ?? new List<string>())
                    .Where(d => !string.IsNullOrWhiteSpace(d) && !string.Equals(d, "Keyboard", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                joystickTargets = matchedJoystickTargets.Count > 0
                    ? matchedJoystickTargets
                    : options.DeviceFilter
                        .Where(d => !string.Equals(d, "Keyboard", StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                includeKeyboardDefaults =
                    options.IncludeKeyboard &&
                    options.DeviceFilter.Any(d => string.Equals(d, "Keyboard", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                joystickTargets = parsed
                    .Select(p => p.Device ?? string.Empty)
                    .Where(d => !string.IsNullOrWhiteSpace(d) && !string.Equals(d, "Keyboard", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                includeKeyboardDefaults = options.IncludeKeyboard;
            }

            if (options.IgnoreDeviceFilter != null && options.IgnoreDeviceFilter.Count > 0)
            {
                HashSet<string> ignoredSet = new HashSet<string>(options.IgnoreDeviceFilter, StringComparer.OrdinalIgnoreCase);
                HashSet<string> ignoredByNameSet = BuildGuidlessDeviceFilterSet(ignoredSet);
                joystickTargets = joystickTargets
                    .Where(d => !MatchesDeviceFilter(d, ignoredSet, ignoredByNameSet))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (includeKeyboardDefaults && ignoredSet.Contains("Keyboard"))
                    includeKeyboardDefaults = false;
            }

            HashSet<string> matchedPlanes = new HashSet<string>(result.MatchedPlanes ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> matchedDevices = new HashSet<string>(result.MatchedDevices ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < planes.Count; i++)
            {
                string plane = planes[i];
                if (string.IsNullOrWhiteSpace(plane))
                    continue;

                string joystickDefaultFile = Path.Combine(cleanProfileRoot, plane + ".cf");
                List<ParsedBind> joystickDefaults = ParseDefaultFile(joystickDefaultFile, plane, joystickTargets, result);
                for (int d = 0; d < joystickDefaults.Count; d++)
                {
                    ParsedBind bind = joystickDefaults[d];
                    string idKey = BuildPlaneDeviceIdKey(bind.Plane, bind.Device, bind.Id, bind.IsAxis);
                    if (addedIdsByPlaneDevice.Contains(idKey))
                        continue;
                    UpsertParsedBind(merged, bind);
                    matchedPlanes.Add(bind.Plane);
                    matchedDevices.Add(bind.Device);
                }

                if (!includeKeyboardDefaults)
                    continue;

                string keyboardDefaultFile = Path.Combine(keyboardCleanRoot, plane + ".cf");
                List<ParsedBind> keyboardDefaults = ParseDefaultFile(keyboardDefaultFile, plane, new List<string> { "Keyboard" }, result);
                for (int k = 0; k < keyboardDefaults.Count; k++)
                {
                    ParsedBind bind = keyboardDefaults[k];
                    string idKey = BuildPlaneDeviceIdKey(bind.Plane, bind.Device, bind.Id, bind.IsAxis);
                    if (addedIdsByPlaneDevice.Contains(idKey))
                        continue;
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

        private static List<string> CollectCleanProfilePlanes(string cleanProfileRoot, string keyboardCleanRoot)
        {
            HashSet<string> planes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(cleanProfileRoot))
            {
                string[] files = Directory.GetFiles(cleanProfileRoot, "*.cf", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < files.Length; i++)
                {
                    string plane = Path.GetFileNameWithoutExtension(files[i]);
                    if (!string.IsNullOrWhiteSpace(plane))
                        planes.Add(plane.Trim());
                }
            }

            if (Directory.Exists(keyboardCleanRoot))
            {
                string[] files = Directory.GetFiles(keyboardCleanRoot, "*.cf", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < files.Length; i++)
                {
                    string plane = Path.GetFileNameWithoutExtension(files[i]);
                    if (!string.IsNullOrWhiteSpace(plane))
                        planes.Add(plane.Trim());
                }
            }

            return planes.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<ParsedBind> ParseDefaultFile(
            string filePath,
            string plane,
            List<string> devices,
            EngineDcsImportResult result)
        {
            List<ParsedBind> output = new List<ParsedBind>();
            if (!File.Exists(filePath) || devices == null || devices.Count == 0)
                return output;

            string text;
            try
            {
                text = File.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Skipping unreadable clean profile file: " + filePath + " (" + ex.Message + ")");
                return output;
            }

            ParseDefaultSection(text, "axisDiffs", true, plane, devices, output, result);
            ParseDefaultSection(text, "keyDiffs", false, plane, devices, output, result);
            return output;
        }

        private static void ParseDefaultSection(
            string fileText,
            string sectionName,
            bool isAxis,
            string plane,
            List<string> devices,
            List<ParsedBind> output,
            EngineDcsImportResult result)
        {
            string sectionBlock;
            if (!TryExtractNamedBlock(fileText, sectionName, out sectionBlock))
                return;

            List<SectionEntry> entries = ParseTopLevelEntries(sectionBlock);
            for (int i = 0; i < entries.Count; i++)
            {
                SectionEntry entry = entries[i];
                List<AddedEntry> removed = ExtractRemovedEntries(entry.Block);
                if (removed.Count == 0)
                    continue;

                AddedEntry first = removed[0];
                if (string.IsNullOrWhiteSpace(first.Key))
                    continue;

                string displayName = ExtractEntryName(entry.Block);
                for (int d = 0; d < devices.Count; d++)
                {
                    string device = devices[d];
                    if (string.IsNullOrWhiteSpace(device))
                        continue;

                    ParsedBind bind = new ParsedBind();
                    bind.Id = entry.Id;
                    bind.Name = string.IsNullOrWhiteSpace(displayName) ? entry.Id : displayName;
                    bind.IsAxis = isAxis;
                    bind.Plane = string.IsNullOrWhiteSpace(plane) ? "UnknownPlane" : plane;
                    bind.Device = device.Trim();
                    bind.Key = first.Key.Trim();
                    bind.Reformers = new List<string>();
                    bind.RelationName = null;
                    bind.Groups = new List<string> { "DCS" };
                    bind.MetadataPriority = 0;
                    bind.SourceFile = string.Empty;
                    result.DefaultEntriesParsed++;
                    output.Add(bind);
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
            EngineDcsImportResult result)
        {
            string text;
            try
            {
                text = File.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Skipping unreadable file: " + filePath + " (" + ex.Message + ")");
                return;
            }

            string plane = ExtractPlaneName(filePath);
            string device = ExtractDeviceName(filePath);
            Dictionary<string, string> modifierLookup = GetModifierLookupForPlane(filePath, plane, modifierLookupByPlane, result);
            HashSet<string> backupAllowed = isBackupFile ? BuildBackupAllowedBinds(filePath, plane) : null;
            ParseSection(text, "axisDiffs", true, plane, device, filePath, isBackupFile, modifierLookup, parsedBySignature, result, backupAllowed);
            ParseSection(text, "keyDiffs", false, plane, device, filePath, isBackupFile, modifierLookup, parsedBySignature, result, backupAllowed);
            ParseForceFeedbackSection(text, plane, device, filePath, isBackupFile, ffbCorrections, parsedFfbByDevice, result);
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
            HashSet<string> backupAllowed = null)
        {
            string sectionBlock;
            if (!TryExtractNamedBlock(fileText, sectionName, out sectionBlock))
                return;

            List<SectionEntry> entries = ParseTopLevelEntries(sectionBlock);
            for (int i = 0; i < entries.Count; i++)
            {
                SectionEntry entry = entries[i];
                List<AddedEntry> added = ExtractAddedEntries(entry.Block);
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

                string displayName = ExtractEntryName(entry.Block);
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
                    bind.Key = addedEntry.Key.Trim();
                    bind.Reformers = NormalizeImportedReformers(addedEntry.Reformers, modifierLookup);
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

            bool hasInvertX = Regex.IsMatch(ffBlock, "\\[\"invertX\"\\]\\s*=", RegexOptions.IgnoreCase);
            bool hasInvertY = Regex.IsMatch(ffBlock, "\\[\"invertY\"\\]\\s*=", RegexOptions.IgnoreCase);
            bool hasSwapAxes = Regex.IsMatch(ffBlock, "\\[\"swapAxes\"\\]\\s*=", RegexOptions.IgnoreCase);
            bool hasSwapAxis = Regex.IsMatch(ffBlock, "\\[\"swapAxis\"\\]\\s*=", RegexOptions.IgnoreCase);
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

            for (int i = 0; i < parsed.Count; i++)
            {
                ParsedBind parsedBind = parsed[i];
                if (string.IsNullOrWhiteSpace(parsedBind.Id) || string.IsNullOrWhiteSpace(parsedBind.Key))
                {
                    result.SkippedEntries++;
                    continue;
                }

                string preferredName = BuildRequestedRelationName(parsedBind);
                string relationName = preferredName;

                if (TryGetCompatibleExistingRelation(relationName, parsedBind, relations, binds, out Relation existingRelation, out Bind existingBind))
                {
                    MergeIntoExistingRelation(parsedBind, existingRelation, existingBind, options);
                    continue;
                }

                if (relations.ContainsKey(relationName))
                    relationName = BuildUniqueRelationNameFromBase(preferredName, relations);

                Relation relation = CreateRelation(parsedBind, relationName);
                Bind bind = CreateBind(parsedBind, relation, options);
                relation.bind = bind;

                relations[relationName] = relation;
                binds[relationName] = bind;
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

        private static void MergeIntoExistingRelation(ParsedBind parsedBind, Relation relation, Bind bind, EngineDcsImportOptions options)
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
            // A relation JoyPro exported without groups (its .jp names it) stays without; others get "DCS".
            if (groups.Count == 0 && string.IsNullOrWhiteSpace(parsedBind.RelationName))
                AddUniqueGroup(relation.Groups, "DCS");
            else
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
        // counts only if the diff has it (the .jp adds relation names and groups) or if it is a plane default the diff
        // does not remove (DCS drops binds equal to a default when it saves). Other .jp binds were removed in DCS.
        // Returns null when there is no diff: then the .jp is all there is.
        private static HashSet<string> BuildBackupAllowedBinds(string backupPath, string plane)
        {
            string diffPath = backupPath.Substring(0, backupPath.Length - ".jp".Length) + ".diff.lua";
            if (!File.Exists(diffPath))
                return null;

            string diffText;
            try
            {
                diffText = File.ReadAllText(diffPath);
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
            string cleanPath = Path.Combine(GameDataPaths.CleanProfileFolder("DCS", keyboard), plane + ".cf");
            if (File.Exists(cleanPath))
            {
                HashSet<string> defaults = new HashSet<string>(StringComparer.Ordinal);
                HashSet<string> unused = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    CollectBackupBindKeys(File.ReadAllText(cleanPath), unused, defaults);
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

                string text = File.ReadAllText(modifiersPath);
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
            Match match = Regex.Match(text, pattern);
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

            MatchCollection matches = Regex.Matches(
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
            Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
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
            Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
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

            MatchCollection matches = Regex.Matches(
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

            bool hasInvert = Regex.IsMatch(filterBlock, "\\[\"invert\"\\]\\s*=", RegexOptions.IgnoreCase);
            if (hasInvert)
            {
                entry.HasInverted = true;
                entry.Inverted = ExtractNamedBool(filterBlock, "invert", Bind.Inverted_Default);
            }

            bool hasSlider = Regex.IsMatch(filterBlock, "\\[\"slider\"\\]\\s*=", RegexOptions.IgnoreCase);
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

            return normalized
                .Where(v => !string.IsNullOrWhiteSpace(v))
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

        private static string BuildUniqueRelationNameFromBase(string baseName, Dictionary<string, Relation> current)
        {
            string candidate = string.IsNullOrWhiteSpace(baseName) ? "Relation" : baseName.Trim();
            int count = 1;

            while (current.Keys.Any(k => string.Equals(k, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidate = baseName + "__" + count;
                count++;
            }

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
