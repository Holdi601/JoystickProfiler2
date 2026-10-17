#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.DeviceReference
{
    // One drawing primitive of a schematic in canvas units (DeviceSchematic.Width x Height):
    //   rect    X, Y, W, H, corner radius R
    //   ellipse center X, Y, radii W, H
    //   path    Path (SVG path data with M L C Q Z, absolute)
    //   line    from X, Y to W, H (stroke only)
    //   text    Text at X, Y (baseline middle), FontSize
    // Role picks the colors (DeviceSchematic.RoleColors): shadow, body, bodyAlt, face, screen, control, mark, hot,
    // accent, line, label.
    public sealed class SchematicShape
    {
        public string Kind = "rect";
        public double X, Y, W, H, R;
        public string Path = string.Empty;
        public string Role = "body";
        public string Text = string.Empty;
        public double FontSize = 20;
    }

    // One DirectInput input of a marker, with its own point (a hat direction sits on that side of the hat).
    public sealed class SchematicInput
    {
        public string Input = string.Empty;
        public string Control = string.Empty;
        public string Direction = string.Empty;
        // In plain words: "TMS hat up", "A3 hat right".
        public string Name = string.Empty;
        // 0..1 of the picture.
        public double X, Y;
    }

    // One physical part of the device (a hat, a button, a switch, an axis) and where it is on the picture.
    public sealed class SchematicMarker
    {
        // The part's vocabulary id ("stick.hat.tms", "panel.toggle.3"); also the key of marker overrides.
        public string Id = string.Empty;
        // hat, ministick, trigger, button, release, switch, encoder, selector, rocker, osb, key, paddle, lever, slider,
        // knob, wheel, twist, gimbal, rudder, brake, detent.
        public string Type = "button";
        // The printed label of the part or its name in plain words ("A3 hat", "TMS hat").
        public string Name = string.Empty;
        // What the part is in the vocabulary, in plain words ("TMS hat"), when Name is a printed label.
        public string Role = string.Empty;
        // A few letters for small badges ("TMS", "T1"); empty = use Number.
        public string Short = string.Empty;
        public int Number;
        // Center and size (radius as a fraction of the picture width), 0..1 of the picture.
        public double X, Y, Size;
        // Where it sits: head, front, handle, base, face, extra ... (the picture's region).
        public string Region = string.Empty;
        public List<SchematicInput> Inputs = new List<SchematicInput>();

        public bool HasInputs { get { return Inputs.Any(i => i.Input.Length > 0); } }
    }

    public sealed class DeviceSchematic
    {
        public string ModelId = string.Empty;
        public string Name = string.Empty;
        public string Kind = string.Empty;
        public string Shape = string.Empty;
        public string Hand = string.Empty;
        public double Width = 1000;
        public double Height = 1000;
        public List<SchematicShape> Shapes = new List<SchematicShape>();
        public List<SchematicMarker> Markers = new List<SchematicMarker>();
        // The drawn device's bounds, 0..1 of the picture (left, top, right, bottom): what a photo's content is
        // aligned to.
        public double[] Body = { 0, 0, 1, 1 };

        public SchematicMarker MarkerOf(string input)
        {
            return Markers.FirstOrDefault(m => m.Inputs.Any(i => string.Equals(i.Input, input, StringComparison.OrdinalIgnoreCase)));
        }

        public SchematicInput InputOf(string input)
        {
            return Markers.SelectMany(m => m.Inputs).FirstOrDefault(i => string.Equals(i.Input, input, StringComparison.OrdinalIgnoreCase));
        }

        // What is wrong with the picture: a part reaching out of it, two parts on top of each other, an input on two
        // parts. Empty = fine.
        public List<string> Problems()
        {
            List<string> problems = new List<string>();
            if (Width <= 0 || Height <= 0)
                problems.Add("empty picture");
            foreach (SchematicMarker m in Markers)
            {
                double x = m.X * Width, y = m.Y * Height, r = m.Size * Width;
                if (x - r < -0.5 || y - r < -0.5 || x + r > Width + 0.5 || y + r > Height + 0.5)
                    problems.Add("'" + m.Name + "' reaches out of the picture");
            }
            for (int i = 0; i < Markers.Count; i++)
            {
                for (int j = i + 1; j < Markers.Count; j++)
                {
                    SchematicMarker a = Markers[i], b = Markers[j];
                    double dx = (a.X - b.X) * Width, dy = (a.Y - b.Y) * Height;
                    double limit = (a.Size + b.Size) * Width * 0.5;
                    if (Math.Sqrt(dx * dx + dy * dy) < limit)
                        problems.Add("'" + a.Name + "' and '" + b.Name + "' overlap");
                }
            }
            foreach (IGrouping<string, string> dup in Markers.SelectMany(m => m.Inputs.Where(i => i.Input.Length > 0).Select(i => i.Input).Distinct(StringComparer.OrdinalIgnoreCase)
                         .Select(i => i.ToUpperInvariant())).GroupBy(i => i).Where(g => g.Count() > 1))
                problems.Add(dup.Key + " is on " + dup.Count() + " parts");
            return problems;
        }

        // Fill and stroke of each role, dark theme (navy/blue) and light theme. "none" = no fill / no stroke.
        public static string[] RoleColors(string role, bool dark)
        {
            switch (role)
            {
                case "shadow": return dark ? new[] { "#40000000", "none" } : new[] { "#1A0F172A", "none" };
                case "body": return dark ? new[] { "#1E2B45", "#40598A" } : new[] { "#DCE5F2", "#8DA2C0" };
                case "bodyAlt": return dark ? new[] { "#152038", "#33476E" } : new[] { "#C9D5E6", "#8DA2C0" };
                case "face": return dark ? new[] { "#26365A", "#40598A" } : new[] { "#EAF0F8", "#A9B9D0" };
                case "screen": return dark ? new[] { "#0B1220", "#33476E" } : new[] { "#1E293B", "#64748B" };
                case "control": return dark ? new[] { "#2B4C8C", "#93C5FD" } : new[] { "#FFFFFF", "#2563EB" };
                case "mark": return dark ? new[] { "#93C5FD", "none" } : new[] { "#2563EB", "none" };
                case "hot": return dark ? new[] { "#9F3A44", "#FCA5A5" } : new[] { "#FEE2E2", "#DC2626" };
                case "accent": return dark ? new[] { "#3B82F6", "none" } : new[] { "#2563EB", "none" };
                case "line": return dark ? new[] { "none", "#40598A" } : new[] { "none", "#8DA2C0" };
                case "label": return dark ? new[] { "#93A4BF", "none" } : new[] { "#56657E", "none" };
                default: return dark ? new[] { "#1E2B45", "#40598A" } : new[] { "#DCE5F2", "#8DA2C0" };
            }
        }

        // The schematic as an SVG document (JoyPro's own drawing; numbered badges on the markers when badges is set).
        public string ToSvg(bool dark, bool badges)
        {
            StringBuilder sb = new StringBuilder();
            string w = F(Width), h = F(Height);
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"" + w + "\" height=\"" + h + "\" viewBox=\"0 0 " + w + " " + h + "\">");
            sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"" + (dark ? "#0B1220" : "#EEF2F7") + "\"/>");
            foreach (SchematicShape s in Shapes)
            {
                string[] c = RoleColors(s.Role, dark);
                string paint = " fill=\"" + Color(c[0]) + "\"" + Opacity("fill", c[0]) + " stroke=\"" + Color(c[1]) + "\" stroke-width=\"" + (s.Kind == "line" ? "4" : "3") + "\"";
                switch (s.Kind)
                {
                    case "rect":
                        sb.Append("<rect x=\"" + F(s.X) + "\" y=\"" + F(s.Y) + "\" width=\"" + F(s.W) + "\" height=\"" + F(s.H) + "\" rx=\"" + F(s.R) + "\"" + paint + "/>");
                        break;
                    case "ellipse":
                        sb.Append("<ellipse cx=\"" + F(s.X) + "\" cy=\"" + F(s.Y) + "\" rx=\"" + F(s.W) + "\" ry=\"" + F(s.H) + "\"" + paint + "/>");
                        break;
                    case "path":
                        sb.Append("<path d=\"" + s.Path + "\"" + paint + " stroke-linejoin=\"round\"/>");
                        break;
                    case "line":
                        sb.Append("<line x1=\"" + F(s.X) + "\" y1=\"" + F(s.Y) + "\" x2=\"" + F(s.W) + "\" y2=\"" + F(s.H) + "\" stroke=\"" + Color(c[1]) + "\" stroke-width=\"4\" stroke-linecap=\"round\"/>");
                        break;
                    case "text":
                        sb.Append("<text x=\"" + F(s.X) + "\" y=\"" + F(s.Y) + "\" font-family=\"Segoe UI, Arial, sans-serif\" font-size=\"" + F(s.FontSize) + "\" text-anchor=\"middle\" fill=\"" + Color(c[0]) + "\">" + Escape(s.Text) + "</text>");
                        break;
                }
            }
            if (badges)
            {
                foreach (SchematicMarker m in Markers)
                {
                    double x, y, r;
                    BadgePoint(m, out x, out y, out r);
                    sb.Append("<circle cx=\"" + F(x) + "\" cy=\"" + F(y) + "\" r=\"" + F(r) + "\" fill=\"#3B82F6\" stroke=\"" + (dark ? "#0B1220" : "#FFFFFF") + "\" stroke-width=\"3\"/>");
                    sb.Append("<text x=\"" + F(x) + "\" y=\"" + F(y + r * 0.38) + "\" font-family=\"Segoe UI, Arial, sans-serif\" font-weight=\"600\" font-size=\"" + F(r * 1.05) + "\" text-anchor=\"middle\" fill=\"#FFFFFF\">" + m.Number.ToString(CultureInfo.InvariantCulture) + "</text>");
                }
            }
            sb.Append("</svg>");
            return sb.ToString();
        }

        // Where the number badge of a marker goes, in canvas units: up and right of the part.
        public void BadgePoint(SchematicMarker m, out double x, out double y, out double r)
        {
            double size = m.Size * Width;
            r = Math.Max(13, Math.Min(18, Width / 70));
            x = m.X * Width + size * 0.8;
            y = m.Y * Height - size * 0.8;
            x = Math.Max(r, Math.Min(Width - r, x));
            y = Math.Max(r, Math.Min(Height - r, y));
        }

        private static string Color(string color)
        {
            if (color == "none")
                return "none";
            return color.Length == 9 ? "#" + color.Substring(3) : color;
        }

        private static string Opacity(string what, string color)
        {
            if (color.Length != 9)
                return string.Empty;
            int alpha = int.Parse(color.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return " " + what + "-opacity=\"" + (alpha / 255.0).ToString("0.###", CultureInfo.InvariantCulture) + "\"";
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        internal static string F(double value)
        {
            return Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    // Draws JoyPro's own schematic of a device model from its catalog entry: the outline of its family (fighter, space
    // or classic grip, desk stick, throttle, HOTAS, pedals, yoke, quadrant, collective, MFD, panel, base) and every
    // part of the device where it sits on such a device: named hats and buttons on their usual places (TMS, DMS, CMS,
    // trim, trigger, pinky, mic, speed brake ...), the remaining ones in rows on the head, the face or the base. The
    // markers are exactly where the parts are drawn, so the picture and the markers always agree.
    public static class SchematicBuilder
    {
        private static readonly Dictionary<string, double> Radius = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            { "hat", 34 }, { "ministick", 30 }, { "trigger", 30 }, { "button", 19 }, { "release", 22 }, { "switch", 21 },
            { "encoder", 24 }, { "selector", 26 }, { "rocker", 22 }, { "osb", 26 }, { "key", 26 }, { "paddle", 24 },
            { "lever", 32 }, { "slider", 24 }, { "knob", 24 }, { "wheel", 24 }, { "twist", 34 }, { "gimbal", 42 },
            { "rudder", 44 }, { "brake", 32 }, { "detent", 16 }
        };

        // The drawing family of a model: its "reference.shape", else from its kind and name.
        public static string ShapeOf(RefDevice model)
        {
            if (model == null)
                return "panel";
            if (!string.IsNullOrWhiteSpace(model.Shape))
                return model.Shape.Trim().ToLowerInvariant();
            string kind = (model.Kind ?? string.Empty).ToLowerInvariant();
            string text = ((model.Id ?? string.Empty) + " " + (model.Name ?? string.Empty)).ToLowerInvariant();
            switch (kind)
            {
                case "stick":
                    if (Regex.IsMatch(text, @"\bscg\b|space|kosmosima|alpha|constellation|delta|ursa-minor-space|gladiator nxt evo|cdt-aero|vfx|ace-?space"))
                        return "grip.space";
                    if (Regex.IsMatch(text, @"kg12|wwii|ww2|\bb8\b|mig|vpc-?b|sidestick|airbus|airline|sol-r|ma3x|farmstick"))
                        return "grip.classic";
                    if (Regex.IsMatch(text, @"extreme|3d pro|t\.?16000|t16000|t\.?flight|tflight|attack|cyborg|sidewinder|\bx-?(36|45|52|55|56|65)\b|x52|x56|x55|x65|x45|x36|hori|pxn|speedlink|trust|genius|fighterstick|combatstick|flightstick|freedom|g940|velocityone|precision|hotas one|hotas x|hotas 4|warthog-base") && !model.IsGrip)
                        return "grip.desk";
                    return "grip.fighter";
                case "hotas": return "hotas";
                case "throttle": return "throttle";
                case "collective": return "collective";
                case "pedals": return "pedals";
                case "yoke": return "yoke";
                case "quadrant": return "quadrant";
                case "mfd": return "mfd";
                case "base": return "base";
                default: return "panel";
            }
        }

        public static DeviceSchematic Build(RefDevice model, RefDevice baseModel)
        {
            if (model == null)
                throw new ArgumentNullException("model");
            List<Element> elements = Elements(model, baseModel);
            string shape = ShapeOf(model);
            string hand = (model.Hand ?? "none").ToLowerInvariant();
            Canvas canvas = new Canvas();
            switch (shape)
            {
                case "grip.fighter":
                case "grip.space":
                case "grip.classic":
                case "grip.desk":
                    Stick(canvas, elements, shape, hand == "left", 0, 0, model.IsGrip);
                    canvas.Width = 1000;
                    canvas.Height = 1300;
                    break;
                case "throttle":
                    Throttle(canvas, elements, hand == "right", 0, 0);
                    canvas.Width = 1300;
                    canvas.Height = 1000;
                    break;
                case "hotas":
                    List<Element> stickPart = elements.Where(e => e.Id.StartsWith("stick.", StringComparison.Ordinal)).ToList();
                    List<Element> throttlePart = elements.Except(stickPart).ToList();
                    Throttle(canvas, throttlePart, false, 0, 200);
                    Stick(canvas, stickPart, "grip.fighter", false, 1300, 0, false);
                    canvas.Width = 2300;
                    canvas.Height = 1300;
                    break;
                case "collective":
                    Collective(canvas, elements, hand == "right");
                    canvas.Width = 1300;
                    canvas.Height = 1000;
                    break;
                case "pedals":
                    Pedals(canvas, elements);
                    canvas.Width = 1200;
                    canvas.Height = 800;
                    break;
                case "yoke":
                    Yoke(canvas, elements);
                    canvas.Width = 1400;
                    canvas.Height = 1000;
                    break;
                case "quadrant":
                    Quadrant(canvas, elements);
                    canvas.Width = 1200;
                    canvas.Height = 900;
                    break;
                case "mfd":
                    Mfd(canvas, elements);
                    break;
                case "base":
                    GimbalBase(canvas, elements);
                    canvas.Width = 1000;
                    canvas.Height = 900;
                    break;
                default:
                    Panel(canvas, elements, 0, 0, true);
                    break;
            }

            // Parts no region took (or that did not fit): a strip "More inputs" on the right.
            List<Element> rest = elements.Where(e => !e.Placed).ToList();
            if (rest.Count > 0)
                Extra(canvas, rest);

            DeviceSchematic schematic = new DeviceSchematic
            {
                ModelId = model.Id,
                Name = model.Name,
                Kind = model.Kind,
                Shape = shape,
                Hand = hand,
                Width = canvas.Width,
                Height = canvas.Height,
                Shapes = canvas.Shapes
            };
            // Marker positions the catalog (or the user) set for this model win over the computed ones.
            foreach (Element element in elements)
            {
                double[] xy;
                if (model.Markers.TryGetValue(element.Id, out xy))
                {
                    element.X = xy[0] * canvas.Width;
                    element.Y = xy[1] * canvas.Height;
                }
            }
            int number = 0;
            foreach (Element element in elements.OrderBy(e => e.Order).ThenBy(e => e.SortKey, StringComparer.Ordinal))
            {
                canvas.Glyph(element);
                schematic.Markers.Add(ToMarker(element, ++number, canvas));
            }
            // The device's bounds: its outline and every part sticking out of it (a trigger in front of the grip).
            double[] body = canvas.BodyBounds();
            foreach (Element e in elements.Where(x => x.Region != "extra"))
            {
                body[0] = Math.Min(body[0], Clamp01((e.X - e.R) / canvas.Width));
                body[1] = Math.Min(body[1], Clamp01((e.Y - e.R) / canvas.Height));
                body[2] = Math.Max(body[2], Clamp01((e.X + e.R) / canvas.Width));
                body[3] = Math.Max(body[3], Clamp01((e.Y + e.R) / canvas.Height));
            }
            schematic.Body = body;
            return schematic;
        }

        // ---------------------------------------------------------------- parts

        internal sealed class Element
        {
            public string Id = string.Empty;
            public string Type = "button";
            public string Name = string.Empty;
            public string Role = string.Empty;
            public string SortKey = string.Empty;
            public List<RefControl> Controls = new List<RefControl>();
            public double X, Y, R;
            public bool Placed;
            public bool Mirror;
            public string Region = string.Empty;
            public int Order;
        }

        // The parts of a model (and of its base, for a grip): its controls grouped as physical parts, in vocabulary
        // order.
        private static List<Element> Elements(RefDevice model, RefDevice baseModel)
        {
            List<RefControl> controls = model.Axes.Concat(model.Controls).Select(c => c.Clone()).ToList();
            if (baseModel != null)
            {
                foreach (RefControl c in baseModel.Axes.Concat(baseModel.Controls))
                {
                    bool known = c.Input.Length > 0 ? controls.Any(x => string.Equals(x.Input, c.Input, StringComparison.OrdinalIgnoreCase))
                        : controls.Any(x => string.Equals(x.Control, c.Control, StringComparison.OrdinalIgnoreCase));
                    if (!known)
                        controls.Add(c.Clone());
                }
            }
            Dictionary<string, Element> byId = new Dictionary<string, Element>(StringComparer.OrdinalIgnoreCase);
            List<Element> list = new List<Element>();
            foreach (RefControl control in controls)
            {
                if (control.Control.Length == 0 && control.Input.Length == 0)
                    continue;
                string direction;
                string id = control.Control.Length > 0 ? DeviceVocabulary.ElementOf(control.Control, out direction) : "input." + control.Input.ToLowerInvariant();
                Element element;
                if (!byId.TryGetValue(id, out element))
                {
                    element = new Element { Id = id, Type = control.Control.Length > 0 ? DeviceVocabulary.ElementType(id) : "button", SortKey = DeviceVocabulary.NaturalKey(id) };
                    byId[id] = element;
                    list.Add(element);
                }
                element.Controls.Add(control);
            }
            foreach (Element element in list)
            {
                List<string> labels = element.Controls.Select(c => c.Label.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string role = DeviceVocabulary.ElementName(element.Id);
                element.Role = role;
                element.Name = labels.Count == 1 ? labels[0] : labels.Count > 1 ? CommonLabel(labels, role) : role;
                element.R = Radius.ContainsKey(element.Type) ? Radius[element.Type] : 20;
            }
            return list.OrderBy(e => e.SortKey, StringComparer.Ordinal).ToList();
        }

        private static string CommonLabel(List<string> labels, string fallback)
        {
            string first = labels[0];
            int length = 0;
            while (length < first.Length && labels.All(l => l.Length > length && char.ToLowerInvariant(l[length]) == char.ToLowerInvariant(first[length])))
                length++;
            string common = first.Substring(0, length).Trim(' ', '-', '(', ',', '/');
            return common.Length >= 3 ? common : fallback;
        }

        private static SchematicMarker ToMarker(Element element, int number, Canvas canvas)
        {
            SchematicMarker marker = new SchematicMarker
            {
                Id = element.Id,
                Type = element.Type,
                Name = element.Name,
                Role = string.Equals(element.Name, element.Role, StringComparison.OrdinalIgnoreCase) ? string.Empty : element.Role,
                Short = DeviceVocabulary.ShortName(element.Id, element.Name),
                Number = number,
                X = Clamp01(element.X / canvas.Width),
                Y = Clamp01(element.Y / canvas.Height),
                Size = element.R / canvas.Width,
                Region = element.Region
            };
            foreach (RefControl control in element.Controls)
            {
                string direction;
                DeviceVocabulary.ElementOf(control.Control, out direction);
                double dx, dy;
                Offset(element, direction, out dx, out dy);
                marker.Inputs.Add(new SchematicInput
                {
                    Input = control.Input,
                    Control = control.Control,
                    Direction = direction,
                    Name = DeviceVocabulary.Describe(control.Control, control.Label),
                    X = Clamp01((element.X + dx) / canvas.Width),
                    Y = Clamp01((element.Y + dy) / canvas.Height)
                });
            }
            return marker;
        }

        // The point of one direction of a part (relative to its center, canvas units).
        private static void Offset(Element e, string direction, out double dx, out double dy)
        {
            double r = e.R * 0.6;
            double d = r * 0.7071;
            dx = dy = 0;
            switch ((direction ?? string.Empty).ToLowerInvariant())
            {
                case "up": case "fwd": case "push": case "in": dy = -r; break;
                case "down": case "aft": case "pull": case "out": dy = r; break;
                case "left": case "ccw": dx = -r; break;
                case "right": case "cw": dx = r; break;
                case "upright": case "ur": dx = d; dy = -d; break;
                case "upleft": case "ul": dx = -d; dy = -d; break;
                case "downright": case "dr": dx = d; dy = d; break;
                case "downleft": case "dl": dx = -d; dy = d; break;
                case "stage2": dy = r * 0.8; break;
                case "stage1": dy = -r * 0.4; break;
                default:
                    if (direction != null && direction.StartsWith("pos", StringComparison.OrdinalIgnoreCase))
                    {
                        int pos;
                        if (int.TryParse(direction.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out pos))
                        {
                            double angle = (-135 + (pos - 1) * 45) * Math.PI / 180;
                            dx = Math.Cos(angle) * r;
                            dy = Math.Sin(angle) * r;
                        }
                    }
                    break;
            }
        }

        private static double Clamp01(double value)
        {
            return double.IsNaN(value) ? 0.5 : Math.Max(0, Math.Min(1, value));
        }

        // ---------------------------------------------------------------- families

        // A stick grip seen from the thumb side (front = left for a right hand; mirrored for a left hand), on its
        // gimbal and base. Local canvas 1000 x 1300 at (ox, oy).
        private static void Stick(Canvas c, List<Element> elements, string shape, bool mirror, double ox, double oy, bool isGrip)
        {
            Frame f = new Frame(ox, oy, 1000, mirror);
            bool baseParts = elements.Any(e => e.Id.Contains(".base.") || !e.Id.StartsWith("stick.", StringComparison.Ordinal) || e.Id == "stick.axis.slider");
            bool bigBase = shape == "grip.desk" || baseParts || !isGrip;
            double hx0, hy0, hx1, hy1;
            c.Add(f.Ellipse(500, 1255, bigBase ? 400 : 230, 26, "shadow"));
            if (bigBase)
            {
                c.Add(f.Rect(140, 1060, 720, 185, 44, "bodyAlt"), true);
                c.Add(f.Rect(170, 1046, 660, 44, 22, "body"), true);
            }
            else
            {
                c.Add(f.Rect(320, 1080, 360, 150, 36, "bodyAlt"), true);
            }
            c.Add(f.Ellipse(500, 1045, 125, 34, "screen"));
            if (shape == "grip.space")
            {
                hx0 = 290; hy0 = 90; hx1 = 770; hy1 = 520;
                c.Add(f.Path(new PathData().M(445, 1040).C(435, 900, 415, 800, 410, 700).C(405, 630, 330, 600, 300, 540).L(290, 200)
                    .C(290, 125, 340, 90, 420, 90).L(680, 90).C(750, 90, 775, 140, 770, 210).L(752, 520).C(722, 600, 662, 640, 652, 700)
                    .C(642, 820, 627, 930, 597, 1040).Z(), "body"), true);
                c.Add(f.Rect(335, 130, 400, 370, 64, "face"));
            }
            else if (shape == "grip.classic")
            {
                hx0 = 380; hy0 = 180; hx1 = 655; hy1 = 470;
                c.Add(f.Path(new PathData().M(442, 1040).C(432, 880, 422, 760, 417, 640).C(412, 560, 382, 520, 382, 470)
                    .C(382, 330, 402, 220, 472, 190).C(522, 170, 582, 175, 612, 200).C(662, 240, 662, 330, 652, 470)
                    .C(647, 560, 632, 620, 632, 700).C(627, 820, 617, 930, 592, 1040).Z(), "body"), true);
                c.Add(f.Rect(418, 220, 210, 240, 80, "face"));
            }
            else
            {
                hx0 = 340; hy0 = 130; hx1 = 715; hy1 = 470;
                c.Add(f.Path(new PathData().M(432, 1040).C(422, 900, 402, 800, 397, 700).C(392, 620, 362, 560, 350, 520).L(340, 330)
                    .C(335, 200, 380, 130, 470, 130).L(610, 130).C(690, 135, 720, 190, 715, 260).L(700, 470).C(690, 560, 650, 620, 640, 700)
                    .C(630, 820, 620, 930, 592, 1040).Z(), "body"), true);
                c.Add(f.Rect(382, 172, 300, 290, 56, "face"));
            }
            double hw = hx1 - hx0, hh = hy1 - hy0;
            double front = hx0;
            Slots slots = new Slots(c, f);
            slots.Add("stick.hat.trim", hx0 + hw * 0.50, hy0 + hh * 0.13, "head");
            slots.Add("stick.weapon_release", hx0 + hw * 0.17, hy0 + hh * 0.14, "head");
            slots.Add("stick.trigger.flip", front - 6, hy0 + hh * 0.58, "front");
            slots.Add("stick.hat.tms", hx0 + hw * 0.74, hy0 + hh * 0.34, "head");
            slots.Add("stick.hat.dms", hx0 + hw * 0.42, hy0 + hh * 0.42, "head");
            slots.Add("stick.hat.cms", hx0 + hw * 0.70, hy0 + hh * 0.72, "head");
            slots.Add("stick.ministick", hx0 + hw * 0.40, hy0 + hh * 0.78, "head");
            slots.Add("stick.trigger", front - 40, hy1 + 30, "front");
            slots.Add("stick.button.index_lower", front - 4, hy1 + 150, "front");
            slots.Add("stick.pinky.lever", front + 40, 830, "front");
            slots.Add("stick.pinky.button", front + 70, 905, "front");
            slots.Add("stick.axis.twist", 515, 760, "handle");
            slots.Add("stick.gimbal", 500, 1045, "base");
            slots.Add("stick.axis.brake_lever", hx1 - 30, hy1 + 150, "handle");
            slots.Add("stick.axis.slider", bigBase ? 790 : 650, bigBase ? 1150 : 900, bigBase ? "base" : "handle");
            slots.Assign(elements);

            Pools pools = new Pools(c, f);
            pools.Add("head", hx0 + hw * 0.12, hy0 + hh * 0.22, hx1 - hw * 0.06, hy1 - hh * 0.05,
                e => e.Id.StartsWith("stick.", StringComparison.Ordinal) && !e.Id.Contains(".base.") && !e.Id.Contains(".index") && e.Type != "gimbal");
            pools.Add("front", front - 40, hy1 + 70, front + 30, hy1 + 300, e => e.Id.Contains(".index") || e.Type == "trigger" || e.Type == "paddle");
            pools.Add("handle", 450, 560, 600, 720, e => e.Id.StartsWith("stick.", StringComparison.Ordinal) && !e.Id.Contains(".base."));
            if (bigBase)
                pools.Add("base", 200, 1100, 760, 1225, e => true);
            pools.Layout(elements);
        }

        // A throttle seen from the thumb side (front = right for a left hand; mirrored for a right hand): handle head,
        // lever arm(s) and the base with its switches. Local canvas 1300 x 1000 at (ox, oy).
        private static void Throttle(Canvas c, List<Element> elements, bool mirror, double ox, double oy)
        {
            Frame f = new Frame(ox, oy, 1300, mirror);
            bool dual = elements.Any(e => e.Id == "throttle.axis.left") && elements.Any(e => e.Id == "throttle.axis.right");
            c.Add(f.Ellipse(650, 975, 570, 22, "shadow"));
            c.Add(f.Rect(90, 600, 1120, 360, 40, "bodyAlt"), true);
            c.Add(f.Rect(120, 584, 1060, 42, 18, "body"), true);
            c.Add(f.Rect(300, 592, 700, 18, 9, "screen"));
            c.Add(f.Rect(126, 640, 1048, 300, 26, "face"));
            if (dual)
            {
                c.Add(f.Path(new PathData().M(500, 600).L(580, 600).L(770, 470).L(680, 450).Z(), "bodyAlt"));
                c.Add(f.Path(new PathData().M(600, 470).C(580, 380, 600, 250, 660, 200).C(720, 150, 860, 140, 960, 160).L(980, 170).L(960, 470).Z(), "bodyAlt"));
            }
            c.Add(f.Path(new PathData().M(560, 600).L(640, 600).L(830, 470).L(740, 450).Z(), "body"), true);
            c.Add(f.Path(new PathData().M(640, 470).C(620, 380, 640, 250, 700, 200).C(760, 150, 900, 140, 1000, 160).C(1080, 175, 1110, 230, 1100, 300)
                .L(1080, 440).C(1070, 490, 1020, 510, 960, 505).L(720, 500).C(680, 500, 650, 490, 640, 470).Z(), "body"), true);
            c.Add(f.Rect(690, 200, 380, 270, 50, "face"));
            double hx0 = 650, hy0 = 160, hw = 450, hh = 340;
            Slots slots = new Slots(c, f);
            slots.Add("throttle.hat.mic", hx0 + hw * 0.62, hy0 + hh * 0.22, "head");
            slots.Add("throttle.speedbrake", hx0 + hw * 0.90, hy0 + hh * 0.42, "head");
            slots.Add("throttle.boat", hx0 + hw * 0.36, hy0 + hh * 0.24, "head");
            slots.Add("throttle.china", hx0 + hw * 0.12, hy0 + hh * 0.48, "head");
            slots.Add("throttle.coolie", hx0 + hw * 0.70, hy0 + hh * 0.58, "head");
            slots.Add("throttle.dogfight", hx0 + hw * 0.88, hy0 + hh * 0.13, "head");
            slots.Add("throttle.cage", hx0 + hw * 0.18, hy0 + hh * 0.82, "head");
            slots.Add("throttle.pinky", hx0 + hw * 0.36, hy0 + hh * 1.0, "head");
            slots.Add("throttle.pinky.lever", hx0 + hw * 0.36, hy0 + hh * 1.0, "head");
            slots.Add("throttle.pinky.button", hx0 + hw * 0.52, hy0 + hh * 1.0, "head");
            slots.Add("throttle.ministick", hx0 + hw * 0.46, hy0 + hh * 0.64, "head");
            slots.Add("throttle.axis.main", 690, 540, "lever");
            slots.Add("throttle.axis.left", 600, 545, "lever");
            slots.Add("throttle.axis.right", 705, 540, "lever");
            slots.Add("throttle.axis.friction", 200, 605, "base");
            slots.Add("throttle.detent.cutoff", 320, 600, "base");
            slots.Add("throttle.detent.idle", 380, 600, "base");
            slots.Add("throttle.detent.afterburner", 960, 600, "base");
            slots.Assign(elements);

            Pools pools = new Pools(c, f);
            pools.Add("head", hx0 + hw * 0.1, hy0 + hh * 0.16, hx0 + hw * 0.95, hy0 + hh * 0.9,
                e => e.Id.StartsWith("throttle.", StringComparison.Ordinal) && !e.Id.Contains(".base.") && e.Type != "lever" && e.Type != "detent");
            pools.Add("lever", 480, 520, 640, 590, e => e.Type == "lever" || e.Type == "detent");
            pools.Add("base", 150, 660, 1150, 925, e => true);
            pools.Layout(elements);
        }

        private static void Collective(Canvas c, List<Element> elements, bool mirror)
        {
            Frame f = new Frame(0, 0, 1300, mirror);
            c.Add(f.Ellipse(500, 975, 460, 20, "shadow"));
            c.Add(f.Rect(60, 760, 460, 200, 40, "bodyAlt"), true);
            c.Add(f.Ellipse(300, 760, 80, 34, "screen"));
            c.Add(f.Path(new PathData().M(266, 762).L(334, 762).L(870, 430).L(822, 372).Z(), "body"), true);
            c.Add(f.Rect(780, 130, 420, 310, 64, "body"), true);
            c.Add(f.Rect(818, 168, 344, 234, 44, "face"));
            Slots slots = new Slots(c, f);
            slots.Add("collective.axis.main", 560, 590, "lever");
            slots.Add("collective.axis.throttle_twist", 830, 420, "lever");
            slots.Assign(elements);
            Pools pools = new Pools(c, f);
            pools.Add("head", 830, 180, 1150, 390, e => e.Id.StartsWith("collective.", StringComparison.Ordinal) && e.Type != "lever");
            pools.Add("base", 100, 810, 480, 930, e => true);
            pools.Layout(elements);
        }

        private static void Pedals(Canvas c, List<Element> elements)
        {
            Frame f = new Frame(0, 0, 1200, false);
            c.Add(f.Ellipse(600, 770, 520, 18, "shadow"));
            c.Add(f.Rect(110, 560, 980, 190, 44, "bodyAlt"), true);
            c.Add(f.Rect(330, 596, 540, 34, 17, "screen"));
            c.Add(f.Rect(200, 110, 300, 480, 64, "body"), true);
            c.Add(f.Rect(700, 110, 300, 480, 64, "body"), true);
            c.Add(f.Rect(232, 142, 236, 140, 44, "face"));
            c.Add(f.Rect(732, 142, 236, 140, 44, "face"));
            for (int i = 0; i < 4; i++)
            {
                c.Add(f.Line(250, 330 + i * 60, 450, 330 + i * 60, "line"));
                c.Add(f.Line(750, 330 + i * 60, 950, 330 + i * 60, "line"));
            }
            Slots slots = new Slots(c, f);
            slots.Add("pedals.axis.rudder", 600, 613, "base");
            slots.Add("pedals.axis.brake_left", 350, 212, "pedal");
            slots.Add("pedals.axis.brake_right", 850, 212, "pedal");
            slots.Assign(elements);
            Pools pools = new Pools(c, f);
            pools.Add("base", 160, 660, 1040, 735, e => true);
            pools.Layout(elements);
        }

        private static void Yoke(Canvas c, List<Element> elements)
        {
            Frame f = new Frame(0, 0, 1400, false);
            c.Add(f.Ellipse(700, 975, 470, 18, "shadow"));
            c.Add(f.Rect(250, 770, 900, 200, 40, "bodyAlt"), true);
            c.Add(f.Rect(640, 520, 120, 260, 20, "bodyAlt"), true);
            c.Add(f.Path(new PathData().M(260, 220).C(260, 160, 330, 130, 380, 150).L(420, 170).L(980, 170).L(1020, 150).C(1070, 130, 1140, 160, 1140, 220)
                .L(1140, 600).C(1140, 660, 1080, 690, 1030, 660).L(980, 620).C(930, 560, 880, 540, 800, 540).L(600, 540).C(520, 540, 470, 560, 420, 620)
                .L(370, 660).C(320, 690, 260, 660, 260, 600).Z(), "body"), true);
            c.Add(f.Rect(470, 220, 460, 270, 40, "face"));
            Slots slots = new Slots(c, f);
            slots.Add("yoke.column", 700, 650, "column");
            slots.Add("yoke.hat.1", 330, 250, "left horn");
            slots.Add("yoke.hat.2", 1070, 250, "right horn");
            slots.Assign(elements);
            Pools pools = new Pools(c, f);
            Func<Element, bool> onYoke = e => e.Id.StartsWith("yoke.", StringComparison.Ordinal) && !e.Id.Contains(".switch.") && e.Type != "gimbal";
            pools.Add("left horn", 285, 300, 395, 600, onYoke, 5);
            pools.Add("right horn", 1005, 300, 1115, 600, onYoke, 5);
            pools.Add("hub", 500, 250, 900, 470, onYoke);
            pools.Add("base", 300, 810, 1100, 940, e => true);
            pools.Layout(elements);
        }

        private static void Quadrant(Canvas c, List<Element> elements)
        {
            Frame f = new Frame(0, 0, 1200, false);
            List<Element> levers = elements.Where(e => e.Type == "lever" && e.Id.Contains(".axis.")).OrderBy(e => e.SortKey, StringComparer.Ordinal).ToList();
            int n = Math.Max(1, levers.Count);
            c.Add(f.Ellipse(600, 875, 520, 18, "shadow"));
            c.Add(f.Rect(100, 380, 1000, 470, 40, "bodyAlt"), true);
            c.Add(f.Rect(130, 360, 940, 42, 16, "body"), true);
            c.Add(f.Rect(140, 450, 920, 380, 24, "face"));
            Slots slots = new Slots(c, f);
            for (int i = 0; i < n; i++)
            {
                double x = n == 1 ? 600 : 230 + i * (740.0 / (n - 1));
                c.Add(f.Rect(x - 14, 366, 28, 26, 8, "screen"));
                c.Add(f.Rect(x - 13, 170, 26, 200, 10, "body"), true);
                c.Add(f.Rect(x - 55, 105, 110, 72, 30, "face"), true);
                if (i < levers.Count)
                    slots.Add(levers[i].Id, x, 141, "levers");
                slots.Add("quadrant.detent." + (i + 1).ToString(CultureInfo.InvariantCulture), x, 412, "levers");
            }
            slots.Assign(elements);
            Pools pools = new Pools(c, f);
            pools.Add("face", 170, 470, 1030, 810, e => true);
            pools.Layout(elements);
        }

        private static void GimbalBase(Canvas c, List<Element> elements)
        {
            Frame f = new Frame(0, 0, 1000, false);
            c.Add(f.Ellipse(500, 870, 400, 20, "shadow"));
            c.Add(f.Rect(130, 450, 740, 400, 50, "bodyAlt"), true);
            c.Add(f.Rect(160, 430, 680, 50, 25, "body"), true);
            c.Add(f.Rect(470, 250, 60, 200, 22, "body"), true);
            c.Add(f.Ellipse(500, 250, 100, 30, "face"), true);
            c.Add(f.Ellipse(500, 450, 170, 46, "screen"));
            Slots slots = new Slots(c, f);
            slots.Add("stick.gimbal", 500, 450, "gimbal");
            slots.Add("stick.axis.twist", 500, 340, "gimbal");
            slots.Assign(elements);
            Pools pools = new Pools(c, f);
            pools.Add("base", 190, 530, 810, 820, e => true);
            pools.Layout(elements);
        }

        private static void Mfd(Canvas c, List<Element> elements)
        {
            List<string> sides = elements.Select(e => SideOf(e.Id)).Distinct().OrderBy(s => s == "left" ? 0 : s == "center" ? 1 : 2).ToList();
            if (sides.Count == 0)
                sides.Add("left");
            double x0 = 0;
            foreach (string side in sides)
            {
                List<Element> mine = elements.Where(e => SideOf(e.Id) == side).ToList();
                Frame f = new Frame(x0, 0, 1000, false);
                c.Add(f.Rect(40, 40, 920, 920, 56, "body"), true);
                c.Add(f.Rect(190, 190, 620, 620, 18, "screen"));
                Slots slots = new Slots(c, f);
                for (int i = 1; i <= 5; i++)
                {
                    double p = 310 + (i - 1) * 95;
                    string pre = "mfd." + side + ".osb.";
                    slots.Add(pre + "t" + i, p, 112, "bezel");
                    slots.Add(pre + "b" + i, p, 888, "bezel");
                    slots.Add(pre + "l" + i, 112, p, "bezel");
                    slots.Add(pre + "r" + i, 888, p, "bezel");
                }
                string rocker = "mfd." + side + ".rocker.";
                slots.Add(rocker + "top_left", 120, 120, "bezel");
                slots.Add(rocker + "top_right", 880, 120, "bezel");
                slots.Add(rocker + "bottom_left", 120, 880, "bezel");
                slots.Add(rocker + "bottom_right", 880, 880, "bezel");
                slots.Assign(mine);
                // Other rockers (GAIN, SYM, CON, BRT ...) take the free corners.
                double[][] corners = { new double[] { 120, 120 }, new double[] { 880, 120 }, new double[] { 120, 880 }, new double[] { 880, 880 } };
                foreach (Element e in mine.Where(m => !m.Placed && m.Type == "rocker"))
                {
                    foreach (double[] corner in corners)
                    {
                        double gx = f.X(corner[0]), gy = f.Y(corner[1]);
                        if (mine.Any(m => m.Placed && Math.Abs(m.X - gx) < 1 && Math.Abs(m.Y - gy) < 1))
                            continue;
                        e.X = gx;
                        e.Y = gy;
                        e.Placed = true;
                        e.Region = "bezel";
                        break;
                    }
                }
                Pools pools = new Pools(c, f);
                pools.Add("screen", 240, 240, 760, 760, e => true);
                pools.Layout(mine);
                x0 += 1000;
            }
            c.Width = x0;
            c.Height = 1000;
        }

        private static string SideOf(string id)
        {
            Match m = Regex.Match(id ?? string.Empty, @"^mfd\.(left|right|center)\.");
            return m.Success ? m.Groups[1].Value : "left";
        }

        // A front panel: the parts in rows (levers, switches, selectors and knobs, then buttons), each with its label
        // below. The canvas grows with the number of parts.
        private static void Panel(Canvas c, List<Element> elements, double ox, double oy, bool sizeCanvas)
        {
            List<Element> order = elements.OrderBy(e => PanelOrder(e.Type)).ThenBy(e => e.SortKey, StringComparer.Ordinal).ToList();
            int n = Math.Max(1, order.Count);
            int cols = n <= 4 ? n : Math.Min(12, (int)Math.Ceiling(Math.Sqrt(n * 1.8)));
            int rows = (int)Math.Ceiling(n / (double)cols);
            const double cellW = 170, cellH = 190;
            double width = Math.Max(cols, 3) * cellW + 120;
            double height = rows * cellH + 140;
            Frame f = new Frame(ox, oy, width, false);
            c.Add(f.Ellipse(width / 2, height - 22, width / 2 - 40, 14, "shadow"));
            c.Add(f.Rect(30, 30, width - 60, height - 70, 36, "body"), true);
            c.Add(f.Rect(58, 58, width - 116, height - 126, 22, "face"));
            double gridX = (width - cols * cellW) / 2;
            for (int i = 0; i < order.Count; i++)
            {
                Element e = order[i];
                int row = i / cols, col = i % cols;
                double cx = gridX + col * cellW + cellW / 2;
                double cy = 70 + row * cellH + cellH / 2 - 18;
                e.X = f.X(cx);
                e.Y = f.Y(cy);
                e.R = Math.Min(e.R * 1.15, 40);
                e.Placed = true;
                e.Region = "face";
                e.Order = i;
                c.Add(f.Text(cx, cy + 74, Shorten(e.Name, 15), 21, "label"));
            }
            if (sizeCanvas)
            {
                c.Width = width;
                c.Height = height;
            }
        }

        private static int PanelOrder(string type)
        {
            switch (type)
            {
                case "lever": return 0;
                case "switch": return 1;
                case "selector": return 2;
                case "encoder": return 3;
                case "knob": case "slider": case "wheel": return 4;
                case "rocker": return 5;
                case "hat": case "ministick": return 6;
                case "key": return 7;
                case "osb": return 8;
                default: return 9;
            }
        }

        private static string Shorten(string text, int max)
        {
            string t = (text ?? string.Empty).Trim();
            return t.Length <= max ? t : t.Substring(0, max - 1).TrimEnd() + (char)0x2026;
        }

        // The parts no region took: a "More inputs" strip right of the drawing.
        private static void Extra(Canvas c, List<Element> rest)
        {
            int cols = rest.Count > 24 ? 3 : rest.Count > 8 ? 2 : 1;
            int rows = (int)Math.Ceiling(rest.Count / (double)cols);
            const double cell = 92;
            double x0 = c.Width + 20;
            double width = cols * cell + 60;
            double height = Math.Max(rows * cell + 110, 200);
            Frame f = new Frame(x0, 0, width, false);
            c.Add(f.Rect(0, 30, width, height - 40, 30, "bodyAlt"), true);
            c.Add(f.Text(width / 2, 78, "More inputs", 24, "label"));
            for (int i = 0; i < rest.Count; i++)
            {
                Element e = rest[i];
                e.X = f.X(30 + (i % cols) * cell + cell / 2);
                e.Y = f.Y(100 + (i / cols) * cell + cell / 2);
                e.R = Math.Min(e.R, 30);
                e.Placed = true;
                e.Region = "extra";
            }
            c.Width = x0 + width + 20;
            c.Height = Math.Max(c.Height, height + 20);
        }

        // ---------------------------------------------------------------- drawing helpers

        internal sealed class Frame
        {
            private readonly double ox, oy, width;
            private readonly bool mirror;

            public Frame(double ox, double oy, double width, bool mirror)
            {
                this.ox = ox;
                this.oy = oy;
                this.width = width;
                this.mirror = mirror;
            }

            public bool Mirrored { get { return mirror; } }

            public double X(double x) { return ox + (mirror ? width - x : x); }
            public double Y(double y) { return oy + y; }

            public SchematicShape Rect(double x, double y, double w, double h, double r, string role)
            {
                return new SchematicShape { Kind = "rect", X = mirror ? X(x + w) : X(x), Y = Y(y), W = w, H = h, R = r, Role = role };
            }

            public SchematicShape Ellipse(double cx, double cy, double rx, double ry, string role)
            {
                return new SchematicShape { Kind = "ellipse", X = X(cx), Y = Y(cy), W = rx, H = ry, Role = role };
            }

            public SchematicShape Line(double x1, double y1, double x2, double y2, string role)
            {
                return new SchematicShape { Kind = "line", X = X(x1), Y = Y(y1), W = X(x2), H = Y(y2), Role = role };
            }

            public SchematicShape Text(double x, double y, string text, double size, string role)
            {
                return new SchematicShape { Kind = "text", X = X(x), Y = Y(y), Text = text, FontSize = size, Role = role };
            }

            public SchematicShape Path(PathData data, string role)
            {
                return new SchematicShape { Kind = "path", Path = data.Build(this), Role = role };
            }
        }

        internal sealed class PathData
        {
            private readonly List<KeyValuePair<char, double[]>> parts = new List<KeyValuePair<char, double[]>>();

            public PathData M(double x, double y) { parts.Add(new KeyValuePair<char, double[]>('M', new[] { x, y })); return this; }
            public PathData L(double x, double y) { parts.Add(new KeyValuePair<char, double[]>('L', new[] { x, y })); return this; }
            public PathData Q(double x1, double y1, double x, double y) { parts.Add(new KeyValuePair<char, double[]>('Q', new[] { x1, y1, x, y })); return this; }
            public PathData C(double x1, double y1, double x2, double y2, double x, double y) { parts.Add(new KeyValuePair<char, double[]>('C', new[] { x1, y1, x2, y2, x, y })); return this; }
            public PathData Z() { parts.Add(new KeyValuePair<char, double[]>('Z', new double[0])); return this; }

            public string Build(Frame f)
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<char, double[]> part in parts)
                {
                    if (sb.Length > 0)
                        sb.Append(' ');
                    sb.Append(part.Key);
                    for (int i = 0; i + 1 < part.Value.Length; i += 2)
                        sb.Append(' ').Append(DeviceSchematic.F(f.X(part.Value[i]))).Append(',').Append(DeviceSchematic.F(f.Y(part.Value[i + 1])));
                }
                return sb.ToString();
            }
        }

        internal sealed class Canvas
        {
            public double Width = 1000, Height = 1000;
            public List<SchematicShape> Shapes = new List<SchematicShape>();
            private readonly List<SchematicShape> bodies = new List<SchematicShape>();

            public void Add(SchematicShape shape)
            {
                Shapes.Add(shape);
            }

            // bodyPart: the shape is part of the device's outline (for the bounds a photo is aligned to).
            public void Add(SchematicShape shape, bool bodyPart)
            {
                Shapes.Add(shape);
                if (bodyPart)
                    bodies.Add(shape);
            }

            public double[] BodyBounds()
            {
                double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
                foreach (SchematicShape s in bodies)
                {
                    double x0, y0, x1, y1;
                    Bounds(s, out x0, out y0, out x1, out y1);
                    l = Math.Min(l, x0); t = Math.Min(t, y0); r = Math.Max(r, x1); b = Math.Max(b, y1);
                }
                if (l == double.MaxValue)
                    return new double[] { 0, 0, 1, 1 };
                return new[] { Clamp01(l / Width), Clamp01(t / Height), Clamp01(r / Width), Clamp01(b / Height) };
            }

            private static void Bounds(SchematicShape s, out double x0, out double y0, out double x1, out double y1)
            {
                switch (s.Kind)
                {
                    case "ellipse":
                        x0 = s.X - s.W; y0 = s.Y - s.H; x1 = s.X + s.W; y1 = s.Y + s.H;
                        return;
                    case "path":
                        List<double> xs = new List<double>(), ys = new List<double>();
                        foreach (Match m in Regex.Matches(s.Path, @"(-?[\d.]+),(-?[\d.]+)"))
                        {
                            xs.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                            ys.Add(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
                        }
                        x0 = xs.Count > 0 ? xs.Min() : 0; y0 = ys.Count > 0 ? ys.Min() : 0;
                        x1 = xs.Count > 0 ? xs.Max() : 0; y1 = ys.Count > 0 ? ys.Max() : 0;
                        return;
                    default:
                        x0 = s.X; y0 = s.Y; x1 = s.X + s.W; y1 = s.Y + s.H;
                        return;
                }
            }

            // The drawing of a part at its marker.
            public void Glyph(Element e)
            {
                double x = e.X, y = e.Y, r = e.R;
                string role = e.Type == "release" ? "hot" : "control";
                switch (e.Type)
                {
                    case "hat":
                        Add(new SchematicShape { Kind = "ellipse", X = x, Y = y, W = r, H = r, Role = role });
                        Add(Triangle(x, y - r * 0.62, 0, -1, r * 0.26));
                        Add(Triangle(x, y + r * 0.62, 0, 1, r * 0.26));
                        Add(Triangle(x - r * 0.62, y, -1, 0, r * 0.26));
                        Add(Triangle(x + r * 0.62, y, 1, 0, r * 0.26));
                        Add(new SchematicShape { Kind = "ellipse", X = x, Y = y, W = r * 0.2, H = r * 0.2, Role = "mark" });
                        break;
                    case "ministick":
                        Add(new SchematicShape { Kind = "ellipse", X = x, Y = y, W = r, H = r, Role = role });
                        Add(new SchematicShape { Kind = "ellipse", X = x, Y = y, W = r * 0.55, H = r * 0.55, Role = "face" });
                        Add(new SchematicShape { Kind = "ellipse", X = x, Y = y, W = r * 0.2, H = r * 0.2, Role = "mark" });
                        break;
                    case "trigger":
                    {
                        double s = e.Mirror ? -1 : 1;
                        Add(new SchematicShape
                        {
                            Kind = "path",
                            Role = role,
                            Path = "M " + P(x + s * r * 0.5, y - r * 1.3) + " Q " + P(x - s * r * 1.2, y - r * 0.9) + " " + P(x - s * r * 0.9, y + r * 1.2) +
                                   " L " + P(x - s * r * 0.2, y + r * 1.0) + " Q " + P(x - s * r * 0.2, y - r * 0.2) + " " + P(x + s * r * 0.6, y - r * 0.5) + " Z"
                        });
                        break;
                    }
                    case "switch":
                        Add(new SchematicShape { Kind = "rect", X = x - r * 0.55, Y = y - r, W = r * 1.1, H = r * 2, R = r * 0.5, Role = role });
                        Add(new SchematicShape { Kind = "ellipse", X = x, Y = y - r * 0.45, W = r * 0.36, H = r * 0.36, Role = "mark" });
                        break;
                    case "encoder":
                    case "knob":
                        Add(new SchematicShape { Kind = "ellipse", X = x, Y = y, W = r, H = r, Role = role });
                        Add(new SchematicShape { Kind = "ellipse", X = x, Y = y, W = r * 0.62, H = r * 0.62, Role = "face" });
                        Add(new SchematicShape { Kind = "rect", X = x - r * 0.09, Y = y - r * 0.6, W = r * 0.18, H = r * 0.42, R = r * 0.09, Role = "mark" });
                        break;
                    case "selector":
                        Add(new SchematicShape { Kind = "ellipse", X = x, Y = y, W = r, H = r, Role = role });
                        Add(new SchematicShape { Kind = "rect", X = x - r * 0.16, Y = y - r * 0.85, W = r * 0.32, H = r * 1.1, R = r * 0.16, Role = "mark" });
                        break;
                    case "rocker":
                        Add(new SchematicShape { Kind = "rect", X = x - r * 0.6, Y = y - r, W = r * 1.2, H = r * 2, R = r * 0.35, Role = role });
                        Add(new SchematicShape { Kind = "line", X = x - r * 0.45, Y = y, W = x + r * 0.45, H = y, Role = "line" });
                        break;
                    case "osb":
                    case "key":
                        Add(new SchematicShape { Kind = "rect", X = x - r * 0.8, Y = y - r * 0.62, W = r * 1.6, H = r * 1.24, R = r * 0.25, Role = role });
                        break;
                    case "paddle":
                        Add(new SchematicShape { Kind = "rect", X = x - r * 0.38, Y = y - r, W = r * 0.76, H = r * 2, R = r * 0.38, Role = role });
                        break;
                    case "slider":
                    case "wheel":
                        Add(new SchematicShape { Kind = "rect", X = x - r * 0.32, Y = y - r * 1.15, W = r * 0.64, H = r * 2.3, R = r * 0.32, Role = "screen" });
                        Add(new SchematicShape { Kind = "rect", X = x - r * 0.6, Y = y - r * 0.32, W = r * 1.2, H = r * 0.64, R = r * 0.2, Role = role });
                        break;
                    case "detent":
                        Add(new SchematicShape { Kind = "rect", X = x - r * 0.4, Y = y - r * 0.9, W = r * 0.8, H = r * 1.8, R = r * 0.2, Role = "mark" });
                        break;
                    case "lever":
                        if (e.Id.Contains(".lever.gear") || e.Id.Contains(".lever.flaps"))
                        {
                            Add(new SchematicShape { Kind = "rect", X = x - r * 0.18, Y = y - r * 1.1, W = r * 0.36, H = r * 1.9, R = r * 0.18, Role = "screen" });
                            Add(new SchematicShape { Kind = e.Id.Contains("gear") ? "ellipse" : "rect", X = e.Id.Contains("gear") ? x : x - r * 0.55, Y = e.Id.Contains("gear") ? y - r * 0.7 : y - r * 1.0, W = e.Id.Contains("gear") ? r * 0.5 : r * 1.1, H = e.Id.Contains("gear") ? r * 0.5 : r * 0.5, R = r * 0.15, Role = role });
                        }
                        else
                        {
                            Arrow(x, y, 0, r, role);
                        }
                        break;
                    case "twist":
                        Add(new SchematicShape { Kind = "path", Role = "accent", Path = "M " + P(x - r, y - r * 0.15) + " Q " + P(x, y + r * 0.75) + " " + P(x + r, y - r * 0.15) + " L " + P(x + r * 1.25, y - r * 0.45) + " L " + P(x + r * 1.15, y + r * 0.2) + " L " + P(x + r * 0.55, y + r * 0.05) + " L " + P(x + r * 0.82, y - r * 0.05) + " Q " + P(x, y + r * 0.45) + " " + P(x - r * 0.8, y - r * 0.02) + " Z" });
                        Add(new SchematicShape { Kind = "ellipse", X = x, Y = y - r * 0.25, W = r * 0.22, H = r * 0.22, Role = "mark" });
                        break;
                    case "gimbal":
                        Arrow(x, y, 0, r, "accent");
                        Arrow(x, y, 1, r, "accent");
                        Add(new SchematicShape { Kind = "ellipse", X = x, Y = y, W = r * 0.28, H = r * 0.28, Role = "control" });
                        break;
                    case "rudder":
                        Arrow(x, y, 1, r * 1.3, "accent");
                        Add(new SchematicShape { Kind = "ellipse", X = x, Y = y, W = r * 0.3, H = r * 0.3, Role = "control" });
                        break;
                    case "brake":
                        Add(new SchematicShape { Kind = "path", Role = "accent", Path = "M " + P(x - r * 0.25, y - r) + " L " + P(x + r * 0.25, y - r) + " L " + P(x + r * 0.25, y + r * 0.1) + " L " + P(x + r * 0.6, y + r * 0.1) + " L " + P(x, y + r) + " L " + P(x - r * 0.6, y + r * 0.1) + " L " + P(x - r * 0.25, y + r * 0.1) + " Z" });
                        break;
                    default:
                        Add(new SchematicShape { Kind = "ellipse", X = x, Y = y, W = r, H = r, Role = role });
                        Add(new SchematicShape { Kind = "ellipse", X = x, Y = y, W = r * 0.45, H = r * 0.45, Role = "face" });
                        break;
                }
            }

            // A double arrow through (x, y): axis 0 = vertical, 1 = horizontal.
            private void Arrow(double x, double y, int axis, double r, string role)
            {
                double l = r, w = r * 0.16, h = r * 0.42;
                string path;
                if (axis == 0)
                    path = "M " + P(x, y - l) + " L " + P(x + h, y - l + h) + " L " + P(x + w, y - l + h) + " L " + P(x + w, y + l - h) + " L " + P(x + h, y + l - h) +
                           " L " + P(x, y + l) + " L " + P(x - h, y + l - h) + " L " + P(x - w, y + l - h) + " L " + P(x - w, y - l + h) + " L " + P(x - h, y - l + h) + " Z";
                else
                    path = "M " + P(x - l, y) + " L " + P(x - l + h, y - h) + " L " + P(x - l + h, y - w) + " L " + P(x + l - h, y - w) + " L " + P(x + l - h, y - h) +
                           " L " + P(x + l, y) + " L " + P(x + l - h, y + h) + " L " + P(x + l - h, y + w) + " L " + P(x - l + h, y + w) + " L " + P(x - l + h, y + h) + " Z";
                Add(new SchematicShape { Kind = "path", Path = path, Role = role });
            }

            private static SchematicShape Triangle(double x, double y, int dx, int dy, double s)
            {
                string path;
                if (dx == 0)
                    path = "M " + P(x, y + dy * s * 0.8) + " L " + P(x + s, y - dy * s * 0.5) + " L " + P(x - s, y - dy * s * 0.5) + " Z";
                else
                    path = "M " + P(x + dx * s * 0.8, y) + " L " + P(x - dx * s * 0.5, y + s) + " L " + P(x - dx * s * 0.5, y - s) + " Z";
                return new SchematicShape { Kind = "path", Path = path, Role = "mark" };
            }

            private static string P(double x, double y)
            {
                return DeviceSchematic.F(x) + "," + DeviceSchematic.F(y);
            }
        }

        // Fixed places of named parts (local coordinates of a family).
        private sealed class Slots
        {
            private readonly Frame frame;
            private readonly List<KeyValuePair<string, double[]>> slots = new List<KeyValuePair<string, double[]>>();
            private readonly List<string> regions = new List<string>();

            public Slots(Canvas canvas, Frame frame)
            {
                this.frame = frame;
            }

            public void Add(string id, double x, double y, string region)
            {
                slots.Add(new KeyValuePair<string, double[]>(id, new[] { x, y }));
                regions.Add(region);
            }

            public void Assign(List<Element> elements)
            {
                for (int i = 0; i < slots.Count; i++)
                {
                    Element e = elements.FirstOrDefault(x => !x.Placed && string.Equals(x.Id, slots[i].Key, StringComparison.OrdinalIgnoreCase));
                    if (e == null)
                        continue;
                    e.X = frame.X(slots[i].Value[0]);
                    e.Y = frame.Y(slots[i].Value[1]);
                    e.Placed = true;
                    e.Mirror = frame.Mirrored;
                    e.Region = regions[i];
                    e.Order = i;
                }
            }
        }

        // Regions where the remaining parts go in rows, in order; a part goes to the first region that takes it
        // (and still has room when the region has a capacity).
        private sealed class Pools
        {
            private readonly Frame frame;
            private readonly List<Pool> pools = new List<Pool>();

            private sealed class Pool
            {
                public string Name;
                public double X0, Y0, X1, Y1;
                public Func<Element, bool> Accepts;
                public int Capacity;
                public List<Element> Items = new List<Element>();
            }

            public Pools(Canvas canvas, Frame frame)
            {
                this.frame = frame;
            }

            public void Add(string name, double x0, double y0, double x1, double y1, Func<Element, bool> accepts)
            {
                Add(name, x0, y0, x1, y1, accepts, int.MaxValue);
            }

            public void Add(string name, double x0, double y0, double x1, double y1, Func<Element, bool> accepts, int capacity)
            {
                pools.Add(new Pool { Name = name, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Accepts = accepts, Capacity = capacity });
            }

            public void Layout(List<Element> elements)
            {
                List<Element> fixedOnes = elements.Where(e => e.Placed).ToList();
                foreach (Element e in elements.Where(x => !x.Placed))
                {
                    Pool pool = pools.FirstOrDefault(p => p.Items.Count < p.Capacity && p.Accepts(e));
                    if (pool != null)
                        pool.Items.Add(e);
                }
                int order = 100;
                foreach (Pool pool in pools)
                {
                    if (pool.Items.Count == 0)
                        continue;
                    List<Element> items = pool.Items.OrderBy(e => PoolOrder(e.Type)).ThenBy(e => e.SortKey, StringComparer.Ordinal).ToList();
                    double w = pool.X1 - pool.X0, h = pool.Y1 - pool.Y0;
                    double maxR = items.Max(e => e.R);
                    List<double[]> cells = null;
                    double radiusScale = 1;
                    for (int extra = 0; extra < 40; extra++)
                    {
                        int n = items.Count + extra;
                        int cols = Math.Max(1, (int)Math.Round(Math.Sqrt(n * w / Math.Max(1, h))));
                        int rows = (int)Math.Ceiling(n / (double)cols);
                        double cellW = w / cols, cellH = h / rows;
                        radiusScale = Math.Min(1, Math.Min(cellW, cellH) * 0.42 / maxR);
                        List<double[]> free = new List<double[]>();
                        for (int r = 0; r < rows; r++)
                        {
                            for (int c = 0; c < cols; c++)
                            {
                                double lx = pool.X0 + cellW * (c + 0.5), ly = pool.Y0 + cellH * (r + 0.5);
                                double gx = frame.X(lx), gy = frame.Y(ly);
                                if (fixedOnes.Any(o => Distance(o.X, o.Y, gx, gy) < o.R + maxR * radiusScale + 4))
                                    continue;
                                free.Add(new[] { gx, gy });
                            }
                        }
                        if (free.Count >= items.Count)
                        {
                            cells = free;
                            break;
                        }
                    }
                    if (cells == null || radiusScale * maxR < 9)
                        continue; // left for the "More inputs" strip
                    // Reading order: rows top to bottom, then from the front (left before mirroring).
                    for (int i = 0; i < items.Count; i++)
                    {
                        Element e = items[i];
                        e.X = cells[i][0];
                        e.Y = cells[i][1];
                        e.R = Math.Max(9, e.R * radiusScale);
                        e.Placed = true;
                        e.Mirror = frame.Mirrored;
                        e.Region = pool.Name;
                        e.Order = order++;
                        fixedOnes.Add(e);
                    }
                }
            }

            private static int PoolOrder(string type)
            {
                switch (type)
                {
                    case "hat": return 0;
                    case "ministick": return 1;
                    case "switch": return 2;
                    case "selector": return 3;
                    case "encoder": return 4;
                    case "knob": case "slider": case "wheel": return 5;
                    case "rocker": return 6;
                    default: return 7;
                }
            }

            private static double Distance(double x0, double y0, double x1, double y1)
            {
                double dx = x0 - x1, dy = y0 - y1;
                return Math.Sqrt(dx * dx + dy * dy);
            }
        }
    }
}
