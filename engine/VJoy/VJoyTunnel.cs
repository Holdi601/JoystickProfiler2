using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JoyPro.Engine.VJoy;

namespace JoyPro.Engine
{
    // The vJoy device JoyPro tunnels into (GUI: Settings "vJoy device JoyPro tunnels into", engine --vjoy-tunnel <1-16>):
    // its binds are JoyPro's own forwarding, so imports and scans leave that one vJoy instance out. Every other vJoy
    // instance (VoiceAttack, a user's own feeder) is a normal device; nothing excludes vJoy by default.
    internal static class VJoyTunnel
    {
        public const string Option = "--vjoy-tunnel";

        // 0: none (the default).
        public static int Instance { get; private set; }

        private static List<string> resolved;
        private static readonly List<string> notes = new List<string>();

        public static IReadOnlyList<string> Notes
        {
            get { return notes; }
        }

        // Sets the run's choice ("", "0", "none" or 1..16); false with an error for anything else.
        public static bool TrySet(string raw, out string error)
        {
            error = string.Empty;
            Instance = 0;
            resolved = null;
            notes.Clear();
            string value = (raw ?? string.Empty).Trim();
            if (value.Length == 0 || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) || value == "0")
                return true;
            int id;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || id < 1 || id > VJoySettings.MaxDevices)
            {
                error = Option + " needs a vJoy device number 1-" + VJoySettings.MaxDevices.ToString(CultureInfo.InvariantCulture) + " (or none).";
                return false;
            }
            Instance = id;
            return true;
        }

        // The device names (as JoyPro names them, "vJoy Device {GUID}") of the tunnel instance; empty without one or
        // when that vJoy device is not there (a note says so).
        public static List<string> Devices()
        {
            if (Instance == 0)
                return new List<string>();
            if (resolved != null)
                return new List<string>(resolved);
            resolved = new List<string>();
            try
            {
                EngineVJoy.DeviceScan scan = EngineVJoy.ScanDevices(null, EngineVJoy.ReadVJoyDevices());
                resolved = scan.VJoy.Where(v => v.Id == Instance && !string.IsNullOrWhiteSpace(v.DeviceName))
                    .Select(v => v.DeviceName.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                JoyPro.Logging.JoyProLog.Warn("VJoy", "Could not find vJoy device " + Instance.ToString(CultureInfo.InvariantCulture), ex);
            }
            string number = Instance.ToString(CultureInfo.InvariantCulture);
            notes.Add(resolved.Count == 0
                ? "vJoy device " + number + " (the one JoyPro tunnels into) is not connected; no device is left out."
                : "vJoy device " + number + " is the one JoyPro tunnels into; its binds are not read: " + string.Join(", ", resolved) + ".");
            JoyPro.Logging.JoyProLog.Info("VJoy", notes[notes.Count - 1]);
            return new List<string>(resolved);
        }

        // Adds the tunnel instance to an import's ignore-device list.
        public static void AddTo(List<string> ignoredDevices)
        {
            if (ignoredDevices == null)
                return;
            foreach (string device in Devices())
            {
                if (!ignoredDevices.Contains(device, StringComparer.OrdinalIgnoreCase))
                    ignoredDevices.Add(device);
            }
        }

        public static bool IsTunnelDevice(string device)
        {
            string value = (device ?? string.Empty).Trim();
            return value.Length > 0 && Devices().Any(d => string.Equals(d, value, StringComparison.OrdinalIgnoreCase));
        }
    }
}
