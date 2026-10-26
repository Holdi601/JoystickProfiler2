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
        // Where the photo comes from: "maker" (the maker's own hosts, the default), "review" (a reputable review site
        // of DB\DevicePhotos\_sources.json) or "commons" (Wikimedia Commons, with the file's own licence).
        public string Source = "maker";
        // The publication or maker named in the credit line ("Wikimedia Commons", "SimFlight"); empty = the vendor.
        public string Site = string.Empty;
        // The licence note shown with the photo ("(c) VKB; maker's product photo, linked only, not redistributed").
        public string Licence = string.Empty;
        // The Wayback Machine snapshot of the photo (https://web.archive.org/web/<time>id_/<url>): JoyPro downloads it
        // when the address fails or its content changed. Empty = none recorded (JoyPro then asks for the newest one).
        public string Archive = string.Empty;
        // The picture's size in pixels when recorded (0 = unknown).
        public int Width;
        public int Height;

        // Only reachable through its Wayback snapshot (the original is a plain http address of a site that is gone).
        public bool ArchiveOnly { get { return Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase); } }

        public bool IsReview { get { return string.Equals(Source, "review", StringComparison.OrdinalIgnoreCase) || string.Equals(Source, "commons", StringComparison.OrdinalIgnoreCase); } }

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
                using (Stream stream = System.IO.File.OpenRead(file))
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
                Confidence = DeviceCatalog.Text(item, "confidence"),
                Source = DeviceCatalog.Text(item, "source").Length > 0 ? DeviceCatalog.Text(item, "source").ToLowerInvariant() : "maker",
                Site = DeviceCatalog.Text(item, "site"),
                Licence = DeviceCatalog.Text(item, "licence"),
                Archive = DeviceCatalog.Text(item, "archive")
            };
            int size;
            if (int.TryParse(DeviceCatalog.Text(item, "width"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size) && size > 0)
                photo.Width = size;
            if (int.TryParse(DeviceCatalog.Text(item, "height"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size) && size > 0)
                photo.Height = size;
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
            if (photo.Source.Length > 0 && photo.Source != "maker")
                item["source"] = photo.Source;
            if (photo.Site.Length > 0)
                item["site"] = photo.Site;
            if (photo.Licence.Length > 0)
                item["licence"] = photo.Licence;
            if (photo.Archive.Length > 0)
                item["archive"] = photo.Archive;
            if (photo.Width > 0 && photo.Height > 0)
            {
                item["width"] = photo.Width;
                item["height"] = photo.Height;
            }
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

        // Whether a photo address is on one of the model's official hosts (the vendor file's "officialHosts"). A host
        // entry may carry a path prefix for a shared CDN ("cdn.shopify.com/s/files/1/0438/4735/2471": only that shop).
        public static bool IsOfficial(RefDevice device, string url)
        {
            Uri uri;
            if (device == null || string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
                return false;
            return device.OfficialHosts.Any(h => HostMatches(uri, h));
        }

        // "example.com" matches example.com and its subdomains; "cdn.example.com/shop/1/" also needs the path to start
        // with "/shop/1/" (a shared CDN: only that shop's files).
        public static bool HostMatches(Uri uri, string entry)
        {
            if (uri == null || string.IsNullOrWhiteSpace(entry))
                return false;
            string e = entry.Trim().ToLowerInvariant();
            int slash = e.IndexOf('/');
            string host = slash < 0 ? e : e.Substring(0, slash);
            string prefix = slash < 0 ? string.Empty : e.Substring(slash);
            string h = uri.Host.ToLowerInvariant();
            if (h != host && !h.EndsWith("." + host, StringComparison.Ordinal))
                return false;
            return prefix.Length == 0 || uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // Whether JoyPro may fetch this photo: the maker's photo on an official host, or a review / Wikimedia Commons
        // photo on a site of DevicePhotoSources (never a shop, a forum or an image search).
        public static bool IsAllowed(RefDevice device, DevicePhoto photo)
        {
            return IsAllowed(device, photo, DevicePhotoSources.Current);
        }

        public static bool IsAllowed(RefDevice device, DevicePhoto photo, DevicePhotoSources sources)
        {
            if (device == null || photo == null)
                return false;
            Uri uri;
            if (!photo.IsReview)
            {
                if (IsOfficial(device, photo.Url))
                    return true;
                // A maker's photo that only survives in the Wayback Machine (an old http site): allowed through its
                // snapshot only (JoyPro then never fetches the plain http address).
                return IsArchiveUrl(photo.Archive) && Uri.TryCreate(photo.Url ?? string.Empty, UriKind.Absolute, out uri) &&
                       uri.Scheme == Uri.UriSchemeHttp && device.OfficialHosts.Any(h => HostMatches(uri, h));
            }
            if (!Uri.TryCreate(photo.Url ?? string.Empty, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
                return false;
            return (sources ?? DevicePhotoSources.Current).Allows(photo.Source, uri);
        }

        // Hosts that are shared by many sites: as a maker host they must carry the path of that maker's own folder
        // ("cdn.shopify.com/s/files/1/0571/3192/5689/"); the web archive and Wikimedia Commons are never maker hosts.
        // (A customer's own subdomain of a CDN, such as d7qztf2ityad6.cloudfront.net, is that customer's alone.)
        private static readonly string[] SharedHosts =
        {
            "cdn.shopify.com", "res.cloudinary.com", "static.wixstatic.com", "cloudfront.net", "amazonaws.com", "s3.amazonaws.com",
            "cdn11.bigcommerce.com", "lh3.googleusercontent.com", "imgur.com", "i.imgur.com", "github.com", "raw.githubusercontent.com"
        };

        private static readonly string[] NeverMakerHosts = { "web.archive.org", "archive.org", "upload.wikimedia.org", "wikimedia.org", "wikipedia.org" };

        // Why a maker host entry ("example.com" or "cdn.example.com/folder/") is not acceptable; empty when it is.
        public static string HostEntryProblem(string entry)
        {
            string e = (entry ?? string.Empty).Trim().ToLowerInvariant();
            int slash = e.IndexOf('/');
            string host = slash < 0 ? e : e.Substring(0, slash);
            if (host.Length == 0 || host.IndexOf('.') < 0 || host.StartsWith(".", StringComparison.Ordinal) || host.EndsWith(".", StringComparison.Ordinal) ||
                host.Any(c => !(char.IsLetterOrDigit(c) || c == '.' || c == '-')))
                return "not a host name: " + entry;
            if (NeverMakerHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)))
                return "never a maker's host: " + entry;
            if (slash < 0 && SharedHosts.Contains(host))
                return "a shared host needs the maker's own folder: " + entry;
            if (slash >= 0 && e.Length - slash < 4)
                return "the folder is too short: " + entry;
            return string.Empty;
        }

        // A downloaded photo list (DevicePhotoList, a folder of DevicePhotos files with _sources.json and _hosts.json)
        // checked by the rules of the shipped one: every photo names a catalog model, is allowed for it (the maker's
        // hosts of the catalog plus the list's own, a review site or Wikimedia Commons of the list's _sources.json, an old
        // http address only with its snapshot), a snapshot is a web archive address, a hash is a SHA-256. Problems are
        // returned; an empty list means the list may be used.
        public static List<string> ValidateList(DeviceCatalog catalog, string folder, int minimumPhotos)
        {
            List<string> problems = new List<string>();
            if (catalog == null || string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                problems.Add("no list");
                return problems;
            }
            DevicePhotoSources sources = DevicePhotoSources.Load(folder, problems);
            Dictionary<string, List<string>> hosts = DeviceCatalog.ReadHostsFile(Path.Combine(folder, DeviceCatalog.HostsFileName), problems);
            foreach (KeyValuePair<string, List<string>> pair in hosts)
            {
                if (catalog.Find(pair.Key) == null)
                    problems.Add("hosts of an unknown model: " + pair.Key);
                foreach (string h in pair.Value)
                {
                    string why = HostEntryProblem(h);
                    if (why.Length > 0)
                        problems.Add(pair.Key + ": " + why);
                }
            }
            int count = 0;
            foreach (string file in Directory.GetFiles(folder, "*.json"))
            {
                if (Path.GetFileName(file).StartsWith("_", StringComparison.Ordinal))
                    continue;
                List<DevicePhoto> photos;
                try
                {
                    photos = ParsePhotoFile(File.ReadAllText(file, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    problems.Add(Path.GetFileName(file) + ": " + ex.Message);
                    continue;
                }
                foreach (DevicePhoto photo in photos)
                {
                    count++;
                    RefDevice model = catalog.Find(photo.Model);
                    if (model == null)
                    {
                        problems.Add("photo of an unknown model: " + photo.Model);
                        continue;
                    }
                    RefDevice check = model.Clone();
                    List<string> extra;
                    if (hosts.TryGetValue(model.Id, out extra))
                        check.OfficialHosts.AddRange(extra.Select(h => h.Trim().ToLowerInvariant()).Where(h => !check.OfficialHosts.Contains(h)));
                    if (!IsAllowed(check, photo, sources))
                        problems.Add(model.Id + ": not an allowed source: " + photo.Url);
                    if (photo.Archive.Length > 0 && !IsArchiveUrl(photo.Archive))
                        problems.Add(model.Id + ": not a web archive address: " + photo.Archive);
                    if (photo.Sha256.Length > 0 && (photo.Sha256.Length != 64 || photo.Sha256.Any(c => !Uri.IsHexDigit(c))))
                        problems.Add(model.Id + ": not a SHA-256: " + photo.Sha256);
                }
            }
            if (count < minimumPhotos)
                problems.Add("the list has " + count.ToString(CultureInfo.InvariantCulture) + " photos, fewer than " + minimumPhotos.ToString(CultureInfo.InvariantCulture));
            return problems;
        }

        // The photos JoyPro may fetch for a model, the main picture first: the maker's own photos, then the allowed
        // review and Wikimedia Commons photos.
        public static List<DevicePhoto> UsablePhotos(RefDevice device)
        {
            if (device == null)
                return new List<DevicePhoto>();
            return device.Photos.Where(p => IsAllowed(device, p))
                .GroupBy(p => p.Url, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(p => p.IsReview ? 1 : 0)
                .ThenBy(p => p.View == "main" ? 0 : p.View == "front" ? 1 : p.View == "three-quarter" ? 2 : p.View == "sheet" ? 9 : 5)
                .ToList();
        }

        // Only the maker's own photos (the curated position sets and JoyPro's drawings are made from these).
        public static List<DevicePhoto> OfficialPhotos(RefDevice device)
        {
            return UsablePhotos(device).Where(p => !p.IsReview).ToList();
        }

        // The Wayback Machine address of a photo: the recorded snapshot, else the newest one ("2" = the newest capture
        // the archive has; "id_" = the original bytes without the archive's frame).
        public static string ArchiveUrl(DevicePhoto photo)
        {
            if (photo == null || string.IsNullOrWhiteSpace(photo.Url))
                return string.Empty;
            if (IsArchiveUrl(photo.Archive))
                return photo.Archive;
            return "https://web.archive.org/web/2id_/" + photo.Url.Trim();
        }

        public static bool IsArchiveUrl(string url)
        {
            Uri uri;
            return !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps &&
                   string.Equals(uri.Host, "web.archive.org", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/web/", StringComparison.Ordinal);
        }

        // The credit line of a photo: "SimFlight · (c) SimFlight; review photo, linked only, not redistributed"; the
        // vendor's name for its own photos.
        public static string Credit(RefDevice device, DevicePhoto photo)
        {
            if (photo == null)
                return string.Empty;
            string site = photo.Site.Length > 0 ? photo.Site : photo.Source == "commons" ? "Wikimedia Commons" : device != null ? device.Vendor : string.Empty;
            return photo.Licence.Length > 0 ? site + " · " + photo.Licence : site;
        }
    }

    // The sites besides the makers' own that photos may come from (DB\DevicePhotos\_sources.json):
    //   { "format": "joypro-photo-sources", "review": [ { "host": "simflight.com", "name": "SimFlight" } ],
    //     "commons": [ { "host": "upload.wikimedia.org", "name": "Wikimedia Commons" } ] }
    // Editorial reviews of named publications and Wikimedia Commons only; never shops, forums or image searches.
    public sealed class DevicePhotoSources
    {
        public const string FileName = "_sources.json";
        public readonly Dictionary<string, string> Review = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Commons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // The photo list's version ("version"): raised by the maintainer for every published list; a downloaded list
        // replaces the shipped one only when its version is higher (DevicePhotoList).
        public int Version;

        // The list of the catalog loaded last (DeviceCatalog.MergePhotos reads it with the photo folder).
        public static DevicePhotoSources Current = new DevicePhotoSources();

        public bool Allows(string source, Uri uri)
        {
            Dictionary<string, string> hosts = string.Equals(source, "commons", StringComparison.OrdinalIgnoreCase) ? Commons : Review;
            return uri != null && hosts.Keys.Any(h => DevicePhotoDb.HostMatches(uri, h));
        }

        public static DevicePhotoSources Parse(string json)
        {
            DevicePhotoSources sources = new DevicePhotoSources();
            Dictionary<string, object> root = MiniJson.Parse(json) as Dictionary<string, object>;
            int version;
            if (int.TryParse(DeviceCatalog.Text(root, "version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
                sources.Version = version;
            foreach (Dictionary<string, object> item in DeviceCatalog.Objects(root, "review"))
            {
                if (DeviceCatalog.Text(item, "host").Length > 0)
                    sources.Review[DeviceCatalog.Text(item, "host").ToLowerInvariant()] = DeviceCatalog.Text(item, "name");
            }
            foreach (Dictionary<string, object> item in DeviceCatalog.Objects(root, "commons"))
            {
                if (DeviceCatalog.Text(item, "host").Length > 0)
                    sources.Commons[DeviceCatalog.Text(item, "host").ToLowerInvariant()] = DeviceCatalog.Text(item, "name");
            }
            return sources;
        }

        // _sources.json in a DevicePhotos folder; an empty list when there is none.
        public static DevicePhotoSources Load(string photoFolder, List<string> warnings)
        {
            string file = string.IsNullOrWhiteSpace(photoFolder) ? null : Path.Combine(photoFolder, FileName);
            if (file == null || !File.Exists(file))
                return new DevicePhotoSources();
            try
            {
                return Parse(File.ReadAllText(file, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                if (warnings != null)
                    warnings.Add(FileName + " could not be read: " + ex.Message);
                return new DevicePhotoSources();
            }
        }
    }
}
