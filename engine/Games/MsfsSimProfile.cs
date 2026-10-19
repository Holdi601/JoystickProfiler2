using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace JoyPro.Engine.Games
{
    // The sim's own record of the devices (profile_00), read only (docs/games/msfs.md "profile_00"). A binary tree: a
    // node is an 8-byte name hash, a uint32 size (header included) and a payload; a container holds a uint32 count and
    // child nodes, a leaf a value (strings NUL-terminated). Fields JoyPro reads (MSFS 2024 Steam copy and a tester's
    // Microsoft Store save, 2026-10-19):
    //   - a device node: instance GUID (string; keyboard and mouse: a number), type (1 = joystick, 15 = keyboard/mouse),
    //     product ID, composite ID;
    //   - per device and aircraft configuration a record (device fields + aircraft name) with three preset slots
    //     (general, category with its category name, aircraft); a slot is a device node inside the record whose preset
    //     field holds a file name ("keyboard_2024_planes.xml", the shipped file name in lower case; "inputprofile_<n>"
    //     for an own profile) or a 0 byte (none).
    // JoyPro never writes this file: it carries a checksum, and a file the sim rejects resets the user's sim profile.
    internal sealed class MsfsSimProfile
    {
        private static readonly byte[] HashGuid = { 0xE4, 0x94, 0xA0, 0x78, 0x27, 0x80, 0x95, 0x46 };
        private static readonly byte[] HashProductId = { 0x9C, 0x6A, 0x10, 0x70, 0x54, 0x98, 0x09, 0x63 };
        private static readonly byte[] HashType = { 0xC5, 0xE2, 0x91, 0x53, 0xAB, 0x70, 0xCE, 0x33 };
        private static readonly byte[] HashPreset = { 0xE5, 0x37, 0x4A, 0x44, 0x3E, 0xE0, 0x98, 0x6E };
        private static readonly byte[] HashCategory = { 0xDD, 0x0E, 0x07, 0xAE, 0x6A, 0xD0, 0xA4, 0xFC };
        private static readonly byte[] HashAircraft = { 0x6E, 0xF4, 0x8B, 0x11, 0x82, 0x0C, 0x12, 0x5D };
        private static readonly byte[] HashGeneralSlot = { 0xAE, 0x7F, 0x5B, 0x37, 0xA5, 0x6A, 0x78, 0x1C };
        private static readonly byte[] HashCategorySlot = { 0x8B, 0x57, 0x82, 0x17, 0x91, 0x38, 0x94, 0x04 };
        private static readonly byte[] HashAircraftSlot = { 0x6B, 0xC4, 0xC8, 0x35, 0x74, 0x97, 0x36, 0xA7 };

        // One record of a device (one aircraft configuration): the file names its slots hold (lower case; null = none).
        internal sealed class Record
        {
            public string Aircraft;
            public string Category;
            public string General;
            public string CategoryPreset;
            public string AircraftPreset;
        }

        // The product ID the sim noted per device instance GUID (joysticks only).
        public readonly Dictionary<Guid, int> ProductIds = new Dictionary<Guid, int>();
        // Per device: its records (the sim has decided for these devices).
        public readonly Dictionary<Guid, List<Record>> Records = new Dictionary<Guid, List<Record>>();
        // Where the records came from (to say so in a step).
        public readonly List<string> Sources = new List<string>();

        public bool Knows(Guid device)
        {
            return Records.ContainsKey(device);
        }

        public List<Record> RecordsOf(Guid device)
        {
            List<Record> records;
            return Records.TryGetValue(device, out records) ? records : new List<Record>();
        }

        // The sim's records of one sim: profile_00 of every Steam user (remote folder) and every blob of the Microsoft
        // Store save folder(s) that holds the same tree (plain or zlib/gzip-compressed).
        public static MsfsSimProfile Load(int sim, IEnumerable<string> extraWgsFolders)
        {
            MsfsSimProfile profile = new MsfsSimProfile();
            foreach (string remote in MsfsUserProfiles.SteamRemoteFolders(sim, new List<string>()))
            {
                string file = Path.Combine(remote, "profile_00");
                byte[] data = ReadSmall(file);
                if (data != null && profile.Read(data))
                    profile.Sources.Add(file);
            }
            List<string> wgsFolders = new List<string> { MsfsUserProfiles.StoreWgsFolder(sim) };
            if (extraWgsFolders != null)
                wgsFolders.AddRange(extraWgsFolders.Where(f => !string.IsNullOrWhiteSpace(f)));
            foreach (string wgs in wgsFolders.Select(f => Path.GetFullPath(f).TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(wgs))
                    continue;
                foreach (WgsBlob blob in WgsStore.Blobs(wgs, new List<string>()))
                {
                    byte[] data = ReadSmall(blob.FilePath);
                    if (data == null)
                        continue;
                    if (profile.Read(data) || profile.Read(Inflate(data)))
                        profile.Sources.Add(blob.FilePath);
                }
            }
            return profile;
        }

        // Reads one profile_00 into this record; false when the bytes are no such tree (an input profile, another blob).
        public bool Read(byte[] data)
        {
            if (data == null || data.Length < 16)
                return false;
            int rootSize = BitConverter.ToInt32(data, 8);
            // The tree fills the file but for a short checksum at the end.
            if (rootSize < 16 || rootSize > data.Length || data.Length - rootSize > 64)
                return false;
            List<Node> found = new List<Node>();
            if (!Walk(data, 0, rootSize, null, found, 0))
                return false;
            if (!found.Any(n => n.Type >= 0))
                return false;
            foreach (Node node in found)
            {
                Guid guid;
                if (node.GuidText == null || !Guid.TryParse(node.GuidText, out guid))
                    continue;
                if (node.Type == 1 && node.ProductId >= 0 && node.ProductId <= 0xFFFF && !ProductIds.ContainsKey(guid))
                    ProductIds[guid] = node.ProductId;
                // A record: a device node with slots of the same device inside.
                if (node.InRecord || node.Slots.Count == 0)
                    continue;
                Record record = new Record { Aircraft = node.Aircraft };
                foreach (Node slot in node.Slots)
                {
                    string preset = string.IsNullOrEmpty(slot.Preset) ? null : slot.Preset.ToLowerInvariant();
                    if (slot.SlotKind == 'G')
                        record.General = preset;
                    else if (slot.SlotKind == 'C')
                    {
                        record.CategoryPreset = preset;
                        record.Category = slot.Category;
                    }
                    else if (slot.SlotKind == 'A')
                        record.AircraftPreset = preset;
                }
                List<Record> records;
                if (!Records.TryGetValue(guid, out records))
                    Records[guid] = records = new List<Record>();
                records.Add(record);
            }
            return true;
        }

        private sealed class Node
        {
            public string GuidText;
            public int ProductId = -1;
            public int Type = -1;
            public bool HasPreset;
            public string Preset;
            public string Category;
            public string Aircraft;
            public bool InRecord;
            // 'G' general, 'C' category, 'A' aircraft slot (the slot node's own name hash).
            public char SlotKind;
            public readonly List<Node> Slots = new List<Node>();
        }

        // False when the bytes are no well-formed container (the size of the children must fill it exactly).
        private static bool Walk(byte[] d, int pos, int size, Node parent, List<Node> found, int depth)
        {
            if (depth > 32 || size < 16)
                return false;
            int end = pos + size;
            int count = BitConverter.ToInt32(d, pos + 12);
            if (count <= 0 || count > 100000)
                return false;
            List<KeyValuePair<int, int>> kids = new List<KeyValuePair<int, int>>();
            int p = pos + 16;
            for (int i = 0; i < count; i++)
            {
                if (p + 12 > end)
                    return false;
                int s = BitConverter.ToInt32(d, p + 8);
                if (s < 12 || p + s > end)
                    return false;
                kids.Add(new KeyValuePair<int, int>(p, s));
                p += s;
            }
            if (p != end)
                return false;

            Node node = new Node();
            bool device = false;
            foreach (KeyValuePair<int, int> kid in kids)
            {
                int value = kid.Key + 12, length = kid.Value - 12;
                if (SameHash(d, kid.Key, HashGuid))
                {
                    device = true;
                    if (length > 1 && d[value + length - 1] == 0)
                        node.GuidText = Encoding.ASCII.GetString(d, value, length - 1);
                }
                else if (SameHash(d, kid.Key, HashProductId) && length == 4)
                    node.ProductId = BitConverter.ToInt32(d, value);
                else if (SameHash(d, kid.Key, HashType) && length == 4)
                    node.Type = BitConverter.ToInt32(d, value);
                else if (SameHash(d, kid.Key, HashPreset))
                {
                    node.HasPreset = true;
                    node.Preset = Text(d, value, length);
                }
                else if (SameHash(d, kid.Key, HashCategory))
                    node.Category = Text(d, value, length);
                else if (SameHash(d, kid.Key, HashAircraft))
                    node.Aircraft = Text(d, value, length);
            }
            if (device)
            {
                node.InRecord = parent != null;
                found.Add(node);
                // A slot: a device node with a preset field inside a record of the same device.
                if (parent != null && node.HasPreset && string.Equals(parent.GuidText, node.GuidText, StringComparison.OrdinalIgnoreCase))
                {
                    node.SlotKind = SameHash(d, pos, HashGeneralSlot) ? 'G' : SameHash(d, pos, HashCategorySlot) ? 'C' : SameHash(d, pos, HashAircraftSlot) ? 'A' : '?';
                    parent.Slots.Add(node);
                }
            }
            foreach (KeyValuePair<int, int> kid in kids)
            {
                // A child that is no container is a value (leaf); only containers are walked.
                if (kid.Value >= 16 && LooksLikeContainer(d, kid.Key, kid.Value))
                    Walk(d, kid.Key, kid.Value, device ? node : null, found, depth + 1);
            }
            return true;
        }

        private static string Text(byte[] d, int value, int length)
        {
            return length > 1 && d[value + length - 1] == 0 ? Encoding.ASCII.GetString(d, value, length - 1).Trim() : null;
        }

        private static bool LooksLikeContainer(byte[] d, int pos, int size)
        {
            int end = pos + size;
            int count = BitConverter.ToInt32(d, pos + 12);
            if (count <= 0 || count > 100000)
                return false;
            int p = pos + 16;
            for (int i = 0; i < count; i++)
            {
                if (p + 12 > end)
                    return false;
                int s = BitConverter.ToInt32(d, p + 8);
                if (s < 12 || p + s > end)
                    return false;
                p += s;
            }
            return p == end;
        }

        private static bool SameHash(byte[] d, int pos, byte[] hash)
        {
            for (int i = 0; i < 8; i++)
                if (d[pos + i] != hash[i])
                    return false;
            return true;
        }

        private static byte[] ReadSmall(string file)
        {
            try
            {
                FileInfo info = new FileInfo(file);
                if (!info.Exists || info.Length < 16 || info.Length > 32 * 1024 * 1024)
                    return null;
                using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (MemoryStream copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    return copy.ToArray();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        private static byte[] Inflate(byte[] data)
        {
            if (data.Length < 16)
                return null;
            bool gzip = data[0] == 0x1F && data[1] == 0x8B;
            bool zlib = data[0] == 0x78 && (data[1] == 0x01 || data[1] == 0x5E || data[1] == 0x9C || data[1] == 0xDA);
            if (!gzip && !zlib)
                return null;
            try
            {
                using (MemoryStream input = new MemoryStream(data, gzip ? 0 : 2, data.Length - (gzip ? 0 : 2)))
                using (Stream inflater = gzip ? (Stream)new GZipStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress))
                using (MemoryStream output = new MemoryStream())
                {
                    byte[] buffer = new byte[65536];
                    int read;
                    while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        if (output.Length > 64 * 1024 * 1024)
                            return null;
                    }
                    return output.ToArray();
                }
            }
            catch (InvalidDataException) { }
            catch (IOException) { }
            return null;
        }
    }
}
