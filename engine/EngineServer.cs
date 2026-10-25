using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace JoyPro.Engine
{
    // "engine serve": the app's engine server (docs/testing.md "Speed"). Every engine call of the app used to start a
    // process that loaded the .NET runtime, the profile (0.4-1.3 s for a big one) and the game lists, function map and
    // search index (up to 1 s) again; one user action is several calls. The server stays running for the app and runs
    // the app's interactive commands one after another, each exactly like its own engine process: its own arguments,
    // environment variables, console output, exit code and a fresh profile from the file (EngineProfileCache); what the
    // server keeps are the read-only lists and indexes. The app starts it, sends only commands of EngineServerCommands,
    // starts it again when it changed game data with a command of its own, and the server ends with the app (closed
    // stdin, the app's process gone) or after JOYPRO_SERVER_IDLE_MINUTES (default 20) without a command.
    //
    // Protocol on stdin/stdout, frames of [kind byte][int32 length][UTF-8 bytes]:
    //   app -> server   'R' {"Args":[...],"Env":{"NAME":"value",...}}   run a command (Env: set for this command only)
    //                   'Q'                                            quit
    //   server -> app   'H' engine version (once, at start)
    //                   'E' one stderr line, while the command runs (the busy area's progress lines)
    //                   'O' the command's whole stdout
    //                   'X' the exit code; the command is done
    internal static class EngineServer
    {
        public const string Command = "serve";
        public const string ParentVariable = "JOYPRO_SERVER_PARENT";
        public const string IdleVariable = "JOYPRO_SERVER_IDLE_MINUTES";

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly object WriteGate = new object();
        private static Stream output;
        private static long lastActivity;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private sealed class Request
        {
            public List<string> Args { get; set; }
            public Dictionary<string, string> Env { get; set; }
        }

        public static int Serve(string[] options)
        {
            // engine serve --self-test <profile>: the profile copy (ObjectGraphCloner) must serialize to the same bytes
            // as the profile read from the file, and is timed against the read (smoke test, docs/testing.md "Speed").
            if (options.Length == 2 && options[0] == "--self-test")
                return SelfTest(options[1]);
            // engine serve --script <file>: the commands of the file (one JSON string array per line) one after another
            // in this server, each line timed (benchmarks of the server path without the app).
            if (options.Length == 2 && options[0] == "--script")
                return RunScript(options[1]);
            if (options.Length == 3 && options[0] == "--diff-profiles")
                return DiffProfiles(options[1], options[2]);
            Stream input = Console.OpenStandardInput();
            output = Console.OpenStandardOutput();
            // Nothing a command or a background thread prints may reach the protocol stream.
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            Console.SetIn(new StringReader(string.Empty));
            EngineProfileCache.Enabled = true;
            EngineLoadCorrectionCache.Enabled = true;
            string startDirectory = Environment.CurrentDirectory;
            // What every command does first (EngineState.Initialize): the program folder, and the data folder's game
            // lists set up once. Done here, so the server's first look at the game data sees what its commands see.
            MainStructure.PROGPATH = EngineState.ResolveProgramPath();
            GameDataPaths.EnsureMigrated();
            Environment.CurrentDirectory = startDirectory;
            Touch();
            StartWatchdog();
            JoyPro.Logging.JoyProLog.Info("Server", "Engine server started (pid " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ")");
            Send('H', Utf8.GetBytes(typeof(EngineServer).Assembly.GetName().Version.ToString()));
            // The lists the first commands need, read now (the app sends its first command meanwhile; it waits for this).
            // The game data is stamped before, so a change during the warm-up still ends this server at the first command.
            stamp = DataStamp();
            busy = true;
            WarmUp(startDirectory);
            busy = false;
            int served = 0;
            while (true)
            {
                char kind;
                byte[] payload;
                if (!TryRead(input, out kind, out payload) || kind == 'Q')
                    break;
                if (kind != 'R')
                    continue;
                Touch();
                Request request;
                try
                {
                    request = EngineSerialization.FromJson<Request>(Utf8.GetString(payload));
                }
                catch (Exception ex)
                {
                    Send('O', new byte[0]);
                    Send('E', Utf8.GetBytes("engine error: bad server request: " + ex.Message));
                    Send('X', Utf8.GetBytes("1"));
                    continue;
                }
                // The game lists, function tables and catalogs the server keeps in memory must still be the files on
                // disk: changed meanwhile (a refresh, an update, a hand edit) -> 'S' and the app starts a new server.
                List<string> before = DataStamp();
                if (stamp != null && !SameStamp(stamp, before, "since the last command"))
                {
                    Send('S', new byte[0]);
                    break;
                }
                busy = true;
                trimmed = false;
                try
                {
                    RunRequest(request, startDirectory);
                }
                finally
                {
                    busy = false;
                }
                served++;
                Touch();
                List<string> after = DataStamp();
                bool changed = !SameStamp(before, after, "by the command");
                stamp = after;
                if (changed)
                {
                    // The command changed game data itself: its kept lists may be older than the files. The next
                    // request is answered with 'S' (the app sends it to a new server).
                    stamp = new List<string> { "changed" };
                }
            }
            JoyPro.Logging.JoyProLog.Info("Server", "Engine server stops after " + served.ToString(CultureInfo.InvariantCulture) + " command(s) (profile reads kept: " +
                                                    EngineProfileCache.Hits.ToString(CultureInfo.InvariantCulture) + ", read: " + EngineProfileCache.Misses.ToString(CultureInfo.InvariantCulture) +
                                                    "; load corrections kept: " + EngineLoadCorrectionCache.Hits.ToString(CultureInfo.InvariantCulture) + ", run: " + EngineLoadCorrectionCache.Misses.ToString(CultureInfo.InvariantCulture) + ")");
            return 0;
        }

        private static List<string> stamp;

        // Read-only commands on an empty profile that read the function map, search index, action descriptions and the
        // DCS lists, and the load corrections' lists. Their output is dropped; JOYPRO_SERVER_WARMUP=off skips it.
        private static void WarmUp(string startDirectory)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("JOYPRO_SERVER_WARMUP"), "off", StringComparison.OrdinalIgnoreCase))
                return;
            Stopwatch watch = Stopwatch.StartNew();
            string emptyProfile = Path.Combine(Path.GetTempPath(), "joypro-server-warmup-" + Guid.NewGuid().ToString("N") + ".pr0file");
            // A throwaway profile in the temp folder (no history of it) also runs the profile read, the load corrections
            // (DB fingerprint, device check) and a save once, so the user's first profile load does not pay for them.
            string previousHistory = Environment.GetEnvironmentVariable("JOYPRO_PROFILE_HISTORY");
            Environment.SetEnvironmentVariable("JOYPRO_PROFILE_HISTORY", "off");
            // The engine's "last opened profile" note is put back afterwards (the throwaway profile is not the user's).
            string metaPath = MainStructure.GetMetaPath();
            byte[] meta = File.Exists(metaPath) ? File.ReadAllBytes(metaPath) : null;
            string[][] commands =
            {
                new[] { "--profile", emptyProfile, "--status", "false", "--load-corrections", "false", "relations", "search", "pitch", "--limit", "1" },
                new[] { "--profile", emptyProfile, "--status", "false", "--load-corrections", "false", "relations", "browse", "--game", "DCS", "--plane", "F-16C_50" },
                new[] { "--profile", emptyProfile, "--status", "false", "relations", "add", "Warm-up", "--button", "--node", "DCS:warmup:F-16C_50" },
                new[] { "--profile", emptyProfile, "--status", "false", "relations", "add", "Warm-up 2", "--button", "--node", "DCS:warmup2:F-16C_50" }
            };
            foreach (string[] args in commands)
            {
                try
                {
                    Environment.CurrentDirectory = startDirectory;
                    ResetBetweenCommands();
                    Console.SetOut(TextWriter.Null);
                    Console.SetError(TextWriter.Null);
                    Program.Execute(args);
                }
                catch (Exception ex)
                {
                    JoyPro.Logging.JoyProLog.Warn("Server", "Warm-up command failed (" + string.Join(" ", args.Skip(6)) + ")", ex);
                }
            }
            try
            {
                InternalDataManagement.WarmCorrectionIndex();
                EngineRelationSuggest.WarmSuggestionCatalog();
            }
            catch (Exception ex)
            {
                JoyPro.Logging.JoyProLog.Warn("Server", "Warm-up of the correction and suggestion lists failed", ex);
            }
            Environment.CurrentDirectory = startDirectory;
            Environment.SetEnvironmentVariable("JOYPRO_PROFILE_HISTORY", previousHistory);
            EngineProfileCache.Forget();
            EngineLoadCorrectionCache.Forget();
            try
            {
                if (meta != null)
                    File.WriteAllBytes(metaPath, meta);
                else if (File.Exists(metaPath))
                    File.Delete(metaPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                JoyPro.Logging.JoyProLog.Warn("Server", "Could not put back the engine's last-profile note after the warm-up", ex);
            }
            if (File.Exists(emptyProfile))
                File.Delete(emptyProfile);
            JoyPro.Logging.JoyProLog.Info("Server", "Warm-up done in " + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
        }

        private static bool SameStamp(List<string> a, List<string> b, string when)
        {
            if (a.SequenceEqual(b, StringComparer.Ordinal))
                return true;
            HashSet<string> left = new HashSet<string>(a, StringComparer.Ordinal);
            HashSet<string> right = new HashSet<string>(b, StringComparer.Ordinal);
            IEnumerable<string> differences = left.Except(right).Select(x => "- " + x).Concat(right.Except(left).Select(x => "+ " + x)).Take(6);
            JoyPro.Logging.JoyProLog.Info("Server", "Game data changed " + when + ": the app gets a new server for the next command (" + string.Join("; ", differences) + ")");
            return false;
        }

        // Name, size and time of every file and folder the kept lists are made of: the game lists (DB of the program and
        // of the data folder), the function tables, the layout catalog, test overrides. A few milliseconds.
        internal static List<string> DataStamp()
        {
            List<string> folders = new List<string>
            {
                Path.Combine(GameDataPaths.ProgramRoot(), "DB"),
                Path.Combine(GameDataPaths.ProgramRoot(), "LayoutCatalog"),
                Path.Combine(GameDataPaths.UserRoot(), "DB"),
                EngineNlGroup.FunctionsFolder(),
                EngineRelationSuggest.IndexOverrideFolder()
            };
            List<string> entries = new List<string>();
            foreach (string folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    DirectoryInfo directory = new DirectoryInfo(folder);
                    if (!directory.Exists)
                    {
                        entries.Add(folder + " (none)");
                        continue;
                    }
                    foreach (FileSystemInfo item in directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                    {
                        FileInfo file = item as FileInfo;
                        entries.Add(file == null
                            ? item.FullName + "\\"
                            : item.FullName + "|" + file.Length.ToString(CultureInfo.InvariantCulture) + "|" + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    entries.Add(folder + " !" + ex.GetType().Name);
                }
            }
            string knowledge = EngineFunctionMap.KnowledgeFile();
            FileInfo info = new FileInfo(knowledge);
            entries.Add(knowledge + (info.Exists ? "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks : " (none)"));
            entries.Sort(StringComparer.Ordinal);
            return entries;
        }

        // engine serve --diff-profiles <a> <b>: the first places where two profiles differ in a value or in which values
        // are one shared instance (development: why two saved profiles have different bytes).
        private static int DiffProfiles(string a, string b)
        {
            Pr0file left = InternalDataManagement.ReadProfileBytes(File.ReadAllBytes(a));
            Pr0file right = InternalDataManagement.ReadProfileBytes(File.ReadAllBytes(b));
            Dictionary<object, object> map = new Dictionary<object, object>(new IdentityComparer());
            Dictionary<object, object> back = new Dictionary<object, object>(new IdentityComparer());
            List<string> found = new List<string>();
            Walk(left, right, "profile", map, back, found);
            foreach (string line in found.Take(20))
                Console.WriteLine(line);
            Console.WriteLine(found.Count + " difference(s)");
            return found.Count == 0 ? 0 : 1;
        }

        private sealed class IdentityComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }

        private static void Walk(object x, object y, string path, Dictionary<object, object> map, Dictionary<object, object> back, List<string> found)
        {
            if (found.Count > 50)
                return;
            if (x == null || y == null)
            {
                if (x != null || y != null)
                    found.Add(path + ": " + (x ?? "null") + " / " + (y ?? "null"));
                return;
            }
            Type type = x.GetType();
            if (type != y.GetType())
            {
                found.Add(path + ": type " + type.Name + " / " + y.GetType().Name);
                return;
            }
            bool isValue = type.IsValueType;
            if (!isValue)
            {
                object mapped, mappedBack;
                bool seenX = map.TryGetValue(x, out mapped), seenY = back.TryGetValue(y, out mappedBack);
                if (seenX || seenY)
                {
                    if (!ReferenceEquals(mapped, y) || !ReferenceEquals(mappedBack, x))
                        found.Add(path + ": shared differently (" + type.Name + (type == typeof(string) ? " '" + x + "'" : string.Empty) + ")");
                    return;
                }
                map[x] = y;
                back[y] = x;
            }
            if (type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(DateTime))
            {
                if (!x.Equals(y))
                    found.Add(path + ": '" + x + "' / '" + y + "'");
                return;
            }
            if (type.IsArray)
            {
                Array ax = (Array)x, ay = (Array)y;
                if (ax.Length != ay.Length)
                {
                    found.Add(path + ": length " + ax.Length + " / " + ay.Length);
                    return;
                }
                for (int i = 0; i < ax.Length; i++)
                    Walk(ax.GetValue(i), ay.GetValue(i), path + "[" + i + "]", map, back, found);
                return;
            }
            for (Type t = type; t != null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
            {
                foreach (System.Reflection.FieldInfo field in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (field.IsNotSerialized)
                        continue;
                    Walk(field.GetValue(x), field.GetValue(y), path + "." + field.Name, map, back, found);
                }
            }
        }

        private static int scriptIndex;

        private static int RunScript(string scriptPath)
        {
            TextWriter report = Console.Out;
            EngineProfileCache.Enabled = true;
            EngineLoadCorrectionCache.Enabled = true;
            string startDirectory = Environment.CurrentDirectory;
            MainStructure.PROGPATH = EngineState.ResolveProgramPath();
            GameDataPaths.EnsureMigrated();
            Environment.CurrentDirectory = startDirectory;
            // As the app's server: warmed up first (timed on its own line).
            Stopwatch warm = Stopwatch.StartNew();
            WarmUp(startDirectory);
            Console.SetOut(report);
            report.WriteLine(warm.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture).PadLeft(7) + " ms  warm-up");
            int failed = 0;
            foreach (string line in File.ReadAllLines(scriptPath))
            {
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                    continue;
                string[] args = EngineSerialization.FromJson<string[]>(line);
                StringWriter stdout = new StringWriter(CultureInfo.InvariantCulture);
                StringWriter stderr = new StringWriter(CultureInfo.InvariantCulture);
                Stopwatch watch = Stopwatch.StartNew();
                int exitCode;
                try
                {
                    Environment.CurrentDirectory = startDirectory;
                    ResetBetweenCommands();
                    Console.SetOut(stdout);
                    Console.SetError(stderr);
                    EngineLogging.CaptureConsole(force: true);
                    exitCode = Program.Execute(args);
                }
                finally
                {
                    Console.SetOut(report);
                    Console.SetError(TextWriter.Null);
                    EngineLogging.ReleaseConsole();
                }
                if (exitCode != 0)
                    failed++;
                // JOYPRO_SCRIPT_OUT=<folder>: each command's exit code, stdout and stderr in <n>.txt (old/new comparisons).
                string outFolder = Environment.GetEnvironmentVariable("JOYPRO_SCRIPT_OUT");
                if (!string.IsNullOrWhiteSpace(outFolder))
                {
                    Directory.CreateDirectory(outFolder);
                    File.WriteAllText(Path.Combine(outFolder, (++scriptIndex).ToString("D4", CultureInfo.InvariantCulture) + ".txt"),
                        "exit " + exitCode + "\n" + stdout + "\n--stderr--\n" + stderr, Utf8);
                }
                report.WriteLine(watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture).PadLeft(7) + " ms  exit " + exitCode + "  " + string.Join(" ", args.Select(a => a.Length > 60 ? a.Substring(0, 60) + "..." : a)) +
                                 "  (" + stdout.GetStringBuilder().Length.ToString(CultureInfo.InvariantCulture) + " chars" + (exitCode != 0 ? ": " + stderr.ToString().Trim().Split('\n').LastOrDefault() : string.Empty) + ")");
            }
            return failed == 0 ? 0 : 1;
        }

        private static int SelfTest(string profilePath)
        {
            byte[] content = File.ReadAllBytes(profilePath);
            Stopwatch watch = Stopwatch.StartNew();
            Pr0file read = InternalDataManagement.ReadProfileBytes(content);
            long readMs = watch.ElapsedMilliseconds;
            ObjectGraphCloner.Clone(read);
            watch.Restart();
            Pr0file copy = ObjectGraphCloner.Clone(read);
            long cloneMs = watch.ElapsedMilliseconds;
            byte[] original = Serialize(read);
            byte[] copied = Serialize(copy);
            bool same = original.Length == copied.Length && original.SequenceEqual(copied);
            // The copy is a graph of its own: a change to it leaves the read graph as it was.
            bool separate = true;
            foreach (KeyValuePair<string, Relation> relation in copy.Relations.ToList())
            {
                relation.Value.NAME = relation.Value.NAME + " (changed)";
                relation.Value.NODES?.Clear();
            }
            copy.Relations.Clear();
            separate = Serialize(read).SequenceEqual(original);

            // After a save: a profile that had relations and aircraft removed (free slots in its dictionaries) is written;
            // the copy "as serialized" must behave like the file read back, also when new entries come in afterwards.
            Pr0file live = InternalDataManagement.ReadProfileBytes(content);
            foreach (string name in live.Relations.Keys.Where((k, i) => i % 7 == 3).ToList())
                live.Relations.Remove(name);
            foreach (Relation relation in live.Relations.Values.Where((r, i) => i % 5 == 1))
            {
                foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
                {
                    foreach (string plane in (node.AIRCRAFT ?? new Dictionary<string, bool>()).Keys.Where((k, i) => i % 3 == 0).ToList())
                        node.AIRCRAFT.Remove(plane);
                }
            }
            byte[] written = Serialize(live);
            watch.Restart();
            Pr0file asSerialized = ObjectGraphCloner.CloneAsSerialized(live);
            long compactMs = watch.ElapsedMilliseconds;
            Pr0file readBack = InternalDataManagement.ReadProfileBytes(written);
            Action<Pr0file> grow = p =>
            {
                Relation template = p.Relations.Values.First(r => r.NODES != null && r.NODES.Count > 0 && r.NODES[0].AIRCRAFT != null);
                for (int i = 0; i < 40; i++)
                {
                    p.Relations["Self-test " + i] = template;
                    template.NODES[0].AIRCRAFT["Self-test plane " + i] = true;
                }
            };
            grow(asSerialized);
            grow(readBack);
            bool sameAfterSave = Serialize(asSerialized).SequenceEqual(Serialize(readBack));
            Console.WriteLine("after a save: copy " + compactMs.ToString(CultureInfo.InvariantCulture) + " ms, same as read back (also after new entries): " + sameAfterSave);
            separate = separate && sameAfterSave;
            Console.WriteLine("profile " + profilePath + ": " + content.Length.ToString(CultureInfo.InvariantCulture) + " bytes, read " + readMs.ToString(CultureInfo.InvariantCulture) +
                              " ms, copy " + cloneMs.ToString(CultureInfo.InvariantCulture) + " ms, same bytes: " + same + ", separate graph: " + separate);
            bool corrections = CorrectionCacheSelfTest(read);
            return same && separate && corrections ? 0 : 1;
        }

        // The load correction the server keeps (EngineLoadCorrectionCache) leaves exactly what a correction that runs
        // leaves (profile, modifiers, devices, groups, report); a changed profile file or a changed DB list runs it again.
        // In a data folder of its own (the DB change is made there), with the profile made older than the DB.
        private static bool CorrectionCacheSelfTest(Pr0file read)
        {
            string startDirectory = Environment.CurrentDirectory;
            string previousData = Environment.GetEnvironmentVariable(GameBackups.DataFolderVariable);
            string previousProgram = MainStructure.PROGPATH;
            bool profileCache = EngineProfileCache.Enabled;
            bool correctionCache = EngineLoadCorrectionCache.Enabled;
            string temp = Path.Combine(Path.GetTempPath(), "joypro-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                Environment.SetEnvironmentVariable(GameBackups.DataFolderVariable, Path.Combine(temp, "data"));
                MainStructure.PROGPATH = EngineState.ResolveProgramPath();
                Environment.CurrentDirectory = MainStructure.PROGPATH;
                GameDataPaths.Migrate(GameDataPaths.ProgramRoot(), GameDataPaths.UserRoot());

                read.DatabaseFingerprint = string.Empty;
                string profile = Path.Combine(temp, "older.pr0file");
                File.WriteAllBytes(profile, Serialize(read));

                // As the app's server loads a profile for a change.
                InternalDataManagement.EnableLegacyLoadCorrections = true;
                InternalDataManagement.AutoAddDbItemsOnLoad = true;
                InternalDataManagement.RemapMissingJoysticksOnLoad = true;
                InternalDataManagement.ConfirmMissingJoystickRemapOnLoad = false;
                InternalDataManagement.ConfirmItemCorrectionsOnLoad = false;
                EngineProfileCache.Enabled = true;
                EngineLoadCorrectionCache.Enabled = true;
                EngineProfileCache.Forget();
                EngineLoadCorrectionCache.Forget();
                Func<string> load = () =>
                {
                    InternalDataManagement.LoadProfile(profile);
                    return InternalDataManagement.LoadedStateDigest();
                };

                int misses = EngineLoadCorrectionCache.Misses;
                int hits = EngineLoadCorrectionCache.Hits;
                Stopwatch watch = Stopwatch.StartNew();
                string ran = load();
                long ranMs = watch.ElapsedMilliseconds;
                watch.Restart();
                string kept = load();
                long keptMs = watch.ElapsedMilliseconds;
                bool wasKept = EngineLoadCorrectionCache.Misses == misses + 1 && EngineLoadCorrectionCache.Hits == hits + 1;
                bool sameState = string.Equals(ran, kept, StringComparison.Ordinal);
                bool corrected = ran.Contains("entry=db-fingerprint-check|auto|");

                // The file changed (time stamp): the correction runs again and gives the same.
                File.SetLastWriteTimeUtc(profile, File.GetLastWriteTimeUtc(profile).AddSeconds(2));
                misses = EngineLoadCorrectionCache.Misses;
                string afterFile = load();
                bool fileRunsAgain = EngineLoadCorrectionCache.Misses == misses + 1 && string.Equals(afterFile, ran, StringComparison.Ordinal);

                // A command list of the DB changed (another DB fingerprint): the correction runs again against it.
                string list = Path.Combine(GameDataPaths.UserRoot(), "DB", "IL2", "IL2.actions");
                Directory.CreateDirectory(Path.GetDirectoryName(list));
                File.AppendAllText(list, "\r\n");
                misses = EngineLoadCorrectionCache.Misses;
                string afterDb = load();
                Func<string, string> fingerprints = digest => digest.Split('\n').FirstOrDefault(l => l.StartsWith("fingerprints=", StringComparison.Ordinal)) ?? string.Empty;
                bool dbRunsAgain = EngineLoadCorrectionCache.Misses == misses + 1 && fingerprints(afterDb) != fingerprints(ran);

                Console.WriteLine("load corrections: ran " + ranMs.ToString(CultureInfo.InvariantCulture) + " ms, kept " + keptMs.ToString(CultureInfo.InvariantCulture) +
                                  " ms, corrected: " + corrected + ", kept: " + wasKept + ", same state as a run: " + sameState +
                                  ", run again after a file change: " + fileRunsAgain + ", after a DB change: " + dbRunsAgain);
                return corrected && wasKept && sameState && fileRunsAgain && dbRunsAgain;
            }
            catch (Exception ex)
            {
                Console.WriteLine("load corrections: self-test failed: " + ex.Message);
                return false;
            }
            finally
            {
                EngineProfileCache.Forget();
                EngineLoadCorrectionCache.Forget();
                EngineProfileCache.Enabled = profileCache;
                EngineLoadCorrectionCache.Enabled = correctionCache;
                Environment.SetEnvironmentVariable(GameBackups.DataFolderVariable, previousData);
                MainStructure.PROGPATH = previousProgram;
                Environment.CurrentDirectory = startDirectory;
                try
                {
                    Directory.Delete(temp, true);
                }
                catch (Exception)
                {
                    // A temp folder left behind is harmless.
                }
            }
        }

        private static byte[] Serialize(Pr0file profile)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                System.Runtime.Serialization.Formatters.Binary.BinaryFormatter formatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                formatter.Binder = new LegacyJoyProSerializationBinder();
                formatter.Serialize(buffer, profile);
                return buffer.ToArray();
            }
        }

        private static void RunRequest(Request request, string startDirectory)
        {
            string[] args = (request.Args ?? new List<string>()).ToArray();
            Dictionary<string, string> previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> variable in request.Env ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(variable.Key))
                    continue;
                previous[variable.Key] = Environment.GetEnvironmentVariable(variable.Key);
                Environment.SetEnvironmentVariable(variable.Key, variable.Value);
            }
            string parent;
            JoyPro.Logging.JoyProLog.SetParent(request.Env != null && request.Env.TryGetValue(JoyPro.Logging.JoyProLog.ParentVariable, out parent) ? parent : string.Empty);
            StringWriter stdout = new StringWriter(CultureInfo.InvariantCulture);
            LineWriter stderr = new LineWriter(line => Send('E', Utf8.GetBytes(line)));
            int exitCode = 1;
            try
            {
                Environment.CurrentDirectory = startDirectory;
                ResetBetweenCommands();
                Console.SetOut(stdout);
                Console.SetError(stderr);
                EngineLogging.CaptureConsole(force: true);
                exitCode = Program.Execute(args);
            }
            catch (Exception ex)
            {
                // Program.Execute handles the command's own errors; this is the server's.
                try
                {
                    stderr.WriteLine("engine error: " + ex.Message);
                }
                catch (Exception)
                {
                }
                exitCode = 1;
            }
            finally
            {
                try
                {
                    Console.Out.Flush();
                    Console.Error.Flush();
                }
                catch (Exception)
                {
                }
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);
                EngineLogging.ReleaseConsole();
                stderr.FlushPartial();
                foreach (KeyValuePair<string, string> variable in previous)
                    Environment.SetEnvironmentVariable(variable.Key, variable.Value);
                JoyPro.Logging.JoyProLog.SetParent(string.Empty);
                Send('O', Utf8.GetBytes(stdout.ToString()));
                Send('X', Utf8.GetBytes(exitCode.ToString(CultureInfo.InvariantCulture)));
            }
        }

        // What a fresh engine process would not have from the last command: per-run lists and the device and install
        // state that can change between two commands (a device plugged in, a game installed). Kept: the game lists,
        // function map, search index and other caches of files only the app's own data commands change (the app starts
        // the server again after those, EngineServerCommands).
        internal static void ResetBetweenCommands()
        {
            EngineAxisDetents.Warnings.Clear();
            EngineFunctionMap.ForgetBuildWarnings();
            InternalDataManagement.ForgetPlaneLookupIndex();
            GameExportSafety.ResetRun();
            GameInstalls.ResetCache();
            DcsDeviceNames.SetConnectedForTests(null);
            EngineStarCitizen.ResetConnectedDevices();
            EngineProfileHistory.NoCoalesce = false;
            InternalDataManagement.OverwriteExistingRelationOnInsertInEngine = false;
        }

        private static void Touch()
        {
            Interlocked.Exchange(ref lastActivity, Clock.ElapsedMilliseconds);
        }

        // Ends the server when the app is gone or nothing came for a long time (no engine left behind).
        private static void StartWatchdog()
        {
            int parentPid;
            int.TryParse(Environment.GetEnvironmentVariable(ParentVariable) ?? string.Empty, NumberStyles.Integer, CultureInfo.InvariantCulture, out parentPid);
            double idleMinutes;
            if (!double.TryParse(Environment.GetEnvironmentVariable(IdleVariable) ?? string.Empty, NumberStyles.Float, CultureInfo.InvariantCulture, out idleMinutes) || idleMinutes <= 0)
                idleMinutes = 20;
            Process parent = null;
            if (parentPid > 0)
            {
                try
                {
                    parent = Process.GetProcessById(parentPid);
                }
                catch (ArgumentException)
                {
                    Environment.Exit(0);
                }
            }
            Thread watchdog = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(1000);
                    bool parentGone = false;
                    try
                    {
                        parentGone = parent != null && parent.HasExited;
                    }
                    catch (Exception)
                    {
                    }
                    long quietMs = Clock.ElapsedMilliseconds - Interlocked.Read(ref lastActivity);
                    // Quiet for 3 s after a command: the garbage of the commands is collected and the memory given back,
                    // the kept lists stay (low footprint while the app is idle).
                    if (!busy && !trimmed && quietMs > 3000)
                    {
                        trimmed = true;
                        TrimMemory();
                    }
                    bool idle = quietMs > idleMinutes * 60000;
                    if (parentGone || idle)
                    {
                        JoyPro.Logging.JoyProLog.Info("Server", "Engine server ends: " + (parentGone ? "the app has ended" : "idle for " + idleMinutes.ToString(CultureInfo.InvariantCulture) + " min"));
                        JoyPro.Logging.JoyProLog.Flush();
                        Environment.Exit(0);
                    }
                }
            }) { IsBackground = true, Name = "server-watchdog" };
            watchdog.Start();
        }

        private static volatile bool busy;
        private static volatile bool trimmed;

        [System.Runtime.InteropServices.DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr process);

        private static void TrimMemory()
        {
            try
            {
                long before = Process.GetCurrentProcess().WorkingSet64;
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
                JoyPro.Logging.JoyProLog.Debug("Server", "Memory after the last command: " + (before / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB -> managed " +
                                                         (GC.GetTotalMemory(false) / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB");
            }
            catch (Exception ex)
            {
                JoyPro.Logging.JoyProLog.Debug("Server", "Memory trim failed: " + ex.Message);
            }
        }

        private static void Send(char kind, byte[] payload)
        {
            lock (WriteGate)
            {
                byte[] header = new byte[5];
                header[0] = (byte)kind;
                BitConverter.GetBytes(payload.Length).CopyTo(header, 1);
                output.Write(header, 0, header.Length);
                output.Write(payload, 0, payload.Length);
                output.Flush();
            }
        }

        private static bool sawFirstFrame;

        private static bool ReadExactly(Stream input, byte[] buffer, int count, int offset)
        {
            int read = 0;
            while (read < count)
            {
                int n = input.Read(buffer, offset + read, count - read);
                if (n <= 0)
                    return false;
                read += n;
            }
            return true;
        }

        private static bool TryRead(Stream input, out char kind, out byte[] payload)
        {
            kind = '\0';
            payload = null;
            byte[] header = new byte[5];
            if (!ReadExactly(input, header, 5))
                return false;
            // A .NET Framework or PowerShell parent writes the UTF-8 byte order mark of its stdin writer first: skipped.
            if (!sawFirstFrame && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
            {
                header[0] = header[3];
                header[1] = header[4];
                if (!ReadExactly(input, header, 3, 2))
                    return false;
            }
            sawFirstFrame = true;
            kind = (char)header[0];
            int length = BitConverter.ToInt32(header, 1);
            if (length < 0 || length > 256 * 1024 * 1024)
                return false;
            payload = new byte[length];
            return ReadExactly(input, payload, length);
        }

        private static bool ReadExactly(Stream input, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = input.Read(buffer, read, count - read);
                if (n <= 0)
                    return false;
                read += n;
            }
            return true;
        }

        // stderr of one command: each finished line goes to the app at once (live progress), like a process's stderr.
        private sealed class LineWriter : TextWriter
        {
            private readonly Action<string> onLine;
            private readonly StringBuilder line = new StringBuilder();
            private readonly object gate = new object();

            public LineWriter(Action<string> onLine)
            {
                this.onLine = onLine;
            }

            public override Encoding Encoding
            {
                get { return Utf8; }
            }

            public override void Write(char value)
            {
                lock (gate)
                {
                    if (value == '\n')
                    {
                        string text = line.ToString();
                        line.Clear();
                        onLine(text.EndsWith("\r", StringComparison.Ordinal) ? text.Substring(0, text.Length - 1) : text);
                        return;
                    }
                    line.Append(value);
                }
            }

            public override void Write(string value)
            {
                if (value == null)
                    return;
                lock (gate)
                {
                    foreach (char c in value)
                        Write(c);
                }
            }

            public override void Write(char[] buffer, int index, int count)
            {
                lock (gate)
                {
                    for (int i = 0; i < count; i++)
                        Write(buffer[index + i]);
                }
            }

            public void FlushPartial()
            {
                lock (gate)
                {
                    if (line.Length == 0)
                        return;
                    string text = line.ToString();
                    line.Clear();
                    onLine(text);
                }
            }
        }
    }
}
