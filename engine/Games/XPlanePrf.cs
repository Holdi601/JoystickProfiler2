using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine.Games
{
    // Line editor for X-Plane .prf preference files: "I" (or "A"), "<n> Version", then one "key value" per line.
    // Only lines that get a new value are rewritten; everything else keeps its bytes.
    internal sealed class XPlanePrf
    {
        private List<string> lines;
        private string newline;
        private bool bom;
        private readonly Dictionary<string, int> indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);

        public int ChangedLines { get; private set; }

        public static bool TryLoad(string path, List<string> errors, out XPlanePrf prf)
        {
            prf = null;
            if (!File.Exists(path))
            {
                errors.Add("X-Plane preferences file not found: " + path);
                return false;
            }

            byte[] bytes = File.ReadAllBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = text.Split(new[] { newline }, StringSplitOptions.None).ToList();
            if (lines.Count < 2 || (lines[0].Trim() != "I" && lines[0].Trim() != "A") || !lines[1].TrimEnd().EndsWith(" Version", StringComparison.Ordinal))
            {
                errors.Add("Not an X-Plane preferences file (expected 'I' and '<n> Version' on the first lines): " + path);
                return false;
            }

            prf = new XPlanePrf { lines = lines, newline = newline, bom = bom };
            prf.Reindex();
            return true;
        }

        private void Reindex()
        {
            indexByKey.Clear();
            for (int i = 2; i < lines.Count; i++)
            {
                string key = KeyOf(lines[i]);
                if (key.Length > 0 && !indexByKey.ContainsKey(key))
                    indexByKey[key] = i;
            }
        }

        private static string KeyOf(string line)
        {
            int space = line.IndexOf(' ');
            return space < 0 ? line.Trim() : line.Substring(0, space);
        }

        public string Get(string key)
        {
            int index;
            if (!indexByKey.TryGetValue(key, out index))
                return null;
            string line = lines[index];
            int space = line.IndexOf(' ');
            return space < 0 ? string.Empty : line.Substring(space + 1);
        }

        // A missing key goes after the last line of its family ("_joy_BUTN_use12" -> "_joy_BUTN_use").
        public void Set(string key, string value)
        {
            string line = key + " " + value;
            int index;
            if (indexByKey.TryGetValue(key, out index))
            {
                if (lines[index] == line)
                    return;
                lines[index] = line;
            }
            else
            {
                string family = key.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                int last = -1;
                for (int i = 2; i < lines.Count; i++)
                {
                    string other = KeyOf(lines[i]);
                    if (other.StartsWith(family, StringComparison.Ordinal) && other.Substring(family.Length).All(char.IsDigit))
                        last = i;
                }
                int insertAt = last >= 0 ? last + 1 : (lines.Count > 0 && lines[lines.Count - 1].Length == 0 ? lines.Count - 1 : lines.Count);
                lines.Insert(insertAt, line);
                Reindex();
            }
            ChangedLines++;
        }

        public void Remove(string key)
        {
            int index;
            if (!indexByKey.TryGetValue(key, out index))
                return;
            lines.RemoveAt(index);
            Reindex();
            ChangedLines++;
        }

        public void Save(string path)
        {
            byte[] body = new UTF8Encoding(false).GetBytes(string.Join(newline, lines));
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                if (bom)
                    stream.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                stream.Write(body, 0, body.Length);
            }
        }
    }

    // The value of an "axis_response_curve<i>" line, as the X-Plane 12 exe formats it:
    //   [FIXED_RANGE_TYPE %s FIXED_PT_0 %d FIXED_PT_1 %d FIXED_PT_2 %d ; ]INTERP_MODE %s{ | %f %f }
    // e.g. "INTERP_MODE CATMULL_ROM | 0.000000 0.000000  | 0.403005 0.299401  | 1.000000 1.000000 ".
    internal sealed class XPlaneResponseCurve
    {
        public string RangeType = string.Empty;
        public int[] RangePoints = new int[3];
        public string Mode = "LINEAR";
        public List<KeyValuePair<double, double>> Points = new List<KeyValuePair<double, double>>();

        public static bool TryParse(string value, out XPlaneResponseCurve curve)
        {
            curve = null;
            if (string.IsNullOrWhiteSpace(value))
                return false;
            XPlaneResponseCurve result = new XPlaneResponseCurve();
            string text = value.Trim();
            int semicolon = text.IndexOf(';');
            if (text.StartsWith("FIXED_RANGE_TYPE", StringComparison.Ordinal))
            {
                if (semicolon < 0)
                    return false;
                string[] head = text.Substring(0, semicolon).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (head.Length != 8 || head[0] != "FIXED_RANGE_TYPE" || head[2] != "FIXED_PT_0" || head[4] != "FIXED_PT_1" || head[6] != "FIXED_PT_2")
                    return false;
                result.RangeType = head[1];
                for (int i = 0; i < 3; i++)
                {
                    if (!int.TryParse(head[3 + 2 * i], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result.RangePoints[i]))
                        return false;
                }
                text = text.Substring(semicolon + 1).Trim();
            }
            string[] parts = text.Split('|');
            string[] mode = parts[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (mode.Length != 2 || mode[0] != "INTERP_MODE")
                return false;
            result.Mode = mode[1];
            for (int i = 1; i < parts.Length; i++)
            {
                string[] xy = parts[i].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (xy.Length == 0)
                    continue;
                double x;
                double y;
                if (xy.Length != 2 ||
                    !double.TryParse(xy[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) ||
                    !double.TryParse(xy[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y))
                    return false;
                result.Points.Add(new KeyValuePair<double, double>(x, y));
            }
            if (result.Points.Count < 2)
                return false;
            curve = result;
            return true;
        }

        public string Render()
        {
            StringBuilder sb = new StringBuilder();
            if (RangeType.Length > 0)
                sb.Append("FIXED_RANGE_TYPE ").Append(RangeType)
                  .Append(" FIXED_PT_0 ").Append(RangePoints[0].ToString(System.Globalization.CultureInfo.InvariantCulture))
                  .Append(" FIXED_PT_1 ").Append(RangePoints[1].ToString(System.Globalization.CultureInfo.InvariantCulture))
                  .Append(" FIXED_PT_2 ").Append(RangePoints[2].ToString(System.Globalization.CultureInfo.InvariantCulture))
                  .Append(" ; ");
            sb.Append("INTERP_MODE ").Append(Mode);
            foreach (KeyValuePair<double, double> p in Points)
                sb.Append(" | ").Append(Fixed(p.Key)).Append(' ').Append(Fixed(p.Value)).Append(' ');
            return sb.ToString();
        }

        private static string Fixed(double value)
        {
            string text = Math.Round(value, 6).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
            return text == "-0.000000" ? "0.000000" : text;
        }

        public bool SameAs(XPlaneResponseCurve other)
        {
            if (other == null || !string.Equals(Mode, other.Mode, StringComparison.Ordinal) || !string.Equals(RangeType, other.RangeType, StringComparison.Ordinal) || Points.Count != other.Points.Count)
                return false;
            if (RangeType.Length > 0 && !RangePoints.SequenceEqual(other.RangePoints))
                return false;
            for (int i = 0; i < Points.Count; i++)
            {
                if (Math.Abs(Points[i].Key - other.Points[i].Key) > 5e-7 || Math.Abs(Points[i].Value - other.Points[i].Value) > 5e-7)
                    return false;
            }
            return true;
        }
    }
}
