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

        // JoyPro's package matches the encryption of the Community folder it sits in. A Microsoft Store install on
        // another drive keeps its data encrypted, and Windows refuses a plain file there (409, "The APPX file can not
        // be accessed because it is not encrypted as expected"). JoyPro 2.1.1 to 2.2.1 removed the mark from the
        // package before every export, so an encrypted Community folder gets its package encrypted again
        // (SafeFileIo.MatchFolderEncryption); a preset that cannot be read any more is moved to quarantineFolder (or
        // deleted) and made again by EnsureDevicePresets (a warning names the devices). A plain Community folder: nothing
        // changes. JoyPro never removes the mark here.
        public static void MatchFolderEncryption(string packageFolder, string quarantineFolder, GameIoResult result)
        {
            if (string.IsNullOrWhiteSpace(packageFolder) || !Directory.Exists(packageFolder))
                return;
            List<string> done = new List<string>();
            List<string> lost = new List<string>();
            try
            {
                SafeFileIo.MatchFolderEncryption(packageFolder, quarantineFolder, done, lost);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Warnings.Add("Could not check the encryption of JoyPro's MSFS package " + packageFolder + ": " + ex.Message);
            }
            foreach (string line in done)
                JoyPro.Logging.JoyProLog.Info("MSFS", "Package encryption: " + line);
            List<string> failed = done.Where(d => d.StartsWith("could not", StringComparison.Ordinal)).ToList();
            int repaired = done.Count - failed.Count - lost.Count;
            if (repaired > 0)
                result.Steps.Add("JoyPro's package is encrypted (EFS) again like the Community folder it is in (" + repaired.ToString(CultureInfo.InvariantCulture) +
                                 " file(s) or folder(s); MSFS keeps its data encrypted on this drive).");
            if (failed.Count > 0)
                result.Warnings.Add("JoyPro could not encrypt (EFS) " + failed.Count.ToString(CultureInfo.InvariantCulture) + " file(s) or folder(s) of its MSFS package " + packageFolder +
                                    " like the Community folder it is in, so the sim cannot read them (" + failed[0] + "). Delete the folder " + packageFolder +
                                    " (close the sim first) and export again: JoyPro makes it again from your JoyPro profile.");
            List<string> presets = lost.Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();
            if (presets.Count > 0)
                result.Warnings.Add("JoyPro's MSFS 2024 preset(s) " + string.Join(", ", presets.Select(f => "'" + PresetDeviceLabel(f) + "'")) +
                                    " could not be read any more (an earlier JoyPro version had removed their encryption). They were reset and this export writes them again; " +
                                    "the binds of these devices come from your JoyPro profile. Earlier copies are in JoyPro's backups (" + Path.GetDirectoryName(quarantineFolder) +
                                    ", and 'engine backups list').");
            if (lost.Count > presets.Count)
                result.Steps.Add("Unreadable package file(s) made again by this export: " + string.Join(", ", lost.Where(f => !presets.Contains(f)).Select(Path.GetFileName)));
        }

        // "JoyPro_VPC_Stick_MT-50CM3_0123abcd_2024_Planes.xml" -> "VPC Stick MT-50CM3 (Planes)".
        internal static string PresetDeviceLabel(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            Match m = Regex.Match(name, "^JoyPro_(?<device>.+)_[0-9a-f]{8}_2024_(?<category>[A-Za-z]+)$");
            return m.Success ? m.Groups["device"].Value.Replace('_', ' ') + " (" + m.Groups["category"].Value + ")" : name;
        }

        public static List<string> PresetFiles(string packageFolder)
        {
            string root = Path.Combine(packageFolder, "InputProfiles", "Categories");
            if (!Directory.Exists(root))
                return new List<string>();
            return Directory.GetFiles(root, "*.xml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The category folder a preset file sits in ("Airplane", "Transversal", ...; empty outside Categories).
        public static string PresetCategory(string file)
        {
            if (string.IsNullOrWhiteSpace(file))
                return string.Empty;
            string folder = Path.GetDirectoryName(file);
            string parent = folder == null ? null : Path.GetDirectoryName(folder);
            return parent != null && string.Equals(Path.GetFileName(parent), "Categories", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(folder) : string.Empty;
        }

        // A preset file for every device that has none in the package yet: the airplane category for every device,
        // the transversal one (the sim's "General" binds: cameras, views, pause, ...) for the devices in
        // generalDevices. The sim matches a preset by the product ID it sees for the device: the one it noted in
        // profile_00 (vJoy: 0), else the USB PID.
        public static void EnsureDevicePresets(string packageFolder, IEnumerable<GameDevice> devices, ICollection<Guid> generalDevices, GameIoResult result)
        {
            Dictionary<Guid, int> simIds = SimProductIds();
            HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in PresetFiles(packageFolder))
            {
                MsfsProfileFile profile;
                if (MsfsProfileFile.TryLoad(file, new List<string>(), out profile) && profile.HasDeviceGuid)
                    present.Add(profile.DeviceGuid.ToString("N") + "|" + (IsTransversal(PresetCategory(file)) ? TransversalCategory : "Airplane"));
            }

            foreach (GameDevice device in devices)
            {
                if (device == null || device.InstanceGuid == Guid.Empty)
                    continue;
                List<string> categories = new List<string> { "Airplane" };
                if (generalDevices != null && generalDevices.Contains(device.InstanceGuid))
                    categories.Add(TransversalCategory);
                categories.RemoveAll(c => present.Contains(device.InstanceGuid.ToString("N") + "|" + c));
                if (categories.Count == 0)
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
                foreach (string category in categories)
                {
                    string file = Path.Combine(packageFolder, "InputProfiles", "Categories", category,
                        "JoyPro_" + SafeFileName(name) + "_" + device.InstanceGuid.ToString("N").Substring(0, 8) + "_2024_" + (category == TransversalCategory ? "Transversal" : "Planes") + ".xml");
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllText(file, Skeleton(name, device.InstanceGuid, productId), new UTF8Encoding(true));
                    present.Add(device.InstanceGuid.ToString("N") + "|" + category);
                    result.Steps.Add("Created the MSFS 2024 " + (category == TransversalCategory ? "general (transversal)" : "airplane") + " preset of '" + device.JoyProName + "': " + file);
                }
            }
        }

        // The sim's category folder of the binds that apply to every aircraft ("General" in Controls).
        public const string TransversalCategory = "Transversal";

        public static bool IsTransversal(string category)
        {
            return string.Equals(category, TransversalCategory, StringComparison.OrdinalIgnoreCase);
        }

        // Read-only: the product IDs MSFS 2024 noted per device instance GUID in profile_00 of every Steam user
        // (MsfsSimProfile reads the tree). JoyPro never writes this file: it carries a checksum, and a file the sim
        // rejects resets the user's sim profile.
        public static Dictionary<Guid, int> SimProductIds()
        {
            Dictionary<Guid, int> ids = new Dictionary<Guid, int>();
            // The test home stands in for Steam in tests (the PC's own sim profile never takes part).
            foreach (string steam in MsfsUserProfiles.SteamRoots(new List<string>()))
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
            MsfsSimProfile profile = new MsfsSimProfile();
            if (!profile.Read(data))
                return;
            foreach (KeyValuePair<Guid, int> id in profile.ProductIds)
                if (!ids.ContainsKey(id.Key))
                    ids[id.Key] = id.Value;
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
