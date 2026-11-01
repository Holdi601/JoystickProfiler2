#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.DeviceReference
{
    // One of JoyPro's labels on a cockpit photo: a number, a short text ("Overhead panel"), the point it marks (0..1 of
    // the photo) and what it leads to on the Cockpits page (a catalog panel id, "stick", "throttle" ...).
    public sealed class CockpitPhotoLabel
    {
        public int N;
        public string Text = string.Empty;
        public double X = -1;
        public double Y = -1;
        public string Panel = string.Empty;
        // One or two plain sentences shown in the list under the photo (else the panel's own text).
        public string Note = string.Empty;
    }

    // JoyPro's labels of one photo (DB\Cockpits\photo-labels\<aircraft>.<sha12>.json, format joypro-cockpit-photo-labels):
    // data drawn on top of the photo the user's PC downloaded, never baked into a picture.
    public sealed class CockpitPhotoLabels
    {
        public const string FormatName = "joypro-cockpit-photo-labels";

        public string File = string.Empty;
        public string Aircraft = string.Empty;
        public string Url = string.Empty;
        public string Sha256 = string.Empty;
        // The page title ("Flight deck", "Front panel"); empty: the photo's caption.
        public string Title = string.Empty;
        public string Source = string.Empty;
        public List<CockpitPhotoLabel> Labels = new List<CockpitPhotoLabel>();

        public static CockpitPhotoLabels Parse(Dictionary<string, object> root, string file)
        {
            if (root == null)
                throw new InvalidDataException("not a JSON object");
            string format = DeviceCatalog.Text(root, "format");
            if (!string.Equals(format, FormatName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("format is '" + format + "', not " + FormatName);
            Dictionary<string, object> photo = DeviceCatalog.Object(root, "photo") ?? new Dictionary<string, object>();
            CockpitPhotoLabels labels = new CockpitPhotoLabels
            {
                File = file ?? string.Empty,
                Aircraft = DeviceCatalog.Text(root, "aircraft"),
                Url = DeviceCatalog.Text(photo, "url"),
                Sha256 = DeviceCatalog.Text(photo, "sha256").ToLowerInvariant(),
                Title = DeviceCatalog.Text(root, "title"),
                Source = DeviceCatalog.Text(root, "source")
            };
            foreach (Dictionary<string, object> l in DeviceCatalog.Objects(root, "labels"))
            {
                labels.Labels.Add(new CockpitPhotoLabel
                {
                    N = (int)CockpitCatalog.Number(l, "n", 0),
                    Text = DeviceCatalog.Text(l, "text"),
                    X = CockpitCatalog.Number(l, "x", -1),
                    Y = CockpitCatalog.Number(l, "y", -1),
                    Panel = DeviceCatalog.Text(l, "panel"),
                    Note = DeviceCatalog.Text(l, "note")
                });
            }
            return labels;
        }
    }

    // The verified cockpit photos (docs/cockpits.md "Photos of cockpits no game has"): addresses with a SHA-256 for the
    // aircraft no game has good pictures of; JoyPro's labels on top; where the user's PC keeps its copy.
    public static class CockpitPhotos
    {
        public const string LabelFolderName = "photo-labels";
        // The folder in <data> where the GUI keeps the downloaded photos (ReferencePhotoStore: index.json + files).
        public const string StoreFolderName = "CockpitPhotos";

        private static readonly Regex Hex64 = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

        public static bool IsSha256(string text)
        {
            return !string.IsNullOrEmpty(text) && Hex64.IsMatch(text);
        }

        public static string StoreFolder()
        {
            return Path.Combine(DeviceCatalog.DataRoot(), StoreFolderName);
        }

        // Reads every label file and gives each to its photo (by aircraft and SHA-256); problems go to warnings.
        public static void AttachLabels(CockpitCatalog catalog)
        {
            if (catalog == null || string.IsNullOrWhiteSpace(catalog.Folder))
                return;
            string folder = Path.Combine(catalog.Folder, LabelFolderName);
            if (!Directory.Exists(folder))
                return;
            foreach (string file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                CockpitPhotoLabels labels;
                try
                {
                    labels = CockpitPhotoLabels.Parse(MiniJson.Parse(System.IO.File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>, Path.GetFileName(file));
                }
                catch (Exception ex)
                {
                    catalog.Warnings.Add(LabelFolderName + "/" + Path.GetFileName(file) + ": " + ex.Message);
                    continue;
                }
                CockpitAircraft aircraft = catalog.Aircraft.FirstOrDefault(a => string.Equals(a.Id, labels.Aircraft, StringComparison.OrdinalIgnoreCase));
                CockpitPhoto photo = aircraft == null ? null : aircraft.Photos.FirstOrDefault(p => p.Verified && string.Equals(p.Sha256, labels.Sha256, StringComparison.OrdinalIgnoreCase));
                if (photo == null)
                {
                    catalog.LabelOrphans.Add(labels);
                    continue;
                }
                photo.Labels = labels;
            }
        }

        // The verified photos of a variant: the ones of the variant itself (or the version it inherits from, the version
        // of its seat) first, then the aircraft's general ones; photos of other variants are left out.
        public static List<CockpitPhoto> Of(CockpitVariant v)
        {
            if (v == null || v.Aircraft == null)
                return new List<CockpitPhoto>();
            List<string> own = new List<string> { v.Id, v.Inherits, v.SeatOf };
            Func<CockpitPhoto, int> rank = p => p.Variant.Length == 0 ? 1 : own.Any(id => id.Length > 0 && string.Equals(id, p.Variant, StringComparison.OrdinalIgnoreCase)) ? 0 : 9;
            return v.Aircraft.Photos.Where(p => p.Verified && rank(p) < 9).OrderBy(rank).ToList();
        }

        // The local copy of a verified photo in a folder, or null: the GUI's store (index.json: Url, File, Sha256 relative
        // to the folder) or the maintainer's archive (sha256\<sha256>.<ext>, tools\backup_cockpit_photos.ps1).
        public static string LocalFile(string folder, CockpitPhoto photo)
        {
            if (photo == null || string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return null;
            string store = Path.Combine(folder, "sha256");
            if (photo.Verified && Directory.Exists(store))
            {
                foreach (string sha in new[] { photo.Sha256 }.Concat(photo.Sha256Alt))
                {
                    string hit = Directory.GetFiles(store, sha + ".*").FirstOrDefault();
                    if (hit != null)
                        return hit;
                }
            }
            string index = Path.Combine(folder, "index.json");
            if (!System.IO.File.Exists(index))
                return null;
            try
            {
                List<object> entries = MiniJson.Parse(System.IO.File.ReadAllText(index, Encoding.UTF8)) as List<object>;
                foreach (Dictionary<string, object> e in (entries ?? new List<object>()).OfType<Dictionary<string, object>>())
                {
                    if (!string.Equals(DeviceCatalog.Text(e, "Url").Trim(), photo.Url.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;
                    string file = Path.Combine(folder, DeviceCatalog.Text(e, "File"));
                    return System.IO.File.Exists(file) ? file : null;
                }
            }
            catch (Exception)
            {
                // An unreadable index: as if the photo were not on this PC.
            }
            return null;
        }

        // The pixel size of a JPEG or PNG file (0, 0 when it cannot be read): the page needs the photo's proportions, and
        // a photo a site changed may have another size than on record.
        public static int[] ImageSize(string file)
        {
            try
            {
                using (System.IO.FileStream s = System.IO.File.OpenRead(file))
                using (BinaryReader r = new BinaryReader(s))
                {
                    byte[] head = r.ReadBytes(24);
                    if (head.Length >= 24 && head[0] == 0x89 && head[1] == (byte)'P' && head[2] == (byte)'N' && head[3] == (byte)'G')
                        return new[] { BigEndian(head, 16), BigEndian(head, 20) };
                    if (head.Length < 4 || head[0] != 0xFF || head[1] != 0xD8)
                        return new[] { 0, 0 };
                    s.Position = 2;
                    while (s.Position < s.Length)
                    {
                        int marker;
                        do
                        {
                            marker = s.ReadByte();
                        } while (marker == 0xFF);
                        if (marker < 0)
                            break;
                        int lengthHi = s.ReadByte(), lengthLo = s.ReadByte();
                        if (lengthHi < 0 || lengthLo < 0)
                            break;
                        int length = (lengthHi << 8) | lengthLo;
                        // SOF0..SOF15 except DHT (C4), JPG (C8) and DAC (CC): height, then width.
                        if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                        {
                            byte[] sof = r.ReadBytes(5);
                            if (sof.Length < 5)
                                break;
                            return new[] { (sof[3] << 8) | sof[4], (sof[1] << 8) | sof[2] };
                        }
                        if (length < 2)
                            break;
                        s.Position += length - 2;
                    }
                }
            }
            catch (Exception)
            {
                // Not readable: unknown size.
            }
            return new[] { 0, 0 };
        }

        private static int BigEndian(byte[] b, int at)
        {
            return (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
        }

        // What a label leads to on the Cockpits page: a catalog panel's page, a grip's page, the overview; empty: nothing.
        public static string LinkOf(CockpitVariant v, CockpitPhotoLabel label)
        {
            string target = (label.Panel ?? string.Empty).Trim();
            if (target.Length == 0 || v == null)
                return string.Empty;
            if (string.Equals(target, "overview", StringComparison.OrdinalIgnoreCase))
                return "overview";
            List<string> devices = CockpitCatalog.DevicesOf(v);
            string device = devices.FirstOrDefault(d => string.Equals(d, target, StringComparison.OrdinalIgnoreCase));
            if (device == null && (string.Equals(target, "stick", StringComparison.OrdinalIgnoreCase) || string.Equals(target, "grip", StringComparison.OrdinalIgnoreCase)))
                device = devices.FirstOrDefault(CockpitCatalog.IsStickDevice);
            if (device == null && string.Equals(target, "throttle", StringComparison.OrdinalIgnoreCase))
                device = devices.FirstOrDefault(CockpitCatalog.IsThrottleDevice);
            if (device != null && device != "panel")
                return "device:" + device;
            CockpitPanel panel = v.Panel(target);
            return panel == null ? string.Empty : "panel:" + panel.Key;
        }

        // The rules of the verified photos and their labels (part of cockpits check).
        public static List<string> Check(CockpitCatalog catalog)
        {
            List<string> problems = new List<string>();
            if (catalog == null)
                return problems;
            foreach (CockpitAircraft a in catalog.Aircraft)
            {
                foreach (CockpitPhoto p in a.Photos)
                {
                    string at = a.Id + " photo " + (p.Url.Length > 0 ? p.Url : p.Page);
                    if (p.Sha256.Length == 0)
                    {
                        if (p.Labels != null)
                            problems.Add(at + ": labels need a photo with a SHA-256");
                        continue;
                    }
                    if (!p.Verified)
                        problems.Add(at + ": sha256 is not a lower-case hex SHA-256");
                    if (p.Url.Length == 0)
                        problems.Add(at + ": a verified photo needs its address (url)");
                    foreach (string alt in p.Sha256Alt.Where(x => !IsSha256(x)))
                        problems.Add(at + ": sha256Alt '" + alt + "' is not a SHA-256");
                    if (p.Archive.Length > 0 && !DevicePhotoDb.IsArchiveUrl(p.Archive))
                        problems.Add(at + ": archive is not a Wayback Machine snapshot (https://web.archive.org/web/<time>id_/<address>)");
                    if (p.Width <= 0 || p.Height <= 0)
                        problems.Add(at + ": a verified photo records its width and height");
                    if (p.Credit.Length == 0 || p.License.Length == 0)
                        problems.Add(at + ": a verified photo names its credit and licence");
                    if (a.Fiction && string.Equals(p.Source, "commons", StringComparison.OrdinalIgnoreCase))
                        problems.Add(at + ": a fictional craft has no Commons photo of a real cockpit");
                    if (p.Labels != null)
                        problems.AddRange(CheckLabels(catalog, a, p).Select(x => at + ": " + x));
                }
            }
            foreach (CockpitPhotoLabels orphan in catalog.LabelOrphans)
                problems.Add(LabelFolderName + "/" + orphan.File + ": no verified photo of '" + orphan.Aircraft + "' with SHA-256 " + orphan.Sha256);
            return problems;
        }

        private static List<string> CheckLabels(CockpitCatalog catalog, CockpitAircraft a, CockpitPhoto p)
        {
            List<string> problems = new List<string>();
            CockpitPhotoLabels labels = p.Labels;
            string file = LabelFolderName + "/" + labels.File;
            if (labels.Url.Length > 0 && !string.Equals(labels.Url, p.Url, StringComparison.Ordinal))
                problems.Add(file + " names another address than the photo");
            if (labels.Labels.Count == 0)
                problems.Add(file + " has no label");
            HashSet<int> numbers = new HashSet<int>();
            List<CockpitVariant> variants = a.Variants.Where(v => p.Variant.Length == 0 || string.Equals(v.Id, p.Variant, StringComparison.OrdinalIgnoreCase))
                .Select(catalog.Resolve).ToList();
            foreach (CockpitPhotoLabel l in labels.Labels)
            {
                string at = file + " label " + l.N.ToString(CultureInfo.InvariantCulture);
                if (l.N <= 0 || !numbers.Add(l.N))
                    problems.Add(at + ": numbers run 1..N, each once");
                if (l.Text.Trim().Length == 0 || l.Text.Length > 60)
                    problems.Add(at + ": a text of 1 to 60 characters");
                if (l.Note.Length > 400)
                    problems.Add(at + ": the note is longer than 400 characters");
                if (l.X < 0 || l.X > 1 || l.Y < 0 || l.Y > 1)
                    problems.Add(at + ": x and y lie within the photo (0..1)");
                if (l.Panel.Length > 0 && variants.Count > 0 && variants.All(v => LinkOf(v, l).Length == 0))
                    problems.Add(at + ": panel '" + l.Panel + "' is not a panel or grip of the aircraft");
            }
            return problems;
        }
    }
}
