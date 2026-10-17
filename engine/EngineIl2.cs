using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    internal static class EngineIl2
    {
        private const string Il2Game = "IL2Game";
        // Written like JoyPro1 and IL-2 itself: UTF-8 without a byte order mark.
        private static readonly Encoding NoBom = new UTF8Encoding(false);
        private const string SignatureSeparator = "\u00A7";
        private const string ResponsesCursor = "type1Devices=actionId%2CcenterDeadZone%2CsensitivityBallance%2CsideDeadZone";
        private const string DevicesHeader = "configId,guid,model|";
        private static void ReportStatus(Action<string> statusSink, string message)
        {
            if (statusSink == null || string.IsNullOrWhiteSpace(message))
                return;
            statusSink(message);
        }

        private static void ReportStatus(EngineIl2ImportOptions options, string message)
        {
            if (options == null)
                return;
            ReportStatus(options.StatusSink, message);
        }

        private static readonly string ActionsHeader =
            "// Input map preset.\r\n" +
            "// It is not recommended to change anything here,\r\n" +
            "// but it is still possible on your own risk.\r\n" +
            "\r\n" +
            "&actions = action, command, invert|\r\n";

        private static readonly HashSet<string> DefaultModifierNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "LeftControl",
                "RightControl",
                "LeftAlt",
                "RightAlt",
                "LeftWindowsKey",
                "RightWindowsKey",
                "LeftShift",
                "RightShift"
            };

        public static EngineIl2ImportResult Scan(string rootPath, EngineIl2ImportOptions options = null)
        {
            return ExecuteImport(rootPath, false, false, options);
        }

        public static EngineIl2ImportResult Import(string rootPath, bool merge, EngineIl2ImportOptions options = null)
        {
            return ExecuteImport(rootPath, true, merge, options);
        }

        public static EngineIl2ExportResult Export(string rootPath, string modeRaw, Action<string> statusSink = null, bool nukeUntouchedDevices = false)
        {
            EngineIl2ExportResult result = new EngineIl2ExportResult();
            result.RootPath = Normalize(rootPath);
            ReportStatus(statusSink, "Starting IL2 export.");

            if (!TryParseExportMode(modeRaw, out EngineIl2ExportMode mode))
            {
                result.Errors.Add("Invalid IL2 export mode: " + modeRaw + ". Use clean, add, merge or merge-overwrite.");
                return result;
            }

            result.Mode = ExportModeToString(mode);
            result.InputPath = ResolveInputPath(rootPath);
            if (string.IsNullOrWhiteSpace(result.InputPath))
            {
                result.Errors.Add("Invalid IL2 path: " + result.RootPath);
                return result;
            }

            try
            {
                Directory.CreateDirectory(result.InputPath);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Could not create IL2 input path: " + ex.Message);
                return result;
            }

            result.GlobalActionsFile = Path.Combine(result.InputPath, "global.actions");
            result.JpActionsFile = Path.Combine(result.InputPath, "jp.actions");
            result.DevicesFile = Path.Combine(result.InputPath, "devices.txt");
            result.ResponsesFile = Path.Combine(result.InputPath, "current.responses");

            ReportStatus(statusSink, "Reading existing IL2 input files.");
            LoadKeyboardConversions(
                out Dictionary<string, string> il2ToDx,
                out Dictionary<string, string> dxToIl,
                result.Warnings);
            _ = il2ToDx;

            Dictionary<int, string> existingDeviceMap = ParseDevicesFile(result.DevicesFile, null);
            Dictionary<string, AxisResponseSetting> existingResponses = ParseResponsesFile(result.ResponsesFile, null);
            List<ActionLine> existingGlobal = ParseActionsFile(result.GlobalActionsFile, false, null);
            List<ActionLine> existingJp = ParseActionsFile(result.JpActionsFile, true, null);

            ReportStatus(statusSink, "Building IL2 export templates from profile.");
            List<ExportTemplate> templates = BuildExportTemplates(result);
            if (templates.Count == 0)
            {
                // Writing here used to replace the user's IL-2 bindings with empty files, even in merge mode.
                // An export without IL-2 binds has nothing to add, and a clean export would only erase.
                result.ActionLinesGenerated = 0;
                result.ActionLinesWritten = 0;
                result.DeviceCount = 0;
                result.AxisResponsesWritten = 0;
                result.Warnings.Add("The profile has no IL-2 binds. Existing IL-2 input files were left unchanged.");
                WriteGameSettings(result);
                ReportStatus(statusSink, "No IL2 templates found. No binds written.");
                return result;
            }

            Dictionary<string, int> deviceIndex = BuildDeviceIndexMap(templates, existingDeviceMap, mode);
            Dictionary<string, AxisResponseSetting> generatedResponses = new Dictionary<string, AxisResponseSetting>(StringComparer.OrdinalIgnoreCase);
            List<ActionLine> generatedGlobal = new List<ActionLine>();
            List<ActionLine> generatedJp = new List<ActionLine>();

            ReportStatus(statusSink, "Rendering " + templates.Count.ToString(CultureInfo.InvariantCulture) + " IL2 template(s).");
            for (int i = 0; i < templates.Count; i++)
            {
                ExportTemplate template = templates[i];
                if (!TryRenderTemplate(template, deviceIndex, dxToIl, generatedResponses, out ActionLine globalLine, out ActionLine jpLine, result))
                {
                    result.SkippedEntries++;
                    continue;
                }

                generatedGlobal.Add(globalLine);
                generatedJp.Add(jpLine);

                int done = i + 1;
                if (done == templates.Count || done % 100 == 0)
                {
                    ReportStatus(
                        statusSink,
                        "Rendered " +
                        done.ToString(CultureInfo.InvariantCulture) +
                        "/" +
                        templates.Count.ToString(CultureInfo.InvariantCulture) +
                        " IL2 template(s).");
                }
            }

            result.ActionLinesGenerated = generatedGlobal.Count;

            // Devices are told apart by their joy<N> index; the keyboard is its own device. clean releases the other
            // binds of the devices the export writes, nuke the binds of the joysticks it does not write.
            HashSet<string> writtenDevices = new HashSet<string>(generatedGlobal.Select(DeviceOf), StringComparer.OrdinalIgnoreCase);
            List<ActionLine> finalGlobal = MergeActionLines(mode, existingGlobal, generatedGlobal, writtenDevices, nukeUntouchedDevices);
            List<ActionLine> finalJp = MergeActionLines(mode, existingJp, generatedJp, writtenDevices, nukeUntouchedDevices);
            Dictionary<string, AxisResponseSetting> finalResponses = MergeAxisSettings(mode, existingResponses, generatedResponses);

            WriteActionsFile(result.GlobalActionsFile, finalGlobal.Select(l => l.RawLine).ToList(), result);
            WriteActionsFile(result.JpActionsFile, finalJp.Select(l => l.RawLine).ToList(), result);
            WriteDevicesFile(result.DevicesFile, deviceIndex, mode, result);
            WriteResponsesFile(result.ResponsesFile, finalResponses, result);
            WriteGameSettings(result);

            result.ActionLinesWritten = finalGlobal.Count;
            result.DeviceCount = deviceIndex.Count;
            result.AxisResponsesWritten = finalResponses.Count;
            ReportStatus(
                statusSink,
                "IL2 export complete. Action lines written: " +
                result.ActionLinesWritten.ToString(CultureInfo.InvariantCulture) +
                ", files: " +
                result.FilesWritten.ToString(CultureInfo.InvariantCulture) + ".");
            return result;
        }

        // Game-wide force feedback and noise filter settings (engine ffb set --device * --game IL2Game): independent of
        // the binds, so they are written by an export without IL-2 binds too.
        private static void WriteGameSettings(EngineIl2ExportResult result)
        {
            // Game-wide force feedback settings (engine ffb set --device * --game IL2Game) in data\startup.cfg.
            string startupPath = Path.Combine(Path.GetDirectoryName(result.InputPath.TrimEnd('\\', '/')) ?? string.Empty, "startup.cfg");
            Dictionary<string, double> ffb = JoyPro.Engine.Axis.FfbSettings.Resolve(InternalDataManagement.FfbOverrides, JoyPro.Engine.Axis.FfbSettings.AnyDevice, "IL2Game", null);
            if (ffb.Count > 0 && File.Exists(startupPath))
            {
                string startup = File.ReadAllText(startupPath);
                List<string> ffbChanges = new List<string>();
                if (JoyPro.Engine.Axis.Il2StartupForceFeedback.Apply(ref startup, ffb, ffbChanges))
                    WriteIfChanged(startupPath, startup, result);
            }
            // Settings > Input devices keeps force feedback and the joystick noise filter in data\input\current.devices,
            // which the game writes on the page's ACCEPT. JoyPro edits it in place and does not create it: the game's
            // mouse values in the same line have no known defaults.
            string devicesPath = Path.Combine(result.InputPath, "current.devices");
            if (ffb.Count > 0)
            {
                if (!File.Exists(devicesPath))
                    result.Warnings.Add("Force feedback / noise filter not written to " + devicesPath + ": it does not exist yet (open Settings > Input devices once and press ACCEPT).");
                else
                {
                    string devices = File.ReadAllText(devicesPath);
                    List<string> deviceChanges = new List<string>();
                    if (JoyPro.Engine.Axis.Il2CurrentDevices.Apply(ref devices, ffb, deviceChanges))
                        WriteIfChanged(devicesPath, devices, result);
                }
            }
        }

        public static string ToText(EngineIl2ImportResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("IL2 Import");
            sb.AppendLine("Root Path: " + Safe(result.RootPath));
            sb.AppendLine("Input Path: " + Safe(result.InputPath));
            sb.AppendLine("Merge: " + result.Merge);
            sb.AppendLine("Selected Devices: " + (result.SelectedDevices == null || result.SelectedDevices.Count == 0 ? "<all>" : string.Join(", ", result.SelectedDevices)));
            sb.AppendLine("Ignored Devices: " + (result.IgnoredDevices == null || result.IgnoredDevices.Count == 0 ? "<none>" : string.Join(", ", result.IgnoredDevices)));
            sb.AppendLine("Import Inverted: " + result.ImportInverted);
            sb.AppendLine("Import Curvature: " + result.ImportCurvature);
            sb.AppendLine("Import Deadzone: " + result.ImportDeadzone);
            sb.AppendLine("Global Actions: " + Safe(result.GlobalActionsFile));
            sb.AppendLine("JP Actions: " + Safe(result.JpActionsFile));
            sb.AppendLine("Devices: " + Safe(result.DevicesFile));
            sb.AppendLine("Responses: " + Safe(result.ResponsesFile));
            sb.AppendLine("Action Lines Parsed: " + result.ActionLinesParsed);
            sb.AppendLine("Keyboard Entries Parsed: " + result.KeyboardEntriesParsed);
            sb.AppendLine("Joystick Entries Parsed: " + result.JoystickEntriesParsed);
            sb.AppendLine("Import Candidates: " + result.CandidatesParsed);
            sb.AppendLine("Filtered By Device: " + result.CandidatesFilteredByDevice);
            sb.AppendLine("Imported Relations: " + result.ImportedRelations);
            sb.AppendLine("Imported Binds: " + result.ImportedBinds);
            sb.AppendLine("Merged Into Existing: " + result.MergedIntoExisting);
            sb.AppendLine("Skipped Entries: " + result.SkippedEntries);

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

        public static string ToText(EngineIl2ExportResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("IL2 Export");
            sb.AppendLine("Root Path: " + Safe(result.RootPath));
            sb.AppendLine("Input Path: " + Safe(result.InputPath));
            sb.AppendLine("Mode: " + Safe(result.Mode));
            sb.AppendLine("Global Actions: " + Safe(result.GlobalActionsFile));
            sb.AppendLine("JP Actions: " + Safe(result.JpActionsFile));
            sb.AppendLine("Devices: " + Safe(result.DevicesFile));
            sb.AppendLine("Responses: " + Safe(result.ResponsesFile));
            sb.AppendLine("Relations Scanned: " + result.RelationsScanned);
            sb.AppendLine("Nodes Scanned: " + result.NodesScanned);
            sb.AppendLine("Action Lines Generated: " + result.ActionLinesGenerated);
            sb.AppendLine("Action Lines Written: " + result.ActionLinesWritten);
            sb.AppendLine("Devices Written: " + result.DeviceCount);
            sb.AppendLine("Axis Responses Written: " + result.AxisResponsesWritten);
            sb.AppendLine("Files Written: " + result.FilesWritten);
            sb.AppendLine("Skipped Entries: " + result.SkippedEntries);

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

        private static EngineIl2ImportResult ExecuteImport(string rootPath, bool applyImport, bool merge, EngineIl2ImportOptions options)
        {
            EngineIl2ImportOptions effectiveOptions = NormalizeImportOptions(options);
            EngineIl2ImportResult result = new EngineIl2ImportResult();
            ReportStatus(effectiveOptions, applyImport ? "Starting IL2 import." : "Starting IL2 scan.");
            result.RootPath = Normalize(rootPath);
            result.Merge = merge;
            result.SelectedDevices = new List<string>(effectiveOptions.DeviceFilter);
            result.IgnoredDevices = new List<string>(effectiveOptions.IgnoreDeviceFilter);
            result.ImportInverted = effectiveOptions.ImportInverted;
            result.ImportCurvature = effectiveOptions.ImportCurvature;
            result.ImportDeadzone = effectiveOptions.ImportDeadzone;
            result.InputPath = ResolveInputPath(rootPath);

            if (string.IsNullOrWhiteSpace(result.InputPath))
            {
                result.Errors.Add("Invalid IL2 path: " + result.RootPath);
                return result;
            }

            if (!Directory.Exists(result.InputPath))
            {
                result.Errors.Add("IL2 input path does not exist: " + result.InputPath);
                return result;
            }

            result.GlobalActionsFile = Path.Combine(result.InputPath, "global.actions");
            result.JpActionsFile = Path.Combine(result.InputPath, "jp.actions");
            result.DevicesFile = Path.Combine(result.InputPath, "devices.txt");
            result.ResponsesFile = Path.Combine(result.InputPath, "current.responses");

            if (!File.Exists(result.GlobalActionsFile))
            {
                result.Errors.Add("Missing global.actions: " + result.GlobalActionsFile);
                return result;
            }

            LoadKeyboardConversions(
                out Dictionary<string, string> il2ToDx,
                out Dictionary<string, string> dxToIl,
                result.Warnings);
            _ = dxToIl;

            ReportStatus(effectiveOptions, "Parsing IL2 input files.");
            Dictionary<int, string> devices = ParseDevicesFile(result.DevicesFile, result.Warnings);
            Dictionary<string, AxisResponseSetting> responses = ParseResponsesFile(result.ResponsesFile, result.Warnings);
            List<ActionLine> globalLines = ParseActionsFile(result.GlobalActionsFile, true, result.Warnings);
            List<ActionLine> jpLines = ParseActionsFile(result.JpActionsFile, true, result.Warnings);

            AttachBackupMetadata(globalLines, jpLines);

            List<ImportCandidate> candidates = BuildImportCandidates(globalLines, devices, responses, il2ToDx, effectiveOptions, result);
            ReportStatus(
                effectiveOptions,
                "Built " +
                candidates.Count.ToString(CultureInfo.InvariantCulture) +
                " IL2 import candidate(s).");
            if (!applyImport)
                return result;

            ReportStatus(effectiveOptions, "Applying IL2 import candidates.");
            ApplyImport(candidates, merge, effectiveOptions, result);
            ReportStatus(
                effectiveOptions,
                "IL2 import complete. Relations: " +
                result.ImportedRelations.ToString(CultureInfo.InvariantCulture) +
                ", binds: " +
                result.ImportedBinds.ToString(CultureInfo.InvariantCulture) + ".");
            return result;
        }

        private static void ApplyImport(List<ImportCandidate> candidates, bool merge, EngineIl2ImportOptions options, EngineIl2ImportResult result)
        {
            Dictionary<string, Relation> relations = merge
                ? new Dictionary<string, Relation>(InternalDataManagement.AllRelations, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Relation>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Bind> binds = merge
                ? new Dictionary<string, Bind>(InternalDataManagement.AllBinds, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> aliases = merge
                ? new Dictionary<string, string>(InternalDataManagement.JoystickAliases, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < candidates.Count; i++)
            {
                ImportCandidate candidate = candidates[i];
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.RelationName))
                {
                    result.SkippedEntries++;
                    continue;
                }

                string relationName = candidate.RelationName;
                if (TryGetCompatibleExistingRelation(relationName, candidate, relations, binds, out Relation existingRelation, out Bind existingBind))
                {
                    MergeCandidateIntoExisting(candidate, existingRelation, existingBind, aliases);
                    result.MergedIntoExisting++;
                    continue;
                }

                if (relations.ContainsKey(relationName))
                    relationName = BuildUniqueRelationName(relationName, relations);

                Relation relation = new Relation();
                relation.NAME = relationName;
                relation.ISAXIS = candidate.IsAxis;
                relation.Groups = candidate.Groups == null || candidate.Groups.Count == 0
                    ? new List<string> { "IL2" }
                    : candidate.Groups.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                relation.NODES = new List<RelationItem>();
                relation.AddNode(candidate.NodeId, Il2Game, candidate.IsAxis, Il2Game, false);

                Bind bind = new Bind(relation);
                bind.Joystick = candidate.Joystick;
                bind.JoyBackup = candidate.Joystick;
                if (candidate.IsAxis)
                {
                    bind.JAxis = candidate.Input;
                    bind.JButton = string.Empty;
                }
                else
                {
                    bind.JButton = candidate.Input;
                    bind.JAxis = string.Empty;
                }

                bind.AdditionalImportInfo = candidate.Description;
                bind.AllReformers = candidate.Reformers == null ? new List<string>() : new List<string>(candidate.Reformers);
                bind.Curvature = new List<double> { candidate.Sensitivity };
                bind.Slider = candidate.Slider;
                bind.Deadzone = candidate.Deadzone;
                bind.Inverted = candidate.IsAxis ? (bool?)candidate.Inverted : Bind.Inverted_Default;
                bind.AliasJoystick = candidate.Alias ?? string.Empty;
                ApplyStoredTuning(bind, candidate);
                ApplyImportedResponse(bind, candidate);
                relation.bind = bind;

                relations[relationName] = relation;
                binds[relationName] = bind;
                result.ImportedRelations++;
                result.ImportedBinds++;

                if (!string.IsNullOrWhiteSpace(candidate.Alias))
                    aliases[candidate.Joystick] = candidate.Alias;

                int done = i + 1;
                if (done == candidates.Count || done % 200 == 0)
                {
                    ReportStatus(
                        options,
                        "Applied " +
                        done.ToString(CultureInfo.InvariantCulture) +
                        "/" +
                        candidates.Count.ToString(CultureInfo.InvariantCulture) +
                        " IL2 import candidate(s).");
                }
            }

            InternalDataManagement.AllRelations = relations;
            InternalDataManagement.AllBinds = binds;
            InternalDataManagement.JoystickAliases = aliases;
            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
        }

        private static bool TryGetCompatibleExistingRelation(
            string relationName,
            ImportCandidate candidate,
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
            if (relation.ISAXIS != candidate.IsAxis)
                return false;
            if (!string.Equals(bind.Joystick ?? string.Empty, candidate.Joystick ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                return false;

            string input = candidate.IsAxis ? bind.JAxis : bind.JButton;
            if (!string.Equals(input ?? string.Empty, candidate.Input ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                return false;

            return SameReformerSet(bind.AllReformers, candidate.Reformers);
        }

        private static void MergeCandidateIntoExisting(ImportCandidate candidate, Relation relation, Bind bind, Dictionary<string, string> aliases)
        {
            relation.AddNode(candidate.NodeId, Il2Game, candidate.IsAxis, Il2Game, false);

            if (candidate.Groups != null)
            {
                for (int i = 0; i < candidate.Groups.Count; i++)
                    AddUniqueGroup(relation.Groups, candidate.Groups[i]);
            }

            if (bind.AllReformers == null)
                bind.AllReformers = new List<string>();
            if (candidate.Reformers != null)
            {
                for (int i = 0; i < candidate.Reformers.Count; i++)
                    AddUniqueString(bind.AllReformers, candidate.Reformers[i]);
            }

            if (string.IsNullOrWhiteSpace(bind.AdditionalImportInfo) && !string.IsNullOrWhiteSpace(candidate.Description))
                bind.AdditionalImportInfo = candidate.Description;

            if (!string.IsNullOrWhiteSpace(candidate.Alias))
            {
                bind.AliasJoystick = candidate.Alias;
                aliases[candidate.Joystick] = candidate.Alias;
            }

            if (candidate.IsAxis)
            {
                bind.Curvature = new List<double> { candidate.Sensitivity };
                bind.Slider = candidate.Slider;
                bind.Deadzone = candidate.Deadzone;
                bind.Inverted = candidate.Inverted;
                ApplyStoredTuning(bind, candidate);
                ApplyImportedResponse(bind, candidate);
            }
        }

        // An axis JoyPro exported carries its exact tuning in the //JP: comment (base and every scope): it replaces the
        // approximation from the IL-2 files whatever the import options say. A response changed in IL-2 since then
        // still wins: ApplyImportedResponse runs afterwards and keeps it as the IL-2 tuning when it differs.
        private static void ApplyStoredTuning(Bind bind, ImportCandidate candidate)
        {
            if (candidate.IsAxis && candidate.Meta != null && candidate.Meta.HasTuning)
                candidate.Meta.ApplyTuning(bind);
        }

        // The base keeps what it can (curvature = 2 x balance, dead zone = 4 x the centre, or the edges for a slider).
        // A response the base cannot reproduce on export (both dead zones, or values beyond the base's range) is kept
        // as the bind's IL-2 tuning; one it can reproduce drops an older IL-2 tuning, so the values from the game win.
        private static void ApplyImportedResponse(Bind bind, ImportCandidate candidate)
        {
            if (!candidate.IsAxis || candidate.Response == null)
                return;
            string scope = JoyPro.Engine.Axis.BindTuning.GameScope(Il2Game);
            JoyPro.Engine.Axis.BindTuning.Clear(bind, scope);
            JoyPro.Engine.Axis.AxisTuning resolved = JoyPro.Engine.Axis.BindTuning.Resolve(bind, Il2Game, null).Tuning;
            AxisResponseSetting r = candidate.Response;
            Func<string, double, bool> holds = (key, value) => Math.Abs(Math.Round(JoyPro.Engine.Axis.AxisModels.Il2Value(resolved, key), 2) - value) < 1e-9;
            bool same = r.OneSided
                ? resolved.Slider && holds("highValuesDeadZone", r.HighDeadZone) && holds("lowValuesDeadZone", r.LowDeadZone)
                : !resolved.Slider && holds("centerDeadZone", r.CenterDeadZone) && holds("sensitivityBallance", r.Sensitivity) && holds("sideDeadZone", r.SideDeadZone)
                    && holds("pitchShift", r.PitchShift);
            if (same)
                return;
            JoyPro.Engine.Axis.AxisTuning tuning = new JoyPro.Engine.Axis.AxisTuning { Inverted = candidate.Inverted, Slider = r.OneSided };
            if (r.OneSided)
            {
                tuning.Native["highValuesDeadZone"] = r.HighDeadZone;
                tuning.Native["lowValuesDeadZone"] = r.LowDeadZone;
            }
            else
            {
                tuning.Native["centerDeadZone"] = r.CenterDeadZone;
                tuning.Native["sensitivityBallance"] = r.Sensitivity;
                tuning.Native["sideDeadZone"] = r.SideDeadZone;
                if (r.PitchShift != 0.0 || r.Kind == "type3")
                    tuning.Native["pitchShift"] = r.PitchShift;
            }
            JoyPro.Engine.Axis.BindTuning.Set(bind, scope, tuning);
        }

        private static EngineIl2ImportOptions NormalizeImportOptions(EngineIl2ImportOptions options)
        {
            EngineIl2ImportOptions normalized = options ?? new EngineIl2ImportOptions();
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

        private static List<ImportCandidate> BuildImportCandidates(
            List<ActionLine> lines,
            Dictionary<int, string> devices,
            Dictionary<string, AxisResponseSetting> responses,
            Dictionary<string, string> il2ToDx,
            EngineIl2ImportOptions options,
            EngineIl2ImportResult result)
        {
            List<ImportCandidate> candidates = new List<ImportCandidate>();
            HashSet<string> deviceFilter = new HashSet<string>(
                options == null ? new List<string>() : options.DeviceFilter ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> deviceFilterByName = BuildGuidlessDeviceFilterSet(deviceFilter);
            HashSet<string> ignoredDeviceFilter = new HashSet<string>(
                options == null ? new List<string>() : options.IgnoreDeviceFilter ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> ignoredDeviceFilterByName = BuildGuidlessDeviceFilterSet(ignoredDeviceFilter);

            for (int i = 0; i < lines.Count; i++)
            {
                ActionLine line = lines[i];
                if (line == null || line.Sides == null || line.Sides.Count == 0)
                    continue;

                result.ActionLinesParsed++;
                for (int s = 0; s < line.Sides.Count; s++)
                {
                    ActionSide side = line.Sides[s];
                    if (side == null || string.IsNullOrWhiteSpace(side.MainToken))
                    {
                        result.SkippedEntries++;
                        continue;
                    }

                    if (side.MainToken.StartsWith("mouse_", StringComparison.OrdinalIgnoreCase))
                    {
                        result.SkippedEntries++;
                        continue;
                    }

                    if (!TryConvertIl2TokenToDcs(side.MainToken, devices, il2ToDx, out string joystick, out string input, out bool isAxis, out string error))
                    {
                        result.Warnings.Add("Skipping unsupported IL2 input '" + side.MainToken + "' for id '" + line.Id + "'" +
                            (string.IsNullOrWhiteSpace(error) ? string.Empty : " (" + error + ")"));
                        result.SkippedEntries++;
                        continue;
                    }

                    if (deviceFilter.Count > 0 && !MatchesDeviceFilter(joystick, deviceFilter, deviceFilterByName))
                    {
                        result.CandidatesFilteredByDevice++;
                        continue;
                    }
                    if (ignoredDeviceFilter.Count > 0 && MatchesDeviceFilter(joystick, ignoredDeviceFilter, ignoredDeviceFilterByName))
                    {
                        result.CandidatesFilteredByDevice++;
                        continue;
                    }

                    List<string> reformers = ConvertModifierTokens(side.ModifierTokens, devices, il2ToDx, result.Warnings, line.Id);
                    MetadataSegment meta = side.Metadata ?? new MetadataSegment();

                    string relationName = string.IsNullOrWhiteSpace(meta.RelationName)
                        ? BuildGeneratedRelationName(line, side, joystick, input)
                        : meta.RelationName.Trim();
                    List<string> groups = meta.Groups == null || meta.Groups.Count == 0
                        ? new List<string> { "IL2" }
                        : meta.Groups.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    string alias = string.IsNullOrWhiteSpace(meta.Alias) ? string.Empty : meta.Alias.Trim();

                    AxisResponseSetting axisSettings = null;
                    responses.TryGetValue(line.Id, out axisSettings);

                    ImportCandidate candidate = new ImportCandidate();
                    candidate.RelationName = relationName;
                    candidate.NodeId = line.Id + (side.SideSuffix == '\0' ? string.Empty : side.SideSuffix.ToString());
                    candidate.IsAxis = isAxis;
                    candidate.Joystick = joystick;
                    candidate.Input = input;
                    candidate.Reformers = reformers;
                    candidate.Description = string.IsNullOrWhiteSpace(line.Description) ? line.Id : line.Description;
                    candidate.Groups = groups;
                    candidate.Alias = alias;
                    candidate.Meta = meta.Full;
                    if (meta.Full != null)
                    {
                        List<string> stored = meta.Full.ReformersMatching(joystick, isAxis, input, reformers);
                        if (stored != null)
                            candidate.Reformers = stored;
                    }
                    candidate.Inverted = isAxis && line.Inverted;
                    candidate.Slider = Bind.Slider_Default;
                    candidate.Deadzone = Bind.Deadzone_Default;
                    candidate.Sensitivity = Bind.Curvature_Default[0];

                    if (axisSettings != null)
                    {
                        candidate.Response = axisSettings;
                        candidate.Sensitivity = axisSettings.Sensitivity * 2.0;
                        // A one-sided row (type2/type4) is a slider whose low end dead zone is the base dead zone. In a
                        // type1 row only a side dead zone marks a slider (what JoyPro wrote before); a row without any
                        // dead zone (rpc_pitch,0,0,0.0) is a centred axis, else the shared bind would turn stick axes into
                        // DCS sliders.
                        if (axisSettings.OneSided)
                        {
                            candidate.Slider = true;
                            candidate.Sensitivity = Bind.Curvature_Default[0];
                            candidate.Deadzone = axisSettings.LowDeadZone;
                        }
                        else if (axisSettings.CenterDeadZone <= 0.0 && axisSettings.SideDeadZone > 0.0)
                        {
                            candidate.Slider = true;
                            candidate.Deadzone = axisSettings.SideDeadZone * 4.0;
                        }
                        else
                        {
                            candidate.Slider = false;
                            candidate.Deadzone = axisSettings.CenterDeadZone * 4.0;
                        }
                    }

                    if (isAxis)
                    {
                        if (options == null || !options.ImportCurvature || !options.ImportDeadzone)
                            candidate.Response = null;
                        if (options == null || !options.ImportCurvature)
                            candidate.Sensitivity = Bind.Curvature_Default[0];
                        if (options == null || !options.ImportDeadzone)
                        {
                            candidate.Slider = Bind.Slider_Default;
                            candidate.Deadzone = Bind.Deadzone_Default;
                        }
                        if (options == null || !options.ImportInverted)
                            candidate.Inverted = Bind.Inverted_Default;
                    }
                    else
                    {
                        candidate.Inverted = Bind.Inverted_Default;
                    }

                    candidates.Add(candidate);
                    result.CandidatesParsed++;
                    if (string.Equals(joystick, "Keyboard", StringComparison.OrdinalIgnoreCase))
                        result.KeyboardEntriesParsed++;
                    else
                        result.JoystickEntriesParsed++;
                }
            }

            return candidates;
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

        private static bool MatchesDeviceFilter(string joystick, HashSet<string> fullFilters, HashSet<string> guidlessFilters)
        {
            string value = (joystick ?? string.Empty).Trim();
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

        private static string StripDeviceGuidSuffix(string joystick)
        {
            string value = (joystick ?? string.Empty).Trim();
            int split = value.LastIndexOf(" {", StringComparison.Ordinal);
            if (split > 0 && value.EndsWith("}", StringComparison.Ordinal))
                return value.Substring(0, split).Trim();
            return value;
        }

        private static List<string> ConvertModifierTokens(
            List<string> modifierTokens,
            Dictionary<int, string> devices,
            Dictionary<string, string> il2ToDx,
            List<string> warnings,
            string actionId)
        {
            List<string> result = new List<string>();
            if (modifierTokens == null || modifierTokens.Count == 0)
                return result;

            for (int i = 0; i < modifierTokens.Count; i++)
            {
                string token = modifierTokens[i];
                if (string.IsNullOrWhiteSpace(token))
                    continue;
                if (token.StartsWith("mouse_", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryConvertIl2TokenToDcs(token, devices, il2ToDx, out string device, out string key, out bool _, out string error))
                {
                    warnings.Add("Skipping unsupported modifier '" + token + "' for id '" + actionId + "'" +
                        (string.IsNullOrWhiteSpace(error) ? string.Empty : " (" + error + ")"));
                    continue;
                }

                string modName = ResolveModifierName(key);
                string reformer = BuildReformerString(modName, device, key, false);
                AddUniqueString(result, reformer);
            }

            return result;
        }

        private static bool TryConvertIl2TokenToDcs(
            string token,
            Dictionary<int, string> devices,
            Dictionary<string, string> il2ToDx,
            out string device,
            out string input,
            out bool axis,
            out string error)
        {
            device = string.Empty;
            input = string.Empty;
            axis = false;
            error = string.Empty;

            string value = (token ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                error = "token is empty";
                return false;
            }

            if (value.StartsWith("key_", StringComparison.OrdinalIgnoreCase))
            {
                string key = value.Substring(4).Trim();
                if (key.Length == 0)
                {
                    error = "key token is empty";
                    return false;
                }

                device = "Keyboard";
                if (il2ToDx.ContainsKey(key))
                    input = il2ToDx[key];
                else
                    input = key.Length == 1 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key.Substring(1);
                axis = false;
                return true;
            }

            if (!value.StartsWith("joy", StringComparison.OrdinalIgnoreCase))
            {
                error = "unsupported token prefix";
                return false;
            }

            int underscore = value.IndexOf('_');
            if (underscore <= 3)
            {
                error = "invalid joystick token";
                return false;
            }

            string indexRaw = value.Substring(3, underscore - 3);
            if (!int.TryParse(indexRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                error = "invalid joystick index";
                return false;
            }

            string rest = value.Substring(underscore + 1);
            if (rest.Length == 0)
            {
                error = "missing joystick input part";
                return false;
            }

            device = devices.ContainsKey(index)
                ? devices[index]
                : "Joystick " + index.ToString(CultureInfo.InvariantCulture);

            if (rest.StartsWith("axis_", StringComparison.OrdinalIgnoreCase))
            {
                string axisToken = rest.Substring("axis_".Length).Trim().ToLowerInvariant();
                switch (axisToken)
                {
                    case "x":
                        input = "JOY_X";
                        break;
                    case "y":
                        input = "JOY_Y";
                        break;
                    case "z":
                        input = "JOY_Z";
                        break;
                    case "w":
                        input = "JOY_RX";
                        break;
                    case "s":
                        input = "JOY_RY";
                        break;
                    case "t":
                        input = "JOY_RZ";
                        break;
                    case "p":
                        input = "JOY_SLIDER1";
                        break;
                    case "q":
                        input = "JOY_SLIDER2";
                        break;
                    default:
                        error = "unsupported IL2 axis token '" + axisToken + "'";
                        return false;
                }

                axis = true;
                return true;
            }

            if (rest.StartsWith("b", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(rest, "b63_povnu", StringComparison.OrdinalIgnoreCase))
                {
                    input = "JOY_BTN64";
                    axis = false;
                    return true;
                }

                if (!int.TryParse(rest.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int button))
                {
                    error = "unsupported IL2 button token '" + rest + "'";
                    return false;
                }

                input = "JOY_BTN" + (button + 1).ToString(CultureInfo.InvariantCulture);
                axis = false;
                return true;
            }

            // IL-2 numbers hats from 0 (pov0 = first hat), JoyPro like DCS from 1 (JOY_BTN_POV1_U): povN is
            // JOY_BTN_POV<N+1>. (JoyPro 1 read pov0 as POV0 and pov1+ as buttons 80+, and JoyPro 2's export wrote POV1
            // as pov1: a hat bind went to the next hat in IL-2.)
            if (rest.StartsWith("pov", StringComparison.OrdinalIgnoreCase) && rest.IndexOf('_') > 3)
            {
                int povSplit = rest.IndexOf('_');
                string povAngle = rest.Substring(povSplit + 1).Trim();
                string dir = AngleToPovDirection(povAngle);
                int povIndex;
                if (!string.IsNullOrWhiteSpace(dir) &&
                    int.TryParse(rest.Substring(3, povSplit - 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out povIndex) && povIndex >= 0)
                {
                    input = "JOY_BTN_POV" + (povIndex + 1).ToString(CultureInfo.InvariantCulture) + "_" + dir;
                    axis = false;
                    return true;
                }
            }

            if (rest.StartsWith("pov", StringComparison.OrdinalIgnoreCase))
            {
                int split = rest.IndexOf('_');
                if (split < 0)
                {
                    error = "invalid POV token '" + rest + "'";
                    return false;
                }

                string headRaw = rest.Substring(3, split - 3);
                string angleRaw = rest.Substring(split + 1);
                if (!int.TryParse(headRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int povHead))
                {
                    error = "invalid POV head";
                    return false;
                }

                int addition = PovAngleToAddition(angleRaw);
                if (addition < 0)
                {
                    error = "unsupported POV angle '" + angleRaw + "'";
                    return false;
                }

                int finalButton = (povHead - 1) * 8 + 80 + addition;
                input = "JOY_BTN" + finalButton.ToString(CultureInfo.InvariantCulture);
                axis = false;
                return true;
            }

            error = "unsupported joystick token part '" + rest + "'";
            return false;
        }

        private static bool TryConvertDcsTokenToIl2(
            string joystick,
            string input,
            bool axis,
            Dictionary<string, int> deviceIndex,
            Dictionary<string, string> dxToIl,
            out string token,
            out string error)
        {
            token = string.Empty;
            error = string.Empty;

            string joy = (joystick ?? string.Empty).Trim();
            string key = (input ?? string.Empty).Trim();
            if (joy.Length == 0 || key.Length == 0)
            {
                error = "missing joystick/input";
                return false;
            }

            if (string.Equals(joy, "Keyboard", StringComparison.OrdinalIgnoreCase))
            {
                string mapped = dxToIl.ContainsKey(key) ? dxToIl[key] : key.ToLowerInvariant();
                token = "key_" + mapped;
                return true;
            }

            if (!deviceIndex.ContainsKey(joy))
            {
                error = "device index not found for '" + joy + "'";
                return false;
            }

            int index = deviceIndex[joy];
            string prefix = "joy" + index.ToString(CultureInfo.InvariantCulture) + "_";

            if (axis)
            {
                string axisKey = key.Replace("JOY_", string.Empty).Trim().ToLowerInvariant();
                switch (axisKey)
                {
                    case "x":
                        token = prefix + "axis_x";
                        return true;
                    case "y":
                        token = prefix + "axis_y";
                        return true;
                    case "z":
                        token = prefix + "axis_z";
                        return true;
                    case "rx":
                        token = prefix + "axis_w";
                        return true;
                    case "ry":
                        token = prefix + "axis_s";
                        return true;
                    case "rz":
                        token = prefix + "axis_t";
                        return true;
                    case "slider1":
                        token = prefix + "axis_p";
                        return true;
                    case "slider2":
                        token = prefix + "axis_q";
                        return true;
                    default:
                        error = "unsupported axis '" + key + "'";
                        return false;
                }
            }

            if (key.StartsWith("JOY_BTN_POV", StringComparison.OrdinalIgnoreCase))
            {
                string povRaw = key.Substring("JOY_BTN_POV".Length);
                int split = povRaw.IndexOf('_');
                if (split < 0)
                {
                    error = "invalid POV input '" + key + "'";
                    return false;
                }

                string povIdRaw = povRaw.Substring(0, split);
                string direction = povRaw.Substring(split + 1);
                if (!int.TryParse(povIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int povId))
                {
                    error = "invalid POV id '" + povIdRaw + "'";
                    return false;
                }

                string angle = PovDirectionToAngle(direction);
                if (string.IsNullOrWhiteSpace(angle))
                {
                    error = "invalid POV direction '" + direction + "'";
                    return false;
                }

                int il2Pov = povId <= 0 ? 0 : povId - 1;
                token = prefix + "pov" + il2Pov.ToString(CultureInfo.InvariantCulture) + "_" + angle;
                return true;
            }

            if (!key.StartsWith("JOY_BTN", StringComparison.OrdinalIgnoreCase))
            {
                error = "unsupported button input '" + key + "'";
                return false;
            }

            string rawNumber = key.Substring("JOY_BTN".Length).Trim();
            if (!int.TryParse(rawNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out int buttonNumber))
            {
                error = "invalid button number '" + rawNumber + "'";
                return false;
            }

            int zeroBased = buttonNumber - 1;
            if (zeroBased < 0)
            {
                error = "button index below zero";
                return false;
            }
            if (zeroBased < 63)
            {
                token = prefix + "b" + zeroBased.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            if (zeroBased == 63)
            {
                token = prefix + "b63_povnu";
                return true;
            }

            if (buttonNumber >= 80)
            {
                int start = buttonNumber - 80;
                int povHead = (start / 8) + 1;
                int angle = (start % 8) * 45;
                token = prefix + "pov" + povHead.ToString(CultureInfo.InvariantCulture) + "_" + angle.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            token = prefix + "b" + zeroBased.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryRenderTemplate(
            ExportTemplate template,
            Dictionary<string, int> deviceIndex,
            Dictionary<string, string> dxToIl,
            Dictionary<string, AxisResponseSetting> generatedResponses,
            out ActionLine globalLine,
            out ActionLine jpLine,
            EngineIl2ExportResult result)
        {
            globalLine = null;
            jpLine = null;

            if (template == null || template.Primary == null)
                return false;

            if (!TryRenderCommandPart(template.Primary, deviceIndex, dxToIl, out string positiveToken, out string positiveError))
            {
                result.Warnings.Add("Skipping export of '" + template.Id + "': " + positiveError);
                return false;
            }

            string command = positiveToken;
            if (template.Secondary != null)
            {
                if (!TryRenderCommandPart(template.Secondary, deviceIndex, dxToIl, out string negativeToken, out string negativeError))
                {
                    result.Warnings.Add("Skipping export of '" + template.Id + "' secondary side: " + negativeError);
                    return false;
                }

                command = positiveToken + "/" + negativeToken;
            }

            bool inverted = template.Inverted && template.IsAxis;
            string descriptionComment = RenderDescriptionComment(template.IsAxis, template.Description);
            string metadataComment = RenderMetadataComment(template);
            string globalComment = descriptionComment;
            string jpComment = string.IsNullOrWhiteSpace(metadataComment)
                ? descriptionComment
                : (string.IsNullOrWhiteSpace(descriptionComment) ? metadataComment : descriptionComment + " " + metadataComment);

            string globalRaw = RenderActionRaw(template.Id, command, inverted, globalComment);
            string jpRaw = RenderActionRaw(template.Id, command, inverted, jpComment);

            globalLine = new ActionLine();
            globalLine.Id = template.Id;
            globalLine.Command = command;
            globalLine.Inverted = inverted;
            globalLine.Description = template.Description;
            globalLine.RawLine = globalRaw;
            globalLine.Sides = new List<ActionSide>();

            jpLine = new ActionLine();
            jpLine.Id = template.Id;
            jpLine.Command = command;
            jpLine.Inverted = inverted;
            jpLine.Description = template.Description;
            jpLine.RawLine = jpRaw;
            jpLine.Sides = new List<ActionSide>();

            if (template.IsAxis)
            {
                AxisResponseSetting setting = BuildAxisResponse(template);
                if (setting != null && !string.IsNullOrWhiteSpace(setting.Id))
                    generatedResponses[setting.Id] = setting;
            }

            return true;
        }

        private static bool TryRenderCommandPart(
            ExportPart part,
            Dictionary<string, int> deviceIndex,
            Dictionary<string, string> dxToIl,
            out string token,
            out string error)
        {
            token = string.Empty;
            error = string.Empty;
            if (part == null)
            {
                error = "missing export part";
                return false;
            }

            if (!TryConvertDcsTokenToIl2(part.Joystick, part.Input, part.IsAxis, deviceIndex, dxToIl, out string main, out string mainError))
            {
                error = mainError;
                return false;
            }

            List<string> modifierTokens = new List<string>();
            for (int i = 0; i < part.Reformers.Count; i++)
            {
                string reformer = part.Reformers[i];
                if (!TryParseReformer(reformer, out string name, out string device, out string key, out bool _))
                {
                    if (string.IsNullOrWhiteSpace(reformer))
                        continue;
                    name = reformer.Trim();
                    device = "Keyboard";
                    key = reformer.Trim();
                }

                string modName = string.IsNullOrWhiteSpace(name) ? ResolveModifierName(key) : name.Trim();
                _ = modName;
                if (!TryConvertDcsTokenToIl2(device, key, false, deviceIndex, dxToIl, out string modToken, out string modError))
                {
                    error = "unsupported modifier '" + reformer + "': " + modError;
                    return false;
                }
                AddUniqueString(modifierTokens, modToken);
            }

            if (modifierTokens.Count == 0)
            {
                token = main;
                return true;
            }

            token = string.Join("+", modifierTokens) + "+" + main;
            return true;
        }

        private static AxisResponseSetting BuildAxisResponse(ExportTemplate template)
        {
            if (template == null || template.Primary == null || !template.IsAxis)
                return null;

            ExportPart source = template.Primary;
            AxisResponseSetting setting = new AxisResponseSetting();
            setting.Id = template.Id;
            if (source.Tuning != null)
            {
                // A bind without IL-2 settings resolves to its base fitted by the "il2" model. A slider is a one-sided
                // row (type2: high and low dead zone); pitch with a centre shift goes to global.pitch (type3).
                JoyPro.Engine.Axis.AxisTuning t = source.Tuning;
                if (t.Slider)
                {
                    setting.Kind = "type2";
                    setting.HighDeadZone = Math.Round(JoyPro.Engine.Axis.AxisModels.Il2Value(t, "highValuesDeadZone"), 2);
                    setting.LowDeadZone = Math.Round(JoyPro.Engine.Axis.AxisModels.Il2Value(t, "lowValuesDeadZone"), 2);
                    return setting;
                }
                setting.CenterDeadZone = Math.Round(JoyPro.Engine.Axis.AxisModels.Il2Value(t, "centerDeadZone"), 2);
                setting.Sensitivity = Math.Round(JoyPro.Engine.Axis.AxisModels.Il2Value(t, "sensitivityBallance"), 2);
                setting.SideDeadZone = Math.Round(JoyPro.Engine.Axis.AxisModels.Il2Value(t, "sideDeadZone"), 2);
                setting.PitchShift = Math.Round(JoyPro.Engine.Axis.AxisModels.Il2Value(t, "pitchShift"), 2);
                if (setting.PitchShift != 0.0 && string.Equals(setting.Id, "rpc_pitch", StringComparison.OrdinalIgnoreCase))
                    setting.Kind = "type3";
                return setting;
            }
            if (source.Slider)
            {
                setting.CenterDeadZone = 0.0;
                setting.SideDeadZone = Math.Round(source.Deadzone * 0.25, 2);
            }
            else
            {
                setting.CenterDeadZone = Math.Round(source.Deadzone * 0.25, 2);
                setting.SideDeadZone = 0.0;
            }

            setting.Sensitivity = Math.Round(source.Sensitivity * 0.5, 2);
            return setting;
        }

        private static List<ExportTemplate> BuildExportTemplates(EngineIl2ExportResult result)
        {
            List<ExportNodeSource> sources = new List<ExportNodeSource>();
            foreach (KeyValuePair<string, Relation> relationEntry in InternalDataManagement.AllRelations.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                Relation relation = relationEntry.Value;
                result.RelationsScanned++;
                if (relation == null || relation.NODES == null || relation.NODES.Count == 0)
                {
                    result.SkippedEntries++;
                    continue;
                }

                Bind bind = relation.bind;
                if (bind == null && InternalDataManagement.AllBinds.ContainsKey(relationEntry.Key))
                    bind = InternalDataManagement.AllBinds[relationEntry.Key];
                if (bind == null)
                {
                    result.SkippedEntries++;
                    continue;
                }

                string input = relation.ISAXIS ? bind.JAxis : bind.JButton;
                if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(bind.Joystick))
                {
                    result.SkippedEntries++;
                    continue;
                }

                for (int i = 0; i < relation.NODES.Count; i++)
                {
                    RelationItem node = relation.NODES[i];
                    if (node == null || string.IsNullOrWhiteSpace(node.ID))
                        continue;
                    if (!string.Equals(NormalizeGame(node.Game), Il2Game, StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.NodesScanned++;
                    ExportNodeSource source = new ExportNodeSource();
                    source.NodeId = node.ID.Trim();
                    source.RelationName = relation.NAME;
                    source.Groups = relation.Groups == null ? new List<string>() : relation.Groups.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    source.Joystick = bind.Joystick;
                    source.Input = input;
                    source.IsAxis = relation.ISAXIS;
                    source.Inverted = bind.Inverted == true;
                    source.Slider = bind.Slider == true;
                    source.Deadzone = bind.Deadzone;
                    source.Sensitivity = bind.Curvature == null || bind.Curvature.Count == 0 ? Bind.Curvature_Default[0] : bind.Curvature[0];
                    source.Tuning = relation.ISAXIS ? JoyPro.Engine.Axis.BindTuning.Resolve(bind, Il2Game, null).Tuning : null;
                    source.Reformers = bind.AllReformers == null ? new List<string>() : new List<string>(bind.AllReformers);
                    source.Description = string.IsNullOrWhiteSpace(bind.AdditionalImportInfo) ? relation.NAME : bind.AdditionalImportInfo;
                    source.Alias = !string.IsNullOrWhiteSpace(bind.AliasJoystick)
                        ? bind.AliasJoystick
                        : (InternalDataManagement.JoystickAliases.ContainsKey(bind.Joystick) ? InternalDataManagement.JoystickAliases[bind.Joystick] : string.Empty);
                    source.Meta = JoyPro.Engine.Games.BindMetadata.FromRelation(relation, bind);
                    sources.Add(source);
                }
            }

            if (sources.Count == 0)
                return new List<ExportTemplate>();

            List<ExportTemplate> templates = new List<ExportTemplate>();
            Dictionary<string, List<ExportNodeSource>> byBase = new Dictionary<string, List<ExportNodeSource>>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < sources.Count; i++)
            {
                ExportNodeSource source = sources[i];
                string baseId = ExtractBaseId(source.NodeId);
                if (!byBase.ContainsKey(baseId))
                    byBase.Add(baseId, new List<ExportNodeSource>());
                byBase[baseId].Add(source);
            }

            foreach (KeyValuePair<string, List<ExportNodeSource>> entry in byBase.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                List<ExportNodeSource> all = entry.Value;
                List<ExportNodeSource> plain = all.Where(s => SideSuffix(s.NodeId) == '\0').ToList();
                List<ExportNodeSource> positive = all.Where(s => SideSuffix(s.NodeId) == '+').ToList();
                List<ExportNodeSource> negative = all.Where(s => SideSuffix(s.NodeId) == '-').ToList();

                for (int p = 0; p < plain.Count; p++)
                {
                    ExportNodeSource source = plain[p];
                    ExportTemplate template = new ExportTemplate();
                    template.Id = source.NodeId;
                    template.IsAxis = source.IsAxis;
                    template.Inverted = source.Inverted;
                    template.Description = source.Description;
                    template.Primary = ToExportPart(source);
                    templates.Add(template);
                }

                int pairCount = Math.Min(positive.Count, negative.Count);
                for (int pair = 0; pair < pairCount; pair++)
                {
                    ExportNodeSource pos = positive[pair];
                    ExportNodeSource neg = negative[pair];
                    ExportTemplate template = new ExportTemplate();
                    template.Id = entry.Key;
                    template.IsAxis = pos.IsAxis || neg.IsAxis;
                    template.Inverted = pos.Inverted;
                    template.Description = string.IsNullOrWhiteSpace(pos.Description) ? neg.Description : pos.Description;
                    template.Primary = ToExportPart(pos);
                    template.Secondary = ToExportPart(neg);
                    templates.Add(template);
                }

                for (int pair = pairCount; pair < positive.Count; pair++)
                {
                    ExportNodeSource pos = positive[pair];
                    ExportTemplate template = new ExportTemplate();
                    template.Id = pos.NodeId;
                    template.IsAxis = pos.IsAxis;
                    template.Inverted = pos.Inverted;
                    template.Description = pos.Description;
                    template.Primary = ToExportPart(pos);
                    templates.Add(template);
                }

                for (int pair = pairCount; pair < negative.Count; pair++)
                {
                    ExportNodeSource neg = negative[pair];
                    ExportTemplate template = new ExportTemplate();
                    template.Id = neg.NodeId;
                    template.IsAxis = neg.IsAxis;
                    template.Inverted = neg.Inverted;
                    template.Description = neg.Description;
                    template.Primary = ToExportPart(neg);
                    templates.Add(template);
                }
            }

            return templates;
        }

        private static Dictionary<string, int> BuildDeviceIndexMap(
            List<ExportTemplate> templates,
            Dictionary<int, string> existingDevices,
            EngineIl2ExportMode mode)
        {
            Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            HashSet<int> used = new HashSet<int>();

            // Every mode keeps the numbers of the devices already listed: clean keeps the binds of devices it does
            // not write, and those refer to their joy<N>.
            _ = mode;
            if (existingDevices != null)
            {
                foreach (KeyValuePair<int, string> existing in existingDevices.OrderBy(kvp => kvp.Key))
                {
                    if (string.IsNullOrWhiteSpace(existing.Value))
                        continue;
                    if (string.Equals(existing.Value, "Keyboard", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (map.ContainsKey(existing.Value))
                        continue;

                    map.Add(existing.Value, existing.Key);
                    used.Add(existing.Key);
                }
            }

            Dictionary<string, int> usage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < templates.Count; i++)
            {
                ExportTemplate template = templates[i];
                CountDeviceUsage(usage, template.Primary);
                CountDeviceUsage(usage, template.Secondary);
            }

            foreach (KeyValuePair<string, int> item in usage.OrderByDescending(u => u.Value).ThenBy(u => u.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (map.ContainsKey(item.Key))
                    continue;

                int index = 0;
                while (used.Contains(index))
                    index++;
                map[item.Key] = index;
                used.Add(index);
            }

            return map;
        }

        private static void CountDeviceUsage(Dictionary<string, int> usage, ExportPart part)
        {
            if (part == null || string.IsNullOrWhiteSpace(part.Joystick))
                return;
            if (string.Equals(part.Joystick, "Keyboard", StringComparison.OrdinalIgnoreCase))
                return;

            if (!usage.ContainsKey(part.Joystick))
                usage.Add(part.Joystick, 0);
            usage[part.Joystick]++;
        }

        // The device an action line binds: "joy<N>" or "key" (keyboard / mouse).
        private static string DeviceOf(ActionLine line)
        {
            Match match = Regex.Match(line == null ? string.Empty : line.Command ?? string.Empty, @"joy(\d+)_", RegexOptions.IgnoreCase);
            return match.Success ? "joy" + match.Groups[1].Value : "key";
        }

        private static string ActionDeviceOf(ActionLine line)
        {
            return (line.Id ?? string.Empty).Trim().ToLowerInvariant() + "|" + DeviceOf(line);
        }

        private static List<ActionLine> MergeActionLines(
            EngineIl2ExportMode mode,
            List<ActionLine> existing,
            List<ActionLine> generated,
            HashSet<string> writtenDevices,
            bool nukeUntouchedDevices)
        {
            existing = (existing ?? new List<ActionLine>()).Where(l => l != null).ToList();
            generated = (generated ?? new List<ActionLine>()).Where(l => l != null).ToList();
            writtenDevices = writtenDevices ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (nukeUntouchedDevices)
                existing = existing.Where(l => DeviceOf(l) == "key" || writtenDevices.Contains(DeviceOf(l))).ToList();

            switch (mode)
            {
                case EngineIl2ExportMode.Clean:
                    {
                        // Lines of the keyboard and of devices the export does not write stay.
                        List<ActionLine> combined = new List<ActionLine>(generated);
                        combined.AddRange(existing.Where(l => !writtenDevices.Contains(DeviceOf(l))));
                        return combined;
                    }
                case EngineIl2ExportMode.Add:
                    {
                        // A line the file already has (same action and input) is not added again.
                        List<ActionLine> combined = new List<ActionLine>(existing);
                        HashSet<string> existingBinds = new HashSet<string>(
                            existing.Where(l => l != null).Select(BindKeyOf),
                            StringComparer.OrdinalIgnoreCase);
                        foreach (ActionLine line in generated)
                        {
                            if (line != null && existingBinds.Add(BindKeyOf(line)))
                                combined.Add(line);
                        }
                        return combined;
                    }
                case EngineIl2ExportMode.Merge:
                    {
                        // A conflict is the same action on the same device: the file's line stays.
                        List<ActionLine> combined = new List<ActionLine>(existing);
                        HashSet<string> existingKeys = new HashSet<string>(existing.Select(ActionDeviceOf), StringComparer.Ordinal);
                        foreach (ActionLine line in generated)
                        {
                            if (existingKeys.Add(ActionDeviceOf(line)))
                                combined.Add(line);
                        }
                        return combined;
                    }
                case EngineIl2ExportMode.MergeOverwrite:
                    {
                        // A conflict is the same action on the same device: the profile's line replaces it.
                        List<ActionLine> combined = new List<ActionLine>(generated);
                        HashSet<string> generatedKeys = new HashSet<string>(generated.Select(ActionDeviceOf), StringComparer.Ordinal);
                        combined.AddRange(existing.Where(l => !generatedKeys.Contains(ActionDeviceOf(l))));
                        return combined;
                    }
                default:
                    return new List<ActionLine>(generated);
            }
        }

        private static string BindKeyOf(ActionLine line)
        {
            return (line.Id ?? string.Empty).Trim() + "|" + (line.Command ?? string.Empty).Replace(" ", string.Empty);
        }

        private static Dictionary<string, AxisResponseSetting> MergeAxisSettings(
            EngineIl2ExportMode mode,
            Dictionary<string, AxisResponseSetting> existing,
            Dictionary<string, AxisResponseSetting> generated)
        {
            existing = existing ?? new Dictionary<string, AxisResponseSetting>(StringComparer.OrdinalIgnoreCase);
            generated = generated ?? new Dictionary<string, AxisResponseSetting>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, AxisResponseSetting> merged = new Dictionary<string, AxisResponseSetting>(StringComparer.OrdinalIgnoreCase);
            if (mode == EngineIl2ExportMode.Clean)
            {
                foreach (KeyValuePair<string, AxisResponseSetting> item in generated)
                    merged[item.Key] = item.Value;
                return merged;
            }

            foreach (KeyValuePair<string, AxisResponseSetting> item in existing)
                merged[item.Key] = item.Value;

            foreach (KeyValuePair<string, AxisResponseSetting> item in generated)
            {
                if (mode == EngineIl2ExportMode.MergeOverwrite)
                {
                    merged[item.Key] = item.Value;
                }
                else if (!merged.ContainsKey(item.Key))
                {
                    merged[item.Key] = item.Value;
                }
            }

            return merged;
        }

        // Backs the file up and writes it only when the text changes, so an export that changes nothing leaves the
        // game files and their .jpbak backups alone.
        private static void WriteIfChanged(string filePath, string text, EngineIl2ExportResult result)
        {
            if (File.Exists(filePath) && string.Equals(File.ReadAllText(filePath), text, StringComparison.Ordinal))
                return;
            GameExportSafety.BackupBeforeOverwrite(filePath, result.Warnings);
            File.WriteAllText(filePath, text, NoBom);
            result.FilesWritten++;
            result.WrittenFiles.Add(filePath);
        }

        private static void WriteActionsFile(string filePath, List<string> lines, EngineIl2ExportResult result)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? string.Empty);

                StringBuilder sb = new StringBuilder();
                sb.Append(ActionsHeader);
                for (int i = 0; i < lines.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i]))
                        continue;
                    sb.Append(lines[i]);
                    if (i < lines.Count - 1)
                        sb.Append("\r\n");
                }

                WriteIfChanged(filePath, sb.ToString(), result);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Failed writing '" + filePath + "': " + ex.Message);
            }
        }

        private static void WriteDevicesFile(string filePath, Dictionary<string, int> nameToIndex, EngineIl2ExportMode mode, EngineIl2ExportResult result)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? string.Empty);

                IEnumerable<KeyValuePair<string, int>> ordered = nameToIndex
                    .Where(kvp => !string.Equals(kvp.Key, "Keyboard", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(kvp => kvp.Value)
                    .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine(DevicesHeader);
                foreach (KeyValuePair<string, int> entry in ordered)
                {
                    string line = DcsJoyIdToIl2JoyId(entry.Key, entry.Value);
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        line = entry.Value.ToString(CultureInfo.InvariantCulture) +
                               ",%22" + BuildFallbackDeviceGuid(entry.Value) + "%22," +
                               UrlEncodeDeviceName(entry.Key);
                    }
                    sb.AppendLine(line + "|");
                }

                WriteIfChanged(filePath, sb.ToString(), result);
                _ = mode;
            }
            catch (Exception ex)
            {
                result.Errors.Add("Failed writing '" + filePath + "': " + ex.Message);
            }
        }

        private const string OneSidedFields = "actionId%2ChighValuesDeadZone%2ClowValuesDeadZone";
        private const string PitchFields = "actionId%2CcenterDeadZone%2CpitchShift%2CsensitivityBallance%2CsideDeadZone";
        private const string DefaultThrottleRow = "rpc_all_engines_throttles%2C0%2C0";

        // current.responses: type4, type2 (one-sided rows) and type1 (centred rows) in the order IL-2 writes them, every
        // action in one section only; the game's own type2 throttle row stays unless JoyPro writes that action. The
        // pitch rows (type3) go to global.pitch, which is written only when it exists or gets a row.
        private static void WriteResponsesFile(string filePath, Dictionary<string, AxisResponseSetting> settings, EngineIl2ExportResult result)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? string.Empty);
                List<AxisResponseSetting> ordered = settings.Values.Where(s => s != null && !string.IsNullOrWhiteSpace(s.Id))
                    .OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList();
                StringBuilder sb = new StringBuilder();
                sb.Append("type4Devices=" + OneSidedFields);
                foreach (AxisResponseSetting s in ordered.Where(s => s.Kind == "type4"))
                    sb.Append("%7C%0D%0A" + s.Id + "%2C" + ToInvariant(s.HighDeadZone) + "%2C" + ToInvariant(s.LowDeadZone));
                sb.Append("&type2Devices=" + OneSidedFields);
                if (!settings.ContainsKey("rpc_all_engines_throttles"))
                    sb.Append("%7C%0D%0A" + DefaultThrottleRow);
                foreach (AxisResponseSetting s in ordered.Where(s => s.Kind == "type2"))
                    sb.Append("%7C%0D%0A" + s.Id + "%2C" + ToInvariant(s.HighDeadZone) + "%2C" + ToInvariant(s.LowDeadZone));
                sb.Append("&" + ResponsesCursor);
                foreach (AxisResponseSetting s in ordered.Where(s => !s.OneSided && s.Kind != "type3"))
                    sb.Append("%7C%0D%0A" + s.Id + "%2C" + ToInvariant(s.CenterDeadZone) + "%2C" + ToInvariant(s.Sensitivity) + "%2C" + ToInvariant(s.SideDeadZone));
                WriteIfChanged(filePath, sb.ToString(), result);

                string pitchPath = PitchFileOf(filePath);
                List<AxisResponseSetting> pitchRows = ordered.Where(s => s.Kind == "type3").ToList();
                if (pitchRows.Count > 0 || File.Exists(pitchPath))
                {
                    StringBuilder pitch = new StringBuilder("type3Devices=" + PitchFields);
                    foreach (AxisResponseSetting s in pitchRows)
                        pitch.Append("%7C%0D%0A" + s.Id + "%2C" + ToInvariant(s.CenterDeadZone) + "%2C" + ToInvariant(s.PitchShift) + "%2C" + ToInvariant(s.Sensitivity) + "%2C" + ToInvariant(s.SideDeadZone));
                    WriteIfChanged(pitchPath, pitch.ToString(), result);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add("Failed writing '" + filePath + "': " + ex.Message);
            }
        }

        private static void AttachBackupMetadata(List<ActionLine> globalLines, List<ActionLine> jpLines)
        {
            Dictionary<string, ActionLine> jpBySignature = new Dictionary<string, ActionLine>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<ActionLine>> jpById = new Dictionary<string, List<ActionLine>>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < jpLines.Count; i++)
            {
                ActionLine line = jpLines[i];
                if (line == null)
                    continue;
                if (!jpBySignature.ContainsKey(line.Signature))
                    jpBySignature.Add(line.Signature, line);
                if (!jpById.ContainsKey(line.Id))
                    jpById.Add(line.Id, new List<ActionLine>());
                jpById[line.Id].Add(line);
            }

            for (int i = 0; i < globalLines.Count; i++)
            {
                ActionLine global = globalLines[i];
                if (global == null || HasMetadata(global))
                    continue;

                if (jpBySignature.ContainsKey(global.Signature))
                {
                    CopyMetadata(global, jpBySignature[global.Signature]);
                    continue;
                }

                if (!jpById.ContainsKey(global.Id))
                    continue;
                List<ActionLine> candidates = jpById[global.Id];
                ActionLine best = candidates.FirstOrDefault(c => string.Equals(StripWhitespace(c.Command), StripWhitespace(global.Command), StringComparison.OrdinalIgnoreCase));
                if (best == null)
                    best = candidates.FirstOrDefault();
                if (best != null)
                    CopyMetadata(global, best);
            }
        }

        private static bool HasMetadata(ActionLine line)
        {
            if (line == null || line.Sides == null)
                return false;
            for (int i = 0; i < line.Sides.Count; i++)
            {
                ActionSide side = line.Sides[i];
                if (side != null && side.Metadata != null && !string.IsNullOrWhiteSpace(side.Metadata.RelationName))
                    return true;
            }
            return false;
        }

        private static void CopyMetadata(ActionLine target, ActionLine source)
        {
            if (target == null || source == null || target.Sides == null || source.Sides == null)
                return;

            int count = Math.Min(target.Sides.Count, source.Sides.Count);
            for (int i = 0; i < count; i++)
            {
                if (target.Sides[i] == null || source.Sides[i] == null)
                    continue;
                target.Sides[i].Metadata = source.Sides[i].Metadata == null
                    ? null
                    : source.Sides[i].Metadata.Copy();
            }

            if (string.IsNullOrWhiteSpace(target.Description) && !string.IsNullOrWhiteSpace(source.Description))
                target.Description = source.Description;
        }

        private static List<ActionLine> ParseActionsFile(string filePath, bool parseMetadata, List<string> warnings)
        {
            List<ActionLine> actions = new List<ActionLine>();
            if (!File.Exists(filePath))
                return actions;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(filePath);
            }
            catch (Exception ex)
            {
                warnings?.Add("Could not read actions file '" + filePath + "': " + ex.Message);
                return actions;
            }

            bool actionsStarted = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!actionsStarted)
                {
                    if (line.IndexOf("&actions", StringComparison.OrdinalIgnoreCase) >= 0)
                        actionsStarted = true;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (!TryParseActionLine(line, parseMetadata, out ActionLine parsed))
                    continue;
                actions.Add(parsed);
            }

            return actions;
        }

        private static bool TryParseActionLine(string rawLine, bool parseMetadata, out ActionLine line)
        {
            line = null;
            if (string.IsNullOrWhiteSpace(rawLine))
                return false;

            int firstComma = rawLine.IndexOf(',');
            if (firstComma < 0)
                return false;
            int secondComma = rawLine.IndexOf(',', firstComma + 1);
            if (secondComma < 0)
                return false;
            int pipe = rawLine.IndexOf('|', secondComma + 1);
            if (pipe < 0)
                return false;

            string id = rawLine.Substring(0, firstComma).Trim();
            string command = rawLine.Substring(firstComma + 1, secondComma - firstComma - 1).Trim();
            string invertRaw = rawLine.Substring(secondComma + 1, pipe - secondComma - 1).Trim();
            string tail = pipe + 1 < rawLine.Length ? rawLine.Substring(pipe + 1) : string.Empty;

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(command))
                return false;

            bool inverted = invertRaw == "1" || string.Equals(invertRaw, "true", StringComparison.OrdinalIgnoreCase);
            string description = ParseDescription(tail);
            List<MetadataSegment> metadata = parseMetadata ? ParseMetadata(rawLine) : new List<MetadataSegment>();
            List<ActionSide> sides = ParseActionSides(command);
            if (sides.Count == 0)
                return false;

            for (int i = 0; i < sides.Count; i++)
            {
                ActionSide side = sides[i];
                if (side == null)
                    continue;
                if (metadata.Count == 1)
                    side.Metadata = metadata[0].Copy();
                else if (metadata.Count >= 2)
                    side.Metadata = (i == 0 ? metadata[0] : metadata[1]).Copy();
            }

            line = new ActionLine();
            line.Id = id;
            line.Command = command;
            line.Inverted = inverted;
            line.Description = description;
            line.RawLine = rawLine.TrimEnd();
            line.Sides = sides;
            line.Signature = BuildActionSignature(id, command, inverted);
            return true;
        }

        private static List<ActionSide> ParseActionSides(string command)
        {
            List<ActionSide> sides = new List<ActionSide>();
            if (string.IsNullOrWhiteSpace(command))
                return sides;

            string[] split = command.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            bool dual = split.Length > 1;
            for (int i = 0; i < split.Length; i++)
            {
                string sideRaw = split[i].Trim();
                if (sideRaw.Length == 0)
                    continue;

                string[] parts = sideRaw.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;

                string main = parts[parts.Length - 1].Trim();
                if (main.Length == 0)
                    continue;

                ActionSide side = new ActionSide();
                side.RawToken = sideRaw;
                side.MainToken = main;
                side.SideSuffix = dual ? (i == 0 ? '+' : '-') : '\0';
                side.ModifierTokens = new List<string>();
                for (int p = 0; p < parts.Length - 1; p++)
                {
                    string modifier = parts[p].Trim();
                    if (modifier.Length > 0)
                        side.ModifierTokens.Add(modifier);
                }
                sides.Add(side);
            }

            return sides;
        }

        private static string ParseDescription(string tail)
        {
            if (string.IsNullOrWhiteSpace(tail))
                return string.Empty;

            string text = tail.Trim();
            int jpIndex = text.IndexOf("//JP:", StringComparison.OrdinalIgnoreCase);
            if (jpIndex >= 0)
                text = text.Substring(0, jpIndex).Trim();
            if (text.StartsWith("//", StringComparison.Ordinal))
                text = text.Substring(2).Trim();
            if (text.Length >= 2 && (text.StartsWith("a ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("b ", StringComparison.OrdinalIgnoreCase)))
                text = text.Substring(2).Trim();
            return text.Trim();
        }

        private static List<MetadataSegment> ParseMetadata(string rawLine)
        {
            List<MetadataSegment> metadata = new List<MetadataSegment>();
            if (string.IsNullOrWhiteSpace(rawLine))
                return metadata;

            string marker = "//JP:";
            int index = 0;
            while (true)
            {
                int start = rawLine.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                    break;

                int contentStart = start + marker.Length;
                int next = rawLine.IndexOf(marker, contentStart, StringComparison.OrdinalIgnoreCase);
                string segment = next < 0
                    ? rawLine.Substring(contentStart)
                    : rawLine.Substring(contentStart, next - contentStart);
                index = next < 0 ? rawLine.Length : next;

                JoyPro.Engine.Games.BindMetadata fields = JoyPro.Engine.Games.BindMetadata.FromCommentFields(segment.Trim());
                MetadataSegment item = new MetadataSegment();
                item.RelationName = fields.RelationName;
                item.Groups = fields.Groups;
                item.Alias = fields.Alias;
                item.Full = fields;
                metadata.Add(item);

                if (next < 0)
                    break;
            }

            return metadata;
        }

        private static Dictionary<int, string> ParseDevicesFile(string filePath, List<string> warnings)
        {
            Dictionary<int, string> map = new Dictionary<int, string>();
            if (!File.Exists(filePath))
                return map;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(filePath);
            }
            catch (Exception ex)
            {
                warnings?.Add("Could not read devices file '" + filePath + "': " + ex.Message);
                return map;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                string trimmed = line.Trim().TrimEnd('|').Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("configId", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] parts = trimmed.Split(',');
                if (parts.Length < 3)
                    continue;
                if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                    continue;

                string guid = parts[1].Trim().Replace("%22", string.Empty).Replace("\"", string.Empty);
                string device = parts[2].Trim();
                string dcsName = Il2JoyIdToDcsJoyId(guid, device);
                if (string.IsNullOrWhiteSpace(dcsName))
                    dcsName = Uri.UnescapeDataString(device.Replace("%20", " "));
                if (string.IsNullOrWhiteSpace(dcsName))
                    dcsName = "Joystick " + id.ToString(CultureInfo.InvariantCulture);

                if (!map.ContainsKey(id))
                    map.Add(id, dcsName);
            }

            return map;
        }

        // current.responses ("type4Devices=<fields>|rows&type2Devices=...&type1Devices=...", URL-encoded) and the pitch
        // file global.pitch next to it ("type3Devices=..."). Fields are read by their header names.
        private static Dictionary<string, AxisResponseSetting> ParseResponsesFile(string filePath, List<string> warnings)
        {
            Dictionary<string, AxisResponseSetting> output = new Dictionary<string, AxisResponseSetting>(StringComparer.OrdinalIgnoreCase);
            foreach (AxisResponseSetting setting in ParseResponseSections(filePath, warnings))
            {
                AxisResponseSetting held;
                // A one-sided row wins over a type1 row of the same action, as in the game.
                if (output.TryGetValue(setting.Id, out held) && held.OneSided && !setting.OneSided)
                    continue;
                output[setting.Id] = setting;
            }
            foreach (AxisResponseSetting setting in ParseResponseSections(PitchFileOf(filePath), warnings))
            {
                if (!string.Equals(setting.Kind, "type3", StringComparison.OrdinalIgnoreCase))
                    continue;
                AxisResponseSetting held;
                if (output.TryGetValue(setting.Id, out held) && !PitchFileWins)
                    continue;
                output[setting.Id] = setting;
            }
            return output;
        }

        // Whether a global.pitch row wins over a current.responses row of the same action.
        private const bool PitchFileWins = true;

        private static string PitchFileOf(string responsesPath)
        {
            return Path.Combine(Path.GetDirectoryName(responsesPath) ?? string.Empty, "global.pitch");
        }

        private static List<AxisResponseSetting> ParseResponseSections(string filePath, List<string> warnings)
        {
            List<AxisResponseSetting> output = new List<AxisResponseSetting>();
            if (!File.Exists(filePath))
                return output;
            string content;
            try
            {
                content = File.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                warnings?.Add("Could not read responses file '" + filePath + "': " + ex.Message);
                return output;
            }

            foreach (string section in content.Trim().Split('&'))
            {
                int eq = section.IndexOf('=');
                if (eq <= 0)
                    continue;
                string name = section.Substring(0, eq).Trim();
                if (!name.EndsWith("Devices", StringComparison.OrdinalIgnoreCase))
                    continue;
                string kind = name.Substring(0, name.Length - "Devices".Length).ToLowerInvariant();
                string[] entries = section.Substring(eq + 1).Split(new[] { "%7C%0D%0A", "%7C" }, StringSplitOptions.RemoveEmptyEntries);
                if (entries.Length == 0)
                    continue;
                string[] fields = entries[0].Split(new[] { "%2C" }, StringSplitOptions.None);
                for (int i = 1; i < entries.Length; i++)
                {
                    string[] parts = entries[i].Split(new[] { "%2C" }, StringSplitOptions.None);
                    string id = parts[0].Trim();
                    if (id.Length == 0)
                        continue;
                    AxisResponseSetting setting = new AxisResponseSetting { Id = id, Kind = kind };
                    for (int f = 1; f < fields.Length && f < parts.Length; f++)
                    {
                        if (!TryParseDouble(parts[f], out double value))
                            value = 0.0;
                        switch (fields[f].Trim())
                        {
                            case "centerDeadZone": setting.CenterDeadZone = value; break;
                            case "sensitivityBallance": setting.Sensitivity = value; break;
                            case "sideDeadZone": setting.SideDeadZone = value; break;
                            case "pitchShift": setting.PitchShift = value; break;
                            case "highValuesDeadZone": setting.HighDeadZone = value; break;
                            case "lowValuesDeadZone": setting.LowDeadZone = value; break;
                        }
                    }
                    output.Add(setting);
                }
            }
            return output;
        }

        internal static void LoadKeyboardConversions(
            out Dictionary<string, string> il2ToDx,
            out Dictionary<string, string> dxToIl,
            List<string> warnings)
        {
            il2ToDx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            dxToIl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string path = Path.Combine(MainStructure.PROGPATH ?? Environment.CurrentDirectory, "TOOLS", "Conversions", "IL2DX.keyboardconversion");
            if (!File.Exists(path))
            {
                warnings.Add("Missing keyboard conversion file: " + path);
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read keyboard conversion file '" + path + "': " + ex.Message);
                return;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                string normalized = line.Replace("Â§", SignatureSeparator);
                int split = normalized.IndexOf(SignatureSeparator, StringComparison.Ordinal);
                if (split <= 0 || split >= normalized.Length - 1)
                    continue;

                string left = normalized.Substring(0, split).Trim();
                string right = normalized.Substring(split + SignatureSeparator.Length).Trim();
                if (left.Length == 0 || right.Length == 0)
                    continue;

                if (!il2ToDx.ContainsKey(left))
                    il2ToDx.Add(left, right);
                if (!dxToIl.ContainsKey(right))
                    dxToIl.Add(right, left);
            }
        }

        private static bool TryParseExportMode(string raw, out EngineIl2ExportMode mode)
        {
            mode = EngineIl2ExportMode.Clean;
            string value = string.IsNullOrWhiteSpace(raw) ? "clean" : raw.Trim().ToLowerInvariant();
            switch (value)
            {
                case "clean":
                    mode = EngineIl2ExportMode.Clean;
                    return true;
                case "add":
                    mode = EngineIl2ExportMode.Add;
                    return true;
                case "merge":
                    mode = EngineIl2ExportMode.Merge;
                    return true;
                case "merge-overwrite":
                case "mergeoverwrite":
                case "overwrite":
                    mode = EngineIl2ExportMode.MergeOverwrite;
                    return true;
                default:
                    return false;
            }
        }

        private static string ExportModeToString(EngineIl2ExportMode mode)
        {
            switch (mode)
            {
                case EngineIl2ExportMode.Add:
                    return "add";
                case EngineIl2ExportMode.Merge:
                    return "merge";
                case EngineIl2ExportMode.MergeOverwrite:
                    return "merge-overwrite";
                default:
                    return "clean";
            }
        }

        internal static string ResolveInputPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string full = Path.GetFullPath(path);
            if (File.Exists(full) && full.EndsWith(".actions", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(full);
            if (File.Exists(Path.Combine(full, "global.actions")))
                return full;

            string normalized = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string dataInput = Path.Combine("data", "input");
            if (normalized.EndsWith(dataInput, StringComparison.OrdinalIgnoreCase))
                return normalized;
            if (normalized.EndsWith("input", StringComparison.OrdinalIgnoreCase) && Directory.Exists(normalized))
                return normalized;
            if (normalized.EndsWith("data", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(normalized, "input");
            return Path.Combine(full, "data", "input");
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

        private static string BuildActionSignature(string id, string command, bool inverted)
        {
            return (id ?? string.Empty).Trim().ToLowerInvariant() + "|" +
                   StripWhitespace(command).ToLowerInvariant() + "|" +
                   (inverted ? "1" : "0");
        }

        private static string StripWhitespace(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!char.IsWhiteSpace(c))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static string BuildGeneratedRelationName(ActionLine line, ActionSide side, string joystick, string input)
        {
            string source = string.IsNullOrWhiteSpace(line.Description) ? line.Id : line.Description;
            string name = SanitizeName(source);
            string device = SanitizeName(joystick);
            string key = SanitizeName(input);
            string id = SanitizeName(line.Id);
            string suffix = side.SideSuffix == '\0' ? "single" : (side.SideSuffix == '+' ? "pos" : "neg");
            return name + "__" + device + "__" + key + "__" + id + "__" + suffix;
        }

        private static string BuildUniqueRelationName(string baseName, Dictionary<string, Relation> existing)
        {
            string root = string.IsNullOrWhiteSpace(baseName) ? "Relation" : baseName.Trim();
            string candidate = root;
            int count = 1;
            while (existing.ContainsKey(candidate))
            {
                candidate = root + "__" + count.ToString(CultureInfo.InvariantCulture);
                count++;
            }
            return candidate;
        }

        private static bool SameReformerSet(List<string> left, List<string> right)
        {
            List<string> a = NormalizeReformerNames(left);
            List<string> b = NormalizeReformerNames(right);
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private static List<string> NormalizeReformerNames(List<string> reformers)
        {
            List<string> names = new List<string>();
            if (reformers == null)
                return names;
            for (int i = 0; i < reformers.Count; i++)
            {
                string reformer = reformers[i];
                if (TryParseReformer(reformer, out string name, out string _, out string _, out bool _sw))
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        AddUniqueString(names, name.Trim());
                }
            }
            return names.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool TryParseReformer(string reformer, out string name, out string device, out string key, out bool sw)
        {
            name = string.Empty;
            device = string.Empty;
            key = string.Empty;
            sw = false;

            if (string.IsNullOrWhiteSpace(reformer))
                return false;

            string normalized = reformer.Replace("\u00C2\u00A7", SignatureSeparator).Replace("Â§", SignatureSeparator);
            string[] parts = normalized.Split(new[] { SignatureSeparator }, StringSplitOptions.None);
            if (parts.Length < 3)
                return false;

            name = (parts[0] ?? string.Empty).Trim();
            device = (parts[1] ?? string.Empty).Trim();
            key = (parts[2] ?? string.Empty).Trim();
            if (parts.Length > 3)
                sw = ParseBool(parts[3]);
            return true;
        }

        private static string BuildReformerString(string name, string device, string key, bool sw)
        {
            return (name ?? string.Empty).Trim() + SignatureSeparator +
                   (device ?? string.Empty).Trim() + SignatureSeparator +
                   (key ?? string.Empty).Trim() + SignatureSeparator +
                   sw.ToString();
        }

        private static string ResolveModifierName(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return "Modifier";
            string trimmed = key.Trim();
            if (DefaultModifierNames.Contains(trimmed))
                return trimmed;
            return trimmed;
        }

        private static string RenderDescriptionComment(bool axis, string description)
        {
            string prefix = axis ? "//a" : "//b";
            if (string.IsNullOrWhiteSpace(description))
                return prefix;
            return prefix + " " + description.Trim();
        }

        private static string RenderMetadataComment(ExportTemplate template)
        {
            if (template == null || template.Primary == null)
                return string.Empty;

            string primary = "//JP:" + RenderMetadataFields(template.Primary);
            if (template.Secondary == null)
                return primary;
            return primary + "//JP:" + RenderMetadataFields(template.Secondary);
        }

        // name;groups;alias (the old form, which JoyPro 1 also read) and, escaped, the modifiers with their names and the
        // axis tuning (base and every scope), so an import gets the relation back as it was (BindMetadata).
        private static string RenderMetadataFields(ExportPart part)
        {
            if (part.Meta != null)
                return part.Meta.ToCommentFields();
            JoyPro.Engine.Games.BindMetadata meta = new JoyPro.Engine.Games.BindMetadata
            {
                RelationName = part.RelationName ?? string.Empty,
                Groups = part.Groups ?? new List<string>(),
                Alias = part.Alias ?? string.Empty
            };
            return meta.ToCommentFields();
        }

        private static string RenderActionRaw(string id, string command, bool inverted, string comment)
        {
            string safeId = string.IsNullOrWhiteSpace(id) ? "unknown" : id.Trim();
            string safeCommand = string.IsNullOrWhiteSpace(command) ? "key_unknown" : command.Trim();
            string safeComment = string.IsNullOrWhiteSpace(comment) ? string.Empty : comment.Trim();
            if (safeComment.Length == 0)
                return safeId + ", " + safeCommand + ", " + (inverted ? "1" : "0") + "|";
            return safeId + ", " + safeCommand + ", " + (inverted ? "1" : "0") + "| " + safeComment;
        }

        private static ExportPart ToExportPart(ExportNodeSource source)
        {
            ExportPart part = new ExportPart();
            part.RelationName = source.RelationName;
            part.Groups = source.Groups ?? new List<string>();
            part.Alias = source.Alias ?? string.Empty;
            part.Joystick = source.Joystick ?? string.Empty;
            part.Input = source.Input ?? string.Empty;
            part.IsAxis = source.IsAxis;
            part.Inverted = source.Inverted;
            part.Slider = source.Slider;
            part.Deadzone = source.Deadzone;
            part.Sensitivity = source.Sensitivity;
            part.Tuning = source.Tuning;
            part.Reformers = source.Reformers ?? new List<string>();
            part.Meta = source.Meta;
            return part;
        }

        private static string ExtractBaseId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return string.Empty;
            char suffix = SideSuffix(id);
            if (suffix == '+' || suffix == '-')
                return id.Substring(0, id.Length - 1);
            return id;
        }

        private static char SideSuffix(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return '\0';
            char last = id[id.Length - 1];
            return (last == '+' || last == '-') ? last : '\0';
        }

        private static List<string> ParseGroupList(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();
            return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(g => g.Trim())
                .Where(g => g.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g)
                .ToList();
        }

        private static string SanitizeMetadataValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return value
                .Replace(";", ",")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
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
                char outChar = (char.IsLetterOrDigit(c) || c == '-' || c == '_') ? c : '_';
                if (outChar == '_' && previous == '_')
                    continue;
                sb.Append(outChar);
                previous = outChar;
            }

            string value = sb.ToString().Trim('_');
            return value.Length == 0 ? "item" : value;
        }

        private static string AngleToPovDirection(string angleRaw)
        {
            switch ((angleRaw ?? string.Empty).Trim())
            {
                case "0":
                    return "U";
                case "45":
                    return "UR";
                case "90":
                    return "R";
                case "135":
                    return "DR";
                case "180":
                    return "D";
                case "225":
                    return "DL";
                case "270":
                    return "L";
                case "315":
                    return "UL";
                default:
                    return null;
            }
        }

        private static int PovAngleToAddition(string angleRaw)
        {
            switch ((angleRaw ?? string.Empty).Trim())
            {
                case "0":
                    return 0;
                case "45":
                    return 1;
                case "90":
                    return 2;
                case "135":
                    return 3;
                case "180":
                    return 4;
                case "225":
                    return 5;
                case "270":
                    return 6;
                case "315":
                    return 7;
                default:
                    return -1;
            }
        }

        private static string PovDirectionToAngle(string directionRaw)
        {
            string value = (directionRaw ?? string.Empty).Trim().ToUpperInvariant();
            switch (value)
            {
                case "U":
                    return "0";
                case "UR":
                    return "45";
                case "R":
                    return "90";
                case "DR":
                    return "135";
                case "D":
                    return "180";
                case "DL":
                    return "225";
                case "L":
                    return "270";
                case "UL":
                    return "315";
                default:
                    return null;
            }
        }

        private static string Il2JoyIdToDcsJoyId(string guid, string device)
        {
            try
            {
                string cleanGuid = (guid ?? string.Empty).Replace("%22", string.Empty).Replace("\"", string.Empty);
                string cleanDevice = Uri.UnescapeDataString((device ?? string.Empty).Replace("%20", "%20")).Trim();
                if (cleanGuid.Length == 0 || cleanDevice.Length == 0)
                    return string.Empty;

                string[] parts = cleanGuid.Split('-');
                if (parts.Length < 3)
                    return string.Empty;
                // IL-2 writes the last 8 bytes of the DirectInput GUID reversed as one group of 16 hex digits
                // (8002-444553540000 -> 0000545345440280); turn them back into JoyPro's device name.
                if (parts.Length == 4 && parts[3].Length == 16 && IsHex(parts[3]))
                {
                    string tail = ReverseHexBytes(parts[3]);
                    parts = new[] { parts[0], parts[1], parts[2], tail.Substring(0, 4), tail.Substring(4) };
                }

                StringBuilder sb = new StringBuilder();
                sb.Append(cleanDevice);
                sb.Append(" {");
                sb.Append(parts[0].ToUpperInvariant());
                for (int i = 1; i < parts.Length; i++)
                {
                    sb.Append("-");
                    sb.Append(i == 2 ? parts[i].ToLowerInvariant() : parts[i].ToUpperInvariant());
                }
                sb.Append("}");
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string DcsJoyIdToIl2JoyId(string dcsJoyId, int id)
        {
            if (id < 0 || string.IsNullOrWhiteSpace(dcsJoyId) || !dcsJoyId.Contains("{"))
                return string.Empty;

            string[] major = dcsJoyId.Split('{');
            if (major.Length < 2)
                return string.Empty;

            string guid = major[1].Replace("}", string.Empty).Trim();
            string name = major[0].Trim().Replace(" ", "%20");
            string[] guidCells = guid.Split('-');
            // A device name an older import took over in IL-2's form stays in that form.
            if (guidCells.Length == 4 && guidCells[3].Length == 16 && IsHex(guidCells[3]))
                return id.ToString(CultureInfo.InvariantCulture) + ",%22" + guid.ToLowerInvariant() + "%22," + name;
            if (guidCells.Length < 5 || guidCells[3].Length != 4 || guidCells[4].Length != 12 || !IsHex(guidCells[3] + guidCells[4]))
                return string.Empty;

            // IL-2's form: the last 8 bytes reversed as one group (see Il2JoyIdToDcsJoyId).
            return id.ToString(CultureInfo.InvariantCulture) + ",%22" +
                   guidCells[0].ToLowerInvariant() + "-" +
                   guidCells[1].ToLowerInvariant() + "-" +
                   guidCells[2].ToLowerInvariant() + "-" +
                   ReverseHexBytes(guidCells[3] + guidCells[4]).ToLowerInvariant() +
                   "%22," + name;
        }

        private static string ReverseHexBytes(string hex)
        {
            StringBuilder sb = new StringBuilder(hex.Length);
            for (int i = hex.Length - 2; i >= 0; i -= 2)
                sb.Append(hex, i, 2);
            return sb.ToString();
        }

        private static bool IsHex(string text)
        {
            return !string.IsNullOrEmpty(text) && text.All(Uri.IsHexDigit);
        }

        private static string BuildFallbackDeviceGuid(int index)
        {
            string suffix = index.ToString("D4", CultureInfo.InvariantCulture);
            return "00000000-0000-0000-0000-00000000" + suffix;
        }

        private static string UrlEncodeDeviceName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Device";
            return value.Trim().Replace(" ", "%20");
        }

        private static bool TryParseDouble(string value, out double parsed)
        {
            return double.TryParse(
                (value ?? string.Empty).Trim(),
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out parsed);
        }

        private static string ToInvariant(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static bool ParseBool(string value)
        {
            if (bool.TryParse(value, out bool parsed))
                return parsed;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedInt))
                return parsedInt != 0;
            return false;
        }

        private static void AddUniqueString(List<string> list, string value)
        {
            if (list == null || string.IsNullOrWhiteSpace(value))
                return;
            if (!list.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)))
                list.Add(value);
        }

        private static void AddUniqueGroup(List<string> groups, string group)
        {
            if (groups == null || string.IsNullOrWhiteSpace(group))
                return;
            if (!groups.Any(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase)))
                groups.Add(group.Trim());
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<none>" : value;
        }

        private sealed class ActionLine
        {
            public string Id { get; set; }
            public string Command { get; set; }
            public bool Inverted { get; set; }
            public string Description { get; set; }
            public string RawLine { get; set; }
            public string Signature { get; set; }
            public List<ActionSide> Sides { get; set; }

            public ActionLine()
            {
                Id = string.Empty;
                Command = string.Empty;
                Description = string.Empty;
                RawLine = string.Empty;
                Signature = string.Empty;
                Sides = new List<ActionSide>();
            }
        }

        private sealed class ActionSide
        {
            public char SideSuffix { get; set; }
            public string RawToken { get; set; }
            public string MainToken { get; set; }
            public List<string> ModifierTokens { get; set; }
            public MetadataSegment Metadata { get; set; }

            public ActionSide()
            {
                SideSuffix = '\0';
                RawToken = string.Empty;
                MainToken = string.Empty;
                ModifierTokens = new List<string>();
                Metadata = null;
            }
        }

        private sealed class MetadataSegment
        {
            public string RelationName { get; set; }
            public List<string> Groups { get; set; }
            public string Alias { get; set; }
            // All fields of the comment (modifiers, tuning); null for a segment not read from a comment.
            public JoyPro.Engine.Games.BindMetadata Full { get; set; }

            public MetadataSegment()
            {
                RelationName = string.Empty;
                Groups = new List<string>();
                Alias = string.Empty;
            }

            public MetadataSegment Copy()
            {
                return new MetadataSegment
                {
                    RelationName = RelationName,
                    Groups = new List<string>(Groups ?? new List<string>()),
                    Alias = Alias,
                    Full = Full
                };
            }
        }

        // One response row. Kind is its section: "type1" (centred axes, current.responses), "type2"/"type4" (one-sided
        // axes such as throttles: high and low dead zone, current.responses) or "type3" (the pitch file global.pitch,
        // with the centre shift). IL-2 applies a row to its action whatever the section; a one-sided row wins over a
        // type1 row of the same action (tools\e2e\research\Il2GbAxesSurvey.ps1).
        private sealed class AxisResponseSetting
        {
            public string Id { get; set; }
            public string Kind { get; set; }
            public double CenterDeadZone { get; set; }
            public double Sensitivity { get; set; }
            public double SideDeadZone { get; set; }
            public double PitchShift { get; set; }
            public double HighDeadZone { get; set; }
            public double LowDeadZone { get; set; }

            public bool OneSided
            {
                get { return Kind == "type2" || Kind == "type4"; }
            }

            public AxisResponseSetting()
            {
                Id = string.Empty;
                Kind = "type1";
            }
        }

        private sealed class ImportCandidate
        {
            public string RelationName { get; set; }
            public string NodeId { get; set; }
            public bool IsAxis { get; set; }
            public string Joystick { get; set; }
            public string Input { get; set; }
            public List<string> Reformers { get; set; }
            public string Description { get; set; }
            public List<string> Groups { get; set; }
            public string Alias { get; set; }
            public bool Inverted { get; set; }
            public bool Slider { get; set; }
            public double Deadzone { get; set; }
            public double Sensitivity { get; set; }
            // The axis row of current.responses (null when not imported).
            public AxisResponseSetting Response { get; set; }
            // JoyPro's data from the //JP: comment (null without one).
            public JoyPro.Engine.Games.BindMetadata Meta { get; set; }

            public ImportCandidate()
            {
                RelationName = string.Empty;
                NodeId = string.Empty;
                Joystick = string.Empty;
                Input = string.Empty;
                Reformers = new List<string>();
                Description = string.Empty;
                Groups = new List<string>();
                Alias = string.Empty;
            }
        }

        private sealed class ExportNodeSource
        {
            public string NodeId { get; set; }
            public string RelationName { get; set; }
            public List<string> Groups { get; set; }
            public string Alias { get; set; }
            public string Joystick { get; set; }
            public string Input { get; set; }
            public bool IsAxis { get; set; }
            public bool Inverted { get; set; }
            public bool Slider { get; set; }
            public double Deadzone { get; set; }
            public double Sensitivity { get; set; }
            // The IL-2 response the bind resolves to (game:IL2Game, family:il2, else its base fitted); null for buttons.
            public JoyPro.Engine.Axis.AxisTuning Tuning { get; set; }
            public List<string> Reformers { get; set; }
            public string Description { get; set; }
            // The bind's JoyPro data for the //JP: comment (name, groups, alias, modifiers, tuning).
            public JoyPro.Engine.Games.BindMetadata Meta { get; set; }

            public ExportNodeSource()
            {
                NodeId = string.Empty;
                RelationName = string.Empty;
                Groups = new List<string>();
                Alias = string.Empty;
                Joystick = string.Empty;
                Input = string.Empty;
                Reformers = new List<string>();
                Description = string.Empty;
            }
        }

        private sealed class ExportPart
        {
            public string RelationName { get; set; }
            public List<string> Groups { get; set; }
            public string Alias { get; set; }
            public string Joystick { get; set; }
            public string Input { get; set; }
            public bool IsAxis { get; set; }
            public bool Inverted { get; set; }
            public bool Slider { get; set; }
            public double Deadzone { get; set; }
            public double Sensitivity { get; set; }
            public JoyPro.Engine.Axis.AxisTuning Tuning { get; set; }
            public List<string> Reformers { get; set; }

            public JoyPro.Engine.Games.BindMetadata Meta { get; set; }

            public ExportPart()
            {
                RelationName = string.Empty;
                Groups = new List<string>();
                Alias = string.Empty;
                Joystick = string.Empty;
                Input = string.Empty;
                Reformers = new List<string>();
            }
        }

        private sealed class ExportTemplate
        {
            public string Id { get; set; }
            public bool IsAxis { get; set; }
            public bool Inverted { get; set; }
            public string Description { get; set; }
            public ExportPart Primary { get; set; }
            public ExportPart Secondary { get; set; }

            public ExportTemplate()
            {
                Id = string.Empty;
                Description = string.Empty;
            }
        }

        private enum EngineIl2ExportMode
        {
            Clean,
            Add,
            Merge,
            MergeOverwrite
        }
    }
}
