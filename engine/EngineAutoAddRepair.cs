using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.History;

namespace JoyPro.Engine
{
    // The repair for profiles an earlier load correction filled up (docs/relation-builder.md "Load correction"): up to
    // 2.6.1 "Auto Add DB Items" switched on, after every change of the command lists (Refresh DB, a JoyPro update),
    // every aircraft of a node's id that the node did not hold yet - aircraft the user had never bound, e.g. a tester's
    // "THROTTLE - SLIDER -HELO POWER BOTH" went from AH-64D, Ka-50 III and Mosquito to 21 aircraft (F-15C, A-10C,
    // MiG-29, Su-25, "Default" by the shared FC3 thrust id), saved under the label of his next click.
    //
    // engine profile auto-added: the profile history finds such a step - a version made by a change (not an undo,
    // restore, import or builder: IsBuilderStep, CameAfterImportCheckpoint, BuiltSomething) whose content has another DB
    // fingerprint than the version it was made on, and in which aircraft went from "not in the node" to "on" in existing
    // nodes of at least MinRelations relations. Offered are the aircraft of those steps that are still on now (and that
    // no import has brought since); the user unticks any to keep. --apply takes them out (the node gets back to the
    // aircraft it had), --keep only stops asking; both are one profile version (one undo step) and mark the steps as
    // looked at (Pr0file.ReviewedAutoAdds). Nothing changes without one of them.
    //
    // "Default" and the other input layers (EngineDcsExport.IsNonAircraftLayer) are listed and taken out like the rest,
    // but marked NonAircraft and counted apart: an export writes them into the layer's own folder, never into a module.
    // A profile with nothing else gets no notice in the GUI (MainWindowViewModel.AutoAddRepair.cs).
    internal static class EngineAutoAddRepair
    {
        // A step counts when it switched on aircraft in at least this many relations (the old pass did it in dozens; a
        // user's edit names its relation).
        internal const int MinRelations = 3;

        // Labels of steps whose own work switches on aircraft in many relations: such a step is not taken for the old
        // pass (an import is the user's own binds). The history keeps what the label was when the step was made: the
        // engine's name of the command (EngineProfileHistory.DescribeCommand, or a Note it sets), or the words of the
        // GUI's busy area (busyLabel, historyNote), past and present. They are matched from the start of the label, so
        // "Importing" also covers "Importing from Microsoft Flight Simulator 2024 (experimental)".
        private static readonly string[] BuilderLabels =
        {
            // Imports and take-overs
            "Import from", "Import relations", "Import binds", "Importing",
            "Take over changes made in a game", "Taking over",
            // Auto Group, Auto Assign, grouping by description
            "Auto Group", "Auto grouping", "Auto Assign", "Assigning controls",
            "Group by description", "Creating relations from your description",
            // Suggestions for many relations (one relation's are named in the label)
            "Add suggested actions", "Suggest missing",
            // The assistant and the plans of the AI pages
            "Change by AI chat", "JoyPro assistant", "The JoyPro assistant", "Edit with AI", "Manage with AI", "engine relations plan",
            // Layouts, the setup wizard
            "Lay out with AI", "Applying your layout", "Reading your preferences and laying out", "Suggesting layouts",
            "Setup wizard", "Game setup", "engine setup",
            // Merges
            "Merge profiles", "Merging profiles", "Merging copied relations", "Merging throttle relations",
            "Detent mode: merged", "engine relations merge-copies",
            // The user's own changes of many relations at once
            "Running mass operation", "Change relations (", "Edit relations", "Opening relations editor",
            // The load correction's own step since 2.6.2, and this repair
            "Updated to the new command lists", "Took out aircraft JoyPro added", "Took out input layers JoyPro added",
            "Taking out the aircraft JoyPro added", "Kept the aircraft JoyPro added", "Keeping the aircraft JoyPro added"
        };

        // The checkpoint an import keeps of the profile just before it (EngineProfileHistory.CheckpointLabel).
        private const string ImportCheckpointLabel = "Before import from";

        internal sealed class Entry
        {
            public string Key = string.Empty;
            public int Version;
            public string Relation = string.Empty;
            public string Game = "DCS";
            public string Id = string.Empty;
            public string Plane = string.Empty;
            public string Title = string.Empty;
            // "Default", the cameras, the radio menu: an input layer, no aircraft.
            public bool NonAircraft;
        }

        internal sealed class Step
        {
            public ProfileVersion Version;
            public List<Entry> Entries = new List<Entry>();
        }

        // The steps of the old pass in the history of this profile, with their aircraft that are on in the profile now.
        internal static List<Step> Find(string profilePath, List<string> warnings)
        {
            List<Step> steps = new List<Step>();
            if (string.IsNullOrWhiteSpace(profilePath) || !ProfileHistory.Enabled)
                return steps;
            ProfileHistoryLog log;
            try
            {
                log = ProfileHistory.Read(profilePath);
            }
            catch (Exception ex)
            {
                warnings.Add("The profile history could not be read: " + ex.Message);
                return steps;
            }
            HashSet<string> reviewed = new HashSet<string>(InternalDataManagement.ReviewedAutoAdds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            // The chain the profile is at: from the current version back over the versions each was made on.
            List<ProfileVersion> chain = new List<ProfileVersion>();
            HashSet<int> seen = new HashSet<int>();
            for (ProfileVersion v = log.CursorVersion; v != null && seen.Add(v.Id); v = v.Parent > 0 ? log.Find(v.Parent) : null)
                chain.Add(v);
            chain.Reverse();

            for (int i = 1; i < chain.Count; i++)
            {
                ProfileVersion parent = chain[i - 1];
                ProfileVersion version = chain[i];
                if (version.Kind != ProfileVersionKind.Change || string.IsNullOrWhiteSpace(version.Hash) || version.Hash == parent.Hash)
                    continue;
                if (reviewed.Contains(version.Hash) || IsBuilderStep(version.Label) || CameAfterImportCheckpoint(parent))
                    continue;
                // Cheap first: the items (one line per relation with its count of switched-on aircraft).
                if (RelationsThatGained(log.Folder, parent.Hash, version.Hash) < MinRelations)
                    continue;
                Pr0file before;
                Pr0file after;
                try
                {
                    before = InternalDataManagement.ReadProfileBytes(ProfileHistory.ReadContent(profilePath, parent.Id));
                    after = InternalDataManagement.ReadProfileBytes(ProfileHistory.ReadContent(profilePath, version.Id));
                }
                catch (Exception ex)
                {
                    warnings.Add("History version " + version.Id.ToString(CultureInfo.InvariantCulture) + " could not be read: " + ex.Message);
                    continue;
                }
                if (before == null || after == null)
                    continue;
                // The command lists changed in this step (the pass runs only then).
                if (string.Equals(Fingerprint(before), Fingerprint(after), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (BuiltSomething(before, after))
                    continue;
                HashSet<string> named = NamedRelations(version.Label);
                List<Entry> entries = SwitchedOnInExistingNodes(before, after, version.Id)
                    .Where(e => !named.Contains(e.Relation)).ToList();
                if (entries.Select(e => e.Relation).Distinct(StringComparer.OrdinalIgnoreCase).Count() < MinRelations)
                    continue;
                steps.Add(new Step { Version = version, Entries = entries });
            }

            // Only what is still on now, and not what an import brought in since (the user's own binds).
            foreach (Step step in steps)
            {
                step.Entries = step.Entries.Where(StillOnNow).ToList();
                foreach (Entry entry in step.Entries)
                    entry.Title = TitleOf(entry);
            }
            // An aircraft offered by an older step and again by a newer one: once (the newer step).
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = steps.Count - 1; i >= 0; i--)
                steps[i].Entries = steps[i].Entries.Where(e => keys.Add(e.Key)).ToList();
            return steps.Where(s => s.Entries.Count > 0).ToList();
        }

        // Several saves of one click are one version, with their labels joined: "Opening the profile + Importing from
        // DCS" (ProfileHistory.MergeLabels).
        private static bool IsBuilderStep(string label)
        {
            return (label ?? string.Empty).Split(new[] { " + " }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Any(part =>
                BuilderLabels.Any(b => part.StartsWith(b, StringComparison.OrdinalIgnoreCase)) ||
                part.StartsWith("Undo", StringComparison.OrdinalIgnoreCase) || part.StartsWith("Redo", StringComparison.OrdinalIgnoreCase));
        }

        // The two guards that do not depend on a step's own label (a label this list does not know, or one that went into
        // the history translated). An import always keeps a checkpoint just before it, under a label no language changes.
        private static bool CameAfterImportCheckpoint(ProfileVersion parent)
        {
            return parent != null && parent.Kind == ProfileVersionKind.Checkpoint &&
                   (parent.Label ?? string.Empty).StartsWith(ImportCheckpointLabel, StringComparison.OrdinalIgnoreCase);
        }

        // The old pass only switched on aircraft in nodes that were there: it made no relation and no bind. A step with
        // several new ones was a builder, whatever its label says (one click makes one).
        private static bool BuiltSomething(Pr0file before, Pr0file after)
        {
            return CountNew(before.Relations, after.Relations) >= MinRelations || CountNew(before.Binds, after.Binds) >= MinRelations;
        }

        private static int CountNew<T>(Dictionary<string, T> before, Dictionary<string, T> after)
        {
            if (after == null)
                return 0;
            HashSet<string> had = new HashSet<string>(before == null ? Enumerable.Empty<string>() : before.Keys, StringComparer.OrdinalIgnoreCase);
            return after.Keys.Count(k => !had.Contains(k));
        }

        private static string Fingerprint(Pr0file profile)
        {
            return (profile.DatabaseFingerprint ?? string.Empty).Trim().ToLowerInvariant();
        }

        // The relations a version's label names ("Adding 1 actions to 'Pitch'"): their changes were the user's.
        private static HashSet<string> NamedRelations(string label)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(label ?? string.Empty, "'([^']+)'"))
                names.Add(match.Groups[1].Value);
            return names;
        }

        // How many relations have more switched-on aircraft after than before (history items: "R\tname\tcount\t...").
        private static int RelationsThatGained(string folder, string beforeHash, string afterHash)
        {
            IList<string> before = ProfileHistory.ReadItemsOf(folder, beforeHash);
            IList<string> after = ProfileHistory.ReadItemsOf(folder, afterHash);
            // Without items (a version changed outside JoyPro): the contents decide.
            if (before == null || after == null)
                return int.MaxValue;
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in before)
            {
                string[] parts = line.Split('\t');
                int n;
                if (parts.Length > 2 && parts[0] == "R" && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    counts[parts[1]] = n;
            }
            int gained = 0;
            foreach (string line in after)
            {
                string[] parts = line.Split('\t');
                int n;
                int was;
                if (parts.Length > 2 && parts[0] == "R" && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out n) &&
                    counts.TryGetValue(parts[1], out was) && n > was)
                    gained++;
            }
            return gained;
        }

        // Aircraft that are on in a node after and were not in the same node before (the node was there before).
        private static List<Entry> SwitchedOnInExistingNodes(Pr0file before, Pr0file after, int versionId)
        {
            Dictionary<string, RelationItem> old = new Dictionary<string, RelationItem>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> pair in before.Relations ?? new Dictionary<string, Relation>())
            {
                Relation relation = pair.Value;
                if (relation == null)
                    continue;
                string name = string.IsNullOrWhiteSpace(relation.NAME) ? pair.Key ?? string.Empty : relation.NAME;
                foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
                {
                    if (node != null && !string.IsNullOrWhiteSpace(node.ID))
                        old[name + "\t" + GameOf(node) + "\t" + node.ID] = node;
                }
            }
            List<Entry> entries = new List<Entry>();
            foreach (KeyValuePair<string, Relation> pair in after.Relations ?? new Dictionary<string, Relation>())
            {
                Relation relation = pair.Value;
                if (relation == null)
                    continue;
                string name = string.IsNullOrWhiteSpace(relation.NAME) ? pair.Key ?? string.Empty : relation.NAME;
                foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
                {
                    if (node == null || string.IsNullOrWhiteSpace(node.ID) || node.AIRCRAFT == null)
                        continue;
                    RelationItem was;
                    if (!old.TryGetValue(name + "\t" + GameOf(node) + "\t" + node.ID, out was))
                        continue;
                    Dictionary<string, bool> had = was.AIRCRAFT ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT)
                    {
                        if (!plane.Value || string.IsNullOrWhiteSpace(plane.Key) || had.Keys.Any(k => string.Equals(k, plane.Key, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        entries.Add(new Entry
                        {
                            Key = KeyOf(name, GameOf(node), node.ID, plane.Key),
                            Version = versionId,
                            Relation = name,
                            Game = GameOf(node),
                            Id = node.ID,
                            Plane = plane.Key,
                            // The UI layer is no aircraft either, but DCS uses its binds in every aircraft: it counts.
                            NonAircraft = InternalDataManagement.IsPseudoPlane(GameOf(node), plane.Key) &&
                                          !string.Equals(plane.Key, "UiLayer", StringComparison.OrdinalIgnoreCase)
                        });
                    }
                }
            }
            return entries;
        }

        private static string GameOf(RelationItem node)
        {
            return string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game.Trim();
        }

        // A short key of one aircraft of one node of one relation (the GUI's unticked ones come back as --skip).
        internal static string KeyOf(string relation, string game, string id, string plane)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes((relation ?? string.Empty) + "\t" + (game ?? string.Empty).ToUpperInvariant() + "\t" + (id ?? string.Empty) + "\t" + (plane ?? string.Empty).ToUpperInvariant()));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 6; i++)
                    sb.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static RelationItem NodeNow(Entry entry, out Relation relation)
        {
            relation = null;
            if (!InternalDataManagement.AllRelations.TryGetValue(entry.Relation, out relation) || relation == null || relation.NODES == null)
                return null;
            return relation.NODES.FirstOrDefault(n => n != null && string.Equals(n.ID, entry.Id, StringComparison.Ordinal) &&
                                                       string.Equals(GameOf(n), entry.Game, StringComparison.OrdinalIgnoreCase));
        }

        private static bool StillOnNow(Entry entry)
        {
            Relation relation;
            RelationItem node = NodeNow(entry, out relation);
            bool on;
            return node != null && node.AIRCRAFT != null && node.AIRCRAFT.TryGetValue(entry.Plane, out on) && on && !node.IsFromGameFiles(entry.Plane);
        }

        private static string TitleOf(Entry entry)
        {
            try
            {
                if (string.Equals(entry.Game, "DCS", StringComparison.OrdinalIgnoreCase))
                    return InternalDataManagement.DcsTitleOrEmpty(entry.Id, entry.Plane);
            }
            catch (Exception ex)
            {
                JoyPro.Logging.JoyProLog.Debug("Corrections", "No title of " + entry.Plane + " " + entry.Id + ": " + ex.Message);
            }
            return string.Empty;
        }

        // ---- the command ----

        internal static EngineAutoAddRepairResult Run(string profilePath, bool apply, bool keep, ICollection<string> skip)
        {
            EngineAutoAddRepairResult result = new EngineAutoAddRepairResult();
            List<Step> steps = Find(profilePath, result.Warnings);
            HashSet<string> skipped = new HashSet<string>(skip ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (Step step in steps)
            {
                result.Steps.Add(new EngineAutoAddStep
                {
                    Version = step.Version.Id,
                    Utc = step.Version.Utc.ToString("o", CultureInfo.InvariantCulture),
                    Local = ProfileHistory.FormatLocal(step.Version.Utc),
                    Label = step.Version.Label ?? string.Empty,
                    Aircraft = step.Entries.Count(e => !e.NonAircraft),
                    NonAircraft = step.Entries.Count(e => e.NonAircraft),
                    Relations = step.Entries.Where(e => !e.NonAircraft).Select(e => e.Relation).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                });
                foreach (Entry entry in step.Entries)
                {
                    result.Entries.Add(new EngineAutoAddEntry
                    {
                        Key = entry.Key, Version = entry.Version, Relation = entry.Relation, Game = entry.Game, Id = entry.Id,
                        Plane = entry.Plane, Title = entry.Title, NonAircraft = entry.NonAircraft, Skipped = skipped.Contains(entry.Key)
                    });
                }
            }
            // Per relation: what stays and what goes (the preview's rows).
            foreach (IGrouping<string, EngineAutoAddEntry> relation in result.Entries.GroupBy(e => e.Relation, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                Relation now;
                InternalDataManagement.AllRelations.TryGetValue(relation.Key, out now);
                HashSet<string> taken = new HashSet<string>(relation.Select(e => e.Game + "\t" + e.Id + "\t" + e.Plane), StringComparer.OrdinalIgnoreCase);
                List<string> keeps = now == null || now.NODES == null ? new List<string>() : now.NODES
                    .Where(n => n != null && n.AIRCRAFT != null)
                    .SelectMany(n => n.AIRCRAFT.Where(p => p.Value && !taken.Contains(GameOf(n) + "\t" + n.ID + "\t" + p.Key)).Select(p => p.Key))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                List<string> removes = relation.Select(e => e.Plane).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                result.Relations.Add(new EngineAutoAddRelation
                {
                    Relation = relation.Key,
                    Remove = removes,
                    Keeps = keeps,
                    KeepsAircraft = now == null || now.NODES == null ? new List<string>() : now.NODES
                        .Where(n => n != null && n.AIRCRAFT != null && string.Equals(GameOf(n), "DCS", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(n => n.AIRCRAFT.Where(p => p.Value && !taken.Contains(GameOf(n) + "\t" + n.ID + "\t" + p.Key) && !EngineDcsExport.IsNonAircraftLayer(p.Key)).Select(p => p.Key))
                        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
                    NonAircraft = removes.Concat(keeps).Where(EngineDcsExport.IsNonAircraftLayer).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                });
            }
            result.Found = result.Entries.Count > 0;
            result.Aircraft = result.Entries.Count(e => !e.NonAircraft);
            result.NonAircraft = result.Entries.Count(e => e.NonAircraft);
            result.RelationCount = result.Entries.Where(e => !e.NonAircraft).Select(e => e.Relation).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (!apply && !keep)
                return result;
            if (!result.Found)
            {
                result.Message = "Nothing to repair: no aircraft of an earlier load correction is on in this profile.";
                return result;
            }

            int removed = 0;
            int removedNonAircraft = 0;
            HashSet<string> touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (apply)
            {
                foreach (Step step in steps)
                {
                    foreach (Entry entry in step.Entries.Where(e => !skipped.Contains(e.Key)))
                    {
                        Relation relation;
                        RelationItem node = NodeNow(entry, out relation);
                        if (node == null || node.AIRCRAFT == null)
                            continue;
                        string key = node.AIRCRAFT.Keys.FirstOrDefault(k => string.Equals(k, entry.Plane, StringComparison.OrdinalIgnoreCase));
                        if (key == null)
                            continue;
                        // Back to "not in the node", as before the correction.
                        node.AIRCRAFT.Remove(key);
                        removed++;
                        if (entry.NonAircraft)
                            removedNonAircraft++;
                        touched.Add(relation.NAME ?? entry.Relation);
                        relation.RecalculateElementCount();
                    }
                }
            }
            foreach (Step step in steps)
            {
                if (!InternalDataManagement.ReviewedAutoAdds.Contains(step.Version.Hash, StringComparer.OrdinalIgnoreCase))
                    InternalDataManagement.ReviewedAutoAdds.Add(step.Version.Hash);
            }
            result.Applied = apply;
            result.Kept = keep && !apply;
            result.Removed = removed;
            result.RemovedNonAircraft = removedNonAircraft;
            result.KeptAircraft = result.Entries.Count - removed;
            int removedAircraft = removed - removedNonAircraft;
            string relationsWord = touched.Count.ToString(CultureInfo.InvariantCulture) + (touched.Count == 1 ? " relation" : " relations");
            result.HistoryLabel = !apply ? "Kept the aircraft JoyPro added"
                : removedAircraft == 0 && removedNonAircraft > 0 ? "Took out input layers JoyPro added: " + removedNonAircraft.ToString(CultureInfo.InvariantCulture) + " in " + relationsWord
                : "Took out aircraft JoyPro added: " + removedAircraft.ToString(CultureInfo.InvariantCulture) + " aircraft in " + relationsWord;
            string layers = removedNonAircraft > 0 ? " and " + removedNonAircraft.ToString(CultureInfo.InvariantCulture) + " input layers (no aircraft)" : string.Empty;
            result.Message = apply
                ? "Took out " + removedAircraft.ToString(CultureInfo.InvariantCulture) + " aircraft" + layers + " in " + relationsWord + " that an earlier load correction had switched on" +
                  (result.KeptAircraft > 0 ? "; kept " + result.KeptAircraft.ToString(CultureInfo.InvariantCulture) + " you unticked" : string.Empty) + "."
                : "Kept the " + result.Entries.Count.ToString(CultureInfo.InvariantCulture) + " entries; JoyPro does not ask about them again.";
            return result;
        }

        public static string ToText(EngineAutoAddRepairResult result)
        {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(result.Message))
                sb.AppendLine(result.Message);
            if (!result.Found)
            {
                if (string.IsNullOrEmpty(result.Message))
                    sb.AppendLine("No aircraft of an earlier load correction to take out.");
            }
            else
            {
                sb.AppendLine("Aircraft an earlier load correction switched on: " + result.Aircraft.ToString(CultureInfo.InvariantCulture) + " in " + result.RelationCount.ToString(CultureInfo.InvariantCulture) + " relations");
                if (result.NonAircraft > 0)
                    sb.AppendLine("Not aircraft (input layers such as Default; an export writes them into the layer's own folder, not into a module): " + result.NonAircraft.ToString(CultureInfo.InvariantCulture));
                foreach (EngineAutoAddStep step in result.Steps)
                    sb.AppendLine("  Step #" + step.Version.ToString(CultureInfo.InvariantCulture) + " " + step.Local + " '" + step.Label + "': " + step.Aircraft.ToString(CultureInfo.InvariantCulture) + " aircraft in " + step.Relations.ToString(CultureInfo.InvariantCulture) + " relations" +
                                  (step.NonAircraft > 0 ? ", " + step.NonAircraft.ToString(CultureInfo.InvariantCulture) + " not aircraft" : string.Empty));
                foreach (EngineAutoAddRelation relation in result.Relations)
                    sb.AppendLine("  - " + relation.Relation + " | take out: " + string.Join(", ", relation.Remove.Select(p => relation.NonAircraft.Contains(p, StringComparer.OrdinalIgnoreCase) ? p + " (not an aircraft)" : p)) +
                                  " | keeps: " + (relation.Keeps.Count == 0 ? "-" : string.Join(", ", relation.Keeps)));
            }
            foreach (string warning in result.Warnings)
                sb.AppendLine("Warning: " + warning);
            return sb.ToString().TrimEnd();
        }
    }

    public sealed class EngineAutoAddStep
    {
        public int Version { get; set; }
        public string Utc { get; set; }
        public string Local { get; set; }
        public string Label { get; set; }
        public int Aircraft { get; set; }
        public int NonAircraft { get; set; }
        public int Relations { get; set; }
    }

    public sealed class EngineAutoAddEntry
    {
        public string Key { get; set; }
        public int Version { get; set; }
        public string Relation { get; set; }
        public string Game { get; set; }
        public string Id { get; set; }
        public string Plane { get; set; }
        public string Title { get; set; }
        public bool NonAircraft { get; set; }
        public bool Skipped { get; set; }
    }

    public sealed class EngineAutoAddRelation
    {
        public string Relation { get; set; }
        public List<string> Remove { get; set; } = new List<string>();
        public List<string> Keeps { get; set; } = new List<string>();
        // Of Keeps the DCS aircraft (what the GUI's example names; a game's own key or a layer is no aircraft).
        public List<string> KeepsAircraft { get; set; } = new List<string>();
        // Those of Remove and Keeps that are input layers, no aircraft ("Default").
        public List<string> NonAircraft { get; set; } = new List<string>();
    }

    public sealed class EngineAutoAddRepairResult
    {
        public bool Found { get; set; }
        // Real aircraft and the relations they are in; NonAircraft: input layers such as "Default". Found also when there
        // are only input layers (--apply and --keep work on them).
        public int Aircraft { get; set; }
        public int NonAircraft { get; set; }
        public int RelationCount { get; set; }
        public bool Applied { get; set; }
        public bool Kept { get; set; }
        public int Removed { get; set; }
        public int RemovedNonAircraft { get; set; }
        public int KeptAircraft { get; set; }
        public string Message { get; set; } = string.Empty;
        public string HistoryLabel { get; set; } = string.Empty;
        public List<EngineAutoAddStep> Steps { get; set; } = new List<EngineAutoAddStep>();
        public List<EngineAutoAddRelation> Relations { get; set; } = new List<EngineAutoAddRelation>();
        public List<EngineAutoAddEntry> Entries { get; set; } = new List<EngineAutoAddEntry>();
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
