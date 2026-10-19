using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Games
{
    // MSFS saves a device's profile only once the user changes a bind; until then the device runs on the preset the
    // sim ships for it (MSFS 2024: packages fs-input-profiles / asobo-input-profiles-pc,
    // InputProfiles\Categories\<Category>\*.xml; MSFS 2020: <install>\Input and its Official packages). Import and scan
    // take that preset for every connected device without an own profile of a category, as the sim does: by the
    // product ID of <Device ProductID="hex"> (the one the sim noted in profile_00, else the USB PID; the sim's
    // DeviceConfig.xml lists devices by product ID only), the preset marked Primary="1". Read only: the shipped
    // presets are never written. See docs/games/msfs.md "Devices on the sim's default preset".
    internal static class MsfsSimDefaults
    {
        private static readonly Regex DeviceLine = new Regex("<Device\\s+(?<attrs>[^>]*)>", RegexOptions.Compiled);
        private static readonly Regex RootLine = new Regex("<DefaulftInput(?<attrs>[^>]*)>", RegexOptions.Compiled);

        // MSFS 2024 categories JoyPro imports defaults of: airplanes and the General binds.
        private static readonly string[] Categories2024 = { "AIRPLANE", "TRANSVERSAL" };

        internal sealed class Preset
        {
            public string File;
            public string Name;
            public string Category;
            public string ProductIdText;
            public int CompositeId;
            public bool Primary;
        }

        internal sealed class Applied
        {
            public GameDevice Device;
            public MsfsProfileFile Profile;
            public Preset Preset;
        }

        // The shipped presets of joystick devices (keyboard, mouse and gamepad presets have no device GUID).
        public static List<Preset> ShippedPresets(int sim, List<string> warnings)
        {
            List<Preset> presets = new List<Preset>();
            List<string> files;
            try
            {
                files = MsfsGameData.PresetFiles(MsfsGameData.Roots(sim, warnings));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                JoyPro.Logging.JoyProLog.Debug("MSFS", "No shipped presets: " + ex.Message);
                return presets;
            }
            foreach (string file in files)
            {
                Preset preset = ReadHead(file);
                if (preset != null)
                    presets.Add(preset);
            }
            return presets;
        }

        // The <DefaulftInput> and <Device> lines of a preset (the head of the file is enough).
        internal static Preset ReadHead(string file)
        {
            string head;
            try
            {
                using (StreamReader reader = new StreamReader(file, Encoding.UTF8, true))
                {
                    char[] buffer = new char[4096];
                    int read = reader.ReadBlock(buffer, 0, buffer.Length);
                    head = new string(buffer, 0, read);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
            Match root = RootLine.Match(head);
            Match device = DeviceLine.Match(head);
            if (!root.Success || !device.Success)
                return null;
            string attrs = device.Groups["attrs"].Value;
            Guid guid;
            if (!Guid.TryParse(Attribute(attrs, "GUID"), out guid) || guid == Guid.Empty)
                return null;
            string productId = Attribute(attrs, "ProductID");
            if (productId.Length == 0)
                return null;
            int composite;
            int.TryParse(Attribute(attrs, "CompositeID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out composite);
            string name = WebUtility.HtmlDecode(Attribute(attrs, "DeviceName")).Trim();
            // A name the sim localizes ("@fs-base,TT:INPUT_PROFILE...") or none: the file name.
            if (name.Length == 0 || name.StartsWith("@", StringComparison.Ordinal))
                name = Path.GetFileNameWithoutExtension(file).Replace('_', ' ');
            return new Preset
            {
                File = file,
                Name = name,
                Category = MsfsCommunityPackage.PresetCategory(file).ToUpperInvariant(),
                ProductIdText = productId,
                CompositeId = composite,
                Primary = Attribute(root.Groups["attrs"].Value, "Primary") != "0"
            };
        }

        // The defaults for every connected device and category that has no own profile (ownProfiles: the user's
        // profiles and JoyPro's package presets already chosen for the import).
        public static List<Applied> ForConnectedDevices(int sim, List<Preset> presets, IEnumerable<MsfsProfileFile> ownProfiles, IEnumerable<GameDevice> devices,
                                                        IDictionary<Guid, int> simProductIds, List<string> notes)
        {
            List<Applied> applied = new List<Applied>();
            List<KeyValuePair<GameDevice, Preset>> ambiguous = new List<KeyValuePair<GameDevice, Preset>>();
            HashSet<string> own = new HashSet<string>(ownProfiles.Select(p => p.DeviceGuid.ToString("N") + "|" + p.CategoryKey), StringComparer.OrdinalIgnoreCase);
            string[] categories = sim == 2024 ? Categories2024 : new[] { string.Empty };
            foreach (GameDevice device in devices.Where(d => d != null && d.Connected && d.InstanceGuid != Guid.Empty))
            {
                int productId;
                if (simProductIds == null || !simProductIds.TryGetValue(device.InstanceGuid, out productId))
                    productId = device.ProductId;
                if (productId < 0)
                    continue;
                foreach (string category in categories)
                {
                    if (own.Contains(device.InstanceGuid.ToString("N") + "|" + category))
                        continue;
                    List<Preset> fitting = presets.Where(p => string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase) && Fits(sim, p, productId, device)).ToList();
                    if (fitting.Count == 0)
                        continue;
                    List<Preset> primary = fitting.Where(p => p.Primary).ToList();
                    if (primary.Count == 0)
                        continue;
                    // Several parts of one device (VelocityOne Flight: yoke CompositeID 0, quadrant 1) share a product
                    // ID; JoyPro cannot tell which part this DirectInput device is, so it takes none.
                    if (primary.Select(p => p.CompositeId).Distinct().Count() > 1)
                    {
                        ambiguous.AddRange(primary.Select(p => new KeyValuePair<GameDevice, Preset>(device, p)));
                        continue;
                    }
                    Preset chosen = primary.OrderBy(p => p.File, StringComparer.OrdinalIgnoreCase).First();
                    MsfsProfileFile profile;
                    if (!MsfsProfileFile.TryLoad(chosen.File, new List<string>(), out profile))
                        continue;
                    profile.UseForDevice(device.InstanceGuid);
                    applied.Add(new Applied { Device = device, Profile = profile, Preset = chosen });
                }
            }
            if (notes != null)
            {
                foreach (IGrouping<GameDevice, Applied> group in applied.GroupBy(a => a.Device))
                {
                    List<Applied> items = group.ToList();
                    notes.Add(DeviceLabel(group.Key) + ": no own MSFS profile, imported the sim's default preset" + (items.Count > 1 ? "s " : " ") +
                              string.Join(", ", items.Select(a => "'" + a.Preset.Name + "'" + CategoryLabel(a.Preset.Category))) + ".");
                }
                foreach (IGrouping<GameDevice, KeyValuePair<GameDevice, Preset>> group in ambiguous.GroupBy(a => a.Key))
                {
                    notes.Add(DeviceLabel(group.Key) + ": no own MSFS profile, and several default presets of the sim fit (" +
                              string.Join(", ", group.Select(a => "'" + a.Value.Name + "'")) +
                              "); JoyPro cannot tell which part of the device this is, so none was imported. Change a bind in the sim's Controls to save an own profile.");
                }
            }
            return applied;
        }

        // MSFS 2024 matches by the hex product ID only. MSFS 2020's shipped presets were not checked on a real
        // install: the ID may be written hex or decimal there, so a 2020 preset must also carry the device's name.
        internal static bool Fits(int sim, Preset preset, int productId, GameDevice device)
        {
            int hex;
            bool hexFits = int.TryParse(preset.ProductIdText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hex) && hex == productId;
            if (sim == 2024)
                return hexFits;
            int dec;
            bool decFits = int.TryParse(preset.ProductIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out dec) && dec == productId;
            return (hexFits || decFits) && NameFits(preset.Name, device);
        }

        private static readonly Regex PresetSuffix = new Regex("\\s+(2020|2024)(\\s+\\w+)*$", RegexOptions.Compiled);

        internal static bool NameFits(string presetName, GameDevice device)
        {
            string preset = Plain(PresetSuffix.Replace(presetName ?? string.Empty, string.Empty));
            if (preset.Length < 4)
                return false;
            foreach (string name in new[] { device.InstanceName, device.ProductName, device.HidProductString, device.JoyProName })
            {
                string plain = Plain(name);
                if (plain.Length >= 4 && (plain.Contains(preset) || preset.Contains(plain)))
                    return true;
            }
            return false;
        }

        private static string Plain(string text)
        {
            return new string((text ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        }

        private static string DeviceLabel(GameDevice device)
        {
            if (!string.IsNullOrWhiteSpace(device.InstanceName))
                return device.InstanceName.Trim();
            return string.IsNullOrWhiteSpace(device.JoyProName) ? "{" + device.InstanceGuid.ToString().ToUpperInvariant() + "}" : device.JoyProName;
        }

        private static string CategoryLabel(string category)
        {
            switch ((category ?? string.Empty).ToUpperInvariant())
            {
                case "AIRPLANE": return " (Airplanes)";
                case "TRANSVERSAL": return " (General)";
                default: return string.Empty;
            }
        }

        private static string Attribute(string text, string name)
        {
            Match match = Regex.Match(text, "\\b" + name + "=\"(?<value>[^\"]*)\"");
            return match.Success ? match.Groups["value"].Value : string.Empty;
        }
    }
}
