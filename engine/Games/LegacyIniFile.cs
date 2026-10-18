using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine.Games
{
    // A Windows ini file edited in place (IL-2 1946 settings.ini / conf.ini, Cliffs of Dover confuser.ini): every line
    // JoyPro does not change stays byte for byte. Single-byte files are read as ISO-8859-1, which maps every byte to one
    // char and back, so pilot names or comments in the user's code page survive a rewrite; UTF-8 (with BOM) and UTF-16
    // files keep their encoding. Keys may contain spaces ("Joystick1 Button3=..."); the key is the text before the first
    // '=' without surrounding blanks, the value the text after it.
    internal sealed class LegacyIniFile
    {
        private readonly List<string> lines = new List<string>();
        private Encoding encoding;
        private bool bom;
        private string newline = "\r\n";
        private bool endsWithNewline = true;

        public int ChangedLines { get; private set; }

        public IReadOnlyList<string> Lines
        {
            get { return lines; }
        }

        public static LegacyIniFile Parse(string text)
        {
            LegacyIniFile file = new LegacyIniFile { encoding = Encoding.GetEncoding(28591) };
            file.Load(text ?? string.Empty);
            return file;
        }

        public static bool TryLoad(string path, List<string> errors, out LegacyIniFile file)
        {
            file = null;
            if (!File.Exists(path))
            {
                errors.Add("File not found: " + path);
                return false;
            }
            byte[] bytes = File.ReadAllBytes(path);
            file = new LegacyIniFile();
            string text;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                file.encoding = new UnicodeEncoding(false, true);
                file.bom = true;
                text = file.encoding.GetString(bytes, 2, bytes.Length - 2);
            }
            else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                file.encoding = new UTF8Encoding(false);
                file.bom = true;
                text = file.encoding.GetString(bytes, 3, bytes.Length - 3);
            }
            else
            {
                file.encoding = Encoding.GetEncoding(28591);
                text = file.encoding.GetString(bytes);
            }
            file.Load(text);
            return true;
        }

        private void Load(string text)
        {
            newline = text.Contains("\r\n") ? "\r\n" : (text.Contains("\n") ? "\n" : "\r\n");
            endsWithNewline = text.Length == 0 || text.EndsWith("\n", StringComparison.Ordinal);
            string body = endsWithNewline && text.Length > 0 ? text.Substring(0, text.Length - (text.EndsWith("\r\n", StringComparison.Ordinal) ? 2 : 1)) : text;
            lines.Clear();
            if (text.Length > 0)
                lines.AddRange(body.Split(new[] { newline }, StringSplitOptions.None));
        }

        public string Render()
        {
            string text = string.Join(newline, lines);
            return endsWithNewline && lines.Count > 0 ? text + newline : text;
        }

        public void Save(string path)
        {
            byte[] body = encoding.GetBytes(Render());
            byte[] preamble = bom ? encoding.GetPreamble() : new byte[0];
            string temp = path + ".jptmp";
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                stream.Write(preamble, 0, preamble.Length);
                stream.Write(body, 0, body.Length);
            }
            SafeFileIo.CopyOverAndDelete(temp, path);
        }

        public static bool IsSectionHeader(string line, out string name)
        {
            name = string.Empty;
            string trimmed = (line ?? string.Empty).Trim();
            if (trimmed.Length < 2 || trimmed[0] != '[')
                return false;
            int close = trimmed.IndexOf(']');
            if (close < 1)
                return false;
            name = trimmed.Substring(1, close - 1).Trim();
            return true;
        }

        public static bool TrySplitEntry(string line, out string key, out string value)
        {
            key = string.Empty;
            value = string.Empty;
            string text = line ?? string.Empty;
            string trimmed = text.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '#' || trimmed[0] == '[')
                return false;
            int eq = text.IndexOf('=');
            if (eq <= 0)
                return false;
            key = text.Substring(0, eq).Trim();
            value = text.Substring(eq + 1).Trim();
            return key.Length > 0;
        }

        public List<string> SectionNames()
        {
            List<string> names = new List<string>();
            foreach (string line in lines)
            {
                string name;
                if (IsSectionHeader(line, out name))
                    names.Add(name);
            }
            return names;
        }

        // Index of the header line of the first section with this name (case-insensitive), -1 when missing.
        public int FindSection(string section)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string name;
                if (IsSectionHeader(lines[i], out name) && string.Equals(name, section, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        // The line after the section's last line (the next header or the end).
        private int SectionEnd(int header)
        {
            int i = header + 1;
            string ignored;
            while (i < lines.Count && !IsSectionHeader(lines[i], out ignored))
                i++;
            return i;
        }

        public sealed class Entry
        {
            public int Line;
            public string Key;
            public string Value;
        }

        public List<Entry> Entries(string section)
        {
            List<Entry> entries = new List<Entry>();
            int header = FindSection(section);
            if (header < 0)
                return entries;
            int end = SectionEnd(header);
            for (int i = header + 1; i < end; i++)
            {
                string key;
                string value;
                if (TrySplitEntry(lines[i], out key, out value))
                    entries.Add(new Entry { Line = i, Key = key, Value = value });
            }
            return entries;
        }

        public string Get(string section, string key)
        {
            Entry entry = Entries(section).FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
            return entry == null ? null : entry.Value;
        }

        // Sets "key=value" in the section: the first line of the key is rewritten (only when the value differs), other
        // lines of the same key are removed; a missing key is added after the section's last entry, a missing section
        // at the end of the file. Returns true when the file changed.
        public bool Set(string section, string key, string value)
        {
            List<Entry> same = Entries(section).Where(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
            bool changed = false;
            if (same.Count > 0)
            {
                for (int i = same.Count - 1; i >= 1; i--)
                {
                    lines.RemoveAt(same[i].Line);
                    changed = true;
                }
                if (same[0].Value != value)
                {
                    lines[same[0].Line] = same[0].Key + "=" + value;
                    changed = true;
                }
            }
            else
            {
                Insert(section, key + "=" + value);
                changed = true;
            }
            if (changed)
                ChangedLines++;
            return changed;
        }

        // Adds a line at the end of the section's entries (before trailing blank lines); creates the section.
        public void Insert(string section, string line)
        {
            int header = FindSection(section);
            if (header < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0)
                    lines.Add(string.Empty);
                lines.Add("[" + section + "]");
                lines.Add(line);
                ChangedLines++;
                return;
            }
            int end = SectionEnd(header);
            int at = end;
            while (at > header + 1 && lines[at - 1].Trim().Length == 0)
                at--;
            lines.Insert(at, line);
            ChangedLines++;
        }

        // Removes the entry lines of the section whose key matches; returns how many went.
        public int RemoveWhere(string section, Func<string, string, bool> match)
        {
            List<Entry> entries = Entries(section).Where(e => match(e.Key, e.Value)).ToList();
            for (int i = entries.Count - 1; i >= 0; i--)
                lines.RemoveAt(entries[i].Line);
            ChangedLines += entries.Count;
            return entries.Count;
        }

        // Removes these lines (indices of the current Lines); the others keep their order.
        public void RemoveLines(IEnumerable<int> indices)
        {
            foreach (int index in indices.Distinct().OrderByDescending(i => i))
            {
                if (index < 0 || index >= lines.Count)
                    continue;
                lines.RemoveAt(index);
                ChangedLines++;
            }
        }

        public void RewriteLine(int index, string line)
        {
            if (index < 0 || index >= lines.Count || lines[index] == line)
                return;
            lines[index] = line;
            ChangedLines++;
        }
    }
}
