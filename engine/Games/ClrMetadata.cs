using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace JoyPro.Engine.Games
{
    // A minimal, read-only reader of a .NET assembly's metadata and IL (ECMA-335): type names, their methods and, per
    // method, the calls it makes with the string literals loaded before each call. Used to read a mod's own command list
    // from the installed mod (VTOL VR's BYOJoystick, VtolVrMod); nothing is loaded or run. Only what that needs is
    // decoded: the PE sections, the metadata streams (#~, #Strings, #US, #Blob), the tables up to GenericParamConstraint
    // and the method bodies (tiny and fat headers).
    internal sealed class ClrMetadata
    {
        public sealed class TypeInfo
        {
            public string Name;
            public string Namespace;
            public string BaseName;
            public List<MethodInfo> Methods = new List<MethodInfo>();
        }

        public sealed class MethodInfo
        {
            public string Name;
            public uint Rva;
        }

        // One call in a method body: the called method's name and the string literals (ldstr) since the previous call
        // of a method the caller asked to track, in order.
        public sealed class CallSite
        {
            public string Method;
            public List<string> Strings = new List<string>();
        }

        private readonly byte[] data;
        private readonly List<int[]> sections = new List<int[]>(); // virtual address, virtual size, raw pointer, raw size
        private int stringsHeap, stringsSize, usHeap, usSize, blobHeap;
        private bool wideStrings, wideGuids, wideBlobs;
        private readonly int[] rows = new int[64];
        private readonly int[] tableOffsets = new int[64];
        private readonly int[] rowSizes = new int[64];

        public List<TypeInfo> Types { get; private set; }

        private ClrMetadata(byte[] data)
        {
            this.data = data;
            Types = new List<TypeInfo>();
        }

        public static ClrMetadata Load(string path, List<string> warnings)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                ClrMetadata metadata = new ClrMetadata(bytes);
                if (!metadata.Parse())
                {
                    warnings?.Add(path + " is not a .NET assembly JoyPro can read.");
                    return null;
                }
                return metadata;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is IndexOutOfRangeException || ex is ArgumentException)
            {
                warnings?.Add("Could not read " + path + ": " + ex.Message);
                return null;
            }
        }

        // ---- PE and metadata root ---------------------------------------------------------------------------------

        private bool Parse()
        {
            if (data.Length < 0x100 || data[0] != 'M' || data[1] != 'Z')
                return false;
            int pe = I32(0x3C);
            if (pe <= 0 || pe + 24 > data.Length || I32(pe) != 0x00004550)
                return false;
            int sectionCount = U16(pe + 6);
            int optionalSize = U16(pe + 20);
            int optional = pe + 24;
            int magic = U16(optional);
            int directories = optional + (magic == 0x20B ? 112 : 96);
            int cliRva = I32(directories + 14 * 8);
            int sectionTable = optional + optionalSize;
            for (int i = 0; i < sectionCount; i++)
            {
                int s = sectionTable + i * 40;
                sections.Add(new[] { I32(s + 12), I32(s + 8), I32(s + 20), I32(s + 16) });
            }
            int cli = Offset(cliRva);
            if (cli < 0)
                return false;
            int root = Offset(I32(cli + 8));
            if (root < 0 || I32(root) != 0x424A5342)
                return false;
            int versionLength = I32(root + 12);
            int p = root + 16 + versionLength;
            int streams = U16(p + 2);
            p += 4;
            int tables = -1;
            for (int i = 0; i < streams; i++)
            {
                int offset = I32(p);
                int size = I32(p + 4);
                p += 8;
                int start = p;
                while (data[p] != 0)
                    p++;
                string name = Encoding.ASCII.GetString(data, start, p - start);
                p = start + ((p - start + 4) & ~3);
                switch (name)
                {
                    case "#~":
                    case "#-":
                        tables = root + offset;
                        break;
                    case "#Strings":
                        stringsHeap = root + offset;
                        stringsSize = size;
                        break;
                    case "#US":
                        usHeap = root + offset;
                        usSize = size;
                        break;
                    case "#Blob":
                        blobHeap = root + offset;
                        break;
                }
            }
            if (tables < 0 || stringsHeap == 0)
                return false;
            ParseTables(tables);
            ReadTypes();
            return true;
        }

        private int Offset(int rva)
        {
            foreach (int[] s in sections)
            {
                if (rva >= s[0] && rva < s[0] + Math.Max(s[1], s[3]))
                    return rva - s[0] + s[2];
            }
            return -1;
        }

        // ---- tables -------------------------------------------------------------------------------------------------

        private const int Module = 0x00, TypeRef = 0x01, TypeDef = 0x02, FieldPtr = 0x03, Field = 0x04, MethodPtr = 0x05,
            MethodDef = 0x06, ParamPtr = 0x07, Param = 0x08, InterfaceImpl = 0x09, MemberRef = 0x0A, DeclSecurity = 0x0E,
            StandAloneSig = 0x11, Event = 0x14, EventPtr = 0x13, Property = 0x17, PropertyPtr = 0x16, ModuleRef = 0x1A,
            TypeSpec = 0x1B, Assembly = 0x20, AssemblyRef = 0x23, File_ = 0x26, ExportedType = 0x27, ManifestResource = 0x28,
            GenericParam = 0x2A, MethodSpec = 0x2B, GenericParamConstraint = 0x2C;

        // Column kinds: >0 fixed size in bytes; negative values below.
        private const int Str = -1, Guid_ = -2, Blob = -3;
        private const int CTypeDefOrRef = -10, CHasConstant = -11, CHasCustomAttribute = -12, CHasFieldMarshal = -13,
            CHasDeclSecurity = -14, CMemberRefParent = -15, CHasSemantics = -16, CMethodDefOrRef = -17, CMemberForwarded = -18,
            CImplementation = -19, CCustomAttributeType = -20, CResolutionScope = -21, CTypeOrMethodDef = -22;
        private const int IndexBase = -100; // -100 - table = simple index into that table

        private static int Idx(int table)
        {
            return IndexBase - table;
        }

        private static readonly int[][] Schema =
        {
            /* 00 Module */ new[] { 2, Str, Guid_, Guid_, Guid_ },
            /* 01 TypeRef */ new[] { CResolutionScope, Str, Str },
            /* 02 TypeDef */ new[] { 4, Str, Str, CTypeDefOrRef, Idx(Field), Idx(MethodDef) },
            /* 03 FieldPtr */ new[] { Idx(Field) },
            /* 04 Field */ new[] { 2, Str, Blob },
            /* 05 MethodPtr */ new[] { Idx(MethodDef) },
            /* 06 MethodDef */ new[] { 4, 2, 2, Str, Blob, Idx(Param) },
            /* 07 ParamPtr */ new[] { Idx(Param) },
            /* 08 Param */ new[] { 2, 2, Str },
            /* 09 InterfaceImpl */ new[] { Idx(TypeDef), CTypeDefOrRef },
            /* 0A MemberRef */ new[] { CMemberRefParent, Str, Blob },
            /* 0B Constant */ new[] { 2, CHasConstant, Blob },
            /* 0C CustomAttribute */ new[] { CHasCustomAttribute, CCustomAttributeType, Blob },
            /* 0D FieldMarshal */ new[] { CHasFieldMarshal, Blob },
            /* 0E DeclSecurity */ new[] { 2, CHasDeclSecurity, Blob },
            /* 0F ClassLayout */ new[] { 2, 4, Idx(TypeDef) },
            /* 10 FieldLayout */ new[] { 4, Idx(Field) },
            /* 11 StandAloneSig */ new[] { Blob },
            /* 12 EventMap */ new[] { Idx(TypeDef), Idx(Event) },
            /* 13 EventPtr */ new[] { Idx(Event) },
            /* 14 Event */ new[] { 2, Str, CTypeDefOrRef },
            /* 15 PropertyMap */ new[] { Idx(TypeDef), Idx(Property) },
            /* 16 PropertyPtr */ new[] { Idx(Property) },
            /* 17 Property */ new[] { 2, Str, Blob },
            /* 18 MethodSemantics */ new[] { 2, Idx(MethodDef), CHasSemantics },
            /* 19 MethodImpl */ new[] { Idx(TypeDef), CMethodDefOrRef, CMethodDefOrRef },
            /* 1A ModuleRef */ new[] { Str },
            /* 1B TypeSpec */ new[] { Blob },
            /* 1C ImplMap */ new[] { 2, CMemberForwarded, Str, Idx(ModuleRef) },
            /* 1D FieldRVA */ new[] { 4, Idx(Field) },
            /* 1E EncLog */ new[] { 4, 4 },
            /* 1F EncMap */ new[] { 4 },
            /* 20 Assembly */ new[] { 4, 2, 2, 2, 2, 4, Blob, Str, Str },
            /* 21 AssemblyProcessor */ new[] { 4 },
            /* 22 AssemblyOS */ new[] { 4, 4, 4 },
            /* 23 AssemblyRef */ new[] { 2, 2, 2, 2, 4, Blob, Str, Str, Blob },
            /* 24 AssemblyRefProcessor */ new[] { 4, Idx(AssemblyRef) },
            /* 25 AssemblyRefOS */ new[] { 4, 4, 4, Idx(AssemblyRef) },
            /* 26 File */ new[] { 4, Str, Blob },
            /* 27 ExportedType */ new[] { 4, 4, Str, Str, CImplementation },
            /* 28 ManifestResource */ new[] { 4, 4, Str, CImplementation },
            /* 29 NestedClass */ new[] { Idx(TypeDef), Idx(TypeDef) },
            /* 2A GenericParam */ new[] { 2, 2, CTypeOrMethodDef, Str },
            /* 2B MethodSpec */ new[] { CMethodDefOrRef, Blob },
            /* 2C GenericParamConstraint */ new[] { Idx(GenericParam), CTypeDefOrRef }
        };

        private static readonly Dictionary<int, int[]> Coded = new Dictionary<int, int[]>
        {
            { CTypeDefOrRef, new[] { TypeDef, TypeRef, TypeSpec } },
            { CHasConstant, new[] { Field, Param, Property } },
            { CHasCustomAttribute, new[] { MethodDef, Field, TypeRef, TypeDef, Param, InterfaceImpl, MemberRef, Module, DeclSecurity, Property, Event, StandAloneSig, ModuleRef, TypeSpec, Assembly, AssemblyRef, File_, ExportedType, ManifestResource, GenericParam, GenericParamConstraint, MethodSpec } },
            { CHasFieldMarshal, new[] { Field, Param } },
            { CHasDeclSecurity, new[] { TypeDef, MethodDef, Assembly } },
            { CMemberRefParent, new[] { TypeDef, TypeRef, ModuleRef, MethodDef, TypeSpec } },
            { CHasSemantics, new[] { Event, Property } },
            { CMethodDefOrRef, new[] { MethodDef, MemberRef } },
            { CMemberForwarded, new[] { Field, MethodDef } },
            { CImplementation, new[] { File_, AssemblyRef, ExportedType } },
            { CCustomAttributeType, new[] { -1, -1, MethodDef, MemberRef, -1 } },
            { CResolutionScope, new[] { Module, ModuleRef, AssemblyRef, TypeRef } },
            { CTypeOrMethodDef, new[] { TypeDef, MethodDef } }
        };

        private void ParseTables(int p)
        {
            byte heapSizes = data[p + 6];
            wideStrings = (heapSizes & 1) != 0;
            wideGuids = (heapSizes & 2) != 0;
            wideBlobs = (heapSizes & 4) != 0;
            ulong valid = BitConverter.ToUInt64(data, p + 8);
            int q = p + 24;
            for (int t = 0; t < 64; t++)
            {
                if ((valid & (1UL << t)) != 0)
                {
                    rows[t] = I32(q);
                    q += 4;
                }
            }
            // Tables after GenericParamConstraint (0x2C) do not occur in assemblies JoyPro reads; stop if they do.
            for (int t = 0; t < 64; t++)
            {
                if (rows[t] == 0)
                    continue;
                if (t >= Schema.Length)
                    throw new ArgumentException("unknown metadata table " + t.ToString("X2"));
                int size = 0;
                foreach (int column in Schema[t])
                    size += ColumnSize(column);
                rowSizes[t] = size;
                tableOffsets[t] = q;
                q += size * rows[t];
            }
        }

        private int ColumnSize(int column)
        {
            if (column > 0)
                return column;
            if (column == Str)
                return wideStrings ? 4 : 2;
            if (column == Guid_)
                return wideGuids ? 4 : 2;
            if (column == Blob)
                return wideBlobs ? 4 : 2;
            if (column <= IndexBase)
                return rows[IndexBase - column] < 0x10000 ? 2 : 4;
            int[] tables = Coded[column];
            int bits = 0;
            while ((1 << bits) < tables.Length)
                bits++;
            int max = 0;
            foreach (int t in tables)
            {
                if (t >= 0)
                    max = Math.Max(max, rows[t]);
            }
            return max < (1 << (16 - bits)) ? 2 : 4;
        }

        // The value of one column of one row (1-based row).
        private int Cell(int table, int row, int column)
        {
            int p = tableOffsets[table] + (row - 1) * rowSizes[table];
            for (int c = 0; c < column; c++)
                p += ColumnSize(Schema[table][c]);
            int size = ColumnSize(Schema[table][column]);
            return size == 2 ? U16(p) : size == 1 ? data[p] : I32(p);
        }

        private string String(int offset)
        {
            if (offset <= 0 || offset >= stringsSize)
                return string.Empty;
            int start = stringsHeap + offset;
            int end = start;
            while (data[end] != 0)
                end++;
            return Encoding.UTF8.GetString(data, start, end - start);
        }

        // A #US string by its ldstr token (0x70xxxxxx).
        public string UserString(int token)
        {
            int offset = token & 0x00FFFFFF;
            if (usHeap == 0 || offset <= 0 || offset >= usSize)
                return null;
            int p = usHeap + offset;
            int length = BlobLength(ref p);
            if (length <= 1)
                return string.Empty;
            return Encoding.Unicode.GetString(data, p, length - 1);
        }

        private int BlobLength(ref int p)
        {
            byte b = data[p];
            if ((b & 0x80) == 0)
            {
                p += 1;
                return b;
            }
            if ((b & 0xC0) == 0x80)
            {
                int value = ((b & 0x3F) << 8) | data[p + 1];
                p += 2;
                return value;
            }
            int wide = ((b & 0x1F) << 24) | (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3];
            p += 4;
            return wide;
        }

        private void ReadTypes()
        {
            int typeCount = rows[TypeDef];
            int methodCount = rows[MethodDef];
            for (int t = 1; t <= typeCount; t++)
            {
                TypeInfo type = new TypeInfo
                {
                    Name = String(Cell(TypeDef, t, 1)),
                    Namespace = String(Cell(TypeDef, t, 2)),
                    BaseName = TypeName(Cell(TypeDef, t, 3), CTypeDefOrRef)
                };
                int first = Cell(TypeDef, t, 5);
                int last = t < typeCount ? Cell(TypeDef, t + 1, 5) : methodCount + 1;
                for (int m = first; m < last && m <= methodCount; m++)
                    type.Methods.Add(new MethodInfo { Name = String(Cell(MethodDef, m, 3)), Rva = (uint)Cell(MethodDef, m, 0) });
                Types.Add(type);
            }
        }

        private string TypeName(int coded, int kind)
        {
            int[] tables = Coded[kind];
            int bits = 0;
            while ((1 << bits) < tables.Length)
                bits++;
            int tag = coded & ((1 << bits) - 1);
            int row = coded >> bits;
            if (row == 0 || tag >= tables.Length)
                return string.Empty;
            if (tables[tag] == TypeDef)
                return String(Cell(TypeDef, row, 1));
            if (tables[tag] == TypeRef)
                return String(Cell(TypeRef, row, 1));
            return string.Empty;
        }

        // The name of the method a call token points to (MethodDef, MemberRef or a MethodSpec of either).
        public string MethodName(int token)
        {
            int table = (int)((uint)token >> 24);
            int row = token & 0x00FFFFFF;
            if (row <= 0 || table >= 64 || row > rows[table])
                return string.Empty;
            if (table == MethodDef)
                return String(Cell(MethodDef, row, 3));
            if (table == MemberRef)
                return String(Cell(MemberRef, row, 1));
            if (table == MethodSpec)
            {
                int coded = Cell(MethodSpec, row, 0);
                int tag = coded & 1;
                int target = coded >> 1;
                return MethodName(((tag == 0 ? MethodDef : MemberRef) << 24) | target);
            }
            return string.Empty;
        }

        // ---- IL ---------------------------------------------------------------------------------------------------

        // The string literal a method returns first (a property getter "=> \"FA26B\""), or null.
        public string FirstString(MethodInfo method)
        {
            foreach (KeyValuePair<int, int> instruction in Instructions(method))
            {
                if (instruction.Key == 0x72)
                    return UserString(instruction.Value);
            }
            return null;
        }

        // The calls of a method to the methods "tracked" accepts, each with the ldstr literals since the previous call.
        public List<CallSite> Calls(MethodInfo method, Func<string, bool> tracked)
        {
            List<CallSite> calls = new List<CallSite>();
            List<string> strings = new List<string>();
            foreach (KeyValuePair<int, int> instruction in Instructions(method))
            {
                if (instruction.Key == 0x72)
                {
                    string text = UserString(instruction.Value);
                    if (text != null)
                        strings.Add(text);
                }
                else if (instruction.Key == 0x28 || instruction.Key == 0x6F)
                {
                    string name = MethodName(instruction.Value);
                    if (!tracked(name))
                    {
                        // Another call used the strings loaded so far (a log line, a lookup): they are not arguments
                        // of the next tracked call. (Delegates for the helpers' arguments are made with newobj.)
                        strings.Clear();
                        continue;
                    }
                    calls.Add(new CallSite { Method = name, Strings = strings });
                    strings = new List<string>();
                }
            }
            return calls;
        }

        // Opcode (0xFExx for two-byte opcodes) and its 4-byte operand where it has one (0 otherwise).
        private IEnumerable<KeyValuePair<int, int>> Instructions(MethodInfo method)
        {
            if (method.Rva == 0)
                yield break;
            int p = Offset((int)method.Rva);
            if (p < 0 || p >= data.Length)
                yield break;
            int codeSize;
            int code;
            byte header = data[p];
            if ((header & 3) == 2)
            {
                codeSize = header >> 2;
                code = p + 1;
            }
            else if ((header & 3) == 3)
            {
                int headerSize = (data[p + 1] >> 4) * 4;
                codeSize = I32(p + 4);
                code = p + headerSize;
            }
            else
                yield break;
            int end = Math.Min(code + codeSize, data.Length);
            int i = code;
            while (i < end)
            {
                int op = data[i++];
                int operandSize;
                if (op == 0xFE)
                {
                    if (i >= end)
                        yield break;
                    op = 0xFE00 | data[i++];
                    operandSize = TwoByteOperand(op & 0xFF);
                }
                else
                {
                    operandSize = OneByteOperand(op, i, end);
                }
                if (operandSize < 0 || i + operandSize > end)
                    yield break;
                int operand = operandSize == 4 ? I32(i) : 0;
                i += operandSize;
                yield return new KeyValuePair<int, int>(op, operand);
            }
        }

        private int OneByteOperand(int op, int i, int end)
        {
            if (op <= 0x0D)
                return 0;
            if (op <= 0x13)
                return 1;
            if (op <= 0x1E)
                return 0;
            if (op == 0x1F)
                return 1;
            if (op == 0x20 || op == 0x22)
                return 4;
            if (op == 0x21 || op == 0x23)
                return 8;
            if (op >= 0x25 && op <= 0x26)
                return 0;
            if (op >= 0x27 && op <= 0x29)
                return 4;
            if (op == 0x2A)
                return 0;
            if (op >= 0x2B && op <= 0x37)
                return 1;
            if (op >= 0x38 && op <= 0x44)
                return 4;
            if (op == 0x45)
            {
                if (i + 4 > end)
                    return -1;
                return 4 + 4 * I32(i);
            }
            if (op >= 0x46 && op <= 0x6E)
                return 0;
            if (op >= 0x6F && op <= 0x75)
                return 4;
            if (op == 0x76 || op == 0x7A)
                return 0;
            if (op == 0x79 || (op >= 0x7B && op <= 0x81))
                return 4;
            if (op >= 0x82 && op <= 0x8B)
                return 0;
            if (op == 0x8C || op == 0x8D || op == 0x8F)
                return 4;
            if (op == 0x8E || (op >= 0x90 && op <= 0xA2))
                return 0;
            if (op >= 0xA3 && op <= 0xA5)
                return 4;
            if (op >= 0xB3 && op <= 0xBA)
                return 0;
            if (op == 0xC2 || op == 0xC6 || op == 0xD0 || op == 0xDD)
                return 4;
            if (op == 0xC3 || (op >= 0xD1 && op <= 0xDC) || op == 0xDF || op == 0xE0)
                return 0;
            if (op == 0xDE)
                return 1;
            return -1;
        }

        private static int TwoByteOperand(int op)
        {
            switch (op)
            {
                case 0x06:
                case 0x07:
                case 0x15:
                case 0x16:
                case 0x1C:
                    return 4;
                case 0x09:
                case 0x0A:
                case 0x0B:
                case 0x0C:
                case 0x0D:
                case 0x0E:
                    return 2;
                case 0x12:
                case 0x19:
                    return 1;
                case 0x00:
                case 0x01:
                case 0x02:
                case 0x03:
                case 0x04:
                case 0x05:
                case 0x0F:
                case 0x11:
                case 0x13:
                case 0x14:
                case 0x17:
                case 0x18:
                case 0x1A:
                case 0x1D:
                case 0x1E:
                    return 0;
                default:
                    return -1;
            }
        }

        private int I32(int p)
        {
            return BitConverter.ToInt32(data, p);
        }

        private int U16(int p)
        {
            return BitConverter.ToUInt16(data, p);
        }
    }
}
