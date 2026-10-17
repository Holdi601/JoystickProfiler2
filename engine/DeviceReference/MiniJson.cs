#if NET
#nullable disable
#endif
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace JoyPro.DeviceReference
{
    // A small JSON reader and writer for the device catalog (DB\Devices\*.json) and the user's device definitions.
    // Shared by the engine (.NET Framework) and the GUI (.NET 9, linked file), so both read the catalog the same way.
    // Objects become Dictionary<string, object> (ordinal, case-sensitive keys), arrays List<object>, numbers double,
    // plus string, bool and null. Plain C# 7.3.
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            if (json == null)
                throw new ArgumentNullException("json");
            Reader reader = new Reader(json);
            reader.SkipWhite();
            object value = reader.ReadValue();
            reader.SkipWhite();
            if (!reader.End)
                throw reader.Error("unexpected text after the JSON value");
            return value;
        }

        public static string Write(object value, bool indented)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, value, indented, 0);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value, bool indented, int depth)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }
            string text = value as string;
            if (text != null)
            {
                WriteString(sb, text);
                return;
            }
            if (value is bool)
            {
                sb.Append((bool)value ? "true" : "false");
                return;
            }
            if (value is double || value is float || value is decimal)
            {
                double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d))
                    d = 0;
                sb.Append(Math.Round(d, 5).ToString("0.#####", CultureInfo.InvariantCulture));
                return;
            }
            if (value is int || value is long || value is short || value is byte)
            {
                sb.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            }
            IDictionary<string, object> map = value as IDictionary<string, object>;
            if (map != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> pair in map)
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    NewLine(sb, indented, depth + 1);
                    WriteString(sb, pair.Key);
                    sb.Append(indented ? ": " : ":");
                    WriteValue(sb, pair.Value, indented, depth + 1);
                }
                if (!first)
                    NewLine(sb, indented, depth);
                sb.Append('}');
                return;
            }
            IEnumerable list = value as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                bool simple = true;
                foreach (object item in list)
                {
                    if (item is IDictionary<string, object> || (item is IEnumerable && !(item is string)))
                        simple = false;
                }
                foreach (object item in list)
                {
                    if (!first)
                        sb.Append(simple && indented ? ", " : ",");
                    first = false;
                    if (!simple)
                        NewLine(sb, indented, depth + 1);
                    WriteValue(sb, item, indented, depth + 1);
                }
                if (!first && !simple)
                    NewLine(sb, indented, depth);
                sb.Append(']');
                return;
            }
            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void NewLine(StringBuilder sb, bool indented, int depth)
        {
            if (!indented)
                return;
            sb.Append(Environment.NewLine);
            sb.Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder sb, string text)
        {
            sb.Append('"');
            foreach (char c in text)
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

        private sealed class Reader
        {
            private readonly string text;
            private int pos;

            public Reader(string text)
            {
                this.text = text;
                // A UTF-8 byte order mark read as text.
                if (text.Length > 0 && text[0] == (char)0xFEFF)
                    pos = 1;
            }

            public bool End { get { return pos >= text.Length; } }

            public FormatException Error(string message)
            {
                int line = 1;
                for (int i = 0; i < pos && i < text.Length; i++)
                {
                    if (text[i] == '\n')
                        line++;
                }
                return new FormatException("JSON: " + message + " (line " + line.ToString(CultureInfo.InvariantCulture) + ")");
            }

            public void SkipWhite()
            {
                while (pos < text.Length && char.IsWhiteSpace(text[pos]))
                    pos++;
            }

            public object ReadValue()
            {
                if (End)
                    throw Error("unexpected end");
                char c = text[pos];
                if (c == '{')
                    return ReadObject();
                if (c == '[')
                    return ReadArray();
                if (c == '"')
                    return ReadString();
                if (c == 't' && Match("true"))
                    return true;
                if (c == 'f' && Match("false"))
                    return false;
                if (c == 'n' && Match("null"))
                    return null;
                if (c == '-' || char.IsDigit(c))
                    return ReadNumber();
                throw Error("unexpected character '" + c + "'");
            }

            private bool Match(string word)
            {
                if (string.CompareOrdinal(text, pos, word, 0, word.Length) != 0)
                    return false;
                pos += word.Length;
                return true;
            }

            private Dictionary<string, object> ReadObject()
            {
                Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
                pos++;
                SkipWhite();
                if (!End && text[pos] == '}')
                {
                    pos++;
                    return map;
                }
                while (true)
                {
                    SkipWhite();
                    if (End || text[pos] != '"')
                        throw Error("expected a property name");
                    string key = ReadString();
                    SkipWhite();
                    if (End || text[pos] != ':')
                        throw Error("expected ':'");
                    pos++;
                    SkipWhite();
                    map[key] = ReadValue();
                    SkipWhite();
                    if (End)
                        throw Error("unexpected end in an object");
                    if (text[pos] == ',')
                    {
                        pos++;
                        continue;
                    }
                    if (text[pos] == '}')
                    {
                        pos++;
                        return map;
                    }
                    throw Error("expected ',' or '}'");
                }
            }

            private List<object> ReadArray()
            {
                List<object> list = new List<object>();
                pos++;
                SkipWhite();
                if (!End && text[pos] == ']')
                {
                    pos++;
                    return list;
                }
                while (true)
                {
                    SkipWhite();
                    list.Add(ReadValue());
                    SkipWhite();
                    if (End)
                        throw Error("unexpected end in an array");
                    if (text[pos] == ',')
                    {
                        pos++;
                        continue;
                    }
                    if (text[pos] == ']')
                    {
                        pos++;
                        return list;
                    }
                    throw Error("expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                pos++;
                StringBuilder sb = new StringBuilder();
                while (true)
                {
                    if (End)
                        throw Error("unterminated string");
                    char c = text[pos++];
                    if (c == '"')
                        return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (End)
                        throw Error("unterminated escape");
                    char e = text[pos++];
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
                            if (pos + 4 > text.Length)
                                throw Error("short \\u escape");
                            sb.Append((char)int.Parse(text.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            pos += 4;
                            break;
                        default:
                            throw Error("unknown escape \\" + e);
                    }
                }
            }

            private double ReadNumber()
            {
                int start = pos;
                while (pos < text.Length && "+-0123456789.eE".IndexOf(text[pos]) >= 0)
                    pos++;
                double value;
                if (!double.TryParse(text.Substring(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    throw Error("bad number");
                return value;
            }
        }
    }
}
