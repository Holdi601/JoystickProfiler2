using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Games
{
    // MSFS 2024 keeps the user's presets on its servers; there is no local file to edit. It also loads device presets
    // from packages (the way Asobo ships the presets of known sticks, package asobo-input-profiles-pc): plain XML files
    // under InputProfiles\Categories\<Airplane|Helicopter|...|Transversal>\, root <DefaulftInput Primary="1">, matched
    // to a device by <Device ProductID="hex" CompositeID HWVer>. JoyPro writes its presets as such a package into the
    // sim's Community folder ("joypro-input-profiles"): after the export the sim starts with them, nothing to click.
    // See docs/games/msfs.md.
    internal static class MsfsCommunityPackage
    {
        public const string PackageName = "joypro-input-profiles";
        private const string AirplaneFolder = "InputProfiles\\Categories\\Airplane";
        private static readonly Regex InstalledPackagesPath = new Regex("^\\s*InstalledPackagesPath\\s+\"(?<path>[^\"]+)\"", RegexOptions.Multiline | RegexOptions.Compiled);

        // UserCfg.opt of MSFS 2024 (Steam, then Microsoft Store).
        private static IEnumerable<string> UserCfgCandidates()
        {
            string roaming = MsfsUserProfiles.RoamingAppData();
            string local = MsfsUserProfiles.LocalAppData();
            yield return Path.Combine(roaming, "Microsoft Flight Simulator 2024", "UserCfg.opt");
            yield return Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt");
        }

        public static string FindCommunityFolder()
        {
            foreach (string cfg in UserCfgCandidates())
            {
                try
                {
                    if (!File.Exists(cfg))
                        continue;
                    Match m = InstalledPackagesPath.Match(File.ReadAllText(cfg));
                    if (m.Success)
                    {
                        // MSFS 2024 loads packages built for it from Community2024 ("communityfolderfs24"); in
                        // Community they count as MSFS 2020 packages.
                        string fs24 = Path.Combine(m.Groups["path"].Value, "Community2024");
                        return Directory.Exists(fs24) ? fs24 : Path.Combine(m.Groups["path"].Value, "Community");
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            return null;
        }

        // The JoyPro package folder for a config path: the package itself, or a Community folder (the package in it).
        public static bool TryGetPackageFolder(string configPath, out string packageFolder)
        {
            packageFolder = null;
            if (string.IsNullOrWhiteSpace(configPath))
                return false;
            string full = Path.GetFullPath(configPath).TrimEnd('\\', '/');
            if (string.Equals(Path.GetFileName(full), PackageName, StringComparison.OrdinalIgnoreCase))
            {
                packageFolder = full;
                return true;
            }
            string leaf = Path.GetFileName(full);
            if (string.Equals(leaf, "Community", StringComparison.OrdinalIgnoreCase) || string.Equals(leaf, "Community2024", StringComparison.OrdinalIgnoreCase))
            {
                packageFolder = Path.Combine(full, PackageName);
                return true;
            }
            return false;
        }

        public static List<string> PresetFiles(string packageFolder)
        {
            string root = Path.Combine(packageFolder, "InputProfiles", "Categories");
            if (!Directory.Exists(root))
                return new List<string>();
            return Directory.GetFiles(root, "*.xml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // A preset file for every device that has none in the package yet (airplane category). The sim matches a
        // preset by the product ID it sees for the device: the one it noted in profile_00 (vJoy: 0), else the USB PID.
        public static void EnsureDevicePresets(string packageFolder, IEnumerable<GameDevice> devices, GameIoResult result)
        {
            Dictionary<Guid, int> simIds = SimProductIds();
            HashSet<Guid> present = new HashSet<Guid>();
            foreach (string file in PresetFiles(packageFolder))
            {
                MsfsProfileFile profile;
                if (MsfsProfileFile.TryLoad(file, new List<string>(), out profile) && profile.HasDeviceGuid)
                    present.Add(profile.DeviceGuid);
            }

            foreach (GameDevice device in devices)
            {
                if (device == null || device.InstanceGuid == Guid.Empty || present.Contains(device.InstanceGuid))
                    continue;
                int productId;
                if (!simIds.TryGetValue(device.InstanceGuid, out productId))
                    productId = device.ProductId;
                if (productId < 0)
                {
                    result.Warnings.Add("Device '" + device.JoyProName + "' is not connected (no USB product ID): MSFS 2024 cannot match a preset to it; connect it and export again.");
                    continue;
                }
                string name = string.IsNullOrWhiteSpace(device.InstanceName) ? device.JoyProName : device.InstanceName.Trim();
                string file = Path.Combine(packageFolder, AirplaneFolder, "JoyPro_" + SafeFileName(name) + "_" + device.InstanceGuid.ToString("N").Substring(0, 8) + "_2024_Planes.xml");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, Skeleton(name, device.InstanceGuid, productId), new UTF8Encoding(true));
                present.Add(device.InstanceGuid);
                result.Steps.Add("Created the MSFS 2024 preset of '" + device.JoyProName + "': " + file);
            }
        }

        // Read-only: the product IDs MSFS 2024 noted per device instance GUID in profile_00 of every Steam user
        // (binary tree: 8-byte name hash, uint32 size incl. header, payload; a device record holds the instance GUID
        // string, the type (1 = joystick) and the product ID). JoyPro never writes this file: it carries a checksum,
        // and a file the sim rejects resets the user's sim profile.
        private static readonly byte[] HashGuid = { 0xE4, 0x94, 0xA0, 0x78, 0x27, 0x80, 0x95, 0x46 };
        private static readonly byte[] HashProductId = { 0x9C, 0x6A, 0x10, 0x70, 0x54, 0x98, 0x09, 0x63 };
        private static readonly byte[] HashType = { 0xC5, 0xE2, 0x91, 0x53, 0xAB, 0x70, 0xCE, 0x33 };

        public static Dictionary<Guid, int> SimProductIds()
        {
            Dictionary<Guid, int> ids = new Dictionary<Guid, int>();
            foreach (string steam in GamePathDiscovery.GetSteamInstallRoots(new List<string>()))
            {
                string userdata = Path.Combine(steam, "userdata");
                if (!Directory.Exists(userdata))
                    continue;
                foreach (string user in Directory.GetDirectories(userdata))
                {
                    string file = Path.Combine(user, "2537590", "remote", "profile_00");
                    try
                    {
                        if (File.Exists(file))
                            ReadProductIds(File.ReadAllBytes(file), ids);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            return ids;
        }

        internal static void ReadProductIds(byte[] data, Dictionary<Guid, int> ids)
        {
            if (data.Length < 16)
                return;
            int rootSize = BitConverter.ToInt32(data, 8);
            if (rootSize < 16 || rootSize > data.Length)
                return;
            Walk(data, 0, rootSize, ids, 0);
        }

        private static void Walk(byte[] d, int pos, int size, Dictionary<Guid, int> ids, int depth)
        {
            if (depth > 32 || size < 16)
                return;
            int end = pos + size;
            int count = BitConverter.ToInt32(d, pos + 12);
            if (count <= 0 || count > 100000)
                return;
            List<KeyValuePair<int, int>> kids = new List<KeyValuePair<int, int>>();
            int p = pos + 16;
            for (int i = 0; i < count; i++)
            {
                if (p + 12 > end)
                    return;
                int s = BitConverter.ToInt32(d, p + 8);
                if (s < 12 || p + s > end)
                    return;
                kids.Add(new KeyValuePair<int, int>(p, s));
                p += s;
            }
            if (p != end)
                return;

            string guid = null;
            int productId = -1, type = -1;
            foreach (KeyValuePair<int, int> kid in kids)
            {
                if (SameHash(d, kid.Key, HashGuid) && kid.Value > 12)
                    guid = Encoding.ASCII.GetString(d, kid.Key + 12, kid.Value - 12).TrimEnd('\0');
                else if (SameHash(d, kid.Key, HashProductId) && kid.Value == 16)
                    productId = BitConverter.ToInt32(d, kid.Key + 12);
                else if (SameHash(d, kid.Key, HashType) && kid.Value == 16)
                    type = BitConverter.ToInt32(d, kid.Key + 12);
            }
            Guid parsed;
            if (guid != null && type == 1 && productId >= 0 && productId <= 0xFFFF && Guid.TryParse(guid, out parsed) && !ids.ContainsKey(parsed))
                ids[parsed] = productId;
            foreach (KeyValuePair<int, int> kid in kids)
                Walk(d, kid.Key, kid.Value, ids, depth + 1);
        }

        private static bool SameHash(byte[] d, int pos, byte[] hash)
        {
            for (int i = 0; i < 8; i++)
                if (d[pos + i] != hash[i])
                    return false;
            return true;
        }

        private static string Skeleton(string deviceName, Guid instanceGuid, int productId)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
            sb.Append("<DefaulftInput Primary=\"1\" PlatformAvailability=\"PC\">\r\n");
            sb.Append("\t<Version Num=\"4548\" />\r\n");
            sb.Append("\t<Device DeviceName=\"" + System.Net.WebUtility.HtmlEncode(deviceName) + "\" GUID=\"" + instanceGuid.ToString("D") +
                      "\" ProductID=\"" + productId.ToString("X4", CultureInfo.InvariantCulture) + "\" CompositeID=\"0\" HWVer=\"1.0.0.0\">\r\n");
            sb.Append("\t</Device>\r\n");
            sb.Append("</DefaulftInput>\r\n");
            return sb.ToString();
        }

        private static string SafeFileName(string name)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
            return sb.Length == 0 ? "Device" : sb.ToString();
        }

        // layout.json lists every file of the package (the sim ignores files missing there); manifest.json describes it.
        public static void WriteLayoutAndManifest(string packageFolder, GameIoResult result)
        {
            List<string> entries = new List<string>();
            long total = 0;
            foreach (string file in Directory.GetFiles(packageFolder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string relative = file.Substring(packageFolder.Length).TrimStart('\\', '/').Replace('\\', '/');
                if (string.Equals(relative, "layout.json", StringComparison.OrdinalIgnoreCase) || string.Equals(relative, "manifest.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                FileInfo info = new FileInfo(file);
                total += info.Length;
                entries.Add("    {\r\n      \"path\": \"" + JsonEscape(relative.ToLowerInvariant()) + "\",\r\n      \"size\": " + info.Length.ToString(CultureInfo.InvariantCulture) +
                            ",\r\n      \"date\": " + info.LastWriteTimeUtc.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture) + "\r\n    }");
            }
            string layout = "{\r\n  \"content\": [\r\n" + string.Join(",\r\n", entries) + "\r\n  ]\r\n}\r\n";
            string manifest = "{\r\n  \"dependencies\": [],\r\n  \"content_type\": \"MISC\",\r\n  \"title\": \"JoyPro Input Profiles\",\r\n  \"manufacturer\": \"\",\r\n" +
                              "  \"creator\": \"JoyPro\",\r\n  \"package_version\": \"1.0.0\",\r\n  \"minimum_game_version\": \"1.8.16\",\r\n" +
                              "  \"minimum_compatibility_version\": \"8.11.0.236\",\r\n  \"export_type\": \"Community\",\r\n  \"builder\": \"Microsoft Flight Simulator 2024\",\r\n" +
                              "  \"release_notes\": {\r\n    \"neutral\": {\r\n      \"LastUpdate\": \"\",\r\n      \"OlderHistory\": \"\"\r\n    }\r\n  },\r\n" +
                              "  \"total_package_size\": \"" + total.ToString("D20", CultureInfo.InvariantCulture) + "\",\r\n  \"package_order_hint\": \"INPUT\"\r\n}\r\n";
            WriteIfChanged(Path.Combine(packageFolder, "layout.json"), layout, result);
            WriteIfChanged(Path.Combine(packageFolder, "manifest.json"), manifest, result);
        }

        private static void WriteIfChanged(string path, string text, GameIoResult result)
        {
            if (File.Exists(path) && File.ReadAllText(path) == text)
                return;
            File.WriteAllText(path, text, new UTF8Encoding(false));
            result.FilesWritten.Add(path);
        }

        private static string JsonEscape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
