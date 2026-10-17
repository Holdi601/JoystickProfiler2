using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using JoyPro.DeviceReference;

namespace JoyPro.Engine
{
    // JoyPro's drawing of a device (DeviceLineArt) as a bitmap for the callout sheets and cheat sheets the engine
    // renders, in the user's drawing style (DrawingStyle: lines or cel-shaded, line and page colours): the same colours
    // as the GUI's canvas and device cards.
    internal static class EngineLineArtBitmap
    {
        public static Bitmap Render(DeviceLineArt art, int width, DrawingColors colors, bool page = true)
        {
            width = Math.Max(1, width);
            float scale = (float)(width / art.Width);
            int height = Math.Max(1, (int)Math.Ceiling(art.Height * scale));
            Bitmap image = new Bitmap(width, height);
            bool cel = colors.Cel && art.HasFills;
            using (Graphics g = Graphics.FromImage(image))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(page ? ToColor(colors.Paper) : Color.Transparent);
                g.ScaleTransform(scale, scale);
                if (cel)
                {
                    foreach (LineArtFill fill in art.Fills)
                    {
                        using (SolidBrush brush = new SolidBrush(ToColor(fill.Color)))
                        using (GraphicsPath shape = ToPath(DeviceLineArt.ParsePath(fill.D)))
                        {
                            shape.FillMode = fill.EvenOdd ? FillMode.Alternate : FillMode.Winding;
                            g.FillPath(brush, shape);
                        }
                    }
                }
                foreach (LineArtPath path in art.Paths)
                {
                    using (Pen pen = new Pen(ToColor(colors.StrokeFor(art.HasFills, path.IsOutline)), (float)art.StrokeWidth(path, cel)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    using (GraphicsPath shape = ToPath(DeviceLineArt.ParsePath(path.D)))
                        g.DrawPath(pen, shape);
                }
            }
            return image;
        }

        public static Color ToColor(string hex)
        {
            int[] c = DrawingStyle.Rgb(hex);
            return Color.FromArgb(c[0], c[1], c[2]);
        }

        // M L Q C Z into a GraphicsPath (a quadratic curve becomes the equal cubic one).
        internal static GraphicsPath ToPath(List<LineArtCommand> commands)
        {
            GraphicsPath path = new GraphicsPath();
            PointF pos = PointF.Empty;
            bool open = false;
            foreach (LineArtCommand c in commands)
            {
                switch (c.Op)
                {
                    case 'M':
                        path.StartFigure();
                        pos = new PointF((float)c.P[0], (float)c.P[1]);
                        open = true;
                        break;
                    case 'L':
                    {
                        PointF to = new PointF((float)c.P[0], (float)c.P[1]);
                        path.AddLine(pos, to);
                        pos = to;
                        break;
                    }
                    case 'Q':
                    {
                        PointF q = new PointF((float)c.P[0], (float)c.P[1]);
                        PointF to = new PointF((float)c.P[2], (float)c.P[3]);
                        PointF c1 = new PointF(pos.X + 2f / 3f * (q.X - pos.X), pos.Y + 2f / 3f * (q.Y - pos.Y));
                        PointF c2 = new PointF(to.X + 2f / 3f * (q.X - to.X), to.Y + 2f / 3f * (q.Y - to.Y));
                        path.AddBezier(pos, c1, c2, to);
                        pos = to;
                        break;
                    }
                    case 'C':
                    {
                        PointF to = new PointF((float)c.P[4], (float)c.P[5]);
                        path.AddBezier(pos, new PointF((float)c.P[0], (float)c.P[1]), new PointF((float)c.P[2], (float)c.P[3]), to);
                        pos = to;
                        break;
                    }
                    case 'Z':
                        if (open)
                            path.CloseFigure();
                        open = false;
                        break;
                }
            }
            return path;
        }
    }
}
