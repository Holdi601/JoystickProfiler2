using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace JoyPro.Engine.Games
{
    // Reads what the Arma games define about their controls from the installed game, read only, without a first start
    // (docs/games/arma3.md, arma2.md "Catalog"):
    //   - the action names and their default binds: the game config in bin.pbo (config.bin, binarized "raP"):
    //     Arma 3 class CfgDefaultKeysPresets >> Default >> Mappings, Arma 2 / OA class CfgDefaultKeysMapping. Every entry
    //     is an action the profile names as key<Action>[]={...};
    //   - the menu names: the English column of languagecore's stringtable.xml, key "str_usract_" + the action name in
    //     snake case (heliCyclicLeft -> str_usract_heli_cyclic_left).
    // PBO: a list of file headers (name, packing method, sizes) and then the files one after the other. Only stored
    // (unpacked) files are read; bin.pbo and languagecore keep both stored.
    internal static class ArmaGameData
    {
        private const uint VersionEntry = 0x56657273; // "Vers": the product header entry

        // The bytes of one stored file of a PBO, null when the PBO or the file is missing or packed.
        public static byte[] ReadPboFile(string pboPath, string fileName, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(pboPath) || !File.Exists(pboPath))
                return null;
            try
            {
                using (FileStream stream = new FileStream(pboPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.ASCII))
                {
                    List<Tuple<string, uint, uint>> entries = new List<Tuple<string, uint, uint>>();
                    while (stream.Position < stream.Length)
                    {
                        string name = ReadZ(reader);
                        uint method = reader.ReadUInt32();
                        reader.ReadUInt32(); // original size
                        reader.ReadUInt32(); // reserved
                        reader.ReadUInt32(); // time stamp
                        uint size = reader.ReadUInt32();
                        if (name.Length == 0 && method == VersionEntry)
                        {
                            while (ReadZ(reader).Length > 0)
                                ReadZ(reader);
                            continue;
                        }
                        if (name.Length == 0)
                            break;
                        entries.Add(Tuple.Create(name, method, size));
                        if (entries.Count > 200000)
                            return null;
                    }
                    long offset = stream.Position;
                    foreach (Tuple<string, uint, uint> entry in entries)
                    {
                        if (string.Equals(entry.Item1, fileName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (entry.Item2 != 0)
                            {
                                warnings?.Add(fileName + " in " + pboPath + " is packed; JoyPro reads only stored files.");
                                return null;
                            }
                            if (offset + entry.Item3 > stream.Length)
                                return null;
                            stream.Position = offset;
                            return reader.ReadBytes((int)entry.Item3);
                        }
                        offset += entry.Item3;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is EndOfStreamException)
            {
                warnings?.Add("Could not read " + pboPath + ": " + ex.Message);
            }
            return null;
        }

        private static string ReadZ(BinaryReader reader)
        {
            StringBuilder text = new StringBuilder();
            while (true)
            {
                byte b = reader.ReadByte();
                if (b == 0)
                    return text.ToString();
                text.Append((char)b);
                if (text.Length > 4096)
                    throw new EndOfStreamException("unterminated name");
            }
        }

        // The entry names (arrays and values) of one class of a binarized config, by path ("CfgDefaultKeysMapping",
        // "CfgDefaultKeysPresets/Default/Mappings"); null when the config or the class is missing.
        public static List<string> ConfigEntryNames(byte[] config, string classPath)
        {
            if (config == null || config.Length < 16 || config[0] != 0 || config[1] != (byte)'r' || config[2] != (byte)'a' || config[3] != (byte)'P')
                return null;
            try
            {
                int body = 16;
                foreach (string part in classPath.Split('/'))
                {
                    int next = -1;
                    foreach (RapEntry entry in ReadClass(config, body))
                    {
                        if (entry.Type == 0 && string.Equals(entry.Name, part, StringComparison.OrdinalIgnoreCase))
                        {
                            next = entry.Offset;
                            break;
                        }
                    }
                    if (next < 0 || next >= config.Length)
                        return null;
                    body = next;
                }
                return ReadClass(config, body).Where(e => e.Type == 1 || e.Type == 2 || e.Type == 5).Select(e => e.Name).ToList();
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentException || ex is FormatException)
            {
                return null;
            }
        }

        private struct RapEntry
        {
            public int Type;
            public string Name;
            public int Offset;
        }

        private static List<RapEntry> ReadClass(byte[] b, int p)
        {
            List<RapEntry> entries = new List<RapEntry>();
            Z(b, ref p); // parent class
            int count = CompressedInt(b, ref p);
            for (int i = 0; i < count; i++)
            {
                int type = b[p++];
                RapEntry entry = new RapEntry { Type = type };
                switch (type)
                {
                    case 0: // class: name, offset of its body
                        entry.Name = Z(b, ref p);
                        entry.Offset = BitConverter.ToInt32(b, p);
                        p += 4;
                        break;
                    case 1: // value: sub type, name, value
                    {
                        int sub = b[p++];
                        entry.Name = Z(b, ref p);
                        if (sub == 0 || sub == 4)
                            Z(b, ref p);
                        else if (sub == 1 || sub == 2)
                            p += 4;
                        else if (sub == 6)
                            p += 8;
                        else
                            throw new FormatException("value type " + sub.ToString(CultureInfo.InvariantCulture));
                        break;
                    }
                    case 2: // array
                        entry.Name = Z(b, ref p);
                        SkipArray(b, ref p);
                        break;
                    case 3: // extern class
                    case 4: // delete
                        entry.Name = Z(b, ref p);
                        break;
                    case 5: // array with a flag (+= / -=)
                        p += 4;
                        entry.Name = Z(b, ref p);
                        SkipArray(b, ref p);
                        break;
                    default:
                        throw new FormatException("entry type " + type.ToString(CultureInfo.InvariantCulture));
                }
                entries.Add(entry);
            }
            return entries;
        }

        private static void SkipArray(byte[] b, ref int p)
        {
            int count = CompressedInt(b, ref p);
            for (int i = 0; i < count; i++)
            {
                int type = b[p++];
                if (type == 0 || type == 4)
                    Z(b, ref p);
                else if (type == 1 || type == 2)
                    p += 4;
                else if (type == 3)
                    SkipArray(b, ref p);
                else if (type == 6)
                    p += 8;
                else
                    throw new FormatException("array element type " + type.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static int CompressedInt(byte[] b, ref int p)
        {
            int value = 0;
            int shift = 0;
            while (true)
            {
                byte c = b[p++];
                value |= (c & 0x7F) << shift;
                if ((c & 0x80) == 0)
                    return value;
                shift += 7;
                if (shift > 28)
                    throw new FormatException("compressed int");
            }
        }

        private static string Z(byte[] b, ref int p)
        {
            int end = Array.IndexOf(b, (byte)0, p);
            if (end < 0)
                throw new FormatException("unterminated string");
            string text = Encoding.UTF8.GetString(b, p, end - p);
            p = end + 1;
            return text;
        }

        // English names of the user actions: stringtable key (lower case) -> text, only the str_usract_ keys.
        public static Dictionary<string, string> ActionTitles(byte[] stringtable)
        {
            Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (stringtable == null)
                return titles;
            try
            {
                using (MemoryStream stream = new MemoryStream(stringtable))
                using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
                {
                    string key = null;
                    bool advanced = false;
                    while (advanced || reader.Read())
                    {
                        advanced = false;
                        if (reader.NodeType != XmlNodeType.Element)
                            continue;
                        if (reader.Name == "Key")
                        {
                            string id = reader.GetAttribute("ID");
                            key = id != null && id.StartsWith("str_usract_", StringComparison.OrdinalIgnoreCase) ? id : null;
                        }
                        else if (key != null && (reader.Name == "English" || reader.Name == "Original"))
                        {
                            bool english = reader.Name == "English";
                            // ReadElementContentAsString moves to the node after the element: do not Read() past it.
                            string text = reader.ReadElementContentAsString().Trim();
                            advanced = true;
                            if (text.Length > 0 && (english || !titles.ContainsKey(key)))
                                titles[key] = text;
                        }
                    }
                }
            }
            catch (XmlException)
            {
            }
            return titles;
        }

        // The stringtable key of an action: "str_usract_" + snake case (heliCyclicLeft -> str_usract_heli_cyclic_left,
        // VTOLVectoring -> str_usract_vtol_vectoring).
        public static string TitleKey(string action)
        {
            string snake = Regex.Replace(action ?? string.Empty, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", "_").ToLowerInvariant();
            return "str_usract_" + snake;
        }

        public static string Title(Dictionary<string, string> titles, string action)
        {
            string text;
            if (titles != null && titles.TryGetValue(TitleKey(action), out text) && text.Trim().Length > 0)
                return text.Trim().Replace('\t', ' ');
            return null;
        }
    }
}
