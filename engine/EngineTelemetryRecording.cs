using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.TelemetrySessions;

namespace JoyPro.Engine
{
    // engine telemetry ...: telemetry recording of every game that has telemetry (docs/telemetry-recording.md).
    //   telemetry games                                              games, what JoyPro listens to, ACMI possible
    //   telemetry config show
    //   telemetry config set [--game G] --param key=value ...         enabled, folder, max-total-mb, keep-days,
    //                                                                 sample-hz, keep-raw, session-timeout; --game: enabled
    //   telemetry sessions [--game G] [--folder F]                    recorded sessions, newest first
    //   telemetry show <session> [--folder F]                         one session: facts and channels
    //   telemetry export <session> --format acmi|acmi-zip|csv [--out P] [--max-hz N] [--no-per-channel] [--folder F]
    //   telemetry preview <session> [--channel KEY]... [--points N]   a few channels reduced for a timeline
    //   telemetry delete <session>... [--folder F]
    //   telemetry recover [--folder F]                                finish sessions cut off by a crash
    //   telemetry prune [--folder F]                                  apply the retention settings now
    //   telemetry record --game G [--seconds N] [--folder F] [--sample-hz N] [--no-raw] [--no-game-check]
    //       records now, also when the settings say off (runs JoyPro.Ffb record; Ctrl+C ends it)
    // <session> = a path, a file name in the folder, "latest" or a number of the list (1 = newest).
    // Settings: <data>\Telemetry\telemetry-recording.json (data = JOYPRO_DATA_DIR or %LOCALAPPDATA%\JoyPro2), the format
    // gui/JoyPro.Ffb.Core/Telemetry/Recording/TelemetryRecordingConfig.cs reads. Everything stays on this PC.
    internal static partial class Program
    {
        internal const string TelemetryRecordingConfigFormat = "joypro-telemetry-recording-config";

        private static int RunTelemetry(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "sessions" : command[0].ToLowerInvariant();
            List<string> args = command.Skip(1).ToList();
            List<string> warnings = new List<string>();
            try
            {
                string folderOption = PopOptionValue(args, "--folder");
                Dictionary<string, object> config = LoadTelemetryRecordingConfig(warnings);
                string folder = !string.IsNullOrWhiteSpace(folderOption)
                    ? NormalizeTelemetryPath(folderOption)
                    : TelemetrySessionStore.ResolveFolder(TelemetryConfigString(config, "folder"));
                switch (action)
                {
                    case "games":
                        return EmitTelemetryGames(args, outPath, textOutput, config);
                    case "config":
                        return RunTelemetryConfig(args, outPath, textOutput, config, warnings);
                    case "sessions":
                    case "list":
                        return EmitTelemetrySessions(args, folder, outPath, textOutput, warnings);
                    case "show":
                        return EmitTelemetrySession(args, folder, outPath, textOutput);
                    case "export":
                        return ExportTelemetrySession(args, folder, outPath, textOutput);
                    case "preview":
                        return EmitTelemetryPreview(args, folder, outPath, textOutput);
                    case "delete":
                    case "remove":
                        return DeleteTelemetrySessions(args, folder, outPath, textOutput);
                    case "recover":
                    {
                        if (args.Count > 0)
                            return Fail("Unknown telemetry recover argument(s): " + string.Join(" ", args));
                        List<string> problems = new List<string>();
                        List<string> done = TelemetrySessionStore.Recover(folder, problems);
                        EmitPayload(textOutput
                            ? "Finished " + done.Count.ToString(CultureInfo.InvariantCulture) + " session(s) that were cut off." + string.Concat(done.Select(d => Environment.NewLine + "  " + d)) + string.Concat(problems.Select(p => Environment.NewLine + "  problem: " + p))
                            : EngineSerialization.ToJson(new { Folder = folder, Recovered = done, Problems = problems }), outPath);
                        return 0;
                    }
                    case "prune":
                    {
                        if (args.Count > 0)
                            return Fail("Unknown telemetry prune argument(s): " + string.Join(" ", args));
                        long maxBytes = (long)Math.Max(0, TelemetryConfigNumber(config, "maxTotalMb", 4096)) * 1024L * 1024L;
                        int keepDays = (int)Math.Max(0, TelemetryConfigNumber(config, "keepDays", 0));
                        List<string> problems = new List<string>();
                        List<string> deleted = TelemetrySessionStore.Prune(folder, maxBytes, keepDays, DateTime.UtcNow, problems);
                        EmitPayload(textOutput
                            ? "Deleted " + deleted.Count.ToString(CultureInfo.InvariantCulture) + " session(s) (size cap " + (maxBytes == 0 ? "none" : TelemetrySessionStore.FormatSize(maxBytes)) + ", keep days " + (keepDays == 0 ? "no limit" : keepDays.ToString(CultureInfo.InvariantCulture)) + ")." + string.Concat(deleted.Select(d => Environment.NewLine + "  " + d))
                            : EngineSerialization.ToJson(new { Folder = folder, Deleted = deleted, Problems = problems }), outPath);
                        return 0;
                    }
                    case "record":
                        return RecordTelemetry(args, folderOption);
                    default:
                        return Fail("engine telemetry games|config show|config set|sessions|show|export|preview|delete|recover|prune|record");
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is InvalidDataException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
            {
                return Fail(ex.Message);
            }
        }

        // ---------------------------------------------------------------- settings

        internal static string TelemetryRecordingConfigPath()
        {
            return Path.Combine(GameInstalls.DataRoot(), "Telemetry", "telemetry-recording.json");
        }

        internal static Dictionary<string, object> LoadTelemetryRecordingConfig(List<string> warnings)
        {
            Dictionary<string, object> raw = null;
            string path = TelemetryRecordingConfigPath();
            if (File.Exists(path))
            {
                try
                {
                    raw = EngineSerialization.FromJson<Dictionary<string, object>>(File.ReadAllText(path));
                }
                catch (Exception ex)
                {
                    warnings.Add("The telemetry recording settings could not be read (" + ex.Message + "); the defaults apply.");
                }
            }
            raw = raw ?? new Dictionary<string, object>();
            Dictionary<string, object> config = new Dictionary<string, object>(StringComparer.Ordinal);
            config["format"] = TelemetryRecordingConfigFormat;
            config["version"] = 1;
            config["enabled"] = TelemetryConfigBool(raw, "enabled", false);
            config["folder"] = TelemetryConfigString(raw, "folder");
            config["maxTotalMb"] = (int)Clamp(TelemetryConfigNumber(raw, "maxTotalMb", 4096), 0, 10000000);
            config["keepDays"] = (int)Clamp(TelemetryConfigNumber(raw, "keepDays", 0), 0, 100000);
            config["sampleHz"] = Clamp(TelemetryConfigNumber(raw, "sampleHz", 20), 0, 1000);
            config["keepRaw"] = TelemetryConfigBool(raw, "keepRaw", true);
            config["sessionTimeoutSeconds"] = Clamp(TelemetryConfigNumber(raw, "sessionTimeoutSeconds", 60), 5, 3600);
            Dictionary<string, object> games = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            object gamesRaw;
            if (raw.TryGetValue("games", out gamesRaw) && gamesRaw is Dictionary<string, object>)
            {
                foreach (KeyValuePair<string, object> pair in (Dictionary<string, object>)gamesRaw)
                {
                    Dictionary<string, object> game = pair.Value as Dictionary<string, object>;
                    bool enabled = game != null ? TelemetryConfigBool(game, "enabled", true) : !(pair.Value is bool) || (bool)pair.Value;
                    games[pair.Key] = new Dictionary<string, object> { { "enabled", enabled } };
                }
            }
            config["games"] = games;
            return config;
        }

        internal static void SaveTelemetryRecordingConfig(Dictionary<string, object> config)
        {
            string path = TelemetryRecordingConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, EngineFfbTelemetry.IndentJson(EngineSerialization.ToJson(config)) + Environment.NewLine, new UTF8Encoding(false));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
        }

        private static bool TelemetryGameEnabled(Dictionary<string, object> config, string gameId)
        {
            Dictionary<string, object> games = config["games"] as Dictionary<string, object>;
            object entry;
            if (games == null || !games.TryGetValue(gameId, out entry))
                return true;
            Dictionary<string, object> game = entry as Dictionary<string, object>;
            return game == null || TelemetryConfigBool(game, "enabled", true);
        }

        private static int RunTelemetryConfig(List<string> args, string outPath, bool textOutput, Dictionary<string, object> config, List<string> warnings)
        {
            string action = args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0].ToLowerInvariant() : "show";
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
                args.RemoveAt(0);
            string game = PopOptionValue(args, "--game");
            List<string> parameters = PopOptionValues(args, "--param");
            if (args.Count > 0)
                return Fail("Unknown telemetry config argument(s): " + string.Join(" ", args));
            if (action == "show")
            {
                EmitPayload(textOutput
                    ? EngineFfbTelemetry.IndentJson(EngineSerialization.ToJson(config))
                    : EngineSerialization.ToJson(new { ConfigPath = TelemetryRecordingConfigPath(), Folder = TelemetrySessionStore.ResolveFolder(TelemetryConfigString(config, "folder")), Config = config, Warnings = warnings }), outPath);
                return 0;
            }
            if (action != "set")
                return Fail("engine telemetry config show|set");
            if (parameters.Count == 0)
                return Fail("telemetry config set needs --param key=value (enabled, folder, max-total-mb, keep-days, sample-hz, keep-raw, session-timeout).");
            List<string> changes = new List<string>();
            string gameId = null;
            if (!string.IsNullOrWhiteSpace(game))
            {
                TelemetryRecordingGame spec = TelemetryRecordingGames.Find(game);
                if (spec == null)
                    return Fail("JoyPro records no telemetry of '" + game + "'. See: engine telemetry games.");
                gameId = spec.Id;
            }
            foreach (string parameter in parameters)
            {
                int eq = parameter.IndexOf('=');
                if (eq <= 0)
                    return Fail("--param needs key=value: " + parameter);
                string key = parameter.Substring(0, eq).Trim().ToLowerInvariant().Replace("_", "-");
                string value = parameter.Substring(eq + 1).Trim();
                bool flag;
                double number;
                if (gameId != null)
                {
                    if (key != "enabled" || !bool.TryParse(value, out flag))
                        return Fail("With --game only enabled=true|false can be set.");
                    ((Dictionary<string, object>)config["games"])[gameId] = new Dictionary<string, object> { { "enabled", flag } };
                    changes.Add(gameId + ": " + (flag ? "recorded" : "not recorded"));
                    continue;
                }
                switch (key)
                {
                    case "enabled":
                    case "keep-raw":
                        if (!bool.TryParse(value, out flag))
                            return Fail(key + " must be true or false.");
                        config[key == "enabled" ? "enabled" : "keepRaw"] = flag;
                        changes.Add(key + " = " + (flag ? "true" : "false"));
                        break;
                    case "folder":
                        config["folder"] = value.Length == 0 ? string.Empty : NormalizeTelemetryPath(value);
                        changes.Add("folder = " + TelemetrySessionStore.ResolveFolder((string)config["folder"]));
                        break;
                    case "max-total-mb":
                    case "keep-days":
                    case "sample-hz":
                    case "session-timeout":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || number < 0)
                            return Fail(key + " must be a number >= 0.");
                        switch (key)
                        {
                            case "max-total-mb": config["maxTotalMb"] = (int)Clamp(number, 0, 10000000); break;
                            case "keep-days": config["keepDays"] = (int)Clamp(number, 0, 100000); break;
                            case "sample-hz": config["sampleHz"] = Clamp(number, 0, 1000); break;
                            default: config["sessionTimeoutSeconds"] = Clamp(number, 5, 3600); break;
                        }
                        changes.Add(key + " = " + value);
                        break;
                    default:
                        return Fail("Unknown telemetry setting '" + key + "' (enabled, folder, max-total-mb, keep-days, sample-hz, keep-raw, session-timeout).");
                }
            }
            SaveTelemetryRecordingConfig(config);
            if (textOutput)
                EmitPayload("Telemetry recording settings: " + TelemetryRecordingConfigPath() + string.Concat(changes.Select(c => Environment.NewLine + "  * " + c)), outPath);
            else
                EmitPayload(EngineSerialization.ToJson(new { ConfigPath = TelemetryRecordingConfigPath(), Changes = changes, Warnings = warnings, Config = config }), outPath);
            return 0;
        }

        // ---------------------------------------------------------------- listing and exporting

        private static int EmitTelemetryGames(List<string> args, string outPath, bool textOutput, Dictionary<string, object> config)
        {
            if (args.Count > 0)
                return Fail("Unknown telemetry games argument(s): " + string.Join(" ", args));
            bool on = TelemetryConfigBool(config, "enabled", false);
            var games = TelemetryRecordingGames.All.Select(g => new
            {
                g.Id,
                g.Title,
                g.Transport,
                ProcessNames = g.ProcessNames.Select(p => p + ".exe").ToList(),
                Acmi = g.HasPosition,
                g.Setup,
                Recorded = on && TelemetryGameEnabled(config, g.Id)
            }).ToList();
            if (!textOutput)
            {
                EmitPayload(EngineSerialization.ToJson(new { Enabled = on, Games = games }), outPath);
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Telemetry recording is " + (on ? "on" : "off") + ". Games with telemetry: " + games.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var g in games)
            {
                sb.AppendLine("  " + g.Id + " - " + g.Title + (g.Recorded ? " [recorded]" : string.Empty));
                sb.AppendLine("      " + g.Transport + "; ACMI " + (g.Acmi ? "yes" : "no (no position)") + (g.Setup.Length > 0 ? "; needs " + g.Setup : string.Empty));
            }
            EmitPayload(sb.ToString().TrimEnd(), outPath);
            return 0;
        }

        private static object TelemetrySessionDto(TelemetrySessionInfo s)
        {
            return new
            {
                s.Path,
                s.FileName,
                s.Game,
                s.GameTitle,
                Created = s.CreatedUtc == DateTime.MinValue ? string.Empty : TelemetrySessionFormat.FormatTime(s.CreatedUtc),
                CreatedLocal = s.CreatedUtc == DateTime.MinValue ? string.Empty : s.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                DurationSeconds = Math.Round(s.Duration, 3),
                Duration = TelemetrySessionStore.FormatDuration(s.Duration),
                s.SizeBytes,
                Size = TelemetrySessionStore.FormatSize(s.SizeBytes),
                s.Aircraft,
                s.Maps,
                s.Frames,
                s.Events,
                s.RawPackets,
                s.Channels,
                s.Complete,
                Recording = s.InUse,
                s.EndReason,
                Acmi = s.HasPosition
            };
        }

        private static int EmitTelemetrySessions(List<string> args, string folder, string outPath, bool textOutput, List<string> warnings)
        {
            string game = PopOptionValue(args, "--game");
            if (args.Count > 0)
                return Fail("Unknown telemetry sessions argument(s): " + string.Join(" ", args));
            List<string> problems = new List<string>();
            List<TelemetrySessionInfo> sessions = TelemetrySessionStore.List(folder, problems);
            if (!string.IsNullOrWhiteSpace(game))
            {
                TelemetryRecordingGame spec = TelemetryRecordingGames.Find(game);
                string id = spec != null ? spec.Id : game.Trim();
                sessions = sessions.Where(s => string.Equals(s.Game, id, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (!textOutput)
            {
                EmitPayload(EngineSerialization.ToJson(new
                {
                    Folder = folder,
                    TotalBytes = sessions.Sum(s => s.SizeBytes),
                    Sessions = sessions.Select(TelemetrySessionDto).ToList(),
                    Problems = problems,
                    Warnings = warnings
                }), outPath);
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Recorded sessions in " + folder + ": " + sessions.Count.ToString(CultureInfo.InvariantCulture) + " (" + TelemetrySessionStore.FormatSize(sessions.Sum(s => s.SizeBytes)) + ")");
            int n = 1;
            foreach (TelemetrySessionInfo s in sessions)
            {
                sb.AppendLine("  " + (n++).ToString(CultureInfo.InvariantCulture) + ". " + (s.CreatedUtc == DateTime.MinValue ? "?" : s.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)) +
                              "  " + s.GameTitle + (s.Aircraft.Count > 0 ? " - " + string.Join(", ", s.Aircraft) : string.Empty) +
                              "  " + TelemetrySessionStore.FormatDuration(s.Duration) + "  " + TelemetrySessionStore.FormatSize(s.SizeBytes) +
                              (s.InUse ? "  [recording]" : !s.Complete ? "  [unfinished]" : string.Empty) + "  " + s.FileName);
            }
            foreach (string problem in problems)
                sb.AppendLine("  problem: " + problem);
            EmitPayload(sb.ToString().TrimEnd(), outPath);
            return 0;
        }

        private static string ResolveTelemetrySession(List<string> args, string folder)
        {
            string reference = args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : string.Empty;
            if (reference.Length > 0)
                args.RemoveAt(0);
            if (reference.Length == 0)
                throw new ArgumentException("Which session? A path, a file name, \"latest\" or a number from: engine telemetry sessions --text");
            string path = TelemetrySessionStore.Resolve(folder, reference);
            if (path == null)
                throw new ArgumentException("No recorded session '" + reference + "' in " + folder + ".");
            return path;
        }

        private static int EmitTelemetrySession(List<string> args, string folder, string outPath, bool textOutput)
        {
            string path = ResolveTelemetrySession(args, folder);
            if (args.Count > 0)
                return Fail("Unknown telemetry show argument(s): " + string.Join(" ", args));
            TelemetrySessionInfo info = TelemetrySessionStore.ReadInfo(path);
            List<TelemetrySessionChannel> channels;
            Dictionary<string, int> eventKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            using (TelemetrySessionReader reader = new TelemetrySessionReader(path))
            {
                while (reader.Next())
                {
                    if (reader.Kind == TelemetrySessionReader.RecordKind.Event)
                    {
                        int count;
                        eventKinds.TryGetValue(reader.EventKind, out count);
                        eventKinds[reader.EventKind] = count + 1;
                    }
                }
                channels = reader.Channels.Where(c => c != null).ToList();
            }
            if (!textOutput)
            {
                EmitPayload(EngineSerialization.ToJson(new
                {
                    Session = TelemetrySessionDto(info),
                    info.Header,
                    info.Summary,
                    Nav = info.Nav.Select(n => new { n.Target, n.Key, n.Scale, n.Offset }).ToList(),
                    Channels = channels.Select(c => new { c.Key, c.Title, c.Unit, c.Category, c.Min, c.Max }).ToList(),
                    Events = eventKinds
                }), outPath);
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(info.FileName + ": " + info.GameTitle + (info.Aircraft.Count > 0 ? " - " + string.Join(", ", info.Aircraft) : string.Empty));
            sb.AppendLine("  started " + (info.CreatedUtc == DateTime.MinValue ? "?" : info.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) + ", " + TelemetrySessionStore.FormatDuration(info.Duration) +
                          ", " + TelemetrySessionStore.FormatSize(info.SizeBytes) + ", " + info.Frames.ToString(CultureInfo.InvariantCulture) + " frames, " + info.Events.ToString(CultureInfo.InvariantCulture) + " events, " +
                          info.RawPackets.ToString(CultureInfo.InvariantCulture) + " raw packets; end: " + info.EndReason);
            if (info.Maps.Count > 0)
                sb.AppendLine("  map: " + string.Join(", ", info.Maps));
            sb.AppendLine("  ACMI export: " + (info.HasPosition ? "yes" : "no (no position in this game's telemetry)"));
            sb.AppendLine("  channels: " + channels.Count.ToString(CultureInfo.InvariantCulture));
            foreach (IGrouping<string, TelemetrySessionChannel> group in channels.GroupBy(c => c.Category.Length > 0 ? c.Category : "Other"))
                sb.AppendLine("    " + group.Key + ": " + string.Join(", ", group.Select(c => c.Key + (c.Unit.Length > 0 ? " [" + c.Unit + "]" : string.Empty))));
            if (eventKinds.Count > 0)
                sb.AppendLine("  events: " + string.Join(", ", eventKinds.OrderByDescending(e => e.Value).Select(e => e.Key + " " + e.Value.ToString(CultureInfo.InvariantCulture))));
            EmitPayload(sb.ToString().TrimEnd(), outPath);
            return 0;
        }

        private static int ExportTelemetrySession(List<string> args, string folder, string outPath, bool textOutput)
        {
            string path = ResolveTelemetrySession(args, folder);
            string format = (PopOptionValue(args, "--format") ?? "acmi-zip").Trim().ToLowerInvariant();
            if (format == "zip" || format == "zip.acmi")
                format = "acmi-zip";
            if (format == "txt.acmi" || format == "text")
                format = "acmi";
            string target = PopOptionValue(args, "--to");
            string maxHzText = PopOptionValue(args, "--max-hz");
            bool perChannel = !PopFlag(args, "--no-per-channel");
            if (args.Count > 0)
                return Fail("Unknown telemetry export argument(s): " + string.Join(" ", args));
            double maxHz;
            if (!double.TryParse(maxHzText ?? string.Empty, NumberStyles.Float, CultureInfo.InvariantCulture, out maxHz))
                maxHz = format == "csv" ? 0 : TelemetryAcmiExport.DefaultMaxHz;
            if (format != "acmi" && format != "acmi-zip" && format != "csv")
                return Fail("--format acmi|acmi-zip|csv");
            string destination = string.IsNullOrWhiteSpace(target) ? TelemetrySessionStore.ExportPath(path, format) : NormalizeTelemetryPath(target);
            TelemetryExportResult result = format == "csv"
                ? TelemetryCsvExport.Export(path, destination, perChannel, maxHz)
                : TelemetryAcmiExport.Export(path, destination, format == "acmi-zip", maxHz);
            if (textOutput)
                EmitPayload("Exported " + Path.GetFileName(path) + " as " + format + ": " + destination + " (" + result.Rows.ToString(CultureInfo.InvariantCulture) + " rows" +
                            (result.Objects > 0 ? ", " + result.Objects.ToString(CultureInfo.InvariantCulture) + " other objects" : string.Empty) + ")" +
                            string.Concat(result.Warnings.Select(w => Environment.NewLine + "  warning: " + w)), outPath);
            else
                EmitPayload(EngineSerialization.ToJson(new { Session = path, Format = format, Output = destination, result.Files, result.Frames, result.Rows, result.Objects, result.Warnings }), outPath);
            return 0;
        }

        private static int EmitTelemetryPreview(List<string> args, string folder, string outPath, bool textOutput)
        {
            string path = ResolveTelemetrySession(args, folder);
            List<string> channels = PopOptionValues(args, "--channel");
            string pointsText = PopOptionValue(args, "--points");
            if (args.Count > 0)
                return Fail("Unknown telemetry preview argument(s): " + string.Join(" ", args));
            int points;
            if (!int.TryParse(pointsText ?? string.Empty, NumberStyles.Integer, CultureInfo.InvariantCulture, out points))
                points = 200;
            List<TelemetryPreviewSeries> series = TelemetryPreview.Read(path, channels, Math.Max(4, Math.Min(5000, points)));
            if (textOutput)
                EmitPayload(string.Join(Environment.NewLine, series.Select(s => s.Key + " (" + s.Title + (s.Unit.Length > 0 ? ", " + s.Unit : string.Empty) + "): " +
                                                                                s.Values.Count.ToString(CultureInfo.InvariantCulture) + " points, " + TelemetrySessionFormat.FormatNumber(s.Min) + " .. " + TelemetrySessionFormat.FormatNumber(s.Max))), outPath);
            else
                EmitPayload(EngineSerialization.ToJson(new { Session = path, Series = series.Select(s => new { s.Key, s.Title, s.Unit, s.Min, s.Max, s.Times, s.Values }).ToList() }), outPath);
            return 0;
        }

        private static int DeleteTelemetrySessions(List<string> args, string folder, string outPath, bool textOutput)
        {
            List<string> deleted = new List<string>();
            List<string> problems = new List<string>();
            if (args.Count == 0)
                return Fail("telemetry delete <session>... (a path, a file name, \"latest\" or a number)");
            List<string> paths = new List<string>();
            while (args.Count > 0)
                paths.Add(ResolveTelemetrySession(args, folder));
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (TelemetrySessionStore.Delete(path, problems))
                    deleted.Add(path);
            }
            if (textOutput)
                EmitPayload("Deleted " + deleted.Count.ToString(CultureInfo.InvariantCulture) + " session(s)." + string.Concat(deleted.Select(d => Environment.NewLine + "  " + d)) + string.Concat(problems.Select(p => Environment.NewLine + "  not deleted: " + p)), outPath);
            else
                EmitPayload(EngineSerialization.ToJson(new { Deleted = deleted, Problems = problems }), outPath);
            return problems.Count > 0 ? Fail(string.Join(" | ", problems)) : 0;
        }

        // ---------------------------------------------------------------- recording now

        // JoyPro.Ffb.exe (the process that reads the games' telemetry): JOYPRO_FFB_EXE, else <engine>\..\ffb\.
        internal static string FindFfbProcessExe()
        {
            string configured = Environment.GetEnvironmentVariable("JOYPRO_FFB_EXE");
            if (!string.IsNullOrWhiteSpace(configured))
                return configured.Trim().Trim('"');
            string engineFolder = AppDomain.CurrentDomain.BaseDirectory;
            string bundled = Path.GetFullPath(Path.Combine(engineFolder, "..", "ffb", "JoyPro.Ffb.exe"));
            return bundled;
        }

        private static int RecordTelemetry(List<string> args, string folderOption)
        {
            string game = PopOptionValue(args, "--game");
            string seconds = PopOptionValue(args, "--seconds");
            string sampleHz = PopOptionValue(args, "--sample-hz");
            bool noRaw = PopFlag(args, "--no-raw");
            bool noGameCheck = PopFlag(args, "--no-game-check");
            if (args.Count > 0)
                return Fail("Unknown telemetry record argument(s): " + string.Join(" ", args));
            TelemetryRecordingGame spec = TelemetryRecordingGames.Find(game);
            if (spec == null)
                return Fail("telemetry record --game " + string.Join("|", TelemetryRecordingGames.All.Select(g => g.Id)) + " [--seconds N] [--folder F] [--sample-hz N] [--no-raw] [--no-game-check]");
            string exe = FindFfbProcessExe();
            if (!File.Exists(exe))
                return Fail("JoyPro.Ffb.exe not found (" + exe + "); it reads the games' telemetry. Set JOYPRO_FFB_EXE to its path.");
            List<string> arguments = new List<string> { "record", "--game", spec.Id, "--force" };
            if (!string.IsNullOrWhiteSpace(folderOption))
            {
                arguments.Add("--folder");
                arguments.Add(NormalizeTelemetryPath(folderOption));
            }
            if (!string.IsNullOrWhiteSpace(seconds))
            {
                arguments.Add("--duration");
                arguments.Add(seconds);
            }
            if (!string.IsNullOrWhiteSpace(sampleHz))
            {
                arguments.Add("--sample-hz");
                arguments.Add(sampleHz);
            }
            if (noRaw)
                arguments.Add("--no-raw");
            if (noGameCheck)
                arguments.Add("--no-game-check");
            ProcessStartInfo start = new ProcessStartInfo(exe, string.Join(" ", arguments.Select(QuoteTelemetryArgument)))
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)
            };
            Status("Recording " + spec.Title + " telemetry (Ctrl+C ends it): JoyPro.Ffb " + string.Join(" ", arguments));
            using (Process process = Process.Start(start))
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        private static string QuoteTelemetryArgument(string argument)
        {
            return argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? argument : "\"" + argument.Replace("\"", "\\\"") + "\"";
        }

        // ---------------------------------------------------------------- helpers

        private static string NormalizeTelemetryPath(string path)
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        }

        private static string TelemetryConfigString(Dictionary<string, object> json, string name)
        {
            object value;
            return json != null && json.TryGetValue(name, out value) && value is string ? (string)value : string.Empty;
        }

        private static bool TelemetryConfigBool(Dictionary<string, object> json, string name, bool fallback)
        {
            object value;
            if (json == null || !json.TryGetValue(name, out value))
                return fallback;
            if (value is bool)
                return (bool)value;
            bool parsed;
            return value is string && bool.TryParse((string)value, out parsed) ? parsed : fallback;
        }

        private static double TelemetryConfigNumber(Dictionary<string, object> json, string name, double fallback)
        {
            object value;
            if (json == null || !json.TryGetValue(name, out value) || value == null || value is bool)
                return fallback;
            double number;
            if (value is string)
                return double.TryParse((string)value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : fallback;
            try
            {
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return double.IsNaN(number) || double.IsInfinity(number) ? fallback : number;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
