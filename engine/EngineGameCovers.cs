using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace JoyPro.Engine
{
    // Game covers from the web (docs/export.md#pictures, docs/privacy.md): JoyPro ships no artwork of a game. When Steam
    // has no cover of a game on this PC, the cover is downloaded once on the user's PC from the address below and kept in
    // <data>\Media\Covers\<GameId>.<ext> (with <GameId>.source: kind and address). Each address was checked with an HTTP
    // GET (image/*, 200) on 2026-10-11; SourcePage is the page it was taken from.
    //   Steam games: Steam's public store CDN by app id (library capsule, then main capsule, then header; the names come
    //                from IStoreBrowseService/GetItems, some carry a hash folder).
    //   Others:      the publisher's own site (RSI for Star Citizen, falcon-bms.com, prepar3d.com by Lockheed Martin).
    // Rules: https only (http only to 127.0.0.1 for the tests), 10 s timeout, at most MaxBytes, Content-Type image/*,
    // and the file must start like a JPEG, PNG or WebP. Off with --no-internet-pictures or JOYPRO_NO_INTERNET_PICTURES=1
    // (the GUI's "Manufacturer photos" setting and the tests): then neither a download nor a downloaded file is used.
    // JOYPRO_COVER_CATALOG=<json file> replaces the list (tests: the loopback fake server); format:
    //   { "<GameId>": [ { "Url": "...", "Kind": "cover|logo", "SourcePage": "..." } ] }
    internal static class EngineGameCovers
    {
        public const string NoInternetVariable = "JOYPRO_NO_INTERNET_PICTURES";
        public const string CatalogVariable = "JOYPRO_COVER_CATALOG";
        internal const long MaxBytes = 5L * 1024 * 1024;
        private const int TimeoutMs = 10000;
        // After a game's download failed (offline, address gone) it is tried again a day later, not on every start.
        private static readonly TimeSpan RetryAfter = TimeSpan.FromDays(1);

        private const string SteamCdn = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/";
        private const string Rsi = "https://robertsspaceindustries.com/en/download";
        private const string StarCitizenSite = "https://starcitizen.com/en/";
        private const string FalconBms = "https://www.falcon-bms.com/";
        private const string Prepar3D = "https://www.prepar3d.com/";

        private static readonly Dictionary<string, List<EngineCoverSource>> BuiltIn = Build();

        private static Dictionary<string, List<EngineCoverSource>> Build()
        {
            Dictionary<string, List<EngineCoverSource>> map = new Dictionary<string, List<EngineCoverSource>>(StringComparer.OrdinalIgnoreCase);
            // GameId, Steam app id, library capsule (portrait), main capsule, header: paths below steam/apps/<id>/.
            Steam(map, "DCS", 223750, "library_600x900.jpg", "capsule_616x353.jpg", "header.jpg");
            Steam(map, "IL2Game", 307960, "library_600x900.jpg", "capsule_616x353.jpg", "header.jpg");
            Steam(map, "IL2Korea", 247970, "7b507812eebffc51c1d4a98dbcd4eff17b2dc876/library_capsule.jpg", "bbca62ba800619714e19a99487d021c75e9ad6c7/capsule_616x353.jpg", "c0d86dfb1e69d6165de8556949746d46e1d28f96/header.jpg");
            Steam(map, "MSFS2024", 2537590, "360b0a83bcf30de3cfc5c59efa280e3b998b41a8/library_600x900.jpg", "8fa176d72210f43c71d50f4c8ffca70a4f171e62/capsule_616x353.jpg", "header.jpg");
            Steam(map, "MSFS2020", 1250410, "library_600x900.jpg", "capsule_616x353.jpg", "header.jpg");
            Steam(map, "XPlane12", 2014780, "library_600x900.jpg", "capsule_616x353.jpg", "header.jpg");
            Steam(map, "XPlane11", 269950, "library_600x900.jpg", "capsule_616x353.jpg", "header.jpg");
            Steam(map, "WarThunder", 236390, "44a7800c8a2e665325739b15d6be13ff4fcd68d6/library_capsule.jpg", "34080813e94ef5a2cefc45fd57c3c09df4f490b1/capsule_616x353.jpg", "38881c549e623fe432af4e7b4bc3952b443906f5/header.jpg");
            Steam(map, "Arma3", 107410, "library_600x900.jpg", "capsule_616x353.jpg", "header.jpg");
            Steam(map, "Arma2", 33900, "library_600x900.jpg", "capsule_616x353.jpg", "header.jpg");
            Steam(map, "Arma2OA", 33930, "library_600x900.jpg", "capsule_616x353.jpg", "header.jpg");
            Steam(map, "ArmaReforger", 1874880, "library_600x900.jpg", "capsule_616x353.jpg", "header.jpg");
            Steam(map, "Wardogs", 1867240, "31dd6ce5538a3064749d769527656f9002a382b0/library_capsule.jpg", "24c0b6c0621560e922fbed18545921e7eaa9036e/capsule_616x353.jpg", "59d4daf753bd5d982e6675f7eee363bc817c574e/header.jpg");
            Steam(map, "HLLVietnam", 3079210, "5f414404a79b668999c85ec6654ed1b749354156/library_capsule.jpg", "aeedc94bb7c21f9a4a4e9bb14beca090b36c7db1/capsule_616x353.jpg", "2e52f99e70e827b57a2205469dc5d529e8e0490a/header.jpg");
            Steam(map, "Battlefield6", 2807960, "289b1c193f9730a0d4ea4dbf912219e46cd1a8a3/library_capsule.jpg", "92359619c2aa832e3423cebb27a5f921b1a30a67/capsule_616x353_alt_assets_23.jpg", "c12d12ce3c7d217398d3fcad77427bfc9d57c570/header.jpg");
            Steam(map, "Battlefield2042", 1517290, "e68a8598b5c60d8188968f2da0da941f4834d5b2/library_600x900.jpg", "capsule_616x353.jpg", "header.jpg");
            Steam(map, "AceCombat8", 2288340, "a1fb5685d2c140309d5549af1ddc725a7114d889/library_capsule.jpg", "3bd993fa9f94ebd60c1b05dd61a4315cde829830/capsule_616x353.jpg", "13dea8084135fd130605b78189c82e39bf944c35/header.jpg");
            Steam(map, "AceCombat7", 502500, "library_600x900.jpg", "capsule_616x353.jpg", "header.jpg");
            Steam(map, "ProjectWingman", 895870, "library_600x900.jpg", "capsule_616x353.jpg", "header.jpg");
            Steam(map, "X4", 392160, "library_600x900.jpg", "220ae723b1adb9cd560ffce765ba447151e9f51e/capsule_616x353.jpg", "header.jpg");
            Steam(map, "EliteDangerous", 359320, "library_600x900.jpg", "capsule_616x353.jpg", "header.jpg");
            Steam(map, "TinyCombatArena", 1347550, "library_600x900.jpg", "capsule_616x353.jpg", "header.jpg");
            Steam(map, "GRebels", 2445980, "library_600x900.jpg", "header.jpg");
            Steam(map, "VTOLVR", 667970, "library_600x900.jpg", "ed3d0e7991c6e91203a3b707942ca91439ed0eba/capsule_616x353.jpg", "header.jpg");
            Steam(map, "VTOLVRNative", 667970, "library_600x900.jpg", "ed3d0e7991c6e91203a3b707942ca91439ed0eba/capsule_616x353.jpg", "header.jpg");
            Steam(map, "NuclearOption", 2168680, "103d520427ae2f08d5f42c06cba2f3a9b24e6491/library_capsule.jpg", "4c2faf4a19d3805fe2abf70df795522fc72af539/capsule_616x353.jpg", "c56ac51c1670abb8e7f8d0eef35000d75c9738d8/header.jpg");
            Steam(map, "IL21946", 15320, "674a73f4514f6bfe44b030ef63b20804d829b582/library_capsule.jpg", "9722f41db3aadf18db7afbb234bb6b20f8a8b38c/capsule_616x353.jpg", "d2b2eabc7b9612dad560e60d009f7f0dd7c44fc1/header.jpg");
            Steam(map, "IL2CoD", 63950, "cbf41a0f528d0b8d2cb749354c4d8000f27f77c2/library_capsule.jpg", "a0f3079902522089d59bf7f04784f40060a7c4ad/capsule_616x353.jpg", "19a13e4d890d4aada22835b41cc1baac3350df2a/header.jpg");
            Steam(map, "IL2CoDBlitz", 754530, "cbf41a0f528d0b8d2cb749354c4d8000f27f77c2/library_capsule.jpg", "a0f3079902522089d59bf7f04784f40060a7c4ad/capsule_616x353.jpg", "19a13e4d890d4aada22835b41cc1baac3350df2a/header.jpg");
            Steam(map, "FSXSE", 314160, "library_600x900.jpg", "capsule_616x353.jpg", "header.jpg");
            // FSX (boxed, 2006) is the same simulator: the Steam Edition's art.
            Steam(map, "FSX", 314160, "library_600x900.jpg", "capsule_616x353.jpg", "header.jpg");

            // Star Citizen (not on Steam): the key art of RSI's download page (og:image; logo centred, so a portrait crop
            // keeps it), else the Star Citizen logo of starcitizen.com (og:image).
            map["StarCitizen"] = new List<EngineCoverSource>
            {
                new EngineCoverSource("https://robertsspaceindustries.com/i/dd4cd4be8a7a39cb700a20c6d1b550e5a792e1a5/resize(2048,1024,cover,ADdPNihJzmPbNuTnFsH1DqUeqBRpXdSXVVtgJTyDDgscGKrzJuoFjReske2p7FuQNaraRGpW1yMWE1mFbfQKahnk6)/source.jpg", "cover", Rsi),
                new EngineCoverSource("https://cdn.robertsspaceindustries.com/static/images/thumbnail/starcitizen.png", "logo", StarCitizenSite)
            };
            // Falcon BMS (not on Steam): the BMS app icon of falcon-bms.com (web app manifest, 512 px), then its og:image.
            map["FalconBMS"] = new List<EngineCoverSource>
            {
                new EngineCoverSource("https://www.falcon-bms.com/images/webapp/web-app-manifest-512x512.png", "logo", FalconBms),
                new EngineCoverSource("https://www.falcon-bms.com/images/webapp/web_logo_icon.png", "logo", FalconBms)
            };
            // Prepar3D (Lockheed Martin, not on Steam): the product marks of each version on prepar3d.com (store tiles).
            map["P3D4"] = new List<EngineCoverSource> { new EngineCoverSource("https://www.prepar3d.com/wp-content/uploads/2023/04/v4_pro.png", "logo", Prepar3D) };
            map["P3D5"] = new List<EngineCoverSource> { new EngineCoverSource("https://www.prepar3d.com/wp-content/uploads/2023/04/v5_pro.png", "logo", Prepar3D) };
            map["P3D6"] = new List<EngineCoverSource>
            {
                new EngineCoverSource("https://www.prepar3d.com/wp-content/uploads/2023/04/P3D_v6_Pro.png", "logo", Prepar3D),
                new EngineCoverSource("https://www.prepar3d.com/wp-content/uploads/2023/03/P3D_Logo_v6.png", "logo", Prepar3D)
            };
            return map;
        }

        private static void Steam(Dictionary<string, List<EngineCoverSource>> map, string gameId, int app, params string[] files)
        {
            string id = app.ToString(CultureInfo.InvariantCulture);
            string page = "https://store.steampowered.com/app/" + id + "/";
            map[gameId] = files.Select(f => new EngineCoverSource(SteamCdn + id + "/" + f, "cover", page)).ToList();
        }

        // The sources of every game (the built-in list, or JOYPRO_COVER_CATALOG).
        public static Dictionary<string, List<EngineCoverSource>> Catalog()
        {
            string file = Environment.GetEnvironmentVariable(CatalogVariable);
            if (string.IsNullOrWhiteSpace(file))
                return BuiltIn;
            Dictionary<string, List<EngineCoverSource>> map = new Dictionary<string, List<EngineCoverSource>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Dictionary<string, List<EngineCoverSource>> read = EngineSerialization.FromJson<Dictionary<string, List<EngineCoverSource>>>(File.ReadAllText(file, Encoding.UTF8));
                foreach (KeyValuePair<string, List<EngineCoverSource>> pair in read ?? new Dictionary<string, List<EngineCoverSource>>())
                    map[pair.Key] = (pair.Value ?? new List<EngineCoverSource>()).Where(s => s != null && !string.IsNullOrWhiteSpace(s.Url)).ToList();
            }
            catch (Exception)
            {
                // An unreadable test catalog: no sources (never the real web addresses).
            }
            return map;
        }

        public static List<EngineCoverSource> SourcesOf(string gameId)
        {
            List<EngineCoverSource> sources;
            return Catalog().TryGetValue(gameId ?? string.Empty, out sources) ? sources : new List<EngineCoverSource>();
        }

        public static bool InternetBlocked(bool flag)
        {
            string value = Environment.GetEnvironmentVariable(NoInternetVariable);
            return flag || (!string.IsNullOrWhiteSpace(value) && value.Trim() != "0" && !string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase));
        }

        public static string Folder()
        {
            return Path.Combine(GameInstalls.DataRoot(), "Media", "Covers");
        }

        // The downloaded cover of a game (the file and its kind), downloading it when there is none. Null when there is
        // no source, the download failed or it was refused (warnings say why).
        public static EngineCoverFile Get(string gameId, List<string> warnings)
        {
            List<EngineCoverSource> sources = SourcesOf(gameId);
            EngineCoverFile cached = Cached(gameId);
            // A file from an address the list no longer has is replaced when the new download works.
            if (cached != null && (sources.Count == 0 || sources.Any(s => string.Equals(s.Url, cached.Url, StringComparison.Ordinal))))
                return cached;
            if (sources.Count == 0)
                return cached;
            string miss = Path.Combine(Folder(), gameId + ".miss");
            if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < RetryAfter)
                return cached;
            List<string> reasons = new List<string>();
            foreach (EngineCoverSource source in sources)
            {
                string reason;
                EngineCoverFile file = Download(gameId, source, out reason);
                if (file != null)
                {
                    TryDelete(miss);
                    return file;
                }
                reasons.Add(source.Url + ": " + reason);
            }
            try
            {
                Directory.CreateDirectory(Folder());
                File.WriteAllText(miss, string.Join(Environment.NewLine, reasons), new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
            warnings?.Add("No cover for " + gameId + " from the web: " + string.Join(" | ", reasons));
            return cached;
        }

        // <GameId>.jpg|png|webp with <GameId>.source ("<kind>\n<url>").
        public static EngineCoverFile Cached(string gameId)
        {
            string folder = Folder();
            try
            {
                if (!Directory.Exists(folder))
                    return null;
                string picture = new[] { ".jpg", ".png", ".webp" }.Select(e => Path.Combine(folder, gameId + e)).FirstOrDefault(File.Exists);
                if (picture == null)
                    return null;
                string[] lines = File.Exists(Path.Combine(folder, gameId + ".source")) ? File.ReadAllLines(Path.Combine(folder, gameId + ".source")) : new string[0];
                return new EngineCoverFile
                {
                    Path = picture,
                    Kind = lines.Length > 0 && lines[0].Trim() == "logo" ? "logo" : "cover",
                    Url = lines.Length > 1 ? lines[1].Trim() : string.Empty
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static EngineCoverFile Download(string gameId, EngineCoverSource source, out string reason)
        {
            Uri uri;
            if (!Uri.TryCreate(source.Url, UriKind.Absolute, out uri) || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            {
                reason = "not an https address";
                return null;
            }
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
                request.UserAgent = "JoyPro2";
                request.Accept = "image/*";
                request.Timeout = TimeoutMs;
                request.ReadWriteTimeout = TimeoutMs;
                request.AllowAutoRedirect = true;
                request.MaximumAutomaticRedirections = 3;
                if (uri.IsLoopback)
                    request.Proxy = null;
                byte[] data;
                string type;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    type = (response.ContentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
                    if (!type.StartsWith("image/", StringComparison.Ordinal))
                    {
                        reason = "not a picture (" + (type.Length == 0 ? "no type" : type) + ")";
                        return null;
                    }
                    if (response.ContentLength > MaxBytes)
                    {
                        reason = "too big (" + response.ContentLength.ToString(CultureInfo.InvariantCulture) + " bytes)";
                        return null;
                    }
                    using (Stream input = response.GetResponseStream())
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        byte[] chunk = new byte[81920];
                        int read;
                        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
                        {
                            buffer.Write(chunk, 0, read);
                            if (buffer.Length > MaxBytes)
                            {
                                reason = "too big (more than " + MaxBytes.ToString(CultureInfo.InvariantCulture) + " bytes)";
                                return null;
                            }
                        }
                        data = buffer.ToArray();
                    }
                }
                string extension = PictureExtension(data);
                if (extension.Length == 0)
                {
                    reason = "not a JPEG, PNG or WebP picture";
                    return null;
                }
                string folder = Folder();
                Directory.CreateDirectory(folder);
                foreach (string old in new[] { ".jpg", ".png", ".webp" })
                    TryDelete(Path.Combine(folder, gameId + old));
                string target = Path.Combine(folder, gameId + extension);
                string part = target + ".part";
                File.WriteAllBytes(part, data);
                File.Move(part, target);
                string kind = source.Kind == "logo" ? "logo" : "cover";
                File.WriteAllText(Path.Combine(folder, gameId + ".source"), kind + "\n" + source.Url + "\n", new UTF8Encoding(false));
                reason = string.Empty;
                return new EngineCoverFile { Path = target, Kind = kind, Url = source.Url };
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                reason = response != null ? "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) : ex.Status.ToString();
                return null;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return null;
            }
        }

        // JPEG (FF D8 FF), PNG (89 50 4E 47) or WebP (RIFF....WEBP): the extension, else empty.
        internal static string PictureExtension(byte[] data)
        {
            if (data == null || data.Length < 12)
                return string.Empty;
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return ".jpg";
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
                return ".png";
            if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
                return ".webp";
            return string.Empty;
        }

        private static void TryDelete(string file)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (Exception)
            {
            }
        }

        public static string SourcesText(IEnumerable<string> gameIds)
        {
            StringBuilder sb = new StringBuilder();
            Dictionary<string, List<EngineCoverSource>> catalog = Catalog();
            foreach (string id in gameIds)
            {
                List<EngineCoverSource> sources;
                if (!catalog.TryGetValue(id, out sources) || sources.Count == 0)
                {
                    sb.AppendLine("  - " + id + " | no web source");
                    continue;
                }
                foreach (EngineCoverSource source in sources)
                    sb.AppendLine("  - " + id + " | " + source.Kind + " | " + source.Url + " | from " + source.SourcePage);
            }
            return sb.ToString();
        }
    }

    public sealed class EngineCoverSource
    {
        public string Url { get; set; }
        public string Kind { get; set; }
        public string SourcePage { get; set; }

        public EngineCoverSource()
        {
            Url = SourcePage = string.Empty;
            Kind = "cover";
        }

        public EngineCoverSource(string url, string kind, string sourcePage)
        {
            Url = url;
            Kind = kind;
            SourcePage = sourcePage;
        }
    }

    public sealed class EngineCoverFile
    {
        public string Path { get; set; }
        public string Kind { get; set; }
        public string Url { get; set; }
    }
}
