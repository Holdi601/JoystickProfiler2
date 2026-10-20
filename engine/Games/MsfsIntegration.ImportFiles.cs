using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace JoyPro.Engine.Games
{
    // MSFS 2024 "import files" (docs/games/msfs.md "Getting the binds into the sim"): the sim applies a preset of
    // JoyPro's package only where it has not set up a device for an aircraft yet (one record per device and aircraft in
    // profile_00, made the first time the device is used in that aircraft). Every device a user has flown with keeps the
    // sim's own settings there, and the Store version keeps the user's profiles in its save containers, which JoyPro does
    // not write. So every export to the package also writes, per device and category, a profile in the sim's own format
    // (the user's profile of that device and category with JoyPro's binds, else one listing the command list's actions)
    // into a folder of JoyPro, and says how to import it in the sim (Controls > device > category > the profile's cogwheel
    // > Import: the sim imports a file only for the same device and category).
    internal sealed partial class MsfsIntegration
    {
        public const string ImportFilesFolderName = "MSFS 2024 import files";

        // Documents\JoyPro2\MSFS 2024 import files, beside the JoyPro 2 profiles (a folder the sim's file picker reaches);
        // with JOYPRO_DATA_DIR (tests, portable setups) inside that folder.
        public static string ImportFilesFolder()
        {
            string root = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JoyPro2");
            return Path.Combine(root, ImportFilesFolderName);
        }

        // 2.2.7 wrote the files to Documents\JoyPro (JoyPro 1's folder): they move to the JoyPro 2 folder once.
        private static void MoveImportFilesOfJoyPro227(string folder, GameIoResult result)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR")))
                return;
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JoyPro", ImportFilesFolderName);
            try
            {
                if (!Directory.Exists(old))
                    return;
                Directory.CreateDirectory(folder);
                foreach (string file in Directory.GetFiles(old, "*.xml"))
                {
                    string target = Path.Combine(folder, Path.GetFileName(file));
                    if (!File.Exists(target))
                        File.Move(file, target);
                    else
                        File.Delete(file);
                }
                if (Directory.GetFileSystemEntries(old).Length == 0)
                    Directory.Delete(old);
                result.Steps.Add("The MSFS 2024 import files moved from " + old + " to " + folder + ".");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                JoyPro.Logging.JoyProLog.Debug("MSFS", "Could not move the import files of 2.2.7 from " + old + ": " + ex.Message);
            }
        }

        private static Dictionary<string, string> FileHashes(string folder)
        {
            Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!Directory.Exists(folder))
                    return hashes;
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                {
                    foreach (string file in Directory.GetFiles(folder, "*.xml"))
                        hashes[Path.GetFullPath(file)] = Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(file)));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
            return hashes;
        }

        // The user's own 2024 profiles (Steam remote folders, Microsoft Store save), read only.
        private List<MsfsProfileFile> OwnProfiles2024()
        {
            List<MsfsProfileFile> own = new List<MsfsProfileFile>();
            foreach (MsfsProfileSource source in MsfsUserProfiles.UserSources(sim, new List<string>()))
            {
                foreach (string file in source.Files)
                {
                    MsfsProfileFile profile;
                    if (MsfsProfileFile.TryLoad(file, new List<string>(), out profile) && profile.HasDeviceGuid && profile.Is2024 && !profile.IsPreset)
                    {
                        profile.Source = source.Kind;
                        KeyValuePair<string, string> container;
                        if (source.Containers.TryGetValue(file, out container))
                        {
                            profile.StoreContainer = container.Key;
                            profile.StoreUserFolder = container.Value;
                        }
                        own.Add(profile);
                    }
                }
            }
            return own;
        }

        // The sim's name of an own profile: the Steam file name or the Store container (inputprofile_<n>).
        internal static string SimProfileName(MsfsProfileFile profile)
        {
            if (profile == null)
                return string.Empty;
            if (!string.IsNullOrEmpty(profile.StoreContainer))
                return profile.StoreContainer;
            return profile.Source == MsfsProfileSourceKind.Steam ? Path.GetFileName(profile.FilePath) : string.Empty;
        }

        // The own profile of a device and category the import file starts from: the one the sim's records name (the
        // active one), else the newest. active: the records name it.
        private static MsfsProfileFile ImportBasis(List<MsfsProfileFile> own, MsfsSimProfile records, Guid deviceGuid, bool general, out bool active)
        {
            List<MsfsProfileFile> candidates = own.Where(p => p.DeviceGuid == deviceGuid && (general ? p.CategoryKey.Length == 0 || p.IsGeneralCategory : p.CategoryKey == "AIRPLANE")).ToList();
            int naming, total;
            List<string> others;
            string name = ActiveOwnProfile(records, deviceGuid, general, out naming, out total, out others);
            MsfsProfileFile named = name == null ? null : candidates.FirstOrDefault(p => string.Equals(SimProfileName(p), name, StringComparison.OrdinalIgnoreCase));
            active = named != null;
            return named ?? candidates.OrderByDescending(p => SavedAt(p.FilePath)).FirstOrDefault();
        }

        // The number of files written. only: "<guid N>|G" / "<guid N>|A" of the devices and categories to write
        // (null = all).
        private int WriteSimImportFiles(GameExportRequest request, GameIoResult result, HashSet<string> only = null)
        {
            if (sim != 2024 || request.DryRun || request.Bindings == null)
                return 0;
            if (only != null && only.Count == 0)
                return 0;
            Dictionary<Guid, Dictionary<string, List<MsfsWanted>>> wantedByDevice = WantedByDevice(request, false);
            if (wantedByDevice.Count == 0)
                return 0;

            // Each import file starts from the user's own profile of its device and category (the one the sim uses), so
            // the sim keeps the user's other binds.
            List<MsfsProfileFile> own = OwnProfiles2024();
            MsfsSimProfile records = MsfsSimProfile.Load(sim, null);
            string folder = ImportFilesFolder();
            MoveImportFilesOfJoyPro227(folder, result);
            Dictionary<string, string> before = FileHashes(folder);
            List<string> written = new List<string>();
            try
            {
                Directory.CreateDirectory(folder);
                foreach (KeyValuePair<Guid, Dictionary<string, List<MsfsWanted>>> device in wantedByDevice)
                {
                    GameDevice connected = request.Devices == null ? null : request.Devices.Devices.FirstOrDefault(d => d.InstanceGuid == device.Key);
                    bool generalActive, airplaneActive;
                    MsfsProfileFile generalBasis = ImportBasis(own, records, device.Key, true, out generalActive);
                    MsfsProfileFile airplaneBasis = ImportBasis(own, records, device.Key, false, out airplaneActive);
                    foreach (bool general in new[] { false, true })
                    {
                        if (only != null && !only.Contains(device.Key.ToString("N") + "|" + (general ? "G" : "A")))
                            continue;
                        // Every bind goes to the category the sim lists its action in (KEY_ACTIVE_PAUSE_TOGGLE: General).
                        Dictionary<string, List<MsfsWanted>> part = device.Value
                            .Where(e => GoesToGeneral(e.Key, request.Catalog.Find(e.Key), generalBasis, airplaneBasis) == general)
                            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
                        if (part.Count == 0)
                            continue;
                        string path = WriteImportFile(request, device.Key, connected, general, part, general ? generalBasis : airplaneBasis, general ? generalActive : airplaneActive, records, folder, result);
                        if (path != null)
                            written.Add(path);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Warnings.Add("Could not write the MSFS 2024 import files into " + folder + ": " + ex.Message);
            }
            if (written.Count == 0)
                return 0;
            result.Steps.Add("MSFS 2024 import files written (" + written.Count.ToString(CultureInfo.InvariantCulture) + ", one per device and category): " + folder);
            // Only a file that changed has to be imported again in the sim.
            Dictionary<string, string> after = FileHashes(folder);
            List<string> changed = written.Where(p =>
            {
                string full = Path.GetFullPath(p);
                string old;
                string now;
                return !before.TryGetValue(full, out old) || !after.TryGetValue(full, out now) || old != now;
            }).Select(Path.GetFileName).ToList();
            result.Steps.Add(changed.Count == 0
                ? "No import file changed since the last export: nothing to import again in the sim."
                : "Import these again in the sim (new or changed since the last export): " + string.Join(", ", changed) +
                  (written.Count > changed.Count ? " (" + (written.Count - changed.Count).ToString(CultureInfo.InvariantCulture) + " unchanged)" : string.Empty) + ".");
            result.Steps.Add("To use JoyPro's binds where the sim already has settings for a device: in MSFS 2024 open Settings > Controls, pick the device and the category " +
                             "(Airplanes or General), click the cogwheel of the profile (not the preset's), choose Import and select the file JoyPro wrote for that device and category. " +
                             "The sim imports a file only for the same device and category. JoyPro's preset package reaches only aircraft the sim has not set up for a device yet; " +
                             "Controls > Reset to Defaults would apply it everywhere, but resets every device and aircraft.");
            return written.Count;
        }

        private string WriteImportFile(GameExportRequest request, Guid deviceGuid, GameDevice device, bool general, Dictionary<string, List<MsfsWanted>> wanted,
                                       MsfsProfileFile basis, bool basisActive, MsfsSimProfile records, string folder, GameIoResult result)
        {
            string label = general ? "General" : "Airplanes";
            string name = device != null && !string.IsNullOrWhiteSpace(device.InstanceName) ? device.InstanceName.Trim()
                        : basis != null && basis.DeviceName.Length > 0 ? basis.DeviceName.Trim() : "Device";
            string path = Path.Combine(folder, SafeName(name) + "_" + deviceGuid.ToString("N").Substring(0, 8) + "_" + label + ".xml");
            if (basis != null)
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes(MsfsUserProfiles.DecodeText(SafeFileIo.ReadAllBytes(basis.FilePath))));
            else
            {
                int productId;
                if (!records.ProductIds.TryGetValue(deviceGuid, out productId))
                    productId = device != null ? device.ProductId : -1;
                if (productId < 0)
                {
                    result.Warnings.Add("No MSFS 2024 import file for '" + name + "' (" + label + "): the device is not connected and the sim has no profile of it; connect it and export again.");
                    return null;
                }
                File.WriteAllText(path, NewImportProfile(name, deviceGuid, productId, general, request.Catalog), new UTF8Encoding(false));
            }
            MsfsProfileFile profile;
            List<string> errors = new List<string>();
            if (!MsfsProfileFile.TryLoad(path, errors, out profile))
            {
                result.Warnings.AddRange(errors);
                return null;
            }
            // Named like the user's active profile: the sim's Import then asks "a profile with that name already exists,
            // overwrite?" and Yes puts the binds into that profile (tester report, MSFS 2024 Store). Otherwise a new
            // profile "JoyPro <device> <category>".
            bool overwrites = basis != null && basisActive && basis.FriendlyName.Length > 0;
            if (!overwrites)
                profile.SetFriendlyName("JoyPro " + name + " " + label);
            // The package export already reported every bind; this pass only writes the copy. Ownership: as the user's
            // profile it overwrites (an earlier import of the file put JoyPro's binds there).
            GameIoResult scratch = new GameIoResult();
            string ownershipKey = OwnershipKey(overwrites ? StableProfilePath(basis) : path);
            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(ownershipKey, scratch.Warnings);
            ApplyToProfile(profile, request, wanted, owned, new Dictionary<string, string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), scratch);
            profile.Save(path);
            GameOwnershipStore.Save(ownershipKey, GameId, owned);
            // Completeness: every bind of this file is in it as written.
            MsfsProfileFile written;
            if (MsfsProfileFile.TryLoad(path, new List<string>(), out written))
            {
                foreach (KeyValuePair<string, List<MsfsWanted>> entry in wanted)
                {
                    MsfsAction action = written.Find(entry.Key);
                    foreach (MsfsWanted item in entry.Value.Where(w => action == null || !action.Combos.Any(c => c.Signature == w.Combo.Signature)))
                    {
                        string reason = scratch.Warnings.FirstOrDefault(w => w.Contains("(" + entry.Key + ")")) ??
                                        (action == null ? "the profile does not list " + entry.Key : "the profile kept another bind there");
                        result.Warnings.Add("MSFS 2024 import file of '" + name + "': not written: " + BindName(item, request.Catalog.Find(entry.Key)) + " (" + label + ") - " + reason + ".");
                    }
                }
            }
            JoyPro.Logging.JoyProLog.Debug("MSFS", "Import file written: " + path + " (" + name + " {" + deviceGuid.ToString().ToUpperInvariant() + "}, " + label + ", base: " +
                (basis != null ? basis.Source + " profile '" + basis.FriendlyName + "' " + basis.FilePath : "new, product ID") + "): " +
                profile.ChangedActions.ToString(CultureInfo.InvariantCulture) + " action(s) changed (" + string.Join(", ", profile.Actions.Where(a => a.Changed).Select(a => a.Name).Take(40)) + ")");

            // What the sim does with this device now: its records (one per aircraft it was used in) keep their settings.
            List<MsfsSimProfile.Record> deviceRecords = records.RecordsOf(deviceGuid).Where(r => general || string.IsNullOrEmpty(r.Category) || string.Equals(r.Category, "AIRPLANE", StringComparison.OrdinalIgnoreCase)).ToList();
            List<string> aircraft = deviceRecords.Select(r => r.Aircraft).Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
            int ownProfile = deviceRecords.Count(r => IsOwnProfile(general ? r.General : (r.AircraftPreset ?? r.CategoryPreset)));
            string state = aircraft.Count == 0
                ? (records.Sources.Count == 0 ? "the sim's device records were not found" : "the sim has not set up this device for any aircraft yet, so JoyPro's preset applies")
                : "the sim keeps its own settings of this device for " + aircraft.Count.ToString(CultureInfo.InvariantCulture) + " aircraft (" + string.Join(", ", aircraft.Take(4)) +
                  (aircraft.Count > 4 ? ", ..." : string.Empty) + (ownProfile > 0 ? "; your own profile in " + ownProfile.ToString(CultureInfo.InvariantCulture) + " record(s)" : string.Empty) +
                  ") and does not use JoyPro's preset there; import this file to use JoyPro's binds";
            result.Steps.Add("MSFS 2024 import file of '" + name + "' (" + label + ", " + (basis != null ? "your profile '" + basis.FriendlyName + "' with JoyPro's binds" : "JoyPro's binds") +
                             "): " + path + " - " + state + "." +
                             (overwrites ? " Import it and confirm 'overwrite': your profile '" + basis.FriendlyName + "' then has JoyPro's binds." : string.Empty));
            return path;
        }

        private static bool IsOwnProfile(string preset)
        {
            return !string.IsNullOrEmpty(preset) && preset.StartsWith("inputprofile_", StringComparison.OrdinalIgnoreCase);
        }

        // A profile in the sim's own format (as Controls > Export writes it) listing every action of the command list for
        // the category: the flight contexts for airplanes, the general ones for General (the sim lists every action of a
        // profile's contexts, unbound ones as empty elements).
        private static string NewImportProfile(string deviceName, Guid deviceGuid, int productId, bool general, GameCatalog catalog)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<Version Num=\"-1\"/>\r\n");
            sb.Append("<FriendlyName PlatformAvailability=\"1\" Locked=\"false\">JoyPro</FriendlyName>\r\n");
            sb.Append("<Device DeviceName=\"" + WebUtility.HtmlEncode(deviceName) + "\" GUID=\"" + deviceGuid.ToString("B").ToUpperInvariant() + "\" ProductID=\"" +
                      productId.ToString(CultureInfo.InvariantCulture) + "\" CompositeID=\"0\" HWVer=\"1.0.0.0\">\r\n");
            if (!general)
                sb.Append("\t<AircraftInfo CategoryName=\"AIRPLANE\"/>\r\n");
            foreach (IGrouping<string, GameAction> context in catalog.Actions
                         .Where(a => general ? IsGeneralContext(a.Category) : FlightContexts.Contains(a.Category ?? string.Empty))
                         .GroupBy(a => a.Category, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.Append("\t<Context ContextName=\"" + context.Key + "\">\r\n");
                foreach (GameAction action in context.OrderBy(a => a.Id, StringComparer.Ordinal))
                    sb.Append("\t\t<Action ActionName=\"" + action.Id + "\" ValueEvent=\"0.000000\" Delay=\"0.000000\" Flag=\"2\"/>\r\n");
                sb.Append("\t</Context>\r\n");
            }
            sb.Append("</Device>\r\n");
            return sb.ToString();
        }

        private static string SafeName(string name)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in name ?? string.Empty)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
            return sb.Length == 0 ? "Device" : sb.ToString();
        }
    }
}
