using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.DeviceReference;

namespace JoyPro.Engine
{
    // The kneeboard page of "layout kneeboard --page list" (docs/device-reference.md "Kneeboard"): a 3:4 portrait page
    // made to be read in the cockpit. A header with the aircraft and the device, the device picture small at the top with
    // a numbered badge on each control that has binds (optional), and below it only the bound controls as a list in a
    // large font, grouped by control as the Layout page's tables name them ("TMS hat: Up - Target Management Switch -
    // Up"); binds with a modifier carry a coloured tag with its name. No empty rows, no GUID. The font is as large as fits
    // (at least 20 px at 768x1024, about 14 px cap height); what does not fit goes on the next page. Several devices of
    // one aircraft (--combine) are sections of the same pages.
    internal static class EngineKneeboardPage
    {
        // Font sizes in pixels of a 768x1024 page (scaled with the page).
        private const float MaxFont = 30f, MinFont = 20f;
        private const float LineFactor = 1.16f, PadFactor = 0.22f;

        internal sealed class Row
        {
            public string Label = string.Empty;
            public string Modifier = string.Empty;
            public string Title = string.Empty;
            public int Order;
        }

        internal sealed class Group
        {
            public string Title = string.Empty;
            public int Number;
            public bool Known;
            public double SpotX, SpotY;
            public double Sort;
            // The start all command titles of the group share ("Countermeasures Management Switch" of "... - Fwd",
            // "... - Aft"): written once under the group's title, the rows show only the rest.
            public string Common = string.Empty;
            public List<Row> Rows = new List<Row>();
        }

        internal sealed class Section
        {
            public string Device = string.Empty;
            public Bitmap Picture;
            public List<Group> Groups = new List<Group>();
        }

        // The bound controls of a device for one aircraft, grouped by the controls of its layout (the Layout page's
        // tables), else by the kind of input. picture: the device picture of the layout (null when none or not wanted).
        public static Section Build(string joystick, string displayName, string layoutPath, string game, string plane, bool withPicture, bool dark, List<string> warnings)
        {
            Section section = new Section { Device = displayName };
            List<KeyValuePair<CalloutSheet, Bitmap>> sheets = LoadSheets(layoutPath, withPicture, dark, warnings);
            try
            {
                Dictionary<string, KeyValuePair<CalloutTable, CalloutRow>> byInput = new Dictionary<string, KeyValuePair<CalloutTable, CalloutRow>>(StringComparer.OrdinalIgnoreCase);
                Dictionary<CalloutTable, int> tableSheet = new Dictionary<CalloutTable, int>();
                for (int s = 0; s < sheets.Count; s++)
                {
                    foreach (CalloutTable table in sheets[s].Key.Tables)
                    {
                        tableSheet[table] = s;
                        foreach (CalloutRow row in table.Rows)
                        {
                            if (row.Input.Length > 0 && !byInput.ContainsKey(row.Input))
                                byInput[row.Input] = new KeyValuePair<CalloutTable, CalloutRow>(table, row);
                        }
                    }
                }

                Dictionary<string, Group> groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, Bind> entry in MultiBind.WithActiveExtras(InternalDataManagement.AllBinds))
                {
                    Bind bind = entry.Value;
                    if (bind == null || bind.Rl == null || !string.Equals((bind.Joystick ?? string.Empty).Trim(), joystick.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!EngineLayoutRenderer.RelationHasActivePlane(bind.Rl, game, plane))
                        continue;
                    string input = (bind.Rl.ISAXIS ? bind.JAxis : bind.JButton) ?? string.Empty;
                    if (input.Trim().Length == 0)
                        input = (string.IsNullOrWhiteSpace(bind.JButton) ? bind.JAxis : bind.JButton) ?? string.Empty;
                    input = input.Trim();
                    if (input.Length == 0)
                        continue;
                    string title = EngineLayoutRenderer.RelationDescriptionForGamePlane(bind.Rl, game, plane);
                    if (title.Length == 0)
                        title = (bind.Rl.NAME ?? entry.Key ?? string.Empty).Trim();
                    string modifier = string.Join(" + ", (bind.AllReformers ?? new List<string>())
                        .Select(r => (r ?? string.Empty).Split('§')[0].Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));

                    Group group;
                    Row row = new Row { Modifier = modifier, Title = title };
                    KeyValuePair<CalloutTable, CalloutRow> place;
                    if (byInput.TryGetValue(input, out place))
                    {
                        string key = "t:" + tableSheet[place.Key] + ":" + place.Key.Id;
                        if (!groups.TryGetValue(key, out group))
                        {
                            Bitmap picture = sheets[tableSheet[place.Key]].Value;
                            group = new Group
                            {
                                Title = CleanTitle(place.Key.Title),
                                Known = place.Key.Known && tableSheet[place.Key] == 0 && picture != null,
                                SpotX = place.Key.SpotX,
                                SpotY = place.Key.SpotY,
                                Sort = tableSheet[place.Key] * 10 + (place.Key.Known ? place.Key.SpotY * 2 + place.Key.SpotX * 0.2 : 5)
                            };
                            groups[key] = group;
                        }
                        row.Label = ShortLabel(RowLabel(place.Value), place.Key.Title);
                        row.Order = place.Key.Rows.IndexOf(place.Value);
                    }
                    else
                    {
                        string kind = InputKind(input);
                        string key = "k:" + kind;
                        if (!groups.TryGetValue(key, out group))
                        {
                            group = new Group { Title = kind, Sort = 100 + (kind == "Axes" ? 0 : kind == "Hats" ? 1 : 2) };
                            groups[key] = group;
                        }
                        row.Label = InputName(input);
                        row.Order = InputOrder(input);
                    }
                    if (!group.Rows.Any(r => r.Label == row.Label && r.Modifier == row.Modifier && r.Title == row.Title))
                        group.Rows.Add(row);
                }

                section.Groups = groups.Values.OrderBy(g => g.Sort).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
                int number = 1;
                foreach (Group group in section.Groups)
                {
                    group.Rows = group.Rows.OrderBy(r => r.Modifier.Length > 0 ? 1 : 0).ThenBy(r => r.Modifier, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Order).ToList();
                    // The same row name twice in a group (two detents named alike): keep them apart by their order.
                    foreach (IGrouping<string, Row> same in group.Rows.GroupBy(r => r.Label + "\u001F" + r.Modifier).Where(g => g.Count() > 1))
                    {
                        int n = 1;
                        foreach (Row r in same)
                            r.Label = r.Label + " " + (n++).ToString(CultureInfo.InvariantCulture);
                    }
                    if (group.Known)
                        group.Number = number++;
                    FactorCommonTitle(group);
                }
                if (withPicture && sheets.Count > 0 && sheets[0].Value != null && section.Groups.Count > 0)
                {
                    section.Picture = sheets[0].Value;
                    sheets[0] = new KeyValuePair<CalloutSheet, Bitmap>(sheets[0].Key, null);
                }
                return section;
            }
            finally
            {
                foreach (KeyValuePair<CalloutSheet, Bitmap> sheet in sheets)
                    if (sheet.Value != null)
                        sheet.Value.Dispose();
            }
        }

        // The sheets (tables and lines) of a layout file: one, or one per view of a page with several views; with the
        // picture of the first when wanted.
        private static List<KeyValuePair<CalloutSheet, Bitmap>> LoadSheets(string layoutPath, bool withPicture, bool dark, List<string> warnings)
        {
            List<KeyValuePair<CalloutSheet, Bitmap>> sheets = new List<KeyValuePair<CalloutSheet, Bitmap>>();
            if (string.IsNullOrWhiteSpace(layoutPath) || !File.Exists(layoutPath))
                return sheets;
            List<string> files = new List<string>();
            try
            {
                if (EngineMultiView.IsMultiViewFile(layoutPath))
                    files.AddRange(EngineMultiView.Load(layoutPath).Items.Where(i => i.Sheet.Length > 0 && File.Exists(i.Sheet)).Select(i => i.Sheet));
                else if (EngineCalloutRenderer.IsCalloutLayout(layoutPath))
                    files.Add(layoutPath);
            }
            catch (Exception ex)
            {
                warnings.Add("kneeboard: the layout '" + layoutPath + "' could not be read: " + ex.Message);
            }
            foreach (string file in files)
            {
                try
                {
                    DeviceLayoutSet set = DeviceLayoutSet.Parse(File.ReadAllText(file, Encoding.UTF8), file);
                    CalloutSheet sheet = EngineCalloutRenderer.SheetOf(set, false, warnings);
                    if (sheet == null)
                        continue;
                    Bitmap picture = withPicture && sheets.Count == 0 ? EngineCalloutRenderer.LoadPicture(set, sheet, null, dark, warnings) : null;
                    sheets.Add(new KeyValuePair<CalloutSheet, Bitmap>(sheet, picture));
                }
                catch (Exception ex)
                {
                    warnings.Add("kneeboard: the layout '" + file + "' could not be read: " + ex.Message);
                }
            }
            // A JoyPro 1 layout or a picture file: no named controls, but its picture.
            if (sheets.Count == 0 && withPicture && files.Count == 0)
            {
                LayoutFile layout;
                string error;
                if (EngineLayoutRenderer.TryLoadLayoutTemplate(layoutPath, out layout, out error) && layout.backup != null)
                    sheets.Add(new KeyValuePair<CalloutSheet, Bitmap>(new CalloutSheet(), layout.backup));
            }
            return sheets;
        }

        // DCS names commands "<control> - <position>": when every row of a group (two or more) has the same control, it
        // goes under the group's title once.
        internal static void FactorCommonTitle(Group group)
        {
            // The control of the rows without a modifier (a modifier layer often does something else); rows of the
            // modifier layer with the same control are shortened too, the others keep their whole title.
            List<Row> plain = group.Rows.Where(r => r.Modifier.Length == 0).ToList();
            if (plain.Count < 2)
                return;
            string common = null;
            foreach (Row row in plain)
            {
                int split = row.Title.LastIndexOf(" - ", StringComparison.Ordinal);
                if (split <= 0)
                    return;
                string head = row.Title.Substring(0, split).Trim();
                if (common == null)
                    common = head;
                else if (!string.Equals(common, head, StringComparison.Ordinal))
                    return;
            }
            if (string.IsNullOrEmpty(common))
                return;
            group.Common = common;
            foreach (Row row in group.Rows.Where(r => r.Title.StartsWith(common + " - ", StringComparison.Ordinal)))
                row.Title = row.Title.Substring(common.Length).TrimStart(' ', '-').Trim();
        }

        // "Brake lever detents First stage" in the group "Brake lever / Brake lever detents": "First stage".
        internal static string ShortLabel(string label, string groupTitle)
        {
            foreach (string part in (groupTitle ?? string.Empty).Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries).OrderByDescending(x => x.Length))
            {
                string head = part.Trim() + " ";
                if (head.Length > 1 && label.StartsWith(head, StringComparison.OrdinalIgnoreCase) && label.Length > head.Length)
                {
                    string rest = label.Substring(head.Length).Trim();
                    return char.ToUpperInvariant(rest[0]) + rest.Substring(1);
                }
            }
            return label;
        }

        private static string CleanTitle(string title)
        {
            string t = (title ?? string.Empty).Trim();
            return t.Length == 0 ? "Control" : char.ToUpperInvariant(t[0]) + t.Substring(1);
        }

        private static string RowLabel(CalloutRow row)
        {
            string label = (row.Label ?? string.Empty).Trim();
            return label.Length > 0 ? char.ToUpperInvariant(label[0]) + label.Substring(1) : InputName(row.Input);
        }

        private static string InputKind(string input)
        {
            string u = input.ToUpperInvariant();
            if (u.Contains("POV"))
                return "Hats";
            if (u.StartsWith("JOY_BTN", StringComparison.Ordinal))
                return "Buttons";
            if (u.StartsWith("JOY_", StringComparison.Ordinal))
                return "Axes";
            return "Other inputs";
        }

        // JOY_BTN7 -> Button 7, JOY_BTN_POV1_U -> Hat 1 up, JOY_RZ -> Rz axis, JOY_SLIDER1 -> Slider 1.
        internal static string InputName(string input)
        {
            string u = (input ?? string.Empty).Trim().ToUpperInvariant();
            Match pov = Regex.Match(u, @"POV(\d+)_?(U|D|L|R|UR|UL|DR|DL)?$");
            if (pov.Success)
            {
                string dir;
                switch (pov.Groups[2].Value)
                {
                    case "U": dir = " up"; break;
                    case "D": dir = " down"; break;
                    case "L": dir = " left"; break;
                    case "R": dir = " right"; break;
                    case "UR": dir = " up right"; break;
                    case "UL": dir = " up left"; break;
                    case "DR": dir = " down right"; break;
                    case "DL": dir = " down left"; break;
                    default: dir = string.Empty; break;
                }
                return "Hat " + pov.Groups[1].Value + dir;
            }
            Match button = Regex.Match(u, @"^JOY_BTN(\d+)$");
            if (button.Success)
                return "Button " + button.Groups[1].Value;
            Match slider = Regex.Match(u, @"^JOY_SLIDER(\d+)$");
            if (slider.Success)
                return "Slider " + slider.Groups[1].Value;
            Match axis = Regex.Match(u, @"^JOY_(R?[XYZ])$");
            if (axis.Success)
                return (axis.Groups[1].Value.Length == 2 ? "R" + axis.Groups[1].Value.Substring(1).ToLowerInvariant() : axis.Groups[1].Value) + " axis";
            return (input ?? string.Empty).Trim();
        }

        private static int InputOrder(string input)
        {
            Match n = Regex.Match(input ?? string.Empty, @"(\d+)");
            return n.Success ? int.Parse(n.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        // ------------------------------------------------------------------ drawing

        private sealed class Palette
        {
            public Color Page, Text, Faint, HeaderBack, HeaderText, GroupBack, GroupText, Stripe, Badge, BadgeText, Rule;
        }

        private static Palette PaletteOf(bool dark)
        {
            return dark
                ? new Palette
                {
                    Page = Color.FromArgb(30, 34, 40), Text = Color.FromArgb(242, 244, 247), Faint = Color.FromArgb(170, 178, 189),
                    HeaderBack = Color.FromArgb(14, 17, 21), HeaderText = Color.White, GroupBack = Color.FromArgb(46, 62, 82),
                    GroupText = Color.FromArgb(214, 232, 255), Stripe = Color.FromArgb(37, 42, 49), Badge = Color.FromArgb(255, 196, 0),
                    BadgeText = Color.Black, Rule = Color.FromArgb(70, 78, 90)
                }
                : new Palette
                {
                    Page = Color.White, Text = Color.FromArgb(17, 17, 17), Faint = Color.FromArgb(90, 96, 105),
                    HeaderBack = Color.FromArgb(22, 38, 58), HeaderText = Color.White, GroupBack = Color.FromArgb(222, 231, 242),
                    GroupText = Color.FromArgb(10, 50, 95), Stripe = Color.FromArgb(244, 246, 249), Badge = Color.FromArgb(214, 40, 40),
                    BadgeText = Color.White, Rule = Color.FromArgb(200, 206, 214)
                };
        }

        private static readonly Color[] ModifierColors =
        {
            Color.FromArgb(0, 122, 204), Color.FromArgb(200, 90, 0), Color.FromArgb(120, 60, 180), Color.FromArgb(0, 140, 90),
            Color.FromArgb(190, 30, 110), Color.FromArgb(90, 110, 20)
        };

        // One laid-out line of the list: a section title, a group title, or a bind row (with its wrapped text).
        private sealed class Item
        {
            public string Kind = "row"; // section, group, row
            public string Text = string.Empty;
            public Group Group;
            public Row Row;
            public List<string> LabelLines = new List<string>();
            public List<string> TitleLines = new List<string>();
            public float Height;
            public float LabelWidth;
            public bool TagOwnLine;
            public bool Stripe;
        }

        // The pages of one aircraft: header, the picture of the first section (when it has one), and the list.
        public static List<Bitmap> Render(List<Section> sections, string game, string plane, string headerDevice, int pageHeight, bool dark,
            string title = null, string gameName = null)
        {
            int height = Math.Max(512, pageHeight);
            int width = (int)Math.Round(height * 0.75);
            float u = height / 1024f;
            Palette palette = PaletteOf(dark);
            float margin = 18 * u;
            float headerHeight = 74 * u;
            Bitmap picture = sections.Count == 1 ? sections[0].Picture : null;
            float fullPicture = picture == null ? 0 : Math.Min(250 * u, (width - 2 * margin) * picture.Height / (float)picture.Width);
            float pictureHeight = fullPicture;
            float listTop = 0;
            float listHeightFirst = 0;
            float listHeightNext = height - (margin + headerHeight + 12 * u) - margin;
            Action<float> setPicture = h =>
            {
                pictureHeight = h;
                listTop = margin + headerHeight + (picture == null ? 12 * u : pictureHeight + 20 * u);
                listHeightFirst = height - listTop - margin;
            };
            setPicture(fullPicture);
            Dictionary<string, Color> modifierColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            foreach (string modifier in sections.SelectMany(s => s.Groups).SelectMany(g => g.Rows).Select(r => r.Modifier).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                modifierColors[modifier] = ModifierColors[modifierColors.Count % ModifierColors.Length];

            using (Bitmap probe = new Bitmap(4, 4))
            using (Graphics measure = Graphics.FromImage(probe))
            {
                measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                // The largest font (and 1 or 2 columns) that puts everything on one page; else the smallest, on more pages.
                List<List<List<Item>>> best = null;
                float bestFont = MinFont;
                int bestColumns = 2;
                float[] pictureSizes = picture == null ? new[] { 0f } : new[] { fullPicture, Math.Min(fullPicture, 170 * u), Math.Min(fullPicture, 115 * u) };
                for (float font = MaxFont; font >= MinFont - 0.01f && best == null; font -= 1f)
                {
                    foreach (float pictureSize in pictureSizes)
                    {
                        setPicture(pictureSize);
                        foreach (int columns in new[] { 1, 2 })
                        {
                            List<List<List<Item>>> pages = Paginate(measure, sections, font * u, columns, width, margin, listHeightFirst, listHeightNext, modifierColors.Keys);
                            if (pages.Count == 1)
                            {
                                best = pages;
                                bestFont = font;
                                bestColumns = columns;
                                break;
                            }
                        }
                        if (best != null)
                            break;
                    }
                }
                if (best == null)
                {
                    // More pages anyway: the picture at its full size on the first.
                    setPicture(fullPicture);
                    best = Paginate(measure, sections, MinFont * u, bestColumns, width, margin, listHeightFirst, listHeightNext, modifierColors.Keys);
                }

                List<Bitmap> result = new List<Bitmap>();
                for (int p = 0; p < best.Count; p++)
                {
                    Bitmap page = new Bitmap(width, height);
                    using (Graphics g = Graphics.FromImage(page))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        g.Clear(palette.Page);
                        DrawHeader(g, palette, HeaderTitle(plane, title), HeaderLine(headerDevice, game, gameName, plane, title), p, best.Count, width, margin, headerHeight, u);
                        float top = margin + headerHeight + 12 * u;
                        if (p == 0 && picture != null)
                        {
                            DrawPicture(g, palette, picture, sections[0].Groups, new RectangleF(margin, margin + headerHeight + 8 * u, width - 2 * margin, pictureHeight), u);
                            top = listTop;
                        }
                        DrawColumns(g, palette, best[p], bestFont * u, bestColumns, width, margin, top, modifierColors);
                    }
                    result.Add(page);
                }
                return result;
            }
        }

        private static Font NewFont(float size, FontStyle style)
        {
            foreach (string family in new[] { "Segoe UI", "Bahnschrift", "Arial" })
            {
                try
                {
                    Font font = new Font(family, size, style, GraphicsUnit.Pixel);
                    if (string.Equals(font.Name, family, StringComparison.OrdinalIgnoreCase))
                        return font;
                    font.Dispose();
                }
                catch (ArgumentException)
                {
                }
            }
            return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Pixel);
        }

        // The list items of all sections, measured for this font and column width, split into pages of columns.
        private static List<List<List<Item>>> Paginate(Graphics g, List<Section> sections, float fontSize, int columns, int width, float margin,
            float firstHeight, float nextHeight, IEnumerable<string> modifiers)
        {
            float gap = 16 * fontSize / 22f;
            float columnWidth = (width - 2 * margin - (columns - 1) * gap) / columns;
            List<Item> items = Measure(g, sections, fontSize, columnWidth);
            List<List<List<Item>>> pages = new List<List<List<Item>>>();
            List<List<Item>> page = new List<List<Item>> { new List<Item>() };
            float used = 0;
            float limit = firstHeight;
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                // A section or group title never stands alone at the bottom: it goes with its first row.
                float need = item.Height;
                if (item.Kind != "row" && i + 1 < items.Count)
                    need += items[i + 1].Height + (items[i + 1].Kind == "group" && i + 2 < items.Count ? items[i + 2].Height : 0);
                if (used + need > limit && page[page.Count - 1].Count > 0)
                {
                    if (page.Count < columns)
                        page.Add(new List<Item>());
                    else
                    {
                        pages.Add(page);
                        page = new List<List<Item>> { new List<Item>() };
                        limit = nextHeight;
                    }
                    used = 0;
                    Item header = item.Kind == "row" ? items.Take(i).LastOrDefault(x => x.Kind == "group" && x.Group == item.Group) : null;
                    if (header != null)
                    {
                        page[page.Count - 1].Add(new Item { Kind = "group", Text = header.Text + " (continued)", Group = header.Group, TitleLines = header.TitleLines, Height = header.Height });
                        used += header.Height;
                    }
                }
                page[page.Count - 1].Add(item);
                used += item.Height;
            }
            pages.Add(page);
            return pages;
        }

        private static List<Item> Measure(Graphics g, List<Section> sections, float fontSize, float columnWidth)
        {
            List<Item> items = new List<Item>();
            using (Font regular = NewFont(fontSize, FontStyle.Regular))
            using (Font bold = NewFont(fontSize, FontStyle.Bold))
            using (Font tag = NewFont(fontSize * 0.72f, FontStyle.Bold))
            {
                float line = fontSize * LineFactor;
                float pad = fontSize * PadFactor;
                // As wide as most labels need (a rare long one wraps), 18-36 % of the column.
                List<float> widths = new List<float>();
                foreach (Row row in sections.SelectMany(x => x.Groups).SelectMany(x => x.Rows))
                {
                    float tagWidth = row.Modifier.Length > 0 ? g.MeasureString(row.Modifier, tag, PointF.Empty, StringFormat.GenericTypographic).Width + fontSize * 0.6f : 0;
                    widths.Add(tagWidth + g.MeasureString(row.Label, bold, PointF.Empty, StringFormat.GenericTypographic).Width);
                }
                widths.Sort();
                float widest = widths.Count == 0 ? 0 : widths[(int)Math.Floor((widths.Count - 1) * 0.85)];
                float labelWidth = Math.Max(columnWidth * 0.18f, Math.Min(columnWidth * 0.36f, widest + fontSize * 0.5f));
                float titleWidth = columnWidth - labelWidth - fontSize * 0.5f;
                foreach (Section section in sections)
                {
                    if (sections.Count > 1)
                    {
                        using (Font sectionFont = NewFont(fontSize * 1.15f, FontStyle.Bold))
                        {
                            List<string> nameLines = Wrap(g, section.Device, sectionFont, columnWidth, columnWidth);
                            items.Add(new Item { Kind = "section", Text = section.Device, TitleLines = nameLines, Height = line * 1.2f * nameLines.Count + pad * 3 });
                        }
                    }
                    foreach (Group group in section.Groups)
                    {
                        List<string> commonLines = group.Common.Length > 0 ? Wrap(g, group.Common, regular, columnWidth - fontSize * 0.8f, columnWidth - fontSize * 0.8f) : new List<string>();
                        items.Add(new Item { Kind = "group", Text = group.Title, Group = group, TitleLines = commonLines, Height = line * (1 + commonLines.Count) + pad * 2 + fontSize * 0.25f });
                        bool stripe = false;
                        foreach (Row row in group.Rows)
                        {
                            float tagWidth = row.Modifier.Length > 0 ? g.MeasureString(row.Modifier, tag, PointF.Empty, StringFormat.GenericTypographic).Width + fontSize * 0.6f : 0;
                            // The modifier tag goes on a line of its own when the label's first word does not fit next to it.
                            string firstWord = row.Label.Split(' ')[0];
                            bool tagOwnLine = tagWidth > 0 &&
                                tagWidth + g.MeasureString(firstWord, bold, PointF.Empty, StringFormat.GenericTypographic).Width > labelWidth - fontSize * 0.25f;
                            List<string> labelLines = Wrap(g, row.Label, bold, labelWidth - fontSize * 0.25f, labelWidth - fontSize * 0.25f - (tagOwnLine ? 0 : tagWidth));
                            List<string> titleLines = Wrap(g, row.Title, regular, titleWidth, titleWidth);
                            int lines = Math.Max(labelLines.Count + (tagOwnLine ? 1 : 0), titleLines.Count);
                            items.Add(new Item { Kind = "row", Group = group, Row = row, LabelLines = labelLines, TitleLines = titleLines, Height = lines * line + pad * 2, Stripe = stripe, LabelWidth = labelWidth, TagOwnLine = tagOwnLine });
                            stripe = !stripe;
                        }
                    }
                }
            }
            return items;
        }

        // Word wrap; a word longer than the line is cut.
        private static List<string> Wrap(Graphics g, string text, Font font, float width, float firstWidth)
        {
            List<string> lines = new List<string>();
            string current = string.Empty;
            foreach (string word in (text ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                float limit = lines.Count == 0 ? firstWidth : width;
                if (current.Length > 0 && g.MeasureString(candidate, font, PointF.Empty, StringFormat.GenericTypographic).Width > limit)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                    current = candidate;
            }
            if (current.Length > 0 || lines.Count == 0)
                lines.Add(current);
            return lines;
        }

        // The big line: the aircraft's readable name ("F-16C bl.50"), else its id.
        internal static string HeaderTitle(string plane, string title)
        {
            return string.IsNullOrWhiteSpace(title) ? plane : title.Trim();
        }

        // The small line: device · game · the aircraft id when the big line shows another name (the id the binds use).
        internal static string HeaderLine(string device, string game, string gameName, string plane, string title)
        {
            List<string> parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(device))
                parts.Add(device);
            string shownGame = string.IsNullOrWhiteSpace(gameName) ? game : gameName;
            if (!string.IsNullOrWhiteSpace(shownGame) && !string.Equals(shownGame, HeaderTitle(plane, title), StringComparison.OrdinalIgnoreCase))
                parts.Add(shownGame);
            if (!string.IsNullOrWhiteSpace(plane) && !string.Equals(plane, HeaderTitle(plane, title), StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plane, game, StringComparison.OrdinalIgnoreCase))
                parts.Add(plane);
            return string.Join("  ·  ", parts);
        }

        private static void DrawHeader(Graphics g, Palette palette, string plane, string line, int page, int pages, int width, float margin, float height, float u)
        {
            RectangleF band = new RectangleF(margin, margin, width - 2 * margin, height);
            using (Brush back = new SolidBrush(palette.HeaderBack))
            using (GraphicsPath path = Rounded(band, 10 * u))
                g.FillPath(back, path);
            using (Font big = NewFont(30 * u, FontStyle.Bold))
            using (Font small = NewFont(19 * u, FontStyle.Regular))
            using (Brush text = new SolidBrush(palette.HeaderText))
            using (Brush faint = new SolidBrush(Color.FromArgb(200, palette.HeaderText)))
            {
                string pageText = pages > 1 ? (page + 1).ToString(CultureInfo.InvariantCulture) + "/" + pages.ToString(CultureInfo.InvariantCulture) : string.Empty;
                float pageWidth = pageText.Length > 0 ? g.MeasureString(pageText, big).Width + 8 * u : 0;
                RectangleF title = new RectangleF(band.X + 14 * u, band.Y + 6 * u, band.Width - 28 * u - pageWidth, 40 * u);
                using (StringFormat clip = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter })
                {
                    g.DrawString(plane, big, text, title, clip);
                    g.DrawString(line, small, faint,
                        new RectangleF(band.X + 14 * u, band.Y + 44 * u, band.Width - 28 * u, 26 * u), clip);
                }
                if (pageText.Length > 0)
                    g.DrawString(pageText, big, text, band.Right - 14 * u - pageWidth + 8 * u, band.Y + 6 * u);
            }
        }

        private static void DrawPicture(Graphics g, Palette palette, Bitmap picture, List<Group> groups, RectangleF area, float u)
        {
            float scale = Math.Min(area.Width / picture.Width, area.Height / picture.Height);
            float w = picture.Width * scale, h = picture.Height * scale;
            RectangleF target = new RectangleF(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
            g.DrawImage(picture, target);
            List<Group> numbered = groups.Where(x => x.Number > 0).ToList();
            if (numbered.Count == 0)
                return;
            List<PointF> spots = numbered.Select(x => new PointF(target.X + (float)x.SpotX * w, target.Y + (float)x.SpotY * h)).ToList();
            List<string> labels = numbered.Select(x => x.Number.ToString(CultureInfo.InvariantCulture)).ToList();
            // Close controls on a small picture: the badges first get smaller (down to 60 %), then the ones still on top
            // of each other move apart, each with a thin line back to its control.
            float rMax = Math.Max(7 * u, Math.Min(13 * u, area.Height / 13f));
            float rMin = Math.Max(5.5f * u, rMax * 0.6f);
            float r = rMax;
            List<PointF> placed = BadgePlaces(spots, labels, r, area);
            while (r > rMin && BadgesMoved(spots, placed, r))
            {
                r = Math.Max(rMin, r - 0.5f * u);
                placed = BadgePlaces(spots, labels, r, area);
            }
            using (Font badge = NewFont(r * 1.3f, FontStyle.Bold))
            using (Brush fill = new SolidBrush(palette.Badge))
            using (Brush text = new SolidBrush(palette.BadgeText))
            using (Pen ring = new Pen(palette.Page, 2.5f * u * r / rMax))
            using (Pen leader = new Pen(palette.Badge, Math.Max(1f, 1.5f * u)))
            using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                for (int i = 0; i < numbered.Count; i++)
                {
                    if (Distance(spots[i], placed[i]) > r * 0.5f)
                    {
                        g.DrawLine(leader, spots[i], placed[i]);
                        g.FillEllipse(fill, spots[i].X - 2 * u, spots[i].Y - 2 * u, 4 * u, 4 * u);
                    }
                }
                for (int i = 0; i < numbered.Count; i++)
                {
                    float bw = BadgeWidth(labels[i], r);
                    RectangleF circle = new RectangleF(placed[i].X - bw / 2, placed[i].Y - r, bw, 2 * r);
                    using (GraphicsPath pill = Rounded(circle, r))
                    {
                        g.FillPath(fill, pill);
                        g.DrawPath(ring, pill);
                    }
                    g.DrawString(labels[i], badge, text, circle, center);
                }
            }
        }

        // A badge's width: a circle for one digit, a pill for more.
        private static float BadgeWidth(string label, float r)
        {
            return label.Length <= 1 ? 2 * r : 2 * r + (label.Length - 1) * r * 0.75f;
        }

        private static float Distance(PointF a, PointF b)
        {
            return (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        }

        private static bool BadgesMoved(List<PointF> spots, List<PointF> placed, float r)
        {
            for (int i = 0; i < spots.Count; i++)
                if (Distance(spots[i], placed[i]) > r * 0.5f)
                    return true;
            return false;
        }

        // Where the badges go: on their controls, pushed apart (a few rounds) where two would overlap; inside the area.
        internal static List<PointF> BadgePlaces(List<PointF> spots, List<string> labels, float r, RectangleF area)
        {
            List<PointF> p = spots.ToList();
            float gap = r * 0.15f;
            for (int round = 0; round < 60; round++)
            {
                bool moved = false;
                for (int i = 0; i < p.Count; i++)
                {
                    for (int j = i + 1; j < p.Count; j++)
                    {
                        float need = (BadgeWidth(labels[i], r) + BadgeWidth(labels[j], r)) / 2 + gap;
                        float dx = p[j].X - p[i].X, dy = p[j].Y - p[i].Y;
                        float d = (float)Math.Sqrt(dx * dx + dy * dy);
                        if (d >= need)
                            continue;
                        if (d < 0.01f)
                        {
                            double angle = (i * 7 + j * 3) * 0.9;
                            dx = (float)Math.Cos(angle);
                            dy = (float)Math.Sin(angle);
                            d = 1f;
                        }
                        float push = (need - d) / 2 + 0.01f;
                        p[i] = new PointF(p[i].X - dx / d * push, p[i].Y - dy / d * push);
                        p[j] = new PointF(p[j].X + dx / d * push, p[j].Y + dy / d * push);
                        moved = true;
                    }
                }
                for (int i = 0; i < p.Count; i++)
                {
                    float half = BadgeWidth(labels[i], r) / 2;
                    p[i] = new PointF(Math.Min(area.Right - half, Math.Max(area.Left + half, p[i].X)), Math.Min(area.Bottom - r, Math.Max(area.Top + r, p[i].Y)));
                }
                if (!moved)
                    break;
            }
            return p;
        }

        private static void DrawColumns(Graphics g, Palette palette, List<List<Item>> columns, float fontSize, int columnCount, int width, float margin, float top,
            Dictionary<string, Color> modifierColors)
        {
            float gap = 16 * fontSize / 22f;
            float columnWidth = (width - 2 * margin - (columnCount - 1) * gap) / columnCount;
            float line = fontSize * LineFactor;
            float pad = fontSize * PadFactor;
            using (Font regular = NewFont(fontSize, FontStyle.Regular))
            using (Font bold = NewFont(fontSize, FontStyle.Bold))
            using (Font groupFont = NewFont(fontSize * 1.02f, FontStyle.Bold))
            using (Font sectionFont = NewFont(fontSize * 1.15f, FontStyle.Bold))
            using (Font tag = NewFont(fontSize * 0.72f, FontStyle.Bold))
            using (Brush text = new SolidBrush(palette.Text))
            using (Brush groupBack = new SolidBrush(palette.GroupBack))
            using (Brush groupText = new SolidBrush(palette.GroupText))
            using (Brush stripe = new SolidBrush(palette.Stripe))
            using (Brush badge = new SolidBrush(palette.Badge))
            using (Brush badgeText = new SolidBrush(palette.BadgeText))
            using (Pen rule = new Pen(palette.Rule, Math.Max(1f, fontSize / 14f)))
            using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (StringFormat clip = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center })
            {
                for (int c = 0; c < columns.Count; c++)
                {
                    float x = margin + c * (columnWidth + gap);
                    float y = top;
                    foreach (Item item in columns[c])
                    {
                        if (item.Kind == "section")
                        {
                            float h = line * 1.2f;
                            for (int i = 0; i < item.TitleLines.Count; i++)
                                g.DrawString(item.TitleLines[i], sectionFont, text, new RectangleF(x, y + pad + i * h, columnWidth, h), clip);
                            float bottom = y + pad + h * item.TitleLines.Count;
                            g.DrawLine(rule, x, bottom, x + columnWidth, bottom);
                        }
                        else if (item.Kind == "group")
                        {
                            RectangleF full = new RectangleF(x, y + fontSize * 0.25f, columnWidth, line * (1 + item.TitleLines.Count) + pad * 2);
                            using (GraphicsPath path = Rounded(full, fontSize * 0.3f))
                                g.FillPath(groupBack, path);
                            RectangleF band = new RectangleF(full.X, full.Y, full.Width, line + pad * 2);
                            for (int i = 0; i < item.TitleLines.Count; i++)
                                g.DrawString(item.TitleLines[i], regular, groupText, full.X + fontSize * 0.4f, band.Bottom - pad + i * line + (line - fontSize * 1.33f) / 2);
                            float textX = band.X + fontSize * 0.4f;
                            if (item.Group.Number > 0)
                            {
                                float r = line * 0.42f;
                                string number = item.Group.Number.ToString(CultureInfo.InvariantCulture);
                                using (Font small = NewFont(fontSize * 0.75f, FontStyle.Bold))
                                {
                                    float w = Math.Max(2 * r, g.MeasureString(number, small, PointF.Empty, StringFormat.GenericTypographic).Width + r);
                                    RectangleF circle = new RectangleF(textX, band.Y + band.Height / 2 - r, w, 2 * r);
                                    using (GraphicsPath pill = Rounded(circle, r))
                                        g.FillPath(badge, pill);
                                    g.DrawString(number, small, badgeText, circle, center);
                                    textX += w + fontSize * 0.35f;
                                }
                            }
                            g.DrawString(item.Text, groupFont, groupText, new RectangleF(textX, band.Y, band.Right - textX - fontSize * 0.3f, band.Height), clip);
                        }
                        else
                        {
                            if (item.Stripe)
                                g.FillRectangle(stripe, x, y, columnWidth, item.Height);
                            float ly = y + pad;
                            float lx = x + fontSize * 0.25f;
                            bool tagOwnLine = false;
                            float labelWidth = item.LabelWidth;
                            if (item.Row.Modifier.Length > 0)
                            {
                                SizeF size = g.MeasureString(item.Row.Modifier, tag, PointF.Empty, StringFormat.GenericTypographic);
                                RectangleF pill = new RectangleF(lx, ly + (line - fontSize * 0.95f) / 2, size.Width + fontSize * 0.45f, fontSize * 0.95f);
                                Color color;
                                modifierColors.TryGetValue(item.Row.Modifier, out color);
                                using (Brush pillBrush = new SolidBrush(color.A == 0 ? ModifierColors[0] : color))
                                using (GraphicsPath path = Rounded(pill, pill.Height / 2))
                                    g.FillPath(pillBrush, path);
                                using (Brush white = new SolidBrush(Color.White))
                                    g.DrawString(item.Row.Modifier, tag, white, pill, center);
                                tagOwnLine = item.TagOwnLine;
                                if (!tagOwnLine)
                                    lx += pill.Width + fontSize * 0.15f;
                            }
                            float labelY = tagOwnLine ? ly + line : ly;
                            for (int i = 0; i < item.LabelLines.Count; i++)
                                g.DrawString(item.LabelLines[i], bold, text, (i == 0 ? lx : x + fontSize * 0.25f), labelY + i * line + (line - fontSize * 1.33f) / 2, StringFormat.GenericTypographic);
                            float tx = x + labelWidth + fontSize * 0.5f;
                            for (int i = 0; i < item.TitleLines.Count; i++)
                                g.DrawString(item.TitleLines[i], regular, text, tx, ly + i * line + (line - fontSize * 1.33f) / 2, StringFormat.GenericTypographic);
                        }
                        y += item.Height;
                    }
                }
            }
        }

        private static GraphicsPath Rounded(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = Math.Max(0.1f, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
