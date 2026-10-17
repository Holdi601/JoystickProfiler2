using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using ZstdSharp;

namespace JoyPro.Engine.Games
{
    // Reads files from War Thunder's *.vromfs.bin archives (Dagor Engine, open source: prog/engine/ioSys/vromfsLoad.cpp,
    // prog/dagorInclude/supp/dag_zstdObfuscate.h, osApiWrappers/dag_vromfs.h):
    //   header  'VRFs' | 'VRFx', target ("\0\0PC"), full size, hw32 (bits 0-29 packed size, bit 30 zstd, bit 31 signed);
    //           VRFx adds an extension header (uint16 size, uint16 flags, uint32 version) of 'size' bytes.
    //   body    zstd (first and last 16 bytes masked as in dag_zstdObfuscate.h) or zlib or stored; the decompressed body starts with
    //           (names offset, names count, -, -, data offset, data count): names = uint64 offsets of C strings,
    //           data = (uint32 offset, uint32 size, 8 bytes padding) per file.
    // Read only; used by the catalog refresh (scripts/controls/shortcutslist/*.nut in gui.vromfs.bin,
    // lang/controls.csv in lang.vromfs.bin).
    internal static class WarThunderVromfs
    {
        private static readonly uint[] XorKey = { 0xAA55AA55u, 0xF00FF00Fu, 0xAA55AA55u, 0x12481248u };

        public static Dictionary<string, byte[]> ReadFiles(string archive, Func<string, bool> wanted)
        {
            byte[] body = ReadBody(File.ReadAllBytes(archive));
            Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            int namesOffset = BitConverter.ToInt32(body, 0);
            int namesCount = BitConverter.ToInt32(body, 4);
            int dataOffset = BitConverter.ToInt32(body, 16);
            for (int i = 0; i < namesCount; i++)
            {
                long nameAt = BitConverter.ToInt64(body, namesOffset + 8 * i);
                int end = Array.IndexOf(body, (byte)0, (int)nameAt);
                string name = Encoding.UTF8.GetString(body, (int)nameAt, end - (int)nameAt);
                if (!wanted(name))
                    continue;
                int at = BitConverter.ToInt32(body, dataOffset + 16 * i);
                int size = BitConverter.ToInt32(body, dataOffset + 16 * i + 4);
                byte[] data = new byte[size];
                Buffer.BlockCopy(body, at, data, 0, size);
                files[name] = data;
            }
            return files;
        }

        private static byte[] ReadBody(byte[] file)
        {
            string label = Encoding.ASCII.GetString(file, 0, 4);
            if (label != "VRFs" && label != "VRFx")
                throw new InvalidDataException("Not a vromfs archive.");
            int fullSize = BitConverter.ToInt32(file, 8);
            uint hw = BitConverter.ToUInt32(file, 12);
            int position = 16;
            if (label == "VRFx")
                position += BitConverter.ToUInt16(file, 16);
            int packed = (int)(hw & 0x3FFFFFFFu);
            if (packed == 0)
            {
                byte[] stored = new byte[fullSize];
                Buffer.BlockCopy(file, position, stored, 0, fullSize);
                return stored;
            }

            byte[] raw = new byte[packed];
            Buffer.BlockCopy(file, position, raw, 0, packed);
            if ((hw & 0x40000000u) != 0)
            {
                Deobfuscate(raw);
                using (DecompressionStream zstd = new DecompressionStream(new MemoryStream(raw)))
                    return ReadAll(zstd, fullSize);
            }
            // zlib: skip the 2-byte header, the rest is deflate.
            using (DeflateStream inflate = new DeflateStream(new MemoryStream(raw, 2, raw.Length - 2), CompressionMode.Decompress))
                return ReadAll(inflate, fullSize);
        }

        private static byte[] ReadAll(Stream stream, int size)
        {
            byte[] data = new byte[size];
            int read = 0;
            while (read < size)
            {
                int n = stream.Read(data, read, size - read);
                if (n <= 0)
                    break;
                read += n;
            }
            if (read != size)
                throw new InvalidDataException("vromfs body is shorter than its header says.");
            return data;
        }

        private static void Deobfuscate(byte[] data)
        {
            if (data.Length < 16)
                return;
            Xor(data, 0, XorKey[0], XorKey[1], XorKey[2], XorKey[3]);
            if (data.Length >= 32)
                Xor(data, (data.Length / 4 - 4) * 4, XorKey[3], XorKey[2], XorKey[1], XorKey[0]);
        }

        private static void Xor(byte[] data, int offset, uint a, uint b, uint c, uint d)
        {
            uint[] key = { a, b, c, d };
            for (int i = 0; i < 4; i++)
            {
                uint word = BitConverter.ToUInt32(data, offset + 4 * i) ^ key[i];
                byte[] bytes = BitConverter.GetBytes(word);
                Buffer.BlockCopy(bytes, 0, data, offset + 4 * i, 4);
            }
        }
    }
}
