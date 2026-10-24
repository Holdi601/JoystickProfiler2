using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine.Games
{
    internal static class GameRegistry
    {
        private static readonly List<IGameIntegration> Integrations = new List<IGameIntegration>
        {
            new WardogsIntegration(),
            new Arma3Integration(),
            new Arma2OaIntegration(),
            new Arma2Integration(),
            new ArmaReforgerIntegration(),
            new WarThunderIntegration(),
            new XPlaneIntegration(12),
            new XPlaneIntegration(11),
            new MsfsIntegration(2024),
            new MsfsIntegration(2020),
            new FalconBmsIntegration(),
            new Il2KoreaIntegration(),
            new X4Integration(),
            new EliteDangerousIntegration(),
            new VtolVrIntegration(),
            new VtolVrNativeIntegration(),
            new NuclearOptionIntegration(),
            new TinyCombatArenaIntegration(),
            new GRebelsIntegration(),
            new HllVietnamIntegration(),
            new BattlefieldIntegration(6),
            new BattlefieldIntegration(2042),
            new AceCombat8Integration(),
            new AceCombat7Integration(),
            new ProjectWingmanIntegration(),
            new Il21946Integration(),
            new Il2CodIntegration(true),
            new Il2CodIntegration(false),
            new FsxIntegration(FsxIntegration.Sim.Fsx),
            new FsxIntegration(FsxIntegration.Sim.FsxSe),
            new FsxIntegration(FsxIntegration.Sim.P3D4),
            new FsxIntegration(FsxIntegration.Sim.P3D5),
            new FsxIntegration(FsxIntegration.Sim.P3D6)
        };

        public static IReadOnlyList<IGameIntegration> All
        {
            get { return Integrations; }
        }

        // The answer per name, kept: the list of integrations never changes, and a profile's nodes ask for the same few
        // game names again and again (engine vjoy status asked ~700000 times for 527 relations, 8 s).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IGameIntegration> ResolveCache =
            new System.Collections.Concurrent.ConcurrentDictionary<string, IGameIntegration>(StringComparer.Ordinal);

        public static bool TryResolve(string nameOrAlias, out IGameIntegration game)
        {
            string key = Normalize(nameOrAlias);
            if (key.Length == 0)
            {
                game = null;
                return false;
            }
            game = ResolveCache.GetOrAdd(key, k => Integrations.FirstOrDefault(g =>
                Normalize(g.GameId) == k ||
                Normalize(g.DisplayName) == k ||
                g.Aliases.Any(a => Normalize(a) == k)));
            return game != null;
        }

        // A node of a game that was split into versions (MSFS, XPlane; LegacyGameIds) belongs to every version until
        // the profile is loaded again (the load migrates it into one node per version).
        public static bool GameMatches(IGameIntegration game, string nodeGame)
        {
            if (game != null && LegacyGameIds.IsLegacy(nodeGame))
                return LegacyGameIds.Expand(nodeGame).Contains(game.GameId, StringComparer.OrdinalIgnoreCase);
            IGameIntegration resolved;
            return TryResolve(nodeGame, out resolved) && ReferenceEquals(resolved, game);
        }

        private static string Normalize(string value)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in (value ?? string.Empty))
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }

    // DB\<Folder>\actions.tsv: "id<TAB>title<TAB>axis|button<TAB>category[<TAB>native id[<TAB>aircraft1|aircraft2]]", '#'
    // starts a comment line.
    internal static class GameCatalogStore
    {
        public const string FileName = "actions.tsv";

        // The catalog to read (game-data folder first, GameDataPaths); Save writes only into the game-data folder.
        public static string CatalogPath(IGameIntegration game)
        {
            return GameDataPaths.DbFile(game.DbFolder, FileName);
        }

        public static GameCatalog Load(IGameIntegration game, List<string> warnings)
        {
            GameCatalog catalog = new GameCatalog();
            catalog.GameId = game.GameId;
            catalog.SourcePath = CatalogPath(game);
            if (!File.Exists(catalog.SourcePath))
            {
                IGameBuiltInCatalog builtIn = game as IGameBuiltInCatalog;
                if (builtIn != null)
                {
                    GameCatalog known = builtIn.BuiltInCatalog();
                    known.GameId = game.GameId;
                    known.SourcePath = catalog.SourcePath;
                    return known;
                }
                warnings?.Add(game.DisplayName + " action catalog not found: " + catalog.SourcePath);
                return catalog;
            }

            foreach (string raw in File.ReadAllLines(catalog.SourcePath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith("#", StringComparison.Ordinal))
                    continue;

                string[] parts = raw.Split('\t');
                if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[0]))
                    continue;

                catalog.Add(new GameAction
                {
                    Id = parts[0].Trim(),
                    Title = parts[1].Trim(),
                    IsAxis = string.Equals(parts[2].Trim(), "axis", StringComparison.OrdinalIgnoreCase),
                    Category = parts.Length > 3 ? parts[3].Trim() : string.Empty,
                    NativeId = parts.Length > 4 ? parts[4].Trim() : string.Empty,
                    Aircraft = parts.Length > 5
                        ? parts[5].Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()).Where(a => a.Length > 0).ToList()
                        : new List<string>()
                });
            }

            IGameCatalogTidy tidy = game as IGameCatalogTidy;
            if (tidy != null)
                tidy.TidyCatalog(catalog);
            // Titles that look like ids ("FMS: key_0", "HeliBrakes", "TOGGLE PUSHBACK") read as words.
            foreach (GameAction action in catalog.Actions)
            {
                action.Title = EngineReadableNames.Title(game.GameId, action.Title);
                // A bare key of a pad ("0" ... "9" of VTOL VR's number pad) names its group too: "Number pad 0".
                if (action.Title.Length > 0 && action.Title.All(char.IsDigit) && (action.Category ?? string.Empty).Trim().Length > 0)
                    action.Title = EngineReadableNames.Category(game.GameId, action.Category) + " " + action.Title;
            }
            return catalog;
        }

        public static void Save(IGameIntegration game, GameCatalog catalog, string header)
        {
            string path = GameDataPaths.WritableDbFile(game.DbFolder, FileName);
            StringBuilder sb = new StringBuilder();
            foreach (string line in (header ?? string.Empty).Split('\n'))
            {
                if (line.Trim().Length > 0)
                    sb.Append("# ").Append(line.Trim()).Append("\r\n");
            }
            foreach (GameAction action in catalog.Actions)
            {
                sb.Append(action.Id).Append('\t')
                  .Append(action.Title).Append('\t')
                  .Append(action.IsAxis ? "axis" : "button").Append('\t')
                  .Append(action.Category);
                bool hasAircraft = action.Aircraft != null && action.Aircraft.Count > 0;
                if (!string.IsNullOrEmpty(action.NativeId) || hasAircraft)
                    sb.Append('\t').Append(action.NativeId ?? string.Empty);
                if (hasAircraft)
                    sb.Append('\t').Append(string.Join("|", action.Aircraft));
                sb.Append("\r\n");
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }

    internal static class GameProcessGuard
    {
        // Set only by the smoke test, which writes temp copies and must not fail while the developer plays.
        public const string IgnoreRunningGamesVariable = "JOYPRO_TEST_IGNORE_RUNNING_GAMES";

        public static List<string> FindRunning(IGameIntegration game)
        {
            IGameProcessFilter filter = game as IGameProcessFilter;
            if (filter == null)
                return FindRunning(game.ProcessNames);
            List<string> running = new List<string>();
            if (Environment.GetEnvironmentVariable(IgnoreRunningGamesVariable) == "1")
                return running;
            foreach (string name in game.ProcessNames)
            {
                string processName = Path.GetFileNameWithoutExtension(name);
                Process[] processes = Process.GetProcessesByName(processName);
                // A generic name (Cliffs of Dover's "Launcher", also Falcon BMS's launcher): only the game's own exe counts.
                // An exe path that cannot be read counts as the game (refusing an export is the safe side).
                if (processes.Any(p => { string image = ImagePath(p); return image.Length == 0 || filter.IsGameProcess(image); }))
                    running.Add(processName);
                foreach (Process process in processes)
                    process.Dispose();
            }
            return running;
        }

        // The full exe path of a process (also of an elevated one: limited query rights are enough); empty when unknown.
        public static string ImagePath(Process process)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(0x1000, false, process.Id);
                if (handle == IntPtr.Zero)
                    return string.Empty;
                StringBuilder text = new StringBuilder(1024);
                int size = text.Capacity;
                return QueryFullProcessImageName(handle, 0, text, ref size) ? text.ToString(0, size) : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                    CloseHandle(handle);
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder text, ref int size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        public static List<string> FindRunning(IEnumerable<string> processNames)
        {
            List<string> running = new List<string>();
            if (Environment.GetEnvironmentVariable(IgnoreRunningGamesVariable) == "1")
                return running;
            foreach (string name in processNames)
            {
                string processName = Path.GetFileNameWithoutExtension(name);
                Process[] processes = Process.GetProcessesByName(processName);
                if (processes.Length > 0)
                    running.Add(processName);
                foreach (Process process in processes)
                    process.Dispose();
            }
            return running;
        }
    }

    // Where a game keeps its controls: a file or folder, or a registry key (Unity PlayerPrefs, e.g. Nuclear Option).
    internal static class GameConfigLocation
    {
        public static bool IsRegistryKey(string path)
        {
            string value = (path ?? string.Empty).Trim();
            return value.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) || value.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase);
        }

        // A file JoyPro keeps next to the game's file ("<file><suffix>"); for a registry key it goes into JoyPro's data
        // folder (GameMetadata\<key>.<suffix>).
        public static string SidecarPath(string configPath, string suffix)
        {
            if (!IsRegistryKey(configPath))
                return configPath + suffix;
            string name = new string(configPath.Trim().Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
            return Path.Combine(GameInstalls.DataRoot(), "GameMetadata", name + suffix);
        }
    }

    internal static class GameText
    {
        public static string FormatFloat(double value, int decimals)
        {
            return value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }
    }
}
