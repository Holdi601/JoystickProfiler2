using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine.Games
{
    internal sealed class GvasKeyMapping
    {
        public string MappingName = string.Empty;
        public string CurrentKey = string.Empty;
        public string HardwareInputClass = "None";
        public string HardwareDevice = "None";
        public byte PrimaryDeviceType;
        public int SupportedFeatures;
        public byte Slot;
        // The bytes the mapping was read from; null for new or changed mappings.
        public byte[] Raw;
    }

    // Player-mapped keys in an Unreal Engine 5 EnhancedInputUserSettings.sav (GVAS). The mapping list follows the
    // key profile's class and object name; only that list is rewritten, the header, the properties and everything
    // after the list stay byte-identical. Layout: tools/research/gvas.py, docs/games/hll-vietnam.md.
    internal sealed class GvasKeyMappings
    {
        private const string SettingsClass = "/Script/EnhancedInput.EnhancedInputUserSettings";
        private const string ProfileClass = "/Script/EnhancedInput.EnhancedPlayerMappableKeyProfile";

        private byte[] data;
        private int listStart;
        private int listEnd;

        public List<GvasKeyMapping> Mappings { get; private set; }
        public bool Changed { get; set; }

        public static bool TryLoad(string path, List<string> errors, out GvasKeyMappings file)
        {
            file = null;
            if (!File.Exists(path))
            {
                errors.Add("Input settings file not found: " + path);
                return false;
            }

            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 4 || Encoding.ASCII.GetString(data, 0, 4) != "GVAS" || IndexOf(data, FStringBytes(SettingsClass), 0) < 0)
            {
                errors.Add("Not an Unreal EnhancedInput user settings save: " + path);
                return false;
            }

            byte[] marker = FStringBytes(ProfileClass);
            int first = IndexOf(data, marker, 0);
            if (first < 0)
            {
                errors.Add("The save has no key profile yet; change one key in the game once: " + path);
                return false;
            }
            if (IndexOf(data, marker, first + 1) >= 0)
            {
                errors.Add("The save has several key profiles; JoyPro only edits files with one profile: " + path);
                return false;
            }

            try
            {
                int position = first + marker.Length;
                ReadFString(data, ref position);
                int listStart = position;
                int count = ReadInt32(data, ref position);
                if (count < 0 || count > 100000)
                    throw new InvalidDataException("bad mapping count " + count);
                List<GvasKeyMapping> mappings = new List<GvasKeyMapping>();
                for (int i = 0; i < count; i++)
                {
                    int start = position;
                    GvasKeyMapping mapping = new GvasKeyMapping
                    {
                        MappingName = ReadFString(data, ref position),
                        CurrentKey = ReadFString(data, ref position),
                        HardwareInputClass = ReadFString(data, ref position),
                        HardwareDevice = ReadFString(data, ref position)
                    };
                    mapping.PrimaryDeviceType = ReadByte(data, ref position);
                    mapping.SupportedFeatures = ReadInt32(data, ref position);
                    mapping.Slot = ReadByte(data, ref position);
                    mapping.Raw = data.Skip(start).Take(position - start).ToArray();
                    mappings.Add(mapping);
                }
                file = new GvasKeyMappings { data = data, listStart = listStart, listEnd = position, Mappings = mappings };
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IndexOutOfRangeException || ex is ArgumentException)
            {
                errors.Add("The key profile of " + path + " has an unknown layout: " + ex.Message);
                return false;
            }
        }

        public byte[] Serialize()
        {
            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(data, 0, listStart);
                WriteInt32(stream, Mappings.Count);
                foreach (GvasKeyMapping mapping in Mappings)
                {
                    if (mapping.Raw != null)
                    {
                        stream.Write(mapping.Raw, 0, mapping.Raw.Length);
                        continue;
                    }
                    WriteFString(stream, mapping.MappingName);
                    WriteFString(stream, mapping.CurrentKey);
                    WriteFString(stream, mapping.HardwareInputClass);
                    WriteFString(stream, mapping.HardwareDevice);
                    stream.WriteByte(mapping.PrimaryDeviceType);
                    WriteInt32(stream, mapping.SupportedFeatures);
                    stream.WriteByte(mapping.Slot);
                }
                stream.Write(data, listEnd, data.Length - listEnd);
                return stream.ToArray();
            }
        }

        public void Save(string path)
        {
            File.WriteAllBytes(path, Serialize());
        }

        private static byte[] FStringBytes(string value)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                WriteFString(stream, value);
                return stream.ToArray();
            }
        }

        private static int IndexOf(byte[] data, byte[] pattern, int from)
        {
            for (int i = from; i <= data.Length - pattern.Length; i++)
            {
                int k = 0;
                while (k < pattern.Length && data[i + k] == pattern[k])
                    k++;
                if (k == pattern.Length)
                    return i;
            }
            return -1;
        }

        private static byte ReadByte(byte[] data, ref int position)
        {
            if (position >= data.Length)
                throw new InvalidDataException("unexpected end of file");
            return data[position++];
        }

        private static int ReadInt32(byte[] data, ref int position)
        {
            if (position + 4 > data.Length)
                throw new InvalidDataException("unexpected end of file");
            int value = BitConverter.ToInt32(data, position);
            position += 4;
            return value;
        }

        // FString: length > 0 = Latin-1 with NUL, length < 0 = UTF-16 chars with NUL, 0 = empty.
        private static string ReadFString(byte[] data, ref int position)
        {
            int length = ReadInt32(data, ref position);
            if (length == 0)
                return string.Empty;
            if (length > 0)
            {
                if (length > 65536 || position + length > data.Length)
                    throw new InvalidDataException("bad string length " + length);
                string text = Encoding.GetEncoding(28591).GetString(data, position, length - 1);
                position += length;
                return text;
            }
            int chars = -length;
            if (chars > 65536 || position + chars * 2 > data.Length)
                throw new InvalidDataException("bad string length " + length);
            string wide = Encoding.Unicode.GetString(data, position, (chars - 1) * 2);
            position += chars * 2;
            return wide;
        }

        private static void WriteInt32(Stream stream, int value)
        {
            stream.Write(BitConverter.GetBytes(value), 0, 4);
        }

        private static void WriteFString(Stream stream, string value)
        {
            value = value ?? string.Empty;
            if (value.Length == 0)
            {
                WriteInt32(stream, 0);
                return;
            }
            if (value.All(c => c < 0x80))
            {
                byte[] ascii = Encoding.ASCII.GetBytes(value + "\0");
                WriteInt32(stream, ascii.Length);
                stream.Write(ascii, 0, ascii.Length);
                return;
            }
            byte[] wide = Encoding.Unicode.GetBytes(value + "\0");
            WriteInt32(stream, -(wide.Length / 2));
            stream.Write(wide, 0, wide.Length);
        }
    }
}
