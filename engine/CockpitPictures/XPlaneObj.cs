#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace JoyPro.CockpitPictures
{
    // X-Plane's object format (OBJ8, open text): vertices (VT), indices (IDX/IDX10), triangle runs (TRIS), animations
    // (ANIM_trans, ANIM_rotate, key-framed, hide/show by dataref) and the manipulators (ATTR_manip_*: what a click does,
    // the command or dataref and the tooltip text). Read in the resting pose: every dataref at 0.
    internal sealed class XPlaneObj
    {
        public string Texture = string.Empty;
        public List<XPlaneTris> Runs = new List<XPlaneTris>();
        public float[] Vertices;  // 8 per vertex: x y z nx ny nz s t
        public int[] Indices;

        public static XPlaneObj Read(string path)
        {
            XPlaneObj o = new XPlaneObj();
            List<float> vt = new List<float>();
            List<int> idx = new List<int>();
            Stack<Frame> stack = new Stack<Frame>();
            Frame cur = new Frame { Matrix = Mat4.Identity() };
            XPlaneManip manip = null;
            bool drawDisabled = false;
            RotateKeys rot = null;
            TransKeys trans = null;
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                int sp = line.IndexOfAny(new[] { ' ', '\t' });
                string cmd = sp < 0 ? line : line.Substring(0, sp);
                string[] t = sp < 0 ? new string[0] : line.Substring(sp + 1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                switch (cmd)
                {
                    case "VT":
                        for (int i = 0; i < 8 && i < t.Length; i++)
                            vt.Add(F(t[i]));
                        for (int i = t.Length; i < 8; i++)
                            vt.Add(0);
                        break;
                    case "IDX10":
                    case "IDX":
                        foreach (string s in t)
                        {
                            int v;
                            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                                idx.Add(v);
                        }
                        break;
                    case "TEXTURE":
                        if (t.Length > 0)
                            o.Texture = string.Join(" ", t);
                        break;
                    case "TRIS":
                        if (t.Length >= 2)
                        {
                            o.Runs.Add(new XPlaneTris
                            {
                                Offset = (int)F(t[0]),
                                Count = (int)F(t[1]),
                                Matrix = (double[])cur.Matrix.Clone(),
                                Hidden = cur.Hidden || drawDisabled,
                                Manip = manip,
                                Datarefs = cur.Datarefs.ToList()
                            });
                        }
                        break;
                    case "ANIM_begin":
                        stack.Push(cur);
                        cur = new Frame { Matrix = (double[])cur.Matrix.Clone(), Hidden = cur.Hidden, Datarefs = cur.Datarefs.ToList() };
                        break;
                    case "ANIM_end":
                        if (stack.Count > 0)
                            cur = stack.Pop();
                        break;
                    case "ANIM_trans":
                        if (t.Length >= 9 && t[8] != "none")
                            cur.Datarefs.Add(t[8]);
                        if (t.Length >= 8)
                        {
                            double v1 = F(t[6]), v2 = F(t[7]);
                            double k = Fraction(0, v1, v2);
                            cur.Matrix = Mat4.Multiply(cur.Matrix, Mat4.Translation(F(t[0]) + (F(t[3]) - F(t[0])) * k, F(t[1]) + (F(t[4]) - F(t[1])) * k, F(t[2]) + (F(t[5]) - F(t[2])) * k));
                        }
                        break;
                    case "ANIM_rotate":
                        if (t.Length >= 8)
                            cur.Datarefs.Add(t[7]);
                        if (t.Length >= 7)
                        {
                            double r = F(t[3]) + (F(t[4]) - F(t[3])) * Fraction(0, F(t[5]), F(t[6]));
                            cur.Matrix = Mat4.Multiply(cur.Matrix, AxisAngle(F(t[0]), F(t[1]), F(t[2]), r));
                        }
                        break;
                    case "ANIM_rotate_begin":
                        if (t.Length >= 3)
                            rot = new RotateKeys { X = F(t[0]), Y = F(t[1]), Z = F(t[2]) };
                        if (t.Length >= 4)
                            cur.Datarefs.Add(t[3]);
                        break;
                    case "ANIM_rotate_key":
                        if (rot != null && t.Length >= 2)
                            rot.Keys.Add(new double[] { F(t[0]), F(t[1]) });
                        break;
                    case "ANIM_rotate_end":
                        if (rot != null)
                        {
                            cur.Matrix = Mat4.Multiply(cur.Matrix, AxisAngle(rot.X, rot.Y, rot.Z, Keyed(rot.Keys, 1)));
                            rot = null;
                        }
                        break;
                    case "ANIM_trans_begin":
                        trans = new TransKeys();
                        if (t.Length >= 1)
                            cur.Datarefs.Add(t[0]);
                        break;
                    case "ANIM_trans_key":
                        if (trans != null && t.Length >= 4)
                            trans.Keys.Add(new double[] { F(t[0]), F(t[1]), F(t[2]), F(t[3]) });
                        break;
                    case "ANIM_trans_end":
                        if (trans != null && trans.Keys.Count > 0)
                        {
                            cur.Matrix = Mat4.Multiply(cur.Matrix, Mat4.Translation(Keyed(trans.Keys, 1), Keyed(trans.Keys, 2), Keyed(trans.Keys, 3)));
                            trans = null;
                        }
                        break;
                    case "ANIM_hide":
                    case "ANIM_show":
                        if (t.Length >= 3)
                        {
                            string dref = t[2];
                            // The pilot's choices (hide the yoke ...) count as "shown".
                            if (dref.IndexOf("hide_yoke", StringComparison.OrdinalIgnoreCase) >= 0 || dref.IndexOf("/view/", StringComparison.OrdinalIgnoreCase) >= 0)
                                break;
                            bool inside = 0 >= Math.Min(F(t[0]), F(t[1])) && 0 <= Math.Max(F(t[0]), F(t[1]));
                            if (cmd == "ANIM_hide" && inside)
                                cur.Hidden = true;
                            if (cmd == "ANIM_show" && !inside)
                                cur.Hidden = true;
                        }
                        break;
                    case "ATTR_draw_disable":
                        drawDisabled = true;
                        break;
                    case "ATTR_draw_enable":
                        drawDisabled = false;
                        break;
                    case "ATTR_manip_none":
                        manip = null;
                        break;
                    case "ATTR_manip_keyframe":
                    case "ATTR_manip_wheel":
                    case "ATTR_axis_detent_range":
                    case "ATTR_axis_detented":
                        break;
                    default:
                        if (cmd.StartsWith("ATTR_manip_", StringComparison.Ordinal))
                            manip = XPlaneManip.Parse(cmd.Substring(11), t);
                        break;
                }
            }
            o.Vertices = vt.ToArray();
            o.Indices = idx.ToArray();
            return o;
        }

        private sealed class Frame
        {
            public double[] Matrix;
            public bool Hidden;
            public List<string> Datarefs = new List<string>();
        }

        private sealed class RotateKeys
        {
            public double X, Y, Z;
            public List<double[]> Keys = new List<double[]>();
        }

        private sealed class TransKeys
        {
            public List<double[]> Keys = new List<double[]>();
        }

        private static float F(string s)
        {
            float v;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        // Where 0 lies between v1 and v2 (0..1, clamped).
        private static double Fraction(double v, double v1, double v2)
        {
            if (Math.Abs(v2 - v1) < 1e-12)
                return 0;
            return Math.Max(0, Math.Min(1, (v - v1) / (v2 - v1)));
        }

        // The keyed value at dataref 0 (column c of the keys; column 0 is the dataref value).
        private static double Keyed(List<double[]> keys, int c)
        {
            if (keys.Count == 0)
                return 0;
            List<double[]> k = keys.OrderBy(x => x[0]).ToList();
            if (0 <= k[0][0])
                return k[0][c];
            if (0 >= k[k.Count - 1][0])
                return k[k.Count - 1][c];
            for (int i = 0; i + 1 < k.Count; i++)
            {
                if (0 >= k[i][0] && 0 <= k[i + 1][0])
                {
                    double f = Fraction(0, k[i][0], k[i + 1][0]);
                    return k[i][c] + (k[i + 1][c] - k[i][c]) * f;
                }
            }
            return k[0][c];
        }

        private static double[] AxisAngle(double x, double y, double z, double degrees)
        {
            double l = Math.Sqrt(x * x + y * y + z * z);
            if (l < 1e-12)
                return Mat4.Identity();
            double a = degrees * Math.PI / 180 / 2;
            double s = Math.Sin(a) / l;
            return Mat4.Rotation(new[] { Math.Cos(a), x * s, y * s, z * s });
        }
    }

    internal sealed class XPlaneTris
    {
        public int Offset;
        public int Count;
        public double[] Matrix;
        public bool Hidden;
        public XPlaneManip Manip;
        public List<string> Datarefs = new List<string>();
    }

    // A manipulator: its kind, the commands or datarefs it works and the tooltip.
    internal sealed class XPlaneManip
    {
        public string Kind = string.Empty;
        public List<string> Commands = new List<string>();
        public List<string> Datarefs = new List<string>();
        public string Tooltip = string.Empty;

        public bool Clickable
        {
            get { return Kind != "noop" && (Commands.Count > 0 || Datarefs.Count > 0); }
        }

        public static XPlaneManip Parse(string kind, string[] t)
        {
            XPlaneManip m = new XPlaneManip { Kind = kind };
            // Tokens: the cursor, numbers, commands or datarefs (they hold a '/'), "none", then the tooltip words.
            int last = 0;
            for (int i = 0; i < t.Length; i++)
            {
                string s = t[i];
                double ignored;
                bool number = double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out ignored);
                bool reference = System.Text.RegularExpressions.Regex.IsMatch(s, @"^[a-z0-9_]+(/[A-Za-z0-9_\[\]\.\-]+)+$");
                if (i == 0 || number || reference || s == "none")
                {
                    last = i;
                    if (reference)
                    {
                        if (kind.IndexOf("command", StringComparison.Ordinal) >= 0)
                            m.Commands.Add(s);
                        else
                            m.Datarefs.Add(s);
                    }
                    continue;
                }
                break;
            }
            m.Tooltip = t.Length > last + 1 ? string.Join(" ", t.Skip(last + 1)).Trim() : string.Empty;
            return m;
        }
    }
}
