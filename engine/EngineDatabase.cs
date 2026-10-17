using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    internal static class EngineDatabase
    {
        private static readonly HashSet<string> ExcludedDcsCleanChecks =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "uilayer",
                "cameraobject",
                "explorations",
                "geoterrain",
                "jft binocular",
                "jft camera",
                "jft global",
                "trainingwaitforuser"
            };

        private static readonly Dictionary<string, string[]> DcsCleanAliasMap =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "A-10C_2", new[] { "A-10C II" } },
                { "F-14B-Pilot", new[] { "F-14B", "F-14" } },
                { "F-14B-RIO", new[] { "F-14_RIO", "F-14B_RIO" } },
                { "F-16C", new[] { "F-16C_50" } },
                { "F-4E-Pilot", new[] { "F-4E-45MC" } },
                { "F-4E-WSO", new[] { "F-4E-45MC_WSO" } },
                { "F-5E", new[] { "F-5E-3" } },
                { "FA-18C", new[] { "FA-18C_hornet" } },
                { "ka-50_3", new[] { "Ka-50 III", "ka-50" } },
                { "Mi-8MTV2", new[] { "mi-8mt" } },
                { "Mi-8MTV2_Gunner", new[] { "mi-8mt_Gunner" } },
                { "Mi-8MTV2_TrackIR_Gunner", new[] { "mi-8mt_TrackIR_Gunner" } },
                { "MiG-21", new[] { "MiG-21Bis" } },
                { "Mi_24P_AimingStation", new[] { "Mi-24P_AimingStation" } },
                { "Mi_24P_AI_Menu", new[] { "Mi-24P_AI_Menu" } },
                { "Mi_24P_op", new[] { "Mi-24P_operator" } },
                { "Mi_24P_pilot", new[] { "Mi-24P_pilot" } },
                { "OH-58D CPG", new[] { "OH-58D Copilot" } },
                { "OH-58D M4", new[] { "OH-58D Copilot Personal Weapon" } },
                { "OH-58D PLT", new[] { "OH-58D Pilot" } },
                { "SA342_COPILOT", new[] { "SA342 Copilot" } },
                { "SA342_GUNNER", new[] { "SA342 Gunner" } },
                { "SA342_PILOT", new[] { "SA342 Pilot" } }
            };

        public static EngineDbDocument InspectDcsCleanProfiles(string rootPath, bool includeKeyboardCheck)
        {
            EngineDbDocument doc = new EngineDbDocument();
            doc.RootPath = Normalize(rootPath);
            // The lists built from the user's DCS: the game-data folder's, else the program folder's (GameDataPaths).
            doc.GameDataRoot = GameDataPaths.UserRoot();
            doc.DbPath = GameDataPaths.DbFolder("DCS");
            doc.CleanProfilePath = GameDataPaths.CleanProfileFolder("DCS", false);
            doc.KeyboardCleanProfilePath = GameDataPaths.CleanProfileFolder("DCS", true);

            if (!Directory.Exists(doc.RootPath))
            {
                doc.Errors.Add("Root path does not exist: " + doc.RootPath);
                return doc;
            }
            if (!Directory.Exists(doc.DbPath))
            {
                doc.Errors.Add("No DCS command list yet (" + doc.DbPath + "). JoyPro builds it from your DCS installation: engine dcs refresh id --headless-lua true.");
                return doc;
            }
            if (!Directory.Exists(doc.CleanProfilePath))
            {
                doc.Errors.Add("No DCS clean profiles yet (" + doc.CleanProfilePath + "). JoyPro builds them from your DCS installation: engine dcs refresh clean --headless-lua true.");
                return doc;
            }

            doc.KeyboardCheckEnabled = includeKeyboardCheck && Directory.Exists(doc.KeyboardCleanProfilePath);
            HashSet<string> cleanNames = ReadCleanProfileNames(doc.CleanProfilePath);
            Dictionary<string, List<string>> cleanNormalizedMap = BuildNormalizedNameMap(cleanNames);
            HashSet<string> keyboardCleanNames = doc.KeyboardCheckEnabled
                ? ReadCleanProfileNames(doc.KeyboardCleanProfilePath)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<string>> keyboardNormalizedMap = doc.KeyboardCheckEnabled
                ? BuildNormalizedNameMap(keyboardCleanNames)
                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            DirectoryInfo dbDir = new DirectoryInfo(doc.DbPath);
            FileInfo[] htmlFiles = dbDir.GetFiles("*.html");
            Array.Sort(htmlFiles, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            for (int i = 0; i < htmlFiles.Length; i++)
            {
                FileInfo html = htmlFiles[i];
                string moduleName = Path.GetFileNameWithoutExtension(html.Name);
                string resolvedCleanName = ResolveDcsCleanName(moduleName, cleanNames, cleanNormalizedMap);
                string resolvedKeyboardCleanName = doc.KeyboardCheckEnabled
                    ? ResolveDcsCleanName(moduleName, keyboardCleanNames, keyboardNormalizedMap)
                    : null;
                string cleanFileName = string.IsNullOrWhiteSpace(resolvedCleanName) ? moduleName : resolvedCleanName;
                string kbCleanFileName = string.IsNullOrWhiteSpace(resolvedKeyboardCleanName) ? moduleName : resolvedKeyboardCleanName;
                string cleanFile = Path.Combine(doc.CleanProfilePath, cleanFileName + ".cf");
                string kbCleanFile = Path.Combine(doc.KeyboardCleanProfilePath, kbCleanFileName + ".cf");

                EngineDbModuleDto row = new EngineDbModuleDto();
                row.Name = moduleName;
                row.DbFile = html.FullName;
                row.CleanFile = cleanFile;
                row.KeyboardCleanFile = kbCleanFile;
                row.ExcludedFromCleanCheck = ExcludedDcsCleanChecks.Contains(moduleName);
                row.HasCleanFile = !string.IsNullOrWhiteSpace(resolvedCleanName);
                row.HasKeyboardCleanFile = doc.KeyboardCheckEnabled && !string.IsNullOrWhiteSpace(resolvedKeyboardCleanName);
                row.CleanMatchedByAlias =
                    row.HasCleanFile &&
                    !string.Equals(resolvedCleanName ?? string.Empty, moduleName, StringComparison.OrdinalIgnoreCase);
                row.KeyboardCleanMatchedByAlias =
                    row.HasKeyboardCleanFile &&
                    !string.Equals(resolvedKeyboardCleanName ?? string.Empty, moduleName, StringComparison.OrdinalIgnoreCase);

                if (!row.ExcludedFromCleanCheck && !row.HasCleanFile)
                    row.Missing.Add("clean");
                if (doc.KeyboardCheckEnabled && !row.ExcludedFromCleanCheck && !row.HasKeyboardCleanFile)
                    row.Missing.Add("keyboard_clean");

                doc.Modules.Add(row);
            }

            return doc;
        }

        public static EngineDbDocument Filter(EngineDbDocument source, string query)
        {
            if (source == null)
                return new EngineDbDocument();

            string q = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
            EngineDbDocument filtered = new EngineDbDocument();
            filtered.FormatVersion = source.FormatVersion;
            filtered.GeneratedUtc = source.GeneratedUtc;
            filtered.RootPath = source.RootPath;
            filtered.DbPath = source.DbPath;
            filtered.CleanProfilePath = source.CleanProfilePath;
            filtered.KeyboardCleanProfilePath = source.KeyboardCleanProfilePath;
            filtered.KeyboardCheckEnabled = source.KeyboardCheckEnabled;
            filtered.Errors = source.Errors == null ? new List<string>() : new List<string>(source.Errors);

            IEnumerable<EngineDbModuleDto> rows = source.Modules ?? new List<EngineDbModuleDto>();
            if (!string.IsNullOrWhiteSpace(q))
            {
                rows = rows.Where(r =>
                    ContainsIgnoreCase(r.Name, q) ||
                    ContainsIgnoreCase(r.DbFile, q) ||
                    ContainsIgnoreCase(r.CleanFile, q) ||
                    ContainsIgnoreCase(r.KeyboardCleanFile, q));
            }

            filtered.Modules = rows.ToList();
            return filtered;
        }

        public static int MissingCleanCount(EngineDbDocument doc)
        {
            if (doc == null || doc.Modules == null)
                return 0;
            return doc.Modules.Count(m => m != null && m.Missing.Contains("clean"));
        }

        public static int MissingKeyboardCleanCount(EngineDbDocument doc)
        {
            if (doc == null || doc.Modules == null)
                return 0;
            return doc.Modules.Count(m => m != null && m.Missing.Contains("keyboard_clean"));
        }

        public static int AliasMatchedCleanCount(EngineDbDocument doc)
        {
            if (doc == null || doc.Modules == null)
                return 0;
            return doc.Modules.Count(m => m != null && m.CleanMatchedByAlias);
        }

        public static int AliasMatchedKeyboardCleanCount(EngineDbDocument doc)
        {
            if (doc == null || doc.Modules == null)
                return 0;
            return doc.Modules.Count(m => m != null && m.KeyboardCleanMatchedByAlias);
        }

        public static EngineDbCatalogDocument InspectCatalog(string rootPath)
        {
            EngineDbCatalogDocument doc = new EngineDbCatalogDocument();
            doc.RootPath = Normalize(rootPath);
            doc.DbRootPath = Path.Combine(doc.RootPath, "DB");
            doc.GameDataDbPath = Path.Combine(GameDataPaths.UserRoot(), "DB");

            if (!Directory.Exists(doc.RootPath))
            {
                doc.Errors.Add("Root path does not exist: " + doc.RootPath);
                return doc;
            }
            if (!Directory.Exists(doc.DbRootPath))
            {
                doc.Errors.Add("DB folder missing: " + doc.DbRootPath);
                return doc;
            }

            // Every game folder of the game-data folder and the program folder, each read where GameDataPaths reads it.
            foreach (KeyValuePair<string, string> entry in GameDataPaths.GameDbFolders())
            {
                string path = entry.Value;
                string folder = entry.Key;
                // Only games: DB also holds data folders (Devices, Functions, Llm) that are no game's command catalog.
                if (!IsGameDbFolder(folder))
                    continue;
                string game = CanonicalGameFromDbFolder(folder);

                EngineDbGameCatalogDto row = new EngineDbGameCatalogDto();
                row.Game = game;
                row.SourceFolder = folder;
                row.DbPath = path;
                row.Planes = DiscoverPlanesForGame(game, path);
                doc.Games.Add(row);
            }

            return doc;
        }

        public static EngineDbCatalogDocument FilterCatalog(EngineDbCatalogDocument source, string gameQuery, string planeQuery)
        {
            if (source == null)
                return new EngineDbCatalogDocument();

            string gameQ = string.IsNullOrWhiteSpace(gameQuery) ? null : gameQuery.Trim();
            string planeQ = string.IsNullOrWhiteSpace(planeQuery) ? null : planeQuery.Trim();

            EngineDbCatalogDocument filtered = new EngineDbCatalogDocument();
            filtered.FormatVersion = source.FormatVersion;
            filtered.GeneratedUtc = source.GeneratedUtc;
            filtered.RootPath = source.RootPath;
            filtered.DbRootPath = source.DbRootPath;
            filtered.Errors = source.Errors == null ? new List<string>() : new List<string>(source.Errors);

            IEnumerable<EngineDbGameCatalogDto> games = source.Games ?? new List<EngineDbGameCatalogDto>();
            if (!string.IsNullOrWhiteSpace(gameQ))
            {
                games = games.Where(g =>
                    ContainsIgnoreCase(g.Game, gameQ) ||
                    ContainsIgnoreCase(g.SourceFolder, gameQ));
            }

            foreach (EngineDbGameCatalogDto game in games)
            {
                if (game == null)
                    continue;

                List<string> planes = game.Planes == null
                    ? new List<string>()
                    : new List<string>(game.Planes);

                if (!string.IsNullOrWhiteSpace(planeQ))
                {
                    planes = planes
                        .Where(p => ContainsIgnoreCase(p, planeQ))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                else
                {
                    planes = planes
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                if (!string.IsNullOrWhiteSpace(planeQ) && planes.Count == 0)
                    continue;

                EngineDbGameCatalogDto copy = new EngineDbGameCatalogDto();
                copy.Game = game.Game;
                copy.SourceFolder = game.SourceFolder;
                copy.DbPath = game.DbPath;
                copy.Planes = planes;
                filtered.Games.Add(copy);
            }

            filtered.Games = filtered.Games
                .OrderBy(g => g.Game, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return filtered;
        }

        public static string ToTextSummary(EngineDbDocument doc)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DB Check: DCS");
            sb.AppendLine("Root: " + Safe(doc.RootPath));
            sb.AppendLine("Game Data Root: " + Safe(doc.GameDataRoot));
            sb.AppendLine("DB Path: " + Safe(doc.DbPath));
            sb.AppendLine("Clean Path: " + Safe(doc.CleanProfilePath));
            sb.AppendLine("Keyboard Clean Path: " + Safe(doc.KeyboardCleanProfilePath));
            sb.AppendLine("Keyboard Check Enabled: " + doc.KeyboardCheckEnabled);
            sb.AppendLine("Modules: " + (doc.Modules == null ? 0 : doc.Modules.Count));
            sb.AppendLine("Missing Clean: " + MissingCleanCount(doc));
            sb.AppendLine("Missing Keyboard Clean: " + MissingKeyboardCleanCount(doc));
            sb.AppendLine("Alias-Matched Clean: " + AliasMatchedCleanCount(doc));
            sb.AppendLine("Alias-Matched Keyboard Clean: " + AliasMatchedKeyboardCleanCount(doc));

            if (doc.Errors != null && doc.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                for (int i = 0; i < doc.Errors.Count; i++)
                {
                    sb.AppendLine("  - " + doc.Errors[i]);
                }
            }
            return sb.ToString();
        }

        public static string ToTextList(EngineDbDocument doc, bool onlyMissing)
        {
            StringBuilder sb = new StringBuilder();
            int total = doc.Modules == null ? 0 : doc.Modules.Count;
            sb.AppendLine("DB Modules: " + total);
            if (doc.Modules == null)
                return sb.ToString();

            IEnumerable<EngineDbModuleDto> rows = doc.Modules;
            if (onlyMissing)
                rows = rows.Where(r => r != null && r.Missing != null && r.Missing.Count > 0);

            foreach (EngineDbModuleDto row in rows)
            {
                if (row == null)
                    continue;

                string clean = row.ExcludedFromCleanCheck ? "skip" : (row.HasCleanFile ? "ok" : "missing");
                string kb = doc.KeyboardCheckEnabled
                    ? (row.ExcludedFromCleanCheck ? "skip" : (row.HasKeyboardCleanFile ? "ok" : "missing"))
                    : "n/a";
                if (!row.ExcludedFromCleanCheck && row.HasCleanFile && row.CleanMatchedByAlias)
                    clean = "alias:" + Path.GetFileNameWithoutExtension(row.CleanFile ?? string.Empty);
                if (doc.KeyboardCheckEnabled && !row.ExcludedFromCleanCheck && row.HasKeyboardCleanFile && row.KeyboardCleanMatchedByAlias)
                    kb = "alias:" + Path.GetFileNameWithoutExtension(row.KeyboardCleanFile ?? string.Empty);

                sb.AppendLine(
                    row.Name +
                    " | clean=" + clean +
                    " | keyboard_clean=" + kb +
                    " | db=" + row.DbFile);
            }
            return sb.ToString();
        }

        public static string ToTextGames(EngineDbCatalogDocument doc)
        {
            StringBuilder sb = new StringBuilder();
            int totalGames = doc == null || doc.Games == null ? 0 : doc.Games.Count;
            sb.AppendLine("DB Games: " + totalGames);

            if (doc != null && doc.Games != null)
            {
                for (int i = 0; i < doc.Games.Count; i++)
                {
                    EngineDbGameCatalogDto game = doc.Games[i];
                    if (game == null)
                        continue;
                    sb.AppendLine(
                        game.Game +
                        " | planes=" + (game.Planes == null ? 0 : game.Planes.Count) +
                        " | folder=" + Safe(game.SourceFolder) +
                        " | db=" + Safe(game.DbPath));
                }
            }

            if (doc != null && doc.Errors != null && doc.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                for (int i = 0; i < doc.Errors.Count; i++)
                    sb.AppendLine("  - " + doc.Errors[i]);
            }

            return sb.ToString();
        }

        public static string ToTextPlanes(EngineDbCatalogDocument doc)
        {
            StringBuilder sb = new StringBuilder();
            int totalGames = doc == null || doc.Games == null ? 0 : doc.Games.Count;
            int totalPlanes = doc == null || doc.Games == null
                ? 0
                : doc.Games.Sum(g => g == null || g.Planes == null ? 0 : g.Planes.Count);
            sb.AppendLine("DB Planes: " + totalPlanes + " (Games: " + totalGames + ")");

            if (doc != null && doc.Games != null)
            {
                for (int i = 0; i < doc.Games.Count; i++)
                {
                    EngineDbGameCatalogDto game = doc.Games[i];
                    if (game == null)
                        continue;

                    sb.AppendLine(game.Game + " (" + (game.Planes == null ? 0 : game.Planes.Count) + ")");
                    if (game.Planes == null)
                        continue;

                    for (int p = 0; p < game.Planes.Count; p++)
                        sb.AppendLine("  - " + game.Planes[p]);
                }
            }

            if (doc != null && doc.Errors != null && doc.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                for (int i = 0; i < doc.Errors.Count; i++)
                    sb.AppendLine("  - " + doc.Errors[i]);
            }

            return sb.ToString();
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Environment.CurrentDirectory;
            return Path.GetFullPath(path);
        }

        private static HashSet<string> ReadCleanProfileNames(string path)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return output;

            string[] files = Directory.GetFiles(path, "*.cf", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileNameWithoutExtension(files[i]);
                if (!string.IsNullOrWhiteSpace(name))
                    output.Add(name.Trim());
            }
            return output;
        }

        private static Dictionary<string, List<string>> BuildNormalizedNameMap(IEnumerable<string> names)
        {
            Dictionary<string, List<string>> map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (names == null)
                return map;

            foreach (string raw in names)
            {
                string name = (raw ?? string.Empty).Trim();
                if (name.Length == 0)
                    continue;

                string key = NormalizeDcsNameForMatching(name);
                if (key.Length == 0)
                    continue;

                if (!map.TryGetValue(key, out List<string> bucket))
                {
                    bucket = new List<string>();
                    map[key] = bucket;
                }
                if (!bucket.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase)))
                    bucket.Add(name);
            }
            return map;
        }

        private static string ResolveDcsCleanName(
            string moduleName,
            HashSet<string> knownNames,
            Dictionary<string, List<string>> normalizedMap)
        {
            if (knownNames == null)
                return null;

            string name = (moduleName ?? string.Empty).Trim();
            if (name.Length == 0)
                return null;

            if (knownNames.Contains(name))
                return ResolveKnownNameCase(knownNames, name);

            List<string> aliases = BuildDcsCleanAliasCandidates(name);
            for (int i = 0; i < aliases.Count; i++)
            {
                if (knownNames.Contains(aliases[i]))
                    return ResolveKnownNameCase(knownNames, aliases[i]);
            }

            if (normalizedMap == null || normalizedMap.Count == 0)
                return null;

            string moduleKey = NormalizeDcsNameForMatching(name);
            if (moduleKey.Length > 0 && normalizedMap.TryGetValue(moduleKey, out List<string> directBucket) && directBucket.Count > 0)
                return directBucket[0];

            for (int i = 0; i < aliases.Count; i++)
            {
                string aliasKey = NormalizeDcsNameForMatching(aliases[i]);
                if (aliasKey.Length == 0)
                    continue;
                if (normalizedMap.TryGetValue(aliasKey, out List<string> bucket) && bucket.Count > 0)
                    return bucket[0];
            }

            return null;
        }

        private static string ResolveKnownNameCase(HashSet<string> knownNames, string candidate)
        {
            if (knownNames == null)
                return candidate;

            foreach (string existing in knownNames)
            {
                if (string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase))
                    return existing;
            }

            return candidate;
        }

        private static List<string> BuildDcsCleanAliasCandidates(string moduleName)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> output = new List<string>();
            AddAlias(output, seen, moduleName);

            string name = (moduleName ?? string.Empty).Trim();
            if (name.Length == 0)
                return output;

            AddAlias(output, seen, name.Replace('_', ' '));
            AddAlias(output, seen, name.Replace('_', '-'));
            AddAlias(output, seen, name.Replace('-', '_'));

            if (DcsCleanAliasMap.TryGetValue(name, out string[] aliases))
            {
                for (int i = 0; i < aliases.Length; i++)
                    AddAlias(output, seen, aliases[i]);
            }

            return output;
        }

        private static void AddAlias(List<string> output, HashSet<string> seen, string value)
        {
            string alias = (value ?? string.Empty).Trim();
            if (alias.Length == 0)
                return;
            if (seen.Add(alias))
                output.Add(alias);
        }

        private static string NormalizeDcsNameForMatching(string value)
        {
            string raw = (value ?? string.Empty).Trim();
            if (raw.Length == 0)
                return string.Empty;

            string lowered = raw.ToLowerInvariant()
                .Replace('_', ' ')
                .Replace('-', ' ')
                .Replace('/', ' ')
                .Replace('.', ' ')
                .Replace("(", " ")
                .Replace(")", " ");

            string[] parts = lowered.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                string token = RomanToArabicToken(parts[i]);
                for (int c = 0; c < token.Length; c++)
                {
                    char ch = token[c];
                    if (char.IsLetterOrDigit(ch))
                        sb.Append(ch);
                }
            }
            return sb.ToString();
        }

        private static string RomanToArabicToken(string token)
        {
            string value = (token ?? string.Empty).Trim().ToLowerInvariant();
            switch (value)
            {
                case "ii": return "2";
                case "iii": return "3";
                case "iv": return "4";
                case "v": return "5";
                case "vi": return "6";
                case "vii": return "7";
                case "viii": return "8";
                case "ix": return "9";
                default: return value;
            }
        }

        // A DB folder that holds a game's commands: DCS, IL-2 GB, Star Citizen or a registry game's DbFolder.
        private static bool IsGameDbFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return false;
            if (folder.Equals("DCS", StringComparison.OrdinalIgnoreCase) || folder.Equals("IL2", StringComparison.OrdinalIgnoreCase) ||
                folder.Equals("IL2Game", StringComparison.OrdinalIgnoreCase) || folder.Equals("StarCitizen", StringComparison.OrdinalIgnoreCase))
                return true;
            return Games.GameRegistry.All.Any(g => string.Equals(g.DbFolder, folder, StringComparison.OrdinalIgnoreCase));
        }

        private static string CanonicalGameFromDbFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return string.Empty;
            if (folder.Equals("DCS", StringComparison.OrdinalIgnoreCase))
                return "DCS";
            if (folder.Equals("IL2", StringComparison.OrdinalIgnoreCase) ||
                folder.Equals("IL2Game", StringComparison.OrdinalIgnoreCase))
                return "IL2Game";
            if (folder.Equals("StarCitizen", StringComparison.OrdinalIgnoreCase))
                return "StarCitizen";
            return folder;
        }

        private static List<string> DiscoverPlanesForGame(string canonicalGame, string gameFolderPath)
        {
            HashSet<string> planes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(gameFolderPath) || !Directory.Exists(gameFolderPath))
                return planes.ToList();

            if (canonicalGame.Equals("DCS", StringComparison.OrdinalIgnoreCase))
            {
                string[] htmlFiles = Directory.GetFiles(gameFolderPath, "*.html", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < htmlFiles.Length; i++)
                {
                    string name = Path.GetFileNameWithoutExtension(htmlFiles[i]);
                    if (!string.IsNullOrWhiteSpace(name))
                        planes.Add(name.Trim());
                }
            }
            else if (canonicalGame.Equals("IL2Game", StringComparison.OrdinalIgnoreCase))
            {
                string actionsFile = Path.Combine(gameFolderPath, "IL2.actions");
                string mapFile = Path.Combine(gameFolderPath, "IL2.map");
                if (File.Exists(actionsFile) || File.Exists(mapFile))
                    planes.Add("IL2Game");
            }
            else if (canonicalGame.Equals("StarCitizen", StringComparison.OrdinalIgnoreCase))
            {
                string[] xmlFiles = Directory.GetFiles(gameFolderPath, "*.xml", SearchOption.TopDirectoryOnly);
                if (xmlFiles.Length > 0)
                    planes.Add("StarCitizen");
            }
            else if (File.Exists(Path.Combine(gameFolderPath, Games.MsfsAircraftCatalog.FileName)))
            {
                // A game with an aircraft list (MSFS, written by its refresh-db): the game name stands for all aircraft,
                // the keys of aircraft.tsv for one aircraft each (relation nodes use them as plane).
                planes.Add(canonicalGame);
                foreach (Games.MsfsAircraft aircraft in Games.MsfsAircraftCatalog.Load(Path.Combine(gameFolderPath, Games.MsfsAircraftCatalog.FileName)))
                    planes.Add(aircraft.Key);
            }
            else
            {
                string[] files = Directory.GetFiles(gameFolderPath, "*", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < files.Length; i++)
                {
                    string name = Path.GetFileNameWithoutExtension(files[i]);
                    if (!string.IsNullOrWhiteSpace(name))
                        planes.Add(name.Trim());
                }
            }

            return planes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool ContainsIgnoreCase(string text, string query)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(query))
                return false;
            return text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<none>" : value;
        }
    }
}
