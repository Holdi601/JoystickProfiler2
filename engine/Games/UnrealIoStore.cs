using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace JoyPro.Engine.Games
{
    // Read-only access to an Unreal Engine 5 IoStore container (.utoc + .ucas, TOC version 8, unencrypted) and to the
    // cooked Zen packages in it, as far as JoyPro needs it: the Enhanced Input mapping contexts (IMC_*) and input
    // actions (IA_*) of Hell Let Loose: Vietnam (docs/games/hll-vietnam.md). Layout verified on HLLV (UE 5.5.4,
    // 2026-10). The blocks are Oodle-compressed: decompression goes through an Oodle DLL (oo2core_*_win64.dll) that is
    // already on the user's PC (JoyPro does not ship Oodle); without one the container cannot be read.
    internal sealed class UnrealIoStore : IDisposable
    {
        private const string Magic = "-==--==--==--==-";

        private readonly string utocPath;
        private readonly FileStream ucas;
        private readonly OodleDecompressor oodle;
        private readonly long chunkTable;   // offset/length table (5+5 bytes big-endian per entry)
        private readonly long blockTable;   // 12 bytes per compression block
        private readonly int entryCount;
        private readonly int blockSize;
        private byte[] utoc;

        public Dictionary<string, int> Files { get; private set; }
        public string MountPoint { get; private set; }

        private UnrealIoStore(string utocPath, OodleDecompressor oodle)
        {
            this.utocPath = utocPath;
            this.oodle = oodle;
            utoc = File.ReadAllBytes(utocPath);
            if (utoc.Length < 144 || Encoding.ASCII.GetString(utoc, 0, 16) != Magic)
                throw new InvalidDataException("Not an IoStore table of contents: " + utocPath);
            int version = utoc[16];
            if (version < 8)
                throw new InvalidDataException("IoStore TOC version " + version + " is not supported (8 is).");
            int headerSize = BitConverter.ToInt32(utoc, 20);
            entryCount = BitConverter.ToInt32(utoc, 24);
            int blockCount = BitConverter.ToInt32(utoc, 28);
            int methodCount = BitConverter.ToInt32(utoc, 36);
            int methodNameLength = BitConverter.ToInt32(utoc, 40);
            blockSize = BitConverter.ToInt32(utoc, 44);
            int directorySize = BitConverter.ToInt32(utoc, 48);
            byte flags = utoc[0x50];
            int seeds = BitConverter.ToInt32(utoc, 0x54);
            int withoutPerfectHash = BitConverter.ToInt32(utoc, 0x60);
            if ((flags & 0x02) != 0)
                throw new InvalidDataException("The IoStore container is encrypted.");

            long p = headerSize;
            p += 12L * entryCount; // chunk ids
            chunkTable = p;
            p += 10L * entryCount;
            p += 4L * seeds;
            p += 4L * withoutPerfectHash;
            blockTable = p;
            p += 12L * blockCount;
            p += (long)methodNameLength * methodCount;
            if ((flags & 0x04) != 0)
            {
                int hashSize = BitConverter.ToInt32(utoc, (int)p);
                p += 4 + 2L * hashSize + 20L * blockCount;
            }
            Files = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if ((flags & 0x08) != 0)
                ReadDirectory((int)p, directorySize);
            ucas = new FileStream(Path.ChangeExtension(utocPath, ".ucas"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        }

        public static UnrealIoStore Open(string utocPath, OodleDecompressor oodle)
        {
            return new UnrealIoStore(utocPath, oodle);
        }

        public void Dispose()
        {
            ucas.Dispose();
            utoc = null;
        }

        private void ReadDirectory(int start, int size)
        {
            int p = start;
            int end = start + size;
            MountPoint = ReadFString(utoc, ref p);
            int dirCount = BitConverter.ToInt32(utoc, p);
            p += 4;
            int dirs = p;
            p += 16 * dirCount;
            int fileCount = BitConverter.ToInt32(utoc, p);
            p += 4;
            int files = p;
            p += 12 * fileCount;
            int stringCount = BitConverter.ToInt32(utoc, p);
            p += 4;
            string[] strings = new string[stringCount];
            for (int i = 0; i < stringCount && p < end; i++)
                strings[i] = ReadFString(utoc, ref p);

            const uint none = 0xFFFFFFFF;
            Stack<KeyValuePair<uint, string>> stack = new Stack<KeyValuePair<uint, string>>();
            stack.Push(new KeyValuePair<uint, string>(0, MountPoint));
            while (stack.Count > 0)
            {
                KeyValuePair<uint, string> item = stack.Pop();
                int d = dirs + 16 * (int)item.Key;
                uint name = BitConverter.ToUInt32(utoc, d);
                uint child = BitConverter.ToUInt32(utoc, d + 4);
                uint file = BitConverter.ToUInt32(utoc, d + 12);
                string path = name == none ? item.Value : item.Value + strings[name] + "/";
                while (file != none)
                {
                    int f = files + 12 * (int)file;
                    Files[path + strings[BitConverter.ToUInt32(utoc, f)]] = (int)BitConverter.ToUInt32(utoc, f + 8);
                    file = BitConverter.ToUInt32(utoc, f + 4);
                }
                while (child != none)
                {
                    stack.Push(new KeyValuePair<uint, string>(child, path));
                    child = BitConverter.ToUInt32(utoc, dirs + 16 * (int)child + 8);
                }
            }
        }

        // The bytes of one chunk (a package), decompressed.
        public byte[] ReadChunk(int index)
        {
            if (index < 0 || index >= entryCount)
                throw new ArgumentOutOfRangeException("index");
            long entry = chunkTable + 10L * index;
            long offset = BigEndian40(utoc, (int)entry);
            long length = BigEndian40(utoc, (int)entry + 5);
            long first = offset / blockSize;
            long last = (offset + length - 1) / blockSize;
            byte[] output = new byte[length];
            long written = 0;
            for (long block = first; block <= last; block++)
            {
                int b = (int)(blockTable + 12 * block);
                ulong low = BitConverter.ToUInt64(utoc, b);
                uint high = BitConverter.ToUInt32(utoc, b + 8);
                long blockOffset = (long)(low & 0xFFFFFFFFFFUL);
                int compressed = (int)((low >> 40) & 0xFFFFFF);
                int raw = (int)(high & 0xFFFFFF);
                int method = (int)(high >> 24);
                byte[] packed = new byte[compressed];
                ucas.Position = blockOffset;
                ReadExactly(ucas, packed);
                byte[] data;
                if (method == 0)
                {
                    data = packed;
                }
                else
                {
                    if (oodle == null)
                        throw new InvalidDataException("The container is Oodle-compressed and no Oodle decompressor is available.");
                    data = oodle.Decompress(packed, raw);
                }
                long blockStart = block * blockSize;
                long from = Math.Max(offset, blockStart) - blockStart;
                long to = Math.Min(offset + length, blockStart + raw) - blockStart;
                Buffer.BlockCopy(data, (int)from, output, (int)written, (int)(to - from));
                written += to - from;
            }
            return output;
        }

        private static void ReadExactly(Stream stream, byte[] buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0)
                    throw new EndOfStreamException();
                read += n;
            }
        }

        private static long BigEndian40(byte[] data, int p)
        {
            long value = 0;
            for (int i = 0; i < 5; i++)
                value = (value << 8) | data[p + i];
            return value;
        }

        internal static string ReadFString(byte[] data, ref int p)
        {
            int n = BitConverter.ToInt32(data, p);
            p += 4;
            if (n == 0)
                return string.Empty;
            string value;
            if (n < 0)
            {
                value = Encoding.Unicode.GetString(data, p, -2 * n - 2);
                p += -2 * n;
            }
            else
            {
                value = Encoding.GetEncoding(28591).GetString(data, p, n - 1);
                p += n;
            }
            return value;
        }
    }

    // OodleLZ_Decompress of an oo2core_*_win64.dll that is on the user's PC (a game ships it; JoyPro does not).
    internal sealed class OodleDecompressor : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate long DecompressFn(byte[] compressed, long compressedSize, byte[] raw, long rawSize, int fuzzSafe, int checkCrc, int verbosity,
            IntPtr decodeBufferBase, long decodeBufferSize, IntPtr callback, IntPtr callbackUserData, IntPtr decoderMemory, long decoderMemorySize, int threadPhase);

        private IntPtr module;
        private readonly DecompressFn decompress;

        public string Path { get; private set; }

        private OodleDecompressor(string path, IntPtr module, DecompressFn decompress)
        {
            Path = path;
            this.module = module;
            this.decompress = decompress;
        }

        public static OodleDecompressor TryLoad(string path, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !Environment.Is64BitProcess)
                return null;
            IntPtr module = LoadLibraryEx(path, IntPtr.Zero, 0x00000008 /* LOAD_WITH_ALTERED_SEARCH_PATH */);
            if (module == IntPtr.Zero)
            {
                warnings?.Add("Oodle could not be loaded from " + path + " (error " + Marshal.GetLastWin32Error() + ").");
                return null;
            }
            IntPtr function = GetProcAddress(module, "OodleLZ_Decompress");
            if (function == IntPtr.Zero)
            {
                FreeLibrary(module);
                warnings?.Add(path + " has no OodleLZ_Decompress.");
                return null;
            }
            return new OodleDecompressor(path, module, (DecompressFn)Marshal.GetDelegateForFunctionPointer(function, typeof(DecompressFn)));
        }

        public byte[] Decompress(byte[] compressed, int rawSize)
        {
            byte[] raw = new byte[rawSize];
            long result = decompress(compressed, compressed.Length, raw, rawSize, 1, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 3);
            if (result != rawSize)
                throw new InvalidDataException("Oodle could not decompress a block (" + result + " of " + rawSize + " bytes).");
            return raw;
        }

        public void Dispose()
        {
            if (module != IntPtr.Zero)
                FreeLibrary(module);
            module = IntPtr.Zero;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);
    }

    // One cooked Zen package (UE 5.5, unversioned): names, imports, exports and their serialized data.
    internal sealed class ZenPackage
    {
        private readonly byte[] data;
        private readonly int headerSize;
        private readonly ulong[] imports;
        private readonly List<string> importedPackages;

        public List<string> Names { get; private set; }
        public List<ZenExport> Exports { get; private set; }

        public ZenPackage(byte[] data)
        {
            this.data = data;
            if (BitConverter.ToUInt32(data, 0) != 0)
                throw new InvalidDataException("Versioned Zen packages are not supported.");
            headerSize = BitConverter.ToInt32(data, 4);
            int importMap = BitConverter.ToInt32(data, 28);
            int exportMap = BitConverter.ToInt32(data, 32);
            int exportBundles = BitConverter.ToInt32(data, 36);
            int importedPackageNames = BitConverter.ToInt32(data, 48);
            int p = 52;
            Names = ReadNameBatch(data, ref p);
            imports = new ulong[Math.Max(0, (exportMap - importMap) / 8)];
            for (int i = 0; i < imports.Length; i++)
                imports[i] = BitConverter.ToUInt64(data, importMap + 8 * i);
            Exports = new List<ZenExport>();
            for (int e = exportMap; e + 72 <= exportBundles; e += 72)
            {
                uint nameIndex = BitConverter.ToUInt32(data, e + 16);
                uint nameNumber = BitConverter.ToUInt32(data, e + 20);
                Exports.Add(new ZenExport
                {
                    SerialOffset = (long)BitConverter.ToUInt64(data, e),
                    SerialSize = (long)BitConverter.ToUInt64(data, e + 8),
                    Name = WithNumber(Names[(int)(nameIndex & 0x3FFFFFFF)], nameNumber)
                });
            }
            importedPackages = new List<string>();
            int q = importedPackageNames;
            List<string> packageNames = ReadNameBatch(data, ref q);
            for (int i = 0; i < packageNames.Count; i++)
                importedPackages.Add(WithNumber(packageNames[i], BitConverter.ToUInt32(data, q + 4 * i)));
        }

        public ZenReader Reader(ZenExport export)
        {
            return new ZenReader(this, data, (int)(headerSize + export.SerialOffset), (int)(headerSize + export.SerialOffset + export.SerialSize));
        }

        // An object reference (i32): > 0 export (1-based), < 0 import; the import's package (last path part) or null.
        public string ImportedPackage(int reference)
        {
            if (reference >= 0 || -reference - 1 >= imports.Length)
                return null;
            ulong import = imports[-reference - 1];
            if ((import >> 62) != 2)
                return null;
            int index = (int)((import >> 32) & 0x3FFFFFFF);
            if (index >= importedPackages.Count)
                return null;
            string package = importedPackages[index];
            int slash = package.LastIndexOf('/');
            return slash < 0 ? package : package.Substring(slash + 1);
        }

        public string FName(uint index, uint number)
        {
            int i = (int)(index & 0x3FFFFFFF);
            return i < Names.Count ? WithNumber(Names[i], number) : string.Empty;
        }

        private static string WithNumber(string name, uint number)
        {
            return number == 0 ? name : name + "_" + (number - 1);
        }

        private static List<string> ReadNameBatch(byte[] data, ref int p)
        {
            List<string> names = new List<string>();
            int count = BitConverter.ToInt32(data, p);
            if (count == 0)
            {
                p += 4;
                return names;
            }
            p += 16 + 8 * count; // count, string bytes, hash version, hashes
            int[] headers = new int[count];
            for (int i = 0; i < count; i++)
                headers[i] = (data[p + 2 * i] << 8) | data[p + 2 * i + 1];
            p += 2 * count;
            foreach (int header in headers)
            {
                if ((header & 0x8000) != 0)
                {
                    int length = header & 0x7FFF;
                    if ((p & 1) != 0)
                        p++;
                    names.Add(Encoding.Unicode.GetString(data, p, 2 * length));
                    p += 2 * length;
                }
                else
                {
                    names.Add(Encoding.GetEncoding(28591).GetString(data, p, header));
                    p += header;
                }
            }
            return names;
        }
    }

    internal sealed class ZenExport
    {
        public long SerialOffset { get; set; }
        public long SerialSize { get; set; }
        public string Name { get; set; }

        // "InputModifierScalar_3" -> "InputModifierScalar".
        public string ClassName
        {
            get
            {
                int underscore = Name.LastIndexOf('_');
                int number;
                return underscore > 0 && int.TryParse(Name.Substring(underscore + 1), out number) ? Name.Substring(0, underscore) : Name;
            }
        }
    }

    // Reads unversioned (cooked) property data.
    internal sealed class ZenReader
    {
        private readonly ZenPackage package;
        private readonly byte[] data;
        private readonly int end;

        public int Position { get; set; }

        public ZenReader(ZenPackage package, byte[] data, int start, int end)
        {
            this.package = package;
            this.data = data;
            Position = start;
            this.end = end;
        }

        public byte U8()
        {
            Check(1);
            return data[Position++];
        }

        public int I32()
        {
            Check(4);
            int value = BitConverter.ToInt32(data, Position);
            Position += 4;
            return value;
        }

        public uint U32()
        {
            Check(4);
            uint value = BitConverter.ToUInt32(data, Position);
            Position += 4;
            return value;
        }

        public float F32()
        {
            Check(4);
            float value = BitConverter.ToSingle(data, Position);
            Position += 4;
            return value;
        }

        public string FName()
        {
            uint index = U32();
            uint number = U32();
            return package.FName(index, number);
        }

        // The unversioned header: (schema index, is zero) per serialized property.
        public List<KeyValuePair<int, bool>> Header()
        {
            List<int[]> fragments = new List<int[]>();
            while (true)
            {
                Check(2);
                int value = BitConverter.ToUInt16(data, Position);
                Position += 2;
                fragments.Add(new[] { value & 0x7F, (value & 0x80) != 0 ? 1 : 0, value >> 9 });
                if ((value & 0x100) != 0)
                    break;
            }
            int zeroCount = fragments.Where(f => f[1] == 1).Sum(f => f[2]);
            ulong mask = 0;
            if (zeroCount > 0)
            {
                if (zeroCount <= 8)
                    mask = U8();
                else if (zeroCount <= 16)
                {
                    Check(2);
                    mask = BitConverter.ToUInt16(data, Position);
                    Position += 2;
                }
                else
                {
                    int words = (zeroCount + 31) / 32;
                    for (int i = 0; i < words; i++)
                    {
                        ulong word = U32();
                        if (i < 2)
                            mask |= word << (32 * i);
                    }
                }
            }
            List<KeyValuePair<int, bool>> output = new List<KeyValuePair<int, bool>>();
            int index = 0;
            int bit = 0;
            foreach (int[] fragment in fragments)
            {
                index += fragment[0];
                for (int i = 0; i < fragment[2]; i++)
                {
                    bool zero = false;
                    if (fragment[1] == 1)
                    {
                        zero = bit < 64 && ((mask >> bit) & 1) != 0;
                        bit++;
                    }
                    output.Add(new KeyValuePair<int, bool>(index, zero));
                    index++;
                }
            }
            return output;
        }

        private void Check(int bytes)
        {
            if (Position + bytes > end)
                throw new InvalidDataException("Unexpected end of the package data.");
        }
    }

    // One Enhanced Input key mapping of a mapping context.
    internal sealed class InputKeyMapping
    {
        public string Context { get; set; }
        public string Action { get; set; }
        public string Key { get; set; }
        public string MappingName { get; set; }
        public List<string> Modifiers { get; set; }

        public InputKeyMapping()
        {
            Context = Action = Key = MappingName = string.Empty;
            Modifiers = new List<string>();
        }
    }

    // The Enhanced Input assets of a package: UInputMappingContext.Mappings and UInputAction.ValueType.
    internal static class EnhancedInputAssets
    {
        // FEnhancedActionKeyMapping: 0 Triggers, 1 Modifiers, 2 Action, 3 Key {0 KeyName}, 6 SettingBehavior,
        // 7 PlayerMappableKeySettings; UPlayerMappableKeySettings: 1 Name.
        public static List<InputKeyMapping> Mappings(ZenPackage package, string context)
        {
            List<InputKeyMapping> output = new List<InputKeyMapping>();
            if (package.Exports.Count == 0)
                return output;
            ZenReader reader = package.Reader(package.Exports[0]);
            foreach (KeyValuePair<int, bool> property in reader.Header())
            {
                if (property.Key != 0)
                    throw new InvalidDataException("Unexpected mapping context property " + property.Key + ".");
                if (property.Value)
                    continue;
                int count = reader.I32();
                for (int m = 0; m < count; m++)
                {
                    InputKeyMapping mapping = new InputKeyMapping { Context = context };
                    foreach (KeyValuePair<int, bool> field in reader.Header())
                    {
                        if (field.Value)
                            continue;
                        switch (field.Key)
                        {
                            case 0:
                            case 1:
                                int n = reader.I32();
                                for (int i = 0; i < n; i++)
                                {
                                    int reference = reader.I32();
                                    if (field.Key == 1 && reference > 0 && reference - 1 < package.Exports.Count)
                                        mapping.Modifiers.Add(package.Exports[reference - 1].ClassName);
                                }
                                break;
                            case 2:
                                mapping.Action = package.ImportedPackage(reader.I32()) ?? string.Empty;
                                break;
                            case 3:
                                foreach (KeyValuePair<int, bool> keyField in reader.Header())
                                {
                                    if (keyField.Key == 0 && !keyField.Value)
                                        mapping.Key = reader.FName();
                                }
                                break;
                            case 6:
                                reader.U8();
                                break;
                            case 7:
                                int settings = reader.I32();
                                if (settings > 0 && settings - 1 < package.Exports.Count)
                                    mapping.MappingName = MappableName(package, package.Exports[settings - 1]);
                                break;
                            default:
                                throw new InvalidDataException("Unexpected key mapping property " + field.Key + ".");
                        }
                    }
                    output.Add(mapping);
                }
            }
            return output;
        }

        private static string MappableName(ZenPackage package, ZenExport export)
        {
            ZenReader reader = package.Reader(export);
            foreach (KeyValuePair<int, bool> property in reader.Header())
            {
                if (property.Value)
                    continue;
                if (property.Key == 0)
                    reader.I32();
                else if (property.Key == 1)
                    return reader.FName();
                else
                    break;
            }
            return string.Empty;
        }

        // UInputAction.ValueType (schema index 6): 0 Boolean, 1 Axis1D, 2 Axis2D, 3 Axis3D. Other byte properties
        // before it are skipped; -1 when it cannot be read.
        public static int ValueType(ZenPackage package)
        {
            if (package.Exports.Count == 0)
                return -1;
            ZenReader reader = package.Reader(package.Exports[0]);
            try
            {
                foreach (KeyValuePair<int, bool> property in reader.Header())
                {
                    int value = property.Value ? 0 : reader.U8();
                    if (property.Key == 6)
                        return value;
                }
            }
            catch (InvalidDataException)
            {
                return -1;
            }
            return 0;
        }
    }
}
