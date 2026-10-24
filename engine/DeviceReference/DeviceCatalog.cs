#if NET
#nullable disable
#endif
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.DeviceReference
{
    // One input of a catalog device: the DirectInput input ("JOY_BTN3", "JOY_BTN_POV1_U", "JOY_X"), the control in the
    // shared vocabulary ("stick.hat.tms.up", docs/devices.md) and the printed label. Input is empty when the catalog
    // knows the control but not its number.
    public sealed class RefControl
    {
        public string Input = string.Empty;
        public string Control = string.Empty;
        public string Label = string.Empty;
        public string Type = "momentary";
        public string Finger = string.Empty;

        public RefControl Clone()
        {
            return (RefControl)MemberwiseClone();
        }
    }

    // A device model of the catalog (DB\Devices\*.json) or a user definition (<data>\Devices\*.json).
    public sealed class RefDevice
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Vendor = string.Empty;
        public string Kind = "other";
        public string Hand = "none";
        public string Base = string.Empty;
        // The manufacturer's product picture (only shown when its host is one of the vendor's official hosts).
        public string Image = string.Empty;
        public string ProductUrl = string.Empty;
        public string Confidence = string.Empty;
        // "catalog" (shipped with JoyPro) or "user" (made with the marker assistant or imported).
        public string Origin = "catalog";
        public string File = string.Empty;
        public string Note = string.Empty;
        public List<string> VidPids = new List<string>();
        public List<string> Names = new List<string>();
        public List<string> Sources = new List<string>();
        public List<string> OfficialHosts = new List<string>();
        public List<RefControl> Axes = new List<RefControl>();
        public List<RefControl> Controls = new List<RefControl>();
        // "reference": { "shape": "grip.space", "markers": { "<element id>": [x, y] } } - optional hints for the
        // schematic: the drawing family and marker positions (0..1 of the schematic) that override the computed ones.
        public string Shape = string.Empty;
        public Dictionary<string, double[]> Markers = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        // A grip that also fits other bases than Base ("fitsBases": the same connector, an adapter).
        public List<string> FitsBases = new List<string>();
        // Reference photos (the catalog's "image" as view "main", the entry's "photos" and DB\DevicePhotos).
        public List<DevicePhoto> Photos = new List<DevicePhoto>();

        public bool IsGrip { get { return Base.Length > 0; } }

        // For UI bindings (fields are not bindable).
        public string DisplayName { get { return Name; } }

        public IEnumerable<RefControl> AllControls { get { return Axes.Concat(Controls); } }

        public DeviceSignature Signature { get { return DeviceSignature.Of(AllControls.Select(c => c.Input)); } }

        public override string ToString()
        {
            return Id + " (" + Name + ")";
        }

        public RefDevice Clone()
        {
            RefDevice copy = (RefDevice)MemberwiseClone();
            copy.VidPids = new List<string>(VidPids);
            copy.Names = new List<string>(Names);
            copy.Sources = new List<string>(Sources);
            copy.OfficialHosts = new List<string>(OfficialHosts);
            copy.Axes = Axes.Select(a => a.Clone()).ToList();
            copy.Controls = Controls.Select(c => c.Clone()).ToList();
            copy.Markers = new Dictionary<string, double[]>(Markers, StringComparer.OrdinalIgnoreCase);
            copy.FitsBases = new List<string>(FitsBases);
            copy.Photos = Photos.Select(p => p.Clone()).ToList();
            return copy;
        }
    }

    // What a device reports: the highest button number, the axes and the number of hats. Used to tell apart grips
    // that report the same USB id (a VKB base, a VPforce Rhino, a Virpil base with different grips).
    public struct DeviceSignature
    {
        public int MaxButton;
        public int Hats;
        public List<string> Axes;

        public static DeviceSignature Of(IEnumerable<string> inputs)
        {
            DeviceSignature signature = new DeviceSignature { Axes = new List<string>() };
            foreach (string raw in inputs ?? Enumerable.Empty<string>())
            {
                string input = (raw ?? string.Empty).Trim().ToUpperInvariant();
                if (input.Length == 0)
                    continue;
                int pov;
                if (input.StartsWith("JOY_BTN_POV", StringComparison.Ordinal) && TryNumber(input, 11, out pov))
                {
                    signature.Hats = Math.Max(signature.Hats, pov);
                    continue;
                }
                int button;
                if (input.StartsWith("JOY_BTN", StringComparison.Ordinal) && TryNumber(input, 7, out button))
                {
                    signature.MaxButton = Math.Max(signature.MaxButton, button);
                    continue;
                }
                if (input.StartsWith("JOY_", StringComparison.Ordinal) && !signature.Axes.Contains(input))
                    signature.Axes.Add(input);
            }
            return signature;
        }

        private static bool TryNumber(string text, int start, out int value)
        {
            int end = start;
            while (end < text.Length && char.IsDigit(text[end]))
                end++;
            value = 0;
            return end > start && int.TryParse(text.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }

    public sealed class DeviceCatalog
    {
        public const string KbFolderVariable = "JOYPRO_DEVICE_KB";
        public const string DataFolderVariable = "JOYPRO_DATA_DIR";

        public List<RefDevice> Devices = new List<RefDevice>();
        public List<string> Warnings = new List<string>();

        // The shipped catalog of an engine folder (engine.exe's folder): <engine>\DB\Devices, or JOYPRO_DEVICE_KB.
        public static string CatalogFolder(string engineFolder)
        {
            string overrideFolder = Environment.GetEnvironmentVariable(KbFolderVariable);
            if (!string.IsNullOrWhiteSpace(overrideFolder))
                return overrideFolder;
            return Path.Combine(engineFolder ?? string.Empty, "DB", "Devices");
        }

        // The user's own device definitions: <data>\Devices (data = JOYPRO_DATA_DIR or %LOCALAPPDATA%\JoyPro, as the
        // engine's other data).
        public static string UserFolder()
        {
            return Path.Combine(DataRoot(), "Devices");
        }

        // <data>: JOYPRO_DATA_DIR, else %LOCALAPPDATA%\JoyPro.
        public static string DataRoot()
        {
            string root = Environment.GetEnvironmentVariable(DataFolderVariable);
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro");
            return root;
        }

        // Cheat-sheet pictures the user received from others (community sheets) that JoyPro does not ship, e.g. because
        // they hold vendor product renders: <data>\Layouts\Community. A 2D layout variant with a localPicture finds its
        // picture here by name and SHA-256.
        public static string CommunityLayoutFolder()
        {
            return Path.Combine(DataRoot(), "Layouts", "Community");
        }

        // The user's JoyPro 1 layouts JoyPro took over ("Use my old JoyPro layouts..."): <data>\Layouts\JoyPro1. Copies;
        // the user's old folder is never written.
        public static string ImportedLayoutFolder()
        {
            return Path.Combine(DataRoot(), "Layouts", "JoyPro1");
        }

        // Reads the catalog folders in order; a later folder's device replaces an earlier one with the same id (the
        // user's definition of a model wins over the shipped one).
        public static DeviceCatalog Load(params string[] folders)
        {
            DeviceCatalog catalog = new DeviceCatalog();
            foreach (string folder in folders ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    continue;
                bool user = string.Equals(Path.GetFullPath(folder).TrimEnd('\\', '/'), Path.GetFullPath(UserFolder()).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
                foreach (string file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        foreach (RefDevice device in ParseFile(File.ReadAllText(file, Encoding.UTF8), file))
                        {
                            if (user)
                                device.Origin = "user";
                            catalog.Devices.RemoveAll(d => string.Equals(d.Id, device.Id, StringComparison.OrdinalIgnoreCase));
                            catalog.Devices.Add(device);
                        }
                    }
                    catch (Exception ex)
                    {
                        catalog.Warnings.Add("Device catalog file '" + Path.GetFileName(file) + "' could not be read: " + ex.Message);
                    }
                }
                catalog.MergePhotos(DevicePhotoDb.PhotoFolderOf(folder));
            }
            return catalog;
        }

        // Adds the photo references of a DevicePhotos folder to the models they name.
        public void MergePhotos(string photoFolder)
        {
            if (string.IsNullOrWhiteSpace(photoFolder) || !Directory.Exists(photoFolder))
                return;
            foreach (string file in Directory.GetFiles(photoFolder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    foreach (DevicePhoto photo in DevicePhotoDb.ParsePhotoFile(File.ReadAllText(file, Encoding.UTF8)))
                    {
                        RefDevice device = Find(photo.Model);
                        if (device == null)
                        {
                            Warnings.Add("Photo of unknown model '" + photo.Model + "' in " + Path.GetFileName(file));
                            continue;
                        }
                        DevicePhoto known = device.Photos.FirstOrDefault(p => string.Equals(p.Url, photo.Url, StringComparison.OrdinalIgnoreCase));
                        if (known != null)
                            device.Photos.Remove(known);
                        // The catalog's own picture stays the main one (shown first); the photo file adds its hash and parts.
                        if (known != null && known.View == "main")
                            photo.View = "main";
                        device.Photos.Add(photo);
                    }
                }
                catch (Exception ex)
                {
                    Warnings.Add("Photo file '" + Path.GetFileName(file) + "' could not be read: " + ex.Message);
                }
            }
        }

        // A catalog file: { "vendor": ..., "officialHosts": [...], "devices": [ ... ] }, or one device object.
        public static List<RefDevice> ParseFile(string json, string file)
        {
            List<RefDevice> devices = new List<RefDevice>();
            Dictionary<string, object> root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null)
                throw new FormatException("the file is not a JSON object");
            string vendor = Text(root, "vendor");
            List<string> hosts = Strings(root, "officialHosts").Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).ToList();
            IEnumerable<Dictionary<string, object>> items = root.ContainsKey("devices") ? Objects(root, "devices") : new[] { root };
            foreach (Dictionary<string, object> item in items)
            {
                RefDevice device = ParseDevice(item, vendor);
                device.File = file ?? string.Empty;
                device.OfficialHosts.AddRange(hosts.Concat(Strings(item, "officialHosts").Select(h => h.Trim().ToLowerInvariant())).Where(h => h.Length > 0).Distinct());
                if (device.Id.Length > 0)
                    devices.Add(device);
            }
            return devices;
        }

        public static RefDevice ParseDevice(Dictionary<string, object> item, string vendor)
        {
            RefDevice device = new RefDevice
            {
                Id = Text(item, "id"),
                Name = Text(item, "name"),
                Vendor = Text(item, "vendor").Length > 0 ? Text(item, "vendor") : vendor ?? string.Empty,
                Kind = Text(item, "kind").Length > 0 ? Text(item, "kind").ToLowerInvariant() : "other",
                Hand = Text(item, "hand").Length > 0 ? Text(item, "hand").ToLowerInvariant() : "none",
                Base = Text(item, "base"),
                Image = Text(item, "image"),
                ProductUrl = Text(item, "productUrl"),
                Confidence = Text(item, "confidence"),
                Origin = Text(item, "origin").Length > 0 ? Text(item, "origin") : "catalog"
            };
            if (device.Name.Length == 0)
                device.Name = device.Id;
            Dictionary<string, object> identify = Object(item, "identify");
            if (identify != null)
            {
                device.VidPids.AddRange(Strings(identify, "vidPids").Select(NormalizeVidPid).Where(v => v.Length > 0));
                device.Names.AddRange(Strings(identify, "names").Where(n => n.Trim().Length > 0));
                device.Note = Text(identify, "note");
            }
            device.Sources.AddRange(Strings(item, "sources"));
            device.FitsBases.AddRange(Strings(item, "fitsBases").Select(b => b.Trim()).Where(b => b.Length > 0));
            if (device.Image.Length > 0)
                device.Photos.Add(new DevicePhoto { Model = device.Id, Url = device.Image, View = "main", Page = device.ProductUrl, Confidence = device.Confidence });
            foreach (Dictionary<string, object> photoItem in Objects(item, "photos"))
            {
                DevicePhoto photo = DevicePhotoDb.ParsePhoto(photoItem);
                photo.Model = device.Id;
                if (photo.Url.Length > 0 && !device.Photos.Any(p => string.Equals(p.Url, photo.Url, StringComparison.OrdinalIgnoreCase)))
                    device.Photos.Add(photo);
            }
            device.Axes.AddRange(Objects(item, "axes").Select(ParseControl));
            device.Controls.AddRange(Objects(item, "controls").Select(ParseControl));
            Dictionary<string, object> reference = Object(item, "reference");
            if (reference != null)
            {
                device.Shape = Text(reference, "shape");
                Dictionary<string, object> markers = Object(reference, "markers");
                if (markers != null)
                {
                    foreach (KeyValuePair<string, object> pair in markers)
                    {
                        List<object> xy = pair.Value as List<object>;
                        if (xy == null || xy.Count < 2)
                            continue;
                        double x = Convert.ToDouble(xy[0], CultureInfo.InvariantCulture);
                        double y = Convert.ToDouble(xy[1], CultureInfo.InvariantCulture);
                        if (x >= 0 && x <= 1 && y >= 0 && y <= 1)
                            device.Markers[pair.Key] = new[] { x, y };
                    }
                }
            }
            return device;
        }

        // A device as catalog JSON (the format of DB\Devices, one object), for user definitions and sharing.
        public static Dictionary<string, object> ToJson(RefDevice device)
        {
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item["id"] = device.Id;
            item["name"] = device.Name;
            if (device.Vendor.Length > 0)
                item["vendor"] = device.Vendor;
            item["kind"] = device.Kind;
            item["hand"] = device.Hand;
            if (device.Base.Length > 0)
                item["base"] = device.Base;
            if (device.Origin.Length > 0 && device.Origin != "catalog")
                item["origin"] = device.Origin;
            Dictionary<string, object> identify = new Dictionary<string, object>(StringComparer.Ordinal);
            identify["vidPids"] = device.VidPids.Cast<object>().ToList();
            identify["names"] = device.Names.Cast<object>().ToList();
            if (device.Note.Length > 0)
                identify["note"] = device.Note;
            item["identify"] = identify;
            if (device.Image.Length > 0)
                item["image"] = device.Image;
            if (device.ProductUrl.Length > 0)
                item["productUrl"] = device.ProductUrl;
            if (device.Sources.Count > 0)
                item["sources"] = device.Sources.Cast<object>().ToList();
            if (device.FitsBases.Count > 0)
                item["fitsBases"] = device.FitsBases.Cast<object>().ToList();
            List<DevicePhoto> extraPhotos = device.Photos.Where(p => !string.Equals(p.Url, device.Image, StringComparison.OrdinalIgnoreCase)).ToList();
            if (extraPhotos.Count > 0)
                item["photos"] = extraPhotos.Select(p => (object)DevicePhotoDb.PhotoToJson(p)).ToList();
            item["confidence"] = device.Confidence.Length > 0 ? device.Confidence : "low";
            item["axes"] = device.Axes.Select(ControlToJson).Cast<object>().ToList();
            item["controls"] = device.Controls.Select(ControlToJson).Cast<object>().ToList();
            if (device.Shape.Length > 0 || device.Markers.Count > 0)
            {
                Dictionary<string, object> reference = new Dictionary<string, object>(StringComparer.Ordinal);
                if (device.Shape.Length > 0)
                    reference["shape"] = device.Shape;
                if (device.Markers.Count > 0)
                {
                    Dictionary<string, object> markers = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, double[]> pair in device.Markers.OrderBy(p => p.Key, StringComparer.Ordinal))
                        markers[pair.Key] = new List<object> { Math.Round(pair.Value[0], 4), Math.Round(pair.Value[1], 4) };
                    reference["markers"] = markers;
                }
                item["reference"] = reference;
            }
            return item;
        }

        private static Dictionary<string, object> ControlToJson(RefControl control)
        {
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            if (control.Input.Length > 0)
                item["input"] = control.Input;
            item["control"] = control.Control;
            if (control.Label.Length > 0)
                item["label"] = control.Label;
            if (control.Finger.Length > 0)
                item["finger"] = control.Finger;
            item["type"] = control.Type.Length > 0 ? control.Type : "momentary";
            return item;
        }

        public RefDevice Find(string id)
        {
            return Devices.FirstOrDefault(d => string.Equals(d.Id, (id ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public RefDevice BaseOf(RefDevice device)
        {
            return device == null || !device.IsGrip ? null : Find(device.Base);
        }

        // The grips of a base: its own (Base) and those that also fit it (FitsBases).
        public List<RefDevice> GripsOf(string baseId)
        {
            return Devices.Where(d => string.Equals(d.Base, baseId, StringComparison.OrdinalIgnoreCase) ||
                                      d.FitsBases.Any(b => string.Equals(b, baseId, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        public List<RefDevice> Search(string query)
        {
            string q = (query ?? string.Empty).Trim();
            if (q.Length == 0)
                return Devices.ToList();
            string[] words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return Devices.Where(d => words.All(w =>
                    d.Id.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    d.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    d.Vendor.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    d.VidPids.Any(v => v.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    d.Names.Any(n => n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)))
                .ToList();
        }

        // The manufacturer's picture of a model, when its address is on one of the vendor's official hosts (the
        // catalog file's "officialHosts"). Pictures of shops, blogs and other sites are never shown.
        public static string OfficialPhoto(RefDevice device)
        {
            if (device == null || device.Image.Length == 0)
                return string.Empty;
            Uri uri;
            if (!Uri.TryCreate(device.Image, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
                return string.Empty;
            string host = uri.Host.ToLowerInvariant();
            return device.OfficialHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)) ? device.Image : string.Empty;
        }

        public static string NormalizeVidPid(string value)
        {
            string text = (value ?? string.Empty).Trim().ToUpperInvariant().Replace("VID_", string.Empty).Replace("PID_", string.Empty).Replace("&", ":");
            string[] parts = text.Split(':');
            int vid, pid;
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid) ||
                !int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid))
                return string.Empty;
            return vid.ToString("X4", CultureInfo.InvariantCulture) + ":" + pid.ToString("X4", CultureInfo.InvariantCulture);
        }

        public static string Normalize(string value)
        {
            return new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        public static RefControl ParseControl(Dictionary<string, object> item)
        {
            return new RefControl
            {
                Input = Text(item, "input").ToUpperInvariant(),
                Control = Text(item, "control"),
                Label = Text(item, "label"),
                Type = Text(item, "type").Length > 0 ? Text(item, "type") : "momentary",
                Finger = Text(item, "finger")
            };
        }

        internal static string Text(Dictionary<string, object> item, string key)
        {
            object value;
            if (item == null || !item.TryGetValue(key, out value) || value == null || value is IDictionary || (value is IList))
                return string.Empty;
            return Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
        }

        internal static Dictionary<string, object> Object(Dictionary<string, object> item, string key)
        {
            object value;
            return item != null && item.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        internal static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> item, string key)
        {
            object value;
            if (item == null || !item.TryGetValue(key, out value))
                return Enumerable.Empty<Dictionary<string, object>>();
            List<object> list = value as List<object>;
            return list == null ? Enumerable.Empty<Dictionary<string, object>>() : list.OfType<Dictionary<string, object>>();
        }

        internal static IEnumerable<string> Strings(Dictionary<string, object> item, string key)
        {
            object value;
            if (item == null || !item.TryGetValue(key, out value) || value == null)
                return Enumerable.Empty<string>();
            string single = value as string;
            if (single != null)
                return new[] { single };
            List<object> list = value as List<object>;
            return list == null ? Enumerable.Empty<string>() : list.Where(o => o != null && !(o is IDictionary)).Select(o => Convert.ToString(o, CultureInfo.InvariantCulture));
        }
    }
}
