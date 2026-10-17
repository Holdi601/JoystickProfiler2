using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Games
{
    // The FSX / Prepar3D control file (Controls\Standard.xml, a SimBase.Document of <SimControls.Map> blocks), edited as
    // text: JoyPro replaces, removes or adds whole <Axis>/<Entry>/<POV> blocks (and whole maps) and leaves every other
    // byte as it was, so the sim's own formatting, comments and unknown elements survive. Single-byte files (the sims
    // declare Windows-1252) are read as ISO-8859-1, which keeps every byte; UTF-8/UTF-16 files keep their encoding.
    internal sealed class FsxControlsFile
    {
        private static readonly Regex MapRegex = new Regex(@"<SimControls\.Map>(?<inner>.*?)</SimControls\.Map>", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex NameRegex = new Regex(@"<Name>(?<name>.*?)</Name>", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex ChildRegex = new Regex(@"(?<lead>[ \t]*)<(?<tag>Axis|Entry|POV)>(?<body>.*?)</\k<tag>>(?<trail>[ \t]*\r?\n)?", RegexOptions.Singleline | RegexOptions.Compiled);

        private string text;
        private Encoding encoding;
        private byte[] preamble = new byte[0];
        private readonly List<KeyValuePair<int, KeyValuePair<int, string>>> edits = new List<KeyValuePair<int, KeyValuePair<int, string>>>();

        public string NewLine { get; private set; }
        public List<FsxMap> Maps { get; private set; }

        public int ChangeCount
        {
            get { return edits.Count; }
        }

        public static bool TryLoad(string path, List<string> errors, out FsxControlsFile file)
        {
            file = null;
            if (!File.Exists(path))
            {
                errors.Add("Control file not found: " + path);
                return false;
            }
            byte[] bytes = File.ReadAllBytes(path);
            file = new FsxControlsFile();
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                file.encoding = new UTF8Encoding(false);
                file.preamble = new byte[] { 0xEF, 0xBB, 0xBF };
                file.text = file.encoding.GetString(bytes, 3, bytes.Length - 3);
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                file.encoding = new UnicodeEncoding(false, false);
                file.preamble = new byte[] { 0xFF, 0xFE };
                file.text = file.encoding.GetString(bytes, 2, bytes.Length - 2);
            }
            else
            {
                string head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 200));
                file.encoding = Regex.IsMatch(head, "encoding=\"utf-8\"", RegexOptions.IgnoreCase) ? (Encoding)new UTF8Encoding(false) : Encoding.GetEncoding(28591);
                file.text = file.encoding.GetString(bytes);
            }
            if (file.text.IndexOf("<SimBase.Document", StringComparison.Ordinal) < 0)
            {
                errors.Add("Not a SimControls file (no <SimBase.Document>): " + path);
                file = null;
                return false;
            }
            file.NewLine = file.text.Contains("\r\n") ? "\r\n" : "\n";
            file.Parse();
            return true;
        }

        private void Parse()
        {
            Maps = new List<FsxMap>();
            foreach (Match map in MapRegex.Matches(text))
            {
                Group inner = map.Groups["inner"];
                Match name = NameRegex.Match(inner.Value);
                FsxMap parsed = new FsxMap
                {
                    Name = name.Success ? Unescape(name.Groups["name"].Value) : string.Empty,
                    Start = map.Index,
                    End = map.Index + map.Length,
                    InnerStart = inner.Index,
                    InnerEnd = inner.Index + inner.Length
                };
                parsed.Indent = LineIndent(map.Index);
                foreach (Match child in ChildRegex.Matches(inner.Value))
                {
                    FsxChild c = new FsxChild
                    {
                        Kind = child.Groups["tag"].Value,
                        Start = inner.Index + child.Index,
                        End = inner.Index + child.Index + child.Length,
                        Lead = child.Groups["lead"].Value,
                        Body = child.Groups["body"].Value
                    };
                    c.Index = IntField(c.Body, "Index");
                    c.AxName = Field(c.Body, "AxName");
                    c.AxEvent = Field(c.Body, "AxEvent");
                    c.AxScale = IntField(c.Body, "AxScale");
                    c.AxNull = IntField(c.Body, "AxNull");
                    c.Down = Field(c.Body, "Down");
                    c.Up = Field(c.Body, "Up");
                    c.Key = Field(c.Body, "Key");
                    parsed.Children.Add(c);
                }
                Maps.Add(parsed);
            }
        }

        private string LineIndent(int position)
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
            string before = text.Substring(lineStart, position - lineStart);
            return before.Trim().Length == 0 ? before : string.Empty;
        }

        public static string Field(string body, string name)
        {
            Match m = Regex.Match(body, "<" + Regex.Escape(name) + ">(?<v>.*?)</" + Regex.Escape(name) + ">", RegexOptions.Singleline);
            return m.Success ? Unescape(m.Groups["v"].Value.Trim()) : null;
        }

        private static int? IntField(string body, string name)
        {
            string value = Field(body, name);
            int number;
            return value != null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : (int?)null;
        }

        public static string Unescape(string value)
        {
            return (value ?? string.Empty).Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");
        }

        public static string Escape(string value)
        {
            return SecurityElement.Escape(value ?? string.Empty);
        }

        // ---- edits (applied on Save, from the end of the file backwards) -----------------------------------------

        public void Remove(FsxChild child)
        {
            edits.Add(new KeyValuePair<int, KeyValuePair<int, string>>(child.Start, new KeyValuePair<int, string>(child.End, string.Empty)));
            child.Removed = true;
        }

        public void Replace(FsxChild child, string newText)
        {
            edits.Add(new KeyValuePair<int, KeyValuePair<int, string>>(child.Start, new KeyValuePair<int, string>(child.End, newText)));
            child.Removed = true;
        }

        // Adds blocks at the end of a map (before its closing tag, on a line of their own).
        public void Append(FsxMap map, string blocks)
        {
            int closing = map.End - "</SimControls.Map>".Length;
            int lineStart = text.LastIndexOf('\n', Math.Max(0, closing - 1)) + 1;
            bool closingOnOwnLine = text.Substring(lineStart, closing - lineStart).Trim().Length == 0;
            int at = closingOnOwnLine ? lineStart : closing;
            string insert = closingOnOwnLine ? blocks : NewLine + blocks + map.Indent;
            edits.Add(new KeyValuePair<int, KeyValuePair<int, string>>(at, new KeyValuePair<int, string>(at, insert)));
        }

        // Adds a whole map before </SimBase.Document>.
        public void AppendMap(string name, string blocks)
        {
            int closing = text.LastIndexOf("</SimBase.Document>", StringComparison.Ordinal);
            if (closing < 0)
                closing = text.Length;
            int lineStart = text.LastIndexOf('\n', Math.Max(0, closing - 1)) + 1;
            bool ownLine = text.Substring(lineStart, closing - lineStart).Trim().Length == 0;
            int at = ownLine ? lineStart : closing;
            string indent = Maps.Count > 0 ? Maps[0].Indent : "    ";
            string map = indent + "<SimControls.Map>" + NewLine +
                         indent + "    <Name>" + Escape(name) + "</Name>" + NewLine +
                         blocks +
                         indent + "</SimControls.Map>" + NewLine;
            edits.Add(new KeyValuePair<int, KeyValuePair<int, string>>(at, new KeyValuePair<int, string>(at, ownLine ? map : NewLine + map)));
        }

        public string Render()
        {
            StringBuilder sb = new StringBuilder(text);
            foreach (KeyValuePair<int, KeyValuePair<int, string>> edit in edits.Select((e, i) => new { e, i }).OrderByDescending(x => x.e.Key).ThenByDescending(x => x.i).Select(x => x.e))
            {
                sb.Remove(edit.Key, edit.Value.Key - edit.Key);
                sb.Insert(edit.Key, edit.Value.Value);
            }
            return sb.ToString();
        }

        public void Save(string path)
        {
            byte[] body = encoding.GetBytes(Render());
            string temp = path + ".jptmp";
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                stream.Write(preamble, 0, preamble.Length);
                stream.Write(body, 0, body.Length);
            }
            File.Copy(temp, path, true);
            File.Delete(temp);
        }
    }

    internal sealed class FsxMap
    {
        public string Name = string.Empty;
        public int Start;
        public int End;
        public int InnerStart;
        public int InnerEnd;
        public string Indent = string.Empty;
        public List<FsxChild> Children = new List<FsxChild>();

        // The indentation of a child block in this map (copied from the first child, else one level deeper).
        public string ChildIndent
        {
            get { return Children.Count > 0 ? Children[0].Lead : Indent + "    "; }
        }
    }

    internal sealed class FsxChild
    {
        public string Kind = string.Empty;   // Axis, Entry, POV
        public int Start;
        public int End;
        public string Lead = string.Empty;
        public string Body = string.Empty;
        public int? Index;
        public string AxName;
        public string AxEvent;
        public int? AxScale;
        public int? AxNull;
        public string Down;
        public string Up;
        public string Key;
        public bool Removed;

        // The event the block triggers: an axis's AxEvent, a button's or hat's Down.
        public string Event
        {
            get { return Kind == "Axis" ? AxEvent : Down; }
        }
    }
}
