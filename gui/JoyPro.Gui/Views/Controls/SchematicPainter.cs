using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Views.Controls;

// Draws JoyPro's drawing of a device (DeviceSchematic shapes in its own units = picture pixels) in the theme's colours:
// the picture canvas of the Layout page and the small device pictures of the Start page and Settings > Devices.
// Small pictures (thumbnail) use the same shapes in a brighter palette with outlines of at least ~1.3 px and without
// the drawing's text, so the device's shape reads at a glance on the dark and the light theme.
public sealed class SchematicPainter
{
    private readonly Dictionary<SchematicShape, Geometry?> geometries = [];
    private DeviceSchematic? source;
    private Rect? contentBounds;

    // The schematic scaled to target (target's width = the schematic's width).
    public void Draw(DrawingContext context, DeviceSchematic schematic, Rect target, bool dark, bool thumbnail = false)
    {
        Use(schematic);
        double scale = target.Width / schematic.Width;
        // Outlines thick enough to be seen when the drawing is small (in schematic units).
        double minStroke = thumbnail && scale > 0 ? 1.3 / scale : 0;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(target.X, target.Y)))
        {
            foreach (SchematicShape shape in schematic.Shapes)
            {
                if (thumbnail && (shape.Kind == "text" || shape.Role is "label" or "shadow"))
                    continue;
                string[] colors = thumbnail ? ThumbnailColors(shape.Role, dark) : DeviceSchematic.RoleColors(shape.Role, dark);
                IBrush? fill = colors[0] == "none" ? null : new SolidColorBrush(Color.Parse(colors[0]));
                double width = Math.Max(shape.Kind == "line" ? 4 : 3, shape.Kind == "line" ? minStroke * 1.25 : minStroke);
                IPen? pen = colors[1] == "none" ? null : new Pen(new SolidColorBrush(Color.Parse(colors[1])), width, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
                switch (shape.Kind)
                {
                    case "rect":
                        context.DrawRectangle(fill, pen, new Rect(shape.X, shape.Y, shape.W, shape.H), shape.R, shape.R);
                        break;
                    case "ellipse":
                        context.DrawEllipse(fill, pen, new Point(shape.X, shape.Y), shape.W, shape.H);
                        break;
                    case "line":
                        if (pen != null)
                            context.DrawLine(pen, new Point(shape.X, shape.Y), new Point(shape.W, shape.H));
                        break;
                    case "path":
                        if (GeometryOf(shape) is { } geometry)
                            context.DrawGeometry(fill, pen, geometry);
                        break;
                    case "text":
                        FormattedText text = new(shape.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, shape.FontSize, fill ?? Brushes.Gray);
                        context.DrawText(text, new Point(shape.X - text.Width / 2, shape.Y - text.Height * 0.8));
                        break;
                }
            }
        }
    }

    // The bright palette of small pictures: a light body with clear outlines on dark, a mid-grey body with dark outlines on
    // light; the controls in the accent colour as on the Layout page.
    public static string[] ThumbnailColors(string role, bool dark)
    {
        switch (role)
        {
            case "body": return dark ? ["#8EA3C6", "#E2E8F0"] : ["#B4C2D8", "#334155"];
            case "bodyAlt": return dark ? ["#6E85AD", "#CBD5E1"] : ["#97A9C4", "#334155"];
            case "face": return dark ? ["#B4C4DE", "#F1F5F9"] : ["#D9E2EF", "#334155"];
            case "screen": return dark ? ["#1E293B", "#CBD5E1"] : ["#1E293B", "#334155"];
            case "control": return dark ? ["#3B82F6", "#EFF6FF"] : ["#FFFFFF", "#1D4ED8"];
            case "mark": return dark ? ["#EFF6FF", "none"] : ["#1D4ED8", "none"];
            case "hot": return dark ? ["#EF4444", "#FEE2E2"] : ["#FECACA", "#B91C1C"];
            case "accent": return dark ? ["#60A5FA", "none"] : ["#2563EB", "none"];
            case "line": return dark ? ["none", "#CBD5E1"] : ["none", "#334155"];
            default: return dark ? ["#8EA3C6", "#E2E8F0"] : ["#B4C2D8", "#334155"];
        }
    }

    // Where the drawn shapes are (schematic units): the device itself, without its shadow and text labels.
    public Rect ContentBounds(DeviceSchematic schematic)
    {
        Use(schematic);
        if (contentBounds is { } known)
            return known;
        Rect? bounds = null;
        foreach (SchematicShape shape in schematic.Shapes)
        {
            if (shape.Kind == "text" || shape.Role is "label" or "shadow")
                continue;
            Rect? r = shape.Kind switch
            {
                "rect" => new Rect(shape.X, shape.Y, Math.Max(0, shape.W), Math.Max(0, shape.H)),
                "ellipse" => new Rect(shape.X - shape.W, shape.Y - shape.H, 2 * Math.Max(0, shape.W), 2 * Math.Max(0, shape.H)),
                "line" => new Rect(new Point(shape.X, shape.Y), new Point(shape.W, shape.H)).Normalize(),
                "path" => GeometryOf(shape)?.Bounds,
                _ => null
            };
            if (r is { } rect && rect.Width + rect.Height > 0)
                bounds = bounds is { } b ? b.Union(rect) : rect;
        }
        Rect result = bounds is { } found && found.Width > 0 && found.Height > 0
            ? found.Intersect(new Rect(0, 0, schematic.Width, schematic.Height)) is { Width: > 0, Height: > 0 } inside ? inside : found
            : new Rect(0, 0, schematic.Width, schematic.Height);
        contentBounds = result;
        return result;
    }

    // The drawn device (ContentBounds plus a small margin) fitted into bounds, centred: where the whole schematic has to
    // be drawn (its width = the returned rect's width) so the device fills a small picture.
    public Rect FitContent(DeviceSchematic schematic, Rect bounds, double margin = 0.04)
    {
        Rect content = ContentBounds(schematic);
        double pad = Math.Max(content.Width, content.Height) * margin;
        content = content.Inflate(pad);
        if (content.Width <= 0 || content.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return new Rect(bounds.X, bounds.Y, 0, 0);
        double scale = Math.Min(bounds.Width / content.Width, bounds.Height / content.Height);
        double x = bounds.X + (bounds.Width - content.Width * scale) / 2 - content.X * scale;
        double y = bounds.Y + (bounds.Height - content.Height * scale) / 2 - content.Y * scale;
        return new Rect(x, y, schematic.Width * scale, schematic.Height * scale);
    }

    private void Use(DeviceSchematic schematic)
    {
        if (ReferenceEquals(source, schematic))
            return;
        geometries.Clear();
        contentBounds = null;
        source = schematic;
    }

    private Geometry? GeometryOf(SchematicShape shape)
    {
        if (geometries.TryGetValue(shape, out Geometry? geometry))
            return geometry;
        try
        {
            geometry = StreamGeometry.Parse(shape.Path);
        }
        catch (Exception)
        {
            geometry = null;
        }
        geometries[shape] = geometry;
        return geometry;
    }
}
