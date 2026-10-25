#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JoyPro.DeviceReference
{
    // A modifier and the colour its markers and columns get on the cheat sheets ("LAlt" -> "#FF38BDF8").
    public sealed class SheetModifierColor
    {
        public string Name = string.Empty;
        public string Color = string.Empty;

        public SheetModifierColor()
        {
        }

        public SheetModifierColor(string name, string color)
        {
            Name = name ?? string.Empty;
            Color = color ?? string.Empty;
        }
    }

    // The look of the cheat sheets (docs/ui/layout-redesign.md, "Sheet style"), the same in every renderer: the Layout
    // page's editor (CalloutSheetCanvas, DeviceMarkerCanvas, the layout file's tables), the cheat sheet preview and the
    // engine's "layout render" / "layout callouts". Kept by the GUI as the global style (<settings>\sheet-style.json),
    // as a device's own style inside its sheet ("style" of the callouts, a ".style.json" next to a .layout file) and as
    // named presets (<data>\SheetStyles\*.json); the engine gets it as --sheet-style / the single options.
    //   Colours: "#AARRGGBB" or "#RRGGBB"; empty = not set (the base palette of the sheet, the layout file's own label
    //   colour, the engine's marker colours). An alpha of 0 is "none" (no outline, no label box).
    public sealed class SheetStyle
    {
        public const string FormatName = "joypro-sheet-style";
        // The size the sheet's tables are made for: a size of 16 draws them as they are, 24 makes the text 1.5 times as big.
        public const double BaseFontSize = 16;

        public static readonly string[] Weights = { "normal", "semibold", "bold" };
        public static readonly string[] PresetIds = { "joypro", "print", "high-contrast", "dark-sheet" };

        public string Name = string.Empty;
        // Empty = JoyPro's font (Bahnschrift SemiCondensed on the tables, the layout file's font on its labels).
        public string FontFamily = string.Empty;
        // 0 = not set.
        public double FontSize;
        public string FontWeight = string.Empty;
        // The bound commands written on a layout picture (JoyPro 1's labels): text, halo, box behind them.
        public string TextColor = string.Empty;
        public string OutlineColor = string.Empty;
        public string LabelBackground = string.Empty;
        // The sheet: page, tables, their title row, the text in them, the leader lines.
        public string PageBackground = string.Empty;
        public string TableBackground = string.Empty;
        public string TableBorder = string.Empty;
        public string HeaderBackground = string.Empty;
        public string HeaderText = string.Empty;
        public string TableText = string.Empty;
        public string LineColor = string.Empty;
        // The markers on a picture by the kind of input.
        public string MarkerButton = string.Empty;
        public string MarkerAxis = string.Empty;
        public string MarkerHat = string.Empty;
        public string MarkerSlider = string.Empty;
        public List<SheetModifierColor> Modifiers = new List<SheetModifierColor>();
        // JoyPro's own drawing of the device on the sheet (DrawingStyle spec); empty = the export drawing style.
        public string Drawing = string.Empty;

        public SheetStyle Clone()
        {
            SheetStyle copy = (SheetStyle)MemberwiseClone();
            copy.Modifiers = Modifiers.Select(m => new SheetModifierColor(m.Name, m.Color)).ToList();
            return copy;
        }

        // ------------------------------------------------------------------ presets

        public static readonly string[] DefaultModifierCsv = { "LAlt=#FF38BDF8", "LCtrl=#FFF59E0B", "LShift=#FF34D399", "RAlt=#FFA78BFA", "RShift=#FFFB7185" };

        // JoyPro's look (JoyPro 1's grey page, white tables), a white page to print, high contrast, dark tables.
        public static SheetStyle Preset(string id)
        {
            string key = (id ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
            SheetStyle s = new SheetStyle
            {
                Name = key,
                FontFamily = string.Empty,
                FontSize = BaseFontSize,
                FontWeight = "normal",
                TextColor = "#FFFFFFFF",
                OutlineColor = "#FF000000",
                LabelBackground = "#88000000",
                PageBackground = "#FF2B3038",
                TableBackground = "#FFF7F7F7",
                TableBorder = "#FF0E1116",
                HeaderBackground = "#FFD5DAE1",
                HeaderText = "#FF0E1116",
                TableText = "#FF0E1116",
                LineColor = "#FFE6E8EB",
                MarkerButton = "#FF22C55E",
                MarkerAxis = "#FF38BDF8",
                MarkerHat = "#FFA855F7",
                MarkerSlider = "#FFFBBF24",
                Modifiers = ParseModifierCsv(string.Join(",", DefaultModifierCsv)),
                Drawing = "mode=cel;preset=joypro"
            };
            switch (key)
            {
                case "print":
                case "light":
                    s.Name = "print";
                    s.TextColor = "#FF111418";
                    s.OutlineColor = "#00000000";
                    s.LabelBackground = "#00000000";
                    s.PageBackground = "#FFFFFFFF";
                    s.TableBackground = "#FFFFFFFF";
                    s.TableBorder = "#FF2F3640";
                    s.HeaderBackground = "#FFE3E7ED";
                    s.HeaderText = "#FF111418";
                    s.TableText = "#FF111418";
                    s.LineColor = "#FF7C8794";
                    s.Drawing = "mode=lines;preset=print";
                    break;
                case "high-contrast":
                case "contrast":
                    s.Name = "high-contrast";
                    s.FontWeight = "semibold";
                    s.TextColor = "#FFFFFFFF";
                    s.OutlineColor = "#FF000000";
                    s.LabelBackground = "#FF000000";
                    s.PageBackground = "#FF000000";
                    s.TableBackground = "#FFFFFFFF";
                    s.TableBorder = "#FF000000";
                    s.HeaderBackground = "#FFFFD400";
                    s.HeaderText = "#FF000000";
                    s.TableText = "#FF000000";
                    s.LineColor = "#FFFFD400";
                    s.MarkerButton = "#FF00E676";
                    s.MarkerAxis = "#FF00B0FF";
                    s.MarkerHat = "#FFFF4081";
                    s.MarkerSlider = "#FFFFEA00";
                    s.Modifiers = ParseModifierCsv("LAlt=#FF00E5FF,LCtrl=#FFFFAB00,LShift=#FF76FF03,RAlt=#FFE040FB,RShift=#FFFF5252");
                    s.Drawing = "mode=lines;preset=print";
                    break;
                case "dark-sheet":
                case "dark":
                    s.Name = "dark-sheet";
                    s.TextColor = "#FFF8FAFC";
                    s.OutlineColor = "#FF000000";
                    s.LabelBackground = "#99000000";
                    s.PageBackground = "#FF0F172A";
                    s.TableBackground = "#FF1E293B";
                    s.TableBorder = "#FF475569";
                    s.HeaderBackground = "#FF334155";
                    s.HeaderText = "#FFF8FAFC";
                    s.TableText = "#FFE2E8F0";
                    s.LineColor = "#FF38BDF8";
                    s.Drawing = "mode=cel;preset=manual";
                    break;
                default:
                    s.Name = "joypro";
                    break;
            }
            return s;
        }

        public static bool IsPresetId(string id)
        {
            string key = (id ?? string.Empty).Trim().ToLowerInvariant();
            return PresetIds.Contains(key) || key == "light" || key == "dark" || key == "contrast";
        }

        // ------------------------------------------------------------------ values

        // "LAlt=#38BDF8,LCtrl=#F59E0B" (the old --modifier-colors text) <-> the list.
        public static List<SheetModifierColor> ParseModifierCsv(string csv)
        {
            List<SheetModifierColor> list = new List<SheetModifierColor>();
            foreach (string part in (csv ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int split = part.IndexOf('=');
                if (split <= 0 || split >= part.Length - 1)
                    continue;
                string name = part.Substring(0, split).Trim();
                string color = NormalizeColor(part.Substring(split + 1));
                if (name.Length == 0 || color.Length == 0)
                    continue;
                list.RemoveAll(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                list.Add(new SheetModifierColor(name, color));
            }
            return list;
        }

        public string ModifierCsv
        {
            get
            {
                return string.Join(",", Modifiers
                    .Where(m => m != null && (m.Name ?? string.Empty).Trim().Length > 0 && NormalizeColor(m.Color).Length > 0)
                    .Select(m => m.Name.Trim() + "=" + ShortHex(NormalizeColor(m.Color))));
            }
        }

        // "#RRGGBB" for an opaque colour, else "#AARRGGBB".
        public static string ShortHex(string color)
        {
            string c = NormalizeColor(color);
            return c.Length == 9 && c.StartsWith("#FF", StringComparison.Ordinal) ? "#" + c.Substring(3) : c;
        }

        // The colour of a modifier (the first of a combination "LAlt+LCtrl" that has one); "" = none.
        public string ModifierColor(string combo)
        {
            foreach (string name in (combo ?? string.Empty).Split(new[] { '+', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                SheetModifierColor found = Modifiers.FirstOrDefault(m => m != null && string.Equals((m.Name ?? string.Empty).Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (found != null && NormalizeColor(found.Color).Length > 0)
                    return NormalizeColor(found.Color);
            }
            return string.Empty;
        }

        // The marker colour of an input kind (button, axis, pov/hat, slider); "" = not set.
        public string MarkerColor(string kind)
        {
            switch ((kind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "axis":
                    return NormalizeColor(MarkerAxis);
                case "pov":
                case "hat":
                    return NormalizeColor(MarkerHat);
                case "slider":
                    return NormalizeColor(MarkerSlider);
                default:
                    return NormalizeColor(MarkerButton);
            }
        }

        public string NormalizedWeight
        {
            get
            {
                string w = (FontWeight ?? string.Empty).Trim().ToLowerInvariant().Replace("-", string.Empty).Replace(" ", string.Empty);
                if (w == "semibold" || w == "demibold" || w == "600")
                    return "semibold";
                if (w == "bold" || w == "700" || w == "extrabold" || w == "black" || w == "800" || w == "900")
                    return "bold";
                return w.Length == 0 ? string.Empty : "normal";
            }
        }

        public bool IsBold { get { return NormalizedWeight == "semibold" || NormalizedWeight == "bold"; } }

        // How much bigger than made the tables' text is drawn (0.6 .. 1.6; 1 without a size).
        public double TextScale
        {
            get { return FontSize > 0 ? Math.Max(0.6, Math.Min(1.6, FontSize / BaseFontSize)) : 1.0; }
        }

        // A dark page (the drawing on it and the line glow follow): the page colour, else the base given.
        public bool IsDarkPage(bool fallback)
        {
            uint page;
            return TryParse(PageBackground, out page) ? Luminance(page) < 0.45 : fallback;
        }

        // ------------------------------------------------------------------ colours

        // "#AARRGGBB" (upper case) of "#rgb", "#rrggbb", "#aarrggbb" (with or without '#'); "none" -> "#00000000";
        // "" when it is no colour.
        public static string NormalizeColor(string value)
        {
            uint c;
            return TryParse(value, out c) ? Hex(c) : string.Empty;
        }

        public static bool TryParse(string value, out uint argb)
        {
            argb = 0;
            string text = (value ?? string.Empty).Trim();
            if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "transparent", StringComparison.OrdinalIgnoreCase))
                return true;
            string hex = text.TrimStart('#');
            if (hex.Length == 3)
                hex = string.Concat(hex.Select(ch => new string(ch, 2)));
            if (hex.Length == 6)
                hex = "FF" + hex;
            if (hex.Length != 8)
                return false;
            return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out argb);
        }

        public static string Hex(uint argb)
        {
            return "#" + argb.ToString("X8", CultureInfo.InvariantCulture);
        }

        public static byte A(uint c) { return (byte)(c >> 24); }
        public static byte R(uint c) { return (byte)(c >> 16); }
        public static byte G(uint c) { return (byte)(c >> 8); }
        public static byte B(uint c) { return (byte)c; }

        public static uint Argb(byte a, byte r, byte g, byte b)
        {
            return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        }

        // Relative luminance (sRGB, 0..1).
        public static double Luminance(uint c)
        {
            Func<byte, double> lin = v =>
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            };
            return 0.2126 * lin(R(c)) + 0.7152 * lin(G(c)) + 0.0722 * lin(B(c));
        }

        // a moved towards b by t (opaque).
        public static uint Mix(uint a, uint b, double t)
        {
            Func<byte, byte, byte> m = (x, y) => (byte)Math.Round(x + (y - x) * t);
            return Argb(255, m(R(a), R(b)), m(G(a), G(b)), m(B(a), B(b)));
        }

        // Light text on a dark colour, dark text on a light one.
        public static uint Readable(uint background)
        {
            return Luminance(background) < 0.4 ? 0xFFF2F4F7u : 0xFF111418u;
        }

        // ------------------------------------------------------------------ the palette a sheet is drawn with

        // The sheet's palette: the base palette (JoyPro 1's dark grey page or the white page) with the style's colours
        // over it. Colours the style does not set and colours made from others (the modifier column, the grid lines, the
        // faint text) come from the base, so a style with the base's own colours draws exactly the base sheet.
        public SheetPalette Resolve(bool dark)
        {
            SheetPalette p = SheetPalette.Base(dark);
            SheetPalette basePalette = SheetPalette.Base(dark);
            uint c;
            if (TryParse(PageBackground, out c))
            {
                p.Page = c;
                bool darkPage = Luminance(c) < 0.45;
                p.LineGlow = darkPage ? 0x5A000000u : 0u;
                p.OtherText = Readable(c);
            }
            if (TryParse(TableBackground, out c))
            {
                p.Table = c;
                p.BandBox = c;
            }
            if (TryParse(HeaderBackground, out c))
                p.Title = c;
            if (TryParse(TableText, out c))
            {
                p.Text = c;
                p.TitleText = c;
                p.BandBoxText = c;
            }
            if (TryParse(HeaderText, out c))
                p.TitleText = c;
            if (TryParse(TableBorder, out c))
            {
                p.Border = c;
                if (c != basePalette.Border)
                {
                    p.Band = c;
                    p.BandText = Readable(c);
                }
            }
            if (TryParse(LineColor, out c))
                p.Line = c;
            if (p.Table != basePalette.Table || p.Title != basePalette.Title)
                p.Mod = Mix(p.Table, p.Title, 0.42);
            if (p.Table != basePalette.Table || p.Border != basePalette.Border)
                p.Grid = Mix(p.Table, p.Border, 0.4);
            if (p.Table != basePalette.Table || p.Text != basePalette.Text)
                p.Faint = Mix(p.Table, p.Text, 0.45);
            return p;
        }

        // ------------------------------------------------------------------ JSON

        public Dictionary<string, object> ToJsonObject()
        {
            Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal);
            root["format"] = FormatName;
            root["version"] = 1;
            if (Name.Length > 0)
                root["name"] = Name;
            if (FontFamily.Length > 0)
                root["fontFamily"] = FontFamily;
            if (FontSize > 0)
                root["fontSize"] = Math.Round(FontSize, 2);
            if (NormalizedWeight.Length > 0)
                root["fontWeight"] = NormalizedWeight;
            Put(root, "textColor", TextColor);
            Put(root, "outlineColor", OutlineColor);
            Put(root, "labelBackground", LabelBackground);
            Put(root, "pageBackground", PageBackground);
            Put(root, "tableBackground", TableBackground);
            Put(root, "tableBorder", TableBorder);
            Put(root, "headerBackground", HeaderBackground);
            Put(root, "headerText", HeaderText);
            Put(root, "tableText", TableText);
            Put(root, "lineColor", LineColor);
            Put(root, "markerButton", MarkerButton);
            Put(root, "markerAxis", MarkerAxis);
            Put(root, "markerHat", MarkerHat);
            Put(root, "markerSlider", MarkerSlider);
            List<object> modifiers = new List<object>();
            foreach (SheetModifierColor m in Modifiers)
            {
                if (m == null || (m.Name ?? string.Empty).Trim().Length == 0 || NormalizeColor(m.Color).Length == 0)
                    continue;
                modifiers.Add(new Dictionary<string, object>(StringComparer.Ordinal) { { "name", m.Name.Trim() }, { "color", NormalizeColor(m.Color) } });
            }
            if (modifiers.Count > 0)
                root["modifiers"] = modifiers;
            if (Drawing.Length > 0)
                root["drawing"] = Drawing;
            return root;
        }

        private static void Put(Dictionary<string, object> root, string key, string color)
        {
            string value = NormalizeColor(color);
            if (value.Length > 0)
                root[key] = value;
        }

        public string ToJson()
        {
            return MiniJson.Write(ToJsonObject(), true);
        }

        public static SheetStyle FromJsonObject(Dictionary<string, object> root)
        {
            if (root == null)
                return null;
            SheetStyle s = new SheetStyle
            {
                Name = DeviceCatalog.Text(root, "name"),
                FontFamily = DeviceCatalog.Text(root, "fontFamily"),
                FontSize = Number(root, "fontSize"),
                FontWeight = DeviceCatalog.Text(root, "fontWeight"),
                TextColor = NormalizeColor(DeviceCatalog.Text(root, "textColor")),
                OutlineColor = NormalizeColor(DeviceCatalog.Text(root, "outlineColor")),
                LabelBackground = NormalizeColor(DeviceCatalog.Text(root, "labelBackground")),
                PageBackground = NormalizeColor(DeviceCatalog.Text(root, "pageBackground")),
                TableBackground = NormalizeColor(DeviceCatalog.Text(root, "tableBackground")),
                TableBorder = NormalizeColor(DeviceCatalog.Text(root, "tableBorder")),
                HeaderBackground = NormalizeColor(DeviceCatalog.Text(root, "headerBackground")),
                HeaderText = NormalizeColor(DeviceCatalog.Text(root, "headerText")),
                TableText = NormalizeColor(DeviceCatalog.Text(root, "tableText")),
                LineColor = NormalizeColor(DeviceCatalog.Text(root, "lineColor")),
                MarkerButton = NormalizeColor(DeviceCatalog.Text(root, "markerButton")),
                MarkerAxis = NormalizeColor(DeviceCatalog.Text(root, "markerAxis")),
                MarkerHat = NormalizeColor(DeviceCatalog.Text(root, "markerHat")),
                MarkerSlider = NormalizeColor(DeviceCatalog.Text(root, "markerSlider")),
                Drawing = DeviceCatalog.Text(root, "drawing")
            };
            if (s.FontSize < 0 || double.IsNaN(s.FontSize))
                s.FontSize = 0;
            s.FontSize = Math.Min(s.FontSize, 200);
            foreach (Dictionary<string, object> item in DeviceCatalog.Objects(root, "modifiers"))
            {
                string name = DeviceCatalog.Text(item, "name").Trim();
                string color = NormalizeColor(DeviceCatalog.Text(item, "color"));
                if (name.Length > 0 && color.Length > 0)
                    s.Modifiers.Add(new SheetModifierColor(name, color));
            }
            return s;
        }

        // A style file (or a style object's JSON); null when it is none.
        public static SheetStyle Parse(string json)
        {
            try
            {
                return FromJsonObject(MiniJson.Parse(json ?? string.Empty) as Dictionary<string, object>);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static double Number(Dictionary<string, object> item, string key)
        {
            object value;
            if (item == null || !item.TryGetValue(key, out value) || value == null)
                return 0;
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // This style with the set values of another over it (the command line's options over --sheet-style).
        public void Overlay(SheetStyle other)
        {
            if (other == null)
                return;
            if (other.FontFamily.Length > 0) FontFamily = other.FontFamily;
            if (other.FontSize > 0) FontSize = other.FontSize;
            if (other.FontWeight.Length > 0) FontWeight = other.FontWeight;
            if (other.TextColor.Length > 0) TextColor = other.TextColor;
            if (other.OutlineColor.Length > 0) OutlineColor = other.OutlineColor;
            if (other.LabelBackground.Length > 0) LabelBackground = other.LabelBackground;
            if (other.PageBackground.Length > 0) PageBackground = other.PageBackground;
            if (other.TableBackground.Length > 0) TableBackground = other.TableBackground;
            if (other.TableBorder.Length > 0) TableBorder = other.TableBorder;
            if (other.HeaderBackground.Length > 0) HeaderBackground = other.HeaderBackground;
            if (other.HeaderText.Length > 0) HeaderText = other.HeaderText;
            if (other.TableText.Length > 0) TableText = other.TableText;
            if (other.LineColor.Length > 0) LineColor = other.LineColor;
            if (other.MarkerButton.Length > 0) MarkerButton = other.MarkerButton;
            if (other.MarkerAxis.Length > 0) MarkerAxis = other.MarkerAxis;
            if (other.MarkerHat.Length > 0) MarkerHat = other.MarkerHat;
            if (other.MarkerSlider.Length > 0) MarkerSlider = other.MarkerSlider;
            if (other.Modifiers.Count > 0) Modifiers = other.Clone().Modifiers;
            if (other.Drawing.Length > 0) Drawing = other.Drawing;
        }

        public bool IsEmpty
        {
            get { return ToJsonObject().Count <= 2; }
        }
    }

    // The colours a sheet is drawn with (ARGB), shared by the engine's GDI+ renderer and the GUI's canvases.
    public sealed class SheetPalette
    {
        public uint Page, Table, Title, TitleText, Mod, Border, Grid, Text, Faint, Line, LineGlow, Band, BandText, BandBox, BandBoxText, PictureBack, OtherText;

        // JoyPro 1's dark grey page with white tables, or the white page (the engine's --style dark|light).
        public static SheetPalette Base(bool dark)
        {
            if (dark)
            {
                return new SheetPalette
                {
                    Page = 0xFF2B3038, Table = 0xFFF7F7F7, Title = 0xFFD5DAE1, TitleText = 0xFF0E1116, Mod = 0xFFE9ECF0, Border = 0xFF0E1116,
                    Grid = 0xFF9AA1AB, Text = 0xFF0E1116, Faint = 0xFF8A919B, Line = 0xFFE6E8EB, LineGlow = 0x5A000000,
                    Band = 0xFF15181D, BandText = 0xFFF2F4F7, BandBox = 0xFFF7F7F7, BandBoxText = 0xFF0E1116, PictureBack = 0xFF0B1220, OtherText = 0xFFF2F4F7
                };
            }
            return new SheetPalette
            {
                Page = 0xFFFFFFFF, Table = 0xFFFFFFFF, Title = 0xFFE3E7ED, TitleText = 0xFF111418, Mod = 0xFFF3F5F8, Border = 0xFF2F3640,
                Grid = 0xFFB7BDC6, Text = 0xFF111418, Faint = 0xFF9AA1AB, Line = 0xFF7C8794, LineGlow = 0x00000000,
                Band = 0xFF2F3640, BandText = 0xFFFFFFFF, BandBox = 0xFFFFFFFF, BandBoxText = 0xFF111418, PictureBack = 0xFFEEF2F7, OtherText = 0xFF111418
            };
        }
    }
}
