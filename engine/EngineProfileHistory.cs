using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using JoyPro.History;

namespace JoyPro.Engine
{
    // The engine's side of the profile history (docs/profile-history.md, store in ProfileHistory.cs): every save of a
    // profile becomes a version with the profile's items (what changed is described from them), a version is kept before
    // every import and export, and "engine profile history ..." lists, compares, restores, undoes, keeps and cleans up.
    internal static class EngineProfileHistory
    {
        // --history-note: what the GUI calls the change ("Adding relation"); else the command's own name.
        public static string Note = string.Empty;
        public static string CommandLabel = string.Empty;
        // --profile of this run (history commands also work for a profile file that is gone).
        public static string ProfilePath = string.Empty;

        // A change that must stay a version of its own (not merged with the edit just before it), so one undo takes
        // exactly it back (binds detent merge).
        public static bool NoCoalesce;

        public static string Label
        {
            get
            {
                string note = (Note ?? string.Empty).Trim().TrimEnd('.').Trim();
                return note.Length > 0 ? note : (CommandLabel ?? string.Empty);
            }
        }

        // After InternalDataManagement.SaveProfileTo wrote the file: the version, and before it the file's former content
        // when the history does not know it (no history yet, or changed outside JoyPro).
        // previousGraph: the file before the save as read (the engine server's kept graph), else null and it is read
        // from previous when the history needs it.
        public static void AfterSave(string fullPath, byte[] previous, byte[] content, Pr0file snapshot, Pr0file previousGraph = null)
        {
            try
            {
                string previousHash = previous == null ? null : ProfileHistory.HashOf(previous);
                string hash = ProfileHistory.HashOf(content);
                ProfileHistoryLog log = ProfileHistory.Read(fullPath);
                ProfileVersion cursor = log.CursorVersion;
                ProfileHistoryRecordOptions change = new ProfileHistoryRecordOptions
                {
                    Kind = ProfileVersionKind.Change,
                    Label = Label,
                    Items = () => Items(snapshot),
                    PreviousHash = previousHash,
                    Coalesce = !NoCoalesce
                };
                if (previous != null && previousHash != hash && (cursor == null || cursor.Hash != previousHash))
                {
                    // Two versions at once (the file before this save, then this save): both files are written side by
                    // side first, then both versions in one write.
                    Func<IList<string>> previousItems = () => previousGraph != null ? Items(previousGraph) : ItemsOfContent(previous);
                    ProfileHistory.PrepareVersions(fullPath, previousHash,
                        new KeyValuePair<byte[], Func<IList<string>>>(previous, previousItems),
                        new KeyValuePair<byte[], Func<IList<string>>>(content, change.Items));
                    ProfileHistory.RecordVersions(fullPath,
                        new KeyValuePair<byte[], ProfileHistoryRecordOptions>(previous, new ProfileHistoryRecordOptions
                        {
                            Kind = cursor == null ? ProfileVersionKind.Open : ProfileVersionKind.External,
                            Label = cursor == null ? "Before the history started" : "Changed outside JoyPro",
                            Items = previousItems,
                            PreviousHash = previousHash,
                            Coalesce = false
                        }),
                        new KeyValuePair<byte[], ProfileHistoryRecordOptions>(content, change));
                    return;
                }
                ProfileHistory.Record(fullPath, content, change);
            }
            catch (Exception ex)
            {
                // The profile is saved; only its history entry is missing.
                JoyPro.Logging.JoyProLog.Warn("History", "Could not record a version of " + fullPath, ex);
            }
        }

        // Before an import or export: a named version of the profile as it is.
        public static void Checkpoint(string profilePath, string label)
        {
            try
            {
                ProfileHistory.Checkpoint(profilePath, label, CurrentItems);
            }
            catch (Exception ex)
            {
                JoyPro.Logging.JoyProLog.Warn("History", "Could not keep a version before '" + label + "' of " + profilePath, ex);
            }
        }

        // "Before export to DCS" / "Before import from IL-2" for the commands that write game files or take them in.
        public static string CheckpointLabel(string area, List<string> command)
        {
            string action = command != null && command.Count > 0 ? (command[0] ?? string.Empty).Trim().ToLowerInvariant() : string.Empty;
            if (command != null && command.Any(c => string.Equals(c, "--dry-run", StringComparison.OrdinalIgnoreCase)))
                return null;
            if (action != "export" && action != "import")
                return null;
            string game = GameName(area);
            if (game.Length == 0)
                return null;
            return action == "export" ? "Before export to " + game : "Before import from " + game;
        }

        public static string GameName(string area)
        {
            switch ((area ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "dcs": return "DCS";
                case "il2": return "IL-2";
                case "sc":
                case "starcitizen": return "Star Citizen";
            }
            Games.IGameIntegration game;
            return Games.GameRegistry.TryResolve(area, out game) ? game.DisplayName : string.Empty;
        }

        // A name for the change when the GUI gave none.
        public static string DescribeCommand(string area, List<string> command)
        {
            string a = (area ?? string.Empty).Trim().ToLowerInvariant();
            string action = command != null && command.Count > 0 ? (command[0] ?? string.Empty).Trim().ToLowerInvariant() : string.Empty;
            string sub = command != null && command.Count > 1 ? (command[1] ?? string.Empty).Trim().ToLowerInvariant() : string.Empty;
            string game = GameName(a);
            if (game.Length > 0 && action == "import")
                return "Import from " + game;
            if (game.Length > 0 && action == "export")
                return "Export to " + game;
            switch (a)
            {
                case "relations":
                    switch (action)
                    {
                        case "add": return "Add relation";
                        case "item": return "Change relation actions";
                        case "group": return "Change relation groups";
                        case "mass": return sub == "delete" ? "Delete relations" : "Change relations (" + sub + ")";
                        case "import": return "Import relations";
                        case "auto-group": return "Auto Group";
                        case "from-text": return "Group by description";
                        case "auto-assign": return "Auto Assign";
                        case "suggest": return "Add suggested actions";
                        case "chat": return "Change by AI chat";
                        case "assistant": return "JoyPro assistant";
                        case "rename": return "Rename relation";
                        case "edit": return "Edit relations";
                    }
                    break;
                case "binds":
                    switch (action)
                    {
                        case "set": return "Set bind";
                        case "clear": return "Clear bind";
                        case "tuning": return "Axis tuning";
                        case "modifier": return sub == "remove" ? "Remove modifier" : sub == "rename" ? "Rename modifier" : "Add modifier";
                        case "unbind": return "Unbind input";
                        case "import": return "Import binds";
                        case "edit": return "Edit binds";
                    }
                    break;
                case "sync": return "Take over changes made in a game";
                case "vjoy": return "Virtual joystick settings";
                case "ffb": return "Force feedback settings";
                case "devices": return "Devices (" + action + ")";
                case "layout": return "Layout picks";
                case "profile":
                    switch (action)
                    {
                        case "new": return "New profile";
                        case "merge": return "Merge profiles";
                        case "save": return "Save";
                    }
                    break;
            }
            return ("engine " + a + " " + action).Trim();
        }

        // ---- items --------------------------------------------------------------------------------------------------

        public static List<string> CurrentItems()
        {
            Pr0file profile = new Pr0file
            {
                Relations = InternalDataManagement.AllRelations,
                Binds = InternalDataManagement.AllBinds,
                JoystickAliases = InternalDataManagement.JoystickAliases,
                JoystickFileImages = InternalDataManagement.JoystickFileImages,
                JoystickLayoutExport = InternalDataManagement.JoystickLayoutExport,
                PlaneAliases = InternalDataManagement.PlaneAliases,
                JoysticksPGuids = InternalDataManagement.LocalJoystickPGUID,
                modifierNameChanges = InternalDataManagement.ModifierNameChanges,
                JoystickFFB = InternalDataManagement.JoystickFFB,
                FfbOverrides = InternalDataManagement.FfbOverrides,
                AxisCalibrations = InternalDataManagement.AxisCalibrations,
                VirtualJoystick = InternalDataManagement.VirtualJoystickConfig,
                LayoutPreferences = InternalDataManagement.LayoutPreferences,
                KeptDuplicates = InternalDataManagement.KeptDuplicates,
                ExportSkipMarks = InternalDataManagement.ExportSkipMarks,
                DismissedSuggestions = InternalDataManagement.DismissedSuggestions,
                SyncedGames = InternalDataManagement.SyncedGames
            };
            return Items(profile);
        }

        private static List<string> ItemsOfContent(byte[] content)
        {
            Pr0file profile = InternalDataManagement.ReadProfileBytes(content);
            return profile == null ? null : Items(profile);
        }

        // One line per relation, bind, modifier and settings section (ProfileHistory.DescribeChanges reads them).
        public static List<string> Items(Pr0file profile)
        {
            List<string> lines = new List<string>();
            if (profile == null)
                return lines;
            using (SHA1 sha = SHA1.Create())
            {
                StringBuilder sb = new StringBuilder();
                Func<string, string> hash = text => Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty)), 12);

                Dictionary<string, Relation> relations = profile.Relations ?? new Dictionary<string, Relation>();
                foreach (KeyValuePair<string, Relation> pair in relations.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    Relation relation = pair.Value;
                    if (relation == null)
                        continue;
                    sb.Clear();
                    int actions = 0;
                    foreach (RelationItem node in (relation.NODES ?? new List<RelationItem>()).Where(n => n != null)
                                 .OrderBy(n => n.Game ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.ID ?? string.Empty, StringComparer.Ordinal))
                    {
                        sb.Append(node.Game).Append('|').Append(node.ID).Append('|');
                        if (node.AIRCRAFT != null)
                        {
                            foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                            {
                                sb.Append(plane.Key).Append(plane.Value ? '+' : '-').Append(',');
                                if (plane.Value)
                                    actions++;
                            }
                        }
                        sb.Append(';');
                    }
                    string groups = string.Join(", ", (relation.Groups ?? new List<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).OrderBy(g => g, StringComparer.OrdinalIgnoreCase));
                    string name = Clean(string.IsNullOrWhiteSpace(relation.NAME) ? pair.Key : relation.NAME);
                    lines.Add("R\t" + name + "\t" + actions.ToString(CultureInfo.InvariantCulture) + "\t" + Clean(groups) + "\t" +
                              hash(sb.ToString()) + "\t" + hash((relation.ISAXIS ? "axis|" : "button|") + groups));
                }

                Dictionary<string, Modifier> modifiers = new Dictionary<string, Modifier>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, Bind> binds = profile.Binds ?? new Dictionary<string, Bind>();
                foreach (KeyValuePair<string, Bind> pair in binds.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    Bind bind = pair.Value;
                    if (bind == null)
                        continue;
                    List<string> reformers = (bind.AllReformers ?? new List<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).OrderBy(r => r, StringComparer.Ordinal).ToList();
                    foreach (string reformer in reformers)
                    {
                        Modifier modifier = Modifier.ReformerToMod(reformer);
                        if (modifier != null && !string.IsNullOrWhiteSpace(modifier.name) && !modifiers.ContainsKey(modifier.name))
                            modifiers[modifier.name] = modifier;
                    }
                    string input = !string.IsNullOrWhiteSpace(bind.JAxis) ? bind.JAxis : bind.JButton;
                    if (string.IsNullOrWhiteSpace(bind.Joystick) && string.IsNullOrWhiteSpace(input))
                        continue;
                    string tuning = string.Join("|", new[]
                    {
                        bind.Inverted.HasValue ? bind.Inverted.Value.ToString() : "-",
                        bind.Slider.HasValue ? bind.Slider.Value.ToString() : "-",
                        bind.Deadzone.ToString("R", CultureInfo.InvariantCulture),
                        string.Join(",", (bind.Curvature ?? new List<double>()).Select(c => c.ToString("R", CultureInfo.InvariantCulture))),
                        bind.SaturationX.ToString("R", CultureInfo.InvariantCulture),
                        bind.SaturationY.ToString("R", CultureInfo.InvariantCulture),
                        bind.doubleTap.ToString(),
                        bind.TuningOverrides == null ? string.Empty : string.Join(";", bind.TuningOverrides.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase).Select(t => t.Key + "=" + t.Value))
                    });
                    string name = Clean(bind.Rl != null && !string.IsNullOrWhiteSpace(bind.Rl.NAME) ? bind.Rl.NAME : pair.Key);
                    lines.Add("B\t" + name + "\t\t" + Clean(DeviceLabel(bind.Joystick) + " " + (input ?? string.Empty)) + "\t" +
                              hash((bind.Joystick ?? string.Empty) + "|" + (bind.JAxis ?? string.Empty) + "|" + (bind.JButton ?? string.Empty)) + "\t" +
                              hash(string.Join("\n", reformers)) + "\t" + hash(tuning));
                }

                foreach (Modifier modifier in modifiers.Values.OrderBy(m => m.name, StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add("M\t" + Clean(modifier.name) + "\t\t" + Clean(DeviceLabel(modifier.device) + " " + (modifier.key ?? string.Empty)) + "\t" +
                              hash((modifier.device ?? string.Empty) + "|" + (modifier.key ?? string.Empty) + "|" + modifier.sw));
                }

                AddSection(lines, hash, "Device names", Join(profile.JoystickAliases));
                AddSection(lines, hash, "Device pictures", Join(profile.JoystickFileImages));
                AddSection(lines, hash, "Layout export folder", profile.JoystickLayoutExport);
                AddSection(lines, hash, "Aircraft names", profile.PlaneAliases == null ? string.Empty
                    : string.Join("\n", profile.PlaneAliases.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + ":" + Join(p.Value))));
                AddSection(lines, hash, "Device ids", Join(profile.JoysticksPGuids));
                AddSection(lines, hash, "Modifier renames", profile.modifierNameChanges == null ? string.Empty
                    : string.Join("\n", profile.modifierNameChanges.Select(p => p.Key + "=" + p.Value)));
                AddSection(lines, hash, "DCS force feedback", profile.JoystickFFB == null ? string.Empty
                    : string.Join("\n", profile.JoystickFFB.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(p => p.Key + ":" + (p.Value == null ? "-" : p.Value.invertX + "," + p.Value.invertY + "," + p.Value.swapAxis))));
                AddSection(lines, hash, "Force feedback settings", Join(profile.FfbOverrides));
                AddSection(lines, hash, "Virtual joystick", profile.VirtualJoystick);
                AddSection(lines, hash, "Detent calibration", Join(profile.AxisCalibrations));
                AddSection(lines, hash, "Layout preferences", profile.LayoutPreferences);
                AddSection(lines, hash, "Actions kept in several relations", profile.KeptDuplicates == null ? string.Empty
                    : string.Join("\n", profile.KeptDuplicates.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)));
                AddSection(lines, hash, "Dismissed suggestions", profile.DismissedSuggestions == null ? string.Empty
                    : string.Join("\n", profile.DismissedSuggestions.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)));
            }
            return lines;
        }

        private static void AddSection(List<string> lines, Func<string, string> hash, string name, string content)
        {
            if (!string.IsNullOrEmpty(content))
                lines.Add("S\t" + name + "\t\t\t" + hash(content));
        }

        private static string Join(Dictionary<string, string> map)
        {
            return map == null ? string.Empty : string.Join("\n", map.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value));
        }

        // "Joystick - HOTAS Warthog {GUID}" -> "Joystick - HOTAS Warthog".
        private static string DeviceLabel(string device)
        {
            string text = (device ?? string.Empty).Trim();
            int brace = text.IndexOf('{');
            return brace > 0 ? text.Substring(0, brace).Trim() : text;
        }

        private static string Clean(string text)
        {
            return (text ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static string Hex(byte[] bytes, int length)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString().Substring(0, Math.Min(length, sb.Length));
        }
    }

    public sealed class EngineHistoryVersionDto
    {
        public int Id { get; set; }
        public string Utc { get; set; }
        public string Local { get; set; }
        public string Kind { get; set; }
        public string Label { get; set; }
        public string Summary { get; set; }
        public bool Keep { get; set; }
        public long Size { get; set; }
        public int Parent { get; set; }
        public int From { get; set; }
        public bool Current { get; set; }
    }

    public sealed class EngineHistoryListDto
    {
        public string Profile { get; set; }
        public string Folder { get; set; }
        public int Cursor { get; set; }
        public bool CanUndo { get; set; }
        public string UndoLabel { get; set; }
        public bool CanRedo { get; set; }
        public string RedoLabel { get; set; }
        public long Bytes { get; set; }
        public int Kept { get; set; }
        public List<EngineHistoryVersionDto> Versions { get; set; }
    }

    public sealed class EngineHistoryDiffDto
    {
        public int From { get; set; }
        public int To { get; set; }
        public bool Available { get; set; }
        public string Summary { get; set; }
        public List<string> Changes { get; set; }
    }

    public sealed class EngineHistoryActionDto
    {
        public string Action { get; set; }
        public bool Changed { get; set; }
        public string Message { get; set; }
        public int Version { get; set; }
        public int Removed { get; set; }
        public long BytesBefore { get; set; }
        public long BytesAfter { get; set; }
        public int VersionsLeft { get; set; }
        public int Profiles { get; set; }
        public int Versions { get; set; }
        public int Kept { get; set; }
        public long Bytes { get; set; }
    }

    internal static partial class Program
    {
        // engine profile history [list] | show <id> | diff <id> [<id>|current] | restore <id> | undo | redo
        //                        | keep <id> [on|off] | checkpoint [--label <text>] | prune [--all] [--dry-run] | usage [--all]
        private static int RunProfileHistory(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command == null ? new List<string>() : new List<string>(command);
            string action = args.Count == 0 ? "list" : args[0].ToLowerInvariant();
            if (args.Count > 0)
                args.RemoveAt(0);
            bool all = PopFlag(args, "--all");
            bool dryRun = PopFlag(args, "--dry-run");
            string label = PopOptionValue(args, "--label");
            string profile = EngineProfileHistory.ProfilePath;
            bool needsProfile = !(all && (action == "prune" || action == "usage"));
            if (needsProfile && string.IsNullOrWhiteSpace(profile))
                return Fail("engine profile history " + action + " needs --profile <file>.");

            switch (action)
            {
                case "list":
                    {
                        if (args.Count > 0)
                            return Fail("Unknown profile history list argument(s): " + string.Join(" ", args));
                        ProfileHistoryLog log = ProfileHistory.Read(profile);
                        EngineHistoryListDto list = BuildHistoryList(log);
                        EmitPayload(textOutput ? HistoryListText(list) : EngineSerialization.ToJson(list), outPath);
                        return 0;
                    }
                case "show":
                case "diff":
                    {
                        ProfileHistoryLog log = ProfileHistory.Read(profile);
                        int fromId;
                        if (args.Count < 1 || !TryParseVersion(args[0], log, out fromId))
                            return Fail("engine profile history " + action + " <version id>" + (action == "diff" ? " [<version id>|current]" : string.Empty));
                        ProfileVersion version = log.Find(fromId);
                        if (version == null)
                            return Fail("Version " + args[0] + " is not in the history.");
                        int toId;
                        if (action == "show")
                        {
                            // What this version changed: against the version it was made on.
                            toId = version.Id;
                            fromId = version.Parent;
                        }
                        else if (args.Count < 2)
                        {
                            toId = log.CursorVersion == null ? 0 : log.CursorVersion.Id;
                        }
                        else if (!TryParseVersion(args[1], log, out toId))
                        {
                            return Fail("Unknown version: " + args[1]);
                        }
                        List<string> changes = fromId > 0 && toId > 0 ? ProfileHistory.Diff(profile, fromId, toId) : null;
                        EngineHistoryDiffDto diff = new EngineHistoryDiffDto
                        {
                            From = fromId,
                            To = toId,
                            Available = changes != null,
                            Summary = changes == null ? (action == "show" ? version.Summary : string.Empty) : ProfileHistory.Summarize(changes),
                            Changes = changes ?? new List<string>()
                        };
                        if (textOutput)
                        {
                            StringBuilder sb = new StringBuilder();
                            if (action == "show")
                            {
                                sb.AppendLine("Version " + version.Id.ToString(CultureInfo.InvariantCulture) + " | " + ProfileHistory.FormatLocal(version.Utc) + " | " + version.Kind + (version.Keep ? " | kept" : string.Empty));
                                sb.AppendLine(version.Description);
                            }
                            else
                            {
                                sb.AppendLine("Changes from version " + fromId.ToString(CultureInfo.InvariantCulture) + " to " + toId.ToString(CultureInfo.InvariantCulture) + ":");
                            }
                            if (!diff.Available)
                                sb.AppendLine("  (no details: one of the versions was changed outside JoyPro)");
                            else if (diff.Changes.Count == 0)
                                sb.AppendLine("  no change in relations, binds or settings");
                            foreach (string change in diff.Changes)
                                sb.AppendLine("  - " + change);
                            EmitPayload(sb.ToString().TrimEnd(), outPath);
                        }
                        else
                        {
                            EmitPayload(EngineSerialization.ToJson(diff), outPath);
                        }
                        return 0;
                    }
                case "restore":
                case "undo":
                case "redo":
                    {
                        ProfileHistoryWriteResult result;
                        if (action == "restore")
                        {
                            ProfileHistoryLog log = ProfileHistory.Read(profile);
                            int id;
                            if (args.Count < 1 || !TryParseVersion(args[0], log, out id) || log.Find(id) == null)
                                return Fail("engine profile history restore <version id> (a version of 'engine profile history list').");
                            result = ProfileHistory.Restore(profile, id);
                        }
                        else
                        {
                            result = action == "undo" ? ProfileHistory.Undo(profile) : ProfileHistory.Redo(profile);
                        }
                        EngineHistoryActionDto dto = new EngineHistoryActionDto
                        {
                            Action = action,
                            Changed = result.Changed,
                            Message = result.Message,
                            Version = result.Version == null ? 0 : result.Version.Id
                        };
                        EmitPayload(textOutput ? result.Message : EngineSerialization.ToJson(dto), outPath);
                        return 0;
                    }
                case "keep":
                    {
                        ProfileHistoryLog log = ProfileHistory.Read(profile);
                        int id;
                        if (args.Count < 1 || !TryParseVersion(args[0], log, out id))
                            return Fail("engine profile history keep <version id> [on|off]");
                        bool keep = args.Count < 2 || !(args[1].Equals("off", StringComparison.OrdinalIgnoreCase) || args[1].Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (!ProfileHistory.SetKeep(profile, id, keep))
                            return Fail("Version " + args[0] + " is not in the history.");
                        string message = "Version " + id.ToString(CultureInfo.InvariantCulture) + (keep ? " is kept (never cleaned up)." : " may be cleaned up again.");
                        EmitPayload(textOutput ? message : EngineSerialization.ToJson(new EngineHistoryActionDto { Action = "keep", Changed = true, Message = message, Version = id }), outPath);
                        return 0;
                    }
                case "checkpoint":
                    {
                        if (!File.Exists(profile))
                            return Fail("The profile file does not exist: " + profile);
                        ProfileVersion version = ProfileHistory.Checkpoint(profile, string.IsNullOrWhiteSpace(label) ? "Checkpoint" : label.Trim(), EngineProfileHistory.CurrentItems);
                        string message = version == null ? "The profile history is switched off." : "Kept as version " + version.Id.ToString(CultureInfo.InvariantCulture) + ".";
                        EmitPayload(textOutput ? message : EngineSerialization.ToJson(new EngineHistoryActionDto { Action = "checkpoint", Changed = version != null, Message = message, Version = version == null ? 0 : version.Id }), outPath);
                        return 0;
                    }
                case "prune":
                    {
                        ProfileHistoryPruneResult result = all ? ProfileHistory.PruneAll(null, dryRun) : ProfileHistory.Prune(profile, null, dryRun);
                        string message = (dryRun ? "Would remove " : "Removed ") + result.Removed.Count.ToString(CultureInfo.InvariantCulture) + " version(s): " +
                                         ProfileHistory.FormatBytes(result.BytesBefore) + " -> " + ProfileHistory.FormatBytes(result.BytesAfter) +
                                         ", " + result.VersionsLeft.ToString(CultureInfo.InvariantCulture) + " version(s) left.";
                        if (textOutput)
                        {
                            StringBuilder sb = new StringBuilder();
                            sb.AppendLine(message);
                            foreach (ProfileVersion removed in result.Removed.OrderBy(v => v.Utc))
                                sb.AppendLine("  - #" + removed.Id.ToString(CultureInfo.InvariantCulture) + " " + ProfileHistory.FormatLocal(removed.Utc) + " " + removed.Title);
                            EmitPayload(sb.ToString().TrimEnd(), outPath);
                        }
                        else
                        {
                            EmitPayload(EngineSerialization.ToJson(new EngineHistoryActionDto
                            {
                                Action = "prune",
                                Changed = !dryRun && result.Removed.Count > 0,
                                Message = message,
                                Removed = result.Removed.Count,
                                BytesBefore = result.BytesBefore,
                                BytesAfter = result.BytesAfter,
                                VersionsLeft = result.VersionsLeft
                            }), outPath);
                        }
                        return 0;
                    }
                case "usage":
                    {
                        ProfileHistoryUsage usage = all ? ProfileHistory.UsageAll() : ProfileHistory.Usage(profile);
                        string message = "Profile history" + (all ? " of " + usage.Profiles.ToString(CultureInfo.InvariantCulture) + " profile(s)" : string.Empty) + ": " +
                                         usage.Versions.ToString(CultureInfo.InvariantCulture) + " version(s), " + usage.Kept.ToString(CultureInfo.InvariantCulture) + " kept, " +
                                         ProfileHistory.FormatBytes(usage.Bytes) + " in " + ProfileHistory.Root();
                        EmitPayload(textOutput ? message : EngineSerialization.ToJson(new EngineHistoryActionDto
                        {
                            Action = "usage",
                            Message = message,
                            Profiles = usage.Profiles,
                            Versions = usage.Versions,
                            Kept = usage.Kept,
                            Bytes = usage.Bytes
                        }), outPath);
                        return 0;
                    }
                default:
                    return Fail("Unknown profile history action: " + action + " (list, show, diff, restore, undo, redo, keep, checkpoint, prune, usage).");
            }
        }

        private static bool TryParseVersion(string text, ProfileHistoryLog log, out int id)
        {
            id = 0;
            string value = (text ?? string.Empty).Trim().TrimStart('#');
            if (value.Equals("current", StringComparison.OrdinalIgnoreCase))
            {
                id = log.CursorVersion == null ? 0 : log.CursorVersion.Id;
                return id > 0;
            }
            if (value.Equals("latest", StringComparison.OrdinalIgnoreCase))
            {
                id = log.Latest == null ? 0 : log.Latest.Id;
                return id > 0;
            }
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0;
        }

        private static EngineHistoryListDto BuildHistoryList(ProfileHistoryLog log)
        {
            ProfileVersion cursor = log.CursorVersion;
            ProfileVersion undo = log.UndoSubject;
            ProfileVersion redo = log.RedoTarget;
            return new EngineHistoryListDto
            {
                Profile = log.ProfilePath,
                Folder = log.Folder,
                Cursor = cursor == null ? 0 : cursor.Id,
                CanUndo = log.CanUndo,
                UndoLabel = undo == null ? string.Empty : undo.Description,
                CanRedo = log.CanRedo,
                RedoLabel = redo == null ? string.Empty : redo.Description,
                Bytes = ProfileHistory.Usage(log.ProfilePath).Bytes,
                Kept = log.Versions.Count(v => v.Keep),
                Versions = log.Versions.AsEnumerable().Reverse().Select(v => new EngineHistoryVersionDto
                {
                    Id = v.Id,
                    Utc = v.Utc.ToString("o", CultureInfo.InvariantCulture),
                    Local = ProfileHistory.FormatLocal(v.Utc),
                    Kind = v.Kind,
                    Label = v.Title,
                    Summary = v.Summary ?? string.Empty,
                    Keep = v.Keep,
                    Size = v.Size,
                    Parent = v.Parent,
                    From = v.From,
                    Current = cursor != null && v.Id == cursor.Id
                }).ToList()
            };
        }

        private static string HistoryListText(EngineHistoryListDto list)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("History of " + list.Profile + ": " + list.Versions.Count.ToString(CultureInfo.InvariantCulture) + " version(s), " +
                          list.Kept.ToString(CultureInfo.InvariantCulture) + " kept, " + ProfileHistory.FormatBytes(list.Bytes));
            foreach (EngineHistoryVersionDto version in list.Versions)
            {
                sb.Append("  #").Append(version.Id.ToString(CultureInfo.InvariantCulture).PadRight(5)).Append(' ')
                  .Append(version.Local).Append("  ").Append(version.Kind.PadRight(10)).Append(' ')
                  .Append(version.Label);
                if (!string.IsNullOrWhiteSpace(version.Summary) && version.Summary != version.Label)
                    sb.Append(": ").Append(version.Summary);
                if (version.Keep)
                    sb.Append("  [kept]");
                if (version.Current)
                    sb.Append("  [current]");
                sb.AppendLine();
            }
            sb.AppendLine("Undo: " + (list.CanUndo ? list.UndoLabel : "nothing"));
            sb.Append("Redo: " + (list.CanRedo ? list.RedoLabel : "nothing"));
            return sb.ToString();
        }
    }
}
