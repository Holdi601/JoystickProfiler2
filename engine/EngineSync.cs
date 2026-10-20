using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using JoyPro.Engine.Axis;
using JoyPro.Engine.Games;
using Microsoft.Win32;

namespace JoyPro.Engine
{
    // engine sync status|accept|diff|apply: what changed in a game's own config since JoyPro last wrote (or accepted) it.
    //   status: per game the files (or registry values) JoyPro manages, their fingerprint (size + mtime + SHA-256) and
    //           whether it differs from the one recorded after JoyPro's last export / accept / apply.
    //   diff:   the game's config read with the game's own import code into a scratch profile (the loaded profile is
    //           never changed), compared with the binds and axis tuning the profile has for the game. Only what JoyPro
    //           manages counts, values the game merely rewrites (0.21 -> 0.20999999, reordered keys) are no change.
    //   apply:  the user's choice per change (a relation, a new relation, or ignore) goes into the profile.
    // The fingerprints live in <data>\Sync\<game>.json with a baseline (hashes of the binds the file held when recorded),
    // so a diff only reports what the game changed, not what the user changed in JoyPro since the export.
    internal static class EngineSync
    {
        private const char Sep = '\u001F';
        // The section sign between the parts of a modifier string (name, device, key, switch).
        private const char ModifierSeparator = (char)0xA7;
        private const int MaxConfigsPerGame = 8;
        private const string SyncFolder = "Sync";

        internal sealed class SyncGame
        {
            public string Id { get; set; }
            public string DisplayName { get; set; }
            // dcs, il2, starcitizen or registry
            public string Kind { get; set; }
            public IGameIntegration Registry { get; set; }
            public List<string> ProcessNames { get; set; }
            // Image path prefixes whose processes count as the game too (MSFS Microsoft Store: the XboxGames and
            // WindowsApps install folders, whatever the exe is called there, e.g. gamelaunchhelper.exe).
            public List<string> ProcessFolders { get; set; }
        }

        internal sealed class SyncWatch
        {
            public string Directory { get; set; }
            public List<string> Filters { get; set; }
            public bool IncludeSubdirectories { get; set; }
        }

        private sealed class SyncLocation
        {
            public string ConfigPath = string.Empty;
            public string RegistryKey = string.Empty;
            public string OwnedPrefix = string.Empty;
            public List<SyncWatch> Watch = new List<SyncWatch>();
            public bool Exists;
            // Seconds without a write before the watcher checks (0: the watcher's default). The Microsoft Store's save
            // folder is written several times within seconds of each other (the sim, then the Xbox app's sync after the
            // sim closed), so it waits longer and checks once.
            public int SettleSeconds;
        }

        // One file (or the registry key) of a game's config as JoyPro saw it.
        private sealed class FileStamp
        {
            public string Path { get; set; }
            public long Size { get; set; }
            public long MtimeTicks { get; set; }
            public string Sha256 { get; set; }
        }

        private sealed class SyncRecord
        {
            public string ConfigPath { get; set; }
            public string RecordedUtc { get; set; }
            public string Reason { get; set; }
            public string Fingerprint { get; set; }
            public List<FileStamp> Items { get; set; }
            // Hashes of plane|action|device|input|modifiers of every bind the file held (Keys) and of the axis tuning of
            // the axes (States); null when the file could not be read.
            public List<string> BaselineKeys { get; set; }
            public List<string> BaselineStates { get; set; }
            // Hashes of key=value of the game-wide settings (device *) the config held.
            public List<string> BaselineSettings { get; set; }
        }

        private sealed class SyncDocument
        {
            public string Game { get; set; }
            public List<SyncRecord> Configs { get; set; }
        }

        // A bind of one action in one plane: the unit the diff compares.
        private sealed class SyncEntry
        {
            public string Plane = string.Empty;
            public string ActionId = string.Empty;
            public string ActionKey = string.Empty;
            public string Device = string.Empty;
            public string Input = string.Empty;
            public bool IsAxis;
            public List<string> Reformers = new List<string>();
            public AxisTuning Tuning;
            public string Relation = string.Empty;
            public string Title = string.Empty;
            public List<string> Groups = new List<string>();
            public string Key = string.Empty;
            public string LooseKey = string.Empty;
            public string TuningText = string.Empty;

            public bool IsKeyboard
            {
                get { return string.Equals(Device.Trim(), "Keyboard", StringComparison.OrdinalIgnoreCase); }
            }

            public string Signature
            {
                get { return GameProfileBridge.BuildSignature(Device, IsAxis, Input, Reformers); }
            }
        }

        private sealed class SyncChange
        {
            public string Id = string.Empty;
            public string Kind = string.Empty;
            public string GameId = string.Empty;
            public string Plane = string.Empty;
            public string ActionId = string.Empty;
            public string ActionKey = string.Empty;
            public string ActionTitle = string.Empty;
            public bool IsAxis;
            public string Device = string.Empty;
            public string Input = string.Empty;
            public string LooseKey = string.Empty;
            public List<string> Modifiers = new List<string>();
            public List<string> OldModifiers = new List<string>();
            public AxisTuning OldTuning;
            public AxisTuning NewTuning;
            // changed-setting: a game-wide setting (device *) of the game's FfbModel.
            public string SettingKey = string.Empty;
            public string SettingLabel = string.Empty;
            public double OldValue;
            public double NewValue;
            // Relations of the profile that hold the bind as it was (removed / changed-*).
            public List<string> Relations = new List<string>();
            public List<string> Candidates = new List<string>();
            public string Suggested = string.Empty;
            public string NewRelationName = string.Empty;
            public List<string> NewGroups = new List<string>();
        }

        private sealed class SyncDiff
        {
            public SyncGame Game;
            public SyncLocation Location;
            public bool HasBaseline;
            public bool Tracked;
            public List<string> Planes = new List<string>();
            public int ProfileBinds;
            public int FileBinds;
            public List<SyncChange> Changes = new List<SyncChange>();
            public List<string> Warnings = new List<string>();
            public List<string> Errors = new List<string>();
        }

        private sealed class SyncChoice
        {
            public string Id { get; set; }
            public string Target { get; set; }
            public string Name { get; set; }
        }

        private sealed class SyncChoicesDocument
        {
            public List<SyncChoice> Choices { get; set; }
        }

        private sealed class StatusResult
        {
            public SyncGame Game;
            public SyncLocation Location;
            public bool Installed;
            public bool Disabled;
            public string State = string.Empty;
            public SyncRecord Record;
            public List<FileStamp> Current = new List<FileStamp>();
            public List<string> ChangedFiles = new List<string>();
            public string Fingerprint = string.Empty;
            public List<string> Warnings = new List<string>();
        }

        // ---- CLI ---------------------------------------------------------------------------------------------------

        public static readonly string[] Actions = { "status", "accept", "diff", "apply" };

        public static bool IsMutating(List<string> command)
        {
            string action = command == null || command.Count == 0 ? string.Empty : (command[0] ?? string.Empty).Trim().ToLowerInvariant();
            return action == "apply";
        }

        public static int Run(List<string> command, bool textOutput, out string payload)
        {
            payload = string.Empty;
            string action = command.Count == 0 ? "status" : command[0].ToLowerInvariant();
            List<string> args = command.Skip(1).ToList();
            string gameName = PopValue(args, "--game");
            string path = PopValue(args, "--path");
            string choicesPath = PopValue(args, "--choices");
            List<string> unknown = args.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
            if (unknown.Count > 0)
                return Fail("Unknown sync " + action + " argument(s): " + string.Join(" ", unknown));
            if (!string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(gameName))
                return Fail("--path needs --game.");

            SyncGame game = null;
            if (!string.IsNullOrWhiteSpace(gameName) && !TryResolveGame(gameName, out game))
                return Fail("Unknown game: " + gameName + ". Use DCS, IL2Game, StarCitizen or a game of engine games integrations.");
            if (action != "status" && game == null)
                return Fail("engine sync " + action + " needs --game <id>.");

            switch (action)
            {
                case "status":
                    return RunStatus(game, path, textOutput, out payload);
                case "accept":
                    return RunAccept(game, path, textOutput, out payload);
                case "diff":
                    return RunDiff(game, path, textOutput, out payload);
                case "apply":
                    if (string.IsNullOrWhiteSpace(choicesPath))
                        return Fail("engine sync apply needs --choices <json file>.");
                    return RunApply(game, path, choicesPath, textOutput, out payload);
                default:
                    return Fail("Unknown sync action: " + action + ". Use " + string.Join(", ", Actions) + ".");
            }
        }

        private static int RunStatus(SyncGame only, string explicitPath, bool textOutput, out string payload)
        {
            List<StatusResult> results = new List<StatusResult>();
            foreach (SyncGame game in only != null ? new List<SyncGame> { only } : AllGames())
            {
                StatusResult main = Status(game, explicitPath, only != null);
                results.Add(main);
                if (!string.IsNullOrWhiteSpace(explicitPath) || main.State == "disabled" || main.State == "not-installed")
                    continue;
                // Configs JoyPro exported to elsewhere (a custom --path) are watched too.
                foreach (SyncRecord other in LoadDocument(game.Id).Configs.Where(c => c != null && !string.IsNullOrWhiteSpace(c.ConfigPath) && !SamePath(c.ConfigPath, main.Location.ConfigPath)))
                {
                    StatusResult extra = Status(game, other.ConfigPath, true);
                    if (extra.State != "missing")
                        results.Add(extra);
                }
            }

            foreach (StatusResult r in results)
            {
                JoyPro.Logging.JoyProLog.Debug("Sync", "Status " + r.Game.Id + ": " + r.State + StateNote(r.State) + (string.IsNullOrEmpty(r.Location.ConfigPath) ? string.Empty : " | " + r.Location.ConfigPath) +
                                                       (r.ChangedFiles.Count > 0 ? " | changed: " + string.Join(", ", r.ChangedFiles) : string.Empty) +
                                                       WatchNote(r) +
                                                       (r.Warnings.Count > 0 ? " | warnings: " + string.Join(" | ", r.Warnings) : string.Empty));
                LogComparison(r);
            }

            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Game Config Sync");
                sb.AppendLine("Data: " + SyncDirectory());
                foreach (StatusResult r in results)
                {
                    sb.AppendLine(r.Game.Id + " | " + r.State + (string.IsNullOrEmpty(r.Location.ConfigPath) ? string.Empty : " | " + r.Location.ConfigPath));
                    if (r.Record != null)
                        sb.AppendLine("  recorded " + r.Record.RecordedUtc + " (" + r.Record.Reason + ")");
                    foreach (string file in r.ChangedFiles)
                        sb.AppendLine("  changed: " + file);
                    foreach (string warning in r.Warnings)
                        sb.AppendLine("  warning: " + warning);
                }
                payload = sb.ToString();
            }
            else
            {
                payload = EngineSerialization.ToJson(new
                {
                    DataFolder = SyncDirectory(),
                    Games = results.Select(StatusJson).ToList()
                });
            }
            return 0;
        }

        // Why a game is not watched (diagnostics: the support file shows where the chain stops).
        private static string StateNote(string state)
        {
            switch (state)
            {
                case "untracked": return " (no export or import recorded yet: the watcher does not watch it)";
                case "missing": return " (no config found: not watched)";
                case "not-installed": return " (not installed: not watched)";
                case "disabled": return " (\"I do not have this game\": not watched)";
                default: return string.Empty;
            }
        }

        // What the watcher gets for a tracked game (folders, filters, processes, wait), when it changed or at most every
        // 10 minutes per game.
        private static string WatchNote(StatusResult r)
        {
            if (r.Record == null || r.Location == null || !(r.State == "changed" || JoyPro.Logging.JoyProLog.ShouldLog("sync-watch-" + r.Game.Id + "|" + r.Location.ConfigPath, TimeSpan.FromMinutes(10))))
                return string.Empty;
            return " | watched: " + (r.Location.RegistryKey.Length > 0 ? r.Location.RegistryKey : string.Join("; ", r.Location.Watch.Select(w => w.Directory + "\\" + string.Join("|", w.Filters) + (w.IncludeSubdirectories ? " (with subfolders)" : string.Empty)))) +
                   " | processes: " + string.Join(", ", r.Game.ProcessNames ?? new List<string>()) +
                   ((r.Game.ProcessFolders ?? new List<string>()).Count > 0 ? " and any exe under " + string.Join(", ", r.Game.ProcessFolders) : string.Empty) +
                   (r.Location.SettleSeconds > 0 ? " | settle " + r.Location.SettleSeconds.ToString(CultureInfo.InvariantCulture) + " s" : string.Empty) +
                   " | recorded " + (r.Record.Reason ?? "-") + " " + (r.Record.RecordedUtc ?? "-");
        }

        private static object StatusJson(StatusResult r)
        {
            return new
            {
                Game = r.Game.Id,
                DisplayName = r.Game.DisplayName,
                Installed = r.Installed,
                Disabled = r.Disabled,
                ConfigPath = r.Location.ConfigPath,
                Kind = r.Location.RegistryKey.Length > 0 ? "registry" : "files",
                State = r.State,
                Tracked = r.Record != null,
                Changed = r.State == "changed",
                RecordedUtc = r.Record == null ? string.Empty : r.Record.RecordedUtc,
                Reason = r.Record == null ? string.Empty : r.Record.Reason,
                Fingerprint = r.Fingerprint,
                Files = r.Current.Count,
                ChangedFiles = r.ChangedFiles,
                ChangedPlanes = r.Game.Kind == "dcs" ? DcsPlanesOf(r.Location, r.ChangedFiles) : new List<string>(),
                Watch = r.Location.Watch.Select(w => new { w.Directory, w.Filters, w.IncludeSubdirectories }).ToList(),
                RegistryKey = r.Location.RegistryKey,
                ProcessNames = r.Game.ProcessNames,
                ProcessFolders = r.Game.ProcessFolders ?? new List<string>(),
                SettleSeconds = r.Location.SettleSeconds,
                Warnings = r.Warnings
            };
        }

        private static int RunAccept(SyncGame game, string explicitPath, bool textOutput, out string payload)
        {
            List<string> warnings = new List<string>();
            SyncLocation location = ResolveLocation(game, explicitPath, warnings);
            if (!location.Exists)
            {
                payload = string.Empty;
                return Fail("No " + game.DisplayName + " config found" + (string.IsNullOrEmpty(location.ConfigPath) ? "." : ": " + location.ConfigPath));
            }
            SyncRecord record = Record(game, location, "accept", warnings);
            payload = textOutput
                ? game.Id + ": the current config (" + location.ConfigPath + ") is known now; fingerprint " + record.Fingerprint + Environment.NewLine +
                  string.Concat(warnings.Select(w => "warning: " + w + Environment.NewLine))
                : EngineSerialization.ToJson(new
                {
                    Game = game.Id,
                    ConfigPath = location.ConfigPath,
                    State = "unchanged",
                    record.Fingerprint,
                    record.RecordedUtc,
                    Baseline = record.BaselineKeys != null,
                    Warnings = warnings
                });
            return 0;
        }

        private static int RunDiff(SyncGame game, string explicitPath, bool textOutput, out string payload)
        {
            SyncDiff diff = ComputeDiff(game, explicitPath);
            // The comparison's result: what the watcher's dialog will list (none: the GUI accepts the state silently).
            JoyPro.Logging.JoyProLog.Debug("Sync", "Diff " + game.Id + ": " + diff.Changes.Count.ToString(CultureInfo.InvariantCulture) + " change(s)" +
                                                   (diff.Changes.Count == 0 ? " (nothing JoyPro manages changed: only formatting, or binds JoyPro does not manage)" : ": " +
                                                    string.Join("; ", diff.Changes.Take(30).Select(c => c.Id + " " + c.Kind + ": " + Describe(c)))) +
                                                   " | baseline: " + (diff.HasBaseline ? "yes" : "no") + " | binds JoyPro manages: " + diff.FileBinds.ToString(CultureInfo.InvariantCulture) +
                                                   " in the game, " + diff.ProfileBinds.ToString(CultureInfo.InvariantCulture) + " in the profile" + (diff.Errors.Count > 0 ? " | errors: " + string.Join(" | ", diff.Errors) : string.Empty));
            payload = textOutput ? DiffText(diff) : EngineSerialization.ToJson(DiffJson(diff));
            if (diff.Errors.Count > 0)
            {
                Console.Error.WriteLine("sync diff failed: " + string.Join(" | ", diff.Errors));
                return 1;
            }
            return 0;
        }

        private static object DiffJson(SyncDiff diff)
        {
            return new
            {
                Game = diff.Game.Id,
                DisplayName = diff.Game.DisplayName,
                ConfigPath = diff.Location == null ? string.Empty : diff.Location.ConfigPath,
                Tracked = diff.Tracked,
                Baseline = diff.HasBaseline,
                Planes = diff.Planes,
                ProfileBinds = diff.ProfileBinds,
                FileBinds = diff.FileBinds,
                Changes = diff.Changes.Select(c => new
                {
                    c.Id,
                    c.Kind,
                    Game = diff.Game.Id,
                    c.Plane,
                    c.ActionId,
                    c.ActionTitle,
                    c.IsAxis,
                    c.Device,
                    c.Input,
                    Modifiers = c.Modifiers.Select(ModifierLabel).ToList(),
                    ModifierStrings = c.Modifiers,
                    OldModifiers = c.OldModifiers.Select(ModifierLabel).ToList(),
                    OldTuning = c.OldTuning == null ? string.Empty : c.OldTuning.Format(),
                    NewTuning = c.NewTuning == null ? string.Empty : c.NewTuning.Format(),
                    c.Relations,
                    c.Candidates,
                    c.Suggested,
                    c.NewRelationName,
                    c.SettingKey,
                    c.SettingLabel,
                    OldValue = c.Kind == "changed-setting" ? (double?)c.OldValue : null,
                    NewValue = c.Kind == "changed-setting" ? (double?)c.NewValue : null,
                    Description = Describe(c)
                }).ToList(),
                Warnings = diff.Warnings,
                Errors = diff.Errors
            };
        }

        private static string DiffText(SyncDiff diff)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(diff.Game.DisplayName + " Config Changes");
            sb.AppendLine("Config: " + (diff.Location == null || diff.Location.ConfigPath.Length == 0 ? "<none>" : diff.Location.ConfigPath));
            sb.AppendLine("Baseline: " + (diff.HasBaseline ? "yes" : "no (compared with the profile only)"));
            if (diff.Planes.Count > 0)
                sb.AppendLine("Planes: " + string.Join(", ", diff.Planes));
            sb.AppendLine("Changes: " + diff.Changes.Count.ToString(CultureInfo.InvariantCulture));
            foreach (SyncChange c in diff.Changes)
            {
                sb.AppendLine("  - " + c.Id + " " + c.Kind + ": " + Describe(c));
                if (c.Candidates.Count > 0)
                    sb.AppendLine("      candidates: " + string.Join(" | ", c.Candidates) + "; suggested: " + c.Suggested);
                else
                    sb.AppendLine("      suggested: " + c.Suggested);
            }
            foreach (string warning in diff.Warnings)
                sb.AppendLine("Warning: " + warning);
            foreach (string error in diff.Errors)
                sb.AppendLine("Error: " + error);
            return sb.ToString();
        }

        private static string Describe(SyncChange c)
        {
            string where = c.Plane.Length > 0 && !string.Equals(c.Plane, c.GameId, StringComparison.OrdinalIgnoreCase) ? c.Plane + ": " : string.Empty;
            string title = c.ActionTitle.Length > 0 ? c.ActionTitle : c.ActionId;
            string input = c.Device + " " + c.Input + (c.Modifiers.Count > 0 ? " + " + string.Join(" + ", c.Modifiers.Select(ModifierLabel)) : string.Empty);
            switch (c.Kind)
            {
                case "changed-setting":
                    return "Game setting " + (c.SettingLabel.Length > 0 ? c.SettingLabel + " (" + c.SettingKey + ")" : c.SettingKey) + ": " +
                           AxisTuning.Num(c.OldValue) + " -> " + AxisTuning.Num(c.NewValue);
                case "added":
                    return where + title + " now on " + input;
                case "removed":
                    return where + title + " no longer on " + input;
                case "changed-modifiers":
                    return where + title + " on " + c.Device + " " + c.Input + ": modifiers " +
                           (c.OldModifiers.Count == 0 ? "none" : string.Join(" + ", c.OldModifiers.Select(ModifierLabel))) + " -> " +
                           (c.Modifiers.Count == 0 ? "none" : string.Join(" + ", c.Modifiers.Select(ModifierLabel)));
                default:
                    return where + title + " on " + input + ": axis tuning " + (c.OldTuning == null ? "?" : c.OldTuning.Format()) + " -> " + (c.NewTuning == null ? "?" : c.NewTuning.Format());
            }
        }

        private static string ModifierLabel(string reformer)
        {
            string[] parts = (reformer ?? string.Empty).Split(ModifierSeparator);
            return parts.Length >= 3 ? parts[1] + " " + parts[2] : reformer ?? string.Empty;
        }

        private static int RunApply(SyncGame game, string explicitPath, string choicesPath, bool textOutput, out string payload)
        {
            payload = string.Empty;
            List<SyncChoice> choices;
            string error;
            if (!TryReadChoices(choicesPath, out choices, out error))
                return Fail(error);

            SyncDiff diff = ComputeDiff(game, explicitPath);
            if (diff.Errors.Count > 0)
                return Fail("sync apply: the game's config could not be read: " + string.Join(" | ", diff.Errors));

            List<string> steps = new List<string>();
            List<string> warnings = new List<string>(diff.Warnings);
            List<string> created = new List<string>();
            List<string> changed = new List<string>();
            int applied = 0;
            int ignored = 0;
            Dictionary<string, SyncChange> byId = diff.Changes.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Removals first, so a change that moves a bind to another relation is not undone by its old relation's removal.
            foreach (SyncChoice choice in choices.Where(c => c != null && !string.IsNullOrWhiteSpace(c.Id))
                         .OrderBy(c => byId.ContainsKey(c.Id.Trim()) && byId[c.Id.Trim()].Kind == "removed" ? 0 : 1))
            {
                string id = choice.Id.Trim();
                SyncChange change;
                if (!byId.TryGetValue(id, out change))
                {
                    warnings.Add("Change " + id + " is not (or no longer) in the game's config; skipped.");
                    continue;
                }
                if (!seen.Add(id))
                    continue;
                string target = (choice.Target ?? string.Empty).Trim();
                if (target.Length == 0 || target.Equals("ignore", StringComparison.OrdinalIgnoreCase))
                {
                    ignored++;
                    continue;
                }
                if (change.Kind == "changed-setting")
                {
                    // Any target but ignore ("apply") takes the game's value as the profile's setting for the game.
                    ApplySetting(game, change);
                    steps.Add(Describe(change));
                    applied++;
                    continue;
                }
                string relationName;
                if (ApplyChange(game, change, target, choice.Name, steps, warnings, out relationName))
                {
                    applied++;
                    if (target.Equals("new", StringComparison.OrdinalIgnoreCase))
                        created.Add(relationName);
                    else if (!changed.Contains(relationName, StringComparer.OrdinalIgnoreCase))
                        changed.Add(relationName);
                }
            }
            ignored += diff.Changes.Count(c => !seen.Contains(c.Id));

            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();

            // The file as it is now is the new baseline: the changes the user ignored are not offered again.
            SyncRecord record = diff.Location != null && diff.Location.Exists ? Record(game, diff.Location, "apply", warnings) : null;

            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(game.DisplayName + " Config Changes Applied");
                sb.AppendLine("Applied: " + applied.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Ignored: " + ignored.ToString(CultureInfo.InvariantCulture));
                if (created.Count > 0)
                    sb.AppendLine("New Relations: " + string.Join(", ", created));
                if (changed.Count > 0)
                    sb.AppendLine("Changed Relations: " + string.Join(", ", changed));
                foreach (string step in steps)
                    sb.AppendLine("  - " + step);
                foreach (string warning in warnings)
                    sb.AppendLine("Warning: " + warning);
                payload = sb.ToString();
            }
            else
            {
                payload = EngineSerialization.ToJson(new
                {
                    Game = game.Id,
                    ConfigPath = diff.Location == null ? string.Empty : diff.Location.ConfigPath,
                    Applied = applied,
                    Ignored = ignored,
                    NewRelations = created,
                    ChangedRelations = changed,
                    Fingerprint = record == null ? string.Empty : record.Fingerprint,
                    Steps = steps,
                    Warnings = warnings
                });
            }
            return 0;
        }

        private static bool TryReadChoices(string path, out List<SyncChoice> choices, out string error)
        {
            choices = new List<SyncChoice>();
            error = string.Empty;
            string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            if (!File.Exists(full))
            {
                error = "Choices file not found: " + full;
                return false;
            }
            try
            {
                string json = File.ReadAllText(full, Encoding.UTF8).Trim();
                if (json.StartsWith("[", StringComparison.Ordinal))
                    choices = EngineSerialization.FromJson<List<SyncChoice>>(json) ?? new List<SyncChoice>();
                else
                {
                    SyncChoicesDocument document = EngineSerialization.FromJson<SyncChoicesDocument>(json);
                    choices = document == null || document.Choices == null ? new List<SyncChoice>() : document.Choices;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Choices file " + full + " is no JSON list of {Id, Target, Name}: " + ex.Message;
                return false;
            }
        }

        // ---- Hooks for the exporters --------------------------------------------------------------------------------

        // After a successful export: the written config is JoyPro's own state, so the watcher does not ask about it.
        // Never fails the export; problems become warnings.
        public static void RecordAfterExport(string gameId, string configPath, List<string> warnings)
        {
            try
            {
                SyncGame game;
                if (!TryResolveGame(gameId, out game))
                    return;
                List<string> local = new List<string>();
                SyncLocation location = ResolveLocation(game, configPath, local);
                if (!location.Exists)
                    return;
                Record(game, location, "export", local);
                JoyPro.Logging.JoyProLog.Info("Sync", "Recorded the exported config of " + game.Id + " as JoyPro's state: " + location.ConfigPath);
                if (warnings != null)
                    warnings.AddRange(local.Select(w => "Sync: " + w));
            }
            catch (Exception ex)
            {
                JoyPro.Logging.JoyProLog.Warn("Sync", "Could not record the exported config of " + gameId, ex);
                if (warnings != null)
                    warnings.Add("Sync: the fingerprint of the exported config could not be recorded: " + ex.Message);
            }
        }

        // The config location of a game as the watcher sees it: the config path and the watched folders (diagnostics, the
        // support bundle). Empty for an unknown game.
        public static List<string> ConfigPathsOf(string gameId, out string displayName)
        {
            displayName = gameId;
            List<string> paths = new List<string>();
            SyncGame game;
            if (!TryResolveGame(gameId, out game))
                return paths;
            displayName = game.DisplayName;
            SyncLocation location = ResolveLocation(game, null, new List<string>());
            if (!string.IsNullOrWhiteSpace(location.ConfigPath))
                paths.Add(location.ConfigPath);
            paths.AddRange(location.Watch.Select(w => w.Directory).Where(d => !string.IsNullOrWhiteSpace(d)));
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<string> AllGameIds()
        {
            return AllGames().Select(g => g.Id).ToList();
        }

        // After an import: a game JoyPro has no record of yet (never exported, "untracked") gets the imported state as its
        // record, so the watcher watches it from now on (the profile holds what the game has). A game with a record keeps
        // it (the next export records it again).
        public static void RecordAfterImport(string gameId, string configPath, List<string> warnings)
        {
            try
            {
                SyncGame game;
                if (!TryResolveGame(gameId, out game))
                    return;
                List<string> local = new List<string>();
                SyncLocation location = ResolveLocation(game, configPath, local);
                if (!location.Exists || FindRecord(game.Id, location.ConfigPath) != null)
                    return;
                Record(game, location, "import", local);
                JoyPro.Logging.JoyProLog.Info("Sync", "Recorded the imported config of " + game.Id + " (it had no record yet), so the watcher watches it: " + location.ConfigPath);
            }
            catch (Exception ex)
            {
                JoyPro.Logging.JoyProLog.Warn("Sync", "Could not record the imported config of " + gameId, ex);
                if (warnings != null)
                    warnings.Add("Sync: the fingerprint of the imported config could not be recorded: " + ex.Message);
            }
        }

        // ---- Games and their config locations -----------------------------------------------------------------------

        public static List<SyncGame> AllGames()
        {
            List<SyncGame> games = new List<SyncGame>
            {
                new SyncGame { Id = "DCS", DisplayName = "DCS World", Kind = "dcs", ProcessNames = new List<string> { "DCS" } },
                new SyncGame { Id = "IL2Game", DisplayName = "IL-2 Sturmovik: Great Battles", Kind = "il2", ProcessNames = new List<string> { "Il-2" } },
                new SyncGame { Id = "StarCitizen", DisplayName = "Star Citizen", Kind = "starcitizen", ProcessNames = new List<string> { "StarCitizen" } }
            };
            foreach (IGameIntegration integration in GameRegistry.All)
            {
                games.Add(new SyncGame
                {
                    Id = integration.GameId,
                    DisplayName = integration.DisplayName,
                    Kind = "registry",
                    Registry = integration,
                    ProcessNames = integration.ProcessNames.Select(p => Path.GetFileNameWithoutExtension(p)).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                });
            }
            Dictionary<string, List<string>> testNames = TestProcessNames();
            foreach (SyncGame game in games)
            {
                Games.MsfsIntegration msfs = game.Registry as Games.MsfsIntegration;
                game.ProcessFolders = msfs != null ? MsfsProcessFolders(msfs.Sim) : new List<string>();
                List<string> names;
                if (testNames.TryGetValue(game.Id, out names))
                    game.ProcessNames = names;
            }
            return games;
        }

        // Tests: the process names engine sync status reports for a game instead of the game's own
        // ("MSFS2024=jpfake1,jpfake2;DCS=x"; "MSFS2024=" for none), so a stand-in process plays the game and a real game
        // the developer plays meanwhile does not make a test wait (docs/sync-watcher.md, docs/testing.md).
        public const string TestProcessNamesVariable = "JOYPRO_TEST_GAME_PROCESSES";

        private static Dictionary<string, List<string>> TestProcessNames()
        {
            Dictionary<string, List<string>> names = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string value = Environment.GetEnvironmentVariable(TestProcessNamesVariable);
            if (string.IsNullOrWhiteSpace(value))
                return names;
            foreach (string part in value.Split(';'))
            {
                int equals = part.IndexOf('=');
                if (equals <= 0)
                    continue;
                names[part.Substring(0, equals).Trim()] = part.Substring(equals + 1).Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
            }
            return names;
        }

        // The Microsoft Store build runs from <drive>\XboxGames\<game>\Content (an older install from
        // <drive>\[Program Files\]WindowsApps\<family>_<version>...): any process started from there counts as the sim,
        // whatever its exe is called (FlightSimulator2024.exe, gamelaunchhelper.exe). Image path prefixes, compared by the
        // watcher. Tests (JOYPRO_TEST_MSFS_HOME): <home>\XboxGames only.
        private static List<string> MsfsProcessFolders(int sim)
        {
            List<string> folders = new List<string>();
            string game = sim == 2020 ? "Microsoft Flight Simulator" : "Microsoft Flight Simulator 2024";
            string family = Games.MsfsUserProfiles.StorePackageFamily(sim);
            string familyPrefix = family.Substring(0, family.IndexOf('_') + 1);
            string home = Environment.GetEnvironmentVariable(Games.MsfsUserProfiles.TestHomeVariable);
            if (!string.IsNullOrWhiteSpace(home))
            {
                folders.Add(Path.Combine(home.Trim(), "XboxGames", game, "Content") + "\\");
                return folders;
            }
            try
            {
                folders.AddRange(Games.MsfsGameData.StoreContentFolders(sim).Select(f => f.TrimEnd('\\') + "\\"));
                foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
                {
                    folders.Add(Path.Combine(drive.Name, "Program Files", "WindowsApps", familyPrefix));
                    folders.Add(Path.Combine(drive.Name, "WindowsApps", familyPrefix));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A drive that is not ready: its folders are left out.
            }
            return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool TryResolveGame(string name, out SyncGame game)
        {
            string id = GameInstalls.Resolve(name);
            game = AllGames().FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase) || string.Equals(g.Id, (name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
            return game != null;
        }

        private static SyncLocation ResolveLocation(SyncGame game, string explicitPath, List<string> warnings)
        {
            SyncLocation location = new SyncLocation();
            string path = string.IsNullOrWhiteSpace(explicitPath) ? null : explicitPath.Trim().Trim('"');
            if (path != null && !GameConfigLocation.IsRegistryKey(path))
                path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            try
            {
                switch (game.Kind)
                {
                    case "dcs":
                        {
                            if (path == null)
                                path = GamePathDiscovery.DiscoverDcs(warnings).PreferredConfigPath;
                            if (string.IsNullOrWhiteSpace(path))
                                return location;
                            location.ConfigPath = FullPath(path).TrimEnd('\\', '/');
                            string input = DcsInputRoot(location.ConfigPath);
                            location.Exists = Directory.Exists(input) && Directory.EnumerateFiles(input, "*.diff.lua", SearchOption.AllDirectories).Any();
                            location.Watch.Add(new SyncWatch { Directory = input, Filters = new List<string> { "*.diff.lua", "modifiers.lua" }, IncludeSubdirectories = true });
                            return location;
                        }
                    case "il2":
                        {
                            if (path == null)
                                path = GameInstalls.ConfigOverride("IL2Game") ?? GamePathDiscovery.DiscoverIl2(warnings).PreferredInstallPath;
                            if (string.IsNullOrWhiteSpace(path))
                                return location;
                            location.ConfigPath = FullPath(path).TrimEnd('\\', '/');
                            string input = EngineIl2.ResolveInputPath(location.ConfigPath) ?? string.Empty;
                            location.Exists = input.Length > 0 && File.Exists(Path.Combine(input, "global.actions"));
                            if (input.Length > 0)
                            {
                                location.Watch.Add(new SyncWatch { Directory = input, Filters = new List<string>(Il2Files), IncludeSubdirectories = false });
                                // data\startup.cfg: the force feedback section JoyPro writes (game-wide settings).
                                location.Watch.Add(new SyncWatch { Directory = Path.GetDirectoryName(input.TrimEnd('\\', '/')) ?? string.Empty, Filters = new List<string> { "startup.cfg" }, IncludeSubdirectories = false });
                            }
                            return location;
                        }
                    case "starcitizen":
                        {
                            string file = EngineStarCitizen.ResolveActionmapsFile(path, warnings);
                            if (string.IsNullOrWhiteSpace(file))
                            {
                                location.ConfigPath = path ?? string.Empty;
                                return location;
                            }
                            location.ConfigPath = file = FullPath(file);
                            location.Exists = File.Exists(file);
                            location.Watch.Add(new SyncWatch { Directory = Path.GetDirectoryName(file), Filters = new List<string> { Path.GetFileName(file), "attributes.xml" }, IncludeSubdirectories = false });
                            return location;
                        }
                    default:
                        {
                            if (path == null)
                                path = GameInstalls.WithConfigOverride(game.Id, game.Registry.DiscoverConfigPaths(warnings)).FirstOrDefault();
                            if (string.IsNullOrWhiteSpace(path))
                                return location;
                            location.ConfigPath = path = FullPath(path);
                            if (GameConfigLocation.IsRegistryKey(path))
                            {
                                location.RegistryKey = path;
                                location.OwnedPrefix = OwnedRegistryPrefix(game.Id);
                                using (RegistryKey key = OpenRegistryKey(path))
                                    location.Exists = key != null;
                                return location;
                            }
                            // MSFS: the user's own profiles (Steam remote folders, the Microsoft Store's save containers) are
                            // where a change made in the sim lands; JoyPro's preset package is only the export target.
                            bool userProfiles = game.Registry is Games.MsfsIntegration msfs && AddMsfsUserWatches(msfs, location, path, warnings);
                            if (Directory.Exists(path))
                            {
                                location.Exists = true;
                                location.Watch.Insert(0, new SyncWatch { Directory = path, Filters = new List<string>(DirectoryPatterns(game.Id)), IncludeSubdirectories = false });
                                return location;
                            }
                            location.Exists = File.Exists(path) || userProfiles;
                            location.Watch.Add(new SyncWatch { Directory = Path.GetDirectoryName(path), Filters = new List<string> { Path.GetFileName(path) }, IncludeSubdirectories = false });
                            AddSettingsWatch(game, location);
                            return location;
                        }
                }
            }
            catch (Exception ex)
            {
                warnings.Add(game.DisplayName + ": " + ex.Message);
                return location;
            }
        }

        // Steam: inputprofile_* of every remote folder of the sim (profile_00, which the sim rewrites on every start, is
        // left out); Microsoft Store: containers.index of the save folder, which changes with every save of a container
        // (a save that is no input profile gives an empty diff, accepted without asking). True when one exists.
        private const int MsfsStoreSettleSeconds = 10;

        private static bool AddMsfsUserWatches(Games.MsfsIntegration msfs, SyncLocation location, string configPath, List<string> warnings)
        {
            bool any = false;
            foreach (string remote in Games.MsfsUserProfiles.SteamRemoteFolders(msfs.Sim, warnings))
            {
                any = true;
                if (string.Equals(FullPath(remote).TrimEnd('\\'), configPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    continue;
                location.Watch.Add(new SyncWatch { Directory = remote, Filters = new List<string> { "inputprofile_*" }, IncludeSubdirectories = false });
            }
            string wgs = Games.MsfsUserProfiles.StoreWgsFolder(msfs.Sim);
            if (Directory.Exists(wgs))
            {
                any = true;
                location.Watch.Add(new SyncWatch { Directory = wgs, Filters = new List<string> { "containers.index" }, IncludeSubdirectories = true });
                // A save replaces containers.index (deleted and created again) several times: the sim while it runs, the
                // Xbox app's sync for up to ~15 s after it closed, 3 to 8 s apart (2.2.7 tester logs). One check after
                // that, instead of one per write that reads the save in the middle of the sync.
                location.SettleSeconds = MsfsStoreSettleSeconds;
            }
            return any;
        }

        // Game-wide settings kept in other files than the config (X4 config.xml, VTOL VR gameSettings.cfg, Arma Reforger's
        // game settings): watched with it, so a change in the game's menu makes the config "changed".
        private static void AddSettingsWatch(SyncGame game, SyncLocation location)
        {
            IGameSettingsSource source = game.Registry as IGameSettingsSource;
            if (source == null)
                return;
            foreach (string file in source.SettingsFiles(location.ConfigPath) ?? new List<string>())
            {
                string directory = Path.GetDirectoryName(FullPath(file)) ?? string.Empty;
                SyncWatch watch = location.Watch.FirstOrDefault(w => string.Equals(w.Directory, directory, StringComparison.OrdinalIgnoreCase) && !w.IncludeSubdirectories);
                if (watch == null)
                    location.Watch.Add(watch = new SyncWatch { Directory = directory, Filters = new List<string>(), IncludeSubdirectories = false });
                if (!watch.Filters.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                    watch.Filters.Add(Path.GetFileName(file));
            }
        }

        // The game whose FfbModel holds the game-wide settings (VTOL VR native: VTOLVR), else the game itself.
        private static string SettingsGameId(SyncGame game)
        {
            IGameSettingsSource source = game.Registry as IGameSettingsSource;
            return source != null && !string.IsNullOrWhiteSpace(source.SettingsGame) ? source.SettingsGame : game.Id;
        }

        private static readonly string[] Il2Files = { "global.actions", "devices.txt", "current.responses", "current.devices" };

        // One spelling per file (discovery may mix "/" and "\"), so the export's record and the watcher's status match.
        private static string FullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || GameConfigLocation.IsRegistryKey(path))
                return (path ?? string.Empty).Trim();
            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch (Exception)
            {
                return path.Trim();
            }
        }

        // Games that keep their controls in a folder: the files JoyPro reads there.
        private static string[] DirectoryPatterns(string gameId)
        {
            switch (gameId)
            {
                case "FalconBMS": return new[] { "Setup.v100.*.xml", "Falcon BMS User.cfg", "Feedback.ini", "*.pop" };
                case "VTOLVR": return new[] { "BYOJ_*.xml" };
                default: return new[] { "*" };
            }
        }

        // Registry games: the values JoyPro owns (the Rewired controller maps of Nuclear Option's Player0 and Tiny Combat
        // Arena's Player).
        private static string OwnedRegistryPrefix(string gameId)
        {
            if (gameId == "NuclearOption")
                return Games.RewiredMapIntegration.OwnedPrefix("Player0");
            return gameId == "TinyCombatArena" ? Games.RewiredMapIntegration.OwnedPrefix("Player") : string.Empty;
        }

        private static RegistryKey OpenRegistryKey(string path)
        {
            string sub = path.Substring(path.IndexOf('\\') + 1);
            return Registry.CurrentUser.OpenSubKey(sub, false);
        }

        private static string DcsInputRoot(string savedGames)
        {
            foreach (string candidate in new[] { Path.Combine(savedGames, "Config", "Input"), Path.Combine(savedGames, "Input") })
            {
                if (Directory.Exists(candidate))
                    return candidate;
            }
            return Path.Combine(savedGames, "Config", "Input");
        }

        // The aircraft folders (Config\Input\<plane>\...) of changed DCS files.
        private static List<string> DcsPlanesOf(SyncLocation location, IEnumerable<string> files)
        {
            string root = DcsInputRoot(location.ConfigPath).TrimEnd('\\') + "\\";
            List<string> planes = new List<string>();
            foreach (string file in files ?? Enumerable.Empty<string>())
            {
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    continue;
                string rest = file.Substring(root.Length);
                int slash = rest.IndexOf('\\');
                if (slash > 0)
                {
                    string plane = rest.Substring(0, slash);
                    if (!planes.Contains(plane, StringComparer.OrdinalIgnoreCase))
                        planes.Add(plane);
                }
            }
            return planes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ---- Fingerprints -------------------------------------------------------------------------------------------

        private static List<string> ManagedFiles(SyncGame game, SyncLocation location)
        {
            List<string> files = new List<string>();
            if (!location.Exists || location.RegistryKey.Length > 0)
                return files;
            foreach (SyncWatch watch in location.Watch)
            {
                if (string.IsNullOrWhiteSpace(watch.Directory) || !Directory.Exists(watch.Directory))
                    continue;
                SearchOption option = watch.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (string filter in watch.Filters)
                {
                    foreach (string file in Directory.EnumerateFiles(watch.Directory, filter, option))
                    {
                        if (!IsJoyProFile(file) && !files.Contains(file, StringComparer.OrdinalIgnoreCase))
                            files.Add(file);
                    }
                }
            }
            return files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // JoyPro's own files next to the game's: never part of the game's state.
        private static bool IsJoyProFile(string file)
        {
            string name = Path.GetFileName(file) ?? string.Empty;
            return name.EndsWith(".jpbak", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(GameMetadataStore.Suffix, StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".joypro.json", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".jp", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
        }

        // Stamps of the current state; a file whose size and time match the recorded stamp keeps its recorded hash.
        private static List<FileStamp> CurrentStamps(SyncGame game, SyncLocation location, SyncRecord record)
        {
            List<FileStamp> stamps = new List<FileStamp>();
            if (location.RegistryKey.Length > 0)
            {
                if (location.Exists)
                    stamps.Add(RegistryStamp(location));
                return stamps;
            }
            Dictionary<string, FileStamp> known = record == null || record.Items == null
                ? new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase)
                : record.Items.Where(i => i != null && !string.IsNullOrEmpty(i.Path)).GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (string file in ManagedFiles(game, location))
            {
                FileInfo info = new FileInfo(file);
                if (!info.Exists)
                    continue;
                FileStamp stamp = new FileStamp { Path = info.FullName, Size = info.Length, MtimeTicks = info.LastWriteTimeUtc.Ticks };
                FileStamp old;
                stamp.Sha256 = known.TryGetValue(stamp.Path, out old) && old.Size == stamp.Size && old.MtimeTicks == stamp.MtimeTicks && !string.IsNullOrEmpty(old.Sha256)
                    ? old.Sha256
                    : HashFile(info.FullName);
                stamps.Add(stamp);
            }
            return stamps;
        }

        private static FileStamp RegistryStamp(SyncLocation location)
        {
            using (SHA256 sha = SHA256.Create())
            using (RegistryKey key = OpenRegistryKey(location.RegistryKey))
            {
                int count = 0;
                if (key != null)
                {
                    foreach (string name in key.GetValueNames().OrderBy(n => n, StringComparer.Ordinal))
                    {
                        if (location.OwnedPrefix.Length > 0 && !name.StartsWith(location.OwnedPrefix, StringComparison.Ordinal))
                            continue;
                        object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        byte[] nameBytes = Encoding.UTF8.GetBytes(name + "\0");
                        sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
                        byte[] data = value as byte[] ?? Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                        sha.TransformBlock(data, 0, data.Length, null, 0);
                        count++;
                    }
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return new FileStamp { Path = location.RegistryKey, Size = count, MtimeTicks = 0, Sha256 = Hex(sha.Hash) };
            }
        }

        // A file Windows does not let JoyPro read (a Microsoft Store install keeps its data encrypted with EFS and refuses a
        // file that lost the mark: "The APPX file can not be accessed because it is not encrypted as expected.", 2.2.1
        // report) gets a stamp from its size and write time instead of its hash. It used to throw, so the export's record
        // failed and MSFS 2024 stayed "untracked": the watcher never watched it and changes made in the sim were not
        // noticed.
        private static string HashFile(string path)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                    return Hex(sha.ComputeHash(SafeFileIo.ReadAllBytes(path)));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                FileInfo info = new FileInfo(path);
                if (JoyPro.Logging.JoyProLog.ShouldLog("sync-unreadable-" + path, TimeSpan.FromHours(1)))
                    JoyPro.Logging.JoyProLog.Warn("Sync", "Cannot read " + path + " for its fingerprint (size and write time used instead): " + ex.Message);
                return "unreadable:" + (info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "missing");
            }
        }

        private static string Hex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static string ShortHash(string text)
        {
            using (SHA256 sha = SHA256.Create())
                return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty))).Substring(0, 16);
        }

        private static string CombinedFingerprint(List<FileStamp> stamps)
        {
            return "sha256:" + Hex(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n",
                stamps.OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase).Select(s => s.Path.ToLowerInvariant() + "|" + s.Sha256)))));
        }

        private static StatusResult Status(SyncGame game, string explicitPath, bool explicitGame)
        {
            StatusResult result = new StatusResult { Game = game };
            GameInstall install = GameInstalls.Find(game.Id, result.Warnings);
            result.Installed = install.Installed;
            result.Disabled = install.Disabled;
            // The user does not have the game (engine games install disable): never watched, never checked.
            if (install.Disabled)
            {
                result.Location = new SyncLocation();
                result.State = "disabled";
                return result;
            }
            if (!result.Installed && string.IsNullOrWhiteSpace(explicitPath) && !explicitGame)
            {
                result.Location = new SyncLocation();
                result.State = "not-installed";
                return result;
            }
            result.Location = ResolveLocation(game, explicitPath, result.Warnings);
            result.Record = FindRecord(game.Id, result.Location.ConfigPath);
            if (!result.Location.Exists)
            {
                result.State = "missing";
                return result;
            }
            if (result.Record == null)
            {
                result.State = "untracked";
                return result;
            }
            try
            {
                result.Current = CurrentStamps(game, result.Location, result.Record);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Could not read " + result.Location.ConfigPath + ": " + ex.Message);
                result.State = "missing";
                return result;
            }
            result.Fingerprint = CombinedFingerprint(result.Current);
            result.ChangedFiles = ChangedItems(result.Record, result.Current);
            result.State = result.ChangedFiles.Count == 0 ? "unchanged" : "changed";
            return result;
        }

        // What a status compared (docs/logging.md "Game runs"): a changed config gets the environment and, per changed file,
        // the recorded and the current stamp; an unchanged one the files compared (at most every 10 minutes per game, the
        // GUI asks often).
        private static void LogComparison(StatusResult r)
        {
            if (r.Record == null || r.Current == null)
                return;
            Dictionary<string, FileStamp> recorded = (r.Record.Items ?? new List<FileStamp>()).Where(i => i != null && !string.IsNullOrEmpty(i.Path))
                .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            Func<FileStamp, string> describe = s => s == null ? "none" : s.Size.ToString(CultureInfo.InvariantCulture) + " bytes, " +
                new DateTime(Math.Max(0, s.MtimeTicks), DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z, " + ((s.Sha256 ?? string.Empty).Length > 12 ? s.Sha256.Substring(0, 12) : s.Sha256);
            if (r.State == "changed")
            {
                EngineDiagnostics.LogEnvironment(r.Game.Id, new[] { r.Location.ConfigPath }.Concat(r.Location.Watch.Select(w => w.Directory)));
                foreach (string path in r.ChangedFiles.Take(40))
                {
                    FileStamp before;
                    recorded.TryGetValue(path, out before);
                    FileStamp now = r.Current.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
                    JoyPro.Logging.JoyProLog.Debug("Sync", r.Game.Id + " changed file " + path + " | recorded (" + (r.Record.Reason ?? "-") + " " + (r.Record.RecordedUtc ?? "-") + "): " + describe(before) + " | now: " + describe(now));
                }
                return;
            }
            if (r.State == "unchanged" && JoyPro.Logging.JoyProLog.ShouldLog("sync-compared-" + r.Game.Id, TimeSpan.FromMinutes(10)))
                JoyPro.Logging.JoyProLog.Debug("Sync", r.Game.Id + " unchanged: " + r.Current.Count.ToString(CultureInfo.InvariantCulture) + " file(s) compared with the record (" +
                                                      (r.Record.Reason ?? "-") + " " + (r.Record.RecordedUtc ?? "-") + "): " +
                                                      string.Join("; ", r.Current.Take(8).Select(c => c.Path + " " + describe(c))) + (r.Current.Count > 8 ? "; ..." : string.Empty));
        }

        private static List<string> ChangedItems(SyncRecord record, List<FileStamp> current)
        {
            List<string> changed = new List<string>();
            Dictionary<string, string> recorded = (record.Items ?? new List<FileStamp>()).Where(i => i != null && !string.IsNullOrEmpty(i.Path))
                .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Sha256 ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            foreach (FileStamp stamp in current)
            {
                string sha;
                if (!recorded.TryGetValue(stamp.Path, out sha) || !string.Equals(sha, stamp.Sha256, StringComparison.OrdinalIgnoreCase))
                    changed.Add(stamp.Path);
            }
            HashSet<string> now = new HashSet<string>(current.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
            changed.AddRange(recorded.Keys.Where(p => !now.Contains(p)));
            return changed.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Records the current state of the config (fingerprint and baseline).
        private static SyncRecord Record(SyncGame game, SyncLocation location, string reason, List<string> warnings)
        {
            SyncRecord previous = FindRecord(game.Id, location.ConfigPath);
            List<FileStamp> stamps = CurrentStamps(game, location, previous);
            SyncRecord record = new SyncRecord
            {
                ConfigPath = location.ConfigPath,
                RecordedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Reason = reason,
                Fingerprint = CombinedFingerprint(stamps),
                Items = stamps
            };
            List<string> readWarnings = new List<string>();
            List<string> readErrors = new List<string>();
            Dictionary<string, Relation> scratch;
            List<SyncEntry> entries = ReadCurrent(game, location, null, readWarnings, readErrors, out scratch);
            if (readErrors.Count == 0)
            {
                record.BaselineKeys = entries.Select(e => ShortHash(e.Key)).Distinct().OrderBy(h => h, StringComparer.Ordinal).ToList();
                record.BaselineStates = entries.Where(e => e.IsAxis).Select(e => ShortHash(e.Key + Sep + e.TuningText)).Distinct().OrderBy(h => h, StringComparer.Ordinal).ToList();
            }
            else
                warnings.Add("The config could not be read for the baseline (" + string.Join(" | ", readErrors) + "); a diff compares with the profile only.");
            Dictionary<string, double> settings = ReadSettings(game, location, readWarnings);
            if (settings != null)
                record.BaselineSettings = settings.Select(s => SettingHash(s.Key, s.Value)).Distinct().OrderBy(h => h, StringComparer.Ordinal).ToList();
            SaveRecord(game.Id, record);
            return record;
        }

        private static string SyncDirectory()
        {
            return Path.Combine(GameInstalls.DataRoot(), SyncFolder);
        }

        private static string SyncFile(string gameId)
        {
            return Path.Combine(SyncDirectory(), gameId + ".json");
        }

        private static SyncDocument LoadDocument(string gameId)
        {
            string path = SyncFile(gameId);
            try
            {
                if (File.Exists(path))
                {
                    SyncDocument document = EngineSerialization.FromJson<SyncDocument>(File.ReadAllText(path, Encoding.UTF8));
                    if (document != null)
                    {
                        if (document.Configs == null)
                            document.Configs = new List<SyncRecord>();
                        return document;
                    }
                }
            }
            catch (Exception)
            {
                // An unreadable file counts as no record (the next export writes it again).
            }
            return new SyncDocument { Game = gameId, Configs = new List<SyncRecord>() };
        }

        private static SyncRecord FindRecord(string gameId, string configPath)
        {
            if (string.IsNullOrWhiteSpace(configPath))
                return null;
            return LoadDocument(gameId).Configs.FirstOrDefault(c => c != null && SamePath(c.ConfigPath, configPath));
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals((a ?? string.Empty).TrimEnd('\\', '/'), (b ?? string.Empty).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private static void SaveRecord(string gameId, SyncRecord record)
        {
            SyncDocument document = LoadDocument(gameId);
            document.Game = gameId;
            document.Configs.RemoveAll(c => c == null || SamePath(c.ConfigPath, record.ConfigPath));
            // Configs that are gone (temporary copies) are dropped; the newest few are kept.
            document.Configs.RemoveAll(c => !GameConfigLocation.IsRegistryKey(c.ConfigPath) && !File.Exists(c.ConfigPath) && !Directory.Exists(c.ConfigPath));
            document.Configs.Insert(0, record);
            if (document.Configs.Count > MaxConfigsPerGame)
                document.Configs.RemoveRange(MaxConfigsPerGame, document.Configs.Count - MaxConfigsPerGame);
            string path = SyncFile(gameId);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
            string temp = path + ".tmp";
            File.WriteAllText(temp, EngineSerialization.ToJson(document), new UTF8Encoding(false));
            SafeFileIo.ReplaceFile(temp, path);
        }

        // ---- Reading binds: the profile and the game's file ---------------------------------------------------------

        // The game's config read by the game's own import into a scratch profile; the loaded profile is restored after.
        private static List<SyncEntry> ReadCurrent(SyncGame game, SyncLocation location, HashSet<string> planes, List<string> warnings, List<string> errors, out Dictionary<string, Relation> scratchRelations)
        {
            scratchRelations = new Dictionary<string, Relation>(StringComparer.OrdinalIgnoreCase);
            ProfileSnapshot snapshot = ProfileSnapshot.Capture();
            try
            {
                switch (game.Kind)
                {
                    case "dcs":
                        {
                            EngineDcsImportOptions options = new EngineDcsImportOptions
                            {
                                PlaneFilter = planes == null ? new List<string>() : planes.ToList(),
                                IncludeKeyboard = true,
                                ImportInverted = true,
                                ImportSlider = true,
                                ImportCurvature = true,
                                ImportDeadzone = true,
                                ImportSaturationX = true,
                                ImportSaturationY = true
                            };
                            EngineDcsImportResult result = EngineDcsImport.Import(location.ConfigPath, false, options);
                            errors.AddRange(result.Errors ?? new List<string>());
                            break;
                        }
                    case "il2":
                        {
                            EngineIl2ImportOptions options = new EngineIl2ImportOptions { ImportInverted = true, ImportCurvature = true, ImportDeadzone = true };
                            EngineIl2ImportResult result = EngineIl2.Import(location.ConfigPath, false, options);
                            errors.AddRange(result.Errors ?? new List<string>());
                            break;
                        }
                    case "starcitizen":
                        {
                            EngineStarCitizenImportOptions options = new EngineStarCitizenImportOptions { IncludeKeyboard = true };
                            EngineStarCitizenImportResult result = EngineStarCitizen.Import(location.ConfigPath, false, options);
                            errors.AddRange(result.Errors ?? new List<string>());
                            break;
                        }
                    default:
                        {
                            List<string> readWarnings = new List<string>();
                            GameCatalog catalog = GameCatalogStore.Load(game.Registry, readWarnings);
                            GameDeviceDirectory devices = GameDeviceDirectory.Discover(readWarnings);
                            // What the game has, as an import reads it (MSFS: the user's own profiles beside JoyPro's package).
                            IGameImportReading importReading = game.Registry as IGameImportReading;
                            if (importReading != null)
                                importReading.ImportReading = true;
                            GameScanResult result;
                            try
                            {
                                result = game.Registry.Read(location.ConfigPath, catalog, devices) ?? new GameScanResult();
                            }
                            finally
                            {
                                if (importReading != null)
                                    importReading.ImportReading = false;
                            }
                            result.ConfigPath = location.ConfigPath;
                            errors.AddRange(result.Errors);
                            if (result.Errors.Count == 0)
                                GameProfileBridge.ApplyImport(game.Registry, catalog, result.Bindings, false, result);
                            break;
                        }
                }
                if (errors.Count > 0)
                    return new List<SyncEntry>();
                scratchRelations = InternalDataManagement.AllRelations;
                return Collect(game, InternalDataManagement.AllRelations, InternalDataManagement.AllBinds, planes);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                return new List<SyncEntry>();
            }
            finally
            {
                snapshot.Restore();
            }
        }

        // The game-wide settings (device *) the config holds now, in the FfbModel's units; null for games whose game-wide
        // settings JoyPro does not read back (DCS keeps its force feedback per device and aircraft).
        private static Dictionary<string, double> ReadSettings(SyncGame game, SyncLocation location, List<string> warnings)
        {
            try
            {
                switch (game.Kind)
                {
                    case "il2":
                        {
                            string input = EngineIl2.ResolveInputPath(location.ConfigPath) ?? string.Empty;
                            if (input.Length == 0)
                                return null;
                            string devices = Path.Combine(input, "current.devices");
                            Dictionary<string, double> values = File.Exists(devices) ? Il2CurrentDevices.Read(File.ReadAllText(devices)) : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                            // startup.cfg for what current.devices does not have (the game's menu writes current.devices).
                            string startup = Path.Combine(Path.GetDirectoryName(input.TrimEnd('\\', '/')) ?? string.Empty, "startup.cfg");
                            if (File.Exists(startup))
                            {
                                foreach (KeyValuePair<string, double> value in Il2StartupForceFeedback.Read(File.ReadAllText(startup)))
                                {
                                    if (!values.ContainsKey(value.Key))
                                        values[value.Key] = value.Value;
                                }
                            }
                            return values;
                        }
                    case "starcitizen":
                        {
                            string attributes = Path.Combine(Path.GetDirectoryName(location.ConfigPath) ?? string.Empty, "attributes.xml");
                            return File.Exists(attributes) ? StarCitizenGameSettings.Read(File.ReadAllText(attributes)) : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        }
                    case "registry":
                        {
                            IGameSettingsFile settingsFile = game.Registry as IGameSettingsFile;
                            if (settingsFile != null)
                                return settingsFile.ReadSettings(location.ConfigPath, warnings);
                            IGameSettingsReader reader = game.Registry as IGameSettingsReader;
                            return reader == null ? null : reader.ReadGameSettings(location.ConfigPath, warnings);
                        }
                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read the game settings: " + ex.Message);
                return null;
            }
        }

        private static string SettingHash(string key, double value)
        {
            return ShortHash(key.ToLowerInvariant() + "=" + AxisTuning.Num(Round(value)));
        }

        // Every bind of the relations with a node of the game, per plane (games without planes: the game id).
        private static List<SyncEntry> Collect(SyncGame game, Dictionary<string, Relation> relations, Dictionary<string, Bind> binds, HashSet<string> planes)
        {
            List<SyncEntry> entries = new List<SyncEntry>();
            foreach (KeyValuePair<string, Relation> pair in relations)
            {
                Relation relation = pair.Value;
                if (relation == null || relation.NODES == null)
                    continue;
                Bind bind = relation.bind;
                if (bind == null && binds != null)
                    binds.TryGetValue(pair.Key, out bind);
                string input = bind == null ? string.Empty : (relation.ISAXIS ? bind.JAxis : bind.JButton);
                if (bind == null || string.IsNullOrWhiteSpace(bind.Joystick) || string.IsNullOrWhiteSpace(input))
                    continue;
                foreach (RelationItem node in relation.NODES)
                {
                    if (node == null || string.IsNullOrWhiteSpace(node.ID) || !string.Equals(NodeGameId(node.Game), game.Id, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (string plane in ActivePlanes(game, node))
                    {
                        if (planes != null && !planes.Contains(plane))
                            continue;
                        SyncEntry entry = new SyncEntry
                        {
                            Plane = plane,
                            ActionId = node.ID.Trim(),
                            ActionKey = ActionKey(game, node.ID),
                            Device = bind.Joystick.Trim(),
                            Input = input.Trim(),
                            IsAxis = relation.ISAXIS,
                            Reformers = bind.AllReformers == null ? new List<string>() : bind.AllReformers.Where(r => !string.IsNullOrWhiteSpace(r)).ToList(),
                            Tuning = relation.ISAXIS ? BindTuning.Resolve(bind, game.Id, game.Kind == "dcs" ? plane : null).Tuning : null,
                            Relation = pair.Key,
                            Title = bind.AdditionalImportInfo ?? string.Empty,
                            Groups = relation.Groups == null ? new List<string>() : new List<string>(relation.Groups)
                        };
                        Finish(entry);
                        entries.Add(entry);
                    }
                }
            }
            return entries;
        }

        private static void Finish(SyncEntry entry)
        {
            string device = entry.Device.Trim().ToLowerInvariant();
            string input = entry.Input.Trim().ToLowerInvariant();
            entry.Key = entry.Plane.ToLowerInvariant() + Sep + entry.ActionKey + Sep + entry.Signature;
            entry.LooseKey = entry.Plane.ToLowerInvariant() + Sep + entry.ActionKey + Sep + device + Sep + (entry.IsAxis ? "1" : "0") + Sep + input;
            entry.TuningText = entry.Tuning == null ? string.Empty : Semantic(entry.Tuning);
        }

        // The tuning as the game uses it, numbers rounded to 4 decimals: a game that writes 0.21 back as 0.20999999 (or
        // drops values equal to its default and reads them back the same) changes nothing.
        private static string Semantic(AxisTuning tuning)
        {
            AxisTuning rounded = tuning.Clone();
            rounded.Deadzone = Round(rounded.Deadzone);
            rounded.SaturationX = Round(rounded.SaturationX);
            rounded.SaturationY = Round(rounded.SaturationY);
            rounded.Curvature = (rounded.Curvature ?? new List<double> { 0.0 }).Select(Round).ToList();
            if (rounded.Curvature.Count == 0)
                rounded.Curvature.Add(0.0);
            foreach (string key in rounded.Native.Keys.ToList())
                rounded.Native[key] = Round(rounded.Native[key]);
            return rounded.Format();
        }

        private static double Round(double value)
        {
            double rounded = Math.Round(value, 4, MidpointRounding.AwayFromZero);
            return rounded == 0 ? 0 : rounded;
        }

        private static string NodeGameId(string nodeGame)
        {
            if (string.IsNullOrWhiteSpace(nodeGame))
                return "DCS";
            string resolved = GameInstalls.Resolve(nodeGame);
            return resolved.Length > 0 ? resolved : nodeGame.Trim();
        }

        private static IEnumerable<string> ActivePlanes(SyncGame game, RelationItem node)
        {
            if (game.Kind == "dcs")
                return node.GetActiveAircraftList();
            // Games with an aircraft list (MSFS): as the export without --aircraft, only the nodes for all aircraft.
            Games.IGameIntegration registryGame;
            if (Games.GameRegistry.TryResolve(game.Id, out registryGame) && registryGame is Games.IGameAircraftScope)
                return Games.GameProfileBridge.LimitedAircraft(node, registryGame.GameId) == null ? new[] { game.Id } : new string[0];
            bool active;
            if (node.AIRCRAFT == null || node.AIRCRAFT.Count == 0)
                active = true;
            else if (!node.AIRCRAFT.TryGetValue(game.Id, out active))
                active = node.AIRCRAFT.Values.Any(v => v);
            return active ? new[] { game.Id } : new string[0];
        }

        // Star Citizen node ids come as "map|action", "map___action" or a bare action: the action name is the key.
        private static string ActionKey(SyncGame game, string id)
        {
            string key = (id ?? string.Empty).Trim().ToLowerInvariant();
            if (game.Kind == "starcitizen")
            {
                key = key.Replace("___", "|");
                int bar = key.LastIndexOf('|');
                if (bar >= 0)
                    key = key.Substring(bar + 1);
            }
            return key;
        }

        private static string ScMap(string id)
        {
            string key = (id ?? string.Empty).Trim().Replace("___", "|");
            int bar = key.IndexOf('|');
            return bar > 0 ? key.Substring(0, bar) : string.Empty;
        }

        // ---- Diff ---------------------------------------------------------------------------------------------------

        private static SyncDiff ComputeDiff(SyncGame game, string explicitPath)
        {
            SyncDiff diff = new SyncDiff { Game = game };
            diff.Location = ResolveLocation(game, explicitPath, diff.Warnings);
            EngineDiagnostics.LogEnvironment(game.Id, new[] { diff.Location.ConfigPath }.Concat(diff.Location.Watch.Select(w => w.Directory)));
            if (!diff.Location.Exists)
            {
                diff.Errors.Add("No " + game.DisplayName + " config found" + (diff.Location.ConfigPath.Length == 0 ? "." : ": " + diff.Location.ConfigPath));
                return diff;
            }

            SyncRecord record = FindRecord(game.Id, diff.Location.ConfigPath);
            diff.Tracked = record != null;
            diff.HasBaseline = record != null && record.BaselineKeys != null;
            HashSet<string> planes = null;
            if (game.Kind == "dcs" && record != null)
            {
                // Only the aircraft whose files changed since the record.
                List<FileStamp> current = CurrentStamps(game, diff.Location, record);
                diff.Planes = DcsPlanesOf(diff.Location, ChangedItems(record, current));
                planes = new HashSet<string>(diff.Planes, StringComparer.OrdinalIgnoreCase);
                if (planes.Count == 0)
                    return diff;
            }

            Dictionary<string, Relation> scratch;
            List<SyncEntry> file = ReadCurrent(game, diff.Location, planes, diff.Warnings, diff.Errors, out scratch);
            if (diff.Errors.Count > 0)
                return diff;
            List<SyncEntry> profile = Collect(game, InternalDataManagement.AllRelations, InternalDataManagement.AllBinds, planes);

            // What JoyPro manages: the devices the profile uses, the keyboard only when the profile has keyboard binds
            // for the game, and the game's actions JoyPro writes.
            HashSet<string> devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> pair in InternalDataManagement.AllRelations)
            {
                Bind bind = pair.Value == null ? null : pair.Value.bind;
                if (bind == null)
                    InternalDataManagement.AllBinds.TryGetValue(pair.Key, out bind);
                if (bind != null && !string.IsNullOrWhiteSpace(bind.Joystick))
                    devices.Add(bind.Joystick.Trim());
            }
            bool keyboard = profile.Any(e => e.IsKeyboard);
            GameCatalog catalog = game.Kind == "registry" ? GameCatalogStore.Load(game.Registry, new List<string>()) : null;
            Func<SyncEntry, bool> managed = e =>
                (e.IsKeyboard ? keyboard : devices.Contains(e.Device)) &&
                (catalog == null || catalog.Actions.Count == 0 || catalog.Contains(e.ActionId)) &&
                (game.Kind != "starcitizen" || ScMap(e.ActionId).Length == 0 || EngineStarCitizen.IsFlightMap(ScMap(e.ActionId)));
            file = file.Where(managed).ToList();
            profile = profile.Where(managed).ToList();
            diff.FileBinds = file.Count;
            diff.ProfileBinds = profile.Count;

            HashSet<string> baseKeys = diff.HasBaseline ? new HashSet<string>(record.BaselineKeys, StringComparer.Ordinal) : null;
            HashSet<string> baseStates = diff.HasBaseline ? new HashSet<string>(record.BaselineStates ?? new List<string>(), StringComparer.Ordinal) : null;
            Dictionary<string, List<SyncEntry>> profileByKey = profile.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.ToList());
            Dictionary<string, SyncEntry> fileByKey = file.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First());

            List<SyncChange> added = new List<SyncChange>();
            List<SyncChange> removed = new List<SyncChange>();
            foreach (SyncEntry entry in fileByKey.Values)
            {
                List<SyncEntry> held;
                if (!profileByKey.TryGetValue(entry.Key, out held))
                {
                    // In the file but not in the profile: new, unless the file had it already when JoyPro recorded it.
                    if (baseKeys == null || !baseKeys.Contains(ShortHash(entry.Key)))
                        added.Add(NewChange(game, "added", entry, null));
                    continue;
                }
                if (!entry.IsAxis || held.Any(h => h.TuningText == entry.TuningText))
                    continue;
                if (baseStates != null && baseStates.Contains(ShortHash(entry.Key + Sep + entry.TuningText)))
                    continue;
                SyncChange tuning = NewChange(game, "changed-tuning", entry, held);
                tuning.OldTuning = held[0].Tuning;
                tuning.NewTuning = entry.Tuning;
                diff.Changes.Add(tuning);
            }
            foreach (KeyValuePair<string, List<SyncEntry>> pair in profileByKey)
            {
                if (fileByKey.ContainsKey(pair.Key))
                    continue;
                // In the profile but no longer in the file: only when the file had it when JoyPro recorded it (the game
                // removed it), not when the export could not write it or the user added it in JoyPro since.
                if (baseKeys != null && !baseKeys.Contains(ShortHash(pair.Key)))
                    continue;
                removed.Add(NewChange(game, "removed", pair.Value[0], pair.Value));
            }

            // The same action on the same input with other modifiers: one modifier change instead of removed + added.
            foreach (SyncChange add in added.ToList())
            {
                SyncChange old = removed.FirstOrDefault(r => r.LooseKey == add.LooseKey);
                if (old == null)
                    continue;
                removed.Remove(old);
                added.Remove(add);
                SyncChange modifiers = add;
                modifiers.Kind = "changed-modifiers";
                modifiers.OldModifiers = old.Modifiers;
                modifiers.OldTuning = old.OldTuning;
                modifiers.Relations = old.Relations;
                diff.Changes.Add(modifiers);
            }
            diff.Changes.AddRange(added);
            diff.Changes.AddRange(removed);

            Dictionary<string, string> il2Titles = game.Kind == "il2" ? Il2Titles() : null;
            foreach (SyncChange change in diff.Changes)
            {
                GameAction action = catalog == null ? null : catalog.Find(change.ActionId);
                string il2Title;
                if (action != null && !string.IsNullOrWhiteSpace(action.Title))
                    change.ActionTitle = action.Title;
                else if (il2Titles != null && (change.ActionTitle.Length == 0 || change.ActionTitle == change.ActionId) && il2Titles.TryGetValue(change.ActionId, out il2Title))
                    change.ActionTitle = il2Title;
                Complete(game, change, profile);
            }
            diff.Changes = diff.Changes
                .OrderBy(c => c.Plane, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.ActionTitle.Length > 0 ? c.ActionTitle : c.ActionId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Kind, StringComparer.Ordinal)
                .ToList();
            diff.Changes.AddRange(SettingChanges(game, diff, record));
            return diff;
        }

        // Game-wide settings the profile holds for the game (what JoyPro writes) whose value the game changed.
        private static List<SyncChange> SettingChanges(SyncGame game, SyncDiff diff, SyncRecord record)
        {
            List<SyncChange> changes = new List<SyncChange>();
            string settingsGame = SettingsGameId(game);
            Dictionary<string, double> profile = FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, settingsGame, null);
            if (profile.Count == 0)
                return changes;
            Dictionary<string, double> file = ReadSettings(game, diff.Location, diff.Warnings);
            if (file == null)
                return changes;
            HashSet<string> baseline = record != null && record.BaselineSettings != null ? new HashSet<string>(record.BaselineSettings, StringComparer.Ordinal) : null;
            foreach (KeyValuePair<string, double> wanted in profile.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                double now;
                if (!file.TryGetValue(wanted.Key, out now))
                {
                    // Games that save only what differs from their defaults (War Thunder's controls params) drop the
                    // line when the user sets the default in the game: the file had JoyPro's value when it was
                    // recorded and now has none, so the value is the parameter's default.
                    FfbParameter dropped = FfbSettings.Parameter(settingsGame, wanted.Key);
                    if (dropped == null || baseline == null || !baseline.Contains(SettingHash(wanted.Key, wanted.Value)))
                        continue;
                    now = dropped.Default;
                }
                if (Round(now) == Round(wanted.Value))
                    continue;
                // The file held this value when JoyPro recorded it (the game rounds, or the user kept it): no game change.
                if (baseline != null && baseline.Contains(SettingHash(wanted.Key, now)))
                    continue;
                FfbParameter parameter = FfbSettings.Parameter(settingsGame, wanted.Key);
                SyncChange change = new SyncChange
                {
                    Kind = "changed-setting",
                    GameId = game.Id,
                    Plane = game.Id,
                    SettingKey = parameter != null ? parameter.Key : wanted.Key,
                    SettingLabel = parameter != null ? parameter.Label ?? string.Empty : string.Empty,
                    OldValue = wanted.Value,
                    NewValue = now,
                    Suggested = "apply"
                };
                change.ActionTitle = change.SettingLabel;
                change.Id = "c" + ShortHash("changed-setting" + Sep + change.SettingKey.ToLowerInvariant() + Sep + AxisTuning.Num(Round(now))).Substring(0, 10);
                changes.Add(change);
            }
            return changes;
        }

        // IL-2 Great Battles action titles from JoyPro's command list (DB\IL2\IL2.actions: "id, keys, 0| //b Title"): the
        // file the game writes has no descriptions, so the dialog would show "gear_up" and suggest the import's generated
        // relation name.
        private static Dictionary<string, string> Il2Titles()
        {
            Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string file = GameDataPaths.DbFile("IL2", "IL2.actions");
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                    return titles;
                foreach (string raw in File.ReadAllLines(file))
                {
                    int comma = raw.IndexOf(',');
                    int comment = comma < 0 ? -1 : raw.IndexOf("//", comma, StringComparison.Ordinal);
                    if (comment < 0)
                        continue;
                    string marker = raw.Substring(comment).Trim();
                    string id = raw.Substring(0, comma).Trim();
                    if (id.Length == 0 || marker.Length <= 3 || !(marker.StartsWith("//a", StringComparison.OrdinalIgnoreCase) || marker.StartsWith("//b", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    titles[id] = marker.Substring(3).Trim();
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return titles;
        }

        private static SyncChange NewChange(SyncGame game, string kind, SyncEntry entry, List<SyncEntry> held)
        {
            SyncChange change = new SyncChange
            {
                Kind = kind,
                GameId = game.Id,
                Plane = entry.Plane,
                ActionId = entry.ActionId,
                ActionKey = entry.ActionKey,
                ActionTitle = entry.Title,
                IsAxis = entry.IsAxis,
                Device = entry.Device,
                Input = entry.Input,
                Modifiers = new List<string>(entry.Reformers),
                Relations = held == null ? new List<string>() : held.Select(h => h.Relation).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                NewRelationName = kind == "removed" ? string.Empty : entry.Relation,
                NewGroups = kind == "removed" ? new List<string>() : new List<string>(entry.Groups)
            };
            if (kind == "removed")
                change.OldTuning = entry.Tuning;
            else
                change.NewTuning = entry.Tuning;
            change.LooseKey = entry.LooseKey;
            return change;
        }

        // Candidates (relations that bind the same device/input/modifiers in this game, then the ones that hold the
        // action), the suggested target, a unique name for a new relation and a stable id.
        private static void Complete(SyncGame game, SyncChange change, List<SyncEntry> profile)
        {
            string signature = GameProfileBridge.BuildSignature(change.Device, change.IsAxis, change.Input, change.Modifiers);
            List<string> sameInput = profile.Where(e => e.Signature == signature && e.IsAxis == change.IsAxis).Select(e => e.Relation).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> holders = profile.Where(e => e.ActionKey == change.ActionKey && (game.Kind != "dcs" || e.Plane.Equals(change.Plane, StringComparison.OrdinalIgnoreCase)))
                .Select(e => e.Relation).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            change.Candidates = change.Relations.Concat(sameInput).Concat(holders).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (change.Kind == "added")
                change.Suggested = sameInput.Count > 0 ? sameInput[0] : "new";
            else if (change.Kind == "removed")
                change.Suggested = change.Relations.Count > 0 ? change.Relations[0] : "ignore";
            else
                change.Suggested = change.Relations.Count > 0 ? change.Relations[0] : "new";

            if (change.Kind != "removed")
            {
                // The name JoyPro's data next to the file has for the bind, else the action's title (the DCS import's
                // generated "<name>__<device>__<key>__<id>__button" names are not shown).
                string title = change.ActionTitle.Length > 0 ? change.ActionTitle : change.ActionId;
                bool generated = new[] { "__button", "__axis", "__single", "__pos", "__neg" }.Any(s => change.NewRelationName.EndsWith(s, StringComparison.Ordinal));
                // The action's readable title; the game in brackets only when another game's relation has that name.
                change.NewRelationName = change.NewRelationName.Length > 0 && !generated && !InternalDataManagement.AllRelations.ContainsKey(change.NewRelationName)
                    ? change.NewRelationName
                    : game.Kind == "dcs" ? UniqueName(title, InternalDataManagement.AllRelations)
                    : GameProfileBridge.NewRelationName(title, game.Id, game.DisplayName, InternalDataManagement.AllRelations);
            }
            if (change.NewGroups.Count == 0)
                change.NewGroups = new List<string> { "GENERATED", game.DisplayName };

            change.Id = "c" + ShortHash(change.Kind + Sep + change.Plane.ToLowerInvariant() + Sep + change.ActionKey + Sep + signature + Sep +
                                        GameProfileBridge.BuildSignature(change.Device, change.IsAxis, change.Input, change.OldModifiers)).Substring(0, 10);
        }

        private static string UniqueName(string baseName, Dictionary<string, Relation> existing)
        {
            string root = (baseName ?? string.Empty).Trim();
            if (root.Length == 0)
                root = "Relation";
            string candidate = root;
            int index = 2;
            while (existing.ContainsKey(candidate))
            {
                candidate = root + " (" + index.ToString(CultureInfo.InvariantCulture) + ")";
                index++;
            }
            return candidate;
        }

        // ---- Apply --------------------------------------------------------------------------------------------------

        private static bool ApplyChange(SyncGame game, SyncChange change, string target, string newName, List<string> steps, List<string> warnings, out string relationName)
        {
            relationName = string.Empty;
            List<string> modifiers = ProfileModifiers(change.Modifiers);
            if (target.Equals("new", StringComparison.OrdinalIgnoreCase))
            {
                if (change.Kind == "removed")
                {
                    warnings.Add(Describe(change) + ": a removed bind cannot go into a new relation; ignored.");
                    return false;
                }
                relationName = UniqueName(string.IsNullOrWhiteSpace(newName) ? change.NewRelationName : newName, InternalDataManagement.AllRelations);
                Relation relation = new Relation { NAME = relationName, ISAXIS = change.IsAxis, Groups = new List<string>(change.NewGroups), NODES = new List<RelationItem>() };
                Bind bind = new Bind(relation);
                SetInput(bind, change, modifiers);
                bind.AdditionalImportInfo = change.ActionTitle;
                AddNode(game, relation, change);
                if (change.IsAxis && change.NewTuning != null)
                    SetTuning(game, relation, bind, change.Plane, change.NewTuning, true);
                InternalDataManagement.AllRelations[relationName] = relation;
                InternalDataManagement.AllBinds[relationName] = bind;
                foreach (string old in change.Relations)
                {
                    RemoveNode(game, old, change);
                    RemoveIfEmpty(old, steps);
                }
                steps.Add("New relation '" + relationName + "': " + Describe(change));
                return true;
            }

            Relation existing;
            string name = InternalDataManagement.AllRelations.Keys.FirstOrDefault(k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));
            if (name == null || !InternalDataManagement.AllRelations.TryGetValue(name, out existing) || existing == null)
            {
                warnings.Add("Relation '" + target + "' does not exist; " + change.Id + " skipped.");
                return false;
            }
            relationName = name;
            if (change.Kind == "removed")
            {
                if (!RemoveNode(game, name, change))
                {
                    warnings.Add("Relation '" + name + "' does not hold " + (change.ActionTitle.Length > 0 ? change.ActionTitle : change.ActionId) + "; nothing removed.");
                    return false;
                }
                steps.Add("Relation '" + name + "': " + Describe(change));
                RemoveIfEmpty(name, steps);
                return true;
            }
            if (existing.ISAXIS != change.IsAxis && existing.NODES != null && existing.NODES.Count > 0)
            {
                warnings.Add("Relation '" + name + "' is " + (existing.ISAXIS ? "an axis" : "a button") + " relation; " + change.Id + " skipped.");
                return false;
            }
            Bind targetBind = existing.bind;
            if (targetBind == null)
                InternalDataManagement.AllBinds.TryGetValue(name, out targetBind);
            if (targetBind == null)
            {
                targetBind = new Bind(existing);
                InternalDataManagement.AllBinds[name] = targetBind;
            }
            string current = existing.ISAXIS ? targetBind.JAxis : targetBind.JButton;
            string signature = GameProfileBridge.BuildSignature(change.Device, change.IsAxis, change.Input, modifiers);
            if (string.IsNullOrWhiteSpace(targetBind.Joystick) || string.IsNullOrWhiteSpace(current))
                SetInput(targetBind, change, modifiers);
            else if (GameProfileBridge.BuildSignature(targetBind.Joystick, existing.ISAXIS, current, targetBind.AllReformers) != signature)
            {
                bool sameInput = string.Equals(targetBind.Joystick.Trim(), change.Device.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(current.Trim(), change.Input.Trim(), StringComparison.OrdinalIgnoreCase);
                SetInput(targetBind, change, modifiers);
                steps.Add(sameInput
                    ? "Relation '" + name + "' has the modifiers " + (modifiers.Count == 0 ? "none" : string.Join(" + ", modifiers.Select(ModifierLabel))) + " now (all its games)."
                    : "Relation '" + name + "' is bound to " + change.Device + " " + change.Input + " now (all its games).");
            }
            existing.ISAXIS = change.IsAxis;
            AddNode(game, existing, change);
            if (change.Kind != "added")
            {
                foreach (string old in change.Relations.Where(r => !string.Equals(r, name, StringComparison.OrdinalIgnoreCase)))
                {
                    RemoveNode(game, old, change);
                    RemoveIfEmpty(old, steps);
                }
            }
            if (change.IsAxis && change.NewTuning != null && change.Kind != "changed-modifiers")
                SetTuning(game, existing, targetBind, change.Plane, change.NewTuning, false);
            steps.Add("Relation '" + name + "': " + Describe(change));
            return true;
        }

        // The game's value becomes the profile's game-wide setting (device *, scope game:<settings game>), as engine ffb
        // set stores it.
        private static void ApplySetting(SyncGame game, SyncChange change)
        {
            if (InternalDataManagement.FfbOverrides == null)
                InternalDataManagement.FfbOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string key = FfbSettings.Key(FfbSettings.AnyDevice, BindTuning.GameScope(SettingsGameId(game)));
            string stored;
            Dictionary<string, double> values = InternalDataManagement.FfbOverrides.TryGetValue(key, out stored)
                ? FfbSettings.Parse(stored)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            values[change.SettingKey] = Math.Round(change.NewValue, 6);
            InternalDataManagement.FfbOverrides[key] = FfbSettings.Format(values);
        }

        // The file's modifiers as the profile names them (same device and key), else as the game's import named them.
        private static List<string> ProfileModifiers(List<string> reformers)
        {
            List<string> result = new List<string>();
            foreach (string reformer in reformers ?? new List<string>())
            {
                Modifier parsed = Modifier.ReformerToMod(reformer);
                if (parsed == null)
                    continue;
                Modifier known = InternalDataManagement.GetModifierWithKeyCombo(parsed.device, parsed.key);
                result.Add(known != null ? known.toReformerString() : reformer);
            }
            return result;
        }

        private static void SetInput(Bind bind, SyncChange change, List<string> modifiers)
        {
            bind.Joystick = change.Device;
            bind.JoyBackup = change.Device;
            if (change.IsAxis)
            {
                bind.JAxis = change.Input;
                bind.JButton = string.Empty;
            }
            else
            {
                bind.JButton = change.Input;
                bind.JAxis = string.Empty;
                bind.Inverted = null;
            }
            bind.AllReformers = new List<string>(modifiers);
            string alias;
            if (string.IsNullOrWhiteSpace(bind.AliasJoystick) && InternalDataManagement.JoystickAliases != null &&
                InternalDataManagement.JoystickAliases.TryGetValue(change.Device, out alias))
                bind.AliasJoystick = alias ?? string.Empty;
        }

        private static void AddNode(SyncGame game, Relation relation, SyncChange change)
        {
            relation.AddNode(change.ActionId, game.Id, change.IsAxis, change.Plane, false);
        }

        // A relation that holds no action any more after a take-over (its last action was removed or moved in the game)
        // goes away with its bind, as an import would leave it out (tester report on 2.2.9).
        private static void RemoveIfEmpty(string relationName, List<string> steps)
        {
            Relation relation;
            if (string.IsNullOrWhiteSpace(relationName) || !InternalDataManagement.AllRelations.TryGetValue(relationName, out relation) || relation == null)
                return;
            if (relation.NODES != null && relation.NODES.Count > 0)
                return;
            InternalDataManagement.AllRelations.Remove(relationName);
            InternalDataManagement.AllBinds.Remove(relationName);
            steps.Add("Relation '" + relationName + "' removed with its bind: it holds no action any more.");
        }

        // Takes the action (in this plane) out of a relation; returns whether it held it.
        private static bool RemoveNode(SyncGame game, string relationName, SyncChange change)
        {
            Relation relation;
            if (!InternalDataManagement.AllRelations.TryGetValue(relationName, out relation) || relation == null || relation.NODES == null)
                return false;
            bool removed = false;
            for (int i = relation.NODES.Count - 1; i >= 0; i--)
            {
                RelationItem node = relation.NODES[i];
                if (node == null || !string.Equals(NodeGameId(node.Game), game.Id, StringComparison.OrdinalIgnoreCase) || ActionKey(game, node.ID) != change.ActionKey)
                    continue;
                if (game.Kind == "dcs")
                {
                    if (node.AIRCRAFT == null || !node.AIRCRAFT.ContainsKey(change.Plane))
                        continue;
                    node.DeleteAircraftFromActivity(change.Plane);
                    removed = true;
                    if (!node.AIRCRAFT.Values.Any(v => v))
                        relation.NODES.RemoveAt(i);
                }
                else
                {
                    relation.NODES.RemoveAt(i);
                    removed = true;
                }
            }
            relation.RecalculateElementCount();
            return removed;
        }

        // DCS: the aircraft's tuning (the base when the relation only flies that aircraft); other games: the game's.
        private static void SetTuning(SyncGame game, Relation relation, Bind bind, string plane, AxisTuning value, bool newRelation)
        {
            // The game's float noise (0.20000000298023) is not kept.
            AxisTuning tuning = AxisTuning.Parse(value.Format());
            if (game.Kind == "dcs")
            {
                List<string> dcsPlanes = relation.NODES == null ? new List<string>() : relation.NODES
                    .Where(n => n != null && NodeGameId(n.Game) == "DCS")
                    .SelectMany(n => n.GetActiveAircraftList())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                bool onlyThisPlane = dcsPlanes.Count == 1 && dcsPlanes[0].Equals(plane, StringComparison.OrdinalIgnoreCase);
                bool hasDcsScopes = bind.TuningOverrides != null && bind.TuningOverrides.Keys.Any(k => k.StartsWith("plane:DCS:", StringComparison.OrdinalIgnoreCase) || k.Equals("game:DCS", StringComparison.OrdinalIgnoreCase));
                if (newRelation || (onlyThisPlane && !hasDcsScopes))
                    BindTuning.Set(bind, BindTuning.BaseScope, tuning);
                else
                    BindTuning.Set(bind, BindTuning.PlaneScope("DCS", plane), tuning);
                return;
            }
            BindTuning.Set(bind, BindTuning.GameScope(game.Id), tuning);
        }

        // ---- Scratch profile ----------------------------------------------------------------------------------------

        // The global profile state an import touches, copied so a scratch import leaves the loaded profile as it was.
        private sealed class ProfileSnapshot
        {
            private Dictionary<string, Relation> relations;
            private Dictionary<string, Bind> binds;
            private List<string> groups;
            private Dictionary<string, bool> groupActivity;
            private Dictionary<string, bool> joystickActivity;
            private Dictionary<string, string> aliases;
            private Dictionary<string, Modifier> modifiers;
            private Dictionary<string, ForceFeedbackS> ffb;
            private Dictionary<string, string> ffbOverrides;
            private Dictionary<string, Dictionary<string, string>> planeAliases;

            public static ProfileSnapshot Capture()
            {
                return new ProfileSnapshot
                {
                    relations = InternalDataManagement.AllRelations,
                    binds = InternalDataManagement.AllBinds,
                    groups = InternalDataManagement.AllGroups == null ? null : new List<string>(InternalDataManagement.AllGroups),
                    groupActivity = Copy(InternalDataManagement.GroupActivity),
                    joystickActivity = Copy(InternalDataManagement.JoystickActivity),
                    aliases = Copy(InternalDataManagement.JoystickAliases),
                    modifiers = Copy(InternalDataManagement.AllModifiers),
                    ffb = Copy(InternalDataManagement.JoystickFFB),
                    ffbOverrides = Copy(InternalDataManagement.FfbOverrides),
                    planeAliases = InternalDataManagement.PlaneAliases == null ? null : InternalDataManagement.PlaneAliases.ToDictionary(p => p.Key, p => p.Value == null ? null : Copy(p.Value), StringComparer.OrdinalIgnoreCase)
                };
            }

            public void Restore()
            {
                InternalDataManagement.AllRelations = relations;
                InternalDataManagement.AllBinds = binds;
                InternalDataManagement.AllGroups = groups;
                InternalDataManagement.GroupActivity = groupActivity;
                InternalDataManagement.JoystickActivity = joystickActivity;
                InternalDataManagement.JoystickAliases = aliases;
                InternalDataManagement.AllModifiers = modifiers;
                InternalDataManagement.JoystickFFB = ffb;
                InternalDataManagement.FfbOverrides = ffbOverrides;
                InternalDataManagement.PlaneAliases = planeAliases;
            }

            private static Dictionary<string, T> Copy<T>(Dictionary<string, T> source)
            {
                return source == null ? null : new Dictionary<string, T>(source, StringComparer.OrdinalIgnoreCase);
            }
        }

        // ---- Helpers ------------------------------------------------------------------------------------------------

        private static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            return 1;
        }

        private static string PopValue(List<string> args, string name)
        {
            int index = args.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= args.Count)
                return string.Empty;
            string value = args[index + 1];
            args.RemoveRange(index, 2);
            return value;
        }
    }
}
