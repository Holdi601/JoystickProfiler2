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
    // The Cockpits page as a manual (docs/cockpits.md#the-cockpits-page): an overview of the cockpit with its numbered
    // areas, a page per grip (the drawing with numbered callouts and the HOTAS table: a row per control, direction, press
    // and condition, a column per mode or sensor of interest) and a page per panel (the drawing with numbered callouts and
    // the numbered list). Laid out here once, in page units, with a fixed text metric, so the GUI (Avalonia) and the engine
    // (PNG / PDF export) draw the same page.

    public sealed class CockpitArt
    {
        public DeviceLineArt LineArt;
        public DeviceSchematic Schematic;
        // A picture made from the user's game (PNG or JPEG, CockpitPictureSet): drawn as it is, in its own size.
        public string Picture = string.Empty;
        // A JPEG picture's transparency (8-bit grey PNG, CockpitPictureView.Mask); empty for none.
        public string Mask = string.Empty;
        public double PictureWidth;
        public double PictureHeight;

        public bool HasPicture { get { return Picture.Length > 0; } }

        public double Width { get { return HasPicture ? PictureWidth : LineArt != null ? LineArt.Width : Schematic != null ? Schematic.Width : 1; } }

        public double Height { get { return HasPicture ? PictureHeight : LineArt != null ? LineArt.Height : Schematic != null ? Schematic.Height : 1; } }
    }

    public sealed class ManualPageItem
    {
        // rect, text, line, dot, drawing.
        public string Kind = "rect";
        public double X;
        public double Y;
        public double W;
        public double H;
        public string Fill = string.Empty;
        public string Stroke = string.Empty;
        public double StrokeWidth = 1;
        public double Radius;
        // Text: the lines already wrapped to W at Size.
        public List<string> Lines = new List<string>();
        public double Size = 13;
        public bool Bold;
        // left, center.
        public string Align = "left";
        public string Color = string.Empty;
        // A line through these points (x0, y0, x1, y1, ...).
        public double[] Points;
        public CockpitArt Art;
        // The page an item leads to (an overview area to its panel page).
        public string Link = string.Empty;
        // What the item shows, for tooltips and the search ("fn:<id>", "control:<id>", "item:<panel>:<n>").
        public string Tag = string.Empty;
        public bool Highlight;
    }

    public sealed class ManualPage
    {
        // overview, device, panel.
        public string Kind = string.Empty;
        // overview, device:<device>, panel:<panel id>.
        public string Id = string.Empty;
        public string Title = string.Empty;
        public string Subtitle = string.Empty;
        public double Width = ManualLayout.PageWidth;
        public double Height;
        public string Paper = "#FFFFFF";
        public List<ManualPageItem> Items = new List<ManualPageItem>();
        // The text of the page (for the search): every control, row, cell and item.
        public string SearchText = string.Empty;
    }

    public sealed class ManualPageOptions
    {
        public bool Dark;
        // The user's drawing style (Settings > Drawings): the page's paper and ink and the drawings' look.
        public DrawingColors Colors;
        // (control, row, mode) -> a mark after the cell's text (Compare with my setup); null = none.
        public Func<CockpitControl, CockpitFunction, string, string> Mark;
        // Words to highlight (the search).
        public string Highlight = string.Empty;
        // The folder of DB\Cockpits (line drawings); empty = only JoyPro's schematic drawings.
        public string Folder = string.Empty;
        // The pictures made from the user's game for this variant (CockpitPictureSet.For); null = JoyPro's drawings.
        public CockpitPictureSet Pictures;
        // How the game pictures are shown: "picture" (the render) or "drawing" (JoyPro's line drawing of it, in the
        // user's drawing style).
        public string PictureMode = "picture";
        // The cockpit guide of the variant (CockpitGuideTree.Build): the game panels' titles and the callouts' names and
        // one-line explanations come from it; null = the game's own names.
        public CockpitGuideTree Guide;
    }

    public static class ManualLayout
    {
        public const double PageWidth = 1400;
        public const double Margin = 40;
        // The text metric every renderer lays out with: the mean glyph width of a sans-serif face per point.
        public const double CharWidth = 0.54;
        public const double BoldCharWidth = 0.58;
        public const double LineHeight = 1.28;

        public static double TextWidth(string text, double size, bool bold)
        {
            double w = 0;
            foreach (char ch in text ?? string.Empty)
                w += ch == ' ' ? 0.30 : "il.,:;'|!".IndexOf(ch) >= 0 ? 0.30 : "mwMW".IndexOf(ch) >= 0 ? 0.85 : char.IsUpper(ch) ? 0.66 : bold ? BoldCharWidth : CharWidth;
            return w * size;
        }

        // Word wrap to a width; a word longer than the line is cut.
        public static List<string> Wrap(string text, double width, double size, bool bold = false, int maxLines = 0)
        {
            List<string> lines = new List<string>();
            foreach (string paragraph in (text ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
            {
                string line = string.Empty;
                foreach (string word in paragraph.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string next = line.Length == 0 ? word : line + " " + word;
                    if (TextWidth(next, size, bold) <= width || line.Length == 0)
                    {
                        line = next;
                        while (TextWidth(line, size, bold) > width && line.Length > 4)
                        {
                            int cut = Math.Max(3, (int)(line.Length * width / TextWidth(line, size, bold)) - 1);
                            lines.Add(line.Substring(0, cut));
                            line = line.Substring(cut);
                        }
                    }
                    else
                    {
                        lines.Add(line);
                        line = word;
                    }
                }
                if (line.Length > 0 || lines.Count == 0)
                    lines.Add(line);
            }
            if (maxLines > 0 && lines.Count > maxLines)
            {
                lines = lines.Take(maxLines).ToList();
                lines[maxLines - 1] = lines[maxLines - 1].TrimEnd('.') + "...";
            }
            return lines;
        }

        public static double TextHeight(int lines, double size)
        {
            return Math.Max(1, lines) * size * LineHeight;
        }
    }

    // The page colours: the drawing style's paper and ink, the manual's callout orange, a dark table header and pastel
    // row colours per control (darker tints on a dark paper).
    public sealed class ManualPalette
    {
        public string Paper;
        public string Ink;
        public string Muted;
        public string Accent;
        public string Header;
        public string HeaderInk;
        public string Grid;
        public string[] Rows;
        public string[] RowsStrong;
        public string Mark;

        public static ManualPalette Of(ManualPageOptions o)
        {
            DrawingColors c = o.Colors ?? DrawingStyle.Default.Resolve(o.Dark);
            bool dark = DrawingStyle.IsDarkColor(c.Paper);
            return dark
                ? new ManualPalette
                {
                    Paper = c.Paper, Ink = "#E6EDF5", Muted = "#9AA9BC", Accent = "#F39200", Header = "#2E4260", HeaderInk = "#FFFFFF", Grid = "#4A5A70",
                    Rows = new[] { "#3B3520", "#1E3047", "#40242C", "#233A22", "#30284A", "#40301E", "#1D3836" },
                    RowsStrong = new[] { "#544A22", "#26405F", "#5A2F3A", "#2E4F2C", "#3F3463", "#5A4126", "#264B48" },
                    Mark = "#FFD34D"
                }
                : new ManualPalette
                {
                    Paper = c.Paper, Ink = "#18212D", Muted = "#5B6878", Accent = "#F39200", Header = "#2F3E56", HeaderInk = "#FFFFFF", Grid = "#8E99A8",
                    Rows = new[] { "#FFF4BF", "#DDEBFA", "#FCE1E7", "#E1F2DA", "#ECE3F8", "#FFE6CF", "#DAF2F0" },
                    RowsStrong = new[] { "#FFE680", "#B9D6F5", "#F7C1CE", "#C2E5B6", "#D5C4F0", "#FFCB9C", "#B3E4DF" },
                    Mark = "#FFE14D"
                };
        }
    }

    public static class CockpitManualPages
    {
        private const double TitleSize = 26;
        private const double BodySize = 14;
        private const double CellSize = 12.5;
        private const double CalloutWidth = 300;

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, DeviceLineArt> ArtCache = new Dictionary<string, DeviceLineArt>(StringComparer.OrdinalIgnoreCase);

        // A line drawing by id (DB\Cockpits\line-drawings\<id>.json), read once; null when there is none.
        public static DeviceLineArt LineArt(string cockpitFolder, string id)
        {
            if (string.IsNullOrWhiteSpace(cockpitFolder) || string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return null;
            string file = Path.Combine(DeviceLineArtDb.CockpitFolderOf(cockpitFolder), id + ".json");
            lock (Gate)
            {
                DeviceLineArt art;
                if (ArtCache.TryGetValue(file, out art))
                    return art;
                try
                {
                    art = File.Exists(file) ? DeviceLineArt.Parse(File.ReadAllText(file, Encoding.UTF8), file) : null;
                }
                catch (Exception)
                {
                    art = null;
                }
                if (ArtCache.Count >= 64)
                    ArtCache.Clear();
                ArtCache[file] = art;
                return art;
            }
        }

        // The line drawing of a grip of the variant: <variant>.<device>.json, else one of its aircraft for that device
        // that carries positions.
        public static DeviceLineArt DeviceArt(string cockpitFolder, CockpitVariant v, string device)
        {
            if (v == null || v.Aircraft == null || v.Aircraft.Fiction)
                return null;
            DeviceLineArt own = LineArt(cockpitFolder, v.Id + "." + device);
            if (own != null)
                return own;
            if (v.Inherits.Length > 0)
            {
                DeviceLineArt parent = LineArt(cockpitFolder, v.Inherits + "." + device);
                if (parent != null)
                    return parent;
            }
            if (v.SeatOf.Length > 0)
            {
                DeviceLineArt seat = LineArt(cockpitFolder, v.SeatOf + "." + device);
                if (seat != null)
                    return seat;
            }
            // The aircraft's grip (<aircraft>.<device>.json), read on its own: the folder also holds every panel drawing.
            DeviceLineArt aircraft = LineArt(cockpitFolder, v.Aircraft.Id + "." + device);
            return aircraft != null && aircraft.Parts.Count > 0 ? aircraft : null;
        }

        // Every line drawing a variant names exists (and none is of a fictional design).
        public static List<string> CheckDrawings(CockpitCatalog catalog)
        {
            List<string> problems = new List<string>();
            if (catalog == null || string.IsNullOrWhiteSpace(catalog.Folder))
                return problems;
            foreach (CockpitAircraft a in catalog.Aircraft)
            {
                foreach (CockpitVariant v in a.Variants)
                {
                    IEnumerable<string> ids = (v.Overview != null && v.Overview.Drawing.Length > 0 ? new[] { v.Overview.Drawing } : new string[0])
                        .Concat(v.Panels.Where(p => p.Drawing.Length > 0).Select(p => p.Drawing));
                    foreach (string id in ids)
                    {
                        if (LineArt(catalog.Folder, id) == null)
                            problems.Add(a.Id + "/" + v.Id + ": line drawing '" + id + "' does not exist (line-drawings/" + id + ".json)");
                    }
                }
            }
            return problems;
        }

        // ------------------------------------------------------------------ the pages of a variant

        public static List<ManualPage> Build(CockpitCatalog catalog, CockpitVariant v, ManualPageOptions o)
        {
            List<ManualPage> pages = new List<ManualPage>();
            if (catalog == null || v == null)
                return pages;
            o = o ?? new ManualPageOptions();
            List<CockpitPictureView> game = GameViews(v, o);
            pages.Add(game.Count > 0 ? GameOverview(catalog, v, o, game) : Overview(catalog, v, o));
            foreach (string device in CockpitCatalog.DevicesOf(v).Where(d => d != "panel"))
            {
                ManualPage page = Device(catalog, v, device, o);
                if (page != null)
                    pages.Add(page);
            }
            foreach (CockpitPictureView view in game.Where(x => x.Kind == "panel"))
                pages.Add(GamePanel(v, view, o));
            foreach (CockpitPanel p in v.Panels)
                pages.Add(Panel(catalog, v, p, o));
            return pages;
        }

        public static string PageTitle(ManualPage page)
        {
            return page.Title;
        }

        private static string Heading(CockpitVariant v)
        {
            string name = v.Aircraft == null ? v.Name : v.Aircraft.Name + " · " + v.Name;
            return name + (v.Seat.Length > 0 && name.IndexOf(v.Seat, StringComparison.OrdinalIgnoreCase) < 0 ? " · " + v.Seat : string.Empty);
        }

        private static ManualPage NewPage(string kind, string id, string title, CockpitVariant v, ManualPalette pal)
        {
            ManualPage page = new ManualPage { Kind = kind, Id = id, Title = title, Paper = pal.Paper };
            page.Items.Add(Text(ManualLayout.Margin, 26, ManualLayout.PageWidth - 2 * ManualLayout.Margin, Heading(v), 13, false, pal.Muted));
            page.Items.Add(Text(ManualLayout.Margin, 46, ManualLayout.PageWidth - 2 * ManualLayout.Margin, title, TitleSize, true, pal.Ink));
            page.Items.Add(new ManualPageItem { Kind = "rect", X = ManualLayout.Margin, Y = 86, W = ManualLayout.PageWidth - 2 * ManualLayout.Margin, H = 2, Fill = pal.Accent });
            return page;
        }

        private static ManualPageItem Text(double x, double y, double w, string text, double size, bool bold, string color, string align = "left", int maxLines = 0)
        {
            List<string> lines = ManualLayout.Wrap(text, w, size, bold, maxLines);
            return new ManualPageItem { Kind = "text", X = x, Y = y, W = w, H = ManualLayout.TextHeight(lines.Count, size), Lines = lines, Size = size, Bold = bold, Color = color, Align = align };
        }

        // ------------------------------------------------------------------ pictures from the user's game

        // The game views of the variant's seat, or none.
        private static List<CockpitPictureView> GameViews(CockpitVariant v, ManualPageOptions o)
        {
            if (o.Pictures == null)
                return new List<CockpitPictureView>();
            return o.Pictures.ViewsOf(o.Pictures.SeatOf(v));
        }

        // A game view as the page's picture: the render, or JoyPro's line drawing of it (the drawing mode, or no render).
        public static CockpitArt GameArt(CockpitPictureView view, ManualPageOptions o)
        {
            // A picture the quality gate rated unusable (below 45, PictureQuality.Keep; a low-quality set's overview is
            // kept for its callouts): the page lists its controls without it.
            if (view == null || (view.Quality >= 0 && view.Quality < 45))
                return null;
            // A drawing that would be a mess (a busy perspective, a poor render) or of a screenshot: the picture instead.
            bool drawing = string.Equals(o.PictureMode, "drawing", StringComparison.OrdinalIgnoreCase) && view.DrawingReadable &&
                           !(view.Set != null && string.Equals(view.Set.Source, "screenshot", StringComparison.OrdinalIgnoreCase));
            if (!drawing && File.Exists(view.Image))
                return new CockpitArt { Picture = view.Image, Mask = File.Exists(view.Mask) ? view.Mask : string.Empty, PictureWidth = view.Width, PictureHeight = view.Height };
            DeviceLineArt art = view.Set != null ? view.Set.DrawingOf(view) : null;
            if (art != null)
                return new CockpitArt { LineArt = art };
            return File.Exists(view.Image) ? new CockpitArt { Picture = view.Image, Mask = File.Exists(view.Mask) ? view.Mask : string.Empty, PictureWidth = view.Width, PictureHeight = view.Height } : null;
        }

        private static string GameSource(CockpitPictureSet set)
        {
            if (set == null)
                return string.Empty;
            // Screenshots taken in the game (its models are encrypted): no callouts on the picture, the list names the
            // aircraft's own controls.
            if (string.Equals(set.Source, "screenshot", StringComparison.OrdinalIgnoreCase))
                return "Screenshot taken in " + GameName(set.Game) + (set.FromPack ? " (picture pack version " + set.PackVersion.ToString(CultureInfo.InvariantCulture) + ")" : " on this PC") +
                       (set.Module.Length > 0 ? " (" + set.Module + ")" : string.Empty) + "; the controls listed are the aircraft's own names for them, without places on the picture.";
            return (set.FromPack ? "Picture rendered from " + GameName(set.Game) + " with JoyPro (picture pack version " + set.PackVersion.ToString(CultureInfo.InvariantCulture) + ")" : "Picture made on this PC from " + GameName(set.Game)) + (set.Module.Length > 0 ? " (" + set.Module + (set.Source.Length > 0 ? ", " + set.Source : string.Empty) + ")" : string.Empty) +
                   (set.GameVersion.Length > 0 ? ", game version " + set.GameVersion : string.Empty) + "; callouts at the game's own controls, functions as the game names them.";
        }

        public static string GameName(string game)
        {
            switch (game)
            {
                case "DCS":
                    return "DCS World";
                case "XPlane12":
                    return "X-Plane 12";
                case "MSFS2020":
                    return "Microsoft Flight Simulator 2020";
                case "MSFS2024":
                    return "Microsoft Flight Simulator 2024";
                case "IL2Game":
                    return "IL-2 Sturmovik";
                case "IL2Korea":
                    return "IL-2 Korea";
                case "NuclearOption":
                    return "Nuclear Option";
                case "StarCitizen":
                    return "Star Citizen";
                case "WarThunder":
                    return "War Thunder";
                default:
                    return game;
            }
        }

        // The page a game overview callout leads to: a grip's page, or the game panel's page.
        private static string GameLink(CockpitVariant v, string link)
        {
            if (string.IsNullOrEmpty(link))
                return string.Empty;
            int at = link.IndexOf('@');
            if (link.StartsWith("device:", StringComparison.Ordinal))
            {
                string kind = (at > 0 ? link.Substring(7, at - 7) : link.Substring(7)).ToLowerInvariant();
                List<string> devices = CockpitCatalog.DevicesOf(v);
                string device = kind.StartsWith("throttle", StringComparison.Ordinal) ? devices.FirstOrDefault(CockpitCatalog.IsThrottleDevice) : devices.FirstOrDefault(CockpitCatalog.IsStickDevice);
                return device == null ? string.Empty : "device:" + device;
            }
            return "game:" + link;
        }

        public static ManualPage GameOverview(CockpitCatalog catalog, CockpitVariant v, ManualPageOptions o, List<CockpitPictureView> views)
        {
            ManualPalette pal = ManualPalette.Of(o);
            ManualPage page = NewPage("overview", "overview", "Cockpit overview", v, pal);
            double y = 104;
            CockpitPictureView ov = views.FirstOrDefault(x => x.Kind == "overview");
            string intro = v.Overview != null && v.Overview.Text.Length > 0 ? v.Overview.Text : v.Cockpit;
            if (intro.Length > 0)
            {
                ManualPageItem t = Text(ManualLayout.Margin, y, ManualLayout.PageWidth - 2 * ManualLayout.Margin, intro, BodySize, false, pal.Ink);
                page.Items.Add(t);
                y += t.H + 14;
            }
            CockpitArt art = GameArt(ov, o);
            List<Callout> callouts = new List<Callout>();
            if (ov != null)
            {
                foreach (CockpitPictureCallout c in ov.Callouts)
                {
                    CockpitGuideNode node = GuideNode(o, ov, c);
                    string label = node != null && !node.Generic ? node.Name : ViewTitle(o, c.Link, c.Label);
                    callouts.Add(new Callout { N = c.N, Label = label, X = c.X, Y = c.Y, Link = GameLink(v, c.Link), Tag = "area:" + c.N, Text = node == null || node.Generic ? string.Empty : node.Short });
                }
            }
            if (art != null)
                y = PlaceDrawing(page, art, callouts, y, 620, true, pal, o);
            y += 10;
            foreach (Callout c in callouts.OrderBy(x => x.N))
                y = NumberedLine(page, y, c.N, c.Label, c.Text, string.Empty, pal, o, c.Link, c.Tag);
            ManualPageItem src = Text(ManualLayout.Margin, y + 8, ManualLayout.PageWidth - 2 * ManualLayout.Margin, GameSource(o.Pictures), 11.5, false, pal.Muted);
            page.Items.Add(src);
            y += src.H + 8;
            page.SearchText = string.Join(" ", page.Items.SelectMany(i => i.Lines));
            page.Height = y + ManualLayout.Margin;
            return page;
        }

        public static ManualPage GamePanel(CockpitVariant v, CockpitPictureView view, ManualPageOptions o)
        {
            ManualPalette pal = ManualPalette.Of(o);
            ManualPage page = NewPage("panel", "game:" + view.Id, ViewTitle(o, view.Id, view.Title), v, pal);
            double y = 104;
            string area = CockpitSchematic.AreaTitle(view.Area);
            if (area.Length > 0)
            {
                ManualPageItem t = Text(ManualLayout.Margin, y, ManualLayout.PageWidth - 2 * ManualLayout.Margin, area + ".", BodySize, false, pal.Ink);
                page.Items.Add(t);
                y += t.H + 12;
            }
            CockpitArt art = GameArt(view, o);
            if (art != null)
            {
                List<Callout> callouts = view.Callouts.Select(c => new Callout { N = c.N, Label = GuideName(o, view, c), X = c.X, Y = c.Y, Tag = "click:" + c.Id }).ToList();
                y = PlaceDrawing(page, art, callouts, y, 560, true, pal, o);
                y += 10;
            }
            foreach (CockpitPictureCallout c in view.Callouts.OrderBy(x => x.N))
            {
                List<string> names = c.Commands.Select(x => CommandName(x.Name)).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                CockpitGuideNode node = GuideNode(o, view, c);
                string explain = node == null || node.Generic ? string.Empty : node.Short;
                int keep = explain.Length > 0 ? 3 : 5;
                string text = names.Count == 0 ? string.Empty : "Commands to bind: " + string.Join("; ", names.Take(keep)) + (names.Count > keep ? "; ..." : string.Empty) + ".";
                text = explain + (explain.Length > 0 && text.Length > 0 ? " " : string.Empty) + text;
                y = NumberedLine(page, y, c.N, GuideName(o, view, c), text, string.Empty, pal, o, string.Empty, "click:" + c.Id);
            }
            ManualPageItem src = Text(ManualLayout.Margin, y + 8, ManualLayout.PageWidth - 2 * ManualLayout.Margin, GameSource(view.Set ?? o.Pictures), 11.5, false, pal.Muted);
            page.Items.Add(src);
            y += src.H + 8;
            page.SearchText = string.Join(" ", page.Items.SelectMany(i => i.Lines));
            page.Height = y + ManualLayout.Margin;
            return page;
        }

        // A command's readable name: a game's command path (X-Plane: "laminar/B738/switch/gen2_up",
        // "sim/electrical/battery_1_on") as words with its system ("Gen 2 up", "Battery 1 on (electrical)"); a name that
        // is already words (DCS: "JFS Switch - START 1 / OFF") as it is.
        private static readonly HashSet<string> GenericFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sim", "laminar", "cockpit", "cockpit2", "switch", "switches", "toggle_switch", "push_button", "one_way_switch", "knob", "knobs",
            "button", "buttons", "actuators", "rheos", "slider", "commands", "custom", "guard_cover"
        };

        public static string CommandName(string command)
        {
            string s = (command ?? string.Empty).Trim();
            if (s.IndexOf('/') < 0 || s.IndexOf(' ') >= 0)
                return s;
            string[] parts = s.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return s;
            string last = Regex.Replace(parts[parts.Length - 1], @"\[(\d+)\]$", " $1");
            string words = Words(last);
            if (words.Length == 0)
                return s;
            // The system: the nearest folder that says something (not the vendor, the aircraft or a kind of switch).
            string system = string.Empty;
            for (int i = parts.Length - 2; i >= 1 && system.Length == 0; i--)
            {
                string f = parts[i];
                if (GenericFolders.Contains(f) || Regex.IsMatch(f, @"^[A-Z]{0,3}\d{2,4}[A-Z]?$") || f.Length <= 2)
                    continue;
                system = Words(f).ToLowerInvariant();
            }
            return char.ToUpperInvariant(words[0]) + words.Substring(1) + (system.Length > 0 && words.IndexOf(system, StringComparison.OrdinalIgnoreCase) < 0 ? " (" + system + ")" : string.Empty);
        }

        private static string Words(string s)
        {
            string t = Regex.Replace(s, "([a-z])([A-Z])", "$1 $2");
            t = Regex.Replace(t, "([A-Za-z])([0-9])", "$1 $2");
            t = Regex.Replace(t, "([0-9])([A-Za-z])", "$1 $2");
            t = Regex.Replace(t, @"[_\-\.\s]+", " ").Trim();
            // Lower case, except words that are all capitals (CDU, MCP, APU).
            return string.Join(" ", t.Split(' ').Select(w => w.Length > 1 && w.All(ch => char.IsUpper(ch) || char.IsDigit(ch)) ? w : w.ToLowerInvariant()));
        }

        // The guide's node of a game callout, or null.
        private static CockpitGuideNode GuideNode(ManualPageOptions o, CockpitPictureView view, CockpitPictureCallout c)
        {
            CockpitGuideNode node;
            return o.Guide != null && view != null && c != null && o.Guide.ByCallout.TryGetValue(view.Id + "|" + c.Id, out node) ? node : null;
        }

        // A callout's name: the guide's name of the control (the game's name for it when the guide has none).
        private static string GuideName(ManualPageOptions o, CockpitPictureView view, CockpitPictureCallout c)
        {
            CockpitGuideNode node = GuideNode(o, view, c);
            return node != null && !node.Generic && node.Name.Length > 0 ? node.Name : c.Label;
        }

        // A game view's title by the guide's panels of its controls ("ELEC control panel, EPU control panel").
        private static string ViewTitle(ManualPageOptions o, string viewId, string fallback)
        {
            string title;
            return o.Guide != null && !string.IsNullOrEmpty(viewId) && o.Guide.ViewTitles.TryGetValue(viewId, out title) ? title : fallback;
        }

        // ------------------------------------------------------------------ overview

        public static ManualPage Overview(CockpitCatalog catalog, CockpitVariant v, ManualPageOptions o)
        {
            ManualPalette pal = ManualPalette.Of(o);
            ManualPage page = NewPage("overview", "overview", "Cockpit overview", v, pal);
            double y = 104;
            CockpitOverview ov = v.Overview;
            string intro = ov != null && ov.Text.Length > 0 ? ov.Text : v.Cockpit;
            if (intro.Length > 0)
            {
                ManualPageItem t = Text(ManualLayout.Margin, y, ManualLayout.PageWidth - 2 * ManualLayout.Margin, intro, BodySize, false, pal.Ink);
                page.Items.Add(t);
                y += t.H + 14;
            }
            // No drawing without pictures from a game: the traced line drawings and the plan of area boxes are not shown
            // (TracedDrawings); the numbered list of the areas below is the page.
            DeviceLineArt art = TracedDrawings && ov != null && !(v.Aircraft != null && v.Aircraft.Fiction) ? LineArt(o.Folder, ov.Drawing) : null;
            if (art != null)
            {
                List<Callout> callouts = ov.Areas.Select(a => new Callout { N = a.N, Label = a.Name, X = a.X, Y = a.Y, Link = AreaLink(v, a) }).ToList();
                y = PlaceDrawing(page, new CockpitArt { LineArt = art }, callouts, y, 520, false, pal, o);
            }
            // The numbered list of the areas and the panels in each.
            if (ov != null && ov.Areas.Count > 0)
            {
                y += 10;
                foreach (CockpitOverviewArea a in ov.Areas.OrderBy(x => x.N))
                {
                    List<string> panels = a.Panels.Select(id => v.Panel(id)).Where(p => p != null).Select(p => p.Name).ToList();
                    string text = a.Text + (panels.Count > 0 ? (a.Text.Length > 0 ? " " : string.Empty) + "Panels: " + string.Join(", ", panels) + "." : string.Empty);
                    y = NumberedLine(page, y, a.N, a.Name, text, v.Cite(a.Src, a.Page), pal, o, AreaLink(v, a), "area:" + a.N);
                }
            }
            page.SearchText = string.Join(" ", page.Items.SelectMany(i => i.Lines));
            page.Height = y + ManualLayout.Margin;
            return page;
        }

        // Where an overview area leads: the stick or throttle page for the grips, else its (first) panel's page.
        private static string AreaLink(CockpitVariant v, CockpitOverviewArea a)
        {
            string area = CockpitSchematic.AreaKey(a.Area);
            List<string> devices = CockpitCatalog.DevicesOf(v);
            if (area == "stick")
                return devices.Where(CockpitCatalog.IsStickDevice).Select(d => "device:" + d).FirstOrDefault() ?? string.Empty;
            if (area == "throttle")
                return devices.Where(CockpitCatalog.IsThrottleDevice).Select(d => "device:" + d).FirstOrDefault() ?? string.Empty;
            return a.Panels.Count > 0 ? "panel:" + a.Panels[0] : string.Empty;
        }

        // JoyPro's traced line drawings of real cockpits, panels and grips (DB\Cockpits\line-drawings) are not shown on the
        // pages (the user's verdict: not clean enough); where a game's pictures are good, no drawing is shown at all, and
        // without them only JoyPro's clean vector drawings of the grips. The files stay for the checks and later use.
        public static bool TracedDrawings = false;

        // The variant has good pictures from a game (a set that is not rated low): those are shown, no drawings.
        public static bool GoodPictures(ManualPageOptions o)
        {
            return o != null && o.Pictures != null && !o.Pictures.LowQuality;
        }

        // ------------------------------------------------------------------ a grip with its HOTAS table

        public static ManualPage Device(CockpitCatalog catalog, CockpitVariant v, string device, ManualPageOptions o)
        {
            List<CockpitControl> controls = v.Controls.Where(c => string.Equals(c.Device, device, StringComparison.OrdinalIgnoreCase) && c.Functions.Count > 0).ToList();
            if (controls.Count == 0)
                return null;
            ManualPalette pal = ManualPalette.Of(o);
            ManualPage page = NewPage("device", "device:" + device, CockpitCatalog.DeviceTitle(device), v, pal);
            double y = 104;
            // Numbers: the manual's own (N), else in list order after the highest given one.
            Dictionary<CockpitControl, int> numbers = Number(controls);
            List<Callout> callouts = new List<Callout>();
            CockpitArt picture = null;
            DeviceLineArt art = TracedDrawings && !GoodPictures(o) ? DeviceArt(o.Folder, v, device) : null;
            CockpitPictureView grip = o.Pictures == null ? null : o.Pictures.Grip(o.Pictures.SeatOf(v), device);
            CockpitArt gripArt = grip == null ? null : GameArt(grip, o);
            if (gripArt != null)
            {
                // The game's own grip, the controls where the model has a named part for them.
                picture = gripArt;
                Dictionary<string, CockpitPictureCallout> parts = CockpitPictureSet.MatchGrip(grip, controls);
                foreach (CockpitControl c in controls)
                {
                    CockpitPictureCallout at;
                    parts.TryGetValue(c.Id, out at);
                    callouts.Add(new Callout { N = numbers[c], Label = c.Name, X = at == null ? -1 : at.X, Y = at == null ? -1 : at.Y, Tag = "control:" + c.Id });
                }
            }
            else if (art != null && art.Parts.Count > 0)
            {
                picture = new CockpitArt { LineArt = art };
                foreach (CockpitControl c in controls)
                {
                    double[] at;
                    if (!art.Parts.TryGetValue(c.PartId, out at) && !art.Parts.TryGetValue(c.Id, out at))
                        art.Parts.TryGetValue(numbers[c].ToString(CultureInfo.InvariantCulture), out at);
                    callouts.Add(new Callout { N = numbers[c], Label = c.Name, X = at == null ? -1 : at[0], Y = at == null ? -1 : at[1], Tag = "control:" + c.Id });
                }
            }
            else if (!GoodPictures(o))
            {
                // No game picture of this grip and no good set: JoyPro's clean vector drawing of the grip.
                CockpitDrawing drawing = catalog.DrawingFor(v, device);
                if (drawing != null)
                {
                    CockpitSheet sheet = CockpitSchematic.Build(drawing, controls, device, 0);
                    picture = new CockpitArt { Schematic = sheet.Schematic };
                    foreach (CockpitControl c in controls)
                    {
                        double[] at;
                        sheet.Known.TryGetValue(c.Id, out at);
                        callouts.Add(new Callout { N = numbers[c], Label = c.Name, X = at == null ? -1 : at[0], Y = at == null ? -1 : at[1], Tag = "control:" + c.Id });
                    }
                }
            }
            if (picture != null)
                y = PlaceDrawing(page, picture, callouts, y, 470, false, pal, o);
            y = HotasTable(page, v, controls, numbers, y + 16, pal, o);
            // Where each control is (the words), for the pages without a drawing of the real grip.
            List<string> cites = controls.SelectMany(c => new[] { v.Cite(c.Src, c.Page) }.Concat(c.Functions.Select(f => v.Cite(f.Src, f.Page))))
                .Where(s => s.Length > 0).Distinct().ToList();
            if (cites.Count > 0)
            {
                ManualPageItem src = Text(ManualLayout.Margin, y + 10, ManualLayout.PageWidth - 2 * ManualLayout.Margin, "Sources: " + string.Join("; ", cites.Take(6)), 11.5, false, pal.Muted);
                page.Items.Add(src);
                y += src.H + 10;
            }
            page.SearchText = string.Join(" ", page.Items.SelectMany(i => i.Lines));
            page.Height = y + ManualLayout.Margin;
            return page;
        }

        public static Dictionary<CockpitControl, int> Number(List<CockpitControl> controls)
        {
            Dictionary<CockpitControl, int> numbers = new Dictionary<CockpitControl, int>();
            HashSet<int> used = new HashSet<int>(controls.Where(c => c.N > 0).Select(c => c.N));
            int next = 1;
            foreach (CockpitControl c in controls)
            {
                if (c.N > 0 && !numbers.ContainsValue(c.N))
                {
                    numbers[c] = c.N;
                    continue;
                }
                while (used.Contains(next))
                    next++;
                numbers[c] = next;
                used.Add(next);
            }
            return numbers;
        }

        // The direction in the table's words: "Fwd", "Depress", "Up" ...
        public static string ActionWords(CockpitFunction f)
        {
            string d = f.Dir ?? string.Empty;
            string words;
            switch (d)
            {
                case "": words = "Press"; break;
                case "press": words = "Depress"; break;
                case "fwd": words = "Fwd"; break;
                case "aft": words = "Aft"; break;
                case "upleft": words = "Up-left"; break;
                case "upright": words = "Up-right"; break;
                case "downleft": words = "Down-left"; break;
                case "downright": words = "Down-right"; break;
                case "cw": words = "Clockwise"; break;
                case "ccw": words = "Counter-clockwise"; break;
                case "stage1": words = "1st detent"; break;
                case "stage2": words = "2nd detent"; break;
                case "x": words = "Left / right"; break;
                case "y": words = "Fwd / aft"; break;
                case "axis": words = "Axis"; break;
                default:
                    words = d.StartsWith("pos", StringComparison.Ordinal) ? "Position " + d.Substring(3) : char.ToUpperInvariant(d[0]) + d.Substring(1);
                    break;
            }
            return words;
        }

        public static string PressWords(string press)
        {
            switch (press ?? string.Empty)
            {
                case "short": return "Short";
                case "long": return "Long";
                case "press": return "Press";
                case "hold": return "Hold";
                case "double": return "Double";
                case "release": return "Release";
                default: return string.Empty;
            }
        }

        private sealed class Cell
        {
            public int Col;
            public int Span = 1;
            public string Text = string.Empty;
            public string Fn = string.Empty;
            public List<string> Lines;
            public bool Highlight;
        }

        private sealed class TableRow
        {
            public CockpitFunction F;
            public List<Cell> Cells = new List<Cell>();
            public List<string> Press = new List<string>();
            public double H;
        }

        private static double HotasTable(ManualPage page, CockpitVariant v, List<CockpitControl> controls, Dictionary<CockpitControl, int> numbers, double y,
            ManualPalette pal, ManualPageOptions o)
        {
            List<CockpitMode> modes = v.Modes.Count > 0 ? v.Modes : new List<CockpitMode> { new CockpitMode { Id = string.Empty, Name = "Function" } };
            bool modal = v.Modes.Count > 0;
            bool presses = controls.Any(c => c.Functions.Any(f => f.Press.Length > 0));
            double x0 = ManualLayout.Margin, width = ManualLayout.PageWidth - 2 * ManualLayout.Margin;
            double controlW = 170, actionW = presses ? 100 : 130, pressW = presses ? 70 : 0;
            double modeW = (width - controlW - actionW - pressW) / modes.Count;
            double headerH = modal ? 50 : 30;
            string highlight = o.Highlight ?? string.Empty;
            // Header: "Control | Action | Selected sensor of interest: TAD TGP ..." (or "Function").
            AddCell(page, x0, y, controlW, headerH, pal.Header, pal.Grid, new List<string> { "Control" }, CellSize, true, pal.HeaderInk, "center");
            AddCell(page, x0 + controlW, y, actionW + pressW, headerH, pal.Header, pal.Grid, new List<string> { "Action" }, CellSize, true, pal.HeaderInk, "center");
            double mx = x0 + controlW + actionW + pressW;
            if (modal)
            {
                string kind = v.Modes.All(m => m.Kind == "soi") ? "Selected sensor of interest" : v.Modes.All(m => m.Kind == "master") ? "Master mode" : "Mode";
                AddCell(page, mx, y, modeW * modes.Count, 22, pal.Header, pal.Grid, new List<string> { kind }, CellSize, true, pal.HeaderInk, "center");
                for (int i = 0; i < modes.Count; i++)
                    AddCell(page, mx + i * modeW, y + 22, modeW, headerH - 22, pal.Header, pal.Grid, ManualLayout.Wrap(modes[i].Name, modeW - 8, CellSize, true, 2), CellSize, true, pal.HeaderInk, "center");
            }
            else
            {
                AddCell(page, mx, y, modeW, headerH, pal.Header, pal.Grid, new List<string> { "Function" }, CellSize, true, pal.HeaderInk, "center");
            }
            y += headerH;
            int colour = 0;
            foreach (CockpitControl c in controls)
            {
                string fill = pal.Rows[colour % pal.Rows.Length], strong = pal.RowsStrong[colour % pal.RowsStrong.Length];
                colour++;
                // Pass 1: the rows' cells (equal neighbouring cells merged) and heights.
                List<TableRow> rows = new List<TableRow>();
                foreach (CockpitFunction f in c.Functions)
                {
                    TableRow row = new TableRow { F = f, H = 22 };
                    for (int m = 0; m < modes.Count; m++)
                    {
                        string text = modal ? f.TextIn(modes[m].Id) : f.Text;
                        string fn = modal ? f.FnIn(modes[m].Id) : f.Fn;
                        string mark = o.Mark == null ? null : o.Mark(c, f, modes[m].Id);
                        if (!string.IsNullOrEmpty(mark))
                            text = text + "  " + mark;
                        Cell last = row.Cells.Count > 0 ? row.Cells[row.Cells.Count - 1] : null;
                        if (last != null && last.Text == text && last.Fn == fn)
                            last.Span++;
                        else
                            row.Cells.Add(new Cell { Col = m, Text = text, Fn = fn });
                    }
                    foreach (Cell cell in row.Cells)
                    {
                        cell.Lines = ManualLayout.Wrap(cell.Text.Length == 0 ? "—" : cell.Text, modeW * cell.Span - 10, CellSize);
                        cell.Highlight = Matches(cell.Text, highlight);
                        row.H = Math.Max(row.H, ManualLayout.TextHeight(cell.Lines.Count, CellSize) + 8);
                    }
                    row.Press = ManualLayout.Wrap(PressWords(f.Press), Math.Max(20, pressW - 8), CellSize, true);
                    if (presses)
                        row.H = Math.Max(row.H, ManualLayout.TextHeight(row.Press.Count, CellSize) + 8);
                    rows.Add(row);
                }
                // The action cells: rows of one direction and condition share one; a long action name grows the group.
                List<Tuple<int, int, List<string>>> groups = new List<Tuple<int, int, List<string>>>();
                for (int r = 0; r < rows.Count;)
                {
                    int end = r;
                    while (end + 1 < rows.Count && rows[end + 1].F.Dir == rows[r].F.Dir && rows[end + 1].F.When == rows[r].F.When)
                        end++;
                    string action = (rows[r].F.When.Length > 0 ? rows[r].F.When + ": " : string.Empty) + ActionWords(rows[r].F);
                    List<string> lines = ManualLayout.Wrap(action, actionW - 8, CellSize, true);
                    double need = ManualLayout.TextHeight(lines.Count, CellSize) + 8, have = 0;
                    for (int k = r; k <= end; k++)
                        have += rows[k].H;
                    if (need > have)
                        rows[end].H += need - have;
                    groups.Add(Tuple.Create(r, end, lines));
                    r = end + 1;
                }
                string name = numbers[c].ToString(CultureInfo.InvariantCulture) + ". " + c.Name;
                List<string> nameLines = ManualLayout.Wrap(name, controlW - 10, CellSize, true);
                double total = rows.Sum(r => r.H), needName = ManualLayout.TextHeight(nameLines.Count, CellSize) + 10;
                if (needName > total)
                    rows[rows.Count - 1].H += needName - total;
                // Pass 2: place.
                double top = y;
                double[] rowTop = new double[rows.Count];
                for (int k = 0; k < rows.Count; k++)
                {
                    TableRow row = rows[k];
                    rowTop[k] = y;
                    foreach (Cell cell in row.Cells)
                    {
                        ManualPageItem item = AddCell(page, mx + cell.Col * modeW, y, modeW * cell.Span, row.H, cell.Highlight ? pal.Mark : fill, pal.Grid, cell.Lines, CellSize, false, pal.Ink, "center");
                        item.Tag = cell.Fn.Length > 0 ? "fn:" + cell.Fn : string.Empty;
                        item.Highlight = cell.Highlight;
                    }
                    if (presses)
                        AddCell(page, x0 + controlW + actionW, y, pressW, row.H, fill, pal.Grid, row.Press, CellSize, true, pal.Ink, "center");
                    y += row.H;
                }
                foreach (Tuple<int, int, List<string>> g in groups)
                {
                    double gTop = rowTop[g.Item1], gBottom = rowTop[g.Item2] + rows[g.Item2].H;
                    AddCell(page, x0 + controlW, gTop, actionW, gBottom - gTop, fill, pal.Grid, g.Item3, CellSize, true, pal.Ink, "center");
                }
                bool hit = Matches(c.Name + " " + c.Id, highlight);
                ManualPageItem control = AddCell(page, x0, top, controlW, y - top, hit ? pal.Mark : strong, pal.Grid, nameLines, CellSize, true, pal.Ink, "center");
                control.Tag = "control:" + c.Id;
                control.Highlight = hit;
            }
            return y;
        }

        private static bool Matches(string text, string query)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(text))
                return false;
            string t = CockpitCatalog.Norm(text);
            return query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(CockpitCatalog.Norm).Where(q => q.Length > 0).All(q => t.Contains(q));
        }

        // A table cell: a filled box with the text centred vertically.
        private static ManualPageItem AddCell(ManualPage page, double x, double y, double w, double h, string fill, string grid, List<string> lines, double size, bool bold, string ink, string align)
        {
            ManualPageItem box = new ManualPageItem { Kind = "rect", X = x, Y = y, W = w, H = h, Fill = fill, Stroke = grid, StrokeWidth = 1, Lines = lines, Size = size, Bold = bold, Color = ink, Align = align };
            box.Kind = "cell";
            page.Items.Add(box);
            return box;
        }

        // ------------------------------------------------------------------ a panel

        public static ManualPage Panel(CockpitCatalog catalog, CockpitVariant v, CockpitPanel p, ManualPageOptions o)
        {
            ManualPalette pal = ManualPalette.Of(o);
            ManualPage page = NewPage("panel", "panel:" + p.Key, p.Name, v, pal);
            double y = 104;
            string area = CockpitSchematic.AreaTitle(p.Area);
            if (p.Text.Length > 0 || area.Length > 0)
            {
                ManualPageItem t = Text(ManualLayout.Margin, y, ManualLayout.PageWidth - 2 * ManualLayout.Margin, (area.Length > 0 ? area + ". " : string.Empty) + p.Text, BodySize, false, pal.Ink);
                page.Items.Add(t);
                y += t.H + 12;
            }
            DeviceLineArt art = !TracedDrawings || GoodPictures(o) || (v.Aircraft != null && v.Aircraft.Fiction) ? null : LineArt(o.Folder, p.Drawing);
            if (art != null)
            {
                List<Callout> callouts = p.Items.Select(i => new Callout { N = i.N, Label = string.Empty, X = i.X, Y = i.Y, Tag = "item:" + p.Key + ":" + i.N }).ToList();
                y = PlaceDrawing(page, new CockpitArt { LineArt = art }, callouts, y, 520, true, pal, o);
                y += 10;
            }
            foreach (CockpitPanelItem i in p.Items.OrderBy(x => x.N))
            {
                string text = i.Text;
                if (i.Positions.Count > 0)
                    text += (text.Length > 0 ? " " : string.Empty) + "Positions: " + string.Join(" / ", i.Positions) + ".";
                y = NumberedLine(page, y, i.N, i.Name, text, string.Empty, pal, o, string.Empty, "item:" + p.Key + ":" + i.N + (i.Fn.Length > 0 ? "|fn:" + i.Fn : string.Empty));
            }
            string cite = v.Cite(p.Src, p.Page);
            if (cite.Length > 0)
            {
                ManualPageItem src = Text(ManualLayout.Margin, y + 8, ManualLayout.PageWidth - 2 * ManualLayout.Margin, "Source: " + cite, 11.5, false, pal.Muted);
                page.Items.Add(src);
                y += src.H + 8;
            }
            page.SearchText = string.Join(" ", page.Items.SelectMany(it => it.Lines));
            page.Height = y + ManualLayout.Margin;
            return page;
        }

        // "N.  Name  text" as a numbered list line (two columns: the number and name bold, then the text).
        private static double NumberedLine(ManualPage page, double y, int n, string name, string text, string cite, ManualPalette pal, ManualPageOptions o, string link, string tag)
        {
            double x = ManualLayout.Margin, width = ManualLayout.PageWidth - 2 * ManualLayout.Margin;
            double numW = 40;
            string body0 = text + (cite.Length > 0 ? " (" + cite + ")" : string.Empty);
            // Only a name: it takes the whole line.
            double nameW = body0.Trim().Length == 0 ? width - numW : 330;
            bool hit = Matches(name + " " + text, o.Highlight);
            ManualPageItem num = Text(x, y, numW, n.ToString(CultureInfo.InvariantCulture) + ".", BodySize, true, pal.Accent);
            ManualPageItem title = Text(x + numW, y, nameW, name, BodySize, true, pal.Ink);
            string body = text + (cite.Length > 0 ? " (" + cite + ")" : string.Empty);
            ManualPageItem desc = Text(x + numW + nameW + 10, y, width - numW - nameW - 10, body.Length > 0 ? body : " ", BodySize, false, pal.Ink);
            foreach (ManualPageItem it in new[] { num, title, desc })
            {
                it.Link = link;
                it.Tag = tag;
                it.Highlight = hit;
                page.Items.Add(it);
            }
            double h = Math.Max(title.H, desc.H);
            if (hit)
                page.Items.Insert(page.Items.Count - 3, new ManualPageItem { Kind = "rect", X = x - 6, Y = y - 2, W = width + 12, H = h + 4, Fill = pal.Mark, Radius = 4, Tag = tag });
            return y + h + 6;
        }

        // ------------------------------------------------------------------ the drawing with numbered callouts

        private sealed class Callout
        {
            public int N;
            public string Label = string.Empty;
            public double X = -1;
            public double Y = -1;
            public string Link = string.Empty;
            public string Tag = string.Empty;
            public string Text = string.Empty;
        }

        // The drawing centred between two callout columns; a callout box per control ("7. Trim Switch", or only the
        // number on a panel) on the side of its control, a line from the box to the control. Returns the bottom.
        private static double PlaceDrawing(ManualPage page, CockpitArt art, List<Callout> callouts, double top, double maxHeight, bool numbersOnly, ManualPalette pal, ManualPageOptions o)
        {
            double width = ManualLayout.PageWidth - 2 * ManualLayout.Margin;
            double side = numbersOnly ? 60 : CalloutWidth;
            double maxW = width - 2 * side - 40;
            double scale = Math.Min(maxW / art.Width, maxHeight / art.Height);
            double w = art.Width * scale, h = art.Height * scale;
            double x = ManualLayout.Margin + (width - w) / 2;
            page.Items.Add(new ManualPageItem { Kind = "drawing", X = x, Y = top, W = w, H = h, Art = art });
            List<Callout> placed = callouts.Where(c => c.X >= 0 && c.Y >= 0 && c.X <= 1 && c.Y <= 1).ToList();
            List<Callout> left = placed.Where(c => c.X < 0.5).OrderBy(c => c.Y).ToList();
            List<Callout> right = placed.Where(c => c.X >= 0.5).OrderBy(c => c.Y).ToList();
            // Balance: a side with far more callouts gives its middle ones to the other side.
            while (left.Count > right.Count + 3)
            {
                Callout c = left.OrderByDescending(k => k.X).First();
                left.Remove(c);
                right.Add(c);
                right = right.OrderBy(k => k.Y).ToList();
            }
            while (right.Count > left.Count + 3)
            {
                Callout c = right.OrderBy(k => k.X).First();
                right.Remove(c);
                left.Add(c);
                left = left.OrderBy(k => k.Y).ToList();
            }
            double bottom = top + h;
            bottom = Math.Max(bottom, Column(page, left, true, ManualLayout.Margin, side, x, top, h, w, numbersOnly, pal, o));
            bottom = Math.Max(bottom, Column(page, right, false, x + w + 40 + (side - (numbersOnly ? 60 : CalloutWidth)), side, x, top, h, w, numbersOnly, pal, o));
            // Controls the drawing has no position for: listed below it.
            List<Callout> rest = callouts.Where(c => c.X < 0 || c.Y < 0 || c.X > 1 || c.Y > 1).OrderBy(c => c.N).ToList();
            if (rest.Count > 0 && !numbersOnly)
            {
                ManualPageItem t = Text(ManualLayout.Margin, bottom + 8, width, "Not shown on the drawing: " + string.Join(", ", rest.Select(c => c.N + ". " + c.Label)), 12, false, pal.Muted);
                page.Items.Add(t);
                bottom += t.H + 8;
            }
            return bottom + 6;
        }

        private static double Column(ManualPage page, List<Callout> list, bool leftSide, double colX, double colW, double dx, double dy, double dh, double dw,
            bool numbersOnly, ManualPalette pal, ManualPageOptions o)
        {
            if (list.Count == 0)
                return dy;
            double boxW = numbersOnly ? 34 : colW;
            if (!leftSide)
                colX = dx + dw + 40;
            else
                colX = dx - 40 - boxW;
            List<Tuple<Callout, double, List<string>>> boxes = list.Select(c =>
            {
                List<string> lines = numbersOnly ? new List<string> { c.N.ToString(CultureInfo.InvariantCulture) }
                    : ManualLayout.Wrap(c.N.ToString(CultureInfo.InvariantCulture) + ". " + c.Label, boxW - 14, 13, false, 3);
                return Tuple.Create(c, ManualLayout.TextHeight(lines.Count, 13) + 10, lines);
            }).ToList();
            // Each box as near its control's height as the boxes above allow, then pushed up from the bottom.
            double[] ys = new double[boxes.Count];
            double at = dy;
            for (int i = 0; i < boxes.Count; i++)
            {
                double want = dy + boxes[i].Item1.Y * dh - boxes[i].Item2 / 2;
                ys[i] = Math.Max(want, at);
                at = ys[i] + boxes[i].Item2 + 8;
            }
            double limit = Math.Max(dy + dh, dy + boxes.Sum(b => b.Item2 + 8));
            for (int i = boxes.Count - 1; i >= 0; i--)
            {
                if (ys[i] + boxes[i].Item2 > limit)
                    ys[i] = limit - boxes[i].Item2;
                limit = ys[i] - 8;
            }
            double bottom = dy;
            for (int i = 0; i < boxes.Count; i++)
            {
                Callout c = boxes[i].Item1;
                double by = ys[i], bh = boxes[i].Item2;
                double tx = dx + c.X * dw, ty = dy + c.Y * dh;
                double ex = leftSide ? colX + boxW : colX, ey = by + bh / 2;
                double kx = leftSide ? ex + 14 : ex - 14;
                bool hit = Matches(c.Label, o.Highlight);
                page.Items.Add(new ManualPageItem { Kind = "line", Points = new[] { ex, ey, kx, ey, tx, ty }, Stroke = pal.Accent, StrokeWidth = 1.6, Tag = c.Tag, Link = c.Link });
                page.Items.Add(new ManualPageItem { Kind = "dot", X = tx - 3.5, Y = ty - 3.5, W = 7, H = 7, Fill = pal.Accent, Tag = c.Tag });
                page.Items.Add(new ManualPageItem
                {
                    Kind = "cell", X = colX, Y = by, W = boxW, H = bh, Fill = hit ? pal.Mark : pal.Paper, Stroke = pal.Accent, StrokeWidth = 1.6,
                    Lines = boxes[i].Item3, Size = 13, Bold = numbersOnly, Color = pal.Ink, Align = numbersOnly ? "center" : "left", Tag = c.Tag, Link = c.Link, Highlight = hit
                });
                bottom = Math.Max(bottom, by + bh);
            }
            return bottom;
        }
    }
}
