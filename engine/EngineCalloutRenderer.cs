using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.DeviceReference;

namespace JoyPro.Engine
{
    public sealed class CalloutRenderOptions
    {
        public bool Dark = true;
        // The width of the written picture in pixels (A4 landscape at 300 dpi = 3508); 0 = the sheet's own size.
        public int PrintWidth = 3508;
        // GAME / PLANE header band (JoyPro 1's look); empty game = no header.
        public string Game = string.Empty;
        public string Plane = string.Empty;
        public string Device = string.Empty;
        // The leader lines' colour (#RRGGBB; empty = the palette's): the drawing style's accent on JoyPro's drawings.
        public string LineColor = string.Empty;
    }

    // Draws a callout sheet (docs/device-reference.md, "Tables and lines") like JoyPro 1's cheat sheets: the device
    // picture, a table per control group with the bound command and the modifier bind, thin orthogonal leader lines,
    // and a GAME / PLANE band. The same sheet the Layout page shows (CalloutSheet, shared code), so the GUI's cheat
    // sheets and "engine layout render" / "engine layout callouts" give the same picture.
    internal static class EngineCalloutRenderer
    {
        private sealed class Palette
        {
            public Color Page, Table, Title, Mod, Border, Grid, Text, Faint, Line, LineGlow, Header, HeaderText, HeaderBox, PictureBack;
        }

        private static Palette PaletteOf(bool dark)
        {
            if (dark)
            {
                return new Palette
                {
                    Page = Hex("#2B3038"), Table = Hex("#F7F7F7"), Title = Hex("#D5DAE1"), Mod = Hex("#E9ECF0"), Border = Hex("#0E1116"),
                    Grid = Hex("#9AA1AB"), Text = Hex("#0E1116"), Faint = Hex("#8A919B"), Line = Hex("#E6E8EB"), LineGlow = Color.FromArgb(90, 0, 0, 0),
                    Header = Hex("#15181D"), HeaderText = Hex("#F2F4F7"), HeaderBox = Hex("#F7F7F7"), PictureBack = Hex("#0B1220")
                };
            }
            return new Palette
            {
                Page = Hex("#FFFFFF"), Table = Hex("#FFFFFF"), Title = Hex("#E3E7ED"), Mod = Hex("#F3F5F8"), Border = Hex("#2F3640"),
                Grid = Hex("#B7BDC6"), Text = Hex("#111418"), Faint = Hex("#9AA1AB"), Line = Hex("#7C8794"), LineGlow = Color.FromArgb(0, 0, 0, 0),
                Header = Hex("#2F3640"), HeaderText = Hex("#FFFFFF"), HeaderBox = Hex("#FFFFFF"), PictureBack = Hex("#EEF2F7")
            };
        }

        // The font of the tables: JoyPro 1's narrow look where Windows has it.
        private static FontFamily TextFamily()
        {
            foreach (string name in new[] { "Bahnschrift SemiCondensed", "Bahnschrift", "Segoe UI" })
            {
                try
                {
                    return new FontFamily(name);
                }
                catch (ArgumentException)
                {
                }
            }
            return FontFamily.GenericSansSerif;
        }

        public static double HeaderHeight(CalloutSheet sheet)
        {
            return Math.Round(sheet.RowHeight * 1.6);
        }

        // text(input, modifier) = what a row shows; "" = nothing (an unbound input).
        public static Bitmap Render(CalloutSheet sheet, Image picture, Func<string, bool, string> text, CalloutRenderOptions options)
        {
            Func<string, int, string> byColumn = text == null ? (Func<string, int, string>)null : (input, column) => text(input, column > 0);
            return Render(sheet, picture, byColumn, options);
        }

        // text(input, column) = what a row shows in a column (0 = no modifier, 1.. = the sheet's modifier columns,
        // CalloutSheet.ComboOfColumn); "" = nothing (an unbound input).
        public static Bitmap Render(CalloutSheet sheet, Image picture, Func<string, int, string> text, CalloutRenderOptions options)
        {
            options = options ?? new CalloutRenderOptions();
            Palette pal = PaletteOf(options.Dark);
            if (DrawingStyle.NormalizeColor(options.LineColor).Length > 0)
                pal.Line = Hex(DrawingStyle.NormalizeColor(options.LineColor));
            bool header = options.Game.Length > 0 || options.Plane.Length > 0;
            double headerH = header ? HeaderHeight(sheet) : 0;
            double sheetW = Math.Max(1, sheet.Width), sheetH = Math.Max(1, sheet.Height + headerH);
            float scale = options.PrintWidth > 0 ? (float)Math.Max(0.25, Math.Min(4.0, options.PrintWidth / sheetW)) : 1f;
            // Exactly the print width when the sheet is scaled to it (float rounding gave 3509 for 3508).
            bool exact = options.PrintWidth > 0 && Math.Abs(scale - options.PrintWidth / sheetW) < 1e-4;
            int w = exact ? options.PrintWidth : Math.Max(1, (int)Math.Ceiling(sheetW * scale - 1e-3)), h = Math.Max(1, (int)Math.Ceiling(sheetH * scale - 1e-3));
            Bitmap output = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            output.SetResolution(300, 300);
            using (Graphics g = Graphics.FromImage(output))
            using (FontFamily family = TextFamily())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(pal.Page);
                g.ScaleTransform(scale, scale);

                RectangleF pic = new RectangleF((float)sheet.PictureX, (float)sheet.PictureY, (float)sheet.PictureWidth, (float)sheet.PictureHeight);
                if (picture != null)
                    g.DrawImage(picture, pic);
                else
                    using (SolidBrush back = new SolidBrush(pal.PictureBack))
                        g.FillRectangle(back, pic);

                float rh = (float)sheet.RowHeight;
                using (Font rowFont = new Font(family, rh * 0.5f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Font titleFont = new Font(family, rh * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Font smallFont = new Font(family, rh * 0.36f, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    DrawLines(g, sheet, pal);
                    foreach (CalloutTable table in sheet.Tables)
                        DrawTable(g, sheet, table, pal, rowFont, titleFont, smallFont, text);
                    if (sheet.OtherY >= 0)
                    {
                        using (SolidBrush brush = new SolidBrush(options.Dark ? pal.HeaderText : pal.Text))
                            g.DrawString("Other controls (no spot on this picture yet)", titleFont, brush, (float)(sheet.RowHeight * 0.8), (float)sheet.OtherY);
                    }
                    if (header)
                        DrawHeader(g, sheet, pal, family, (float)sheet.Height, (float)headerH, options);
                }
            }
            return output;
        }

        private static void DrawLines(Graphics g, CalloutSheet sheet, Palette pal)
        {
            float width = (float)Math.Max(1.5, sheet.RowHeight * 0.07);
            using (Pen glow = new Pen(pal.LineGlow, width * 3f) { LineJoin = LineJoin.Miter })
            using (Pen pen = new Pen(pal.Line, width) { LineJoin = LineJoin.Miter })
            using (SolidBrush dot = new SolidBrush(pal.Line))
            using (Pen dotRing = new Pen(pal.Border, width * 0.8f))
            {
                foreach (CalloutLine line in sheet.Lines)
                {
                    if (line.Points.Count < 2)
                        continue;
                    PointF[] points = line.Points.Select(p => new PointF((float)p[0], (float)p[1])).ToArray();
                    if (pal.LineGlow.A > 0)
                        g.DrawLines(glow, points);
                    g.DrawLines(pen, points);
                    float r = (float)(sheet.RowHeight * 0.13);
                    g.FillEllipse(dot, points[0].X - r, points[0].Y - r, r * 2, r * 2);
                    g.DrawEllipse(dotRing, points[0].X - r, points[0].Y - r, r * 2, r * 2);
                }
            }
        }

        private static void DrawTable(Graphics g, CalloutSheet sheet, CalloutTable table, Palette pal, Font rowFont, Font titleFont, Font smallFont, Func<string, int, string> text)
        {
            double[] r = sheet.RectOf(table);
            RectangleF rect = new RectangleF((float)r[0], (float)r[1], (float)r[2], (float)r[3]);
            float th = (float)sheet.TitleHeight, rh = (float)sheet.RowHeight;
            List<CalloutRow> rows = table.VisibleRows();
            using (SolidBrush fill = new SolidBrush(pal.Table))
            using (SolidBrush titleFill = new SolidBrush(pal.Title))
            using (SolidBrush modFill = new SolidBrush(pal.Mod))
            using (SolidBrush textBrush = new SolidBrush(pal.Text))
            using (SolidBrush faintBrush = new SolidBrush(pal.Faint))
            using (Pen gridPen = new Pen(pal.Grid, Math.Max(1f, rh * 0.03f)))
            using (Pen borderPen = new Pen(pal.Border, Math.Max(1.5f, rh * 0.07f)))
            using (StringFormat left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                g.FillRectangle(fill, rect);
                g.FillRectangle(titleFill, rect.X, rect.Y, rect.Width, th);
                // Several modifier combinations: each column is headed with its combination, the title keeps the main
                // column.
                int columns = sheet.ModColumnCount(table);
                bool headed = columns > 0 && sheet.Combos.Count > 0;
                float titleWidth = headed ? (float)(rect.Width - sheet.ModColumnWidth(table)) : rect.Width;
                g.DrawString(table.Title, titleFont, textBrush, new RectangleF(rect.X + rh * 0.25f, rect.Y, titleWidth - rh * 0.5f, th), left);
                if (headed)
                {
                    for (int c = 1; c <= columns; c++)
                    {
                        double[] head = sheet.CellOf(table, 0, c);
                        RectangleF headCell = new RectangleF((float)head[0], rect.Y, (float)head[2], th);
                        g.FillRectangle(modFill, headCell);
                        g.DrawLine(gridPen, headCell.X, headCell.Y, headCell.X, headCell.Bottom);
                        g.DrawString(CalloutCombos.Label(sheet.ComboOfColumn(c)), smallFont, faintBrush, new RectangleF(headCell.X + rh * 0.12f, headCell.Y, headCell.Width - rh * 0.2f, th), left);
                    }
                }
                g.DrawLine(gridPen, rect.X, rect.Y + th, rect.Right, rect.Y + th);
                for (int i = 0; i < rows.Count; i++)
                {
                    CalloutRow row = rows[i];
                    double[] main = sheet.CellOf(table, i, false);
                    RectangleF cell = new RectangleF((float)main[0], (float)main[1], (float)main[2], (float)main[3]);
                    if (table.IsGrid)
                    {
                        string value = row.Input.Length > 0 && text != null ? (text(row.Input, 0) ?? string.Empty) : string.Empty;
                        string label = row.Label + ":";
                        SizeF size = g.MeasureString(label, smallFont);
                        g.DrawString(label, smallFont, faintBrush, new RectangleF(cell.X + rh * 0.15f, cell.Y, size.Width + 2, cell.Height), left);
                        g.DrawString(value, rowFont, textBrush, new RectangleF(cell.X + rh * 0.15f + size.Width, cell.Y, cell.Width - size.Width - rh * 0.3f, cell.Height), left);
                        // A key's cell (its lines together: a switch's up and down), lines of one key parted faintly.
                        if (sheet.FirstLineOfCell(table, rows, i))
                        {
                            double[] box = sheet.KeyCellOf(table, i);
                            g.DrawRectangle(gridPen, (float)box[0], (float)box[1], (float)box[2], (float)box[3]);
                        }
                        else
                        {
                            using (Pen faintPen = new Pen(Color.FromArgb(90, pal.Grid), Math.Max(0.8f, rh * 0.02f)))
                                g.DrawLine(faintPen, cell.X + rh * 0.3f, cell.Y, cell.Right - rh * 0.3f, cell.Y);
                        }
                        continue;
                    }
                    if (i > 0)
                        g.DrawLine(gridPen, rect.X, cell.Y, rect.Right, cell.Y);
                    RectangleF icon = new RectangleF(cell.X, cell.Y, rh, rh);
                    DrawIcon(g, row.Icon, row.Label, icon, pal, smallFont);
                    string mainText = row.Input.Length > 0 && text != null ? (text(row.Input, 0) ?? string.Empty) : string.Empty;
                    g.DrawString(mainText, rowFont, textBrush, new RectangleF(cell.X + rh * 1.08f, cell.Y, cell.Width - rh * 1.2f, cell.Height), left);
                    if (row.Icon == "zone")
                        g.DrawString(row.Label, rowFont, faintBrush, new RectangleF(cell.X + rh * 1.08f, cell.Y, cell.Width - rh * 1.2f, cell.Height), left);
                    for (int c = 1; c <= columns; c++)
                    {
                        double[] mod = sheet.CellOf(table, i, c);
                        RectangleF modCell = new RectangleF((float)mod[0], (float)mod[1], (float)mod[2], (float)mod[3]);
                        g.FillRectangle(modFill, modCell);
                        g.DrawLine(gridPen, modCell.X, modCell.Y, modCell.X, modCell.Bottom);
                        string modText = row.Input.Length > 0 && text != null ? (text(row.Input, c) ?? string.Empty) : string.Empty;
                        g.DrawString(modText, rowFont, textBrush, new RectangleF(modCell.X + rh * 0.15f, modCell.Y, modCell.Width - rh * 0.25f, modCell.Height), left);
                    }
                }
                if (!table.IsGrid)
                    g.DrawLine(gridPen, rect.X + rh, rect.Y + th, rect.X + rh, rect.Bottom);
                g.DrawRectangle(borderPen, rect.X, rect.Y, rect.Width, rect.Height);
            }
        }

        // The row icons of JoyPro 1: a button cap, an arrow in a ring for a direction, a four-way ring for an axis.
        private static void DrawIcon(Graphics g, string icon, string label, RectangleF cell, Palette pal, Font smallFont)
        {
            float cx = cell.X + cell.Width / 2, cy = cell.Y + cell.Height / 2, r = cell.Height * 0.34f;
            using (Pen ring = new Pen(pal.Text, Math.Max(1.2f, cell.Height * 0.07f)))
            using (SolidBrush ink = new SolidBrush(pal.Text))
            {
                switch (icon)
                {
                    case "button":
                        g.FillEllipse(ink, cx - r, cy - r * 0.55f, r * 2, r * 1.1f);
                        break;
                    case "zone":
                        g.FillRectangle(ink, cx - r, cy - r * 0.18f, r * 2, r * 0.36f);
                        break;
                    case "mid":
                        g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
                        g.FillEllipse(ink, cx - r * 0.35f, cy - r * 0.35f, r * 0.7f, r * 0.7f);
                        break;
                    case "pos":
                        g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
                        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            g.DrawString(label, smallFont, ink, new RectangleF(cx - r, cy - r, r * 2, r * 2), center);
                        break;
                    case "axis":
                        g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
                        foreach (float angle in new[] { 0f, 90f, 180f, 270f })
                            g.FillPolygon(ink, Arrow(cx, cy, r * 0.75f, angle, r * 0.42f));
                        break;
                    default:
                        float a = AngleOf(icon);
                        if (float.IsNaN(a))
                            goto case "button";
                        g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
                        g.FillPolygon(ink, Arrow(cx, cy, r * 0.72f, a, r * 0.7f));
                        using (Pen stem = new Pen(pal.Text, Math.Max(1.2f, cell.Height * 0.08f)))
                        {
                            double rad = a * Math.PI / 180;
                            g.DrawLine(stem, cx - (float)Math.Cos(rad) * r * 0.55f, cy - (float)Math.Sin(rad) * r * 0.55f, cx + (float)Math.Cos(rad) * r * 0.1f, cy + (float)Math.Sin(rad) * r * 0.1f);
                        }
                        break;
                }
            }
        }

        // Screen angle in degrees (0 = right, 90 = down) of a direction icon; NaN for other icons.
        internal static float AngleOf(string icon)
        {
            switch (icon)
            {
                case "right": return 0;
                case "downright": return 45;
                case "down": return 90;
                case "downleft": return 135;
                case "left": return 180;
                case "upleft": return 225;
                case "up": return 270;
                case "upright": return 315;
            }
            return float.NaN;
        }

        // A triangle whose tip is `reach` from the centre in the direction `angle`.
        private static PointF[] Arrow(float cx, float cy, float reach, float angle, float size)
        {
            double a = angle * Math.PI / 180;
            float tx = cx + (float)Math.Cos(a) * reach, ty = cy + (float)Math.Sin(a) * reach;
            float bx = cx + (float)Math.Cos(a) * (reach - size), by = cy + (float)Math.Sin(a) * (reach - size);
            float nx = -(float)Math.Sin(a) * size * 0.6f, ny = (float)Math.Cos(a) * size * 0.6f;
            return new[] { new PointF(tx, ty), new PointF(bx + nx, by + ny), new PointF(bx - nx, by - ny) };
        }

        private static void DrawHeader(Graphics g, CalloutSheet sheet, Palette pal, FontFamily family, float top, float height, CalloutRenderOptions options)
        {
            float w = (float)sheet.Width, pad = height * 0.18f;
            using (SolidBrush band = new SolidBrush(pal.Header))
            using (SolidBrush box = new SolidBrush(pal.HeaderBox))
            using (SolidBrush label = new SolidBrush(pal.HeaderText))
            using (SolidBrush value = new SolidBrush(pal.Text))
            using (Font big = new Font(family, height * 0.56f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font boxFont = new Font(family, height * 0.42f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (StringFormat center = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                g.FillRectangle(band, 0, top, w, height);
                List<KeyValuePair<string, string>> fields = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("GAME:", options.Game),
                    new KeyValuePair<string, string>("PLANE:", options.Plane)
                };
                if (options.Device.Length > 0)
                    fields.Add(new KeyValuePair<string, string>("DEVICE:", options.Device));
                float slot = w / fields.Count, x = pad;
                foreach (KeyValuePair<string, string> field in fields)
                {
                    SizeF size = g.MeasureString(field.Key, big);
                    g.DrawString(field.Key, big, label, new RectangleF(x, top, size.Width + 4, height), center);
                    RectangleF valueBox = new RectangleF(x + size.Width + pad * 0.5f, top + pad, Math.Max(10, slot - size.Width - pad * 2.5f), height - pad * 2);
                    g.FillRectangle(box, valueBox);
                    g.DrawString(field.Value, boxFont, value, new RectangleF(valueBox.X + pad * 0.5f, valueBox.Y, valueBox.Width - pad, valueBox.Height), center);
                    x += slot;
                }
            }
        }

        // ------------------------------------------------------------------ callout layout files

        // A JoyPro device layout (joypro-device-layout JSON) that carries tables and lines.
        public static bool IsCalloutLayout(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                return false;
            try
            {
                string head = File.ReadAllText(path, Encoding.UTF8);
                return head.IndexOf(DeviceLayoutSet.FormatName, StringComparison.Ordinal) >= 0 && head.IndexOf("\"callouts\"", StringComparison.Ordinal) >= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The drawing style of exports: the --drawing-* options of this run, else the user's choice in the GUI's
        // settings (ExportDrawingStyle, else DrawingStyle), else JoyPro's default.
        internal static DrawingStyle ExportStyle;

        internal static DrawingStyle CurrentStyle()
        {
            return ExportStyle ?? DrawingStyle.FromSettings(DeviceCatalog.DataRoot(), true);
        }

        // The leader colour of a sheet on JoyPro's drawing: the style's accent; empty for photos (the palette's).
        internal static void ApplyStyle(CalloutRenderOptions options, DeviceLayoutSet set)
        {
            if (options != null && set != null && set.PhotoKind == "lineart")
                options.LineColor = CurrentStyle().Resolve(options.Dark).Accent;
        }

        // The picture of a device layout: the user's photo inside the file, its local copy, or JoyPro's drawing of the
        // model. A maker's photo is never downloaded here: the GUI keeps a local copy when it saves the sheet.
        public static Bitmap LoadPicture(DeviceLayoutSet set, CalloutSheet sheet, string pictureOverride, bool dark, List<string> warnings)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(pictureOverride))
                    return LoadBitmap(pictureOverride);
                if (set.PhotoFileBase64.Length > 0)
                {
                    using (MemoryStream stream = new MemoryStream(Convert.FromBase64String(set.PhotoFileBase64)))
                    using (Image image = Image.FromStream(stream))
                        return new Bitmap(image);
                }
                if (set.LocalPhotoFile.Length > 0 && File.Exists(set.LocalPhotoFile))
                    return LoadBitmap(set.LocalPhotoFile);
                if (set.PhotoKind == "lineart")
                {
                    // JoyPro's line drawing of the photo the positions belong to: shipped, nothing to download.
                    // The set names the photo's hash, or the drawing's own key when JoyPro does not know the photo's.
                    List<DeviceLineArt> arts = DeviceLineArtDb.Cached(DeviceLineArtDb.FolderOf(DeviceCatalog.CatalogFolder(MainStructure.PROGPATH)));
                    DeviceLineArt art = DeviceLineArtDb.OfPhoto(arts, set.Model, set.PhotoSha256) ??
                                        arts.FirstOrDefault(a => string.Equals(a.Model, set.Model, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Key, set.PhotoSha256, StringComparison.OrdinalIgnoreCase));
                    if (art != null)
                        return EngineLineArtBitmap.Render(art, (int)Math.Max(1, Math.Round(sheet.PictureWidth)), CurrentStyle().Resolve(dark));
                    if (warnings != null)
                        warnings.Add("layout: JoyPro has no line drawing of '" + set.Model + "' for this picture.");
                    return null;
                }
                if (set.PhotoKind == "drawing" || set.PhotoKind == "schematic")
                {
                    DeviceCatalog catalog = Program.LoadReferenceCatalog();
                    RefDevice model = catalog.Find(set.Model);
                    if (model != null)
                    {
                        DeviceSchematic schematic = SchematicBuilder.Build(model, catalog.BaseOf(model));
                        return EngineSchematicBitmap.Render(schematic, (int)Math.Max(1, Math.Round(sheet.PictureWidth)), dark);
                    }
                    if (warnings != null)
                        warnings.Add("layout: the model '" + set.Model + "' is not in the device catalog; the sheet has no picture.");
                    return null;
                }
                if (warnings != null)
                    warnings.Add("layout: no local copy of the picture of '" + set.Model + "' (" + (set.PhotoUrl.Length > 0 ? set.PhotoUrl : set.PhotoKind) +
                                 "); open the device on the Layout page once, or pass --picture <file>.");
            }
            catch (Exception ex)
            {
                if (warnings != null)
                    warnings.Add("layout: the picture of '" + set.Model + "' could not be read: " + ex.Message);
            }
            return null;
        }

        private static Bitmap LoadBitmap(string file)
        {
            using (FileStream stream = File.OpenRead(file))
            using (Image image = Image.FromStream(stream))
                return new Bitmap(image);
        }

        // The sheet of a layout: the saved one, else arranged from the layout's positions on the model's parts.
        public static CalloutSheet SheetOf(DeviceLayoutSet set, bool arrange, List<string> warnings)
        {
            if (set.Callouts != null && !arrange)
                return set.Callouts;
            DeviceCatalog catalog = Program.LoadReferenceCatalog();
            RefDevice model = catalog.Find(set.Model);
            if (model == null)
            {
                if (warnings != null)
                    warnings.Add("layout: the model '" + set.Model + "' is not in the device catalog.");
                return set.Callouts;
            }
            DeviceSchematic schematic = SchematicBuilder.Build(model, catalog.BaseOf(model));
            double pw = set.Callouts != null ? set.Callouts.PictureWidth : schematic.Width;
            double ph = set.Callouts != null ? set.Callouts.PictureHeight : schematic.Height;
            return CalloutLayouter.Arrange(schematic, set.Markers, pw, ph, set.Callouts);
        }

        // Several sheets on one page, one under the other (multi-device pages).
        public static Bitmap Combine(IList<string> files, bool dark)
        {
            List<Image> images = new List<Image>();
            try
            {
                foreach (string file in files)
                    images.Add(LoadBitmap(file));
                int gap = 24;
                int width = images.Max(i => i.Width);
                int height = images.Sum(i => i.Height) + gap * (images.Count - 1);
                Bitmap page = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                page.SetResolution(300, 300);
                using (Graphics g = Graphics.FromImage(page))
                {
                    g.Clear(PaletteOf(dark).Page);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    int y = 0;
                    foreach (Image image in images)
                    {
                        g.DrawImage(image, (width - image.Width) / 2, y, image.Width, image.Height);
                        y += image.Height + gap;
                    }
                }
                return page;
            }
            finally
            {
                foreach (Image image in images)
                    image.Dispose();
            }
        }

        private static Color Hex(string hex)
        {
            return ColorTranslator.FromHtml(hex);
        }
    }
}
