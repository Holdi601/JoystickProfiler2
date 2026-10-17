using System;
using System.Collections.Generic;
using System.Text;

namespace JoyPro.Engine.Games
{
    // An Unreal ini struct value "(Key=Value,Key=(...),Key="text")" parsed into an ordered field list.
    // Scalar values keep their raw text, so ToString() reproduces unchanged structs exactly.
    internal sealed class UeIniStruct
    {
        public List<UeIniField> Fields { get; private set; }

        public UeIniStruct()
        {
            Fields = new List<UeIniField>();
        }

        public static bool TryParse(string text, out UeIniStruct result)
        {
            result = null;
            int position = 0;
            string value = text ?? string.Empty;
            if (!TryParseStruct(value, ref position, out result))
                return false;
            return position == value.Length;
        }

        private static bool TryParseStruct(string text, ref int position, out UeIniStruct result)
        {
            result = new UeIniStruct();
            if (position >= text.Length || text[position] != '(')
                return false;
            position++;

            if (position < text.Length && text[position] == ')')
            {
                position++;
                return true;
            }

            while (position < text.Length)
            {
                int equals = text.IndexOf('=', position);
                if (equals < 0)
                    return false;

                UeIniField field = new UeIniField { Key = text.Substring(position, equals - position) };
                position = equals + 1;

                if (position < text.Length && text[position] == '(')
                {
                    UeIniStruct nested;
                    if (!TryParseStruct(text, ref position, out nested))
                        return false;
                    field.Struct = nested;
                }
                else
                {
                    int start = position;
                    bool quoted = false;
                    while (position < text.Length)
                    {
                        char c = text[position];
                        if (c == '"')
                            quoted = !quoted;
                        else if (!quoted && (c == ',' || c == ')'))
                            break;
                        position++;
                    }
                    if (quoted)
                        return false;
                    field.Raw = text.Substring(start, position - start);
                }

                result.Fields.Add(field);
                if (position >= text.Length)
                    return false;
                if (text[position] == ',')
                {
                    position++;
                    continue;
                }
                if (text[position] == ')')
                {
                    position++;
                    return true;
                }
                return false;
            }

            return false;
        }

        public int IndexOf(string key)
        {
            return Fields.FindIndex(f => string.Equals(f.Key, key, StringComparison.Ordinal));
        }

        public UeIniField Get(string key)
        {
            int index = IndexOf(key);
            return index < 0 ? null : Fields[index];
        }

        // The field right after an anchor field; used for obfuscated keys whose name must not be hardcoded.
        public UeIniField After(string anchorKey)
        {
            int index = IndexOf(anchorKey);
            return index < 0 || index + 1 >= Fields.Count ? null : Fields[index + 1];
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('(');
            for (int i = 0; i < Fields.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(Fields[i].Key).Append('=');
                if (Fields[i].Struct != null)
                    sb.Append(Fields[i].Struct.ToString());
                else
                    sb.Append(Fields[i].Raw);
            }
            sb.Append(')');
            return sb.ToString();
        }
    }

    internal sealed class UeIniField
    {
        public string Key { get; set; }
        public string Raw { get; set; }
        public UeIniStruct Struct { get; set; }

        public bool IsScalar
        {
            get { return Struct == null; }
        }

        public string Text
        {
            get
            {
                string raw = Raw ?? string.Empty;
                return raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"' ? raw.Substring(1, raw.Length - 2) : raw;
            }
        }

        public void SetQuoted(string value)
        {
            Raw = "\"" + (value ?? string.Empty) + "\"";
        }

        public bool TryGetInt(out int value)
        {
            return int.TryParse(Raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        public bool TryGetBool(out bool value)
        {
            value = false;
            if (string.Equals(Raw, "True", StringComparison.OrdinalIgnoreCase))
                value = true;
            else if (!string.Equals(Raw, "False", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
    }
}
