using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    // DCS names a joystick "<DirectInput instance name> {<instance GUID>}" with the instance name exactly as DirectInput
    // reports it (VKB sticks: " VKB-Sim Gunfighter Modern Combat Pro Twist  {...}", leading and trailing spaces kept).
    // JoyPro's device names trim the instance name, so every DCS file name goes through these helpers:
    // - DcsName: JoyPro name -> the name DCS uses (connected device, else an existing file with that GUID).
    // - JoyProName: DCS name (file name) -> JoyPro name.
    // - TemplateName: DCS's Input.Utils.getDeviceTemplateName (name without " {GUID}"), which picks the device's
    //   default file "<template>.lua" / "<template>.diff.lua" in an aircraft's joystick input folder.
    internal static class DcsDeviceNames
    {
        // DCS: string.gsub(deviceName, '(.*)(%s{.*})', '%1'); the greedy (.*) splits at the last whitespace + '{'.
        private static readonly Regex TemplateSplit = new Regex(@"^(.*)\s\{.*$", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex GuidPart = new Regex(@"\{([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}\s*$", RegexOptions.Compiled);

        public static string TemplateName(string dcsName)
        {
            if (string.IsNullOrEmpty(dcsName))
                return string.Empty;
            Match m = TemplateSplit.Match(dcsName);
            return m.Success ? m.Groups[1].Value : dcsName;
        }

        public static string JoyProName(string dcsName)
        {
            if (string.IsNullOrEmpty(dcsName))
                return dcsName;
            Match guid = GuidPart.Match(dcsName);
            if (!guid.Success)
                return dcsName;
            string name = TemplateName(dcsName).Trim();
            return name.Length == 0 ? dcsName : name + " {" + guid.Groups[1].Value + "}";
        }

        public static bool TryGetInstanceGuid(string name, out Guid guid)
        {
            guid = Guid.Empty;
            Match m = name == null ? Match.Empty : GuidPart.Match(name);
            return m.Success && Guid.TryParse(m.Groups[1].Value, out guid);
        }

        private static List<DirectInputDeviceDiscovery.DirectInputDeviceInfo> connected;

        // The connected DirectInput devices, read once per process (an export runs in one short-lived process).
        private static List<DirectInputDeviceDiscovery.DirectInputDeviceInfo> Connected()
        {
            if (connected == null)
            {
                try
                {
                    connected = DirectInputDeviceDiscovery.EnumerateConnectedDeviceInfos(new List<string>()) ?? new List<DirectInputDeviceDiscovery.DirectInputDeviceInfo>();
                }
                catch
                {
                    connected = new List<DirectInputDeviceDiscovery.DirectInputDeviceInfo>();
                }
            }
            return connected;
        }

        // Test hook: replaces the DirectInput enumeration (null = enumerate again).
        internal static void SetConnectedForTests(List<DirectInputDeviceDiscovery.DirectInputDeviceInfo> devices)
        {
            connected = devices;
        }

        // The name DCS gives a JoyPro device: the raw DirectInput instance name of the connected device with that
        // instance GUID; else the name of an existing .diff.lua/.jp in the folder whose JoyPro name matches; else the
        // JoyPro name itself (devices without spaces at the ends are named the same in both).
        public static string DcsName(string joyProName, string joystickFolder)
        {
            if (string.IsNullOrWhiteSpace(joyProName))
                return joyProName;
            Guid guid;
            if (TryGetInstanceGuid(joyProName, out guid))
            {
                foreach (DirectInputDeviceDiscovery.DirectInputDeviceInfo info in Connected())
                {
                    if (info == null || info.InstanceGuid != guid || string.IsNullOrEmpty(info.InstanceName))
                        continue;
                    string raw = info.InstanceName.Replace("/", "_").Replace("\"", "_") + " {" + DirectInputDeviceDiscovery.FormatGuidToken(guid) + "}";
                    if (string.Equals(JoyProName(raw), joyProName, StringComparison.OrdinalIgnoreCase))
                        return raw;
                }
            }

            if (!string.IsNullOrEmpty(joystickFolder) && Directory.Exists(joystickFolder))
            {
                foreach (string file in Directory.GetFiles(joystickFolder))
                {
                    string name = Path.GetFileName(file);
                    if (name.EndsWith(".diff.lua", StringComparison.OrdinalIgnoreCase))
                        name = name.Substring(0, name.Length - ".diff.lua".Length);
                    else if (name.EndsWith(".jp", StringComparison.OrdinalIgnoreCase))
                        name = name.Substring(0, name.Length - ".jp".Length);
                    else
                        continue;
                    if (!string.Equals(name, joyProName, StringComparison.Ordinal) &&
                        string.Equals(JoyProName(name), joyProName, StringComparison.OrdinalIgnoreCase))
                        return name;
                }
            }
            return joyProName;
        }
    }
}
