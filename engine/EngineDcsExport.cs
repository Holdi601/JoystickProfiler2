using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    internal static class EngineDcsExport
    {
        private const string SignatureSeparator = "\u00A7";
        // DCS loads these files with Lua 5.1, which rejects a UTF-8 byte order mark ("unexpected symbol near '\xEF'").
        private static readonly Encoding NoBom = new UTF8Encoding(false);
        private static readonly Regex NameRegex = new Regex("\\[\"name\"\\]\\s*=\\s*\"(?<name>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled);
        private static void ReportStatus(EngineDcsExportOptions options, string message)
        {
            if (options == null || options.StatusSink == null || string.IsNullOrWhiteSpace(message))
                return;
            options.StatusSink(message);
        }

        public static EngineDcsExportResult ExportToSavedGames(string savedGamesPath, EngineDcsExportOptions options = null)
        {
            EngineDcsExportResult result = new EngineDcsExportResult();
            EngineDcsExportOptions effectiveOptions = NormalizeExportOptions(options);
            ReportStatus(effectiveOptions, "Preparing DCS export.");
            if (!TryParseExportMode(effectiveOptions.Mode, out EngineDcsExportMode mode))
            {
                result.Errors.Add("Invalid DCS export mode: " + effectiveOptions.Mode + ". Use clean, add, merge or merge-overwrite.");
                return result;
            }

            result.Mode = ExportModeToString(mode);
            result.KeepKeyboardDefaults = effectiveOptions.KeepKeyboardDefaults;
            result.NukeUntouchedDevices = effectiveOptions.NukeUntouchedDevices;
            result.SavedGamesPath = Normalize(savedGamesPath);
            result.InputPath = ResolveOutputInputPath(result.SavedGamesPath);

            if (string.IsNullOrWhiteSpace(result.InputPath))
            {
                result.Errors.Add("Invalid export path: " + result.SavedGamesPath);
                return result;
            }

            try
            {
                Directory.CreateDirectory(result.InputPath);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Could not create output input path: " + ex.Message);
                return result;
            }

            // The input files the export merges with (and the state record after it reads): read once, several at once.
            try
            {
                RunFileCache.Prefetch(Directory.EnumerateFiles(result.InputPath, "*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".diff.lua", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jp", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(Path.GetFileName(f), "modifiers.lua", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // The export reads each file itself then (and reports what it cannot read).
            }

            ReportStatus(effectiveOptions, "Building export payloads from current profile.");
            Dictionary<string, Dictionary<string, DiffPayload>> generatedPayloads = BuildPayloads(result, effectiveOptions);
            foreach (string missing in effectiveOptions.Planes.Where(p => !generatedPayloads.ContainsKey(p)))
                result.Warnings.Add("Nothing to export for " + missing + ": the profile has no bound relation for it.");
            Dictionary<string, Dictionary<string, ModifierPayload>> generatedModifiers = BuildGeneratedPlaneModifiers(generatedPayloads);
            Dictionary<string, DiffPayload> cleanProfiles = new Dictionary<string, DiffPayload>(StringComparer.OrdinalIgnoreCase);
            ReportStatus(
                effectiveOptions,
                "Writing payload files for " +
                generatedPayloads.Count.ToString(CultureInfo.InvariantCulture) +
                " plane(s).");

            int planeIndex = 0;
            foreach (KeyValuePair<string, Dictionary<string, DiffPayload>> plane in generatedPayloads)
            {
                string planeName = plane.Key;
                string planeRootDir = Path.Combine(result.InputPath, planeName);
                EngineProgress.Report(++planeIndex, generatedPayloads.Count, "DCS World, writing your binds", planeName);
                ReportStatus(
                    effectiveOptions,
                    "Writing plane " +
                    planeName +
                    " (" +
                    plane.Value.Count.ToString(CultureInfo.InvariantCulture) +
                    " device(s)).");
                foreach (KeyValuePair<string, DiffPayload> devicePayload in plane.Value)
                {
                    string device = devicePayload.Key;
                    DiffPayload generated = devicePayload.Value;

                    bool keyboard = string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase);
                    string planeDir = Path.Combine(result.InputPath, planeName, keyboard ? "keyboard" : "joystick");
                    DeviceFiles files = ResolveDeviceFiles(planeDir, device, keyboard);

                    DiffPayload clean = LoadCleanProfile(planeName, keyboard, files.DcsDevice, cleanProfiles, result);
                    DiffPayload existing = LoadExistingPayload(files.ExistingDiff, files.ExistingBackup, result, clean);
                    DiffPayload merged = MergePayloads(mode, existing, generated, result);
                    AddCleanProfileRemovals(merged, clean);
                    DropRemovalsOfAddedBinds(merged);

                    WritePayloadFile(planeDir, files.Diff, merged, false, result);
                    WritePayloadFile(planeDir, files.Backup, merged, true, result);
                    RemoveLegacyFiles(files, result);
                }

                Dictionary<string, ModifierPayload> generatedPlaneModifiers;
                if (!generatedModifiers.TryGetValue(planeName, out generatedPlaneModifiers))
                    generatedPlaneModifiers = CreateDefaultModifierMap();
                MergeAndWriteModifiers(mode, planeRootDir, generatedPlaneModifiers, result);
            }

            // JoyPro takes over whole devices (docs/games/README.md, rule 5): the written devices also lose their DCS
            // defaults in every other installed aircraft. Clean also clears their binds there; the other modes keep
            // the binds the files already have.
            if (effectiveOptions.CleanAllAircraft && effectiveOptions.Planes.Count == 0)
            {
                bool keepBinds = mode != EngineDcsExportMode.Clean;
                ReportStatus(effectiveOptions, keepBinds
                    ? "Removing the DCS default binds of the written devices in the other aircraft."
                    : "Clearing the written devices in the other aircraft.");
                CleanOtherAircraft(result.InputPath, generatedPayloads, cleanProfiles, result, keepBinds);
            }
            WarnAboutUncoveredDeviceTypes(generatedPayloads, result);

            if (effectiveOptions.NukeUntouchedDevices)
            {
                ReportStatus(effectiveOptions, "Removing all binds of devices the profile does not use.");
                NukeUnusedDevices(result.InputPath, generatedPayloads, cleanProfiles, result);
            }

            result.PlaneCount = generatedPayloads.Keys.Count;
            result.DeviceCount = generatedPayloads.Values
                .SelectMany(v => v.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            ReportStatus(
                effectiveOptions,
                "DCS export complete. Files written: " +
                result.FilesWritten.ToString(CultureInfo.InvariantCulture) +
                ", backups: " +
                result.BackupFilesWritten.ToString(CultureInfo.InvariantCulture) + ".");
            return result;
        }

        public static string ToText(EngineDcsExportResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DCS Export");
            sb.AppendLine("Saved Games Path: " + Safe(result.SavedGamesPath));
            sb.AppendLine("Input Path: " + Safe(result.InputPath));
            sb.AppendLine("Mode: " + Safe(result.Mode));
            sb.AppendLine("Keep Keyboard Defaults: " + result.KeepKeyboardDefaults);
            sb.AppendLine("Nuke Untouched Devices: " + result.NukeUntouchedDevices);
            sb.AppendLine("Relations Scanned: " + result.RelationsScanned);
            sb.AppendLine("Entries Written: " + result.EntriesWritten);
            sb.AppendLine("Files Written: " + result.FilesWritten);
            sb.AppendLine("Backup Files Written (.jp): " + result.BackupFilesWritten);
            sb.AppendLine("Existing Files Parsed: " + result.ExistingFilesParsed);
            sb.AppendLine("Existing Diff Files Parsed: " + result.ExistingDiffFilesParsed);
            sb.AppendLine("Existing Backup Files Parsed (.jp): " + result.ExistingBackupFilesParsed);
            sb.AppendLine("Merge Conflicts Kept: " + result.MergeConflictsKept);
            sb.AppendLine("Merge Conflicts Overwritten: " + result.MergeConflictsOverwritten);
            sb.AppendLine("Merge Conflicts Added: " + result.MergeConflictsAdded);
            sb.AppendLine("Device Files Cleared (Nuke): " + result.DevicesCleared);
            sb.AppendLine("Other Aircraft Files Cleaned (Clean): " + result.OtherAircraftFilesCleaned);
            if (result.RenamedDeviceFiles != null)
                foreach (string renamed in result.RenamedDeviceFiles)
                    sb.AppendLine("Renamed to DCS device name: " + renamed);
            sb.AppendLine("Planes: " + result.PlaneCount);
            sb.AppendLine("Devices: " + result.DeviceCount);
            sb.AppendLine("Skipped Relations: " + result.SkippedRelations);
            sb.AppendLine("Skipped Keyboard Entries: " + result.SkippedKeyboardEntries);

            if (result.Errors != null && result.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                for (int i = 0; i < result.Errors.Count; i++)
                    sb.AppendLine("  - " + result.Errors[i]);
            }

            if (result.Warnings != null && result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < result.Warnings.Count; i++)
                    sb.AppendLine("  - " + result.Warnings[i]);
            }

            return sb.ToString();
        }

        private static EngineDcsExportOptions NormalizeExportOptions(EngineDcsExportOptions options)
        {
            EngineDcsExportOptions effective = options ?? new EngineDcsExportOptions();
            EngineDcsExportOptions normalized = new EngineDcsExportOptions();
            normalized.Mode = string.IsNullOrWhiteSpace(effective.Mode)
                ? "merge-overwrite"
                : effective.Mode.Trim();
            normalized.KeepKeyboardDefaults = effective.KeepKeyboardDefaults;
            normalized.NukeUntouchedDevices = effective.NukeUntouchedDevices;
            normalized.CleanAllAircraft = effective.CleanAllAircraft;
            normalized.Planes = (effective.Planes ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => SanitizePathSegment(p.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            normalized.StatusSink = effective.StatusSink;
            return normalized;
        }

        private static Dictionary<string, Dictionary<string, DiffPayload>> BuildPayloads(
            EngineDcsExportResult result,
            EngineDcsExportOptions options)
        {
            Dictionary<string, Dictionary<string, DiffPayload>> payloads =
                new Dictionary<string, Dictionary<string, DiffPayload>>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> wantedPlanes = options.Planes.Count == 0 ? null : new HashSet<string>(options.Planes, StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations)
            {
                Relation relation = entry.Value;
                result.RelationsScanned++;
                if (relation == null || relation.NODES == null || relation.NODES.Count == 0)
                {
                    result.SkippedRelations++;
                    continue;
                }

                Bind bind = relation.bind;
                if (bind == null && InternalDataManagement.AllBinds.ContainsKey(entry.Key))
                    bind = InternalDataManagement.AllBinds[entry.Key];

                if (bind == null)
                {
                    result.SkippedRelations++;
                    continue;
                }

                string key = relation.ISAXIS ? bind.JAxis : bind.JButton;
                if (string.IsNullOrWhiteSpace(key))
                {
                    result.SkippedRelations++;
                    continue;
                }

                string device = string.IsNullOrWhiteSpace(bind.Joystick)
                    ? "UnknownDevice"
                    : bind.Joystick.Trim();
                if (options.KeepKeyboardDefaults && string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase))
                {
                    result.SkippedKeyboardEntries++;
                    continue;
                }

                string name = string.IsNullOrWhiteSpace(bind.AdditionalImportInfo)
                    ? (string.IsNullOrWhiteSpace(relation.NAME) ? entry.Key : relation.NAME)
                    : bind.AdditionalImportInfo;
                List<string> reformers = ExtractReformerNames(bind.AllReformers);
                string relationName = string.IsNullOrWhiteSpace(relation.NAME) ? entry.Key : relation.NAME;
                List<string> groups = ExtractGroups(relation.Groups);
                AxisFilter axisFilter = BuildAxisFilter(bind, relation.ISAXIS);

                for (int i = 0; i < relation.NODES.Count; i++)
                {
                    RelationItem node = relation.NODES[i];
                    if (node == null)
                        continue;
                    if (!string.Equals(NormalizeGame(node.Game), "DCS", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.IsNullOrWhiteSpace(node.ID))
                        continue;

                    // Only the aircraft the user left active (JoyPro 1 did the same): a node with every aircraft
                    // switched off is not exported (it used to land in Config\Input\Default).
                    List<string> planes = node.GetActiveAircraftList();
                    if (planes.Count == 0)
                    {
                        string inactive = "Relation '" + relationName + "': a DCS node (" + node.ID + ") has no active aircraft and is not exported.";
                        if (!result.Warnings.Contains(inactive))
                            result.Warnings.Add(inactive);
                    }

                    for (int p = 0; p < planes.Count; p++)
                    {
                        string plane = SanitizePathSegment(planes[p]);
                        if (wantedPlanes != null && !wantedPlanes.Contains(plane))
                            continue;
                        if (!payloads.ContainsKey(plane))
                            payloads.Add(plane, new Dictionary<string, DiffPayload>(StringComparer.OrdinalIgnoreCase));
                        if (!payloads[plane].ContainsKey(device))
                            payloads[plane].Add(device, new DiffPayload());

                        DiffPayload payload = payloads[plane][device];
                        // An aircraft or DCS tuning of the relation replaces the bind's base values for this plane.
                        AxisFilter planeFilter = relation.ISAXIS && bind.TuningOverrides != null && bind.TuningOverrides.Count > 0
                            ? BuildAxisFilter(JoyPro.Engine.Axis.BindTuning.Resolve(bind, "DCS", planes[p]).Tuning)
                            : axisFilter;
                        if (AddEntry(payload, node.ID, name, key, relation.ISAXIS, reformers, relationName, groups, planeFilter))
                            result.EntriesWritten++;
                    }
                }
            }

            AttachForceFeedbackPayloads(payloads, result);
            return payloads;
        }

        private static Dictionary<string, Dictionary<string, ModifierPayload>> BuildGeneratedPlaneModifiers(
            Dictionary<string, Dictionary<string, DiffPayload>> payloads)
        {
            Dictionary<string, Dictionary<string, ModifierPayload>> modifiersByPlane =
                new Dictionary<string, Dictionary<string, ModifierPayload>>(StringComparer.OrdinalIgnoreCase);
            if (payloads == null || payloads.Count == 0)
                return modifiersByPlane;

            foreach (string plane in payloads.Keys)
                modifiersByPlane[plane] = CreateDefaultModifierMap();

            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations)
            {
                Relation relation = entry.Value;
                if (relation == null || relation.NODES == null || relation.NODES.Count == 0)
                    continue;

                Bind bind = relation.bind;
                if (bind == null && InternalDataManagement.AllBinds.ContainsKey(entry.Key))
                    bind = InternalDataManagement.AllBinds[entry.Key];
                if (bind == null || bind.AllReformers == null || bind.AllReformers.Count == 0)
                    continue;

                for (int i = 0; i < relation.NODES.Count; i++)
                {
                    RelationItem node = relation.NODES[i];
                    if (node == null)
                        continue;
                    if (!string.Equals(NormalizeGame(node.Game), "DCS", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.IsNullOrWhiteSpace(node.ID))
                        continue;

                    // Only the aircraft the user left active (JoyPro 1 did the same): a node with every aircraft
                    // switched off is not exported (it used to land in Config\Input\Default).
                    List<string> planes = node.GetActiveAircraftList();

                    for (int p = 0; p < planes.Count; p++)
                    {
                        string plane = SanitizePathSegment(planes[p]);
                        if (!modifiersByPlane.ContainsKey(plane))
                            continue;
                        AddBindModifiersToPlane(modifiersByPlane[plane], bind);
                    }
                }
            }

            return modifiersByPlane;
        }

        private static void AddBindModifiersToPlane(Dictionary<string, ModifierPayload> target, Bind bind)
        {
            if (target == null || bind == null)
                return;

            List<ModifierPayload> modifiers = ExtractModifierDefinitions(bind);
            for (int i = 0; i < modifiers.Count; i++)
            {
                ModifierPayload modifier = modifiers[i];
                if (modifier == null || string.IsNullOrWhiteSpace(modifier.Name))
                    continue;
                target[modifier.Name] = CloneModifier(modifier);
            }
        }

        private static List<ModifierPayload> ExtractModifierDefinitions(Bind bind)
        {
            List<ModifierPayload> result = new List<ModifierPayload>();
            if (bind == null || bind.AllReformers == null || bind.AllReformers.Count == 0)
                return result;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < bind.AllReformers.Count; i++)
            {
                string reformer = bind.AllReformers[i];
                if (string.IsNullOrWhiteSpace(reformer))
                    continue;

                ModifierPayload parsed = ParseReformerModifier(reformer);
                if (parsed == null || string.IsNullOrWhiteSpace(parsed.Name))
                    continue;

                string name = parsed.Name.Trim();
                if (seen.Contains(name))
                    continue;

                seen.Add(name);
                result.Add(parsed);
            }

            return result;
        }

        private static ModifierPayload ParseReformerModifier(string reformer)
        {
            if (string.IsNullOrWhiteSpace(reformer))
                return null;

            string[] parts = SplitReformerParts(reformer);
            string name = parts.Length > 0 ? (parts[0] ?? string.Empty).Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                return null;

            string device = parts.Length > 1 ? NormalizeOptionalString(parts[1]) : null;
            string key = parts.Length > 2 ? NormalizeOptionalString(parts[2]) : null;
            bool sw = false;
            if (parts.Length > 3)
                sw = TryParseBoolean(parts[3], false);

            Modifier lookup = null;
            if (InternalDataManagement.AllModifiers != null && InternalDataManagement.AllModifiers.ContainsKey(name))
                lookup = InternalDataManagement.AllModifiers[name];

            ModifierPayload payload = new ModifierPayload();
            payload.Name = name;
            payload.Device = !string.IsNullOrWhiteSpace(device)
                ? device
                : NormalizeOptionalString(lookup == null ? null : lookup.device) ?? "Keyboard";
            payload.Key = !string.IsNullOrWhiteSpace(key)
                ? key
                : NormalizeOptionalString(lookup == null ? null : lookup.key) ?? name;
            payload.Switch = parts.Length > 3
                ? sw
                : (lookup != null && lookup.sw);
            return payload;
        }

        private static string[] SplitReformerParts(string reformer)
        {
            string normalized = (reformer ?? string.Empty).Trim();
            if (normalized.Length == 0)
                return new string[0];

            string[] sectionParts = normalized.Split('\u00A7');
            if (sectionParts.Length >= 3)
                return sectionParts;

            string[] legacyParts = normalized.Split(new[] { "Â§" }, StringSplitOptions.None);
            if (legacyParts.Length >= 3)
                return legacyParts;

            return new[] { normalized };
        }

        private static bool TryParseBoolean(string value, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
                return defaultValue;

            bool parsedBool;
            if (bool.TryParse(value, out parsedBool))
                return parsedBool;

            int parsedInt;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedInt))
                return parsedInt != 0;

            return defaultValue;
        }

        private static Dictionary<string, ModifierPayload> CreateDefaultModifierMap()
        {
            Dictionary<string, ModifierPayload> result = new Dictionary<string, ModifierPayload>(StringComparer.OrdinalIgnoreCase);
            AddDefaultModifier(result, "LAlt");
            AddDefaultModifier(result, "LCtrl");
            AddDefaultModifier(result, "LShift");
            AddDefaultModifier(result, "LWin");
            AddDefaultModifier(result, "RAlt");
            AddDefaultModifier(result, "RCtrl");
            AddDefaultModifier(result, "RShift");
            AddDefaultModifier(result, "RWin");
            return result;
        }

        private static void AddDefaultModifier(Dictionary<string, ModifierPayload> target, string name)
        {
            if (target == null || string.IsNullOrWhiteSpace(name))
                return;
            target[name] = new ModifierPayload
            {
                Name = name,
                Device = "Keyboard",
                Key = name,
                Switch = false
            };
        }

        private static void AttachForceFeedbackPayloads(
            Dictionary<string, Dictionary<string, DiffPayload>> payloads,
            EngineDcsExportResult result)
        {
            if (payloads == null || payloads.Count == 0)
                return;
            bool hasSettings = InternalDataManagement.FfbOverrides != null && JoyPro.Engine.Axis.FfbSettings.DevicesFor(InternalDataManagement.FfbOverrides, "DCS").Any();
            if ((InternalDataManagement.JoystickFFB == null || InternalDataManagement.JoystickFFB.Count == 0) && !hasSettings)
                return;

            Dictionary<string, EngineDcsFfbPlaneCorrection> corrections =
                EngineDcsFfbCorrectionCatalog.Load(result.Warnings);

            foreach (KeyValuePair<string, Dictionary<string, DiffPayload>> planeEntry in payloads)
            {
                string plane = planeEntry.Key;
                Dictionary<string, DiffPayload> devices = planeEntry.Value;
                if (devices == null)
                    continue;

                foreach (KeyValuePair<string, DiffPayload> deviceEntry in devices)
                {
                    string device = deviceEntry.Key ?? string.Empty;
                    if (string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool legacy = InternalDataManagement.JoystickFFB != null && TryResolveForceFeedbackSetting(device, out ForceFeedbackS setting) && setting != null;
                    Dictionary<string, double> settings = JoyPro.Engine.Axis.FfbSettings.Resolve(InternalDataManagement.FfbOverrides, device, "DCS", plane);
                    if (!legacy && settings.Count == 0)
                        continue;

                    DiffForceFeedback ffb = legacy
                        ? BuildPlaneAdjustedForceFeedback(InternalDataManagement.JoystickFFB.ContainsKey(device) ? InternalDataManagement.JoystickFFB[device] : ResolveForceFeedbackOrDefault(device), plane, corrections)
                        : new DiffForceFeedback();
                    ApplyForceFeedbackSettings(ffb, settings);
                    deviceEntry.Value.HasFfb = true;
                    deviceEntry.Value.Ffb = ffb;
                }
            }
        }

        private static ForceFeedbackS ResolveForceFeedbackOrDefault(string device)
        {
            ForceFeedbackS setting;
            return TryResolveForceFeedbackSetting(device, out setting) && setting != null ? setting : new ForceFeedbackS();
        }

        // The device's FFB settings for this aircraft (engine ffb set): flags replace JoyPro's invert/swap, trimmer,
        // shake and ignore are written next to them.
        private static void ApplyForceFeedbackSettings(DiffForceFeedback ffb, Dictionary<string, double> settings)
        {
            double value;
            if (settings.TryGetValue("invertX", out value)) { ffb.InvertX = value != 0; ffb.HasFlags = true; }
            if (settings.TryGetValue("invertY", out value)) { ffb.InvertY = value != 0; ffb.HasFlags = true; }
            if (settings.TryGetValue("swapAxes", out value)) { ffb.SwapAxes = value != 0; ffb.HasFlags = true; }
            if (settings.TryGetValue("ignore", out value))
                ffb.Other["ignore"] = value != 0 ? "true" : "false";
            if (settings.TryGetValue("trimmer", out value))
                ffb.Other["trimmer"] = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            if (settings.TryGetValue("shake", out value))
                ffb.Other["shake"] = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool TryResolveForceFeedbackSetting(string device, out ForceFeedbackS setting)
        {
            setting = null;
            string key = (device ?? string.Empty).Trim();
            if (key.Length == 0 || InternalDataManagement.JoystickFFB == null)
                return false;

            if (InternalDataManagement.JoystickFFB.TryGetValue(key, out setting) && setting != null)
                return true;

            string baseName = StripDeviceGuidSuffix(key);
            List<KeyValuePair<string, ForceFeedbackS>> baseMatches = InternalDataManagement.JoystickFFB
                .Where(kvp =>
                    kvp.Value != null &&
                    string.Equals(StripDeviceGuidSuffix(kvp.Key), baseName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (baseMatches.Count == 0)
                return false;

            setting = baseMatches[0].Value;
            return setting != null;
        }

        private static DiffForceFeedback BuildPlaneAdjustedForceFeedback(
            ForceFeedbackS setting,
            string plane,
            Dictionary<string, EngineDcsFfbPlaneCorrection> corrections)
        {
            DiffForceFeedback output = new DiffForceFeedback();
            output.InvertX = setting != null && setting.invertX;
            output.InvertY = setting != null && setting.invertY;
            output.SwapAxes = setting != null && setting.swapAxis;
            output.HasFlags = true;

            if (corrections == null || string.IsNullOrWhiteSpace(plane))
                return output;
            if (!corrections.TryGetValue(plane, out EngineDcsFfbPlaneCorrection correction))
                return output;
            if (correction == null)
                return output;

            if (correction.InvertX)
                output.InvertX = !output.InvertX;
            if (correction.InvertY)
                output.InvertY = !output.InvertY;
            if (correction.SwapAxis)
                output.SwapAxes = !output.SwapAxes;
            return output;
        }

        private static AxisFilter BuildAxisFilter(Bind bind, bool isAxis)
        {
            if (!isAxis || bind == null)
                return new AxisFilter();
            return BuildAxisFilter(JoyPro.Engine.Axis.BindTuning.Base(bind));
        }

        // Only values that differ from DCS's defaults are written.
        private static AxisFilter BuildAxisFilter(JoyPro.Engine.Axis.AxisTuning bind)
        {
            AxisFilter filter = new AxisFilter();
            bool inverted = bind.Inverted;
            bool slider = bind.Slider;

            if (inverted != Bind.Inverted_Default)
            {
                filter.HasInverted = true;
                filter.Inverted = inverted;
            }
            if (slider != Bind.Slider_Default)
            {
                filter.HasSlider = true;
                filter.Slider = slider;
            }
            if (!NearlyEqual(bind.Deadzone, Bind.Deadzone_Default))
            {
                filter.HasDeadzone = true;
                filter.Deadzone = bind.Deadzone;
            }
            if (!NearlyEqual(bind.SaturationX, Bind.SaturationX_Default))
            {
                filter.HasSaturationX = true;
                filter.SaturationX = bind.SaturationX;
            }
            if (!NearlyEqual(bind.SaturationY, Bind.SaturationY_Default))
            {
                filter.HasSaturationY = true;
                filter.SaturationY = bind.SaturationY;
            }

            List<double> curvature = NormalizeCurvature(bind.Curvature);
            if (!CurvatureEquals(curvature, Bind.Curvature_Default))
                filter.Curvature = curvature;

            return filter;
        }

        private static bool AddEntry(
            DiffPayload payload,
            string id,
            string name,
            string key,
            bool isAxis,
            List<string> reformers,
            string relationName,
            List<string> groups,
            AxisFilter axisFilter)
        {
            Dictionary<string, DiffEntry> target = isAxis ? payload.Axis : payload.Keys;
            if (!target.ContainsKey(id))
            {
                DiffEntry entry = new DiffEntry();
                entry.Name = name;
                target.Add(id, entry);
            }

            DiffEntry refEntry = target[id];
            if (string.IsNullOrWhiteSpace(refEntry.Name) && !string.IsNullOrWhiteSpace(name))
                refEntry.Name = name;

            DiffAdded added = new DiffAdded();
            added.Key = key;
            added.Reformers = reformers == null ? new List<string>() : new List<string>(reformers);
            added.RelationName = relationName ?? string.Empty;
            added.Groups = groups == null ? new List<string>() : new List<string>(groups);
            if (isAxis && axisFilter != null)
                ApplyAxisFilter(added, axisFilter);

            string signature = BuildAddedSignature(added, true);
            if (refEntry.Signatures.Contains(signature))
                return false;

            refEntry.Added.Add(added);
            refEntry.Signatures.Add(signature);
            return true;
        }

        private static DiffPayload MergePayloads(
            EngineDcsExportMode mode,
            DiffPayload existing,
            DiffPayload generated,
            EngineDcsExportResult result)
        {
            existing = existing ?? new DiffPayload();
            generated = generated ?? new DiffPayload();

            DiffPayload merged = BuildMergedPayload(mode, existing, generated, result);
            KeepOtherForceFeedbackSettings(merged, existing, mode == EngineDcsExportMode.Merge);
            return merged;
        }

        // The device's own force feedback settings that JoyPro does not manage (trimmer, shake, ignore) are not
        // binds: every mode keeps them.
        private static void KeepOtherForceFeedbackSettings(DiffPayload merged, DiffPayload existing, bool existingWins)
        {
            if (existing == null || existing.Ffb == null || existing.Ffb.Other.Count == 0)
                return;
            foreach (KeyValuePair<string, string> other in existing.Ffb.Other)
            {
                if (existingWins || !merged.Ffb.Other.ContainsKey(other.Key))
                    merged.Ffb.Other[other.Key] = other.Value;
            }
            merged.HasFfb = true;
        }

        private static DiffPayload BuildMergedPayload(
            EngineDcsExportMode mode,
            DiffPayload existing,
            DiffPayload generated,
            EngineDcsExportResult result)
        {
            DiffPayload merged = new DiffPayload();
            switch (mode)
            {
                case EngineDcsExportMode.Clean:
                    merged.Axis = CloneSection(generated.Axis);
                    merged.Keys = CloneSection(generated.Keys);
                    merged.HasFfb = generated.HasFfb;
                    merged.Ffb = CloneForceFeedback(generated.Ffb);
                    return merged;

                case EngineDcsExportMode.Merge:
                    merged.Axis = MergeSectionKeepExisting(existing.Axis, generated.Axis, result);
                    merged.Keys = MergeSectionKeepExisting(existing.Keys, generated.Keys, result);
                    MergeForceFeedbackSettings(merged, existing, generated, keepExistingOnConflict: true);
                    return merged;

                case EngineDcsExportMode.MergeOverwrite:
                    merged.Axis = MergeSectionOverwrite(existing.Axis, generated.Axis, result);
                    merged.Keys = MergeSectionOverwrite(existing.Keys, generated.Keys, result);
                    MergeForceFeedbackSettings(merged, existing, generated, keepExistingOnConflict: false);
                    return merged;

                case EngineDcsExportMode.Add:
                    merged.Axis = MergeSectionAdd(existing.Axis, generated.Axis, result);
                    merged.Keys = MergeSectionAdd(existing.Keys, generated.Keys, result);
                    MergeForceFeedbackSettings(merged, existing, generated, keepExistingOnConflict: true);
                    return merged;

                default:
                    merged.Axis = CloneSection(generated.Axis);
                    merged.Keys = CloneSection(generated.Keys);
                    merged.HasFfb = generated.HasFfb;
                    merged.Ffb = CloneForceFeedback(generated.Ffb);
                    return merged;
            }
        }

        private static void MergeForceFeedbackSettings(
            DiffPayload merged,
            DiffPayload existing,
            DiffPayload generated,
            bool keepExistingOnConflict)
        {
            if (merged == null)
                return;

            bool existingHas = existing != null && existing.HasFfb;
            bool generatedHas = generated != null && generated.HasFfb;

            if (existingHas && generatedHas)
            {
                merged.HasFfb = true;
                merged.Ffb = keepExistingOnConflict
                    ? CloneForceFeedback(existing.Ffb)
                    : CloneForceFeedback(generated.Ffb);
                return;
            }

            if (generatedHas)
            {
                merged.HasFfb = true;
                merged.Ffb = CloneForceFeedback(generated.Ffb);
                return;
            }

            if (existingHas)
            {
                merged.HasFfb = true;
                merged.Ffb = CloneForceFeedback(existing.Ffb);
                return;
            }

            merged.HasFfb = false;
            merged.Ffb = new DiffForceFeedback();
        }

        private static Dictionary<string, DiffEntry> MergeSectionKeepExisting(
            Dictionary<string, DiffEntry> existing,
            Dictionary<string, DiffEntry> generated,
            EngineDcsExportResult result)
        {
            Dictionary<string, DiffEntry> merged = CloneSection(existing);
            generated = generated ?? new Dictionary<string, DiffEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, DiffEntry> entry in generated)
            {
                DiffEntry target;
                if (!merged.TryGetValue(entry.Key, out target))
                {
                    merged.Add(entry.Key, CloneEntry(entry.Value));
                    continue;
                }
                if (target.Added.Count > 0)
                {
                    result.MergeConflictsKept++;
                    continue;
                }

                // The file only drops defaults on this action: no bind to keep, the profile's bind goes in.
                DiffEntry source = entry.Value ?? new DiffEntry();
                if (string.IsNullOrWhiteSpace(target.Name))
                    target.Name = source.Name;
                foreach (DiffAdded add in source.Added)
                {
                    DiffAdded copy = CloneAdded(add);
                    target.Added.Add(copy);
                    target.Signatures.Add(BuildAddedSignature(copy, true));
                }
                foreach (DiffRemoved removed in source.Removed)
                    AddRemoved(target, removed);
            }

            return merged;
        }

        private static Dictionary<string, DiffEntry> MergeSectionOverwrite(
            Dictionary<string, DiffEntry> existing,
            Dictionary<string, DiffEntry> generated,
            EngineDcsExportResult result)
        {
            Dictionary<string, DiffEntry> merged = CloneSection(existing);
            generated = generated ?? new Dictionary<string, DiffEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, DiffEntry> entry in generated)
            {
                DiffEntry replacement = CloneEntry(entry.Value);
                DiffEntry old;
                if (merged.TryGetValue(entry.Key, out old))
                {
                    result.MergeConflictsOverwritten++;
                    // The action's binds are replaced; defaults the file removed stay removed.
                    foreach (DiffRemoved removed in old.Removed)
                        AddRemoved(replacement, removed);
                }
                merged[entry.Key] = replacement;
            }

            return merged;
        }

        private static Dictionary<string, DiffEntry> MergeSectionAdd(
            Dictionary<string, DiffEntry> existing,
            Dictionary<string, DiffEntry> generated,
            EngineDcsExportResult result)
        {
            Dictionary<string, DiffEntry> merged = CloneSection(existing);
            generated = generated ?? new Dictionary<string, DiffEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, DiffEntry> entry in generated)
            {
                if (!merged.ContainsKey(entry.Key))
                {
                    merged.Add(entry.Key, CloneEntry(entry.Value));
                    continue;
                }

                result.MergeConflictsAdded++;
                DiffEntry target = merged[entry.Key];
                DiffEntry source = entry.Value ?? new DiffEntry();
                if (string.IsNullOrWhiteSpace(target.Name) && !string.IsNullOrWhiteSpace(source.Name))
                    target.Name = source.Name;

                for (int i = 0; i < source.Added.Count; i++)
                {
                    DiffAdded incoming = source.Added[i];
                    string signature = BuildAddedSignature(incoming, true);
                    if (target.Signatures.Contains(signature))
                        continue;

                    target.Added.Add(CloneAdded(incoming));
                    target.Signatures.Add(signature);
                }
                foreach (DiffRemoved removed in source.Removed)
                    AddRemoved(target, removed);
            }

            return merged;
        }

        private static DiffPayload LoadExistingPayload(string diffFilePath, string backupFilePath, EngineDcsExportResult result, DiffPayload clean = null)
        {
            DiffPayload diffPayload = null;
            DiffPayload backupPayload = null;

            if (File.Exists(diffFilePath))
            {
                diffPayload = ParsePayloadFile(diffFilePath, result);
                if (diffPayload != null)
                {
                    result.ExistingFilesParsed++;
                    result.ExistingDiffFilesParsed++;
                }
            }

            if (File.Exists(backupFilePath))
            {
                backupPayload = ParsePayloadFile(backupFilePath, result);
                if (backupPayload != null)
                {
                    result.ExistingFilesParsed++;
                    result.ExistingBackupFilesParsed++;
                }
            }

            if (diffPayload == null && backupPayload == null)
                return new DiffPayload();
            if (diffPayload == null)
                return backupPayload;
            if (backupPayload == null)
                return diffPayload;

            DiffPayload existing = OverlayBackupMetadata(diffPayload, backupPayload);
            RestoreNormalizedDefaults(existing, backupPayload, clean);
            return existing;
        }

        // DCS writes a diff only against the plane's defaults: when it saves a file JoyPro wrote, a bind equal to a
        // default (JOY_Y on pitch) disappears from the diff although it is still active. The .jp still has it, so a
        // default bind JoyPro wrote that the diff neither has nor removes is still the file's bind.
        private static void RestoreNormalizedDefaults(DiffPayload existing, DiffPayload backup, DiffPayload clean)
        {
            if (existing == null || backup == null || clean == null)
                return;
            RestoreNormalizedSection(existing.Axis, backup.Axis, clean.Axis);
            RestoreNormalizedSection(existing.Keys, backup.Keys, clean.Keys);
        }

        private static void RestoreNormalizedSection(
            Dictionary<string, DiffEntry> existing,
            Dictionary<string, DiffEntry> backup,
            Dictionary<string, DiffEntry> clean)
        {
            foreach (KeyValuePair<string, DiffEntry> backupItem in backup)
            {
                DiffEntry cleanEntry;
                if (backupItem.Value == null || !clean.TryGetValue(backupItem.Key, out cleanEntry) || cleanEntry == null)
                    continue;
                HashSet<string> defaults = new HashSet<string>(cleanEntry.Removed.Select(r => RemovedSignature(r.Key, r.Reformers)), StringComparer.Ordinal);
                DiffEntry target;
                existing.TryGetValue(backupItem.Key, out target);
                foreach (DiffAdded add in backupItem.Value.Added)
                {
                    string signature = RemovedSignature(add.Key, add.Reformers);
                    if (!defaults.Contains(signature))
                        continue;
                    if (target != null &&
                        (target.Removed.Any(r => RemovedSignature(r.Key, r.Reformers) == signature) ||
                         target.Added.Any(a => RemovedSignature(a.Key, a.Reformers) == signature)))
                        continue;
                    if (target == null)
                    {
                        target = new DiffEntry();
                        target.Name = backupItem.Value.Name;
                        existing.Add(backupItem.Key, target);
                    }
                    DiffAdded restored = CloneAdded(add);
                    target.Added.Add(restored);
                    target.Signatures.Add(BuildAddedSignature(restored, true));
                }
            }
        }

        private static DiffPayload OverlayBackupMetadata(DiffPayload basePayload, DiffPayload backupPayload)
        {
            DiffPayload merged = ClonePayload(basePayload);
            OverlaySectionMetadata(merged.Axis, backupPayload.Axis);
            OverlaySectionMetadata(merged.Keys, backupPayload.Keys);
            if (!merged.HasFfb && backupPayload != null && backupPayload.HasFfb)
            {
                merged.HasFfb = true;
                merged.Ffb = CloneForceFeedback(backupPayload.Ffb);
            }
            return merged;
        }

        private static void OverlaySectionMetadata(
            Dictionary<string, DiffEntry> target,
            Dictionary<string, DiffEntry> backup)
        {
            if (target == null || backup == null)
                return;

            foreach (KeyValuePair<string, DiffEntry> targetItem in target)
            {
                if (!backup.ContainsKey(targetItem.Key))
                    continue;

                DiffEntry targetEntry = targetItem.Value;
                DiffEntry backupEntry = backup[targetItem.Key];
                if (targetEntry == null || backupEntry == null)
                    continue;

                if (string.IsNullOrWhiteSpace(targetEntry.Name) && !string.IsNullOrWhiteSpace(backupEntry.Name))
                    targetEntry.Name = backupEntry.Name;

                Dictionary<string, DiffAdded> backupBySignature = new Dictionary<string, DiffAdded>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < backupEntry.Added.Count; i++)
                {
                    DiffAdded add = backupEntry.Added[i];
                    string signature = BuildAddedSignature(add, false);
                    if (!backupBySignature.ContainsKey(signature))
                        backupBySignature.Add(signature, add);
                }

                for (int i = 0; i < targetEntry.Added.Count; i++)
                {
                    DiffAdded current = targetEntry.Added[i];
                    string signature = BuildAddedSignature(current, false);
                    if (!backupBySignature.ContainsKey(signature))
                        continue;

                    DiffAdded source = backupBySignature[signature];
                    if (!string.IsNullOrWhiteSpace(source.RelationName))
                        current.RelationName = source.RelationName;
                    if (source.Groups != null && source.Groups.Count > 0)
                        current.Groups = NormalizeList(source.Groups);

                    if (!current.HasInverted && source.HasInverted)
                    {
                        current.HasInverted = true;
                        current.Inverted = source.Inverted;
                    }
                    if (!current.HasSlider && source.HasSlider)
                    {
                        current.HasSlider = true;
                        current.Slider = source.Slider;
                    }
                    if (!current.HasDeadzone && source.HasDeadzone)
                    {
                        current.HasDeadzone = true;
                        current.Deadzone = source.Deadzone;
                    }
                    if ((current.Curvature == null || current.Curvature.Count == 0) && source.Curvature != null && source.Curvature.Count > 0)
                        current.Curvature = new List<double>(source.Curvature);
                    if (!current.HasSaturationX && source.HasSaturationX)
                    {
                        current.HasSaturationX = true;
                        current.SaturationX = source.SaturationX;
                    }
                    if (!current.HasSaturationY && source.HasSaturationY)
                    {
                        current.HasSaturationY = true;
                        current.SaturationY = source.SaturationY;
                    }
                }

                RebuildSignatures(targetEntry);
            }
        }

        private static DiffPayload ParsePayloadFile(string filePath, EngineDcsExportResult result)
        {
            try
            {
                string text = RunFileCache.ReadAllText(filePath);
                DiffPayload payload = new DiffPayload();
                ParseSectionIntoPayload(text, "axisDiffs", payload.Axis);
                ParseSectionIntoPayload(text, "keyDiffs", payload.Keys);
                ParseForceFeedbackSection(text, payload);
                return payload;
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Could not parse existing DCS file '" + filePath + "': " + ex.Message);
                return null;
            }
        }

        private static void ParseSectionIntoPayload(string fileText, string sectionName, Dictionary<string, DiffEntry> target)
        {
            string sectionBlock;
            if (!TryExtractNamedBlock(fileText, sectionName, out sectionBlock))
                return;

            List<SectionEntry> entries = ParseTopLevelEntries(sectionBlock);
            for (int i = 0; i < entries.Count; i++)
            {
                SectionEntry entry = entries[i];
                if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Block))
                    continue;

                DiffEntry parsedEntry = new DiffEntry();
                parsedEntry.Name = ExtractEntryName(entry.Block);
                List<DiffAdded> addedEntries = ExtractAddedEntries(entry.Block);
                for (int a = 0; a < addedEntries.Count; a++)
                {
                    DiffAdded add = addedEntries[a];
                    string signature = BuildAddedSignature(add, true);
                    if (parsedEntry.Signatures.Contains(signature))
                        continue;

                    parsedEntry.Added.Add(add);
                    parsedEntry.Signatures.Add(signature);
                }
                foreach (DiffRemoved removed in ExtractRemovedEntries(entry.Block))
                    AddRemoved(parsedEntry, removed);

                if (!target.ContainsKey(entry.Id))
                {
                    target.Add(entry.Id, parsedEntry);
                }
                else
                {
                    DiffEntry existing = target[entry.Id];
                    if (string.IsNullOrWhiteSpace(existing.Name) && !string.IsNullOrWhiteSpace(parsedEntry.Name))
                        existing.Name = parsedEntry.Name;

                    for (int a = 0; a < parsedEntry.Added.Count; a++)
                    {
                        DiffAdded add = parsedEntry.Added[a];
                        string signature = BuildAddedSignature(add, true);
                        if (existing.Signatures.Contains(signature))
                            continue;
                        existing.Added.Add(add);
                        existing.Signatures.Add(signature);
                    }
                    foreach (DiffRemoved removed in parsedEntry.Removed)
                        AddRemoved(existing, removed);
                }
            }
        }

        private static void ParseForceFeedbackSection(string fileText, DiffPayload payload)
        {
            if (payload == null)
                return;

            string ffBlock;
            if (!TryExtractNamedBlock(fileText, "ffDiffs", out ffBlock))
                return;

            bool hasInvertX = CachedRegex.IsMatch(ffBlock, "\\[\"invertX\"\\]\\s*=", RegexOptions.IgnoreCase);
            bool hasInvertY = CachedRegex.IsMatch(ffBlock, "\\[\"invertY\"\\]\\s*=", RegexOptions.IgnoreCase);
            bool hasSwapAxes = CachedRegex.IsMatch(ffBlock, "\\[\"swapAxes\"\\]\\s*=", RegexOptions.IgnoreCase);
            bool hasSwapAxis = CachedRegex.IsMatch(ffBlock, "\\[\"swapAxis\"\\]\\s*=", RegexOptions.IgnoreCase);

            // DCS's other force feedback settings of the device (trimmer, shake, ignore, ...) are kept as written.
            foreach (Match other in CachedRegex.Matches(ffBlock, "\\[\"(?<key>[A-Za-z_][A-Za-z0-9_]*)\"\\]\\s*=\\s*(?<value>[^,\\r\\n{}]+?)\\s*,"))
            {
                string key = other.Groups["key"].Value;
                if (FfbFlagKeys.Contains(key) || payload.Ffb.Other.ContainsKey(key))
                    continue;
                payload.Ffb.Other[key] = other.Groups["value"].Value.Trim();
            }
            if (!hasInvertX && !hasInvertY && !hasSwapAxes && !hasSwapAxis)
            {
                payload.HasFfb = payload.Ffb.Other.Count > 0;
                return;
            }

            payload.HasFfb = true;
            payload.Ffb.HasFlags = true;
            payload.Ffb.InvertX = hasInvertX && ExtractNamedBool(ffBlock, "invertX", false);
            payload.Ffb.InvertY = hasInvertY && ExtractNamedBool(ffBlock, "invertY", false);
            payload.Ffb.SwapAxes = hasSwapAxes
                ? ExtractNamedBool(ffBlock, "swapAxes", false)
                : (hasSwapAxis && ExtractNamedBool(ffBlock, "swapAxis", false));
        }

        private static List<DiffAdded> ExtractAddedEntries(string entryBlock)
        {
            List<DiffAdded> result = new List<DiffAdded>();
            string addedBlock;
            if (!TryExtractNamedBlock(entryBlock, "added", out addedBlock))
                return result;

            List<SectionEntry> items = ParseTopLevelEntries(addedBlock);
            for (int i = 0; i < items.Count; i++)
            {
                SectionEntry item = items[i];
                string key = ExtractNamedString(item.Block, "key");
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                DiffAdded entry = new DiffAdded();
                entry.Key = key.Trim();
                entry.Reformers = ExtractNamedStringArray(item.Block, "reformers");
                entry.RelationName = NormalizeOptionalString(ExtractNamedString(item.Block, "JPK"));
                entry.Groups = ExtractNamedStringArray(item.Block, "JGRP");
                PopulateAxisFilter(entry, item.Block);
                result.Add(entry);
            }

            return result;
        }

        private static List<DiffRemoved> ExtractRemovedEntries(string entryBlock)
        {
            List<DiffRemoved> result = new List<DiffRemoved>();
            string removedBlock;
            if (!TryExtractNamedBlock(entryBlock, "removed", out removedBlock))
                return result;

            foreach (SectionEntry item in ParseTopLevelEntries(removedBlock))
            {
                string key = ExtractNamedString(item.Block, "key");
                if (!string.IsNullOrWhiteSpace(key))
                    result.Add(new DiffRemoved { Key = key.Trim(), Reformers = ExtractNamedStringArray(item.Block, "reformers") });
            }
            return result;
        }

        private static string RenderDiffLua(DiffPayload payload, bool includeBackupMetadata)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("local diff = {");
            sb.AppendLine("  [\"axisDiffs\"] = {");
            RenderSection(sb, payload.Axis, includeBackupMetadata, true);
            sb.AppendLine("  },");
            if (payload.HasFfb)
            {
                // Keys in DCS's order (sorted): JoyPro's three flags among the device's other settings.
                SortedDictionary<string, string> values = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> other in payload.Ffb.Other)
                    values[other.Key] = other.Value;
                if (payload.Ffb.HasFlags)
                {
                    values["invertX"] = payload.Ffb.InvertX ? "true" : "false";
                    values["invertY"] = payload.Ffb.InvertY ? "true" : "false";
                    values["swapAxes"] = payload.Ffb.SwapAxes ? "true" : "false";
                }
                sb.AppendLine("  [\"ffDiffs\"] = {");
                foreach (KeyValuePair<string, string> value in values)
                    sb.AppendLine("    [\"" + value.Key + "\"] = " + value.Value + ",");
                sb.AppendLine("  },");
            }
            sb.AppendLine("  [\"keyDiffs\"] = {");
            RenderSection(sb, payload.Keys, includeBackupMetadata, false);
            sb.AppendLine("  },");
            sb.AppendLine("}");
            sb.AppendLine("return diff");
            return sb.ToString();
        }

        private static void RenderSection(
            StringBuilder sb,
            Dictionary<string, DiffEntry> entries,
            bool includeBackupMetadata,
            bool isAxis)
        {
            string[] ids = entries.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                DiffEntry entry = entries[id];
                sb.AppendLine("    [\"" + EscapeLua(id) + "\"] = {");
                sb.AppendLine("      [\"name\"] = \"" + EscapeLua(entry.Name) + "\",");
                if (entry.Removed.Count > 0)
                {
                    sb.AppendLine("      [\"removed\"] = {");
                    for (int r = 0; r < entry.Removed.Count; r++)
                    {
                        DiffRemoved removed = entry.Removed[r];
                        sb.AppendLine("        [" + (r + 1) + "] = {");
                        sb.AppendLine("          [\"key\"] = \"" + EscapeLua(removed.Key) + "\",");
                        if (removed.Reformers != null && removed.Reformers.Count > 0)
                        {
                            sb.AppendLine("          [\"reformers\"] = {");
                            for (int f = 0; f < removed.Reformers.Count; f++)
                                sb.AppendLine("            [" + (f + 1) + "] = \"" + EscapeLua(removed.Reformers[f]) + "\",");
                            sb.AppendLine("          },");
                        }
                        sb.AppendLine("        },");
                    }
                    sb.AppendLine("      },");
                    if (entry.Added.Count == 0)
                    {
                        sb.AppendLine("    },");
                        continue;
                    }
                }
                sb.AppendLine("      [\"added\"] = {");
                for (int a = 0; a < entry.Added.Count; a++)
                {
                    DiffAdded add = entry.Added[a];
                    sb.AppendLine("        [" + (a + 1) + "] = {");
                    sb.AppendLine("          [\"key\"] = \"" + EscapeLua(add.Key) + "\",");
                    if (add.Reformers != null && add.Reformers.Count > 0)
                    {
                        sb.AppendLine("          [\"reformers\"] = {");
                        for (int r = 0; r < add.Reformers.Count; r++)
                            sb.AppendLine("            [" + (r + 1) + "] = \"" + EscapeLua(add.Reformers[r]) + "\",");
                        sb.AppendLine("          },");
                    }

                    if (isAxis && HasAxisFilter(add))
                        RenderAxisFilter(sb, add);

                    if (includeBackupMetadata)
                    {
                        if (!string.IsNullOrWhiteSpace(add.RelationName))
                            sb.AppendLine("          [\"JPK\"] = \"" + EscapeLua(add.RelationName) + "\",");

                        if (add.Groups != null && add.Groups.Count > 0)
                        {
                            sb.AppendLine("          [\"JGRP\"] = {");
                            for (int g = 0; g < add.Groups.Count; g++)
                                sb.AppendLine("            [" + (g + 1) + "] = \"" + EscapeLua(add.Groups[g]) + "\",");
                            sb.AppendLine("          },");
                        }
                    }

                    sb.AppendLine("        },");
                }
                sb.AppendLine("      },");
                sb.AppendLine("    },");
            }
        }

        private static void RenderAxisFilter(StringBuilder sb, DiffAdded add)
        {
            sb.AppendLine("          [\"filter\"] = {");
            if (add.HasInverted)
                sb.AppendLine("            [\"invert\"] = " + (add.Inverted ? "true" : "false") + ",");
            if (add.HasSlider)
                sb.AppendLine("            [\"slider\"] = " + (add.Slider ? "true" : "false") + ",");
            if (add.HasDeadzone)
                sb.AppendLine("            [\"deadzone\"] = " + ToInvariant(add.Deadzone) + ",");
            if (add.Curvature != null && add.Curvature.Count > 0)
            {
                sb.AppendLine("            [\"curvature\"] = {");
                for (int i = 0; i < add.Curvature.Count; i++)
                    sb.AppendLine("              [" + (i + 1) + "] = " + ToInvariant(add.Curvature[i]) + ",");
                sb.AppendLine("            },");
            }
            if (add.HasSaturationX)
                sb.AppendLine("            [\"saturationX\"] = " + ToInvariant(add.SaturationX) + ",");
            if (add.HasSaturationY)
                sb.AppendLine("            [\"saturationY\"] = " + ToInvariant(add.SaturationY) + ",");
            sb.AppendLine("          },");
        }

        private static void WritePayloadFile(
            string outputDirectory,
            string filePath,
            DiffPayload payload,
            bool includeBackupMetadata,
            EngineDcsExportResult result)
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);
                GameBackups.Protect(filePath, result.Warnings);
                RunFileCache.WriteAllText(filePath, RenderDiffLua(payload, includeBackupMetadata));
                result.FilesWritten++;
                if (includeBackupMetadata)
                    result.BackupFilesWritten++;
                result.WrittenFiles.Add(filePath);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Failed writing file '" + filePath + "': " + ex.Message);
            }
        }

        private static void MergeAndWriteModifiers(
            EngineDcsExportMode mode,
            string planeDirectory,
            Dictionary<string, ModifierPayload> generated,
            EngineDcsExportResult result)
        {
            if (string.IsNullOrWhiteSpace(planeDirectory))
                return;

            Dictionary<string, ModifierPayload> existing = new Dictionary<string, ModifierPayload>(StringComparer.OrdinalIgnoreCase);
            string modifiersPath = Path.Combine(planeDirectory, "modifiers.lua");
            if (mode != EngineDcsExportMode.Clean && File.Exists(modifiersPath))
                existing = ParseModifiersFile(modifiersPath, result);

            Dictionary<string, ModifierPayload> merged;
            switch (mode)
            {
                case EngineDcsExportMode.Clean:
                    merged = CloneModifierSection(generated);
                    break;
                case EngineDcsExportMode.Merge:
                    merged = MergeModifierSectionKeepExisting(existing, generated);
                    break;
                case EngineDcsExportMode.MergeOverwrite:
                case EngineDcsExportMode.Add:
                    merged = MergeModifierSectionOverwrite(existing, generated);
                    break;
                default:
                    merged = CloneModifierSection(generated);
                    break;
            }

            WriteModifiersFile(planeDirectory, modifiersPath, merged, result);
        }

        private static Dictionary<string, ModifierPayload> ParseModifiersFile(string filePath, EngineDcsExportResult result)
        {
            Dictionary<string, ModifierPayload> parsed = new Dictionary<string, ModifierPayload>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string text = RunFileCache.ReadAllText(filePath);
                string modifiersBlock;
                if (!TryExtractModifiersBlock(text, out modifiersBlock))
                    return parsed;

                List<SectionEntry> entries = ParseTopLevelEntries(modifiersBlock);
                for (int i = 0; i < entries.Count; i++)
                {
                    SectionEntry entry = entries[i];
                    string name = NormalizeOptionalString(entry.Id);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    string jpk = NormalizeOptionalString(ExtractNamedString(entry.Block, "JPK"));
                    if (!string.IsNullOrWhiteSpace(jpk))
                        name = jpk;

                    ModifierPayload modifier = new ModifierPayload();
                    modifier.Name = name;
                    modifier.Device = NormalizeOptionalString(ExtractNamedString(entry.Block, "device")) ?? "Keyboard";
                    modifier.Key = NormalizeOptionalString(ExtractNamedString(entry.Block, "key")) ?? name;
                    modifier.Switch = ExtractNamedBool(entry.Block, "switch", false);
                    parsed[name] = modifier;
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Could not parse existing DCS modifiers.lua '" + filePath + "': " + ex.Message);
            }

            return parsed;
        }

        private static bool TryExtractModifiersBlock(string text, out string block)
        {
            block = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            Match match = CachedRegex.Match(text, "local\\s+modifiers\\s*=\\s*\\{", RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;

            int openBrace = text.IndexOf('{', match.Index);
            if (openBrace < 0)
                return false;

            int closeBrace;
            if (!TryFindMatchingBrace(text, openBrace, out closeBrace))
                return false;

            block = text.Substring(openBrace, closeBrace - openBrace + 1);
            return true;
        }

        private static Dictionary<string, ModifierPayload> MergeModifierSectionKeepExisting(
            Dictionary<string, ModifierPayload> existing,
            Dictionary<string, ModifierPayload> generated)
        {
            Dictionary<string, ModifierPayload> merged = CloneModifierSection(existing);
            generated = generated ?? new Dictionary<string, ModifierPayload>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, ModifierPayload> entry in generated)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                    continue;
                if (merged.ContainsKey(entry.Key))
                    continue;
                merged.Add(entry.Key, CloneModifier(entry.Value));
            }

            return merged;
        }

        private static Dictionary<string, ModifierPayload> MergeModifierSectionOverwrite(
            Dictionary<string, ModifierPayload> existing,
            Dictionary<string, ModifierPayload> generated)
        {
            Dictionary<string, ModifierPayload> merged = CloneModifierSection(existing);
            generated = generated ?? new Dictionary<string, ModifierPayload>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, ModifierPayload> entry in generated)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                    continue;
                merged[entry.Key] = CloneModifier(entry.Value);
            }

            return merged;
        }

        private static Dictionary<string, ModifierPayload> CloneModifierSection(Dictionary<string, ModifierPayload> source)
        {
            Dictionary<string, ModifierPayload> cloned = new Dictionary<string, ModifierPayload>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
                return cloned;

            foreach (KeyValuePair<string, ModifierPayload> entry in source)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                    continue;
                cloned[entry.Key] = CloneModifier(entry.Value);
            }

            return cloned;
        }

        private static ModifierPayload CloneModifier(ModifierPayload source)
        {
            if (source == null)
                return new ModifierPayload();

            return new ModifierPayload
            {
                Name = source.Name ?? string.Empty,
                Device = source.Device ?? string.Empty,
                Key = source.Key ?? string.Empty,
                Switch = source.Switch
            };
        }

        private static void WriteModifiersFile(
            string outputDirectory,
            string filePath,
            Dictionary<string, ModifierPayload> modifiers,
            EngineDcsExportResult result)
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);
                GameBackups.Protect(filePath, result.Warnings);
                RunFileCache.WriteAllText(filePath, RenderModifiersLua(modifiers));
                result.FilesWritten++;
                result.WrittenFiles.Add(filePath);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Failed writing file '" + filePath + "': " + ex.Message);
            }
        }

        private static string RenderModifiersLua(Dictionary<string, ModifierPayload> modifiers)
        {
            modifiers = modifiers ?? new Dictionary<string, ModifierPayload>(StringComparer.OrdinalIgnoreCase);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("local modifiers = {");

            string[] names = modifiers.Keys
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                ModifierPayload modifier = modifiers[name] ?? new ModifierPayload();
                string device = string.IsNullOrWhiteSpace(modifier.Device) ? "Keyboard" : modifier.Device.Trim();
                string key = string.IsNullOrWhiteSpace(modifier.Key) ? name : modifier.Key.Trim();

                sb.AppendLine("  [\"" + EscapeLua(name) + "\"] = {");
                sb.AppendLine("    [\"device\"] = \"" + EscapeLua(device) + "\",");
                sb.AppendLine("    [\"key\"] = \"" + EscapeLua(key) + "\",");
                sb.AppendLine("    [\"JPK\"] = \"" + EscapeLua(name) + "\",");
                sb.AppendLine("    [\"switch\"] = " + (modifier.Switch ? "true" : "false") + ",");
                sb.AppendLine("  },");
            }

            sb.AppendLine("}");
            sb.AppendLine("return modifiers");
            return sb.ToString();
        }

        // Input layers that are not an aircraft; nuke never touches them.
        private static readonly HashSet<string> NonAircraftLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Default", "UiLayer", "CameraFree", "CameraObject", "CameraRoom", "CommandMenu", "QuadMenu", "RadioVoIP",
            "VoiceChat", "ground_unit", "TrackIR", "Headtracker"
        };

        // JoyPro1 behaviour: in every plane the profile exports, each joystick device the profile does not use there
        // (connected, or with an existing file) gets a file that only removes the plane's DCS defaults, so it has no
        // binds at all. Force feedback settings of the file stay. Keyboard files and other planes are not touched.
        private static void NukeUnusedDevices(
            string inputPath,
            Dictionary<string, Dictionary<string, DiffPayload>> generatedPayloads,
            Dictionary<string, DiffPayload> cleanProfiles,
            EngineDcsExportResult result)
        {
            List<string> connected = Games.GameDeviceDirectory.Discover(result.Warnings).Devices
                .Where(d => d.Connected && !string.IsNullOrWhiteSpace(d.JoyProName))
                .Select(d => d.JoyProName.Trim())
                .ToList();
            List<string> withoutCleanProfile = new List<string>();

            foreach (KeyValuePair<string, Dictionary<string, DiffPayload>> plane in generatedPayloads.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (NonAircraftLayers.Contains(plane.Key))
                    continue;
                string joystickDir = Path.Combine(inputPath, plane.Key, "joystick");
                // Compared by JoyPro name: a file named as DCS names the device (spaces kept) is the same device.
                HashSet<string> used = new HashSet<string>(
                    plane.Value.Keys
                        .Where(d => !string.Equals(d, "Keyboard", StringComparison.OrdinalIgnoreCase))
                        .Select(d => DcsDeviceNames.JoyProName(SanitizeDcsFileName(DcsDeviceNames.DcsName(d, joystickDir)))),
                    StringComparer.OrdinalIgnoreCase);

                HashSet<string> devices = new HashSet<string>(
                    connected.Select(d => SanitizeDcsFileName(DcsDeviceNames.DcsName(d, joystickDir))),
                    StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(joystickDir))
                {
                    foreach (string file in Directory.GetFiles(joystickDir))
                    {
                        string name = Path.GetFileName(file);
                        if (name.EndsWith(".diff.lua", StringComparison.OrdinalIgnoreCase))
                            devices.Add(name.Substring(0, name.Length - ".diff.lua".Length));
                        else if (name.EndsWith(".jp", StringComparison.OrdinalIgnoreCase))
                            devices.Add(name.Substring(0, name.Length - ".jp".Length));
                    }
                }
                devices.RemoveWhere(d => used.Contains(DcsDeviceNames.JoyProName(d)));
                if (devices.Count == 0)
                    continue;

                foreach (string device in devices.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    DiffPayload clean = LoadCleanProfile(plane.Key, false, device, cleanProfiles, result);
                    if (clean == null && !withoutCleanProfile.Contains(plane.Key))
                        withoutCleanProfile.Add(plane.Key);
                    string diffFilePath = Path.Combine(joystickDir, device + ".diff.lua");
                    string backupFilePath = Path.Combine(joystickDir, device + ".jp");
                    DiffPayload existing = LoadExistingPayload(diffFilePath, backupFilePath, result);
                    DiffPayload nuked = new DiffPayload();
                    nuked.HasFfb = existing.HasFfb;
                    nuked.Ffb = CloneForceFeedback(existing.Ffb);
                    AddCleanProfileRemovals(nuked, clean);
                    WritePayloadFile(joystickDir, diffFilePath, nuked, false, result);
                    WritePayloadFile(joystickDir, backupFilePath, nuked, true, result);
                    result.DevicesCleared++;
                    result.ClearedFiles.Add(diffFilePath);
                }
            }

            if (withoutCleanProfile.Count > 0)
                result.Warnings.Add("Nuke emptied unused devices in " + string.Join(", ", withoutCleanProfile) + ", but without a clean profile DCS defaults can still apply there.");
        }

        // Clean mode: a written joystick device must have no binds but JoyPro's in every aircraft, not only in the
        // aircraft the profile binds. Every other aircraft with a clean profile (the installed modules of the last
        // DB refresh) gets a file for the device that only removes that aircraft's DCS defaults for that device type.
        // Force feedback settings of an existing file stay. Input layers that are no aircraft are never touched.
        private static void CleanOtherAircraft(
            string inputPath,
            Dictionary<string, Dictionary<string, DiffPayload>> generatedPayloads,
            Dictionary<string, DiffPayload> cleanProfiles,
            EngineDcsExportResult result,
            bool keepBinds = false)
        {
            List<string> devices = generatedPayloads.Values
                .SelectMany(p => p.Keys)
                .Where(d => !string.Equals(d, "Keyboard", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (devices.Count == 0)
                return;

            string cleanRoot = GameDataPaths.CleanProfileFolder("DCS", false);
            if (!Directory.Exists(cleanRoot))
            {
                result.Warnings.Add("No DCS clean profiles in " + cleanRoot + ": the other aircraft keep their binds. Refresh the DCS DB.");
                return;
            }

            // Every aircraft, also the exported ones: there a written device the profile binds in other aircraft
            // only has no payload of its own and would keep its DCS defaults.
            List<string> planes = Directory.GetFiles(cleanRoot, "*.cf")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(p => !string.IsNullOrWhiteSpace(p) && !NonAircraftLayers.Contains(p))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string plane in planes)
            {
                string joystickDir = Path.Combine(inputPath, plane, "joystick");
                Dictionary<string, DiffPayload> written;
                HashSet<string> writtenHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (generatedPayloads.TryGetValue(plane, out written))
                    foreach (string key in written.Keys)
                        writtenHere.Add(DcsDeviceNames.JoyProName(key.Trim()));
                foreach (string device in devices)
                {
                    if (writtenHere.Contains(DcsDeviceNames.JoyProName(device.Trim())))
                        continue;
                    DeviceFiles files = ResolveDeviceFiles(joystickDir, device, false);
                    DiffPayload clean = LoadCleanProfile(plane, false, files.DcsDevice, cleanProfiles, result);
                    if (clean == null)
                        continue;
                    DiffPayload cleaned;
                    if (keepBinds)
                    {
                        // Only the removals are added: the file's own binds, removals and force feedback stay.
                        if (!HasRemovals(clean))
                            continue;
                        cleaned = LoadExistingPayload(files.ExistingDiff, files.ExistingBackup, result, clean);
                        if (ContainsAllRemovals(cleaned, clean))
                            continue;
                        AddCleanProfileRemovals(cleaned, clean);
                        DropRemovalsOfAddedBinds(cleaned);
                    }
                    else
                    {
                        DiffPayload existing = LoadExistingPayload(files.ExistingDiff, files.ExistingBackup, result);
                        cleaned = new DiffPayload();
                        cleaned.HasFfb = existing.HasFfb;
                        cleaned.Ffb = CloneForceFeedback(existing.Ffb);
                        AddCleanProfileRemovals(cleaned, clean);
                    }
                    WritePayloadFile(joystickDir, files.Diff, cleaned, false, result);
                    WritePayloadFile(joystickDir, files.Backup, cleaned, true, result);
                    RemoveLegacyFiles(files, result);
                    result.OtherAircraftFilesCleaned++;
                }
            }
        }

        // The files of one device in one input folder. Joysticks are named as DCS names them (DcsDeviceNames); a file
        // an older JoyPro wrote under the trimmed JoyPro name ("legacy") is read when the DCS-named one does not exist
        // yet and removed after the write, so DCS finds the device's binds and import sees one file per device.
        private sealed class DeviceFiles
        {
            public string DcsDevice;
            public string Diff;
            public string Backup;
            public string ExistingDiff;
            public string ExistingBackup;
            public string LegacyDiff;
            public string LegacyBackup;
        }

        private static DeviceFiles ResolveDeviceFiles(string folder, string device, bool keyboard)
        {
            DeviceFiles files = new DeviceFiles();
            files.DcsDevice = keyboard ? device : DcsDeviceNames.DcsName(device, folder);
            string baseName = keyboard ? SanitizeFileName(device) : SanitizeDcsFileName(files.DcsDevice);
            files.Diff = Path.Combine(folder, baseName + ".diff.lua");
            files.Backup = Path.Combine(folder, baseName + ".jp");
            files.ExistingDiff = files.Diff;
            files.ExistingBackup = files.Backup;

            string legacyName = SanitizeFileName(device);
            if (!keyboard && !string.Equals(legacyName, baseName, StringComparison.Ordinal))
            {
                files.LegacyDiff = Path.Combine(folder, legacyName + ".diff.lua");
                files.LegacyBackup = Path.Combine(folder, legacyName + ".jp");
                bool hasNew = File.Exists(files.Diff) || File.Exists(files.Backup);
                bool hasLegacy = File.Exists(files.LegacyDiff) || File.Exists(files.LegacyBackup);
                if (!hasNew && hasLegacy)
                {
                    files.ExistingDiff = files.LegacyDiff;
                    files.ExistingBackup = files.LegacyBackup;
                }
            }
            return files;
        }

        private static void RemoveLegacyFiles(DeviceFiles files, EngineDcsExportResult result)
        {
            bool removed = false;
            foreach (string path in new[] { files.LegacyDiff, files.LegacyBackup })
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    continue;
                // Names that differ only in case ("{...-11F1-...}" vs DCS's "{...-11f1-...}") are the same file on
                // Windows: deleting the "old" one would delete the file just written.
                if (string.Equals(path, files.Diff, StringComparison.OrdinalIgnoreCase) || string.Equals(path, files.Backup, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    GameBackups.Protect(path, result.Warnings);
                    File.Delete(path);
                    removed = true;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add("Could not remove the old file '" + path + "': " + ex.Message);
                }
            }
            if (removed)
                result.RenamedDeviceFiles.Add(files.LegacyDiff + " -> " + files.Diff);
        }

        // File name of a DCS device: invalid characters replaced, spaces at the ends kept (DCS keeps them).
        private static string SanitizeDcsFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "item";
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(invalid.Contains(c) ? '_' : c);
            return sb.ToString();
        }

        private static List<string> ExtractReformerNames(List<string> allReformers)
        {
            if (allReformers == null || allReformers.Count == 0)
                return new List<string>();

            List<string> names = new List<string>();
            for (int i = 0; i < allReformers.Count; i++)
            {
                string raw = allReformers[i];
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                string[] parts = raw.Split('\u00A7');
                if (parts.Length < 1)
                    continue;
                string name = parts[0].Trim();
                if (name.Length == 0)
                    continue;
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
            }

            return names;
        }

        private static List<string> ExtractGroups(List<string> groups)
        {
            if (groups == null || groups.Count == 0)
                return new List<string>();

            List<string> values = new List<string>();
            for (int i = 0; i < groups.Count; i++)
            {
                string group = groups[i];
                if (string.IsNullOrWhiteSpace(group))
                    continue;
                string trimmed = group.Trim();
                if (trimmed.Length == 0)
                    continue;
                if (!values.Any(v => string.Equals(v, trimmed, StringComparison.OrdinalIgnoreCase)))
                    values.Add(trimmed);
            }
            // In the relation's order, so an import gives the groups back as they were.
            return values;
        }

        private static void ApplyAxisFilter(DiffAdded target, AxisFilter filter)
        {
            if (target == null || filter == null)
                return;
            target.HasInverted = filter.HasInverted;
            target.Inverted = filter.Inverted;
            target.HasSlider = filter.HasSlider;
            target.Slider = filter.Slider;
            target.HasDeadzone = filter.HasDeadzone;
            target.Deadzone = filter.Deadzone;
            target.Curvature = filter.Curvature == null ? new List<double>() : new List<double>(filter.Curvature);
            target.HasSaturationX = filter.HasSaturationX;
            target.SaturationX = filter.SaturationX;
            target.HasSaturationY = filter.HasSaturationY;
            target.SaturationY = filter.SaturationY;
        }

        private static bool HasAxisFilter(DiffAdded add)
        {
            if (add == null)
                return false;
            return add.HasInverted ||
                   add.HasSlider ||
                   add.HasDeadzone ||
                   (add.Curvature != null && add.Curvature.Count > 0) ||
                   add.HasSaturationX ||
                   add.HasSaturationY;
        }

        private static string BuildAddedSignature(DiffAdded add, bool includeMetadata)
        {
            if (add == null)
                return string.Empty;

            List<string> reformers = NormalizeList(add.Reformers)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> groups = NormalizeList(add.Groups)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<double> curvature = NormalizeCurvature(add.Curvature);

            StringBuilder sb = new StringBuilder();
            sb.Append((add.Key ?? string.Empty).Trim().ToLowerInvariant());
            sb.Append(SignatureSeparator);
            sb.Append(string.Join(SignatureSeparator, reformers).ToLowerInvariant());
            sb.Append(SignatureSeparator);
            sb.Append(add.HasInverted ? (add.Inverted ? "invert:1" : "invert:0") : "invert:n");
            sb.Append(SignatureSeparator);
            sb.Append(add.HasSlider ? (add.Slider ? "slider:1" : "slider:0") : "slider:n");
            sb.Append(SignatureSeparator);
            sb.Append(add.HasDeadzone ? "deadzone:" + ToInvariant(add.Deadzone) : "deadzone:n");
            sb.Append(SignatureSeparator);
            sb.Append(curvature.Count == 0 ? "curvature:n" : "curvature:" + string.Join(",", curvature.Select(ToInvariant)));
            sb.Append(SignatureSeparator);
            sb.Append(add.HasSaturationX ? "satx:" + ToInvariant(add.SaturationX) : "satx:n");
            sb.Append(SignatureSeparator);
            sb.Append(add.HasSaturationY ? "saty:" + ToInvariant(add.SaturationY) : "saty:n");

            if (includeMetadata)
            {
                sb.Append(SignatureSeparator);
                sb.Append((add.RelationName ?? string.Empty).Trim().ToLowerInvariant());
                sb.Append(SignatureSeparator);
                sb.Append(string.Join(SignatureSeparator, groups).ToLowerInvariant());
            }

            return sb.ToString();
        }

        private static void RebuildSignatures(DiffEntry entry)
        {
            if (entry == null)
                return;

            entry.Signatures.Clear();
            List<DiffAdded> deduped = new List<DiffAdded>();
            for (int i = 0; i < entry.Added.Count; i++)
            {
                DiffAdded add = entry.Added[i];
                string signature = BuildAddedSignature(add, true);
                if (entry.Signatures.Contains(signature))
                    continue;
                entry.Signatures.Add(signature);
                deduped.Add(add);
            }
            entry.Added = deduped;
        }

        private static DiffPayload ClonePayload(DiffPayload source)
        {
            DiffPayload clone = new DiffPayload();
            clone.Axis = CloneSection(source == null ? null : source.Axis);
            clone.Keys = CloneSection(source == null ? null : source.Keys);
            clone.HasFfb = source != null && source.HasFfb;
            clone.Ffb = CloneForceFeedback(source == null ? null : source.Ffb);
            return clone;
        }

        private static DiffForceFeedback CloneForceFeedback(DiffForceFeedback source)
        {
            DiffForceFeedback clone = new DiffForceFeedback();
            if (source == null)
                return clone;
            clone.InvertX = source.InvertX;
            clone.InvertY = source.InvertY;
            clone.SwapAxes = source.SwapAxes;
            clone.HasFlags = source.HasFlags;
            foreach (KeyValuePair<string, string> other in source.Other)
                clone.Other[other.Key] = other.Value;
            return clone;
        }

        private static Dictionary<string, DiffEntry> CloneSection(Dictionary<string, DiffEntry> source)
        {
            Dictionary<string, DiffEntry> clone = new Dictionary<string, DiffEntry>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
                return clone;

            foreach (KeyValuePair<string, DiffEntry> item in source)
                clone[item.Key] = CloneEntry(item.Value);
            return clone;
        }

        private static DiffEntry CloneEntry(DiffEntry source)
        {
            DiffEntry entry = new DiffEntry();
            if (source == null)
                return entry;

            entry.Name = source.Name;
            for (int i = 0; i < source.Added.Count; i++)
            {
                DiffAdded add = CloneAdded(source.Added[i]);
                entry.Added.Add(add);
                entry.Signatures.Add(BuildAddedSignature(add, true));
            }
            for (int i = 0; i < source.Removed.Count; i++)
                AddRemoved(entry, source.Removed[i]);
            return entry;
        }

        private static string RemovedSignature(string key, List<string> reformers)
        {
            return (key ?? string.Empty).Trim().ToLowerInvariant() + SignatureSeparator +
                   string.Join(SignatureSeparator, NormalizeList(reformers).Select(r => r.ToLowerInvariant()).OrderBy(r => r, StringComparer.Ordinal));
        }

        private static void AddRemoved(DiffEntry entry, DiffRemoved removed)
        {
            if (entry == null || removed == null || string.IsNullOrWhiteSpace(removed.Key))
                return;
            string signature = RemovedSignature(removed.Key, removed.Reformers);
            if (entry.Removed.Any(r => RemovedSignature(r.Key, r.Reformers) == signature))
                return;
            entry.Removed.Add(new DiffRemoved { Key = removed.Key.Trim(), Reformers = NormalizeList(removed.Reformers) });
        }

        // JoyPro1 behaviour: every device file JoyPro writes for a plane drops all of that plane's DCS default binds
        // (CleanProfile\DCS\<plane>.cf, KeyboardCleanProfile for the keyboard), so only the profile's binds remain.
        private static void AddCleanProfileRemovals(DiffPayload target, DiffPayload clean)
        {
            if (target == null || clean == null)
                return;
            AddCleanSectionRemovals(target.Axis, clean.Axis);
            AddCleanSectionRemovals(target.Keys, clean.Keys);
        }

        private static void AddCleanSectionRemovals(Dictionary<string, DiffEntry> target, Dictionary<string, DiffEntry> clean)
        {
            foreach (KeyValuePair<string, DiffEntry> cleanEntry in clean)
            {
                if (cleanEntry.Value == null || cleanEntry.Value.Removed.Count == 0)
                    continue;
                DiffEntry entry;
                if (!target.TryGetValue(cleanEntry.Key, out entry))
                {
                    entry = new DiffEntry();
                    entry.Name = cleanEntry.Value.Name;
                    target.Add(cleanEntry.Key, entry);
                }
                foreach (DiffRemoved removed in cleanEntry.Value.Removed)
                    AddRemoved(entry, removed);
            }
        }

        private static bool HasRemovals(DiffPayload clean)
        {
            return clean != null &&
                   (clean.Axis.Values.Any(e => e != null && e.Removed.Count > 0) || clean.Keys.Values.Any(e => e != null && e.Removed.Count > 0));
        }

        // True when the payload already removes every DCS default of the clean profile (or binds it itself): the file
        // need not be written again.
        private static bool ContainsAllRemovals(DiffPayload payload, DiffPayload clean)
        {
            foreach (KeyValuePair<Dictionary<string, DiffEntry>, Dictionary<string, DiffEntry>> pair in new[]
                     {
                         new KeyValuePair<Dictionary<string, DiffEntry>, Dictionary<string, DiffEntry>>(payload.Axis, clean.Axis),
                         new KeyValuePair<Dictionary<string, DiffEntry>, Dictionary<string, DiffEntry>>(payload.Keys, clean.Keys)
                     })
            {
                foreach (KeyValuePair<string, DiffEntry> cleanEntry in pair.Value)
                {
                    if (cleanEntry.Value == null || cleanEntry.Value.Removed.Count == 0)
                        continue;
                    DiffEntry entry;
                    if (!pair.Key.TryGetValue(cleanEntry.Key, out entry) || entry == null)
                        return false;
                    HashSet<string> known = new HashSet<string>(
                        entry.Removed.Select(r => RemovedSignature(r.Key, r.Reformers))
                            .Concat(entry.Added.Select(a => RemovedSignature(a.Key, a.Reformers))),
                        StringComparer.Ordinal);
                    if (cleanEntry.Value.Removed.Any(r => !known.Contains(RemovedSignature(r.Key, r.Reformers))))
                        return false;
                }
            }
            return true;
        }

        // A device type the clean profiles were not built for can have defaults of its own (an entry in DCS's
        // Scripts\Input\DefaultAssignments.lua), which the generic clean profile does not remove: say so once.
        private static void WarnAboutUncoveredDeviceTypes(Dictionary<string, Dictionary<string, DiffPayload>> generatedPayloads, EngineDcsExportResult result)
        {
            HashSet<string> covered = new HashSet<string>(EngineDcsRefresh.ReadCoveredDeviceTypes().Select(t => t.Trim()), StringComparer.Ordinal);
            HashSet<string> special = new HashSet<string>(
                EngineDcsDefaultsStrip.DefaultAssignmentDeviceTypes(EngineDcsDefaultsStrip.ResolveInstallPath(null)).Select(t => t.Trim()),
                StringComparer.Ordinal);
            List<string> uncovered = new List<string>();
            foreach (KeyValuePair<string, Dictionary<string, DiffPayload>> plane in generatedPayloads)
            {
                string joystickDir = Path.Combine(result.InputPath, plane.Key, "joystick");
                foreach (string device in plane.Value.Keys)
                {
                    if (string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string template = DcsDeviceNames.TemplateName(SanitizeDcsFileName(DcsDeviceNames.DcsName(device, joystickDir))).Trim();
                    // Covered by the last refresh, or a type DCS gives no defaults of its own (generic stick).
                    if (covered.Contains(template) || !special.Contains(template))
                        continue;
                    if (FindCleanProfileIgnoringEndSpaces(Path.Combine(GameDataPaths.CleanProfileFolder("DCS", false), plane.Key), template) != null)
                        continue;
                    if (!uncovered.Contains(template))
                        uncovered.Add(template);
                }
            }
            if (EngineDcsDefaultsStrip.LoadState().CleanProfilesStale)
                result.Warnings.Add("DCS's device default files were moved or put back after JoyPro's DCS list was built: update the DCS list (Start page, DCS card), then export again, so every DCS default bind is removed.");
            if (uncovered.Count > 0)
                result.Warnings.Add("DCS has default binds of its own for " + string.Join(", ", uncovered) +
                                    ", which JoyPro's DCS list was not built for: some of these defaults can stay. Update the DCS list (Start page, DCS card) once, then export again.");
        }

        // A bind cannot be removed and added on the same action: the profile's bind wins.
        private static void DropRemovalsOfAddedBinds(DiffPayload payload)
        {
            foreach (Dictionary<string, DiffEntry> section in new[] { payload.Axis, payload.Keys })
            {
                foreach (DiffEntry entry in section.Values)
                {
                    if (entry == null || entry.Removed.Count == 0 || entry.Added.Count == 0)
                        continue;
                    HashSet<string> added = new HashSet<string>(entry.Added.Select(a => RemovedSignature(a.Key, a.Reformers)), StringComparer.Ordinal);
                    entry.Removed.RemoveAll(r => added.Contains(RemovedSignature(r.Key, r.Reformers)));
                }
            }
        }

        // The DCS defaults to remove for a device in a plane. Joysticks: DCS loads "<template>.lua" of the device type
        // when the plane has one (else default.lua), so the refresh stores those per device type as
        // CleanProfile\DCS\<plane>\<template>.cf; without one the plane's generic <plane>.cf applies.
        private static DiffPayload LoadCleanProfile(string plane, bool keyboard, string dcsDevice, Dictionary<string, DiffPayload> cache, EngineDcsExportResult result)
        {
            DiffPayload clean;
            if (!keyboard && !string.IsNullOrEmpty(dcsDevice))
            {
                string template = DcsDeviceNames.TemplateName(dcsDevice);
                string deviceKey = "jsdev|" + plane + "|" + template;
                if (cache.TryGetValue(deviceKey, out clean))
                {
                    if (clean != null)
                        return clean;
                }
                else
                {
                    string devicePath = null;
                    try
                    {
                        devicePath = Path.Combine(GameDataPaths.CleanProfileFolder("DCS", false), plane, template + ".cf");
                    }
                    catch (ArgumentException)
                    {
                    }
                    if (devicePath != null && !File.Exists(devicePath))
                        devicePath = FindCleanProfileIgnoringEndSpaces(Path.GetDirectoryName(devicePath), template) ?? devicePath;
                    clean = devicePath != null && File.Exists(devicePath) ? ParsePayloadFile(devicePath, result) : null;
                    cache[deviceKey] = clean;
                    if (clean != null)
                        return clean;
                }
            }

            string key = (keyboard ? "kb|" : "js|") + plane;
            if (cache.TryGetValue(key, out clean))
                return clean;

            string path = Path.Combine(GameDataPaths.CleanProfileFolder("DCS", keyboard), plane + ".cf");
            clean = File.Exists(path) ? ParsePayloadFile(path, result) : null;
            if (clean == null && !keyboard)
                result.Warnings.Add("No clean profile for '" + plane + "' (" + path + "): DCS default binds stay active on its devices. Refresh the DCS DB for this module.");
            cache[key] = clean;
            return clean;
        }

        // A device that is not connected and has no file yet is known by its JoyPro name, whose instance name has no
        // spaces at the ends (" VKB-Sim ... Twist " -> "VKB-Sim ... Twist"): find its type's clean profile anyway.
        private static string FindCleanProfileIgnoringEndSpaces(string folder, string template)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(template) || !Directory.Exists(folder))
                return null;
            string wanted = template.Trim();
            foreach (string file in Directory.GetFiles(folder, "*.cf"))
            {
                string name = Path.GetFileName(file);
                name = name.Substring(0, name.Length - ".cf".Length);
                if (string.Equals(name.Trim(), wanted, StringComparison.Ordinal))
                    return file;
            }
            return null;
        }

        private static DiffAdded CloneAdded(DiffAdded source)
        {
            DiffAdded add = new DiffAdded();
            if (source == null)
                return add;

            add.Key = source.Key;
            add.Reformers = source.Reformers == null ? new List<string>() : new List<string>(source.Reformers);
            add.RelationName = source.RelationName;
            add.Groups = source.Groups == null ? new List<string>() : new List<string>(source.Groups);
            add.HasInverted = source.HasInverted;
            add.Inverted = source.Inverted;
            add.HasSlider = source.HasSlider;
            add.Slider = source.Slider;
            add.HasDeadzone = source.HasDeadzone;
            add.Deadzone = source.Deadzone;
            add.Curvature = source.Curvature == null ? new List<double>() : new List<double>(source.Curvature);
            add.HasSaturationX = source.HasSaturationX;
            add.SaturationX = source.SaturationX;
            add.HasSaturationY = source.HasSaturationY;
            add.SaturationY = source.SaturationY;
            return add;
        }

        private static List<double> NormalizeCurvature(List<double> values)
        {
            List<double> normalized = new List<double>();
            if (values == null)
                return normalized;

            for (int i = 0; i < values.Count; i++)
            {
                double value = values[i];
                if (double.IsNaN(value) || double.IsInfinity(value))
                    continue;
                normalized.Add(value);
            }

            return normalized;
        }

        private static bool CurvatureEquals(List<double> left, List<double> right)
        {
            List<double> a = NormalizeCurvature(left);
            List<double> b = NormalizeCurvature(right);
            if (a.Count == 0 && b.Count == 0)
                return true;
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!NearlyEqual(a[i], b[i]))
                    return false;
            }
            return true;
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
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
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
                double parsed;
                if (!double.TryParse(
                    matches[i].Groups["value"].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out parsed))
                {
                    continue;
                }
                values.Add(parsed);
            }
            return values;
        }

        private static void PopulateAxisFilter(DiffAdded entry, string entryBlock)
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

            double deadzone;
            if (TryExtractNamedDouble(filterBlock, "deadzone", out deadzone))
            {
                entry.HasDeadzone = true;
                entry.Deadzone = deadzone;
            }

            List<double> curvature = ExtractNamedDoubleArray(filterBlock, "curvature");
            if (curvature.Count > 0)
                entry.Curvature = curvature;

            double satX;
            if (TryExtractNamedDouble(filterBlock, "saturationX", out satX))
            {
                entry.HasSaturationX = true;
                entry.SaturationX = satX;
            }

            double satY;
            if (TryExtractNamedDouble(filterBlock, "saturationY", out satY))
            {
                entry.HasSaturationY = true;
                entry.SaturationY = satY;
            }
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

        private static string NormalizeOptionalString(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return value.Trim();
        }

        private static string StripDeviceGuidSuffix(string device)
        {
            string value = (device ?? string.Empty).Trim();
            int split = value.LastIndexOf(" {", StringComparison.Ordinal);
            if (split > 0 && value.EndsWith("}", StringComparison.Ordinal))
                return value.Substring(0, split).Trim();
            return value;
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

        private static bool TryParseExportMode(string raw, out EngineDcsExportMode mode)
        {
            mode = EngineDcsExportMode.MergeOverwrite;
            string value = string.IsNullOrWhiteSpace(raw) ? "merge-overwrite" : raw.Trim().ToLowerInvariant();
            switch (value)
            {
                case "clean":
                    mode = EngineDcsExportMode.Clean;
                    return true;
                case "add":
                    mode = EngineDcsExportMode.Add;
                    return true;
                case "merge":
                    mode = EngineDcsExportMode.Merge;
                    return true;
                case "merge-overwrite":
                case "mergeoverwrite":
                case "overwrite":
                    mode = EngineDcsExportMode.MergeOverwrite;
                    return true;
                default:
                    return false;
            }
        }

        private static string ExportModeToString(EngineDcsExportMode mode)
        {
            switch (mode)
            {
                case EngineDcsExportMode.Clean:
                    return "clean";
                case EngineDcsExportMode.Add:
                    return "add";
                case EngineDcsExportMode.Merge:
                    return "merge";
                case EngineDcsExportMode.MergeOverwrite:
                    return "merge-overwrite";
                default:
                    return "merge-overwrite";
            }
        }

        private static string ResolveOutputInputPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string full = Path.GetFullPath(path);
            string normalized = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (normalized.EndsWith(Path.Combine("Config", "Input"), StringComparison.OrdinalIgnoreCase))
                return normalized;
            if (normalized.EndsWith("Input", StringComparison.OrdinalIgnoreCase))
                return normalized;
            return Path.Combine(full, "Config", "Input");
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;
            return Path.GetFullPath(path);
        }

        private static string NormalizeGame(string game)
        {
            return string.IsNullOrWhiteSpace(game) ? "DCS" : game.Trim();
        }

        private static string SanitizePathSegment(string value)
        {
            return SanitizeFileName(string.IsNullOrWhiteSpace(value) ? "Default" : value);
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "item";

            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (invalid.Contains(c))
                    sb.Append('_');
                else
                    sb.Append(c);
            }
            string result = sb.ToString().Trim();
            return result.Length == 0 ? "item" : result;
        }

        private static string EscapeLua(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
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

        private static bool NearlyEqual(double left, double right)
        {
            return Math.Abs(left - right) < 0.0000001;
        }

        private static string ToInvariant(double value)
        {
            return value.ToString("0.###############", CultureInfo.InvariantCulture);
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

        private sealed class DiffPayload
        {
            public Dictionary<string, DiffEntry> Axis { get; set; }
            public Dictionary<string, DiffEntry> Keys { get; set; }
            public bool HasFfb { get; set; }
            public DiffForceFeedback Ffb { get; set; }

            public DiffPayload()
            {
                Axis = new Dictionary<string, DiffEntry>(StringComparer.OrdinalIgnoreCase);
                Keys = new Dictionary<string, DiffEntry>(StringComparer.OrdinalIgnoreCase);
                HasFfb = false;
                Ffb = new DiffForceFeedback();
            }
        }

        private static readonly HashSet<string> FfbFlagKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "invertX", "invertY", "swapAxes", "swapAxis" };

        private sealed class DiffForceFeedback
        {
            public bool InvertX { get; set; }
            public bool InvertY { get; set; }
            public bool SwapAxes { get; set; }
            // invertX/invertY/swapAxes are written (the profile has them, or the file had them).
            public bool HasFlags { get; set; }
            // The device's other ffDiffs settings (trimmer, shake, ignore, ...): raw Lua values, never changed by JoyPro.
            public Dictionary<string, string> Other { get; set; }

            public DiffForceFeedback()
            {
                InvertX = false;
                InvertY = false;
                SwapAxes = false;
                Other = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        private sealed class DiffEntry
        {
            public string Name { get; set; }
            public List<DiffAdded> Added { get; set; }
            public HashSet<string> Signatures { get; set; }
            // Default binds this device drops for the action (DCS "removed" list).
            public List<DiffRemoved> Removed { get; set; }

            public DiffEntry()
            {
                Name = string.Empty;
                Added = new List<DiffAdded>();
                Signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Removed = new List<DiffRemoved>();
            }
        }

        private sealed class DiffRemoved
        {
            public string Key { get; set; }
            public List<string> Reformers { get; set; }

            public DiffRemoved()
            {
                Key = string.Empty;
                Reformers = new List<string>();
            }
        }

        private sealed class DiffAdded
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

            public DiffAdded()
            {
                Key = string.Empty;
                Reformers = new List<string>();
                RelationName = string.Empty;
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

        private sealed class AxisFilter
        {
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

            public AxisFilter()
            {
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

        private sealed class ModifierPayload
        {
            public string Name { get; set; }
            public string Device { get; set; }
            public string Key { get; set; }
            public bool Switch { get; set; }

            public ModifierPayload()
            {
                Name = string.Empty;
                Device = string.Empty;
                Key = string.Empty;
                Switch = false;
            }
        }

        private enum EngineDcsExportMode
        {
            Clean,
            Add,
            Merge,
            MergeOverwrite
        }
    }

    public sealed class EngineDcsExportOptions
    {
        public string Mode { get; set; }
        public bool KeepKeyboardDefaults { get; set; }
        public bool NukeUntouchedDevices { get; set; }
        // Clean mode: the written joystick devices also lose their binds and DCS defaults in every other aircraft
        // (only when no aircraft filter is given).
        public bool CleanAllAircraft { get; set; }
        // Only these aircraft (and layers) are written; empty = every aircraft of the profile.
        public List<string> Planes { get; set; }
        public Action<string> StatusSink { get; set; }

        public EngineDcsExportOptions()
        {
            Mode = "merge-overwrite";
            KeepKeyboardDefaults = true;
            NukeUntouchedDevices = false;
            CleanAllAircraft = true;
            Planes = new List<string>();
            StatusSink = null;
        }
    }

    public sealed class EngineDcsExportResult
    {
        public string FormatVersion { get; set; }
        public string GeneratedUtc { get; set; }
        public string SavedGamesPath { get; set; }
        public string InputPath { get; set; }
        public string Mode { get; set; }
        public bool KeepKeyboardDefaults { get; set; }
        public bool NukeUntouchedDevices { get; set; }
        public int RelationsScanned { get; set; }
        public int EntriesWritten { get; set; }
        public int FilesWritten { get; set; }
        public int BackupFilesWritten { get; set; }
        public int ExistingFilesParsed { get; set; }
        public int ExistingDiffFilesParsed { get; set; }
        public int ExistingBackupFilesParsed { get; set; }
        public int MergeConflictsKept { get; set; }
        public int MergeConflictsOverwritten { get; set; }
        public int MergeConflictsAdded { get; set; }
        public int DevicesCleared { get; set; }
        public int OtherAircraftFilesCleaned { get; set; }
        // Files an older JoyPro wrote under the trimmed device name, now written as DCS names the device.
        public List<string> RenamedDeviceFiles { get; set; }
        public int PlaneCount { get; set; }
        public int DeviceCount { get; set; }
        public int SkippedRelations { get; set; }
        public int SkippedKeyboardEntries { get; set; }
        public List<string> WrittenFiles { get; set; }
        public List<string> ClearedFiles { get; set; }
        public List<string> Errors { get; set; }
        public List<string> Warnings { get; set; }

        public EngineDcsExportResult()
        {
            FormatVersion = "1";
            GeneratedUtc = DateTime.UtcNow.ToString("o");
            SavedGamesPath = string.Empty;
            InputPath = string.Empty;
            Mode = string.Empty;
            KeepKeyboardDefaults = true;
            NukeUntouchedDevices = false;
            RelationsScanned = 0;
            EntriesWritten = 0;
            FilesWritten = 0;
            BackupFilesWritten = 0;
            ExistingFilesParsed = 0;
            ExistingDiffFilesParsed = 0;
            ExistingBackupFilesParsed = 0;
            MergeConflictsKept = 0;
            MergeConflictsOverwritten = 0;
            MergeConflictsAdded = 0;
            DevicesCleared = 0;
            OtherAircraftFilesCleaned = 0;
            RenamedDeviceFiles = new List<string>();
            PlaneCount = 0;
            DeviceCount = 0;
            SkippedRelations = 0;
            SkippedKeyboardEntries = 0;
            WrittenFiles = new List<string>();
            ClearedFiles = new List<string>();
            Errors = new List<string>();
            Warnings = new List<string>();
        }
    }
}
