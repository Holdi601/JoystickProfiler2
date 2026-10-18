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
    // JoyPro's line drawing of a device (docs/device-reference.md, "JoyPro's line drawings"): a clean black-on-white
    // drawing of the model's silhouette and the contours of its buttons, hats and switches, as vector paths with a
    // uniform stroke. It lies in the same coordinate frame as one reference photo of the model, so the curated marker
    // positions of that photo (DB\DeviceLayouts, 0..1 of the picture) and its callout sheet apply to it 1:1. The drawing
    // names the photo only by an opaque key (OpaqueKey of the photo's SHA-256). Format "joypro-device-drawing" v1:
    //   { "format": "joypro-device-drawing", "version": 1, "model": "<catalog id>", "view": "main", "key": "<16 hex>",
    //     "class": "stick", "title": "...", "width": 768, "height": 768, "outlineWidth": 2.3, "detailWidth": 1.6,
    //     "paths": [ { "role": "outline", "d": "M10,20Q...Z" }, { "role": "detail", "d": "M..." } ],   (role minor: finer)
    //     "fills": [ { "color": "#3A3A3A", "rule": "evenodd", "d": "M...Z" } ] }   (optional: the cel-shaded style)
    // How it is painted (lines or cel-shaded, the colours) is the user's DrawingStyle, the same in every renderer.
    // Cockpit grips (DB\Cockpits\line-drawings) carry "aircraft" and "device" (stick, throttle, cyclic, ...) instead of
    // "model". Path data: absolute M, L, Q, C and Z with "x,y" pairs, nothing else (no arcs, no fills, no pictures).
    public sealed class DeviceLineArt
    {
        public const string FormatName = "joypro-device-drawing";
        public const string FolderName = "DeviceDrawings";
        public const string CockpitFolderName = "line-drawings";

        public string Model = string.Empty;
        public string Aircraft = string.Empty;
        public string Device = string.Empty;
        public string View = "main";
        public string Key = string.Empty;
        public string Class = string.Empty;
        public string Title = string.Empty;
        public double Width;
        public double Height;
        public double OutlineWidth = 2;
        public double DetailWidth = 1.4;
        public List<LineArtPath> Paths = new List<LineArtPath>();
        // The cel-shaded style (optional): flat colour regions drawn under the strokes, in the listed order.
        public List<LineArtFill> Fills = new List<LineArtFill>();
        // Cockpit grips (optional): where the controls are on the drawing (0..1), by the cockpit drawing's part ids;
        // device drawings take their positions from the curated set of their photo instead.
        public Dictionary<string, double[]> Parts = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        // Where it was read from (not written).
        public string File = string.Empty;

        public bool IsCockpit { get { return Aircraft.Length > 0; } }

        // The key a drawing carries for its reference photo: the first 16 hex digits of SHA-256("joypro-drawing:" +
        // the photo's SHA-256 in lower case). It finds the photo's curated positions without naming the photo.
        public static string OpaqueKey(string photoSha256)
        {
            if (string.IsNullOrWhiteSpace(photoSha256))
                return string.Empty;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes("joypro-drawing:" + photoSha256.Trim().ToLowerInvariant()));
                return string.Concat(hash.Take(8).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        public static DeviceLineArt Parse(string json, string file)
        {
            Dictionary<string, object> root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null || !string.Equals(DeviceCatalog.Text(root, "format"), FormatName, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("not a JoyPro line drawing (format '" + FormatName + "')");
            DeviceLineArt art = new DeviceLineArt
            {
                Model = DeviceCatalog.Text(root, "model"),
                Aircraft = DeviceCatalog.Text(root, "aircraft"),
                Device = DeviceCatalog.Text(root, "device"),
                View = DeviceCatalog.Text(root, "view").Length > 0 ? DeviceCatalog.Text(root, "view") : "main",
                Key = DeviceCatalog.Text(root, "key").ToLowerInvariant(),
                Class = DeviceCatalog.Text(root, "class"),
                Title = DeviceCatalog.Text(root, "title"),
                Width = Number(root, "width", 0),
                Height = Number(root, "height", 0),
                OutlineWidth = Number(root, "outlineWidth", 2),
                DetailWidth = Number(root, "detailWidth", 1.4),
                File = file ?? string.Empty
            };
            if (art.Width <= 0 || art.Height <= 0)
                throw new FormatException("a line drawing needs a width and a height");
            foreach (Dictionary<string, object> item in DeviceCatalog.Objects(root, "paths"))
            {
                string d = DeviceCatalog.Text(item, "d");
                if (d.Length == 0)
                    continue;
                ParsePath(d); // throws on anything but absolute M L Q C Z
                art.Paths.Add(new LineArtPath { Role = DeviceCatalog.Text(item, "role").Length > 0 ? DeviceCatalog.Text(item, "role") : "detail", D = d });
            }
            foreach (Dictionary<string, object> item in DeviceCatalog.Objects(root, "fills"))
            {
                string d = DeviceCatalog.Text(item, "d");
                string color = DrawingStyle.NormalizeColor(DeviceCatalog.Text(item, "color"));
                if (d.Length == 0 || color.Length == 0)
                    continue;
                ParsePath(d);
                art.Fills.Add(new LineArtFill { Color = color, D = d, EvenOdd = !string.Equals(DeviceCatalog.Text(item, "rule"), "nonzero", StringComparison.OrdinalIgnoreCase) });
            }
            Dictionary<string, object> parts = DeviceCatalog.Object(root, "parts");
            if (parts != null)
            {
                foreach (KeyValuePair<string, object> pair in parts)
                {
                    List<object> xy = pair.Value as List<object>;
                    if (xy != null && xy.Count >= 2)
                        art.Parts[pair.Key] = new[] { Convert.ToDouble(xy[0], CultureInfo.InvariantCulture), Convert.ToDouble(xy[1], CultureInfo.InvariantCulture) };
                }
            }
            return art;
        }

        public bool HasFills { get { return Fills.Count > 0; } }

        private static double Number(Dictionary<string, object> item, string key, double fallback)
        {
            object value;
            if (item == null || !item.TryGetValue(key, out value) || value == null)
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

        // The commands of a path: M (x y), L (x y), Q (cx cy x y), C (c1x c1y c2x c2y x y), Z, all absolute.
        public static List<LineArtCommand> ParsePath(string d)
        {
            List<LineArtCommand> commands = new List<LineArtCommand>();
            int i = 0;
            char op = '\0';
            while (true)
            {
                SkipSeparators(d, ref i);
                if (i >= d.Length)
                    break;
                char c = d[i];
                if (char.IsLetter(c))
                {
                    if ("MLQCZ".IndexOf(c) < 0)
                        throw new FormatException("path command '" + c + "' is not allowed (only absolute M L Q C Z)");
                    op = c;
                    i++;
                    if (op == 'Z')
                    {
                        commands.Add(new LineArtCommand { Op = 'Z', P = new double[0] });
                        continue;
                    }
                }
                else if (op == '\0' || op == 'Z')
                {
                    throw new FormatException("path data must start with a command");
                }
                int count = op == 'Q' ? 4 : op == 'C' ? 6 : 2;
                double[] p = new double[count];
                for (int k = 0; k < count; k++)
                {
                    SkipSeparators(d, ref i);
                    p[k] = ReadNumber(d, ref i);
                }
                commands.Add(new LineArtCommand { Op = op, P = p });
                // A repeated pair after M is a line (as in SVG).
                if (op == 'M')
                    op = 'L';
            }
            if (commands.Count == 0 || commands[0].Op != 'M')
                throw new FormatException("path data must start with M");
            return commands;
        }

        private static void SkipSeparators(string d, ref int i)
        {
            while (i < d.Length && (d[i] == ' ' || d[i] == ',' || d[i] == '\n' || d[i] == '\r' || d[i] == '\t'))
                i++;
        }

        private static double ReadNumber(string d, ref int i)
        {
            int start = i;
            if (i < d.Length && (d[i] == '-' || d[i] == '+'))
                i++;
            while (i < d.Length && (char.IsDigit(d[i]) || d[i] == '.'))
                i++;
            if (i < d.Length && (d[i] == 'e' || d[i] == 'E'))
            {
                i++;
                if (i < d.Length && (d[i] == '-' || d[i] == '+'))
                    i++;
                while (i < d.Length && char.IsDigit(d[i]))
                    i++;
            }
            double value;
            if (i == start || !double.TryParse(d.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                throw new FormatException("a number was expected at position " + start + " of the path data");
            return value;
        }

        // The paths as polylines in drawing units (curves cut into straight pieces), for hit tests and simple renderers.
        public List<List<double[]>> Polylines(string role = null, int curveSteps = 8)
        {
            List<List<double[]>> lines = new List<List<double[]>>();
            foreach (LineArtPath path in Paths)
            {
                if (role != null && !string.Equals(path.Role, role, StringComparison.OrdinalIgnoreCase))
                    continue;
                lines.AddRange(Flatten(ParsePath(path.D), curveSteps));
            }
            return lines;
        }

        public static List<List<double[]>> Flatten(List<LineArtCommand> commands, int curveSteps)
        {
            List<List<double[]>> lines = new List<List<double[]>>();
            List<double[]> current = null;
            double[] start = null, pos = { 0, 0 };
            foreach (LineArtCommand c in commands)
            {
                switch (c.Op)
                {
                    case 'M':
                        if (current != null && current.Count > 1)
                            lines.Add(current);
                        pos = new[] { c.P[0], c.P[1] };
                        start = pos;
                        current = new List<double[]> { pos };
                        break;
                    case 'L':
                        pos = new[] { c.P[0], c.P[1] };
                        current.Add(pos);
                        break;
                    case 'Q':
                        for (int k = 1; k <= curveSteps; k++)
                        {
                            double u = k / (double)curveSteps, v = 1 - u;
                            current.Add(new[] { v * v * pos[0] + 2 * v * u * c.P[0] + u * u * c.P[2], v * v * pos[1] + 2 * v * u * c.P[1] + u * u * c.P[3] });
                        }
                        pos = new[] { c.P[2], c.P[3] };
                        break;
                    case 'C':
                        for (int k = 1; k <= curveSteps; k++)
                        {
                            double u = k / (double)curveSteps, v = 1 - u;
                            current.Add(new[]
                            {
                                v * v * v * pos[0] + 3 * v * v * u * c.P[0] + 3 * v * u * u * c.P[2] + u * u * u * c.P[4],
                                v * v * v * pos[1] + 3 * v * v * u * c.P[1] + 3 * v * u * u * c.P[3] + u * u * u * c.P[5]
                            });
                        }
                        pos = new[] { c.P[4], c.P[5] };
                        break;
                    case 'Z':
                        if (current != null && start != null)
                        {
                            current.Add(start);
                            pos = start;
                        }
                        break;
                }
            }
            if (current != null && current.Count > 1)
                lines.Add(current);
            return lines;
        }

        // Whether a point (0..1 of the picture) is on the device: inside the silhouette (even-odd over the outline
        // paths), or within margin (0..1 of the picture's longer side) of it, for parts on the edge.
        public bool Covers(double x, double y, double margin)
        {
            double px = x * Width, py = y * Height, reach = margin * Math.Max(Width, Height);
            List<List<double[]>> outlines = Polylines("outline");
            bool inside = false;
            double best = double.MaxValue;
            foreach (List<double[]> line in outlines)
            {
                for (int i = 0, j = line.Count - 1; i < line.Count; j = i++)
                {
                    double[] a = line[i], b = line[j];
                    if ((a[1] > py) != (b[1] > py) && px < (b[0] - a[0]) * (py - a[1]) / (b[1] - a[1]) + a[0])
                        inside = !inside;
                    best = Math.Min(best, DistanceToSegment(px, py, a, b));
                }
            }
            if (inside || best <= reach)
                return true;
            // A drawing with colour regions: a part on any region (a lever in front of the body, whose outline the
            // body's silhouette does not enclose) is on the device too.
            foreach (LineArtFill fill in Fills)
            {
                foreach (List<double[]> line in Flatten(ParsePath(fill.D), 8))
                {
                    bool own = false;
                    for (int i = 0, j = line.Count - 1; i < line.Count; j = i++)
                    {
                        double[] a = line[i], b = line[j];
                        if ((a[1] > py) != (b[1] > py) && px < (b[0] - a[0]) * (py - a[1]) / (b[1] - a[1]) + a[0])
                            own = !own;
                    }
                    if (own)
                        return true;
                }
            }
            return false;
        }

        private static double DistanceToSegment(double px, double py, double[] a, double[] b)
        {
            double dx = b[0] - a[0], dy = b[1] - a[1];
            double len = dx * dx + dy * dy;
            double t = len <= 0 ? 0 : Math.Max(0, Math.Min(1, ((px - a[0]) * dx + (py - a[1]) * dy) / len));
            double cx = a[0] + t * dx - px, cy = a[1] + t * dy - py;
            return Math.Sqrt(cx * cx + cy * cy);
        }

        // The stroke widths of a style: the cel style's outlines are bolder; "minor" lines (fine inner detail) are drawn
        // at 0.65 of the detail width, so the silhouette, the controls and the fine lines read as three weights.
        public double StrokeWidth(LineArtPath path, bool cel)
        {
            double width = path.IsOutline ? OutlineWidth : path.IsMinor ? DetailWidth * 0.65 : DetailWidth;
            return cel && HasFills ? width * (path.IsOutline ? 1.6 : 1.25) : width;
        }

        // An SVG of the drawing in a style (the line style, or the cel style when the drawing has fills).
        public string ToSvg(DrawingColors colors)
        {
            bool cel = colors.Cel && HasFills;
            string ink = colors.StrokeFor(HasFills), paper = colors.Paper;
            StringBuilder sb = new StringBuilder();
            string w = Width.ToString("0.##", CultureInfo.InvariantCulture), h = Height.ToString("0.##", CultureInfo.InvariantCulture);
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(w).Append(' ').Append(h).Append("\" width=\"").Append(w).Append("\" height=\"").Append(h).Append("\">\n");
            sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"").Append(paper).Append("\"/>\n");
            if (cel)
            {
                foreach (LineArtFill fill in Fills)
                    sb.Append("<path fill=\"").Append(fill.Color).Append("\" fill-rule=\"").Append(fill.EvenOdd ? "evenodd" : "nonzero").Append("\" d=\"").Append(fill.D).Append("\"/>\n");
            }
            sb.Append("<g fill=\"none\" stroke=\"").Append(ink).Append("\" stroke-linecap=\"round\" stroke-linejoin=\"round\">\n");
            foreach (LineArtPath path in Paths)
            {
                sb.Append("<path stroke-width=\"").Append(StrokeWidth(path, cel).ToString("0.##", CultureInfo.InvariantCulture)).Append('"');
                string own = colors.StrokeFor(HasFills, path.IsOutline);
                if (own != ink)
                    sb.Append(" stroke=\"").Append(own).Append('"');
                sb.Append(" d=\"").Append(path.D).Append("\"/>\n");
            }
            sb.Append("</g>\n</svg>\n");
            return sb.ToString();
        }

        public string ToSvg(bool dark)
        {
            return ToSvg(new DrawingStyle { Mode = DrawingStyle.Lines }.Resolve(dark));
        }
    }

    public sealed class LineArtFill
    {
        public string Color = "#3A3A3A";
        public string D = string.Empty;
        public bool EvenOdd = true;
    }

    public sealed class LineArtPath
    {
        // outline (the silhouette, bolder), detail (inner contours: controls, panels) or minor (fine lines, thinner).
        public string Role = "detail";
        public string D = string.Empty;

        public bool IsOutline { get { return string.Equals(Role, "outline", StringComparison.OrdinalIgnoreCase); } }

        public bool IsMinor { get { return string.Equals(Role, "minor", StringComparison.OrdinalIgnoreCase); } }
    }

    public struct LineArtCommand
    {
        public char Op;
        public double[] P;
    }

    // The shipped line drawings: DB\DeviceDrawings (devices) and DB\Cockpits\line-drawings (cockpit grips).
    public static class DeviceLineArtDb
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, List<DeviceLineArt>> Cache = new Dictionary<string, List<DeviceLineArt>>(StringComparer.OrdinalIgnoreCase);

        // The drawing folder next to a catalog folder (DB\Devices -> DB\DeviceDrawings).
        public static string FolderOf(string catalogFolder)
        {
            string parent = string.IsNullOrWhiteSpace(catalogFolder) ? null : Path.GetDirectoryName(Path.GetFullPath(catalogFolder).TrimEnd('\\', '/'));
            return parent == null ? string.Empty : Path.Combine(parent, DeviceLineArt.FolderName);
        }

        // DB\Cockpits -> DB\Cockpits\line-drawings.
        public static string CockpitFolderOf(string cockpitFolder)
        {
            return string.IsNullOrWhiteSpace(cockpitFolder) ? string.Empty : Path.Combine(cockpitFolder, DeviceLineArt.CockpitFolderName);
        }

        public static List<DeviceLineArt> LoadFolder(string folder, List<string> warnings)
        {
            List<DeviceLineArt> list = new List<DeviceLineArt>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return list;
            foreach (string file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    list.Add(DeviceLineArt.Parse(System.IO.File.ReadAllText(file, Encoding.UTF8), file));
                }
                catch (Exception ex)
                {
                    if (warnings != null)
                        warnings.Add("Line drawing '" + Path.GetFileName(file) + "' skipped: " + ex.Message);
                }
            }
            return list;
        }

        // A folder's drawings, read once per process (the folder is part of the program and does not change).
        public static List<DeviceLineArt> Cached(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return new List<DeviceLineArt>();
            lock (Gate)
            {
                List<DeviceLineArt> list;
                if (!Cache.TryGetValue(folder, out list))
                {
                    list = LoadFolder(folder, null);
                    Cache[folder] = list;
                }
                return list;
            }
        }

        // The drawings of a model, the most neutral view first.
        public static List<DeviceLineArt> OfModel(IEnumerable<DeviceLineArt> all, string model)
        {
            return all.Where(a => string.Equals(a.Model, model, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => ViewRank(a.View)).ThenBy(a => a.View, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The cockpit grip drawings of an aircraft's device (stick, throttle, cyclic, collective, yoke), or none.
        public static List<DeviceLineArt> OfCockpit(IEnumerable<DeviceLineArt> all, string aircraft, string device)
        {
            return all.Where(a => string.Equals(a.Aircraft, aircraft, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(a.Device, device, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Whether a drawing belongs to a picture key: the photo's SHA-256 it was made on (by its opaque key), or the
        // drawing's own key (a drawing JoyPro authored in its own frame: its positions are kept under its key).
        public static bool Matches(DeviceLineArt art, string pictureKey)
        {
            if (art == null || string.IsNullOrWhiteSpace(pictureKey) || art.Key.Length == 0)
                return false;
            return string.Equals(art.Key, pictureKey.Trim(), StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(DeviceLineArt.OpaqueKey(pictureKey), art.Key, StringComparison.OrdinalIgnoreCase);
        }

        // The drawing made from exactly this photo (its SHA-256), or the drawing with this own key, or null.
        public static DeviceLineArt OfPhoto(IEnumerable<DeviceLineArt> all, string model, string photoSha256)
        {
            if (string.IsNullOrWhiteSpace(photoSha256))
                return null;
            return all.FirstOrDefault(a => string.Equals(a.Model, model, StringComparison.OrdinalIgnoreCase) && Matches(a, photoSha256));
        }

        // The photo a drawing was made from, among the given content hashes (curated sets, photo references; a set on
        // a drawing in JoyPro's own frame names the drawing's key instead); empty when none of them matches.
        public static string PhotoHashOf(DeviceLineArt art, IEnumerable<string> hashes)
        {
            if (art == null || art.Key.Length == 0)
                return string.Empty;
            foreach (string hash in hashes)
            {
                if (Matches(art, hash))
                    return hash.Trim().ToLowerInvariant();
            }
            return string.Empty;
        }

        public static int ViewRank(string view)
        {
            switch ((view ?? string.Empty).ToLowerInvariant())
            {
                case "main": return 0;
                case "three-quarter": return 1;
                case "front": return 2;
                case "side": return 3;
                case "left": return 4;
                case "right": return 5;
                case "top": return 6;
                case "back": return 7;
                case "base": return 8;
                case "sheet": return 20;
                default: return 10;
            }
        }
    }
}
