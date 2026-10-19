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
    // into a folder of JoyPro, and says how to import it in the sim (Controls > device > category > preset cogwheel >
    // Import: the sim imports a file only for the same device and category).
    internal sealed partial class MsfsIntegration
    {
        public const string ImportFilesFolderName = "MSFS 2024 import files";

        // Documents\JoyPro\MSFS 2024 import files (a folder the sim's file picker reaches); with JOYPRO_DATA_DIR
        // (tests, portable setups) inside that folder.
        public static string ImportFilesFolder()
        {
            string root = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JoyPro");
            return Path.Combine(root, ImportFilesFolderName);
        }

        // The number of files written.
        private int WriteSimImportFiles(GameExportRequest request, GameIoResult result)
        {
            if (sim != 2024 || request.DryRun || request.Bindings == null)
                return 0;
            Dictionary<Guid, Dictionary<string, List<MsfsWanted>>> wantedByDevice = new Dictionary<Guid, Dictionary<string, List<MsfsWanted>>>();
            foreach (GameInputBinding binding in request.Bindings)
            {
                GameAction action = request.Catalog.Find(binding.ActionId);
                Guid guid;
                MsfsWanted wanted;
                if (action == null || Encode(binding, action, out guid, out wanted).Length > 0)
                    continue;
                Dictionary<string, List<MsfsWanted>> byAction;
                if (!wantedByDevice.TryGetValue(guid, out byAction))
                    wantedByDevice[guid] = byAction = new Dictionary<string, List<MsfsWanted>>(StringComparer.Ordinal);
                List<MsfsWanted> list;
                if (!byAction.TryGetValue(action.Id, out list))
                    byAction[action.Id] = list = new List<MsfsWanted>();
                if (!list.Any(w => w.Combo.Signature == wanted.Combo.Signature))
                    list.Add(wanted);
            }
            if (wantedByDevice.Count == 0)
                return 0;

            // The user's own profiles (Steam remote folder, Microsoft Store save), read only; each import file starts from
            // the newest one of its device and category, so the sim keeps the user's other binds.
            List<MsfsProfileFile> own = new List<MsfsProfileFile>();
            foreach (MsfsProfileSource source in MsfsUserProfiles.UserSources(sim, new List<string>()))
            {
                foreach (string file in source.Files)
                {
                    MsfsProfileFile profile;
                    if (MsfsProfileFile.TryLoad(file, new List<string>(), out profile) && profile.HasDeviceGuid && profile.Is2024 && !profile.IsPreset)
                    {
                        profile.Source = source.Kind;
                        own.Add(profile);
                    }
                }
            }
            MsfsSimProfile records = MsfsSimProfile.Load(sim, null);
            string folder = ImportFilesFolder();
            List<string> written = new List<string>();
            try
            {
                Directory.CreateDirectory(folder);
                foreach (KeyValuePair<Guid, Dictionary<string, List<MsfsWanted>>> device in wantedByDevice)
                {
                    GameDevice connected = request.Devices == null ? null : request.Devices.Devices.FirstOrDefault(d => d.InstanceGuid == device.Key);
                    foreach (bool general in new[] { false, true })
                    {
                        Dictionary<string, List<MsfsWanted>> part = device.Value
                            .Where(e => IsGeneralContext(request.Catalog.Find(e.Key).Category) == general)
                            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
                        if (part.Count == 0)
                            continue;
                        string path = WriteImportFile(request, device.Key, connected, general, part, own, records, folder, result);
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
            result.Steps.Add("To use JoyPro's binds where the sim already has settings for a device: in MSFS 2024 open Settings > Controls, pick the device and the category " +
                             "(Airplanes or General), open the preset menu (cogwheel next to the preset name), choose Import and select the file JoyPro wrote for that device and category. " +
                             "The sim imports a file only for the same device and category. JoyPro's preset package reaches only aircraft the sim has not set up for a device yet; " +
                             "Controls > Reset to Defaults would apply it everywhere, but resets every device and aircraft.");
            return written.Count;
        }

        private string WriteImportFile(GameExportRequest request, Guid deviceGuid, GameDevice device, bool general, Dictionary<string, List<MsfsWanted>> wanted,
                                       List<MsfsProfileFile> own, MsfsSimProfile records, string folder, GameIoResult result)
        {
            string label = general ? "General" : "Airplanes";
            MsfsProfileFile basis = own.Where(p => p.DeviceGuid == deviceGuid && (general ? p.CategoryKey.Length == 0 || p.IsGeneralCategory : p.CategoryKey == "AIRPLANE"))
                                       .OrderByDescending(p => SavedAt(p.FilePath)).FirstOrDefault();
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
            profile.SetFriendlyName("JoyPro " + name + " " + label);
            // The package export already reported every bind; this pass only writes the copy.
            GameIoResult scratch = new GameIoResult();
            string ownershipKey = OwnershipKey(path);
            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(ownershipKey, scratch.Warnings);
            ApplyToProfile(profile, request, wanted, owned, new Dictionary<string, string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), scratch);
            profile.Save(path);
            GameOwnershipStore.Save(ownershipKey, GameId, owned);
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
                             "): " + path + " - " + state + ".");
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
