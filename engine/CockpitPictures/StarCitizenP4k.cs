#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace JoyPro.CockpitPictures
{
    // Star Citizen's Data.p4k, read only (docs/cockpits.md "Pictures from your games"): a ZIP64 archive with CIG's own
    // extra fields (0x0001 the zip64 sizes, 0x5000, 0x5002 = 1 for an encrypted entry, 0x5003; CIG's extra lengths count
    // the 4 bytes of the field's own header). Entries are stored (0) or zstd packed (100). Encrypted entries are refused,
    // never decrypted. The central directory (1.4 million entries, half a gigabyte) is cached per archive size and time;
    // only the kinds of files the cockpit pictures use are kept.
    internal sealed class StarCitizenP4k : IDisposable
    {
        public const int Stored = 0, Deflated = 8, Zstandard = 100;
        public const int CacheVersion = 1;

        public sealed class EncryptedEntryException : IOException
        {
            public string Entry;

            public EncryptedEntryException(string entry) : base(entry + " is encrypted (not read)")
            {
                Entry = entry;
            }
        }

        private struct Entry
        {
            public int Name;          // offset into the name bytes
            public ushort NameLength;
            public byte Method;
            public bool Encrypted;
            public long Packed;
            public long Size;
            public long Offset;       // the local header
        }

        private Entry[] entries = new Entry[0];
        private int count;
        private byte[] names = new byte[0];
        private int nameBytes;
        private int[] buckets;
        private int[] next;
        private Stream stream;
        private readonly object gate = new object();
        // zstd: (data, offset, length) -> the unpacked bytes (the engine passes ZstdSharp).
        public Func<byte[], int, int, byte[]> Zstd;
        public string Path = string.Empty;
        // Encrypted entries asked for (to report which ones were needed).
        public readonly HashSet<string> EncryptedAsked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int Count { get { return count; } }

        // Kinds of entries kept: geometry, materials, textures (split mips .dds.N, .dds.Na), object containers, the
        // DataForge database, the localization, and XML/JSON (to tell which encrypted ones were needed).
        private static readonly string[] Kept = { ".cga", ".cgam", ".cgf", ".cgfm", ".skin", ".skinm", ".chr", ".cdf", ".mtl", ".dds", ".socpak", ".dcb", ".ini", ".xml", ".json" };

        public static bool Keep(string name)
        {
            string n = name;
            // .dds.3, .dds.3a
            int dot = n.LastIndexOf('.');
            if (dot > 0 && dot + 1 < n.Length && char.IsDigit(n[dot + 1]))
                n = n.Substring(0, dot);
            foreach (string k in Kept)
            {
                if (n.EndsWith(k, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ opening

        public static StarCitizenP4k Open(string path, string cacheFolder, Func<byte[], int, int, byte[]> zstd)
        {
            StarCitizenP4k p = new StarCitizenP4k { Path = path, Zstd = zstd };
            FileInfo fi = new FileInfo(path);
            string cache = string.IsNullOrEmpty(cacheFolder) ? string.Empty
                : System.IO.Path.Combine(cacheFolder, "p4k-" + fi.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + fi.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".index");
            bool loaded = false;
            if (cache.Length > 0 && File.Exists(cache))
            {
                try
                {
                    p.LoadCache(cache);
                    loaded = true;
                }
                catch (Exception)
                {
                    p.count = 0;
                    p.nameBytes = 0;
                }
            }
            p.stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
            try
            {
                if (!loaded)
                {
                    p.ReadCentralDirectory(fi.Length);
                    if (cache.Length > 0)
                    {
                        try
                        {
                            Directory.CreateDirectory(cacheFolder);
                            // Older caches of this archive go.
                            foreach (string old in Directory.GetFiles(cacheFolder, "p4k-*.index"))
                            {
                                if (!string.Equals(old, cache, StringComparison.OrdinalIgnoreCase))
                                    File.Delete(old);
                            }
                            p.SaveCache(cache);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                p.BuildIndex();
            }
            catch
            {
                p.Dispose();
                throw;
            }
            return p;
        }

        // An archive in memory (the unit tests).
        public static StarCitizenP4k OpenStream(Stream s, Func<byte[], int, int, byte[]> zstd)
        {
            StarCitizenP4k p = new StarCitizenP4k { Zstd = zstd, stream = s };
            p.ReadCentralDirectory(s.Length);
            p.BuildIndex();
            return p;
        }

        public void Dispose()
        {
            if (stream != null)
                stream.Dispose();
            stream = null;
        }

        private void ReadCentralDirectory(long length)
        {
            // The end records: EOCD (22 bytes, maybe a comment), before it the zip64 locator (20 bytes).
            int tailLength = (int)Math.Min(length, 65557 + 20);
            byte[] tail = ReadAt(length - tailLength, tailLength);
            int eocd = -1;
            for (int i = tail.Length - 22; i >= 0; i--)
            {
                if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 5 && tail[i + 3] == 6)
                {
                    eocd = i;
                    break;
                }
            }
            if (eocd < 0)
                throw new InvalidDataException("Data.p4k: no end of central directory");
            long entriesTotal = BitConverter.ToUInt16(tail, eocd + 10);
            long cdSize = BitConverter.ToUInt32(tail, eocd + 12);
            long cdOffset = BitConverter.ToUInt32(tail, eocd + 16);
            int loc = eocd - 20;
            if (loc >= 0 && BitConverter.ToUInt32(tail, loc) == 0x07064b50)
            {
                long z64 = BitConverter.ToInt64(tail, loc + 8);
                byte[] rec = ReadAt(z64, 56);
                if (BitConverter.ToUInt32(rec, 0) != 0x06064b50)
                    throw new InvalidDataException("Data.p4k: bad zip64 end record");
                entriesTotal = BitConverter.ToInt64(rec, 32);
                cdSize = BitConverter.ToInt64(rec, 40);
                cdOffset = BitConverter.ToInt64(rec, 48);
            }
            if (cdOffset < 0 || cdOffset + cdSize > length || entriesTotal < 0)
                throw new InvalidDataException("Data.p4k: bad central directory");
            entries = new Entry[Math.Max(16, (int)Math.Min(entriesTotal / 2 + 16, 4000000))];
            names = new byte[1 << 20];
            lock (gate)
            {
                stream.Position = cdOffset;
                using (BufferedStream bs = new BufferedStream(new NonClosing(stream), 1 << 22))
                {
                    byte[] h = new byte[46];
                    byte[] buf = new byte[65536 * 2];
                    long read = 0;
                    for (long e = 0; e < entriesTotal && read < cdSize; e++)
                    {
                        Fill(bs, h, 46);
                        if (BitConverter.ToUInt32(h, 0) != 0x02014b50)
                            throw new InvalidDataException("Data.p4k: bad central directory entry " + e.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        int method = BitConverter.ToUInt16(h, 10);
                        long packed = BitConverter.ToUInt32(h, 20);
                        long size = BitConverter.ToUInt32(h, 24);
                        int nlen = BitConverter.ToUInt16(h, 28), xlen = BitConverter.ToUInt16(h, 30), clen = BitConverter.ToUInt16(h, 32);
                        long offset = BitConverter.ToUInt32(h, 42);
                        int total = nlen + xlen + clen;
                        if (buf.Length < total)
                            buf = new byte[total];
                        Fill(bs, buf, total);
                        read += 46 + total;
                        bool encrypted;
                        Extras(buf, nlen, xlen, true, ref size, ref packed, ref offset, out encrypted);
                        string name = Encoding.UTF8.GetString(buf, 0, nlen);
                        if (!Keep(name))
                            continue;
                        Add(buf, nlen, method, encrypted, packed, size, offset);
                    }
                }
            }
        }

        // The extra fields: zip64 sizes and offset (as needed), CIG's 0x5002 (1 = encrypted). CIG's lengths include the
        // field header; standard ZIP lengths do not.
        internal static void Extras(byte[] buf, int start, int xlen, bool cig, ref long size, ref long packed, ref long offset, out bool encrypted)
        {
            encrypted = false;
            int k = start, end = start + xlen;
            while (k + 4 <= end)
            {
                int id = BitConverter.ToUInt16(buf, k), len = BitConverter.ToUInt16(buf, k + 2);
                int body = k + 4, bodyLen = cig ? len - 4 : len;
                if (bodyLen < 0 || body + bodyLen > end)
                    break;
                if (id == 0x0001)
                {
                    int o = body;
                    if (size == 0xFFFFFFFFL && o + 8 <= body + bodyLen)
                    {
                        size = BitConverter.ToInt64(buf, o);
                        o += 8;
                    }
                    if (packed == 0xFFFFFFFFL && o + 8 <= body + bodyLen)
                    {
                        packed = BitConverter.ToInt64(buf, o);
                        o += 8;
                    }
                    if (offset == 0xFFFFFFFFL && o + 8 <= body + bodyLen)
                        offset = BitConverter.ToInt64(buf, o);
                }
                else if (id == 0x5002 && bodyLen >= 2)
                {
                    encrypted = BitConverter.ToUInt16(buf, body) == 1;
                }
                k = body + bodyLen;
            }
        }

        private void Add(byte[] name, int nlen, int method, bool encrypted, long packed, long size, long offset)
        {
            if (count == entries.Length)
                Array.Resize(ref entries, entries.Length * 2);
            while (nameBytes + nlen > names.Length)
                Array.Resize(ref names, names.Length * 2);
            Buffer.BlockCopy(name, 0, names, nameBytes, nlen);
            entries[count++] = new Entry { Name = nameBytes, NameLength = (ushort)nlen, Method = (byte)Math.Min(255, method), Encrypted = encrypted, Packed = packed, Size = size, Offset = offset };
            nameBytes += nlen;
        }

        private sealed class NonClosing : Stream
        {
            private readonly Stream s;
            public NonClosing(Stream s) { this.s = s; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return s.Length; } }
            public override long Position { get { return s.Position; } set { s.Position = value; } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int c) { return s.Read(buffer, offset, c); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int c) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { }
        }

        private static void Fill(Stream s, byte[] b, int n)
        {
            int at = 0;
            while (at < n)
            {
                int r = s.Read(b, at, n - at);
                if (r <= 0)
                    throw new EndOfStreamException();
                at += r;
            }
        }

        private byte[] ReadAt(long offset, int length)
        {
            byte[] b = new byte[length];
            lock (gate)
            {
                stream.Position = offset;
                Fill(stream, b, length);
            }
            return b;
        }

        // ------------------------------------------------------------------ the cache

        private void SaveCache(string file)
        {
            string tmp = file + ".tmp";
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (GZipStream gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (BinaryWriter w = new BinaryWriter(gz))
            {
                w.Write(0x4b503443); // "C4PK"
                w.Write(CacheVersion);
                w.Write(count);
                w.Write(nameBytes);
                w.Write(names, 0, nameBytes);
                for (int i = 0; i < count; i++)
                {
                    Entry e = entries[i];
                    w.Write(e.Name);
                    w.Write(e.NameLength);
                    w.Write(e.Method);
                    w.Write(e.Encrypted);
                    w.Write(e.Packed);
                    w.Write(e.Size);
                    w.Write(e.Offset);
                }
            }
            if (File.Exists(file))
                File.Delete(file);
            File.Move(tmp, file);
        }

        private void LoadCache(string file)
        {
            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (GZipStream gz = new GZipStream(fs, CompressionMode.Decompress))
            using (BinaryReader r = new BinaryReader(new BufferedStream(gz, 1 << 20)))
            {
                if (r.ReadInt32() != 0x4b503443 || r.ReadInt32() != CacheVersion)
                    throw new InvalidDataException("old cache");
                count = r.ReadInt32();
                nameBytes = r.ReadInt32();
                names = r.ReadBytes(nameBytes);
                if (names.Length != nameBytes || count < 0)
                    throw new InvalidDataException("short cache");
                entries = new Entry[Math.Max(16, count)];
                for (int i = 0; i < count; i++)
                {
                    entries[i] = new Entry { Name = r.ReadInt32(), NameLength = r.ReadUInt16(), Method = r.ReadByte(), Encrypted = r.ReadBoolean(), Packed = r.ReadInt64(), Size = r.ReadInt64(), Offset = r.ReadInt64() };
                    if (entries[i].Name < 0 || entries[i].Name + entries[i].NameLength > nameBytes)
                        throw new InvalidDataException("bad cache");
                }
            }
        }

        // ------------------------------------------------------------------ lookups

        private void BuildIndex()
        {
            int size = 16;
            while (size < count * 2)
                size <<= 1;
            buckets = new int[size];
            next = new int[Math.Max(1, count)];
            for (int i = 0; i < buckets.Length; i++)
                buckets[i] = -1;
            for (int i = 0; i < count; i++)
            {
                int b = (int)(Hash(names, entries[i].Name, entries[i].NameLength) & (uint)(size - 1));
                next[i] = buckets[b];
                buckets[b] = i;
            }
        }

        // Case-insensitive, '/' = '\'.
        private static uint Hash(byte[] d, int at, int len)
        {
            uint h = 2166136261;
            for (int i = 0; i < len; i++)
            {
                h ^= Fold(d[at + i]);
                h *= 16777619;
            }
            return h;
        }

        private static byte Fold(byte c)
        {
            if (c == (byte)'/')
                return (byte)'\\';
            if (c >= (byte)'A' && c <= (byte)'Z')
                return (byte)(c + 32);
            return c;
        }

        // "objects/x.cga", "Objects\x.cga" and "Data\Objects\x.cga" all name Data\Objects\x.cga.
        public static string Normalize(string name)
        {
            string n = (name ?? string.Empty).Replace('/', '\\').TrimStart('\\');
            if (!n.StartsWith("Data\\", StringComparison.OrdinalIgnoreCase))
                n = "Data\\" + n;
            return n;
        }

        private int Find(string name)
        {
            if (count == 0)
                return -1;
            byte[] key = Encoding.UTF8.GetBytes(Normalize(name));
            int b = (int)(Hash(key, 0, key.Length) & (uint)(buckets.Length - 1));
            for (int i = buckets[b]; i >= 0; i = next[i])
            {
                if (entries[i].NameLength != key.Length)
                    continue;
                bool same = true;
                for (int k = 0; k < key.Length && same; k++)
                    same = Fold(names[entries[i].Name + k]) == Fold(key[k]);
                if (same)
                    return i;
            }
            return -1;
        }

        public string NameOf(int i)
        {
            return Encoding.UTF8.GetString(names, entries[i].Name, entries[i].NameLength);
        }

        public bool Exists(string name)
        {
            return Find(name) >= 0;
        }

        public bool IsEncrypted(string name)
        {
            int i = Find(name);
            return i >= 0 && entries[i].Encrypted;
        }

        public long SizeOf(string name)
        {
            int i = Find(name);
            return i < 0 ? -1 : entries[i].Size;
        }

        // The entries under a folder ("Data\Objects\Spaceships\Ships\AEGS\"), as their full names.
        public List<string> List(string folder)
        {
            string f = Normalize(folder);
            if (!f.EndsWith("\\", StringComparison.Ordinal))
                f += "\\";
            byte[] key = Encoding.UTF8.GetBytes(f);
            List<string> list = new List<string>();
            for (int i = 0; i < count; i++)
            {
                if (entries[i].NameLength < key.Length)
                    continue;
                bool same = true;
                for (int k = 0; k < key.Length && same; k++)
                    same = Fold(names[entries[i].Name + k]) == Fold(key[k]);
                if (same)
                    list.Add(NameOf(i));
            }
            return list;
        }

        // The entry's bytes; null when there is none; EncryptedEntryException for an encrypted one.
        public byte[] Read(string name)
        {
            int i = Find(name);
            if (i < 0)
                return null;
            Entry e = entries[i];
            if (e.Encrypted)
            {
                lock (EncryptedAsked)
                    EncryptedAsked.Add(NameOf(i));
                throw new EncryptedEntryException(NameOf(i));
            }
            if (e.Packed > int.MaxValue || e.Size > int.MaxValue)
                throw new InvalidDataException(NameOf(i) + " is too large");
            byte[] head = ReadAt(e.Offset, 30);
            // (CIG writes PK 3 4 or PK 3 20 as the local header's mark; whether an entry is encrypted is told by the
            // central directory only, checked above. Until 2026-10-26 the second kind was not read: textures such as
            // the RSI ships' metals were drawn in a flat colour.)
            uint mark = BitConverter.ToUInt32(head, 0);
            if (mark != 0x04034b50 && mark != 0x14034b50)
                throw new InvalidDataException(NameOf(i) + ": bad local header");
            int skip = 30 + BitConverter.ToUInt16(head, 26) + BitConverter.ToUInt16(head, 28);
            byte[] data = ReadAt(e.Offset + skip, (int)e.Packed);
            return Unpack(data, e.Method, e.Size, NameOf(i), Zstd);
        }

        internal static byte[] Unpack(byte[] data, int method, long size, string name, Func<byte[], int, int, byte[]> zstd)
        {
            switch (method)
            {
                case Stored:
                    return data;
                case Deflated:
                    using (MemoryStream packed = new MemoryStream(data))
                    using (DeflateStream z = new DeflateStream(packed, CompressionMode.Decompress))
                    using (MemoryStream raw = new MemoryStream((int)Math.Max(0, Math.Min(int.MaxValue, size))))
                    {
                        z.CopyTo(raw);
                        return raw.ToArray();
                    }
                case Zstandard:
                    if (zstd == null)
                        throw new InvalidDataException(name + ": zstd is not available");
                    byte[] r = zstd(data, 0, data.Length);
                    if (size >= 0 && r.Length != size)
                        throw new InvalidDataException(name + ": unpacked to " + r.Length + " bytes, not " + size);
                    return r;
                default:
                    throw new InvalidDataException(name + ": unknown packing " + method);
            }
        }

        // ------------------------------------------------------------------ socpak (a plain ZIP in memory)

        // The files of a ZIP held in memory (an object container .socpak): name ('/' separated, lower case) -> bytes.
        public static Dictionary<string, byte[]> ReadZip(byte[] zip, Func<byte[], int, int, byte[]> zstd)
        {
            Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            int eocd = -1;
            for (int i = zip.Length - 22; i >= Math.Max(0, zip.Length - 65557); i--)
            {
                if (zip[i] == 0x50 && zip[i + 1] == 0x4b && zip[i + 2] == 5 && zip[i + 3] == 6)
                {
                    eocd = i;
                    break;
                }
            }
            if (eocd < 0)
                throw new InvalidDataException("not a ZIP");
            int n = BitConverter.ToUInt16(zip, eocd + 10);
            long p = BitConverter.ToUInt32(zip, eocd + 16);
            for (int e = 0; e < n && p + 46 <= zip.Length; e++)
            {
                if (BitConverter.ToUInt32(zip, (int)p) != 0x02014b50)
                    break;
                int at = (int)p;
                int method = BitConverter.ToUInt16(zip, at + 10);
                long packed = BitConverter.ToUInt32(zip, at + 20), size = BitConverter.ToUInt32(zip, at + 24);
                int nlen = BitConverter.ToUInt16(zip, at + 28), xlen = BitConverter.ToUInt16(zip, at + 30), clen = BitConverter.ToUInt16(zip, at + 32);
                long offset = BitConverter.ToUInt32(zip, at + 42);
                bool enc;
                Extras(zip, at + 46 + nlen, xlen, false, ref size, ref packed, ref offset, out enc);
                string name = Encoding.UTF8.GetString(zip, at + 46, nlen).Replace('\\', '/');
                p = at + 46 + nlen + xlen + clen;
                if (offset + 30 > zip.Length)
                    continue;
                int lo = (int)offset;
                int skip = 30 + BitConverter.ToUInt16(zip, lo + 26) + BitConverter.ToUInt16(zip, lo + 28);
                if (lo + skip + packed > zip.Length || name.EndsWith("/", StringComparison.Ordinal))
                    continue;
                byte[] data = new byte[packed];
                Buffer.BlockCopy(zip, lo + skip, data, 0, (int)packed);
                try
                {
                    files[name] = Unpack(data, method, size, name, zstd);
                }
                catch (InvalidDataException)
                {
                }
            }
            return files;
        }

        // ------------------------------------------------------------------ the game must not run

        // The running Star Citizen game process, if any (we never read the archive while the game runs).
        public static string RunningGame()
        {
            foreach (string name in new[] { "StarCitizen", "StarCitizen_Launcher" })
            {
                Process[] ps = new Process[0];
                try
                {
                    ps = Process.GetProcessesByName(name);
                    if (ps.Length > 0)
                        return name + ".exe";
                }
                catch (Exception)
                {
                }
                finally
                {
                    foreach (Process p in ps)
                        p.Dispose();
                }
            }
            return string.Empty;
        }
    }
}
