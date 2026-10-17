#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JoyPro.DeviceReference
{
    // How JoyPro's own device drawings (DeviceLineArt) are painted, the same in every renderer (the Layout page's
    // canvas, the device cards, the callout sheets, the engine's exports): docs/device-reference.md, "Drawing style".
    //   Mode   lines (strokes only) or cel (flat colour fills under bold outlines);
    //   Preset joypro (the theme's colours), manual (white lines on near-black), blueprint, print (black on white),
    //          custom (the colours below);
    //   Ink    line colour, Paper background colour, Accent leader lines and labels (empty = the preset's).
    // Written as a spec string "mode=cel;preset=manual;ink=#F2F2F2;paper=#0B0F14;accent=#C8C8C8" (settings, CLI).
    public sealed class DrawingStyle
    {
        public const string Lines = "lines";
        public const string Cel = "cel";

        public static readonly string[] Presets = { "joypro", "manual", "blueprint", "print", "custom" };

        public string Mode = Cel;
        public string Preset = "joypro";
        public string Ink = string.Empty;
        public string Paper = string.Empty;
        public string Accent = string.Empty;

        public bool IsCel { get { return string.Equals(Mode, Cel, StringComparison.OrdinalIgnoreCase); } }

        public static DrawingStyle Default { get { return new DrawingStyle(); } }

        public static DrawingStyle Parse(string spec)
        {
            DrawingStyle style = new DrawingStyle();
            if (string.IsNullOrWhiteSpace(spec))
                return style;
            foreach (string part in spec.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                string key = (eq < 0 ? part : part.Substring(0, eq)).Trim().ToLowerInvariant();
                string value = eq < 0 ? string.Empty : part.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "mode":
                    case "style":
                        style.Mode = string.Equals(value, Lines, StringComparison.OrdinalIgnoreCase) ? Lines : Cel;
                        break;
                    case "preset":
                        style.Preset = Presets.Contains(value.ToLowerInvariant()) ? value.ToLowerInvariant() : "joypro";
                        break;
                    case "ink":
                        style.Ink = NormalizeColor(value);
                        break;
                    case "paper":
                        style.Paper = NormalizeColor(value);
                        break;
                    case "accent":
                        style.Accent = NormalizeColor(value);
                        break;
                    case "lines":
                        style.Mode = Lines;
                        break;
                    case "cel":
                        style.Mode = Cel;
                        break;
                }
            }
            return style;
        }

        public override string ToString()
        {
            List<string> parts = new List<string> { "mode=" + Mode, "preset=" + Preset };
            if (Ink.Length > 0)
                parts.Add("ink=" + Ink);
            if (Paper.Length > 0)
                parts.Add("paper=" + Paper);
            if (Accent.Length > 0)
                parts.Add("accent=" + Accent);
            return string.Join(";", parts);
        }

        public DrawingStyle Clone()
        {
            return (DrawingStyle)MemberwiseClone();
        }

        // The user's choice from the GUI's settings (<data>\DeviceReference\settings.json): "DrawingStyle" for the
        // screen, "ExportDrawingStyle" for exports and cheat sheets (empty = the screen's). The engine uses it when no
        // --drawing-* option is given.
        public static DrawingStyle FromSettings(string dataRoot, bool export)
        {
            try
            {
                string file = System.IO.Path.Combine(dataRoot ?? string.Empty, "DeviceReference", "settings.json");
                if (!System.IO.File.Exists(file))
                    return Default;
                Dictionary<string, object> root = MiniJson.Parse(System.IO.File.ReadAllText(file, System.Text.Encoding.UTF8)) as Dictionary<string, object>;
                string screen = Setting(root, "DrawingStyle"), exported = Setting(root, "ExportDrawingStyle");
                return Parse(export && exported.Length > 0 ? exported : screen);
            }
            catch (Exception)
            {
                return Default;
            }
        }

        private static string Setting(Dictionary<string, object> root, string name)
        {
            if (root == null)
                return string.Empty;
            foreach (KeyValuePair<string, object> pair in root)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase) && pair.Value is string)
                    return (string)pair.Value;
            }
            return string.Empty;
        }

        // Command-line options (--drawing-style <spec or lines|cel>, --drawing-preset, --drawing-ink, --drawing-paper,
        // --drawing-accent) over a base style; the options are removed from the list. Null when none was given.
        public static DrawingStyle FromOptions(List<string> args, DrawingStyle baseStyle)
        {
            DrawingStyle style = (baseStyle ?? Default).Clone();
            bool any = false;
            for (int i = 0; i < args.Count - 1; i++)
            {
                string key = args[i].ToLowerInvariant(), value = args[i + 1];
                bool known = true;
                switch (key)
                {
                    case "--drawing-style":
                        if (value.IndexOf('=') >= 0)
                        {
                            DrawingStyle parsed = Parse(value);
                            style.Mode = parsed.Mode;
                            style.Preset = parsed.Preset;
                            style.Ink = parsed.Ink.Length > 0 ? parsed.Ink : style.Ink;
                            style.Paper = parsed.Paper.Length > 0 ? parsed.Paper : style.Paper;
                            style.Accent = parsed.Accent.Length > 0 ? parsed.Accent : style.Accent;
                        }
                        else
                        {
                            style.Mode = string.Equals(value, Lines, StringComparison.OrdinalIgnoreCase) ? Lines : Cel;
                        }
                        break;
                    case "--drawing-preset":
                        style.Preset = Presets.Contains(value.ToLowerInvariant()) ? value.ToLowerInvariant() : "joypro";
                        break;
                    case "--drawing-ink":
                        style.Ink = NormalizeColor(value);
                        break;
                    case "--drawing-paper":
                        style.Paper = NormalizeColor(value);
                        break;
                    case "--drawing-accent":
                        style.Accent = NormalizeColor(value);
                        break;
                    default:
                        known = false;
                        break;
                }
                if (known)
                {
                    args.RemoveAt(i + 1);
                    args.RemoveAt(i);
                    i--;
                    any = true;
                }
            }
            return any ? style : null;
        }

        // "#RRGGBB" (upper case) of "#rgb", "#rrggbb" or "rrggbb"; empty when it is no colour.
        public static string NormalizeColor(string value)
        {
            string hex = (value ?? string.Empty).Trim().TrimStart('#');
            if (hex.Length == 3)
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            if (hex.Length == 8)
                hex = hex.Substring(2); // #AARRGGBB: the alpha is dropped
            int ignored;
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ignored))
                return string.Empty;
            return "#" + hex.ToUpperInvariant();
        }

        // The colours to paint with: the preset's (the joypro preset follows the theme), overridden by the user's own.
        public DrawingColors Resolve(bool dark)
        {
            DrawingColors colors;
            switch (Preset)
            {
                case "manual":
                    colors = new DrawingColors("#F2F2F2", "#0B0F14", "#C8C8C8");
                    break;
                case "blueprint":
                    colors = new DrawingColors("#EAF2FF", "#1D4E89", "#BFD7F5");
                    break;
                case "print":
                    colors = new DrawingColors("#111111", "#FFFFFF", "#333333");
                    break;
                default:
                    // JoyPro's theme: light lines on the dark surface with the accent blue, dark lines on the light one.
                    colors = dark ? new DrawingColors("#E6E9EF", "#0D1526", "#60A5FA") : new DrawingColors("#111827", "#F8FAFC", "#2563EB");
                    break;
            }
            if (Ink.Length > 0)
                colors.Ink = Ink;
            if (Paper.Length > 0)
                colors.Paper = Paper;
            if (Accent.Length > 0)
                colors.Accent = Accent;
            colors.Cel = IsCel;
            return colors;
        }

        public static int[] Rgb(string hex)
        {
            string h = NormalizeColor(hex);
            if (h.Length == 0)
                return new[] { 0, 0, 0 };
            return new[]
            {
                int.Parse(h.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(h.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(h.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            };
        }

        // Whether a colour reads as dark (relative luminance below ~43 %).
        public static bool IsDarkColor(string hex)
        {
            int[] c = Rgb(hex);
            return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2] < 110;
        }
    }

    public sealed class DrawingColors
    {
        public string Ink;
        public string Paper;
        public string Accent;
        public bool Cel;

        public DrawingColors(string ink, string paper, string accent)
        {
            Ink = ink;
            Paper = paper;
            Accent = accent;
        }

        // The stroke colour: the ink for the line style; bold near-black outlines over the cel fills (the silhouette in
        // the ink on a dark page, so the device stands out from it).
        public string StrokeFor(bool hasFills, bool outline = false)
        {
            if (!(Cel && hasFills))
                return Ink;
            return outline && DrawingStyle.IsDarkColor(Paper) ? Ink : "#141414";
        }
    }
}
