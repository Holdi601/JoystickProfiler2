using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace JoyPro.Engine.Games.FreeSpaceOpen
{
    // Where FreeSpace Open mods and their players live. Knossos.NET (and the older Knossos) install a mod as
    // <library>\<mod id>\<mod id>-<version>\ with a mod.json; the library is kn_portable\Library next to Knossos.NET.exe
    // in portable mode (a "kn_portable" folder beside the exe; any drive), else the "base_path" of
    // %APPDATA%\KnossosNET\settings.json (old Knossos: %APPDATA%\knossos\settings.json). The game's user data (pilots,
    // presets, the mod's settings) is kn_portable\HardLightProductions\FreeSpaceOpen in portable mode (Knossos sets
    // FSO_PREFERENCES_PATH), else SDL's pref path %APPDATA%\HardLightProductions\FreeSpaceOpen (osapi.cpp).
    internal static class FsoInstall
    {
        public const string PortableFolder = "kn_portable";
        private static readonly object Gate = new object();
        private static List<string> portableCache;

        public static string DefaultUserData()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HardLightProductions", "FreeSpaceOpen");
        }

        // The user data folder of a mod folder: kn_portable\HardLightProductions\FreeSpaceOpen when the mod lies in a
        // portable Knossos library, else the pref path.
        public static string UserDataFor(string modFolder)
        {
            string portable = PortableRootOf(modFolder);
            if (portable != null)
                return Path.Combine(portable, "HardLightProductions", "FreeSpaceOpen");
            return DefaultUserData();
        }

        private static string PortableRootOf(string path)
        {
            try
            {
                DirectoryInfo dir = string.IsNullOrWhiteSpace(path) ? null : new DirectoryInfo(path);
                for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
                {
                    if (string.Equals(dir.Name, PortableFolder, StringComparison.OrdinalIgnoreCase))
                        return dir.FullName;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        // Every mod folder <library>\<modId>\<modId>-<version> (or <library>\<modId>-<version>) of every Knossos library
        // found, the newest version first.
        public static List<string> ModFolders(string modId, List<string> warnings)
        {
            List<string> folders = new List<string>();
            foreach (string library in Libraries(warnings))
                folders.AddRange(ModFoldersIn(library, modId));
            return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<string> ModFoldersIn(string library, string modId)
        {
            List<string> found = new List<string>();
            try
            {
                foreach (string parent in new[] { Path.Combine(library, modId), library })
                {
                    if (!Directory.Exists(parent))
                        continue;
                    foreach (string dir in Directory.GetDirectories(parent, modId + "-*"))
                    {
                        if (File.Exists(Path.Combine(dir, "mod.json")) && string.Equals(ModIdOf(dir), modId, StringComparison.OrdinalIgnoreCase))
                            found.Add(dir);
                    }
                }
            }
            catch (Exception)
            {
            }
            return found.OrderByDescending(f => VersionKey(VersionOf(f)), StringComparer.Ordinal).ToList();
        }

        private static string VersionKey(string version)
        {
            return string.Join(".", Regex.Split(version ?? string.Empty, @"[^0-9]+").Where(p => p.Length > 0).Take(4).Select(p => p.PadLeft(6, '0')));
        }

        public static string VersionOf(string modFolder)
        {
            string version = JsonString(Path.Combine(modFolder ?? string.Empty, "mod.json"), "version");
            if (!string.IsNullOrEmpty(version))
                return version;
            string name = Path.GetFileName((modFolder ?? string.Empty).TrimEnd('\\', '/'));
            int dash = name.LastIndexOf('-');
            return dash > 0 ? name.Substring(dash + 1) : string.Empty;
        }

        public static string ModIdOf(string modFolder)
        {
            return JsonString(Path.Combine(modFolder ?? string.Empty, "mod.json"), "id");
        }

        // The mod's package folders in mod.json order (Knossos' -mod list), the enabled ones.
        public static List<string> PackageFolders(string modFolder)
        {
            List<string> folders = new List<string>();
            Dictionary<string, object> json = ReadJson(Path.Combine(modFolder ?? string.Empty, "mod.json"));
            object packages;
            if (json == null || !json.TryGetValue("packages", out packages) || !(packages is object[]))
                return folders;
            foreach (Dictionary<string, object> package in ((object[])packages).OfType<Dictionary<string, object>>())
            {
                object enabled;
                if (package.TryGetValue("isEnabled", out enabled) && enabled is bool && !(bool)enabled)
                    continue;
                object folder;
                string name = package.TryGetValue("folder", out folder) && folder != null ? Convert.ToString(folder, CultureInfo.InvariantCulture) : string.Empty;
                if (name.Length > 0)
                    folders.Add(Path.Combine(modFolder, name));
            }
            return folders;
        }

        // The mod's settings file: data\<mod folder name without the Knossos version>_settings.ini (osregistry.cpp strips
        // "-<semantic version>" and what follows it).
        public static string SettingsIniName(string modFolder)
        {
            string name = Path.GetFileName((modFolder ?? string.Empty).TrimEnd('\\', '/'));
            string[] parts = name.Split('-');
            int keep = parts.Length;
            for (int i = 1; i < parts.Length; i++)
            {
                if (Regex.IsMatch(parts[i], @"^\d+(\.\d+){2,}$"))
                {
                    keep = i;
                    break;
                }
            }
            string root = string.Join("-", parts.Take(keep));
            return root.Length == 0 ? "fs2_open.ini" : root + "_settings.ini";
        }

        // A path the user picked for the mod (the mod folder, its library or mod id folder, kn_portable, the Knossos folder
        // or Knossos.NET.exe) -> the newest mod folder of that id found there; null when none.
        public static string ResolveModFolder(string path, string modId)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            string folder;
            try
            {
                folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
                if (File.Exists(folder))
                    folder = Path.GetDirectoryName(folder) ?? folder;
            }
            catch (Exception)
            {
                return null;
            }
            if (!Directory.Exists(folder))
                return null;
            if (File.Exists(Path.Combine(folder, "mod.json")) && string.Equals(ModIdOf(folder), modId, StringComparison.OrdinalIgnoreCase))
                return folder;
            List<string> libraries = new List<string> { folder, Path.Combine(folder, "Library"), Path.Combine(folder, PortableFolder, "Library") };
            DirectoryInfo parent = Directory.GetParent(folder);
            if (parent != null)
                libraries.Add(parent.FullName);
            foreach (string library in libraries)
            {
                string found = ModFoldersIn(library, modId).FirstOrDefault();
                if (found != null)
                    return found;
            }
            return null;
        }

        // Knossos libraries: portable kn_portable\Library folders and the base_path of the installed launchers.
        public static List<string> Libraries(List<string> warnings)
        {
            List<string> libraries = new List<string>();
            foreach (string portable in PortableRoots())
            {
                libraries.Add(Path.Combine(portable, "Library"));
                string basePath = JsonString(Path.Combine(portable, "KnossosNET", "settings.json"), "base_path");
                if (!string.IsNullOrEmpty(basePath))
                    libraries.Add(basePath);
            }
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (string settings in new[] { Path.Combine(appData, "KnossosNET", "settings.json"), Path.Combine(appData, "knossos", "settings.json") })
            {
                string basePath = JsonString(settings, "base_path");
                if (!string.IsNullOrEmpty(basePath))
                    libraries.Add(basePath);
            }
            return libraries.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // kn_portable folders on the fixed drives (the drive root and two folder levels below it) and in the user's own
        // folders (Desktop, Downloads, Documents, Games); found once per run.
        public static List<string> PortableRoots()
        {
            lock (Gate)
            {
                // A long-running engine (the app's engine server) forgets its list when the saved file is gone (Look again).
                string savedPath = PortableRootsPath();
                if (portableCache != null && savedPath != null && !File.Exists(savedPath))
                    portableCache = null;
                if (portableCache != null)
                    return portableCache;
                // The folders found last time (saved in JoyPro's data folder) while they all still exist and the search is
                // less than a week old: no walk over every drive on each start (cold disks, sleeping hard drives).
                List<string> saved = ReadSavedPortableRoots();
                if (saved != null)
                {
                    portableCache = saved;
                    return portableCache;
                }
                List<string> roots = new List<string>();
                List<string> starts = new List<string>();
                starts.AddRange(GameInstalls.FixedDriveRoots());
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (string own in GameInstalls.TestDrives() != null ? new string[0] : new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                               Path.Combine(profile, "Downloads"), Path.Combine(profile, "Games"), Path.Combine(profile, "Saved Games") })
                {
                    if (!string.IsNullOrEmpty(own))
                        starts.Add(own);
                }
                foreach (string start in starts.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
                    Scan(start, 0, roots);
                portableCache = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                SavePortableRoots(portableCache);
                return portableCache;
            }
        }

        private static readonly TimeSpan PortableSearchAge = TimeSpan.FromDays(7);
        private const string PortableRootsFile = "fso-portable-roots.txt";

        private static string PortableRootsPath()
        {
            if (GameInstalls.TestDrives() != null)
                return null;
            try
            {
                return JoyPro.Logging.JoyProDataFolder.Combine(PortableRootsFile);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // null = search again (no file, older than a week, or a saved folder is gone).
        private static List<string> ReadSavedPortableRoots()
        {
            string path = PortableRootsPath();
            try
            {
                if (path == null || !File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > PortableSearchAge)
                    return null;
                List<string> roots = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                return roots.All(Directory.Exists) ? roots : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void SavePortableRoots(List<string> roots)
        {
            string path = PortableRootsPath();
            if (path == null)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, roots);
            }
            catch (Exception)
            {
            }
        }

        // "Look again" on the games: the next look walks the drives again.
        public static void ForgetPortableRoots()
        {
            lock (Gate)
            {
                portableCache = null;
                string path = PortableRootsPath();
                try
                {
                    if (path != null && File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception)
                {
                }
            }
        }

        private static readonly HashSet<string> SkipFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "$Recycle.Bin", "System Volume Information", "ProgramData", "Recovery", "PerfLogs", "$WinREAgent", "AppData", "node_modules", ".git"
        };

        private static void Scan(string folder, int depth, List<string> found)
        {
            string[] children;
            try
            {
                children = Directory.GetDirectories(folder);
            }
            catch (Exception)
            {
                return;
            }
            foreach (string child in children)
            {
                string name = Path.GetFileName(child);
                if (string.Equals(name, PortableFolder, StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(Path.Combine(child, "Library")) || Directory.Exists(Path.Combine(child, "HardLightProductions")) || Directory.Exists(Path.Combine(child, "KnossosNET")))
                        found.Add(child);
                    continue;
                }
                if (depth < 2 && !SkipFolders.Contains(name) && !name.StartsWith(".", StringComparison.Ordinal))
                    Scan(child, depth + 1, found);
            }
        }

        // The user data folders FSO uses with these mod folders (portable ones next to the library, then the pref path).
        public static List<string> UserDataFolders(IEnumerable<string> modFolders)
        {
            List<string> folders = (modFolders ?? Enumerable.Empty<string>()).Select(UserDataFor).ToList();
            folders.AddRange(PortableRoots().Select(p => Path.Combine(p, "HardLightProductions", "FreeSpaceOpen")));
            folders.Add(DefaultUserData());
            return folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The pilots of a user data folder (data\players\*.json with the PLR_ signature), the last player
        // (fs2_open.ini [Default] LastPlayer) first.
        public static List<string> Pilots(string userData)
        {
            string players = Path.Combine(userData ?? string.Empty, "data", "players");
            if (!Directory.Exists(players))
                return new List<string>();
            List<string> pilots = new List<string>();
            foreach (string file in Directory.GetFiles(players, "*.json"))
            {
                if (FsoPilot.Read(file, null) != null)
                    pilots.Add(file);
            }
            string last = FsoIni.Load(Path.Combine(userData, "fs2_open.ini")).Get("Default", "LastPlayer") ?? string.Empty;
            return pilots.OrderBy(p => string.Equals(Path.GetFileNameWithoutExtension(p), last, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                         .ThenByDescending(File.GetLastWriteTimeUtc).ToList();
        }

        // The user data folder of a path inside it: <user data>\data\players\<pilot>.json, ...\players\presets\<preset>.json,
        // ...\data\<mod>_settings.ini or the folder itself (it has fs2_open.ini or data\players).
        public static string UserDataOf(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return string.Empty;
                DirectoryInfo dir = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path).Directory;
                for (int i = 0; dir != null && i < 4; i++, dir = dir.Parent)
                {
                    if (string.Equals(dir.Name, "players", StringComparison.OrdinalIgnoreCase) && dir.Parent != null &&
                        string.Equals(dir.Parent.Name, "data", StringComparison.OrdinalIgnoreCase) && dir.Parent.Parent != null)
                        return dir.Parent.Parent.FullName;
                    if (File.Exists(Path.Combine(dir.FullName, "fs2_open.ini")) || Directory.Exists(Path.Combine(dir.FullName, "data", "players")))
                        return dir.FullName;
                }
            }
            catch (Exception)
            {
            }
            return string.Empty;
        }

        private static Dictionary<string, object> ReadJson(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string JsonString(string path, string key)
        {
            Dictionary<string, object> json = ReadJson(path);
            object value;
            return json != null && json.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty;
        }
    }
}
