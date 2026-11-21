using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JoyPro.Engine.Games
{
    // The JSON of the Falcon BMS launcher's binding files (User\Config\JSON, BMS 4.38.2 / Launcher 3.x), read and written
    // so that a file JoyPro does not change comes out byte for byte as the launcher wrote it: members keep their order,
    // numbers their text, and the writer uses the launcher's layout (two-space indent, "name": value, CRLF, an empty
    // array as "[" and "]" on two lines, a line end after the last brace).
    internal sealed class BmsJsonObject
    {
        public readonly List<KeyValuePair<string, object>> Members = new List<KeyValuePair<string, object>>();

        public object Get(string name)
        {
            foreach (KeyValuePair<string, object> member in Members)
            {
                if (string.Equals(member.Key, name, StringComparison.Ordinal))
                    return member.Value;
            }
            return null;
        }

        public bool Has(string name)
        {
            return Members.Any(m => string.Equals(m.Key, name, StringComparison.Ordinal));
        }

        // Replaces the value in place (the member keeps its position) or appends the member.
        public void Set(string name, object value)
        {
            for (int i = 0; i < Members.Count; i++)
            {
                if (string.Equals(Members[i].Key, name, StringComparison.Ordinal))
                {
                    Members[i] = new KeyValuePair<string, object>(name, value);
                    return;
                }
            }
            Members.Add(new KeyValuePair<string, object>(name, value));
        }

        public string String(string name)
        {
            return Get(name) as string;
        }

        public List<object> Array(string name)
        {
            return Get(name) as List<object>;
        }

        // An integer member; null for null, a missing member or something else.
        public int? Int(string name)
        {
            BmsJsonNumber number = Get(name) as BmsJsonNumber;
            int value;
            return number != null && int.TryParse(number.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : (int?)null;
        }

        public bool? Bool(string name)
        {
            object value = Get(name);
            return value is bool ? (bool)value : (bool?)null;
        }
    }

    // A number as the file spells it (written back unchanged).
    internal sealed class BmsJsonNumber
    {
        public string Raw;

        public BmsJsonNumber(string raw)
        {
            Raw = raw;
        }

        public static BmsJsonNumber Of(int value)
        {
            return new BmsJsonNumber(value.ToString(CultureInfo.InvariantCulture));
        }

        public override bool Equals(object obj)
        {
            BmsJsonNumber other = obj as BmsJsonNumber;
            return other != null && string.Equals(other.Raw, Raw, StringComparison.Ordinal);
        }

        public override int GetHashCode()
        {
            return (Raw ?? string.Empty).GetHashCode();
        }
    }

    internal static class BmsJson
    {
        public static object Parse(string text)
        {
            int position = 0;
            object value = ParseValue(text, ref position);
            SkipWhitespace(text, ref position);
            if (position != text.Length)
                throw new FormatException("Unexpected text after the JSON value at " + position.ToString(CultureInfo.InvariantCulture) + ".");
            return value;
        }

        public static string Write(object value)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, value, 0);
            sb.Append("\r\n");
            return sb.ToString();
        }

        private static void SkipWhitespace(string text, ref int position)
        {
            while (position < text.Length && (text[position] == ' ' || text[position] == '\t' || text[position] == '\r' || text[position] == '\n'))
                position++;
        }

        private static object ParseValue(string text, ref int position)
        {
            SkipWhitespace(text, ref position);
            if (position >= text.Length)
                throw new FormatException("Unexpected end of the JSON text.");
            char c = text[position];
            if (c == '{')
            {
                position++;
                BmsJsonObject obj = new BmsJsonObject();
                SkipWhitespace(text, ref position);
                if (position < text.Length && text[position] == '}')
                {
                    position++;
                    return obj;
                }
                while (true)
                {
                    SkipWhitespace(text, ref position);
                    string name = ParseString(text, ref position);
                    SkipWhitespace(text, ref position);
                    Expect(text, ref position, ':');
                    obj.Members.Add(new KeyValuePair<string, object>(name, ParseValue(text, ref position)));
                    SkipWhitespace(text, ref position);
                    if (position < text.Length && text[position] == ',')
                    {
                        position++;
                        continue;
                    }
                    Expect(text, ref position, '}');
                    return obj;
                }
            }
            if (c == '[')
            {
                position++;
                List<object> list = new List<object>();
                SkipWhitespace(text, ref position);
                if (position < text.Length && text[position] == ']')
                {
                    position++;
                    return list;
                }
                while (true)
                {
                    list.Add(ParseValue(text, ref position));
                    SkipWhitespace(text, ref position);
                    if (position < text.Length && text[position] == ',')
                    {
                        position++;
                        continue;
                    }
                    Expect(text, ref position, ']');
                    return list;
                }
            }
            if (c == '"')
                return ParseString(text, ref position);
            if (Word(text, ref position, "true"))
                return true;
            if (Word(text, ref position, "false"))
                return false;
            if (Word(text, ref position, "null"))
                return null;
            int start = position;
            while (position < text.Length && "+-0123456789.eE".IndexOf(text[position]) >= 0)
                position++;
            if (position == start)
                throw new FormatException("Unexpected '" + c + "' at " + start.ToString(CultureInfo.InvariantCulture) + ".");
            return new BmsJsonNumber(text.Substring(start, position - start));
        }

        private static bool Word(string text, ref int position, string word)
        {
            if (string.CompareOrdinal(text, position, word, 0, word.Length) != 0)
                return false;
            position += word.Length;
            return true;
        }

        private static void Expect(string text, ref int position, char wanted)
        {
            if (position >= text.Length || text[position] != wanted)
                throw new FormatException("Expected '" + wanted + "' at " + position.ToString(CultureInfo.InvariantCulture) + ".");
            position++;
        }

        private static string ParseString(string text, ref int position)
        {
            Expect(text, ref position, '"');
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (position >= text.Length)
                    throw new FormatException("Unterminated JSON string.");
                char c = text[position++];
                if (c == '"')
                    return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (position >= text.Length)
                    throw new FormatException("Unterminated JSON escape.");
                char e = text[position++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (position + 4 > text.Length)
                            throw new FormatException("Short \\u escape.");
                        sb.Append((char)int.Parse(text.Substring(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        position += 4;
                        break;
                    default:
                        throw new FormatException("Unknown escape \\" + e + ".");
                }
            }
        }

        private static void WriteValue(StringBuilder sb, object value, int indent)
        {
            string pad = new string(' ', indent * 2);
            BmsJsonObject obj = value as BmsJsonObject;
            if (obj != null)
            {
                sb.Append("{\r\n");
                for (int i = 0; i < obj.Members.Count; i++)
                {
                    sb.Append(pad).Append("  ");
                    WriteString(sb, obj.Members[i].Key);
                    sb.Append(": ");
                    WriteValue(sb, obj.Members[i].Value, indent + 1);
                    sb.Append(i + 1 < obj.Members.Count ? ",\r\n" : "\r\n");
                }
                sb.Append(pad).Append('}');
                return;
            }
            List<object> list = value as List<object>;
            if (list != null)
            {
                sb.Append("[\r\n");
                for (int i = 0; i < list.Count; i++)
                {
                    sb.Append(pad).Append("  ");
                    WriteValue(sb, list[i], indent + 1);
                    sb.Append(i + 1 < list.Count ? ",\r\n" : "\r\n");
                }
                sb.Append(pad).Append(']');
                return;
            }
            if (value == null)
                sb.Append("null");
            else if (value is bool)
                sb.Append((bool)value ? "true" : "false");
            else if (value is BmsJsonNumber)
                sb.Append(((BmsJsonNumber)value).Raw);
            else if (value is int)
                sb.Append(((int)value).ToString(CultureInfo.InvariantCulture));
            else
                WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
