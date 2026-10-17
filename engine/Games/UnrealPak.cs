using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace JoyPro.Engine.Games
{
    // Read-only access to an Unreal Engine 4 .pak file (version 10/11 index with a full directory index, unencrypted),
    // as far as JoyPro needs it: small config files such as <Project>/Config/DefaultInput.ini (Project Wingman,
    // docs/games/project-wingman.md). Entries stored or zlib compressed; encrypted entries are not read. Only the footer,
    // the index and the requested entries are read, so a 17 GB pak costs a few hundred KB of I/O.
    internal sealed class UnrealPak : IDisposable
    {
        private const uint Magic = 0x5A6F12E1;
        private const int FooterSize = 221;   // guid 16, encrypted 1, magic 4, version 4, offset 8, size 8, hash 20, 5 x 32 method names

        private readonly FileStream stream;
        private readonly string[] methods = new string[5];
        private byte[] encodedEntries;
        public int Version { get; private set; }
        public string MountPoint { get; private set; }
        // Full path (mount point removed, "/" separated) -> offset of its encoded entry.
        public Dictionary<string, int> Files { get; private set; }

        private UnrealPak(string path)
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Files = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        public static UnrealPak Open(string path)
        {
            UnrealPak pak = new UnrealPak(path);
            try
            {
                pak.ReadIndex();
                return pak;
            }
            catch
            {
                pak.Dispose();
                throw;
            }
        }

        private void ReadIndex()
        {
            if (stream.Length < FooterSize)
                throw new InvalidDataException("Too small for a pak file.");
            byte[] footer = ReadAt(stream.Length - FooterSize, FooterSize);
            if (BitConverter.ToUInt32(footer, 17) != Magic)
                throw new InvalidDataException("No pak footer (only UE4 pak version 10/11 is read).");
            if (footer[16] != 0)
                throw new InvalidDataException("The pak index is encrypted.");
            Version = (int)BitConverter.ToUInt32(footer, 21);
            if (Version < 10)
                throw new InvalidDataException("Pak version " + Version + " is not read (10 and 11 are).");
            long indexOffset = BitConverter.ToInt64(footer, 25);
            long indexSize = BitConverter.ToInt64(footer, 33);
            for (int i = 0; i < 5; i++)
                methods[i] = Encoding.ASCII.GetString(footer, 61 + 32 * i, 32).TrimEnd('\0');
            if (indexOffset < 0 || indexSize <= 0 || indexOffset + indexSize > stream.Length || indexSize > 256L * 1024 * 1024)
                throw new InvalidDataException("Bad pak index position.");
            byte[] index = ReadAt(indexOffset, (int)indexSize);
            int p = 0;
            MountPoint = ReadString(index, ref p);
            p += 4;   // entry count
            p += 8;   // path hash seed
            if (BitConverter.ToInt32(index, p) != 0) p += 4 + 8 + 8 + 20; else p += 4;
            bool hasDirectoryIndex = BitConverter.ToInt32(index, p) != 0;
            p += 4;
            if (!hasDirectoryIndex)
                throw new InvalidDataException("The pak has no full directory index.");
            long dirOffset = BitConverter.ToInt64(index, p);
            long dirSize = BitConverter.ToInt64(index, p + 8);
            p += 16 + 20;
            int encodedSize = BitConverter.ToInt32(index, p);
            p += 4;
            encodedEntries = new byte[encodedSize];
            Buffer.BlockCopy(index, p, encodedEntries, 0, encodedSize);
            if (dirOffset < 0 || dirSize <= 0 || dirOffset + dirSize > stream.Length || dirSize > 256L * 1024 * 1024)
                throw new InvalidDataException("Bad pak directory index position.");
            byte[] dirs = ReadAt(dirOffset, (int)dirSize);
            int q = 0;
            int dirCount = BitConverter.ToInt32(dirs, q);
            q += 4;
            for (int d = 0; d < dirCount; d++)
            {
                string dir = ReadString(dirs, ref q);
                int fileCount = BitConverter.ToInt32(dirs, q);
                q += 4;
                for (int f = 0; f < fileCount; f++)
                {
                    string name = ReadString(dirs, ref q);
                    int entry = BitConverter.ToInt32(dirs, q);
                    q += 4;
                    Files[(dir + name).TrimStart('/')] = entry;
                }
            }
        }

        // The bytes of a file in the pak (path relative to the mount point, e.g. "ProjectWingman/Config/DefaultInput.ini").
        public byte[] Read(string path)
        {
            int entry;
            if (!Files.TryGetValue(path.Replace('\\', '/').TrimStart('/'), out entry))
                throw new FileNotFoundException("Not in the pak: " + path);
            int e = entry;
            uint bits = BitConverter.ToUInt32(encodedEntries, e);
            e += 4;
            if ((bits & 0x3f) == 0x3f)
                e += 4;   // explicit block size, not needed
            int blockCount = (int)((bits >> 6) & 0xffff);
            bool encrypted = ((bits >> 22) & 1) != 0;
            int method = (int)((bits >> 23) & 0x3f);
            long offset = ReadVar(ref e, ((bits >> 31) & 1) != 0);
            long uncompressed = ReadVar(ref e, ((bits >> 30) & 1) != 0);
            long compressed = method != 0 ? ReadVar(ref e, ((bits >> 29) & 1) != 0) : uncompressed;
            if (encrypted)
                throw new InvalidDataException("Encrypted pak entry: " + path);
            if (uncompressed > 64L * 1024 * 1024)
                throw new InvalidDataException("Pak entry too large for JoyPro: " + path);
            List<long> blockSizes = new List<long>();
            if (blockCount > 0 && blockCount != 1)
            {
                for (int i = 0; i < blockCount; i++)
                {
                    blockSizes.Add(BitConverter.ToUInt32(encodedEntries, e));
                    e += 4;
                }
            }
            else if (blockCount == 1)
                blockSizes.Add(compressed);
            // The data follows the entry's own serialized header in the file.
            int header = 8 + 8 + 8 + 4 + 20 + (method != 0 ? 4 + 16 * blockCount : 0) + 1 + 4;
            if (method == 0)
                return ReadAt(offset + header, (int)uncompressed);
            string name = method - 1 < methods.Length ? methods[method - 1] : string.Empty;
            if (!string.Equals(name, "Zlib", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pak entry compressed with '" + name + "' (only zlib is read): " + path);
            MemoryStream output = new MemoryStream((int)uncompressed);
            long at = offset + header;
            foreach (long size in blockSizes)
            {
                byte[] raw = ReadAt(at, (int)size);
                at += size;
                using (DeflateStream inflate = new DeflateStream(new MemoryStream(raw, 2, raw.Length - 2), CompressionMode.Decompress))
                    inflate.CopyTo(output);
            }
            return output.ToArray();
        }

        // A text file of the pak (UTF-8, UTF-16 with BOM).
        public string ReadText(string path)
        {
            byte[] bytes = Read(path);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return Encoding.UTF8.GetString(bytes);
        }

        private long ReadVar(ref int e, bool fits32)
        {
            if (fits32)
            {
                long v = BitConverter.ToUInt32(encodedEntries, e);
                e += 4;
                return v;
            }
            long w = BitConverter.ToInt64(encodedEntries, e);
            e += 8;
            return w;
        }

        private byte[] ReadAt(long offset, int count)
        {
            byte[] buffer = new byte[count];
            stream.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0)
                    throw new EndOfStreamException("Pak ends early.");
                read += n;
            }
            return buffer;
        }

        private static string ReadString(byte[] data, ref int p)
        {
            int length = BitConverter.ToInt32(data, p);
            p += 4;
            string text;
            if (length < 0)
            {
                text = Encoding.Unicode.GetString(data, p, -2 * length);
                p += -2 * length;
            }
            else
            {
                text = Encoding.UTF8.GetString(data, p, length);
                p += length;
            }
            return text.TrimEnd('\0');
        }

        public void Dispose()
        {
            stream.Dispose();
        }
    }
}
