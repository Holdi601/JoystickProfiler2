using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    // engine db refresh-il2 [<install, data\input folder or .actions file>] [--path <same>] [--db-path <folder>] [--dry-run]
    // (also engine il2 refresh-db): adds the actions of IL-2 Great Battles' key mapping files (data\input\*.actions,
    // which the game writes with every action and its English description) that DB\IL2\IL2.actions lacks. Known lines
    // stay byte for byte (relation names come from the titles), except a title the game left as its string-table key
    // ("inprb_pitch") or empty: that one gets JoyPro's own name (DB\Functions\ActionTexts\IL2Game.tsv), new actions too.
    // The file is written only when an action is new or such a title was renamed.
    internal static partial class Program
    {
        private const int Il2ActionsColumn = 50;

        private static int RunDbRefreshIl2(List<string> args, string outPath, bool textOutput)
        {
            List<string> options = args == null ? new List<string>() : new List<string>(args);
            string pathOption;
            string dbPathOption;
            try
            {
                pathOption = NormalizePath(PopOptionValue(options, "--path"));
                dbPathOption = NormalizePath(PopOptionValue(options, "--db-path"));
            }
            catch (ArgumentException ex)
            {
                return Fail(ex.Message);
            }
            // --dry-run alone or with true/false.
            bool dryRun = false;
            int dryAt = options.FindIndex(o => o.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
            if (dryAt >= 0)
            {
                dryRun = true;
                options.RemoveAt(dryAt);
                bool parsed;
                if (dryAt < options.Count && TryParseOptionBoolean(options[dryAt], out parsed))
                {
                    dryRun = parsed;
                    options.RemoveAt(dryAt);
                }
            }
            string positional = options.FirstOrDefault(o => !o.StartsWith("--", StringComparison.Ordinal));
            if (positional != null)
                options.Remove(positional);
            if (options.Count > 0)
                return Fail("Unknown db refresh-il2 argument(s): " + string.Join(" ", options));

            string source = string.IsNullOrWhiteSpace(pathOption) ? NormalizePath(positional) : pathOption;
            if (string.IsNullOrWhiteSpace(source))
            {
                // Without a path the installed game is the source.
                string refusal = RefuseGameAction("IL2Game", "refresh-db");
                if (refusal != null)
                    return Fail(refusal);
            }

            Il2DbRefreshResult result = RefreshIl2Db(source, dbPathOption, dryRun);
            EmitPayload(textOutput ? ToText(result) : EngineSerialization.ToJson(result), outPath);
            if (!result.Success)
                return Fail("db refresh-il2 failed: " + result.Error);
            // The build stamp belongs to JoyPro's own DB, not to a catalog refreshed elsewhere (--db-path).
            if (!dryRun && string.IsNullOrWhiteSpace(dbPathOption))
                DbUpdateCheck.RecordCurrent("IL2Game", result.Warnings);
            return 0;
        }

        private static Il2DbRefreshResult RefreshIl2Db(string source, string dbPath, bool dryRun)
        {
            Il2DbRefreshResult result = new Il2DbRefreshResult { DryRun = dryRun };
            // JoyPro's own list lives in the game-data folder (GameDataPaths); a dry run only reads.
            string dbFolder = !string.IsNullOrWhiteSpace(dbPath) ? dbPath
                : dryRun ? GameDataPaths.DbFolder("IL2")
                : GameDataPaths.WritableDbFolder("IL2");
            result.DbFile = Path.Combine(dbFolder, "IL2.actions");
            // A release package ships no IL-2 catalog: the first refresh builds it from the game's key mapping files.
            bool newCatalog = !File.Exists(result.DbFile);

            string inputFolder;
            result.SourceFiles = Il2ActionsSources(source, result.Warnings, out inputFolder);
            if (result.SourceFiles.Count == 0)
            {
                result.Error = string.IsNullOrWhiteSpace(source)
                    ? "No key mapping file (data\\input\\global.actions) was found in the IL-2 installation. Start the game once, or pass the file: engine db refresh-il2 <path>."
                    : "No IL-2 key mapping file (*.actions) was found at " + source + ".";
                return result;
            }

            // Ids of the catalog: the first column of every action line (lines without a description are extra
            // default inputs of the same action).
            string dbText = newCatalog
                ? "// JoyPro's IL-2 Great Battles command list, built on this PC from the game's key mapping files.\r\n&actions=action,command,invert|\r\n"
                : Encoding.UTF8.GetString(File.ReadAllBytes(result.DbFile));
            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in dbText.Split('\n'))
            {
                string id = Il2ActionId(line);
                if (id != null)
                    known.Add(id);
            }
            result.CatalogActions = known.Count;

            HashSet<string> axisHints = Il2AxisHints(inputFolder);
            Dictionary<string, Il2DbRefreshAction> found = new Dictionary<string, Il2DbRefreshAction>(StringComparer.OrdinalIgnoreCase);
            List<Il2DbRefreshAction> order = new List<Il2DbRefreshAction>();
            Regex comment = new Regex(@"//(?<kind>[ab](?=\s|$))?\s*(?<title>.*)$", RegexOptions.CultureInvariant);
            int fileIndex = 0;
            foreach (string file in result.SourceFiles)
            {
                EngineProgress.Report(++fileIndex, result.SourceFiles.Count, "IL-2 Great Battles, reading the key mapping files", Path.GetFileName(file));
                foreach (string raw in File.ReadAllLines(file))
                {
                    string id = Il2ActionId(raw);
                    if (id == null)
                        continue;
                    int comma = raw.IndexOf(',');
                    int bar = raw.IndexOf('|', comma);
                    string body = bar < 0 ? raw.Substring(comma + 1) : raw.Substring(comma + 1, bar - comma - 1);
                    string command = body.Split(',')[0].Trim();
                    Il2DbRefreshAction action;
                    if (!found.TryGetValue(id, out action))
                    {
                        action = new Il2DbRefreshAction { Id = id };
                        found[id] = action;
                        order.Add(action);
                    }
                    if (command.IndexOf('/') >= 0 || command.IndexOf("axis", StringComparison.OrdinalIgnoreCase) >= 0)
                        action.IsAxis = true;
                    if (bar < 0 || action.Title.Length > 0)
                        continue;
                    // The game writes "// <description>"; a file JoyPro exported has "//a <title>" or "//b <title>"
                    // and its metadata after "//JP:".
                    string tail = raw.Substring(bar + 1);
                    int jp = tail.IndexOf("//JP:", StringComparison.Ordinal);
                    if (jp >= 0)
                        tail = tail.Substring(0, jp);
                    Match match = comment.Match(tail);
                    if (!match.Success)
                        continue;
                    if (match.Groups["kind"].Value == "a")
                        action.IsAxis = true;
                    action.Title = match.Groups["title"].Value.Trim();
                }
            }
            result.GameActions = order.Count;

            // Titles that are no words (string-table keys, empty, the id): JoyPro's own name instead.
            string newline = dbText.Contains("\r\n") ? "\r\n" : "\n";
            string renamedText = RenameWeakIl2Titles(dbText, result.Renamed);

            foreach (Il2DbRefreshAction action in order)
            {
                if (known.Contains(action.Id))
                    continue;
                // Axes: a key pair or axis input in the file, the compiled halves (<id>_p/_n) or a response curve in
                // current.map, or a plane control (rpc_*: all of them are axes).
                if (axisHints.Contains(action.Id) || action.Id.StartsWith("rpc_", StringComparison.OrdinalIgnoreCase))
                    action.IsAxis = true;
                ActionTexts.Entry own = ActionTexts.IsWeakTitle(action.Id, action.Title) ? EngineActionTexts.Find("IL2Game", action.Id) : null;
                if (own != null && own.Name.Length > 0)
                    action.Title = own.Name;
                if (action.Title.Length == 0)
                    action.Title = action.Id;
                result.Added.Add(action);
            }

            if (result.Renamed.Count > 0 && !dryRun && !newCatalog)
            {
                // Only the renamed titles change; every other byte stays.
                File.WriteAllBytes(result.DbFile, new UTF8Encoding(false).GetBytes(renamedText));
                dbText = renamedText;
                result.Written = true;
            }
            if (result.Added.Count > 0 && !dryRun)
            {
                EngineProgress.Report(1, 1, "IL-2 Great Battles, writing the command list", result.Added.Count.ToString(CultureInfo.InvariantCulture) + " new actions");
                StringBuilder sb = new StringBuilder();
                if (dbText.Length > 0 && !dbText.EndsWith("\n", StringComparison.Ordinal))
                    sb.Append(newline);
                foreach (Il2DbRefreshAction action in result.Added)
                    sb.Append(Il2ActionsColumnText(action.Id + ",")).Append(Il2ActionsColumnText(",")).Append("0| //").Append(action.IsAxis ? "a" : "b")
                      .Append(' ').Append(action.Title.Replace("\r", " ").Replace("\n", " ")).Append(newline);
                if (newCatalog)
                {
                    Directory.CreateDirectory(dbFolder);
                    File.WriteAllText(result.DbFile, dbText, new UTF8Encoding(false));
                }
                byte[] tail = new UTF8Encoding(false).GetBytes(sb.ToString());
                using (FileStream stream = new FileStream(result.DbFile, FileMode.Append, FileAccess.Write))
                    stream.Write(tail, 0, tail.Length);
                result.Written = true;
            }
            result.Success = true;
            return result;
        }

        // Lines of the catalog whose title is no words ("//a inprb_pitch", "//b ", the id) and that JoyPro's table names: the
        // title replaced by that name, the rest of the line as it is. renamed gets "id: old -> new".
        private static string RenameWeakIl2Titles(string text, List<string> renamed)
        {
            Regex title = new Regex(@"^(?<head>\s*(?<id>[A-Za-z0-9_]+)\s*,[^|]*\|\s*//(?<kind>[ab]))(?<gap>[ \t]*)(?<title>[^\r]*?)(?<tail>\s*(?://JP:[^\r]*)?)(?<cr>\r?)$", RegexOptions.CultureInvariant);
            string[] lines = text.Split('\n');
            bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                Match m = title.Match(lines[i]);
                if (!m.Success)
                    continue;
                string id = m.Groups["id"].Value;
                string old = m.Groups["title"].Value.Trim();
                if (!ActionTexts.IsWeakTitle(id, old))
                    continue;
                ActionTexts.Entry own = EngineActionTexts.Find("IL2Game", id);
                if (own == null || own.Name.Length == 0 || string.Equals(own.Name, old, StringComparison.Ordinal))
                    continue;
                lines[i] = m.Groups["head"].Value + " " + own.Name + m.Groups["tail"].Value + m.Groups["cr"].Value;
                renamed.Add(id + ": " + (old.Length == 0 ? "(no title)" : old) + " -> " + own.Name);
                changed = true;
            }
            return changed ? string.Join("\n", lines) : text;
        }

        // "id," padded to the catalog's 50-character columns (at least one space).
        private static string Il2ActionsColumnText(string text)
        {
            return text.Length < Il2ActionsColumn ? text.PadRight(Il2ActionsColumn) : text + " ";
        }

        // The action id of a key mapping line ("rpc_pitch, joy0_axis_y, 0| ..."), null for comments and the header.
        private static string Il2ActionId(string line)
        {
            Match match = Regex.Match(line ?? string.Empty, @"^\s*([A-Za-z0-9_]+)\s*,");
            return match.Success ? match.Groups[1].Value : null;
        }

        // The *.actions files of the game's input folder (global.actions first; JoyPro's own jp.actions is skipped).
        private static List<string> Il2ActionsSources(string source, List<string> warnings, out string inputFolder)
        {
            inputFolder = null;
            List<string> folders = new List<string>();
            if (!string.IsNullOrWhiteSpace(source))
            {
                if (File.Exists(source))
                {
                    inputFolder = Path.GetDirectoryName(source);
                    return new List<string> { source };
                }
                if (Directory.Exists(source))
                    folders.AddRange(new[] { source, Path.Combine(source, "input"), Path.Combine(source, "data", "input") });
            }
            else
            {
                folders.AddRange(GamePathDiscovery.DiscoverIl2ConfigPaths(warnings));
            }

            foreach (string folder in folders.Where(Directory.Exists))
            {
                List<string> files = Directory.GetFiles(folder, "*.actions")
                    .Where(f => !string.Equals(Path.GetFileName(f), "jp.actions", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => string.Equals(Path.GetFileName(f), "global.actions", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (files.Count > 0)
                {
                    inputFolder = folder;
                    return files;
                }
            }
            return new List<string>();
        }

        // Axis actions named by the game's compiled map: action("<id>_p", ...) / ("<id>_n", ...) and [<id>] curves.
        private static HashSet<string> Il2AxisHints(string inputFolder)
        {
            HashSet<string> axes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string map = string.IsNullOrWhiteSpace(inputFolder) ? null : Path.Combine(inputFolder, "current.map");
            if (map == null || !File.Exists(map))
                return axes;
            bool curves = false;
            foreach (string raw in File.ReadAllLines(map))
            {
                string line = raw.Trim();
                Match action = Regex.Match(line, "^action\\(\"([A-Za-z0-9_]+)_[pn]\"");
                if (action.Success)
                    axes.Add(action.Groups[1].Value);
                if (line == "[axiscurves]")
                    curves = true;
                else if (curves)
                {
                    Match section = Regex.Match(line, @"^\[([A-Za-z0-9_]+)\]$");
                    if (section.Success && section.Groups[1].Value != "end")
                        axes.Add(section.Groups[1].Value);
                }
            }
            return axes;
        }

        private static string ToText(Il2DbRefreshResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("IL-2 DB Refresh");
            sb.AppendLine("Success: " + result.Success.ToString().ToLowerInvariant());
            sb.AppendLine("Dry Run: " + result.DryRun.ToString().ToLowerInvariant());
            sb.AppendLine("DB File: " + (string.IsNullOrWhiteSpace(result.DbFile) ? "<none>" : result.DbFile));
            sb.AppendLine("Source Files: " + result.SourceFiles.Count.ToString(CultureInfo.InvariantCulture));
            foreach (string file in result.SourceFiles)
                sb.AppendLine("  - " + file);
            sb.AppendLine("Game Actions: " + result.GameActions.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Catalog Actions: " + result.CatalogActions.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("New Actions: " + result.Added.Count.ToString(CultureInfo.InvariantCulture));
            foreach (Il2DbRefreshAction action in result.Added)
                sb.AppendLine("  + " + action.Id + " (" + (action.IsAxis ? "axis" : "button") + "): " + action.Title);
            sb.AppendLine("Renamed Titles: " + result.Renamed.Count.ToString(CultureInfo.InvariantCulture));
            foreach (string renamed in result.Renamed)
                sb.AppendLine("  ~ " + renamed);
            sb.AppendLine("Written: " + result.Written.ToString().ToLowerInvariant() +
                          (result.Written ? string.Empty : result.DryRun ? " (dry run)" : result.Success ? " (the catalog is up to date)" : string.Empty));
            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                foreach (string warning in result.Warnings)
                    sb.AppendLine("  - " + warning);
            }
            if (!string.IsNullOrWhiteSpace(result.Error))
                sb.AppendLine("Error: " + result.Error);
            return sb.ToString().TrimEnd();
        }

        private sealed class Il2DbRefreshAction
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public bool IsAxis { get; set; }

            public Il2DbRefreshAction()
            {
                Id = string.Empty;
                Title = string.Empty;
            }
        }

        private sealed class Il2DbRefreshResult
        {
            public bool Success { get; set; }
            public bool DryRun { get; set; }
            public bool Written { get; set; }
            public string DbFile { get; set; }
            public List<string> SourceFiles { get; set; }
            public int GameActions { get; set; }
            public int CatalogActions { get; set; }
            public List<Il2DbRefreshAction> Added { get; set; }
            // "id: old -> new" for titles that were no words and got JoyPro's own name.
            public List<string> Renamed { get; set; }
            public List<string> Warnings { get; set; }
            public string Error { get; set; }

            public Il2DbRefreshResult()
            {
                DbFile = string.Empty;
                SourceFiles = new List<string>();
                Added = new List<Il2DbRefreshAction>();
                Renamed = new List<string>();
                Warnings = new List<string>();
                Error = string.Empty;
            }
        }
    }
}
