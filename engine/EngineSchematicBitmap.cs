using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;
using JoyPro.DeviceReference;

namespace JoyPro.Engine
{
    // JoyPro's device drawing (DeviceSchematic, docs/device-reference.md) as a bitmap for layouts the engine makes
    // (layout suggest): JoyPro's own picture of a model instead of a placeholder, with markers on the real parts.
    internal static class EngineSchematicBitmap
    {
        public static Bitmap Render(DeviceSchematic schematic, int width, bool dark)
        {
            float scale = (float)(width / schematic.Width);
            int height = Math.Max(1, (int)Math.Ceiling(schematic.Height * scale));
            Bitmap image = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(image))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(dark ? ParseColor("#0B1220") : ParseColor("#EEF2F7"));
                g.ScaleTransform(scale, scale);
                foreach (SchematicShape shape in schematic.Shapes)
                {
                    string[] colors = DeviceSchematic.RoleColors(shape.Role, dark);
                    using (Brush fill = colors[0] == "none" ? null : new SolidBrush(ParseColor(colors[0])))
                    using (Pen pen = colors[1] == "none" ? null : new Pen(ParseColor(colors[1]), shape.Kind == "line" ? 4f : 3f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        switch (shape.Kind)
                        {
                            case "rect":
                                using (GraphicsPath path = RoundedRect((float)shape.X, (float)shape.Y, (float)shape.W, (float)shape.H, (float)shape.R))
                                    Draw(g, path, fill, pen);
                                break;
                            case "ellipse":
                                using (GraphicsPath path = new GraphicsPath())
                                {
                                    path.AddEllipse((float)(shape.X - shape.W), (float)(shape.Y - shape.H), (float)(shape.W * 2), (float)(shape.H * 2));
                                    Draw(g, path, fill, pen);
                                }
                                break;
                            case "line":
                                if (pen != null)
                                    g.DrawLine(pen, (float)shape.X, (float)shape.Y, (float)shape.W, (float)shape.H);
                                break;
                            case "path":
                                using (GraphicsPath path = ParsePath(shape.Path))
                                    Draw(g, path, fill, pen);
                                break;
                            case "text":
                                using (Font font = new Font("Segoe UI", (float)shape.FontSize, FontStyle.Regular, GraphicsUnit.Pixel))
                                using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center })
                                    g.DrawString(shape.Text, font, fill ?? Brushes.Gray, (float)shape.X, (float)(shape.Y - shape.FontSize), format);
                                break;
                        }
                    }
                }
            }
            return image;
        }

        private static void Draw(Graphics g, GraphicsPath path, Brush fill, Pen pen)
        {
            if (fill != null)
                g.FillPath(fill, path);
            if (pen != null)
                g.DrawPath(pen, path);
        }

        private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
        {
            GraphicsPath path = new GraphicsPath();
            float d = Math.Min(Math.Min(w, h), r * 2);
            if (d <= 0.5f)
            {
                path.AddRectangle(new RectangleF(x, y, w, h));
                return path;
            }
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // The path data DeviceSchematic writes: absolute M, L, Q, C and Z with "x,y" points.
        private static GraphicsPath ParsePath(string data)
        {
            GraphicsPath path = new GraphicsPath();
            PointF current = PointF.Empty, start = PointF.Empty;
            bool open = false;
            foreach (Match command in Regex.Matches(data ?? string.Empty, @"([MLQCZ])([^MLQCZ]*)"))
            {
                List<PointF> points = new List<PointF>();
                foreach (Match p in Regex.Matches(command.Groups[2].Value, @"(-?[\d.]+),(-?[\d.]+)"))
                    points.Add(new PointF(float.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture), float.Parse(p.Groups[2].Value, CultureInfo.InvariantCulture)));
                switch (command.Groups[1].Value)
                {
                    case "M":
                        if (open)
                            path.StartFigure();
                        if (points.Count > 0)
                            current = start = points[0];
                        open = true;
                        break;
                    case "L":
                        foreach (PointF p in points)
                        {
                            path.AddLine(current, p);
                            current = p;
                        }
                        break;
                    case "Q":
                        for (int i = 0; i + 1 < points.Count; i += 2)
                        {
                            PointF c = points[i], end = points[i + 1];
                            PointF c1 = new PointF(current.X + 2f / 3f * (c.X - current.X), current.Y + 2f / 3f * (c.Y - current.Y));
                            PointF c2 = new PointF(end.X + 2f / 3f * (c.X - end.X), end.Y + 2f / 3f * (c.Y - end.Y));
                            path.AddBezier(current, c1, c2, end);
                            current = end;
                        }
                        break;
                    case "C":
                        for (int i = 0; i + 2 < points.Count; i += 3)
                        {
                            path.AddBezier(current, points[i], points[i + 1], points[i + 2]);
                            current = points[i + 2];
                        }
                        break;
                    case "Z":
                        path.CloseFigure();
                        current = start;
                        open = false;
                        break;
                }
            }
            return path;
        }

        // "#RRGGBB" or "#AARRGGBB".
        private static Color ParseColor(string hex)
        {
            string h = hex.TrimStart('#');
            uint value = uint.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return h.Length == 8 ? Color.FromArgb(unchecked((int)value)) : Color.FromArgb(255, Color.FromArgb(unchecked((int)value)));
        }
    }
}
