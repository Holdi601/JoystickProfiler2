using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Web.Script.Serialization;
using JoyPro.Engine;

namespace JoyPro
{
    public static class InternalDataManagement
    {
        public static string[] RelationWordFilter = new string[0];
        public static bool showUnassignedRelations = true;
        public static bool showUnassignedGroups = true;
        public static Dictionary<string, Relation> AllRelations = CreateRelationMap();
        public static Dictionary<string, Bind> AllBinds = CreateBindMap();
        public static List<string> AllGroups = new List<string>();
        public static Dictionary<string, bool> GroupActivity = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public static Dictionary<string, bool> JoystickActivity = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public static Dictionary<string, string> JoystickAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static string[] LocalJoysticks = new string[0];
        public static Dictionary<string, string> LocalJoystickPGUID = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static Dictionary<string, Modifier> AllModifiers = new Dictionary<string, Modifier>(StringComparer.OrdinalIgnoreCase);
        public static Dictionary<string, string> JoystickFileImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static string JoystickLayoutExport = string.Empty;
        public static Dictionary<string, Dictionary<string, string>> PlaneAliases = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        public static List<KeyValuePair<string, string>> ModifierNameChanges = new List<KeyValuePair<string, string>>();
        public static Dictionary<string, ForceFeedbackS> JoystickFFB = new Dictionary<string, ForceFeedbackS>(StringComparer.OrdinalIgnoreCase);
        public static Dictionary<string, string> FfbOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Detent calibration per device axis ("<device>|<axis>" -> DetentCalibration text).
        public static Dictionary<string, string> AxisCalibrations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Virtual joystick mode settings and mapping table as JSON (Engine.VJoy.VJoySettings); empty = never used.
        public static string VirtualJoystickConfig = string.Empty;
        public static bool OverwriteExistingRelationOnInsertInEngine = false;
        public static bool EnableLegacyLoadCorrections = true;
        public static bool AutoAddDbItemsOnLoad = true;
        public static bool RemapMissingJoysticksOnLoad = true;
        public static bool ConfirmMissingJoystickRemapOnLoad = true;
        public static Action<string> LoadCorrectionMessageSink = null;
        public static Func<string, string, string, bool> LoadDeviceRemapConfirmation = null;
        public static LoadItemCorrectionMode ItemCorrectionModeOnLoad = LoadItemCorrectionMode.Balanced;
        public static int? ItemCorrectionAutoApplyScoreThresholdOnLoad = null;
        public static bool ConfirmItemCorrectionsOnLoad = true;
        public static Func<LoadCorrectionSuggestion, bool> LoadItemCorrectionConfirmation = null;
        public static string CurrentDatabaseFingerprint = string.Empty;
        public static string LoadedProfileDatabaseFingerprint = string.Empty;

        private static DateTime LastLoadCorrectionRunUtc = DateTime.MinValue;
        private static bool LastLoadCorrectionsEnabled = false;
        private static bool LastAutoAddDbItemsEnabled = false;
        private static bool LastRemapMissingDevicesEnabled = false;
        private static bool LastConfirmDeviceRemapEnabled = false;
        private static bool LastConfirmItemCorrectionsEnabled = false;
        private static string LastItemCorrectionMode = LoadItemCorrectionMode.Balanced.ToString().ToLowerInvariant();
        private static int? LastItemCorrectionAutoApplyScoreThreshold = null;
        private static List<LoadCorrectionReportEntry> LastLoadCorrectionEntries = new List<LoadCorrectionReportEntry>();

        private static readonly HashSet<string> PlaneFamilyStopTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AI", "MENU", "TRACKIR", "EASY",
            "PILOT", "COPILOT", "RIO", "WSO", "CPG", "PLT", "GUNNER", "OPERATOR",
            "PERSONAL", "WEAPON", "STATION", "AIMING",
            "HORNET", "CREW"
        };

        public enum LoadItemCorrectionMode
        {
            Strict,
            Balanced,
            Aggressive
        }

        public sealed class LoadCorrectionSuggestion
        {
            public string RelationName { get; set; }
            public string Game { get; set; }
            public string OldId { get; set; }
            public string OldTitle { get; set; }
            public string OldPlane { get; set; }
            public string NewId { get; set; }
            public string NewTitle { get; set; }
            public string NewPlane { get; set; }
            public string Reason { get; set; }
            public int Score { get; set; }
            public string Confidence { get; set; }
            public string Mode { get; set; }

            public LoadCorrectionSuggestion()
            {
                RelationName = string.Empty;
                Game = string.Empty;
                OldId = string.Empty;
                OldTitle = string.Empty;
                OldPlane = string.Empty;
                NewId = string.Empty;
                NewTitle = string.Empty;
                NewPlane = string.Empty;
                Reason = string.Empty;
                Score = 0;
                Confidence = string.Empty;
                Mode = string.Empty;
            }
        }

        public sealed class LoadCorrectionReportEntry
        {
            public DateTime TimestampUtc { get; set; }
            public string Kind { get; set; }
            public string Action { get; set; }
            public string RelationName { get; set; }
            public string Game { get; set; }
            public string OldId { get; set; }
            public string OldTitle { get; set; }
            public string OldPlane { get; set; }
            public string NewId { get; set; }
            public string NewTitle { get; set; }
            public string NewPlane { get; set; }
            public string Confidence { get; set; }
            public int Score { get; set; }
            public bool PlaneMissingInDb { get; set; }
            public bool UsedPlaneFamilyAlias { get; set; }
            public string Reason { get; set; }
            public string Details { get; set; }

            public LoadCorrectionReportEntry()
            {
                TimestampUtc = DateTime.UtcNow;
                Kind = string.Empty;
                Action = string.Empty;
                RelationName = string.Empty;
                Game = string.Empty;
                OldId = string.Empty;
                OldTitle = string.Empty;
                OldPlane = string.Empty;
                NewId = string.Empty;
                NewTitle = string.Empty;
                NewPlane = string.Empty;
                Confidence = string.Empty;
                Score = 0;
                PlaneMissingInDb = false;
                UsedPlaneFamilyAlias = false;
                Reason = string.Empty;
                Details = string.Empty;
            }
        }

        public sealed class LoadCorrectionReport
        {
            public DateTime RunUtc { get; set; }
            public bool CorrectionsEnabled { get; set; }
            public string ItemCorrectionMode { get; set; }
            public int? ItemCorrectionAutoApplyScoreThreshold { get; set; }
            public bool ConfirmItemCorrections { get; set; }
            public bool AutoAddDbItems { get; set; }
            public bool RemapMissingDevices { get; set; }
            public bool ConfirmDeviceRemap { get; set; }
            public int TotalEntries { get; set; }
            public int AppliedEntries { get; set; }
            public int SkippedEntries { get; set; }
            public List<LoadCorrectionReportEntry> Entries { get; set; }

            public LoadCorrectionReport()
            {
                RunUtc = DateTime.MinValue;
                CorrectionsEnabled = false;
                ItemCorrectionMode = string.Empty;
                ItemCorrectionAutoApplyScoreThreshold = null;
                ConfirmItemCorrections = false;
                AutoAddDbItems = false;
                RemapMissingDevices = false;
                ConfirmDeviceRemap = false;
                TotalEntries = 0;
                AppliedEntries = 0;
                SkippedEntries = 0;
                Entries = new List<LoadCorrectionReportEntry>();
            }
        }

        public static LoadCorrectionReport GetLastLoadCorrectionReport()
        {
            LoadCorrectionReport report = new LoadCorrectionReport();
            report.RunUtc = LastLoadCorrectionRunUtc;
            report.CorrectionsEnabled = LastLoadCorrectionsEnabled;
            report.ItemCorrectionMode = LastItemCorrectionMode ?? string.Empty;
            report.ItemCorrectionAutoApplyScoreThreshold = LastItemCorrectionAutoApplyScoreThreshold;
            report.ConfirmItemCorrections = LastConfirmItemCorrectionsEnabled;
            report.AutoAddDbItems = LastAutoAddDbItemsEnabled;
            report.RemapMissingDevices = LastRemapMissingDevicesEnabled;
            report.ConfirmDeviceRemap = LastConfirmDeviceRemapEnabled;
            report.Entries = LastLoadCorrectionEntries
                .Select(CloneLoadCorrectionReportEntry)
                .ToList();
            report.TotalEntries = report.Entries.Count;
            report.AppliedEntries = report.Entries.Count(e =>
                e != null &&
                (string.Equals(e.Action, "auto", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(e.Action, "confirmed", StringComparison.OrdinalIgnoreCase)));
            report.SkippedEntries = report.Entries.Count(e =>
                e != null &&
                string.Equals(e.Action, "skipped", StringComparison.OrdinalIgnoreCase));
            return report;
        }

        public static void NewFile(bool keepMetaSettings = false)
        {
            AllRelations = CreateRelationMap();
            AllBinds = CreateBindMap();
            AllGroups = new List<string>();
            GroupActivity = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            JoystickActivity = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            JoystickAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            LocalJoysticks = new string[0];
            LocalJoystickPGUID = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AllModifiers = new Dictionary<string, Modifier>(StringComparer.OrdinalIgnoreCase);
            JoystickFileImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JoystickLayoutExport = string.Empty;
            PlaneAliases = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            ModifierNameChanges = new List<KeyValuePair<string, string>>();
            JoystickFFB = new Dictionary<string, ForceFeedbackS>(StringComparer.OrdinalIgnoreCase);
            FfbOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AxisCalibrations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            VirtualJoystickConfig = string.Empty;
            CurrentDatabaseFingerprint = string.Empty;
            LoadedProfileDatabaseFingerprint = string.Empty;

            if (!keepMetaSettings || MainStructure.msave == null)
                MainStructure.msave = new MetaSave();
        }

        public static void LoadProfile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("profile path is empty.");
            if (!File.Exists(filePath))
                throw new FileNotFoundException("profile does not exist.", filePath);

            bool json = LooksLikeJsonProfile(filePath);
            Pr0file profile = json
                ? LoadJsonProfile(filePath)
                : LoadBinaryProfile(filePath);

            ApplyProfile(profile, EnableLegacyLoadCorrections, AutoAddDbItemsOnLoad);
            MainStructure.msave.lastOpenedLocation = filePath;
            MainStructure.SaveMetaOnly();
            JoyPro.Logging.JoyProLog.Info("Profile", "Loaded " + (json ? "JSON" : "binary") + " profile " + filePath + " (" + new FileInfo(filePath).Length + " bytes): " +
                                                     (AllRelations == null ? 0 : AllRelations.Count) + " relation(s), " + (AllBinds == null ? 0 : AllBinds.Count) + " bind(s)" +
                                                     (EnableLegacyLoadCorrections ? ", load corrections on" : string.Empty));
        }

        public static void SaveProfileTo(string filePath)
        {
            SaveProfileTo(filePath, true);
        }

        internal static void SaveProfileTo(string filePath, bool updateMeta)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("profile path is empty.");

            string fullPath = Path.GetFullPath(filePath);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            ResyncBindsToMods();
            ResyncRelations();
            RecalcFigures();

            Pr0file profile = BuildProfileSnapshot();
            byte[] content;
            using (MemoryStream buffer = new MemoryStream())
            {
                BinaryFormatter formatter = new BinaryFormatter();
                formatter.Binder = new LegacyJoyProSerializationBinder();
                formatter.Serialize(buffer, profile);
                content = buffer.ToArray();
            }

            // The profile history (docs/profile-history.md) needs the file as it was before this save; JoyPro's own
            // last.pr0file (updateMeta false) has no history.
            bool recordHistory = updateMeta && JoyPro.History.ProfileHistory.Enabled;
            byte[] previous = null;
            if (recordHistory && File.Exists(fullPath))
            {
                try
                {
                    previous = File.ReadAllBytes(fullPath);
                }
                catch (IOException)
                {
                    previous = null;
                }
            }

            // Atomic: a temp file next to the profile, then swapped in. A crash or a full disk leaves the old profile.
            JoyPro.History.ProfileHistory.WriteFileAtomic(fullPath, content);
            JoyPro.Logging.JoyProLog.Info("Profile", "Saved profile " + fullPath + " (" + content.Length + " bytes): " +
                                                     (AllRelations == null ? 0 : AllRelations.Count) + " relation(s), " + (AllBinds == null ? 0 : AllBinds.Count) + " bind(s)");
            if (recordHistory)
                EngineProfileHistory.AfterSave(fullPath, previous, content, profile);

            if (updateMeta)
            {
                MainStructure.msave.lastOpenedLocation = fullPath;
                MainStructure.SaveMetaOnly();
            }
        }

        public static void ResyncBindsToMods()
        {
            Dictionary<string, Modifier> rebuilt = new Dictionary<string, Modifier>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, Bind> relationBind in AllBinds)
            {
                Bind bind = relationBind.Value;
                if (bind == null)
                    continue;

                if (bind.AllReformers == null)
                    bind.AllReformers = new List<string>();

                for (int i = bind.AllReformers.Count - 1; i >= 0; i--)
                {
                    string reformer = bind.AllReformers[i];
                    Modifier modifier = Modifier.ReformerToMod(reformer);
                    if (modifier == null || string.IsNullOrWhiteSpace(modifier.name))
                    {
                        bind.AllReformers.RemoveAt(i);
                        continue;
                    }

                    if (!rebuilt.ContainsKey(modifier.name))
                        rebuilt.Add(modifier.name, modifier);
                }
            }

            AllModifiers = rebuilt;
        }

        public static void ResyncRelations()
        {
            if (AllRelations == null)
                AllRelations = CreateRelationMap();
            if (AllBinds == null)
                AllBinds = CreateBindMap();

            List<string> relationKeys = AllRelations.Keys.ToList();
            for (int i = 0; i < relationKeys.Count; i++)
            {
                string key = relationKeys[i];
                Relation relation = AllRelations[key];
                if (relation == null)
                {
                    relation = new Relation();
                    AllRelations[key] = relation;
                }

                relation.NAME = key;
                if (relation.Groups == null)
                    relation.Groups = new List<string>();
                if (relation.NODES == null)
                    relation.NODES = new List<RelationItem>();

                for (int n = relation.NODES.Count - 1; n >= 0; n--)
                {
                    RelationItem node = relation.NODES[n];
                    if (node == null || string.IsNullOrWhiteSpace(node.ID))
                    {
                        relation.NODES.RemoveAt(n);
                        continue;
                    }

                    node.Game = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game;
                    if (node.AIRCRAFT == null)
                        node.AIRCRAFT = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                }

                relation.RecalculateElementCount();

                if (AllBinds.ContainsKey(key))
                {
                    relation.bind = AllBinds[key];
                    if (relation.bind != null)
                        relation.bind.Rl = relation;
                }
                else
                {
                    relation.bind = null;
                }
            }

            List<string> bindKeys = AllBinds.Keys.ToList();
            for (int i = 0; i < bindKeys.Count; i++)
            {
                string key = bindKeys[i];
                Bind bind = AllBinds[key];
                if (bind == null)
                {
                    AllBinds.Remove(key);
                    continue;
                }

                if (!AllRelations.ContainsKey(key))
                {
                    Relation relation = new Relation();
                    relation.NAME = key;
                    relation.ISAXIS = !string.IsNullOrWhiteSpace(bind.JAxis);
                    AllRelations.Add(key, relation);
                }

                Relation refRelation = AllRelations[key];
                bind.Rl = refRelation;
                refRelation.bind = bind;
                if (bind.AllReformers == null)
                    bind.AllReformers = new List<string>();
                if (bind.Curvature == null || bind.Curvature.Count == 0)
                    bind.Curvature = new List<double> { 0.0 };
                bind.Joystick = NormalizeJoystickDeviceName(bind.Joystick ?? string.Empty);
                bind.JAxis = bind.JAxis ?? string.Empty;
                bind.JButton = bind.JButton ?? string.Empty;
                bind.AdditionalImportInfo = bind.AdditionalImportInfo ?? string.Empty;
                bind.AliasJoystick = bind.AliasJoystick ?? string.Empty;
            }

            RebuildGroupList();
            RebuildJoystickActivity();
        }

        public static void RecalcFigures()
        {
            foreach (KeyValuePair<string, Relation> entry in AllRelations)
            {
                if (entry.Value != null)
                    entry.Value.RecalculateElementCount();
            }
        }

        public static Modifier GetModifierWithKeyCombo(string device, string key)
        {
            if (string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(key))
                return null;

            foreach (KeyValuePair<string, Modifier> entry in AllModifiers)
            {
                Modifier modifier = entry.Value;
                if (modifier == null)
                    continue;

                if (string.Equals(modifier.device, device, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(modifier.key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return modifier;
                }
            }

            return null;
        }

        public static ModExists DoesReformerExistInMods(string reformer)
        {
            Modifier modifier = Modifier.ReformerToMod(reformer);
            if (modifier == null || string.IsNullOrWhiteSpace(modifier.name))
                return ModExists.ERROR;

            if (!AllModifiers.ContainsKey(modifier.name))
            {
                Modifier comboMatch = GetModifierWithKeyCombo(modifier.device, modifier.key);
                return comboMatch == null ? ModExists.NOT_EXISTENT : ModExists.KEYBIND_EXISTS;
            }

            Modifier existing = AllModifiers[modifier.name];
            if (existing == null)
                return ModExists.NOT_EXISTENT;

            if (string.Equals(existing.device, modifier.device, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.key, modifier.key, StringComparison.OrdinalIgnoreCase))
            {
                return ModExists.ALL_EXISTS;
            }

            return ModExists.BINDNAME_EXISTS;
        }

        public static string GetReformerStringFromMod(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            if (!AllModifiers.ContainsKey(name))
                return null;
            return AllModifiers[name].toReformerString();
        }

        public static void AddReformerToMods(string reformer)
        {
            Modifier modifier = Modifier.ReformerToMod(reformer);
            if (modifier == null || string.IsNullOrWhiteSpace(modifier.name))
                return;

            if (!AllModifiers.ContainsKey(modifier.name))
                AllModifiers.Add(modifier.name, modifier);
        }

        private static Dictionary<string, Relation> CreateRelationMap()
        {
            return new Dictionary<string, Relation>(StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, Bind> CreateBindMap()
        {
            return new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase);
        }

        private static void ApplyProfile(Pr0file profile, bool applyLegacyCorrections, bool autoAddDbItems)
        {
            NewFile(true);

            string profileFingerprint = NormalizeDatabaseFingerprint(profile == null ? null : profile.DatabaseFingerprint);
            string currentFingerprint = applyLegacyCorrections
                ? GetOrComputeCurrentDatabaseFingerprint()
                : string.Empty;
            LoadedProfileDatabaseFingerprint = profileFingerprint;
            CurrentDatabaseFingerprint = currentFingerprint;

            bool fingerprintMatch =
                !string.IsNullOrWhiteSpace(profileFingerprint) &&
                !string.IsNullOrWhiteSpace(currentFingerprint) &&
                string.Equals(profileFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase);
            bool runCorrections = applyLegacyCorrections && !fingerprintMatch;

            BeginLoadCorrectionReportSession(runCorrections, autoAddDbItems);
            if (applyLegacyCorrections)
            {
                LoadCorrectionReportEntry gateEntry = new LoadCorrectionReportEntry();
                gateEntry.Kind = "db-fingerprint-check";
                gateEntry.RelationName = string.Empty;
                gateEntry.Game = string.Empty;
                gateEntry.OldId = string.Empty;
                gateEntry.OldTitle = string.Empty;
                gateEntry.OldPlane = string.Empty;
                gateEntry.NewId = string.Empty;
                gateEntry.NewTitle = string.Empty;
                gateEntry.NewPlane = string.Empty;
                gateEntry.Confidence = "high";
                gateEntry.Score = 100;

                if (fingerprintMatch)
                {
                    gateEntry.Action = "skipped";
                    gateEntry.Reason = "Profile DB fingerprint matches current DB fingerprint.";
                    ReportLoadCorrectionMessage("Load correction skipped: profile DB fingerprint matches current DB fingerprint.");
                }
                else if (string.IsNullOrWhiteSpace(profileFingerprint))
                {
                    gateEntry.Action = "auto";
                    gateEntry.Reason = "Profile has no DB fingerprint (older profile); running load correction.";
                    ReportLoadCorrectionMessage("Load correction: profile has no DB fingerprint, running full correction pass.");
                }
                else if (string.IsNullOrWhiteSpace(currentFingerprint))
                {
                    gateEntry.Action = "auto";
                    gateEntry.Reason = "Current DB fingerprint unavailable; running load correction.";
                    ReportLoadCorrectionMessage("Load correction: current DB fingerprint unavailable, running full correction pass.");
                }
                else
                {
                    gateEntry.Action = "auto";
                    gateEntry.Reason = "DB fingerprint mismatch; running load correction.";
                    ReportLoadCorrectionMessage("Load correction: DB fingerprint mismatch, running full correction pass.");
                }

                gateEntry.Details =
                    "profile-fingerprint=" + (profileFingerprint ?? string.Empty) +
                    "; current-fingerprint=" + (currentFingerprint ?? string.Empty);
                RecordLoadCorrectionEntry(gateEntry);
            }

            if (profile == null)
                return;

            Dictionary<string, Relation> relations = CreateRelationMap();
            foreach (KeyValuePair<string, Relation> entry in profile.Relations ?? new Dictionary<string, Relation>())
            {
                Relation relation = entry.Value ?? new Relation();
                string key = !string.IsNullOrWhiteSpace(relation.NAME)
                    ? relation.NAME
                    : (entry.Key ?? string.Empty);
                if (string.IsNullOrWhiteSpace(key))
                    continue;
                key = MakeUniqueKey(relations, key.Trim());
                NormalizeRelation(relation, key);
                relations.Add(key, relation);
            }

            Dictionary<string, Bind> binds = CreateBindMap();
            foreach (KeyValuePair<string, Bind> entry in profile.Binds ?? new Dictionary<string, Bind>())
            {
                Bind bind = entry.Value ?? new Bind();
                string key = string.IsNullOrWhiteSpace(entry.Key) ? string.Empty : entry.Key.Trim();
                if (string.IsNullOrWhiteSpace(key))
                    key = bind.Rl != null && !string.IsNullOrWhiteSpace(bind.Rl.NAME) ? bind.Rl.NAME.Trim() : string.Empty;
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (!relations.ContainsKey(key))
                {
                    Relation relation = new Relation();
                    relation.NAME = key;
                    relation.ISAXIS = !string.IsNullOrWhiteSpace(bind.JAxis);
                    relations.Add(key, relation);
                }

                NormalizeBind(bind, relations[key]);
                binds[key] = bind;
                relations[key].bind = bind;
            }

            AllRelations = relations;
            AllBinds = binds;

            JoystickAliases = profile.JoystickAliases == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(profile.JoystickAliases, StringComparer.OrdinalIgnoreCase);
            JoystickFileImages = profile.JoystickFileImages == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(profile.JoystickFileImages, StringComparer.OrdinalIgnoreCase);
            JoystickLayoutExport = profile.JoystickLayoutExport ?? string.Empty;
            PlaneAliases = profile.PlaneAliases == null
                ? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Dictionary<string, string>>(profile.PlaneAliases, StringComparer.OrdinalIgnoreCase);
            LocalJoystickPGUID = profile.JoysticksPGuids == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(profile.JoysticksPGuids, StringComparer.OrdinalIgnoreCase);
            ModifierNameChanges = profile.modifierNameChanges == null
                ? new List<KeyValuePair<string, string>>()
                : new List<KeyValuePair<string, string>>(profile.modifierNameChanges);
            JoystickFFB = profile.JoystickFFB == null
                ? new Dictionary<string, ForceFeedbackS>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ForceFeedbackS>(profile.JoystickFFB, StringComparer.OrdinalIgnoreCase);
            FfbOverrides = profile.FfbOverrides == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(profile.FfbOverrides, StringComparer.OrdinalIgnoreCase);
            JoyPro.Engine.Games.LegacyGameIds.MigrateGameSettings(FfbOverrides);
            AxisCalibrations = profile.AxisCalibrations == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(profile.AxisCalibrations, StringComparer.OrdinalIgnoreCase);
            VirtualJoystickConfig = profile.VirtualJoystick ?? string.Empty;

            LocalJoysticks = AllBinds.Values
                .Where(b => b != null && !string.IsNullOrWhiteSpace(b.Joystick))
                .Select(b => b.Joystick)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v)
                .ToArray();

            ResyncBindsToMods();
            if (runCorrections)
                ApplyLegacyLoadCorrections(autoAddDbItems);
            ResyncRelations();
            RecalcFigures();
        }

        private static string MakeUniqueKey(Dictionary<string, Relation> map, string baseKey)
        {
            string key = string.IsNullOrWhiteSpace(baseKey) ? "Relation" : baseKey;
            int count = 1;
            while (map.ContainsKey(key))
            {
                key = baseKey + "__" + count;
                count++;
            }
            return key;
        }

        private static void NormalizeRelation(Relation relation, string key)
        {
            relation.NAME = key;
            if (relation.NODES == null)
                relation.NODES = new List<RelationItem>();
            if (relation.Groups == null)
                relation.Groups = new List<string>();

            for (int i = relation.NODES.Count - 1; i >= 0; i--)
            {
                RelationItem node = relation.NODES[i];
                if (node == null || string.IsNullOrWhiteSpace(node.ID))
                {
                    relation.NODES.RemoveAt(i);
                    continue;
                }

                node.Game = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game;
                if (node.AIRCRAFT == null)
                    node.AIRCRAFT = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                MigratePlanelessNodeAircraft(node);
                if (node.AllInputs == null)
                    node.AllInputs = new DCSInput[0];
                if (node.OtherInputs == null)
                    node.OtherInputs = new OtherGameInput[0];
            }

            // Games split into versions (MSFS, XPlane): one node per version.
            JoyPro.Engine.Games.LegacyGameIds.MigrateRelation(relation);
            relation.RecalculateElementCount();
        }

        // Games without aircraft (IL-2, Star Citizen, the registry games) keep their node state under the game name,
        // as the engine's importers write it. Older GUI builds wrote "Default" instead; that key is renamed.
        internal static void MigratePlanelessNodeAircraft(RelationItem node)
        {
            if (node == null || node.AIRCRAFT == null || string.Equals(node.Game, "DCS", StringComparison.OrdinalIgnoreCase))
                return;
            string legacyKey = node.AIRCRAFT.Keys.FirstOrDefault(k => string.Equals(k, "Default", StringComparison.OrdinalIgnoreCase));
            if (legacyKey == null)
                return;
            bool value = node.AIRCRAFT[legacyKey];
            node.AIRCRAFT.Remove(legacyKey);
            string gameKey = node.AIRCRAFT.Keys.FirstOrDefault(k => string.Equals(k, node.Game, StringComparison.OrdinalIgnoreCase));
            if (gameKey == null)
                node.AIRCRAFT[node.Game] = value;
            else
                node.AIRCRAFT[gameKey] = node.AIRCRAFT[gameKey] || value;
        }

        private static void NormalizeBind(Bind bind, Relation relation)
        {
            bind.Joystick = NormalizeJoystickDeviceName(bind.Joystick ?? string.Empty);
            bind.JAxis = bind.JAxis ?? string.Empty;
            bind.JButton = bind.JButton ?? string.Empty;
            bind.Reformer_depr = bind.Reformer_depr ?? string.Empty;
            bind.Curvature = bind.Curvature == null || bind.Curvature.Count == 0
                ? new List<double> { 0.0 }
                : new List<double>(bind.Curvature);
            bind.AllReformers = bind.AllReformers == null
                ? new List<string>()
                : bind.AllReformers.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            bind.AdditionalImportInfo = bind.AdditionalImportInfo ?? string.Empty;
            bind.AliasJoystick = bind.AliasJoystick ?? string.Empty;
            bind.Rl = relation;
            JoyPro.Engine.Games.LegacyGameIds.MigrateTuningScopes(bind.TuningOverrides);
            bind.JoyBackup = string.IsNullOrWhiteSpace(bind.JoyBackup) ? bind.Joystick : bind.JoyBackup;
        }

        private static void RebuildGroupList()
        {
            HashSet<string> groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> entry in AllRelations)
            {
                Relation relation = entry.Value;
                if (relation == null || relation.Groups == null)
                    continue;
                for (int i = 0; i < relation.Groups.Count; i++)
                {
                    string group = relation.Groups[i];
                    if (!string.IsNullOrWhiteSpace(group))
                        groups.Add(group.Trim());
                }
            }

            AllGroups = groups.OrderBy(v => v).ToList();
            GroupActivity = AllGroups.ToDictionary(g => g, g => true, StringComparer.OrdinalIgnoreCase);
        }

        private static void RebuildJoystickActivity()
        {
            Dictionary<string, bool> activity = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Bind> entry in AllBinds)
            {
                Bind bind = entry.Value;
                if (bind == null || string.IsNullOrWhiteSpace(bind.Joystick))
                    continue;
                activity[bind.Joystick] = true;
            }

            JoystickActivity = activity;
            LocalJoysticks = activity.Keys.OrderBy(v => v).ToArray();
        }

        private sealed class DbCorrectionIndex
        {
            public List<DCSInput> AllDcsInputs { get; private set; }
            public Dictionary<string, List<DCSInput>> DcsById { get; private set; }
            public Dictionary<string, List<OtherGameInput>> AllOtherInputsByGame { get; private set; }
            public Dictionary<string, Dictionary<string, List<OtherGameInput>>> OtherByGameAndId { get; private set; }

            public DbCorrectionIndex()
            {
                AllDcsInputs = new List<DCSInput>();
                DcsById = new Dictionary<string, List<DCSInput>>(StringComparer.OrdinalIgnoreCase);
                AllOtherInputsByGame = new Dictionary<string, List<OtherGameInput>>(StringComparer.OrdinalIgnoreCase);
                OtherByGameAndId = new Dictionary<string, Dictionary<string, List<OtherGameInput>>>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private sealed class CurrentBindInfo
        {
            public bool Found { get; set; }
            public string Joystick { get; set; }
            public string ModButton { get; set; }

            public CurrentBindInfo()
            {
                Found = false;
                Joystick = string.Empty;
                ModButton = string.Empty;
            }
        }

        private sealed class LoadDeviceCandidate
        {
            public string Name { get; set; }
            public string Source { get; set; }
            public int Priority { get; set; }

            public LoadDeviceCandidate()
            {
                Name = string.Empty;
                Source = string.Empty;
                Priority = int.MaxValue;
            }
        }

        private sealed class RelationReplacementDecision<T>
            where T : class
        {
            public bool Apply { get; set; }
            public T Candidate { get; set; }
            public int Score { get; set; }
            public string Confidence { get; set; }
            public string Action { get; set; }
            public string Reason { get; set; }
            public bool UsedPlaneFamilyAlias { get; set; }
            public bool PlaneMissingInDb { get; set; }

            public RelationReplacementDecision()
            {
                Apply = false;
                Candidate = null;
                Score = 0;
                Confidence = "low";
                Action = "skipped";
                Reason = string.Empty;
                UsedPlaneFamilyAlias = false;
                PlaneMissingInDb = false;
            }
        }

        private static void ApplyLegacyLoadCorrections(bool autoAddDbItems)
        {
            DbCorrectionIndex index = BuildDbCorrectionIndex();
            CorrectModifiersInBindsForLoad();

            foreach (KeyValuePair<string, Relation> entry in AllRelations)
            {
                Relation relation = entry.Value;
                if (relation == null)
                    continue;
                CorrectRelationNamesAgainstDb(relation, index, autoAddDbItems);
            }

            AddLoadedJoysticks();
            if (RemapMissingJoysticksOnLoad)
                CorrectMissingJoystickDevicesForLoad(ConfirmMissingJoystickRemapOnLoad);
            CleanJoystickNodes();
        }

        private static void CorrectModifiersInBindsForLoad()
        {
            foreach (KeyValuePair<string, Bind> entry in AllBinds)
            {
                Bind bind = entry.Value;
                if (bind == null)
                    continue;
                if (bind.AllReformers == null)
                    bind.AllReformers = new List<string>();

                for (int i = bind.AllReformers.Count - 1; i >= 0; i--)
                {
                    string reformer = NormalizeReformerDelimiter(bind.AllReformers[i]);
                    if (string.IsNullOrWhiteSpace(reformer))
                    {
                        bind.AllReformers.RemoveAt(i);
                        continue;
                    }

                    Modifier parsed = Modifier.ReformerToMod(reformer);
                    if (parsed == null || string.IsNullOrWhiteSpace(parsed.device) || string.IsNullOrWhiteSpace(parsed.key))
                    {
                        bind.AllReformers.RemoveAt(i);
                        continue;
                    }

                    Modifier existing = GetModifierWithKeyCombo(parsed.device, parsed.key);
                    bind.AllReformers[i] = (existing ?? parsed).toReformerString();
                }
            }
        }

        private static void CorrectRelationNamesAgainstDb(Relation relation, DbCorrectionIndex index, bool autoAddDbItems)
        {
            if (relation == null)
                return;
            if (relation.NODES == null)
                relation.NODES = new List<RelationItem>();

            List<DCSInput> toAddDcs = new List<DCSInput>();
            List<OtherGameInput> toAddOther = new List<OtherGameInput>();
            CurrentBindInfo currentBind = GetCurrentBindInfo(relation);

            for (int i = 0; i < relation.NODES.Count; i++)
            {
                RelationItem item = relation.NODES[i];
                if (item == null)
                    continue;

                List<object> toAdd = CorrectRelationItemAgainstDb(item, relation, index, autoAddDbItems, currentBind);
                for (int j = 0; j < toAdd.Count; j++)
                {
                    object next = toAdd[j];
                    DCSInput dcs = next as DCSInput;
                    if (dcs != null)
                    {
                        toAddDcs.Add(dcs);
                        continue;
                    }

                    OtherGameInput other = next as OtherGameInput;
                    if (other != null)
                        toAddOther.Add(other);
                }
            }

            for (int i = 0; i < toAddDcs.Count; i++)
            {
                DCSInput input = toAddDcs[i];
                bool nothingElseBound = true;
                if (currentBind.Found && autoAddDbItems)
                {
                    string desc = GetDescriptionForJoystickButtonGamePlane(currentBind.Joystick, currentBind.ModButton, "DCS", input.Plane);
                    if (!string.IsNullOrWhiteSpace(desc))
                        nothingElseBound = false;
                }

                if (autoAddDbItems &&
                    GetPlaneRelationState(relation, input.Plane, "DCS", null) < 1 &&
                    nothingElseBound)
                {
                    relation.AddNode(input.ID, "DCS", input.IsAxis, input.Plane);
                }
            }

            for (int i = 0; i < toAddOther.Count; i++)
            {
                OtherGameInput input = toAddOther[i];
                string game = NormalizeGameNameForCorrection(input.Game);
                string plane = string.IsNullOrWhiteSpace(input.Plane) ? game : input.Plane;

                bool nothingElseBound = true;
                if (currentBind.Found && autoAddDbItems)
                {
                    string desc = GetDescriptionForJoystickButtonGamePlane(currentBind.Joystick, currentBind.ModButton, game, plane);
                    if (!string.IsNullOrWhiteSpace(desc))
                        nothingElseBound = false;
                }

                if (autoAddDbItems &&
                    GetPlaneRelationState(relation, plane, game, null) < 1 &&
                    nothingElseBound)
                {
                    relation.AddNode(input.ID, game, input.IsAxis, plane);
                }
            }
        }

        private static List<object> CorrectRelationItemAgainstDb(
            RelationItem item,
            Relation relation,
            DbCorrectionIndex index,
            bool autoAddDbItems,
            CurrentBindInfo currentBind)
        {
            List<object> toReturn = new List<object>();
            if (item == null || relation == null || index == null)
                return toReturn;

            if (item.AIRCRAFT == null)
                item.AIRCRAFT = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            string game = NormalizeGameNameForCorrection(item.Game);
            item.Game = game;

            // No command list of this game on this PC yet (a fresh install ships none and builds it from the installed
            // game): there is nothing to correct against, so the node stays exactly as it is instead of losing its
            // aircraft.
            List<OtherGameInput> knownOfGame;
            bool gameHasDb = game.Equals("DCS", StringComparison.OrdinalIgnoreCase)
                ? index.AllDcsInputs != null && index.AllDcsInputs.Count > 0
                : index.AllOtherInputsByGame.TryGetValue(game, out knownOfGame) && knownOfGame != null && knownOfGame.Count > 0;
            if (!gameHasDb)
                return toReturn;

            if (game.Equals("DCS", StringComparison.OrdinalIgnoreCase))
            {
                List<DCSInput> dbItems = GetDcsInputsById(index, item.ID);
                DCSInput[] currentInputs = item.AllInputs ?? new DCSInput[0];
                List<DCSInput> toKeep = new List<DCSInput>();

                for (int i = currentInputs.Length - 1; i >= 0; i--)
                {
                    DCSInput current = currentInputs[i];
                    if (current == null)
                        continue;
                    string currentPlane = current.Plane ?? string.Empty;

                    if (!IsLoadCorrectionPlaneActive(item, currentPlane, "DCS"))
                    {
                        toKeep.Add(current);
                        continue;
                    }

                    DCSInput replacementById = InputContainsPlane(dbItems, currentPlane);
                    if (replacementById != null)
                    {
                        bool sameTitle = string.Equals(replacementById.Title ?? string.Empty, current.Title ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                        bool sameCategory = string.Equals(replacementById.Cat ?? string.Empty, current.Cat ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                        if (sameTitle && sameCategory)
                            toKeep.Add(current);
                        else
                        {
                            toKeep.Add(replacementById.Copy());
                            RecordLoadCorrectionEntry(new LoadCorrectionReportEntry
                            {
                                Kind = "relation-item-metadata-refresh",
                                Action = "auto",
                                RelationName = relation.NAME ?? string.Empty,
                                Game = "DCS",
                                OldId = item.ID ?? string.Empty,
                                OldTitle = current.Title ?? string.Empty,
                                OldPlane = currentPlane,
                                NewId = replacementById.ID ?? string.Empty,
                                NewTitle = replacementById.Title ?? string.Empty,
                                NewPlane = replacementById.Plane ?? string.Empty,
                                Confidence = "high",
                                Score = 100,
                                Reason = "ID still exists; metadata updated from DB",
                                Details = "old-category=" + (current.Cat ?? string.Empty) + "; new-category=" + (replacementById.Cat ?? string.Empty)
                            });
                        }
                    }
                    else
                    {
                        RelationReplacementDecision<DCSInput> decision =
                            EvaluateDcsReplacementDecision(index, relation, item, current);
                        if (decision.Apply && decision.Candidate != null)
                            toReturn.Add(decision.Candidate.Copy());

                        RecordLoadCorrectionEntry(new LoadCorrectionReportEntry
                        {
                            Kind = "relation-item-remap",
                            Action = decision.Action ?? "skipped",
                            RelationName = relation.NAME ?? string.Empty,
                            Game = "DCS",
                            OldId = item.ID ?? string.Empty,
                            OldTitle = current.Title ?? string.Empty,
                            OldPlane = currentPlane,
                            NewId = decision.Candidate == null ? string.Empty : (decision.Candidate.ID ?? string.Empty),
                            NewTitle = decision.Candidate == null ? string.Empty : (decision.Candidate.Title ?? string.Empty),
                            NewPlane = decision.Candidate == null ? string.Empty : (decision.Candidate.Plane ?? string.Empty),
                            Confidence = decision.Confidence ?? "low",
                            Score = decision.Score,
                            PlaneMissingInDb = decision.PlaneMissingInDb,
                            UsedPlaneFamilyAlias = decision.UsedPlaneFamilyAlias,
                            Reason = decision.Reason ?? string.Empty
                        });

                        if (!string.IsNullOrWhiteSpace(currentPlane) && item.AIRCRAFT.ContainsKey(currentPlane))
                            item.AIRCRAFT.Remove(currentPlane);
                    }
                }

                for (int i = 0; i < dbItems.Count; i++)
                {
                    DCSInput db = dbItems[i];
                    DCSInput inExisting = InputContainsPlane(currentInputs, db.Plane);
                    if (inExisting != null)
                        continue;

                    DCSInput inKeep = InputContainsPlane(toKeep, db.Plane);
                    toKeep.Add(db.Copy());

                    bool nothingElseBound = true;
                    if (currentBind.Found && autoAddDbItems)
                    {
                        string desc = GetDescriptionForJoystickButtonGamePlane(currentBind.Joystick, currentBind.ModButton, "DCS", db.Plane);
                        if (!string.IsNullOrWhiteSpace(desc))
                            nothingElseBound = false;
                    }

                    if (inKeep == null && !item.AIRCRAFT.ContainsKey(db.Plane))
                    {
                        bool canActivate =
                            autoAddDbItems &&
                            GetPlaneRelationState(relation, db.Plane, "DCS", item) < 1 &&
                            nothingElseBound;
                        item.AIRCRAFT[db.Plane] = canActivate;
                    }
                }

                item.AllInputs = toKeep.ToArray();
                item.OtherInputs = item.OtherInputs ?? new OtherGameInput[0];
            }
            else
            {
                List<OtherGameInput> dbItems = GetOtherInputsById(index, game, item.ID);
                OtherGameInput[] currentInputs = item.OtherInputs ?? new OtherGameInput[0];
                List<OtherGameInput> toKeep = new List<OtherGameInput>();

                for (int i = 0; i < currentInputs.Length; i++)
                {
                    OtherGameInput current = currentInputs[i];
                    if (current == null)
                        continue;
                    string currentPlane = string.IsNullOrWhiteSpace(current.Plane) ? game : current.Plane;

                    if (!IsLoadCorrectionPlaneActive(item, currentPlane, game))
                    {
                        toKeep.Add(current);
                        continue;
                    }

                    OtherGameInput replacementById = InputContainsPlane(dbItems, currentPlane);
                    if (replacementById != null)
                    {
                        bool sameTitle = string.Equals(replacementById.Title ?? string.Empty, current.Title ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                        bool sameCategory = string.Equals(replacementById.Category ?? string.Empty, current.Category ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                        if (sameTitle && sameCategory)
                            toKeep.Add(current);
                        else
                        {
                            toKeep.Add(replacementById.Copy());
                            RecordLoadCorrectionEntry(new LoadCorrectionReportEntry
                            {
                                Kind = "relation-item-metadata-refresh",
                                Action = "auto",
                                RelationName = relation.NAME ?? string.Empty,
                                Game = game,
                                OldId = item.ID ?? string.Empty,
                                OldTitle = current.Title ?? string.Empty,
                                OldPlane = currentPlane ?? string.Empty,
                                NewId = replacementById.ID ?? string.Empty,
                                NewTitle = replacementById.Title ?? string.Empty,
                                NewPlane = replacementById.Plane ?? string.Empty,
                                Confidence = "high",
                                Score = 100,
                                Reason = "ID still exists; metadata updated from DB",
                                Details = "old-category=" + (current.Category ?? string.Empty) + "; new-category=" + (replacementById.Category ?? string.Empty)
                            });
                        }
                    }
                    else
                    {
                        RelationReplacementDecision<OtherGameInput> decision =
                            EvaluateOtherReplacementDecision(index, relation, item, current, game, currentPlane);
                        if (decision.Apply && decision.Candidate != null)
                            toReturn.Add(decision.Candidate.Copy());

                        RecordLoadCorrectionEntry(new LoadCorrectionReportEntry
                        {
                            Kind = "relation-item-remap",
                            Action = decision.Action ?? "skipped",
                            RelationName = relation.NAME ?? string.Empty,
                            Game = game,
                            OldId = item.ID ?? string.Empty,
                            OldTitle = current.Title ?? string.Empty,
                            OldPlane = currentPlane ?? string.Empty,
                            NewId = decision.Candidate == null ? string.Empty : (decision.Candidate.ID ?? string.Empty),
                            NewTitle = decision.Candidate == null ? string.Empty : (decision.Candidate.Title ?? string.Empty),
                            NewPlane = decision.Candidate == null ? string.Empty : (decision.Candidate.Plane ?? string.Empty),
                            Confidence = decision.Confidence ?? "low",
                            Score = decision.Score,
                            PlaneMissingInDb = decision.PlaneMissingInDb,
                            UsedPlaneFamilyAlias = decision.UsedPlaneFamilyAlias,
                            Reason = decision.Reason ?? string.Empty
                        });

                        if (item.AIRCRAFT.ContainsKey(currentPlane))
                            item.AIRCRAFT.Remove(currentPlane);
                    }
                }

                for (int i = 0; i < dbItems.Count; i++)
                {
                    OtherGameInput db = dbItems[i];
                    string dbPlane = string.IsNullOrWhiteSpace(db.Plane) ? game : db.Plane;
                    OtherGameInput inExisting = InputContainsPlane(currentInputs, dbPlane);
                    if (inExisting != null)
                        continue;

                    OtherGameInput inKeep = InputContainsPlane(toKeep, dbPlane);
                    toKeep.Add(db.Copy());

                    bool nothingElseBound = true;
                    if (currentBind.Found && autoAddDbItems)
                    {
                        string desc = GetDescriptionForJoystickButtonGamePlane(currentBind.Joystick, currentBind.ModButton, game, dbPlane);
                        if (!string.IsNullOrWhiteSpace(desc))
                            nothingElseBound = false;
                    }

                    if (inKeep == null && !item.AIRCRAFT.ContainsKey(dbPlane))
                    {
                        bool canActivate =
                            autoAddDbItems &&
                            GetPlaneRelationState(relation, dbPlane, game, item) < 1 &&
                            nothingElseBound;
                        item.AIRCRAFT[dbPlane] = canActivate;
                    }
                }

                item.OtherInputs = toKeep.ToArray();
                item.AllInputs = item.AllInputs ?? new DCSInput[0];
            }

            return toReturn;
        }

        private static RelationReplacementDecision<DCSInput> EvaluateDcsReplacementDecision(
            DbCorrectionIndex index,
            Relation relation,
            RelationItem item,
            DCSInput current)
        {
            RelationReplacementDecision<DCSInput> decision = new RelationReplacementDecision<DCSInput>();
            if (index == null || current == null)
            {
                decision.Reason = "missing correction context";
                return decision;
            }

            List<DCSInput> pool = (index.AllDcsInputs ?? new List<DCSInput>())
                .Where(input => input != null && input.IsAxis == current.IsAxis)
                .ToList();
            if (pool.Count == 0)
            {
                decision.Reason = "no DCS candidates with matching input type";
                return decision;
            }

            string oldPlane = current.Plane ?? string.Empty;
            bool planeExistsInDb = IsDcsPlanePresentInDb(index, oldPlane);
            decision.PlaneMissingInDb = !planeExistsInDb && !string.IsNullOrWhiteSpace(oldPlane);

            IEnumerable<DCSInput> filtered = pool;
            if (planeExistsInDb)
            {
                filtered = filtered.Where(input =>
                    string.Equals(input.Plane ?? string.Empty, oldPlane, StringComparison.OrdinalIgnoreCase));
            }

            DCSInput best = null;
            int bestScore = int.MinValue;
            bool bestUsedAlias = false;
            foreach (DCSInput candidate in filtered)
            {
                bool usedAlias;
                int score = ComputeRelationItemScore(
                    current.Title,
                    current.Cat,
                    oldPlane,
                    candidate.Title,
                    candidate.Cat,
                    candidate.Plane,
                    planeExistsInDb,
                    out usedAlias);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                    bestUsedAlias = usedAlias;
                }
            }

            if (best == null)
            {
                decision.Reason = planeExistsInDb
                    ? "no same-plane candidate exists in DB"
                    : "no candidate exists in DB";
                return decision;
            }

            decision.Candidate = best.Copy();
            decision.Score = Math.Max(0, bestScore);
            decision.UsedPlaneFamilyAlias = bestUsedAlias;
            decision.Confidence = ClassifyCorrectionConfidence(decision.Score, out int highThreshold, out int mediumThreshold);
            decision.Reason = "score " + decision.Score + " (high>=" + highThreshold + ", medium>=" + mediumThreshold + ")";

            if (string.Equals(decision.Confidence, "high", StringComparison.OrdinalIgnoreCase))
            {
                decision.Apply = true;
                decision.Action = "auto";
                return decision;
            }

            if (string.Equals(decision.Confidence, "medium", StringComparison.OrdinalIgnoreCase))
            {
                if (ConfirmItemCorrectionsOnLoad)
                {
                    LoadCorrectionSuggestion suggestion = BuildLoadCorrectionSuggestion(
                        relation,
                        "DCS",
                        item,
                        current.Title,
                        oldPlane,
                        best.ID,
                        best.Title,
                        best.Plane,
                        decision.Score,
                        decision.Confidence,
                        decision.Reason);
                    bool confirmed = ConfirmLoadItemCorrection(suggestion);
                    decision.Apply = confirmed;
                    decision.Action = confirmed ? "confirmed" : "skipped";
                    if (!confirmed)
                        decision.Reason = decision.Reason + "; confirmation declined";
                }
                else
                {
                    int? autoThreshold = ItemCorrectionAutoApplyScoreThresholdOnLoad;
                    if (autoThreshold.HasValue && decision.Score < autoThreshold.Value)
                    {
                        decision.Apply = false;
                        decision.Action = "skipped";
                        decision.Reason = decision.Reason + "; below auto-apply threshold (" + autoThreshold.Value + ")";
                    }
                    else
                    {
                        decision.Apply = true;
                        decision.Action = "auto";
                    }
                }
                return decision;
            }

            decision.Apply = false;
            decision.Action = "skipped";
            decision.Reason = decision.Reason + "; below confidence threshold";
            return decision;
        }

        private static RelationReplacementDecision<OtherGameInput> EvaluateOtherReplacementDecision(
            DbCorrectionIndex index,
            Relation relation,
            RelationItem item,
            OtherGameInput current,
            string game,
            string currentPlane)
        {
            RelationReplacementDecision<OtherGameInput> decision = new RelationReplacementDecision<OtherGameInput>();
            if (index == null || current == null)
            {
                decision.Reason = "missing correction context";
                return decision;
            }

            string normalizedGame = NormalizeGameNameForCorrection(game);
            List<OtherGameInput> gamePool;
            if (!index.AllOtherInputsByGame.TryGetValue(normalizedGame, out gamePool))
                gamePool = new List<OtherGameInput>();

            List<OtherGameInput> pool = gamePool
                .Where(input => input != null && input.IsAxis == current.IsAxis)
                .ToList();
            if (pool.Count == 0)
            {
                decision.Reason = "no " + normalizedGame + " candidates with matching input type";
                return decision;
            }

            string oldPlane = string.IsNullOrWhiteSpace(currentPlane) ? normalizedGame : currentPlane;
            bool planeExistsInDb = IsOtherPlanePresentInDb(index, normalizedGame, oldPlane);
            decision.PlaneMissingInDb = !planeExistsInDb && !string.IsNullOrWhiteSpace(oldPlane);

            IEnumerable<OtherGameInput> filtered = pool;
            if (planeExistsInDb)
            {
                filtered = filtered.Where(input =>
                {
                    string candidatePlane = string.IsNullOrWhiteSpace(input.Plane) ? normalizedGame : input.Plane;
                    return string.Equals(candidatePlane, oldPlane, StringComparison.OrdinalIgnoreCase);
                });
            }

            OtherGameInput best = null;
            int bestScore = int.MinValue;
            bool bestUsedAlias = false;
            foreach (OtherGameInput candidate in filtered)
            {
                string candidatePlane = string.IsNullOrWhiteSpace(candidate.Plane) ? normalizedGame : candidate.Plane;
                bool usedAlias;
                int score = ComputeRelationItemScore(
                    current.Title,
                    current.Category,
                    oldPlane,
                    candidate.Title,
                    candidate.Category,
                    candidatePlane,
                    planeExistsInDb,
                    out usedAlias);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                    bestUsedAlias = usedAlias;
                }
            }

            if (best == null)
            {
                decision.Reason = planeExistsInDb
                    ? "no same-plane candidate exists in DB"
                    : "no candidate exists in DB";
                return decision;
            }

            OtherGameInput candidateCopy = best.Copy();
            if (string.IsNullOrWhiteSpace(candidateCopy.Game))
                candidateCopy.Game = normalizedGame;
            if (string.IsNullOrWhiteSpace(candidateCopy.Plane))
                candidateCopy.Plane = normalizedGame;

            decision.Candidate = candidateCopy;
            decision.Score = Math.Max(0, bestScore);
            decision.UsedPlaneFamilyAlias = bestUsedAlias;
            decision.Confidence = ClassifyCorrectionConfidence(decision.Score, out int highThreshold, out int mediumThreshold);
            decision.Reason = "score " + decision.Score + " (high>=" + highThreshold + ", medium>=" + mediumThreshold + ")";

            if (string.Equals(decision.Confidence, "high", StringComparison.OrdinalIgnoreCase))
            {
                decision.Apply = true;
                decision.Action = "auto";
                return decision;
            }

            if (string.Equals(decision.Confidence, "medium", StringComparison.OrdinalIgnoreCase))
            {
                if (ConfirmItemCorrectionsOnLoad)
                {
                    LoadCorrectionSuggestion suggestion = BuildLoadCorrectionSuggestion(
                        relation,
                        normalizedGame,
                        item,
                        current.Title,
                        oldPlane,
                        candidateCopy.ID,
                        candidateCopy.Title,
                        candidateCopy.Plane,
                        decision.Score,
                        decision.Confidence,
                        decision.Reason);
                    bool confirmed = ConfirmLoadItemCorrection(suggestion);
                    decision.Apply = confirmed;
                    decision.Action = confirmed ? "confirmed" : "skipped";
                    if (!confirmed)
                        decision.Reason = decision.Reason + "; confirmation declined";
                }
                else
                {
                    int? autoThreshold = ItemCorrectionAutoApplyScoreThresholdOnLoad;
                    if (autoThreshold.HasValue && decision.Score < autoThreshold.Value)
                    {
                        decision.Apply = false;
                        decision.Action = "skipped";
                        decision.Reason = decision.Reason + "; below auto-apply threshold (" + autoThreshold.Value + ")";
                    }
                    else
                    {
                        decision.Apply = true;
                        decision.Action = "auto";
                    }
                }
                return decision;
            }

            decision.Apply = false;
            decision.Action = "skipped";
            decision.Reason = decision.Reason + "; below confidence threshold";
            return decision;
        }

        private static LoadCorrectionSuggestion BuildLoadCorrectionSuggestion(
            Relation relation,
            string game,
            RelationItem item,
            string oldTitle,
            string oldPlane,
            string newId,
            string newTitle,
            string newPlane,
            int score,
            string confidence,
            string reason)
        {
            return new LoadCorrectionSuggestion
            {
                RelationName = relation == null ? string.Empty : (relation.NAME ?? string.Empty),
                Game = game ?? string.Empty,
                OldId = item == null ? string.Empty : (item.ID ?? string.Empty),
                OldTitle = oldTitle ?? string.Empty,
                OldPlane = oldPlane ?? string.Empty,
                NewId = newId ?? string.Empty,
                NewTitle = newTitle ?? string.Empty,
                NewPlane = newPlane ?? string.Empty,
                Score = score,
                Confidence = confidence ?? string.Empty,
                Mode = ItemCorrectionModeOnLoad.ToString().ToLowerInvariant(),
                Reason = reason ?? string.Empty
            };
        }

        private static bool ConfirmLoadItemCorrection(LoadCorrectionSuggestion suggestion)
        {
            Func<LoadCorrectionSuggestion, bool> callback = LoadItemCorrectionConfirmation;
            if (callback == null)
                return false;

            try
            {
                return callback(suggestion ?? new LoadCorrectionSuggestion());
            }
            catch
            {
                return false;
            }
        }

        private static string ClassifyCorrectionConfidence(int score, out int highThreshold, out int mediumThreshold)
        {
            switch (ItemCorrectionModeOnLoad)
            {
                case LoadItemCorrectionMode.Strict:
                    highThreshold = 100;
                    mediumThreshold = 101;
                    break;
                case LoadItemCorrectionMode.Aggressive:
                    highThreshold = 90;
                    mediumThreshold = 72;
                    break;
                default:
                    highThreshold = 95;
                    mediumThreshold = 80;
                    break;
            }

            if (ItemCorrectionAutoApplyScoreThresholdOnLoad.HasValue)
            {
                int custom = ItemCorrectionAutoApplyScoreThresholdOnLoad.Value;
                if (custom < 0)
                    custom = 0;
                if (custom > 200)
                    custom = 200;
                highThreshold = custom;
            }

            if (score >= highThreshold)
                return "high";
            if (score >= mediumThreshold)
                return "medium";
            return "low";
        }

        private static int ComputeRelationItemScore(
            string oldTitle,
            string oldCategory,
            string oldPlane,
            string candidateTitle,
            string candidateCategory,
            string candidatePlane,
            bool planeExistsInDb,
            out bool usedPlaneFamilyAlias)
        {
            usedPlaneFamilyAlias = false;
            int titleScore = ComputeTitleSimilarityScore(oldTitle, candidateTitle);
            if (titleScore < 35)
                return int.MinValue;

            int score = titleScore;

            string oldPlaneNorm = oldPlane ?? string.Empty;
            string candidatePlaneNorm = candidatePlane ?? string.Empty;
            if (planeExistsInDb)
            {
                if (!string.Equals(oldPlaneNorm, candidatePlaneNorm, StringComparison.OrdinalIgnoreCase))
                    return int.MinValue;
                score += 30;
            }
            else
            {
                if (string.Equals(oldPlaneNorm, candidatePlaneNorm, StringComparison.OrdinalIgnoreCase))
                {
                    score += 20;
                }
                else if (ArePlaneFamilyEquivalent(oldPlaneNorm, candidatePlaneNorm))
                {
                    score += 12;
                    usedPlaneFamilyAlias = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(oldCategory) &&
                !string.IsNullOrWhiteSpace(candidateCategory) &&
                string.Equals(oldCategory.Trim(), candidateCategory.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                score += 8;
            }

            return score;
        }

        private static int ComputeTitleSimilarityScore(string left, string right)
        {
            string a = NormalizeTextTokenForSimilarity(left);
            string b = NormalizeTextTokenForSimilarity(right);
            if (a.Length == 0 || b.Length == 0)
                return 0;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return 70;

            int score = 0;
            if (a.Contains(b) || b.Contains(a))
                score = Math.Max(score, 58 - Math.Abs(a.Length - b.Length));

            int maxLen = Math.Max(a.Length, b.Length);
            int dist = LoadLevenshteinDistance(a, b);
            int distanceScore = Math.Max(0, 70 - ((dist * 70) / Math.Max(1, maxLen)));
            score = Math.Max(score, distanceScore);

            // "Lights +" must never become "Lights -" (or Up/Down, On/Off ...): opposite commands are no candidates.
            if (HaveOppositeDirections(left, right))
                score = Math.Min(score, 30);
            return score;
        }

        private static readonly string[][] OppositeDirectionWords =
        {
            new[] { "PLUS", "MINUS" }, new[] { "UP", "DOWN" }, new[] { "LEFT", "RIGHT" }, new[] { "ON", "OFF" },
            new[] { "INCREASE", "DECREASE" }, new[] { "INC", "DEC" }, new[] { "CW", "CCW" }, new[] { "OPEN", "CLOSE" },
            new[] { "EXTEND", "RETRACT" }, new[] { "FWD", "AFT" }, new[] { "FORWARD", "AFT" }, new[] { "FORWARD", "BACK" },
            new[] { "NEXT", "PREV" }, new[] { "NEXT", "PREVIOUS" }, new[] { "IN", "OUT" }, new[] { "ARM", "SAFE" },
            new[] { "ENGAGE", "DISENGAGE" }, new[] { "START", "STOP" }, new[] { "LOCK", "UNLOCK" }
        };

        internal static bool HaveOppositeDirections(string left, string right)
        {
            HashSet<string> a = DirectionWords(left);
            HashSet<string> b = DirectionWords(right);
            foreach (string[] pair in OppositeDirectionWords)
            {
                bool aFirst = a.Contains(pair[0]) && !a.Contains(pair[1]);
                bool aSecond = a.Contains(pair[1]) && !a.Contains(pair[0]);
                bool bFirst = b.Contains(pair[0]) && !b.Contains(pair[1]);
                bool bSecond = b.Contains(pair[1]) && !b.Contains(pair[0]);
                if ((aFirst && bSecond) || (aSecond && bFirst))
                    return true;
            }
            return false;
        }

        private static HashSet<string> DirectionWords(string value)
        {
            HashSet<string> words = new HashSet<string>(StringComparer.Ordinal);
            string raw = value ?? string.Empty;
            StringBuilder word = new StringBuilder();
            for (int i = 0; i <= raw.Length; i++)
            {
                char ch = i < raw.Length ? char.ToUpperInvariant(raw[i]) : ' ';
                if (char.IsLetterOrDigit(ch))
                {
                    word.Append(ch);
                    continue;
                }
                if (word.Length > 0)
                {
                    words.Add(word.ToString());
                    word.Clear();
                }
                if (i < raw.Length && ch == '+')
                    words.Add("PLUS");
                else if (i < raw.Length && ch == '-' && IsSignMinus(raw, i))
                    words.Add("MINUS");
            }
            return words;
        }

        // A '-' is a minus sign at the end of a title or before '/', ')' or ',' ("Lights -", "Range -/+"),
        // not the separator in "Switch - ON".
        private static bool IsSignMinus(string raw, int index)
        {
            int next = index + 1;
            while (next < raw.Length && raw[next] == ' ')
                next++;
            return next >= raw.Length || raw[next] == '/' || raw[next] == ')' || raw[next] == ',';
        }

        private static string NormalizeTextTokenForSimilarity(string value)
        {
            string raw = (value ?? string.Empty).Trim();
            if (raw.Length == 0)
                return string.Empty;

            StringBuilder sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char ch = char.ToUpperInvariant(raw[i]);
                if (char.IsLetterOrDigit(ch))
                    sb.Append(ch);
                else if (ch == '+')
                    sb.Append("PLUS");
                else if (ch == '-' && IsSignMinus(raw, i))
                    sb.Append("MINUS");
            }
            return sb.ToString();
        }

        private static bool IsDcsPlanePresentInDb(DbCorrectionIndex index, string plane)
        {
            if (index == null || string.IsNullOrWhiteSpace(plane))
                return false;

            for (int i = 0; i < index.AllDcsInputs.Count; i++)
            {
                DCSInput input = index.AllDcsInputs[i];
                if (input == null)
                    continue;
                if (string.Equals(input.Plane ?? string.Empty, plane, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsOtherPlanePresentInDb(DbCorrectionIndex index, string game, string plane)
        {
            if (index == null || string.IsNullOrWhiteSpace(plane))
                return false;

            string normalizedGame = NormalizeGameNameForCorrection(game);
            List<OtherGameInput> pool;
            if (!index.AllOtherInputsByGame.TryGetValue(normalizedGame, out pool))
                return false;

            for (int i = 0; i < pool.Count; i++)
            {
                OtherGameInput input = pool[i];
                if (input == null)
                    continue;
                string inputPlane = string.IsNullOrWhiteSpace(input.Plane) ? normalizedGame : input.Plane;
                if (string.Equals(inputPlane, plane, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool ArePlaneFamilyEquivalent(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            HashSet<string> leftKeys = BuildPlaneFamilyKeys(left);
            HashSet<string> rightKeys = BuildPlaneFamilyKeys(right);
            if (leftKeys.Count == 0 || rightKeys.Count == 0)
                return false;

            return leftKeys.Overlaps(rightKeys);
        }

        private static HashSet<string> BuildPlaneFamilyKeys(string plane)
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> tokens = TokenizePlaneName(plane)
                .Select(NormalizePlaneToken)
                .Where(token => token.Length > 0 && !PlaneFamilyStopTokens.Contains(token))
                .ToList();

            if (tokens.Count == 0)
                return keys;

            keys.Add(string.Join("_", tokens));
            if (tokens.Count > 1 && IsNumericToken(tokens[tokens.Count - 1]))
                keys.Add(string.Join("_", tokens.Take(tokens.Count - 1)));

            return keys;
        }

        private static List<string> TokenizePlaneName(string plane)
        {
            string raw = (plane ?? string.Empty).ToUpperInvariant();
            List<string> tokens = new List<string>();
            StringBuilder current = new StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                char ch = raw[i];
                if (char.IsLetterOrDigit(ch))
                {
                    current.Append(ch);
                }
                else if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Length = 0;
                }
            }

            if (current.Length > 0)
                tokens.Add(current.ToString());

            return tokens;
        }

        private static string NormalizePlaneToken(string token)
        {
            string value = (token ?? string.Empty).Trim().ToUpperInvariant();
            switch (value)
            {
                case "II": return "2";
                case "III": return "3";
                case "IV": return "4";
                case "V": return "5";
                default: return value;
            }
        }

        private static bool IsNumericToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;
            for (int i = 0; i < token.Length; i++)
            {
                if (!char.IsDigit(token[i]))
                    return false;
            }
            return true;
        }

        private static CurrentBindInfo GetCurrentBindInfo(Relation relation)
        {
            CurrentBindInfo result = new CurrentBindInfo();
            if (relation == null || relation.bind == null)
                return result;

            Bind bind = relation.bind;
            if (string.IsNullOrWhiteSpace(bind.Joystick))
                return result;

            string token = BuildBindTokenForConflictCheck(bind);
            if (string.IsNullOrWhiteSpace(token))
                return result;

            result.Found = true;
            result.Joystick = bind.Joystick;
            result.ModButton = token;
            return result;
        }

        private static string BuildBindTokenForConflictCheck(Bind bind)
        {
            if (bind == null)
                return string.Empty;

            string input = string.Empty;
            if (bind.Rl != null && bind.Rl.ISAXIS)
                input = bind.JAxis ?? string.Empty;
            else
                input = bind.JButton ?? string.Empty;

            if (string.IsNullOrWhiteSpace(input))
            {
                if (!string.IsNullOrWhiteSpace(bind.JButton))
                    input = bind.JButton;
                else
                    input = bind.JAxis ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            List<string> modifiers = new List<string>();
            if (bind.AllReformers != null)
            {
                for (int i = 0; i < bind.AllReformers.Count; i++)
                {
                    string reformer = NormalizeReformerDelimiter(bind.AllReformers[i]);
                    if (string.IsNullOrWhiteSpace(reformer))
                        continue;

                    int split = reformer.IndexOf('§');
                    string name = split > 0 ? reformer.Substring(0, split) : reformer;
                    name = name.Trim();
                    if (name.Length == 0)
                        continue;
                    if (!modifiers.Contains(name, StringComparer.OrdinalIgnoreCase))
                        modifiers.Add(name);
                }
            }

            if (modifiers.Count == 0)
                return input;

            modifiers = modifiers.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            return string.Join("+", modifiers) + "+" + input;
        }

        private static string GetDescriptionForJoystickButtonGamePlane(string joystick, string modButton, string game, string plane)
        {
            if (string.IsNullOrWhiteSpace(joystick) ||
                string.IsNullOrWhiteSpace(modButton) ||
                string.IsNullOrWhiteSpace(plane))
            {
                return string.Empty;
            }

            string normalizedGame = NormalizeGameNameForCorrection(game);
            foreach (KeyValuePair<string, Bind> entry in AllBinds)
            {
                Bind bind = entry.Value;
                if (bind == null || bind.Rl == null)
                    continue;
                if (!string.Equals(bind.Joystick ?? string.Empty, joystick, StringComparison.OrdinalIgnoreCase))
                    continue;

                string bindToken = BuildBindTokenForConflictCheck(bind);
                if (!string.Equals(bindToken, modButton, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (RelationHasActivePlane(bind.Rl, normalizedGame, plane))
                    return bind.Rl.NAME ?? string.Empty;
            }

            return string.Empty;
        }

        private static bool RelationHasActivePlane(Relation relation, string game, string plane)
        {
            if (relation == null || relation.NODES == null || string.IsNullOrWhiteSpace(plane))
                return false;

            string normalizedGame = NormalizeGameNameForCorrection(game);
            for (int i = 0; i < relation.NODES.Count; i++)
            {
                RelationItem node = relation.NODES[i];
                if (node == null || node.AIRCRAFT == null)
                    continue;
                if (!string.Equals(NormalizeGameNameForCorrection(node.Game), normalizedGame, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (node.AIRCRAFT.ContainsKey(plane) && node.AIRCRAFT[plane])
                    return true;
            }

            return false;
        }

        private static bool IsLoadCorrectionPlaneActive(RelationItem item, string plane, string game)
        {
            if (item == null)
                return false;
            if (item.AIRCRAFT == null || item.AIRCRAFT.Count == 0)
                return true;

            string normalizedPlane = string.IsNullOrWhiteSpace(plane) ? string.Empty : plane.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedPlane) &&
                item.AIRCRAFT.ContainsKey(normalizedPlane))
            {
                return item.AIRCRAFT[normalizedPlane];
            }

            string normalizedGame = NormalizeGameNameForCorrection(game);
            if (!string.IsNullOrWhiteSpace(normalizedGame) &&
                item.AIRCRAFT.ContainsKey(normalizedGame))
            {
                return item.AIRCRAFT[normalizedGame];
            }

            return false;
        }

        private static int GetPlaneRelationState(Relation relation, string plane, string game, RelationItem toIgnore)
        {
            if (relation == null || relation.NODES == null || string.IsNullOrWhiteSpace(plane))
                return 0;

            int counter = 0;
            string normalizedGame = NormalizeGameNameForCorrection(game);
            for (int i = 0; i < relation.NODES.Count; i++)
            {
                RelationItem node = relation.NODES[i];
                if (node == null || object.ReferenceEquals(node, toIgnore))
                    continue;

                if (!string.Equals(NormalizeGameNameForCorrection(node.Game), normalizedGame, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (node.GetStateAircraft(plane) == PlaneState.ACTIVE)
                    counter++;
            }

            return counter;
        }

        private static void AddLoadedJoysticks()
        {
            List<string> sticks = new List<string>();
            if (LocalJoysticks != null)
            {
                for (int i = 0; i < LocalJoysticks.Length; i++)
                {
                    string joystick = NormalizeJoystickDeviceName(LocalJoysticks[i]);
                    AddUniqueCaseInsensitive(sticks, joystick);
                }
            }

            foreach (KeyValuePair<string, Bind> entry in AllBinds)
            {
                Bind bind = entry.Value;
                if (bind == null)
                    continue;
                string joystick = NormalizeJoystickDeviceName(bind.Joystick);
                if (!string.IsNullOrWhiteSpace(joystick))
                    AddUniqueCaseInsensitive(sticks, joystick);
            }

            foreach (KeyValuePair<string, string> entry in LocalJoystickPGUID)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key))
                    AddUniqueCaseInsensitive(sticks, NormalizeJoystickDeviceName(entry.Key));
            }

            foreach (KeyValuePair<string, string> entry in JoystickFileImages)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key))
                    AddUniqueCaseInsensitive(sticks, NormalizeJoystickDeviceName(entry.Key));
            }

            foreach (KeyValuePair<string, Modifier> entry in AllModifiers)
            {
                Modifier modifier = entry.Value;
                if (modifier == null || string.IsNullOrWhiteSpace(modifier.device))
                    continue;
                AddUniqueCaseInsensitive(sticks, NormalizeJoystickDeviceName(modifier.device));
            }

            LocalJoysticks = sticks.ToArray();
        }

        // A profile device that is not connected gets the connected device of the same product with a new instance GUID
        // (same name or USB id, not used in the profile yet, exactly one pair: engine devices auto-remap). A device of
        // another product is never picked on its own; that is Replace device, with a preview. (Before, the most similar
        // name among all connected devices was taken, even another product.)
        private static void CorrectMissingJoystickDevicesForLoad(bool confirmSuggestions)
        {
            List<string> warnings = new List<string>();
            JoyPro.Engine.Games.GameDeviceDirectory directory = JoyPro.Engine.Games.GameDeviceDirectory.Discover(warnings);
            if (directory.Devices.Count(d => d.Connected) == 0)
            {
                ReportLoadCorrectionMessage("Load correction: no connected joystick devices were detected; skipped missing-device remap.");
                RecordLoadCorrectionEntry(new LoadCorrectionReportEntry { Kind = "device-remap", Action = "skipped", Confidence = "low", Reason = "no connected joystick devices were detected" });
                return;
            }
            Program.AutoRemapResult remaps = Program.FindAutoRemaps(directory, warnings);
            foreach (Program.AutoRemapSkip skip in remaps.Skipped)
            {
                ReportLoadCorrectionMessage("Load correction: not remapped '" + skip.OldDevice + "': " + skip.Reason + ".");
                RecordLoadCorrectionEntry(new LoadCorrectionReportEntry { Kind = "device-remap", Action = "skipped", Confidence = "medium", OldTitle = skip.OldDevice, Reason = skip.Reason, Details = string.Join(", ", skip.Candidates) });
            }
            int replaced = 0;
            foreach (Program.AutoRemapEntry entry in remaps.Remapped)
            {
                LoadDeviceCandidate candidate = new LoadDeviceCandidate { Name = entry.NewDevice, Source = entry.Reason, Priority = 0 };
                if (confirmSuggestions && !ConfirmLoadDeviceRemap(entry.OldDevice, candidate))
                {
                    ReportLoadCorrectionMessage("Load correction: skipped replacement for '" + entry.OldDevice + "'.");
                    RecordLoadCorrectionEntry(new LoadCorrectionReportEntry { Kind = "device-remap", Action = "skipped", Confidence = "high", OldTitle = entry.OldDevice, NewTitle = entry.NewDevice, Reason = "replacement suggestion was not confirmed", Details = entry.Reason });
                    continue;
                }
                Program.ApplyAutoRemap(entry, directory);
                replaced++;
                ReportLoadCorrectionMessage("Load correction: replaced missing device '" + entry.OldDevice + "' with '" + entry.NewDevice + "' (" + entry.Reason + ").");
                RecordLoadCorrectionEntry(new LoadCorrectionReportEntry { Kind = "device-remap", Action = confirmSuggestions ? "confirmed" : "auto", Confidence = "high", OldTitle = entry.OldDevice, NewTitle = entry.NewDevice, Reason = "missing profile device remapped (same product, new GUID)", Details = entry.Reason });
            }
            if (replaced > 0)
                AddLoadedJoysticks();
        }

        private static bool ConfirmLoadDeviceRemap(string missingDevice, LoadDeviceCandidate candidate)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Name))
                return false;

            Func<string, string, string, bool> callback = LoadDeviceRemapConfirmation;
            if (callback == null)
                return false;

            try
            {
                return callback(missingDevice, candidate.Name, candidate.Source ?? string.Empty);
            }
            catch
            {
                return false;
            }
        }

        private static void ReportLoadCorrectionMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            Action<string> sink = LoadCorrectionMessageSink;
            if (sink == null)
                return;

            try
            {
                sink(message);
            }
            catch
            {
                // Ignore message sink failures during correction flow.
            }
        }

        private static void BeginLoadCorrectionReportSession(bool correctionsEnabled, bool autoAddDbItems)
        {
            LastLoadCorrectionRunUtc = DateTime.UtcNow;
            LastLoadCorrectionsEnabled = correctionsEnabled;
            LastAutoAddDbItemsEnabled = autoAddDbItems;
            LastRemapMissingDevicesEnabled = RemapMissingJoysticksOnLoad;
            LastConfirmDeviceRemapEnabled = ConfirmMissingJoystickRemapOnLoad;
            LastConfirmItemCorrectionsEnabled = ConfirmItemCorrectionsOnLoad;
            LastItemCorrectionMode = ItemCorrectionModeOnLoad.ToString().ToLowerInvariant();
            LastItemCorrectionAutoApplyScoreThreshold = ItemCorrectionAutoApplyScoreThresholdOnLoad;
            LastLoadCorrectionEntries = new List<LoadCorrectionReportEntry>();
        }

        private static void RecordLoadCorrectionEntry(LoadCorrectionReportEntry entry)
        {
            if (entry == null)
                return;

            if (LastLoadCorrectionEntries == null)
                LastLoadCorrectionEntries = new List<LoadCorrectionReportEntry>();

            LoadCorrectionReportEntry copy = CloneLoadCorrectionReportEntry(entry);
            if (copy.TimestampUtc == DateTime.MinValue)
                copy.TimestampUtc = DateTime.UtcNow;
            LastLoadCorrectionEntries.Add(copy);
        }

        private static LoadCorrectionReportEntry CloneLoadCorrectionReportEntry(LoadCorrectionReportEntry entry)
        {
            if (entry == null)
                return new LoadCorrectionReportEntry();

            return new LoadCorrectionReportEntry
            {
                TimestampUtc = entry.TimestampUtc,
                Kind = entry.Kind ?? string.Empty,
                Action = entry.Action ?? string.Empty,
                RelationName = entry.RelationName ?? string.Empty,
                Game = entry.Game ?? string.Empty,
                OldId = entry.OldId ?? string.Empty,
                OldTitle = entry.OldTitle ?? string.Empty,
                OldPlane = entry.OldPlane ?? string.Empty,
                NewId = entry.NewId ?? string.Empty,
                NewTitle = entry.NewTitle ?? string.Empty,
                NewPlane = entry.NewPlane ?? string.Empty,
                Confidence = entry.Confidence ?? string.Empty,
                Score = entry.Score,
                PlaneMissingInDb = entry.PlaneMissingInDb,
                UsedPlaneFamilyAlias = entry.UsedPlaneFamilyAlias,
                Reason = entry.Reason ?? string.Empty,
                Details = entry.Details ?? string.Empty
            };
        }

        private static HashSet<string> DiscoverConnectedJoysticksForLoad()
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddDirectInputConnectedJoysticksForLoad(result);
            AddWmiConnectedJoysticksForLoad(result);
            return result;
        }

        private static void AddDirectInputConnectedJoysticksForLoad(HashSet<string> result)
        {
            if (result == null)
                return;

            List<string> warnings = new List<string>();
            List<string> devices = DirectInputDeviceDiscovery.EnumerateConnectedDevices(warnings);
            for (int i = 0; i < devices.Count; i++)
            {
                string normalized = NormalizeJoystickDeviceName(devices[i]);
                if (!string.IsNullOrWhiteSpace(normalized) &&
                    !string.Equals(normalized, "Keyboard", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(normalized);
                }
            }

            for (int i = 0; i < warnings.Count; i++)
            {
                ReportLoadCorrectionMessage("Load correction: " + warnings[i]);
            }
        }

        private static void AddWmiConnectedJoysticksForLoad(HashSet<string> result)
        {
            if (result == null)
                return;

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Name, PNPClass, Status FROM Win32_PnPEntity WHERE Status='OK'"))
                using (ManagementObjectCollection collection = searcher.Get())
                {
                    foreach (ManagementObject obj in collection)
                    {
                        string name = Convert.ToString(obj["Name"]) ?? string.Empty;
                        string pnpClass = Convert.ToString(obj["PNPClass"]) ?? string.Empty;
                        if (!IsLikelyJoystickNameForLoad(name, pnpClass))
                            continue;

                        string normalized = NormalizeJoystickDeviceName(name.Trim());
                        if (!string.IsNullOrWhiteSpace(normalized) &&
                            !string.Equals(normalized, "Keyboard", StringComparison.OrdinalIgnoreCase))
                        {
                            result.Add(normalized);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ReportLoadCorrectionMessage("Load correction: connected joystick query failed: " + ex.Message);
            }
        }

        private static bool IsLikelyJoystickNameForLoad(string name, string pnpClass)
        {
            string value = (name ?? string.Empty).Trim();
            if (value.Length == 0)
                return false;

            string upper = value.ToUpperInvariant();
            string cls = (pnpClass ?? string.Empty).ToUpperInvariant();
            if (upper.Contains("HOST CONTROLLER") ||
                upper.Contains("SYSTEM CONTROLLER") ||
                upper.Contains("I2C CONTROLLER") ||
                upper.Contains("GPIO CONTROLLER") ||
                upper.Contains("AUDIO CONTROLLER") ||
                upper.Contains("NVM EXPRESS CONTROLLER") ||
                upper.Contains("AHCI CONTROLLER") ||
                upper.Contains("INTERRUPT CONTROLLER") ||
                upper.Contains("MEMORY ACCESS CONTROLLER") ||
                upper.Contains("STORAGE SPACES CONTROLLER"))
            {
                return false;
            }

            bool inputLikeClass = cls.Length == 0 || cls.Contains("HID") || cls.Contains("USB") || cls.Contains("MEDIA");
            if (!inputLikeClass)
                return false;

            return upper.Contains("JOYSTICK") ||
                   upper.Contains("GAMEPAD") ||
                   upper.Contains("GAME CONTROLLER") ||
                   upper.Contains("HOTAS") ||
                   upper.Contains("THROTTLE") ||
                   upper.Contains("RUDDER") ||
                   upper.Contains("PEDAL") ||
                   upper.Contains("FLIGHT STICK") ||
                   upper.Contains("FLIGHTSTICK") ||
                   upper.Contains("XBOX") ||
                   upper.Contains("VJOY") ||
                   upper.Contains("VKB") ||
                   upper.Contains("WINWING") ||
                   upper.Contains("VIRPIL");
        }

        private static HashSet<string> CollectMissingJoystickNames(HashSet<string> connected)
        {
            HashSet<string> missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> connectedNormalized = new HashSet<string>(
                (connected ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                    .Select(v => NormalizeJoystickDeviceName(v ?? string.Empty))
                    .Where(v => !string.IsNullOrWhiteSpace(v)),
                StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, Bind> entry in AllBinds)
            {
                Bind bind = entry.Value;
                if (bind == null || string.IsNullOrWhiteSpace(bind.Joystick))
                    continue;

                string joystick = NormalizeJoystickDeviceName(bind.Joystick);
                if (string.IsNullOrWhiteSpace(joystick) ||
                    string.Equals(joystick, "Keyboard", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!connectedNormalized.Contains(joystick))
                    missing.Add(joystick);
            }

            foreach (KeyValuePair<string, Modifier> entry in AllModifiers)
            {
                Modifier modifier = entry.Value;
                if (modifier == null || string.IsNullOrWhiteSpace(modifier.device))
                    continue;

                string joystick = NormalizeJoystickDeviceName(modifier.device);
                if (string.IsNullOrWhiteSpace(joystick) ||
                    string.Equals(joystick, "Keyboard", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!connectedNormalized.Contains(joystick))
                    missing.Add(joystick);
            }

            return missing;
        }

        private static List<LoadDeviceCandidate> BuildLoadDeviceCandidates(HashSet<string> connected)
        {
            Dictionary<string, LoadDeviceCandidate> map = new Dictionary<string, LoadDeviceCandidate>(StringComparer.OrdinalIgnoreCase);
            AddLoadDeviceCandidates(map, connected, "connected", 0);
            AddLoadDeviceCandidates(map, BuildProfileJoystickCandidateNames(), "profile", 1);

            return map.Values
                .Where(v => v != null && !string.IsNullOrWhiteSpace(v.Name))
                .OrderBy(v => v.Priority)
                .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> BuildProfileJoystickCandidateNames()
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (LocalJoysticks != null)
            {
                for (int i = 0; i < LocalJoysticks.Length; i++)
                {
                    string joystick = NormalizeJoystickDeviceName(LocalJoysticks[i]);
                    if (!string.IsNullOrWhiteSpace(joystick) &&
                        !string.Equals(joystick, "Keyboard", StringComparison.OrdinalIgnoreCase))
                    {
                        names.Add(joystick);
                    }
                }
            }

            foreach (KeyValuePair<string, Bind> entry in AllBinds)
            {
                Bind bind = entry.Value;
                if (bind == null || string.IsNullOrWhiteSpace(bind.Joystick))
                    continue;

                string joystick = NormalizeJoystickDeviceName(bind.Joystick);
                if (!string.IsNullOrWhiteSpace(joystick) &&
                    !string.Equals(joystick, "Keyboard", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(joystick);
                }
            }

            foreach (KeyValuePair<string, string> entry in LocalJoystickPGUID)
            {
                string joystick = NormalizeJoystickDeviceName(entry.Key);
                if (!string.IsNullOrWhiteSpace(joystick) &&
                    !string.Equals(joystick, "Keyboard", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(joystick);
                }
            }

            foreach (KeyValuePair<string, Modifier> entry in AllModifiers)
            {
                Modifier modifier = entry.Value;
                if (modifier == null || string.IsNullOrWhiteSpace(modifier.device))
                    continue;

                string joystick = NormalizeJoystickDeviceName(modifier.device);
                if (!string.IsNullOrWhiteSpace(joystick) &&
                    !string.Equals(joystick, "Keyboard", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(joystick);
                }
            }

            return names.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddLoadDeviceCandidates(
            Dictionary<string, LoadDeviceCandidate> map,
            IEnumerable<string> names,
            string source,
            int priority)
        {
            if (map == null || names == null)
                return;

            foreach (string raw in names)
            {
                string name = NormalizeJoystickDeviceName(raw ?? string.Empty);
                if (string.IsNullOrWhiteSpace(name) ||
                    string.Equals(name, "Keyboard", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                LoadDeviceCandidate existing;
                if (map.TryGetValue(name, out existing))
                {
                    if (priority < existing.Priority)
                    {
                        existing.Priority = priority;
                        existing.Source = source;
                    }
                    continue;
                }

                LoadDeviceCandidate candidate = new LoadDeviceCandidate();
                candidate.Name = name;
                candidate.Source = source;
                candidate.Priority = priority;
                map[name] = candidate;
            }
        }

        private static LoadDeviceCandidate ChooseBestLoadDeviceCandidate(string requested, IEnumerable<LoadDeviceCandidate> candidates)
        {
            if (candidates == null)
                return null;

            return candidates
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Name))
                .OrderByDescending(c => LoadDeviceSimilarityScore(requested, c.Name))
                .ThenBy(c => c.Priority)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static int LoadDeviceSimilarityScore(string requested, string candidate)
        {
            string reqFull = NormalizeLoadDeviceTokenForSimilarity(requested);
            string reqBase = NormalizeLoadDeviceTokenForSimilarity(StripLoadDeviceGuidSuffix(requested));
            string candFull = NormalizeLoadDeviceTokenForSimilarity(candidate);
            string candBase = NormalizeLoadDeviceTokenForSimilarity(StripLoadDeviceGuidSuffix(candidate));
            if (reqFull.Length == 0 || reqBase.Length == 0 || candFull.Length == 0 || candBase.Length == 0)
                return int.MinValue;

            int score = 0;
            if (string.Equals(reqFull, candFull, StringComparison.OrdinalIgnoreCase))
                score += 100000;
            if (string.Equals(reqBase, candBase, StringComparison.OrdinalIgnoreCase))
                score += 50000;

            if (candBase.Contains(reqBase) || reqBase.Contains(candBase))
                score += 12000 - Math.Abs(candBase.Length - reqBase.Length);

            int distBase = LoadLevenshteinDistance(reqBase, candBase);
            int distFull = LoadLevenshteinDistance(reqFull, candFull);
            score += Math.Max(0, 10000 - (distBase * 250));
            score += Math.Max(0, 2500 - (distFull * 50));
            return score;
        }

        private static string NormalizeLoadDeviceTokenForSimilarity(string value)
        {
            string raw = (value ?? string.Empty).Trim();
            if (raw.Length == 0)
                return string.Empty;

            char[] filtered = raw
                .ToUpperInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray();
            if (filtered.Length == 0)
                return raw.ToUpperInvariant();
            return new string(filtered);
        }

        private static string StripLoadDeviceGuidSuffix(string value)
        {
            string token = NormalizeJoystickDeviceName(value ?? string.Empty).Trim();
            int split = token.LastIndexOf(" {", StringComparison.Ordinal);
            if (split > 0 && token.EndsWith("}", StringComparison.Ordinal))
                return token.Substring(0, split).Trim();
            return token;
        }

        private static int LoadLevenshteinDistance(string left, string right)
        {
            string a = left ?? string.Empty;
            string b = right ?? string.Empty;
            if (a.Length == 0)
                return b.Length;
            if (b.Length == 0)
                return a.Length;

            int[] previous = new int[b.Length + 1];
            int[] current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++)
                previous[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int deletion = previous[j] + 1;
                    int insertion = current[j - 1] + 1;
                    int substitution = previous[j - 1] + cost;
                    current[j] = Math.Min(Math.Min(deletion, insertion), substitution);
                }

                int[] swap = previous;
                previous = current;
                current = swap;
            }

            return previous[b.Length];
        }

        private static void ExchangeSticksInBind(string oldValue, string newValue)
        {
            string oldName = NormalizeJoystickDeviceName(oldValue);
            string replacement = NormalizeJoystickDeviceName(newValue);
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(replacement))
                return;

            foreach (KeyValuePair<string, Bind> entry in AllBinds)
            {
                Bind bind = entry.Value;
                if (bind == null || string.IsNullOrWhiteSpace(bind.Joystick))
                    continue;

                string bindJoystick = NormalizeJoystickDeviceName(bind.Joystick);
                if (string.Equals(bindJoystick, oldName, StringComparison.OrdinalIgnoreCase))
                    bind.Joystick = replacement;
            }
        }

        private static void ExchangeStickInModifiers(string oldValue, string newValue)
        {
            string oldName = NormalizeJoystickDeviceName(oldValue);
            string replacement = NormalizeJoystickDeviceName(newValue);
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(replacement))
                return;

            foreach (KeyValuePair<string, Modifier> entry in AllModifiers)
            {
                Modifier modifier = entry.Value;
                if (modifier == null || string.IsNullOrWhiteSpace(modifier.device))
                    continue;

                string current = NormalizeJoystickDeviceName(modifier.device);
                if (string.Equals(current, oldName, StringComparison.OrdinalIgnoreCase))
                    modifier.device = replacement;
            }

            foreach (KeyValuePair<string, Bind> entry in AllBinds)
            {
                Bind bind = entry.Value;
                if (bind == null || bind.AllReformers == null)
                    continue;

                for (int i = 0; i < bind.AllReformers.Count; i++)
                {
                    string reformer = NormalizeReformerDelimiter(bind.AllReformers[i]);
                    Modifier modifier = Modifier.ReformerToMod(reformer);
                    if (modifier == null || string.IsNullOrWhiteSpace(modifier.device))
                        continue;

                    string current = NormalizeJoystickDeviceName(modifier.device);
                    if (!string.Equals(current, oldName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    modifier.device = replacement;
                    bind.AllReformers[i] = modifier.toReformerString();
                }
            }
        }

        private static void CleanJoystickNodes()
        {
            List<string> active = GetJoysticksActiveInBinds();

            List<string> activityRemove = new List<string>();
            foreach (KeyValuePair<string, bool> entry in JoystickActivity)
            {
                if (!active.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))
                    activityRemove.Add(entry.Key);
            }
            for (int i = 0; i < activityRemove.Count; i++)
                JoystickActivity.Remove(activityRemove[i]);

            List<string> aliasRemove = new List<string>();
            foreach (KeyValuePair<string, string> entry in JoystickAliases)
            {
                if (!active.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))
                    aliasRemove.Add(entry.Key);
            }
            for (int i = 0; i < aliasRemove.Count; i++)
                JoystickAliases.Remove(aliasRemove[i]);

            List<string> imageRemove = new List<string>();
            foreach (KeyValuePair<string, string> entry in JoystickFileImages)
            {
                if (!active.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))
                    imageRemove.Add(entry.Key);
            }
            for (int i = 0; i < imageRemove.Count; i++)
                JoystickFileImages.Remove(imageRemove[i]);
        }

        private static List<string> GetJoysticksActiveInBinds()
        {
            List<string> result = new List<string>();
            foreach (KeyValuePair<string, Bind> entry in AllBinds)
            {
                Bind bind = entry.Value;
                if (bind == null || string.IsNullOrWhiteSpace(bind.Joystick))
                    continue;
                AddUniqueCaseInsensitive(result, NormalizeJoystickDeviceName(bind.Joystick));
            }
            return result;
        }

        // The command lists of DCS, IL-2 and Star Citizen, each read where GameDataPaths finds it (the game-data folder
        // first, else the program folder).
        private static DbCorrectionIndex BuildDbCorrectionIndex()
        {
            DbCorrectionIndex index = new DbCorrectionIndex();

            try
            {
                LoadDcsDb(index, GameDataPaths.DbFolder("DCS"));
            }
            catch
            {
                // Best-effort correction load.
            }

            try
            {
                LoadIl2Db(index, GameDataPaths.DbFolder("IL2"));
            }
            catch
            {
                // Best-effort correction load.
            }

            try
            {
                LoadStarCitizenDb(index, GameDataPaths.DbFolder("StarCitizen"));
            }
            catch
            {
                // Best-effort correction load.
            }

            return index;
        }

        private static void LoadDcsDb(DbCorrectionIndex index, string dcsRoot)
        {
            if (!Directory.Exists(dcsRoot))
                return;

            string[] files = Directory.GetFiles(dcsRoot, "*.html", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < files.Length; i++)
            {
                string plane = Path.GetFileNameWithoutExtension(files[i]);
                List<DCSInput> parsed = ParseDcsHtmlInputs(files[i], plane);
                for (int p = 0; p < parsed.Count; p++)
                    AddDcsInputToIndex(index, parsed[p]);
            }
        }

        internal static List<DCSInput> ParseDcsHtmlInputs(string filePath, string plane)
        {
            List<DCSInput> result = new List<DCSInput>();
            if (!File.Exists(filePath))
                return result;

            using (StreamReader reader = new StreamReader(filePath))
            {
                int iterator = 0;
                string title = string.Empty;
                string category = string.Empty;
                string id = string.Empty;

                while (!reader.EndOfStream)
                {
                    string line = reader.ReadLine() ?? string.Empty;
                    if (iterator > 0)
                    {
                        string cleaned = line
                            .Replace("\t", string.Empty)
                            .Replace("<td>", string.Empty)
                            .Replace("</td>", string.Empty)
                            .Replace("  ", string.Empty)
                            .Replace("\"", string.Empty)
                            .Trim();

                        switch (iterator)
                        {
                            case 1:
                                break;
                            case 2:
                                title = cleaned;
                                break;
                            case 3:
                                category = cleaned;
                                break;
                            case 4:
                                id = cleaned;
                                iterator = -1;
                                if (!string.IsNullOrWhiteSpace(id))
                                {
                                    DCSInput input = new DCSInput();
                                    input.ID = id;
                                    input.Plane = plane ?? string.Empty;
                                    input.Title = title ?? string.Empty;
                                    input.Cat = category ?? string.Empty;
                                    input.IsAxis = id.StartsWith("a", StringComparison.OrdinalIgnoreCase);
                                    result.Add(input);
                                }
                                title = string.Empty;
                                category = string.Empty;
                                id = string.Empty;
                                break;
                        }

                        iterator++;
                    }

                    if (line.IndexOf("</tr>", StringComparison.OrdinalIgnoreCase) >= 0)
                        iterator = 0;
                    if (line.IndexOf("<tr>", StringComparison.OrdinalIgnoreCase) >= 0)
                        iterator++;
                }
            }

            return result;
        }

        private static void LoadIl2Db(DbCorrectionIndex index, string il2Root)
        {
            string actionsPath = Path.Combine(il2Root, "IL2.actions");
            if (!File.Exists(actionsPath))
                return;

            string[] lines = File.ReadAllLines(actionsPath);
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i] ?? string.Empty;
                if (raw.IndexOf("//a", StringComparison.OrdinalIgnoreCase) < 0 &&
                    raw.IndexOf("//b", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                int comma = raw.IndexOf(',');
                if (comma < 0)
                    continue;

                string id = raw.Substring(0, comma).Trim();
                if (id.Length == 0)
                    continue;

                int comment = raw.IndexOf("//", comma, StringComparison.OrdinalIgnoreCase);
                if (comment < 0)
                    continue;

                string marker = raw.Substring(comment).Trim();
                bool isAxis = marker.StartsWith("//a", StringComparison.OrdinalIgnoreCase);
                string title = marker.Length > 3 ? marker.Substring(3).Trim() : id;

                OtherGameInput input = new OtherGameInput();
                input.ID = id;
                input.Game = "IL2Game";
                input.Plane = "IL2Game";
                input.Title = title;
                input.IsAxis = isAxis;
                input.Category = string.Empty;
                AddOtherInputToIndex(index, "IL2Game", input);

                if (isAxis)
                {
                    OtherGameInput plus = new OtherGameInput();
                    plus.ID = id + "+";
                    plus.Game = "IL2Game";
                    plus.Plane = "IL2Game";
                    plus.Title = title + " (BUTTON PLUS)";
                    plus.IsAxis = false;
                    plus.Category = string.Empty;
                    AddOtherInputToIndex(index, "IL2Game", plus);

                    OtherGameInput minus = new OtherGameInput();
                    minus.ID = id + "-";
                    minus.Game = "IL2Game";
                    minus.Plane = "IL2Game";
                    minus.Title = title + " (BUTTON MINUS)";
                    minus.IsAxis = false;
                    minus.Category = string.Empty;
                    AddOtherInputToIndex(index, "IL2Game", minus);
                }
            }
        }

        private static void LoadStarCitizenDb(DbCorrectionIndex index, string scRoot)
        {
            string filePath = Path.Combine(scRoot, "AllBinds.xml");
            if (!File.Exists(filePath))
                filePath = Path.Combine(scRoot, "actionmaps.xml");
            if (!File.Exists(filePath))
                return;

            string lastCategory = string.Empty;
            const string categoryToken = "<actionmap name=\"";
            const string idToken = "<action name=\"";
            const string commentStart = "<!--";
            const string commentEnd = "-->";

            using (StreamReader reader = new StreamReader(filePath))
            {
                while (!reader.EndOfStream)
                {
                    string raw = reader.ReadLine() ?? string.Empty;

                    int categoryIndex = raw.IndexOf(categoryToken, StringComparison.OrdinalIgnoreCase);
                    if (categoryIndex >= 0)
                    {
                        string tail = raw.Substring(categoryIndex + categoryToken.Length);
                        int end = tail.IndexOf('\"');
                        if (end > 0)
                            lastCategory = tail.Substring(0, end).Trim();
                    }

                    if (raw.IndexOf(idToken, StringComparison.OrdinalIgnoreCase) < 0 ||
                        raw.IndexOf(commentStart, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    string id = string.Empty;
                    {
                        int start = raw.IndexOf(idToken, StringComparison.OrdinalIgnoreCase);
                        string tail = raw.Substring(start + idToken.Length);
                        int end = tail.IndexOf('\"');
                        if (end > 0)
                            id = tail.Substring(0, end).Trim();
                    }

                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    string description = id;
                    bool axis = false;
                    int cStart = raw.IndexOf(commentStart, StringComparison.OrdinalIgnoreCase);
                    int cEnd = raw.IndexOf(commentEnd, StringComparison.OrdinalIgnoreCase);
                    if (cStart >= 0 && cEnd > cStart)
                    {
                        string comment = raw.Substring(cStart + commentStart.Length, cEnd - (cStart + commentStart.Length));
                        string[] parts = comment.Split(new[] { "//" }, StringSplitOptions.None);
                        if (parts.Length > 1)
                        {
                            description = parts[0].Trim();
                            axis = string.Equals(parts[1].Trim(), "true", StringComparison.OrdinalIgnoreCase);
                        }
                    }

                    string dictKey = string.IsNullOrWhiteSpace(lastCategory)
                        ? id
                        : lastCategory + "___" + id;

                    OtherGameInput input = new OtherGameInput();
                    input.ID = dictKey;
                    input.Game = "StarCitizen";
                    input.Plane = "StarCitizen";
                    input.Title = description;
                    input.IsAxis = axis;
                    input.Category = lastCategory ?? string.Empty;
                    AddOtherInputToIndex(index, "StarCitizen", input);
                }
            }
        }

        private static void AddDcsInputToIndex(DbCorrectionIndex index, DCSInput input)
        {
            if (index == null || input == null || string.IsNullOrWhiteSpace(input.ID))
                return;

            DCSInput copy = input.Copy();
            index.AllDcsInputs.Add(copy);

            List<DCSInput> bucket;
            if (!index.DcsById.TryGetValue(copy.ID, out bucket))
            {
                bucket = new List<DCSInput>();
                index.DcsById.Add(copy.ID, bucket);
            }
            bucket.Add(copy);
        }

        private static void AddOtherInputToIndex(DbCorrectionIndex index, string game, OtherGameInput input)
        {
            if (index == null || input == null || string.IsNullOrWhiteSpace(input.ID))
                return;

            string normalizedGame = NormalizeGameNameForCorrection(game);
            OtherGameInput copy = input.Copy();
            copy.Game = normalizedGame;
            if (string.IsNullOrWhiteSpace(copy.Plane))
                copy.Plane = normalizedGame;

            List<OtherGameInput> gameList;
            if (!index.AllOtherInputsByGame.TryGetValue(normalizedGame, out gameList))
            {
                gameList = new List<OtherGameInput>();
                index.AllOtherInputsByGame.Add(normalizedGame, gameList);
            }
            gameList.Add(copy);

            Dictionary<string, List<OtherGameInput>> idMap;
            if (!index.OtherByGameAndId.TryGetValue(normalizedGame, out idMap))
            {
                idMap = new Dictionary<string, List<OtherGameInput>>(StringComparer.OrdinalIgnoreCase);
                index.OtherByGameAndId.Add(normalizedGame, idMap);
            }

            List<OtherGameInput> idBucket;
            if (!idMap.TryGetValue(copy.ID, out idBucket))
            {
                idBucket = new List<OtherGameInput>();
                idMap.Add(copy.ID, idBucket);
            }
            idBucket.Add(copy);
        }

        private static DbCorrectionIndex planeLookupIndex;

        // The DCS aircraft whose DB has this input id: JoyPro 1 gave a new DCS node all of them (RelationItem.InitDCS),
        // and the export writes a node only for its active aircraft.
        public static List<string> DcsPlanesWithId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return new List<string>();
            if (planeLookupIndex == null)
                planeLookupIndex = BuildDbCorrectionIndex();
            return GetDcsInputsById(planeLookupIndex, id.Trim())
                .Select(i => i.Plane)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<DCSInput> GetDcsInputsById(DbCorrectionIndex index, string id)
        {
            if (index == null || string.IsNullOrWhiteSpace(id))
                return new List<DCSInput>();

            List<DCSInput> bucket;
            if (!index.DcsById.TryGetValue(id, out bucket))
                return new List<DCSInput>();

            List<DCSInput> copy = new List<DCSInput>();
            for (int i = 0; i < bucket.Count; i++)
                copy.Add(bucket[i].Copy());
            return copy;
        }

        private static List<OtherGameInput> GetOtherInputsById(DbCorrectionIndex index, string game, string id)
        {
            string normalizedGame = NormalizeGameNameForCorrection(game);
            if (index == null || string.IsNullOrWhiteSpace(id))
                return new List<OtherGameInput>();

            Dictionary<string, List<OtherGameInput>> idMap;
            if (!index.OtherByGameAndId.TryGetValue(normalizedGame, out idMap))
                return new List<OtherGameInput>();

            List<OtherGameInput> bucket;
            if (!idMap.TryGetValue(id, out bucket))
                return new List<OtherGameInput>();

            List<OtherGameInput> copy = new List<OtherGameInput>();
            for (int i = 0; i < bucket.Count; i++)
                copy.Add(bucket[i].Copy());
            return copy;
        }

        private static List<DCSInput> FindDcsReplacementByTitlePlaneAxis(DbCorrectionIndex index, string title, string plane, bool axis)
        {
            List<DCSInput> result = new List<DCSInput>();
            if (index == null || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(plane))
                return result;

            for (int i = 0; i < index.AllDcsInputs.Count; i++)
            {
                DCSInput input = index.AllDcsInputs[i];
                if (input == null)
                    continue;
                if (!string.Equals(input.Plane ?? string.Empty, plane, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (input.IsAxis != axis)
                    continue;
                if (!string.Equals(input.Title ?? string.Empty, title, StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(input.Copy());
            }

            return result;
        }

        private static List<OtherGameInput> FindOtherReplacementByTitlePlaneAxis(
            DbCorrectionIndex index,
            string game,
            string title,
            string plane,
            bool axis)
        {
            List<OtherGameInput> result = new List<OtherGameInput>();
            string normalizedGame = NormalizeGameNameForCorrection(game);
            if (index == null || string.IsNullOrWhiteSpace(title))
                return result;

            List<OtherGameInput> pool;
            if (!index.AllOtherInputsByGame.TryGetValue(normalizedGame, out pool))
                return result;

            string expectedPlane = string.IsNullOrWhiteSpace(plane) ? normalizedGame : plane;
            for (int i = 0; i < pool.Count; i++)
            {
                OtherGameInput input = pool[i];
                if (input == null)
                    continue;
                if (input.IsAxis != axis)
                    continue;
                if (!string.Equals(input.Title ?? string.Empty, title, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(input.Plane ?? normalizedGame, expectedPlane, StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(input.Copy());
            }

            return result;
        }

        private static DCSInput InputContainsPlane(IEnumerable<DCSInput> inputs, string plane)
        {
            if (inputs == null || string.IsNullOrWhiteSpace(plane))
                return null;
            foreach (DCSInput input in inputs)
            {
                if (input == null)
                    continue;
                if (string.Equals(input.Plane ?? string.Empty, plane, StringComparison.OrdinalIgnoreCase))
                    return input;
            }
            return null;
        }

        private static OtherGameInput InputContainsPlane(IEnumerable<OtherGameInput> inputs, string plane)
        {
            if (inputs == null || string.IsNullOrWhiteSpace(plane))
                return null;
            foreach (OtherGameInput input in inputs)
            {
                if (input == null)
                    continue;
                string candidatePlane = string.IsNullOrWhiteSpace(input.Plane) ? NormalizeGameNameForCorrection(input.Game) : input.Plane;
                if (string.Equals(candidatePlane, plane, StringComparison.OrdinalIgnoreCase))
                    return input;
            }
            return null;
        }

        private static void AddUniqueCaseInsensitive(List<string> list, string value)
        {
            if (list == null || string.IsNullOrWhiteSpace(value))
                return;
            if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
                list.Add(value);
        }

        private static string NormalizeGameNameForCorrection(string game)
        {
            if (string.IsNullOrWhiteSpace(game))
                return "DCS";

            string value = game.Trim();
            if (value.Equals("DCS", StringComparison.OrdinalIgnoreCase))
                return "DCS";
            if (value.Equals("IL2", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("IL2Game", StringComparison.OrdinalIgnoreCase))
                return "IL2Game";
            if (value.Equals("StarCitizen", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Star Citizen", StringComparison.OrdinalIgnoreCase))
                return "StarCitizen";
            return value;
        }

        private static string NormalizeReformerDelimiter(string reformer)
        {
            if (string.IsNullOrWhiteSpace(reformer))
                return string.Empty;
            return reformer.Replace("Â§", "§");
        }

        private static string NormalizeJoystickDeviceName(string joystick)
        {
            if (string.IsNullOrWhiteSpace(joystick))
                return string.Empty;

            try
            {
                string value = joystick;
                if (!value.Contains("{") || !value.Contains("}") || !value.Contains("-"))
                    return value;

                string[] outerParts = value.Split('{');
                if (outerParts.Length < 2)
                    return value;

                string[] innerParts = outerParts[1].Split('-');
                if (innerParts.Length < 2)
                    return value;

                string corrected = outerParts[0] + "{" + innerParts[0].ToUpperInvariant();
                for (int i = 1; i < innerParts.Length; i++)
                {
                    if (i == 2)
                        corrected += "-" + innerParts[i].ToLowerInvariant();
                    else
                        corrected += "-" + innerParts[i].ToUpperInvariant();
                }

                return corrected;
            }
            catch
            {
                return joystick;
            }
        }

        private static string NormalizeDatabaseFingerprint(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint))
                return string.Empty;
            return fingerprint.Trim().ToLowerInvariant();
        }

        private static string GetOrComputeCurrentDatabaseFingerprint()
        {
            if (!string.IsNullOrWhiteSpace(CurrentDatabaseFingerprint))
                return CurrentDatabaseFingerprint;

            string rootPath = MainStructure.PROGPATH;
            if (string.IsNullOrWhiteSpace(rootPath))
                rootPath = Environment.CurrentDirectory;

            CoreDbFingerprint.FingerprintResult result = CoreDbFingerprint.Compute(rootPath);
            CurrentDatabaseFingerprint = NormalizeDatabaseFingerprint(result.Fingerprint);
            return CurrentDatabaseFingerprint;
        }

        private static bool LooksLikeJsonProfile(string filePath)
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            int offset = 0;
            if (bytes.Length >= 3 &&
                bytes[0] == 0xEF &&
                bytes[1] == 0xBB &&
                bytes[2] == 0xBF)
            {
                offset = 3; // UTF-8 BOM
            }
            else if (bytes.Length >= 2 &&
                     ((bytes[0] == 0xFF && bytes[1] == 0xFE) ||
                      (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            {
                offset = 2; // UTF-16 BOM
            }

            for (int i = offset; i < bytes.Length; i++)
            {
                byte current = bytes[i];
                if (current == 9 || current == 10 || current == 13 || current == 32)
                    continue;
                return current == '{' || current == '[';
            }
            return false;
        }

        private static Pr0file LoadJsonProfile(string filePath)
        {
            string json = File.ReadAllText(filePath);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            return serializer.Deserialize<Pr0file>(json);
        }

        // A profile file's content as a document, without loading it (the profile history describes older versions).
        internal static Pr0file ReadProfileBytes(byte[] content)
        {
            if (content == null || content.Length == 0)
                return null;
            int offset = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;
            for (int i = offset; i < content.Length; i++)
            {
                byte current = content[i];
                if (current == 9 || current == 10 || current == 13 || current == 32)
                    continue;
                if (current == '{' || current == '[')
                {
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    serializer.MaxJsonLength = int.MaxValue;
                    return serializer.Deserialize<Pr0file>(Encoding.UTF8.GetString(content, offset, content.Length - offset));
                }
                break;
            }
            using (MemoryStream stream = new MemoryStream(content))
            {
                BinaryFormatter formatter = new BinaryFormatter();
                formatter.Binder = new LegacyJoyProSerializationBinder();
                return formatter.Deserialize(stream) as Pr0file;
            }
        }

        private static Pr0file LoadBinaryProfile(string filePath)
        {
            using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                BinaryFormatter formatter = new BinaryFormatter();
                formatter.Binder = new LegacyJoyProSerializationBinder();
                object payload = formatter.Deserialize(stream);
                Pr0file profile = payload as Pr0file;
                if (profile == null)
                    throw new SerializationException("Profile payload is not JoyPro.Pr0file.");
                return profile;
            }
        }

        private static Pr0file BuildProfileSnapshot()
        {
            string fingerprint = GetOrComputeCurrentDatabaseFingerprint();
            if (string.IsNullOrWhiteSpace(fingerprint))
                fingerprint = NormalizeDatabaseFingerprint(LoadedProfileDatabaseFingerprint);

            Pr0file profile = new Pr0file();
            profile.Relations = AllRelations;
            profile.Binds = AllBinds;
            profile.LastSelectedDCSInstance = string.Empty;
            profile.JoystickAliases = JoystickAliases;
            profile.JoystickFileImages = JoystickFileImages;
            profile.JoystickLayoutExport = JoystickLayoutExport;
            profile.PlaneAliases = PlaneAliases;
            profile.JoysticksPGuids = LocalJoystickPGUID;
            profile.modifierNameChanges = ModifierNameChanges;
            profile.JoystickFFB = JoystickFFB;
            profile.FfbOverrides = FfbOverrides == null || FfbOverrides.Count == 0 ? null : FfbOverrides;
            profile.VirtualJoystick = string.IsNullOrWhiteSpace(VirtualJoystickConfig) ? null : VirtualJoystickConfig;
            profile.AxisCalibrations = AxisCalibrations == null || AxisCalibrations.Count == 0 ? null : AxisCalibrations;
            profile.DatabaseFingerprint = fingerprint ?? string.Empty;
            return profile;
        }
    }
}
