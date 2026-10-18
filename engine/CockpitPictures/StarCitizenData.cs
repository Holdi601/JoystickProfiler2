#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace JoyPro.CockpitPictures
{
    // An XML element of Star Citizen's files: binary CryXmlB or text XML.
    internal sealed class CryXmlNode
    {
        public string Tag = string.Empty;
        public string Content = string.Empty;
        public Dictionary<string, string> Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<CryXmlNode> Children = new List<CryXmlNode>();

        public string Get(string name)
        {
            string v;
            return Attributes.TryGetValue(name, out v) ? v : string.Empty;
        }

        public CryXmlNode Child(string tag)
        {
            return Children.FirstOrDefault(c => string.Equals(c.Tag, tag, StringComparison.OrdinalIgnoreCase));
        }

        public IEnumerable<CryXmlNode> Descendants(string tag)
        {
            Stack<CryXmlNode> todo = new Stack<CryXmlNode>();
            todo.Push(this);
            while (todo.Count > 0)
            {
                CryXmlNode n = todo.Pop();
                if (n != this && string.Equals(n.Tag, tag, StringComparison.OrdinalIgnoreCase))
                    yield return n;
                for (int i = n.Children.Count - 1; i >= 0; i--)
                    todo.Push(n.Children[i]);
            }
        }
    }

    // CryEngine's binary XML ("CryXmlB\0", then: size, the node table offset and count, the attribute table offset and
    // count, the child table offset and count, the string data offset and size; nodes of 28 bytes: tag, content, attribute
    // count, child count, parent, first attribute, first child, reserved; attributes: key, value string offsets).
    internal static class CryXml
    {
        public static bool IsBinary(byte[] d, int at = 0)
        {
            return d != null && d.Length >= at + 44 && Encoding.ASCII.GetString(d, at, 7) == "CryXmlB";
        }

        public static CryXmlNode Parse(byte[] d, int at = 0)
        {
            if (d == null)
                return null;
            if (!IsBinary(d, at))
                return ParseText(d, at);
            int noff = at + BitConverter.ToInt32(d, at + 12), ncnt = BitConverter.ToInt32(d, at + 16);
            int aoff = at + BitConverter.ToInt32(d, at + 20), acnt = BitConverter.ToInt32(d, at + 24);
            int coff = at + BitConverter.ToInt32(d, at + 28), ccnt = BitConverter.ToInt32(d, at + 32);
            int soff = at + BitConverter.ToInt32(d, at + 36), ssize = BitConverter.ToInt32(d, at + 40);
            if (ncnt <= 0 || noff + 28L * ncnt > d.Length || aoff + 8L * acnt > d.Length || coff + 4L * ccnt > d.Length || soff + (long)ssize > d.Length)
                throw new InvalidDataException("bad CryXmlB");
            Dictionary<int, string> strings = new Dictionary<int, string>();
            Func<int, string> s = o =>
            {
                string v;
                if (strings.TryGetValue(o, out v))
                    return v;
                int start = soff + o, e = start;
                if (o < 0 || start >= soff + ssize)
                    return string.Empty;
                while (e < soff + ssize && d[e] != 0)
                    e++;
                v = Encoding.UTF8.GetString(d, start, e - start);
                strings[o] = v;
                return v;
            };
            CryXmlNode[] nodes = new CryXmlNode[ncnt];
            int[] parents = new int[ncnt];
            for (int i = 0; i < ncnt; i++)
            {
                int p = noff + 28 * i;
                CryXmlNode n = new CryXmlNode { Tag = s(BitConverter.ToInt32(d, p)), Content = s(BitConverter.ToInt32(d, p + 4)) };
                int na = BitConverter.ToUInt16(d, p + 8);
                parents[i] = BitConverter.ToInt32(d, p + 12);
                int fa = BitConverter.ToInt32(d, p + 16);
                for (int k = 0; k < na && fa + k < acnt; k++)
                {
                    int ap = aoff + 8 * (fa + k);
                    n.Attributes[s(BitConverter.ToInt32(d, ap))] = s(BitConverter.ToInt32(d, ap + 4));
                }
                nodes[i] = n;
            }
            // Children in the child table's order.
            for (int i = 0; i < ncnt; i++)
            {
                int p = noff + 28 * i;
                int nc = BitConverter.ToUInt16(d, p + 10), fc = BitConverter.ToInt32(d, p + 20);
                for (int k = 0; k < nc && fc + k < ccnt; k++)
                {
                    int c = BitConverter.ToInt32(d, coff + 4 * (fc + k));
                    if (c > 0 && c < ncnt && c != i)
                        nodes[i].Children.Add(nodes[c]);
                }
            }
            return nodes[0];
        }

        private static CryXmlNode ParseText(byte[] d, int at)
        {
            string text = Encoding.UTF8.GetString(d, at, d.Length - at).TrimStart('﻿', ' ', '\r', '\n', '\t');
            if (!text.StartsWith("<", StringComparison.Ordinal))
                return null;
            XElement root = XDocument.Parse(text).Root;
            return root == null ? null : From(root);
        }

        private static CryXmlNode From(XElement e)
        {
            CryXmlNode n = new CryXmlNode { Tag = e.Name.LocalName };
            foreach (XAttribute a in e.Attributes())
                n.Attributes[a.Name.LocalName] = a.Value;
            foreach (XElement c in e.Elements())
                n.Children.Add(From(c));
            if (!e.HasElements)
                n.Content = e.Value;
            return n;
        }

        // "1,0.5,0" -> numbers.
        public static double[] Numbers(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return new double[0];
            return s.Split(',').Select(x =>
            {
                double v;
                return double.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
            }).ToArray();
        }
    }

    // Star Citizen's DataForge database (Data\Game2.dcb): the definitions of the game's entities (ships, items, seats,
    // their geometry and default loadouts). Version 6 and newer: names in the second string table; version 8 records are
    // 36 bytes. Records are read into dictionaries: property name -> value (bool, numbers, string, nested dictionary for
    // a class or a strong pointer, list for an array; "_type" holds the struct name; references -> their GUID text).
    internal sealed class DataForge
    {
        private byte[] d;
        public int Version;
        private int structCount, propCount, enumCount, mapCount, recordCount;
        private int[] sName, sParent, sPropCount, sFirstProp;
        private int[] pName, pStruct, pType, pConv;
        private int[] rName, rFile, rStruct, rInstance;
        private string[] rGuid;
        private long aI8, aI16, aI32, aI64, aU8, aU16, aU32, aU64, aBool, aF32, aF64, aGuid, aStr, aLoc, aEnum, aStrong, aWeak, aRef;
        private long text, blob;
        private int[] sSize;
        private List<int>[] sProps;
        private long[] instanceStart;
        private int[] instanceCount;
        private Dictionary<string, int> byName;

        public int RecordCount { get { return recordCount; } }

        public static DataForge Parse(byte[] data)
        {
            DataForge f = new DataForge { d = data };
            f.Load();
            return f;
        }

        private int I(long at) { return BitConverter.ToInt32(d, (int)at); }

        private void Load()
        {
            if (d.Length < 120)
                throw new InvalidDataException("DataForge: too short");
            Version = I(4);
            if (Version < 6)
                throw new InvalidDataException("DataForge version " + Version + " is not supported");
            int[] c = new int[26];
            for (int i = 0; i < 26; i++)
                c[i] = I(16 + 4 * i);
            structCount = c[0];
            propCount = c[1];
            enumCount = c[2];
            mapCount = c[3];
            recordCount = c[4];
            // Record size: 36 bytes from version 8 (a further field), else 32; the one that ends the data at the file's end.
            foreach (int recordSize in Version >= 8 ? new[] { 36, 32 } : new[] { 32, 36 })
            {
                if (TryLayout(c, recordSize))
                    return;
            }
            throw new InvalidDataException("DataForge version " + Version + ": the layout is not understood");
        }

        private bool TryLayout(int[] c, int recordSize)
        {
            long p = 120;
            long structs = p;
            p += 16L * structCount;
            long props = p;
            p += 12L * propCount;
            p += 8L * enumCount;
            long maps = p;
            p += 8L * mapCount;
            long records = p;
            p += (long)recordSize * recordCount;
            Func<int, int, long> arr = (n, size) => { long a = p; p += (long)n * size; return a; };
            aI8 = arr(c[6], 1); aI16 = arr(c[7], 2); aI32 = arr(c[8], 4); aI64 = arr(c[9], 8);
            aU8 = arr(c[10], 1); aU16 = arr(c[11], 2); aU32 = arr(c[12], 4); aU64 = arr(c[13], 8);
            aBool = arr(c[5], 1); aF32 = arr(c[14], 4); aF64 = arr(c[15], 8); aGuid = arr(c[16], 16);
            aStr = arr(c[17], 4); aLoc = arr(c[18], 4); aEnum = arr(c[19], 4); aStrong = arr(c[20], 8);
            aWeak = arr(c[21], 8); aRef = arr(c[22], 20);
            arr(c[23], 4);
            text = p;
            p += (uint)c[24];
            blob = p;
            p += (uint)c[25];
            long data = p;
            if (data > d.Length)
                return false;
            sName = new int[structCount]; sParent = new int[structCount]; sPropCount = new int[structCount]; sFirstProp = new int[structCount];
            for (int i = 0; i < structCount; i++)
            {
                long at = structs + 16L * i;
                sName[i] = I(at);
                sParent[i] = I(at + 4);
                sPropCount[i] = BitConverter.ToUInt16(d, (int)at + 8);
                sFirstProp[i] = BitConverter.ToUInt16(d, (int)at + 10);
            }
            pName = new int[propCount]; pStruct = new int[propCount]; pType = new int[propCount]; pConv = new int[propCount];
            for (int i = 0; i < propCount; i++)
            {
                long at = props + 12L * i;
                pName[i] = I(at);
                pStruct[i] = BitConverter.ToUInt16(d, (int)at + 4);
                pType[i] = BitConverter.ToUInt16(d, (int)at + 6);
                pConv[i] = BitConverter.ToUInt16(d, (int)at + 8);
            }
            sProps = new List<int>[structCount];
            sSize = new int[structCount];
            for (int i = 0; i < structCount; i++)
                sSize[i] = -1;
            for (int i = 0; i < structCount; i++)
            {
                if (Size(i, 0) < 0)
                    return false;
            }
            instanceStart = new long[structCount];
            instanceCount = new int[structCount];
            long q = data;
            for (int i = 0; i < mapCount; i++)
            {
                int cnt = I(maps + 8L * i), si = I(maps + 8L * i + 4);
                if (si < 0 || si >= structCount)
                    return false;
                instanceStart[si] = q;
                instanceCount[si] = cnt;
                q += (long)cnt * sSize[si];
            }
            if (q != d.Length)
                return false;
            rName = new int[recordCount]; rFile = new int[recordCount]; rStruct = new int[recordCount]; rInstance = new int[recordCount];
            rGuid = new string[recordCount];
            for (int i = 0; i < recordCount; i++)
            {
                long at = records + (long)recordSize * i;
                rName[i] = I(at);
                rFile[i] = I(at + 4);
                int extra = recordSize - 32;
                rStruct[i] = I(at + 8 + extra);
                rGuid[i] = null;
                rInstance[i] = BitConverter.ToUInt16(d, (int)(at + 28 + extra));
            }
            recordGuidAt = records;
            recordStride = recordSize;
            return true;
        }

        private long recordGuidAt;
        private int recordStride;

        private List<int> Props(int si)
        {
            if (sProps[si] != null)
                return sProps[si];
            List<int> chain = new List<int>();
            int s = si, guard = 0;
            while (s >= 0 && s < structCount && guard++ < 64)
            {
                chain.Add(s);
                s = sParent[s];
            }
            List<int> props = new List<int>();
            for (int k = chain.Count - 1; k >= 0; k--)
            {
                for (int i = 0; i < sPropCount[chain[k]]; i++)
                    props.Add(sFirstProp[chain[k]] + i);
            }
            sProps[si] = props;
            return props;
        }

        private int Size(int si, int depth)
        {
            if (sSize[si] >= 0)
                return sSize[si];
            if (depth > 64)
                return -1;
            int n = 0;
            foreach (int pi in Props(si))
            {
                if (pi < 0 || pi >= propCount)
                    return -1;
                if (pConv[pi] != 0)
                {
                    n += 8;
                    continue;
                }
                int s = InlineSize(pType[pi], pStruct[pi], depth);
                if (s < 0)
                    return -1;
                n += s;
            }
            sSize[si] = n;
            return n;
        }

        private int InlineSize(int type, int sidx, int depth)
        {
            switch (type)
            {
                case 1: case 2: case 6: return 1;
                case 3: case 7: return 2;
                case 4: case 8: case 10: case 11: case 13: case 15: return 4;
                case 5: case 9: case 12: return 8;
                case 14: return 16;
                case 0x10: return sidx < structCount ? Size(sidx, depth + 1) : -1;
                case 0x110: case 0x210: return 8;
                case 0x310: return 20;
                default: return -1;
            }
        }

        private string CString(long table, int offset)
        {
            long start = table + offset;
            if (offset < 0 || start >= d.Length)
                return string.Empty;
            long e = start;
            while (e < d.Length && d[e] != 0)
                e++;
            return Encoding.UTF8.GetString(d, (int)start, (int)(e - start));
        }

        private string Text(int offset) { return CString(text, offset); }

        private readonly Dictionary<int, string> names = new Dictionary<int, string>();

        private string Name(int offset)
        {
            string s;
            if (!names.TryGetValue(offset, out s))
            {
                s = CString(blob, offset);
                names[offset] = s;
            }
            return s;
        }

        public string StructName(int si) { return Name(sName[si]); }
        public string RecordName(int r) { return Name(rName[r]); }
        public string RecordFile(int r) { return Text(rFile[r]); }

        public string RecordGuid(int r)
        {
            if (rGuid[r] == null)
                rGuid[r] = Guid(recordGuidAt + (long)recordStride * r + 12 + (recordStride - 32));
            return rGuid[r];
        }

        private string Guid(long at)
        {
            StringBuilder sb = new StringBuilder(32);
            for (int k = 0; k < 16; k++)
                sb.Append(d[at + k].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // The record of a name ("EntityClassDefinition.AEGS_Gladius"), -1 if none.
        public int Find(string name)
        {
            if (byName == null)
            {
                byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < recordCount; i++)
                {
                    string n = RecordName(i);
                    if (!byName.ContainsKey(n))
                        byName[n] = i;
                }
            }
            int r;
            return byName.TryGetValue(name ?? string.Empty, out r) ? r : -1;
        }

        public IEnumerable<int> RecordsUnder(string filePrefix)
        {
            for (int i = 0; i < recordCount; i++)
            {
                if (RecordFile(i).StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                    yield return i;
            }
        }

        public Dictionary<string, object> Read(int record, int depth)
        {
            return Instance(rStruct[record], rInstance[record], depth);
        }

        private Dictionary<string, object> Instance(int si, int index, int depth)
        {
            if (si < 0 || si >= structCount || index < 0 || index >= instanceCount[si])
                return null;
            return Struct(si, instanceStart[si] + (long)index * sSize[si], depth);
        }

        private Dictionary<string, object> Struct(int si, long at, int depth)
        {
            Dictionary<string, object> o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["_type"] = StructName(si);
            foreach (int pi in Props(si))
            {
                string name = Name(pName[pi]);
                int type = pType[pi], sidx = pStruct[pi];
                if (pConv[pi] != 0)
                {
                    int cnt = I(at), first = I(at + 4);
                    at += 8;
                    List<object> list = new List<object>();
                    for (int k = 0; k < cnt && k < 4000; k++)
                        list.Add(ArrayValue(type, sidx, first + k, depth));
                    o[name] = list;
                    continue;
                }
                o[name] = Value(type, sidx, ref at, depth);
            }
            return o;
        }

        private object Value(int type, int sidx, ref long at, int depth)
        {
            int p = (int)at;
            switch (type)
            {
                case 1: at += 1; return d[p] != 0;
                case 2: at += 1; return (double)(sbyte)d[p];
                case 3: at += 2; return (double)BitConverter.ToInt16(d, p);
                case 4: at += 4; return (double)BitConverter.ToInt32(d, p);
                case 5: at += 8; return (double)BitConverter.ToInt64(d, p);
                case 6: at += 1; return (double)d[p];
                case 7: at += 2; return (double)BitConverter.ToUInt16(d, p);
                case 8: at += 4; return (double)BitConverter.ToUInt32(d, p);
                case 9: at += 8; return (double)BitConverter.ToUInt64(d, p);
                case 10: at += 4; return Text(BitConverter.ToInt32(d, p));
                case 11: at += 4; return (double)BitConverter.ToSingle(d, p);
                case 12: at += 8; return BitConverter.ToDouble(d, p);
                case 13: at += 4; return Text(BitConverter.ToInt32(d, p));
                case 14: at += 16; return Guid(p);
                case 15: at += 4; return Text(BitConverter.ToInt32(d, p));
                case 0x10:
                    at += sSize[sidx];
                    return depth > 0 ? Struct(sidx, p, depth - 1) : null;
                case 0x110:
                    {
                        at += 8;
                        int s = BitConverter.ToInt32(d, p), i = BitConverter.ToInt32(d, p + 4);
                        return s < 0 || depth <= 0 ? null : Instance(s, i, depth - 1);
                    }
                case 0x210:
                    at += 8;
                    return null;
                case 0x310:
                    at += 20;
                    return Guid(p + 4);
                default:
                    throw new InvalidDataException("DataForge: unknown type " + type);
            }
        }

        private object ArrayValue(int type, int sidx, int i, int depth)
        {
            switch (type)
            {
                case 1: return d[aBool + i] != 0;
                case 2: return (double)(sbyte)d[aI8 + i];
                case 3: return (double)BitConverter.ToInt16(d, (int)(aI16 + 2L * i));
                case 4: return (double)BitConverter.ToInt32(d, (int)(aI32 + 4L * i));
                case 5: return (double)BitConverter.ToInt64(d, (int)(aI64 + 8L * i));
                case 6: return (double)d[aU8 + i];
                case 7: return (double)BitConverter.ToUInt16(d, (int)(aU16 + 2L * i));
                case 8: return (double)BitConverter.ToUInt32(d, (int)(aU32 + 4L * i));
                case 9: return (double)BitConverter.ToUInt64(d, (int)(aU64 + 8L * i));
                case 10: return Text(BitConverter.ToInt32(d, (int)(aStr + 4L * i)));
                case 11: return (double)BitConverter.ToSingle(d, (int)(aF32 + 4L * i));
                case 12: return BitConverter.ToDouble(d, (int)(aF64 + 8L * i));
                case 13: return Text(BitConverter.ToInt32(d, (int)(aLoc + 4L * i)));
                case 14: return Guid(aGuid + 16L * i);
                case 15: return Text(BitConverter.ToInt32(d, (int)(aEnum + 4L * i)));
                case 0x10: return depth > 0 ? Instance(sidx, i, depth - 1) : null;
                case 0x110:
                    {
                        int s = BitConverter.ToInt32(d, (int)(aStrong + 8L * i)), ii = BitConverter.ToInt32(d, (int)(aStrong + 8L * i + 4));
                        return s < 0 || depth <= 0 ? null : Instance(s, ii, depth - 1);
                    }
                case 0x210: return null;
                case 0x310: return Guid(aRef + 20L * i + 4);
                default: throw new InvalidDataException("DataForge: unknown type " + type);
            }
        }

        // ------------------------------------------------------------------ helpers on the read values

        public static Dictionary<string, object> Obj(object o, string key)
        {
            Dictionary<string, object> m = o as Dictionary<string, object>;
            object v;
            return m != null && m.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        public static List<object> List(object o, string key)
        {
            Dictionary<string, object> m = o as Dictionary<string, object>;
            object v;
            return m != null && m.TryGetValue(key, out v) && v is List<object> ? (List<object>)v : new List<object>();
        }

        public static string Str(object o, string key)
        {
            Dictionary<string, object> m = o as Dictionary<string, object>;
            object v;
            return m != null && m.TryGetValue(key, out v) && v is string ? (string)v : string.Empty;
        }

        public static double Num(object o, string key, double fallback = 0)
        {
            Dictionary<string, object> m = o as Dictionary<string, object>;
            object v;
            return m != null && m.TryGetValue(key, out v) && v is double ? (double)v : fallback;
        }

        public static string TypeOf(object o)
        {
            return Str(o, "_type");
        }

        // The first component of a type in an entity's Components (or StaticEntityClassData).
        public static Dictionary<string, object> Component(Dictionary<string, object> entity, string type)
        {
            foreach (string list in new[] { "Components", "StaticEntityClassData" })
            {
                foreach (object c in List(entity, list))
                {
                    if (TypeOf(c) == type)
                        return (Dictionary<string, object>)c;
                }
            }
            return null;
        }
    }

    // The game's English texts (Data\Localization\english\global.ini: key=value lines).
    internal sealed class StarCitizenTexts
    {
        private readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int Count { get { return map.Count; } }

        public static StarCitizenTexts Parse(byte[] ini)
        {
            StarCitizenTexts t = new StarCitizenTexts();
            if (ini == null)
                return t;
            string text = Encoding.UTF8.GetString(ini);
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = line.Substring(0, eq).Trim().TrimStart('﻿');
                // "key,P=value" variants (platform markers) count as the key.
                int comma = key.IndexOf(',');
                if (comma > 0)
                    key = key.Substring(0, comma);
                if (!t.map.ContainsKey(key))
                    t.map[key] = line.Substring(eq + 1).Trim();
            }
            return t;
        }

        // "@vehicle_NameAEGS_Gladius" -> "Aegis Gladius"; "" when unknown.
        public string Text(string key)
        {
            if (string.IsNullOrEmpty(key))
                return string.Empty;
            string k = key.StartsWith("LOC:", StringComparison.OrdinalIgnoreCase) ? key.Substring(4) : key;
            k = k.TrimStart('@');
            string v;
            if (!map.TryGetValue(k, out v) || v.IndexOf("UNINITIALIZED", StringComparison.OrdinalIgnoreCase) >= 0 || v.StartsWith("@", StringComparison.Ordinal))
                return string.Empty;
            return v.Replace("\\n", " ").Trim();
        }
    }
}
