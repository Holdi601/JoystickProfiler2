using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using JoyPro.Engine.Axis;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    internal static partial class EngineStarCitizen
    {
        private const string StarCitizenGame = "StarCitizen";
        private const string StarCitizenPlane = "StarCitizen";
        private const string SignatureSeparator = "\u00A7";
        private const string KeyboardConversionSeparator = "\u00A7";
        private const int MaxJoystickSlots = 8;

        // Star Citizen also has on-foot, EVA and ground-vehicle controls; JoyPro only imports and writes ship controls.
        private static readonly HashSet<string> ShipMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "seat_general", "IFCS_controls", "lights_controller", "turret_movement", "turret_advanced", "vehicle_general", "vehicle_mfd"
        };

        internal static bool IsFlightMap(string category)
        {
            string name = (category ?? string.Empty).Trim();
            return name.StartsWith("spaceship_", StringComparison.OrdinalIgnoreCase) || ShipMaps.Contains(name);
        }

        public static EngineStarCitizenImportResult Scan(string rootOrPath, EngineStarCitizenImportOptions options = null)
        {
            return ExecuteImport(rootOrPath, false, false, options);
        }

        public static EngineStarCitizenImportResult Import(string rootOrPath, bool merge, EngineStarCitizenImportOptions options = null)
        {
            return ExecuteImport(rootOrPath, true, merge, options);
        }

        public static EngineStarCitizenExportResult Export(string rootOrPath, string modeRaw, EngineStarCitizenExportOptions options = null)
        {
            EngineStarCitizenExportResult result = new EngineStarCitizenExportResult();
            EngineStarCitizenExportOptions effectiveOptions = NormalizeExportOptions(options);
            ReportStatus(effectiveOptions, "Starting Star Citizen export.");

            if (!TryParseExportMode(modeRaw, out StarCitizenExportMode mode))
            {
                result.Errors.Add("Invalid Star Citizen export mode: " + (modeRaw ?? string.Empty) + ". Use clean, add, merge or merge-overwrite.");
                return result;
            }

            result.Mode = ExportModeToString(mode);
            result.KeepKeyboardDefaults = effectiveOptions.KeepKeyboardDefaults;
            result.PreferLocalOrder = effectiveOptions.PreferLocalOrder;

            List<string> pathWarnings = new List<string>();
            ScProfilePaths paths = ResolveProfilePaths(rootOrPath, effectiveOptions.SourcePath, true, pathWarnings);
            AddRangeUnique(result.Warnings, pathWarnings);
            result.RootPath = paths.RootPath;
            result.ProfileDirectory = paths.ProfileDirectory;
            result.ActionmapsFile = paths.ActionmapsFile;
            result.MetadataFile = paths.MetadataFile;

            if (!paths.IsValid)
            {
                result.Errors.Add("Could not resolve Star Citizen profile path from: " + (rootOrPath ?? "<auto>"));
                return result;
            }

            Dictionary<string, ScOptionGroupInfo> optionTree;
            Dictionary<string, ScCatalogAction> catalog = LoadCatalog(result.Warnings, out optionTree);
            if (catalog.Count == 0)
            {
                result.Errors.Add("Star Citizen DB catalog is empty. Run: engine db refresh-starcitizen");
                return result;
            }

            // The existing actionmaps.xml is edited in place: only rebinds of ship actions change; other action maps,
            // mouse and gamepad rebinds, activation modes, device options and modifiers stay as they are.
            Dictionary<string, ScBindingState> states = BuildStateFromCatalog(catalog);
            ScActionFileData localActionData = new ScActionFileData();
            bool hasLocalActionmaps = TryReadActionFile(paths.ActionmapsFile, result.Warnings, out localActionData);

            List<KeyValuePair<ScBindSource, ScBindingState>> resolved = new List<KeyValuePair<ScBindSource, ScBindingState>>();
            int keyboardSkipped = 0;
            foreach (ScBindSource source in CollectProfileBindings(result))
            {
                if (source == null)
                    continue;
                if (source.InputType == "kb" && !effectiveOptions.IncludeKeyboard)
                {
                    keyboardSkipped++;
                    result.SkippedEntries++;
                    continue;
                }
                string nodeMap = NodeKeyMap(source.NodeKey);
                if (nodeMap.Length > 0 && !IsFlightMap(nodeMap))
                {
                    result.SkippedEntries++;
                    AddUnique(result.Warnings, "Skipping '" + source.NodeKey + "': JoyPro only writes Star Citizen ship controls.");
                    continue;
                }
                ScBindingState state;
                string lookupProblem;
                if (!TryResolveNodeState(states, source.NodeKey, out state, out lookupProblem))
                {
                    result.SkippedEntries++;
                    AddUnique(result.Warnings, lookupProblem);
                    continue;
                }
                if (!IsFlightMap(state.Category))
                {
                    result.SkippedEntries++;
                    AddUnique(result.Warnings, "Skipping '" + state.Category + "|" + state.Id + "': JoyPro only writes Star Citizen ship controls.");
                    continue;
                }
                resolved.Add(new KeyValuePair<ScBindSource, ScBindingState>(source, state));
            }
            if (keyboardSkipped > 0)
                AddUnique(result.Warnings, keyboardSkipped.ToString(CultureInfo.InvariantCulture) + " keyboard bind(s) not exported (use --include-keyboard true to include them).");

            Dictionary<string, int> deviceUsage = resolved
                .Select(p => p.Key)
                .Where(s => !string.IsNullOrWhiteSpace(s.Device) && s.InputType == "js")
                .GroupBy(s => s.Device, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            ScSlotPlan slotPlan = BuildJoystickSlotPlan(
                hasLocalActionmaps ? localActionData.JoystickOptions : new List<ScOptionDevice>(),
                deviceUsage,
                effectiveOptions.PreferLocalOrder,
                result.Warnings);
            foreach (ScOptionDevice option in slotPlan.Options.OrderBy(v => v.Instance))
                result.JoystickOptions.Add("js" + option.Instance.ToString(CultureInfo.InvariantCulture) + " -> " + option.Product);

            // Wanted rebinds per ship action and input type (one per type, as in the game's own menu).
            Dictionary<string, Dictionary<string, ScWantedRebind>> wanted = new Dictionary<string, Dictionary<string, ScWantedRebind>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<ScBindSource, ScBindingState> pair in resolved)
            {
                string convertedInput;
                if (!TryConvertProfileBindToScInput(pair.Key, slotPlan, result.Warnings, out convertedInput))
                {
                    result.SkippedEntries++;
                    continue;
                }
                string key = BuildNodeKey(pair.Value.Category, pair.Value.Id);
                Dictionary<string, ScWantedRebind> byType;
                if (!wanted.TryGetValue(key, out byType))
                {
                    byType = new Dictionary<string, ScWantedRebind>(StringComparer.OrdinalIgnoreCase);
                    wanted[key] = byType;
                }
                ScWantedRebind existingWanted;
                if (byType.TryGetValue(pair.Key.InputType, out existingWanted))
                {
                    result.SkippedEntries++;
                    AddUnique(result.Warnings, "Star Citizen takes one " + pair.Key.InputType + " bind per action: '" + key + "' keeps relation '" + existingWanted.RelationName + "', skipped '" + pair.Key.RelationName + "'.");
                    continue;
                }
                byType[pair.Key.InputType] = new ScWantedRebind { State = pair.Value, Input = convertedInput, RelationName = pair.Key.RelationName, IsAxis = pair.Key.IsAxis, Bind = pair.Key.Bind };
            }

            ScActionmapsDocument document = ScActionmapsDocument.Open(paths.ActionmapsFile, hasLocalActionmaps, localActionData);
            document.SetJoystickProducts(slotPlan, result.Warnings);
            List<XElement> joyProRebinds = new List<XElement>();
            List<ScWantedRebind> writtenRebinds = new List<ScWantedRebind>();

            foreach (KeyValuePair<string, Dictionary<string, ScWantedRebind>> entry in wanted)
            {
                foreach (KeyValuePair<string, ScWantedRebind> typed in entry.Value)
                {
                    ScWantedRebind want = typed.Value;
                    XElement action = document.FindOrCreateAction(want.State.Category, want.State.Id, want.State.Title, want.State.IsAxis);
                    List<XElement> current = action.Elements("rebind").Where(r => RebindType(r) == typed.Key).ToList();
                    if (current.Count > 0 && (mode == StarCitizenExportMode.Merge || mode == StarCitizenExportMode.Add) &&
                        !current.Any(r => string.Equals(RebindInput(r), want.Input, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.MergeConflictsKept++;
                        continue;
                    }
                    XElement same = current.FirstOrDefault(r => string.Equals(RebindInput(r), want.Input, StringComparison.OrdinalIgnoreCase));
                    if (same == null && current.Count > 0)
                    {
                        // Only the input changes; the rebind keeps its place and attributes (activation mode etc.).
                        same = current[0];
                        same.SetAttributeValue("input", want.Input);
                        result.MergeConflictsOverwritten++;
                        result.EntriesWritten++;
                    }
                    foreach (XElement old in current.Where(r => !ReferenceEquals(r, same)))
                    {
                        ScActionmapsDocument.RemoveWithIndent(old);
                        result.MergeConflictsOverwritten++;
                    }
                    if (same == null)
                    {
                        same = document.AddRebind(action, want.Input);
                        result.EntriesWritten++;
                        result.MergeConflictsAdded++;
                    }
                    joyProRebinds.Add(same);
                    writtenRebinds.Add(want);
                }
            }

            // Axis settings of the axis rebinds JoyPro wrote: option groups (invert, exponent/curve, sensitivity) and
            // device options (dead zone, saturation). Ownership next to actionmaps.xml lets merge keep in-game changes.
            Dictionary<string, HashSet<string>> ownedBefore = GameOwnershipStore.Load(paths.ActionmapsFile, result.Warnings);
            Dictionary<string, HashSet<string>> owned = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            ApplyAxisSettings(document, writtenRebinds, optionTree, mode, ownedBefore, owned, result);

            // Clean releases the ship binds of the devices the export writes, nuke those of the devices it does not use.
            // Keyboard rebinds only with --include-keyboard; mouse and gamepad rebinds are never touched.
            if (mode == StarCitizenExportMode.Clean || effectiveOptions.NukeUnusedDevices)
            {
                HashSet<int> usedSlots = new HashSet<int>(slotPlan.DeviceToSlot.Values);
                bool keyboardUsed = wanted.Values.Any(t => t.ContainsKey("kb"));
                foreach (XElement action in document.ShipActions().ToList())
                {
                    string key = BuildNodeKey((string)action.Parent.Attribute("name"), (string)action.Attribute("name"));
                    Dictionary<string, ScWantedRebind> wantedTypes;
                    wanted.TryGetValue(key, out wantedTypes);
                    foreach (XElement rebind in action.Elements("rebind").ToList())
                    {
                        string type = RebindType(rebind);
                        if (wantedTypes != null && wantedTypes.ContainsKey(type))
                            continue;
                        bool release;
                        if (type == "js")
                            release = usedSlots.Contains(RebindInstance(rebind)) ? mode == StarCitizenExportMode.Clean : effectiveOptions.NukeUnusedDevices;
                        else if (type == "kb")
                            release = effectiveOptions.IncludeKeyboard && (keyboardUsed ? mode == StarCitizenExportMode.Clean : effectiveOptions.NukeUnusedDevices);
                        else
                            release = false;
                        if (!release)
                            continue;
                        ScActionmapsDocument.RemoveWithIndent(rebind);
                        result.MergeConflictsOverwritten++;
                    }
                    if (!action.Elements().Any())
                        document.RemoveEmptyAction(action);
                }

                // The game's own joystick defaults (defaultProfile.xml joystick="y", "button1", ...) act on js1 until
                // the action has a js rebind. Clean (js1 written) and nuke (js1 not used) switch them off with an
                // empty rebind "js1_ ", as the game's menu does when a bind is cleared. Ship actions only.
                bool js1Used = usedSlots.Contains(1);
                if (js1Used ? mode == StarCitizenExportMode.Clean : effectiveOptions.NukeUnusedDevices)
                {
                    foreach (KeyValuePair<string, ScCatalogAction> entry in catalog.Where(c => !string.IsNullOrWhiteSpace(c.Value.DefaultJoystick) && IsFlightMap(c.Value.Category)))
                    {
                        Dictionary<string, ScWantedRebind> wantedTypes;
                        if (wanted.TryGetValue(entry.Key, out wantedTypes) && wantedTypes.ContainsKey("js"))
                            continue;
                        XElement existingAction = document.ShipActions().FirstOrDefault(a =>
                            string.Equals((string)a.Parent.Attribute("name"), entry.Value.Category, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals((string)a.Attribute("name"), entry.Value.Id, StringComparison.OrdinalIgnoreCase));
                        if (existingAction != null && existingAction.Elements("rebind").Any(r => RebindType(r) == "js"))
                            continue;
                        XElement action = existingAction ?? document.FindOrCreateAction(entry.Value.Category, entry.Value.Id, entry.Value.Title, entry.Value.IsAxis);
                        document.AddRebind(action, "js1_ ");
                        result.DefaultJoystickBindsCleared++;
                    }
                }
            }

            result.CategoriesWritten = wanted.Values.SelectMany(t => t.Values).Select(w => w.State.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            result.ActionsWritten = wanted.Count;
            result.JoystickSlotsUsed = slotPlan.DeviceToSlot.Values.Distinct().Count();

            // actionmaps.jp: the same file plus JoyPro's relation names as comments (read back on import).
            string actionmapsPayload = document.Render();
            foreach (XElement rebind in joyProRebinds)
            {
                ScWantedRebind want = wanted.Values.SelectMany(t => t.Values).FirstOrDefault(w => string.Equals(w.Input, RebindInput(rebind), StringComparison.OrdinalIgnoreCase));
                if (want != null && rebind.Parent != null)
                    rebind.AddAfterSelf(new XComment(XmlEscapeComment(want.RelationName) + "//" + (want.IsAxis ? "true" : "false")));
            }
            string metadataPayload = document.Render();

            if (!WriteTextFile(paths.ActionmapsFile, actionmapsPayload, result))
                return result;
            if (!WriteTextFile(paths.MetadataFile, metadataPayload, result))
                return result;

            result.FilesWritten = 2;
            if (owned.Count > 0 || File.Exists(paths.ActionmapsFile + GameOwnershipStore.Suffix))
            {
                try
                {
                    GameOwnershipStore.Save(paths.ActionmapsFile, StarCitizenGame, owned);
                }
                catch (Exception ex)
                {
                    AddUnique(result.Warnings, "Could not save the Star Citizen ownership file: " + ex.Message);
                }
            }
            ApplyGameSettings(paths.ProfileDirectory, result);
            ReportStatus(
                effectiveOptions,
                "Star Citizen export complete. Actions written: " +
                result.ActionsWritten.ToString(CultureInfo.InvariantCulture) +
                ", entries: " +
                result.EntriesWritten.ToString(CultureInfo.InvariantCulture) + ".");
            return result;
        }

        public static List<string> DiscoverInputDevices(string rootOrPath, string sourcePath, List<string> warnings = null)
        {
            List<string> localWarnings = warnings ?? new List<string>();
            ScProfilePaths paths = ResolveProfilePaths(rootOrPath, sourcePath, false, localWarnings);
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            output.Add("Keyboard");

            if (!paths.IsValid || !File.Exists(paths.ActionmapsFile))
                return output.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

            ScActionFileData data;
            if (!TryReadActionFile(paths.ActionmapsFile, localWarnings, out data))
                return output.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

            for (int i = 0; i < data.JoystickOptions.Count; i++)
            {
                string product = NormalizeScProductToken(data.JoystickOptions[i].Product);
                int occurrence = data.JoystickOptions.Take(i).Count(o => string.Equals(NormalizeScProductToken(o.Product), product, StringComparison.OrdinalIgnoreCase));
                string resolved = ResolveDeviceNameFromProduct(product, localWarnings, occurrence);
                if (!string.IsNullOrWhiteSpace(resolved))
                    output.Add(resolved);
            }

            if (!string.IsNullOrWhiteSpace(data.GamepadProduct))
                output.Add(ResolveGamepadDeviceName(data.GamepadProduct));
            else
                output.Add("Controller (Gamepad)");

            return output.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string ToText(EngineStarCitizenImportResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Star Citizen Import");
            sb.AppendLine("Root Path: " + Safe(result.RootPath));
            sb.AppendLine("Profile Directory: " + Safe(result.ProfileDirectory));
            sb.AppendLine("Actionmaps: " + Safe(result.ActionmapsFile));
            sb.AppendLine("Metadata: " + Safe(result.MetadataFile));
            sb.AppendLine("Merge: " + result.Merge);
            sb.AppendLine("Selected Devices: " + (result.SelectedDevices.Count == 0 ? "<all>" : string.Join(", ", result.SelectedDevices)));
            sb.AppendLine("Ignored Devices: " + (result.IgnoredDevices.Count == 0 ? "<none>" : string.Join(", ", result.IgnoredDevices)));
            sb.AppendLine("Matched Devices: " + (result.MatchedDevices.Count == 0 ? "<none>" : string.Join(", ", result.MatchedDevices)));
            sb.AppendLine("Include Keyboard: " + result.IncludeKeyboard);
            sb.AppendLine("Actions Parsed: " + result.ActionsParsed);
            sb.AppendLine("Rebind Entries Parsed: " + result.RebindEntriesParsed);
            sb.AppendLine("Candidates Parsed: " + result.CandidatesParsed);
            sb.AppendLine("Filtered By Device: " + result.CandidatesFilteredByDevice);
            sb.AppendLine("Imported Relations: " + result.ImportedRelations);
            sb.AppendLine("Imported Binds: " + result.ImportedBinds);
            sb.AppendLine("Merged Into Existing: " + result.MergedIntoExisting);
            sb.AppendLine("Skipped Entries: " + result.SkippedEntries);
            sb.AppendLine("Axis Settings Imported: " + result.AxisSettingsImported);

            if (result.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                for (int i = 0; i < result.Errors.Count; i++)
                    sb.AppendLine("  - " + result.Errors[i]);
            }

            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < result.Warnings.Count; i++)
                    sb.AppendLine("  - " + result.Warnings[i]);
            }

            return sb.ToString();
        }

        public static string ToText(EngineStarCitizenExportResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Star Citizen Export");
            sb.AppendLine("Root Path: " + Safe(result.RootPath));
            sb.AppendLine("Profile Directory: " + Safe(result.ProfileDirectory));
            sb.AppendLine("Actionmaps: " + Safe(result.ActionmapsFile));
            sb.AppendLine("Metadata: " + Safe(result.MetadataFile));
            sb.AppendLine("Mode: " + Safe(result.Mode));
            sb.AppendLine("Keep Keyboard Defaults: " + result.KeepKeyboardDefaults);
            sb.AppendLine("Prefer Local Order: " + result.PreferLocalOrder);
            sb.AppendLine("Relations Scanned: " + result.RelationsScanned);
            sb.AppendLine("Nodes Scanned: " + result.NodesScanned);
            sb.AppendLine("Entries Written: " + result.EntriesWritten);
            sb.AppendLine("Actions Written: " + result.ActionsWritten);
            sb.AppendLine("Categories Written: " + result.CategoriesWritten);
            sb.AppendLine("Joystick Slots Used: " + result.JoystickSlotsUsed);
            sb.AppendLine("Files Written: " + result.FilesWritten);
            sb.AppendLine("Merge Conflicts Kept: " + result.MergeConflictsKept);
            sb.AppendLine("Merge Conflicts Overwritten: " + result.MergeConflictsOverwritten);
            sb.AppendLine("Default Joystick Binds Cleared: " + result.DefaultJoystickBindsCleared);
            sb.AppendLine("Merge Conflicts Added: " + result.MergeConflictsAdded);
            sb.AppendLine("Skipped Entries: " + result.SkippedEntries);

            if (result.JoystickOptions.Count > 0)
            {
                sb.AppendLine("Joystick Slots:");
                for (int i = 0; i < result.JoystickOptions.Count; i++)
                    sb.AppendLine("  - " + result.JoystickOptions[i]);
            }

            sb.AppendLine("Option Groups Written: " + result.OptionGroupsWritten);
            sb.AppendLine("Device Options Written: " + result.DeviceOptionsWritten);
            if (result.AxisSettings.Count > 0)
            {
                sb.AppendLine("Axis Settings:");
                for (int i = 0; i < result.AxisSettings.Count; i++)
                    sb.AppendLine("  - " + result.AxisSettings[i]);
            }
            if (result.GameSettings.Count > 0)
            {
                sb.AppendLine("Game Settings (attributes.xml):");
                for (int i = 0; i < result.GameSettings.Count; i++)
                    sb.AppendLine("  - " + result.GameSettings[i]);
            }

            if (result.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                for (int i = 0; i < result.Errors.Count; i++)
                    sb.AppendLine("  - " + result.Errors[i]);
            }

            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < result.Warnings.Count; i++)
                    sb.AppendLine("  - " + result.Warnings[i]);
            }

            return sb.ToString();
        }

        private static EngineStarCitizenImportResult ExecuteImport(
            string rootOrPath,
            bool applyImport,
            bool merge,
            EngineStarCitizenImportOptions options)
        {
            EngineStarCitizenImportResult result = new EngineStarCitizenImportResult();
            EngineStarCitizenImportOptions effectiveOptions = NormalizeImportOptions(options);
            result.Merge = merge;
            result.IncludeKeyboard = effectiveOptions.IncludeKeyboard;
            result.SelectedDevices = new List<string>(effectiveOptions.DeviceFilter);
            result.IgnoredDevices = new List<string>(effectiveOptions.IgnoreDeviceFilter);
            ReportStatus(effectiveOptions, applyImport ? "Starting Star Citizen import." : "Starting Star Citizen scan.");

            List<string> pathWarnings = new List<string>();
            ScProfilePaths paths = ResolveProfilePaths(rootOrPath, effectiveOptions.SourcePath, false, pathWarnings);
            AddRangeUnique(result.Warnings, pathWarnings);
            result.RootPath = paths.RootPath;
            result.ProfileDirectory = paths.ProfileDirectory;
            result.ActionmapsFile = paths.ActionmapsFile;
            result.MetadataFile = paths.MetadataFile;

            if (!paths.IsValid || !File.Exists(paths.ActionmapsFile))
            {
                result.Errors.Add("Star Citizen actionmaps.xml not found. Checked: " + Safe(paths.ActionmapsFile));
                return result;
            }

            ScActionFileData actionData;
            if (!TryReadActionFile(paths.ActionmapsFile, result.Warnings, out actionData))
            {
                result.Errors.Add("Could not parse Star Citizen actionmaps.xml: " + Safe(paths.ActionmapsFile));
                return result;
            }

            Dictionary<string, ScOptionGroupInfo> optionTree;
            Dictionary<string, ScCatalogAction> catalog = LoadCatalog(result.Warnings, out optionTree);
            Dictionary<string, ScRebindMetadataHints> hints = ParseRebindMetadataHints(paths.MetadataFile, result.Warnings);
            Dictionary<string, string> scToDxKeyboard = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> dxToScKeyboard = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            LoadKeyboardConversion(scToDxKeyboard, dxToScKeyboard, result.Warnings);
            _ = dxToScKeyboard;

            List<ScImportCandidate> candidates = BuildImportCandidates(
                actionData,
                catalog,
                hints,
                scToDxKeyboard,
                effectiveOptions,
                result,
                optionTree);

            if (!applyImport)
                return result;

            ApplyImportCandidates(candidates, merge, result);
            ReportStatus(
                effectiveOptions,
                "Star Citizen import complete. Relations: " +
                result.ImportedRelations.ToString(CultureInfo.InvariantCulture) +
                ", binds: " +
                result.ImportedBinds.ToString(CultureInfo.InvariantCulture) + ".");
            return result;
        }

        private static EngineStarCitizenImportOptions NormalizeImportOptions(EngineStarCitizenImportOptions options)
        {
            EngineStarCitizenImportOptions normalized = options ?? new EngineStarCitizenImportOptions();
            normalized.DeviceFilter = NormalizeStringList(normalized.DeviceFilter);
            normalized.IgnoreDeviceFilter = NormalizeStringList(normalized.IgnoreDeviceFilter);
            normalized.SourcePath = NormalizePath(normalized.SourcePath);
            return normalized;
        }

        private static EngineStarCitizenExportOptions NormalizeExportOptions(EngineStarCitizenExportOptions options)
        {
            EngineStarCitizenExportOptions normalized = options ?? new EngineStarCitizenExportOptions();
            normalized.SourcePath = NormalizePath(normalized.SourcePath);
            return normalized;
        }

        private static void ApplyImportCandidates(
            List<ScImportCandidate> candidates,
            bool merge,
            EngineStarCitizenImportResult result)
        {
            Dictionary<string, Relation> relations = merge
                ? new Dictionary<string, Relation>(InternalDataManagement.AllRelations, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Relation>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Bind> binds = merge
                ? new Dictionary<string, Bind>(InternalDataManagement.AllBinds, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, string> signatureToRelation = BuildSignatureIndex(relations, binds);
            Dictionary<Bind, KeyValuePair<string, ScAxisValues>> appliedAxisValues = new Dictionary<Bind, KeyValuePair<string, ScAxisValues>>();

            for (int i = 0; i < candidates.Count; i++)
            {
                ScImportCandidate candidate = candidates[i];
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.NodeKey))
                {
                    result.SkippedEntries++;
                    continue;
                }

                string signature = BuildSignature(candidate.Device, candidate.IsAxis, candidate.Input, candidate.Reformers);
                Relation relation;
                Bind bind;

                if (!string.IsNullOrWhiteSpace(candidate.RelationName) &&
                    TryGetCompatibleExistingRelation(candidate.RelationName, candidate, relations, binds, out relation, out bind))
                {
                    MergeCandidateIntoExisting(candidate, relation, bind);
                    result.MergedIntoExisting++;
                    if (ApplyImportedAxisValues(bind, candidate.AxisValues, relation.NAME, candidate.NodeKey, appliedAxisValues, result.Warnings))
                        result.AxisSettingsImported++;
                    if (!signatureToRelation.ContainsKey(signature))
                        signatureToRelation[signature] = relation.NAME;
                    continue;
                }

                string existingBySignature;
                if (signatureToRelation.TryGetValue(signature, out existingBySignature) &&
                    TryGetCompatibleExistingRelation(existingBySignature, candidate, relations, binds, out relation, out bind))
                {
                    MergeCandidateIntoExisting(candidate, relation, bind);
                    result.MergedIntoExisting++;
                    if (ApplyImportedAxisValues(bind, candidate.AxisValues, relation.NAME, candidate.NodeKey, appliedAxisValues, result.Warnings))
                        result.AxisSettingsImported++;
                    continue;
                }

                string relationName = string.IsNullOrWhiteSpace(candidate.RelationName)
                    ? BuildGeneratedRelationName(candidate)
                    : candidate.RelationName.Trim();
                if (relations.ContainsKey(relationName))
                    relationName = BuildUniqueRelationName(relationName, relations);

                relation = new Relation();
                relation.NAME = relationName;
                relation.ISAXIS = candidate.IsAxis;
                relation.Groups = candidate.Groups == null || candidate.Groups.Count == 0
                    ? new List<string> { "GENERATED", "StarCitizen" }
                    : candidate.Groups.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                relation.NODES = new List<RelationItem>();
                relation.AddNode(candidate.NodeKey, StarCitizenGame, candidate.IsAxis, StarCitizenPlane, false);

                bind = new Bind(relation);
                bind.Joystick = candidate.Device;
                bind.JoyBackup = candidate.Device;
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

                bind.AdditionalImportInfo = candidate.Title;
                bind.AllReformers = candidate.Reformers == null ? new List<string>() : new List<string>(candidate.Reformers);
                bind.Curvature = new List<double>(Bind.Curvature_Default);
                bind.SaturationX = Bind.SaturationX_Default;
                bind.SaturationY = Bind.SaturationY_Default;
                bind.Deadzone = Bind.Deadzone_Default;
                bind.Slider = Bind.Slider_Default;
                bind.Inverted = candidate.IsAxis ? (bool?)Bind.Inverted_Default : null;
                bind.AliasJoystick = ResolveAlias(candidate.Device);
                relation.bind = bind;
                // The base keeps JoyPro's defaults; the game's axis settings (option group and device axis) become the
                // relation's Star Citizen tuning.
                if (candidate.IsAxis && ApplyImportedAxisValues(bind, candidate.AxisValues, relation.NAME, candidate.NodeKey, appliedAxisValues, result.Warnings))
                    result.AxisSettingsImported++;

                relations[relationName] = relation;
                binds[relationName] = bind;
                signatureToRelation[signature] = relationName;
                result.ImportedRelations++;
                result.ImportedBinds++;
            }

            InternalDataManagement.AllRelations = relations;
            InternalDataManagement.AllBinds = binds;
            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
        }

        private static List<ScImportCandidate> BuildImportCandidates(
            ScActionFileData data,
            Dictionary<string, ScCatalogAction> catalog,
            Dictionary<string, ScRebindMetadataHints> hints,
            Dictionary<string, string> scToDxKeyboard,
            EngineStarCitizenImportOptions options,
            EngineStarCitizenImportResult result,
            Dictionary<string, ScOptionGroupInfo> optionTree)
        {
            List<ScImportCandidate> output = new List<ScImportCandidate>();
            HashSet<string> matchedDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> selectedFilter = new HashSet<string>(options.DeviceFilter, StringComparer.OrdinalIgnoreCase);
            HashSet<string> selectedFilterByName = BuildGuidlessDeviceFilterSet(selectedFilter);
            HashSet<string> ignoredFilter = new HashSet<string>(options.IgnoreDeviceFilter, StringComparer.OrdinalIgnoreCase);
            HashSet<string> ignoredFilterByName = BuildGuidlessDeviceFilterSet(ignoredFilter);

            Dictionary<int, string> jsInstanceProducts = data.JoystickOptions
                .Where(v => v != null && v.Instance > 0)
                .GroupBy(v => v.Instance)
                .ToDictionary(g => g.Key, g => NormalizeScProductToken(g.First().Product), EqualityComparer<int>.Default);

            for (int i = 0; i < data.Actions.Count; i++)
            {
                ScActionEntry action = data.Actions[i];
                if (action == null)
                    continue;
                result.ActionsParsed++;

                string nodeKey = BuildNodeKey(action.Category, action.Id);
                if (!IsFlightMap(action.Category))
                {
                    result.SkippedEntries += action.Rebinds.Count;
                    continue;
                }
                ScCatalogAction catalogItem = null;
                if (catalog.ContainsKey(nodeKey))
                    catalogItem = catalog[nodeKey];

                bool axis = catalogItem != null ? catalogItem.IsAxis : action.IsAxis;
                string title = !string.IsNullOrWhiteSpace(action.Title)
                    ? action.Title
                    : (catalogItem != null ? catalogItem.Title : action.Id);
                ScRebindMetadataHints actionHints = hints.ContainsKey(nodeKey)
                    ? hints[nodeKey]
                    : new ScRebindMetadataHints();

                for (int r = 0; r < action.Rebinds.Count; r++)
                {
                    ScRebindEntry rebind = action.Rebinds[r];
                    if (rebind == null || string.IsNullOrWhiteSpace(rebind.Input))
                        continue;
                    // "js1_ " / "kb1_ ": a cleared bind (the game's default switched off), not an input.
                    if (System.Text.RegularExpressions.Regex.IsMatch(rebind.Input, "^\\s*[A-Za-z]{2}\\d*_\\s*$"))
                        continue;
                    result.RebindEntriesParsed++;

                    string inputType;
                    int instance;
                    List<string> inputTokens;
                    string parseError;
                    if (!TryParseScInput(rebind.Input, out inputType, out instance, out inputTokens, out parseError))
                    {
                        result.SkippedEntries++;
                        AddUnique(result.Warnings, "Skipping Star Citizen rebind '" + rebind.Input + "' for " + nodeKey + ": " + parseError);
                        continue;
                    }
                    if (inputTokens.Count == 0)
                    {
                        result.SkippedEntries++;
                        continue;
                    }

                    string device = ResolveImportDeviceName(inputType, instance, jsInstanceProducts, data.GamepadProduct, result.Warnings);
                    if (string.IsNullOrWhiteSpace(device))
                    {
                        result.SkippedEntries++;
                        AddUnique(result.Warnings, "Skipping Star Citizen rebind with unresolved device: " + rebind.Input + " (" + nodeKey + ").");
                        continue;
                    }
                    if (string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase) && !options.IncludeKeyboard)
                    {
                        result.CandidatesFilteredByDevice++;
                        continue;
                    }
                    if (selectedFilter.Count > 0 && !MatchesDeviceFilter(device, selectedFilter, selectedFilterByName))
                    {
                        result.CandidatesFilteredByDevice++;
                        continue;
                    }
                    if (ignoredFilter.Count > 0 && MatchesDeviceFilter(device, ignoredFilter, ignoredFilterByName))
                    {
                        result.CandidatesFilteredByDevice++;
                        continue;
                    }
                    matchedDevices.Add(device);

                    string mainRaw = inputTokens[inputTokens.Count - 1];
                    string mainInput;
                    if (!TryConvertScTokenToJoyPro(inputType, mainRaw, scToDxKeyboard, out mainInput))
                    {
                        result.SkippedEntries++;
                        AddUnique(result.Warnings, "Skipping unsupported Star Citizen input token '" + mainRaw + "' for " + nodeKey + ".");
                        continue;
                    }

                    List<string> reformers = new List<string>();
                    for (int t = 0; t < inputTokens.Count - 1; t++)
                    {
                        string modToken = inputTokens[t];
                        string modifierKey;
                        if (!TryConvertScTokenToJoyPro(inputType, modToken, scToDxKeyboard, out modifierKey))
                        {
                            AddUnique(result.Warnings, "Skipping unsupported modifier token '" + modToken + "' for " + nodeKey + ".");
                            continue;
                        }

                        Modifier modifier = new Modifier();
                        modifier.device = device;
                        modifier.name = modifierKey;
                        modifier.key = modifierKey;
                        modifier.sw = false;
                        reformers.Add(modifier.toReformerString());
                    }

                    ScImportCandidate candidate = new ScImportCandidate();
                    candidate.NodeKey = nodeKey;
                    candidate.Category = action.Category;
                    candidate.ActionId = action.Id;
                    candidate.Title = title;
                    candidate.Device = device;
                    candidate.Reformers = reformers;
                    candidate.IsAxis = axis || IsAxisToken(mainInput);
                    candidate.Input = mainInput;
                    if (candidate.IsAxis && inputType == "js")
                        candidate.AxisValues = ReadImportValues(data, optionTree, instance, catalogItem != null ? catalogItem.OptionGroup : string.Empty, mainRaw.ToLowerInvariant());
                    candidate.Groups = new List<string> { "GENERATED", "StarCitizen" };

                    switch (inputType)
                    {
                        case "kb":
                            candidate.RelationName = actionHints.KeyboardRelationName;
                            break;
                        case "gp":
                            candidate.RelationName = actionHints.GamepadRelationName;
                            break;
                        default:
                            candidate.RelationName = actionHints.JoystickRelationName;
                            break;
                    }

                    if (string.IsNullOrWhiteSpace(candidate.RelationName))
                        candidate.RelationName = BuildGeneratedRelationName(candidate);

                    output.Add(candidate);
                    result.CandidatesParsed++;
                }
            }

            result.MatchedDevices = matchedDevices.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            return output;
        }

        private static Dictionary<string, string> BuildProfileProductMap()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (InternalDataManagement.LocalJoystickPGUID == null)
                return map;

            foreach (KeyValuePair<string, string> entry in InternalDataManagement.LocalJoystickPGUID)
            {
                string device = (entry.Key ?? string.Empty).Trim();
                string product = NormalizeScProductToken(entry.Value);
                if (device.Length == 0 || product.Length == 0)
                    continue;
                if (!map.ContainsKey(device))
                    map.Add(device, product);
            }

            return map;
        }

        private static ScSlotPlan BuildJoystickSlotPlan(
            List<ScOptionDevice> localOptions,
            Dictionary<string, int> deviceUsage,
            bool preferLocalOrder,
            List<string> warnings)
        {
            ScSlotPlan plan = new ScSlotPlan();
            plan.Options = new List<ScOptionDevice>();
            plan.DeviceToSlot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            plan.SlotToProduct = new Dictionary<int, string>();

            Dictionary<string, string> profileProductMap = BuildProfileProductMap();
            Dictionary<string, int> usage = deviceUsage ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> connectedStartupOrder = BuildConnectedStartupOrder(warnings);
            List<string> requiredDevices = usage
                .OrderBy(kvp =>
                {
                    if (!preferLocalOrder || connectedStartupOrder.Count == 0)
                        return int.MaxValue;

                    string product = ResolveProductForDevice(kvp.Key, profileProductMap);
                    if (product.Length == 0)
                        product = NormalizeScProductToken(kvp.Key);

                    int rank;
                    return connectedStartupOrder.TryGetValue(product, out rank) ? rank : int.MaxValue;
                })
                .ThenByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => kvp.Key)
                .ToList();

            Dictionary<int, ScOptionDevice> baseSlots = new Dictionary<int, ScOptionDevice>();
            Dictionary<string, int> productToSlot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (preferLocalOrder)
            {
                foreach (KeyValuePair<string, int> entry in connectedStartupOrder
                    .Where(v => !string.IsNullOrWhiteSpace(v.Key) && v.Value > 0 && v.Value <= MaxJoystickSlots)
                    .OrderBy(v => v.Value))
                {
                    if (baseSlots.ContainsKey(entry.Value))
                        continue;

                    ScOptionDevice startupOption = new ScOptionDevice();
                    startupOption.Instance = entry.Value;
                    startupOption.Product = entry.Key;
                    baseSlots[startupOption.Instance] = startupOption;
                    if (!productToSlot.ContainsKey(startupOption.Product))
                        productToSlot[startupOption.Product] = startupOption.Instance;
                }

                for (int i = 0; i < localOptions.Count; i++)
                {
                    ScOptionDevice option = localOptions[i];
                    if (option == null || option.Instance <= 0 || option.Instance > MaxJoystickSlots)
                        continue;

                    ScOptionDevice copy = new ScOptionDevice();
                    copy.Instance = option.Instance;
                    copy.Product = NormalizeScProductToken(option.Product);
                    if (copy.Product.Length == 0)
                        continue;

                    if (productToSlot.ContainsKey(copy.Product))
                        continue;

                    if (baseSlots.ContainsKey(copy.Instance))
                    {
                        int localFallback = NextFreeSlot(baseSlots.Keys.ToList());
                        if (localFallback <= 0 || localFallback > MaxJoystickSlots)
                            continue;
                        copy.Instance = localFallback;
                    }

                    baseSlots[copy.Instance] = copy;
                    if (!productToSlot.ContainsKey(copy.Product))
                        productToSlot[copy.Product] = copy.Instance;
                }
            }

            for (int i = 0; i < requiredDevices.Count; i++)
            {
                string device = requiredDevices[i];
                string product = ResolveProductForDevice(device, profileProductMap);
                int slot;

                if (product.Length > 0 && productToSlot.TryGetValue(product, out slot))
                {
                    plan.DeviceToSlot[device] = slot;
                    continue;
                }

                slot = NextFreeSlot(baseSlots.Keys.ToList());
                if (slot <= 0 || slot > MaxJoystickSlots)
                {
                    AddUnique(warnings, "Star Citizen export: device '" + device + "' has no free joystick slot (max " + MaxJoystickSlots + ").");
                    continue;
                }

                if (product.Length == 0)
                    product = NormalizeScProductToken(device);
                if (product.Length == 0)
                    product = "Unknown Device {" + Guid.Empty.ToString().ToUpperInvariant() + "}";

                ScOptionDevice created = new ScOptionDevice();
                created.Instance = slot;
                created.Product = product;
                baseSlots[slot] = created;
                productToSlot[product] = slot;
                plan.DeviceToSlot[device] = slot;
            }

            if (!preferLocalOrder)
            {
                baseSlots.Clear();
                productToSlot.Clear();
                int next = 1;
                for (int i = 0; i < requiredDevices.Count; i++)
                {
                    string device = requiredDevices[i];
                    string product = ResolveProductForDevice(device, profileProductMap);
                    if (product.Length == 0)
                        product = NormalizeScProductToken(device);
                    if (product.Length == 0)
                        continue;

                    int existingSlot;
                    if (productToSlot.TryGetValue(product, out existingSlot))
                    {
                        plan.DeviceToSlot[device] = existingSlot;
                        continue;
                    }

                    if (next > MaxJoystickSlots)
                    {
                        AddUnique(warnings, "Star Citizen export: device '" + device + "' exceeds joystick slot limit (" + MaxJoystickSlots + ").");
                        continue;
                    }

                    ScOptionDevice option = new ScOptionDevice();
                    option.Instance = next;
                    option.Product = product;
                    baseSlots[next] = option;
                    productToSlot[product] = next;
                    plan.DeviceToSlot[device] = next;
                    next++;
                }
            }

            plan.Options = baseSlots.Values
                .OrderBy(v => v.Instance)
                .Take(MaxJoystickSlots)
                .ToList();
            for (int i = 0; i < plan.Options.Count; i++)
                plan.SlotToProduct[plan.Options[i].Instance] = plan.Options[i].Product;
            return plan;
        }

        private static Dictionary<string, int> BuildConnectedStartupOrder(List<string> warnings)
        {
            Dictionary<string, int> order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<string> localWarnings = new List<string>();
            List<string> connected = DirectInputDeviceDiscovery.EnumerateConnectedDevicesInOrder(localWarnings);

            for (int i = 0; i < localWarnings.Count; i++)
                AddUnique(warnings, localWarnings[i]);

            int rank = 1;
            for (int i = 0; i < connected.Count; i++)
            {
                string product = NormalizeScProductToken(connected[i]);
                if (product.Length == 0 || order.ContainsKey(product))
                    continue;

                order[product] = rank;
                rank++;
            }

            return order;
        }

        private static bool TryConvertProfileBindToScInput(
            ScBindSource source,
            ScSlotPlan slotPlan,
            List<string> warnings,
            out string convertedInput)
        {
            convertedInput = string.Empty;
            if (source == null || string.IsNullOrWhiteSpace(source.Input))
                return false;

            Dictionary<string, string> scToDx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> dxToSc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            LoadKeyboardConversion(scToDx, dxToSc, warnings);
            _ = scToDx;

            if (source.InputType == "kb")
            {
                List<string> tokens = new List<string>();
                for (int i = 0; i < source.Reformers.Count; i++)
                {
                    Modifier mod = Modifier.ReformerToMod(source.Reformers[i]);
                    if (mod == null || string.IsNullOrWhiteSpace(mod.key))
                        continue;
                    string modToken;
                    if (TryConvertJoyProTokenToSc("kb", mod.key, dxToSc, out modToken))
                        tokens.Add(modToken);
                }
                string mainToken;
                if (!TryConvertJoyProTokenToSc("kb", source.Input, dxToSc, out mainToken))
                    return false;
                tokens.Add(mainToken);
                convertedInput = "kb1_" + string.Join("+", tokens);
                return true;
            }

            if (source.InputType == "gp")
            {
                List<string> tokens = new List<string>();
                for (int i = 0; i < source.Reformers.Count; i++)
                {
                    Modifier mod = Modifier.ReformerToMod(source.Reformers[i]);
                    if (mod == null || string.IsNullOrWhiteSpace(mod.key))
                        continue;
                    string modToken;
                    if (TryConvertJoyProTokenToSc("gp", mod.key, dxToSc, out modToken))
                        tokens.Add(modToken);
                }
                string mainToken;
                if (!TryConvertJoyProTokenToSc("gp", source.Input, dxToSc, out mainToken))
                    return false;
                tokens.Add(mainToken);
                convertedInput = "gp1_" + string.Join("+", tokens);
                return true;
            }

            int slot;
            if (!slotPlan.DeviceToSlot.TryGetValue(source.Device, out slot))
            {
                AddUnique(warnings, "Star Citizen export: no joystick slot assigned for device '" + source.Device + "'.");
                return false;
            }

            string jsToken;
            if (!TryConvertJoyProTokenToSc("js", source.Input, dxToSc, out jsToken))
                return false;

            convertedInput = "js" + slot.ToString(CultureInfo.InvariantCulture) + "_" + jsToken;
            return true;
        }


        private static ScActionFileData ReadActionFile(string path)
        {
            ScActionFileData data = new ScActionFileData();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return data;

            XDocument doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            XElement root = doc.Root;
            if (root == null)
                return data;

            XElement actionProfiles = root.Name.LocalName.Equals("ActionProfiles", StringComparison.OrdinalIgnoreCase)
                ? root
                : root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("ActionProfiles", StringComparison.OrdinalIgnoreCase));
            if (actionProfiles == null)
                return data;

            data.Version = (string)actionProfiles.Attribute("version") ?? "1";
            data.OptionsVersion = (string)actionProfiles.Attribute("optionsVersion") ?? "2";
            data.RebindVersion = (string)actionProfiles.Attribute("rebindVersion") ?? "2";
            data.ProfileName = (string)actionProfiles.Attribute("profileName") ?? "default";

            foreach (XElement option in actionProfiles.Elements().Where(e => e.Name.LocalName.Equals("options", StringComparison.OrdinalIgnoreCase)))
            {
                string type = ((string)option.Attribute("type") ?? string.Empty).Trim().ToLowerInvariant();
                string product = NormalizeScProductToken((string)option.Attribute("Product") ?? string.Empty);
                int instance = ParseInt((string)option.Attribute("instance"), 0);
                if (type == "keyboard")
                {
                    data.KeyboardProduct = product;
                }
                else if (type == "gamepad")
                {
                    data.GamepadProduct = product;
                }
                else if (type == "joystick" && instance > 0)
                {
                    ScOptionDevice item = new ScOptionDevice();
                    item.Instance = instance;
                    item.Product = product;
                    data.JoystickOptions.Add(item);
                    if (!data.JoystickOptionElements.ContainsKey(instance))
                        data.JoystickOptionElements[instance] = option;
                }
            }
            // Dead zone / saturation per device axis (placement in the 4.x file not observed: read wherever it is).
            data.DeviceOptions = root.Descendants().Where(e => e.Name.LocalName.Equals("deviceoptions", StringComparison.OrdinalIgnoreCase)).ToList();
            XElement joystickTree = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("optiontree", StringComparison.OrdinalIgnoreCase) &&
                                                                          string.Equals((string)e.Attribute("type"), "joystick", StringComparison.OrdinalIgnoreCase));
            if (joystickTree != null)
                data.OptionTree = ParseOptionTree(joystickTree);

            int order = 0;
            foreach (XElement actionMap in actionProfiles.Elements().Where(e => e.Name.LocalName.Equals("actionmap", StringComparison.OrdinalIgnoreCase)))
            {
                string category = ((string)actionMap.Attribute("name") ?? string.Empty).Trim();
                foreach (XElement action in actionMap.Elements().Where(e => e.Name.LocalName.Equals("action", StringComparison.OrdinalIgnoreCase)))
                {
                    string id = ((string)action.Attribute("name") ?? string.Empty).Trim();
                    if (id.Length == 0)
                        continue;

                    ScActionEntry entry = new ScActionEntry();
                    entry.Category = category;
                    entry.Id = id;
                    entry.Order = order++;
                    string title;
                    bool? axis;
                    ParseActionComment(action, out title, out axis);
                    entry.Title = string.IsNullOrWhiteSpace(title) ? id : title;
                    entry.IsAxis = axis == true;
                    // Catalog: <optionGroup name="..."/> after the comment; the game's defaultProfile: optionGroup="...".
                    entry.OptionGroup = ((string)action.Attribute("optionGroup") ?? string.Empty).Trim();
                    XElement groupElement = action.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("optionGroup", StringComparison.OrdinalIgnoreCase));
                    if (entry.OptionGroup.Length == 0 && groupElement != null)
                        entry.OptionGroup = ((string)groupElement.Attribute("name") ?? string.Empty).Trim();
                    // Catalog: <defaultJoystick input="y"/>; the game's defaultProfile: joystick="y".
                    XElement defaultElement = action.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("defaultJoystick", StringComparison.OrdinalIgnoreCase));
                    entry.DefaultJoystick = defaultElement != null
                        ? ((string)defaultElement.Attribute("input") ?? string.Empty).Trim()
                        : ((string)action.Attribute("joystick") ?? string.Empty).Trim();
                    if (entry.DefaultJoystick == "0" || entry.DefaultJoystick == "1")
                        entry.DefaultJoystick = string.Empty;

                    foreach (XElement rebind in action.Elements().Where(e => e.Name.LocalName.Equals("rebind", StringComparison.OrdinalIgnoreCase)))
                    {
                        string input = ((string)rebind.Attribute("input") ?? string.Empty).Trim();
                        if (input.Length == 0)
                            continue;
                        ScRebindEntry r = new ScRebindEntry();
                        r.Input = input;
                        r.DefaultInput = ((string)rebind.Attribute("defaultInput") ?? string.Empty).Trim();
                        entry.Rebinds.Add(r);
                    }

                    data.Actions.Add(entry);
                }
            }

            data.JoystickOptions = data.JoystickOptions
                .OrderBy(v => v.Instance)
                .GroupBy(v => v.Instance)
                .Select(g => g.First())
                .ToList();
            return data;
        }

        private static bool TryReadActionFile(string path, List<string> warnings, out ScActionFileData data)
        {
            data = new ScActionFileData();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            try
            {
                data = ReadActionFile(path);
                return true;
            }
            catch (Exception ex)
            {
                AddUnique(warnings, "Star Citizen action file parse failed ('" + path + "'): " + ex.Message);
                return false;
            }
        }


        private static Dictionary<string, ScCatalogAction> LoadCatalog(List<string> warnings, out Dictionary<string, ScOptionGroupInfo> optionTree)
        {
            optionTree = BuiltInOptionTree();
            Dictionary<string, ScCatalogAction> output = new Dictionary<string, ScCatalogAction>(StringComparer.OrdinalIgnoreCase);
            string scRoot = GameDataPaths.DbFolder("StarCitizen");
            string catalogPath = Path.Combine(scRoot, "AllBinds.xml");
            if (!File.Exists(catalogPath))
                catalogPath = Path.Combine(scRoot, "actionmaps.xml");
            // A fresh install has no list yet: built from the installed game first.
            if (!File.Exists(catalogPath) && Program.BuildStarCitizenDbOnDemand(warnings))
            {
                scRoot = GameDataPaths.DbFolder("StarCitizen");
                catalogPath = Path.Combine(scRoot, "AllBinds.xml");
                if (!File.Exists(catalogPath))
                    catalogPath = Path.Combine(scRoot, "actionmaps.xml");
            }
            if (!File.Exists(catalogPath))
            {
                AddUnique(warnings, "Star Citizen DB catalog not found at '" + Path.Combine(scRoot, "AllBinds.xml") + "'.");
                return output;
            }

            ScActionFileData parsed;
            if (!TryReadActionFile(catalogPath, warnings, out parsed))
                return output;
            if (parsed.OptionTree != null && parsed.OptionTree.Count > 0)
                optionTree = parsed.OptionTree;

            for (int i = 0; i < parsed.Actions.Count; i++)
            {
                ScActionEntry action = parsed.Actions[i];
                if (action == null)
                    continue;

                ScCatalogAction item = new ScCatalogAction();
                item.Category = action.Category;
                item.Id = action.Id;
                item.Title = string.IsNullOrWhiteSpace(action.Title) ? action.Id : action.Title;
                item.IsAxis = action.IsAxis;
                item.Order = action.Order;
                item.OptionGroup = action.OptionGroup ?? string.Empty;
                item.DefaultJoystick = action.DefaultJoystick ?? string.Empty;
                item.DefaultKeyboardInput = action.Rebinds
                    .Where(r => r != null && !string.IsNullOrWhiteSpace(r.DefaultInput))
                    .Select(r => r.DefaultInput.Trim())
                    .FirstOrDefault(v => v.StartsWith("kb", StringComparison.OrdinalIgnoreCase));
                output[BuildNodeKey(action.Category, action.Id)] = item;
            }

            return output;
        }

        private static Dictionary<string, ScBindingState> BuildStateFromCatalog(Dictionary<string, ScCatalogAction> catalog)
        {
            Dictionary<string, ScBindingState> output = new Dictionary<string, ScBindingState>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, ScCatalogAction> entry in catalog)
            {
                ScCatalogAction action = entry.Value;
                ScBindingState state = new ScBindingState();
                state.Category = action.Category;
                state.Id = action.Id;
                state.Title = action.Title;
                state.IsAxis = action.IsAxis;
                state.Order = action.Order;
                state.OptionGroup = action.OptionGroup ?? string.Empty;
                output[entry.Key] = state;
            }
            return output;
        }

        private static Dictionary<string, ScRebindMetadataHints> ParseRebindMetadataHints(string metadataPath, List<string> warnings)
        {
            Dictionary<string, ScRebindMetadataHints> output = new Dictionary<string, ScRebindMetadataHints>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(metadataPath) || !File.Exists(metadataPath))
                return output;

            try
            {
                string currentCategory = string.Empty;
                string currentAction = string.Empty;
                string[] lines = File.ReadAllLines(metadataPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i] ?? string.Empty;

                    if (line.IndexOf("<actionmap", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        currentCategory = ParseXmlAttributeInline(line, "name");
                        continue;
                    }
                    if (line.IndexOf("<action ", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        currentAction = ParseXmlAttributeInline(line, "name");
                        continue;
                    }
                    if (line.IndexOf("<rebind", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    string key = BuildNodeKey(currentCategory, currentAction);
                    if (key.Length == 0)
                        continue;
                    string input = ParseXmlAttributeInline(line, "input");
                    if (input.Length == 0)
                        continue;

                    int commentStart = line.IndexOf("<!--", StringComparison.OrdinalIgnoreCase);
                    int commentEnd = line.IndexOf("-->", StringComparison.OrdinalIgnoreCase);
                    if (commentStart < 0 || commentEnd <= commentStart)
                        continue;
                    string comment = line.Substring(commentStart + 4, commentEnd - (commentStart + 4)).Trim();
                    if (comment.Length == 0)
                        continue;

                    string relationName = comment;
                    int split = comment.IndexOf("//", StringComparison.Ordinal);
                    if (split >= 0)
                        relationName = comment.Substring(0, split).Trim();
                    if (relationName.Length == 0)
                        continue;

                    ScRebindMetadataHints hints;
                    if (!output.TryGetValue(key, out hints))
                    {
                        hints = new ScRebindMetadataHints();
                        output[key] = hints;
                    }

                    string lower = input.ToLowerInvariant();
                    if (lower.StartsWith("kb", StringComparison.Ordinal))
                        hints.KeyboardRelationName = relationName;
                    else if (lower.StartsWith("gp", StringComparison.Ordinal))
                        hints.GamepadRelationName = relationName;
                    else if (lower.StartsWith("js", StringComparison.Ordinal))
                        hints.JoystickRelationName = relationName;
                }
            }
            catch (Exception ex)
            {
                AddUnique(warnings, "Could not parse Star Citizen metadata file '" + metadataPath + "': " + ex.Message);
            }

            return output;
        }

        private static bool TryConvertScTokenToJoyPro(
            string inputType,
            string token,
            Dictionary<string, string> scToDxKeyboard,
            out string output)
        {
            output = string.Empty;
            string value = (token ?? string.Empty).Trim();
            if (value.Length == 0)
                return false;

            if (inputType == "kb")
            {
                string mapped;
                if (scToDxKeyboard != null &&
                    scToDxKeyboard.TryGetValue(value, out mapped) &&
                    !string.IsNullOrWhiteSpace(mapped))
                {
                    output = mapped.Trim();
                    return true;
                }
                output = value;
                return true;
            }

            if (inputType == "gp")
            {
                output = SC2JPGPKey(value);
                return output.Length > 0;
            }

            if (inputType == "js")
            {
                output = SC2JPJoyKey(value);
                return output.Length > 0;
            }

            return false;
        }

        private static bool TryConvertJoyProTokenToSc(
            string inputType,
            string token,
            Dictionary<string, string> dxToScKeyboard,
            out string output)
        {
            output = string.Empty;
            string value = (token ?? string.Empty).Trim();
            if (value.Length == 0)
                return false;

            if (inputType == "kb")
            {
                string mapped;
                if (dxToScKeyboard != null &&
                    dxToScKeyboard.TryGetValue(value, out mapped) &&
                    !string.IsNullOrWhiteSpace(mapped))
                {
                    output = mapped.Trim().ToLowerInvariant();
                    return true;
                }
                output = value.ToLowerInvariant();
                return true;
            }

            if (inputType == "gp")
            {
                output = JP2SCGPKey(value);
                return output.Length > 0;
            }

            if (inputType == "js")
            {
                output = JP2SCJoyKey(value);
                return output.Length > 0;
            }

            return false;
        }

        private static bool TryParseScInput(
            string raw,
            out string inputType,
            out int instance,
            out List<string> tokens,
            out string error)
        {
            inputType = string.Empty;
            instance = 0;
            tokens = new List<string>();
            error = string.Empty;

            string value = (raw ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                error = "empty input value";
                return false;
            }

            int split = value.IndexOf('_');
            if (split <= 0 || split >= value.Length - 1)
            {
                error = "missing prefix separator";
                return false;
            }

            string prefix = value.Substring(0, split).Trim().ToLowerInvariant();
            string body = value.Substring(split + 1).Trim();
            if (body.Length == 0 || body == "_")
            {
                error = "empty input body";
                return false;
            }

            if (prefix.Length < 2)
            {
                error = "invalid prefix";
                return false;
            }

            string type = prefix.Substring(0, 2);
            if (type != "kb" && type != "js" && type != "gp")
            {
                error = "unsupported input type '" + type + "'";
                return false;
            }

            string instanceRaw = prefix.Length > 2 ? prefix.Substring(2) : "1";
            if (!int.TryParse(instanceRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out instance))
                instance = 1;
            if (instance <= 0)
                instance = 1;

            string[] splitTokens = body.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < splitTokens.Length; i++)
            {
                string token = splitTokens[i].Trim();
                if (token.Length > 0)
                    tokens.Add(token);
            }

            inputType = type;
            return tokens.Count > 0;
        }

        private static string ResolveImportDeviceName(
            string inputType,
            int instance,
            Dictionary<int, string> jsInstanceProducts,
            string gamepadProduct,
            List<string> warnings)
        {
            if (inputType == "kb")
                return "Keyboard";

            if (inputType == "gp")
                return ResolveGamepadDeviceName(gamepadProduct);

            string product;
            if (!jsInstanceProducts.TryGetValue(instance, out product))
            {
                AddUnique(warnings, "Star Citizen joystick instance js" + instance.ToString(CultureInfo.InvariantCulture) + " missing options Product entry.");
                return "Joystick " + instance.ToString(CultureInfo.InvariantCulture);
            }

            // Earlier slots with the same product (two identical sticks): this one is the next of them.
            int occurrence = jsInstanceProducts.Count(p => p.Key < instance && string.Equals(NormalizeScProductToken(p.Value), NormalizeScProductToken(product), StringComparison.OrdinalIgnoreCase));
            return ResolveDeviceNameFromProduct(product, warnings, occurrence);
        }

        private static string ResolveGamepadDeviceName(string gamepadProduct)
        {
            string value = NormalizeScProductToken(gamepadProduct);
            if (value.Length == 0)
                value = "Controller (Gamepad)";
            return value;
        }

        private static string ResolveProductForDevice(string device, Dictionary<string, string> profileProductMap)
        {
            string normalizedDevice = (device ?? string.Empty).Trim();
            if (normalizedDevice.Length == 0)
                return string.Empty;

            string product;
            if (profileProductMap != null &&
                profileProductMap.TryGetValue(normalizedDevice, out product) &&
                !string.IsNullOrWhiteSpace(product))
            {
                return NormalizeScProductToken(product);
            }

            if (LooksLikeDeviceWithGuid(normalizedDevice))
                return NormalizeScProductToken(normalizedDevice.Replace(" {", "  {").Trim());

            return NormalizeScProductToken(normalizedDevice);
        }

        private static Dictionary<string, string> connectedProducts;
        // The same pairs (instance name, product name) in DirectInput order: two devices of one product are told apart by it.
        private static List<KeyValuePair<string, string>> connectedInOrder = new List<KeyValuePair<string, string>>();

        // The name the game gives a slot: its DirectInput product ("vJoy Device  {BEAD1234-0000-0000-0000-504944564944}",
        // product GUID = PID, VID, "PIDVID"), not JoyPro's instance name ("vJoy Device {89D63500-...}"). A slot written with
        // the instance name is not recognized: the game rewrites it with its own product and drops the slot's settings and
        // deviceoptions (found in game 2026-10-10). Slot planning keeps the instance names; only what goes into the file
        // is converted (connected devices; anything else is written as it is).
        internal static string ScProductForFile(string product)
        {
            string normalized = NormalizeScProductToken(product);
            if (normalized.Length == 0)
                return product;
            string connected = ConnectedProductForInstance(normalized);
            if (connected.Length == 0)
            {
                foreach (KeyValuePair<string, string> pair in ConnectedProductsByInstance())
                {
                    if (NormalizeScProductToken(pair.Key) == normalized)
                    {
                        connected = pair.Value;
                        break;
                    }
                }
            }
            return connected.Length > 0 ? connected.Replace(" {", "  {").Trim() : product;
        }

        private static Dictionary<string, string> ConnectedProductsByInstance()
        {
            ConnectedProductForInstance(string.Empty);
            return connectedProducts;
        }

        // DirectInput instance name -> product name of the connected devices (enumerated once per process).
        private static string ConnectedProductForInstance(string device)
        {
            if (connectedProducts == null)
            {
                connectedProducts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (DirectInputDeviceDiscovery.DirectInputDeviceInfo info in DirectInputDeviceDiscovery.EnumerateConnectedDeviceInfos(new List<string>()))
                    {
                        if (!string.IsNullOrWhiteSpace(info.InstanceToken) && !string.IsNullOrWhiteSpace(info.ProductToken) && !connectedProducts.ContainsKey(info.InstanceToken.Trim()))
                        {
                            connectedProducts[info.InstanceToken.Trim()] = info.ProductToken.Trim();
                            connectedInOrder.Add(new KeyValuePair<string, string>(info.InstanceToken.Trim(), info.ProductToken.Trim()));
                        }
                    }
                }
                catch
                {
                    // No DirectInput (tests on fixtures): the instance name stays the fallback.
                }
            }
            string product;
            return connectedProducts.TryGetValue(device, out product) ? product : string.Empty;
        }

        // The game (and JoyPro's export, see ScProductForFile) names a slot by its DirectInput product: back to the
        // connected device's instance name, which the profile uses. Two connected devices of one product: the
        // occurrence-th of them in DirectInput order (the n-th slot of that product). "" when none is connected.
        private static string ConnectedInstanceForProduct(string product, int occurrence)
        {
            ConnectedProductForInstance(string.Empty);
            string normalized = NormalizeScProductToken(product);
            string guid = ExtractGuidToken(normalized);
            List<string> matches = connectedInOrder
                .Where(p => (guid.Length > 0 && string.Equals(ExtractGuidToken(NormalizeScProductToken(p.Value)), guid, StringComparison.OrdinalIgnoreCase)) ||
                            string.Equals(NormalizeScProductToken(p.Value), normalized, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Key)
                .ToList();
            return matches.Count == 0 ? string.Empty : matches[Math.Max(0, Math.Min(occurrence, matches.Count - 1))];
        }

        private static string ResolveDeviceNameFromProduct(string product, List<string> warnings, int occurrence = 0)
        {
            string normalizedProduct = NormalizeScProductToken(product);
            if (normalizedProduct.Length == 0)
                return string.Empty;

            string connectedInstance = ConnectedInstanceForProduct(normalizedProduct, occurrence);
            if (connectedInstance.Length > 0)
                return connectedInstance;

            string productGuid = ExtractGuidToken(normalizedProduct);
            string productBase = StripDeviceGuidSuffix(normalizedProduct);

            List<string> knownDevices = BuildKnownDevicesForProductResolution(warnings);
            for (int i = 0; i < knownDevices.Count; i++)
            {
                string known = knownDevices[i];
                string knownGuid = ExtractGuidToken(known);
                if (productGuid.Length > 0 &&
                    knownGuid.Length > 0 &&
                    string.Equals(productGuid, knownGuid, StringComparison.OrdinalIgnoreCase))
                {
                    return known;
                }
            }

            for (int i = 0; i < knownDevices.Count; i++)
            {
                string known = knownDevices[i];
                string knownBase = StripDeviceGuidSuffix(known);
                if (string.Equals(knownBase, productBase, StringComparison.OrdinalIgnoreCase))
                    return known;
            }

            return normalizedProduct;
        }

        private static List<string> BuildKnownDevicesForProductResolution(List<string> warnings)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (InternalDataManagement.LocalJoystickPGUID != null)
            {
                foreach (KeyValuePair<string, string> entry in InternalDataManagement.LocalJoystickPGUID)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Key))
                        output.Add(entry.Key.Trim());
                }
            }

            if (InternalDataManagement.LocalJoysticks != null)
            {
                for (int i = 0; i < InternalDataManagement.LocalJoysticks.Length; i++)
                {
                    string value = (InternalDataManagement.LocalJoysticks[i] ?? string.Empty).Trim();
                    if (value.Length > 0)
                        output.Add(value);
                }
            }

            List<string> connectedWarnings = new List<string>();
            List<string> connected = DirectInputDeviceDiscovery.EnumerateConnectedDevices(connectedWarnings);
            for (int i = 0; i < connectedWarnings.Count; i++)
                AddUnique(warnings, connectedWarnings[i]);
            for (int i = 0; i < connected.Count; i++)
            {
                string value = (connected[i] ?? string.Empty).Trim();
                if (value.Length > 0)
                    output.Add(value);
            }

            List<string> productOrdered = DirectInputDeviceDiscovery.EnumerateConnectedProductsInOrder(connectedWarnings);
            for (int i = 0; i < connectedWarnings.Count; i++)
                AddUnique(warnings, connectedWarnings[i]);
            for (int i = 0; i < productOrdered.Count; i++)
            {
                string value = (productOrdered[i] ?? string.Empty).Trim();
                if (value.Length > 0)
                    output.Add(value);
            }

            return output.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }



        private static bool WriteTextFile(string path, string content, EngineStarCitizenExportResult result)
        {
            try
            {
                string fullPath = NormalizePath(path);
                string parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);
                GameExportSafety.BackupBeforeOverwrite(fullPath, result.Warnings);
                File.WriteAllText(fullPath, content ?? string.Empty, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                result.Errors.Add("Could not write Star Citizen file '" + (path ?? string.Empty) + "': " + ex.Message);
                return false;
            }
        }

        private static ScProfilePaths ResolveProfilePaths(
            string rootOrPath,
            string sourcePath,
            bool createIfMissing,
            List<string> warnings)
        {
            ScProfilePaths result = new ScProfilePaths();
            string source = NormalizePath(sourcePath);
            string input = NormalizePath(rootOrPath);

            if (string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(input))
            {
                List<string> discoveryWarnings = new List<string>();
                GamePathEntry discovered = GamePathDiscovery.DiscoverStarCitizen(discoveryWarnings);
                for (int i = 0; i < discoveryWarnings.Count; i++)
                    AddUnique(warnings, discoveryWarnings[i]);

                if (!string.IsNullOrWhiteSpace(discovered.PreferredConfigPath))
                    input = NormalizePath(discovered.PreferredConfigPath);
                else if (!string.IsNullOrWhiteSpace(discovered.PreferredInstallPath))
                    input = NormalizePath(discovered.PreferredInstallPath);
            }

            string profileDir = ResolveProfileDirectoryCandidate(source, warnings);
            if (string.IsNullOrWhiteSpace(profileDir))
                profileDir = ResolveProfileDirectoryCandidate(input, warnings);

            if (string.IsNullOrWhiteSpace(profileDir))
            {
                result.IsValid = false;
                result.RootPath = input ?? string.Empty;
                result.ProfileDirectory = string.Empty;
                result.ActionmapsFile = string.Empty;
                result.MetadataFile = string.Empty;
                return result;
            }

            if (createIfMissing)
            {
                try
                {
                    Directory.CreateDirectory(profileDir);
                }
                catch (Exception ex)
                {
                    AddUnique(warnings, "Could not create Star Citizen profile directory '" + profileDir + "': " + ex.Message);
                }
            }

            result.IsValid = true;
            result.RootPath = input ?? profileDir;
            result.ProfileDirectory = profileDir;
            result.ActionmapsFile = Path.Combine(profileDir, "actionmaps.xml");
            result.MetadataFile = Path.Combine(profileDir, "actionmaps.jp");
            return result;
        }

        // The actionmaps.xml an install root, profile folder or file points to (empty: discovery); empty when none.
        internal static string ResolveActionmapsFile(string rootOrPath, List<string> warnings)
        {
            ScProfilePaths paths = ResolveProfilePaths(rootOrPath, null, false, warnings ?? new List<string>());
            return paths.IsValid ? paths.ActionmapsFile : string.Empty;
        }

        private static string ResolveProfileDirectoryCandidate(string path, List<string> warnings)
        {
            string normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            if (File.Exists(normalized))
            {
                string fileName = Path.GetFileName(normalized) ?? string.Empty;
                if (fileName.Equals("actionmaps.xml", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("actionmaps.jp", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetDirectoryName(normalized) ?? string.Empty;
                }
                AddUnique(warnings, "Star Citizen path points to unsupported file: " + normalized);
                return string.Empty;
            }

            if (!Directory.Exists(normalized))
                return string.Empty;

            string directActionmaps = Path.Combine(normalized, "actionmaps.xml");
            if (File.Exists(directActionmaps))
                return normalized;

            string defaultProfile = Path.Combine(normalized, "default");
            if (File.Exists(Path.Combine(defaultProfile, "actionmaps.xml")))
                return defaultProfile;

            string installDefault = Path.Combine(normalized, "LIVE", "USER", "Client", "0", "Profiles", "default");
            if (File.Exists(Path.Combine(installDefault, "actionmaps.xml")) || Directory.Exists(installDefault))
                return installDefault;

            string[] branches;
            try
            {
                branches = Directory.GetDirectories(normalized);
            }
            catch
            {
                return string.Empty;
            }

            for (int i = 0; i < branches.Length; i++)
            {
                string branchDefault = Path.Combine(branches[i], "USER", "Client", "0", "Profiles", "default");
                if (File.Exists(Path.Combine(branchDefault, "actionmaps.xml")) || Directory.Exists(branchDefault))
                    return branchDefault;
            }

            return string.Empty;
        }

        private static bool TryParseExportMode(string modeRaw, out StarCitizenExportMode mode)
        {
            string normalized = (modeRaw ?? "merge-overwrite").Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "clean":
                    mode = StarCitizenExportMode.Clean;
                    return true;
                case "add":
                    mode = StarCitizenExportMode.Add;
                    return true;
                case "merge":
                    mode = StarCitizenExportMode.Merge;
                    return true;
                case "merge-overwrite":
                case "mergeoverwrite":
                case "overwrite":
                    mode = StarCitizenExportMode.MergeOverwrite;
                    return true;
                default:
                    mode = StarCitizenExportMode.MergeOverwrite;
                    return false;
            }
        }

        private static string ExportModeToString(StarCitizenExportMode mode)
        {
            switch (mode)
            {
                case StarCitizenExportMode.Clean:
                    return "clean";
                case StarCitizenExportMode.Add:
                    return "add";
                case StarCitizenExportMode.Merge:
                    return "merge";
                default:
                    return "merge-overwrite";
            }
        }

        private static List<ScBindSource> CollectProfileBindings(EngineStarCitizenExportResult result)
        {
            List<ScBindSource> output = new List<ScBindSource>();
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

                for (int n = 0; n < relation.NODES.Count; n++)
                {
                    RelationItem node = relation.NODES[n];
                    if (node == null)
                        continue;
                    if (!string.Equals(NormalizeGame(node.Game), StarCitizenGame, StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.NodesScanned++;
                    string nodeKey = NormalizeNodeKey(node.ID);
                    if (string.IsNullOrWhiteSpace(nodeKey))
                    {
                        result.SkippedEntries++;
                        continue;
                    }

                    ScBindSource source = new ScBindSource();
                    source.NodeKey = nodeKey;
                    source.RelationName = relation.NAME;
                    source.Title = string.IsNullOrWhiteSpace(bind.AdditionalImportInfo) ? relation.NAME : bind.AdditionalImportInfo;
                    source.IsAxis = relation.ISAXIS;
                    source.Device = bind.Joystick;
                    source.Input = input;
                    source.Reformers = bind.AllReformers == null ? new List<string>() : new List<string>(bind.AllReformers);
                    source.InputType = ResolveInputTypeForDevice(bind.Joystick);
                    source.IsGamepad = source.InputType == "gp";
                    source.Bind = bind;
                    output.Add(source);
                }
            }
            return output;
        }

        private static string ResolveInputTypeForDevice(string device)
        {
            string token = (device ?? string.Empty).Trim();
            if (token.Length == 0)
                return "js";
            if (string.Equals(token, "Keyboard", StringComparison.OrdinalIgnoreCase))
                return "kb";
            if (IsLikelyGamepadDevice(token))
                return "gp";
            return "js";
        }

        private static bool IsLikelyGamepadDevice(string name)
        {
            string upper = (name ?? string.Empty).Trim().ToUpperInvariant();
            if (upper.Length == 0)
                return false;
            return upper.Contains("GAMEPAD") ||
                   upper.Contains("XBOX") ||
                   upper.Contains("DUALSHOCK") ||
                   upper.Contains("DUALSENSE") ||
                   upper.Contains("CONTROLLER");
        }




        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string trimmed = path.Trim();
            try
            {
                return Path.GetFullPath(trimmed);
            }
            catch
            {
                return trimmed;
            }
        }

        private static string Safe(string value)
        {
            return value ?? string.Empty;
        }

        private static List<string> NormalizeStringList(IEnumerable<string> values)
        {
            return (values ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void ReportStatus(EngineStarCitizenImportOptions options, string message)
        {
            if (options == null || options.StatusSink == null || string.IsNullOrWhiteSpace(message))
                return;
            options.StatusSink(message);
        }

        private static void ReportStatus(EngineStarCitizenExportOptions options, string message)
        {
            if (options == null || options.StatusSink == null || string.IsNullOrWhiteSpace(message))
                return;
            options.StatusSink(message);
        }

        private static void AddUnique(List<string> target, string value)
        {
            if (target == null || string.IsNullOrWhiteSpace(value))
                return;
            if (!target.Any(v => string.Equals(v, value, StringComparison.Ordinal)))
                target.Add(value);
        }

        private static void AddRangeUnique(List<string> target, IEnumerable<string> values)
        {
            if (target == null || values == null)
                return;

            foreach (string value in values)
                AddUnique(target, value);
        }

        private static HashSet<string> BuildGuidlessDeviceFilterSet(IEnumerable<string> filters)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in filters ?? new List<string>())
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

        private static string BuildGeneratedRelationName(ScImportCandidate candidate)
        {
            if (candidate == null)
                return "SC_Relation";

            string action = SanitizeName(string.IsNullOrWhiteSpace(candidate.Title) ? candidate.ActionId : candidate.Title);
            string device = SanitizeName(candidate.Device);
            string input = SanitizeName(candidate.Input);
            string category = SanitizeName(candidate.Category);
            if (action.Length == 0)
                action = "Action";
            if (device.Length == 0)
                device = "Device";
            if (input.Length == 0)
                input = "Input";
            if (category.Length == 0)
                category = "Category";
            return "SC__" + category + "__" + action + "__" + device + "__" + input;
        }

        private static string SanitizeName(string value)
        {
            string source = (value ?? string.Empty).Trim();
            if (source.Length == 0)
                return string.Empty;

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
                else
                    sb.Append('_');
            }

            string normalized = sb.ToString();
            while (normalized.Contains("__"))
                normalized = normalized.Replace("__", "_");
            return normalized.Trim('_');
        }

        private static string BuildUniqueRelationName(string baseName, Dictionary<string, Relation> existing)
        {
            string root = string.IsNullOrWhiteSpace(baseName) ? "SC_Relation" : baseName.Trim();
            string candidate = root;
            int index = 1;
            while (existing != null && existing.ContainsKey(candidate))
            {
                candidate = root + "__" + index.ToString(CultureInfo.InvariantCulture);
                index++;
            }
            return candidate;
        }

        private static string BuildSignature(string device, bool axis, string input, IEnumerable<string> reformers)
        {
            List<string> normalizedReformers = NormalizeReformerSet(reformers);
            string join = string.Join(SignatureSeparator, normalizedReformers);
            return ((device ?? string.Empty).Trim().ToLowerInvariant()) +
                   SignatureSeparator +
                   (axis ? "1" : "0") +
                   SignatureSeparator +
                   ((input ?? string.Empty).Trim().ToLowerInvariant()) +
                   SignatureSeparator +
                   join;
        }

        private static Dictionary<string, string> BuildSignatureIndex(
            Dictionary<string, Relation> relations,
            Dictionary<string, Bind> binds)
        {
            Dictionary<string, string> output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> entry in relations ?? new Dictionary<string, Relation>(StringComparer.OrdinalIgnoreCase))
            {
                string relationName = entry.Key ?? string.Empty;
                Relation relation = entry.Value;
                if (string.IsNullOrWhiteSpace(relationName) || relation == null)
                    continue;

                Bind bind = null;
                if (binds != null && binds.ContainsKey(relationName))
                    bind = binds[relationName];
                if (bind == null)
                    bind = relation.bind;
                if (bind == null)
                    continue;

                string input = relation.ISAXIS ? bind.JAxis : bind.JButton;
                string signature = BuildSignature(bind.Joystick, relation.ISAXIS, input, bind.AllReformers);
                if (!output.ContainsKey(signature))
                    output.Add(signature, relationName);
            }
            return output;
        }

        private static List<string> NormalizeReformerSet(IEnumerable<string> reformers)
        {
            List<string> output = new List<string>();
            foreach (string reformer in reformers ?? new List<string>())
            {
                string normalized = NormalizeReformer(reformer);
                if (normalized.Length == 0)
                    continue;
                if (!output.Any(v => string.Equals(v, normalized, StringComparison.OrdinalIgnoreCase)))
                    output.Add(normalized);
            }

            return output
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NormalizeReformer(string reformer)
        {
            Modifier parsed = Modifier.ReformerToMod(reformer);
            if (parsed == null)
                return (reformer ?? string.Empty).Trim().ToLowerInvariant();

            return ((parsed.name ?? string.Empty).Trim().ToLowerInvariant()) +
                   SignatureSeparator +
                   ((parsed.device ?? string.Empty).Trim().ToLowerInvariant()) +
                   SignatureSeparator +
                   ((parsed.key ?? string.Empty).Trim().ToLowerInvariant());
        }

        private static bool SameReformerSet(IEnumerable<string> left, IEnumerable<string> right)
        {
            List<string> a = NormalizeReformerSet(left);
            List<string> b = NormalizeReformerSet(right);
            if (a.Count != b.Count)
                return false;

            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static bool TryGetCompatibleExistingRelation(
            string relationName,
            ScImportCandidate candidate,
            Dictionary<string, Relation> relations,
            Dictionary<string, Bind> binds,
            out Relation relation,
            out Bind bind)
        {
            relation = null;
            bind = null;

            if (string.IsNullOrWhiteSpace(relationName) || candidate == null)
                return false;
            if (relations == null || binds == null)
                return false;
            if (!relations.ContainsKey(relationName) || !binds.ContainsKey(relationName))
                return false;

            relation = relations[relationName];
            bind = binds[relationName];
            if (relation == null || bind == null)
                return false;
            if (relation.ISAXIS != candidate.IsAxis)
                return false;
            if (!string.Equals(bind.Joystick ?? string.Empty, candidate.Device ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                return false;

            string existingInput = candidate.IsAxis ? (bind.JAxis ?? string.Empty) : (bind.JButton ?? string.Empty);
            if (!string.Equals(existingInput, candidate.Input ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                return false;

            return SameReformerSet(bind.AllReformers, candidate.Reformers);
        }

        private static void MergeCandidateIntoExisting(ScImportCandidate candidate, Relation relation, Bind bind)
        {
            if (candidate == null || relation == null || bind == null)
                return;

            relation.AddNode(candidate.NodeKey, StarCitizenGame, candidate.IsAxis, StarCitizenPlane, false);
            foreach (string group in candidate.Groups ?? new List<string>())
                AddUniqueGroup(relation.Groups, group);

            if (bind.AllReformers == null)
                bind.AllReformers = new List<string>();
            foreach (string reformer in candidate.Reformers ?? new List<string>())
                AddUniqueString(bind.AllReformers, reformer);

            if (string.IsNullOrWhiteSpace(bind.AdditionalImportInfo) && !string.IsNullOrWhiteSpace(candidate.Title))
                bind.AdditionalImportInfo = candidate.Title;

            if (string.IsNullOrWhiteSpace(bind.AliasJoystick))
                bind.AliasJoystick = ResolveAlias(bind.Joystick);

            relation.bind = bind;
        }

        private static string ResolveAlias(string device)
        {
            string token = (device ?? string.Empty).Trim();
            if (token.Length == 0 || InternalDataManagement.JoystickAliases == null)
                return string.Empty;
            if (!InternalDataManagement.JoystickAliases.ContainsKey(token))
                return string.Empty;
            return InternalDataManagement.JoystickAliases[token] ?? string.Empty;
        }

        private static void AddUniqueGroup(List<string> groups, string value)
        {
            if (groups == null || string.IsNullOrWhiteSpace(value))
                return;
            if (!groups.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)))
                groups.Add(value.Trim());
        }

        private static void AddUniqueString(List<string> list, string value)
        {
            if (list == null || string.IsNullOrWhiteSpace(value))
                return;
            if (!list.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)))
                list.Add(value.Trim());
        }

        private static bool IsAxisToken(string value)
        {
            string token = (value ?? string.Empty).Trim().ToUpperInvariant();
            return token == "JOY_X" ||
                   token == "JOY_Y" ||
                   token == "JOY_Z" ||
                   token == "JOY_RX" ||
                   token == "JOY_RY" ||
                   token == "JOY_RZ" ||
                   token == "JOY_SLIDER1" ||
                   token == "JOY_SLIDER2";
        }

        private static int NextFreeSlot(List<int> used)
        {
            HashSet<int> usedSet = new HashSet<int>(used ?? new List<int>());
            for (int i = 1; i <= MaxJoystickSlots; i++)
            {
                if (!usedSet.Contains(i))
                    return i;
            }
            return -1;
        }

        private static string NormalizeNodeKey(string value)
        {
            string token = (value ?? string.Empty).Trim();
            if (token.Length == 0)
                return string.Empty;

            if (token.StartsWith("game=", StringComparison.OrdinalIgnoreCase))
            {
                int idIdx = token.IndexOf(",id=", StringComparison.OrdinalIgnoreCase);
                if (idIdx >= 0 && idIdx + 4 < token.Length)
                    token = token.Substring(idIdx + 4).Trim();
            }
            else if (token.StartsWith("id=", StringComparison.OrdinalIgnoreCase))
            {
                token = token.Substring(3).Trim();
            }

            token = token.Replace("::", "|");
            return token;
        }

        private static string BuildNodeKey(string category, string id)
        {
            string left = (category ?? string.Empty).Trim();
            string right = (id ?? string.Empty).Trim();
            if (left.Length == 0 || right.Length == 0)
                return string.Empty;
            return left + "|" + right;
        }

        // Node IDs exist in three spellings: "actionmap|action" (engine), "actionmap___action" (DB correction index)
        // and a bare "action" (GUI node search of older profiles). A bare action only resolves when exactly one
        // action map has it.
        private static bool TryResolveNodeState(Dictionary<string, ScBindingState> states, string nodeKey, out ScBindingState state, out string problem)
        {
            problem = string.Empty;
            string key = (nodeKey ?? string.Empty).Trim();
            if (states.TryGetValue(key, out state))
                return true;

            int split = key.IndexOf("___", StringComparison.Ordinal);
            if (split > 0 && states.TryGetValue(key.Substring(0, split) + "|" + key.Substring(split + 3), out state))
                return true;

            if (key.Length > 0 && key.IndexOf('|') < 0 && split < 0)
            {
                List<ScBindingState> matches = states.Values
                    .Where(v => string.Equals(v.Id, key, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count == 1)
                {
                    state = matches[0];
                    return true;
                }
                if (matches.Count > 1)
                {
                    problem = "Skipping Star Citizen node '" + key + "': the action exists in several action maps (" +
                              string.Join(", ", matches.Select(v => v.Category).OrderBy(v => v, StringComparer.OrdinalIgnoreCase)) +
                              "). Re-add the node with its action map, e.g. '" + matches[0].Category + "|" + key + "'.";
                    return false;
                }
            }

            problem = "Skipping Star Citizen node not found in catalog: " + key;
            return false;
        }

        private static string NormalizeGame(string game)
        {
            return string.IsNullOrWhiteSpace(game) ? "DCS" : game.Trim();
        }

        private static int ParseInt(string raw, int fallback)
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return parsed;
            return fallback;
        }

        private static string ParseXmlAttributeInline(string line, string attribute)
        {
            string source = line ?? string.Empty;
            string attr = (attribute ?? string.Empty).Trim();
            if (source.Length == 0 || attr.Length == 0)
                return string.Empty;

            string needle = attr + "=\"";
            int start = source.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;
            start += needle.Length;
            int end = source.IndexOf('"', start);
            if (end <= start)
                return string.Empty;
            return source.Substring(start, end - start).Trim();
        }

        private static void ParseActionComment(XElement action, out string title, out bool? axis)
        {
            title = string.Empty;
            axis = null;

            if (action == null)
                return;

            XComment comment = action.Nodes()
                .OfType<XComment>()
                .FirstOrDefault();
            if (comment == null)
                return;

            string value = (comment.Value ?? string.Empty).Trim();
            if (value.Length == 0)
                return;

            int split = value.IndexOf("//", StringComparison.Ordinal);
            if (split < 0)
            {
                title = value;
                return;
            }

            title = value.Substring(0, split).Trim();
            string axisRaw = value.Substring(split + 2).Trim();
            if (bool.TryParse(axisRaw, out bool parsed))
                axis = parsed;
        }

        private static string NormalizeScProductToken(string product)
        {
            string value = (product ?? string.Empty).Trim();
            if (value.Length == 0)
                return string.Empty;

            value = value.Replace("  {", " {");
            string guid = ExtractGuidToken(value);
            if (guid.Length > 0)
            {
                string baseName = StripDeviceGuidSuffix(value);
                if (baseName.Length > 0)
                    return baseName + " " + guid;
                return guid;
            }

            return value;
        }

        private static bool LooksLikeDeviceWithGuid(string value)
        {
            return ExtractGuidToken(value).Length > 0;
        }

        private static string ExtractGuidToken(string value)
        {
            string token = (value ?? string.Empty).Trim();
            int open = token.LastIndexOf('{');
            int close = token.LastIndexOf('}');
            if (open < 0 || close <= open)
                return string.Empty;

            string raw = token.Substring(open, close - open + 1);
            string guidRaw = raw.Trim('{', '}');
            if (!Guid.TryParse(guidRaw, out Guid parsed))
                return string.Empty;
            return "{" + parsed.ToString().ToUpperInvariant() + "}";
        }

        private static string StripDeviceGuidSuffix(string value)
        {
            string token = (value ?? string.Empty).Trim();
            int split = token.LastIndexOf(" {", StringComparison.Ordinal);
            if (split > 0 && token.EndsWith("}", StringComparison.Ordinal))
                return token.Substring(0, split).Trim();
            return token;
        }

        private static string XmlEscape(string value)
        {
            string token = value ?? string.Empty;
            return token
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private static string XmlEscapeComment(string value)
        {
            string token = (value ?? string.Empty).Trim();
            token = token.Replace("--", "- -");
            if (token.EndsWith("-", StringComparison.Ordinal))
                token += " ";
            return token;
        }

        private static void LoadKeyboardConversion(
            Dictionary<string, string> scToDx,
            Dictionary<string, string> dxToSc,
            List<string> warnings)
        {
            string basePath = MainStructure.PROGPATH ?? Environment.CurrentDirectory;
            string path = Path.Combine(basePath, "TOOLS", "Conversions", "SC2DX.keyboardconversion");
            if (!File.Exists(path))
            {
                AddUnique(warnings, "Missing keyboard conversion file: " + path);
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                AddUnique(warnings, "Could not read keyboard conversion file '" + path + "': " + ex.Message);
                return;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (!TryParseKeyboardConversionLine(lines[i], out string left, out string right))
                    continue;

                if (!scToDx.ContainsKey(left))
                    scToDx[left] = right;
                if (!dxToSc.ContainsKey(right))
                    dxToSc[right] = left;
            }
        }

        private static bool TryParseKeyboardConversionLine(string line, out string left, out string right)
        {
            left = string.Empty;
            right = string.Empty;
            string normalized = (line ?? string.Empty).Trim();
            if (normalized.Length == 0)
                return false;

            normalized = normalized.Replace("Ã‚Â§", KeyboardConversionSeparator);
            normalized = normalized.Replace("Â§", KeyboardConversionSeparator);
            int split = normalized.IndexOf(KeyboardConversionSeparator, StringComparison.Ordinal);
            if (split <= 0 || split >= normalized.Length - KeyboardConversionSeparator.Length)
                return false;

            left = normalized.Substring(0, split).Trim();
            right = normalized.Substring(split + KeyboardConversionSeparator.Length).Trim();
            return left.Length > 0 && right.Length > 0;
        }

        private static string SC2JPJoyKey(string key)
        {
            string token = (key ?? string.Empty).Trim();
            if (token.Length == 0)
                return string.Empty;

            string lower = token.ToLowerInvariant();
            if (lower == "x" || lower == "y" || lower == "z" || lower.StartsWith("slider", StringComparison.Ordinal))
                return "JOY_" + token.ToUpperInvariant();
            if (lower.StartsWith("rot", StringComparison.Ordinal))
                return "JOY_R" + token.Substring(3).ToUpperInvariant();
            if (lower.StartsWith("button", StringComparison.Ordinal))
                return "JOY_BTN" + token.Substring(6);
            if (lower.StartsWith("hat", StringComparison.Ordinal))
            {
                string[] parts = lower.Split('_');
                if (parts.Length < 2)
                    return string.Empty;

                string dir;
                switch (parts[1])
                {
                    case "up":
                        dir = "U";
                        break;
                    case "upright":
                        dir = "UR";
                        break;
                    case "right":
                        dir = "R";
                        break;
                    case "downright":
                        dir = "DR";
                        break;
                    case "down":
                        dir = "D";
                        break;
                    case "downleft":
                        dir = "DL";
                        break;
                    case "left":
                        dir = "L";
                        break;
                    case "upleft":
                        dir = "UL";
                        break;
                    default:
                        return string.Empty;
                }

                string head = parts[0].Substring(3);
                return "JOY_BTN_POV" + head + "_" + dir;
            }

            return string.Empty;
        }

        private static string JP2SCJoyKey(string key)
        {
            string token = (key ?? string.Empty).Trim().ToUpperInvariant();
            if (token.Length == 0)
                return string.Empty;

            if (token.StartsWith("JOY_BTN_POV", StringComparison.OrdinalIgnoreCase))
            {
                string sub = token.Substring("JOY_BTN_POV".Length);
                int split = sub.IndexOf('_');
                if (split <= 0 || split >= sub.Length - 1)
                    return string.Empty;

                string head = sub.Substring(0, split);
                string dir = sub.Substring(split + 1).ToUpperInvariant();
                string mapped;
                switch (dir)
                {
                    case "U":
                        mapped = "up";
                        break;
                    case "UR":
                        mapped = "upright";
                        break;
                    case "R":
                        mapped = "right";
                        break;
                    case "DR":
                        mapped = "downright";
                        break;
                    case "D":
                        mapped = "down";
                        break;
                    case "DL":
                        mapped = "downleft";
                        break;
                    case "L":
                        mapped = "left";
                        break;
                    case "UL":
                        mapped = "upleft";
                        break;
                    default:
                        return string.Empty;
                }

                return "hat" + head + "_" + mapped;
            }

            if (token.StartsWith("JOY_BTN", StringComparison.OrdinalIgnoreCase))
                return "button" + token.Substring("JOY_BTN".Length).ToLowerInvariant();

            if (token.StartsWith("JOY_", StringComparison.OrdinalIgnoreCase))
            {
                string axis = token.Substring("JOY_".Length).ToLowerInvariant();
                if (axis.StartsWith("slider", StringComparison.Ordinal))
                    return axis;
                if (axis.StartsWith("r", StringComparison.Ordinal))
                    return "rot" + axis.Substring(1);
                return axis;
            }

            return string.Empty;
        }

        private static string SC2JPGPKey(string key)
        {
            switch ((key ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "shoulderl": return "JOY_BTN5";
                case "shoulderr": return "JOY_BTN6";
                case "dpad_up": return "JOY_BTN_POV1_U";
                case "dpad_left": return "JOY_BTN_POV1_L";
                case "dpad_right": return "JOY_BTN_POV1_R";
                case "dpad_down": return "JOY_BTN_POV1_D";
                case "b": return "JOY_BTN2";
                case "a": return "JOY_BTN1";
                case "y": return "JOY_BTN4";
                case "x": return "JOY_BTN3";
                case "thumblx": return "JOY_X";
                case "thumbly": return "JOY_Y";
                case "thumbrx": return "JOY_RX";
                case "thumbry": return "JOY_RY";
                case "thumbl": return "JOY_BTN9";
                case "thumbr": return "JOY_BTN10";
                case "triggerr": return "JOY_Z";
                case "triggerl": return "JOY_RZ";
                case "triggerr_btn": return "JOY_BTN16";
                case "triggerl_btn": return "JOY_BTN17";
                case "thumbl_up": return "JOY_BTN_POV2_U";
                case "thumbl_left": return "JOY_BTN_POV2_L";
                case "thumbl_right": return "JOY_BTN_POV2_R";
                case "thumbl_down": return "JOY_BTN_POV2_D";
                case "thumbr_up": return "JOY_BTN_POV3_U";
                case "thumbr_left": return "JOY_BTN_POV3_L";
                case "thumbr_right": return "JOY_BTN_POV3_R";
                case "thumbr_down": return "JOY_BTN_POV3_D";
                default:
                    return string.Empty;
            }
        }

        private static string JP2SCGPKey(string key)
        {
            switch ((key ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "JOY_BTN5": return "shoulderl";
                case "JOY_BTN6": return "shoulderr";
                case "JOY_BTN_POV1_U": return "dpad_up";
                case "JOY_BTN_POV1_L": return "dpad_left";
                case "JOY_BTN_POV1_R": return "dpad_right";
                case "JOY_BTN_POV1_D": return "dpad_down";
                case "JOY_BTN2": return "b";
                case "JOY_BTN1": return "a";
                case "JOY_BTN4": return "y";
                case "JOY_BTN3": return "x";
                case "JOY_X": return "thumblx";
                case "JOY_Y": return "thumbly";
                case "JOY_RX": return "thumbrx";
                case "JOY_RY": return "thumbry";
                case "JOY_BTN9": return "thumbl";
                case "JOY_BTN10": return "thumbr";
                case "JOY_Z": return "triggerr";
                case "JOY_RZ": return "triggerl";
                case "JOY_BTN16": return "triggerr_btn";
                case "JOY_BTN17": return "triggerl_btn";
                case "JOY_BTN_POV2_U": return "thumbl_up";
                case "JOY_BTN_POV2_L": return "thumbl_left";
                case "JOY_BTN_POV2_R": return "thumbl_right";
                case "JOY_BTN_POV2_D": return "thumbl_down";
                case "JOY_BTN_POV3_U": return "thumbr_up";
                case "JOY_BTN_POV3_L": return "thumbr_left";
                case "JOY_BTN_POV3_R": return "thumbr_right";
                case "JOY_BTN_POV3_D": return "thumbr_down";
                default:
                    return string.Empty;
            }
        }

        private sealed class ScProfilePaths
        {
            public bool IsValid;
            public string RootPath;
            public string ProfileDirectory;
            public string ActionmapsFile;
            public string MetadataFile;
        }

        private enum StarCitizenExportMode
        {
            Clean,
            Add,
            Merge,
            MergeOverwrite
        }

        private sealed class ScWantedRebind
        {
            public ScBindingState State;
            public string Input;
            public string RelationName;
            public bool IsAxis;
            public Bind Bind;
        }

        // Action map of "map|action" or "map___action"; empty for a bare action.
        private static string NodeKeyMap(string nodeKey)
        {
            string key = (nodeKey ?? string.Empty).Trim();
            int split = key.IndexOf('|');
            if (split < 0)
                split = key.IndexOf("___", StringComparison.Ordinal);
            return split > 0 ? key.Substring(0, split) : string.Empty;
        }

        private static string RebindInput(XElement rebind)
        {
            return ((string)rebind.Attribute("input") ?? string.Empty).Trim();
        }

        // "kb", "js", "gp", "mo" (mouse) or empty; instance 1 when the prefix has no number.
        private static string RebindType(XElement rebind)
        {
            string input = RebindInput(rebind);
            return input.Length >= 2 ? input.Substring(0, 2).ToLowerInvariant() : string.Empty;
        }

        private static int RebindInstance(XElement rebind)
        {
            string input = RebindInput(rebind);
            int split = input.IndexOf('_');
            int instance;
            return split > 2 && int.TryParse(input.Substring(2, split - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out instance) ? instance : 1;
        }

        // actionmaps.xml as an XDocument with the original whitespace; new elements get the game's one-space indent.
        private sealed class ScActionmapsDocument
        {
            private XDocument document;
            private XElement profile;
            private string newline = "\n";
            private bool endsWithNewline = true;
            // The game writes empty elements with attributes as <x a="1"/>; XmlWriter adds a space before the slash.
            private bool compactEmptyTags = true;

            public static ScActionmapsDocument Open(string path, bool exists, ScActionFileData data)
            {
                ScActionmapsDocument result = new ScActionmapsDocument();
                string text = exists ? File.ReadAllText(path) : string.Empty;
                if (text.Contains("\r\n"))
                    result.newline = "\r\n";
                if (exists)
                {
                    result.endsWithNewline = text.EndsWith("\n", StringComparison.Ordinal);
                    result.compactEmptyTags = !text.Contains("\" />");
                }
                if (exists)
                {
                    result.document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
                }
                else
                {
                    string nl = result.newline;
                    result.document = XDocument.Parse(
                        "<ActionMaps>" + nl +
                        " <ActionProfiles version=\"1\" optionsVersion=\"2\" rebindVersion=\"2\" profileName=\"default\">" + nl +
                        "  <options type=\"keyboard\" instance=\"1\" Product=\"Keyboard  {" + Guid.Empty.ToString().ToUpperInvariant() + "}\"/>" + nl +
                        "  <options type=\"gamepad\" instance=\"1\" Product=\"Controller (Gamepad)\"/>" + nl +
                        "  <modifiers />" + nl +
                        " </ActionProfiles>" + nl +
                        "</ActionMaps>", LoadOptions.PreserveWhitespace);
                }
                XElement root = result.document.Root;
                result.profile = root.Element("ActionProfiles");
                if (result.profile == null)
                {
                    result.profile = new XElement("ActionProfiles", new XAttribute("version", "1"), new XAttribute("optionsVersion", "2"), new XAttribute("rebindVersion", "2"), new XAttribute("profileName", "default"));
                    result.AddChild(root, result.profile, 1);
                }
                return result;
            }

            private string Indent(int depth)
            {
                return newline + new string(' ', depth);
            }

            // Adds a child after the last child element (or as first child), keeping the parent's closing indent.
            private void AddChild(XElement parent, XElement child, int depth, XElement after = null)
            {
                XElement anchor = after ?? parent.Elements().LastOrDefault();
                if (anchor != null)
                {
                    anchor.AddAfterSelf(new XText(Indent(depth)), child);
                    return;
                }
                foreach (XText whitespace in parent.Nodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).ToList())
                    whitespace.Remove();
                parent.Add(new XText(Indent(depth)), child, new XText(Indent(depth - 1)));
            }

            public static void RemoveWithIndent(XElement element)
            {
                XText whitespace = element.PreviousNode as XText;
                if (whitespace != null && string.IsNullOrWhiteSpace(whitespace.Value))
                    whitespace.Remove();
                element.Remove();
            }

            // Joystick slots JoyPro uses get the device's product name. The options' children (option groups: curves,
            // inversion) belong to the device: when a slot changes device they move with it to its new slot, and the
            // slot gets the children its new device had elsewhere in the file.
            public void SetJoystickProducts(ScSlotPlan plan, List<string> warnings)
            {
                Dictionary<string, List<XNode>> childrenByProduct = new Dictionary<string, List<XNode>>(StringComparer.OrdinalIgnoreCase);
                foreach (XElement existing in profile.Elements("options").Where(o => (string)o.Attribute("type") == "joystick"))
                {
                    string existingProduct = NormalizeScProductToken((string)existing.Attribute("Product"));
                    if (existingProduct.Length > 0 && existing.Elements().Any() && !childrenByProduct.ContainsKey(existingProduct))
                        childrenByProduct[existingProduct] = existing.Nodes().Select(CloneNode).ToList();
                }
                HashSet<string> placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<int, string> s in plan.SlotToProduct.Where(s => plan.DeviceToSlot.ContainsValue(s.Key)))
                {
                    placed.Add(NormalizeScProductToken(s.Value));
                    placed.Add(NormalizeScProductToken(ScProductForFile(s.Value)));
                }

                foreach (KeyValuePair<int, string> slot in plan.SlotToProduct.Where(s => plan.DeviceToSlot.ContainsValue(s.Key)))
                {
                    string instance = slot.Key.ToString(CultureInfo.InvariantCulture);
                    // The file gets the game's product name; a slot written earlier with the instance name counts as the
                    // same device (its settings stay, the name is corrected).
                    string fileProduct = ScProductForFile(slot.Value);
                    string wanted = NormalizeScProductToken(fileProduct);
                    string planned = NormalizeScProductToken(slot.Value);
                    List<XNode> moved;
                    if (!childrenByProduct.TryGetValue(wanted, out moved))
                        childrenByProduct.TryGetValue(planned, out moved);
                    XElement option = profile.Elements("options").FirstOrDefault(o => (string)o.Attribute("type") == "joystick" && (string)o.Attribute("instance") == instance);
                    if (option == null)
                    {
                        option = new XElement("options", new XAttribute("type", "joystick"), new XAttribute("instance", instance), new XAttribute("Product", fileProduct));
                        if (moved != null)
                            option.Add(moved.Select(CloneNode));
                        XElement lastOption = profile.Elements("options").LastOrDefault();
                        AddChild(profile, option, 2, lastOption);
                        continue;
                    }
                    string product = ((string)option.Attribute("Product") ?? string.Empty).Trim();
                    if (NormalizeScProductToken(product) == wanted)
                        continue;
                    if (NormalizeScProductToken(product) == planned)
                    {
                        option.SetAttributeValue("Product", fileProduct);
                        continue;
                    }
                    string previous = NormalizeScProductToken(product);
                    if (option.Elements().Any() && previous.Length > 0 && !placed.Contains(previous))
                        AddUnique(warnings, "Star Citizen: js" + instance + " now belongs to '" + slot.Value + "'; the axis settings of '" + previous + "' on it are dropped (that device gets no slot).");
                    option.RemoveNodes();
                    if (moved != null)
                        option.Add(moved.Select(CloneNode));
                    option.SetAttributeValue("Product", fileProduct);
                }
            }

            private static XNode CloneNode(XNode node)
            {
                XElement element = node as XElement;
                if (element != null)
                    return new XElement(element);
                XText text = node as XText;
                if (text != null)
                    return new XText(text.Value);
                XComment comment = node as XComment;
                return comment != null ? (XNode)new XComment(comment.Value) : new XText(string.Empty);
            }

            public XElement JoystickOptions(int instance)
            {
                string key = instance.ToString(CultureInfo.InvariantCulture);
                return profile.Elements("options").FirstOrDefault(o => (string)o.Attribute("type") == "joystick" && (string)o.Attribute("instance") == key);
            }

            public List<XElement> DeviceOptions()
            {
                return document.Root.Descendants("deviceoptions").ToList();
            }

            public void AddElement(XElement parent, XElement child, int depth)
            {
                AddChild(parent, child, depth);
            }

            // <option input="x"/> in the device's <deviceoptions name="<Product as in its options>">; a new block goes
            // before the first <options> (INF: the 3.x layout; not observed in a 4.10 file).
            public XElement AddDeviceOption(string rawProduct, string axisToken)
            {
                string normalized = NormalizeScProductToken(rawProduct);
                XElement block = DeviceOptions().FirstOrDefault(d => NormalizeScProductToken((string)d.Attribute("name")) == normalized);
                if (block == null)
                {
                    block = new XElement("deviceoptions", new XAttribute("name", rawProduct));
                    XElement firstOptions = profile.Elements("options").FirstOrDefault();
                    if (firstOptions != null)
                        firstOptions.AddBeforeSelf(block, new XText(Indent(2)));
                    else
                        AddChild(profile, block, 2);
                }
                XElement option = new XElement("option", new XAttribute("input", axisToken));
                AddChild(block, option, 3);
                return option;
            }

            public XElement FindOrCreateAction(string category, string id, string title, bool isAxis)
            {
                XElement map = profile.Elements("actionmap").FirstOrDefault(m => string.Equals((string)m.Attribute("name"), category, StringComparison.OrdinalIgnoreCase));
                if (map == null)
                {
                    map = new XElement("actionmap", new XAttribute("name", category));
                    AddChild(profile, map, 2);
                }
                XElement action = map.Elements("action").FirstOrDefault(a => string.Equals((string)a.Attribute("name"), id, StringComparison.OrdinalIgnoreCase));
                if (action == null)
                {
                    action = new XElement("action", new XAttribute("name", id));
                    AddChild(map, action, 3);
                }
                return action;
            }

            public XElement AddRebind(XElement action, string input)
            {
                XElement rebind = new XElement("rebind", new XAttribute("input", input));
                AddChild(action, rebind, 4);
                return rebind;
            }

            public IEnumerable<XElement> ShipActions()
            {
                return profile.Elements("actionmap")
                    .Where(m => IsFlightMap((string)m.Attribute("name")))
                    .SelectMany(m => m.Elements("action"));
            }

            public void RemoveEmptyAction(XElement action)
            {
                XElement map = action.Parent;
                RemoveWithIndent(action);
                if (map != null && !map.Elements().Any())
                    RemoveWithIndent(map);
            }

            public string Render()
            {
                XmlWriterSettings settings = new XmlWriterSettings
                {
                    OmitXmlDeclaration = document.Declaration == null,
                    Indent = false,
                    NewLineHandling = NewLineHandling.Replace,
                    NewLineChars = newline,
                    Encoding = new UTF8Encoding(false)
                };
                string text;
                using (MemoryStream stream = new MemoryStream())
                {
                    using (XmlWriter writer = XmlWriter.Create(stream, settings))
                        document.Save(writer);
                    text = new UTF8Encoding(false).GetString(stream.ToArray());
                }
                if (compactEmptyTags)
                    text = text.Replace("\" />", "\"/>");
                text = text.TrimEnd('\r', '\n');
                return endsWithNewline ? text + newline : text;
            }
        }

        private sealed class ScActionFileData
        {
            public string Version;
            public string OptionsVersion;
            public string RebindVersion;
            public string ProfileName;
            public string KeyboardProduct;
            public string GamepadProduct;
            public List<ScOptionDevice> JoystickOptions;
            public List<ScActionEntry> Actions;
            public Dictionary<int, XElement> JoystickOptionElements = new Dictionary<int, XElement>();
            public List<XElement> DeviceOptions = new List<XElement>();
            public Dictionary<string, ScOptionGroupInfo> OptionTree;

            public ScActionFileData()
            {
                Version = "1";
                OptionsVersion = "2";
                RebindVersion = "2";
                ProfileName = "default";
                KeyboardProduct = string.Empty;
                GamepadProduct = string.Empty;
                JoystickOptions = new List<ScOptionDevice>();
                Actions = new List<ScActionEntry>();
            }
        }

        private sealed class ScOptionDevice
        {
            public int Instance;
            public string Product;
        }

        private sealed class ScActionEntry
        {
            public string Category;
            public string Id;
            public string Title;
            public bool IsAxis;
            public int Order;
            public string OptionGroup = string.Empty;
            public string DefaultJoystick = string.Empty;
            public List<ScRebindEntry> Rebinds;

            public ScActionEntry()
            {
                Category = string.Empty;
                Id = string.Empty;
                Title = string.Empty;
                IsAxis = false;
                Order = 0;
                Rebinds = new List<ScRebindEntry>();
            }
        }

        private sealed class ScRebindEntry
        {
            public string Input;
            public string DefaultInput;
        }

        private sealed class ScCatalogAction
        {
            public string Category;
            public string Id;
            public string Title;
            public bool IsAxis;
            public int Order;
            public string OptionGroup;
            public string DefaultJoystick;
            public string DefaultKeyboardInput;
        }

        private sealed class ScRebindMetadataHints
        {
            public string KeyboardRelationName;
            public string GamepadRelationName;
            public string JoystickRelationName;

            public ScRebindMetadataHints()
            {
                KeyboardRelationName = string.Empty;
                GamepadRelationName = string.Empty;
                JoystickRelationName = string.Empty;
            }
        }

        private sealed class ScImportCandidate
        {
            public string NodeKey;
            public string Category;
            public string ActionId;
            public string Title;
            public string Device;
            public List<string> Reformers;
            public bool IsAxis;
            public string Input;
            public List<string> Groups;
            public string RelationName;
            public ScAxisValues AxisValues;
        }

        private sealed class ScSlotPlan
        {
            public List<ScOptionDevice> Options;
            public Dictionary<string, int> DeviceToSlot;
            public Dictionary<int, string> SlotToProduct;
        }

        private sealed class ScBindSource
        {
            public string NodeKey;
            public string RelationName;
            public string Title;
            public bool IsAxis;
            public string Device;
            public string Input;
            public List<string> Reformers;
            public string InputType;
            public bool IsGamepad;
            public Bind Bind;
        }

        private sealed class ScBindingState
        {
            public string Category;
            public string Id;
            public string Title;
            public bool IsAxis;
            public int Order;
            public string OptionGroup = string.Empty;
            public string KeyboardInput;
            public string KeyboardRelationName;
            public string JoystickInput;
            public string JoystickRelationName;
            public string GamepadInput;
            public string GamepadRelationName;

            public ScBindingState()
            {
                Category = string.Empty;
                Id = string.Empty;
                Title = string.Empty;
                IsAxis = false;
                Order = 0;
                KeyboardInput = string.Empty;
                KeyboardRelationName = string.Empty;
                JoystickInput = string.Empty;
                JoystickRelationName = string.Empty;
                GamepadInput = string.Empty;
                GamepadRelationName = string.Empty;
            }
        }
    }
}
