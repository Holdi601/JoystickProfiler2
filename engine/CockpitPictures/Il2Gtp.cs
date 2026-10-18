#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.CockpitPictures
{
    // The data archives of IL-2 Sturmovik: Great Battles (data\*.gtp), read only. Each is a plain container: a header
    // ("S16R!", the archive size and the record count), a directory ("STRMFAT?" and DIR!/FILE records with the full path
    // of every file) and the files, each behind a 32-byte "STRMFILE" header, stored as they are. Archives of another
    // layout (IL-2 Korea's) are left alone.
    internal sealed class Il2Gtp : IDisposable
    {
        public sealed class Entry
        {
            public string Path = string.Empty;  // "/graphics/planes/bf109f4/bf109f4-c.mgm" (lower case)
            public string Archive = string.Empty;
            public long Offset;
            public long Size;
        }

        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FileStream> streams = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();
        public readonly List<string> Archives = new List<string>();

        public int Count { get { return entries.Count; } }

        // Indexes every archive of the game's data folder (later archives win for a path).
        public static Il2Gtp Open(string dataFolder)
        {
            Il2Gtp g = new Il2Gtp();
            if (string.IsNullOrWhiteSpace(dataFolder) || !Directory.Exists(dataFolder))
                return g;
            foreach (string file in Directory.GetFiles(dataFolder, "*.gtp").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    g.AddArchive(file);
                }
                catch (Exception)
                {
                    // An unreadable archive is skipped.
                }
            }
            return g;
        }

        public void AddArchive(string file)
        {
            byte[] dir;
            using (FileStream s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                dir = ReadDirectoryBytes(s);
            if (dir == null)
                return;
            foreach (Entry e in ParseDirectory(dir))
            {
                e.Archive = file;
                entries[e.Path] = e;
            }
            Archives.Add(file);
        }

        // The directory of an archive (the bytes after the 64-byte header), or null for another layout.
        public static byte[] ReadDirectoryBytes(Stream s)
        {
            byte[] head = new byte[0x40];
            if (s.Read(head, 0, head.Length) != head.Length)
                return null;
            if (Encoding.ASCII.GetString(head, 0, 5) != "S16R!" || Encoding.ASCII.GetString(head, 0x20, 8) != "STRMFAT?")
                return null;
            long size = BitConverter.ToUInt32(head, 0x28);
            if (size <= 0 || size > 256L * 1024 * 1024 || 0x40 + size > s.Length)
                return null;
            byte[] dir = new byte[size];
            int read = 0;
            while (read < dir.Length)
            {
                int n = s.Read(dir, read, dir.Length - read);
                if (n <= 0)
                    return null;
                read += n;
            }
            return dir;
        }

        // DIR!/FILE records: tag, u32 kind, u32 offset (files) , u32 size, u32 hash, u32 parent, u64 name length, the
        // full path (zero terminated).
        public static List<Entry> ParseDirectory(byte[] dir)
        {
            List<Entry> list = new List<Entry>();
            int o = 0;
            while (o + 32 <= dir.Length)
            {
                string tag = Encoding.ASCII.GetString(dir, o, 4);
                if (tag != "DIR!" && tag != "FILE")
                    break;
                long offset = BitConverter.ToUInt32(dir, o + 8);
                long size = BitConverter.ToUInt32(dir, o + 12);
                long nameLength = (long)BitConverter.ToUInt64(dir, o + 24);
                if (nameLength < 0 || o + 32 + nameLength > dir.Length)
                    break;
                string name = Encoding.GetEncoding(28591).GetString(dir, o + 32, (int)nameLength).TrimEnd('\0');
                if (tag == "FILE" && name.Length > 0)
                    list.Add(new Entry { Path = Normalize(name), Offset = offset, Size = size });
                o += 32 + (int)nameLength;
            }
            return list;
        }

        public static string Normalize(string path)
        {
            string p = (path ?? string.Empty).Replace('\\', '/').Trim().ToLowerInvariant();
            while (p.Contains("//"))
                p = p.Replace("//", "/");
            return p.StartsWith("/", StringComparison.Ordinal) ? p : "/" + p;
        }

        public bool Has(string path)
        {
            return entries.ContainsKey(Normalize(path));
        }

        public Entry Find(string path)
        {
            Entry e;
            return entries.TryGetValue(Normalize(path), out e) ? e : null;
        }

        public IEnumerable<string> Paths(string prefix)
        {
            string p = Normalize(prefix);
            return entries.Keys.Where(k => k.StartsWith(p, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal);
        }

        public IEnumerable<string> AllPaths { get { return entries.Keys; } }

        // A file's bytes, or null.
        public byte[] Read(string path)
        {
            Entry e = Find(path);
            if (e == null || e.Size < 0 || e.Size > int.MaxValue)
                return null;
            lock (gate)
            {
                try
                {
                    FileStream s;
                    if (!streams.TryGetValue(e.Archive, out s))
                    {
                        s = new FileStream(e.Archive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        streams[e.Archive] = s;
                    }
                    s.Position = e.Offset;
                    byte[] head = new byte[32];
                    if (s.Read(head, 0, 32) != 32 || Encoding.ASCII.GetString(head, 0, 8) != "STRMFILE")
                        return null;
                    byte[] data = new byte[e.Size];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = s.Read(data, read, data.Length - read);
                        if (n <= 0)
                            return null;
                        read += n;
                    }
                    return data;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public string ReadText(string path, Encoding encoding = null)
        {
            byte[] d = Read(path);
            if (d == null)
                return null;
            if (d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE)
                return Encoding.Unicode.GetString(d, 2, d.Length - 2);
            if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF)
                return Encoding.UTF8.GetString(d, 3, d.Length - 3);
            return (encoding ?? Encoding.GetEncoding(28591)).GetString(d);
        }

        public void Dispose()
        {
            lock (gate)
            {
                foreach (FileStream s in streams.Values)
                    s.Dispose();
                streams.Clear();
            }
        }
    }
}
