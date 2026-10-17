using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    // What a controller actually is: vendor and model from the USB VID:PID and the HID product string (the
    // DirectInput instance name and GUID alone say little), plus its kind and a web search query for its picture.
    public sealed class DeviceModelIdentity
    {
        public string Device { get; set; }
        public string Vendor { get; set; }
        public string Model { get; set; }
        public string VidPid { get; set; }
        public string Kind { get; set; }
        public bool Connected { get; set; }
        public string SearchQuery { get; set; }
        public string SearchUrl { get; set; }

        public DeviceModelIdentity()
        {
            Device = string.Empty;
            Vendor = string.Empty;
            Model = string.Empty;
            VidPid = string.Empty;
            Kind = string.Empty;
            SearchQuery = string.Empty;
            SearchUrl = string.Empty;
        }

        public string DisplayName
        {
            get { return (Vendor.Length > 0 && !Model.StartsWith(Vendor, StringComparison.OrdinalIgnoreCase) ? Vendor + " " : string.Empty) + Model; }
        }

        // USB vendor IDs of common flight-sim hardware. Several brands ship devices with a generic chip VID; those
        // are recognized by their product name instead (see VendorsByName).
        private static readonly Dictionary<int, string> VendorsByVid = new Dictionary<int, string>
        {
            { 0x231D, "VKB" }, { 0x3344, "Virpil" }, { 0x4098, "WinWing" }, { 0x044F, "Thrustmaster" },
            { 0x046D, "Logitech" }, { 0x06A3, "Saitek" }, { 0x0738, "Saitek" }, { 0x068E, "CH Products" },
            { 0x16D0, "MFG" }, { 0x1DD2, "Leo Bodnar" }, { 0x294B, "Honeycomb" }, { 0x10F5, "Turtle Beach" },
            { 0x346E, "Moza" }, { 0x045E, "Microsoft" }, { 0x054C, "Sony" }, { 0x0FD9, "Elgato" }, { 0x057E, "Nintendo" },
            { 0x2DC8, "8BitDo" }, { 0x0EB7, "Fanatec" }, { 0x3416, "WinWing" }, { 0x31E3, "Wooting" }, { 0x1234, "vJoy" }
        };

        private static readonly string[][] VendorsByName =
        {
            new[] { "vkb", "VKB" }, new[] { "vpc", "Virpil" }, new[] { "virpil", "Virpil" }, new[] { "winwing", "WinWing" },
            new[] { "thrustmaster", "Thrustmaster" }, new[] { "t.16000m", "Thrustmaster" }, new[] { "twcs", "Thrustmaster" },
            new[] { "warthog", "Thrustmaster" }, new[] { "logitech", "Logitech" }, new[] { "saitek", "Saitek" },
            new[] { "honeycomb", "Honeycomb" }, new[] { "mfg", "MFG" }, new[] { "crosswind", "MFG" }, new[] { "moza", "Moza" },
            new[] { "vpforce", "VPforce" }, new[] { "rhino ffb", "VPforce" }, new[] { "velocityone", "Turtle Beach" },
            new[] { "xbox", "Microsoft" }, new[] { "ch", "CH Products" }, new[] { "gladiator", "VKB" }, new[] { "gunfighter", "VKB" }
        };

        private static readonly Regex Noise = new Regex(@"\((ARM|HID|USB)\)|\bConsumer Control\b|\bHID-compliant game controller\b|\bGame Controller\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static DeviceModelIdentity Resolve(string device, GameDeviceDirectory devices)
        {
            DeviceModelIdentity identity = new DeviceModelIdentity { Device = (device ?? string.Empty).Trim() };
            string instanceName = RemoveGuid(identity.Device);
            GameDevice connected = devices == null ? null : devices.FindByJoyProName(identity.Device);
            identity.Connected = connected != null;

            string productName = connected != null && connected.HidProductString.Trim().Length > 0 ? connected.HidProductString.Trim() : instanceName;
            string vendor = string.Empty;
            if (connected != null && connected.HasVidPid)
            {
                identity.VidPid = GameDeviceDirectory.VidPidKey(connected.VendorId, connected.ProductId);
                VendorsByVid.TryGetValue(connected.VendorId, out vendor);
            }
            if (string.IsNullOrEmpty(vendor) || vendor == "vJoy" && productName.IndexOf("vjoy", StringComparison.OrdinalIgnoreCase) < 0)
                vendor = VendorFromName(productName + " " + instanceName);
            identity.Vendor = vendor ?? string.Empty;

            // Virpil names carry the configured hand ("LEFT VPC ...", "R-VPC ..."), which is not part of the model.
            string model = Regex.Replace(Noise.Replace(productName, " "), @"^(LEFT|RIGHT)\s+(?=VPC)|^[LR]-(?=VPC)", string.Empty, RegexOptions.IgnoreCase);
            model = Regex.Replace(model, @"\s+", " ").Trim(' ', '-', '_');
            identity.Model = model.Length > 0 ? model : instanceName;
            identity.Kind = GuessKind(identity.Model + " " + instanceName, connected);
            identity.SearchQuery = identity.DisplayName;
            identity.SearchUrl = "https://www.google.com/search?tbm=isch&q=" + Uri.EscapeDataString(identity.SearchQuery);
            return identity;
        }

        internal static string VendorFromName(string name)
        {
            string lower = (name ?? string.Empty).ToLowerInvariant();
            foreach (string[] pair in VendorsByName)
            {
                if (Regex.IsMatch(lower, "(^|[^a-z0-9])" + Regex.Escape(pair[0].Trim()) + "($|[^a-z0-9])"))
                    return pair[1];
            }
            return string.Empty;
        }

        private static string GuessKind(string name, GameDevice connected)
        {
            string lower = (name ?? string.Empty).ToLowerInvariant();
            if (Regex.IsMatch(lower, "rudder|pedal|crosswind"))
                return "Pedals";
            if (Regex.IsMatch(lower, "throttle|quadrant|stecs|tqs|twcs|collective|omni"))
                return "Throttle";
            if (Regex.IsMatch(lower, "xbox|gamepad|dualsense|dualshock|wireless controller|pro controller"))
                return "Gamepad";
            if (lower.Contains("yoke"))
                return "Yoke";
            if (Regex.IsMatch(lower, "panel|mfd|ufc|icp|pto|button ?box|stream ?deck|keypad"))
                return "Panel";
            if (connected != null && connected.CapabilitiesKnown)
            {
                if (connected.AxisSlots.Contains(0) && connected.AxisSlots.Contains(1))
                    return "Stick";
                if (connected.AxisSlots.Count == 0 && connected.ButtonCount > 0)
                    return "Panel";
            }
            return Regex.IsMatch(lower, "stick|joystick|grip|gunfighter|gladiator|alpha|warbrd|ffb|sidestick") ? "Stick" : "Device";
        }

        internal static string RemoveGuid(string value)
        {
            string text = (value ?? string.Empty).Trim();
            int open = text.LastIndexOf('{');
            return open > 0 && text.EndsWith("}", StringComparison.Ordinal) ? text.Substring(0, open).Trim() : text;
        }

        public static string ToText(IEnumerable<DeviceModelIdentity> identities)
        {
            List<string> lines = new List<string> { "Device Models" };
            foreach (DeviceModelIdentity identity in identities)
            {
                lines.Add("  - " + identity.Device);
                lines.Add("      model=" + identity.DisplayName + " | kind=" + identity.Kind +
                          " | vid:pid=" + (identity.VidPid.Length > 0 ? identity.VidPid : "?") +
                          " | connected=" + (identity.Connected ? "yes" : "no"));
                lines.Add("      search=" + identity.SearchUrl);
            }
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }
    }
}
