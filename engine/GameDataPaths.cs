using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace JoyPro.Engine
{
    // Where JoyPro keeps game data (docs/installs.md "Game data").
    //
    // Program folder (engine.exe's folder, MainStructure.PROGPATH): JoyPro's own data, shipped with JoyPro and replaced
    //   by every update or rebuild: DB\Functions, DB\Devices, DB\Llm, DB\DevicePhotos, DB\DeviceLayouts,
    //   DB\Cockpits, TOOLS\Conversions, LayoutCatalog, Clicks.
    // Game-data root (<data>\GameData, data = JOYPRO_DATA_DIR or %LOCALAPPDATA%\JoyPro): everything built from the
    //   user's games: DB\<Game> (command lists, aircraft lists, source-version.txt), CleanProfile\<Game> and
    //   KeyboardCleanProfile\<Game>. Writable without admin rights, and no update or rebuild touches it.
    //
    // Reading: per game folder (DB\<Game>, CleanProfile\<Game>, KeyboardCleanProfile\<Game>) the game-data root's copy
    //   whenever it exists, else the program folder's (a development build carries EngineOnly_Runtime's lists; a release
    //   ships none).
    // Writing: only into the game-data root. The first write into a folder the root does not have yet copies the
    //   program folder's copy over first, so refreshing one aircraft keeps the others.
    // Migration (every engine start, cheap; Migrate): a game folder of the program folder that the root does not have is
    //   copied over (this keeps lists that older versions refreshed into the program folder), and a program-folder DB
    //   whose source-version.txt is newer than the root's (a newer game version, more modules with the same version)
    //   replaces the root's copy, the old one kept under GameData\Replaced\<time>. Nothing is ever deleted.
    internal static class GameDataPaths
    {
        public const string FolderName = "GameData";
        public const string StampFile = "source-version.txt";
        public const string ReplacedFolderName = "Replaced";

        // DB folders that are JoyPro's own data (tools/ReleaseContent.psm1 ships exactly these).
        private static readonly HashSet<string> ShippedDbFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Functions", "Devices", "Llm", "DevicePhotos", "DeviceLayouts", "DeviceDrawings", "Cockpits", "Detents"
        };

        // Top folders of built data next to DB: one subfolder per game.
        private static readonly string[] CleanProfileFolders = { "CleanProfile", "KeyboardCleanProfile" };

        private static readonly object Sync = new object();

        public static string ProgramRoot()
        {
            string root = MainStructure.PROGPATH;
            return string.IsNullOrWhiteSpace(root) ? Environment.CurrentDirectory : root;
        }

        public static string UserRoot()
        {
            return Path.Combine(GameInstalls.DataRoot(), FolderName);
        }

        public static bool IsShippedDbFolder(string folder)
        {
            return ShippedDbFolders.Contains((folder ?? string.Empty).Trim());
        }

        // ---- Reading ----

        // DB\<folder> to read from: the game-data root's when it exists, else the program folder's (JoyPro's own DB
        // folders always come from the program folder).
        public static string DbFolder(string folder)
        {
            if (IsShippedDbFolder(folder))
                return Path.Combine(ProgramRoot(), "DB", folder ?? string.Empty);
            return ReadFolder(Path.Combine("DB", folder ?? string.Empty));
        }

        public static string DbFile(string folder, string fileName)
        {
            return Path.Combine(DbFolder(folder), fileName ?? string.Empty);
        }

        // CleanProfile\<game> (KeyboardCleanProfile\<game>) to read from.
        public static string CleanProfileFolder(string game, bool keyboard)
        {
            return ReadFolder(Path.Combine(keyboard ? "KeyboardCleanProfile" : "CleanProfile", game ?? string.Empty));
        }

        // Where a game folder is read from: "user" (game-data root), "program" or "none".
        public static string SourceOf(string relativeFolder)
        {
            if (Directory.Exists(Path.Combine(UserRoot(), relativeFolder)))
                return "user";
            return Directory.Exists(Path.Combine(ProgramRoot(), relativeFolder)) ? "program" : "none";
        }

        // Every game DB folder of both roots (name -> folder to read), the game-data root's first.
        public static List<KeyValuePair<string, string>> GameDbFolders()
        {
            Dictionary<string, string> folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in new[] { UserRoot(), ProgramRoot() })
            {
                string db = Path.Combine(root, "DB");
                if (!Directory.Exists(db))
                    continue;
                foreach (string path in Directory.GetDirectories(db))
                {
                    string name = Path.GetFileName(path);
                    if (!IsShippedDbFolder(name) && !folders.ContainsKey(name))
                        folders[name] = path;
                }
            }
            return folders.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ---- Writing ----

        // DB\<folder> in the game-data root, created (with the program folder's files when the root had none yet).
        public static string WritableDbFolder(string folder)
        {
            if (IsShippedDbFolder(folder))
                throw new InvalidOperationException("DB\\" + folder + " is JoyPro's own data and is not written at run time.");
            return WriteFolder(Path.Combine("DB", folder ?? string.Empty));
        }

        public static string WritableDbFile(string folder, string fileName)
        {
            return Path.Combine(WritableDbFolder(folder), fileName ?? string.Empty);
        }

        public static string WritableCleanProfileFolder(string game, bool keyboard)
        {
            return WriteFolder(Path.Combine(keyboard ? "KeyboardCleanProfile" : "CleanProfile", game ?? string.Empty));
        }

        // A DB file path given by the user (--db-path) or JoyPro's default: the default goes to the game-data root.
        public static string WritableDbFileOrOverride(string overridePath, string folder, string fileName)
        {
            return string.IsNullOrWhiteSpace(overridePath) ? WritableDbFile(folder, fileName) : overridePath;
        }

        private static string ReadFolder(string relative)
        {
            string user = Path.Combine(UserRoot(), relative);
            if (Directory.Exists(user))
                return user;
            string program = Path.Combine(ProgramRoot(), relative);
            return Directory.Exists(program) ? program : user;
        }

        private static string WriteFolder(string relative)
        {
            string user = Path.Combine(UserRoot(), relative);
            if (Directory.Exists(user))
                return user;
            string program = Path.Combine(ProgramRoot(), relative);
            lock (Sync)
            {
                if (!Directory.Exists(user) && Directory.Exists(program) && !SamePath(program, user))
                    CopyFolderAtomic(program, user);
                Directory.CreateDirectory(user);
            }
            return user;
        }

        // ---- Migration ----

        public sealed class MigrationReport
        {
            public List<string> Copied = new List<string>();
            public List<string> Replaced = new List<string>();
            public List<string> Warnings = new List<string>();
        }

        private static bool migrated;

        // Once per process (EngineState.Initialize): see the class comment.
        public static MigrationReport EnsureMigrated()
        {
            lock (Sync)
            {
                if (migrated)
                    return new MigrationReport();
                migrated = true;
            }
            return Migrate(ProgramRoot(), UserRoot());
        }

        public static MigrationReport Migrate(string programRoot, string userRoot)
        {
            MigrationReport report = new MigrationReport();
            if (string.IsNullOrWhiteSpace(programRoot) || string.IsNullOrWhiteSpace(userRoot) ||
                !Directory.Exists(Path.Combine(programRoot, "DB")) || SamePath(programRoot, userRoot))
                return report;

            List<string> units = GameUnits(programRoot);
            if (units.Count == 0)
                return report;

            // Several engines may start at once (the GUI runs some in parallel): one migrates, the others wait.
            using (Mutex mutex = new Mutex(false, "Local\\JoyPro.GameData." + Hash(Path.GetFullPath(userRoot).TrimEnd('\\').ToLowerInvariant())))
            {
                bool owned = false;
                try
                {
                    try
                    {
                        owned = mutex.WaitOne(TimeSpan.FromSeconds(60));
                    }
                    catch (AbandonedMutexException)
                    {
                        owned = true;
                    }
                    if (!owned)
                    {
                        report.Warnings.Add("Game data migration skipped: another JoyPro process holds the lock.");
                        return report;
                    }
                    MigrateUnits(programRoot, userRoot, units, report);
                }
                catch (Exception ex)
                {
                    report.Warnings.Add("Game data migration failed: " + ex.Message);
                }
                finally
                {
                    if (owned)
                        mutex.ReleaseMutex();
                }
            }

            foreach (string copied in report.Copied)
                JoyPro.Logging.JoyProLog.Info("GameData", "Copied " + copied + " from the program folder to " + userRoot + ".");
            foreach (string replaced in report.Replaced)
                JoyPro.Logging.JoyProLog.Info("GameData", "Replaced " + replaced + " in " + userRoot + " with the program folder's newer copy.");
            foreach (string warning in report.Warnings)
                JoyPro.Logging.JoyProLog.Warn("GameData", warning);
            return report;
        }

        // The game names with built data in the program folder: DB\<Game>, CleanProfile\<Game>, KeyboardCleanProfile\<Game>.
        private static List<string> GameUnits(string programRoot)
        {
            HashSet<string> games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetDirectories(Path.Combine(programRoot, "DB")))
            {
                string name = Path.GetFileName(path);
                if (!IsShippedDbFolder(name))
                    games.Add(name);
            }
            foreach (string top in CleanProfileFolders)
            {
                string folder = Path.Combine(programRoot, top);
                if (Directory.Exists(folder))
                    foreach (string path in Directory.GetDirectories(folder))
                        games.Add(Path.GetFileName(path));
            }
            return games.OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void MigrateUnits(string programRoot, string userRoot, List<string> games, MigrationReport report)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            foreach (string game in games)
            {
                List<string> parts = new List<string> { Path.Combine("DB", game) };
                parts.AddRange(CleanProfileFolders.Select(top => Path.Combine(top, game)));

                Dictionary<string, string> programStamp = ReadStampFields(Path.Combine(programRoot, "DB", game, StampFile));
                Dictionary<string, string> userStamp = ReadStampFields(Path.Combine(userRoot, "DB", game, StampFile));
                bool userHasDb = Directory.Exists(Path.Combine(userRoot, "DB", game));
                bool programNewer = userHasDb && IsNewer(programStamp, userStamp);

                foreach (string part in parts)
                {
                    string source = Path.Combine(programRoot, part);
                    string target = Path.Combine(userRoot, part);
                    if (!Directory.Exists(source))
                        continue;
                    try
                    {
                        if (!Directory.Exists(target))
                        {
                            CopyFolderAtomic(source, target);
                            report.Copied.Add(part);
                        }
                        else if (programNewer)
                        {
                            // The user's copy stays available: it is copied aside before the newer one goes over it.
                            CopyFolder(target, Path.Combine(userRoot, ReplacedFolderName, stamp, part), false);
                            CopyFolder(source, target, true);
                            report.Replaced.Add(part);
                        }
                    }
                    catch (Exception ex)
                    {
                        report.Warnings.Add("Could not copy " + part + " to the game-data folder: " + ex.Message);
                    }
                }
            }
        }

        // True when the program folder's stamp is newer than the game-data root's: a higher game version or Steam build;
        // with the same version more DCS modules (a strict superset); otherwise a later "recorded" time when other
        // fields differ. No stamp in the program folder, or none in the root (the user's own list), is never newer.
        internal static bool IsNewer(Dictionary<string, string> program, Dictionary<string, string> user)
        {
            if (program == null || user == null || program.Count == 0 || user.Count == 0)
                return false;

            foreach (string key in new[] { "version", "steam-build" })
            {
                string a, b;
                if (program.TryGetValue(key, out a) && user.TryGetValue(key, out b) && !string.Equals(a, b, StringComparison.Ordinal))
                {
                    int compared = CompareVersions(a, b);
                    if (compared != 0)
                        return compared > 0;
                }
            }

            string programModules, userModules;
            program.TryGetValue("modules", out programModules);
            user.TryGetValue("modules", out userModules);
            if (!string.Equals(programModules ?? string.Empty, userModules ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                HashSet<string> p = SplitSet(programModules);
                HashSet<string> u = SplitSet(userModules);
                return p.IsSupersetOf(u) && p.Count > u.Count;
            }

            bool otherDiffers = program.Where(e => e.Key != "recorded" && e.Key != "modules")
                                       .Any(e => { string v; return !user.TryGetValue(e.Key, out v) || !string.Equals(v, e.Value, StringComparison.Ordinal); });
            if (!otherDiffers)
                return false;
            DateTime programTime, userTime;
            return TryRecorded(program, out programTime) && TryRecorded(user, out userTime) && programTime > userTime;
        }

        // source-version.txt: "key=value" lines ('#' comments), including "recorded".
        internal static Dictionary<string, string> ReadStampFields(string path)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(path))
                    return fields;
                foreach (string line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && !line.StartsWith("#", StringComparison.Ordinal))
                        fields[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception)
            {
                fields.Clear();
            }
            return fields;
        }

        private static int CompareVersions(string a, string b)
        {
            long[] x = Regex.Matches(a ?? string.Empty, "\\d+").Cast<Match>().Select(m => ParseLong(m.Value)).ToArray();
            long[] y = Regex.Matches(b ?? string.Empty, "\\d+").Cast<Match>().Select(m => ParseLong(m.Value)).ToArray();
            if (x.Length == 0 || y.Length == 0)
                return 0;
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                long left = i < x.Length ? x[i] : 0;
                long right = i < y.Length ? y[i] : 0;
                if (left != right)
                    return left.CompareTo(right);
            }
            return 0;
        }

        private static long ParseLong(string value)
        {
            long parsed;
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static HashSet<string> SplitSet(string value)
        {
            return new HashSet<string>((value ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()), StringComparer.OrdinalIgnoreCase);
        }

        private static bool TryRecorded(Dictionary<string, string> fields, out DateTime value)
        {
            string raw;
            value = DateTime.MinValue;
            return fields.TryGetValue("recorded", out raw) &&
                   DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }

        // ---- Copying ----

        // Copies a folder into a temp folder next to the target and renames it, so a reader never sees half a copy.
        private static void CopyFolderAtomic(string source, string target)
        {
            string parent = Path.GetDirectoryName(target);
            Directory.CreateDirectory(parent);
            string temp = target + ".copy-" + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
            if (Directory.Exists(temp))
                Directory.Delete(temp, true);
            CopyFolder(source, temp, true);
            try
            {
                Directory.Move(temp, target);
            }
            catch (IOException)
            {
                // Another process was quicker: its copy is the same; only this process's own temp copy goes.
                if (!Directory.Exists(target))
                    throw;
                Directory.Delete(temp, true);
            }
        }

        private static void CopyFolder(string source, string target, bool overwrite)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                string destination = Path.Combine(target, Path.GetFileName(file));
                if (overwrite || !File.Exists(destination))
                    File.Copy(file, destination, overwrite);
            }
            foreach (string folder in Directory.GetDirectories(source))
                CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)), overwrite);
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Hash(string value)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty).Substring(0, 16);
        }
    }
}
