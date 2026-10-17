using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace JoyPro.Engine.Games
{
    // One file in an Enfusion .pak archive.
    internal sealed class EnfusionPakEntry
    {
        public string Path;
        public long Offset;
        public int PackedSize;
        public int Size;
        // 0 stored, 1 zlib.
        public int Compression;
    }

    // Reads files from Arma Reforger's Enfusion *.pak archives (addons\data\data*.pak, addons\core\data.pak). Read only;
    // used by the catalog refresh. Layout (seen in Reforger 1.9 Steam, HEAD version 0x10003; matches the community
    // descriptions of the format, e.g. the enfusion_pak crate and the Go package pac1):
    //   "FORM" + big-endian uint32 size of the rest, "PAC1", then chunks of 4-byte tag + big-endian uint32 size:
    //   HEAD (version and hashes), DATA (the file contents), FILE (the directory tree).
    //   FILE: a tree of entries, starting with the unnamed root folder: byte type (0 folder, 1 file), byte name length,
    //   name (UTF-8); a folder then has a little-endian uint32 child count, a file 24 bytes: uint32 absolute offset,
    //   uint32 stored size, uint32 size, uint32 (0), 2 bytes (0), byte compression (0 stored, 1 zlib), byte level,
    //   uint32 time stamp.
    internal static class EnfusionPak
    {
        public static List<EnfusionPakEntry> ReadDirectory(string pak)
        {
            using (FileStream stream = new FileStream(pak, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] header = ReadExactly(stream, 12);
                if (Encoding.ASCII.GetString(header, 0, 4) != "FORM" || Encoding.ASCII.GetString(header, 8, 4) != "PAC1")
                    throw new InvalidDataException(pak + " is not an Enfusion PAK (FORM/PAC1) archive.");
                long end = Math.Min(stream.Length, 8L + BigEndian(header, 4));
                long position = 12;
                while (position + 8 <= end)
                {
                    stream.Position = position;
                    byte[] chunk = ReadExactly(stream, 8);
                    string tag = Encoding.ASCII.GetString(chunk, 0, 4);
                    long size = BigEndian(chunk, 4);
                    if (tag == "FILE")
                    {
                        if (size > int.MaxValue || position + 8 + size > stream.Length)
                            throw new InvalidDataException("The FILE chunk of " + pak + " is cut off.");
                        return ParseTree(ReadExactly(stream, (int)size), pak);
                    }
                    position += 8 + size;
                }
                throw new InvalidDataException(pak + " has no FILE chunk.");
            }
        }

        public static byte[] Read(string pak, EnfusionPakEntry entry)
        {
            using (FileStream stream = new FileStream(pak, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Position = entry.Offset;
                byte[] raw = ReadExactly(stream, entry.PackedSize);
                if (entry.Compression == 0)
                    return raw;
                if (entry.Compression != 1 || raw.Length < 2)
                    throw new InvalidDataException(entry.Path + " in " + pak + " uses an unknown compression (" + entry.Compression + ").");
                // zlib: 2-byte header, deflate data, Adler-32 (not checked).
                using (DeflateStream inflate = new DeflateStream(new MemoryStream(raw, 2, raw.Length - 2), CompressionMode.Decompress))
                    return ReadExactly(inflate, entry.Size);
            }
        }

        // The wanted paths (case-insensitive, '/' separated) from several archives; a later archive replaces a file an
        // earlier one has. Archives that cannot be read are reported and skipped.
        public static Dictionary<string, byte[]> ReadFiles(IEnumerable<string> paks, ICollection<string> wanted, List<string> warnings)
        {
            HashSet<string> paths = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string pak in paks)
            {
                try
                {
                    foreach (EnfusionPakEntry entry in ReadDirectory(pak).Where(e => paths.Contains(e.Path)))
                        files[entry.Path] = Read(pak, entry);
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                {
                    warnings.Add("Could not read " + pak + ": " + ex.Message);
                }
            }
            return files;
        }

        private static List<EnfusionPakEntry> ParseTree(byte[] data, string pak)
        {
            List<EnfusionPakEntry> entries = new List<EnfusionPakEntry>();
            int position = 0;
            ParseNode(data, ref position, string.Empty, entries, 0, pak);
            return entries;
        }

        private static void ParseNode(byte[] data, ref int position, string parent, List<EnfusionPakEntry> entries, int depth, string pak)
        {
            if (depth > 64 || position + 2 > data.Length)
                throw new InvalidDataException("The directory of " + pak + " is damaged.");
            int type = data[position];
            int nameLength = data[position + 1];
            position += 2;
            if (position + nameLength > data.Length)
                throw new InvalidDataException("The directory of " + pak + " is damaged.");
            string name = Encoding.UTF8.GetString(data, position, nameLength);
            position += nameLength;
            string path = parent.Length == 0 ? name : name.Length == 0 ? parent : parent + "/" + name;
            if (type == 0)
            {
                if (position + 4 > data.Length)
                    throw new InvalidDataException("The directory of " + pak + " is damaged.");
                uint count = BitConverter.ToUInt32(data, position);
                position += 4;
                for (uint i = 0; i < count; i++)
                    ParseNode(data, ref position, path, entries, depth + 1, pak);
                return;
            }
            if (type != 1 || position + 24 > data.Length)
                throw new InvalidDataException("The directory of " + pak + " is damaged.");
            entries.Add(new EnfusionPakEntry
            {
                Path = path,
                Offset = BitConverter.ToUInt32(data, position),
                PackedSize = checked((int)BitConverter.ToUInt32(data, position + 4)),
                Size = checked((int)BitConverter.ToUInt32(data, position + 8)),
                Compression = data[position + 18]
            });
            position += 24;
        }

        private static long BigEndian(byte[] data, int offset)
        {
            return ((long)data[offset] << 24) | ((long)data[offset + 1] << 16) | ((long)data[offset + 2] << 8) | data[offset + 3];
        }

        private static byte[] ReadExactly(Stream stream, int size)
        {
            byte[] data = new byte[size];
            int read = 0;
            while (read < size)
            {
                int n = stream.Read(data, read, size - read);
                if (n <= 0)
                    throw new InvalidDataException("Unexpected end of data in an Enfusion PAK archive.");
                read += n;
            }
            return data;
        }
    }

    // A node of an Enfusion text file (.conf, .layout, string tables): "Head tokens {" ... "}" is a block, any other line
    // a value (its tokens; quoted strings without quotes, "\"" inside a string is a quote, a "\" after a string continues
    // it with the next quoted string).
    internal sealed class EnfusionNode
    {
        public List<string> Head = new List<string>();
        public List<EnfusionNode> Children;

        public string Name
        {
            get { return Head.Count > 0 ? Head[0] : string.Empty; }
        }

        public IEnumerable<EnfusionNode> Blocks(string name)
        {
            return (Children ?? new List<EnfusionNode>()).Where(c => c.Children != null && c.Name == name);
        }

        public EnfusionNode Block(string name)
        {
            return Blocks(name).FirstOrDefault();
        }

        // The value of the first "Key value" line.
        public string Value(string key)
        {
            EnfusionNode line = (Children ?? new List<EnfusionNode>()).FirstOrDefault(c => c.Children == null && c.Name == key && c.Head.Count > 1);
            return line == null ? string.Empty : line.Head[1];
        }

        public IEnumerable<EnfusionNode> Descendants()
        {
            foreach (EnfusionNode child in Children ?? new List<EnfusionNode>())
            {
                yield return child;
                foreach (EnfusionNode below in child.Descendants())
                    yield return below;
            }
        }

        public static EnfusionNode Parse(string text)
        {
            EnfusionNode root = new EnfusionNode { Children = new List<EnfusionNode>() };
            Stack<EnfusionNode> open = new Stack<EnfusionNode>();
            open.Push(root);
            List<string> tokens = new List<string>();
            bool continued = false;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\n')
                {
                    Flush(open.Peek(), tokens);
                    i++;
                }
                else if (char.IsWhiteSpace(c))
                    i++;
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n')
                        i++;
                }
                else if (c == '{')
                {
                    EnfusionNode block = new EnfusionNode { Head = new List<string>(tokens), Children = new List<EnfusionNode>() };
                    tokens.Clear();
                    open.Peek().Children.Add(block);
                    open.Push(block);
                    i++;
                }
                else if (c == '}')
                {
                    Flush(open.Peek(), tokens);
                    if (open.Count > 1)
                        open.Pop();
                    i++;
                }
                else if (c == '"')
                {
                    StringBuilder value = new StringBuilder();
                    i++;
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '"')
                        {
                            value.Append('"');
                            i += 2;
                        }
                        else
                            value.Append(text[i++]);
                    }
                    i++;
                    if (continued && tokens.Count > 0)
                        tokens[tokens.Count - 1] += value.ToString();
                    else
                        tokens.Add(value.ToString());
                    continued = false;
                    // "..."\ at the end of a line: the string goes on in the next quoted string.
                    int j = i;
                    while (j < text.Length && (text[j] == ' ' || text[j] == '\t'))
                        j++;
                    if (j < text.Length && text[j] == '\\')
                    {
                        j++;
                        while (j < text.Length && (text[j] == ' ' || text[j] == '\t' || text[j] == '\r'))
                            j++;
                        if (j < text.Length && text[j] == '\n')
                        {
                            continued = true;
                            i = j + 1;
                        }
                    }
                }
                else
                {
                    int start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}' && text[i] != '"')
                        i++;
                    tokens.Add(text.Substring(start, i - start));
                }
            }
            Flush(open.Peek(), tokens);
            return root;
        }

        private static void Flush(EnfusionNode parent, List<string> tokens)
        {
            if (tokens.Count == 0)
                return;
            parent.Children.Add(new EnfusionNode { Head = new List<string>(tokens) });
            tokens.Clear();
        }
    }
}
