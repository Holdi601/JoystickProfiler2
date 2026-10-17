using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.DeviceReference;

namespace JoyPro.Engine
{
    // The Cockpits page's manual pages (CockpitManualPages) as bitmaps and as a PDF: the same layout the GUI shows,
    // drawn with System.Drawing (engine cockpits render --page ..., the export of the Cockpits page).
    internal static class EngineManualPageRenderer
    {
        public static Bitmap Render(ManualPage page, double scale, DrawingColors colors, bool dark)
        {
            int width = Math.Max(1, (int)Math.Ceiling(page.Width * scale));
            int height = Math.Max(1, (int)Math.Ceiling(page.Height * scale));
            Bitmap image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(image))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(EngineLineArtBitmap.ToColor(page.Paper));
                foreach (ManualPageItem item in page.Items)
                    Draw(g, item, (float)scale, colors, dark);
            }
            return image;
        }

        private static void Draw(Graphics g, ManualPageItem item, float s, DrawingColors colors, bool dark)
        {
            RectangleF r = new RectangleF((float)item.X * s, (float)item.Y * s, (float)item.W * s, (float)item.H * s);
            switch (item.Kind)
            {
                case "rect":
                case "cell":
                    if (item.Fill.Length > 0)
                    {
                        using (SolidBrush b = new SolidBrush(EngineLineArtBitmap.ToColor(item.Fill)))
                            g.FillRectangle(b, r);
                    }
                    if (item.Stroke.Length > 0)
                    {
                        using (Pen p = new Pen(EngineLineArtBitmap.ToColor(item.Stroke), (float)item.StrokeWidth * s))
                            g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                    }
                    if (item.Lines.Count > 0)
                        DrawLines(g, item, r, s, true);
                    break;
                case "text":
                    DrawLines(g, item, r, s, false);
                    break;
                case "line":
                    if (item.Points != null && item.Points.Length >= 4)
                    {
                        PointF[] pts = Enumerable.Range(0, item.Points.Length / 2).Select(i => new PointF((float)item.Points[2 * i] * s, (float)item.Points[2 * i + 1] * s)).ToArray();
                        using (Pen p = new Pen(EngineLineArtBitmap.ToColor(item.Stroke), (float)item.StrokeWidth * s) { LineJoin = LineJoin.Round })
                            g.DrawLines(p, pts);
                    }
                    break;
                case "dot":
                    using (SolidBrush b = new SolidBrush(EngineLineArtBitmap.ToColor(item.Fill)))
                        g.FillEllipse(b, r);
                    break;
                case "drawing":
                    if (item.Art == null)
                        break;
                    if (item.Art.HasPicture)
                    {
                        // A picture made from the user's game.
                        try
                        {
                            using (Bitmap picture = new Bitmap(item.Art.Picture))
                            {
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.DrawImage(picture, r);
                            }
                        }
                        catch (Exception)
                        {
                        }
                        break;
                    }
                    int w = Math.Max(1, (int)Math.Round(r.Width));
                    using (Bitmap art = item.Art.LineArt != null ? EngineLineArtBitmap.Render(item.Art.LineArt, w, colors, true) : EngineSchematicBitmap.Render(item.Art.Schematic, w, dark))
                        g.DrawImage(art, r);
                    break;
            }
        }

        private static void DrawLines(Graphics g, ManualPageItem item, RectangleF r, float s, bool centreVertically)
        {
            float size = (float)item.Size * s;
            float lineH = (float)(item.Size * ManualLayout.LineHeight) * s;
            float y = centreVertically ? r.Y + (r.Height - lineH * item.Lines.Count) / 2 : r.Y;
            Color ink = item.Color.Length > 0 ? EngineLineArtBitmap.ToColor(item.Color) : Color.Black;
            using (SolidBrush brush = new SolidBrush(ink))
            using (StringFormat format = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.None })
            {
                foreach (string line in item.Lines)
                {
                    float fit = size;
                    using (Font probe = new Font("Segoe UI", size, item.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        float measured = g.MeasureString(line, probe, PointF.Empty, StringFormat.GenericTypographic).Width;
                        float room = r.Width - 6 * s;
                        if (measured > room && measured > 0)
                            fit = Math.Max(size * 0.6f, size * room / measured);
                    }
                    using (Font font = new Font("Segoe UI", fit, item.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        float w = g.MeasureString(line, font, PointF.Empty, StringFormat.GenericTypographic).Width;
                        float x = item.Align == "center" ? r.X + (r.Width - w) / 2 : r.X + (item.Kind == "cell" ? 6 * s : 0);
                        g.DrawString(line, font, brush, x, y + (lineH - fit * 1.2f) / 2, StringFormat.GenericTypographic);
                    }
                    y += lineH;
                }
            }
        }

        // A PDF with one page per picture (each a JPEG at the page's size): small, exact and readable everywhere.
        public static void SavePdf(IList<Bitmap> pages, string path, double pointsPerPixel)
        {
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                List<long> offsets = new List<long>();
                Action<string> write = text =>
                {
                    byte[] b = Encoding.ASCII.GetBytes(text);
                    fs.Write(b, 0, b.Length);
                };
                write("%PDF-1.4\n");
                int count = pages.Count;
                // Objects: 1 catalog, 2 pages, then per page: page, content, image.
                Func<int, int> pageObj = i => 3 + i * 3;
                offsets.Add(fs.Position);
                write("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
                offsets.Add(fs.Position);
                write("2 0 obj << /Type /Pages /Count " + count + " /Kids [" + string.Join(" ", Enumerable.Range(0, count).Select(i => pageObj(i) + " 0 R")) + "] >> endobj\n");
                ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                for (int i = 0; i < count; i++)
                {
                    Bitmap bmp = pages[i];
                    byte[] data;
                    using (MemoryStream ms = new MemoryStream())
                    using (EncoderParameters ep = new EncoderParameters(1))
                    {
                        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                        bmp.Save(ms, jpeg, ep);
                        data = ms.ToArray();
                    }
                    double w = bmp.Width * pointsPerPixel, h = bmp.Height * pointsPerPixel;
                    string ws = w.ToString("0.##", CultureInfo.InvariantCulture), hs = h.ToString("0.##", CultureInfo.InvariantCulture);
                    int p = pageObj(i);
                    offsets.Add(fs.Position);
                    write(p + " 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 " + ws + " " + hs + "] /Resources << /XObject << /Im0 " + (p + 2) + " 0 R >> >> /Contents " + (p + 1) + " 0 R >> endobj\n");
                    string content = "q " + ws + " 0 0 " + hs + " 0 0 cm /Im0 Do Q";
                    offsets.Add(fs.Position);
                    write((p + 1) + " 0 obj << /Length " + content.Length + " >> stream\n" + content + "\nendstream endobj\n");
                    offsets.Add(fs.Position);
                    write((p + 2) + " 0 obj << /Type /XObject /Subtype /Image /Width " + bmp.Width + " /Height " + bmp.Height +
                          " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + data.Length + " >> stream\n");
                    fs.Write(data, 0, data.Length);
                    write("\nendstream endobj\n");
                }
                long xref = fs.Position;
                write("xref\n0 " + (offsets.Count + 1) + "\n0000000000 65535 f \n");
                foreach (long o in offsets)
                    write(o.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
                write("trailer << /Size " + (offsets.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
            }
        }
    }
}
