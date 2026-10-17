#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace JoyPro.DeviceReference
{
    // One reference photo of a model (docs/device-reference.md, "Photo references"): where JoyPro fetches it at run time
    // on the user's PC (never shipped), which view of the device it shows, and the SHA-256 of its content after the
    // first fetch. Curated marker positions (DeviceLayoutSet) belong to exactly that content: a photo whose content
    // changed loses them.
    public sealed class DevicePhoto
    {
        public string Model = string.Empty;
        public string Url = string.Empty;
        // front, side, back, top, base, three-quarter, main (the catalog's "image"), sheet (the maker's button map).
        public string View = "main";
        // The product or press page the photo is taken from.
        public string Page = string.Empty;
        // Lower-case hex SHA-256 of the photo file; empty = not fetched by a maintainer yet (no curated positions).
        public string Sha256 = string.Empty;
        public string Confidence = string.Empty;
        // The parts (vocabulary ids without direction: "stick.hat.tms") this view shows; empty = not recorded.
        public List<string> Shows = new List<string>();

        public string ViewTitle
        {
            get
            {
                switch ((View ?? string.Empty).ToLowerInvariant())
                {
                    case "front": return "Front";
                    case "side": return "Side";
                    case "left": return "Left side";
                    case "right": return "Right side";
                    case "back": return "Back";
                    case "top": return "Top";
                    case "base": return "Base";
                    case "three-quarter": return "3/4 view";
                    case "sheet": return "Button map";
                    default:
                        // gallery-3: a shop photo whose view nobody tagged yet.
                        string view = View ?? string.Empty;
                        return view.StartsWith("gallery-", StringComparison.OrdinalIgnoreCase) ? "Photo " + view.Substring(8) : "Photo";
                }
            }
        }

        public DevicePhoto Clone()
        {
            DevicePhoto copy = (DevicePhoto)MemberwiseClone();
            copy.Shows = new List<string>(Shows);
            return copy;
        }
    }

    // A set of marker positions for one picture of one model: JoyPro's curated sets (DB\DeviceLayouts), what the user
    // placed (<data>\DeviceReference\Layouts) and what he imported or exports to share. Format "joypro-device-layout"
    // version 1:
    //   { "format": "joypro-device-layout", "version": 1, "model": "<catalog id>",
    //     "photo": { "kind": "vendor"|"user"|"drawing", "url": "...", "sha256": "...", "view": "front", "file": "<base64>" },
    //     "markers": { "<part id>": [x, y] },  (0..1 of the picture)
    //     "source": "...", "confidence": "high", "note": "..." }
    // "file" (the picture itself) is only ever written for the user's own photo, never for a maker's photo.
    // JoyPro's curated sets (tools/device_positions.py) add, all optional:
    //     "parts": { "<part id>": { "type": "hat", "name": "...", "inputs": [ { "input": "JOY_BTN14", "control": "stick.hat.tms.up" } ],
    //                               "mapping": "high", "confidence": "high", "note": "..." } },   every part: its group of inputs,
    //                               how certain the input mapping is and (when placed) how certain the position is
    //     "unplaced": { "<part id>": "<why it is not placed on this photo>" },
    //     "candidates": { "<part id>": [x, y] },   a likely position that is not certain enough to be shown
    //     "mappingSource": "<where the button numbers come from>"
    public sealed class DeviceLayoutSet
    {
        public const string FormatName = "joypro-device-layout";

        public string Model = string.Empty;
        public string PhotoKind = "vendor";
        public string PhotoUrl = string.Empty;
        public string PhotoSha256 = string.Empty;
        public string View = string.Empty;
        // The user's own photo inside a shared file (base64); empty otherwise.
        public string PhotoFileBase64 = string.Empty;
        public string PhotoFileName = string.Empty;
        public Dictionary<string, double[]> Markers = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        public string Source = string.Empty;
        public string Confidence = string.Empty;
        public string Note = string.Empty;
        // Curated sets only (see above): the parts with their inputs, the parts not placed and why, likely positions
        // that are not shown, and where the input mapping comes from.
        public Dictionary<string, DeviceLayoutPart> Parts = new Dictionary<string, DeviceLayoutPart>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Unplaced = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double[]> Candidates = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        public string MappingSource = string.Empty;
        // The tables and leader lines of the picture (CalloutSheet, "callouts"); null = none saved.
        public CalloutSheet Callouts;
        // A local copy of the picture on this PC ("photo.local"): written only into the user's own files, never into a
        // shared one (ToJson(false)), so the engine can render a sheet without downloading the maker's photo.
        public string LocalPhotoFile = string.Empty;
        // Where it was read from (not written).
        public string File = string.Empty;

        // The key a picture's positions are kept under: the content hash for a photo, "drawing" for JoyPro's drawing.
        public string PictureKey { get { return PhotoKind == "drawing" ? "drawing" : (PhotoSha256 ?? string.Empty).ToLowerInvariant(); } }

        public static DeviceLayoutSet Parse(string json, string file)
        {
            Dictionary<string, object> root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null || !string.Equals(DeviceCatalog.Text(root, "format"), FormatName, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("not a JoyPro device layout (format '" + FormatName + "')");
            DeviceLayoutSet set = new DeviceLayoutSet
            {
                Model = DeviceCatalog.Text(root, "model"),
                Source = DeviceCatalog.Text(root, "source"),
                Confidence = DeviceCatalog.Text(root, "confidence"),
                Note = DeviceCatalog.Text(root, "note"),
                File = file ?? string.Empty
            };
            Dictionary<string, object> photo = DeviceCatalog.Object(root, "photo");
            if (photo != null)
            {
                set.PhotoKind = DeviceCatalog.Text(photo, "kind").Length > 0 ? DeviceCatalog.Text(photo, "kind").ToLowerInvariant() : "vendor";
                set.PhotoUrl = DeviceCatalog.Text(photo, "url");
                set.PhotoSha256 = DeviceCatalog.Text(photo, "sha256").ToLowerInvariant();
                set.View = DeviceCatalog.Text(photo, "view");
                set.PhotoFileBase64 = DeviceCatalog.Text(photo, "file");
                set.PhotoFileName = DeviceCatalog.Text(photo, "fileName");
                set.LocalPhotoFile = DeviceCatalog.Text(photo, "local");
            }
            Dictionary<string, object> callouts = DeviceCatalog.Object(root, "callouts");
            if (callouts != null)
                set.Callouts = CalloutSheet.FromJsonObject(callouts);
            ReadPositions(DeviceCatalog.Object(root, "markers"), set.Markers);
            ReadPositions(DeviceCatalog.Object(root, "candidates"), set.Candidates);
            set.MappingSource = DeviceCatalog.Text(root, "mappingSource");
            Dictionary<string, object> unplaced = DeviceCatalog.Object(root, "unplaced");
            if (unplaced != null)
            {
                foreach (KeyValuePair<string, object> pair in unplaced)
                    set.Unplaced[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
            Dictionary<string, object> parts = DeviceCatalog.Object(root, "parts");
            if (parts != null)
            {
                foreach (KeyValuePair<string, object> pair in parts)
                {
                    Dictionary<string, object> item = pair.Value as Dictionary<string, object>;
                    if (item == null)
                        continue;
                    DeviceLayoutPart part = new DeviceLayoutPart
                    {
                        Type = DeviceCatalog.Text(item, "type"),
                        Name = DeviceCatalog.Text(item, "name"),
                        Mapping = DeviceCatalog.Text(item, "mapping"),
                        Confidence = DeviceCatalog.Text(item, "confidence"),
                        Note = DeviceCatalog.Text(item, "note")
                    };
                    foreach (Dictionary<string, object> input in DeviceCatalog.Objects(item, "inputs"))
                        part.Inputs.Add(new DeviceLayoutPartInput { Input = DeviceCatalog.Text(input, "input"), Control = DeviceCatalog.Text(input, "control") });
                    set.Parts[pair.Key] = part;
                }
            }
            if (set.Model.Length == 0)
                throw new FormatException("the layout names no model");
            return set;
        }

        private static void ReadPositions(Dictionary<string, object> source, Dictionary<string, double[]> target)
        {
            if (source == null)
                return;
            foreach (KeyValuePair<string, object> pair in source)
            {
                List<object> xy = pair.Value as List<object>;
                if (xy == null || xy.Count < 2)
                    continue;
                double x = Convert.ToDouble(xy[0], CultureInfo.InvariantCulture), y = Convert.ToDouble(xy[1], CultureInfo.InvariantCulture);
                if (x >= 0 && x <= 1 && y >= 0 && y <= 1)
                    target[pair.Key] = new[] { x, y };
            }
        }

        private static Dictionary<string, object> WritePositions(Dictionary<string, double[]> positions)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, double[]> pair in positions.OrderBy(p => p.Key, StringComparer.Ordinal))
                result[pair.Key] = new List<object> { Math.Round(pair.Value[0], 4), Math.Round(pair.Value[1], 4) };
            return result;
        }

        public string ToJson()
        {
            return ToJson(false);
        }

        // includeLocal: also the local copy of the picture (the user's own files only).
        public string ToJson(bool includeLocal)
        {
            Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal);
            root["format"] = FormatName;
            root["version"] = 1;
            root["model"] = Model;
            Dictionary<string, object> photo = new Dictionary<string, object>(StringComparer.Ordinal);
            photo["kind"] = PhotoKind;
            if (PhotoUrl.Length > 0)
                photo["url"] = PhotoUrl;
            if (PhotoSha256.Length > 0)
                photo["sha256"] = PhotoSha256;
            if (View.Length > 0)
                photo["view"] = View;
            if (PhotoKind == "user" && PhotoFileBase64.Length > 0)
            {
                photo["file"] = PhotoFileBase64;
                if (PhotoFileName.Length > 0)
                    photo["fileName"] = PhotoFileName;
            }
            if (includeLocal && LocalPhotoFile.Length > 0)
                photo["local"] = LocalPhotoFile;
            root["photo"] = photo;
            root["markers"] = WritePositions(Markers);
            if (Parts.Count > 0)
            {
                Dictionary<string, object> parts = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, DeviceLayoutPart> pair in Parts)
                {
                    Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (pair.Value.Type.Length > 0)
                        item["type"] = pair.Value.Type;
                    if (pair.Value.Name.Length > 0)
                        item["name"] = pair.Value.Name;
                    item["inputs"] = pair.Value.Inputs.Select(i => (object)new Dictionary<string, object>(StringComparer.Ordinal) { ["input"] = i.Input, ["control"] = i.Control }).ToList();
                    if (pair.Value.Mapping.Length > 0)
                        item["mapping"] = pair.Value.Mapping;
                    if (pair.Value.Confidence.Length > 0)
                        item["confidence"] = pair.Value.Confidence;
                    if (pair.Value.Note.Length > 0)
                        item["note"] = pair.Value.Note;
                    parts[pair.Key] = item;
                }
                root["parts"] = parts;
            }
            if (Unplaced.Count > 0)
                root["unplaced"] = Unplaced.ToDictionary(p => p.Key, p => (object)p.Value, StringComparer.Ordinal);
            if (Candidates.Count > 0)
                root["candidates"] = WritePositions(Candidates);
            if (Source.Length > 0)
                root["source"] = Source;
            if (MappingSource.Length > 0)
                root["mappingSource"] = MappingSource;
            if (Confidence.Length > 0)
                root["confidence"] = Confidence;
            if (Note.Length > 0)
                root["note"] = Note;
            if (Callouts != null)
                root["callouts"] = Callouts.ToJsonObject();
            return MiniJson.Write(root, indented: true);
        }

        // All sets in a folder (*.json of this format); files of another format are skipped, unreadable ones reported.
        public static List<DeviceLayoutSet> LoadFolder(string folder, List<string> warnings)
        {
            List<DeviceLayoutSet> sets = new List<DeviceLayoutSet>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return sets;
            foreach (string file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    sets.Add(Parse(System.IO.File.ReadAllText(file, Encoding.UTF8), file));
                }
                catch (Exception ex)
                {
                    if (warnings != null)
                        warnings.Add("Device layout '" + Path.GetFileName(file) + "' skipped: " + ex.Message);
                }
            }
            return sets;
        }

        // The SHA-256 of a file's content (lower-case hex); empty when it cannot be read.
        public static string HashFile(string file)
        {
            try
            {
                using (FileStream stream = System.IO.File.OpenRead(file))
                using (SHA256 sha = SHA256.Create())
                    return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    // One part of a curated set: the control group (a hat with its directions, a switch with its positions, an axis)
    // as the catalog gives it, how certain the input mapping is and, when placed, how certain the position is.
    public sealed class DeviceLayoutPart
    {
        public string Type = string.Empty;
        public string Name = string.Empty;
        public List<DeviceLayoutPartInput> Inputs = new List<DeviceLayoutPartInput>();
        // high, medium or low: the button numbers / directions of this part (the set's mappingSource says from where).
        public string Mapping = string.Empty;
        // high or medium: the position on the photo (empty when the part is not placed).
        public string Confidence = string.Empty;
        public string Note = string.Empty;
    }

    public sealed class DeviceLayoutPartInput
    {
        public string Input = string.Empty;
        public string Control = string.Empty;
    }

    // The photo references of the catalog (DB\DevicePhotos\*.json, merged into each model's Photos by DeviceCatalog.Load)
    // and the curated marker positions (DB\DeviceLayouts\*.json). Format of a photo file:
    //   { "photos": [ { "model": "<id>", "url": "https://<official host>/...", "view": "front", "page": "<product page>",
    //                   "sha256": "<after the first fetch>", "confidence": "high", "shows": ["stick.hat.tms", ...] } ] }
    public static class DevicePhotoDb
    {
        public const string PhotoFolderName = "DevicePhotos";
        public const string LayoutFolderName = "DeviceLayouts";

        public static List<DevicePhoto> ParsePhotoFile(string json)
        {
            List<DevicePhoto> photos = new List<DevicePhoto>();
            Dictionary<string, object> root = MiniJson.Parse(json) as Dictionary<string, object>;
            foreach (Dictionary<string, object> item in DeviceCatalog.Objects(root, "photos"))
            {
                DevicePhoto photo = ParsePhoto(item);
                if (photo.Model.Length > 0 && photo.Url.Length > 0)
                    photos.Add(photo);
            }
            return photos;
        }

        public static DevicePhoto ParsePhoto(Dictionary<string, object> item)
        {
            DevicePhoto photo = new DevicePhoto
            {
                Model = DeviceCatalog.Text(item, "model"),
                Url = DeviceCatalog.Text(item, "url"),
                View = DeviceCatalog.Text(item, "view").Length > 0 ? DeviceCatalog.Text(item, "view").ToLowerInvariant() : "main",
                Page = DeviceCatalog.Text(item, "page"),
                Sha256 = DeviceCatalog.Text(item, "sha256").ToLowerInvariant(),
                Confidence = DeviceCatalog.Text(item, "confidence")
            };
            photo.Shows.AddRange(DeviceCatalog.Strings(item, "shows").Where(s => s.Trim().Length > 0).Select(s => s.Trim()));
            return photo;
        }

        public static Dictionary<string, object> PhotoToJson(DevicePhoto photo)
        {
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item["model"] = photo.Model;
            item["url"] = photo.Url;
            item["view"] = photo.View;
            if (photo.Page.Length > 0)
                item["page"] = photo.Page;
            if (photo.Sha256.Length > 0)
                item["sha256"] = photo.Sha256;
            if (photo.Confidence.Length > 0)
                item["confidence"] = photo.Confidence;
            if (photo.Shows.Count > 0)
                item["shows"] = photo.Shows.Cast<object>().ToList();
            return item;
        }

        // The photo folder next to a catalog folder (DB\Devices -> DB\DevicePhotos).
        public static string PhotoFolderOf(string catalogFolder)
        {
            string parent = string.IsNullOrWhiteSpace(catalogFolder) ? null : Path.GetDirectoryName(Path.GetFullPath(catalogFolder).TrimEnd('\\', '/'));
            return parent == null ? string.Empty : Path.Combine(parent, PhotoFolderName);
        }

        // The curated layouts next to a catalog folder (DB\Devices -> DB\DeviceLayouts).
        public static string LayoutFolderOf(string catalogFolder)
        {
            string parent = string.IsNullOrWhiteSpace(catalogFolder) ? null : Path.GetDirectoryName(Path.GetFullPath(catalogFolder).TrimEnd('\\', '/'));
            return parent == null ? string.Empty : Path.Combine(parent, LayoutFolderName);
        }

        // Whether a photo address is on one of the model's official hosts (the vendor file's "officialHosts").
        public static bool IsOfficial(RefDevice device, string url)
        {
            Uri uri;
            if (device == null || string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
                return false;
            string host = uri.Host.ToLowerInvariant();
            return device.OfficialHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
        }

        // The photos JoyPro may fetch for a model, the main picture first: only addresses on the vendor's own hosts.
        public static List<DevicePhoto> OfficialPhotos(RefDevice device)
        {
            if (device == null)
                return new List<DevicePhoto>();
            return device.Photos.Where(p => IsOfficial(device, p.Url))
                .GroupBy(p => p.Url, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(p => p.View == "main" ? 0 : p.View == "front" ? 1 : p.View == "three-quarter" ? 2 : p.View == "sheet" ? 9 : 5)
                .ToList();
        }
    }
}
