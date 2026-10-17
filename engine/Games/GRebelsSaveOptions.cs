using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace JoyPro.Engine.Games
{
    // G-Rebels' SaveOptions.sav (UE 5.8 GVAS of the Blueprint SaveOptions_C, docs/games/g-rebels.md). JoyPro edits it in
    // place only: values of fixed size (an enum name of the same length, a bool in the tag's flags, a double) change,
    // nothing is added, removed or moved, so every other byte stays as the game wrote it. Two arrays matter:
    //   Joysticks:   S_Controller_Save { DeviceID (HID path), Enabled, MultiID (the channel of INPUT > DEVICES) }
    //   AxisOptions: S_Input_Axis_Save { Key (FKey, e.g. RawInput_Controller0_AxisY), Offset, Multiplier,
    //                Role (E_Input_Axis_Roles::NewEnumerator<n>), Inverted, DeadZone }
    // Property tags are UE 5.4+ tags: FString name ("None" ends a struct), the type as a tree (FString name, int32
    // count of inner types, the inner types), int32 size, uint8 flags (1 array index, 2 property guid, 4 extensions,
    // 8 binary/native, 0x10 bool true), the optional parts, then the value.
    internal sealed class GRebelsSaveOptions
    {
        public const string ClassPath = "/Game/BASE/UI/STARTMENU/Blueprints/SaveFiles/SaveOptions.SaveOptions_C";
        private const byte FlagArrayIndex = 0x01;
        private const byte FlagGuid = 0x02;
        private const byte FlagExtensions = 0x04;
        public const byte FlagBoolTrue = 0x10;

        internal sealed class Device
        {
            public string Path = string.Empty;
            public bool Enabled;
            public int Channel;
        }

        internal sealed class Axis
        {
            public string Key = string.Empty;
            public int Role = -1;
            public bool Inverted;
            public double DeadZone;
            // Byte positions of the editable values (-1 = not in the file).
            public int RoleDigit = -1;
            public int InvertedFlags = -1;
            public int DeadZoneValue = -1;
        }

        private byte[] data;
        public List<Device> Devices { get; private set; }
        public List<Axis> Axes { get; private set; }
        public bool Changed { get; private set; }

        public static bool TryLoad(string path, List<string> errors, out GRebelsSaveOptions file)
        {
            file = null;
            if (!File.Exists(path))
            {
                errors.Add("SaveOptions.sav not found: " + path + " (start G-Rebels once).");
                return false;
            }
            return TryParse(File.ReadAllBytes(path), path, errors, out file);
        }

        public static bool TryParse(byte[] data, string where, List<string> errors, out GRebelsSaveOptions file)
        {
            file = new GRebelsSaveOptions { data = data, Devices = new List<Device>(), Axes = new List<Axis>() };
            if (data.Length < 4 || Encoding.ASCII.GetString(data, 0, 4) != "GVAS" || IndexOf(data, FStringBytes(ClassPath), 0) < 0)
            {
                errors.Add(where + " is not G-Rebels' SaveOptions save.");
                file = null;
                return false;
            }
            try
            {
                int axes = FindTag(data, "AxisOptions");
                if (axes >= 0)
                    file.ReadArray(axes, file.ReadAxis);
                int joysticks = FindTag(data, "Joysticks");
                if (joysticks >= 0)
                    file.ReadArray(joysticks, file.ReadDevice);
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is ArgumentException || ex is IndexOutOfRangeException)
            {
                errors.Add(where + " has an unknown layout: " + ex.Message);
                file = null;
                return false;
            }
        }

        public byte[] Bytes()
        {
            return data;
        }

        // Role 0..6 (Yaw, Thrust, Roll, Pitch, Side strafe, Up/down, None).
        public void SetRole(Axis axis, int role)
        {
            if (axis.RoleDigit < 0 || role < 0 || role > 9 || axis.Role == role)
                return;
            data[axis.RoleDigit] = (byte)('0' + role);
            axis.Role = role;
            Changed = true;
        }

        public void SetInverted(Axis axis, bool inverted)
        {
            if (axis.InvertedFlags < 0 || axis.Inverted == inverted)
                return;
            data[axis.InvertedFlags] = (byte)(inverted ? data[axis.InvertedFlags] | FlagBoolTrue : data[axis.InvertedFlags] & ~FlagBoolTrue);
            axis.Inverted = inverted;
            Changed = true;
        }

        public void SetDeadZone(Axis axis, double deadZone)
        {
            if (axis.DeadZoneValue < 0 || Math.Abs(axis.DeadZone - deadZone) < 1e-9)
                return;
            Buffer.BlockCopy(BitConverter.GetBytes(deadZone), 0, data, axis.DeadZoneValue, 8);
            axis.DeadZone = deadZone;
            Changed = true;
        }

        // ---- reading ----------------------------------------------------------------------------------------------

        private sealed class Tag
        {
            public string Name = string.Empty;
            public string Type = string.Empty;
            public List<string> Inner = new List<string>();
            public int Size;
            public byte Flags;
            public int FlagsAt;
            public int ValueAt;
        }

        // The tag "name" at the top level: its FString followed by the FString of its type.
        private static int FindTag(byte[] data, string name)
        {
            byte[] pattern = FStringBytes(name);
            for (int at = IndexOf(data, pattern, 0); at >= 0; at = IndexOf(data, pattern, at + 1))
            {
                int position = at + pattern.Length;
                try
                {
                    if (ReadFString(data, ref position) == "ArrayProperty")
                        return at;
                }
                catch (InvalidDataException)
                {
                }
            }
            return -1;
        }

        private Tag ReadTag(ref int position)
        {
            Tag tag = new Tag { Name = ReadFString(data, ref position) };
            if (tag.Name == "None")
                return tag;
            tag.Type = ReadTypeNode(ref position, tag.Inner);
            tag.Size = ReadInt32(data, ref position);
            tag.FlagsAt = position;
            tag.Flags = data[position++];
            if ((tag.Flags & FlagArrayIndex) != 0)
                position += 4;
            if ((tag.Flags & FlagGuid) != 0)
                position += 16;
            if ((tag.Flags & FlagExtensions) != 0)
                throw new InvalidDataException("property extensions on '" + tag.Name + "'");
            tag.ValueAt = position;
            if (tag.Size < 0 || position + tag.Size > data.Length)
                throw new InvalidDataException("bad size of '" + tag.Name + "'");
            return tag;
        }

        private string ReadTypeNode(ref int position, List<string> inner)
        {
            string name = ReadFString(data, ref position);
            int count = ReadInt32(data, ref position);
            if (count < 0 || count > 16)
                throw new InvalidDataException("bad type tree");
            for (int i = 0; i < count; i++)
            {
                List<string> deeper = new List<string>();
                inner.Add(ReadTypeNode(ref position, deeper));
                inner.AddRange(deeper);
            }
            return name;
        }

        // An array of structs: the tag, int32 count, then each element's tags up to "None".
        private void ReadArray(int tagAt, Action<int, int> element)
        {
            int position = tagAt;
            Tag tag = ReadTag(ref position);
            if (tag.Type != "ArrayProperty" || tag.Inner.Count == 0 || tag.Inner[0] != "StructProperty")
                throw new InvalidDataException("'" + tag.Name + "' is no struct array");
            int end = tag.ValueAt + tag.Size;
            int count = ReadInt32(data, ref position);
            for (int i = 0; i < count; i++)
            {
                int start = position;
                SkipStruct(ref position);
                element(start, position);
            }
            if (position != end)
                throw new InvalidDataException("'" + tag.Name + "' does not end where its size says");
        }

        private void SkipStruct(ref int position)
        {
            while (true)
            {
                Tag tag = ReadTag(ref position);
                if (tag.Name == "None")
                    return;
                position = tag.ValueAt + tag.Size;
            }
        }

        private void ReadAxis(int start, int end)
        {
            Axis axis = new Axis();
            int position = start;
            while (position < end)
            {
                Tag tag = ReadTag(ref position);
                if (tag.Name == "None")
                    break;
                string field = FieldName(tag.Name);
                if (field == "Key" && tag.Type == "StructProperty")
                {
                    int inner = tag.ValueAt;
                    while (inner < tag.ValueAt + tag.Size)
                    {
                        Tag keyTag = ReadTag(ref inner);
                        if (keyTag.Name == "None")
                            break;
                        if (keyTag.Name == "KeyName" && keyTag.Type == "NameProperty")
                        {
                            int value = keyTag.ValueAt;
                            axis.Key = ReadFString(data, ref value);
                        }
                        inner = keyTag.ValueAt + keyTag.Size;
                    }
                }
                else if (field == "Role" && tag.Type == "ByteProperty")
                {
                    int value = tag.ValueAt;
                    int stringStart = value + 4;
                    string role = ReadFString(data, ref value);
                    int marker = role.LastIndexOf("NewEnumerator", StringComparison.Ordinal);
                    int number;
                    if (marker >= 0 && role.Length == marker + 14 && int.TryParse(role.Substring(marker + 13), out number))
                    {
                        axis.Role = number;
                        axis.RoleDigit = stringStart + marker + 13;
                    }
                }
                else if (field == "Inverted" && tag.Type == "BoolProperty")
                {
                    axis.Inverted = (tag.Flags & FlagBoolTrue) != 0;
                    axis.InvertedFlags = tag.FlagsAt;
                }
                else if (field == "DeadZone" && tag.Type == "DoubleProperty" && tag.Size == 8)
                {
                    axis.DeadZone = BitConverter.ToDouble(data, tag.ValueAt);
                    axis.DeadZoneValue = tag.ValueAt;
                }
                position = tag.ValueAt + tag.Size;
            }
            Axes.Add(axis);
        }

        private void ReadDevice(int start, int end)
        {
            Device device = new Device();
            int position = start;
            while (position < end)
            {
                Tag tag = ReadTag(ref position);
                if (tag.Name == "None")
                    break;
                string field = FieldName(tag.Name);
                int value = tag.ValueAt;
                if (field == "DeviceID" && tag.Type == "StrProperty")
                    device.Path = ReadFString(data, ref value);
                else if (field == "Enabled" && tag.Type == "BoolProperty")
                    device.Enabled = (tag.Flags & FlagBoolTrue) != 0;
                else if (field == "MultiID" && tag.Type == "IntProperty" && tag.Size == 4)
                    device.Channel = BitConverter.ToInt32(data, tag.ValueAt);
                position = tag.ValueAt + tag.Size;
            }
            Devices.Add(device);
        }

        // Blueprint struct members are saved as "<Name>_<n>_<GUID>".
        private static string FieldName(string tagName)
        {
            int underscore = tagName.IndexOf('_');
            return underscore > 0 ? tagName.Substring(0, underscore) : tagName;
        }

        // ---- primitives ------------------------------------------------------------------------------------------

        private static int ReadInt32(byte[] data, ref int position)
        {
            if (position < 0 || position + 4 > data.Length)
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
                if (length > 4096 || position + length > data.Length)
                    throw new InvalidDataException("bad string");
                // TrimEnd: the game saves the HID path with one NUL too many.
                string text = Encoding.GetEncoding(28591).GetString(data, position, length - 1).TrimEnd('\0');
                position += length;
                return text;
            }
            int chars = -length;
            if (chars > 4096 || position + chars * 2 > data.Length)
                throw new InvalidDataException("bad string");
            string wide = Encoding.Unicode.GetString(data, position, (chars - 1) * 2);
            position += chars * 2;
            return wide;
        }

        private static byte[] FStringBytes(string value)
        {
            byte[] text = Encoding.GetEncoding(28591).GetBytes(value);
            byte[] bytes = new byte[4 + text.Length + 1];
            Buffer.BlockCopy(BitConverter.GetBytes(text.Length + 1), 0, bytes, 0, 4);
            Buffer.BlockCopy(text, 0, bytes, 4, text.Length);
            return bytes;
        }

        private static int IndexOf(byte[] data, byte[] pattern, int from)
        {
            for (int i = Math.Max(0, from); i <= data.Length - pattern.Length; i++)
            {
                int k = 0;
                while (k < pattern.Length && data[i + k] == pattern[k])
                    k++;
                if (k == pattern.Length)
                    return i;
            }
            return -1;
        }
    }
}
