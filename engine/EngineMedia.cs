using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    // Official pictures for the GUI: game covers and DCS aircraft pictures. Nothing is scraped:
    //   games    - the cover Steam keeps on this PC for a game of the user's library (appcache\librarycache\<app id>,
    //              also in its hash sub folders and the old flat <app id>_library_600x900.jpg), else the cover
    //              downloaded once from the game's fixed web source (EngineGameCovers: Steam's store CDN, or the
    //              publisher's site; kept in <data>\Media\Covers), else the icon of the game's own executable (Star
    //              Citizen, Falcon BMS, DCS standalone). Games without any get a badge drawn by the GUI;
    //   aircraft - the icon and main menu picture every DCS module ships in its own folder (Skins\1\icon.png,
    //              Skins\1\ME\BackGround-*.png), found through make_flyable(...) in the module's entry.lua.
    internal static class EngineMedia
    {
        // Steam's pictures of a game, best first: the portrait covers (the GUI's tiles are portrait), then the wide ones.
        private static readonly string[] SteamCoverNames = { "library_600x900.jpg", "library_capsule.jpg", "library_header.jpg", "header.jpg" };

        // JoyPro game id -> Steam app ids (first = preferred cover).
        private static readonly Dictionary<string, int[]> SteamApps = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "DCS", new[] { 223750 } },
            { "IL2Game", new[] { 307960 } },
            { "IL2Korea", new[] { 247970 } },
            { "MSFS2024", new[] { 2537590 } },
            { "MSFS2020", new[] { 1250410 } },
            { "XPlane12", new[] { 2014780 } },
            { "XPlane11", new[] { 269950 } },
            { "WarThunder", new[] { 236390 } },
            { "Arma3", new[] { 107410 } },
            { "ArmaReforger", new[] { 1874880 } },
            { "Arma2", new[] { 33900, 33910 } },
            { "Arma2OA", new[] { 33930 } },
            { "Wardogs", new[] { 1867240 } },
            { "X4", new[] { 392160 } },
            { "EliteDangerous", new[] { 359320 } },
            { "VTOLVR", new[] { 667970 } },
            { "VTOLVRNative", new[] { 667970 } },
            { "NuclearOption", new[] { 2168680 } },
            { "TinyCombatArena", new[] { 1347550 } },
            { "GRebels", new[] { 2445980 } },
            { "IL21946", new[] { 15320 } },
            { "IL2CoD", new[] { 63950 } },
            { "IL2CoDBlitz", new[] { 754530 } },
            { "FSXSE", new[] { 314160 } }
        };

        // The games JoyPro knows (DCS, IL-2 GB, Star Citizen and the game registry).
        public static List<EngineMediaGame> KnownGames()
        {
            List<EngineMediaGame> games = new List<EngineMediaGame>
            {
                new EngineMediaGame { GameId = "DCS", Name = "DCS World" },
                new EngineMediaGame { GameId = "IL2Game", Name = "IL-2 Great Battles" },
                new EngineMediaGame { GameId = "StarCitizen", Name = "Star Citizen" }
            };
            games.AddRange(GameRegistry.All.Select(g => new EngineMediaGame { GameId = g.GameId, Name = g.DisplayName }));
            return games;
        }

        // Per game, the first that works: Steam's cover on this PC, the cover downloaded before, a download from the
        // game's web source (EngineGameCovers; not with noInternet), the icon of the game's executable. Games without
        // any get JoyPro's badge in the GUI.
        public static List<EngineMediaGame> Games(List<string> warnings, bool noInternet = false)
        {
            List<EngineMediaGame> games = KnownGames();
            List<string> caches = SteamLibraryCaches(warnings);
            List<EngineMediaGame> needWeb = new List<EngineMediaGame>();
            foreach (EngineMediaGame game in games)
            {
                // Games without an entry here use the Steam ids of their install detection.
                int[] apps;
                if (!SteamApps.TryGetValue(game.GameId, out apps))
                    apps = GameInstalls.SteamAppIds(game.GameId);
                if (apps.Length > 0)
                {
                    game.SteamAppIds = apps.ToList();
                    string local = SteamCachedCover(caches, apps);
                    if (local.Length > 0)
                    {
                        game.Cover = local;
                        game.CoverSource = "steam-cache";
                        game.CoverKind = "cover";
                        continue;
                    }
                }
                needWeb.Add(game);
            }
            if (!EngineGameCovers.InternetBlocked(noInternet) && needWeb.Count > 0)
            {
                ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 4);
                List<string>[] gameWarnings = needWeb.Select(_ => new List<string>()).ToArray();
                Parallel.For(0, needWeb.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
                {
                    EngineCoverFile file = EngineGameCovers.Get(needWeb[i].GameId, gameWarnings[i]);
                    if (file != null)
                    {
                        needWeb[i].Cover = file.Path;
                        needWeb[i].CoverSource = "download";
                        needWeb[i].CoverKind = file.Kind;
                        needWeb[i].CoverUrl = file.Url;
                    }
                });
                foreach (List<string> list in gameWarnings)
                    warnings?.AddRange(list);
            }
            foreach (EngineMediaGame game in needWeb.Where(g => g.Cover.Length == 0))
            {
                string exe = GameExecutable(game.GameId, warnings);
                if (exe.Length > 0)
                {
                    string icon = ExecutableIcon(game.GameId, exe, warnings);
                    if (icon.Length > 0)
                    {
                        game.Cover = icon;
                        game.CoverSource = "exe-icon";
                        game.CoverKind = "logo";
                    }
                }
            }
            return games;
        }

        public static List<EngineMediaAircraft> Aircraft(List<string> warnings)
        {
            List<EngineMediaAircraft> aircraft = new List<EngineMediaAircraft>();
            string install = GamePathDiscovery.DiscoverDcsInstallPaths(warnings).FirstOrDefault(Directory.Exists) ?? string.Empty;
            if (install.Length == 0)
                return aircraft;
            Dictionary<string, EngineMediaAircraft> byPlane = new Dictionary<string, EngineMediaAircraft>(StringComparer.OrdinalIgnoreCase);
            // Per module: its pictures and its Input sub folders (for names entry.lua builds from variables).
            List<KeyValuePair<string, EngineMediaAircraft>> modules = new List<KeyValuePair<string, EngineMediaAircraft>>();
            foreach (string root in new[] { Path.Combine(install, "Mods", "aircraft"), Path.Combine(install, "CoreMods", "aircraft") }.Where(Directory.Exists))
            {
                foreach (string module in Directory.GetDirectories(root))
                {
                    string entry = Path.Combine(module, "entry.lua");
                    if (!File.Exists(entry))
                        continue;
                    string text;
                    try
                    {
                        text = File.ReadAllText(entry);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    // The module's picture folder: Skins = {{ name = ..., dir = "Skins/1" }} in entry.lua (also "Theme", "Themes", "Skins").
                    List<string> skinDirs = new List<string>();
                    Match skins = Regex.Match(text, @"Skins\s*=\s*\{\s*\{[^}]*?dir\s*=\s*['""]([^'""]+)['""]", RegexOptions.Singleline);
                    if (skins.Success)
                        skinDirs.Add(Path.Combine(module, skins.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar)));
                    skinDirs.AddRange(new[] { "Skins\\1", "Skins", "Theme", "Themes", "theme" }.Select(d => Path.Combine(module, d)));
                    string skinDir = skinDirs.FirstOrDefault(d => File.Exists(Path.Combine(d, "icon.png"))) ?? skinDirs.FirstOrDefault(Directory.Exists) ?? string.Empty;
                    string icon = skinDir.Length == 0 ? string.Empty : FirstExisting(Path.Combine(skinDir, "icon.png"), Path.Combine(skinDir, "icon 76x76.png"));
                    string displayName = Regex.Match(text, @"displayName\s*=\s*_\(\s*['""]([^'""]+)['""]").Groups[1].Value;
                    string moduleBackground = Directory.Exists(Path.Combine(skinDir, "ME")) ? Directory.GetFiles(Path.Combine(skinDir, "ME"), "BackGround*.png").FirstOrDefault() ?? string.Empty : string.Empty;
                    modules.Add(new KeyValuePair<string, EngineMediaAircraft>(module, new EngineMediaAircraft { Module = Path.GetFileName(module), DisplayName = displayName, Icon = icon, Background = moduleBackground }));
                    // The names JoyPro's DCS database uses are the InputProfiles keys (["F-15E_WSO"] = current_mod_path ..),
                    // the flyable unit types (make_flyable('F-15ESE', ...)) come second.
                    List<string> planes = new List<string>();
                    Match profiles = Regex.Match(text, @"InputProfiles\s*=\s*\{(.*?)\}", RegexOptions.Singleline);
                    if (profiles.Success)
                        planes.AddRange(Regex.Matches(profiles.Groups[1].Value, @"\[\s*['""]([^'""]+)['""]\s*\]\s*=").Cast<Match>().Select(m => m.Groups[1].Value));
                    planes.AddRange(Regex.Matches(text, @"make_flyable\(\s*['""]([^'""]+)['""]").Cast<Match>().Select(m => m.Groups[1].Value));
                    foreach (string plane in planes.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (byPlane.ContainsKey(plane))
                            continue;
                        string me = Path.Combine(skinDir, "ME");
                        string background = skinDir.Length > 0 && Directory.Exists(me)
                            ? Directory.GetFiles(me, "BackGround*.png").OrderBy(f => f.IndexOf(plane, StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).FirstOrDefault() ?? string.Empty
                            : string.Empty;
                        byPlane[plane] = new EngineMediaAircraft
                        {
                            Plane = plane,
                            Module = Path.GetFileName(module),
                            DisplayName = displayName,
                            Icon = icon,
                            Background = background
                        };
                    }
                }
            }
            // Game-mode and seat variants without their own make_flyable ("F-16C_50_easy") use their base aircraft.
            string dcsDb = GameDataPaths.DbFolder("DCS");
            if (Directory.Exists(dcsDb))
            {
                foreach (string plane in Directory.GetFiles(dcsDb, "*.html").Select(Path.GetFileNameWithoutExtension))
                {
                    if (byPlane.ContainsKey(plane))
                        continue;
                    // 1. a module with Input\<plane>; 2. a known aircraft the name starts with ("F-16C_50_easy");
                    // 3. a module folder the name starts with ("AH-64D_BLK_II_PLT" -> AH-64D).
                    EngineMediaAircraft source = modules.Where(m => Directory.Exists(Path.Combine(m.Key, "Input", plane))).Select(m => m.Value).FirstOrDefault();
                    if (source == null)
                    {
                        string basis = byPlane.Keys.Where(k => plane.StartsWith(k, StringComparison.OrdinalIgnoreCase)).OrderByDescending(k => k.Length).FirstOrDefault();
                        source = basis != null ? byPlane[basis] : null;
                    }
                    if (source == null)
                    {
                        source = modules.Where(m => plane.StartsWith(Path.GetFileName(m.Key), StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(m => Path.GetFileName(m.Key).Length).Select(m => m.Value).FirstOrDefault();
                    }
                    if (source == null)
                        continue;
                    byPlane[plane] = new EngineMediaAircraft { Plane = plane, Module = source.Module, DisplayName = source.DisplayName, Icon = source.Icon, Background = source.Background };
                }
            }
            aircraft.AddRange(byPlane.Values.OrderBy(a => a.Plane, StringComparer.OrdinalIgnoreCase));
            return aircraft;
        }

        // The best picture Steam keeps for one of the app ids: <cache>\<id>\<name> or <cache>\<id>\<hash>\<name> (newer
        // Steam), or the old flat <cache>\<id>_<name>.
        internal static string SteamCachedCover(IEnumerable<string> caches, IEnumerable<int> apps)
        {
            List<string> roots = caches.ToList();
            foreach (string name in SteamCoverNames)
            {
                foreach (int app in apps)
                {
                    string id = app.ToString(CultureInfo.InvariantCulture);
                    foreach (string cache in roots)
                    {
                        string folder = Path.Combine(cache, id);
                        try
                        {
                            if (Directory.Exists(folder))
                            {
                                string direct = Path.Combine(folder, name);
                                if (File.Exists(direct))
                                    return direct;
                                string nested = Directory.GetDirectories(folder).Select(d => Path.Combine(d, name)).Where(File.Exists)
                                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                                if (nested != null)
                                    return nested;
                            }
                            string flat = Path.Combine(cache, id + "_" + name);
                            if (File.Exists(flat))
                                return flat;
                        }
                        catch (Exception)
                        {
                            // An unreadable cache folder: try the next one.
                        }
                    }
                }
            }
            return string.Empty;
        }

        private static string FirstExisting(params string[] paths)
        {
            return paths.FirstOrDefault(File.Exists) ?? string.Empty;
        }

        private static List<string> SteamLibraryCaches(List<string> warnings)
        {
            return GamePathDiscovery.GetSteamInstallRoots(warnings)
                .Select(r => Path.Combine(r, "appcache", "librarycache"))
                .Where(Directory.Exists)
                .ToList();
        }

        private static string GameExecutable(string gameId, List<string> warnings)
        {
            try
            {
                if (string.Equals(gameId, "StarCitizen", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string root in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roberts Space Industries") })
                    {
                        string[] candidates =
                        {
                            Path.Combine(root, "StarCitizen", "LIVE", "Bin64", "StarCitizen.exe"),
                            Path.Combine(root, "RSI Launcher", "RSI Launcher.exe")
                        };
                        string found = candidates.FirstOrDefault(File.Exists);
                        if (found != null)
                            return found;
                    }
                }
                if (string.Equals(gameId, "DCS", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string root in GamePathDiscovery.DiscoverDcsInstallPaths(warnings))
                    {
                        string found = new[] { Path.Combine(root, "bin", "DCS.exe"), Path.Combine(root, "bin-mt", "DCS.exe") }.FirstOrDefault(File.Exists);
                        if (found != null)
                            return found;
                    }
                }
                if (string.Equals(gameId, "FalconBMS", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string root in FalconBmsIntegration.DiscoverInstallRoots())
                    {
                        string found = new[] { Path.Combine(root, "Launcher.exe"), Path.Combine(root, "Bin", "x64", "Falcon BMS.exe") }.FirstOrDefault(File.Exists);
                        if (found != null)
                            return found;
                    }
                }
            }
            catch (Exception ex)
            {
                warnings?.Add("Could not look for the " + gameId + " executable: " + ex.Message);
            }
            return string.Empty;
        }

        // The executable's icon at up to 256 px through the Windows shell, cached as PNG in the JoyPro data folder.
        private static string ExecutableIcon(string gameId, string exe, List<string> warnings)
        {
            string target = Path.Combine(global::JoyPro.Logging.JoyProDataFolder.Root(), "Media", gameId + ".png");
            if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(exe))
                return target;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (Bitmap bitmap = ShellIcon.Get(exe, 256))
                {
                    if (bitmap == null)
                        return string.Empty;
                    bitmap.Save(target, ImageFormat.Png);
                }
                return target;
            }
            catch (Exception ex)
            {
                warnings?.Add("Could not read the icon of " + exe + ": " + ex.Message);
                return string.Empty;
            }
        }

        public static string ToText(List<EngineMediaGame> games, List<EngineMediaAircraft> aircraft)
        {
            StringBuilder sb = new StringBuilder();
            if (games != null)
            {
                sb.AppendLine("Games: " + games.Count.ToString(CultureInfo.InvariantCulture));
                foreach (EngineMediaGame game in games)
                    sb.AppendLine("  - " + game.GameId + " | " + game.Name + " | " + (game.Cover.Length > 0 ? game.CoverSource + " " + game.CoverKind + " " + game.Cover : "no picture"));
            }
            if (aircraft != null)
            {
                sb.AppendLine("Aircraft: " + aircraft.Count.ToString(CultureInfo.InvariantCulture));
                foreach (EngineMediaAircraft plane in aircraft)
                    sb.AppendLine("  - " + plane.Plane + " | " + plane.Module + " | " + (plane.Icon.Length > 0 ? plane.Icon : "no icon") + (plane.Background.Length > 0 ? " | " + plane.Background : string.Empty));
            }
            return sb.ToString();
        }

        private static class ShellIcon
        {
            [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IShellItemImageFactory
            {
                void GetImage([In] Size size, [In] int flags, [Out] out IntPtr bitmap);
            }

            [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
            private static extern void SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindContext,
                [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

            [DllImport("gdi32.dll")]
            private static extern bool DeleteObject(IntPtr handle);

            private const int IconOnly = 0x4;

            public static Bitmap Get(string path, int size)
            {
                IShellItemImageFactory factory;
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
                IntPtr handle;
                factory.GetImage(new Size(size, size), IconOnly, out handle);
                try
                {
                    using (Bitmap source = Image.FromHbitmap(handle))
                        return WithAlpha(source);
                }
                finally
                {
                    DeleteObject(handle);
                }
            }

            // Image.FromHbitmap drops the alpha channel of 32-bit icons: copy the bits as ARGB.
            private static Bitmap WithAlpha(Bitmap source)
            {
                Rectangle rect = new Rectangle(0, 0, source.Width, source.Height);
                BitmapData data = source.LockBits(rect, ImageLockMode.ReadOnly, source.PixelFormat);
                try
                {
                    using (Bitmap view = new Bitmap(data.Width, data.Height, data.Stride, PixelFormat.Format32bppArgb, data.Scan0))
                        return new Bitmap(view);
                }
                finally
                {
                    source.UnlockBits(data);
                }
            }
        }
    }

    public sealed class EngineMediaGame
    {
        public string GameId { get; set; }
        public string Name { get; set; }
        public string Cover { get; set; }
        // steam-cache, download or exe-icon.
        public string CoverSource { get; set; }
        // cover (a picture that fills the tile) or logo (a mark drawn on JoyPro's badge); empty without a picture.
        public string CoverKind { get; set; }
        // The web address of a downloaded cover.
        public string CoverUrl { get; set; }
        public List<int> SteamAppIds { get; set; }

        public EngineMediaGame()
        {
            GameId = Name = Cover = CoverSource = CoverKind = CoverUrl = string.Empty;
            SteamAppIds = new List<int>();
        }
    }

    public sealed class EngineMediaAircraft
    {
        public string Plane { get; set; }
        public string Module { get; set; }
        public string DisplayName { get; set; }
        public string Icon { get; set; }
        public string Background { get; set; }

        public EngineMediaAircraft()
        {
            Plane = Module = DisplayName = Icon = Background = string.Empty;
        }
    }
}
