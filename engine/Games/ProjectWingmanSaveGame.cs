using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine.Games
{
    // Project Wingman's SaveGames\savegame.sav (GVAS, /Game/ProjectWingman/Blueprints/Instance/WingmanSaveGame): flat
    // top-level properties (MasterAudio, VersionNumber, CameraID, PovWeapon1, ..., "None"). The curves and dead zones of
    // CURVES + DEADZONES are FloatProperty "<Axis>Curve" / "<Axis>Deadzone" (Pitch, Roll, Thrust, Yaw, ViewX, ViewY),
    // present once changed in the menu (else the game's defaults apply). No checksum. JoyPro edits only those floats in
    // place, or adds the missing ones before the closing "None"; every other byte stays.
    internal static class PwSaveGame
    {
        internal sealed class Setting
        {
            // Menu units: curve 0..2 (1 = straight), dead zone 0..1.
            public double Curve = 1.0;
            public double Deadzone;
        }

        private static readonly byte[] NoneTag = BuildString("None");

        // Axis prefix -> its settings (the game's default where the save has no value).
        public static Dictionary<string, Setting> ReadAxisSettings(string path, List<string> warnings)
        {
            Dictionary<string, Setting> result = new Dictionary<string, Setting>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return result;
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                warnings.Add("savegame.sav could not be read: " + ex.Message);
                return result;
            }
            if (!IsGvas(data))
            {
                warnings.Add(path + " is not a GVAS save game; curves and dead zones were not read.");
                return result;
            }
            foreach (string prefix in ProjectWingmanIntegration.AxisSettingPrefix.Values)
            {
                double curve;
                double deadzone;
                bool hasCurve = TryReadFloat(data, prefix + "Curve", out curve);
                bool hasDeadzone = TryReadFloat(data, prefix + "Deadzone", out deadzone);
                double defaultDeadzone;
                ProjectWingmanIntegration.DefaultDeadzone.TryGetValue(prefix, out defaultDeadzone);
                result[prefix] = new Setting
                {
                    Curve = hasCurve ? Math.Round(curve * 2.0, 2) : 1.0,
                    Deadzone = hasDeadzone ? Math.Round(deadzone, 2) : defaultDeadzone
                };
            }
            return result;
        }

        public static void WriteAxisSettings(string path, Dictionary<string, Setting> settings, GameIoResult result)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                result.Warnings.Add("savegame.sav not found (" + path + "): start the game once; the curves and dead zones were not written.");
                return;
            }
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                result.Errors.Add("savegame.sav could not be read: " + ex.Message);
                return;
            }
            if (!IsGvas(data) || LastIndexOf(data, NoneTag) < 0)
            {
                result.Errors.Add(path + " is not a Project Wingman save game; the curves and dead zones were not written.");
                return;
            }
            byte[] updated = data;
            List<string> changes = new List<string>();
            foreach (KeyValuePair<string, Setting> entry in settings.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                updated = SetFloat(updated, entry.Key + "Curve", (float)Math.Round(entry.Value.Curve / 2.0, 4), changes);
                updated = SetFloat(updated, entry.Key + "Deadzone", (float)Math.Round(entry.Value.Deadzone, 4), changes);
            }
            if (changes.Count == 0)
            {
                result.Steps.Add("savegame.sav: the curves and dead zones are already up to date.");
                return;
            }
            GameExportSafety.BackupBeforeOverwrite(path, result.Warnings);
            try
            {
                string temp = path + ".joypro-tmp";
                File.WriteAllBytes(temp, updated);
                File.Replace(temp, path, null);
            }
            catch (Exception ex)
            {
                result.Errors.Add("savegame.sav could not be written: " + ex.Message);
                return;
            }
            result.FilesWritten.Add(path);
            result.Steps.Add("savegame.sav: " + string.Join(", ", changes) + " (Steam Cloud syncs this file).");
        }

        private static bool IsGvas(byte[] data)
        {
            return data.Length > 8 && data[0] == (byte)'G' && data[1] == (byte)'V' && data[2] == (byte)'A' && data[3] == (byte)'S';
        }

        // FString: int32 length incl. the terminator, ASCII, 0.
        private static byte[] BuildString(string text)
        {
            byte[] bytes = new byte[4 + text.Length + 1];
            BitConverter.GetBytes(text.Length + 1).CopyTo(bytes, 0);
            Encoding.ASCII.GetBytes(text).CopyTo(bytes, 4);
            return bytes;
        }

        // Offset of the float value of a top-level FloatProperty, or -1.
        private static int FindFloat(byte[] data, string name)
        {
            byte[] tag = BuildString(name);
            byte[] type = BuildString("FloatProperty");
            int at = IndexOf(data, tag, 0);
            while (at >= 0)
            {
                int p = at + tag.Length;
                if (Matches(data, p, type) && p + type.Length + 8 + 1 + 4 <= data.Length)
                {
                    p += type.Length;
                    long size = BitConverter.ToInt64(data, p);
                    if (size == 4 && data[p + 8] == 0)
                        return p + 9;
                }
                at = IndexOf(data, tag, at + 1);
            }
            return -1;
        }

        private static bool TryReadFloat(byte[] data, string name, out double value)
        {
            value = 0;
            int at = FindFloat(data, name);
            if (at < 0)
                return false;
            value = BitConverter.ToSingle(data, at);
            return true;
        }

        private static byte[] SetFloat(byte[] data, string name, float value, List<string> changes)
        {
            int at = FindFloat(data, name);
            string text = name + "=" + value.ToString("0.####", CultureInfo.InvariantCulture);
            if (at >= 0)
            {
                if (Math.Abs(BitConverter.ToSingle(data, at) - value) < 1e-6)
                    return data;
                byte[] copy = (byte[])data.Clone();
                BitConverter.GetBytes(value).CopyTo(copy, at);
                changes.Add(text);
                return copy;
            }
            // A new property before the closing "None" of the top-level list.
            int none = LastIndexOf(data, NoneTag);
            using (MemoryStream property = new MemoryStream())
            {
                byte[] tag = BuildString(name);
                byte[] type = BuildString("FloatProperty");
                property.Write(tag, 0, tag.Length);
                property.Write(type, 0, type.Length);
                property.Write(BitConverter.GetBytes(4L), 0, 8);
                property.WriteByte(0);
                property.Write(BitConverter.GetBytes(value), 0, 4);
                byte[] add = property.ToArray();
                byte[] result = new byte[data.Length + add.Length];
                Buffer.BlockCopy(data, 0, result, 0, none);
                Buffer.BlockCopy(add, 0, result, none, add.Length);
                Buffer.BlockCopy(data, none, result, none + add.Length, data.Length - none);
                changes.Add(text + " (new)");
                return result;
            }
        }

        private static bool Matches(byte[] data, int at, byte[] pattern)
        {
            if (at < 0 || at + pattern.Length > data.Length)
                return false;
            for (int i = 0; i < pattern.Length; i++)
            {
                if (data[at + i] != pattern[i])
                    return false;
            }
            return true;
        }

        private static int IndexOf(byte[] data, byte[] pattern, int from)
        {
            for (int i = Math.Max(0, from); i + pattern.Length <= data.Length; i++)
            {
                if (Matches(data, i, pattern))
                    return i;
            }
            return -1;
        }

        private static int LastIndexOf(byte[] data, byte[] pattern)
        {
            for (int i = data.Length - pattern.Length; i >= 0; i--)
            {
                if (Matches(data, i, pattern))
                    return i;
            }
            return -1;
        }
    }
}
