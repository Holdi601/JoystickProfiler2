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
    // The device classes JoyPro can always draw (docs/device-reference.md, "Picture sources"): the last picture source
    // when there is no photo and JoyPro's drawing of the model would not be recognizable (a grip with 30 parts in
    // rows). A class drawing is a clean outline of that kind of device with no markers: it never pretends to know where
    // a part is. The legend lists the inputs; the marker assistant places them.
    public static class DeviceClasses
    {
        public static readonly string[] All =
        {
            "stick", "throttle", "hotas", "pedals", "panel", "buttonbox", "keyboard", "gamepad", "wheel", "yoke", "collective",
            "quadrant", "mfd", "base", "virtual"
        };

        public static string Title(string cls)
        {
            switch (cls)
            {
                case "stick": return "Joystick / grip";
                case "throttle": return "Throttle";
                case "hotas": return "HOTAS (stick and throttle in one)";
                case "pedals": return "Rudder pedals";
                case "panel": return "Panel";
                case "buttonbox": return "Button box";
                case "keyboard": return "Keyboard";
                case "gamepad": return "Gamepad";
                case "wheel": return "Steering wheel";
                case "yoke": return "Yoke";
                case "collective": return "Collective";
                case "quadrant": return "Throttle quadrant";
                case "mfd": return "MFD / OSB frame";
                case "base": return "Stick base";
                case "virtual": return "Virtual device";
                default: return "Device";
            }
        }

        // The class of a catalog model.
        public static string Of(RefDevice model)
        {
            if (model == null)
                return "buttonbox";
            string text = (model.Id + " " + model.Name).ToLowerInvariant();
            if (Regex.IsMatch(text, @"\bvjoy\b|virtual"))
                return "virtual";
            if (Regex.IsMatch(text, @"keyboard|wooting|keychron|razer huntsman"))
                return "keyboard";
            switch (model.Kind)
            {
                case "stick": return "stick";
                case "throttle": return "throttle";
                case "hotas": return "hotas";
                case "pedals": return "pedals";
                case "panel": return "panel";
                case "mfd": return "mfd";
                case "yoke": return "yoke";
                case "quadrant": return "quadrant";
                case "collective": return "collective";
                case "base": return "base";
                case "buttonbox": return "buttonbox";
                case "keyboard": return "keyboard";
                case "gamepad": return "gamepad";
                case "wheel": return "wheel";
            }
            if (Regex.IsMatch(text, @"xbox|gamepad|dualsense|dualshock|controller \(|game ?pad"))
                return "gamepad";
            if (Regex.IsMatch(text, @"\bwheel\b|g29|g920|g923|fanatec|\bmoza r\d"))
                return "wheel";
            return "buttonbox";
        }

        // The class of a device JoyPro does not know, from its name and its inputs.
        public static string Guess(DeviceProbe probe)
        {
            string name = probe == null ? string.Empty : (probe.ProductName + " " + probe.InstanceName).ToLowerInvariant();
            if (Regex.IsMatch(name, @"\bvjoy\b|virtual"))
                return "virtual";
            if (Regex.IsMatch(name, @"keyboard|wooting|keychron"))
                return "keyboard";
            if (Regex.IsMatch(name, @"xbox|gamepad|dualsense|dualshock|wireless controller|game ?pad|\bpad\b"))
                return "gamepad";
            if (Regex.IsMatch(name, @"\bwheel\b|g29|g920|g923|fanatec|racing"))
                return "wheel";
            string kind = DeviceGeneric.GuessKind(probe);
            if (kind == "panel")
            {
                int axes = probe == null || probe.Axes == null ? 0 : probe.Axes.Count;
                return axes == 0 && probe != null && probe.Buttons >= 8 ? "buttonbox" : "panel";
            }
            return kind;
        }

        // JoyPro's drawing of a model is only shown when it is recognizable: devices whose parts sit in rows anyway
        // (pedals, panels, MFDs, quadrants, yokes, collectives) and grips, bases or throttles with few parts. A grip
        // or throttle with many parts drawn in rows is not (docs: the MT-50 and Alpha drawings were "not usable"):
        // then the class drawing and the legend are shown instead.
        public static bool DrawingIsRecognizable(RefDevice model, DeviceSchematic schematic)
        {
            if (model == null || schematic == null || string.Equals(model.Origin, "generic", StringComparison.OrdinalIgnoreCase))
                return false;
            // A virtual device, a keyboard, a gamepad or a wheel drawn as a panel of buttons is not recognizable: their
            // class outline is.
            string cls = Of(model);
            if (cls == "virtual" || cls == "keyboard" || cls == "gamepad" || cls == "wheel")
                return false;
            string shape = schematic.Shape ?? string.Empty;
            if (shape == "pedals" || shape == "mfd" || shape == "panel" || shape == "quadrant" || shape == "yoke" || shape == "collective")
                return schematic.Markers.Count > 0;
            return schematic.Markers.Count > 0 && schematic.Markers.Count <= 14;
        }

        // The outline of a class (no markers). Hand mirrors sticks for the left hand.
        public static DeviceSchematic Drawing(string cls, string hand, string title)
        {
            DrawingBuilder d = new DrawingBuilder(1000, 1000);
            switch (cls)
            {
                case "stick": Stick(d, 0, 0, 1, false); break;
                case "base": StickBase(d, 0, 0, 1); break;
                case "throttle": Throttle(d, 0, 0, 1); break;
                case "hotas":
                    Stick(d, -150, 60, 0.85, false);
                    Throttle(d, 330, 210, 0.62);
                    break;
                case "pedals": Pedals(d); break;
                case "panel": Panel(d); break;
                case "keyboard": d = new DrawingBuilder(1200, 560); Keyboard(d); break;
                case "gamepad": Gamepad(d); break;
                case "wheel": Wheel(d); break;
                case "yoke": Yoke(d); break;
                case "collective": Collective(d); break;
                case "quadrant": Quadrant(d); break;
                case "mfd": Mfd(d); break;
                case "virtual": Stick(d, 0, -30, 0.92, true); d.Text(500, 960, "virtual device", 34); break;
                default: ButtonBox(d); break;
            }
            DeviceSchematic schematic = new DeviceSchematic
            {
                ModelId = "class-" + cls,
                Name = string.IsNullOrWhiteSpace(title) ? Title(cls) : title,
                Kind = cls,
                Shape = "class." + cls,
                Hand = hand ?? string.Empty,
                Width = d.Width,
                Height = d.Height,
                Shapes = d.Shapes,
                Body = new[] { 0.1, 0.1, 0.9, 0.9 }
            };
            if (hand == "left" && (cls == "stick" || cls == "virtual" || cls == "hotas"))
                d.MirrorInto(schematic);
            return schematic;
        }

        private static void StickBase(DrawingBuilder d, double ox, double oy, double s)
        {
            d.Ellipse(500, 905, 300, 36, "shadow", ox, oy, s);
            d.Rect(250, 760, 500, 130, 26, "body", ox, oy, s);
            d.Rect(285, 735, 430, 46, 18, "face", ox, oy, s);
            d.Path("M420 742 L580 742 L556 676 L444 676 Z", "bodyAlt", ox, oy, s);
            d.Rect(474, 560, 52, 130, 10, "bodyAlt", ox, oy, s);
            d.Ellipse(500, 560, 46, 22, "face", ox, oy, s);
            d.Ellipse(330, 820, 14, 14, "mark", ox, oy, s);
            d.Ellipse(670, 820, 14, 14, "mark", ox, oy, s);
        }

        private static void Stick(DrawingBuilder d, double ox, double oy, double s, bool ghost)
        {
            string body = ghost ? "line" : "body", alt = ghost ? "line" : "bodyAlt", face = ghost ? "line" : "face", control = ghost ? "line" : "control";
            d.Ellipse(500, 905, 300, 36, ghost ? "line" : "shadow", ox, oy, s);
            d.Rect(250, 770, 500, 120, 26, body, ox, oy, s);
            d.Rect(285, 745, 430, 46, 18, face, ox, oy, s);
            d.Path("M425 752 L575 752 L552 690 L448 690 Z", alt, ox, oy, s);
            d.Rect(476, 600, 48, 100, 10, alt, ox, oy, s);
            // The grip, thumb side, trigger to the left.
            d.Path("M452 612 C440 540 428 450 436 360 C442 280 462 220 505 188 C560 150 640 156 676 200 C706 238 700 292 668 326 C646 350 630 380 626 420 C620 480 610 545 594 612 Z", body, ox, oy, s);
            d.Path("M468 330 C500 300 560 290 610 300 C628 304 640 296 650 284", ghost ? "line" : "line", ox, oy, s);
            d.Ellipse(565, 222, 28, 22, control, ox, oy, s);
            d.Ellipse(628, 250, 18, 15, control, ox, oy, s);
            d.Ellipse(520, 260, 16, 13, control, ox, oy, s);
            d.Path("M440 392 C410 396 396 420 402 448 C422 444 440 434 452 426 Z", control, ox, oy, s);
            d.Path("M612 470 C650 476 672 500 676 528 C652 526 628 514 614 504 Z", control, ox, oy, s);
        }

        private static void Throttle(DrawingBuilder d, double ox, double oy, double s)
        {
            d.Ellipse(500, 900, 360, 36, "shadow", ox, oy, s);
            d.Rect(170, 690, 660, 180, 28, "body", ox, oy, s);
            d.Rect(200, 668, 600, 52, 18, "face", ox, oy, s);
            d.Rect(300, 684, 400, 18, 9, "screen", ox, oy, s);
            d.Path("M470 690 L530 690 L566 470 L504 470 Z", "bodyAlt", ox, oy, s);
            d.Path("M370 330 C370 286 402 258 446 258 L652 258 C704 258 736 290 736 340 L736 432 C736 474 704 498 662 498 L440 498 C398 498 370 470 370 430 Z", "body", ox, oy, s);
            d.Path("M398 300 C430 284 520 280 600 290", "line", ox, oy, s);
            d.Ellipse(446, 320, 20, 20, "control", ox, oy, s);
            d.Ellipse(512, 318, 16, 16, "control", ox, oy, s);
            d.Ellipse(660, 330, 26, 26, "control", ox, oy, s);
            d.Rect(590, 380, 70, 34, 10, "control", ox, oy, s);
            for (int i = 0; i < 4; i++)
                d.Ellipse(250 + i * 70, 790, 16, 16, "control", ox, oy, s);
            d.Rect(560, 768, 120, 44, 10, "control", ox, oy, s);
        }

        private static void Pedals(DrawingBuilder d)
        {
            d.Ellipse(500, 880, 420, 36, "shadow", 0, 0, 1);
            d.Path("M120 760 L880 760 L830 830 L170 830 Z", "bodyAlt", 0, 0, 1);
            d.Rect(390, 600, 220, 180, 24, "body", 0, 0, 1);
            foreach (double x in new double[] { 160, 610 })
            {
                d.Path("M" + F(x) + " 330 C" + F(x) + " 296 " + F(x + 26) + " 276 " + F(x + 58) + " 276 L" + F(x + 172) + " 276 C" + F(x + 204) + " 276 " + F(x + 230) + " 296 " + F(x + 230) + " 330 L" + F(x + 218) + " 760 L" + F(x + 12) + " 760 Z", "body", 0, 0, 1);
                for (int g = 0; g < 6; g++)
                    d.Line(x + 40, 340 + g * 62, x + 190, 340 + g * 62);
            }
        }

        private static void Panel(DrawingBuilder d)
        {
            d.Rect(90, 230, 820, 540, 26, "shadow", 0, 12, 1);
            d.Rect(90, 220, 820, 540, 26, "body", 0, 0, 1);
            d.Rect(120, 250, 760, 480, 18, "face", 0, 0, 1);
            foreach (double[] p in new[] { new double[] { 120, 250 }, new double[] { 880, 250 }, new double[] { 120, 730 }, new double[] { 880, 730 } })
                d.Ellipse(p[0] + (p[0] < 500 ? 20 : -20), p[1] + (p[1] < 500 ? 20 : -20), 9, 9, "mark", 0, 0, 1);
            for (int i = 0; i < 5; i++)
            {
                double x = 210 + i * 145;
                d.Ellipse(x, 360, 24, 24, "bodyAlt", 0, 0, 1);
                d.Rect(x - 7, 300, 14, 64, 7, "control", 0, 0, 1);
            }
            for (int i = 0; i < 3; i++)
                d.Ellipse(260 + i * 220, 520, 46, 46, "control", 0, 0, 1);
            for (int i = 0; i < 6; i++)
                d.Rect(190 + i * 108, 630, 70, 46, 10, "control", 0, 0, 1);
        }

        private static void ButtonBox(DrawingBuilder d)
        {
            d.Rect(150, 300, 700, 450, 30, "shadow", 0, 14, 1);
            d.Rect(150, 290, 700, 450, 30, "body", 0, 0, 1);
            d.Rect(180, 320, 640, 390, 20, "face", 0, 0, 1);
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 4; col++)
                    d.Ellipse(285 + col * 145, 400 + row * 115, 38, 38, "control", 0, 0, 1);
        }

        private static void Keyboard(DrawingBuilder d)
        {
            d.Rect(40, 110, 1120, 360, 26, "shadow", 0, 14, 1);
            d.Rect(40, 100, 1120, 360, 26, "body", 0, 0, 1);
            double key = 58, gap = 8;
            double[] rowStart = { 70, 70, 70, 70, 70 };
            int[] rowKeys = { 13, 13, 12, 11, 0 };
            for (int row = 0; row < 4; row++)
            {
                double y = 130 + row * (key + gap);
                double x = rowStart[row] + row * 18;
                for (int k = 0; k < rowKeys[row]; k++)
                {
                    d.Rect(x, y, key, key, 8, "face", 0, 0, 1);
                    x += key + gap;
                }
            }
            double bottom = 130 + 4 * (key + gap);
            d.Rect(70, bottom, 90, key, 8, "face", 0, 0, 1);
            d.Rect(170, bottom, 90, key, 8, "face", 0, 0, 1);
            d.Rect(270, bottom, 430, key, 8, "face", 0, 0, 1);
            d.Rect(710, bottom, 90, key, 8, "face", 0, 0, 1);
            d.Rect(810, bottom, 90, key, 8, "face", 0, 0, 1);
            // Arrows and number pad block.
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 3; c++)
                    d.Rect(950 + c * (key + gap), 130 + r * (key + gap), key, key, 8, "face", 0, 0, 1);
            d.Rect(950, bottom, key * 2 + gap, key, 8, "face", 0, 0, 1);
            d.Rect(950 + 2 * (key + gap), bottom, key, key, 8, "face", 0, 0, 1);
        }

        private static void Gamepad(DrawingBuilder d)
        {
            d.Ellipse(500, 820, 340, 30, "shadow", 0, 0, 1);
            d.Path("M300 330 C360 300 640 300 700 330 C800 340 860 420 890 560 C915 680 880 760 820 770 C770 778 735 735 690 660 C670 628 640 612 600 612 L400 612 C360 612 330 628 310 660 C265 735 230 778 180 770 C120 760 85 680 110 560 C140 420 200 340 300 330 Z", "body", 0, 0, 1);
            d.Rect(250, 290, 140, 36, 16, "bodyAlt", 0, 0, 1);
            d.Rect(610, 290, 140, 36, 16, "bodyAlt", 0, 0, 1);
            d.Ellipse(320, 450, 50, 50, "face", 0, 0, 1);
            d.Ellipse(320, 450, 30, 30, "control", 0, 0, 1);
            d.Ellipse(590, 560, 50, 50, "face", 0, 0, 1);
            d.Ellipse(590, 560, 30, 30, "control", 0, 0, 1);
            d.Path("M390 535 L420 535 L420 505 L450 505 L450 535 L480 535 L480 565 L450 565 L450 595 L420 595 L420 565 L390 565 Z", "control", 0, 0, 1);
            d.Ellipse(700, 400, 22, 22, "control", 0, 0, 1);
            d.Ellipse(745, 445, 22, 22, "control", 0, 0, 1);
            d.Ellipse(655, 445, 22, 22, "control", 0, 0, 1);
            d.Ellipse(700, 490, 22, 22, "control", 0, 0, 1);
            d.Ellipse(500, 420, 26, 26, "accent", 0, 0, 1);
        }

        private static void Wheel(DrawingBuilder d)
        {
            d.Ellipse(500, 900, 300, 32, "shadow", 0, 0, 1);
            d.Rect(380, 690, 240, 190, 24, "bodyAlt", 0, 0, 1);
            d.Ellipse(500, 450, 330, 330, "body", 0, 0, 1);
            d.Ellipse(500, 450, 270, 270, "face", 0, 0, 1);
            d.Rect(230, 420, 540, 70, 26, "body", 0, 0, 1);
            d.Path("M440 480 L560 480 L590 720 L410 720 Z", "body", 0, 0, 1);
            d.Ellipse(500, 455, 80, 80, "bodyAlt", 0, 0, 1);
            d.Ellipse(320, 455, 18, 18, "control", 0, 0, 1);
            d.Ellipse(680, 455, 18, 18, "control", 0, 0, 1);
        }

        private static void Yoke(DrawingBuilder d)
        {
            d.Rect(200, 720, 600, 160, 24, "body", 0, 0, 1);
            d.Rect(470, 560, 60, 180, 14, "bodyAlt", 0, 0, 1);
            d.Path("M200 300 C200 250 240 230 280 230 L330 230 C370 230 390 260 390 300 L390 420 L610 420 L610 300 C610 260 630 230 670 230 L720 230 C760 230 800 250 800 300 L800 520 C800 570 760 600 710 600 L290 600 C240 600 200 570 200 520 Z", "body", 0, 0, 1);
            d.Ellipse(290, 290, 22, 22, "control", 0, 0, 1);
            d.Ellipse(710, 290, 22, 22, "control", 0, 0, 1);
            d.Rect(440, 470, 120, 60, 12, "face", 0, 0, 1);
        }

        private static void Collective(DrawingBuilder d)
        {
            d.Rect(160, 760, 340, 120, 22, "body", 0, 0, 1);
            d.Path("M300 780 L360 780 L820 330 L770 290 Z", "bodyAlt", 0, 0, 1);
            d.Path("M720 210 C720 180 745 160 775 160 L860 160 C890 160 910 182 910 212 L910 330 C910 360 888 380 858 380 L772 380 C742 380 720 360 720 330 Z", "body", 0, 0, 1);
            d.Ellipse(780, 220, 20, 20, "control", 0, 0, 1);
            d.Ellipse(850, 220, 20, 20, "control", 0, 0, 1);
            d.Rect(770, 280, 90, 40, 10, "control", 0, 0, 1);
        }

        private static void Quadrant(DrawingBuilder d)
        {
            d.Rect(180, 560, 640, 300, 30, "body", 0, 0, 1);
            d.Rect(210, 590, 580, 60, 16, "face", 0, 0, 1);
            string[] roles = { "control", "accent", "hot" };
            for (int i = 0; i < 3; i++)
            {
                double x = 330 + i * 170;
                d.Rect(x - 6, 600, 12, 40, 6, "screen", 0, 0, 1);
                d.Path("M" + F(x - 10) + " 610 L" + F(x + 10) + " 610 L" + F(x + 16) + " 360 L" + F(x - 16) + " 360 Z", "bodyAlt", 0, 0, 1);
                d.Rect(x - 50, 300, 100, 70, 18, roles[i], 0, 0, 1);
            }
        }

        private static void Mfd(DrawingBuilder d)
        {
            d.Rect(130, 130, 740, 740, 40, "body", 0, 0, 1);
            d.Rect(240, 240, 520, 520, 16, "screen", 0, 0, 1);
            for (int i = 0; i < 5; i++)
            {
                double p = 290 + i * 105;
                d.Rect(p - 28, 168, 56, 40, 8, "control", 0, 0, 1);
                d.Rect(p - 28, 792, 56, 40, 8, "control", 0, 0, 1);
                d.Rect(168, p - 20, 40, 56 - 16, 8, "control", 0, 0, 1);
                d.Rect(792, p - 20, 40, 56 - 16, 8, "control", 0, 0, 1);
            }
        }

        private static string F(double v)
        {
            return v.ToString("0.#", CultureInfo.InvariantCulture);
        }

        // Shapes in a canvas, moved by (ox, oy) and scaled by s around the canvas' top left.
        private sealed class DrawingBuilder
        {
            public readonly double Width, Height;
            public readonly List<SchematicShape> Shapes = new List<SchematicShape>();

            public DrawingBuilder(double width, double height)
            {
                Width = width;
                Height = height;
            }

            public void Rect(double x, double y, double w, double h, double r, string role, double ox, double oy, double s)
            {
                Shapes.Add(new SchematicShape { Kind = "rect", X = ox + x * s, Y = oy + y * s, W = w * s, H = h * s, R = r * s, Role = role });
            }

            public void Ellipse(double x, double y, double rx, double ry, string role, double ox, double oy, double s)
            {
                Shapes.Add(new SchematicShape { Kind = "ellipse", X = ox + x * s, Y = oy + y * s, W = rx * s, H = ry * s, Role = role });
            }

            public void Line(double x1, double y1, double x2, double y2)
            {
                Shapes.Add(new SchematicShape { Kind = "line", X = x1, Y = y1, W = x2, H = y2, Role = "line" });
            }

            public void Text(double x, double y, string text, double size)
            {
                Shapes.Add(new SchematicShape { Kind = "text", X = x, Y = y, Text = text, FontSize = size, Role = "label" });
            }

            // Path data with absolute M/L/C/Q/Z commands: every number pair is a point.
            public void Path(string data, string role, double ox, double oy, double s)
            {
                Shapes.Add(new SchematicShape { Kind = "path", Path = Transform(data, (x, y) => new[] { ox + x * s, oy + y * s }), Role = role });
            }

            public void MirrorInto(DeviceSchematic schematic)
            {
                double w = schematic.Width;
                foreach (SchematicShape shape in schematic.Shapes)
                {
                    switch (shape.Kind)
                    {
                        case "rect": shape.X = w - shape.X - shape.W; break;
                        case "ellipse": case "text": shape.X = w - shape.X; break;
                        case "line": shape.X = w - shape.X; shape.W = w - shape.W; break;
                        case "path": shape.Path = Transform(shape.Path, (x, y) => new[] { w - x, y }); break;
                    }
                }
            }

            private static string Transform(string data, Func<double, double, double[]> map)
            {
                StringBuilder sb = new StringBuilder();
                List<double> pending = new List<double>();
                foreach (Match token in Regex.Matches(data ?? string.Empty, @"[MLCQZmlcqz]|-?\d+(\.\d+)?"))
                {
                    string t = token.Value;
                    if (char.IsLetter(t[0]))
                    {
                        sb.Append(t).Append(' ');
                        continue;
                    }
                    pending.Add(double.Parse(t, CultureInfo.InvariantCulture));
                    if (pending.Count == 2)
                    {
                        double[] p = map(pending[0], pending[1]);
                        sb.Append(F(p[0])).Append(' ').Append(F(p[1])).Append(' ');
                        pending.Clear();
                    }
                }
                return sb.ToString().Trim();
            }
        }
    }
}
