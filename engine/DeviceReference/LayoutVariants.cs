#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace JoyPro.DeviceReference
{
    // A 2D layout variant of a device model (docs/device-reference.md, "2D layouts"): a cheat-sheet picture with drawn
    // boxes and the text position of every input in it, like a JoyPro 1 layout. The bound commands are written as plain
    // text at these positions. Shipped in DB\DeviceLayouts2D (JSON + picture); a variant whose picture is not JoyPro's
    // to ship names the file and its SHA-256 instead ("localPicture") and is offered only when that file is on this PC
    // (a JoyPro 1 folder). The engine reads the same JSON as a layout file (layout render, template-inspect,
    // template-save), the GUI lists the variants of a model in the Layout page's picture strip.
    //
    //   { "format": "joypro-2d-layout", "version": 1, "id": "...", "title": "...", "models": ["vkb-..."],
    //     "picture": "file.png" | "localPicture": { "name": "x.layout", "sha256": "..." },
    //     "width": 2108, "height": 2970, "font": "Agency FB", "fontSize": 28, "textColor": "#FF000000", "labelWidth": 240,
    //     "credit": "maogowzeg (Discord)", "source": "...", "positions": { "JOY_BTN1": [x, y], "MOD+JOY_BTN1": [x, y] } }
    //
    // Positions are picture pixels (the top-left of the text). A key "MOD+<input>" is the modifier column: it is filled
    // with the user's first modifier when the variant is taken over (WithModifier).
    public sealed class LayoutVariant
    {
        public const string FormatName = "joypro-2d-layout";
        public const string FolderName = "DeviceLayouts2D";
        public const string ModifierPlaceholder = "MOD";

        public string Id = string.Empty;
        public string Title = string.Empty;
        public List<string> Models = new List<string>();
        // A picture next to the JSON (shipped) or an absolute path (a variant taken over by the user).
        public string Picture = string.Empty;
        // A picture that is not shipped: found on this PC by name and content.
        public string LocalPictureName = string.Empty;
        public string LocalPictureSha256 = string.Empty;
        // Other files that are the same picture (the original a community sheet was sent as, another format): name and
        // SHA-256 each ("localPictureAlternatives"). The positions are the same on all of them.
        public List<KeyValuePair<string, string>> LocalPictureAlternatives = new List<KeyValuePair<string, string>>();
        public int Width;
        public int Height;
        public string Font = "Arial";
        public double FontSize = 12;
        public string TextColor = "#FF000000";
        // The room of a label in the sheet's boxes (picture pixels; 0 = up to the next label on its line).
        public double LabelWidth;
        // Who made the sheet ("maogowzeg (Discord)"); shown with the picture and printed on the cheat sheets.
        public string Credit = string.Empty;
        public string Source = string.Empty;
        public Dictionary<string, double[]> Positions = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        // Where it was read from (not written).
        public string File = string.Empty;

        public bool IsShipped { get { return Picture.Length > 0; } }

        public bool HasModifierColumn { get { return Positions.Keys.Any(k => k.StartsWith(ModifierPlaceholder + "+", StringComparison.OrdinalIgnoreCase)); } }

        public static bool LooksLikeVariant(string json)
        {
            return !string.IsNullOrEmpty(json) && json.IndexOf("\"" + FormatName + "\"", StringComparison.Ordinal) >= 0;
        }

        public static bool IsVariantFile(string path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(path) &&
                       LooksLikeVariant(System.IO.File.ReadAllText(path));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static LayoutVariant Parse(string json, string file)
        {
            Dictionary<string, object> root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null || !string.Equals(DeviceCatalog.Text(root, "format"), FormatName, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("not a JoyPro 2D layout (format '" + FormatName + "')");
            LayoutVariant variant = new LayoutVariant
            {
                Id = DeviceCatalog.Text(root, "id"),
                Title = DeviceCatalog.Text(root, "title"),
                Picture = DeviceCatalog.Text(root, "picture"),
                Font = DeviceCatalog.Text(root, "font").Length > 0 ? DeviceCatalog.Text(root, "font") : "Arial",
                TextColor = DeviceCatalog.Text(root, "textColor").Length > 0 ? DeviceCatalog.Text(root, "textColor") : "#FF000000",
                Credit = DeviceCatalog.Text(root, "credit"),
                Source = DeviceCatalog.Text(root, "source"),
                File = file ?? string.Empty
            };
            if (variant.Id.Length == 0 && !string.IsNullOrEmpty(file))
                variant.Id = Path.GetFileNameWithoutExtension(file);
            variant.Models.AddRange(DeviceCatalog.Strings(root, "models").Where(m => m.Length > 0));
            Dictionary<string, object> local = DeviceCatalog.Object(root, "localPicture");
            if (local != null)
            {
                variant.LocalPictureName = DeviceCatalog.Text(local, "name");
                variant.LocalPictureSha256 = DeviceCatalog.Text(local, "sha256").ToLowerInvariant();
            }
            foreach (Dictionary<string, object> alternative in DeviceCatalog.Objects(root, "localPictureAlternatives"))
            {
                string name = DeviceCatalog.Text(alternative, "name");
                if (name.Length > 0)
                    variant.LocalPictureAlternatives.Add(new KeyValuePair<string, string>(name, DeviceCatalog.Text(alternative, "sha256").ToLowerInvariant()));
            }
            variant.Width = (int)Number(root, "width", 0);
            variant.Height = (int)Number(root, "height", 0);
            variant.FontSize = Number(root, "fontSize", 12);
            variant.LabelWidth = Number(root, "labelWidth", 0);
            Dictionary<string, object> positions = DeviceCatalog.Object(root, "positions");
            if (positions != null)
            {
                foreach (KeyValuePair<string, object> pair in positions)
                {
                    List<object> xy = pair.Value as List<object>;
                    if (pair.Key.Trim().Length == 0 || xy == null || xy.Count < 2)
                        continue;
                    variant.Positions[pair.Key.Trim()] = new[] { Convert.ToDouble(xy[0], CultureInfo.InvariantCulture), Convert.ToDouble(xy[1], CultureInfo.InvariantCulture) };
                }
            }
            return variant;
        }

        private static double Number(Dictionary<string, object> root, string key, double fallback)
        {
            object value;
            if (root == null || !root.TryGetValue(key, out value) || value == null)
                return fallback;
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        public string ToJson()
        {
            Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["format"] = FormatName,
                ["version"] = 1.0,
                ["id"] = Id,
                ["title"] = Title,
                ["models"] = Models.Cast<object>().ToList()
            };
            if (Picture.Length > 0)
                root["picture"] = Picture;
            if (LocalPictureName.Length > 0)
                root["localPicture"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["name"] = LocalPictureName, ["sha256"] = LocalPictureSha256 };
            if (LocalPictureAlternatives.Count > 0)
                root["localPictureAlternatives"] = LocalPictureAlternatives.Select(a => (object)new Dictionary<string, object>(StringComparer.Ordinal) { ["name"] = a.Key, ["sha256"] = a.Value }).ToList();
            root["width"] = (double)Width;
            root["height"] = (double)Height;
            root["font"] = Font;
            root["fontSize"] = FontSize;
            root["textColor"] = TextColor;
            if (LabelWidth > 0)
                root["labelWidth"] = LabelWidth;
            if (Credit.Length > 0)
                root["credit"] = Credit;
            if (Source.Length > 0)
                root["source"] = Source;
            Dictionary<string, object> positions = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, double[]> pair in Positions.OrderBy(p => p.Value[1]).ThenBy(p => p.Value[0]))
                positions[pair.Key] = new List<object> { Math.Round(pair.Value[0], 1), Math.Round(pair.Value[1], 1) };
            root["positions"] = positions;
            return MiniJson.Write(root, true);
        }

        // The picture file on this PC: the shipped one next to the JSON (or an absolute path), else a local picture found
        // by name and SHA-256 in one of the folders (JoyPro 1 folders). Null when there is none.
        public string ResolvePicture(IEnumerable<string> localFolders)
        {
            if (Picture.Length > 0)
            {
                string path = Path.IsPathRooted(Picture) ? Picture : Path.Combine(Path.GetDirectoryName(File) ?? string.Empty, Picture);
                return System.IO.File.Exists(path) ? path : null;
            }
            if (LocalPictureName.Length == 0)
                return null;
            List<string> folders = (localFolders ?? Enumerable.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            foreach (KeyValuePair<string, string> wanted in new[] { new KeyValuePair<string, string>(LocalPictureName, LocalPictureSha256) }.Concat(LocalPictureAlternatives))
            {
                foreach (string folder in folders)
                {
                    try
                    {
                        string candidate = Path.Combine(folder, wanted.Key);
                        if (!System.IO.File.Exists(candidate))
                            continue;
                        if (wanted.Value.Length == 0 || string.Equals(DeviceLayoutSet.HashFile(candidate), wanted.Value, StringComparison.OrdinalIgnoreCase))
                            return candidate;
                    }
                    catch (Exception)
                    {
                        // A folder that cannot be read is skipped.
                    }
                }
            }
            return null;
        }

        // A copy for the user: the picture as an absolute path, the modifier column for the given modifier (or left
        // out without one).
        public LayoutVariant TakenOver(string picturePath, string modifier)
        {
            LayoutVariant copy = new LayoutVariant
            {
                Id = Id,
                Title = Title,
                Models = new List<string>(Models),
                Picture = picturePath ?? string.Empty,
                Width = Width,
                Height = Height,
                Font = Font,
                FontSize = FontSize,
                TextColor = TextColor,
                LabelWidth = LabelWidth,
                Credit = Credit,
                Source = Source
            };
            string mod = (modifier ?? string.Empty).Trim();
            foreach (KeyValuePair<string, double[]> pair in Positions)
            {
                string key = pair.Key;
                if (key.StartsWith(ModifierPlaceholder + "+", StringComparison.OrdinalIgnoreCase))
                {
                    if (mod.Length == 0)
                        continue;
                    key = mod + key.Substring(ModifierPlaceholder.Length);
                }
                copy.Positions[key] = new[] { pair.Value[0], pair.Value[1] };
            }
            return copy;
        }
    }

    // The shipped 2D layouts: DB\DeviceLayouts2D\*.json next to the catalog.
    public static class LayoutVariantDb
    {
        private static readonly object Gate = new object();
        private static string cachedFolder;
        private static List<LayoutVariant> cached;

        // DB\Devices -> DB\DeviceLayouts2D.
        public static string FolderOf(string catalogFolder)
        {
            string parent = string.IsNullOrWhiteSpace(catalogFolder) ? null : Path.GetDirectoryName(Path.GetFullPath(catalogFolder).TrimEnd('\\', '/'));
            return parent == null ? string.Empty : Path.Combine(parent, LayoutVariant.FolderName);
        }

        public static List<LayoutVariant> Load(string folder)
        {
            List<LayoutVariant> variants = new List<LayoutVariant>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return variants;
            foreach (string file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    variants.Add(LayoutVariant.Parse(System.IO.File.ReadAllText(file), file));
                }
                catch (Exception)
                {
                    // A broken file is left out (the curation check reports it).
                }
            }
            return variants;
        }

        public static List<LayoutVariant> Cached(string folder)
        {
            lock (Gate)
            {
                if (cached == null || !string.Equals(cachedFolder, folder, StringComparison.OrdinalIgnoreCase))
                {
                    cached = Load(folder);
                    cachedFolder = folder;
                }
                return cached;
            }
        }

        // The variants made for a model (a grip's sheet names the grip, not its base: another grip on the same base has
        // other controls).
        public static List<LayoutVariant> For(IEnumerable<LayoutVariant> variants, string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                return new List<LayoutVariant>();
            return (variants ?? Enumerable.Empty<LayoutVariant>()).Where(v => v.Models.Any(m => string.Equals(m, modelId, StringComparison.OrdinalIgnoreCase))).ToList();
        }
    }
}
