#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JoyPro.DeviceReference
{
    // One device of a cockpit variant as JoyPro draws it: the drawing (DeviceSchematic: the drawing's own shapes plus
    // the parts JoyPro draws on it), the callout sheet (a table per control with a line to its part, CalloutLayouter) and
    // the text of every row. The GUI shows it with the Layout page's canvas (CalloutSheetCanvas), the engine draws it with
    // the cheat sheet renderer (engine cockpits render).
    public sealed class CockpitSheet
    {
        public string Device = string.Empty;
        public string Title = string.Empty;
        public CockpitDrawing Drawing;
        public DeviceSchematic Schematic;
        public CalloutSheet Sheet;
        // Row input -> what the row says ("1st detent: Laser fire").
        public Dictionary<string, string> Texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Row input -> (control, function).
        public Dictionary<string, Tuple<CockpitControl, CockpitFunction>> Rows = new Dictionary<string, Tuple<CockpitControl, CockpitFunction>>(StringComparer.OrdinalIgnoreCase);
        // Part id -> [x, y] (0..1 of the drawing) of the placed controls.
        public Dictionary<string, double[]> Known = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        public List<CockpitControl> Controls = new List<CockpitControl>();
        public int Unplaced;

        public string TextOf(string input, bool modifier)
        {
            string text;
            return !modifier && Texts.TryGetValue(input ?? string.Empty, out text) ? text : string.Empty;
        }
    }

    public static class CockpitSchematic
    {
        // The sheets of a variant: one per device with controls, stick side first.
        public static List<CockpitSheet> SheetsOf(CockpitCatalog catalog, CockpitVariant variant)
        {
            List<CockpitSheet> sheets = new List<CockpitSheet>();
            if (catalog == null || variant == null)
                return sheets;
            int number = 0;
            foreach (string device in CockpitCatalog.DevicesOf(variant))
            {
                List<CockpitControl> controls = variant.Controls.Where(c => string.Equals(c.Device, device, StringComparison.OrdinalIgnoreCase) && c.Functions.Count > 0).ToList();
                if (controls.Count == 0)
                    continue;
                CockpitDrawing drawing = catalog.DrawingFor(variant, device);
                if (drawing == null)
                    continue;
                CockpitSheet sheet = Build(drawing, controls, device, number);
                number += controls.Count;
                sheets.Add(sheet);
            }
            return sheets;
        }

        // A drawing with the controls of one device on it. firstNumber: the callout numbers continue across devices.
        public static CockpitSheet Build(CockpitDrawing drawing, List<CockpitControl> controls, string device, int firstNumber)
        {
            CockpitSheet result = new CockpitSheet
            {
                Device = device ?? string.Empty,
                Title = CockpitCatalog.DeviceTitle(device),
                Drawing = drawing,
                Controls = controls
            };
            Dictionary<CockpitControl, CockpitDrawingPart> placed = Place(drawing, controls);
            // A generic drawing shows only the parts that carry a control; a real grip shows all of its controls.
            DeviceSchematic s = Drawing(drawing, drawing.Generic ? new HashSet<CockpitDrawingPart>(placed.Values) : null);
            s.Kind = device ?? drawing.Kind;
            // The picture is cut to the drawn device (plus a margin), so the tables sit close to it.
            double[] offset = Trim(s);
            int n = firstNumber;
            foreach (CockpitControl c in controls)
            {
                n++;
                CockpitDrawingPart part;
                placed.TryGetValue(c, out part);
                SchematicMarker m = new SchematicMarker
                {
                    Id = c.Id,
                    Type = MarkerType(c.Type),
                    Name = n.ToString(CultureInfo.InvariantCulture) + "  " + c.Name,
                    Role = c.Name,
                    Number = n,
                    Region = c.Where
                };
                if (part != null)
                {
                    m.X = (part.X - offset[0]) / s.Width;
                    m.Y = (part.Y - offset[1]) / s.Height;
                    m.Size = part.R / s.Width;
                    if (!result.Known.ContainsKey(c.Id))
                        result.Known[c.Id] = new[] { m.X, m.Y };
                }
                else
                {
                    result.Unplaced++;
                }
                // Up and forward (down and aft) share an arrow: a control with both says the direction in words.
                HashSet<string> dirs = new HashSet<string>(c.Functions.Select(f => (f.Dir ?? string.Empty).ToLowerInvariant()));
                bool spell = (dirs.Contains("up") && dirs.Contains("fwd")) || (dirs.Contains("down") && dirs.Contains("aft"));
                foreach (CockpitFunction f in c.Functions)
                {
                    string input = InputOf(c, f);
                    if (result.Rows.ContainsKey(input))
                        continue;
                    m.Inputs.Add(new SchematicInput { Input = input, Control = c.Id, Direction = RowDirection(f.Dir), Name = c.Name + " " + DirectionWords(f.Dir), X = m.X, Y = m.Y });
                    result.Rows[input] = Tuple.Create(c, f);
                    string d = (f.Dir ?? string.Empty).ToLowerInvariant();
                    result.Texts[input] = spell && (d == "up" || d == "down" || d == "fwd" || d == "aft")
                        ? char.ToUpperInvariant(DirectionWords(d)[0]) + DirectionWords(d).Substring(1) + ": " + f.Text
                        : RowText(f);
                }
                s.Markers.Add(m);
            }
            result.Schematic = s;
            List<CalloutTable> tables = CalloutLayouter.BuildTables(s, result.Known);
            foreach (CalloutTable t in tables)
                t.ShowModifier = false;
            CalloutSheet style = new CalloutSheet();
            // Larger rows than a device sheet: the tables are the reference, the drawing only shows where the control is.
            style.RowHeight = Math.Max(20, Math.Round(Math.Max(drawing.Width, drawing.Height) * 0.034));
            style.MainWidth = Math.Round(style.RowHeight * 11);
            style.ModWidth = 0;
            result.Sheet = CalloutLayouter.Arrange(tables, s.Width, s.Height, style);
            return result;
        }

        private static readonly System.Text.RegularExpressions.Regex PathPoint =
            new System.Text.RegularExpressions.Regex(@"(-?[\d.]+),(-?[\d.]+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        // Cuts the picture to its shapes plus a margin and moves the shapes there; returns the (x, y) taken off.
        public static double[] Trim(DeviceSchematic s)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            Action<double, double> add = (x, y) =>
            {
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
            };
            foreach (SchematicShape shape in s.Shapes)
            {
                switch (shape.Kind)
                {
                    case "rect": add(shape.X, shape.Y); add(shape.X + shape.W, shape.Y + shape.H); break;
                    case "ellipse": add(shape.X - shape.W, shape.Y - shape.H); add(shape.X + shape.W, shape.Y + shape.H); break;
                    case "line": add(shape.X, shape.Y); add(shape.W, shape.H); break;
                    case "text":
                        double half = shape.FontSize * 0.3 * (shape.Text ?? string.Empty).Length;
                        add(shape.X - half, shape.Y - shape.FontSize); add(shape.X + half, shape.Y + shape.FontSize * 0.3);
                        break;
                    case "path":
                        foreach (System.Text.RegularExpressions.Match p in PathPoint.Matches(shape.Path ?? string.Empty))
                            add(double.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(p.Groups[2].Value, CultureInfo.InvariantCulture));
                        break;
                }
            }
            if (x0 > x1 || y0 > y1)
                return new[] { 0.0, 0.0 };
            double margin = Math.Round(Math.Max(s.Width, s.Height) * 0.03);
            x0 = Math.Max(0, Math.Floor(x0 - margin));
            y0 = Math.Max(0, Math.Floor(y0 - margin));
            x1 = Math.Min(s.Width, Math.Ceiling(x1 + margin));
            y1 = Math.Min(s.Height, Math.Ceiling(y1 + margin));
            if (x0 <= 0 && y0 <= 0 && x1 >= s.Width && y1 >= s.Height)
                return new[] { 0.0, 0.0 };
            double dx = x0, dy = y0;
            foreach (SchematicShape shape in s.Shapes)
            {
                switch (shape.Kind)
                {
                    case "line": shape.X -= dx; shape.Y -= dy; shape.W -= dx; shape.H -= dy; break;
                    case "path":
                        shape.Path = PathPoint.Replace(shape.Path ?? string.Empty, m =>
                            (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - dx).ToString("0.#", CultureInfo.InvariantCulture) + "," +
                            (double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - dy).ToString("0.#", CultureInfo.InvariantCulture));
                        break;
                    default: shape.X -= dx; shape.Y -= dy; break;
                }
            }
            s.Width = x1 - x0;
            s.Height = y1 - y0;
            return new[] { dx, dy };
        }

        // Where each control is drawn: its part (Part, else its id) on the drawing. On a generic drawing a control whose
        // part the drawing has not gets the next free part of a fitting kind (a hat on a hat, a lever on a lever ...), so
        // any aircraft's controls show on the generic stick or throttle in their listed order.
        public static Dictionary<CockpitControl, CockpitDrawingPart> Place(CockpitDrawing drawing, List<CockpitControl> controls)
        {
            Dictionary<CockpitControl, CockpitDrawingPart> placed = new Dictionary<CockpitControl, CockpitDrawingPart>();
            HashSet<CockpitDrawingPart> used = new HashSet<CockpitDrawingPart>();
            foreach (CockpitControl c in controls)
            {
                CockpitDrawingPart part = drawing.PartOf(c.PartId);
                if (part != null && !used.Contains(part))
                {
                    placed[c] = part;
                    used.Add(part);
                }
            }
            if (!drawing.Generic)
                return placed;
            foreach (CockpitControl c in controls)
            {
                if (placed.ContainsKey(c))
                    continue;
                foreach (string type in FittingParts(c.Type))
                {
                    CockpitDrawingPart free = drawing.Parts.FirstOrDefault(p => !used.Contains(p) && string.Equals(p.Type, type, StringComparison.OrdinalIgnoreCase));
                    if (free == null)
                        continue;
                    placed[c] = free;
                    used.Add(free);
                    break;
                }
            }
            return placed;
        }

        private static string[] FittingParts(string controlType)
        {
            switch ((controlType ?? string.Empty).ToLowerInvariant())
            {
                case "hat4": case "hat8": return new[] { "hat4", "hat8", "ministick" };
                case "hat2": case "rocker": return new[] { "hat2", "rocker", "hat4", "switch3", "switch2" };
                case "trigger": return new[] { "trigger", "button" };
                case "lever": case "paddle": return new[] { "lever", "paddle", "button" };
                case "switch2": case "switch3": case "selector": return new[] { "switch3", "switch2", "hat2", "rocker", "button" };
                case "slider": return new[] { "slider", "wheel", "knob" };
                case "wheel": case "knob": case "encoder": return new[] { "wheel", "knob", "encoder", "slider" };
                case "ministick": return new[] { "ministick", "hat4", "hat8" };
                case "axis": return new[] { "axis", "slider" };
                case "gimbal": return new[] { "gimbal" };
                case "twist": return new[] { "twist" };
                case "guard": return new[] { "guard", "release", "button" };
                case "detent": return new[] { "detent", "button" };
                default: return new[] { "button", "release" };
            }
        }

        // The control vocabulary id of one direction of a control (docs/devices.md): "stick.hat.tms.up",
        // "stick.axis.pitch", "throttle.axis.ministick.x". What Auto Assign and the setup wizard place functions on.
        public static string Vocabulary(CockpitControl c, CockpitFunction f)
        {
            string id = c.Id ?? string.Empty;
            string dir = (f.Dir ?? string.Empty).ToLowerInvariant();
            int dot = id.IndexOf('.');
            string device = dot > 0 ? id.Substring(0, dot) : id;
            if (c.Type == "gimbal" && (dir == "pitch" || dir == "roll"))
                return device + ".axis." + dir;
            if (c.Type == "gimbal" && dir == "yaw")
                return device + ".axis.twist";
            if (c.Type == "ministick" && (dir == "x" || dir == "y"))
                return device + ".axis." + id.Substring(dot + 1).Split('.').Last() + "." + dir;
            if (CockpitCatalog.IsAxisDirection(dir) || dir.Length == 0)
                return id;
            if (dir == "long" || dir == "double" || dir == "hold")
                return id;
            // A hat's forward / aft (the manuals' words) are its up / down in the control vocabulary.
            if (c.Type == "hat4" || c.Type == "hat8" || c.Type == "hat2")
            {
                if (dir == "fwd") dir = "up";
                else if (dir == "aft") dir = "down";
            }
            return id + "." + dir;
        }

        // The row key of a function: "cp:<control>:<dir>", axes "JOY_AXIS:<control>:<dir>" (the sheet draws the axis icon
        // for inputs that read as an axis).
        public static string InputOf(CockpitControl c, CockpitFunction f)
        {
            string dir = (f.Dir ?? string.Empty).ToLowerInvariant();
            bool axis = CockpitCatalog.IsAxisDirection(dir) || ((c.Type == "axis" || c.Type == "slider" || c.Type == "twist") && dir.Length == 0);
            return (axis ? "JOY_AXIS:" : "cp:") + c.Id + ":" + dir;
        }

        // The direction the sheet orders and draws the row by (DeviceVocabulary's words).
        public static string RowDirection(string dir)
        {
            switch ((dir ?? string.Empty).ToLowerInvariant())
            {
                case "pitch": return "y";
                case "roll": return "x";
                case "on": return "up";
                case "off": return "down";
                case "long": case "double": case "hold": case "yaw": case "axis": return string.Empty;
                default: return (dir ?? string.Empty).ToLowerInvariant();
            }
        }

        // Words in front of the text where the row's icon cannot say it.
        public static string DirectionPrefix(string dir)
        {
            switch ((dir ?? string.Empty).ToLowerInvariant())
            {
                case "stage1": return "1st detent";
                case "stage2": return "2nd detent";
                case "long": return "Long press";
                case "double": return "Double press";
                case "hold": return "Hold";
                case "on": return "On";
                case "off": return "Off";
                case "pull": return "Pull";
                case "push": return "Push";
                case "in": return "In";
                case "out": return "Out";
                case "center": return "Center";
                case "x": return "X";
                case "y": return "Y";
                case "pitch": return "Pitch";
                case "roll": return "Roll";
                case "yaw": return "Yaw";
                default: return string.Empty;
            }
        }

        public static string DirectionWords(string dir)
        {
            string d = (dir ?? string.Empty).ToLowerInvariant();
            switch (d)
            {
                case "": return string.Empty;
                case "fwd": return "forward";
                case "aft": return "aft";
                case "cw": return "clockwise";
                case "ccw": return "counter-clockwise";
                case "upleft": return "up-left";
                case "upright": return "up-right";
                case "downleft": return "down-left";
                case "downright": return "down-right";
                case "press": return "press";
            }
            string prefix = DirectionPrefix(d);
            if (prefix.Length > 0)
                return prefix.ToLowerInvariant();
            if (d.StartsWith("pos", StringComparison.Ordinal))
                return "position " + d.Substring(3);
            return d;
        }

        public static string RowText(CockpitFunction f)
        {
            string prefix = DirectionPrefix(f.Dir);
            // "Pitch: Pitch (elevators)" says it twice: a text that starts with the direction keeps only the text.
            if (prefix.Length == 0 || (f.Text ?? string.Empty).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return f.Text;
            return prefix + ": " + f.Text;
        }

        private static string MarkerType(string type)
        {
            switch ((type ?? string.Empty).ToLowerInvariant())
            {
                case "hat4": case "hat8": case "hat2": return "hat";
                case "rocker": return "rocker";
                case "trigger": return "trigger";
                case "lever": case "paddle": return "paddle";
                case "switch2": case "switch3": case "guard": return "switch";
                case "slider": return "slider";
                case "wheel": return "wheel";
                case "knob": return "knob";
                case "encoder": return "encoder";
                case "selector": return "selector";
                case "ministick": return "ministick";
                case "axis": return "lever";
                case "gimbal": return "gimbal";
                case "twist": return "twist";
                case "detent": return "detent";
                default: return "button";
            }
        }

        // ------------------------------------------------------------------ the drawing

        // The drawing's shapes plus JoyPro's glyph of every part (hats, buttons, triggers ...), in drawing units.
        public static DeviceSchematic Drawing(CockpitDrawing drawing, ICollection<CockpitDrawingPart> only = null)
        {
            DeviceSchematic s = new DeviceSchematic
            {
                ModelId = "cockpit:" + drawing.Id,
                Name = drawing.Name,
                Kind = drawing.Kind,
                Shape = drawing.Id,
                Width = drawing.Width,
                Height = drawing.Height
            };
            foreach (SchematicShape shape in drawing.Shapes)
            {
                s.Shapes.Add(new SchematicShape
                {
                    Kind = shape.Kind, Role = shape.Role, Path = shape.Path, X = shape.X, Y = shape.Y, W = shape.W, H = shape.H, R = shape.R,
                    Text = shape.Text, FontSize = shape.FontSize
                });
            }
            foreach (CockpitDrawingPart part in drawing.Parts.Where(p => only == null || only.Contains(p)))
                s.Shapes.AddRange(Glyph(part));
            return s;
        }

        // JoyPro's look of a part: the same family of shapes as the device drawings (DeviceSchematic roles).
        public static List<SchematicShape> Glyph(CockpitDrawingPart p)
        {
            List<SchematicShape> shapes = new List<SchematicShape>();
            double r = Math.Max(4, p.R);
            Func<double, double, double[]> at = Rotator(p.X, p.Y, p.Angle);
            switch ((p.Type ?? string.Empty).ToLowerInvariant())
            {
                case "hat4":
                case "hat8":
                    shapes.Add(Ellipse(p.X, p.Y, r, r, "control"));
                    shapes.Add(Ellipse(p.X, p.Y, r * 0.42, r * 0.42, "face"));
                    foreach (double a in new[] { 0.0, 90, 180, 270 })
                        shapes.Add(Path(Triangle(at, a, r * 0.72, r * 0.26), "mark"));
                    if (p.Type == "hat8")
                    {
                        foreach (double a in new[] { 45.0, 135, 225, 315 })
                        {
                            double[] q = at(Math.Sin(a * Math.PI / 180) * r * 0.72, -Math.Cos(a * Math.PI / 180) * r * 0.72);
                            shapes.Add(Ellipse(q[0], q[1], r * 0.08, r * 0.08, "mark"));
                        }
                    }
                    shapes.Add(Ellipse(p.X, p.Y, r * 0.12, r * 0.12, "mark"));
                    break;
                case "hat2":
                case "rocker":
                    shapes.Add(Path(RoundRect(at, r * 0.95, r * 2.3, r * 0.45), "control"));
                    shapes.Add(Path(Triangle(at, 0, r * 0.78, r * 0.26), "mark"));
                    shapes.Add(Path(Triangle(at, 180, r * 0.78, r * 0.26), "mark"));
                    break;
                case "release":
                    shapes.Add(Ellipse(p.X, p.Y, r, r, "hot"));
                    shapes.Add(Ellipse(p.X, p.Y, r * 0.55, r * 0.55, "hot"));
                    break;
                case "trigger":
                    shapes.Add(Path(Trigger(at, r), "control"));
                    break;
                case "lever":
                case "paddle":
                    shapes.Add(Path(RoundRect(at, r * 0.75, r * 2.6, r * 0.36), "control"));
                    {
                        double[] pivot = at(0, r * 0.95);
                        shapes.Add(Ellipse(pivot[0], pivot[1], r * 0.16, r * 0.16, "mark"));
                    }
                    break;
                case "switch2":
                case "switch3":
                    shapes.Add(Path(RoundRect(at, r * 1.5, r * 1.5, r * 0.3), "bodyAlt"));
                    {
                        double[] tip = at(0, -r * 1.05);
                        shapes.Add(Line(at(0, 0), tip, "line"));
                        shapes.Add(Ellipse(tip[0], tip[1], r * 0.32, r * 0.32, "control"));
                        if (p.Type == "switch3")
                        {
                            shapes.Add(Line(at(-r * 0.55, r * 0.5), at(r * 0.55, r * 0.5), "line"));
                        }
                    }
                    break;
                case "slider":
                    shapes.Add(Path(RoundRect(at, r * 0.55, r * 3.0, r * 0.27), "bodyAlt"));
                    shapes.Add(Path(RoundRect(at, r * 1.1, r * 0.6, r * 0.2), "control"));
                    break;
                case "wheel":
                    shapes.Add(Path(RoundRect(at, r * 0.8, r * 2.0, r * 0.38), "control"));
                    foreach (double y in new[] { -0.55, 0.0, 0.55 })
                        shapes.Add(Line(at(-r * 0.32, r * y), at(r * 0.32, r * y), "line"));
                    break;
                case "knob":
                case "encoder":
                case "selector":
                    shapes.Add(Ellipse(p.X, p.Y, r, r, "control"));
                    shapes.Add(Line(at(0, 0), at(0, -r * 0.8), "line"));
                    if (p.Type != "knob")
                    {
                        foreach (double a in new[] { -50.0, -25, 0, 25, 50 })
                        {
                            double ra = a * Math.PI / 180;
                            shapes.Add(Line(at(Math.Sin(ra) * r * 1.15, -Math.Cos(ra) * r * 1.15), at(Math.Sin(ra) * r * 1.4, -Math.Cos(ra) * r * 1.4), "line"));
                        }
                    }
                    break;
                case "ministick":
                    shapes.Add(Ellipse(p.X, p.Y, r, r, "bodyAlt"));
                    shapes.Add(Ellipse(p.X, p.Y, r * 0.62, r * 0.62, "control"));
                    shapes.Add(Line(at(-r * 0.36, 0), at(r * 0.36, 0), "line"));
                    shapes.Add(Line(at(0, -r * 0.36), at(0, r * 0.36), "line"));
                    break;
                case "gimbal":
                    foreach (double a in new[] { 0.0, 90, 180, 270 })
                        shapes.Add(Path(Arrow(at, a, r * 0.25, r, r * 0.42), "accent"));
                    shapes.Add(Ellipse(p.X, p.Y, r * 0.22, r * 0.22, "accent"));
                    break;
                case "axis":
                    shapes.Add(Path(Arrow(at, 0, r * 0.1, r * 1.3, r * 0.5), "accent"));
                    shapes.Add(Path(Arrow(at, 180, r * 0.1, r * 1.3, r * 0.5), "accent"));
                    shapes.Add(Ellipse(p.X, p.Y, r * 0.2, r * 0.2, "accent"));
                    break;
                case "twist":
                    shapes.Add(Path(TwistArrow(at, r, 1), "accent"));
                    shapes.Add(Path(TwistArrow(at, r, -1), "accent"));
                    break;
                case "detent":
                    shapes.Add(Path(RoundRect(at, r * 1.8, r * 0.4, r * 0.15), "mark"));
                    break;
                case "guard":
                    shapes.Add(Path(RoundRect(at, r * 1.6, r * 1.25, r * 0.3), "hot"));
                    shapes.Add(Ellipse(p.X, p.Y, r * 0.4, r * 0.4, "control"));
                    break;
                case "label":
                    shapes.Add(new SchematicShape { Kind = "text", Role = "label", X = p.X, Y = p.Y, Text = p.Label, FontSize = Math.Max(10, r) });
                    break;
                default: // button
                    shapes.Add(Ellipse(p.X, p.Y, r, r, "control"));
                    shapes.Add(Ellipse(p.X, p.Y, r * 0.5, r * 0.5, "face"));
                    break;
            }
            return shapes;
        }

        private static Func<double, double, double[]> Rotator(double cx, double cy, double angle)
        {
            double a = angle * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
            return (dx, dy) => new[] { cx + dx * cos - dy * sin, cy + dx * sin + dy * cos };
        }

        private static SchematicShape Ellipse(double x, double y, double w, double h, string role)
        {
            return new SchematicShape { Kind = "ellipse", X = x, Y = y, W = w, H = h, Role = role };
        }

        private static SchematicShape Line(double[] a, double[] b, string role)
        {
            return new SchematicShape { Kind = "line", X = a[0], Y = a[1], W = b[0], H = b[1], Role = role };
        }

        private static SchematicShape Path(string path, string role)
        {
            return new SchematicShape { Kind = "path", Path = path, Role = role };
        }

        private static string P(double[] p)
        {
            return p[0].ToString("0.#", CultureInfo.InvariantCulture) + "," + p[1].ToString("0.#", CultureInfo.InvariantCulture);
        }

        // A small triangle pointing outward in direction a (degrees, 0 = up) with its tip at distance reach.
        private static string Triangle(Func<double, double, double[]> at, double a, double reach, double size)
        {
            double ra = a * Math.PI / 180;
            double ux = Math.Sin(ra), uy = -Math.Cos(ra), px = -uy, py = ux;
            double bx = ux * (reach - size * 1.1), by = uy * (reach - size * 1.1);
            return "M" + P(at(ux * reach, uy * reach)) + " L" + P(at(bx + px * size, by + py * size)) + " L" + P(at(bx - px * size, by - py * size)) + " Z";
        }

        // An arrow from the centre outward (a, degrees, 0 = up): shaft from start to reach, head of the given size.
        private static string Arrow(Func<double, double, double[]> at, double a, double start, double reach, double head)
        {
            double ra = a * Math.PI / 180;
            double ux = Math.Sin(ra), uy = -Math.Cos(ra), px = -uy, py = ux;
            double shaft = head * 0.32, neck = reach - head;
            StringBuilder sb = new StringBuilder();
            sb.Append("M").Append(P(at(ux * start + px * shaft, uy * start + py * shaft)));
            sb.Append(" L").Append(P(at(ux * neck + px * shaft, uy * neck + py * shaft)));
            sb.Append(" L").Append(P(at(ux * neck + px * head * 0.75, uy * neck + py * head * 0.75)));
            sb.Append(" L").Append(P(at(ux * reach, uy * reach)));
            sb.Append(" L").Append(P(at(ux * neck - px * head * 0.75, uy * neck - py * head * 0.75)));
            sb.Append(" L").Append(P(at(ux * neck - px * shaft, uy * neck - py * shaft)));
            sb.Append(" L").Append(P(at(ux * start - px * shaft, uy * start - py * shaft)));
            sb.Append(" Z");
            return sb.ToString();
        }

        // A rounded rectangle w x h centred on the part, corners of radius c (quadratic corners).
        private static string RoundRect(Func<double, double, double[]> at, double w, double h, double c)
        {
            double x0 = -w / 2, x1 = w / 2, y0 = -h / 2, y1 = h / 2;
            c = Math.Min(c, Math.Min(w, h) / 2);
            StringBuilder sb = new StringBuilder();
            sb.Append("M").Append(P(at(x0 + c, y0)));
            sb.Append(" L").Append(P(at(x1 - c, y0))).Append(" Q").Append(P(at(x1, y0))).Append(" ").Append(P(at(x1, y0 + c)));
            sb.Append(" L").Append(P(at(x1, y1 - c))).Append(" Q").Append(P(at(x1, y1))).Append(" ").Append(P(at(x1 - c, y1)));
            sb.Append(" L").Append(P(at(x0 + c, y1))).Append(" Q").Append(P(at(x0, y1))).Append(" ").Append(P(at(x0, y1 - c)));
            sb.Append(" L").Append(P(at(x0, y0 + c))).Append(" Q").Append(P(at(x0, y0))).Append(" ").Append(P(at(x0 + c, y0)));
            sb.Append(" Z");
            return sb.ToString();
        }

        // A trigger blade: curved toward -x (the finger side; turn with the angle), its spot at the centre.
        private static string Trigger(Func<double, double, double[]> at, double r)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("M").Append(P(at(r * 0.35, -r * 1.25)));
            sb.Append(" Q").Append(P(at(-r * 1.05, -r * 0.7))).Append(" ").Append(P(at(-r * 0.75, r * 1.2)));
            sb.Append(" L").Append(P(at(-r * 0.35, r * 1.25)));
            sb.Append(" Q").Append(P(at(-r * 0.45, -r * 0.2))).Append(" ").Append(P(at(r * 0.55, -r * 0.8)));
            sb.Append(" Z");
            return sb.ToString();
        }

        // A curved arrow around the centre (twist), clockwise (sign 1) on the top, counter-clockwise below.
        private static string TwistArrow(Func<double, double, double[]> at, double r, int sign)
        {
            double y = sign > 0 ? -r * 0.55 : r * 0.55;
            double bulge = sign > 0 ? -r * 0.95 : r * 0.95;
            double tipX = sign > 0 ? r * 0.95 : -r * 0.95;
            double w = r * 0.13;
            StringBuilder sb = new StringBuilder();
            sb.Append("M").Append(P(at(-tipX * 0.85, y - w)));
            sb.Append(" Q").Append(P(at(0, bulge - w))).Append(" ").Append(P(at(tipX * 0.6, y - w)));
            sb.Append(" L").Append(P(at(tipX * 0.55, y - w * 3.2)));
            sb.Append(" L").Append(P(at(tipX, y + w * 0.5)));
            sb.Append(" L").Append(P(at(tipX * 0.5, y + w * 3)));
            sb.Append(" L").Append(P(at(tipX * 0.6, y + w)));
            sb.Append(" Q").Append(P(at(0, bulge + w))).Append(" ").Append(P(at(-tipX * 0.85, y + w)));
            sb.Append(" Z");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ the surrounding cockpit

        // A plan of the cockpit around the pilot, drawn from the variant's panels: glareshield and the main panel on top,
        // the consoles left and right, the pedestal between the knees, the overhead above. Generic boxes with the panel
        // names, JoyPro's own diagram for any aircraft (also fictional ones: it shows no design).
        public static DeviceSchematic Plan(CockpitVariant variant)
        {
            DeviceSchematic s = new DeviceSchematic { ModelId = "cockpit-plan:" + (variant == null ? string.Empty : variant.Id), Name = "Cockpit", Kind = "panel", Width = 1000, Height = 700 };
            if (variant == null)
                return s;
            Dictionary<string, double[]> areas = PlanAreas();
            HashSet<string> used = new HashSet<string>(variant.Panels.Select(p => AreaKey(p.Area)), StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, double[]> area in areas)
            {
                if (!used.Contains(area.Key) && area.Key != "front-center" && area.Key != "left-console" && area.Key != "right-console")
                    continue;
                double[] r = area.Value;
                s.Shapes.Add(new SchematicShape { Kind = "rect", Role = used.Contains(area.Key) ? "face" : "bodyAlt", X = r[0], Y = r[1], W = r[2], H = r[3], R = 14 });
                s.Shapes.Add(new SchematicShape { Kind = "text", Role = "label", X = r[0] + r[2] / 2, Y = r[1] + 26, Text = AreaTitle(area.Key), FontSize = 18 });
            }
            // The seat and the pilot's hands.
            s.Shapes.Add(new SchematicShape { Kind = "rect", Role = "bodyAlt", X = 420, Y = 520, W = 160, H = 150, R = 30 });
            s.Shapes.Add(new SchematicShape { Kind = "text", Role = "label", X = 500, Y = 605, Text = "Seat", FontSize = 18 });
            int n = 0;
            foreach (IGrouping<string, CockpitPanel> group in variant.Panels.GroupBy(p => AreaKey(p.Area)))
            {
                double[] r;
                if (!areas.TryGetValue(group.Key, out r))
                    continue;
                double y = r[1] + 52;
                foreach (CockpitPanel p in group)
                {
                    n++;
                    if (y + 18 > r[1] + r[3])
                        break;
                    s.Shapes.Add(new SchematicShape { Kind = "text", Role = "line", X = r[0] + r[2] / 2, Y = y, Text = Clip(p.Name, (int)(r[2] / 10)), FontSize = 16 });
                    y += 24;
                }
            }
            return s;
        }

        private static string Clip(string text, int max)
        {
            text = text ?? string.Empty;
            return text.Length <= max ? text : text.Substring(0, Math.Max(1, max - 2)) + "..";
        }

        public static string AreaKey(string area)
        {
            string a = (area ?? string.Empty).Trim().ToLowerInvariant();
            switch (a)
            {
                case "front": case "main-panel": case "front-panel": case "center": return "front-center";
                case "pedestal": case "center-console": return "center-pedestal";
                case "left": return "left-console";
                case "right": return "right-console";
                case "hud": case "glare-shield": return "glareshield";
                default: return a;
            }
        }

        public static string AreaTitle(string area)
        {
            switch (AreaKey(area))
            {
                case "glareshield": return "Glareshield / HUD";
                case "front-left": return "Main panel, left";
                case "front-center": return "Main panel";
                case "front-right": return "Main panel, right";
                case "left-console": return "Left console";
                case "right-console": return "Right console";
                case "center-pedestal": return "Centre pedestal";
                case "overhead": return "Overhead";
                case "left-side": return "Left side wall";
                case "right-side": return "Right side wall";
                case "floor": return "Floor";
                case "rear": return "Behind the seat";
                case "helmet": return "Helmet";
                case "seat": return "Seat";
                case "stick": return "Stick";
                case "throttle": return "Throttle";
                default: return area ?? string.Empty;
            }
        }

        // The centre of an area on the plan (0..1); stick and throttle at the pilot's hands; null for an unknown area.
        public static double[] AreaCenter(string area)
        {
            string key = AreaKey(area);
            if (key == "stick")
                return new[] { 0.55, 0.72 };
            if (key == "throttle")
                return new[] { 0.36, 0.66 };
            double[] r;
            return PlanAreas().TryGetValue(key, out r) ? new[] { (r[0] + r[2] / 2) / 1000.0, (r[1] + r[3] / 2) / 700.0 } : null;
        }

        // Areas of the plan (x, y, w, h on 1000 x 700).
        private static Dictionary<string, double[]> PlanAreas()
        {
            return new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "overhead", new double[] { 330, 8, 340, 92 } },
                { "glareshield", new double[] { 240, 108, 520, 82 } },
                { "front-left", new double[] { 150, 198, 220, 200 } },
                { "front-center", new double[] { 380, 198, 240, 200 } },
                { "front-right", new double[] { 630, 198, 220, 200 } },
                { "left-side", new double[] { 8, 108, 134, 290 } },
                { "right-side", new double[] { 858, 108, 134, 290 } },
                { "left-console", new double[] { 60, 410, 340, 200 } },
                { "right-console", new double[] { 600, 410, 340, 200 } },
                { "center-pedestal", new double[] { 420, 406, 160, 108 } },
                { "floor", new double[] { 60, 618, 340, 76 } },
                { "rear", new double[] { 600, 618, 340, 76 } },
                { "helmet", new double[] { 8, 8, 200, 92 } }
            };
        }
    }
}
